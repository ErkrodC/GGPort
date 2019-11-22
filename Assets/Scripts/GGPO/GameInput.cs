/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

// GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS * 8 must be less than
// 2^BITVECTOR_NIBBLE_SIZE (see bitvector.h)

namespace GGPort { // NOTE LOH, still need to fix format strings in InputQueue.cs.
                   // NOTE could just change sig to pass interp'd string as arg and be done rid of fmt string altogether....that sounds nice.....
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
		
		public void init(int frame, char *bits, int size, int offset);
		public void init(int frame, char *bits, int size);

		public bool value(int i) {
			return (bits[i/8] & (1 << (i%8))) != 0;
		}

		public void set(int i) {
			bits[i/8] |= (1 << (i%8));
		}

		public void clear(int i) {
			bits[i/8] &= ~(1 << (i%8));
		}

		public void erase() {
			memset(bits, 0, sizeof(bits));
		}
		
		public void desc(char *buf, size_t buf_size, bool show_frame = true);
		public void log(char *prefix, bool show_frame = true);
		public bool equal(ref GameInput input, bool bitsonly = false);
	};
}