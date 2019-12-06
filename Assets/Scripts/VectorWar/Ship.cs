using System;

namespace VectorWar {
	[Serializable]
	public class Ship {
		public const int STARTING_HEALTH = 100;
		public const int SHIP_RADIUS = 15;
		public const int SHIP_WIDTH = 8;
		public const int SHIP_TUCK = 3;
		public const float SHIP_THRUST = 0.06f;
		public const float SHIP_MAX_THRUST = 4.0f;
		public const float SHIP_BREAK_SPEED = 0.6f;
		public const int ROTATE_INCREMENT = 3;
		public const int MAX_BULLETS = 30;

		public Vector2 position;
		public Vector2 velocity;
		public int radius;
		public int heading;
		public int health;
		public int speed;
		public int cooldown;
		public Bullet[] bullets = new Bullet[MAX_BULLETS];
		public int score;

		public static unsafe int Size() {
			return
				sizeof(Vector2)
				+ sizeof(Vector2)
				+ sizeof(int) * 6
				+ sizeof(Bullet) * MAX_BULLETS;
		}
	}
}