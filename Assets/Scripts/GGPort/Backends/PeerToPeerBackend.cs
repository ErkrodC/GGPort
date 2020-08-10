using System;
using System.Net;
using System.Threading;

namespace GGPort {
	public class PeerToPeerBackend<TGameState> : Session<TGameState>, IPollSink {
		private const int m_RECOMMENDATION_INTERVAL = 240;
		private const int m_DEFAULT_DISCONNECT_TIMEOUT = 5000;
		private const int m_DEFAULT_DISCONNECT_NOTIFY_START = 750;
		
		private Poll m_poll;
		private readonly Sync<TGameState> m_sync;
		private Transport m_transport;
		private Peer[] m_endpoints;
		private readonly Peer[] m_spectators;
		private int m_numSpectators;
		private readonly int m_inputSize;

		private bool m_synchronizing;
		private readonly int m_numPlayers;
		private int m_nextRecommendedSleep;

		private int m_nextSpectatorFrame;
		private uint m_disconnectTimeout;
		private uint m_disconnectNotifyStart;

		private readonly PeerMessage.ConnectStatus[] m_localConnectStatuses;

		public PeerToPeerBackend(
			BeginGameDelegate beginGameCallback,
			SaveGameStateDelegate saveGameStateCallback,
			LoadGameStateDelegate loadGameStateCallback,
			LogGameStateDelegate logGameStateCallback,
			FreeBufferDelegate freeBufferCallback,
			AdvanceFrameDelegate advanceFrameCallback,
			OnEventDelegate onEventCallback,
			LogTextDelegate logTextCallback,
			string gameName,
			ushort localPort,
			int numPlayers,
			int inputSize
		) : base(
			beginGameCallback,
			saveGameStateCallback,
			loadGameStateCallback,
			logGameStateCallback,
			freeBufferCallback,
			advanceFrameCallback,
			onEventCallback,
			logTextCallback
		) {
			m_spectators = new Peer[Types.kMaxSpectators];
			m_numPlayers = numPlayers;
			m_inputSize = inputSize;
			m_localConnectStatuses = new PeerMessage.ConnectStatus[PeerMessage.kMaxPlayers];
			
			m_sync = new Sync<TGameState>(m_localConnectStatuses, Sync<TGameState>.MAX_PREDICTION_FRAMES, numPlayers, inputSize);
			m_sync.advanceFrameEvent += advanceFrameCallback;
			m_sync.saveGameStateEvent += saveGameStateCallback;
			m_sync.loadGameStateEvent += loadGameStateCallback;
			m_sync.freeBufferEvent += freeBufferCallback;
			
			m_disconnectTimeout = m_DEFAULT_DISCONNECT_TIMEOUT;
			m_disconnectNotifyStart = m_DEFAULT_DISCONNECT_NOTIFY_START;
			m_numSpectators = 0;
			m_nextSpectatorFrame = 0;
			m_synchronizing = true;
			m_nextRecommendedSleep = 0;

			// Initialize the UDP port
			m_poll = new Poll(); // TODO pry need to do the same in other backend classes
			m_transport = new Transport(localPort, m_poll, OnMessageReceived);

			m_endpoints = new Peer[m_numPlayers];
			for (int i = 0; i < m_numPlayers; i++) {
				m_endpoints[i] = new Peer();
			}

			for (int i = 0; i < m_localConnectStatuses.Length; i++) {
				m_localConnectStatuses[i] = default;
			}

			for (int i = 0; i < m_localConnectStatuses.Length; i++) {
				m_localConnectStatuses[i].LastFrame = -1;
			}

			// Preload the ROM
			LogUtil.LogEvent += LogTextEvent;
			BeginGameEvent?.Invoke(gameName);
		}

		~PeerToPeerBackend() {
			m_endpoints = null;
			LogUtil.LogEvent -= LogTextEvent;
		}

		public override unsafe ErrorCode Idle(int timeout) {
			if (m_sync.InRollback()) { return ErrorCode.Success; }

			m_poll.Pump(0);

			PollUdpProtocolEvents();

			if (m_synchronizing) { return ErrorCode.Success; }

			m_sync.CheckSimulation(timeout);

			// notify all of our endpoints of their local frame number for their
			// next connection quality report
			int currentFrame = m_sync.GetFrameCount();
			for (int i = 0; i < m_numPlayers; i++) {
				m_endpoints[i].SetLocalFrameNumber(currentFrame);
			}

			int totalMinConfirmed = m_numPlayers <= 2
				? Poll2Players(currentFrame)
				: PollNPlayers();

			LogUtil.Log($"last confirmed frame in p2p backend is {totalMinConfirmed}.{Environment.NewLine}");
			if (totalMinConfirmed >= 0) {
				Platform.Assert(totalMinConfirmed != int.MaxValue);

				if (m_numSpectators > 0) {
					while (m_nextSpectatorFrame <= totalMinConfirmed) {
						LogUtil.Log($"pushing frame {m_nextSpectatorFrame} to spectators.{Environment.NewLine}");

						GameInput input = new GameInput {
							Frame = m_nextSpectatorFrame,
							Size = m_inputSize * m_numPlayers
						};

						m_sync.GetConfirmedInputs(input.Bits, m_inputSize * m_numPlayers, m_nextSpectatorFrame);
						for (int i = 0; i < m_numSpectators; i++) {
							m_spectators[i].SendInput(input);
						}

						m_nextSpectatorFrame++;
					}
				}

				LogUtil.Log($"setting confirmed frame in sync to {totalMinConfirmed}.{Environment.NewLine}");
				m_sync.SetLastConfirmedFrame(totalMinConfirmed);
			}

			// send timeSync notifications if now is the proper time
			if (currentFrame > m_nextRecommendedSleep) {
				int interval = 0;
				for (int i = 0; i < m_numPlayers; i++) {
					interval = Math.Max(interval, m_endpoints[i].RecommendFrameDelay());
				}

				if (interval > 0) {
					Event info = new Event {
						code = EventCode.TimeSync,
						timeSync = {
							framesAhead = interval
						}
					};
					OnEventEvent?.Invoke(info);
					m_nextRecommendedSleep = currentFrame + m_RECOMMENDATION_INTERVAL;
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
			if (player.Type == PlayerType.Spectator) {
				return AddSpectator(player.EndPoint);
			}

			int queue = player.PlayerNum - 1;
			if (player.PlayerNum < 1 || player.PlayerNum > m_numPlayers) {
				return ErrorCode.PlayerOutOfRange;
			}

			handle = QueueToPlayerHandle(queue);

			if (player.Type == PlayerType.Remote) {
				AddRemotePlayer(player.EndPoint, queue);
			}

			return ErrorCode.Success;
		}

		// TODO refactor to take in long (8 byte bitfield) as input type, keeping size param
		// TODO alternatively keep as byte array and do away with size param (just read byte[].Length)
		public override ErrorCode AddLocalInput(PlayerHandle player, byte[] value, int size) {
			GameInput input = new GameInput();

			if (m_sync.InRollback()) {
				return ErrorCode.InRollback;
			}

			if (m_synchronizing) {
				return ErrorCode.NotSynchronized;
			}

			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (!result.IsSuccess()) {
				return result;
			}

			input.Init(-1, value, value.Length);

			// Feed the input for the current frame into the synchronization layer.
			if (!m_sync.AddLocalInput(queue, ref input)) {
				return ErrorCode.PredictionThreshold;
			}

			// xxx: <- comment why this is the case
			if (input.IsNull()) { return ErrorCode.Success; }

			// Update the local connect status state to indicate that we've got a
			// confirmed local frame for this player.  this must come first so it
			// gets incorporated into the next packet we send.
			LogUtil.Log(
				$"setting local connect status for local queue {queue} to {input.Frame}{Environment.NewLine}"
			);
				
			m_localConnectStatuses[queue].LastFrame = input.Frame;

			// Send the input to all the remote players.
			for (int i = 0; i < m_numPlayers; i++) {
				if (m_endpoints[i].IsInitialized()) {
					m_endpoints[i].SendInput(input);
				}
			}

			return ErrorCode.Success;
		}

		public override ErrorCode SynchronizeInput(Array values, int size, ref int disconnectFlags) {
			// Wait until we've started to return inputs.
			if (m_synchronizing) {
				return ErrorCode.NotSynchronized;
			}

			disconnectFlags = m_sync.SynchronizeInputs(values, size);
			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			LogUtil.Log($"End of frame ({m_sync.GetFrameCount()})...{Environment.NewLine}");
			m_sync.IncrementFrame();
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

			if (m_localConnectStatuses[queue].IsDisconnected) {
				return ErrorCode.PlayerDisconnected;
			}

			if (!m_endpoints[queue].IsInitialized()) {
				int currentFrame = m_sync.GetFrameCount();

				// xxx: we should be tracking who the local player is, but for now assume
				// that if the endpoint is not initialized, this must be the local player.
				LogUtil.Log(
					$"Disconnecting local player {queue} at frame {m_localConnectStatuses[queue].LastFrame} "
					+ $"by user request.{Environment.NewLine}"
				);

				for (int i = 0; i < m_numPlayers; i++) {
					if (m_endpoints[i].IsInitialized()) {
						DisconnectPlayerQueue(i, currentFrame);
					}
				}
			} else {
				LogUtil.Log(
					$"Disconnecting queue {queue} at frame {m_localConnectStatuses[queue].LastFrame} "
					+ $"by user request.{Environment.NewLine}"
				);
				DisconnectPlayerQueue(queue, m_localConnectStatuses[queue].LastFrame);
			}

			return ErrorCode.Success;
		}

		public override ErrorCode GetNetworkStats(out NetworkStats stats, PlayerHandle player) {
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (result.IsSuccess()) {
				stats = m_endpoints[queue].GetNetworkStats();
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

			m_sync.SetFrameDelay(queue, frameDelay);
			return ErrorCode.Success;
		}

		public override ErrorCode SetDisconnectTimeout(uint timeout) {
			m_disconnectTimeout = timeout;
			for (int i = 0; i < m_numPlayers; i++) {
				if (m_endpoints[i].IsInitialized()) {
					m_endpoints[i].SetDisconnectTimeout(m_disconnectTimeout);
				}
			}

			return ErrorCode.Success;
		}

		public override ErrorCode SetDisconnectNotifyStart(uint timeout) {
			m_disconnectNotifyStart = timeout;
			for (int i = 0; i < m_numPlayers; i++) {
				if (m_endpoints[i].IsInitialized()) {
					m_endpoints[i].SetDisconnectNotifyStart(m_disconnectNotifyStart);
				}
			}

			return ErrorCode.Success;
		}

		public virtual void OnMessageReceived(IPEndPoint from, PeerMessage msg) {
			for (int i = 0; i < m_numPlayers; i++) {
				if (!m_endpoints[i].HandlesMsg(from)) { continue; }

				m_endpoints[i].OnMsg(msg);
				return;
			}

			for (int i = 0; i < m_numSpectators; i++) {
				if (!m_spectators[i].HandlesMsg(from)) { continue; }

				m_spectators[i].OnMsg(msg);
				return;
			}
		}

		private ErrorCode TryGetPlayerQueueID(PlayerHandle player, out int queueID) {
			int offset = player.HandleValue - 1;
			if (0 <= offset && offset < m_numPlayers) {
				queueID = offset;
				return ErrorCode.Success;
			}

			queueID = -1;
			return ErrorCode.InvalidPlayerHandle;
		}

		private PlayerHandle QueueToPlayerHandle(int queue) { return new PlayerHandle(queue + 1); }

		private PlayerHandle QueueToSpectatorHandle(int queue) {
			// out of range of the player array, basically
			return new PlayerHandle(queue + 1000);
		}

		private void DisconnectPlayerQueue(int queue, int syncTo) {
			int frameCount = m_sync.GetFrameCount();

			m_endpoints[queue].Disconnect();

			LogUtil.Log(
				$"Changing queue {queue} local connect status for last frame "
				+ $"from {m_localConnectStatuses[queue].LastFrame} to {syncTo} on disconnect request "
				+ $"(current: {frameCount}).{Environment.NewLine}"
			);

			m_localConnectStatuses[queue].IsDisconnected = true;
			m_localConnectStatuses[queue].LastFrame = syncTo;

			if (syncTo < frameCount) {
				LogUtil.Log(
					$"adjusting simulation to account for the fact that {queue} "
					+ $"disconnected @ {syncTo}.{Environment.NewLine}"
				);
				m_sync.AdjustSimulation(syncTo);
				LogUtil.Log($"finished adjusting simulation.{Environment.NewLine}");
			}

			Event info = new Event {
				code = EventCode.DisconnectedFromPeer,
				disconnected = {
					player = QueueToPlayerHandle(queue)
				}
			};

			OnEventEvent?.Invoke(info);

			CheckInitialSync();
		}

		private void PollSyncEvents() {
			while (m_sync.GetEvent(out Sync.Event syncEvent)) {
				OnSyncEvent(syncEvent);
			}
		}

		private void PollUdpProtocolEvents() {
			for (int i = 0; i < m_numPlayers; i++) {
				while (m_endpoints[i].GetEvent(out Peer.Event evt)) {
					OnUdpProtocolPeerEvent(ref evt, i);
				}
			}

			for (int i = 0; i < m_numSpectators; i++) {
				while (m_spectators[i].GetEvent(out Peer.Event evt)) {
					OnUdpProtocolSpectatorEvent(ref evt, i);
				}
			}
		}

		private void CheckInitialSync() {
			if (!m_synchronizing) { return; }

			// Check to see if everyone is now synchronized.  If so,
			// go ahead and tell the client that we're ok to accept input.
			for (int i = 0; i < m_numPlayers; i++) {
				// xxx: IsInitialized() must go... we're actually using it as a proxy for "represents the local player"
				if (m_endpoints[i].IsInitialized()
				    && !m_endpoints[i].IsSynchronized()
				    && !m_localConnectStatuses[i].IsDisconnected) {
					return;
				}
			}

			for (int i = 0; i < m_numSpectators; i++) {
				if (m_spectators[i].IsInitialized() && !m_spectators[i].IsSynchronized()) {
					return;
				}
			}

			Event info = new Event {
				code = EventCode.Running
			};

			OnEventEvent?.Invoke(info);
			m_synchronizing = false;
		}

		private int Poll2Players(int currentFrame) {
			// discard confirmed frames as appropriate
			int totalMinConfirmed = int.MaxValue;
			for (int peer = 0; peer < m_numPlayers; peer++) {
				bool isPeerConnected = true;

				if (m_endpoints[peer].IsRunning()) {
					isPeerConnected = m_endpoints[peer].GetPeerConnectStatus(peer, out int ignore);
				}

				if (!m_localConnectStatuses[peer].IsDisconnected) {
					totalMinConfirmed = Math.Min(m_localConnectStatuses[peer].LastFrame, totalMinConfirmed);
				}

				LogUtil.Log(
					$"  local endp: connected = {!m_localConnectStatuses[peer].IsDisconnected}, "
					+ $"last_received = {m_localConnectStatuses[peer].LastFrame}, "
					+ $"total_min_confirmed = {totalMinConfirmed}.{Environment.NewLine}"
				);

				if (!isPeerConnected && !m_localConnectStatuses[peer].IsDisconnected) {
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
			for (queue = 0; queue < m_numPlayers; queue++) {
				bool queueConnected = true;
				int queueMinConfirmed = int.MaxValue;
				LogUtil.Log($"considering queue {queue}.{Environment.NewLine}");

				for (int i = 0; i < m_numPlayers; i++) {
					// we're going to do a lot of logic here in consideration of endpoint i.
					// keep accumulating the minimum confirmed point for all n*n packets and
					// throw away the rest.
					if (m_endpoints[i].IsRunning()) {
						bool connected = m_endpoints[i].GetPeerConnectStatus(queue, out int lastReceivedFrameNumber);

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
				if (!m_localConnectStatuses[queue].IsDisconnected) {
					queueMinConfirmed = Math.Min(m_localConnectStatuses[queue].LastFrame, queueMinConfirmed);
				}

				LogUtil.Log(
					$"  local endpoint: connected = {!m_localConnectStatuses[queue].IsDisconnected}, "
					+ $"{nameof(PeerMessage.ConnectStatus.LastFrame)} = {m_localConnectStatuses[queue].LastFrame}, "
					+ $"{nameof(queueMinConfirmed)} = {queueMinConfirmed}.{Environment.NewLine}"
				);

				if (queueConnected) {
					totalMinConfirmed = Math.Min(queueMinConfirmed, totalMinConfirmed);
				} else {
					// check to see if this disconnect notification is further back than we've been before.  If
					// so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
					// and later receive a disconnect notification for frame n-1.
					if (!m_localConnectStatuses[queue].IsDisconnected
						|| m_localConnectStatuses[queue].LastFrame > queueMinConfirmed) {
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
			m_synchronizing = true;

			m_endpoints[queue].Init(ref m_transport, ref m_poll, queue, remoteEndPoint, m_localConnectStatuses);
			m_endpoints[queue].SetDisconnectTimeout(m_disconnectTimeout);
			m_endpoints[queue].SetDisconnectNotifyStart(m_disconnectNotifyStart);
			m_endpoints[queue].Synchronize();
		}

		private ErrorCode AddSpectator(IPEndPoint remoteEndPoint) {
			if (m_numSpectators == Types.kMaxSpectators) {
				return ErrorCode.TooManySpectators;
			}

			// Currently, we can only add spectators before the game starts.
			if (!m_synchronizing) {
				return ErrorCode.InvalidRequest;
			}

			int queue = m_numSpectators++;

			m_spectators[queue].Init(ref m_transport, ref m_poll, queue + 1000, remoteEndPoint, m_localConnectStatuses);
			m_spectators[queue].SetDisconnectTimeout(m_disconnectTimeout);
			m_spectators[queue].SetDisconnectNotifyStart(m_disconnectNotifyStart);
			m_spectators[queue].Synchronize();

			return ErrorCode.Success;
		}

		// TODO remove? does nothing here, but might do something in syncTest || spectator backends
		protected virtual void OnSyncEvent(Sync.Event syncEvent) { }

		protected virtual void OnUdpProtocolEvent(ref Peer.Event evt, PlayerHandle handle) {
			switch (evt.type) {
				case Peer.Event.Type.Connected: {
					Event info = new Event {
						code = EventCode.ConnectedToPeer,
						connected = {
							player = handle
						}
					};

					OnEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronizing: {
					Event info = new Event {
						code = EventCode.SynchronizingWithPeer,
						synchronizing = {
							player = handle, count = evt.synchronizing.Count, total = evt.synchronizing.Total
						}
					};

					OnEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronized: {
					Event info = new Event {
						code = EventCode.SynchronizedWithPeer,
						synchronized = {
							player = handle
						}
					};

					OnEventEvent?.Invoke(info);

					CheckInitialSync();
					break;
				}

				case Peer.Event.Type.NetworkInterrupted: {
					Event info = new Event {
						code = EventCode.ConnectionInterrupted,
						connectionInterrupted = {
							player = handle, disconnect_timeout = evt.network_interrupted.DisconnectTimeout
						}
					};

					OnEventEvent?.Invoke(info);
					break;
				}

				case Peer.Event.Type.NetworkResumed: {
					Event info = new Event {
						code = EventCode.ConnectionResumed,
						connectionResumed = {
							player = handle
						}
					};

					OnEventEvent?.Invoke(info);
					break;
				}
			}
		}

		protected virtual void OnUdpProtocolPeerEvent(ref Peer.Event evt, int queue) {
			OnUdpProtocolEvent(ref evt, QueueToPlayerHandle(queue));
			switch (evt.type) {
				case Peer.Event.Type.Input:
					if (!m_localConnectStatuses[queue].IsDisconnected) {
						int currentRemoteFrame = m_localConnectStatuses[queue].LastFrame;
						int newRemoteFrame = evt.input.Frame;

						Platform.Assert(currentRemoteFrame == -1 || newRemoteFrame == currentRemoteFrame + 1);

						m_sync.AddRemoteInput(queue, ref evt.input);
						// Notify the other endpoints which frame we received from a peer
						LogUtil.Log(
							$"setting remote connect status for queue {queue} to {evt.input.Frame}{Environment.NewLine}"
						);
						m_localConnectStatuses[queue].LastFrame = evt.input.Frame;
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
					m_spectators[queue].Disconnect();

					Event info = new Event {
						code = EventCode.DisconnectedFromPeer,
						disconnected = {
							player = handle
						}
					};

					OnEventEvent?.Invoke(info);

					break;
			}
		}

		public virtual bool OnHandlePoll(object cookie) { return true; }
		public virtual bool OnMsgPoll(object cookie) { return true; }
		public virtual bool OnPeriodicPoll(object cookie, long lastFireTime) { return true; }
		public virtual bool OnLoopPoll(object cookie) { return true; }
	};
}