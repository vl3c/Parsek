using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #99: KSC view vessel spawn at recording end.
    /// Tests the ShouldSpawnAtKscEnd eligibility check (static, no Unity).
    /// </summary>
    [Collection("Sequential")]
    public class KscSpawnTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KscSpawnTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region Helpers

        static Recording MakeEligibleRecording(string id = "rec-1", string vesselName = "TestVessel")
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", vesselName);

            return new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                VesselPersistentId = 12345,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 2000,
                VesselSnapshot = snapshot,
                VesselSpawned = false,
                VesselDestroyed = false,
                PlaybackEnabled = true,
                LoopPlayback = false,
                ChainBranch = 0,
                ChildBranchPointId = null,
                IsDebris = false,
                SpawnedVesselPersistentId = 0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 },
                    new TrajectoryPoint { ut = 2000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 }
                }
            };
        }

        #endregion

        #region ShouldSpawnAtKscEnd — eligible cases

        [Fact]
        public void ShouldSpawnAtKscEnd_EligibleRecording_ReturnsTrue()
        {
            var rec = MakeEligibleRecording();

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_EligibleRecording_NoChain_ReturnsTrue()
        {
            var rec = MakeEligibleRecording();
            rec.ChainId = null;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        #endregion

        #region ShouldSpawnAtKscEnd — suppressed cases

        [Fact]
        public void ShouldSpawnAtKscEnd_NoSnapshot_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.VesselSnapshot = null;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("no vessel snapshot", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_AlreadySpawned_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.VesselSpawned = true;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("already spawned", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_VesselDestroyed_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.VesselDestroyed = true;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("vessel destroyed", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_BranchGtZero_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.ChainBranch = 1;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("branch > 0", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_TerminalDestroyed_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.TerminalStateValue = TerminalState.Destroyed;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_TerminalRecovered_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.TerminalStateValue = TerminalState.Recovered;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_Debris_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.IsDebris = true;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("debris", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_NonLeafTree_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.ChildBranchPointId = "bp-1";

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("non-leaf", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_AlreadySpawnedPid_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.SpawnedVesselPersistentId = 99999;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("already spawned", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_SnapshotSituationFlying_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.VesselSnapshot.SetValue("sit", "FLYING", true);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("snapshot situation unsafe", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_StandaloneLoopPlayback_AllowsFirstSpawn()
        {
            // Regression: standalone looping recordings DO spawn on first playthrough
            // (matching Flight behavior). VesselSpawned=true prevents re-spawn on
            // subsequent loops. TrySpawnAtRecordingEnd has a separate LoopPlayback
            // early return as a safety net, but the eligibility check allows it.
            var rec = MakeEligibleRecording();
            rec.ChainId = null;
            rec.LoopPlayback = true;
            rec.VesselSpawned = false;

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_StandaloneLoopPlayback_AlreadySpawned_ReturnsFalse()
        {
            // After first spawn, VesselSpawned=true prevents re-spawning on loop restart
            var rec = MakeEligibleRecording();
            rec.ChainId = null;
            rec.LoopPlayback = true;
            rec.VesselSpawned = true;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("already spawned", reason);
        }

        #endregion

        #region ShouldSpawnAtKscEnd — chain suppression

        [Fact]
        public void ShouldSpawnAtKscEnd_ChainLooping_ReturnsFalse()
        {
            // Test the chain tip (highest index) — mid-segments are already
            // suppressed by IsChainMidSegment. Looping suppresses even the tip.
            var midRec = MakeEligibleRecording("rec-mid", "LoopVessel");
            midRec.ChainId = "chain-loop";
            midRec.ChainIndex = 0;
            midRec.ChainBranch = 0;
            midRec.LoopPlayback = true;

            var tipRec = MakeEligibleRecording("rec-tip", "LoopVessel");
            tipRec.ChainId = "chain-loop";
            tipRec.ChainIndex = 1;
            tipRec.ChainBranch = 0;

            RecordingStore.AddRecordingWithTreeForTesting(midRec);
            RecordingStore.AddRecordingWithTreeForTesting(tipRec);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(tipRec, tipRec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("chain looping", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_ChainFullyDisabled_StillSpawnsAtTip()
        {
            // Bug #433: a fully-disabled chain still spawns its vessel at tip.
            // Visibility toggle does not gate career state. Mid-segments stay
            // suppressed by IsChainMidSegment (orthogonal).
            var midRec = MakeEligibleRecording("rec-mid", "DisabledVessel");
            midRec.ChainId = "chain-off";
            midRec.ChainIndex = 0;
            midRec.ChainBranch = 0;
            midRec.PlaybackEnabled = false;

            var tipRec = MakeEligibleRecording("rec-tip", "DisabledVessel");
            tipRec.ChainId = "chain-off";
            tipRec.ChainIndex = 1;
            tipRec.ChainBranch = 0;
            tipRec.PlaybackEnabled = false;

            RecordingStore.AddRecordingWithTreeForTesting(midRec);
            RecordingStore.AddRecordingWithTreeForTesting(tipRec);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(tipRec, tipRec.EndUT + 1);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_ChainNotLoopingNotDisabled_ReturnsTrue()
        {
            // Chain tip with no looping — should spawn
            var midRec = MakeEligibleRecording("rec-mid", "OtherVessel");
            midRec.ChainId = "chain-ok";
            midRec.ChainIndex = 0;
            midRec.ChainBranch = 0;
            midRec.PlaybackEnabled = true;
            midRec.LoopPlayback = false;

            var tipRec = MakeEligibleRecording("rec-tip", "OtherVessel");
            tipRec.ChainId = "chain-ok";
            tipRec.ChainIndex = 1;
            tipRec.ChainBranch = 0;
            tipRec.PlaybackEnabled = true;
            tipRec.LoopPlayback = false;

            RecordingStore.AddRecordingWithTreeForTesting(midRec);
            RecordingStore.AddRecordingWithTreeForTesting(tipRec);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(tipRec, tipRec.EndUT + 1);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        #endregion

        #region ShouldShowInKSC filtering

        [Fact]
        public void ShouldShowInKSC_DisabledPlayback_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.PlaybackEnabled = false;

            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_TooFewPoints_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 1000, bodyName = "Kerbin" }
            };

            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_NotKerbin_ReturnsFalse()
        {
            var rec = MakeEligibleRecording();
            rec.Points[0] = new TrajectoryPoint { ut = 1000, bodyName = "Mun" };

            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_EligibleRecording_ReturnsTrue()
        {
            var rec = MakeEligibleRecording();

            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_IntermediateChainSegment_ReturnsFalse()
        {
            // Chain mid-segments should not spawn — only the chain tip spawns.
            // Without this check, all segments in a chain would spawn at KSC.
            var midRec = MakeEligibleRecording("rec-mid", "ChainVessel");
            midRec.ChainId = "chain-1";
            midRec.ChainIndex = 0;

            var tipRec = MakeEligibleRecording("rec-tip", "ChainVessel");
            tipRec.ChainId = "chain-1";
            tipRec.ChainIndex = 1;

            RecordingStore.AddRecordingWithTreeForTesting(midRec);
            RecordingStore.AddRecordingWithTreeForTesting(tipRec);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(midRec, midRec.EndUT + 1);
            Assert.False(needsSpawn);
            Assert.Contains("intermediate chain segment", reason);
        }

        [Fact]
        public void ShouldSpawnAtKscEnd_ChainTip_ReturnsTrue()
        {
            var midRec = MakeEligibleRecording("rec-mid", "ChainVessel");
            midRec.ChainId = "chain-1";
            midRec.ChainIndex = 0;

            var tipRec = MakeEligibleRecording("rec-tip", "ChainVessel");
            tipRec.ChainId = "chain-1";
            tipRec.ChainIndex = 1;

            RecordingStore.AddRecordingWithTreeForTesting(midRec);
            RecordingStore.AddRecordingWithTreeForTesting(tipRec);

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(tipRec, tipRec.EndUT + 1);
            Assert.True(needsSpawn);
        }

        #endregion

        #region KSC spawn rejects orbital vessels (#171)

        [Fact]
        public void KscSpawn_OrbitingTerminal_DeferredToFlightScene()
        {
            var rec = MakeEligibleRecording("rec-orbit", "OrbitalShip");
            rec.TerminalStateValue = TerminalState.Orbiting;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("orbital vessel deferred to flight scene", reason);
        }

        [Fact]
        public void KscSpawn_DockedTerminal_DeferredToFlightScene()
        {
            var rec = MakeEligibleRecording("rec-docked", "DockedStation");
            rec.TerminalStateValue = TerminalState.Docked;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Contains("orbital vessel deferred to flight scene", reason);
        }

        [Fact]
        public void KscSpawn_LandedTerminal_StillAllowed()
        {
            var rec = MakeEligibleRecording("rec-landed", "LandedVessel");
            rec.TerminalStateValue = TerminalState.Landed;

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void KscSpawn_SplashedTerminal_StillAllowed()
        {
            var rec = MakeEligibleRecording("rec-splashed", "SplashedVessel");
            rec.TerminalStateValue = TerminalState.Splashed;
            rec.VesselSnapshot.SetValue("sit", "SPLASHED", true);

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void KscSpawn_NullTerminal_StillAllowed()
        {
            var rec = MakeEligibleRecording("rec-null", "NoTerminal");
            rec.TerminalStateValue = null;

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);

            Assert.True(needsSpawn);
        }

        #endregion
    }
}
