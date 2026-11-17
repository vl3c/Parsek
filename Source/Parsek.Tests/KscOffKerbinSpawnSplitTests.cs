using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo KSC-END-SPAWN-SKIPS-OFF-KERBIN: the Space Center's structural check is split
    /// into a draw half (Kerbin only, the scene can only place a ghost there) and a spawn
    /// half (any body), so a recording that starts off Kerbin still runs its end spawn,
    /// decided by the same spawn predicates as every other scene.
    /// </summary>
    [Collection("Sequential")]
    public class KscOffKerbinSpawnSplitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KscOffKerbinSpawnSplitTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        static bool SpawnHalf(Recording rec)
        {
            return ParsekKSC.ClassifyKscStructuralRoute(rec, out _) != ParsekKSC.KscStructuralRoute.Skip;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        static Recording MakeRecording(
            string body,
            TerminalState? terminal = TerminalState.Splashed,
            string sit = "SPLASHED",
            int points = 2)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", sit);
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", "Duna Rocket");

            var pts = new List<TrajectoryPoint>();
            for (int i = 0; i < points; i++)
                pts.Add(new TrajectoryPoint { ut = 1000 + i * 1000, bodyName = body, altitude = 70 });

            return new Recording
            {
                RecordingId = "rec-" + body,
                VesselName = "Duna Rocket",
                VesselPersistentId = 12345,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 2000,
                VesselSnapshot = snapshot,
                PlaybackEnabled = true,
                TerminalStateValue = terminal,
                Points = pts,
            };
        }

        [Fact]
        public void Classify_KerbinRecording_RendersWithNoReason()
        {
            var rec = MakeRecording("Kerbin");
            Assert.Equal(ParsekKSC.KscStructuralRoute.Render,
                ParsekKSC.ClassifyKscStructuralRoute(rec, out string reason));
            Assert.Null(reason);
            Assert.True(ParsekKSC.IsKscDrawEligible(rec));
            Assert.True(SpawnHalf(rec));
        }

        [Fact]
        public void Classify_OffKerbinRecording_IsSpawnEligibleButNotDrawEligible()
        {
            var rec = MakeRecording("Laythe");
            Assert.Equal(ParsekKSC.KscStructuralRoute.SpawnOnly,
                ParsekKSC.ClassifyKscStructuralRoute(rec, out string reason));
            Assert.Equal(ParsekKSC.KscStructuralReasonOffKerbin, reason);
            Assert.False(ParsekKSC.IsKscDrawEligible(rec));
            Assert.True(SpawnHalf(rec));
            // The draw half still keeps it out of the KSC ghost list.
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Theory]
        [InlineData("Kerbin")]
        [InlineData("Laythe")]
        public void Classify_TooFewPoints_SkipsBothHalves(string body)
        {
            var rec = MakeRecording(body, points: 1);
            Assert.Equal(ParsekKSC.KscStructuralRoute.Skip,
                ParsekKSC.ClassifyKscStructuralRoute(rec, out string reason));
            Assert.Equal(ParsekKSC.KscStructuralReasonTooFewPoints, reason);
            Assert.False(ParsekKSC.IsKscDrawEligible(rec));
            Assert.False(SpawnHalf(rec));
        }

        [Fact]
        public void Classify_NullRecordingOrPoints_Skips()
        {
            Assert.Equal(ParsekKSC.KscStructuralRoute.Skip,
                ParsekKSC.ClassifyKscStructuralRoute(null, out string nullReason));
            Assert.Equal(ParsekKSC.KscStructuralReasonNullRecording, nullReason);

            var rec = MakeRecording("Laythe");
            rec.Points = null;
            Assert.False(SpawnHalf(rec));
        }

        [Fact]
        public void SpawnPredicate_OffKerbinSplashedPastEnd_SpawnsLikeKerbin()
        {
            // The spawn half hands the decision to the same predicate a Kerbin recording
            // uses; it has no body rule.
            var laythe = MakeRecording("Laythe");
            var kerbin = MakeRecording("Kerbin");
            var (laytheSpawn, laytheReason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(laythe, laythe.EndUT + 1);
            var (kerbinSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(kerbin, kerbin.EndUT + 1);
            Assert.True(laytheSpawn, laytheReason);
            Assert.Equal(kerbinSpawn, laytheSpawn);
        }

        [Fact]
        public void SpawnPredicate_OffKerbinDestroyed_DoesNotSpawn()
        {
            var rec = MakeRecording("Laythe", TerminalState.Destroyed, sit: "FLYING");
            Assert.True(SpawnHalf(rec));
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);
            Assert.False(needsSpawn);
            Assert.Contains("Destroyed", reason);
        }

        [Fact]
        public void SpawnPredicate_OffKerbinOrbiting_DefersToFlight()
        {
            var rec = MakeRecording("Ike", TerminalState.Orbiting, sit: "ORBITING");
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);
            Assert.False(needsSpawn);
            Assert.Contains("orbital vessel deferred to flight scene", reason);
        }

        [Fact]
        public void SpawnPredicate_NullTerminalOrbitingSnapshot_DefersToFlight()
        {
            // Un-finalized: the snapshot's situation stands in for the terminal (#171).
            var rec = MakeRecording("Ike", terminal: null, sit: "ORBITING");
            Assert.True(GhostPlaybackLogic.IsUnfinalizedOrbitalSnapshot(rec));
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);
            Assert.False(needsSpawn);
            Assert.Contains("unfinalized, snapshot situation maps to terminal Orbiting", reason);
        }

        [Fact]
        public void SpawnPredicate_NullTerminalNonLeaf_DoesNotSpawn()
        {
            var rec = MakeRecording("Laythe", terminal: null, sit: "SPLASHED");
            rec.ChildBranchPointId = "bp-1";
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);
            Assert.False(needsSpawn);
            Assert.Contains("non-leaf", reason);
            Assert.False(GhostPlaybackLogic.IsUnfinalizedOrbitalSnapshot(rec));
        }

        [Fact]
        public void SpawnPredicate_OffKerbinDebrisAndGhostOnly_DoNotSpawn()
        {
            var debris = MakeRecording("Laythe");
            debris.IsDebris = true;
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtKscEnd(debris, debris.EndUT + 1).needsSpawn);

            var ghostOnly = MakeRecording("Laythe");
            ghostOnly.ChainBranch = 1;
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtKscEnd(ghostOnly, ghostOnly.EndUT + 1).needsSpawn);

            var spawned = MakeRecording("Laythe");
            spawned.SpawnedVesselPersistentId = 777;
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtKscEnd(spawned, spawned.EndUT + 1).needsSpawn);
        }

        [Theory]
        // loopDriven, currentUT, endUT, historical, expected
        [InlineData(false, 2001.0, 2000.0, false, "PastEndSpawn")]
        [InlineData(false, 2000.0, 2000.0, false, "None")]
        [InlineData(false, 1500.0, 2000.0, false, "None")]
        [InlineData(false, 2001.0, 2000.0, true, "None")]
        [InlineData(true, 1500.0, 2000.0, false, "LoopFirstRun")]
        [InlineData(true, 2001.0, 2000.0, true, "LoopFirstRun")]
        public void DecideKscSpawnOnlyAction_MirrorsTheRenderBranches(
            bool loopDriven, double currentUT, double endUT, bool historical,
            string expected)
        {
            // A loop-driven recording goes through the loop first-run spawn, which applies
            // its own end-UT and replay-scope gates (ShouldAttemptLoopFirstRunSpawn).
            Assert.Equal(expected,
                ParsekKSC.DecideKscSpawnOnlyAction(loopDriven, currentUT, endUT, historical).ToString());
        }

        [Fact]
        public void LogKscSpawnOnlyAttemptOnce_LogsOncePerRecording()
        {
            var rec = MakeRecording("Laythe");
            var keys = new HashSet<string>();
            Assert.True(ParsekKSC.LogKscSpawnOnlyAttemptOnce(
                rec, 3, ParsekKSC.KscStructuralReasonOffKerbin, keys));
            Assert.False(ParsekKSC.LogKscSpawnOnlyAttemptOnce(
                rec, 3, ParsekKSC.KscStructuralReasonOffKerbin, keys));

            Assert.Single(logLines.FindAll(l => l.Contains("[KSCSpawn]")
                && l.Contains("Spawn-only past-end: attempting spawn for #3")
                && l.Contains("reason=off-kerbin")
                && l.Contains("firstBody=Laythe")));
        }

        [Fact]
        public void StructuralPassCounts_SummaryAndStateKey()
        {
            var counts = new ParsekKSC.KscStructuralPassCounts();
            counts.Count(ParsekKSC.KscStructuralRoute.Render);
            counts.Count(ParsekKSC.KscStructuralRoute.Render);
            counts.Count(ParsekKSC.KscStructuralRoute.SpawnOnly);
            counts.Count(ParsekKSC.KscStructuralRoute.Skip);

            Assert.Equal(4, counts.Total);
            Assert.Equal("2|1|1", counts.StateKey());
            string summary = ParsekKSC.FormatKscStructuralPassSummary(counts);
            Assert.Contains("KSC structural pass: drawable=2 spawnOnly=1", summary);
            Assert.Contains("skipped=1", summary);
        }
    }
}
