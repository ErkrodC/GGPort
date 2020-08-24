using System;
using FixMath.NET;

namespace VectorWar {
	[Serializable]
	public struct Bullet {
		public const int BULLET_COOLDOWN = 8;
		public const int BULLET_DAMAGE = 10;
		public static readonly Fix64 bulletSpeed = (Fix64) 5;

		public bool active;
		public FixVector2 position;
		public FixVector2 velocity;
	}
}