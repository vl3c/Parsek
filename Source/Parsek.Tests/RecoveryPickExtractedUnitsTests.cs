using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the four units <c>LedgerOrchestrator.PickRecoveryRecording</c> was split
    /// into (wave 2 W7b): the store scan, the identity-path choice, the guid-filter outcome line
    /// and the UT tier walk. The end-to-end behavior is pinned by
    /// <see cref="PickRecoveryRecordingCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryPickExtractedUnitsTests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaa4aaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbb4bbbbbbbbbbbbbbbbbbb";
        private const uint BakedPid = 2905720181u;

        private readonly List<string> logLines = new List<string>();

        public RecoveryPickExtractedUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ResetAll();
            logLines.Clear();
        }

        public void Dispose()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording Rec(string id, string name, double startUt, double endUt,
            string guid = null, uint spawnedPid = 0u)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = name,
                RecordedVesselGuid = guid,
                VesselPersistentId = BakedPid,
                SpawnedVesselPersistentId = spawnedPid,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUt });
            rec.Points.Add(new TrajectoryPoint { ut = endUt });
            return rec;
        }

        // ---------------- WalkRecoveryPickTiers ----------------

        [Fact]
        public void Tiers_EmptyCandidates_AllNull()
        {
            Recording b, bsp, mre, gl;
            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording>(), 500.0, null, out b, out bsp, out mre, out gl);
            Assert.Null(b);
            Assert.Null(bsp);
            Assert.Null(mre);
            Assert.Null(gl);
        }

        [Fact]
        public void Tiers_BracketingLargestEndUtWins_TiesKeepFirst()
        {
            var a = Rec("a", "V", 100, 800);
            var b = Rec("b", "V", 200, 900);
            var c = Rec("c", "V", 300, 900);
            var ended = Rec("ended", "V", 10, 50);
            Recording br, bsp, mre, gl;
            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording> { a, b, c, ended }, 500.0, null,
                out br, out bsp, out mre, out gl);
            Assert.Same(b, br);
            Assert.Null(bsp);
            Assert.Same(ended, mre);
            Assert.Same(b, gl);
        }

        [Fact]
        public void Tiers_EndedAndGlobalLatestTies_KeepFirst()
        {
            var a = Rec("a", "V", 100, 300);
            var b = Rec("b", "V", 150, 400);
            var c = Rec("c", "V", 160, 400);
            Recording br, bsp, mre, gl;
            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording> { a, b, c }, 1000.0, null, out br, out bsp, out mre, out gl);
            Assert.Null(br);
            Assert.Same(b, mre);
            Assert.Same(b, gl);
        }

        [Fact]
        public void Tiers_EndUtEqualToUt_CountsAsBothBracketingAndEnded()
        {
            var a = Rec("a", "V", 100, 500);
            Recording br, bsp, mre, gl;
            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording> { a }, 500.0, null, out br, out bsp, out mre, out gl);
            Assert.Same(a, br);
            Assert.Same(a, mre);
            Assert.Same(a, gl);
        }

        [Fact]
        public void Tiers_SessionProvisionalMatchedByReferenceOnly()
        {
            var origin = Rec("origin", "V", 100, 900);
            var prov = Rec("prov", "V", 500, 700);
            var sameIdOtherInstance = Rec("prov", "V", 500, 700);
            Recording br, bsp, mre, gl;

            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording> { origin, prov }, 600.0, prov,
                out br, out bsp, out mre, out gl);
            Assert.Same(origin, br);
            Assert.Same(prov, bsp);

            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording> { origin, prov }, 600.0, sameIdOtherInstance,
                out br, out bsp, out mre, out gl);
            Assert.Same(origin, br);
            Assert.Null(bsp);
        }

        [Fact]
        public void Tiers_SessionProvisionalOutsideBracket_IsNotTheTieBreak()
        {
            var origin = Rec("origin", "V", 100, 900);
            var prov = Rec("prov", "V", 700, 800);
            Recording br, bsp, mre, gl;
            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording> { origin, prov }, 600.0, prov,
                out br, out bsp, out mre, out gl);
            Assert.Same(origin, br);
            Assert.Null(bsp);
        }

        [Fact]
        public void Tiers_UtBeforeEveryone_OnlyGlobalLatest()
        {
            var a = Rec("a", "V", 600, 700);
            var b = Rec("b", "V", 650, 800);
            Recording br, bsp, mre, gl;
            LedgerOrchestrator.WalkRecoveryPickTiers(
                new List<Recording> { a, b }, 10.0, null, out br, out bsp, out mre, out gl);
            Assert.Null(br);
            Assert.Null(mre);
            Assert.Same(b, gl);
        }

        // ---------------- SelectRecoveryCandidatePath ----------------

        [Fact]
        public void Path_LaunchGuid_AppendsUnknownGuidProvisionalToTheGuidList()
        {
            var g = Rec("g", "Hopper Mk2", 100, 900, GuidA);
            var n = Rec("n", "Hopper", 100, 900, GuidB);
            var prov = Rec("prov", "Hopper", 500, 700);
            var guidMatches = new List<Recording> { g };
            var nameMatches = new List<Recording> { n, prov };
            List<Recording> candidates;
            int dropped, zombies;
            var path = LedgerOrchestrator.SelectRecoveryCandidatePath(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA, BakedPid), "500.0",
                nameMatches, guidMatches, new List<Recording>(), prov,
                7, 3, 5, out candidates, out dropped, out zombies);

            Assert.Equal(RecoveryPickPath.LaunchGuid, path);
            Assert.Same(guidMatches, candidates);
            Assert.Equal(new[] { g, prov }, candidates);
            Assert.Equal(0, dropped);
            Assert.Equal(3, zombies);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("PickRecoveryRecordingId path: vessel='Hopper' ut=500.0 path=launch-guid "
                    + "identityMatches=2 identityNameMismatch=1 nameOnlyIgnored=1 "
                    + "unknownGuidSessionProvisionalAdmitted=True"));
        }

        [Fact]
        public void Path_SpawnPid_WhenNoGuidMatch()
        {
            var s = Rec("s", "Other", 100, 400, GuidB, 777u);
            var spawnMatches = new List<Recording> { s };
            List<Recording> candidates;
            int dropped, zombies;
            var path = LedgerOrchestrator.SelectRecoveryCandidatePath(
                RecoveredVesselIdentity.FromRawName("Hopper", null, 777u), "500.0",
                new List<Recording>(), new List<Recording>(), spawnMatches, null,
                7, 3, 5, out candidates, out dropped, out zombies);

            Assert.Equal(RecoveryPickPath.SpawnPid, path);
            Assert.Same(spawnMatches, candidates);
            Assert.Equal(0, dropped);
            Assert.Equal(5, zombies);
            Assert.Contains(logLines, l => l.Contains(
                "path=spawn-pid identityMatches=1 spawnPid=777 nameMatches=0"));
        }

        [Fact]
        public void Path_NameFallback_RunsTheGuidFilter()
        {
            var diff = Rec("diff", "Hopper", 100, 900, GuidB);
            var unknown = Rec("unknown", "Hopper", 100, 400);
            List<Recording> candidates;
            int dropped, zombies;
            var path = LedgerOrchestrator.SelectRecoveryCandidatePath(
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA, BakedPid), "500.0",
                new List<Recording> { diff, unknown }, new List<Recording>(),
                new List<Recording>(), null, 7, 3, 5,
                out candidates, out dropped, out zombies);

            Assert.Equal(RecoveryPickPath.NameFallback, path);
            Assert.Equal(new[] { unknown }, candidates);
            Assert.Equal(1, dropped);
            Assert.Equal(7, zombies);
            Assert.Contains(logLines, l => l.Contains(
                "path=name-fallback reason=no-recording-carries-launch-guid nameMatches=2"));
        }

        [Fact]
        public void Path_NameFallback_NoNameNoGuid_LogsNothing()
        {
            List<Recording> candidates;
            int dropped, zombies;
            var path = LedgerOrchestrator.SelectRecoveryCandidatePath(
                RecoveredVesselIdentity.FromRawName("Hopper"), "500.0",
                new List<Recording>(), new List<Recording>(), new List<Recording>(), null,
                0, 0, 0, out candidates, out dropped, out zombies);

            Assert.Equal(RecoveryPickPath.NameFallback, path);
            Assert.Empty(candidates);
            Assert.Empty(logLines);
        }

        // ---------------- LogRecoveryGuidFilterOutcome ----------------

        [Fact]
        public void GuidFilterLine_IdentityPaths_WriteNothing()
        {
            var id = RecoveredVesselIdentity.FromRawName("Hopper", GuidA, BakedPid);
            LedgerOrchestrator.LogRecoveryGuidFilterOutcome(
                id, "1.0", RecoveryPickPath.LaunchGuid, 2, 1, 3);
            LedgerOrchestrator.LogRecoveryGuidFilterOutcome(
                id, "1.0", RecoveryPickPath.SpawnPid, 2, 1, 3);
            Assert.Empty(logLines);
        }

        [Fact]
        public void GuidFilterLine_NameFallback_DroppedIsInfo_NoDropIsVerbose_NoMatchesIsSilent()
        {
            var withGuid = RecoveredVesselIdentity.FromRawName("Hopper", GuidA, BakedPid);
            LedgerOrchestrator.LogRecoveryGuidFilterOutcome(
                withGuid, "1.0", RecoveryPickPath.NameFallback, 2, 1, 3);
            Assert.Single(logLines);
            Assert.Equal("[Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId guid filter: "
                + "vessel='Hopper' ut=1.0 dropped=2 remaining=1 reason=guid-conclusive-mismatch",
                logLines[0]);

            logLines.Clear();
            LedgerOrchestrator.LogRecoveryGuidFilterOutcome(
                withGuid, "1.0", RecoveryPickPath.NameFallback, 0, 3, 3);
            Assert.Equal("[Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: "
                + "vessel='Hopper' ut=1.0 dropped=0 remaining=3 reason=no-conclusive-mismatch",
                Assert.Single(logLines));

            logLines.Clear();
            LedgerOrchestrator.LogRecoveryGuidFilterOutcome(
                RecoveredVesselIdentity.FromRawName("Hopper"), "1.0",
                RecoveryPickPath.NameFallback, 0, 3, 3);
            Assert.Contains("reason=live-launch-guid-unknown", Assert.Single(logLines));

            logLines.Clear();
            LedgerOrchestrator.LogRecoveryGuidFilterOutcome(
                withGuid, "1.0", RecoveryPickPath.NameFallback, 0, 0, 0);
            Assert.Empty(logLines);
        }

        // ---------------- ScanRecoveryCandidates ----------------

        [Fact]
        public void Scan_SortsByKey_CountsZombies_AdmitsTheSessionProvisional()
        {
            var name = Rec("name", "Hopper", 100, 900);
            var guid = Rec("guid", "Renamed", 100, 900, GuidA);
            var spawn = Rec("spawn", "Other", 100, 900, null, 777u);
            var zombie = Rec("zombie", "Hopper", 100, 900, GuidA);
            var prov = Rec("prov", "Hopper", 500, 700);
            foreach (var r in new[] { name, guid, spawn, zombie, prov })
                RecordingStore.AddRecordingWithTreeForTesting(r);
            zombie.MergeState = MergeState.NotCommitted;
            prov.MergeState = MergeState.NotCommitted;

            List<Recording> nameMatches, guidMatches, spawnMatches;
            Recording unknownProv;
            int zName, zGuid, zSpawn;
            LedgerOrchestrator.ScanRecoveryCandidates(
                RecordingStore.CommittedRecordings,
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA, 777u), 500.0, "prov",
                out nameMatches, out guidMatches, out spawnMatches, out unknownProv,
                out zName, out zGuid, out zSpawn);

            Assert.Equal(new[] { name, prov }, nameMatches);
            Assert.Equal(new[] { guid }, guidMatches);
            Assert.Equal(new[] { spawn }, spawnMatches);
            Assert.Same(prov, unknownProv);
            Assert.Equal(1, zName);
            Assert.Equal(1, zGuid);
            Assert.Equal(0, zSpawn);
            Assert.DoesNotContain(logLines, l => l.Contains("effective filter"));
        }

        [Fact]
        public void Scan_SupersededMatch_IsSkippedAndLogged()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rel", OldRecordingId = "old",
                        NewRecordingId = "new", UT = 500.0
                    },
                },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RecordingStore.AddRecordingWithTreeForTesting(Rec("old", "Hopper", 100, 960, GuidA));
            RecordingStore.AddRecordingWithTreeForTesting(Rec("new", "Hopper", 500, 800, GuidA));

            List<Recording> nameMatches, guidMatches, spawnMatches;
            Recording unknownProv;
            int zName, zGuid, zSpawn;
            LedgerOrchestrator.ScanRecoveryCandidates(
                RecordingStore.CommittedRecordings,
                RecoveredVesselIdentity.FromRawName("Hopper", GuidA, BakedPid), 1000.0, null,
                out nameMatches, out guidMatches, out spawnMatches, out unknownProv,
                out zName, out zGuid, out zSpawn);

            Assert.Single(nameMatches);
            Assert.Equal("new", Assert.Single(guidMatches).RecordingId);
            Assert.Null(unknownProv);
            Assert.Contains(logLines, l => l == "[Parsek][VERBOSE][LedgerOrchestrator] "
                + "PickRecoveryRecordingId effective filter: vessel='Hopper' ut=1000.0 "
                + "skippedNotEffective=1 reason=superseded-or-rewind-retired");
        }
    }
}
