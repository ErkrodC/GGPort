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
	public class Transport : IUpdateHandler {
		public const int MAX_UDP_ENDPOINTS = 16;

		// TODO readdress serialization, no need for type data to be sent with custom protocol
		private const int _MAX_UDP_PACKET_SIZE = /*4096*/ 4096 * 2;

		public event Action<IPEndPoint, PeerMessage> messageReceivedEvent;

		// Network transmission information
		private Socket _socket;
		private byte[] _receiveBuffer = new byte[_MAX_UDP_PACKET_SIZE];

		private static void Log(string msg) {
			LogUtil.Log($"{nameof(Transport)} | {msg}");
		}

		public Transport(ushort port, FrameUpdater frameUpdater) {
			frameUpdater.Register(this);

			Log($"binding udp socket to port {port}.{Environment.NewLine}");
			_socket = CreateSocket(port, 0);
		}

		~Transport() {
			_socket.Close();
			_socket = null;
		}

		public int SendTo(byte[] buffer, int len, SocketFlags flags, IPEndPoint dst) {
			int sentBytes;

			try {
				sentBytes = _socket.SendTo(buffer, len, flags, dst);
				Log($"sent packet length {len} to {dst.Address}:{dst.Port} (ret:{sentBytes}).{Environment.NewLine}");
			} catch (Exception exception) {
				Platform.AssertFailed(exception); // TODO do .ToString() for exceptions elsewhere
				throw;
			}

			return sentBytes;
		}

		private static Socket CreateSocket(ushort portToBindTo, int retries) {
			Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
			socket.Blocking = false;

			for (ushort port = portToBindTo; port <= portToBindTo + retries; port++) {
				try {
					socket.Bind(new IPEndPoint(IPAddress.Any, port));
					Log($"Udp bound to port: {port}.{Environment.NewLine}");
					return socket;
				} catch (Exception exception) {
					Platform.AssertFailed(exception); // TODO this kills retry attempts
				}
			}

			socket.Close();
			return null;
		}

		#region IUpdateHandler

		public bool OnUpdate() {
			for (int i = 0; i < _receiveBuffer.Length; i++) { _receiveBuffer[i] = 0; }

			EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

			for (;;) {
				try {
					if (_socket.Available == 0) { break; }

					int len = _socket.ReceiveFrom(_receiveBuffer, _MAX_UDP_PACKET_SIZE, SocketFlags.None, ref remoteEP);

					if (remoteEP is IPEndPoint remoteIP) {
						Log($"recvfrom returned (len:{len}  from:{remoteIP.Address}:{remoteIP.Port}).{Environment.NewLine}");
						IFormatter br = new BinaryFormatter();
						using (MemoryStream ms = new MemoryStream(_receiveBuffer)) {
							PeerMessage msg = (PeerMessage) br.Deserialize(ms);
							messageReceivedEvent?.Invoke(remoteIP, msg);
						} // TODO optimize refactor
					} else {
						Platform.AssertFailed(
							$"Expecting endpoint of type {nameof(IPEndPoint)}, but was given {remoteEP.GetType()}."
						);
					}

					// TODO: handle len == 0... indicates a disconnect.
				} catch { /* TODO ignoring ALL exceptions is ham-fisted */
				}
			}

			return true;
		}

		#endregion

		// TODO rename after perf tools up
		public struct Stats {
			public readonly int bytesSent;
			public readonly int packetsSent;
			public readonly float kbpsSent;
		}
	}
}