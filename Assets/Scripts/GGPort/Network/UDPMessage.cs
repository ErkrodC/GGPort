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
