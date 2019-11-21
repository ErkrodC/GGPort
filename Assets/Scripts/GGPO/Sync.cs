/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

namespace GGPort {
	public class Sync {
		public const int MAX_PREDICTION_FRAMES = 8;
		
		public struct Config {
			public readonly GGPOSessionCallbacks callbacks;
			public readonly int num_prediction_frames;
			public readonly int num_players;
			public readonly int input_size;

			public Config(GGPOSessionCallbacks callbacks, int numPredictionFrames) : this() {
				this.callbacks = callbacks;
				num_prediction_frames = numPredictionFrames;
			}
		};
		
		public struct Event {
			public readonly Type type;
			public readonly ConfirmedInput confirmedInput;
			
			public enum Type {
				ConfirmedInput,
			}
			public struct ConfirmedInput {
				public readonly GameInput input;
			}
		}

		public Sync(UdpMsg.connect_status *connect_status);
		//public virtual ~Sync();
		
		public void Init(Config &config);

		public void SetLastConfirmedFrame(int frame);
		public void SetFrameDelay(int queue, int delay);
		public bool AddLocalInput(int queue, GameInput &input);
		public void AddRemoteInput(int queue, GameInput &input);
		public int GetConfirmedInputs(void *values, int size, int frame);
		public int SynchronizeInputs(void *values, int size);
		
		public void CheckSimulation(int timeout);
		public void AdjustSimulation(int seek_to);
		public void IncrementFrame();
		
		public int GetFrameCount() { return _framecount; }
		public bool InRollback() { return _rollingback; }
		
		public bool GetEvent(Event &e);
		
		//friend SyncTestBackend;

		public struct SavedFrame {
			public readonly byte[]    buf;
			public readonly int      cbuf;
			public readonly int      frame;
			public readonly int      checksum;
			public SavedFrame() : buf(NULL), cbuf(0), frame(-1), checksum(0) { }
		};
		
		protected struct SavedState {
			public readonly SavedFrame frames[MAX_PREDICTION_FRAMES + 2];
			public readonly int head;
		};

		public void LoadFrame(int frame);
		public void SaveCurrentFrame();
		protected int FindSavedFrameIndex(int frame);
		public SavedFrame GetLastSavedFrame();
		
		protected bool CreateQueues(Config &config);
		protected bool CheckSimulationConsistency(int *seekTo);
		protected void ResetPrediction(int frameNumber);
		
		protected GGPOSessionCallbacks _callbacks;
		protected SavedState     _savedstate;
		protected Config         _config;
		
		protected bool           _rollingback;
		protected int            _last_confirmed_frame;
		protected int            _framecount;
		protected int            _max_prediction_frames;
		
		protected InputQueue     *_input_queues;

		protected RingBuffer<> 32> _event_queue;
		protected UdpMsg.connect_status *_local_connect_status;
   }
}