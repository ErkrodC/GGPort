/*
 * nongamestate.h --
 *
 * These are other pieces of information not related to the state
 * of the game which are useful to carry around.  They are not
 * included in the GameState class because they specifically
 * should not be rolled back.
 */

using GGPort;

namespace VectorWar {
	public enum PlayerConnectState {
		Connecting = 0,
		Synchronizing,
		Running,
		Disconnected,
		Disconnecting,
	};

	public struct PlayerConnectionInfo {
		public PlayerType Type { get; set; }
		public PlayerHandle Handle { get; set; }
		public PlayerConnectState State { get; set; }
		public int ConnectProgress { get; set; }
		public int DisconnectTimeout { get; set; }
		public long DisconnectStart { get; set; }
	};

	public class NonGameState {
		private const int kMaxPlayers = 64;

		public PlayerHandle LocalPlayerHandle;
		public readonly PlayerConnectionInfo[] Players;
		public int NumPlayers;

		public ChecksumInfo Now;
		public ChecksumInfo Periodic;

		public NonGameState() {
			Players = new PlayerConnectionInfo[kMaxPlayers];
		}

		public void SetConnectState(PlayerHandle handle, PlayerConnectState state) {
			for (int i = 0; i < NumPlayers; i++) {
				if (Players[i].Handle.HandleValue == handle.HandleValue) {
					Players[i].ConnectProgress = 0;
					Players[i].State = state;
					break;
				}
			}
		}

		public void SetConnectState(PlayerConnectState state) {
			for (int i = 0; i < NumPlayers; i++) {
				Players[i].State = state;
			}
		}

		public void SetDisconnectTimeout(PlayerHandle handle, long when, int timeout) {
			for (int i = 0; i < NumPlayers; i++) {
				if (Players[i].Handle.HandleValue == handle.HandleValue) {
					Players[i].DisconnectStart = when;
					Players[i].DisconnectTimeout = timeout;
					Players[i].State = PlayerConnectState.Disconnecting;
					break;
				}
			}
		}

		public void UpdateConnectProgress(PlayerHandle handle, int progress) {
			for (int i = 0; i < NumPlayers; i++) {
				if (Players[i].Handle.HandleValue == handle.HandleValue) {
					Players[i].ConnectProgress = progress;
					break;
				}
			}
		}
		
		public struct ChecksumInfo {
			public int FrameNumber;
			public int Checksum;
		};
	}
}