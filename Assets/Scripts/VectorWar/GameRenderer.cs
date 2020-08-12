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

		private GameObject[] m_visualShips;
		
		private void Awake() {
			instance = this;
			
			m_visualShips = new GameObject[GameState.MAX_SHIPS];
			for (int i = 0; i < m_visualShips.Length; i++) {
				m_visualShips[i] = Instantiate(shipPrefab, gameCanvas.transform);
				m_visualShips[i].SetActive(false);
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
				GameObject visualShip = m_visualShips[shipIndex];

				visualShip.SetActive(true);
				visualShip.transform.position = new Vector3(ship.position.x, ship.position.y, 0);
				visualShip.transform.rotation = Quaternion.AngleAxis(ship.heading, Vector3.forward);
			}
		}
	}
}