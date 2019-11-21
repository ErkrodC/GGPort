/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Runtime.InteropServices;

#pragma pack(push, 1)

namespace GGPort {
	[StructLayout(LayoutKind.Explicit)]
	public struct UdpMsg {
		public const ushort MAX_COMPRESSED_BITS = 4096;
		public const byte UDP_MSG_MAX_PLAYERS = 4;
		
		[FieldOffset(0)] public readonly HDR hdr;
		[FieldOffset(5)] public readonly SyncRequest sync_request;
		[FieldOffset(5)] public readonly SyncReply sync_reply;
		[FieldOffset(5)] public readonly QualityReport quality_report;
		[FieldOffset(5)] public readonly QualityReply quality_reply;
		[FieldOffset(5)] public readonly Input input;
		[FieldOffset(5)] public readonly InputAck input_ack;
		
		public struct SyncRequest {
			public readonly uint random_request; /* please reply back with this random data */
			public readonly ushort remote_magic;
			public readonly byte remote_endpoint;
		}
		
		public struct SyncReply {
			public readonly uint random_reply; /* OK, here's your random data back */
		}
		
		public struct QualityReport {
			public readonly sbyte frame_advantage; /* what's the other guy's frame advantage? */
			public readonly uint ping;
		}
		
		public struct QualityReply {
			public readonly uint pong;
		}

		public unsafe struct Input {
			// TODO connect_status fields should be here to create fixed size array of them, then make a getter for this prop using a single index
			public fixed connect_status peer_connect_status[UDP_MSG_MAX_PLAYERS];

			public readonly uint start_frame;

			public readonly int disconnect_requested;//:1;
			public readonly int ack_frame;//:31;

			public readonly ushort num_bits;
			public readonly byte input_size; // XXX: shouldn't be in every single packet!
			public fixed byte bits[MAX_COMPRESSED_BITS]; /* must be last */
		}

		public struct InputAck{
			public readonly int ack_frame;//:31;
		}

		public UdpMsg(MsgType t) {
			hdr = new HDR {
				type = (byte) t
			};

			sync_request = default;
			sync_reply = default;
			quality_report = default;
			quality_reply = default;
			input = default;
			input_ack = default;
		}
		
		// TODO remove unsafe
		public unsafe int PacketSize() {
			return sizeof(HDR) + PayloadSize();
		}

		// TODO remove unsafe
		public unsafe int PayloadSize() {
			switch ((MsgType) hdr.type) {
				case MsgType.SyncRequest:   return sizeof(SyncRequest);
				case MsgType.SyncReply:     return sizeof(SyncReply);
				case MsgType.QualityReport: return sizeof(QualityReport);
				case MsgType.QualityReply:  return sizeof(QualityReply);
				case MsgType.InputAck:      return sizeof(InputAck);
				case MsgType.KeepAlive:     return 0;
				case MsgType.Input:
					int size = sizeof(Input) - MAX_COMPRESSED_BITS;
					size += (input.num_bits + 7) / 8;
					return size;
			}
			
			throw new ArgumentException();
		}
		
		public enum MsgType {
			Invalid       = 0,
			SyncRequest   = 1,
			SyncReply     = 2,
			Input         = 3,
			QualityReport = 4,
			QualityReply  = 5,
			KeepAlive     = 6,
			InputAck      = 7,
		};

		public struct connect_status {
			public readonly uint disconnected;//:1;
			public readonly int last_frame;//:31;
		};
		
		public struct HDR {
			public readonly ushort magic;
			public readonly ushort sequence_number;
			public byte type; // packet type
		}
	};
}

#pragma pack(pop)