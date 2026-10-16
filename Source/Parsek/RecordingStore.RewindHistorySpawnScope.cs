using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// Result of scoping a plain rewind's spawned-vessel PID strip.
        /// </summary>
        internal sealed class RewindSpawnStripScope
        {
            /// <summary>Spawned pids a replaying recording re-produces: strip them.</summary>
            internal readonly HashSet<uint> StripPids = new HashSet<uint>();

            /// <summary>Spawned pids of committed history no replaying recording re-produces: keep.</summary>
            internal readonly HashSet<uint> KeptPids = new HashSet<uint>();

            /// <summary>Ids of the recordings holding a kept pid (their spawn state is preserved).</summary>
            internal readonly HashSet<string> KeptHolderRecordingIds = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// Pids of foreign vessels a tree adopted by inventory transfer: never stripped by
            /// their holder, and their holder's spawn state resets so the chain re-spawns the tip.
            /// </summary>
            internal readonly HashSet<uint> ForeignAdoptedPids = new HashSet<uint>();
        }

        /// <summary>
        /// True when <paramref name="rec"/> will replay (ghost and terminal spawn) after a
        /// rewind whose playhead lands on <paramref name="adjustedUT"/>: it is already in
        /// replay scope, or its activation start lies at or after the landing UT (within the
        /// scope tolerance). A recording that answers false is historical: the replay-scope
        /// gate (<see cref="PlaybackScopeTracker.IsHistoricalNeverReplayed"/>) suppresses both
        /// its ghost and its terminal spawn after the rewind. An unknown landing UT (NaN /
        /// infinite) answers true, so an unscoped call keeps the strip-everything behavior.
        /// </summary>
        internal static bool WillReplayAfterRewind(Recording rec, double adjustedUT)
        {
            if (rec == null)
                return false;
            if (double.IsNaN(adjustedUT) || double.IsInfinity(adjustedUT))
                return true;
            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec);
            return !PlaybackScopeTracker.IsHistoricalNeverReplayed(
                rec.RecordingId, adjustedUT, activationStartUT);
        }

        /// <summary>
        /// Whether a recording that replays after the rewind will re-produce the vessel
        /// <paramref name="holder"/> spawned (its <c>SpawnedVesselPersistentId</c>). The
        /// candidates, in order: the holder itself; the continuation its terminal spawn was
        /// handed to; any other member of its chain (the chain tip owns the terminal spawn,
        /// and an optimizer split can leave the spawn fields on an earlier segment); and any
        /// recording that flies that same vessel. The last route matches a KSP-unique spawn
        /// pid by pid alone, and an adoption-stamped (craft-baked) pid only when the launch
        /// guids do not conclusively differ, so a later relaunch of the same craft does not
        /// count as re-producing an earlier launch's vessel. An InventoryTransfer member
        /// (<paramref name="transferMemberIds"/>) never counts on that last route: it flies a
        /// vessel that existed before its tree, and its replay ghosts that vessel rather than
        /// producing a second one.
        /// </summary>
        internal static bool IsSpawnedVesselReproducedByRewindReplay(
            Recording holder,
            IReadOnlyList<Recording> recordings,
            double adjustedUT,
            out string via,
            ICollection<string> transferMemberIds = null)
        {
            via = null;
            if (holder == null || holder.SpawnedVesselPersistentId == 0)
                return false;

            if (WillReplayAfterRewind(holder, adjustedUT))
            {
                via = "holder-replays";
                return true;
            }

            if (recordings == null)
                return false;

            uint pid = holder.SpawnedVesselPersistentId;
            bool uniqueSpawnPid = pid != holder.VesselPersistentId;
            string supersededBy = holder.TerminalSpawnSupersededByRecordingId;

            for (int i = 0; i < recordings.Count; i++)
            {
                Recording other = recordings[i];
                if (other == null || ReferenceEquals(other, holder))
                    continue;
                if (!string.IsNullOrEmpty(holder.RecordingId)
                    && string.Equals(other.RecordingId, holder.RecordingId, StringComparison.Ordinal))
                    continue;

                string reason = null;
                if (!string.IsNullOrEmpty(supersededBy)
                    && string.Equals(other.RecordingId, supersededBy, StringComparison.Ordinal))
                {
                    reason = "superseding-continuation-replays";
                }
                else if (!string.IsNullOrEmpty(holder.ChainId)
                    && string.Equals(other.ChainId, holder.ChainId, StringComparison.Ordinal)
                    && string.Equals(other.TreeId, holder.TreeId, StringComparison.Ordinal))
                {
                    reason = "chain-member-replays";
                }
                else if (other.VesselPersistentId == pid
                    && !IsTransferMemberId(other, transferMemberIds)
                    && (uniqueSpawnPid
                        || !VesselLaunchIdentity.GuidsConclusivelyDiffer(
                            other.RecordedVesselGuid, holder.RecordedVesselGuid)))
                {
                    reason = "same-vessel-recording-replays";
                }

                if (reason == null)
                    continue;
                if (!WillReplayAfterRewind(other, adjustedUT))
                    continue;

                via = reason + ":" + (other.RecordingId ?? "<no-id>");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Splits the committed recordings' spawned-vessel pids into the ones a plain rewind
        /// strips (a replaying recording re-produces the vessel) and the ones it keeps
        /// (committed history: the vessel exists at the rewind target and nothing that replays
        /// will spawn it again, so stripping it would lose it for good). A pid is stripped when
        /// ANY holder's vessel is re-produced. A holder whose spawn is a foreign vessel its tree
        /// adopted by inventory transfer (<see cref="ForeignInventoryTransfer.FindAdoptedForeignVesselMember"/>)
        /// neither strips nor keeps its pid: that vessel existed before the tree, so the rewind
        /// save holds its pre-adoption self, which the chain walker claims and ghosts, and the
        /// holder's spawn state resets so the chain tip spawns again at its end.
        /// </summary>
        internal static RewindSpawnStripScope ResolveRewindSpawnStripScope(
            IReadOnlyList<Recording> recordings, double adjustedUT,
            ICollection<string> transferMemberIds = null)
        {
            var scope = new RewindSpawnStripScope();
            if (recordings == null)
                return scope;

            var keptHolders = new List<Recording>();
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording holder = recordings[i];
                if (holder == null || holder.SpawnedVesselPersistentId == 0)
                    continue;
                uint pid = holder.SpawnedVesselPersistentId;
                Recording adoptedMember = ForeignInventoryTransfer.FindAdoptedForeignVesselMember(
                    holder, recordings, transferMemberIds);
                if (adoptedMember != null)
                {
                    if (scope.ForeignAdoptedPids.Add(pid) && !SuppressLogging)
                        ParsekLog.Verbose("Rewind",
                            string.Format(CultureInfo.InvariantCulture,
                                "Rewind strip scope: leave spawned pid={0} of '{1}' (id={2}) in the rewind save: " +
                                "a foreign vessel its tree adopted by inventory transfer (member={3}), " +
                                "so its chain ghosts the real vessel and spawns the tip again",
                                pid, holder.VesselName ?? "<unnamed>",
                                holder.RecordingId ?? "<no-id>",
                                adoptedMember.RecordingId ?? "<no-id>"));
                    continue;
                }
                if (IsSpawnedVesselReproducedByRewindReplay(
                        holder, recordings, adjustedUT, out string via, transferMemberIds))
                {
                    if (scope.StripPids.Add(pid) && !SuppressLogging)
                        ParsekLog.Verbose("Rewind",
                            string.Format(CultureInfo.InvariantCulture,
                                "Rewind strip scope: strip spawned pid={0} of '{1}' (id={2}) via {3}",
                                pid, holder.VesselName ?? "<unnamed>",
                                holder.RecordingId ?? "<no-id>", via ?? "unknown"));
                }
                else
                {
                    keptHolders.Add(holder);
                }
            }

            for (int i = 0; i < keptHolders.Count; i++)
            {
                Recording holder = keptHolders[i];
                uint pid = holder.SpawnedVesselPersistentId;
                // Another holder of the same pid is re-produced: the vessel is stripped and
                // this holder's spawn state is reset with everyone else's.
                if (scope.StripPids.Contains(pid))
                    continue;
                scope.KeptPids.Add(pid);
                if (!string.IsNullOrEmpty(holder.RecordingId))
                    scope.KeptHolderRecordingIds.Add(holder.RecordingId);
                if (!SuppressLogging)
                    ParsekLog.Verbose("Rewind",
                        string.Format(CultureInfo.InvariantCulture,
                            "Rewind strip scope: keep spawned pid={0} of '{1}' (id={2}) as committed history " +
                            "(no recording that replays after adjustedUT={3} re-produces it)",
                            pid, holder.VesselName ?? "<unnamed>",
                            holder.RecordingId ?? "<no-id>",
                            adjustedUT.ToString("F1", CultureInfo.InvariantCulture)));
            }

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    string.Format(CultureInfo.InvariantCulture,
                        "Rewind strip scope: adjustedUT={0} stripPids={1} keptHistoryPids={2} keptHolders={3} foreignAdoptedPids={4}",
                        adjustedUT.ToString("F1", CultureInfo.InvariantCulture),
                        scope.StripPids.Count, scope.KeptPids.Count, scope.KeptHolderRecordingIds.Count,
                        scope.ForeignAdoptedPids.Count));
            return scope;
        }

        private static bool IsTransferMemberId(Recording rec, ICollection<string> transferMemberIds)
        {
            return rec != null && transferMemberIds != null
                && !string.IsNullOrEmpty(rec.RecordingId)
                && transferMemberIds.Contains(rec.RecordingId);
        }

        /// <summary>
        /// The plain rewind's PID-strip resolver (passed to <see cref="PreProcessRewindSave(string, HashSet{string}, Func{double, HashSet{uint}}, double)"/>):
        /// scopes the strip over every committed recording at the save's adjusted UT and
        /// records the kept holders for the OnLoad playback reset.
        /// </summary>
        internal static HashSet<uint> ResolveRewindStripSpawnedPids(double adjustedUT)
        {
            RewindSpawnStripScope scope = ResolveRewindSpawnStripScope(
                CollectAllCommittedRecordings(), adjustedUT,
                ForeignInventoryTransfer.CollectTransferMemberRecordingIds(CommittedTrees));
            RewindContext.SetHistoricalSpawnKeepRecordingIds(scope.KeptHolderRecordingIds);
            return scope.StripPids;
        }
    }
}
