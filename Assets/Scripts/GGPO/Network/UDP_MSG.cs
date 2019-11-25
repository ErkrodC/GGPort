/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Runtime.InteropServices;

namespace GGPort {
	[StructLayout(LayoutKind.Explicit, Pack = 1)]
	public struct UdpMsg {
		public const ushort MAX_COMPRESSED_BITS = 4096;
		public const byte UDP_MSG_MAX_PLAYERS = 4;
		
		[FieldOffset(0)] public HDR hdr;
		[FieldOffset(5)] public SyncRequest sync_request;
		[FieldOffset(5)] public SyncReply sync_reply;
		[FieldOffset(5)] public QualityReport quality_report;
		[FieldOffset(5)] public QualityReply quality_reply;
		[FieldOffset(5)] public readonly Input input;
		[FieldOffset(5)] public InputAck input_ack;
		
		public struct SyncRequest {
			public uint random_request { get; set; } /* please reply back with this random data */
			public readonly ushort remote_magic;
			public readonly byte remote_endpoint;
		}
		
		public struct SyncReply {
			public uint random_reply { get; set; } /* OK, here's your random data back */
		}
		
		public struct QualityReport {
			public sbyte frame_advantage { get; set; } /* what's the other guy's frame advantage? */
			public long ping { get; set; }
		}
		
		public struct QualityReply {
			public long pong { get; set; }
		}

		public class Input {
			public connect_status[] peer_connect_status = new connect_status[UDP_MSG_MAX_PLAYERS];

			public uint start_frame { get; set; }

			// TODO address bitfields
			public bool disconnect_requested { get; set; } //:1;
			public int ack_frame { get; set; } //:31;

			public ushort num_bits { get; set; }
			public byte input_size { get; set; }  // XXX: shouldn't be in every single packet!
			public byte[] bits = new byte[MAX_COMPRESSED_BITS]; /* must be last */
		}

		public struct InputAck {
			// TODO address bitfields
			public int ack_frame { get; set; } //:31;
		}

		public UdpMsg(MsgType t) {
			hdr = new HDR {
				type = t
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
			switch (hdr.type) {
				case MsgType.SyncRequest:   return sizeof(SyncRequest);
				case MsgType.SyncReply:     return sizeof(SyncReply);
				case MsgType.QualityReport: return sizeof(QualityReport);
				case MsgType.QualityReply:  return sizeof(QualityReply);
				case MsgType.InputAck:      return sizeof(InputAck);
				case MsgType.KeepAlive:     return 0;
				case MsgType.Input:
					int size = 0;
					size += sizeof(connect_status) * UDP_MSG_MAX_PLAYERS; // NOTE should be size of array pointer?
					size += sizeof(uint);
					size += sizeof(int);
					size += sizeof(int);
					size += sizeof(ushort);
					size += sizeof(byte);
					size += (input.num_bits + 7) / 8;
					return size;
			}
			
			throw new ArgumentException();
		}
		
		public enum MsgType : byte {
			Invalid       = 0,
			SyncRequest   = 1,
			SyncReply     = 2,
			Input         = 3,
			QualityReport = 4,
			QualityReply  = 5,
			KeepAlive     = 6,
			InputAck      = 7,
		};
		
		// TODO address bitfields
		public struct connect_status {
			public bool disconnected;//:1;
			public int last_frame;//:31;
		};
		
		public struct HDR {
			public ushort magic { get; set; }
			public ushort sequence_number { get; set; }
			public MsgType type; // packet type
		}
	};
}
