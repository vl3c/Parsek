using System.Collections.Generic;
using System.Linq;
using Xunit;
using Parsek;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the rewind-time supersede-rollback fix:
    /// <see cref="RecordingStore.DropSupersedesRewoundOutOfExistencePure"/>.
    ///
    /// <para>
    /// Bug source: 2026-05-08 playtest. After a Re-Fly, the user clicks Rewind on
    /// the launch row. The Re-Fly's <see cref="RecordingSupersedeRelation"/> rows
    /// persist through Rewind, so the original recording's launch ghost stays
    /// suppressed via <c>reason=superseded-by-relation</c> after re-launch even
    /// though the user has rewound past the moment the forks were created.
    /// </para>
    ///
    /// <para>
    /// The fix walks the rewound owner's whole tree and drops supersede rows
    /// where the fork's <c>StartUT >= rewindAdjustedUT</c>. Walking the tree (not
    /// just the owner) is load-bearing: branch recordings (e.g. an upper-stage
    /// Probe at index #7 in the playtest) carry their own supersede rows whose
    /// <c>OldRecordingId</c> is the branch, not the owner.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RewindSupersedeRollbackTests : System.IDisposable
    {
        private readonly bool prevSuppress;
        private readonly List<string> logLines = new List<string>();

        public RewindSupersedeRollbackTests()
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

        private static Recording MakeRec(string id, double startUT)
        {
            // Recording.StartUT is computed from ExplicitStartUT (when set) or first
            // trajectory point. ExplicitStartUT pins StartUT deterministically.
            //
            // MergeState explicitly NotCommitted: Recording's field-default is
            // Immutable (Recording.cs:254 — "the pre-feature invariant: every
            // recording reachable from a committed tree was already immutable"),
            // but tests in this file that exercise the rollback drop+retire
            // path are testing non-canon forks. fix-rewind-canon-forks
            // preserves Immutable forks across parent rewind, so any test
            // wanting drop behaviour must opt out of the default. Tests that
            // specifically exercise canon preservation use MakeRecWithMergeState
            // with MergeState.Immutable.
            return new Recording
            {
                RecordingId = id,
                ExplicitStartUT = startUT,
                VesselName = $"vessel-{id}",
                MergeState = MergeState.NotCommitted
            };
        }

        private static RecordingSupersedeRelation MakeRel(string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                OldRecordingId = oldId,
                NewRecordingId = newId
            };
        }

        [Fact]
        public void Drops_OwnerRow_WhenForkInFuture()
        {
            var owner = MakeRec("orig", startUT: 6.5);
            var fork = MakeRec("fork-A", startUT: 31.5);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig", owner },
                { "fork-A", fork }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("orig", "fork-A")
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner, fork },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(1, dropped);
            Assert.Empty(supersedes);
        }

        [Fact]
        public void Drops_BranchRow_WhenForkInFuture()
        {
            // The 2026-05-08 playtest regression: KSP.log:129371-129373 showed the
            // original Kerbal X (#0) AND the original Kerbal X Probe (#7) both
            // skipped as superseded-by-relation. The Probe (#7) is a tree branch
            // with its own supersede row (#7 -> probe-fork). Dropping only the
            // owner's row would leave #7 suppressed. The tree-walk catches it.
            var owner = MakeRec("kerbal-x", startUT: 6.5);
            var probe = MakeRec("kerbal-x-probe", startUT: 30.7);  // branch
            var forkA = MakeRec("fork-A", startUT: 31.5);          // fork of Kerbal X
            var forkB = MakeRec("fork-B", startUT: 31.5);          // fork of Probe
            var liveById = new Dictionary<string, Recording>
            {
                { "kerbal-x", owner },
                { "kerbal-x-probe", probe },
                { "fork-A", forkA },
                { "fork-B", forkB }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("kerbal-x", "fork-A"),
                MakeRel("kerbal-x-probe", "fork-B")
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner, probe, forkA, forkB },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(2, dropped);
            Assert.Empty(supersedes);
        }

        [Fact]
        public void Keeps_RowFromUnrelatedTree()
        {
            // A supersede whose OldRecordingId is NOT in the rewound owner's tree
            // must be left alone — it belongs to a different launch's history.
            var owner = MakeRec("owner", startUT: 6.5);
            var unrelated = MakeRec("unrelated", startUT: 100.0);
            var unrelatedFork = MakeRec("unrelated-fork", startUT: 200.0);
            var liveById = new Dictionary<string, Recording>
            {
                { "owner", owner },
                { "unrelated", unrelated },
                { "unrelated-fork", unrelatedFork }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("unrelated", "unrelated-fork")
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner }, // owner's tree only
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(0, dropped);
            Assert.Single(supersedes);
        }

        [Fact]
        public void Keeps_ForkBeforeRewindUT()
        {
            // Edge case — fork StartUT is BEFORE rewindUT, so it's not "rewound out".
            // Pin the boundary to >= so a fork at exactly the rewind UT also drops.
            var owner = MakeRec("orig", startUT: 6.5);
            var earlyFork = MakeRec("early-fork", startUT: 5.0);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig", owner },
                { "early-fork", earlyFork }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("orig", "early-fork")
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner, rewindAdjustedUT: 10.0,
                ownerTreeRecordings: new List<Recording> { owner, earlyFork },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(0, dropped);
            Assert.Single(supersedes);
        }

        [Fact]
        public void Drops_MultiGenerationalChain()
        {
            // A → B → C: A is the owner, B and C are post-rewind forks.
            // Both A→B and B→C relations should drop because:
            //   - A→B: A is the owner (in rewoundOutOldIds), B.StartUT >= rewindUT
            //   - B→C: B.StartUT >= rewindUT (in rewoundOutOldIds via tree walk),
            //          C.StartUT >= rewindUT
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRec("B", startUT: 31.5);
            var c = MakeRec("C", startUT: 50.0);
            var liveById = new Dictionary<string, Recording>
            {
                { "A", a }, { "B", b }, { "C", c }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("A", "B"),
                MakeRel("B", "C")
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                a, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { a, b, c },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(2, dropped);
            Assert.Empty(supersedes);
        }

        [Fact]
        public void DetailedRollback_MultiGenerationalChain_RetiresForksAndRestoresOnlyOrigin()
        {
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRec("B", startUT: 31.5);
            var c = MakeRec("C", startUT: 50.0);
            var liveById = new Dictionary<string, Recording>
            {
                { "A", a }, { "B", b }, { "C", c }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("A", "B"),
                MakeRel("B", "C")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                a,
                rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { a, b, c },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(2, result.DroppedRelationCount);
            Assert.Contains("B", result.RetiredForkRecordingIds);
            Assert.Contains("C", result.RetiredForkRecordingIds);
            Assert.Contains("A", result.RestoredRecordingIds);
            Assert.DoesNotContain("B", result.RestoredRecordingIds);
            Assert.Empty(supersedes);
        }

        [Fact]
        public void LiveRollback_DeduplicatesRetirement_WhenMultipleOldRowsPointToSameNew()
        {
            // Two supersedes (A→C and B→C) collapse to one fork retirement for
            // C (RetiredForkRecordingIds dedupes by hash). Owner A stays
            // restored (StartUT == rewindAdjustedUT). B is an old-side with
            // StartUT > rewindAdjustedUT, so it gains its own retirement under
            // the old-side pass.
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRec("B", startUT: 31.5);
            var c = MakeRec("C", startUT: 50.0);
            InstallCommittedTreeForTesting("tree-dedup", a, b, c);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("A", "C"),
                    MakeRel("B", "C")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(a, 6.5);

            Assert.Equal(2, dropped);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Equal(2, scenario.RecordingRewindRetirements.Count);

            RecordingRewindRetirement forkRetirement = scenario.RecordingRewindRetirements
                .Single(r => r.Reason == RecordingRewindRetirement.DefaultReason);
            Assert.Equal("C", forkRetirement.RecordingId);
            Assert.Equal("A", forkRetirement.RestoredRecordingId);

            RecordingRewindRetirement oldSideRetirement = scenario.RecordingRewindRetirements
                .Single(r => r.Reason == RecordingRewindRetirement.RewoundOutOldSideReason);
            Assert.Equal("B", oldSideRetirement.RecordingId);
            Assert.Null(oldSideRetirement.RestoredRecordingId);
            Assert.Null(oldSideRetirement.SourceSupersedeRelationId);

            // Owner A stays visible (rewind target; not in retirement set).
            Assert.DoesNotContain(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "A");
        }

        [Fact]
        public void LiveRollback_RetiresOldSides_WhenAllStartAfterRewindUT()
        {
            // Mirrors logs/2026-05-10_1713 — owner is the Kerbal X main rocket
            // recording (StartUT=302) at the launch pad; rewindAdjustedUT=287
            // accounts for RewindToLaunchLeadTimeSeconds. The Re-Fly fork (F)
            // and the three originals it superseded (P_atmo, P_destroyed,
            // P_continuation) all start AFTER the rewind boundary, so they all
            // need to be hidden after rollback. Without the old-side retirement
            // pass, P_destroyed re-appears in the recordings table when the
            // supersede relation is dropped.
            var owner = MakeRec("rocket", startUT: 302.0);
            var probeAtmo = MakeRec("P_atmo", startUT: 456.0);
            var probeDestroyed = MakeRec("P_destroyed", startUT: 466.0);
            var probeContinuation = MakeRec("P_continuation", startUT: 960.0);
            var fork = MakeRec("F", startUT: 457.0);
            InstallCommittedTreeForTesting("tree-playtest",
                owner, probeAtmo, probeDestroyed, probeContinuation, fork);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("P_atmo", "F"),
                    MakeRel("P_destroyed", "F"),
                    MakeRel("P_continuation", "F")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(owner, 287.0);

            Assert.Equal(3, dropped);
            Assert.Empty(scenario.RecordingSupersedes);
            // 1 fork retirement + 3 old-side retirements.
            Assert.Equal(4, scenario.RecordingRewindRetirements.Count);

            RecordingRewindRetirement forkRetirement = scenario.RecordingRewindRetirements
                .Single(r => r.Reason == RecordingRewindRetirement.DefaultReason);
            Assert.Equal("F", forkRetirement.RecordingId);

            var oldSideRetirements = scenario.RecordingRewindRetirements
                .Where(r => r.Reason == RecordingRewindRetirement.RewoundOutOldSideReason)
                .ToList();
            Assert.Equal(3, oldSideRetirements.Count);
            Assert.Contains(oldSideRetirements, r => r.RecordingId == "P_atmo");
            Assert.Contains(oldSideRetirements, r => r.RecordingId == "P_destroyed");
            Assert.Contains(oldSideRetirements, r => r.RecordingId == "P_continuation");
            Assert.All(oldSideRetirements, r =>
            {
                Assert.Null(r.RestoredRecordingId);
                Assert.Null(r.SourceSupersedeRelationId);
                Assert.Equal(287.0, r.RewindUT);
            });

            // P_destroyed is the playtest's smoking-gun "Destroyed" outcome —
            // EffectiveState.IsRewindRetired must hide it after rollback.
            Assert.True(EffectiveState.IsRewindRetired(
                probeDestroyed, scenario.RecordingRewindRetirements));

            // Owner stays visible.
            Assert.DoesNotContain(scenario.RecordingRewindRetirements,
                r => r.RecordingId == "rocket");
            Assert.False(EffectiveState.IsRewindRetired(
                owner, scenario.RecordingRewindRetirements));

            // Summary log captures the new field; per-row log captures each old side.
            Assert.Contains(logLines, line =>
                line.Contains("[Rewind]")
                && line.Contains("Rewind supersede rollback")
                && line.Contains("retiredOldSides=3"));
            Assert.Equal(3, logLines.Count(line =>
                line.Contains("[Rewind]")
                && line.Contains("Retired rewound-out old-side rec=")));
        }

        [Fact]
        public void LiveRollback_KeepsOwnerVisible_WhenOwnerWasOldSide()
        {
            // Stacked re-fly: owner is itself the OldRecordingId of a dropped
            // supersede (rewindAdjustedUT < owner.StartUT due to launch lead).
            // The owner-skip in the old-side pass keeps owner out of the
            // retirement list even though it lives in RestoredRecordingIds.
            var owner = MakeRec("owner", startUT: 302.0);
            var fork = MakeRec("F", startUT: 320.0);
            InstallCommittedTreeForTesting("tree-owner-oldside", owner, fork);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("owner", "F")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(owner, 287.0);

            Assert.Equal(1, dropped);
            // Only the fork is retired; owner stays restored despite owner.StartUT > rewindUT.
            RecordingRewindRetirement retirement = Assert.Single(scenario.RecordingRewindRetirements);
            Assert.Equal("F", retirement.RecordingId);
            Assert.Equal(RecordingRewindRetirement.DefaultReason, retirement.Reason);

            Assert.False(EffectiveState.IsRewindRetired(owner, scenario.RecordingRewindRetirements));
            Assert.Contains(logLines, line =>
                line.Contains("[Rewind]")
                && line.Contains("Old-side retirement skipped for owner rec=owner"));
        }

        [Fact]
        public void LiveRollback_KeepsOriginAtBoundary_WhenStartUTEqualsRewindUT()
        {
            // Canonical "rewind to launch": owner.StartUT == rewindAdjustedUT.
            // The strict `>` filter in the old-side pass keeps the owner
            // visible even before the explicit owner-skip fires.
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRec("B", startUT: 31.5);
            var c = MakeRec("C", startUT: 50.0);
            InstallCommittedTreeForTesting("tree-boundary", a, b, c);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("A", "B"),
                    MakeRel("B", "C")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(a, 6.5);

            Assert.Equal(2, dropped);
            // Two fork retirements (B and C), zero old-side retirements:
            //   - A is the owner — explicit owner-skip in the old-side pass.
            //   - B has StartUT=31.5 > rewindUT=6.5 AND is the OldRecordingId of
            //     the dropped B->C relation (so B lands in RestoredRecordingIds),
            //     but B is ALSO the NewRecordingId of the dropped A->B relation
            //     (so B is in RetiredForkRecordingIds first). Pass 1 retires B
            //     as a fork; pass 2's seenRetiredIds.Contains check then skips B.
            //   - C has StartUT=50 > rewindUT=6.5 and is in RetiredForkRecordingIds
            //     only, so it retires as a fork.
            // Both retirements therefore carry DefaultReason, none carry
            // RewoundOutOldSideReason.
            Assert.Equal(2, scenario.RecordingRewindRetirements.Count);
            Assert.All(scenario.RecordingRewindRetirements, r =>
                Assert.Equal(RecordingRewindRetirement.DefaultReason, r.Reason));
            Assert.False(EffectiveState.IsRewindRetired(a, scenario.RecordingRewindRetirements));
        }

        [Fact]
        public void ReapplyRewindSupersedeDropAfterLoad_Idempotent_DoesNotDuplicateOldSideRetirements()
        {
            // Cross-LoadScene re-apply is the second call site of the same
            // rollback. After the first call retires the old side, the second
            // call must early-out via the existing-id guard (the first run
            // already dropped the supersedes; the second sees an empty list).
            var owner = MakeRec("rocket", startUT: 302.0);
            var oldSide = MakeRec("old", startUT: 456.0);
            var fork = MakeRec("F", startUT: 457.0);
            InstallCommittedTreeForTesting("tree-reapply-old", owner, oldSide, fork);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("old", "F")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(owner.StartUT, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(287.0);
            RecordingStore.SetRewindReplayTargetScope(owner);

            int firstDropped = RecordingStore.ReapplyRewindSupersedeDropAfterLoad();
            Assert.Equal(1, firstDropped);
            Assert.Equal(2, scenario.RecordingRewindRetirements.Count);

            int secondDropped = RecordingStore.ReapplyRewindSupersedeDropAfterLoad();
            // Second pass drops nothing (supersede list is empty) but more
            // importantly does not duplicate the existing retirements.
            Assert.Equal(0, secondDropped);
            Assert.Equal(2, scenario.RecordingRewindRetirements.Count);
        }

        [Fact]
        public void ReapplyRewindSupersedeDropAfterLoad_RecreatesRetirement_WhenScenarioRestoresSupersede()
        {
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRec("B", startUT: 31.5);
            InstallCommittedTreeForTesting("tree-reapply", a, b);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("A", "B")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(a.StartUT, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(6.5);
            RecordingStore.SetRewindReplayTargetScope(a);

            int dropped = RecordingStore.ReapplyRewindSupersedeDropAfterLoad();

            Assert.Equal(1, dropped);
            Assert.Empty(scenario.RecordingSupersedes);
            RecordingRewindRetirement retirement = Assert.Single(scenario.RecordingRewindRetirements);
            Assert.Equal("B", retirement.RecordingId);
            Assert.Equal("A", retirement.RestoredRecordingId);
        }

        [Fact]
        public void LiveRollback_CreatedUTFallsBackToRewindAdjustedUT_WhenClockUnavailable()
        {
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRec("B", startUT: 31.5);
            InstallCommittedTreeForTesting("tree-created-ut", a, b);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("A", "B")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(a.StartUT, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(6.5);
            RecordingStore.CurrentUniversalTimeForRewindRetirementOverrideForTesting =
                () => throw new System.InvalidOperationException("clock unavailable");

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(a, 6.5);

            Assert.Equal(1, dropped);
            RecordingRewindRetirement retirement = Assert.Single(scenario.RecordingRewindRetirements);
            Assert.Equal(6.5, retirement.CreatedUT);
            Assert.Contains(logLines, line =>
                line.Contains("[Rewind]")
                && line.Contains("createdUT fallback to rewindAdjustedUT=6.5")
                && line.Contains("InvalidOperationException"));
        }

        [Fact]
        public void CanFastForwardAtUT_RewindRetiredRecording_ReturnsFalse()
        {
            var rec = MakeRec("fork", startUT: 100.0);
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
                rec,
                now: 10.0,
                out string reason,
                isRecording: false);

            Assert.False(canFastForward);
            Assert.Contains("rewound out", reason);
        }

        [Fact]
        public void NullArgs_NoOp()
        {
            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner: null,
                rewindAdjustedUT: 0,
                ownerTreeRecordings: null,
                liveRecordingsById: null,
                supersedes: null);
            Assert.Equal(0, dropped);
        }

        [Fact]
        public void Pure_HandlesNullOwnerTreeRecordings_OwnerOnlyDrop()
        {
            // Pre-tree-mode (or migration-corrupted) owner: live entry passes
            // ownerTreeRecordings=null when the tree can't be resolved by id.
            // Pure path must still drop the owner-row supersede (no branch walk).
            var owner = MakeRec("legacy", startUT: 6.5);
            var fork = MakeRec("fork-A", startUT: 31.5);
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("legacy", "fork-A")
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: null,
                liveRecordingsById: new Dictionary<string, Recording>
                {
                    { "legacy", owner },
                    { "fork-A", fork }
                },
                supersedes: supersedes);

            Assert.Equal(1, dropped);
            Assert.Empty(supersedes);
        }

        [Fact]
        public void OrphanRow_DropsByRelUT_WhenForkMissingFromLiveDict()
        {
            // If the fork is missing from the live dict (e.g. fork was deleted /
            // orphaned out of band), we can't read its StartUT. But the relation's
            // own UT field carries the merge time, which is post-fork-creation —
            // close enough to use as a fallback. Without this, a one-sided orphan
            // row would still suppress OldRecordingId via EffectiveState.IsVisible
            // after rewind.
            var owner = MakeRec("orig", startUT: 6.5);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig", owner }
                // fork-A intentionally missing
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "orig",
                    NewRecordingId = "fork-A",
                    UT = 31.5  // merge time — fork was authored at this UT
                }
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(1, dropped);
            Assert.Empty(supersedes);
        }

        // ---- Cross-LoadScene re-apply path coverage ----

        [Fact]
        public void TryFindCommittedRecordingById_ReturnsRecording_WhenIdMatches()
        {
            var rec = MakeRec("foo", startUT: 6.5);
            RecordingStore.AddCommittedInternal(rec);
            try
            {
                var found = RecordingStore.TryFindCommittedRecordingById("foo");
                Assert.Same(rec, found);
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void TryFindCommittedRecordingById_ReturnsNull_OnNullEmptyOrUnknownId()
        {
            Assert.Null(RecordingStore.TryFindCommittedRecordingById(null));
            Assert.Null(RecordingStore.TryFindCommittedRecordingById(""));
            Assert.Null(RecordingStore.TryFindCommittedRecordingById("missing"));
        }

        [Fact]
        public void ReapplyRewindSupersedeDropAfterLoad_NoOp_WhenNotRewinding()
        {
            // Defensive: outside an active rewind, the re-apply must NOT mutate state.
            // RewindContext.IsRewinding defaults to false in xUnit.
            int dropped = RecordingStore.ReapplyRewindSupersedeDropAfterLoad();
            Assert.Equal(0, dropped);
        }

        [Fact]
        public void ReapplyRewindSupersedeDropAfterLoad_NoOp_WhenOwnerIdEmpty()
        {
            // Sanity: even if RewindContext.IsRewinding is true, with no owner id the
            // re-apply must early-return without touching anything.
            try
            {
                RewindContext.BeginRewind(0, default(BudgetSummary), 0, 0, 0);
                // RewindReplayTargetRecordingId left null intentionally.
                int dropped = RecordingStore.ReapplyRewindSupersedeDropAfterLoad();
                Assert.Equal(0, dropped);
            }
            finally
            {
                RewindContext.EndRewind();
            }
        }

        [Fact]
        public void OrphanRow_KeptWhenRelUT_BeforeRewindUT()
        {
            // Fallback symmetry: if the orphan row's UT is BEFORE the rewindUT,
            // it doesn't represent a "rewound out" merge and must stay.
            var owner = MakeRec("orig", startUT: 6.5);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig", owner }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "orig",
                    NewRecordingId = "fork-A-ancient",
                    UT = 5.0  // pre-rewind merge
                }
            };

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistencePure(
                owner, rewindAdjustedUT: 10.0,
                ownerTreeRecordings: new List<Recording> { owner },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(0, dropped);
            Assert.Single(supersedes);
        }

        private static void InstallCommittedTreeForTesting(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = recordings != null && recordings.Length > 0
                    ? recordings[0].RecordingId
                    : null
            };

            if (recordings != null)
            {
                for (int i = 0; i < recordings.Length; i++)
                {
                    Recording rec = recordings[i];
                    rec.TreeId = treeId;
                    tree.AddOrReplaceRecording(rec);
                    RecordingStore.AddCommittedInternal(rec);
                }
            }

            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // ----------------------------------------------------------------
        // Canon (Immutable) fork preservation tests — fix-rewind-canon-forks.
        //
        // After a Re-Fly merge with a stable terminal (Orbiting/Landed/Splashed),
        // SupersedeCommit flips the fork's MergeState to Immutable. The contract
        // on Immutable is "sealed forever". Parent-tree Rewind must NOT drop
        // such a fork's supersede relation; otherwise the canon recording is
        // silently retired and its spawn-at-recording-end re-materialization
        // never fires.
        //
        // Mixed/multi-generation chains use a Pass 2 demotion rule: if a canon
        // fork's priorTip is itself being retired by a Pass 1 drop, the canon
        // collapses too (it has no live source to be canon over).
        // ----------------------------------------------------------------

        private static Recording MakeRecWithMergeState(string id, double startUT, MergeState state)
        {
            var rec = MakeRec(id, startUT);
            rec.MergeState = state;
            return rec;
        }

        [Fact]
        public void Rollback_PreservesRelation_WhenForkIsImmutable()
        {
            var owner = MakeRec("orig-priorTip", startUT: 6.5);
            var canonFork = MakeRecWithMergeState("canon-fork", startUT: 31.5, MergeState.Immutable);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig-priorTip", owner },
                { "canon-fork", canonFork }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("orig-priorTip", "canon-fork")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner, canonFork },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(0, result.DroppedRelationCount);
            Assert.Single(supersedes);
            Assert.Empty(result.RetiredForkRecordingIds);
            Assert.Empty(result.RestoredRecordingIds);
            Assert.Equal(1, result.SkippedImmutableForkCount);
            Assert.Contains("canon-fork", result.SkippedImmutableForkRecordingIds);
            Assert.Equal(0, result.DemotedImmutablePreservationCount);
        }

        [Fact]
        public void Rollback_DropsRelation_WhenForkIsCommittedProvisional()
        {
            var owner = MakeRec("orig", startUT: 6.5);
            var fork = MakeRecWithMergeState("fork-cp", startUT: 31.5, MergeState.CommittedProvisional);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig", owner }, { "fork-cp", fork }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("orig", "fork-cp")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner, fork },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(1, result.DroppedRelationCount);
            Assert.Empty(supersedes);
            Assert.Contains("fork-cp", result.RetiredForkRecordingIds);
            Assert.Equal(0, result.SkippedImmutableForkCount);
        }

        [Fact]
        public void Rollback_DropsRelation_WhenForkIsNotCommitted()
        {
            var owner = MakeRec("orig", startUT: 6.5);
            var fork = MakeRecWithMergeState("fork-nc", startUT: 31.5, MergeState.NotCommitted);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig", owner }, { "fork-nc", fork }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("orig", "fork-nc")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner, fork },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(1, result.DroppedRelationCount);
            Assert.Contains("fork-nc", result.RetiredForkRecordingIds);
            Assert.Equal(0, result.SkippedImmutableForkCount);
        }

        [Fact]
        public void Rollback_MixedChain_DemotesImmutablePreservation_WhenPriorTipIsRetired()
        {
            // A → B(Provisional) → C(Immutable). Rewind past A's start.
            // Pass 1: A→B drops (B not Immutable), B→C tentatively preserved.
            // Pass 2: B→C's Old is B; B is in pendingRetiredNewIds → demote to drop.
            // Result: A restored, B retired, C retired.
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRecWithMergeState("B", startUT: 31.5, MergeState.CommittedProvisional);
            var c = MakeRecWithMergeState("C", startUT: 50.0, MergeState.Immutable);
            var liveById = new Dictionary<string, Recording>
            {
                { "A", a }, { "B", b }, { "C", c }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("A", "B"),
                MakeRel("B", "C")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                a, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { a, b, c },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(2, result.DroppedRelationCount);
            Assert.Empty(supersedes);
            Assert.Contains("B", result.RetiredForkRecordingIds);
            Assert.Contains("C", result.RetiredForkRecordingIds);
            // Restored set excludes anything also in retired (the restore-then-prune step).
            Assert.Contains("A", result.RestoredRecordingIds);
            Assert.DoesNotContain("B", result.RestoredRecordingIds);
            Assert.Equal(0, result.SkippedImmutableForkCount);
            Assert.Equal(1, result.DemotedImmutablePreservationCount);
            Assert.Contains("C", result.DemotedImmutablePreservationIds);
        }

        [Fact]
        public void Rollback_TwoGenerationCanon_ChainPreservedIntact()
        {
            // A → B(Immutable) → C(Immutable). Rewind past A's start.
            // Pass 1: both classify as Immutable preservations.
            // Pass 2: pendingRetiredNewIds is empty → neither demoted.
            // Result: zero drops, zero retirements; chain stays canon.
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRecWithMergeState("B", startUT: 31.5, MergeState.Immutable);
            var c = MakeRecWithMergeState("C", startUT: 50.0, MergeState.Immutable);
            var liveById = new Dictionary<string, Recording>
            {
                { "A", a }, { "B", b }, { "C", c }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("A", "B"),
                MakeRel("B", "C")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                a, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { a, b, c },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(0, result.DroppedRelationCount);
            Assert.Equal(2, supersedes.Count);
            Assert.Empty(result.RetiredForkRecordingIds);
            Assert.Empty(result.RestoredRecordingIds);
            Assert.Equal(2, result.SkippedImmutableForkCount);
            Assert.Contains("B", result.SkippedImmutableForkRecordingIds);
            Assert.Contains("C", result.SkippedImmutableForkRecordingIds);
            Assert.Equal(0, result.DemotedImmutablePreservationCount);
        }

        [Fact]
        public void Rollback_LogsSkippedImmutableSummary()
        {
            // Live entry point exercises the summary log line — pure helper
            // doesn't log on its own.
            var owner = MakeRecWithMergeState("orig", startUT: 6.5, MergeState.Immutable);
            var canonFork = MakeRecWithMergeState("canon-fork", startUT: 31.5, MergeState.Immutable);
            InstallCommittedTreeForTesting("tree-1", owner, canonFork);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("orig", "canon-fork")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(owner.StartUT, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(6.5);

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(owner, 6.5);

            Assert.Equal(0, dropped);
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("skippedImmutable=1") &&
                l.Contains("dropped=0") && l.Contains("retiredForks=0"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("Preserved canon fork across parent rewind") &&
                l.Contains("rec=canon-fork") &&
                l.Contains("mergeState=Immutable"));
        }

        [Fact]
        public void Rollback_DropsImmutable_WhenLiveLookupMissing()
        {
            // Orphan fallback: relation OldRecordingId is in rewoundOutOldIds but
            // newRec is missing from liveRecordingsById. We can't read MergeState,
            // so the relation drops as a dangling row (pre-fix behaviour).
            var owner = MakeRec("orig", startUT: 6.5);
            var liveById = new Dictionary<string, Recording>
            {
                { "orig", owner }
                // "fork-orphan" intentionally NOT in liveById
            };
            var rel = MakeRel("orig", "fork-orphan");
            rel.UT = 31.5; // forces effectiveForkUT = 31.5 ≥ rewindUT
            var supersedes = new List<RecordingSupersedeRelation> { rel };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                owner, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { owner },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            Assert.Equal(1, result.DroppedRelationCount);
            Assert.Empty(supersedes);
            Assert.Contains("fork-orphan", result.RetiredForkRecordingIds);
            Assert.Equal(0, result.SkippedImmutableForkCount);
        }

        [Fact]
        public void Rollback_ThreeGenChain_TransitiveDemotion_AllNonCanonRetire()
        {
            // A → B(Provisional) → C(Immutable) → D(Immutable). Rewind past A.
            //
            // This chain is the genuine multi-iteration fixpoint case (Pass 1
            // drop is at the START of the chain, so demotions must cascade
            // through C and D in two iterations):
            //   Pass 1: A→B drop (B Prov), B→C preserve (C Imm), C→D preserve (D Imm).
            //   Pass 2 init: pendingRetiredNewIds = {B}.
            //   Iter 1 (reverse walk):
            //     C→D Old=C ∉ {B} — keep.
            //     B→C Old=B ∈ {B} — demote, pendingRetiredNewIds = {B,C}.
            //   Iter 2 (preservations now = [C→D]):
            //     C→D Old=C ∈ {B,C} — demote, pendingRetiredNewIds = {B,C,D}.
            //   Iter 3: empty preservations — terminate.
            //
            // Single-pass logic (initial pendingRetiredNewIds={B}, no update
            // during iteration) would only demote B→C in iter 1 because the
            // set never grows. C→D would survive as a preservation, so D
            // would render as canon alongside the restored A — exactly the
            // double-materialization regression PR #776/#777 fixes. The
            // fixpoint loop is the only thing closing this gap.
            //
            // Critical to the chain shape: B is Provisional (the Pass-1 drop
            // anchor), not Immutable. Putting an Immutable B + Provisional C
            // would make C the Pass-1 drop and trivially seed
            // pendingRetiredNewIds={C}, which collapses the cascade to a
            // single iteration and the test no longer distinguishes
            // single-pass from fixpoint.
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRecWithMergeState("B", startUT: 31.5, MergeState.CommittedProvisional);
            var c = MakeRecWithMergeState("C", startUT: 50.0, MergeState.Immutable);
            var d = MakeRecWithMergeState("D", startUT: 75.0, MergeState.Immutable);
            var liveById = new Dictionary<string, Recording>
            {
                { "A", a }, { "B", b }, { "C", c }, { "D", d }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("A", "B"),
                MakeRel("B", "C"),
                MakeRel("C", "D")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                a, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { a, b, c, d },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            // All three relations dropped (A→B Pass 1; B→C and C→D Pass 2
            // demotions across two fixpoint iterations).
            Assert.Equal(3, result.DroppedRelationCount);
            Assert.Empty(supersedes);
            // B Pass-1 drop. C and D both transitively demoted.
            Assert.Contains("B", result.RetiredForkRecordingIds);
            Assert.Contains("C", result.RetiredForkRecordingIds);
            Assert.Contains("D", result.RetiredForkRecordingIds);
            // No canon survives — the chain's first canon link (C) lost its
            // priorTip (B) to a Pass-1 drop, so the entire downstream Imm
            // tail collapses transitively.
            Assert.Empty(result.SkippedImmutableForkRecordingIds);
            Assert.Equal(0, result.SkippedImmutableForkCount);
            // 2 demotions: B→C in iter 1, C→D in iter 2.
            Assert.Equal(2, result.DemotedImmutablePreservationCount);
            Assert.Contains("C", result.DemotedImmutablePreservationIds);
            Assert.Contains("D", result.DemotedImmutablePreservationIds);
            // A is the priorTip of A→B (the Pass-1 drop). After dropping +
            // pruning retired ids, RestoredRecordingIds = {A}.
            Assert.Contains("A", result.RestoredRecordingIds);
        }

        [Fact]
        public void Rollback_FourGenWithCanonHead_TransitiveDemotionStopsAtCanonBoundary()
        {
            // A(Imm) → B(Imm) → C(Provisional) → D(Imm). Rewind past A.
            //
            //   Pass 1: A→B preserve, B→C drop, C→D preserve.
            //   Pass 2 init: pendingRetiredNewIds = {C}.
            //   Iter 1: A→B Old=A ∉ {C} — keep. C→D Old=C ∈ {C} — demote,
            //     pendingRetiredNewIds = {C,D}.
            //   Iter 2: A→B Old=A ∉ {C,D} — still keep. terminate.
            //
            // The cascade does NOT propagate through A→B because A (the
            // owner being rewound) is not itself in the retired set — the
            // canon head B survives. This is the correct semantic: B was
            // sealed at a stable terminal before the user re-flew C and D;
            // the user wants to keep B and re-do everything after.
            var a = MakeRecWithMergeState("A", startUT: 6.5, MergeState.Immutable);
            var b = MakeRecWithMergeState("B", startUT: 31.5, MergeState.Immutable);
            var c = MakeRecWithMergeState("C", startUT: 50.0, MergeState.CommittedProvisional);
            var d = MakeRecWithMergeState("D", startUT: 75.0, MergeState.Immutable);
            var liveById = new Dictionary<string, Recording>
            {
                { "A", a }, { "B", b }, { "C", c }, { "D", d }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("A", "B"),
                MakeRel("B", "C"),
                MakeRel("C", "D")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                a, rewindAdjustedUT: 6.5,
                ownerTreeRecordings: new List<Recording> { a, b, c, d },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            // A→B preserved (B stays canon). B→C and C→D dropped (C Pass-1, D Pass-2).
            Assert.Equal(2, result.DroppedRelationCount);
            Assert.Single(supersedes);
            Assert.Equal("A", supersedes[0].OldRecordingId);
            Assert.Equal("B", supersedes[0].NewRecordingId);
            Assert.Contains("B", result.SkippedImmutableForkRecordingIds);
            Assert.Contains("C", result.RetiredForkRecordingIds);
            Assert.Contains("D", result.RetiredForkRecordingIds);
            Assert.Equal(1, result.DemotedImmutablePreservationCount);
            Assert.Contains("D", result.DemotedImmutablePreservationIds);
            Assert.Equal(1, result.SkippedImmutableForkCount);
        }

        [Fact]
        public void LiveRollback_DemotedImmutableFork_WritesRetirement()
        {
            // The live path's defensive Immutable guard in
            // EnsureRewindRetirementsForRollback must NOT skip retirement for
            // Immutable forks that the upstream classifier explicitly demoted
            // — those are intentional drops where the priorTip is itself
            // retired, so the canon must collapse to preserve the
            // no-double-materialization invariant.
            //
            // Without the demoted-id bypass, the live path would: drop B→C
            // and C→D, then refuse to retire D (Immutable defense), then
            // re-insert C→D into supersedes. End state: A restored, B retired,
            // C retired (D's priorTip), D visible (relation preserved by the
            // defense, but C is retired so D effectively has no live source —
            // double-materialization regression).
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRecWithMergeState("B", startUT: 31.5, MergeState.Immutable);
            var c = MakeRecWithMergeState("C", startUT: 50.0, MergeState.CommittedProvisional);
            var d = MakeRecWithMergeState("D", startUT: 75.0, MergeState.Immutable);
            InstallCommittedTreeForTesting("tree-3gen", a, b, c, d);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("A", "B"),
                    MakeRel("B", "C"),
                    MakeRel("C", "D")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(a.StartUT, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(6.5);

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(a, 6.5);

            // Pass 1 forks dropped: B→C drops (C Prov). Pass 2 demote: C→D
            // (Old=C ∈ pendingRetiredNewIds={C}, demoted). Total 2 drops.
            Assert.Equal(2, dropped);
            // A→B preserved (B canon, A→B's New is Imm and its Old A is not
            // in pendingRetiredNewIds at any iteration — Pass 2 fixpoint stops
            // at the canon boundary).
            Assert.Single(scenario.RecordingSupersedes);
            // Retirements: 2 forks (C from Pass 1, D from Pass 2 demote) + 1
            // old-side (B). The new old-side pass added by PR #807 retires
            // OldRecordingIds in RestoredRecordingIds whose StartUT >
            // rewindAdjustedUT. After the apply-drops loop's prune step,
            // RestoredRecordingIds={B} (B was Old of B→C and not in
            // RetiredForkRecordingIds={C,D}); B.StartUT=31.5>6.5 so B retires.
            Assert.Equal(3, scenario.RecordingRewindRetirements.Count);
            var retiredIds = new HashSet<string>(
                scenario.RecordingRewindRetirements.ConvertAll(r => r.RecordingId),
                System.StringComparer.Ordinal);
            Assert.Contains("B", retiredIds); // old-side
            Assert.Contains("C", retiredIds); // fork — Pass 1 drop
            Assert.Contains("D", retiredIds); // fork — Pass 2 demote
            // Reasons distinguish demotion intent from regular retirement.
            // C: Pass 1 drop, default reason. D: explicit demotion → DemotedCanonReason.
            // B: old-side pass → RewoundOutOldSideReason.
            var bRetirement = scenario.RecordingRewindRetirements.Find(r => r.RecordingId == "B");
            var cRetirement = scenario.RecordingRewindRetirements.Find(r => r.RecordingId == "C");
            var dRetirement = scenario.RecordingRewindRetirements.Find(r => r.RecordingId == "D");
            Assert.Equal(RecordingRewindRetirement.RewoundOutOldSideReason, bRetirement.Reason);
            Assert.Equal(RecordingRewindRetirement.DefaultReason, cRetirement.Reason);
            Assert.Equal(RecordingRewindRetirement.DemotedCanonReason, dRetirement.Reason);
            // Defensive Immutable warning must NOT fire — D was explicitly
            // demoted, so the bypass kicks in and retirement proceeds normally.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("Skipping retirement for Immutable canon recording"));
        }

        [Fact]
        public void LiveRollback_DemotedImmutableFork_RetirementCarriesDemotedCanonReason()
        {
            // Pass-2 demoted Immutable retirements MUST carry
            // RecordingRewindRetirement.DemotedCanonReason — not the default
            // reason. LoadTimeSweep's legacy-Immutable cleanup uses the
            // reason tag to distinguish intentional Pass-2 demotions from
            // pre-fix bad state. Without the tag, a save/load round-trip on
            // a legitimate mixed-chain rollback would have the sweep undo
            // the demotion (remove the retirement and reconstruct the
            // priorTip → canon supersede), making the demoted canon visible
            // again and silently re-introducing the regression in-game.
            var a = MakeRec("A", startUT: 6.5);
            var b = MakeRecWithMergeState("B", startUT: 31.5, MergeState.CommittedProvisional);
            var c = MakeRecWithMergeState("C", startUT: 50.0, MergeState.Immutable);
            InstallCommittedTreeForTesting("tree-mixed", a, b, c);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    MakeRel("A", "B"),
                    MakeRel("B", "C")
                },
                RecordingRewindRetirements = new List<RecordingRewindRetirement>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindContext.BeginRewind(a.StartUT, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(6.5);

            int dropped = RecordingStore.DropSupersedesRewoundOutOfExistence(a, 6.5);

            Assert.Equal(2, dropped);
            Assert.Equal(2, scenario.RecordingRewindRetirements.Count);
            // B is the Pass-1 drop — default reason.
            var bRetirement = scenario.RecordingRewindRetirements.Find(
                r => r.RecordingId == "B");
            Assert.NotNull(bRetirement);
            Assert.Equal(RecordingRewindRetirement.DefaultReason, bRetirement.Reason);
            // C is the Pass-2 demoted Immutable — DemotedCanonReason.
            var cRetirement = scenario.RecordingRewindRetirements.Find(
                r => r.RecordingId == "C");
            Assert.NotNull(cRetirement);
            Assert.Equal(RecordingRewindRetirement.DemotedCanonReason, cRetirement.Reason);
        }

        [Fact]
        public void Rollback_OwnerIsImmutableFork_RewindOnSelfStillDrops()
        {
            // Edge case: the owner of the rewind is itself an Immutable canon
            // fork. Self-rewind. Relations where owner is the Old should still
            // drop — the user is asking to undo this canon recording. The
            // Immutable guard protects forks from being retired by a different
            // recording's rewind, not from self-rewind.
            var canonOwner = MakeRecWithMergeState("canon-owner", startUT: 31.5, MergeState.Immutable);
            var downstream = MakeRecWithMergeState("downstream", startUT: 50.0, MergeState.NotCommitted);
            var liveById = new Dictionary<string, Recording>
            {
                { "canon-owner", canonOwner }, { "downstream", downstream }
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                MakeRel("canon-owner", "downstream")
            };

            var result = RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure(
                canonOwner, rewindAdjustedUT: 31.5,
                ownerTreeRecordings: new List<Recording> { canonOwner, downstream },
                liveRecordingsById: liveById,
                supersedes: supersedes);

            // canon-owner.RecordingId is in rewoundOutOldIds (owner self-add).
            // Relation canon-owner → downstream classifies as drop (downstream NotCommitted).
            Assert.Equal(1, result.DroppedRelationCount);
            Assert.Empty(supersedes);
            Assert.Contains("downstream", result.RetiredForkRecordingIds);
            Assert.Equal(0, result.SkippedImmutableForkCount);
        }
    }
}
