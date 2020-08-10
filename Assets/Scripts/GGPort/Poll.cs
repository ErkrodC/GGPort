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
		private const int MAX_POLLABLE_HANDLES = 64;

		private long m_startTime;
		private readonly int m_handleCount;
		private readonly WaitHandle[] m_handles; // TODO better as a list? and using .Count over _handle_count
		private readonly PollSinkCallback[] m_handleSinks;

		private readonly List<PollSinkCallback> m_messageSinks;
		private readonly List<PollSinkCallback> m_loopSinks;
		private readonly List<PollPeriodicSinkCallback> m_periodicSinks;

		public Poll() {
			m_handleCount = 0;
			m_startTime = 0;

			m_handles = new WaitHandle[MAX_POLLABLE_HANDLES];
			for (m_handleCount = 0; m_handleCount < m_handles.Length; m_handleCount++) {
				m_handles[m_handleCount] = new EventWaitHandle(false, EventResetMode.ManualReset);
			}
			
			m_handleSinks = new PollSinkCallback[MAX_POLLABLE_HANDLES];
			
			m_messageSinks = new List<PollSinkCallback>(16);
			m_loopSinks = new List<PollSinkCallback>(16);
			m_periodicSinks = new List<PollPeriodicSinkCallback>(16);
		}

		/*public void RegisterHandle(ref IPollSink sink, ref EventWaitHandle eventWaitHandle, object cookie = null) {
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
		}*/

		public void RegisterLoop(IPollSink sink, object cookie = null) {
			m_loopSinks.Add(new PollSinkCallback(sink, cookie));
		}

		/*public void Run() {
			while (Pump(100)) { }
		}*/

		public bool Pump(long timeout) {
			const int INFINITE_TIMEOUT = -1;
			bool finished = false;

			if (m_startTime == 0) { m_startTime = Platform.GetCurrentTimeMS(); }
			
			long elapsedMS = Platform.GetCurrentTimeMS() - m_startTime;
			long maxWait = ComputeWaitTime(elapsedMS);
			if (maxWait != INFINITE_TIMEOUT) {
				timeout = Math.Min(timeout, maxWait);
			}

			int handleIndex = WaitHandle.WaitAny(m_handles, (int) timeout);
			if (0 <= handleIndex && handleIndex < m_handleCount) {
				PollSinkCallback pollSinkCallback = m_handleSinks[handleIndex];
				finished = !pollSinkCallback.pollSink.OnHandlePoll(pollSinkCallback.cookie);
			}
			
			foreach (PollSinkCallback callback in m_messageSinks) { finished = !callback.pollSink.OnMsgPoll(callback.cookie) || finished; }

			foreach (PollPeriodicSinkCallback callback in m_periodicSinks) {
				if (callback.periodMS + callback.lastFiredTime > elapsedMS) { continue; }

				callback.lastFiredTime = (elapsedMS / callback.periodMS) * callback.periodMS;
				finished = !callback.pollSink.OnPeriodicPoll(callback.cookie, callback.lastFiredTime) || finished;
			}

			foreach (PollSinkCallback callback in m_loopSinks) { finished = !callback.pollSink.OnLoopPoll(callback.cookie) || finished; }
			
			return finished;
		}

		private long ComputeWaitTime(long elapsed) {
			const int kInfiniteTimeout = int.MaxValue;
			long waitTime = kInfiniteTimeout;
			int count = m_periodicSinks.Count;

			if (count > 0) {
				for (int i = 0; i < count; i++) {
					PollPeriodicSinkCallback callback = m_periodicSinks[i];
					long timeout = callback.periodMS + callback.lastFiredTime - elapsed;
					if (waitTime == kInfiniteTimeout || timeout < waitTime) {
						waitTime = Math.Max(timeout, 0);
					}         
				}
			}
			
			return waitTime;
		}

		protected class PollSinkCallback {
			public readonly IPollSink pollSink;
			public readonly object cookie;

			public PollSinkCallback(IPollSink pollSink, object cookie) {
				this.pollSink = pollSink;
				this.cookie = cookie;
			}
		};

		protected class PollPeriodicSinkCallback : PollSinkCallback {
			public readonly long periodMS;
			public long lastFiredTime;

			public PollPeriodicSinkCallback(IPollSink pollSink = null, object cookie = null, int periodMS = 0) : base(pollSink, cookie) {
				this.periodMS = periodMS;
			}
		};
	}
}
