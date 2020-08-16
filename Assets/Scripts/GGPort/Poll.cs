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
		private const int _MAX_POLLABLE_HANDLES = 64;

		private long _startTime;
		private readonly int _handleCount;
		private readonly WaitHandle[] _handles; // TODO better as a list? and using .Count over _handle_count
		private readonly PollSinkCallback[] _handleSinks;

		private readonly List<PollSinkCallback> _messageSinks;
		private readonly List<PollSinkCallback> _loopSinks;
		private readonly List<PollPeriodicSinkCallback> _periodicSinks;

		public Poll() {
			_handleCount = 0;
			_startTime = 0;

			_handles = new WaitHandle[_MAX_POLLABLE_HANDLES];
			for (_handleCount = 0; _handleCount < _handles.Length; _handleCount++) {
				_handles[_handleCount] = new EventWaitHandle(false, EventResetMode.ManualReset);
			}

			_handleSinks = new PollSinkCallback[_MAX_POLLABLE_HANDLES];

			_messageSinks = new List<PollSinkCallback>(16);
			_loopSinks = new List<PollSinkCallback>(16);
			_periodicSinks = new List<PollPeriodicSinkCallback>(16);
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
			_loopSinks.Add(new PollSinkCallback(sink, cookie));
		}

		/*public void Run() {
			while (Pump(100)) { }
		}*/

		public bool Pump(long timeout) {
			const int _INFINITE_TIMEOUT = -1;
			bool finished = false;

			if (_startTime == 0) { _startTime = Platform.GetCurrentTimeMS(); }

			long elapsedMS = Platform.GetCurrentTimeMS() - _startTime;
			long maxWait = _INFINITE_TIMEOUT;

			foreach (PollPeriodicSinkCallback callback in _periodicSinks) {
				long callbackCooldownRemaining = callback.periodMS + callback.lastFiredTime - elapsedMS;
				if (maxWait == _INFINITE_TIMEOUT || callbackCooldownRemaining < maxWait) {
					maxWait = Math.Max(callbackCooldownRemaining, 0);
				}
			}

			if (maxWait != _INFINITE_TIMEOUT) {
				timeout = Math.Min(timeout, maxWait);
			}

			int handleIndex = WaitHandle.WaitAny(_handles, (int) timeout);
			if (0 <= handleIndex && handleIndex < _handleCount) {
				PollSinkCallback pollSinkCallback = _handleSinks[handleIndex];
				finished = !pollSinkCallback.pollSink.OnHandlePoll(pollSinkCallback.cookie);
			}

			foreach (PollSinkCallback callback in _messageSinks) {
				finished = !callback.pollSink.OnMsgPoll(callback.cookie) || finished;
			}

			foreach (PollPeriodicSinkCallback callback in _periodicSinks) {
				if (callback.periodMS + callback.lastFiredTime > elapsedMS) { continue; }

				callback.lastFiredTime = elapsedMS - elapsedMS % 2;
				finished = !callback.pollSink.OnPeriodicPoll(callback.cookie, callback.lastFiredTime) || finished;
			}

			foreach (PollSinkCallback callback in _loopSinks) {
				finished = !callback.pollSink.OnLoopPoll(callback.cookie) || finished;
			}

			return finished;
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

			public PollPeriodicSinkCallback(IPollSink pollSink = null, object cookie = null, int periodMS = 0)
				: base(pollSink, cookie) {
				this.periodMS = periodMS;
			}
		};
	}
}