using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Parsek;

namespace Parsek.Tests
{
    /// <summary>
    /// A plain rewind (Rewind-to-Launch) moves the clock and nothing else: it keeps every
    /// supersede row of the rewound tree (owner ruling 2026-10-24; design section 2.2).
    /// A sealed re-fly stays sealed, an open one stays open, and a row whose fork is missing
    /// keeps hiding the flight it replaced, as the load-time sweep keeps it.
    ///
    /// <para>History these cells pin against: from PR #774 (2026-05-08) to PR #2230 the
    /// rewind dropped rows and retired forks (open forks always, sealed forks on a
    /// self-rewind or behind a dropped open fork), so a rewind could bring the original
    /// stage back and reopen a slot whose re-fly had been merged. #2230 kept committed
    /// forks; the dangling-row drop and its Pass 2 cascade (which could still retire a
    /// sealed fork behind a missing one) went with this change.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RewindKeepsSupersedeRowsTests : IDisposable
    {
        private readonly bool prevSuppress;
        private readonly List<string> logLines = new List<string>();

        public RewindKeepsSupersedeRowsTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = prevSuppress;
            RewindContext.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording MakeRec(string id, double startUT, MergeState state = MergeState.Immutable)
        {
            return new Recording
            {
                RecordingId = id,
                ExplicitStartUT = startUT,
                VesselName = $"vessel-{id}",
                MergeState = state
            };
        }

        private static RecordingSupersedeRelation MakeRel(string oldId, string newId, double ut = 0.0)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = $"rel_{oldId}_{newId}",
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = ut
            };
        }

        private static Dictionary<string, Recording> ById(params Recording[] recs)
        {
            var d = new Dictionary<string, Recording>(StringComparer.Ordinal);
            foreach (var r in recs) d[r.RecordingId] = r;
            return d;
        }

        private static void InstallCommittedTreeForTesting(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = recordings[0].RecordingId
            };
            foreach (var rec in recordings)
            {
                rec.TreeId = treeId;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddCommittedInternal(rec);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        private ParsekScenario InstallScenario(params RecordingSupersedeRelation[] rows)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(rows),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        // ---------------------------------------------------------------- pure audit

        [Fact]
        public void Audit_NullOrEmptyInputs_CountNothing()
        {
            var a = RecordingStore.AuditSupersedesKeptAcrossRewindPure(null, 0, null, null, null);
            Assert.Equal(0, a.Rows);
            var owner = MakeRec("o", 1.0);
            a = RecordingStore.AuditSupersedesKeptAcrossRewindPure(
                owner, 0, null, null, new List<RecordingSupersedeRelation>());
            Assert.Equal(0, a.Rows);
            Assert.Empty(a.ForkIds);
        }

        [Fact]
        public void Audit_OpenThenSealedReFly_ParentRewind_CountsBothAndKeepsTheList()
        {
            // The likeliest shape of the 2026-10-24 report: stage A re-flown, the re-fly
            // crashed again (A1 open), re-flown again and sealed (A2). The old rollback
            // dropped A->A1 (open) and then demoted A1->A2 because its source was retired,
            // so the sealed A2 vanished and the original A came back.
            var owner = MakeRec("ab", 5.0);
            var a = MakeRec("a", 30.0, MergeState.CommittedProvisional);
            var a1 = MakeRec("a1", 30.0, MergeState.CommittedProvisional);
            var a2 = MakeRec("a2", 30.0, MergeState.Immutable);
            var rows = new List<RecordingSupersedeRelation> { MakeRel("a", "a1"), MakeRel("a1", "a2") };
            var before = rows.ToList();

            var audit = RecordingStore.AuditSupersedesKeptAcrossRewindPure(
                owner, 0.0, new List<Recording> { owner, a, a1, a2 }, ById(owner, a, a1, a2), rows);

            Assert.Equal(2, audit.Rows);
            Assert.Equal(2, audit.Forks);
            Assert.Equal(1, audit.Open);
            Assert.Equal(1, audit.Sealed);
            Assert.Equal(0, audit.Dangling);
            Assert.Equal(new[] { "a1", "a2" }, audit.ForkIds);
            Assert.Equal(before, rows);
        }

        [Fact]
        public void Audit_DanglingUpstream_SealedDownstream_BothCounted_NoneDropped()
        {
            // A -> B (recording failed to load) -> C (sealed). #2230 still dropped A->B and
            // its Pass 2 cascade then retired the sealed C. Now both rows stay: A stays
            // hidden (as on any load) and C stays the slot's tip.
            var owner = MakeRec("a", 5.0);
            var c = MakeRec("c", 30.0, MergeState.Immutable);
            var rows = new List<RecordingSupersedeRelation> { MakeRel("a", "b", ut: 30.0), MakeRel("b", "c") };

            var audit = RecordingStore.AuditSupersedesKeptAcrossRewindPure(
                owner, 0.0, new List<Recording> { owner, c }, ById(owner, c), rows);

            Assert.Equal(1, audit.Dangling);
            Assert.Equal(0, audit.Sealed);
            Assert.Equal(2, rows.Count);
        }

        [Fact]
        public void Audit_SelfRewindOnSealedFork_CountsTheIncomingRow()
        {
            // The rewind owner is the sealed fork itself. The old rollback force-dropped the
            // incoming priorTip -> fork row and retired the fork, unsealing the slot.
            var prior = MakeRec("prior", 2.0, MergeState.CommittedProvisional);
            var canon = MakeRec("canon", 15.0, MergeState.Immutable);
            var rows = new List<RecordingSupersedeRelation> { MakeRel("prior", "canon") };

            var audit = RecordingStore.AuditSupersedesKeptAcrossRewindPure(
                canon, 15.0, new List<Recording> { prior, canon }, ById(prior, canon), rows);

            Assert.Equal(1, audit.Rows);
            Assert.Equal(1, audit.Sealed);
            Assert.Single(rows);
        }

        [Fact]
        public void Audit_Scope_MatchesTheRewoundTreeOnly()
        {
            var owner = MakeRec("orig", 6.5);
            var branch = MakeRec("probe", 31.5, MergeState.CommittedProvisional);
            var forkOwner = MakeRec("fork-owner", 31.5, MergeState.CommittedProvisional);
            var forkBranch = MakeRec("fork-probe", 31.5, MergeState.Immutable);
            var early = MakeRec("early-fork", 3.0, MergeState.Immutable);
            var foreignOld = MakeRec("other-tree", 40.0);
            var foreignNew = MakeRec("other-fork", 41.0);
            var rows = new List<RecordingSupersedeRelation>
            {
                MakeRel("orig", "fork-owner"),          // owner row: in scope
                MakeRel("probe", "fork-probe"),         // branch row: in scope
                MakeRel("orig", "early-fork"),          // fork before the rewind: out
                MakeRel("other-tree", "other-fork"),    // another tree: out
                MakeRel("orig", "lost-fork", ut: 2.0),  // missing fork, merged before the rewind: out
                MakeRel("orig", "lost-late", ut: 33.0), // missing fork, merged after: in, dangling
                MakeRel("", "orig", ut: 33.0),          // malformed
                MakeRel("orig", "", ut: 33.0),          // malformed
            };

            var audit = RecordingStore.AuditSupersedesKeptAcrossRewindPure(
                owner, 6.5, new List<Recording> { owner, branch, forkOwner, forkBranch, early },
                ById(owner, branch, forkOwner, forkBranch, early, foreignOld, foreignNew), rows);

            Assert.Equal(3, audit.Rows);
            Assert.Equal(new[] { "fork-owner", "fork-probe", "lost-late" }, audit.ForkIds);
            Assert.Equal(1, audit.Open);
            Assert.Equal(1, audit.Sealed);
            Assert.Equal(1, audit.Dangling);
            Assert.Equal(8, rows.Count);
        }

        [Fact]
        public void Audit_ClosureRowsNamingOneFork_CountTheForkOnce()
        {
            // A merge writes one row per member of the replaced closure, all naming the same
            // fork; forks= counts the fork once, rows= counts every row.
            var owner = MakeRec("root", 1.0);
            var booster = MakeRec("booster", 20.0, MergeState.CommittedProvisional);
            var debris = MakeRec("debris", 25.0, MergeState.Immutable);
            var fork = MakeRec("fork", 20.0, MergeState.CommittedProvisional);
            var rows = new List<RecordingSupersedeRelation> { MakeRel("booster", "fork"), MakeRel("debris", "fork") };

            var audit = RecordingStore.AuditSupersedesKeptAcrossRewindPure(
                owner, 0.0, new List<Recording> { owner, booster, debris, fork }, ById(owner, booster, debris, fork), rows);

            Assert.Equal(2, audit.Rows);
            Assert.Equal(1, audit.Forks);
            Assert.Equal(1, audit.Open);
        }

        // ---------------------------------------------------------------- live entry

        [Fact]
        public void Live_KeepsOpenAndSealedChain_WritesNoRetirement_BumpsNothing()
        {
            var owner = MakeRec("ab", 5.0);
            var a = MakeRec("a", 30.0, MergeState.CommittedProvisional);
            var a1 = MakeRec("a1", 30.0, MergeState.CommittedProvisional);
            var a2 = MakeRec("a2", 30.0, MergeState.Immutable);
            InstallCommittedTreeForTesting("tree-ab", owner, a, a1, a2);
            var scenario = InstallScenario(MakeRel("a", "a1"), MakeRel("a1", "a2"));
            var rowsBefore = scenario.RecordingSupersedes.ToList();
            int versionBefore = scenario.SupersedeStateVersion;

            int rows = RecordingStore.LogSupersedesKeptAcrossRewind(owner, 0.0);

            Assert.Equal(2, rows);
            Assert.Equal(rowsBefore, scenario.RecordingSupersedes);
            Assert.Empty(scenario.RecordingRewindRetirements);
            Assert.Equal(versionBefore, scenario.SupersedeStateVersion);
            Assert.Equal(MergeState.CommittedProvisional, a1.MergeState);
            Assert.Equal(MergeState.Immutable, a2.MergeState);
            Assert.True(EffectiveState.IsSupersededByRelation(a, scenario.RecordingSupersedes));
            Assert.True(EffectiveState.IsSupersededByRelation(a1, scenario.RecordingSupersedes));
            Assert.False(EffectiveState.IsSupersededByRelation(a2, scenario.RecordingSupersedes));

            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Rewind keeps supersede rows: rows=2 forks=2 sealed=1 open=1 dangling=0 rewindUT=0.0 owner='vessel-ab'"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Kept re-fly fork across rewind: rec=a1 mergeState=CommittedProvisional"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Kept re-fly fork across rewind: rec=a2 mergeState=Immutable"));
        }

        [Fact]
        public void Live_DanglingRow_StaysAndKeepsHidingTheReplacedFlight()
        {
            // The gameplay decision: a re-fly whose recording failed to load keeps its row.
            // The ledger still holds that re-fly's rows and the replaced flight's tombstones
            // (owner ruling O3), so the replaced flight must not come back at a rewind; a
            // plain load keeps the same row (LoadTimeSweep warns, keeps it).
            var owner = MakeRec("root", 5.0);
            var booster = MakeRec("booster", 30.0, MergeState.CommittedProvisional);
            InstallCommittedTreeForTesting("tree-root", owner, booster);
            var scenario = InstallScenario(MakeRel("booster", "lost-refly", ut: 31.0));

            RecordingStore.LogSupersedesKeptAcrossRewind(owner, 0.0);

            Assert.Single(scenario.RecordingSupersedes);
            Assert.Empty(scenario.RecordingRewindRetirements);
            Assert.True(EffectiveState.IsSupersededByRelation(booster, scenario.RecordingSupersedes));
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Rewind keeps supersede rows: rows=1 forks=1 sealed=0 open=0 dangling=1"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Kept re-fly fork across rewind: rec=lost-refly mergeState=<missing>"));
        }

        [Fact]
        public void Live_NoRowInScope_LogsVerboseOnly()
        {
            var owner = MakeRec("solo", 5.0);
            InstallCommittedTreeForTesting("tree-solo", owner);
            InstallScenario(MakeRel("elsewhere", "elsewhere-fork", ut: 50.0));

            Assert.Equal(0, RecordingStore.LogSupersedesKeptAcrossRewind(owner, 0.0));

            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Rewind keeps supersede rows: none in scope") && l.Contains("treeResolved=True"));
            Assert.DoesNotContain(logLines, l => l.Contains("Kept re-fly fork across rewind"));
        }

        [Fact]
        public void Live_NoScenarioOrNoRows_IsANoOp()
        {
            Assert.Equal(0, RecordingStore.LogSupersedesKeptAcrossRewind(null, 0.0));
            Assert.Equal(0, RecordingStore.LogSupersedesKeptAcrossRewind(MakeRec("x", 1.0), 0.0));
            InstallScenario();
            Assert.Equal(0, RecordingStore.LogSupersedesKeptAcrossRewind(MakeRec("x", 1.0), 0.0));
            Assert.DoesNotContain(logLines, l => l.Contains("Rewind keeps supersede rows"));
        }

        // ---------------------------------------------------------------- source and fixture gates

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }

        private static string StripLineComments(string source)
        {
            var lines = source.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join("\n", lines);
        }

        [Fact]
        public void Source_NothingOutsideTheLoaderBuildsARewindRetirement()
        {
            // The rewind used to be the one writer of RecordingRewindRetirement rows. A new
            // `new RecordingRewindRetirement` in production code is a new way to retire a
            // committed flight, which needs an owner ruling first; the loader's LoadFrom is
            // the one allowed site.
            string root = Path.Combine(ResolveRepoRoot(), "Source", "Parsek");
            var offenders = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("InGameTests/", StringComparison.Ordinal)) continue;
                if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)) continue;
                if (rel == "RecordingRewindRetirement.cs") continue;
                if (StripLineComments(File.ReadAllText(path)).Contains("new RecordingRewindRetirement"))
                    offenders.Add(rel);
            }
            Assert.True(offenders.Count == 0,
                "Production code builds a RecordingRewindRetirement outside the loader: "
                + string.Join(", ", offenders));
        }

        [Fact]
        public void Fixtures_CarryNoRewindRetirement()
        {
            // Saves written before PR #2230 can carry the old rollback's retirements (a
            // re-fly hidden and its original back). Nothing repairs them, so no committed
            // fixture may carry one: every retirement row has a reason starting with this
            // literal (RecordingRewindRetirement's four reason constants).
            string repo = ResolveRepoRoot();
            var dirs = new[]
            {
                Path.Combine(repo, "harness", "fixtures"),
                Path.Combine(repo, "Source", "Parsek.Tests", "Fixtures"),
            };
            var offenders = new List<string>();
            int scanned = 0;
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".sfs" && ext != ".cfg" && ext != ".txt") continue;
                    scanned++;
                    if (File.ReadAllText(path).Contains("rewound-out-supersede"))
                        offenders.Add(path.Substring(repo.Length + 1));
                }
            }
            Assert.True(scanned > 0, "no fixture files scanned; the fixture paths moved");
            Assert.True(offenders.Count == 0,
                "Committed fixtures carry a pre-#2230 rewind retirement: " + string.Join(", ", offenders));
        }

        // ---------------------------------------------------------------- kept from the rollback file

        [Fact]
        public void CanFastForwardAtUT_RewindRetiredRecording_ReturnsFalse()
        {
            // A save written before 2026-10-25 may still carry a retirement; its readers stay.
            var rec = MakeRec("fork", startUT: 100.0, state: MergeState.NotCommitted);
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 101.0 });
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    new RecordingRewindRetirement
                    {
                        RetirementId = "rrt_fork",
                        RecordingId = "fork",
                        Reason = RecordingRewindRetirement.DefaultReason
                    }
                }
            });

            bool canFastForward = RecordingStore.CanFastForwardAtUT(
                rec, now: 10.0, out string reason, isRecording: false);

            Assert.False(canFastForward);
            Assert.Contains("rewound out", reason);
        }

        [Fact]
        public void TryFindCommittedRecordingById_ReturnsRecording_WhenIdMatches()
        {
            var rec = MakeRec("foo", startUT: 6.5);
            RecordingStore.AddCommittedInternal(rec);
            Assert.Same(rec, RecordingStore.TryFindCommittedRecordingById("foo"));
        }

        [Fact]
        public void TryFindCommittedRecordingById_ReturnsNull_OnNullEmptyOrUnknownId()
        {
            Assert.Null(RecordingStore.TryFindCommittedRecordingById(null));
            Assert.Null(RecordingStore.TryFindCommittedRecordingById(""));
            Assert.Null(RecordingStore.TryFindCommittedRecordingById("missing"));
        }
    }
}
