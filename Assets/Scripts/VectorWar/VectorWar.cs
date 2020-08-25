using System;
using System.IO;
using System.Net;
using System.Text;
using GGPort;
using System.Threading;

//#define SYNC_TEST    // test: turn on synctest

namespace VectorWar {
	// Interface to the vector war application.
	public static class VectorWar {
		public static event LogTextDelegate logTextEvent;

		private const int _FRAME_DELAY = 0;
		private const int _MAX_PLAYERS = 64;
		private const int _SIZE_OF_INPUT = sizeof(ShipInput);
		private const int _FRAMES_PER_SECOND = 60;

		private static GameState _gameState;
		private static NonGameState _nonGameState;
		private static Session<GameState> _session;
		private static readonly byte[] _serializedSyncedInputArray = new byte[GameState.MAX_SHIPS * _SIZE_OF_INPUT];
		private static readonly byte[] _serializedLocalInput = new byte[_SIZE_OF_INPUT];
		private static readonly ShipInput[] _syncedInputArray = new ShipInput[GameState.MAX_SHIPS];

		private static Func<ShipInput> _readInputsFunc;

		[Flags]
		public enum ShipInput : byte {
			None = 0,
			Thrust = 1 << 0,
			Brake = 1 << 1,
			CounterClockwise = 1 << 2,
			Clockwise = 1 << 3,
			Fire = 1 << 4,
			Bomb = 1 << 5
		}

		public static void Init(
			ushort localPort,
			int numPlayers,
			Player[] players,
			int numSpectators,
			Func<ShipInput> readInputsFunc,
			float screenBoundsXMin,
			float screenBoundsXMax,
			float screenBoundsYMin,
			float screenBoundsYMax
		) {
			// Initialize the game state
			_readInputsFunc = readInputsFunc;
			if (_readInputsFunc == null) {
				// ER TODO add LogError api to LogUtil and use here. Keeps unity out of game sim to do it this way.
				LogUtil.Log($"{nameof(_readInputsFunc)} is not set.");
			}

			_gameState = new GameState(
				numPlayers,
				screenBoundsXMin,
				screenBoundsXMax,
				screenBoundsYMin,
				screenBoundsYMax
			);
			
			_nonGameState = new NonGameState(numPlayers);
			
#if SYNC_TEST
			result = GGPOMain.ggpo_start_synctest(ref ggpo, ref cb, "vectorwar", num_players, sizeof(int), 1);
#else
			Session<GameState>.StartSession(
				out _session,
				OnBeginGame,
				OnSaveGameState,
				OnLoadGameState,
				OnLogGameState,
				OnFreeBuffer,
				OnAdvanceFrame,
				OnEvent,
				logTextEvent,
				numPlayers,
				_SIZE_OF_INPUT,
				localPort
			);
#endif

			// automatically disconnect clients after 3000 ms and start our count-down timer
			// for disconnects after 1000 ms. To completely disable disconnects, simply use
			// a value of 0 for ggpo_set_disconnect_timeout.
			_session.SetDisconnectTimeout(0);
			_session.SetDisconnectNotifyStart(1000);

			int i;
			for (i = 0; i < numPlayers + numSpectators; i++) {
				_session.AddPlayer(players[i], out PlayerHandle handle);
				_nonGameState.players[i].handle = handle;
				_nonGameState.players[i].type = players[i].type;
				if (players[i].type == PlayerType.Local) {
					_nonGameState.players[i].connectProgress = 100;
					_nonGameState.localPlayerHandle = handle;
					_nonGameState.SetConnectState(handle, PlayerConnectState.Connecting);
					_session.SetFrameDelay(handle, _FRAME_DELAY);
				} else {
					_nonGameState.players[i].connectProgress = 0;
				}
			}

			PerfMon<GameState>.ggpoutil_perfmon_init();
			GameRenderer.instance.SetStatusText("Connecting to peers.");
		}

		// Create a new spectator session
		public static void InitSpectator(
			ushort localPort,
			int numPlayers,
			IPEndPoint hostEndPoint,
			float screenBoundsXMin,
			float screenBoundsXMax,
			float screenBoundsYMin,
			float screenBoundsYMax
		) {
			// Initialize the game state
			_gameState = new GameState(
				numPlayers,
				screenBoundsXMin,
				screenBoundsXMax,
				screenBoundsYMin,
				screenBoundsYMax
			);
			
			_nonGameState = new NonGameState(numPlayers);

			Session<GameState>.StartSpectating(
				out Session<GameState> _,
				OnBeginGame,
				OnSaveGameState,
				OnLoadGameState,
				OnLogGameState,
				OnFreeBuffer,
				OnAdvanceFrame,
				OnEvent,
				logTextEvent,
				numPlayers,
				sizeof(int),
				localPort,
				hostEndPoint
			);

			PerfMon<GameState>.ggpoutil_perfmon_init();

			GameRenderer.instance.SetStatusText("Starting new spectator session");
		}

		// Disconnects a player from this session.
		private static void DisconnectPlayer(int player) {
			if (player >= _nonGameState.numPlayers) { return; }

			ErrorCode result = _session.DisconnectPlayer(_nonGameState.players[player].handle);

			string logMsg = result.IsSuccess()
				? $"Disconnected player {player}.{Environment.NewLine}"
				: $"Error while disconnecting player (err:{result}).{Environment.NewLine}";

			GameRenderer.instance.SetStatusText(logMsg);
		}

		// Draws the current frame without modifying the game state.
		private static void DrawCurrentFrame() { GameRenderer.instance.Draw(_gameState, _nonGameState); }

		// Advances the game state by exactly 1 frame using the inputs specified for player 1 and player 2.
		private static void AdvanceFrame(ShipInput[] inputs, int disconnectFlags) {
			_gameState.Update(inputs, disconnectFlags);

			// update the checksums to display in the top of the window.  this
			// helps to detect desyncs.
			_nonGameState.now.frameNumber = _gameState.frameNumber;
			// ER TODO Fletcher32Checksum(_gameState.Serialize());
			_nonGameState.now.checksum = _gameState.frameNumber;
			if (_gameState.frameNumber % 90 == 0) {
				_nonGameState.periodic = _nonGameState.now;
			}

			// Notify ggpo that we've moved forward exactly 1 frame.
			_session.AdvanceFrame();

			// Update the performance monitor display.
			PlayerHandle[] handles = new PlayerHandle[_MAX_PLAYERS];
			int count = 0;
			for (int i = 0; i < _nonGameState.numPlayers; i++) {
				if (_nonGameState.players[i].type == PlayerType.Remote) {
					handles[count++] = _nonGameState.players[i].handle;
				}
			}

			PerfMon<GameState>.ggpoutil_perfmon_update(ref _session, handles, count);
		}

		// Read the inputs for player 1 from the keyboard.  We never have to worry about player 2.
		// GGPO will handle remapping his inputs transparently.
		// Run a single frame of the game.
		public static void RunFrame() {
			ErrorCode result = ErrorCode.Success;

			// ER TODO does this condition filter out spectators so as to not bother reading+sending their inputs?
			if (_nonGameState.localPlayerHandle.handleValue != PlayerHandle.INVALID_HANDLE) {
#if SYNC_TEST
				input = rand(); // test: use random inputs to demonstrate sync testing
#endif
				// ER TODO generalize for varying sizes
				_serializedLocalInput[0] = _readInputsFunc != null
					? (byte) _readInputsFunc()
					: byte.MinValue;

				// XXX LOH size should 4 bytes? check inside, erroneously using serializedInputLength?
				result = _session.AddLocalInput(
					_nonGameState.localPlayerHandle,
					_serializedLocalInput,
					sizeof(ShipInput)
				); // NOTE hardcoding input type
			}

			// synchronize these inputs with ggpo.  If we have enough input to proceed
			// ggpo will modify the input list with the correct inputs to use and
			// return 1.
			if (result.IsSuccess()) {
				int disconnectFlags = 0;
				result = _session.SynchronizeInput(
					_serializedSyncedInputArray,
					_serializedSyncedInputArray.Length,
					ref disconnectFlags
				);
				
				if (result.IsSuccess()) {
					// inputs[0] and inputs[1] contain the inputs for p1 and p2.  Advance
					// the game by 1 frame using those inputs.
					AdvanceFrame(DeserializeShipInputs(_serializedSyncedInputArray), disconnectFlags);
				}
			}

			DrawCurrentFrame();
		}

		// Spend our idle time in ggpo so it can use whatever time we have left over for its internal bookkeeping.
		public static void Idle(int time) { _session.Idle(time); }

		public static void Exit() {
			_gameState = null;
			_nonGameState = null;

			_session?.CloseSession();
			_session = null;

			GameRenderer.instance = null;
		}

		// Simple checksum function stolen from wikipedia: http://en.wikipedia.org/wiki/Fletcher%27s_checksum
		public static int Fletcher32Checksum(byte[] data) { // TODO fix
			short[] newData = new short[data.Length / 2];
			for (int i = 0; i < data.Length; i++) {
				Buffer.SetByte(newData, i, data[i]);
			}

			return Fletcher32Checksum(newData);
		}

		private static int Fletcher32Checksum(short[] data) {
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
			return (sum2 << 16) | sum1;
		}

		// The begin game callback.  We don't need to do anything special here, so just return true.
		private static bool OnBeginGame() { return true; }

		/*
		* Notification from GGPO that something has happened.  Update the status
		* text at the bottom of the screen to notify the user.
		*/
		// TODO refactor to C# events
		private static bool OnEvent(EventData info) {
			switch (info.code) {
				case EventCode.ConnectedToPeer:
					_nonGameState.SetConnectState(info.connected.player, PlayerConnectState.Synchronizing);
					GameRenderer.instance.SetStatusText($"Connected to player {info.connected.player.handleValue}");
					break;
				case EventCode.SynchronizingWithPeer:
					int progress = 100 * info.synchronizing.count / info.synchronizing.total;
					_nonGameState.UpdateConnectProgress(info.synchronizing.player, progress);
					break;
				case EventCode.SynchronizedWithPeer:
					_nonGameState.UpdateConnectProgress(info.synchronized.player, 100);
					break;
				case EventCode.Running:
					_nonGameState.SetConnectState(PlayerConnectState.Running);
					GameRenderer.instance.SetStatusText("");
					break;
				case EventCode.ConnectionInterrupted:
					_nonGameState.SetDisconnectTimeout(
						info.connectionInterrupted.player,
						Platform.GetCurrentTimeMS(),
						info.connectionInterrupted.disconnectTimeout
					);
					break;
				case EventCode.ConnectionResumed:
					_nonGameState.SetConnectState(info.connectionResumed.player, PlayerConnectState.Running);
					break;
				case EventCode.DisconnectedFromPeer:
					_nonGameState.SetConnectState(info.disconnected.player, PlayerConnectState.Disconnected);
					break;
				case EventCode.TimeSync:
					Thread.Sleep(1000 * info.timeSync.framesAhead / _FRAMES_PER_SECOND);
					break;
			}

			return true;
		}

		// Notification from GGPO that we should step forward exactly 1 frame during a rollback.
		private static bool OnAdvanceFrame(int flags) {
			for (int i = 0; i < _serializedSyncedInputArray.Length; i++) { _serializedSyncedInputArray[i] = 0; }

			// Make sure we fetch new inputs from GGPO and use those to update
			// the game state instead of reading from the keyboard.
			// ER NOTE SynchronizeInput expects an array of primitives, TODO allow handling of enum arrays (i.e. flags)
			int disconnectFlags = 0;
			_session.SynchronizeInput(_serializedSyncedInputArray, _serializedSyncedInputArray.Length, ref disconnectFlags);
			AdvanceFrame(DeserializeShipInputs(_serializedSyncedInputArray), disconnectFlags);
			return true;
		}

		private static ShipInput[] DeserializeShipInputs(byte[] serializedInputs) {
			for (int i = 0; i < _syncedInputArray.Length; i++) { _syncedInputArray[i] = 0; }

			for (int i = 0; i < serializedInputs.Length; i++) {
				// ER TODO generalize for ship input size in bytes, this is done only because synchronize inputs doesn't
				// handle non primitive arrays, see VectorWar::OnAdvanceFrame
				_syncedInputArray[i] = (ShipInput) serializedInputs[i];
			}

			return _syncedInputArray;
		}

		// Makes our current state match the state passed in by GGPO.
		private static bool OnLoadGameState(GameState gameState) {
			_gameState = gameState;
			return true;
		}

		// Save the current state to a buffer and return it to GGPO via the buffer and len parameters.
		private static bool OnSaveGameState(out GameState gameState, out int checksum, int frame) {
			gameState = (GameState) _gameState.Clone();
			//_gameState.Serialize(_gameState.Size(), out byte[] buffer); // TODO probably a better way to get the checksum.
			checksum = _gameState.frameNumber; // ER TODO optional checksum: Fletcher32Checksum(buffer);
			return true;
		}

		// Log the game state.  Used by the sync test debugging tool.
		private static bool OnLogGameState(string filename, GameState gameState) {
			FileStream fp = File.Open(filename, FileMode.OpenOrCreate, FileAccess.Write);

			StringBuilder stringBuilder = new StringBuilder($"GameState object.{Environment.NewLine}");
			stringBuilder.Append(
				$"  bounds: {gameState.bounds.xMin},{gameState.bounds.yMin} x {gameState.bounds.xMax},{gameState.bounds.yMax}.{Environment.NewLine}"
			);
			stringBuilder.Append($"  num_ships: {gameState.numShips}.{Environment.NewLine}");

			for (int i = 0; i < gameState.numShips; i++) {
				Ship ship = gameState.ships[i];

				stringBuilder.Append(
					$"  ship {i} position:  {ship.position.x:F4}, {ship.position.y:F4}{Environment.NewLine}"
				);
				stringBuilder.Append(
					$"  ship {i} velocity:  {ship.velocity.x:F4}, {ship.velocity.y:F4}{Environment.NewLine}"
				);
				stringBuilder.Append($"  ship {i} radius:    {ship.radius}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} headingDeg:   {ship.headingDeg}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} health:    {ship.health}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} speed:     {ship.speed}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} cooldown:  {ship.cooldown}.{Environment.NewLine}");
				stringBuilder.Append($"  ship {i} score:     {ship.score}.{Environment.NewLine}");

				for (int j = 0; j < Ship.MAX_BULLETS; j++) {
					Bullet bullet = ship.bullets[j];
					stringBuilder.Append(
						$"  ship {i} bullet {j}: {bullet.position.x:F2} {bullet.position.y:F2} -> {bullet.velocity.x} {bullet.velocity.y}.{Environment.NewLine}"
					);
				}
			}

			byte[] messageArr = Encoding.Default.GetBytes(stringBuilder.ToString());
			fp.Write(messageArr, 0, messageArr.Length);
			fp.Close();
			return true;
		}

		// Free a save state buffer previously returned in vw_save_game_state_callback.
		private static void OnFreeBuffer(object gameState) {
			//free(buffer); // NOTE nothing for managed lang, though could prove useful nonetheless.
		}
	}
}