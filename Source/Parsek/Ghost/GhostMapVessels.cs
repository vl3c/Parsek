using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The ghost map vessel primitive's guard state: the set of persistentIds that belong to a
    /// Parsek ghost map ProtoVessel, and the depth of the Parsek ghost teardown scopes on the
    /// stack. <see cref="GhostMapPresence"/> writes and reads both through its own entry points
    /// (<c>ghostMapVesselPids</c>, <c>IsGhostMapVessel</c>, <c>BeginGhostTeardown</c> and the
    /// rest), which forward here, so every caller keeps the same object and the same calls.
    /// Every pid also has an owner (<see cref="GhostMapVesselOwner"/>): the own timeline, or a
    /// foreign lane recorded in <see cref="foreignOwners"/>. The own-timeline teardown
    /// (<see cref="UnregisterAllOwnTimeline"/>) leaves a foreign lane's pids registered.
    /// </summary>
    internal static class GhostMapVessels
    {
        /// <summary>
        /// PID tracking set: the canonical ghost vessel identification.
        /// Every guard in the codebase checks this for O(1) exclusion.
        /// </summary>
        internal static readonly HashSet<uint> Pids = new HashSet<uint>();

        /// <summary>
        /// Owner of every registered pid that does not belong to the own timeline. A pid with
        /// no entry here is an own-timeline pid. Empty in single player, where every ghost map
        /// vessel is the own timeline's.
        /// </summary>
        private static readonly Dictionary<uint, GhostMapVesselOwner> foreignOwners =
            new Dictionary<uint, GhostMapVesselOwner>();

        /// <summary>
        /// How many Parsek ghost ProtoVessel <c>Die()</c> calls are on the stack right now.
        /// Nested because <see cref="GhostMapPresence.RemoveAllGhostVessels"/> wraps a whole
        /// loop while the single-ghost removers wrap one call each.
        ///
        /// WHY IT EXISTS: <c>Vessel.Die()</c> fires stock <c>onVesselDestroy</c> SYNCHRONOUSLY,
        /// and <c>SpaceTracking</c> handles that event by rebuilding its vessel list. When that
        /// rebuild throws, <see cref="Parsek.Patches.GhostTrackingBuildVesselsListPatch"/> has to
        /// decide whether the failure is attributable to our ghosts, and its live
        /// <c>FlightGlobals.Vessels</c> scan can itself throw during teardown, leaving the
        /// classifier with zeroed counts and no evidence. This flag is the scan-INDEPENDENT
        /// attribution signal: it is set only while OUR teardown is running.
        /// </summary>
        private static int teardownDepth;

        /// <summary>
        /// O(1) check used by all guard code throughout the codebase.
        /// Returns true if the given persistentId belongs to a ghost map ProtoVessel.
        /// </summary>
        internal static bool IsGhostMapVessel(uint persistentId)
        {
            return Pids.Contains(persistentId);
        }

        /// <summary>
        /// Registers <paramref name="pid"/> as a ghost map vessel of <paramref name="owner"/>.
        /// An own-timeline owner is the absence of an entry in <see cref="foreignOwners"/>, so
        /// only a foreign owner is recorded there.
        /// </summary>
        internal static void Register(uint pid, GhostMapVesselOwner owner)
        {
            Pids.Add(pid);
            if (!owner.IsOwnTimeline)
                foreignOwners[pid] = owner;
        }

        /// <summary>Drops <paramref name="pid"/> from the guard set and its owner record.</summary>
        internal static void Unregister(uint pid)
        {
            Pids.Remove(pid);
            foreignOwners.Remove(pid);
        }

        /// <summary>
        /// The own timeline's teardown: drops every pid that has no foreign owner and keeps the
        /// foreign lanes' pids and owners. With no foreign owner (single player) it is a whole
        /// clear of the guard set.
        /// </summary>
        internal static void UnregisterAllOwnTimeline()
        {
            if (foreignOwners.Count == 0)
            {
                Pids.Clear();
                return;
            }

            // Keeps exactly the pids that have a foreign owner; never enumerates Pids while
            // removing from it.
            Pids.IntersectWith(foreignOwners.Keys);
        }

        /// <summary>Test seam: registers <paramref name="pid"/> under a foreign lane's owner.</summary>
        internal static void RegisterForeignForTesting(uint pid, string laneId, string recordingId)
        {
            Register(pid, new GhostMapVesselOwner(laneId, recordingId));
        }

        /// <summary>Test seam: the foreign owner recorded for <paramref name="pid"/>, if any.</summary>
        internal static bool TryGetForeignOwnerForTesting(uint pid, out GhostMapVesselOwner owner)
        {
            return foreignOwners.TryGetValue(pid, out owner);
        }

        /// <summary>Test seam: how many pids have a foreign owner.</summary>
        internal static int ForeignOwnerCountForTesting
        {
            get { return foreignOwners.Count; }
        }

        /// <summary>Enter a Parsek ghost-teardown scope. Pair with <see cref="EndTeardown"/>
        /// in a <c>finally</c>; never let an exception leave the depth raised.</summary>
        internal static void BeginTeardown()
        {
            teardownDepth++;
        }

        /// <summary>Leave a Parsek ghost-teardown scope. Floors at zero so an unbalanced
        /// decrement can never latch the flag inverted for the rest of the session.</summary>
        internal static void EndTeardown()
        {
            if (teardownDepth > 0)
                teardownDepth--;
        }

        /// <summary>True while a Parsek ghost ProtoVessel <c>Die()</c> is on the stack.</summary>
        internal static bool IsTeardownInProgress
        {
            get { return teardownDepth > 0; }
        }

        /// <summary>Live count of registered ghost map ProtoVessels. Read by the
        /// buildVesselsList finalizer as evidence that survives a failed context scan.</summary>
        internal static int RegisteredCount
        {
            get { return Pids.Count; }
        }

        /// <summary>Reset the guard set, its owner record and the teardown depth for testing.</summary>
        internal static void ResetForTesting()
        {
            teardownDepth = 0;
            Pids.Clear();
            foreignOwners.Clear();
        }
    }

    /// <summary>
    /// Who a ghost map vessel pid belongs to: the own timeline (<see cref="OwnTimeline"/>, a
    /// null lane) or a foreign lane's recording. Fields, not properties.
    /// </summary>
    internal readonly struct GhostMapVesselOwner
    {
        /// <summary>The own timeline's owner: no lane, no recording.</summary>
        public static readonly GhostMapVesselOwner OwnTimeline = new GhostMapVesselOwner(null, null);

        /// <summary>The owning lane; null for the own timeline.</summary>
        public readonly string LaneId;

        /// <summary>The owning lane's recording id; null for the own timeline.</summary>
        public readonly string RecordingId;

        public GhostMapVesselOwner(string laneId, string recordingId)
        {
            LaneId = laneId;
            RecordingId = recordingId;
        }

        /// <summary>True for the own timeline (no lane).</summary>
        public bool IsOwnTimeline => LaneId == null;
    }
}
