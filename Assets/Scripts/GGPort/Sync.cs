/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	public class Sync {
		public const int kMaxPredictionFrames = 8;
		
		protected SessionCallbacks Callbacks;
		protected SavedState     savedState;
		protected Config         config;
		
		protected bool           IsRollingBack;
		protected int            LastConfirmedFrame;
		protected int            FrameCount;
		protected int            MaxPredictionFrames;
		
		protected InputQueue[]     InputQueues;

		protected CircularQueue<Event> EventQueue = new CircularQueue<Event>(32);
		protected UDPMessage.ConnectStatus[] LocalConnectStatuses;
		
		public struct Config {
			public readonly SessionCallbacks Callbacks;
			public readonly int NumPredictionFrames;
			public readonly int NumPlayers;
			public readonly int InputSize;

			public Config(SessionCallbacks callbacks, int numPredictionFrames) : this() {
				Callbacks = callbacks;
				NumPredictionFrames = numPredictionFrames;
			}

			public Config(SessionCallbacks callbacks, int numPredictionFrames, int numPlayers, int inputSize) {
				Callbacks = callbacks;
				NumPredictionFrames = numPredictionFrames;
				NumPlayers = numPlayers;
				InputSize = inputSize;
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

		public Sync(ref UDPMessage.ConnectStatus[] localConnectStatuses) {
			LocalConnectStatuses = localConnectStatuses;
			InputQueues = null;
			FrameCount = 0;
			LastConfirmedFrame = -1;
			MaxPredictionFrames = 0;
			savedState = new SavedState(0);
		}

		~Sync() {
			for (int i = 0; i < savedState.Frames.Length; i++) {
				Callbacks.FreeBuffer(savedState.Frames[i].GameState);
			}

			InputQueues = null;
		}

		public void Init(ref Config config) {
			this.config = config;
			Callbacks = config.Callbacks;
			FrameCount = 0;
			IsRollingBack = false;

			MaxPredictionFrames = config.NumPredictionFrames;

			CreateQueues(ref config);
		}

		public void SetLastConfirmedFrame(int frame) {
			LastConfirmedFrame = frame;
			if (LastConfirmedFrame > 0) {
				for (int i = 0; i < config.NumPlayers; i++) {
					InputQueues[i].DiscardConfirmedFrames(frame - 1);
				}
			}
		}

		public void SetFrameDelay(int queue, int delay) {
			InputQueues[queue].SetFrameDelay(delay);
		}

		public bool AddLocalInput(int queue, ref GameInput input) {
			int frames_behind = FrameCount - LastConfirmedFrame; 
			if (FrameCount >= MaxPredictionFrames && frames_behind >= MaxPredictionFrames) {
				LogUtil.Log($"Rejecting input from emulator: reached prediction barrier.{Environment.NewLine}");
				return false;
			}

			if (FrameCount == 0) {
				SaveCurrentFrame();
			}

			LogUtil.Log($"Sending undelayed local frame {FrameCount} to queue {queue}.{Environment.NewLine}");
			input.frame = FrameCount;
			InputQueues[queue].AddInput(ref input);

			return true;
		}

		public void AddRemoteInput(int queue, ref GameInput input) {
			InputQueues[queue].AddInput(ref input);
		}

		public unsafe int GetConfirmedInputs(byte* values, int size, int frame) {
			int disconnect_flags = 0;

			if (size < config.NumPlayers * config.InputSize) {
				throw new ArgumentException();
			}

			for (int i = 0; i < size; i++) {
				values[i] = 0;
			}
			
			for (int i = 0; i < config.NumPlayers; i++) {
				GameInput input = new GameInput();
				if (LocalConnectStatuses[i].IsDisconnected && frame > LocalConnectStatuses[i].LastFrame) {
					disconnect_flags |= (1 << i);
					input.erase();
				} else {
					InputQueues[i].GetConfirmedInput(frame, out input);
				}

				int startingByteIndex = i * config.InputSize;
				for (int j = 0; j < config.InputSize; j++) {
					values[startingByteIndex + j] = input.bits[j];
				}
			}
			return disconnect_flags;
		}

		public unsafe int SynchronizeInputs(ref Array values, int size) {
			int disconnect_flags = 0;
			//char *output = (char *)values;

			if (size < config.NumPlayers * config.InputSize) {
				throw new ArgumentException();
			}
			
			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, 0);
			}
			
			for (int i = 0; i < config.NumPlayers; i++) {
				GameInput input = new GameInput();
				if (LocalConnectStatuses[i].IsDisconnected && FrameCount > LocalConnectStatuses[i].LastFrame) {
					disconnect_flags |= (1 << i);
					input.erase();
				} else {
					InputQueues[i].GetInput(FrameCount, out input);
				}
				
				int startingByteIndex = i * config.InputSize;
				for (int j = 0; j < config.InputSize; j++) {
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
			int framecount = FrameCount;
			int count = FrameCount - seek_to;

			LogUtil.Log($"Catching up{Environment.NewLine}");
			IsRollingBack = true;

			// Flush our input queue and load the last frame.
			LoadFrame(seek_to);
			if (FrameCount != seek_to) {
				throw new ArgumentException();
			}

			// Advance frame by frame (stuffing notifications back to the master).
			ResetPrediction(FrameCount);
			for (int i = 0; i < count; i++) {
				Callbacks.AdvanceFrame(0);
			}

			if (FrameCount != framecount) {
				throw new ArgumentException();
			}

			IsRollingBack = false;

			LogUtil.Log($"---{Environment.NewLine}");   
		}

		public void IncrementFrame() {
			FrameCount++;
			SaveCurrentFrame();
		}
		
		public int GetFrameCount() { return FrameCount; }
		public bool InRollback() { return IsRollingBack; }

		public bool GetEvent(out Event e) {
			if (EventQueue.Count != 0) {
				e = EventQueue.Pop();
				return true;
			}

			e = default;
			return false;
		}
		
		//friend SyncTestBackend;

		public struct SavedFrame {
			public object GameState;
			public int Frame { get; set; }
			public int Checksum;

			private SavedFrame(object gameState, int frame, int checksum) {
				GameState = gameState;
				Frame = frame;
				Checksum = checksum;
			}

			public static SavedFrame CreateDefault() {
				return new SavedFrame(
					null,
					-1,
					0
				);
			}
		};
		
		protected struct SavedState {
			public readonly SavedFrame[] Frames;
			public int Head { get; set; }

			public SavedState(int head) : this() {
				Frames = new SavedFrame[kMaxPredictionFrames + 2];
				Head = head;
			}
		};

		public void LoadFrame(int frame) {
			// find the frame in question
			if (frame == FrameCount) {
				LogUtil.Log($"Skipping NOP.{Environment.NewLine}");
				return;
			}

			// Move the head pointer back and load it up
			savedState.Head = FindSavedFrameIndex(frame);
			SavedFrame savedFrame = savedState.Frames[savedState.Head];

			LogUtil.Log($"=== Loading frame info {savedFrame.Frame} (checksum: {savedFrame.Checksum:x8}).{Environment.NewLine}");

			if (savedFrame.GameState == null) {
				throw new ArgumentException($"{nameof(savedFrame.GameState)} inside {nameof(SavedFrame)} was null and cannot be restored.");
			}
			
			Callbacks.LoadGameState(savedFrame.GameState);

			// Reset framecount and the head of the state ring-buffer to point in
			// advance of the current frame (as if we had just finished executing it).
			FrameCount = savedFrame.Frame;
			savedState.Head = (savedState.Head + 1) % savedState.Frames.Length;
		}


		public void SaveCurrentFrame() {
			/*
			* See StateCompress for the real save feature implemented by FinalBurn.
			* Write everything into the head, then advance the head pointer.
			*/
			SavedFrame savedFrame = savedState.Frames[savedState.Head];
			if (savedFrame.GameState != null) {
				Callbacks.FreeBuffer(savedFrame.GameState);
				savedFrame.GameState = null;
			}
			
			savedFrame.Frame = FrameCount;
			Callbacks.SaveGameState(out savedFrame.GameState, out savedFrame.Checksum, savedFrame.Frame);

			LogUtil.Log($"=== Saved frame info {savedFrame.Frame} (checksum: {savedFrame.Checksum:x8}).{Environment.NewLine}");
			savedState.Head = (savedState.Head + 1) % savedState.Frames.Length;
		}

		protected int FindSavedFrameIndex(int frame) {
			int i, count = savedState.Frames.Length;
			for (i = 0; i < count; i++) {
				if (savedState.Frames[i].Frame == frame) {
					break;
				}
			}
			
			if (i == count) {
				throw new ArgumentException();
			}
			
			return i;
		}

		public SavedFrame GetLastSavedFrame() {
			int i = savedState.Head - 1;
			if (i < 0) {
				i = savedState.Frames.Length - 1;
			}
			return savedState.Frames[i];
		}

		protected bool CreateQueues(ref Config config) {
			InputQueues = new InputQueue[this.config.NumPlayers];
			for (int i = 0; i < InputQueues.Length; i++) {
				InputQueues[i] = new InputQueue();
			}

			for (int i = 0; i < this.config.NumPlayers; i++) {
				InputQueues[i].Init(i, this.config.InputSize);
			}
			return true;
		}

		protected bool CheckSimulationConsistency(out int seekTo) {
			int first_incorrect = GameInput.kNullFrame;
			for (int i = 0; i < config.NumPlayers; i++) {
				int incorrect = InputQueues[i].GetFirstIncorrectFrame();
				LogUtil.Log($"considering incorrect frame {incorrect} reported by queue {i}.{Environment.NewLine}");

				if (incorrect != GameInput.kNullFrame && (first_incorrect == GameInput.kNullFrame || incorrect < first_incorrect)) {
					first_incorrect = incorrect;
				}
			}

			if (first_incorrect == GameInput.kNullFrame) {
				LogUtil.Log($"prediction ok.  proceeding.{Environment.NewLine}");
				seekTo = -1;
				return true;
			}
			seekTo = first_incorrect;
			return false;
		}

		protected void ResetPrediction(int frameNumber) {
			for (int i = 0; i < config.NumPlayers; i++) {
				InputQueues[i].ResetPrediction(frameNumber);
			}
		}
	}
}