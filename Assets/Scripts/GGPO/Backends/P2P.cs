using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;

namespace GGPort {
	public class Peer2PeerBackend : GGPOSession, IPollSink, Udp.Callbacks {
		const int RECOMMENDATION_INTERVAL = 240;
		const int DEFAULT_DISCONNECT_TIMEOUT = 5000;
		const int DEFAULT_DISCONNECT_NOTIFY_START = 750;

		protected GGPOSessionCallbacks _callbacks;
		protected Poll _poll;
		protected Sync _sync;
		protected Udp _udp;
		protected UdpProtocol[] _endpoints;
		protected UdpProtocol[] _spectators = new UdpProtocol[Globals.GGPO_MAX_SPECTATORS];
		protected int _num_spectators;
		protected int _input_size;

		protected bool _synchronizing;
		protected int _num_players;
		protected int _next_recommended_sleep;

		protected int _next_spectator_frame;
		protected uint _disconnect_timeout;
		protected uint _disconnect_notify_start;

		protected UdpMsg.connect_status[] _local_connect_status = new UdpMsg.connect_status[UdpMsg.UDP_MSG_MAX_PLAYERS];

		public unsafe Peer2PeerBackend(
			ref GGPOSessionCallbacks cb,
			string gamename,
			ushort localport,
			int num_players,
			int input_size
		) {
			_num_players = num_players;
			_input_size = input_size;
			_sync = new Sync(ref _local_connect_status);
			_disconnect_timeout = DEFAULT_DISCONNECT_TIMEOUT;
			_disconnect_notify_start = DEFAULT_DISCONNECT_NOTIFY_START;
			_num_spectators = 0;
			_next_spectator_frame = 0;
			_callbacks = cb;
			_synchronizing = true;
			_next_recommended_sleep = 0;

			/*
			* Initialize the synchronziation layer
			*/
			Sync.Config config = new Sync.Config(
				_callbacks,
				Sync.MAX_PREDICTION_FRAMES,
				num_players,
				input_size
			);
			
			_sync.Init(ref config);

			/*
			 * Initialize the UDP port
			 */
			_udp = new Udp();
			_poll = new Poll();
			_udp.Init(localport, ref _poll, this);

			_endpoints = new UdpProtocol[_num_players];
			for (int i = 0; i < _num_players; i++) {
				_endpoints[i] = new UdpProtocol();
			}
			

			for (int i = 0; i < _local_connect_status.Length; i++) {
				_local_connect_status[i] = default;
			}
			
			for (int i = 0; i < _local_connect_status.Length; i++) {
				_local_connect_status[i].last_frame = -1;
			}

			/*
			* Preload the ROM
			*/
			_callbacks.begin_game(gamename);
		}

		~Peer2PeerBackend() {
			_endpoints = null;
		}

		public override unsafe GGPOErrorCode DoPoll(int timeout) {
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

				   Log($"last confirmed frame in p2p backend is {total_min_confirmed}.\n");
				   if (total_min_confirmed >= 0) {
					   if (total_min_confirmed == int.MaxValue) {
						   throw new ArgumentException();
					   }
					   
					   if (_num_spectators > 0) {
						   while (_next_spectator_frame <= total_min_confirmed) {
							   Log($"pushing frame {_next_spectator_frame} to spectators.\n");

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
					   
					   Log($"setting confirmed frame in sync to {total_min_confirmed}.\n");
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
							   code = GGPOEventCode.GGPO_EVENTCODE_TIMESYNC,
							   timesync = {
								   frames_ahead = interval
							   }
						   };
						   _callbacks.on_event(ref info);
						   _next_recommended_sleep = current_frame + RECOMMENDATION_INTERVAL;
					   }
				   }
				   
				   // XXX: this is obviously a farce...
				   if (timeout != 0) {
					   Thread.Sleep(1);
				   }
			   }
			}
			
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode AddPlayer(ref GGPOPlayer player, out GGPOPlayerHandle handle) {
			handle = new GGPOPlayerHandle(-1);
			if (player.type == GGPOPlayerType.GGPO_PLAYERTYPE_SPECTATOR) {
				return AddSpectator(player.remote);
			}

			int queue = player.player_num - 1;
			if (player.player_num < 1 || player.player_num > _num_players) {
				return GGPOErrorCode.GGPO_ERRORCODE_PLAYER_OUT_OF_RANGE;
			}
			
			handle = QueueToPlayerHandle(queue);

			if (player.type == GGPOPlayerType.GGPO_PLAYERTYPE_REMOTE) {
				AddRemotePlayer(player.remote, queue);
			}
			
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode AddLocalInput(GGPOPlayerHandle player, object value, int size) {
			GameInput input = new GameInput();

			if (_sync.InRollback()) {
				return GGPOErrorCode.GGPO_ERRORCODE_IN_ROLLBACK;
			}
			if (_synchronizing) {
				return GGPOErrorCode.GGPO_ERRORCODE_NOT_SYNCHRONIZED;
			}
   
			GGPOErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Globals.GGPO_SUCCEEDED(result)) {
				return result;
			}

			BinaryFormatter bf = new BinaryFormatter();
			byte[] valByteArr = new byte[size];
			using (MemoryStream ms = new MemoryStream(valByteArr)) {
				bf.Serialize(ms, value);
			} // TODO optimize refactor
			
			input.init(-1, valByteArr, size);

			// Feed the input for the current frame into the synchronzation layer.
			if (!_sync.AddLocalInput(queue, ref input)) {
				return GGPOErrorCode.GGPO_ERRORCODE_PREDICTION_THRESHOLD;
			}

			if (input.frame != GameInput.NullFrame) { // xxx: <- comment why this is the case
				// Update the local connect status state to indicate that we've got a
				// confirmed local frame for this player.  this must come first so it
				// gets incorporated into the next packet we send.
				Log($"setting local connect status for local queue {queue} to {input.frame}");
				_local_connect_status[queue].last_frame = input.frame;

				// Send the input to all the remote players.
				for (int i = 0; i < _num_players; i++) {
					if (_endpoints[i].IsInitialized()) {
						_endpoints[i].SendInput(ref input);
					}
				}
			}

			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode SyncInput(ref Array values, int size, ref int? disconnect_flags) {
			// Wait until we've started to return inputs.
			if (_synchronizing) {
				return GGPOErrorCode.GGPO_ERRORCODE_NOT_SYNCHRONIZED;
			}
			
			int flags = _sync.SynchronizeInputs(ref values, size);
			if (disconnect_flags != null) {
				disconnect_flags = flags;
			}
			
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode IncrementFrame() {
			Log($"End of frame ({_sync.GetFrameCount()})...\n");
			_sync.IncrementFrame();
			DoPoll(0);
			PollSyncEvents();

			return GGPOErrorCode.GGPO_OK;
		}

		/*
		* Called only as the result of a local decision to disconnect.  The remote
		* decisions to disconnect are a result of us parsing the peer_connect_settings
		* blob in every endpoint periodically.
		*/
		public override GGPOErrorCode DisconnectPlayer(GGPOPlayerHandle player) {
			GGPOErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Globals.GGPO_SUCCEEDED(result)) {
				return result;
			}
   
			if (_local_connect_status[queue].disconnected) {
				return GGPOErrorCode.GGPO_ERRORCODE_PLAYER_DISCONNECTED;
			}

			if (!_endpoints[queue].IsInitialized()) {
				int current_frame = _sync.GetFrameCount();
				
				// xxx: we should be tracking who the local player is, but for now assume
				// that if the endpoint is not initalized, this must be the local player.
				Log($"Disconnecting local player {queue} at frame {_local_connect_status[queue].last_frame} by user request.\n");
				
				for (int i = 0; i < _num_players; i++) {
					if (_endpoints[i].IsInitialized()) {
						DisconnectPlayerQueue(i, current_frame);
					}
				}
			} else {
				Log($"Disconnecting queue {queue} at frame {_local_connect_status[queue].last_frame} by user request.\n");
				DisconnectPlayerQueue(queue, _local_connect_status[queue].last_frame);
			}
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode GetNetworkStats(out GGPONetworkStats stats, GGPOPlayerHandle player) {
			stats = default;

			GGPOErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Globals.GGPO_SUCCEEDED(result)) {
				return result;
			}
			
			_endpoints[queue].GetNetworkStats(ref stats);

			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay) {
			GGPOErrorCode result = PlayerHandleToQueue(player, out int queue);
			if (!Globals.GGPO_SUCCEEDED(result)) {
				return result;
			}
			
			_sync.SetFrameDelay(queue, delay);
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode SetDisconnectTimeout(uint timeout) {
			_disconnect_timeout = timeout;
			for (int i = 0; i < _num_players; i++) {
				if (_endpoints[i].IsInitialized()) {
					_endpoints[i].SetDisconnectTimeout(_disconnect_timeout);
				}
			}
			
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode SetDisconnectNotifyStart(uint timeout) {
			_disconnect_notify_start = timeout;
			for (int i = 0; i < _num_players; i++) {
				if (_endpoints[i].IsInitialized()) {
					_endpoints[i].SetDisconnectNotifyStart(_disconnect_notify_start);
				}
			}
			return GGPOErrorCode.GGPO_OK;
		}

		public virtual void OnMsg(IPEndPoint from, ref UdpMsg msg, int len) {
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

		protected GGPOErrorCode PlayerHandleToQueue(GGPOPlayerHandle player, out int queue) {
			int offset = player.handleValue - 1;
			if (offset < 0 || offset >= _num_players) {
				queue = -1;
				return GGPOErrorCode.GGPO_ERRORCODE_INVALID_PLAYER_HANDLE;
			}
			
			queue = offset;
			return GGPOErrorCode.GGPO_OK;
		}
		
		protected GGPOPlayerHandle QueueToPlayerHandle(int queue) { return new GGPOPlayerHandle(queue + 1); }

		protected GGPOPlayerHandle QueueToSpectatorHandle(int queue) {
			return new GGPOPlayerHandle(queue + 1000);
		} /* out of range of the player array, basically */

		protected void DisconnectPlayerQueue(int queue, int syncto) {
			int framecount = _sync.GetFrameCount();

			_endpoints[queue].Disconnect();

			Log($"Changing queue {queue} local connect status for last frame from {_local_connect_status[queue].last_frame} to {syncto} on disconnect request (current: {framecount}).\n");

			_local_connect_status[queue].disconnected = true;
			_local_connect_status[queue].last_frame = syncto;

			if (syncto < framecount) {
				Log($"adjusting simulation to account for the fact that {queue} disconnected @ {syncto}.\n");
				_sync.AdjustSimulation(syncto);
				Log("finished adjusting simulation.\n");
			}

			GGPOEvent info = new GGPOEvent {
				code = GGPOEventCode.GGPO_EVENTCODE_DISCONNECTED_FROM_PEER,
				disconnected = {
					player = QueueToPlayerHandle(queue)
				}
			};
			
			_callbacks.on_event(ref info);

			CheckInitialSync();
		}

		protected void PollSyncEvents() {
			while (_sync.GetEvent(out Sync.Event e)) {
				OnSyncEvent(ref e); // TODO out param?
			}
		}

		protected void PollUdpProtocolEvents() {
			for (int i = 0; i < _num_players; i++) {
				while (_endpoints[i].GetEvent(out UdpProtocol.Event evt)) {
					OnUdpProtocolPeerEvent(ref evt, i);
				}
			}
			
			for (int i = 0; i < _num_spectators; i++) {
				while (_spectators[i].GetEvent(out UdpProtocol.Event evt)) {
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
					if (_endpoints[i].IsInitialized() && !_endpoints[i].IsSynchronized() && !_local_connect_status[i].disconnected) {
						return;
					}
				}
				
				for (int i = 0; i < _num_spectators; i++) {
					if (_spectators[i].IsInitialized() && !_spectators[i].IsSynchronized()) {
						return;
					}
				}

				GGPOEvent info = new GGPOEvent {
					code = GGPOEventCode.GGPO_EVENTCODE_RUNNING
				};
				
				_callbacks.on_event(ref info);
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
				
				if (!_local_connect_status[i].disconnected) {
					total_min_confirmed = Math.Min(_local_connect_status[i].last_frame, total_min_confirmed);
				}
				
				Log($"  local endp: connected = {!_local_connect_status[i].disconnected}, last_received = {_local_connect_status[i].last_frame}, total_min_confirmed = {total_min_confirmed}.\n");
				if (!queue_connected && !_local_connect_status[i].disconnected) {
					Log($"disconnecting i {i} by remote request.\n");
					DisconnectPlayerQueue(i, total_min_confirmed);
				}
				
				Log($"  total_min_confirmed = {total_min_confirmed}.\n");
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
				Log($"considering queue {queue}.\n");

				for (int i = 0; i < _num_players; i++) {
					// we're going to do a lot of logic here in consideration of endpoint i.
					// keep accumulating the minimum confirmed point for all n*n packets and
					// throw away the rest.
					if (_endpoints[i].IsRunning()) {
						bool connected = _endpoints[i].GetPeerConnectStatus(queue, out int last_received); // TODO out param?

						queue_connected = queue_connected && connected;
						queue_min_confirmed = Math.Min(last_received, queue_min_confirmed);
						Log($"  endpoint {i}: connected = {connected}, last_received = {last_received}, queue_min_confirmed = {queue_min_confirmed}.\n");
					} else {
						Log($"  endpoint {i}: ignoring... not running.\n");
					}
				}
				
				// merge in our local status only if we're still connected!
				if (!_local_connect_status[queue].disconnected) {
					queue_min_confirmed = Math.Min(_local_connect_status[queue].last_frame, queue_min_confirmed);
				}
				
				Log($"  local endp: connected = {!_local_connect_status[queue].disconnected}, last_received = {_local_connect_status[queue].last_frame}, queue_min_confirmed = {queue_min_confirmed}.\n");

				if (queue_connected) {
					total_min_confirmed = Math.Min(queue_min_confirmed, total_min_confirmed);
				} else {
					// check to see if this disconnect notification is further back than we've been before.  If
					// so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
					// and later receive a disconnect notification for frame n-1.
					if (!_local_connect_status[queue].disconnected || _local_connect_status[queue].last_frame > queue_min_confirmed) {
						Log($"disconnecting queue {queue} by remote request.\n");
						DisconnectPlayerQueue(queue, queue_min_confirmed);
					}
				}
				Log($"  total_min_confirmed = {total_min_confirmed}.\n");
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

		protected GGPOErrorCode AddSpectator(IPEndPoint remoteEndPoint) {
			if (_num_spectators == Globals.GGPO_MAX_SPECTATORS) {
				return GGPOErrorCode.GGPO_ERRORCODE_TOO_MANY_SPECTATORS;
			}
			
			// Currently, we can only add spectators before the game starts.
			if (!_synchronizing) {
				return GGPOErrorCode.GGPO_ERRORCODE_INVALID_REQUEST;
			}
			int queue = _num_spectators++;

			_spectators[queue].Init(ref _udp, ref _poll, queue + 1000, remoteEndPoint, _local_connect_status);
			_spectators[queue].SetDisconnectTimeout(_disconnect_timeout);
			_spectators[queue].SetDisconnectNotifyStart(_disconnect_notify_start);
			_spectators[queue].Synchronize();

			return GGPOErrorCode.GGPO_OK;
		}
		
		protected virtual void OnSyncEvent(ref Sync.Event e) { }

		protected virtual void OnUdpProtocolEvent(ref UdpProtocol.Event evt, GGPOPlayerHandle handle) {
			switch (evt.type) {
				case UdpProtocol.Event.Type.Connected: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.GGPO_EVENTCODE_CONNECTED_TO_PEER,
						connected = {
							player = handle
						}
					};
					
					_callbacks.on_event(ref info);
					break;
				}
				case UdpProtocol.Event.Type.Synchronizing: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.GGPO_EVENTCODE_SYNCHRONIZING_WITH_PEER,
						synchronizing = {
							player = handle, count = evt.synchronizing.count, total = evt.synchronizing.total
						}
					};
					
					_callbacks.on_event(ref info);
					break;
				}
				case UdpProtocol.Event.Type.Synchronzied: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.GGPO_EVENTCODE_SYNCHRONIZED_WITH_PEER,
						synchronized = {
							player = handle
						}
					};
					 
					_callbacks.on_event(ref info);

					CheckInitialSync();
					break;
				}

				case UdpProtocol.Event.Type.NetworkInterrupted: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.GGPO_EVENTCODE_CONNECTION_INTERRUPTED,
						connection_interrupted = {
							player = handle, disconnect_timeout = evt.network_interrupted.disconnect_timeout
						}
					};
					
					_callbacks.on_event(ref info);
					break;
				}

				case UdpProtocol.Event.Type.NetworkResumed: {
					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.GGPO_EVENTCODE_CONNECTION_RESUMED,
						connection_resumed = {
							player = handle
						}
					};
					
					_callbacks.on_event(ref info);
					break;
				}
			}
		}

		protected virtual void OnUdpProtocolPeerEvent(ref UdpProtocol.Event evt, int queue) {
			OnUdpProtocolEvent(ref evt, QueueToPlayerHandle(queue));
			switch (evt.type) {
				case UdpProtocol.Event.Type.Input:
					if (!_local_connect_status[queue].disconnected) {
						int current_remote_frame = _local_connect_status[queue].last_frame;
						int new_remote_frame = evt.input.input.frame;
						
						if (current_remote_frame != -1 && new_remote_frame != current_remote_frame + 1) {
							throw new ArgumentException();
						}

						_sync.AddRemoteInput(queue, ref evt.input.input);
						// Notify the other endpoints which frame we received from a peer
						Log($"setting remote connect status for queue {queue} to {evt.input.input.frame}\n");
						_local_connect_status[queue].last_frame = evt.input.input.frame;
					}
					break;

				case UdpProtocol.Event.Type.Disconnected:
					DisconnectPlayer(QueueToPlayerHandle(queue));
					break;
			}
		}

		protected virtual void OnUdpProtocolSpectatorEvent(ref UdpProtocol.Event evt, int queue) {
			GGPOPlayerHandle handle = QueueToSpectatorHandle(queue);
			OnUdpProtocolEvent(ref evt, handle);

			switch (evt.type) {
				case UdpProtocol.Event.Type.Disconnected:
					_spectators[queue].Disconnect();

					GGPOEvent info = new GGPOEvent {
						code = GGPOEventCode.GGPO_EVENTCODE_DISCONNECTED_FROM_PEER,
						disconnected = {
							player = handle
						}
					};
					
					_callbacks.on_event(ref info);

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