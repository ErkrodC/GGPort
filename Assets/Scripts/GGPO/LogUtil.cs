using System;
using System.IO;

namespace GGPort {
	public static class LogUtil {
		public static FileStream logfile = null;
		public static string logbuf = null;
		
		// TODO these two are the same...
		public static void Log(string fmt, params object[] args) {
			Logv(fmt, args);
		}

		public static void Logv(string fmt, params object[] args) {
			if (!Platform.GetConfigBool("ggpo.log") || Platform.GetConfigBool("ggpo.log.ignore")) {
				return;
			}
			
			if (logfile == null) {
				string filename = $"log-{Platform.GetProcessID()}.log";
				logfile = File.OpenWrite(filename);
			}
			
			Logv(logfile, fmt, args);
		}

		private static long start = 0;
		public static void Logv(FileStream fp, string fmt, params object[] args) {
			char[] msg;
			byte[] buf;
			
			if (Platform.GetConfigBool("ggpo.log.timestamps")) {
				//static int start = 0;
				long t = 0;
				if (start == 0) {
					start = Platform.GetCurrentTimeMS();
				} else {
					t = Platform.GetCurrentTimeMS() - start;
				}
				
				msg = $"{t / 1000}.{t % 1000:000} : ".ToCharArray();
				buf = new byte[msg.Length];
				Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);
				fp.Write(buf, 0, msg.Length);
			}

			msg = string.Format(fmt, args).ToCharArray();
			buf = new byte[msg.Length];
			Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);

			fp.Write(buf, 0, msg.Length);
			fp.Flush();

			logbuf = string.Format(fmt, args);
		}

		public static void LogFlush() {
			logfile?.Flush();
		}
		
		public static void LogFlushOnLog(bool flush) { }
	}
}