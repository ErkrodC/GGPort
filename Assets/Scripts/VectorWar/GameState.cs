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
using FixMath.NET;
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
		public void Init(int numPlayers, float boundsXMin, float boundsXMax, float boundsYMin, float boundsYMax) {
			Fix64 w, h, r;

			bounds = new Rect(boundsXMin, boundsXMax, boundsYMin, boundsYMax);

			// NOTE possible source of non-determinism
			w = bounds.xMax - bounds.xMin;
			h = bounds.yMax - bounds.yMin;
			r = h / (Fix64) 4;

			frameNumber = 0;
			numShips = numPlayers;

			ships = new Ship[MAX_SHIPS];
			for (int j = 0; j < ships.Length; j++) {
				ships[j] = new Ship();
			}

			for (int i = 0; i < numShips; i++) {
				Fix64 heading = (Fix64) (i * 360 / numPlayers);
				Fix64 theta = heading * Fix64.PiOver180;
				Fix64 cost = Fix64.Cos(theta);
				Fix64 sint = Fix64.Sin(theta);

				ships[i].position.x = (w / (Fix64) 2 + r * cost); // NOTE possible source of non-determinism
				ships[i].position.y = (h / (Fix64) 2 + r * sint); // NOTE possible source of non-determinism
				ships[i].heading = (heading + Fix64.OneEighty) % Fix64.ThreeSixty;
				ships[i].health = Ship.STARTING_HEALTH;
				ships[i].radius = Ship.shipRadius;
			}
		}

		public void GetShipAI(int i, out Fix64 heading, out Fix64 thrust, out bool fire) {
			heading = (ships[i].heading + (Fix64) 5) % Fix64.ThreeSixty;
			thrust = Fix64.Zero;
			fire = false;
		}

		public void ParseShipInputs(VectorWar.ShipInput inputFlags, int i, out Fix64 heading, out Fix64 thrust, out bool fire) {
			Ship ship = ships[i];

			LogUtil.Log($"parsing ship {i} inputFlags: {inputFlags}.{Environment.NewLine}");

			if (inputFlags.HasFlag(VectorWar.ShipInput.Clockwise)) {
				heading = (ship.heading - Ship.rotateIncrement) % (Fix64) 360;
			} else if (inputFlags.HasFlag(VectorWar.ShipInput.CounterClockwise)) {
				heading = (ship.heading + Ship.rotateIncrement) % (Fix64) 360;
			} else {
				heading = ship.heading;
			}

			if (inputFlags.HasFlag(VectorWar.ShipInput.Thrust)) {
				thrust = Ship.shipThrust;
			} else if (inputFlags.HasFlag(VectorWar.ShipInput.Brake)) {
				thrust = -Ship.shipThrust;
			} else {
				thrust = Fix64.Zero;
			}

			fire = inputFlags.HasFlag(VectorWar.ShipInput.Fire);
		}

		public void MoveShip(int shipIndex, Fix64 heading, Fix64 thrust, bool fire) {
			Ship ship = ships[shipIndex];

			LogUtil.Log(
				$"calculation of new ship coordinates: (thrust:{thrust:F4} heading:{heading:F4}).{Environment.NewLine}"
			);

			ship.heading = heading;

			if (ship.cooldown == 0) {
				if (fire) {
					LogUtil.Log(
						$"Firing bullet.{Environment.NewLine}"
					); // TODO tag based log? to easily enable/disable entire categories
					for (int i = 0; i < Ship.MAX_BULLETS; i++) {
						// NOTE possible sources of non-determinism
						Fix64 dx = Fix64.Cos(Fix64.DegToRad(ship.heading));
						Fix64 dy = Fix64.Sin(Fix64.DegToRad(ship.heading));

						if (ship.bullets[i].active) { continue; }

						ship.bullets[i].active = true;
						// NOTE possible sources of non-determinism
						ship.bullets[i].position.x = ship.position.x + ship.radius * dx;
						ship.bullets[i].position.y = ship.position.y + ship.radius * dy;
						ship.bullets[i].velocity.x = ship.velocity.x + Bullet.bulletSpeed * dx;
						ship.bullets[i].velocity.y = ship.velocity.y + Bullet.bulletSpeed * dy;
						ship.cooldown = Bullet.BULLET_COOLDOWN;
						break;
					}
				}
			}

			if (thrust != Fix64.Zero) {
				// NOTE possible sources of non-determinism
				Fix64 dx = (thrust * Fix64.Cos(Fix64.DegToRad(heading)));
				Fix64 dy = (thrust * Fix64.Sin(Fix64.DegToRad(heading)));

				ship.velocity.x += dx;
				ship.velocity.y += dy;
				// NOTE possible source of non-determinism
				Fix64 mag = Fix64.Sqrt(ship.velocity.x * ship.velocity.x + ship.velocity.y * ship.velocity.y);
				if (mag > Ship.shipMaxThrust) {
					ship.velocity.x = ship.velocity.x * Ship.shipMaxThrust / mag;
					ship.velocity.y = ship.velocity.y * Ship.shipMaxThrust / mag;
				}
			}

			LogUtil.Log($"new ship velocity: (dx:{ship.velocity.x:F4} dy:{ship.velocity.y:F4}).{Environment.NewLine}");

			ship.position.x += ship.velocity.x;
			ship.position.y += ship.velocity.y;
			LogUtil.Log($"new ship position: (dx:{ship.position.x:F4} dy:{ship.position.y:F4}).{Environment.NewLine}");

			// TODO this might not work as expected, bouncing of screen bounds
			if (ship.position.x - ship.radius < bounds.xMin || ship.position.x + ship.radius > bounds.xMax) {
				ship.velocity.x *= (Fix64) (-1); // XXX Divergence by multiplicative factor
				ship.position.x += ship.velocity.x * (Fix64) 2;
			}

			// TODO same
			if (ship.position.y - ship.radius < bounds.yMin || ship.position.y + ship.radius > bounds.yMax) {
				ship.velocity.y *= (Fix64) (-1); // XXX Divergence by multiplicative factor 
				ship.position.y += ship.velocity.y * (Fix64) 2;
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
							if (FixVector2.Distance(bullet.position, other.position) >= other.radius) { continue; }

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
				Fix64 thrust;
				Fix64 heading;
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
		public Fix64 xMin;
		public Fix64 xMax;
		public Fix64 yMin;
		public Fix64 yMax;

		public Rect(float xMin, float xMax, float yMin, float yMax) {
			this.xMin = (Fix64) xMin;
			this.xMax = (Fix64) xMax;
			this.yMin = (Fix64) yMin;
			this.yMax = (Fix64) yMax;
		}
	}
}