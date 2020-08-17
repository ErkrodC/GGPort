/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System.Collections.Generic;

namespace GGPort {
	public interface IUpdateHandler {
		bool OnUpdate();
	};

	public class FrameUpdater {
		private readonly List<IUpdateHandler> _updateHandlers;

		public FrameUpdater() {
			_updateHandlers = new List<IUpdateHandler>(16);
		}

		public void Register(IUpdateHandler updateHandler) {
			_updateHandlers.Add(updateHandler);
		}

		public bool Update() {
			bool finished = false;

			foreach (IUpdateHandler callback in _updateHandlers) {
				finished = !callback.OnUpdate() || finished;
			}

			return finished;
		}
	}
}