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

		public static void Assert(
			bool expression,
			string msg = "Assertion Failed.",
			[CallerFilePath] string callerFilePath = "",
			[CallerMemberName] string callerName = "",
			[CallerLineNumber] int callerLineNumber = -1
		) {
			do {
				if (!(expression)) {
					AssertFailedInternal(msg, callerFilePath, callerName, callerLineNumber);
				}
			} while (false);
		}

		public static void AssertFailed(
			Exception exception,
			[CallerFilePath] string callerFilePath = "",
			[CallerMemberName] string callerName = "",
			[CallerLineNumber] int callerLineNumber = -1
		) {
			Debug.LogException(exception);
#if !UNITY_EDITOR
			AssertFailedInternal(exception.ToString(), callerFilePath, callerName, callerLineNumber);
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
			AssertFailedInternal(msg, callerFilePath, callerName, callerLineNumber);
#else
			Debugger.Break();
#endif
		}

		private static void AssertFailedInternal(
			string msg,
			string callerFilePath,
			string callerName,
			int callerLineNumber
		) {
			if (!string.IsNullOrEmpty(callerFilePath)) {
				string[] callerFilePathSplit = callerFilePath.Split('/', '\\');
				string fileName = callerFilePathSplit[callerFilePathSplit.Length - 1];
				msg += $"{Environment.NewLine}\t\t{(callerLineNumber == -1 ? "In " : "@ ")} {fileName}";

				if (callerLineNumber != -1) {
					msg += $":{callerLineNumber}";
				}

				if (!string.IsNullOrEmpty(callerName)) {
					msg += $",{Environment.NewLine}\t\tMethod: {callerName}";
				}
			}

			LogUtil.Log($"{Environment.NewLine}{msg}");
			Debugger.Break();
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