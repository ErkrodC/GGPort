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
		private const int SPECTATOR_FRAME_BUFFER_SIZE = 64;
		
		private readonly Poll m_poll;
		private readonly Transport m_transport;
		private readonly Peer m_host;
		private readonly int m_inputSize;
		private readonly int m_numPlayers;
		private readonly GameInput[] m_inputs;
		private bool m_isSynchronizing;
		private int m_nextInputToSend;

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
		) : base(
			beginGameCallback,
			saveGameStateCallback,
			loadGameStateCallback,
			logGameStateCallback,
			freeBufferCallback,
			advanceFrameCallback,
			onEventCallback,
			logTextCallback
		) {
			m_numPlayers = numPlayers;
			m_inputSize = inputSize;
			m_nextInputToSend = 0;
			
			m_isSynchronizing = true;

			m_inputs = new GameInput[SPECTATOR_FRAME_BUFFER_SIZE];
			for (int i = 0; i < m_inputs.Length; i++) { m_inputs[i].Frame = -1; }

			// Initialize the UDP port
			m_poll = new Poll();
			m_transport = new Transport(localPort, m_poll, OnMessageReceived);

			// Init the host endpoint
			m_host = new Peer();
			m_host.Init(ref m_transport, ref m_poll, 0, hostEndPoint, null); // TODO maybe remove init and set with ctor?
			m_host.Synchronize();

			// Preload the ROM
			BeginGameEvent?.Invoke(gameName);
		}

		public override ErrorCode Idle(int timeout) {
			m_poll.Pump(0);

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
			if (m_isSynchronizing) {
				return ErrorCode.NotSynchronized;
			}

			GameInput input = m_inputs[m_nextInputToSend % SPECTATOR_FRAME_BUFFER_SIZE];
			if (input.Frame < m_nextInputToSend) {
				// Haven't received the input from the host yet.  Wait
				return ErrorCode.PredictionThreshold;
			}
			if (input.Frame > m_nextInputToSend) {
				// The host is way way way far ahead of the spectator.  How'd this
				// happen?  Anyway, the input we need is gone forever.
				return ErrorCode.GeneralFailure;
			}

			Platform.Assert(size >= m_inputSize * m_numPlayers);

			int valuesSizeInBytes = m_inputSize * m_numPlayers;
			for (int i = 0; i < valuesSizeInBytes; i++) {
				Buffer.SetByte(values, i, input.Bits[i]);
			}
			
			disconnectFlags = 0; // xxx: should get them from the host!
			
			m_nextInputToSend++;

			m_inputs[m_nextInputToSend % SPECTATOR_FRAME_BUFFER_SIZE] = input;

			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			LogUtil.Log($"End of frame ({m_nextInputToSend - 1})...{Environment.NewLine}");
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
			if (m_host.HandlesMsg(from)) {
				m_host.OnMsg(msg);
			}
		}

		protected void PollUdpProtocolEvents() {
			while (m_host.GetEvent(out Peer.Event evt)) {
				OnUdpProtocolEvent(ref evt);
			}
		}

		protected void OnUdpProtocolEvent(ref Peer.Event evt) {
			Event info = new Event();
			
			switch (evt.type) {
				case Peer.Event.Type.Connected: {
					info.code = EventCode.ConnectedToPeer;
					info.connected.player = new PlayerHandle(0);
					OnEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronizing: {
					info.code = EventCode.SynchronizingWithPeer;
					info.synchronizing.player = new PlayerHandle(0);
					info.synchronizing.count = evt.synchronizing.Count;
					info.synchronizing.total = evt.synchronizing.Total;
					OnEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Synchronized: {
					if (m_isSynchronizing) {
						info.code = EventCode.SynchronizedWithPeer;
						info.synchronized.player = new PlayerHandle(0);
						OnEventEvent?.Invoke(info);

						info.code = EventCode.Running;
						OnEventEvent?.Invoke(info);
						m_isSynchronizing = false;
					}
					break;
				}
				case Peer.Event.Type.NetworkInterrupted: {
					info.code = EventCode.ConnectionInterrupted;
					info.connectionInterrupted.player = new PlayerHandle(0);
					info.connectionInterrupted.disconnect_timeout = evt.network_interrupted.DisconnectTimeout;
					OnEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.NetworkResumed: {
					info.code = EventCode.ConnectionResumed;
					info.connectionResumed.player = new PlayerHandle(0);
					OnEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Disconnected: {
					info.code = EventCode.DisconnectedFromPeer;
					info.disconnected.player = new PlayerHandle(0);
					OnEventEvent?.Invoke(info);
					break;
				}
				case Peer.Event.Type.Input: {
					GameInput input = evt.input;

					m_host.SetLocalFrameNumber(input.Frame);
					m_host.SendInputAck();
					m_inputs[input.Frame % SPECTATOR_FRAME_BUFFER_SIZE] = input;
					break;
				}
			}
		}
		
		public virtual bool OnHandlePoll(object cookie) { return true; }
		public virtual bool OnMsgPoll(object cookie) { return true; }
		public virtual bool OnPeriodicPoll(object cookie, long lastFireTime) { return true; }
		public virtual bool OnLoopPoll(object cookie) { return true; }
	}
}