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
	public class Transport : IPollSink {
		public const int kMaxUDPEndpoints = 16;
		// TODO readdress serialization, no need for type data to be sent with custom protocol
		private const int kMaxUDPPacketSize = /*4096*/ 4096 * 2;
		
		// Network transmission information
		private Socket socket;

		// state management
		private ICallbacks callbacks;
		private Poll poll;

		protected static void Log(string msg) {
			
		}

		public Transport() {
			socket = null;
			callbacks = null;
		}

		~Transport() {
			if (socket == null) { return; }

			socket.Close();
			socket = null;
		}

		public void Init(ushort port, Poll poll, ICallbacks callbacks) {
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
			} catch (Exception exception) {
				Log($"{exception.Message}.{Environment.NewLine}");
				Platform.AssertFailed(exception.ToString()); // TODO do .ToString() for exceptions elsewhere
				throw;
			}

			return sentBytes;
		}

		public virtual bool OnLoopPoll(object cookie) {
			byte[] receiveBuffer = new byte[kMaxUDPPacketSize];

			EndPoint fromEndPoint = new IPEndPoint(IPAddress.Any, 0);
			
			for (;;) {
				try {
					if (socket.Available <= 0) {
						break;
					}
					int len = socket.ReceiveFrom(receiveBuffer, kMaxUDPPacketSize, SocketFlags.None, ref fromEndPoint);

					if (fromEndPoint is IPEndPoint fromIPEndPoint) {
						Log($"recvfrom returned (len:{len}  from:{fromIPEndPoint.Address}:{fromIPEndPoint.Port}).{Environment.NewLine}");
						IFormatter br = new BinaryFormatter();
						using (MemoryStream ms = new MemoryStream(receiveBuffer)) {
							PeerMessage msg = (PeerMessage) br.Deserialize(ms); // TODO deserialization probably doesn't work? why does it work for syncing but not input?
							callbacks.OnMsg(fromIPEndPoint, msg);
						} // TODO optimize refactor
					} else {
						Platform.AssertFailed($"Expecting endpoint of type {nameof(IPEndPoint)}, but was given {fromEndPoint.GetType()}.");
					}

					// TODO: handle len == 0... indicates a disconnect.
				} catch (Exception exception) {
					Log($"{exception.Message}{Environment.NewLine}");
					Platform.AssertFailed(exception.ToString());
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
					//break; // TODO remove
				}
			}
			
			s.Close();
			return null;
		}
		
		// TODO fix param names, 4 fxns
		// TODO in fact, could probably use async C# sockets over polling 
		public virtual bool OnHandlePoll(object TODO) { return true; }
		public virtual bool OnMsgPoll(object TODO) { return true; }
		public virtual bool OnPeriodicPoll(object TODO0, long TODO1) { return true; }
		
		public struct Stats {
			public readonly int bytes_sent;
			public readonly int packets_sent;
			public readonly float kbps_sent;
		}

		public interface ICallbacks {
			void OnMsg(IPEndPoint from, PeerMessage msg);
		}
	}
}
