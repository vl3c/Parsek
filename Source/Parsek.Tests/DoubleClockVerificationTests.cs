using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    // THE DOUBLE-CLOCK VERIFICATION (design-dock-event-graph.md 7.8, gating open question Q9;
    // analysis crosstree-dock-loop-coherence-analysis-2026-08-12.md section 2(e), invariant I6).
    //
    // WHAT THE ANALYSIS CLAIMED, AND WHY IT NEEDED VERIFYING.
    // Invariant I6 ("one physical assembly is never concurrently rendered at two different replay
    // times by two independent clocks") is listed as "Violated (latent)" and explicitly marked
    // UNVERIFIED: two disjoint-tree missions MAY loop concurrently (the pinned behavior - the
    // one-loop-per-spanned-set enforcement treats them as disjoint until a link is included), and
    // mission 1's loop renders the merged AB stretch (which CONTAINS B's parts, baked into the
    // post-couple merged snapshot) on tree ta's clock while mission 2's loop renders B's own
    // recording on tree tb's clock. Nothing in the code couples the two clocks. The design forbids
    // shipping the R6 advisory on an unverified severity claim, so this file is the verification.
    //
    // WHAT IS VERIFIED HERE, EXACTLY.
    // This pins the STRUCTURAL double render: both units build, the enforcement provably does not
    // couple them, and there exist wall-clock UTs at which DecideUnitMemberRender returns Render
    // for BOTH (unit1, the AB docked-stretch member) and (unit2, the B0 member) while the two
    // units' spanLoopUTs differ by far more than LoopTiming.BoundaryEpsilon. That is the render
    // decision itself - the same physical matter (B's parts) being told to render at two different
    // RECORDED times in the same frame.
    //
    // WHAT IS NOT VERIFIED HERE (the honest epistemic boundary).
    // The VISUAL severity - two ghosts of the same vessel visible together in flight / map view,
    // possibly kilometres apart - is not asserted. It FOLLOWS from the structural result (the two
    // loopUTs index different points of the recorded trajectories, and those trajectories are what
    // the ghost positioner samples), but "follows" is an inference, not a measurement: it depends
    // on both ghosts being spawned, in range, and not suppressed by an unrelated gate. That
    // confirmation is collected opportunistically in ordinary play, exactly like the other M-MIS-8
    // per-scene visuals (design 16.2 gives seam markers no in-game cell in v1 for the same reason).
    // This file deliberately does not simulate a scene to claim more than it can.
    //
    // METHOD: a fine sweep of the shared wall clock (currentUT) across two full cadences of the
    // longer unit, calling the two REAL pure render decisions at every step. DecideUnitMemberRender
    // is pure, so thousands of calls cost nothing and no sampling artefact can hide a collision
    // that a coarse step would step over.
    //
    // [Collection("Sequential")] because MissionStore and ParsekLog are process-wide statics.
    [Collection("Sequential")]
    public class DoubleClockVerificationTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly List<string> logLines = new List<string>();

        // The two loops are enabled at DIFFERENT wall UTs, both well past both spans, so each
        // mission's phase anchor is its own enable UT (the normal flow: a loop is turned on after
        // its flight finished). Deliberately not the same instant: a player enables two missions'
        // loops at two different moments, and equal anchors would make the two clocks agree at
        // every cycle boundary - a coincidence the real case does not have.
        private const double Mission1LoopEnableUT = 1000.0;
        private const double Mission2LoopEnableUT = 1037.0;

        // The sweep. Step is 1 s of wall clock; the range covers two full cadences of the LONGER
        // unit (mission 1's span is 400 s; mission 2's is 100 s), so every phase relationship the
        // two clocks can take is visited twice.
        private const double SweepStepSeconds = 1.0;
        private const double SweepSeconds = 800.0;

        public DoubleClockVerificationTests(ITestOutputHelper output)
        {
            this.output = output;
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = true;
            DockEventGraph.SuppressLogging = true;
            MissionCrossTreeDock.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ------------------------------------------------------------------
        // Fixture: the AB/CD two-tree shape (CrossTreeDockFixture).
        //
        //   tb: B0 [0 .. 100]                       - the partner's own solo flight
        //   ta: A0 [0 .. 150] -> Dock(target=pid B) -> AB [150 .. 300] (B's parts are IN here)
        //                     -> Undock -> { A1 [300 .. 400], B1 [300 .. 380] }
        //
        // Mission 1 = ta's mission (everything included, loop ON).
        // Mission 2 = tb's mission (link OFF, loop ON).
        // ------------------------------------------------------------------

        private sealed class Fixture
        {
            internal RecordingTree Tb;
            internal RecordingTree Ta;
            internal List<RecordingTree> Trees;
            internal List<Recording> Committed;
            internal Dictionary<string, int> IndexById;
            internal Mission M1;
            internal Mission M2;

            internal int Idx(string recordingId) => IndexById[recordingId];
        }

        // Seeds both missions into MissionStore (so ClearLoopsConflictingWith sees them) via the
        // codec round-trip the other store tests use - MissionStore has no direct Add.
        private static Fixture BuildFixture(bool mission2Loops)
        {
            var f = new Fixture
            {
                Tb = CrossTreeDockFixture.PartnerTree(),
                Ta = CrossTreeDockFixture.ControllerTree(),
            };
            f.Trees = new List<RecordingTree> { f.Tb, f.Ta };
            f.Committed = CrossTreeDockFixture.Committed(f.Tb, f.Ta);
            f.IndexById = CrossTreeDockFixture.IndexById(f.Committed);

            var m1 = new Mission("m1", "ta", "AB")
            {
                LoopPlayback = true,
                LoopAnchorUT = Mission1LoopEnableUT,
            };
            var m2 = new Mission("m2", "tb", "CD Freighter")
            {
                LoopPlayback = mission2Loops,
                LoopAnchorUT = Mission2LoopEnableUT,
            };
            var node = new ConfigNode("PARSEK");
            m1.Save(node.AddNode("MISSION"));
            m2.Save(node.AddNode("MISSION"));
            MissionStore.Load(node);
            f.M1 = MissionStore.Missions.First(x => x.Id == "m1");
            f.M2 = MissionStore.Missions.First(x => x.Id == "m2");
            return f;
        }

        private static GhostPlaybackLogic.LoopUnitSet BuildUnits(Fixture f)
            => MissionLoopUnitBuilder.Build(
                MissionStore.Missions.ToList(), f.Trees, f.Committed, autoLoopIntervalSeconds: 30.0);

        // One member's render decision at one wall UT, through the REAL pure seam the flight
        // engine drives per member per frame.
        private static GhostPlaybackLogic.UnitMemberRenderDecision Decide(
            GhostPlaybackLogic.LoopUnit unit, Recording rec, int memberIndex,
            double currentUT, out double spanLoopUT)
            => GhostPlaybackLogic.DecideUnitMemberRender(
                currentUT,
                unit.PhaseAnchorUT,
                unit.SpanStartUT,
                unit.SpanEndUT,
                unit.CadenceSeconds,
                unit.MemberStartUT(memberIndex, rec.ExplicitStartUT),
                unit.MemberEndUT(memberIndex, rec.ExplicitEndUT),
                out spanLoopUT,
                out long _,
                out bool _2);

        // ==================================================================
        // 1. Both units build, and the enforcement provably does not couple them
        // ==================================================================

        [Fact]
        public void TwoDisjointTreeMissions_BothBuildLoopUnits_AndTheEnforcementDoesNotCoupleThem()
        {
            // Regression / pinned behavior: this is the PRECONDITION of the whole double-clock
            // claim. If the one-loop-per-spanned-set rule silently coupled the two trees, no
            // collision could exist and the advisory would be pointless. The rule is keyed on
            // SPANNED TREE SETS (own tree + INCLUDED foreign links), and with the link off the two
            // sets are {ta} and {tb} - disjoint. Pinned behavior #4 of CrossTreeDockLoopUnitInGameTest.
            Fixture f = BuildFixture(mission2Loops: true);

            MissionStore.ClearLoopsConflictingWith(
                f.M1, f.Trees, out int clearedSameTree, out int clearedCrossTree, "verification");

            Assert.Equal(0, clearedSameTree);
            Assert.Equal(0, clearedCrossTree);
            Assert.True(f.M2.LoopPlayback);   // untouched: still looping alongside mission 1

            GhostPlaybackLogic.LoopUnitSet set = BuildUnits(f);

            Assert.Equal(2, set.Count);
            Assert.True(set.TryGetUnitForMember(f.Idx("AB"), out GhostPlaybackLogic.LoopUnit u1));
            Assert.True(set.TryGetUnitForMember(f.Idx("B0"), out GhostPlaybackLogic.LoopUnit u2));
            // Two DISTINCT units: different owners, hence two independent span clocks.
            Assert.NotEqual(u1.OwnerIndex, u2.OwnerIndex);
            // ...whose spans differ, which is what makes their clocks diverge in the first place.
            Assert.NotEqual(u1.SpanEndUT, u2.SpanEndUT);
        }

        // ==================================================================
        // 2. THE COLLISION PROBE
        // ==================================================================

        [Fact]
        public void CollisionProbe_TheSameMatterIsToldToRenderAtTwoDifferentRecordedTimes()
        {
            // THE I6 DEMONSTRATION, at the render-decision level. B's parts exist in two places on
            // disk: inside the AB merged snapshot (recorded 150..300 in tree ta) and in B0, B's own
            // recording (recorded 0..100 in tree tb). Mission 1 loops the first, mission 2 loops the
            // second, on independent clocks. This sweep finds the wall UTs where BOTH are told to
            // render, and measures how far apart the two recorded times are when they are.
            Fixture f = BuildFixture(mission2Loops: true);
            GhostPlaybackLogic.LoopUnitSet set = BuildUnits(f);
            Assert.True(set.TryGetUnitForMember(f.Idx("AB"), out GhostPlaybackLogic.LoopUnit u1));
            Assert.True(set.TryGetUnitForMember(f.Idx("B0"), out GhostPlaybackLogic.LoopUnit u2));
            Recording ab = f.Ta.Recordings["AB"];
            Recording b0 = f.Tb.Recordings["B0"];

            int collisions = 0;
            int divergedCollisions = 0;
            double minDivergence = double.PositiveInfinity;
            double maxDivergence = double.NegativeInfinity;
            double firstCollisionUT = double.NaN;
            double sweepStart = Math.Max(u1.PhaseAnchorUT, u2.PhaseAnchorUT);
            int steps = (int)(SweepSeconds / SweepStepSeconds);

            for (int i = 0; i <= steps; i++)
            {
                double ut = sweepStart + i * SweepStepSeconds;
                var d1 = Decide(u1, ab, f.Idx("AB"), ut, out double loop1);
                var d2 = Decide(u2, b0, f.Idx("B0"), ut, out double loop2);
                if (d1 != GhostPlaybackLogic.UnitMemberRenderDecision.Render
                    || d2 != GhostPlaybackLogic.UnitMemberRenderDecision.Render)
                    continue;

                collisions++;
                if (double.IsNaN(firstCollisionUT))
                    firstCollisionUT = ut;
                double divergence = Math.Abs(loop1 - loop2);
                if (divergence > LoopTiming.BoundaryEpsilon)
                    divergedCollisions++;
                if (divergence < minDivergence) minDivergence = divergence;
                if (divergence > maxDivergence) maxDivergence = divergence;
            }

            var ic = CultureInfo.InvariantCulture;
            string report =
                "double-clock probe: sweep=[" + sweepStart.ToString("F1", ic) + ", "
                + (sweepStart + SweepSeconds).ToString("F1", ic) + "] step="
                + SweepStepSeconds.ToString("F1", ic) + "s samples=" + (steps + 1).ToString(ic)
                + " collisions=" + collisions.ToString(ic)
                + " divergedCollisions=" + divergedCollisions.ToString(ic)
                + " minDivergence=" + minDivergence.ToString("F3", ic) + "s"
                + " maxDivergence=" + maxDivergence.ToString("F3", ic) + "s"
                + " firstCollisionUT=" + firstCollisionUT.ToString("F1", ic);
            output.WriteLine(report);
            output.WriteLine(Describe("u1(ta)", u1, f.Idx("AB"), ab, ic));
            output.WriteLine(Describe("u2(tb)", u2, f.Idx("B0"), b0, ic));

            // (a) The collision EXISTS. If this ever fails, the structural claim is refuted and
            //     the R6 advisory must be DROPPED, not repaired.
            Assert.True(collisions > 0,
                "no wall UT rendered both members concurrently - the structural claim would be "
                + "refuted and the R6 advisory dropped. " + report);

            // (b) The two clocks are DIVERGED wherever they collide - and not by an epsilon:
            //     EVERY colliding sample is diverged, because the two members' recorded windows
            //     ([150, 300] on ta's clock and [0, 100] on tb's) cannot take the same value. One
            //     shared clock would put both at the same recorded time; two clocks cannot.
            Assert.Equal(collisions, divergedCollisions);
            Assert.True(minDivergence > LoopTiming.BoundaryEpsilon, report);

            // (c) The measured separation. Two layers, deliberately:
            //
            //     Geometry (holds for ANY pair of loop-enable UTs): the closest the two recorded
            //     times can ever come is B0's end (100) against AB's start (150) = 50 s; the widest
            //     is B0's start (0) against AB's end (300) = 300 s.
            Assert.InRange(minDivergence, 50.0, 300.0);
            Assert.InRange(maxDivergence, 50.0, 300.0);
            //     This fixture's actual numbers, pinned so a change in the clock arithmetic shows
            //     up as a diff rather than as a still-green test. The observed set is exactly
            //     {137, 237} s: the two cadences are commensurate here (400 = 4 x 100), so the
            //     phase difference is quantized - a fixture property, not a property of the defect.
            //     The 37 s is the gap between the two loop-enable UTs.
            Assert.Equal(137.0, minDivergence, 6);
            Assert.Equal(237.0, maxDivergence, 6);
            // ...and the collision is not a rare grazing sample: 302 of 801 swept wall UTs.
            Assert.Equal(302, collisions);
        }

        // The unit parameters behind a probe result, so the measured numbers in the research note
        // can be re-derived from the test log alone.
        private static string Describe(
            string label, GhostPlaybackLogic.LoopUnit unit, int memberIndex, Recording rec,
            IFormatProvider ic)
            => label
               + " span=[" + unit.SpanStartUT.ToString("F1", ic) + ", "
               + unit.SpanEndUT.ToString("F1", ic) + "]"
               + " cadence=" + unit.CadenceSeconds.ToString("F1", ic)
               + " anchor=" + unit.PhaseAnchorUT.ToString("F1", ic)
               + " memberWindow=[" + unit.MemberStartUT(memberIndex, rec.ExplicitStartUT).ToString("F1", ic)
               + ", " + unit.MemberEndUT(memberIndex, rec.ExplicitEndUT).ToString("F1", ic) + "]";

        // ==================================================================
        // 3. The control: the collision comes from the two clocks, not the fixture
        // ==================================================================

        [Fact]
        public void Control_WithMission2NotLooping_NoUTInTheSweepDoubleRenders()
        {
            // Regression: a probe that "finds" a collision no matter what proves nothing. Turning
            // mission 2's loop OFF removes its unit entirely (a non-looping mission builds none),
            // so nothing renders B0 - and the AB stretch alone is a single-clock render, which is
            // exactly the I1-holding case. Same fixture, same sweep, same members: only the second
            // clock is gone.
            Fixture f = BuildFixture(mission2Loops: false);
            GhostPlaybackLogic.LoopUnitSet set = BuildUnits(f);

            Assert.Equal(1, set.Count);
            Assert.False(set.TryGetUnitForMember(f.Idx("B0"), out GhostPlaybackLogic.LoopUnit _));
            Assert.True(set.TryGetUnitForMember(f.Idx("AB"), out GhostPlaybackLogic.LoopUnit u1));
            Recording ab = f.Ta.Recordings["AB"];

            int abRenders = 0;
            int collisions = 0;
            int steps = (int)(SweepSeconds / SweepStepSeconds);
            for (int i = 0; i <= steps; i++)
            {
                double ut = u1.PhaseAnchorUT + i * SweepStepSeconds;
                bool abRendered = Decide(u1, ab, f.Idx("AB"), ut, out double _)
                    == GhostPlaybackLogic.UnitMemberRenderDecision.Render;
                if (abRendered)
                    abRenders++;
                // B0 has no unit at all, so there is no second render decision to be had: the
                // partner's ghost is simply not driven by any loop clock at any UT in the sweep.
                bool b0Rendered = set.TryGetUnitForMember(
                    f.Idx("B0"), out GhostPlaybackLogic.LoopUnit u2)
                    && Decide(u2, f.Tb.Recordings["B0"], f.Idx("B0"), ut, out double _2)
                        == GhostPlaybackLogic.UnitMemberRenderDecision.Render;
                if (abRendered && b0Rendered)
                    collisions++;
            }

            Assert.Equal(0, collisions);
            // The docked stretch still replays (so the sweep is live, not vacuous) - it simply has
            // no second clock to collide with. Same fixture, same sweep, one clock: no I6 breach.
            Assert.True(abRenders > 0);
        }
    }
}
