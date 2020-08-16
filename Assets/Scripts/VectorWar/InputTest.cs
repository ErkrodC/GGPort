using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InputTest : MonoBehaviour {
	public static readonly HashSet<KeyCode> testInputIsOnByKey = new HashSet<KeyCode>();

	private readonly KeyCode[] _testInputs = {
		KeyCode.RightArrow
	};

	private static bool _usedInput;
	private readonly WaitUntil _waitUntil = new WaitUntil(() => _usedInput);

	public void OnShortPulseButtonClicked() {
		StartCoroutine(ShortPulseCoroutine());

		IEnumerator ShortPulseCoroutine() {
			foreach (KeyCode keyCode in _testInputs) {
				testInputIsOnByKey.Add(keyCode);
			}
			
			yield return _waitUntil;
			/*for (int i = 0; i < 5; i++) {
				yield return null;
			}*/

			foreach (KeyCode keyCode in _testInputs) {
				testInputIsOnByKey.Remove(keyCode);
			}
			
			_usedInput = false;
		}
	}

	public static void UseTestInput() {
		_usedInput = true;
	}
}