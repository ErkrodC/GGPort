/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Debug = UnityEngine.Debug;

namespace GGPort {
	public static class Platform {
		private static readonly Dictionary<string, int> _configIntDefaults = new Dictionary<string, int> {
			["ggpo.network.delay"] = 0,
			["ggpo.oop.percent"] = 0
		};

		private static readonly Dictionary<string, bool> _configBoolDefaults = new Dictionary<string, bool> {
			["ggpo.log.timestamps"] = true,
			["ggpo.log"] = true,
			["ggpo.log.ignore"] = false
		};

		public static int GetProcessID() {
			return Process.GetCurrentProcess().Id;
		}

		public static void Assert(
			bool expression,
			string msg = "Assertion Failed.",
			[CallerFilePath] string callerFilePath = "",
			[CallerMemberName] string callerName = "",
			[CallerLineNumber] int callerLineNumber = -1
		) {
			do {
				if (!expression) {
					LogUtil.LogFailedAssertToFile(msg, callerFilePath, callerName, callerLineNumber);
					Debugger.Break();
				}
			} while (false);
		}

		public static void AssertFailed(Exception exception) {
			Debug.LogException(exception);
#if !UNITY_EDITOR
			LogUtil.LogException(exception);
#else
			Debugger.Break();
#endif
		}

		public static void AssertFailed(
			string msg,
			[CallerFilePath] string callerFilePath = "",
			[CallerMemberName] string callerName = "",
			[CallerLineNumber] int callerLineNumber = -1
		) {
			Debug.LogError(msg);
#if !UNITY_EDITOR
			LogUtil.LogFailedAssertToFile(msg, callerFilePath, callerName, callerLineNumber);
#else
			Debugger.Break();
#endif
		}

		public static long GetCurrentTimeMS() {
			return DateTimeOffset.Now.ToUnixTimeMilliseconds();
		}

		public static int GetConfigInt(string name) {
			if (int.TryParse(Environment.GetEnvironmentVariable(name), out int parsedEnvVarValue)) {
				return parsedEnvVarValue;
			}

			return _configIntDefaults.TryGetValue(name, out int value)
				? value
				: 0;
		}

		public static bool GetConfigBool(string name) {
			if (int.TryParse(Environment.GetEnvironmentVariable(name), out int parsedEnvVarValue)) {
				return parsedEnvVarValue != 0;
			}

			return _configBoolDefaults.TryGetValue(name, out bool value) && value;
		}
	}
}