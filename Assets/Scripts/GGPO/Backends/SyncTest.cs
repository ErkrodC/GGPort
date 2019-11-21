using System;
using System.Diagnostics;
using System.IO;

namespace GGPort {
	public class SyncTestBackend : GGPOSession {
		protected GGPOSessionCallbacks _callbacks;
		protected Sync _sync;
		protected int _num_players;
		protected int _check_distance;
		protected int _last_verified;
		protected bool _rollingback;
		protected bool _running;
		protected FileStream _logfp;
		protected string _game;
		
		protected GameInput                  _current_input;
		protected GameInput                  _last_input;
		protected RingBuffer<SavedInfo>  _saved_frames = new RingBuffer<SavedInfo>(32);

		public SyncTestBackend(ref GGPOSessionCallbacks cb, string gamename, int frames, int num_players) {
			_sync = null;
			_callbacks = cb;
			_num_players = num_players;
			_check_distance = frames;
			_last_verified = 0;
			_rollingback = false;
			_running = false;
			_logfp = null;
			_current_input.erase();
			_game = gamename;

			/*
			 * Initialize the synchronziation layer
			 */
			Sync.Config config = new Sync.Config(_callbacks, Sync.MAX_PREDICTION_FRAMES);
			_sync.Init(config);

			/*
			 * Preload the ROM
			 */
			_callbacks.begin_game(gamename);
		}

		public virtual GGPOErrorCode DoPoll(int timeout) {
			if (!_running) {
				GGPOEvent info = new GGPOEvent(GGPOEventCode.GGPO_EVENTCODE_RUNNING);
				
				_callbacks.on_event(info);
				_running = true;
			}
			return GGPOErrorCode.GGPO_OK;
		}

		public override GGPOErrorCode AddPlayer(ref GGPOPlayer player, ref GGPOPlayerHandle handle) {
			if (player.player_num < 1 || player.player_num > _num_players) {
				return GGPOErrorCode.GGPO_ERRORCODE_PLAYER_OUT_OF_RANGE;
			}
			handle = new GGPOPlayerHandle(player.player_num - 1);
			return GGPOErrorCode.GGPO_OK;
		}

		public override unsafe GGPOErrorCode AddLocalInput(GGPOPlayerHandle player, object[] values, int size) {
			if (!_running) {
				return GGPOErrorCode.GGPO_ERRORCODE_NOT_SYNCHRONIZED;
			}

			int index = player.handleValue;
			
			
			for (int i = 0; i < size; i++) {
				_current_input.bits[index * size + i] |= Buffer.GetByte(values, i);
			}
			return GGPOErrorCode.GGPO_OK;
		}

		public override unsafe GGPOErrorCode SyncInput(ref object[] values, int size, ref int? disconnect_flags) {
			BeginLog(false);
			
			if (_rollingback) {
				_last_input = _saved_frames.front().input;
			} else {
				if (_sync.GetFrameCount() == 0) {
					_sync.SaveCurrentFrame();
				}
				_last_input = _current_input;
			}

			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, _last_input.bits[i]);
			}
			
			if (disconnect_flags != null) {
				disconnect_flags = 0;
			}
			
			return GGPOErrorCode.GGPO_OK;
		}

		public virtual GGPOErrorCode IncrementFrame() {
			_sync.IncrementFrame();
			_current_input.erase();
   
			LogUtil.Log("End of frame({0})...\n", _sync.GetFrameCount());
			EndLog();

			if (_rollingback) {
				return GGPOErrorCode.GGPO_OK;
			}

			int frame = _sync.GetFrameCount();
			// Hold onto the current frame in our queue of saved states.  We'll need
			// the checksum later to verify that our replay of the same frame got the
			// same results.
			Sync.SavedFrame lastSavedFrame = _sync.GetLastSavedFrame();
			byte[] lastFrameBufDeepCopy = new byte[lastSavedFrame.cbuf];
			for (int i = 0; i < lastSavedFrame.cbuf; i++) {
				lastFrameBufDeepCopy[i] = lastSavedFrame.buf[i];
			}
			
			SavedInfo info = new SavedInfo(
				frame,
				lastSavedFrame.checksum,
				lastFrameBufDeepCopy,
				lastSavedFrame.cbuf,
				_last_input
			);
			_saved_frames.push(info);

			if (frame - _last_verified == _check_distance) {
				// We've gone far enough ahead and should now start replaying frames.
				// Load the last verified frame and set the rollback flag to true.
				_sync.LoadFrame(_last_verified);

				_rollingback = true;
				while(!_saved_frames.empty()) {
					_callbacks.advance_frame(0);

					// Verify that the checksumn of this frame is the same as the one in our
					// list.
					info = _saved_frames.front();
					_saved_frames.pop();

					if (info.frame != _sync.GetFrameCount()) {
						RaiseSyncError("Frame number %d does not match saved frame number %d", info.frame, frame);
					}
					int checksum = _sync.GetLastSavedFrame().checksum;
					if (info.checksum != checksum) {
						LogSaveStates(info);
						RaiseSyncError("Checksum for frame %d does not match saved (%d != %d)", frame, checksum, info.checksum);
					}
					
					Console.WriteLine($"Checksum {checksum:00000000} for frame {info.frame} matches.\n");
					info.FreeBuffer();
				}
				_last_verified = frame;
				_rollingback = false;
			}

			return GGPOErrorCode.GGPO_OK;
		}

		public virtual GGPOErrorCode Logv(string fmt, params object[] args) {
			if (_logfp != null) {
				char[] msg = string.Format(fmt, args).ToCharArray();
				byte[] buf = new byte[msg.Length];
				Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);

				_logfp.Write(buf, 0, msg.Length);
			}
			
			return GGPOErrorCode.GGPO_OK;
		}

		protected struct SavedInfo {
			public readonly int frame;
			public readonly int checksum;
			public byte[] buf { get; private set; }
			public readonly int cbuf;
			public readonly GameInput input;

			public SavedInfo(int frame, int checksum, byte[] buf, int cbuf, GameInput input) {
				this.frame = frame;
				this.checksum = checksum;
				this.buf = buf;
				this.cbuf = cbuf;
				this.input = input;
			}

			public void FreeBuffer() {
				buf = null;
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
				$"synclogs\\{(saving ? "state" : "log")}-{_sync.GetFrameCount():0000}-{(_rollingback ? "replay" : "original")}.log";

			_logfp = File.Open(filename, FileMode.OpenOrCreate);
		}

		protected void EndLog() {
			if (_logfp != null) {
				char[] msg = "Closing log file.\n".ToCharArray();
				byte[] buf = new byte[msg.Length];
				Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);

				_logfp.Write(buf, 0, msg.Length);
				_logfp.Close();
				_logfp = null;
			}
		}

		protected void LogSaveStates(SavedInfo info) {
			string filename = $"synclogs\\state-{_sync.GetFrameCount():0000}-original.log";
			_callbacks.log_game_state(filename, info.buf, info.cbuf);

			filename = $"synclogs\\state-{_sync.GetFrameCount():0000}-replay.log";
			_callbacks.log_game_state(filename, _sync.GetLastSavedFrame().buf, _sync.GetLastSavedFrame().cbuf);
		}
	};
}