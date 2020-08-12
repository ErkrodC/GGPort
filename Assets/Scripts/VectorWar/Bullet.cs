using System;
using UnityEngine;

namespace VectorWar {
	[Serializable]
	public struct Bullet {
		public const int BULLET_SPEED = 5;
		public const int BULLET_COOLDOWN = 8;
		public const int BULLET_DAMAGE = 10;

		public bool active;
		public Vector2 position;
		public Vector2 velocity;
	}

	[Serializable]
	public struct Vector2 {
		public float x;
		public float y;

		public static float Distance(Vector2 vec0, Vector2 vec1) {
			float dx = vec0.x - vec1.x;
			float dy = vec0.y - vec1.y;
			return Mathf.Sqrt(dx * dx + dy * dy);
		}
	}
}