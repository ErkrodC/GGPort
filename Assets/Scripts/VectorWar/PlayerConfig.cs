using System.Net;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable 0649

public class PlayerConfig : MonoBehaviour {
	[SerializeField] private Toggle localPlayerToggle;
	[SerializeField] private IPInputField ipInputField;

	private void Start() {
		ipInputField.gameObject.SetActive(!localPlayerToggle.isOn);
		localPlayerToggle.onValueChanged.AddListener(value => ipInputField.gameObject.SetActive(!value));
	}

	public bool isLocal {
		get => localPlayerToggle.isOn;
		set => localPlayerToggle.isOn = value;
	}

	public IPEndPoint GetIPEndPoint() {
		return ipInputField.GetIPEndPoint();
	}
}