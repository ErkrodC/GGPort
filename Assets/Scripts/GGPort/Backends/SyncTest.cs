using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace GGPort {
	public class SyncTestBackend : Session {
		private readonly SessionCallbacks callbacks;
		private readonly Sync sync;
		private readonly int numPlayers;
		private readonly int checkDistance;
		private int lastVerified;
		private bool isRollingBack;
		private bool isRunning;
		private FileStream logFile;
		private string gameName;

		private GameInput currentInput;
		private GameInput lastInput;
		private readonly CircularQueue<SavedInfo> savedFrames;

		public SyncTestBackend(ref SessionCallbacks cb, string gameName, int frames, int numPlayers) {
			savedFrames = new CircularQueue<SavedInfo>(32);
			
			sync = null;
			callbacks = cb;
			this.numPlayers = numPlayers;
			checkDistance = frames;
			lastVerified = 0;
			isRollingBack = false;
			isRunning = false;
			logFile = null;
			currentInput.Erase();
			this.gameName = gameName;

			/*
			 * Initialize the synchronziation layer
			 */
			Sync.Config config = new Sync.Config(callbacks, Sync.kMaxPredictionFrames);
			sync.Init(config);

			/*
			 * Preload the ROM
			 */
			callbacks.BeginGame(gameName);
		}

		public override ErrorCode Idle(int timeout) {
			if (!isRunning) {
				Event info = new Event(EventCode.Running);
				
				callbacks.OnEvent(info);
				isRunning = true;
			}
			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(Player player, out PlayerHandle handle) {
			if (player.PlayerNum < 1 || player.PlayerNum > numPlayers) {
				handle = new PlayerHandle(-1);
				return ErrorCode.PlayerOutOfRange;
			}
			
			handle = new PlayerHandle(player.PlayerNum - 1);
			return ErrorCode.Success;
		}

		public override unsafe ErrorCode AddLocalInput(PlayerHandle player, byte[] value, int size) {
			if (!isRunning) {
				return ErrorCode.NotSynchronized;
			}

			int index = player.HandleValue;
			
			byte[] valByteArr = new byte[size];
			BinaryFormatter bf = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(valByteArr)) {
				bf.Serialize(ms, value);
			} // TODO refactor/optimize
			
			for (int i = 0; i < size; i++) {
				currentInput.Bits[index * size + i] |= valByteArr[i];
			}
			return ErrorCode.Success;
		}

		// TODO verify removal of ref on values Array doesn't cause issues (though it is ref type anyways so shouldn't be). Used similarly in the other backends, no need to triple check
		public override unsafe ErrorCode SynchronizeInput(Array values, int size, ref int disconnectFlags) {
			BeginLog(false);
			
			if (isRollingBack) {
				lastInput = savedFrames.Peek().Input;
			} else {
				if (sync.GetFrameCount() == 0) {
					sync.SaveCurrentFrame();
				}
				lastInput = currentInput;
			}

			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, lastInput.Bits[i]);
			}
			
			disconnectFlags = 0;
			
			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			sync.IncrementFrame();
			currentInput.Erase();
   
			LogUtil.Log($"End of frame({sync.GetFrameCount()})...{Environment.NewLine}");
			EndLog();

			if (isRollingBack) {
				return ErrorCode.Success;
			}

			int frame = sync.GetFrameCount();
			// Hold onto the current frame in our queue of saved states.  We'll need
			// the checksum later to verify that our replay of the same frame got the
			// same results.
			Sync.SavedFrame lastSavedFrame = sync.GetLastSavedFrame();
			
			SavedInfo info = new SavedInfo(
				frame,
				lastSavedFrame.Checksum,
				lastSavedFrame.GameState,
				lastInput
			);
			
			savedFrames.Push(info);

			if (frame - lastVerified == checkDistance) {
				// We've gone far enough ahead and should now start replaying frames.
				// Load the last verified frame and set the rollback flag to true.
				sync.LoadFrame(lastVerified);

				isRollingBack = true;
				while(savedFrames.Count > 0) {
					callbacks.AdvanceFrame(0);

					// Verify that the checksumn of this frame is the same as the one in our
					// list.
					info = savedFrames.Pop();

					if (info.Frame != sync.GetFrameCount()) {
						RaiseSyncError("Frame number %d does not match saved frame number %d", info.Frame, frame);
					}
					int checksum = sync.GetLastSavedFrame().Checksum;
					if (info.Checksum != checksum) {
						LogSaveStates(info);
						RaiseSyncError("Checksum for frame %d does not match saved (%d != %d)", frame, checksum, info.Checksum);
					}
					
					Console.WriteLine($"Checksum {checksum:00000000} for frame {info.Frame} matches.{Environment.NewLine}");
					info.FreeBuffer();
				}
				lastVerified = frame;
				isRollingBack = false;
			}

			return ErrorCode.Success;
		}

		public virtual ErrorCode Logv(string fmt, params object[] args) {
			if (logFile != null) {
				char[] msg = string.Format(fmt, args).ToCharArray();
				byte[] buf = new byte[msg.Length];
				Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);

				logFile.Write(buf, 0, msg.Length);
			}
			
			return ErrorCode.Success;
		}

		protected struct SavedInfo {
			public readonly int Frame;
			public readonly int Checksum;
			public object GameState { get; private set; }
			public readonly GameInput Input;

			public SavedInfo(int frame, int checksum, object gameState, GameInput input) {
				Frame = frame;
				Checksum = checksum;
				GameState = gameState;
				Input = input;
			}

			// TODO remove
			public void FreeBuffer() {
				
			}
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
				$"synclogs\\{(saving ? "state" : "log")}-{sync.GetFrameCount():0000}-{(isRollingBack ? "replay" : "original")}.log";

			logFile = File.Open(filename, FileMode.OpenOrCreate);
		}

		protected void EndLog() {
			if (logFile != null) {
				string msg = $"Closing log file.{Environment.NewLine}";
				byte[] buffer = Encoding.UTF8.GetBytes(msg);

				logFile.Write(buffer, 0, buffer.Length);
				logFile.Close();
				logFile = null;
			}
		}

		protected void LogSaveStates(SavedInfo info) {
			string filename = $"synclogs\\state-{sync.GetFrameCount():0000}-original.log";
			callbacks.LogGameState(filename, info.GameState);

			filename = $"synclogs\\state-{sync.GetFrameCount():0000}-replay.log";
			callbacks.LogGameState(filename, sync.GetLastSavedFrame().GameState);
		}
	};
}