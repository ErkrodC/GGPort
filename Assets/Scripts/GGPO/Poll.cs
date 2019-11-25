/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Threading;

namespace GGPort {
	public interface IPollSink {
		bool OnHandlePoll(object TODO); // TODO
		bool OnMsgPoll(object TODO); // TODO
		bool OnPeriodicPoll(object TODO0, long TODO1); // TODO
		bool OnLoopPoll(object cookie); // TODO
	};

	public class Poll {
		public const int MAX_POLLABLE_HANDLES = 64;
		
		protected long _start_time;
		protected int _handle_count;
		protected EventWaitHandle[] _handles = new EventWaitHandle[MAX_POLLABLE_HANDLES]; // TODO better as a list? and using .Count over _handle_count
		protected PollSinkCb[] _handle_sinks = new PollSinkCb[MAX_POLLABLE_HANDLES];

		protected StaticBuffer<PollSinkCb> _msg_sinks;
		protected StaticBuffer<PollSinkCb> _loop_sinks;
		protected StaticBuffer<PollPeriodicSinkCb> _periodic_sinks;

		public Poll() {
			_handle_count = 0;
			_start_time = 0;
			_handles[_handle_count++] = new EventWaitHandle(false, EventResetMode.ManualReset);
			
			_msg_sinks = new StaticBuffer<PollSinkCb>(16);
			_loop_sinks = new StaticBuffer<PollSinkCb>(16);
			_periodic_sinks = new StaticBuffer<PollPeriodicSinkCb>(16);
		}

		public void RegisterHandle(ref IPollSink sink, ref EventWaitHandle h, object cookie = null) {
			if (_handle_count >= MAX_POLLABLE_HANDLES - 1) {
				throw new IndexOutOfRangeException();
			}

			_handles[_handle_count] = h;
			_handle_sinks[_handle_count] = new PollSinkCb(sink, cookie);
			_handle_count++;
		}

		public void RegisterMsgLoop(ref IPollSink sink, object cookie = null) {
			_msg_sinks.push_back(new PollSinkCb(sink, cookie));
		}

		public void RegisterPeriodic(ref IPollSink sink, int interval, object cookie = null) {
			_periodic_sinks.push_back(new PollPeriodicSinkCb(sink, cookie, interval));
		}

		public void RegisterLoop(IPollSink sink, object cookie = null) {
			_loop_sinks.push_back(new PollSinkCb(sink, cookie));
		}

		public void Run() {
			while (Pump(100)) { }
		}

		public bool Pump(long timeout) {
			const int infiniteTimeout = -1;
			int i, res;
			bool finished = false;

			if (_start_time == 0) {
				_start_time = Platform.GetCurrentTimeMS();
			}
			
			long elapsed = Platform.GetCurrentTimeMS() - _start_time;
			long maxWait = ComputeWaitTime(elapsed);
			if (maxWait != infiniteTimeout) {
				timeout = Math.Min(timeout, maxWait);
			}

			res = WaitHandle.WaitAny(_handles, (int) timeout);
			if (0 <= res && res < _handle_count) {
				finished = !_handle_sinks[res].sink.OnHandlePoll(_handle_sinks[res].cookie) || finished;
			}
			for (i = 0; i < _msg_sinks.size(); i++) {
				PollSinkCb cb = _msg_sinks[i];
				finished = !cb.sink.OnMsgPoll(cb.cookie) || finished;
			}

			for (i = 0; i < _periodic_sinks.size(); i++) {
				PollPeriodicSinkCb cb = _periodic_sinks[i];
				if (cb.interval + cb.last_fired <= elapsed) {
					cb.last_fired = (elapsed / cb.interval) * cb.interval;
					finished = !cb.sink.OnPeriodicPoll(cb.cookie, cb.last_fired) || finished;
				}
			}

			for (i = 0; i < _loop_sinks.size(); i++) {
				PollSinkCb cb = _loop_sinks[i];
				finished = !cb.sink.OnLoopPoll(cb.cookie) || finished;
			}
			return finished;
		}

		protected long ComputeWaitTime(long elapsed) {
			const int infiniteTimeout = -1;
			long waitTime = infiniteTimeout;
			int count = _periodic_sinks.size();

			if (count > 0) {
				for (int i = 0; i < count; i++) {
					PollPeriodicSinkCb cb = _periodic_sinks[i];
					long timeout = (cb.interval + cb.last_fired) - elapsed;
					if (waitTime == infiniteTimeout || (timeout < waitTime)) {
						waitTime = Math.Max(timeout, 0);
					}         
				}
			}
			return waitTime;
		}

		protected class PollSinkCb { // TODO this was struct, check for others w/o notes
			public IPollSink sink;
			public object cookie;

			public PollSinkCb(IPollSink s, object c) {
				sink = s;
				cookie = c;
			}
		};

		protected class PollPeriodicSinkCb : PollSinkCb { // TODO this was struct
			public long interval;
			public long last_fired;

			public PollPeriodicSinkCb() : base(null, null) {
				interval = 0;
				last_fired = 0;
			}

			public PollPeriodicSinkCb(IPollSink s, object c, int i) : base(s, c) {
				interval = i;
				last_fired = 0;
			}
		};
	}
}
