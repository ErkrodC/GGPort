using System;
using System.Net;
using System.Threading;

namespace GGPort {
	public class PeerToPeerBackend<TGameState> : Session<TGameState>, IPollSink {
		private const int _RECOMMENDATION_INTERVAL = 240;
		private const int _DEFAULT_DISCONNECT_TIMEOUT = 5000;
		private const int _DEFAULT_DISCONNECT_NOTIFY_START = 750;

		private Poll _poll;
		private readonly Sync<TGameState> _sync;
		private Transport _transport;
		private Peer[] _endpoints;
		private readonly Peer[] _spectators;
		private int _numSpectators;
		private readonly int _inputSize;

		private bool _synchronizing;
		private readonly int _numPlayers;
		private int _nextRecommendedSleep;

		private int _nextSpectatorFrame;
		private uint _disconnectTimeout;
		private uint _disconnectNotifyStart;

		private readonly PeerMessage.ConnectStatus[] _localConnectStatuses;

		public PeerToPeerBackend(
			BeginGameDelegate beginGameCallback,
			SaveGameStateDelegate<TGameState> saveGameStateCallback,
			LoadGameStateDelegate<TGameState> loadGameStateCallback,
			LogGameStateDelegate<TGameState> logGameStateCallback,
			FreeBufferDelegate<TGameState> freeBufferCallback,
			AdvanceFrameDelegate advanceFrameCallback,
			OnEventDelegate onEventCallback,
			LogTextDelegate logTextCallback,
			ushort localPort,
			int numPlayers,
			int inputSize
		)
			: base(
				beginGameCallback,
				saveGameStateCallback,
				loadGameStateCallback,
				logGameStateCallback,
				freeBufferCallback,
				advanceFrameCallback,
				onEventCallback,
				logTextCallback
			) {
			_spectators = new Peer[Types.MAX_SPECTATORS];
			_numPlayers = numPlayers;
			_inputSize = inputSize;
			_localConnectStatuses = new PeerMessage.ConnectStatus[PeerMessage.MAX_PLAYERS];

			_sync = new Sync<TGameState>(_localConnectStatuses, Sync.MAX_PREDICTION_FRAMES, numPlayers, inputSize);
			_sync.advanceFrameEvent += advanceFrameEvent;
			_sync.saveGameStateEvent += saveGameStateEvent;
			_sync.loadGameStateEvent += loadGameStateEvent;
			_sync.freeBufferEvent += freeBufferEvent;

			_disconnectTimeout = _DEFAULT_DISCONNECT_TIMEOUT;
			_disconnectNotifyStart = _DEFAULT_DISCONNECT_NOTIFY_START;
			_numSpectators = 0;
			_nextSpectatorFrame = 0;
			_synchronizing = true;
			_nextRecommendedSleep = 0;

			// Initialize the UDP port
			_poll = new Poll(); // TODO pry need to do the same in other backend classes
			_transport = new Transport(localPort, _poll);
			_transport.messageReceivedEvent += OnMessageReceived;

			_endpoints = new Peer[_numPlayers];
			for (int i = 0; i < _numPlayers; i++) {
				_endpoints[i] = new Peer();
			}

			for (int i = 0; i < _localConnectStatuses.Length; i++) {
				_localConnectStatuses[i] = default;
			}

			for (int i = 0; i < _localConnectStatuses.Length; i++) {
				_localConnectStatuses[i].lastFrame = -1;
			}

			// Preload the ROM
			LogUtil.logEvent += logTextEvent;
			beginGameEvent?.Invoke();
		}

		~PeerToPeerBackend() {
			_endpoints = null;
			LogUtil.logEvent -= logTextEvent;
		}

		public override unsafe ErrorCode Idle(int timeout) {
			if (_sync.InRollback()) { return ErrorCode.Success; }

			_poll.Pump(0);

			PollUdpProtocolEvents();

			if (_synchronizing) { return ErrorCode.Success; }

			_sync.CheckSimulation(timeout);

			// notify all of our endpoints of their local frame number for their
			// next connection quality report
			int currentFrame = _sync.GetFrameCount();
			for (int i = 0; i < _numPlayers; i++) {
				_endpoints[i].SetLocalFrameNumber(currentFrame);
			}

			int totalMinConfirmed = _numPlayers <= 2
				? Poll2Players(currentFrame)
				: PollNPlayers();

			LogUtil.Log($"last confirmed frame in p2p backend is {totalMinConfirmed}.{Environment.NewLine}");
			if (totalMinConfirmed >= 0) {
				Platform.Assert(totalMinConfirmed != int.MaxValue);

				if (_numSpectators > 0) {
					while (_nextSpectatorFrame <= totalMinConfirmed) {
						LogUtil.Log($"pushing frame {_nextSpectatorFrame} to spectators.{Environment.NewLine}");

						GameInput input = new GameInput {
							frame = _nextSpectatorFrame,
							size = _inputSize * _numPlayers
						};

						_sync.GetConfirmedInputs(input.bits, _inputSize * _numPlayers, _nextSpectatorFrame);
						for (int i = 0; i < _numSpectators; i++) {
							_spectators[i].SendInput(input);
						}

						_nextSpectatorFrame++;
					}
				}

				LogUtil.Log($"setting confirmed frame in sync to {totalMinConfirmed}.{Environment.NewLine}");
				_sync.SetLastConfirmedFrame(totalMinConfirmed);
			}

			// send timeSync notifications if now is the proper time
			if (currentFrame > _nextRecommendedSleep) {
				int interval = 0;
				for (int i = 0; i < _numPlayers; i++) {
					interval = Math.Max(interval, _endpoints[i].RecommendFrameDelay());
				}

				if (interval > 0) {
					EventData info = new EventData {
						code = EventCode.TimeSync,
						timeSync = {
							framesAhead = interval
						}
					};
					onEventEvent?.Invoke(info);
					_nextRecommendedSleep = currentFrame + _RECOMMENDATION_INTERVAL;
				}
			}

			// XXX: this is obviously a farce...
			if (timeout != 0) {
				Thread.Sleep(1);
			}

			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(Player player, out PlayerHandle handle) {
			handle = new PlayerHandle(-1);
			if (player.type == PlayerType.Spectator) {
				return AddSpectator(player.endPoint);
			}


			if (player.playerNum < 1 || player.playerNum > _numPlayers) {
				return ErrorCode.PlayerOutOfRange;
			}

			int queue = player.playerNum - 1;
			handle = QueueToPlayerHandle(queue);

			if (player.type == PlayerType.Remote) {
				AddRemotePlayer(player.endPoint, queue);
			}

			return ErrorCode.Success;
		}

		// TODO refactor to take in long (8 byte bitfield) as input type, keeping size param
		// TODO alternatively keep as byte array and do away with size param (just read byte[].Length)
		public override ErrorCode AddLocalInput(PlayerHandle player, byte[] value, int size) {
			GameInput input = new GameInput();

			if (_sync.InRollback()) {
				return ErrorCode.InRollback;
			}

			if (_synchronizing) {
				return ErrorCode.NotSynchronized;
			}

			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (!result.IsSuccess()) {
				return result;
			}

			input.Init(-1, value, value.Length);

			// Feed the input for the current frame into the synchronization layer.
			if (!_sync.AddLocalInput(queue, ref input)) {
				return ErrorCode.PredictionThreshold;
			}

			// xxx: <- comment why this is the case
			if (input.IsNull()) { return ErrorCode.Success; }

			// Update the local connect status state to indicate that we've got a
			// confirmed local frame for this player.  this must come first so it
			// gets incorporated into the next packet we send.
			LogUtil.Log(
				$"setting local connect status for local queue {queue} to {input.frame}{Environment.NewLine}"
			);

			_localConnectStatuses[queue].lastFrame = input.frame;

			// Send the input to all the remote players.
			for (int i = 0; i < _numPlayers; i++) {
				if (_endpoints[i].IsInitialized()) {
					_endpoints[i].SendInput(input);
				}
			}

			return ErrorCode.Success;
		}

		public override ErrorCode SynchronizeInput(Array values, int size, ref int disconnectFlags) {
			// Wait until we've started to return inputs.
			if (_synchronizing) {
				return ErrorCode.NotSynchronized;
			}

			disconnectFlags = _sync.SynchronizeInputs(values, size);
			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			LogUtil.Log($"End of frame ({_sync.GetFrameCount()})...{Environment.NewLine}");
			_sync.IncrementFrame();
			Idle(0);
			PollSyncEvents();

			return ErrorCode.Success;
		}

		// Called only as the result of a local decision to disconnect.  The remote
		// decisions to disconnect are a result of us parsing the peer_connect_settings
		// blob in every endpoint periodically.
		public override ErrorCode DisconnectPlayer(PlayerHandle player) {
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (!result.IsSuccess()) {
				return result;
			}

			if (_localConnectStatuses[queue].isDisconnected) {
				return ErrorCode.PlayerDisconnected;
			}

			if (!_endpoints[queue].IsInitialized()) {
				int currentFrame = _sync.GetFrameCount();

				// xxx: we should be tracking who the local player is, but for now assume
				// that if the endpoint is not initialized, this must be the local player.
				LogUtil.Log(
					$"Disconnecting local player {queue} at frame {_localConnectStatuses[queue].lastFrame} "
					+ $"by user request.{Environment.NewLine}"
				);

				for (int i = 0; i < _numPlayers; i++) {
					if (_endpoints[i].IsInitialized()) {
						DisconnectPlayerQueue(i, currentFrame);
					}
				}
			} else {
				LogUtil.Log(
					$"Disconnecting queue {queue} at frame {_localConnectStatuses[queue].lastFrame} "
					+ $"by user request.{Environment.NewLine}"
				);
				DisconnectPlayerQueue(queue, _localConnectStatuses[queue].lastFrame);
			}

			return ErrorCode.Success;
		}

		public override ErrorCode GetNetworkStats(out NetworkStats stats, PlayerHandle player) {
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (result.IsSuccess()) {
				stats = _endpoints[queue].GetNetworkStats();
				return ErrorCode.Success;
			}

			stats = default;
			return result;
		}

		public override ErrorCode SetFrameDelay(PlayerHandle player, int frameDelay) {
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (!result.IsSuccess()) {
				return result;
			}

			_sync.SetFrameDelay(queue, frameDelay);
			return ErrorCode.Success;
		}

		public override ErrorCode SetDisconnectTimeout(uint timeout) {
			_disconnectTimeout = timeout;
			for (int i = 0; i < _numPlayers; i++) {
				if (_endpoints[i].IsInitialized()) {
					_endpoints[i].SetDisconnectTimeout(_disconnectTimeout);
				}
			}

			return ErrorCode.Success;
		}

		public override ErrorCode SetDisconnectNotifyStart(uint timeout) {
			_disconnectNotifyStart = timeout;
			for (int i = 0; i < _numPlayers; i++) {
				if (_endpoints[i].IsInitialized()) {
					_endpoints[i].SetDisconnectNotifyStart(_disconnectNotifyStart);
				}
			}

			return ErrorCode.Success;
		}

		public virtual void OnMessageReceived(IPEndPoint from, PeerMessage msg) {
			for (int i = 0; i < _numPlayers; i++) {
				if (!_endpoints[i].DoesHandleMessageFromEndPoint(from)) { continue; }

				_endpoints[i].OnMessageReceived(msg);
				return;
			}

			for (int i = 0; i < _numSpectators; i++) {
				if (!_spectators[i].DoesHandleMessageFromEndPoint(from)) { continue; }

				_spectators[i].OnMessageReceived(msg);
				return;
			}
		}

		private ErrorCode TryGetPlayerQueueID(PlayerHandle player, out int queueID) {
			int offset = player.handleValue - 1;
			if (0 <= offset && offset < _numPlayers) {
				queueID = offset;
				return ErrorCode.Success;
			}

			queueID = -1;
			return ErrorCode.InvalidPlayerHandle;
		}

		private PlayerHandle QueueToPlayerHandle(int queue) {
			return new PlayerHandle(queue + 1);
		}

		private PlayerHandle QueueToSpectatorHandle(int queue) {
			// out of range of the player array, basically
			return new PlayerHandle(queue + 1000);
		}

		private void DisconnectPlayerQueue(int queue, int syncTo) {
			int frameCount = _sync.GetFrameCount();

			_endpoints[queue].Disconnect();

			LogUtil.Log(
				$"Changing queue {queue} local connect status for last frame "
				+ $"from {_localConnectStatuses[queue].lastFrame} to {syncTo} on disconnect request "
				+ $"(current: {frameCount}).{Environment.NewLine}"
			);

			_localConnectStatuses[queue].isDisconnected = true;
			_localConnectStatuses[queue].lastFrame = syncTo;

			if (syncTo < frameCount) {
				LogUtil.Log(
					$"adjusting simulation to account for the fact that {queue} "
					+ $"disconnected @ {syncTo}.{Environment.NewLine}"
				);
				_sync.AdjustSimulation(syncTo);
				LogUtil.Log($"finished adjusting simulation.{Environment.NewLine}");
			}

			EventData info = new EventData {
				code = EventCode.DisconnectedFromPeer,
				disconnected = {
					player = QueueToPlayerHandle(queue)
				}
			};

			onEventEvent?.Invoke(info);

			CheckInitialSync();
		}

		private void PollSyncEvents() {
			while (_sync.GetEvent(out Sync.Event syncEvent)) {
				OnSyncEvent(syncEvent);
			}
		}

		private void PollUdpProtocolEvents() {
			for (int i = 0; i < _numPlayers; i++) {
				while (_endpoints[i].GetEvent(out Peer.Event evt)) {
					OnUdpProtocolPeerEvent(ref evt, i);
				}
			}

			for (int i = 0; i < _numSpectators; i++) {
				while (_spectators[i].GetEvent(out Peer.Event evt)) {
					OnUdpProtocolSpectatorEvent(ref evt, i);
				}
			}
		}

		private void CheckInitialSync() {
			if (!_synchronizing) { return; }

			// Check to see if everyone is now synchronized.  If so,
			// go ahead and tell the client that we're ok to accept input.
			for (int i = 0; i < _numPlayers; i++) {
				// xxx: IsInitialized() must go... we're actually using it as a proxy for "represents the local player"
				if (_endpoints[i].IsInitialized()
				    && !_endpoints[i].IsSynchronized()
				    && !_localConnectStatuses[i].isDisconnected) {
					return;
				}
			}

			for (int i = 0; i < _numSpectators; i++) {
				if (_spectators[i].IsInitialized() && !_spectators[i].IsSynchronized()) {
					return;
				}
			}

			EventData info = new EventData {
				code = EventCode.Running
			};

			onEventEvent?.Invoke(info);
			_synchronizing = false;
		}

		private int Poll2Players(int currentFrame) {
			// discard confirmed frames as appropriate
			int totalMinConfirmed = int.MaxValue;
			for (int peer = 0; peer < _numPlayers; peer++) {
				bool isPeerConnected = true;

				if (_endpoints[peer].IsRunning()) {
					isPeerConnected = _endpoints[peer].GetPeerConnectStatus(peer, out int ignore);
				}

				if (!_localConnectStatuses[peer].isDisconnected) {
					totalMinConfirmed = Math.Min(_localConnectStatuses[peer].lastFrame, totalMinConfirmed);
				}

				LogUtil.Log(
					$"  local endp: connected = {!_localConnectStatuses[peer].isDisconnected}, "
					+ $"last_received = {_localConnectStatuses[peer].lastFrame}, "
					+ $"total_min_confirmed = {totalMinConfirmed}.{Environment.NewLine}"
				);

				if (!isPeerConnected && !_localConnectStatuses[peer].isDisconnected) {
					LogUtil.Log($"disconnecting i {peer} by remote request.{Environment.NewLine}");
					DisconnectPlayerQueue(peer, totalMinConfirmed);
				}

				LogUtil.Log($"  total_min_confirmed = {totalMinConfirmed}.{Environment.NewLine}");
			}

			return totalMinConfirmed;
		}

		private int PollNPlayers() {
			int queue;

			// discard confirmed frames as appropriate
			int totalMinConfirmed = int.MaxValue;
			for (queue = 0; queue < _numPlayers; queue++) {
				bool queueConnected = true;
				int queueMinConfirmed = int.MaxValue;
				LogUtil.Log($"considering queue {queue}.{Environment.NewLine}");

				for (int i = 0; i < _numPlayers; i++) {
					// we're going to do a lot of logic here in consideration of endpoint i.
					// keep accumulating the minimum confirmed point for all n*n packets and
					// throw away the rest.
					if (_endpoints[i].IsRunning()) {
						bool connected = _endpoints[i].GetPeerConnectStatus(queue, out int lastReceivedFrameNumber);

						queueConnected = queueConnected && connected;
						queueMinConfirmed = Math.Min(lastReceivedFrameNumber, queueMinConfirmed);
						LogUtil.Log(
							$"  endpoint {i}: connected = {connected}, "
							+ $"{nameof(lastReceivedFrameNumber)} = {lastReceivedFrameNumber}, "
							+ $"{nameof(queueMinConfirmed)} = {queueMinConfirmed}.{Environment.NewLine}"
						);
					} else {
						LogUtil.Log($"  endpoint {i}: ignoring... not running.{Environment.NewLine}");
					}
				}

				// merge in our local status only if we're still connected!
				if (!_localConnectStatuses[queue].isDisconnected) {
					queueMinConfirmed = Math.Min(_localConnectStatuses[queue].lastFrame, queueMinConfirmed);
				}

				LogUtil.Log(
					$"  local endpoint: connected = {!_localConnectStatuses[queue].isDisconnected}, "
					+ $"{nameof(PeerMessage.ConnectStatus.lastFrame)} = {_localConnectStatuses[queue].lastFrame}, "
					+ $"{nameof(queueMinConfirmed)} = {queueMinConfirmed}.{Environment.NewLine}"
				);

				if (queueConnected) {
					totalMinConfirmed = Math.Min(queueMinConfirmed, totalMinConfirmed);
				} else {
					// check to see if this disconnect notification is further back than we've been before.  If
					// so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
					// and later receive a disconnect notification for frame n-1.
					if (!_localConnectStatuses[queue].isDisconnected
					    || _localConnectStatuses[queue].lastFrame > queueMinConfirmed) {
						LogUtil.Log($"disconnecting queue {queue} by remote request.{Environment.NewLine}");
						DisconnectPlayerQueue(queue, queueMinConfirmed);
					}
				}

				LogUtil.Log($"  total_min_confirmed = {totalMinConfirmed}.{Environment.NewLine}");
			}

			return totalMinConfirmed;
		}

		private void AddRemotePlayer(IPEndPoint remoteEndPoint, int queue) {
			// Start the state machine (xxx: no)
			_synchronizing = true;

			_endpoints[queue].Init(ref _transport, ref _poll, queue, remoteEndPoint, _localConnectStatuses);
			_endpoints[queue].SetDisconnectTimeout(_disconnectTimeout);
			_endpoints[queue].SetDisconnectNotifyStart(_disconnectNotifyStart);
			_endpoints[queue].Synchronize();
		}

		private ErrorCode AddSpectator(IPEndPoint remoteEndPoint) {
			if (_numSpectators == Types.MAX_SPECTATORS) {
				return ErrorCode.TooManySpectators;
			}

			// Currently, we can only add spectators before the game starts.
			if (!_synchronizing) {
				return ErrorCode.InvalidRequest;
			}

			int queue = _numSpectators++;

			_spectators[queue].Init(ref _transport, ref _poll, queue + 1000, remoteEndPoint, _localConnectStatuses);
			_spectators[queue].SetDisconnectTimeout(_disconnectTimeout);
			_spectators[queue].SetDisconnectNotifyStart(_disconnectNotifyStart);
			_spectators[queue].Synchronize();

			return ErrorCode.Success;
		}

		// TODO remove? does nothing here, but might do something in syncTest || spectator backends
		protected virtual void OnSyncEvent(Sync.Event syncEvent) { }

		protected virtual void OnUdpProtocolEvent(ref Peer.Event evt, PlayerHandle handle) {
			switch (evt.type) {
				case Peer.Event.Type.Connected: {
					EventData info = new EventData {
						code = EventCode.ConnectedToPeer,
						connected = {
							player = handle
						}
					};

					onEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronizing: {
					EventData info = new EventData {
						code = EventCode.SynchronizingWithPeer,
						synchronizing = {
							player = handle, count = evt.synchronizing.count, total = evt.synchronizing.total
						}
					};

					onEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronized: {
					EventData info = new EventData {
						code = EventCode.SynchronizedWithPeer,
						synchronized = {
							player = handle
						}
					};

					onEventEvent?.Invoke(info);

					CheckInitialSync();
					break;
				}

				case Peer.Event.Type.NetworkInterrupted: {
					EventData info = new EventData {
						code = EventCode.ConnectionInterrupted,
						connectionInterrupted = {
							player = handle, disconnectTimeout = evt.network_interrupted.disconnectTimeout
						}
					};

					onEventEvent?.Invoke(info);
					break;
				}

				case Peer.Event.Type.NetworkResumed: {
					EventData info = new EventData {
						code = EventCode.ConnectionResumed,
						connectionResumed = {
							player = handle
						}
					};

					onEventEvent?.Invoke(info);
					break;
				}
			}
		}

		protected virtual void OnUdpProtocolPeerEvent(ref Peer.Event evt, int queue) {
			OnUdpProtocolEvent(ref evt, QueueToPlayerHandle(queue));
			switch (evt.type) {
				case Peer.Event.Type.Input:
					if (!_localConnectStatuses[queue].isDisconnected) {
						int currentRemoteFrame = _localConnectStatuses[queue].lastFrame;
						int newRemoteFrame = evt.input.frame;

						Platform.Assert(currentRemoteFrame == -1 || newRemoteFrame == currentRemoteFrame + 1);

						_sync.AddRemoteInput(queue, ref evt.input);
						// Notify the other endpoints which frame we received from a peer
						LogUtil.Log(
							$"setting remote connect status for queue {queue} to {evt.input.frame}{Environment.NewLine}"
						);
						_localConnectStatuses[queue].lastFrame = evt.input.frame;
					}

					break;

				case Peer.Event.Type.Disconnected:
					DisconnectPlayer(QueueToPlayerHandle(queue));
					break;
			}
		}

		protected virtual void OnUdpProtocolSpectatorEvent(ref Peer.Event evt, int queue) {
			PlayerHandle handle = QueueToSpectatorHandle(queue);
			OnUdpProtocolEvent(ref evt, handle);

			switch (evt.type) {
				case Peer.Event.Type.Disconnected:
					_spectators[queue].Disconnect();

					EventData info = new EventData {
						code = EventCode.DisconnectedFromPeer,
						disconnected = {
							player = handle
						}
					};

					onEventEvent?.Invoke(info);

					break;
			}
		}

		public virtual bool OnHandlePoll(object cookie) {
			return true;
		}

		public virtual bool OnMsgPoll(object cookie) {
			return true;
		}

		public virtual bool OnPeriodicPoll(object cookie, long lastFireTime) {
			return true;
		}

		public virtual bool OnLoopPoll(object cookie) {
			return true;
		}
	};
}