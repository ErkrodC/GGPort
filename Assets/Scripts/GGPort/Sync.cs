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

		private SessionCallbacks callbacks;
		private SavedState savedState;
		private Config config;

		private bool isRollingBack;
		private int lastConfirmedFrame;
		private int frameCount;
		private int maxPredictionFrames;

		private InputQueue[] inputQueues;

		private readonly CircularQueue<Event> eventQueue;
		private readonly PeerMessage.ConnectStatus[] localConnectStatuses;

		public Sync(PeerMessage.ConnectStatus[] localConnectStatuses) {
			eventQueue = new CircularQueue<Event>(32);
			this.localConnectStatuses = localConnectStatuses;
			inputQueues = null;
			frameCount = 0;
			lastConfirmedFrame = -1;
			maxPredictionFrames = 0;
			savedState = new SavedState(0);
		}

		~Sync() {
			for (int i = 0; i < savedState.Frames.Length; i++) {
				callbacks.FreeBuffer(savedState.Frames[i].GameState);
			}

			inputQueues = null;
		}

		public void Init(Config config) {
			this.config = config;
			callbacks = config.Callbacks;
			frameCount = 0;
			isRollingBack = false;

			maxPredictionFrames = config.NumPredictionFrames;

			CreateQueues();
		}

		public void SetLastConfirmedFrame(int frame) {
			lastConfirmedFrame = frame;
			if (lastConfirmedFrame <= 0) { return; }

			for (int i = 0; i < config.NumPlayers; i++) {
				inputQueues[i].DiscardConfirmedFrames(frame - 1);
			}
		}

		public void SetFrameDelay(int queue, int delay) {
			inputQueues[queue].SetFrameDelay(delay);
		}

		public bool AddLocalInput(int queue, ref GameInput input) {
			int framesBehind = frameCount - lastConfirmedFrame; 
			if (frameCount >= maxPredictionFrames && framesBehind >= maxPredictionFrames) {
				LogUtil.Log($"Rejecting input from emulator: reached prediction barrier.{Environment.NewLine}");
				return false;
			}

			if (frameCount == 0) {
				SaveCurrentFrame();
			}

			LogUtil.Log($"Sending undelayed local frame {frameCount} to queue {queue}.{Environment.NewLine}");
			input.Frame = frameCount;
			inputQueues[queue].AddInput(ref input);

			return true;
		}

		public void AddRemoteInput(int queue, ref GameInput input) {
			inputQueues[queue].AddInput(ref input);
		}

		public unsafe int GetConfirmedInputs(byte* values, int size, int frame) {
			int disconnectFlags = 0;

			Platform.Assert(size >= config.NumPlayers * config.InputSize);

			for (int i = 0; i < size; i++) {
				values[i] = 0;
			}
			
			for (int i = 0; i < config.NumPlayers; i++) {
				GameInput input = new GameInput();
				if (localConnectStatuses[i].IsDisconnected && frame > localConnectStatuses[i].LastFrame) {
					disconnectFlags |= (1 << i);
					input.Erase();
				} else {
					inputQueues[i].GetConfirmedInput(frame, out input);
				}

				int startingByteIndex = i * config.InputSize;
				for (int j = 0; j < config.InputSize; j++) {
					values[startingByteIndex + j] = input.Bits[j];
				}
			}
			return disconnectFlags;
		}

		public unsafe int SynchronizeInputs(Array values, int size) {
			int disconnectFlags = 0;
			//char *output = (char *)values; // TODO

			Platform.Assert(size >= config.NumPlayers * config.InputSize);
			
			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, 0);
			}
			
			for (int i = 0; i < config.NumPlayers; i++) {
				GameInput input = new GameInput();
				if (localConnectStatuses[i].IsDisconnected && frameCount > localConnectStatuses[i].LastFrame) {
					disconnectFlags |= (1 << i);
					input.Erase();
				} else {
					inputQueues[i].GetInput(frameCount, out input);
				}
				
				int startingByteIndex = i * config.InputSize;
				for (int j = 0; j < config.InputSize; j++) {
					Buffer.SetByte(values, startingByteIndex + j, input.Bits[j]);
				}
			}
			return disconnectFlags;
		}

		public void CheckSimulation(int timeout) {
			if (!CheckSimulationConsistency(out int seekTo)) {
				AdjustSimulation(seekTo);
			}
		}

		public void AdjustSimulation(int seekTo) {
			int cachedFrameCount = frameCount;
			int count = frameCount - seekTo;

			LogUtil.Log($"Catching up{Environment.NewLine}");
			isRollingBack = true;

			// Flush our input queue and load the last frame.
			LoadFrame(seekTo);
			Platform.Assert(frameCount == seekTo);

			// Advance frame by frame (stuffing notifications back to the master).
			ResetPrediction(frameCount);
			for (int i = 0; i < count; i++) {
				callbacks.AdvanceFrame(0);
			}

			Platform.Assert(frameCount == cachedFrameCount);

			isRollingBack = false;

			LogUtil.Log($"---{Environment.NewLine}");   
		}

		public void IncrementFrame() {
			frameCount++;
			SaveCurrentFrame();
		}
		
		public int GetFrameCount() { return frameCount; }
		public bool InRollback() { return isRollingBack; }

		public bool GetEvent(out Event e) {
			if (eventQueue.Count != 0) {
				e = eventQueue.Pop();
				return true;
			}

			e = default;
			return false;
		}
		
		//friend SyncTestBackend; // TODO

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
			if (frame == frameCount) {
				LogUtil.Log($"Skipping NOP.{Environment.NewLine}");
				return;
			}

			// Move the head pointer back and load it up
			savedState.Head = FindSavedFrameIndex(frame);
			SavedFrame savedFrame = savedState.Frames[savedState.Head];

			LogUtil.Log($"=== Loading frame info {savedFrame.Frame} (checksum: {savedFrame.Checksum:x8}).{Environment.NewLine}");

			Platform.Assert(
				savedFrame.GameState != null,
				$"{nameof(savedFrame.GameState)} inside {nameof(SavedFrame)} was null and cannot be restored."
			);
			
			callbacks.LoadGameState(savedFrame.GameState);

			// Reset frameCount and the head of the state ring-buffer to point in
			// advance of the current frame (as if we had just finished executing it).
			frameCount = savedFrame.Frame;
			savedState.Head = (savedState.Head + 1) % savedState.Frames.Length;
		}
		
		public void SaveCurrentFrame() {
			/*
			* See StateCompress for the real save feature implemented by FinalBurn.
			* Write everything into the head, then advance the head pointer.
			*/
			SavedFrame savedFrame = savedState.Frames[savedState.Head];
			if (savedFrame.GameState != null) {
				callbacks.FreeBuffer(savedFrame.GameState);
				savedFrame.GameState = null;
			}
			
			savedFrame.Frame = frameCount;
			callbacks.SaveGameState(out savedFrame.GameState, out savedFrame.Checksum, savedFrame.Frame);

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

			Platform.Assert(i != count, $"Could not find saved frame with frame index {frame}");
			
			return i;
		}

		public SavedFrame GetLastSavedFrame() {
			int i = savedState.Head - 1;
			if (i < 0) {
				i = savedState.Frames.Length - 1;
			}
			return savedState.Frames[i];
		}

		private void CreateQueues() {
			inputQueues = new InputQueue[config.NumPlayers];
			for (int i = 0; i < inputQueues.Length; i++) {
				inputQueues[i] = new InputQueue();
			}

			for (int i = 0; i < config.NumPlayers; i++) {
				inputQueues[i].Init(i, config.InputSize);
			}
		}

		private bool CheckSimulationConsistency(out int seekTo) {
			int firstIncorrect = GameInput.kNullFrame;
			for (int i = 0; i < config.NumPlayers; i++) {
				int incorrect = inputQueues[i].GetFirstIncorrectFrame();
				LogUtil.Log($"considering incorrect frame {incorrect} reported by queue {i}.{Environment.NewLine}");

				if (incorrect != GameInput.kNullFrame && (firstIncorrect == GameInput.kNullFrame || incorrect < firstIncorrect)) {
					firstIncorrect = incorrect;
				}
			}

			if (firstIncorrect == GameInput.kNullFrame) {
				LogUtil.Log($"prediction ok.  proceeding.{Environment.NewLine}");
				seekTo = -1;
				return true;
			}
			seekTo = firstIncorrect;
			return false;
		}

		private void ResetPrediction(int frameNumber) {
			for (int i = 0; i < config.NumPlayers; i++) {
				inputQueues[i].ResetPrediction(frameNumber);
			}
		}
		
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
		
		// TODO remove? does nothing here but might do something in syncTest || spectator backends
		public struct Event {
			public readonly Type type;
			public readonly ConfirmedInput confirmedInput;
			
			public enum Type {
				ConfirmedInput,
			}
			
			public struct ConfirmedInput {
				public readonly GameInput Input;
			}
		}
	}
}