using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    // Automated merge gate for the FLIGHT descent-handoff hide (branch
    // claude/flight-descent-trigger-followup): while a re-aim descent-trigger unit's shared descent
    // phase is Descent, a CURRENTLY-RENDERING non-descent member (the transfer/loiter member circling
    // the shifted parking conic) must HIDE its flight 3D ghost and hand off to the descent member -
    // ONE vessel at the handoff, no double ghost (the engine twin of the TS double-icon bug,
    // 2026-06-20 13:34 log). This replaces the manual playtest gate ("fly the TS repro save in
    // FLIGHT, watch the looped re-aim arrival, assert ONE ghost at the handoff").
    //
    // SHAPE: a read-only PURE-DECISION probe on the REAL mission in the loaded save (s15 "Duna One",
    // the Kerbin->Duna re-aim landing mission). RealSaveMissionFinder builds the live LoopUnit through
    // the production MissionLoopUnitBuilder (transient loop-enabled clone, store untouched), then the
    // test probes the SAME pure functions the engine per-frame path composes, in the SAME order:
    //   1. GhostPlaybackLogic.DecideUnitMemberRender        (the base span-clock render decision)
    //   2. GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff (the ONE shared hide
    //      decision consumed by BOTH GhostPlaybackEngine.UpdateUnitMemberPlayback and the map/TS
    //      resolver ResolveTrackingStationSampleUT)
    //   3. GhostPlaybackLogic.ResolveDescentMemberEngineRender (the descent member's own render)
    // at three probe UTs derived from the production descent timing
    // (DescentTrigger.ComputeDescentTiming): pre-descent, mid-descent, and post-descent.
    //
    // WHY NOT the live engine / resolver call sites: the "engine transfer/loiter member=... -> HIDDEN"
    // log line fires only inside the engine's per-frame UpdateUnitMemberPlayback (which needs live
    // ghost state at the descent UT - hours of warp away), and driving the TS resolver at synthetic
    // UTs would corrupt the DescentRenderTrace per-unit state machine the live polyline Driver feeds
    // (the engine explicitly documents that two feeders interleaving currentUTs produce spurious
    // Reverted/Skipped events). The pure functions ARE the decision seam both call sites consume, so
    // the probe validates the shipped contract without mutating anything.
    //
    // READ-ONLY / BATCH-SAFE: no store mutation (the finder clones), no log-sink redirection, no
    // engine state touched, so nothing needs restoring and AllowBatchExecution stays default-true.
    // Raw RecordingStore.CommittedRecordings reads are allowlisted for Source/Parsek/InGameTests/
    // (scripts/ers-els-audit-allowlist.txt) and mirror the engine's traj.StartUT/EndUT bounds.
    public class DescentHandoffVisibilityInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "Missions", Scene = GameScenes.FLIGHT,
            Description = "MERGE GATE (flight descent handoff): on the real re-aim landing mission in the loaded save (s15 'Duna One'), while the shared descent phase is Descent the transfer/loiter member's render decision flips to HIDDEN via the shared ShouldHideNonDescentMemberForDescentHandoff and exactly ONE unit member (a descent member) renders - no double ghost at the handoff; pre-descent the transfer member renders and the descent member is hidden; post-descent (Done) the hide releases. Skips cleanly when the save has no descent-trigger re-aim mission.")]
        public void DescentHandoff_OneGhostAtHandoff_TransferHiddenWhileDescentPlays()
        {
            if (!IsMissionStoreReady())
                InGameAssert.Skip("Missions not loaded / FlightGlobals bodies not ready; load a save with recorded missions (e.g. s15 'Duna One')");

            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;
            TransitedBodyRotationMode tbrMode =
                ParsekSettings.Current?.TransitedBodyRotationMode ?? TransitedBodyRotationMode.Loose;

            if (!RealSaveMissionFinder.TryFindReaimMission(
                    FlightGlobalsBodyInfo.Instance, autoLoopIntervalSeconds, tbrMode,
                    out RealSaveMissionFinder.ReaimMissionMatch match))
            {
                InGameAssert.Skip(
                    "no re-aim mission in the loaded save; load s15 (the Kerbin->Duna 'Duna One' re-aim landing mission)");
            }

            GhostPlaybackLogic.LoopUnit unit = match.Unit;
            if (!unit.HasDescentTrigger)
            {
                InGameAssert.Skip(
                    $"re-aim mission '{match.Mission.Name}' carries no descent trigger (not a looped landing arrival); "
                    + "load s15 (the Kerbin->Duna 'Duna One' re-aim landing mission)");
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;

            // Unit coherence preconditions: with the trigger engaged there must be a real transfer
            // member, and the unit must run the engine's span-clock branch (an overlapping unit would
            // return before the handoff-hide block, so the hide contract would not apply in flight).
            int transferIdx = unit.TransferMemberIndex;
            InGameAssert.IsTrue(transferIdx >= 0 && transferIdx < committed.Count,
                $"descent-trigger unit must carry a valid TransferMemberIndex (got {transferIdx.ToString(IC)}, committed={committed.Count.ToString(IC)})");
            InGameAssert.IsFalse(GhostPlaybackLogic.UnitMemberOverlaps(unit),
                "descent-trigger unit must not self-overlap (the engine's overlap branch bypasses the descent-handoff hide)");

            MemberWindow(unit, committed, transferIdx, out double tWinStart, out double tWinEnd);

            // Pick the descent member + a mid-descent head target: the first descent-set member whose
            // trimmed window overlaps the recorded clip [RecordedDeorbitUT, DescentEndUT]. The probe
            // head is the overlap midpoint, so the shared head falls strictly inside that member's
            // slice (the engine renders exactly that member there).
            int probeDescentIdx = -1;
            double headTarget = double.NaN;
            int descentScanned = 0, descentBadIndex = 0, descentNoOverlap = 0;
            for (int k = 0; k < unit.DescentMemberIndices.Length; k++)
            {
                int m = unit.DescentMemberIndices[k];
                if (m < 0 || m >= committed.Count) { descentBadIndex++; continue; }
                descentScanned++;
                MemberWindow(unit, committed, m, out double ws, out double we);
                double lo = Math.Max(ws, unit.RecordedDeorbitUT);
                double hi = Math.Min(we, unit.DescentEndUT);
                if (hi - lo <= 2.0) { descentNoOverlap++; continue; }
                probeDescentIdx = m;
                headTarget = 0.5 * (lo + hi);
                break;
            }
            ParsekLog.Verbose("DescentHandoffTest",
                $"descent-set scan: members={unit.DescentMemberIndices.Length.ToString(IC)} scanned={descentScanned.ToString(IC)} "
                + $"badIndex={descentBadIndex.ToString(IC)} noOverlap={descentNoOverlap.ToString(IC)} "
                + $"probeIdx={probeDescentIdx.ToString(IC)}");
            InGameAssert.IsTrue(probeDescentIdx >= 0,
                "no descent-set member window overlaps the recorded descent clip "
                + $"[{unit.RecordedDeorbitUT.ToString("F0", IC)},{unit.DescentEndUT.ToString("F0", IC)}] - unit/window incoherence");
            MemberWindow(unit, committed, probeDescentIdx, out double dWinStart, out double dWinEnd);
            double probeOffset = headTarget - unit.RecordedDeorbitUT; // > 0 by construction

            // Descent timing for the probed cycle, via the SAME production timing the trigger uses.
            // The cycle must be the one the production span clock resolves at the in-descent probe UT
            // (the engine passes DecideUnitMemberRender's unitCycle into the hide decision), so start
            // at cycle 0 and re-resolve until the span clock agrees (converges immediately in practice;
            // the loop only matters when the descent window straddles a cadence boundary).
            long cycle = 0;
            double conicEnd = double.NaN, entryUT = double.NaN, triggerUT = double.NaN;
            bool converged = false;
            for (int iter = 0; iter < 4 && !converged; iter++)
            {
                DescentTrigger.ComputeDescentTiming(
                    cycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                    unit.RecordedDeorbitUT, unit.DestinationBodyRotationPeriodSeconds,
                    unit.CaptureShiftSeconds, unit.LoiterCuts,
                    out conicEnd, out entryUT, out triggerUT);
                InGameAssert.IsTrue(!double.IsNaN(entryUT) && !double.IsNaN(triggerUT),
                    $"descent timing must resolve for cycle {cycle.ToString(IC)} (entryUT={entryUT.ToString("R", IC)} triggerUT={triggerUT.ToString("R", IC)})");
                DecideMember(unit, triggerUT + probeOffset, tWinStart, tWinEnd, out _, out long resolved);
                if (resolved == cycle) converged = true;
                else cycle = resolved;
            }
            InGameAssert.IsTrue(converged,
                $"span-clock cycle did not converge with the descent timing (last cycle={cycle.ToString(IC)} triggerUT={triggerUT.ToString("R", IC)})");

            double preUT = entryUT - 3600.0;                                              // pre-descent (Inert)
            double inUT = triggerUT + probeOffset;                                        // mid-descent (Descent)
            double doneUT = triggerUT + (unit.DescentEndUT - unit.RecordedDeorbitUT) + 60.0; // post-descent (Done)

            // ---- (A) PRE-DESCENT: transfer member renders (it carries the icon/ghost through the
            // wait), the hide is OFF, and the descent member is hidden (it never rides the raw loop
            // clock - the pre-existing descent-trigger contract).
            var preDecision = DecideMember(unit, preUT, tWinStart, tWinEnd, out double preLoopUT, out long preCycle);
            InGameAssert.AreEqual(GhostPlaybackLogic.UnitMemberRenderDecision.Render, preDecision,
                $"pre-descent the transfer member must render (preUT={preUT.ToString("F0", IC)} loopUT={preLoopUT.ToString("F0", IC)} cycle={preCycle.ToString(IC)})");
            bool hidePre = GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(
                unit, transferIdx, preUT, preCycle, out DescentTrigger.DescentHeadPhase phasePre);
            InGameAssert.IsFalse(hidePre,
                $"pre-descent the handoff hide must be OFF (phase={phasePre})");
            InGameAssert.AreEqual(DescentTrigger.DescentHeadPhase.Inert, phasePre,
                $"pre-descent (entryUT-3600) the unit descent phase must be Inert (got {phasePre})");
            var dPre = GhostPlaybackLogic.ResolveDescentMemberEngineRender(
                unit, probeDescentIdx, preUT, preCycle, dWinStart, dWinEnd);
            InGameAssert.IsFalse(dPre.Render,
                $"pre-descent the descent member must be hidden (phase={dPre.Phase})");

            // ---- (B) IN-DESCENT: the transfer member's base decision is still Render (it WOULD
            // otherwise render - the double-ghost precondition the 2026-06-20 13:34 log captured), the
            // shared hide flips it to HIDDEN, and the descent member renders the re-anchored head.
            var inDecision = DecideMember(unit, inUT, tWinStart, tWinEnd, out double inLoopUT, out long inCycle);
            InGameAssert.AreEqual(GhostPlaybackLogic.UnitMemberRenderDecision.Render, inDecision,
                "in-descent the transfer member's BASE decision must be Render (the hide only engages on a "
                + $"currently-rendering member; inUT={inUT.ToString("F0", IC)} loopUT={inLoopUT.ToString("F0", IC)} "
                + $"cycle={inCycle.ToString(IC)} win=[{tWinStart.ToString("F0", IC)},{tWinEnd.ToString("F0", IC)}])");
            bool hideIn = GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(
                unit, transferIdx, inUT, inCycle, out DescentTrigger.DescentHeadPhase phaseIn);
            InGameAssert.AreEqual(DescentTrigger.DescentHeadPhase.Descent, phaseIn,
                $"in-descent probe must land in the Descent phase (got {phaseIn}; inUT={inUT.ToString("F0", IC)} triggerUT={triggerUT.ToString("F0", IC)})");
            InGameAssert.IsTrue(hideIn,
                "in-descent the transfer/loiter member must be HIDDEN by the shared descent-handoff decision "
                + "(ShouldHideNonDescentMemberForDescentHandoff) - the flight 3D ghost hands off to the descent member");
            bool hideDescentMember = GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(
                unit, probeDescentIdx, inUT, inCycle, out _);
            InGameAssert.IsFalse(hideDescentMember,
                "the handoff hide must never apply to a descent member (it is governed by its own re-anchored head)");
            var dIn = GhostPlaybackLogic.ResolveDescentMemberEngineRender(
                unit, probeDescentIdx, inUT, inCycle, dWinStart, dWinEnd);
            InGameAssert.IsTrue(dIn.Render,
                $"in-descent the descent member must render (phase={dIn.Phase} head={dIn.Head.ToString("F0", IC)} "
                + $"win=[{dWinStart.ToString("F0", IC)},{dWinEnd.ToString("F0", IC)}])");
            InGameAssert.IsTrue(
                dIn.Head >= unit.RecordedDeorbitUT && dIn.Head <= unit.DescentEndUT,
                $"the re-anchored descent head must fall inside the recorded clip (head={dIn.Head.ToString("F0", IC)} "
                + $"clip=[{unit.RecordedDeorbitUT.ToString("F0", IC)},{unit.DescentEndUT.ToString("F0", IC)}])");

            // ---- ONE GHOST AT THE HANDOFF: across ALL unit members, exactly one renders at the
            // in-descent probe UT, and it is a descent-set member (the playtest's core check).
            int visibleCount = 0;
            int visibleIdx = -1;
            bool visibleIsDescent = false;
            int scanned = 0, badIndex = 0;
            var detail = new StringBuilder();
            for (int k = 0; k < unit.MemberIndices.Length; k++)
            {
                int m = unit.MemberIndices[k];
                if (m < 0 || m >= committed.Count) { badIndex++; continue; }
                scanned++;
                MemberWindow(unit, committed, m, out double ws, out double we);
                bool vis;
                string why;
                var d = DecideMember(unit, inUT, ws, we, out _, out long cyc);
                if (unit.IsDescentMember(m))
                {
                    if (d == GhostPlaybackLogic.UnitMemberRenderDecision.SpanClockUnresolved)
                    {
                        vis = false;
                        why = "span-unresolved";
                    }
                    else
                    {
                        var r = GhostPlaybackLogic.ResolveDescentMemberEngineRender(unit, m, inUT, cyc, ws, we);
                        vis = r.Render;
                        why = "descent:" + r.Phase;
                    }
                }
                else
                {
                    bool hide = d == GhostPlaybackLogic.UnitMemberRenderDecision.Render
                        && GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(unit, m, inUT, cyc, out _);
                    vis = d == GhostPlaybackLogic.UnitMemberRenderDecision.Render && !hide;
                    why = d.ToString() + (hide ? "+handoff-hide" : string.Empty);
                }
                if (vis)
                {
                    visibleCount++;
                    visibleIdx = m;
                    visibleIsDescent = unit.IsDescentMember(m);
                }
                detail.Append(m.ToString(IC)).Append(vis ? "=VISIBLE(" : "=hidden(").Append(why).Append(") ");
            }
            ParsekLog.Verbose("DescentHandoffTest",
                $"handoff sweep at inUT={inUT.ToString("F0", IC)}: members={unit.MemberIndices.Length.ToString(IC)} "
                + $"scanned={scanned.ToString(IC)} badIndex={badIndex.ToString(IC)} visible={visibleCount.ToString(IC)} | {detail}");
            InGameAssert.IsTrue(visibleCount == 1 && visibleIsDescent,
                $"exactly ONE unit member (a descent member) must render at the handoff - got {visibleCount.ToString(IC)} "
                + $"visible (last visible idx={visibleIdx.ToString(IC)} isDescent={visibleIsDescent}): {detail}");

            // ---- (C) POST-DESCENT (Done): the clip is over - the hide releases (the transfer member
            // returns to its own span-clock contract) and the descent member hides until the next loop.
            DecideMember(unit, doneUT, tWinStart, tWinEnd, out _, out long doneCycle);
            bool hideDone = GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(
                unit, transferIdx, doneUT, doneCycle, out DescentTrigger.DescentHeadPhase phaseDone);
            InGameAssert.AreEqual(DescentTrigger.DescentHeadPhase.Done, phaseDone,
                $"post-descent probe must land in the Done phase (got {phaseDone}; doneUT={doneUT.ToString("F0", IC)})");
            InGameAssert.IsFalse(hideDone,
                "post-descent (Done) the handoff hide must release (the hide is Descent-only by design)");
            var dDone = GhostPlaybackLogic.ResolveDescentMemberEngineRender(
                unit, probeDescentIdx, doneUT, doneCycle, dWinStart, dWinEnd);
            InGameAssert.IsFalse(dDone.Render,
                $"post-descent the descent member must hide until the next loop (phase={dDone.Phase})");

            ParsekLog.Info("DescentHandoffTest",
                $"DescentHandoff_OneGhostAtHandoff: PASS mission='{match.Mission.Name}' tree={match.Tree.Id} "
                + $"transferIdx={transferIdx.ToString(IC)} descentIdx={probeDescentIdx.ToString(IC)} cycle={inCycle.ToString(IC)} "
                + $"entryUT={entryUT.ToString("F0", IC)} triggerUT={triggerUT.ToString("F0", IC)} "
                + $"probes(pre/in/done)={preUT.ToString("F0", IC)}/{inUT.ToString("F0", IC)}/{doneUT.ToString("F0", IC)} "
                + $"pre: transfer=Render hide=false descent={dPre.Phase} | in: transfer=Render->HIDDEN(handoff) "
                + $"descent=Render head={dIn.Head.ToString("F0", IC)} visible={visibleCount.ToString(IC)} | "
                + $"done: hide released, descent={dDone.Phase}");
        }

        // The engine trims the raw recording bounds through the unit's interval trims
        // (GhostPlaybackEngine.UpdateUnitMemberPlayback uses traj.StartUT/EndUT; the TS resolver uses
        // rec.StartUT/EndUT) - mirror that exactly.
        private static void MemberWindow(
            GhostPlaybackLogic.LoopUnit unit, IReadOnlyList<Recording> committed, int i,
            out double startUT, out double endUT)
        {
            startUT = unit.MemberStartUT(i, committed[i].StartUT);
            endUT = unit.MemberEndUT(i, committed[i].EndUT);
        }

        // The production base render decision with the unit's full span-clock inputs (the same call
        // shape as GhostPlaybackEngine.UpdateUnitMemberPlayback / ResolveTrackingStationSampleUT).
        private static GhostPlaybackLogic.UnitMemberRenderDecision DecideMember(
            GhostPlaybackLogic.LoopUnit unit, double liveUT, double memberStartUT, double memberEndUT,
            out double loopUT, out long unitCycle)
        {
            return GhostPlaybackLogic.DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out loopUT, out unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts,
                unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged, unit.RecordedSoiExitUT);
        }

        // Same readiness gate as RealSaveMissionInGameTests: the finder needs the mission store
        // populated and the live body graph up; on an unrelated fresh scene skip rather than false-fail.
        private static bool IsMissionStoreReady()
        {
            if (FlightGlobals.fetch == null || FlightGlobals.Bodies == null || FlightGlobals.Bodies.Count == 0)
                return false;
            var missions = MissionStore.Missions;
            var committed = RecordingStore.CommittedRecordings;
            var trees = RecordingStore.CommittedTrees;
            return missions != null && missions.Count > 0
                && committed != null && committed.Count > 0
                && trees != null && trees.Count > 0;
        }
    }
}
