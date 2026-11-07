using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    // The route's loop clock: the backing mission's loop unit and the seconds to the next dock crossing or dispatch window.
    internal static partial class RouteOrchestrator
    {
        /// <summary>
        /// READ-ONLY next-dock-crossing countdown accessor (H1, UI layer). Given a
        /// route and the current UT, returns in <paramref name="seconds"/> the
        /// wall-clock-UT seconds until the route's loop clock next reaches the
        /// recorded dock PHASE (one delivery == one dock crossing). Built for the
        /// Logistics window's "Next delivery" countdown; the window THROTTLES the
        /// call (the <see cref="ResolveLoopUnit"/> build is not free) and never sees
        /// a <see cref="GhostPlaybackLogic.LoopUnit"/> or the raw committed lists, so
        /// the ERS/ELS grep gate stays green with no allowlist change (the only raw
        /// read stays behind <see cref="ResolveLoopUnit"/> in this allowlisted file).
        ///
        /// <para><b>Strictly read-only.</b> Mirrors the
        /// <see cref="ProcessLoopRoute"/> resolve path
        /// (<see cref="ResolveLoopUnit"/>) but mutates NOTHING: it never touches
        /// <see cref="Route.LastObservedLoopCycleIndex"/> or any loop-clock state.
        /// Only <see cref="ProcessLoopRoute"/> advances the dispatch phase.</para>
        ///
        /// <para>Returns <c>false</c> (<paramref name="seconds"/> = 0) when the route
        /// is not ghost-driving (<see cref="RouteStatusPolicy.GhostDriving"/> false:
        /// Paused / EndpointLost / MissingSourceRecording / SourceChanged), has no
        /// resolvable loop unit this tick, carries a non-v0 schedule / loiter cut, or
        /// the recorded dock UT falls outside the unit's span. The window shows a
        /// dash / falls back to its wait-state countdown in those cases.</para>
        /// </summary>
        /// <param name="route">The route to count down for.</param>
        /// <param name="nowUT">Current game UT.</param>
        /// <param name="seconds">Seconds until the next dock crossing (always &gt; 0
        /// on success); 0 on a false return.</param>
        /// <returns>True when a finite next-dock-crossing countdown exists.</returns>
        internal static bool TryComputeSecondsToNextDockCrossing(
            Route route, double nowUT, out double seconds)
        {
            seconds = 0.0;

            if (route == null)
                return false;

            // Status gate: only ghost-driving routes run the loop clock, so only
            // they have a next-crossing. Mirrors ProcessLoopRoute's first gate.
            if (!RouteStatusPolicy.GhostDriving(route.Status))
            {
                ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                    $"NextDockCrossing: route {ShortIdForLog(route)} status={route.Status} " +
                    "not ghost-driving, no countdown", 5.0);
                return false;
            }

            // Resolve the backing-mission loop unit (test seam or live build). The
            // raw committed-list read stays inside ResolveLoopUnit in this
            // allowlisted file.
            GhostPlaybackLogic.LoopUnit? unitOpt = ResolveLoopUnit(route, nowUT);
            if (!unitOpt.HasValue)
            {
                ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                    $"NextDockCrossing: route {ShortIdForLog(route)} no resolvable loop unit, no countdown",
                    5.0);
                return false;
            }
            GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;

            // Pure crossing-to-seconds math (gate-safe; reads only LoopUnit accessors).
            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT, route.RecordedDockUT, route.LastObservedLoopCycleIndex, out seconds);
            if (!ok)
            {
                ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                    $"NextDockCrossing: route {ShortIdForLog(route)} dock-out-of-span/non-v0 " +
                    $"(dockUT={route.RecordedDockUT.ToString("R", IC)} " +
                    $"spanStart={unit.SpanStartUT.ToString("R", IC)} spanEnd={unit.SpanEndUT.ToString("R", IC)}) " +
                    "no countdown", 5.0);
                seconds = 0.0;
                return false;
            }

            ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                $"NextDockCrossing: route {ShortIdForLog(route)} secondsToNextDock={seconds.ToString("R", IC)} " +
                $"at ut={nowUT.ToString("R", IC)} lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)}",
                5.0);
            return true;
        }

        /// <summary>
        /// (M5 D8) READ-ONLY next-dispatch-WINDOW countdown accessor for the
        /// Logistics window. Mirrors <see cref="TryComputeSecondsToNextDockCrossing"/>'s
        /// discipline (throttled by the ~1 Hz legibility cache, mutates NOTHING,
        /// never leaks a <see cref="GhostPlaybackLogic.LoopUnit"/> or a raw
        /// committed list) but serves the WINDOWED bases the flat H1 helper
        /// deliberately refuses: for <see cref="RouteWindowBasis.ZeroDriftSchedule"/>
        /// the next scheduled launch (<c>RelaunchSchedule.NextLaunchAfter</c>);
        /// for <see cref="RouteWindowBasis.ReaimWindows"/> the launch of the next
        /// DELIVERABLE window - the smallest k with
        /// <c>unit.PhaseAnchorUT + k * unit.CadenceSeconds &gt; nowUT</c> that the
        /// residual modulo (anchor + N, D2/D3) admits. The countdown targets the
        /// window LAUNCH, not the dock instant (per-launch knob timings / loiter
        /// cuts make the exact dock offset builder-internal; the launch is the
        /// honest, stable number - label it so, D8).
        ///
        /// <para><b>Field-source pin (review Missed #2).</b> ALL window
        /// arithmetic here reads <c>unit.PhaseAnchorUT</c> /
        /// <c>unit.CadenceSeconds</c>, NEVER raw <c>ReaimSchedule</c> fields:
        /// pad-align updates <c>sched.FirstDepartureUT</c> / <c>CadenceSeconds</c>
        /// but NOT <c>sched.PhaseAnchorUT</c>, and the unit anchor additionally
        /// carries the loiter-compression cutBeforeDeparture shift - raw sched
        /// fields desynchronize k from the firing index space.</para>
        ///
        /// <para><paramref name="basis"/> + <paramref name="targetBody"/> are
        /// populated whenever the unit resolves (even on a false return) so the
        /// caller can label the row; a <see cref="RouteWindowBasis.FlatInterval"/>
        /// route returns false and the caller keeps the existing flat dock
        /// countdown - flat rows render byte-identically.</para>
        /// </summary>
        /// <param name="route">The route to count down for.</param>
        /// <param name="nowUT">Current game UT.</param>
        /// <param name="seconds">Seconds until the next deliverable dispatch
        /// window's launch (&gt; 0 on success); 0 on a false return.</param>
        /// <param name="basis">The derived window basis (FlatInterval when the
        /// route is not ghost-driving / has no resolvable unit).</param>
        /// <param name="targetBody">The re-aim plan's target body for the basis
        /// label ("(Duna transfer)"), or null.</param>
        /// <returns>True when a finite windowed launch countdown exists.</returns>
        internal static bool TryComputeSecondsToNextDispatchWindow(
            Route route, double nowUT, out double seconds,
            out RouteWindowBasis basis, out string targetBody)
        {
            seconds = 0.0;
            basis = RouteWindowBasis.FlatInterval;
            targetBody = null;

            if (route == null)
                return false;

            // Status gate: only ghost-driving routes run the loop clock (mirrors
            // the H1 accessor, whose rate-limited log already names the status).
            if (!RouteStatusPolicy.GhostDriving(route.Status))
                return false;

            GhostPlaybackLogic.LoopUnit? unitOpt = ResolveLoopUnit(route, nowUT);
            if (!unitOpt.HasValue)
                return false;
            GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;

            basis = RouteLoopClock.DeriveWindowBasis(unit);
            if (basis == RouteWindowBasis.ReaimWindows && unit.ReaimPlan.HasValue)
                targetBody = unit.ReaimPlan.Value.TargetBody;

            switch (basis)
            {
                case RouteWindowBasis.ZeroDriftSchedule:
                {
                    // Non-uniform launch UTs: resolve through the schedule only
                    // (the flat arithmetic is INVALID for a Scheduled index).
                    double next = unit.RelaunchSchedule.NextLaunchAfter(nowUT);
                    if (double.IsNaN(next) || next <= nowUT)
                        return false;
                    seconds = next - nowUT;
                    ParsekLog.VerboseRateLimited(Tag, "next-window-" + route.Id,
                        $"NextDispatchWindow: route {ShortIdForLog(route)} basis=ZeroDriftSchedule " +
                        $"nextLaunchUT={next.ToString("R", IC)} seconds={seconds.ToString("R", IC)} " +
                        $"at ut={nowUT.ToString("R", IC)}", 5.0);
                    return true;
                }

                case RouteWindowBasis.ReaimWindows:
                {
                    // Field-source pin: unit anchor + unit cadence ONLY.
                    double anchorUT = unit.PhaseAnchorUT;
                    double cadence = unit.CadenceSeconds;
                    if (double.IsNaN(cadence) || cadence <= 0.0)
                        return false;

                    // Smallest window k whose launch is strictly after now.
                    long k = (long)Math.Floor((nowUT - anchorUT) / cadence) + 1;
                    if (k < 0)
                        k = 0;

                    // Advance to the next DELIVERABLE window under the residual
                    // modulo. An unset anchor (-1) means the next crossing
                    // adopts and delivers (D3), so k stands as-is.
                    int nResidual = RouteLoopClock.ResolveResidualCadence(
                        basis, route.CadenceMultiplier);
                    if (nResidual > 1 && route.WindowAnchorCycleIndex >= 0)
                    {
                        long m = (k - route.WindowAnchorCycleIndex) % nResidual;
                        if (m < 0)
                            m += nResidual;
                        if (m != 0)
                            k += nResidual - m;
                    }

                    double launchUT = anchorUT + k * cadence;
                    if (launchUT <= nowUT)
                        return false;
                    seconds = launchUT - nowUT;
                    ParsekLog.VerboseRateLimited(Tag, "next-window-" + route.Id,
                        $"NextDispatchWindow: route {ShortIdForLog(route)} basis=ReaimWindows " +
                        $"window={k.ToString(IC)} launchUT={launchUT.ToString("R", IC)} " +
                        $"seconds={seconds.ToString("R", IC)} " +
                        $"(N={nResidual.ToString(IC)} anchor={route.WindowAnchorCycleIndex.ToString(IC)} " +
                        $"target={targetBody ?? "<none>"}) at ut={nowUT.ToString("R", IC)}", 5.0);
                    return true;
                }

                default:
                    // FlatInterval: the existing flat dock countdown owns it.
                    return false;
            }
        }

        /// <summary>
        /// Resolves the route's backing-mission loop unit for this tick. Routes
        /// through the <see cref="LoopUnitResolverForTesting"/> seam when set;
        /// otherwise builds it from live KSP state via the first-class Missions
        /// entry point (<c>MissionLoopUnitBuilder.TryBuildLoopUnitForSelection</c>,
        /// M-MIS-11 item 1 - the same pipeline the host push seams'
        /// <c>Build</c> runs for a one-element list) over the SAME
        /// <c>RecordingStore.CommittedRecordings</c> / <c>CommittedTrees</c>
        /// snapshot, so member indices align. It also passes the IDENTICAL
        /// phase-lock seams the render path uses
        /// (<c>FlightGlobalsBodyInfo.Instance</c> +
        /// <see cref="ParsekSettings.LandingBodyAlignmentMode"/>) so the delivery
        /// clock's <c>CadenceSeconds</c> / <c>PhaseAnchorUT</c> match the rendered
        /// ghost's exactly (DEL-1): a supported looping mission snaps its phase
        /// anchor to the next faithful launch window, and an unsupported config
        /// (cross-parent / rendezvous / no constraint — or, in xUnit, an empty
        /// <c>FlightGlobals</c>) degrades to no phase-lock, byte-identical to the
        /// pre-DEL-1 <c>bodyInfo:null</c> path. For a v0 same-body route this is the
        /// faithful (non-re-aimed) loop. Returns null when no unit owns the route's
        /// members this tick.
        ///
        /// <para><b>(M-MIS-11 item 1) Signature-gated cache.</b> The full builder
        /// pipeline used to re-run on EVERY orchestrator tick and again per
        /// Logistics-window countdown call. The built unit is now cached on the
        /// route (runtime-only fields, <c>RouteCodec</c> untouched) and reused
        /// while BOTH cache keys hold: the one-element
        /// <c>MissionLoopUnitBuilder.BuildSignature</c> (the SAME change-detection
        /// signature every scene render driver uses, covering the mission fields,
        /// tree counts, committed-list identity, auto-loop setting, body-geometry
        /// + station-anchor digests, and rotation mode) and the M-MIS-9
        /// <c>RouteBackingMission.ComputeTopologySignature</c> of the backing tree
        /// (id-level rolling hashes that catch count-neutral tree mutations the
        /// builder signature's counts miss). Any input drift the old
        /// rebuild-every-tick code would have picked up moves one of the two keys
        /// and forces a rebuild; a cached NULL outcome (builder yielded no unit)
        /// is reused the same way. Rebuilds log Verbose with the change reason;
        /// steady-state hits log nothing.</para>
        /// </summary>
        internal static GhostPlaybackLogic.LoopUnit? ResolveLoopUnit(Route route, double currentUT)
        {
            var resolver = LoopUnitResolverForTesting;
            if (resolver != null)
                return resolver(route, currentUT);

            // Live build. Construct the route's backing Mission (cheap: the
            // M-MIS-9 auto-exclude derivation inside is already signature-gated),
            // then resolve the unit through the signature-gated cache below. The
            // build gates on Mission.LoopPlayback (set by BuildMission).
            Mission mission = RouteBackingMission.BuildMission(route, currentUT);
            if (mission == null)
                return null;

            // [ERS-exempt] member-index alignment with the engine. MissionLoopUnitBuilder
            // returns LoopUnit member indices keyed to the RAW
            // RecordingStore.CommittedRecordings list (the engine's alignment
            // contract). The host push seams (ParsekFlight / ParsekKSC /
            // ParsekTrackingStation DriveMissionLoopUnits — all already allowlisted)
            // build the rendered LoopUnitSet off this SAME raw list; the orchestrator
            // MUST source the identical committed snapshot so its clock-unit member
            // indices match the rendered ones (plan "Highest Residual Risks" #2). A
            // supersede-aware ERS filter would re-index the list and silently point
            // the loop clock at the wrong recording. No ledger read here.
            var committed = RecordingStore.CommittedRecordings;
            var trees = RecordingStore.CommittedTrees;
            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;

            // DEL-1: pass the SAME phase-lock seams every render seam uses
            // (ParsekFlight / ParsekKSC / ParsekTrackingStation DriveMissionLoopUnits)
            // so the delivery clock phase-locks to the IDENTICAL CadenceSeconds /
            // PhaseAnchorUT the ghost renders on. FlightGlobalsBodyInfo.Instance is a
            // non-null static singleton; in xUnit (empty FlightGlobals) it degrades to
            // no phase-lock, byte-identical to the pre-DEL-1 bodyInfo:null path, so
            // tests on the LoopUnitResolverForTesting seam are unaffected.
            IBodyInfo bodyInfo = FlightGlobalsBodyInfo.Instance;
            TransitedBodyRotationMode tbrMode = ParsekSettings.LandingBodyAlignmentMode;
            // Force-faithful knob (harness-only since the 2026-08-27 settings simplification;
            // set via the M-A2 command seam): read and folded into builderSignature below, so a
            // flipped value invalidates THIS route's cached loop unit too and the delivery clock
            // keeps matching the render seams' schedule.
            bool forceFaithful = ParsekSettings.Current?.forceFaithfulLoopPlayback ?? false;

            // (M-MIS-11 item 1) Cache keys. The builder signature is the SAME
            // sanctioned change-detection fold the render drivers gate their
            // per-frame Build on; the topology signature is the SAME M-MIS-9
            // scheme BuildMission's auto-exclude cache uses (id-level hashes
            // covering count-neutral tree mutations the builder signature's
            // count folds miss). Computing both per tick is the render seams'
            // established per-frame cost; the FULL builder pipeline only runs on
            // a key change.
            var missionList = new List<Mission> { mission };
            string builderSignature = MissionLoopUnitBuilder.BuildSignature(
                missionList, trees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode, forceFaithful);
            string topologySignature = RouteBackingMission.ComputeTopologySignature(
                RouteTreeGuard.FindCommittedTree(route.BackingMissionTreeId));

            bool cachePrimed = route.LoopUnitBuilderSignature != null;
            bool builderKeyHolds = cachePrimed && string.Equals(
                builderSignature, route.LoopUnitBuilderSignature, StringComparison.Ordinal);
            bool topologyKeyHolds = cachePrimed && string.Equals(
                topologySignature, route.LoopUnitTopologySignature, StringComparison.Ordinal);
            if (builderKeyHolds && topologyKeyHolds)
                return route.CachedLoopUnit; // steady-state hit: no build, no log

            string rebuildReason = !cachePrimed
                ? "first-build"
                : !builderKeyHolds ? "builder-inputs-changed" : "tree-topology-changed";

            // Suppress the pure-derivation diagnostic logs the builder pipeline emits
            // (BuildMissionStructure / BuildComposition / ExtractConstraints / Solve /
            // ReaimDiag / MissionLoopUnit / PhaseLock). This resolver runs on EVERY delivery-clock
            // tick (ProcessLoopRoute) and every Logistics-window OnGUI frame
            // (ComputeRouteLegibility -> TryComputeSecondsToNextDockCrossing); the
            // signature cache above collapses steady-state calls, but a genuinely
            // changing input set (e.g. an in-progress tree gaining recordings) can
            // still rebuild often, so an un-suppressed build would flood the log
            // with a near-static verdict. The LoopUnit computed is byte-identical -
            // only the diagnostic output is gated (mirrors MissionsWindowUI's
            // display-mirror build). These flags gate Verbose/Info plus two
            // pre-existing MissionPeriodicity diagnostic Warns (degenerate-period
            // filter; over-constrained Tier-1 residual); the MissionLoopUnitBuilder
            // owner/member collision Warns stay un-gated. Silencing the
            // MissionPeriodicity Warns here is intentional - at Warn they ignore
            // verbose-off and would flood, and the signature-gated render build
            // (DriveMissionLoopUnits) plus the Missions window still surface them
            // un-suppressed for the same config.
            bool prevStructSuppress = MissionStructureBuilder.SuppressLogging;
            bool prevCompSuppress = MissionCompositionBuilder.SuppressLogging;
            bool prevPeriodicitySuppress = MissionPeriodicity.SuppressLogging;
            bool prevLoopSuppress = MissionLoopUnitBuilder.SuppressLogging;
            // M-MIS-8 parity with the UI display-mirror build: a synthesized backing mission
            // never carries dock links today, but gate the fifth pipeline flag anyway so a
            // future linked selection cannot flood this per-tick path.
            bool prevCrossTreeSuppress = MissionCrossTreeDock.SuppressLogging;
            MissionStructureBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = true;
            MissionCrossTreeDock.SuppressLogging = true;
            bool built;
            GhostPlaybackLogic.LoopUnit unit;
            try
            {
                built = MissionLoopUnitBuilder.TryBuildLoopUnitForSelection(
                    mission, trees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode, forceFaithful,
                    out unit);
            }
            finally
            {
                MissionStructureBuilder.SuppressLogging = prevStructSuppress;
                MissionCompositionBuilder.SuppressLogging = prevCompSuppress;
                MissionPeriodicity.SuppressLogging = prevPeriodicitySuppress;
                MissionLoopUnitBuilder.SuppressLogging = prevLoopSuppress;
                MissionCrossTreeDock.SuppressLogging = prevCrossTreeSuppress;
            }

            route.CachedLoopUnit = built ? unit : (GhostPlaybackLogic.LoopUnit?)null;
            route.LoopUnitBuilderSignature = builderSignature;
            route.LoopUnitTopologySignature = topologySignature;
            // Rate-limited per route+reason: a quantization-boundary thrash (loaded
            // anchor under thrust straddling the 1s period floor) rebuilds with a
            // constant reason on every Tick and Logistics-window OnGUI frame; the
            // reason in the key keeps distinct transitions individually visible.
            ParsekLog.VerboseRateLimited(Tag,
                "loop-unit-cache-rebuild-" + route.Id + "-" + rebuildReason,
                $"ResolveLoopUnit: route {ShortIdForLog(route)} loop-unit cache rebuilt " +
                $"reason={rebuildReason} built={(built ? "1" : "0")} " +
                $"tree={route.BackingMissionTreeId ?? "<null>"} at ut={currentUT.ToString("R", IC)}");
            return route.CachedLoopUnit;
        }
    }
}
