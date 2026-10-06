using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHAIN-TIP-ADOPTS-STALE-VESSEL-OUTSIDE-FLIGHT: a chain tip whose spawn UT passes outside
    /// the flight chain path must replace the live pre-claim original, not adopt it. Fixture:
    /// a transport (pid 500) docks to a station (pid 777) and undocks; the station half keeps
    /// the station's pid and launch guid and is the chain tip; a live vessel with pid 777 and
    /// that guid, last simulated at UT 900 (before the dock at 1500), is the station as the
    /// rewind left it.
    /// </summary>
    [Collection("Sequential")]
    public class ChainTipStaleVesselTests : IDisposable
    {
        private const uint StationPid = 777u;
        private const uint TransportPid = 500u;
        private const string StationGuid = "5a7e11002b3c4d5e8f90a1b2c3d4e5f6";
        private const string TransportGuid = "7a7e11002b3c4d5e8f90a1b2c3d4e5f6";
        private const double DockUT = 1500.0;
        private const double PreClaimLastUT = 900.0;

        private readonly List<string> logLines = new List<string>();
        private readonly List<uint> despawned = new List<uint>();
        private bool stationLive = true;
        private double stationLastUT = PreClaimLastUT;

        public ChainTipStaleVesselTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GhostMapPresence.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselGuidResolverOverrideForTesting();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            ChainTipStaleVessel.ResetForTesting();
            ParsekSettingsPersistence.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // The live station, as the rewind left it: pid 777, the station's launch guid.
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == StationPid && stationLive);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => pid == StationPid ? StationGuid : null);
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == StationPid && stationLive);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => pid == StationPid ? StationGuid : null);
            ChainTipStaleVessel.LiveVesselInUseOverrideForTesting = _ => false;
            ChainTipStaleVessel.LiveVesselLastUTOverrideForTesting =
                pid => pid == StationPid ? stationLastUT : double.NaN;
            ChainTipStaleVessel.DespawnOverrideForTesting = (pid, scene) =>
            {
                despawned.Add(pid);
                stationLive = false;
                return true;
            };
        }

        public void Dispose()
        {
            ChainTipStaleVessel.ResetForTesting();
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselGuidResolverOverrideForTesting();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            GhostMapPresence.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekSettingsPersistence.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region Fixture

        private static Recording MakeRecording(
            string id, uint pid, string guid, double startUT, double endUT,
            TerminalState? terminal, string parentBpId, string childBpId, string name)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "ORBITING");
            snapshot.AddValue("type", "Station");
            snapshot.AddValue("name", name);
            return new Recording
            {
                RecordingId = id,
                TreeId = "tree-transport",
                VesselName = name,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                ParentBranchPointId = parentBpId,
                ChildBranchPointId = childBpId,
                VesselSnapshot = snapshot,
                PlaybackEnabled = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT, bodyName = "Kerbin", altitude = 100000 },
                    new TrajectoryPoint { ut = endUT, bodyName = "Kerbin", altitude = 100000 }
                }
            };
        }

        /// <summary>
        /// Transport pre-dock -> Dock(target 777) -> merged (777) -> Undock -> station half
        /// (777, the chain tip) + transport half (900). Commits the tree and returns the tip.
        /// </summary>
        private static Recording CommitDockUndockTree(TerminalState tipTerminal = TerminalState.Orbiting)
        {
            var transport = MakeRecording("tr-predock", TransportPid, TransportGuid, 1000, 1500,
                TerminalState.Docked, null, "bp-dock", "Transport");
            var merged = MakeRecording("merged", StationPid, StationGuid, 1500, 1600,
                null, "bp-dock", "bp-undock", "Station");
            var stationTip = MakeRecording("station-tip", StationPid, StationGuid, 1600, 1700,
                tipTerminal, "bp-undock", null, "Station");
            var transportHalf = MakeRecording("tr-half", 900u, TransportGuid, 1600, 1800,
                TerminalState.Orbiting, "bp-undock", null, "Transport");

            var tree = new RecordingTree
            {
                Id = "tree-transport",
                TreeName = "Transport",
                RootRecordingId = transport.RecordingId
            };
            tree.AddOrReplaceRecording(transport);
            tree.AddOrReplaceRecording(merged);
            tree.AddOrReplaceRecording(stationTip);
            tree.AddOrReplaceRecording(transportHalf);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                Type = BranchPointType.Dock,
                UT = DockUT,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { transport.RecordingId },
                ChildRecordingIds = new List<string> { merged.RecordingId }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-undock",
                Type = BranchPointType.Undock,
                UT = 1600,
                ParentRecordingIds = new List<string> { merged.RecordingId },
                ChildRecordingIds = new List<string> { stationTip.RecordingId, transportHalf.RecordingId }
            });
            RecordingStore.AddCommittedTreeForTesting(tree);
            return stationTip;
        }

        /// <summary>The playhead was seen before the tip in this session (a rewind or a load).</summary>
        private static void LatchRewoundBefore(Recording rec)
        {
            PlaybackScopeTracker.NotePlayhead(rec.RecordingId, rec.StartUT - 600, rec.StartUT);
            Assert.True(PlaybackScopeTracker.WasPlayheadSeenBeforeActivation(rec.RecordingId));
        }

        private static Dictionary<uint, GhostChain> Chains()
        {
            return GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 0.0);
        }

        #endregion

        #region Pure predicate

        [Fact]
        public void Fixture_StationHalfIsTheTipOfTheStationChain()
        {
            Recording tip = CommitDockUndockTree();
            var chains = Chains();

            GhostChain chain = GhostChainWalker.FindChainForVessel(chains, StationPid);
            Assert.NotNull(chain);
            Assert.Equal(tip.RecordingId, chain.TipRecordingId);
            Assert.False(chain.IsTerminated);
        }

        [Fact]
        public void Predicate_TipOfActiveChainWithStalePreClaimVessel_Replaces()
        {
            Recording tip = CommitDockUndockTree();

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), liveSameLaunchVesselExists: true, playheadSeenBeforeTip: true,
                liveVesselLastUT: PreClaimLastUT, liveVesselInUse: false, out string reason);

            Assert.True(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonReplace, reason);
        }

        [Fact]
        public void Predicate_AlreadySpawnedTip_LeavesTheSiteUnchanged()
        {
            Recording tip = CommitDockUndockTree();
            tip.VesselSpawned = true;
            tip.SpawnedVesselPersistentId = StationPid;

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), true, true, PreClaimLastUT, false, out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonTipSpawned, reason);
        }

        [Fact]
        public void Predicate_RecordingOutsideAnyChain_LeavesTheSiteUnchanged()
        {
            CommitDockUndockTree();
            var standalone = MakeRecording("standalone", 4242u, StationGuid, 100, 200,
                TerminalState.Orbiting, null, null, "Probe");

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                standalone, Chains(), true, true, PreClaimLastUT, false, out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonNotChainTip, reason);
        }

        [Fact]
        public void Predicate_IntermediateLinkOnTheClaimedPid_IsNotReplaced()
        {
            CommitDockUndockTree();
            Recording merged = RecordingStore.CommittedTrees[0].Recordings["merged"];

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                merged, Chains(), true, true, PreClaimLastUT, false, out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonNotChainTip, reason);
        }

        [Fact]
        public void Predicate_TerminatedChain_LeavesTheSiteUnchanged()
        {
            Recording tip = CommitDockUndockTree(TerminalState.Destroyed);
            var chains = Chains();
            Assert.True(GhostChainWalker.FindChainForVessel(chains, StationPid).IsTerminated);

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, chains, true, true, PreClaimLastUT, false, out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonTerminated, reason);
        }

        [Fact]
        public void Predicate_NeverRewoundBeforeTheTip_KeepsAdoption()
        {
            // Normal forward play: the live station IS the vessel the tip recorded.
            Recording tip = CommitDockUndockTree();

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), true, playheadSeenBeforeTip: false, liveVesselLastUT: PreClaimLastUT,
                liveVesselInUse: false, out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonNotRewoundBeforeTip, reason);
        }

        [Fact]
        public void Predicate_LiveVesselSimulatedAtOrAfterTheClaim_KeepsAdoption()
        {
            // The vessel that went through the dock was in physics at the dock, so its lastUT
            // is at least the claim UT: it carries the recorded change and is adopted.
            Recording tip = CommitDockUndockTree();

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), true, true, liveVesselLastUT: DockUT, liveVesselInUse: false,
                out string atClaim));
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, atClaim);
            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), true, true, liveVesselLastUT: 1650.0, liveVesselInUse: false,
                out string afterTip));
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, afterTip);
        }

        [Fact]
        public void Predicate_UnknownLastSimulatedUT_KeepsAdoption()
        {
            Recording tip = CommitDockUndockTree();

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), true, true, liveVesselLastUT: double.NaN, liveVesselInUse: false,
                out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, reason);
        }

        [Fact]
        public void Predicate_NeverLoadedVessel_IsStale()
        {
            // KSP writes lastUT = -1 for a vessel never simulated; it cannot carry the change.
            Recording tip = CommitDockUndockTree();

            Assert.True(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), true, true, liveVesselLastUT: -1.0, liveVesselInUse: false,
                out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonReplace, reason);
        }

        [Fact]
        public void LatestClaimUT_IsTheLastLinkAcrossUnsortedLinks()
        {
            var chain = new GhostChain();
            chain.Links.Add(new ChainLink { recordingId = "b", ut = 2000.0 });
            chain.Links.Add(new ChainLink { recordingId = "a", ut = 1500.0 });

            Assert.Equal(2000.0, ChainTipStaleVessel.LatestClaimUT(chain));
            Assert.True(double.IsNaN(ChainTipStaleVessel.LatestClaimUT(new GhostChain())));
            Assert.True(double.IsNaN(ChainTipStaleVessel.LatestClaimUT(null)));
        }

        [Fact]
        public void Predicate_TwoDocksLiveVesselOnlyThroughTheFirst_IsStale()
        {
            // A second committed mission docks at 2500: a station loaded from between the two
            // docks went through the first (lastUT 1550) but not the second, so it is stale.
            Recording tip = CommitDockUndockTree();
            var chains = Chains();
            GhostChain chain = GhostChainWalker.FindChainForVessel(chains, StationPid);
            chain.Links.Add(new ChainLink { recordingId = "second-dock", ut = 2500.0 });

            Assert.True(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, chains, true, true, liveVesselLastUT: 1550.0, liveVesselInUse: false,
                out string between));
            Assert.Equal(ChainTipStaleVessel.ReasonReplace, between);
            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, chains, true, true, liveVesselLastUT: 2600.0, liveVesselInUse: false,
                out string after));
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, after);
        }

        [Fact]
        public void Predicate_LiveVesselInThePlayersHands_KeepsAdoption()
        {
            Recording tip = CommitDockUndockTree();

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), true, true, PreClaimLastUT, liveVesselInUse: true, out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonInUse, reason);
        }

        [Fact]
        public void Predicate_NoLiveSameLaunchVessel_LeavesTheSiteUnchanged()
        {
            Recording tip = CommitDockUndockTree();

            bool replace = ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), liveSameLaunchVesselExists: false, playheadSeenBeforeTip: true,
                liveVesselLastUT: PreClaimLastUT, liveVesselInUse: false, out string reason);

            Assert.False(replace);
            Assert.Equal(ChainTipStaleVessel.ReasonNoLiveVessel, reason);
        }

        #endregion

        #region Tracking Station hand-off

        [Fact]
        public void TrackingStationHandoff_StalePreClaimVesselLive_ReplacesItInsteadOfAdopting()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            bool? spawnedWithPreservedIdentity = null;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
            {
                spawnedWithPreservedIdentity = preserveIdentity;
                rec.VesselSpawned = true;
                rec.SpawnedVesselPersistentId = StationPid;
            };

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.True(handled);
            Assert.Equal(new List<uint> { StationPid }, despawned);
            Assert.True(spawnedWithPreservedIdentity);
            Assert.True(tip.VesselSpawned);
            Assert.DoesNotContain(logLines, l => l.Contains("skipped duplicate spawn"));
            Assert.Contains(logLines, l => l.Contains("[ChainTip]")
                && l.Contains("Replacing stale pre-claim vessel (TRACKSTATION)")
                && l.Contains("rec=station-tip"));
        }

        [Fact]
        public void TrackingStationHandoff_NeverRewoundBeforeTheTip_AdoptsAsBefore()
        {
            Recording tip = CommitDockUndockTree();
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.True(handled);
            Assert.Empty(despawned);
            Assert.False(spawnCalled);
            Assert.True(tip.VesselSpawned);
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("skipped duplicate spawn"));
        }

        [Fact]
        public void TrackingStationHandoff_CommitSecondsAfterTipStart_AdoptsTheRealVessel()
        {
            // A merge-dialog commit made a second after the undock latches the tip into
            // replay scope (activation tolerance), but the playhead never stood before the
            // tip: the live station is the vessel the tip recorded, so it is adopted.
            Recording tip = CommitDockUndockTree();
            PlaybackScopeTracker.NotePlayhead(tip.RecordingId, tip.StartUT + 1.0, tip.StartUT);
            Assert.True(PlaybackScopeTracker.IsInReplayScope(tip.RecordingId));
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.Empty(despawned);
            Assert.False(spawnCalled);
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
        }

        [Fact]
        public void TrackingStationHandoff_RewoundThenLoadedASaveFromAfterTheChange_AdoptsTheRealVessel()
        {
            // The session rewound before the tip (strict latch set), then loaded a save from
            // the original timeline: the live station there was simulated through the dock.
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            stationLastUT = 1650.0;
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.Empty(despawned);
            Assert.False(spawnCalled);
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("[ChainTip]")
                && l.Contains("reason=" + ChainTipStaleVessel.ReasonSimulatedSinceClaim));
        }

        [Fact]
        public void TrackingStationHandoff_RemovalFails_AdoptsAsBefore()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            ChainTipStaleVessel.DespawnOverrideForTesting = (pid, scene) => false;
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.False(spawnCalled);
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("[ChainTip]")
                && l.Contains("Stale chain-tip vessel removal failed (TRACKSTATION)"));
        }

        #endregion

        #region Space Center end spawn and flight leaf spawn (shared entry)

        [Fact]
        public void SpaceCenterEndSpawn_StalePreClaimVesselLive_ReplacesItInsteadOfAdopting()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);

            bool replaced = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 4, out StaleChainVesselFocus focus);

            Assert.True(replaced);
            Assert.Equal(new List<uint> { StationPid }, despawned);
            // The site's adoption check that follows now finds nothing to adopt.
            Assert.False(VesselSpawner.TryAdoptExistingSourceVesselForSpawn(tip, "KSCSpawn", "test"));
            Assert.Equal(0u, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("Replacing stale pre-claim vessel (SPACECENTER) #4"));
        }

        [Fact]
        public void SpaceCenterEndSpawn_NeverRewoundBeforeTheTip_AdoptsAsBefore()
        {
            Recording tip = CommitDockUndockTree();

            bool replaced = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 4, out StaleChainVesselFocus focus);

            Assert.False(replaced);
            Assert.Empty(despawned);
            Assert.True(VesselSpawner.TryAdoptExistingSourceVesselForSpawn(tip, "KSCSpawn", "test"));
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
        }

        [Fact]
        public void SpaceCenterEndSpawn_DifferentLaunchOnTheBakedPid_IsNeitherReplacedNorAdopted()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(
                pid => pid == StationPid ? "ffffffffffffffffffffffffffffffff" : null);

            bool replaced = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 4, out StaleChainVesselFocus focus);

            Assert.False(replaced);
            Assert.Empty(despawned);
        }

        [Fact]
        public void FlightLeafSpawn_ReplayBypass_NeverReplaces()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);

            bool replaced = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "FLIGHT", 1, out StaleChainVesselFocus focus, allowExistingSourceDuplicate: true);

            Assert.False(replaced);
            Assert.Empty(despawned);
        }

        [Fact]
        public void FlightLeafSpawn_StalePreClaimVesselInUse_AdoptsAsBeforeAndSaysWhy()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            ChainTipStaleVessel.LiveVesselInUseOverrideForTesting = pid => pid == StationPid;

            bool replaced = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "FLIGHT", 1, out StaleChainVesselFocus focus);

            Assert.False(replaced);
            Assert.Empty(despawned);
            Assert.Contains(logLines, l => l.Contains("[INFO][ChainTip]")
                && l.Contains("Stale chain-tip vessel kept (FLIGHT)"));
        }

        [Fact]
        public void FlightLeafSpawn_PastSpawnUTWithStaleVessel_ReplacesInsteadOfAdopting()
        {
            // The ordinary flight leaf path (no active ghost chain: the flight loaded past
            // the tip's spawn UT). SpawnAttempts at the cap stops the shared spawn helper right
            // after its adoption check, before any KSP call.
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            tip.SpawnAttempts = 3;
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            MethodInfo spawn = typeof(ParsekFlight).GetMethod(
                "SpawnVesselOrChainTip", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(spawn);

            spawn.Invoke(host, new object[] { tip, 0 });

            Assert.Equal(new List<uint> { StationPid }, despawned);
            Assert.False(tip.VesselSpawned);
            Assert.Equal(0u, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("Replacing stale pre-claim vessel (FLIGHT) #0"));
        }

        #endregion
    }
}
