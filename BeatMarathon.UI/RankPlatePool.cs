using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeatMarathon.Models;
using BeatMarathon.Services;
using BeatSaberMarkupLanguage;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace BeatMarathon.UI;

public class RankPlatePool : IInitializable, IDisposable
{
	public const int MaxPoolSize = 24;

	private readonly PluginConfig _config;

	private readonly AvatarCacheService _avatarCacheService;

	private GameObject _poolRootObject;

	private Transform _groundContainerTransform;

	private Transform _verticalBarContainerTransform;

	private GameObject _verticalBarCenterMarkerObj;

	private CurvedTextMeshPro _verticalBarCenterMarkerText;

	private int _lastCenterMarkerAccCent = int.MinValue;

	private readonly List<RankPlateView> _plates = new List<RankPlateView>(24);

	private readonly bool[] _plateKeepFlags = new bool[24];

	private readonly bool[] _entryAssignedFlags = new bool[24];

	private TMP_FontAsset _sharedFont;

	private Material _sharedOutlineMaterial;

	private Material _sharedSpriteMaterial;

	private bool _ownsSharedSpriteMaterial;

	public RankPlatePool(PluginConfig config, AvatarCacheService avatarCacheService)
	{
		_config = config;
		_avatarCacheService = avatarCacheService;
	}

	public void Initialize()
	{
		_poolRootObject = new GameObject("BeatMarathon_PoolRoot");
		_groundContainerTransform = new GameObject("GroundModeContainer").transform;
		_groundContainerTransform.SetParent(_poolRootObject.transform, worldPositionStays: false);
		_groundContainerTransform.localPosition = Vector3.zero;
		_groundContainerTransform.localRotation = Quaternion.identity;
		_verticalBarContainerTransform = new GameObject("VerticalBarModeContainer").transform;
		_verticalBarContainerTransform.SetParent(_poolRootObject.transform, worldPositionStays: false);
		ApplyVerticalBarRootTransform();
		PrepareSharedMaterials();
		CreateVerticalBarCenterMarker();
		Transform parentTransform = ((_config.DisplayMode == MarathonDisplayMode.RoadSignMarathon) ? _groundContainerTransform : _verticalBarContainerTransform);
		for (int i = 0; i < 24; i++)
		{
			RankPlateView item = new RankPlateView(parentTransform, _sharedFont, _sharedOutlineMaterial, _sharedSpriteMaterial, i);
			_plates.Add(item);
		}
		SetAllVisible(visible: false);
	}

	private void PrepareSharedMaterials()
	{
		try
		{
			Material baseUiMaterial = null;
			GameObject probeGo = new GameObject("BeatMarathon_FontProbe", typeof(RectTransform));
			try
			{
				probeGo.SetActive(value: false);
#pragma warning disable CS0618
				CurvedTextMeshPro template = BeatSaberUI.CreateText<CurvedTextMeshPro>((RectTransform)probeGo.transform, string.Empty, Vector2.zero, new Vector2(10f, 10f));
#pragma warning restore CS0618
				if ((object)template != null)
				{
					_sharedFont = template.font;
					baseUiMaterial = template.fontSharedMaterial;
				}
			}
			catch (Exception ex)
			{
				Plugin.Log?.Warn("[RankPlatePool] BeatSaberUI font probe warning: " + ex.Message);
			}
			finally
			{
				UnityEngine.Object.Destroy(probeGo);
			}

			if (_sharedFont == null)
			{
				_sharedFont = BeatSaberUI.MainTextFont;
			}
			if (_sharedFont == null)
			{
				TMP_FontAsset[] source = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
				_sharedFont = source.FirstOrDefault((TMP_FontAsset f) => f != null && f.name.IndexOf("Teko-Medium SDF No Glow", StringComparison.OrdinalIgnoreCase) >= 0)
					?? source.FirstOrDefault((TMP_FontAsset f) => f != null && f.name.IndexOf("Teko-Medium", StringComparison.OrdinalIgnoreCase) >= 0)
					?? source.FirstOrDefault((TMP_FontAsset f) => f != null);
			}

			Material sourceMat = baseUiMaterial ?? _sharedFont?.material;
			if (sourceMat != null)
			{
				_sharedOutlineMaterial = new Material(sourceMat);
				_sharedOutlineMaterial.name = "RankMarathon_SharedTMPOutline";
				if (_sharedOutlineMaterial.HasProperty("_OutlineWidth"))
				{
					_sharedOutlineMaterial.EnableKeyword("OUTLINE_ON");
					_sharedOutlineMaterial.SetFloat("_OutlineWidth", 0.2f);
				}
				if (_sharedOutlineMaterial.HasProperty("_OutlineColor"))
				{
					_sharedOutlineMaterial.SetColor("_OutlineColor", Color.black);
				}
			}
			Material material = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault((Material m) => m != null && string.Equals(m.name, "UINoGlow", StringComparison.OrdinalIgnoreCase));
			if (material != null)
			{
				_sharedSpriteMaterial = material;
				_ownsSharedSpriteMaterial = false;
				return;
			}
			Shader shader = Shader.Find("Custom/UI") ?? Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
			if (shader != null)
			{
				_sharedSpriteMaterial = new Material(shader);
				_sharedSpriteMaterial.name = "RankMarathon_SharedSpriteMat";
				_ownsSharedSpriteMaterial = true;
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.Warn("[RankPlatePool] PrepareSharedMaterials warning: " + ex.Message);
		}
	}

	private void CreateVerticalBarCenterMarker()
	{
		_verticalBarCenterMarkerObj = new GameObject("VerticalBar_YouMarker", typeof(RectTransform), typeof(Canvas), typeof(CurvedCanvasSettings));
		_verticalBarCenterMarkerObj.layer = 5;
		RectTransform markerRect = (RectTransform)_verticalBarCenterMarkerObj.transform;
		markerRect.SetParent(_verticalBarContainerTransform, worldPositionStays: false);
		markerRect.sizeDelta = new Vector2(600f, 50f);
		markerRect.pivot = new Vector2(0.5f, 0.5f);
		markerRect.localPosition = Vector3.zero;
		markerRect.localRotation = Quaternion.identity;
		markerRect.localScale = new Vector3(0.0042f, 0.0042f, 0.0042f);

		Canvas markerCanvas = _verticalBarCenterMarkerObj.GetComponent<Canvas>();
		markerCanvas.renderMode = RenderMode.WorldSpace;
		markerCanvas.sortingOrder = 11;
		markerCanvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;

		CurvedCanvasSettings curved = _verticalBarCenterMarkerObj.GetComponent<CurvedCanvasSettings>();
		curved.SetRadius(0f);

#pragma warning disable CS0618
		_verticalBarCenterMarkerText = BeatSaberUI.CreateText<CurvedTextMeshPro>(markerRect, "─── ◄ YOU ► ───", Vector2.zero, new Vector2(600f, 50f));
#pragma warning restore CS0618
		if (_sharedFont != null)
		{
			_verticalBarCenterMarkerText.font = _sharedFont;
		}
		if (_sharedOutlineMaterial != null)
		{
			_verticalBarCenterMarkerText.fontSharedMaterial = _sharedOutlineMaterial;
		}
		_verticalBarCenterMarkerText.fontSize = 25f;
		_verticalBarCenterMarkerText.alignment = TextAlignmentOptions.Center;
		_verticalBarCenterMarkerText.enableWordWrapping = false;
		_verticalBarCenterMarkerText.overflowMode = TextOverflowModes.Overflow;
		_verticalBarCenterMarkerText.raycastTarget = false;
		_verticalBarCenterMarkerText.color = new Color(1f, 0.92f, 0.25f, 1f);
		_verticalBarCenterMarkerText.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
		_verticalBarCenterMarkerText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
		_verticalBarCenterMarkerText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
		_verticalBarCenterMarkerText.rectTransform.localScale = Vector3.one;
		_verticalBarCenterMarkerText.rectTransform.localRotation = Quaternion.identity;
		_verticalBarCenterMarkerObj.SetActive(value: false);
	}

	public void ApplyVerticalBarRootTransform()
	{
		if (!(_verticalBarContainerTransform == null))
		{
			_verticalBarContainerTransform.localPosition = _config.GetVerticalBarPosition();
			_verticalBarContainerTransform.localRotation = _config.GetVerticalBarRotation();
			float num = Mathf.Clamp(_config.VerticalBarScale, 0.2f, 4f);
			_verticalBarContainerTransform.localScale = new Vector3(num, num, num);
			Transform parentContainer = ((_config.DisplayMode == MarathonDisplayMode.RoadSignMarathon) ? _groundContainerTransform : _verticalBarContainerTransform);
			for (int i = 0; i < _plates.Count; i++)
			{
				_plates[i].Hide();
				_plates[i].SetParentContainer(parentContainer);
			}
		}
	}

	public void AssignTargetsToPool(IReadOnlyList<BeatMarathon.Models.LeaderboardEntry> targetEntries)
	{
		if (targetEntries == null || targetEntries.Count == 0)
		{
			for (int i = 0; i < _plates.Count; i++)
			{
				_plates[i].Hide();
			}
			return;
		}
		int count = _plates.Count;
		int num = Mathf.Min(count, targetEntries.Count);
		for (int j = 0; j < count; j++)
		{
			_plateKeepFlags[j] = false;
		}
		for (int k = 0; k < num; k++)
		{
			_entryAssignedFlags[k] = false;
		}
		for (int l = 0; l < num; l++)
		{
			BeatMarathon.Models.LeaderboardEntry leaderboardEntry = targetEntries[l];
			for (int m = 0; m < count; m++)
			{
				if (!_plateKeepFlags[m])
				{
					BeatMarathon.Models.LeaderboardEntry boundEntry = _plates[m].BoundEntry;
					if (boundEntry != null && IsSameEntry(boundEntry, leaderboardEntry))
					{
						_plateKeepFlags[m] = true;
						_entryAssignedFlags[l] = true;
						BindEntryWithAvatar(_plates[m], leaderboardEntry);
						break;
					}
				}
			}
		}
		for (int n = 0; n < count; n++)
		{
			if (!_plateKeepFlags[n])
			{
				_plates[n].Hide();
			}
		}
		int num2 = 0;
		for (int num3 = 0; num3 < num; num3++)
		{
			if (!_entryAssignedFlags[num3])
			{
				for (; num2 < count && _plateKeepFlags[num2]; num2++)
				{
				}
				if (num2 >= count)
				{
					break;
				}
				_plateKeepFlags[num2] = true;
				BindEntryWithAvatar(_plates[num2], targetEntries[num3]);
				num2++;
			}
		}
	}

	private static bool IsSameEntry(BeatMarathon.Models.LeaderboardEntry a, BeatMarathon.Models.LeaderboardEntry b)
	{
		if (a == b)
		{
			return true;
		}
		if (a == null || b == null)
		{
			return false;
		}
		if (!string.IsNullOrEmpty(a.PlayerId) && !string.IsNullOrEmpty(b.PlayerId))
		{
			return string.Equals(a.PlayerId, b.PlayerId, StringComparison.Ordinal);
		}
		if (a.Rank == b.Rank)
		{
			return string.Equals(a.PlayerName, b.PlayerName, StringComparison.Ordinal);
		}
		return false;
	}

	private void BindEntryWithAvatar(RankPlateView plate, BeatMarathon.Models.LeaderboardEntry entry)
	{
		Sprite sprite = entry.CachedAvatar;
		if ((object)sprite == null && !string.IsNullOrEmpty(entry.PlayerId) && _avatarCacheService.TryGetAvatar(entry.PlayerId, out var sprite2))
		{
			entry.CachedAvatar = sprite2;
			sprite = sprite2;
		}
		plate.BindEntry(entry, sprite);
	}

	public void UpdateActivePlates(float currentAccuracy, int currentRank, float deltaTime)
	{
		if (_config.DisplayMode == MarathonDisplayMode.VerticalBar)
		{
			if (_verticalBarCenterMarkerObj != null)
			{
				if (!_verticalBarCenterMarkerObj.activeSelf)
				{
					_verticalBarCenterMarkerObj.SetActive(value: true);
				}
				int num = Mathf.RoundToInt(currentAccuracy * 10000f);
				if (num != _lastCenterMarkerAccCent)
				{
					_lastCenterMarkerAccCent = num;
					_verticalBarCenterMarkerText.text = string.Format(CultureInfo.InvariantCulture, "─── ◄ YOU #{0} ({1:F2}%) ► ───", currentRank, currentAccuracy * 100f);
				}
			}
		}
		else if (_verticalBarCenterMarkerObj != null && _verticalBarCenterMarkerObj.activeSelf)
		{
			_verticalBarCenterMarkerObj.SetActive(value: false);
		}
		for (int i = 0; i < _plates.Count; i++)
		{
			BeatMarathon.Models.LeaderboardEntry boundEntry = _plates[i].BoundEntry;
			if (boundEntry != null)
			{
				if ((object)boundEntry.CachedAvatar == null && !string.IsNullOrEmpty(boundEntry.PlayerId) && _avatarCacheService.TryGetAvatar(boundEntry.PlayerId, out var sprite))
				{
					boundEntry.CachedAvatar = sprite;
				}
				_plates[i].UpdatePlateTransformAndVisuals(currentAccuracy, _config, deltaTime);
			}
		}
	}

	public void SetAllVisible(bool visible)
	{
		if (!visible)
		{
			for (int i = 0; i < _plates.Count; i++)
			{
				_plates[i].Hide();
			}
			if (_verticalBarCenterMarkerObj != null && _verticalBarCenterMarkerObj.activeSelf)
			{
				_verticalBarCenterMarkerObj.SetActive(value: false);
			}
		}
	}

	public void Dispose()
	{
		for (int i = 0; i < _plates.Count; i++)
		{
			_plates[i].Destroy();
		}
		_plates.Clear();
		if (_poolRootObject != null)
		{
			UnityEngine.Object.Destroy(_poolRootObject);
			_poolRootObject = null;
		}
		if (_sharedOutlineMaterial != null)
		{
			UnityEngine.Object.Destroy(_sharedOutlineMaterial);
			_sharedOutlineMaterial = null;
		}
		if (_ownsSharedSpriteMaterial && _sharedSpriteMaterial != null)
		{
			UnityEngine.Object.Destroy(_sharedSpriteMaterial);
		}
		_sharedSpriteMaterial = null;
	}
}
