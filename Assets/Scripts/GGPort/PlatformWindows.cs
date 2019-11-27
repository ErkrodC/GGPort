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
		private static readonly Dictionary<string, int> configIntDefaults = new Dictionary<string, int> {
			["ggpo.network.delay"] = 0,
			["ggpo.oop.percent"] = 50
		};
		
		private static readonly Dictionary<string, bool> configBoolDefaults = new Dictionary<string, bool> {
			["ggpo.log.timestamps"] = true,
			["ggpo.log"] = true,
			["ggpo.log.ignore"] = false
		};

		public static int GetProcessID() { return Process.GetCurrentProcess().Id; }
		
		public static void ASSERT(bool expression, string msg = "") {
			do {
				if (!(expression)) {
					
					AssertFailed(msg);
					Environment.Exit(0);
				}
			} while (false);
		}

		public static void AssertFailed(string msg) {
			Console.WriteLine(msg);
			Debugger.Break();
			// TODO
			//MessageBoxA(NULL, msg, "GGPO Assertion Failed", MB_OK | MB_ICONEXCLAMATION);
		}

		public static long GetCurrentTimeMS() { return DateTime.Now.ToFileTimeUtc(); }

		public static int GetConfigInt(string name) {
			if (int.TryParse(Environment.GetEnvironmentVariable(name), out int parsedEnvVarValue)) {
				return parsedEnvVarValue;
			}
			
			return configIntDefaults.TryGetValue(name, out int value) ? value : 0;
		}

		public static bool GetConfigBool(string name) {
			if (int.TryParse(Environment.GetEnvironmentVariable(name), out int parsedEnvVarValue)) {
				return parsedEnvVarValue != 0;
			}
			
			return configBoolDefaults.TryGetValue(name, out bool value) && value;
		}
	}
}