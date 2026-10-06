using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHAIN-TIP-ADOPTS-STALE-VESSEL-OUTSIDE-FLIGHT: a chain tip whose spawn UT passes outside
    /// the flight chain path must replace the live pre-claim original, not adopt it, and a
    /// replacement must never lose the vessel. Fixture: a transport (pid 500) docks to a
    /// station (pid 777) and undocks; the station half keeps the station's pid and launch guid
    /// and is the chain tip; a live vessel with pid 777 and that guid, last simulated at UT 900
    /// (before the dock at 1500), is the station as the rewind left it. The transport-dominant
    /// shape keeps the transport's pid through the dock, so the station half gets pid 888 on
    /// undock and is the tip of the station's (pid 777) chain through its parts.
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
        private const uint StationHalfNewPid = 888u;
        private const string StationHalfNewGuid = "8a7e11002b3c4d5e8f90a1b2c3d4e5f6";

        private readonly List<string> logLines = new List<string>();
        private readonly List<uint> removed = new List<uint>();
        private readonly List<uint> restored = new List<uint>();
        private bool stationLive = true;
        private double stationLastUT = PreClaimLastUT;
        private string stationLiveGuid = StationGuid;

        // Other live vessels by pid (guid, lastUT), e.g. the stale depot of the two-claim shape.
        private readonly Dictionary<uint, KeyValuePair<string, double>> otherLive =
            new Dictionary<uint, KeyValuePair<string, double>>();
        private const uint DepotPid = 600u;
        private const string DepotGuid = "6a7e11002b3c4d5e8f90a1b2c3d4e5f6";

        private bool IsLive(uint pid)
        {
            return pid == StationPid ? stationLive : otherLive.ContainsKey(pid);
        }

        private string LiveGuidOf(uint pid)
        {
            if (pid == StationPid)
                return StationGuid;
            KeyValuePair<string, double> v;
            return otherLive.TryGetValue(pid, out v) ? v.Key : null;
        }

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
            ParsekFlight.LeafSpawnOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // The live station, as the rewind left it: pid 777, the station's launch guid.
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => IsLive(pid));
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => LiveGuidOf(pid));
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => IsLive(pid));
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => LiveGuidOf(pid));
            ChainTipStaleVessel.LiveVesselInUseOverrideForTesting = _ => false;
            ChainTipStaleVessel.LiveVesselProbeOverrideForTesting = pid =>
            {
                if (pid == StationPid && stationLive)
                    return new LiveClaimedVesselProbe { Exists = true, Guid = stationLiveGuid, LastUT = stationLastUT };
                KeyValuePair<string, double> other;
                if (otherLive.TryGetValue(pid, out other))
                    return new LiveClaimedVesselProbe { Exists = true, Guid = other.Key, LastUT = other.Value };
                return new LiveClaimedVesselProbe { LastUT = double.NaN };
            };
            ChainTipStaleVessel.RemoveOverrideForTesting = (pid, scene) =>
            {
                removed.Add(pid);
                if (pid == StationPid)
                    stationLive = false;
                else
                    otherLive.Remove(pid);
                var snapshot = new ConfigNode("VESSEL");
                snapshot.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return snapshot;
            };
            ChainTipStaleVessel.RestoreOverrideForTesting = (snapshot, pid, scene) =>
            {
                Assert.Equal(pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    snapshot.GetValue("persistentId"));
                restored.Add(pid);
                stationLive = true;
                return pid;
            };
        }

        public void Dispose()
        {
            ChainTipStaleVessel.ResetForTesting();
            ParsekFlight.LeafSpawnOverrideForTesting = null;
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
            TerminalState? terminal, string parentBpId, string childBpId, string name,
            params uint[] partPids)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "ORBITING");
            snapshot.AddValue("type", "Station");
            snapshot.AddValue("name", name);
            foreach (uint partPid in partPids)
            {
                ConfigNode part = snapshot.AddNode("PART");
                part.AddValue("persistentId", partPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
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

        /// <summary>
        /// Transport-dominant dock: transport (500, parts 5001/5002) docks to station 777 and
        /// keeps its pid on the merged vessel (500, all four parts); on undock the transport
        /// half keeps 500 and the station half (parts 7771/7772) gets the new pid 888 and guid.
        /// Main's part-identity walk makes the station half the tip of the 777 chain.
        /// </summary>
        private static Recording CommitTransportDominantTree()
        {
            var transport = MakeRecording("tr-predock", TransportPid, TransportGuid, 1000, 1500,
                TerminalState.Docked, null, "bp-dock", "Transport", 5001u, 5002u);
            var merged = MakeRecording("merged", TransportPid, TransportGuid, 1500, 1600,
                null, "bp-dock", "bp-undock", "Transport", 5001u, 5002u, 7771u, 7772u);
            var transportHalf = MakeRecording("tr-half", TransportPid, TransportGuid, 1600, 1800,
                TerminalState.Orbiting, "bp-undock", null, "Transport", 5001u, 5002u);
            var stationHalf = MakeRecording("station-half", StationHalfNewPid, StationHalfNewGuid, 1600, 1700,
                TerminalState.Orbiting, "bp-undock", null, "Station", 7771u, 7772u);

            var tree = new RecordingTree
            {
                Id = "tree-transport",
                TreeName = "Transport",
                RootRecordingId = transport.RecordingId
            };
            tree.AddOrReplaceRecording(transport);
            tree.AddOrReplaceRecording(merged);
            tree.AddOrReplaceRecording(transportHalf);
            tree.AddOrReplaceRecording(stationHalf);
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
                ChildRecordingIds = new List<string> { transportHalf.RecordingId, stationHalf.RecordingId }
            });
            RecordingStore.AddCommittedTreeForTesting(tree);
            return stationHalf;
        }

        /// <summary>
        /// One tip that ends TWO claimed vessels: a tug (300) docks to depot 600, carries it to
        /// station 777 and docks there; the final recording (777, the station dominant) ends
        /// both lineages. The walker folds the 777 chain into the 600 chain
        /// (<c>MergeCrossTreeLinks</c>), so one chain keyed 600 has a tip that carries 777. The
        /// stale depot and the stale station are both live.
        /// </summary>
        private Recording CommitTwoClaimTree()
        {
            var tug = MakeRecording("tug", 300u, TransportGuid, 1000, 1200,
                TerminalState.Docked, null, "bp-dock-depot", "Tug");
            var tugDepot = MakeRecording("tug-depot", 300u, TransportGuid, 1200, 1500,
                TerminalState.Docked, "bp-dock-depot", "bp-dock-station", "Tug");
            var final = MakeRecording("final", StationPid, StationGuid, 1500, 1700,
                TerminalState.Orbiting, "bp-dock-station", null, "Station");

            var tree = new RecordingTree
            {
                Id = "tree-transport",
                TreeName = "Tug",
                RootRecordingId = tug.RecordingId
            };
            tree.AddOrReplaceRecording(tug);
            tree.AddOrReplaceRecording(tugDepot);
            tree.AddOrReplaceRecording(final);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock-depot",
                Type = BranchPointType.Dock,
                UT = 1200,
                TargetVesselPersistentId = DepotPid,
                ParentRecordingIds = new List<string> { tug.RecordingId },
                ChildRecordingIds = new List<string> { tugDepot.RecordingId }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock-station",
                Type = BranchPointType.Dock,
                UT = DockUT,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { tugDepot.RecordingId },
                ChildRecordingIds = new List<string> { final.RecordingId }
            });
            RecordingStore.AddCommittedTreeForTesting(tree);
            otherLive[DepotPid] = new KeyValuePair<string, double>(DepotGuid, PreClaimLastUT);
            return final;
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

        /// <summary>The evidence of the stale station: live, rewound before, pre-claim, spawnable, free.</summary>
        private static StaleVesselEvidence Stale(
            bool liveExists = true,
            bool seenBefore = true,
            double lastUT = PreClaimLastUT,
            bool inUse = false,
            string blocker = null)
        {
            return new StaleVesselEvidence
            {
                LiveExists = liveExists,
                LiveGuid = StationGuid,
                PlayheadSeenBeforeTip = seenBefore,
                LiveLastUT = lastUT,
                LiveInUse = inUse,
                TipSpawnBlocker = blocker
            };
        }

        private static void SpawnTipAs(Recording rec, uint pid)
        {
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = pid;
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

            Assert.True(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonReplace, reason);
        }

        [Fact]
        public void Predicate_AlreadySpawnedTip_LeavesTheSiteUnchanged()
        {
            Recording tip = CommitDockUndockTree();
            SpawnTipAs(tip, StationPid);

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonTipSpawned, reason);
        }

        [Fact]
        public void Predicate_RecordingOutsideAnyChain_LeavesTheSiteUnchanged()
        {
            CommitDockUndockTree();
            var standalone = MakeRecording("standalone", 4242u, StationGuid, 100, 200,
                TerminalState.Orbiting, null, null, "Probe");

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                standalone, Chains(), Stale(), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonNotChainTip, reason);
        }

        [Fact]
        public void Predicate_IntermediateLinkOnTheClaimedPid_IsNotReplaced()
        {
            CommitDockUndockTree();
            Recording merged = RecordingStore.CommittedTrees[0].Recordings["merged"];

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                merged, Chains(), Stale(), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonNotChainTip, reason);
        }

        [Fact]
        public void Predicate_TerminatedChain_LeavesTheSiteUnchanged()
        {
            Recording tip = CommitDockUndockTree(TerminalState.Destroyed);
            var chains = Chains();
            Assert.True(GhostChainWalker.FindChainForVessel(chains, StationPid).IsTerminated);

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, chains, Stale(), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonTerminated, reason);
        }

        [Fact]
        public void Predicate_NeverRewoundBeforeTheTip_KeepsAdoption()
        {
            // Normal forward play: the live station IS the vessel the tip recorded.
            Recording tip = CommitDockUndockTree();

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(seenBefore: false), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonNotRewoundBeforeTip, reason);
        }

        [Fact]
        public void Predicate_LiveVesselSimulatedAtOrAfterTheClaim_KeepsAdoption()
        {
            // The vessel that went through the dock was in physics at the dock, so its lastUT
            // is at least the claim UT: it carries the recorded change and is adopted.
            Recording tip = CommitDockUndockTree();

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(lastUT: DockUT), out string atClaim));
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, atClaim);
            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(lastUT: 1650.0), out string afterTip));
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, afterTip);
        }

        [Fact]
        public void Predicate_UnknownLastSimulatedUT_KeepsAdoption()
        {
            Recording tip = CommitDockUndockTree();

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(lastUT: double.NaN), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, reason);
        }

        [Fact]
        public void Predicate_NeverLoadedVessel_IsStale()
        {
            // KSP writes lastUT = -1 for a vessel never simulated; it cannot carry the change.
            Recording tip = CommitDockUndockTree();

            Assert.True(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(lastUT: -1.0), out string reason));
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
                tip, chains, Stale(lastUT: 1550.0), out string between));
            Assert.Equal(ChainTipStaleVessel.ReasonReplace, between);
            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, chains, Stale(lastUT: 2600.0), out string after));
            Assert.Equal(ChainTipStaleVessel.ReasonSimulatedSinceClaim, after);
        }

        [Fact]
        public void Predicate_TipThatCannotSpawn_KeepsTheLiveVessel()
        {
            Recording tip = CommitDockUndockTree();

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(blocker: ChainTipStaleVessel.BlockerAttemptCap), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonTipCannotSpawn, reason);
        }

        [Fact]
        public void Predicate_LiveVesselInThePlayersHands_KeepsAdoption()
        {
            Recording tip = CommitDockUndockTree();

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(inUse: true), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonInUse, reason);
        }

        [Fact]
        public void Predicate_NoLiveSameLaunchVessel_LeavesTheSiteUnchanged()
        {
            Recording tip = CommitDockUndockTree();

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(liveExists: false), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonNoLiveVessel, reason);
        }

        [Fact]
        public void TipSpawnBlocker_NamesEveryStateThatWouldSpawnNothing()
        {
            Recording tip = CommitDockUndockTree();
            Assert.Null(ChainTipStaleVessel.ResolveTipSpawnBlocker(tip));

            tip.SpawnAttempts = VesselSpawner.MaxSpawnAttempts;
            Assert.Equal(ChainTipStaleVessel.BlockerAttemptCap, ChainTipStaleVessel.ResolveTipSpawnBlocker(tip));
            tip.SpawnAttempts = 0;

            tip.TerminalSpawnCannotSpawnSafely = true;
            Assert.Equal(ChainTipStaleVessel.BlockerTerminalOrbitHold, ChainTipStaleVessel.ResolveTipSpawnBlocker(tip));
            tip.TerminalSpawnCannotSpawnSafely = false;

            tip.TerminalSpawnSafetyDeferred = true;
            Assert.Equal(ChainTipStaleVessel.BlockerTerminalOrbitHold, ChainTipStaleVessel.ResolveTipSpawnBlocker(tip));
            tip.TerminalSpawnSafetyDeferred = false;

            tip.SpawnAbandoned = true;
            Assert.Equal(ChainTipStaleVessel.BlockerAbandoned, ChainTipStaleVessel.ResolveTipSpawnBlocker(tip));
            tip.SpawnAbandoned = false;

            tip.VesselSnapshot = null;
            Assert.Equal(ChainTipStaleVessel.BlockerNoSnapshot, ChainTipStaleVessel.ResolveTipSpawnBlocker(tip));
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
                SpawnTipAs(rec, StationPid);
            };

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.True(handled);
            Assert.Equal(new List<uint> { StationPid }, removed);
            Assert.Empty(restored);
            Assert.True(spawnedWithPreservedIdentity);
            Assert.True(tip.VesselSpawned);
            Assert.DoesNotContain(logLines, l => l.Contains("skipped duplicate spawn"));
            Assert.Contains(logLines, l => l.Contains("[ChainTip]")
                && l.Contains("Replacing stale pre-claim vessel (TRACKSTATION)")
                && l.Contains("rec=station-tip"));
            Assert.Contains(logLines, l => l.Contains("Chain-tip replacement complete (TRACKSTATION)"));
        }

        [Fact]
        public void TrackingStationHandoff_TipSpawnsNothing_RestoresTheRemovedVessel()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                rec.SpawnAttempts++;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.Equal(new List<uint> { StationPid }, removed);
            Assert.Equal(new List<uint> { StationPid }, restored);
            Assert.True(stationLive);
            Assert.False(tip.VesselSpawned);
            Assert.Contains(logLines, l => l.Contains("[WARN][ChainTip]")
                && l.Contains("Chain-tip replacement undone (TRACKSTATION)")
                && l.Contains("tip spawned no vessel")
                && l.Contains("restored the removed vessel pid=777"));
        }

        [Fact]
        public void TrackingStationHandoff_TipSpawnThrows_RestoresTheRemovedVessel()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                throw new InvalidOperationException("spawn blew up");

            Assert.Throws<InvalidOperationException>(() =>
                GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                    new List<Recording> { tip }, 0, tip.EndUT + 10));

            Assert.Equal(new List<uint> { StationPid }, restored);
            Assert.True(stationLive);
        }

        [Fact]
        public void TrackingStationHandoff_TipOutOfSpawnAttempts_KeepsAndAdoptsTheLiveVessel()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            tip.SpawnAttempts = VesselSpawner.MaxSpawnAttempts;
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.Empty(removed);
            Assert.False(spawnCalled);
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("Stale chain-tip vessel kept (TRACKSTATION)")
                && l.Contains(ChainTipStaleVessel.BlockerAttemptCap));
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
            Assert.Empty(removed);
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

            Assert.Empty(removed);
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

            Assert.Empty(removed);
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
            ChainTipStaleVessel.RemoveOverrideForTesting = (pid, scene) => null;
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.False(spawnCalled);
            Assert.Empty(restored);
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

            StaleVesselReplacement replacement = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 4);

            Assert.NotNull(replacement);
            Assert.Equal(new List<uint> { StationPid }, removed);
            // The site's adoption check that follows now finds nothing to adopt.
            Assert.False(VesselSpawner.TryAdoptExistingSourceVesselForSpawn(tip, "KSCSpawn", "test"));
            Assert.Equal(0u, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("Replacing stale pre-claim vessel (SPACECENTER) #4"));
        }

        [Fact]
        public void SpaceCenterEndSpawn_DeadCrewAbandon_RestoresTheRemovedVessel()
        {
            // The KSC spawn's dead-crew branch settles the tip spawned-with-no-vessel; the
            // site's finally then completes the replacement.
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            StaleVesselReplacement replacement = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 4);
            tip.VesselSpawned = true;
            tip.SpawnAbandoned = true;

            Assert.True(ChainTipStaleVessel.CompleteReplacement(tip, replacement));

            Assert.Equal(new List<uint> { StationPid }, restored);
            Assert.True(stationLive);
        }

        [Fact]
        public void CompleteReplacement_TipSpawned_RestoresNothing()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            StaleVesselReplacement replacement = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 4);
            SpawnTipAs(tip, StationPid);

            Assert.False(ChainTipStaleVessel.CompleteReplacement(tip, replacement));
            Assert.Empty(restored);
            Assert.False(ChainTipStaleVessel.CompleteReplacement(tip, null));
        }

        [Fact]
        public void CompleteReplacement_RestoreFails_SaysSoAsAnError()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            StaleVesselReplacement replacement = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 4);
            ChainTipStaleVessel.RestoreOverrideForTesting = (snapshot, pid, scene) => 0u;

            Assert.False(ChainTipStaleVessel.CompleteReplacement(tip, replacement));
            Assert.Contains(logLines, l => l.Contains("[ERROR][ChainTip]")
                && l.Contains("could not be restored"));
        }

        [Fact]
        public void SpaceCenterEndSpawn_NeverRewoundBeforeTheTip_AdoptsAsBefore()
        {
            Recording tip = CommitDockUndockTree();

            Assert.Null(ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(tip, "SPACECENTER", 4));
            Assert.Empty(removed);
            Assert.True(VesselSpawner.TryAdoptExistingSourceVesselForSpawn(tip, "KSCSpawn", "test"));
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
        }

        [Fact]
        public void SpaceCenterEndSpawn_DifferentLaunchOnTheBakedPid_IsNeitherReplacedNorAdopted()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            stationLiveGuid = "ffffffffffffffffffffffffffffffff";

            Assert.Null(ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(tip, "SPACECENTER", 4));
            Assert.Empty(removed);
            Assert.Contains(logLines, l => l.Contains("reason=" + ChainTipStaleVessel.ReasonDifferentLaunch));
        }

        [Fact]
        public void FlightLeafSpawn_ReplayBypass_NeverReplaces()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);

            Assert.Null(ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "FLIGHT", 1, allowExistingSourceDuplicate: true));
            Assert.Empty(removed);
        }

        [Fact]
        public void FlightLeafSpawn_StalePreClaimVesselInUse_AdoptsAsBeforeAndSaysWhy()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            ChainTipStaleVessel.LiveVesselInUseOverrideForTesting = pid => pid == StationPid;

            Assert.Null(ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(tip, "FLIGHT", 1));
            Assert.Empty(removed);
            Assert.Contains(logLines, l => l.Contains("[INFO][ChainTip]")
                && l.Contains("Stale chain-tip vessel kept (FLIGHT)"));
        }

        private static void InvokeFlightLeafSpawn(Recording rec, int index)
        {
            // The ordinary flight leaf path (no active ghost chain: the flight loaded past the
            // tip's spawn UT).
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            MethodInfo spawn = typeof(ParsekFlight).GetMethod(
                "SpawnVesselOrChainTip", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(spawn);
            spawn.Invoke(host, new object[] { rec, index });
        }

        [Fact]
        public void FlightLeafSpawn_PastSpawnUTWithStaleVessel_ReplacesInsteadOfAdopting()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            bool? spawnedWithPreservedIdentity = null;
            ParsekFlight.LeafSpawnOverrideForTesting = (rec, index, preserveIdentity, allowDuplicate) =>
            {
                spawnedWithPreservedIdentity = preserveIdentity;
                SpawnTipAs(rec, StationPid);
            };

            InvokeFlightLeafSpawn(tip, 0);

            Assert.Equal(new List<uint> { StationPid }, removed);
            Assert.Empty(restored);
            Assert.True(spawnedWithPreservedIdentity);
            Assert.Contains(logLines, l => l.Contains("Replacing stale pre-claim vessel (FLIGHT) #0"));
        }

        [Fact]
        public void FlightLeafSpawn_TipSpawnsNothing_RestoresTheRemovedVessel()
        {
            Recording tip = CommitDockUndockTree();
            LatchRewoundBefore(tip);
            ParsekFlight.LeafSpawnOverrideForTesting = (rec, index, preserveIdentity, allowDuplicate) => { };

            InvokeFlightLeafSpawn(tip, 0);

            Assert.Equal(new List<uint> { StationPid }, removed);
            Assert.Equal(new List<uint> { StationPid }, restored);
            Assert.True(stationLive);
            Assert.False(tip.VesselSpawned);
        }

        #endregion

        #region One tip ending two claimed vessels

        [Fact]
        public void TwoClaims_Fixture_OneChainKeyedByTheDepotEndsAtTheStationTip()
        {
            Recording tip = CommitTwoClaimTree();
            var chains = Chains();

            GhostChain chain = ChainTipStaleVessel.FindChainForTip(chains, tip.RecordingId);
            Assert.NotNull(chain);
            Assert.Equal(DepotPid, chain.OriginalVesselPid);
            Assert.Equal(StationPid, tip.VesselPersistentId);
            Assert.Null(GhostChainWalker.FindChainForVessel(chains, StationPid));
            Assert.Equal(new HashSet<uint> { DepotPid, StationPid },
                ChainTipStaleVessel.ResolveClaimedPidsForTip(chains, tip.RecordingId, RecordingStore.CommittedTrees));
        }

        [Fact]
        public void TwoClaims_Predicate_RefusesAndSaysWhy()
        {
            Recording tip = CommitTwoClaimTree();
            StaleVesselEvidence evidence = Stale();
            evidence.ClaimedVesselCount = 2;

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), evidence, out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonSeveralClaimedVessels, reason);
        }

        [Fact]
        public void TipIdentityLive_OnAnotherPid_Predicate_Refuses()
        {
            // The claimed pid differs from the tip's and a live vessel already matches the tip's
            // own identity: adopting it is the site's answer, so nothing may be removed.
            Recording tip = CommitTransportDominantTree();
            StaleVesselEvidence evidence = Stale();
            evidence.TipIdentityLive = true;

            Assert.False(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), evidence, out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonTipIdentityLive, reason);
        }

        [Fact]
        public void TwoClaims_TrackingStationHandoff_RemovesNothingAndAdoptsAsBefore()
        {
            Recording tip = CommitTwoClaimTree();
            LatchRewoundBefore(tip);
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.Empty(removed);
            Assert.False(spawnCalled);
            Assert.True(otherLive.ContainsKey(DepotPid));
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("[ChainTip]")
                && l.Contains("reason=" + ChainTipStaleVessel.ReasonSeveralClaimedVessels));
        }

        [Fact]
        public void TwoClaims_SpaceCenterAndFlightEntry_RemoveNothing()
        {
            Recording tip = CommitTwoClaimTree();
            LatchRewoundBefore(tip);

            Assert.Null(ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(tip, "SPACECENTER", 2));
            Assert.Null(ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(tip, "FLIGHT", 2));
            Assert.Empty(removed);
            Assert.True(otherLive.ContainsKey(DepotPid));
        }

        [Fact]
        public void TwoClaims_FlightLeafSpawn_AdoptsTheStationAndLeavesTheDepot()
        {
            Recording tip = CommitTwoClaimTree();
            LatchRewoundBefore(tip);
            ParsekFlight.LeafSpawnOverrideForTesting = (rec, index, preserveIdentity, allowDuplicate) =>
                VesselSpawner.TryAdoptExistingSourceVesselForSpawn(rec, "Spawner", "test");

            InvokeFlightLeafSpawn(tip, 0);

            Assert.Empty(removed);
            Assert.Empty(restored);
            Assert.True(otherLive.ContainsKey(DepotPid));
            Assert.Equal(StationPid, tip.SpawnedVesselPersistentId);
        }

        [Fact]
        public void TrackingStationHandoff_ReplacementThenTipIdentityLive_PutsTheRemovedVesselBack()
        {
            // The two existence reads disagree (the TS reads loaded vessels, the replacement
            // reads loaded and proto vessels): the TS finds the tip's own identity live after a
            // replacement removed the claimed vessel. That exit adopts, so it must restore.
            Recording tip = CommitTransportDominantTree();
            LatchRewoundBefore(tip);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => IsLive(pid) || pid == StationHalfNewPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(
                pid => pid == StationHalfNewPid ? StationHalfNewGuid : LiveGuidOf(pid));
            bool spawnCalled = false;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
                spawnCalled = true;

            GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.Equal(new List<uint> { StationPid }, removed);
            Assert.Equal(new List<uint> { StationPid }, restored);
            Assert.False(spawnCalled);
            Assert.Equal(StationHalfNewPid, tip.SpawnedVesselPersistentId);
        }

        #endregion

        #region Transport-dominant dock (the tip carries a new pid)

        [Fact]
        public void TransportDominant_Fixture_StationHalfWithNewPidIsTheTipOfTheClaimedChain()
        {
            Recording tip = CommitTransportDominantTree();
            var chains = Chains();

            GhostChain chain = GhostChainWalker.FindChainForVessel(chains, StationPid);
            Assert.NotNull(chain);
            Assert.Equal(tip.RecordingId, chain.TipRecordingId);
            Assert.Equal(StationHalfNewPid, tip.VesselPersistentId);
            Assert.Null(GhostChainWalker.FindChainForVessel(chains, StationHalfNewPid));
            Assert.Same(chain, ChainTipStaleVessel.FindChainForTip(chains, tip.RecordingId));
        }

        [Fact]
        public void TransportDominant_Predicate_StaleStationOnTheClaimedPid_Replaces()
        {
            Recording tip = CommitTransportDominantTree();

            Assert.True(ChainTipStaleVessel.ShouldReplaceStaleLiveVessel(
                tip, Chains(), Stale(), out string reason));
            Assert.Equal(ChainTipStaleVessel.ReasonReplace, reason);
        }

        [Fact]
        public void ExpectedClaimedGuid_ChainGuidThenSamePidTipGuidThenUnknown()
        {
            var chain = new GhostChain { OriginalVesselPid = StationPid, LaunchGuid = StationGuid };
            var samePidTip = new Recording { VesselPersistentId = StationPid, RecordedVesselGuid = "a" };
            var newPidTip = new Recording { VesselPersistentId = StationHalfNewPid, RecordedVesselGuid = "b" };

            Assert.Equal(StationGuid, ChainTipStaleVessel.ExpectedClaimedGuid(newPidTip, chain));
            chain.LaunchGuid = null;
            Assert.Equal("a", ChainTipStaleVessel.ExpectedClaimedGuid(samePidTip, chain));
            Assert.Null(ChainTipStaleVessel.ExpectedClaimedGuid(newPidTip, chain));
            Assert.Null(ChainTipStaleVessel.ExpectedClaimedGuid(newPidTip, null));
        }

        [Fact]
        public void TransportDominant_TrackingStationHandoff_RemovesTheStaleStationBeforeTheTipSpawns()
        {
            // Without the claimed-pid lookup the tip (pid 888) spawns beside the stale station
            // (pid 777): two stations.
            Recording tip = CommitTransportDominantTree();
            LatchRewoundBefore(tip);
            bool? spawnedWithPreservedIdentity = null;
            GhostMapPresence.TrackingStationSpawnOverrideForTesting = (rec, index, preserveIdentity) =>
            {
                spawnedWithPreservedIdentity = preserveIdentity;
                SpawnTipAs(rec, StationHalfNewPid);
            };

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { tip }, 0, tip.EndUT + 10);

            Assert.True(handled);
            Assert.Equal(new List<uint> { StationPid }, removed);
            Assert.Empty(restored);
            Assert.True(spawnedWithPreservedIdentity);
            Assert.Equal(StationHalfNewPid, tip.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("Replacing stale pre-claim vessel (TRACKSTATION)")
                && l.Contains("live pid=777") && l.Contains("tipPid=888"));
        }

        [Fact]
        public void TransportDominant_SpaceCenterAndFlightEntry_RemoveTheStaleStation()
        {
            Recording tip = CommitTransportDominantTree();
            LatchRewoundBefore(tip);

            StaleVesselReplacement replacement = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                tip, "SPACECENTER", 2);

            Assert.NotNull(replacement);
            Assert.Equal(StationPid, replacement.RemovedPid);
            Assert.Equal(new List<uint> { StationPid }, removed);
        }

        [Fact]
        public void TransportDominant_NeverRewoundBeforeTheTip_LeavesTheLiveStationAlone()
        {
            // Normal play: the original station merged into the transport and the real station
            // is the pid-888 half; a pid-777 vessel here was never rewound past, so it is left.
            Recording tip = CommitTransportDominantTree();

            Assert.Null(ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(tip, "SPACECENTER", 2));
            Assert.Empty(removed);
        }

        #endregion
    }
}
