using System;
using JetBrains.Annotations;

namespace GGPort {
	public static class BitVector {
		public const int BITVECTOR_NIBBLE_SIZE = 8;

		// TODO rename
		public static void BitVector_SetBit(byte[] vector, ref int offset) {
			vector[offset / 8] |= (byte) (1 << offset % 8);
			++offset;
		}

		public static void BitVector_ClearBit(byte[] vector, ref int offset) {
			vector[offset / 8] &= (byte) ~(1 << offset % 8);
			++offset;
		}

		public static void BitVector_WriteNibblet(byte[] vector, int nibble, ref int offset) {
			if (nibble >= 1 << BITVECTOR_NIBBLE_SIZE) {
				throw new ArgumentException();
			}
			
			for (int i = 0; i < BITVECTOR_NIBBLE_SIZE; i++) {
				if ((nibble & (1 << i)) != 0) {
					BitVector_SetBit(vector, ref offset);
				} else {
					BitVector_ClearBit(vector, ref offset);
				}
			}
		}

		public static int BitVector_ReadBit(byte[] vector, ref int offset) {
			int result = vector[offset / 8] & 1 << offset % 8;
			++offset;
			return result;
		}

		public static int BitVector_ReadNibblet(byte[] vector, ref int offset) {
			int nibblet = 0;
			
			for (int i = 0; i < BITVECTOR_NIBBLE_SIZE; i++) {
				nibblet |= BitVector_ReadBit(vector, ref offset) << i;
			}
			
			return nibblet;
		}
	}
}