using System;
using System.IO;

namespace GGPort {
	public static class LogUtil {
		public static FileStream logfile = null;
		public static string logbuf = null;
		private static long start = 0;

		public static void Log(string msg) {
			if (!Platform.GetConfigBool("ggpo.log") || Platform.GetConfigBool("ggpo.log.ignore")) {
				return;
			}
			
			if (logfile == null) {
				string filename = $"log-{Platform.GetProcessID()}.log";
				logfile = File.OpenWrite(filename);
			}
			
			Log(logfile, msg);
		}

		public static void Log(FileStream fp, string msg) {
			char[] msgCharArray;
			byte[] buf;
			
			if (Platform.GetConfigBool("ggpo.log.timestamps")) {
				long t = 0;
				if (start == 0) {
					start = Platform.GetCurrentTimeMS();
				} else {
					t = Platform.GetCurrentTimeMS() - start;
				}
				
				msgCharArray = $"{t / 1000}.{t % 1000:000} : ".ToCharArray();
				buf = new byte[msgCharArray.Length];
				Buffer.BlockCopy(msgCharArray, 0, buf, 0, msgCharArray.Length);
				fp.Write(buf, 0, msgCharArray.Length);
			}

			msgCharArray = msg.ToCharArray();
			buf = new byte[msgCharArray.Length];
			Buffer.BlockCopy(msgCharArray, 0, buf, 0, msgCharArray.Length);

			fp.Write(buf, 0, msgCharArray.Length);
			fp.Flush();

			logbuf = msg;
		}

		public static void LogFlush() {
			logfile?.Flush();
		}
		
		public static void LogFlushOnLog(bool flush) { }
	}
}