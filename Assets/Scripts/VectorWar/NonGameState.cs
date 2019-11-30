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
		public GGPOPlayerType type { get; set; }
		public GGPOPlayerHandle handle { get; set; }
		public PlayerConnectState state { get; set; }
		public int connect_progress { get; set; }
		public int disconnect_timeout { get; set; }
		public long disconnect_start { get; set; }
	};

	public class NonGameState {
		public const int MAX_PLAYERS = 64;

		public GGPOPlayerHandle local_player_handle;
		public PlayerConnectionInfo[] players = new PlayerConnectionInfo[MAX_PLAYERS];
		public int num_players;

		public ChecksumInfo now;
		public ChecksumInfo periodic;
		
		public struct ChecksumInfo {
			public int framenumber;
			public int checksum;
		};

		public void SetConnectState(GGPOPlayerHandle handle, PlayerConnectState state) {
			for (int i = 0; i < num_players; i++) {
				if (players[i].handle.HandleValue == handle.HandleValue) {
					players[i].connect_progress = 0;
					players[i].state = state;
					break;
				}
			}
		}

		public void SetConnectState(PlayerConnectState state) {
			for (int i = 0; i < num_players; i++) {
				players[i].state = state;
			}
		}

		public void SetDisconnectTimeout(GGPOPlayerHandle handle, long when, int timeout) {
			for (int i = 0; i < num_players; i++) {
				if (players[i].handle.HandleValue == handle.HandleValue) {
					players[i].disconnect_start = when;
					players[i].disconnect_timeout = timeout;
					players[i].state = PlayerConnectState.Disconnecting;
					break;
				}
			}
		}

		public void UpdateConnectProgress(GGPOPlayerHandle handle, int progress) {
			for (int i = 0; i < num_players; i++) {
				if (players[i].handle.HandleValue == handle.HandleValue) {
					players[i].connect_progress = progress;
					break;
				}
			}
		}
	}
}