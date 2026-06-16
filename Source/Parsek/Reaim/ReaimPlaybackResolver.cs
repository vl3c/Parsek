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
                return new ReaimedTrajectory(inner, segments);
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
                entry = new CacheEntry
                {
                    Window = cycleIndex,
                    Resolved = true,
                    Segments = BuildWindowSegments(memberId, memberSegments, plan, schedule, cycleIndex)
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
            ReaimWindowPlanner.ReaimWindowSchedule schedule, long windowIndex)
        {
            var ic = CultureInfo.InvariantCulture;
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
            IReadOnlyList<double> candidateTofs = ReaimTofSearch.BuildCandidateTofs(
                schedule.TofSeconds, geomTofSeconds, eTarget);

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
                        out transferOrbit, out soiEntryUT, out encounterBody, out failReason))
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

            // Heliocentric-PARK re-phase (Increment 1, departure-side render). A two-burn departure escapes
            // into a Sun-inertial PARKING orbit BEFORE the trans-target burn. That park renders verbatim at
            // its RECORDED solar longitude (~239 deg off live Kerbin at the captured window) while the
            // escape leg (body-relative) and the re-aimed transfer (synthesized at D_k) anchor to LIVE
            // Kerbin - a teleport seam. Rotate the recorded park's LAN by Delta_lon(window) = omega_parent *
            // (D_k - RecordedDepartureUT) so it re-phases into the live frame and connects to the escape +
            // transfer. Gated on DepartedFromHeliocentricPark (a direct transfer has it false => 0 => byte-
            // identical) + a near-equatorial guard; FAILS CLOSED (decline this window to faithful, NEVER the
            // verbatim 239 deg park) on a non-equatorial park or a degenerate launch-body period. NOTE
            // (Increment 1): windowIndex here is the SPAN-clock window (the unit is still single-instance),
            // so park + transfer share the same window and connect, and window 0 renders correct; the
            // span-vs-synodic window drift for later windows is what the Increment-2 overlap build fixes.
            double parkDeltaLonDeg = 0.0;
            if (plan.DepartedFromHeliocentricPark)
            {
                double launchPeriod = launchBody.orbit != null ? launchBody.orbit.period : double.NaN;
                double parkInc = ReaimSegmentAssembler.FindHeliocentricParkInclination(
                    memberSegments, plan.CommonAncestor, plan.RecordedDepartureUT);
                if (!ReaimSegmentAssembler.TryComputeParkRephase(
                        parkInc, nominalDepartureUT, plan.RecordedDepartureUT, launchPeriod,
                        out parkDeltaLonDeg))
                {
                    ParsekLog.Verbose("ReaimPlayback",
                        $"member={memberId} window={windowIndex} park re-phase DECLINED -> faithful " +
                        $"(parkInc={parkInc.ToString("F2", ic)} maxInc={ReaimSegmentAssembler.ParkRephaseMaxInclinationDeg.ToString("F1", ic)} " +
                        $"launchPeriod={launchPeriod.ToString("R", ic)})");
                    return null; // fail closed to faithful; never the verbatim ~239 deg park
                }
            }

            // Render the re-aimed transfer over the FULL recorded heliocentric span
            // [RecordedDepartureUT, RecordedArrivalUT] (NaN render bounds => no trim). That span is exactly
            // where the member's recorded launch-escape leg ENDS and its recorded capture leg BEGINS, so the
            // re-aimed transfer hands off seamlessly from the recorded escape - no gap. An earlier pass
            // trimmed the launch side to the SYNTHESIZED center-to-center transfer's launch-SOI-exit UT,
            // which lands ~0.9 day LATER (in recorded-span time) than where the recorded escape ends: that
            // opened a gap right after the launch SOI exit where the orbit ghost was destroyed
            // (gap-between-orbit-segments) and the transfer line restarted displaced by the launch body's own
            // motion across the gap (the "transfer is in the wrong place right after Kerbin SOI exit"
            // regression). Full-span render restores the seamless handoff; the brief in-SOI stub sits
            // sub-pixel at the body centre at map scale, and the recorded escape / capture legs cover inside
            // the SOI. parkDeltaLonDeg re-phases the recorded park (departure-side) into the live frame.
            // Gate ONLY the assembler call on the reaimChainSynthesis flag (default OFF -> identity path).
            // The flag must not gate the Supported / no-helio-leg / parked guards, the faithful/declined
            // return, or the ReferenceEquals(effective, recorded) no-op contract above; it only chooses
            // between today's single-leg replacement (OFF) and the P3 chain synthesis (ON, currently a
            // placeholder returning the same result). The #1167 parkDeltaLonDeg re-phase is threaded
            // through AssembleWindowChain to ReplaceHeliocentricLeg unchanged. See ReaimChainSynthesis.
            List<OrbitSegment> assembled = ReaimSegmentAssembler.AssembleWindowChain(
                ReaimChainSynthesis.IsEnabled,
                memberSegments, transferSeg, plan.CommonAncestor,
                plan.RecordedDepartureUT, plan.RecordedArrivalUT,
                double.NaN, double.NaN, parkDeltaLonDeg);
            if (assembled == null || assembled.Count == 0)
            {
                ParsekLog.Warn("ReaimPlayback",
                    $"member={memberId} window={windowIndex} heliocentric-leg replace returned empty - faithful this window");
                return null;
            }

            // usedTof deviation from BOTH centers (recorded tof = the band center / step 0; geomTof = the
            // geometric Hohmann time the ecc band reaches toward) so an "Eeloo declined / drifted" report can
            // be diagnosed straight from KSP.log: which center the resolved tof landed near tells whether the
            // ecc band did the work. NaN-safe (geomTof can be NaN if the parent mu/SMAs are degenerate).
            double devFromRecorded = usedTofSeconds - schedule.TofSeconds;
            double devFromGeom = usedTofSeconds - geomTofSeconds;
            ParsekLog.Verbose("ReaimPlayback",
                $"member={memberId} window={windowIndex} re-aimed transfer ready: departUT={departureUT.ToString("R", ic)} (nominal D_k) " +
                $"tof={usedTofSeconds.ToString("R", ic)} (recorded={schedule.TofSeconds.ToString("R", ic)} geom={geomTofSeconds.ToString("R", ic)} " +
                $"eTarget={eTarget.ToString("F4", ic)} halfWidthFraction={halfWidthFraction.ToString("F4", ic)} " +
                $"devFromRecorded={devFromRecorded.ToString("R", ic)}s devFromGeom={devFromGeom.ToString("R", ic)}s) " +
                $"soiEntryUT={soiEntryUT.ToString("R", ic)} " +
                $"encounter={(encounterBody != null ? encounterBody.bodyName : "<none>")} segs={assembled.Count} " +
                $"parkDeltaLon={parkDeltaLonDeg.ToString("R", ic)}deg (parking={plan.DepartedFromHeliocentricPark}) " +
                $"renderSpan=full-recorded=[{plan.RecordedDepartureUT.ToString("R", ic)},{plan.RecordedArrivalUT.ToString("R", ic)}]");
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
