/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	public class Sync {
		public const int MAX_PREDICTION_FRAMES = 8;
		
		public struct Config {
			public readonly GGPOSessionCallbacks callbacks;
			public readonly int num_prediction_frames;
			public readonly int num_players;
			public readonly int input_size;

			public Config(GGPOSessionCallbacks callbacks, int numPredictionFrames) : this() {
				this.callbacks = callbacks;
				num_prediction_frames = numPredictionFrames;
			}

			public Config(GGPOSessionCallbacks callbacks, int numPredictionFrames, int numPlayers, int inputSize) {
				this.callbacks = callbacks;
				num_prediction_frames = numPredictionFrames;
				num_players = numPlayers;
				input_size = inputSize;
			}
		};
		
		public struct Event {
			public readonly Type type;
			public readonly ConfirmedInput confirmedInput;
			
			public enum Type {
				ConfirmedInput,
			}
			public struct ConfirmedInput {
				public readonly GameInput input;
			}
		}

		public Sync(ref UdpMsg.connect_status[] connect_status) {
			_local_connect_status = connect_status;
			_input_queues = null;
			_framecount = 0;
			_last_confirmed_frame = -1;
			_max_prediction_frames = 0;
			_savedstate = default;
		}

		~Sync() {
			for (int i = 0; i < _savedstate.frames.Length; i++) {
				_callbacks.free_buffer(_savedstate.frames[i].buf);
			}

			_input_queues = null;
		}

		public void Init(ref Config config) {
			_config = config;
			_callbacks = config.callbacks;
			_framecount = 0;
			_rollingback = false;

			_max_prediction_frames = config.num_prediction_frames;

			CreateQueues(ref config);
		}

		public void SetLastConfirmedFrame(int frame) {
			_last_confirmed_frame = frame;
			if (_last_confirmed_frame > 0) {
				for (int i = 0; i < _config.num_players; i++) {
					_input_queues[i].DiscardConfirmedFrames(frame - 1);
				}
			}
		}

		public void SetFrameDelay(int queue, int delay) {
			_input_queues[queue].SetFrameDelay(delay);
		}

		public bool AddLocalInput(int queue, ref GameInput input) {
			int frames_behind = _framecount - _last_confirmed_frame; 
			if (_framecount >= _max_prediction_frames && frames_behind >= _max_prediction_frames) {
				LogUtil.Log("Rejecting input from emulator: reached prediction barrier.\n");
				return false;
			}

			if (_framecount == 0) {
				SaveCurrentFrame();
			}

			LogUtil.Log($"Sending undelayed local frame {_framecount} to queue {queue}.\n");
			input.frame = _framecount;
			_input_queues[queue].AddInput(ref input);

			return true;
		}

		public void AddRemoteInput(int queue, ref GameInput input) {
			_input_queues[queue].AddInput(ref input);
		}

		public unsafe int GetConfirmedInputs(byte* values, int size, int frame) {
			int disconnect_flags = 0;

			if (size < _config.num_players * _config.input_size) {
				throw new ArgumentException();
			}

			for (int i = 0; i < size; i++) {
				values[i] = 0;
			}
			
			for (int i = 0; i < _config.num_players; i++) {
				GameInput input = new GameInput();
				if (_local_connect_status[i].disconnected && frame > _local_connect_status[i].last_frame) {
					disconnect_flags |= (1 << i);
					input.erase();
				} else {
					_input_queues[i].GetConfirmedInput(frame, out input);
				}

				int startingByteIndex = i * _config.input_size;
				for (int j = 0; j < _config.input_size; j++) {
					values[startingByteIndex + j] = input.bits[j];
				}
			}
			return disconnect_flags;
		}

		public unsafe int SynchronizeInputs(ref Array values, int size) {
			int disconnect_flags = 0;
			//char *output = (char *)values;

			if (size < _config.num_players * _config.input_size) {
				throw new ArgumentException();
			}
			
			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, 0);
			}
			
			for (int i = 0; i < _config.num_players; i++) {
				GameInput input = new GameInput();
				if (_local_connect_status[i].disconnected && _framecount > _local_connect_status[i].last_frame) {
					disconnect_flags |= (1 << i);
					input.erase();
				} else {
					_input_queues[i].GetInput(_framecount, out input);
				}
				
				int startingByteIndex = i * _config.input_size;
				for (int j = 0; j < _config.input_size; j++) {
					Buffer.SetByte(values, startingByteIndex + j, input.bits[j]);
				}
			}
			return disconnect_flags;
		}

		public void CheckSimulation(int timeout) {
			if (!CheckSimulationConsistency(out int seek_to)) {
				AdjustSimulation(seek_to);
			}
		}

		public void AdjustSimulation(int seek_to) {
			int framecount = _framecount;
			int count = _framecount - seek_to;

			LogUtil.Log("Catching up\n");
			_rollingback = true;

			/*
			 * Flush our input queue and load the last frame.
			 */
			LoadFrame(seek_to);
			if (_framecount != seek_to) {
				throw new ArgumentException();
			}

			/*
			 * Advance frame by frame (stuffing notifications back to 
			 * the master).
			 */
			ResetPrediction(_framecount);
			for (int i = 0; i < count; i++) {
				_callbacks.advance_frame(0);
			}

			if (_framecount != framecount) {
				throw new ArgumentException();
			}

			_rollingback = false;

			LogUtil.Log("---\n");   
		}

		public void IncrementFrame() {
			_framecount++;
			SaveCurrentFrame();
		}
		
		public int GetFrameCount() { return _framecount; }
		public bool InRollback() { return _rollingback; }

		public bool GetEvent(out Event e) {
			if (_event_queue.size() != 0) {
				e = _event_queue.front();
				_event_queue.pop();
				return true;
			}

			e = default;
			return false;
		}
		
		//friend SyncTestBackend;

		public struct SavedFrame {
			public byte[] buf;
			public int cbuf;
			public int frame { get; set; }
			public int checksum;

			private SavedFrame(byte[] buf, int cbuf, int frame, int checksum) {
				this.buf = buf;
				this.cbuf = cbuf;
				this.frame = frame;
				this.checksum = checksum;
			}

			public static SavedFrame CreateDefault() {
				return new SavedFrame(
					null,
					0,
					-1,
					0
				);
			}
		};
		
		protected struct SavedState {
			public readonly SavedFrame[] frames;
			public int head { get; set; }

			public SavedState(int head) : this() {
				frames = new SavedFrame[MAX_PREDICTION_FRAMES + 2];
			}
		};

		public void LoadFrame(int frame) {
			// find the frame in question
			if (frame == _framecount) {
				LogUtil.Log("Skipping NOP.\n");
				return;
			}

			// Move the head pointer back and load it up
			_savedstate.head = FindSavedFrameIndex(frame);
			SavedFrame state = _savedstate.frames[_savedstate.head];

			LogUtil.Log($"=== Loading frame info {state.frame} (size: {state.cbuf}  checksum: {state.checksum:x8}).\n");

			if (state.buf == null || state.cbuf == 0) {
				throw new ArgumentException();
			}
			
			_callbacks.load_game_state(state.buf);

			// Reset framecount and the head of the state ring-buffer to point in
			// advance of the current frame (as if we had just finished executing it).
			_framecount = state.frame;
			_savedstate.head = (_savedstate.head + 1) % _savedstate.frames.Length;
		}


		public void SaveCurrentFrame() {
			/*
			* See StateCompress for the real save feature implemented by FinalBurn.
			* Write everything into the head, then advance the head pointer.
			*/
			SavedFrame state = _savedstate.frames[_savedstate.head];
			if (state.buf != null) {
				_callbacks.free_buffer(state.buf);
				state.buf = null;
			}
			state.frame = _framecount;
			_callbacks.save_game_state(ref state.buf, ref state.cbuf, ref state.checksum, state.frame);

			LogUtil.Log($"=== Saved frame info {state.frame} (size: {state.cbuf}  checksum: {state.checksum:x8}).\n");
			_savedstate.head = (_savedstate.head + 1) % _savedstate.frames.Length;
		}

		protected int FindSavedFrameIndex(int frame) {
			int i, count = _savedstate.frames.Length;
			for (i = 0; i < count; i++) {
				if (_savedstate.frames[i].frame == frame) {
					break;
				}
			}
			if (i == count) {
				throw new ArgumentException();
			}
			return i;
		}

		public SavedFrame GetLastSavedFrame() {
			int i = _savedstate.head - 1;
			if (i < 0) {
				i = _savedstate.frames.Length - 1;
			}
			return _savedstate.frames[i];
		}

		protected bool CreateQueues(ref Config config) {
			_input_queues = new InputQueue[_config.num_players];
			for (int i = 0; i < _input_queues.Length; i++) {
				_input_queues[i] = new InputQueue();
			}

			for (int i = 0; i < _config.num_players; i++) {
				_input_queues[i].Init(i, _config.input_size);
			}
			return true;
		}

		protected bool CheckSimulationConsistency(out int seekTo) {
			int first_incorrect = GameInput.NullFrame;
			for (int i = 0; i < _config.num_players; i++) {
				int incorrect = _input_queues[i].GetFirstIncorrectFrame();
				LogUtil.Log($"considering incorrect frame {incorrect} reported by queue {i}.\n");

				if (incorrect != GameInput.NullFrame && (first_incorrect == GameInput.NullFrame || incorrect < first_incorrect)) {
					first_incorrect = incorrect;
				}
			}

			if (first_incorrect == GameInput.NullFrame) {
				LogUtil.Log("prediction ok.  proceeding.\n");
				seekTo = -1;
				return true;
			}
			seekTo = first_incorrect;
			return false;
		}

		protected void ResetPrediction(int frameNumber) {
			for (int i = 0; i < _config.num_players; i++) {
				_input_queues[i].ResetPrediction(frameNumber);
			}
		}
		
		protected GGPOSessionCallbacks _callbacks;
		protected SavedState     _savedstate;
		protected Config         _config;
		
		protected bool           _rollingback;
		protected int            _last_confirmed_frame;
		protected int            _framecount;
		protected int            _max_prediction_frames;
		
		protected InputQueue[]     _input_queues;

		protected RingBuffer<Event> _event_queue;
		protected UdpMsg.connect_status[] _local_connect_status;
   }
}