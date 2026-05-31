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

        // Last arrival-seam rotation angle (degrees) applied per member, for the per-frame seam diagnostic
        // in ParsekFlight.LogReaimGhostTrace (NaN = no rotation applied this window / faithful). Diagnostic
        // only; not part of the playback contract.
        private readonly Dictionary<string, double> lastArrivalSeamRAngleDeg = new Dictionary<string, double>();

        /// <summary>Drops all cached window adapters (call when the committed set / missions change so a
        /// stale window adapter cannot survive a recording edit).</summary>
        internal void Clear()
        {
            cacheByMember.Clear();
            lastArrivalSeamRAngleDeg.Clear();
        }

        /// <summary>The last arrival-seam rotation angle (degrees) applied for <paramref name="memberId"/>,
        /// or NaN when none was applied / the member is unknown. Diagnostic read (ParsekFlight seam log).</summary>
        internal double GetLastArrivalSeamRAngleDeg(string memberId)
        {
            if (!string.IsNullOrEmpty(memberId)
                && lastArrivalSeamRAngleDeg.TryGetValue(memberId, out double deg))
                return deg;
            return double.NaN;
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
                    Segments = BuildWindowSegments(memberId, memberSegments, plan, schedule, cycleIndex,
                        out double appliedRAngleDeg)
                };
                cacheByMember[memberId] = entry;
                lastArrivalSeamRAngleDeg[memberId] = appliedRAngleDeg;
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
            out double appliedRAngleDeg)
        {
            appliedRAngleDeg = double.NaN; // set when the arrival seam is actually rotated this window
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
            const double TofSearchStepFraction = 0.005; // of the recorded tof (~0.4 day for Kerbin->Duna)
            const int SearchMaxSteps = 12;              // +-6% of the recorded tof
            double tofStep = schedule.TofSeconds * TofSearchStepFraction;
            double departureUT = nominalDepartureUT;

            Orbit transferOrbit = null;
            double soiEntryUT = double.NaN;
            CelestialBody encounterBody = null;
            double usedTofSeconds = double.NaN;
            string failReason = null;
            for (int s = 0; s <= SearchMaxSteps && transferOrbit == null; s++)
            {
                double[] tofs = s == 0
                    ? new[] { schedule.TofSeconds }
                    : new[] { schedule.TofSeconds + s * tofStep, schedule.TofSeconds - s * tofStep };
                for (int t = 0; t < tofs.Length; t++)
                {
                    if (tofs[t] > 0.0 && ReaimTransferSynthesizer.TrySynthesizeTransfer(
                            launchBody, targetBody, departureUT, tofs[t], progradeWanted,
                            out transferOrbit, out soiEntryUT, out encounterBody, out failReason))
                    {
                        usedTofSeconds = tofs[t];
                        break;
                    }
                }
            }
            if (transferOrbit == null)
            {
                ParsekLog.Verbose("ReaimPlayback",
                    $"member={memberId} window={windowIndex} departUT={nominalDepartureUT.ToString("R", ic)} " +
                    $"synth failed across tof +-{(SearchMaxSteps * tofStep).ToString("F0", ic)}s search ({failReason}) - faithful this window");
                return null;
            }

            double shift = plan.RecordedDepartureUT - departureUT;

            OrbitSegment transferSeg = ReaimOrbitSegmentConverter.ToSegment(transferOrbit, plan.CommonAncestor);
            // Shift the transfer's epoch from the absolute departure into recorded-span time so the
            // segment's phase matches the recorded-span playback clock: at recorded-span
            // RecordedDepartureUT the orbit sits where it was at absolute departureUT.
            transferSeg = ReaimSegmentAssembler.ShiftInTime(transferSeg, shift);

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
            // the SOI.
            List<OrbitSegment> assembled = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                memberSegments, transferSeg, plan.CommonAncestor,
                plan.RecordedDepartureUT, plan.RecordedArrivalUT,
                double.NaN, double.NaN);
            if (assembled == null || assembled.Count == 0)
            {
                ParsekLog.Warn("ReaimPlayback",
                    $"member={memberId} window={windowIndex} heliocentric-leg replace returned empty - faithful this window");
                return null;
            }

            // ARRIVAL-SEAM RESTITCH (S4, docs/dev/plans/reaim-arrival-seam-restitch.md). The recorded
            // target-body legs (approach hyperbola + capture + descent) keep their ORIGINAL recorded
            // approach orientation, so the re-aimed transfer reaches the target's SOI from the re-planned
            // direction and the recorded arrival is spliced on from a different bearing -> the ~1.37 Gm
            // jump-back-out. Rotate the recorded arrival sub-chain by a rigid Zup rotation R that maps the
            // recorded incoming frame (s_rec, h_rec) onto the re-aimed approach frame (s_re, h_re). Fail to
            // faithful (no rotation) on any degeneracy or a handedness flip - never rotate into a retrograde
            // join. Runs once per window (cached), so logs at Verbose.
            appliedRAngleDeg = RotateArrivalSeam(
                memberId, windowIndex, assembled, plan, targetBody, transferOrbit, soiEntryUT, shift);

            ParsekLog.Verbose("ReaimPlayback",
                $"member={memberId} window={windowIndex} re-aimed transfer ready: departUT={departureUT.ToString("R", ic)} (nominal D_k) " +
                $"tof={usedTofSeconds.ToString("R", ic)} (recorded={schedule.TofSeconds.ToString("R", ic)}) soiEntryUT={soiEntryUT.ToString("R", ic)} " +
                $"encounter={(encounterBody != null ? encounterBody.bodyName : "<none>")} segs={assembled.Count} " +
                $"renderSpan=full-recorded=[{plan.RecordedDepartureUT.ToString("R", ic)},{plan.RecordedArrivalUT.ToString("R", ic)}]");
            return assembled;
        }

        // Rotates the recorded arrival sub-chain (target-body approach + capture + descent segments) in the
        // assembled list so its incoming approach FRAME lines up with the re-aimed transfer's, closing the
        // gross arrival seam (docs/dev/plans/reaim-arrival-seam-restitch.md). Mutates `assembled` in place.
        // Fails to faithful (leaves the list untouched) on any degenerate geometry or a handedness flip.
        // After rotating, applies the 4.4 time-shift only when it is meaningfully non-zero (it is ~0 in the
        // nominal tof == recorded-tof case; see the in-method note). One-shot per window (cached), Verbose.
        // Returns the applied rotation angle in DEGREES (NaN when the arrival was left faithful), for the
        // per-frame seam log in ParsekFlight.LogReaimGhostTrace.
        private static double RotateArrivalSeam(
            string memberId, long windowIndex, List<OrbitSegment> assembled, ReaimMissionPlan plan,
            CelestialBody targetBody, Orbit transferOrbit, double soiEntryUT, double shift)
        {
            var ic = CultureInfo.InvariantCulture;

            // Recorded-side incoming frame from the recorded ArrivalLeg (hyperbolic approach about the
            // target). If the recorded arrival is already captured (ecc <= 1, no incoming asymptote) there
            // is no asymptote to match -> keep faithful.
            if (!ReaimElementRotation.TryRecordedArrivalFrame(
                    plan.ArrivalLeg, targetBody, out double[] sRec, out double[] hRec, out double recEcc))
            {
                ParsekLog.Verbose("ReaimSeam",
                    $"member={memberId} window={windowIndex} no recorded incoming asymptote " +
                    $"(arrival ecc={recEcc.ToString("F4", ic)} <= 1 or degenerate) - arrival NOT rotated (faithful)");
                return double.NaN;
            }

            // Re-aimed-side incoming frame from the synthesized transfer at the target-SOI entry.
            if (!ReaimElementRotation.TryReaimedArrivalFrame(
                    transferOrbit, targetBody, soiEntryUT, out double[] sRe, out double[] hRe, out double reEcc))
            {
                ParsekLog.Verbose("ReaimSeam",
                    $"member={memberId} window={windowIndex} no re-aimed incoming asymptote " +
                    $"(reaimed ecc={reEcc.ToString("F4", ic)} <= 1 or degenerate) - arrival NOT rotated (faithful)");
                return double.NaN;
            }

            // HANDEDNESS GUARD (plan 4.2): rotate only when the recorded capture and the re-aimed approach
            // share the prograde-normal sense. A negative dot means opposite handedness; rotating would flip
            // the orbit's travel direction (a retrograde join). Fall back to faithful (no rotation).
            double handednessDot = ReaimRotation.Dot(hRec, hRe);
            if (handednessDot <= 0.0)
            {
                ParsekLog.Verbose("ReaimSeam",
                    $"member={memberId} window={windowIndex} handedness dot={handednessDot.ToString("F4", ic)} <= 0 " +
                    "(recorded vs re-aimed plane normals opposed) - arrival NOT rotated (faithful, avoids retrograde join)");
                return double.NaN;
            }

            double[,] r = ReaimRotation.RotationFrameToFrame(sRec, hRec, sRe, hRe);
            if (r == null)
            {
                ParsekLog.Verbose("ReaimSeam",
                    $"member={memberId} window={windowIndex} rotation-frame build returned null (degenerate frame) - arrival NOT rotated (faithful)");
                return double.NaN;
            }

            int rotated = ReaimElementRotation.RotateBodyRelativeSegments(
                assembled, targetBody, plan.RecordedArrivalUT, r, out int skippedNonTarget);

            // TIME-SHIFT (plan 4.4): COMPUTED AND LOGGED, NOT APPLIED in v1. The recorded-span image of the
            // re-aimed handoff instant is soiEntryUT + shift; the recorded arrival sub-chain begins at
            // RecordedArrivalUT. In the validated playtest the window tof equalled the recorded tof, so
            // timeShift was ~0. Applying a non-zero shift in v1 would re-introduce a smaller seam: the
            // transfer leg always renders to RecordedArrivalUT (full-span), so moving the rotated arrival
            // sub-chain off RecordedArrivalUT (a negative shift starts it BEFORE the transfer end ->
            // overlap; a positive shift starts it after -> coverage gap) opens a discontinuity at the seam,
            // on an unexercised path. The rotation ALONE already closes the gross seam, and because it never
            // touched the segment UTs the arrival stays anchored at RecordedArrivalUT, contiguous with the
            // transfer end by construction. So v1 only LOGS the shift (a playtest diagnostic: tells us
            // whether a sub-tof window ever produces a materially non-zero value). Correct application
            // (moving the transfer render-END and the arrival start together to soiEntryUT + shift, which
            // requires relaxing the ReplaceHeliocentricLeg renderEnd clamp) is the fast-follow, done once a
            // playtest shows the shift is ever materially non-zero.
            double timeShift = ReaimSegmentAssembler.ComputeArrivalTimeShift(
                soiEntryUT, shift, plan.RecordedArrivalUT);

            double rAngleDeg = ReaimRotation.RotationAngleRadians(r) * 180.0 / System.Math.PI;
            ParsekLog.Verbose("ReaimSeam",
                $"member={memberId} window={windowIndex} arrival rotated: R-angle={rAngleDeg.ToString("F2", ic)}deg " +
                $"handednessDot={handednessDot.ToString("F4", ic)} recEcc={recEcc.ToString("F4", ic)} reEcc={reEcc.ToString("F4", ic)} " +
                $"segsRotated={rotated} skippedNonTarget={skippedNonTarget} " +
                $"timeShift={timeShift.ToString("F2", ic)}s (computed, not applied in v1)");
            // R angle reported to the per-frame seam diagnostic only when at least one segment was actually
            // rotated (otherwise the rotation was a no-op for this member's geometry).
            return rotated > 0 ? rAngleDeg : double.NaN;
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
