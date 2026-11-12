using System;
using BeatMarathon.Services;
using UnityEngine;
using Zenject;

namespace BeatMarathon;

internal class MenuPreloadController : IInitializable, IDisposable
{
	private readonly StandardLevelDetailViewController _levelDetailViewController;

	private readonly LeaderboardFetcher _leaderboardFetcher;

	private readonly AvatarCacheService _avatarCacheService;

	private readonly PluginConfig _config;

	public MenuPreloadController([InjectOptional] StandardLevelDetailViewController levelDetailViewController, LeaderboardFetcher leaderboardFetcher, AvatarCacheService avatarCacheService, PluginConfig config)
	{
		_levelDetailViewController = levelDetailViewController;
		_leaderboardFetcher = leaderboardFetcher;
		_avatarCacheService = avatarCacheService;
		_config = config;
	}

	public void Initialize()
	{
		_avatarCacheService.SetGameplayLock(locked: false);
		_config.ConfigChangedEvent += OnConfigChanged;
		if ((UnityEngine.Object)(object)_levelDetailViewController != null)
		{
			_levelDetailViewController.didChangeDifficultyBeatmapEvent += OnDifficultyBeatmapChanged;
			_levelDetailViewController.didChangeContentEvent += OnContentChanged;
		}
	}

	private void OnConfigChanged()
	{
		TriggerPreloadIfReady();
	}

	private void OnDifficultyBeatmapChanged(StandardLevelDetailViewController controller)
	{
		TriggerPreloadIfReady();
	}

	private void OnContentChanged(StandardLevelDetailViewController controller, StandardLevelDetailViewController.ContentType contentType)
	{
		if (contentType == StandardLevelDetailViewController.ContentType.OwnedAndReady)
		{
			_avatarCacheService.SetGameplayLock(locked: false);
			TriggerPreloadIfReady();
		}
	}

	private void TriggerPreloadIfReady()
	{
		if (_config.Enabled && !((UnityEngine.Object)(object)_levelDetailViewController == null) && ((Behaviour)(object)_levelDetailViewController).isActiveAndEnabled)
		{
			BeatmapLevel beatmapLevel = _levelDetailViewController.beatmapLevel;
			BeatmapKey beatmapKey = _levelDetailViewController.beatmapKey;
			if (beatmapLevel != null && beatmapKey.IsValid())
			{
				_leaderboardFetcher.PreloadForMapAsync(beatmapLevel, beatmapKey, allowTop10AvatarDownload: true);
			}
		}
	}

	public void Dispose()
	{
		_config.ConfigChangedEvent -= OnConfigChanged;
		if ((UnityEngine.Object)(object)_levelDetailViewController != null)
		{
			_levelDetailViewController.didChangeDifficultyBeatmapEvent -= OnDifficultyBeatmapChanged;
			_levelDetailViewController.didChangeContentEvent -= OnContentChanged;
		}
	}
}
