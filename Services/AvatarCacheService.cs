using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;
using IPA.Utilities.Async;
using RealtimeRankMarathon.Models;
using UnityEngine;
using Zenject;

namespace RealtimeRankMarathon.Services
{
    using LeaderboardEntry = RealtimeRankMarathon.Models.LeaderboardEntry;

    public class AvatarCacheService : IInitializable, IDisposable
    {
        private const int AvatarTextureSize = 96;
        private static readonly HttpClient SharedAvatarHttpClient = CreateAvatarHttpClient();

        private static HttpClient CreateAvatarHttpClient()
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
        private readonly string _cacheDirectory;

        // メモリ上の PlayerId -> Sprite キャッシュ (ConcurrentDictionaryでスレッドセーフに参照可能)
        private readonly ConcurrentDictionary<string, Sprite> _avatarCache =
            new ConcurrentDictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        // 重複ダウンロード防止用セット
        private readonly ConcurrentDictionary<string, byte> _inFlightDownloads =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource _cts;
        private volatile bool _isGameplayLocked;

        /// <summary>
        /// プレイ中（ノーツ到来以降〜曲終了まで）は true となり、画像通信および Texture2D / Sprite 生成を完全禁止する
        /// </summary>
        public bool IsGameplayLocked => _isGameplayLocked;

        public int CachedCount => _avatarCache.Count;

        public AvatarCacheService()
        {
            _cacheDirectory = Path.Combine(UnityGame.UserDataPath, "RealtimeRankMarathon", "AvatarCache");
        }

        public void Initialize()
        {
            _cts = new CancellationTokenSource();

            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    Directory.CreateDirectory(_cacheDirectory);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[AvatarCacheService] Failed to create cache directory: {ex.Message}");
            }

            // 起動時にローカル永続キャッシュを非同期でメモリへプリロード
            _ = PreloadDiskCacheAsync(_cts.Token);
        }

        /// <summary>
        /// プレイ中の画像通信・テクスチャ生成ロック状態を切り替える
        /// </summary>
        public void SetGameplayLock(bool locked)
        {
            _isGameplayLocked = locked;
        }

        /// <summary>
        /// メモリキャッシュに PlayerId に対応する Sprite が存在すれば取得する（アロケーションゼロ）
        /// </summary>
        public bool TryGetAvatar(string playerId, out Sprite sprite)
        {
            sprite = null;
            if (string.IsNullOrEmpty(playerId))
            {
                return false;
            }

            string key = SanitizePlayerId(playerId);
            return _avatarCache.TryGetValue(key, out sprite) && !ReferenceEquals(sprite, null);
        }

        /// <summary>
        /// リスト内の全エントリに対して、既にメモリキャッシュ済みのアイコンがあれば割り当てる（通信・生成は行わない）
        /// </summary>
        public void AssignExistingCachedAvatars(IList<LeaderboardEntry> entries)
        {
            if (entries == null) return;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && ReferenceEquals(entry.CachedAvatar, null) && !string.IsNullOrEmpty(entry.PlayerId))
                {
                    if (TryGetAvatar(entry.PlayerId, out Sprite cachedSprite))
                    {
                        entry.CachedAvatar = cachedSprite;
                    }
                }
            }
        }

        /// <summary>
        /// 指定されたエントリ群のうち未キャッシュのアイコンをバックグラウンドで取得し、メモリとディスク(AvatarCache)に保存する。
        /// ※ プレイ中 (_isGameplayLocked == true) は一切実行しない。
        /// </summary>
        public async Task DownloadAndCacheAvatarsAsync(IEnumerable<LeaderboardEntry> targetEntries, CancellationToken cancellationToken = default)
        {
            if (_isGameplayLocked || targetEntries == null)
            {
                return;
            }

            var entriesToProcess = new List<LeaderboardEntry>();
            foreach (var entry in targetEntries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.PlayerId))
                {
                    continue;
                }

                if (TryGetAvatar(entry.PlayerId, out Sprite existing))
                {
                    entry.CachedAvatar = existing;
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.AvatarUrl))
                {
                    entriesToProcess.Add(entry);
                }
            }

            const int batchSize = 4;
            for (int i = 0; i < entriesToProcess.Count; i += batchSize)
            {
                if (_isGameplayLocked || cancellationToken.IsCancellationRequested || (_cts != null && _cts.IsCancellationRequested))
                {
                    break;
                }

                var batch = entriesToProcess.Skip(i).Take(batchSize).ToList();
                var batchTasks = batch.Select(async entry =>
                {
                    if (_isGameplayLocked || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    string key = SanitizePlayerId(entry.PlayerId);
                    if (TryGetAvatar(key, out Sprite existing))
                    {
                        entry.CachedAvatar = existing;
                        return;
                    }

                    if (!_inFlightDownloads.TryAdd(key, 1))
                    {
                        return;
                    }

                    try
                    {
                        Sprite createdSprite = await FetchAndSaveSingleAvatarAsync(key, entry.AvatarUrl, cancellationToken);
                        if (!ReferenceEquals(createdSprite, null))
                        {
                            _avatarCache[key] = createdSprite;
                            entry.CachedAvatar = createdSprite;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.Debug($"[AvatarCacheService] Avatar download skipped for {key}: {ex.Message}");
                    }
                    finally
                    {
                        _inFlightDownloads.TryRemove(key, out _);
                    }
                });

                await Task.WhenAll(batchTasks);
            }
        }

        private async Task PreloadDiskCacheAsync(CancellationToken token)
        {
            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    return;
                }

                string[] files = await Task.Run(() => Directory.GetFiles(_cacheDirectory, "*.png"), token);
                if (files == null || files.Length == 0)
                {
                    return;
                }

                foreach (string filePath in files)
                {
                    if (token.IsCancellationRequested) return;

                    // プレイ中はプリロードによるテクスチャ生成を一時停止して待機
                    while (_isGameplayLocked && !token.IsCancellationRequested)
                    {
                        await Task.Delay(500, token);
                    }

                    string playerId = Path.GetFileNameWithoutExtension(filePath);
                    if (string.IsNullOrEmpty(playerId) || _avatarCache.ContainsKey(playerId))
                    {
                        continue;
                    }

                    byte[] bytes = await Task.Run(() =>
                    {
                        try
                        {
                            return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
                        }
                        catch
                        {
                            return null;
                        }
                    }, token);

                    if (bytes == null || bytes.Length == 0 || _isGameplayLocked || token.IsCancellationRequested)
                    {
                        continue;
                    }

                    // Unityメインスレッド上で確実に Texture2D / Sprite を生成し DontDestroyOnLoad を付与
                    Sprite sprite = await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                    {
                        if (_isGameplayLocked || token.IsCancellationRequested) return null;
                        return CreateSpriteFromBytes(bytes, playerId, applyCircularMask: false);
                    }, token);

                    if (!ReferenceEquals(sprite, null))
                    {
                        _avatarCache[playerId] = sprite;
                    }
                }

                Plugin.Log?.Info($"[AvatarCacheService] Preloaded {_avatarCache.Count} avatars from disk cache.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[AvatarCacheService] Error during disk cache preload: {ex.Message}");
            }
        }

        private async Task<Sprite> FetchAndSaveSingleAvatarAsync(string sanitizedPlayerId, string url, CancellationToken token)
        {
            if (_isGameplayLocked || string.IsNullOrEmpty(url))
            {
                return null;
            }

            string diskPath = Path.Combine(_cacheDirectory, $"{sanitizedPlayerId}.png");

            // もしディスク上に既にファイルが存在し、まだメモリに載っていなかった場合はディスクから読み込む
            if (File.Exists(diskPath))
            {
                byte[] existingBytes = await Task.Run(() =>
                {
                    try { return File.ReadAllBytes(diskPath); }
                    catch { return null; }
                }, token);

                if (existingBytes != null && existingBytes.Length > 0 && !_isGameplayLocked)
                {
                    return await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                    {
                        if (_isGameplayLocked || token.IsCancellationRequested) return null;
                        return CreateSpriteFromBytes(existingBytes, sanitizedPlayerId, applyCircularMask: false);
                    }, token);
                }
            }

            if (_isGameplayLocked) return null;

            // UnityWebRequest は Unity メインスレッド上で作成・送信し、ポーリング待機する
            byte[] downloadedBytes = await DownloadBytesOnMainThreadAsync(url, token);
            if (downloadedBytes == null || downloadedBytes.Length == 0 || _isGameplayLocked || token.IsCancellationRequested)
            {
                return null;
            }

            byte[] encodedPngForDisk = null;
            Sprite createdSprite = await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
            {
                if (_isGameplayLocked || token.IsCancellationRequested) return null;

                Texture2D maskedTexture = CreateCircularMaskedTexture(downloadedBytes, sanitizedPlayerId, out encodedPngForDisk);
                if (maskedTexture == null) return null;

                UnityEngine.Object.DontDestroyOnLoad(maskedTexture);

                Sprite sprite = Sprite.Create(
                    maskedTexture,
                    new Rect(0f, 0f, maskedTexture.width, maskedTexture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                sprite.name = $"Avatar_{sanitizedPlayerId}";
                UnityEngine.Object.DontDestroyOnLoad(sprite);
                return sprite;
            }, token);

            byte[] bytesToWrite = encodedPngForDisk ?? downloadedBytes;
            if (bytesToWrite != null && bytesToWrite.Length > 0)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        File.WriteAllBytes(diskPath, bytesToWrite);
                    }
                    catch
                    {
                    }
                });
            }

            return createdSprite;
        }

        private async Task<byte[]> DownloadBytesOnMainThreadAsync(string url, CancellationToken token)
        {
            try
            {
                using (var response = await SharedAvatarHttpClient.GetAsync(url, token).ConfigureAwait(false))
                {
                    if (_isGameplayLocked || token.IsCancellationRequested || !response.IsSuccessStatusCode)
                    {
                        return null;
                    }
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                return null;
            }
        }

        private Sprite CreateSpriteFromBytes(byte[] bytes, string sanitizedPlayerId, bool applyCircularMask)
        {
            if (_isGameplayLocked || bytes == null || bytes.Length == 0)
            {
                return null;
            }

            try
            {
                Texture2D tex;
                if (applyCircularMask)
                {
                    tex = CreateCircularMaskedTexture(bytes, sanitizedPlayerId, out _);
                }
                else
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.filterMode = FilterMode.Bilinear;
                    if (!ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                    {
                        UnityEngine.Object.Destroy(tex);
                        return null;
                    }
                    tex.name = $"AvatarTex_{sanitizedPlayerId}";
                }

                if (tex == null) return null;

                UnityEngine.Object.DontDestroyOnLoad(tex);

                Sprite sprite = Sprite.Create(
                    tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                sprite.name = $"Avatar_{sanitizedPlayerId}";
                UnityEngine.Object.DontDestroyOnLoad(sprite);
                return sprite;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ダウンロードした画像バイト列からアンチエイリアス付きの丸型（円形マスク）Texture2D を生成する。
        /// 事前キャッシュ時点で丸型透過PNGにしておくことで、プレイ中のシェーダー負荷をゼロにする。
        /// </summary>
        private static Texture2D CreateCircularMaskedTexture(byte[] rawBytes, string sanitizedPlayerId, out byte[] outPngBytes)
        {
            outPngBytes = null;
            Texture2D srcTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!ImageConversion.LoadImage(srcTex, rawBytes, markNonReadable: false))
                {
                    UnityEngine.Object.Destroy(srcTex);
                    return null;
                }

                int size = AvatarTextureSize;
                Texture2D dstTex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                dstTex.name = $"AvatarTex_{sanitizedPlayerId}";
                dstTex.wrapMode = TextureWrapMode.Clamp;
                dstTex.filterMode = FilterMode.Bilinear;

                Color[] pixels = new Color[size * size];
                float center = (size - 1) * 0.5f;
                float radius = size * 0.5f - 1.0f;

                for (int y = 0; y < size; y++)
                {
                    float dy = y - center;
                    float v = (y + 0.5f) / size;

                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - center;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);

                        if (dist > radius + 1.0f)
                        {
                            pixels[y * size + x] = Color.clear;
                        }
                        else
                        {
                            float u = (x + 0.5f) / size;
                            Color c = srcTex.GetPixelBilinear(u, v);

                            // エッジのアンチエイリアスフェード
                            float edgeAlpha = Mathf.Clamp01(radius + 0.5f - dist);
                            c.a *= edgeAlpha;
                            pixels[y * size + x] = c;
                        }
                    }
                }

                dstTex.SetPixels(pixels);
                dstTex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                try
                {
                    outPngBytes = ImageConversion.EncodeToPNG(dstTex);
                }
                catch
                {
                    outPngBytes = rawBytes;
                }

                // GPUへ転送しCPU側システムメモリを解放
                dstTex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                return dstTex;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (srcTex != null)
                {
                    UnityEngine.Object.Destroy(srcTex);
                }
            }
        }

        public static string SanitizePlayerId(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            if (playerId.IndexOfAny(invalid) < 0)
            {
                return playerId.Trim();
            }

            var chars = playerId.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }
            return new string(chars).Trim();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            foreach (var kvp in _avatarCache)
            {
                if (kvp.Value != null)
                {
                    if (kvp.Value.texture != null)
                    {
                        UnityEngine.Object.Destroy(kvp.Value.texture);
                    }
                    UnityEngine.Object.Destroy(kvp.Value);
                }
            }
            _avatarCache.Clear();
        }
    }
}
