using System;
using System.Collections;
using System.Globalization;
using System.Text;
using Parsek.Reaim;
using UnityEngine;

namespace Parsek.InGameTests
{
    // MapRender high-warp icon-jump CANARY (PR #1258, branch claude/maprender-warp-artifacts-m4b-mpot91).
    //
    // Automates as much of the manual M4b validation recipe as is RELIABLY automatable:
    // "looped station mission at 1000x warp with mapRenderTracing ON, zero icon-jump anomalies
    // including a 50x->1000x step-up, anchor legs carry basis=inertial".
    //
    // WHAT IT DRIVES (residual 2 = reseed-lag icon jumps at 344x-1000x; residual 3 = anchored-leg
    // curvature morph):
    //   - forces the map-render tracer on (MapRenderTrace.ForceEnabledForTesting) so the end-of-frame
    //     MapRenderProbe emits its pure-predicate anomalies - the probe's own IsIconJump predicate is
    //     the ORACLE here, this test never re-implements it;
    //   - enables loop playback on a REAL station-or-reaim mission found in the loaded save (through the
    //     production MissionStore.SetLoopEnabled path) so a live looped ghost drives on the map;
    //   - enters map view and waits (bounded) for a ghost map ProtoVessel to materialize;
    //   - steps real TimeWarp 50x -> 1000x (the step-up the recipe names) -> back to 1x, holding at each
    //     rate for a few wall-seconds of game frames;
    //   - asserts ZERO icon-jump (icon-teleport) anomaly lines fired from the probe across the whole
    //     driven window, and that any anchored bridge-leg line drawn carried basis=inertial.
    //
    // FEASIBILITY / RESIDUAL GAP (honest): several steps are not guaranteed in a headless-driven scene,
    // so each is a BOUNDED-timeout + LOUD-SKIP (never a false fail): no station/reaim mission in the
    // save, no ghost materializing on the map, or high (on-rails) warp being unavailable for the active
    // vessel's situation all Skip loudly rather than fail. The reliable core - the probe's icon-jump
    // predicate reading zero on live ghost data through a real 50x->1000x step-up - is the permanent
    // tripwire; the pure reseed-cadence + step-up-clamp laws themselves are separately unit-tested
    // headlessly (GhostMapObservabilityTests). Isolated-tier: mutates a mission's loop flag + drives
    // warp under live statics, so the outer baseline quickload is the net; teardown also restores
    // everything explicitly.
    public sealed class MapRenderHighWarpCanaryInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // The recipe's two named rates. Resolved against the live rails table (custom-warp saves may
        // differ) via the nearest-rate index.
        private const double LowHoldRate = 50.0;
        private const double HighRate = 1000.0;

        // Bounded waits (loud-skip, never fail, on timeout).
        private const int GhostMaterializeWaitFrames = 600;
        private const float LowHoldWallSeconds = 2.0f;
        private const float HighHoldWallSeconds = 6.0f;
        private const float StepDownWallSeconds = 1.0f;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - drives real high time-warp, enters map view, and toggles a mission's loop-playback flag under live KSP statics; excluded from ordinary Run All / Run category. Use Run All + Isolated on the 'orbital supply route' save in a disposable FLIGHT session.",
            Description = "High-warp icon-jump canary (PR #1258): with map-render tracing forced on and a real looped station/reaim ghost live on the map, steps TimeWarp 50x->1000x->1x and asserts ZERO icon-jump (icon-teleport) anomalies from the probe (residual 2) and that any anchored bridge leg drew with basis=inertial (residual 3). Skips loudly when the save has no station/reaim mission, no ghost materializes, or high warp is unavailable.")]
        public IEnumerator HighWarpIconJumpCanary()
        {
            // ---- PRECONDITIONS (no state mutated yet; a skip here is a clean no-op) ----
            if (FlightGlobals.fetch == null || FlightGlobals.Bodies == null || FlightGlobals.Bodies.Count == 0)
                InGameAssert.Skip("FlightGlobals bodies not ready; load a save in FLIGHT first");
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("No active vessel; need a live orbiting vessel for high warp");
            if (TimeWarp.fetch == null || Planetarium.fetch == null)
                InGameAssert.Skip("TimeWarp / Planetarium not ready");

            var missions = MissionStore.Missions;
            var committed = RecordingStore.CommittedRecordings;
            var trees = RecordingStore.CommittedTrees;
            if (missions == null || missions.Count == 0
                || committed == null || committed.Count == 0
                || trees == null || trees.Count == 0)
                InGameAssert.Skip(
                    "no committed missions in the loaded save; load the 'orbital supply route' save "
                    + "(a looped Mun/Duna station resupply mission)");

            // Find a REAL looped/loopable station-or-reaim mission through the shipped finder.
            Mission mission = null;
            string shapeLabel = null;
            if (RealSaveMissionFinder.TryFindStationRendezvousMission(
                    FlightGlobalsBodyInfo.Instance,
                    out RealSaveMissionFinder.StationMissionMatch stationMatch))
            {
                mission = stationMatch.Mission;
                shapeLabel = "station";
            }
            else
            {
                double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                                 ?? LoopTiming.DefaultLoopIntervalSeconds;
                TransitedBodyRotationMode tbrMode =
                    ParsekSettings.Current?.TransitedBodyRotationMode ?? TransitedBodyRotationMode.Loose;
                if (RealSaveMissionFinder.TryFindReaimMission(
                        FlightGlobalsBodyInfo.Instance, autoLoopIntervalSeconds, tbrMode,
                        out RealSaveMissionFinder.ReaimMissionMatch reaimMatch))
                {
                    mission = reaimMatch.Mission;
                    shapeLabel = "reaim";
                }
            }
            if (mission == null)
                InGameAssert.Skip(
                    "no looped/loopable station-or-reaim mission in the loaded save; load the "
                    + "'orbital supply route' save (a Mun/Duna station resupply mission) or an s15-style "
                    + "Kerbin->Duna re-aim mission");

            // Resolve the 50x + 1000x rail indices against the live warp table.
            float[] rates = TimeWarp.fetch.warpRates;
            if (rates == null || rates.Length == 0)
                InGameAssert.Skip("TimeWarp.warpRates unavailable");
            int lowIdx = NearestRateIndex(rates, LowHoldRate);
            int highIdx = NearestRateIndex(rates, HighRate);
            if (highIdx <= lowIdx)
                InGameAssert.Skip(
                    $"the live warp table has no distinct 50x->1000x step-up (lowIdx={lowIdx.ToString(IC)} "
                    + $"highIdx={highIdx.ToString(IC)}); cannot exercise the recipe's named step-up");

            // ---- Observer: count icon-jump (icon-teleport) anomalies + validate anchored-leg basis ----
            // The probe routes its icon-jump anomaly through EmitAnomaly with reason=icon-teleport (the
            // IsIconJump predicate); the anchored-leg fix stamps "Anchor leg: ... basis=inertial". We
            // OBSERVE (not sink) so KSP.log still records every line for post-hoc diagnosis. The observer
            // runs on the same (main) thread as the probe and must never throw.
            int iconJumpCount = 0;
            var iconJumpSamples = new System.Collections.Generic.List<string>();
            int anchorLegLines = 0;
            int anchorLegNonInertial = 0;
            var anchorLegBad = new System.Collections.Generic.List<string>();
            bool observing = false;

            Action<string> observer = line =>
            {
                if (!observing || string.IsNullOrEmpty(line))
                    return;
                if (line.IndexOf("[" + MapRenderTrace.Tag + "]", StringComparison.Ordinal) >= 0
                    && line.IndexOf("phase=Anomaly", StringComparison.Ordinal) >= 0
                    && (line.IndexOf("reason=icon-teleport", StringComparison.Ordinal) >= 0
                        || line.IndexOf("reason=icon-jump", StringComparison.Ordinal) >= 0))
                {
                    iconJumpCount++;
                    if (iconJumpSamples.Count < 5)
                        iconJumpSamples.Add(line);
                }
                if (line.IndexOf("Anchor leg:", StringComparison.Ordinal) >= 0)
                {
                    anchorLegLines++;
                    if (line.IndexOf("basis=inertial", StringComparison.Ordinal) < 0)
                    {
                        anchorLegNonInertial++;
                        if (anchorLegBad.Count < 5)
                            anchorLegBad.Add(line);
                    }
                }
            };

            // ---- Teardown state ----
            bool tracingPrev = MapRenderTrace.ForceEnabledForTesting;
            bool? verbosePrev = ParsekLog.VerboseOverrideForTesting;
            Action<string> observerPrev = ParsekLog.TestObserverForTesting;
            bool enteredMap = false;
            bool loopChanged = false;
            bool originalLoop = mission.LoopPlayback;
            double currentUT = Planetarium.GetUniversalTime();
            int drivenFrames = 0;
            int ghostCount = 0;

            try
            {
                // Force the tracer on (the probe is the oracle) + verbose on (so the VerboseRateLimited
                // "Anchor leg:" lines emit for the residual-3 basis check). Observe every emitted line.
                MapRenderTrace.ForceEnabledForTesting = true;
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestObserverForTesting = observer;

                // Enable loop playback on the found mission via the production store path so a live
                // looped ghost drives on the map (matches the recipe's "looped station mission").
                if (!originalLoop)
                {
                    MissionStore.SetLoopEnabled(mission, true, currentUT);
                    loopChanged = true;
                }

                // Enter map view so ghost map ProtoVessels materialize (creation gates on MapView).
                if (!MapView.MapIsEnabled)
                {
                    MapView.EnterMapView();
                    enteredMap = true;
                }

                // Bounded wait for a ghost to appear on the map. Loud-skip (not fail) if none.
                int gw = 0;
                while (gw++ < GhostMaterializeWaitFrames
                       && GhostMapPresence.ghostMapVesselPids.Count == 0)
                    yield return null;
                ghostCount = GhostMapPresence.ghostMapVesselPids.Count;
                if (ghostCount == 0)
                    InGameAssert.Skip(
                        $"no ghost map presence materialized within {GhostMaterializeWaitFrames.ToString(IC)} "
                        + $"frames after enabling loop + entering map view (mission='{mission.Name}' "
                        + $"shape={shapeLabel}); cannot exercise the high-warp probe on live ghost data. "
                        + "Load the 'orbital supply route' save whose looped station ghost renders on the map.");

                ParsekLog.Info("MapRenderCanary",
                    $"HighWarpIconJumpCanary: setup ready mission='{mission.Name}' shape={shapeLabel} "
                    + $"ghosts={ghostCount.ToString(IC)} lowIdx={lowIdx.ToString(IC)}({rates[lowIdx].ToString("F0", IC)}x) "
                    + $"highIdx={highIdx.ToString(IC)}({rates[highIdx].ToString("F0", IC)}x); beginning driven window");

                // ---- DRIVE: 50x hold -> 1000x step-up -> hold -> step down. Observe the whole window. ----
                observing = true;

                // (1) 50x hold.
                TimeWarp.SetRate(lowIdx, true);
                yield return null; yield return null; yield return null;
                if (!IsRailsWarpAtLeast(rates, lowIdx))
                {
                    InGameAssert.Skip(
                        $"TimeWarp did not accept the {rates[lowIdx].ToString("F0", IC)}x low-hold rate "
                        + $"(mode={TimeWarp.WarpMode} rate={TimeWarp.CurrentRate.ToString("F0", IC)}); high "
                        + "(on-rails) warp is unavailable for this vessel/situation - run on an orbiting station.");
                }
                float lowStart = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - lowStart < LowHoldWallSeconds)
                {
                    drivenFrames++;
                    yield return null;
                }

                // (2) step-up to 1000x (the named 50x->1000x step-up; exercises ClampPendingReseedDeadline).
                TimeWarp.SetRate(highIdx, true);
                yield return null; yield return null; yield return null;
                if (!IsRailsWarpAtLeast(rates, highIdx))
                {
                    InGameAssert.Skip(
                        $"TimeWarp did not reach the {rates[highIdx].ToString("F0", IC)}x step-up rate "
                        + $"(mode={TimeWarp.WarpMode} rate={TimeWarp.CurrentRate.ToString("F0", IC)}); the "
                        + "50x->1000x step-up the recipe names is unavailable here - run on an orbiting "
                        + "station clear of atmosphere.");
                }
                float highStart = Time.realtimeSinceStartup;
                int highHoldGhostFrames = 0;
                while (Time.realtimeSinceStartup - highStart < HighHoldWallSeconds)
                {
                    drivenFrames++;
                    if (GhostMapPresence.ghostMapVesselPids.Count > 0)
                        highHoldGhostFrames++;
                    yield return null;
                }

                // (3) step back down to 1x.
                TimeWarp.SetRate(0, true);
                yield return null; yield return null;
                float downStart = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - downStart < StepDownWallSeconds)
                {
                    drivenFrames++;
                    yield return null;
                }

                observing = false;

                // ---- ASSERT ----
                ParsekLog.Info("MapRenderCanary",
                    $"HighWarpIconJumpCanary: driven window done mission='{mission.Name}' shape={shapeLabel} "
                    + $"ghosts={ghostCount.ToString(IC)} drivenFrames={drivenFrames.ToString(IC)} "
                    + $"highHoldGhostFrames={highHoldGhostFrames.ToString(IC)} "
                    + $"warpRates=[{rates[lowIdx].ToString("F0", IC)}x,{rates[highIdx].ToString("F0", IC)}x,1x] "
                    + $"iconJumps={iconJumpCount.ToString(IC)} anchorLegLines={anchorLegLines.ToString(IC)} "
                    + $"anchorLegNonInertial={anchorLegNonInertial.ToString(IC)}");

                // Vacuity guard: zero anomalies proves nothing if no ghost was actually on the map
                // during the high-warp hold (the mission window may have ended or loop playback torn
                // the presence down mid-window). Loud-skip rather than pass vacuously.
                if (highHoldGhostFrames == 0)
                    InGameAssert.Skip(
                        $"no ghost map presence was live during the {rates[highIdx].ToString("F0", IC)}x hold "
                        + $"({drivenFrames.ToString(IC)} driven frames) - the zero-anomaly assertion would be "
                        + "vacuous. Re-run on the 'orbital supply route' save with the looped station mission "
                        + "mid-window.");

                // Residual 2: the probe's own IsIconJump predicate is the oracle; zero icon-jump
                // (icon-teleport) anomalies across the driven window is the tripwire.
                if (iconJumpCount > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append(iconJumpCount.ToString(IC))
                      .Append(" icon-jump (icon-teleport) anomaly line(s) fired from the map-render probe during the ")
                      .Append("50x->").Append(rates[highIdx].ToString("F0", IC))
                      .Append("x->1x driven window - the high-warp reseed-lag icon jump regressed (residual 2). Samples:");
                    for (int i = 0; i < iconJumpSamples.Count; i++)
                        sb.Append(" | ").Append(iconJumpSamples[i]);
                    InGameAssert.Fail(sb.ToString());
                }

                // Residual 3: any anchored bridge leg drawn during the window must carry basis=inertial.
                // Opportunistic - a save with no anchored bridge legs simply reports 0 (passes).
                if (anchorLegNonInertial > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append(anchorLegNonInertial.ToString(IC))
                      .Append(" anchored bridge-leg line(s) drew WITHOUT basis=inertial - the anchored-leg ")
                      .Append("inertial-basis fix regressed (residual 3). Samples:");
                    for (int i = 0; i < anchorLegBad.Count; i++)
                        sb.Append(" | ").Append(anchorLegBad[i]);
                    InGameAssert.Fail(sb.ToString());
                }

                ParsekLog.Info("MapRenderCanary",
                    $"HighWarpIconJumpCanary: PASS zero icon-jump anomalies across the 50x->"
                    + $"{rates[highIdx].ToString("F0", IC)}x step-up; anchorLegLines={anchorLegLines.ToString(IC)} "
                    + $"(all basis=inertial); ghosts={ghostCount.ToString(IC)} drivenFrames={drivenFrames.ToString(IC)}");
            }
            finally
            {
                observing = false;
                // Restore warp to 1x instantly.
                try { if (TimeWarp.fetch != null) TimeWarp.SetRate(0, true); }
                catch (Exception ex)
                {
                    ParsekLog.Warn("MapRenderCanary",
                        $"cleanup: failed to restore warp ({ex.GetType().Name}: {ex.Message})");
                }
                // Exit map view if we entered it.
                try { if (enteredMap && MapView.MapIsEnabled) MapView.ExitMapView(); }
                catch (Exception ex)
                {
                    ParsekLog.Warn("MapRenderCanary",
                        $"cleanup: failed to exit map view ({ex.GetType().Name}: {ex.Message})");
                }
                // Un-loop the mission if we enabled it (restore its original flag).
                try { if (loopChanged) MissionStore.SetLoopEnabled(mission, originalLoop, currentUT); }
                catch (Exception ex)
                {
                    ParsekLog.Warn("MapRenderCanary",
                        $"cleanup: failed to restore mission loop flag ({ex.GetType().Name}: {ex.Message})");
                }
                // Restore the tracer / verbose / log observer.
                MapRenderTrace.ForceEnabledForTesting = tracingPrev;
                ParsekLog.VerboseOverrideForTesting = verbosePrev;
                ParsekLog.TestObserverForTesting = observerPrev;
            }
        }

        // The rails-table index whose rate is closest to <paramref name="target"/>. Handles custom-warp
        // saves whose table differs from the stock [1,5,10,50,100,1000,10000,100000].
        private static int NearestRateIndex(float[] rates, double target)
        {
            int best = 0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < rates.Length; i++)
            {
                double d = Math.Abs(rates[i] - target);
                if (d < bestDiff) { bestDiff = d; best = i; }
            }
            return best;
        }

        // True when KSP is in HIGH (on-rails) warp at (approximately) the requested rail rate. High warp
        // is refused when the active vessel is below the altitude limit / under acceleration, so a false
        // return means the situation cannot support the rate (caller loud-skips).
        private static bool IsRailsWarpAtLeast(float[] rates, int idx)
        {
            if (TimeWarp.WarpMode != TimeWarp.Modes.HIGH)
                return false;
            return TimeWarp.CurrentRate >= rates[idx] - 0.5f;
        }
    }
}
