using TMPro;
using UnityEngine;

#pragma warning disable 0649

namespace VectorWar {
	public class GameRenderer : MonoBehaviour {
		public static GameRenderer instance;
		[SerializeField] private TMP_Text statusText;
		[SerializeField] private MessageBox messageBox;
		[SerializeField] private LogView logView;
		[SerializeField] private GameObject shipPrefab;
		[SerializeField] private Canvas gameCanvas;

		private GameObject[] _visualShips;

		private void Awake() {
			instance = this;

			_visualShips = new GameObject[GameState.MAX_SHIPS];
			for (int i = 0; i < _visualShips.Length; i++) {
				_visualShips[i] = Instantiate(shipPrefab, gameCanvas.transform);
				_visualShips[i].SetActive(false);
			}

#if SHOW_LOG
			logView.gameObject.SetActive(true);
			VectorWar.logTextEvent += logView.Log;
#else
			logView.gameObject.SetActive(false);
#endif
		}

		private void OnDestroy() {
#if SHOW_LOG
			VectorWar.logTextEvent -= logView.Log;
#endif
		}

		public void SetStatusText(string text) {
			statusText.text = text;
		}

		public void MessageBox(string message) {
			messageBox.ShowMessage(message);
		}

		public void Draw(GameState gameState, NonGameState nonGameState) {
			for (int shipIndex = 0; shipIndex < gameState.ships.Length; shipIndex++) {
				Ship ship = gameState.ships[shipIndex];
				GameObject visualShip = _visualShips[shipIndex];

				visualShip.SetActive(true);
				visualShip.transform.position = new Vector3((float) ship.position.x, (float) ship.position.y, 0);
				visualShip.transform.rotation = Quaternion.AngleAxis((float) ship.heading, Vector3.forward);
			}
		}
	}
}