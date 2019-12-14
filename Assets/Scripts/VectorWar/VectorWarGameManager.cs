using System;
using System.Collections.Generic;
using System.Net;
using GGPort;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable 0649

namespace VectorWar {
	public class VectorWarGameManager : MonoBehaviour {
		[SerializeField] private GameObject gameStartUI;
		[SerializeField] private TMP_Text titleText;
		[SerializeField] private TMP_InputField localPortText;
		[SerializeField] private TMP_InputField numPlayersText;
		[SerializeField] private Toggle spectateModeToggle;
		[SerializeField] private IPInputField hostIPText;
		[SerializeField] private Transform playerConfsContainer;
		[SerializeField] private GameObject playerConfPrefab;
		[SerializeField] private Button startButton;
		private List<PlayerConf> playerConfs = new List<PlayerConf>();

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
			
			numPlayersText.onValueChanged.AddListener(PopulatePlayerConfContainer);
			PopulatePlayerConfContainer(numPlayersText.text);

			foreach (PlayerConf playerConf in playerConfs) {
				playerConf.gameObject.SetActive(!spectateModeToggle.isOn);
				spectateModeToggle.onValueChanged.AddListener(isOn => playerConf.gameObject.SetActive(!isOn));
			}

			hostIPText.gameObject.SetActive(spectateModeToggle.isOn);
			spectateModeToggle.onValueChanged.AddListener(hostIPText.gameObject.SetActive);

#if !UNITY_EDITOR
			localPortText.text = "5556";
#endif
			playerConfs[0].IsLocal = true;
		}

		private void Update() {
			if (!started) { return; }
			
			if (Input.GetKeyUp(KeyCode.P)) {
				PerfMon.ggpoutil_perfmon_toggle();
			} else if (Input.GetKeyUp(KeyCode.Escape)) {
				VectorWar.Exit();
				Application.Quit();
			} else {
				foreach (KeyCode fKey in fKeys) {
					if (Input.GetKeyUp(fKey)) {
						int playerIndex = int.Parse(fKey.ToString().Split('F')[1]) - 1;
						VectorWar.DisconnectPlayer(playerIndex);
					}
				}
			}

			now = Platform.GetCurrentTimeMS();
			VectorWar.Idle((int) Math.Max(0, next - now - 1));
			if (now >= next) {
				VectorWar.RunFrame(); 
				next = now + (1000 / 60);
			}
		}

		private void OnStartButton() {
			ushort localPort = ushort.Parse(localPortText.text);
			int numPlayers = int.Parse(numPlayersText.text);

			if (spectateModeToggle.isOn) {
				IPEndPoint hostEndPoint = hostIPText.GetIPEndPoint();
				VectorWar.InitSpectator(localPort, numPlayers, hostEndPoint);
			} else {
				Player[] players = new Player[GGPort.Types.kMaxSpectators + GGPort.Types.kMaxPlayers];

				int playerIndex;
				for (playerIndex = 0; playerIndex < numPlayers; playerIndex++) {
					players[playerIndex].Size = players[playerIndex].CalculateSize(); // TODO for what is size used?
					players[playerIndex].PlayerNum = playerIndex + 1;

					PlayerConf playerConf = playerConfs[playerIndex];
					
					if (playerConf.IsLocal) {
						players[playerIndex].Type = PlayerType.Local;
					} else {
						players[playerIndex].Type = PlayerType.Remote;
						players[playerIndex].EndPoint = playerConf.GetIPEndPoint();
					}
				}

				// TODO allow adding spectators
				/*// these are spectators...
				int numSpectators = 0;
				for (int spectatorIndex = playerIndex; spectatorIndex < numPlayers; spectatorIndex++, numSpectators++) {
					players[spectatorIndex].type = GGPOPlayerType.GGPO_PLAYERTYPE_SPECTATOR;
					players[spectatorIndex].remote = playerConfs[spectatorIndex].GetIPEndPoint();
				}*/
				
				/*while (playerIndex < playerConfs.Count) {
					
					playerIndex++;
					numSpectators++;
				}*/

				gameStartUI.SetActive(false);
				VectorWar.Init(localPort, numPlayers, players, 0/*numSpectators*/);
			}

			start = Platform.GetCurrentTimeMS();
			next = start;
			now = start;

			started = true;
		}

		private void PopulatePlayerConfContainer(string text) {
			if (int.TryParse(text, out int numPlayers)) {
				const int playerConfLimit = GGPort.Types.kMaxPlayers;
				if (numPlayers > playerConfLimit) {
					numPlayersText.text = playerConfLimit.ToString();
					return;
				}
				
				int currentNumConfs = playerConfsContainer.childCount;

				if (numPlayers > currentNumConfs) {
					for (int i = currentNumConfs; i < numPlayers; i++) {
						PlayerConf newPlayerConf = Instantiate(playerConfPrefab, playerConfsContainer).GetComponent<PlayerConf>();
						spectateModeToggle.onValueChanged.AddListener(isOn => newPlayerConf.gameObject.SetActive(!isOn));
						playerConfs.Add(newPlayerConf);
					}
				} else if (numPlayers < currentNumConfs) {
					for (int i = numPlayers; i < currentNumConfs; i++) {
						playerConfs[i].gameObject.SetActive(false);
					}
				}

				for (int i = 0; i < numPlayers; i++) {
					playerConfs[i].gameObject.SetActive(!spectateModeToggle.isOn);
				}
			}
		}
	}
}