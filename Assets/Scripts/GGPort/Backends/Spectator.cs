/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Net;

namespace GGPort {
	public class SpectatorBackend : GGPOSession, IPollSink, UDP.Callbacks {
		public const int SPECTATOR_FRAME_BUFFER_SIZE = 64;
		
		protected SessionCallbacks _callbacks;
		protected Poll _poll;
		protected UDP _udp;
		protected UDPProtocol _host;
		protected bool _synchronizing;
		protected int _input_size;
		protected int _num_players;
		protected int _next_input_to_send;
		protected GameInput[] _inputs = new GameInput[SPECTATOR_FRAME_BUFFER_SIZE];

		public SpectatorBackend(
			ref SessionCallbacks cb,
			string gamename,
			ushort localport,
			int num_players,
			int input_size,
			IPEndPoint hostEndPoint
		) {
			_num_players = num_players;
			_input_size = input_size;
			_next_input_to_send = 0;
				
			_callbacks = cb;
			_synchronizing = true;

			for (int i = 0; i < _inputs.Length; i++) {
				_inputs[i].frame = -1;
			}

			// Initialize the UDP port
			_udp.Init(localport, ref _poll, this);

			// Init the host endpoint
			_host.Init(ref _udp, ref _poll, 0, hostEndPoint, null);
			_host.Synchronize();

			// Preload the ROM
			_callbacks.BeginGame(gamename);
		}

		public override ErrorCode DoPoll(int timeout) {
			_poll.Pump(0);

			PollUdpProtocolEvents();
			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(ref GGPOPlayer player, out GGPOPlayerHandle handle) {
			handle = new GGPOPlayerHandle(-1);
			return ErrorCode.Unsupported;
		}

		public override ErrorCode AddLocalInput(GGPOPlayerHandle player, byte[] value, int size) {
			return ErrorCode.Success;
		}

		// TODO nullable int should probably go...
		public override unsafe ErrorCode SyncInput(ref Array values, int size, ref int? disconnect_flags) {
			// Wait until we've started to return inputs.
			if (_synchronizing) {
				return ErrorCode.NotSynchronized;
			}

			GameInput input = _inputs[_next_input_to_send % SPECTATOR_FRAME_BUFFER_SIZE];
			if (input.frame < _next_input_to_send) {
				// Haven't received the input from the host yet.  Wait
				return ErrorCode.PredictionThreshold;
			}
			if (input.frame > _next_input_to_send) {
				// The host is way way way far ahead of the spectator.  How'd this
				// happen?  Anyway, the input we need is gone forever.
				return ErrorCode.GeneralFailure;
			}

			Platform.ASSERT(size >= _input_size * _num_players);

			int valuesSizeInBytes = _input_size * _num_players;
			for (int i = 0; i < valuesSizeInBytes; i++) {
				Buffer.SetByte(values, i, input.bits[i]);
			}
			
			if (disconnect_flags != null) {
				disconnect_flags = 0; // xxx: should get them from the host!
			}
			
			_next_input_to_send++;

			_inputs[_next_input_to_send % SPECTATOR_FRAME_BUFFER_SIZE] = input;

			return ErrorCode.Success;
		}

		public override ErrorCode IncrementFrame() {
			Log($"End of frame ({_next_input_to_send - 1})...{Environment.NewLine}");
			DoPoll(0);
			PollUdpProtocolEvents();

			return ErrorCode.Success;
		}

		public override ErrorCode DisconnectPlayer(GGPOPlayerHandle handle) {
			return ErrorCode.Unsupported;
		}

		public override ErrorCode GetNetworkStats(out GGPONetworkStats stats, GGPOPlayerHandle handle) {
			stats = default;
			return ErrorCode.Unsupported;
		}

		public override ErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay) {
			return ErrorCode.Unsupported;
		}

		public override ErrorCode SetDisconnectTimeout(uint timeout) {
			return ErrorCode.Unsupported;
		}

		public override ErrorCode SetDisconnectNotifyStart(uint timeout) {
			return ErrorCode.Unsupported;
		}

		public virtual void OnMsg(IPEndPoint from, ref UDPMessage msg, int len) {
			if (_host.HandlesMsg(ref from, ref msg)) {
				_host.OnMsg(ref msg, len);
			}
		}

		protected void PollUdpProtocolEvents() {
			while (_host.GetEvent(out UDPProtocol.Event evt)) {
				OnUdpProtocolEvent(ref evt);
			}
		}

		protected void OnUdpProtocolEvent(ref UDPProtocol.Event evt) {
			GGPOEvent info = new GGPOEvent();
			
			switch (evt.type) {
				case UDPProtocol.Event.Type.Connected: {
					info.code = GGPOEventCode.ConnectedToPeer;
					info.connected.player = new GGPOPlayerHandle(0);
					_callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronizing: {
					info.code = GGPOEventCode.SynchronizingWithPeer;
					info.synchronizing.player = new GGPOPlayerHandle(0);
					info.synchronizing.count = evt.synchronizing.count;
					info.synchronizing.total = evt.synchronizing.total;
					_callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronized: {
					if (_synchronizing) {
						info.code = GGPOEventCode.SynchronizedWithPeer;
						info.synchronized.player = new GGPOPlayerHandle(0);
						_callbacks.OnEvent(ref info);

						info.code = GGPOEventCode.Running;
						_callbacks.OnEvent(ref info);
						_synchronizing = false;
					}
					break;
				}
				case UDPProtocol.Event.Type.NetworkInterrupted: {
					info.code = GGPOEventCode.ConnectionInterrupted;
					info.connectionInterrupted.player = new GGPOPlayerHandle(0);
					info.connectionInterrupted.disconnect_timeout = evt.network_interrupted.disconnect_timeout;
					_callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.NetworkResumed: {
					info.code = GGPOEventCode.ConnectionResumed;
					info.connectionResumed.player = new GGPOPlayerHandle(0);
					_callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Disconnected: {
					info.code = GGPOEventCode.DisconnectedFromPeer;
					info.disconnected.player = new GGPOPlayerHandle(0);
					_callbacks.OnEvent(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Input: {
					GameInput input = evt.input.input;

					_host.SetLocalFrameNumber(input.frame);
					_host.SendInputAck();
					_inputs[input.frame % SPECTATOR_FRAME_BUFFER_SIZE] = input;

					evt.input.input = input; // TODO necessary? I added this
					break;
				}
			}
		}
		
		// TODO fix param names, 4 fxns
		public virtual bool OnHandlePoll(object TODO) { return true; }
		public virtual bool OnMsgPoll(object TODO) { return true; }
		public virtual bool OnPeriodicPoll(object TODO0, long TODO1) { return true; }
		public virtual bool OnLoopPoll(object cookie) { return true; }
	}
}