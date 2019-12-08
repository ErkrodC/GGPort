using System.IO;
using System.Text;

namespace GGPort {
	public static class LogUtil {
		private static FileStream logfile;
		private static long start;
		public static event SessionCallbacks.LogDelegate LogCallback = delegate(string message) {  };

		public static void Log(string msg) {
			if (!Platform.GetConfigBool("ggpo.log") || Platform.GetConfigBool("ggpo.log.ignore")) {
				return;
			}
			
			if (logfile == null) {
				string filename = $"log-{Platform.GetProcessID()}.log";
				logfile = File.OpenWrite(filename);
			}
			
			LogToFile(logfile, msg);
		}

		private static void LogToFile(FileStream fp, string msg) {
			string toWrite = "";

			if (Platform.GetConfigBool("ggpo.log.timestamps")) {
				long t = 0;
				if (start == 0) {
					start = Platform.GetCurrentTimeMS();
				} else {
					t = Platform.GetCurrentTimeMS() - start;
				}
				
				toWrite = $"[{t / 1000}.{t % 1000:000}]: ";
			}

			toWrite += msg;
			LogCallback?.Invoke(toWrite);
			byte[] buffer = Encoding.UTF8.GetBytes(toWrite);

			fp.Write(buffer, 0, buffer.Length);
			fp.Flush();
		}
	}
}