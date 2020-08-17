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
using Unity.Collections.LowLevel.Unsafe;

namespace GGPort {
	public class Peer : IUpdateHandler {
		private const int _UDP_HEADER_SIZE = 28; // Size of IP + UDP headers
		private const uint _NUM_SYNC_PACKETS = 5;
		private const uint _SYNC_RETRY_INTERVAL = 2000;
		private const uint _SYNC_FIRST_RETRY_INTERVAL = 500;
		private const int _RUNNING_RETRY_INTERVAL = 200;
		private const int _KEEP_ALIVE_INTERVAL = 200;
		private const int _QUALITY_REPORT_INTERVAL = 1000;
		private const int _NETWORK_STATS_INTERVAL = 1000;
		private const int _UDP_SHUTDOWN_TIMER = 5000;
		private const int _MAX_SEQ_DISTANCE = 1 << 15;
		private static readonly int _sendLatency = Platform.GetConfigInt("ggpo.network.delay");
		private static readonly Random _random = new Random();


		// Network transmission information
		private Transport _transport;
		private IPEndPoint _peerAddress;
		private ushort _magicNumber;
		private int _queueID;
		private ushort _remoteMagicNumber;
		private bool _connected;
		private OutOfOrderPacket _outOfOrderPacket;
		private readonly CircularQueue<StampedPeerMessage> _sendQueue;
		private PeerMessage.ConnectStatus[] _remoteStatuses;

		// Stats
		private int _roundTripTime;
		private int _packetsSent;
		private int _bytesSent;
		private int _kbpsSent;
		private long _statsStartTime;

		// The state machine
		private readonly PeerMessage.ConnectStatus[] _peerConnectStatuses;
		private PeerMessage.ConnectStatus[] _localConnectStatuses;
		private State _currentState;
		private StateUnion _state;

		// Fairness
		private int _localFrameAdvantage;
		private int _remoteFrameAdvantage;

		// Packet loss
		private readonly CircularQueue<GameInput> _pendingOutgoingInputs;
		private GameInput _lastReceivedInput;
		private GameInput _lastSentInput;
		private GameInput _lastAckedInput;
		private long _lastSendTime;
		private long _lastReceiveTime;
		private long _shutdownTimeout;
		private bool _disconnectEventSent;
		private uint _disconnectTimeout;
		private uint _disconnectNotifyStart;
		private bool _disconnectNotifySent;

		private ushort _nextSendSequenceNumber;
		private ushort _nextReceiveSequenceNumber;

		// Rift synchronization
		private readonly TimeSync _timeSync;

		// Event queue
		private readonly CircularQueue<Event> _eventQueue;

		// Message dispatch
		private delegate bool MessageHandler(PeerMessage incomingMsg);

		private readonly Dictionary<PeerMessage.MsgType, MessageHandler> _messageHandlersByType;

		public Peer() {
			_sendQueue = new CircularQueue<StampedPeerMessage>(64);
			_pendingOutgoingInputs = new CircularQueue<GameInput>(64);
			_peerConnectStatuses = new PeerMessage.ConnectStatus[PeerMessage.MAX_PLAYERS];
			_timeSync = new TimeSync();
			_eventQueue = new CircularQueue<Event>(64);
			_remoteStatuses = new PeerMessage.ConnectStatus[PeerMessage.MAX_PLAYERS];

			_localFrameAdvantage = 0;
			_remoteFrameAdvantage = 0;
			_queueID = -1;
			_magicNumber = 0;
			_remoteMagicNumber = 0;
			_packetsSent = 0;
			_bytesSent = 0;
			_statsStartTime = 0;
			_lastSendTime = 0;
			_shutdownTimeout = 0;
			_disconnectTimeout = 0;
			_disconnectNotifyStart = 0;
			_disconnectNotifySent = false;
			_disconnectEventSent = false;
			_connected = false;
			_nextSendSequenceNumber = 0;
			_nextReceiveSequenceNumber = 0;
			_transport = null;

			_lastSentInput.Init(-1, null, 1);
			_lastReceivedInput.Init(-1, null, 1);
			_lastAckedInput.Init(-1, null, 1);

			_state = default;

			for (int i = 0; i < _peerConnectStatuses.Length; i++) {
				_peerConnectStatuses[i] = default;
				_peerConnectStatuses[i].lastFrame = -1;
			}

			_peerAddress = default;
			_outOfOrderPacket = new OutOfOrderPacket(_queueID);
			_outOfOrderPacket.Clear();

			_messageHandlersByType = new Dictionary<PeerMessage.MsgType, MessageHandler> {
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
		public void Init(
			ref Transport transport,
			ref FrameUpdater frameUpdater,
			int queueID,
			IPEndPoint endPoint,
			PeerMessage.ConnectStatus[] statuses
		) {
			_transport = transport;
			_queueID = queueID;
			_localConnectStatuses = statuses;

			_peerAddress = endPoint;

			do {
				_magicNumber = (ushort) new Random().Next(
					0,
					ushort.MaxValue
				); // TODO this class should hold a Random type var
			} while (_magicNumber == 0);

			frameUpdater.Register(this);
		}

		public void Synchronize() {
			if (_transport == null) { return; }

			_currentState = State.Syncing;
			_state.sync.roundTripsRemaining = _NUM_SYNC_PACKETS;
			SendSyncRequest();
		}

		public bool GetPeerConnectStatus(int id, out int frame) {
			frame = _peerConnectStatuses[id].lastFrame;
			return !_peerConnectStatuses[id].isDisconnected;
		}

		public bool IsInitialized() {
			return _transport != null;
		}

		public bool IsSynchronized() {
			return _currentState == State.Running;
		}

		public bool IsRunning() {
			return _currentState == State.Running;
		}

		public void SendInput(GameInput input) {
			if (_transport == null) { return; }

			if (_currentState == State.Running) {
				// Check to see if this is a good time to adjust for the rift...
				_timeSync.AdvanceFrame(input, _localFrameAdvantage, _remoteFrameAdvantage);

				/*
				* Save this input packet
				*
				* XXX: This queue may fill up for spectators who do not ack input packets in a timely
				* manner.  When this happens, we can either resize the queue (ug) or disconnect them
				* (better, but still ug).  For the meantime, make this queue really big to decrease
				* the odds of this happening...
				*/
				_pendingOutgoingInputs.Push(input);
			}

			SendPendingOutput();
		}

		public void SendInputAck() {
			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.InputAck) {
				inputAck = {
					ackFrame = _lastReceivedInput.frame
				}
			};

			SendMsg(msg);
		}

		public bool DoesHandleMessageFromEndPoint(IPEndPoint from) {
			return _transport != null && _peerAddress.Equals(from);
		}

		public void OnMessageReceived(PeerMessage message) {
			// filter out messages that don't match what we expect
			ushort seq = message.header.sequenceNumber;
			if (message.header.type != PeerMessage.MsgType.SyncRequest
			    && message.header.type != PeerMessage.MsgType.SyncReply) {
				if (message.header.magicNumber != _remoteMagicNumber) {
					LogMsg("recv rejecting", message);
					return;
				}

				// filter out out-of-order packets
				ushort skipped = (ushort) (seq - _nextReceiveSequenceNumber);
				// Log("checking sequence number -> next - seq : %d - %d = %d{Environment.NewLine}", seq, _next_recv_seq, skipped);
				if (skipped > _MAX_SEQ_DISTANCE) {
					Log(
						$"dropping out of order packet (seq: {seq}, last seq:{_nextReceiveSequenceNumber}){Environment.NewLine}"
					);
					return;
				}
			}

			_nextReceiveSequenceNumber = seq;
			LogMsg("recv", message);
			if ((byte) message.header.type >= _messageHandlersByType.Count) {
				OnInvalid(message);
			} else if (_messageHandlersByType[message.header.type](message)) {
				_lastReceiveTime = Platform.GetCurrentTimeMS();

				bool canResumeIfNeeded = _disconnectNotifySent && _currentState == State.Running;
				if (canResumeIfNeeded) {
					Event evt = new Event(Event.Type.NetworkResumed);
					QueueEvent(evt);
					_disconnectNotifySent = false;
				}
			}
		}

		public void Disconnect() {
			_currentState = State.Disconnected;
			_shutdownTimeout = Platform.GetCurrentTimeMS() + _UDP_SHUTDOWN_TIMER;
		}

		public NetworkStats GetNetworkStats() {
			return new NetworkStats {
				network = {
					ping = _roundTripTime,
					sendQueueLength = _pendingOutgoingInputs.count,
					kbpsSent = _kbpsSent
				},
				timeSync = {
					remoteFramesBehind = _remoteFrameAdvantage,
					localFramesBehind = _localFrameAdvantage
				}
			};
		}

		public bool GetEvent(out Event evt) {
			if (_eventQueue.count == 0) {
				evt = default;
				return false;
			}

			evt = _eventQueue.Pop();

			return true;
		}

		public void SetLocalFrameNumber(int localFrame) {
			/*
			* Estimate which frame the other guy is one by looking at the
			* last frame they gave us plus some delta for the one-way packet
			* trip time.
			*/
			int remoteFrame = _lastReceivedInput.frame + _roundTripTime * 60 / 1000;

			/*
			* Our frame advantage is how many frames *behind* the other guy
			* we are.  Counter-intuitive, I know.  It's an advantage because
			* it means they'll have to predict more often and our moves will
			* pop more frequently.
			*/
			_localFrameAdvantage = remoteFrame - localFrame;
		}

		public int RecommendFrameDelay() {
			// XXX: require idle input should be a configuration parameter
			return _timeSync.recommend_frame_wait_duration(false);
		}

		public void SetDisconnectTimeout(uint timeout) {
			_disconnectTimeout = timeout;
		}

		public void SetDisconnectNotifyStart(uint timeout) {
			_disconnectNotifyStart = timeout;
		}

		private void UpdateNetworkStats() {
			long now = Platform.GetCurrentTimeMS();

			if (_statsStartTime == 0) {
				_statsStartTime = now;
			}

			int totalBytesSent = _bytesSent + _UDP_HEADER_SIZE * _packetsSent;
			float seconds = (float) ((now - _statsStartTime) / 1000.0);
			float bytesPerSecond = totalBytesSent / seconds;
			float udpOverhead = (float) (100.0 * (_UDP_HEADER_SIZE * _packetsSent) / _bytesSent);

			_kbpsSent = (int) (bytesPerSecond / 1024);

			Log(
				$"Network Stats -- "
				+ $"Bandwidth: {_kbpsSent:F} KBps   "
				+ $"Packets Sent: {_packetsSent:D5} ({(float) _packetsSent * 1000 / (now - _statsStartTime):F} pps)   "
				+ $"KB Sent: {totalBytesSent / 1024.0:F}    "
				+ $"UDP Overhead: {udpOverhead:F} %%.{Environment.NewLine}"
			);
		}

		private void QueueEvent(Event evt) {
			LogEvent("Queuing event", evt);
			_eventQueue.Push(evt);
		}

		private void ClearSendQueue() {
			while (_sendQueue.count > 0) {
				_sendQueue.Peek().msg = default;
				_sendQueue.Pop();
			}
		}

		// TODO revalidate these log wrappers as they may add some info to the string passed to global log
		private void Log(string msg) {
			Log(_queueID, msg);
		}

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
					Log(
						string.Format(
							"{0} game-compressed-input {1} (+ {2} bits).{3}",
							prefix,
							msg.input.startFrame,
							msg.input.numBits,
							Environment.NewLine
						)
					);
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
			_state.sync.random = (uint) (_random.Next(1, ushort.MaxValue) & 0xFFFF);

			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.SyncRequest) {
				syncRequest = {
					randomRequest = _state.sync.random
				}
			};

			SendMsg(msg);
		}

		private void SendMsg(PeerMessage msg) {
			LogMsg("send", msg);

			_packetsSent++;
			_lastSendTime = Platform.GetCurrentTimeMS();

			msg.header.magicNumber = _magicNumber;
			msg.header.sequenceNumber = _nextSendSequenceNumber++;

			_sendQueue.Push(new StampedPeerMessage(_peerAddress, msg));
			PumpSendQueue();
		}

		private void PumpSendQueue() {
			Random random = new Random(); // TODO pry move to more global scope...maybe?
			BinaryFormatter formatter = new BinaryFormatter(); // TODO same here
			while (_sendQueue.count > 0) {
				StampedPeerMessage entry = _sendQueue.Peek();

				if (_sendLatency != 0) {
					// should really come up with a gaussian distribution based on the configured
					// value, but this will do for now.

					// TODO cleanup rand
					int jitter = _sendLatency * 2 / 3 + random.Next(0, ushort.MaxValue) % _sendLatency / 3;
					if (Platform.GetCurrentTimeMS() < _sendQueue.Peek().creationTime + jitter) {
						break;
					}
				}

				bool shouldCreatePacket = !_outOfOrderPacket.RandomSet(entry);
				if (shouldCreatePacket) {
					Platform.Assert(!entry.destinationAddress.Address.Equals(IPAddress.None));

					using (MemoryStream ms = new MemoryStream()) {
						formatter.Serialize(ms, entry.msg);
						_bytesSent += _transport.SendTo(ms.ToArray(), (int) ms.Length, 0, entry.destinationAddress);
					} // TODO optimize/refactor

					entry.msg = default;
				}

				_sendQueue.Pop();
			}

			if (!_outOfOrderPacket.ShouldSendNow()) { return; }

			Log($"Sending rogue {nameof(OutOfOrderPacket)}!");

			using (MemoryStream ms = new MemoryStream()) {
				formatter.Serialize(ms, _outOfOrderPacket.message);
				_bytesSent += _transport.SendTo(ms.ToArray(), (int) ms.Length, 0, _outOfOrderPacket.destinationAddress);
			} // TODO optimize/refactor

			_outOfOrderPacket.Clear();
		}

		private unsafe void SendPendingOutput() {
			//LogUtil.Log($"SENDING {pendingOutgoingInputs.Count} PENDING INPUT(S) ----------------------------------------------------------{Environment.NewLine}"); // TODO remove when no longer needed
			int offset = 0;
			PeerMessage msg = new PeerMessage(PeerMessage.MsgType.Input);

			if (_pendingOutgoingInputs.count != 0) {
				GameInput lastInput = _lastAckedInput;
				GameInput outputQueueFront = _pendingOutgoingInputs.Peek();
				msg.input.startFrame = outputQueueFront.frame;
				msg.input.inputSize = (byte) outputQueueFront.size;

				Platform.Assert(
					lastInput.IsNull() || lastInput.frame + 1 == msg.input.startFrame,
					string.Format(
						"lastInput.IsNull() || lastInput.frame + 1 == msg.input.startFrame{0}{1} || {2} + 1 == {3}",
						Environment.NewLine,
						lastInput.IsNull(),
						lastInput.frame,
						msg.input.startFrame
					)
				);

				// set msg.input.bits (i.e. compressed outgoing input queue data)
				foreach (GameInput pendingInput in _pendingOutgoingInputs) {
					if (UnsafeUtility.MemCmp(pendingInput.bits, lastInput.bits, pendingInput.size) != 0) {
						Platform.Assert((GameInput.MAX_BYTES * GameInput.MAX_PLAYERS * 8) < (1 << BitVector.NIBBLE_SIZE));
						for (int i = 0; i < pendingInput.size * 8; i++) {
							Platform.Assert(i < (1 << BitVector.NIBBLE_SIZE));
							bool pendingInputBit = pendingInput[i];
							if (pendingInputBit != lastInput[i]) {
								BitVector.WriteBit(msg.input.bits, ref offset, true);
								BitVector.WriteBit(msg.input.bits, ref offset, pendingInputBit);
								BitVector.WriteNibblet(msg.input.bits, i, ref offset);
							}
						}
					}

					BitVector.WriteBit(msg.input.bits, ref offset, false);
					lastInput = _lastSentInput = pendingInput;
				}
			} else {
				msg.input.startFrame = 0;
				msg.input.inputSize = 0;
			}

			msg.input.ackFrame = _lastReceivedInput.frame;
			msg.input.numBits = (ushort) offset;

			msg.input.disconnectRequested = _currentState == State.Disconnected;

			for (int peerIndex = 0; peerIndex < PeerMessage.MAX_PLAYERS; peerIndex++) {
				msg.input.SetPeerConnectStatus(peerIndex, _localConnectStatuses?[peerIndex] ?? default);
			}

			Platform.Assert(offset < PeerMessage.MAX_COMPRESSED_BITS);

			SendMsg(msg);
		}

		private bool OnInvalid(PeerMessage incomingMessage) {
			Platform.AssertFailed($"Invalid {nameof(incomingMessage)} in {nameof(Peer)}.");
			return false;
		}

		private bool OnSyncRequest(PeerMessage incomingMessage) {
			if (_remoteMagicNumber != 0 && incomingMessage.header.magicNumber != _remoteMagicNumber) {
				Log(
					$"Ignoring sync request from unknown endpoint ({incomingMessage.header.magicNumber} != {_remoteMagicNumber}).{Environment.NewLine}"
				);

				return false;
			}

			PeerMessage reply = new PeerMessage(PeerMessage.MsgType.SyncReply) {
				syncReply = {
					randomReply = incomingMessage.syncRequest.randomRequest
				}
			};

			if (!_state.sync.hasStartedSyncing || _state.sync.startedInorganically) {
				if (!_state.sync.hasStartedSyncing) { _state.sync.roundTripsRemaining = _NUM_SYNC_PACKETS; }

				_state.sync.startedInorganically = true;

				PeerMessage syncReplyMessage = new PeerMessage(PeerMessage.MsgType.SyncReply) {
					header = {
						magicNumber = incomingMessage.header.magicNumber
					},
					syncReply = {
						randomReply = _state.sync.random
					}
				};

				OnSyncReply(syncReplyMessage);
			}

			SendMsg(reply);

			return true;
		}

		private bool OnSyncReply(PeerMessage incomingMessage) {
			if (_currentState != State.Syncing) {
				Log($"Ignoring SyncReply while not syncing.{Environment.NewLine}");

				return incomingMessage.header.magicNumber == _remoteMagicNumber;
			}

			if (incomingMessage.syncReply.randomReply != _state.sync.random) {
				Log(
					$"sync reply {incomingMessage.syncReply.randomReply} != {_state.sync.random}.  Keep looking...{Environment.NewLine}"
				);

				return false;
			}

			if (!_connected) {
				Event evt = new Event(Event.Type.Connected);
				QueueEvent(evt);
				_connected = true;
			}

			Log($"Checking sync state ({_state.sync.roundTripsRemaining} round trips remaining).{Environment.NewLine}");
			if (--_state.sync.roundTripsRemaining == 0) {
				Log($"Synchronized!{Environment.NewLine}");

				Event evt = new Event(Event.Type.Synchronized);
				QueueEvent(evt);
				_currentState = State.Running;
				_lastReceivedInput.frame = -1;
				_remoteMagicNumber = incomingMessage.header.magicNumber;
			} else {
				_state.sync.hasStartedSyncing = true;

				Event evt = new Event(Event.Type.Synchronizing) {
					synchronizing = {
						total = (int) _NUM_SYNC_PACKETS,
						count = (int) (_NUM_SYNC_PACKETS - _state.sync.roundTripsRemaining)
					}
				};

				QueueEvent(evt);
				if (!_state.sync.startedInorganically) { SendSyncRequest(); }
			}

			return true;
		}

		private unsafe bool OnInput(PeerMessage incomingMessage) {
			// If a disconnect is requested, go ahead and disconnect now.
			bool disconnectRequested = incomingMessage.input.disconnectRequested;
			if (disconnectRequested) {
				if (_currentState != State.Disconnected && !_disconnectEventSent) {
					Log($"Disconnecting endpoint on remote request.{Environment.NewLine}");
					QueueEvent(new Event(Event.Type.Disconnected));
					_disconnectEventSent = true;
				}
			} else {
				// Update the peer connection status if this peer is still considered to be part of the network.
				incomingMessage.input.GetConnectStatuses(ref _remoteStatuses);
				for (int i = 0; i < _peerConnectStatuses.Length; i++) {
					Platform.Assert(_remoteStatuses[i].lastFrame >= _peerConnectStatuses[i].lastFrame);

					_peerConnectStatuses[i].isDisconnected =
						_peerConnectStatuses[i].isDisconnected || _remoteStatuses[i].isDisconnected;

					_peerConnectStatuses[i].lastFrame = Math.Max(
						_peerConnectStatuses[i].lastFrame,
						_remoteStatuses[i].lastFrame
					);
				}
			}

			// Decompress the input.
			int lastReceivedFrameNumber = _lastReceivedInput.frame;
			if (incomingMessage.input.numBits != 0) {
				int offset = 0;
				byte* incomingBits = incomingMessage.input.bits;
				int numBits = incomingMessage.input.numBits;
				int currentFrame = incomingMessage.input.startFrame;

				_lastReceivedInput.size = incomingMessage.input.inputSize;
				if (_lastReceivedInput.frame < 0) {
					_lastReceivedInput.frame = incomingMessage.input.startFrame - 1;
				}

				while (offset < numBits) {
					/*
					* Keep walking through the frames (parsing bits) until we reach
					* the inputs for the frame right after the one we're on.
					*/
					Platform.Assert(currentFrame <= _lastReceivedInput.frame + 1);

					bool useInputs = currentFrame == _lastReceivedInput.frame + 1;

					while (BitVector.ReadBit(incomingBits, ref offset)) {
						bool isOn = BitVector.ReadBit(incomingBits, ref offset);
						int buttonBitIndex = BitVector.ReadNibblet(incomingBits, ref offset);

						if (useInputs) { _lastReceivedInput[buttonBitIndex] = isOn; }
					}

					Platform.Assert(offset <= numBits);

					// Now if we want to use these inputs, go ahead and send them to the emulator.
					if (useInputs) {
						// Move forward 1 frame in the stream.
						Platform.Assert(currentFrame == _lastReceivedInput.frame + 1);

						_lastReceivedInput.frame = currentFrame;

						// Send the event to the emulator
						Event evt = new Event(Event.Type.Input) {
							input = _lastReceivedInput
						};

						_state.running.lastInputPacketReceiveTime = Platform.GetCurrentTimeMS();

						Log(
							string.Format(
								"Sending frame {0} to emu queue {1} ({2}).{3}",
								_lastReceivedInput.frame,
								_queueID,
								_lastReceivedInput.Desc(),
								Environment.NewLine
							)
						);
						QueueEvent(evt);
					} else {
						Log(
							$"Skipping past frame:({currentFrame}) current is {_lastReceivedInput.frame}.{Environment.NewLine}"
						);
					}

					// Move forward 1 frame in the input stream.
					currentFrame++;
				}
			}

			Platform.Assert(_lastReceivedInput.frame >= lastReceivedFrameNumber);

			// Get rid of our buffered input
			while (_pendingOutgoingInputs.count != 0
			       && _pendingOutgoingInputs.Peek().frame < incomingMessage.input.ackFrame) {
				Log($"Throwing away pending output frame {_pendingOutgoingInputs.Peek().frame}{Environment.NewLine}");
				_lastAckedInput = _pendingOutgoingInputs.Pop();
			}

			return true;
		}

		private bool OnInputAck(PeerMessage incomingMessage) {
			// Get rid of our buffered input
			while (_pendingOutgoingInputs.count != 0
			       && _pendingOutgoingInputs.Peek().frame < incomingMessage.inputAck.ackFrame) {
				Log($"Throwing away pending output frame {_pendingOutgoingInputs.Peek().frame}{Environment.NewLine}");
				_lastAckedInput = _pendingOutgoingInputs.Pop();
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

			_remoteFrameAdvantage = incomingMessage.qualityReport.frameAdvantage;

			return true;
		}

		private bool OnQualityReply(PeerMessage incomingMessage) {
			_roundTripTime = (int) (Platform.GetCurrentTimeMS() - incomingMessage.qualityReply.pong);

			return true;
		}

		private bool OnKeepAlive(PeerMessage incomingMessage) {
			return true;
		}

		#region IUpdateHandler

		public virtual bool OnUpdate() {
			if (_transport == null) {
				return true;
			}

			long now = Platform.GetCurrentTimeMS();

			PumpSendQueue();
			switch (_currentState) {
				case State.Syncing:
					uint nextInterval = _state.sync.roundTripsRemaining == _NUM_SYNC_PACKETS
						? _SYNC_FIRST_RETRY_INTERVAL
						: _SYNC_RETRY_INTERVAL;
					if (_lastSendTime != 0 && _lastSendTime + nextInterval < now) {
						Log($"No luck syncing after {nextInterval} ms... Re-queueing sync packet.{Environment.NewLine}");
						SendSyncRequest();
					}

					break;

				case State.Running:
					// xxx: rig all this up with a timer wrapper
					if (_state.running.lastInputPacketReceiveTime == 0
					    || _state.running.lastInputPacketReceiveTime + _RUNNING_RETRY_INTERVAL < now) {
						Log(
							$"Haven't exchanged packets in a while (last received:{_lastReceivedInput.frame}  last sent:{_lastSentInput.frame}).  Resending.{Environment.NewLine}"
						);
						SendPendingOutput();
						_state.running.lastInputPacketReceiveTime = now;
					}

					if (_state.running.lastQualityReportTime == 0
					    || _state.running.lastQualityReportTime + _QUALITY_REPORT_INTERVAL < now) {
						PeerMessage msg = new PeerMessage(PeerMessage.MsgType.QualityReport) {
							qualityReport = {
								ping = Platform.GetCurrentTimeMS(), frameAdvantage = (sbyte) _localFrameAdvantage
							}
						};

						SendMsg(msg);
						_state.running.lastQualityReportTime = now;
					}

					if (_state.running.lastNetworkStatsInterval == 0
					    || _state.running.lastNetworkStatsInterval + _NETWORK_STATS_INTERVAL < now) {
						UpdateNetworkStats();
						_state.running.lastNetworkStatsInterval = now;
					}

					if (_lastSendTime != 0 && _lastSendTime + _KEEP_ALIVE_INTERVAL < now) {
						Log($"Sending keep alive packet{Environment.NewLine}");

						PeerMessage peerMsg = new PeerMessage(PeerMessage.MsgType.KeepAlive);
						SendMsg(peerMsg);
					}

					if (_disconnectTimeout != 0
					    && _disconnectNotifyStart != 0
					    && !_disconnectNotifySent
					    && _lastReceiveTime + _disconnectNotifyStart < now) {
						Log(
							$"Endpoint has stopped receiving packets for {_disconnectNotifyStart} ms.  Sending notification.{Environment.NewLine}"
						);
						Event e = new Event(Event.Type.NetworkInterrupted) {
							network_interrupted = {
								disconnectTimeout = (int) (_disconnectTimeout - _disconnectNotifyStart)
							}
						};

						QueueEvent(e);
						_disconnectNotifySent = true;
					}

					if (_disconnectTimeout != 0 && _lastReceiveTime + _disconnectTimeout < now) {
						if (!_disconnectEventSent) {
							Log(
								$"Endpoint has stopped receiving packets for {_disconnectTimeout} ms.  Disconnecting.{Environment.NewLine}"
							);

							Event evt = new Event(Event.Type.Disconnected);
							QueueEvent(evt);
							_disconnectEventSent = true;
						}
					}

					break;

				case State.Disconnected:
					if (_shutdownTimeout < now) {
						Log($"Shutting down udp connection.{Environment.NewLine}");
						_transport = null;
						_shutdownTimeout = 0;
					}

					break;
			}

			return true;
		}

		#endregion

		// TODO rename once perf tools are up
		public struct Stats {
			public readonly int ping;
			public readonly int remoteFrameAdvantage;
			public readonly int localFrameAdvantage;
			public readonly int sendQueueLen;
			public readonly Transport.Stats udp;
		}

		[StructLayout(LayoutKind.Explicit)]
		public struct Event {
			[FieldOffset(0)] public readonly Type type;
			[FieldOffset(4)] public GameInput input;
			[FieldOffset(4)] public Synchronizing synchronizing;
			[FieldOffset(4)] public NetworkInterrupted network_interrupted;

			public struct Synchronizing {
				public int total { get; set; }
				public int count { get; set; }
			}

			public struct NetworkInterrupted {
				public int disconnectTimeout { get; set; }
			}

			public Event(Type t = Type.Unknown)
				: this() {
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
				NetworkResumed
			}
		}

		// TODO should go in debug define
		private struct OutOfOrderPacket {
			private static readonly int _outOfOrderPacketPercentage = Platform.GetConfigInt("ggpo.oop.percent");

			public IPEndPoint destinationAddress { get; private set; }

			public PeerMessage message => _message ?? default;
			private PeerMessage? _message;

			private readonly int _queueID;
			private long _sendTime;

			public OutOfOrderPacket(int queueID)
				: this() {
				_queueID = queueID;
			}

			public bool RandomSet(StampedPeerMessage entry) {
				bool shouldSet = _outOfOrderPacketPercentage > 0
				                 && _message == null
				                 && _random.Next(0, ushort.MaxValue) % 100 < _outOfOrderPacketPercentage;

				if (shouldSet) {
					int delay = _random.Next(0, ushort.MaxValue) % (_sendLatency * 10 + 1000);
					_sendTime = Platform.GetCurrentTimeMS() + delay;
					_message = entry.msg;
					destinationAddress = entry.destinationAddress;

					Log(
						_queueID,
						$"creating rogue oop (seq: {entry.msg.header.sequenceNumber}  delay: {delay}){Environment.NewLine}"
					);
				}

				return shouldSet;
			}

			public bool ShouldSendNow() {
				return _message != null && _sendTime < Platform.GetCurrentTimeMS();
			}

			public void Clear() {
				_message = null;
			}
		}

		private enum State {
			Syncing,
			Synchronized,
			Running,
			Disconnected
		}

		private struct StampedPeerMessage {
			public readonly long creationTime;
			public readonly IPEndPoint destinationAddress;
			public PeerMessage msg;

			public StampedPeerMessage(IPEndPoint destinationAddress, PeerMessage msg)
				: this() {
				creationTime = Platform.GetCurrentTimeMS();
				this.destinationAddress = destinationAddress;
				this.msg = msg;
			}
		}

		[StructLayout(LayoutKind.Explicit)]
		public struct StateUnion {
			[FieldOffset(0)] public Sync sync;
			[FieldOffset(0)] public Running running;

			public struct Sync {
				public uint roundTripsRemaining { get; set; }
				public uint random { get; set; } // last acked random? TODO
				public bool hasStartedSyncing;
				public bool startedInorganically;
			}

			public struct Running {
				public long lastQualityReportTime { get; set; }
				public long lastNetworkStatsInterval { get; set; }
				public long lastInputPacketReceiveTime { get; set; }
			}
		}
	}
}