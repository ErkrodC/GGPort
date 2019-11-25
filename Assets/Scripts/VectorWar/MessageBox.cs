using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class MessageBox : MonoBehaviour {
	[SerializeField] private Text messageText;

	private void Start() {
		gameObject.SetActive(false);
	}

	public void ShowMessage(string message) {
		messageText.text = message;
		gameObject.SetActive(true);

		StartCoroutine(DelayedDeactivate());

		IEnumerator DelayedDeactivate() {
			yield return new WaitForSeconds(5);
			gameObject.SetActive(false);
		}
	}
}
