using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    // M-MIS-2 P5 - re-aim Phase-4 render-path threading canary (plan
    // docs/dev/plans/reaim-destination-arrival-alignment.md section 12 / Phase 5).
    //
    // THE PROPERTY: for a re-aimed loop unit, the DIRECTOR-driven orbit (the icon drive /
    // proto-orbit epoch bake: GhostMapPresence computes loopEpochShiftSeconds = currentUT - effUT
    // and seeds the ghost orbit + arc bounds with it) and the BODY-FIXED POLYLINE (the
    // GhostTrajectoryPolylineRenderer.Driver walk, which resolves its per-recording head via the
    // dual-clock frame resolver and gates/derotates body-fixed legs off that head) must read the
    // SAME loop shift for the same recording at the same frame. Both are DESIGNED to inherit the
    // shift from the one shared span clock (GhostPlaybackLogic.TryComputeSpanLoopUT via
    // ResolveTrackingStationSampleUT), so the property is structural today - this canary exists to
    // catch a future split of the convention. The convention has ALREADY split once (blocker B1 of
    // the P4 plan: the director-baked orbit samples at currentUT while a raw recorded orbit samples
    // at currentUT - loopShift; MapRenderProbe.ResolveFaithfulLookupUT exists because of it), so a
    // second silent split is a real risk class, not a hypothetical.
    //
    // WHAT IS COMPARED (production entry points, NOT a test-side re-derivation): each render path's
    // OWN resolver seam, with the exact argument wiring its production call site uses -
    //   - director / map-presence side: GhostMapPresence.ResolveMapPresenceSampleUT(idx,
    //     rec.StartUT, rec.EndUT, ut, units, out hidden, out shift) - the seam the flight-map orbit
    //     driver reads (GhostMapPresence orbit refresh) and whose shift value is what
    //     ApplyOrbitToVessel bakes into the proto orbit epoch (the tracking-station lifecycle
    //     computes the identical currentUT - ResolveTrackingStationSampleUT(...) inline);
    //   - polyline side: GhostPlaybackLogic.ResolveTrackingStationSampleFrame(idx, rec.StartUT,
    //     rec.EndUT, ut, units, ...) - the exact call the polyline Driver walk makes; its primary
    //     head gates the body-fixed legs, so the polyline's effective loop shift is ut - headUT.
    // A test that re-implemented "shift = ut - TryComputeSpanLoopUT(...)" itself would prove
    // nothing (it would drift together with whichever path it copied); calling the two seams the
    // renderers actually consume is the honest read.
    //
    // SCOPE: read-only (the finder builds a transient loop-enabled clone; the store is untouched),
    // batch-safe, real-save driven: finds a committed re-aimed loop mission via
    // RealSaveMissionFinder.TryFindReaimMission and SKIPS cleanly (naming the save) when the loaded
    // save has none - the maintainer runs it on save s15 ("Duna One", the real Kerbin->Duna re-aim
    // landing). Samples across TWO live frames (coroutine yield) and, per frame, probes the pure
    // resolvers at the live UT plus offsets spanning TWO loop cycles, because a shift divergence
    // would appear at cycle boundaries / re-aim window changes, not necessarily at the instant the
    // test happens to run. Explicitly OUT OF SCOPE: the S4 descent re-stitch and any change to how
    // either path computes the shift.
    public class ReaimRenderLoopShiftCanaryInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Both compared seams funnel into the same shared span clock today, so the two shifts are
        /// equal by construction; the epsilon only needs to absorb double round-off, not model error.
        /// </summary>
        private const double ShiftEqualityEpsilonSeconds = 1e-6;

        /// <summary>A sample only counts as NON-VACUOUS evidence when the member actually renders
        /// with a real (nonzero) loop shift; hidden or zero-shift samples satisfy equality trivially.</summary>
        private const double MeaningfulShiftSeconds = 1.0;

        [InGameTest(Category = "Missions", Scene = GameScenes.FLIGHT,
            Description = "M-MIS-2 P5 render canary: for a real re-aimed loop mission in the loaded save (e.g. s15 'Duna One'), the director-driven orbit (icon-drive epoch shift, GhostMapPresence.ResolveMapPresenceSampleUT) and the body-fixed polyline (Driver head, GhostPlaybackLogic.ResolveTrackingStationSampleFrame) read the SAME loop shift for every unit member, sampled across two frames and probed across two loop cycles. Skips cleanly when the save has no re-aim mission.")]
        public IEnumerator RealSave_ReaimLoop_DirectorOrbitAndPolylineReadSameLoopShift()
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
            GhostPlaybackLogic.LoopUnitSet units = match.Units;
            InGameAssert.IsNotNull(units, "the finder must carry the built LoopUnitSet");
            InGameAssert.IsTrue(unit.MemberIndices != null && unit.MemberIndices.Length > 0,
                "the re-aim unit must have at least one member");
            InGameAssert.IsTrue(
                unit.CadenceSeconds > 0.0 && !double.IsNaN(unit.CadenceSeconds)
                && !double.IsInfinity(unit.CadenceSeconds),
                $"the unit cadence must be finite positive (got {unit.CadenceSeconds.ToString("R", IC)})");

            // Two live frames: the resolvers are pure in the passed UT, but sampling on two REAL
            // frames also exercises them at two genuinely distinct live clocks (and would catch a
            // hypothetical frame-cached divergence on either side).
            int meaningful = SampleFrame(match, "frame1");
            yield return null;
            meaningful += SampleFrame(match, "frame2");

            if (meaningful == 0)
            {
                InGameAssert.Skip(
                    "every probed sample was hidden or zero-shift (live clock before the loop start or still inside "
                    + "the recorded span), so the equality held only vacuously; re-run on s15 at a UT past the "
                    + "recorded mission so the loop actually replays with a nonzero shift");
            }

            ParsekLog.Info("Reaim",
                $"P5 render loop-shift canary: PASS mission='{match.Mission.Name}' tree={match.Tree.Id} "
                + $"members={unit.MemberIndices.Length.ToString(IC)} cadence={unit.CadenceSeconds.ToString("F0", IC)}s "
                + $"meaningfulSamples={meaningful.ToString(IC)} (director epoch shift == polyline head shift "
                + $"within {ShiftEqualityEpsilonSeconds.ToString("R", IC)}s at every probe)");
        }

        /// <summary>
        /// Probes every unit member through BOTH production resolver seams at the live UT and at
        /// offsets spanning two loop cycles, asserting hidden-parity and shift equality per sample.
        /// Returns the number of non-vacuous samples (rendering with a meaningful nonzero shift).
        /// </summary>
        private static int SampleFrame(RealSaveMissionFinder.ReaimMissionMatch match, string frameLabel)
        {
            GhostPlaybackLogic.LoopUnit unit = match.Unit;
            GhostPlaybackLogic.LoopUnitSet units = match.Units;
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            double liveUT = Planetarium.GetUniversalTime();
            double cadence = unit.CadenceSeconds;

            // Probe offsets: the live frame itself, three quarter-cycle points (so at least one
            // probe lands inside a member window regardless of the live loop phase), and the same
            // again one full cadence later (the NEXT loop cycle - a shift divergence would appear at
            // cycle boundaries / per-loop hold re-alignment, not necessarily at the live instant).
            double[] offsets =
            {
                0.0,
                cadence * 0.25, cadence * 0.5, cadence * 0.75,
                cadence, cadence * 1.25, cadence * 1.5, cadence * 1.75,
            };

            int meaningful = 0;
            int samples = 0;
            for (int m = 0; m < unit.MemberIndices.Length; m++)
            {
                int idx = unit.MemberIndices[m];
                if (idx < 0 || idx >= committed.Count || committed[idx] == null)
                    continue;
                Recording rec = committed[idx];

                // A DESCENT-set member's resolver branch is not pure observability: it feeds the
                // bounded per-cycle DescentRenderTrace state machine and (debug-gated) registers
                // MapRenderWarpControl watch windows from the UT it is called with. At the LIVE UT
                // that is exactly what the production Driver already did this frame (the calls are
                // idempotent per frame); at SYNTHETIC future UTs it would pollute that per-cycle
                // state. So descent members are probed at the live UT only - their head resolves
                // through the same shared clock either way, and the future-cycle coverage comes from
                // the non-descent members.
                bool liveOnly = unit.HasDescentTrigger && unit.IsDescentMember(idx);

                for (int o = 0; o < offsets.Length; o++)
                {
                    if (liveOnly && offsets[o] != 0.0)
                        continue;
                    double probeUT = liveUT + offsets[o];

                    // DIRECTOR / map-presence seam (the shift ApplyOrbitToVessel bakes into the
                    // proto orbit epoch), with the flight-map driver's exact argument wiring.
                    double directorEffUT = GhostMapPresence.ResolveMapPresenceSampleUT(
                        idx, rec.StartUT, rec.EndUT, probeUT, units,
                        out bool directorHidden, out double directorShift);

                    // POLYLINE seam (the Driver walk's head resolver); the polyline's effective
                    // loop shift for its body-fixed leg gating is probeUT - headUT.
                    double polylineHeadUT = GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                        idx, rec.StartUT, rec.EndUT, probeUT, units,
                        out bool polylineHidden, out bool hasSecondary, out double secondaryUT,
                        out long secondaryCycle);
                    double polylineShift = probeUT - polylineHeadUT;

                    samples++;

                    InGameAssert.IsTrue(directorHidden == polylineHidden,
                        $"{frameLabel}: hidden-decision split for member #{idx.ToString(IC)} "
                        + $"('{rec.VesselName ?? "(null)"}') at offset {offsets[o].ToString("F0", IC)}s: "
                        + $"director hidden={directorHidden} polyline hidden={polylineHidden} "
                        + $"probeUT={probeUT.ToString("F1", IC)}");

                    InGameAssert.IsTrue(
                        System.Math.Abs(directorShift - polylineShift) <= ShiftEqualityEpsilonSeconds,
                        $"{frameLabel}: LOOP-SHIFT SPLIT for member #{idx.ToString(IC)} "
                        + $"('{rec.VesselName ?? "(null)"}') at offset {offsets[o].ToString("F0", IC)}s: "
                        + $"director shift={directorShift.ToString("R", IC)}s (effUT={directorEffUT.ToString("F1", IC)}) "
                        + $"polyline shift={polylineShift.ToString("R", IC)}s (headUT={polylineHeadUT.ToString("F1", IC)}) "
                        + $"probeUT={probeUT.ToString("F1", IC)} - the director orbit and the body-fixed polyline "
                        + "no longer read the same loop shift (M-MIS-2 P5 contract)");

                    if (!directorHidden && System.Math.Abs(directorShift) > MeaningfulShiftSeconds)
                        meaningful++;

                    // Log the two shift values for the LIVE probe so a failure (or a pass) is
                    // diagnosable from KSP.log alone; bounded to members x 2 frames. When a live
                    // ghost map vessel exists (map view / TS visited this scene), also log the
                    // APPLIED per-pid epoch shift registry value as a diagnostic. It is written at
                    // reseed time and can lag the resolver by up to one reseed interval right after
                    // a cycle wrap, so it is logged, not hard-asserted (the decision-side equality
                    // above is the P5 contract; the applied registry is that value cached).
                    if (offsets[o] == 0.0)
                    {
                        uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(idx);
                        string applied = ghostPid != 0
                            ? GhostMapPresence.GetGhostOrbitEpochShift(ghostPid).ToString("F3", IC) + "s(pid=" + ghostPid.ToString(IC) + ")"
                            : "n/a(no map ghost)";
                        // The boundary-overlap SECONDARY (early-launch instance N+1) is resolved by the
                        // same frame call on both consumers, so it is logged for context, not asserted.
                        string secondary = hasSecondary
                            ? "shift=" + (probeUT - secondaryUT).ToString("F3", IC) + "s cycle=" + secondaryCycle.ToString(IC)
                            : "none";
                        ParsekLog.Info("Reaim",
                            $"P5 canary {frameLabel} member=#{idx.ToString(IC)} '{rec.VesselName ?? "(null)"}' "
                            + $"liveUT={liveUT.ToString("F1", IC)}: directorShift={directorShift.ToString("F3", IC)}s "
                            + $"polylineShift={polylineShift.ToString("F3", IC)}s hidden={directorHidden} "
                            + $"appliedGhostOrbitEpochShift={applied} secondary={secondary}");
                    }
                }
            }

            ParsekLog.Info("Reaim",
                $"P5 canary {frameLabel} summary: samples={samples.ToString(IC)} "
                + $"meaningful={meaningful.ToString(IC)} liveUT={liveUT.ToString("F1", IC)} "
                + $"cadence={cadence.ToString("F0", IC)}s");
            return meaningful;
        }

        // Same readiness gate as RealSaveMissionInGameTests: the finder needs the mission store
        // populated and the live body graph up; otherwise skip rather than false-fail.
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
