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
		private const int kFrameDelay = 0;
		private const int kMaxPlayers = 64;

		private static GameState gameState = new GameState();
		private static readonly NonGameState kNonGameState = new NonGameState();
		private static Session session = null;
		public static event SessionCallbacks.LogDelegate LogCallback = delegate(string message) {  };

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
			gameState.Init(numPlayers);
			kNonGameState.NumPlayers = numPlayers;

			// Fill in a ggpo callbacks structure to pass to start_session.
			SessionCallbacks sessionCallbacks = new SessionCallbacks {
				BeginGame = BeginGame,
				AdvanceFrame = AdvanceFrame,
				LoadGameState = LoadGameState,
				SaveGameState = SaveGameState,
				FreeBuffer = FreeBuffer,
				OnEvent = OnEvent,
				LogGameState = LogGameState,
#if SHOW_LOG				
				LogText = LogCallback
#endif
			};

#if SYNC_TEST
			result = GGPOMain.ggpo_start_synctest(ref ggpo, ref cb, "vectorwar", num_players, sizeof(int), 1);
#else
			result = Session.StartSession(out session, sessionCallbacks, "VectorWar", numPlayers, sizeof(int), localPort);
#endif

			// automatically disconnect clients after 3000 ms and start our count-down timer
			// for disconnects after 1000 ms.   To completely disable disconnects, simply use
			// a value of 0 for ggpo_set_disconnect_timeout.
			session.SetDisconnectTimeout(3000);
			session.SetDisconnectNotifyStart(1000);

			int i;
			for (i = 0; i < numPlayers + numSpectators; i++) {
				result = session.AddPlayer(players[i], out PlayerHandle handle);
				kNonGameState.Players[i].Handle = handle;
				kNonGameState.Players[i].Type = players[i].Type;
				if (players[i].Type == PlayerType.Local) {
					kNonGameState.Players[i].ConnectProgress = 100;
					kNonGameState.LocalPlayerHandle = handle;
					kNonGameState.SetConnectState(handle, PlayerConnectState.Connecting);
					session.SetFrameDelay(handle, kFrameDelay);
				} else {
					kNonGameState.Players[i].ConnectProgress = 0;
				}
			}

			PerfMon.ggpoutil_perfmon_init();
			GameRenderer.instance.SetStatusText("Connecting to peers.");
		}

		// Create a new spectator session
		private static Session spectatorSession; // TODO was this variable in the C++?
		public static void InitSpectator(ushort localPort, int numPlayers, IPEndPoint hostEndPoint) {
			ErrorCode result;

			// Initialize the game state
			gameState.Init(numPlayers);
			kNonGameState.NumPlayers = numPlayers;

			// Fill in a ggpo callbacks structure to pass to start_session.
			SessionCallbacks callbacks = new SessionCallbacks {
				BeginGame = BeginGame,
				AdvanceFrame = AdvanceFrame,
				LoadGameState = LoadGameState,
				SaveGameState = SaveGameState,
				FreeBuffer = FreeBuffer,
				OnEvent = OnEvent,
				LogGameState = LogGameState,
#if SHOW_LOG
				LogText = LogCallback
#endif
			};

			result = Session.StartSpectating(
				out spectatorSession,
				callbacks,
				"VectorWar",
				numPlayers,
				sizeof(int),
				localPort,
				hostEndPoint
			);

			PerfMon.ggpoutil_perfmon_init();

			GameRenderer.instance.SetStatusText("Starting new spectator session");
		}
		
		// Disconnects a player from this session.
		public static void DisconnectPlayer(int player) {
			if (player >= kNonGameState.NumPlayers) { return; }

			ErrorCode result = session.DisconnectPlayer(kNonGameState.Players[player].Handle);

			string logMsg = result.IsSuccess()
				? $"Disconnected player {player}.{Environment.NewLine}"
				: $"Error while disconnecting player (err:{result}).{Environment.NewLine}";

			GameRenderer.instance.SetStatusText(logMsg);
		}

		// Draws the current frame without modifying the game state.
		public static void DrawCurrentFrame() {
			
				GameRenderer.instance.Draw(gameState, kNonGameState);
			// TODO update unity visualization here
		}

		/*
		* Advances the game state by exactly 1 frame using the inputs specified
		* for player 1 and player 2.
		*/
		public static void AdvanceFrame(int[] inputs, int disconnectFlags) {
			gameState.Update(inputs, disconnectFlags);

			// update the checksums to display in the top of the window.  this
			// helps to detect desyncs.
			kNonGameState.Now.FrameNumber = gameState.FrameNumber;
			kNonGameState.Now.Checksum = Fletcher32Checksum(gameState.Serialize());
			if (gameState.FrameNumber % 90 == 0) {
				kNonGameState.Periodic = kNonGameState.Now;
			}

			// Notify ggpo that we've moved forward exactly 1 frame.
			session.AdvanceFrame();

			// Update the performance monitor display.
			PlayerHandle[] handles = new PlayerHandle[kMaxPlayers];
			int count = 0;
			for (int i = 0; i < kNonGameState.NumPlayers; i++) {
				if (kNonGameState.Players[i].Type == PlayerType.Remote) {
					handles[count++] = kNonGameState.Players[i].Handle;
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
				} else if (inputtable[i].KeyCode == KeyCode.LeftArrow) {
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
			int[] inputs = new int[GameState.kMaxShips];

			for (int i = 0; i < inputs.Length; i++) { inputs[i] = 0; }

			if (kNonGameState.LocalPlayerHandle.HandleValue != PlayerHandle.kInvalidHandle) {
				int input = ReadInputs();
#if SYNC_TEST
				input = rand(); // test: use random inputs to demonstrate sync testing
#endif
				SerializedInput[0] = (byte) input;
				SerializedInput[1] = (byte) (input >> 8);
				SerializedInput[2] = (byte) (input >> 16);
				SerializedInput[3] = (byte) (input >> 24);

				// XXX LOH size should 4 bytes? check inside, erroneously using serializedInputLength?
				result = session.AddLocalInput(kNonGameState.LocalPlayerHandle, SerializedInput, sizeof(int)); // NOTE hardcoding input type
			}

			// synchronize these inputs with ggpo.  If we have enough input to proceed
			// ggpo will modify the input list with the correct inputs to use and
			// return 1.
			if (result.IsSuccess()) {
				result = session.SynchronizeInput(inputs, sizeof(int) * GameState.kMaxShips, ref disconnectFlags);
				if (result.IsSuccess()) {
					// inputs[0] and inputs[1] contain the inputs for p1 and p2.  Advance
					// the game by 1 frame using those inputs.
					AdvanceFrame(inputs, disconnectFlags);
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

			GameRenderer.instance = null;
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
		// TODO refactor to C# events
		public static bool OnEvent(Event info) {
			switch (info.code) {
				case EventCode.ConnectedToPeer:
					kNonGameState.SetConnectState(info.connected.player, PlayerConnectState.Synchronizing);
					GameRenderer.instance.SetStatusText($"Connected to player {info.connected.player.HandleValue}");
					break;
				case EventCode.SynchronizingWithPeer:
					int progress = 100 * info.synchronizing.count / info.synchronizing.total;
					kNonGameState.UpdateConnectProgress(info.synchronizing.player, progress);
					break;
				case EventCode.SynchronizedWithPeer:
					kNonGameState.UpdateConnectProgress(info.synchronized.player, 100);
					break;
				case EventCode.Running:
					kNonGameState.SetConnectState(PlayerConnectState.Running);
					GameRenderer.instance.SetStatusText("");
					break;
				case EventCode.ConnectionInterrupted:
					kNonGameState.SetDisconnectTimeout(info.connectionInterrupted.player,
						Platform.GetCurrentTimeMS(),
						info.connectionInterrupted.disconnect_timeout);
					break;
				case EventCode.ConnectionResumed:
					kNonGameState.SetConnectState(info.connectionResumed.player, PlayerConnectState.Running);
					break;
				case EventCode.DisconnectedFromPeer:
					kNonGameState.SetConnectState(info.disconnected.player, PlayerConnectState.Disconnected);
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
			int[] inputs = new int[GameState.kMaxShips];
			int disconnectFlags = 0;

			// Make sure we fetch new inputs from GGPO and use those to update
			// the game state instead of reading from the keyboard.
			session.SynchronizeInput(inputs, sizeof(int) * GameState.kMaxShips, ref disconnectFlags);
			AdvanceFrame(inputs, disconnectFlags);
			return true;
		}
		
		// Makes our current state match the state passed in by GGPO.
		public static bool LoadGameState(object gameState) {
			VectorWar.gameState = gameState as GameState;
			return true;
		}
		
		/*
		* Save the current state to a buffer and return it to GGPO via the
		* buffer and len parameters.
		*/
		public static bool SaveGameState(out object gameState, out int checksum, int frame) {
			gameState = VectorWar.gameState;
			VectorWar.gameState.Serialize(VectorWar.gameState.Size(), out byte[] buffer); // TODO probably a better way to get the checksum.
			checksum = Fletcher32Checksum(buffer);
			return true;
		}
		
		// Log the game state.  Used by the sync test debugging tool.
		public static bool LogGameState(string filename, object gameState) {
			FileStream fp = File.Open(filename, FileMode.OpenOrCreate, FileAccess.Write);

			GameState castedGameState = gameState as GameState;
			StringBuilder stringBuilder = new StringBuilder($"GameState object.{Environment.NewLine}");
			stringBuilder.Append($"  bounds: {castedGameState.Bounds.xMin},{castedGameState.Bounds.yMin} x {castedGameState.Bounds.xMax},{castedGameState.Bounds.yMax}.{Environment.NewLine}");
			stringBuilder.Append($"  num_ships: {castedGameState.NumShips}.{Environment.NewLine}");
			
			for (int i = 0; i < castedGameState.NumShips; i++) {
				Ship ship = castedGameState.Ships[i];
				
				stringBuilder.Append($"  ship {i} position:  {ship.position.x:F4}, {ship.position.y:F4}{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} velocity:  {ship.velocity.x:F4}, {ship.velocity.y:F4}{Environment.NewLine}");
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