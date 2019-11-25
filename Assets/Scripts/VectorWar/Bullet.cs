using UnityEngine;

namespace VectorWar {
	public struct Bullet {
		public const int BULLET_SPEED = 5;
		public const int BULLET_COOLDOWN = 8;
		public const int BULLET_DAMAGE = 10;

		public bool active;
		public Vector2 position;
		public Vector2 velocity; // TODO actually deltaVelocity
	}
}