using System;
using System.Net;

namespace GGPort {
	public static class GGPortMain {
		public static Random Random;
		
		public static bool Main() {
			Random = new Random((int) (Platform.GetCurrentTimeMS() + Platform.GetProcessID()));
			return true;
		}
		
		/*
		* ggpo_start_session --
		*
		* Used to being a new GGPO.net session.  The ggpo object returned by ggpo_start_session
		* uniquely identifies the state for this session and should be passed to all other
		* functions.
		*
		* session - An out parameter to the new ggpo session object.
		*
		* cb - A GGPOSessionCallbacks structure which contains the callbacks you implement
		* to help GGPO.net synchronize the two games.  You must implement all functions in
		* cb, even if they do nothing but 'return true';
		*
		* game - The name of the game.  This is used internally for GGPO for logging purposes only.
		*
		* num_players - The number of players which will be in this game.  The number of players
		* per session is fixed.  If you need to change the number of players or any player
		* disconnects, you must start a new session.
		*
		* input_size - The size of the game inputs which will be passsed to ggpo_add_local_input.
		*
		* local_port - The port GGPO should bind to for UDP traffic.
		*/
		public static GGPOErrorCode ggpo_start_session(ref GGPOSession session, ref GGPOSessionCallbacks cb, string game, int num_players, int input_size, ushort localport) {
			session = new Peer2PeerBackend(ref cb, game, localport, num_players, input_size);
			return GGPOErrorCode.GGPO_OK;
		}


		/*
		* ggpo_add_player --
		*
		* Must be called for each player in the session (e.g. in a 3 player session, must
		* be called 3 times).
		*
		* player - A GGPOPlayer struct used to describe the player.
		*
		* handle - An out parameter to a handle used to identify this player in the future.
		* (e.g. in the on_event callbacks).
		*/
		public static GGPOErrorCode ggpo_add_player(ref GGPOSession ggpo, ref GGPOPlayer player, out GGPOPlayerHandle handle) {
			handle = new GGPOPlayerHandle(-1);
			return ggpo?.AddPlayer(ref player, out handle) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_start_synctest --
		*
		* Used to being a new GGPO.net sync test session.  During a sync test, every
		* frame of execution is run twice: once in prediction mode and once again to
		* verify the result of the prediction.  If the checksums of your save states
		* do not match, the test is aborted.
		*
		* cb - A GGPOSessionCallbacks structure which contains the callbacks you implement
		* to help GGPO.net synchronize the two games.  You must implement all functions in
		* cb, even if they do nothing but 'return true';
		*
		* game - The name of the game.  This is used internally for GGPO for logging purposes only.
		*
		* num_players - The number of players which will be in this game.  The number of players
		* per session is fixed.  If you need to change the number of players or any player
		* disconnects, you must start a new session.
		*
		* input_size - The size of the game inputs which will be passsed to ggpo_add_local_input.
		*
		* frames - The number of frames to run before verifying the prediction.  The
		* recommended value is 1.
		*
		*/
		public static GGPOErrorCode ggpo_start_synctest(
			ref GGPOSession ggpo,
			ref GGPOSessionCallbacks cb,
			string game,
			int num_players,
			int input_size,
			int frames
		) {
			ggpo = new SyncTestBackend(ref cb, game, frames, num_players);
			return GGPOErrorCode.GGPO_OK;
		}

		/*
		* ggpo_start_spectating --
		*
		* Start a spectator session.
		*
		* cb - A GGPOSessionCallbacks structure which contains the callbacks you implement
		* to help GGPO.net synchronize the two games.  You must implement all functions in
		* cb, even if they do nothing but 'return true';
		*
		* game - The name of the game.  This is used internally for GGPO for logging purposes only.
		*
		* num_players - The number of players which will be in this game.  The number of players
		* per session is fixed.  If you need to change the number of players or any player
		* disconnects, you must start a new session.
		*
		* input_size - The size of the game inputs which will be passsed to ggpo_add_local_input.
		*
		* local_port - The port GGPO should bind to for UDP traffic.
		*
		* host_ip - The IP address of the host who will serve you the inputs for the game.  Any
		* player partcipating in the session can serve as a host.
		*
		* host_port - The port of the session on the host
		*/
		public static GGPOErrorCode ggpo_start_spectating(
			ref GGPOSession session,
			ref GGPOSessionCallbacks cb,
			string game,
			int num_players,
			int input_size,
			ushort local_port,
			IPEndPoint hostEndPoint
		) {
			return GGPOErrorCode.GGPO_OK;
		}

		/*
		* ggpo_close_session --
		* Used to close a session.  You must call ggpo_close_session to
		* free the resources allocated in ggpo_start_session.
		*/
		public static GGPOErrorCode ggpo_close_session(ref GGPOSession ggpo) {
			if (ggpo == null) {
				return GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
			}

			ggpo = null;
			return GGPOErrorCode.GGPO_OK;
		}

		/*
		* ggpo_set_frame_delay --
		*
		* Change the amount of frames ggpo will delay local input.  Must be called
		* before the first call to ggpo_synchronize_input.
		*/
		public static GGPOErrorCode ggpo_set_frame_delay(
			ref GGPOSession ggpo,
			GGPOPlayerHandle player,
			int frame_delay
		) {
			return ggpo?.SetFrameDelay(player, frame_delay) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_idle --
		* Should be called periodically by your application to give GGPO.net
		* a chance to do some work.  Most packet transmissions and rollbacks occur
		* in ggpo_idle.
		*
		* timeout - The amount of time GGPO.net is allowed to spend in this function,
		* in milliseconds.
		*/
		public static GGPOErrorCode ggpo_idle(ref GGPOSession ggpo, int timeout) {
			return ggpo?.DoPoll(timeout) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_add_local_input --
		*
		* Used to notify GGPO.net of inputs that should be trasmitted to remote
		* players.  ggpo_add_local_input must be called once every frame for
		* all player of type GGPO_PLAYERTYPE_LOCAL.
		*
		* player - The player handle returned for this player when you called
		* ggpo_add_local_player.
		*
		* values - The controller inputs for this player.
		*
		* size - The size of the controller inputs.  This must be exactly equal to the
		* size passed into ggpo_start_session.
		*/
		public static GGPOErrorCode ggpo_add_local_input(
			ref GGPOSession ggpo,
			GGPOPlayerHandle player,
			object value,
			int size
		) {
			return ggpo?.AddLocalInput(player, value, size) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_synchronize_input --
		*
		* You should call ggpo_synchronize_input before every frame of execution,
		* including those frames which happen during rollback.
		*
		* values - When the function returns, the values parameter will contain
		* inputs for this frame for all players.  The values array must be at
		* least (size * players) large.
		*
		* size - The size of the values array.
		*
		* disconnect_flags - Indicated whether the input in slot (1 << flag) is
		* valid.  If a player has disconnected, the input in the values array for
		* that player will be zeroed and the i-th flag will be set.  For example,
		* if only player 3 has disconnected, disconnect flags will be 8 (i.e. 1 << 3).
		*/
		public static GGPOErrorCode ggpo_synchronize_input(
			ref GGPOSession ggpo,
			Array values,
			int size,
			int? disconnect_flags
		) {
			return ggpo?.SyncInput(ref values, size, ref disconnect_flags) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_disconnect_player --
		*
		* Disconnects a remote player from a game.  Will return GGPO_ERRORCODE_PLAYER_DISCONNECTED
		* if you try to disconnect a player who has already been disconnected.
		*/
		public static GGPOErrorCode ggpo_disconnect_player(ref GGPOSession ggpo, GGPOPlayerHandle player) {
			return ggpo?.DisconnectPlayer(player) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_advance_frame --
		*
		* You should call ggpo_advance_frame to notify GGPO.net that you have
		* advanced your gamestate by a single frame.  You should call this everytime
		* you advance the gamestate by a frame, even during rollbacks.  GGPO.net
		* may call your save_state callback before this function returns.
		*/
		public static GGPOErrorCode ggpo_advance_frame(ref GGPOSession ggpo) {
			return ggpo?.IncrementFrame() ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_get_network_stats --
		*
		* Used to fetch some statistics about the quality of the network connection.
		*
		* player - The player handle returned from the ggpo_add_player function you used
		* to add the remote player.
		*
		* stats - Out parameter to the network statistics.
		*/
		public static GGPOErrorCode ggpo_get_network_stats(
			ref GGPOSession ggpo,
			GGPOPlayerHandle player,
			out GGPONetworkStats stats
		) {
			stats = default;
			return ggpo?.GetNetworkStats(out stats, player) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_set_disconnect_timeout --
		*
		* Sets the disconnect timeout.  The session will automatically disconnect
		* from a remote peer if it has not received a packet in the timeout window.
		* You will be notified of the disconnect via a GGPO_EVENTCODE_DISCONNECTED_FROM_PEER
		* event.
		*
		* Setting a timeout value of 0 will disable automatic disconnects.
		*
		* timeout - The time in milliseconds to wait before disconnecting a peer.
		*/
		public static GGPOErrorCode ggpo_set_disconnect_timeout(ref GGPOSession ggpo, uint timeout) {
			return ggpo?.SetDisconnectTimeout(timeout) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_set_disconnect_notify_start --
		*
		* The time to wait before the first GGPO_EVENTCODE_NETWORK_INTERRUPTED timeout
		* will be sent.
		*
		* timeout - The amount of time which needs to elapse without receiving a packet
		*           before the GGPO_EVENTCODE_NETWORK_INTERRUPTED event is sent.
		*/
		public static GGPOErrorCode ggpo_set_disconnect_notify_start(ref GGPOSession ggpo, uint timeout) {
			return ggpo?.SetDisconnectNotifyStart(timeout) ?? GGPOErrorCode.GGPO_ERRORCODE_INVALID_SESSION;
		}

		/*
		* ggpo_log --
		*
		* Used to write to the ggpo.net log.  In the current versions of the
		* SDK, a log file is only generated if the "quark.log" environment
		* variable is set to 1.  This will change in future versions of the
		* SDK.
		*/
		public static void ggpo_log(ref GGPOSession ggpo, string msg) {
			ggpo?.Log(msg);
		}
	}
}