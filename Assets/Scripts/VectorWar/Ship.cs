using System;
using FixMath.NET;

namespace VectorWar {
	[Serializable]
	public class Ship {
		public const int STARTING_HEALTH = 100;
		public static readonly Fix64 shipRadius = (Fix64) 15;
		public const int SHIP_WIDTH = 8;
		public const int SHIP_TUCK = 3;
		public static readonly Fix64 shipThrust = (Fix64) 0.06f;
		public static readonly Fix64 shipMaxThrust = (Fix64) 4.0f;
		public static readonly Fix64 shipBrakeSpeed = (Fix64) 0.6f;
		public static readonly Fix64 rotateIncrement = (Fix64) 3;
		public const int MAX_BULLETS = 30;

		public FixVector2 position;
		public FixVector2 velocity;
		public Fix64 radius;
		public Fix64 heading;
		public int health;
		public Fix64 speed;
		public int cooldown;
		public int score;
		public Bullet[] bullets = new Bullet[MAX_BULLETS];

		public Ship() { }

		public Ship(Ship other) {
			position = other.position;
			velocity = other.velocity;
			radius = other.radius;
			heading = other.heading;
			health = other.health;
			speed = other.speed;
			cooldown = other.cooldown;
			score = other.score;

			bullets = new Bullet[MAX_BULLETS];
			for (int i = 0; i < MAX_BULLETS; i++) {
				bullets[i] = other.bullets[i];
			}
		}

		public static unsafe int Size() {
			return
				sizeof(FixVector2)
				+ sizeof(FixVector2)
				+ sizeof(int) * 6
				+ sizeof(Bullet) * MAX_BULLETS;
		}
	}
}