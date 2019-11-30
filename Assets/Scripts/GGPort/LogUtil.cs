using System;
using System.IO;
using System.Text;

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
			string toWrite = "";

			if (Platform.GetConfigBool("ggpo.log.timestamps")) {
				long t = 0;
				if (start == 0) {
					start = Platform.GetCurrentTimeMS();
				} else {
					t = Platform.GetCurrentTimeMS() - start;
				}
				
				toWrite = $"[{t / 1000}.{t % 1000:000}] : ";
			}

			toWrite += msg;
			byte[] buffer = Encoding.UTF8.GetBytes(toWrite);

			fp.Write(buffer, 0, buffer.Length);
			fp.Flush();

			logbuf = msg;
		}

		public static void LogFlush() {
			logfile?.Flush();
		}
		
		public static void LogFlushOnLog(bool flush) { }
	}
}