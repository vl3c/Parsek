using System;
using System.Collections.Generic;
using Parsek.Patches;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Far watch step 2 (docs/dev/design-far-watch.md 17.1): the drone identity, the ghost-or-
    /// drone guard, the player-ship predicate, the save-strip index repair, the switch token
    /// rule, the transitions and the entry refusal.
    /// </summary>
    [Collection("Sequential")]
    public class CameraDroneIdentityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private static readonly Guid DroneGuid = new Guid("11111111-2222-3333-4444-555555555555");
        private static readonly Guid ShipGuid = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        public CameraDroneIdentityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            CameraDroneIdentity.ResetForTesting();
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            CameraDroneIdentity.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void Empty_IdentityMatchesNothing()
        {
            Assert.False(CameraDroneIdentity.Exists);
            Assert.False(CameraDroneIdentity.IsDrone(0u));
            Assert.False(CameraDroneIdentity.IsDrone(0u, Guid.Empty));
            Assert.False(CameraDroneIdentity.TryGetPlayerShip(out _, out _));
        }

        [Fact]
        public void SetDrone_MatchesPidAndGuid_LogsIdentity()
        {
            CameraDroneIdentity.SetDrone(4242u, DroneGuid);
            Assert.True(CameraDroneIdentity.Exists);
            Assert.True(CameraDroneIdentity.IsDrone(4242u));
            Assert.True(CameraDroneIdentity.IsDrone(4242u, DroneGuid));
            // A craft-baked pid collision with another launch is not the drone.
            Assert.False(CameraDroneIdentity.IsDrone(4242u, ShipGuid));
            Assert.False(CameraDroneIdentity.IsDrone(4242u, Guid.Empty));
            Assert.False(CameraDroneIdentity.IsDrone(4243u, DroneGuid));
            Assert.Contains(logLines, l => l.Contains("[CameraDrone]") && l.Contains("identity set dronePid=4242"));
        }

        [Fact]
        public void PlayerShip_NeedsDroneAndGuid()
        {
            CameraDroneIdentity.SetShip(77u, ShipGuid);
            Assert.False(CameraDroneIdentity.TryGetPlayerShip(out _, out _));
            CameraDroneIdentity.SetDrone(4242u, DroneGuid);
            Assert.True(CameraDroneIdentity.TryGetPlayerShip(out uint pid, out Guid guid));
            Assert.Equal(77u, pid);
            Assert.Equal(ShipGuid, guid);
            Assert.True(FarWatchPlayerVessel.IsPlayerShip(77u, ShipGuid));
            Assert.False(FarWatchPlayerVessel.IsPlayerShip(77u, Guid.Empty));
            Assert.False(FarWatchPlayerVessel.IsPlayerShip(78u, ShipGuid));
        }

        [Fact]
        public void StripPending_KeepsIdentityUntilAfterStrip()
        {
            CameraDroneIdentity.MarkStripPending();
            Assert.False(CameraDroneIdentity.StripPending); // nothing to keep
            CameraDroneIdentity.SetDrone(4242u, DroneGuid);
            CameraDroneIdentity.SetShip(77u, ShipGuid);
            CameraDroneIdentity.ClearAfterStrip("not-pending");
            Assert.True(CameraDroneIdentity.Exists);
            CameraDroneIdentity.MarkStripPending();
            Assert.True(CameraDroneIdentity.StripPending);
            CameraDroneIdentity.ClearAfterStrip("save");
            Assert.False(CameraDroneIdentity.Exists);
            Assert.False(CameraDroneIdentity.StripPending);
            Assert.Contains(logLines, l => l.Contains("identity cleared reason=after-strip:save"));
        }

        [Fact]
        public void ClearOnLoad_DropsEverythingIncludingTheToken()
        {
            CameraDroneIdentity.SetDrone(4242u, DroneGuid);
            CameraDroneIdentity.BeginSwitch();
            Assert.True(CameraDroneIdentity.SwitchTokenHeld);
            CameraDroneIdentity.ClearOnLoad("OnLoad");
            Assert.False(CameraDroneIdentity.Exists);
            Assert.False(CameraDroneIdentity.SwitchTokenHeld);
            Assert.Contains(logLines, l => l.Contains("identity cleared reason=load:OnLoad"));
        }

        [Fact]
        public void ClearOnLoad_NothingSet_LogsNothing()
        {
            CameraDroneIdentity.ClearOnLoad("OnLoad");
            Assert.DoesNotContain(logLines, l => l.Contains("identity cleared"));
        }

        [Fact]
        public void IsGhostOrDroneVessel_CoversBothSets()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(900u);
            Assert.True(GhostMapPresence.IsGhostOrDroneVessel(900u));
            Assert.False(GhostMapPresence.IsGhostOrDroneVessel(4242u));
            Assert.False(CameraDroneIdentity.IsDrone(4242u));
            CameraDroneIdentity.SetDrone(4242u, DroneGuid);
            Assert.True(GhostMapPresence.IsGhostOrDroneVessel(4242u));
            Assert.True(CameraDroneIdentity.IsDrone(4242u));
            // A drone is not a ghost map vessel: the map-only guards keep their meaning.
            Assert.False(GhostMapPresence.IsGhostMapVessel(4242u));
            Assert.False(GhostMapPresence.IsGhostOrDroneVessel(0u));
        }
    }

    public class FarWatchSaveStripIndexTests
    {
        private static readonly Guid A = new Guid("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid B = new Guid("00000000-0000-0000-0000-00000000000b");
        private static readonly Guid Ship = new Guid("00000000-0000-0000-0000-0000000000cc");
        private static readonly Guid Drone = new Guid("00000000-0000-0000-0000-0000000000dd");

        [Fact]
        public void NoDrone_IsTheGhostOnlyResolver()
        {
            var pids = new List<uint> { 1u, 900u, 2u };
            var guids = new List<Guid> { A, B, Ship };
            int idx = GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(pids, guids, new HashSet<uint> { 900u },
                2, 0u, Guid.Empty, Ship, out int removed, out GhostMapPresence.ActiveVesselIdxOutcome outcome);
            Assert.Equal(1, idx);
            Assert.Equal(1, removed);
            Assert.Equal(GhostMapPresence.ActiveVesselIdxOutcome.Shifted, outcome);
        }

        [Fact]
        public void Drone_TargetsTheShipByGuidPastGhostsAndTheDrone()
        {
            // The captured index names the drone after the hand-over; it is never trusted.
            var pids = new List<uint> { 900u, 5u, 1u, 77u };
            var guids = new List<Guid> { A, Drone, B, Ship };
            int idx = GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(pids, guids, new HashSet<uint> { 900u },
                1, 5u, Drone, Ship, out int removed, out GhostMapPresence.ActiveVesselIdxOutcome outcome);
            Assert.Equal(1, idx); // [1(B), 77(Ship)] after the strip
            Assert.Equal(2, removed);
            Assert.Equal(GhostMapPresence.ActiveVesselIdxOutcome.ShipByGuid, outcome);
        }

        [Fact]
        public void Drone_ZeroCapturedIndex_StillFindsTheShip()
        {
            var pids = new List<uint> { 77u, 5u };
            var guids = new List<Guid> { Ship, Drone };
            int idx = GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(pids, guids, new HashSet<uint>(),
                0, 5u, Drone, Ship, out int removed, out GhostMapPresence.ActiveVesselIdxOutcome outcome);
            Assert.Equal(0, idx);
            Assert.Equal(0, removed);
            Assert.Equal(GhostMapPresence.ActiveVesselIdxOutcome.ShipByGuid, outcome);
        }

        [Fact]
        public void Drone_ShipMissing_FailsClosed()
        {
            var pids = new List<uint> { 1u, 5u };
            var guids = new List<Guid> { A, Drone };
            int idx = GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(pids, guids, new HashSet<uint>(),
                1, 5u, Drone, Ship, out _, out GhostMapPresence.ActiveVesselIdxOutcome outcome);
            Assert.Equal(-1, idx);
            Assert.Equal(GhostMapPresence.ActiveVesselIdxOutcome.ShipMissing, outcome);
        }

        [Fact]
        public void Drone_PidCollisionWithAnotherLaunch_IsKept()
        {
            // Same baked pid as the drone, a different guid: not removed, counted as a survivor.
            var pids = new List<uint> { 5u, 5u, 77u };
            var guids = new List<Guid> { A, Drone, Ship };
            int idx = GhostMapPresence.ResolveActiveVesselIdxAfterGhostStrip(pids, guids, new HashSet<uint>(),
                1, 5u, Drone, Ship, out int removed, out _);
            Assert.Equal(1, idx);
            Assert.Equal(1, removed);
            Assert.True(GhostMapPresence.IsDroneProtoVessel(5u, Drone, 5u, Drone));
            Assert.False(GhostMapPresence.IsDroneProtoVessel(5u, A, 5u, Drone));
            Assert.False(GhostMapPresence.IsDroneProtoVessel(5u, Guid.Empty, 5u, Guid.Empty));
        }
    }

    public class VesselSwitchTokenRuleTests
    {
        [Theory]
        // droneExists, stripPending, tokenHeld, targetIsShip, droneLive, refuse
        [InlineData(false, false, false, false, false, false)]
        [InlineData(true, false, false, false, true, true)]
        [InlineData(true, false, true, false, true, false)]
        [InlineData(true, true, false, false, true, false)]
        [InlineData(true, false, false, true, true, true)]
        [InlineData(true, false, false, true, false, false)]
        [InlineData(true, false, false, false, false, true)]
        public void ShouldRefuseSwitch_Table(bool exists, bool strip, bool token, bool ship, bool live, bool refuse)
        {
            Assert.Equal(refuse, CameraDroneSwitchTokenPatch.ShouldRefuseSwitch(exists, strip, token, ship, live));
        }
    }

    public class FarWatchTransitionTests
    {
        [Theory]
        [InlineData("Idle", "StartAdmitted", "Spawning")]
        [InlineData("Spawning", "DroneLoaded", "HandoverOut")]
        [InlineData("Spawning", "SpawnFailed", "Aborted")]
        [InlineData("Spawning", "EndRequested", "Aborted")]
        [InlineData("HandoverOut", "SwitchedToDrone", "Following")]
        [InlineData("HandoverOut", "SwitchOutFailed", "Aborted")]
        [InlineData("HandoverOut", "EndRequested", "HandoverBack")]
        [InlineData("Following", "EndRequested", "HandoverBack")]
        [InlineData("Following", "DroneLost", "Aborted")]
        [InlineData("Following", "ShipLost", "Aborted")]
        [InlineData("HandoverBack", "HandbackSwitched", "Teardown")]
        [InlineData("HandoverBack", "ShipNeverLoaded", "Aborted")]
        [InlineData("Teardown", "TeardownComplete", "Idle")]
        [InlineData("Aborted", "AbortToTeardown", "Teardown")]
        [InlineData("Aborted", "AbortResolved", "Idle")]
        [InlineData("Following", "SceneExit", "Idle")]
        [InlineData("Teardown", "SceneExit", "Idle")]
        public void LegalPairs(string fromName, string triggerName, string expectedName)
        {
            var from = (FarWatchState)Enum.Parse(typeof(FarWatchState), fromName);
            var trigger = (FarWatchTrigger)Enum.Parse(typeof(FarWatchTrigger), triggerName);
            var expected = (FarWatchState)Enum.Parse(typeof(FarWatchState), expectedName);
            Assert.True(FarWatchTransitions.TryTransition(from, trigger, out FarWatchState to));
            Assert.Equal(expected, to);
        }

        [Theory]
        [InlineData("Idle", "SceneExit")]
        [InlineData("Idle", "EndRequested")]
        [InlineData("Following", "StartAdmitted")]
        [InlineData("Teardown", "EndRequested")]
        [InlineData("HandoverBack", "EndRequested")]
        public void IllegalPairs_KeepTheState(string fromName, string triggerName)
        {
            var from = (FarWatchState)Enum.Parse(typeof(FarWatchState), fromName);
            var trigger = (FarWatchTrigger)Enum.Parse(typeof(FarWatchTrigger), triggerName);
            Assert.False(FarWatchTransitions.TryTransition(from, trigger, out FarWatchState to));
            Assert.Equal(from, to);
        }

        [Fact]
        public void IsRunning_EverythingButIdle()
        {
            foreach (FarWatchState s in Enum.GetValues(typeof(FarWatchState)))
                Assert.Equal(s != FarWatchState.Idle, FarWatchTransitions.IsRunning(s));
        }

        private static FarWatchEntryInputs Allowed()
        {
            return new FarWatchEntryInputs
            {
                InFlightScene = true,
                HasActiveVessel = true,
                GhostResolvable = true,
                FollowGhost = true,
            };
        }

        [Fact]
        public void EntryRefusal_AllowedBaseline()
        {
            Assert.Null(FarWatchTransitions.EvaluateEntryRefusal(Allowed()));
        }

        [Fact]
        public void EntryRefusal_NoBodyTerm_FollowAndHoldAllowedAtAnyBody()
        {
            // Step 4: the follow watch moves the drone to the ghost's body, so the entry has no
            // body term (the old ghost-other-body refusal is gone).
            FarWatchEntryInputs i = Allowed();
            Assert.Null(FarWatchTransitions.EvaluateEntryRefusal(i));
            i.FollowGhost = false;
            Assert.Null(FarWatchTransitions.EvaluateEntryRefusal(i));
            i.GhostResolvable = false;
            Assert.Equal("ghost-not-resolvable", FarWatchTransitions.EvaluateEntryRefusal(i));
        }

        [Fact]
        public void EntryRefusal_OrderAndEachWindow()
        {
            FarWatchEntryInputs i = Allowed();
            i.ReFlyActive = true;
            Assert.Equal("refly-active", FarWatchTransitions.EvaluateEntryRefusal(i));
            i.PendingSplitInProgress = true;
            Assert.Equal("pending-split", FarWatchTransitions.EvaluateEntryRefusal(i));
            i.ActiveVesselIsEva = true;
            Assert.Equal("active-is-eva", FarWatchTransitions.EvaluateEntryRefusal(i));
            i.AlreadyActive = true;
            Assert.Equal("already-active", FarWatchTransitions.EvaluateEntryRefusal(i));
            i.InFlightScene = false;
            Assert.Equal("not-flight", FarWatchTransitions.EvaluateEntryRefusal(i));

            void Check(Func<FarWatchEntryInputs, FarWatchEntryInputs> set, string reason)
            {
                Assert.Equal(reason, FarWatchTransitions.EvaluateEntryRefusal(set(Allowed())));
            }
            Check(x => { x.HasActiveVessel = false; return x; }, "no-active-vessel");
            Check(x => { x.ActiveVesselIsGhostOrDrone = true; return x; }, "active-is-ghost-or-drone");
            Check(x => { x.PendingSplitRecorder = true; return x; }, "pending-split-recorder");
            Check(x => { x.DeferredJointBreakCheckInFlight = true; return x; }, "pending-joint-break-check");
            Check(x => { x.CrashCoalescerPendingBreakup = true; return x; }, "pending-breakup");
            Check(x => { x.PendingTreeDockMerge = true; return x; }, "pending-dock-merge");
            Check(x => { x.PendingAutoRecord = true; return x; }, "pending-auto-record");
            Check(x => { x.ChainToVesselPending = true; return x; }, "pending-board-merge");
            Check(x => { x.TransitionToBackgroundPending = true; return x; }, "pending-background-transition");
            Check(x => { x.TimeJumpPlacementArmed = true; return x; }, "time-jump-placement-armed");
            Check(x => { x.TimeJumpPlacementWatched = true; return x; }, "time-jump-placement-watched");
            Check(x => { x.RestoringActiveTree = true; return x; }, "restoring-active-tree");
            Check(x => { x.GhostResolvable = false; return x; }, "ghost-not-resolvable");
        }

        [Theory]
        [InlineData(5, 5, false)]
        [InlineData(5, 6, true)]
        [InlineData(5, 3, true)]
        [InlineData(-1, 4, false)]
        [InlineData(4, -1, false)]
        public void ShipStructureChanged_OnlyOnAKnownCountChange(int before, int now, bool changed)
        {
            Assert.Equal(changed, FarWatchTransitions.ShipStructureChanged(before, now));
        }

        [Fact]
        public void TransitionLine_IsInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string line = FarWatchTransitions.FormatTransitionLine(FarWatchState.HandoverOut, FarWatchState.Following,
                    FarWatchEndReason.None, null, "DD1", 77u, 4242u, 1234.5);
                Assert.Equal("FarWatch transition from=HandoverOut to=Following reason=None ghostRec=(none) ship=DD1 " +
                    "shipPid=77 dronePid=4242 ut=1234.50", line);
                Assert.Equal("ticks=3 writes=2 skips=1 floorClamps=0 maxDevM=1.5 holdRenewals=3",
                    FarWatchTransitions.FormatTickSummary(3, 2, 1, 0, 1.5, 3));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
