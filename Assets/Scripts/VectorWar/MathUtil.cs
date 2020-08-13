using System;

namespace VectorWar {
	public static class MathUtil {
		public const float PI = 3.1415926f;
		private const float _FLOAT_COMP_TOLERANCE = 0.0001f;

		public static float DegToRad(float deg) {
			return PI * deg / 180;
		}

		public static bool Equals0(float val) {
			return Math.Abs(val) < _FLOAT_COMP_TOLERANCE;
		}

		public static bool AppxEquals(float val0, float val1) {
			return Math.Abs(val0 - val1) < _FLOAT_COMP_TOLERANCE;
		}
	}
}