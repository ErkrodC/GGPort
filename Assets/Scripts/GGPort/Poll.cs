/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Collections.Generic;
using System.Threading;

namespace GGPort {
	// TODO see where poll is used, replace with events (or existing async behavior, like for sockets)
	public interface IPollSink {
		bool OnHandlePoll(object cookie);
		bool OnMsgPoll(object cookie);
		bool OnPeriodicPoll(object cookie, long lastFireTime);
		bool OnLoopPoll(object cookie);
	};

	public class Poll {
		private const int kMaxPollableHandles = 64;

		private long startTime;
		private int handleCount;
		private readonly EventWaitHandle[] handles; // TODO better as a list? and using .Count over _handle_count
		private readonly PollSinkCallback[] handleSinks;

		private readonly List<PollSinkCallback> msgSinks;
		private readonly List<PollSinkCallback> loopSinks;
		private readonly List<PollPeriodicSinkCallback> periodicSinks;

		public Poll() {
			handleCount = 0;
			startTime = 0;

			handles = new EventWaitHandle[kMaxPollableHandles];
			for (handleCount = 0; handleCount < handles.Length; handleCount++) {
				handles[handleCount] = new EventWaitHandle(false, EventResetMode.ManualReset);
			}
			
			handleSinks = new PollSinkCallback[kMaxPollableHandles];
			
			msgSinks = new List<PollSinkCallback>(16);
			loopSinks = new List<PollSinkCallback>(16);
			periodicSinks = new List<PollPeriodicSinkCallback>(16);
		}

		public void RegisterHandle(ref IPollSink sink, ref EventWaitHandle eventWaitHandle, object cookie = null) {
			if (handleCount >= kMaxPollableHandles - 1) {
				throw new IndexOutOfRangeException();
			}

			handles[handleCount] = eventWaitHandle;
			handleSinks[handleCount] = new PollSinkCallback(sink, cookie);
			handleCount++;
		}

		public void RegisterMsgLoop(ref IPollSink sink, object cookie = null) {
			msgSinks.Add(new PollSinkCallback(sink, cookie));
		}

		public void RegisterPeriodic(ref IPollSink sink, int interval, object cookie = null) {
			periodicSinks.Add(new PollPeriodicSinkCallback(sink, cookie, interval));
		}

		public void RegisterLoop(IPollSink sink, object cookie = null) {
			loopSinks.Add(new PollSinkCallback(sink, cookie));
		}

		public void Run() {
			while (Pump(100)) { }
		}

		public bool Pump(long timeout) {
			const int kInfiniteTimeout = -1;
			bool finished = false;

			if (startTime == 0) { startTime = Platform.GetCurrentTimeMS(); }
			
			long elapsedMS = Platform.GetCurrentTimeMS() - startTime;
			long maxWait = ComputeWaitTime(elapsedMS);
			if (maxWait != kInfiniteTimeout) {
				timeout = Math.Min(timeout, maxWait);
			}

			int handleIndex = WaitHandle.WaitAny(handles, (int) timeout);
			if (0 <= handleIndex && handleIndex < handleCount) {
				PollSinkCallback pollSinkCallback = handleSinks[handleIndex];
				finished = !pollSinkCallback.Sink.OnHandlePoll(pollSinkCallback.Cookie) || finished;
			}
			
			foreach (PollSinkCallback callback in msgSinks) { finished = !callback.Sink.OnMsgPoll(callback.Cookie) || finished; }

			foreach (PollPeriodicSinkCallback callback in periodicSinks) {
				if (callback.PeriodMS + callback.LastFiredTime > elapsedMS) { continue; }

				callback.LastFiredTime = (elapsedMS / callback.PeriodMS) * callback.PeriodMS;
				finished = !callback.Sink.OnPeriodicPoll(callback.Cookie, callback.LastFiredTime) || finished;
			}

			foreach (PollSinkCallback callback in loopSinks) { finished = !callback.Sink.OnLoopPoll(callback.Cookie) || finished; }
			
			return finished;
		}

		private long ComputeWaitTime(long elapsed) {
			const int kInfiniteTimeout = int.MaxValue;
			long waitTime = kInfiniteTimeout;
			int count = periodicSinks.Count;

			if (count > 0) {
				for (int i = 0; i < count; i++) {
					PollPeriodicSinkCallback callback = periodicSinks[i];
					long timeout = callback.PeriodMS + callback.LastFiredTime - elapsed;
					if (waitTime == kInfiniteTimeout || timeout < waitTime) {
						waitTime = Math.Max(timeout, 0);
					}         
				}
			}
			
			return waitTime;
		}

		protected class PollSinkCallback {
			public readonly IPollSink Sink;
			public readonly object Cookie;

			public PollSinkCallback(IPollSink sink, object cookie) {
				Sink = sink;
				Cookie = cookie;
			}
		};

		protected class PollPeriodicSinkCallback : PollSinkCallback {
			public readonly long PeriodMS;
			public long LastFiredTime;

			public PollPeriodicSinkCallback() : base(null, null) {
				PeriodMS = 0;
				LastFiredTime = 0;
			}

			public PollPeriodicSinkCallback(IPollSink sink, object cookie, int periodMS) : base(sink, cookie) {
				PeriodMS = periodMS;
				LastFiredTime = 0;
			}
		};
	}
}
