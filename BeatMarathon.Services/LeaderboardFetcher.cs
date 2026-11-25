using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using BeatMarathon.Models;
#if BS_1_44_1
using OculusStudios.Platform.Core;
#endif
using SongCore;
using Steamworks;
using UnityEngine;
using Zenject;

namespace BeatMarathon.Services;

public class LeaderboardFetcher : IInitializable, IDisposable
{
	private static readonly HttpClient SharedHttpClient = CreateHttpClient();

#if BS_1_44_1
	private readonly IPlatform _platform;
#else
	private readonly IPlatformUserModel _platformUserModel;
#endif

	private readonly AvatarCacheService _avatarCacheService;

	private readonly PluginConfig _config;

	private readonly object _entriesLock = new object();

	private List<BeatMarathon.Models.LeaderboardEntry> _currentEntries = new List<BeatMarathon.Models.LeaderboardEntry>();

	private CancellationTokenSource _cts;

	private CancellationTokenSource _activeFetchCts;

	private string _localPlayerId = string.Empty;

	private string _localPlayerName = string.Empty;

	private string _localCountryCode = string.Empty;

	private readonly HashSet<string> _friendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private bool _userDataInitialized;

	private string _currentMapCacheKey = string.Empty;

	private Task _activePreloadTask;

	private string _currentHash = string.Empty;

	private string _currentDifficultyName = string.Empty;

	private int _currentDifficultyInt = 9;

	private string _currentModeName = string.Empty;

	private int _currentMaxScore;

	private int _currentTotalScoresCount;

	private volatile bool _isFetchingGap;

	private long _lastGapFetchTicks;

	public bool HasEntries
	{
		get
		{
			lock (_entriesLock)
			{
				return _currentEntries.Count > 0;
			}
		}
	}

	public event Action<IReadOnlyList<BeatMarathon.Models.LeaderboardEntry>> LeaderboardUpdatedEvent;

	private static HttpClient CreateHttpClient()
	{
		HttpClient httpClient = new HttpClient(new HttpClientHandler
		{
			AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate)
		});
		httpClient.Timeout = TimeSpan.FromSeconds(8.0);
#if BS_1_44_1
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BeatMarathon/1.0.0 (BeatSaber/1.44.1)");
#else
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BeatMarathon/1.0.0 (BeatSaber/1.40.8)");
#endif
		return httpClient;
	}

#if BS_1_44_1
	public LeaderboardFetcher([InjectOptional] IPlatform platform, AvatarCacheService avatarCacheService, PluginConfig config)
	{
		_platform = platform;
		_avatarCacheService = avatarCacheService;
		_config = config;
	}
#else
	public LeaderboardFetcher([InjectOptional] IPlatformUserModel platformUserModel, AvatarCacheService avatarCacheService, PluginConfig config)
	{
		_platformUserModel = platformUserModel;
		_avatarCacheService = avatarCacheService;
		_config = config;
	}
#endif

	public void Initialize()
	{
		_cts = new CancellationTokenSource();
		InitializeUserAndFriendsAsync(_cts.Token);
	}

	public List<BeatMarathon.Models.LeaderboardEntry> GetEntriesSnapshot()
	{
		lock (_entriesLock)
		{
			return new List<BeatMarathon.Models.LeaderboardEntry>(_currentEntries);
		}
	}

	public async Task PreloadForMapAsync(BeatmapLevel level, BeatmapKey key, bool allowTop10AvatarDownload)
	{
		if (!_config.Enabled || level == null || !key.IsValid())
		{
			return;
		}
		string text = ExtractCustomLevelHash(level.levelID);
		if (string.IsNullOrEmpty(text))
		{
			UpdateEntriesInternal(new List<BeatMarathon.Models.LeaderboardEntry>());
			return;
		}
		string text2 = key.difficulty.ToString();
		int num = ConvertDifficultyToScoreSaberInt(key.difficulty);
		string text3 = (((UnityEngine.Object)(object)key.beatmapCharacteristic != null && !string.IsNullOrEmpty(key.beatmapCharacteristic.serializedName)) ? key.beatmapCharacteristic.serializedName : "Standard");
		int notesCount = 0;
		if (level.beatmapBasicData != null && level.beatmapBasicData.TryGetValue((key.beatmapCharacteristic, key.difficulty), out var value) && value != null)
		{
			notesCount = value.notesCount;
		}
		string text4 = $"{text}_{text2}_{text3}_{_config.DataSource}_{_config.Scope}";
		if (string.Equals(_currentMapCacheKey, text4, StringComparison.OrdinalIgnoreCase))
		{
			if (_activePreloadTask != null && !_activePreloadTask.IsCompleted)
			{
				await _activePreloadTask;
				return;
			}
			if (HasEntries)
			{
				List<BeatMarathon.Models.LeaderboardEntry> snapshot = GetEntriesSnapshot();
				_avatarCacheService.AssignExistingCachedAvatars(snapshot);
				if (allowTop10AvatarDownload && !_avatarCacheService.IsGameplayLocked)
				{
					List<BeatMarathon.Models.LeaderboardEntry> targetEntries = snapshot.Take(10).ToList();
					await _avatarCacheService.DownloadAndCacheAvatarsAsync(targetEntries);
				}
				this.LeaderboardUpdatedEvent?.Invoke(snapshot);
				return;
			}
		}
		_activeFetchCts?.Cancel();
		_activeFetchCts?.Dispose();
		_activeFetchCts = new CancellationTokenSource();
		CancellationToken token = _activeFetchCts.Token;
		_currentMapCacheKey = text4;
		_currentHash = text;
		_currentDifficultyName = text2;
		_currentDifficultyInt = num;
		_currentModeName = text3;
		_currentMaxScore = ComputeMaxScoreFromNotesCount(notesCount);
		_currentTotalScoresCount = 0;
		_activePreloadTask = ExecutePreloadCoreAsync(text, text2, num, text3, notesCount, allowTop10AvatarDownload, token);
		await _activePreloadTask;
	}

	private async Task ExecutePreloadCoreAsync(string hash, string diffName, int diffInt, string modeName, int notesCount, bool allowTop10AvatarDownload, CancellationToken token)
	{
		try
		{
			if (!_userDataInitialized)
			{
				await InitializeUserAndFriendsAsync(token);
			}
			Plugin.Log?.Info($"[LeaderboardFetcher] Fetching leaderboard ({_config.DataSource} / {_config.Scope}) for {hash} [{diffName} {modeName}]...");
			List<BeatMarathon.Models.LeaderboardEntry> fetchedEntries;
			if (_config.DataSource == LeaderboardDataSource.BeatLeader)
			{
				fetchedEntries = await FetchBeatLeaderInitialAsync(hash, diffName, modeName, _config.Scope, token);
			}
			else
			{
				fetchedEntries = await FetchScoreSaberInitialAsync(hash, diffInt, modeName, notesCount, _config.Scope, token);
			}
			if (token.IsCancellationRequested)
			{
				return;
			}
			FinalizeAndSortEntries(fetchedEntries);
			UpdateEntriesInternal(fetchedEntries);
			Plugin.Log?.Info($"[LeaderboardFetcher] Loaded {fetchedEntries.Count} entries for {hash} [{diffName}].");
			if (!allowTop10AvatarDownload || _avatarCacheService.IsGameplayLocked || fetchedEntries.Count <= 0)
			{
				return;
			}
			List<BeatMarathon.Models.LeaderboardEntry> list = new List<BeatMarathon.Models.LeaderboardEntry>(20);
			list.AddRange(fetchedEntries.Take(10));
			int num = fetchedEntries.FindIndex((BeatMarathon.Models.LeaderboardEntry e) => e.IsSelf);
			if (num >= 0)
			{
				int num2 = Mathf.Max(0, num - 5);
				int num3 = Mathf.Min(fetchedEntries.Count - 1, num + 5);
				for (int i = num2; i <= num3; i++)
				{
					if (!list.Contains(fetchedEntries[i]))
					{
						list.Add(fetchedEntries[i]);
					}
				}
			}
			await _avatarCacheService.DownloadAndCacheAvatarsAsync(list, token);
			if (!token.IsCancellationRequested)
			{
				this.LeaderboardUpdatedEvent?.Invoke(GetEntriesSnapshot());
			}
			if (_avatarCacheService.IsGameplayLocked || fetchedEntries.Count <= 10 || token.IsCancellationRequested)
			{
				return;
			}
			Task.Run(async delegate
			{
				try
				{
					List<BeatMarathon.Models.LeaderboardEntry> remaining = (from e in fetchedEntries.Skip(10)
						where (object)e.CachedAvatar == null
						select e).ToList();
					for (int j = 0; j < remaining.Count; j += 12)
					{
						if (_avatarCacheService.IsGameplayLocked)
						{
							break;
						}
						if (token.IsCancellationRequested)
						{
							break;
						}
						List<BeatMarathon.Models.LeaderboardEntry> targetEntries = remaining.Skip(j).Take(12).ToList();
						await _avatarCacheService.DownloadAndCacheAvatarsAsync(targetEntries, token);
						if (!token.IsCancellationRequested && !_avatarCacheService.IsGameplayLocked)
						{
							this.LeaderboardUpdatedEvent?.Invoke(GetEntriesSnapshot());
						}
					}
				}
				catch
				{
				}
			}, token);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			Plugin.Log?.Warn("[LeaderboardFetcher] PreloadForMapAsync failed: " + ex2.Message);
		}
	}

	public async Task FetchGapEntriesForAccuracyAsync(double targetAccuracy)
	{
		if (_isFetchingGap || string.IsNullOrEmpty(_currentHash) || _config.Scope == RankingScope.Friends)
		{
			return;
		}
		long ticks = DateTime.UtcNow.Ticks;
		if (ticks - Interlocked.Read(ref _lastGapFetchTicks) < TimeSpan.FromSeconds(4.0).Ticks)
		{
			return;
		}
		_isFetchingGap = true;
		Interlocked.Exchange(ref _lastGapFetchTicks, ticks);
		try
		{
			CancellationToken token = ((_cts != null) ? _cts.Token : CancellationToken.None);
			await Task.Run(async delegate
			{
				List<BeatMarathon.Models.LeaderboardEntry> list = ((_config.DataSource != 0) ? (await BinarySearchScoreSaberPageByAccuracyAsync(_currentHash, _currentDifficultyInt, _currentModeName, _config.Scope, targetAccuracy, token).ConfigureAwait(continueOnCapturedContext: false)) : (await BinarySearchBeatLeaderPageByAccuracyAsync(_currentHash, _currentDifficultyName, _currentModeName, _config.Scope, targetAccuracy, token).ConfigureAwait(continueOnCapturedContext: false)));
				if (list != null && list.Count > 0)
				{
					MergeAdditionalTextEntries(list);
				}
			}, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Plugin.Log?.Debug("[LeaderboardFetcher] Gap text fetch skipped: " + ex.Message);
		}
		finally
		{
			_isFetchingGap = false;
		}
	}

	public async Task CacheAroundFinalAccuracyAsync(double finalAccuracy)
	{
		try
		{
			_avatarCacheService.SetGameplayLock(locked: false);
			List<BeatMarathon.Models.LeaderboardEntry> entriesSnapshot = GetEntriesSnapshot();
			if (entriesSnapshot.Count != 0)
			{
				BeatMarathon.Models.LeaderboardEntry item = new BeatMarathon.Models.LeaderboardEntry
				{
					Accuracy = finalAccuracy
				};
				int num = entriesSnapshot.BinarySearch(item, BeatMarathon.Models.LeaderboardEntry.DescendingAccuracyComparer.Instance);
				if (num < 0)
				{
					num = ~num;
				}
				int num2 = Mathf.Max(0, num - 5);
				int num3 = Mathf.Min(entriesSnapshot.Count - 1, num + 4);
				List<BeatMarathon.Models.LeaderboardEntry> list = new List<BeatMarathon.Models.LeaderboardEntry>(10);
				for (int i = num2; i <= num3; i++)
				{
					list.Add(entriesSnapshot[i]);
				}
				Plugin.Log?.Info($"[LeaderboardFetcher] Level ended. Caching avatars around index {num} ({list.Count} players).");
				CancellationToken cancellationToken = ((_cts != null) ? _cts.Token : CancellationToken.None);
				await _avatarCacheService.DownloadAndCacheAvatarsAsync(list, cancellationToken);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.Warn("[LeaderboardFetcher] Failed to cache avatars on level end: " + ex.Message);
		}
	}

	private async Task InitializeUserAndFriendsAsync(CancellationToken token)
	{
		if (_userDataInitialized)
		{
			return;
		}
		try
		{
			CSteamID steamID = SteamUser.GetSteamID();
			if (steamID.IsValid() && steamID.m_SteamID != 0L)
			{
				_localPlayerId = steamID.m_SteamID.ToString(CultureInfo.InvariantCulture);
			}
			int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
			for (int i = 0; i < friendCount; i++)
			{
				CSteamID friendByIndex = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
				if (friendByIndex.IsValid() && friendByIndex.m_SteamID != 0L)
				{
					_friendIds.Add(friendByIndex.m_SteamID.ToString(CultureInfo.InvariantCulture));
				}
			}
		}
		catch
		{
		}
		try
		{
#if BS_1_44_1
			if (_platform?.user != null)
			{
				if (string.IsNullOrEmpty(_localPlayerId) && _platform.user.userId != 0UL)
				{
					_localPlayerId = _platform.user.userId.ToString(CultureInfo.InvariantCulture);
				}
				if (!string.IsNullOrEmpty(_platform.user.displayName))
				{
					_localPlayerName = _platform.user.displayName;
				}
			}
			await Task.CompletedTask;
#else
			if (_platformUserModel != null)
			{
				Task<UserInfo> userInfoTask = _platformUserModel.GetUserInfo(token);
				if (await Task.WhenAny(userInfoTask, Task.Delay(2000, token)) == userInfoTask && !userInfoTask.IsFaulted && userInfoTask.Result != null)
				{
					if (string.IsNullOrEmpty(_localPlayerId))
					{
						_localPlayerId = userInfoTask.Result.platformUserId ?? string.Empty;
					}
					_localPlayerName = userInfoTask.Result.userName ?? string.Empty;
				}
				try
				{
					Task<IReadOnlyList<string>> friendTask = _platformUserModel.GetUserFriendsUserIds(cached: true);
					if (await Task.WhenAny(friendTask, Task.Delay(1500, token)) == friendTask && !friendTask.IsFaulted && friendTask.Result != null)
					{
						foreach (string item in friendTask.Result)
						{
							if (!string.IsNullOrEmpty(item))
							{
								_friendIds.Add(item);
							}
						}
					}
				}
				catch
				{
				}
			}
#endif
		}
		catch (Exception ex)
		{
			Plugin.Log?.Debug("[LeaderboardFetcher] PlatformUserModel init warning: " + ex.Message);
		}
		if (!string.IsNullOrEmpty(_localPlayerId))
		{
			_friendIds.Add(_localPlayerId);
		}
		try
		{
			_localCountryCode = RegionInfo.CurrentRegion.TwoLetterISORegionName;
		}
		catch
		{
			_localCountryCode = "JP";
		}
		_userDataInitialized = true;
		if (!string.IsNullOrEmpty(_localPlayerId))
		{
			Task.Run(async delegate
			{
				string text = await FetchPlayerCountryCodeAsync(_localPlayerId, token).ConfigureAwait(continueOnCapturedContext: false);
				if (!string.IsNullOrEmpty(text))
				{
					_localCountryCode = text;
				}
			}, token);
		}
		Plugin.Log?.Info($"[LeaderboardFetcher] User initialized: ID={_localPlayerId}, Country={_localCountryCode}, Friends={_friendIds.Count}");
	}

	private async Task<string> FetchPlayerCountryCodeAsync(string playerId, CancellationToken token)
	{
		string text = await SendGetRequestAsync("https://api.beatleader.xyz/player/" + playerId, token);
		if (!string.IsNullOrEmpty(text))
		{
			try
			{
				string text2 = (string?)JObject.Parse(text)["country"];
				if (!string.IsNullOrEmpty(text2) && text2 != "not set")
				{
					return text2.ToUpperInvariant();
				}
			}
			catch
			{
			}
		}
		string text3 = await SendGetRequestAsync("https://scoresaber.com/api/player/" + playerId + "/basic", token);
		if (!string.IsNullOrEmpty(text3))
		{
			try
			{
				string text4 = (string?)JObject.Parse(text3)["country"];
				if (!string.IsNullOrEmpty(text4))
				{
					return text4.ToUpperInvariant();
				}
			}
			catch
			{
			}
		}
		return string.Empty;
	}

	private static string GetBeatLeaderScopeSegment(RankingScope scope)
	{
		return scope switch
		{
			RankingScope.Country => "country", 
			RankingScope.Friends => "friends", 
			_ => "global", 
		};
	}

	private async Task<List<BeatMarathon.Models.LeaderboardEntry>> FetchBeatLeaderInitialAsync(string hash, string diffName, string modeName, RankingScope scope, CancellationToken token)
	{
		Dictionary<string, BeatMarathon.Models.LeaderboardEntry> result = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
		string scopeSegment = GetBeatLeaderScopeSegment(scope);
		string text = ((!string.IsNullOrEmpty(_localPlayerId)) ? ("&player=" + Uri.EscapeDataString(_localPlayerId)) : string.Empty);
		string text2 = await SendGetRequestAsync("https://api.beatleader.xyz/v3/scores/" + hash + "/" + diffName + "/" + modeName + "/general/" + scopeSegment + "/page?page=1&count=100" + text, token).ConfigureAwait(continueOnCapturedContext: false);
		if (!string.IsNullOrEmpty(text2))
		{
			ParseBeatLeaderScoresResponse(text2, result, out var totalCount);
			_currentTotalScoresCount = totalCount;
		}
		if (!string.IsNullOrEmpty(_localPlayerId) && !result.ContainsKey(_localPlayerId))
		{
			string text3 = await SendGetRequestAsync("https://api.beatleader.xyz/v3/scores/" + hash + "/" + diffName + "/" + modeName + "/general/" + scopeSegment + "/around?player=" + Uri.EscapeDataString(_localPlayerId) + "&count=100", token).ConfigureAwait(continueOnCapturedContext: false);
			if (!string.IsNullOrEmpty(text3))
			{
				ParseBeatLeaderScoresResponse(text3, result, out var _);
			}
		}
		if (scope == RankingScope.Friends && result.Count < 3 && _friendIds.Count > 1)
		{
			return await BuildBeatLeaderFriendsLeaderboardAsync(hash, diffName, modeName, result.Values, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		return result.Values.ToList();
	}

	private async Task<List<BeatMarathon.Models.LeaderboardEntry>> BuildBeatLeaderFriendsLeaderboardAsync(string hash, string diffName, string modeName, IEnumerable<BeatMarathon.Models.LeaderboardEntry> initialEntries, CancellationToken token)
	{
		Dictionary<string, BeatMarathon.Models.LeaderboardEntry> friendEntries = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (BeatMarathon.Models.LeaderboardEntry initialEntry in initialEntries)
		{
			friendEntries[initialEntry.PlayerId] = initialEntry;
		}
		string text = await SendGetRequestAsync("https://api.beatleader.xyz/v3/scores/" + hash + "/" + diffName + "/" + modeName + "/general/global/page?page=1&count=100", token).ConfigureAwait(continueOnCapturedContext: false);
		if (!string.IsNullOrEmpty(text))
		{
			Dictionary<string, BeatMarathon.Models.LeaderboardEntry> dictionary = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
			ParseBeatLeaderScoresResponse(text, dictionary, out var _);
			foreach (KeyValuePair<string, BeatMarathon.Models.LeaderboardEntry> item in dictionary)
			{
				if (_friendIds.Contains(item.Key))
				{
					friendEntries[item.Key] = item.Value;
				}
			}
		}
		List<string> missingFriends = _friendIds.Where((string id) => !friendEntries.ContainsKey(id)).Take(25).ToList();
		for (int i = 0; i < missingFriends.Count; i += 5)
		{
			if (token.IsCancellationRequested)
			{
				break;
			}
			await Task.WhenAll(missingFriends.Skip(i).Take(5).Select((Func<string, Task>)async delegate(string friendId)
			{
				string text2 = await SendGetRequestAsync("https://api.beatleader.xyz/v3/scores/" + hash + "/" + diffName + "/" + modeName + "/general/global/around?player=" + Uri.EscapeDataString(friendId) + "&count=1", token).ConfigureAwait(continueOnCapturedContext: false);
				if (!string.IsNullOrEmpty(text2))
				{
					Dictionary<string, BeatMarathon.Models.LeaderboardEntry> dictionary2 = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
					ParseBeatLeaderScoresResponse(text2, dictionary2, out var _);
					if (dictionary2.TryGetValue(friendId, out var value))
					{
						lock (friendEntries)
						{
							friendEntries[friendId] = value;
						}
					}
				}
			})).ConfigureAwait(continueOnCapturedContext: false);
		}
		List<BeatMarathon.Models.LeaderboardEntry> list = friendEntries.Values.OrderByDescending((BeatMarathon.Models.LeaderboardEntry e) => e.Accuracy).ToList();
		for (int j = 0; j < list.Count; j++)
		{
			list[j].Rank = j + 1;
		}
		return list;
	}

	private async Task<List<BeatMarathon.Models.LeaderboardEntry>> BinarySearchBeatLeaderPageByAccuracyAsync(string hash, string diffName, string modeName, RankingScope scope, double targetAccuracy, CancellationToken token)
	{
		if (_currentTotalScoresCount <= 100)
		{
			return null;
		}
		string scopeSegment = GetBeatLeaderScopeSegment(scope);
		string playerQuery = ((!string.IsNullOrEmpty(_localPlayerId)) ? ("&player=" + Uri.EscapeDataString(_localPlayerId)) : string.Empty);
		int a = Mathf.CeilToInt((float)_currentTotalScoresCount / 100f);
		int lowPage = 2;
		int highPage = Mathf.Min(a, 50);
		int bestPage = lowPage;
		int iterations = 0;
		int totalCount;
		while (lowPage <= highPage && iterations < 5)
		{
			totalCount = iterations++;
			int midPage = (lowPage + highPage) / 2;
			string text = await SendGetRequestAsync($"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/{scopeSegment}/page?page={midPage}&count=100{playerQuery}", token).ConfigureAwait(continueOnCapturedContext: false);
			if (string.IsNullOrEmpty(text))
			{
				break;
			}
			Dictionary<string, BeatMarathon.Models.LeaderboardEntry> dictionary = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
			ParseBeatLeaderScoresResponse(text, dictionary, out totalCount);
			if (dictionary.Count == 0)
			{
				break;
			}
			List<BeatMarathon.Models.LeaderboardEntry> list = dictionary.Values.OrderByDescending((BeatMarathon.Models.LeaderboardEntry e) => e.Accuracy).ToList();
			double accuracy = list[0].Accuracy;
			double accuracy2 = list[list.Count - 1].Accuracy;
			if (targetAccuracy <= accuracy && targetAccuracy >= accuracy2)
			{
				return list;
			}
			bestPage = midPage;
			if (targetAccuracy > accuracy)
			{
				highPage = midPage - 1;
			}
			else
			{
				lowPage = midPage + 1;
			}
		}
		string text2 = await SendGetRequestAsync($"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/{scopeSegment}/page?page={bestPage}&count=100{playerQuery}", token).ConfigureAwait(continueOnCapturedContext: false);
		if (!string.IsNullOrEmpty(text2))
		{
			Dictionary<string, BeatMarathon.Models.LeaderboardEntry> dictionary2 = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
			ParseBeatLeaderScoresResponse(text2, dictionary2, out totalCount);
			return dictionary2.Values.ToList();
		}
		return null;
	}

	private static void ParseBeatLeaderScoresResponse(string json, Dictionary<string, BeatMarathon.Models.LeaderboardEntry> targetDict, out int totalCount)
	{
		totalCount = 0;
		try
		{
			JObject jObject = JObject.Parse(json);
			totalCount = ((int?)jObject["metadata"]?["total"]).GetValueOrDefault();
			if (!(jObject["data"] is JArray jArray))
			{
				return;
			}
			foreach (JToken item in jArray)
			{
				JToken jToken = item["player"];
				string text = ((string?)jToken?["id"]) ?? ((string?)item["playerId"]) ?? string.Empty;
				if (!string.IsNullOrEmpty(text))
				{
					double valueOrDefault = ((double?)item["accuracy"]).GetValueOrDefault();
					if (!(valueOrDefault <= 0.0) && !(valueOrDefault > 1.5))
					{
						int valueOrDefault2 = ((int?)item["rank"]).GetValueOrDefault();
						string playerName = ((string?)jToken?["name"]) ?? "Unknown";
						string avatarUrl = ((string?)jToken?["avatar"]) ?? string.Empty;
						targetDict[text] = new BeatMarathon.Models.LeaderboardEntry
						{
							PlayerId = text,
							PlayerName = playerName,
							Rank = valueOrDefault2,
							Accuracy = valueOrDefault,
							AvatarUrl = avatarUrl
						};
					}
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.Debug("[LeaderboardFetcher] ParseBeatLeaderScoresResponse error: " + ex.Message);
		}
	}

	private static BeatMarathon.Models.LeaderboardEntry TryParseSingleBeatLeaderScore(string json)
	{
		try
		{
			JObject jObject = JObject.Parse(json);
			JToken jToken = jObject["player"];
			string text = ((string?)jToken?["id"]) ?? ((string?)jObject["playerId"]) ?? string.Empty;
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			double valueOrDefault = ((double?)jObject["accuracy"]).GetValueOrDefault();
			if (valueOrDefault <= 0.0)
			{
				return null;
			}
			return new BeatMarathon.Models.LeaderboardEntry
			{
				PlayerId = text,
				PlayerName = (((string?)jToken?["name"]) ?? "Friend"),
				Rank = ((int?)jObject["rank"]).GetValueOrDefault(),
				Accuracy = valueOrDefault,
				AvatarUrl = (((string?)jToken?["avatar"]) ?? string.Empty)
			};
		}
		catch
		{
			return null;
		}
	}

	private async Task<List<BeatMarathon.Models.LeaderboardEntry>> FetchScoreSaberInitialAsync(string hash, int diffInt, string modeName, int fallbackNotesCount, RankingScope scope, CancellationToken token)
	{
		Dictionary<string, BeatMarathon.Models.LeaderboardEntry> result = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
		string text = await SendGetRequestAsync($"https://scoresaber.com/api/leaderboard/by-hash/{hash}/info?difficulty={diffInt}&gameMode=Solo{modeName}", token);
		int maxScore = ComputeMaxScoreFromNotesCount(fallbackNotesCount);
		if (!string.IsNullOrEmpty(text))
		{
			try
			{
				JObject jObject = JObject.Parse(text);
				int valueOrDefault = ((int?)jObject["maxScore"]).GetValueOrDefault();
				if (valueOrDefault > 0)
				{
					maxScore = valueOrDefault;
				}
				_currentTotalScoresCount = ((int?)jObject["plays"]).GetValueOrDefault();
			}
			catch
			{
			}
		}
		_currentMaxScore = maxScore;
		string countryParam = ((scope == RankingScope.Country && !string.IsNullOrEmpty(_localCountryCode)) ? ("&countries=" + Uri.EscapeDataString(_localCountryCode)) : string.Empty);
		int pagesToFetch = ((scope == RankingScope.Friends) ? 12 : 9);
		for (int startPage = 1; startPage <= pagesToFetch; startPage += 3)
		{
			if (token.IsCancellationRequested)
			{
				break;
			}
			int num = Mathf.Min(pagesToFetch, startPage + 3 - 1);
			List<Task<string>> list = new List<Task<string>>();
			for (int i = startPage; i <= num; i++)
			{
				string url = $"https://scoresaber.com/api/leaderboard/by-hash/{hash}/scores?difficulty={diffInt}&gameMode=Solo{modeName}&page={i}{countryParam}";
				list.Add(SendGetRequestAsync(url, token));
			}
			string[] obj2 = await Task.WhenAll(list);
			bool flag = false;
			string[] array = obj2;
			foreach (string text2 in array)
			{
				if (string.IsNullOrEmpty(text2))
				{
					flag = true;
					continue;
				}
				int totalCount;
				int num2 = ParseScoreSaberScoresResponse(text2, maxScore, result, out totalCount);
				if (totalCount > 0)
				{
					_currentTotalScoresCount = totalCount;
				}
				if (num2 < 12)
				{
					flag = true;
				}
			}
			if (flag)
			{
				break;
			}
		}
		if (scope == RankingScope.Friends)
		{
			List<BeatMarathon.Models.LeaderboardEntry> list2 = (from e in result.Values
				where _friendIds.Contains(e.PlayerId)
				orderby e.Accuracy descending
				select e).ToList();
			for (int k = 0; k < list2.Count; k++)
			{
				list2[k].Rank = k + 1;
			}
			return list2;
		}
		return result.Values.ToList();
	}

	private async Task<List<BeatMarathon.Models.LeaderboardEntry>> BinarySearchScoreSaberPageByAccuracyAsync(string hash, int diffInt, string modeName, RankingScope scope, double targetAccuracy, CancellationToken token)
	{
		if (_currentTotalScoresCount <= 108 || _currentMaxScore <= 0)
		{
			return null;
		}
		string countryParam = ((scope == RankingScope.Country && !string.IsNullOrEmpty(_localCountryCode)) ? ("&countries=" + Uri.EscapeDataString(_localCountryCode)) : string.Empty);
		int totalPages = Mathf.CeilToInt((float)_currentTotalScoresCount / 12f);
		int lowPage = 10;
		int highPage = Mathf.Min(totalPages, 250);
		int bestPage = lowPage;
		int iterations = 0;
		int totalCount;
		int midPage2;
		while (lowPage <= highPage && iterations < 6)
		{
			totalCount = iterations++;
			midPage2 = (lowPage + highPage) / 2;
			string text = await SendGetRequestAsync($"https://scoresaber.com/api/leaderboard/by-hash/{hash}/scores?difficulty={diffInt}&gameMode=Solo{modeName}&page={midPage2}{countryParam}", token);
			if (string.IsNullOrEmpty(text))
			{
				break;
			}
			Dictionary<string, BeatMarathon.Models.LeaderboardEntry> dictionary = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
			ParseScoreSaberScoresResponse(text, _currentMaxScore, dictionary, out totalCount);
			if (dictionary.Count == 0)
			{
				break;
			}
			List<BeatMarathon.Models.LeaderboardEntry> list = dictionary.Values.OrderByDescending((BeatMarathon.Models.LeaderboardEntry e) => e.Accuracy).ToList();
			double accuracy = list[0].Accuracy;
			double accuracy2 = list[list.Count - 1].Accuracy;
			bestPage = midPage2;
			if (targetAccuracy <= accuracy && targetAccuracy >= accuracy2)
			{
				break;
			}
			if (targetAccuracy > accuracy)
			{
				highPage = midPage2 - 1;
			}
			else
			{
				lowPage = midPage2 + 1;
			}
		}
		Dictionary<string, BeatMarathon.Models.LeaderboardEntry> aroundDict = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
		int num = Mathf.Max(10, bestPage - 4);
		int endP = Mathf.Min(totalPages, bestPage + 4);
		midPage2 = num;
		while (midPage2 <= endP && !token.IsCancellationRequested)
		{
			string text2 = await SendGetRequestAsync($"https://scoresaber.com/api/leaderboard/by-hash/{hash}/scores?difficulty={diffInt}&gameMode=Solo{modeName}&page={midPage2}{countryParam}", token);
			if (!string.IsNullOrEmpty(text2))
			{
				ParseScoreSaberScoresResponse(text2, _currentMaxScore, aroundDict, out totalCount);
			}
			totalCount = midPage2++;
		}
		return aroundDict.Values.ToList();
	}

	private static int ParseScoreSaberScoresResponse(string json, int maxScore, Dictionary<string, BeatMarathon.Models.LeaderboardEntry> targetDict, out int totalCount)
	{
		totalCount = 0;
		int num = 0;
		try
		{
			JObject jObject = JObject.Parse(json);
			totalCount = ((int?)jObject["metadata"]?["total"]).GetValueOrDefault();
			if (!(jObject["scores"] is JArray jArray))
			{
				return 0;
			}
			foreach (JToken item in jArray)
			{
				JToken jToken = item["leaderboardPlayerInfo"];
				string text = ((string?)jToken?["id"]) ?? string.Empty;
				if (!string.IsNullOrEmpty(text))
				{
					int valueOrDefault = ((int?)item["baseScore"]).GetValueOrDefault();
					int valueOrDefault2 = ((int?)item["rank"]).GetValueOrDefault();
					double num2 = ((maxScore > 0 && valueOrDefault > 0) ? ((double)valueOrDefault / (double)maxScore) : 0.0);
					if (!(num2 <= 0.0) && !(num2 > 1.5))
					{
						string playerName = ((string?)jToken?["name"]) ?? "Unknown";
						string avatarUrl = ((string?)jToken?["profilePicture"]) ?? string.Empty;
						targetDict[text] = new BeatMarathon.Models.LeaderboardEntry
						{
							PlayerId = text,
							PlayerName = playerName,
							Rank = valueOrDefault2,
							Accuracy = num2,
							AvatarUrl = avatarUrl
						};
						num++;
					}
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log?.Debug("[LeaderboardFetcher] ParseScoreSaberScoresResponse error: " + ex.Message);
		}
		return num;
	}

	private void FinalizeAndSortEntries(List<BeatMarathon.Models.LeaderboardEntry> entries)
	{
		if (entries != null)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				BeatMarathon.Models.LeaderboardEntry leaderboardEntry = entries[i];
				leaderboardEntry.IsSelf = !string.IsNullOrEmpty(_localPlayerId) && string.Equals(leaderboardEntry.PlayerId, _localPlayerId, StringComparison.OrdinalIgnoreCase);
			}
			_avatarCacheService.AssignExistingCachedAvatars(entries);
			BeatMarathon.Models.LeaderboardEntry.SortAndAssignSlots(entries, _config);
		}
	}

	private void MergeAdditionalTextEntries(List<BeatMarathon.Models.LeaderboardEntry> additionalEntries)
	{
		if (additionalEntries == null || additionalEntries.Count == 0)
		{
			return;
		}
		List<BeatMarathon.Models.LeaderboardEntry> list;
		lock (_entriesLock)
		{
			Dictionary<string, BeatMarathon.Models.LeaderboardEntry> dictionary = new Dictionary<string, BeatMarathon.Models.LeaderboardEntry>(_currentEntries.Count + additionalEntries.Count, StringComparer.OrdinalIgnoreCase);
			foreach (BeatMarathon.Models.LeaderboardEntry currentEntry in _currentEntries)
			{
				dictionary[currentEntry.PlayerId] = currentEntry;
			}
			foreach (BeatMarathon.Models.LeaderboardEntry additionalEntry in additionalEntries)
			{
				if (!dictionary.ContainsKey(additionalEntry.PlayerId))
				{
					dictionary[additionalEntry.PlayerId] = additionalEntry;
				}
			}
			list = dictionary.Values.ToList();
		}
		FinalizeAndSortEntries(list);
		UpdateEntriesInternal(list);
	}

	private void UpdateEntriesInternal(List<BeatMarathon.Models.LeaderboardEntry> newEntries)
	{
		lock (_entriesLock)
		{
			_currentEntries = newEntries ?? new List<BeatMarathon.Models.LeaderboardEntry>();
		}
		this.LeaderboardUpdatedEvent?.Invoke(GetEntriesSnapshot());
	}

	public void RecalculateSlotsForCurrentEntries()
	{
		List<BeatMarathon.Models.LeaderboardEntry> entriesSnapshot = GetEntriesSnapshot();
		if (entriesSnapshot.Count != 0)
		{
			BeatMarathon.Models.LeaderboardEntry.SortAndAssignSlots(entriesSnapshot, _config);
			UpdateEntriesInternal(entriesSnapshot);
		}
	}

	private static async Task<string> SendGetRequestAsync(string url, CancellationToken token)
	{
		_ = 1;
		try
		{
			using HttpResponseMessage response = await SharedHttpClient.GetAsync(url, token).ConfigureAwait(continueOnCapturedContext: false);
			if (response.IsSuccessStatusCode)
			{
				return await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch
		{
		}
		return null;
	}

	private static string ExtractCustomLevelHash(string levelId)
	{
		if (string.IsNullOrEmpty(levelId))
		{
			return string.Empty;
		}
		try
		{
			string customLevelHash = Collections.GetCustomLevelHash(levelId);
			if (!string.IsNullOrEmpty(customLevelHash))
			{
				return customLevelHash;
			}
		}
		catch
		{
		}
		if (levelId.StartsWith("custom_level_", StringComparison.OrdinalIgnoreCase))
		{
			string text = levelId.Substring("custom_level_".Length);
			if (text.Length >= 40)
			{
				return text.Substring(0, 40);
			}
			return text;
		}
		return string.Empty;
	}

	private static int ConvertDifficultyToScoreSaberInt(BeatmapDifficulty difficulty)
	{
		return difficulty switch
		{
			BeatmapDifficulty.Easy => 1, 
			BeatmapDifficulty.Normal => 3, 
			BeatmapDifficulty.Hard => 5, 
			BeatmapDifficulty.Expert => 7, 
			BeatmapDifficulty.ExpertPlus => 9, 
			_ => 9, 
		};
	}

	public static int ComputeMaxScoreFromNotesCount(int notesCount)
	{
		if (notesCount <= 0)
		{
			return 0;
		}
		if (notesCount == 1)
		{
			return 115;
		}
		if (notesCount <= 5)
		{
			return 115 + (notesCount - 1) * 230;
		}
		if (notesCount <= 13)
		{
			return 1035 + (notesCount - 5) * 460;
		}
		return 4715 + (notesCount - 13) * 920;
	}

	public void Dispose()
	{
		_activeFetchCts?.Cancel();
		_activeFetchCts?.Dispose();
		_activeFetchCts = null;
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;
	}
}
