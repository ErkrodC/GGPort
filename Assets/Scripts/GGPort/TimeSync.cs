/* -----------------------------------------------------------------------
 * GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
 *
 * Use of this software is governed by the MIT license that can be found
 * in the LICENSE file.
 */

using System;

namespace GGPort {
	public class TimeSync {
		public const int FRAME_WINDOW_SIZE = 40;
		public const int MIN_UNIQUE_FRAMES = 10;
		public const int MIN_FRAME_ADVANTAGE = 3;
		public const int MAX_FRAME_ADVANTAGE = 9;
		
		protected int[] _local = new int[FRAME_WINDOW_SIZE];
		protected int[] _remote = new int[FRAME_WINDOW_SIZE];
		protected GameInput[] _last_inputs = new GameInput[MIN_UNIQUE_FRAMES];
		protected int _next_prediction;
		
		private static int count = 0;

		public TimeSync() {
			for (int i = 0; i < FRAME_WINDOW_SIZE; i++) {
				_local[i] = 0;
				_remote[i] = 0;
			}
			
			_next_prediction = FRAME_WINDOW_SIZE * 3;
		}

		public void advance_frame(ref GameInput input, int advantage, int radvantage) {
			// Remember the last frame and frame advantage
			_last_inputs[input.frame % _last_inputs.Length] = input;
			_local[input.frame % _local.Length] = advantage;
			_remote[input.frame % _remote.Length] = radvantage;
		}

		public int recommend_frame_wait_duration(bool require_idle_input) {
			// Average our local and remote frame advantages
			int i, sum = 0;
			float advantage, radvantage;
			for (i = 0; i < _local.Length; i++) {
				sum += _local[i];
			}
			advantage = sum / (float) _local.Length;

			sum = 0;
			for (i = 0; i < _remote.Length; i++) {
				sum += _remote[i];
			}
			radvantage = sum / (float) _remote.Length;
			
			count++;

			// See if someone should take action.  The person furthest ahead
			// needs to slow down so the other user can catch up.
			// Only do this if both clients agree on who's ahead!!
			if (advantage >= radvantage) { return 0; }

			// Both clients agree that we're the one ahead.  Split
			// the difference between the two to figure out how long to
			// sleep for.
			int sleep_frames = (int)(((radvantage - advantage) / 2) + 0.5);

			LogUtil.Log($"iteration {count}:  sleep frames is {sleep_frames}\n");

			// Some things just aren't worth correcting for.  Make sure
			// the difference is relevant before proceeding.
			if (sleep_frames < MIN_FRAME_ADVANTAGE) { return 0; }

			// Make sure our input had been "idle enough" before recommending
			// a sleep.  This tries to make the emulator sleep while the
			// user's input isn't sweeping in arcs (e.g. fireball motions in
			// Street Fighter), which could cause the player to miss moves.
			if (require_idle_input) {
				for (i = 1; i < _last_inputs.Length; i++) {
					if (!_last_inputs[i].equal(ref _last_inputs[0], true)) {
						LogUtil.Log($"iteration {count}:  rejecting due to input stuff at position {i}...!!!\n");
						return 0;
					}
				}
			}

			// Success!!! Recommend the number of frames to sleep and adjust
			return Math.Min(sleep_frames, MAX_FRAME_ADVANTAGE);
		}
	}
}