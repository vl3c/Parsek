using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit cells for the flight playback host's per-recording loop schedule, its chain and tree
    /// predicates, and the blocked chain-tip ghost release / capture wiring. The release cells
    /// cover the committed-list index contract on this host: the blocked-tip hold stores a
    /// committed index that no IndexShift handler moves, so a list shift must release it
    /// ("index-shifted") rather than draw the hold over a different recording.
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackHostScheduleAndChainTipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PlaybackHostScheduleAndChainTipTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            GhostPlaybackLogic.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings();
        }

        public void Dispose()
        {
            ParsekSettings.CurrentOverrideForTesting = null;
            GhostPlaybackLogic.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---- helpers ----

        private static ParsekFlight NewHost(bool withEngine = true)
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            host.activeGhostChains = new Dictionary<uint, GhostChain>();
            if (withEngine)
            {
                host.engine = new GhostPlaybackEngine(null);
                host.policy = new ParsekPlaybackPolicy(host.engine, host);
            }
            return host;
        }

        private static Recording Rec(string id, double startUT, double endUT, uint pid = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "V-" + id,
                VesselPersistentId = pid,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT },
                    new TrajectoryPoint { ut = endUT },
                },
            };
        }

        private static Recording Commit(Recording rec)
        {
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        private static Recording LoopRec(
            string id, double startUT, double endUT,
            LoopTimeUnit unit = LoopTimeUnit.Sec, double interval = 90.0)
        {
            Recording rec = Rec(id, startUT, endUT);
            rec.LoopPlayback = true;
            rec.LoopTimeUnit = unit;
            rec.LoopIntervalSeconds = interval;
            return rec;
        }

        // A GameObject the Unity null check reads as alive without touching native code
        // (the cached native pointer is the first thing UnityEngine.Object's == consults).
        private static GameObject FakeLoadedGhost()
        {
            var ghost = (GameObject)FormatterServices.GetUninitializedObject(typeof(GameObject));
            FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cachedPtr);
            cachedPtr.SetValue(ghost, new IntPtr(1));
            return ghost;
        }

        private bool AnyLog(string tag, string text)
        {
            return logLines.Any(l => l.Contains("[" + tag + "]") && l.Contains(text));
        }

        // ---- TryGetLoopSchedule / GetLoopIntervalSeconds / TryComputeLoopPlaybackUT ----

        [Fact]
        public void LoopSchedule_NullRecording_FalseWithZeroedOutputs()
        {
            var host = NewHost();
            bool ok = host.PlaybackHost.TryGetLoopSchedule(
                null, -1, out double start, out double schedule, out double duration, out double interval);

            Assert.False(ok);
            Assert.Equal(0.0, start);
            Assert.Equal(0.0, schedule);
            Assert.Equal(0.0, duration);
            Assert.Equal(0.0, interval);
        }

        [Fact]
        public void LoopSchedule_NotLooping_False()
        {
            var host = NewHost();
            Recording rec = Rec("plain", 100.0, 160.0);

            Assert.False(host.PlaybackHost.TryGetLoopSchedule(
                rec, -1, out _, out _, out _, out _));
        }

        [Fact]
        public void LoopSchedule_ManualInterval_NoIndex_UsesRecordingBoundsAndInterval()
        {
            var host = NewHost();
            Recording rec = LoopRec("loop", 100.0, 160.0, LoopTimeUnit.Sec, 90.0);

            bool ok = host.PlaybackHost.TryGetLoopSchedule(
                rec, -1, out double start, out double schedule, out double duration, out double interval);

            Assert.True(ok);
            Assert.Equal(100.0, start);
            Assert.Equal(100.0, schedule);
            Assert.Equal(60.0, duration);
            Assert.Equal(90.0, interval);
        }

        [Fact]
        public void LoopSchedule_LoopRangeInsideRecording_NarrowsStartAndDuration()
        {
            var host = NewHost();
            Recording rec = LoopRec("trimmed", 100.0, 160.0);
            rec.LoopStartUT = 110.0;
            rec.LoopEndUT = 150.0;

            Assert.True(host.PlaybackHost.TryGetLoopSchedule(
                rec, -1, out double start, out double schedule, out double duration, out _));
            Assert.Equal(110.0, start);
            Assert.Equal(110.0, schedule);
            Assert.Equal(40.0, duration);
        }

        [Fact]
        public void LoopSchedule_AutoUnit_NoIndex_UsesTheSettingsAutoInterval()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { autoLoopIntervalSeconds = 45f };
            var host = NewHost();
            Recording rec = LoopRec("auto", 100.0, 160.0, LoopTimeUnit.Auto);

            Assert.True(host.PlaybackHost.TryGetLoopSchedule(
                rec, -1, out _, out double schedule, out _, out double interval));
            Assert.Equal(100.0, schedule);
            Assert.Equal(45.0, interval);
        }

        [Fact]
        public void LoopSchedule_AutoUnit_WithIndex_TakesItsSlotInTheAutoLaunchQueue()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { autoLoopIntervalSeconds = 45f };
            var host = NewHost();
            Commit(LoopRec("auto-a", 100.0, 160.0, LoopTimeUnit.Auto));
            Recording second = Commit(LoopRec("auto-b", 200.0, 230.0, LoopTimeUnit.Auto));

            Assert.True(host.PlaybackHost.TryGetLoopSchedule(
                second, 1, out double start, out double schedule, out double duration, out double interval));

            // Slot 1 of a two-member queue anchored at the earliest start: anchor + one gap,
            // cadence = gap * queue size; the playback window itself is the recording's own.
            Assert.Equal(200.0, start);
            Assert.Equal(145.0, schedule);
            Assert.Equal(30.0, duration);
            Assert.Equal(90.0, interval);
        }

        [Fact]
        public void LoopSchedule_AutoUnit_FirstSlot_KeepsItsOwnStartAndQueueCadence()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { autoLoopIntervalSeconds = 45f };
            var host = NewHost();
            Recording first = Commit(LoopRec("auto-a", 100.0, 160.0, LoopTimeUnit.Auto));
            Commit(LoopRec("auto-b", 200.0, 230.0, LoopTimeUnit.Auto));

            Assert.True(host.PlaybackHost.TryGetLoopSchedule(
                first, 0, out _, out double schedule, out _, out double interval));
            Assert.Equal(100.0, schedule);
            Assert.Equal(90.0, interval);
        }

        [Fact]
        public void LoopSchedule_ManualUnit_WithIndex_IgnoresTheAutoQueue()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { autoLoopIntervalSeconds = 45f };
            var host = NewHost();
            Commit(LoopRec("auto-a", 100.0, 160.0, LoopTimeUnit.Auto));
            Recording manual = Commit(LoopRec("manual", 200.0, 230.0, LoopTimeUnit.Sec, 70.0));

            Assert.True(host.PlaybackHost.TryGetLoopSchedule(
                manual, 1, out _, out double schedule, out _, out double interval));
            Assert.Equal(200.0, schedule);
            Assert.Equal(70.0, interval);
        }

        [Fact]
        public void LoopIntervalSeconds_NonLoopingRecording_FallsBackToTheEngineInterval()
        {
            var host = NewHost();
            Recording rec = Rec("plain", 100.0, 160.0);
            rec.LoopTimeUnit = LoopTimeUnit.Sec;
            rec.LoopIntervalSeconds = 75.0;

            Assert.Equal(75.0, host.PlaybackHost.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void LoopIntervalSeconds_NonLoopingRecording_BelowMinimumIsClampedByTheEngine()
        {
            var host = NewHost();
            Recording rec = Rec("plain", 100.0, 160.0);
            rec.LoopTimeUnit = LoopTimeUnit.Sec;
            rec.LoopIntervalSeconds = 1.0;

            Assert.Equal(LoopTiming.MinCycleDuration, host.PlaybackHost.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void LoopIntervalSeconds_AutoQueueMember_ReturnsTheQueueCadence()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { autoLoopIntervalSeconds = 45f };
            var host = NewHost();
            Commit(LoopRec("auto-a", 100.0, 160.0, LoopTimeUnit.Auto));
            Recording second = Commit(LoopRec("auto-b", 200.0, 230.0, LoopTimeUnit.Auto));

            Assert.Equal(90.0, host.PlaybackHost.GetLoopIntervalSeconds(second, 1));
            Assert.Equal(45.0, host.PlaybackHost.GetLoopIntervalSeconds(second));
        }

        [Fact]
        public void LoopPlaybackUT_NotLooping_FalseWithZeroedOutputs()
        {
            var host = NewHost();
            bool ok = host.PlaybackHost.TryComputeLoopPlaybackUT(
                Rec("plain", 100.0, 160.0), 130.0, out double loopUT, out long cycle, out bool pause);

            Assert.False(ok);
            Assert.Equal(0.0, loopUT);
            Assert.Equal(0L, cycle);
            Assert.False(pause);
        }

        [Fact]
        public void LoopPlaybackUT_BeforeScheduleStart_False()
        {
            var host = NewHost();
            Assert.False(host.PlaybackHost.TryComputeLoopPlaybackUT(
                LoopRec("loop", 100.0, 160.0), 99.0, out _, out _, out _));
        }

        [Theory]
        [InlineData(130.0, 130.0, 0L, false)]
        [InlineData(200.0, 110.0, 1L, false)]
        [InlineData(175.0, 160.0, 0L, true)]
        public void LoopPlaybackUT_MapsTheClockIntoTheLoopWindow(
            double currentUT, double expectedLoopUT, long expectedCycle, bool expectedPause)
        {
            var host = NewHost();
            Recording rec = LoopRec("loop", 100.0, 160.0, LoopTimeUnit.Sec, 90.0);

            Assert.True(host.PlaybackHost.TryComputeLoopPlaybackUT(
                rec, currentUT, out double loopUT, out long cycle, out bool pause));
            Assert.Equal(expectedLoopUT, loopUT, 6);
            Assert.Equal(expectedCycle, cycle);
            Assert.Equal(expectedPause, pause);
        }

        [Fact]
        public void LoopPlaybackUT_PhaseOffsetAppliesOnlyWithARecordingIndex()
        {
            var host = NewHost();
            Recording rec = Commit(LoopRec("loop", 100.0, 160.0, LoopTimeUnit.Sec, 90.0));
            host.engine.loopPhaseOffsets[0] = 20.0;

            // A comma-decimal culture proves the asserted log line is invariant
            // (the dev machine runs ro-RO; CI's culture would hide a regression).
            var savedCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            try
            {
                Assert.True(host.PlaybackHost.TryComputeLoopPlaybackUT(
                    rec, 130.0, out double withIndex, out _, out _, recIdx: 0));
                Assert.True(host.PlaybackHost.TryComputeLoopPlaybackUT(
                    rec, 130.0, out double withoutIndex, out _, out _));

                Assert.Equal(150.0, withIndex, 6);
                Assert.Equal(130.0, withoutIndex, 6);
                Assert.True(AnyLog("Engine", "applying phase offset 20.00s for recIdx=0"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = savedCulture;
            }
        }

        [Fact]
        public void LoopPlaybackUT_PhaseOffsetOfAnotherIndexIsNotApplied()
        {
            var host = NewHost();
            Recording rec = Commit(LoopRec("loop", 100.0, 160.0, LoopTimeUnit.Sec, 90.0));
            host.engine.loopPhaseOffsets[1] = 20.0;

            Assert.True(host.PlaybackHost.TryComputeLoopPlaybackUT(
                rec, 130.0, out double loopUT, out _, out _, recIdx: 0));
            Assert.Equal(130.0, loopUT, 6);
        }

        // ---- IsActiveTreeRecording ----

        private static RecordingTree CommitTree(string treeId, string activeRecordingId)
        {
            var tree = new RecordingTree { Id = treeId, ActiveRecordingId = activeRecordingId };
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        [Fact]
        public void ActiveTreeRecording_StandaloneRecording_False()
        {
            var host = NewHost();
            CommitTree("t1", "r1");
            var rec = new Recording { RecordingId = "r1", TreeId = null };

            Assert.False(host.PlaybackHost.IsActiveTreeRecording(rec));
        }

        [Fact]
        public void ActiveTreeRecording_TreeNotCommitted_False()
        {
            var host = NewHost();
            CommitTree("t-other", "r1");
            var rec = new Recording { RecordingId = "r1", TreeId = "t1" };

            Assert.False(host.PlaybackHost.IsActiveTreeRecording(rec));
        }

        [Fact]
        public void ActiveTreeRecording_TreeActiveRecordingMatches_True()
        {
            var host = NewHost();
            CommitTree("t0", "x");
            CommitTree("t1", "r1");
            var rec = new Recording { RecordingId = "r1", TreeId = "t1" };

            Assert.True(host.PlaybackHost.IsActiveTreeRecording(rec));
        }

        [Fact]
        public void ActiveTreeRecording_TreeActiveRecordingDiffers_False()
        {
            var host = NewHost();
            CommitTree("t1", "r2");
            var rec = new Recording { RecordingId = "r1", TreeId = "t1" };

            Assert.False(host.PlaybackHost.IsActiveTreeRecording(rec));
        }

        [Fact]
        public void ActiveTreeRecording_NullActiveIdNeverMatchesANullRecordingId()
        {
            var host = NewHost();
            CommitTree("t1", null);
            var rec = new Recording { RecordingId = null, TreeId = "t1" };

            Assert.False(host.PlaybackHost.IsActiveTreeRecording(rec));
        }

        // ---- IsChainSeamSuccessorAtFrame ----

        private static Recording ChainRec(string id, int chainIndex, double startUT, double endUT,
            string chainId = "chain-1", int chainBranch = 0)
        {
            Recording rec = Rec(id, startUT, endUT);
            rec.ChainId = chainId;
            rec.ChainIndex = chainIndex;
            rec.ChainBranch = chainBranch;
            return rec;
        }

        private static ParsekFlight SeamHost(out Recording successor, bool predecessorGhost = true)
        {
            var host = NewHost();
            Commit(ChainRec("pred", 0, 100.0, 200.0));
            successor = Commit(ChainRec("succ", 1, 200.0, 300.0));
            if (predecessorGhost)
                host.engine.ghostStates[0] = new GhostPlaybackState();
            return host;
        }

        [Fact]
        public void ChainSeam_NullRecording_False()
        {
            var host = SeamHost(out _);
            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(null, 250.0));
        }

        [Fact]
        public void ChainSeam_PredecessorGhostAndClockAtSeam_True()
        {
            var host = SeamHost(out Recording succ);
            Assert.True(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 200.0));
            Assert.True(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 250.0));
        }

        [Fact]
        public void ChainSeam_ClockBeforeSeam_FalseButSubMicrosecondEarlyCounts()
        {
            var host = SeamHost(out Recording succ);
            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 199.9));
            Assert.True(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 200.0 - 5e-7));
        }

        [Fact]
        public void ChainSeam_PredecessorWithoutGhostState_False()
        {
            var host = SeamHost(out Recording succ, predecessorGhost: false);
            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 250.0));
        }

        [Fact]
        public void ChainSeam_PredecessorGhostSlotHoldsNull_False()
        {
            var host = SeamHost(out Recording succ, predecessorGhost: false);
            host.engine.ghostStates[0] = null;
            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 250.0));
        }

        [Fact]
        public void ChainSeam_NoEngine_False()
        {
            var host = SeamHost(out Recording succ);
            host.engine = null;
            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 250.0));
        }

        [Fact]
        public void ChainSeam_FirstChainSegment_False()
        {
            var host = SeamHost(out _);
            Recording first = RecordingStore.CommittedRecordings[0];
            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(first, 250.0));
        }

        [Fact]
        public void ChainSeam_UnchainedOrBranchRecording_False()
        {
            var host = SeamHost(out _);
            Recording unchained = Rec("solo", 200.0, 300.0);
            Recording branch = ChainRec("branch", 1, 200.0, 300.0, chainBranch: 1);

            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(unchained, 250.0));
            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(branch, 250.0));
        }

        [Fact]
        public void ChainSeam_PredecessorNotCommitted_False()
        {
            var host = NewHost();
            host.engine.ghostStates[0] = new GhostPlaybackState();
            Commit(ChainRec("other-chain", 0, 100.0, 200.0, chainId: "chain-2"));
            Recording succ = Commit(ChainRec("succ", 1, 200.0, 300.0));

            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 250.0));
        }

        [Fact]
        public void ChainSeam_ReadsThePredecessorsOwnGhostSlot()
        {
            var host = NewHost();
            Commit(Rec("filler", 0.0, 10.0));
            Commit(ChainRec("pred", 0, 100.0, 200.0));
            Recording succ = Commit(ChainRec("succ", 1, 200.0, 300.0));
            host.engine.ghostStates[0] = new GhostPlaybackState(); // the filler's slot, not the predecessor's

            Assert.False(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 250.0));
            host.engine.ghostStates[1] = new GhostPlaybackState();
            Assert.True(host.PlaybackHost.IsChainSeamSuccessorAtFrame(succ, 250.0));
        }

        // ---- FindTipRecordingById / ResolveLiveRecorderRecordingId ----

        [Fact]
        public void FindTipRecordingById_ReturnsTheCommittedRecording()
        {
            Commit(Rec("a", 0.0, 1.0));
            Recording b = Commit(Rec("b", 0.0, 1.0));

            Assert.Same(b, ParsekFlightPlaybackHost.FindTipRecordingById("b"));
        }

        [Fact]
        public void FindTipRecordingById_UnknownId_Null()
        {
            Commit(Rec("a", 0.0, 1.0));
            Assert.Null(ParsekFlightPlaybackHost.FindTipRecordingById("zzz"));
        }

        [Fact]
        public void FindTipRecordingById_DuplicateIds_FirstCommittedWins()
        {
            Recording first = Commit(Rec("dup", 0.0, 1.0));
            Commit(Rec("dup", 5.0, 6.0));

            Assert.Same(first, ParsekFlightPlaybackHost.FindTipRecordingById("dup"));
        }

        private static FlightRecorder Recorder(bool isRecording)
        {
            var recorder = (FlightRecorder)FormatterServices.GetUninitializedObject(typeof(FlightRecorder));
            recorder.IsRecording = isRecording;
            return recorder;
        }

        [Fact]
        public void LiveRecorderRecordingId_NoRecorderAndNoParkedSplit_Null()
        {
            var host = NewHost();
            host.activeTree = new RecordingTree { Id = "t", ActiveRecordingId = "live" };

            Assert.Null(host.PlaybackHost.ResolveLiveRecorderRecordingId());
        }

        [Fact]
        public void LiveRecorderRecordingId_RecordingRecorder_ReturnsTheActiveTreeLeaf()
        {
            var host = NewHost();
            host.recorder = Recorder(true);
            host.activeTree = new RecordingTree { Id = "t", ActiveRecordingId = "live" };

            Assert.Equal("live", host.PlaybackHost.ResolveLiveRecorderRecordingId());
        }

        [Fact]
        public void LiveRecorderRecordingId_IdleRecorder_Null()
        {
            var host = NewHost();
            host.recorder = Recorder(false);
            host.activeTree = new RecordingTree { Id = "t", ActiveRecordingId = "live" };

            Assert.Null(host.PlaybackHost.ResolveLiveRecorderRecordingId());
        }

        [Fact]
        public void LiveRecorderRecordingId_ParkedSplitRecorderKeepsTheRecordingLive()
        {
            var host = NewHost();
            host.recorder = Recorder(false);
            host.pendingSplitRecorder = Recorder(false);
            host.activeTree = new RecordingTree { Id = "t", ActiveRecordingId = "live" };

            Assert.Equal("live", host.PlaybackHost.ResolveLiveRecorderRecordingId());
        }

        [Fact]
        public void LiveRecorderRecordingId_RecordingWithoutActiveTree_Null()
        {
            var host = NewHost();
            host.recorder = Recorder(true);

            Assert.Null(host.PlaybackHost.ResolveLiveRecorderRecordingId());
        }

        // ---- ReleaseStaleBlockedChainTipGhosts ----

        private const uint ChainPid = 5001u;

        private sealed class TipFixture
        {
            public ParsekFlight Host;
            public GhostChain Chain;
            public GameObject Ghost;
            public ParsekFlight.BlockedChainTipGhostState State;
            public Recording Tip;
            public int TipIndex;
        }

        // Committed: r0, r1, tip (index 2), r3. Chain blocked with the tip held by the policy and
        // drawn by the engine slot; the blocked-tip hold captured exactly that ghost.
        private static TipFixture HeldTip()
        {
            var f = new TipFixture { Host = NewHost() };
            Commit(Rec("r0", 0.0, 10.0));
            Commit(Rec("r1", 0.0, 10.0));
            f.Tip = Commit(Rec("tip", 0.0, 10.0));
            Commit(Rec("r3", 0.0, 10.0));
            f.TipIndex = 2;
            f.Chain = new GhostChain
            {
                OriginalVesselPid = ChainPid,
                TipRecordingId = "tip",
                SpawnBlocked = true,
            };
            f.Host.activeGhostChains[ChainPid] = f.Chain;
            f.Ghost = FakeLoadedGhost();
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = f.Ghost };
            f.Host.policy.heldGhosts[f.TipIndex] = new HeldGhostInfo { recordingId = "tip" };
            f.State = new ParsekFlight.BlockedChainTipGhostState
            {
                chain = f.Chain,
                chainPid = ChainPid,
                index = f.TipIndex,
                recordingId = "tip",
                vesselName = "Tip Vessel",
                ghost = f.Ghost,
                heldSinceUT = 50.0,
            };
            f.Host.PlaybackHost.blockedChainTipGhosts[ChainPid] = f.State;
            return f;
        }

        private void ReleasePass(TipFixture f, double ut = 80.0)
        {
            f.Host.PlaybackHost.ReleaseStaleBlockedChainTipGhosts(RecordingStore.CommittedRecordings, ut);
        }

        private void AssertReleased(TipFixture f, string reason)
        {
            Assert.False(f.Host.PlaybackHost.blockedChainTipGhosts.ContainsKey(ChainPid));
            Assert.True(AnyLog(ChainTipBlockedGhost.Tag, "Blocked chain tip ghost released:"));
            Assert.True(AnyLog(ChainTipBlockedGhost.Tag, "reason=" + reason + " "),
                "expected release reason " + reason + "; log:\n" + string.Join("\n", logLines));
        }

        private void AssertKept(TipFixture f)
        {
            Assert.True(f.Host.PlaybackHost.blockedChainTipGhosts.TryGetValue(ChainPid, out var kept));
            Assert.Same(f.State, kept);
            Assert.False(AnyLog(ChainTipBlockedGhost.Tag, "Blocked chain tip ghost released:"));
        }

        [Fact]
        public void BlockedTipRelease_EverythingIntact_KeepsTheHold()
        {
            TipFixture f = HeldTip();
            ReleasePass(f);
            AssertKept(f);
        }

        [Fact]
        public void BlockedTipRelease_EmptySet_IsANoOp()
        {
            var host = NewHost();
            host.PlaybackHost.ReleaseStaleBlockedChainTipGhosts(RecordingStore.CommittedRecordings, 80.0);
            Assert.Empty(host.PlaybackHost.blockedChainTipGhosts);
            Assert.Empty(logLines.Where(l => l.Contains("[" + ChainTipBlockedGhost.Tag + "]")));
        }

        [Fact]
        public void BlockedTipRelease_TipSpawned_ReleasesAsSpawned()
        {
            TipFixture f = HeldTip();
            f.Tip.VesselSpawned = true;
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseSpawned);
        }

        [Fact]
        public void BlockedTipRelease_ReleaseLineNamesTheHoldAndItsDuration()
        {
            TipFixture f = HeldTip();
            f.Tip.VesselSpawned = true;
            ReleasePass(f, 80.0);
            Assert.True(AnyLog(ChainTipBlockedGhost.Tag,
                "#2 \"Tip Vessel\" rec=tip chainPid=5001 reason=spawned heldSince=50.0 UT=80.0 heldFor=30.0s"));
        }

        [Fact]
        public void BlockedTipRelease_ChainLeftTheActiveSet_ReleasesAsChainClosed()
        {
            TipFixture f = HeldTip();
            f.Host.activeGhostChains.Remove(ChainPid);
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseChainClosed);
        }

        [Fact]
        public void BlockedTipRelease_ChainRebuiltUnderTheSamePid_ReleasesAsChainClosed()
        {
            TipFixture f = HeldTip();
            f.Host.activeGhostChains[ChainPid] = new GhostChain
            {
                OriginalVesselPid = ChainPid,
                TipRecordingId = "tip",
                SpawnBlocked = true,
            };
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseChainClosed);
        }

        [Fact]
        public void BlockedTipRelease_ChainTerminated_ReleasesAsChainClosed()
        {
            TipFixture f = HeldTip();
            f.Chain.IsTerminated = true;
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseChainClosed);
        }

        [Fact]
        public void BlockedTipRelease_NoChainDictionary_ReleasesAsChainClosed()
        {
            TipFixture f = HeldTip();
            f.Host.activeGhostChains = null;
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseChainClosed);
        }

        [Fact]
        public void BlockedTipRelease_ChainNoLongerBlocked_ReleasesAsUnblocked()
        {
            TipFixture f = HeldTip();
            f.Chain.SpawnBlocked = false;
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseUnblocked);
        }

        [Fact]
        public void BlockedTipRelease_CommittedRemovalBelowTheTip_ReleasesAsIndexShifted()
        {
            TipFixture f = HeldTip();
            Assert.True(RecordingStore.RemoveCommittedById("r0"));
            // The tip now sits at index 1; the hold still names index 2, where r3 now lives.
            Assert.Equal("r3", RecordingStore.CommittedRecordings[f.TipIndex].RecordingId);
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseIndexShifted);
        }

        [Fact]
        public void BlockedTipRelease_CommittedInsertBelowTheTip_ReleasesAsIndexShifted()
        {
            TipFixture f = HeldTip();
            RecordingStore.InsertCommittedAfter("r0", Rec("inserted", 0.0, 10.0));
            Assert.Equal("r1", RecordingStore.CommittedRecordings[f.TipIndex].RecordingId);
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseIndexShifted);
        }

        [Fact]
        public void BlockedTipRelease_ShiftedIndexWinsOverTheTipsSpawnedFlag()
        {
            TipFixture f = HeldTip();
            f.Tip.VesselSpawned = true;
            Assert.True(RecordingStore.RemoveCommittedById("r0"));
            // The recording that slid into the stale slot is spawned too: neither its flag nor
            // the moved tip's may read as this hold's tip having spawned.
            RecordingStore.CommittedRecordings[f.TipIndex].VesselSpawned = true;
            ReleasePass(f);
            // The spawned flag is read only through a still-valid index.
            AssertReleased(f, ChainTipBlockedGhost.ReleaseIndexShifted);
        }

        [Fact]
        public void BlockedTipRelease_CommittedChangesAboveTheTip_KeepTheHold()
        {
            TipFixture f = HeldTip();
            Assert.True(RecordingStore.RemoveCommittedById("r3"));
            RecordingStore.InsertCommittedAfter("tip", Rec("after-tip", 0.0, 10.0));
            ReleasePass(f);
            AssertKept(f);
        }

        [Fact]
        public void BlockedTipRelease_ListShrankBelowTheIndex_ReleasesAsIndexShifted()
        {
            TipFixture f = HeldTip();
            Assert.True(RecordingStore.RemoveCommittedById("r3"));
            Assert.True(RecordingStore.RemoveCommittedById("tip"));
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseIndexShifted);
        }

        [Fact]
        public void BlockedTipRelease_NullCommittedList_ReleasesAsIndexShifted()
        {
            TipFixture f = HeldTip();
            f.Host.PlaybackHost.ReleaseStaleBlockedChainTipGhosts(null, 80.0);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseIndexShifted);
        }

        [Fact]
        public void BlockedTipRelease_PolicyDroppedTheHold_ReleasesAsHoldEnded()
        {
            TipFixture f = HeldTip();
            f.Host.policy.heldGhosts.Remove(f.TipIndex);
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseHoldEnded);
        }

        [Fact]
        public void BlockedTipRelease_NoPolicy_ReleasesAsHoldEnded()
        {
            TipFixture f = HeldTip();
            f.Host.policy = null;
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseHoldEnded);
        }

        [Fact]
        public void BlockedTipRelease_EngineSlotCarriesAnotherGhost_ReleasesAsGhostGone()
        {
            TipFixture f = HeldTip();
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseGhostGone);
        }

        [Fact]
        public void BlockedTipRelease_EngineSlotEmpty_ReleasesAsGhostGone()
        {
            TipFixture f = HeldTip();
            f.Host.engine.ghostStates.Remove(f.TipIndex);
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseGhostGone);
        }

        [Fact]
        public void BlockedTipRelease_HoldAndSlotBothWithoutAGhost_ReleasesAsGhostGone()
        {
            TipFixture f = HeldTip();
            f.State.ghost = null;
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = null };
            ReleasePass(f);
            AssertReleased(f, ChainTipBlockedGhost.ReleaseGhostGone);
        }

        [Fact]
        public void BlockedTipRelease_ReleasesOnlyTheStaleHold()
        {
            TipFixture f = HeldTip();
            const uint otherPid = 7007u;
            var otherChain = new GhostChain { OriginalVesselPid = otherPid, TipRecordingId = "r1", SpawnBlocked = true };
            f.Host.activeGhostChains[otherPid] = otherChain;
            var otherState = new ParsekFlight.BlockedChainTipGhostState
            {
                chain = otherChain, chainPid = otherPid, index = 1, recordingId = "r1", ghost = FakeLoadedGhost(),
            };
            f.Host.PlaybackHost.blockedChainTipGhosts[otherPid] = otherState;
            f.Host.policy.heldGhosts[1] = new HeldGhostInfo { recordingId = "r1" };
            f.Host.engine.ghostStates[1] = new GhostPlaybackState { ghost = otherState.ghost };
            f.Tip.VesselSpawned = true;

            ReleasePass(f);

            Assert.False(f.Host.PlaybackHost.blockedChainTipGhosts.ContainsKey(ChainPid));
            Assert.True(f.Host.PlaybackHost.blockedChainTipGhosts.ContainsKey(otherPid));
            Assert.Single(logLines.Where(l => l.Contains("Blocked chain tip ghost released:")));
        }

        // ---- CaptureBlockedChainTipGhosts: every gate that declines a capture ----
        // (A capture itself resolves the spawn body through live KSP and is covered in game.)

        private static TipFixture HeldTipNotYetCaptured()
        {
            TipFixture f = HeldTip();
            f.Host.PlaybackHost.blockedChainTipGhosts.Clear();
            // No engine ghost: the last gate declines, so a test that clears one earlier gate
            // still never reaches the live capture.
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = null };
            return f;
        }

        private void CapturePass(TipFixture f)
        {
            f.Host.PlaybackHost.CaptureBlockedChainTipGhosts(RecordingStore.CommittedRecordings, 80.0);
        }

        private void AssertNothingCaptured(TipFixture f)
        {
            Assert.Empty(f.Host.PlaybackHost.blockedChainTipGhosts);
            Assert.False(AnyLog(ChainTipBlockedGhost.Tag, "Blocked chain tip ghost"));
        }

        [Fact]
        public void BlockedTipCapture_EngineHasNoGhost_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_NoActiveChains_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            f.Host.activeGhostChains.Clear();
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_NoEngine_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.engine = null;
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_NullCommittedList_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            f.Host.PlaybackHost.CaptureBlockedChainTipGhosts(null, 80.0);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_HeldIndexOutOfRange_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.policy.heldGhosts.Clear();
            f.Host.policy.heldGhosts[99] = new HeldGhostInfo { recordingId = "tip" };
            f.Host.policy.heldGhosts[-1] = new HeldGhostInfo { recordingId = "tip" };
            f.Host.engine.ghostStates[99] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_HeldRecordingIsNotAChainTip_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.policy.heldGhosts.Clear();
            f.Host.policy.heldGhosts[1] = new HeldGhostInfo { recordingId = "r1" };
            f.Host.engine.ghostStates[1] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_TerminatedChain_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            f.Chain.IsTerminated = true;
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_TipAlreadySpawned_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            f.Tip.VesselSpawned = true;
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_ChainNotSpawnBlocked_Declines()
        {
            TipFixture f = HeldTipNotYetCaptured();
            f.Host.engine.ghostStates[f.TipIndex] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            f.Chain.SpawnBlocked = false;
            CapturePass(f);
            AssertNothingCaptured(f);
        }

        [Fact]
        public void BlockedTipCapture_AlreadyCaptured_KeepsTheExistingHold()
        {
            TipFixture f = HeldTip();
            CapturePass(f);
            Assert.Same(f.State, f.Host.PlaybackHost.blockedChainTipGhosts[ChainPid]);
            Assert.Single(f.Host.PlaybackHost.blockedChainTipGhosts);
            Assert.False(AnyLog(ChainTipBlockedGhost.Tag, "Blocked chain tip ghost held"));
        }
    }
}
