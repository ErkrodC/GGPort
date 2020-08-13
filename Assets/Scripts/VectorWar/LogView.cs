using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class LogView : ScrollRect, IPointerClickHandler {
	private TMP_Text _logText;
	private ScrollRect _scrollRect;
	private bool _needsRecenter;
	private bool _recenterEnabled = true;
	private float _timeSinceLastPointerClick;

	protected override void Awake() {
		base.Awake();
		_scrollRect = GetComponentInChildren<ScrollRect>();
		_logText = GetComponentInChildren<TMP_Text>();
	}

	private void FixedUpdate() {
		if (!_needsRecenter || !_recenterEnabled) { return; }

		_scrollRect.verticalNormalizedPosition = 0;
		_needsRecenter = false;
	}

	public void Log(string message) {
		_logText.text += $"{message}";
		_needsRecenter = true;
	}

	public override void OnScroll(PointerEventData eventData) {
		base.OnScroll(eventData);
		_recenterEnabled = false;
	}

	public void OnPointerClick(PointerEventData eventData) {
		if (Time.realtimeSinceStartup - _timeSinceLastPointerClick < 1) {
			_recenterEnabled = true;
		}

		_timeSinceLastPointerClick = Time.realtimeSinceStartup;
	}
}