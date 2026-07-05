using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 11 / N2 (test-automation coverage follow-up) - the DOCK/UNDOCK MEMBER-WINDOW spine test. It closes
    // the dock/undock case at the DECISION layer with the proven P2-pure pattern (synthetic PhaseChain +
    // LoopUnitSet, sampled via ChainSampler.Sample + GhostRenderDirector.Decide - no ghost / scene).
    //
    // THE MODEL: a docking absorbs one member's identity into another at dockUT (the absorbed craft stops
    // existing as a distinct ghost when it docks), and an undock SPLITS off a child member at splitUT (the new
    // craft begins to exist as a distinct ghost when it undocks). At the render-decision layer these are
    // member-window TRIMS on the shared mission span clock: a LoopUnit whose memberWindows dict trims
    //   - the ABSORBED member to END at dockUT (its window is [spanStart, dockUT]); and
    //   - the SPLIT child member to BEGIN at splitUT (its window is [splitUT, spanEnd]).
    //
    // THE CONTRACT (the live assertions): sampling at the boundary -/+ eps, the spine must
    //   - flip the absorbed member Visible -> Hidden EXACTLY at dockUT (no clamp past its window - a regression
    //     that clamped a docked ghost past dockUT, the "stale ghost after dock" bug, would keep it Visible); and
    //   - flip the split child Hidden -> Visible EXACTLY at splitUT (it does not exist before it undocks).
    //
    // To make the boundary deterministic, the unit runs ONE span instance (CadenceSeconds == span,
    // phaseAnchorUT == spanStart), so the shared span-clock loopUT tracks liveUT linearly inside the span and the
    // member-window trim is the SOLE governing factor (IsLoopUTInMemberWindow is epsilon-tolerant at 1e-6; the
    // -/+ 1.0s probes are far outside that, so the flips are unambiguous).
    //
    // ARCHITECTURAL TRUTH respected + honest caveat: this asserts the spine's member-window DECISION (the
    // Visible/Hidden flip at the dock/undock boundary), NOT the live ProtoVessel lifecycle (no ghost is created
    // or destroyed here) nor any 5b pixel. The member-window trim math is exercised headlessly via the span
    // clock; this drives it through the SAME ChainSampler.Sample entry the production spine inlines.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics) so it runs on a cold launch pad. Fast (void, no
    // ghost / scene / save). FLIGHT only; career-independent; self-contained.
    public class DockUndockMemberWindowSpineInGameTest
    {
        private const string KerbinBodyName = "Kerbin";

        private const int AbsorbedIdx = 0;   // the member that docks (its identity is absorbed at dockUT)
        private const int SplitIdx = 1;      // the child that undocks (begins at splitUT)
        private const double SpanStart = 1000.0;
        private const double SpanEnd = 2000.0;
        private const double DockUT = 1400.0;    // absorbed member trimmed to END here
        private const double SplitUT = 1600.0;   // split child trimmed to BEGIN here
        private const double Eps = 1.0;          // boundary probe (>> BoundaryEpsilon 1e-6)

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 11 N2 dock/undock member-window (spine): the absorbed member flips Visible->"
                + "Hidden EXACTLY at dockUT (no clamp past its window - the stale-ghost-after-dock guard) and "
                + "the split child flips Hidden->Visible EXACTLY at splitUT (it does not exist before undock). "
                + "Asserts the spine member-window DECISION, not the ProtoVessel lifecycle.")]
        public void MemberWindow_DockUndock_FlipsVisibilityExactlyAtBoundaries()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            GhostPlaybackLogic.LoopUnitSet units = BuildDockUndockUnit();
            PhaseChain absorbedChain = BuildMemberChain(AbsorbedIdx, "rec-absorbed");
            PhaseChain splitChain = BuildMemberChain(SplitIdx, "rec-split");

            // --- ABSORBED member: Visible just BEFORE dockUT, Hidden just AFTER (no clamp past its window) ---
            bool absorbedBeforeDock = SampleVisible(absorbedChain, AbsorbedIdx, DockUT - Eps, units, "absorbed");
            bool absorbedAfterDock = SampleVisible(absorbedChain, AbsorbedIdx, DockUT + Eps, units, "absorbed");

            InGameAssert.IsTrue(absorbedBeforeDock,
                "the absorbed member must be VISIBLE just before dockUT (it still exists as a distinct ghost "
                + "until it docks)");
            InGameAssert.IsFalse(absorbedAfterDock,
                "the absorbed member must flip to HIDDEN just after dockUT - never clamp past its trimmed "
                + "window (the stale-ghost-after-dock bug: a docked craft's ghost must stop, not freeze)");

            // --- SPLIT child member: Hidden just BEFORE splitUT, Visible just AFTER (begins at undock) ---
            bool splitBeforeSplit = SampleVisible(splitChain, SplitIdx, SplitUT - Eps, units, "split");
            bool splitAfterSplit = SampleVisible(splitChain, SplitIdx, SplitUT + Eps, units, "split");

            InGameAssert.IsFalse(splitBeforeSplit,
                "the split child must be HIDDEN just before splitUT - it does not exist as a distinct ghost "
                + "before it undocks (no premature appearance)");
            InGameAssert.IsTrue(splitAfterSplit,
                "the split child must flip to VISIBLE just after splitUT (it begins to exist at undock)");

            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "DockUndockMemberWindow: dockUT={0:F1} absorbed[before={1} after={2}] | splitUT={3:F1} "
                + "split[before={4} after={5}]",
                DockUT, absorbedBeforeDock, absorbedAfterDock, SplitUT, splitBeforeSplit, splitAfterSplit));
        }

        // Sample one member's chain at liveUT and return whether the director's intent is visible. The chain's
        // full window is [SpanStart, SpanEnd]; the member-window trim (in the unit) is the governing factor.
        private static bool SampleVisible(
            PhaseChain chain, int memberIdx, double liveUT, GhostPlaybackLogic.LoopUnitSet units, string label)
        {
            GhostSample sample = ChainSampler.Sample(chain, liveUT, units);
            GhostRenderIntent intent = GhostRenderDirector.Decide(sample, GhostRenderIntent.Hidden(), label);
            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "DockUndockMemberWindow.sample: member={0} liveUT={1:F1} cov={2} vis={3} driveUT={4:F3}",
                memberIdx, liveUT, sample.Coverage, intent.Visible, sample.DriveUT));
            return intent.Visible;
        }

        // One LoopUnit running ONE span instance (cadence == span, anchor == spanStart -> loopUT tracks liveUT
        // linearly in [SpanStart, SpanEnd]) with a memberWindows dict trimming the absorbed member to end at
        // dockUT and the split child to begin at splitUT. The MemberWindow ctor is at SpanClock.cs:61.
        private static GhostPlaybackLogic.LoopUnitSet BuildDockUndockUnit()
        {
            var memberWindows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                { AbsorbedIdx, new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, DockUT) },
                { SplitIdx, new GhostPlaybackLogic.LoopUnit.MemberWindow(SplitUT, SpanEnd) },
            };
            // The 7-arg + memberWindows LoopUnit ctor (SpanClock.cs:61). overlapCadence == cadence keeps it a
            // single non-overlap instance (the boundary flip is the subject, not overlap).
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: AbsorbedIdx,
                memberIndices: new[] { AbsorbedIdx, SplitIdx },
                spanStartUT: SpanStart,
                spanEndUT: SpanEnd,
                cadenceSeconds: SpanEnd - SpanStart,        // == span -> one instance, loopUT tracks liveUT
                phaseAnchorUT: SpanStart,
                overlapCadenceSeconds: SpanEnd - SpanStart, // == cadence -> no overlap
                memberWindows: memberWindows);
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { AbsorbedIdx, unit } };
            var ownerByIndex = new Dictionary<int, int>
            {
                { AbsorbedIdx, AbsorbedIdx }, { SplitIdx, AbsorbedIdx },
            };
            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        // A one-conic chain covering the WHOLE span [SpanStart, SpanEnd] for a member, so the ONLY thing that
        // governs Visible/Hidden at the dock/undock boundary is the member-window trim (the chain itself never
        // gates these UTs out).
        private static PhaseChain BuildMemberChain(int memberIdx, string recId)
        {
            var anchor = new AnchorFrame.BodyAnchor(KerbinBodyName);
            var phase = new DepartureLoiterPhase(
                new PhaseId(recId, 0, 0), SegmentProvenance.Recorded, anchor, SpanStart, SpanEnd,
                new OrbitSegment
                {
                    startUT = SpanStart, endUT = SpanEnd, bodyName = KerbinBodyName,
                    semiMajorAxis = 850000.0, eccentricity = 0.0, epoch = SpanStart,
                });
            return new PhaseChain(
                recId, committedIndex: memberIdx, instanceKey: 0,
                phases: new List<TrajectoryPhase> { phase }, windowStartUt: SpanStart, windowEndUt: SpanEnd);
        }
    }
}
