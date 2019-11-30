using System;

namespace GGPort {
	// TODO this should really be an interface...
	// XXX need to go through all interfaces and see that the implementers are correctly overriding rather than just hiding
	// NOTE to that end, incorrect signatures will be a comp error for not implementing interface/abstract functions
	public abstract class GGPOSession {
		public virtual ErrorCode DoPoll(int timeout) { return ErrorCode.Success; }
		public abstract ErrorCode AddPlayer(ref GGPOPlayer player, out GGPOPlayerHandle handle);
		public abstract ErrorCode AddLocalInput(GGPOPlayerHandle player, byte[] value, int size);
		public abstract ErrorCode SyncInput(ref Array values, int size, ref int? disconnect_flags);
		public virtual ErrorCode IncrementFrame() { return ErrorCode.Success; }
		public virtual ErrorCode Chat(string text) { return ErrorCode.Success; }
		public virtual ErrorCode DisconnectPlayer(GGPOPlayerHandle handle) { return ErrorCode.Success; }

		public virtual ErrorCode GetNetworkStats(out GGPONetworkStats stats, GGPOPlayerHandle handle) {
			stats = default;
			return ErrorCode.Success;
		}

		public virtual ErrorCode Log(string msg) {
			LogUtil.Log(msg);
			return ErrorCode.Success;
		}

		public virtual ErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay) {
			return ErrorCode.Unsupported;
		}

		public virtual ErrorCode SetDisconnectTimeout(uint timeout) {
			return ErrorCode.Unsupported;
		}

		public virtual ErrorCode SetDisconnectNotifyStart(uint timeout) {
			return ErrorCode.Unsupported;
		}
	}
}