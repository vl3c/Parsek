using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The ghost map vessel primitive's guard state: the set of persistentIds that belong to a
    /// Parsek ghost map ProtoVessel, and the depth of the Parsek ghost teardown scopes on the
    /// stack. <see cref="GhostMapPresence"/> writes and reads both through its own entry points
    /// (<c>ghostMapVesselPids</c>, <c>IsGhostMapVessel</c>, <c>BeginGhostTeardown</c> and the
    /// rest), which forward here, so every caller keeps the same object and the same calls.
    /// </summary>
    internal static class GhostMapVessels
    {
        /// <summary>
        /// PID tracking set: the canonical ghost vessel identification.
        /// Every guard in the codebase checks this for O(1) exclusion.
        /// </summary>
        internal static readonly HashSet<uint> Pids = new HashSet<uint>();

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

        /// <summary>Reset the guard set and the teardown depth for testing.</summary>
        internal static void ResetForTesting()
        {
            teardownDepth = 0;
            Pids.Clear();
        }
    }
}
