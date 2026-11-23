using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-24: a dropped stage's predicted fall plays in mission loops but does not
    /// count toward loop timing. These cells pin the TIMING half on the recorded data: where a
    /// recording's recorded part ends (<see cref="PredictedEndingTakeover.ResolveRecordedEndUT"/>), and the unit the builder makes of
    /// it (span, cadences, cap, first-play floor, constraint windows, log tokens). Every cell with no
    /// predicted tail pins the old numbers.
    /// </summary>
    [Collection("Sequential")]
    public class MissionLoopFallTimingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionLoopFallTimingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        // ---- fixtures ----

        private static TrajectoryPoint Pt(double ut) =>
            new TrajectoryPoint { ut = ut, bodyName = "Kerbin" };

        private static OrbitSegment Seg(double start, double end, bool predicted, string body = "Kerbin") =>
            new OrbitSegment
            {
                startUT = start, endUT = end, isPredicted = predicted, bodyName = body, semiMajorAxis = 700000
            };

        private static Recording Rec(string id, double start, double end, string chain = null,
            int chainIndex = 0, string vessel = "V")
        {
            var rec = new Recording
            {
                RecordingId = id, VesselName = vessel, ChainId = chain ?? id, ChainIndex = chainIndex,
                ExplicitStartUT = start, ExplicitEndUT = end
            };
            rec.Points.Add(Pt(start));
            rec.Points.Add(Pt(end));
            return rec;
        }

        // A fall: predicted Kepler segment from the last sample to the impact, ending Destroyed.
        private static Recording WithFall(Recording rec, double tailEnd)
        {
            double lastSample = rec.Points[rec.Points.Count - 1].ut;
            rec.OrbitSegments.Add(Seg(lastSample, tailEnd, predicted: true));
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.TerminalPredicted = true;
            rec.ExplicitEndUT = tailEnd;
            return rec;
        }

        // GS-12's shape: the stack [31, 140.5] and the dropped core [119, 140.4 + fall].
        private static RecordingTree StageTree(double coreTailEnd, out List<Recording> committed)
        {
            Recording stack = Rec("stack", 31, 140.5, vessel: "Kerbal X");
            Recording core = Rec("core", 119, 140.4, vessel: "Kerbal X Probe");
            core.ParentAnchorRecordingId = "stack";
            if (coreTailEnd > 0)
                WithFall(core, coreTailEnd);
            var tree = new RecordingTree { Id = "t", RootRecordingId = "stack" };
            tree.Recordings["stack"] = stack;
            tree.Recordings["core"] = core;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp", Type = BranchPointType.JointBreak, UT = 119,
                ParentRecordingIds = new List<string> { "stack" },
                ChildRecordingIds = new List<string> { "core" }
            });
            committed = new List<Recording> { stack, core };
            return tree;
        }

        private static Mission Loop(LoopTimeUnit unit, double interval = 0, double anchor = double.NaN,
            params string[] excluded)
        {
            var m = new Mission("m1", "t", "Kerbal X")
            {
                LoopPlayback = true, LoopTimeUnit = unit, LoopIntervalSeconds = interval, LoopAnchorUT = anchor
            };
            foreach (string key in excluded)
                m.ExcludedIntervalKeys.Add(key);
            return m;
        }

        private static GhostPlaybackLogic.LoopUnit BuildOne(
            RecordingTree tree, List<Recording> committed, Mission mission, double auto = 30)
        {
            var set = MissionLoopUnitBuilder.Build(new[] { mission }, new[] { tree }, committed, auto);
            return set.UnitsByOwner.Values.Single();
        }

        // ---- the last recorded UT, by shape ----

        [Fact]
        public void RecordedEndUT_NoTail_IsTheEnd_ExplicitEndPastTheLastSampleIncluded()
        {
            // A packed lander records no samples: the close UT is recorded time.
            Recording lander = Rec("l", 10, 20);
            lander.ExplicitEndUT = 3600;
            lander.TerminalStateValue = TerminalState.Landed;
            Assert.Equal(3600, PredictedEndingTakeover.ResolveRecordedEndUT(lander));
            // A coast closed on rails: the non-predicted segment is recorded time.
            Recording coast = Rec("c", 10, 20);
            coast.OrbitSegments.Add(Seg(20, 900, predicted: false));
            Assert.Equal(900, PredictedEndingTakeover.ResolveRecordedEndUT(coast));
            // No payload at all: the explicit end.
            Assert.Equal(55, PredictedEndingTakeover.ResolveRecordedEndUT(new Recording { ExplicitStartUT = 5, ExplicitEndUT = 55 }));
        }

        [Theory]
        // minutes, days, the 50-year horizon
        [InlineData(332.4)]
        [InlineData(600000.0)]
        [InlineData(460080140.4)]
        public void RecordedEndUT_PredictedFall_IsTheLastSample(double tailEnd)
        {
            Recording rec = WithFall(Rec("f", 119, 140.4), tailEnd);
            Assert.Equal(tailEnd, rec.EndUT);
            Assert.Equal(140.4, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
        }

        [Fact]
        public void RecordedEndUT_EscapeFollowedToItsFinalSunOrbit_SettleSpanIncluded()
        {
            Recording rec = Rec("e", 100, 200);
            rec.OrbitSegments.Add(Seg(200, 300, predicted: false));
            rec.OrbitSegments.Add(Seg(300, 50000, predicted: true));
            rec.OrbitSegments.Add(Seg(50000, 50060, predicted: true, body: "Sun"));
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalPredicted = true;
            rec.ExplicitEndUT = 50060;
            Assert.Equal(300, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
        }

        [Fact]
        public void RecordedEndUT_PredictedCoastEndingSubOrbital_IsTheLastSample()
        {
            Recording rec = Rec("s", 100, 200);
            rec.OrbitSegments.Add(Seg(200, 9000, predicted: true));
            rec.TerminalStateValue = TerminalState.SubOrbital;
            rec.ExplicitEndUT = 9000;
            Assert.Equal(200, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
        }

        [Fact]
        public void RecordedEndUT_PredictedChuteDescent_IsTheLastSampleBeforeTheDescent()
        {
            Recording rec = Rec("d", 100, 200);
            rec.Points.Add(Pt(260));
            rec.Points.Add(Pt(320));
            rec.TrackSections.Add(new TrackSection
            {
                startUT = 200, endUT = 320, source = TrackSectionSource.Predicted,
                referenceFrame = ReferenceFrame.Absolute,
                frames = new List<TrajectoryPoint> { Pt(200), Pt(260), Pt(320) }
            });
            rec.TerminalStateValue = TerminalState.Landed;
            rec.TerminalPredicted = true;
            rec.ExplicitEndUT = 320;
            Assert.Equal(320, rec.EndUT);
            Assert.Equal(200, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
        }

        [Fact]
        public void RecordedEndUT_PredictedCrashUTWithNoSegment_IsTheLastSample()
        {
            Recording rec = Rec("n", 100, 200);
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.TerminalPredicted = true;
            rec.ExplicitEndUT = 400;
            Assert.Equal(200, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
            // A confirmed crash, and a breakup remnant KSP keeps, forecast nothing.
            rec.TerminalPredicted = false;
            Assert.Equal(400, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
            rec.TerminalPredicted = true;
            rec.TerminalRemnantSurvives = true;
            Assert.Equal(400, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
        }

        [Fact]
        public void RecordedEndUT_PredictedSegmentBeforeRealData_IsNoTail()
        {
            Recording rec = Rec("i", 100, 200);
            rec.OrbitSegments.Add(Seg(120, 150, predicted: true));
            Assert.Equal(200, PredictedEndingTakeover.ResolveRecordedEndUT(rec));
        }

        [Fact]
        public void RecordedEndUT_PredictedDataOnly_HoldsNoRecordedTime()
        {
            var rec = new Recording { RecordingId = "p", ExplicitStartUT = 100, ExplicitEndUT = 900 };
            rec.OrbitSegments.Add(Seg(100, 900, predicted: true));
            Assert.True(double.IsNaN(PredictedEndingTakeover.ResolveRecordedEndUT(rec)));
            Assert.True(double.IsNaN(PredictedEndingTakeover.ResolveRecordedEndUT(null)));
        }

        // ---- the builder: timing ----

        [Theory]
        [InlineData(332.4)]          // GS-12's 192 s fall
        [InlineData(600000.0)]       // days
        [InlineData(460080140.4)]    // the 50-year horizon
        public void Build_FallOfAnyLength_ChangesNoTiming(double tailEnd)
        {
            foreach (var mk in new Func<Mission>[]
            {
                () => Loop(LoopTimeUnit.Sec, 150),
                () => Loop(LoopTimeUnit.Sec, 5),
                () => Loop(LoopTimeUnit.Sec, 900000),
                () => Loop(LoopTimeUnit.Auto),
                () => Loop(LoopTimeUnit.Auto, 0, anchor: 145.0),
                () => Loop(LoopTimeUnit.Auto, 0, anchor: 99000.0),
            })
            {
                GhostPlaybackLogic.LoopUnit plain = BuildOne(StageTree(0, out var c0), c0, mk());
                GhostPlaybackLogic.LoopUnit fall = BuildOne(StageTree(tailEnd, out var c1), c1, mk());

                Assert.Equal(plain.SpanStartUT, fall.SpanStartUT);
                Assert.Equal(plain.SpanEndUT, fall.SpanEndUT);
                Assert.Equal(plain.CadenceSeconds, fall.CadenceSeconds);
                Assert.Equal(plain.OverlapCadenceSeconds, fall.OverlapCadenceSeconds);
                Assert.Equal(plain.PhaseAnchorUT, fall.PhaseAnchorUT);
                Assert.Equal(
                    GhostPlaybackLogic.UnitMemberOverlaps(plain), GhostPlaybackLogic.UnitMemberOverlaps(fall));
                // What moved: the drawn end, and the falling member's own window.
                Assert.False(plain.HasLoopFalls);
                Assert.Equal(plain.SpanEndUT, plain.RenderSpanEndUT);
                Assert.True(fall.HasLoopFalls);
                Assert.Equal(tailEnd, fall.RenderSpanEndUT);
                Assert.Equal(tailEnd, fall.MemberEndUT(1, double.NaN));
                Assert.Equal(140.5, fall.MemberEndUT(0, double.NaN));
            }
        }

        [Fact]
        public void Build_Gs12Stages_ReadTheRecordedSpan()
        {
            RecordingTree tree = StageTree(332.4, out var committed);
            double span = 140.5 - 31;

            var one = BuildOne(tree, committed, Loop(LoopTimeUnit.Sec, 150));
            Assert.Equal(150, one.CadenceSeconds);
            Assert.Equal(150, one.OverlapCadenceSeconds);
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(one));
            // the first-play floor is the recorded span end
            Assert.Equal(140.5, one.PhaseAnchorUT);

            var auto = BuildOne(tree, committed, Loop(LoopTimeUnit.Auto));
            Assert.Equal(span, auto.CadenceSeconds);
            Assert.Equal(30, auto.OverlapCadenceSeconds);

            // the cap: span / 20, not the fall / 20 (which would read 15)
            var cap = BuildOne(tree, committed, Loop(LoopTimeUnit.Sec, 5));
            Assert.Equal(span / 20, cap.OverlapCadenceSeconds, 9);
            Assert.True(Math.Ceiling(span / cap.OverlapCadenceSeconds) <= GhostPlayback.MaxOverlapMissionInstances);
        }

        [Fact]
        public void Build_FirstPlayFloor_IsTheRecordedEnd_ALaterAnchorIsKept()
        {
            RecordingTree tree = StageTree(5000, out var committed);
            Assert.Equal(140.5, BuildOne(tree, committed, Loop(LoopTimeUnit.Sec, 150, anchor: 50)).PhaseAnchorUT);
            Assert.Equal(200, BuildOne(tree, committed, Loop(LoopTimeUnit.Sec, 150, anchor: 200)).PhaseAnchorUT);
        }

        [Fact]
        public void Build_RecordedEndPastTheOtherMembers_StillCounts()
        {
            // The dropped stage was watched past the stack's end: its recorded part is timed, its fall is not.
            RecordingTree tree = StageTree(0, out var committed);
            committed[1].Points.Add(Pt(400));
            committed[1].ExplicitEndUT = 400;
            WithFall(committed[1], 900);
            var unit = BuildOne(tree, committed, Loop(LoopTimeUnit.Sec, 150));
            Assert.Equal(400, unit.SpanEndUT);
            Assert.Equal(369, unit.CadenceSeconds);
            Assert.Equal(900, unit.RenderSpanEndUT);
        }

        [Fact]
        public void Build_VesselIntervalExcluded_LeavesNoFall()
        {
            RecordingTree tree = StageTree(332.4, out var committed);
            var unit = BuildOne(tree, committed, Loop(LoopTimeUnit.Sec, 150, double.NaN, "core"));
            Assert.Equal(new[] { 0 }, unit.MemberIndices);
            Assert.False(unit.HasLoopFalls);
            Assert.Equal(140.5, unit.SpanEndUT);
        }

        [Fact]
        public void Build_OnlyTheFallingVesselLooped_TimesItsRecordedPart()
        {
            RecordingTree tree = StageTree(332.4, out var committed);
            var unit = BuildOne(tree, committed, Loop(LoopTimeUnit.Sec, 10, double.NaN, "stack", "stack/seg1"));
            Assert.Equal(new[] { 1 }, unit.MemberIndices);
            Assert.Equal(119, unit.SpanStartUT);
            Assert.Equal(140.4, unit.SpanEndUT);
            Assert.Equal(332.4, unit.RenderSpanEndUT);
        }

        [Fact]
        public void Build_NoRecordedTimeAnywhere_FallsBackToTheDrawnSpan()
        {
            var rec = new Recording { RecordingId = "p", ChainId = "p", ExplicitStartUT = 100, ExplicitEndUT = 900 };
            rec.OrbitSegments.Add(Seg(100, 900, predicted: true));
            var tree = new RecordingTree { Id = "t", RootRecordingId = "p" };
            tree.Recordings["p"] = rec;
            var unit = BuildOne(tree, new List<Recording> { rec }, Loop(LoopTimeUnit.Sec, 50));
            Assert.Equal(900, unit.SpanEndUT);
            Assert.False(unit.HasLoopFalls);
        }

        [Fact]
        public void TimingMemberWindows_AreTheSameInstance_WithNoTail_AndClipOrDropWithOne()
        {
            var plain = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(31, 140.5)
            };
            var committed = new List<Recording> { Rec("a", 31, 140.5) };
            Assert.Same(plain, MissionLoopUnitBuilder.ComputeTimingMemberWindows(plain, committed));

            var onlyPredicted = new Recording { RecordingId = "p", ExplicitStartUT = 150, ExplicitEndUT = 900 };
            onlyPredicted.OrbitSegments.Add(Seg(150, 900, predicted: true));
            committed.Add(WithFall(Rec("b", 119, 140.4), 332.4));
            committed.Add(onlyPredicted);
            plain[1] = new GhostPlaybackLogic.LoopUnit.MemberWindow(119, 332.4);
            plain[2] = new GhostPlaybackLogic.LoopUnit.MemberWindow(150, 900);
            var clipped = MissionLoopUnitBuilder.ComputeTimingMemberWindows(plain, committed);
            Assert.NotSame(plain, clipped);
            Assert.Equal(new[] { 0, 1 }, clipped.Keys.OrderBy(k => k));
            Assert.Equal(140.4, clipped[1].EndUT);
            Assert.Equal(140.5, clipped[0].EndUT);
            // the drawn windows are untouched
            Assert.Equal(332.4, plain[1].EndUT);
        }

        [Fact]
        public void UnitLine_PrintsTheFallTokens_OnlyWithAFall_AndKeepsTheOthers()
        {
            BuildOne(StageTree(332.4, out var c1), c1, Loop(LoopTimeUnit.Sec, 150));
            string fall = logLines.Last(l => l.Contains("MissionLoopUnit: mission="));
            Assert.Contains(
                "members=2 foreignMembers=0 skipped=0 span=[31,140.5] spanDur=109.5 unit=Sec cadence=150 " +
                "overlapCadence=150 overlaps=no owner=0 scheduled=no phaseAnchor=140.5 " +
                "renderSpanEnd=332.39999999999998 fallMembers=1 fallOverlaps=yes",
                fall.Replace("332.4 ", "332.39999999999998 "));

            logLines.Clear();
            BuildOne(StageTree(0, out var c0), c0, Loop(LoopTimeUnit.Sec, 150));
            string plain = logLines.Last(l => l.Contains("MissionLoopUnit: mission="));
            Assert.EndsWith("phaseAnchor=140.5", plain);
        }

        [Theory]
        [InlineData(0, 100, 250, 0, 150, "")]                // no falling member
        [InlineData(0, 100, 250, 1, 150, " renderSpanEnd=250 fallMembers=1 fallOverlaps=yes")]
        [InlineData(0, 100, 140, 2, 150, " renderSpanEnd=140 fallMembers=2 fallOverlaps=no")]
        [InlineData(0, 100, 100, 1, 150, "")]
        public void FormatFallTokens_Cases(
            double start, double end, double renderEnd, int members, double cadence, string expected)
        {
            Assert.Equal(expected,
                MissionLoopUnitBuilder.FormatFallTokens(start, end, renderEnd, members, cadence));
        }

        // ---- the supply-route clock ----

        [Fact]
        public void RouteClock_IsTheSame_WithAndWithoutTheFall()
        {
            // A dock at 130 recorded, N = 1: the delivery instants never move.
            GhostPlaybackLogic.LoopUnit plain = BuildOne(StageTree(0, out var c0), c0, Loop(LoopTimeUnit.Sec, 150));
            GhostPlaybackLogic.LoopUnit fall = BuildOne(
                StageTree(460080140.4, out var c1), c1, Loop(LoopTimeUnit.Sec, 150));
            const double dock = 130;
            Assert.True(Parsek.Logistics.RouteLoopClock.IsDockUTInSpan(fall, dock));
            Assert.False(Parsek.Logistics.RouteLoopClock.IsDockUTInSpan(fall, 200));
            for (double t = 100; t < 2000; t += 13.7)
            {
                bool a = Parsek.Logistics.RouteLoopClock.TryGetRouteLoopState(
                    plain, t, out double loopA, out long cycleA, out bool tailA);
                bool b = Parsek.Logistics.RouteLoopClock.TryGetRouteLoopState(
                    fall, t, out double loopB, out long cycleB, out bool tailB);
                Assert.Equal((a, loopA, cycleA, tailA), (b, loopB, cycleB, tailB));
                Assert.Equal(
                    Parsek.Logistics.RouteLoopClock.TryComputeSecondsToNextDockCrossing(plain, t, dock, -1, out double sa),
                    Parsek.Logistics.RouteLoopClock.TryComputeSecondsToNextDockCrossing(fall, t, dock, -1, out double sb));
                Assert.Equal(sa, sb);
            }
        }

        // ---- the published sets ----

        [Fact]
        public void WithFallCopies_IsCarriedByEveryDerivedView_AndInertOnAnEmptySet()
        {
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty,
                GhostPlaybackLogic.LoopUnitSet.Empty.WithFallCopies());
            RecordingTree tree = StageTree(332.4, out var committed);
            var set = MissionLoopUnitBuilder.Build(
                new[] { Loop(LoopTimeUnit.Sec, 150) }, new[] { tree }, committed, 30);
            Assert.False(set.DrawsFallCopies);
            var flight = set.WithFallCopies();
            Assert.True(flight.DrawsFallCopies);
            Assert.Same(flight, flight.WithFallCopies());
            Assert.True(flight.LiveAt(10).DrawsFallCopies);
            Assert.True(flight.WithStandaloneLoopSource(committed).DrawsFallCopies);
            Assert.Equal(1, flight.LiveAt(200).Count);
        }
    }
}
