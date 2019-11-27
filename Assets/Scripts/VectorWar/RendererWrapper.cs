using TMPro;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable 0649

namespace VectorWar {
	// wrapper for unity UI API, basically
	public class RendererWrapper : MonoBehaviour {
		public static RendererWrapper instance;
		[SerializeField] private TMP_Text statusText;
		[SerializeField] private MessageBox messageBox;

		private void Awake() {
			instance = this;
		}

		public void SetStatusText(string text) {
			statusText.text = text;
		}

		public void MessageBox(string message) {
			messageBox.ShowMessage(message);
		}
	}
}