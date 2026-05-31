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
            // the instant the ghost departs (the transfer line touches Kerbin). Departure stays PINNED at
            // D_k (we never search departure); only the TIME OF FLIGHT is free.
            //
            // SELECTION OBJECTIVE (arrival-seam SOI-timing, docs/dev/plans/reaim-arrival-seam-timing.md):
            // instead of taking the FIRST tof that hits the body (position-min), evaluate ALL candidate tofs
            // (the +-6% / 12-step sweep, plus a refine pass around the best) and pick the one whose ARRIVAL
            // v_inf best matches the RECORDED arrival v_inf. The recorded leg (arrival hyperbola + capture +
            // descent) is NEVER rotated / shifted / relocated; v1 only changes WHICH transfer timing is
            // chosen per window so the recorded approach splices on with the smallest seam. The position-min
            // (first-success) transfer is also kept as the FAITHFUL baseline, and we DECLINE TO FAITHFUL
            // unless the v_inf-chosen transfer produces a strictly smaller seam under tolerance (so v1 can
            // never regress). Cached per window (runs once per advance).
            const double TofSearchStepFraction = 0.005; // of the recorded tof (~0.4 day for Kerbin->Duna)
            const int SearchMaxSteps = 12;              // +-6% of the recorded tof
            const int RefineSteps = 4;                  // parabolic refine around the best coarse tof
            const double DeclineSoiFraction = 0.25;     // accept the chosen transfer only below 0.25 * SOI
            double tofStep = schedule.TofSeconds * TofSearchStepFraction;
            double departureUT = nominalDepartureUT;

            // The recorded arrival v_inf (the fixed target we match against), in Zup. Build it once per
            // window. When the arrival leg is not hyperbolic / degenerate, vInfRec is null and the v_inf
            // objective cannot score: we then fall back to the faithful (first-success) transfer.
            double[] vInfRec = ReaimArrivalVInf.RecordedArrivalVInf(plan.ArrivalLeg, targetBody);

            // Collect every successful candidate tof, scoring its arrival-v_inf mismatch. The faithful
            // baseline is the FIRST success in the existing search order (position-min / current behavior).
            Orbit faithfulOrbit = null, chosenOrbit = null;
            double faithfulSoiUT = double.NaN, chosenSoiUT = double.NaN;
            double faithfulTof = double.NaN, chosenTof = double.NaN;
            double[] chosenVInf = null;
            double bestMismatch = double.PositiveInfinity;
            CelestialBody encounterBody = null;
            int candidatesEvaluated = 0, candidatesScored = 0;
            string failReason = null;

            // Local: try one tof, and if it synthesizes, record it as the faithful baseline (first success)
            // and update the best-v_inf-mismatch winner. Returns whether the tof synthesized.
            bool TryCandidate(double tof)
            {
                if (!(tof > 0.0))
                    return false;
                candidatesEvaluated++;
                if (!ReaimTransferSynthesizer.TrySynthesizeTransfer(
                        launchBody, targetBody, departureUT, tof, progradeWanted,
                        out Orbit cand, out double candSoiUT, out CelestialBody candEnc, out string candFail))
                {
                    failReason = candFail;
                    return false;
                }
                if (faithfulOrbit == null) // first success in search order = the position-min faithful pick
                {
                    faithfulOrbit = cand;
                    faithfulSoiUT = candSoiUT;
                    faithfulTof = tof;
                }
                encounterBody = candEnc;
                double[] candVInf = ReaimArrivalVInf.CandidateArrivalVInf(cand, targetBody, candSoiUT);
                double mismatch = ReaimArrivalGeometry.VInfMismatch(candVInf, vInfRec);
                if (!double.IsNaN(mismatch) && !double.IsInfinity(mismatch))
                {
                    candidatesScored++;
                    if (mismatch < bestMismatch)
                    {
                        bestMismatch = mismatch;
                        chosenOrbit = cand;
                        chosenSoiUT = candSoiUT;
                        chosenTof = tof;
                        chosenVInf = candVInf;
                    }
                }
                return true;
            }

            // Coarse sweep: recorded tof (step 0), then +- each step out to +-6%. Evaluate ALL (no break).
            TryCandidate(schedule.TofSeconds);
            for (int s = 1; s <= SearchMaxSteps; s++)
            {
                TryCandidate(schedule.TofSeconds + s * tofStep);
                TryCandidate(schedule.TofSeconds - s * tofStep);
            }

            // Refine pass: if a scored winner exists, sample a finer grid around the best coarse tof
            // (+-1 coarse step at RefineSteps resolution) to nudge the v_inf mismatch toward its local min.
            if (chosenOrbit != null && !double.IsNaN(chosenTof) && RefineSteps > 0)
            {
                double refineStep = tofStep / RefineSteps;
                double center = chosenTof;
                for (int s = 1; s <= RefineSteps; s++)
                {
                    TryCandidate(center + s * refineStep);
                    TryCandidate(center - s * refineStep);
                }
            }

            if (faithfulOrbit == null)
            {
                ParsekLog.Verbose("ReaimPlayback",
                    $"member={memberId} window={windowIndex} departUT={nominalDepartureUT.ToString("R", ic)} " +
                    $"synth failed across tof +-{(SearchMaxSteps * tofStep).ToString("F0", ic)}s search " +
                    $"(evaluated={candidatesEvaluated} lastFail={failReason}) - faithful this window");
                return null;
            }

            // Decline-to-faithful gate: compute the seam the v_inf-chosen transfer produces and the seam the
            // faithful (position-min) transfer produces; accept the chosen one only when its seam is strictly
            // smaller AND below 0.25 * SOI. Otherwise use the faithful transfer (the current cosmetic seam),
            // so v1 never regresses. When the v_inf objective could not score (vInfRec null, or no scored
            // candidate), chosenOrbit is null and we go faithful by definition.
            double faithfulSeam = ReaimArrivalVInf.SoiEdgeSeamMeters(
                faithfulOrbit, plan.ArrivalLeg, targetBody, faithfulSoiUT);
            double chosenSeam = chosenOrbit != null
                ? ReaimArrivalVInf.SoiEdgeSeamMeters(chosenOrbit, plan.ArrivalLeg, targetBody, chosenSoiUT)
                : double.NaN;
            bool accepted = chosenOrbit != null && ReaimArrivalGeometry.AcceptChosenOverFaithful(
                chosenSeam, faithfulSeam, targetBody.sphereOfInfluence, DeclineSoiFraction);

            double dirResidualDeg = (chosenVInf != null && vInfRec != null)
                ? ReaimArrivalGeometry.AngleBetweenDegrees(chosenVInf, vInfRec)
                : double.NaN;
            ParsekLog.Info("ReaimPlayback",
                $"arrival-timing: member={memberId} window={windowIndex} " +
                $"vinfRec={ReaimArrivalVInf.FormatVInfMag(vInfRec)}m/s " +
                $"vinfChosen={ReaimArrivalVInf.FormatVInfMag(chosenVInf)}m/s " +
                $"vinfResidual={(double.IsInfinity(bestMismatch) ? double.NaN : bestMismatch).ToString("F1", ic)}m/s " +
                $"dirDeg={dirResidualDeg.ToString("F2", ic)} " +
                $"faithfulSeam={faithfulSeam.ToString("F0", ic)}m chosenSeam={chosenSeam.ToString("F0", ic)}m " +
                $"accepted={(accepted ? "true" : "false")} " +
                $"(scored={candidatesScored}/{candidatesEvaluated} chosenTof={chosenTof.ToString("F0", ic)} " +
                $"faithfulTof={faithfulTof.ToString("F0", ic)} recordedTof={schedule.TofSeconds.ToString("F0", ic)})");

            // Use the accepted transfer (v_inf-chosen when it beats faithful, else the faithful baseline).
            Orbit transferOrbit = accepted ? chosenOrbit : faithfulOrbit;
            double soiEntryUT = accepted ? chosenSoiUT : faithfulSoiUT;
            double usedTofSeconds = accepted ? chosenTof : faithfulTof;

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

            ParsekLog.Verbose("ReaimPlayback",
                $"member={memberId} window={windowIndex} re-aimed transfer ready: departUT={departureUT.ToString("R", ic)} (nominal D_k) " +
                $"tof={usedTofSeconds.ToString("R", ic)} (recorded={schedule.TofSeconds.ToString("R", ic)}) soiEntryUT={soiEntryUT.ToString("R", ic)} " +
                $"encounter={(encounterBody != null ? encounterBody.bodyName : "<none>")} segs={assembled.Count} " +
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
