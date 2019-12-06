using System;
using System.IO;
using System.Net;
using System.Text;
using GGPort;
using System.Threading;
using UnityEngine;
using Event = GGPort.Event;

//#define SYNC_TEST    // test: turn on synctest

namespace VectorWar {
	// Interface to the vector war application.
	public static class VectorWar {
		private const int kFrameDelay = 2;
		private const int kMaxPlayers = 64;

		private static GameState GameState = new GameState();
		private static readonly NonGameState NonGameState = new NonGameState();
		private static Session session = null;
		private static RendererWrapper renderer = RendererWrapper.instance;

		[Flags]
		public enum Input {
			InputThrust = 1,
			InputBreak = 1 << 1,
			InputRotateLeft = 1 << 2,
			InputRotateRight = 1 << 3,
			InputFire = 1 << 4,
			InputBomb = 1 << 5,
		};

		public static void Init(ushort localPort, int numPlayers, Player[] players, int numSpectators) {
			ErrorCode result;

			// Initialize the game state
			GameState.Init(numPlayers);
			NonGameState.num_players = numPlayers;

			// Fill in a ggpo callbacks structure to pass to start_session.
			SessionCallbacks sessionCallbacks = new SessionCallbacks {
				BeginGame = BeginGame,
				AdvanceFrame = AdvanceFrame,
				LoadGameState = LoadGameState,
				SaveGameState = SaveGameState,
				FreeBuffer = FreeBuffer,
				OnEvent = OnEvent,
				LogGameState = LogGameState
			};

#if SYNC_TEST
			result = GGPOMain.ggpo_start_synctest(ref ggpo, ref cb, "vectorwar", num_players, sizeof(int), 1);
#else
			result = Session.StartSession(out session, ref sessionCallbacks, "vectorwar", numPlayers, sizeof(int), localPort);
#endif

			// automatically disconnect clients after 3000 ms and start our count-down timer
			// for disconnects after 1000 ms.   To completely disable disconnects, simply use
			// a value of 0 for ggpo_set_disconnect_timeout.
			session.SetDisconnectTimeout(3000);
			session.SetDisconnectNotifyStart(1000);

			int i;
			for (i = 0; i < numPlayers + numSpectators; i++) {
				result = session.AddPlayer(ref players[i], out PlayerHandle handle);
				NonGameState.players[i].handle = handle;
				NonGameState.players[i].type = players[i].Type;
				if (players[i].Type == GGPOPlayerType.Local) {
					NonGameState.players[i].connect_progress = 100;
					NonGameState.local_player_handle = handle;
					NonGameState.SetConnectState(handle, PlayerConnectState.Connecting);
					session.SetFrameDelay(handle, kFrameDelay);
				} else {
					NonGameState.players[i].connect_progress = 0;
				}
			}

			PerfMon.ggpoutil_perfmon_init();
			renderer.SetStatusText("Connecting to peers.");
		}

		// Create a new spectator session
		private static Session spectatorSession; // TODO was this variable in the C++?
		public static void InitSpectator(ushort localPort, int numPlayers, IPEndPoint hostEndPoint) {
			ErrorCode result;

			// Initialize the game state
			GameState.Init(numPlayers);
			NonGameState.num_players = numPlayers;

			// Fill in a ggpo callbacks structure to pass to start_session.
			SessionCallbacks callbacks = new SessionCallbacks {
				BeginGame = BeginGame,
				AdvanceFrame = AdvanceFrame,
				LoadGameState = LoadGameState,
				SaveGameState = SaveGameState,
				FreeBuffer = FreeBuffer,
				OnEvent = OnEvent,
				LogGameState = LogGameState
			};

			result = Session.StartSpectating(
				out spectatorSession,
				ref callbacks,
				"vectorwar",
				numPlayers,
				sizeof(int),
				localPort,
				hostEndPoint
			);

			PerfMon.ggpoutil_perfmon_init();

			renderer.SetStatusText("Starting new spectator session");
		}
		
		// Disconnects a player from this session.
		public static void DisconnectPlayer(int player) {
			if (player >= NonGameState.num_players) { return; }

			ErrorCode result = session.DisconnectPlayer(NonGameState.players[player].handle);

			string logMsg = GGPort.Types.GGPOSucceeded(result)
				? $"Disconnected player {player}.{Environment.NewLine}"
				: $"Error while disconnecting player (err:{result}).{Environment.NewLine}";

			renderer.SetStatusText(logMsg);
		}

		// Draws the current frame without modifying the game state.
		public static void DrawCurrentFrame() {
			/*if (renderer != nullptr) {
				renderer.Draw(gs, ngs);
			}*/
			// TODO update unity visualization here
		}

		/*
		* Advances the game state by exactly 1 frame using the inputs specified
		* for player 1 and player 2.
		*/
		public static void AdvanceFrame(int[] inputs, int disconnectFlags) {
			GameState.Update(inputs, disconnectFlags);

			// update the checksums to display in the top of the window.  this
			// helps to detect desyncs.
			NonGameState.now.framenumber = GameState._framenumber;
			NonGameState.now.checksum = Fletcher32Checksum(GameState.Serialize());
			if (GameState._framenumber % 90 == 0) {
				NonGameState.periodic = NonGameState.now;
			}

			// Notify ggpo that we've moved forward exactly 1 frame.
			session.AdvanceFrame();

			// Update the performance monitor display.
			PlayerHandle[] handles = new PlayerHandle[kMaxPlayers];
			int count = 0;
			for (int i = 0; i < NonGameState.num_players; i++) {
				if (NonGameState.players[i].type == GGPOPlayerType.Remote) {
					handles[count++] = NonGameState.players[i].handle;
				}
			}

			PerfMon.ggpoutil_perfmon_update(ref session, handles, count);
		}
		
		/*
		* Read the inputs for player 1 from the keyboard.  We never have to
		* worry about player 2.  GGPO will handle remapping his inputs 
		* transparently.
		*/
		public static int ReadInputs() {
			Keybind[] inputtable = {
				new Keybind{ KeyCode = KeyCode.UpArrow, Input = Input.InputThrust },
				new Keybind{ KeyCode = KeyCode.DownArrow, Input = Input.InputBreak },
				new Keybind{ KeyCode = KeyCode.LeftArrow, Input = Input.InputRotateLeft },
				new Keybind{ KeyCode = KeyCode.RightArrow, Input = Input.InputRotateRight },
				new Keybind{ KeyCode = KeyCode.D, Input = Input.InputFire },
				new Keybind{ KeyCode = KeyCode.S, Input = Input.InputBomb }
			};
			
			int i, inputs = 0;
			
			for (i = 0; i < inputtable.Length; i++) {
				if (UnityEngine.Input.GetKey(inputtable[i].KeyCode)) {
					inputs |= (int) inputtable[i].Input;
				}
			}
   
			return inputs;
		}

		// Run a single frame of the game.
		private static readonly byte[] SerializedInput = new byte[4];
		public static void RunFrame() {
			ErrorCode result = ErrorCode.Success;
			int disconnectFlags = 0;
			Array inputs = new int[GameState.MAX_SHIPS];

			for (int i = 0; i < inputs.Length; i++) {
				inputs.SetValue(0, i);
			}

			if (NonGameState.local_player_handle.HandleValue != PlayerHandle.kInvalidHandle) {
				int input = ReadInputs();
#if SYNC_TEST
				input = rand(); // test: use random inputs to demonstrate sync testing
#endif
				SerializedInput[0] = (byte) input;
				SerializedInput[1] = (byte) (input >> 8);
				SerializedInput[2] = (byte) (input >> 16);
				SerializedInput[3] = (byte) (input >> 24);

				result = session.AddLocalInput(NonGameState.local_player_handle, SerializedInput, sizeof(int)); // NOTE hardcoding input type
			}

			// synchronize these inputs with ggpo.  If we have enough input to proceed
			// ggpo will modify the input list with the correct inputs to use and
			// return 1.
			if (GGPort.Types.GGPOSucceeded(result)) {
				result = session.SynchronizeInput(ref inputs, sizeof(int) * GameState.MAX_SHIPS, ref disconnectFlags);
				if (GGPort.Types.GGPOSucceeded(result)) {
					// inputs[0] and inputs[1] contain the inputs for p1 and p2.  Advance
					// the game by 1 frame using those inputs.
					AdvanceFrame(inputs as int[], disconnectFlags);
				}
			}

			DrawCurrentFrame();
		}

		/*
		* Spend our idle time in ggpo so it can use whatever time we have left over
		* for its internal bookkeeping.
		*/
		public static void Idle(int time) {
			session.Idle(time);
		}

		public static void Exit() {
			// TODO
			/*gs = TODO_memsetZero;
			ngs = TODO_memsetZero;*/

			if (session != null) {
				session.CloseSession();
				session = null;
			}

			renderer = null;
		}

		/* 
		* Simple checksum function stolen from wikipedia:
		*
		*   http://en.wikipedia.org/wiki/Fletcher%27s_checksum
		*/
		public static int Fletcher32Checksum(byte[] data) { // TODO fix
			short[] newData = new short[data.Length / 2];
			for (int i = 0; i < data.Length; i++) {
				Buffer.SetByte(newData, i, data[i]);
			}

			return Fletcher32Checksum(newData);
		}
		
		public static int Fletcher32Checksum(short[] data) {
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
		* The begin game callback.  We don't need to do anything special here,
		* so just return true.
		*/
		public static bool BeginGame(string game) {
			return true;
		}
		
		/*
		* Notification from GGPO that something has happened.  Update the status
		* text at the bottom of the screen to notify the user.
		*/
		public static bool OnEvent(ref Event info) {
			switch (info.code) {
				case EventCode.ConnectedToPeer:
					NonGameState.SetConnectState(info.connected.player, PlayerConnectState.Synchronizing);
					renderer.SetStatusText($"Connected to player {info.connected.player.HandleValue}");
					break;
				case EventCode.SynchronizingWithPeer:
					int progress = 100 * info.synchronizing.count / info.synchronizing.total;
					NonGameState.UpdateConnectProgress(info.synchronizing.player, progress);
					break;
				case EventCode.SynchronizedWithPeer:
					NonGameState.UpdateConnectProgress(info.synchronized.player, 100);
					break;
				case EventCode.Running:
					NonGameState.SetConnectState(PlayerConnectState.Running);
					renderer.SetStatusText("");
					break;
				case EventCode.ConnectionInterrupted:
					NonGameState.SetDisconnectTimeout(info.connectionInterrupted.player,
						Platform.GetCurrentTimeMS(),
						info.connectionInterrupted.disconnect_timeout);
					break;
				case EventCode.ConnectionResumed:
					NonGameState.SetConnectState(info.connectionResumed.player, PlayerConnectState.Running);
					break;
				case EventCode.DisconnectedFromPeer:
					NonGameState.SetConnectState(info.disconnected.player, PlayerConnectState.Disconnected);
					break;
				case EventCode.TimeSync:
					Thread.Sleep(1000 * info.timeSync.framesAhead / 60);
					break;
			}
			return true;
		}
		
		/*
		* Notification from GGPO we should step forward exactly 1 frame
		* during a rollback.
		*/
		public static bool AdvanceFrame(int flags) {
			Array inputs = new int[GameState.MAX_SHIPS];
			int disconnectFlags = 0;

			// Make sure we fetch new inputs from GGPO and use those to update
			// the game state instead of reading from the keyboard.
			session.SynchronizeInput(ref inputs, sizeof(int) * GameState.MAX_SHIPS, ref disconnectFlags);
			AdvanceFrame(inputs as int[], disconnectFlags);
			return true;
		}
		
		// Makes our current state match the state passed in by GGPO.
		public static bool LoadGameState(object gameState) {
			GameState = gameState as GameState;
			return true;
		}
		
		/*
		* Save the current state to a buffer and return it to GGPO via the
		* buffer and len parameters.
		*/
		public static bool SaveGameState(out object gameState, out int checksum, int frame) {
			gameState = GameState;
			GameState.Serialize(GameState.Size(), out byte[] buffer); // TODO probably a better way to get the checksum.
			checksum = Fletcher32Checksum(buffer);
			return true;
		}
		
		// Log the game state.  Used by the sync test debugging tool.
		public static bool LogGameState(string filename, object gameState) {
			FileStream fp = File.Open(filename, FileMode.OpenOrCreate, FileAccess.Write);

			GameState castedGameState = gameState as GameState;
			StringBuilder stringBuilder = new StringBuilder($"GameState object.{Environment.NewLine}");
			stringBuilder.Append($"  bounds: {castedGameState._bounds.xMin},{castedGameState._bounds.yMin} x {castedGameState._bounds.xMax},{castedGameState._bounds.yMax}.{Environment.NewLine}");
			stringBuilder.Append($"  num_ships: {castedGameState._num_ships}.{Environment.NewLine}");
			
			for (int i = 0; i < castedGameState._num_ships; i++) {
				Ship ship = castedGameState._ships[i];
				
				stringBuilder.Append($"  ship {i} position:  {ship.position.x:F4}, {ship.position.y:F4}{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} velocity:  {ship.deltaVelocity.x:F4}, {ship.deltaVelocity.y:F4}{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} radius:    {ship.radius}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} heading:   {ship.heading}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} health:    {ship.health}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} speed:     {ship.speed}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} cooldown:  {ship.cooldown}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} score:     {ship.score}.{Environment.NewLine}");
				
				for (int j = 0; j < Ship.MAX_BULLETS; j++) {
					Bullet bullet = ship.bullets[j];
					stringBuilder.Append($"  ship {i} bullet {j}: {bullet.position.x:F2} {bullet.position.y:F2} -> {bullet.velocity.x} {bullet.velocity.y}.{Environment.NewLine}");
				}
			}

			byte[] messageArr = Encoding.Default.GetBytes(stringBuilder.ToString());
			fp.Write(messageArr, 0, messageArr.Length);
			fp.Close();
			return true;
		}
		
		// Free a save state buffer previously returned in vw_save_game_state_callback.
		public static void FreeBuffer(object gameState) {
			//free(buffer); // NOTE nothing for managed lang, though could prove useful nonetheless.
		}
		
		public struct Keybind {
			public KeyCode KeyCode;
			public Input Input;
		}
	}
}