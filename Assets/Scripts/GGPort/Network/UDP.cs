/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

namespace GGPort {
	public class UDP : IPollSink {
		public const int MAX_UDP_ENDPOINTS = 16;
		// TODO readdress serialization, no need for type data to be sent with custom protocol
		public const int MAX_UDP_PACKET_SIZE = /*4096*/ 5437;
		
		// Network transmission information
		protected Socket socket;

		// state management
		protected Callbacks callbacks;
		protected Poll poll;

		protected static void Log(string msg) {
			
		}

		public UDP() {
			socket = null;
			callbacks = null;
		}

		~UDP() {
			if (socket != null) {
				socket.Close();
				socket = null;
			}
		}

		public void Init(ushort port, ref Poll poll, Callbacks callbacks) {
			this.callbacks = callbacks;

			this.poll = poll;
			this.poll.RegisterLoop(this);

			Log($"binding udp socket to port {port}.{Environment.NewLine}");
			socket = CreateSocket(port, 0);
		}
	   
		public int SendTo(byte[] buffer, int len, SocketFlags flags, IPEndPoint dst) {
			int sentBytes = 0;
			try {
				 sentBytes = socket.SendTo(buffer, len, flags, dst);
				Log($"sent packet length {len} to {dst.Address}:{dst.Port} (ret:{sentBytes}).{Environment.NewLine}");
			} catch (Exception e) {
				Log($"{e.Message}.{Environment.NewLine}");
				throw;
			}

			return sentBytes;
		}

		public virtual bool OnLoopPoll(object cookie) {
			byte[] receiveBuffer = new byte[MAX_UDP_PACKET_SIZE];

			EndPoint senderEndPoint = new IPEndPoint(IPAddress.Any, 0);
			
			for (;;) {
				try {
					if (socket.Available <= 0) {
						break;
					}
					int len = socket.ReceiveFrom(receiveBuffer, MAX_UDP_PACKET_SIZE, SocketFlags.None, ref senderEndPoint);

					if (senderEndPoint is IPEndPoint recvAddrIP) {
						Log($"recvfrom returned (len:{len}  from:{recvAddrIP.Address}:{recvAddrIP.Port}).{Environment.NewLine}");
						IFormatter br = new BinaryFormatter();
						using (MemoryStream ms = new MemoryStream(receiveBuffer)) {
							UDPMessage msg = (UDPMessage) br.Deserialize(ms); // TODO deserialization probably doesn't work? why does it work for syncing but not input?
							callbacks.OnMsg(recvAddrIP, ref msg, len);
						} // TODO optimize refactor
					} else {
						Platform.AssertFailed($"Expecting endpoint of type {nameof(IPEndPoint)}, but was given {senderEndPoint.GetType()}.");
					}

					// TODO: handle len == 0... indicates a disconnect.
				} catch (Exception e) {
					Log($"{e.Message}{Environment.NewLine}");
					break;
				} 
			}
			return true;
		}

		public static Socket CreateSocket(ushort bind_port, int retries) {
			Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
			//s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true); // TODO why doesn't this work?

			// non-blocking...
			s.Blocking = false;

			for (ushort port = bind_port; port <= bind_port + retries; port++) {
				try {
					s.Bind(new IPEndPoint(IPAddress.Any, port));
					Log($"Udp bound to port: {port}.{Environment.NewLine}");
					return s;
				} catch (Exception e) {
					Console.WriteLine(e);
					//break;
				}
			}
			
			s.Close();
			return null;
		}
		
		// TODO fix param names, 4 fxns
		public virtual bool OnHandlePoll(object TODO) { return true; }
		public virtual bool OnMsgPoll(object TODO) { return true; }
		public virtual bool OnPeriodicPoll(object TODO0, long TODO1) { return true; }
		
		public struct Stats {
			public readonly int bytes_sent;
			public readonly int packets_sent;
			public readonly float kbps_sent;
		}

		public interface Callbacks {
			void OnMsg(IPEndPoint from, ref UDPMessage msg, int len);
		}
	}
}
