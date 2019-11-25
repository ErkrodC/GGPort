using System.Net;
using UnityEngine;
using UnityEngine.UI;

public class IPAddressText : MonoBehaviour {
	[SerializeField] private Text fullAddressText;
	
	public IPEndPoint GetIPEndPoint() {
		string[] ipAddressAndPortStrings = fullAddressText.text.Split(':');
		string ipAddressString = ipAddressAndPortStrings[0];
		int port = int.Parse(ipAddressAndPortStrings[1]);
		
		return new IPEndPoint(IPAddress.Parse(ipAddressString), port);
	}
}