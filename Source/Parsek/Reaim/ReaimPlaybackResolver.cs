using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Reaim
{
    // Per-window playback resolver for re-aimed interplanetary loops (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3c). Owned by the flight scene (ParsekFlight); called
    // once per frame per re-aim loop member from the trajectory-substitution seam. It maps the live
    // clock to a synodic window index, synthesizes that window's heliocentric transfer (live Lambert +
    // PatchedConics), assembles the recorded-span OrbitSegment list, wraps it in a ReaimedTrajectory,
    // and CACHES the result by (member, window) so the expensive Lambert/CalculatePatch run happens
    // only when the window advances (never on the per-frame hot path).
    //
    // Failure handling (fail to faithful, never half-applied): if the window's Lambert solve / patch
    // finds no sane transfer or no target encounter, the resolver returns the inner (recorded)
    // trajectory for that window so the ghost still plays its recorded mission rather than vanishing.
    // The miss is cached too, so a bad window is not re-solved every frame.
    internal sealed class ReaimPlaybackResolver
    {
        // Process-shared so the flight engine (cachedTrajectories substitution) and the map-presence /
        // tracking-station orbit path resolve the SAME per-window adapter from one cache. Cleared when
        // the committed set / missions change (ParsekFlight.DriveMissionLoopUnits rebuild) so a stale
        // window adapter cannot survive a recording edit. Member ids are globally unique, so a single
        // shared cache is safe.
        internal static readonly ReaimPlaybackResolver Shared = new ReaimPlaybackResolver();

        private struct CacheEntry
        {
            public long Window;
            public bool Resolved;          // a cache entry exists for this window
            public List<OrbitSegment> Segments; // the re-aimed segment list, or null on a miss (=> faithful)
            // The amount the parking-departure capture leg was shifted (review FIX #2): ResolveForFrame
            // re-times the in-capture PartEvents (ut >= RecordedArrivalUT) by this same value so capture-phase
            // FX stay aligned with the shifted geometry. 0 on the direct path / decline / miss => no re-time.
            public double CaptureShiftSeconds;
            public double RecordedArrivalUT;    // the boundary above which a PartEvent is "in-capture"
        }

        // Keyed by the member recording id (stable across frames; the committed index can shuffle).
        private readonly Dictionary<string, CacheEntry> cacheByMember = new Dictionary<string, CacheEntry>();

        /// <summary>Drops all cached window adapters (call when the committed set / missions change so a
        /// stale window adapter cannot survive a recording edit).</summary>
        internal void Clear()
        {
            cacheByMember.Clear();
        }

        /// <summary>
        /// Resolves the playback trajectory for one re-aim loop member this frame. Returns the per-window
        /// <see cref="ReaimedTrajectory"/> when the window's transfer synthesizes, or
        /// <paramref name="inner"/> (the recorded trajectory) on a window miss / pre-first-window. Never
        /// returns null. <paramref name="windowIndex"/> is the resolved synodic window (or -1 before the
        /// first window). The unit's phase-anchor / span / cadence are the SAME values the engine uses to
        /// compute its loop cycle, so this window index matches the engine's per-member cycle exactly.
        /// </summary>
        internal IPlaybackTrajectory ResolveForFrame(
            string memberId,
            IPlaybackTrajectory inner,
            ReaimMissionPlan plan,
            ReaimWindowPlanner.ReaimWindowSchedule schedule,
            double unitPhaseAnchorUT, double unitSpanStartUT, double unitSpanEndUT, double unitCadenceSeconds,
            double currentUT,
            out long windowIndex)
        {
            if (inner == null)
            {
                windowIndex = -1;
                return null;
            }
            // The member's OWN recorded segments are the substitution base: only this member's
            // heliocentric leg (if any) is re-aimed; its body-relative segments pass through.
            if (TryResolveWindowSegments(memberId, inner.OrbitSegments, plan, schedule,
                    unitPhaseAnchorUT, unitSpanStartUT, unitSpanEndUT, unitCadenceSeconds, currentUT,
                    out List<OrbitSegment> segments, out windowIndex))
            {
                // Re-time the in-capture PartEvents by the SAME shift the capture OrbitSegment moved (review
                // FIX #2): the parking-departure F2 path arrives on the shorter Hohmann tof, so the capture
                // leg + a capture-phase event (capture-burn / staging / decouple) move earlier together. The
                // flight engine consumes ReaimedTrajectory.PartEvents for FX (GhostPlaybackLogic.ApplyPartEvents),
                // so a non-zero shift here would otherwise fire capture FX |captureShift| off from the shifted
                // geometry when the ghost is watched at the arrival. captureShift is 0 on the direct path /
                // decline / miss (TryResolveWindowSegments only returns true with a cached resolved entry), so
                // this is a no-op copy there. Map/TS paths read only OrbitSegments, never these PartEvents.
                cacheByMember.TryGetValue(memberId, out CacheEntry resolved);
                List<PartEvent> retimedPartEvents = ReaimSegmentAssembler.ShiftCapturePartEvents(
                    inner.PartEvents, resolved.RecordedArrivalUT, resolved.CaptureShiftSeconds);
                return new ReaimedTrajectory(inner, segments, retimedPartEvents);
            }
            return inner;
        }

        /// <summary>
        /// Resolves the re-aimed OrbitSegment list for one re-aim member at the current frame, or false
        /// (keep the faithful recorded segments) on a window miss / pre-first-window. Shared by the flight
        /// engine (which wraps the list in a <see cref="ReaimedTrajectory"/>) AND the map-presence /
        /// tracking-station orbit path (which searches the list at the recorded-span effUT). The window is
        /// mapped from the LIVE <paramref name="currentUT"/> using the SAME span-clock + unit fields the
        /// engine uses, so flight and map resolve the IDENTICAL window. Cached by (member, window) so the
        /// live Lambert / CalculatePatch solve runs only when the window advances.
        /// <paramref name="windowIndex"/> is the resolved synodic window (or -1 before the first window).
        /// </summary>
        internal bool TryResolveWindowSegments(
            string memberId,
            IReadOnlyList<OrbitSegment> memberSegments,
            ReaimMissionPlan plan,
            ReaimWindowPlanner.ReaimWindowSchedule schedule,
            double unitPhaseAnchorUT, double unitSpanStartUT, double unitSpanEndUT, double unitCadenceSeconds,
            double currentUT,
            out List<OrbitSegment> segments, out long windowIndex)
        {
            segments = null;
            windowIndex = -1;
            if (!plan.Supported || !schedule.Valid || string.IsNullOrEmpty(memberId))
                return false;

            // Cheap pre-check: a member with no heliocentric leg in the transfer window (a launch /
            // arrival / debris leg of a chained mission) has nothing to re-aim - skip the Lambert solve
            // entirely and keep its faithful body-relative segments.
            if (!ReaimSegmentAssembler.HasHeliocentricLegInWindow(
                    memberSegments, plan.CommonAncestor, plan.RecordedDepartureUT, plan.RecordedArrivalUT))
                return false;

            // Map the live clock to the synodic window index using the SAME span-clock the engine uses
            // (schedule == null => uniform cadence path; cadence is the synodic period set by the builder).
            if (!GhostPlaybackLogic.TryComputeSpanLoopUT(
                    currentUT, unitPhaseAnchorUT, unitSpanStartUT, unitSpanEndUT, unitCadenceSeconds,
                    out double _, out long cycleIndex, out bool _, schedule: null))
            {
                // Parked before the first window: nothing renders yet, keep the faithful surface.
                return false;
            }
            windowIndex = cycleIndex;

            if (!cacheByMember.TryGetValue(memberId, out CacheEntry entry)
                || !entry.Resolved || entry.Window != cycleIndex)
            {
                List<OrbitSegment> built = BuildWindowSegments(
                    memberId, memberSegments, plan, schedule, cycleIndex, out double captureShiftSeconds);
                entry = new CacheEntry
                {
                    Window = cycleIndex,
                    Resolved = true,
                    Segments = built,
                    CaptureShiftSeconds = captureShiftSeconds,
                    RecordedArrivalUT = plan.RecordedArrivalUT
                };
                cacheByMember[memberId] = entry;
            }

            if (entry.Segments == null)
                return false; // window miss (cached) => faithful
            segments = entry.Segments;
            return true;
        }

        // Synthesizes the window transfer and replaces the member's heliocentric leg with it. Returns null
        // (=> faithful) on any synthesis/replacement failure. One-shot per window (cached), so logs at Verbose.
        private static List<OrbitSegment> BuildWindowSegments(
            string memberId, IReadOnlyList<OrbitSegment> memberSegments, ReaimMissionPlan plan,
            ReaimWindowPlanner.ReaimWindowSchedule schedule, long windowIndex,
            out double captureShiftSeconds)
        {
            var ic = CultureInfo.InvariantCulture;
            // Default 0 for every fail/decline/miss return below (definite-assignment-safe); set on the
            // success path so ResolveForFrame can re-time the in-capture PartEvents by the SAME shift the
            // capture OrbitSegment moved (review FIX #2).
            captureShiftSeconds = 0.0;
            double nominalDepartureUT = schedule.DepartureUTForWindow(windowIndex);

            CelestialBody launchBody = FindBody(plan.LaunchBody);
            CelestialBody targetBody = FindBody(plan.TargetBody);
            if (launchBody == null || targetBody == null)
            {
                ParsekLog.Warn("ReaimPlayback",
                    $"member={memberId} window={windowIndex} cannot resolve bodies launch='{plan.LaunchBody}' target='{plan.TargetBody}' - faithful");
                return null;
            }

            // Adapt to the RECORDED transfer's direction (handedness) instead of forcing prograde: find the
            // recorded heliocentric leg's inclination (> 90 deg => the recorded transfer was retrograde) and
            // require the synthesized transfer to match it. With the synth's ecliptic plane constraint the
            // synthesized transfer is coplanar-prograde, so a recorded-prograde mission matches at the
            // nominal departure; a recorded-retrograde mission asks the synth for the retrograde branch.
            double recordedInc = RecordedHeliocentricInclination(
                memberSegments, plan.CommonAncestor, plan.RecordedDepartureUT, plan.RecordedArrivalUT);
            bool recordedRetrograde = ReaimTransferSynthesizer.IsRetrogradeTransfer(recordedInc);
            bool progradeWanted = !recordedRetrograde;

            // Anchor the departure to the loop's NOMINAL replay time of the recorded departure (D_k =
            // schedule.DepartureUTForWindow). The loop replays the recorded departure EXACTLY at D_k, so
            // pinning the transfer's departure there places its near-launch point on the LIVE launch body at
            // the instant the ghost departs (the transfer line touches Kerbin). The synodic window lands on
            // a ~180-degree transfer angle (the single-rev Lambert degeneracy); an earlier pass searched the
            // DEPARTURE around D_k to dodge it, but that desynced the transfer from D_k - the loop still
            // replayed the departure at D_k while the geometry was aimed at the launch body's position days
            // later, so the heliocentric segment hung in front of Kerbin (the playtest "transfer far from /
            // in front of Kerbin" regression). Keep the departure FIXED at D_k and search the TIME OF FLIGHT
            // instead: a small tof step slides the TARGET off the antipodal 180-degree point while the launch
            // endpoint (and the perigee) stay put. With the plane constraint the nominal departure + recorded
            // tof converges for almost every window (step 0), so the tof search only nudges the rare exactly-
            // 180-degree window; the sub-pixel target-end drift it introduces is acceptable, the departure
            // staying glued to the launch body is what matters. Cached per window (runs once per advance).
            double departureUT = nominalDepartureUT;

            // Stage B (docs/dev/plans/reaim-eccentric-tof-reliability.md section 4.1): the candidate tof list
            // is built by the pure ReaimTofSearch helper. Step 0 is ALWAYS the RECORDED tof (schedule.TofSeconds),
            // probed first and unchanged - zero regression for every window that resolves today. After the
            // recorded-centered +-6% base band, an eccentricity-gated, bounded expansion extends the search
            // toward the GEOMETRIC Hohmann time (geomTof) so an eccentric target (Eeloo/Moho), whose
            // geometrically-required tof drifts out of the recorded +-6% band each synodic window, can still
            // resolve. The bodies + parent are in hand past the null-guard above, so the geometric inputs need
            // no new plumbing. B-minimal (per-mission constant): geomTof from the launch/target SMAs + parent mu.
            double geomTofSeconds = TransferWindowMath.HohmannTransferTimeSeconds(
                launchBody.orbit.semiMajorAxis, targetBody.orbit.semiMajorAxis,
                launchBody.referenceBody != null ? launchBody.referenceBody.gravParameter : double.NaN);
            double eTarget = targetBody.orbit.eccentricity;
            double halfWidthFraction = ReaimTofSearch.HalfWidthFraction(eTarget);

            // DEPARTURE-ANCHOR DECISION (moved BEFORE the tof loop so r1 is fixed for every candidate).
            // For a heliocentric-PARKING departure the icon coasts on the heliocentric PARK at the burn, so
            // the transfer must emanate from the vessel's re-phased PARK-END state, not the launch body's
            // center. Resolve WHICH segment is the park (the SAME predicate the inc guard uses, via
            // FindHeliocentricParkSegment) + the pure mode decision, then reconstruct the LAN-rotated park
            // Sun orbit and evaluate r1 / vel at the RECORDED burn UT (BLOCKER 1: the rendered park-end the
            // icon abuts is the LAN-rotated park at RecordedDepartureUT - the park is only LAN-rotated, NOT
            // ShiftInTime'd, so it keeps its recorded epoch; evaluating at the future synodic departure D0
            // would advance it N*synodic revs and re-open the seam).
            //
            // FAIL-CLOSED ORDER: a parking departure whose re-phase DECLINES returns faithful (null) HERE,
            // BEFORE any synthesis - it must NEVER fall back to a launch-center r1 for a parking departure
            // (that would render the original seam disguised as a fix). A direct transfer takes the
            // launch-center path with hasDepartureOverride=false => byte-identical to main.
            double parkReplayUT = schedule.RelaunchUTForWindow(windowIndex);
            // Walk the member's segments ONCE: FindHeliocentricParkSegment is the single source for both the
            // inc guard (DecideDepartureAnchor) and the r1 reconstruction below, so resolve the park segment
            // here and reuse the same Nullable instead of also calling FindHeliocentricParkInclination (which
            // would re-walk the list internally).
            OrbitSegment? parkSeg = ReaimSegmentAssembler.FindHeliocentricParkSegment(
                memberSegments, plan.CommonAncestor, plan.RecordedDepartureUT);
            double parkInc = parkSeg.HasValue ? parkSeg.Value.inclination : double.NaN;
            ReaimSegmentAssembler.DepartureAnchorDecision anchor =
                ReaimSegmentAssembler.DecideDepartureAnchor(
                    plan.DepartedFromHeliocentricPark, parkInc, parkReplayUT, plan.RecordedDepartureUT,
                    launchBody.orbit != null ? launchBody.orbit.period : double.NaN);

            if (anchor.Mode == ReaimSegmentAssembler.DepartureAnchorMode.DeclineToFaithful)
            {
                ParsekLog.Verbose("ReaimPlayback",
                    $"member={memberId} window={windowIndex} park re-phase DECLINED -> faithful " +
                    $"(parkInc={parkInc.ToString("F2", ic)} maxInc={ReaimSegmentAssembler.ParkRephaseMaxInclinationDeg.ToString("F1", ic)} " +
                    $"launchPeriod={(launchBody.orbit != null ? launchBody.orbit.period.ToString("R", ic) : "NaN")})");
                return null; // fail closed to faithful; never the verbatim ~239 deg park, never launch-center r1
            }

            double parkDeltaLonDeg = anchor.ParkDeltaLonDeg;
            bool hasDepartureOverride = anchor.Mode == ReaimSegmentAssembler.DepartureAnchorMode.ParkEndOverride;
            Vector3d parkEndPosUnswizzled = default(Vector3d);
            Vector3d parkEndVelUnswizzled = default(Vector3d);
            double parkEndOffLaunchCenter = double.NaN; // |parkEndPos - launchBody center| (window-0 proof)
            if (hasDepartureOverride)
            {
                // Reconstruct the recorded park (the same Nullable resolved above), LAN-rotate it by the
                // window's re-phase angle (same RotateLanForParkRephase the assembler applies to the rendered
                // park leg), then evaluate r1 / vel on it at the RECORDED burn UT. getRelativePositionAtUT/.xzy
                // match the synth frame (NEVER getPositionAtUT - that adds the Sun world offset and breaks the
                // round-trip with UpdateFromStateVectors). Relies on the Sun being the root body (the
                // common-ancestor leg).
                if (!parkSeg.HasValue)
                {
                    // The mode decision said ParkEndOverride (inc was finite) but the segment vanished -
                    // structural inconsistency; fail closed rather than silently anchoring on the launch center.
                    ParsekLog.Warn("ReaimPlayback",
                        $"member={memberId} window={windowIndex} park-end override requested but no park segment resolved - faithful this window");
                    return null;
                }
                OrbitSegment rotatedPark = ReaimSegmentAssembler.RotateLanForParkRephase(
                    parkSeg.Value, parkDeltaLonDeg);
                if (!TryBuildParkEndState(rotatedPark, anchor.ParkEvalUT, launchBody,
                        out parkEndPosUnswizzled, out parkEndVelUnswizzled, out parkEndOffLaunchCenter))
                {
                    ParsekLog.Warn("ReaimPlayback",
                        $"member={memberId} window={windowIndex} park-end state reconstruction failed (body/elements) - faithful this window");
                    return null;
                }
            }

            // The tof candidate list is GATED on the parking path (F2). DIRECT transfer
            // (hasDepartureOverride == false): byte-identical to main - the recorded-tof-centered band
            // (BuildCandidateTofs). PARKING departure (hasDepartureOverride == true): the recorded tof is whatever
            // the player flew (s15 ~1.44x Hohmann) and forcing it from the re-phased park-end makes a degenerate
            // conic, so search around the GEOMETRIC Hohmann time instead (BuildParkingCandidateTofs, step 0 =
            // geomTof). geomTofSeconds/eTarget are already computed above; the park-vs-Kerbin sma is ~2%, inside
            // the band, so launchBody.orbit.semiMajorAxis is the right radius for the Hohmann time.
            IReadOnlyList<double> candidateTofs = hasDepartureOverride
                ? ReaimTofSearch.BuildParkingCandidateTofs(geomTofSeconds, eTarget)
                : ReaimTofSearch.BuildCandidateTofs(schedule.TofSeconds, geomTofSeconds, eTarget);

            Orbit transferOrbit = null;
            double soiEntryUT = double.NaN;
            CelestialBody encounterBody = null;
            double usedTofSeconds = double.NaN;
            string failReason = null;
            for (int c = 0; c < candidateTofs.Count; c++)
            {
                double tof = candidateTofs[c];
                // ReaimTofSearch drops non-positive tofs already; keep the positivity guard as a backstop.
                if (tof > 0.0 && ReaimTransferSynthesizer.TrySynthesizeTransfer(
                        launchBody, targetBody, departureUT, tof, progradeWanted,
                        out transferOrbit, out soiEntryUT, out encounterBody, out failReason,
                        parkEndPosUnswizzled, parkEndVelUnswizzled, hasDepartureOverride))
                {
                    usedTofSeconds = tof;
                    break;
                }
            }
            if (transferOrbit == null)
            {
                ParsekLog.Verbose("ReaimPlayback",
                    $"member={memberId} window={windowIndex} departUT={nominalDepartureUT.ToString("R", ic)} " +
                    $"synth failed across {candidateTofs.Count} tof candidates (recordedTof={schedule.TofSeconds.ToString("R", ic)} " +
                    $"geomTof={geomTofSeconds.ToString("R", ic)} eTarget={eTarget.ToString("F4", ic)} halfWidthFraction={halfWidthFraction.ToString("F4", ic)}) " +
                    $"({failReason}) - faithful this window");
                return null;
            }

            double shift = plan.RecordedDepartureUT - departureUT;

            OrbitSegment transferSeg = ReaimOrbitSegmentConverter.ToSegment(transferOrbit, plan.CommonAncestor);
            // Shift the transfer's epoch from the absolute departure into recorded-span time so the
            // segment's phase matches the recorded-span playback clock: at recorded-span
            // RecordedDepartureUT the orbit sits where it was at absolute departureUT.
            transferSeg = ReaimSegmentAssembler.ShiftInTime(transferSeg, shift);

            // Heliocentric-PARK re-phase (departure-side render). A two-burn departure escapes into a
            // Sun-inertial PARKING orbit BEFORE the trans-target burn. That park renders verbatim at its
            // RECORDED solar longitude (~239 deg off live Kerbin at the captured window) while the escape leg
            // (body-relative) anchors to the LIVE launch body (KSP Orbit.getPositionAtUT adds the live
            // referenceBody.position) - a teleport seam right at SOI exit. parkDeltaLonDeg (decided ABOVE
            // via DecideDepartureAnchor, BEFORE the tof loop) rotates the recorded park's LAN by
            // Delta_lon(window) = omega_parent * (replayUT - RecordedDepartureUT) so the Sun-inertial park
            // re-phases into the live frame and sits next to the live launch body, connecting to the escape.
            // The SAME rotated park supplies the transfer's r1 departure anchor (the override above), so the
            // park-end and the transfer-start are the SAME point and the icon traverses park -> transfer
            // continuously.
            //
            // CLOCK CHOICE (the issue-1 fix): replayUT is the CADENCE-clock relaunch time
            // (schedule.RelaunchUTForWindow = D0 + window*cadence), NOT the synodic departure
            // (DepartureUTForWindow = D0 + window*synodic). The loop ENGINE relaunches the ghost every
            // CADENCE = max(span, synodic); the body-relative escape leg therefore follows the launch body at
            // its position D0 + window*cadence, so the park must re-phase to the SAME time to meet it. When
            // cadence == synodic (synodic > span, the normal interplanetary case) the two clocks coincide and
            // this is byte-identical to the synodic departure. They DIVERGE only when the recorded span
            // exceeds the synodic (a mission longer than its own transfer window, e.g. Kerbal X #2): there the
            // synodic-clock re-phase drifts the park ~142 deg*window off the live launch body, which is the
            // distant-loiter teleport. Pinning the park to the cadence clock keeps the loiter on the live
            // launch body at every window. (The TRANSFER's ARRIVAL still rides the synodic clock so it aims
            // at the target's true position; the residual transfer->target seam in the span>synodic case is
            // the separate Increment-2 overlap work, out of scope for this departure-seam fix.)

            // RENDER SPAN + CAPTURE RE-TIME (F2, parking path only). The parking-departure transfer now uses
            // the GEOMETRIC Hohmann tof (much shorter than the recorded tof), so it ARRIVES earlier than the
            // recorded arrival. Two coupled adjustments, both GATED on hasDepartureOverride:
            //   (ii) render span = the TRUE transfer duration [RecordedDepartureUT, RecordedDepartureUT +
            //        usedTof]. The launch side stays at RecordedDepartureUT (the recorded escape ends exactly
            //        at the burn UT - no launch-side gap); the arrival side trims to the new (earlier) arrival
            //        so the transfer line does not over-run to the stale recorded arrival. ReplaceHeliocentricLeg
            //        clamps these into [recordedDeparture, recordedArrival] and falls back to the full leg if
            //        invalid, so a degenerate usedTof cannot mis-render.
            //   (iii) capture re-time: the recorded Duna capture leg, recorded to begin at RecordedArrivalUT,
            //        must shift back by captureShift = newArrivalUT - RecordedArrivalUT (negative) so it begins
            //        where the transfer now ends (seam closes). ShiftInTime moves startUT/endUT/epoch together.
            //
            // DIRECT path (hasDepartureOverride == false): renderStart/End stay NaN (full-leg, byte-identical)
            // and captureShift/targetBody are 0/null (no-op) - exactly as on main. The full-recorded-span
            // rationale below still applies to the direct path (the recorded escape/capture cover inside the SOI).
            //
            // Full-recorded-span (direct) rationale: the synthesized transfer is center-to-center, so its
            // first/last stretches sit inside the launch/target SOI at the body center; the recorded escape /
            // capture legs cover inside the SOI and the brief in-SOI stub sits sub-pixel at map scale. An
            // earlier pass trimmed the LAUNCH side to the synthesized SOI-exit UT and opened a gap right after
            // Kerbin SOI exit - the parking path here trims only the ARRIVAL side (the launch side stays at the
            // burn UT, where the recorded escape ends), so it does not reintroduce that launch-side gap.
            //
            // COUPLING GUARD (review FIX #1): the render-end and the captureShift MUST be derived from the SAME
            // arrival UT, or they desync into a real GAP. BuildParkingCandidateTofs allows usedTof up to the
            // band cap (~1.20*geomTof), so a near-Hohmann two-burn (or an eccentric target where the cap binds)
            // can yield usedTof > recordedTof => newArrivalUT > RecordedArrivalUT. If we passed that raw forward
            // arrival, ReplaceHeliocentricLeg's render clamp (renderEnd > recordedArrival) would silently fall
            // back to the FULL recorded span while captureShift stayed POSITIVE and ShiftInTime'd the capture
            // FORWARD - opening a gap of (newArrivalUT - RecordedArrivalUT) between the (full-span) transfer end
            // and the shifted capture start. Clamp newArrivalUT to <= RecordedArrivalUT FIRST and feed the
            // clamped value to BOTH: usedTof > recordedTof then degrades cleanly to render-full-span +
            // captureShift 0 (no gap, no forward shift); the s15 case (usedTof < recordedTof) is unchanged
            // (newArrivalUT < RecordedArrivalUT, so the clamp is a no-op and the capture re-times earlier).
            double rawArrivalUT = plan.RecordedDepartureUT + usedTofSeconds;
            double newArrivalUT = rawArrivalUT < plan.RecordedArrivalUT ? rawArrivalUT : plan.RecordedArrivalUT;
            double captureShift = hasDepartureOverride ? (newArrivalUT - plan.RecordedArrivalUT) : 0.0;
            double renderStartUT = hasDepartureOverride ? plan.RecordedDepartureUT : double.NaN;
            double renderEndUT = hasDepartureOverride ? newArrivalUT : double.NaN;
            List<OrbitSegment> assembled = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                memberSegments, transferSeg, plan.CommonAncestor,
                plan.RecordedDepartureUT, plan.RecordedArrivalUT,
                renderStartUT, renderEndUT, parkDeltaLonDeg,
                captureShift, hasDepartureOverride ? plan.TargetBody : null);
            if (assembled == null || assembled.Count == 0)
            {
                ParsekLog.Warn("ReaimPlayback",
                    $"member={memberId} window={windowIndex} heliocentric-leg replace returned empty - faithful this window");
                return null;
            }

            // usedTof deviation from BOTH centers (recorded tof = the direct-path band center / step 0; geomTof
            // = the parking-path band center and the geometric Hohmann time) so an "Eeloo declined / drifted"
            // report can be diagnosed straight from KSP.log. On the PARKING path the tof is centered on geomTof
            // (F2), so devFromGeom should be ~0 and the recorded tof is NOT used for the band - the log carries
            // both deltas regardless so the regression is visible either way. NaN-safe (geomTof can be NaN if
            // the parent mu/SMAs are degenerate).
            double devFromRecorded = usedTofSeconds - schedule.TofSeconds;
            double devFromGeom = usedTofSeconds - geomTofSeconds;
            ParsekLog.Verbose("ReaimPlayback",
                $"member={memberId} window={windowIndex} re-aimed transfer ready: departUT={departureUT.ToString("R", ic)} (nominal D_k) " +
                $"tof={usedTofSeconds.ToString("R", ic)} " +
                (hasDepartureOverride
                    ? $"(geom={geomTofSeconds.ToString("R", ic)} [parking band center; recorded NOT used] "
                    : $"(recorded={schedule.TofSeconds.ToString("R", ic)} geom={geomTofSeconds.ToString("R", ic)} ") +
                $"eTarget={eTarget.ToString("F4", ic)} halfWidthFraction={halfWidthFraction.ToString("F4", ic)} " +
                $"devFromRecorded={devFromRecorded.ToString("R", ic)}s devFromGeom={devFromGeom.ToString("R", ic)}s) " +
                $"soiEntryUT={soiEntryUT.ToString("R", ic)} " +
                $"encounter={(encounterBody != null ? encounterBody.bodyName : "<none>")} segs={assembled.Count} " +
                $"departAnchor={(hasDepartureOverride ? "parkEnd" : "launchCenter")} " +
                (hasDepartureOverride
                    ? $"parkEndOffLaunchCenter={parkEndOffLaunchCenter.ToString("R", ic)}m (r1 moved off {launchBody.bodyName}; evalUT={anchor.ParkEvalUT.ToString("R", ic)}=RecordedDepartureUT) "
                    : "") +
                $"parkDeltaLon={parkDeltaLonDeg.ToString("R", ic)}deg (parking={plan.DepartedFromHeliocentricPark} " +
                $"parkReplayUT={parkReplayUT.ToString("R", ic)} cadence={schedule.CadenceSeconds.ToString("R", ic)} synodic={schedule.SynodicPeriodSeconds.ToString("R", ic)}) " +
                (hasDepartureOverride
                    ? $"renderSpan=[{plan.RecordedDepartureUT.ToString("R", ic)},{newArrivalUT.ToString("R", ic)}] (transfer-duration) " +
                      $"captureShift={captureShift.ToString("R", ic)}s (recordedArrival={plan.RecordedArrivalUT.ToString("R", ic)} -> newArrival={newArrivalUT.ToString("R", ic)})"
                    : $"renderSpan=full-recorded=[{plan.RecordedDepartureUT.ToString("R", ic)},{plan.RecordedArrivalUT.ToString("R", ic)}]"));
            captureShiftSeconds = captureShift; // success: the capture leg moved by this; PartEvents re-time the same
            return assembled;
        }

        // The recorded heliocentric (common-ancestor) leg's inclination within the transfer window, or NaN
        // when the member has none. Used to match the synthesized transfer's handedness to the recorded
        // mission's (the re-aim adapts to what was recorded; it does not force prograde). Same window
        // predicate as ReaimSegmentAssembler.ReplaceHeliocentricLeg / HasHeliocentricLegInWindow.
        private static double RecordedHeliocentricInclination(
            IReadOnlyList<OrbitSegment> memberSegments, string commonAncestor,
            double recordedDepartureUT, double recordedArrivalUT)
        {
            if (memberSegments == null || string.IsNullOrEmpty(commonAncestor))
                return double.NaN;
            for (int i = 0; i < memberSegments.Count; i++)
            {
                OrbitSegment s = memberSegments[i];
                if (!s.isPredicted && s.bodyName == commonAncestor
                    && s.startUT < recordedArrivalUT && s.endUT > recordedDepartureUT)
                    return s.inclination;
            }
            return double.NaN;
        }

        // Live (Unity-bound) reconstruction of the LAN-rotated park-end state used as the transfer's r1
        // departure anchor. Rebuilds the Sun orbit from the rotated park's Kepler elements with the SAME
        // new Orbit(inc, ecc, sma, LAN, argPe, mEp, epoch, body) field-order/units contract used in
        // TrajectoryMath / ReaimOrbitSegmentConverter (inc/LAN/argPe degrees, mEp radians, epoch UT), then
        // evaluates getRelativePositionAtUT / getOrbitalVelocityAtUT at evalUT (== RecordedDepartureUT) and
        // un-swizzles with .xzy to match the synth's r1/r2 Lambert frame. NEVER getPositionAtUT (that adds
        // the Sun world offset and breaks the round-trip with UpdateFromStateVectors). Relies on the
        // common-ancestor body (the Sun) being the root. Returns false on a missing body / NaN result.
        // offLaunchCenter = |parkEndPos - launchBody center| in the same .xzy frame (the window-0 proof that
        // r1 actually moved off the launch body's center).
        private static bool TryBuildParkEndState(
            OrbitSegment rotatedPark, double evalUT, CelestialBody launchBody,
            out Vector3d parkEndPosUnswizzled, out Vector3d parkEndVelUnswizzled, out double offLaunchCenter)
        {
            parkEndPosUnswizzled = default(Vector3d);
            parkEndVelUnswizzled = default(Vector3d);
            offLaunchCenter = double.NaN;

            CelestialBody parent = FindBody(rotatedPark.bodyName);
            if (parent == null)
                return false;
            try
            {
                Orbit park = new Orbit(
                    rotatedPark.inclination, rotatedPark.eccentricity, rotatedPark.semiMajorAxis,
                    rotatedPark.longitudeOfAscendingNode, rotatedPark.argumentOfPeriapsis,
                    rotatedPark.meanAnomalyAtEpoch, rotatedPark.epoch, parent);
                Vector3d pos = park.getRelativePositionAtUT(evalUT).xzy;
                Vector3d vel = park.getOrbitalVelocityAtUT(evalUT).xzy;
                if (double.IsNaN(pos.x) || double.IsNaN(pos.y) || double.IsNaN(pos.z)
                    || double.IsNaN(vel.x) || double.IsNaN(vel.y) || double.IsNaN(vel.z))
                    return false;
                parkEndPosUnswizzled = pos;
                parkEndVelUnswizzled = vel;
                if (launchBody != null && launchBody.orbit != null)
                    offLaunchCenter = (pos - launchBody.orbit.getRelativePositionAtUT(evalUT).xzy).magnitude;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static CelestialBody FindBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName) || FlightGlobals.Bodies == null)
                return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody b = FlightGlobals.Bodies[i];
                if (b != null && b.bodyName == bodyName)
                    return b;
            }
            return null;
        }
    }
}
