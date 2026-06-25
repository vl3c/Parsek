using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// When a newly committed tree continues a vessel that was previously
        /// materialized from another recording, the older recording is an intermediate
        /// endpoint. Its ghost should still play, but its terminal real-vessel spawn
        /// must be suppressed so the later continuation owns the final vessel spawn.
        /// </summary>
        internal static int MarkSupersededTerminalSpawnsForContinuedSources(
            RecordingTree tree,
            string logContext = "CommitTree")
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;
            if (committedRecordings == null || committedRecordings.Count == 0)
                return 0;

            double treeStartUT = GetTreeStartUT(tree);
            int marked = 0;
            foreach (Recording continued in tree.Recordings.Values)
            {
                if (continued == null || continued.VesselPersistentId == 0)
                    continue;
                if (string.IsNullOrEmpty(continued.RecordingId))
                    continue;

                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording prior = committedRecordings[i];
                    if (prior == null || ReferenceEquals(prior, continued))
                        continue;
                    if (string.Equals(prior.RecordingId, continued.RecordingId, StringComparison.Ordinal))
                        continue;
                    if (!string.IsNullOrEmpty(prior.TerminalSpawnSupersededByRecordingId))
                        continue;
                    if (!ShouldMarkSupersededTerminalSpawn(
                            prior,
                            continued,
                            treeStartUT,
                            out string matchReason))
                        continue;

                    prior.TerminalSpawnSupersededByRecordingId = continued.RecordingId;
                    prior.FilesDirty = true;
                    marked++;
                    ParsekLog.Info("RecordingStore",
                        string.Format(CultureInfo.InvariantCulture,
                            "{0}: terminal spawn for recording '{1}' vessel='{2}' " +
                            "superseded by continuation '{3}' vesselPid={4} reason={5}",
                            logContext ?? "MarkSupersededTerminalSpawns",
                            prior.RecordingId ?? "<no-id>",
                            prior.VesselName ?? "<unnamed>",
                            continued.RecordingId,
                            continued.VesselPersistentId,
                            matchReason ?? "unknown"));
                }
            }

            if (marked > 0)
                BumpStateVersion();
            return marked;
        }

        internal static int MarkSupersededTerminalSpawnsForCommittedContinuations(
            string logContext = "CommittedContinuationRepair")
        {
            if (committedTrees == null || committedTrees.Count == 0)
                return 0;

            int marked = 0;
            for (int i = 0; i < committedTrees.Count; i++)
                marked += MarkSupersededTerminalSpawnsForContinuedSources(
                    committedTrees[i],
                    logContext);

            return marked;
        }

        /// <summary>
        /// Dock-merge terminal-spawn supersession (phantom-rover fix). When a dock/board
        /// merge absorbs a Parsek-spawned or adopted vessel that has a committed terminal
        /// leaf (e.g. a landed rover a logistics transport docked into), that leaf's live
        /// vessel disappears from FlightGlobals (its pid is absorbed into the surviving
        /// merged vessel) WITHOUT dying. The pid-equality / name+UT branches of
        /// <see cref="ShouldMarkSupersededTerminalSpawn"/> never link the two because the
        /// merged continuation carries the SURVIVOR's pid, not the absorbed one. Left
        /// unmarked, the per-frame spawn-death check resets the leaf for re-spawn and
        /// KSCSpawn later materialises a duplicate "out of thin air" at the runway.
        ///
        /// Mark the absorbed leaf's terminal spawn superseded by the merged continuation so
        /// neither the flight nor the KSC spawn path re-materialises it, and clear its spawn
        /// state so the spawn-death loop goes quiet.
        ///
        /// Identity is keyed on the absorbed live vessel's pid (<paramref name="absorbedPid"/>,
        /// the dock branch point's TargetVesselPersistentId):
        ///   - a genuine Parsek spawn carries a KSP-unique spawn pid
        ///     (SpawnedVesselPersistentId != VesselPersistentId) -> pid-only match, collision-free;
        ///   - an adopted / originally-recorded leaf carries the craft-baked VesselPersistentId,
        ///     which a relaunch of the same craft reuses, so that route is guid-gated against the
        ///     absorbed vessel's live launch guid (#976-class).
        /// The VesselPersistentId route is durable: it does NOT depend on the spawn-death check
        /// having not yet zeroed SpawnedVesselPersistentId earlier in the same frame.
        ///
        /// Two accepted limitations:
        ///   - When the absorbed vessel's launch guid is unknown (e.g. snapshot guid backfill
        ///     failed) the baked-pid route falls back to pid-only, so several committed leaves
        ///     that share the same craft-baked pid could all be superseded by one dock. This is
        ///     benign over-suppression of historical duplicates and only arises in the abnormal
        ///     no-guid state; with guids present the gate disambiguates to the one launch.
        ///   - The unique-spawn-pid route is durable only while the merge runs before the same-
        ///     frame spawn-death reset (the normal one-frame-deferred dock ordering). The
        ///     reported bug is the adoption / baked-pid case, which is durable regardless.
        /// </summary>
        internal static int MarkTerminalSpawnSupersededByDockMerge(
            uint absorbedPid,
            string absorbedLaunchGuid,
            uint mergedPid,
            string mergedContinuationRecordingId,
            string logContext = "DockMerge")
        {
            // 0 = no resolvable target; == merged means the "target" survived AS the merged
            // vessel (its terminal spawn is owned by the live merged continuation, not lost).
            if (absorbedPid == 0 || absorbedPid == mergedPid)
                return 0;
            if (string.IsNullOrEmpty(mergedContinuationRecordingId))
                return 0;
            if (committedRecordings == null || committedRecordings.Count == 0)
                return 0;

            int marked = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                Recording prior = committedRecordings[i];
                if (prior == null || string.IsNullOrEmpty(prior.RecordingId))
                    continue;
                if (string.Equals(prior.RecordingId, mergedContinuationRecordingId,
                        StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(prior.TerminalSpawnSupersededByRecordingId))
                    continue;

                bool uniqueSpawnMatch = prior.SpawnedVesselPersistentId != 0
                    && prior.SpawnedVesselPersistentId == absorbedPid
                    && prior.SpawnedVesselPersistentId != prior.VesselPersistentId;
                bool bakedPidMatch = prior.VesselPersistentId != 0
                    && prior.VesselPersistentId == absorbedPid;
                if (!uniqueSpawnMatch && !bakedPidMatch)
                    continue;

                // Guid-gate the craft-baked-pid identity (reusable across relaunches of the
                // same craft); a unique spawn pid cannot collide so it stays pid-only. A
                // null/unknown guid is not conclusive -> falls back to pid-only.
                if (!uniqueSpawnMatch
                    && VesselLaunchIdentity.GuidsConclusivelyDiffer(
                        prior.RecordedVesselGuid, absorbedLaunchGuid))
                    continue;

                prior.TerminalSpawnSupersededByRecordingId = mergedContinuationRecordingId;
                prior.VesselSpawned = false;
                prior.SpawnedVesselPersistentId = 0;
                prior.FilesDirty = true;
                marked++;
                ParsekLog.Info("RecordingStore",
                    string.Format(CultureInfo.InvariantCulture,
                        "{0}: terminal spawn for recording '{1}' vessel='{2}' superseded by " +
                        "dock-merge continuation '{3}' absorbedPid={4} mergedPid={5} " +
                        "match={6} guid={7}",
                        logContext ?? "DockMerge",
                        prior.RecordingId,
                        prior.VesselName ?? "<unnamed>",
                        mergedContinuationRecordingId,
                        absorbedPid,
                        mergedPid,
                        uniqueSpawnMatch ? "spawn-pid" : "baked-pid",
                        absorbedLaunchGuid ?? "<unknown>"));
            }

            if (marked > 0)
                BumpStateVersion();
            return marked;
        }

        private static bool ShouldMarkSupersededTerminalSpawn(
            Recording prior,
            Recording continued,
            double continuationTreeStartUT,
            out string reason)
        {
            reason = null;
            if (prior == null || continued == null)
                return false;
            if (continued.VesselPersistentId == 0)
                return false;
            if (prior.SpawnedVesselPersistentId == 0)
                return false;
            if (prior.EndUT > continued.EndUT + 1e-3)
                return false;

            // #976-class: an adoption-stamped prior carries SpawnedVesselPersistentId == its
            // craft-baked VesselPersistentId, which a relaunch of the same craft reuses as its own
            // VesselPersistentId, so a bare pid match would mark an unrelated later launch as
            // superseding this prior's terminal spawn. Guid-disambiguate only the adoption-stamp
            // case (real spawns use a KSP-unique spawn pid that cannot collide). A relaunch then
            // falls through to the name+UT-contiguity branch below, which rejects it (the relaunch's
            // tree starts after prior ends). Null/unknown guid keeps today's pid-only behavior.
            bool spawnedPidMatch = prior.SpawnedVesselPersistentId == continued.VesselPersistentId;
            bool adoptionRelaunchCollision = spawnedPidMatch
                && prior.SpawnedVesselPersistentId == prior.VesselPersistentId
                && VesselLaunchIdentity.GuidsConclusivelyDiffer(
                    prior.RecordedVesselGuid, continued.RecordedVesselGuid);
            if (spawnedPidMatch && !adoptionRelaunchCollision)
            {
                reason = "spawned-pid-match";
                return true;
            }

            if (string.Equals(prior.TreeId, continued.TreeId, StringComparison.Ordinal))
                return false;
            if (continued.TreeOrder <= 0)
                return false;
            if (double.IsNaN(continuationTreeStartUT))
                return false;
            if (continuationTreeStartUT > prior.EndUT + 1e-3)
                return false;
            if (prior.EndUT > continued.StartUT + 1e-3)
                return false;
            if (!string.Equals(prior.VesselName, continued.VesselName, StringComparison.Ordinal))
                return false;

            reason = "same-name-overlapping-continuation-tree";
            return true;
        }

        private static double GetTreeStartUT(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return double.NaN;

            double min = double.PositiveInfinity;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                double startUT = rec.StartUT;
                if (startUT < min)
                    min = startUT;
            }

            return double.IsPositiveInfinity(min) ? double.NaN : min;
        }
    }
}
