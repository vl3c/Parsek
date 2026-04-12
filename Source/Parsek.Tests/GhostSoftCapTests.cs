using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostSoftCapTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostSoftCapTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostSoftCapManager.ResetThresholds();
            GhostSoftCapManager.Enabled = true; // tests assume caps are active
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostSoftCapManager.ResetThresholds();
        }

        #region ClassifyPriority — Looping Recordings

        [Fact]
        public void ClassifyPriority_LoopingHighCycle_ReturnsLoopedOldest()
        {
            var rec = new Recording { LoopPlayback = true, VesselName = "Looper" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 10);
            Assert.Equal(GhostPriority.LoopedOldest, priority);
        }

        [Fact]
        public void ClassifyPriority_LoopingLowCycle_ReturnsLoopedRecent()
        {
            var rec = new Recording { LoopPlayback = true, VesselName = "Looper" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 2);
            Assert.Equal(GhostPriority.LoopedRecent, priority);
        }

        [Fact]
        public void ClassifyPriority_LoopingAtBoundary5_ReturnsLoopedRecent()
        {
            // Cycle index 5 is the boundary — threshold is >, not >=
            var rec = new Recording { LoopPlayback = true, VesselName = "Looper" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 5);
            Assert.Equal(GhostPriority.LoopedRecent, priority);
        }

        [Fact]
        public void ClassifyPriority_LoopingAtBoundary6_ReturnsLoopedOldest()
        {
            var rec = new Recording { LoopPlayback = true, VesselName = "Looper" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 6);
            Assert.Equal(GhostPriority.LoopedOldest, priority);
        }

        [Fact]
        public void ClassifyPriority_LoopingZeroCycle_ReturnsLoopedRecent()
        {
            var rec = new Recording { LoopPlayback = true, VesselName = "Looper" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 0);
            Assert.Equal(GhostPriority.LoopedRecent, priority);
        }

        #endregion

        #region ClassifyPriority — Debris and Normal

        [Fact]
        public void ClassifyPriority_Debris_ReturnsBackgroundDebris()
        {
            var rec = new Recording { IsDebris = true, VesselName = "Stage 1" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 0);
            Assert.Equal(GhostPriority.BackgroundDebris, priority);
        }

        [Fact]
        public void ClassifyPriority_NormalRecording_ReturnsFullTimeline()
        {
            var rec = new Recording { VesselName = "Rocket" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 0);
            Assert.Equal(GhostPriority.FullTimeline, priority);
        }

        [Fact]
        public void ClassifyPriority_LoopTakesPrecedenceOverDebris()
        {
            // A recording that is both looped and debris — LoopPlayback checked first
            var rec = new Recording { LoopPlayback = true, IsDebris = true, VesselName = "DebrisLoop" };
            var priority = GhostSoftCapManager.ClassifyPriority(rec, 10);
            Assert.Equal(GhostPriority.LoopedOldest, priority);
        }

        #endregion

        #region EvaluateCaps — Disabled

        [Fact]
        public void EvaluateCaps_Disabled_ReturnsEmpty_EvenAboveAllThresholds()
        {
            GhostSoftCapManager.Enabled = false;
            // 20 zone1 (above despawn 15), 25 zone2 (above simplify 20)
            var zone1 = MakeGhostList(20, GhostPriority.LoopedOldest);
            var zone2 = MakeGhostList(25, GhostPriority.FullTimeline);

            var actions = GhostSoftCapManager.EvaluateCaps(20, 25, zone1, zone2);

            Assert.Empty(actions);
        }

        #endregion

        #region EvaluateCaps — Below Thresholds

        [Fact]
        public void EvaluateCaps_AllBelowThresholds_ReturnsEmpty()
        {
            var zone1 = MakeGhostList(5, GhostPriority.FullTimeline);
            var zone2 = MakeGhostList(10, GhostPriority.FullTimeline);
            var actions = GhostSoftCapManager.EvaluateCaps(5, 10, zone1, zone2);
            Assert.Empty(actions);
        }

        [Fact]
        public void EvaluateCaps_ExactlyAtThresholds_ReturnsEmpty()
        {
            // Threshold is >, not >= — exactly at threshold means no action
            var zone1 = MakeGhostList(8, GhostPriority.FullTimeline);
            var zone2 = MakeGhostList(20, GhostPriority.FullTimeline);
            var actions = GhostSoftCapManager.EvaluateCaps(8, 20, zone1, zone2);
            Assert.Empty(actions);
        }

        [Fact]
        public void EvaluateCaps_EmptyGhostLists_ReturnsEmpty()
        {
            var empty = new List<(int, GhostPriority)>();
            var actions = GhostSoftCapManager.EvaluateCaps(0, 0, empty, empty);
            Assert.Empty(actions);
        }

        #endregion

        #region EvaluateCaps — Zone 1 Fidelity Reduction

        [Fact]
        public void EvaluateCaps_Zone1At10_ReducesTwoLowestPriority()
        {
            // 10 ghosts, threshold 8 → 2 should get ReduceFidelity
            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.FullTimeline),
                (1, GhostPriority.FullTimeline),
                (2, GhostPriority.BackgroundDebris),
                (3, GhostPriority.LoopedRecent),
                (4, GhostPriority.LoopedOldest),
                (5, GhostPriority.FullTimeline),
                (6, GhostPriority.FullTimeline),
                (7, GhostPriority.BackgroundDebris),
                (8, GhostPriority.LoopedRecent),
                (9, GhostPriority.FullTimeline),
            };
            var zone2 = new List<(int, GhostPriority)>();

            var actions = GhostSoftCapManager.EvaluateCaps(10, 0, zone1, zone2);

            // 2 ghosts reduced: the LoopedOldest (idx=4) and one LoopedRecent (idx=3 or 8)
            Assert.Equal(2, actions.Count);
            Assert.Equal(GhostCapAction.ReduceFidelity, actions[4]); // LoopedOldest
            Assert.True(actions.ContainsKey(3) || actions.ContainsKey(8)); // one LoopedRecent
        }

        [Fact]
        public void EvaluateCaps_Zone1At9_ReducesOneLowestPriority()
        {
            // 9 ghosts, threshold 8 → 1 should get ReduceFidelity
            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.FullTimeline),
                (1, GhostPriority.LoopedOldest),
                (2, GhostPriority.FullTimeline),
                (3, GhostPriority.FullTimeline),
                (4, GhostPriority.FullTimeline),
                (5, GhostPriority.FullTimeline),
                (6, GhostPriority.FullTimeline),
                (7, GhostPriority.FullTimeline),
                (8, GhostPriority.FullTimeline),
            };
            var zone2 = new List<(int, GhostPriority)>();

            var actions = GhostSoftCapManager.EvaluateCaps(9, 0, zone1, zone2);

            Assert.Single(actions);
            Assert.Equal(GhostCapAction.ReduceFidelity, actions[1]); // LoopedOldest is lowest priority
        }

        #endregion

        #region EvaluateCaps — Zone 1 Despawning

        [Fact]
        public void EvaluateCaps_Zone1At16_DespawnsOneLowestPriority()
        {
            // 16 ghosts, despawn threshold 15 → 1 despawned
            var zone1 = new List<(int, GhostPriority)>();
            for (int i = 0; i < 15; i++)
                zone1.Add((i, GhostPriority.FullTimeline));
            zone1.Add((15, GhostPriority.LoopedOldest)); // lowest priority → despawned

            var actions = GhostSoftCapManager.EvaluateCaps(16, 0, zone1, new List<(int, GhostPriority)>());

            Assert.True(actions.ContainsKey(15));
            Assert.Equal(GhostCapAction.Despawn, actions[15]);
        }

        [Fact]
        public void EvaluateCaps_Zone1At16_AlsoReducesFidelityForOverflow()
        {
            // 16 ghosts, despawn 15 → 1 despawned, also reduce threshold 8 exceeded → some reduced
            var zone1 = new List<(int, GhostPriority)>();
            // 2 LoopedOldest, 5 LoopedRecent, 9 FullTimeline
            zone1.Add((0, GhostPriority.LoopedOldest));
            zone1.Add((1, GhostPriority.LoopedOldest));
            for (int i = 2; i < 7; i++)
                zone1.Add((i, GhostPriority.LoopedRecent));
            for (int i = 7; i < 16; i++)
                zone1.Add((i, GhostPriority.FullTimeline));

            var actions = GhostSoftCapManager.EvaluateCaps(16, 0, zone1, new List<(int, GhostPriority)>());

            // 1 despawned (lowest priority = LoopedOldest idx 0)
            Assert.Equal(GhostCapAction.Despawn, actions[0]);
            // Remaining slots up to (16-8)=8 should be ReduceFidelity
            // After despawning idx 0, sorted remaining start at idx 1 (LoopedOldest), then 2-6 (LoopedRecent), then 7-15 (FullTimeline)
            // Indices 1 through 7 (that's 7 more) should be ReduceFidelity
            Assert.Equal(GhostCapAction.ReduceFidelity, actions[1]);
        }

        [Fact]
        public void EvaluateCaps_PriorityOrdering_LoopedOldestDespawnedBeforeFullTimeline()
        {
            // Mix of priorities — LoopedOldest should be despawned first
            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.FullTimeline),
                (1, GhostPriority.LoopedOldest),
                (2, GhostPriority.BackgroundDebris),
                (3, GhostPriority.FullTimeline),
                (4, GhostPriority.LoopedOldest),
                (5, GhostPriority.FullTimeline),
                (6, GhostPriority.FullTimeline),
                (7, GhostPriority.FullTimeline),
                (8, GhostPriority.FullTimeline),
                (9, GhostPriority.FullTimeline),
                (10, GhostPriority.FullTimeline),
                (11, GhostPriority.FullTimeline),
                (12, GhostPriority.FullTimeline),
                (13, GhostPriority.FullTimeline),
                (14, GhostPriority.FullTimeline),
                (15, GhostPriority.FullTimeline),
            };
            var actions = GhostSoftCapManager.EvaluateCaps(16, 0, zone1, new List<(int, GhostPriority)>());

            // 1 despawned: the lowest priority — one of the LoopedOldest (idx 1 or 4)
            var despawned = actions.Where(kv => kv.Value == GhostCapAction.Despawn).ToList();
            Assert.Single(despawned);
            Assert.True(despawned[0].Key == 1 || despawned[0].Key == 4,
                $"Expected LoopedOldest to be despawned, got idx={despawned[0].Key}");

            // FullTimeline ghosts should NOT be despawned
            foreach (var kv in actions)
            {
                if (kv.Value == GhostCapAction.Despawn)
                {
                    var ghost = zone1.First(g => g.Item1 == kv.Key);
                    Assert.NotEqual(GhostPriority.FullTimeline, ghost.Item2);
                }
            }
        }

        #endregion

        #region EvaluateCaps — Zone 2 Simplification

        [Fact]
        public void EvaluateCaps_Zone2At25_SimplifiesAllZone2()
        {
            var zone1 = new List<(int, GhostPriority)>();
            var zone2 = MakeGhostList(25, GhostPriority.FullTimeline);

            var actions = GhostSoftCapManager.EvaluateCaps(0, 25, zone1, zone2);

            Assert.Equal(25, actions.Count);
            foreach (var kv in actions)
                Assert.Equal(GhostCapAction.SimplifyToOrbitLine, kv.Value);
        }

        [Fact]
        public void EvaluateCaps_Zone2At21_SimplifiesAllZone2()
        {
            var zone2 = MakeGhostList(21, GhostPriority.FullTimeline);
            var actions = GhostSoftCapManager.EvaluateCaps(0, 21, new List<(int, GhostPriority)>(), zone2);

            Assert.Equal(21, actions.Count);
            foreach (var kv in actions)
                Assert.Equal(GhostCapAction.SimplifyToOrbitLine, kv.Value);
        }

        [Fact]
        public void EvaluateCaps_Zone2AtExactly20_NoSimplification()
        {
            var zone2 = MakeGhostList(20, GhostPriority.FullTimeline);
            var actions = GhostSoftCapManager.EvaluateCaps(0, 20, new List<(int, GhostPriority)>(), zone2);
            Assert.Empty(actions);
        }

        #endregion

        #region EvaluateCaps — Combined Zones

        [Fact]
        public void EvaluateCaps_Zone1DespawnAndZone2Simplify_BothApply()
        {
            // Zone 1 = 16 (above despawn 15), Zone 2 = 25 (above simplify 20)
            var zone1 = new List<(int, GhostPriority)>();
            zone1.Add((100, GhostPriority.LoopedOldest));
            for (int i = 1; i < 16; i++)
                zone1.Add((100 + i, GhostPriority.FullTimeline));

            var zone2 = new List<(int, GhostPriority)>();
            for (int i = 0; i < 25; i++)
                zone2.Add((200 + i, GhostPriority.FullTimeline));

            var actions = GhostSoftCapManager.EvaluateCaps(16, 25, zone1, zone2);

            // Zone 1: idx 100 (LoopedOldest) should be despawned
            Assert.Equal(GhostCapAction.Despawn, actions[100]);
            // Zone 2: all should be simplified
            for (int i = 0; i < 25; i++)
                Assert.Equal(GhostCapAction.SimplifyToOrbitLine, actions[200 + i]);
        }

        #endregion

        #region EvaluateCaps — Custom Thresholds

        [Fact]
        public void EvaluateCaps_CustomReduceThreshold_TriggersAtLowerCount()
        {
            GhostSoftCapManager.Zone1ReduceThreshold = 2;
            var zone1 = MakeGhostList(3, GhostPriority.LoopedRecent);

            var actions = GhostSoftCapManager.EvaluateCaps(3, 0, zone1, new List<(int, GhostPriority)>());

            // 3 > 2 → 1 ghost reduced
            Assert.Single(actions);
            Assert.Equal(GhostCapAction.ReduceFidelity, actions.Values.First());
        }

        [Fact]
        public void EvaluateCaps_CustomDespawnThreshold_TriggersAtLowerCount()
        {
            GhostSoftCapManager.Zone1DespawnThreshold = 3;
            GhostSoftCapManager.Zone1ReduceThreshold = 2;
            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.LoopedOldest),
                (1, GhostPriority.LoopedRecent),
                (2, GhostPriority.FullTimeline),
                (3, GhostPriority.FullTimeline),
            };

            var actions = GhostSoftCapManager.EvaluateCaps(4, 0, zone1, new List<(int, GhostPriority)>());

            // 4 > 3 → 1 despawned (LoopedOldest, idx 0)
            Assert.Equal(GhostCapAction.Despawn, actions[0]);
        }

        [Fact]
        public void EvaluateCaps_CustomZone2Threshold_TriggersAtLowerCount()
        {
            GhostSoftCapManager.Zone2SimplifyThreshold = 5;
            var zone2 = MakeGhostList(6, GhostPriority.FullTimeline);

            var actions = GhostSoftCapManager.EvaluateCaps(0, 6, new List<(int, GhostPriority)>(), zone2);

            Assert.Equal(6, actions.Count);
            foreach (var kv in actions)
                Assert.Equal(GhostCapAction.SimplifyToOrbitLine, kv.Value);
        }

        #endregion

        #region EvaluateCaps — Logging

        [Fact]
        public void EvaluateCaps_Zone1ReduceExceeded_LogsThresholdExceeded()
        {
            var zone1 = MakeGhostList(10, GhostPriority.FullTimeline);
            GhostSoftCapManager.EvaluateCaps(10, 0, zone1, new List<(int, GhostPriority)>());
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("reduce threshold exceeded") &&
                l.Contains("count=10") && l.Contains("threshold=8"));
        }

        [Fact]
        public void EvaluateCaps_Zone1DespawnExceeded_LogsThresholdExceeded()
        {
            var zone1 = MakeGhostList(16, GhostPriority.LoopedOldest);
            GhostSoftCapManager.EvaluateCaps(16, 0, zone1, new List<(int, GhostPriority)>());
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("despawn threshold exceeded") &&
                l.Contains("count=16") && l.Contains("threshold=15"));
        }

        [Fact]
        public void EvaluateCaps_Zone2SimplifyExceeded_LogsThresholdExceeded()
        {
            var zone2 = MakeGhostList(21, GhostPriority.FullTimeline);
            GhostSoftCapManager.EvaluateCaps(0, 21, new List<(int, GhostPriority)>(), zone2);
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("Zone 2 simplify threshold exceeded") &&
                l.Contains("count=21") && l.Contains("threshold=20"));
        }

        [Fact]
        public void EvaluateCaps_LogsPerGhostAction()
        {
            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.LoopedOldest),
                (1, GhostPriority.FullTimeline),
                (2, GhostPriority.FullTimeline),
                (3, GhostPriority.FullTimeline),
                (4, GhostPriority.FullTimeline),
                (5, GhostPriority.FullTimeline),
                (6, GhostPriority.FullTimeline),
                (7, GhostPriority.FullTimeline),
                (8, GhostPriority.FullTimeline),
            };
            GhostSoftCapManager.EvaluateCaps(9, 0, zone1, new List<(int, GhostPriority)>());
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("idx=0") &&
                l.Contains("ReduceFidelity"));
        }

        #endregion

        #region ResetThresholds

        [Fact]
        public void ResetThresholds_RestoresDefaults()
        {
            GhostSoftCapManager.Zone1ReduceThreshold = 99;
            GhostSoftCapManager.Zone1DespawnThreshold = 99;
            GhostSoftCapManager.Zone2SimplifyThreshold = 99;

            GhostSoftCapManager.ResetThresholds();

            Assert.Equal(8, GhostSoftCapManager.Zone1ReduceThreshold);
            Assert.Equal(15, GhostSoftCapManager.Zone1DespawnThreshold);
            Assert.Equal(20, GhostSoftCapManager.Zone2SimplifyThreshold);
        }

        [Fact]
        public void ResetThresholds_AfterCustom_BehavesAsDefault()
        {
            GhostSoftCapManager.Zone1ReduceThreshold = 1;
            GhostSoftCapManager.ResetThresholds();

            // With default threshold (8), 5 ghosts should not trigger any action
            var zone1 = MakeGhostList(5, GhostPriority.FullTimeline);
            var actions = GhostSoftCapManager.EvaluateCaps(5, 0, zone1, new List<(int, GhostPriority)>());
            Assert.Empty(actions);
        }

        #endregion

        #region Helpers

        private List<(int recordingIndex, GhostPriority priority)> MakeGhostList(
            int count, GhostPriority priority)
        {
            var list = new List<(int, GhostPriority)>();
            for (int i = 0; i < count; i++)
                list.Add((i, priority));
            return list;
        }

        #endregion
    }
}
