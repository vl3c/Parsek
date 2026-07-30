using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Seeded random-graph fuzzer for the <see cref="EffectiveState"/> supersede /
    /// chain / closure graph walkers (test-coverage audit 2026-07-29, Tier D row
    /// D1; design-testing-unified.md section 8 Phase 2 item 5).
    ///
    /// <para>
    /// Motivation: the walkers are cycle-prone by construction - every one of them
    /// carries a visited-set guard precisely because corrupt supersede / chain
    /// state has occurred in the wild - yet the pre-existing proofs are all
    /// hand-built fixtures of at most four nodes (A -&gt; B -&gt; A and friends).
    /// This harness generates deterministic pseudo-random graphs of up to ~100
    /// recordings carrying the full corrupt-shape catalog (self loops, 2-cycles,
    /// 3-cycles, dangling ids, diamonds, duplicate outgoing edges, null rows,
    /// empty targets, chains crossing supersede rows, tree-id mismatches) and
    /// asserts structural invariants that must hold no matter how broken the
    /// input is.
    /// </para>
    ///
    /// <para>
    /// Determinism: every graph is built from a fixed seed listed in
    /// <see cref="CorruptSeeds"/> / <see cref="ValidSeeds"/>. Every assertion
    /// failure carries a compact graph dump so the exact case reproduces from the
    /// seed alone.
    /// </para>
    ///
    /// <para>
    /// Termination is proved in two layers, because a walker that loses its
    /// visited-set guard spins forever rather than returning a wrong answer.
    /// (1) The supersede table is handed to the walkers through
    /// <see cref="HopBoundedSupersedeList"/>, a read-counting proxy: the walkers
    /// rescan the table once per hop, so a read cap IS an observable hop bound,
    /// and a runaway walk throws on the calling thread within microseconds.
    /// (2) The paths that do not take the table as a parameter (the closure BFS,
    /// <c>ComputeERS</c>) run on a worker thread under a wall-clock budget in
    /// <see cref="WithinBudget"/>. Both layers were verified by mutation: with the
    /// visited-set guard removed from <c>EffectiveRecordingId</c> the suite fails
    /// in 62 ms naming the hop bound, instead of hanging.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class EffectiveStateGraphFuzzerTests : IDisposable
    {
        // Wall-clock budget per generated graph. Healthy graphs finish in tens of
        // milliseconds; the budget only fires when a walker fails to terminate.
        private const int GraphBudgetMilliseconds = 4000;

        // Corrupt-mode seeds paired with node counts. Node counts span the range
        // the audit called for (up to ~50-100) so cycle-heavy large graphs are
        // covered as well as dense small ones.
        private static readonly (int Seed, int Nodes)[] CorruptSeeds =
        {
            (0x5EED, 8),
            (1, 13),
            (7, 21),
            (1337, 34),
            (424242, 50),
            (90210, 64),
            (0xBADF00D, 80),
            (2026_07_29, 100),
        };

        // Production-shaped seeds: acyclic, single outgoing supersede edge per
        // node, no dangling ids, no null rows. These prove the invariants are not
        // vacuous on well-formed input.
        private static readonly (int Seed, int Nodes)[] ValidSeeds =
        {
            (11, 12),
            (23, 30),
            (57, 48),
            (911, 72),
        };

        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public EffectiveStateGraphFuzzerTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            // The fuzz loop drives thousands of walker calls whose cycle paths
            // each emit a Warn; suppressing keeps the run cheap. The Warn text
            // itself is asserted by the dedicated pin below and by
            // EffectiveStateCompositeWalkerTests.
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // =====================================================================
        // Entry points
        // =====================================================================

        [Fact]
        public void Fuzz_CorruptGraphs_AllWalkerInvariantsHold()
        {
            int graphs = 0;
            for (int i = 0; i < CorruptSeeds.Length; i++)
            {
                RunGraphWithinBudget(CorruptSeeds[i].Seed, CorruptSeeds[i].Nodes, corrupt: true);
                graphs++;
            }
            Assert.Equal(CorruptSeeds.Length, graphs);
        }

        [Fact]
        public void Fuzz_ProductionShapedGraphs_AllWalkerInvariantsHold()
        {
            int graphs = 0;
            for (int i = 0; i < ValidSeeds.Length; i++)
            {
                RunGraphWithinBudget(ValidSeeds[i].Seed, ValidSeeds[i].Nodes, corrupt: false);
                graphs++;
            }
            Assert.Equal(ValidSeeds.Length, graphs);
        }

        /// <summary>
        /// Anti-vacuity instrument. The invariants above are only worth anything if
        /// the generated corpus actually carries the shapes they are supposed to
        /// survive, and if the subjects actually do work (a closure that is always
        /// just its root, or an ERS that never filters anything, would pass every
        /// assertion while proving nothing). This test fails if the generator ever
        /// stops producing one of the catalogued corrupt shapes, or if the corpus
        /// stops exercising the filters.
        /// </summary>
        [Fact]
        public void Fuzz_Corpus_CarriesEveryCorruptShapeAndExercisesTheFilters()
        {
            bool selfLoop = false, twoCycle = false, longerCycle = false;
            bool danglingTarget = false, danglingSource = false, emptyTarget = false;
            bool nullRow = false, diamond = false, duplicateOutgoing = false;
            bool chainCrossingSupersede = false, danglingBpChild = false;
            bool cyclicClosureEdge = false, duplicateRecordingId = false;
            bool notCommittedPresent = false, debrisAnchorPresent = false;
            bool nonTrivialClosure = false, ersFilteredSomething = false;
            bool retirementCascadeGrew = false, walkerHitCycle = false;

            for (int s = 0; s < CorruptSeeds.Length; s++)
            {
                var g = BuildGraph(CorruptSeeds[s].Seed, CorruptSeeds[s].Nodes, corrupt: true);

                var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < g.Recordings.Count; i++)
                {
                    var rec = g.Recordings[i];
                    ids.Add(rec.RecordingId);
                    idCounts.TryGetValue(rec.RecordingId, out int n);
                    idCounts[rec.RecordingId] = n + 1;
                    if (rec.MergeState == MergeState.NotCommitted) notCommittedPresent = true;
                    if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId)) debrisAnchorPresent = true;
                }
                foreach (var kv in idCounts)
                    if (kv.Value > 1) duplicateRecordingId = true;

                for (int i = 0; i < g.Supersedes.Count; i++)
                {
                    var rel = g.Supersedes[i];
                    if (rel == null) { nullRow = true; continue; }
                    if (string.Equals(rel.OldRecordingId, rel.NewRecordingId, StringComparison.Ordinal))
                        selfLoop = true;
                    if (rel.NewRecordingId != null && rel.NewRecordingId.Length == 0) emptyTarget = true;
                    if (!string.IsNullOrEmpty(rel.NewRecordingId) && !ids.Contains(rel.NewRecordingId))
                        danglingTarget = true;
                    if (!string.IsNullOrEmpty(rel.OldRecordingId) && !ids.Contains(rel.OldRecordingId))
                        danglingSource = true;
                    if (!string.IsNullOrEmpty(rel.OldRecordingId))
                    {
                        if (!outgoing.TryGetValue(rel.OldRecordingId, out var targets))
                        {
                            targets = new List<string>();
                            outgoing[rel.OldRecordingId] = targets;
                        }
                        targets.Add(rel.NewRecordingId);
                        if (targets.Count > 1) duplicateOutgoing = true;
                    }
                    if (!string.IsNullOrEmpty(rel.NewRecordingId))
                    {
                        incoming.TryGetValue(rel.NewRecordingId, out int n);
                        incoming[rel.NewRecordingId] = n + 1;
                        if (n + 1 > 1) diamond = true;
                    }

                    var oldRec = FindByIdIn(g.Recordings, rel.OldRecordingId);
                    var newRec = FindByIdIn(g.Recordings, rel.NewRecordingId);
                    if (oldRec != null && newRec != null
                        && !string.IsNullOrEmpty(oldRec.ChainId)
                        && !string.IsNullOrEmpty(newRec.ChainId)
                        && !string.Equals(oldRec.ChainId, newRec.ChainId, StringComparison.Ordinal))
                        chainCrossingSupersede = true;
                }

                // Cycle detection over the first-match successor function.
                foreach (string start in ids)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal) { start };
                    string cur = start;
                    int steps = 0;
                    while (steps++ <= ids.Count + 2)
                    {
                        if (!outgoing.TryGetValue(cur, out var t) || t.Count == 0) break;
                        string next = t[0];
                        if (string.IsNullOrEmpty(next)) break;
                        if (!seen.Add(next))
                        {
                            walkerHitCycle = true;
                            if (string.Equals(next, cur, StringComparison.Ordinal)) selfLoop = true;
                            else if (seen.Count == 2) twoCycle = true;
                            else longerCycle = true;
                            break;
                        }
                        cur = next;
                    }
                }

                for (int t = 0; t < g.Trees.Count; t++)
                {
                    var bps = g.Trees[t].BranchPoints;
                    for (int b = 0; bps != null && b < bps.Count; b++)
                    {
                        var bp = bps[b];
                        for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                        {
                            string cid = bp.ChildRecordingIds[c];
                            if (!ids.Contains(cid)) danglingBpChild = true;
                            // A branch point naming one of its own parents as a
                            // child is the cyclic closure edge the visited set
                            // exists for.
                            if (bp.ParentRecordingIds.Contains(cid)) cyclicClosureEdge = true;
                        }
                    }
                }

                Install(g);
                bool localClosure = false, localErs = false, localCascade = false;
                // Fail fast and cleanly before the unbounded ERS / closure calls
                // below: if a walker lost its cycle guard the proxy throws here
                // rather than letting ComputeERS spin inside its own lock.
                long probeBudget = SingleWalkReadBudget(
                    g.Recordings.Count + g.Supersedes.Count, g.Supersedes.Count);
                for (int p = 0; p < g.ProbeIds.Count; p++)
                {
                    EffectiveState.EffectiveRecordingId(
                        g.ProbeIds[p], Bounded(g.Supersedes, probeBudget));
                }

                WithinBudget("corpus probe seed=" + CorruptSeeds[s].Seed, () =>
                {
                    var closure = new HashSet<string>(
                        EffectiveState.ComputeSessionSuppressedSubtree(g.Marker), StringComparer.Ordinal);
                    if (closure.Count > 1) localClosure = true;

                    var ers = EffectiveState.ComputeERS();
                    if (ers.Count < g.Recordings.Count) localErs = true;

                    var seedRetired = EffectiveState.ComputeRewindRetiredRecordingIds(g.Retirements);
                    var cascaded = EffectiveState.ComputeRewindRetiredRecordingIds(
                        g.Recordings, g.Retirements);
                    if (cascaded.Count > seedRetired.Count) localCascade = true;
                });
                nonTrivialClosure |= localClosure;
                ersFilteredSomething |= localErs;
                retirementCascadeGrew |= localCascade;
            }

            Assert.True(selfLoop, "corpus lost the self-loop supersede shape");
            Assert.True(twoCycle, "corpus lost the 2-cycle supersede shape");
            Assert.True(longerCycle, "corpus lost the longer-than-2 cycle supersede shape");
            Assert.True(danglingTarget, "corpus lost the dangling supersede target shape");
            Assert.True(danglingSource, "corpus lost the dangling supersede source shape");
            Assert.True(emptyTarget, "corpus lost the empty supersede target shape");
            Assert.True(nullRow, "corpus lost the null supersede row shape");
            Assert.True(diamond, "corpus lost the diamond (two sources, one target) shape");
            Assert.True(duplicateOutgoing, "corpus lost the duplicate-outgoing-edge shape");
            Assert.True(chainCrossingSupersede, "corpus lost the chain-crossing supersede shape");
            Assert.True(danglingBpChild, "corpus lost the dangling branch-point child shape");
            Assert.True(cyclicClosureEdge, "corpus lost the cyclic branch-point closure edge shape");
            Assert.True(duplicateRecordingId, "corpus lost the duplicate recording-id shape");
            Assert.True(notCommittedPresent, "corpus lost NotCommitted recordings");
            Assert.True(debrisAnchorPresent, "corpus lost parent-anchored debris");
            Assert.True(walkerHitCycle, "corpus produced no reachable supersede cycle at all");
            Assert.True(nonTrivialClosure, "no generated graph produced a closure larger than its root");
            Assert.True(ersFilteredSomething, "ERS never filtered anything across the whole corpus");
            Assert.True(retirementCascadeGrew, "the retirement cascade never expanded past its seed rows");
        }

        /// <summary>
        /// Runs one generated graph inside a wall-clock budget. A non-terminating
        /// walker (the shape a removed visited-set guard produces) fails here with
        /// the seed named rather than hanging the suite forever.
        /// </summary>
        private static void RunGraphWithinBudget(int seed, int nodes, bool corrupt)
        {
            var graph = BuildGraph(seed, nodes, corrupt);
            Install(graph);
            WithinBudget(
                "seed=" + seed + " nodes=" + nodes + " corrupt=" + corrupt + "\n" + graph.Dump(),
                () => ExerciseGraph(graph));
        }

        /// <summary>
        /// Runs <paramref name="body"/> on a dedicated worker thread with a
        /// wall-clock budget. EVERY walker call in this file goes through here: a
        /// walker whose visited-set guard is gone spins forever, and a direct call
        /// on the test thread would hang the whole suite instead of reporting a
        /// failure. On budget expiry the worker is aborted (the test project
        /// targets net472, where <see cref="Thread.Abort"/> still works) so a
        /// runaway walker cannot outlive its own test and take the host down with
        /// it - an aborted thread also releases any monitor it held, which matters
        /// because the ERS walk runs inside <c>EffectiveState</c>'s lock.
        /// </summary>
        private static void WithinBudget(string context, Action body)
        {
            Exception captured = null;
            var worker = new Thread(() =>
            {
                try { body(); }
                catch (ThreadAbortException) { /* budget kill; re-raised automatically */ }
                catch (Exception ex) { captured = ex; }
            });
            worker.IsBackground = true;
            worker.Start();

            if (!worker.Join(GraphBudgetMilliseconds))
            {
                try { worker.Abort(); }
                catch (Exception) { /* best effort */ }
                Assert.True(false,
                    "Walker did not terminate within " + GraphBudgetMilliseconds +
                    " ms (visited-set cycle guard missing?) for " + context);
            }

            if (captured != null)
                ExceptionDispatchInfo.Capture(captured).Throw();
        }

        // =====================================================================
        // Invariants
        // =====================================================================

        private static void ExerciseGraph(FuzzGraph g)
        {
            var supersedes = g.Supersedes;
            var committedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < g.Recordings.Count; i++)
            {
                string id = g.Recordings[i].RecordingId;
                if (!string.IsNullOrEmpty(id)) committedIds.Add(id);
            }

            // Universe of ids any walker may legitimately return.
            var universe = new HashSet<string>(committedIds, StringComparer.Ordinal);
            for (int i = 0; i < supersedes.Count; i++)
            {
                var rel = supersedes[i];
                if (rel == null) continue;
                if (!string.IsNullOrEmpty(rel.OldRecordingId)) universe.Add(rel.OldRecordingId);
                if (!string.IsNullOrEmpty(rel.NewRecordingId)) universe.Add(rel.NewRecordingId);
            }
            int hopCap = universe.Count + 2;

            AssertSupersedeWalker(g, supersedes, universe, hopCap);
            AssertCompositeWalker(g, supersedes, universe);
            AssertVisibilityConsistency(g, supersedes, committedIds);
            AssertClosureSoundness(g, committedIds);
            AssertRetirementSoundness(g, committedIds);
            AssertErsSoundnessAndIdempotence(g, supersedes, committedIds);
            AssertRewindPointSlotResolutionInRange(g, supersedes);
        }

        /// <summary>
        /// INV-1 (termination / hop bound) and INV-2 (stability) for
        /// <see cref="EffectiveState.EffectiveRecordingId"/>.
        ///
        /// The supersede table induces a FUNCTIONAL graph (the walker takes the
        /// FIRST matching row for a given id), so a bounded reference walk with an
        /// explicit hop counter is a faithful oracle AND yields the hop bound the
        /// production API does not expose. The reference caps its own hops at
        /// |universe| + 2 and throws if that cap is reached, which is the
        /// termination statement: the walk visits at most |universe| distinct
        /// nodes.
        /// </summary>
        private static void AssertSupersedeWalker(
            FuzzGraph g,
            List<RecordingSupersedeRelation> supersedes,
            HashSet<string> universe,
            int hopCap)
        {
            long budget = SingleWalkReadBudget(universe.Count, supersedes.Count);
            for (int i = 0; i < g.ProbeIds.Count; i++)
            {
                string origin = g.ProbeIds[i];
                string actual = BoundedCall(g, "EffectiveRecordingId(" + Show(origin) + ")",
                    supersedes, budget, s => EffectiveState.EffectiveRecordingId(origin, s));

                if (string.IsNullOrEmpty(origin))
                {
                    Check(actual == null, g, "EffectiveRecordingId(null/empty) must return null");
                    continue;
                }

                bool endedOnCycle;
                int hops;
                string expected = ReferenceSupersedeWalk(
                    origin, supersedes, hopCap, out hops, out endedOnCycle);

                Check(string.Equals(actual, expected, StringComparison.Ordinal), g,
                    "EffectiveRecordingId(" + origin + ") = " + Show(actual) +
                    " but bounded reference walk yields " + Show(expected));
                Check(hops <= universe.Count + 1, g,
                    "EffectiveRecordingId(" + origin + ") took " + hops +
                    " hops, above the |universe|+1 = " + (universe.Count + 1) + " bound");
                Check(!string.IsNullOrEmpty(actual), g,
                    "EffectiveRecordingId(" + origin + ") returned empty for a non-empty origin");
                Check(universe.Contains(actual) || string.Equals(actual, origin, StringComparison.Ordinal), g,
                    "EffectiveRecordingId(" + origin + ") returned " + Show(actual) +
                    " which is outside the id universe");

                // Stability: the walker is pure, repeated calls must agree.
                string again = BoundedCall(g, "EffectiveRecordingId(" + origin + ") repeat",
                    supersedes, budget, s => EffectiveState.EffectiveRecordingId(origin, s));
                Check(string.Equals(actual, again, StringComparison.Ordinal), g,
                    "EffectiveRecordingId(" + origin + ") is not stable across calls: " +
                    Show(actual) + " then " + Show(again));

                // Idempotence holds only when the walk reached a real terminus.
                // A walk that ended by closing a cycle returns the last-visited
                // node, and re-walking from THAT node lands somewhere else in the
                // cycle - pinned deterministically by
                // EffectiveRecordingId_TwoCycle_IsNotIdempotent below.
                if (!endedOnCycle)
                {
                    string twice = BoundedCall(g, "EffectiveRecordingId(" + actual + ") idempotence",
                        supersedes, budget, s => EffectiveState.EffectiveRecordingId(actual, s));
                    Check(string.Equals(actual, twice, StringComparison.Ordinal), g,
                        "EffectiveRecordingId is not idempotent on an acyclic walk from " +
                        origin + ": f(x)=" + Show(actual) + " f(f(x))=" + Show(twice));
                }
            }
        }

        /// <summary>
        /// INV-3 for <see cref="EffectiveState.EffectiveTipRecordingId"/>: the
        /// composite chain+supersede walker terminates, stays inside the id
        /// universe, is stable, agrees with its own hot-loop dictionary overload,
        /// and - on a graph carrying NO chain links at all - is exactly the
        /// pure-supersede walker (the chain hop cannot fire, so the two loops are
        /// the same loop). The chain-free agreement is verified against a
        /// chain-stripped clone of the same graph so the comparison is real
        /// rather than assumed.
        /// </summary>
        private static void AssertCompositeWalker(
            FuzzGraph g,
            List<RecordingSupersedeRelation> supersedes,
            HashSet<string> universe)
        {
            var index = EffectiveState.BuildRecordingIdIndex();
            long budget = SingleWalkReadBudget(universe.Count, supersedes.Count);

            for (int i = 0; i < g.ProbeIds.Count; i++)
            {
                string origin = g.ProbeIds[i];
                string tip = BoundedCall(g, "EffectiveTipRecordingId(" + Show(origin) + ")",
                    supersedes, budget, s => EffectiveState.EffectiveTipRecordingId(origin, s));

                if (string.IsNullOrEmpty(origin))
                {
                    Check(tip == null, g, "EffectiveTipRecordingId(null/empty) must return null");
                    continue;
                }

                Check(!string.IsNullOrEmpty(tip), g,
                    "EffectiveTipRecordingId(" + origin + ") returned empty for a non-empty origin");
                Check(universe.Contains(tip) || string.Equals(tip, origin, StringComparison.Ordinal), g,
                    "EffectiveTipRecordingId(" + origin + ") returned " + Show(tip) +
                    " which is outside the id universe");

                string again = BoundedCall(g, "EffectiveTipRecordingId(" + origin + ") repeat",
                    supersedes, budget, s => EffectiveState.EffectiveTipRecordingId(origin, s));
                Check(string.Equals(tip, again, StringComparison.Ordinal), g,
                    "EffectiveTipRecordingId(" + origin + ") is not stable across calls");

                // Pass 5 review M2 equivalence: the pre-built id index overload
                // must be indistinguishable from the linear-scan overload.
                string viaIndex = BoundedCall(g, "EffectiveTipRecordingId(" + origin + ") via index",
                    supersedes, budget, s => EffectiveState.EffectiveTipRecordingId(origin, s, index));
                Check(string.Equals(tip, viaIndex, StringComparison.Ordinal), g,
                    "EffectiveTipRecordingId(" + origin + ") disagrees between the linear-scan (" +
                    Show(tip) + ") and dictionary (" + Show(viaIndex) + ") overloads");
            }

            // Chain-free agreement: strip every ChainId, reinstall, and require
            // the composite walker to collapse onto the pure supersede walker.
            var chainFree = g.CloneWithoutChains();
            Install(chainFree);
            try
            {
                for (int i = 0; i < chainFree.ProbeIds.Count; i++)
                {
                    string origin = chainFree.ProbeIds[i];
                    if (string.IsNullOrEmpty(origin)) continue;
                    string plain = BoundedCall(chainFree, "chain-free EffectiveRecordingId(" + origin + ")",
                        chainFree.Supersedes, budget,
                        s => EffectiveState.EffectiveRecordingId(origin, s));
                    string composite = BoundedCall(chainFree, "chain-free EffectiveTipRecordingId(" + origin + ")",
                        chainFree.Supersedes, budget,
                        s => EffectiveState.EffectiveTipRecordingId(origin, s));
                    Check(string.Equals(plain, composite, StringComparison.Ordinal), chainFree,
                        "On a chain-free graph EffectiveTipRecordingId(" + origin + ") = " +
                        Show(composite) + " must equal EffectiveRecordingId = " + Show(plain));
                }
            }
            finally
            {
                Install(g);
            }
        }

        /// <summary>
        /// INV-4: <see cref="EffectiveState.IsVisible"/> and
        /// <see cref="EffectiveState.IsSupersededByRelation"/> are exact
        /// complements for a committed, non-NotCommitted recording carrying an id,
        /// and walker-superseded implies membership in the raw-index helper
        /// <c>ComputeSupersededRecordingIdsByRelation</c>. The converse does NOT
        /// hold on corrupt duplicate-outgoing-edge state and is pinned separately
        /// by <see cref="SupersededSurfaces_SelfLoopBeforeRealEdge_Disagree"/>.
        /// </summary>
        private static void AssertVisibilityConsistency(
            FuzzGraph g,
            List<RecordingSupersedeRelation> supersedes,
            HashSet<string> committedIds)
        {
            var byRelation = EffectiveState.ComputeSupersededRecordingIdsByRelation(
                g.Recordings, supersedes);

            for (int i = 0; i < g.Recordings.Count; i++)
            {
                var rec = g.Recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;

                bool superseded = EffectiveState.IsSupersededByRelation(rec, supersedes);
                bool visible = EffectiveState.IsVisible(rec, supersedes);

                if (rec.MergeState == MergeState.NotCommitted)
                {
                    Check(!visible, g,
                        "NotCommitted recording " + rec.RecordingId + " must never be IsVisible");
                }
                else
                {
                    Check(visible != superseded, g,
                        "IsVisible / IsSupersededByRelation disagree for " + rec.RecordingId +
                        " (visible=" + visible + " superseded=" + superseded + ")");
                }

                if (superseded)
                {
                    Check(byRelation.Contains(rec.RecordingId), g,
                        "Walker says " + rec.RecordingId + " is superseded but " +
                        "ComputeSupersededRecordingIdsByRelation does not list it");
                }
            }

            foreach (string id in byRelation)
            {
                Check(committedIds.Contains(id), g,
                    "ComputeSupersededRecordingIdsByRelation returned non-committed id " + id);
            }
        }

        /// <summary>
        /// INV-5 (closure soundness): the session-suppressed subtree is a subset of
        /// the committed recording ids plus the requested root, always contains the
        /// root, is stable across repeated calls, and every member is reachable
        /// from the root through the four edge families the closure documents
        /// (branch-point children, chain siblings, same-PID peers, debris
        /// children). The reference reachability walk drops every gate the
        /// production walk applies, so it is a strict superset - a closure member
        /// outside it is an unreachable node the walker invented.
        /// </summary>
        private static void AssertClosureSoundness(FuzzGraph g, HashSet<string> committedIds)
        {
            var raw = EffectiveState.ComputeSessionSuppressedSubtree(g.Marker);
            Check(raw != null, g, "ComputeSessionSuppressedSubtree returned null");
            var closure = new HashSet<string>(raw, StringComparer.Ordinal);

            string root = g.Marker?.OriginChildRecordingId;
            if (g.Marker == null || string.IsNullOrEmpty(root))
            {
                Check(closure.Count == 0, g,
                    "Closure for a null marker / empty origin must be empty, got " + closure.Count);
                return;
            }

            Check(closure.Contains(root), g, "Closure must always contain its root " + root);

            // Membership universe. The closure is NOT strictly a subset of the
            // committed recordings: the branch-point child loop in
            // ComputeSubtreeClosureInternal adds `childId` without checking that
            // it resolves to a live recording, so a BranchPoint whose
            // ChildRecordingIds names a nonexistent id admits that id verbatim.
            // That fallthrough is deliberate downstream (SupersedeCommit's
            // row-write site documents "closure id not in CommittedRecordings ...
            // falls through to row-write", and LoadTimeSweep warn-logs the
            // resulting orphan row), so the accurate contract is
            // committed + {root} + {branch-point child ids}. Pinned by
            // Closure_DanglingBranchPointChildId_IsAdmittedVerbatim.
            var bpChildIds = AllBranchPointChildIds(g);
            var permissive = PermissiveReachable(g, root);
            foreach (string id in closure)
            {
                Check(string.Equals(id, root, StringComparison.Ordinal)
                        || committedIds.Contains(id)
                        || bpChildIds.Contains(id), g,
                    "Closure member " + id + " is not the root, a committed recording, " +
                    "or a branch-point child id");
                Check(permissive.Contains(id), g,
                    "Closure member " + id + " is not reachable from root " + root +
                    " over the documented closure edge families");
            }

            var again = EffectiveState.ComputeSessionSuppressedSubtree(g.Marker);
            Check(again.Count == closure.Count, g,
                "Closure is not stable across calls: " + closure.Count + " then " + again.Count);
            foreach (string id in again)
                Check(closure.Contains(id), g, "Closure gained member " + id + " on a repeat call");
        }

        /// <summary>
        /// INV-6: the rewind-retirement cascade reaches a fixed point (it only ever
        /// adds, and the loop is bounded by the recording count), contains its seed
        /// rows, and never invents an id that is neither committed nor named by a
        /// retirement row. Also checks the timeline-inactive map keys are exactly
        /// covered by the superseded and retired sets.
        /// </summary>
        private static void AssertRetirementSoundness(FuzzGraph g, HashSet<string> committedIds)
        {
            var seed = EffectiveState.ComputeRewindRetiredRecordingIds(g.Retirements);
            var cascade = EffectiveState.ComputeRewindRetiredRecordingIds(g.Recordings, g.Retirements);

            foreach (string id in seed)
                Check(cascade.Contains(id), g, "Retirement cascade dropped seed id " + id);

            var seedIds = new HashSet<string>(seed, StringComparer.Ordinal);
            foreach (string id in cascade)
            {
                Check(committedIds.Contains(id) || seedIds.Contains(id), g,
                    "Retirement cascade invented id " + id +
                    " (neither committed nor named by a retirement row)");
            }

            var inactive = EffectiveState.ComputeTimelineInactiveRecordingIds(
                g.Recordings, g.Supersedes, g.Retirements);
            var superseded = EffectiveState.ComputeSupersededRecordingIdsByRelation(
                g.Recordings, g.Supersedes);
            foreach (var kv in inactive)
            {
                Check(superseded.Contains(kv.Key) || cascade.Contains(kv.Key), g,
                    "Timeline-inactive map lists " + kv.Key +
                    " which is neither supersede-hidden nor rewind-retired");
                Check(kv.Value != TimelineInactiveReason.None, g,
                    "Timeline-inactive map gave " + kv.Key + " the None reason");
            }
        }

        /// <summary>
        /// INV-7: ERS is exactly the committed recordings that survive all four
        /// documented filters (NotCommitted, supersede-hidden, rewind-retired,
        /// session-suppressed), carries no duplicates by reference, and is
        /// idempotent - both through the version-keyed cache (same instance) and
        /// after a forced cache drop (same content).
        /// </summary>
        private static void AssertErsSoundnessAndIdempotence(
            FuzzGraph g,
            List<RecordingSupersedeRelation> supersedes,
            HashSet<string> committedIds)
        {
            var ers = EffectiveState.ComputeERS();
            Check(ers != null, g, "ComputeERS returned null");

            var cached = EffectiveState.ComputeERS();
            Check(ReferenceEquals(ers, cached), g,
                "ComputeERS did not hit its version-keyed cache on an unchanged store");

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(
                RecordingStore.CommittedRecordings, g.Retirements);
            var closure = new HashSet<string>(
                EffectiveState.ComputeSessionSuppressedSubtree(g.Marker), StringComparer.Ordinal);

            var seenRefs = new HashSet<Recording>();
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                Check(rec != null, g, "ERS contains a null entry at index " + i);
                Check(seenRefs.Add(rec), g,
                    "ERS contains a duplicate entry for " + Show(rec.RecordingId));
                Check(committedIds.Contains(rec.RecordingId), g,
                    "ERS member " + Show(rec.RecordingId) + " is not a committed recording");
                Check(rec.MergeState != MergeState.NotCommitted, g,
                    "ERS member " + rec.RecordingId + " is NotCommitted");
                Check(EffectiveState.IsVisible(rec, supersedes), g,
                    "ERS member " + rec.RecordingId + " fails IsVisible");
                Check(!retired.Contains(rec.RecordingId), g,
                    "ERS member " + rec.RecordingId + " is rewind-retired");
                Check(!closure.Contains(rec.RecordingId), g,
                    "ERS member " + rec.RecordingId + " is inside the session-suppressed subtree");
            }

            // Completeness: nothing that passes every filter may be missing.
            for (int i = 0; i < g.Recordings.Count; i++)
            {
                var rec = g.Recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                bool shouldBeIn = rec.MergeState != MergeState.NotCommitted
                    && EffectiveState.IsVisible(rec, supersedes)
                    && !retired.Contains(rec.RecordingId)
                    && !closure.Contains(rec.RecordingId);
                Check(shouldBeIn == seenRefs.Contains(rec), g,
                    "ERS membership mismatch for " + rec.RecordingId +
                    " (filters say " + shouldBeIn + ", ERS says " + seenRefs.Contains(rec) + ")");
            }

            // Idempotence across a forced rebuild: same members, same order.
            EffectiveState.ResetCachesForTesting();
            var rebuilt = EffectiveState.ComputeERS();
            Check(rebuilt.Count == ers.Count, g,
                "ComputeERS is not idempotent across a cache drop: " + ers.Count +
                " then " + rebuilt.Count);
            for (int i = 0; i < rebuilt.Count; i++)
            {
                Check(ReferenceEquals(rebuilt[i], ers[i]), g,
                    "ComputeERS rebuild diverged at index " + i);
            }
        }

        /// <summary>
        /// INV-8: <c>ResolveRewindPointSlotIndexForRecording</c> - which drives the
        /// composite walker AND the <c>IsInSupersedeForwardTrail</c> BFS (a third
        /// visited-set walker) - always terminates and returns either -1 or a valid
        /// index into the rewind point's slot list. Sampled over a bounded subset
        /// of recordings so the O(slots * N * E) BFS stays inside the suite budget.
        /// </summary>
        private static void AssertRewindPointSlotResolutionInRange(
            FuzzGraph g,
            List<RecordingSupersedeRelation> supersedes)
        {
            int stride = Math.Max(1, g.Recordings.Count / 10);
            for (int rp = 0; rp < g.RewindPoints.Count; rp++)
            {
                var point = g.RewindPoints[rp];
                // Per slot the call runs three supersede-scanning walkers (id-local,
                // composite, and the forward-trail BFS), each bounded by the node
                // count; four scans per slot leaves headroom without admitting a
                // runaway.
                long budget = (long)(point.ChildSlots.Count + 1) * 4L
                    * SingleWalkReadBudget(g.Recordings.Count + 2, supersedes.Count);
                for (int i = 0; i < g.Recordings.Count; i += stride)
                {
                    var rec = g.Recordings[i];
                    int slot = BoundedCall(g,
                        "ResolveRewindPointSlotIndexForRecording(" + point.RewindPointId + "," + rec.RecordingId + ")",
                        supersedes, budget,
                        s => EffectiveState.ResolveRewindPointSlotIndexForRecording(point, rec, s));
                    Check(slot == -1 || (slot >= 0 && point.ChildSlots != null
                            && slot < point.ChildSlots.Count), g,
                        "ResolveRewindPointSlotIndexForRecording returned out-of-range slot " +
                        slot + " for rp=" + point.RewindPointId + " rec=" + rec.RecordingId);
                }
            }
        }

        // =====================================================================
        // Reference implementations (test-side oracles)
        // =====================================================================

        /// <summary>
        /// Bounded mirror of the supersede forward walk. Independent of production
        /// only in that it carries an explicit hop counter and throws on exceeding
        /// the cap - which is the termination proof the production API cannot
        /// express (it has no hop-count output).
        /// </summary>
        private static string ReferenceSupersedeWalk(
            string origin,
            List<RecordingSupersedeRelation> supersedes,
            int hopCap,
            out int hops,
            out bool endedOnCycle)
        {
            hops = 0;
            endedOnCycle = false;
            if (string.IsNullOrEmpty(origin)) return null;
            if (supersedes == null || supersedes.Count == 0) return origin;

            var visited = new HashSet<string>(StringComparer.Ordinal) { origin };
            string current = origin;
            while (hops <= hopCap)
            {
                string next = null;
                for (int i = 0; i < supersedes.Count; i++)
                {
                    var rel = supersedes[i];
                    if (rel == null) continue;
                    if (string.Equals(rel.OldRecordingId, current, StringComparison.Ordinal))
                    {
                        next = rel.NewRecordingId;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(next)) return current;
                if (visited.Contains(next))
                {
                    endedOnCycle = true;
                    return current;
                }
                visited.Add(next);
                current = next;
                hops++;
            }
            throw new InvalidOperationException(
                "Reference supersede walk exceeded its hop cap of " + hopCap +
                " starting at " + origin + " - the supersede graph is not a bounded functional graph");
        }

        /// <summary>
        /// Every id named by any branch point's <c>ChildRecordingIds</c>, across
        /// every tree - the third population the closure may legitimately admit
        /// alongside committed recordings and the root.
        /// </summary>
        private static HashSet<string> AllBranchPointChildIds(FuzzGraph g)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int t = 0; t < g.Trees.Count; t++)
            {
                var bps = g.Trees[t]?.BranchPoints;
                if (bps == null) continue;
                for (int b = 0; b < bps.Count; b++)
                {
                    var children = bps[b]?.ChildRecordingIds;
                    if (children == null) continue;
                    for (int c = 0; c < children.Count; c++)
                    {
                        if (!string.IsNullOrEmpty(children[c])) ids.Add(children[c]);
                    }
                }
            }
            return ids;
        }

        /// <summary>
        /// Gate-free reachability over the four closure edge families. Strict
        /// superset of what <c>ComputeSubtreeClosureInternal</c> may produce.
        /// </summary>
        private static HashSet<string> PermissiveReachable(FuzzGraph g, string root)
        {
            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < g.Recordings.Count; i++)
            {
                var r = g.Recordings[i];
                if (r != null && !string.IsNullOrEmpty(r.RecordingId) && !byId.ContainsKey(r.RecordingId))
                    byId[r.RecordingId] = r;
            }

            // Branch points from every tree, keyed by id (production prefers the
            // owning tree then falls back to any tree; the union covers both).
            var bpChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (int t = 0; t < g.Trees.Count; t++)
            {
                var tree = g.Trees[t];
                if (tree?.BranchPoints == null) continue;
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    var bp = tree.BranchPoints[b];
                    if (bp == null || string.IsNullOrEmpty(bp.Id) || bp.ChildRecordingIds == null) continue;
                    if (!bpChildren.TryGetValue(bp.Id, out var list))
                    {
                        list = new List<string>();
                        bpChildren[bp.Id] = list;
                    }
                    list.AddRange(bp.ChildRecordingIds);
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal) { root };
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                string currentId = queue.Dequeue();
                if (!byId.TryGetValue(currentId, out var cur) || cur == null) continue;

                void Push(string id)
                {
                    if (string.IsNullOrEmpty(id)) return;
                    if (seen.Add(id)) queue.Enqueue(id);
                }

                // (1) branch-point children
                if (!string.IsNullOrEmpty(cur.ChildBranchPointId)
                    && bpChildren.TryGetValue(cur.ChildBranchPointId, out var kids))
                {
                    for (int i = 0; i < kids.Count; i++) Push(kids[i]);
                }

                foreach (var cand in byId.Values)
                {
                    if (cand == null || ReferenceEquals(cand, cur)) continue;
                    if (string.IsNullOrEmpty(cand.RecordingId)) continue;
                    bool sameTree = !string.IsNullOrEmpty(cur.TreeId)
                        && string.Equals(cand.TreeId, cur.TreeId, StringComparison.Ordinal);

                    // (2) chain siblings
                    if (sameTree
                        && !string.IsNullOrEmpty(cur.ChainId)
                        && string.Equals(cand.ChainId, cur.ChainId, StringComparison.Ordinal)
                        && cand.ChainBranch == cur.ChainBranch)
                    {
                        Push(cand.RecordingId);
                        continue;
                    }

                    // (3) same-PID peers
                    if (sameTree && cur.VesselPersistentId != 0
                        && cand.VesselPersistentId == cur.VesselPersistentId)
                    {
                        Push(cand.RecordingId);
                        continue;
                    }

                    // (4) debris children anchored to the dequeued recording
                    if (sameTree
                        && !string.IsNullOrEmpty(cand.ParentAnchorRecordingId)
                        && string.Equals(cand.ParentAnchorRecordingId, cur.RecordingId, StringComparison.Ordinal))
                    {
                        Push(cand.RecordingId);
                    }
                }
            }
            return seen;
        }

        // =====================================================================
        // Deterministic pins found while building the fuzzer
        // =====================================================================

        /// <summary>
        /// Pins the cross-tree TODO at <c>EffectiveState.cs:109</c>
        /// ("halt walk at cross-tree boundaries once cross-tree supersedes are
        /// producible"). The fuzzer's multi-tree graphs reach this path, so the
        /// current behavior is stated here rather than left implicit: the walk
        /// does NOT halt at a tree boundary - it follows the row straight into the
        /// other tree. When the TODO is implemented this test must be updated
        /// deliberately, not discovered by a red fuzzer.
        /// </summary>
        [Fact]
        public void EffectiveRecordingId_CrossTreeSupersede_DoesNotHaltAtTreeBoundary()
        {
            var a = MakeRecording("rec_A", "tree_1");
            var b = MakeRecording("rec_B", "tree_2");
            InstallTree("tree_1", new List<Recording> { a }, null);
            InstallTree("tree_2", new List<Recording> { b }, null);

            var sups = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            Assert.Equal("rec_B", EffectiveState.EffectiveRecordingId("rec_A", sups));
            Assert.Equal("rec_B", EffectiveState.EffectiveTipRecordingId("rec_A", sups));
        }

        /// <summary>
        /// Pins the non-idempotence the fuzzer's acyclic-only idempotence guard
        /// carves out: on a 2-cycle the walker returns the last-visited node, and
        /// re-walking from that node lands on the other member. Stated explicitly
        /// so the carve-out in <see cref="AssertSupersedeWalker"/> is documented
        /// behavior rather than a silently weakened assertion.
        /// </summary>
        [Fact]
        public void EffectiveRecordingId_TwoCycle_IsNotIdempotent()
        {
            var sups = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_A"),
            };

            string once = EffectiveState.EffectiveRecordingId("rec_A", Bounded(sups, 32));
            Assert.Equal("rec_B", once);
            string twice = EffectiveState.EffectiveRecordingId(once, Bounded(sups, 32));
            Assert.Equal("rec_A", twice);
        }

        /// <summary>
        /// Pins a divergence the fuzzer surfaced between the two "is this
        /// superseded?" surfaces on corrupt duplicate-outgoing-edge state. The
        /// walker (<c>IsSupersededByRelation</c> / <c>IsVisible</c> / ERS) takes
        /// the FIRST matching row, so a self-loop row ordered ahead of a real
        /// supersede row makes it report "not superseded"; the raw-index helper
        /// <c>ComputeSupersededRecordingIdsByRelation</c> scans ALL rows, skips
        /// the self loop, and reports "superseded".
        ///
        /// <para>
        /// This is corrupt input by construction - <c>SupersedeCommit</c> dedupes
        /// on the (old, new) PAIR, so two outgoing rows from one id are only
        /// reachable through hand-edited or damaged save state, and a self-loop
        /// row is never authored at all. Recorded as current behavior, not fixed:
        /// the two surfaces have deliberately different contracts (ERS visibility
        /// vs raw-index UI suppression) and picking a winner is a design decision,
        /// not a bug fix.
        /// </para>
        /// </summary>
        [Fact]
        public void SupersededSurfaces_SelfLoopBeforeRealEdge_Disagree()
        {
            var rec = MakeRecording("rec_A", "tree_1");
            var other = MakeRecording("rec_B", "tree_1");
            InstallTree("tree_1", new List<Recording> { rec, other }, null);

            var sups = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_A"),   // self loop, ordered first
                Rel("rec_A", "rec_B"),   // real supersede row
            };

            // Walker short-circuits on the self loop and calls rec_A visible.
            Assert.False(EffectiveState.IsSupersededByRelation(rec, Bounded(sups, 32)));
            Assert.True(EffectiveState.IsVisible(rec, Bounded(sups, 32)));

            // The raw-index helper skips the self loop and hides rec_A.
            var byRelation = EffectiveState.ComputeSupersededRecordingIdsByRelation(
                new List<Recording> { rec, other }, sups);
            Assert.Contains("rec_A", byRelation);
        }

        /// <summary>
        /// Pins the closure-membership shape the fuzzer surfaced: a BranchPoint
        /// whose <c>ChildRecordingIds</c> names an id with no matching recording
        /// admits that id into the session-suppressed closure verbatim - the
        /// branch-point child loop in <c>ComputeSubtreeClosureInternal</c> has no
        /// existence check before <c>result.Add(childId)</c>.
        ///
        /// <para>
        /// Not treated as a defect: the downstream row-write site in
        /// <c>SupersedeCommit.AppendRelations</c> explicitly documents the
        /// "closure id not in CommittedRecordings ... falls through to row-write"
        /// case as the intended default, and <c>LoadTimeSweep</c> warn-logs the
        /// orphan supersede row that results. Recorded so the closure's real
        /// contract (committed recordings, plus the root, plus branch-point child
        /// ids) is stated somewhere rather than inferred.
        /// </para>
        /// </summary>
        [Fact]
        public void Closure_DanglingBranchPointChildId_IsAdmittedVerbatim()
        {
            var origin = MakeRecording("rec_origin", "tree_1");
            origin.ChildBranchPointId = "bp_dangle";
            var bp = new BranchPoint
            {
                Id = "bp_dangle",
                Type = BranchPointType.Undock,
                UT = 0.0,
                ParentRecordingIds = new List<string> { "rec_origin" },
                ChildRecordingIds = new List<string> { "rec_does_not_exist" },
            };
            InstallTree("tree_1", new List<Recording> { origin }, new List<BranchPoint> { bp });

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_dangle",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            var closure = new HashSet<string>(
                EffectiveState.ComputeSessionSuppressedSubtree(marker), StringComparer.Ordinal);

            Assert.Contains("rec_origin", closure);
            Assert.Contains("rec_does_not_exist", closure);
            // The dangling id cannot hide anything: ERS filters by recording id
            // and no recording carries it.
            Assert.Empty(EffectiveState.ComputeERS());
        }

        /// <summary>
        /// The fuzz loop runs with logging suppressed for speed; this keeps one
        /// live proof that a generated cycle still produces the grep-stable
        /// <c>[Supersede]</c> Warn the operator debugs from.
        /// </summary>
        [Fact]
        public void GeneratedCycle_LogsSupersedeCycleWarn()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                var g = BuildGraph(0x5EED, 8, corrupt: true);
                Install(g);
                long budget = SingleWalkReadBudget(
                    g.Recordings.Count + g.Supersedes.Count, g.Supersedes.Count);
                for (int i = 0; i < g.ProbeIds.Count; i++)
                {
                    EffectiveState.EffectiveRecordingId(
                        g.ProbeIds[i], Bounded(g.Supersedes, budget));
                }

                Assert.Contains(logLines, l =>
                    l.Contains("[Supersede]") && l.Contains("cycle detected"));
            }
            finally
            {
                ParsekLog.TestSinkForTesting = null;
                ParsekLog.SuppressLogging = true;
            }
        }

        // =====================================================================
        // Generator
        // =====================================================================

        /// <summary>
        /// Builds one deterministic graph from <paramref name="seed"/>.
        ///
        /// <para>Node distribution: <paramref name="nodeCount"/> recordings spread
        /// over 1-3 trees; ~45% carry a ChainId drawn from a small pool (so chains
        /// have several members) with a ChainIndex in [0,4) and a ChainBranch in
        /// [0,2); VesselPersistentId is drawn from {0, 101, 202, 303} so same-PID
        /// peer expansion fires and PID-0 (unknown) is represented; MergeState is
        /// ~70% Immutable, ~20% CommittedProvisional, ~10% NotCommitted; ~18% are
        /// debris carrying a ParentAnchorRecordingId.</para>
        ///
        /// <para>Edge distribution: roughly <paramref name="nodeCount"/> supersede
        /// rows. In corrupt mode they are drawn from the shape catalog - forward
        /// edge, self loop, 2-cycle, 3-cycle, dangling target, dangling source,
        /// diamond (two sources one target), duplicate outgoing (one source two
        /// targets), empty target, null row, chain-crossing edge. In valid mode
        /// only strictly-forward single-outgoing edges are emitted, which makes the
        /// supersede relation acyclic and every walk terminate at a real
        /// endpoint.</para>
        ///
        /// <para>Corrupt mode additionally produces tree-id mismatches (a recording
        /// stored in one tree claiming another tree's id, or no tree id at all),
        /// duplicate recording ids, self-anchored and dangling debris parents,
        /// branch points with multiple parents / dangling child ids / cyclic child
        /// lists, dangling retirement rows, and a marker rooted at a nonexistent
        /// id.</para>
        /// </summary>
        private static FuzzGraph BuildGraph(int seed, int nodeCount, bool corrupt)
        {
            var rng = new Random(seed);
            var g = new FuzzGraph { Seed = seed, NodeCount = nodeCount, Corrupt = corrupt };

            int treeCount = 1 + rng.Next(3);
            for (int t = 0; t < treeCount; t++)
            {
                g.Trees.Add(new RecordingTree
                {
                    Id = "t" + t,
                    TreeName = "FuzzTree" + t,
                    BranchPoints = new List<BranchPoint>(),
                });
            }

            int chainPoolSize = Math.Max(1, nodeCount / 6);
            uint[] pidPool = { 0u, 101u, 202u, 303u };

            var treeMembers = new List<List<string>>();
            for (int t = 0; t < treeCount; t++) treeMembers.Add(new List<string>());

            for (int i = 0; i < nodeCount; i++)
            {
                string id = "r" + i.ToString("D3");
                int treeIdx = rng.Next(treeCount);
                var rec = new Recording
                {
                    RecordingId = id,
                    VesselName = id,
                    MergeState = RollMergeState(rng),
                    TreeId = g.Trees[treeIdx].Id,
                    VesselPersistentId = pidPool[rng.Next(pidPool.Length)],
                    ExplicitStartUT = rng.Next(0, 200),
                };

                if (rng.NextDouble() < 0.45)
                {
                    rec.ChainId = "c" + rng.Next(chainPoolSize);
                    rec.ChainIndex = rng.Next(0, 4);
                    rec.ChainBranch = rng.Next(0, 2);
                }

                if (corrupt && rng.NextDouble() < 0.08)
                {
                    // Tree-id mismatch / legacy orphan: stored in this tree but
                    // claiming a different one (or none).
                    rec.TreeId = rng.NextDouble() < 0.5
                        ? null
                        : g.Trees[rng.Next(treeCount)].Id;
                }

                g.Recordings.Add(rec);
                g.TreeOfRecording.Add(treeIdx);
                treeMembers[treeIdx].Add(id);
            }

            // Duplicate recording id (corrupt only): the same id appearing twice in
            // the committed list, which the id index and the linear scan must both
            // resolve to the first occurrence.
            if (corrupt && nodeCount >= 8 && rng.NextDouble() < 0.5)
            {
                int src = rng.Next(g.Recordings.Count);
                var dup = new Recording
                {
                    RecordingId = g.Recordings[src].RecordingId,
                    VesselName = g.Recordings[src].VesselName + "_dup",
                    MergeState = MergeState.Immutable,
                    TreeId = g.Recordings[src].TreeId,
                    ExplicitStartUT = 1.0,
                };
                g.Recordings.Add(dup);
                g.TreeOfRecording.Add(g.TreeOfRecording[src]);
            }

            // Debris / parent-anchor edges.
            for (int i = 0; i < g.Recordings.Count; i++)
            {
                if (rng.NextDouble() >= 0.18) continue;
                var rec = g.Recordings[i];
                rec.IsDebris = true;
                double roll = rng.NextDouble();
                if (corrupt && roll < 0.15)
                    rec.ParentAnchorRecordingId = rec.RecordingId;          // self anchor
                else if (corrupt && roll < 0.30)
                    rec.ParentAnchorRecordingId = "ghost_anchor_" + i;      // dangling anchor
                else
                    rec.ParentAnchorRecordingId =
                        g.Recordings[rng.Next(g.Recordings.Count)].RecordingId;
            }

            // Branch points, wired into the recordings that reference them.
            var bpTypes = new[]
            {
                BranchPointType.Breakup, BranchPointType.JointBreak,
                BranchPointType.Undock, BranchPointType.Dock,
                BranchPointType.EVA, BranchPointType.Launch,
            };
            for (int t = 0; t < treeCount; t++)
            {
                var members = treeMembers[t];
                if (members.Count == 0) continue;
                int bpCount = Math.Max(1, members.Count / 3);
                for (int b = 0; b < bpCount; b++)
                {
                    var bp = new BranchPoint
                    {
                        Id = "bp_t" + t + "_" + b,
                        Type = bpTypes[rng.Next(bpTypes.Length)],
                        UT = rng.Next(0, 200),
                        ParentRecordingIds = new List<string>(),
                        ChildRecordingIds = new List<string>(),
                    };

                    int parents = corrupt && rng.NextDouble() < 0.25 ? 2 : 1;
                    for (int p = 0; p < parents; p++)
                    {
                        string pid = corrupt && rng.NextDouble() < 0.10
                            ? "ghost_parent_" + b + "_" + p
                            : members[rng.Next(members.Count)];
                        bp.ParentRecordingIds.Add(pid);
                    }

                    int children = 1 + rng.Next(3);
                    for (int c = 0; c < children; c++)
                    {
                        string cid = corrupt && rng.NextDouble() < 0.10
                            ? "ghost_child_" + b + "_" + c
                            : members[rng.Next(members.Count)];
                        bp.ChildRecordingIds.Add(cid);
                    }

                    g.Trees[t].BranchPoints.Add(bp);

                    // Wire the back-references. Random parent/child draws mean a
                    // recording can end up as its own branch-point child, and a
                    // child list can name an ancestor - the cyclic closure shapes
                    // the walker's visited set exists for.
                    for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                    {
                        var rec = FindByIdIn(g.Recordings, bp.ParentRecordingIds[p]);
                        if (rec != null) rec.ChildBranchPointId = bp.Id;
                    }
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        var rec = FindByIdIn(g.Recordings, bp.ChildRecordingIds[c]);
                        if (rec != null) rec.ParentBranchPointId = bp.Id;
                    }
                }
            }

            // Supersede rows.
            int edgeTarget = nodeCount;
            if (corrupt)
            {
                int ghost = 0;
                while (g.Supersedes.Count < edgeTarget)
                {
                    int roll = rng.Next(100);
                    string a = PickId(g, rng);
                    string b = PickId(g, rng);
                    string c = PickId(g, rng);
                    if (roll < 40) g.Supersedes.Add(Rel(a, b));
                    else if (roll < 50) g.Supersedes.Add(Rel(a, a));
                    else if (roll < 60) { g.Supersedes.Add(Rel(a, b)); g.Supersedes.Add(Rel(b, a)); }
                    else if (roll < 68)
                    {
                        g.Supersedes.Add(Rel(a, b));
                        g.Supersedes.Add(Rel(b, c));
                        g.Supersedes.Add(Rel(c, a));
                    }
                    else if (roll < 75) g.Supersedes.Add(Rel(a, "ghost_new_" + ghost++));
                    else if (roll < 81) g.Supersedes.Add(Rel("ghost_old_" + ghost++, b));
                    else if (roll < 87) { g.Supersedes.Add(Rel(a, c)); g.Supersedes.Add(Rel(b, c)); }
                    else if (roll < 92) { g.Supersedes.Add(Rel(a, b)); g.Supersedes.Add(Rel(a, c)); }
                    else if (roll < 95) g.Supersedes.Add(Rel(a, string.Empty));
                    else if (roll < 97) g.Supersedes.Add(null);
                    else g.Supersedes.Add(Rel(PickChainMember(g, rng), PickChainMember(g, rng)));
                }
            }
            else
            {
                // Production shape: at most one outgoing row per id, strictly
                // forward in index order, no dangling / null / empty rows.
                var used = new HashSet<int>();
                int attempts = 0;
                while (g.Supersedes.Count < edgeTarget / 2 && attempts++ < edgeTarget * 4)
                {
                    int from = rng.Next(g.Recordings.Count - 1);
                    if (!used.Add(from)) continue;
                    int to = from + 1 + rng.Next(g.Recordings.Count - from - 1);
                    g.Supersedes.Add(Rel(g.Recordings[from].RecordingId, g.Recordings[to].RecordingId));
                }
            }

            // Rewind points + slots.
            int rpCount = 1 + rng.Next(2);
            for (int r = 0; r < rpCount; r++)
            {
                var slots = new List<ChildSlot>();
                int slotCount = 2 + rng.Next(3);
                for (int s = 0; s < slotCount; s++)
                {
                    string originId;
                    if (corrupt && rng.NextDouble() < 0.15)
                        originId = rng.NextDouble() < 0.5 ? null : "ghost_slot_" + r + "_" + s;
                    else
                        originId = PickId(g, rng);
                    slots.Add(new ChildSlot
                    {
                        SlotIndex = s,
                        OriginChildRecordingId = originId,
                        Controllable = true,
                    });
                }
                g.RewindPoints.Add(new RewindPoint
                {
                    RewindPointId = "rp_" + r,
                    BranchPointId = "bp_t0_0",
                    UT = 0.0,
                    SessionProvisional = false,
                    ChildSlots = slots,
                });
            }

            // A slice of recordings claims provisional ownership of an RP so the
            // retirement cascade's chain-continuation edge is reachable.
            for (int i = 0; i < g.Recordings.Count; i++)
            {
                if (rng.NextDouble() < 0.2)
                    g.Recordings[i].ProvisionalForRpId = g.RewindPoints[rng.Next(g.RewindPoints.Count)].RewindPointId;
            }

            // Retirement rows (some dangling in corrupt mode).
            int retireCount = rng.Next(4);
            for (int i = 0; i < retireCount; i++)
            {
                string id = corrupt && rng.NextDouble() < 0.25
                    ? "ghost_retire_" + i
                    : PickId(g, rng);
                g.Retirements.Add(new RecordingRewindRetirement
                {
                    RetirementId = "rrt_" + i,
                    RecordingId = id,
                    Reason = RecordingRewindRetirement.DefaultReason,
                });
            }

            // Session marker.
            double markerRoll = rng.NextDouble();
            string root;
            if (corrupt && markerRoll < 0.10) root = null;
            else if (corrupt && markerRoll < 0.20) root = "ghost_root";
            else root = PickId(g, rng);
            g.Marker = new ReFlySessionMarker
            {
                SessionId = "fuzz_sess_" + seed,
                TreeId = g.Trees[0].Id,
                ActiveReFlyRecordingId = "rec_provisional",
                OriginChildRecordingId = root,
                RewindPointId = g.RewindPoints[0].RewindPointId,
                InvokedUT = rng.Next(0, 200),
            };

            // Probe ids: every recording id, a few dangling ids, plus the empty /
            // null origins the walkers must answer null for.
            for (int i = 0; i < g.Recordings.Count; i++)
                g.ProbeIds.Add(g.Recordings[i].RecordingId);
            g.ProbeIds.Add("ghost_probe_1");
            g.ProbeIds.Add("ghost_probe_2");
            g.ProbeIds.Add(null);
            g.ProbeIds.Add(string.Empty);

            return g;
        }

        private static MergeState RollMergeState(Random rng)
        {
            int roll = rng.Next(10);
            if (roll < 7) return MergeState.Immutable;
            if (roll < 9) return MergeState.CommittedProvisional;
            return MergeState.NotCommitted;
        }

        private static string PickId(FuzzGraph g, Random rng)
            => g.Recordings[rng.Next(g.Recordings.Count)].RecordingId;

        private static string PickChainMember(FuzzGraph g, Random rng)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var rec = g.Recordings[rng.Next(g.Recordings.Count)];
                if (!string.IsNullOrEmpty(rec.ChainId)) return rec.RecordingId;
            }
            return PickId(g, rng);
        }

        private static Recording FindByIdIn(List<Recording> recs, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < recs.Count; i++)
            {
                if (string.Equals(recs[i].RecordingId, id, StringComparison.Ordinal))
                    return recs[i];
            }
            return null;
        }

        // =====================================================================
        // Installation into the shared statics
        // =====================================================================

        private static void Install(FuzzGraph g)
        {
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();

            for (int t = 0; t < g.Trees.Count; t++)
            {
                var tree = g.Trees[t];
                tree.Recordings.Clear();
                RecordingStore.AddCommittedTreeForTesting(tree);
            }
            for (int i = 0; i < g.Recordings.Count; i++)
            {
                var rec = g.Recordings[i];
                var tree = g.Trees[g.TreeOfRecording[i]];
                tree.AddOrReplaceRecording(rec);
                if (string.IsNullOrEmpty(tree.RootRecordingId))
                    tree.RootRecordingId = rec.RecordingId;
                RecordingStore.AddCommittedInternal(rec);
            }

            var scenario = new ParsekScenario
            {
                // Deliberately EMPTY while the version bumps run below.
                // ParsekScenario.BumpSupersedeStateVersion drives
                // RouteStore.RevalidateSources -> EffectiveState.ComputeERS as a
                // side effect, on the caller's thread and outside any hop bound
                // this harness controls. Feeding the fuzzer's corrupt table
                // through that scaffolding call would make a walker regression
                // surface as a hung test host during INSTALL rather than as a
                // named assertion during the actual invariant checks. The
                // supersede table is installed immediately after the bumps and
                // every cache is dropped, so the subjects still see it.
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = g.Retirements,
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = g.RewindPoints,
                ActiveReFlySessionMarker = g.Marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            scenario.RecordingSupersedes = g.Supersedes;
            EffectiveState.ResetCachesForTesting();
        }

        private static void InstallTree(string treeId, List<Recording> recordings, List<BranchPoint> bps)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                BranchPoints = bps ?? new List<BranchPoint>(),
            };
            for (int i = 0; i < recordings.Count; i++)
            {
                recordings[i].TreeId = treeId;
                tree.AddOrReplaceRecording(recordings[i]);
                RecordingStore.AddCommittedInternal(recordings[i]);
            }
            if (recordings.Count > 0) tree.RootRecordingId = recordings[0].RecordingId;
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        private static Recording MakeRecording(string id, string treeId)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TreeId = treeId,
            };
        }

        private static RecordingSupersedeRelation Rel(string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = "rsr_" + oldId + "_" + newId,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 0.0,
            };
        }

        private static void Check(bool condition, FuzzGraph g, string message)
        {
            if (condition) return;
            Assert.True(false, message + "\n" + g.Dump());
        }

        private static string Show(string s) => s == null ? "<null>" : (s.Length == 0 ? "<empty>" : s);

        // =====================================================================
        // Observable hop bound
        // =====================================================================

        /// <summary>
        /// Raised by <see cref="HopBoundedSupersedeList"/> when a walker reads more
        /// supersede rows than a terminating walk possibly can.
        /// </summary>
        private sealed class HopBoundExceededException : Exception
        {
            internal HopBoundExceededException(string message) : base(message) { }
        }

        /// <summary>
        /// Read-counting proxy over the supersede table.
        ///
        /// <para>
        /// The walkers take the table as <c>IReadOnlyList</c> and rescan it once
        /// per hop, so the total number of element reads IS an observable proxy for
        /// the hop count - the thing the public API otherwise gives no way to
        /// assert. Capping reads at what a visited-set-guarded walk can possibly
        /// perform turns "the walker did not terminate" from a hang into an
        /// immediate, named exception on the calling thread. The thread budget in
        /// <see cref="WithinBudget"/> stays as the backstop for the paths that do
        /// not go through this list (closure BFS, ERS).
        /// </para>
        /// </summary>
        private sealed class HopBoundedSupersedeList : System.Collections.Generic.IReadOnlyList<RecordingSupersedeRelation>
        {
            private readonly IReadOnlyList<RecordingSupersedeRelation> inner;
            private readonly long maxReads;
            private long reads;

            internal HopBoundedSupersedeList(IReadOnlyList<RecordingSupersedeRelation> inner, long maxReads)
            {
                this.inner = inner;
                this.maxReads = maxReads;
            }

            public RecordingSupersedeRelation this[int index]
            {
                get
                {
                    if (++reads > maxReads)
                    {
                        throw new HopBoundExceededException(
                            "read " + reads + " supersede rows, above the " + maxReads +
                            " a terminating walk can perform");
                    }
                    return inner[index];
                }
            }

            public int Count => inner.Count;

            public System.Collections.Generic.IEnumerator<RecordingSupersedeRelation> GetEnumerator()
                => inner.GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => inner.GetEnumerator();
        }

        /// <summary>
        /// Invokes <paramref name="call"/> with a hop-bounded view of the supersede
        /// table and converts a bound violation into a graph-dumping assertion.
        /// </summary>
        private static T BoundedCall<T>(
            FuzzGraph g,
            string label,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            long maxReads,
            Func<IReadOnlyList<RecordingSupersedeRelation>, T> call)
        {
            var bounded = new HopBoundedSupersedeList(supersedes, maxReads);
            try
            {
                return call(bounded);
            }
            catch (HopBoundExceededException ex)
            {
                Check(false, g, label + " exceeded its observable hop bound: " + ex.Message);
                throw; // unreachable; Check always throws on false
            }
        }

        private static long SingleWalkReadBudget(int universeCount, int supersedeCount)
            => (long)(universeCount + 2) * Math.Max(1, supersedeCount) + 16L;

        /// <summary>
        /// Hop-bounded view for the small deterministic pins, which have no graph
        /// dump to attach - a bound violation surfaces as
        /// <see cref="HopBoundExceededException"/> and fails the test directly.
        /// </summary>
        private static IReadOnlyList<RecordingSupersedeRelation> Bounded(
            List<RecordingSupersedeRelation> supersedes, long maxReads)
            => new HopBoundedSupersedeList(supersedes, maxReads);

        // =====================================================================
        // Graph model + reproduction dump
        // =====================================================================

        private sealed class FuzzGraph
        {
            internal int Seed;
            internal int NodeCount;
            internal bool Corrupt;
            internal readonly List<Recording> Recordings = new List<Recording>();
            internal readonly List<int> TreeOfRecording = new List<int>();
            internal readonly List<RecordingTree> Trees = new List<RecordingTree>();
            internal readonly List<RecordingSupersedeRelation> Supersedes =
                new List<RecordingSupersedeRelation>();
            internal readonly List<RecordingRewindRetirement> Retirements =
                new List<RecordingRewindRetirement>();
            internal readonly List<RewindPoint> RewindPoints = new List<RewindPoint>();
            internal readonly List<string> ProbeIds = new List<string>();
            internal ReFlySessionMarker Marker;

            /// <summary>
            /// Deep-enough clone with every ChainId stripped, used for the
            /// chain-free walker-agreement invariant. Recordings are cloned so the
            /// original graph's chain data survives for the caller's re-install.
            /// </summary>
            internal FuzzGraph CloneWithoutChains()
            {
                var clone = new FuzzGraph
                {
                    Seed = Seed,
                    NodeCount = NodeCount,
                    Corrupt = Corrupt,
                    Marker = Marker,
                };
                for (int t = 0; t < Trees.Count; t++)
                {
                    clone.Trees.Add(new RecordingTree
                    {
                        Id = Trees[t].Id,
                        TreeName = Trees[t].TreeName,
                        BranchPoints = Trees[t].BranchPoints,
                    });
                }
                for (int i = 0; i < Recordings.Count; i++)
                {
                    var src = Recordings[i];
                    clone.Recordings.Add(new Recording
                    {
                        RecordingId = src.RecordingId,
                        VesselName = src.VesselName,
                        MergeState = src.MergeState,
                        TreeId = src.TreeId,
                        VesselPersistentId = src.VesselPersistentId,
                        ExplicitStartUT = src.ExplicitStartUT,
                        IsDebris = src.IsDebris,
                        ParentAnchorRecordingId = src.ParentAnchorRecordingId,
                        ParentBranchPointId = src.ParentBranchPointId,
                        ChildBranchPointId = src.ChildBranchPointId,
                        ProvisionalForRpId = src.ProvisionalForRpId,
                        // ChainId deliberately left null.
                        ChainIndex = -1,
                        ChainBranch = 0,
                    });
                    clone.TreeOfRecording.Add(TreeOfRecording[i]);
                }
                clone.Supersedes.AddRange(Supersedes);
                clone.Retirements.AddRange(Retirements);
                clone.RewindPoints.AddRange(RewindPoints);
                clone.ProbeIds.AddRange(ProbeIds);
                return clone;
            }

            internal string Dump()
            {
                var sb = new StringBuilder();
                sb.Append("FuzzGraph seed=").Append(Seed)
                  .Append(" nodes=").Append(NodeCount)
                  .Append(" corrupt=").Append(Corrupt)
                  .Append(" trees=").Append(Trees.Count)
                  .Append(" recordings=").Append(Recordings.Count)
                  .Append(" supersedes=").Append(Supersedes.Count)
                  .AppendLine();
                sb.AppendLine("  marker.origin=" + Show(Marker?.OriginChildRecordingId) +
                              " invokedUT=" + (Marker == null ? "<none>" : Marker.InvokedUT.ToString(
                                  System.Globalization.CultureInfo.InvariantCulture)));
                for (int i = 0; i < Recordings.Count; i++)
                {
                    var r = Recordings[i];
                    sb.Append("  rec ").Append(r.RecordingId)
                      .Append(" tree=").Append(Show(r.TreeId))
                      .Append(" state=").Append(r.MergeState)
                      .Append(" chain=").Append(Show(r.ChainId))
                      .Append("/").Append(r.ChainIndex).Append("/").Append(r.ChainBranch)
                      .Append(" pid=").Append(r.VesselPersistentId)
                      .Append(" debris=").Append(r.IsDebris)
                      .Append(" anchor=").Append(Show(r.ParentAnchorRecordingId))
                      .Append(" parentBp=").Append(Show(r.ParentBranchPointId))
                      .Append(" childBp=").Append(Show(r.ChildBranchPointId))
                      .AppendLine();
                }
                for (int t = 0; t < Trees.Count; t++)
                {
                    var bps = Trees[t].BranchPoints;
                    for (int b = 0; bps != null && b < bps.Count; b++)
                    {
                        var bp = bps[b];
                        sb.Append("  bp ").Append(bp.Id).Append(" type=").Append(bp.Type)
                          .Append(" parents=[").Append(string.Join(",", bp.ParentRecordingIds))
                          .Append("] children=[").Append(string.Join(",", bp.ChildRecordingIds))
                          .Append("]").AppendLine();
                    }
                }
                for (int i = 0; i < Supersedes.Count; i++)
                {
                    var rel = Supersedes[i];
                    sb.Append("  sup ")
                      .Append(rel == null ? "<null row>" : Show(rel.OldRecordingId) + " -> " + Show(rel.NewRecordingId))
                      .AppendLine();
                }
                for (int i = 0; i < Retirements.Count; i++)
                    sb.Append("  retire ").Append(Show(Retirements[i].RecordingId)).AppendLine();
                for (int i = 0; i < RewindPoints.Count; i++)
                {
                    var rp = RewindPoints[i];
                    sb.Append("  rp ").Append(rp.RewindPointId).Append(" slots=[");
                    for (int s = 0; s < rp.ChildSlots.Count; s++)
                    {
                        if (s > 0) sb.Append(",");
                        sb.Append(Show(rp.ChildSlots[s].OriginChildRecordingId));
                    }
                    sb.Append("]").AppendLine();
                }
                return sb.ToString();
            }
        }
    }
}
