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
	public class GameState {
		public const int MAX_SHIPS = 4;

		public int FrameNumber;
		public Rect Bounds;
		public int NumShips;
		public Ship[] Ships; // TODO ecgh

		public GameState() { }

		public GameState(byte[] buffer) {
			Deserialize(buffer);
		}

		/*
		* InitGameState --
		*
		* Initialize our game state.
		*/
		public void Init(int numPlayers) {
			int i, w, h, r;

			Bounds = new Rect(Screen.safeArea);

			w = Convert.ToInt32(Bounds.xMax - Bounds.xMin); // NOTE possible source of non-determinism
			h = Convert.ToInt32(Bounds.yMax - Bounds.yMin);
			r = h / 4;

			FrameNumber = 0;
			NumShips = numPlayers;
			
			Ships = new Ship[MAX_SHIPS];
			for (int j = 0; j < Ships.Length; j++) {
				Ships[j] = new Ship();
			}
			
			for (i = 0; i < NumShips; i++) {
				int heading = i * 360 / numPlayers;
				double cost, sint, theta;

				theta = heading * MathUtil.PI / 180;
				cost = Math.Cos(theta);
				sint = Math.Sin(theta);

				Ships[i].position.x = (float) (w / 2 + r * cost); // NOTE possible source of non-determinism
				Ships[i].position.y = (float) (h / 2 + r * sint); // NOTE possible source of non-determinism
				Ships[i].heading = (heading + 180) % 360;
				Ships[i].health = Ship.STARTING_HEALTH;
				Ships[i].radius = Ship.SHIP_RADIUS;
			}
		}

		public void GetShipAI(int i, out float heading, out float thrust, out int fire) {
			heading = (Ships[i].heading + 5) % 360;
			thrust = 0;
			fire = 0;
		}

		public void ParseShipInputs(int inputs, int i, out float heading, out float thrust, out int fire) {
			Ship ship = Ships[i];

			LogUtil.Log($"parsing ship {i} inputs: {inputs}.{Environment.NewLine}");

			if ((inputs & (int) VectorWar.Input.InputRotateRight) != 0) {
				heading = (ship.heading + Ship.ROTATE_INCREMENT) % 360;
			} else if ((inputs & (int) VectorWar.Input.InputRotateLeft) != 0) {
				heading = (ship.heading - Ship.ROTATE_INCREMENT + 360) % 360;
			} else {
				heading = ship.heading;
			}

			if ((inputs & (int) VectorWar.Input.InputThrust) != 0) {
				thrust = Ship.SHIP_THRUST;
			} else if ((inputs & (int) VectorWar.Input.InputBreak) != 0) {
				thrust = -Ship.SHIP_THRUST;
			} else {
				thrust = 0;
			}

			fire = inputs & (int) VectorWar.Input.InputFire;
		}

		public void MoveShip(int shipIndex, float heading, float thrust, int fire) {
			Ship ship = Ships[shipIndex];

			LogUtil.Log(
				$"calculation of new ship coordinates: (thrust:{thrust:F4} heading:{heading:F4}).{Environment.NewLine}"
			);

			ship.heading = (int) heading;

			if (ship.cooldown == 0) {
				if (fire != 0) {
					LogUtil.Log($"firing bullet.{Environment.NewLine}");
					for (int i = 0; i < Ship.MAX_BULLETS; i++) {
						float dx = (float) Math.Cos(MathUtil.degtorad(ship.heading)); // NOTE possible sources of non-determinism
						float dy = (float) Math.Sin(MathUtil.degtorad(ship.heading));

						if (!ship.bullets[i].active) {
							ship.bullets[i].active = true;
							ship.bullets[i].position.x = ship.position.x + (ship.radius * dx); // NOTE possible sources of non-determinism
							ship.bullets[i].position.y = ship.position.y + (ship.radius * dy);
							ship.bullets[i].velocity.x = ship.velocity.x + (Bullet.BULLET_SPEED * dx);
							ship.bullets[i].velocity.y = ship.velocity.y + (Bullet.BULLET_SPEED * dy);
							ship.cooldown = Bullet.BULLET_COOLDOWN;
							break;
						}
					}
				}
			}

			if (!MathUtil.Equals0(thrust)) {
				float dx = (float) (thrust * Math.Cos(MathUtil.degtorad(heading))); // NOTE possible sources of non-determinism
				float dy = (float) (thrust * Math.Sin(MathUtil.degtorad(heading)));

				ship.velocity.x += dx;
				ship.velocity.y += dy;
				float mag = (float) Math.Sqrt(
					ship.velocity.x * ship.velocity.x + ship.velocity.y * ship.velocity.y
				); // NOTE possible source of non-determinism
				if (mag > Ship.SHIP_MAX_THRUST) {
					ship.velocity.x = (ship.velocity.x * Ship.SHIP_MAX_THRUST) / mag;
					ship.velocity.y = (ship.velocity.y * Ship.SHIP_MAX_THRUST) / mag;
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
			if (ship.position.x - ship.radius < Bounds.xMin || ship.position.x + ship.radius > Bounds.xMax) {
				ship.velocity.x *= -1; // XXX Divergence by multiplicative factor
				ship.position.x += ship.velocity.x * 2;
			}

			// TODO same
			if (ship.position.y - ship.radius < Bounds.yMin || ship.position.y + ship.radius > Bounds.yMax) {
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
					if (bullet.position.x < Bounds.xMin
					    || bullet.position.y < Bounds.yMin
					    || bullet.position.x > Bounds.xMax
					    || bullet.position.y > Bounds.yMax) {
						bullet.active = false;
					} else {
						for (int j = 0; j < NumShips; j++) {
							Ship other = Ships[j];
							if (Vector2.Distance(bullet.position, other.position) < other.radius) {
								ship.score++;
								other.health -= Bullet.BULLET_DAMAGE;
								bullet.active = false;
								break;
							}
						}
					}
				}
			}
		}

		// NOTE called in VectorWar_AdvanceFrame, which is ggpo's advance_frame callback
		public void Update(int[] inputs, int disconnectFlags) {
			FrameNumber++;
			for (int i = 0; i < NumShips; i++) {
				float thrust;
				float heading;
				int fire;

				if ((disconnectFlags & (1 << i)) != 0) {
					GetShipAI(i, out heading, out thrust, out fire);
				} else {
					ParseShipInputs(inputs[i], i, out heading, out thrust, out fire);
				}

				MoveShip(i, heading, thrust, fire);

				if (Ships[i].cooldown != 0) {
					Ships[i].cooldown--;
				}
			}
		}

		public void Deserialize(byte[] buffer) {
			GameState deserializedGameState = DeserializeInternal(buffer);
			FrameNumber = deserializedGameState.FrameNumber;
			Bounds = deserializedGameState.Bounds;
			NumShips = deserializedGameState.NumShips;

			// TODO dont wanna create garbaj
			Ships = new Ship[MAX_SHIPS];
			for (int i = 0; i < MAX_SHIPS; i++) {
				// TODO not sure this'll work, might wanna use json for the time being?
				Ships[i] = deserializedGameState.Ships[i];
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