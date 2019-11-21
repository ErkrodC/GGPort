namespace GGPort {
	// TODO rename to GGPOSession
	public abstract class GGPOSession {
		public virtual GGPOErrorCode DoPoll(int timeout) { return GGPOErrorCode.GGPO_OK; }
		public abstract GGPOErrorCode AddPlayer(ref GGPOPlayer player, ref GGPOPlayerHandle handle);
		public abstract GGPOErrorCode AddLocalInput(GGPOPlayerHandle player, object[] values, int size);
		public abstract GGPOErrorCode SyncInput(ref object[] values, int size, ref int? disconnect_flags);
		public virtual GGPOErrorCode IncrementFrame() { return GGPOErrorCode.GGPO_OK; }
		public virtual GGPOErrorCode Chat(string text) { return GGPOErrorCode.GGPO_OK; }
		public virtual GGPOErrorCode DisconnectPlayer(GGPOPlayerHandle handle) { return GGPOErrorCode.GGPO_OK; }

		public virtual GGPOErrorCode GetNetworkStats(GGPONetworkStats stats, GGPOPlayerHandle handle) {
			return GGPOErrorCode.GGPO_OK;
		}

		public virtual GGPOErrorCode Logv(string fmt, params object[] args) {
			LogUtil.Logv(fmt, args);
			return GGPOErrorCode.GGPO_OK;
		}

		public virtual GGPOErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public virtual GGPOErrorCode SetDisconnectTimeout(int timeout) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}

		public virtual GGPOErrorCode SetDisconnectNotifyStart(int timeout) {
			return GGPOErrorCode.GGPO_ERRORCODE_UNSUPPORTED;
		}
	}
}