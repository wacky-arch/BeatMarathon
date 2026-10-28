using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RealtimeRankMarathon.Models;
using Steamworks;
using UnityEngine;
using Zenject;

namespace RealtimeRankMarathon.Services
{
    using LeaderboardEntry = RealtimeRankMarathon.Models.LeaderboardEntry;

    public class LeaderboardFetcher : IInitializable, IDisposable
    {
        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RealtimeRankMarathon/1.0.0 (BeatSaber/1.40.8)");
            return client;
        }
        private readonly IPlatformUserModel _platformUserModel;
        private readonly AvatarCacheService _avatarCacheService;
        private readonly PluginConfig _config;

        private readonly object _entriesLock = new object();
        private List<LeaderboardEntry> _currentEntries = new List<LeaderboardEntry>();

        private CancellationTokenSource _cts;
        private CancellationTokenSource _activeFetchCts;

        private string _localPlayerId = string.Empty;
        private string _localPlayerName = string.Empty;
        private string _localCountryCode = string.Empty;
        private readonly HashSet<string> _friendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _userDataInitialized;

        // 現在プリロード済み/ロード中の譜面キー情報
        private string _currentMapCacheKey = string.Empty;
        private Task _activePreloadTask;
        private string _currentHash = string.Empty;
        private string _currentDifficultyName = string.Empty;
        private int _currentDifficultyInt = 9;
        private string _currentModeName = string.Empty;
        private int _currentMaxScore = 0;
        private int _currentTotalScoresCount = 0;

        private volatile bool _isFetchingGap;
        private long _lastGapFetchTicks;

        public event Action<IReadOnlyList<LeaderboardEntry>> LeaderboardUpdatedEvent;

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

        public LeaderboardFetcher(
            [InjectOptional] IPlatformUserModel platformUserModel,
            AvatarCacheService avatarCacheService,
            PluginConfig config)
        {
            _platformUserModel = platformUserModel;
            _avatarCacheService = avatarCacheService;
            _config = config;
        }

        public void Initialize()
        {
            _cts = new CancellationTokenSource();
            _ = InitializeUserAndFriendsAsync(_cts.Token);
        }

        /// <summary>
        /// 現在のリーダーボードエントリのスナップショット（精度降順）を取得する
        /// </summary>
        public List<LeaderboardEntry> GetEntriesSnapshot()
        {
            lock (_entriesLock)
            {
                return new List<LeaderboardEntry>(_currentEntries);
            }
        }

        /// <summary>
        /// 曲開始前（メニュー選択時または導入部）に対象譜面のTOP100（＋自己ベストが101位以下なら前後50人）のテキストと、
        /// TOP10の未キャッシュアイコンを取得する。
        /// </summary>
        public async Task PreloadForMapAsync(BeatmapLevel level, BeatmapKey key, bool allowTop10AvatarDownload)
        {
            if (!_config.Enabled || level == null || !key.IsValid())
            {
                return;
            }

            string hash = ExtractCustomLevelHash(level.levelID);
            if (string.IsNullOrEmpty(hash))
            {
                // 公式譜面等でハッシュがない場合はクリア
                UpdateEntriesInternal(new List<LeaderboardEntry>());
                return;
            }

            string diffName = key.difficulty.ToString();
            int diffInt = ConvertDifficultyToScoreSaberInt(key.difficulty);
            string modeName = key.beatmapCharacteristic != null && !string.IsNullOrEmpty(key.beatmapCharacteristic.serializedName)
                ? key.beatmapCharacteristic.serializedName
                : "Standard";

            int notesCount = 0;
            if (level.beatmapBasicData != null &&
                level.beatmapBasicData.TryGetValue((key.beatmapCharacteristic, key.difficulty), out var basicData) &&
                basicData != null)
            {
                notesCount = basicData.notesCount;
            }

            string mapCacheKey = $"{hash}_{diffName}_{modeName}_{_config.DataSource}_{_config.Scope}";
            if (string.Equals(_currentMapCacheKey, mapCacheKey, StringComparison.OrdinalIgnoreCase))
            {
                // 既に同じ譜面・設定で通信中のタスクが走っている場合はキャンセルせずその完了を待つ
                if (_activePreloadTask != null && !_activePreloadTask.IsCompleted)
                {
                    await _activePreloadTask;
                    return;
                }

                // 既に取得完了している場合は既存キャッシュアイコンの再割り当てとTOP10補完のみ行う
                if (HasEntries)
                {
                    var snapshot = GetEntriesSnapshot();
                    _avatarCacheService.AssignExistingCachedAvatars(snapshot);
                    if (allowTop10AvatarDownload && !_avatarCacheService.IsGameplayLocked)
                    {
                        var top10 = snapshot.Take(10).ToList();
                        await _avatarCacheService.DownloadAndCacheAvatarsAsync(top10);
                    }
                    LeaderboardUpdatedEvent?.Invoke(snapshot);
                    return;
                }
            }

            _activeFetchCts?.Cancel();
            _activeFetchCts?.Dispose();
            _activeFetchCts = new CancellationTokenSource();
            var token = _activeFetchCts.Token;

            _currentMapCacheKey = mapCacheKey;
            _currentHash = hash;
            _currentDifficultyName = diffName;
            _currentDifficultyInt = diffInt;
            _currentModeName = modeName;
            _currentMaxScore = ComputeMaxScoreFromNotesCount(notesCount);
            _currentTotalScoresCount = 0;

            _activePreloadTask = ExecutePreloadCoreAsync(hash, diffName, diffInt, modeName, notesCount, allowTop10AvatarDownload, token);
            await _activePreloadTask;
        }

        private async Task ExecutePreloadCoreAsync(
            string hash,
            string diffName,
            int diffInt,
            string modeName,
            int notesCount,
            bool allowTop10AvatarDownload,
            CancellationToken token)
        {
            try
            {
                if (!_userDataInitialized)
                {
                    await InitializeUserAndFriendsAsync(token);
                }

                Plugin.Log?.Info($"[LeaderboardFetcher] Fetching leaderboard ({_config.DataSource} / {_config.Scope}) for {hash} [{diffName} {modeName}]...");

                List<LeaderboardEntry> fetchedEntries;
                if (_config.DataSource == LeaderboardDataSource.BeatLeader)
                {
                    fetchedEntries = await FetchBeatLeaderInitialAsync(hash, diffName, modeName, _config.Scope, token);
                }
                else
                {
                    fetchedEntries = await FetchScoreSaberInitialAsync(hash, diffInt, modeName, notesCount, _config.Scope, token);
                }

                if (token.IsCancellationRequested) return;

                // プレイヤー自身のフラグ設定＆既存キャッシュ画像の割り当て
                FinalizeAndSortEntries(fetchedEntries);
                UpdateEntriesInternal(fetchedEntries);

                Plugin.Log?.Info($"[LeaderboardFetcher] Loaded {fetchedEntries.Count} entries for {hash} [{diffName}].");

                // 1. TOP 10（1位〜10位）および自己ベスト前後5人の未キャッシュアイコンを最優先でダウンロード
                if (allowTop10AvatarDownload && !_avatarCacheService.IsGameplayLocked && fetchedEntries.Count > 0)
                {
                    var priorityEntries = new List<LeaderboardEntry>(20);
                    priorityEntries.AddRange(fetchedEntries.Take(10));

                    int selfIdx = fetchedEntries.FindIndex(e => e.IsSelf);
                    if (selfIdx >= 0)
                    {
                        int startIdx = Mathf.Max(0, selfIdx - 5);
                        int endIdx = Mathf.Min(fetchedEntries.Count - 1, selfIdx + 5);
                        for (int i = startIdx; i <= endIdx; i++)
                        {
                            if (!priorityEntries.Contains(fetchedEntries[i]))
                            {
                                priorityEntries.Add(fetchedEntries[i]);
                            }
                        }
                    }

                    await _avatarCacheService.DownloadAndCacheAvatarsAsync(priorityEntries, token);
                    if (!token.IsCancellationRequested)
                    {
                        LeaderboardUpdatedEvent?.Invoke(GetEntriesSnapshot());
                    }

                    // 2. メニュー待機中（プレイロック前）であれば、11位以降の未キャッシュアイコンもバックグラウンドで順次プリロード
                    if (!_avatarCacheService.IsGameplayLocked && fetchedEntries.Count > 10 && !token.IsCancellationRequested)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var remaining = fetchedEntries.Skip(10).Where(e => ReferenceEquals(e.CachedAvatar, null)).ToList();
                                const int bgBatchSize = 12;
                                for (int i = 0; i < remaining.Count; i += bgBatchSize)
                                {
                                    if (_avatarCacheService.IsGameplayLocked || token.IsCancellationRequested)
                                    {
                                        break;
                                    }

                                    var batch = remaining.Skip(i).Take(bgBatchSize).ToList();
                                    await _avatarCacheService.DownloadAndCacheAvatarsAsync(batch, token);
                                    if (!token.IsCancellationRequested && !_avatarCacheService.IsGameplayLocked)
                                    {
                                        LeaderboardUpdatedEvent?.Invoke(GetEntriesSnapshot());
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[LeaderboardFetcher] PreloadForMapAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// プレイ中に未取得順位帯（ギャップまたは100位以下）に現在精度が位置している場合、
        /// バックグラウンドでAPIページネーションを用いた二分探索を行い、該当順位ページ（前後50人分）のテキストデータのみを追加取得する。
        /// ※ 画像通信・Sprite生成は一切行わず、既存の PlayerId キャッシュ照合のみ行う。
        /// </summary>
        public async Task FetchGapEntriesForAccuracyAsync(double targetAccuracy)
        {
            if (_isFetchingGap || string.IsNullOrEmpty(_currentHash) || _config.Scope == RankingScope.Friends)
            {
                return;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - Interlocked.Read(ref _lastGapFetchTicks) < TimeSpan.FromSeconds(4).Ticks)
            {
                return;
            }

            _isFetchingGap = true;
            Interlocked.Exchange(ref _lastGapFetchTicks, nowTicks);

            try
            {
                var token = _cts != null ? _cts.Token : CancellationToken.None;

                // プレイ中のメインスレッドブロックを100%防ぐため、通信・JSONパース・マージ計算をすべてバックグラウンドスレッドで実行
                await Task.Run(async () =>
                {
                    List<LeaderboardEntry> additionalEntries = null;

                    if (_config.DataSource == LeaderboardDataSource.BeatLeader)
                    {
                        additionalEntries = await BinarySearchBeatLeaderPageByAccuracyAsync(
                            _currentHash,
                            _currentDifficultyName,
                            _currentModeName,
                            _config.Scope,
                            targetAccuracy,
                            token).ConfigureAwait(false);
                    }
                    else
                    {
                        additionalEntries = await BinarySearchScoreSaberPageByAccuracyAsync(
                            _currentHash,
                            _currentDifficultyInt,
                            _currentModeName,
                            _config.Scope,
                            targetAccuracy,
                            token).ConfigureAwait(false);
                    }

                    if (additionalEntries != null && additionalEntries.Count > 0)
                    {
                        MergeAdditionalTextEntries(additionalEntries);
                    }
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug($"[LeaderboardFetcher] Gap text fetch skipped: {ex.Message}");
            }
            finally
            {
                _isFetchingGap = false;
            }
        }

        /// <summary>
        /// 曲終了時（クリアまたはフェイル時）に、最終精度の順位を中心として前後5人ずつ（計10人分）のプレイヤーを特定し、
        /// 未キャッシュのアイコン画像をバックグラウンドでダウンロードして AvatarCache に保存する。
        /// </summary>
        public async Task CacheAroundFinalAccuracyAsync(double finalAccuracy)
        {
            try
            {
                // 曲終了につき画像生成ロックを解除
                _avatarCacheService.SetGameplayLock(false);

                var entries = GetEntriesSnapshot();
                if (entries.Count == 0)
                {
                    return;
                }

                var dummy = new LeaderboardEntry { Accuracy = finalAccuracy };
                int index = entries.BinarySearch(dummy, LeaderboardEntry.DescendingAccuracyComparer.Instance);
                if (index < 0)
                {
                    index = ~index;
                }

                // 前後5人ずつ（計10人分）を特定
                int startIndex = Mathf.Max(0, index - 5);
                int endIndex = Mathf.Min(entries.Count - 1, index + 4);

                var targetTen = new List<LeaderboardEntry>(10);
                for (int i = startIndex; i <= endIndex; i++)
                {
                    targetTen.Add(entries[i]);
                }

                Plugin.Log?.Info($"[LeaderboardFetcher] Level ended. Caching avatars around index {index} ({targetTen.Count} players).");
                var token = _cts != null ? _cts.Token : CancellationToken.None;
                await _avatarCacheService.DownloadAndCacheAvatarsAsync(targetTen, token);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[LeaderboardFetcher] Failed to cache avatars on level end: {ex.Message}");
            }
        }

        #region User & Friends Initialization

        private async Task InitializeUserAndFriendsAsync(CancellationToken token)
        {
            if (_userDataInitialized) return;

            // 1. Steamworks から即座に（同期・0msで）SteamIDとフレンドリストを取得
            try
            {
                var selfSteamId = SteamUser.GetSteamID();
                if (selfSteamId.IsValid() && selfSteamId.m_SteamID != 0)
                {
                    _localPlayerId = selfSteamId.m_SteamID.ToString(CultureInfo.InvariantCulture);
                }

                int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < friendCount; i++)
                {
                    CSteamID friendSteamId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    if (friendSteamId.IsValid() && friendSteamId.m_SteamID != 0)
                    {
                        _friendIds.Add(friendSteamId.m_SteamID.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            catch
            {
                // Steam非初期化時は無視
            }

            // 2. IPlatformUserModel が利用可能な場合は 2秒のタイムアウト付きで補完取得（ハング防止）
            try
            {
                if (_platformUserModel != null)
                {
                    var userInfoTask = _platformUserModel.GetUserInfo(token);
                    var completed = await Task.WhenAny(userInfoTask, Task.Delay(2000, token));
                    if (completed == userInfoTask && !userInfoTask.IsFaulted && userInfoTask.Result != null)
                    {
                        if (string.IsNullOrEmpty(_localPlayerId))
                        {
                            _localPlayerId = userInfoTask.Result.platformUserId ?? string.Empty;
                        }
                        _localPlayerName = userInfoTask.Result.userName ?? string.Empty;
                    }

                    try
                    {
                        var friendTask = _platformUserModel.GetUserFriendsUserIds(cached: true);
                        var friendCompleted = await Task.WhenAny(friendTask, Task.Delay(1500, token));
                        if (friendCompleted == friendTask && !friendTask.IsFaulted && friendTask.Result != null)
                        {
                            foreach (var fid in friendTask.Result)
                            {
                                if (!string.IsNullOrEmpty(fid))
                                {
                                    _friendIds.Add(fid);
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug($"[LeaderboardFetcher] PlatformUserModel init warning: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(_localPlayerId))
            {
                _friendIds.Add(_localPlayerId);
            }

            // 3. デフォルト国コードを即座に設定し、APIからの国コード確認はバックグラウンドで行う
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
                _ = Task.Run(async () =>
                {
                    string apiCountry = await FetchPlayerCountryCodeAsync(_localPlayerId, token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(apiCountry))
                    {
                        _localCountryCode = apiCountry;
                    }
                }, token);
            }

            Plugin.Log?.Info($"[LeaderboardFetcher] User initialized: ID={_localPlayerId}, Country={_localCountryCode}, Friends={_friendIds.Count}");
        }

        private async Task<string> FetchPlayerCountryCodeAsync(string playerId, CancellationToken token)
        {
            // BeatLeader で国コード確認
            string blJson = await SendGetRequestAsync($"https://api.beatleader.xyz/player/{playerId}", token);
            if (!string.IsNullOrEmpty(blJson))
            {
                try
                {
                    var obj = JObject.Parse(blJson);
                    string country = (string)obj["country"];
                    if (!string.IsNullOrEmpty(country) && country != "not set")
                    {
                        return country.ToUpperInvariant();
                    }
                }
                catch
                {
                }
            }

            // ScoreSaber でフォールバック確認
            string ssJson = await SendGetRequestAsync($"https://scoresaber.com/api/player/{playerId}/basic", token);
            if (!string.IsNullOrEmpty(ssJson))
            {
                try
                {
                    var obj = JObject.Parse(ssJson);
                    string country = (string)obj["country"];
                    if (!string.IsNullOrEmpty(country))
                    {
                        return country.ToUpperInvariant();
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        #endregion

        #region BeatLeader API Fetching

        private static string GetBeatLeaderScopeSegment(RankingScope scope)
        {
            switch (scope)
            {
                case RankingScope.Country:
                    return "country";
                case RankingScope.Friends:
                    return "friends";
                case RankingScope.Global:
                default:
                    return "global";
            }
        }

        private async Task<List<LeaderboardEntry>> FetchBeatLeaderInitialAsync(
            string hash,
            string diffName,
            string modeName,
            RankingScope scope,
            CancellationToken token)
        {
            var result = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
            string scopeSegment = GetBeatLeaderScopeSegment(scope);
            string playerQuery = !string.IsNullOrEmpty(_localPlayerId)
                ? $"&player={Uri.EscapeDataString(_localPlayerId)}"
                : string.Empty;

            // 1. TOP 100 の取得 (/v3/scores/{hash}/{diff}/{mode}/general/{scope}/page?page=1&count=100)
            string top100Url = $"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/{scopeSegment}/page?page=1&count=100{playerQuery}";
            string json = await SendGetRequestAsync(top100Url, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(json))
            {
                ParseBeatLeaderScoresResponse(json, result, out int totalCount);
                _currentTotalScoresCount = totalCount;
            }

            // 2. 自己ベストが TOP 100 内に含まれていない場合、/around エンドポイントで自己ベスト前後50人分（計100人）を取得
            if (!string.IsNullOrEmpty(_localPlayerId) && !result.ContainsKey(_localPlayerId))
            {
                string aroundUrl = $"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/{scopeSegment}/around?player={Uri.EscapeDataString(_localPlayerId)}&count=100";
                string aroundJson = await SendGetRequestAsync(aroundUrl, token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(aroundJson))
                {
                    ParseBeatLeaderScoresResponse(aroundJson, result, out _);
                }
            }

            // Friends スコープで BeatLeader 上のフレンドリストが空または少ない場合、ローカルの Steam フレンドIDでも補完
            if (scope == RankingScope.Friends && result.Count < 3 && _friendIds.Count > 1)
            {
                return await BuildBeatLeaderFriendsLeaderboardAsync(hash, diffName, modeName, result.Values, token).ConfigureAwait(false);
            }

            return result.Values.ToList();
        }

        private async Task<List<LeaderboardEntry>> BuildBeatLeaderFriendsLeaderboardAsync(
            string hash,
            string diffName,
            string modeName,
            IEnumerable<LeaderboardEntry> initialEntries,
            CancellationToken token)
        {
            var friendEntries = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in initialEntries)
            {
                friendEntries[entry.PlayerId] = entry;
            }

            // Global TOP 100 内にいる Steam フレンドも照合
            string globalTopUrl = $"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/global/page?page=1&count=100";
            string globalJson = await SendGetRequestAsync(globalTopUrl, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(globalJson))
            {
                var globalDict = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
                ParseBeatLeaderScoresResponse(globalJson, globalDict, out _);
                foreach (var kvp in globalDict)
                {
                    if (_friendIds.Contains(kvp.Key))
                    {
                        friendEntries[kvp.Key] = kvp.Value;
                    }
                }
            }

            // 個別のフレンド周辺スコアも /around で照合（最大25人）
            var missingFriends = _friendIds.Where(id => !friendEntries.ContainsKey(id)).Take(25).ToList();
            const int batchSize = 5;
            for (int i = 0; i < missingFriends.Count; i += batchSize)
            {
                if (token.IsCancellationRequested) break;

                var batch = missingFriends.Skip(i).Take(batchSize);
                var tasks = batch.Select(async friendId =>
                {
                    string url = $"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/global/around?player={Uri.EscapeDataString(friendId)}&count=1";
                    string json = await SendGetRequestAsync(url, token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var tempDict = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
                        ParseBeatLeaderScoresResponse(json, tempDict, out _);
                        if (tempDict.TryGetValue(friendId, out var friendEntry))
                        {
                            lock (friendEntries)
                            {
                                friendEntries[friendId] = friendEntry;
                            }
                        }
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var list = friendEntries.Values.OrderByDescending(e => e.Accuracy).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Rank = i + 1;
            }
            return list;
        }

        private async Task<List<LeaderboardEntry>> BinarySearchBeatLeaderPageByAccuracyAsync(
            string hash,
            string diffName,
            string modeName,
            RankingScope scope,
            double targetAccuracy,
            CancellationToken token)
        {
            if (_currentTotalScoresCount <= 100) return null;

            string scopeSegment = GetBeatLeaderScopeSegment(scope);
            string playerQuery = !string.IsNullOrEmpty(_localPlayerId)
                ? $"&player={Uri.EscapeDataString(_localPlayerId)}"
                : string.Empty;

            int totalPages = Mathf.CeilToInt(_currentTotalScoresCount / 100f);
            int lowPage = 2;
            int highPage = Mathf.Min(totalPages, 50); // 最大5000位まで探索
            int bestPage = lowPage;

            int iterations = 0;
            while (lowPage <= highPage && iterations < 5)
            {
                iterations++;
                int midPage = (lowPage + highPage) / 2;
                string url = $"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/{scopeSegment}/page?page={midPage}&count=100{playerQuery}";
                string json = await SendGetRequestAsync(url, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json)) break;

                var pageDict = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
                ParseBeatLeaderScoresResponse(json, pageDict, out _);
                if (pageDict.Count == 0) break;

                var pageList = pageDict.Values.OrderByDescending(e => e.Accuracy).ToList();
                double highestAcc = pageList[0].Accuracy;
                double lowestAcc = pageList[pageList.Count - 1].Accuracy;

                if (targetAccuracy <= highestAcc && targetAccuracy >= lowestAcc)
                {
                    return pageList;
                }

                bestPage = midPage;
                if (targetAccuracy > highestAcc)
                {
                    highPage = midPage - 1;
                }
                else
                {
                    lowPage = midPage + 1;
                }
            }

            string finalUrl = $"https://api.beatleader.xyz/v3/scores/{hash}/{diffName}/{modeName}/general/{scopeSegment}/page?page={bestPage}&count=100{playerQuery}";
            string finalJson = await SendGetRequestAsync(finalUrl, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(finalJson))
            {
                var finalDict = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
                ParseBeatLeaderScoresResponse(finalJson, finalDict, out _);
                return finalDict.Values.ToList();
            }

            return null;
        }

        private static void ParseBeatLeaderScoresResponse(
            string json,
            Dictionary<string, LeaderboardEntry> targetDict,
            out int totalCount)
        {
            totalCount = 0;
            try
            {
                var root = JObject.Parse(json);
                totalCount = (int?)root["metadata"]?["total"] ?? 0;

                var dataArray = root["data"] as JArray;
                if (dataArray == null) return;

                foreach (var item in dataArray)
                {
                    var player = item["player"];
                    string playerId = (string)player?["id"] ?? (string)item["playerId"] ?? string.Empty;
                    if (string.IsNullOrEmpty(playerId)) continue;

                    double accuracy = (double?)item["accuracy"] ?? 0.0;
                    if (accuracy <= 0.0 || accuracy > 1.5) continue;

                    int rank = (int?)item["rank"] ?? 0;
                    string playerName = (string)player?["name"] ?? "Unknown";
                    string avatarUrl = (string)player?["avatar"] ?? string.Empty;

                    targetDict[playerId] = new LeaderboardEntry
                    {
                        PlayerId = playerId,
                        PlayerName = playerName,
                        Rank = rank,
                        Accuracy = accuracy,
                        AvatarUrl = avatarUrl
                    };
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug($"[LeaderboardFetcher] ParseBeatLeaderScoresResponse error: {ex.Message}");
            }
        }

        private static LeaderboardEntry TryParseSingleBeatLeaderScore(string json)
        {
            try
            {
                var item = JObject.Parse(json);
                var player = item["player"];
                string playerId = (string)player?["id"] ?? (string)item["playerId"] ?? string.Empty;
                if (string.IsNullOrEmpty(playerId)) return null;

                double accuracy = (double?)item["accuracy"] ?? 0.0;
                if (accuracy <= 0.0) return null;

                return new LeaderboardEntry
                {
                    PlayerId = playerId,
                    PlayerName = (string)player?["name"] ?? "Friend",
                    Rank = (int?)item["rank"] ?? 0,
                    Accuracy = accuracy,
                    AvatarUrl = (string)player?["avatar"] ?? string.Empty
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region ScoreSaber API Fetching

        private async Task<List<LeaderboardEntry>> FetchScoreSaberInitialAsync(
            string hash,
            int diffInt,
            string modeName,
            int fallbackNotesCount,
            RankingScope scope,
            CancellationToken token)
        {
            var result = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);

            // 1. Leaderboard Info から maxScore と total プレイ数を取得
            string infoUrl = $"https://scoresaber.com/api/leaderboard/by-hash/{hash}/info?difficulty={diffInt}&gameMode=Solo{modeName}";
            string infoJson = await SendGetRequestAsync(infoUrl, token);

            int maxScore = ComputeMaxScoreFromNotesCount(fallbackNotesCount);
            if (!string.IsNullOrEmpty(infoJson))
            {
                try
                {
                    var infoObj = JObject.Parse(infoJson);
                    int apiMaxScore = (int?)infoObj["maxScore"] ?? 0;
                    if (apiMaxScore > 0)
                    {
                        maxScore = apiMaxScore;
                    }
                    _currentTotalScoresCount = (int?)infoObj["plays"] ?? 0;
                }
                catch
                {
                }
            }

            _currentMaxScore = maxScore;

            string countryParam = (scope == RankingScope.Country && !string.IsNullOrEmpty(_localCountryCode))
                ? $"&countries={Uri.EscapeDataString(_localCountryCode)}"
                : string.Empty;

            // 2. ScoreSaber は 1ページ = 12件のため、TOP 100 (Page 1〜9: 108件分) を並列バッチで取得
            int pagesToFetch = scope == RankingScope.Friends ? 12 : 9;
            const int batchSize = 3;

            for (int startPage = 1; startPage <= pagesToFetch; startPage += batchSize)
            {
                if (token.IsCancellationRequested) break;

                int endPage = Mathf.Min(pagesToFetch, startPage + batchSize - 1);
                var pageTasks = new List<Task<string>>();
                for (int p = startPage; p <= endPage; p++)
                {
                    string pageUrl = $"https://scoresaber.com/api/leaderboard/by-hash/{hash}/scores?difficulty={diffInt}&gameMode=Solo{modeName}&page={p}{countryParam}";
                    pageTasks.Add(SendGetRequestAsync(pageUrl, token));
                }

                string[] responses = await Task.WhenAll(pageTasks);
                bool reachedEnd = false;
                foreach (string pageJson in responses)
                {
                    if (string.IsNullOrEmpty(pageJson))
                    {
                        reachedEnd = true;
                        continue;
                    }

                    int countInPage = ParseScoreSaberScoresResponse(pageJson, maxScore, result, out int totalFromMeta);
                    if (totalFromMeta > 0)
                    {
                        _currentTotalScoresCount = totalFromMeta;
                    }
                    if (countInPage < 12)
                    {
                        reachedEnd = true;
                    }
                }

                if (reachedEnd) break;
            }

            if (scope == RankingScope.Friends)
            {
                var friendList = result.Values
                    .Where(e => _friendIds.Contains(e.PlayerId))
                    .OrderByDescending(e => e.Accuracy)
                    .ToList();

                for (int i = 0; i < friendList.Count; i++)
                {
                    friendList[i].Rank = i + 1;
                }
                return friendList;
            }

            return result.Values.ToList();
        }

        private async Task<List<LeaderboardEntry>> BinarySearchScoreSaberPageByAccuracyAsync(
            string hash,
            int diffInt,
            string modeName,
            RankingScope scope,
            double targetAccuracy,
            CancellationToken token)
        {
            if (_currentTotalScoresCount <= 108 || _currentMaxScore <= 0) return null;

            string countryParam = (scope == RankingScope.Country && !string.IsNullOrEmpty(_localCountryCode))
                ? $"&countries={Uri.EscapeDataString(_localCountryCode)}"
                : string.Empty;

            int totalPages = Mathf.CeilToInt(_currentTotalScoresCount / 12f);
            int lowPage = 10;
            int highPage = Mathf.Min(totalPages, 250);
            int bestPage = lowPage;

            int iterations = 0;
            while (lowPage <= highPage && iterations < 6)
            {
                iterations++;
                int midPage = (lowPage + highPage) / 2;
                string url = $"https://scoresaber.com/api/leaderboard/by-hash/{hash}/scores?difficulty={diffInt}&gameMode=Solo{modeName}&page={midPage}{countryParam}";
                string json = await SendGetRequestAsync(url, token);
                if (string.IsNullOrEmpty(json)) break;

                var pageDict = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
                ParseScoreSaberScoresResponse(json, _currentMaxScore, pageDict, out _);
                if (pageDict.Count == 0) break;

                var pageList = pageDict.Values.OrderByDescending(e => e.Accuracy).ToList();
                double highestAcc = pageList[0].Accuracy;
                double lowestAcc = pageList[pageList.Count - 1].Accuracy;

                bestPage = midPage;
                if (targetAccuracy <= highestAcc && targetAccuracy >= lowestAcc)
                {
                    break;
                }

                if (targetAccuracy > highestAcc)
                {
                    highPage = midPage - 1;
                }
                else
                {
                    lowPage = midPage + 1;
                }
            }

            // bestPage を中心に前後4ページ（約100人分＝前後50人）のテキストデータを取得
            var aroundDict = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
            int startP = Mathf.Max(10, bestPage - 4);
            int endP = Mathf.Min(totalPages, bestPage + 4);

            for (int p = startP; p <= endP; p++)
            {
                if (token.IsCancellationRequested) break;
                string url = $"https://scoresaber.com/api/leaderboard/by-hash/{hash}/scores?difficulty={diffInt}&gameMode=Solo{modeName}&page={p}{countryParam}";
                string json = await SendGetRequestAsync(url, token);
                if (!string.IsNullOrEmpty(json))
                {
                    ParseScoreSaberScoresResponse(json, _currentMaxScore, aroundDict, out _);
                }
            }

            return aroundDict.Values.ToList();
        }

        private static int ParseScoreSaberScoresResponse(
            string json,
            int maxScore,
            Dictionary<string, LeaderboardEntry> targetDict,
            out int totalCount)
        {
            totalCount = 0;
            int parsedCount = 0;
            try
            {
                var root = JObject.Parse(json);
                totalCount = (int?)root["metadata"]?["total"] ?? 0;

                var scoresArray = root["scores"] as JArray;
                if (scoresArray == null) return 0;

                foreach (var item in scoresArray)
                {
                    var playerInfo = item["leaderboardPlayerInfo"];
                    string playerId = (string)playerInfo?["id"] ?? string.Empty;
                    if (string.IsNullOrEmpty(playerId)) continue;

                    int baseScore = (int?)item["baseScore"] ?? 0;
                    int rank = (int?)item["rank"] ?? 0;

                    // ゼロ除算対策を徹底した純粋精度の算出
                    double accuracy = (maxScore > 0 && baseScore > 0)
                        ? (double)baseScore / (double)maxScore
                        : 0.0;

                    if (accuracy <= 0.0 || accuracy > 1.5) continue;

                    string playerName = (string)playerInfo?["name"] ?? "Unknown";
                    string avatarUrl = (string)playerInfo?["profilePicture"] ?? string.Empty;

                    targetDict[playerId] = new LeaderboardEntry
                    {
                        PlayerId = playerId,
                        PlayerName = playerName,
                        Rank = rank,
                        Accuracy = accuracy,
                        AvatarUrl = avatarUrl
                    };
                    parsedCount++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug($"[LeaderboardFetcher] ParseScoreSaberScoresResponse error: {ex.Message}");
            }

            return parsedCount;
        }

        #endregion

        #region Helpers & Sorting

        private void FinalizeAndSortEntries(List<LeaderboardEntry> entries)
        {
            if (entries == null) return;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entry.IsSelf = !string.IsNullOrEmpty(_localPlayerId) &&
                               string.Equals(entry.PlayerId, _localPlayerId, StringComparison.OrdinalIgnoreCase);
            }

            // 既にキャッシュに存在するPlayerIdの画像を割り当て（通信・生成なし）
            _avatarCacheService.AssignExistingCachedAvatars(entries);

            // 降順ソートおよびSlot(0〜3)の事前割り当て
            LeaderboardEntry.SortAndAssignSlots(entries, _config);
        }

        private void MergeAdditionalTextEntries(List<LeaderboardEntry> additionalEntries)
        {
            if (additionalEntries == null || additionalEntries.Count == 0) return;

            List<LeaderboardEntry> merged;
            lock (_entriesLock)
            {
                var map = new Dictionary<string, LeaderboardEntry>(_currentEntries.Count + additionalEntries.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var existing in _currentEntries)
                {
                    map[existing.PlayerId] = existing;
                }

                foreach (var added in additionalEntries)
                {
                    if (!map.ContainsKey(added.PlayerId))
                    {
                        map[added.PlayerId] = added;
                    }
                }

                merged = map.Values.ToList();
            }

            FinalizeAndSortEntries(merged);
            UpdateEntriesInternal(merged);
        }

        private void UpdateEntriesInternal(List<LeaderboardEntry> newEntries)
        {
            lock (_entriesLock)
            {
                _currentEntries = newEntries ?? new List<LeaderboardEntry>();
            }

            LeaderboardUpdatedEvent?.Invoke(GetEntriesSnapshot());
        }

        public void RecalculateSlotsForCurrentEntries()
        {
            var snapshot = GetEntriesSnapshot();
            if (snapshot.Count == 0) return;

            LeaderboardEntry.SortAndAssignSlots(snapshot, _config);
            UpdateEntriesInternal(snapshot);
        }

        private static async Task<string> SendGetRequestAsync(string url, CancellationToken token)
        {
            try
            {
                using (var response = await SharedHttpClient.GetAsync(url, token).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string ExtractCustomLevelHash(string levelId)
        {
            if (string.IsNullOrEmpty(levelId)) return string.Empty;

            try
            {
                string hash = SongCore.Collections.GetCustomLevelHash(levelId);
                if (!string.IsNullOrEmpty(hash))
                {
                    return hash;
                }
            }
            catch
            {
            }

            const string prefix = "custom_level_";
            if (levelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string raw = levelId.Substring(prefix.Length);
                if (raw.Length >= 40)
                {
                    return raw.Substring(0, 40);
                }
                return raw;
            }

            return string.Empty;
        }

        private static int ConvertDifficultyToScoreSaberInt(BeatmapDifficulty difficulty)
        {
            switch (difficulty)
            {
                case BeatmapDifficulty.Easy: return 1;
                case BeatmapDifficulty.Normal: return 3;
                case BeatmapDifficulty.Hard: return 5;
                case BeatmapDifficulty.Expert: return 7;
                case BeatmapDifficulty.ExpertPlus: return 9;
                default: return 9;
            }
        }

        /// <summary>
        /// ScoreSaber の maxScore が API から取得できない場合の Beat Saber 標準最大スコア算出式
        /// </summary>
        public static int ComputeMaxScoreFromNotesCount(int notesCount)
        {
            if (notesCount <= 0) return 0;
            if (notesCount == 1) return 115;
            if (notesCount <= 5) return 115 + (notesCount - 1) * 230;
            if (notesCount <= 13) return 115 + 4 * 230 + (notesCount - 5) * 460;
            return 115 + 4 * 230 + 8 * 460 + (notesCount - 13) * 920;
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

        #endregion
    }
}
