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
		private const int kUDPHeaderSize = 28; // Size of IP + UDP headers
		private const uint kNumSyncPackets = 5;
		private const uint kSyncRetryInterval = 2000;
		private const uint kSyncFirstRetryInterval = 500;
		private const int kRunningRetryInterval = 200;
		private const int kKeepAliveInterval = 200;
		private const int kQualityReportInterval = 1000;
		private const int kNetworkStatsInterval = 1000;
		private const int kUDPShutdownTimer = 5000;
		private const int kMaxSeqDistance = 1 << 15;
		
		// Network transmission information
		protected UDP udp;
		protected IPEndPoint peerAddress;
		protected ushort magicNumber;
		protected int queue;
		protected ushort remoteMagicNumber;
		protected bool connected;
		protected int sendLatency;
		protected int oopPercent;
		protected OOPacket ooPacket;
		protected CircularQueue<QueueEntry> sendQueue = new CircularQueue<QueueEntry>(64);
		
		// Stats
		protected int roundTripTime;
		protected int packetsSent;
		protected int bytesSent;
		protected int kbpsSent;
		protected long statsStartTime;

		// The state machine // TODO plural naming?
		protected UDPMessage.ConnectStatus[] localConnectStatus;
		protected UDPMessage.ConnectStatus[] peerConnectStatus = new UDPMessage.ConnectStatus[UDPMessage.UDP_MSG_MAX_PLAYERS];
		protected State currentState;
		protected StateUnion state;

		// Fairness
		protected int localFrameAdvantage;
		protected int remoteFrameAdvantage;

		// Packet loss
		protected CircularQueue<GameInput> pendingOutgoingInputs = new CircularQueue<GameInput>(64);
		protected GameInput lastReceivedInput;
		protected GameInput lastSentInput;
		protected GameInput lastAckedInput;
		protected long lastSendTime;
		protected long lastReceiveTime;
		protected long shutdownTimeout;
		protected bool disconnectEventSent;
		protected uint disconnectTimeout;
		protected uint disconnectNotifyStart;
		protected bool disconnectNotifySent;

		protected ushort nextSendSequenceNumber;
		protected ushort nextReceiveSequenceNumber;

		// Rift synchronization
		protected TimeSync timeSync = new TimeSync();

		// Event queue
		protected CircularQueue<Event> eventQueue = new CircularQueue<Event>(64);
		
		// Message dispatch
		private delegate bool DispatchFn(ref UDPMessage msg, int len);
		private readonly Dictionary<UDPMessage.MsgType, DispatchFn> table;

		public UDPProtocol() {
			localFrameAdvantage = 0;
			remoteFrameAdvantage = 0;
			queue = -1;
			magicNumber = 0;
			remoteMagicNumber = 0;
			packetsSent = 0;
			bytesSent = 0;
			statsStartTime = 0;
			lastSendTime = 0;
			shutdownTimeout = 0;
			disconnectTimeout = 0;
			disconnectNotifyStart = 0;
			disconnectNotifySent = false;
			disconnectEventSent = false;
			connected = false;
			nextSendSequenceNumber = 0;
			nextReceiveSequenceNumber = 0;
			udp = null;
			
			lastSentInput.Init(-1, null, 1);
			lastReceivedInput.Init(-1, null, 1);
			lastAckedInput.Init(-1, null, 1);

			state = default;
			
			for (int i = 0; i < peerConnectStatus.Length; i++) {
				peerConnectStatus[i] = default;
				peerConnectStatus[i].LastFrame = -1;
			}

			peerAddress = default;
			ooPacket.msg = null;

			sendLatency = Platform.GetConfigInt("ggpo.network.delay");
			oopPercent = Platform.GetConfigInt("ggpo.oop.percent");
			
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
			this.udp = udp;
			this.queue = queue;
			localConnectStatus = status;

			peerAddress = endPoint;

			do {
				magicNumber = (ushort) new Random().Next(0, ushort.MaxValue); // TODO this class should hold a Random type var
			} while (magicNumber == 0);
			
			poll.RegisterLoop(this);
		}

		public void Synchronize() {
			if (udp != null) {
				currentState = State.Syncing;
				state.sync.roundtrips_remaining = kNumSyncPackets;
				SendSyncRequest();
			}
		}

		public bool GetPeerConnectStatus(int id, out int frame) {
			frame = peerConnectStatus[id].LastFrame;
			return !peerConnectStatus[id].IsDisconnected;
		}
		
		public bool IsInitialized() { return udp != null; }
		public bool IsSynchronized() { return currentState == State.Running; }
		public bool IsRunning() { return currentState == State.Running; }

		public void SendInput(ref GameInput input) {
			if (udp == null) { return; }

			if (currentState == State.Running) {
				// Check to see if this is a good time to adjust for the rift...
				timeSync.advance_frame(ref input, localFrameAdvantage, remoteFrameAdvantage);

				/*
				* Save this input packet
				*
				* XXX: This queue may fill up for spectators who do not ack input packets in a timely
				* manner.  When this happens, we can either resize the queue (ug) or disconnect them
				* (better, but still ug).  For the meantime, make this queue really big to decrease
				* the odds of this happening...
				*/
				pendingOutgoingInputs.Push(input);
			}
				
			SendPendingOutput();
		}

		public void SendInputAck() {
			UDPMessage msg = new UDPMessage(UDPMessage.MsgType.InputAck);
			msg.inputAck.ackFrame = lastReceivedInput.Frame;
			SendMsg(ref msg);
		}

		public bool HandlesMsg(ref IPEndPoint from, ref UDPMessage msg) {
			if (udp == null) { return false; }

			return peerAddress.Equals(from);
		}
		
		public void OnMsg(ref UDPMessage msg, int len) {
			bool handled = false;

			// filter out messages that don't match what we expect
			ushort seq = msg.header.SequenceNumber;
			if (msg.header.type != UDPMessage.MsgType.SyncRequest &&
			    msg.header.type != UDPMessage.MsgType.SyncReply) {
				if (msg.header.magic != remoteMagicNumber) {
					LogMsg("recv rejecting", ref msg);
					return;
				}

				// filter out out-of-order packets
				ushort skipped = (ushort)(seq - nextReceiveSequenceNumber);
				// Log("checking sequence number -> next - seq : %d - %d = %d{Environment.NewLine}", seq, _next_recv_seq, skipped);
				if (skipped > kMaxSeqDistance) {
					Log($"dropping out of order packet (seq: {seq}, last seq:{nextReceiveSequenceNumber}){Environment.NewLine}");
					return;
				}
			}

			nextReceiveSequenceNumber = seq;
			LogMsg("recv", ref msg);
			if ((byte) msg.header.type >= table.Count) {
				OnInvalid(ref msg, len);
			} else {
				handled = table[msg.header.type](ref msg, len);
			}
			if (handled) {
				lastReceiveTime = Platform.GetCurrentTimeMS();
				if (disconnectNotifySent && currentState == State.Running) {
					Event evt = new Event(Event.Type.NetworkResumed);
					QueueEvent(ref evt); // TODO ref is unnecessary?
					disconnectNotifySent = false;
				}
			}
		}

		public void Disconnect() {
			currentState = State.Disconnected;
			shutdownTimeout = Platform.GetCurrentTimeMS() + kUDPShutdownTimer;
		}

		public void GetNetworkStats(ref NetworkStats stats) { // TODO cleaner to serve stats struct from here? to get out over ref
			stats.network.Ping = roundTripTime;
			stats.network.SendQueueLength = pendingOutgoingInputs.Count;
			stats.network.KbpsSent = kbpsSent;
			stats.timeSync.RemoteFramesBehind = remoteFrameAdvantage;
			stats.timeSync.LocalFramesBehind = localFrameAdvantage;
		}

		public bool GetEvent(out Event e) {
			if (eventQueue.Count == 0) {
				e = default;
				return false;
			}

			e = eventQueue.Pop();
			
			return true;
		}

		public void SetLocalFrameNumber(int localFrame) {
			/*
			* Estimate which frame the other guy is one by looking at the
			* last frame they gave us plus some delta for the one-way packet
			* trip time.
			*/
			int remoteFrame = lastReceivedInput.Frame + (roundTripTime * 60 / 1000);

			/*
			* Our frame advantage is how many frames *behind* the other guy
			* we are.  Counter-intuative, I know.  It's an advantage because
			* it means they'll have to predict more often and our moves will
			* pop more frequenetly.
			*/
			localFrameAdvantage = remoteFrame - localFrame;
		}

		public int RecommendFrameDelay() {
			// XXX: require idle input should be a configuration parameter
			return timeSync.recommend_frame_wait_duration(false);
		}

		public void SetDisconnectTimeout(uint timeout) { // TODO cleanup as Setter?
			disconnectTimeout = timeout;
		}

		public void SetDisconnectNotifyStart(uint timeout) {
			disconnectNotifyStart = timeout;
		}

		protected void UpdateNetworkStats() {
			long now = Platform.GetCurrentTimeMS();

			if (statsStartTime == 0) {
				statsStartTime = now;
			}

			int totalBytesSent = bytesSent + (kUDPHeaderSize * packetsSent);
			float seconds = (float)((now - statsStartTime) / 1000.0);
			float bytesPerSecond = totalBytesSent / seconds;
			float udpOverhead = (float)(100.0 * (kUDPHeaderSize * packetsSent) / bytesSent);

			kbpsSent = (int) (bytesPerSecond / 1024);

			Log(
				$"Network Stats -- "
				+ $"Bandwidth: {kbpsSent:F} KBps   "
				+ $"Packets Sent: {packetsSent:D5} ({(float) packetsSent * 1000 / (now - statsStartTime):F} pps)   "
				+ $"KB Sent: {totalBytesSent / 1024.0:F}    "
				+ $"UDP Overhead: {udpOverhead:F} %%.{Environment.NewLine}"
			);
		}

		protected void QueueEvent(ref Event evt) {
			LogEvent("Queuing event", evt);
			eventQueue.Push(evt);
		}

		protected void ClearSendQueue() {
			while (sendQueue.Count > 0) {
				sendQueue.Peek().msg = default;
				sendQueue.Pop();
			}
		}

		// TODO revalidate these log wrappers as they may add some info to the string passed to global log
		protected void Log(string msg) {
			string prefix = $"udpproto{queue} ";
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
					Platform.AssertFailed($"Unknown {nameof(UDPMessage)} type.");
					break;
			}
		}

		protected void LogEvent(string prefix, Event evt) {
			switch (evt.type) {
				case Event.Type.Synchronized:
					Log($"{prefix} (event: {nameof(Event.Type.Synchronized)}).{Environment.NewLine}");
					break;
			}
		}

		protected void SendSyncRequest() {
			if (state.sync.random == 0) {
				state.sync.random = (uint) (new Random().Next(0, ushort.MaxValue) & 0xFFFF);
			}

			UDPMessage msg = new UDPMessage(UDPMessage.MsgType.SyncRequest) {
				syncRequest = {
					randomRequest = state.sync.random
				}
			};
			
			SendMsg(ref msg);
		}

		protected void SendMsg(ref UDPMessage msg) {
			LogMsg("send", ref msg);

			packetsSent++;
			lastSendTime = Platform.GetCurrentTimeMS();

			msg.header.magic = magicNumber;
			msg.header.SequenceNumber = nextSendSequenceNumber++;

			sendQueue.Push(new QueueEntry(Platform.GetCurrentTimeMS(), ref peerAddress, ref msg));
			PumpSendQueue();
		}

		protected void PumpSendQueue() {
			Random random = new Random(); // TODO pry move to more global scope...maybe?
			while (sendQueue.Count > 0 ) {
				QueueEntry entry = sendQueue.Peek();

				if (sendLatency != 0) {
					// should really come up with a gaussian distributation based on the configured
					// value, but this will do for now.
					
					int jitter = sendLatency * 2 / 3 + random.Next(0, ushort.MaxValue) % sendLatency / 3; // TODO cleanup rand
					if (Platform.GetCurrentTimeMS() < sendQueue.Peek().queue_time + jitter) {
						break;
					}
				}
				
				if (oopPercent != 0 && ooPacket.msg == null && random.Next(0, ushort.MaxValue) % 100 < oopPercent) { // TODO cleanup rand
					int delay = random.Next(0, ushort.MaxValue) % (sendLatency * 10 + 1000); // TODO cleanup rand
					Log($"creating rogue oop (seq: {entry.msg.header.SequenceNumber}  delay: {delay}){Environment.NewLine}");
					ooPacket.send_time = Platform.GetCurrentTimeMS() + delay;
					ooPacket.msg = entry.msg;
					ooPacket.dest_addr = entry.dest_addr;
				} else {
					Platform.Assert(!entry.dest_addr.Address.Equals(IPAddress.None));
					
					BinaryFormatter formatter = new BinaryFormatter(); // LOH relates to here as well
					using (MemoryStream ms = new MemoryStream()) {
						formatter.Serialize(ms, entry.msg);
						bytesSent += udp.SendTo(ms.ToArray(), (int) ms.Length, 0, entry.dest_addr);
					} // TODO optimize/refactor

					entry.msg = default;
				}
				
				sendQueue.Pop();
			}
			if (ooPacket.msg != null && ooPacket.send_time < Platform.GetCurrentTimeMS()) {
				Log("sending rogue oop!");
					
				BinaryFormatter formatter = new BinaryFormatter();
				using (MemoryStream ms = new MemoryStream()) {
					formatter.Serialize(ms, (UDPMessage) ooPacket.msg); // TODO does this needs to cast from <UdpMsg?> to <UdpMsg> ???
					bytesSent += udp.SendTo(ms.ToArray(), (int) ms.Length, 0, ooPacket.dest_addr);
				} // TODO optimize/refactor

				ooPacket.msg = null; // TODO does this need to be nullable?
			}
		}

		// LOH problem with sending a UDPMessage >4096 bytes originates here
		// could possibly have to do with either input serialization and/or msg.input structure
		protected unsafe void SendPendingOutput() {
			UDPMessage msg = new UDPMessage(UDPMessage.MsgType.Input);
			int offset = 0;

			if (pendingOutgoingInputs.Count != 0) {
				GameInput outputQueueFront = pendingOutgoingInputs.Peek();
				msg.input.startFrame = (uint) outputQueueFront.Frame;
				msg.input.inputSize = (byte) outputQueueFront.Size;

				Platform.Assert(lastAckedInput.Frame == -1 || lastAckedInput.Frame + 1 == msg.input.startFrame);
				
				foreach (GameInput pendingInput in pendingOutgoingInputs) {
					bool currentEqualsLastBits = true;
					for (int j = 0; j < pendingInput.Size; j++) {
						if (pendingInput.Bits[j] != lastAckedInput.Bits[j]) {
							currentEqualsLastBits = false;
							break;
						}
					}
					
					if (!currentEqualsLastBits) {
						Platform.Assert(GameInput.kMaxBytes * GameInput.kMaxPlayers * 8 < 1 << BitVector.kBitVectorNibbleSize);

						for (int bitIndex = 0; bitIndex < pendingInput.Size * 8; bitIndex++) {
							Platform.Assert(bitIndex < 1 << BitVector.kBitVectorNibbleSize);
							bool pendingInputBit = pendingInput[bitIndex];
							bool lastAckedInputBit = lastAckedInput[bitIndex];
							
							if (pendingInputBit != lastAckedInputBit) {
								BitVector.WriteBit(msg.input.bits, ref offset, true);
								BitVector.WriteBit(msg.input.bits, ref offset, pendingInputBit);
								BitVector.WriteNibblet(msg.input.bits, bitIndex, ref offset);
							}
						}
					}

					BitVector.WriteBit(msg.input.bits, ref offset, false);
					lastAckedInput = lastSentInput = pendingInput;
				}
			} else {
				msg.input.startFrame = 0;
				msg.input.inputSize = 0;
			}
			
			msg.input.ackFrame = lastReceivedInput.Frame;
			msg.input.numBits = (ushort) offset;

			msg.input.disconnectRequested = currentState == State.Disconnected;
			if (localConnectStatus != null) {
				for (int k = 0; k < msg.input.peerConnectStatus.Length; k++) {
					msg.input.peerConnectStatus[k] = localConnectStatus[k];
				}
			} else {
				for (int k = 0; k < msg.input.peerConnectStatus.Length; k++) {
					msg.input.peerConnectStatus[k] = default;
				}
			}

			Platform.Assert(offset < UDPMessage.MAX_COMPRESSED_BITS);

			SendMsg(ref msg);
		}

		protected bool OnInvalid(ref UDPMessage msg, int len) {
			Platform.AssertFailed($"Invalid {nameof(msg)} in {nameof(UDPProtocol)}.");
			return false;
		}

		protected bool OnSyncRequest(ref UDPMessage msg, int len) {
			if (remoteMagicNumber != 0 && msg.header.magic != remoteMagicNumber) {
				Log($"Ignoring sync request from unknown endpoint ({msg.header.magic} != {remoteMagicNumber}).{Environment.NewLine}");
				
				return false;
			}

			UDPMessage reply = new UDPMessage(UDPMessage.MsgType.SyncReply) {
				syncReply = {
					randomReply = msg.syncRequest.randomRequest
				}
			};
			
			SendMsg(ref reply);
			
			return true;
		}

		protected bool OnSyncReply(ref UDPMessage msg, int len) {
			if (currentState != State.Syncing) {
				Log($"Ignoring SyncReply while not syncing.{Environment.NewLine}");
				
				return msg.header.magic == remoteMagicNumber;
			}

			if (msg.syncReply.randomReply != state.sync.random) {
				Log($"sync reply {msg.syncReply.randomReply} != {state.sync.random}.  Keep looking...{Environment.NewLine}");
				
				return false;
			}

			if (!connected) {
				Event evt = new Event(Event.Type.Connected);
				QueueEvent(ref evt); // TODO ref is unnecessary?
				connected = true;
			}

			Log($"Checking sync state ({state.sync.roundtrips_remaining} round trips remaining).{Environment.NewLine}");
			if (--state.sync.roundtrips_remaining == 0) {
				Log($"Synchronized!{Environment.NewLine}");

				Event evt = new Event(Event.Type.Synchronized);
				QueueEvent(ref evt); // TODO ref is unnecessary?
				currentState = State.Running;
				lastReceivedInput.Frame = -1;
				remoteMagicNumber = msg.header.magic;
			} else {
				Event evt = new Event(Event.Type.Synchronizing) {
					synchronizing = {
						total = (int) kNumSyncPackets,
						count = (int) (kNumSyncPackets - state.sync.roundtrips_remaining)
					}
				};
				
				QueueEvent(ref evt);
				SendSyncRequest();
			}
			return true;
		}

		protected bool OnInput(ref UDPMessage incomingMsg, int len) {
			// If a disconnect is requested, go ahead and disconnect now.
			bool disconnectRequested = incomingMsg.input.disconnectRequested;
			if (disconnectRequested) {
				if (currentState != State.Disconnected && !disconnectEventSent) {
					Log($"Disconnecting endpoint on remote request.{Environment.NewLine}");

					Event evt = new Event(Event.Type.Disconnected);
					QueueEvent(ref evt); // TODO ref is unnecessary?
					disconnectEventSent = true;
				}
			} else {
				// Update the peer connection status if this peer is still considered to be part of the network.
				UDPMessage.ConnectStatus[] remoteStatus = incomingMsg.input.peerConnectStatus;
				for (int i = 0; i < peerConnectStatus.Length; i++) {
					Platform.Assert(remoteStatus[i].LastFrame >= peerConnectStatus[i].LastFrame);
					
					peerConnectStatus[i].IsDisconnected = peerConnectStatus[i].IsDisconnected || remoteStatus[i].IsDisconnected;
					peerConnectStatus[i].LastFrame = Math.Max(
						peerConnectStatus[i].LastFrame,
						remoteStatus[i].LastFrame
					);
				}
			}

			// Decompress the input.
			int lastReceivedFrameNumber = lastReceivedInput.Frame;
			if (incomingMsg.input.numBits != 0) {
				int offset = 0;
				byte[] incomingBits = incomingMsg.input.bits;
				int numBits = incomingMsg.input.numBits;
				int currentFrame = (int) incomingMsg.input.startFrame; // TODO ecgh

				lastReceivedInput.Size = incomingMsg.input.inputSize;
				if (lastReceivedInput.Frame < 0) {
					lastReceivedInput.Frame = (int) (incomingMsg.input.startFrame - 1); // TODO ecgh
				}

				while (offset < numBits) {
					/*
					* Keep walking through the frames (parsing bits) until we reach
					* the inputs for the frame right after the one we're on.
					*/
					Platform.Assert(currentFrame <= lastReceivedInput.Frame + 1);
					
					bool useInputs = currentFrame == lastReceivedInput.Frame + 1;

					while (BitVector.ReadBit(incomingBits, ref offset) != 0) {
						if (useInputs) {
							int buttonBitIndex = BitVector.ReadNibblet(incomingBits, ref offset);
							bool isOn = BitVector.ReadBit(incomingBits, ref offset) != 0;
							lastReceivedInput[buttonBitIndex] = isOn;
						}
					}

					Platform.Assert(offset <= numBits);

					// Now if we want to use these inputs, go ahead and send them to the emulator.
					if (useInputs) {
						// Move forward 1 frame in the stream.
						Platform.Assert(currentFrame == lastReceivedInput.Frame + 1);
						
						lastReceivedInput.Frame = currentFrame;

						// Send the event to the emulator
						Event evt = new Event(Event.Type.Input) {
							input = {
								input = lastReceivedInput
							}
						};

						string desc = lastReceivedInput.Desc();
						state.running.last_input_packet_recv_time = Platform.GetCurrentTimeMS();

						Log($"Sending frame {lastReceivedInput.Frame} to emu queue {queue} ({desc}).{Environment.NewLine}");
						QueueEvent(ref evt);
					} else {
						Log($"Skipping past frame:({currentFrame}) current is {lastReceivedInput.Frame}.{Environment.NewLine}");
					}

					// Move forward 1 frame in the input stream.
					currentFrame++;
				}
			}

			Platform.Assert(lastReceivedInput.Frame >= lastReceivedFrameNumber);

			// Get rid of our buffered input
			while (pendingOutgoingInputs.Count != 0 && pendingOutgoingInputs.Peek().Frame < incomingMsg.input.ackFrame) {
				Log($"Throwing away pending output frame {pendingOutgoingInputs.Peek().Frame}{Environment.NewLine}");
				lastAckedInput = pendingOutgoingInputs.Pop();
			}

			return true;
		}

		protected bool OnInputAck(ref UDPMessage msg, int len) {
			// Get rid of our buffered input
			while (pendingOutgoingInputs.Count != 0 && pendingOutgoingInputs.Peek().Frame < msg.inputAck.ackFrame) {
				Log($"Throwing away pending output frame {pendingOutgoingInputs.Peek().Frame}{Environment.NewLine}");
				lastAckedInput = pendingOutgoingInputs.Pop();
			}
			
			return true;
		}

		protected bool OnQualityReport(ref UDPMessage msg, int len) {
			// send a reply so the other side can compute the round trip transmit time.
			UDPMessage reply = new UDPMessage(UDPMessage.MsgType.QualityReply) {
				qualityReply = {
					pong = msg.qualityReport.ping
				}
			};
			
			SendMsg(ref reply);

			remoteFrameAdvantage = msg.qualityReport.frameAdvantage;
			
			return true;
		}

		protected bool OnQualityReply(ref UDPMessage msg, int len) {
			roundTripTime = (int) (Platform.GetCurrentTimeMS() - msg.qualityReply.pong);
			
			return true;
		}

		protected bool OnKeepAlive(ref UDPMessage msg, int len) {
			return true;
		}
		
		public virtual bool OnHandlePoll(object TODO) { return true; }
		public virtual bool OnMsgPoll(object TODO) { return true; }
		public virtual bool OnPeriodicPoll(object TODO0, long TODO1) { return true; }
		public virtual bool OnLoopPoll(object cookie) {
			if (udp == null) {
			   return true;
			}

			long now = Platform.GetCurrentTimeMS();

			PumpSendQueue();
			switch (currentState) {
				case State.Syncing:
					uint nextInterval = (state.sync.roundtrips_remaining == kNumSyncPackets) ? kSyncFirstRetryInterval : kSyncRetryInterval;
					if (lastSendTime != 0 && lastSendTime + nextInterval < now) {
					   Log($"No luck syncing after {nextInterval} ms... Re-queueing sync packet.{Environment.NewLine}");
					   SendSyncRequest();
					}
					break;

				case State.Running:
					// xxx: rig all this up with a timer wrapper
					if (state.running.last_input_packet_recv_time == 0 || state.running.last_input_packet_recv_time + kRunningRetryInterval < now) {
					   Log($"Haven't exchanged packets in a while (last received:{lastReceivedInput.Frame}  last sent:{lastSentInput.Frame}).  Resending.{Environment.NewLine}");
					   SendPendingOutput();
					   state.running.last_input_packet_recv_time = now;
					}

					if (state.running.last_quality_report_time == 0 || state.running.last_quality_report_time + kQualityReportInterval < now) {
						UDPMessage msg = new UDPMessage(UDPMessage.MsgType.QualityReport) {
							qualityReport = {
								ping = Platform.GetCurrentTimeMS(), frameAdvantage = (sbyte) localFrameAdvantage
							}
						};
						
						SendMsg(ref msg);
					   state.running.last_quality_report_time = now;
					}

					if (state.running.last_network_stats_interval == 0 || state.running.last_network_stats_interval + kNetworkStatsInterval < now) {
					   UpdateNetworkStats();
					   state.running.last_network_stats_interval = now;
					}

					if (lastSendTime != 0 && lastSendTime + kKeepAliveInterval < now) {
					   Log($"Sending keep alive packet{Environment.NewLine}");

					   UDPMessage udpMsg = new UDPMessage(UDPMessage.MsgType.KeepAlive);
					   SendMsg(ref udpMsg); // TODO ref is unnecessary?
					}

					if (disconnectTimeout != 0 && disconnectNotifyStart != 0 && 
					   !disconnectNotifySent && (lastReceiveTime + disconnectNotifyStart < now)) {
						
					   Log($"Endpoint has stopped receiving packets for {disconnectNotifyStart} ms.  Sending notification.{Environment.NewLine}");
					   Event e = new Event(Event.Type.NetworkInterrupted) {
						   network_interrupted = {
							   disconnect_timeout = (int) (disconnectTimeout - disconnectNotifyStart)
						   }
					   };
					   
					   QueueEvent(ref e);
					   disconnectNotifySent = true;
					}

					if (disconnectTimeout != 0 && (lastReceiveTime + disconnectTimeout < now)) {
					   if (!disconnectEventSent) {
					      Log($"Endpoint has stopped receiving packets for {disconnectTimeout} ms.  Disconnecting.{Environment.NewLine}");
					      
					      Event evt = new Event(Event.Type.Disconnected);
					      QueueEvent(ref evt); // TODO ref is unnecessary?
					      disconnectEventSent = true;
					   }
					}
					break;

				case State.Disconnected:
				   if (shutdownTimeout < now) {
				      Log($"Shutting down udp connection.{Environment.NewLine}");
				      udp = null;
				      shutdownTimeout = 0;
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