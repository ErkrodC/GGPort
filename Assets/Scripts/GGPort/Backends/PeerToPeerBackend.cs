using System;
using System.Net;
using System.Threading;

namespace GGPort {
	public class PeerToPeerBackend : Session, IPollSink, Transport.ICallbacks {
		private const int kRecommendationInterval = 240;
		private const int kDefaultDisconnectTimeout = 5000;
		private const int kDefaultDisconnectNotifyStart = 750;

		private readonly SessionCallbacks callbacks;
		private Poll poll;
		private readonly Sync sync;
		private Transport transport;
		private Peer[] endpoints;
		private readonly Peer[] spectators;
		private int numSpectators;
		private readonly int inputSize;

		private bool synchronizing;
		private readonly int numPlayers;
		private int nextRecommendedSleep;

		private int nextSpectatorFrame;
		private uint disconnectTimeout;
		private uint disconnectNotifyStart;

		private readonly PeerMessage.ConnectStatus[] localConnectStatuses;

		public PeerToPeerBackend(
			SessionCallbacks cb,
			string gameName,
			ushort localPort,
			int numPlayers,
			int inputSize
		) {
			spectators = new Peer[Types.kMaxSpectators];
			this.numPlayers = numPlayers;
			this.inputSize = inputSize;
			localConnectStatuses = new PeerMessage.ConnectStatus[PeerMessage.kMaxPlayers];
			sync = new Sync(localConnectStatuses);
			disconnectTimeout = kDefaultDisconnectTimeout;
			disconnectNotifyStart = kDefaultDisconnectNotifyStart;
			numSpectators = 0;
			nextSpectatorFrame = 0;
			callbacks = cb;
			synchronizing = true;
			nextRecommendedSleep = 0;

			// Initialize the synchronization layer
			Sync.Config config = new Sync.Config(
				callbacks,
				Sync.kMaxPredictionFrames,
				numPlayers,
				inputSize
			);
			
			sync.Init(config);

			// Initialize the UDP port
			transport = new Transport();
			poll = new Poll(); // TODO pry need to do the same in other backend classes
			transport.Init(localPort, poll, this);

			endpoints = new Peer[this.numPlayers];
			for (int i = 0; i < this.numPlayers; i++) {
				endpoints[i] = new Peer();
			}

			for (int i = 0; i < localConnectStatuses.Length; i++) {
				localConnectStatuses[i] = default;
			}
			
			for (int i = 0; i < localConnectStatuses.Length; i++) {
				localConnectStatuses[i].LastFrame = -1;
			}

			// Preload the ROM
			LogUtil.LogCallback += callbacks.LogText;
			callbacks.BeginGame(gameName);
		}

		~PeerToPeerBackend() {
			endpoints = null;
			LogUtil.LogCallback -= callbacks.LogText;
		}

		public override unsafe ErrorCode Idle(int timeout) {
			if (!sync.InRollback()) {
			   poll.Pump(0);

			   PollUdpProtocolEvents();

			   if (!synchronizing) {
				   sync.CheckSimulation(timeout);

				   // notify all of our endpoints of their local frame number for their
				   // next connection quality report
				   int currentFrame = sync.GetFrameCount();
				   for (int i = 0; i < numPlayers; i++) {
					   endpoints[i].SetLocalFrameNumber(currentFrame);
				   }
				   
				   int totalMinConfirmed = numPlayers <= 2
					   ? Poll2Players(currentFrame)
					   : PollNPlayers();

				   LogUtil.Log($"last confirmed frame in p2p backend is {totalMinConfirmed}.{Environment.NewLine}");
				   if (totalMinConfirmed >= 0) {
					   Platform.Assert(totalMinConfirmed != int.MaxValue);
					   
					   if (numSpectators > 0) {
						   while (nextSpectatorFrame <= totalMinConfirmed) {
							   LogUtil.Log($"pushing frame {nextSpectatorFrame} to spectators.{Environment.NewLine}");

							   GameInput input = new GameInput {
								   Frame = nextSpectatorFrame,
								   Size = inputSize * numPlayers
							   };
							   
							   sync.GetConfirmedInputs(input.Bits, inputSize * numPlayers, nextSpectatorFrame);
							   for (int i = 0; i < numSpectators; i++) {
								   spectators[i].SendInput(input);
							   }
							   nextSpectatorFrame++;
						   }
					   }

					   LogUtil.Log($"setting confirmed frame in sync to {totalMinConfirmed}.{Environment.NewLine}");
					   sync.SetLastConfirmedFrame(totalMinConfirmed);
				   }

				   // send timeSync notifications if now is the proper time
				   if (currentFrame > nextRecommendedSleep) {
					   int interval = 0;
					   for (int i = 0; i < numPlayers; i++) {
						   interval = Math.Max(interval, endpoints[i].RecommendFrameDelay());
					   }

					   if (interval > 0) {
						   Event info = new Event {
							   code = EventCode.TimeSync,
							   timeSync = {
								   framesAhead = interval
							   }
						   };
						   callbacks.OnEvent(info);
						   nextRecommendedSleep = currentFrame + kRecommendationInterval;
					   }
				   }
				   
				   // XXX: this is obviously a farce...
				   if (timeout != 0) {
					   Thread.Sleep(1);
				   }
			   }
			}
			
			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(Player player, out PlayerHandle handle) {
			handle = new PlayerHandle(-1);
			if (player.Type == PlayerType.Spectator) {
				return AddSpectator(player.EndPoint);
			}

			int queue = player.PlayerNum - 1;
			if (player.PlayerNum < 1 || player.PlayerNum > numPlayers) {
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

			if (sync.InRollback()) {
				return ErrorCode.InRollback;
			}
			if (synchronizing) {
				return ErrorCode.NotSynchronized;
			}
   
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (!result.Succeeded()) {
				return result;
			}
			
			input.Init(-1, value, value.Length);

			// Feed the input for the current frame into the synchronzation layer.
			if (!sync.AddLocalInput(queue, ref input)) {
				return ErrorCode.PredictionThreshold;
			}

			if (!input.IsNull()) { // xxx: <- comment why this is the case
				// Update the local connect status state to indicate that we've got a
				// confirmed local frame for this player.  this must come first so it
				// gets incorporated into the next packet we send.
				LogUtil.Log($"setting local connect status for local queue {queue} to {input.Frame}{Environment.NewLine}");
				localConnectStatuses[queue].LastFrame = input.Frame;

				// Send the input to all the remote players.
				for (int i = 0; i < numPlayers; i++) {
					if (endpoints[i].IsInitialized()) {
						endpoints[i].SendInput(input);
					}
				}
			}

			return ErrorCode.Success;
		}

		public override ErrorCode SynchronizeInput(Array values, int size, ref int disconnectFlags) {
			// Wait until we've started to return inputs.
			if (synchronizing) {
				return ErrorCode.NotSynchronized;
			}
			
			disconnectFlags = sync.SynchronizeInputs(values, size);
			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			LogUtil.Log($"End of frame ({sync.GetFrameCount()})...{Environment.NewLine}");
			sync.IncrementFrame();
			Idle(0);
			PollSyncEvents();

			return ErrorCode.Success;
		}

		/*
		* Called only as the result of a local decision to disconnect.  The remote
		* decisions to disconnect are a result of us parsing the peer_connect_settings
		* blob in every endpoint periodically.
		*/
		public override ErrorCode DisconnectPlayer(PlayerHandle player) {
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (!result.Succeeded()) {
				return result;
			}
   
			if (localConnectStatuses[queue].IsDisconnected) {
				return ErrorCode.PlayerDisconnected;
			}

			if (!endpoints[queue].IsInitialized()) {
				int currentFrame = sync.GetFrameCount();
				
				// xxx: we should be tracking who the local player is, but for now assume
				// that if the endpoint is not initialized, this must be the local player.
				LogUtil.Log($"Disconnecting local player {queue} at frame {localConnectStatuses[queue].LastFrame} by user request.{Environment.NewLine}");
				
				for (int i = 0; i < numPlayers; i++) {
					if (endpoints[i].IsInitialized()) {
						DisconnectPlayerQueue(i, currentFrame);
					}
				}
			} else {
				LogUtil.Log($"Disconnecting queue {queue} at frame {localConnectStatuses[queue].LastFrame} by user request.{Environment.NewLine}");
				DisconnectPlayerQueue(queue, localConnectStatuses[queue].LastFrame);
			}
			return ErrorCode.Success;
		}

		public override ErrorCode GetNetworkStats(out NetworkStats stats, PlayerHandle player) {
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (result.Succeeded()) {
				stats = endpoints[queue].GetNetworkStats();
				return ErrorCode.Success;
			}

			stats = default;
			return result;
		}

		public override ErrorCode SetFrameDelay(PlayerHandle player, int frameDelay) {
			ErrorCode result = TryGetPlayerQueueID(player, out int queue);
			if (!result.Succeeded()) {
				return result;
			}

			sync.SetFrameDelay(queue, frameDelay);
			return ErrorCode.Success;

		}

		public override ErrorCode SetDisconnectTimeout(uint timeout) {
			disconnectTimeout = timeout;
			for (int i = 0; i < numPlayers; i++) {
				if (endpoints[i].IsInitialized()) {
					endpoints[i].SetDisconnectTimeout(disconnectTimeout);
				}
			}
			
			return ErrorCode.Success;
		}

		public override ErrorCode SetDisconnectNotifyStart(uint timeout) {
			disconnectNotifyStart = timeout;
			for (int i = 0; i < numPlayers; i++) {
				if (endpoints[i].IsInitialized()) {
					endpoints[i].SetDisconnectNotifyStart(disconnectNotifyStart);
				}
			}
			return ErrorCode.Success;
		}

		public virtual void OnMsg(IPEndPoint from, PeerMessage msg) {
			for (int i = 0; i < numPlayers; i++) {
				if (!endpoints[i].HandlesMsg(from)) { continue; }

				endpoints[i].OnMsg(msg);
				return;
			}
			
			for (int i = 0; i < numSpectators; i++) {
				if (!spectators[i].HandlesMsg(from)) { continue; }

				spectators[i].OnMsg(msg);
				return;
			}
		}
		
		private ErrorCode TryGetPlayerQueueID(PlayerHandle player, out int queueID) {
			int offset = player.HandleValue - 1;
			if (0 <= offset && offset < numPlayers) {
				queueID = offset;
				return ErrorCode.Success;
			}

			queueID = -1;
			return ErrorCode.InvalidPlayerHandle;
		}

		private PlayerHandle QueueToPlayerHandle(int queue) { return new PlayerHandle(queue + 1); }

		private PlayerHandle QueueToSpectatorHandle(int queue) {
			return new PlayerHandle(queue + 1000);
		} /* out of range of the player array, basically */

		private void DisconnectPlayerQueue(int queue, int syncTo) {
			int frameCount = sync.GetFrameCount();

			endpoints[queue].Disconnect();

			LogUtil.Log($"Changing queue {queue} local connect status for last frame from {localConnectStatuses[queue].LastFrame} to {syncTo} on disconnect request (current: {frameCount}).{Environment.NewLine}");

			localConnectStatuses[queue].IsDisconnected = true;
			localConnectStatuses[queue].LastFrame = syncTo;

			if (syncTo < frameCount) {
				LogUtil.Log($"adjusting simulation to account for the fact that {queue} disconnected @ {syncTo}.{Environment.NewLine}");
				sync.AdjustSimulation(syncTo);
				LogUtil.Log($"finished adjusting simulation.{Environment.NewLine}");
			}

			Event info = new Event {
				code = EventCode.DisconnectedFromPeer,
				disconnected = {
					player = QueueToPlayerHandle(queue)
				}
			};
			
			callbacks.OnEvent(info);

			CheckInitialSync();
		}

		private void PollSyncEvents() {
			while (sync.GetEvent(out Sync.Event syncEvent)) {
				OnSyncEvent(syncEvent);
			}
		}

		private void PollUdpProtocolEvents() {
			for (int i = 0; i < numPlayers; i++) {
				while (endpoints[i].GetEvent(out Peer.Event evt)) {
					OnUdpProtocolPeerEvent(ref evt, i);
				}
			}
			
			for (int i = 0; i < numSpectators; i++) {
				while (spectators[i].GetEvent(out Peer.Event evt)) {
					OnUdpProtocolSpectatorEvent(ref evt, i);
				}
			}
		}

		protected void CheckInitialSync() {
			if (synchronizing) {
				// Check to see if everyone is now synchronized.  If so,
				// go ahead and tell the client that we're ok to accept input.
				for (int i = 0; i < numPlayers; i++) {
					// xxx: IsInitialized() must go... we're actually using it as a proxy for "represents the local player"
					if (endpoints[i].IsInitialized() && !endpoints[i].IsSynchronized() && !localConnectStatuses[i].IsDisconnected) {
						return;
					}
				}
				
				for (int i = 0; i < numSpectators; i++) {
					if (spectators[i].IsInitialized() && !spectators[i].IsSynchronized()) {
						return;
					}
				}

				Event info = new Event {
					code = EventCode.Running
				};
				
				callbacks.OnEvent(info);
				synchronizing = false;
			}
		}

		protected int Poll2Players(int current_frame) {
			int i;

			// discard confirmed frames as appropriate
			int total_min_confirmed = int.MaxValue;
			for (i = 0; i < numPlayers; i++) {
				bool queue_connected = true;
				
				if (endpoints[i].IsRunning()) {
					queue_connected = endpoints[i].GetPeerConnectStatus(i, out int ignore);
				}
				
				if (!localConnectStatuses[i].IsDisconnected) {
					total_min_confirmed = Math.Min(localConnectStatuses[i].LastFrame, total_min_confirmed);
				}

				LogUtil.Log($"  local endp: connected = {!localConnectStatuses[i].IsDisconnected}, last_received = {localConnectStatuses[i].LastFrame}, total_min_confirmed = {total_min_confirmed}.{Environment.NewLine}");
				if (!queue_connected && !localConnectStatuses[i].IsDisconnected) {
					LogUtil.Log($"disconnecting i {i} by remote request.{Environment.NewLine}");
					DisconnectPlayerQueue(i, total_min_confirmed);
				}

				LogUtil.Log($"  total_min_confirmed = {total_min_confirmed}.{Environment.NewLine}");
			}
			
			return total_min_confirmed;
		}

		private int PollNPlayers() {
			int queue;

			// discard confirmed frames as appropriate
			int totalMinConfirmed = int.MaxValue;
			for (queue = 0; queue < numPlayers; queue++) {
				bool queueConnected = true;
				int queueMinConfirmed = int.MaxValue;
				LogUtil.Log($"considering queue {queue}.{Environment.NewLine}");

				for (int i = 0; i < numPlayers; i++) {
					// we're going to do a lot of logic here in consideration of endpoint i.
					// keep accumulating the minimum confirmed point for all n*n packets and
					// throw away the rest.
					if (endpoints[i].IsRunning()) {
						bool connected = endpoints[i].GetPeerConnectStatus(queue, out int lastReceivedFrameNumber);

						queueConnected = queueConnected && connected;
						queueMinConfirmed = Math.Min(lastReceivedFrameNumber, queueMinConfirmed);
						LogUtil.Log($"  endpoint {i}: connected = {connected}, {nameof(lastReceivedFrameNumber)} = {lastReceivedFrameNumber}, {nameof(queueMinConfirmed)} = {queueMinConfirmed}.{Environment.NewLine}");
					} else {
						LogUtil.Log($"  endpoint {i}: ignoring... not running.{Environment.NewLine}");
					}
				}
				
				// merge in our local status only if we're still connected!
				if (!localConnectStatuses[queue].IsDisconnected) {
					queueMinConfirmed = Math.Min(localConnectStatuses[queue].LastFrame, queueMinConfirmed);
				}

				LogUtil.Log($"  local endpoint: connected = {!localConnectStatuses[queue].IsDisconnected}, {nameof(PeerMessage.ConnectStatus.LastFrame)} = {localConnectStatuses[queue].LastFrame}, {nameof(queueMinConfirmed)} = {queueMinConfirmed}.{Environment.NewLine}");

				if (queueConnected) {
					totalMinConfirmed = Math.Min(queueMinConfirmed, totalMinConfirmed);
				} else {
					// check to see if this disconnect notification is further back than we've been before.  If
					// so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
					// and later receive a disconnect notification for frame n-1.
					if (!localConnectStatuses[queue].IsDisconnected || localConnectStatuses[queue].LastFrame > queueMinConfirmed) {
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
			synchronizing = true;
   
			endpoints[queue].Init(ref transport, ref poll, queue, remoteEndPoint, localConnectStatuses);
			endpoints[queue].SetDisconnectTimeout(disconnectTimeout);
			endpoints[queue].SetDisconnectNotifyStart(disconnectNotifyStart);
			endpoints[queue].Synchronize();
		}

		private ErrorCode AddSpectator(IPEndPoint remoteEndPoint) {
			if (numSpectators == Types.kMaxSpectators) {
				return ErrorCode.TooManySpectators;
			}
			
			// Currently, we can only add spectators before the game starts.
			if (!synchronizing) {
				return ErrorCode.InvalidRequest;
			}
			int queue = numSpectators++;

			spectators[queue].Init(ref transport, ref poll, queue + 1000, remoteEndPoint, localConnectStatuses);
			spectators[queue].SetDisconnectTimeout(disconnectTimeout);
			spectators[queue].SetDisconnectNotifyStart(disconnectNotifyStart);
			spectators[queue].Synchronize();

			return ErrorCode.Success;
		}
		
		protected virtual void OnSyncEvent(Sync.Event syncEvent) { } // TODO remove? does nothing here, but might do something in syncTest || spectator backends

		protected virtual void OnUdpProtocolEvent(ref Peer.Event evt, PlayerHandle handle) {
			switch (evt.type) {
				case Peer.Event.Type.Connected: {
					Event info = new Event {
						code = EventCode.ConnectedToPeer,
						connected = {
							player = handle
						}
					};
					
					callbacks.OnEvent(info);
					break;
				}
				case Peer.Event.Type.Synchronizing: {
					Event info = new Event {
						code = EventCode.SynchronizingWithPeer,
						synchronizing = {
							player = handle, count = evt.synchronizing.Count, total = evt.synchronizing.Total
						}
					};
					
					callbacks.OnEvent(info);
					break;
				}
				case Peer.Event.Type.Synchronized: {
					Event info = new Event {
						code = EventCode.SynchronizedWithPeer,
						synchronized = {
							player = handle
						}
					};
					 
					callbacks.OnEvent(info);

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
					
					callbacks.OnEvent(info);
					break;
				}

				case Peer.Event.Type.NetworkResumed: {
					Event info = new Event {
						code = EventCode.ConnectionResumed,
						connectionResumed = {
							player = handle
						}
					};
					
					callbacks.OnEvent(info);
					break;
				}
			}
		}

		protected virtual void OnUdpProtocolPeerEvent(ref Peer.Event evt, int queue) {
			OnUdpProtocolEvent(ref evt, QueueToPlayerHandle(queue));
			switch (evt.type) {
				case Peer.Event.Type.Input:
					if (!localConnectStatuses[queue].IsDisconnected) {
						int currentRemoteFrame = localConnectStatuses[queue].LastFrame;
						int newRemoteFrame = evt.input.Frame;
						
						Platform.Assert(currentRemoteFrame == -1 || newRemoteFrame == currentRemoteFrame + 1);

						sync.AddRemoteInput(queue, ref evt.input);
						// Notify the other endpoints which frame we received from a peer
						LogUtil.Log($"setting remote connect status for queue {queue} to {evt.input.Frame}{Environment.NewLine}");
						localConnectStatuses[queue].LastFrame = evt.input.Frame;
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
					spectators[queue].Disconnect();

					Event info = new Event {
						code = EventCode.DisconnectedFromPeer,
						disconnected = {
							player = handle
						}
					};
					
					callbacks.OnEvent(info);

					break;
			}
		}

		public virtual bool OnHandlePoll(object cookie) { return true; }
		public virtual bool OnMsgPoll(object cookie) { return true; }
		public virtual bool OnPeriodicPoll(object cookie, long lastFireTime) { return true; }
		public virtual bool OnLoopPoll(object cookie) { return true; }
	};
}