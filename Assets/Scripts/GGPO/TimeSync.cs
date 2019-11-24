/* -----------------------------------------------------------------------
 * GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
 *
 * Use of this software is governed by the MIT license that can be found
 * in the LICENSE file.
 */

namespace GGPort {
	public class TimeSync {
		public const int FRAME_WINDOW_SIZE = 40;
		public const int MIN_UNIQUE_FRAMES = 10;
		public const int MIN_FRAME_ADVANTAGE = 3;
		public const int MAX_FRAME_ADVANTAGE = 9;

		public TimeSync();
		~TimeSync();

		public void advance_frame(ref GameInput input, int advantage, int radvantage);
		public int recommend_frame_wait_duration(bool require_idle_input);

		protected int _local[FRAME_WINDOW_SIZE];
		protected int _remote[FRAME_WINDOW_SIZE];
		protected GameInput _last_inputs[MIN_UNIQUE_FRAMES];
		protected int _next_prediction;
	}
}