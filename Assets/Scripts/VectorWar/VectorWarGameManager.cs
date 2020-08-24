using System.Collections.Generic;
using System.Net;
using GGPort;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
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

		public static VectorWarInput vectorWarInput;
		
		private readonly List<PlayerConfig> _playerConfigs = new List<PlayerConfig>();
		private readonly KeyCode[] _fKeys = {
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

		private bool _started;

		private void Awake() {
			titleText.text = $"(pid:{Platform.GetProcessID()} GGPort SDK Sample: Vector War)";
			_started = false;
			startButton.onClick.AddListener(OnStartButton);

			hostIPText.gameObject.SetActive(spectateModeToggle.isOn);
			spectateModeToggle.onValueChanged.AddListener(hostIPText.gameObject.SetActive);

			numPlayersText.onValueChanged.AddListener(PopulatePlayerConfContainer);
			PopulatePlayerConfContainer(numPlayersText.text);

			foreach (PlayerConfig playerConfig in _playerConfigs) {
				playerConfig.gameObject.SetActive(!spectateModeToggle.isOn);
				spectateModeToggle.onValueChanged.AddListener(isOn => playerConfig.gameObject.SetActive(!isOn));
			}

			hostIPText.gameObject.SetActive(spectateModeToggle.isOn);
			spectateModeToggle.onValueChanged.AddListener(hostIPText.gameObject.SetActive);

#if !UNITY_EDITOR
			localPortText.text = "5556";
#endif
			_playerConfigs[0].isLocal = true;

			vectorWarInput = new VectorWarInput();
			vectorWarInput.GlobalMap.Enable();
			vectorWarInput.ShipBattleMap.Enable();
		}

		private void Update() {
			if (!_started) { return; }

			if (vectorWarInput.GlobalMap.TogglePerformanceMonitoring.triggered) {
				PerfMon<GameState>.ggpoutil_perfmon_toggle();
			} else if (vectorWarInput.GlobalMap.QuitApplication.triggered) {
				VectorWar.Exit();
				Application.Quit();
			} else {
				/*foreach (KeyCode fKey in _fKeys) {
					if (!Input.GetKeyUp(fKey)) { continue; }

					int playerIndex = int.Parse(fKey.ToString().Split('F')[1]) - 1;
					VectorWar.DisconnectPlayer(playerIndex);
				}*/
			}
			
			VectorWar.Idle(0);
		}

		private void FixedUpdate() {
			if (!_started) { return; }
			
			VectorWar.RunFrame();
		}

		private void OnStartButton() {
			ushort localPort = ushort.Parse(localPortText.text);
			int numPlayers = int.Parse(numPlayersText.text);

			if (spectateModeToggle.isOn) {
				IPEndPoint hostEndPoint = hostIPText.GetIPEndPoint();
				VectorWar.InitSpectator(
					localPort,
					numPlayers,
					hostEndPoint,
					Screen.safeArea.xMin,
					Screen.safeArea.xMax,
					Screen.safeArea.yMin,
					Screen.safeArea.yMax
				);
			} else {
				Player[] players = new Player[GGPort.Types.MAX_SPECTATORS + GGPort.Types.MAX_PLAYERS];

				int playerIndex;
				for (playerIndex = 0; playerIndex < numPlayers; playerIndex++) {
					players[playerIndex].size = players[playerIndex].CalculateSize(); // TODO for what is size used?
					players[playerIndex].playerNum = playerIndex + 1;

					PlayerConfig playerConfig = _playerConfigs[playerIndex];

					if (playerConfig.isLocal) {
						players[playerIndex].type = PlayerType.Local;
					} else {
						players[playerIndex].type = PlayerType.Remote;
						players[playerIndex].endPoint = playerConfig.GetIPEndPoint();
					}
				}

				// TODO allow adding spectators
				/*// these are spectators...
				int numSpectators = 0;
				for (int spectatorIndex = playerIndex; spectatorIndex < _numPlayers; spectatorIndex++, numSpectators++) {
					players[spectatorIndex].type = GGPOPlayerType.GGPO_PLAYERTYPE_SPECTATOR;
					players[spectatorIndex].remote = playerConfs[spectatorIndex].GetIPEndPoint();
				}*/

				/*while (playerIndex < playerConfs.Count) {
					
					playerIndex++;
					numSpectators++;
				}*/

				gameStartUI.SetActive(false);
				VectorWar.Init(
					localPort,
					numPlayers,
					players,
					0 /*numSpectators*/,
					ReadInputs,
					Screen.safeArea.xMin,
					Screen.safeArea.xMax,
					Screen.safeArea.yMin,
					Screen.safeArea.yMax
				);
			}
			
			_started = true;
		}

		private void PopulatePlayerConfContainer(string text) {
			if (int.TryParse(text, out int numPlayers)) {
				const int _PLAYER_CONF_LIMIT = GGPort.Types.MAX_PLAYERS;
				if (numPlayers > _PLAYER_CONF_LIMIT) {
					numPlayersText.text = _PLAYER_CONF_LIMIT.ToString();
					return;
				}

				int currentNumConfs = playerConfsContainer.childCount;

				if (numPlayers > currentNumConfs) {
					for (int i = currentNumConfs; i < numPlayers; i++) {
						PlayerConfig newPlayerConfig =
							Instantiate(playerConfPrefab, playerConfsContainer).GetComponent<PlayerConfig>();
						spectateModeToggle.onValueChanged.AddListener(isOn => newPlayerConfig.gameObject.SetActive(!isOn));
						_playerConfigs.Add(newPlayerConfig);
					}
				} else if (numPlayers < currentNumConfs) {
					for (int i = numPlayers; i < currentNumConfs; i++) {
						_playerConfigs[i].gameObject.SetActive(false);
					}
				}

				for (int i = 0; i < numPlayers; i++) {
					_playerConfigs[i].gameObject.SetActive(!spectateModeToggle.isOn);
				}
			}
		}
		
		private static VectorWar.ShipInput ReadInputs() {
			VectorWar.ShipInput inputs = VectorWar.ShipInput.None;
			
			InputActionMap shipBattleActionMap = VectorWarGameManager.vectorWarInput.ShipBattleMap.Get();
			for (int i = 0; i < shipBattleActionMap.actions.Count; i++) {
				inputs |= (VectorWar.ShipInput) ((int) shipBattleActionMap.actions[i].ReadValue<float>() << i);
			}
			
			return inputs;
		}
	}
}