using System;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using RealtimeRankMarathon.Managers;
using RealtimeRankMarathon.Services;
using RealtimeRankMarathon.UI;
using SiraUtil.Zenject;
using Zenject;
using IPALogger = IPA.Logging.Logger;

namespace RealtimeRankMarathon
{
    [Plugin(RuntimeOptions.DynamicInit), NoEnableDisable]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; }

        [Init]
        public Plugin(IPALogger logger, Config conf, Zenjector zenjector)
        {
            Log = logger;
            PluginConfig.Instance = conf.Generated<PluginConfig>();

            zenjector.UseLogger(logger);
            zenjector.Install<MarathonAppInstaller>(Location.App);
            zenjector.Install<MarathonMenuInstaller>(Location.Menu);
            zenjector.Install<MarathonPlayerInstaller>(Location.Player);
        }
    }

    internal class MarathonAppInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInstance(PluginConfig.Instance).AsSingle();
            Container.BindInterfacesAndSelfTo<AvatarCacheService>().AsSingle();
            Container.BindInterfacesAndSelfTo<LeaderboardFetcher>().AsSingle();
        }
    }

    internal class MarathonMenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInterfacesAndSelfTo<SettingsViewController>().AsSingle();
            Container.BindInterfacesAndSelfTo<MenuPreloadController>().AsSingle();
        }
    }

    internal class MarathonPlayerInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInterfacesAndSelfTo<RankPlatePool>().AsSingle();
            Container.BindInterfacesAndSelfTo<MarathonManager>().AsSingle();
        }
    }

    /// <summary>
    /// メニュー画面で譜面・難易度が選択された際に先行してTOP100テキスト＋TOP10アイコンをプリロードするコントローラー
    /// </summary>
    internal class MenuPreloadController : IInitializable, IDisposable
    {
        private readonly StandardLevelDetailViewController _levelDetailViewController;
        private readonly LeaderboardFetcher _leaderboardFetcher;
        private readonly AvatarCacheService _avatarCacheService;
        private readonly PluginConfig _config;

        public MenuPreloadController(
            [InjectOptional] StandardLevelDetailViewController levelDetailViewController,
            LeaderboardFetcher leaderboardFetcher,
            AvatarCacheService avatarCacheService,
            PluginConfig config)
        {
            _levelDetailViewController = levelDetailViewController;
            _leaderboardFetcher = leaderboardFetcher;
            _avatarCacheService = avatarCacheService;
            _config = config;
        }

        public void Initialize()
        {
            // メニュー画面では常に画像ダウンロード・生成を許可
            _avatarCacheService.SetGameplayLock(false);

            _config.ConfigChangedEvent += OnConfigChanged;

            if (_levelDetailViewController != null)
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
                _avatarCacheService.SetGameplayLock(false);
                TriggerPreloadIfReady();
            }
        }

        private void TriggerPreloadIfReady()
        {
            if (!_config.Enabled || _levelDetailViewController == null || !_levelDetailViewController.isActiveAndEnabled)
            {
                return;
            }

            var level = _levelDetailViewController.beatmapLevel;
            var key = _levelDetailViewController.beatmapKey;
            if (level == null || !key.IsValid())
            {
                return;
            }

            _ = _leaderboardFetcher.PreloadForMapAsync(level, key, allowTop10AvatarDownload: true);
        }

        public void Dispose()
        {
            _config.ConfigChangedEvent -= OnConfigChanged;

            if (_levelDetailViewController != null)
            {
                _levelDetailViewController.didChangeDifficultyBeatmapEvent -= OnDifficultyBeatmapChanged;
                _levelDetailViewController.didChangeContentEvent -= OnContentChanged;
            }
        }
    }
}
