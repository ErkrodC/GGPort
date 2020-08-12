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
	public class Peer : IPollSink {
		private const int m_UDP_HEADER_SIZE = 28; // Size of IP + UDP headers
		private const uint m_NUM_SYNC_PACKETS = 5;
		private const uint m_SYNC_RETRY_INTERVAL = 2000;
		private const uint m_SYNC_FIRST_RETRY_INTERVAL = 500;
		private const int m_RUNNING_RETRY_INTERVAL = 200;
		private const int m_KEEP_ALIVE_INTERVAL = 200;
		private const int m_QUALITY_REPORT_INTERVAL = 1000;
		private const int m_NETWORK_STATS_INTERVAL = 1000;
		private const int m_UDP_SHUTDOWN_TIMER = 5000;
		private const int m_MAX_SEQ_DISTANCE = 1 << 15;
		private static readonly int ms_SEND_LATENCY = Platform.GetConfigInt("ggpo.network.delay");
		private static readonly Random ms_RANDOM = new Random();


		// Network transmission information
		private Transport m_transport;
		private IPEndPoint m_peerAddress;
		private ushort m_magicNumber;
		private int m_queueID;
		private ushort m_remoteMagicNumber;
		private bool m_connected;
		private OutOfOrderPacket m_outOfOrderPacket;
		private readonly CircularQueue<StampedPeerMessage> m_sendQueue;
		private PeerMessage.ConnectStatus[] m_remoteStatuses;
		
		// Stats
		private int m_roundTripTime;
		private int m_packetsSent;
		private int m_bytesSent;
		private int m_kbpsSent;
		private long m_statsStartTime;

		// The state machine
		private readonly PeerMessage.ConnectStatus[] m_peerConnectStatuses;
		private PeerMessage.ConnectStatus[] m_localConnectStatuses;
		private State m_currentState;
		private StateUnion m_state;

		// Fairness
		private int m_localFrameAdvantage;
		private int m_remoteFrameAdvantage;

		// Packet loss
		private readonly CircularQueue<GameInput> m_pendingOutgoingInputs;
		private GameInput m_lastReceivedInput;
		private GameInput m_lastSentInput;
		private GameInput m_lastAckedInput;
		private long m_lastSendTime;
		private long m_lastReceiveTime;
		private long m_shutdownTimeout;
		private bool m_disconnectEventSent;
		private uint m_disconnectTimeout;
		private uint m_disconnectNotifyStart;
		private bool m_disconnectNotifySent;

		private ushort m_nextSendSequenceNumber;
		private ushort m_nextReceiveSequenceNumber;

		// Rift synchronization
		private readonly TimeSync m_timeSync;

		// Event queue
		private readonly CircularQueue<Event> m_eventQueue;
		
		// Message dispatch
		private delegate bool MessageHandler(PeerMessage incomingMsg);
		private readonly Dictionary<PeerMessage.MsgType, MessageHandler> m_messageHandlersByType;

		public Peer() {
			m_sendQueue = new CircularQueue<StampedPeerMessage>(64);
			m_pendingOutgoingInputs = new CircularQueue<GameInput>(64);
			m_peerConnectStatuses = new PeerMessage.ConnectStatus[PeerMessage.MAX_PLAYERS];
			m_timeSync = new TimeSync();
			m_eventQueue = new CircularQueue<Event>(64);
			m_remoteStatuses = new PeerMessage.ConnectStatus[PeerMessage.MAX_PLAYERS];
			
			m_localFrameAdvantage = 0;
			m_remoteFrameAdvantage = 0;
			m_queueID = -1;
			m_magicNumber = 0;
			m_remoteMagicNumber = 0;
			m_packetsSent = 0;
			m_bytesSent = 0;
			m_statsStartTime = 0;
			m_lastSendTime = 0;
			m_shutdownTimeout = 0;
			m_disconnectTimeout = 0;
			m_disconnectNotifyStart = 0;
			m_disconnectNotifySent = false;
			m_disconnectEventSent = false;
			m_connected = false;
			m_nextSendSequenceNumber = 0;
			m_nextReceiveSequenceNumber = 0;
			m_transport = null;
			
			m_lastSentInput.Init(-1, null, 1);
			m_lastReceivedInput.Init(-1, null, 1);
			m_lastAckedInput.Init(-1, null, 1);

			m_state = default;
			
			for (int i = 0; i < m_peerConnectStatuses.Length; i++) {
				m_peerConnectStatuses[i] = default;
				m_peerConnectStatuses[i].lastFrame = -1;
			}

			m_peerAddress = default;
			m_outOfOrderPacket = new OutOfOrderPacket(m_queueID);
			m_outOfOrderPacket.Clear();

			m_messageHandlersByType = new Dictionary<PeerMessage.MsgType, MessageHandler> {
				[PeerMessage.MsgType.Invalid] = OnInvalid,
				[PeerMessage.MsgType.SyncRequest] = OnSyncRequest,
				[PeerMessage.MsgType.SyncReply] = OnSyncReply,
				[PeerMessage.MsgType.Input] = OnInput,
				[PeerMessage.MsgType.QualityReport] = OnQualityReport,
				[PeerMessage.MsgType.QualityReply] = OnQualityReply,
				[PeerMessage.MsgType.KeepAlive] = OnKeepAlive,
				[PeerMessage.MsgType.InputAck] = OnInputAck
			};
		}

		~Peer() {
			ClearSendQueue();
		}

		// TODO last param is list?
		public void Init(ref Transport transport, ref Poll poll, int queueID, IPEndPoint endPoint, PeerMessage.ConnectStatus[] statuses) {
			this.m_transport = transport;
			this.m_queueID = queueID;
			m_localConnectStatuses = statuses;

			m_peerAddress = endPoint;

			do {
				m_magicNumber = (ushort) new Random().Next(0, ushort.MaxValue); // TODO this class should hold a Random type var
			} while (m_magicNumber == 0);
			
			poll.RegisterLoop(this);
		}

		public void Synchronize() {
			if (m_transport == null) { return; }

			m_currentState = State.Syncing;
			m_state.sync.RoundTripsRemaining = m_NUM_SYNC_PACKETS;
			SendSyncRequest();
		}

		public bool GetPeerConnectStatus(int id, out int frame) {
			frame = m_peerConnectStatuses[id].lastFrame;
			return !m_peerConnectStatuses[id].isDisconnected;
		}
		
		public bool IsInitialized() { return m_transport != null; }
		public bool IsSynchronized() { return m_currentState == State.Running; }
		public bool IsRunning() { return m_currentState == State.Running; }

		public void SendInput(GameInput input) {
			if (m_transport == null) { return; }

			if (m_currentState == State.Running) {
				// Check to see if this is a good time to adjust for the rift...
				m_timeSync.AdvanceFrame(input, m_localFrameAdvantage, m_remoteFrameAdvantage);

				/*
				* Save this input packet
				*
				* XXX: This queue may fill up for spectators who do not ack input packets in a timely
				* manner.  When this happens, we can either resize the queue (ug) or disconnect them
				* (better, but still ug).  For the meantime, make this queue really big to decrease
				* the odds of this happening...
				*/
				m_pendingOutgoingInputs.Push(input);
			}
				
			SendPendingOutput();
		}

		public void SendInputAck() {
			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.InputAck) {
				inputAck = {
					ackFrame = m_lastReceivedInput.frame
				}
			};
			
			SendMsg(msg);
		}

		public bool DoesHandleMessageFromEndPoint(IPEndPoint from) {
			return m_transport != null && m_peerAddress.Equals(from);
		}
		
		public void OnMessageReceived(PeerMessage message) {
			// filter out messages that don't match what we expect
			ushort seq = message.header.sequenceNumber;
			if (message.header.type != PeerMessage.MsgType.SyncRequest &&
			    message.header.type != PeerMessage.MsgType.SyncReply) {
				if (message.header.magicNumber != m_remoteMagicNumber) {
					LogMsg("recv rejecting", message);
					return;
				}

				// filter out out-of-order packets
				ushort skipped = (ushort)(seq - m_nextReceiveSequenceNumber);
				// Log("checking sequence number -> next - seq : %d - %d = %d{Environment.NewLine}", seq, _next_recv_seq, skipped);
				if (skipped > m_MAX_SEQ_DISTANCE) {
					Log($"dropping out of order packet (seq: {seq}, last seq:{m_nextReceiveSequenceNumber}){Environment.NewLine}");
					return;
				}
			}

			m_nextReceiveSequenceNumber = seq;
			LogMsg("recv", message);
			if ((byte) message.header.type >= m_messageHandlersByType.Count) {
				OnInvalid(message);
			} else if (m_messageHandlersByType[message.header.type](message)) {
				m_lastReceiveTime = Platform.GetCurrentTimeMS();

				bool canResumeIfNeeded = m_disconnectNotifySent && m_currentState == State.Running;
				if (canResumeIfNeeded) {
					Event evt = new Event(Event.Type.NetworkResumed);
					QueueEvent(evt);
					m_disconnectNotifySent = false;
				}
			}
		}

		public void Disconnect() {
			m_currentState = State.Disconnected;
			m_shutdownTimeout = Platform.GetCurrentTimeMS() + m_UDP_SHUTDOWN_TIMER;
		}

		public NetworkStats GetNetworkStats() {
			return new NetworkStats {
				network = {
					Ping = m_roundTripTime,
					SendQueueLength = m_pendingOutgoingInputs.Count,
					KbpsSent = m_kbpsSent
				},
				timeSync = {
					RemoteFramesBehind = m_remoteFrameAdvantage,
					LocalFramesBehind = m_localFrameAdvantage
				}
			};
		}

		public bool GetEvent(out Event evt) {
			if (m_eventQueue.Count == 0) {
				evt = default;
				return false;
			}

			evt = m_eventQueue.Pop();
			
			return true;
		}

		public void SetLocalFrameNumber(int localFrame) {
			/*
			* Estimate which frame the other guy is one by looking at the
			* last frame they gave us plus some delta for the one-way packet
			* trip time.
			*/
			int remoteFrame = m_lastReceivedInput.frame + (m_roundTripTime * 60 / 1000);

			/*
			* Our frame advantage is how many frames *behind* the other guy
			* we are.  Counter-intuitive, I know.  It's an advantage because
			* it means they'll have to predict more often and our moves will
			* pop more frequently.
			*/
			m_localFrameAdvantage = remoteFrame - localFrame;
		}

		public int RecommendFrameDelay() {
			// XXX: require idle input should be a configuration parameter
			return m_timeSync.recommend_frame_wait_duration(false);
		}

		public void SetDisconnectTimeout(uint timeout) {
			m_disconnectTimeout = timeout;
		}

		public void SetDisconnectNotifyStart(uint timeout) {
			m_disconnectNotifyStart = timeout;
		}

		private void UpdateNetworkStats() {
			long now = Platform.GetCurrentTimeMS();

			if (m_statsStartTime == 0) {
				m_statsStartTime = now;
			}

			int totalBytesSent = m_bytesSent + (m_UDP_HEADER_SIZE * m_packetsSent);
			float seconds = (float)((now - m_statsStartTime) / 1000.0);
			float bytesPerSecond = totalBytesSent / seconds;
			float udpOverhead = (float)(100.0 * (m_UDP_HEADER_SIZE * m_packetsSent) / m_bytesSent);

			m_kbpsSent = (int) (bytesPerSecond / 1024);

			Log(
				$"Network Stats -- "
				+ $"Bandwidth: {m_kbpsSent:F} KBps   "
				+ $"Packets Sent: {m_packetsSent:D5} ({(float) m_packetsSent * 1000 / (now - m_statsStartTime):F} pps)   "
				+ $"KB Sent: {totalBytesSent / 1024.0:F}    "
				+ $"UDP Overhead: {udpOverhead:F} %%.{Environment.NewLine}"
			);
		}

		private void QueueEvent(Event evt) {
			LogEvent("Queuing event", evt);
			m_eventQueue.Push(evt);
		}

		private void ClearSendQueue() {
			while (m_sendQueue.Count > 0) {
				m_sendQueue.Peek().Msg = default;
				m_sendQueue.Pop();
			}
		}

		// TODO revalidate these log wrappers as they may add some info to the string passed to global log
		private void Log(string msg) => Log(m_queueID, msg);
		private static void Log(int queueID, string msg) {
			string prefix = $"{nameof(Peer)} {queueID} | ";
			LogUtil.Log(prefix + msg);
		}

		private void LogMsg(string prefix, PeerMessage msg) {
			switch (msg.header.type) {
				case PeerMessage.MsgType.SyncRequest:
					Log($"{prefix} sync-request ({msg.syncRequest.randomRequest}).{Environment.NewLine}");
					break;
				case PeerMessage.MsgType.SyncReply:
					Log($"{prefix} sync-reply ({msg.syncReply.randomReply}).{Environment.NewLine}");
					break;
				case PeerMessage.MsgType.QualityReport:
					Log($"{prefix} quality report.{Environment.NewLine}");
					break;
				case PeerMessage.MsgType.QualityReply:
					Log($"{prefix} quality reply.{Environment.NewLine}");
					break;
				case PeerMessage.MsgType.KeepAlive:
					Log($"{prefix} keep alive.{Environment.NewLine}");
					break;
				case PeerMessage.MsgType.Input:
					Log($"{prefix} game-compressed-input {msg.input.startFrame} (+ {msg.input.numBits} bits).{Environment.NewLine}");
					break;
				case PeerMessage.MsgType.InputAck:
					Log($"{prefix} input ack.{Environment.NewLine}");
					break;
				default:
					Platform.AssertFailed($"Unknown {nameof(PeerMessage)} type.");
					break;
			}
		}

		private void LogEvent(string prefix, Event evt) {
			switch (evt.type) {
				case Event.Type.Synchronized:
					Log($"{prefix} (event: {nameof(Event.Type.Synchronized)}).{Environment.NewLine}");
					break;
			}
		}
		
		private void SendSyncRequest() {
			m_state.sync.Random = (uint) (ms_RANDOM.Next(1, ushort.MaxValue) & 0xFFFF);

			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.SyncRequest) {
				syncRequest = {
					randomRequest = m_state.sync.Random
				}
			};
			
			SendMsg(msg);
		}

		private void SendMsg(PeerMessage msg) {
			LogMsg("send", msg);

			m_packetsSent++;
			m_lastSendTime = Platform.GetCurrentTimeMS();

			msg.header.magicNumber = m_magicNumber;
			msg.header.sequenceNumber = m_nextSendSequenceNumber++;

			m_sendQueue.Push(new StampedPeerMessage(m_peerAddress, msg));
			PumpSendQueue();
		}

		private void PumpSendQueue() {
			Random random = new Random(); // TODO pry move to more global scope...maybe?
			BinaryFormatter formatter = new BinaryFormatter(); // TODO same here
			while (m_sendQueue.Count > 0 ) {
				StampedPeerMessage entry = m_sendQueue.Peek();

				if (ms_SEND_LATENCY != 0) {
					// should really come up with a gaussian distribution based on the configured
					// value, but this will do for now.
					
					int jitter = ms_SEND_LATENCY * 2 / 3 + random.Next(0, ushort.MaxValue) % ms_SEND_LATENCY / 3; // TODO cleanup rand
					if (Platform.GetCurrentTimeMS() < m_sendQueue.Peek().CreationTime + jitter) {
						break;
					}
				}

				bool shouldCreatePacket = !m_outOfOrderPacket.RandomSet(entry);
				if (shouldCreatePacket) {
					Platform.Assert(!entry.DestinationAddress.Address.Equals(IPAddress.None));

					using (MemoryStream ms = new MemoryStream()) {
						formatter.Serialize(ms, entry.Msg);
						m_bytesSent += m_transport.SendTo(ms.ToArray(), (int) ms.Length, 0, entry.DestinationAddress);
					} // TODO optimize/refactor

					entry.Msg = default;
				}

				m_sendQueue.Pop();
			}
			
			if (!m_outOfOrderPacket.ShouldSendNow()) { return; }
			
			Log($"Sending rogue {nameof(OutOfOrderPacket)}!");

			using (MemoryStream ms = new MemoryStream()) {
				formatter.Serialize(ms, m_outOfOrderPacket.Message);
				m_bytesSent += m_transport.SendTo(ms.ToArray(), (int) ms.Length, 0, m_outOfOrderPacket.DestinationAddress);
			} // TODO optimize/refactor

			m_outOfOrderPacket.Clear();
		}

		private unsafe void SendPendingOutput() {
			//LogUtil.Log($"SENDING {pendingOutgoingInputs.Count} PENDING INPUT(S) ----------------------------------------------------------{Environment.NewLine}"); // TODO remove when no longer needed
			int offset = 0;
			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.Input);

			if (m_pendingOutgoingInputs.Count != 0) {
				GameInput lastInput = m_lastAckedInput;
				GameInput outputQueueFront = m_pendingOutgoingInputs.Peek();
				msg.input.startFrame = outputQueueFront.frame;
				msg.input.inputSize = (byte) outputQueueFront.size;

				Platform.Assert(
					lastInput.IsNull() || lastInput.frame + 1 == msg.input.startFrame, 
					$"lastInput.IsNull() || lastInput.frame + 1 == msg.input.startFrame{Environment.NewLine}"
					+ $"{lastInput.IsNull()} || {lastInput.frame} + 1 == {msg.input.startFrame}"
				);
				
				// set msg.input.bits (i.e. compressed outgoing input queue data)
				foreach (GameInput pendingInput in m_pendingOutgoingInputs) {
					bool currentEqualsLastBits = true;
					for (int j = 0; j < pendingInput.size; j++) {
						if (pendingInput.bits[j] == lastInput.bits[j]) { continue; }

						currentEqualsLastBits = false;
						break;
					}
					
					if (!currentEqualsLastBits) {
						Platform.Assert(GameInput.MAX_BYTES * GameInput.MAX_PLAYERS * 8 < 1 << BitVector.BIT_VECTOR_NIBBLE_SIZE);

						for (int bitIndex = 0; bitIndex < pendingInput.size * 8; bitIndex++) {
							Platform.Assert(bitIndex < 1 << BitVector.BIT_VECTOR_NIBBLE_SIZE);
							bool pendingInputBit = pendingInput[bitIndex];
							bool lastAckedInputBit = lastInput[bitIndex];

							if (pendingInputBit == lastAckedInputBit) { continue; }

							BitVector.WriteBit(msg.input.bits, ref offset, true);
							BitVector.WriteBit(msg.input.bits, ref offset, pendingInputBit);
							BitVector.WriteNibblet(msg.input.bits, bitIndex, ref offset);
						}
					}

					BitVector.WriteBit(msg.input.bits, ref offset, false);
					lastInput = m_lastSentInput = pendingInput;
				}
			} else {
				msg.input.startFrame = 0;
				msg.input.inputSize = 0;
			}
			
			msg.input.ackFrame = m_lastReceivedInput.frame;
			msg.input.numBits = (ushort) offset;

			msg.input.disconnectRequested = m_currentState == State.Disconnected;
			
			for (int peerIndex = 0; peerIndex < PeerMessage.MAX_PLAYERS; peerIndex++) {
				msg.input.SetPeerConnectStatus(peerIndex, m_localConnectStatuses?[peerIndex] ?? default);
			}

			Platform.Assert(offset < PeerMessage.MAX_COMPRESSED_BITS);

			SendMsg(msg);
		}

		private bool OnInvalid(PeerMessage incomingMessage) {
			Platform.AssertFailed($"Invalid {nameof(incomingMessage)} in {nameof(Peer)}.");
			return false;
		}

		private bool OnSyncRequest(PeerMessage incomingMessage) {
			if (m_remoteMagicNumber != 0 && incomingMessage.header.magicNumber != m_remoteMagicNumber) {
				Log($"Ignoring sync request from unknown endpoint ({incomingMessage.header.magicNumber} != {m_remoteMagicNumber}).{Environment.NewLine}");
				
				return false;
			}

			PeerMessage reply = new PeerMessage(PeerMessage.MsgType.SyncReply) {
				syncReply = {
					randomReply = incomingMessage.syncRequest.randomRequest
				}
			};

			if (!m_state.sync.HasStartedSyncing || m_state.sync.StartedInorganically) {
				if (!m_state.sync.HasStartedSyncing) { m_state.sync.RoundTripsRemaining = m_NUM_SYNC_PACKETS; }
				m_state.sync.StartedInorganically = true;

				PeerMessage syncReplyMessage = new PeerMessage(PeerMessage.MsgType.SyncReply) {
					header = {
						magicNumber = incomingMessage.header.magicNumber
					},
					syncReply = {
						randomReply = m_state.sync.Random
					}
				};

				OnSyncReply(syncReplyMessage);
			}
			
			SendMsg(reply);
			
			return true;
		}

		private bool OnSyncReply(PeerMessage incomingMessage) {
			if (m_currentState != State.Syncing) {
				Log($"Ignoring SyncReply while not syncing.{Environment.NewLine}");
				
				return incomingMessage.header.magicNumber == m_remoteMagicNumber;
			}

			if (incomingMessage.syncReply.randomReply != m_state.sync.Random) {
				Log($"sync reply {incomingMessage.syncReply.randomReply} != {m_state.sync.Random}.  Keep looking...{Environment.NewLine}");
				
				return false;
			}

			if (!m_connected) {
				Event evt = new Event(Event.Type.Connected);
				QueueEvent(evt);
				m_connected = true;
			}

			Log($"Checking sync state ({m_state.sync.RoundTripsRemaining} round trips remaining).{Environment.NewLine}");
			if (--m_state.sync.RoundTripsRemaining == 0) {
				Log($"Synchronized!{Environment.NewLine}");

				Event evt = new Event(Event.Type.Synchronized);
				QueueEvent(evt);
				m_currentState = State.Running;
				m_lastReceivedInput.frame = -1;
				m_remoteMagicNumber = incomingMessage.header.magicNumber;
			} else {
				m_state.sync.HasStartedSyncing = true;
				
				Event evt = new Event(Event.Type.Synchronizing) {
					synchronizing = {
						Total = (int) m_NUM_SYNC_PACKETS,
						Count = (int) (m_NUM_SYNC_PACKETS - m_state.sync.RoundTripsRemaining)
					}
				};
				
				QueueEvent(evt);
				if (!m_state.sync.StartedInorganically) { SendSyncRequest(); }
			}
			return true;
		}

		private unsafe bool OnInput(PeerMessage incomingMessage) {
			// If a disconnect is requested, go ahead and disconnect now.
			bool disconnectRequested = incomingMessage.input.disconnectRequested;
			if (disconnectRequested) {
				if (m_currentState != State.Disconnected && !m_disconnectEventSent) {
					Log($"Disconnecting endpoint on remote request.{Environment.NewLine}");
					QueueEvent(new Event(Event.Type.Disconnected));
					m_disconnectEventSent = true;
				}
			} else {
				// Update the peer connection status if this peer is still considered to be part of the network.
				incomingMessage.input.GetConnectStatuses(ref m_remoteStatuses);
				for (int i = 0; i < m_peerConnectStatuses.Length; i++) {
					Platform.Assert(m_remoteStatuses[i].lastFrame >= m_peerConnectStatuses[i].lastFrame);

					m_peerConnectStatuses[i].isDisconnected =
						m_peerConnectStatuses[i].isDisconnected || m_remoteStatuses[i].isDisconnected;
					
					m_peerConnectStatuses[i].lastFrame = Math.Max(
						m_peerConnectStatuses[i].lastFrame,
						m_remoteStatuses[i].lastFrame
					);
				}
			}
			
			// Decompress the input.
			int lastReceivedFrameNumber = m_lastReceivedInput.frame;
			if (incomingMessage.input.numBits != 0) {
				int offset = 0;
				byte* incomingBits = incomingMessage.input.bits;
				int numBits = incomingMessage.input.numBits;
				int currentFrame = incomingMessage.input.startFrame;

				m_lastReceivedInput.size = incomingMessage.input.inputSize;
				if (m_lastReceivedInput.frame < 0) {
					m_lastReceivedInput.frame = incomingMessage.input.startFrame - 1;
				}

				while (offset < numBits) {
					/*
					* Keep walking through the frames (parsing bits) until we reach
					* the inputs for the frame right after the one we're on.
					*/
					Platform.Assert(currentFrame <= m_lastReceivedInput.frame + 1);
					
					bool useInputs = currentFrame == m_lastReceivedInput.frame + 1;
					
					while (BitVector.ReadBit(incomingBits, ref offset)) {
						bool isOn = BitVector.ReadBit(incomingBits, ref offset);
						int buttonBitIndex = BitVector.ReadNibblet(incomingBits, ref offset);

						if (useInputs) { m_lastReceivedInput[buttonBitIndex] = isOn; }
					}

					Platform.Assert(offset <= numBits);

					// Now if we want to use these inputs, go ahead and send them to the emulator.
					if (useInputs) {
						// Move forward 1 frame in the stream.
						Platform.Assert(currentFrame == m_lastReceivedInput.frame + 1);
						
						m_lastReceivedInput.frame = currentFrame;

						// Send the event to the emulator
						Event evt = new Event(Event.Type.Input) {
							input = m_lastReceivedInput
						};
						
						m_state.running.LastInputPacketReceiveTime = Platform.GetCurrentTimeMS();

						Log(string.Format(
							"Sending frame {0} to emu queue {1} ({2}).{3}",
							m_lastReceivedInput.frame,
							m_queueID,
							m_lastReceivedInput.Desc(),
							Environment.NewLine
						));
						QueueEvent(evt);
					} else {
						Log($"Skipping past frame:({currentFrame}) current is {m_lastReceivedInput.frame}.{Environment.NewLine}");
					}

					// Move forward 1 frame in the input stream.
					currentFrame++;
				}
			}

			Platform.Assert(m_lastReceivedInput.frame >= lastReceivedFrameNumber);

			// Get rid of our buffered input
			while (m_pendingOutgoingInputs.Count != 0 && m_pendingOutgoingInputs.Peek().frame < incomingMessage.input.ackFrame) {
				Log($"Throwing away pending output frame {m_pendingOutgoingInputs.Peek().frame}{Environment.NewLine}");
				m_lastAckedInput = m_pendingOutgoingInputs.Pop();
			}

			return true;
		}

		private bool OnInputAck(PeerMessage incomingMessage) {
			// Get rid of our buffered input
			while (m_pendingOutgoingInputs.Count != 0 && m_pendingOutgoingInputs.Peek().frame < incomingMessage.inputAck.ackFrame) {
				Log($"Throwing away pending output frame {m_pendingOutgoingInputs.Peek().frame}{Environment.NewLine}");
				m_lastAckedInput = m_pendingOutgoingInputs.Pop();
			}
			
			return true;
		}

		private bool OnQualityReport(PeerMessage incomingMessage) {
			// send a reply so the other side can compute the round trip transmit time.
			PeerMessage reply = new PeerMessage(PeerMessage.MsgType.QualityReply) {
				qualityReply = {
					pong = incomingMessage.qualityReport.ping
				}
			};
			
			SendMsg(reply);

			m_remoteFrameAdvantage = incomingMessage.qualityReport.frameAdvantage;
			
			return true;
		}

		private bool OnQualityReply(PeerMessage incomingMessage) {
			m_roundTripTime = (int) (Platform.GetCurrentTimeMS() - incomingMessage.qualityReply.pong);
			
			return true;
		}

		private bool OnKeepAlive(PeerMessage incomingMessage) {
			return true;
		}
		
		public virtual bool OnHandlePoll(object cookie) { return true; }
		public virtual bool OnMsgPoll(object cookie) { return true; }
		public virtual bool OnPeriodicPoll(object cookie, long lastFireTime) { return true; }
		public virtual bool OnLoopPoll(object cookie) {
			if (m_transport == null) {
			   return true;
			}

			long now = Platform.GetCurrentTimeMS();

			PumpSendQueue();
			switch (m_currentState) {
				case State.Syncing:
					uint nextInterval = (m_state.sync.RoundTripsRemaining == m_NUM_SYNC_PACKETS) ? m_SYNC_FIRST_RETRY_INTERVAL : m_SYNC_RETRY_INTERVAL;
					if (m_lastSendTime != 0 && m_lastSendTime + nextInterval < now) {
					   Log($"No luck syncing after {nextInterval} ms... Re-queueing sync packet.{Environment.NewLine}");
					   SendSyncRequest();
					}
					break;

				case State.Running:
					// xxx: rig all this up with a timer wrapper
					if (m_state.running.LastInputPacketReceiveTime == 0 || m_state.running.LastInputPacketReceiveTime + m_RUNNING_RETRY_INTERVAL < now) {
					   Log($"Haven't exchanged packets in a while (last received:{m_lastReceivedInput.frame}  last sent:{m_lastSentInput.frame}).  Resending.{Environment.NewLine}");
					   SendPendingOutput();
					   m_state.running.LastInputPacketReceiveTime = now;
					}

					if (m_state.running.LastQualityReportTime == 0 || m_state.running.LastQualityReportTime + m_QUALITY_REPORT_INTERVAL < now) {
						PeerMessage msg = new PeerMessage(PeerMessage.MsgType.QualityReport) {
							qualityReport = {
								ping = Platform.GetCurrentTimeMS(), frameAdvantage = (sbyte) m_localFrameAdvantage
							}
						};
						
						SendMsg(msg);
						m_state.running.LastQualityReportTime = now;
					}

					if (m_state.running.LastNetworkStatsInterval == 0 || m_state.running.LastNetworkStatsInterval + m_NETWORK_STATS_INTERVAL < now) {
					   UpdateNetworkStats();
					   m_state.running.LastNetworkStatsInterval = now;
					}

					if (m_lastSendTime != 0 && m_lastSendTime + m_KEEP_ALIVE_INTERVAL < now) {
					   Log($"Sending keep alive packet{Environment.NewLine}");

					   PeerMessage peerMsg = new PeerMessage(PeerMessage.MsgType.KeepAlive);
					   SendMsg(peerMsg);
					}

					if (m_disconnectTimeout != 0 && m_disconnectNotifyStart != 0 && 
					   !m_disconnectNotifySent && (m_lastReceiveTime + m_disconnectNotifyStart < now)) {
						
					   Log($"Endpoint has stopped receiving packets for {m_disconnectNotifyStart} ms.  Sending notification.{Environment.NewLine}");
					   Event e = new Event(Event.Type.NetworkInterrupted) {
						   network_interrupted = {
							   DisconnectTimeout = (int) (m_disconnectTimeout - m_disconnectNotifyStart)
						   }
					   };
					   
					   QueueEvent(e);
					   m_disconnectNotifySent = true;
					}

					if (m_disconnectTimeout != 0 && (m_lastReceiveTime + m_disconnectTimeout < now)) {
					   if (!m_disconnectEventSent) {
					      Log($"Endpoint has stopped receiving packets for {m_disconnectTimeout} ms.  Disconnecting.{Environment.NewLine}");
					      
					      Event evt = new Event(Event.Type.Disconnected);
					      QueueEvent(evt);
					      m_disconnectEventSent = true;
					   }
					}
					break;

				case State.Disconnected:
				   if (m_shutdownTimeout < now) {
				      Log($"Shutting down udp connection.{Environment.NewLine}");
				      m_transport = null;
				      m_shutdownTimeout = 0;
				   }
				   
				   break;
			}

			return true;
		}
		
		// TODO rename once perf tools are up
		public struct Stats {
			public readonly int ping;
			public readonly int remote_frame_advantage;
			public readonly int local_frame_advantage;
			public readonly int send_queue_len;
			public readonly Transport.Stats udp;
		}

		[StructLayout(LayoutKind.Explicit)]
		public struct Event {
			[FieldOffset(0)] public readonly Type type;
			[FieldOffset(4)] public GameInput input;
			[FieldOffset(4)] public Synchronizing synchronizing;
			[FieldOffset(4)] public NetworkInterrupted network_interrupted;

			public struct Synchronizing {
				public int Total { get; set; }
				public int Count { get; set; }
			}

			public struct NetworkInterrupted {
				public int DisconnectTimeout { get; set; }
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

		// TODO should go in debug define
		private struct OutOfOrderPacket {
			private static readonly Random kRandom = new Random();
			private static readonly int kOutOfOrderPacketPercentage = Platform.GetConfigInt("ggpo.oop.percent");

			public IPEndPoint DestinationAddress { get; private set; }

			public PeerMessage Message => message ?? default;
			private PeerMessage? message;
			
			private readonly int queueID;
			private long sendTime;

			public OutOfOrderPacket(int queueID) : this() {
				this.queueID = queueID;
			}

			public bool RandomSet(StampedPeerMessage entry) {
				bool shouldSet = kOutOfOrderPacketPercentage > 0
				                 && message == null
				                 && kRandom.Next(0, ushort.MaxValue) % 100 < kOutOfOrderPacketPercentage;

				if (shouldSet) {
					int delay = kRandom.Next(0, ushort.MaxValue) % (ms_SEND_LATENCY * 10 + 1000);
					sendTime = Platform.GetCurrentTimeMS() + delay;
					message = entry.Msg;
					DestinationAddress = entry.DestinationAddress;
					
					Log(queueID, $"creating rogue oop (seq: {entry.Msg.header.sequenceNumber}  delay: {delay}){Environment.NewLine}");
				}

				return shouldSet;
			}

			public bool ShouldSendNow() {
				return message != null && sendTime < Platform.GetCurrentTimeMS();
			}

			public void Clear() {
				message = null;
			}
		}

		private enum State {
			Syncing,
			Synchronized,
			Running,
			Disconnected
		}

		private struct StampedPeerMessage {
			public readonly long CreationTime;
			public readonly IPEndPoint DestinationAddress;
			public PeerMessage Msg;

			public StampedPeerMessage(IPEndPoint destinationAddress, PeerMessage msg) : this() {
				CreationTime = Platform.GetCurrentTimeMS();
				DestinationAddress = destinationAddress;
				Msg = msg;
			}
		}

		[StructLayout(LayoutKind.Explicit)]
		public struct StateUnion {
			[FieldOffset(0)] public Sync sync;
			[FieldOffset(0)] public Running running;

			public struct Sync {
				public uint RoundTripsRemaining { get; set; }
				public uint Random { get; set; } // last acked random? TODO
				public bool HasStartedSyncing;
				public bool StartedInorganically;
			}

			public struct Running {
				public long LastQualityReportTime { get; set; }
				public long LastNetworkStatsInterval { get; set; }
				public long LastInputPacketReceiveTime { get; set; }
			}
		}
	}
}