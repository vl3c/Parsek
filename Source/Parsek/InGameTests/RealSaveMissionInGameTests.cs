using System.Globalization;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    // Real-save mission validation (M-MIS): validate the SHIPPED re-aim / station-rendezvous
    // behavior against the REAL recorded missions already present in the currently-loaded KSP
    // save, so the maintainer confirms the contract via Ctrl+Shift+T instead of loading a save
    // and watching a ghost fly. Unlike the synthetic ReaimEndToEndInGameTest / MissionPeriodicity
    // xUnit suites (which build hand-authored fixtures), these run the maintainer's ACTUAL mission
    // through the live builder + live body graph (FlightGlobalsBodyInfo.Instance) - so they catch a
    // real-save-only deviation the synthetic fixtures cannot, and SKIP cleanly on a save that has no
    // such mission (a graceful no-op on unrelated saves).
    //
    // These tests only READ store state and build read-models with SuppressLogging on; they mutate
    // NOTHING in the store (the finder builds transient loop-enabled clones, never flipping a
    // committed Mission's flags), so they are safe in the ordinary shared batch (AllowBatchExecution
    // defaults true - no isolation / baseline-restore needed).
    //
    // Shape classification lives in the reusable RealSaveMissionFinder so future feature branches
    // (M5 dock-boundary #1239, later re-aim milestones) can add their own real-save assertions on
    // the same seam.
    public class RealSaveMissionInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "Missions", Scene = GameScenes.FLIGHT,
            Description = "A real re-aim mission in the loaded save (e.g. s15 'Duna One') engages re-aim through the live builder, its synodic window schedule is valid with multiple windows, and it either produces a destination arrival hold or re-aims the transfer per window (validates M-MIS-2 / M-MIS-3). Skips cleanly when the save has no re-aim mission.")]
        public void RealSave_ReaimMission_EngagesAndAlignsArrival()
        {
            if (!IsMissionStoreReady())
                InGameAssert.Skip("Missions not loaded / FlightGlobals bodies not ready; load a save with recorded missions");

            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;
            TransitedBodyRotationMode tbrMode =
                ParsekSettings.Current?.TransitedBodyRotationMode ?? TransitedBodyRotationMode.Loose;

            if (!RealSaveMissionFinder.TryFindReaimMission(
                    FlightGlobalsBodyInfo.Instance, autoLoopIntervalSeconds, tbrMode,
                    out RealSaveMissionFinder.ReaimMissionMatch match))
            {
                InGameAssert.Skip(
                    "no re-aim mission in the loaded save; load e.g. s15 (the Kerbin->Duna 'Duna One' re-aim landing mission)");
            }

            GhostPlaybackLogic.LoopUnit unit = match.Unit;

            // (1) Re-aim ENGAGED through the LIVE builder (the finder guarantees it; assert explicitly
            //     so a builder regression that stops engaging on the real mission fails here).
            InGameAssert.IsTrue(unit.IsReaim,
                $"real mission '{match.Mission.Name}' must engage re-aim through the live builder");
            InGameAssert.IsTrue(unit.ReaimPlan.HasValue && unit.ReaimPlan.Value.Supported,
                "the re-aim plan must be Supported");
            InGameAssert.IsTrue(unit.ReaimSchedule.HasValue && unit.ReaimSchedule.Value.Valid,
                "the re-aim window schedule must be valid: "
                + (unit.ReaimSchedule.HasValue ? (unit.ReaimSchedule.Value.Reason ?? "") : "<no schedule>"));

            ReaimMissionPlan plan = unit.ReaimPlan.Value;
            ReaimWindowPlanner.ReaimWindowSchedule sched = unit.ReaimSchedule.Value;

            // (2) The window schedule is a real synodic-cadence relaunch: a finite positive synodic
            //     period, and a cadence that never truncates the recorded span (so a single mission
            //     instance always plays in full and the loop relaunches once per synodic window ->
            //     MULTIPLE windows over game time).
            double span = unit.SpanEndUT - unit.SpanStartUT;
            InGameAssert.IsTrue(
                sched.SynodicPeriodSeconds > 0.0 && !double.IsNaN(sched.SynodicPeriodSeconds)
                && !double.IsInfinity(sched.SynodicPeriodSeconds),
                $"synodic period must be finite positive (got {sched.SynodicPeriodSeconds.ToString("R", IC)})");
            InGameAssert.IsTrue(sched.CadenceSeconds >= span - 1.0,
                $"the re-aim cadence ({sched.CadenceSeconds.ToString("F0", IC)}s) must be at least the "
                + $"recorded span ({span.ToString("F0", IC)}s) so a single window plays in full");

            // Successive windows are distinct, ascending, and spaced by the cadence (a whole synodic
            // multiple): three consecutive departure UTs prove the transfer REGENERATES per window
            // rather than replaying one frozen geometry - the core congruent-window claim.
            double d0 = sched.DepartureUTForWindow(0);
            double d1 = sched.DepartureUTForWindow(1);
            double d2 = sched.DepartureUTForWindow(2);
            InGameAssert.IsTrue(d1 > d0 && d2 > d1,
                $"consecutive re-aim windows must advance (d0={d0.ToString("F0", IC)} d1={d1.ToString("F0", IC)} d2={d2.ToString("F0", IC)})");
            InGameAssert.IsTrue(
                System.Math.Abs((d1 - d0) - sched.CadenceSeconds) < 1.0
                && System.Math.Abs((d2 - d1) - sched.CadenceSeconds) < 1.0,
                "consecutive re-aim windows must be spaced by the synodic-multiple cadence "
                + $"(cadence={sched.CadenceSeconds.ToString("F0", IC)}s)");

            // (3) Destination alignment: for a landing / station destination the builder produces an
            //     arrival hold (ArrivalHoldPlanner path); for a D8 dual-constraint destination it
            //     fails closed with an amber reason; a transfer-only re-aim (no destination phase
            //     constraint) legitimately produces neither and is validated by the per-window
            //     regeneration above. Assert the hold is coherent when present; never require it
            //     unconditionally (that would false-fail an orbit-only re-aim).
            bool hasHold = unit.ArrivalHoldSeconds > 0.0 && !double.IsInfinity(unit.ArrivalHoldSeconds);
            if (hasHold)
            {
                InGameAssert.IsTrue(!double.IsNaN(unit.ArrivalHoldAtUT),
                    "an arrival hold must carry a finite hold-at UT");
                InGameAssert.IsTrue(
                    unit.ArrivalAlignPeriodSeconds > 0.0 && !double.IsNaN(unit.ArrivalAlignPeriodSeconds)
                    && !double.IsInfinity(unit.ArrivalAlignPeriodSeconds),
                    $"an arrival hold must carry a finite positive align period (got {unit.ArrivalAlignPeriodSeconds.ToString("R", IC)})");
                InGameAssert.IsTrue(unit.ArrivalHoldSeconds < unit.ArrivalAlignPeriodSeconds + 1.0,
                    "the arrival hold must fall within one alignment period "
                    + $"(hold={unit.ArrivalHoldSeconds.ToString("F1", IC)}s align={unit.ArrivalAlignPeriodSeconds.ToString("F1", IC)}s)");
            }

            ParsekLog.Info("RealSaveMissionTest",
                $"RealSave_ReaimMission: PASS mission='{match.Mission.Name}' tree={match.Tree.Id} "
                + $"{plan.LaunchBody}->{plan.TargetBody} via {plan.CommonAncestor} "
                + $"synodic={sched.SynodicPeriodSeconds.ToString("F0", IC)}s cadence={sched.CadenceSeconds.ToString("F0", IC)}s "
                + $"span={span.ToString("F0", IC)}s d0={d0.ToString("F0", IC)} d1={d1.ToString("F0", IC)} d2={d2.ToString("F0", IC)} "
                + $"arrivalHold={(hasHold ? unit.ArrivalHoldSeconds.ToString("F0", IC) + "s@" + unit.ArrivalAlignPeriodSeconds.ToString("F0", IC) + "s" : "none")} "
                + $"descentTrigger={(unit.HasDescentTrigger ? "yes" : "no")} "
                + $"amber={(unit.ArrivalAmberReason ?? "none")}");
        }

        [InGameTest(Category = "Missions", Scene = GameScenes.FLIGHT,
            Description = "A real station-rendezvous mission in the loaded save (e.g. an 'orbital supply route' Mun/Duna station resupply) emits a VesselOrbital constraint through the live extractor and either phase-locks (same-parent) or produces a station arrival hold (cross-parent) with a sane finite period (validates M-MIS-4). Skips cleanly when the save has no station mission.")]
        public void RealSave_StationMission_PhaseLocksOrHolds()
        {
            if (!IsMissionStoreReady())
                InGameAssert.Skip("Missions not loaded / FlightGlobals bodies not ready; load a save with recorded missions");

            if (!RealSaveMissionFinder.TryFindStationRendezvousMission(
                    FlightGlobalsBodyInfo.Instance,
                    out RealSaveMissionFinder.StationMissionMatch match))
            {
                InGameAssert.Skip(
                    "no station-rendezvous mission in the loaded save; load e.g. the 'orbital supply route' save (a Mun/Duna station resupply mission)");
            }

            PhaseConstraint station = match.StationConstraint;
            ConstraintExtraction extraction = match.Extraction;

            // (1) The live extractor emitted a VesselOrbital constraint (rendezvous with an orbiting
            //     station) with a sane finite period - the M-MIS-4 Tier-1/Tier-2 precondition.
            InGameAssert.AreEqual(ConstraintKind.VesselOrbital, station.Kind,
                "the resolved station constraint must be VesselOrbital");
            InGameAssert.IsTrue(station.AnchorVesselPid != 0u,
                "the station constraint must carry a live anchor vessel pid");
            InGameAssert.IsTrue(
                station.PeriodSeconds > 0.0 && !double.IsNaN(station.PeriodSeconds)
                && !double.IsInfinity(station.PeriodSeconds),
                $"the station orbital period must be finite positive (got {station.PeriodSeconds.ToString("R", IC)})");

            // (2) The solver either PHASE-LOCKS the config (same-parent: the station orbits the launch
            //     body / a transited body directly, so the whole config recurs and Solve locks it with
            //     a finite recurrence period P) OR the destination is cross-parent (UnsupportedCrossParent),
            //     where re-aim consumes the VesselOrbital as a STATION arrival hold (T_station substituted
            //     for T_rot). Both are the shipped M-MIS-4 outcomes; a bare UnsupportedRendezvous reject
            //     (the pre-M4a behavior) would be a regression and is asserted against.
            InGameAssert.AreNotEqual(Support.UnsupportedRendezvous, extraction.Support,
                "a real station rendezvous must no longer fail closed as UnsupportedRendezvous (M-MIS-4 flipped reject -> extract)");

            PeriodicitySolution solution = match.Solution;
            bool sameParentPhaseLock = solution.ShouldPhaseLock
                && solution.P > 0.0 && !double.IsNaN(solution.P) && !double.IsInfinity(solution.P)
                && !double.IsNaN(solution.NextWindowUT);
            bool crossParentHold = extraction.Support == Support.UnsupportedCrossParent;

            InGameAssert.IsTrue(sameParentPhaseLock || crossParentHold,
                "a real station mission must either phase-lock (same-parent, finite recurrence P) or be "
                + $"cross-parent (station arrival hold): support={extraction.Support} shouldPhaseLock={solution.ShouldPhaseLock} "
                + $"P={solution.P.ToString("R", IC)} method={solution.Method ?? "?"}");

            if (sameParentPhaseLock)
            {
                // The locked recurrence must be finite and at least the station period (the dominant
                // intercept ranks with Orbital, so the joint period is a whole multiple of a constraint
                // period, never shorter than the station's own orbit).
                InGameAssert.IsTrue(solution.P >= station.PeriodSeconds - 1.0,
                    $"the phase-lock recurrence P ({solution.P.ToString("F0", IC)}s) must be at least the "
                    + $"station period ({station.PeriodSeconds.ToString("F0", IC)}s)");
            }

            ParsekLog.Info("RealSaveMissionTest",
                $"RealSave_StationMission: PASS mission='{match.Mission.Name}' tree={match.Tree.Id} "
                + $"station=pid{station.AnchorVesselPid.ToString(IC)}@{station.BodyName ?? "?"} "
                + $"Tstation={station.PeriodSeconds.ToString("F0", IC)}s support={extraction.Support} "
                + $"{(sameParentPhaseLock ? "phaseLock P=" + solution.P.ToString("F0", IC) + "s method=" + (solution.Method ?? "?") : "cross-parent station hold")} "
                + $"launchBody={extraction.LaunchBodyName ?? "?"}");
        }

        // The finder needs the mission store populated and the live body graph ready (the extractor +
        // builder read FlightGlobalsBodyInfo.Instance, which resolves bodies through FlightGlobals).
        // On a fresh scene with no missions, or before FlightGlobals is up, there is nothing to
        // classify - skip rather than false-fail.
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
