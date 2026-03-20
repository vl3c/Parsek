using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ChainSpawnSuppressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainSpawnSuppressionTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
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
        }

        #region Helpers

        static Recording MakeRecording(string id, uint vesselPid,
            double startUT, double endUT, string vesselName = "")
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselName = vesselName,
                Points = new List<TrajectoryPoint>()
            };
        }

        /// <summary>
        /// Builds a two-link chain for vessel PID=100.
        /// Link 1: recordingId="r1" at ut=1000
        /// Link 2 (tip): recordingId="tip-rec" at ut=2000
        /// </summary>
        static (Dictionary<uint, GhostChain> chains, GhostChain chain) MakeTwoLinkChain(
            bool terminated = false)
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipRecordingId = "tip-rec",
                SpawnUT = 2000.0,
                IsTerminated = terminated
            };
            chain.Links.Add(new ChainLink { recordingId = "r1", ut = 1000.0 });
            chain.Links.Add(new ChainLink { recordingId = "tip-rec", ut = 2000.0 });
            var chains = new Dictionary<uint, GhostChain> { { 100, chain } };
            return (chains, chain);
        }

        #endregion

        #region ShouldSuppressSpawnForChain

        /// <summary>
        /// An intermediate chain link (not the tip) should have its spawn suppressed.
        /// </summary>
        [Fact]
        public void IntermediateLink_SpawnSuppressed()
        {
            var (chains, _) = MakeTwoLinkChain();
            var intermediateRec = MakeRecording("r1", 50, 900, 1050, "IntermediateVessel");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                chains, intermediateRec);

            Assert.True(suppressed);
            Assert.Equal("intermediate ghost chain link", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("Intermediate spawn suppressed")
                && l.Contains("rec=r1"));
        }

        /// <summary>
        /// The chain tip should NOT have its spawn suppressed.
        /// </summary>
        [Fact]
        public void ChainTip_SpawnAllowed()
        {
            var (chains, _) = MakeTwoLinkChain();
            var tipRec = MakeRecording("tip-rec", 100, 1500, 2000, "TipVessel");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                chains, tipRec);

            Assert.False(suppressed);
            Assert.Equal("", reason);
        }

        /// <summary>
        /// THE CRITICAL REGRESSION GUARD: standalone recording with empty chains
        /// must NOT be suppressed.
        /// </summary>
        [Fact]
        public void StandaloneRecording_NoChainsPresent_SpawnAllowed()
        {
            var emptyChains = new Dictionary<uint, GhostChain>();
            var standalone = MakeRecording("standalone-1", 500, 3000, 3120, "MyRocket");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                emptyChains, standalone);

            Assert.False(suppressed);
            Assert.Equal("", reason);
        }

        /// <summary>
        /// Chains exist for other vessels, but the recording is not in any chain.
        /// Spawn must be allowed.
        /// </summary>
        [Fact]
        public void StandaloneRecording_ChainsExistButRecordingNotInAny_SpawnAllowed()
        {
            var (chains, _) = MakeTwoLinkChain();
            // Recording with PID=999 — not claimed by any chain
            var unrelated = MakeRecording("unrelated-rec", 999, 5000, 5120, "UnrelatedVessel");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                chains, unrelated);

            Assert.False(suppressed);
            Assert.Equal("", reason);
        }

        /// <summary>
        /// Null chains dict must not suppress spawn (caller has no chain data).
        /// </summary>
        [Fact]
        public void NullChains_SpawnAllowed()
        {
            var rec = MakeRecording("any-rec", 100, 1000, 1060, "SomeVessel");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                null, rec);

            Assert.False(suppressed);
            Assert.Equal("", reason);
        }

        /// <summary>
        /// Terminated chain: the tip recording should have its spawn suppressed.
        /// </summary>
        [Fact]
        public void TerminatedChain_TipSuppressed()
        {
            var (chains, _) = MakeTwoLinkChain(terminated: true);
            var tipRec = MakeRecording("tip-rec", 100, 1500, 2000, "TerminatedTipVessel");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                chains, tipRec);

            Assert.True(suppressed);
            Assert.Equal("terminated ghost chain", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("Terminated chain spawn suppressed")
                && l.Contains("rec=tip-rec"));
        }

        /// <summary>
        /// Terminated chain, but the recording is not the tip (e.g., intermediate link).
        /// The intermediate check fires first (it IS an intermediate link), so it gets
        /// suppressed as intermediate, not as terminated.
        /// For a recording that is NOT in the chain at all, spawn is allowed.
        /// </summary>
        [Fact]
        public void TerminatedChain_NonTipRecording_NotAffected()
        {
            var (chains, _) = MakeTwoLinkChain(terminated: true);
            // A recording that is NOT the chain tip and NOT an intermediate link
            // (completely unrelated vessel PID)
            var unrelated = MakeRecording("other-rec", 999, 5000, 5120, "OtherVessel");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                chains, unrelated);

            Assert.False(suppressed);
            Assert.Equal("", reason);
        }

        /// <summary>
        /// A looped recording at the chain tip should NOT be suppressed by chain logic.
        /// Loop dedup is handled separately by ShouldSpawnAtRecordingEnd via SpawnedVesselPersistentId.
        /// </summary>
        [Fact]
        public void LoopedRecordingAtChainTip_SpawnAllowed()
        {
            var (chains, chain) = MakeTwoLinkChain();
            var tipRec = MakeRecording("tip-rec", 100, 1500, 2000, "LoopedTipVessel");
            tipRec.LoopPlayback = true;

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                chains, tipRec);

            Assert.False(suppressed);
            Assert.Equal("", reason);
        }

        /// <summary>
        /// A Recording with VesselPersistentId=100 and EndUT=1500 (before chain tip SpawnUT=2000)
        /// whose RecordingId is NOT in the chain's Links list — but its vessel PID IS claimed.
        /// ShouldSuppressSpawnForChain should return (true, "intermediate ghost chain link")
        /// because IsIntermediateChainLink uses the PID-based path.
        /// </summary>
        [Fact]
        public void PidBasedIntermediate_SpawnSuppressed()
        {
            var (chains, _) = MakeTwoLinkChain();

            // Recording with PID=100 (same as chain's OriginalVesselPid) and
            // EndUT=1500 (before chain.SpawnUT=2000), but RecordingId NOT in chain links
            var pidMatchRec = MakeRecording("pid-match-rec", 100, 1400, 1500, "PidMatchVessel");

            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                chains, pidMatchRec);

            Assert.True(suppressed);
            Assert.Equal("intermediate ghost chain link", reason);
        }

        /// <summary>
        /// Verify that ShouldSpawnAtRecordingEnd still exists and works correctly
        /// with a simple recording — we did NOT accidentally modify the existing method.
        /// </summary>
        [Fact]
        public void ExistingSpawnLogicUnchanged()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = false,
                VesselDestroyed = false,
                SpawnedVesselPersistentId = 0,
                LoopPlayback = false
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        #endregion
    }
}
