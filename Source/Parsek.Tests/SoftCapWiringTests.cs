using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SoftCapWiringTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SoftCapWiringTests()
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

        #region ApplySettings — Threshold Flow

        [Fact]
        public void ApplySettings_SetsAllThresholds()
        {
            GhostSoftCapManager.ApplySettings(4, 10, 12);

            Assert.Equal(4, GhostSoftCapManager.Zone1ReduceThreshold);
            Assert.Equal(10, GhostSoftCapManager.Zone1DespawnThreshold);
            Assert.Equal(12, GhostSoftCapManager.Zone2SimplifyThreshold);
        }

        [Fact]
        public void ApplySettings_ThresholdsAffectEvaluateCaps()
        {
            // Set low thresholds so 3 zone1 ghosts triggers reduce
            GhostSoftCapManager.ApplySettings(2, 10, 5);

            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.LoopedOldest),
                (1, GhostPriority.FullTimeline),
                (2, GhostPriority.FullTimeline),
            };
            var zone2 = new List<(int, GhostPriority)>();

            var actions = GhostSoftCapManager.EvaluateCaps(3, 0, zone1, zone2);

            // 3 > 2 threshold → 1 ghost reduced (lowest priority = LoopedOldest at idx 0)
            Assert.Single(actions);
            Assert.Equal(GhostCapAction.ReduceFidelity, actions[0]);
        }

        [Fact]
        public void ApplySettings_HighThresholds_NoActions()
        {
            // Set very high thresholds — no caps should trigger
            GhostSoftCapManager.ApplySettings(100, 200, 300);

            var zone1 = new List<(int, GhostPriority)>();
            for (int i = 0; i < 20; i++)
                zone1.Add((i, GhostPriority.FullTimeline));

            var zone2 = new List<(int, GhostPriority)>();
            for (int i = 20; i < 40; i++)
                zone2.Add((i, GhostPriority.FullTimeline));

            var actions = GhostSoftCapManager.EvaluateCaps(20, 20, zone1, zone2);
            Assert.Empty(actions);
        }

        [Fact]
        public void ApplySettings_LogsThresholdValues()
        {
            GhostSoftCapManager.ApplySettings(5, 12, 18);

            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") &&
                l.Contains("Thresholds applied") &&
                l.Contains("zone1Reduce=5") &&
                l.Contains("zone1Despawn=12") &&
                l.Contains("zone2Simplify=18"));
        }

        [Fact]
        public void ApplySettings_ThenReset_RestoresDefaults()
        {
            GhostSoftCapManager.ApplySettings(1, 2, 3);
            GhostSoftCapManager.ResetThresholds();

            Assert.Equal(8, GhostSoftCapManager.Zone1ReduceThreshold);
            Assert.Equal(15, GhostSoftCapManager.Zone1DespawnThreshold);
            Assert.Equal(20, GhostSoftCapManager.Zone2SimplifyThreshold);
        }

        #endregion

        #region Cap Actions — Despawn Path

        [Fact]
        public void EvaluateCaps_DespawnAction_TargetsLowestPriority()
        {
            // Simulate a scenario where despawn threshold is exceeded
            GhostSoftCapManager.ApplySettings(3, 5, 20);

            var zone1 = new List<(int, GhostPriority)>
            {
                (10, GhostPriority.FullTimeline),
                (11, GhostPriority.FullTimeline),
                (12, GhostPriority.LoopedOldest),  // lowest priority — despawned
                (13, GhostPriority.BackgroundDebris),
                (14, GhostPriority.FullTimeline),
                (15, GhostPriority.FullTimeline),
            };
            var actions = GhostSoftCapManager.EvaluateCaps(6, 0, zone1, new List<(int, GhostPriority)>());

            // 6 > 5 → 1 despawned: idx 12 (LoopedOldest)
            Assert.Equal(GhostCapAction.Despawn, actions[12]);
        }

        [Fact]
        public void EvaluateCaps_DespawnAction_LogsPerGhost()
        {
            GhostSoftCapManager.ApplySettings(2, 3, 20);

            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.LoopedOldest),
                (1, GhostPriority.FullTimeline),
                (2, GhostPriority.FullTimeline),
                (3, GhostPriority.FullTimeline),
            };
            GhostSoftCapManager.EvaluateCaps(4, 0, zone1, new List<(int, GhostPriority)>());

            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") &&
                l.Contains("despawn threshold exceeded") &&
                l.Contains("count=4") &&
                l.Contains("threshold=3"));
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") &&
                l.Contains("idx=0") &&
                l.Contains("Despawn"));
        }

        #endregion

        #region Cap Actions — Simplify Path

        [Fact]
        public void EvaluateCaps_SimplifyAction_AffectsAllZone2Ghosts()
        {
            GhostSoftCapManager.ApplySettings(8, 15, 3);

            var zone2 = new List<(int, GhostPriority)>
            {
                (20, GhostPriority.FullTimeline),
                (21, GhostPriority.BackgroundDebris),
                (22, GhostPriority.LoopedRecent),
                (23, GhostPriority.FullTimeline),
            };
            var actions = GhostSoftCapManager.EvaluateCaps(
                0, 4, new List<(int, GhostPriority)>(), zone2);

            // 4 > 3 → all 4 simplified
            Assert.Equal(4, actions.Count);
            foreach (var kv in actions)
                Assert.Equal(GhostCapAction.SimplifyToOrbitLine, kv.Value);
        }

        [Fact]
        public void EvaluateCaps_SimplifyAction_LogsThresholdExceeded()
        {
            GhostSoftCapManager.ApplySettings(8, 15, 2);

            var zone2 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.FullTimeline),
                (1, GhostPriority.FullTimeline),
                (2, GhostPriority.FullTimeline),
            };
            GhostSoftCapManager.EvaluateCaps(0, 3, new List<(int, GhostPriority)>(), zone2);

            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") &&
                l.Contains("Zone 2 simplify threshold exceeded") &&
                l.Contains("count=3") &&
                l.Contains("threshold=2"));
        }

        #endregion

        #region Cap Actions — Combined Zones

        [Fact]
        public void EvaluateCaps_BothZonesExceedThresholds_BothApply()
        {
            GhostSoftCapManager.ApplySettings(2, 4, 3);

            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.LoopedOldest),
                (1, GhostPriority.LoopedRecent),
                (2, GhostPriority.FullTimeline),
                (3, GhostPriority.FullTimeline),
                (4, GhostPriority.FullTimeline),
            };
            var zone2 = new List<(int, GhostPriority)>
            {
                (10, GhostPriority.FullTimeline),
                (11, GhostPriority.FullTimeline),
                (12, GhostPriority.FullTimeline),
                (13, GhostPriority.FullTimeline),
            };

            var actions = GhostSoftCapManager.EvaluateCaps(5, 4, zone1, zone2);

            // Zone 1: 5 > 4 despawn threshold → 1 despawned (idx 0, LoopedOldest)
            Assert.Equal(GhostCapAction.Despawn, actions[0]);
            // Zone 2: 4 > 3 simplify threshold → all 4 simplified
            Assert.Equal(GhostCapAction.SimplifyToOrbitLine, actions[10]);
            Assert.Equal(GhostCapAction.SimplifyToOrbitLine, actions[11]);
            Assert.Equal(GhostCapAction.SimplifyToOrbitLine, actions[12]);
            Assert.Equal(GhostCapAction.SimplifyToOrbitLine, actions[13]);
        }

        #endregion

        #region Backend Bootstrap — Internal Defaults

        [Fact]
        public void ApplyAutomaticDefaults_EnablesManager_AndRestoresThresholds()
        {
            GhostSoftCapManager.Enabled = false;
            GhostSoftCapManager.ApplySettings(5, 10, 15);

            GhostSoftCapManager.ApplyAutomaticDefaults();

            Assert.True(GhostSoftCapManager.Enabled);
            Assert.Equal(GhostSoftCapManager.DefaultZone1ReduceThreshold, GhostSoftCapManager.Zone1ReduceThreshold);
            Assert.Equal(GhostSoftCapManager.DefaultZone1DespawnThreshold, GhostSoftCapManager.Zone1DespawnThreshold);
            Assert.Equal(GhostSoftCapManager.DefaultZone2SimplifyThreshold, GhostSoftCapManager.Zone2SimplifyThreshold);
        }

        [Fact]
        public void ApplyAutomaticDefaults_LogsBackendOwnedDefaults()
        {
            GhostSoftCapManager.ApplyAutomaticDefaults();

            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") &&
                l.Contains("Automatic defaults applied") &&
                l.Contains("enabled=True") &&
                l.Contains($"zone1Reduce={GhostSoftCapManager.DefaultZone1ReduceThreshold}") &&
                l.Contains($"zone1Despawn={GhostSoftCapManager.DefaultZone1DespawnThreshold}") &&
                l.Contains($"zone2Simplify={GhostSoftCapManager.DefaultZone2SimplifyThreshold}"));
        }

        [Fact]
        public void ParsekSettings_DoesNotExposeGhostCapFields()
        {
            var settingsType = typeof(ParsekSettings);

            Assert.Null(settingsType.GetField("ghostCapEnabled"));
            Assert.Null(settingsType.GetField("ghostCapZone1Reduce"));
            Assert.Null(settingsType.GetField("ghostCapZone1Despawn"));
            Assert.Null(settingsType.GetField("ghostCapZone2Simplify"));
        }

        #endregion

        #region Logging — Rate-Limited Cap Trigger

        [Fact]
        public void ApplySettings_MultipleCallsAllLog()
        {
            // Each call to ApplySettings should log
            GhostSoftCapManager.ApplySettings(5, 10, 15);
            GhostSoftCapManager.ApplySettings(6, 11, 16);

            var applyLogs = logLines.Where(l =>
                l.Contains("[SoftCap]") && l.Contains("Thresholds applied")).ToList();
            Assert.Equal(2, applyLogs.Count);
        }

        [Fact]
        public void EvaluateCaps_Zone1Despawn_LogsDespawnThresholdAndGhostActions()
        {
            GhostSoftCapManager.ApplySettings(2, 3, 20);

            var zone1 = new List<(int, GhostPriority)>
            {
                (0, GhostPriority.LoopedOldest),
                (1, GhostPriority.LoopedRecent),
                (2, GhostPriority.BackgroundDebris),
                (3, GhostPriority.FullTimeline),
            };

            GhostSoftCapManager.EvaluateCaps(4, 0, zone1, new List<(int, GhostPriority)>());

            // Should log threshold exceeded
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("despawn threshold exceeded"));

            // Should log per-ghost action for the despawned ghost
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("Despawn") && l.Contains("idx=0"));
        }

        [Fact]
        public void EvaluateCaps_Zone1Reduce_LogsReduceThresholdAndGhostActions()
        {
            var zone1 = new List<(int, GhostPriority)>();
            for (int i = 0; i < 10; i++)
                zone1.Add((i, i == 0 ? GhostPriority.LoopedOldest : GhostPriority.FullTimeline));

            GhostSoftCapManager.EvaluateCaps(10, 0, zone1, new List<(int, GhostPriority)>());

            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("reduce threshold exceeded"));
            Assert.Contains(logLines, l =>
                l.Contains("[SoftCap]") && l.Contains("ReduceFidelity") && l.Contains("idx=0"));
        }

        #endregion
    }
}
