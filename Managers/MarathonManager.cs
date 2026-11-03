using System;
using System.Collections.Generic;
using RealtimeRankMarathon.Models;
using RealtimeRankMarathon.Services;
using RealtimeRankMarathon.UI;
using UnityEngine;
using Zenject;

namespace RealtimeRankMarathon.Managers
{
    using LeaderboardEntry = RealtimeRankMarathon.Models.LeaderboardEntry;

    public class MarathonManager : IInitializable, ITickable, IDisposable
    {
        public const int RequiredNotesBeforeDisplay = 20;
        private const int MaxAheadRivals = 5;
        private const int MaxBehindRivals = 3;

        private IScoreController _scoreController;
        private RelativeScoreAndImmediateRankCounter _relativeScoreCounter;
        private IReadonlyBeatmapData _beatmapData;
        private readonly GameplayCoreSceneSetupData _sceneSetupData;
        private readonly ILevelEndActions _levelEndActions;
        private readonly LeaderboardFetcher _leaderboardFetcher;
        private readonly AvatarCacheService _avatarCacheService;
        private readonly RankPlatePool _platePool;
        private readonly PluginConfig _config;

        private List<LeaderboardEntry> _entries = new List<LeaderboardEntry>();
        private readonly List<LeaderboardEntry> _activeTargetBuffer = new List<LeaderboardEntry>(RankPlatePool.MaxPoolSize);
        private readonly LeaderboardEntry _searchProbe = new LeaderboardEntry();

        private int _scoredNoteCount;
        private int _totalNotesCount = 500;
        private float _rawAccuracy = 1.0f;
        private float _targetDisplayAccuracy = 1.0f;
        private float _smoothedDisplayAccuracy = 1.0f;
        private float _lastRecalculatedDisplayAccuracy = -1.0f;
        private int _currentEstimatedRank = 1;
        private bool _isDisplayActive;
        private bool _levelEndTriggered;
        private volatile bool _entriesUpdatePending;

        [Inject]
        public MarathonManager(
            [InjectOptional] IScoreController scoreController,
            [InjectOptional] RelativeScoreAndImmediateRankCounter relativeScoreCounter,
            [InjectOptional] IReadonlyBeatmapData beatmapData,
            [InjectOptional] GameplayCoreSceneSetupData sceneSetupData,
            [InjectOptional] ILevelEndActions levelEndActions,
            LeaderboardFetcher leaderboardFetcher,
            AvatarCacheService avatarCacheService,
            RankPlatePool platePool,
            PluginConfig config)
        {
            _scoreController = scoreController;
            _relativeScoreCounter = relativeScoreCounter;
            _beatmapData = beatmapData;
            _sceneSetupData = sceneSetupData;
            _levelEndActions = levelEndActions;
            _leaderboardFetcher = leaderboardFetcher;
            _avatarCacheService = avatarCacheService;
            _platePool = platePool;
            _config = config;
        }

        public void Initialize()
        {
            _scoredNoteCount = 0;
            _isDisplayActive = false;
            _levelEndTriggered = false;
            _lastRecalculatedDisplayAccuracy = -1.0f;
            _entries = _leaderboardFetcher.GetEntriesSnapshot();
            _totalNotesCount = ResolveTotalScorableNotesCount();

            // DIで取得できなかった場合の確実なフォールバック検索
            if (_scoreController == null)
            {
                _scoreController = UnityEngine.Object.FindObjectOfType<ScoreController>();
            }

            if (_relativeScoreCounter == null)
            {
                _relativeScoreCounter = UnityEngine.Object.FindObjectOfType<RelativeScoreAndImmediateRankCounter>();
            }

            _config.ConfigChangedEvent += OnConfigChanged;
            _leaderboardFetcher.LeaderboardUpdatedEvent += OnLeaderboardUpdated;

            if (_scoreController != null)
            {
                _scoreController.scoringForNoteFinishedEvent += OnScoringForNoteFinished;
            }
            else
            {
                Plugin.Log?.Warn("[MarathonManager] IScoreController could not be resolved in Gameplay scene.");
            }

            if (_levelEndActions != null)
            {
                _levelEndActions.levelFinishedEvent += OnLevelEnded;
                _levelEndActions.levelFailedEvent += OnLevelEnded;
            }

            // メニュー選択時に未取得だった場合のみ、最初のノーツが来る前の導入部でプリロードを実行
            if (_config.Enabled && _sceneSetupData != null && _sceneSetupData.beatmapLevel != null)
            {
                _ = _leaderboardFetcher.PreloadForMapAsync(
                    _sceneSetupData.beatmapLevel,
                    _sceneSetupData.beatmapKey,
                    allowTop10AvatarDownload: true);
            }
        }

        private int ResolveTotalScorableNotesCount()
        {
            try
            {
                var bmData = _beatmapData ?? _sceneSetupData?.transformedBeatmapData;
                if (bmData != null)
                {
                    int exactScorableCount = 0;
                    foreach (var noteData in bmData.GetBeatmapDataItems<NoteData>(0))
                    {
                        if (noteData != null &&
                            noteData.gameplayType != NoteData.GameplayType.Bomb &&
                            noteData.scoringType != NoteData.ScoringType.Ignore &&
                            noteData.scoringType != NoteData.ScoringType.NoScore)
                        {
                            exactScorableCount++;
                        }
                    }

                    if (exactScorableCount > 0)
                    {
                        return exactScorableCount;
                    }

                    if (bmData.cuttableNotesCount > 0)
                    {
                        return bmData.cuttableNotesCount;
                    }
                }

                if (_sceneSetupData != null && _sceneSetupData.beatmapBasicData != null)
                {
                    if (_sceneSetupData.beatmapBasicData.notesCount > 0)
                    {
                        return _sceneSetupData.beatmapBasicData.notesCount;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[MarathonManager] ResolveTotalScorableNotesCount fallback: {ex.Message}");
            }

            return 500;
        }

        private float GetCurrentRealtimeAccuracy()
        {
            if (_relativeScoreCounter != null)
            {
                return Mathf.Clamp01(_relativeScoreCounter.relativeScore);
            }

            if (_scoreController != null && _scoreController.immediateMaxPossibleMultipliedScore > 0)
            {
                return Mathf.Clamp01((float)_scoreController.multipliedScore / _scoreController.immediateMaxPossibleMultipliedScore);
            }

            return 1.0f;
        }

        /// <summary>
        /// ごぼう抜きモード（Off / 倍率指定 / AUTO最下位）および現在のノーツ進行割合に応じて、
        /// ランキング順位・距離表示に使用する実効精度を算出する。
        /// 20ノーツ時点で開始補正が適用され、最終ノーツ時点で 1.0倍（実精度と完全一致）に収束する。
        /// </summary>
        private float CalculateEffectiveAccuracy(float rawAccuracy)
        {
            if (_config.OvertakeMode == OvertakingMode.Off)
            {
                return rawAccuracy;
            }

            float progress = _totalNotesCount > RequiredNotesBeforeDisplay
                ? Mathf.Clamp01((float)(_scoredNoteCount - RequiredNotesBeforeDisplay) / (_totalNotesCount - RequiredNotesBeforeDisplay))
                : 1.0f;

            if (_config.OvertakeMode == OvertakingMode.AutoLowest)
            {
                float lowestAcc = GetLowestEntryAccuracy();
                if (lowestAcc > 0.01f && lowestAcc < rawAccuracy)
                {
                    return Mathf.Clamp01(Mathf.Lerp(lowestAcc, rawAccuracy, progress));
                }
                return rawAccuracy;
            }

            // OvertakingMode.Multiplier (デフォルト 0.90倍, 0.50〜1.00倍)
            float startMult = Mathf.Clamp(_config.OvertakingStartMultiplier, 0.50f, 1.00f);
            float currentMult = Mathf.Lerp(startMult, 1.0f, progress);
            return Mathf.Clamp01(rawAccuracy * currentMult);
        }

        private float GetLowestEntryAccuracy()
        {
            if (_entries == null || _entries.Count == 0)
            {
                return -1.0f;
            }

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                float acc = (float)_entries[i].Accuracy;
                if (acc > 0.01f)
                {
                    return acc;
                }
            }

            return -1.0f;
        }

        private void OnScoringForNoteFinished(ScoringElement scoringElement)
        {
            if (!_config.Enabled || scoringElement == null || scoringElement.noteData == null)
            {
                return;
            }

            var noteData = scoringElement.noteData;

            // ボムや非採点要素を除外し、純粋な採点完了ノーツのみをカウント
            if (noteData.gameplayType == NoteData.GameplayType.Bomb ||
                noteData.scoringType == NoteData.ScoringType.Ignore ||
                noteData.scoringType == NoteData.ScoringType.NoScore)
            {
                return;
            }

            // ノーツ採点が始まった時点でプレイ中の画像通信・Texture2D/Sprite生成を完全ロック
            if (!_avatarCacheService.IsGameplayLocked)
            {
                _avatarCacheService.SetGameplayLock(true);
            }

            _scoredNoteCount++;
            _rawAccuracy = GetCurrentRealtimeAccuracy();
            _targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);

            // プレイ開始から 20ノーツ の採点が完了するまではランキングUIを完全に非表示
            if (_scoredNoteCount < RequiredNotesBeforeDisplay)
            {
                return;
            }

            if (!_isDisplayActive)
            {
                _isDisplayActive = true;
                _smoothedDisplayAccuracy = _targetDisplayAccuracy;
                RecalculateRankAndAssignPlates();
            }
        }

        public void Tick()
        {
            if (!_config.Enabled)
            {
                return;
            }

            if (_entriesUpdatePending)
            {
                _entriesUpdatePending = false;
                _entries = _leaderboardFetcher.GetEntriesSnapshot();
                if (_isDisplayActive && _scoredNoteCount >= RequiredNotesBeforeDisplay)
                {
                    _targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);
                    RecalculateRankAndAssignPlates();
                }
            }

            if (!_isDisplayActive || _scoredNoteCount < RequiredNotesBeforeDisplay)
            {
                return;
            }

            _rawAccuracy = GetCurrentRealtimeAccuracy();
            _targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);

            // ノーツカット時に精度が大きく変化した場合でも、他プレイヤーの表示が一瞬で飛ばず滑らかに移動するよう補間
            float smoothFactor = 1.0f - Mathf.Exp(-4.5f * Time.deltaTime);
            _smoothedDisplayAccuracy = Mathf.Lerp(_smoothedDisplayAccuracy, _targetDisplayAccuracy, smoothFactor);
            if (Mathf.Abs(_targetDisplayAccuracy - _smoothedDisplayAccuracy) < 0.00001f)
            {
                _smoothedDisplayAccuracy = _targetDisplayAccuracy;
            }

            if (Mathf.Abs(_smoothedDisplayAccuracy - _lastRecalculatedDisplayAccuracy) >= 0.00005f)
            {
                RecalculateRankAndAssignPlates();
            }

            _platePool.UpdateActivePlates(_smoothedDisplayAccuracy, _currentEstimatedRank, Time.deltaTime);
        }

        /// <summary>
        /// 降順対応 BinarySearch で現在順位と表示対象（前方指定人数・後方3人）を特定する
        /// </summary>
        private void RecalculateRankAndAssignPlates()
        {
            if (_entries == null || _entries.Count == 0)
            {
                _platePool.SetAllVisible(false);
                return;
            }

            _lastRecalculatedDisplayAccuracy = _smoothedDisplayAccuracy;
            _searchProbe.Accuracy = _smoothedDisplayAccuracy;

            // 降順リストに対する二分探索
            int searchIndex = _entries.BinarySearch(_searchProbe, LeaderboardEntry.DescendingAccuracyComparer.Instance);
            int insertionIndex = searchIndex >= 0 ? searchIndex : ~searchIndex;

            // 現在順位の推定
            if (insertionIndex <= 0)
            {
                _currentEstimatedRank = 1;
            }
            else if (insertionIndex < _entries.Count)
            {
                _currentEstimatedRank = _entries[insertionIndex].Rank;
            }
            else
            {
                _currentEstimatedRank = _entries[_entries.Count - 1].Rank + 1;
            }

            // 前方指定人数 (config.AheadRivalsCount: 1〜20人) と 後方最大3人を抽出
            int aheadCount = Mathf.Clamp(_config.AheadRivalsCount, 1, 20);
            int totalTargetCount = aheadCount + MaxBehindRivals;

            int startIdx = Mathf.Max(0, insertionIndex - aheadCount);
            int endIdx = Mathf.Min(_entries.Count - 1, insertionIndex + MaxBehindRivals - 1);

            // 端に寄っている場合は totalTargetCount になるようウィンドウを補正
            int currentWindowCount = endIdx - startIdx + 1;
            if (currentWindowCount < totalTargetCount)
            {
                if (startIdx == 0)
                {
                    endIdx = Mathf.Min(_entries.Count - 1, startIdx + totalTargetCount - 1);
                }
                else if (endIdx == _entries.Count - 1)
                {
                    startIdx = Mathf.Max(0, endIdx - totalTargetCount + 1);
                }
            }

            _activeTargetBuffer.Clear();
            for (int i = startIdx; i <= endIdx && i < _entries.Count; i++)
            {
                _activeTargetBuffer.Add(_entries[i]);
            }

            _platePool.AssignTargetsToPool(_activeTargetBuffer);

            // 未取得順位帯（ギャップまたは取得済み下限より下）に現在精度が位置している場合のみ、バックグラウンドでテキスト動的補完
            CheckAndTriggerGapFetchIfNeeded(insertionIndex);
        }

        private void CheckAndTriggerGapFetchIfNeeded(int insertionIndex)
        {
            if (_entries == null || _entries.Count == 0) return;

            bool needsGapFetch = false;

            // ケース1: 事前取得した最下位エントリの近く（下位3人以内）またはそれ以下に落ちた場合
            if (insertionIndex >= _entries.Count - 2)
            {
                needsGapFetch = true;
            }
            // ケース2: TOP100 と 自己ベスト周辺 の間にランク番号の不連続ギャップがあり、そこに位置している場合
            else if (insertionIndex > 0 && insertionIndex < _entries.Count)
            {
                int upperRank = _entries[insertionIndex - 1].Rank;
                int lowerRank = _entries[insertionIndex].Rank;
                if (lowerRank - upperRank > 5)
                {
                    needsGapFetch = true;
                }
            }

            if (needsGapFetch)
            {
                _ = _leaderboardFetcher.FetchGapEntriesForAccuracyAsync(_smoothedDisplayAccuracy);
            }
        }

        private void OnLeaderboardUpdated(IReadOnlyList<LeaderboardEntry> updatedEntries)
        {
            _entriesUpdatePending = true;
        }

        private void OnConfigChanged()
        {
            if (!_config.Enabled)
            {
                _platePool.SetAllVisible(false);
                return;
            }

            _platePool.ApplyVerticalBarRootTransform();
            _leaderboardFetcher.RecalculateSlotsForCurrentEntries();
            _entries = _leaderboardFetcher.GetEntriesSnapshot();

            if (_isDisplayActive && _scoredNoteCount >= RequiredNotesBeforeDisplay)
            {
                _rawAccuracy = GetCurrentRealtimeAccuracy();
                _targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);
                RecalculateRankAndAssignPlates();
            }
        }

        /// <summary>
        /// 曲終了時（クリアまたはフェイル）に最終精度の前後5人ずつ（計10人分）の未キャッシュアイコンを取得・保存する
        /// </summary>
        private void OnLevelEnded()
        {
            if (_levelEndTriggered)
            {
                return;
            }
            _levelEndTriggered = true;

            // ロック解除してリザルト遷移中に前後5人ずつのアイコンをキャッシュ
            _avatarCacheService.SetGameplayLock(false);
            _rawAccuracy = GetCurrentRealtimeAccuracy();

            _ = _leaderboardFetcher.CacheAroundFinalAccuracyAsync(_rawAccuracy);
        }

        public void Dispose()
        {
            // シーン終了時に必ずロックを解除
            _avatarCacheService.SetGameplayLock(false);

            _config.ConfigChangedEvent -= OnConfigChanged;
            _leaderboardFetcher.LeaderboardUpdatedEvent -= OnLeaderboardUpdated;

            if (_scoreController != null)
            {
                _scoreController.scoringForNoteFinishedEvent -= OnScoringForNoteFinished;
            }

            if (_levelEndActions != null)
            {
                _levelEndActions.levelFinishedEvent -= OnLevelEnded;
                _levelEndActions.levelFailedEvent -= OnLevelEnded;
            }
        }
    }
}
