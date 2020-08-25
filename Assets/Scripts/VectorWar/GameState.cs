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

namespace VectorWar {
	[Serializable]
	public class GameState : ICloneable {
		public const int MAX_SHIPS = 4;

		public int frameNumber;
		public Rect bounds;
		public int numShips;
		public Ship[] ships;
		
		//private static readonly 

		public GameState(int numPlayers, float boundsXMin, float boundsXMax, float boundsYMin, float boundsYMax) {
			bounds = new Rect(boundsXMin, boundsXMax, boundsYMin, boundsYMax);
			
			Fix64 screenWidth = bounds.xMax - bounds.xMin;
			Fix64 screenHeight = bounds.yMax - bounds.yMin;
			Fix64 centerOffset = screenHeight / (Fix64) 4;

			frameNumber = 0;
			numShips = numPlayers;

			ships = new Ship[MAX_SHIPS];
			for (int j = 0; j < ships.Length; j++) { ships[j] = new Ship(); }

			Fix64 baseRotation = Fix64.ThreeSixty / (Fix64) numPlayers;
			for (int i = 0; i < numShips; i++) {
				Fix64 heading = baseRotation * (Fix64) i;
				Fix64 thetaRad = heading * Fix64.DegToRad;

				ships[i].position.x = (screenWidth / (Fix64) 2 + centerOffset * Fix64.Cos(thetaRad));
				ships[i].position.y = (screenHeight / (Fix64) 2 + centerOffset * Fix64.Sin(thetaRad));
				ships[i].headingDeg = (heading + Fix64.OneEighty) % Fix64.ThreeSixty;
				ships[i].health = Ship.STARTING_HEALTH;
				ships[i].radius = Ship.shipRadius;
			}
		}

		private GameState(GameState other) {
			frameNumber = other.frameNumber;
			bounds = other.bounds;
			numShips = other.numShips;

			ships = new Ship[MAX_SHIPS];
			for (int i = 0; i < MAX_SHIPS; i++) { ships[i] = other.ships[i]; }
		}

		public void GetShipAI(int i, out Fix64 heading, out Fix64 thrust, out bool fire) {
			heading = (ships[i].headingDeg + (Fix64) 5) % Fix64.ThreeSixty;
			thrust = Fix64.Zero;
			fire = false;
		}

		public void ParseShipInputs(VectorWar.ShipInput inputFlags, int i, out Fix64 heading, out Fix64 thrust, out bool fire) {
			Ship ship = ships[i];

			LogUtil.Log($"parsing ship {i} inputFlags: {inputFlags}.{Environment.NewLine}");

			if (inputFlags.HasFlag(VectorWar.ShipInput.Clockwise)) {
				heading = (ship.headingDeg - Ship.rotateIncrement) % (Fix64) 360;
			} else if (inputFlags.HasFlag(VectorWar.ShipInput.CounterClockwise)) {
				heading = (ship.headingDeg + Ship.rotateIncrement) % (Fix64) 360;
			} else {
				heading = ship.headingDeg;
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
				$"calculation of new ship coordinates: (thrust:{thrust:F4} headingDeg:{heading:F4}).{Environment.NewLine}"
			);

			ship.headingDeg = heading;
			Fix64 headingRad = ship.headingDeg * Fix64.DegToRad;
			Fix64 cosHeading = Fix64.Cos(headingRad);
			Fix64 sinHeading = Fix64.Sin(headingRad);

			if (ship.cooldown == 0) {
				if (fire) {
					// TODO tag based log? to easily enable/disable entire categories
					LogUtil.Log($"Firing bullet.{Environment.NewLine}");
					for (int i = 0; i < Ship.MAX_BULLETS; i++) {
						if (ship.bullets[i].active) { continue; }

						ship.bullets[i].active = true;
						ship.bullets[i].position.x = ship.position.x + ship.radius * cosHeading;
						ship.bullets[i].position.y = ship.position.y + ship.radius * sinHeading;
						ship.bullets[i].velocity.x = ship.velocity.x + Bullet.bulletSpeed * cosHeading;
						ship.bullets[i].velocity.y = ship.velocity.y + Bullet.bulletSpeed * sinHeading;
						ship.cooldown = Bullet.BULLET_COOLDOWN;
						break;
					}
				}
			}

			if (thrust != Fix64.Zero) {
				ship.velocity.x += thrust * cosHeading;
				ship.velocity.y += thrust * sinHeading;
				
				Fix64 shipSpeed = Fix64.Sqrt(ship.velocity.x * ship.velocity.x + ship.velocity.y * ship.velocity.y);
				if (shipSpeed > Ship.shipMaxThrust) {
					ship.velocity.x = ship.velocity.x * Ship.shipMaxThrust / shipSpeed;
					ship.velocity.y = ship.velocity.y * Ship.shipMaxThrust / shipSpeed;
				}
			}

			LogUtil.Log($"new ship velocity: (dx:{ship.velocity.x:F4} dy:{ship.velocity.y:F4}).{Environment.NewLine}");

			ship.position.x += ship.velocity.x;
			ship.position.y += ship.velocity.y;
			LogUtil.Log($"new ship position: (dx:{ship.position.x:F4} dy:{ship.position.y:F4}).{Environment.NewLine}");
			
			if (ship.position.x - ship.radius < bounds.xMin || ship.position.x + ship.radius > bounds.xMax) {
				ship.velocity.x *= Fix64.NegativeOne;
				ship.position.x += ship.velocity.x * (Fix64) 2;
			}
			
			if (ship.position.y - ship.radius < bounds.yMin || ship.position.y + ship.radius > bounds.yMax) {
				ship.velocity.y *= Fix64.NegativeOne; 
				ship.position.y += ship.velocity.y * (Fix64) 2;
			}
			
			for (int i = 0; i < Ship.MAX_BULLETS; i++) {
				Bullet bullet = ship.bullets[i];
				if (!bullet.active) { continue; }

				bullet.position.x += bullet.velocity.x;
				bullet.position.y += bullet.velocity.y;

				// if outside bounds
				if (bullet.position.x < bounds.xMin
				    || bullet.position.y < bounds.yMin
				    || bullet.position.x > bounds.xMax
				    || bullet.position.y > bounds.yMax
				) {
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

				ship.bullets[i] = bullet;
			}
		}
		
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
}