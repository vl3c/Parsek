using System;
using System.Collections.Generic;

namespace Parsek
{
    internal static partial class GhostMapPresence
    {
        // =====================================================================
        //  High-warp collapse of the per-instance overlap map presence
        //  (todo OVERLAP-MAP-GHOSTS-CHURN-AT-HIGH-WARP)
        //
        //  When the game time between two overlap sweeps spans at least one launch cadence, the
        //  active cycle window [firstCycle, lastCycle] moves past live instances on every sweep, so
        //  the per-instance path would create and destroy map ProtoVessels on every sweep (up to
        //  MaxSpawnsPerFrame each way) and the Tracking Station list would refresh after each
        //  mutation: the icons strobe. Above the ghost-hide warp threshold AND only while a sweep
        //  spans a cadence, the recording keeps ONE map vessel: the newest instance whose cycle is
        //  still live, and when that cycle expires the same vessel is re-pointed (re-keyed and its
        //  orbit re-seeded) at the newest cycle instead of being destroyed and recreated. Below the
        //  threshold, or when a sweep spans less than a cadence, nothing here runs.
        // =====================================================================

        /// <summary>Sentinel for "no cycle" in the collapse decision.</summary>
        internal const long NoOverlapCycle = long.MinValue;

        /// <summary>
        /// UT of the previous <see cref="RunOverlapPerInstanceSweep"/> call (NaN before the first one
        /// and after every map-presence reset). The sweep runs once per frame in the flight map and
        /// once per lifecycle tick (0.25 s real time) in the Tracking Station, so the delta is the game
        /// time the cycle window moved between two create/destroy decisions in either scene.
        /// </summary>
        private static double lastOverlapSweepUT = double.NaN;

        // Per-sweep collapse tallies for the one shared-key summary line.
        private static int overlapCollapseRecordings;
        private static int overlapCollapseRepointed;
        private static int overlapCollapseRebuilt;
        private static int overlapCollapseDestroyed;
        private static int overlapCollapseCreated;

        /// <summary>
        /// Pure: game seconds between two overlap sweeps. Zero for the first sweep (no previous UT),
        /// for a non-finite input, and for a clock that did not advance or went backwards (a rewind
        /// or quickload), so those never read as a cadence-spanning sweep.
        /// </summary>
        internal static double ResolveOverlapSweepGameSeconds(double previousSweepUT, double currentUT)
        {
            if (double.IsNaN(previousSweepUT) || double.IsInfinity(previousSweepUT)
                || double.IsNaN(currentUT) || double.IsInfinity(currentUT))
                return 0.0;
            double delta = currentUT - previousSweepUT;
            return delta > 0.0 ? delta : 0.0;
        }

        /// <summary>
        /// Pure gate: should an overlap recording keep a single map vessel this sweep? True only when
        /// the warp rate is above <see cref="WarpThresholds.GhostHide"/> (the rate above which the
        /// flight engine already culls overlap clones in the flight view) AND the game time since the
        /// previous sweep spans at least one launch cycle (<paramref name="cycleDurationSeconds"/>,
        /// the spacing <see cref="GhostPlaybackLogic.GetActiveCycles"/> uses), which is exactly when
        /// the cycle window moves past a live instance on every sweep. Both conditions are required so
        /// the map keeps its N icons whenever the per-instance lifecycle is not churning, and a single
        /// clock jump at 1x (a TimeJump, a quickload) never collapses anything.
        /// </summary>
        internal static bool ShouldCollapseOverlapToSingleInstance(
            float warpRate, double sweepGameSeconds, double cycleDurationSeconds)
        {
            if (!(warpRate > WarpThresholds.GhostHide))
                return false;
            if (double.IsNaN(cycleDurationSeconds) || double.IsInfinity(cycleDurationSeconds)
                || cycleDurationSeconds <= 0.0)
                return false;
            if (double.IsNaN(sweepGameSeconds) || double.IsInfinity(sweepGameSeconds)
                || sweepGameSeconds <= 0.0)
                return false;
            return sweepGameSeconds >= cycleDurationSeconds;
        }

        /// <summary>
        /// Pure collapse decision for one overlap recording. Given the cycles that have a live map
        /// vessel and the active window <c>[firstCycle, lastCycle]</c>:
        ///   - an existing instance whose cycle is still live: keep the newest such one
        ///     (<paramref name="keepCycle"/>), destroy every other;
        ///   - otherwise, when any instance exists (all expired): re-point the newest one at
        ///     <paramref name="lastCycle"/> (<paramref name="repointFromCycle"/>), destroy every other;
        ///   - otherwise: create the newest cycle when the spawn budget allows
        ///     (<paramref name="createNewest"/>).
        /// An empty window (<paramref name="lastCycle"/> &lt; <paramref name="firstCycle"/>) destroys
        /// everything. Unused outputs are <see cref="NoOverlapCycle"/> / false.
        /// </summary>
        internal static void DecideCollapsedOverlapInstanceChanges(
            IEnumerable<long> existingCycles,
            long firstCycle,
            long lastCycle,
            int spawnBudget,
            out long keepCycle,
            out long repointFromCycle,
            out bool createNewest,
            out List<long> toDestroy)
        {
            keepCycle = NoOverlapCycle;
            repointFromCycle = NoOverlapCycle;
            createNewest = false;
            toDestroy = new List<long>();

            bool windowLive = lastCycle >= firstCycle;
            var all = new List<long>();
            long newestLive = NoOverlapCycle;
            long newestAny = NoOverlapCycle;
            if (existingCycles != null)
            {
                foreach (long c in existingCycles)
                {
                    all.Add(c);
                    if (c > newestAny)
                        newestAny = c;
                    if (windowLive && c >= firstCycle && c <= lastCycle && c > newestLive)
                        newestLive = c;
                }
            }

            if (!windowLive)
            {
                toDestroy.AddRange(all);
                return;
            }

            if (newestLive != NoOverlapCycle)
                keepCycle = newestLive;
            else if (newestAny != NoOverlapCycle)
                repointFromCycle = newestAny;
            else
                createNewest = spawnBudget > 0;

            for (int i = 0; i < all.Count; i++)
            {
                long c = all[i];
                if (c != keepCycle && c != repointFromCycle)
                    toDestroy.Add(c);
            }
        }

        private enum OverlapRepointOutcome
        {
            Repointed,
            NoSource,
            NeedsRebuild,
        }

        /// <summary>
        /// Collapsed lifecycle for one overlap recording (see the section header). Returns the number
        /// of map vessels created so the caller debits the shared per-frame spawn counter.
        /// </summary>
        private static int EnsureCollapsedOverlapInstance(
            int recIdx,
            Recording rec,
            double currentUT,
            double playbackStartUT,
            double scheduleStartUT,
            double duration,
            double cycleDuration,
            long firstCycle,
            long lastCycle,
            List<long> existing,
            bool liveLaunchMatchedAnchorOfActiveMember,
            ref int frameSpawnCount)
        {
            int spawnBudget = GhostPlayback.MaxSpawnsPerFrame - frameSpawnCount;
            if (spawnBudget < 0) spawnBudget = 0;

            DecideCollapsedOverlapInstanceChanges(
                existing, firstCycle, lastCycle, spawnBudget,
                out long keepCycle, out long repointFromCycle, out bool createNewest,
                out List<long> toDestroy);

            overlapCollapseRecordings++;

            int destroyed = 0;
            for (int i = 0; i < toDestroy.Count; i++)
            {
                RemoveOverlapInstance(recIdx, toDestroy[i], "overlap-highwarp-collapse");
                destroyed++;
            }

            double newestEffUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                currentUT, scheduleStartUT, playbackStartUT, duration, cycleDuration, lastCycle);
            double newestShift = currentUT - newestEffUT;

            int repointed = 0;
            int rebuilt = 0;
            if (repointFromCycle != NoOverlapCycle)
            {
                OverlapRepointOutcome outcome = TryRepointOverlapInstance(
                    recIdx, rec, repointFromCycle, lastCycle, newestEffUT, newestShift,
                    liveLaunchMatchedAnchorOfActiveMember);
                if (outcome == OverlapRepointOutcome.Repointed)
                {
                    repointed++;
                }
                else
                {
                    // NoSource: the newest cycle has no map-visible orbit at its head, so a fresh
                    // create would be skipped too; drop the expired vessel. NeedsRebuild: the vessel
                    // or the new source is not segment-driven, so fall back to destroy + create.
                    RemoveOverlapInstance(recIdx, repointFromCycle,
                        outcome == OverlapRepointOutcome.NoSource
                            ? "overlap-highwarp-repoint-no-source"
                            : "overlap-highwarp-repoint-rebuild");
                    destroyed++;
                    if (outcome == OverlapRepointOutcome.NeedsRebuild)
                    {
                        createNewest = spawnBudget > 0;
                        rebuilt++;
                    }
                }
            }

            int created = 0;
            if (createNewest
                && !GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame))
            {
                Vessel inst = CreateOverlapInstanceVessel(
                    recIdx, rec, lastCycle, newestEffUT, newestShift,
                    liveLaunchMatchedAnchorOfActiveMember);
                if (inst != null)
                {
                    created++;
                    frameSpawnCount++;
                }
            }

            overlapCollapseRepointed += repointed;
            overlapCollapseRebuilt += rebuilt;
            overlapCollapseDestroyed += destroyed;
            overlapCollapseCreated += created;

            if (created > 0 || destroyed > 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "overlap-lifecycle-" + recIdx,
                    string.Format(ic,
                        "Overlap per-instance lifecycle (high-warp collapse) rec=#{0} \"{1}\" " +
                        "cycles=[{2}..{3}] kept={4} created={5} destroyed={6} liveNow={7} currentUT={8:F1}",
                        recIdx, rec.VesselName ?? "(null)", firstCycle, lastCycle,
                        keepCycle == NoOverlapCycle ? "none" : keepCycle.ToString(ic),
                        created, destroyed, GetOverlapInstanceCount(recIdx), currentUT),
                    2.0);
            }

            return created;
        }

        /// <summary>
        /// Re-point the existing segment-driven map vessel of cycle <paramref name="fromCycle"/> at
        /// cycle <paramref name="toCycle"/>: resolve the covering orbit source at the new cycle's head
        /// exactly as <see cref="CreateOverlapInstanceVessel"/> does, re-seed the orbit, loop shift and
        /// bounds through <see cref="ApplyOrbitToVessel"/> (the same seed a fresh segment instance
        /// gets after its build, and the reseed the per-index path uses on a live ghost), and re-key
        /// the store entry. The pid, and every per-pid registration keyed on it, is kept, so the stock
        /// vessel list does not change.
        /// </summary>
        private static OverlapRepointOutcome TryRepointOverlapInstance(
            int recIdx, Recording rec, long fromCycle, long toCycle,
            double effUT, double loopEpochShiftSeconds,
            bool liveLaunchMatchedAnchorOfActiveMember)
        {
            if (!overlapInstanceVessels.TryGetValue((recIdx, fromCycle), out Vessel vessel)
                || vessel == null
                || vessel.orbitDriver == null)
                return OverlapRepointOutcome.NeedsRebuild;

            uint pid = vessel.persistentId;
            // Only a segment / endpoint-tail instance registers the per-pid epoch shift (see
            // CreateOverlapInstanceVessel); a terminal-orbit or state-vector instance carries a
            // different orbit contract and is rebuilt instead.
            if (!ghostOrbitEpochShift.ContainsKey(pid))
                return OverlapRepointOutcome.NeedsRebuild;

            int cachedStateVectorIndex = -1;
            TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
                rec,
                false,
                IsMaterializedForMapPresence(rec),
                effUT,
                true,
                "overlap-instance-repoint",
                ref cachedStateVectorIndex,
                out OrbitSegment segment,
                out _,
                out _,
                recordingIndex: recIdx,
                loopMemberInWindow: true,
                liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember);

            if (!IsMapCreateAcceptedSource(source))
                return OverlapRepointOutcome.NoSource;
            if (source != TrackingStationGhostSource.Segment
                && source != TrackingStationGhostSource.EndpointTail)
                return OverlapRepointOutcome.NeedsRebuild;

            string logContext = string.Format(ic,
                "overlap instance rec=#{0} cycle={1} (source={2}, repointed from cycle {3})",
                recIdx, toCycle, source, fromCycle);

            // The icon-drive propagate record belongs to the old cycle's phase; drop it so the probe
            // does not trust it for the re-seeded orbit (the same drop the state-vector reseed does).
            ghostIconDrivePropagation.Remove(pid);
            ApplyOrbitToVessel(vessel, segment, logContext, loopEpochShiftSeconds);

            overlapInstanceVessels.Remove((recIdx, fromCycle));
            overlapInstanceVessels[(recIdx, toCycle)] = vessel;

            ParsekLog.VerboseRateLimited(Tag, "overlap-repoint-" + recIdx,
                string.Format(ic,
                    "Re-pointed overlap per-instance map vessel rec=#{0} \"{1}\" cycle {2} -> {3} pid={4} " +
                    "source={5} effUT={6:F1} loopShift={7:F1} (high-warp collapse)",
                    recIdx, rec.VesselName ?? "(null)", fromCycle, toCycle, pid, source,
                    effUT, loopEpochShiftSeconds),
                2.0);
            return OverlapRepointOutcome.Repointed;
        }

        private static void ResetOverlapCollapseTally()
        {
            overlapCollapseRecordings = 0;
            overlapCollapseRepointed = 0;
            overlapCollapseRebuilt = 0;
            overlapCollapseDestroyed = 0;
            overlapCollapseCreated = 0;
        }

        private static void LogOverlapCollapseSummary(float warpRate, double sweepGameSeconds)
        {
            if (overlapCollapseRecordings <= 0)
                return;
            ParsekLog.VerboseRateLimited(Tag, "overlap-highwarp-collapse",
                FormatOverlapCollapseSummary(
                    overlapCollapseRecordings, overlapCollapseRepointed, overlapCollapseRebuilt,
                    overlapCollapseDestroyed, overlapCollapseCreated, warpRate, sweepGameSeconds),
                5.0);
        }

        /// <summary>Pure: the shared-key high-warp collapse summary line (invariant culture).</summary>
        internal static string FormatOverlapCollapseSummary(
            int recordings, int repointed, int rebuilt, int destroyed, int created,
            float warpRate, double sweepGameSeconds)
        {
            return string.Format(ic,
                "Overlap high-warp collapse: recordings={0} repointed={1} rebuilt={2} destroyed={3} " +
                "created={4} warp={5:F0}x sweepGameSeconds={6:F1} (one map vessel per overlap recording " +
                "while a sweep spans a launch cycle)",
                recordings, repointed, rebuilt, destroyed, created, warpRate, sweepGameSeconds);
        }

        /// <summary>Live warp rate for the overlap sweep; 1 when TimeWarp is unavailable.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static float ReadOverlapSweepWarpRate()
        {
            try
            {
                return object.ReferenceEquals(TimeWarp.fetch, null) ? 1f : TimeWarp.CurrentRate;
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        /// <summary>Test seam: the previous-sweep UT the collapse gate reads (NaN when unset).</summary>
        internal static double LastOverlapSweepUTForTesting => lastOverlapSweepUT;
    }
}
