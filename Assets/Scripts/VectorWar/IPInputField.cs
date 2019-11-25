using System.Net;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(TMP_InputField))]
public class IPInputField : MonoBehaviour {
	private TMP_InputField fullAddressText;

	private void Awake() {
		fullAddressText = GetComponent<TMP_InputField>();
	}

	public IPEndPoint GetIPEndPoint() {
		string[] ipAddressAndPortStrings = fullAddressText.text.Split(':');
		string ipAddressString = ipAddressAndPortStrings[0];
		int port = int.Parse(ipAddressAndPortStrings[1]);
		
		return new IPEndPoint(IPAddress.Parse(ipAddressString), port);
	}
}