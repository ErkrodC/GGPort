/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GGPort {
	public static class Platform {
		private static readonly Dictionary<string, int> configIntVariables = new Dictionary<string, int> {
			["ggpo.network.delay"] = TODO,
			["ggpo.oop.percent"] = TODO
		};
		
		private static readonly Dictionary<string, bool> configBoolVariable = new Dictionary<string, bool> {
			["ggpo.log.timestamps"] = TODO,
			["ggpo.log"] = TODO,
			["ggpo.log.ignore"] = TODO
		};

		public static int GetProcessID() { return Process.GetCurrentProcess().Id; }

		public static void AssertFailed(string msg) { // NOTE LOG
			MessageBoxA(NULL, msg, "GGPO Assertion Failed", MB_OK | MB_ICONEXCLAMATION);
		}

		public static long GetCurrentTimeMS() { return DateTime.Now.ToFileTimeUtc(); }

		public static int GetConfigInt(string name) {
			return configIntVariables.TryGetValue(name, out int value) ? value : 0;
		}

		public static bool GetConfigBool(string name) {
			return configBoolVariable.TryGetValue(name, out bool value) && value;
		}
	}
}