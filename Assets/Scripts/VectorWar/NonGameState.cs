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
		Disconnecting
	};

	public struct PlayerConnectionInfo {
		public PlayerType type { get; set; }
		public PlayerHandle handle { get; set; }
		public PlayerConnectState state { get; set; }
		public int connectProgress { get; set; }
		public int disconnectTimeout { get; set; }
		public long disconnectStart { get; set; }
	};

	public class NonGameState {
		private const int _MAX_PLAYERS = 64;

		public PlayerHandle localPlayerHandle;
		public readonly PlayerConnectionInfo[] players;
		public readonly int numPlayers;

		public ChecksumInfo now;
		public ChecksumInfo periodic;

		public NonGameState(int numPlayers) {
			this.numPlayers = numPlayers;
			players = new PlayerConnectionInfo[_MAX_PLAYERS];
		}

		public void SetConnectState(PlayerHandle handle, PlayerConnectState state) {
			for (int i = 0; i < numPlayers; i++) {
				if (players[i].handle.handleValue != handle.handleValue) { continue; }

				players[i].connectProgress = 0;
				players[i].state = state;
				break;
			}
		}

		public void SetConnectState(PlayerConnectState state) {
			for (int i = 0; i < numPlayers; i++) {
				players[i].state = state;
			}
		}

		public void SetDisconnectTimeout(PlayerHandle handle, long when, int timeout) {
			for (int i = 0; i < numPlayers; i++) {
				if (players[i].handle.handleValue != handle.handleValue) { continue; }

				players[i].disconnectStart = when;
				players[i].disconnectTimeout = timeout;
				players[i].state = PlayerConnectState.Disconnecting;
				break;
			}
		}

		public void UpdateConnectProgress(PlayerHandle handle, int progress) {
			for (int i = 0; i < numPlayers; i++) {
				if (players[i].handle.handleValue != handle.handleValue) { continue; }

				players[i].connectProgress = progress;
				break;
			}
		}

		public struct ChecksumInfo {
			public int frameNumber;
			public int checksum;
		};
	}
}