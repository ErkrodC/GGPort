using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using static GGPort.LogUtil;

namespace GGPort {
	public class PeerToPeerBackend : Session, IPollSink, UDP.Callbacks {
		const int kRecommendationInterval = 240;
		const int kDefaultDisconnectTimeout = 5000;
		const int kDefaultDisconnectNotifyStart = 750;

		private readonly SessionCallbacks callbacks;
		private Poll poll;
		private readonly Sync sync;
		private UDP udp;
		private UDPProtocol[] endpoints;
		private readonly UDPProtocol[] spectators = new UDPProtocol[Types.kMaxSpectators];
		private int numSpectators;
		private readonly int inputSize;

		private bool synchronizing;
		private readonly int numPlayers;
		private int nextRecommendedSleep;

		private int nextSpectatorFrame;
		private uint disconnectTimeout;
		private uint disconnectNotifyStart;

		private readonly UDPMessage.ConnectStatus[] localConnectStatus = new UDPMessage.ConnectStatus[UDPMessage.UDP_MSG_MAX_PLAYERS];

		public PeerToPeerBackend(
			ref SessionCallbacks cb,
			string gameName,
			ushort localPort,
			int numPlayers,
			int inputSize
		) {
			this.numPlayers = numPlayers;
			this.inputSize = inputSize;
			sync = new Sync(ref localConnectStatus);
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
			
			sync.Init(ref config);

			// Initialize the UDP port
			udp = new UDP();
			poll = new Poll();
			udp.Init(localPort, ref poll, this);

			endpoints = new UDPProtocol[this.numPlayers];
			for (int i = 0; i < this.numPlayers; i++) {
				endpoints[i] = new UDPProtocol();
			}

			for (int i = 0; i < localConnectStatus.Length; i++) {
				localConnectStatus[i] = default;
			}
			
			for (int i = 0; i < localConnectStatus.Length; i++) {
				localConnectStatus[i].LastFrame = -1;
			}

			// Preload the ROM
			callbacks.BeginGame(gameName);
		}

		~PeerToPeerBackend() {
			endpoints = null;
		}

		public override unsafe ErrorCode Idle(int timeout) {
			if (!sync.InRollback()) {
			   poll.Pump(0);

			   PollUdpProtocolEvents();

			   if (!synchronizing) {
				   sync.CheckSimulation(timeout);

				   // notify all of our endpoints of their local frame number for their
				   // next connection quality report
				   int current_frame = sync.GetFrameCount();
				   for (int i = 0; i < numPlayers; i++) {
					   endpoints[i].SetLocalFrameNumber(current_frame);
				   }

				   int total_min_confirmed;
				   if (numPlayers <= 2) {
					   total_min_confirmed = Poll2Players(current_frame);
				   } else {
					   total_min_confirmed = PollNPlayers(current_frame);
				   }

				   Log($"last confirmed frame in p2p backend is {total_min_confirmed}.{Environment.NewLine}");
				   if (total_min_confirmed >= 0) {
					   if (total_min_confirmed == int.MaxValue) {
						   throw new ArgumentException();
					   }
					   
					   if (numSpectators > 0) {
						   while (nextSpectatorFrame <= total_min_confirmed) {
							   Log($"pushing frame {nextSpectatorFrame} to spectators.{Environment.NewLine}");

							   GameInput input = new GameInput {
								   frame = nextSpectatorFrame,
								   size = inputSize * numPlayers
							   };
							   
							   sync.GetConfirmedInputs(input.bits, inputSize * numPlayers, nextSpectatorFrame);
							   for (int i = 0; i < numSpectators; i++) {
								   spectators[i].SendInput(ref input);
							   }
							   nextSpectatorFrame++;
						   }
					   }
					   
					   Log($"setting confirmed frame in sync to {total_min_confirmed}.{Environment.NewLine}");
					   sync.SetLastConfirmedFrame(total_min_confirmed);
				   }

				   // send timeSync notifications if now is the proper time
				   if (current_frame > nextRecommendedSleep) {
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
						   callbacks.OnEvent(ref info);
						   nextRecommendedSleep = current_frame + kRecommendationInterval;
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

		public override ErrorCode AddPlayer(ref Player player, out PlayerHandle handle) {
			handle = new PlayerHandle(-1);
			if (player.Type == GGPOPlayerType.Spectator) {
				return AddSpectator(player.EndPoint);
			}

			int queue = player.PlayerNum - 1;
			if (player.PlayerNum < 1 || player.PlayerNum > numPlayers) {
				return ErrorCode.PlayerOutOfRange;
			}
			
			handle = QueueToPlayerHandle(queue);

			if (player.Type == GGPOPlayerType.Remote) {
				AddRemotePlayer(player.EndPoint, queue);
			}
			
			return ErrorCode.Success;
		}

		public override ErrorCode AddLocalInput(PlayerHandle player, byte[] value, int size) {
			GameInput input = new GameInput();

			if (sync.InRollback()) {
				return ErrorCode.InRollback;
			}
			if (synchronizing) {
				return ErrorCode.NotSynchronized;
			}
   
			ErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Types.GGPOSucceeded(result)) {
				return result;
			}

			/*BinaryFormatter formatter = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(size)) {
				formatter.Serialize(ms, value);
				input.init(-1, ms.ToArray(), (int) ms.Length);
			} // TODO optimize/refactor*/
			
			input.init(-1, value, value.Length);

			// Feed the input for the current frame into the synchronzation layer.
			if (!sync.AddLocalInput(queue, ref input)) {
				return ErrorCode.PredictionThreshold;
			}

			if (input.frame != GameInput.kNullFrame) { // xxx: <- comment why this is the case
				// Update the local connect status state to indicate that we've got a
				// confirmed local frame for this player.  this must come first so it
				// gets incorporated into the next packet we send.
				Log($"setting local connect status for local queue {queue} to {input.frame}{Environment.NewLine}");
				localConnectStatus[queue].LastFrame = input.frame;

				// Send the input to all the remote players.
				for (int i = 0; i < numPlayers; i++) {
					if (endpoints[i].IsInitialized()) {
						endpoints[i].SendInput(ref input);
					}
				}
			}

			return ErrorCode.Success;
		}

		public override ErrorCode SynchronizeInput(ref Array values, int size, ref int disconnectFlags) {
			// Wait until we've started to return inputs.
			if (synchronizing) {
				return ErrorCode.NotSynchronized;
			}
			
			disconnectFlags = sync.SynchronizeInputs(ref values, size);
			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			Log($"End of frame ({sync.GetFrameCount()})...{Environment.NewLine}");
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
			ErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Types.GGPOSucceeded(result)) {
				return result;
			}
   
			if (localConnectStatus[queue].IsDisconnected) {
				return ErrorCode.PlayerDisconnected;
			}

			if (!endpoints[queue].IsInitialized()) {
				int current_frame = sync.GetFrameCount();
				
				// xxx: we should be tracking who the local player is, but for now assume
				// that if the endpoint is not initalized, this must be the local player.
				Log($"Disconnecting local player {queue} at frame {localConnectStatus[queue].LastFrame} by user request.{Environment.NewLine}");
				
				for (int i = 0; i < numPlayers; i++) {
					if (endpoints[i].IsInitialized()) {
						DisconnectPlayerQueue(i, current_frame);
					}
				}
			} else {
				Log($"Disconnecting queue {queue} at frame {localConnectStatus[queue].LastFrame} by user request.{Environment.NewLine}");
				DisconnectPlayerQueue(queue, localConnectStatus[queue].LastFrame);
			}
			return ErrorCode.Success;
		}

		public override ErrorCode GetNetworkStats(out NetworkStats stats, PlayerHandle player) {
			stats = default;

			ErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Types.GGPOSucceeded(result)) {
				return result;
			}
			
			endpoints[queue].GetNetworkStats(ref stats);

			return ErrorCode.Success;
		}

		public override ErrorCode SetFrameDelay(PlayerHandle player, int frameDelay) {
			ErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Types.GGPOSucceeded(result)) {
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

		public virtual void OnMsg(IPEndPoint from, ref UDPMessage msg, int len) {
			for (int i = 0; i < numPlayers; i++) {
				if (endpoints[i].HandlesMsg(ref from, ref msg)) {
					endpoints[i].OnMsg(ref msg, len);
					return;
				}
			}
			for (int i = 0; i < numSpectators; i++) {
				if (spectators[i].HandlesMsg(ref from, ref msg)) {
					spectators[i].OnMsg(ref msg, len);
					return;
				}
			}
		}

		protected ErrorCode PlayerHandleToQueue(PlayerHandle player, out int queue) {
			int offset = player.HandleValue - 1;
			if (offset < 0 || offset >= numPlayers) {
				queue = -1;
				return ErrorCode.InvalidPlayerHandle;
			}
			
			queue = offset;
			return ErrorCode.Success;
		}
		
		protected PlayerHandle QueueToPlayerHandle(int queue) { return new PlayerHandle(queue + 1); }

		protected PlayerHandle QueueToSpectatorHandle(int queue) {
			return new PlayerHandle(queue + 1000);
		} /* out of range of the player array, basically */

		protected void DisconnectPlayerQueue(int queue, int syncto) {
			int framecount = sync.GetFrameCount();

			endpoints[queue].Disconnect();

			Log($"Changing queue {queue} local connect status for last frame from {localConnectStatus[queue].LastFrame} to {syncto} on disconnect request (current: {framecount}).{Environment.NewLine}");

			localConnectStatus[queue].IsDisconnected = true;
			localConnectStatus[queue].LastFrame = syncto;

			if (syncto < framecount) {
				Log($"adjusting simulation to account for the fact that {queue} disconnected @ {syncto}.{Environment.NewLine}");
				sync.AdjustSimulation(syncto);
				Log($"finished adjusting simulation.{Environment.NewLine}");
			}

			Event info = new Event {
				code = EventCode.DisconnectedFromPeer,
				disconnected = {
					player = QueueToPlayerHandle(queue)
				}
			};
			
			callbacks.OnEvent(ref info);

			CheckInitialSync();
		}

		protected void PollSyncEvents() {
			while (sync.GetEvent(out Sync.Event e)) {
				OnSyncEvent(ref e); // TODO out param?
			}
		}

		protected void PollUdpProtocolEvents() {
			for (int i = 0; i < numPlayers; i++) {
				while (endpoints[i].GetEvent(out UDPProtocol.Event evt)) {
					OnUdpProtocolPeerEvent(ref evt, i);
				}
			}
			
			for (int i = 0; i < numSpectators; i++) {
				while (spectators[i].GetEvent(out UDPProtocol.Event evt)) {
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
					if (endpoints[i].IsInitialized() && !endpoints[i].IsSynchronized() && !localConnectStatus[i].IsDisconnected) {
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
				
				callbacks.OnEvent(ref info);
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
				
				if (!localConnectStatus[i].IsDisconnected) {
					total_min_confirmed = Math.Min(localConnectStatus[i].LastFrame, total_min_confirmed);
				}
				
				Log($"  local endp: connected = {!localConnectStatus[i].IsDisconnected}, last_received = {localConnectStatus[i].LastFrame}, total_min_confirmed = {total_min_confirmed}.{Environment.NewLine}");
				if (!queue_connected && !localConnectStatus[i].IsDisconnected) {
					Log($"disconnecting i {i} by remote request.{Environment.NewLine}");
					DisconnectPlayerQueue(i, total_min_confirmed);
				}
				
				Log($"  total_min_confirmed = {total_min_confirmed}.{Environment.NewLine}");
			}
			
			return total_min_confirmed;
		}

		protected int PollNPlayers(int current_frame) {
			int queue;

			// discard confirmed frames as appropriate
			int total_min_confirmed = int.MaxValue;
			for (queue = 0; queue < numPlayers; queue++) {
				bool queue_connected = true;
				int queue_min_confirmed = int.MaxValue;
				Log($"considering queue {queue}.{Environment.NewLine}");

				for (int i = 0; i < numPlayers; i++) {
					// we're going to do a lot of logic here in consideration of endpoint i.
					// keep accumulating the minimum confirmed point for all n*n packets and
					// throw away the rest.
					if (endpoints[i].IsRunning()) {
						bool connected = endpoints[i].GetPeerConnectStatus(queue, out int last_received);

						queue_connected = queue_connected && connected;
						queue_min_confirmed = Math.Min(last_received, queue_min_confirmed);
						Log($"  endpoint {i}: connected = {connected}, last_received = {last_received}, queue_min_confirmed = {queue_min_confirmed}.{Environment.NewLine}");
					} else {
						Log($"  endpoint {i}: ignoring... not running.{Environment.NewLine}");
					}
				}
				
				// merge in our local status only if we're still connected!
				if (!localConnectStatus[queue].IsDisconnected) {
					queue_min_confirmed = Math.Min(localConnectStatus[queue].LastFrame, queue_min_confirmed);
				}
				
				Log($"  local endp: connected = {!localConnectStatus[queue].IsDisconnected}, last_received = {localConnectStatus[queue].LastFrame}, queue_min_confirmed = {queue_min_confirmed}.{Environment.NewLine}");

				if (queue_connected) {
					total_min_confirmed = Math.Min(queue_min_confirmed, total_min_confirmed);
				} else {
					// check to see if this disconnect notification is further back than we've been before.  If
					// so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
					// and later receive a disconnect notification for frame n-1.
					if (!localConnectStatus[queue].IsDisconnected || localConnectStatus[queue].LastFrame > queue_min_confirmed) {
						Log($"disconnecting queue {queue} by remote request.{Environment.NewLine}");
						DisconnectPlayerQueue(queue, queue_min_confirmed);
					}
				}
				Log($"  total_min_confirmed = {total_min_confirmed}.{Environment.NewLine}");
			}
			
			return total_min_confirmed;
		}

		protected void AddRemotePlayer(IPEndPoint remoteEndPoint, int queue) {
			// Start the state machine (xxx: no)
			synchronizing = true;
   
			endpoints[queue].Init(ref udp, ref poll, queue, remoteEndPoint, localConnectStatus);
			endpoints[queue].SetDisconnectTimeout(disconnectTimeout);
			endpoints[queue].SetDisconnectNotifyStart(disconnectNotifyStart);
			endpoints[queue].Synchronize();
		}

		protected ErrorCode AddSpectator(IPEndPoint remoteEndPoint) {
			if (numSpectators == Types.kMaxSpectators) {
				return ErrorCode.TooManySpectators;
			}
			
			// Currently, we can only add spectators before the game starts.
			if (!synchronizing) {
				return ErrorCode.InvalidRequest;
			}
			int queue = numSpectators++;

			spectators[queue].Init(ref udp, ref poll, queue + 1000, remoteEndPoint, localConnectStatus);
			spectators[queue].SetDisconnectTimeout(disconnectTimeout);
			spectators[queue].SetDisconnectNotifyStart(disconnectNotifyStart);
			spectators[queue].Synchronize();

			return ErrorCode.Success;
		}
		
		protected virtual void OnSyncEvent(ref Sync.Event e) { }

		protected virtual void OnUdpProtocolEvent(ref UDPProtocol.Event evt, PlayerHandle handle) {
			switch (evt.type) {
				case UDPProtocol.Event.Type.Connected: {
					Event info = new Event {
						code = EventCode.ConnectedToPeer,
						connected = {
							player = handle
						}
					};
					
					callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronizing: {
					Event info = new Event {
						code = EventCode.SynchronizingWithPeer,
						synchronizing = {
							player = handle, count = evt.synchronizing.count, total = evt.synchronizing.total
						}
					};
					
					callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronized: {
					Event info = new Event {
						code = EventCode.SynchronizedWithPeer,
						synchronized = {
							player = handle
						}
					};
					 
					callbacks.OnEvent(ref info);

					CheckInitialSync();
					break;
				}

				case UDPProtocol.Event.Type.NetworkInterrupted: {
					Event info = new Event {
						code = EventCode.ConnectionInterrupted,
						connectionInterrupted = {
							player = handle, disconnect_timeout = evt.network_interrupted.disconnect_timeout
						}
					};
					
					callbacks.OnEvent(ref info);
					break;
				}

				case UDPProtocol.Event.Type.NetworkResumed: {
					Event info = new Event {
						code = EventCode.ConnectionResumed,
						connectionResumed = {
							player = handle
						}
					};
					
					callbacks.OnEvent(ref info);
					break;
				}
			}
		}

		protected virtual void OnUdpProtocolPeerEvent(ref UDPProtocol.Event evt, int queue) {
			OnUdpProtocolEvent(ref evt, QueueToPlayerHandle(queue));
			switch (evt.type) {
				case UDPProtocol.Event.Type.Input:
					if (!localConnectStatus[queue].IsDisconnected) {
						int current_remote_frame = localConnectStatus[queue].LastFrame;
						int new_remote_frame = evt.input.input.frame;
						
						if (current_remote_frame != -1 && new_remote_frame != current_remote_frame + 1) {
							throw new ArgumentException();
						}

						sync.AddRemoteInput(queue, ref evt.input.input);
						// Notify the other endpoints which frame we received from a peer
						Log($"setting remote connect status for queue {queue} to {evt.input.input.frame}{Environment.NewLine}");
						localConnectStatus[queue].LastFrame = evt.input.input.frame;
					}
					break;

				case UDPProtocol.Event.Type.Disconnected:
					DisconnectPlayer(QueueToPlayerHandle(queue));
					break;
			}
		}

		protected virtual void OnUdpProtocolSpectatorEvent(ref UDPProtocol.Event evt, int queue) {
			PlayerHandle handle = QueueToSpectatorHandle(queue);
			OnUdpProtocolEvent(ref evt, handle);

			switch (evt.type) {
				case UDPProtocol.Event.Type.Disconnected:
					spectators[queue].Disconnect();

					Event info = new Event {
						code = EventCode.DisconnectedFromPeer,
						disconnected = {
							player = handle
						}
					};
					
					callbacks.OnEvent(ref info);

					break;
			}
		}

		// TODO fix param names, 4 fxns
		public virtual bool OnHandlePoll(object TODO) { return true; }
		public virtual bool OnMsgPoll(object TODO) { return true; }
		public virtual bool OnPeriodicPoll(object TODO0, long TODO1) { return true; }
		public virtual bool OnLoopPoll(object cookie) { return true; }
	};
}