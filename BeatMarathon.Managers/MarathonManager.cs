using System;
using System.Collections.Generic;
using BeatMarathon.Models;
using BeatMarathon.Services;
using BeatMarathon.UI;
using UnityEngine;
using Zenject;

namespace BeatMarathon.Managers;

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

	private List<BeatMarathon.Models.LeaderboardEntry> _entries = new List<BeatMarathon.Models.LeaderboardEntry>();

	private readonly List<BeatMarathon.Models.LeaderboardEntry> _activeTargetBuffer = new List<BeatMarathon.Models.LeaderboardEntry>(24);

	private readonly BeatMarathon.Models.LeaderboardEntry _searchProbe = new BeatMarathon.Models.LeaderboardEntry();

	private int _scoredNoteCount;

	private int _totalNotesCount = 500;

	private float _rawAccuracy = 1f;

	private float _targetDisplayAccuracy = 1f;

	private float _smoothedDisplayAccuracy = 1f;

	private float _lastRecalculatedDisplayAccuracy = -1f;

	private int _currentEstimatedRank = 1;

	private bool _isDisplayActive;

	private bool _levelEndTriggered;

	private volatile bool _entriesUpdatePending;

	[Inject]
	public MarathonManager([InjectOptional] IScoreController scoreController, [InjectOptional] RelativeScoreAndImmediateRankCounter relativeScoreCounter, [InjectOptional] IReadonlyBeatmapData beatmapData, [InjectOptional] GameplayCoreSceneSetupData sceneSetupData, [InjectOptional] ILevelEndActions levelEndActions, LeaderboardFetcher leaderboardFetcher, AvatarCacheService avatarCacheService, RankPlatePool platePool, PluginConfig config)
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
		_lastRecalculatedDisplayAccuracy = -1f;
		_entries = _leaderboardFetcher.GetEntriesSnapshot();
		_totalNotesCount = ResolveTotalScorableNotesCount();
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
		if (_config.Enabled && _sceneSetupData != null && _sceneSetupData.beatmapLevel != null)
		{
			_leaderboardFetcher.PreloadForMapAsync(_sceneSetupData.beatmapLevel, _sceneSetupData.beatmapKey, allowTop10AvatarDownload: true);
		}
	}

	private int ResolveTotalScorableNotesCount()
	{
		try
		{
			IReadonlyBeatmapData readonlyBeatmapData = _beatmapData ?? _sceneSetupData?.transformedBeatmapData;
			if (readonlyBeatmapData != null)
			{
				int num = 0;
				foreach (NoteData beatmapDataItem in readonlyBeatmapData.GetBeatmapDataItems<NoteData>(0))
				{
					if (beatmapDataItem != null && beatmapDataItem.gameplayType != NoteData.GameplayType.Bomb && beatmapDataItem.scoringType != NoteData.ScoringType.Ignore && beatmapDataItem.scoringType != 0)
					{
						num++;
					}
				}
				if (num > 0)
				{
					return num;
				}
				if (readonlyBeatmapData.cuttableNotesCount > 0)
				{
					return readonlyBeatmapData.cuttableNotesCount;
				}
			}
			if (_sceneSetupData != null && _sceneSetupData.beatmapBasicData != null && _sceneSetupData.beatmapBasicData.notesCount > 0)
			{
				return _sceneSetupData.beatmapBasicData.notesCount;
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.Warn("[MarathonManager] ResolveTotalScorableNotesCount fallback: " + ex.Message);
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
			return Mathf.Clamp01((float)_scoreController.multipliedScore / (float)_scoreController.immediateMaxPossibleMultipliedScore);
		}
		return 1f;
	}

	private float CalculateEffectiveAccuracy(float rawAccuracy)
	{
		if (_config.OvertakeMode == OvertakingMode.Off)
		{
			return rawAccuracy;
		}
		float t = ((_totalNotesCount > 20) ? Mathf.Clamp01((float)(_scoredNoteCount - 20) / (float)(_totalNotesCount - 20)) : 1f);
		if (_config.OvertakeMode == OvertakingMode.AutoLowest)
		{
			float lowestEntryAccuracy = GetLowestEntryAccuracy();
			if (lowestEntryAccuracy > 0.01f && lowestEntryAccuracy < rawAccuracy)
			{
				return Mathf.Clamp01(Mathf.Lerp(lowestEntryAccuracy, rawAccuracy, t));
			}
			return rawAccuracy;
		}
		float num = Mathf.Lerp(Mathf.Clamp(_config.OvertakingStartMultiplier, 0.5f, 1f), 1f, t);
		return Mathf.Clamp01(rawAccuracy * num);
	}

	private float GetLowestEntryAccuracy()
	{
		if (_entries == null || _entries.Count == 0)
		{
			return -1f;
		}
		for (int num = _entries.Count - 1; num >= 0; num--)
		{
			float num2 = (float)_entries[num].Accuracy;
			if (num2 > 0.01f)
			{
				return num2;
			}
		}
		return -1f;
	}

	private void OnScoringForNoteFinished(ScoringElement scoringElement)
	{
		if (!_config.Enabled || scoringElement == null || scoringElement.noteData == null)
		{
			return;
		}
		NoteData noteData = scoringElement.noteData;
		if (noteData.gameplayType != NoteData.GameplayType.Bomb && noteData.scoringType != NoteData.ScoringType.Ignore && noteData.scoringType != 0)
		{
			if (!_avatarCacheService.IsGameplayLocked)
			{
				_avatarCacheService.SetGameplayLock(locked: true);
			}
			_scoredNoteCount++;
			_rawAccuracy = GetCurrentRealtimeAccuracy();
			_targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);
			if (_scoredNoteCount >= 20 && !_isDisplayActive)
			{
				_isDisplayActive = true;
				_smoothedDisplayAccuracy = _targetDisplayAccuracy;
				RecalculateRankAndAssignPlates();
			}
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
			if (_isDisplayActive && _scoredNoteCount >= 20)
			{
				_targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);
				RecalculateRankAndAssignPlates();
			}
		}
		if (_isDisplayActive && _scoredNoteCount >= 20)
		{
			_rawAccuracy = GetCurrentRealtimeAccuracy();
			_targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);
			float t = 1f - Mathf.Exp(-4.5f * Time.deltaTime);
			_smoothedDisplayAccuracy = Mathf.Lerp(_smoothedDisplayAccuracy, _targetDisplayAccuracy, t);
			if (Mathf.Abs(_targetDisplayAccuracy - _smoothedDisplayAccuracy) < 1E-05f)
			{
				_smoothedDisplayAccuracy = _targetDisplayAccuracy;
			}
			if (Mathf.Abs(_smoothedDisplayAccuracy - _lastRecalculatedDisplayAccuracy) >= 5E-05f)
			{
				RecalculateRankAndAssignPlates();
			}
			_platePool.UpdateActivePlates(_smoothedDisplayAccuracy, _currentEstimatedRank, Time.deltaTime);
		}
	}

	private void RecalculateRankAndAssignPlates()
	{
		if (_entries == null || _entries.Count == 0)
		{
			_platePool.SetAllVisible(visible: false);
			return;
		}
		_lastRecalculatedDisplayAccuracy = _smoothedDisplayAccuracy;
		_searchProbe.Accuracy = _smoothedDisplayAccuracy;
		int num = _entries.BinarySearch(_searchProbe, BeatMarathon.Models.LeaderboardEntry.DescendingAccuracyComparer.Instance);
		int num2 = ((num >= 0) ? num : (~num));
		if (num2 <= 0)
		{
			_currentEstimatedRank = 1;
		}
		else if (num2 < _entries.Count)
		{
			_currentEstimatedRank = _entries[num2].Rank;
		}
		else
		{
			_currentEstimatedRank = _entries[_entries.Count - 1].Rank + 1;
		}
		int num3 = Mathf.Clamp(_config.AheadRivalsCount, 1, 20);
		int num4 = num3 + 3;
		int num5 = Mathf.Max(0, num2 - num3);
		int num6 = Mathf.Min(_entries.Count - 1, num2 + 3 - 1);
		if (num6 - num5 + 1 < num4)
		{
			if (num5 == 0)
			{
				num6 = Mathf.Min(_entries.Count - 1, num5 + num4 - 1);
			}
			else if (num6 == _entries.Count - 1)
			{
				num5 = Mathf.Max(0, num6 - num4 + 1);
			}
		}
		_activeTargetBuffer.Clear();
		for (int i = num5; i <= num6 && i < _entries.Count; i++)
		{
			_activeTargetBuffer.Add(_entries[i]);
		}
		_platePool.AssignTargetsToPool(_activeTargetBuffer);
		CheckAndTriggerGapFetchIfNeeded(num2);
	}

	private void CheckAndTriggerGapFetchIfNeeded(int insertionIndex)
	{
		if (_entries == null || _entries.Count == 0)
		{
			return;
		}
		bool flag = false;
		if (insertionIndex >= _entries.Count - 2)
		{
			flag = true;
		}
		else if (insertionIndex > 0 && insertionIndex < _entries.Count)
		{
			int rank = _entries[insertionIndex - 1].Rank;
			if (_entries[insertionIndex].Rank - rank > 5)
			{
				flag = true;
			}
		}
		if (flag)
		{
			_leaderboardFetcher.FetchGapEntriesForAccuracyAsync(_smoothedDisplayAccuracy);
		}
	}

	private void OnLeaderboardUpdated(IReadOnlyList<BeatMarathon.Models.LeaderboardEntry> updatedEntries)
	{
		_entriesUpdatePending = true;
	}

	private void OnConfigChanged()
	{
		if (!_config.Enabled)
		{
			_platePool.SetAllVisible(visible: false);
			return;
		}
		_platePool.ApplyVerticalBarRootTransform();
		_leaderboardFetcher.RecalculateSlotsForCurrentEntries();
		_entries = _leaderboardFetcher.GetEntriesSnapshot();
		if (_isDisplayActive && _scoredNoteCount >= 20)
		{
			_rawAccuracy = GetCurrentRealtimeAccuracy();
			_targetDisplayAccuracy = CalculateEffectiveAccuracy(_rawAccuracy);
			RecalculateRankAndAssignPlates();
		}
	}

	private void OnLevelEnded()
	{
		if (!_levelEndTriggered)
		{
			_levelEndTriggered = true;
			_avatarCacheService.SetGameplayLock(locked: false);
			_rawAccuracy = GetCurrentRealtimeAccuracy();
			_leaderboardFetcher.CacheAroundFinalAccuracyAsync(_rawAccuracy);
		}
	}

	public void Dispose()
	{
		_avatarCacheService.SetGameplayLock(locked: false);
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
