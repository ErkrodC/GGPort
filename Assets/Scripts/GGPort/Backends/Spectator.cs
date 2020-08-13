/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Net;

namespace GGPort {
	public class SpectatorBackend<TGameState> : Session<TGameState>, IPollSink {
		private const int _SPECTATOR_FRAME_BUFFER_SIZE = 64;

		private readonly Poll _poll;
		private readonly Transport _transport;
		private readonly Peer _host;
		private readonly int _inputSize;
		private readonly int _numPlayers;
		private readonly GameInput[] _inputs;
		private bool _isSynchronizing;
		private int _nextInputToSend;

		public SpectatorBackend(
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
			int inputSize,
			IPEndPoint hostEndPoint
		)
			: base(
				beginGameCallback,
				saveGameStateCallback,
				loadGameStateCallback,
				logGameStateCallback,
				freeBufferCallback,
				advanceFrameCallback,
				onEventCallback,
				logTextCallback
			) {
			_numPlayers = numPlayers;
			_inputSize = inputSize;
			_nextInputToSend = 0;

			_isSynchronizing = true;

			_inputs = new GameInput[_SPECTATOR_FRAME_BUFFER_SIZE];
			for (int i = 0; i < _inputs.Length; i++) { _inputs[i].frame = -1; }

			// Initialize the UDP port
			_poll = new Poll();
			_transport = new Transport(localPort, _poll);
			_transport.messageReceivedEvent += OnMessageReceived;

			// Init the host endpoint
			_host = new Peer();
			_host.Init(ref _transport, ref _poll, 0, hostEndPoint, null); // TODO maybe remove init and set with ctor?
			_host.Synchronize();

			// Preload the ROM
			beginGameEvent?.Invoke(gameName);
		}

		public override ErrorCode Idle(int timeout) {
			_poll.Pump(0);

			PollUdpProtocolEvents();
			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(Player player, out PlayerHandle handle) {
			handle = new PlayerHandle(-1);
			return ErrorCode.Unsupported;
		}

		public override ErrorCode AddLocalInput(PlayerHandle player, byte[] value, int size) {
			return ErrorCode.Success;
		}

		public override unsafe ErrorCode SynchronizeInput(Array values, int size, ref int disconnectFlags) {
			// Wait until we've started to return inputs.
			if (_isSynchronizing) {
				return ErrorCode.NotSynchronized;
			}

			GameInput input = _inputs[_nextInputToSend % _SPECTATOR_FRAME_BUFFER_SIZE];
			if (input.frame < _nextInputToSend) {
				// Haven't received the input from the host yet.  Wait
				return ErrorCode.PredictionThreshold;
			}

			if (input.frame > _nextInputToSend) {
				// The host is way way way far ahead of the spectator.  How'd this
				// happen?  Anyway, the input we need is gone forever.
				return ErrorCode.GeneralFailure;
			}

			Platform.Assert(size >= _inputSize * _numPlayers);

			int valuesSizeInBytes = _inputSize * _numPlayers;
			for (int i = 0; i < valuesSizeInBytes; i++) {
				Buffer.SetByte(values, i, input.bits[i]);
			}

			disconnectFlags = 0; // xxx: should get them from the host!

			_nextInputToSend++;

			_inputs[_nextInputToSend % _SPECTATOR_FRAME_BUFFER_SIZE] = input;

			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			LogUtil.Log($"End of frame ({_nextInputToSend - 1})...{Environment.NewLine}");
			Idle(0);
			PollUdpProtocolEvents();

			return ErrorCode.Success;
		}

		public override ErrorCode DisconnectPlayer(PlayerHandle handle) {
			return ErrorCode.Unsupported;
		}

		public override ErrorCode GetNetworkStats(out NetworkStats stats, PlayerHandle handle) {
			stats = default;
			return ErrorCode.Unsupported;
		}

		public override ErrorCode SetFrameDelay(PlayerHandle player, int frameDelay) {
			return ErrorCode.Unsupported;
		}

		public override ErrorCode SetDisconnectTimeout(uint timeout) {
			return ErrorCode.Unsupported;
		}

		public override ErrorCode SetDisconnectNotifyStart(uint timeout) {
			return ErrorCode.Unsupported;
		}

		public virtual void OnMessageReceived(IPEndPoint from, PeerMessage msg) {
			if (_host.DoesHandleMessageFromEndPoint(from)) {
				_host.OnMessageReceived(msg);
			}
		}

		protected void PollUdpProtocolEvents() {
			while (_host.GetEvent(out Peer.Event evt)) {
				OnUdpProtocolEvent(ref evt);
			}
		}

		protected void OnUdpProtocolEvent(ref Peer.Event evt) {
			EventData info = new EventData();

			switch (evt.type) {
				case Peer.Event.Type.Connected: {
					info.code = EventCode.ConnectedToPeer;
					info.connected.player = new PlayerHandle(0);
					onEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronizing: {
					info.code = EventCode.SynchronizingWithPeer;
					info.synchronizing.player = new PlayerHandle(0);
					info.synchronizing.count = evt.synchronizing.count;
					info.synchronizing.total = evt.synchronizing.total;
					onEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronized: {
					if (_isSynchronizing) {
						info.code = EventCode.SynchronizedWithPeer;
						info.synchronized.player = new PlayerHandle(0);
						onEventEvent?.Invoke(info);

						info.code = EventCode.Running;
						onEventEvent?.Invoke(info);
						_isSynchronizing = false;
					}

					break;
				}
				case Peer.Event.Type.NetworkInterrupted: {
					info.code = EventCode.ConnectionInterrupted;
					info.connectionInterrupted.player = new PlayerHandle(0);
					info.connectionInterrupted.disconnectTimeout = evt.network_interrupted.disconnectTimeout;
					onEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.NetworkResumed: {
					info.code = EventCode.ConnectionResumed;
					info.connectionResumed.player = new PlayerHandle(0);
					onEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Disconnected: {
					info.code = EventCode.DisconnectedFromPeer;
					info.disconnected.player = new PlayerHandle(0);
					onEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Input: {
					GameInput input = evt.input;

					_host.SetLocalFrameNumber(input.frame);
					_host.SendInputAck();
					_inputs[input.frame % _SPECTATOR_FRAME_BUFFER_SIZE] = input;
					break;
				}
			}
		}

		public virtual bool OnHandlePoll(object cookie) {
			return true;
		}

		public virtual bool OnMsgPoll(object cookie) {
			return true;
		}

		public virtual bool OnPeriodicPoll(object cookie, long lastFireTime) {
			return true;
		}

		public virtual bool OnLoopPoll(object cookie) {
			return true;
		}
	}
}