using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.GameplaySetup;
using BeatSaberMarkupLanguage.Settings;
using Zenject;

namespace RealtimeRankMarathon.UI
{
    public class SettingsViewController : IInitializable, IDisposable, INotifyPropertyChanged
    {
        private const string ResourceName = "RealtimeRankMarathon.UI.Views.SettingsView.bsml";
        private const string MenuTitle = "Rank Marathon";

        private readonly PluginConfig _config;

        public event PropertyChangedEventHandler PropertyChanged;

        [UIValue("data-source-options")]
        public List<object> DataSourceOptions { get; } = new List<object>
        {
            "BeatLeader",
            "ScoreSaber"
        };

        [UIValue("ranking-scope-options")]
        public List<object> RankingScopeOptions { get; } = new List<object>
        {
            "Global",
            "Friends",
            "Country"
        };

        [UIValue("display-mode-options")]
        public List<object> DisplayModeOptions { get; } = new List<object>
        {
            "Road Sign (Ground)",
            "Vertical Bar"
        };

        public SettingsViewController(PluginConfig config)
        {
            _config = config;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Initialize()
        {
            try
            {
                BSMLSettings.Instance.AddSettingsMenu(MenuTitle, ResourceName, this);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"Failed to register BSMLSettings menu: {ex}");
            }

            try
            {
                GameplaySetup.Instance.AddTab(MenuTitle, ResourceName, this, MenuType.All);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"Failed to register GameplaySetup tab: {ex}");
            }
        }

        public void Dispose()
        {
            if (BSMLSettings.Instance != null)
            {
                BSMLSettings.Instance.RemoveSettingsMenu(this);
            }

            if (GameplaySetup.Instance != null)
            {
                GameplaySetup.Instance.RemoveTab(MenuTitle);
            }
        }

        [UIValue("enabled")]
        public bool Enabled
        {
            get => _config.Enabled;
            set
            {
                if (_config.Enabled != value)
                {
                    _config.Enabled = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("data-source")]
        public string DataSource
        {
            get => _config.DataSource == LeaderboardDataSource.ScoreSaber ? "ScoreSaber" : "BeatLeader";
            set
            {
                var parsed = string.Equals(value, "ScoreSaber", StringComparison.OrdinalIgnoreCase)
                    ? LeaderboardDataSource.ScoreSaber
                    : LeaderboardDataSource.BeatLeader;

                if (_config.DataSource != parsed)
                {
                    _config.DataSource = parsed;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("ranking-scope")]
        public string Scope
        {
            get => _config.Scope.ToString();
            set
            {
                if (Enum.TryParse<RankingScope>(value, true, out var parsed) && _config.Scope != parsed)
                {
                    _config.Scope = parsed;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("display-mode")]
        public string DisplayMode
        {
            get => _config.DisplayMode == MarathonDisplayMode.VerticalBar
                ? "Vertical Bar"
                : "Road Sign (Ground)";
            set
            {
                var parsed = string.Equals(value, "Vertical Bar", StringComparison.OrdinalIgnoreCase)
                    ? MarathonDisplayMode.VerticalBar
                    : MarathonDisplayMode.RoadSignMarathon;

                if (_config.DisplayMode != parsed)
                {
                    _config.DisplayMode = parsed;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("show-rank")]
        public bool ShowRank
        {
            get => _config.ShowRank;
            set
            {
                if (_config.ShowRank != value)
                {
                    _config.ShowRank = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("show-avatar")]
        public bool ShowAvatar
        {
            get => _config.ShowAvatar;
            set
            {
                if (_config.ShowAvatar != value)
                {
                    _config.ShowAvatar = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("show-player-name")]
        public bool ShowPlayerName
        {
            get => _config.ShowPlayerName;
            set
            {
                if (_config.ShowPlayerName != value)
                {
                    _config.ShowPlayerName = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("show-accuracy")]
        public bool ShowAccuracy
        {
            get => _config.ShowAccuracy;
            set
            {
                if (_config.ShowAccuracy != value)
                {
                    _config.ShowAccuracy = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("show-delta")]
        public bool ShowDelta
        {
            get => _config.ShowDelta;
            set
            {
                if (_config.ShowDelta != value)
                {
                    _config.ShowDelta = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("ground-z-base")]
        public float GroundZBase
        {
            get => _config.GroundZBase;
            set
            {
                if (Math.Abs(_config.GroundZBase - value) > 0.001f)
                {
                    _config.GroundZBase = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("outer-lane-x")]
        public float OuterLaneXOffset
        {
            get => _config.OuterLaneXOffset;
            set
            {
                if (Math.Abs(_config.OuterLaneXOffset - value) > 0.001f)
                {
                    _config.OuterLaneXOffset = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("inner-lane-tilt")]
        public float InnerLaneTiltAngle
        {
            get => _config.InnerLaneTiltAngle;
            set
            {
                if (Math.Abs(_config.InnerLaneTiltAngle - value) > 0.01f)
                {
                    _config.InnerLaneTiltAngle = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("distance-scale-ground")]
        public float DistanceScaleGround
        {
            get => _config.DistanceScaleGround;
            set
            {
                if (Math.Abs(_config.DistanceScaleGround - value) > 0.01f)
                {
                    _config.DistanceScaleGround = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("distance-scale-vertical")]
        public float DistanceScaleVertical
        {
            get => _config.DistanceScaleVertical;
            set
            {
                if (Math.Abs(_config.DistanceScaleVertical - value) > 0.01f)
                {
                    _config.DistanceScaleVertical = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("vbar-pos-x")]
        public float VerticalBarPosX
        {
            get => _config.VerticalBarPosX;
            set
            {
                if (Math.Abs(_config.VerticalBarPosX - value) > 0.001f)
                {
                    _config.VerticalBarPosX = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("vbar-pos-y")]
        public float VerticalBarPosY
        {
            get => _config.VerticalBarPosY;
            set
            {
                if (Math.Abs(_config.VerticalBarPosY - value) > 0.001f)
                {
                    _config.VerticalBarPosY = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("vbar-pos-z")]
        public float VerticalBarPosZ
        {
            get => _config.VerticalBarPosZ;
            set
            {
                if (Math.Abs(_config.VerticalBarPosZ - value) > 0.001f)
                {
                    _config.VerticalBarPosZ = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("vbar-rot-x")]
        public float VerticalBarRotX
        {
            get => _config.VerticalBarRotX;
            set
            {
                if (Math.Abs(_config.VerticalBarRotX - value) > 0.01f)
                {
                    _config.VerticalBarRotX = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("vbar-rot-y")]
        public float VerticalBarRotY
        {
            get => _config.VerticalBarRotY;
            set
            {
                if (Math.Abs(_config.VerticalBarRotY - value) > 0.01f)
                {
                    _config.VerticalBarRotY = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("vbar-rot-z")]
        public float VerticalBarRotZ
        {
            get => _config.VerticalBarRotZ;
            set
            {
                if (Math.Abs(_config.VerticalBarRotZ - value) > 0.01f)
                {
                    _config.VerticalBarRotZ = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIValue("vbar-scale")]
        public float VerticalBarScale
        {
            get => _config.VerticalBarScale;
            set
            {
                if (Math.Abs(_config.VerticalBarScale - value) > 0.001f)
                {
                    _config.VerticalBarScale = value;
                    _config.Changed();
                    NotifyPropertyChanged();
                }
            }
        }

        [UIAction("reset-defaults")]
        public void ResetDefaults()
        {
            _config.ResetDefaults();
            NotifyPropertyChanged(nameof(GroundZBase));
            NotifyPropertyChanged(nameof(OuterLaneXOffset));
            NotifyPropertyChanged(nameof(InnerLaneTiltAngle));
            NotifyPropertyChanged(nameof(DistanceScaleGround));
            NotifyPropertyChanged(nameof(DistanceScaleVertical));
            NotifyPropertyChanged(nameof(VerticalBarPosX));
            NotifyPropertyChanged(nameof(VerticalBarPosY));
            NotifyPropertyChanged(nameof(VerticalBarPosZ));
            NotifyPropertyChanged(nameof(VerticalBarRotX));
            NotifyPropertyChanged(nameof(VerticalBarRotY));
            NotifyPropertyChanged(nameof(VerticalBarRotZ));
            NotifyPropertyChanged(nameof(VerticalBarScale));
        }

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
