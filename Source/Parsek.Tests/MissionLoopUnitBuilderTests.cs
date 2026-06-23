using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    // Phase C: the pure adapter that turns a looping Mission's selection into a span-clock
    // LoopUnitSet, plus the shared MissionSelection include cascade it depends on. No engine
    // wiring (Phase D); these drive the pure logic with directly constructed inputs.
    [Collection("Sequential")]
    public class MissionLoopUnitBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionLoopUnitBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        // --- Helpers (mirror MissionStructureTests) ---

        // The global auto-loop interval most tests use. Far below any span here, so an Auto mission's
        // OverlapCadenceSeconds resolves to this and produces self-overlap. Tests that care about a
        // specific value pass their own.
        private const double DefaultAutoInterval = 30.0;

        // Build with the default auto interval (most tests do not exercise the overlap cadence).
        private static GhostPlaybackLogic.LoopUnitSet Build(
            IReadOnlyList<Mission> missions,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            double autoInterval = DefaultAutoInterval)
        {
            return MissionLoopUnitBuilder.Build(missions, trees, committed, autoInterval);
        }

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, int chainBranch = 0, string vessel = "V",
            string eva = null, string parentAnchor = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                EvaCrewName = eva,
                ParentAnchorRecordingId = parentAnchor
            };
        }

        private static BranchPoint BP(string id, BranchPointType type,
            string[] parents, string[] children, double ut = 0)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children)
            };
        }

        private static RecordingTree Tree(string id, Recording[] recs,
            BranchPoint[] bps = null)
        {
            var tree = new RecordingTree
            {
                Id = id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            if (bps != null)
                tree.BranchPoints.AddRange(bps);
            return tree;
        }

        private static Mission LoopMission(string treeId, LoopTimeUnit unit = LoopTimeUnit.Auto,
            double interval = 0.0, string[] excluded = null)
        {
            var m = new Mission("m1", treeId, "Loopy")
            {
                LoopPlayback = true,
                LoopTimeUnit = unit,
                LoopIntervalSeconds = interval
            };
            if (excluded != null)
                foreach (var e in excluded)
                    m.ExcludedThroughLineHeadIds.Add(e);
            return m;
        }

        // A simple 3-leg env-split linear vessel tree, committed in StartUT order.
        private static RecordingTree LinearTree(string id = "t1")
        {
            return Tree(id, new[]
            {
                Leg("a", "C", 0, 100, 200),
                Leg("b", "C", 1, 200, 300),
                Leg("c", "C", 2, 300, 410)
            });
        }

        // --- MissionLoopUnitBuilder.Build ---

        [Fact]
        public void Build_NoLoopingMission_ReturnsEmpty()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission>
            {
                new Mission("m0", "t1", "NotLooping") { LoopPlayback = false }
            };

            var set = Build(missions, new[] { tree }, committed);

            Assert.Equal(0, set.Count);
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty, set);
        }

        [Fact]
        public void Build_NullOrEmptyMissions_ReturnsEmpty()
        {
            Assert.Equal(0, MissionLoopUnitBuilder.Build(null, null, null, DefaultAutoInterval).Count);
            Assert.Equal(0, MissionLoopUnitBuilder.Build(
                new List<Mission>(), new List<RecordingTree>(), new List<Recording>(),
                DefaultAutoInterval).Count);
        }

        [Fact]
        public void Build_TreeNotFound_ReturnsEmpty()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("nope") };

            var set = Build(missions, new[] { tree }, committed);

            Assert.Equal(0, set.Count);
            Assert.Contains(logLines, l => l.Contains("[Mission]") && l.Contains("tree not found"));
        }

        [Fact]
        public void Build_LinearMission_AllIncluded_OneUnit_AutoCadenceIsSpan()
        {
            var tree = LinearTree();
            // Committed in StartUT order: a(100..200), b(200..300), c(300..410).
            var committed = new List<Recording>
            {
                tree.Recordings["a"], tree.Recordings["b"], tree.Recordings["c"]
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices); // StartUT order
            Assert.Equal(0, unit.OwnerIndex);                    // earliest start
            Assert.Equal(100.0, unit.SpanStartUT);               // min start
            Assert.Equal(410.0, unit.SpanEndUT);                 // max end
            Assert.Equal(310.0, unit.CadenceSeconds);            // span-clock cadence Auto: span (310 >= MinCycle)
            // Auto overlap cadence = the GLOBAL auto-loop interval (NOT the span), like single
            // recordings. 30 < 310 span -> the mission self-overlaps.
            Assert.Equal(DefaultAutoInterval, unit.OverlapCadenceSeconds);
            Assert.True(unit.OverlapCadenceSeconds < (unit.SpanEndUT - unit.SpanStartUT));
            // Every member resolves to the same owner.
            Assert.True(set.IsMember(1) && set.IsMember(2));
            Assert.Equal(0, set.OwnerByIndex[2]);
        }

        [Fact]
        public void Build_SetAnchor_FlowsToPhaseAnchorUT()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMission("t1", LoopTimeUnit.Auto);
            mission.LoopAnchorUT = 7777.0; // loop was enabled at this UT
            var missions = new List<Mission> { mission };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(7777.0, unit.PhaseAnchorUT); // explicit anchor flows through
            Assert.Equal(100.0, unit.SpanStartUT);    // span start unchanged
            // The Verbose summary records the anchor.
            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("MissionLoopUnit") && l.Contains("phaseAnchor=7777"));
        }

        [Fact]
        public void Build_NaNAnchor_ClampsToSpanEndUT_FirstPlayFloor()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var mission = LoopMission("t1", LoopTimeUnit.Auto);
            Assert.True(double.IsNaN(mission.LoopAnchorUT)); // default, never enabled through the store
            var missions = new List<Mission> { mission };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            // A NaN anchor would fall back to spanStartUT (the old absolute-phase behavior), but the
            // first-play floor clamps it to spanEndUT: a looped mission must not relaunch before its
            // first real play (the original recording, [spanStart, spanEnd]) completes.
            Assert.Equal(unit.SpanEndUT, unit.PhaseAnchorUT);
            Assert.Equal(410.0, unit.PhaseAnchorUT);
        }

        [Fact]
        public void Build_OwnerAndOrder_FollowStartUT_NotCommittedOrder()
        {
            var tree = LinearTree();
            // Committed out of StartUT order: c first, then a, then b.
            var committed = new List<Recording>
            {
                tree.Recordings["c"], // index 0, start 300
                tree.Recordings["a"], // index 1, start 100
                tree.Recordings["b"]  // index 2, start 200
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(1, out var unit));
            // Members sorted by StartUT: a(idx1,100), b(idx2,200), c(idx0,300).
            Assert.Equal(new[] { 1, 2, 0 }, unit.MemberIndices);
            Assert.Equal(1, unit.OwnerIndex); // a is earliest start
            Assert.Equal(100.0, unit.SpanStartUT);
            Assert.Equal(410.0, unit.SpanEndUT);
        }

        [Fact]
        public void Build_ExplicitPeriod_AboveSpan_UsesPeriod_NoOverlap()
        {
            var tree = LinearTree(); // span = 310
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Sec, interval: 600.0) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(600.0, unit.CadenceSeconds);          // span-clock: period >= span -> period
            Assert.Equal(600.0, unit.OverlapCadenceSeconds);   // explicit period kept (>= span -> no overlap)
            Assert.False(unit.OverlapCadenceSeconds < (unit.SpanEndUT - unit.SpanStartUT));
        }

        [Fact]
        public void Build_ExplicitPeriod_BelowSpan_SpanClockRaised_OverlapKeepsPeriod()
        {
            var tree = LinearTree(); // span = 310
            var committed = new List<Recording>(tree.Recordings.Values);
            // 50s period, span 310. cap=12 -> floor = 310/12 = 25.83, so 50 > floor: period kept.
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Sec, interval: 50.0) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(310.0, unit.CadenceSeconds);        // span-clock: period < span -> raised to span
            Assert.Equal(50.0, unit.OverlapCadenceSeconds);  // overlap: explicit period kept, NOT raised
            Assert.True(unit.OverlapCadenceSeconds < (unit.SpanEndUT - unit.SpanStartUT)); // self-overlaps
        }

        [Fact]
        public void Build_ShortPeriod_CapRaisesOverlapCadence_SpanClockUnaffected()
        {
            var tree = LinearTree(); // span = 310
            var committed = new List<Recording>(tree.Recordings.Values);
            // A 1s period would need ceil(310/1) = 310 instances, far over the cap.
            // The cap raises the overlap cadence to the minimum that keeps the count within
            // it: ceil(310 / cadence) <= cap -> cadence >= 310/cap.
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Sec, interval: 1.0) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            double span = unit.SpanEndUT - unit.SpanStartUT;
            int cap = GhostPlayback.MaxOverlapMissionInstances; // per-recording cap (20)
            // Cap strictly observed: live instance count never exceeds the cap.
            Assert.True(Math.Ceiling(span / unit.OverlapCadenceSeconds) <= cap);
            // And raised no further than necessary: one notch lower would breach the cap.
            Assert.True(unit.OverlapCadenceSeconds >= span / cap);
            // Span-clock cadence is unaffected by the overlap cap (still raised to span).
            Assert.Equal(310.0, unit.CadenceSeconds);
            // Still overlaps (cadence < span).
            Assert.True(unit.OverlapCadenceSeconds < span);
        }

        [Fact]
        public void Build_AutoInterval_AtOrAboveSpan_NoOverlap()
        {
            var tree = LinearTree(); // span = 310
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            // Global auto interval >= span: a single instance, no self-overlap.
            var set = Build(missions, new[] { tree }, committed, autoInterval: 400.0);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(400.0, unit.OverlapCadenceSeconds);  // Auto overlap cadence = global interval
            double span = unit.SpanEndUT - unit.SpanStartUT;
            Assert.False(unit.OverlapCadenceSeconds < span);  // >= span -> single span instance
        }

        [Fact]
        public void Build_TinyPeriodAndTinySpan_ClampedToMinCycleDuration()
        {
            // Single tiny leg so span < MinCycleDuration, and a zero period.
            var tree = Tree("t1", new[] { Leg("a", "C", 0, 100.0, 101.0) }); // span = 1
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Sec, interval: 0.0) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(LoopTiming.MinCycleDuration, unit.CadenceSeconds); // 5.0
        }

        [Fact]
        public void Build_AutoTinySpan_ClampedToMinCycleDuration()
        {
            var tree = Tree("t1", new[] { Leg("a", "C", 0, 100.0, 102.0) }); // span = 2
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(LoopTiming.MinCycleDuration, unit.CadenceSeconds);
        }

        [Fact]
        public void Build_ExcludingForkInterval_DropsThatBranchesMemberLegs()
        {
            // Trunk "root" (env-split root->c1->c2) with two forks at bp1: vessel-continuation
            // "c1" stays in the trunk; a separate craft "fork" branches off. Excluding the
            // fork's interval (interval-level selection) must drop the fork's member legs.
            var tree = Tree("t1",
                new[]
                {
                    Leg("root", "CR", 0, 0, 100),
                    Leg("c1", "CC", 0, 100, 200),
                    Leg("c2", "CC", 1, 200, 300),
                    Leg("fork", "CF", 0, 100, 260, parentAnchor: "root") // offshoot craft
                },
                new[]
                {
                    BP("bp1", BranchPointType.Breakup, new[] { "root" }, new[] { "c1", "fork" })
                });
            var committed = new List<Recording>
            {
                tree.Recordings["root"], // 0
                tree.Recordings["c1"],   // 1
                tree.Recordings["c2"],   // 2
                tree.Recordings["fork"]  // 3
            };

            // Sanity: the through-line view should expose "fork" as an offshoot head.
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(tree));
            Assert.Contains("fork", view.ByHeadId.Keys);

            // Exclude the fork's interval (interval-level selection).
            var forkMission = LoopMission("t1", LoopTimeUnit.Auto);
            forkMission.ExcludedIntervalKeys.Add("fork");
            var missions = new List<Mission> { forkMission };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            // root + c1 + c2 remain; fork (index 3) is gone.
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices);
            Assert.False(set.IsMember(3));
            Assert.Equal(0.0, unit.SpanStartUT);
            Assert.Equal(300.0, unit.SpanEndUT); // fork's 260 end no longer extends the span
        }

        [Fact]
        public void Build_ExcludingLaunchInterval_StartTrimsSpanAndMemberWindow()
        {
            // A pod that keeps its recording across a probe decouple: leg "L" [0,120] (the launch
            // stack), the controlled probe "probe" decouples at 30, and the pod's recaptured
            // continuation "cont" [120,200] picks up after an EVA. The pod through-line is L+cont;
            // excluding the launch interval (key "L") must start-trim the pod to the decouple:
            // the span starts at 30 and member L's render window begins at 30 (not 0).
            var tree = Tree("t1",
                new[]
                {
                    Leg("L", "C", 0, 0, 120),
                    Leg("probe", "C2", 0, 30, 84, parentAnchor: "L", vessel: "Probe"),
                    Leg("cont", "C3", 0, 120, 200),
                    Leg("bob", "C4", 0, 120, 150, eva: "Bob", parentAnchor: "L"),
                },
                new[]
                {
                    BP("dp", BranchPointType.JointBreak, new[] { "L" }, new[] { "probe" }, ut: 30),
                    BP("ev", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }, ut: 120),
                });
            tree.BranchPoints[0].SplitCause = "DECOUPLE";
            var committed = new List<Recording>
            {
                tree.Recordings["L"],     // 0
                tree.Recordings["probe"], // 1
                tree.Recordings["cont"],  // 2
                tree.Recordings["bob"],   // 3
            };

            // Keep only the pod's post-decouple survivor: exclude the launch interval ("L") and the
            // peeled branches (probe, the EVA kerbal "bob"), leaving the continuing pod.
            var mission = LoopMission("t1", LoopTimeUnit.Auto);
            mission.ExcludedIntervalKeys.Add("L");      // launch stack interval
            mission.ExcludedIntervalKeys.Add("probe");  // peeled booster branch
            mission.ExcludedIntervalKeys.Add("bob");    // EVA kerbal branch
            var set = Build(new[] { mission }, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(30.0, unit.SpanStartUT);  // start-trimmed to the decouple, not the launch (0)
            Assert.Equal(200.0, unit.SpanEndUT);
            // L renders only from the decouple; cont keeps its full range.
            Assert.Equal(30.0, unit.MemberStartUT(0, tree.Recordings["L"].StartUT));
            Assert.Equal(120.0, unit.MemberEndUT(0, tree.Recordings["L"].EndUT));
            Assert.Equal(120.0, unit.MemberStartUT(2, tree.Recordings["cont"].StartUT));
            // The peeled branches are gone.
            Assert.False(set.IsMember(1)); // probe
            Assert.False(set.IsMember(3)); // bob
        }

        [Fact]
        public void ComputeTrimmedMemberWindows_HonorsIntervalTrim_SharedByBuilderAndUI()
        {
            // ComputeTrimmedMemberWindows is the single source of truth the loop builder AND the
            // Missions UI (period-cell span display + watch target) consume, so this pins that the
            // shared helper honors the interval-level trim (NOT the legacy through-line-head set the
            // UI used before, which ignored interval trims). Same pod-keeps-recording scenario as
            // Build_ExcludingLaunchInterval_StartTrimsSpanAndMemberWindow.
            var tree = Tree("t1",
                new[]
                {
                    Leg("L", "C", 0, 0, 120),
                    Leg("probe", "C2", 0, 30, 84, parentAnchor: "L", vessel: "Probe"),
                    Leg("cont", "C3", 0, 120, 200),
                    Leg("bob", "C4", 0, 120, 150, eva: "Bob", parentAnchor: "L"),
                },
                new[]
                {
                    BP("dp", BranchPointType.JointBreak, new[] { "L" }, new[] { "probe" }, ut: 30),
                    BP("ev", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }, ut: 120),
                });
            tree.BranchPoints[0].SplitCause = "DECOUPLE";
            var committed = new List<Recording>
            {
                tree.Recordings["L"],     // 0
                tree.Recordings["probe"], // 1
                tree.Recordings["cont"],  // 2
                tree.Recordings["bob"],   // 3
            };
            var structure = MissionStructureBuilder.Build(tree);
            var view = MissionThroughLineBuilder.Build(structure);
            var compRoots = MissionCompositionBuilder.Build(structure);

            // No exclusions -> every committed member present at its full range.
            var full = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, compRoots, committed, new HashSet<string>(), null, out _, out _);
            Assert.Equal(new[] { 0, 1, 2, 3 }, full.Keys.OrderBy(k => k).ToArray());
            Assert.Equal(0.0, full[0].StartUT);
            Assert.Equal(120.0, full[0].EndUT);

            // Trim the launch interval + peeled branches: only the post-decouple pod survives,
            // start-trimmed to the decouple (30) - exactly what the loop builder spans, so the
            // period cell / watch target now see the trimmed set too.
            var excluded = new HashSet<string> { "L", "probe", "bob" };
            var trimmed = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, compRoots, committed, excluded, null, out _, out int skipped);
            Assert.Equal(new[] { 0, 2 }, trimmed.Keys.OrderBy(k => k).ToArray());
            Assert.Equal(30.0, trimmed[0].StartUT);   // L start-trimmed to the decouple, not 0
            Assert.Equal(120.0, trimmed[0].EndUT);
            Assert.Equal(120.0, trimmed[2].StartUT);  // cont keeps its full range
            Assert.Equal(200.0, trimmed[2].EndUT);

            // Span MissionSpanSeconds would show = max end - min start over the trimmed windows.
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var w in trimmed.Values)
            {
                if (w.StartUT < min) min = w.StartUT;
                if (w.EndUT > max) max = w.EndUT;
            }
            Assert.Equal(170.0, max - min); // 200 - 30, matches the builder's trimmed span (30..200)
        }

        [Fact]
        public void Build_IncludedIdNotInCommitted_IsSkipped_NoOutOfRangeIndex()
        {
            var tree = LinearTree(); // legs a, b, c
            // "b" is missing from committed; only a and c are present.
            var committed = new List<Recording>
            {
                tree.Recordings["a"], // 0
                tree.Recordings["c"]  // 1
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            // Only valid indices 0 (a) and 1 (c); never an out-of-range 2.
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
            Assert.All(unit.MemberIndices, idx => Assert.InRange(idx, 0, committed.Count - 1));
            Assert.Equal(100.0, unit.SpanStartUT);
            Assert.Equal(410.0, unit.SpanEndUT);
        }

        [Fact]
        public void Build_AllMembersMissingFromCommitted_ReturnsEmpty()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(); // nothing committed
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.Equal(0, set.Count);
        }

        [Fact]
        public void Build_FirstLoopingMissionWins()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission>
            {
                new Mission("m0", "t1", "Off") { LoopPlayback = false },
                LoopMission("t1", LoopTimeUnit.Auto), // first looping
                LoopMission("t1", LoopTimeUnit.Sec, interval: 9999.0) // ignored
            };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(310.0, unit.CadenceSeconds); // Auto from the FIRST looping mission
        }

        [Fact]
        public void Build_LogsSummary_OnSuccess()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            Build(missions, new[] { tree }, committed);

            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("MissionLoopUnit") &&
                l.Contains("members=3") && l.Contains("Loopy"));
        }

        [Fact]
        public void Build_DuplicateCommittedIds_FirstWins()
        {
            var tree = Tree("t1", new[] { Leg("a", "C", 0, 100, 200) });
            var dup = Leg("a", "C", 0, 100, 200); // same id, second position
            var committed = new List<Recording>
            {
                tree.Recordings["a"], // index 0 (first wins)
                dup                   // index 1
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0 }, unit.MemberIndices); // index 0, not 1
        }

        // --- MissionLoopUnitBuilder.BuildSignature ---

        [Fact]
        public void BuildSignature_AutoMission_ChangesWhenGlobalIntervalChanges()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            string sigA = MissionLoopUnitBuilder.BuildSignature(missions, new[] { tree }, committed, 30.0);
            string sigB = MissionLoopUnitBuilder.BuildSignature(missions, new[] { tree }, committed, 45.0);

            // An Auto mission takes its overlap cadence from the global interval, so a change to it
            // must force a rebuild.
            Assert.NotEqual(sigA, sigB);
        }

        [Fact]
        public void BuildSignature_StableWhenNothingChanges()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            string sigA = MissionLoopUnitBuilder.BuildSignature(missions, new[] { tree }, committed, 30.0);
            string sigB = MissionLoopUnitBuilder.BuildSignature(missions, new[] { tree }, committed, 30.0);

            Assert.Equal(sigA, sigB);
        }

        [Fact]
        public void BuildSignature_ChangesWhenSecondMissionStartsLooping()
        {
            var t1 = LinearTree("t1");
            var t2 = LinearTree("t2");
            var committed = new List<Recording>();
            committed.AddRange(t1.Recordings.Values);
            committed.AddRange(t2.Recordings.Values);
            var trees = new[] { t1, t2 };

            var m1 = new Mission("m1", "t1", "A") { LoopPlayback = true, LoopTimeUnit = LoopTimeUnit.Auto };
            var m2 = new Mission("m2", "t2", "B") { LoopPlayback = false, LoopTimeUnit = LoopTimeUnit.Auto };
            var missions = new List<Mission> { m1, m2 };

            string sigOne = MissionLoopUnitBuilder.BuildSignature(missions, trees, committed, 30.0);
            m2.LoopPlayback = true; // a second mission (different tree) starts looping
            string sigTwo = MissionLoopUnitBuilder.BuildSignature(missions, trees, committed, 30.0);

            Assert.NotEqual(sigOne, sigTwo);
        }

        // --- Concurrent multi-mission looping (one unit per looping mission) ---

        [Fact]
        public void Build_TwoMissionsOnDistinctTrees_ProducesTwoUnits_DisjointMembers()
        {
            // Two independent linear trees, committed back-to-back so their indices are distinct.
            var t1 = LinearTree("t1"); // a/b/c at 100..410
            var t2 = Tree("t2", new[]
            {
                Leg("d", "C2", 0, 1000, 1100),
                Leg("e", "C2", 1, 1100, 1200)
            });
            var committed = new List<Recording>();
            committed.AddRange(new[] { t1.Recordings["a"], t1.Recordings["b"], t1.Recordings["c"] }); // 0,1,2
            committed.AddRange(new[] { t2.Recordings["d"], t2.Recordings["e"] });                     // 3,4
            var trees = new[] { t1, t2 };

            var missions = new List<Mission>
            {
                new Mission("m1", "t1", "Kerbal X")   { LoopPlayback = true, LoopTimeUnit = LoopTimeUnit.Auto },
                new Mission("m2", "t2", "Mun Lander") { LoopPlayback = true, LoopTimeUnit = LoopTimeUnit.Auto }
            };

            var set = Build(missions, trees, committed);

            // Two distinct units, owned by each tree's earliest member.
            Assert.Equal(2, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var u1));
            Assert.True(set.TryGetUnitForMember(3, out var u2));
            Assert.Equal(0, u1.OwnerIndex);
            Assert.Equal(3, u2.OwnerIndex);
            Assert.Equal(new[] { 0, 1, 2 }, u1.MemberIndices);
            Assert.Equal(new[] { 3, 4 }, u2.MemberIndices);

            // Members never cross units: each index resolves to its own tree's owner.
            Assert.Equal(0, set.OwnerByIndex[1]);
            Assert.Equal(0, set.OwnerByIndex[2]);
            Assert.Equal(3, set.OwnerByIndex[4]);

            // Independent spans.
            Assert.Equal(100.0, u1.SpanStartUT);
            Assert.Equal(410.0, u1.SpanEndUT);
            Assert.Equal(1000.0, u2.SpanStartUT);
            Assert.Equal(1200.0, u2.SpanEndUT);
        }

        [Fact]
        public void Build_TwoLoopingMissionsOnSameTree_KeepsFirst_WarnsCollision_MapsConsistent()
        {
            // The store enforces one-loop-per-tree, but Build carries a defensive collision guard
            // in case the invariant is violated upstream. Two looping missions on the SAME tree
            // share the trunk (and therefore the earliest committed index / owner), so the second
            // unit's owner slot collides and it is dropped wholesale with a warn.
            var tree = LinearTree("t1"); // single through-line a/b/c -> indices 0,1,2, owner 0
            var committed = new List<Recording>
            {
                tree.Recordings["a"], tree.Recordings["b"], tree.Recordings["c"]
            };
            var missions = new List<Mission>
            {
                new Mission("m1", "t1", "A") { LoopPlayback = true, LoopTimeUnit = LoopTimeUnit.Auto },
                new Mission("m2", "t1", "B") { LoopPlayback = true, LoopTimeUnit = LoopTimeUnit.Auto }
            };

            var set = Build(missions, new[] { tree }, committed);

            // Exactly one unit survives; every index resolves to that one owner (maps agree).
            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(0, unit.OwnerIndex);
            Assert.Equal(0, set.OwnerByIndex[1]);
            Assert.Equal(0, set.OwnerByIndex[2]);
            // Every member the surviving unit lists routes back to the surviving unit.
            foreach (int idx in unit.MemberIndices)
                Assert.Equal(0, set.OwnerByIndex[idx]);
            // The collision was logged.
            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("already owned by another looping unit"));
        }

        [Fact]
        public void Build_OnlyOneOfTwoMissionsLooping_ProducesOneUnit()
        {
            var t1 = LinearTree("t1");
            var t2 = Tree("t2", new[] { Leg("d", "C2", 0, 1000, 1100) });
            var committed = new List<Recording>();
            committed.AddRange(new[] { t1.Recordings["a"], t1.Recordings["b"], t1.Recordings["c"] });
            committed.Add(t2.Recordings["d"]);
            var trees = new[] { t1, t2 };

            var missions = new List<Mission>
            {
                new Mission("m1", "t1", "A") { LoopPlayback = false, LoopTimeUnit = LoopTimeUnit.Auto },
                new Mission("m2", "t2", "B") { LoopPlayback = true,  LoopTimeUnit = LoopTimeUnit.Auto }
            };

            var set = Build(missions, trees, committed);

            Assert.Equal(1, set.Count);
            Assert.False(set.IsMember(0)); // t1 not looping
            Assert.True(set.IsMember(3));  // t2 member (committed index 3)
        }

        // --- MissionSelection.ComputeIncludedHeadIds ---

        [Fact]
        public void Selection_NullView_ReturnsEmpty()
        {
            Assert.Empty(MissionSelection.ComputeIncludedHeadIds(null, new HashSet<string>()));
        }

        [Fact]
        public void Selection_NullExcluded_IncludesEverything()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var included = MissionSelection.ComputeIncludedHeadIds(view, null);
            Assert.Equal(view.ByHeadId.Keys.OrderBy(x => x),
                included.OrderBy(x => x));
        }

        [Fact]
        public void Selection_NoExclusions_IncludesAllRootsAndOffshoots()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var included = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());

            Assert.Contains("root", included);
            Assert.Contains("fork", included);
            Assert.Equal(view.ByHeadId.Count, included.Count);
        }

        [Fact]
        public void Selection_ExcludingRoot_DropsItAndItsOffshoots()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var excluded = new HashSet<string> { "root" };
            var included = MissionSelection.ComputeIncludedHeadIds(view, excluded);

            // root excluded -> root gone AND its offshoot "fork" cascades out.
            Assert.DoesNotContain("root", included);
            Assert.DoesNotContain("fork", included);
            Assert.Empty(included);
        }

        [Fact]
        public void Selection_ExcludingOffshoot_DropsOnlyThatBranch()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var excluded = new HashSet<string> { "fork" };
            var included = MissionSelection.ComputeIncludedHeadIds(view, excluded);

            Assert.Contains("root", included);     // trunk stays
            Assert.DoesNotContain("fork", included); // only the fork drops
        }

        [Fact]
        public void Selection_DeepOffshootChain_CascadesFromTheExcludedAncestor()
        {
            // root -> a -> b -> c (each a separate craft branching off the previous).
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(DeepChain()));

            // No exclusions: all four heads included.
            var all = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());
            Assert.Equal(new[] { "a", "b", "c", "root" }, all.OrderBy(x => x).ToArray());

            // Exclude "a": a, b, c all cascade out; only root remains.
            var fromA = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string> { "a" });
            Assert.Equal(new[] { "root" }, fromA.ToArray());

            // Exclude "b": b and c cascade out; root and a remain.
            var fromB = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string> { "b" });
            Assert.Equal(new[] { "a", "root" }, fromB.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Selection_MergeReachedTwice_DoesNotInfiniteLoop_OrDoubleCount()
        {
            // Same-tree merge: two trunks p1 and p2 both fork-parent the same child "m".
            // "m" is an offshoot of both; the visited set must stop it being traversed twice.
            var tree = Tree("t1",
                new[]
                {
                    Leg("p1", "C1", 0, 0, 100),
                    Leg("p2", "C2", 0, 0, 100),
                    Leg("m", "C3", 0, 100, 200, parentAnchor: "p1")
                },
                new[]
                {
                    BP("bp1", BranchPointType.Breakup, new[] { "p1", "p2" }, new[] { "m" })
                });
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(tree));

            // Should terminate and include each head exactly once.
            var included = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());
            Assert.Equal(included.Count, included.Distinct().Count());
            Assert.Contains("m", included);
        }

        [Fact]
        public void Selection_Cycle_DoesNotInfiniteLoop()
        {
            // Hand-build a deliberately cyclic view (A offshoots B, B offshoots A).
            var view = new MissionThroughLineView { TreeId = "t1" };
            var tlA = new MissionThroughLine { HeadLegId = "A", StartUT = 0 };
            var tlB = new MissionThroughLine { HeadLegId = "B", StartUT = 1 };
            tlA.OffshootHeadIds.Add("B");
            tlB.OffshootHeadIds.Add("A");
            view.ByHeadId["A"] = tlA;
            view.ByHeadId["B"] = tlB;
            view.RootHeadIds.Add("A");

            // Must terminate (the visited guard breaks the cycle).
            var included = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());
            Assert.Contains("A", included);
            Assert.Contains("B", included);
            Assert.Equal(2, included.Count);
        }

        // A trunk that forks off one offshoot craft "fork".
        private static RecordingTree ForkView()
        {
            return Tree("t1",
                new[]
                {
                    Leg("root", "CR", 0, 0, 200),
                    Leg("fork", "CF", 0, 100, 150, parentAnchor: "root")
                },
                new[]
                {
                    BP("bp1", BranchPointType.Breakup, new[] { "root" }, new[] { "fork" })
                });
        }

        // root -> a -> b -> c, each offshoot anchored to the previous craft.
        private static RecordingTree DeepChain()
        {
            return Tree("t1",
                new[]
                {
                    Leg("root", "CR", 0, 0, 400),
                    Leg("a", "CA", 0, 50, 350, parentAnchor: "root"),
                    Leg("b", "CB", 0, 100, 300, parentAnchor: "a"),
                    Leg("c", "CC", 0, 150, 250, parentAnchor: "b")
                },
                new[]
                {
                    BP("bpa", BranchPointType.Breakup, new[] { "root" }, new[] { "a" }),
                    BP("bpb", BranchPointType.Breakup, new[] { "a" }, new[] { "b" }),
                    BP("bpc", BranchPointType.Breakup, new[] { "b" }, new[] { "c" })
                });
        }

        // --- SelectDeorbitTailLegStartUT (loiter-line regression pin, 2026-06-23) ---
        //
        // The C1 icon-engage bound (LoopUnit.FirstDeorbitLegStartUT) must be the deorbit-arc leg ENDING AT
        // the seam (the renderer's leg 10), NOT the min-startUT leg in the wide (seam+captureShift, seam]
        // window. The regression: rec 44 (mission "Kerbal X #2") had an earlier Duna approach leg starting
        // ~51,521 s (about 12.9 parking periods) before the seam; min-startUT picked it, so C1 engaged ~51k s
        // early and the icon left the parking conic across most of the loiter — killing the parking line
        // while no deorbit leg had drawn yet. Max-endUT (the seam-terminating leg) is the fix.

        private static Parsek.Display.GhostTrajectoryPolylineRenderer.LegPolyline Leg(string body, double start, double end)
            => new Parsek.Display.GhostTrajectoryPolylineRenderer.LegPolyline { bodyName = body, startUT = start, endUT = end };

        [Fact]
        public void SelectDeorbitTailLegStartUT_PicksSeamEndingLeg_NotEarliestApproachLeg()
        {
            // The real rec-44 leg set (from log 2026-06-23_1927): the ascent leg on Kerbin, an earlier Duna
            // approach leg [2570490859, ...] (the regression's bad min-startUT pick), and the deorbit arc
            // [2570541342, 2570542381] ENDING at the seam (= leg 10, the one the renderer actually rides).
            const double seam = 2570542380.9910212;
            var legs = new List<Parsek.Display.GhostTrajectoryPolylineRenderer.LegPolyline>
            {
                Leg("Kerbin", 2547277637.2, 2547277750.6),       // ascent — wrong body, ignored
                Leg("Duna",   2570490859.2804718, 2570500000.0), // earlier approach leg (the bad pick)
                Leg("Duna",   2570541342.0, 2570542381.0),       // the deorbit arc ENDING at the seam (leg 10)
            };
            // Max-endUT <= seam+eps -> the seam-terminating leg's start, NOT the earlier approach leg.
            Assert.Equal(2570541342.0,
                MissionLoopUnitBuilder.SelectDeorbitTailLegStartUT(legs, "Duna", seam, 1.0), 3);
        }

        [Fact]
        public void SelectDeorbitTailLegStartUT_FiltersBodyAndPostSeamLegs()
        {
            const double seam = 1000.0;
            var legs = new List<Parsek.Display.GhostTrajectoryPolylineRenderer.LegPolyline>
            {
                Leg("Duna",   100.0, 1500.0),  // ends AFTER seam+eps -> excluded
                Leg("Kerbin", 200.0, 1000.0),  // ends at seam but WRONG body -> excluded
                Leg("Duna",    50.0,  900.0),  // valid: ends below the seam
                Leg("Duna",   300.0,  999.5),  // valid AND ends closer to the seam -> selected (max endUT)
            };
            Assert.Equal(300.0, MissionLoopUnitBuilder.SelectDeorbitTailLegStartUT(legs, "Duna", seam, 1.0), 3);
        }

        [Fact]
        public void SelectDeorbitTailLegStartUT_NoMatch_ReturnsNaN()
        {
            var none = new List<Parsek.Display.GhostTrajectoryPolylineRenderer.LegPolyline>
            {
                Leg("Kerbin", 0.0, 100.0),   // wrong body
                Leg("Duna",   0.0, 5000.0),  // ends well after seam+eps
            };
            Assert.True(double.IsNaN(MissionLoopUnitBuilder.SelectDeorbitTailLegStartUT(none, "Duna", 1000.0, 1.0)));
            Assert.True(double.IsNaN(MissionLoopUnitBuilder.SelectDeorbitTailLegStartUT(null, "Duna", 1000.0, 1.0)));
        }

    }
}
