namespace GGPort {
	// TODO this should really be an interface...
	// XXX need to go through all interfaces and see that the implementers are correctly overriding rather than just hiding
	// NOTE to that end, incorrect signatures will be a comp error for not implementing interface/abstract functions
	public abstract class GGPOSession {
		public virtual GGPOErrorCode DoPoll(int timeout) { return GGPOErrorCode.GGPO_OK; }
		public abstract GGPOErrorCode AddPlayer(ref GGPOPlayer player, ref GGPOPlayerHandle handle);
		public abstract GGPOErrorCode AddLocalInput(GGPOPlayerHandle player, object[] values, int size);
		public abstract GGPOErrorCode SyncInput(ref object[] values, int size, ref int? disconnect_flags);
		public virtual GGPOErrorCode IncrementFrame() { return GGPOErrorCode.GGPO_OK; }
		public virtual GGPOErrorCode Chat(string text) { return GGPOErrorCode.GGPO_OK; }
		public virtual GGPOErrorCode DisconnectPlayer(GGPOPlayerHandle handle) { return GGPOErrorCode.GGPO_OK; }

		public virtual GGPOErrorCode GetNetworkStats(out GGPONetworkStats stats, GGPOPlayerHandle handle) {
			stats = default;
			return GGPOErrorCode.GGPO_OK;
		}

		public virtual GGPOErrorCode Log(string msg) {
			LogUtil.Log(msg);
			return GGPOErrorCode.GGPO_OK;
		}

		public virtual GGPOErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public virtual GGPOErrorCode SetDisconnectTimeout(uint timeout) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public virtual GGPOErrorCode SetDisconnectNotifyStart(uint timeout) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}
	}
}