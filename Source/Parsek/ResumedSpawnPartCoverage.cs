using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The answer of <see cref="ResumedSpawnPartCoverage.Evaluate(Recording, IList{uint})"/>:
    /// whether a restored committed recording can keep recording the live vessel in place, and
    /// the counts the decision line logs.
    /// </summary>
    internal struct ResumedPartCoverage
    {
        /// <summary>True: record into the target recording as before. False: the live vessel
        /// carries part persistentIds the target's snapshot does not hold, so its part events
        /// would not resolve on the target's ghost; continue in a new segment.</summary>
        internal bool Covered;

        /// <summary>Why: <c>no-live-parts</c>, <c>no-snapshot</c>, <c>snapshot-without-parts</c>,
        /// <c>all-covered</c> or <c>uncovered-parts</c>.</summary>
        internal string Reason;

        /// <summary>Which snapshot was read: <c>ghost</c>, <c>vessel</c> or <c>none</c>.</summary>
        internal string SnapshotSource;

        internal int LivePartCount;
        internal int SnapshotPartCount;
        internal int UncoveredPartCount;
    }

    /// <summary>
    /// Pure: does the target recording's snapshot hold every part persistentId of the live
    /// vessel the committed-spawned restore is about to record into it?
    ///
    /// <para>Ghost playback finds a part by the persistentId its snapshot gives it
    /// (<see cref="Recording.GhostVisualSnapshot"/>, else <see cref="Recording.VesselSnapshot"/>,
    /// the same order the INV4 analyzer rule reads). A Parsek spawn regenerates every part
    /// persistentId (<c>VesselSpawner.RegenerateVesselIdentity</c>), so a spawned vessel's
    /// part events never resolve against the snapshot of the recording that spawned it. The
    /// check keys on part pids, never on the recording's spawn stamp: after one resume and a
    /// re-commit a genuine spawn reads like an adoption stamp (spawned pid == vessel pid).</para>
    ///
    /// <para>Fail-open toward today's behaviour: no live parts, no snapshot, or a snapshot
    /// with no part pids all answer covered, because nothing can be compared. A live vessel
    /// that only lost parts since the snapshot (staging) is covered; one that holds a part the
    /// snapshot does not is not.</para>
    /// </summary>
    internal static class ResumedSpawnPartCoverage
    {
        internal const string ReasonNoLiveParts = "no-live-parts";
        internal const string ReasonNoSnapshot = "no-snapshot";
        internal const string ReasonSnapshotWithoutParts = "snapshot-without-parts";
        internal const string ReasonAllCovered = "all-covered";
        internal const string ReasonUncoveredParts = "uncovered-parts";

        /// <summary>Reads the target's ghost snapshot, else its vessel snapshot.</summary>
        internal static ResumedPartCoverage Evaluate(Recording target, IList<uint> livePartPids)
        {
            ConfigNode snapshot = null;
            string source = "none";
            if (target != null && target.GhostVisualSnapshot != null)
            {
                snapshot = target.GhostVisualSnapshot;
                source = "ghost";
            }
            else if (target != null && target.VesselSnapshot != null)
            {
                snapshot = target.VesselSnapshot;
                source = "vessel";
            }
            return Evaluate(snapshot, source, livePartPids);
        }

        internal static ResumedPartCoverage Evaluate(
            ConfigNode snapshot, string snapshotSource, IList<uint> livePartPids)
        {
            var result = new ResumedPartCoverage
            {
                Covered = true,
                SnapshotSource = snapshot != null ? (snapshotSource ?? "unknown") : "none",
            };

            var live = new HashSet<uint>();
            if (livePartPids != null)
            {
                for (int i = 0; i < livePartPids.Count; i++)
                {
                    if (livePartPids[i] != 0)
                        live.Add(livePartPids[i]);
                }
            }
            result.LivePartCount = live.Count;

            var held = new HashSet<uint>(VesselSnapshotOps.CollectPartPersistentIds(snapshot));
            result.SnapshotPartCount = held.Count;

            if (live.Count == 0)
            {
                result.Reason = ReasonNoLiveParts;
                return result;
            }
            if (snapshot == null)
            {
                result.Reason = ReasonNoSnapshot;
                return result;
            }
            if (held.Count == 0)
            {
                result.Reason = ReasonSnapshotWithoutParts;
                return result;
            }

            int uncovered = 0;
            foreach (uint pid in live)
            {
                if (!held.Contains(pid))
                    uncovered++;
            }
            result.UncoveredPartCount = uncovered;
            result.Covered = uncovered == 0;
            result.Reason = uncovered == 0 ? ReasonAllCovered : ReasonUncoveredParts;
            return result;
        }
    }
}
