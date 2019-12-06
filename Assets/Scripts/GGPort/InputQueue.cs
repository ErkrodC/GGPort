/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	public class InputQueue {
		public const int INPUT_QUEUE_LENGTH = 128;
		public const int DEFAULT_INPUT_SIZE = 4;

		public InputQueue(int input_size = DEFAULT_INPUT_SIZE) {
			Init(-1, input_size);
			_inputs = new GameInput[INPUT_QUEUE_LENGTH];
		}

		public unsafe void Init(int id, int input_size) {
			_id = id;
			_head = 0;
			_tail = 0;
			_length = 0;
			_frame_delay = 0;
			_first_frame = true;
			_last_user_added_frame = GameInput.kNullFrame;
			_first_incorrect_frame = GameInput.kNullFrame;
			_last_frame_requested = GameInput.kNullFrame;
			_last_added_frame = GameInput.kNullFrame;
			_prediction.init(GameInput.kNullFrame, null, input_size);

			/*
			* This is safe because we know the GameInput is a proper structure (as in,
			* no virtual methods, no contained classes, etc.).
			*/

			_inputs = new GameInput[INPUT_QUEUE_LENGTH];
			for (int i = 0; i < _inputs.Length; i++) {
				_inputs[i] = default;
			}

			for (int j = 0; j < INPUT_QUEUE_LENGTH; j++) {
				_inputs[j].size = input_size;
			}
		}

		public int GetLastConfirmedFrame() {
			Log($"returning last confirmed frame {_last_added_frame}.{Environment.NewLine}");
			return _last_added_frame;
		}

		public int GetFirstIncorrectFrame() {
			return _first_incorrect_frame;
		}

		public int GetLength() { return _length; }

		public void SetFrameDelay(int delay) { _frame_delay = delay; }

		public void ResetPrediction(int frame) {
			if (_first_incorrect_frame != GameInput.kNullFrame && frame > _first_incorrect_frame) {
				throw new ArgumentException();
			}

			Log($"resetting all prediction errors back to frame {frame}.{Environment.NewLine}");

			/*
			 * There's nothing really to do other than reset our prediction
			 * state and the incorrect frame counter...
			 */
			_prediction.frame = GameInput.kNullFrame;
			_first_incorrect_frame = GameInput.kNullFrame;
			_last_frame_requested = GameInput.kNullFrame;
		}

		public void DiscardConfirmedFrames(int frame) {
			if (frame < 0) {
				throw new ArgumentException();
			}

			if (_last_frame_requested != GameInput.kNullFrame) {
				frame = Math.Min(frame, _last_frame_requested);
			}

			Log(
				$"discarding confirmed frames up to {frame} (last_added:{_last_added_frame} length:{_length} [head:{_head} tail:{_tail}]).{Environment.NewLine}"
			);

			if (frame >= _last_added_frame) {
				_tail = _head;
			} else {
				int offset = frame - _inputs[_tail].frame + 1;

				Log($"difference of {offset} frames.{Environment.NewLine}");
				if (!(offset >= 0)) {
					throw new ArgumentException();
				}

				_tail = (_tail + offset) % INPUT_QUEUE_LENGTH;
				_length -= offset;
			}

			Log($"after discarding, new tail is {_tail} (frame:{_inputs[_tail].frame}).{Environment.NewLine}");
		}

		public bool GetConfirmedInput(int requested_frame, out GameInput input) {
			if (_first_incorrect_frame != GameInput.kNullFrame && requested_frame >= _first_incorrect_frame) {
				throw new ArgumentException();
			}

			int offset = requested_frame % INPUT_QUEUE_LENGTH;
			if (_inputs[offset].frame != requested_frame) {
				input = default;
				return false;
			}

			input = _inputs[offset];
			return true;
		}

		public bool GetInput(int requested_frame, out GameInput input) {
			Log($"requesting input frame {requested_frame}.{Environment.NewLine}");

			/*
			* No one should ever try to grab any input when we have a prediction
			* error.  Doing so means that we're just going further down the wrong
			* path.  ASSERT this to verify that it's true.
			*/
			if (_first_incorrect_frame != GameInput.kNullFrame) {
				throw new ArgumentException();
			}

			/*
			* Remember the last requested frame number for later.  We'll need
			* this in AddInput() to drop out of prediction mode.
			*/
			_last_frame_requested = requested_frame;

			if (requested_frame < _inputs[_tail].frame) {
				throw new ArgumentException();
			}

			if (_prediction.frame == GameInput.kNullFrame) {
				/*
				* If the frame requested is in our range, fetch it out of the queue and
				* return it.
				*/
				int offset = requested_frame - _inputs[_tail].frame;

				if (offset < _length) {
					offset = (offset + _tail) % INPUT_QUEUE_LENGTH;

					if (_inputs[offset].frame != requested_frame) {
						throw new ArgumentException();
					}

					input = _inputs[offset];
					Log($"returning confirmed frame number {input.frame}.{Environment.NewLine}");
					return true;
				}

				/*
				* The requested frame isn't in the queue.  Bummer.  This means we need
				* to return a prediction frame.  Predict that the user will do the
				* same thing they did last time.
				*/
				if (requested_frame == 0) {
					Log($"basing new prediction frame from nothing, you're client wants frame 0.{Environment.NewLine}");
					_prediction.erase();
				} else if (_last_added_frame == GameInput.kNullFrame) {
					Log($"basing new prediction frame from nothing, since we have no frames yet.{Environment.NewLine}");
					_prediction.erase();
				} else {
					Log(
						$"basing new prediction frame from previously added frame (queue entry:{PREVIOUS_FRAME(_head)}, frame:{_inputs[PREVIOUS_FRAME(_head)].frame}).{Environment.NewLine}"
					);
					_prediction = _inputs[PREVIOUS_FRAME(_head)];
				}

				_prediction.frame++;
			}

			if (_prediction.frame < 0) {
				throw new ArgumentException();
			}

			/*
			* If we've made it this far, we must be predicting.  Go ahead and
			* forward the prediction frame contents.  Be sure to return the
			* frame number requested by the client, though.
			*/
			input = _prediction;
			input.frame = requested_frame;
			Log($"returning prediction frame number {input.frame} ({_prediction.frame}).{Environment.NewLine}");

			return false;
		}

		public void AddInput(ref GameInput input) {
			int new_frame;

			Log($"adding input frame number {input.frame} to queue.{Environment.NewLine}");

			/*
			* These next two lines simply verify that inputs are passed in 
			* sequentially by the user, regardless of frame delay.
			*/
			if (_last_user_added_frame != GameInput.kNullFrame && input.frame != _last_user_added_frame + 1) {
				throw new ArgumentException();
			}

			_last_user_added_frame = input.frame;

			/*
			* Move the queue head to the correct point in preparation to
			* input the frame into the queue.
			*/
			new_frame = AdvanceQueueHead(input.frame);
			if (new_frame != GameInput.kNullFrame) {
				AddDelayedInputToQueue(ref input, new_frame);
			}

			/*
			* Update the frame number for the input.  This will also set the
			* frame to GameInput::NullFrame for frames that get dropped (by
			* design).
			*/
			input.frame = new_frame;
		}

		protected int AdvanceQueueHead(int frame) {
			Log($"advancing queue head to frame {frame}.{Environment.NewLine}");

			int expected_frame = _first_frame ? 0 : _inputs[PREVIOUS_FRAME(_head)].frame + 1;

			frame += _frame_delay;

			if (expected_frame > frame) {
				/*
				* This can occur when the frame delay has dropped since the last
				* time we shoved a frame into the system.  In this case, there's
				* no room on the queue.  Toss it.
				*/
				Log($"Dropping input frame {frame} (expected next frame to be {expected_frame}).{Environment.NewLine}");
				return GameInput.kNullFrame;
			}

			while (expected_frame < frame) {
				/*
				* This can occur when the frame delay has been increased since the last
				* time we shoved a frame into the system.  We need to replicate the
				* last frame in the queue several times in order to fill the space
				* left.
				*/
				Log($"Adding padding frame {expected_frame} to account for change in frame delay.{Environment.NewLine}");
				
				AddDelayedInputToQueue(ref _inputs[PREVIOUS_FRAME(_head)], expected_frame);
				expected_frame++;
			}

			if (frame != 0 && frame != _inputs[PREVIOUS_FRAME(_head)].frame + 1) {
				throw new ArgumentException();
			}

			return frame;
		}

		protected void AddDelayedInputToQueue(ref GameInput input, int frame_number) {
			Log($"adding delayed input frame number {frame_number} to queue.{Environment.NewLine}");

			if (input.size != _prediction.size) {
				throw new ArgumentException();
			}

			if (_last_added_frame != GameInput.kNullFrame && frame_number != _last_added_frame + 1) {
				throw new ArgumentException();
			}

			if (!(frame_number == 0 || _inputs[PREVIOUS_FRAME(_head)].frame == frame_number - 1)) {
				throw new ArgumentException();
			}

			/*
			* Add the frame to the back of the queue
			*/
			_inputs[_head] = input;
			_inputs[_head].frame = frame_number;
			_head = (_head + 1) % INPUT_QUEUE_LENGTH;
			_length++;
			_first_frame = false;

			_last_added_frame = frame_number;

			if (_prediction.frame != GameInput.kNullFrame) {
				if (frame_number != _prediction.frame) {
					throw new ArgumentException();
				}

				/*
				* We've been predicting...  See if the inputs we've gotten match
				* what we've been predicting.  If so, don't worry about it.  If not,
				* remember the first input which was incorrect so we can report it
				* in GetFirstIncorrectFrame()
				*/
				if (_first_incorrect_frame == GameInput.kNullFrame && !_prediction.equal(ref input, true)) {
					Log($"frame {frame_number} does not match prediction.  marking error.{Environment.NewLine}");
					_first_incorrect_frame = frame_number;
				}

				/*
				* If this input is the same frame as the last one requested and we
				* still haven't found any mis-predicted inputs, we can dump out
				* of predition mode entirely!  Otherwise, advance the prediction frame
				* count up.
				*/
				if (_prediction.frame == _last_frame_requested && _first_incorrect_frame == GameInput.kNullFrame) {
					Log("prediction is correct!  dumping out of prediction mode.{Environment.NewLine}");
					_prediction.frame = GameInput.kNullFrame;
				} else {
					_prediction.frame++;
				}
			}

			if (_length > INPUT_QUEUE_LENGTH) {
				throw new ArgumentException();
			}
		}

		protected void Log(string msg) {
			LogUtil.Log(msg);
		}

		private static int PREVIOUS_FRAME(int offset) {
			return offset == 0
				? INPUT_QUEUE_LENGTH - 1
				: offset - 1;
		}

		protected int _id;
		protected int _head;
		protected int _tail;
		protected int _length;
		protected bool _first_frame;

		protected int _last_user_added_frame;
		protected int _last_added_frame;
		protected int _first_incorrect_frame;
		protected int _last_frame_requested;

		protected int _frame_delay;

		protected GameInput[] _inputs;
		protected GameInput _prediction;
	}
}