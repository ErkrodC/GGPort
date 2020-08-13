using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace GGPort {
	public class SyncTestBackend<TGameState> : Session<TGameState> {
		private readonly Sync<TGameState> _sync;
		private readonly int _numPlayers;
		private readonly int _checkDistance;
		private int _lastVerified;
		private bool _isRollingBack;
		private bool _isRunning;
		private FileStream _logFile;

		private GameInput _currentInput;
		private GameInput _lastInput;
		private readonly CircularQueue<SavedInfo> _savedFrames;

		public SyncTestBackend(
			BeginGameDelegate beginGameCallback,
			SaveGameStateDelegate<TGameState> saveGameStateCallback,
			LoadGameStateDelegate<TGameState> loadGameStateCallback,
			LogGameStateDelegate<TGameState> logGameStateCallback,
			FreeBufferDelegate<TGameState> freeBufferCallback,
			AdvanceFrameDelegate advanceFrameCallback,
			OnEventDelegate onEventCallback,
			LogTextDelegate logTextCallback,
			int frames,
			int numPlayers
		)
			: base(
				beginGameCallback,
				saveGameStateCallback,
				loadGameStateCallback,
				logGameStateCallback,
				freeBufferCallback,
				advanceFrameCallback,
				onEventCallback,
				logTextCallback
			) {
			_savedFrames = new CircularQueue<SavedInfo>(32);

			_sync = null;
			_numPlayers = numPlayers;
			_checkDistance = frames;
			_lastVerified = 0;
			_isRollingBack = false;
			_isRunning = false;
			_logFile = null;
			_currentInput.Erase();

			// Initialize the synchronization layer
			_sync = new Sync<TGameState>(
				new PeerMessage.ConnectStatus[PeerMessage.MAX_PLAYERS],
				Sync.MAX_PREDICTION_FRAMES
			);
			_sync.advanceFrameEvent += advanceFrameEvent;
			_sync.saveGameStateEvent += saveGameStateEvent;
			_sync.loadGameStateEvent += loadGameStateEvent;
			_sync.freeBufferEvent += freeBufferEvent;

			// Preload the ROM
			beginGameEvent?.Invoke();
		}

		public override ErrorCode Idle(int timeout) {
			if (_isRunning) { return ErrorCode.Success; }

			EventData info = new EventData(EventCode.Running);

			onEventEvent?.Invoke(info);
			_isRunning = true;
			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(Player player, out PlayerHandle handle) {
			if (player.playerNum < 1 || player.playerNum > _numPlayers) {
				handle = new PlayerHandle(-1);
				return ErrorCode.PlayerOutOfRange;
			}

			handle = new PlayerHandle(player.playerNum - 1);
			return ErrorCode.Success;
		}

		public override unsafe ErrorCode AddLocalInput(PlayerHandle player, byte[] value, int size) {
			if (!_isRunning) {
				return ErrorCode.NotSynchronized;
			}

			int index = player.handleValue;

			byte[] valByteArr = new byte[size];
			BinaryFormatter bf = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(valByteArr)) {
				bf.Serialize(ms, value);
			} // TODO refactor/optimize

			for (int i = 0; i < size; i++) {
				_currentInput.bits[index * size + i] |= valByteArr[i];
			}

			return ErrorCode.Success;
		}

		// TODO verify removal of ref on values Array doesn't cause issues (though it is ref type anyways so shouldn't be). Used similarly in the other backends, no need to triple check
		public override unsafe ErrorCode SynchronizeInput(Array values, int size, ref int disconnectFlags) {
			BeginLog(false);

			if (_isRollingBack) {
				_lastInput = _savedFrames.Peek().input;
			} else {
				if (_sync.GetFrameCount() == 0) {
					_sync.SaveCurrentFrame();
				}

				_lastInput = _currentInput;
			}

			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, _lastInput.bits[i]);
			}

			disconnectFlags = 0;

			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			_sync.IncrementFrame();
			_currentInput.Erase();

			LogUtil.Log($"End of frame({_sync.GetFrameCount()})...{Environment.NewLine}");
			EndLog();

			if (_isRollingBack) {
				return ErrorCode.Success;
			}

			int frame = _sync.GetFrameCount();
			// Hold onto the current frame in our queue of saved states.  We'll need
			// the checksum later to verify that our replay of the same frame got the
			// same results.
			Sync<TGameState>.SavedFrame lastSavedFrame = _sync.GetLastSavedFrame();

			SavedInfo info = new SavedInfo(
				frame,
				lastSavedFrame.checksum,
				lastSavedFrame.gameState,
				_lastInput
			);

			_savedFrames.Push(info);

			if (frame - _lastVerified == _checkDistance) {
				// We've gone far enough ahead and should now start replaying frames.
				// Load the last verified frame and set the rollback flag to true.
				_sync.LoadFrame(_lastVerified);

				_isRollingBack = true;
				while (_savedFrames.count > 0) {
					advanceFrameEvent?.Invoke(0);

					// Verify that the checksumn of this frame is the same as the one in our
					// list.
					info = _savedFrames.Pop();

					if (info.frame != _sync.GetFrameCount()) {
						RaiseSyncError("Frame number %d does not match saved frame number %d", info.frame, frame);
					}

					int checksum = _sync.GetLastSavedFrame().checksum;
					if (info.checksum != checksum) {
						LogSaveStates(info);
						RaiseSyncError(
							"Checksum for frame %d does not match saved (%d != %d)",
							frame,
							checksum,
							info.checksum
						);
					}

					Console.WriteLine($"Checksum {checksum:00000000} for frame {info.frame} matches.{Environment.NewLine}");
					info.FreeBuffer();
				}

				_lastVerified = frame;
				_isRollingBack = false;
			}

			return ErrorCode.Success;
		}

		public virtual ErrorCode Logv(string fmt, params object[] args) {
			if (_logFile != null) {
				char[] msg = string.Format(fmt, args).ToCharArray();
				byte[] buf = new byte[msg.Length];
				Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);

				_logFile.Write(buf, 0, msg.Length);
			}

			return ErrorCode.Success;
		}

		protected struct SavedInfo {
			public readonly int frame;
			public readonly int checksum;
			public TGameState gameState { get; private set; }
			public readonly GameInput input;

			public SavedInfo(int frame, int checksum, TGameState gameState, GameInput input) {
				this.frame = frame;
				this.checksum = checksum;
				this.gameState = gameState;
				this.input = input;
			}

			// TODO remove
			public void FreeBuffer() { }
		};

		protected void RaiseSyncError(string fmt, params object[] args) {
			string msg = string.Format(fmt, args);

			Debugger.Log(0, string.Empty, msg);
			EndLog();
			Debugger.Break();
		}

		protected void BeginLog(bool saving) {
			EndLog();

			Directory.CreateDirectory("synclogs");
			string filename =
				$"synclogs\\{(saving ? "state" : "log")}-{_sync.GetFrameCount():0000}-{(_isRollingBack ? "replay" : "original")}.log";

			_logFile = File.Open(filename, FileMode.OpenOrCreate);
		}

		protected void EndLog() {
			if (_logFile != null) {
				string msg = $"Closing log file.{Environment.NewLine}";
				byte[] buffer = Encoding.UTF8.GetBytes(msg);

				_logFile.Write(buffer, 0, buffer.Length);
				_logFile.Close();
				_logFile = null;
			}
		}

		protected void LogSaveStates(SavedInfo info) {
			string filename = $"synclogs\\state-{_sync.GetFrameCount():0000}-original.log";
			logGameStateEvent?.Invoke(filename, info.gameState);

			filename = $"synclogs\\state-{_sync.GetFrameCount():0000}-replay.log";
			logGameStateEvent?.Invoke(filename, _sync.GetLastSavedFrame().gameState);
		}
	};
}