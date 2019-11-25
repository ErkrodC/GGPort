using UnityEngine;
using UnityEngine.UI;

namespace VectorWar {
	
	// wrapper for unity UI API, basically
	public class RendererWrapper : MonoBehaviour {
		public static RendererWrapper instance;
		[SerializeField] private Text statusText;
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