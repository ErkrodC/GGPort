/* -----------------------------------------------------------------------
 * GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
 *
 * Use of this software is governed by the MIT license that can be found
 * in the LICENSE file.
 */

using System;

namespace GGPort {
	public class TimeSync {
		private const int kFrameWindowSize = 40;
		private const int kMinUniqueFrames = 10;
		private const int kMinFrameAdvantage = 3;
		private const int kMaxFrameAdvantage = 9;

		private readonly int[] local = new int[kFrameWindowSize];
		private readonly int[] remote = new int[kFrameWindowSize];
		private readonly GameInput[] lastInputs = new GameInput[kMinUniqueFrames];
		private int nextPrediction; // TODO used for stats?
		
		private static int count = 0;

		public TimeSync() {
			for (int i = 0; i < kFrameWindowSize; i++) {
				local[i] = 0;
				remote[i] = 0;
			}
			
			nextPrediction = kFrameWindowSize * 3;
		}

		public void AdvanceFrame(GameInput input, int advantage, int remoteAdvantage) {
			// Remember the last frame and frame advantage
			lastInputs[input.frame % lastInputs.Length] = input;
			local[input.frame % local.Length] = advantage;
			remote[input.frame % remote.Length] = remoteAdvantage;
		}

		public int recommend_frame_wait_duration(bool require_idle_input) {
			// Average our local and remote frame advantages
			int i, sum = 0;
			for (i = 0; i < local.Length; i++) {
				sum += local[i];
			}

			sum = 0;
			for (i = 0; i < remote.Length; i++) {
				sum += remote[i];
			}

			// See if someone should take action.  The person furthest ahead
			// needs to slow down so the other user can catch up.
			// Only do this if both clients agree on who's ahead!!
			float advantage = sum / (float) local.Length;
			float remoteAdvantage = sum / (float) remote.Length;
			if (advantage >= remoteAdvantage) { return 0; }

			// Both clients agree that we're the one ahead.  Split
			// the difference between the two to figure out how long to
			// sleep for.
			int sleepFrames = (int)((remoteAdvantage - advantage) / 2 + 0.5);

			count++;
			LogUtil.Log($"iteration {count}:  sleep frames is {sleepFrames}{Environment.NewLine}");

			// Some things just aren't worth correcting for.  Make sure
			// the difference is relevant before proceeding.
			if (sleepFrames < kMinFrameAdvantage) { return 0; }

			// Make sure our input had been "idle enough" before recommending
			// a sleep.  This tries to make the emulator sleep while the
			// user's input isn't sweeping in arcs (e.g. fireball motions in
			// Street Fighter), which could cause the player to miss moves.
			if (require_idle_input) {
				for (i = 1; i < lastInputs.Length; i++) {
					if (lastInputs[i].Equal(lastInputs[0], true)) { continue; }

					LogUtil.Log($"iteration {count}:  rejecting due to input stuff at position {i}...!!!{Environment.NewLine}");
					return 0;
				}
			}

			// Success!!! Recommend the number of frames to sleep and adjust
			return Math.Min(sleepFrames, kMaxFrameAdvantage);
		}
	}
}