using System;
using System.Collections.Generic;
using UnityEngine;

namespace BeatMarathon.Models;

public class LeaderboardEntry
{
	public sealed class DescendingAccuracyComparer : IComparer<LeaderboardEntry>
	{
		public static readonly DescendingAccuracyComparer Instance = new DescendingAccuracyComparer();

		public int Compare(LeaderboardEntry x, LeaderboardEntry y)
		{
			if (x == y)
			{
				return 0;
			}
			if (x == null)
			{
				return 1;
			}
			return y?.Accuracy.CompareTo(x.Accuracy) ?? (-1);
		}
	}

	public sealed class FullLeaderboardSorter : IComparer<LeaderboardEntry>
	{
		public static readonly FullLeaderboardSorter Instance = new FullLeaderboardSorter();

		public int Compare(LeaderboardEntry x, LeaderboardEntry y)
		{
			if (x == y)
			{
				return 0;
			}
			if (x == null)
			{
				return 1;
			}
			if (y == null)
			{
				return -1;
			}
			int num = y.Accuracy.CompareTo(x.Accuracy);
			if (num != 0)
			{
				return num;
			}
			int num2 = x.Rank.CompareTo(y.Rank);
			if (num2 != 0)
			{
				return num2;
			}
			return string.Compare(x.PlayerId, y.PlayerId, StringComparison.Ordinal);
		}
	}

	public string PlayerId { get; set; } = string.Empty;


	public string PlayerName { get; set; } = string.Empty;


	public int Rank { get; set; }

	public double Accuracy { get; set; }

	public string AvatarUrl { get; set; } = string.Empty;


	public bool IsSelf { get; set; }

	public int SlotIndex { get; set; }

	public float AssignedGroundX { get; set; }

	public float AssignedGroundY { get; set; }

	public float AssignedGroundRotationX { get; set; }

	public float AssignedVerticalBarXOffset { get; set; }

	public Sprite CachedAvatar { get; set; }

	public double AccuracyPercent => Accuracy * 100.0;

	public static void SortAndAssignSlots(List<LeaderboardEntry> entries, PluginConfig config)
	{
		if (entries == null || entries.Count == 0)
		{
			return;
		}
		entries.Sort(FullLeaderboardSorter.Instance);
		float outerX = config?.OuterLaneXOffset ?? 2f;
		float innerX = config?.InnerLaneXOffset ?? 0.8f;
		float outerY = config?.OuterLaneYOffset ?? 0.8f;
		float innerY = config?.InnerLaneYOffset ?? 0.05f;
		float tiltAngle = config?.InnerLaneTiltAngle ?? 65f;
		double num = ((config != null) ? ((double)config.OverlapThreshold) : 0.05);
		int num2 = 0;
		int num3 = 0;
		for (int num4 = entries.Count - 1; num4 >= 0; num4--)
		{
			LeaderboardEntry leaderboardEntry = entries[num4];
			if (num4 == entries.Count - 1)
			{
				num2 = num3;
				num3 = 1 - num3;
			}
			else
			{
				LeaderboardEntry leaderboardEntry2 = entries[num4 + 1];
				if (Math.Abs(leaderboardEntry.Accuracy - leaderboardEntry2.Accuracy) * 100.0 < num)
				{
					num2 = (num2 + 1) % 4;
				}
				else
				{
					num2 = num3;
					num3 = 1 - num3;
				}
			}
			ApplySlotMetrics(leaderboardEntry, num2, outerX, innerX, outerY, innerY, tiltAngle);
		}
	}

	public static void ApplySlotMetrics(LeaderboardEntry entry, int slotIndex, float outerX, float innerX, float outerY, float innerY, float tiltAngle)
	{
		entry.SlotIndex = slotIndex;
		switch (slotIndex)
		{
		case 0:
			entry.AssignedGroundX = 0f - outerX;
			entry.AssignedGroundY = outerY;
			entry.AssignedGroundRotationX = 0f;
			entry.AssignedVerticalBarXOffset = -0.35f;
			break;
		case 1:
			entry.AssignedGroundX = outerX;
			entry.AssignedGroundY = outerY;
			entry.AssignedGroundRotationX = 0f;
			entry.AssignedVerticalBarXOffset = 0.35f;
			break;
		case 2:
			entry.AssignedGroundX = 0f - innerX;
			entry.AssignedGroundY = innerY;
			entry.AssignedGroundRotationX = tiltAngle;
			entry.AssignedVerticalBarXOffset = -0.75f;
			break;
		default:
			entry.AssignedGroundX = innerX;
			entry.AssignedGroundY = innerY;
			entry.AssignedGroundRotationX = tiltAngle;
			entry.AssignedVerticalBarXOffset = 0.75f;
			break;
		}
	}
}
