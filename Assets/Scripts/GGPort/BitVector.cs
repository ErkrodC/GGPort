namespace GGPort {
	public static class BitVector {
		public const int NIBBLE_SIZE = 8;

		public static unsafe bool ReadBit(byte* vector, ref int offset) {
			bool result = (vector[offset / 8] & (1 << (offset % 8))) != 0;
			++offset;
			return result;
		}

		public static unsafe void WriteBit(byte* vector, ref int offset, bool value) {
			if (value) {
				vector[offset / 8] |= (byte) (1 << (offset % 8));
				++offset;
			} else {
				vector[offset / 8] &= (byte) ~(1 << (offset % 8));
				++offset;
			}
		}

		public static unsafe int ReadNibblet(byte* vector, ref int offset) {
			int nibblet = 0;

			for (int i = 0; i < NIBBLE_SIZE; i++) {
#if BIG_ENDIAN // ER TODO how pervasive is this?
				nibblet |= (vector[offset / NIBBLE_SIZE] & (1 << (offset % NIBBLE_SIZE))) << i;
#else
				nibblet |= (vector[offset / NIBBLE_SIZE] & (1 << (offset % NIBBLE_SIZE))) >> (offset % NIBBLE_SIZE - i);
#endif
				++offset;
			}

			return nibblet;
		}

		public static unsafe void WriteNibblet(byte* vector, int nibble, ref int offset) {
			Platform.Assert(nibble < 1 << NIBBLE_SIZE);

			for (int i = 0; i < NIBBLE_SIZE; i++) {
				if ((nibble & (1 << i)) != 0) {
					vector[offset / 8] |= (byte) (1 << (offset % 8));
					++offset;
				} else {
					vector[offset / 8] &= (byte) ~(1 << (offset % 8));
					++offset;
				}
			}
		}
	}
}