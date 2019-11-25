using System.Net;
using System.Runtime.InteropServices;

namespace GGPort {
	public static class Globals { // TODO rename?
		public const int GGPO_MAX_PLAYERS = 4;
		public const int GGPO_MAX_PREDICTION_FRAMES = 8;
		public const int GGPO_MAX_SPECTATORS = 32;
		public const int GGPO_SPECTATOR_INPUT_INTERVAL = 4;

		public static bool GGPO_SUCCEEDED(GGPOErrorCode result) { // TODO rename
			return result == GGPOErrorCode.GGPO_ERRORCODE_SUCCESS;
		}
	}

	public struct GGPOPlayerHandle {
		public const int GGPO_INVALID_HANDLE = -1;
		
		public readonly int handleValue;

		public GGPOPlayerHandle(int handleValue) {
			this.handleValue = handleValue;
		}
	}

	public enum GGPOPlayerType {
		GGPO_PLAYERTYPE_LOCAL,
		GGPO_PLAYERTYPE_REMOTE,
		GGPO_PLAYERTYPE_SPECTATOR
	}

	/*
	* The GGPOPlayer structure used to describe players in ggpo_add_player
	*
	* size: Should be set to the sizeof(GGPOPlayer)
	*
	* type: One of the GGPOPlayerType values describing how inputs should be handled
	*       Local players must have their inputs updated every frame via
	*       ggpo_add_local_inputs.  Remote players values will come over the
	*       network.
	*
	* player_num: The player number.  Should be between 1 and the number of players
	*       In the game (e.g. in a 2 player game, either 1 or 2).
	*
	* If type == GGPO_PLAYERTYPE_REMOTE:
	* 
	* u.remote.ip_address:  The ip address of the ggpo session which will host this
	*       player.
	*
	* u.remote.port: The port where udp packets should be sent to reach this player.
	*       All the local inputs for this session will be sent to this player at
	*       ip_address:port.
	*
	*/

	public struct GGPOPlayer {
		public int size;
		public GGPOPlayerType type;
		public int player_num;
		public IPEndPoint remote;

		public int Size() {
			int siz = sizeof(int);
			siz += sizeof(GGPOPlayerType);
			siz += sizeof(int);
			siz += type == GGPOPlayerType.GGPO_PLAYERTYPE_LOCAL ? 0 : remote.Address.GetAddressBytes().Length;
			siz += sizeof(int);

			return siz;
		}

		public struct Local { }
	}

	public struct GGPOLocalEndpoint {
		public readonly int player_num;
	}

	// TODO clean up enum val names
	public enum GGPOErrorCode {
		GGPO_OK = 0,
		GGPO_ERRORCODE_SUCCESS = 0,
		GGPO_ERRORCODE_GENERAL_FAILURE = -1,
		GGPO_ERRORCODE_INVALID_SESSION = 1,
		GGPO_ERRORCODE_INVALID_PLAYER_HANDLE = 2,
		GGPO_ERRORCODE_PLAYER_OUT_OF_RANGE = 3,
		GGPO_ERRORCODE_PREDICTION_THRESHOLD = 4,
		GGPO_ERRORCODE_UNSUPPORTED = 5,
		GGPO_ERRORCODE_NOT_SYNCHRONIZED = 6,
		GGPO_ERRORCODE_IN_ROLLBACK = 7,
		GGPO_ERRORCODE_INPUT_DROPPED = 8,
		GGPO_ERRORCODE_PLAYER_DISCONNECTED = 9,
		GGPO_ERRORCODE_TOO_MANY_SPECTATORS = 10,
		GGPO_ERRORCODE_INVALID_REQUEST = 11
	}

	/*
	* The GGPOEventCode enumeration describes what type of event just happened.
	*
	* GGPO_EVENTCODE_CONNECTED_TO_PEER - Handshake with the game running on the
	* other side of the network has been completed.
	* 
	* GGPO_EVENTCODE_SYNCHRONIZING_WITH_PEER - Beginning the synchronization
	* process with the client on the other end of the networking.  The count
	* and total fields in the u.synchronizing struct of the GGPOEvent
	* object indicate progress.
	*
	* GGPO_EVENTCODE_SYNCHRONIZED_WITH_PEER - The synchronziation with this
	* peer has finished.
	*
	* GGPO_EVENTCODE_RUNNING - All the clients have synchronized.  You may begin
	* sending inputs with ggpo_synchronize_inputs.
	*
	* GGPO_EVENTCODE_DISCONNECTED_FROM_PEER - The network connection on 
	* the other end of the network has closed.
	*
	* GGPO_EVENTCODE_TIMESYNC - The time synchronziation code has determined
	* that this client is too far ahead of the other one and should slow
	* down to ensure fairness.  The u.timesync.frames_ahead parameter in
	* the GGPOEvent object indicates how many frames the client is.
	*
	*/
	public enum GGPOEventCode {
		GGPO_EVENTCODE_CONNECTED_TO_PEER = 1000,
		GGPO_EVENTCODE_SYNCHRONIZING_WITH_PEER = 1001,
		GGPO_EVENTCODE_SYNCHRONIZED_WITH_PEER = 1002,
		GGPO_EVENTCODE_RUNNING = 1003,
		GGPO_EVENTCODE_DISCONNECTED_FROM_PEER = 1004,
		GGPO_EVENTCODE_TIMESYNC = 1005,
		GGPO_EVENTCODE_CONNECTION_INTERRUPTED = 1006,
		GGPO_EVENTCODE_CONNECTION_RESUMED = 1007
	}

	/*
	* The GGPOEvent structure contains an asynchronous event notification sent
	* by the on_event callback.  See GGPOEventCode, above, for a detailed
	* explanation of each event.
	*/
	[StructLayout(LayoutKind.Explicit)]
	public struct GGPOEvent {
		[FieldOffset(0)] public GGPOEventCode code;

		// connect, synchronizing, synchronized, disconnected, connection_interrupted, connection_resumed
		[FieldOffset(4)] public Connected connected;
		[FieldOffset(4)] public Synchronizing synchronizing;
		[FieldOffset(4)] public Synchronized synchronized;
		[FieldOffset(4)] public Disconnected disconnected;
		[FieldOffset(4)] public TimeSync timesync;
		[FieldOffset(4)] public ConnectionInterrupted connection_interrupted;
		[FieldOffset(4)] public ConnectionResumed connection_resumed;

		public struct Connected {
			public GGPOPlayerHandle player { get; set; }
		}

		public struct Synchronizing {
			public GGPOPlayerHandle player { get; set; }
			public int count { get; set; }
			public int total { get; set; }
		}

		public struct Synchronized {
			public GGPOPlayerHandle player { get; set; }
		}

		public struct Disconnected {
			public GGPOPlayerHandle player { get; set; }
		}

		public struct TimeSync {
			public int frames_ahead { get; set; }
		}

		public struct ConnectionInterrupted {
			public GGPOPlayerHandle player { get; set; }
			public int disconnect_timeout { get; set; }
		}

		public struct ConnectionResumed {
			public GGPOPlayerHandle player { get; set; }
		}

		public GGPOEvent(GGPOEventCode code) : this() {
			this.code = code;
		}
	}

	/*
	* The GGPOSessionCallbacks structure contains the callback functions that
	* your application must implement.  GGPO.net will periodically call these
	* functions during the game.  All callback functions must be implemented.
	*/
	public struct GGPOSessionCallbacks {
		/*
		* begin_game callback - This callback has been deprecated.  You must
		* implement it, but should ignore the 'game' parameter.
		*/
		public delegate bool BeginGameDelegate(string game);

		public BeginGameDelegate begin_game { get; set; }

		/*
		* save_game_state - The client should allocate a buffer, copy the
		* entire contents of the current game state into it, and copy the
		* length into the *len parameter.  Optionally, the client can compute
		* a checksum of the data and store it in the *checksum argument.
		*/
		public delegate bool SaveGameStateDelegate(ref byte[] buffer, ref int len, ref int checksum, int frame);

		public SaveGameStateDelegate save_game_state { get; set; }

		/*
		* load_game_state - GGPO.net will call this function at the beginning
		* of a rollback.  The buffer and len parameters contain a previously
		* saved state returned from the save_game_state function.  The client
		* should make the current game state match the state contained in the
		* buffer.
		*/
		public delegate bool LoadGameStateDelegate(byte[] buffer);

		public LoadGameStateDelegate load_game_state { get; set; }

		/*
		* log_game_state - Used in diagnostic testing.  The client should use
		* the ggpo_log function to write the contents of the specified save
		* state in a human readible form.
		*/
		public delegate bool LogGameStateDelegate(string filename, byte[] buffer, int len);

		public LogGameStateDelegate log_game_state { get; set; }

		/*
		* free_buffer - Frees a game state allocated in save_game_state.  You
		* should deallocate the memory contained in the buffer.
		*/
		public delegate void FreeBufferDelegate(byte[] buffer);

		public FreeBufferDelegate free_buffer { get; set; }

		/*
		* advance_frame - Called during a rollback.  You should advance your game
		* state by exactly one frame.  Before each frame, call ggpo_synchronize_input
		* to retrieve the inputs you should use for that frame.  After each frame,
		* you should call ggpo_advance_frame to notify GGPO.net that you're
		* finished.
		*
		* The flags parameter is reserved.  It can safely be ignored at this time.
		*/
		public delegate bool AdvanceFrameDelegate(int flags);

		public AdvanceFrameDelegate advance_frame { get; set; }

		/* 
		* on_event - Notification that something has happened.  See the GGPOEventCode
		* structure above for more information.
		*/
		public delegate bool OnEventDelegate(ref GGPOEvent info);

		public OnEventDelegate on_event { get; set; }
	}

	/*
	* The GGPONetworkStats function contains some statistics about the current
	* session.
	*
	* network.send_queue_len - The length of the queue containing UDP packets
	* which have not yet been acknowledged by the end client.  The length of
	* the send queue is a rough indication of the quality of the connection.
	* The longer the send queue, the higher the round-trip time between the
	* clients.  The send queue will also be longer than usual during high
	* packet loss situations.
	*
	* network.recv_queue_len - The number of inputs currently buffered by the
	* GGPO.net network layer which have yet to be validated.  The length of
	* the prediction queue is roughly equal to the current frame number
	* minus the frame number of the last packet in the remote queue.
	*
	* network.ping - The roundtrip packet transmission time as calcuated
	* by GGPO.net.  This will be roughly equal to the actual round trip
	* packet transmission time + 2 the interval at which you call ggpo_idle
	* or ggpo_advance_frame.
	*
	* network.kbps_sent - The estimated bandwidth used between the two
	* clients, in kilobits per second.
	*
	* timesync.local_frames_behind - The number of frames GGPO.net calculates
	* that the local client is behind the remote client at this instant in
	* time.  For example, if at this instant the current game client is running
	* frame 1002 and the remote game client is running frame 1009, this value
	* will mostly likely roughly equal 7.
	*
	* timesync.remote_frames_behind - The same as local_frames_behind, but
	* calculated from the perspective of the remote player.
	*
	*/
	public struct GGPONetworkStats {
		public Network network;
		public TimeSync timesync;

		public struct Network {
			public int send_queue_len { get; set; }
			public readonly int recv_queue_len;
			public int ping { get; set; }
			public int kbps_sent { get; set; }
		}

		public struct TimeSync {
			public int local_frames_behind { get; set; }
			public int remote_frames_behind { get; set; }
		}
	}
}