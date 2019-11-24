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
		public const int GAMEINPUT_MAX_BYTES = 9;
		public const int GAMEINPUT_MAX_PLAYERS = 2;
		public const int NullFrame = -1;
		
		public int frame { get; set; }
		public int size { get; set; } /* size in bytes of the entire input for all players */
		public fixed byte bits[GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS];

		public bool is_null() {
			return frame == -1;
		}

		public void init(int iframe, byte[] ibits, int isize, int offset) {
			if (isize == 0) {
				throw new ArgumentException();
			}

			if (isize > GAMEINPUT_MAX_BYTES) {
				throw new ArgumentException();
			}
			
			frame = iframe;
			size = isize;

			const int sizeOfBits = GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS;
			for (int i = 0; i < sizeOfBits; i++) {
				bits[i] = 0;
			}
			
			if (ibits != null) {
				int startOffset = offset * isize;
				for (int i = 0; i < isize; i++) {
					bits[startOffset + i] = ibits[i];
				}
			}
		}

		public void init(int iframe, byte[] ibits, int isize) {
			if (isize == 0) {
				throw new ArgumentException();
			}

			if (isize > GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS) {
				throw new ArgumentException();
			}
			
			frame = iframe;
			size = isize;

			const int sizeOfBits = GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS;
			for (int i = 0; i < sizeOfBits; i++) {
				bits[i] = 0;
			}
			
			if (ibits != null) {
				for (int i = 0; i < isize; i++) {
					bits[i] = ibits[i];
				}
			}
		}

		public bool value(int i) {
			return (bits[i/8] & (1 << (i%8))) != 0;
		}

		public void set(int i) {
			bits[i/8] |= (byte) (1 << (i%8));
		}

		public void clear(int i) {
			bits[i/8] &= (byte) ~(1 << (i%8));
		}

		public void erase() {
			const int sizeOfBits = GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS;
			for (int i = 0; i < sizeOfBits; i++) {
				bits[i] = 0;
			}
		}

		public string desc(bool show_frame = true) {
			if (size == 0) {
				throw new ArgumentException();
			}
			
			string result = $"({(show_frame ? $"frame:{frame} " : "")}size:{size} ";

			for (int i = 0; i < size * 8; i++) {
				result += $"{i:00} ";
			}

			result += ")";

			return result;
		}

		public void log(string prefix, bool show_frame = true) {
			LogUtil.Log($"{prefix}{desc(show_frame)}\n");
		}

		public bool equal(ref GameInput other, bool bitsonly = false) {
			if (!bitsonly && frame != other.frame) {
				LogUtil.Log($"frames don't match: {frame}, {other.frame}\n");
			}
			
			if (size != other.size) {
				LogUtil.Log($"sizes don't match: {size}, {other.size}\n");
			}

			bool bitsAreEqual = true;

			for (int i = 0; i < size; i++) {
				if (bits[i] != other.bits[i]) {
					bitsAreEqual = false;
					break;
				}
			}
			
			if (!bitsAreEqual) {
				LogUtil.Log($"bits don't match\n");
			}

			if (size == 0 || other.size == 0) {
				throw new ArgumentException();
			}
			
			return (bitsonly || frame == other.frame) &&
			       size == other.size &&
			       bitsAreEqual;
		}
	};
}