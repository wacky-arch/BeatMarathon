using System;
using System.Globalization;
using System.Text;
using BeatMarathon.Models;
using BeatSaberMarkupLanguage;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BeatMarathon.UI;

public class RankPlateView
{
	private const float UiScale = 0.01f;

	private const float UiUnitsPerMeter = 100f;

	private const float AvatarIconSizeUi = 36f;

	private const float ElementGap = 0.08f;

	private const float TextContainerWidthUi = 800f;

	private const float TextContainerHeightUi = 50f;

	private const float UpperLineOffsetY = 0.12f;

	private const float LowerLineOffsetY = -0.12f;

	private static readonly Vector2 ShadowOffsetUi = new Vector2(1.2f, -1.2f);

	private static readonly Color NormalRivalColor = Color.white;

	private static readonly Color SelfBestColor = new Color(0.2f, 0.9f, 1f, 1f);

	private static readonly Color OvertakenHighlightColor = new Color(0.2f, 1f, 0.45f, 1f);

	private static readonly Color OvertakenSelfColor = new Color(1f, 0.85f, 0.2f, 1f);

	private readonly GameObject _rootObject;

	private readonly Transform _rootTransform;

	private readonly GameObject _canvasObject;

	private readonly RectTransform _canvasRect;

	private readonly GameObject _avatarObject;

	private readonly RectTransform _avatarRect;

	private readonly ImageView _avatarImage;

	private readonly GameObject _upperTextObj;

	private readonly GameObject _upperShadowObj;

	private readonly CurvedTextMeshPro _upperTextMesh;

	private readonly CurvedTextMeshPro _upperShadowTextMesh;

	private readonly GameObject _lowerTextObj;

	private readonly GameObject _lowerShadowObj;

	private readonly CurvedTextMeshPro _lowerTextMesh;

	private readonly CurvedTextMeshPro _lowerShadowTextMesh;

	private readonly StringBuilder _sb = new StringBuilder(64);

	private BeatMarathon.Models.LeaderboardEntry _boundEntry;

	private Sprite _fallbackCachedAvatar;

	private int _lastFormattedDeltaCent = int.MinValue;

	private int _lastDisplayMask = -1;

	private bool _hasValidPosition;

	private Vector3 _currentTargetLocalPos;

	private Quaternion _currentTargetLocalRot;

	public BeatMarathon.Models.LeaderboardEntry BoundEntry => _boundEntry;

	public bool IsActive => _rootObject.activeSelf;

	public RankPlateView(Transform parentTransform, TMP_FontAsset sharedFont, Material sharedOutlineMaterial, Material sharedUiNoGlowMaterial, int poolIndex)
	{
		_rootObject = new GameObject($"RankPlate_{poolIndex}");
		_rootTransform = _rootObject.transform;
		_rootTransform.SetParent(parentTransform, worldPositionStays: false);

		_canvasObject = new GameObject("PlateCanvas", typeof(RectTransform), typeof(Canvas), typeof(CurvedCanvasSettings));
		_canvasObject.layer = 5;
		_canvasRect = (RectTransform)_canvasObject.transform;
		_canvasRect.SetParent(_rootTransform, worldPositionStays: false);
		_canvasRect.sizeDelta = new Vector2(900f, 100f);
		_canvasRect.pivot = new Vector2(0.5f, 0.5f);
		_canvasRect.localPosition = Vector3.zero;
		_canvasRect.localRotation = Quaternion.identity;
		_canvasRect.localScale = new Vector3(UiScale, UiScale, UiScale);

		Canvas canvas = _canvasObject.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.sortingOrder = 11;
		canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;

		CurvedCanvasSettings curved = _canvasObject.GetComponent<CurvedCanvasSettings>();
		curved.SetRadius(0f);

		_avatarObject = new GameObject("AvatarIcon", typeof(RectTransform));
		_avatarObject.layer = 5;
		_avatarImage = _avatarObject.AddComponent<ImageView>();
		if (sharedUiNoGlowMaterial != null)
		{
			((Graphic)(object)_avatarImage).material = sharedUiNoGlowMaterial;
		}
		((Image)(object)_avatarImage).type = Image.Type.Simple;
		((Graphic)(object)_avatarImage).raycastTarget = false;
		((Image)(object)_avatarImage).preserveAspect = true;
		_avatarRect = ((Graphic)(object)_avatarImage).rectTransform;
		_avatarRect.SetParent(_canvasRect, worldPositionStays: false);
		_avatarRect.sizeDelta = new Vector2(AvatarIconSizeUi, AvatarIconSizeUi);
		_avatarRect.anchorMin = new Vector2(0.5f, 0.5f);
		_avatarRect.anchorMax = new Vector2(0.5f, 0.5f);
		_avatarRect.pivot = new Vector2(0.5f, 0.5f);
		_avatarRect.localPosition = Vector3.zero;
		_avatarRect.localRotation = Quaternion.identity;
		_avatarRect.localScale = Vector3.one;
		_avatarObject.SetActive(value: false);

#pragma warning disable CS0618
		_upperShadowTextMesh = BeatSaberUI.CreateText<CurvedTextMeshPro>(_canvasRect, string.Empty, Vector2.zero, new Vector2(TextContainerWidthUi, TextContainerHeightUi));
		_upperShadowObj = _upperShadowTextMesh.gameObject;
		_upperShadowObj.name = "UpperNameShadow";
		ConfigureTmp(_upperShadowTextMesh, sharedFont, sharedOutlineMaterial, 23f, new Color(0f, 0f, 0f, 0.95f));

		_upperTextMesh = BeatSaberUI.CreateText<CurvedTextMeshPro>(_canvasRect, string.Empty, Vector2.zero, new Vector2(TextContainerWidthUi, TextContainerHeightUi));
		_upperTextObj = _upperTextMesh.gameObject;
		_upperTextObj.name = "UpperNameText";
		ConfigureTmp(_upperTextMesh, sharedFont, sharedOutlineMaterial, 23f, NormalRivalColor);

		_lowerShadowTextMesh = BeatSaberUI.CreateText<CurvedTextMeshPro>(_canvasRect, string.Empty, Vector2.zero, new Vector2(TextContainerWidthUi, TextContainerHeightUi));
		_lowerShadowObj = _lowerShadowTextMesh.gameObject;
		_lowerShadowObj.name = "LowerStatsShadow";
		ConfigureTmp(_lowerShadowTextMesh, sharedFont, sharedOutlineMaterial, 20f, new Color(0f, 0f, 0f, 0.95f));

		_lowerTextMesh = BeatSaberUI.CreateText<CurvedTextMeshPro>(_canvasRect, string.Empty, Vector2.zero, new Vector2(TextContainerWidthUi, TextContainerHeightUi));
		_lowerTextObj = _lowerTextMesh.gameObject;
		_lowerTextObj.name = "LowerStatsText";
		ConfigureTmp(_lowerTextMesh, sharedFont, sharedOutlineMaterial, 20f, NormalRivalColor);
#pragma warning restore CS0618

		_rootObject.SetActive(value: false);
	}

	private static void ConfigureTmp(CurvedTextMeshPro tmp, TMP_FontAsset sharedFont, Material sharedMaterial, float fontSize, Color color)
	{
		if (sharedFont != null)
		{
			tmp.font = sharedFont;
		}
		if (sharedMaterial != null)
		{
			tmp.fontSharedMaterial = sharedMaterial;
		}
		tmp.fontSize = fontSize;
		tmp.alignment = TextAlignmentOptions.MidlineLeft;
		tmp.enableWordWrapping = false;
		tmp.overflowMode = TextOverflowModes.Overflow;
		tmp.richText = false;
		tmp.raycastTarget = false;
		tmp.color = color;
		RectTransform rectTransform = tmp.rectTransform;
		rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
		rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
		rectTransform.pivot = new Vector2(0.5f, 0.5f);
		rectTransform.sizeDelta = new Vector2(TextContainerWidthUi, TextContainerHeightUi);
		rectTransform.localPosition = Vector3.zero;
		rectTransform.localRotation = Quaternion.identity;
		rectTransform.localScale = Vector3.one;
	}

	public void BindEntry(BeatMarathon.Models.LeaderboardEntry entry, Sprite fallbackCachedAvatar)
	{
		if (entry == null)
		{
			Hide();
			return;
		}
		bool num = _boundEntry == entry || (_boundEntry != null && string.Equals(_boundEntry.PlayerId, entry.PlayerId, StringComparison.Ordinal));
		_boundEntry = entry;
		_fallbackCachedAvatar = fallbackCachedAvatar;
		if (!num)
		{
			_lastFormattedDeltaCent = int.MinValue;
			_lastDisplayMask = -1;
			_hasValidPosition = false;
		}
	}

	public void UpdatePlateTransformAndVisuals(float currentPlayerAccuracy, PluginConfig config, float deltaTime)
	{
		if (_boundEntry == null)
		{
			Hide();
			return;
		}
		if (!config.ShowRank && !config.ShowAvatar && !config.ShowPlayerName && !config.ShowAccuracy && !config.ShowDelta)
		{
			HideWithoutUnbinding();
			return;
		}
		float num = (float)(_boundEntry.Accuracy * 100.0);
		float num2 = currentPlayerAccuracy * 100f;
		float num3 = num - num2;
		bool isOvertaken = num3 < 0f;
		float alpha = 1f;
		bool isLeftSide;
		if (config.DisplayMode == MarathonDisplayMode.RoadSignMarathon)
		{
			float groundZBase = config.GroundZBase;
			float num4 = groundZBase + num3 * config.DistanceScaleGround;
			float num5 = Mathf.Max(10f, config.GroundMaxDistance);
			if (num4 > num5 || num4 < 0.15f)
			{
				HideWithoutUnbinding();
				return;
			}
			float num6 = Mathf.Max(groundZBase + 1f, num5 - 4f);
			if (num4 > num6)
			{
				alpha = Mathf.Clamp01((num5 - num4) / Mathf.Max(0.5f, num5 - num6));
			}
			else if (num4 < groundZBase)
			{
				float num7 = Mathf.Max(0.5f, groundZBase - 0.15f);
				alpha = Mathf.Clamp01((num4 - 0.15f) / num7);
			}
			_currentTargetLocalPos = new Vector3(_boundEntry.AssignedGroundX, _boundEntry.AssignedGroundY, num4);
			_currentTargetLocalRot = Quaternion.Euler(_boundEntry.AssignedGroundRotationX, 0f, 0f);
			_rootTransform.localScale = Vector3.one;
			isLeftSide = _boundEntry.AssignedGroundX < 0f;
		}
		else
		{
			float num8 = num3 * config.DistanceScaleVertical;
			if (num8 > 1.8f || num8 < -1.8f)
			{
				HideWithoutUnbinding();
				return;
			}
			if (Mathf.Abs(num8) > 1.3499999f)
			{
				alpha = Mathf.Clamp01((1.8f - Mathf.Abs(num8)) / 0.45f);
			}
			_currentTargetLocalPos = new Vector3(_boundEntry.AssignedVerticalBarXOffset, num8, 0f);
			_currentTargetLocalRot = Quaternion.identity;
			_rootTransform.localScale = new Vector3(0.38f, 0.38f, 0.38f);
			isLeftSide = _boundEntry.AssignedVerticalBarXOffset < 0f;
		}
		if (!_rootObject.activeSelf)
		{
			_rootObject.SetActive(value: true);
		}
		if (!_hasValidPosition)
		{
			_rootTransform.localPosition = _currentTargetLocalPos;
			_rootTransform.localRotation = _currentTargetLocalRot;
			_hasValidPosition = true;
		}
		else
		{
			float t = 1f - Mathf.Exp(-7.5f * deltaTime);
			_rootTransform.localPosition = Vector3.Lerp(_rootTransform.localPosition, _currentTargetLocalPos, t);
			_rootTransform.localRotation = Quaternion.Slerp(_rootTransform.localRotation, _currentTargetLocalRot, t);
		}
		UpdateTextLayoutAndColor(num, num3, isOvertaken, alpha, isLeftSide, config);
	}

	private void UpdateTextLayoutAndColor(float rivalAccPercent, float deltaPercent, bool isOvertaken, float alpha, bool isLeftSide, PluginConfig config)
	{
		Sprite sprite = (((object)_boundEntry.CachedAvatar != null) ? _boundEntry.CachedAvatar : _fallbackCachedAvatar);
		bool flag = config.ShowAvatar && (object)sprite != null;
		if (flag && (object)((Image)(object)_avatarImage).sprite != sprite)
		{
			((Image)(object)_avatarImage).sprite = sprite;
		}
		int num = (config.ShowRank ? 1 : 0) | (flag ? 2 : 0) | (config.ShowPlayerName ? 4 : 0) | (config.ShowAccuracy ? 8 : 0) | (config.ShowDelta ? 16 : 0) | (isLeftSide ? 32 : 0);
		int num2 = Mathf.RoundToInt(deltaPercent * 100f);
		if (num2 != _lastFormattedDeltaCent || num != _lastDisplayMask)
		{
			_lastFormattedDeltaCent = num2;
			_lastDisplayMask = num;
			RebuildTwoRowLayout(rivalAccPercent, deltaPercent, flag, isLeftSide, config);
		}
		Color color = ((!isOvertaken) ? (_boundEntry.IsSelf ? SelfBestColor : NormalRivalColor) : (_boundEntry.IsSelf ? OvertakenSelfColor : OvertakenHighlightColor));
		color.a = alpha;
		Color color2 = new Color(0f, 0f, 0f, alpha * 0.95f);
		if (_upperTextObj.activeSelf)
		{
			_upperTextMesh.color = color;
			_upperShadowTextMesh.color = color2;
		}
		if (_lowerTextObj.activeSelf)
		{
			_lowerTextMesh.color = color;
			_lowerShadowTextMesh.color = color2;
		}
		if (_avatarObject.activeSelf)
		{
			Color white = Color.white;
			white.a = alpha;
			((Graphic)(object)_avatarImage).color = white;
		}
	}

	private void RebuildTwoRowLayout(float rivalAccPercent, float deltaPercent, bool showAvatar, bool isLeftSide, PluginConfig config)
	{
		string text = BuildUpperNameString(config.ShowPlayerName, isLeftSide);
		string text2 = BuildLowerStatsString(config.ShowRank, config.ShowAccuracy, config.ShowDelta, rivalAccPercent, deltaPercent, isLeftSide);
		bool num = !string.IsNullOrEmpty(text);
		bool flag = !string.IsNullOrEmpty(text2);
		float num2 = 0f;
		if (showAvatar)
		{
			if (!_avatarObject.activeSelf)
			{
				_avatarObject.SetActive(value: true);
			}
			if (isLeftSide)
			{
				_avatarRect.localPosition = new Vector3(-18f, 0f, 0f);
				_avatarRect.localRotation = Quaternion.Euler(0f, -15f, 0f);
				num2 = -0.44f;
			}
			else
			{
				_avatarRect.localPosition = new Vector3(18f, 0f, 0f);
				_avatarRect.localRotation = Quaternion.Euler(0f, -15f, 0f);
				num2 = 0.44f;
			}
		}
		else
		{
			if (_avatarObject.activeSelf)
			{
				_avatarObject.SetActive(value: false);
			}
			num2 = 0f;
		}
		float localY = ((num && flag) ? 0.12f : 0f);
		float localY2 = ((num && flag) ? (-0.12f) : 0f);
		if (num)
		{
			if (!_upperTextObj.activeSelf)
			{
				_upperTextObj.SetActive(value: true);
			}
			if (!_upperShadowObj.activeSelf)
			{
				_upperShadowObj.SetActive(value: true);
			}
			_upperTextMesh.text = text;
			_upperShadowTextMesh.text = text;
			ApplyLineAlignmentAndPosition(_upperTextMesh, _upperShadowTextMesh, isLeftSide, num2, localY);
		}
		else
		{
			if (_upperTextObj.activeSelf)
			{
				_upperTextObj.SetActive(value: false);
			}
			if (_upperShadowObj.activeSelf)
			{
				_upperShadowObj.SetActive(value: false);
			}
		}
		if (flag)
		{
			if (!_lowerTextObj.activeSelf)
			{
				_lowerTextObj.SetActive(value: true);
			}
			if (!_lowerShadowObj.activeSelf)
			{
				_lowerShadowObj.SetActive(value: true);
			}
			_lowerTextMesh.text = text2;
			_lowerShadowTextMesh.text = text2;
			ApplyLineAlignmentAndPosition(_lowerTextMesh, _lowerShadowTextMesh, isLeftSide, num2, localY2);
		}
		else
		{
			if (_lowerTextObj.activeSelf)
			{
				_lowerTextObj.SetActive(value: false);
			}
			if (_lowerShadowObj.activeSelf)
			{
				_lowerShadowObj.SetActive(value: false);
			}
		}
	}

	private static void ApplyLineAlignmentAndPosition(CurvedTextMeshPro mainTmp, CurvedTextMeshPro shadowTmp, bool rightAligned, float anchorX, float localY)
	{
		TextAlignmentOptions alignment = (rightAligned ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft);
		float halfWidthUi = TextContainerWidthUi * 0.5f;
		float anchorXUi = anchorX * UiUnitsPerMeter;
		float posXUi = (rightAligned ? (anchorXUi - halfWidthUi) : (anchorXUi + halfWidthUi));
		float posYUi = localY * UiUnitsPerMeter;
		mainTmp.alignment = alignment;
		mainTmp.rectTransform.anchoredPosition3D = new Vector3(posXUi, posYUi, 0f);
		shadowTmp.alignment = alignment;
		shadowTmp.rectTransform.anchoredPosition3D = new Vector3(posXUi + ShadowOffsetUi.x, posYUi + ShadowOffsetUi.y, 0f);
	}

	private string BuildUpperNameString(bool showPlayerName, bool isLeftSide)
	{
		if (!showPlayerName)
		{
			return string.Empty;
		}
		string text = TruncateName(_boundEntry.PlayerName, 16);
		if (!_boundEntry.IsSelf)
		{
			return text;
		}
		if (!isLeftSide)
		{
			return "[PB] " + text;
		}
		return text + " [PB]";
	}

	private string BuildLowerStatsString(bool showRank, bool showAccuracy, bool showDelta, float rivalAccPercent, float deltaPercent, bool isLeftSide)
	{
		if (!showRank && !showAccuracy && !showDelta)
		{
			return string.Empty;
		}
		_sb.Clear();
		string value = ((deltaPercent >= 0f) ? "+" : "");
		if (isLeftSide)
		{
			if (showDelta)
			{
				_sb.Append('(').Append(value).Append(deltaPercent.ToString("F2", CultureInfo.InvariantCulture))
					.Append("%)");
			}
			if (showAccuracy)
			{
				if (_sb.Length > 0)
				{
					_sb.Append(' ');
				}
				_sb.Append(rivalAccPercent.ToString("F2", CultureInfo.InvariantCulture)).Append('%');
			}
			if (showRank)
			{
				if (_sb.Length > 0)
				{
					_sb.Append(' ');
				}
				_sb.Append('#').Append(_boundEntry.Rank);
			}
		}
		else
		{
			if (showRank)
			{
				_sb.Append('#').Append(_boundEntry.Rank);
			}
			if (showAccuracy)
			{
				if (_sb.Length > 0)
				{
					_sb.Append(' ');
				}
				_sb.Append(rivalAccPercent.ToString("F2", CultureInfo.InvariantCulture)).Append('%');
			}
			if (showDelta)
			{
				if (_sb.Length > 0)
				{
					_sb.Append(' ');
				}
				_sb.Append('(').Append(value).Append(deltaPercent.ToString("F2", CultureInfo.InvariantCulture))
					.Append("%)");
			}
		}
		return _sb.ToString();
	}

	private static string TruncateName(string name, int maxLength)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "Player";
		}
		if (name.Length > maxLength)
		{
			return name.Substring(0, maxLength) + "..";
		}
		return name;
	}

	private void HideWithoutUnbinding()
	{
		if (_rootObject.activeSelf)
		{
			_rootObject.SetActive(value: false);
		}
		_hasValidPosition = false;
	}

	public void Hide()
	{
		_boundEntry = null;
		_fallbackCachedAvatar = null;
		_lastFormattedDeltaCent = int.MinValue;
		_lastDisplayMask = -1;
		_hasValidPosition = false;
		if (_rootObject != null && _rootObject.activeSelf)
		{
			_rootObject.SetActive(value: false);
		}
	}

	public void SetParentContainer(Transform parent)
	{
		if (_rootTransform != null && parent != null && _rootTransform.parent != parent)
		{
			_rootTransform.SetParent(parent, worldPositionStays: false);
			_hasValidPosition = false;
		}
	}

	public void Destroy()
	{
		if (_rootObject != null)
		{
			UnityEngine.Object.Destroy(_rootObject);
		}
	}
}
