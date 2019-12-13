/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Net;

namespace GGPort {
	public class SpectatorBackend : Session, IPollSink, Transport.ICallbacks {
		private const int kSpectatorFrameBufferSize = 64;

		private readonly SessionCallbacks callbacks;
		private readonly Poll poll;
		private readonly Transport transport;
		private readonly Peer host;
		private readonly int inputSize;
		private readonly int numPlayers;
		private readonly GameInput[] inputs;
		private bool isSynchronizing;
		private int nextInputToSend;

		public SpectatorBackend(
			SessionCallbacks cb,
			string gameName,
			ushort localPort,
			int numPlayers,
			int inputSize,
			IPEndPoint hostEndPoint
		) {
			this.numPlayers = numPlayers;
			this.inputSize = inputSize;
			nextInputToSend = 0;
				
			callbacks = cb;
			isSynchronizing = true;

			inputs = new GameInput[kSpectatorFrameBufferSize];
			for (int i = 0; i < inputs.Length; i++) {
				inputs[i].Frame = -1;
			}

			// Initialize the UDP port
			transport.Init(localPort, poll, this);

			// Init the host endpoint
			host = new Peer();
			host.Init(ref transport, ref poll, 0, hostEndPoint, null); // TODO maybe remove init and set with ctor?
			host.Synchronize();

			// Preload the ROM
			callbacks.BeginGame(gameName);
		}

		public override ErrorCode Idle(int timeout) {
			poll.Pump(0);

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
			if (isSynchronizing) {
				return ErrorCode.NotSynchronized;
			}

			GameInput input = inputs[nextInputToSend % kSpectatorFrameBufferSize];
			if (input.Frame < nextInputToSend) {
				// Haven't received the input from the host yet.  Wait
				return ErrorCode.PredictionThreshold;
			}
			if (input.Frame > nextInputToSend) {
				// The host is way way way far ahead of the spectator.  How'd this
				// happen?  Anyway, the input we need is gone forever.
				return ErrorCode.GeneralFailure;
			}

			Platform.Assert(size >= inputSize * numPlayers);

			int valuesSizeInBytes = inputSize * numPlayers;
			for (int i = 0; i < valuesSizeInBytes; i++) {
				Buffer.SetByte(values, i, input.Bits[i]);
			}
			
			disconnectFlags = 0; // xxx: should get them from the host!
			
			nextInputToSend++;

			inputs[nextInputToSend % kSpectatorFrameBufferSize] = input;

			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			LogUtil.Log($"End of frame ({nextInputToSend - 1})...{Environment.NewLine}");
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

		public virtual void OnMsg(IPEndPoint from, PeerMessage msg) {
			if (host.HandlesMsg(from)) {
				host.OnMsg(msg);
			}
		}

		protected void PollUdpProtocolEvents() {
			while (host.GetEvent(out Peer.Event evt)) {
				OnUdpProtocolEvent(ref evt);
			}
		}

		protected void OnUdpProtocolEvent(ref Peer.Event evt) {
			Event info = new Event();
			
			switch (evt.type) {
				case Peer.Event.Type.Connected: {
					info.code = EventCode.ConnectedToPeer;
					info.connected.player = new PlayerHandle(0);
					callbacks.OnEvent(info);
					break;
				}
				case Peer.Event.Type.Synchronizing: {
					info.code = EventCode.SynchronizingWithPeer;
					info.synchronizing.player = new PlayerHandle(0);
					info.synchronizing.count = evt.synchronizing.Count;
					info.synchronizing.total = evt.synchronizing.Total;
					callbacks.OnEvent(info);
					break;
				}
				case Peer.Event.Type.Synchronized: {
					if (isSynchronizing) {
						info.code = EventCode.SynchronizedWithPeer;
						info.synchronized.player = new PlayerHandle(0);
						callbacks.OnEvent(info);

						info.code = EventCode.Running;
						callbacks.OnEvent(info);
						isSynchronizing = false;
					}
					break;
				}
				case Peer.Event.Type.NetworkInterrupted: {
					info.code = EventCode.ConnectionInterrupted;
					info.connectionInterrupted.player = new PlayerHandle(0);
					info.connectionInterrupted.disconnect_timeout = evt.network_interrupted.DisconnectTimeout;
					callbacks.OnEvent(info);
					break;
				}
				case Peer.Event.Type.NetworkResumed: {
					info.code = EventCode.ConnectionResumed;
					info.connectionResumed.player = new PlayerHandle(0);
					callbacks.OnEvent(info);
					break;
				}
				case Peer.Event.Type.Disconnected: {
					info.code = EventCode.DisconnectedFromPeer;
					info.disconnected.player = new PlayerHandle(0);
					callbacks.OnEvent(info);
					break;
				}
				case Peer.Event.Type.Input: {
					GameInput input = evt.input;

					host.SetLocalFrameNumber(input.Frame);
					host.SendInputAck();
					inputs[input.Frame % kSpectatorFrameBufferSize] = input;
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