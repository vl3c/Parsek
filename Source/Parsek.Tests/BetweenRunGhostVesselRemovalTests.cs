using System;
using System.Collections.Generic;
using System.Reflection;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// INGAME-BATCH-TS-ORPHANS-GHOST-MAP-VESSELS: the in-game test runner's between-run
    /// cleanup must destroy every still-registered ghost map vessel before it clears the
    /// ghost-map bookkeeping, so the Tracking Station (no ParsekFlight) never leaves a ghost
    /// ProtoVessel in FlightGlobals.Vessels unregistered.
    /// </summary>
    public class BetweenRunGhostVesselRemovalTests
    {
        [Fact]
        public void Plan_NothingRegistered_NoRemoval()
        {
            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                0, new HashSet<uint>(), new List<uint> { 1u, 2u, 3u }, new HashSet<uint>());

            Assert.False(plan.AnyRemoval);
            Assert.False(plan.RunTrackedRemoval);
            Assert.Equal(0, plan.UntrackedLiveCount);
            Assert.Equal(0, plan.StaleRegisteredPidCount);
        }

        [Fact]
        public void Plan_FlightAfterDestroyAllTimelineGhosts_IsEmpty()
        {
            // FLIGHT: ParsekFlight.DestroyAllTimelineGhosts -> RemoveAllGhostVessels already
            // emptied the maps and the registered set; only real vessels remain live.
            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                0, new HashSet<uint>(), new List<uint> { 10u, 11u }, new HashSet<uint>());

            Assert.False(plan.AnyRemoval);
        }

        [Fact]
        public void Plan_TrackingStationMappedGhosts_RunsTrackedRemoval()
        {
            // The VB-1 reading shape: eight mapped ghosts beside eight real vessels.
            var ghostPids = new HashSet<uint>();
            var live = new List<uint>();
            for (uint i = 0; i < 8; i++)
            {
                ghostPids.Add(5000u + i);
                live.Add(100u + i);
                live.Add(5000u + i);
            }

            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                8, ghostPids, live, ghostPids);

            Assert.True(plan.AnyRemoval);
            Assert.True(plan.RunTrackedRemoval);
            Assert.Equal(8, plan.TrackedVesselCount);
            Assert.Equal(0, plan.UntrackedLiveCount);
            Assert.Equal(0, plan.StaleRegisteredPidCount);
        }

        [Fact]
        public void Plan_RegisteredButUnmappedLiveVessel_IsRemovedSeparately()
        {
            var registered = new HashSet<uint> { 7u, 8u, 9u };
            var tracked = new HashSet<uint> { 7u };
            var live = new List<uint> { 1u, 9u, 7u, 8u, 9u };

            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                1, tracked, live, registered);

            Assert.True(plan.RunTrackedRemoval);
            // Live-list order, distinct, mapped pid 7 excluded, real pid 1 never selected.
            Assert.Equal(new List<uint> { 9u, 8u }, plan.UntrackedLivePids);
            Assert.Equal(0, plan.StaleRegisteredPidCount);
        }

        [Fact]
        public void Plan_UnmappedLiveGhostAlone_StillTriggersRemoval()
        {
            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                0, new HashSet<uint>(), new List<uint> { 4u, 42u }, new HashSet<uint> { 42u });

            Assert.True(plan.AnyRemoval);
            Assert.False(plan.RunTrackedRemoval);
            Assert.Equal(new List<uint> { 42u }, plan.UntrackedLivePids);
        }

        [Fact]
        public void Plan_RegisteredPidWithNoLiveVessel_IsStaleBookkeepingOnly()
        {
            // #417/#418 shape: a ProtoVessel already killed elsewhere, its pid lingering.
            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                0, new HashSet<uint>(), new List<uint> { 1u }, new HashSet<uint> { 77u, 78u });

            Assert.False(plan.AnyRemoval);
            Assert.Equal(2, plan.StaleRegisteredPidCount);
        }

        [Fact]
        public void Plan_NullInputs_AreTolerated()
        {
            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(-3, null, null, null);
            Assert.False(plan.AnyRemoval);
            Assert.Equal(0, plan.TrackedVesselCount);

            var plan2 = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                0, null, new List<uint> { 0u, 5u }, new HashSet<uint> { 0u, 5u });
            // pid 0 is never a vessel identity.
            Assert.Equal(new List<uint> { 5u }, plan2.UntrackedLivePids);
            Assert.Equal(1, plan2.StaleRegisteredPidCount);
        }

        [Fact]
        public void Format_CarriesEveryCount()
        {
            var plan = GhostMapPresence.PlanBetweenRunGhostVesselRemoval(
                8, new HashSet<uint> { 1u }, new List<uint> { 1u, 2u }, new HashSet<uint> { 1u, 2u, 3u });

            string line = GhostMapPresence.FormatBetweenRunGhostVesselRemoval(
                "run-category:VesselBudget", "TRACKSTATION", plan, 1, 17, 8);

            Assert.Equal(
                "Between-run ghost vessel removal: reason=run-category:VesselBudget scene=TRACKSTATION "
                + "tracked=8 untrackedLive=1 untrackedRemoved=1 staleRegisteredPids=1 "
                + "liveVesselsBefore=17 liveVesselsAfter=8",
                line);
        }

        [Fact]
        public void PerformBetweenRunCleanup_RemovesGhostVesselsBeforeClearingBookkeeping()
        {
            MethodInfo cleanup = typeof(InGameTestRunner).GetMethod(
                "PerformBetweenRunCleanup", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo remove = typeof(GhostMapPresence).GetMethod(
                "RemoveRegisteredGhostVesselsBeforeTestReset", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo reset = typeof(GhostMapPresence).GetMethod(
                "ResetBetweenTestRuns", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(cleanup);
            Assert.NotNull(remove);
            Assert.NotNull(reset);

            int removeAt = FirstCallOffset(cleanup, remove);
            int resetAt = FirstCallOffset(cleanup, reset);
            Assert.True(removeAt >= 0, "PerformBetweenRunCleanup must call RemoveRegisteredGhostVesselsBeforeTestReset");
            Assert.True(resetAt >= 0, "PerformBetweenRunCleanup must call ResetBetweenTestRuns");
            Assert.True(removeAt < resetAt,
                "ghost vessels must be destroyed before the bookkeeping clear "
                + "(removeAt=" + removeAt + " resetAt=" + resetAt + ")");
        }

        /// <summary>IL offset of the first call / callvirt resolving to <paramref name="target"/>, or -1.</summary>
        private static int FirstCallOffset(MethodInfo method, MethodBase target)
        {
            var body = method.GetMethodBody();
            if (body == null) return -1;
            byte[] il = body.GetILAsByteArray();
            Module module = method.Module;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                MethodBase resolved;
                try
                {
                    resolved = module.ResolveMethod(token);
                }
                catch (ArgumentException) { continue; }
                catch (BadImageFormatException) { continue; }
                if (resolved != null && resolved.Equals(target))
                    return i;
            }
            return -1;
        }
    }
}
