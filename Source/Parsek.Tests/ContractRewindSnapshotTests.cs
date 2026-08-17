using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the rewind-point contract re-snapshot (P9b).
    ///
    /// <para>
    /// The defect: reinstating a merge-tombstoned contract rebuilt it from the ACCEPT-TIME
    /// snapshot, which is zero-progress by construction. A contract accepted long before a
    /// rewind point, worked halfway, then completed by a branch the merge supersedes came
    /// back from the merge with every parameter reset. The fix captures a second snapshot
    /// population at RP time and selects the newest one at or before the contract's
    /// reinstate cutoff.
    /// </para>
    ///
    /// Three separable pieces are covered here, all headless: the pure selection rule, the
    /// pure per-guid cutoff fold, and the store/capture/purge lifecycle.
    /// </summary>
    [Collection("Sequential")]
    public class ContractRewindSnapshotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ContractRewindSnapshotTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ConfigNode Snap(string marker)
        {
            var node = new ConfigNode("CONTRACT");
            node.AddValue("marker", marker);
            return node;
        }

        // ================================================================
        // SelectContractSnapshotIndexForPatch — the pure selection rule
        // ================================================================

        private static List<ContractSnapshot> Rows(params ContractSnapshot[] rows)
        {
            return new List<ContractSnapshot>(rows);
        }

        private static ContractSnapshot Row(string guid, double ut, string rpId, string marker)
        {
            return new ContractSnapshot
            {
                contractGuid = guid,
                contractNode = Snap(marker),
                ut = ut,
                sourceRpId = rpId
            };
        }

        [Fact]
        public void Select_NoCutoff_UsesAcceptTimeSnapshot()
        {
            // No cutoff means no tombstone rewound this contract, so nothing justifies
            // reinstating RP-era progress. This is the historical behavior verbatim — the
            // property that makes the new rule "never worse". Fails if RP rows start
            // winning unconditionally.
            var rows = Rows(
                Row("g", 100.0, null, "accept"),
                Row("g", 500.0, "rp-1", "at-rp"));

            int idx = GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", null);

            Assert.Equal(0, idx);
            Assert.Equal("accept", rows[idx].contractNode.GetValue("marker"));
        }

        [Fact]
        public void Select_CutoffAfterRpSnapshot_UsesRpSnapshot()
        {
            // THE fix: a cutoff at or after the RP snapshot selects the at-RP state, so the
            // reinstated contract carries the progress it actually had.
            var rows = Rows(
                Row("g", 100.0, null, "accept"),
                Row("g", 500.0, "rp-1", "at-rp"));

            int idx = GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 500.0);

            Assert.Equal(1, idx);
            Assert.Equal("at-rp", rows[idx].contractNode.GetValue("marker"));
        }

        [Fact]
        public void Select_CutoffBeforeEveryRpSnapshot_FallsBackToAcceptTime()
        {
            // A cutoff earlier than every RP snapshot means the surviving timeline never
            // reached any of them; reinstating one would restore progress from a future the
            // merge discarded. Fails if the ut <= cutoff bound is dropped.
            var rows = Rows(
                Row("g", 100.0, null, "accept"),
                Row("g", 900.0, "rp-late", "late"));

            int idx = GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 400.0);

            Assert.Equal(0, idx);
            Assert.Equal("accept", rows[idx].contractNode.GetValue("marker"));
        }

        [Fact]
        public void Select_MultipleEligibleRpSnapshots_PicksTheNewest()
        {
            // Several rewind points before the cutoff: the LAST state the timeline reached
            // is the right one. Fails if the comparison picks the first match instead.
            var rows = Rows(
                Row("g", 100.0, null, "accept"),
                Row("g", 300.0, "rp-1", "early"),
                Row("g", 700.0, "rp-2", "middle"),
                Row("g", 950.0, "rp-3", "too-late"));

            int idx = GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 800.0);

            Assert.Equal("middle", rows[idx].contractNode.GetValue("marker"));
        }

        [Fact]
        public void Select_OtherContractsRpSnapshots_AreNotConsidered()
        {
            // Guid isolation: an RP captures every live Active contract, so the list is
            // dense with other guids at the same UT. Fails if the guid filter is dropped —
            // a contract would be rebuilt from another contract's ConfigNode.
            var rows = Rows(
                Row("g", 100.0, null, "accept-g"),
                Row("other", 500.0, "rp-1", "at-rp-other"));

            int idx = GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 500.0);

            Assert.Equal("accept-g", rows[idx].contractNode.GetValue("marker"));
        }

        [Fact]
        public void Select_NoAcceptTimeAndNoEligibleRp_ReturnsMinusOne()
        {
            var rows = Rows(Row("g", 900.0, "rp-late", "late"));

            Assert.Equal(-1, GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 100.0));
            Assert.Equal(-1, GameStateStore.SelectContractSnapshotIndexForPatch(rows, "missing", 999.0));
            Assert.Equal(-1, GameStateStore.SelectContractSnapshotIndexForPatch(null, "g", 999.0));
            Assert.Equal(-1, GameStateStore.SelectContractSnapshotIndexForPatch(rows, "", 999.0));
        }

        [Fact]
        public void Select_NullNodeRows_AreSkipped()
        {
            // A row whose node failed to deserialize must not be chosen — the caller would
            // read it as "no snapshot" only AFTER discarding a usable accept-time row.
            var rows = Rows(
                Row("g", 100.0, null, "accept"),
                new ContractSnapshot { contractGuid = "g", contractNode = null, ut = 500.0, sourceRpId = "rp-1" });

            int idx = GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 500.0);

            Assert.Equal(0, idx);
        }

        [Fact]
        public void Select_LegacyZeroUtRow_BehavesAsOldestPossible()
        {
            // Persisted rows written before the ut field existed load as ut=0. As an
            // accept-time row that is the fallback (correct). As an RP row it would only be
            // chosen when no better row exists, which is the intended degradation.
            var rows = Rows(
                new ContractSnapshot { contractGuid = "g", contractNode = Snap("legacy"), ut = 0.0, sourceRpId = null });

            Assert.Equal(0, GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", null));
            Assert.Equal(0, GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 1000.0));
        }

        [Fact]
        public void Select_NaNSnapshotUt_IsNeverEligible()
        {
            var rows = Rows(
                Row("g", 100.0, null, "accept"),
                Row("g", double.NaN, "rp-1", "nan"));

            int idx = GameStateStore.SelectContractSnapshotIndexForPatch(rows, "g", 1e9);

            Assert.Equal(0, idx);
        }

        // ================================================================
        // FoldContractReinstateCutoff — per-guid MINIMUM
        // ================================================================

        [Fact]
        public void FoldCutoff_KeepsTheEarliestRetiringStartUt()
        {
            // A contract retired by two different merges must be rebuilt at the EARLIEST
            // rewind, not the latest — the later merge's cutoff would readmit progress the
            // earlier rewind already discarded. Fails if this becomes a max or a last-wins.
            var cutoffs = new Dictionary<Guid, double>();
            var g = Guid.NewGuid();

            LedgerOrchestrator.FoldContractReinstateCutoff(cutoffs, g, 900.0);
            LedgerOrchestrator.FoldContractReinstateCutoff(cutoffs, g, 400.0);
            LedgerOrchestrator.FoldContractReinstateCutoff(cutoffs, g, 700.0);

            Assert.Equal(400.0, cutoffs[g]);
        }

        [Fact]
        public void FoldCutoff_NaNAndNullAreIgnored()
        {
            var cutoffs = new Dictionary<Guid, double>();
            var g = Guid.NewGuid();

            LedgerOrchestrator.FoldContractReinstateCutoff(cutoffs, g, double.NaN);
            Assert.Empty(cutoffs);

            LedgerOrchestrator.FoldContractReinstateCutoff(null, g, 100.0); // must not throw
        }

        [Fact]
        public void FoldCutoff_DistinctGuidsAreIndependent()
        {
            var cutoffs = new Dictionary<Guid, double>();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();

            LedgerOrchestrator.FoldContractReinstateCutoff(cutoffs, a, 100.0);
            LedgerOrchestrator.FoldContractReinstateCutoff(cutoffs, b, 900.0);

            Assert.Equal(100.0, cutoffs[a]);
            Assert.Equal(900.0, cutoffs[b]);
        }

        // ================================================================
        // Store lifecycle: two populations, dedup, purge
        // ================================================================

        [Fact]
        public void AddContractSnapshotAtRp_AppendsBesideTheAcceptTimeRow()
        {
            GameStateStore.AddContractSnapshot("g", Snap("accept"), 100.0);
            GameStateStore.AddContractSnapshotAtRp("g", Snap("at-rp"), 500.0, "rp-1");

            Assert.Equal(2, GameStateStore.ContractSnapshots.Count);

            // GetContractSnapshot keeps its accept-time-only contract for every legacy caller.
            Assert.Equal("accept", GameStateStore.GetContractSnapshot("g").GetValue("marker"));
            // The patch-side reader honours the cutoff.
            Assert.Equal("at-rp",
                GameStateStore.GetContractSnapshotForPatch("g", 500.0).GetValue("marker"));
            Assert.Equal("accept",
                GameStateStore.GetContractSnapshotForPatch("g", null).GetValue("marker"));
        }

        [Fact]
        public void AddContractSnapshot_DoesNotReplaceRpRows()
        {
            // Re-accepting a contract after a failure replaces its accept-time row. It must
            // NOT clobber the RP rows, which belong to their rewind points. Fails if the
            // replace loop drops its sourceRpId guard.
            GameStateStore.AddContractSnapshotAtRp("g", Snap("at-rp"), 500.0, "rp-1");
            GameStateStore.AddContractSnapshot("g", Snap("accept-1"), 100.0);
            GameStateStore.AddContractSnapshot("g", Snap("accept-2"), 600.0);

            Assert.Equal(2, GameStateStore.ContractSnapshots.Count);
            Assert.Equal("accept-2", GameStateStore.GetContractSnapshot("g").GetValue("marker"));
            Assert.Equal("at-rp",
                GameStateStore.GetContractSnapshotForPatch("g", 500.0).GetValue("marker"));
        }

        [Fact]
        public void AddContractSnapshotAtRp_IsIdempotentPerRewindPoint()
        {
            // Deduped on (guid, rpId): a repeated capture for the same RP replaces rather
            // than accumulating, so a retried deferred body cannot grow the store.
            GameStateStore.AddContractSnapshotAtRp("g", Snap("first"), 500.0, "rp-1");
            GameStateStore.AddContractSnapshotAtRp("g", Snap("second"), 500.0, "rp-1");

            Assert.Equal(1, GameStateStore.ContractSnapshots.Count);
            Assert.Equal("second",
                GameStateStore.GetContractSnapshotForPatch("g", 500.0).GetValue("marker"));
        }

        [Fact]
        public void AddContractSnapshotAtRp_RejectsIncompleteInput()
        {
            GameStateStore.AddContractSnapshotAtRp(null, Snap("x"), 1.0, "rp");
            GameStateStore.AddContractSnapshotAtRp("g", null, 1.0, "rp");
            GameStateStore.AddContractSnapshotAtRp("g", Snap("x"), 1.0, null);

            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
        }

        [Fact]
        public void PurgeContractSnapshotsForRewindPoint_DropsOnlyThatRpsRows()
        {
            GameStateStore.AddContractSnapshot("g", Snap("accept"), 100.0);
            GameStateStore.AddContractSnapshotAtRp("g", Snap("rp1"), 300.0, "rp-1");
            GameStateStore.AddContractSnapshotAtRp("h", Snap("rp1-h"), 300.0, "rp-1");
            GameStateStore.AddContractSnapshotAtRp("g", Snap("rp2"), 700.0, "rp-2");

            int removed = GameStateStore.PurgeContractSnapshotsForRewindPoint("rp-1");

            Assert.Equal(2, removed);
            Assert.Equal(2, GameStateStore.ContractSnapshots.Count);
            Assert.NotNull(GameStateStore.GetContractSnapshot("g"));
            Assert.Equal("rp2",
                GameStateStore.GetContractSnapshotForPatch("g", 1000.0).GetValue("marker"));
        }

        [Fact]
        public void PurgeOrphanedContractSnapshots_TakesRpRowsWithTheAccept()
        {
            // An RP snapshot of a contract whose ACCEPT event was purged has nothing left to
            // reinstate. Fails if the purge starts matching on sourceRpId instead of guid.
            GameStateStore.AddContractSnapshot("g", Snap("accept"), 100.0);
            GameStateStore.AddContractSnapshotAtRp("g", Snap("rp1"), 300.0, "rp-1");
            GameStateStore.AddContractSnapshot("keep", Snap("accept-keep"), 100.0);

            var purged = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 100.0,
                    eventType = GameStateEventType.ContractAccepted,
                    key = "g"
                }
            };

            int removed = GameStateStore.PurgeOrphanedContractSnapshots(purged);

            Assert.Equal(2, removed);
            Assert.Equal(1, GameStateStore.ContractSnapshots.Count);
            Assert.NotNull(GameStateStore.GetContractSnapshot("keep"));
        }

        // ================================================================
        // Serialization roundtrip, including legacy nodes
        // ================================================================

        [Fact]
        public void ContractSnapshot_RoundtripsUtAndSourceRpId()
        {
            var original = new ContractSnapshot
            {
                contractGuid = "g",
                contractNode = Snap("payload"),
                ut = 1234.5,
                sourceRpId = "rp-7"
            };

            var parent = new ConfigNode("ROOT");
            original.SerializeInto(parent);
            var restored = ContractSnapshot.DeserializeFrom(parent.GetNode("CONTRACT_SNAPSHOT"));

            Assert.Equal("g", restored.contractGuid);
            Assert.Equal(1234.5, restored.ut, 6);
            Assert.Equal("rp-7", restored.sourceRpId);
            Assert.Equal("payload", restored.contractNode.GetValue("marker"));
        }

        [Fact]
        public void ContractSnapshot_AcceptTimeRow_OmitsSourceRpIdValue()
        {
            var original = new ContractSnapshot
            {
                contractGuid = "g",
                contractNode = Snap("payload"),
                ut = 42.0,
                sourceRpId = null
            };

            var parent = new ConfigNode("ROOT");
            original.SerializeInto(parent);
            var node = parent.GetNode("CONTRACT_SNAPSHOT");

            Assert.False(node.HasValue("sourceRpId"));
            Assert.Null(ContractSnapshot.DeserializeFrom(node).sourceRpId);
        }

        [Fact]
        public void ContractSnapshot_LegacyNodeWithoutUtOrRpId_LoadsAsAcceptTimeAtZero()
        {
            // Every snapshot in an existing save has exactly this shape. Fails if the
            // deserializer starts requiring the new values.
            var node = new ConfigNode("CONTRACT_SNAPSHOT");
            node.AddValue("guid", "legacy-guid");
            node.AddNode(Snap("legacy-payload"));

            var restored = ContractSnapshot.DeserializeFrom(node);

            Assert.Equal("legacy-guid", restored.contractGuid);
            Assert.Equal(0.0, restored.ut);
            Assert.Null(restored.sourceRpId);
            Assert.Equal("legacy-payload", restored.contractNode.GetValue("marker"));
        }

        [Fact]
        public void ContractSnapshot_UnparsableUt_DefaultsToZeroRatherThanThrowing()
        {
            var node = new ConfigNode("CONTRACT_SNAPSHOT");
            node.AddValue("guid", "g");
            node.AddValue("ut", "not-a-number");
            node.AddNode(Snap("payload"));

            Assert.Equal(0.0, ContractSnapshot.DeserializeFrom(node).ut);
        }

        // ================================================================
        // The RP capture pass (driven through the injected contract source)
        // ================================================================

        private static RewindPoint Rp(string id, double ut)
        {
            return new RewindPoint { RewindPointId = id, UT = ut };
        }

        [Fact]
        public void CaptureActiveContractSnapshots_StoresEveryContractAtTheRpUt()
        {
            var ctx = new RewindPointAuthorContext
            {
                ActiveContractSource = () => new List<RewindPointAuthor.ActiveContractCapture>
                {
                    new RewindPointAuthor.ActiveContractCapture { Guid = "g", Node = Snap("g-at-rp") },
                    new RewindPointAuthor.ActiveContractCapture { Guid = "h", Node = Snap("h-at-rp") }
                }
            };

            RewindPointAuthor.CaptureActiveContractSnapshots(Rp("rp-1", 555.0), ctx);

            Assert.Equal(2, GameStateStore.ContractSnapshots.Count);
            Assert.Equal("g-at-rp",
                GameStateStore.GetContractSnapshotForPatch("g", 555.0).GetValue("marker"));
            Assert.Equal("h-at-rp",
                GameStateStore.GetContractSnapshotForPatch("h", 555.0).GetValue("marker"));
            Assert.Contains(logLines, l => l.Contains("Contract re-snapshot") && l.Contains("stored=2"));
        }

        [Fact]
        public void CaptureActiveContractSnapshots_SkipsMalformedEntriesWithoutLosingTheRest()
        {
            var ctx = new RewindPointAuthorContext
            {
                ActiveContractSource = () => new List<RewindPointAuthor.ActiveContractCapture>
                {
                    new RewindPointAuthor.ActiveContractCapture { Guid = null, Node = Snap("no-guid") },
                    new RewindPointAuthor.ActiveContractCapture { Guid = "h", Node = null },
                    new RewindPointAuthor.ActiveContractCapture { Guid = "ok", Node = Snap("ok") }
                }
            };

            RewindPointAuthor.CaptureActiveContractSnapshots(Rp("rp-1", 10.0), ctx);

            Assert.Equal(1, GameStateStore.ContractSnapshots.Count);
            Assert.Contains(logLines, l => l.Contains("stored=1") && l.Contains("failed=2"));
        }

        [Fact]
        public void CaptureActiveContractSnapshots_EnumerationThrow_DegradesToAcceptTime()
        {
            // A throwing enumeration must leave the store untouched and warn, not abort the
            // rewind-point capture — the RP is still usable, contracts just fall back.
            var ctx = new RewindPointAuthorContext
            {
                ActiveContractSource = () => throw new InvalidOperationException("boom")
            };

            RewindPointAuthor.CaptureActiveContractSnapshots(Rp("rp-1", 10.0), ctx);

            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
            Assert.Contains(logLines, l => l.Contains("boom") && l.Contains("accept-time"));
        }

        [Fact]
        public void CaptureActiveContractSnapshots_NoRpOrNoId_IsANoOp()
        {
            var ctx = new RewindPointAuthorContext
            {
                ActiveContractSource = () => new List<RewindPointAuthor.ActiveContractCapture>
                {
                    new RewindPointAuthor.ActiveContractCapture { Guid = "g", Node = Snap("g") }
                }
            };

            RewindPointAuthor.CaptureActiveContractSnapshots(null, ctx);
            RewindPointAuthor.CaptureActiveContractSnapshots(Rp(null, 10.0), ctx);

            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
        }

        [Fact]
        public void CaptureThenSelect_ReinstatesAtRpProgressNotAcceptTimeZero()
        {
            // End-to-end over the headless pieces: accept at UT 100 with no progress, rewind
            // point at UT 500 with progress, cutoff at the rewind. This is the whole point of
            // P9b in one assertion.
            GameStateStore.AddContractSnapshot("g", Snap("progress=0"), 100.0);

            var ctx = new RewindPointAuthorContext
            {
                ActiveContractSource = () => new List<RewindPointAuthor.ActiveContractCapture>
                {
                    new RewindPointAuthor.ActiveContractCapture { Guid = "g", Node = Snap("progress=half") }
                }
            };
            RewindPointAuthor.CaptureActiveContractSnapshots(Rp("rp-1", 500.0), ctx);

            var cutoffs = new Dictionary<Guid, double>();
            var contractGuid = Guid.NewGuid();
            LedgerOrchestrator.FoldContractReinstateCutoff(cutoffs, contractGuid, 500.0);

            var chosen = GameStateStore.GetContractSnapshotForPatch("g", cutoffs[contractGuid]);

            Assert.Equal("progress=half", chosen.GetValue("marker"));
        }
    }
}
