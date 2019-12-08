using TMPro;
using UnityEngine;

#pragma warning disable 0649

namespace VectorWar {
	// wrapper for unity UI API, basically
	public class TextHelper : MonoBehaviour {
		
		public static TextHelper instance;
		[SerializeField] private TMP_Text statusText;
		[SerializeField] private MessageBox messageBox;
		[SerializeField] private LogView logView;
		
		private void Awake() {
			instance = this;
			VectorWar.LogCallback += Log;
		}

		private void OnDestroy() {
			VectorWar.LogCallback -= Log;
		}

		public void SetStatusText(string text) {
			statusText.text = text;
		}

		public void MessageBox(string message) {
			messageBox.ShowMessage(message);
		}

		public void Log(string message) {
			logView.Log(message);
		}
	}
}