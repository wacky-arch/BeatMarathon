using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RealtimeRankMarathon.Models;
using RealtimeRankMarathon.Services;
using TMPro;
using UnityEngine;
using Zenject;

namespace RealtimeRankMarathon.UI
{
    using LeaderboardEntry = RealtimeRankMarathon.Models.LeaderboardEntry;

    public class RankPlatePool : IInitializable, IDisposable
    {
        public const int MaxPoolSize = 24; // 前方最大20人 + 後方3人 の最大24個プール

        private readonly PluginConfig _config;
        private readonly AvatarCacheService _avatarCacheService;

        private GameObject _poolRootObject;
        private Transform _groundContainerTransform;
        private Transform _verticalBarContainerTransform;

        // 縦型バーモード用の「自分（YOU）」固定センターマーカー
        private GameObject _verticalBarCenterMarkerObj;
        private TextMeshPro _verticalBarCenterMarkerText;
        private int _lastCenterMarkerAccCent = int.MinValue;

        private readonly List<RankPlateView> _plates = new List<RankPlateView>(MaxPoolSize);

        private TMP_FontAsset _sharedFont;
        private Material _sharedOutlineMaterial;
        private Material _sharedSpriteMaterial;
        private bool _ownsSharedSpriteMaterial;

        public RankPlatePool(PluginConfig config, AvatarCacheService avatarCacheService)
        {
            _config = config;
            _avatarCacheService = avatarCacheService;
        }

        /// <summary>
        /// 曲開始前にオブジェクトプール（8個のプレート＋縦バー用YOUマーカー）を一括生成する。
        /// プレイ中の Instantiate / Destroy は一切行わない。
        /// </summary>
        public void Initialize()
        {
            _poolRootObject = new GameObject("RealtimeRankMarathon_PoolRoot");

            _groundContainerTransform = new GameObject("GroundModeContainer").transform;
            _groundContainerTransform.SetParent(_poolRootObject.transform, false);
            _groundContainerTransform.localPosition = Vector3.zero;
            _groundContainerTransform.localRotation = Quaternion.identity;

            _verticalBarContainerTransform = new GameObject("VerticalBarModeContainer").transform;
            _verticalBarContainerTransform.SetParent(_poolRootObject.transform, false);
            ApplyVerticalBarRootTransform();

            PrepareSharedMaterials();
            CreateVerticalBarCenterMarker();

            Transform initialParent = _config.DisplayMode == MarathonDisplayMode.RoadSignMarathon
                ? _groundContainerTransform
                : _verticalBarContainerTransform;

            for (int i = 0; i < MaxPoolSize; i++)
            {
                var plate = new RankPlateView(
                    initialParent,
                    _sharedFont,
                    _sharedOutlineMaterial,
                    _sharedSpriteMaterial,
                    i);
                _plates.Add(plate);
            }

            SetAllVisible(false);
        }

        private void PrepareSharedMaterials()
        {
            try
            {
                // ゲーム内の既存 TextMeshPro フォントを取得（VRでのブルーム白飛びを防ぐため No Glow 系を優先）
                var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                _sharedFont = allFonts.FirstOrDefault(f => f != null && f.name.IndexOf("Teko-Medium SDF No Glow", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? allFonts.FirstOrDefault(f => f != null && f.name.IndexOf("Teko-Medium", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? allFonts.FirstOrDefault(f => f != null);

                if (_sharedFont != null && _sharedFont.material != null)
                {
                    _sharedOutlineMaterial = new Material(_sharedFont.material);
                    _sharedOutlineMaterial.name = "RankMarathon_SharedTMPOutline";
                    if (_sharedOutlineMaterial.HasProperty("_OutlineWidth"))
                    {
                        _sharedOutlineMaterial.EnableKeyword("OUTLINE_ON");
                        _sharedOutlineMaterial.SetFloat("_OutlineWidth", 0.20f);
                    }
                    if (_sharedOutlineMaterial.HasProperty("_OutlineColor"))
                    {
                        _sharedOutlineMaterial.SetColor("_OutlineColor", Color.black);
                    }
                }

                // VR Single Pass Instanced 対応かつブルーム白飛びを防ぐため、ゲーム内の UINoGlow マテリアルを優先使用
                Material uiNoGlow = Resources.FindObjectsOfTypeAll<Material>()
                    .FirstOrDefault(m => m != null && string.Equals(m.name, "UINoGlow", StringComparison.OrdinalIgnoreCase));

                if (uiNoGlow != null)
                {
                    _sharedSpriteMaterial = uiNoGlow;
                    _ownsSharedSpriteMaterial = false;
                }
                else
                {
                    Shader spriteShader = Shader.Find("Custom/UI") ?? Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
                    if (spriteShader != null)
                    {
                        _sharedSpriteMaterial = new Material(spriteShader);
                        _sharedSpriteMaterial.name = "RankMarathon_SharedSpriteMat";
                        _ownsSharedSpriteMaterial = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[RankPlatePool] PrepareSharedMaterials warning: {ex.Message}");
            }
        }

        private void CreateVerticalBarCenterMarker()
        {
            _verticalBarCenterMarkerObj = new GameObject("VerticalBar_YouMarker");
            _verticalBarCenterMarkerObj.transform.SetParent(_verticalBarContainerTransform, false);
            _verticalBarCenterMarkerObj.transform.localPosition = Vector3.zero;
            _verticalBarCenterMarkerObj.transform.localRotation = Quaternion.identity;
            _verticalBarCenterMarkerObj.transform.localScale = new Vector3(0.42f, 0.42f, 0.42f);

            _verticalBarCenterMarkerText = _verticalBarCenterMarkerObj.AddComponent<TextMeshPro>();
            if (_sharedFont != null)
            {
                _verticalBarCenterMarkerText.font = _sharedFont;
            }
            if (_sharedOutlineMaterial != null)
            {
                _verticalBarCenterMarkerText.fontSharedMaterial = _sharedOutlineMaterial;
            }

            _verticalBarCenterMarkerText.fontSize = 2.5f;
            _verticalBarCenterMarkerText.alignment = TextAlignmentOptions.Center;
            _verticalBarCenterMarkerText.enableWordWrapping = false;
            _verticalBarCenterMarkerText.overflowMode = TextOverflowModes.Overflow;
            _verticalBarCenterMarkerText.color = new Color(1.0f, 0.92f, 0.25f, 1.0f);
            _verticalBarCenterMarkerText.text = "─── ◄ YOU ► ───";
            _verticalBarCenterMarkerObj.SetActive(false);
        }

        public void ApplyVerticalBarRootTransform()
        {
            if (_verticalBarContainerTransform == null) return;

            _verticalBarContainerTransform.localPosition = _config.GetVerticalBarPosition();
            _verticalBarContainerTransform.localRotation = _config.GetVerticalBarRotation();
            float scale = Mathf.Clamp(_config.VerticalBarScale, 0.2f, 4.0f);
            _verticalBarContainerTransform.localScale = new Vector3(scale, scale, scale);

            Transform activeParent = _config.DisplayMode == MarathonDisplayMode.RoadSignMarathon
                ? _groundContainerTransform
                : _verticalBarContainerTransform;

            for (int i = 0; i < _plates.Count; i++)
            {
                _plates[i].Hide();
                _plates[i].SetParentContainer(activeParent);
            }
        }

        /// <summary>
        /// 抽出された表示対象ライバル（最大8人）をプール内のプレートへ割り当てる
        /// </summary>
        public void AssignTargetsToPool(IReadOnlyList<LeaderboardEntry> targetEntries)
        {
            if (targetEntries == null || targetEntries.Count == 0)
            {
                for (int i = 0; i < _plates.Count; i++)
                {
                    _plates[i].Hide();
                }
                return;
            }

            int count = Mathf.Min(_plates.Count, targetEntries.Count);
            for (int i = 0; i < count; i++)
            {
                var entry = targetEntries[i];
                Sprite cachedIcon = entry.CachedAvatar;
                if (cachedIcon == null && !string.IsNullOrEmpty(entry.PlayerId))
                {
                    if (_avatarCacheService.TryGetAvatar(entry.PlayerId, out Sprite found))
                    {
                        entry.CachedAvatar = found;
                        cachedIcon = found;
                    }
                }

                _plates[i].BindEntry(entry, cachedIcon);
            }

            for (int i = count; i < _plates.Count; i++)
            {
                _plates[i].Hide();
            }
        }

        /// <summary>
        /// 毎フレーム全アクティブプレートの座標補間とフェード処理を更新する
        /// </summary>
        public void UpdateActivePlates(float currentAccuracy, int currentRank, float deltaTime)
        {
            if (_config.DisplayMode == MarathonDisplayMode.VerticalBar)
            {
                if (_verticalBarCenterMarkerObj != null)
                {
                    if (!_verticalBarCenterMarkerObj.activeSelf)
                    {
                        _verticalBarCenterMarkerObj.SetActive(true);
                    }

                    int accCent = Mathf.RoundToInt(currentAccuracy * 10000f);
                    if (accCent != _lastCenterMarkerAccCent)
                    {
                        _lastCenterMarkerAccCent = accCent;
                        _verticalBarCenterMarkerText.text = string.Format(
                            CultureInfo.InvariantCulture,
                            "─── ◄ YOU #{0} ({1:F2}%) ► ───",
                            currentRank,
                            currentAccuracy * 100.0f);
                    }
                }
            }
            else
            {
                if (_verticalBarCenterMarkerObj != null && _verticalBarCenterMarkerObj.activeSelf)
                {
                    _verticalBarCenterMarkerObj.SetActive(false);
                }
            }

            for (int i = 0; i < _plates.Count; i++)
            {
                var bound = _plates[i].BoundEntry;
                if (bound != null)
                {
                    if (ReferenceEquals(bound.CachedAvatar, null) && !string.IsNullOrEmpty(bound.PlayerId))
                    {
                        if (_avatarCacheService.TryGetAvatar(bound.PlayerId, out Sprite found))
                        {
                            bound.CachedAvatar = found;
                        }
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
                    _verticalBarCenterMarkerObj.SetActive(false);
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
}
