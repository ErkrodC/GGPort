/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;

namespace GGPort {
	public class UDPProtocol : IPollSink {
		private const int UDP_HEADER_SIZE = 28; // Size of IP + UDP headers
		private const uint NUM_SYNC_PACKETS = 5;
		private const uint SYNC_RETRY_INTERVAL = 2000;
		private const uint SYNC_FIRST_RETRY_INTERVAL = 500;
		private const int RUNNING_RETRY_INTERVAL = 200;
		private const int KEEP_ALIVE_INTERVAL = 200;
		private const int QUALITY_REPORT_INTERVAL = 1000;
		private const int NETWORK_STATS_INTERVAL = 1000;
		private const int UDP_SHUTDOWN_TIMER = 5000;
		private const int MAX_SEQ_DISTANCE = 1 << 15;
		
		// Network transmission information
		protected UDP _udp;
		protected IPEndPoint _peer_addr;
		protected ushort _magic_number;
		protected int _queue;
		protected ushort _remote_magic_number;
		protected bool _connected;
		protected int _send_latency;
		protected int _oop_percent;
		protected OOPacket _oo_packet;
		protected RingBuffer<QueueEntry> _send_queue = new RingBuffer<QueueEntry>(64);
		
		// Stats
		protected int _round_trip_time;
		protected int _packets_sent;
		protected int _bytes_sent;
		protected int _kbps_sent;
		protected long _stats_start_time;

		// The state machine
		protected UDPMessage.ConnectStatus[] _local_connect_status;
		protected UDPMessage.ConnectStatus[] _peer_connect_status = new UDPMessage.ConnectStatus[UDPMessage.UDP_MSG_MAX_PLAYERS];
		protected State _current_state;
		protected StateUnion _state;

		// Fairness
		protected int _local_frame_advantage;
		protected int _remote_frame_advantage;

		// Packet loss
		protected RingBuffer<GameInput> _pending_output = new RingBuffer<GameInput>(64);
		protected GameInput _last_received_input;
		protected GameInput _last_sent_input;
		protected GameInput _last_acked_input;
		protected long _last_send_time;
		protected long _last_recv_time;
		protected long _shutdown_timeout;
		protected bool _disconnect_event_sent;
		protected uint _disconnect_timeout;
		protected uint _disconnect_notify_start;
		protected bool _disconnect_notify_sent;

		protected ushort _next_send_seq;
		protected ushort _next_recv_seq;

		// Rift synchronization
		protected TimeSync _timesync = new TimeSync();

		// Event queue
		protected RingBuffer<Event> _event_queue = new RingBuffer<Event>(64);
		
		// Message dispatch
		private delegate bool DispatchFn(ref UDPMessage msg, int len);
		private readonly Dictionary<UDPMessage.MsgType, DispatchFn> table;

		public UDPProtocol() {
			_local_frame_advantage = 0;
			_remote_frame_advantage = 0;
			_queue = -1;
			_magic_number = 0;
			_remote_magic_number = 0;
			_packets_sent = 0;
			_bytes_sent = 0;
			_stats_start_time = 0;
			_last_send_time = 0;
			_shutdown_timeout = 0;
			_disconnect_timeout = 0;
			_disconnect_notify_start = 0;
			_disconnect_notify_sent = false;
			_disconnect_event_sent = false;
			_connected = false;
			_next_send_seq = 0;
			_next_recv_seq = 0;
			_udp = null;
			
			_last_sent_input.init(-1, null, 1);
			_last_received_input.init(-1, null, 1);
			_last_acked_input.init(-1, null, 1);

			_state = default;
			
			for (int i = 0; i < _peer_connect_status.Length; i++) {
				_peer_connect_status[i] = default;
				_peer_connect_status[i].LastFrame = -1;
			}

			_peer_addr = default;
			_oo_packet.msg = null;

			_send_latency = Platform.GetConfigInt("ggpo.network.delay");
			_oop_percent = Platform.GetConfigInt("ggpo.oop.percent");
			
			table = new Dictionary<UDPMessage.MsgType, DispatchFn> {
				[UDPMessage.MsgType.Invalid] = OnInvalid,
				[UDPMessage.MsgType.SyncRequest] = OnSyncRequest,
				[UDPMessage.MsgType.SyncReply] = OnSyncReply,
				[UDPMessage.MsgType.Input] = OnInput,
				[UDPMessage.MsgType.QualityReport] = OnQualityReport,
				[UDPMessage.MsgType.QualityReply] = OnQualityReply,
				[UDPMessage.MsgType.KeepAlive] = OnKeepAlive,
				[UDPMessage.MsgType.InputAck] = OnInputAck
			};
		}

		~UDPProtocol() {
			ClearSendQueue();
		}

		// TODO last param is list?
		public void Init(ref UDP udp, ref Poll poll, int queue, IPEndPoint endPoint, UDPMessage.ConnectStatus[] status) {
			_udp = udp;
			_queue = queue;
			_local_connect_status = status;

			_peer_addr = endPoint;

			do {
				_magic_number = (ushort) new Random().Next(0, ushort.MaxValue); // TODO this class should hold a Random type var
			} while (_magic_number == 0);
			
			poll.RegisterLoop(this);
		}

		public void Synchronize() {
			if (_udp != null) {
				_current_state = State.Syncing;
				_state.sync.roundtrips_remaining = NUM_SYNC_PACKETS;
				SendSyncRequest();
			}
		}

		public bool GetPeerConnectStatus(int id, out int frame) {
			frame = _peer_connect_status[id].LastFrame;
			return !_peer_connect_status[id].IsDisconnected;
		}
		
		public bool IsInitialized() { return _udp != null; }
		public bool IsSynchronized() { return _current_state == State.Running; }
		public bool IsRunning() { return _current_state == State.Running; }

		public void SendInput(ref GameInput input) {
			if (_udp == null) { return; }

			if (_current_state == State.Running) {
				// Check to see if this is a good time to adjust for the rift...
				_timesync.advance_frame(ref input, _local_frame_advantage, _remote_frame_advantage);

				/*
				* Save this input packet
				*
				* XXX: This queue may fill up for spectators who do not ack input packets in a timely
				* manner.  When this happens, we can either resize the queue (ug) or disconnect them
				* (better, but still ug).  For the meantime, make this queue really big to decrease
				* the odds of this happening...
				*/
				_pending_output.push(input);
			}
				
			SendPendingOutput();
		}

		public void SendInputAck() {
			UDPMessage msg = new UDPMessage(UDPMessage.MsgType.InputAck);
			msg.inputAck.ackFrame = _last_received_input.frame;
			SendMsg(ref msg);
		}

		public bool HandlesMsg(ref IPEndPoint from, ref UDPMessage msg) {
			if (_udp == null) { return false; }

			return _peer_addr.Equals(from);
		}
		
		public void OnMsg(ref UDPMessage msg, int len) {
			bool handled = false;

			// filter out messages that don't match what we expect
			ushort seq = msg.header.SequenceNumber;
			if (msg.header.type != UDPMessage.MsgType.SyncRequest &&
			    msg.header.type != UDPMessage.MsgType.SyncReply) {
				if (msg.header.magic != _remote_magic_number) {
					LogMsg("recv rejecting", ref msg);
					return;
				}

				// filter out out-of-order packets
				ushort skipped = (ushort)(seq - _next_recv_seq);
				// Log("checking sequence number -> next - seq : %d - %d = %d{Environment.NewLine}", seq, _next_recv_seq, skipped);
				if (skipped > MAX_SEQ_DISTANCE) {
					Log($"dropping out of order packet (seq: {seq}, last seq:{_next_recv_seq}){Environment.NewLine}");
					return;
				}
			}

			_next_recv_seq = seq;
			LogMsg("recv", ref msg);
			if ((byte) msg.header.type >= table.Count) {
				OnInvalid(ref msg, len);
			} else {
				handled = table[msg.header.type](ref msg, len);
			}
			if (handled) {
				_last_recv_time = Platform.GetCurrentTimeMS();
				if (_disconnect_notify_sent && _current_state == State.Running) {
					Event evt = new Event(Event.Type.NetworkResumed);
					QueueEvent(ref evt); // TODO ref is unnecessary?
					_disconnect_notify_sent = false;
				}
			}
		}

		public void Disconnect() {
			_current_state = State.Disconnected;
			_shutdown_timeout = Platform.GetCurrentTimeMS() + UDP_SHUTDOWN_TIMER;
		}

		public void GetNetworkStats(ref NetworkStats stats) { // TODO cleaner to serve stats struct from here? to get out over ref
			stats.network.Ping = _round_trip_time;
			stats.network.SendQueueLength = _pending_output.size();
			stats.network.KbpsSent = _kbps_sent;
			stats.timeSync.RemoteFramesBehind = _remote_frame_advantage;
			stats.timeSync.LocalFramesBehind = _local_frame_advantage;
		}

		public bool GetEvent(out Event e) {
			if (_event_queue.size() == 0) {
				e = default;
				return false;
			}
			
			e = _event_queue.front();
			_event_queue.pop();
			
			return true;
		}

		public void SetLocalFrameNumber(int localFrame) {
			/*
			* Estimate which frame the other guy is one by looking at the
			* last frame they gave us plus some delta for the one-way packet
			* trip time.
			*/
			int remoteFrame = _last_received_input.frame + (_round_trip_time * 60 / 1000);

			/*
			* Our frame advantage is how many frames *behind* the other guy
			* we are.  Counter-intuative, I know.  It's an advantage because
			* it means they'll have to predict more often and our moves will
			* pop more frequenetly.
			*/
			_local_frame_advantage = remoteFrame - localFrame;
		}

		public int RecommendFrameDelay() {
			// XXX: require idle input should be a configuration parameter
			return _timesync.recommend_frame_wait_duration(false);
		}

		public void SetDisconnectTimeout(uint timeout) { // TODO cleanup as Setter?
			_disconnect_timeout = timeout;
		}

		public void SetDisconnectNotifyStart(uint timeout) {
			_disconnect_notify_start = timeout;
		}

		protected void UpdateNetworkStats() {
			long now = Platform.GetCurrentTimeMS();

			if (_stats_start_time == 0) {
				_stats_start_time = now;
			}

			int total_bytes_sent = _bytes_sent + (UDP_HEADER_SIZE * _packets_sent);
			float seconds = (float)((now - _stats_start_time) / 1000.0);
			float Bps = total_bytes_sent / seconds;
			float udp_overhead = (float)(100.0 * (UDP_HEADER_SIZE * _packets_sent) / _bytes_sent);

			_kbps_sent = (int) (Bps / 1024);

			Log($"Network Stats -- Bandwidth: {_kbps_sent:F} KBps   Packets Sent: {_packets_sent:D5} ({(float)_packets_sent * 1000 / (now - _stats_start_time):F} pps)   KB Sent: {total_bytes_sent / 1024.0:F}    UDP Overhead: {udp_overhead:F} %%.{Environment.NewLine}");
		}

		protected void QueueEvent(ref Event evt) {
			LogEvent("Queuing event", evt);
			_event_queue.push(evt);
		}

		protected void ClearSendQueue() {
			while (!_send_queue.empty()) {
				_send_queue.front().msg = default;
				_send_queue.pop();
			}
		}

		// TODO revalidate these log wrappers as they may add some info to the string passed to global log
		protected void Log(string msg) {
			string prefix = $"udpproto{_queue} ";
			LogUtil.Log(prefix + msg);
		}

		protected void LogMsg(string prefix, ref UDPMessage msg) {
			switch (msg.header.type) {
				case UDPMessage.MsgType.SyncRequest:
					Log($"{prefix} sync-request ({msg.syncRequest.randomRequest}).{Environment.NewLine}");
					break;
				case UDPMessage.MsgType.SyncReply:
					Log($"{prefix} sync-reply ({msg.syncReply.randomReply}).{Environment.NewLine}");
					break;
				case UDPMessage.MsgType.QualityReport:
					Log($"{prefix} quality report.{Environment.NewLine}");
					break;
				case UDPMessage.MsgType.QualityReply:
					Log($"{prefix} quality reply.{Environment.NewLine}");
					break;
				case UDPMessage.MsgType.KeepAlive:
					Log($"{prefix} keep alive.{Environment.NewLine}");
					break;
				case UDPMessage.MsgType.Input:
					Log($"{prefix} game-compressed-input {msg.input.startFrame} (+ {msg.input.numBits} bits).{Environment.NewLine}");
					break;
				case UDPMessage.MsgType.InputAck:
					Log($"{prefix} input ack.{Environment.NewLine}");
					break;
				default:
					throw new ArgumentException("Unknown UdpMsg type.");
			}
		}

		protected void LogEvent(string prefix, Event evt) {
			switch (evt.type) {
				case Event.Type.Synchronized:
					Log($"{prefix} (event: Synchronzied).{Environment.NewLine}");
					break;
			}
		}

		protected void SendSyncRequest() {
			if (_state.sync.random == 0) {
				_state.sync.random = (uint) (new Random().Next(0, ushort.MaxValue) & 0xFFFF);
			}
			
			UDPMessage msg = new UDPMessage(UDPMessage.MsgType.SyncRequest);
			msg.syncRequest.randomRequest = _state.sync.random;
			SendMsg(ref msg);
		}

		protected void SendMsg(ref UDPMessage msg) {
			LogMsg("send", ref msg);

			_packets_sent++;
			_last_send_time = Platform.GetCurrentTimeMS();
			_bytes_sent += msg.PacketSize();

			msg.header.magic = _magic_number;
			msg.header.SequenceNumber = _next_send_seq++;

			_send_queue.push(new QueueEntry(Platform.GetCurrentTimeMS(), ref _peer_addr, ref msg));
			PumpSendQueue();
		}

		protected void PumpSendQueue() {
			Random random = new Random(); // TODO pry move to more global scope...maybe?
			while (!_send_queue.empty()) {
				QueueEntry entry = _send_queue.front();

				if (_send_latency != 0) {
					// should really come up with a gaussian distributation based on the configured
					// value, but this will do for now.
					
					int jitter = _send_latency * 2 / 3 + random.Next(0, ushort.MaxValue) % _send_latency / 3; // TODO cleanup rand
					if (Platform.GetCurrentTimeMS() < _send_queue.front().queue_time + jitter) {
						break;
					}
				}
				
				if (_oop_percent != 0 && _oo_packet.msg == null && random.Next(0, ushort.MaxValue) % 100 < _oop_percent) { // TODO cleanup rand
					int delay = random.Next(0, ushort.MaxValue) % (_send_latency * 10 + 1000); // TODO cleanup rand
					Log($"creating rogue oop (seq: {entry.msg.header.SequenceNumber}  delay: {delay}){Environment.NewLine}");
					_oo_packet.send_time = Platform.GetCurrentTimeMS() + delay;
					_oo_packet.msg = entry.msg;
					_oo_packet.dest_addr = entry.dest_addr;
				} else {
					if (entry.dest_addr.Address.Equals(IPAddress.None)) {
						throw new ArgumentException();
					}
					
					BinaryFormatter formatter = new BinaryFormatter();
					using (MemoryStream ms = new MemoryStream(entry.msg.PacketSize())) {
						formatter.Serialize(ms, entry.msg);
						_udp.SendTo(ms.ToArray(), (int) ms.Length, 0, entry.dest_addr);
					} // TODO optimize/refactor

					entry.msg = default;
				}
				
				_send_queue.pop();
			}
			if (_oo_packet.msg != null && _oo_packet.send_time < Platform.GetCurrentTimeMS()) {
				Log("sending rogue oop!");
					
				BinaryFormatter b = new BinaryFormatter();
				using (MemoryStream ms = new MemoryStream((int) _oo_packet.msg?.PacketSize())) {
					b.Serialize(ms, (UDPMessage) _oo_packet.msg); // TODO does this needs to cast from <UdpMsg?> to <UdpMsg> ???
					_udp.SendTo(ms.ToArray(), (int) ms.Length, 0, _oo_packet.dest_addr);
				} // TODO optimize/refactor

				_oo_packet.msg = null; // TODO does this need to be nullable?
			}
		}

		protected unsafe void SendPendingOutput() {
			UDPMessage msg = new UDPMessage(UDPMessage.MsgType.Input);
			int i, j, offset = 0;
			byte[] bits;
			GameInput last;

			if (_pending_output.size() != 0) {
				last = _last_acked_input;
				bits = msg.input.bits;

				msg.input.startFrame = (uint) _pending_output.front().frame;
				msg.input.inputSize = (byte) _pending_output.front().size;

				if (!(last.frame == -1 || last.frame + 1 == msg.input.startFrame)) {
					throw new ArgumentException();
				}
				
				for (j = 0; j < _pending_output.size(); j++) {
					GameInput current = _pending_output.item(j);

					bool currentEqualsLastBits = true;
					for (int k = 0; k < current.size; k++) {
						if (current.bits[k] != last.bits[k]) {
							currentEqualsLastBits = false;
							break;
						}
					}
					
					if (!currentEqualsLastBits) {
						if (GameInput.kMaxBytes * GameInput.kMaxPlayers * 8 >= 1 << BitVector.BITVECTOR_NIBBLE_SIZE) {
							throw new ArgumentException();
						}
						
						for (i = 0; i < current.size * 8; i++) {
							if (i >= 1 << BitVector.BITVECTOR_NIBBLE_SIZE) {
								throw new ArgumentException();
							}
							
							if (current.value(i) != last.value(i)) {
								BitVector.SetBit(msg.input.bits, ref offset);
								if (current.value(i)) {
									BitVector.SetBit(bits, ref offset);
								} else {
									BitVector.ClearBit(bits, ref offset);
								}

								BitVector.WriteNibblet(bits, i, ref offset);
							}
						}
					}

					BitVector.ClearBit(msg.input.bits, ref offset);
					last = _last_sent_input = current;
				}
			} else {
				msg.input.startFrame = 0;
				msg.input.inputSize = 0;
			}
			
			msg.input.ackFrame = _last_received_input.frame;
			msg.input.numBits = (ushort) offset;

			msg.input.disconnectRequested = _current_state == State.Disconnected;
			if (_local_connect_status != null) {
				for (int k = 0; k < msg.input.peerConnectStatus.Length; k++) {
					msg.input.peerConnectStatus[k] = _local_connect_status[k];
				}
			} else {
				for (int k = 0; k < msg.input.peerConnectStatus.Length; k++) {
					msg.input.peerConnectStatus[k] = default;
				}
			}

			if (offset >= UDPMessage.MAX_COMPRESSED_BITS) {
				throw new ArgumentException();
			}

			SendMsg(ref msg);
		}

		protected bool OnInvalid(ref UDPMessage msg, int len) {
			throw new ArgumentException("Invalid msg in UdpProtocol");
			//return false;
		}

		protected bool OnSyncRequest(ref UDPMessage msg, int len) {
			if (_remote_magic_number != 0 && msg.header.magic != _remote_magic_number) {
				Log($"Ignoring sync request from unknown endpoint ({msg.header.magic} != {_remote_magic_number}).{Environment.NewLine}");
				
				return false;
			}
			
			UDPMessage reply = new UDPMessage(UDPMessage.MsgType.SyncReply);
			reply.syncReply.randomReply = msg.syncRequest.randomRequest;
			SendMsg(ref reply);
			
			return true;
		}

		protected bool OnSyncReply(ref UDPMessage msg, int len) {
			if (_current_state != State.Syncing) {
				Log($"Ignoring SyncReply while not syncing.{Environment.NewLine}");
				
				return msg.header.magic == _remote_magic_number;
			}

			if (msg.syncReply.randomReply != _state.sync.random) {
				Log($"sync reply {msg.syncReply.randomReply} != {_state.sync.random}.  Keep looking...{Environment.NewLine}");
				
				return false;
			}

			if (!_connected) {
				Event evt = new Event(Event.Type.Connected);
				QueueEvent(ref evt); // TODO ref is unnecessary?
				_connected = true;
			}

			Log($"Checking sync state ({_state.sync.roundtrips_remaining} round trips remaining).{Environment.NewLine}");
			if (--_state.sync.roundtrips_remaining == 0) {
				Log($"Synchronized!{Environment.NewLine}");

				Event evt = new Event(Event.Type.Synchronized);
				QueueEvent(ref evt); // TODO ref is unnecessary?
				_current_state = State.Running;
				_last_received_input.frame = -1;
				_remote_magic_number = msg.header.magic;
			} else {
				Event evt = new Event(Event.Type.Synchronizing) {
					synchronizing = {
						total = (int) NUM_SYNC_PACKETS,
						count = (int) (NUM_SYNC_PACKETS - _state.sync.roundtrips_remaining)
					}
				};
				
				QueueEvent(ref evt);
				SendSyncRequest();
			}
			return true;
		}

		protected bool OnInput(ref UDPMessage msg, int len) {
			// If a disconnect is requested, go ahead and disconnect now.
			bool disconnect_requested = msg.input.disconnectRequested;
			if (disconnect_requested) {
				if (_current_state != State.Disconnected && !_disconnect_event_sent) {
					Log($"Disconnecting endpoint on remote request.{Environment.NewLine}");

					Event evt = new Event(Event.Type.Disconnected);
					QueueEvent(ref evt); // TODO ref is unnecessary?
					_disconnect_event_sent = true;
				}
			} else {
				// Update the peer connection status if this peer is still considered to be part of the network.
				UDPMessage.ConnectStatus[] remote_status = msg.input.peerConnectStatus;
				for (int i = 0; i < _peer_connect_status.Length; i++) {
					if (remote_status[i].LastFrame < _peer_connect_status[i].LastFrame) {
						throw new ArgumentException();
					}
					
					_peer_connect_status[i].IsDisconnected = _peer_connect_status[i].IsDisconnected || remote_status[i].IsDisconnected;
					_peer_connect_status[i].LastFrame = Math.Max(
						_peer_connect_status[i].LastFrame,
						remote_status[i].LastFrame
					);
				}
			}

			/*
			* Decompress the input.
			*/
			int last_received_frame_number = _last_received_input.frame;
			if (msg.input.numBits != 0) {
				int offset = 0;
				byte[] bits = msg.input.bits;
				int numBits = msg.input.numBits;
				int currentFrame = (int) msg.input.startFrame; // TODO ecgh

				_last_received_input.size = msg.input.inputSize;
				if (_last_received_input.frame < 0) {
					_last_received_input.frame = (int) (msg.input.startFrame - 1); // TODO ecgh
				}

				while (offset < numBits) {
					/*
					* Keep walking through the frames (parsing bits) until we reach
					* the inputs for the frame right after the one we're on.
					*/
					if (currentFrame > _last_received_input.frame + 1) {
						throw new ArgumentException();
					}
					
					bool useInputs = currentFrame == _last_received_input.frame + 1;

					while (BitVector.ReadBit(bits, ref offset) != 0) {
						int on = BitVector.ReadBit(bits, ref offset);
						int button = BitVector.ReadNibblet(bits, ref offset);
						if (useInputs) {
							if (on != 0) {
								_last_received_input.set(button);
							} else {
								_last_received_input.clear(button);
							}
						}
					}

					if (offset > numBits) {
						throw new ArgumentException();
					}

					/*
					* Now if we want to use these inputs, go ahead and send them to
					* the emulator.
					*/
					if (useInputs) {
						/*
						* Move forward 1 frame in the stream.
						*/
						if (currentFrame != _last_received_input.frame + 1) {
							throw new ArgumentException();
						}
						
						_last_received_input.frame = currentFrame;

						/*
						* Send the event to the emualtor
						*/
						Event evt = new Event(Event.Type.Input) {
							input = {
								input = _last_received_input
							}
						};

						string desc = _last_received_input.desc();
						_state.running.last_input_packet_recv_time = Platform.GetCurrentTimeMS();

						Log($"Sending frame {_last_received_input.frame} to emu queue {_queue} ({desc}).{Environment.NewLine}");
						QueueEvent(ref evt);
					} else {
						Log($"Skipping past frame:({currentFrame}) current is {_last_received_input.frame}.{Environment.NewLine}");
					}

					/*
					* Move forward 1 frame in the input stream.
					*/
					currentFrame++;
				}
			}

			if (_last_received_input.frame < last_received_frame_number) {
				throw new ArgumentException();
			}

			/*
			* Get rid of our buffered input
			*/
			while (_pending_output.size() != 0 && _pending_output.front().frame < msg.input.ackFrame) {
				Log($"Throwing away pending output frame {_pending_output.front().frame}{Environment.NewLine}");
				_last_acked_input = _pending_output.front();
				_pending_output.pop();
			}

			return true;
		}

		protected bool OnInputAck(ref UDPMessage msg, int len) {
			/*
			* Get rid of our buffered input
			*/
			while (_pending_output.size() != 0 && _pending_output.front().frame < msg.inputAck.ackFrame) {
				Log($"Throwing away pending output frame {_pending_output.front().frame}{Environment.NewLine}");
				_last_acked_input = _pending_output.front();
				_pending_output.pop();
			}
			
			return true;
		}

		protected bool OnQualityReport(ref UDPMessage msg, int len) {
			// send a reply so the other side can compute the round trip transmit time.
			UDPMessage reply = new UDPMessage(UDPMessage.MsgType.QualityReply);
			reply.qualityReply.pong = msg.qualityReport.ping;
			SendMsg(ref reply);

			_remote_frame_advantage = msg.qualityReport.frameAdvantage;
			
			return true;
		}

		protected bool OnQualityReply(ref UDPMessage msg, int len) {
			_round_trip_time = (int) (Platform.GetCurrentTimeMS() - msg.qualityReply.pong);
			
			return true;
		}

		protected bool OnKeepAlive(ref UDPMessage msg, int len) {
			return true;
		}
		
		public virtual bool OnHandlePoll(object TODO) { return true; }
		public virtual bool OnMsgPoll(object TODO) { return true; }
		public virtual bool OnPeriodicPoll(object TODO0, long TODO1) { return true; }
		public virtual bool OnLoopPoll(object cookie) {
			if (_udp == null) {
			   return true;
			}

			long now = Platform.GetCurrentTimeMS();
			uint next_interval;

			PumpSendQueue();
			switch (_current_state) {
				case State.Syncing:
					next_interval = (_state.sync.roundtrips_remaining == NUM_SYNC_PACKETS) ? SYNC_FIRST_RETRY_INTERVAL : SYNC_RETRY_INTERVAL;
					if (_last_send_time != 0 && _last_send_time + next_interval < now) {
					   Log($"No luck syncing after {next_interval} ms... Re-queueing sync packet.{Environment.NewLine}");
					   SendSyncRequest();
					}
					break;

				case State.Running:
					// xxx: rig all this up with a timer wrapper
					if (_state.running.last_input_packet_recv_time == 0 || _state.running.last_input_packet_recv_time + RUNNING_RETRY_INTERVAL < now) {
					   Log($"Haven't exchanged packets in a while (last received:{_last_received_input.frame}  last sent:{_last_sent_input.frame}).  Resending.{Environment.NewLine}");
					   SendPendingOutput();
					   _state.running.last_input_packet_recv_time = now;
					}

					if (_state.running.last_quality_report_time == 0 || _state.running.last_quality_report_time + QUALITY_REPORT_INTERVAL < now) {
					   UDPMessage msg = new UDPMessage(UDPMessage.MsgType.QualityReport);
					   msg.qualityReport.ping = Platform.GetCurrentTimeMS();
					   msg.qualityReport.frameAdvantage = (sbyte) _local_frame_advantage;
					   SendMsg(ref msg);
					   _state.running.last_quality_report_time = now;
					}

					if (_state.running.last_network_stats_interval == 0 || _state.running.last_network_stats_interval + NETWORK_STATS_INTERVAL < now) {
					   UpdateNetworkStats();
					   _state.running.last_network_stats_interval = now;
					}

					if (_last_send_time != 0 && _last_send_time + KEEP_ALIVE_INTERVAL < now) {
					   Log($"Sending keep alive packet{Environment.NewLine}");

					   UDPMessage udpMsg = new UDPMessage(UDPMessage.MsgType.KeepAlive);
					   SendMsg(ref udpMsg); // TODO ref is unnecessary?
					}

					if (_disconnect_timeout != 0 && _disconnect_notify_start != 0 && 
					   !_disconnect_notify_sent && (_last_recv_time + _disconnect_notify_start < now)) {
						
					   Log($"Endpoint has stopped receiving packets for {_disconnect_notify_start} ms.  Sending notification.{Environment.NewLine}");
					   Event e = new Event(Event.Type.NetworkInterrupted) {
						   network_interrupted = {
							   disconnect_timeout = (int) (_disconnect_timeout - _disconnect_notify_start)
						   }
					   };
					   
					   QueueEvent(ref e);
					   _disconnect_notify_sent = true;
					}

					if (_disconnect_timeout != 0 && (_last_recv_time + _disconnect_timeout < now)) {
					   if (!_disconnect_event_sent) {
					      Log($"Endpoint has stopped receiving packets for {_disconnect_timeout} ms.  Disconnecting.{Environment.NewLine}");
					      
					      Event evt = new Event(Event.Type.Disconnected);
					      QueueEvent(ref evt); // TODO ref is unnecessary?
					      _disconnect_event_sent = true;
					   }
					}
					break;

				case State.Disconnected:
				   if (_shutdown_timeout < now) {
				      Log($"Shutting down udp connection.{Environment.NewLine}");
				      _udp = null;
				      _shutdown_timeout = 0;
				   }
				   
				   break;
			}

			return true;
		}
		
		public struct Stats {
			public readonly int ping;
			public readonly int remote_frame_advantage;
			public readonly int local_frame_advantage;
			public readonly int send_queue_len;
			public readonly UDP.Stats udp;
		}

		[StructLayout(LayoutKind.Explicit)]
		public struct Event {
			[FieldOffset(0)] public readonly Type type;
			[FieldOffset(4)] public Input input;
			[FieldOffset(4)] public Synchronizing synchronizing;
			[FieldOffset(4)] public NetworkInterrupted network_interrupted;

			public struct Input {
				public GameInput input;
			}

			public struct Synchronizing {
				public int total { get; set; }
				public int count { get; set; }
			}

			public struct NetworkInterrupted {
				public int disconnect_timeout { get; set; }
			}

			public Event(Type t = Type.Unknown) : this() {
				type = t;
			}

			public enum Type {
				Unknown = -1,
				Connected,
				Synchronizing,
				Synchronized,
				Input,
				Disconnected,
				NetworkInterrupted,
				NetworkResumed,
			}
		}

		protected struct OOPacket {
			public long send_time { get; set; }
			public IPEndPoint dest_addr { get; set; }
			public UDPMessage? msg { get; set; }
		}
		
		protected enum State {
			Syncing,
			Synchronized,
			Running,
			Disconnected
		}

		protected struct QueueEntry {
			public readonly long queue_time;
			public readonly IPEndPoint dest_addr;
			public UDPMessage msg;

			public QueueEntry(long time, ref IPEndPoint dst, ref UDPMessage m) : this() {
				queue_time = time;
				dest_addr = dst;
				msg = m;
			}
		}

		[StructLayout(LayoutKind.Explicit)]
		public struct StateUnion {
			[FieldOffset(0)] public Sync sync;
			[FieldOffset(0)] public Running running;

			public struct Sync {
				public uint roundtrips_remaining { get; set; }
				public uint random { get; set; }
			}

			public struct Running {
				public long last_quality_report_time { get; set; }
				public long last_network_stats_interval { get; set; }
				public long last_input_packet_recv_time { get; set; }
			}
		}
	}
}