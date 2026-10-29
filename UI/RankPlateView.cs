using System;
using System.Globalization;
using System.Text;
using HMUI;
using RealtimeRankMarathon.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RealtimeRankMarathon.UI
{
    using LeaderboardEntry = RealtimeRankMarathon.Models.LeaderboardEntry;

    public class RankPlateView
    {
        private const float AvatarIconSize = 0.36f;
        private const float ElementGap = 0.08f;
        private const float TextContainerWidth = 8.0f;
        private const float UpperLineOffsetY = 0.12f;
        private const float LowerLineOffsetY = -0.12f;
        private static readonly Vector3 ShadowOffset = new Vector3(0.012f, -0.012f, 0.004f);

        private static readonly Color NormalRivalColor = Color.white;
        private static readonly Color SelfBestColor = new Color(0.20f, 0.90f, 1.00f, 1.0f);           // 自己ベスト識別用シアン
        private static readonly Color OvertakenHighlightColor = new Color(0.20f, 1.00f, 0.45f, 1.0f); // 追い抜き時グリーン
        private static readonly Color OvertakenSelfColor = new Color(1.00f, 0.85f, 0.20f, 1.0f);      // 自己ベスト追い抜き時ゴールド

        private readonly GameObject _rootObject;
        private readonly Transform _rootTransform;

        // アイコン画像表示用 (内側に配置。WorldSpace Canvas + HMUI.ImageView で UINoGlow シェーダーに完全対応)
        private readonly GameObject _avatarObject;
        private readonly RectTransform _avatarRect;
        private readonly ImageView _avatarImage;

        // 上段テキスト: プレイヤー名
        private readonly GameObject _upperTextObj;
        private readonly GameObject _upperShadowObj;
        private readonly TextMeshPro _upperTextMesh;
        private readonly TextMeshPro _upperShadowTextMesh;

        // 下段テキスト: 順位・精度・精度差
        private readonly GameObject _lowerTextObj;
        private readonly GameObject _lowerShadowObj;
        private readonly TextMeshPro _lowerTextMesh;
        private readonly TextMeshPro _lowerShadowTextMesh;

        private readonly StringBuilder _sb = new StringBuilder(64);

        private LeaderboardEntry _boundEntry;
        private Sprite _fallbackCachedAvatar;
        private int _lastFormattedDeltaCent = int.MinValue;
        private int _lastDisplayMask = -1;
        private bool _hasValidPosition;
        private Vector3 _currentTargetLocalPos;
        private Quaternion _currentTargetLocalRot;

        public LeaderboardEntry BoundEntry => _boundEntry;
        public bool IsActive => _rootObject.activeSelf;

        public RankPlateView(
            Transform parentTransform,
            TMP_FontAsset sharedFont,
            Material sharedOutlineMaterial,
            Material sharedUiNoGlowMaterial,
            int poolIndex)
        {
            _rootObject = new GameObject($"RankPlate_{poolIndex}");
            _rootTransform = _rootObject.transform;
            _rootTransform.SetParent(parentTransform, false);

            // 1. 丸型マスク済みアイコン表示用 WorldSpace Canvas + HMUI.ImageView
            // ※ AddComponent<Canvas>() / AddComponent<ImageView>() 実行後に RectTransform を取得・親設定する
            _avatarObject = new GameObject("AvatarIcon");
            _avatarObject.layer = 5; // UI Layer

            var avatarCanvas = _avatarObject.AddComponent<Canvas>();
            avatarCanvas.renderMode = RenderMode.WorldSpace;
            avatarCanvas.sortingOrder = 11;

            _avatarImage = _avatarObject.AddComponent<ImageView>();
            if (sharedUiNoGlowMaterial != null)
            {
                _avatarImage.material = sharedUiNoGlowMaterial;
            }
            _avatarImage.type = Image.Type.Simple;
            _avatarImage.raycastTarget = false;
            _avatarImage.preserveAspect = true;

            _avatarRect = _avatarImage.rectTransform;
            _avatarRect.SetParent(_rootTransform, false);
            _avatarRect.sizeDelta = Vector2.one;
            _avatarRect.anchorMin = new Vector2(0.5f, 0.5f);
            _avatarRect.anchorMax = new Vector2(0.5f, 0.5f);
            _avatarRect.pivot = new Vector2(0.5f, 0.5f);
            _avatarRect.localPosition = Vector3.zero;
            _avatarRect.localRotation = Quaternion.identity;
            _avatarRect.localScale = new Vector3(AvatarIconSize, AvatarIconSize, 1f);

            _avatarObject.SetActive(false);

            // 2. 上段 (プレイヤー名) 用 TextMeshPro (黒縁影 + メイン)
            _upperShadowObj = new GameObject("UpperNameShadow");
            _upperShadowTextMesh = _upperShadowObj.AddComponent<TextMeshPro>();
            _upperShadowTextMesh.rectTransform.SetParent(_rootTransform, false);
            ConfigureTmp(_upperShadowTextMesh, sharedFont, sharedOutlineMaterial, 2.3f, new Color(0f, 0f, 0f, 0.95f), 10);

            _upperTextObj = new GameObject("UpperNameText");
            _upperTextMesh = _upperTextObj.AddComponent<TextMeshPro>();
            _upperTextMesh.rectTransform.SetParent(_rootTransform, false);
            ConfigureTmp(_upperTextMesh, sharedFont, sharedOutlineMaterial, 2.3f, NormalRivalColor, 11);

            // 3. 下段 (順位・精度・精度差) 用 TextMeshPro (黒縁影 + メイン)
            _lowerShadowObj = new GameObject("LowerStatsShadow");
            _lowerShadowTextMesh = _lowerShadowObj.AddComponent<TextMeshPro>();
            _lowerShadowTextMesh.rectTransform.SetParent(_rootTransform, false);
            ConfigureTmp(_lowerShadowTextMesh, sharedFont, sharedOutlineMaterial, 2.0f, new Color(0f, 0f, 0f, 0.95f), 10);

            _lowerTextObj = new GameObject("LowerStatsText");
            _lowerTextMesh = _lowerTextObj.AddComponent<TextMeshPro>();
            _lowerTextMesh.rectTransform.SetParent(_rootTransform, false);
            ConfigureTmp(_lowerTextMesh, sharedFont, sharedOutlineMaterial, 2.0f, NormalRivalColor, 11);

            _rootObject.SetActive(false);
        }

        private static void ConfigureTmp(
            TextMeshPro tmp,
            TMP_FontAsset sharedFont,
            Material sharedMaterial,
            float fontSize,
            Color color,
            int sortingOrder)
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
            tmp.color = color;
            tmp.sortingOrder = sortingOrder;

            var rect = tmp.rectTransform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(TextContainerWidth, 0.5f);
            rect.localPosition = Vector3.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
        }

        /// <summary>
        /// プレートにライバルエントリを割り当てる（プレイ中のInstantiate/Destroyやテクスチャ生成は一切行わない）
        /// </summary>
        public void BindEntry(LeaderboardEntry entry, Sprite fallbackCachedAvatar)
        {
            if (entry == null)
            {
                Hide();
                return;
            }

            bool isSameEntry = ReferenceEquals(_boundEntry, entry) ||
                               (_boundEntry != null && string.Equals(_boundEntry.PlayerId, entry.PlayerId, StringComparison.Ordinal));

            _boundEntry = entry;
            _fallbackCachedAvatar = fallbackCachedAvatar;

            if (!isSameEntry)
            {
                _lastFormattedDeltaCent = int.MinValue;
                _lastDisplayMask = -1;
                _hasValidPosition = false;
            }
        }

        /// <summary>
        /// 毎フレームの座標計算・追い抜きハイライト・距離フェード・Lerp補間を実行する
        /// </summary>
        public void UpdatePlateTransformAndVisuals(
            float currentPlayerAccuracy,
            PluginConfig config,
            float deltaTime)
        {
            if (_boundEntry == null)
            {
                Hide();
                return;
            }

            // いずれの表示項目もOFFの場合は非表示
            if (!config.ShowRank && !config.ShowAvatar && !config.ShowPlayerName && !config.ShowAccuracy && !config.ShowDelta)
            {
                HideWithoutUnbinding();
                return;
            }

            // Δ% = (ライバルの精度% - 自分の現在精度%)
            float rivalAccPercent = (float)(_boundEntry.Accuracy * 100.0);
            float myAccPercent = currentPlayerAccuracy * 100.0f;
            float deltaPercent = rivalAccPercent - myAccPercent;
            bool isOvertaken = deltaPercent < 0f;

            float alpha = 1.0f;
            bool isLeftSide;

            if (config.DisplayMode == MarathonDisplayMode.RoadSignMarathon)
            {
                // モードA：地面・交通標識タイプ（マラソン型）
                // Z = Z_base + (Δ% * DistanceScale_Ground)
                float zBase = config.GroundZBase;
                float zPos = zBase + (deltaPercent * config.DistanceScaleGround);

                // Z > 20.0m の遠方、または完全に後方へ流れ切った場合 (Z < 0.15m) は非表示
                if (zPos > 20.0f || zPos < 0.15f)
                {
                    HideWithoutUnbinding();
                    return;
                }

                // 遠方フェードイン (16m〜20m) および 追い抜き後の後方フェードアウト (Z_base〜0.15m)
                if (zPos > 16.0f)
                {
                    alpha = Mathf.Clamp01((20.0f - zPos) / 4.0f);
                }
                else if (zPos < zBase)
                {
                    float fadeRange = Mathf.Max(0.5f, zBase - 0.15f);
                    alpha = Mathf.Clamp01((zPos - 0.15f) / fadeRange);
                }

                // Slot 0〜3 で事前割り当てされた X, Y, Rotation X を適用
                _currentTargetLocalPos = new Vector3(_boundEntry.AssignedGroundX, _boundEntry.AssignedGroundY, zPos);
                _currentTargetLocalRot = Quaternion.Euler(_boundEntry.AssignedGroundRotationX, 0f, 0f);
                _rootTransform.localScale = Vector3.one;

                // 左側レーン (Slot 0: 左外側, Slot 2: 左内側) は X < 0 なので右詰め（内側にアイコン、左外側へテキスト）
                isLeftSide = _boundEntry.AssignedGroundX < 0f;
            }
            else
            {
                // モードB：縦型バータイプ（メーター型）
                // Y = Y_base(0) + (Δ% * DistanceScale_Vertical)
                float yPos = deltaPercent * config.DistanceScaleVertical;
                const float maxVerticalSpan = 1.8f;

                if (yPos > maxVerticalSpan || yPos < -maxVerticalSpan)
                {
                    HideWithoutUnbinding();
                    return;
                }

                if (Mathf.Abs(yPos) > maxVerticalSpan * 0.75f)
                {
                    alpha = Mathf.Clamp01((maxVerticalSpan - Mathf.Abs(yPos)) / (maxVerticalSpan * 0.25f));
                }

                // 縦型バーモード時は角度は変えず、Slot 0〜3 に応じて X軸（左右）にのみオフセットを適用
                _currentTargetLocalPos = new Vector3(_boundEntry.AssignedVerticalBarXOffset, yPos, 0f);
                _currentTargetLocalRot = Quaternion.identity;
                _rootTransform.localScale = new Vector3(0.38f, 0.38f, 0.38f);

                isLeftSide = _boundEntry.AssignedVerticalBarXOffset < 0f;
            }

            if (!_rootObject.activeSelf)
            {
                _rootObject.SetActive(true);
            }

            // 初回表示時はスナップ、以降は Vector3.Lerp で滑らかに補間
            if (!_hasValidPosition)
            {
                _rootTransform.localPosition = _currentTargetLocalPos;
                _rootTransform.localRotation = _currentTargetLocalRot;
                _hasValidPosition = true;
            }
            else
            {
                float lerpFactor = Mathf.Clamp01(deltaTime * 12.0f);
                _rootTransform.localPosition = Vector3.Lerp(_rootTransform.localPosition, _currentTargetLocalPos, lerpFactor);
                _rootTransform.localRotation = Quaternion.Slerp(_rootTransform.localRotation, _currentTargetLocalRot, lerpFactor);
            }

            UpdateTextLayoutAndColor(rivalAccPercent, deltaPercent, isOvertaken, alpha, isLeftSide, config);
        }

        private void UpdateTextLayoutAndColor(
            float rivalAccPercent,
            float deltaPercent,
            bool isOvertaken,
            float alpha,
            bool isLeftSide,
            PluginConfig config)
        {
            Sprite icon = !ReferenceEquals(_boundEntry.CachedAvatar, null)
                ? _boundEntry.CachedAvatar
                : _fallbackCachedAvatar;

            bool showAvatar = config.ShowAvatar && !ReferenceEquals(icon, null);
            if (showAvatar && !ReferenceEquals(_avatarImage.sprite, icon))
            {
                _avatarImage.sprite = icon;
            }

            int displayMask =
                (config.ShowRank ? 1 : 0) |
                (showAvatar ? 2 : 0) |
                (config.ShowPlayerName ? 4 : 0) |
                (config.ShowAccuracy ? 8 : 0) |
                (config.ShowDelta ? 16 : 0) |
                (isLeftSide ? 32 : 0);

            // 0.01% 単位で変化があった時、または表示設定/左右向きが変化した時のみ文字列・レイアウトを更新
            int deltaCent = Mathf.RoundToInt(deltaPercent * 100f);
            if (deltaCent != _lastFormattedDeltaCent || displayMask != _lastDisplayMask)
            {
                _lastFormattedDeltaCent = deltaCent;
                _lastDisplayMask = displayMask;

                RebuildTwoRowLayout(rivalAccPercent, deltaPercent, showAvatar, isLeftSide, config);
            }

            // 追い抜き時 (Δ% < 0) はハイライトカラー、前方時は通常色（自己ベストはシアン）
            Color baseColor;
            if (isOvertaken)
            {
                baseColor = _boundEntry.IsSelf ? OvertakenSelfColor : OvertakenHighlightColor;
            }
            else
            {
                baseColor = _boundEntry.IsSelf ? SelfBestColor : NormalRivalColor;
            }

            baseColor.a = alpha;
            Color shadowColor = new Color(0f, 0f, 0f, alpha * 0.95f);

            if (_upperTextObj.activeSelf)
            {
                _upperTextMesh.color = baseColor;
                _upperShadowTextMesh.color = shadowColor;
            }

            if (_lowerTextObj.activeSelf)
            {
                _lowerTextMesh.color = baseColor;
                _lowerShadowTextMesh.color = shadowColor;
            }

            if (_avatarObject.activeSelf)
            {
                Color iconColor = Color.white;
                iconColor.a = alpha;
                _avatarImage.color = iconColor;
            }
        }

        /// <summary>
        /// 内側（ノーツレーンに近い側 = localX: 0）にアイコンを配置し、その外側に2段構成のテキストを配置する：
        /// ・右側レーン (左詰め):
        ///   [アイコン]  上段: 名前
        ///               下段: #順位  精度%  (+精度差%)
        /// ・左側レーン (右詰め・逆順):
        ///         上段: 名前                  [アイコン]
        ///   下段: (+精度差%)  精度%  #順位    [アイコン]
        /// </summary>
        private void RebuildTwoRowLayout(
            float rivalAccPercent,
            float deltaPercent,
            bool showAvatar,
            bool isLeftSide,
            PluginConfig config)
        {
            string upperStr = BuildUpperNameString(config.ShowPlayerName, isLeftSide);
            string lowerStr = BuildLowerStatsString(
                config.ShowRank,
                config.ShowAccuracy,
                config.ShowDelta,
                rivalAccPercent,
                deltaPercent,
                isLeftSide);

            bool hasUpper = !string.IsNullOrEmpty(upperStr);
            bool hasLower = !string.IsNullOrEmpty(lowerStr);

            // 1. アイコンを内側（localX = 0 側）に配置
            float textAnchorX = 0f;
            if (showAvatar)
            {
                if (!_avatarObject.activeSelf) _avatarObject.SetActive(true);

                if (isLeftSide)
                {
                    // 左側レーン: 右端 (localX = 0) が内側なので、アイコン中心は -AvatarIconSize * 0.5f
                    _avatarRect.localPosition = new Vector3(-(AvatarIconSize * 0.5f), 0f, 0f);
                    textAnchorX = -(AvatarIconSize + ElementGap);
                }
                else
                {
                    // 右側レーン: 左端 (localX = 0) が内側なので、アイコン中心は +AvatarIconSize * 0.5f
                    _avatarRect.localPosition = new Vector3(AvatarIconSize * 0.5f, 0f, 0f);
                    textAnchorX = AvatarIconSize + ElementGap;
                }
            }
            else
            {
                if (_avatarObject.activeSelf) _avatarObject.SetActive(false);
                textAnchorX = 0f;
            }

            // 2. 上段・下段のY座標（両方ある場合は上下2段、片方のみの場合は中央 Y = 0）
            float upperY = (hasUpper && hasLower) ? UpperLineOffsetY : 0f;
            float lowerY = (hasUpper && hasLower) ? LowerLineOffsetY : 0f;

            // 3. 上段（プレイヤー名）の設定
            if (hasUpper)
            {
                if (!_upperTextObj.activeSelf) _upperTextObj.SetActive(true);
                if (!_upperShadowObj.activeSelf) _upperShadowObj.SetActive(true);

                _upperTextMesh.text = upperStr;
                _upperShadowTextMesh.text = upperStr;
                ApplyLineAlignmentAndPosition(_upperTextMesh, _upperShadowTextMesh, rightAligned: isLeftSide, anchorX: textAnchorX, localY: upperY);
            }
            else
            {
                if (_upperTextObj.activeSelf) _upperTextObj.SetActive(false);
                if (_upperShadowObj.activeSelf) _upperShadowObj.SetActive(false);
            }

            // 4. 下段（順位・精度・精度差）の設定
            if (hasLower)
            {
                if (!_lowerTextObj.activeSelf) _lowerTextObj.SetActive(true);
                if (!_lowerShadowObj.activeSelf) _lowerShadowObj.SetActive(true);

                _lowerTextMesh.text = lowerStr;
                _lowerShadowTextMesh.text = lowerStr;
                ApplyLineAlignmentAndPosition(_lowerTextMesh, _lowerShadowTextMesh, rightAligned: isLeftSide, anchorX: textAnchorX, localY: lowerY);
            }
            else
            {
                if (_lowerTextObj.activeSelf) _lowerTextObj.SetActive(false);
                if (_lowerShadowObj.activeSelf) _lowerShadowObj.SetActive(false);
            }
        }

        /// <summary>
        /// TextMeshPro のネイティブ中心ピボット (0.5, 0.5) を維持したまま、
        /// 左詰め時は左端が anchorX に、右詰め時は右端が anchorX に一致するようオフセット配置する
        /// </summary>
        private static void ApplyLineAlignmentAndPosition(
            TextMeshPro mainTmp,
            TextMeshPro shadowTmp,
            bool rightAligned,
            float anchorX,
            float localY)
        {
            TextAlignmentOptions align = rightAligned
                ? TextAlignmentOptions.MidlineRight
                : TextAlignmentOptions.MidlineLeft;

            float halfWidth = TextContainerWidth * 0.5f;
            float centerX = rightAligned ? (anchorX - halfWidth) : (anchorX + halfWidth);

            mainTmp.alignment = align;
            mainTmp.rectTransform.localPosition = new Vector3(centerX, localY, 0f);

            shadowTmp.alignment = align;
            shadowTmp.rectTransform.localPosition = new Vector3(
                centerX + ShadowOffset.x,
                localY + ShadowOffset.y,
                ShadowOffset.z);
        }

        private string BuildUpperNameString(bool showPlayerName, bool isLeftSide)
        {
            if (!showPlayerName)
            {
                return string.Empty;
            }

            string displayName = TruncateName(_boundEntry.PlayerName, 16);
            if (!_boundEntry.IsSelf)
            {
                return displayName;
            }

            return isLeftSide
                ? string.Concat(displayName, " [PB]")
                : string.Concat("[PB] ", displayName);
        }

        private string BuildLowerStatsString(
            bool showRank,
            bool showAccuracy,
            bool showDelta,
            float rivalAccPercent,
            float deltaPercent,
            bool isLeftSide)
        {
            if (!showRank && !showAccuracy && !showDelta)
            {
                return string.Empty;
            }

            _sb.Clear();
            string sign = deltaPercent >= 0f ? "+" : "";

            if (isLeftSide)
            {
                // 左側レーン（右詰め・逆順）: 外側(左)から内側(右・アイコン側)に向かって [精度差] [精度] [#順位]
                if (showDelta)
                {
                    _sb.Append('(')
                       .Append(sign)
                       .Append(deltaPercent.ToString("F2", CultureInfo.InvariantCulture))
                       .Append("%)");
                }

                if (showAccuracy)
                {
                    if (_sb.Length > 0) _sb.Append(' ');
                    _sb.Append(rivalAccPercent.ToString("F2", CultureInfo.InvariantCulture)).Append('%');
                }

                if (showRank)
                {
                    if (_sb.Length > 0) _sb.Append(' ');
                    _sb.Append('#').Append(_boundEntry.Rank);
                }
            }
            else
            {
                // 右側レーン（左詰め・通常順）: 内側(左・アイコン側)から外側(右)に向かって [#順位] [精度] [精度差]
                if (showRank)
                {
                    _sb.Append('#').Append(_boundEntry.Rank);
                }

                if (showAccuracy)
                {
                    if (_sb.Length > 0) _sb.Append(' ');
                    _sb.Append(rivalAccPercent.ToString("F2", CultureInfo.InvariantCulture)).Append('%');
                }

                if (showDelta)
                {
                    if (_sb.Length > 0) _sb.Append(' ');
                    _sb.Append('(')
                       .Append(sign)
                       .Append(deltaPercent.ToString("F2", CultureInfo.InvariantCulture))
                       .Append("%)");
                }
            }

            return _sb.ToString();
        }

        private static string TruncateName(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name)) return "Player";
            return name.Length <= maxLength ? name : name.Substring(0, maxLength) + "..";
        }

        private void HideWithoutUnbinding()
        {
            if (_rootObject.activeSelf)
            {
                _rootObject.SetActive(false);
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
                _rootObject.SetActive(false);
            }
        }

        public void SetParentContainer(Transform parent)
        {
            if (_rootTransform != null && parent != null && _rootTransform.parent != parent)
            {
                _rootTransform.SetParent(parent, false);
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
}
