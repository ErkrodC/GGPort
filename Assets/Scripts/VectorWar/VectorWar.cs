using System;
using System.IO;
using System.Net;
using System.Text;
using GGPort;
using System.Threading;
using UnityEngine;
using static GGPort.GGPortMain;

//#define SYNC_TEST    // test: turn on synctest

namespace VectorWar {
	public static class Globals {
		/*
		* vectorwar.h --
		*
		* Interface to the vector war application.
		*
		*/

		public const int FRAME_DELAY = 2;
		public const int MAX_PLAYERS = 64;

		public static GameState gs = null;
		public static NonGameState ngs = null;
		public static GGPOSession ggpo = null;
		public static RendererWrapper renderer = RendererWrapper.instance;

		[Flags]
		public enum VectorWarInputs {
			INPUT_THRUST = (1 << 0),
			INPUT_BREAK = (1 << 1),
			INPUT_ROTATE_LEFT = (1 << 2),
			INPUT_ROTATE_RIGHT = (1 << 3),
			INPUT_FIRE = (1 << 4),
			INPUT_BOMB = (1 << 5),
		};

		public static void VectorWar_Init(ushort localport, int num_players, GGPOPlayer[] players, int num_spectators) {
			GGPOErrorCode result;

			// Initialize the game state
			gs.Init(num_players);
			ngs.num_players = num_players;

			// Fill in a ggpo callbacks structure to pass to start_session.
			GGPOSessionCallbacks cb = new GGPOSessionCallbacks {
				begin_game = vw_begin_game_callback,
				advance_frame = vw_advance_frame_callback,
				load_game_state = vw_load_game_state_callback,
				save_game_state = vw_save_game_state_callback,
				free_buffer = vw_free_buffer,
				on_event = vw_on_event_callback,
				log_game_state = vw_log_game_state
			};

#if SYNC_TEST
			result = GGPOMain.ggpo_start_synctest(ref ggpo, ref cb, "vectorwar", num_players, sizeof(int), 1);
#else
			result = ggpo_start_session(ref ggpo, ref cb, "vectorwar", num_players, sizeof(int), localport);
#endif

			// automatically disconnect clients after 3000 ms and start our count-down timer
			// for disconnects after 1000 ms.   To completely disable disconnects, simply use
			// a value of 0 for ggpo_set_disconnect_timeout.
			ggpo_set_disconnect_timeout(ref ggpo, 3000);
			ggpo_set_disconnect_notify_start(ref ggpo, 1000);

			int i;
			for (i = 0; i < num_players + num_spectators; i++) {
				result = ggpo_add_player(ref ggpo, ref players[i], out GGPOPlayerHandle handle);
				ngs.players[i].handle = handle;
				ngs.players[i].type = players[i].type;
				if (players[i].type == GGPOPlayerType.GGPO_PLAYERTYPE_LOCAL) {
					ngs.players[i].connect_progress = 100;
					ngs.local_player_handle = handle;
					ngs.SetConnectState(handle, PlayerConnectState.Connecting);
					ggpo_set_frame_delay(ref ggpo, handle, FRAME_DELAY);
				} else {
					ngs.players[i].connect_progress = 0;
				}
			}

			PerfMon.ggpoutil_perfmon_init();
			renderer.SetStatusText("Connecting to peers.");
		}

		/*
		* VectorWar_InitSpectator --
		*
		* Create a new spectator session
		*/
		public static void VectorWar_InitSpectator(ushort localport, int num_players, IPEndPoint hostEndPoint) {
			GGPOErrorCode result;

			// Initialize the game state
			gs.Init(num_players);
			ngs.num_players = num_players;

			// Fill in a ggpo callbacks structure to pass to start_session.
			GGPOSessionCallbacks cb = new GGPOSessionCallbacks {
				begin_game = vw_begin_game_callback,
				advance_frame = vw_advance_frame_callback,
				load_game_state = vw_load_game_state_callback,
				save_game_state = vw_save_game_state_callback,
				free_buffer = vw_free_buffer,
				on_event = vw_on_event_callback,
				log_game_state = vw_log_game_state
			};

			result = ggpo_start_spectating(
				ref ggpo,
				ref cb,
				"vectorwar",
				num_players,
				sizeof(int),
				localport,
				hostEndPoint
			);

			PerfMon.ggpoutil_perfmon_init();

			renderer.SetStatusText("Starting new spectator session");
		}
		
		/*
		* VectorWar_DisconnectPlayer --
		*
		* Disconnects a player from this session.
		*/
		public static void VectorWar_DisconnectPlayer(int player) {
			if (player >= ngs.num_players) { return; }

			GGPOErrorCode result = ggpo_disconnect_player(ref ggpo, ngs.players[player].handle);

			string logMsg = GGPort.Globals.GGPO_SUCCEEDED(result)
				? $"Disconnected player {player}.\n"
				: $"Error while disconnecting player (err:{result}).\n";

			renderer.SetStatusText(logMsg);
		}

		/*
		* VectorWar_DrawCurrentFrame --
		*
		* Draws the current frame without modifying the game state.
		*/
		public static void VectorWar_DrawCurrentFrame() {
			/*if (renderer != nullptr) {
				renderer.Draw(gs, ngs);
			}*/
		}

		/*
		* VectorWar_AdvanceFrame --
		*
		* Advances the game state by exactly 1 frame using the inputs specified
		* for player 1 and player 2.
		*/
		public static void VectorWar_AdvanceFrame(int[] inputs, int disconnect_flags) {
			gs.Update(inputs, disconnect_flags);

			// update the checksums to display in the top of the window.  this
			// helps to detect desyncs.
			ngs.now.framenumber = gs._framenumber;
			ngs.now.checksum = fletcher32_checksum(gs.Serialize());
			if ((gs._framenumber % 90) == 0) {
				ngs.periodic = ngs.now;
			}

			// Notify ggpo that we've moved forward exactly 1 frame.
			ggpo_advance_frame(ref ggpo);

			// Update the performance monitor display.
			GGPOPlayerHandle[] handles = new GGPOPlayerHandle[MAX_PLAYERS];
			int count = 0;
			for (int i = 0; i < ngs.num_players; i++) {
				if (ngs.players[i].type == GGPOPlayerType.GGPO_PLAYERTYPE_REMOTE) {
					handles[count++] = ngs.players[i].handle;
				}
			}

			PerfMon.ggpoutil_perfmon_update(ref ggpo, handles, count);
		}
		
		/*
		* ReadInputs --
		*
		* Read the inputs for player 1 from the keyboard.  We never have to
		* worry about player 2.  GGPO will handle remapping his inputs 
		* transparently.
		*/
		public static int ReadInputs() {
			Inputtable[] inputtable = {
				new Inputtable{ key = KeyCode.UpArrow, input = VectorWarInputs.INPUT_THRUST },
				new Inputtable{ key = KeyCode.DownArrow, input = VectorWarInputs.INPUT_BREAK },
				new Inputtable{ key = KeyCode.LeftArrow, input = VectorWarInputs.INPUT_ROTATE_LEFT },
				new Inputtable{ key = KeyCode.RightArrow, input = VectorWarInputs.INPUT_ROTATE_RIGHT },
				new Inputtable{ key = KeyCode.D, input = VectorWarInputs.INPUT_FIRE },
				new Inputtable{ key = KeyCode.S, input = VectorWarInputs.INPUT_BOMB }
			};
			
			int i, inputs = 0;
			
			for (i = 0; i < inputtable.Length; i++) {
				if (Input.GetKey(inputtable[i].key)) {
					inputs |= (int) inputtable[i].input;
				}
			}
   
			return inputs;
		}

		/*
		* VectorWar_RunFrame --
		*
		* Run a single frame of the game.
		*/
		public static void VectorWar_RunFrame() {
			GGPOErrorCode result = GGPOErrorCode.GGPO_OK;
			int disconnect_flags = 0;
			int[] inputs = new int[GameState.MAX_SHIPS];

			for (int i = 0; i < inputs.Length; i++) {
				inputs[i] = 0;
			}

			if (ngs.local_player_handle.handleValue != GGPOPlayerHandle.GGPO_INVALID_HANDLE) {
				int input = ReadInputs();
#if SYNC_TEST
				input = rand(); // test: use random inputs to demonstrate sync testing
#endif
				result = ggpo_add_local_input(ref ggpo, ngs.local_player_handle, input, sizeof(int)); // NOTE hardcoding input type
			}

			// synchronize these inputs with ggpo.  If we have enough input to proceed
			// ggpo will modify the input list with the correct inputs to use and
			// return 1.
			if (GGPort.Globals.GGPO_SUCCEEDED(result)) {
				result = ggpo_synchronize_input(ref ggpo, inputs, sizeof(int) * GameState.MAX_SHIPS, disconnect_flags);
				if (GGPort.Globals.GGPO_SUCCEEDED(result)) {
					// inputs[0] and inputs[1] contain the inputs for p1 and p2.  Advance
					// the game by 1 frame using those inputs.
					VectorWar_AdvanceFrame(inputs, disconnect_flags);
				}
			}

			VectorWar_DrawCurrentFrame();
		}

		/*
		* VectorWar_Idle --
		*
		* Spend our idle time in ggpo so it can use whatever time we have left over
		* for its internal bookkeeping.
		*/
		public static void VectorWar_Idle(int time) {
			ggpo_idle(ref ggpo, time);
		}

		public static void VectorWar_Exit() {
			// TODO
			/*gs = TODO_memsetZero;
			ngs = TODO_memsetZero;*/

			if (ggpo != null) {
				ggpo_close_session(ref ggpo);
				ggpo = null;
			}

			renderer = null;
		}

		/* 
		* Simple checksum function stolen from wikipedia:
		*
		*   http://en.wikipedia.org/wiki/Fletcher%27s_checksum
		*/

		public static int fletcher32_checksum(byte[] data) { // TODO fix
			short[] newData = new short[data.Length / 2];
			for (int i = 0; i < data.Length; i++) {
				Buffer.SetByte(newData, i, data[i]);
			}

			return fletcher32_checksum(newData);
		}
		
		public static int fletcher32_checksum(short[] data) {
			int sum1 = 0xffff, sum2 = 0xffff;

			int i = 0;
			int len = data.Length;
			while (len != 0) {
				int tlen = Math.Max(len, 360);
				len -= tlen;
				do {
					sum1 += data[i++];
					sum2 += sum1;
				} while (--tlen != 0);

				sum1 = (sum1 & 0xffff) + (sum1 >> 16);
				sum2 = (sum2 & 0xffff) + (sum2 >> 16);
			}

			/* Second reduction step to reduce sums to 16 bits */
			sum1 = (sum1 & 0xffff) + (sum1 >> 16);
			sum2 = (sum2 & 0xffff) + (sum2 >> 16);
			return sum2 << 16 | sum1;
		}
		
		/*
		* vw_begin_game_callback --
		*
		* The begin game callback.  We don't need to do anything special here,
		* so just return true.
		*/
		public static bool vw_begin_game_callback(string game) {
			return true;
		}
		
		/*
		* vw_on_event_callback --
		*
		* Notification from GGPO that something has happened.  Update the status
		* text at the bottom of the screen to notify the user.
		*/
		public static bool vw_on_event_callback(ref GGPOEvent info) {
			int progress;
			switch (info.code) {
				case GGPOEventCode.GGPO_EVENTCODE_CONNECTED_TO_PEER:
					ngs.SetConnectState(info.connected.player, PlayerConnectState.Synchronizing);
					break;
				case GGPOEventCode.GGPO_EVENTCODE_SYNCHRONIZING_WITH_PEER:
					progress = 100 * info.synchronizing.count / info.synchronizing.total;
					ngs.UpdateConnectProgress(info.synchronizing.player, progress);
					break;
				case GGPOEventCode.GGPO_EVENTCODE_SYNCHRONIZED_WITH_PEER:
					ngs.UpdateConnectProgress(info.synchronized.player, 100);
					break;
				case GGPOEventCode.GGPO_EVENTCODE_RUNNING:
					ngs.SetConnectState(PlayerConnectState.Running);
					renderer.SetStatusText("");
					break;
				case GGPOEventCode.GGPO_EVENTCODE_CONNECTION_INTERRUPTED:
					ngs.SetDisconnectTimeout(info.connection_interrupted.player,
						Platform.GetCurrentTimeMS(),
						info.connection_interrupted.disconnect_timeout);
					break;
				case GGPOEventCode.GGPO_EVENTCODE_CONNECTION_RESUMED:
					ngs.SetConnectState(info.connection_resumed.player, PlayerConnectState.Running);
					break;
				case GGPOEventCode.GGPO_EVENTCODE_DISCONNECTED_FROM_PEER:
					ngs.SetConnectState(info.disconnected.player, PlayerConnectState.Disconnected);
					break;
				case GGPOEventCode.GGPO_EVENTCODE_TIMESYNC:
					Thread.Sleep(1000 * info.timesync.frames_ahead / 60);
					break;
			}
			return true;
		}
		
		/*
		* vw_advance_frame_callback --
		*
		* Notification from GGPO we should step foward exactly 1 frame
		* during a rollback.
		*/
		public static bool vw_advance_frame_callback(int flags) {
			int[] inputs = new int[GameState.MAX_SHIPS];
			int disconnect_flags = 0;

			// Make sure we fetch new inputs from GGPO and use those to update
			// the game state instead of reading from the keyboard.
			ggpo_synchronize_input(ref ggpo, inputs, sizeof(int) * GameState.MAX_SHIPS, disconnect_flags);
			VectorWar_AdvanceFrame(inputs, disconnect_flags);
			return true;
		}
		
		/*
		* vw_load_game_state_callback --
		*
		* Makes our current state match the state passed in by GGPO.
		*/
		public static bool vw_load_game_state_callback(byte[] buffer) {
			gs.Deserialize(buffer);
			return true;
		}
		
		/*
		* vw_save_game_state_callback --
		*
		* Save the current state to a buffer and return it to GGPO via the
		* buffer and len parameters.
		*/
		public static bool vw_save_game_state_callback(ref byte[] buffer, ref int len, ref int checksum, int frame) {
			len = gs.Size();
			gs.Serialize(ref buffer);
			checksum = fletcher32_checksum(buffer);
			return true;
		}
		
		/*
		* vw_log_game_state --
		*
		* Log the gamestate.  Used by the synctest debugging tool.
		*/
		public static bool vw_log_game_state(string filename, byte[] buffer, int len) {
			FileStream fp = File.Open(filename, FileMode.OpenOrCreate, FileAccess.Write);

			GameState gamestate = new GameState(buffer);
			StringBuilder stringBuilder = new StringBuilder("GameState object.\n");
			stringBuilder.Append($"  bounds: {gamestate._bounds.xMin},{gamestate._bounds.yMin} x {gamestate._bounds.xMax},{gamestate._bounds.yMax}.\n");
			stringBuilder.Append($"  num_ships: {gamestate._num_ships}.\n");
			
			for (int i = 0; i < gamestate._num_ships; i++) {
				Ship ship = gamestate._ships[i];
				
				stringBuilder.Append($"  ship {i} position:  {ship.position.x:F4}, {ship.position.y:F4}\n");
				stringBuilder.Append($"  ship {i} velocity:  {ship.velocity.x:F4}, {ship.velocity.y:F4}\n");
				stringBuilder.Append($"  ship {i} radius:    {ship.radius}.\n");
				stringBuilder.Append($"  ship {i} heading:   {ship.heading}.\n");
				stringBuilder.Append($"  ship {i} health:    {ship.health}.\n");
				stringBuilder.Append($"  ship {i} speed:     {ship.speed}.\n");
				stringBuilder.Append($"  ship {i} cooldown:  {ship.cooldown}.\n");
				stringBuilder.Append($"  ship {i} score:     {ship.score}.\n");
				
				for (int j = 0; j < Ship.MAX_BULLETS; j++) {
					Bullet bullet = ship.bullets[j];
					stringBuilder.Append($"  ship {i} bullet {j}: {bullet.position.x:F2} {bullet.position.y:F2} -> {bullet.velocity.x} {bullet.velocity.y}.\n");
				}
			}

			byte[] messageArr = Encoding.Default.GetBytes(stringBuilder.ToString());
			fp.Write(messageArr, 0, messageArr.Length);
			fp.Close();
			return true;
		}
		
		/*
		* vw_free_buffer --
		*
		* Free a save state buffer previously returned in vw_save_game_state_callback.
		*/
		public static void vw_free_buffer(byte[] buffer) {
			//free(buffer); // NOTE nothing for managed lang, though could prove useful nonetheless.
		}
		
		public struct Inputtable {
			public KeyCode key;
			public VectorWarInputs input;
		}
	}
}