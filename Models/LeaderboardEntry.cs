using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealtimeRankMarathon.Models
{
    public class LeaderboardEntry
    {
        public string PlayerId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public int Rank { get; set; }
        public double Accuracy { get; set; }
        public string AvatarUrl { get; set; } = string.Empty;
        public bool IsSelf { get; set; }

        // 事前割り当てされる配置スロット (0〜3) と地面モード用・縦型バー用座標/角度
        public int SlotIndex { get; set; }
        public float AssignedGroundX { get; set; }
        public float AssignedGroundY { get; set; }
        public float AssignedGroundRotationX { get; set; }
        public float AssignedVerticalBarXOffset { get; set; }

        // メモリキャッシュから割り当てられたアイコンSprite (プレイ中は新規生成せず参照のみ)
        public Sprite CachedAvatar { get; set; }

        public double AccuracyPercent => Accuracy * 100.0;

        /// <summary>
        /// BinarySearch 用の降順 Comparer (Accuracyが高い方が先 = インデックス0側)
        /// </summary>
        public sealed class DescendingAccuracyComparer : IComparer<LeaderboardEntry>
        {
            public static readonly DescendingAccuracyComparer Instance = new DescendingAccuracyComparer();

            public int Compare(LeaderboardEntry x, LeaderboardEntry y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return 1;
                if (y == null) return -1;

                // 降順比較: y.Accuracy と x.Accuracy を比較
                return y.Accuracy.CompareTo(x.Accuracy);
            }
        }

        /// <summary>
        /// リスト整列用の降順 Comparer (同精度の場合は Rank 昇順)
        /// </summary>
        public sealed class FullLeaderboardSorter : IComparer<LeaderboardEntry>
        {
            public static readonly FullLeaderboardSorter Instance = new FullLeaderboardSorter();

            public int Compare(LeaderboardEntry x, LeaderboardEntry y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return 1;
                if (y == null) return -1;

                int accCmp = y.Accuracy.CompareTo(x.Accuracy);
                if (accCmp != 0) return accCmp;

                int rankCmp = x.Rank.CompareTo(y.Rank);
                if (rankCmp != 0) return rankCmp;

                return string.Compare(x.PlayerId, y.PlayerId, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// リストを精度降順に整列し、下位から上位へ走査して交通標識スタイル＆密集時寝かせ配置のSlot(0〜3)を事前割り当てする
        /// </summary>
        public static void SortAndAssignSlots(List<LeaderboardEntry> entries, PluginConfig config)
        {
            if (entries == null || entries.Count == 0) return;

            entries.Sort(FullLeaderboardSorter.Instance);

            float outerX = config != null ? config.OuterLaneXOffset : 2.0f;
            float innerX = config != null ? config.InnerLaneXOffset : 0.8f;
            float outerY = config != null ? config.OuterLaneYOffset : 0.8f;
            float innerY = config != null ? config.InnerLaneYOffset : 0.05f;
            float tiltAngle = config != null ? config.InnerLaneTiltAngle : 65.0f;
            double overlapThresholdPercent = config != null ? config.OverlapThreshold : 0.05;

            int currentSlot = 0;
            int nextOuterSlot = 0; // 非密集時は 0 (左外側) と 1 (右外側) を交互に割り当て

            // 下位 (Count - 1) から上位 (0) へ走査
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];

                if (i == entries.Count - 1)
                {
                    currentSlot = nextOuterSlot;
                    nextOuterSlot = 1 - nextOuterSlot;
                }
                else
                {
                    var lowerEntry = entries[i + 1];
                    double diffPercent = Math.Abs(entry.Accuracy - lowerEntry.Accuracy) * 100.0;

                    if (diffPercent < overlapThresholdPercent)
                    {
                        // 密集時は 0 -> 1 -> 2 -> 3 とスロットを進める
                        currentSlot = (currentSlot + 1) % 4;
                    }
                    else
                    {
                        // 閾値以上離れていれば外側レーン (0 または 1) に交互リセット
                        currentSlot = nextOuterSlot;
                        nextOuterSlot = 1 - nextOuterSlot;
                    }
                }

                ApplySlotMetrics(entry, currentSlot, outerX, innerX, outerY, innerY, tiltAngle);
            }
        }

        public static void ApplySlotMetrics(
            LeaderboardEntry entry,
            int slotIndex,
            float outerX,
            float innerX,
            float outerY,
            float innerY,
            float tiltAngle)
        {
            entry.SlotIndex = slotIndex;

            switch (slotIndex)
            {
                case 0:
                    // Slot 0 (左外側・標識型): X = -2.0m, Y = 0.8m, Rotation X = 0° (垂直に立てる)
                    entry.AssignedGroundX = -outerX;
                    entry.AssignedGroundY = outerY;
                    entry.AssignedGroundRotationX = 0f;
                    entry.AssignedVerticalBarXOffset = -0.35f;
                    break;

                case 1:
                    // Slot 1 (右外側・標識型): X = +2.0m, Y = 0.8m, Rotation X = 0° (垂直に立てる)
                    entry.AssignedGroundX = +outerX;
                    entry.AssignedGroundY = outerY;
                    entry.AssignedGroundRotationX = 0f;
                    entry.AssignedVerticalBarXOffset = +0.35f;
                    break;

                case 2:
                    // Slot 2 (左内側・寝かせ型): X = -0.8m, Y = 0.05m, Rotation X = 65° (床近くに配置し奥へ寝かせる)
                    entry.AssignedGroundX = -innerX;
                    entry.AssignedGroundY = innerY;
                    entry.AssignedGroundRotationX = tiltAngle;
                    entry.AssignedVerticalBarXOffset = -0.75f;
                    break;

                case 3:
                default:
                    // Slot 3 (右内側・寝かせ型): X = +0.8m, Y = 0.05m, Rotation X = 65° (床近くに配置し奥へ寝かせる)
                    entry.AssignedGroundX = +innerX;
                    entry.AssignedGroundY = innerY;
                    entry.AssignedGroundRotationX = tiltAngle;
                    entry.AssignedVerticalBarXOffset = +0.75f;
                    break;
            }
        }
    }
}
