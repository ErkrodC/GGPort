/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	public class InputQueue {
		private const int _INPUT_QUEUE_LENGTH = 128;
		private const int _DEFAULT_INPUT_SIZE = 1;

		private int _id;
		private int _head;
		private int _tail;
		private int _length;
		private bool _firstFrame;

		private int _lastUserAddedFrame;
		private int _lastAddedFrame;
		private int _firstIncorrectFrame;
		private int _lastFrameRequested;

		private int _frameDelay;

		private GameInput[] _inputs;
		private GameInput _prediction;

		public InputQueue(int inputSize = _DEFAULT_INPUT_SIZE) {
			Init(-1, inputSize);
			_inputs = new GameInput[_INPUT_QUEUE_LENGTH];
		}

		public void Init(int id, int inputSize) {
			_id = id;
			_head = 0;
			_tail = 0;
			_length = 0;
			_frameDelay = 0;
			_firstFrame = true;
			_lastUserAddedFrame = GameInput.NULL_FRAME;
			_firstIncorrectFrame = GameInput.NULL_FRAME;
			_lastFrameRequested = GameInput.NULL_FRAME;
			_lastAddedFrame = GameInput.NULL_FRAME;
			_prediction.Init(GameInput.NULL_FRAME, null, inputSize);

			/*
			* This is safe because we know the GameInput is a proper structure (as in,
			* no virtual methods, no contained classes, etc.).
			*/

			_inputs = new GameInput[_INPUT_QUEUE_LENGTH];
			for (int i = 0; i < _inputs.Length; i++) {
				_inputs[i] = default;
			}

			for (int j = 0; j < _INPUT_QUEUE_LENGTH; j++) {
				_inputs[j].size = inputSize;
			}
		}

		public int GetLastConfirmedFrame() {
			Log($"returning last confirmed frame {_lastAddedFrame}.{Environment.NewLine}");
			return _lastAddedFrame;
		}

		public int GetFirstIncorrectFrame() {
			return _firstIncorrectFrame;
		}

		public int GetLength() {
			return _length;
		}

		public void SetFrameDelay(int delay) {
			_frameDelay = delay;
		}

		public void ResetPrediction(int frame) {
			Platform.Assert(_firstIncorrectFrame == GameInput.NULL_FRAME || frame <= _firstIncorrectFrame);

			Log($"resetting all prediction errors back to frame {frame}.{Environment.NewLine}");

			/*
			 * There's nothing really to do other than reset our prediction
			 * state and the incorrect frame counter...
			 */
			_prediction.frame = GameInput.NULL_FRAME;
			_firstIncorrectFrame = GameInput.NULL_FRAME;
			_lastFrameRequested = GameInput.NULL_FRAME;
		}

		public void DiscardConfirmedFrames(int frame) {
			Platform.Assert(frame >= 0);

			if (_lastFrameRequested != GameInput.NULL_FRAME) {
				frame = Math.Min(frame, _lastFrameRequested);
			}

			Log(
				$"discarding confirmed frames up to {frame} (last_added:{_lastAddedFrame} length:{_length} [head:{_head} tail:{_tail}]).{Environment.NewLine}"
			);

			if (frame >= _lastAddedFrame) {
				_tail = _head;
			} else {
				int offset = frame - _inputs[_tail].frame + 1;

				Log($"difference of {offset} frames.{Environment.NewLine}");
				Platform.Assert(offset >= 0);

				_tail = (_tail + offset) % _INPUT_QUEUE_LENGTH;
				_length -= offset;
			}

			Log($"after discarding, new tail is {_tail} (frame:{_inputs[_tail].frame}).{Environment.NewLine}");
		}

		public bool GetConfirmedInput(int requestedFrame, out GameInput input) {
			Platform.Assert(_firstIncorrectFrame == GameInput.NULL_FRAME || requestedFrame < _firstIncorrectFrame);

			int offset = requestedFrame % _INPUT_QUEUE_LENGTH;
			if (_inputs[offset].frame != requestedFrame) {
				input = default;
				return false;
			}

			input = _inputs[offset];
			return true;
		}

		public bool GetInput(int requestedFrame, out GameInput input) {
			Log($"requesting input frame {requestedFrame}.{Environment.NewLine}");

			/*
			* No one should ever try to grab any input when we have a prediction
			* error.  Doing so means that we're just going further down the wrong
			* path.  ASSERT this to verify that it's true.
			*/
			Platform.Assert(_firstIncorrectFrame == GameInput.NULL_FRAME);

			/*
			* Remember the last requested frame number for later.  We'll need
			* this in AddInput() to drop out of prediction mode.
			*/
			_lastFrameRequested = requestedFrame;

			Platform.Assert(requestedFrame >= _inputs[_tail].frame);

			if (_prediction.IsNull()) {
				/*
				* If the frame requested is in our range, fetch it out of the queue and
				* return it.
				*/
				int offset = requestedFrame - _inputs[_tail].frame;

				if (offset < _length) {
					offset = (offset + _tail) % _INPUT_QUEUE_LENGTH;

					Platform.Assert(_inputs[offset].frame == requestedFrame);

					input = _inputs[offset];
					Log($"returning confirmed frame number {input.frame}.{Environment.NewLine}");
					return true;
				}

				/*
				* The requested frame isn't in the queue.  Bummer.  This means we need
				* to return a prediction frame.  Predict that the user will do the
				* same thing they did last time.
				*/
				if (requestedFrame == 0) {
					Log($"basing new prediction frame from nothing, you're client wants frame 0.{Environment.NewLine}");
					_prediction.Erase();
				} else if (_lastAddedFrame == GameInput.NULL_FRAME) {
					Log($"basing new prediction frame from nothing, since we have no frames yet.{Environment.NewLine}");
					_prediction.Erase();
				} else {
					Log(
						$"basing new prediction frame from previously added frame (queue entry:{PreviousFrame(_head)}, frame:{_inputs[PreviousFrame(_head)].frame}).{Environment.NewLine}"
					);
					_prediction = _inputs[PreviousFrame(_head)];
				}

				_prediction.frame++;
			}

			Platform.Assert(_prediction.frame >= 0);

			/*
			* If we've made it this far, we must be predicting.  Go ahead and
			* forward the prediction frame contents.  Be sure to return the
			* frame number requested by the client, though.
			*/
			input = _prediction;
			input.frame = requestedFrame;
			Log($"returning prediction frame number {input.frame} ({_prediction.frame}).{Environment.NewLine}");

			return false;
		}

		public void AddInput(ref GameInput input) {
			Log($"adding input frame number {input.frame} to queue.{Environment.NewLine}");

			/*
			* These next two lines simply verify that inputs are passed in 
			* sequentially by the user, regardless of frame delay.
			*/
			Platform.Assert(_lastUserAddedFrame == GameInput.NULL_FRAME || input.frame == _lastUserAddedFrame + 1);

			_lastUserAddedFrame = input.frame;

			/*
			* Move the queue head to the correct point in preparation to
			* input the frame into the queue.
			*/
			int newFrame = AdvanceQueueHead(input.frame);
			if (newFrame != GameInput.NULL_FRAME) {
				AddDelayedInputToQueue(input, newFrame);
			}

			/*
			* Update the frame number for the input.  This will also set the
			* frame to GameInput::NullFrame for frames that get dropped (by
			* design).
			*/
			input.frame = newFrame;
		}

		protected int AdvanceQueueHead(int frame) {
			Log($"advancing queue head to frame {frame}.{Environment.NewLine}");

			int expectedFrame = _firstFrame
				? 0
				: _inputs[PreviousFrame(_head)].frame + 1;

			frame += _frameDelay;

			if (expectedFrame > frame) {
				/*
				* This can occur when the frame delay has dropped since the last
				* time we shoved a frame into the system.  In this case, there's
				* no room on the queue.  Toss it.
				*/
				Log($"Dropping input frame {frame} (expected next frame to be {expectedFrame}).{Environment.NewLine}");
				return GameInput.NULL_FRAME;
			}

			while (expectedFrame < frame) {
				/*
				* This can occur when the frame delay has been increased since the last
				* time we shoved a frame into the system.  We need to replicate the
				* last frame in the queue several times in order to fill the space
				* left.
				*/
				Log($"Adding padding frame {expectedFrame} to account for change in frame delay.{Environment.NewLine}");

				AddDelayedInputToQueue(_inputs[PreviousFrame(_head)], expectedFrame);
				expectedFrame++;
			}

			Platform.Assert(frame == 0 || frame == _inputs[PreviousFrame(_head)].frame + 1);

			return frame;
		}

		private void AddDelayedInputToQueue(GameInput input, int frameNumber) {
			Log($"adding delayed input frame number {frameNumber} to queue.{Environment.NewLine}");

			Platform.Assert(input.size == _prediction.size);
			Platform.Assert(_lastAddedFrame == GameInput.NULL_FRAME || frameNumber == _lastAddedFrame + 1);
			Platform.Assert(frameNumber == 0 || _inputs[PreviousFrame(_head)].frame == frameNumber - 1);

			/*
			* Add the frame to the back of the queue
			*/
			_inputs[_head] = input;
			_inputs[_head].frame = frameNumber;
			_head = (_head + 1) % _INPUT_QUEUE_LENGTH;
			_length++;
			_firstFrame = false;

			_lastAddedFrame = frameNumber;

			if (!_prediction.IsNull()) {
				Platform.Assert(frameNumber == _prediction.frame);

				/*
				* We've been predicting...  See if the inputs we've gotten match
				* what we've been predicting.  If so, don't worry about it.  If not,
				* remember the first input which was incorrect so we can report it
				* in GetFirstIncorrectFrame()
				*/
				if (_firstIncorrectFrame == GameInput.NULL_FRAME && !_prediction.Equal(input, true)) {
					Log($"frame {frameNumber} does not match prediction.  marking error.{Environment.NewLine}");
					_firstIncorrectFrame = frameNumber;
				}

				/*
				* If this input is the same frame as the last one requested and we
				* still haven't found any mis-predicted inputs, we can dump out
				* of predition mode entirely!  Otherwise, advance the prediction frame
				* count up.
				*/
				if (_prediction.frame == _lastFrameRequested && _firstIncorrectFrame == GameInput.NULL_FRAME) {
					Log("prediction is correct!  dumping out of prediction mode.{Environment.NewLine}");
					_prediction.frame = GameInput.NULL_FRAME;
				} else {
					_prediction.frame++;
				}
			}

			Platform.Assert(_length <= _INPUT_QUEUE_LENGTH);
		}

		private void Log(string msg) {
			LogUtil.Log($"input queue{_id} | {msg}");
		}

		private static int PreviousFrame(int offset) {
			return offset == 0
				? _INPUT_QUEUE_LENGTH - 1
				: offset - 1;
		}
	}
}