using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.GameplaySetup;
using BeatSaberMarkupLanguage.Settings;
using BeatSaberMarkupLanguage.Util;
using Zenject;

namespace BeatMarathon.UI;

public class SettingsViewController : IInitializable, IDisposable, INotifyPropertyChanged
{
	private const string ResourceName = "BeatMarathon.UI.Views.SettingsView.bsml";

	private const string MenuTitle = "BeatMarathon";

	private readonly PluginConfig _config;

	[UIValue("data-source-options")]
	public List<object> DataSourceOptions { get; } = new List<object> { "BeatLeader", "ScoreSaber" };


	[UIValue("ranking-scope-options")]
	public List<object> RankingScopeOptions { get; } = new List<object> { "Global", "Friends", "Country" };


	[UIValue("display-mode-options")]
	public List<object> DisplayModeOptions { get; } = new List<object> { "Road Sign (Ground)", "Vertical Bar" };


	[UIValue("overtake-mode-options")]
	public List<object> OvertakeModeOptions { get; } = new List<object> { "Multiplier (倍率指定)", "AUTO (最下位から)", "Off (通常)" };


	[UIValue("enabled")]
	public bool Enabled
	{
		get
		{
			return _config.Enabled;
		}
		set
		{
			if (_config.Enabled != value)
			{
				_config.Enabled = value;
				_config.Changed();
				NotifyPropertyChanged("Enabled");
			}
		}
	}

	[UIValue("data-source")]
	public string DataSource
	{
		get
		{
			if (_config.DataSource != LeaderboardDataSource.ScoreSaber)
			{
				return "BeatLeader";
			}
			return "ScoreSaber";
		}
		set
		{
			LeaderboardDataSource leaderboardDataSource = (string.Equals(value, "ScoreSaber", StringComparison.OrdinalIgnoreCase) ? LeaderboardDataSource.ScoreSaber : LeaderboardDataSource.BeatLeader);
			if (_config.DataSource != leaderboardDataSource)
			{
				_config.DataSource = leaderboardDataSource;
				_config.Changed();
				NotifyPropertyChanged("DataSource");
			}
		}
	}

	[UIValue("ranking-scope")]
	public string Scope
	{
		get
		{
			return _config.Scope.ToString();
		}
		set
		{
			if (Enum.TryParse<RankingScope>(value, ignoreCase: true, out var result) && _config.Scope != result)
			{
				_config.Scope = result;
				_config.Changed();
				NotifyPropertyChanged("Scope");
			}
		}
	}

	[UIValue("display-mode")]
	public string DisplayMode
	{
		get
		{
			if (_config.DisplayMode != MarathonDisplayMode.VerticalBar)
			{
				return "Road Sign (Ground)";
			}
			return "Vertical Bar";
		}
		set
		{
			MarathonDisplayMode marathonDisplayMode = (string.Equals(value, "Vertical Bar", StringComparison.OrdinalIgnoreCase) ? MarathonDisplayMode.VerticalBar : MarathonDisplayMode.RoadSignMarathon);
			if (_config.DisplayMode != marathonDisplayMode)
			{
				_config.DisplayMode = marathonDisplayMode;
				_config.Changed();
				NotifyPropertyChanged("DisplayMode");
			}
		}
	}

	[UIValue("ahead-rivals-count")]
	public int AheadRivalsCount
	{
		get
		{
			return _config.AheadRivalsCount;
		}
		set
		{
			if (_config.AheadRivalsCount != value)
			{
				_config.AheadRivalsCount = value;
				_config.Changed();
				NotifyPropertyChanged("AheadRivalsCount");
			}
		}
	}

	[UIValue("overtake-mode")]
	public string OvertakeMode
	{
		get
		{
			return _config.OvertakeMode switch
			{
				OvertakingMode.AutoLowest => "AUTO (最下位から)", 
				OvertakingMode.Off => "Off (通常)", 
				_ => "Multiplier (倍率指定)", 
			};
		}
		set
		{
			OvertakingMode overtakingMode = ((value != null && value.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase)) ? OvertakingMode.AutoLowest : ((value == null || !value.StartsWith("Off", StringComparison.OrdinalIgnoreCase)) ? OvertakingMode.Multiplier : OvertakingMode.Off));
			if (_config.OvertakeMode != overtakingMode)
			{
				_config.OvertakeMode = overtakingMode;
				_config.Changed();
				NotifyPropertyChanged("OvertakeMode");
			}
		}
	}

	[UIValue("overtake-start-mult")]
	public float OvertakingStartMultiplier
	{
		get
		{
			return _config.OvertakingStartMultiplier;
		}
		set
		{
			if (Math.Abs(_config.OvertakingStartMultiplier - value) > 0.001f)
			{
				_config.OvertakingStartMultiplier = value;
				_config.Changed();
				NotifyPropertyChanged("OvertakingStartMultiplier");
			}
		}
	}

	[UIValue("ground-max-distance")]
	public float GroundMaxDistance
	{
		get
		{
			return _config.GroundMaxDistance;
		}
		set
		{
			if (Math.Abs(_config.GroundMaxDistance - value) > 0.01f)
			{
				_config.GroundMaxDistance = value;
				_config.Changed();
				NotifyPropertyChanged("GroundMaxDistance");
			}
		}
	}

	[UIValue("show-rank")]
	public bool ShowRank
	{
		get
		{
			return _config.ShowRank;
		}
		set
		{
			if (_config.ShowRank != value)
			{
				_config.ShowRank = value;
				_config.Changed();
				NotifyPropertyChanged("ShowRank");
			}
		}
	}

	[UIValue("show-avatar")]
	public bool ShowAvatar
	{
		get
		{
			return _config.ShowAvatar;
		}
		set
		{
			if (_config.ShowAvatar != value)
			{
				_config.ShowAvatar = value;
				_config.Changed();
				NotifyPropertyChanged("ShowAvatar");
			}
		}
	}

	[UIValue("show-player-name")]
	public bool ShowPlayerName
	{
		get
		{
			return _config.ShowPlayerName;
		}
		set
		{
			if (_config.ShowPlayerName != value)
			{
				_config.ShowPlayerName = value;
				_config.Changed();
				NotifyPropertyChanged("ShowPlayerName");
			}
		}
	}

	[UIValue("show-accuracy")]
	public bool ShowAccuracy
	{
		get
		{
			return _config.ShowAccuracy;
		}
		set
		{
			if (_config.ShowAccuracy != value)
			{
				_config.ShowAccuracy = value;
				_config.Changed();
				NotifyPropertyChanged("ShowAccuracy");
			}
		}
	}

	[UIValue("show-delta")]
	public bool ShowDelta
	{
		get
		{
			return _config.ShowDelta;
		}
		set
		{
			if (_config.ShowDelta != value)
			{
				_config.ShowDelta = value;
				_config.Changed();
				NotifyPropertyChanged("ShowDelta");
			}
		}
	}

	[UIValue("ground-z-base")]
	public float GroundZBase
	{
		get
		{
			return _config.GroundZBase;
		}
		set
		{
			if (Math.Abs(_config.GroundZBase - value) > 0.001f)
			{
				_config.GroundZBase = value;
				_config.Changed();
				NotifyPropertyChanged("GroundZBase");
			}
		}
	}

	[UIValue("outer-lane-x")]
	public float OuterLaneXOffset
	{
		get
		{
			return _config.OuterLaneXOffset;
		}
		set
		{
			if (Math.Abs(_config.OuterLaneXOffset - value) > 0.001f)
			{
				_config.OuterLaneXOffset = value;
				_config.Changed();
				NotifyPropertyChanged("OuterLaneXOffset");
			}
		}
	}

	[UIValue("inner-lane-tilt")]
	public float InnerLaneTiltAngle
	{
		get
		{
			return _config.InnerLaneTiltAngle;
		}
		set
		{
			if (Math.Abs(_config.InnerLaneTiltAngle - value) > 0.01f)
			{
				_config.InnerLaneTiltAngle = value;
				_config.Changed();
				NotifyPropertyChanged("InnerLaneTiltAngle");
			}
		}
	}

	[UIValue("distance-scale-ground")]
	public float DistanceScaleGround
	{
		get
		{
			return _config.DistanceScaleGround;
		}
		set
		{
			if (Math.Abs(_config.DistanceScaleGround - value) > 0.01f)
			{
				_config.DistanceScaleGround = value;
				_config.Changed();
				NotifyPropertyChanged("DistanceScaleGround");
			}
		}
	}

	[UIValue("distance-scale-vertical")]
	public float DistanceScaleVertical
	{
		get
		{
			return _config.DistanceScaleVertical;
		}
		set
		{
			if (Math.Abs(_config.DistanceScaleVertical - value) > 0.01f)
			{
				_config.DistanceScaleVertical = value;
				_config.Changed();
				NotifyPropertyChanged("DistanceScaleVertical");
			}
		}
	}

	[UIValue("vbar-pos-x")]
	public float VerticalBarPosX
	{
		get
		{
			return _config.VerticalBarPosX;
		}
		set
		{
			if (Math.Abs(_config.VerticalBarPosX - value) > 0.001f)
			{
				_config.VerticalBarPosX = value;
				_config.Changed();
				NotifyPropertyChanged("VerticalBarPosX");
			}
		}
	}

	[UIValue("vbar-pos-y")]
	public float VerticalBarPosY
	{
		get
		{
			return _config.VerticalBarPosY;
		}
		set
		{
			if (Math.Abs(_config.VerticalBarPosY - value) > 0.001f)
			{
				_config.VerticalBarPosY = value;
				_config.Changed();
				NotifyPropertyChanged("VerticalBarPosY");
			}
		}
	}

	[UIValue("vbar-pos-z")]
	public float VerticalBarPosZ
	{
		get
		{
			return _config.VerticalBarPosZ;
		}
		set
		{
			if (Math.Abs(_config.VerticalBarPosZ - value) > 0.001f)
			{
				_config.VerticalBarPosZ = value;
				_config.Changed();
				NotifyPropertyChanged("VerticalBarPosZ");
			}
		}
	}

	[UIValue("vbar-rot-x")]
	public float VerticalBarRotX
	{
		get
		{
			return _config.VerticalBarRotX;
		}
		set
		{
			if (Math.Abs(_config.VerticalBarRotX - value) > 0.01f)
			{
				_config.VerticalBarRotX = value;
				_config.Changed();
				NotifyPropertyChanged("VerticalBarRotX");
			}
		}
	}

	[UIValue("vbar-rot-y")]
	public float VerticalBarRotY
	{
		get
		{
			return _config.VerticalBarRotY;
		}
		set
		{
			if (Math.Abs(_config.VerticalBarRotY - value) > 0.01f)
			{
				_config.VerticalBarRotY = value;
				_config.Changed();
				NotifyPropertyChanged("VerticalBarRotY");
			}
		}
	}

	[UIValue("vbar-rot-z")]
	public float VerticalBarRotZ
	{
		get
		{
			return _config.VerticalBarRotZ;
		}
		set
		{
			if (Math.Abs(_config.VerticalBarRotZ - value) > 0.01f)
			{
				_config.VerticalBarRotZ = value;
				_config.Changed();
				NotifyPropertyChanged("VerticalBarRotZ");
			}
		}
	}

	[UIValue("vbar-scale")]
	public float VerticalBarScale
	{
		get
		{
			return _config.VerticalBarScale;
		}
		set
		{
			if (Math.Abs(_config.VerticalBarScale - value) > 0.001f)
			{
				_config.VerticalBarScale = value;
				_config.Changed();
				NotifyPropertyChanged("VerticalBarScale");
			}
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	public SettingsViewController(PluginConfig config)
	{
		_config = config;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public void Initialize()
	{
		try
		{
			ZenjectSingleton<BSMLSettings>.Instance.AddSettingsMenu("BeatMarathon", "BeatMarathon.UI.Views.SettingsView.bsml", this);
		}
		catch (Exception arg)
		{
			Plugin.Log?.Error($"Failed to register BSMLSettings menu: {arg}");
		}
		try
		{
			ZenjectSingleton<GameplaySetup>.Instance.AddTab("BeatMarathon", "BeatMarathon.UI.Views.SettingsView.bsml", this, MenuType.All);
		}
		catch (Exception arg2)
		{
			Plugin.Log?.Error($"Failed to register GameplaySetup tab: {arg2}");
		}
	}

	public void Dispose()
	{
		if (ZenjectSingleton<BSMLSettings>.Instance != null)
		{
			ZenjectSingleton<BSMLSettings>.Instance.RemoveSettingsMenu(this);
		}
		if (ZenjectSingleton<GameplaySetup>.Instance != null)
		{
			ZenjectSingleton<GameplaySetup>.Instance.RemoveTab("BeatMarathon");
		}
	}

	[UIAction("reset-defaults")]
	public void ResetDefaults()
	{
		_config.ResetDefaults();
		NotifyPropertyChanged("AheadRivalsCount");
		NotifyPropertyChanged("OvertakeMode");
		NotifyPropertyChanged("OvertakingStartMultiplier");
		NotifyPropertyChanged("GroundMaxDistance");
		NotifyPropertyChanged("GroundZBase");
		NotifyPropertyChanged("OuterLaneXOffset");
		NotifyPropertyChanged("InnerLaneTiltAngle");
		NotifyPropertyChanged("DistanceScaleGround");
		NotifyPropertyChanged("DistanceScaleVertical");
		NotifyPropertyChanged("VerticalBarPosX");
		NotifyPropertyChanged("VerticalBarPosY");
		NotifyPropertyChanged("VerticalBarPosZ");
		NotifyPropertyChanged("VerticalBarRotX");
		NotifyPropertyChanged("VerticalBarRotY");
		NotifyPropertyChanged("VerticalBarRotZ");
		NotifyPropertyChanged("VerticalBarScale");
	}

	protected void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
