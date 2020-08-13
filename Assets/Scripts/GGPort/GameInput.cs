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
		public const int MAX_BYTES = 9;
		public const int MAX_PLAYERS = 2;
		public const int NULL_FRAME = -1;
		private const int _SIZE_OF_BITS = MAX_BYTES * MAX_PLAYERS;

		public int frame;
		public int size; /* size in bytes of the entire input for all players */
		public fixed byte bits[MAX_BYTES * MAX_PLAYERS];

		public bool IsNull() {
			return frame == -1;
		}

		public bool this[int bitIndex] {
			get => (bits[bitIndex / 8] & (1 << (bitIndex % 8))) != 0;
			set {
				if (value) {
					bits[bitIndex / 8] |= (byte) (1 << (bitIndex % 8));
				} else {
					bits[bitIndex / 8] &= (byte) ~(1 << (bitIndex % 8));
				}
			}
		}

		public void Init(int frame, byte[] bits, int size, int offset) {
			Platform.Assert(0 < size && size <= MAX_BYTES);

			this.frame = frame;
			this.size = size;

			for (int i = 0; i < _SIZE_OF_BITS; i++) { this.bits[i] = 0; }

			if (bits == null) { return; }

			int startOffset = offset * size;
			for (int i = 0; i < size; i++) {
				this.bits[startOffset + i] = bits[i];
			}
		}

		public void Init(int frame, byte[] bits, int size) {
			Platform.Assert(0 < size && size <= MAX_BYTES);

			this.frame = frame;
			this.size = size;

			for (int i = 0; i < _SIZE_OF_BITS; i++) {
				this.bits[i] = 0;
			}

			if (bits != null) {
				for (int i = 0; i < size; i++) {
					this.bits[i] = bits[i];
				}
			}
		}

		public void Erase() {
			for (int i = 0; i < _SIZE_OF_BITS; i++) {
				bits[i] = 0;
			}
		}

		public string Desc(bool showFrame = true) {
			Platform.Assert(size != 0);

			string result = $"({(showFrame ? $"frame:{frame} " : "")}size:{size} ";

			for (int i = 0; i < size * 8; i++) {
				result += $"{i:00} ";
			}

			result += ")";

			return result;
		}

		public void Log(string prefix, bool showFrame = true) {
			LogUtil.Log($"{prefix}{Desc(showFrame)}{Environment.NewLine}");
		}

		public bool Equal(GameInput other, bool bitsOnly = false) {
			if (!bitsOnly && frame != other.frame) {
				LogUtil.Log($"frames don't match: {frame}, {other.frame}{Environment.NewLine}");
			}

			if (size != other.size) {
				LogUtil.Log($"sizes don't match: {size}, {other.size}{Environment.NewLine}");
			}

			bool bitsAreEqual = true;
			for (int i = 0; i < size; i++) {
				if (bits[i] == other.bits[i]) { continue; }

				bitsAreEqual = false;
				break;
			}

			if (!bitsAreEqual) { LogUtil.Log($"bits don't match{Environment.NewLine}"); }

			Platform.Assert(size != 0 && other.size != 0);

			return (bitsOnly || frame == other.frame) && size == other.size && bitsAreEqual;
		}
	};
}