/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	// TODO seperate out into smaller derived classes, this used to be a union.
	[Serializable]
	public struct UDPMessage {
		public const ushort MAX_COMPRESSED_BITS = 4096;
		public const byte UDP_MSG_MAX_PLAYERS = 4;
		
		public Header header;
		public SyncRequest syncRequest;
		public SyncReply syncReply;
		public QualityReport qualityReport;
		public QualityReply qualityReply;
		public Input input;
		public InputAck inputAck;
		
		[Serializable]
		public struct Header {
			public ushort magic { get; set; }
			public ushort SequenceNumber { get; set; }
			public MsgType type; // packet type
		}
		
		[Serializable]
		public struct SyncRequest {
			public uint randomRequest; /* please reply back with this random data */
			public ushort remoteMagic;
			public byte remoteEndpoint; // LOH is this set somewhere in the c++?
		}
		
		[Serializable]
		public struct SyncReply {
			public uint randomReply; /* OK, here's your random data back */
		}
		
		[Serializable]
		public struct QualityReport {
			public sbyte frameAdvantage; /* what's the other guy's frame advantage? */
			public long ping;
		}
		
		[Serializable]
		public struct QualityReply {
			public long pong;
		}

		[Serializable]
		public class Input {
			public ConnectStatus[] peerConnectStatus = new ConnectStatus[UDP_MSG_MAX_PLAYERS];

			public uint startFrame;

			// TODO address bitfields
			public bool disconnectRequested; //:1;
			public int ackFrame; //:31;

			public ushort numBits;
			public byte inputSize;  // XXX: shouldn't be in every single packet!
			public byte[] bits = new byte[MAX_COMPRESSED_BITS]; /* must be last */
		}

		[Serializable]
		public struct InputAck {
			// TODO address bitfields
			public int ackFrame; //:31;
		}

		public UDPMessage(MsgType t) {
			header = new Header {
				type = t
			};

			syncRequest = default;
			syncReply = default;
			qualityReport = default;
			qualityReply = default;
			input = t == MsgType.Input ? new Input() : default;
			inputAck = default;
		}
		
		// TODO remove unsafe
		public unsafe int PacketSize() {
			return sizeof(Header) + PayloadSize();
		}

		// TODO remove unsafe
		public unsafe int PayloadSize() {
			/*switch (hdr.type) {
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
			
			throw new ArgumentException();*/
			
			int size = 0;
			size += sizeof(ConnectStatus) * UDP_MSG_MAX_PLAYERS; // NOTE should be size of array pointer?
			size += sizeof(uint);
			size += sizeof(int);
			size += sizeof(int);
			size += sizeof(ushort);
			size += sizeof(byte);
			size += input == null ? 0 : (input.numBits + 7) / 8;
			size += sizeof(SyncRequest);
			size += sizeof(SyncReply);
			size += sizeof(QualityReport);
			size += sizeof(QualityReply);
			size += sizeof(InputAck);

			return size;
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
		[Serializable]
		public struct ConnectStatus {
			public bool IsDisconnected;//:1;
			public int LastFrame;//:31;
		};
	};
}
