using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.PickRecoveryRecording</c>, written
    /// against the method as it stood before the wave-2 W7b extraction and kept unchanged
    /// through it. Each scenario seeds the committed store (and, where needed, a Re-Fly session
    /// or a supersede relation), runs one pick, and records the whole result (id, tier, path,
    /// the survivor list in order, the name-match count, the guid-dropped count) plus every log
    /// line the pick wrote. The scenarios walk every branch: the three identity paths, the
    /// stage-1 guid filter (dropped and not), the effective-timeline filter, the zombie skip,
    /// the three UT tiers and their ties, and the session-provisional bracket tie-break.
    /// A mismatch writes the actual transcript next to the test binary and names it.
    /// </summary>
    [Collection("Sequential")]
    public class PickRecoveryRecordingCharacterizationTests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaa4aaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbb4bbbbbbbbbbbbbbbbbbb";
        private const uint BakedPid = 2905720181u;

        private readonly List<string> logLines = new List<string>();

        public PickRecoveryRecordingCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ResetAll();
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

        private static Recording AddRec(
            string id, string vesselName, double startUt, double endUt, string launchGuid,
            uint spawnedPid = 0u, MergeState? mergeState = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                RecordedVesselGuid = launchGuid,
                VesselPersistentId = BakedPid,
                SpawnedVesselPersistentId = spawnedPid,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUt });
            rec.Points.Add(new TrajectoryPoint { ut = endUt });
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree " + id);
            if (mergeState.HasValue)
                rec.MergeState = mergeState.Value;
            return rec;
        }

        private static void InstallScenario(
            string provisionalId = null, string originId = null,
            List<RecordingSupersedeRelation> supersedes = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            if (provisionalId != null)
            {
                scenario.ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_1",
                    TreeId = "tree_1",
                    ActiveReFlyRecordingId = provisionalId,
                    OriginChildRecordingId = originId,
                    SupersedeTargetId = originId,
                    RewindPointId = "rp_1",
                    InvokedUT = 0.0,
                    PreSessionBranchPointIds = new List<string>(),
                };
            }
            ParsekScenario.SetInstanceForTesting(scenario);
        }

        private void Pick(StringBuilder sb, string label, RecoveredVesselIdentity identity, double ut)
        {
            logLines.Clear();
            RecoveryPickResult r = LedgerOrchestrator.PickRecoveryRecording(identity, ut);
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("result id=").Append(r.RecordingId ?? "<null>")
                .Append(" tier=").Append(r.Tier)
                .Append(" path=").Append(r.Path)
                .Append(" survivors=[");
            if (r.Survivors == null)
            {
                sb.Append("<null>");
            }
            else
            {
                for (int i = 0; i < r.Survivors.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(r.Survivors[i]?.RecordingId ?? "<null>");
                }
            }
            sb.Append("] nameMatches=").Append(r.NameMatchCount)
                .Append(" guidDropped=").Append(r.GuidDropped).Append('\n');
            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');
        }

        private static void AssertGolden(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-actual");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".txt");
            File.WriteAllText(path, actual);
            Assert.True(false, "golden mismatch for " + name + "; actual transcript written to " + path);
        }

        private string RunAllScenarios()
        {
            var sb = new StringBuilder();
            var nameOnly = RecoveredVesselIdentity.FromRawName("Hopper");
            var nameGuidA = RecoveredVesselIdentity.FromRawName("Hopper", GuidA, BakedPid);

            // S01: an empty store, a name-only identity. Nothing matches; nothing is logged.
            ResetAll();
            Pick(sb, "S01 empty store", nameOnly, 500.0);

            // S02: name fallback, live guid unknown; ut brackets both, the larger EndUT wins.
            ResetAll();
            AddRec("s02-a", "Hopper", 100.0, 600.0, null);
            AddRec("s02-b", "Hopper", 200.0, 900.0, GuidA);
            AddRec("s02-x", "Other", 100.0, 950.0, null);
            Pick(sb, "S02 name fallback bracketing", nameOnly, 500.0);

            // S03: name fallback with a live guid no recording carries; a conclusively
            // different guid is dropped by stage 1, an unknown one survives.
            ResetAll();
            AddRec("s03-diff", "Hopper", 100.0, 900.0, GuidB);
            AddRec("s03-unknown", "Hopper", 100.0, 400.0, null);
            Pick(sb, "S03 name fallback guid filter drops", nameGuidA, 500.0);

            // S04: name fallback, ut after every recording: most-recent-ended, EndUT tie keeps
            // the first in store order.
            ResetAll();
            AddRec("s04-a", "Hopper", 100.0, 300.0, null);
            AddRec("s04-b", "Hopper", 150.0, 400.0, null);
            AddRec("s04-c", "Hopper", 160.0, 400.0, null);
            Pick(sb, "S04 most recent ended with tie", nameOnly, 1000.0);

            // S05: ut before every recording: global latest.
            ResetAll();
            AddRec("s05-a", "Hopper", 600.0, 700.0, null);
            AddRec("s05-b", "Hopper", 650.0, 800.0, null);
            Pick(sb, "S05 global latest", nameOnly, 10.0);

            // S06: launch-guid path. A renamed recording of the launch is reached; a
            // same-name recording of another launch is ignored.
            ResetAll();
            AddRec("s06-renamed", "Hopper Mk2", 100.0, 900.0, GuidA);
            AddRec("s06-same", "Hopper", 100.0, 400.0, GuidA);
            AddRec("s06-other", "Hopper", 100.0, 950.0, GuidB);
            Pick(sb, "S06 launch guid path", nameGuidA, 500.0);

            // S07: spawn-pid path: a genuine Parsek spawn pid (differs from the baked pid).
            ResetAll();
            AddRec("s07-spawn", "Renamed", 100.0, 400.0, GuidB, spawnedPid: 777u);
            AddRec("s07-name", "Hopper", 100.0, 900.0, null);
            AddRec("s07-baked", "Hopper", 100.0, 950.0, null, spawnedPid: BakedPid);
            Pick(sb, "S07 spawn pid path",
                RecoveredVesselIdentity.FromRawName("Hopper", null, 777u), 500.0);

            // S08: only zombie provisionals match: pick null with the zombie trace line.
            ResetAll();
            AddRec("s08-zombie1", "Hopper", 100.0, 900.0, null, mergeState: MergeState.NotCommitted);
            AddRec("s08-zombie2", "Hopper", 100.0, 900.0, null, mergeState: MergeState.NotCommitted);
            Pick(sb, "S08 zombie only", nameOnly, 500.0);

            // S09: zombies carry the guid, a committed recording matches by name only: the
            // guid path finds nothing, the name fallback counts the name zombies.
            ResetAll();
            AddRec("s09-zombie", "Hopper", 100.0, 900.0, GuidA, mergeState: MergeState.NotCommitted);
            AddRec("s09-zombie-renamed", "Hop", 100.0, 900.0, GuidA, mergeState: MergeState.NotCommitted);
            AddRec("s09-kept", "Hopper", 100.0, 300.0, null);
            Pick(sb, "S09 guid zombies then name fallback", nameGuidA, 500.0);

            // S10: superseded recordings are off the effective timeline.
            ResetAll();
            InstallScenario(supersedes: new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rel-1", OldRecordingId = "s10-old",
                    NewRecordingId = "s10-new", UT = 500.0
                },
            });
            AddRec("s10-old", "Hopper", 100.0, 960.0, GuidA);
            AddRec("s10-new", "Hopper", 500.0, 800.0, GuidA);
            Pick(sb, "S10 effective filter", nameGuidA, 1000.0);

            // S11: an active Re-Fly session whose provisional has no guid yet: admitted on
            // name on the launch-guid path, and it wins the bracket tie.
            ResetAll();
            InstallScenario("s11-prov", "s11-origin");
            AddRec("s11-origin", "Hopper", 100.0, 900.0, GuidA);
            AddRec("s11-prov", "Hopper", 500.0, 700.0, null, mergeState: MergeState.NotCommitted);
            Pick(sb, "S11 unknown-guid session provisional", nameGuidA, 600.0);

            // S12: the session provisional with a known guid on the name-fallback path (the
            // live guid is unknown) still wins the bracket tie.
            ResetAll();
            InstallScenario("s12-prov", "s12-origin");
            AddRec("s12-origin", "Hopper", 100.0, 900.0, GuidA);
            AddRec("s12-prov", "Hopper", 500.0, 700.0, GuidA, mergeState: MergeState.NotCommitted);
            Pick(sb, "S12 session provisional name fallback", nameOnly, 600.0);

            // S13: a guid-only identity no recording knows: the path line, no pick, no trace.
            ResetAll();
            AddRec("s13-a", "Hopper", 100.0, 900.0, GuidB);
            Pick(sb, "S13 guid only unknown",
                RecoveredVesselIdentity.FromNames("", "", GuidA), 500.0);

            // S14: the session provisional is outside the candidate set (a different launch
            // guid on the launch-guid path): no tie-break, max EndUT wins.
            ResetAll();
            InstallScenario("s14-prov", "s14-origin");
            AddRec("s14-origin", "Hopper", 100.0, 650.0, GuidA);
            AddRec("s14-prov", "Hopper", 500.0, 900.0, GuidB, mergeState: MergeState.NotCommitted);
            Pick(sb, "S14 provisional excluded", nameGuidA, 600.0);

            // S15: name fallback with a live guid, nothing dropped (no conclusive mismatch).
            ResetAll();
            AddRec("s15-a", "Hopper", 100.0, 900.0, null);
            AddRec("s15-b", "#autoLOC_hopper", 100.0, 400.0, null);
            Pick(sb, "S15 name fallback nothing dropped", nameGuidA, 500.0);

            return sb.ToString();
        }

        [Fact]
        public void PickRecoveryRecording_Golden()
        {
            AssertGolden("PickRecoveryRecording_Golden", Golden, RunAllScenarios());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== S01 empty store",
            "result id=<null> tier=None path=NameFallback survivors=[] nameMatches=0 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 0 entries from 0 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "== S02 name fallback bracketing",
            "result id=s02-b tier=Bracketing path=NameFallback survivors=[s02-a,s02-b] nameMatches=2 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 3 entries from 3 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=500.0 path=name-fallback reason=live-launch-guid-unknown nameMatches=2",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=500.0 dropped=0 remaining=2 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=500.0 path=name-fallback nameMatches=2 survivors=2 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=bracketing bracketTie=max-end-ut pick=s02-b",
            "== S03 name fallback guid filter drops",
            "result id=s03-unknown tier=MostRecentEnded path=NameFallback survivors=[s03-unknown] nameMatches=2 guidDropped=1",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 2 entries from 2 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=500.0 path=name-fallback reason=no-recording-carries-launch-guid nameMatches=2",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=500.0 dropped=1 remaining=1 reason=guid-conclusive-mismatch",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=500.0 path=name-fallback nameMatches=2 survivors=1 guidDropped=1 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=s03-unknown",
            "== S04 most recent ended with tie",
            "result id=s04-b tier=MostRecentEnded path=NameFallback survivors=[s04-a,s04-b,s04-c] nameMatches=3 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 3 entries from 3 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=1000.0 path=name-fallback reason=live-launch-guid-unknown nameMatches=3",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=1000.0 dropped=0 remaining=3 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=1000.0 path=name-fallback nameMatches=3 survivors=3 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=s04-b",
            "== S05 global latest",
            "result id=s05-b tier=GlobalLatest path=NameFallback survivors=[s05-a,s05-b] nameMatches=2 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 2 entries from 2 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=10.0 path=name-fallback reason=live-launch-guid-unknown nameMatches=2",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=10.0 dropped=0 remaining=2 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=10.0 path=name-fallback nameMatches=2 survivors=2 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=global-latest bracketTie=n/a pick=s05-b",
            "== S06 launch guid path",
            "result id=s06-renamed tier=Bracketing path=LaunchGuid survivors=[s06-renamed,s06-same] nameMatches=2 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 3 entries from 3 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=500.0 path=launch-guid identityMatches=2 identityNameMismatch=1 nameOnlyIgnored=1 unknownGuidSessionProvisionalAdmitted=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=500.0 path=launch-guid nameMatches=2 survivors=2 guidDropped=n/a skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=bracketing bracketTie=max-end-ut pick=s06-renamed",
            "== S07 spawn pid path",
            "result id=s07-spawn tier=MostRecentEnded path=SpawnPid survivors=[s07-spawn] nameMatches=2 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 3 entries from 3 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=500.0 path=spawn-pid identityMatches=1 spawnPid=777 nameMatches=2",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=500.0 path=spawn-pid nameMatches=2 survivors=1 guidDropped=n/a skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=s07-spawn",
            "== S08 zombie only",
            "result id=<null> tier=None path=NameFallback survivors=[] nameMatches=0 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 0 entries from 2 committed (skippedNotCommitted=2 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=500.0 path=name-fallback nameMatches=0 survivors=0 skippedZombieNotCommitted=2 tier=none pick=<null> (only zombie provisionals matched; payout left untagged)",
            "== S09 guid zombies then name fallback",
            "result id=s09-kept tier=MostRecentEnded path=NameFallback survivors=[s09-kept] nameMatches=1 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 3 committed (skippedNotCommitted=2 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=500.0 path=name-fallback reason=no-recording-carries-launch-guid nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=500.0 dropped=0 remaining=1 reason=no-conclusive-mismatch",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=500.0 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=1 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=s09-kept",
            "== S10 effective filter",
            "result id=s10-new tier=MostRecentEnded path=LaunchGuid survivors=[s10-new] nameMatches=1 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 2 committed (skippedNotCommitted=0 skippedSuperseded=1 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId effective filter: vessel='Hopper' ut=1000.0 skippedNotEffective=1 reason=superseded-or-rewind-retired",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=1000.0 path=launch-guid identityMatches=1 identityNameMismatch=0 nameOnlyIgnored=0 unknownGuidSessionProvisionalAdmitted=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=1000.0 path=launch-guid nameMatches=1 survivors=1 guidDropped=n/a skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=s10-new",
            "== S11 unknown-guid session provisional",
            "result id=s11-prov tier=Bracketing path=LaunchGuid survivors=[s11-origin,s11-prov] nameMatches=2 guidDropped=0",
            "log [Parsek][VERBOSE][ReFlySession] SessionSuppressedSubtree: 1 recording(s) closed from root=s11-origin (childrenAdded=0 siblingsAdded=0 pidPeersAdded=0 debrisAdded=0 debrisAnchorOnlySkips=0 mixedParentHalts=0 sideOffSkips=0 stretchSeeds=0 evaKerbalsAdded=0 sameVesselBoards=0 placedPartsAdded=0 sessionOwnedExcluded=0)",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 0 entries from 2 committed (skippedNotCommitted=1 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=1 marker=sess_1)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=600.0 path=launch-guid identityMatches=2 identityNameMismatch=0 nameOnlyIgnored=0 unknownGuidSessionProvisionalAdmitted=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=600.0 path=launch-guid nameMatches=2 survivors=2 guidDropped=n/a skippedZombieNotCommitted=0 sessionProvisionalAdmitted=True tier=bracketing bracketTie=session-provisional pick=s11-prov",
            "== S12 session provisional name fallback",
            "result id=s12-prov tier=Bracketing path=NameFallback survivors=[s12-origin,s12-prov] nameMatches=2 guidDropped=0",
            "log [Parsek][VERBOSE][ReFlySession] SessionSuppressedSubtree: 1 recording(s) closed from root=s12-origin (childrenAdded=0 siblingsAdded=0 pidPeersAdded=0 debrisAdded=0 debrisAnchorOnlySkips=0 mixedParentHalts=0 sideOffSkips=0 stretchSeeds=0 evaKerbalsAdded=0 sameVesselBoards=0 placedPartsAdded=0 sessionOwnedExcluded=0)",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 0 entries from 2 committed (skippedNotCommitted=1 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=1 marker=sess_1)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=600.0 path=name-fallback reason=live-launch-guid-unknown nameMatches=2",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=600.0 dropped=0 remaining=2 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=600.0 path=name-fallback nameMatches=2 survivors=2 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=True tier=bracketing bracketTie=session-provisional pick=s12-prov",
            "== S13 guid only unknown",
            "result id=<null> tier=None path=NameFallback survivors=[] nameMatches=0 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 1 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='' ut=500.0 path=name-fallback reason=no-recording-carries-launch-guid nameMatches=0",
            "== S14 provisional excluded",
            "result id=s14-origin tier=Bracketing path=LaunchGuid survivors=[s14-origin] nameMatches=2 guidDropped=0",
            "log [Parsek][VERBOSE][ReFlySession] SessionSuppressedSubtree: 1 recording(s) closed from root=s14-origin (childrenAdded=0 siblingsAdded=0 pidPeersAdded=0 debrisAdded=0 debrisAnchorOnlySkips=0 mixedParentHalts=0 sideOffSkips=0 stretchSeeds=0 evaKerbalsAdded=0 sameVesselBoards=0 placedPartsAdded=0 sessionOwnedExcluded=0)",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 0 entries from 2 committed (skippedNotCommitted=1 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=1 marker=sess_1)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=600.0 path=launch-guid identityMatches=1 identityNameMismatch=0 nameOnlyIgnored=1 unknownGuidSessionProvisionalAdmitted=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=600.0 path=launch-guid nameMatches=2 survivors=1 guidDropped=n/a skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=bracketing bracketTie=max-end-ut pick=s14-origin",
            "== S15 name fallback nothing dropped",
            "result id=s15-a tier=Bracketing path=NameFallback survivors=[s15-a] nameMatches=1 guidDropped=0",
            "log [Parsek][VERBOSE][ERS] Rebuilt: 2 entries from 2 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=500.0 path=name-fallback reason=no-recording-carries-launch-guid nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=500.0 dropped=0 remaining=1 reason=no-conclusive-mismatch",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=500.0 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=bracketing bracketTie=max-end-ut pick=s15-a",
        }) + "\n";
    }
}
