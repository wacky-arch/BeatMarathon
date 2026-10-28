using System;
using System.Runtime.CompilerServices;
using IPA.Config.Stores;
using IPA.Config.Stores.Attributes;
using IPA.Config.Stores.Converters;
using UnityEngine;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]

namespace RealtimeRankMarathon
{
    public enum LeaderboardDataSource
    {
        BeatLeader = 0,
        ScoreSaber = 1
    }

    public enum RankingScope
    {
        Global = 0,
        Friends = 1,
        Country = 2
    }

    public enum MarathonDisplayMode
    {
        RoadSignMarathon = 0,
        VerticalBar = 1
    }

    public class PluginConfig
    {
        public static PluginConfig Instance { get; set; }

        // 1. Enable Mod
        public virtual bool Enabled { get; set; } = true;

        // 2. Data Source
        [UseConverter(typeof(EnumConverter<LeaderboardDataSource>))]
        public virtual LeaderboardDataSource DataSource { get; set; } = LeaderboardDataSource.BeatLeader;

        // 3. Ranking Scope
        [UseConverter(typeof(EnumConverter<RankingScope>))]
        public virtual RankingScope Scope { get; set; } = RankingScope.Global;

        // 4. Display Mode
        [UseConverter(typeof(EnumConverter<MarathonDisplayMode>))]
        public virtual MarathonDisplayMode DisplayMode { get; set; } = MarathonDisplayMode.RoadSignMarathon;

        // Display Items (個別表示項目のON/OFF: #順位 アイコン画像 プレイヤー名 精度 自分との精度差)
        public virtual bool ShowRank { get; set; } = true;
        public virtual bool ShowAvatar { get; set; } = true;
        public virtual bool ShowPlayerName { get; set; } = true;
        public virtual bool ShowAccuracy { get; set; } = true;
        public virtual bool ShowDelta { get; set; } = true;

        // 5. Ground Z-Base (m)
        public virtual float GroundZBase { get; set; } = 2.5f;

        // 6. Outer Lane X-Offset (m)
        public virtual float OuterLaneXOffset { get; set; } = 2.0f;

        // Inner Lane X-Offset & Y-Offsets & Overlap Threshold
        public virtual float InnerLaneXOffset { get; set; } = 0.8f;
        public virtual float OuterLaneYOffset { get; set; } = 0.8f;
        public virtual float InnerLaneYOffset { get; set; } = 0.05f;
        public virtual float OverlapThreshold { get; set; } = 0.05f;

        // 7. Inner Lane Tilt Angle (deg)
        public virtual float InnerLaneTiltAngle { get; set; } = 65.0f;

        // 8. Distance Scale (1.0%差あたりの距離倍率: Ground=15.0m (0.1%差で1.5m), Vertical=1.2m)
        public virtual float DistanceScaleGround { get; set; } = 15.0f;
        public virtual float DistanceScaleVertical { get; set; } = 1.2f;

        // 9. Vertical Bar Transform
        public virtual float VerticalBarPosX { get; set; } = 1.8f;
        public virtual float VerticalBarPosY { get; set; } = 1.5f;
        public virtual float VerticalBarPosZ { get; set; } = 3.0f;

        public virtual float VerticalBarRotX { get; set; } = 0.0f;
        public virtual float VerticalBarRotY { get; set; } = 15.0f;
        public virtual float VerticalBarRotZ { get; set; } = 0.0f;

        public virtual float VerticalBarScale { get; set; } = 1.0f;

        public event Action ConfigChangedEvent;

        public Vector3 GetVerticalBarPosition() => new Vector3(VerticalBarPosX, VerticalBarPosY, VerticalBarPosZ);
        public Quaternion GetVerticalBarRotation() => Quaternion.Euler(VerticalBarRotX, VerticalBarRotY, VerticalBarRotZ);

        public void ResetDefaults()
        {
            GroundZBase = 2.5f;
            OuterLaneXOffset = 2.0f;
            InnerLaneXOffset = 0.8f;
            OuterLaneYOffset = 0.8f;
            InnerLaneYOffset = 0.05f;
            OverlapThreshold = 0.05f;
            InnerLaneTiltAngle = 65.0f;
            DistanceScaleGround = 15.0f;
            DistanceScaleVertical = 1.2f;
            VerticalBarPosX = 1.8f;
            VerticalBarPosY = 1.5f;
            VerticalBarPosZ = 3.0f;
            VerticalBarRotX = 0.0f;
            VerticalBarRotY = 15.0f;
            VerticalBarRotZ = 0.0f;
            VerticalBarScale = 1.0f;
            Changed();
        }

        public virtual void OnReload()
        {
        }

        public virtual void Changed()
        {
            ConfigChangedEvent?.Invoke();
        }

        public virtual void CopyFrom(PluginConfig other)
        {
            if (other == null) return;
            Enabled = other.Enabled;
            DataSource = other.DataSource;
            Scope = other.Scope;
            DisplayMode = other.DisplayMode;
            ShowRank = other.ShowRank;
            ShowAvatar = other.ShowAvatar;
            ShowPlayerName = other.ShowPlayerName;
            ShowAccuracy = other.ShowAccuracy;
            ShowDelta = other.ShowDelta;
            GroundZBase = other.GroundZBase;
            OuterLaneXOffset = other.OuterLaneXOffset;
            InnerLaneXOffset = other.InnerLaneXOffset;
            OuterLaneYOffset = other.OuterLaneYOffset;
            InnerLaneYOffset = other.InnerLaneYOffset;
            OverlapThreshold = other.OverlapThreshold;
            InnerLaneTiltAngle = other.InnerLaneTiltAngle;
            DistanceScaleGround = other.DistanceScaleGround;
            DistanceScaleVertical = other.DistanceScaleVertical;
            VerticalBarPosX = other.VerticalBarPosX;
            VerticalBarPosY = other.VerticalBarPosY;
            VerticalBarPosZ = other.VerticalBarPosZ;
            VerticalBarRotX = other.VerticalBarRotX;
            VerticalBarRotY = other.VerticalBarRotY;
            VerticalBarRotZ = other.VerticalBarRotZ;
            VerticalBarScale = other.VerticalBarScale;
        }
    }
}
