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
	public class GameState {
		public const int MAX_SHIPS = 4;

		public int _framenumber;
		public Rect _bounds;
		public int _num_ships;
		public Ship[] _ships = new Ship[MAX_SHIPS]; // TODO ecgh
		
		public GameState() { }

		public GameState(byte[] buffer) {
			Deserialize(buffer);
		}

		/*
		* InitGameState --
		*
		* Initialize our game state.
		*/
		public void Init(int num_players) {
			int i, w, h, r;

			_bounds = Screen.safeArea;

			w = Convert.ToInt32(_bounds.xMax - _bounds.xMin); // NOTE possible source of non-determinism
			h = Convert.ToInt32(_bounds.yMax - _bounds.yMin);
			r = h / 4;

			_framenumber = 0;
			_num_ships = num_players;

			for (int j = 0; j < _ships.Length; j++) { // TODO ecgh
				_ships[j] = new Ship();
			}
			
			for (i = 0; i < _num_ships; i++) {
				int heading = i * 360 / num_players;
				double cost, sint, theta;

				theta = heading * MathUtil.PI / 180;
				cost = Math.Cos(theta);
				sint = Math.Sin(theta);

				_ships[i].position.x = (float) (w / 2 + r * cost); // NOTE possible source of non-determinism
				_ships[i].position.y = (float) (h / 2 + r * sint); // NOTE possible source of non-determinism
				_ships[i].heading = (heading + 180) % 360;
				_ships[i].health = Ship.STARTING_HEALTH;
				_ships[i].radius = Ship.SHIP_RADIUS;
			}
		}

		public void GetShipAI(int i, out float heading, out float thrust, out int fire) {
			heading = (_ships[i].heading + 5) % 360;
			thrust = 0;
			fire = 0;
		}

		public void ParseShipInputs(int inputs, int i, out float heading, out float thrust, out int fire) {
			Ship ship = _ships[i];

			GGPortMain.ggpo_log(ref Globals.ggpo, $"parsing ship {i} inputs: {inputs}.\n");

			if ((inputs & (int) Globals.VectorWarInputs.INPUT_ROTATE_RIGHT) != 0) {
				heading = (ship.heading + Ship.ROTATE_INCREMENT) % 360;
			} else if ((inputs & (int) Globals.VectorWarInputs.INPUT_ROTATE_LEFT) != 0) {
				heading = (ship.heading - Ship.ROTATE_INCREMENT + 360) % 360;
			} else {
				heading = ship.heading;
			}

			if ((inputs & (int) Globals.VectorWarInputs.INPUT_THRUST) != 0) {
				thrust = Ship.SHIP_THRUST;
			} else if ((inputs & (int) Globals.VectorWarInputs.INPUT_BREAK) != 0) {
				thrust = -Ship.SHIP_THRUST;
			} else {
				thrust = 0;
			}

			fire = inputs & (int) Globals.VectorWarInputs.INPUT_FIRE;
		}

		public void MoveShip(int which, float heading, float thrust, int fire) {
			Ship ship = _ships[which];

			GGPortMain.ggpo_log(
				ref Globals.ggpo,
				$"calculation of new ship coordinates: (thrust:{thrust:F4} heading:{heading:F4}).\n"
			);

			ship.heading = (int) heading;

			if (ship.cooldown == 0) {
				if (fire != 0) {
					GGPortMain.ggpo_log(ref Globals.ggpo, "firing bullet.\n");
					for (int i = 0; i < Ship.MAX_BULLETS; i++) {
						float dx = (float) Math.Cos(
							MathUtil.degtorad(ship.heading)
						); // NOTE possible sources of non-determinism
						float dy = (float) Math.Sin(MathUtil.degtorad(ship.heading));

						if (!ship.bullets[i].active) {
							ship.bullets[i].active = true;
							ship.bullets[i].position.x =
								ship.position.x + (ship.radius * dx); // NOTE possible sources of non-determinism
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
				float dx = (float) (thrust
				                    * Math.Cos(MathUtil.degtorad(heading))); // NOTE possible sources of non-determinism
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

			GGPortMain.ggpo_log(
				ref Globals.ggpo,
				$"new ship velocity: (dx:{ship.velocity.x:F4} dy:{ship.velocity.y:F4}).\n"
			);

			ship.position.x += ship.velocity.x;
			ship.position.y += ship.velocity.y;
			GGPortMain.ggpo_log(
				ref Globals.ggpo,
				$"new ship position: (dx:{ship.position.x:F4} dy:{ship.position.y:F4}).\n"
			);

			// TODO this might not work as expected, bouncing of screen bounds
			if (ship.position.x - ship.radius < _bounds.xMin || ship.position.x + ship.radius > _bounds.xMax) {
				ship.velocity.x *= -1; // XXX Divergence by multiplicative factor
				ship.position.x += ship.velocity.x * 2;
			}

			// TODO same
			if (ship.position.y - ship.radius < _bounds.yMin || ship.position.y + ship.radius > _bounds.yMax) {
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
					if (bullet.position.x < _bounds.xMin
					    || bullet.position.y < _bounds.yMin
					    || bullet.position.x > _bounds.xMax
					    || bullet.position.y > _bounds.yMax) {
						bullet.active = false;
					} else {
						for (int j = 0; j < _num_ships; j++) {
							Ship other = _ships[j];
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
		public void Update(int[] inputs, int disconnect_flags) {
			_framenumber++;
			for (int i = 0; i < _num_ships; i++) {
				float thrust;
				float heading;
				int fire;

				if ((disconnect_flags & (1 << i)) != 0) {
					GetShipAI(i, out heading, out thrust, out fire);
				} else {
					ParseShipInputs(inputs[i], i, out heading, out thrust, out fire);
				}

				MoveShip(i, heading, thrust, fire);

				if (_ships[i].cooldown != 0) {
					_ships[i].cooldown--;
				}
			}
		}

		public void Deserialize(byte[] buffer) {
			GameState deserializedGameState = DeserializeInternal(buffer);
			_framenumber = deserializedGameState._framenumber;
			_bounds = deserializedGameState._bounds;
			_num_ships = deserializedGameState._num_ships;

			// TODO dont wanna create garbaj
			_ships = new Ship[MAX_SHIPS];
			for (int i = 0; i < MAX_SHIPS; i++) {
				// TODO not sure this'll work, might wanna use json for the time being?
				_ships[i] = deserializedGameState._ships[i];
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

			/*public int _framenumber;
			Rect _bounds;
			int _num_ships;
			Ship[] _ships = new Ship[MAX_SHIPS];*/
		}

		public void Serialize(ref byte[] buffer) {
			// TODO init in outer scope
			BinaryFormatter bf = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(buffer)) {
				bf.Serialize(ms, this);
			}
		}

		public byte[] Serialize() {
			byte[] serializedGameState = new byte[Size()];
			Serialize(ref serializedGameState);
			return serializedGameState;
		}
	}
}