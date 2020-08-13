using System.Net;
using System.Runtime.InteropServices;

// TODO make game state params determinable with generic type param
namespace GGPort {
	// TODO rename
	public static class Types {
		public const int MAX_PLAYERS = 4;
		public const int MAX_PREDICTION_FRAMES = 8;
		public const int MAX_SPECTATORS = 32;
		public const int SPECTATOR_INPUT_INTERVAL = 4;

		public static bool IsSuccess(this ErrorCode result) {
			return result == ErrorCode.Success;
		}
	}

	public struct PlayerHandle {
		public const int INVALID_HANDLE = -1;

		public readonly int handleValue;

		public PlayerHandle(int handleValue) {
			this.handleValue = handleValue;
		}
	}

	public enum PlayerType {
		Local,
		Remote,
		Spectator
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

	public struct Player {
		public int size;
		public PlayerType type;
		public int playerNum;
		public IPEndPoint endPoint;

		public int CalculateSize() {
			int siz = sizeof(int);
			siz += sizeof(PlayerType);
			siz += sizeof(int);
			siz += type == PlayerType.Local
				? 0
				: endPoint.Address.GetAddressBytes().Length;
			siz += sizeof(int);

			return siz;
		}
	}

	public enum ErrorCode {
		Success = 0,
		GeneralFailure = -1,
		InvalidSession = 1,
		InvalidPlayerHandle = 2,
		PlayerOutOfRange = 3,
		PredictionThreshold = 4,
		Unsupported = 5,
		NotSynchronized = 6,
		InRollback = 7,
		InputDropped = 8,
		PlayerDisconnected = 9,
		TooManySpectators = 10,
		InvalidRequest = 11
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
	public enum EventCode {
		ConnectedToPeer = 1000,
		SynchronizingWithPeer = 1001,
		SynchronizedWithPeer = 1002,
		Running = 1003,
		DisconnectedFromPeer = 1004,
		TimeSync = 1005,
		ConnectionInterrupted = 1006,
		ConnectionResumed = 1007
	}

	/*
	* Contains an asynchronous event notification sent
	* by the on_event callback.  See EventCode, above, for a detailed
	* explanation of each event.
	*/
	[StructLayout(LayoutKind.Explicit)]
	public struct EventData {
		[FieldOffset(0)] public EventCode code;

		[FieldOffset(4)] public Connected connected;
		[FieldOffset(4)] public Synchronizing synchronizing;
		[FieldOffset(4)] public Synchronized synchronized;
		[FieldOffset(4)] public Disconnected disconnected;
		[FieldOffset(4)] public TimeSync timeSync;
		[FieldOffset(4)] public ConnectionInterrupted connectionInterrupted;
		[FieldOffset(4)] public ConnectionResumed connectionResumed;

		public struct Connected {
			public PlayerHandle player { get; set; }
		}

		public struct Synchronizing {
			public PlayerHandle player { get; set; }
			public int count { get; set; }
			public int total { get; set; }
		}

		public struct Synchronized {
			public PlayerHandle player { get; set; }
		}

		public struct Disconnected {
			public PlayerHandle player { get; set; }
		}

		public struct TimeSync {
			public int framesAhead { get; set; }
		}

		public struct ConnectionInterrupted {
			public PlayerHandle player { get; set; }
			public int disconnectTimeout { get; set; }
		}

		public struct ConnectionResumed {
			public PlayerHandle player { get; set; }
		}

		public EventData(EventCode code)
			: this() {
			this.code = code;
		}
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
	public struct NetworkStats {
		public Network network;
		public TimeSync timeSync;

		public struct Network {
			public int sendQueueLength { get; set; }
			public readonly int receiveQueueLength;
			public int ping { get; set; }
			public int kbpsSent { get; set; }
		}

		public struct TimeSync {
			public int localFramesBehind { get; set; }
			public int remoteFramesBehind { get; set; }
		}
	}
}