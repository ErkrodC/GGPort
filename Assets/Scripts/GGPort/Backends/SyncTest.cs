using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace GGPort {
	public class SyncTestBackend<TGameState> : Session<TGameState> {
		private readonly Sync<TGameState> m_sync;
		private readonly int m_numPlayers;
		private readonly int m_checkDistance;
		private int m_lastVerified;
		private bool m_isRollingBack;
		private bool m_isRunning;
		private FileStream m_logFile;
		private string m_gameName;

		private GameInput m_currentInput;
		private GameInput m_lastInput;
		private readonly CircularQueue<SavedInfo> m_savedFrames;

		public SyncTestBackend(
			BeginGameDelegate beginGameCallback,
			SaveGameStateDelegate saveGameStateCallback,
			LoadGameStateDelegate loadGameStateCallback,
			LogGameStateDelegate logGameStateCallback,
			FreeBufferDelegate freeBufferCallback,
			AdvanceFrameDelegate advanceFrameCallback,
			OnEventDelegate onEventCallback,
			LogTextDelegate logTextCallback,
			string gameName,
			int frames,
			int numPlayers
		) : base(
			beginGameCallback,
			saveGameStateCallback,
			loadGameStateCallback,
			logGameStateCallback,
			freeBufferCallback,
			advanceFrameCallback,
			onEventCallback,
			logTextCallback
		) {
			m_savedFrames = new CircularQueue<SavedInfo>(32);
			
			m_sync = null;
			m_numPlayers = numPlayers;
			m_checkDistance = frames;
			m_lastVerified = 0;
			m_isRollingBack = false;
			m_isRunning = false;
			m_logFile = null;
			m_currentInput.Erase();
			m_gameName = gameName;

			// Initialize the synchronization layer
			m_sync = new Sync<TGameState>(new PeerMessage.ConnectStatus[PeerMessage.MAX_PLAYERS], Sync<TGameState>.MAX_PREDICTION_FRAMES);
			m_sync.advanceFrameEvent += advanceFrameEvent;
			m_sync.saveGameStateEvent += saveGameStateEvent;
			m_sync.loadGameStateEvent += loadGameStateEvent;
			m_sync.freeBufferEvent += freeBufferEvent;

			// Preload the ROM
			beginGameEvent?.Invoke(gameName);
		}

		public override ErrorCode Idle(int timeout) {
			if (m_isRunning) { return ErrorCode.Success; }

			Event info = new Event(EventCode.Running);
				
			onEventEvent?.Invoke(info);
			m_isRunning = true;
			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(Player player, out PlayerHandle handle) {
			if (player.PlayerNum < 1 || player.PlayerNum > m_numPlayers) {
				handle = new PlayerHandle(-1);
				return ErrorCode.PlayerOutOfRange;
			}
			
			handle = new PlayerHandle(player.PlayerNum - 1);
			return ErrorCode.Success;
		}

		public override unsafe ErrorCode AddLocalInput(PlayerHandle player, byte[] value, int size) {
			if (!m_isRunning) {
				return ErrorCode.NotSynchronized;
			}

			int index = player.HandleValue;
			
			byte[] valByteArr = new byte[size];
			BinaryFormatter bf = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(valByteArr)) {
				bf.Serialize(ms, value);
			} // TODO refactor/optimize
			
			for (int i = 0; i < size; i++) {
				m_currentInput.bits[index * size + i] |= valByteArr[i];
			}
			return ErrorCode.Success;
		}

		// TODO verify removal of ref on values Array doesn't cause issues (though it is ref type anyways so shouldn't be). Used similarly in the other backends, no need to triple check
		public override unsafe ErrorCode SynchronizeInput(Array values, int size, ref int disconnectFlags) {
			BeginLog(false);
			
			if (m_isRollingBack) {
				m_lastInput = m_savedFrames.Peek().Input;
			} else {
				if (m_sync.GetFrameCount() == 0) {
					m_sync.SaveCurrentFrame();
				}
				m_lastInput = m_currentInput;
			}

			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, m_lastInput.bits[i]);
			}
			
			disconnectFlags = 0;
			
			return ErrorCode.Success;
		}

		public override ErrorCode AdvanceFrame() {
			m_sync.IncrementFrame();
			m_currentInput.Erase();
   
			LogUtil.Log($"End of frame({m_sync.GetFrameCount()})...{Environment.NewLine}");
			EndLog();

			if (m_isRollingBack) {
				return ErrorCode.Success;
			}

			int frame = m_sync.GetFrameCount();
			// Hold onto the current frame in our queue of saved states.  We'll need
			// the checksum later to verify that our replay of the same frame got the
			// same results.
			Sync<TGameState>.SavedFrame lastSavedFrame = m_sync.GetLastSavedFrame();
			
			SavedInfo info = new SavedInfo(
				frame,
				lastSavedFrame.checksum,
				lastSavedFrame.gameState,
				m_lastInput
			);
			
			m_savedFrames.Push(info);

			if (frame - m_lastVerified == m_checkDistance) {
				// We've gone far enough ahead and should now start replaying frames.
				// Load the last verified frame and set the rollback flag to true.
				m_sync.LoadFrame(m_lastVerified);

				m_isRollingBack = true;
				while(m_savedFrames.Count > 0) {
					advanceFrameEvent?.Invoke(0);

					// Verify that the checksumn of this frame is the same as the one in our
					// list.
					info = m_savedFrames.Pop();

					if (info.Frame != m_sync.GetFrameCount()) {
						RaiseSyncError("Frame number %d does not match saved frame number %d", info.Frame, frame);
					}
					int checksum = m_sync.GetLastSavedFrame().checksum;
					if (info.Checksum != checksum) {
						LogSaveStates(info);
						RaiseSyncError("Checksum for frame %d does not match saved (%d != %d)", frame, checksum, info.Checksum);
					}
					
					Console.WriteLine($"Checksum {checksum:00000000} for frame {info.Frame} matches.{Environment.NewLine}");
					info.FreeBuffer();
				}
				m_lastVerified = frame;
				m_isRollingBack = false;
			}

			return ErrorCode.Success;
		}

		public virtual ErrorCode Logv(string fmt, params object[] args) {
			if (m_logFile != null) {
				char[] msg = string.Format(fmt, args).ToCharArray();
				byte[] buf = new byte[msg.Length];
				Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);

				m_logFile.Write(buf, 0, msg.Length);
			}
			
			return ErrorCode.Success;
		}

		protected struct SavedInfo {
			public readonly int Frame;
			public readonly int Checksum;
			public TGameState GameState { get; private set; }
			public readonly GameInput Input;

			public SavedInfo(int frame, int checksum, TGameState gameState, GameInput input) {
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
				$"synclogs\\{(saving ? "state" : "log")}-{m_sync.GetFrameCount():0000}-{(m_isRollingBack ? "replay" : "original")}.log";

			m_logFile = File.Open(filename, FileMode.OpenOrCreate);
		}

		protected void EndLog() {
			if (m_logFile != null) {
				string msg = $"Closing log file.{Environment.NewLine}";
				byte[] buffer = Encoding.UTF8.GetBytes(msg);

				m_logFile.Write(buffer, 0, buffer.Length);
				m_logFile.Close();
				m_logFile = null;
			}
		}

		protected void LogSaveStates(SavedInfo info) {
			string filename = $"synclogs\\state-{m_sync.GetFrameCount():0000}-original.log";
			logGameStateEvent?.Invoke(filename, info.GameState);

			filename = $"synclogs\\state-{m_sync.GetFrameCount():0000}-replay.log";
			logGameStateEvent?.Invoke(filename, m_sync.GetLastSavedFrame().gameState);
		}
	};
}