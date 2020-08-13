using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GGPort {
	public static class LogUtil {
		private static readonly string _filePath = $"log-{Platform.GetProcessID()}.log";

		public static event LogTextDelegate logEvent;

		private static long _start;
		private static FileStream _logFileStream;
		private static Queue<string> _queue = new Queue<string>();
		private static bool _isWriting;

		public static void Log(string msg) {
			Log(msg, false);
		}

		private static async void Log(string msg, bool overrideQueue) {
#if UNITY_EDITOR
			Debug.Log(msg);
#elif DEBUG || DEVELOPMENT_BUILD
			/*if (!Platform.GetConfigBool("ggpo.log") || Platform.GetConfigBool("ggpo.log.ignore")) {
				return;
			}

			if (isWriting && !overrideQueue) {
				_queue.Enqueue(msg);
				return;
			}

			FileMode fileMode = File.Exists(_filePath)
				? FileMode.Append
				: FileMode.Create;

			_logFileStream = File.Open(_filePath, fileMode);
			if (fileMode == FileMode.Create) { _logFileStream.Dispose(); }

			string toWrite = "";

			if (Platform.GetConfigBool("ggpo.log.timestamps")) {
				long t = 0;
				if (_start == 0) {
					_start = Platform.GetCurrentTimeMS();
				} else {
					t = Platform.GetCurrentTimeMS() - _start;
				}

				toWrite = $"[{t / 1000}.{t % 1000:000}]: ";
			}

			toWrite += msg;
			logEvent?.Invoke(toWrite);
			byte[] buffer = Encoding.UTF8.GetBytes(toWrite);
			isWriting = true;
			await _logFileStream.WriteAsync(buffer, 0, buffer.Length);
			_logFileStream.Dispose();
			ProcessDeferredMessages();
			isWriting = false;*/
#endif
		}

		private static void ProcessDeferredMessages() {
			if (_queue.Count == 0) { return; }

			Log(_queue.Dequeue(), true);
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

				msg +=
					$"{Environment.NewLine}{Environment.NewLine}\t\t{nameof(Platform.AssertFailed)} call {(callerLineNumber == -1 ? "In" : "@")} {fileName}";

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
			Log(
				$"{Environment.NewLine}{Environment.NewLine}"
				+ $"{exception}"
				+ $"{Environment.NewLine}{Environment.NewLine}"
			);
		}
	}
}