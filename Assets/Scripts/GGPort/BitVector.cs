namespace GGPort {
	public static class BitVector {
		public const int kBitVectorNibbleSize = 8;

		public static int ReadBit(byte[] vector, ref int offset) {
			int result = vector[offset / 8] & 1 << offset % 8;
			++offset;
			return result;
		}

		public static void WriteBit(byte[] vector, ref int offset, bool value) {
			if (value) {
				vector[offset / 8] |= (byte) (1 << offset % 8);
				++offset;
			} else {
				vector[offset / 8] &= (byte) ~(1 << offset % 8);
				++offset;
			}
		}

		public static int ReadNibblet(byte[] vector, ref int offset) {
			int nibblet = 0;
			
			for (int i = 0; i < kBitVectorNibbleSize; i++) {
				nibblet |= vector[offset / 8] & 1 << offset % 8 << i;
				++offset;
			}
			
			return nibblet;
		}

		public static void WriteNibblet(byte[] vector, int nibble, ref int offset) {
			Platform.Assert(nibble < 1 << kBitVectorNibbleSize);
			
			for (int i = 0; i < kBitVectorNibbleSize; i++) {
				if ((nibble & 1 << i) != 0) {
					vector[offset / 8] |= (byte) (1 << offset % 8);
					++offset;
				} else {
					vector[offset / 8] &= (byte) ~(1 << offset % 8);
					++offset;
				}
			}
		}
	}
}