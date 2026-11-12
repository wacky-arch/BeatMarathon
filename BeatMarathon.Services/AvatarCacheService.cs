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
using BeatMarathon.Models;
using UnityEngine;
using Zenject;

namespace BeatMarathon.Services;

public class AvatarCacheService : IInitializable, IDisposable
{
	private const int AvatarTextureSize = 96;

	private static readonly HttpClient SharedAvatarHttpClient = CreateAvatarHttpClient();

	private readonly string _cacheDirectory;

	private readonly ConcurrentDictionary<string, Sprite> _avatarCache = new ConcurrentDictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, byte> _inFlightDownloads = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private CancellationTokenSource _cts;

	private volatile bool _isGameplayLocked;

	public bool IsGameplayLocked => _isGameplayLocked;

	public int CachedCount => _avatarCache.Count;

	private static HttpClient CreateAvatarHttpClient()
	{
		HttpClient httpClient = new HttpClient(new HttpClientHandler
		{
			AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate)
		});
		httpClient.Timeout = TimeSpan.FromSeconds(8.0);
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BeatMarathon/1.0.0 (BeatSaber/1.40.8)");
		return httpClient;
	}

	public AvatarCacheService()
	{
		_cacheDirectory = Path.Combine(UnityGame.UserDataPath, "BeatMarathon", "AvatarCache");
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
			Plugin.Log?.Warn("[AvatarCacheService] Failed to create cache directory: " + ex.Message);
		}
		PreloadDiskCacheAsync(_cts.Token);
	}

	public void SetGameplayLock(bool locked)
	{
		_isGameplayLocked = locked;
	}

	public bool TryGetAvatar(string playerId, out Sprite sprite)
	{
		sprite = null;
		if (string.IsNullOrEmpty(playerId))
		{
			return false;
		}
		string key = SanitizePlayerId(playerId);
		if (_avatarCache.TryGetValue(key, out sprite))
		{
			return (object)sprite != null;
		}
		return false;
	}

	public void AssignExistingCachedAvatars(IList<BeatMarathon.Models.LeaderboardEntry> entries)
	{
		if (entries == null)
		{
			return;
		}
		for (int i = 0; i < entries.Count; i++)
		{
			BeatMarathon.Models.LeaderboardEntry leaderboardEntry = entries[i];
			if (leaderboardEntry != null && (object)leaderboardEntry.CachedAvatar == null && !string.IsNullOrEmpty(leaderboardEntry.PlayerId) && TryGetAvatar(leaderboardEntry.PlayerId, out var sprite))
			{
				leaderboardEntry.CachedAvatar = sprite;
			}
		}
	}

	public async Task DownloadAndCacheAvatarsAsync(IEnumerable<BeatMarathon.Models.LeaderboardEntry> targetEntries, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_isGameplayLocked || targetEntries == null)
		{
			return;
		}
		List<BeatMarathon.Models.LeaderboardEntry> entriesToProcess = new List<BeatMarathon.Models.LeaderboardEntry>();
		foreach (BeatMarathon.Models.LeaderboardEntry targetEntry in targetEntries)
		{
			if (targetEntry != null && !string.IsNullOrEmpty(targetEntry.PlayerId))
			{
				if (TryGetAvatar(targetEntry.PlayerId, out var sprite))
				{
					targetEntry.CachedAvatar = sprite;
				}
				else if (!string.IsNullOrEmpty(targetEntry.AvatarUrl))
				{
					entriesToProcess.Add(targetEntry);
				}
			}
		}
		for (int i = 0; i < entriesToProcess.Count; i += 4)
		{
			if (_isGameplayLocked)
			{
				break;
			}
			if (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			if (_cts != null && _cts.IsCancellationRequested)
			{
				break;
			}
			await Task.WhenAll(((IEnumerable<BeatMarathon.Models.LeaderboardEntry>)entriesToProcess.Skip(i).Take(4).ToList()).Select((Func<BeatMarathon.Models.LeaderboardEntry, Task>)async delegate(BeatMarathon.Models.LeaderboardEntry entry)
			{
				if (!_isGameplayLocked && !cancellationToken.IsCancellationRequested)
				{
					string key = SanitizePlayerId(entry.PlayerId);
					if (TryGetAvatar(key, out var sprite2))
					{
						entry.CachedAvatar = sprite2;
					}
					else if (_inFlightDownloads.TryAdd(key, 1))
					{
						try
						{
							Sprite sprite3 = await FetchAndSaveSingleAvatarAsync(key, entry.AvatarUrl, cancellationToken);
							if ((object)sprite3 != null)
							{
								_avatarCache[key] = sprite3;
								entry.CachedAvatar = sprite3;
							}
						}
						catch (Exception ex)
						{
							Plugin.Log?.Debug("[AvatarCacheService] Avatar download skipped for " + key + ": " + ex.Message);
						}
						finally
						{
							_inFlightDownloads.TryRemove(key, out var _);
						}
					}
				}
			}));
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
			string[] array = await Task.Run(() => Directory.GetFiles(_cacheDirectory, "*.png"), token);
			if (array == null || array.Length == 0)
			{
				return;
			}
			string[] array2 = array;
			foreach (string filePath in array2)
			{
				if (token.IsCancellationRequested)
				{
					return;
				}
				while (_isGameplayLocked && !token.IsCancellationRequested)
				{
					await Task.Delay(500, token);
				}
				string playerId = Path.GetFileNameWithoutExtension(filePath);
				if (string.IsNullOrEmpty(playerId) || _avatarCache.ContainsKey(playerId))
				{
					continue;
				}
				byte[] bytes = await Task.Run(delegate
				{
					try
					{
						return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
					}
					catch
					{
						return (byte[])null;
					}
				}, token);
				if (bytes != null && bytes.Length != 0 && !_isGameplayLocked && !token.IsCancellationRequested)
				{
					Sprite sprite = await UnityMainThreadTaskScheduler.Factory.StartNew(() => (_isGameplayLocked || token.IsCancellationRequested) ? null : CreateSpriteFromBytes(bytes, playerId, applyCircularMask: false), token);
					if ((object)sprite != null)
					{
						_avatarCache[playerId] = sprite;
					}
				}
			}
			Plugin.Log?.Info($"[AvatarCacheService] Preloaded {_avatarCache.Count} avatars from disk cache.");
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			Plugin.Log?.Warn("[AvatarCacheService] Error during disk cache preload: " + ex2.Message);
		}
	}

	private async Task<Sprite> FetchAndSaveSingleAvatarAsync(string sanitizedPlayerId, string url, CancellationToken token)
	{
		if (_isGameplayLocked || string.IsNullOrEmpty(url))
		{
			return null;
		}
		string diskPath = Path.Combine(_cacheDirectory, sanitizedPlayerId + ".png");
		if (File.Exists(diskPath))
		{
			byte[] existingBytes = await Task.Run(delegate
			{
				try
				{
					return File.ReadAllBytes(diskPath);
				}
				catch
				{
					return (byte[])null;
				}
			}, token);
			if (existingBytes != null && existingBytes.Length != 0 && !_isGameplayLocked)
			{
				return await UnityMainThreadTaskScheduler.Factory.StartNew(() => (_isGameplayLocked || token.IsCancellationRequested) ? null : CreateSpriteFromBytes(existingBytes, sanitizedPlayerId, applyCircularMask: false), token);
			}
		}
		if (_isGameplayLocked)
		{
			return null;
		}
		byte[] downloadedBytes = await DownloadBytesOnMainThreadAsync(url, token);
		if (downloadedBytes == null || downloadedBytes.Length == 0 || _isGameplayLocked || token.IsCancellationRequested)
		{
			return null;
		}
		byte[] encodedPngForDisk = null;
		Sprite result = await UnityMainThreadTaskScheduler.Factory.StartNew(delegate
		{
			if (_isGameplayLocked || token.IsCancellationRequested)
			{
				return (Sprite)null;
			}
			Texture2D texture2D = CreateCircularMaskedTexture(downloadedBytes, sanitizedPlayerId, out encodedPngForDisk);
			if (!(texture2D == null))
			{
				UnityEngine.Object.DontDestroyOnLoad(texture2D);
				Sprite sprite = Sprite.Create(texture2D, new Rect(0f, 0f, texture2D.width, texture2D.height), new Vector2(0.5f, 0.5f), 100f);
				sprite.name = "Avatar_" + sanitizedPlayerId;
				UnityEngine.Object.DontDestroyOnLoad(sprite);
				return sprite;
			}
			return (Sprite)null;
		}, token);
		byte[] bytesToWrite = encodedPngForDisk ?? downloadedBytes;
		if (bytesToWrite != null && bytesToWrite.Length != 0)
		{
			Task.Run(delegate
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
		return result;
	}

	private async Task<byte[]> DownloadBytesOnMainThreadAsync(string url, CancellationToken token)
	{
		_ = 1;
		try
		{
			using HttpResponseMessage response = await SharedAvatarHttpClient.GetAsync(url, token).ConfigureAwait(continueOnCapturedContext: false);
			if (_isGameplayLocked || token.IsCancellationRequested || !response.IsSuccessStatusCode)
			{
				return null;
			}
			return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(continueOnCapturedContext: false);
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
			Texture2D texture2D;
			if (applyCircularMask)
			{
				texture2D = CreateCircularMaskedTexture(bytes, sanitizedPlayerId, out var _);
			}
			else
			{
				texture2D = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: false);
				texture2D.wrapMode = TextureWrapMode.Clamp;
				texture2D.filterMode = FilterMode.Bilinear;
				if (!texture2D.LoadImage(bytes, markNonReadable: true))
				{
					UnityEngine.Object.Destroy(texture2D);
					return null;
				}
				texture2D.name = "AvatarTex_" + sanitizedPlayerId;
			}
			if (texture2D == null)
			{
				return null;
			}
			UnityEngine.Object.DontDestroyOnLoad(texture2D);
			Sprite sprite = Sprite.Create(texture2D, new Rect(0f, 0f, texture2D.width, texture2D.height), new Vector2(0.5f, 0.5f), 100f);
			sprite.name = "Avatar_" + sanitizedPlayerId;
			UnityEngine.Object.DontDestroyOnLoad(sprite);
			return sprite;
		}
		catch
		{
			return null;
		}
	}

	private static Texture2D CreateCircularMaskedTexture(byte[] rawBytes, string sanitizedPlayerId, out byte[] outPngBytes)
	{
		outPngBytes = null;
		Texture2D texture2D = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: false);
		try
		{
			if (!texture2D.LoadImage(rawBytes, markNonReadable: false))
			{
				UnityEngine.Object.Destroy(texture2D);
				return null;
			}
			int num = 96;
			Texture2D texture2D2 = new Texture2D(num, num, TextureFormat.RGBA32, mipChain: false, linear: false);
			texture2D2.name = "AvatarTex_" + sanitizedPlayerId;
			texture2D2.wrapMode = TextureWrapMode.Clamp;
			texture2D2.filterMode = FilterMode.Bilinear;
			Color[] array = new Color[num * num];
			float num2 = (float)(num - 1) * 0.5f;
			float num3 = (float)num * 0.5f - 1f;
			for (int i = 0; i < num; i++)
			{
				float num4 = (float)i - num2;
				float v = ((float)i + 0.5f) / (float)num;
				for (int j = 0; j < num; j++)
				{
					float num5 = (float)j - num2;
					float num6 = Mathf.Sqrt(num5 * num5 + num4 * num4);
					if (num6 > num3 + 1f)
					{
						array[i * num + j] = Color.clear;
						continue;
					}
					float u = ((float)j + 0.5f) / (float)num;
					Color pixelBilinear = texture2D.GetPixelBilinear(u, v);
					float num7 = Mathf.Clamp01(num3 + 0.5f - num6);
					pixelBilinear.a *= num7;
					array[i * num + j] = pixelBilinear;
				}
			}
			texture2D2.SetPixels(array);
			texture2D2.Apply(updateMipmaps: false, makeNoLongerReadable: false);
			try
			{
				outPngBytes = texture2D2.EncodeToPNG();
			}
			catch
			{
				outPngBytes = rawBytes;
			}
			texture2D2.Apply(updateMipmaps: false, makeNoLongerReadable: true);
			return texture2D2;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (texture2D != null)
			{
				UnityEngine.Object.Destroy(texture2D);
			}
		}
	}

	public static string SanitizePlayerId(string playerId)
	{
		if (string.IsNullOrEmpty(playerId))
		{
			return "unknown";
		}
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		if (playerId.IndexOfAny(invalidFileNameChars) < 0)
		{
			return playerId.Trim();
		}
		char[] array = playerId.ToCharArray();
		for (int i = 0; i < array.Length; i++)
		{
			if (Array.IndexOf(invalidFileNameChars, array[i]) >= 0)
			{
				array[i] = '_';
			}
		}
		return new string(array).Trim();
	}

	public void Dispose()
	{
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;
		foreach (KeyValuePair<string, Sprite> item in _avatarCache)
		{
			if (item.Value != null)
			{
				if (item.Value.texture != null)
				{
					UnityEngine.Object.Destroy(item.Value.texture);
				}
				UnityEngine.Object.Destroy(item.Value);
			}
		}
		_avatarCache.Clear();
	}
}
