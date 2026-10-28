using System;
using System.Globalization;
using System.Text;
using HMUI;
using RealtimeRankMarathon.Models;
using TMPro;
using UnityEngine;

namespace RealtimeRankMarathon.UI
{
    using LeaderboardEntry = RealtimeRankMarathon.Models.LeaderboardEntry;

    public class RankPlateView
    {
        private const float AvatarIconSize = 0.28f;
        private const float ElementGap = 0.08f;
        private static readonly Vector3 ShadowOffset = new Vector3(0.012f, -0.012f, 0.004f);

        private static readonly Color NormalRivalColor = Color.white;
        private static readonly Color SelfBestColor = new Color(0.20f, 0.90f, 1.00f, 1.0f);           // 自己ベスト識別用シアン
        private static readonly Color OvertakenHighlightColor = new Color(0.20f, 1.00f, 0.45f, 1.0f); // 追い抜き時グリーン
        private static readonly Color OvertakenSelfColor = new Color(1.00f, 0.85f, 0.20f, 1.0f);      // 自己ベスト追い抜き時ゴールド

        private readonly GameObject _rootObject;
        private readonly Transform _rootTransform;

        // #順位 表示用 (アイコン画像の左側に配置)
        private readonly GameObject _rankTextObj;
        private readonly GameObject _rankShadowObj;
        private readonly TextMeshPro _rankTextMesh;
        private readonly TextMeshPro _rankShadowTextMesh;

        // アイコン画像表示用 (WorldSpace Canvas + HMUI.ImageView で UINoGlow シェーダーに完全対応)
        private readonly GameObject _avatarObject;
        private readonly Transform _avatarTransform;
        private readonly ImageView _avatarImage;

        // プレイヤー名・精度・精度差 表示用 (アイコン画像の右側に配置、またはアイコン非表示時は全体を表示)
        private readonly GameObject _bodyTextObj;
        private readonly GameObject _bodyShadowObj;
        private readonly TextMeshPro _textMesh;
        private readonly TextMeshPro _outlineShadowTextMesh;

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

            // 1. #順位 用 TextMeshPro (黒縁影 + メイン)
            _rankShadowObj = new GameObject("RankNumShadow");
            _rankShadowObj.transform.SetParent(_rootTransform, false);
            _rankShadowTextMesh = _rankShadowObj.AddComponent<TextMeshPro>();
            ConfigureTmp(_rankShadowTextMesh, sharedFont, sharedOutlineMaterial, new Color(0f, 0f, 0f, 0.95f), 10);

            _rankTextObj = new GameObject("RankNumText");
            _rankTextObj.transform.SetParent(_rootTransform, false);
            _rankTextMesh = _rankTextObj.AddComponent<TextMeshPro>();
            ConfigureTmp(_rankTextMesh, sharedFont, sharedOutlineMaterial, NormalRivalColor, 11);

            _rankShadowObj.SetActive(false);
            _rankTextObj.SetActive(false);

            // 2. 丸型マスク済みアイコン表示用 WorldSpace Canvas + HMUI.ImageView (Beat Saber UINoGlow 対応)
            _avatarObject = new GameObject("AvatarIcon");
            _avatarObject.layer = 5; // UI Layer
            _avatarTransform = _avatarObject.transform;
            _avatarTransform.SetParent(_rootTransform, false);
            _avatarTransform.localPosition = Vector3.zero;
            _avatarTransform.localRotation = Quaternion.identity;
            _avatarTransform.localScale = new Vector3(AvatarIconSize, AvatarIconSize, 1f);

            var avatarCanvas = _avatarObject.AddComponent<Canvas>();
            avatarCanvas.renderMode = RenderMode.WorldSpace;
            avatarCanvas.sortingOrder = 11;

            _avatarImage = _avatarObject.AddComponent<ImageView>();
            if (sharedUiNoGlowMaterial != null)
            {
                _avatarImage.material = sharedUiNoGlowMaterial;
            }
            _avatarImage.raycastTarget = false;
            _avatarImage.preserveAspect = true;

            var avatarRect = _avatarImage.rectTransform;
            avatarRect.sizeDelta = new Vector2(1.0f, 1.0f);
            avatarRect.anchorMin = new Vector2(0.5f, 0.5f);
            avatarRect.anchorMax = new Vector2(0.5f, 0.5f);
            avatarRect.pivot = new Vector2(0.5f, 0.5f);
            avatarRect.localPosition = Vector3.zero;

            _avatarObject.SetActive(false);

            // 3. プレイヤー名・精度・自分との精度差 用 TextMeshPro (黒縁影 + メイン)
            _bodyShadowObj = new GameObject("RankBodyShadow");
            _bodyShadowObj.transform.SetParent(_rootTransform, false);
            _outlineShadowTextMesh = _bodyShadowObj.AddComponent<TextMeshPro>();
            ConfigureTmp(_outlineShadowTextMesh, sharedFont, sharedOutlineMaterial, new Color(0f, 0f, 0f, 0.95f), 10);

            _bodyTextObj = new GameObject("RankBodyText");
            _bodyTextObj.transform.SetParent(_rootTransform, false);
            _textMesh = _bodyTextObj.AddComponent<TextMeshPro>();
            ConfigureTmp(_textMesh, sharedFont, sharedOutlineMaterial, NormalRivalColor, 11);

            _rootObject.SetActive(false);
        }

        private static void ConfigureTmp(TextMeshPro tmp, TMP_FontAsset sharedFont, Material sharedMaterial, Color color, int sortingOrder)
        {
            if (sharedFont != null)
            {
                tmp.font = sharedFont;
            }
            if (sharedMaterial != null)
            {
                tmp.fontSharedMaterial = sharedMaterial;
            }

            tmp.fontSize = 2.4f;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.richText = false;
            tmp.color = color;
            tmp.sortingOrder = sortingOrder;

            var rect = tmp.rectTransform;
            rect.sizeDelta = new Vector2(6.0f, 0.6f);
            rect.pivot = new Vector2(0f, 0.5f);
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

                // 左側レーン (Slot 0: 左外側, Slot 2: 左内側) は X < 0 なので右詰め（左側へ伸ばす）
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

                RebuildTextsAndHorizontalLayout(rivalAccPercent, deltaPercent, showAvatar, isLeftSide, config);
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

            if (_rankTextObj.activeSelf)
            {
                _rankTextMesh.color = baseColor;
                _rankShadowTextMesh.color = shadowColor;
            }

            if (_bodyTextObj.activeSelf)
            {
                _textMesh.color = baseColor;
                _outlineShadowTextMesh.color = shadowColor;
            }

            if (_avatarObject.activeSelf)
            {
                Color iconColor = Color.white;
                iconColor.a = alpha;
                _avatarImage.color = iconColor;
            }
        }

        /// <summary>
        /// 並び順「#順位　アイコン画像　プレイヤー名　精度　自分との精度差」を維持しつつ、
        /// 左側配置時は右詰め（左方向 -X へ伸ばす）、右側配置時は左詰め（右方向 +X へ伸ばす）に配置する
        /// </summary>
        private void RebuildTextsAndHorizontalLayout(
            float rivalAccPercent,
            float deltaPercent,
            bool showAvatar,
            bool isLeftSide,
            PluginConfig config)
        {
            // アイコン非表示時は、有効な全テキスト項目を1つの TextMeshPro にまとめて描画する
            if (!showAvatar)
            {
                if (_rankTextObj.activeSelf) _rankTextObj.SetActive(false);
                if (_rankShadowObj.activeSelf) _rankShadowObj.SetActive(false);
                if (_avatarObject.activeSelf) _avatarObject.SetActive(false);

                string combinedText = BuildBodyString(
                    includeRank: config.ShowRank,
                    includeName: config.ShowPlayerName,
                    includeAccuracy: config.ShowAccuracy,
                    includeDelta: config.ShowDelta,
                    rivalAccPercent: rivalAccPercent,
                    deltaPercent: deltaPercent);

                if (string.IsNullOrEmpty(combinedText))
                {
                    if (_bodyTextObj.activeSelf) _bodyTextObj.SetActive(false);
                    if (_bodyShadowObj.activeSelf) _bodyShadowObj.SetActive(false);
                    return;
                }

                if (!_bodyTextObj.activeSelf) _bodyTextObj.SetActive(true);
                if (!_bodyShadowObj.activeSelf) _bodyShadowObj.SetActive(true);

                _textMesh.text = combinedText;
                _outlineShadowTextMesh.text = combinedText;

                // 左側配置時は右詰め (pivot X = 1, MidlineRight)、右側配置時は左詰め (pivot X = 0, MidlineLeft)
                ApplyTextAlignmentAndPosition(_textMesh, _outlineShadowTextMesh, rightAligned: isLeftSide, localX: 0f);
                return;
            }

            // アイコン表示時: [#順位] -> [アイコン画像] -> [プレイヤー名 精度 自分との精度差]
            if (!_avatarObject.activeSelf)
            {
                _avatarObject.SetActive(true);
            }

            bool hasRank = config.ShowRank;
            string rankStr = hasRank
                ? string.Format(CultureInfo.InvariantCulture, "#{0}", _boundEntry.Rank)
                : string.Empty;

            string bodyStr = BuildBodyString(
                includeRank: false,
                includeName: config.ShowPlayerName,
                includeAccuracy: config.ShowAccuracy,
                includeDelta: config.ShowDelta,
                rivalAccPercent: rivalAccPercent,
                deltaPercent: deltaPercent);
            bool hasBody = !string.IsNullOrEmpty(bodyStr);

            if (hasRank)
            {
                if (!_rankTextObj.activeSelf) _rankTextObj.SetActive(true);
                if (!_rankShadowObj.activeSelf) _rankShadowObj.SetActive(true);
                _rankTextMesh.text = rankStr;
                _rankShadowTextMesh.text = rankStr;
            }
            else
            {
                if (_rankTextObj.activeSelf) _rankTextObj.SetActive(false);
                if (_rankShadowObj.activeSelf) _rankShadowObj.SetActive(false);
            }

            if (hasBody)
            {
                if (!_bodyTextObj.activeSelf) _bodyTextObj.SetActive(true);
                if (!_bodyShadowObj.activeSelf) _bodyShadowObj.SetActive(true);
                _textMesh.text = bodyStr;
                _outlineShadowTextMesh.text = bodyStr;
            }
            else
            {
                if (_bodyTextObj.activeSelf) _bodyTextObj.SetActive(false);
                if (_bodyShadowObj.activeSelf) _bodyShadowObj.SetActive(false);
            }

            if (isLeftSide)
            {
                // 左側配置（右詰め）: 右端を localX = 0 に固定し、左方向 (-X) へはみ出すように配置
                // 左から右への読み順は [#順位] -> [アイコン画像] -> [プレイヤー名 精度 精度差]
                float cursor = 0f;

                if (hasBody)
                {
                    ApplyTextAlignmentAndPosition(_textMesh, _outlineShadowTextMesh, rightAligned: true, localX: cursor);
                    float bodyWidth = Mathf.Max(0.1f, _textMesh.GetPreferredValues(bodyStr).x);
                    cursor -= (bodyWidth + ElementGap);
                }

                _avatarTransform.localPosition = new Vector3(cursor - (AvatarIconSize * 0.5f), 0f, 0f);
                cursor -= (AvatarIconSize + ElementGap);

                if (hasRank)
                {
                    ApplyTextAlignmentAndPosition(_rankTextMesh, _rankShadowTextMesh, rightAligned: true, localX: cursor);
                }
            }
            else
            {
                // 右側配置（左詰め）: 左端を localX = 0 に固定し、右方向 (+X) へはみ出すように配置
                // 左から右への読み順は [#順位] -> [アイコン画像] -> [プレイヤー名 精度 精度差]
                float cursor = 0f;

                if (hasRank)
                {
                    ApplyTextAlignmentAndPosition(_rankTextMesh, _rankShadowTextMesh, rightAligned: false, localX: cursor);
                    float rankWidth = Mathf.Max(0.1f, _rankTextMesh.GetPreferredValues(rankStr).x);
                    cursor += (rankWidth + ElementGap);
                }

                _avatarTransform.localPosition = new Vector3(cursor + (AvatarIconSize * 0.5f), 0f, 0f);
                cursor += (AvatarIconSize + ElementGap);

                if (hasBody)
                {
                    ApplyTextAlignmentAndPosition(_textMesh, _outlineShadowTextMesh, rightAligned: false, localX: cursor);
                }
            }
        }

        private static void ApplyTextAlignmentAndPosition(
            TextMeshPro mainTmp,
            TextMeshPro shadowTmp,
            bool rightAligned,
            float localX)
        {
            Vector2 pivot = rightAligned ? new Vector2(1f, 0.5f) : new Vector2(0f, 0.5f);
            TextAlignmentOptions align = rightAligned
                ? TextAlignmentOptions.MidlineRight
                : TextAlignmentOptions.MidlineLeft;

            var mainRect = mainTmp.rectTransform;
            mainRect.pivot = pivot;
            mainTmp.alignment = align;
            mainRect.localPosition = new Vector3(localX, 0f, 0f);

            var shadowRect = shadowTmp.rectTransform;
            shadowRect.pivot = pivot;
            shadowTmp.alignment = align;
            shadowRect.localPosition = new Vector3(localX + ShadowOffset.x, ShadowOffset.y, ShadowOffset.z);
        }

        private string BuildBodyString(
            bool includeRank,
            bool includeName,
            bool includeAccuracy,
            bool includeDelta,
            float rivalAccPercent,
            float deltaPercent)
        {
            _sb.Clear();

            if (includeRank)
            {
                _sb.Append('#').Append(_boundEntry.Rank);
            }

            if (includeName)
            {
                if (_sb.Length > 0) _sb.Append(' ');
                if (_boundEntry.IsSelf)
                {
                    _sb.Append("[PB] ");
                }
                _sb.Append(TruncateName(_boundEntry.PlayerName, 14));
            }

            if (includeAccuracy)
            {
                if (_sb.Length > 0) _sb.Append(' ');
                _sb.Append(rivalAccPercent.ToString("F2", CultureInfo.InvariantCulture)).Append('%');
            }

            if (includeDelta)
            {
                if (_sb.Length > 0) _sb.Append(' ');
                string sign = deltaPercent >= 0f ? "+" : "";
                _sb.Append('(')
                   .Append(sign)
                   .Append(deltaPercent.ToString("F2", CultureInfo.InvariantCulture))
                   .Append("%)");
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
