using System;
using IPA.Config.Stores.Attributes;
using IPA.Config.Stores.Converters;
using UnityEngine;

namespace BeatMarathon;

public class PluginConfig
{
	public static PluginConfig Instance { get; set; }

	public virtual bool Enabled { get; set; } = true;


	[UseConverter(typeof(EnumConverter<LeaderboardDataSource>))]
	public virtual LeaderboardDataSource DataSource { get; set; }

	[UseConverter(typeof(EnumConverter<RankingScope>))]
	public virtual RankingScope Scope { get; set; }

	[UseConverter(typeof(EnumConverter<MarathonDisplayMode>))]
	public virtual MarathonDisplayMode DisplayMode { get; set; }

	public virtual bool ShowRank { get; set; } = true;


	public virtual bool ShowAvatar { get; set; } = true;


	public virtual bool ShowPlayerName { get; set; } = true;


	public virtual bool ShowAccuracy { get; set; } = true;


	public virtual bool ShowDelta { get; set; } = true;


	public virtual int AheadRivalsCount { get; set; } = 5;


	public virtual float GroundMaxDistance { get; set; } = 20f;


	[UseConverter(typeof(EnumConverter<OvertakingMode>))]
	public virtual OvertakingMode OvertakeMode { get; set; } = OvertakingMode.Multiplier;


	public virtual float OvertakingStartMultiplier { get; set; } = 0.9f;


	public virtual float GroundZBase { get; set; } = 2.5f;


	public virtual float OuterLaneXOffset { get; set; } = 2f;


	public virtual float InnerLaneXOffset { get; set; } = 0.8f;


	public virtual float OuterLaneYOffset { get; set; } = 0.8f;


	public virtual float InnerLaneYOffset { get; set; } = 0.05f;


	public virtual float OverlapThreshold { get; set; } = 0.05f;


	public virtual float InnerLaneTiltAngle { get; set; } = 65f;


	public virtual float DistanceScaleGround { get; set; } = 15f;


	public virtual float DistanceScaleVertical { get; set; } = 1.2f;


	public virtual float VerticalBarPosX { get; set; } = 1.8f;


	public virtual float VerticalBarPosY { get; set; } = 1.5f;


	public virtual float VerticalBarPosZ { get; set; } = 3f;


	public virtual float VerticalBarRotX { get; set; }

	public virtual float VerticalBarRotY { get; set; } = 15f;


	public virtual float VerticalBarRotZ { get; set; }

	public virtual float VerticalBarScale { get; set; } = 1f;


	public event Action ConfigChangedEvent;

	public Vector3 GetVerticalBarPosition()
	{
		return new Vector3(VerticalBarPosX, VerticalBarPosY, VerticalBarPosZ);
	}

	public Quaternion GetVerticalBarRotation()
	{
		return Quaternion.Euler(VerticalBarRotX, VerticalBarRotY, VerticalBarRotZ);
	}

	public void ResetDefaults()
	{
		AheadRivalsCount = 5;
		GroundMaxDistance = 20f;
		OvertakeMode = OvertakingMode.Multiplier;
		OvertakingStartMultiplier = 0.9f;
		GroundZBase = 2.5f;
		OuterLaneXOffset = 2f;
		InnerLaneXOffset = 0.8f;
		OuterLaneYOffset = 0.8f;
		InnerLaneYOffset = 0.05f;
		OverlapThreshold = 0.05f;
		InnerLaneTiltAngle = 65f;
		DistanceScaleGround = 15f;
		DistanceScaleVertical = 1.2f;
		VerticalBarPosX = 1.8f;
		VerticalBarPosY = 1.5f;
		VerticalBarPosZ = 3f;
		VerticalBarRotX = 0f;
		VerticalBarRotY = 15f;
		VerticalBarRotZ = 0f;
		VerticalBarScale = 1f;
		Changed();
	}

	public virtual void OnReload()
	{
	}

	public virtual void Changed()
	{
		this.ConfigChangedEvent?.Invoke();
	}

	public virtual void CopyFrom(PluginConfig other)
	{
		if (other != null)
		{
			Enabled = other.Enabled;
			DataSource = other.DataSource;
			Scope = other.Scope;
			DisplayMode = other.DisplayMode;
			ShowRank = other.ShowRank;
			ShowAvatar = other.ShowAvatar;
			ShowPlayerName = other.ShowPlayerName;
			ShowAccuracy = other.ShowAccuracy;
			ShowDelta = other.ShowDelta;
			AheadRivalsCount = other.AheadRivalsCount;
			GroundMaxDistance = other.GroundMaxDistance;
			OvertakeMode = other.OvertakeMode;
			OvertakingStartMultiplier = other.OvertakingStartMultiplier;
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
