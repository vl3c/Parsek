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
                plantedAt[evt.flagSiteName] = evt;
                return true;
            });
            GhostPlaybackLogic.SetBodyRadiusOverrideForTesting(body => body == "Kerbin" ? KerbinRadius : double.NaN);
            // A replayed pick-up takes down every standing flag within the tolerance.
            GhostPlaybackLogic.SetRemoveStandingFlagOverrideForTesting(t =>
            {
                int n = 0;
                foreach (string name in new List<string>(standing))
                {
                    if (!plantedAt.TryGetValue(name, out FlagEvent at)) continue;
                    double d = RemovedReplayFlags.SurfaceDistanceMeters(t.Latitude, t.Longitude, t.Altitude,
                        at.latitude, at.longitude, at.altitude, t.BodyRadius);
                    if (d <= RemovedReplayFlags.MatchToleranceMeters) { standing.Remove(name); takenDown.Add(name); n++; }
                }
                // Stock Vessel.Die() fires onVesselWillDestroy synchronously, so the removal
                // handler runs here, at the walk's (later) UT, exactly as in game.
                if (n > 0 && RemovedReplayFlags.ShouldRecordRemoval(true, false, false,
                        RemovedReplayFlags.ParsekRemovalInProgress))
                {
                    var echo = t;
                    echo.RemovedUT = worldNow;
                    echo.Source = "destroyed-in-flight";
                    RemovedReplayFlags.RecordRemoval(echo);
                }
                return n;
            });
            FlagPickUp.ResetForTesting();
        }

        private readonly Dictionary<string, FlagEvent> plantedAt = new Dictionary<string, FlagEvent>();
        private double worldNow = 1000.0;
        private readonly List<string> takenDown = new List<string>();

        public void Dispose()
        {
            GhostPlaybackLogic.ResetForTesting();
            RemovedReplayFlags.ResetForTesting();
            FlagPickUp.ResetForTesting();
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
        [InlineData(true, false, false, false, true)]
        [InlineData(false, false, false, false, false)]
        [InlineData(true, true, false, false, false)]
        [InlineData(true, false, true, false, false)]
        [InlineData(true, false, false, true, false)]
        public void ShouldRecordRemoval_OnlyAPlayerFlagOutsideARewind(bool isFlag, bool ghost, bool rewinding, bool parsek, bool expected)
        {
            Assert.Equal(expected, RemovedReplayFlags.ShouldRecordRemoval(isFlag, ghost, rewinding, parsek));
        }

        [Fact]
        public void ParsekRemovalScope_IsNestableAndEndsWithTheScope()
        {
            Assert.False(RemovedReplayFlags.ParsekRemovalInProgress);
            using (RemovedReplayFlags.BeginParsekRemoval())
            {
                using (RemovedReplayFlags.BeginParsekRemoval())
                    Assert.True(RemovedReplayFlags.ParsekRemovalInProgress);
                Assert.True(RemovedReplayFlags.ParsekRemovalInProgress);
            }
            Assert.False(RemovedReplayFlags.ParsekRemovalInProgress);
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

        // ---- Recorded pick-ups (owner ruling 2026-11-04) ----

        private static Recording WithPickUp(string id, double ut, FlagEvent at, params FlagEvent[] plants)
        {
            var rec = new Recording { RecordingId = id, VesselName = "Jebediah Kerman", FlagEvents = new List<FlagEvent>(plants) };
            rec.SegmentEvents.Add(FlagPickUp.BuildEvent(ut, at.bodyName, at.latitude, at.longitude, at.altitude, at.flagSiteName));
            return rec;
        }

        [Fact]
        public void PickUpDetails_RoundTrip_WithSeparatorsInTheName_UnderACommaCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string d = FlagPickUp.FormatDetails("Mun", -1.25, 33.5, 1234.75, "Base;one=two");
                Assert.DoesNotContain(",", d.Replace("name=Base;one=two", ""));
                Assert.True(FlagPickUp.TryParseDetails(d, out string body, out double lat, out double lon, out double alt, out string name));
                Assert.Equal("Mun", body);
                Assert.Equal(-1.25, lat);
                Assert.Equal(33.5, lon);
                Assert.Equal(1234.75, alt);
                Assert.Equal("Base;one=two", name);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("body=Kerbin;lat=1;lon=2")]
        [InlineData("body=;lat=1;lon=2;alt=3;name=x")]
        [InlineData("body=Kerbin;lat=NaN;lon=2;alt=3;name=x")]
        public void PickUpDetails_MissingOrBadField_DoesNotParse(string details)
        {
            Assert.False(FlagPickUp.TryParseDetails(details, out _, out _, out _, out _, out _));
        }

        [Fact]
        public void IsPendingPickUp_OnlyTheMarkedFlag()
        {
            Assert.False(FlagPickUp.IsPendingPickUp(42));
            FlagPickUp.PendingFlagPid = 42; FlagPickUp.PendingPickerPid = 7;
            Assert.True(FlagPickUp.IsPendingPickUp(42));
            Assert.False(FlagPickUp.IsPendingPickUp(43));
            Assert.False(FlagPickUp.IsPendingPickUp(0));
        }

        [Fact]
        public void PickUpReplay_AtItsUT_TakesTheFlagDownOnce()
        {
            FlagEvent f = Flag("Mun Base", 100.0);
            Recording planter = LoopRecording(f);
            Recording picker = WithPickUp("rec-picker", 200.0, f);

            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), planter, 150.0);
            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), picker, 190.0);
            Assert.Contains("Mun Base", standing);

            Assert.Equal(1, GhostPlaybackLogic.ApplyFlagPickUps(picker, 200.0));
            Assert.DoesNotContain("Mun Base", standing);
            Assert.Equal(new[] { "Mun Base" }, takenDown);
            Assert.Contains(logLines, l => l.Contains("Flag pick-up replayed: 'Mun Base' on Kerbin")
                && l.Contains("rec=rec-picker") && l.Contains("took down 1"));

            // Later walks (a loop cycle of either recording) change nothing.
            Assert.Equal(0, GhostPlaybackLogic.ApplyFlagPickUps(picker, 200.0));
            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), planter, 150.0);
            Assert.Single(spawned);
            Assert.Single(takenDown);
        }

        [Fact]
        public void MovedFlag_ReplaysAfterARewind_EndsInOnePlace()
        {
            // Flight 1 plants at P1; flight 2's kerbal picks it up and plants it at P2.
            FlagEvent p1 = Flag("Moved", 100.0, lat: 1.0);
            FlagEvent p2 = Flag("Moved again", 210.0, lat: 2.0);
            Recording flight1 = LoopRecording(p1);
            Recording flight2 = WithPickUp("rec-flight2", 200.0, p1, p2);

            // After the rewind the replays cross each moment in order.
            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), flight1, 150.0);
            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), flight2, 205.0);
            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), flight2, 220.0);
            // Flight 1 loops again.
            var loop = new GhostPlaybackState();
            GhostPlaybackLogic.ApplyFlagEvents(loop, flight1, 150.0);
            GhostPlaybackLogic.ResetForLoopCycle(loop, 1);
            GhostPlaybackLogic.ApplyFlagEvents(loop, flight1, 150.0);

            Assert.Equal(new[] { "Moved again" }, standing);
            Assert.Equal(new[] { "Moved", "Moved again" }, spawned);
        }

        [Fact]
        public void LateReplayedPickUp_ThenReplantAtTheSameSpot_TheReplantStands()
        {
            // Plant at S, pick it up at 100, plant it again at S at 110. After a rewind to 70
            // the flag stands; the first flight walk lands at 1000, long past both moments.
            // Parsek's own take-down must not read as a player removal at 1000, which would
            // block the re-plant at 110 forever.
            FlagEvent first = Flag("Here", 50.0);
            FlagEvent again = Flag("Here again", 110.0);
            standing.Add(first.flagSiteName);
            plantedAt[first.flagSiteName] = first;
            Recording rec = WithPickUp("rec-replant", 100.0, first, again);
            worldNow = 1000.0;

            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), rec, 1000.0);

            Assert.Equal(new[] { "Here" }, takenDown);
            Assert.Equal(new[] { "Here again" }, standing);
            Assert.Equal(1, RemovedReplayFlags.Count);
            Assert.False(RemovedReplayFlags.ParsekRemovalInProgress);
        }

        [Fact]
        public void CatchUpWalkPastPlantAndPickUp_NeverPlantsTheFlag()
        {
            // One recording plants and picks up its own flag; a walk that starts past both
            // (a scene entered after them) plants nothing.
            FlagEvent f = Flag("Short stay", 100.0);
            Recording rec = WithPickUp("rec-one", 110.0, f, f);
            GhostPlaybackLogic.ApplyFlagEvents(new GhostPlaybackState(), rec, 300.0);
            Assert.Empty(spawned);
            Assert.Empty(takenDown);
        }

        [Fact]
        public void LivePickUpAlreadyRecorded_ReplayIsANoOp()
        {
            FlagEvent f = Flag("Live", 100.0);
            Recording picker = WithPickUp("rec-live", 200.0, f);
            RemoveFromWorld(f, 200.0); // the live capture's own tombstone, same UT
            Assert.Equal(0, GhostPlaybackLogic.ApplyFlagPickUps(picker, 250.0));
            Assert.Equal(1, RemovedReplayFlags.Count);
        }

        [Fact]
        public void PickUpReplay_BeforeItsUT_DoesNothing_AndUnknownBodyIsSkipped()
        {
            FlagEvent f = Flag("x", 100.0);
            Recording picker = WithPickUp("rec-early", 200.0, f);
            Assert.Equal(0, GhostPlaybackLogic.ApplyFlagPickUps(picker, 199.0));
            FlagEvent onEve = f; onEve.bodyName = "Eve";
            Recording elsewhere = WithPickUp("rec-eve", 200.0, onEve);
            Assert.Equal(0, GhostPlaybackLogic.ApplyFlagPickUps(elsewhere, 300.0));
            Assert.Equal(0, RemovedReplayFlags.Count);
        }

        [Fact]
        public void FlagPickedUp_IsNotAGhostingTrigger()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.FlagPickedUp));
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
