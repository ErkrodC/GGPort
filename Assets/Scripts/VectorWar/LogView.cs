using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class LogView : ScrollRect, IPointerClickHandler {
	private TMP_Text logText;
	private ScrollRect scrollRect;
	private bool needsRecenter;
	private bool recenterEnabled = true;
	private float timeSinceLastPointerClick;

	protected override void Awake() {
		base.Awake();
		scrollRect = GetComponentInChildren<ScrollRect>();
		logText = GetComponentInChildren<TMP_Text>();
	}

	private void FixedUpdate() {
		if (needsRecenter && recenterEnabled) {
			scrollRect.verticalNormalizedPosition = 0;
			needsRecenter = false;
		}
	}

	public void Log(string message) {
		logText.text += $"{message}";
		needsRecenter = true;
	}
	
	public override void OnScroll(PointerEventData eventData) {
		base.OnScroll(eventData);
		recenterEnabled = false;
	}

	public void OnPointerClick(PointerEventData eventData) {
		if (Time.realtimeSinceStartup - timeSinceLastPointerClick < 1) {
			recenterEnabled = true;
		}

		timeSinceLastPointerClick = Time.realtimeSinceStartup;
	}
}