using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo LOOP-REPLAY-REPLANTS-REMOVED-FLAG (replay-theory audit R5): a flag the player removed
    /// is never planted again by a replay walk (loop cycle, overlap, fresh state, deferred flush),
    /// and a load from before the removal forgets it.
    /// </summary>
    [Collection("Sequential")]
    public class RemovedReplayFlagsTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> spawned = new List<string>();
        private readonly HashSet<string> standing = new HashSet<string>();

        public RemovedReplayFlagsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            GhostPlaybackLogic.ResetForTesting();
            RemovedReplayFlags.ResetForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // The world: a flag "stands" once spawned, until the test removes it.
            GhostPlaybackLogic.SetFlagExistsOverrideForTesting(evt => standing.Contains(evt.flagSiteName));
            GhostPlaybackLogic.SetSpawnFlagOverrideForTesting(evt =>
            {
                spawned.Add(evt.flagSiteName);
                standing.Add(evt.flagSiteName);
                return true;
            });
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetForTesting();
            RemovedReplayFlags.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static FlagEvent Flag(string name, double ut, double lat = -0.0972, double lon = -74.5577, double alt = 70.0)
        {
            return new FlagEvent
            {
                ut = ut, flagSiteName = name, placedBy = "Jebediah Kerman", bodyName = "Kerbin",
                latitude = lat, longitude = lon, altitude = alt,
            };
        }

        private static Recording LoopRecording(params FlagEvent[] flags)
        {
            return new Recording
            {
                RecordingId = "rec-loop-flag",
                VesselName = "Flag Planter",
                FlagEvents = new List<FlagEvent>(flags),
            };
        }

        private static RemovedReplayFlags.Tombstone RemovalOf(FlagEvent evt, double removedUT, string source = "test")
        {
            return new RemovedReplayFlags.Tombstone
            {
                BodyName = evt.bodyName,
                BodyRadius = KerbinRadius,
                Latitude = evt.latitude,
                Longitude = evt.longitude,
                Altitude = evt.altitude,
                RemovedUT = removedUT,
                FlagName = evt.flagSiteName,
                Source = source,
            };
        }

        private void RemoveFromWorld(FlagEvent evt, double removedUT)
        {
            standing.Remove(evt.flagSiteName);
            Assert.True(RemovedReplayFlags.RecordRemoval(RemovalOf(evt, removedUT)));
        }

        // ---- The R5 defect, end to end over the cursor walk ----

        [Fact]
        public void LoopCycle_AfterPlayerRemovesFlag_DoesNotPlantItAgain()
        {
            FlagEvent f = Flag("Mun Base", 120.0);
            Recording rec = LoopRecording(f);
            var state = new GhostPlaybackState();

            // Cycle 0: the replay reaches the plant and plants the flag.
            GhostPlaybackLogic.ApplyFlagEvents(state, rec, 130.0);
            Assert.Equal(new[] { "Mun Base" }, spawned);

            // Cycle 1 while the flag still stands: dedup, nothing planted.
            GhostPlaybackLogic.ResetForLoopCycle(state, 1);
            GhostPlaybackLogic.ApplyFlagEvents(state, rec, 130.0);
            Assert.Single(spawned);

            // The player removes it; cycles 2 and 3 must not plant it again.
            RemoveFromWorld(f, 500.0);
            for (long cycle = 2; cycle <= 3; cycle++)
            {
                GhostPlaybackLogic.ResetForLoopCycle(state, cycle);
                GhostPlaybackLogic.ApplyFlagEvents(state, rec, 130.0);
            }
            Assert.Single(spawned);
            Assert.Contains(logLines, l => l.Contains("[" + RemovedReplayFlags.Tag + "]")
                && l.Contains("Flag replay skipped: 'Mun Base'") && l.Contains("removed by the player"));
        }

        [Fact]
        public void WithoutTheTombstone_TheNextCycleReplants_TheDefectThisGuards()
        {
            // Pins the mechanism the tombstone exists for: the world dedup alone replants.
            FlagEvent f = Flag("Mun Base", 120.0);
            Recording rec = LoopRecording(f);
            var state = new GhostPlaybackState();
            GhostPlaybackLogic.ApplyFlagEvents(state, rec, 130.0);
            standing.Remove(f.flagSiteName);
            GhostPlaybackLogic.ResetForLoopCycle(state, 1);
            GhostPlaybackLogic.ApplyFlagEvents(state, rec, 130.0);
            Assert.Equal(2, spawned.Count);
        }

        [Fact]
        public void FreshStateAfterSceneChange_DoesNotPlantARemovedFlag()
        {
            FlagEvent f = Flag("Minmus Arch", 50.0);
            Recording rec = LoopRecording(f);
            RemoveFromWorld(f, 60.0);

            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), rec, 200.0);
            GhostPlaybackLogic.ApplyFlagEvents(null, rec, 200.0); // state-less fallback walk
            Assert.Empty(spawned);
        }

        [Fact]
        public void DeferredFlush_CountsARemovedFlagApart_NotAsAFailure()
        {
            FlagEvent removed = Flag("removed", 100.0);
            FlagEvent missing = Flag("missing", 110.0, lat: 10.0);
            Recording rec = LoopRecording(removed, missing, Flag("future", 130.0, lat: 20.0));
            RemoveFromWorld(removed, 105.0);

            var result = GhostPlaybackLogic.SpawnFlagVesselsUpToUT(rec, 120.0);

            Assert.Equal(2, result.eligibleCount);
            Assert.Equal(1, result.spawnedCount);
            Assert.Equal(0, result.alreadyPresentCount);
            Assert.Equal(0, result.failedCount); // a failure would retry the flush every frame
            Assert.Equal(1, result.removedByPlayerCount);
            Assert.Equal(new[] { "missing" }, spawned);
        }

        [Fact]
        public void OtherFlagsOfTheSameRecording_StillPlant()
        {
            FlagEvent a = Flag("A", 100.0);
            FlagEvent b = Flag("B", 110.0, lat: 5.0);
            RemoveFromWorld(a, 105.0);
            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), LoopRecording(a, b), 200.0);
            Assert.Equal(new[] { "B" }, spawned);
        }

        // ---- Matching ----

        [Fact]
        public void Matches_SameSpotPlantedBeforeTheRemoval()
        {
            FlagEvent f = Flag("x", 100.0);
            Assert.True(RemovedReplayFlags.Matches(RemovalOf(f, 100.0), f));
            Assert.True(RemovedReplayFlags.Matches(RemovalOf(f, 400.0), f));
        }

        [Fact]
        public void Matches_AFlagPlantedThereAfterTheRemoval_IsANewFlag()
        {
            FlagEvent f = Flag("x", 500.0);
            Assert.False(RemovedReplayFlags.Matches(RemovalOf(f, 400.0), f));
        }

        [Fact]
        public void Matches_OtherBody_DoesNotMatch()
        {
            FlagEvent f = Flag("x", 100.0);
            var t = RemovalOf(f, 200.0);
            t.BodyName = "Mun";
            Assert.False(RemovedReplayFlags.Matches(t, f));
        }

        [Fact]
        public void Matches_ToleranceIsThreeMetres()
        {
            FlagEvent f = Flag("x", 100.0, lat: 0.0, lon: 0.0, alt: 0.0);
            // One degree of latitude at the equator is R * pi / 180 metres.
            double metresPerDegree = KerbinRadius * Math.PI / 180.0;
            var near = RemovalOf(f, 200.0);
            near.Latitude = 2.5 / metresPerDegree;
            var far = RemovalOf(f, 200.0);
            far.Latitude = 3.5 / metresPerDegree;
            Assert.True(RemovedReplayFlags.Matches(near, f));
            Assert.False(RemovedReplayFlags.Matches(far, f));
        }

        [Fact]
        public void SurfaceDistance_HighLatitude_UsesTrue3DDistance()
        {
            // Near the pole a large longitude difference is a short distance.
            double d = RemovedReplayFlags.SurfaceDistanceMeters(89.99999, 0.0, 0.0, 89.99999, 180.0, 0.0, KerbinRadius);
            Assert.InRange(d, 0.1, 0.3);
            Assert.Equal(10.0, RemovedReplayFlags.SurfaceDistanceMeters(0, 0, 0, 0, 0, 10, KerbinRadius), 6);
        }

        // ---- Recording a removal ----

        [Fact]
        public void RecordRemoval_SecondReportOfOneRemoval_IsIgnored()
        {
            FlagEvent f = Flag("x", 100.0);
            Assert.True(RemovedReplayFlags.RecordRemoval(RemovalOf(f, 300.0, "recovered")));
            Assert.False(RemovedReplayFlags.RecordRemoval(RemovalOf(f, 301.0, "terminated")));
            Assert.Equal(1, RemovedReplayFlags.Count);
            Assert.Contains(logLines, l => l.Contains("Flag removed by the player: 'x' on Kerbin") && l.Contains("source=recovered"));
            Assert.Contains(logLines, l => l.Contains("Flag removal already recorded") && l.Contains("source=terminated"));
        }

        [Fact]
        public void RecordRemoval_UnusableReport_IsRejected()
        {
            var t = RemovalOf(Flag("x", 100.0), 300.0);
            t.BodyRadius = double.NaN;
            Assert.False(RemovedReplayFlags.RecordRemoval(t));
            var u = RemovalOf(Flag("x", 100.0), 300.0);
            u.BodyName = null;
            Assert.False(RemovedReplayFlags.RecordRemoval(u));
            Assert.Equal(0, RemovedReplayFlags.Count);
        }

        [Theory]
        [InlineData(true, false, false, true)]
        [InlineData(false, false, false, false)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, true, false)]
        public void ShouldRecordRemoval_OnlyAPlayerFlagOutsideARewind(bool isFlag, bool ghost, bool rewinding, bool expected)
        {
            Assert.Equal(expected, RemovedReplayFlags.ShouldRecordRemoval(isFlag, ghost, rewinding));
        }

        // ---- Time travel ----

        [Fact]
        public void LoadBeforeTheRemoval_ForgetsIt_AndReplayPlantsTheFlagAgain()
        {
            FlagEvent f = Flag("Mun Base", 120.0);
            Recording rec = LoopRecording(f);
            RemoveFromWorld(f, 500.0);
            RemoveFromWorld(Flag("older", 10.0, lat: 30.0), 50.0);

            // Rewind to UT 100: before the plant and before the removal.
            Assert.Equal(1, RemovedReplayFlags.DropRemovedAfter(100.0, "test rewind"));
            Assert.Equal(1, RemovedReplayFlags.Count);
            Assert.Contains(logLines, l => l.Contains("Removed-flag tombstones after the loaded UT dropped") && l.Contains("dropped=1"));

            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), rec, 130.0);
            Assert.Equal(new[] { "Mun Base" }, spawned);
        }

        [Fact]
        public void DropRemovedAfter_UnknownUT_KeepsEverything()
        {
            RemoveFromWorld(Flag("x", 10.0), 50.0);
            Assert.Equal(0, RemovedReplayFlags.DropRemovedAfter(double.NaN, "test"));
            Assert.Equal(1, RemovedReplayFlags.Count);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("not reconciled"));
        }

        [Fact]
        public void SaveLoad_RoundTrips_UnderACommaCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                FlagEvent f = Flag("Mun Base", 120.5, lat: -0.0972123, lon: -74.5577456, alt: 70.25);
                RemoveFromWorld(f, 500.125);
                var node = new ConfigNode("PARSEK");
                Assert.Equal(1, RemovedReplayFlags.SaveInto(node));
                Assert.Equal("500.125", node.GetNode(RemovedReplayFlags.NodeName)
                    .GetNode(RemovedReplayFlags.EntryNodeName).GetValue("removedUT"));

                List<RemovedReplayFlags.Tombstone> back = RemovedReplayFlags.LoadFrom(node);
                Assert.Single(back);
                Assert.Equal("Kerbin", back[0].BodyName);
                Assert.Equal(-0.0972123, back[0].Latitude);
                Assert.Equal(500.125, back[0].RemovedUT);
                Assert.Equal("Mun Base", back[0].FlagName);
                Assert.True(RemovedReplayFlags.Matches(back[0], f));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void SaveInto_EmptySet_WritesNoNode_AndClearsAStaleOne()
        {
            var node = new ConfigNode("PARSEK");
            node.AddNode(RemovedReplayFlags.NodeName);
            Assert.Equal(0, RemovedReplayFlags.SaveInto(node));
            Assert.False(node.HasNode(RemovedReplayFlags.NodeName));
        }

        [Fact]
        public void LoadFrom_SkipsAnUnreadableEntry()
        {
            var node = new ConfigNode("PARSEK");
            ConfigNode root = node.AddNode(RemovedReplayFlags.NodeName);
            ConfigNode bad = root.AddNode(RemovedReplayFlags.EntryNodeName);
            bad.AddValue("body", "Kerbin");
            bad.AddValue("lat", "nope");
            Assert.Empty(RemovedReplayFlags.LoadFrom(node));
            Assert.Contains(logLines, l => l.Contains("skipped 1 unreadable entry"));
        }

        // ---- Load policy ----

        [Fact]
        public void Policy_ColdTakesTheSave_EveryOtherLoadReconcilesAtTheCutoff()
        {
            Assert.True(LoadReconcilePolicy.IsConsumedEarly(LoadStateCategory.RemovedReplayFlags));
            foreach (LoadKind kind in Enum.GetValues(typeof(LoadKind)))
            {
                LoadReconcileDecision d = LoadReconcilePolicy.Decide(kind, LoadStateCategory.RemovedReplayFlags);
                Assert.Equal(kind == LoadKind.Cold ? LoadReconcileAction.Save : LoadReconcileAction.ReconcileAtCutoff,
                    d.Action);
                Assert.Null(d.KnownGapTodoId);
            }
            Assert.Equal(LoadReconcileAction.ReconcileAtCutoff,
                LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, LoadStateCategory.RemovedReplayFlags).Action);
        }
    }
}
