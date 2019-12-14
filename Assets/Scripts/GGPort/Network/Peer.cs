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
		private static readonly int kSendLatency = Platform.GetConfigInt("ggpo.network.delay");
		private static readonly Random kRandom = new Random();


		// Network transmission information
		private Transport transport;
		private IPEndPoint peerAddress;
		private ushort magicNumber;
		private int queueID;
		private ushort remoteMagicNumber;
		private bool connected;
		private OutOfOrderPacket outOfOrderPacket;
		private readonly CircularQueue<StampedPeerMessage> sendQueue;
		private PeerMessage.ConnectStatus[] remoteStatuses;
		
		// Stats
		private int roundTripTime;
		private int packetsSent;
		private int bytesSent;
		private int kbpsSent;
		private long statsStartTime;

		// The state machine
		private readonly PeerMessage.ConnectStatus[] peerConnectStatuses;
		private PeerMessage.ConnectStatus[] localConnectStatuses;
		private State currentState;
		private StateUnion state;

		// Fairness
		private int localFrameAdvantage;
		private int remoteFrameAdvantage;

		// Packet loss
		private readonly CircularQueue<GameInput> pendingOutgoingInputs;
		private GameInput lastReceivedInput;
		private GameInput lastSentInput;
		private GameInput lastAckedInput;
		private long lastSendTime;
		private long lastReceiveTime;
		private long shutdownTimeout;
		private bool disconnectEventSent;
		private uint disconnectTimeout;
		private uint disconnectNotifyStart;
		private bool disconnectNotifySent;

		private ushort nextSendSequenceNumber;
		private ushort nextReceiveSequenceNumber;

		// Rift synchronization
		private readonly TimeSync timeSync;

		// Event queue
		private readonly CircularQueue<Event> eventQueue;
		
		// Message dispatch
		private delegate bool MessageHandler(PeerMessage incomingMsg);
		private readonly Dictionary<PeerMessage.MsgType, MessageHandler> messageHandlersByType;

		public Peer() {
			sendQueue = new CircularQueue<StampedPeerMessage>(64);
			pendingOutgoingInputs = new CircularQueue<GameInput>(64);
			peerConnectStatuses = new PeerMessage.ConnectStatus[PeerMessage.kMaxPlayers];
			timeSync = new TimeSync();
			eventQueue = new CircularQueue<Event>(64);
			remoteStatuses = new PeerMessage.ConnectStatus[PeerMessage.kMaxPlayers];
			
			localFrameAdvantage = 0;
			remoteFrameAdvantage = 0;
			queueID = -1;
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
			transport = null;
			
			lastSentInput.Init(-1, null, 1);
			lastReceivedInput.Init(-1, null, 1);
			lastAckedInput.Init(-1, null, 1);

			state = default;
			
			for (int i = 0; i < peerConnectStatuses.Length; i++) {
				peerConnectStatuses[i] = default;
				peerConnectStatuses[i].LastFrame = -1;
			}

			peerAddress = default;
			outOfOrderPacket = new OutOfOrderPacket(queueID);
			outOfOrderPacket.Clear();

			messageHandlersByType = new Dictionary<PeerMessage.MsgType, MessageHandler> {
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
			this.transport = transport;
			this.queueID = queueID;
			localConnectStatuses = statuses;

			peerAddress = endPoint;

			do {
				magicNumber = (ushort) new Random().Next(0, ushort.MaxValue); // TODO this class should hold a Random type var
			} while (magicNumber == 0);
			
			poll.RegisterLoop(this);
		}

		public void Synchronize() {
			if (transport == null) { return; }

			currentState = State.Syncing;
			state.sync.RoundTripsRemaining = kNumSyncPackets;
			SendSyncRequest();
		}

		public bool GetPeerConnectStatus(int id, out int frame) {
			frame = peerConnectStatuses[id].LastFrame;
			return !peerConnectStatuses[id].IsDisconnected;
		}
		
		public bool IsInitialized() { return transport != null; }
		public bool IsSynchronized() { return currentState == State.Running; }
		public bool IsRunning() { return currentState == State.Running; }

		public void SendInput(GameInput input) {
			if (transport == null) { return; }

			if (currentState == State.Running) {
				// Check to see if this is a good time to adjust for the rift...
				timeSync.AdvanceFrame(input, localFrameAdvantage, remoteFrameAdvantage);

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
			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.InputAck) {
				inputAck = {
					ackFrame = lastReceivedInput.Frame
				}
			};
			
			SendMsg(msg);
		}

		public bool HandlesMsg(IPEndPoint from) {
			return transport != null && peerAddress.Equals(from);
		}
		
		public void OnMsg(PeerMessage msg) {
			// filter out messages that don't match what we expect
			ushort seq = msg.header.SequenceNumber;
			if (msg.header.type != PeerMessage.MsgType.SyncRequest &&
			    msg.header.type != PeerMessage.MsgType.SyncReply) {
				if (msg.header.MagicNumber != remoteMagicNumber) {
					LogMsg("recv rejecting", msg);
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
			LogMsg("recv", msg);
			if ((byte) msg.header.type >= messageHandlersByType.Count) {
				OnInvalid(msg);
			} else if (messageHandlersByType[msg.header.type](msg)) {
				lastReceiveTime = Platform.GetCurrentTimeMS();

				bool canResumeIfNeeded = disconnectNotifySent && currentState == State.Running;
				if (canResumeIfNeeded) {
					Event evt = new Event(Event.Type.NetworkResumed);
					QueueEvent(evt);
					disconnectNotifySent = false;
				}
			}
		}

		public void Disconnect() {
			currentState = State.Disconnected;
			shutdownTimeout = Platform.GetCurrentTimeMS() + kUDPShutdownTimer;
		}

		public NetworkStats GetNetworkStats() {
			return new NetworkStats {
				network = {
					Ping = roundTripTime,
					SendQueueLength = pendingOutgoingInputs.Count,
					KbpsSent = kbpsSent
				},
				timeSync = {
					RemoteFramesBehind = remoteFrameAdvantage,
					LocalFramesBehind = localFrameAdvantage
				}
			};
		}

		public bool GetEvent(out Event evt) {
			if (eventQueue.Count == 0) {
				evt = default;
				return false;
			}

			evt = eventQueue.Pop();
			
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
			* we are.  Counter-intuitive, I know.  It's an advantage because
			* it means they'll have to predict more often and our moves will
			* pop more frequently.
			*/
			localFrameAdvantage = remoteFrame - localFrame;
		}

		public int RecommendFrameDelay() {
			// XXX: require idle input should be a configuration parameter
			return timeSync.recommend_frame_wait_duration(false);
		}

		public void SetDisconnectTimeout(uint timeout) {
			disconnectTimeout = timeout;
		}

		public void SetDisconnectNotifyStart(uint timeout) {
			disconnectNotifyStart = timeout;
		}

		private void UpdateNetworkStats() {
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

		private void QueueEvent(Event evt) {
			LogEvent("Queuing event", evt);
			eventQueue.Push(evt);
		}

		private void ClearSendQueue() {
			while (sendQueue.Count > 0) {
				sendQueue.Peek().Msg = default;
				sendQueue.Pop();
			}
		}

		// TODO revalidate these log wrappers as they may add some info to the string passed to global log
		private void Log(string msg) => Log(queueID, msg);
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
			state.sync.Random = (uint) (kRandom.Next(1, ushort.MaxValue) & 0xFFFF);

			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.SyncRequest) {
				syncRequest = {
					randomRequest = state.sync.Random
				}
			};
			
			SendMsg(msg);
		}

		private void SendMsg(PeerMessage msg) {
			LogMsg("send", msg);

			packetsSent++;
			lastSendTime = Platform.GetCurrentTimeMS();

			msg.header.MagicNumber = magicNumber;
			msg.header.SequenceNumber = nextSendSequenceNumber++;

			sendQueue.Push(new StampedPeerMessage(peerAddress, msg));
			PumpSendQueue();
		}

		private void PumpSendQueue() {
			Random random = new Random(); // TODO pry move to more global scope...maybe?
			BinaryFormatter formatter = new BinaryFormatter(); // TODO same here
			while (sendQueue.Count > 0 ) {
				StampedPeerMessage entry = sendQueue.Peek();

				if (kSendLatency != 0) {
					// should really come up with a gaussian distribution based on the configured
					// value, but this will do for now.
					
					int jitter = kSendLatency * 2 / 3 + random.Next(0, ushort.MaxValue) % kSendLatency / 3; // TODO cleanup rand
					if (Platform.GetCurrentTimeMS() < sendQueue.Peek().CreationTime + jitter) {
						break;
					}
				}

				bool shouldCreatePacket = !outOfOrderPacket.RandomSet(entry);
				if (shouldCreatePacket) {
					Platform.Assert(!entry.DestinationAddress.Address.Equals(IPAddress.None));

					using (MemoryStream ms = new MemoryStream()) {
						formatter.Serialize(ms, entry.Msg);
						bytesSent += transport.SendTo(ms.ToArray(), (int) ms.Length, 0, entry.DestinationAddress);
					} // TODO optimize/refactor

					entry.Msg = default;
				}

				sendQueue.Pop();
			}
			
			if (!outOfOrderPacket.ShouldSendNow()) { return; }
			
			Log($"Sending rogue {nameof(OutOfOrderPacket)}!");

			using (MemoryStream ms = new MemoryStream()) {
				formatter.Serialize(ms, outOfOrderPacket.Message);
				bytesSent += transport.SendTo(ms.ToArray(), (int) ms.Length, 0, outOfOrderPacket.DestinationAddress);
			} // TODO optimize/refactor

			outOfOrderPacket.Clear();
		}

		private unsafe void SendPendingOutput() {
			//LogUtil.Log($"SENDING {pendingOutgoingInputs.Count} PENDING INPUT(S) ----------------------------------------------------------{Environment.NewLine}"); // TODO remove when no longer needed
			int offset = 0;
			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.Input);

			if (pendingOutgoingInputs.Count != 0) {
				GameInput lastInput = lastAckedInput;
				GameInput outputQueueFront = pendingOutgoingInputs.Peek();
				msg.input.startFrame = outputQueueFront.Frame;
				msg.input.inputSize = (byte) outputQueueFront.Size;

				Platform.Assert(
					lastInput.IsNull() || lastInput.Frame + 1 == msg.input.startFrame, 
					$"lastInput.IsNull() || lastInput.Frame + 1 == msg.input.startFrame{Environment.NewLine}"
					+ $"{lastInput.IsNull()} || {lastInput.Frame} + 1 == {msg.input.startFrame}"
				);
				
				// set msg.input.bits (i.e. compressed outgoing input queue data)
				foreach (GameInput pendingInput in pendingOutgoingInputs) {
					bool currentEqualsLastBits = true;
					for (int j = 0; j < pendingInput.Size; j++) {
						if (pendingInput.Bits[j] == lastInput.Bits[j]) { continue; }

						currentEqualsLastBits = false;
						break;
					}
					
					if (!currentEqualsLastBits) {
						Platform.Assert(GameInput.kMaxBytes * GameInput.kMaxPlayers * 8 < 1 << BitVector.kBitVectorNibbleSize);

						for (int bitIndex = 0; bitIndex < pendingInput.Size * 8; bitIndex++) {
							Platform.Assert(bitIndex < 1 << BitVector.kBitVectorNibbleSize);
							bool pendingInputBit = pendingInput[bitIndex];
							bool lastAckedInputBit = lastInput[bitIndex];

							if (pendingInputBit == lastAckedInputBit) { continue; }

							BitVector.WriteBit(msg.input.bits, ref offset, true);
							BitVector.WriteBit(msg.input.bits, ref offset, pendingInputBit);
							BitVector.WriteNibblet(msg.input.bits, bitIndex, ref offset);
						}
					}

					BitVector.WriteBit(msg.input.bits, ref offset, false);
					lastInput = lastSentInput = pendingInput;
				}
			} else {
				msg.input.startFrame = 0;
				msg.input.inputSize = 0;
			}
			
			msg.input.ackFrame = lastReceivedInput.Frame;
			msg.input.numBits = (ushort) offset;

			msg.input.disconnectRequested = currentState == State.Disconnected;
			
			for (int peerIndex = 0; peerIndex < PeerMessage.kMaxPlayers; peerIndex++) {
				msg.input.SetPeerConnectStatus(peerIndex, localConnectStatuses?[peerIndex] ?? default);
			}

			Platform.Assert(offset < PeerMessage.kMaxCompressedBits);

			SendMsg(msg);
		}

		private bool OnInvalid(PeerMessage incomingMessage) {
			Platform.AssertFailed($"Invalid {nameof(incomingMessage)} in {nameof(Peer)}.");
			return false;
		}

		private bool OnSyncRequest(PeerMessage incomingMessage) {
			if (remoteMagicNumber != 0 && incomingMessage.header.MagicNumber != remoteMagicNumber) {
				Log($"Ignoring sync request from unknown endpoint ({incomingMessage.header.MagicNumber} != {remoteMagicNumber}).{Environment.NewLine}");
				
				return false;
			}

			PeerMessage reply = new PeerMessage(PeerMessage.MsgType.SyncReply) {
				syncReply = {
					randomReply = incomingMessage.syncRequest.randomRequest
				}
			};

			if (!state.sync.HasStartedSyncing || state.sync.StartedInorganically) {
				if (!state.sync.HasStartedSyncing) { state.sync.RoundTripsRemaining = kNumSyncPackets; }
				state.sync.StartedInorganically = true;

				PeerMessage syncReplyMessage = new PeerMessage(PeerMessage.MsgType.SyncReply) {
					syncReply = {
						randomReply = state.sync.Random
					}
				};

				OnSyncReply(syncReplyMessage);
			}
			
			SendMsg(reply);
			
			return true;
		}

		private bool OnSyncReply(PeerMessage incomingMessage) {
			if (currentState != State.Syncing) {
				Log($"Ignoring SyncReply while not syncing.{Environment.NewLine}");
				
				return incomingMessage.header.MagicNumber == remoteMagicNumber;
			}

			if (incomingMessage.syncReply.randomReply != state.sync.Random) {
				Log($"sync reply {incomingMessage.syncReply.randomReply} != {state.sync.Random}.  Keep looking...{Environment.NewLine}");
				
				return false;
			}

			if (!connected) {
				Event evt = new Event(Event.Type.Connected);
				QueueEvent(evt);
				connected = true;
			}

			Log($"Checking sync state ({state.sync.RoundTripsRemaining} round trips remaining).{Environment.NewLine}");
			if (--state.sync.RoundTripsRemaining == 0) {
				Log($"Synchronized!{Environment.NewLine}");

				Event evt = new Event(Event.Type.Synchronized);
				QueueEvent(evt);
				currentState = State.Running;
				lastReceivedInput.Frame = -1;
				remoteMagicNumber = incomingMessage.header.MagicNumber;
			} else {
				state.sync.HasStartedSyncing = true;
				
				Event evt = new Event(Event.Type.Synchronizing) {
					synchronizing = {
						Total = (int) kNumSyncPackets,
						Count = (int) (kNumSyncPackets - state.sync.RoundTripsRemaining)
					}
				};
				
				QueueEvent(evt);
				if (!state.sync.StartedInorganically) { SendSyncRequest(); }
			}
			return true;
		}

		private unsafe bool OnInput(PeerMessage incomingMessage) {
			// If a disconnect is requested, go ahead and disconnect now.
			bool disconnectRequested = incomingMessage.input.disconnectRequested;
			if (disconnectRequested) {
				if (currentState != State.Disconnected && !disconnectEventSent) {
					Log($"Disconnecting endpoint on remote request.{Environment.NewLine}");

					Event evt = new Event(Event.Type.Disconnected);
					QueueEvent(evt);
					disconnectEventSent = true;
				}
			} else {
				// Update the peer connection status if this peer is still considered to be part of the network.
				incomingMessage.input.GetConnectStatuses(ref remoteStatuses);
				for (int i = 0; i < peerConnectStatuses.Length; i++) {
					Platform.Assert(remoteStatuses[i].LastFrame >= peerConnectStatuses[i].LastFrame);
					
					peerConnectStatuses[i].IsDisconnected = peerConnectStatuses[i].IsDisconnected || remoteStatuses[i].IsDisconnected;
					peerConnectStatuses[i].LastFrame = Math.Max(
						peerConnectStatuses[i].LastFrame,
						remoteStatuses[i].LastFrame
					);
				}
			}

			// Decompress the input.
			int lastReceivedFrameNumber = lastReceivedInput.Frame;
			if (incomingMessage.input.numBits != 0) {
				int offset = 0;
				int numBits = incomingMessage.input.numBits;
				int currentFrame = incomingMessage.input.startFrame;

				lastReceivedInput.Size = incomingMessage.input.inputSize;
				if (lastReceivedInput.Frame < 0) {
					lastReceivedInput.Frame = incomingMessage.input.startFrame - 1;
				}

				while (offset < numBits) {
					/*
					* Keep walking through the frames (parsing bits) until we reach
					* the inputs for the frame right after the one we're on.
					*/
					Platform.Assert(currentFrame <= lastReceivedInput.Frame + 1);
					
					bool useInputs = currentFrame == lastReceivedInput.Frame + 1;

					byte* incomingBits = incomingMessage.input.bits;
					while (BitVector.ReadBit(incomingBits, ref offset)) {
						bool isOn = BitVector.ReadBit(incomingBits, ref offset);
						int buttonBitIndex = BitVector.ReadNibblet(incomingBits, ref offset);

						if (useInputs) { lastReceivedInput[buttonBitIndex] = isOn; }
					}

					Platform.Assert(offset <= numBits);

					// Now if we want to use these inputs, go ahead and send them to the emulator.
					if (useInputs) {
						// Move forward 1 frame in the stream.
						Platform.Assert(currentFrame == lastReceivedInput.Frame + 1);
						
						lastReceivedInput.Frame = currentFrame;

						// Send the event to the emulator
						Event evt = new Event(Event.Type.Input) {
							input = lastReceivedInput
						};

						string desc = lastReceivedInput.Desc();
						state.running.LastInputPacketReceiveTime = Platform.GetCurrentTimeMS();

						Log($"Sending frame {lastReceivedInput.Frame} to emu queue {queueID} ({desc}).{Environment.NewLine}");
						QueueEvent(evt);
					} else {
						Log($"Skipping past frame:({currentFrame}) current is {lastReceivedInput.Frame}.{Environment.NewLine}");
					}

					// Move forward 1 frame in the input stream.
					currentFrame++;
				}
			}

			Platform.Assert(lastReceivedInput.Frame >= lastReceivedFrameNumber);

			// Get rid of our buffered input
			while (pendingOutgoingInputs.Count != 0 && pendingOutgoingInputs.Peek().Frame < incomingMessage.input.ackFrame) {
				Log($"Throwing away pending output frame {pendingOutgoingInputs.Peek().Frame}{Environment.NewLine}");
				lastAckedInput = pendingOutgoingInputs.Pop();
			}

			return true;
		}

		private bool OnInputAck(PeerMessage incomingMessage) {
			// Get rid of our buffered input
			while (pendingOutgoingInputs.Count != 0 && pendingOutgoingInputs.Peek().Frame < incomingMessage.inputAck.ackFrame) {
				Log($"Throwing away pending output frame {pendingOutgoingInputs.Peek().Frame}{Environment.NewLine}");
				lastAckedInput = pendingOutgoingInputs.Pop();
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

			remoteFrameAdvantage = incomingMessage.qualityReport.frameAdvantage;
			
			return true;
		}

		private bool OnQualityReply(PeerMessage incomingMessage) {
			roundTripTime = (int) (Platform.GetCurrentTimeMS() - incomingMessage.qualityReply.pong);
			
			return true;
		}

		private bool OnKeepAlive(PeerMessage incomingMessage) {
			return true;
		}
		
		public virtual bool OnHandlePoll(object cookie) { return true; }
		public virtual bool OnMsgPoll(object cookie) { return true; }
		public virtual bool OnPeriodicPoll(object cookie, long lastFireTime) { return true; }
		public virtual bool OnLoopPoll(object cookie) {
			if (transport == null) {
			   return true;
			}

			long now = Platform.GetCurrentTimeMS();

			PumpSendQueue();
			switch (currentState) {
				case State.Syncing:
					uint nextInterval = (state.sync.RoundTripsRemaining == kNumSyncPackets) ? kSyncFirstRetryInterval : kSyncRetryInterval;
					if (lastSendTime != 0 && lastSendTime + nextInterval < now) {
					   Log($"No luck syncing after {nextInterval} ms... Re-queueing sync packet.{Environment.NewLine}");
					   SendSyncRequest();
					}
					break;

				case State.Running:
					// xxx: rig all this up with a timer wrapper
					if (state.running.LastInputPacketReceiveTime == 0 || state.running.LastInputPacketReceiveTime + kRunningRetryInterval < now) {
					   Log($"Haven't exchanged packets in a while (last received:{lastReceivedInput.Frame}  last sent:{lastSentInput.Frame}).  Resending.{Environment.NewLine}");
					   SendPendingOutput();
					   state.running.LastInputPacketReceiveTime = now;
					}

					if (state.running.LastQualityReportTime == 0 || state.running.LastQualityReportTime + kQualityReportInterval < now) {
						PeerMessage msg = new PeerMessage(PeerMessage.MsgType.QualityReport) {
							qualityReport = {
								ping = Platform.GetCurrentTimeMS(), frameAdvantage = (sbyte) localFrameAdvantage
							}
						};
						
						SendMsg(msg);
						state.running.LastQualityReportTime = now;
					}

					if (state.running.LastNetworkStatsInterval == 0 || state.running.LastNetworkStatsInterval + kNetworkStatsInterval < now) {
					   UpdateNetworkStats();
					   state.running.LastNetworkStatsInterval = now;
					}

					if (lastSendTime != 0 && lastSendTime + kKeepAliveInterval < now) {
					   Log($"Sending keep alive packet{Environment.NewLine}");

					   PeerMessage peerMsg = new PeerMessage(PeerMessage.MsgType.KeepAlive);
					   SendMsg(peerMsg);
					}

					if (disconnectTimeout != 0 && disconnectNotifyStart != 0 && 
					   !disconnectNotifySent && (lastReceiveTime + disconnectNotifyStart < now)) {
						
					   Log($"Endpoint has stopped receiving packets for {disconnectNotifyStart} ms.  Sending notification.{Environment.NewLine}");
					   Event e = new Event(Event.Type.NetworkInterrupted) {
						   network_interrupted = {
							   DisconnectTimeout = (int) (disconnectTimeout - disconnectNotifyStart)
						   }
					   };
					   
					   QueueEvent(e);
					   disconnectNotifySent = true;
					}

					if (disconnectTimeout != 0 && (lastReceiveTime + disconnectTimeout < now)) {
					   if (!disconnectEventSent) {
					      Log($"Endpoint has stopped receiving packets for {disconnectTimeout} ms.  Disconnecting.{Environment.NewLine}");
					      
					      Event evt = new Event(Event.Type.Disconnected);
					      QueueEvent(evt);
					      disconnectEventSent = true;
					   }
					}
					break;

				case State.Disconnected:
				   if (shutdownTimeout < now) {
				      Log($"Shutting down udp connection.{Environment.NewLine}");
				      transport = null;
				      shutdownTimeout = 0;
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
					int delay = kRandom.Next(0, ushort.MaxValue) % (kSendLatency * 10 + 1000);
					sendTime = Platform.GetCurrentTimeMS() + delay;
					message = entry.Msg;
					DestinationAddress = entry.DestinationAddress;
					
					Log(queueID, $"creating rogue oop (seq: {entry.Msg.header.SequenceNumber}  delay: {delay}){Environment.NewLine}");
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