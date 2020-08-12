/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	public class InputQueue {
		private const int kInputQueueLength = 128;
		private const int kDefaultInputSize = 4;

		private int id;
		private int head;
		private int tail;
		private int length;
		private bool firstFrame;

		private int lastUserAddedFrame;
		private int lastAddedFrame;
		private int firstIncorrectFrame;
		private int lastFrameRequested;

		private int frameDelay;

		private GameInput[] inputs;
		private GameInput prediction;

		public InputQueue(int inputSize = kDefaultInputSize) {
			Init(-1, inputSize);
			inputs = new GameInput[kInputQueueLength];
		}

		public void Init(int id, int inputSize) {
			this.id = id;
			head = 0;
			tail = 0;
			length = 0;
			frameDelay = 0;
			firstFrame = true;
			lastUserAddedFrame = GameInput.NULL_FRAME;
			firstIncorrectFrame = GameInput.NULL_FRAME;
			lastFrameRequested = GameInput.NULL_FRAME;
			lastAddedFrame = GameInput.NULL_FRAME;
			prediction.Init(GameInput.NULL_FRAME, null, inputSize);

			/*
			* This is safe because we know the GameInput is a proper structure (as in,
			* no virtual methods, no contained classes, etc.).
			*/

			inputs = new GameInput[kInputQueueLength];
			for (int i = 0; i < inputs.Length; i++) {
				inputs[i] = default;
			}

			for (int j = 0; j < kInputQueueLength; j++) {
				inputs[j].size = inputSize;
			}
		}

		public int GetLastConfirmedFrame() {
			Log($"returning last confirmed frame {lastAddedFrame}.{Environment.NewLine}");
			return lastAddedFrame;
		}

		public int GetFirstIncorrectFrame() {
			return firstIncorrectFrame;
		}

		public int GetLength() { return length; }

		public void SetFrameDelay(int delay) { frameDelay = delay; }

		public void ResetPrediction(int frame) {
			Platform.Assert(firstIncorrectFrame == GameInput.NULL_FRAME || frame <= firstIncorrectFrame);

			Log($"resetting all prediction errors back to frame {frame}.{Environment.NewLine}");

			/*
			 * There's nothing really to do other than reset our prediction
			 * state and the incorrect frame counter...
			 */
			prediction.frame = GameInput.NULL_FRAME;
			firstIncorrectFrame = GameInput.NULL_FRAME;
			lastFrameRequested = GameInput.NULL_FRAME;
		}

		public void DiscardConfirmedFrames(int frame) {
			Platform.Assert(frame >= 0);

			if (lastFrameRequested != GameInput.NULL_FRAME) {
				frame = Math.Min(frame, lastFrameRequested);
			}

			Log(
				$"discarding confirmed frames up to {frame} (last_added:{lastAddedFrame} length:{length} [head:{head} tail:{tail}]).{Environment.NewLine}"
			);

			if (frame >= lastAddedFrame) {
				tail = head;
			} else {
				int offset = frame - inputs[tail].frame + 1;

				Log($"difference of {offset} frames.{Environment.NewLine}");
				Platform.Assert(offset >= 0);

				tail = (tail + offset) % kInputQueueLength;
				length -= offset;
			}

			Log($"after discarding, new tail is {tail} (frame:{inputs[tail].frame}).{Environment.NewLine}");
		}

		public bool GetConfirmedInput(int requestedFrame, out GameInput input) {
			Platform.Assert(firstIncorrectFrame == GameInput.NULL_FRAME || requestedFrame < firstIncorrectFrame);

			int offset = requestedFrame % kInputQueueLength;
			if (inputs[offset].frame != requestedFrame) {
				input = default;
				return false;
			}

			input = inputs[offset];
			return true;
		}

		public bool GetInput(int requestedFrame, out GameInput input) {
			Log($"requesting input frame {requestedFrame}.{Environment.NewLine}");

			/*
			* No one should ever try to grab any input when we have a prediction
			* error.  Doing so means that we're just going further down the wrong
			* path.  ASSERT this to verify that it's true.
			*/
			Platform.Assert(firstIncorrectFrame == GameInput.NULL_FRAME);

			/*
			* Remember the last requested frame number for later.  We'll need
			* this in AddInput() to drop out of prediction mode.
			*/
			lastFrameRequested = requestedFrame;

			Platform.Assert(requestedFrame >= inputs[tail].frame);

			if (prediction.IsNull()) {
				/*
				* If the frame requested is in our range, fetch it out of the queue and
				* return it.
				*/
				int offset = requestedFrame - inputs[tail].frame;

				if (offset < length) {
					offset = (offset + tail) % kInputQueueLength;

					Platform.Assert(inputs[offset].frame == requestedFrame);

					input = inputs[offset];
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
					prediction.Erase();
				} else if (lastAddedFrame == GameInput.NULL_FRAME) {
					Log($"basing new prediction frame from nothing, since we have no frames yet.{Environment.NewLine}");
					prediction.Erase();
				} else {
					Log(
						$"basing new prediction frame from previously added frame (queue entry:{PreviousFrame(head)}, frame:{inputs[PreviousFrame(head)].frame}).{Environment.NewLine}"
					);
					prediction = inputs[PreviousFrame(head)];
				}

				prediction.frame++;
			}

			Platform.Assert(prediction.frame >= 0);

			/*
			* If we've made it this far, we must be predicting.  Go ahead and
			* forward the prediction frame contents.  Be sure to return the
			* frame number requested by the client, though.
			*/
			input = prediction;
			input.frame = requestedFrame;
			Log($"returning prediction frame number {input.frame} ({prediction.frame}).{Environment.NewLine}");

			return false;
		}

		public void AddInput(ref GameInput input) {
			Log($"adding input frame number {input.frame} to queue.{Environment.NewLine}");

			/*
			* These next two lines simply verify that inputs are passed in 
			* sequentially by the user, regardless of frame delay.
			*/
			Platform.Assert(lastUserAddedFrame == GameInput.NULL_FRAME || input.frame == lastUserAddedFrame + 1);

			lastUserAddedFrame = input.frame;

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

			int expectedFrame = firstFrame ? 0 : inputs[PreviousFrame(head)].frame + 1;

			frame += frameDelay;

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
				
				AddDelayedInputToQueue(inputs[PreviousFrame(head)], expectedFrame);
				expectedFrame++;
			}

			Platform.Assert(frame == 0 || frame == inputs[PreviousFrame(head)].frame + 1);

			return frame;
		}

		private void AddDelayedInputToQueue(GameInput input, int frameNumber) {
			Log($"adding delayed input frame number {frameNumber} to queue.{Environment.NewLine}");

			Platform.Assert(input.size == prediction.size);
			Platform.Assert(lastAddedFrame == GameInput.NULL_FRAME || frameNumber == lastAddedFrame + 1);
			Platform.Assert(frameNumber == 0 || inputs[PreviousFrame(head)].frame == frameNumber - 1);

			/*
			* Add the frame to the back of the queue
			*/
			inputs[head] = input;
			inputs[head].frame = frameNumber;
			head = (head + 1) % kInputQueueLength;
			length++;
			firstFrame = false;

			lastAddedFrame = frameNumber;

			if (!prediction.IsNull()) {
				Platform.Assert(frameNumber == prediction.frame);

				/*
				* We've been predicting...  See if the inputs we've gotten match
				* what we've been predicting.  If so, don't worry about it.  If not,
				* remember the first input which was incorrect so we can report it
				* in GetFirstIncorrectFrame()
				*/
				if (firstIncorrectFrame == GameInput.NULL_FRAME && !prediction.Equal(input, true)) {
					Log($"frame {frameNumber} does not match prediction.  marking error.{Environment.NewLine}");
					firstIncorrectFrame = frameNumber;
				}

				/*
				* If this input is the same frame as the last one requested and we
				* still haven't found any mis-predicted inputs, we can dump out
				* of predition mode entirely!  Otherwise, advance the prediction frame
				* count up.
				*/
				if (prediction.frame == lastFrameRequested && firstIncorrectFrame == GameInput.NULL_FRAME) {
					Log("prediction is correct!  dumping out of prediction mode.{Environment.NewLine}");
					prediction.frame = GameInput.NULL_FRAME;
				} else {
					prediction.frame++;
				}
			}

			Platform.Assert(length <= kInputQueueLength);
		}

		private void Log(string msg) {
			LogUtil.Log($"input queue{id} | {msg}");
		}

		private static int PreviousFrame(int offset) {
			return offset == 0
				? kInputQueueLength - 1
				: offset - 1;
		}
	}
}