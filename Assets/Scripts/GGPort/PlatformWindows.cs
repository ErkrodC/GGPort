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

namespace GGPort {
	public static class Platform {
		private static readonly Dictionary<string, int> configIntDefaults = new Dictionary<string, int> {
			["ggpo.network.delay"] = 0,
			["ggpo.oop.percent"] = 0
		};
		
		private static readonly Dictionary<string, bool> configBoolDefaults = new Dictionary<string, bool> {
			["ggpo.log.timestamps"] = true,
			["ggpo.log"] = true,
			["ggpo.log.ignore"] = false
		};

		public static int GetProcessID() { return Process.GetCurrentProcess().Id; }
		
		public static void Assert(bool expression, string msg = "GGPO Assertion Failed.", [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerName = "", [CallerLineNumber] int callerLineNumber = -1) {
			do {
				if (!(expression)) {
					AssertFailedInternal(msg, callerFilePath, callerName, callerLineNumber);
				}
			} while (false);
		}

		public static void AssertFailed(string msg, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerName = "", [CallerLineNumber] int callerLineNumber = -1) {
			AssertFailedInternal(msg, callerFilePath, callerName, callerLineNumber);
		}

		private static void AssertFailedInternal(string msg, string callerFilePath, string callerName, int callerLineNumber) {
			if (!string.IsNullOrEmpty(callerFilePath)) {
				string[] callerFilePathSplit = callerFilePath.Split('/', '\\');
				string fileName = callerFilePathSplit[callerFilePathSplit.Length - 1];
				msg += $"{Environment.NewLine}\tIn {fileName}";

				if (!string.IsNullOrEmpty(callerName)) {
					msg += $", {callerName}";

					if (callerLineNumber != -1) {
						msg += $":{callerLineNumber}";
					}
				}
			}
			
			LogUtil.Log(msg);
			Debugger.Break();
			Environment.Exit(0);
		}

		public static long GetCurrentTimeMS() {
			return DateTimeOffset.Now.ToUnixTimeMilliseconds();
		}

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