using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Chain loop first-run-is-real (design 12.7; operator ruling 2026-09-27 on
    /// LOOPING-CHAIN-FIRST-RUN-TIP-NEVER-SPAWNS): looping ONE phase of a chain keeps the
    /// chain's first run real. The chain's tip spawns once at the end of the first run and
    /// later loop replays are ghost-only. Mid-chain segments never spawn, looped or not.
    /// Covers the shared decision, each scene's gate (Space Center, Tracking Station, the
    /// flight engine's loop first-run seam fed by the decision), the crew side's final-segment
    /// notion, and the Info line.
    /// </summary>
    [Collection("Sequential")]
    public class ChainLoopFirstRunSpawnTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainLoopFirstRunSpawnTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostMapPresence.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        #region Helpers

        private const string ChainId = "chain-lf";

        private static Recording MakeSegment(
            string id, int chainIndex, double startUT, double endUT, bool loop, bool tip)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Probe");
            snapshot.AddValue("name", "Chain Rover");
            return new Recording
            {
                RecordingId = id,
                VesselName = "Chain Rover",
                VesselPersistentId = 5151,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselSnapshot = snapshot,
                TerminalStateValue = tip ? TerminalState.Landed : (TerminalState?)null,
                PlaybackEnabled = true,
                LoopPlayback = loop,
                ChainId = ChainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT, bodyName = "Kerbin", latitude = -0.09, longitude = -74.4, altitude = 70 },
                    new TrajectoryPoint { ut = endUT, bodyName = "Kerbin", latitude = -0.09, longitude = -74.4, altitude = 70 }
                }
            };
        }

        /// <summary>A committed three-phase chain (launch, coast, landing) with the named phase looped.</summary>
        private static Recording[] CommitChain(int loopedIndex)
        {
            var segs = new[]
            {
                MakeSegment("lf-launch", 0, 1000, 1200, loopedIndex == 0, tip: false),
                MakeSegment("lf-coast", 1, 1200, 1600, loopedIndex == 1, tip: false),
                MakeSegment("lf-landing", 2, 1600, 2000, loopedIndex == 2, tip: true),
            };
            foreach (var s in segs)
                RecordingStore.AddRecordingWithTreeForTesting(s);
            return segs;
        }

        #endregion

        #region Shared decision (ShouldSpawnAtRecordingEnd)

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Decision_TipOfChainWithAnyLoopedPhase_Spawns(int loopedIndex)
        {
            var segs = CommitChain(loopedIndex);
            Assert.True(RecordingStore.IsChainLooping(ChainId));

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                segs[2], isActiveChainMember: false);

            Assert.True(needsSpawn, reason);
            Assert.Equal("", reason);
        }

        [Fact]
        public void Decision_LoopedTip_SpawnsOnce_LaterReplaysAlreadySpawned()
        {
            var segs = CommitChain(loopedIndex: 2);

            Assert.True(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(segs[2], false).needsSpawn);
            segs[2].VesselSpawned = true;
            segs[2].SpawnedVesselPersistentId = 900001;

            var later = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(segs[2], false);
            Assert.False(later.needsSpawn);
            Assert.Contains("already spawned", later.reason);
        }

        [Fact]
        public void Decision_NoLoopedPhase_SameAnswerAsLoopedChain()
        {
            // Mirror direction: looping a phase changes nothing about the tip's spawn answer.
            var segs = CommitChain(loopedIndex: -1);
            Assert.False(RecordingStore.IsChainLooping(ChainId));

            Assert.True(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(segs[2], false).needsSpawn);
        }

        #endregion

        #region Scene gates

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void SpaceCenter_TipOfLoopingChain_SpawnsAtItsEnd(int loopedIndex)
        {
            var segs = CommitChain(loopedIndex);

            var before = GhostPlaybackLogic.ShouldSpawnAtKscEnd(segs[2], segs[2].EndUT - 1);
            Assert.False(before.needsSpawn);

            var after = GhostPlaybackLogic.ShouldSpawnAtKscEnd(segs[2], segs[2].EndUT + 1);
            Assert.True(after.needsSpawn, after.reason);
        }

        [Fact]
        public void SpaceCenter_LoopedMidSegment_NeverSpawns()
        {
            var segs = CommitChain(loopedIndex: 0);

            var head = GhostPlaybackLogic.ShouldSpawnAtKscEnd(segs[0], segs[2].EndUT + 1);
            Assert.False(head.needsSpawn);
            Assert.Contains("intermediate chain segment", head.reason);
        }

        [Fact]
        public void TrackingStation_TipOfLoopingChain_Spawns_MidSegmentDoesNot()
        {
            var segs = CommitChain(loopedIndex: 1);

            var tip = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(segs[2], segs[2].EndUT + 1);
            Assert.True(tip.needsSpawn, tip.reason);

            var mid = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(segs[1], segs[2].EndUT + 1);
            Assert.False(mid.needsSpawn);
        }

        private static TrajectoryPlaybackFlags FlagsFor(Recording rec)
        {
            // The flight flag builder's wiring (ParsekFlight.ComputePlaybackFlags): needsSpawn
            // from the shared decision, isMidChain / chainEndUT from the committed chain.
            return new TrajectoryPlaybackFlags
            {
                needsSpawn = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn,
                isMidChain = RecordingStore.IsChainMidSegment(rec),
                chainEndUT = RecordingStore.GetChainEndUT(rec),
                isChainLooping = RecordingStore.IsChainLooping(rec.ChainId),
                recordingId = rec.RecordingId,
            };
        }

        private static int QueueFrames(GhostPlaybackEngine engine, int index, Recording rec,
            params double[] uts)
        {
            var traj = new MockTrajectory().WithTimeRange(rec.StartUT, rec.EndUT).WithLoop();
            int queued = 0;
            for (int k = 0; k < uts.Length; k++)
            {
                var ctx = new FrameContext { currentUT = uts[k], warpRate = 1f };
                if (engine.TryQueueLoopFirstRunSpawn(index, traj, FlagsFor(rec), ctx,
                        GhostPlaybackEngine.ResolveGhostActivationStartUT(traj), hasPointData: true))
                    queued++;
            }
            return queued;
        }

        [Fact]
        public void FlightEngine_LoopedTip_QueuesExactlyOneFirstRunSpawnAcrossCycles()
        {
            var segs = CommitChain(loopedIndex: 2);
            var engine = new GhostPlaybackEngine(positioner: null);

            // The first run, just past its end, then three loop cycles of real-playhead frames.
            Assert.Equal(1, QueueFrames(engine, 2, segs[2],
                1700, 2000, 2000.5, 2400, 2800, 3200));
            var queued = engine.PendingCompletedEventsForTesting;
            Assert.Single(queued);
            Assert.True(queued[0].LoopFirstRun);
            Assert.True(queued[0].Flags.needsSpawn);
            Assert.True(queued[0].PastEffectiveEnd);
        }

        [Fact]
        public void FlightEngine_LoopedMidSegment_QueuesNothing()
        {
            var segs = CommitChain(loopedIndex: 0);
            var engine = new GhostPlaybackEngine(positioner: null);

            Assert.Equal(0, QueueFrames(engine, 0, segs[0], 1100, 1200.5, 2000.5, 2400));
            Assert.Empty(engine.PendingCompletedEventsForTesting);
        }

        #endregion

        #region Crew side (IsFinalSpawnSegment)

        [Fact]
        public void IsFinalSpawnSegment_TipOfLoopingChain_IsFinal_MidSegmentsAreNot()
        {
            // The KSC-retirement crew side must agree with the spawn side on which stop is final.
            var segs = CommitChain(loopedIndex: 0);

            Assert.False(GhostPlaybackLogic.IsFinalSpawnSegment(segs[0]));
            Assert.False(GhostPlaybackLogic.IsFinalSpawnSegment(segs[1]));
            Assert.True(GhostPlaybackLogic.IsFinalSpawnSegment(segs[2]));
        }

        #endregion

        #region Log line

        [Fact]
        public void FormatChainLoopFirstRunSpawn_ChainNotLooping_ReturnsNull()
        {
            var tip = MakeSegment("lf-landing", 2, 1600, 2000, loop: false, tip: true);
            Assert.Null(GhostPlaybackLogic.FormatChainLoopFirstRunSpawn(
                "FLIGHT", 2, tip, 2000.5, chainLooping: false));
        }

        [Fact]
        public void FormatChainLoopFirstRunSpawn_NamesTheTipChainAndScene_InvariantCulture()
        {
            var tip = MakeSegment("lf-landing", 2, 1600, 2000.25, loop: true, tip: true);
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = GhostPlaybackLogic.FormatChainLoopFirstRunSpawn(
                    "SPACECENTER", 7, tip, 2001.5, chainLooping: true);

                Assert.Equal(
                    "Chain loop first-run spawn: #7 \"Chain Rover\" id=lf-landing chain=chain-lf"
                    + " tipLooping=true scene=SPACECENTER UT=2001.50 endUT=2000.25"
                    + " (a chain with a looped phase keeps its first run real: the tip spawns once,"
                    + " later loop replays stay ghost-only)",
                    line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void LogChainLoopFirstRunSpawn_WritesOneInfoLinePerRecording()
        {
            var segs = CommitChain(loopedIndex: 0);

            GhostPlaybackLogic.LogChainLoopFirstRunSpawn(
                "Policy", "FLIGHT", 2, segs[2], 2000.5, chainLooping: true);
            GhostPlaybackLogic.LogChainLoopFirstRunSpawn(
                "Policy", "FLIGHT", 2, segs[2], 2000.6, chainLooping: true);
            GhostPlaybackLogic.LogChainLoopFirstRunSpawn(
                "Policy", "FLIGHT", 3, segs[1], 2000.6, chainLooping: false);

            var hits = logLines.FindAll(l => l.Contains("[Policy]")
                && l.Contains("Chain loop first-run spawn:"));
            Assert.Single(hits);
            Assert.Contains("[INFO]", hits[0]);
            Assert.Contains("id=lf-landing", hits[0]);
        }

        #endregion
    }
}
