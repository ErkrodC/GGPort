using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GGPort {
	public static class LogUtil {
		private static readonly string ms_FILE_PATH = $"log-{Platform.GetProcessID()}.log";
		
		public static event Session.LogTextDelegate logEvent;

		private static long ms_start;
		private static FileStream ms_logFileStream;
		private static Queue<string> ms_queue = new Queue<string>();
		private static bool ms_isWriting;

		public static void Log(string msg) => Log(msg, false);
		private static async void Log(string msg, bool overrideQueue) {
#if UNITY_EDITOR
			Debug.Log(msg);
#elif DEBUG || DEVELOPMENT_BUILD
			/*if (!Platform.GetConfigBool("ggpo.log") || Platform.GetConfigBool("ggpo.log.ignore")) {
				return;
			}

			if (ms_isWriting && !overrideQueue) {
				ms_queue.Enqueue(msg);
				return;
			}

			FileMode fileMode = File.Exists(ms_FILE_PATH)
				? FileMode.Append
				: FileMode.Create;

			ms_logFileStream = File.Open(ms_FILE_PATH, fileMode);
			if (fileMode == FileMode.Create) { ms_logFileStream.Dispose(); }

			string toWrite = "";

			if (Platform.GetConfigBool("ggpo.log.timestamps")) {
				long t = 0;
				if (ms_start == 0) {
					ms_start = Platform.GetCurrentTimeMS();
				} else {
					t = Platform.GetCurrentTimeMS() - ms_start;
				}

				toWrite = $"[{t / 1000}.{t % 1000:000}]: ";
			}

			toWrite += msg;
			logEvent?.Invoke(toWrite);
			byte[] buffer = Encoding.UTF8.GetBytes(toWrite);
			ms_isWriting = true;
			await ms_logFileStream.WriteAsync(buffer, 0, buffer.Length);
			ms_logFileStream.Dispose();
			ProcessDeferredMessages();
			ms_isWriting = false;*/
#endif
		}

		private static void ProcessDeferredMessages() {
			if (ms_queue.Count == 0) { return; }

			Log(ms_queue.Dequeue(), true);
		}

		public static void LogFailedAssertToFile(
			string msg,
			string callerFilePath,
			string callerName,
			int callerLineNumber
		) {
			if (!string.IsNullOrEmpty(callerFilePath)) {
				string[] callerFilePathSplit = callerFilePath.Split('/', '\\');
				string fileName = callerFilePathSplit[callerFilePathSplit.Length - 1];
				
				msg += $"{Environment.NewLine}{Environment.NewLine}\t\t{nameof(Platform.AssertFailed)} call {(callerLineNumber == -1 ? "In" : "@")} {fileName}";

				if (callerLineNumber != -1) {
					msg += $":{callerLineNumber}";
				}

				if (!string.IsNullOrEmpty(callerName)) {
					msg += $",{Environment.NewLine}\t\tCaller: {callerName}{Environment.NewLine}{Environment.NewLine}";
				}
			}

			Log($"{Environment.NewLine}{msg}");
		}

		public static void LogException(Exception exception) {
			Log($"{Environment.NewLine}{Environment.NewLine}"
				+ $"{exception}"
				+ $"{Environment.NewLine}{Environment.NewLine}");
		}
	}
}