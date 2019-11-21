namespace GGPort {
   public class Peer2PeerBackend : GGPOSession, IPollSink, Udp::Callbacks {
      protected GGPOSessionCallbacks  _callbacks;
      protected Poll                  _poll;
      protected Sync                  _sync;
      protected Udp                   _udp;
      protected UdpProtocol           *_endpoints;
      protected UdpProtocol           _spectators[GGPO_MAX_SPECTATORS];
      protected int                   _num_spectators;
      protected int                   _input_size;
      
      protected bool                  _synchronizing;
      protected int                   _num_players;
      protected int                   _next_recommended_sleep;
      
      protected int                   _next_spectator_frame;
      protected int                   _disconnect_timeout;
      protected int                   _disconnect_notify_start;

      protected UdpMsg.connect_status _local_connect_status[UDP_MSG_MAX_PLAYERS];
      
      public Peer2PeerBackend(ref GGPOSessionCallbacks cb, string gamename, ushort localport, int num_players, int input_size);
      virtual ~Peer2PeerBackend();

      public virtual GGPOErrorCode DoPoll(int timeout);
      public virtual GGPOErrorCode AddPlayer(GGPOPlayer *player, GGPOPlayerHandle *handle);
      public virtual GGPOErrorCode AddLocalInput(GGPOPlayerHandle player, void *values, int size);
      public virtual GGPOErrorCode SyncInput(void *values, int size, int *disconnect_flags);
      public virtual GGPOErrorCode IncrementFrame(void);
      public virtual GGPOErrorCode DisconnectPlayer(GGPOPlayerHandle handle);
      public virtual GGPOErrorCode GetNetworkStats(GGPONetworkStats *stats, GGPOPlayerHandle handle);
      public virtual GGPOErrorCode SetFrameDelay(GGPOPlayerHandle player, int delay);
      public virtual GGPOErrorCode SetDisconnectTimeout(int timeout);
      public virtual GGPOErrorCode SetDisconnectNotifyStart(int timeout);

      public virtual void OnMsg(sockaddr_in &from, UdpMsg *msg, int len);

      protected GGPOErrorCode PlayerHandleToQueue(GGPOPlayerHandle player, int *queue);
      protected GGPOPlayerHandle QueueToPlayerHandle(int queue) { return (GGPOPlayerHandle)(queue + 1); }
      protected GGPOPlayerHandle QueueToSpectatorHandle(int queue) { return (GGPOPlayerHandle)(queue + 1000); } /* out of range of the player array, basically */
      protected void DisconnectPlayerQueue(int queue, int syncto);
      protected void PollSyncEvents(void);
      protected void PollUdpProtocolEvents(void);
      protected void CheckInitialSync(void);
      protected int Poll2Players(int current_frame);
      protected int PollNPlayers(int current_frame);
      protected void AddRemotePlayer(char *remoteip, uint16 reportport, int queue);
      protected GGPOErrorCode AddSpectator(char *remoteip, uint16 reportport);
      protected virtual void OnSyncEvent(Sync::Event &e) { }
      protected virtual void OnUdpProtocolEvent(UdpProtocol::Event &e, GGPOPlayerHandle handle);
      protected virtual void OnUdpProtocolPeerEvent(UdpProtocol::Event &e, int queue);
      protected virtual void OnUdpProtocolSpectatorEvent(UdpProtocol::Event &e, int queue);
   };
}