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
        private float _currentAccuracy = 1.0f;
        private int _currentEstimatedRank = 1;
        private bool _isDisplayActive;
        private bool _levelEndTriggered;
        private volatile bool _entriesUpdatePending;

        [Inject]
        public MarathonManager(
            [InjectOptional] IScoreController scoreController,
            [InjectOptional] RelativeScoreAndImmediateRankCounter relativeScoreCounter,
            [InjectOptional] GameplayCoreSceneSetupData sceneSetupData,
            [InjectOptional] ILevelEndActions levelEndActions,
            LeaderboardFetcher leaderboardFetcher,
            AvatarCacheService avatarCacheService,
            RankPlatePool platePool,
            PluginConfig config)
        {
            _scoreController = scoreController;
            _relativeScoreCounter = relativeScoreCounter;
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
            _entries = _leaderboardFetcher.GetEntriesSnapshot();

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
            _currentAccuracy = GetCurrentRealtimeAccuracy();

            // プレイ開始から 20ノーツ の採点が完了するまではランキングUIを完全に非表示
            if (_scoredNoteCount < RequiredNotesBeforeDisplay)
            {
                return;
            }

            if (!_isDisplayActive)
            {
                _isDisplayActive = true;
            }

            RecalculateRankAndAssignPlates();
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
                    RecalculateRankAndAssignPlates();
                }
            }

            if (!_isDisplayActive || _scoredNoteCount < RequiredNotesBeforeDisplay)
            {
                return;
            }

            _currentAccuracy = GetCurrentRealtimeAccuracy();
            _platePool.UpdateActivePlates(_currentAccuracy, _currentEstimatedRank, Time.deltaTime);
        }

        /// <summary>
        /// ノーツ採点完了イベント発火時に、降順対応 BinarySearch で現在順位と表示対象（前方5人・後方3人）を特定する
        /// </summary>
        private void RecalculateRankAndAssignPlates()
        {
            if (_entries == null || _entries.Count == 0)
            {
                _platePool.SetAllVisible(false);
                return;
            }

            _searchProbe.Accuracy = _currentAccuracy;

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

            // 前方最大5人 (insertionIndex - 5 .. insertionIndex - 1) と 後方最大3人 (insertionIndex .. insertionIndex + 2) を抽出
            int startIdx = Mathf.Max(0, insertionIndex - MaxAheadRivals);
            int endIdx = Mathf.Min(_entries.Count - 1, insertionIndex + MaxBehindRivals - 1);

            // 端に寄っている場合は最大8個になるようウィンドウを補正
            int currentWindowCount = endIdx - startIdx + 1;
            if (currentWindowCount < RankPlatePool.MaxPoolSize)
            {
                if (startIdx == 0)
                {
                    endIdx = Mathf.Min(_entries.Count - 1, startIdx + RankPlatePool.MaxPoolSize - 1);
                }
                else if (endIdx == _entries.Count - 1)
                {
                    startIdx = Mathf.Max(0, endIdx - RankPlatePool.MaxPoolSize + 1);
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
                _ = _leaderboardFetcher.FetchGapEntriesForAccuracyAsync(_currentAccuracy);
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
            _currentAccuracy = GetCurrentRealtimeAccuracy();

            _ = _leaderboardFetcher.CacheAroundFinalAccuracyAsync(_currentAccuracy);
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
