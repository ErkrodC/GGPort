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

		public AdvanceFrameDelegate advanceFrameEvent;

		protected readonly int _numPlayers;
		protected readonly int _inputSize;
		protected readonly int _maxPredictionFrames;
		private readonly CircularQueue<Event> _eventQueue;
		private readonly PeerMessage.ConnectStatus[] _localConnectStatuses;
		protected bool _isRollingBack;
		protected int _lastConfirmedFrame;
		protected int _frameCount;
		protected InputQueue[] _inputQueues;

		protected Sync(
			PeerMessage.ConnectStatus[] localConnectStatuses,
			int maxPredictionFrames,
			int numPlayers,
			int inputSize
		) {
			_eventQueue = new CircularQueue<Event>(32);
			_localConnectStatuses = localConnectStatuses;
			_inputQueues = null;
			_frameCount = 0;
			_lastConfirmedFrame = -1;
			_maxPredictionFrames = 0;

			_numPlayers = numPlayers;
			_inputSize = inputSize;
			_frameCount = 0;
			_isRollingBack = false;

			_maxPredictionFrames = maxPredictionFrames;
		}

		~Sync() {
			_inputQueues = null;
		}

		public void SetLastConfirmedFrame(int frame) {
			_lastConfirmedFrame = frame;
			if (_lastConfirmedFrame <= 0) { return; }

			for (int i = 0; i < _numPlayers; i++) {
				_inputQueues[i].DiscardConfirmedFrames(frame - 1);
			}
		}

		public void SetFrameDelay(int queue, int delay) {
			_inputQueues[queue].SetFrameDelay(delay);
		}

		public unsafe int GetConfirmedInputs(byte* values, int size, int frame) {
			int disconnectFlags = 0;

			Platform.Assert(size >= _numPlayers * _inputSize);

			for (int i = 0; i < size; i++) {
				values[i] = 0;
			}

			for (int i = 0; i < _numPlayers; i++) {
				GameInput input = new GameInput();
				if (_localConnectStatuses[i].isDisconnected && frame > _localConnectStatuses[i].lastFrame) {
					disconnectFlags |= 1 << i;
					input.Erase();
				} else {
					_inputQueues[i].GetConfirmedInput(frame, out input);
				}

				int startingByteIndex = i * _inputSize;
				for (int j = 0; j < _inputSize; j++) {
					values[startingByteIndex + j] = input.bits[j];
				}
			}

			return disconnectFlags;
		}

		public unsafe int SynchronizeInputs(Array values, int size) {
			int disconnectFlags = 0;
			//char *output = (char *)values; // TODO

			Platform.Assert(size >= _numPlayers * _inputSize);

			for (int i = 0; i < size; i++) {
				Buffer.SetByte(values, i, 0);
			}

			for (int i = 0; i < _numPlayers; i++) {
				GameInput input = new GameInput();
				if (_localConnectStatuses[i].isDisconnected && _frameCount > _localConnectStatuses[i].lastFrame) {
					disconnectFlags |= 1 << i;
					input.Erase();
				} else {
					_inputQueues[i].GetInput(_frameCount, out input);
				}

				int startingByteIndex = i * _inputSize;
				for (int j = 0; j < _inputSize; j++) {
					Buffer.SetByte(values, startingByteIndex + j, input.bits[j]);
				}
			}

			return disconnectFlags;
		}

		public int GetFrameCount() {
			return _frameCount;
		}

		public bool InRollback() {
			return _isRollingBack;
		}

		public bool GetEvent(out Event e) {
			if (_eventQueue.count != 0) {
				e = _eventQueue.Pop();
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
				ConfirmedInput
			}

			public struct ConfirmedInput {
				public readonly GameInput input;
			}
		}
	}

	public class Sync<TGameState> : Sync {
		public event SaveGameStateDelegate<TGameState> saveGameStateEvent;
		public event LoadGameStateDelegate<TGameState> loadGameStateEvent;
		public event FreeBufferDelegate<TGameState> freeBufferEvent;

		private SavedState _savedState;

		public Sync(
			PeerMessage.ConnectStatus[] localConnectStatuses,
			int maxPredictionFrames,
			int numPlayers = 0,
			int inputSize = 0
		)
			: base(
				localConnectStatuses,
				maxPredictionFrames,
				numPlayers,
				inputSize
			) {
			_savedState = new SavedState(0);
			CreateQueues();
		}

		~Sync() {
			for (int i = 0; i < _savedState.frames.Length; i++) {
				freeBufferEvent?.Invoke(_savedState.frames[i].gameState);
			}
		}

		public void AddRemoteInput(int queue, ref GameInput input) {
			_inputQueues[queue].AddInput(ref input);
		}

		public void CheckSimulation(int timeout) {
			if (CheckSimulationConsistency(out int seekTo)) { return; }

			AdjustSimulation(seekTo);
		}

		public void AdjustSimulation(int seekTo) {
			int cachedFrameCount = _frameCount;
			int count = _frameCount - seekTo;

			LogUtil.Log($"Catching up{Environment.NewLine}");
			_isRollingBack = true;

			// Flush our input queue and load the last frame.
			LoadFrame(seekTo);
			Platform.Assert(_frameCount == seekTo);

			// Advance frame by frame (stuffing notifications back to the master).
			ResetPrediction(_frameCount);
			for (int i = 0; i < count; i++) {
				advanceFrameEvent?.Invoke(0);
			}

			Platform.Assert(_frameCount == cachedFrameCount);

			_isRollingBack = false;

			LogUtil.Log($"---{Environment.NewLine}");
		}

		public void IncrementFrame() {
			_frameCount++;
			SaveCurrentFrame();
		}

		public bool AddLocalInput(int queue, ref GameInput input) {
			int framesBehind = _frameCount - _lastConfirmedFrame;
			if (_frameCount >= _maxPredictionFrames && framesBehind >= _maxPredictionFrames) {
				LogUtil.Log($"Rejecting input from emulator: reached prediction barrier.{Environment.NewLine}");
				return false;
			}

			if (_frameCount == 0) {
				SaveCurrentFrame();
			}

			LogUtil.Log($"Sending undelayed local frame {_frameCount} to queue {queue}.{Environment.NewLine}");
			input.frame = _frameCount;
			_inputQueues[queue].AddInput(ref input);

			return true;
		}

		public class SavedFrame {
			public TGameState gameState;
			public int frame;
			public int checksum;

			public SavedFrame(TGameState gameState = default, int frame = -1, int checksum = 0) {
				this.gameState = gameState;
				this.frame = frame;
				this.checksum = checksum;
			}
		}

		private struct SavedState {
			public readonly SavedFrame[] frames;
			public int head;

			public SavedState(int head)
				: this() {
				frames = new SavedFrame[MAX_PREDICTION_FRAMES + 2];
				for (int i = 0; i < MAX_PREDICTION_FRAMES + 2; i++) { frames[i] = new SavedFrame(); }

				this.head = head;
			}
		}

		public void LoadFrame(int frame) {
			// find the frame in question
			LogUtil.Log($"Try Load Frame {frame}.");

			if (frame == _frameCount) {
				LogUtil.Log($"Skipping NOP.{Environment.NewLine}");
				return;
			}

			// Move the head pointer back and load it up
			_savedState.head = FindSavedFrameIndex(frame);
			SavedFrame savedFrame = _savedState.frames[_savedState.head];

			LogUtil.Log(
				$"=== Loading frame info {savedFrame.frame} (checksum: {savedFrame.checksum:x8}).{Environment.NewLine}"
			);

			Platform.Assert(
				savedFrame.gameState != null,
				$"{nameof(savedFrame.gameState)} inside {nameof(SavedFrame)} was null and cannot be restored."
			);

			loadGameStateEvent?.Invoke(savedFrame.gameState);

			// Reset frameCount and the head of the state ring-buffer to point in
			// advance of the current frame (as if we had just finished executing it).
			_frameCount = savedFrame.frame;
			_savedState.head = (_savedState.head + 1) % _savedState.frames.Length;
		}

		public void SaveCurrentFrame() {
			/*
			* See StateCompress for the real save feature implemented by FinalBurn.
			* Write everything into the head, then advance the head pointer.
			*/
			SavedFrame savedFrame = _savedState.frames[_savedState.head];
			if (savedFrame.gameState != null) {
				freeBufferEvent?.Invoke(savedFrame.gameState);
				savedFrame.gameState = default;
			}

			savedFrame.frame = _frameCount;
			saveGameStateEvent?.Invoke(out savedFrame.gameState, out savedFrame.checksum, savedFrame.frame);
			_savedState.frames[_savedState.head] = savedFrame;

			LogUtil.Log(
				$"=== Saved frame info {savedFrame.frame} (checksum: {savedFrame.checksum:x8}).{Environment.NewLine}"
			);
			_savedState.head = (_savedState.head + 1) % _savedState.frames.Length;
		}

		private int FindSavedFrameIndex(int frame) {
			int i, count = _savedState.frames.Length;
			for (i = 0; i < count; i++) {
				if (_savedState.frames[i].frame == frame) {
					break;
				}
			}

			Platform.Assert(i != count, $"Could not find saved frame with frame index {frame}");

			return i;
		}

		public SavedFrame GetLastSavedFrame() {
			int i = _savedState.head - 1;
			if (i < 0) {
				i = _savedState.frames.Length - 1;
			}

			return _savedState.frames[i];
		}

		private void CreateQueues() {
			_inputQueues = new InputQueue[_numPlayers];
			for (int i = 0; i < _inputQueues.Length; i++) {
				_inputQueues[i] = new InputQueue();
			}

			for (int i = 0; i < _numPlayers; i++) {
				_inputQueues[i].Init(i, _inputSize);
			}
		}

		private bool CheckSimulationConsistency(out int seekTo) {
			int firstIncorrect = GameInput.NULL_FRAME;
			for (int i = 0; i < _numPlayers; i++) {
				int incorrect = _inputQueues[i].GetFirstIncorrectFrame();
				LogUtil.Log($"considering incorrect frame {incorrect} reported by queue {i}.{Environment.NewLine}");

				if (incorrect != GameInput.NULL_FRAME
				    && (firstIncorrect == GameInput.NULL_FRAME || incorrect < firstIncorrect)) {
					firstIncorrect = incorrect;
				}
			}

			if (firstIncorrect == GameInput.NULL_FRAME) {
				LogUtil.Log($"prediction ok.  proceeding.{Environment.NewLine}");
				seekTo = -1;
				return true;
			}

			seekTo = firstIncorrect;
			return false;
		}

		private void ResetPrediction(int frameNumber) {
			for (int i = 0; i < _numPlayers; i++) {
				_inputQueues[i].ResetPrediction(frameNumber);
			}
		}
	}
}