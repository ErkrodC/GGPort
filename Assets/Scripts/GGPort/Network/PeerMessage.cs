/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace GGPort {
	// TODO separate out into smaller derived classes, this used to be a union.
	[Serializable, StructLayout(LayoutKind.Explicit)]
	public struct PeerMessage {
		public const ushort MAX_COMPRESSED_BITS = 4096;
		public const byte MAX_PLAYERS = 4; // TODO probably doesn't belong in this class, i.e. used in a lot of places
		
		[FieldOffset(0)] public Header header;
		[FieldOffset(5)] public SyncRequest syncRequest;
		[FieldOffset(5)] public SyncReply syncReply;
		[FieldOffset(5)] public QualityReport qualityReport;
		[FieldOffset(5)] public QualityReply qualityReply;
		[FieldOffset(5)] public Input input;
		[FieldOffset(5)] public InputAck inputAck;
		
		public PeerMessage(MsgType t) {
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
		
		[Serializable]
		public struct Header {
			public ushort magicNumber;
			public ushort sequenceNumber;
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
		public unsafe struct Input : ISerializable {
			public int startFrame;
			public bool disconnectRequested;
			public int ackFrame;
			public ushort numBits;
			public byte inputSize;  // XXX: shouldn't be in every single packet!
			
			private fixed bool peerDisconnectedFlags[MAX_PLAYERS];
			private fixed int peerLastFrames[MAX_PLAYERS];
			public fixed byte bits[MAX_COMPRESSED_BITS]; /* must be last */ // TODO why?

			public Input(SerializationInfo info, StreamingContext context) : this() {
				startFrame = info.GetInt32(nameof(startFrame));
				disconnectRequested = info.GetBoolean(nameof(disconnectRequested));
				ackFrame = info.GetInt32(nameof(ackFrame));
				numBits = info.GetUInt16(nameof(numBits));
				inputSize = info.GetByte(nameof(inputSize));
				
				// deserialize connect statuses
				bool[] peerDisconnectedFlagsArray = info.GetValue(nameof(peerDisconnectedFlags), typeof(bool[])) as bool[];
				int[] peerLastFramesArray = info.GetValue(nameof(peerLastFrames), typeof(int[])) as int[];

				for (int i = 0; i < MAX_PLAYERS; i++) {
					peerDisconnectedFlags[i] = peerDisconnectedFlagsArray[i];
					peerLastFrames[i] = peerLastFramesArray[i];
				}

				// deserialize bits
				byte[] bitsArray = info.GetValue(nameof(bits), typeof(byte[])) as byte[];
				for (int i = 0; i < MAX_COMPRESSED_BITS; i++) {
					bits[i] = bitsArray[i];
				}
			}

			public void SetPeerConnectStatus(int index, ConnectStatus peerConnectStatus) {
				peerDisconnectedFlags[index] = peerConnectStatus.isDisconnected;
				peerLastFrames[index] = peerConnectStatus.lastFrame;
			}

			public void GetConnectStatuses(ref ConnectStatus[] connectStatuses) {
				for (int i = 0; i < MAX_PLAYERS; i++) {
					connectStatuses[i] = new ConnectStatus {
						isDisconnected = peerDisconnectedFlags[i],
						lastFrame = peerLastFrames[i]
					};
				}
			}

			public void GetObjectData(SerializationInfo info, StreamingContext context) {
				info.AddValue(nameof(startFrame), startFrame);
				info.AddValue(nameof(disconnectRequested), disconnectRequested);
				info.AddValue(nameof(ackFrame), ackFrame);
				info.AddValue(nameof(numBits), numBits);
				info.AddValue(nameof(inputSize), inputSize);
				
				// serialize connect statuses
				bool[] peerDisconnectedFlagsArray = new bool[MAX_PLAYERS];
				int[] peerLastFramesArray = new int[MAX_PLAYERS];
				for (int i = 0; i < MAX_PLAYERS; i++) {
					peerDisconnectedFlagsArray[i] = peerDisconnectedFlags[i];
					peerLastFramesArray[i] = peerLastFrames[i];
				}
				
				info.AddValue(nameof(peerDisconnectedFlags), peerDisconnectedFlagsArray, typeof(bool[]));
				info.AddValue(nameof(peerLastFrames), peerLastFramesArray, typeof(int[]));
				
				// serialize bits
				byte[] bitsArray = new byte[MAX_COMPRESSED_BITS];
				for (int i = 0; i < MAX_COMPRESSED_BITS; i++) {
					bitsArray[i] = bits[i];
				}
				
				info.AddValue(nameof(bits), bitsArray, typeof(byte[]));
			}
		}

		[Serializable]
		public struct InputAck {
			// TODO address bitfields
			public int ackFrame; //:31;
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
		
		[Serializable]
		public struct ConnectStatus {
			public bool isDisconnected;
			public int lastFrame;
		};
	};
}
