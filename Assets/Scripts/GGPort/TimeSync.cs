/* -----------------------------------------------------------------------
 * GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
 *
 * Use of this software is governed by the MIT license that can be found
 * in the LICENSE file.
 */

using System;

namespace GGPort {
	public class TimeSync {
		private const int _FRAME_WINDOW_SIZE = 40;
		private const int _MIN_UNIQUE_FRAMES = 10;
		private const int _MIN_FRAME_ADVANTAGE = 3;
		private const int _MAX_FRAME_ADVANTAGE = 9;

		private readonly int[] _local = new int[_FRAME_WINDOW_SIZE];
		private readonly int[] _remote = new int[_FRAME_WINDOW_SIZE];
		private readonly GameInput[] _lastInputs = new GameInput[_MIN_UNIQUE_FRAMES];
		private int _nextPrediction; // TODO used for stats?

		private static int _count;

		public TimeSync() {
			for (int i = 0; i < _FRAME_WINDOW_SIZE; i++) {
				_local[i] = 0;
				_remote[i] = 0;
			}

			_nextPrediction = _FRAME_WINDOW_SIZE * 3;
		}

		public void AdvanceFrame(GameInput input, int advantage, int remoteAdvantage) {
			// Remember the last frame and frame advantage
			_lastInputs[input.frame % _lastInputs.Length] = input;
			_local[input.frame % _local.Length] = advantage;
			_remote[input.frame % _remote.Length] = remoteAdvantage;
		}

		public int recommend_frame_wait_duration(bool requireIdleInput) {
			// Average our local and remote frame advantages
			int i, sum = 0;
			for (i = 0; i < _local.Length; i++) {
				sum += _local[i];
			}

			sum = 0;
			for (i = 0; i < _remote.Length; i++) {
				sum += _remote[i];
			}

			// See if someone should take action.  The person furthest ahead
			// needs to slow down so the other user can catch up.
			// Only do this if both clients agree on who's ahead!!
			float advantage = sum / (float) _local.Length;
			float remoteAdvantage = sum / (float) _remote.Length;
			if (advantage >= remoteAdvantage) { return 0; }

			// Both clients agree that we're the one ahead.  Split
			// the difference between the two to figure out how long to
			// sleep for.
			int sleepFrames = (int) ((remoteAdvantage - advantage) / 2 + 0.5);

			_count++;
			LogUtil.Log($"iteration {_count}:  sleep frames is {sleepFrames}{Environment.NewLine}");

			// Some things just aren't worth correcting for.  Make sure
			// the difference is relevant before proceeding.
			if (sleepFrames < _MIN_FRAME_ADVANTAGE) { return 0; }

			// Make sure our input had been "idle enough" before recommending
			// a sleep.  This tries to make the emulator sleep while the
			// user's input isn't sweeping in arcs (e.g. fireball motions in
			// Street Fighter), which could cause the player to miss moves.
			if (requireIdleInput) {
				for (i = 1; i < _lastInputs.Length; i++) {
					if (_lastInputs[i].Equal(_lastInputs[0], true)) { continue; }

					LogUtil.Log(
						$"iteration {_count}:  rejecting due to input stuff at position {i}...!!!{Environment.NewLine}"
					);
					return 0;
				}
			}

			// Success!!! Recommend the number of frames to sleep and adjust
			return Math.Min(sleepFrames, _MAX_FRAME_ADVANTAGE);
		}
	}
}