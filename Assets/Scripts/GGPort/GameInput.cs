/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

// GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS * 8 must be less than
// 2^BITVECTOR_NIBBLE_SIZE (see bitvector.h)

using System;

namespace GGPort {
	public unsafe struct GameInput {
		public const int kMaxBytes = 9;
		public const int kMaxPlayers = 2;
		public const int kNullFrame = -1;
		
		public int Frame { get; set; } // TODO do props get copied on pass for val type?
		public int Size { get; set; } /* size in bytes of the entire input for all players */
		public fixed byte Bits[kMaxBytes * kMaxPlayers];

		public bool IsNull() {
			return Frame == -1;
		}

		public bool this[int bitIndex] {
			get => (Bits[bitIndex / 8] & 1 << bitIndex % 8) != 0;
			set {
				if (value) {
					Bits[bitIndex/8] |= (byte) (1 << bitIndex % 8);
				} else {
					Bits[bitIndex / 8] &= (byte) ~(1 << bitIndex % 8);
				}
			}
		}

		public void Init(int frame, byte[] bits, int size, int offset) {
			Platform.Assert(0 < size && size <= kMaxBytes);
			
			Frame = frame;
			Size = size;

			const int kSizeOfBits = kMaxBytes * kMaxPlayers;
			for (int i = 0; i < kSizeOfBits; i++) { Bits[i] = 0; }

			if (bits == null) { return; }

			int startOffset = offset * size;
			for (int i = 0; i < size; i++) {
				Bits[startOffset + i] = bits[i];
			}
		}

		public void Init(int frame, byte[] bits, int size) {
			Platform.Assert(0 < size && size <= kMaxBytes);
			
			Frame = frame;
			Size = size;

			const int kSizeOfBits = kMaxBytes * kMaxPlayers;
			for (int i = 0; i < kSizeOfBits; i++) {
				Bits[i] = 0;
			}
			
			if (bits != null) {
				for (int i = 0; i < size; i++) {
					Bits[i] = bits[i];
				}
			}
		}

		public void Erase() {
			const int kSizeOfBits = kMaxBytes * kMaxPlayers;
			for (int i = 0; i < kSizeOfBits; i++) {
				Bits[i] = 0;
			}
		}

		public string Desc(bool showFrame = true) {
			Platform.Assert(Size != 0);
			
			string result = $"({(showFrame ? $"frame:{Frame} " : "")}size:{Size} ";

			for (int i = 0; i < Size * 8; i++) {
				result += $"{i:00} ";
			}

			result += ")";

			return result;
		}

		public void Log(string prefix, bool showFrame = true) {
			LogUtil.Log($"{prefix}{Desc(showFrame)}{Environment.NewLine}");
		}

		public bool Equal(GameInput other, bool bitsOnly = false) {
			if (!bitsOnly && Frame != other.Frame) {
				LogUtil.Log($"frames don't match: {Frame}, {other.Frame}{Environment.NewLine}");
			}
			
			if (Size != other.Size) {
				LogUtil.Log($"sizes don't match: {Size}, {other.Size}{Environment.NewLine}");
			}

			bool bitsAreEqual = true;
			for (int i = 0; i < Size; i++) {
				if (Bits[i] == other.Bits[i]) { continue; }

				bitsAreEqual = false;
				break;
			}
			
			if (!bitsAreEqual) { LogUtil.Log($"bits don't match{Environment.NewLine}"); }

			Platform.Assert(Size != 0 && other.Size != 0);
			
			return (bitsOnly || Frame == other.Frame) &&
			       Size == other.Size &&
			       bitsAreEqual;
		}
	};
}