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
		
		protected GGPOSessionCallbacks _callbacks;
		protected Poll _poll;
		protected UDP _udp;
		protected UDPProtocol _host;
		protected bool _synchronizing;
		protected int _input_size;
		protected int _num_players;
		protected int _next_input_to_send;
		protected GameInput[] _inputs = new GameInput[SPECTATOR_FRAME_BUFFER_SIZE];

		public SpectatorBackend(
			ref GGPOSessionCallbacks cb,
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
			_callbacks.begin_game(gamename);
		}

		public override GGPOErrorCode DoPoll(int timeout) {
			_poll.Pump(0);

			PollUdpProtocolEvents();
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode AddPlayer(ref GGPOPlayer player, out GGPOPlayerHandle handle) {
			handle = new GGPOPlayerHandle(-1);
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public override GGPOErrorCode AddLocalInput(GGPOPlayerHandle player, object value, int size) {
			return GGPOErrorCode.GGPO_OK;
		}

		// TODO nullable int should probably go...
		public override unsafe GGPOErrorCode SyncInput(ref Array values, int size, ref int? disconnect_flags) {
			// Wait until we've started to return inputs.
			if (_synchronizing) {
				return GGPOErrorCode.GGPO_ERRORCODE_NOT_SYNCHRONIZED;
			}

			GameInput input = _inputs[_next_input_to_send % SPECTATOR_FRAME_BUFFER_SIZE];
			if (input.frame < _next_input_to_send) {
				// Haven't received the input from the host yet.  Wait
				return GGPOErrorCode.GGPO_ERRORCODE_PREDICTION_THRESHOLD;
			}
			if (input.frame > _next_input_to_send) {
				// The host is way way way far ahead of the spectator.  How'd this
				// happen?  Anyway, the input we need is gone forever.
				return GGPOErrorCode.GGPO_ERRORCODE_GENERAL_FAILURE;
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

			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode IncrementFrame() {
			Log($"End of frame ({_next_input_to_send - 1})...\n");
			DoPoll(0);
			PollUdpProtocolEvents();

			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode DisconnectPlayer(GGPOPlayerHandle handle) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public override GGPOErrorCode GetNetworkStats(out GGPONetworkStats stats, GGPOPlayerHandle handle) {
			stats = default;
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public override GGPOErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public override GGPOErrorCode SetDisconnectTimeout(uint timeout) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public override GGPOErrorCode SetDisconnectNotifyStart(uint timeout) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
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
					info.code = GGPOEventCode.GGPO_EVENTCODE_CONNECTED_TO_PEER;
					info.connected.player = new GGPOPlayerHandle(0);
					_callbacks.on_event(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronizing: {
					info.code = GGPOEventCode.GGPO_EVENTCODE_SYNCHRONIZING_WITH_PEER;
					info.synchronizing.player = new GGPOPlayerHandle(0);
					info.synchronizing.count = evt.synchronizing.count;
					info.synchronizing.total = evt.synchronizing.total;
					_callbacks.on_event(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Synchronized: {
					if (_synchronizing) {
						info.code = GGPOEventCode.GGPO_EVENTCODE_SYNCHRONIZED_WITH_PEER;
						info.synchronized.player = new GGPOPlayerHandle(0);
						_callbacks.on_event(ref info);

						info.code = GGPOEventCode.GGPO_EVENTCODE_RUNNING;
						_callbacks.on_event(ref info);
						_synchronizing = false;
					}
					break;
				}
				case UDPProtocol.Event.Type.NetworkInterrupted: {
					info.code = GGPOEventCode.GGPO_EVENTCODE_CONNECTION_INTERRUPTED;
					info.connection_interrupted.player = new GGPOPlayerHandle(0);
					info.connection_interrupted.disconnect_timeout = evt.network_interrupted.disconnect_timeout;
					_callbacks.on_event(ref info);
					break;
				}
				case UDPProtocol.Event.Type.NetworkResumed: {
					info.code = GGPOEventCode.GGPO_EVENTCODE_CONNECTION_RESUMED;
					info.connection_resumed.player = new GGPOPlayerHandle(0);
					_callbacks.on_event(ref info);
					break;
				}
				case UDPProtocol.Event.Type.Disconnected: {
					info.code = GGPOEventCode.GGPO_EVENTCODE_DISCONNECTED_FROM_PEER;
					info.disconnected.player = new GGPOPlayerHandle(0);
					_callbacks.on_event(ref info);
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