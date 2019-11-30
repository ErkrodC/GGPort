﻿using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace GGPort {
	public class SyncTestBackend : GGPOSession {
		protected SessionCallbacks _callbacks;
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

		public SyncTestBackend(ref SessionCallbacks cb, string gamename, int frames, int num_players) {
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
			Sync.Config config = new Sync.Config(_callbacks, Sync.kMaxPredictionFrames);
			_sync.Init(ref config);

			/*
			 * Preload the ROM
			 */
			_callbacks.BeginGame(gamename);
		}

		public override ErrorCode DoPoll(int timeout) {
			if (!_running) {
				GGPOEvent info = new GGPOEvent(GGPOEventCode.Running);
				
				_callbacks.OnEvent(ref info);
				_running = true;
			}
			return ErrorCode.Success;
		}

		public override ErrorCode AddPlayer(ref GGPOPlayer player, out GGPOPlayerHandle handle) {
			if (player.PlayerNum < 1 || player.PlayerNum > _num_players) {
				handle = new GGPOPlayerHandle(-1);
				return ErrorCode.PlayerOutOfRange;
			}
			
			handle = new GGPOPlayerHandle(player.PlayerNum - 1);
			return ErrorCode.Success;
		}

		public override unsafe ErrorCode AddLocalInput(GGPOPlayerHandle player, byte[] value, int size) {
			if (!_running) {
				return ErrorCode.NotSynchronized;
			}

			int index = player.HandleValue;
			
			byte[] valByteArr = new byte[size];
			BinaryFormatter bf = new BinaryFormatter();
			using (MemoryStream ms = new MemoryStream(valByteArr)) {
				bf.Serialize(ms, value);
			} // TODO refactor/optimize
			
			for (int i = 0; i < size; i++) {
				_current_input.bits[index * size + i] |= valByteArr[i];
			}
			return ErrorCode.Success;
		}

		public override unsafe ErrorCode SyncInput(ref Array values, int size, ref int? disconnect_flags) {
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
			
			return ErrorCode.Success;
		}

		public override ErrorCode IncrementFrame() {
			_sync.IncrementFrame();
			_current_input.erase();
   
			LogUtil.Log($"End of frame({_sync.GetFrameCount()})...{Environment.NewLine}");
			EndLog();

			if (_rollingback) {
				return ErrorCode.Success;
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
					_callbacks.AdvanceFrame(0);

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
					
					Console.WriteLine($"Checksum {checksum:00000000} for frame {info.frame} matches.{Environment.NewLine}");
					info.FreeBuffer();
				}
				_last_verified = frame;
				_rollingback = false;
			}

			return ErrorCode.Success;
		}

		public virtual ErrorCode Logv(string fmt, params object[] args) {
			if (_logfp != null) {
				char[] msg = string.Format(fmt, args).ToCharArray();
				byte[] buf = new byte[msg.Length];
				Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);

				_logfp.Write(buf, 0, msg.Length);
			}
			
			return ErrorCode.Success;
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
				string msg = $"Closing log file.{Environment.NewLine}";
				byte[] buffer = Encoding.UTF8.GetBytes(msg);

				_logfp.Write(buffer, 0, buffer.Length);
				_logfp.Close();
				_logfp = null;
			}
		}

		protected void LogSaveStates(SavedInfo info) {
			string filename = $"synclogs\\state-{_sync.GetFrameCount():0000}-original.log";
			_callbacks.LogGameState(filename, info.buf, info.cbuf);

			filename = $"synclogs\\state-{_sync.GetFrameCount():0000}-replay.log";
			_callbacks.LogGameState(filename, _sync.GetLastSavedFrame().buf, _sync.GetLastSavedFrame().cbuf);
		}
	};
}