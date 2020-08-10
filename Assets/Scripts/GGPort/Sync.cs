/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	public abstract class Sync {
		public const int MAX_PREDICTION_FRAMES = 8;

		public Session.AdvanceFrameDelegate advanceFrameEvent;

		protected readonly int m_numPlayers;
		protected readonly int m_inputSize;
		protected readonly int m_maxPredictionFrames;
		private readonly CircularQueue<Event> m_eventQueue;
		private readonly PeerMessage.ConnectStatus[] m_localConnectStatuses;
		protected bool m_isRollingBack;
		protected int m_lastConfirmedFrame;
		protected int m_frameCount;
		protected InputQueue[] m_inputQueues;

		public Sync(PeerMessage.ConnectStatus[] localConnectStatuses, int maxPredictionFrames, int numPlayers, int inputSize) {
			m_eventQueue = new CircularQueue<Event>(32);
			m_localConnectStatuses = localConnectStatuses;
			m_inputQueues = null;
			m_frameCount = 0;
			m_lastConfirmedFrame = -1;
			m_maxPredictionFrames = 0;
			
			m_numPlayers = numPlayers;
			m_inputSize = inputSize;
			m_frameCount = 0;
			m_isRollingBack = false;

			m_maxPredictionFrames = maxPredictionFrames;
		}

		~Sync() {
			m_inputQueues = null;
		}
		
		public void SetLastConfirmedFrame(int frame) {
			m_lastConfirmedFrame = frame;
			if (m_lastConfirmedFrame <= 0) { return; }

			for (int i = 0; i < m_numPlayers; i++) {
				m_inputQueues[i].DiscardConfirmedFrames(frame - 1);
			}
		}
		
		public void SetFrameDelay(int queue, int delay) {
			m_inputQueues[queue].SetFrameDelay(delay);
		}

		public unsafe int GetConfirmedInputs(byte* values, int size, int frame) {
			int disconnectFlags = 0;

			Platform.Assert(size >= m_numPlayers * m_inputSize);

			for (int i = 0; i < size; i++) {
				values[i] = 0;
			}
			
			for (int i = 0; i < m_numPlayers; i++) {
				GameInput input = new GameInput();
				if (m_localConnectStatuses[i].IsDisconnected && frame > m_localConnectStatuses[i].LastFrame) {
					disconnectFlags |= (1 << i);
					input.Erase();
				} else {
					m_inputQueues[i].GetConfirmedInput(frame, out input);
				}

				int startingByteIndex = i * m_inputSize;
				for (int j = 0; j < m_inputSize; j++) {
					values[startingByteIndex + j] = input.Bits[j];
				}
			}
			return disconnectFlags;
		}
		
		public unsafe int SynchronizeInputs(Array values, int size) {
			int disconnectFlags = 0;
			//char *output = (char *)values; // TODO

			Platform.Assert(size >= m_numPlayers * m_inputSize);
			
			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, 0);
			}
			
			for (int i = 0; i < m_numPlayers; i++) {
				GameInput input = new GameInput();
				if (m_localConnectStatuses[i].IsDisconnected && m_frameCount > m_localConnectStatuses[i].LastFrame) {
					disconnectFlags |= (1 << i);
					input.Erase();
				} else {
					m_inputQueues[i].GetInput(m_frameCount, out input);
				}
				
				int startingByteIndex = i * m_inputSize;
				for (int j = 0; j < m_inputSize; j++) {
					Buffer.SetByte(values, startingByteIndex + j, input.Bits[j]);
				}
			}
			return disconnectFlags;
		}
		
		public int GetFrameCount() { return m_frameCount; }
		public bool InRollback() { return m_isRollingBack; }
		
		public bool GetEvent(out Event e) {
			if (m_eventQueue.Count != 0) {
				e = m_eventQueue.Pop();
				return true;
			}

			e = default;
			return false;
		}

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
	
	public class Sync<TGameState> : Sync {
		public event Session<TGameState>.SaveGameStateDelegate saveGameStateEvent;
		public event Session<TGameState>.LoadGameStateDelegate loadGameStateEvent;
		public event Session<TGameState>.FreeBufferDelegate freeBufferEvent;

		private SavedState m_savedState;

		public Sync(PeerMessage.ConnectStatus[] localConnectStatuses, int maxPredictionFrames, int numPlayers = 0, int inputSize = 0)
			: base(localConnectStatuses, maxPredictionFrames, numPlayers, inputSize
		) {
			m_savedState = new SavedState(0);
			CreateQueues();
		}

		~Sync() {
			for (int i = 0; i < m_savedState.Frames.Length; i++) {
				freeBufferEvent?.Invoke(m_savedState.Frames[i].GameState);
			}
		}

		public void AddRemoteInput(int queue, ref GameInput input) {
			m_inputQueues[queue].AddInput(ref input);
		}

		public void CheckSimulation(int timeout) {
			if (!CheckSimulationConsistency(out int seekTo)) {
				AdjustSimulation(seekTo);
			}
		}

		public void AdjustSimulation(int seekTo) {
			int cachedFrameCount = m_frameCount;
			int count = m_frameCount - seekTo;

			LogUtil.Log($"Catching up{Environment.NewLine}");
			m_isRollingBack = true;

			// Flush our input queue and load the last frame.
			LoadFrame(seekTo);
			Platform.Assert(m_frameCount == seekTo);

			// Advance frame by frame (stuffing notifications back to the master).
			ResetPrediction(m_frameCount);
			for (int i = 0; i < count; i++) {
				advanceFrameEvent?.Invoke(0);
			}

			Platform.Assert(m_frameCount == cachedFrameCount);

			m_isRollingBack = false;

			LogUtil.Log($"---{Environment.NewLine}");   
		}

		public void IncrementFrame() {
			m_frameCount++;
			SaveCurrentFrame();
		}
		
		public bool AddLocalInput(int queue, ref GameInput input) {
			int framesBehind = m_frameCount - m_lastConfirmedFrame; 
			if (m_frameCount >= m_maxPredictionFrames && framesBehind >= m_maxPredictionFrames) {
				LogUtil.Log($"Rejecting input from emulator: reached prediction barrier.{Environment.NewLine}");
				return false;
			}

			if (m_frameCount == 0) {
				SaveCurrentFrame();
			}

			LogUtil.Log($"Sending undelayed local frame {m_frameCount} to queue {queue}.{Environment.NewLine}");
			input.Frame = m_frameCount;
			m_inputQueues[queue].AddInput(ref input);

			return true;
		}

		public class SavedFrame {
			public TGameState GameState;
			public int Frame { get; set; }
			public int Checksum;

			private SavedFrame(TGameState gameState, int frame, int checksum) {
				GameState = gameState;
				Frame = frame;
				Checksum = checksum;
			}

			public static SavedFrame CreateDefault() => new SavedFrame(default, -1, 0);
		}

		private struct SavedState {
			public readonly SavedFrame[] Frames;
			public int Head { get; set; }

			public SavedState(int head) : this() {
				Frames = new SavedFrame[MAX_PREDICTION_FRAMES + 2];
				Head = head;
			}
		}

		public void LoadFrame(int frame) {
			// find the frame in question
			LogUtil.Log($"Try Load Frame {frame}.");
			
			if (frame == m_frameCount) {
				LogUtil.Log($"Skipping NOP.{Environment.NewLine}");
				return;
			}

			// Move the head pointer back and load it up
			m_savedState.Head = FindSavedFrameIndex(frame);
			SavedFrame savedFrame = m_savedState.Frames[m_savedState.Head];

			LogUtil.Log($"=== Loading frame info {savedFrame.Frame} (checksum: {savedFrame.Checksum:x8}).{Environment.NewLine}");

			Platform.Assert(
				savedFrame.GameState != null,
				$"{nameof(savedFrame.GameState)} inside {nameof(SavedFrame)} was null and cannot be restored."
			);
			
			loadGameStateEvent?.Invoke(savedFrame.GameState);

			// Reset frameCount and the head of the state ring-buffer to point in
			// advance of the current frame (as if we had just finished executing it).
			m_frameCount = savedFrame.Frame;
			m_savedState.Head = (m_savedState.Head + 1) % m_savedState.Frames.Length;
		}
		
		public void SaveCurrentFrame() {
			/*
			* See StateCompress for the real save feature implemented by FinalBurn.
			* Write everything into the head, then advance the head pointer.
			*/
			SavedFrame savedFrame = m_savedState.Frames[m_savedState.Head];
			if (savedFrame.GameState != null) {
				freeBufferEvent?.Invoke(savedFrame.GameState);
				savedFrame.GameState = default;
			}
			
			savedFrame.Frame = m_frameCount;
			saveGameStateEvent?.Invoke(out savedFrame.GameState, out savedFrame.Checksum, savedFrame.Frame);
			m_savedState.Frames[m_savedState.Head] = savedFrame;

			LogUtil.Log($"=== Saved frame info {savedFrame.Frame} (checksum: {savedFrame.Checksum:x8}).{Environment.NewLine}");
			m_savedState.Head = (m_savedState.Head + 1) % m_savedState.Frames.Length;
		}

		protected int FindSavedFrameIndex(int frame) {
			int i, count = m_savedState.Frames.Length;
			for (i = 0; i < count; i++) {
				if (m_savedState.Frames[i].Frame == frame) {
					break;
				}
			}

			Platform.Assert(i != count, $"Could not find saved frame with frame index {frame}");
			
			return i;
		}

		public SavedFrame GetLastSavedFrame() {
			int i = m_savedState.Head - 1;
			if (i < 0) {
				i = m_savedState.Frames.Length - 1;
			}
			return m_savedState.Frames[i];
		}

		private void CreateQueues() {
			m_inputQueues = new InputQueue[m_numPlayers];
			for (int i = 0; i < m_inputQueues.Length; i++) {
				m_inputQueues[i] = new InputQueue();
			}

			for (int i = 0; i < m_numPlayers; i++) {
				m_inputQueues[i].Init(i, m_inputSize);
			}
		}

		private bool CheckSimulationConsistency(out int seekTo) {
			int firstIncorrect = GameInput.kNullFrame;
			for (int i = 0; i < m_numPlayers; i++) {
				int incorrect = m_inputQueues[i].GetFirstIncorrectFrame();
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
			for (int i = 0; i < m_numPlayers; i++) {
				m_inputQueues[i].ResetPrediction(frameNumber);
			}
		}
	}
}