using System;
using System.Net;
using GGPort;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VectorWar {
	public class VectorWarGameManager : MonoBehaviour {
		[SerializeField] private TMP_Text titleText;
		[SerializeField] private TMP_InputField localPortText;
		[SerializeField] private TMP_InputField numPlayersText;
		[SerializeField] private Toggle spectateModeToggle;
		[SerializeField] private IPInputField hostIPText;
		[SerializeField] private PlayerConf[] playerConfs;
		[SerializeField] private Button startButton;

		private long start;
		private long next;
		private long now;
		private bool started;
		
		private readonly KeyCode[] fKeys = {
			KeyCode.F1,
			KeyCode.F2,
			KeyCode.F3,
			KeyCode.F4,
			KeyCode.F5,
			KeyCode.F6,
			KeyCode.F7,
			KeyCode.F8,
			KeyCode.F9,
			KeyCode.F10,
			KeyCode.F12
		};
		
		private void Awake() {
			titleText.text = $"(pid:{Platform.GetProcessID()} GGPort SDK Sample: Vector War)";
			started = false;
			startButton.onClick.AddListener(OnStartButton);
			
			hostIPText.gameObject.SetActive(spectateModeToggle.isOn);
			spectateModeToggle.onValueChanged.AddListener(hostIPText.gameObject.SetActive);

			foreach (PlayerConf playerConf in playerConfs) {
				playerConf.gameObject.SetActive(!spectateModeToggle.isOn);
				spectateModeToggle.onValueChanged.AddListener((value) => playerConf.gameObject.SetActive(!value));
			}
		}

		private void Update() {
			if (!started) { return; }
			
			if (Input.GetKeyUp(KeyCode.P)) {
				PerfMon.ggpoutil_perfmon_toggle();
			} else if (Input.GetKeyUp(KeyCode.Escape)) {
				Globals.VectorWar_Exit();
				Application.Quit();
			} else {
				foreach (KeyCode fKey in fKeys) {
					if (Input.GetKeyUp(fKey)) {
						int playerIndex = int.Parse(fKey.ToString().Split('F')[1]) - 1;
						Globals.VectorWar_DisconnectPlayer(playerIndex);
					}
				}
			}

			now = Platform.GetCurrentTimeMS();
			Globals.VectorWar_Idle((int) Math.Max(0, next - now - 1));
			if (now >= next) {
				Globals.VectorWar_RunFrame(); 
				next = now + (1000 / 60);
			}
		}

		private void OnStartButton() {
			int remotePlayerIndex = 0;

			ushort localPort = ushort.Parse(localPortText.text);
			int numPlayers = int.Parse(numPlayersText.text);

			if (spectateModeToggle.isOn) {
				IPEndPoint hostEndPoint = hostIPText.GetIPEndPoint();
				Globals.VectorWar_InitSpectator(localPort, numPlayers, hostEndPoint);
			} else {
				GGPOPlayer[] players = new GGPOPlayer[GGPort.Globals.GGPO_MAX_SPECTATORS + GGPort.Globals.GGPO_MAX_PLAYERS];

				int i;
				for (i = 0; i < numPlayers - 1; i++) {
					players[i].size = players[i].Size(); // TODO for what is size used?
					players[i].player_num = i + 1;

					PlayerConf playerConf = playerConfs[remotePlayerIndex];
					remotePlayerIndex++;
					if (playerConf.IsLocal()) {
						players[i].type = GGPOPlayerType.GGPO_PLAYERTYPE_LOCAL;
						continue;
					}

					players[i].type = GGPOPlayerType.GGPO_PLAYERTYPE_REMOTE;
					players[i].remote = playerConf.GetIPEndPoint();
				}

				// these are spectators...
				int numSpectators = 0;
				while (remotePlayerIndex < playerConfs.Length) {
					players[i].type = GGPOPlayerType.GGPO_PLAYERTYPE_SPECTATOR;
					players[i].remote = playerConfs[remotePlayerIndex++].GetIPEndPoint();
					i++;
					numSpectators++;
				}

				Globals.VectorWar_Init(localPort, numPlayers, players, numSpectators);
			}

			start = Platform.GetCurrentTimeMS();
			next = start;
			now = start;

			started = true;
		}
	}
}