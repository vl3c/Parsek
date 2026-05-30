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

            // Localized departure search. The congruent-window D_k = D0 + k*synodic is only an APPROXIMATE
            // geometric recurrence: the target's eccentricity drifts the window over k, so the nominal D_k
            // can land in a Lambert non-convergence (the near-180-degree single-rev singularity the canary
            // also hits ~5/36 of the time). Search a small range around D_k for the CLOSEST departure that
            // yields a sane transfer actually reaching the target (the canary proves convergent departures
            // exist near every window), keeping the departure offset from D_k small (days at this cadence,
            // sub-pixel at map scale). Cached per window, so the search runs once when the window advances.
            const double SearchStepFraction = 0.005; // of the synodic period (~2-3 days for Kerbin->Duna)
            const int SearchMaxSteps = 12;            // +-6% of synodic
            double step = schedule.SynodicPeriodSeconds * SearchStepFraction;

            // Adapt to the RECORDED transfer's direction (handedness) instead of forcing prograde: find the
            // recorded heliocentric leg's inclination (> 90 deg => the recorded transfer was retrograde) and
            // require the synthesized transfer to match it. Near a ~180-degree transfer angle the Lambert
            // branch is unstable, so a window can flip handedness; the synth rejects a mismatch and the
            // search steps to a departure whose transfer travels the same way the recording did.
            double recordedInc = RecordedHeliocentricInclination(
                memberSegments, plan.CommonAncestor, plan.RecordedDepartureUT, plan.RecordedArrivalUT);
            bool recordedRetrograde = ReaimTransferSynthesizer.IsRetrogradeTransfer(recordedInc);
            bool progradeWanted = !recordedRetrograde;

            Orbit transferOrbit = null;
            double soiEntryUT = double.NaN;
            CelestialBody encounterBody = null;
            double departureUT = double.NaN;
            string failReason = null;
            for (int s = 0; s <= SearchMaxSteps && double.IsNaN(departureUT); s++)
            {
                double[] tries = s == 0
                    ? new[] { nominalDepartureUT }
                    : new[] { nominalDepartureUT + s * step, nominalDepartureUT - s * step };
                for (int t = 0; t < tries.Length; t++)
                {
                    if (ReaimTransferSynthesizer.TrySynthesizeTransfer(
                            launchBody, targetBody, tries[t], schedule.TofSeconds, progradeWanted,
                            out transferOrbit, out soiEntryUT, out encounterBody, out failReason))
                    {
                        departureUT = tries[t];
                        break;
                    }
                }
            }
            if (double.IsNaN(departureUT))
            {
                ParsekLog.Verbose("ReaimPlayback",
                    $"member={memberId} window={windowIndex} nominalDepartUT={nominalDepartureUT.ToString("R", ic)} " +
                    $"synth failed across +-{(SearchMaxSteps * step).ToString("F0", ic)}s search ({failReason}) - faithful this window");
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

            ParsekLog.Verbose("ReaimPlayback",
                $"member={memberId} window={windowIndex} re-aimed transfer ready: departUT={departureUT.ToString("R", ic)} " +
                $"tof={schedule.TofSeconds.ToString("R", ic)} soiEntryUT={soiEntryUT.ToString("R", ic)} " +
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
