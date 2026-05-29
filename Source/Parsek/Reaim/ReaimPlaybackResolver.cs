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
            public IPlaybackTrajectory Adapter; // the re-aimed adapter, or null on a miss (=> faithful)
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
            windowIndex = -1;
            if (inner == null || !plan.Supported || !schedule.Valid || string.IsNullOrEmpty(memberId))
                return inner;

            // Map the live clock to the synodic window index using the SAME span-clock the engine uses
            // (schedule == null => uniform cadence path; cadence is the synodic period set by the builder).
            if (!GhostPlaybackLogic.TryComputeSpanLoopUT(
                    currentUT, unitPhaseAnchorUT, unitSpanStartUT, unitSpanEndUT, unitCadenceSeconds,
                    out double _, out long cycleIndex, out bool _, schedule: null))
            {
                // Parked before the first window: nothing renders yet, keep the faithful surface.
                return inner;
            }
            windowIndex = cycleIndex;

            if (cacheByMember.TryGetValue(memberId, out CacheEntry entry)
                && entry.Resolved && entry.Window == cycleIndex)
            {
                return entry.Adapter ?? inner;
            }

            IPlaybackTrajectory adapter = BuildWindowAdapter(memberId, inner, plan, schedule, cycleIndex);
            cacheByMember[memberId] = new CacheEntry
            {
                Window = cycleIndex,
                Resolved = true,
                Adapter = adapter
            };
            return adapter ?? inner;
        }

        // Synthesizes + assembles the re-aimed trajectory for one window. Returns null (=> faithful) on
        // any synthesis/assembly failure. One-shot per window (cached), so logs at Verbose.
        private static IPlaybackTrajectory BuildWindowAdapter(
            string memberId, IPlaybackTrajectory inner, ReaimMissionPlan plan,
            ReaimWindowPlanner.ReaimWindowSchedule schedule, long windowIndex)
        {
            var ic = CultureInfo.InvariantCulture;
            double departureUT = schedule.DepartureUTForWindow(windowIndex);

            CelestialBody launchBody = FindBody(plan.LaunchBody);
            CelestialBody targetBody = FindBody(plan.TargetBody);
            if (launchBody == null || targetBody == null)
            {
                ParsekLog.Warn("ReaimPlayback",
                    $"member={memberId} window={windowIndex} cannot resolve bodies launch='{plan.LaunchBody}' target='{plan.TargetBody}' - faithful");
                return null;
            }

            if (!ReaimTransferSynthesizer.TrySynthesizeTransfer(
                    launchBody, targetBody, departureUT, schedule.HohmannTofSeconds, schedule.Prograde,
                    out Orbit transferOrbit, out double soiEntryUT, out CelestialBody encounterBody,
                    out string failReason))
            {
                ParsekLog.Verbose("ReaimPlayback",
                    $"member={memberId} window={windowIndex} departUT={departureUT.ToString("R", ic)} synth failed ({failReason}) - faithful this window");
                return null;
            }

            OrbitSegment transferSeg = ReaimOrbitSegmentConverter.ToSegment(transferOrbit, plan.CommonAncestor);
            // Shift the transfer's epoch from the absolute departure into recorded-span time so the
            // assembled segment's phase matches the recorded-span playback clock: at recorded-span
            // RecordedDepartureUT the orbit sits where it was at absolute departureUT.
            transferSeg = ReaimSegmentAssembler.ShiftInTime(transferSeg, plan.RecordedDepartureUT - departureUT);

            List<OrbitSegment> assembled = ReaimSegmentAssembler.Assemble(plan, transferSeg, schedule.HohmannTofSeconds);
            if (assembled == null || assembled.Count == 0)
            {
                ParsekLog.Warn("ReaimPlayback",
                    $"member={memberId} window={windowIndex} assemble returned empty - faithful this window");
                return null;
            }

            ParsekLog.Verbose("ReaimPlayback",
                $"member={memberId} window={windowIndex} re-aimed transfer ready: departUT={departureUT.ToString("R", ic)} " +
                $"tof={schedule.HohmannTofSeconds.ToString("R", ic)} soiEntryUT={soiEntryUT.ToString("R", ic)} " +
                $"encounter={(encounterBody != null ? encounterBody.bodyName : "<none>")} segs={assembled.Count}");
            return new ReaimedTrajectory(inner, assembled);
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
