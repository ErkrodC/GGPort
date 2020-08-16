/*
* gamestate.h --
*
* Encapsulates all the game state for the vector war application inside
* a single structure.  This makes it trivial to implement our GGPO
* save and load functions.
*/

using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using GGPort;
using UnityEngine;

namespace VectorWar {
	[Serializable]
	public class GameState : ICloneable {
		public const int MAX_SHIPS = 4;

		public int frameNumber;
		public Rect bounds;
		public int numShips;
		public Ship[] ships;

		public GameState() { }

		public GameState(byte[] buffer) {
			Deserialize(buffer);
		}

		public GameState(GameState other) {
			frameNumber = other.frameNumber;
			bounds = other.bounds;
			numShips = other.numShips;

			ships = new Ship[MAX_SHIPS];
			for (int i = 0; i < MAX_SHIPS; i++) {
				ships[i] = other.ships[i];
			}
		}

		// Initialize our game state.
		public void Init(int numPlayers) {
			int i, w, h, r;

			bounds = new Rect(Screen.safeArea);

			w = Convert.ToInt32(bounds.xMax - bounds.xMin); // NOTE possible source of non-determinism
			h = Convert.ToInt32(bounds.yMax - bounds.yMin);
			r = h / 4;

			frameNumber = 0;
			numShips = numPlayers;

			ships = new Ship[MAX_SHIPS];
			for (int j = 0; j < ships.Length; j++) {
				ships[j] = new Ship();
			}

			for (i = 0; i < numShips; i++) {
				int heading = i * 360 / numPlayers;
				double cost, sint, theta;

				theta = heading * MathUtil.PI / 180;
				cost = Math.Cos(theta);
				sint = Math.Sin(theta);

				ships[i].position.x = (float) (w / 2 + r * cost); // NOTE possible source of non-determinism
				ships[i].position.y = (float) (h / 2 + r * sint); // NOTE possible source of non-determinism
				ships[i].heading = (heading + 180) % 360;
				ships[i].health = Ship.STARTING_HEALTH;
				ships[i].radius = Ship.SHIP_RADIUS;
			}
		}

		public void GetShipAI(int i, out float heading, out float thrust, out bool fire) {
			heading = (ships[i].heading + 5) % 360;
			thrust = 0;
			fire = false;
		}

		public void ParseShipInputs(VectorWar.ShipInput inputMask, int i, out float heading, out float thrust, out bool fire) {
			Ship ship = ships[i];

			LogUtil.Log($"parsing ship {i} inputMask: {inputMask}.{Environment.NewLine}");

			if (inputMask.HasFlag(VectorWar.ShipInput.Clockwise)) {
				heading = (ship.heading - Ship.ROTATE_INCREMENT) % 360;
			} else if (inputMask.HasFlag(VectorWar.ShipInput.CounterClockwise)) {
				heading = (ship.heading + Ship.ROTATE_INCREMENT) % 360;
			} else {
				heading = ship.heading;
			}

			if (inputMask.HasFlag(VectorWar.ShipInput.Thrust)) {
				thrust = Ship.SHIP_THRUST;
			} else if (inputMask.HasFlag(VectorWar.ShipInput.Brake)) {
				thrust = -Ship.SHIP_THRUST;
			} else {
				thrust = 0;
			}

			fire = inputMask.HasFlag(VectorWar.ShipInput.Fire);
		}

		public void MoveShip(int shipIndex, float heading, float thrust, bool fire) {
			Ship ship = ships[shipIndex];

			LogUtil.Log(
				$"calculation of new ship coordinates: (thrust:{thrust:F4} heading:{heading:F4}).{Environment.NewLine}"
			);

			ship.heading = (int) heading;

			if (ship.cooldown == 0) {
				if (fire) {
					LogUtil.Log(
						$"Firing bullet.{Environment.NewLine}"
					); // TODO tag based log? to easily enable/disable entire categories
					for (int i = 0; i < Ship.MAX_BULLETS; i++) {
						float dx = (float) Math.Cos(
							MathUtil.DegToRad(ship.heading)
						); // NOTE possible sources of non-determinism
						float dy = (float) Math.Sin(MathUtil.DegToRad(ship.heading));

						if (ship.bullets[i].active) { continue; }

						ship.bullets[i].active = true;
						ship.bullets[i].position.x =
							ship.position.x + ship.radius * dx; // NOTE possible sources of non-determinism
						ship.bullets[i].position.y = ship.position.y + ship.radius * dy;
						ship.bullets[i].velocity.x = ship.velocity.x + Bullet.BULLET_SPEED * dx;
						ship.bullets[i].velocity.y = ship.velocity.y + Bullet.BULLET_SPEED * dy;
						ship.cooldown = Bullet.BULLET_COOLDOWN;
						break;
					}
				}
			}

			if (!MathUtil.Equals0(thrust)) {
				float dx = (float) (thrust
				                    * Math.Cos(MathUtil.DegToRad(heading))); // NOTE possible sources of non-determinism
				float dy = (float) (thrust * Math.Sin(MathUtil.DegToRad(heading)));

				ship.velocity.x += dx;
				ship.velocity.y += dy;
				float mag = (float) Math.Sqrt(
					ship.velocity.x * ship.velocity.x + ship.velocity.y * ship.velocity.y
				); // NOTE possible source of non-determinism
				if (mag > Ship.SHIP_MAX_THRUST) {
					ship.velocity.x = ship.velocity.x * Ship.SHIP_MAX_THRUST / mag;
					ship.velocity.y = ship.velocity.y * Ship.SHIP_MAX_THRUST / mag;
				}
			}

			LogUtil.Log(
				$"new ship velocity: (dx:{ship.velocity.x:F4} dy:{ship.velocity.y:F4}).{Environment.NewLine}"
			);

			ship.position.x += ship.velocity.x;
			ship.position.y += ship.velocity.y;
			LogUtil.Log(
				$"new ship position: (dx:{ship.position.x:F4} dy:{ship.position.y:F4}).{Environment.NewLine}"
			);

			// TODO this might not work as expected, bouncing of screen bounds
			if (ship.position.x - ship.radius < bounds.xMin || ship.position.x + ship.radius > bounds.xMax) {
				ship.velocity.x *= -1; // XXX Divergence by multiplicative factor
				ship.position.x += ship.velocity.x * 2;
			}

			// TODO same
			if (ship.position.y - ship.radius < bounds.yMin || ship.position.y + ship.radius > bounds.yMax) {
				ship.velocity.y *= -1; // XXX Divergence by multiplicative factor 
				ship.position.y += ship.velocity.y * 2;
			}

			// TODO again
			for (int i = 0; i < Ship.MAX_BULLETS; i++) {
				Bullet bullet = ship.bullets[i];

				if (bullet.active) {
					bullet.position.x += bullet.velocity.x;
					bullet.position.y += bullet.velocity.y;

					// TODO could use .Within()
					if (bullet.position.x < bounds.xMin
					    || bullet.position.y < bounds.yMin
					    || bullet.position.x > bounds.xMax
					    || bullet.position.y > bounds.yMax) {
						bullet.active = false;
					} else {
						for (int j = 0; j < numShips; j++) {
							Ship other = ships[j];
							if (Vector2.Distance(bullet.position, other.position) >= other.radius) { continue; }

							ship.score++;
							other.health -= Bullet.BULLET_DAMAGE;
							bullet.active = false;
							break;
						}
					}
				}
			}
		}

		// NOTE called in VectorWar_AdvanceFrame, which is ggpo's advance_frame callback
		public void Update(VectorWar.ShipInput[] inputs, int disconnectFlags) {
			frameNumber++;
			for (int i = 0; i < numShips; i++) {
				float thrust;
				float heading;
				bool fire;

				if ((disconnectFlags & (1 << i)) != 0) {
					GetShipAI(i, out heading, out thrust, out fire);
				} else {
					ParseShipInputs(inputs[i], i, out heading, out thrust, out fire);
				}

				MoveShip(i, heading, thrust, fire);

				if (ships[i].cooldown != 0) {
					ships[i].cooldown--;
				}
			}
		}

		public void Deserialize(byte[] buffer) {
			GameState deserializedGameState = DeserializeInternal(buffer);
			frameNumber = deserializedGameState.frameNumber;
			bounds = deserializedGameState.bounds;
			numShips = deserializedGameState.numShips;

			// TODO dont wanna create garbaj
			ships = new Ship[MAX_SHIPS];
			for (int i = 0; i < MAX_SHIPS; i++) {
				// TODO not sure this'll work, might wanna use json for the time being?
				ships[i] = deserializedGameState.ships[i];
			}
		}

		// TODO probably don't want to create garbage here.
		private static GameState DeserializeInternal(byte[] buffer) {
			// TODO init in outer scope
			BinaryFormatter bf = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(buffer)) {
				return bf.Deserialize(ms) as GameState;
			}
		}

		public unsafe int Size() {
			return
				sizeof(int)
				+ sizeof(Rect)
				+ sizeof(int)
				+ Ship.Size() * MAX_SHIPS;
		}

		public void Serialize(int size, out byte[] buffer) {
			// TODO init in outer scope
			BinaryFormatter bf = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(size)) {
				bf.Serialize(ms, this);
				buffer = ms.ToArray();
			}
		}

		public byte[] Serialize() {
			Serialize(Size(), out byte[] serializedGameState);
			return serializedGameState;
		}

		public object Clone() {
			return new GameState(this);
		}
	}

	[Serializable]
	public struct Rect {
		public float xMin;
		public float xMax;
		public float yMin;
		public float yMax;

		public Rect(UnityEngine.Rect rect) {
			xMin = rect.xMin;
			xMax = rect.xMax;
			yMin = rect.yMin;
			yMax = rect.yMax;
		}
	}
}