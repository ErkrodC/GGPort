using System;
using System.Net;
using GGPort;
using UnityEngine;
using UnityEngine.UI;

namespace VectorWar {
	public class VectorWarGameManager : MonoBehaviour {
		[SerializeField] private Text localPortText;
		[SerializeField] private Text numPlayersText;
		[SerializeField] private Toggle spectateModeToggle;
		[SerializeField] private IPAddressText hostIPText;
		[SerializeField] private IPAddressText[] remotePlayerIPTexts;
		[SerializeField] private Toggle localPlayerToggle;
		
		private long start, next, now;
		
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
			string title = $"(pid:{Platform.GetProcessID()} ggpo sdk sample: vector war";
		}

		private void Start() {
			int remotePlayerIndex = 0;

			ushort localPort = ushort.Parse(localPortText.text);
			int numPlayers = int.Parse(numPlayersText.text);

			bool spectateMode = spectateModeToggle.isOn;// wcscmp(__wargv[offset], L"spectate") == 0;
			if (spectateMode) {

				IPEndPoint hostEndPoint = hostIPText.GetIPEndPoint();
				Globals.VectorWar_InitSpectator(localPort, numPlayers, hostEndPoint);
			} else {
				GGPOPlayer[] players = new GGPOPlayer[GGPort.Globals.GGPO_MAX_SPECTATORS + GGPort.Globals.GGPO_MAX_PLAYERS];

				int i;
				for (i = 0; i < numPlayers; i++) {
					players[i].size = players[i].Size(); // TODO for what is size used?
					players[i].player_num = i + 1;
					if (localPlayerToggle.isOn) {
						players[i].type = GGPOPlayerType.GGPO_PLAYERTYPE_LOCAL;
						continue;
					}

					players[i].type = GGPOPlayerType.GGPO_PLAYERTYPE_REMOTE;
					players[i].remote = remotePlayerIPTexts[remotePlayerIndex++].GetIPEndPoint();
				}

				// these are spectators...
				int num_spectators = 0;
				while (remotePlayerIndex < remotePlayerIPTexts.Length) {
					players[i].type = GGPOPlayerType.GGPO_PLAYERTYPE_SPECTATOR;
					players[i].remote = remotePlayerIPTexts[remotePlayerIndex++].GetIPEndPoint();
					i++;
					num_spectators++;
				}

				Globals.VectorWar_Init(localPort, numPlayers, players, num_spectators);
			}

			start = Platform.GetCurrentTimeMS();
			next = start;
			now = start;
		}
		
		private void Update() {
			if (Input.GetKeyUp(KeyCode.P)) {
				PerfMon.ggpoutil_perfmon_toggle();
			} else if (Input.GetKeyUp(KeyCode.Escape)) {
				Globals.VectorWar_Exit();
				Application.Quit();
			} else {
				foreach (KeyCode fKey in fKeys) {
					if (Input.GetKeyUp(fKey)) {
						int playerIndex = int.Parse(fKey.ToString().Split('F')[1]);
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
	}
}