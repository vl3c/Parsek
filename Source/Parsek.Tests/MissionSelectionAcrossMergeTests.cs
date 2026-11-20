using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A Mission's include / exclude choices are stored as composition interval keys
    /// (<see cref="Mission.ExcludedIntervalKeys"/>) and validated by
    /// <see cref="MissionStore.ReconcileSelections"/> only at load. These cells drive the
    /// production code paths that change a committed tree's legs between two loads (the
    /// commit-time optimizer split, the Re-Fly merge splitter plus its fork, and the
    /// switch-segment continuation) and prove that, before any reconcile runs, every stored
    /// key still names the SAME interval (same start, same owning vessel), and that the next
    /// load's save / load / reconcile round trip keeps every key.
    ///
    /// Fixture (one tree, every leg controlled):
    /// <code>
    ///   X [0..50] --decouple@50--> C [50..150] (continuation, child[0])
    ///                         \--> P [50..120] (probe peel)
    ///   C        --decouple@150-> C2 [150..250] (continuation, child[0])
    ///                         \--> S [150..180] (stage peel)
    /// </code>
    /// Keys: X [0..50], X/seg1 [50..150], X/seg2 [150..250], P, S.
    /// </summary>
    [Collection("Sequential")]
    public class MissionSelectionAcrossMergeTests : IDisposable
    {
        private const string TreeId = "tree_msel";
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool priorMissionSuppress;

        public MissionSelectionAcrossMergeTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorMissionSuppress = MissionStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            MissionStore.SuppressLogging = false;

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MissionStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            MissionStore.SuppressLogging = priorMissionSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MissionStore.ResetForTesting();
        }

        // ---------- fixture ----------------------------------------------------

        private static TrajectoryPoint Pt(double ut, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                altitude = alt,
                latitude = 0.0,
                longitude = 0.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        // A leg with one Atmospheric section (no optimizer split boundary), or, with
        // splitAtUT set, an Atmospheric -> SurfaceStationary boundary the optimizer splits.
        private static Recording Leg(string id, double start, double end,
            double splitAtUT = double.NaN, TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "V_" + id,
                TreeId = TreeId,
                VesselPersistentId = 1000u,
                MergeState = MergeState.Immutable,
                TerminalStateValue = terminal,
            };
            if (double.IsNaN(splitAtUT))
            {
                double mid = (start + end) / 2.0;
                rec.Points.Add(Pt(start, 5000));
                rec.Points.Add(Pt(mid, 5000));
                rec.Points.Add(Pt(end, 5000));
                rec.TrackSections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = start,
                    endUT = end,
                    sampleRateHz = 1f,
                    minAltitude = float.NaN,
                    maxAltitude = float.NaN,
                    frames = new List<TrajectoryPoint> { Pt(start, 5000), Pt(mid, 5000), Pt(end, 5000) },
                });
            }
            else
            {
                rec.Points.Add(Pt(start, 5000));
                rec.Points.Add(Pt(splitAtUT - 0.01, 200));
                rec.Points.Add(Pt(splitAtUT, 100));
                rec.Points.Add(Pt(end, 0));
                rec.TrackSections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    startUT = start,
                    endUT = splitAtUT,
                    frames = new List<TrajectoryPoint>(),
                });
                rec.TrackSections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceStationary,
                    startUT = splitAtUT,
                    endUT = end,
                    frames = new List<TrajectoryPoint>(),
                });
            }
            return rec;
        }

        private static RecordingTree InstallFixtureTree(double cSplitAtUT = double.NaN)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Selection Fixture",
                RootRecordingId = "X",
                BranchPoints = new List<BranchPoint>(),
            };
            var legs = new[]
            {
                Leg("X", 0, 50),
                Leg("C", 50, 150, cSplitAtUT),
                Leg("P", 50, 120, terminal: TerminalState.Destroyed),
                Leg("C2", 150, 250, terminal: TerminalState.Orbiting),
                Leg("S", 150, 180, terminal: TerminalState.Destroyed),
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_dec1",
                Type = BranchPointType.JointBreak,
                UT = 50,
                SplitCause = "DECOUPLE",
                ParentRecordingIds = new List<string> { "X" },
                ChildRecordingIds = new List<string> { "C", "P" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_dec2",
                Type = BranchPointType.JointBreak,
                UT = 150,
                SplitCause = "DECOUPLE",
                ParentRecordingIds = new List<string> { "C" },
                ChildRecordingIds = new List<string> { "C2", "S" },
            });
            legs[0].ChildBranchPointId = "bp_dec1";
            legs[1].ParentBranchPointId = "bp_dec1";
            legs[2].ParentBranchPointId = "bp_dec1";
            legs[1].ChildBranchPointId = "bp_dec2";
            legs[3].ParentBranchPointId = "bp_dec2";
            legs[4].ParentBranchPointId = "bp_dec2";
            foreach (var leg in legs)
            {
                tree.AddOrReplaceRecording(leg);
                RecordingStore.AddCommittedInternal(leg);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private struct IntervalFact
        {
            public double StartUT;
            public double EndUT;
            public string Owner;
        }

        // Every selectable composition key of the tree, with the interval it names.
        private static Dictionary<string, IntervalFact> Intervals(RecordingTree tree)
        {
            var map = new Dictionary<string, IntervalFact>(StringComparer.Ordinal);
            Collect(MissionCompositionBuilder.Build(MissionStructureBuilder.Build(tree)), map);
            return map;
        }

        private static void Collect(IReadOnlyList<MissionCompositionNode> nodes,
            Dictionary<string, IntervalFact> into)
        {
            if (nodes == null) return;
            for (int i = 0; i < nodes.Count; i++)
            {
                MissionCompositionNode n = nodes[i];
                if (n == null) continue;
                if (n.IsSelectable && !string.IsNullOrEmpty(n.HeadLegId))
                    into[n.HeadLegId] = new IntervalFact
                    {
                        StartUT = n.StartUT,
                        EndUT = n.EndUT,
                        Owner = n.OwnerHeadId,
                    };
                Collect(n.Children, into);
            }
        }

        private static string Describe(Dictionary<string, IntervalFact> map)
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Join(", ", map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=[" + kv.Value.StartUT.ToString("R", ic) + ".."
                    + kv.Value.EndUT.ToString("R", ic) + "]@" + kv.Value.Owner));
        }

        private static Mission SeedMissionExcluding(RecordingTree tree, params string[] keys)
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { tree });
            Mission m = MissionStore.FindOriginalMission(tree.Id);
            Assert.NotNull(m);
            foreach (string k in keys)
                m.ExcludedIntervalKeys.Add(k);
            return m;
        }

        // The stored keys still name the same intervals: same start, same owning vessel.
        private static void AssertKeysStillNameSameIntervals(
            IEnumerable<string> keys,
            Dictionary<string, IntervalFact> before,
            Dictionary<string, IntervalFact> after,
            bool checkEnd)
        {
            foreach (string key in keys)
            {
                Assert.True(after.ContainsKey(key),
                    $"excluded key '{key}' no longer names an interval after the merge; after: {Describe(after)}");
                Assert.True(before[key].StartUT == after[key].StartUT,
                    $"excluded key '{key}' moved start {before[key].StartUT} -> {after[key].StartUT}; after: {Describe(after)}");
                Assert.True(before[key].Owner == after[key].Owner,
                    $"excluded key '{key}' changed owner {before[key].Owner} -> {after[key].Owner}; after: {Describe(after)}");
                if (checkEnd)
                    Assert.True(before[key].EndUT == after[key].EndUT,
                        $"excluded key '{key}' moved end {before[key].EndUT} -> {after[key].EndUT}; after: {Describe(after)}");
            }
        }

        // The next load: MissionStore save -> load, then ReconcileSelections over the
        // post-merge trees. Returns the reloaded mission.
        private static Mission SaveLoadAndReconcile(string missionId, RecordingTree tree, out int removed)
        {
            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            MissionStore.Load(node);
            removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tree });
            return MissionStore.FindById(missionId);
        }

        // ---------- baseline ---------------------------------------------------

        [Fact]
        public void Fixture_MintsTheExpectedKeys()
        {
            RecordingTree tree = InstallFixtureTree();
            var map = Intervals(tree);
            Assert.True(new HashSet<string> { "X", "X/seg1", "X/seg2", "P", "S" }.SetEquals(map.Keys),
                Describe(map));
            Assert.Equal(50.0, map["X/seg1"].StartUT);
            Assert.Equal(150.0, map["X/seg1"].EndUT);
            Assert.Equal(150.0, map["X/seg2"].StartUT);
            Assert.Equal(250.0, map["X/seg2"].EndUT);
        }

        // ---------- tree commit: the commit-time optimizer pass ----------------

        // Fails if: the commit-time optimizer split of a MID-RUN leg (C, which ends at the
        // second decouple) renames or re-targets any interval key - e.g. the decouple branch
        // point stays on the head half, so C2 / S fall out of X's run and "X/seg2" stops
        // existing - or the next load's reconcile drops a key.
        [Fact]
        public void TreeCommitOptimizerSplit_MidRunLeg_KeysStillNameSameIntervals_AndReconcileKeepsThem()
        {
            RecordingTree tree = InstallFixtureTree(cSplitAtUT: 100);
            var before = Intervals(tree);
            string[] excluded = { "X/seg1", "X/seg2", "S" };
            Mission m = SeedMissionExcluding(tree, excluded);

            CommittedListOptimizer.RunOptimizationPass();

            // Sanity: the optimizer actually split C, and kept C's id on the head half.
            Assert.Equal(6, tree.Recordings.Count);
            Assert.True(tree.Recordings["C"].EndUT <= 100.0, "C was not split at its section boundary");

            var after = Intervals(tree);
            AssertKeysStillNameSameIntervals(excluded, before, after, checkEnd: true);
            Assert.True(new HashSet<string>(before.Keys).SetEquals(after.Keys), Describe(after));

            Mission reloaded = SaveLoadAndReconcile(m.Id, tree, out int removed);
            Assert.Equal(0, removed);
            Assert.True(new HashSet<string>(excluded).SetEquals(reloaded.ExcludedIntervalKeys));
        }

        // ---------- Re-Fly merge -----------------------------------------------

        // Fails if: the Re-Fly merge splitter (HEAD keeps the origin id, TIP is a new chain
        // segment) or the merged fork re-targets a stored key, or the next load's reconcile
        // drops one. Also pins the design's open-question-3a contract: the fork is a NEW
        // through-line head key that no earlier Mission references, so it defaults to included.
        [Fact]
        public void ReFlyMerge_SplitterAndFork_KeysStillNameSameIntervals_ForkDefaultsIncluded()
        {
            RecordingTree tree = InstallFixtureTree();
            var before = Intervals(tree);
            string[] excluded = { "X/seg2", "P", "S" };
            Mission m = SeedMissionExcluding(tree, excluded);

            // The Re-Fly re-flies C2 from a rewind point inside it (UT 200, a recorded sample).
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_msel",
                TreeId = TreeId,
                ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "C2",
                SupersedeTargetId = "C2",
                RewindPointUT = 200.0,
                InvokedUT = 200.0,
            };
            var split = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null);
            Assert.False(split.Skipped, split.SkipReason);
            Assert.Equal("C2", split.HeadRecordingId);

            // The merged fork, attached the way RewindInvoker.BuildProvisionalRecording and
            // EnsureForkAttachedToTree attach it: ParentBranchPointId set, no chain identity,
            // not listed in the branch point's ChildRecordingIds.
            Recording fork = Leg("rec_fork", 200, 320, terminal: TerminalState.Landed);
            fork.ParentBranchPointId = "bp_dec2";
            tree.Recordings[fork.RecordingId] = fork;
            RecordingStore.AddCommittedInternal(fork);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_msel",
                        OldRecordingId = split.TipRecordingId,
                        NewRecordingId = fork.RecordingId,
                        UT = 200.0,
                    },
                },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            var after = Intervals(tree);
            AssertKeysStillNameSameIntervals(excluded, before, after, checkEnd: true);
            // The TIP joins C2's chain, so it mints no key of its own.
            Assert.False(after.ContainsKey(split.TipRecordingId), Describe(after));
            // The fork is a new through-line head: a new key, not excluded (default included).
            Assert.True(after.ContainsKey("rec_fork"), Describe(after));
            Assert.Equal("rec_fork", after["rec_fork"].Owner);
            Assert.DoesNotContain("rec_fork", m.ExcludedIntervalKeys);

            Mission reloaded = SaveLoadAndReconcile(m.Id, tree, out int removed);
            Assert.Equal(0, removed);
            Assert.True(new HashSet<string>(excluded).SetEquals(reloaded.ExcludedIntervalKeys));
            Assert.DoesNotContain("rec_fork", reloaded.ExcludedIntervalKeys);
        }

        // ---------- switch-segment merge ---------------------------------------

        // Fails if: attaching a switch continuation under the terminal leaf C2 (the player
        // switched back to that vessel and flew on) re-targets a stored key or the reload
        // drops one. Pins today's semantics for the LAST interval: the continuation is the
        // same vessel (child[0] of a VesselSwitchContinuation branch point), so it extends
        // "X/seg2" rather than minting a key - an exclusion of that interval now also covers
        // the continued flight (open question 3a: topology appended inside an excluded
        // interval inherits its exclusion; only a NEW head defaults to included).
        [Fact]
        public void SwitchSegmentMerge_ContinuationUnderLeaf_KeysKept_LastIntervalExtends()
        {
            RecordingTree tree = InstallFixtureTree();
            var before = Intervals(tree);
            string[] excluded = { "X/seg1", "X/seg2", "P" };
            Mission m = SeedMissionExcluding(tree, excluded);

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, "C2", focusedVesselPersistentId: 1000u,
                focusedVesselName: "V_C2",
                switchUT: 350.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: "rec_switch", newBranchPointId: "bp_switch",
                initialBoundaryPointFactory: ut => Pt(ut, 5000),
                sourceVesselPersistentId: 0u);
            Assert.True(result.Created, result.FailureReason);
            Recording cont = tree.Recordings["rec_switch"];
            cont.Points.Add(Pt(420.0, 5000));
            cont.ExplicitEndUT = 420.0;

            var after = Intervals(tree);
            AssertKeysStillNameSameIntervals(excluded, before, after, checkEnd: false);
            Assert.Equal(before["X/seg1"].EndUT, after["X/seg1"].EndUT);
            Assert.Equal(before["P"].EndUT, after["P"].EndUT);
            Assert.True(new HashSet<string>(before.Keys).SetEquals(after.Keys), Describe(after));
            Assert.Equal(420.0, after["X/seg2"].EndUT);

            Mission reloaded = SaveLoadAndReconcile(m.Id, tree, out int removed);
            Assert.Equal(0, removed);
            Assert.True(new HashSet<string>(excluded).SetEquals(reloaded.ExcludedIntervalKeys));
        }
    }
}
