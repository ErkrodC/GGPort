using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;

namespace GGPort {
	public class PeerToPeerBackend : GGPOSession, IPollSink, UDP.Callbacks {
		const int RECOMMENDATION_INTERVAL = 240;
		const int DEFAULT_DISCONNECT_TIMEOUT = 5000;
		const int DEFAULT_DISCONNECT_NOTIFY_START = 750;

		protected SessionCallbacks _callbacks;
		protected Poll _poll;
		protected Sync _sync;
		protected UDP _udp;
		protected UDPProtocol[] _endpoints;
		protected UDPProtocol[] _spectators = new UDPProtocol[Types.kMaxSpectators];
		protected int _num_spectators;
		protected int _input_size;

		protected bool _synchronizing;
		protected int _num_players;
		protected int _next_recommended_sleep;

		protected int _next_spectator_frame;
		protected uint _disconnect_timeout;
		protected uint _disconnect_notify_start;

		protected UDPMessage.ConnectStatus[] _local_connect_status = new UDPMessage.ConnectStatus[UDPMessage.UDP_MSG_MAX_PLAYERS];

		public PeerToPeerBackend(
			ref SessionCallbacks cb,
			string gameName,
			ushort localPort,
			int numPlayers,
			int inputSize
		) {
			_num_players = numPlayers;
			_input_size = inputSize;
			_sync = new Sync(ref _local_connect_status);
			_disconnect_timeout = DEFAULT_DISCONNECT_TIMEOUT;
			_disconnect_notify_start = DEFAULT_DISCONNECT_NOTIFY_START;
			_num_spectators = 0;
			_next_spectator_frame = 0;
			_callbacks = cb;
			_synchronizing = true;
			_next_recommended_sleep = 0;

			// Initialize the synchronization layer
			Sync.Config config = new Sync.Config(
				_callbacks,
				Sync.kMaxPredictionFrames,
				numPlayers,
				inputSize
			);
			
			_sync.Init(ref config);

			// Initialize the UDP port
			_udp = new UDP();
			_poll = new Poll();
			_udp.Init(localPort, ref _poll, this);

			_endpoints = new UDPProtocol[_num_players];
			for (int i = 0; i < _num_players; i++) {
				_endpoints[i] = new UDPProtocol();
			}

			for (int i = 0; i < _local_connect_status.Length; i++) {
				_local_connect_status[i] = default;
			}
			
			for (int i = 0; i < _local_connect_status.Length; i++) {
				_local_connect_status[i].LastFrame = -1;
			}

			// Preload the ROM
			_callbacks.BeginGame(gameName);
		}

		~PeerToPeerBackend() {
			_endpoints = null;
		}

		public override unsafe ErrorCode DoPoll(int timeout) {
			if (!_sync.InRollback()) {
			   _poll.Pump(0);

			   PollUdpProtocolEvents();

			   if (!_synchronizing) {
				   _sync.CheckSimulation(timeout);

				   // notify all of our endpoints of their local frame number for their
				   // next connection quality report
				   int current_frame = _sync.GetFrameCount();
				   for (int i = 0; i < _num_players; i++) {
					   _endpoints[i].SetLocalFrameNumber(current_frame);
				   }

				   int total_min_confirmed;
				   if (_num_players <= 2) {
					   total_min_confirmed = Poll2Players(current_frame);
				   } else {
					   total_min_confirmed = PollNPlayers(current_frame);
				   }

				   Log($"last confirmed frame in p2p backend is {total_min_confirmed}.{Environment.NewLine}");
				   if (total_min_confirmed >= 0) {
					   if (total_min_confirmed == int.MaxValue) {
						   throw new ArgumentException();
					   }
					   
					   if (_num_spectators > 0) {
						   while (_next_spectator_frame <= total_min_confirmed) {
							   Log($"pushing frame {_next_spectator_frame} to spectators.{Environment.NewLine}");

							   GameInput input = new GameInput {
								   frame = _next_spectator_frame,
								   size = _input_size * _num_players
							   };
							   
							   _sync.GetConfirmedInputs(input.bits, _input_size * _num_players, _next_spectator_frame);
							   for (int i = 0; i < _num_spectators; i++) {
								   _spectators[i].SendInput(ref input);
							   }
							   _next_spectator_frame++;
						   }
					   }
					   
					   Log($"setting confirmed frame in sync to {total_min_confirmed}.{Environment.NewLine}");
					   _sync.SetLastConfirmedFrame(total_min_confirmed);
				   }

				   // send timesync notifications if now is the proper time
				   if (current_frame > _next_recommended_sleep) {
					   int interval = 0;
					   for (int i = 0; i < _num_players; i++) {
						   interval = Math.Max(interval, _endpoints[i].RecommendFrameDelay());
					   }

					   if (interval > 0) {
						   GGPOEvent info = new GGPOEvent {
							   code = GGPOEventCode.TimeSync,
							   timeSync = {
								   framesAhead = interval
							   }
						   };
						   _callbacks.OnEvent(ref info);
						   _next_recommended_sleep = current_frame + RECOMMENDATION_INTERVAL;
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

		public override ErrorCode AddPlayer(ref GGPOPlayer player, out GGPOPlayerHandle handle) {
			handle = new GGPOPlayerHandle(-1);
			if (player.Type == GGPOPlayerType.Spectator) {
				return AddSpectator(player.EndPoint);
			}

			int queue = player.PlayerNum - 1;
			if (player.PlayerNum < 1 || player.PlayerNum > _num_players) {
				return ErrorCode.PlayerOutOfRange;
			}
			
			handle = QueueToPlayerHandle(queue);

			if (player.Type == GGPOPlayerType.Remote) {
				AddRemotePlayer(player.EndPoint, queue);
			}
			
			return ErrorCode.Success;
		}

		public override ErrorCode AddLocalInput(GGPOPlayerHandle player, byte[] value, int size) {
			GameInput input = new GameInput();

			if (_sync.InRollback()) {
				return ErrorCode.InRollback;
			}
			if (_synchronizing) {
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
			if (!_sync.AddLocalInput(queue, ref input)) {
				return ErrorCode.PredictionThreshold;
			}

			if (input.frame != GameInput.kNullFrame) { // xxx: <- comment why this is the case
				// Update the local connect status state to indicate that we've got a
				// confirmed local frame for this player.  this must come first so it
				// gets incorporated into the next packet we send.
				Log($"setting local connect status for local queue {queue} to {input.frame}{Environment.NewLine}");
				_local_connect_status[queue].LastFrame = input.frame;

				// Send the input to all the remote players.
				for (int i = 0; i < _num_players; i++) {
					if (_endpoints[i].IsInitialized()) {
						_endpoints[i].SendInput(ref input);
					}
				}
			}

			return ErrorCode.Success;
		}

		public override ErrorCode SyncInput(ref Array values, int size, ref int? disconnect_flags) {
			// Wait until we've started to return inputs.
			if (_synchronizing) {
				return ErrorCode.NotSynchronized;
			}
			
			int flags = _sync.SynchronizeInputs(ref values, size);
			if (disconnect_flags != null) {
				disconnect_flags = flags;
			}
			
			return ErrorCode.Success;
		}

		public override ErrorCode IncrementFrame() {
			Log($"End of frame ({_sync.GetFrameCount()})...{Environment.NewLine}");
			_sync.IncrementFrame();
			DoPoll(0);
			PollSyncEvents();

			return ErrorCode.Success;
		}

		/*
		* Called only as the result of a local decision to disconnect.  The remote
		* decisions to disconnect are a result of us parsing the peer_connect_settings
		* blob in every endpoint periodically.
		*/
		public override ErrorCode DisconnectPlayer(GGPOPlayerHandle player) {
			ErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Types.GGPOSucceeded(result)) {
				return result;
			}
   
			if (_local_connect_status[queue].IsDisconnected) {
				return ErrorCode.PlayerDisconnected;
			}

			if (!_endpoints[queue].IsInitialized()) {
				int current_frame = _sync.GetFrameCount();
				
				// xxx: we should be tracking who the local player is, but for now assume
				// that if the endpoint is not initalized, this must be the local player.
				Log($"Disconnecting local player {queue} at frame {_local_connect_status[queue].LastFrame} by user request.{Environment.NewLine}");
				
				for (int i = 0; i < _num_players; i++) {
					if (_endpoints[i].IsInitialized()) {
						DisconnectPlayerQueue(i, current_frame);
					}
				}
			} else {
				Log($"Disconnecting queue {queue} at frame {_local_connect_status[queue].LastFrame} by user request.{Environment.NewLine}");
				DisconnectPlayerQueue(queue, _local_connect_status[queue].LastFrame);
			}
			return ErrorCode.Success;
		}

		public override ErrorCode GetNetworkStats(out GGPONetworkStats stats, GGPOPlayerHandle player) {
			stats = default;

			ErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Types.GGPOSucceeded(result)) {
				return result;
			}
			
			_endpoints[queue].GetNetworkStats(ref stats);

			return ErrorCode.Success;
		}

		public override ErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay) {
			ErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Types.GGPOSucceeded(result)) {
				return result;
			}
			
			_sync.SetFrameDelay(queue, delay);
			return ErrorCode.Success;
		}

		public override ErrorCode SetDisconnectTimeout(uint timeout) {
			_disconnect_timeout = timeout;
			for (int i = 0; i < _num_players; i++) {
				if (_endpoints[i].IsInitialized()) {
					_endpoints[i].SetDisconnectTimeout(_disconnect_timeout);
				}
			}
			
			return ErrorCode.Success;
		}

		public override ErrorCode SetDisconnectNotifyStart(uint timeout) {
			_disconnect_notify_start = timeout;
			for (int i = 0; i < _num_players; i++) {
				if (_endpoints[i].IsInitialized()) {
					_endpoints[i].SetDisconnectNotifyStart(_disconnect_notify_start);
				}
			}
			return ErrorCode.Success;
		}

		public virtual void OnMsg(IPEndPoint from, ref UDPMessage msg, int len) {
			for (int i = 0; i < _num_players; i++) {
				if (_endpoints[i].HandlesMsg(ref from, ref msg)) {
					_endpoints[i].OnMsg(ref msg, len);
					return;
				}
			}
			for (int i = 0; i < _num_spectators; i++) {
				if (_spectators[i].HandlesMsg(ref from, ref msg)) {
					_spectators[i].OnMsg(ref msg, len);
					return;
				}
			}
		}

		protected ErrorCode PlayerHandleToQueue(GGPOPlayerHandle player, out int queue) {
			int offset = player.HandleValue - 1;
			if (offset < 0 || offset >= _num_players) {
				queue = -1;
				return ErrorCode.InvalidPlayerHandle;
			}
			
			queue = offset;
			return ErrorCode.Success;
		}
		
		protected GGPOPlayerHandle QueueToPlayerHandle(int queue) { return new GGPOPlayerHandle(queue + 1); }

		protected GGPOPlayerHandle QueueToSpectatorHandle(int queue) {
			return new GGPOPlayerHandle(queue + 1000);
		} /* out of range of the player array, basically */

		protected void DisconnectPlayerQueue(int queue, int syncto) {
			int framecount = _sync.GetFrameCount();

			_endpoints[queue].Disconnect();

			Log($"Changing queue {queue} local connect status for last frame from {_local_connect_status[queue].LastFrame} to {syncto} on disconnect request (current: {framecount}).{Environment.NewLine}");

			_local_connect_status[queue].IsDisconnected = true;
			_local_connect_status[queue].LastFrame = syncto;

			if (syncto < framecount) {
				Log($"adjusting simulation to account for the fact that {queue} disconnected @ {syncto}.{Environment.NewLine}");
				_sync.AdjustSimulation(syncto);
				Log($"finished adjusting simulation.{Environment.NewLine}");
			}

			GGPOEvent info = new GGPOEvent {
				code = GGPOEventCode.DisconnectedFromPeer,
				disconnected = {
					player = QueueToPlayerHandle(queue)
				}
			};
			
			_callbacks.OnEvent(ref info);

			CheckInitialSync();
		}

		protected void PollSyncEvents() {
			while (_sync.GetEvent(out Sync.Event e)) {
				OnSyncEvent(ref e); // TODO out param?
			}
		}

		protected void PollUdpProtocolEvents() {
			for (int i = 0; i < _num_players; i++) {
				while (_endpoints[i].GetEvent(out UDPProtocol.Event evt)) {
					OnUdpProtocolPeerEvent(ref evt, i);
				}
			}
			
			for (int i = 0; i < _num_spectators; i++) {
				while (_spectators[i].GetEvent(out UDPProtocol.Event evt)) {
					OnUdpProtocolSpectatorEvent(ref evt, i);
				}
			}
		}

		protected void CheckInitialSync() {
			if (_synchronizing) {
				// Check to see if everyone is now synchronized.  If so,
				// go ahead and tell the client that we're ok to accept input.
				for (int i = 0; i < _num_players; i++) {
					// xxx: IsInitialized() must go... we're actually using it as a proxy for "represents the local player"
					if (_endpoints[i].IsInitialized() && !_endpoints[i].IsSynchronized() && !_local_connect_status[i].IsDisconnected) {
						return;
					}
				}
				
				for (int i = 0; i < _num_spectators; i++) {
					if (_spectators[i].IsInitialized() && !_spectators[i].IsSynchronized()) {
						return;
					}
				}

				GGPOEvent info = new GGPOEvent {
					code = GGPOEventCode.Running
				};
				
				_callbacks.OnEvent(ref info);
				_synchronizing = false;
			}
		}

		protected int Poll2Players(int current_frame) {
			int i;

			// discard confirmed frames as appropriate
			int total_min_confirmed = int.MaxValue;
			for (i = 0; i < _num_players; i++) {
				bool queue_connected = true;
				
				if (_endpoints[i].IsRunning()) {
					queue_connected = _endpoints[i].GetPeerConnectStatus(i, out int ignore);
				}
				
				if (!_local_connect_status[i].IsDisconnected) {
					total_min_confirmed = Math.Min(_local_connect_status[i].LastFrame, total_min_confirmed);
				}
				
				Log($"  local endp: connected = {!_local_connect_status[i].IsDisconnected}, last_received = {_local_connect_status[i].LastFrame}, total_min_confirmed = {total_min_confirmed}.{Environment.NewLine}");
				if (!queue_connected && !_local_connect_status[i].IsDisconnected) {
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
			for (queue = 0; queue < _num_players; queue++) {
				bool queue_connected = true;
				int queue_min_confirmed = int.MaxValue;
				Log($"considering queue {queue}.{Environment.NewLine}");

				for (int i = 0; i < _num_players; i++) {
					// we're going to do a lot of logic here in consideration of endpoint i.
					// keep accumulating the minimum confirmed point for all n*n packets and
					// throw away the rest.
					if (_endpoints[i].IsRunning()) {
						bool connected = _endpoints[i].GetPeerConnectStatus(queue, out int last_received); // TODO out param?

						queue_connected = queue_connected && connected;
						queue_min_confirmed = Math.Min(last_received, queue_min_confirmed);
						Log($"  endpoint {i}: connected = {connected}, last_received = {last_received}, queue_min_confirmed = {queue_min_confirmed}.{Environment.NewLine}");
					} else {
						Log($"  endpoint {i}: ignoring... not running.{Environment.NewLine}");
					}
				}
				
				// merge in our local status only if we're still connected!
				if (!_local_connect_status[queue].IsDisconnected) {
					queue_min_confirmed = Math.Min(_local_connect_status[queue].LastFrame, queue_min_confirmed);
				}
				
				Log($"  local endp: connected = {!_local_connect_status[queue].IsDisconnected}, last_received = {_local_connect_status[queue].LastFrame}, queue_min_confirmed = {queue_min_confirmed}.{Environment.NewLine}");

				if (queue_connected) {
					total_min_confirmed = Math.Min(queue_min_confirmed, total_min_confirmed);
				} else {
					// check to see if this disconnect notification is further back than we've been before.  If
					// so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
					// and later receive a disconnect notification for frame n-1.
					if (!_local_connect_status[queue].IsDisconnected || _local_connect_status[queue].LastFrame > queue_min_confirmed) {
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
			_synchronizing = true;
   
			_endpoints[queue].Init(ref _udp, ref _poll, queue, remoteEndPoint, _local_connect_status);
			_endpoints[queue].SetDisconnectTimeout(_disconnect_timeout);
			_endpoints[queue].SetDisconnectNotifyStart(_disconnect_notify_start);
			_endpoints[queue].Synchronize();
		}

		protected ErrorCode AddSpectator(IPEndPoint remoteEndPoint) {
			if (_num_spectators == Types.kMaxSpectators) {
				return ErrorCode.TooManySpectators;
			}
			
			// Currently, we can only add spectators before the game starts.
			if (!_synchronizing) {
				return ErrorCode.InvalidRequest;
			}
			int queue = _num_spectators++;

			_spectators[queue].Init(ref _udp, ref _poll, queue + 1000, remoteEndPoint, _local_connect_status);
			_spectators[queue].SetDisconnectTimeout(_disconnect_timeout);
			_spectators[queue].SetDisconnectNotifyStart(_disconnect_notify_start);
			_spectators[queue].Synchronize();

			return ErrorCode.Success;
		}
		
		protected virtual void OnSyncEvent(ref Sync.Event e) { }

		protected virtual void OnUdpProtocolEvent(ref UDPProtocol.Event evt, GGPOPlayerHandle handle) {
			switch (evt.type) {
				case UDPProtocol.Event.Type.Connected: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.ConnectedToPeer,
						connected = {
							player = handle
						}
					};
					
					_callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronizing: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.SynchronizingWithPeer,
						synchronizing = {
							player = handle, count = evt.synchronizing.count, total = evt.synchronizing.total
						}
					};
					
					_callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronized: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.SynchronizedWithPeer,
						synchronized = {
							player = handle
						}
					};
					 
					_callbacks.OnEvent(ref info);

					CheckInitialSync();
					break;
				}

				case UDPProtocol.Event.Type.NetworkInterrupted: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.ConnectionInterrupted,
						connectionInterrupted = {
							player = handle, disconnect_timeout = evt.network_interrupted.disconnect_timeout
						}
					};
					
					_callbacks.OnEvent(ref info);
					break;
				}

				case UDPProtocol.Event.Type.NetworkResumed: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.ConnectionResumed,
						connectionResumed = {
							player = handle
						}
					};
					
					_callbacks.OnEvent(ref info);
					break;
				}
			}
		}

		protected virtual void OnUdpProtocolPeerEvent(ref UDPProtocol.Event evt, int queue) {
			OnUdpProtocolEvent(ref evt, QueueToPlayerHandle(queue));
			switch (evt.type) {
				case UDPProtocol.Event.Type.Input:
					if (!_local_connect_status[queue].IsDisconnected) {
						int current_remote_frame = _local_connect_status[queue].LastFrame;
						int new_remote_frame = evt.input.input.frame;
						
						if (current_remote_frame != -1 && new_remote_frame != current_remote_frame + 1) {
							throw new ArgumentException();
						}

						_sync.AddRemoteInput(queue, ref evt.input.input);
						// Notify the other endpoints which frame we received from a peer
						Log($"setting remote connect status for queue {queue} to {evt.input.input.frame}{Environment.NewLine}");
						_local_connect_status[queue].LastFrame = evt.input.input.frame;
					}
					break;

				case UDPProtocol.Event.Type.Disconnected:
					DisconnectPlayer(QueueToPlayerHandle(queue));
					break;
			}
		}

		protected virtual void OnUdpProtocolSpectatorEvent(ref UDPProtocol.Event evt, int queue) {
			GGPOPlayerHandle handle = QueueToSpectatorHandle(queue);
			OnUdpProtocolEvent(ref evt, handle);

			switch (evt.type) {
				case UDPProtocol.Event.Type.Disconnected:
					_spectators[queue].Disconnect();

					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.DisconnectedFromPeer,
						disconnected = {
							player = handle
						}
					};
					
					_callbacks.OnEvent(ref info);

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