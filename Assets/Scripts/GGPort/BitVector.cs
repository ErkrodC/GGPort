﻿using System;

namespace GGPort {
	public static class BitVector {
		public const int BITVECTOR_NIBBLE_SIZE = 8;

		public static void SetBit(byte[] vector, ref int offset) {
			vector[offset / 8] |= (byte) (1 << offset % 8);
			++offset;
		}

		public static void ClearBit(byte[] vector, ref int offset) {
			vector[offset / 8] &= (byte) ~(1 << offset % 8);
			++offset;
		}

		public static void WriteNibblet(byte[] vector, int nibble, ref int offset) {
			if (nibble >= 1 << BITVECTOR_NIBBLE_SIZE) {
				throw new ArgumentException();
			}
			
			for (int i = 0; i < BITVECTOR_NIBBLE_SIZE; i++) {
				if ((nibble & (1 << i)) != 0) {
					SetBit(vector, ref offset);
				} else {
					ClearBit(vector, ref offset);
				}
			}
		}

		public static int ReadBit(byte[] vector, ref int offset) {
			int result = vector[offset / 8] & 1 << offset % 8;
			++offset;
			return result;
		}

		public static int ReadNibblet(byte[] vector, ref int offset) {
			int nibblet = 0;
			
			for (int i = 0; i < BITVECTOR_NIBBLE_SIZE; i++) {
				nibblet |= ReadBit(vector, ref offset) << i;
			}
			
			return nibblet;
		}
	}
}