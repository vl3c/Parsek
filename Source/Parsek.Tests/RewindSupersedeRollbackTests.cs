using System.Collections.Generic;
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
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = prevSuppress;
            RecordingStore.ResetForTesting();
        }

        private static Recording MakeRec(string id, double startUT)
        {
            // Recording.StartUT is computed from ExplicitStartUT (when set) or first
            // trajectory point. ExplicitStartUT pins StartUT deterministically.
            return new Recording
            {
                RecordingId = id,
                ExplicitStartUT = startUT,
                VesselName = $"vessel-{id}"
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
    }
}
