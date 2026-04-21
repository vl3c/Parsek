using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    #region Bug #64 — Discard pending on revert

    /// <summary>
    /// Tests that pending tree/recording state is detected and would be
    /// cleared during a revert. The actual discard is called in ParsekScenario.OnLoad
    /// (KSP runtime), so we test the guard conditions and discard behavior.
    /// </summary>
    [Collection("Sequential")]
    public class Bug64_DiscardPendingOnRevertTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug64_DiscardPendingOnRevertTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void HasPendingTree_WhenTreeStashed_ReturnsTrue()
        {
            var tree = new RecordingTree { Id = "test-tree-64" };
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void DiscardPendingTree_ClearsPendingState()
        {
            var tree = new RecordingTree { Id = "test-tree-64-discard" };
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.HasPendingTree);

            RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void CreateRecordingFromFlightData_WithPoints_ReturnsNonNull()
        {
            // Need at least 2 points for CreateRecordingFromFlightData to accept
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 200.0 }
            };
            var rec = RecordingStore.CreateRecordingFromFlightData(points, "Test Vessel");

            Assert.NotNull(rec);
        }

        /// <summary>
        /// The revert guard should clear pending tree when it exists.
        /// Standalone pending no longer exists.
        /// </summary>
        [Fact]
        public void DiscardPendingTree_ClearsAllPendingState()
        {
            var tree = new RecordingTree { Id = "test-tree-64-both" };
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.HasPendingTree);

            // Simulate the revert guard from ParsekScenario.OnLoad
            if (RecordingStore.HasPendingTree)
                RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
        }
    }

    #endregion

    #region Bug #79 — SpawnCrossedChainTips returns spawned PIDs without mutating input

    [Collection("Sequential")]
    public class Bug79_SpawnCrossedChainTipsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug79_SpawnCrossedChainTipsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// With a null ghoster, crossed tips are found but not spawned.
        /// The input dict must NOT be mutated (#79 fix).
        /// </summary>
        [Fact]
        public void SpawnCrossedChainTips_DoesNotMutateInputDict()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1500.0) },
                { 200, MakeChain(200, 1600.0) },
                { 300, MakeChain(300, 2000.0) }  // not crossed
            };

            int originalCount = chains.Count;

            // ghoster is null so no actual spawning, but method should still not mutate dict
            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                chains, null, 1000.0, 1700.0);

            // Dict should be unchanged — method no longer mutates
            Assert.Equal(originalCount, chains.Count);
            Assert.True(chains.ContainsKey(100));
            Assert.True(chains.ContainsKey(200));
            Assert.True(chains.ContainsKey(300));

            // Return value is empty because ghoster is null (no actual spawn)
            Assert.Empty(spawnedPids);

            // Log should show skip warnings for null ghoster
            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("Spawn skipped (no ghoster)"));
        }

        [Fact]
        public void SpawnCrossedChainTips_ReturnsEmptyForNoCrossedTips()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 3000.0) }  // not crossed (too far in future)
            };

            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                chains, null, 1000.0, 1500.0);

            Assert.Empty(spawnedPids);
            Assert.Single(chains); // not mutated
        }

        [Fact]
        public void SpawnCrossedChainTips_ReturnsEmptyForNullChains()
        {
            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                null, null, 1000.0, 1500.0);

            Assert.Empty(spawnedPids);
        }

        [Fact]
        public void SpawnCrossedChainTips_ReturnsEmptyForEmptyChains()
        {
            var chains = new Dictionary<uint, GhostChain>();

            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                chains, null, 1000.0, 1500.0);

            Assert.Empty(spawnedPids);
            Assert.Empty(chains);
        }

        private static GhostChain MakeChain(uint pid, double spawnUT)
        {
            return new GhostChain
            {
                OriginalVesselPid = pid,
                SpawnUT = spawnUT,
                GhostStartUT = spawnUT - 100,
                TipRecordingId = $"rec-{pid}",
                TipTreeId = $"tree-{pid}",
                IsTerminated = false
            };
        }
    }

    #endregion

    #region Bug #84 — ComputeLoopPhaseFromUT integer overflow

    public class Bug84_LoopPhaseOverflowTests
    {
        /// <summary>
        /// #443: cycleDuration = max(period, MinCycleDuration=5). For a long-running loop,
        /// cycle count must not overflow int.MaxValue (2.147B). Use 15B elapsed seconds
        /// with period=5s → 3B cycles.
        /// </summary>
        [Fact]
        public void ComputeLoopPhaseFromUT_LargeElapsed_NoOverflow()
        {
            double startUT = 100.0;
            double endUT = 105.0;  // 5.0s recording
            double elapsed = 1.5e10; // 15 billion seconds, period=5s → 3 billion cycles
            double currentUT = startUT + elapsed;

            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT, startUT, endUT, intervalSeconds: 5.0);

            // Should NOT overflow — cycleIndex should be a large positive number
            Assert.True(cycleIndex > 0, $"cycleIndex should be positive, got {cycleIndex}");
            Assert.True(cycleIndex > int.MaxValue,
                $"cycleIndex should exceed int.MaxValue for this test case, got {cycleIndex}");
            Assert.InRange(cycleIndex, (long)(elapsed / 5.0) - 1, (long)(elapsed / 5.0) + 1);
            Assert.False(isInPause);
        }

        /// <summary>
        /// At the int.MaxValue boundary under MinCycleDuration=5 (#443), elapsed must
        /// exceed int.MaxValue * 5 to push cycle index past the overflow line.
        /// </summary>
        [Fact]
        public void ComputeLoopPhaseFromUT_IntMaxBoundary_HandledCorrectly()
        {
            double startUT = 0.0;
            double duration = 5.0;
            double interval = 0.0;
            // #443: cycleDuration = period clamped to MinCycleDuration (5.0s).
            double cycleDuration = Math.Max(interval, LoopTiming.MinCycleDuration); // = 5.0
            double elapsedAtIntMax = (double)int.MaxValue * cycleDuration;
            double currentUT = startUT + elapsedAtIntMax + 2.5; // midway through cycle int.MaxValue+1

            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT, startUT, startUT + duration, interval);

            // cycleIndex should be int.MaxValue (or just past it), not negative from overflow
            Assert.True(cycleIndex >= int.MaxValue,
                $"cycleIndex should be >= int.MaxValue, got {cycleIndex}");
            Assert.False(isInPause);
        }

        /// <summary>
        /// GetActiveCycles uses long for cycle counts. With MinCycleDuration=5 (#443),
        /// elapsed must exceed int.MaxValue * 5 ≈ 10.7B to overflow the int range.
        /// Use 15 billion seconds (~475 years) to be safely past that.
        /// </summary>
        [Fact]
        public void GetActiveCycles_LargeElapsed_NoOverflow()
        {
            double startUT = 0.0;
            double endUT = 5.0;  // 5.0s recording (won't be clamped)
            double elapsed = 1.5e10; // 15 billion seconds, 5.0s cycle = 3 billion cycles
            double currentUT = startUT + elapsed;

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(
                currentUT, startUT, endUT, intervalSeconds: 0.0,
                maxCycles: 20, out firstCycle, out lastCycle);

            Assert.True(lastCycle > int.MaxValue,
                $"lastCycle should exceed int.MaxValue, got {lastCycle}");
            Assert.True(firstCycle >= 0);
            Assert.True(lastCycle >= firstCycle);
        }

        /// <summary>
        /// TryComputeLoopPlaybackUT static overload handles large cycles.
        /// #443: use period=5 (= MinCycleDuration) with 10s duration so period < duration,
        /// no pause window. Elapsed 2.5e10 seconds → 5e9 cycles.
        /// </summary>
        [Fact]
        public void TryComputeLoopPlaybackUT_LargeElapsed_NoOverflow()
        {
            double startUT = 100.0;
            double endUT = 110.0; // 10.0s recording
            double elapsed = 2.5e10; // 25 billion seconds, period=5s → 5 billion cycles
            double currentUT = startUT + elapsed;

            bool result = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT, startUT, endUT, intervalSeconds: 5.0,
                out double loopUT, out long cycleIndex);

            Assert.True(result);
            Assert.True(cycleIndex > int.MaxValue,
                $"cycleIndex should exceed int.MaxValue, got {cycleIndex}");
        }
    }

    #endregion

    #region Bug #130 — GhostPlaybackState.vesselName

    public class Bug130_GhostPlaybackStateVesselNameTests
    {
        [Fact]
        public void VesselName_DefaultIsNull()
        {
            var state = new GhostPlaybackState();
            Assert.Null(state.vesselName);
        }

        [Fact]
        public void VesselName_CanBeAssigned()
        {
            var state = new GhostPlaybackState
            {
                vesselName = "Test Rocket"
            };
            Assert.Equal("Test Rocket", state.vesselName);
        }

        /// <summary>
        /// Verifies the fallback chain pattern used in DestroyGhost and HandleGhostDestroyed:
        /// state?.vesselName ?? traj?.VesselName ?? "Unknown"
        /// </summary>
        [Fact]
        public void VesselName_FallbackChain_UsesStateFirst()
        {
            var state = new GhostPlaybackState { vesselName = "From State" };
            string name = state.vesselName ?? "Unknown";
            Assert.Equal("From State", name);
        }

        [Fact]
        public void VesselName_FallbackChain_FallsToDefault()
        {
            var state = new GhostPlaybackState(); // vesselName is null
            string name = state.vesselName ?? "Unknown";
            Assert.Equal("Unknown", name);
        }
    }

    #endregion

    #region Bug #72 — Antenna combination exponent from strongest combinable

    [Collection("Sequential")]
    public class Bug72_AntennaCombinationExponentTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug72_AntennaCombinationExponentTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostCommNetRelay.ResetRemoteTechCacheForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostCommNetRelay.ResetRemoteTechCacheForTesting();
        }

        /// <summary>
        /// When the strongest antenna is combinable, ResolveCombinationExponent
        /// returns its own exponent (common case, unchanged behavior).
        /// </summary>
        [Fact]
        public void ResolveCombinationExponent_StrongestCombinable_ReturnsItsExponent()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "big", antennaPower = 1000000, antennaCombinable = true, antennaCombinableExponent = 0.75 },
                new AntennaSpec { partName = "small", antennaPower = 500000, antennaCombinable = true, antennaCombinableExponent = 0.5 }
            };

            var (exponent, sourceIdx) = GhostCommNetRelay.ResolveCombinationExponent(specs, strongestIdx: 0);

            Assert.Equal(0.75, exponent);
            Assert.Equal(0, sourceIdx);
        }

        /// <summary>
        /// When the strongest antenna is non-combinable, ResolveCombinationExponent
        /// returns the strongest *combinable* antenna's exponent (Bug #72 fix).
        /// </summary>
        [Fact]
        public void ResolveCombinationExponent_StrongestNonCombinable_UsesStrongestCombinable()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "internal", antennaPower = 5000000, antennaCombinable = false, antennaCombinableExponent = 1.0 },
                new AntennaSpec { partName = "relay", antennaPower = 1000000, antennaCombinable = true, antennaCombinableExponent = 0.75 },
                new AntennaSpec { partName = "smallRelay", antennaPower = 500000, antennaCombinable = true, antennaCombinableExponent = 0.5 }
            };

            var (exponent, sourceIdx) = GhostCommNetRelay.ResolveCombinationExponent(specs, strongestIdx: 0);

            // Should use the strongest combinable (relay at index 1), not the overall strongest (internal at index 0)
            Assert.Equal(0.75, exponent);
            Assert.Equal(1, sourceIdx);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("non-combinable") && l.Contains("relay"));
        }

        /// <summary>
        /// When no antenna is combinable, ResolveCombinationExponent returns sourceIndex -1.
        /// </summary>
        [Fact]
        public void ResolveCombinationExponent_NoCombinable_ReturnsMinusOne()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "a1", antennaPower = 5000000, antennaCombinable = false, antennaCombinableExponent = 1.0 },
                new AntennaSpec { partName = "a2", antennaPower = 1000000, antennaCombinable = false, antennaCombinableExponent = 0.75 }
            };

            var (exponent, sourceIdx) = GhostCommNetRelay.ResolveCombinationExponent(specs, strongestIdx: 0);

            Assert.Equal(-1, sourceIdx);
        }

        /// <summary>
        /// End-to-end: combined power with non-combinable strongest should use
        /// the combinable antenna's exponent, producing different results than
        /// the old buggy code.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_NonCombinableStrongest_UsesCorrectExponent()
        {
            // Non-combinable strongest with exponent 1.0 (would produce wrong result if used)
            // Two combinable antennas with exponent 0.75
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "probeCore", antennaPower = 5000000, antennaCombinable = false, antennaCombinableExponent = 1.0 },
                new AntennaSpec { partName = "relay1", antennaPower = 1000000, antennaCombinable = true, antennaCombinableExponent = 0.75 },
                new AntennaSpec { partName = "relay2", antennaPower = 500000, antennaCombinable = true, antennaCombinableExponent = 0.75 }
            };

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            // Strongest = 5000000 (probeCore, non-combinable)
            // Exponent should come from relay1 (strongest combinable) = 0.75
            // relay1 contribution: 1000000 * (1000000/5000000)^0.75
            // relay2 contribution: 500000 * (500000/5000000)^0.75
            double ratio1 = 1000000.0 / 5000000.0;
            double ratio2 = 500000.0 / 5000000.0;
            double expected = 5000000.0 + 1000000.0 * Math.Pow(ratio1, 0.75) + 500000.0 * Math.Pow(ratio2, 0.75);
            Assert.Equal(expected, result, 1);

            // If the old buggy code used exponent 1.0 instead of 0.75, the result would be:
            double wrongExpected = 5000000.0 + 1000000.0 * Math.Pow(ratio1, 1.0) + 500000.0 * Math.Pow(ratio2, 1.0);
            Assert.NotEqual(wrongExpected, result, 1);
        }
    }

    #endregion

    #region Bug #81 — TrackSection deep copy

    public class Bug81_TrackSectionDeepCopyTests
    {
        /// <summary>
        /// Modifying a copied recording's TrackSection frames must not affect the original.
        /// </summary>
        [Fact]
        public void DeepCopyTrackSections_FramesMutationDoesNotAffectOriginal()
        {
            var original = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    startUT = 100.0,
                    endUT = 200.0,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 100.0, latitude = 1.0 },
                        new TrajectoryPoint { ut = 150.0, latitude = 2.0 }
                    },
                    checkpoints = new List<OrbitSegment>
                    {
                        new OrbitSegment { startUT = 100.0 }
                    }
                }
            };

            var copy = Recording.DeepCopyTrackSections(original);

            // Mutate the copy's frames
            copy[0].frames.Add(new TrajectoryPoint { ut = 175.0, latitude = 3.0 });
            copy[0].checkpoints.Add(new OrbitSegment { startUT = 200.0 });

            // Original must be unaffected
            Assert.Equal(2, original[0].frames.Count);
            Assert.Single(original[0].checkpoints);
            Assert.Equal(3, copy[0].frames.Count);
            Assert.Equal(2, copy[0].checkpoints.Count);
        }

        /// <summary>
        /// Null frames/checkpoints in source should remain null in copy.
        /// </summary>
        [Fact]
        public void DeepCopyTrackSections_NullLists_StayNull()
        {
            var original = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    startUT = 300.0,
                    endUT = 400.0,
                    frames = null,
                    checkpoints = null
                }
            };

            var copy = Recording.DeepCopyTrackSections(original);

            Assert.Null(copy[0].frames);
            Assert.Null(copy[0].checkpoints);
        }

        /// <summary>
        /// Scalar fields (startUT, endUT, environment) are copied correctly.
        /// </summary>
        [Fact]
        public void DeepCopyTrackSections_ScalarFieldsCopied()
        {
            var original = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceMobile,
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 500.0,
                    endUT = 600.0,
                    sampleRateHz = 10.0f,
                    source = TrackSectionSource.Background,
                    boundaryDiscontinuityMeters = 1.5f,
                    frames = new List<TrajectoryPoint>()
                }
            };

            var copy = Recording.DeepCopyTrackSections(original);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, copy[0].environment);
            Assert.Equal(ReferenceFrame.Relative, copy[0].referenceFrame);
            Assert.Equal(500.0, copy[0].startUT);
            Assert.Equal(600.0, copy[0].endUT);
            Assert.Equal(10.0f, copy[0].sampleRateHz);
            Assert.Equal(TrackSectionSource.Background, copy[0].source);
            Assert.Equal(1.5f, copy[0].boundaryDiscontinuityMeters);
        }

        /// <summary>
        /// ApplyPersistenceArtifactsFrom should deep copy TrackSections.
        /// </summary>
        [Fact]
        public void ApplyPersistenceArtifactsFrom_TrackSectionsMutationDoesNotAffectSource()
        {
            var source = new Recording();
            source.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100.0,
                    endUT = 200.0,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 100.0 },
                        new TrajectoryPoint { ut = 200.0 }
                    }
                }
            };

            var dest = new Recording();
            dest.ApplyPersistenceArtifactsFrom(source);

            // Mutate the dest's TrackSection frames
            dest.TrackSections[0].frames.Add(new TrajectoryPoint { ut = 300.0 });

            // Source must be unaffected
            Assert.Equal(2, source.TrackSections[0].frames.Count);
            Assert.Equal(3, dest.TrackSections[0].frames.Count);
        }
    }

    #endregion

    #region Bug #122 — Identity crew status transitions filtered

    [Collection("Sequential")]
    public class Bug122_CrewIdentityTransitionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug122_CrewIdentityTransitionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Dead->Dead is an identity transition and should be filtered.
        /// </summary>
        [Fact]
        public void IsRealStatusChange_DeadToDead_ReturnsFalse()
        {
            bool result = GameStateRecorder.IsRealStatusChange(
                ProtoCrewMember.RosterStatus.Dead,
                ProtoCrewMember.RosterStatus.Dead);

            Assert.False(result);
        }

        /// <summary>
        /// Available->Available is an identity transition and should be filtered.
        /// </summary>
        [Fact]
        public void IsRealStatusChange_AvailableToAvailable_ReturnsFalse()
        {
            bool result = GameStateRecorder.IsRealStatusChange(
                ProtoCrewMember.RosterStatus.Available,
                ProtoCrewMember.RosterStatus.Available);

            Assert.False(result);
        }

        /// <summary>
        /// Assigned->Assigned is an identity transition and should be filtered.
        /// </summary>
        [Fact]
        public void IsRealStatusChange_AssignedToAssigned_ReturnsFalse()
        {
            bool result = GameStateRecorder.IsRealStatusChange(
                ProtoCrewMember.RosterStatus.Assigned,
                ProtoCrewMember.RosterStatus.Assigned);

            Assert.False(result);
        }

        /// <summary>
        /// Available->Assigned is a real transition.
        /// </summary>
        [Fact]
        public void IsRealStatusChange_AvailableToAssigned_ReturnsTrue()
        {
            bool result = GameStateRecorder.IsRealStatusChange(
                ProtoCrewMember.RosterStatus.Available,
                ProtoCrewMember.RosterStatus.Assigned);

            Assert.True(result);
        }

        /// <summary>
        /// Assigned->Dead is a real transition.
        /// </summary>
        [Fact]
        public void IsRealStatusChange_AssignedToDead_ReturnsTrue()
        {
            bool result = GameStateRecorder.IsRealStatusChange(
                ProtoCrewMember.RosterStatus.Assigned,
                ProtoCrewMember.RosterStatus.Dead);

            Assert.True(result);
        }

        /// <summary>
        /// Missing->Available is a real transition.
        /// </summary>
        [Fact]
        public void IsRealStatusChange_MissingToAvailable_ReturnsTrue()
        {
            bool result = GameStateRecorder.IsRealStatusChange(
                ProtoCrewMember.RosterStatus.Missing,
                ProtoCrewMember.RosterStatus.Available);

            Assert.True(result);
        }
    }

    #endregion

    #region Degraded tree detection

    public class DegradedTreeTests
    {
        [Fact]
        public void IsDegraded_AllRecordingsZeroPoints_ReturnsTrue()
        {
            var tree = new RecordingTree { TreeName = "EmptyTree" };
            tree.Recordings["r1"] = new Recording { Points = new List<TrajectoryPoint>() };
            tree.Recordings["r2"] = new Recording { Points = new List<TrajectoryPoint>() };

            Assert.True(tree.IsDegraded);
            Assert.Equal(0, tree.ComputeEndUT());
        }

        [Fact]
        public void IsDegraded_SomeRecordingsHavePoints_ReturnsFalse()
        {
            var tree = new RecordingTree { TreeName = "MixedTree" };
            tree.Recordings["r1"] = new Recording { Points = new List<TrajectoryPoint>() };
            tree.Recordings["r2"] = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100 },
                    new TrajectoryPoint { ut = 200 }
                }
            };

            Assert.False(tree.IsDegraded);
            Assert.Equal(200.0, tree.ComputeEndUT());
        }

        [Fact]
        public void IsDegraded_NoRecordings_ReturnsTrue()
        {
            var tree = new RecordingTree { TreeName = "NoRecs" };

            Assert.True(tree.IsDegraded);
        }

        [Fact]
        public void ComputeEndUT_MultipleRecordings_ReturnsMax()
        {
            var tree = new RecordingTree { TreeName = "Multi" };
            tree.Recordings["r1"] = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100 },
                    new TrajectoryPoint { ut = 150 }
                }
            };
            tree.Recordings["r2"] = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 200 },
                    new TrajectoryPoint { ut = 300 }
                }
            };

            Assert.Equal(300.0, tree.ComputeEndUT());
        }
    }

    #endregion

    #region Pending stash lifecycle (revert dialog fix)

    [Collection("Sequential")]
    public class PendingStashLifecycleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PendingStashLifecycleTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        private List<TrajectoryPoint> MakePoints(int count)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
                points.Add(new TrajectoryPoint
                {
                    ut = 100 + i * 10,
                    latitude = 0, longitude = 0, altitude = 100,
                    rotation = UnityEngine.Quaternion.identity,
                    velocity = UnityEngine.Vector3.zero,
                    bodyName = "Kerbin"
                });
            return points;
        }

        [Fact]
        public void FreshTreeStash_SurvivesRevertGuard()
        {
            var tree = new RecordingTree { TreeName = "FreshTree" };
            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.PendingStashedThisTransition);

            // Guard keeps it
            if (RecordingStore.PendingStashedThisTransition)
                RecordingStore.PendingStashedThisTransition = false;

            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal("FreshTree", RecordingStore.PendingTree.TreeName);
        }

        public void Dispose()
        {
            ParsekLog.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }
    }

    #endregion

    #region Watch mode — camera cutoff decisions

    public class WatchCutoffTests
    {
        [Theory]
        [InlineData(299000, 300, false)]  // just inside 300km cutoff
        [InlineData(300000, 300, true)]   // at boundary → exit
        [InlineData(301000, 300, true)]   // just outside
        [InlineData(0, 300, false)]       // on the pad
        [InlineData(1500000, 300, true)]  // way beyond
        [InlineData(99000, 100, false)]   // custom 100km cutoff, inside
        [InlineData(100000, 100, true)]   // custom 100km cutoff, at boundary
        [InlineData(4999999, 5000, false)] // 5000km cutoff, just inside
        [InlineData(5000000, 5000, true)]  // exactly at boundary
        public void ShouldExitWatchForCutoff(double distMeters, float cutoffKm, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldExitWatchForCutoff(distMeters, cutoffKm));
        }

        [Fact]
        public void IsWithinWatchRange_WithinCutoff_True()
        {
            Assert.True(GhostPlaybackLogic.IsWithinWatchRange(1000, 300));
        }

        [Fact]
        public void IsWithinWatchRange_WellWithinCutoff_True()
        {
            Assert.True(GhostPlaybackLogic.IsWithinWatchRange(200000, 300));
        }

        [Fact]
        public void IsWithinWatchRange_BeyondCutoff_False()
        {
            Assert.False(GhostPlaybackLogic.IsWithinWatchRange(350000, 300));
        }

        [Fact]
        public void IsWithinWatchRange_AtExactCutoff_False()
        {
            // Strict less-than: exactly at cutoff returns false
            Assert.False(GhostPlaybackLogic.IsWithinWatchRange(300000, 300));
        }

        private static WatchModeController MakeWatchModeController(double lastDistanceMeters)
        {
            var host = (ParsekFlight)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(ParsekFlight));
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                lastDistance = lastDistanceMeters
            };

            var engineField = typeof(ParsekFlight).GetField("engine",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            engineField.SetValue(host, engine);

            return new WatchModeController(host);
        }

        [Fact]
        public void IsGhostWithinVisualRange_OrbitalGhostBeyondCutoff_False()
        {
            RecordingStore.ResetForTesting();
            try
            {
                RecordingStore.AddCommittedInternal(new Recording
                {
                    VesselName = "Orbiter",
                    OrbitSegments = new List<OrbitSegment> { new OrbitSegment() }
                });

                var controller = MakeWatchModeController(500000);

                Assert.False(controller.IsGhostWithinVisualRange(0));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void IsGhostWithinVisualRange_OrbitalGhostAtExactCutoff_False()
        {
            RecordingStore.ResetForTesting();
            try
            {
                RecordingStore.AddCommittedInternal(new Recording
                {
                    VesselName = "BoundaryOrbiter",
                    OrbitSegments = new List<OrbitSegment> { new OrbitSegment() }
                });

                var controller = MakeWatchModeController(300000);

                Assert.False(controller.IsGhostWithinVisualRange(0));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void IsGhostWithinVisualRange_OrbitalGhostWithinCutoff_True()
        {
            RecordingStore.ResetForTesting();
            try
            {
                RecordingStore.AddCommittedInternal(new Recording
                {
                    VesselName = "CloseOrbiter",
                    OrbitSegments = new List<OrbitSegment> { new OrbitSegment() }
                });

                var controller = MakeWatchModeController(100000);

                Assert.True(controller.IsGhostWithinVisualRange(0));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Theory]
        [InlineData(500000, 300, true)]    // orbital ghost 500km away — exit
        [InlineData(100000, 300, false)]   // orbital ghost within cutoff — no exit
        [InlineData(300000, 300, true)]    // orbital at exact boundary — exit
        public void ShouldExitWatchForCutoff_AppliesUniformly(double distMeters, float cutoffKm, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldExitWatchForCutoff(distMeters, cutoffKm));
        }
    }

    #endregion

    #region Watch mode — FindNextWatchTarget

    [Collection("Sequential")]
    public class FindNextWatchTargetTests : IDisposable
    {
        public FindNextWatchTargetTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
        }

        private Recording MakeRec(string id, string vesselName = "Ship", uint vesselPid = 100,
            string chainId = null, int chainIndex = -1, int chainBranch = 0,
            string treeId = null, string childBpId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                VesselPersistentId = vesselPid,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
                TreeId = treeId,
                ChildBranchPointId = childBpId,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100 },
                    new TrajectoryPoint { ut = 200 }
                }
            };
        }

        // --- Chain continuation ---

        [Fact]
        public void ChainContinuation_NextIndexExists_ReturnsIt()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };
            // Ghost at index 1 is active
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree>(), idx => idx == 1);
            Assert.Equal(1, result);
        }

        [Fact]
        public void ChainContinuation_NextIndexNotActive_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };
            // No active ghosts
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree>(), idx => false);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void ChainContinuation_SkipsBranchRecordings()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1, chainBranch: 1), // branch, not main
                MakeRec("r2", chainId: "c1", chainIndex: 1, chainBranch: 0), // main continuation
            };
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree>(), idx => true);
            Assert.Equal(2, result); // skips branch at index 1
        }

        [Fact]
        public void ChainContinuation_DifferentChainId_Ignored()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c2", chainIndex: 1), // different chain
            };
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree>(), idx => true);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_SourceGhostStillActive_ReturnsSource()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(
                0, recs, new List<RecordingTree>(), idx => idx == 0 || idx == 1);

            Assert.Equal(0, result);
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_ChainSingleHop_ReturnsActiveContinuation()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
            };

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(
                0, recs, new List<RecordingTree>(), idx => idx == 1);

            Assert.Equal(1, result);
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_ChainMultiHopThroughInactiveIntermediate_ReturnsDeepActiveSegment()
        {
            var recs = new List<Recording>
            {
                MakeRec("r0", chainId: "c1", chainIndex: 0),
                MakeRec("r1", chainId: "c1", chainIndex: 1),
                MakeRec("r2", chainId: "c1", chainIndex: 2),
            };

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(
                0, recs, new List<RecordingTree>(), idx => idx == 2);

            Assert.Equal(2, result);
        }

        // --- Tree branching ---

        [Fact]
        public void TreeBranch_SameVesselPid_Preferred()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-debris", "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-debris", vesselName: "Debris", vesselPid: 200, treeId: "t1"),
                MakeRec("child-main", vesselName: "Ship", vesselPid: 100, treeId: "t1"),
            };

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => true);
            Assert.Equal(2, result); // same vessel PID preferred over debris
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_TreeMultiHopThroughInactiveIntermediates_ReturnsDeepActiveDescendant()
        {
            var bp1 = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-1", "other-vessel" }
            };
            var bp2 = new BranchPoint
            {
                Id = "bp2",
                ChildRecordingIds = new List<string> { "child-2" }
            };
            var bp3 = new BranchPoint
            {
                Id = "bp3",
                ChildRecordingIds = new List<string> { "child-3" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp1, bp2, bp3 }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-1", vesselPid: 100, treeId: "t1", childBpId: "bp2"),
                MakeRec("other-vessel", vesselPid: 200, treeId: "t1"),
                MakeRec("child-2", vesselPid: 100, treeId: "t1", childBpId: "bp3"),
                MakeRec("child-3", vesselPid: 100, treeId: "t1"),
            };

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(
                0, recs, new List<RecordingTree> { tree }, idx => idx == 4);

            Assert.Equal(4, result);
        }

        [Fact]
        public void TreeBranch_DifferentPid_PrefersFirstActiveNonDebris()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                Type = BranchPointType.Undock,
                ChildRecordingIds = new List<string> { "child-debris", "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-debris", vesselName: "Debris", vesselPid: 200, treeId: "t1"),
                MakeRec("child-main", vesselName: "Ship", vesselPid: 300, treeId: "t1"),
            };
            recs[1].IsDebris = true;

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => true);
            Assert.Equal(2, result); // debris skipped, first active non-debris child wins
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_NonBreakupInactiveFallbackChild_DoesNotDescendIntoActiveGrandchild()
        {
            var bp1 = new BranchPoint
            {
                Id = "bp1",
                Type = BranchPointType.Undock,
                ChildRecordingIds = new List<string> { "fallback-main" }
            };
            var bp2 = new BranchPoint
            {
                Id = "bp2",
                ChildRecordingIds = new List<string> { "fallback-grandchild" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp1, bp2 }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("fallback-main", vesselPid: 300, treeId: "t1", childBpId: "bp2"),
                MakeRec("fallback-grandchild", vesselPid: 300, treeId: "t1"),
            };

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(
                0, recs, new List<RecordingTree> { tree }, idx => idx == 2);

            Assert.Equal(-1, result);
        }

        [Fact]
        public void TreeBranch_BreakupDifferentPid_DoesNotFallbackToActiveChild()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                Type = BranchPointType.Breakup,
                ChildRecordingIds = new List<string> { "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselName: "Ship", vesselPid: 300, treeId: "t1"),
            };

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => true);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void ResolveEffectiveWatchTargetIndex_BreakupDifferentPid_DoesNotFallbackToActiveChild()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                Type = BranchPointType.Breakup,
                ChildRecordingIds = new List<string> { "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselName: "Ship", vesselPid: 300, treeId: "t1"),
            };

            int result = GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex(
                0, recs, new List<RecordingTree> { tree }, idx => idx == 1);

            Assert.Equal(-1, result);
        }

        [Fact]
        public void TreeBranch_DebrisOnlyChildren_ReturnsMinusOne()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                Type = BranchPointType.Breakup,
                ChildRecordingIds = new List<string> { "child-debris-a", "child-debris-b" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-debris-a", vesselName: "Debris A", vesselPid: 200, treeId: "t1"),
                MakeRec("child-debris-b", vesselName: "Debris B", vesselPid: 300, treeId: "t1"),
            };
            recs[1].IsDebris = true;
            recs[2].IsDebris = true;

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => true);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void TreeBranch_NoActiveChildren_ReturnsMinusOne()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-a" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-a", vesselPid: 200, treeId: "t1"),
            };

            // No active ghosts — simulates the race condition where continuation hasn't spawned
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => false);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void TreeBranch_RaceCondition_RetryFindsGhost()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselPid: 100, treeId: "t1"),
            };

            // First call: ghost not spawned yet
            int first = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => false);
            Assert.Equal(-1, first);

            // Retry: ghost now active (simulates spawn 1s later)
            int retry = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => idx == 1);
            Assert.Equal(1, retry);
        }

        // --- No chain, no tree ---

        [Fact]
        public void NoChainNoTree_ReturnsMinusOne()
        {
            var recs = new List<Recording>
            {
                MakeRec("standalone"),
            };
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree>(), idx => true);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void NullRecording_ReturnsMinusOne()
        {
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                null, new List<Recording>(), new List<RecordingTree>(), idx => true);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void NullCommitted_ReturnsMinusOne()
        {
            var rec = MakeRec("r0", chainId: "c1", chainIndex: 0);
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                rec, null, new List<RecordingTree>(), idx => true);
            Assert.Equal(-1, result);
        }

        // --- #158: PID-matched continuation without ghost ---

        [Fact]
        public void TreeBranch_PidMatchNoGhost_SuppressesDebrisFallback()
        {
            // Continuation (PID=100) has no ghost, debris (PID=200) does.
            // Should return -1, NOT the debris index.
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-main", "child-debris" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselPid: 100, treeId: "t1"),     // no ghost
                MakeRec("child-debris", vesselPid: 200, treeId: "t1"),   // has ghost
            };

            // Only debris has a ghost
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => idx == 2);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void TreeBranch_PidMatchNoGhost_RecursiveDescentFindsDeeper()
        {
            // Continuation (PID=100) has no ghost but has a child BP leading to
            // a deeper recording (PID=100) that DOES have a ghost.
            var bp1 = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-main", "child-debris" }
            };
            var bp2 = new BranchPoint
            {
                Id = "bp2",
                ChildRecordingIds = new List<string> { "grandchild-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp1, bp2 }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselPid: 100, treeId: "t1", childBpId: "bp2"),     // no ghost, but has child BP
                MakeRec("child-debris", vesselPid: 200, treeId: "t1"),                      // has ghost
                MakeRec("grandchild-main", vesselPid: 100, treeId: "t1"),                   // has ghost
            };

            // Debris (idx=2) and grandchild (idx=3) have ghosts, continuation (idx=1) does not
            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree },
                idx => idx == 2 || idx == 3);
            Assert.Equal(3, result); // recursive descent found grandchild
        }

        [Fact]
        public void TreeBranch_PidMatchNoGhost_RecursiveDeadEnd_ReturnsMinusOne()
        {
            // Continuation (PID=100) has no ghost AND no children.
            // Debris (PID=200) has ghost. Should return -1 (don't follow debris).
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-main", "child-debris" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselPid: 100, treeId: "t1"),     // no ghost, no children
                MakeRec("child-debris", vesselPid: 200, treeId: "t1"),   // has ghost
            };

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree },
                idx => idx == 2);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void TreeBranch_PidMatchNoGhost_PendingActivationUsesActualPayloadStartUT()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-main", "child-debris" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselPid: 100, treeId: "t1"),
                MakeRec("child-debris", vesselPid: 200, treeId: "t1"),
            };

            recs[1].ExplicitStartUT = 110.0;
            recs[1].Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 130.0 },
                new TrajectoryPoint { ut = 140.0 }
            };

            bool found = GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                recs[0], recs, new List<RecordingTree> { tree }, idx => false, out double activationUT);

            Assert.True(found);
            Assert.Equal(130.0, activationUT);
        }

        [Fact]
        public void TreeBranch_PidMatchGhostAlreadyActive_NoPendingActivationReturned()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-main", vesselPid: 100, treeId: "t1"),
            };

            bool found = GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                recs[0], recs, new List<RecordingTree> { tree }, idx => idx == 1, out _);

            Assert.False(found);
        }

        [Fact]
        public void TreeBranch_DifferentPidFallbackNoGhost_PendingActivationReturnedOnUndock()
        {
            var bp = new BranchPoint
            {
                Id = "bp1",
                Type = BranchPointType.Undock,
                ChildRecordingIds = new List<string> { "child-debris", "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-debris", vesselPid: 200, treeId: "t1"),
                MakeRec("child-main", vesselPid: 300, treeId: "t1"),
            };
            recs[1].IsDebris = true;
            recs[2].ExplicitStartUT = 115.0;
            recs[2].Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 132.0 },
                new TrajectoryPoint { ut = 140.0 }
            };

            bool found = GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                recs[0], recs, new List<RecordingTree> { tree }, idx => false, out double activationUT);

            Assert.True(found);
            Assert.Equal(132.0, activationUT);
        }

        [Fact]
        public void TreeBranch_PidMatchWithGhost_StillPreferred()
        {
            // Both continuation and debris have ghosts — PID match wins as before.
            var bp = new BranchPoint
            {
                Id = "bp1",
                ChildRecordingIds = new List<string> { "child-debris", "child-main" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", childBpId: "bp1"),
                MakeRec("child-debris", vesselPid: 200, treeId: "t1"),
                MakeRec("child-main", vesselPid: 100, treeId: "t1"),
            };

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[0], recs, new List<RecordingTree> { tree }, idx => true);
            Assert.Equal(2, result);
        }

        [Fact]
        public void Issue316_ArchivedSameTreeDebrisOutsideWatchedBranch_IsNotAutoFollowTarget()
        {
            var watchBranch = new BranchPoint
            {
                Id = "bp-watch-end",
                ChildRecordingIds = new List<string> { "future-main-a", "future-main-b" }
            };
            var earlierBreakup = new BranchPoint
            {
                Id = "bp-earlier-breakup",
                ParentRecordingIds = new List<string> { "root" },
                ChildRecordingIds = new List<string> { "active-debris" }
            };
            var tree = new RecordingTree
            {
                Id = "t1",
                TreeName = "Issue316",
                BranchPoints = new List<BranchPoint> { watchBranch, earlierBreakup }
            };

            var recs = new List<Recording>
            {
                MakeRec("root", vesselPid: 100, treeId: "t1", chainId: "c1", chainIndex: 0),
                MakeRec("mid", vesselPid: 100, treeId: "t1", chainId: "c1", chainIndex: 1),
                MakeRec("watched", vesselPid: 100, treeId: "t1", chainId: "c1", chainIndex: 2, childBpId: "bp-watch-end"),
                MakeRec("future-main-a", vesselPid: 100, treeId: "t1"),
                MakeRec("future-main-b", vesselPid: 100, treeId: "t1"),
                MakeRec("active-debris", vesselPid: 200, treeId: "t1"),
            };
            recs[5].IsDebris = true;
            recs[5].ParentBranchPointId = "bp-earlier-breakup";

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                recs[2], recs, new List<RecordingTree> { tree }, idx => idx == 5);

            Assert.Equal(-1, result);
        }
    }

    #endregion

    #region Debris auto-grouping and orphan adoption

    [Collection("Sequential")]
    public class DebrisGroupingTests : IDisposable
    {
        public DebrisGroupingTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            GroupHierarchyStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
        }

        private Recording MakeRec(string id, string name, bool isDebris = false, uint pid = 100,
            string treeId = null, double startUT = 100, double endUT = 200)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                VesselPersistentId = pid,
                IsDebris = isDebris,
                TreeId = treeId,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT },
                    new TrajectoryPoint { ut = endUT }
                }
            };
        }

        [Fact]
        public void CommitTree_DebrisGroupedUnderSubgroup()
        {
            var tree = new RecordingTree
            {
                Id = "t1", TreeName = "Rocket",
                Recordings = new Dictionary<string, Recording>
                {
                    { "stage", MakeRec("stage", "Rocket", treeId: "t1") },
                    { "debris", MakeRec("debris", "Rocket Debris", isDebris: true, treeId: "t1") }
                }
            };

            RecordingStore.CommitTree(tree);

            var committed = RecordingStore.CommittedRecordings;
            var stage = committed.FirstOrDefault(r => r.RecordingId == "stage");
            var debris = committed.FirstOrDefault(r => r.RecordingId == "debris");

            // Stage should be in the main group
            Assert.NotNull(stage.RecordingGroups);
            Assert.Single(stage.RecordingGroups);
            Assert.StartsWith("Rocket", stage.RecordingGroups[0]);
            Assert.DoesNotContain("Debris", stage.RecordingGroups[0]);

            // Debris should be in the debris subgroup
            Assert.NotNull(debris.RecordingGroups);
            Assert.Single(debris.RecordingGroups);
            Assert.Contains("Debris", debris.RecordingGroups[0]);

            // Debris subgroup should have parent relationship
            string debrisGroup = debris.RecordingGroups[0];
            string stageGroup = stage.RecordingGroups[0];
            Assert.True(GroupHierarchyStore.TryGetGroupParent(debrisGroup, out string parent));
            Assert.Equal(stageGroup, parent);
        }

        [Fact]
        public void CommitTree_NoDebris_AllInMainGroup()
        {
            var tree = new RecordingTree
            {
                Id = "t2", TreeName = "Ship",
                Recordings = new Dictionary<string, Recording>
                {
                    { "s1", MakeRec("s1", "Ship", treeId: "t2") },
                    { "s2", MakeRec("s2", "Ship", treeId: "t2") }
                }
            };

            RecordingStore.CommitTree(tree);

            var committed = RecordingStore.CommittedRecordings;
            foreach (var rec in committed)
            {
                Assert.NotNull(rec.RecordingGroups);
                Assert.DoesNotContain("Debris", rec.RecordingGroups[0]);
            }
        }

        [Fact]
        public void CommitTree_EvaCrewGroupedUnderCrewSubgroup()
        {
            var vessel = MakeRec("vessel", "Kerbal X", treeId: "t-crew", pid: 100);
            var eva = MakeRec("eva", "Jebediah Kerman", treeId: "t-crew", pid: 200);
            eva.ParentRecordingId = vessel.RecordingId;
            eva.EvaCrewName = "Jebediah Kerman";

            var tree = new RecordingTree
            {
                Id = "t-crew", TreeName = "Kerbal X",
                Recordings = new Dictionary<string, Recording>
                {
                    { vessel.RecordingId, vessel },
                    { eva.RecordingId, eva }
                }
            };

            RecordingStore.CommitTree(tree);

            Assert.NotNull(vessel.RecordingGroups);
            Assert.Single(vessel.RecordingGroups);
            Assert.Equal("Kerbal X", vessel.RecordingGroups[0]);

            Assert.NotNull(eva.RecordingGroups);
            Assert.Single(eva.RecordingGroups);
            Assert.Equal("Kerbal X / Crew", eva.RecordingGroups[0]);
            Assert.True(GroupHierarchyStore.TryGetGroupParent("Kerbal X / Crew", out string parent));
            Assert.Equal("Kerbal X", parent);
        }

        [Fact]
        public void CommitTree_AdoptsOrphanedRecordingByTreeId()
        {
            // Pre-commit an orphaned recording with matching TreeId
            var orphan = MakeRec("orphan", "Rocket", treeId: "t3", pid: 100, startUT: 100, endUT: 150);
            RecordingStore.AddRecordingWithTreeForTesting(orphan);
            Assert.Null(orphan.RecordingGroups);

            var tree = new RecordingTree
            {
                Id = "t3", TreeName = "Rocket",
                Recordings = new Dictionary<string, Recording>
                {
                    // Need >1 recording for auto-grouping to kick in
                    { "root", MakeRec("root", "Rocket", treeId: "t3", startUT: 100, endUT: 200) },
                    { "cont", MakeRec("cont", "Rocket", treeId: "t3", startUT: 200, endUT: 300) }
                }
            };

            RecordingStore.CommitTree(tree);

            // Orphan should now have a group
            Assert.NotNull(orphan.RecordingGroups);
            Assert.Single(orphan.RecordingGroups);
            Assert.StartsWith("Rocket", orphan.RecordingGroups[0]);
        }

        [Fact]
        public void CommitTree_AdoptsOrphanedDebrisWithParentRelation()
        {
            var orphanDebris = MakeRec("orphan-d", "Rocket Debris", isDebris: true, pid: 200, startUT: 100, endUT: 120);
            orphanDebris.TreeId = "t4";
            RecordingStore.AddRecordingWithTreeForTesting(orphanDebris);

            var tree = new RecordingTree
            {
                Id = "t4", TreeName = "Rocket",
                Recordings = new Dictionary<string, Recording>
                {
                    { "root", MakeRec("root", "Rocket", treeId: "t4", startUT: 100, endUT: 200) },
                    { "cont", MakeRec("cont", "Rocket", treeId: "t4", startUT: 200, endUT: 300) }
                }
            };

            RecordingStore.CommitTree(tree);

            // Adopted debris should be in debris subgroup with parent set
            Assert.NotNull(orphanDebris.RecordingGroups);
            Assert.Contains("Debris", orphanDebris.RecordingGroups[0]);
            Assert.True(GroupHierarchyStore.TryGetGroupParent(orphanDebris.RecordingGroups[0], out string parent));
        }

        [Fact]
        public void CommitTree_AdoptsOrphanedEvaIntoCrewSubgroup()
        {
            var orphanEva = MakeRec("orphan-eva", "Bob Kerman", pid: 300, startUT: 120, endUT: 180);
            orphanEva.TreeId = "t-crew-adopt";
            orphanEva.ParentRecordingId = "root";
            orphanEva.EvaCrewName = "Bob Kerman";
            RecordingStore.AddRecordingWithTreeForTesting(orphanEva);

            var tree = new RecordingTree
            {
                Id = "t-crew-adopt", TreeName = "Kerbal X",
                Recordings = new Dictionary<string, Recording>
                {
                    { "root", MakeRec("root", "Kerbal X", treeId: "t-crew-adopt", pid: 100, startUT: 100, endUT: 200) },
                    { "cont", MakeRec("cont", "Kerbal X", treeId: "t-crew-adopt", pid: 100, startUT: 200, endUT: 300) }
                }
            };

            RecordingStore.CommitTree(tree);

            Assert.NotNull(orphanEva.RecordingGroups);
            Assert.Single(orphanEva.RecordingGroups);
            Assert.Equal("Kerbal X / Crew", orphanEva.RecordingGroups[0]);
            Assert.True(GroupHierarchyStore.TryGetGroupParent("Kerbal X / Crew", out string parent));
            Assert.Equal("Kerbal X", parent);
        }

        [Fact]
        public void CommitTree_ReplacesStandaloneAutoGroupWhenAdoptingOrphanEva()
        {
            var orphanEva = MakeRec("orphan-eva-grouped", "Bob Kerman", pid: 300, startUT: 120, endUT: 180);
            orphanEva.TreeId = "t-crew-rehome";
            orphanEva.ParentRecordingId = "root";
            orphanEva.EvaCrewName = "Bob Kerman";
            orphanEva.RecordingGroups = new List<string> { "Bob Kerman" };
            RecordingStore.AddRecordingWithTreeForTesting(orphanEva);
            RecordingStore.MarkAutoAssignedStandaloneGroupForTesting(orphanEva, "Bob Kerman");

            var tree = new RecordingTree
            {
                Id = "t-crew-rehome", TreeName = "Kerbal X",
                Recordings = new Dictionary<string, Recording>
                {
                    { "root", MakeRec("root", "Kerbal X", treeId: "t-crew-rehome", pid: 100, startUT: 100, endUT: 200) },
                    { "cont", MakeRec("cont", "Kerbal X", treeId: "t-crew-rehome", pid: 100, startUT: 200, endUT: 300) }
                }
            };

            RecordingStore.CommitTree(tree);

            Assert.NotNull(orphanEva.RecordingGroups);
            Assert.Single(orphanEva.RecordingGroups);
            Assert.Equal("Kerbal X / Crew", orphanEva.RecordingGroups[0]);
        }

        [Fact]
        public void CommitTree_DoesNotAdoptSameNamedManualRootGroupWithoutAutoGroupMarker()
        {
            var orphanEva = MakeRec("orphan-eva-manual", "Bob Kerman", pid: 300, startUT: 120, endUT: 180);
            orphanEva.TreeId = "t-crew-manual";
            orphanEva.ParentRecordingId = "root";
            orphanEva.EvaCrewName = "Bob Kerman";
            orphanEva.RecordingGroups = new List<string> { "Bob Kerman" };
            RecordingStore.AddRecordingWithTreeForTesting(orphanEva);

            var tree = new RecordingTree
            {
                Id = "t-crew-manual", TreeName = "Kerbal X",
                Recordings = new Dictionary<string, Recording>
                {
                    { "root", MakeRec("root", "Kerbal X", treeId: "t-crew-manual", pid: 100, startUT: 100, endUT: 200) },
                    { "cont", MakeRec("cont", "Kerbal X", treeId: "t-crew-manual", pid: 100, startUT: 200, endUT: 300) }
                }
            };

            RecordingStore.CommitTree(tree);

            Assert.NotNull(orphanEva.RecordingGroups);
            Assert.Single(orphanEva.RecordingGroups);
            Assert.Equal("Bob Kerman", orphanEva.RecordingGroups[0]);
        }

        [Fact]
        public void AutoAssignedStandaloneGroupMarker_RoundTripsThroughRecordingNode()
        {
            var rec = MakeRec("roundtrip", "Bob Kerman", pid: 300, startUT: 120, endUT: 180);
            rec.RecordingGroups = new List<string> { "Bob Kerman" };
            RecordingStore.MarkAutoAssignedStandaloneGroupForTesting(rec, "Bob Kerman");

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording();
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.Equal("Bob Kerman", loaded.AutoAssignedStandaloneGroupName);
        }

        [Fact]
        public void ManualGroupMutations_ClearAutoAssignedStandaloneGroupMarker()
        {
            var renameRec = MakeRec("rename", "Bob Kerman", pid: 300, startUT: 120, endUT: 180);
            renameRec.RecordingGroups = new List<string> { "Bob Kerman" };
            RecordingStore.AddRecordingWithTreeForTesting(renameRec);
            RecordingStore.MarkAutoAssignedStandaloneGroupForTesting(renameRec, "Bob Kerman");

            Assert.True(RecordingStore.RenameGroup("Bob Kerman", "Crew A"));
            Assert.Null(renameRec.AutoAssignedStandaloneGroupName);

            var replaceRec = MakeRec("replace", "Bill Kerman", pid: 301, startUT: 120, endUT: 180);
            replaceRec.RecordingGroups = new List<string> { "Bill Kerman" };
            RecordingStore.AddRecordingWithTreeForTesting(replaceRec);
            RecordingStore.MarkAutoAssignedStandaloneGroupForTesting(replaceRec, "Bill Kerman");

            Assert.Equal(1, RecordingStore.ReplaceGroupOnAll("Bill Kerman", null));
            Assert.Null(replaceRec.AutoAssignedStandaloneGroupName);
        }

        [Fact]
        public void CommitTree_DoesNotAdoptGroupedRecording()
        {
            var alreadyGrouped = MakeRec("grouped", "Rocket", pid: 100, startUT: 100, endUT: 150);
            alreadyGrouped.RecordingGroups = new List<string> { "OtherGroup" };
            RecordingStore.AddRecordingWithTreeForTesting(alreadyGrouped);

            var tree = new RecordingTree
            {
                Id = "t5", TreeName = "Rocket",
                Recordings = new Dictionary<string, Recording>
                {
                    { "root", MakeRec("root", "Rocket", treeId: "t5", startUT: 100, endUT: 200) }
                }
            };

            RecordingStore.CommitTree(tree);

            // Should still be in the original group, not adopted
            Assert.Single(alreadyGrouped.RecordingGroups);
            Assert.Equal("OtherGroup", alreadyGrouped.RecordingGroups[0]);
        }
    }

    #endregion

    #region Bug #157 — CreateBreakupChildRecording with fallback snapshot

    [Collection("Sequential")]
    public class Bug157_FallbackSnapshotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug157_FallbackSnapshotTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void NullVessel_WithFallbackSnapshot_UsesIt()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            var bp = new BranchPoint { Id = "bp1", UT = 100 };

            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", "Debris Piece");

            var rec = ParsekFlight.CreateBreakupChildRecording(
                tree, bp, 42, null, true, "Debris", snapshot);

            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.Equal("Debris Piece", rec.GhostVisualSnapshot.GetValue("name"));
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(100, rec.ExplicitEndUT);

            Assert.Contains(logLines, l => l.Contains("pre-captured snapshot") && l.Contains("pid=42"));
        }

        [Fact]
        public void NullVessel_NoFallback_BothSnapshotsNull()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            var bp = new BranchPoint { Id = "bp1", UT = 100 };

            var rec = ParsekFlight.CreateBreakupChildRecording(
                tree, bp, 42, null, true, "Debris", null);

            Assert.Null(rec.GhostVisualSnapshot);
            Assert.Null(rec.VesselSnapshot);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
        }

        [Fact]
        public void FallbackSnapshot_CopiedToVesselSnapshot()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            var bp = new BranchPoint { Id = "bp1", UT = 100 };

            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("pid", "42");

            var rec = ParsekFlight.CreateBreakupChildRecording(
                tree, bp, 42, null, true, "Debris", snapshot);

            // VesselSnapshot should be a copy, not the same reference
            Assert.NotNull(rec.VesselSnapshot);
            Assert.NotSame(rec.GhostVisualSnapshot, rec.VesselSnapshot);
            Assert.Equal("42", rec.VesselSnapshot.GetValue("pid"));
        }

        [Fact]
        public void NullVessel_WithFallbackTrajectoryPoint_SeedsRecordingPoint()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            var bp = new BranchPoint { Id = "bp1", UT = 100 };
            var point = new TrajectoryPoint
            {
                ut = 100.25,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                bodyName = "Kerbin",
                rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            };

            var rec = ParsekFlight.CreateBreakupChildRecording(
                tree, bp, 42, null, true, "Debris", fallbackSnapshot: null,
                fallbackTrajectoryPoint: point);

            Assert.Single(rec.Points);
            Assert.Equal(100.25, rec.Points[0].ut);
            Assert.Equal(345.0, rec.Points[0].altitude);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(100.25, rec.ExplicitEndUT);
        }

        [Fact]
        public void SeedBreakupChildSnapshots_PrefersPreCapturedGhostButKeepsLiveVesselSnapshot()
        {
            var rec = new Recording();
            var liveSnapshot = new ConfigNode("VESSEL");
            liveSnapshot.AddValue("name", "Live Vessel");
            liveSnapshot.AddValue("liveOnly", "1");

            var preCapturedSnapshot = new ConfigNode("VESSEL");
            preCapturedSnapshot.AddValue("name", "PreCaptured Ghost");
            preCapturedSnapshot.AddValue("preOnly", "1");

            ParsekFlight.SeedBreakupChildSnapshots(rec, 42, liveSnapshot, preCapturedSnapshot);

            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.Equal("PreCaptured Ghost", rec.GhostVisualSnapshot.GetValue("name"));
            Assert.Equal("Live Vessel", rec.VesselSnapshot.GetValue("name"));
            Assert.Equal("1", rec.GhostVisualSnapshot.GetValue("preOnly"));
            Assert.Null(rec.GhostVisualSnapshot.GetValue("liveOnly"));
            Assert.Equal("1", rec.VesselSnapshot.GetValue("liveOnly"));
        }
    }

    #endregion

    #region Bug #219 — PopulateTerminalOrbitFromLastSegment

    [Collection("Sequential")]
    public class Bug219_PopulateTerminalOrbitFromLastSegmentTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug219_PopulateTerminalOrbitFromLastSegmentTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void WithOrbitSegments_PopulatesTerminalOrbit()
        {
            var rec = new Recording();
            rec.OrbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    bodyName = "Kerbin",
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 700000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 1.5,
                    epoch = 100
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(28.5, rec.TerminalOrbitInclination);
            Assert.Equal(0.001, rec.TerminalOrbitEccentricity);
            Assert.Equal(700000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(90, rec.TerminalOrbitLAN);
            Assert.Equal(45, rec.TerminalOrbitArgumentOfPeriapsis);
            Assert.Equal(1.5, rec.TerminalOrbitMeanAnomalyAtEpoch);
            Assert.Equal(100, rec.TerminalOrbitEpoch);

            Assert.Contains(logLines, l => l.Contains("PopulateTerminalOrbitFromLastSegment")
                && l.Contains("Kerbin") && l.Contains("700000"));
        }

        [Fact]
        public void UsesLastSegment_NotFirst()
        {
            var rec = new Recording();
            rec.OrbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 100, endUT = 200, bodyName = "Kerbin", semiMajorAxis = 700000 },
                new OrbitSegment { startUT = 200, endUT = 300, bodyName = "Mun", semiMajorAxis = 250000 }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(250000, rec.TerminalOrbitSemiMajorAxis);
        }

        [Fact]
        public void NoOrbitSegments_DoesNothing()
        {
            var rec = new Recording();
            rec.OrbitSegments = new List<OrbitSegment>();

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Null(rec.TerminalOrbitBody);
            Assert.Equal(0, rec.TerminalOrbitSemiMajorAxis);
        }

        [Fact]
        public void AlreadyPopulatedWithMatchingTuple_DoesNotOverwrite()
        {
            var rec = new Recording
            {
                RecordingId = "preserve-terminal-orbit",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 200, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(700000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Contains(logLines, l => l.Contains("ShouldPopulateTerminalOrbitFromLastSegment")
                && l.Contains("[INFO][Flight]")
                && l.Contains("preserved cached terminal orbit")
                && l.Contains("body=Kerbin")
                && l.Contains("sma=700000.0"));
        }

        [Fact]
        public void AlreadyPopulatedWithNearlyMatchingSerializedTuple_DoesNotOverwrite()
        {
            var rec = new Recording
            {
                RecordingId = "preserve-nearly-matching-terminal-orbit",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0000005,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 200, bodyName = "Kerbin", semiMajorAxis = 700000.0 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(700000.0000005, rec.TerminalOrbitSemiMajorAxis, 10);
            Assert.Contains(logLines, l => l.Contains("ShouldPopulateTerminalOrbitFromLastSegment")
                && l.Contains("preserved cached terminal orbit")
                && l.Contains("body=Kerbin")
                && l.Contains("sma=700000.0"));
        }

        [Fact]
        public void ExplicitEndpointBody_DisagreesWithLaterOrbitSegment_DoesNotOverwrite()
        {
            var rec = new Recording
            {
                RecordingId = "preserve-explicit-terminal-orbit",
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Mun",
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 250000,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 300, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 300, endUT = 400, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(250000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Contains(logLines, l => l.Contains("ShouldPopulateTerminalOrbitFromLastSegment")
                && l.Contains("[INFO][Flight]")
                && l.Contains("preserved cached terminal orbit")
                && l.Contains("preserve-explicit-terminal-orbit")
                && l.Contains("explicit endpoint body=Mun")
                && l.Contains("later segment body=Kerbin")
                && l.Contains("sma=700000.0"));
            Assert.DoesNotContain(logLines, l => l.Contains("PopulateTerminalOrbitFromLastSegment:")
                && l.Contains("preserve-explicit-terminal-orbit"));
        }

        [Fact]
        public void SameBodyWithStaleTuple_WithPersistedExplicitEndpointBody_DoesNotOverwrite()
        {
            var rec = new Recording
            {
                RecordingId = "preserve-persisted-explicit-same-body-terminal-orbit",
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Kerbin",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 500000,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 300, endUT = 400, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(500000, rec.TerminalOrbitSemiMajorAxis);
            Assert.DoesNotContain(logLines, l => l.Contains("PopulateTerminalOrbitFromLastSegment:")
                && l.Contains("preserve-persisted-explicit-same-body-terminal-orbit"));
        }

        [Fact]
        public void SameBodyWithStaleTuple_WithOrbitEndpointAlignedLastSegment_Overwrites()
        {
            var rec = new Recording
            {
                RecordingId = "heal-stale-same-body-terminal-orbit",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 500000,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 300, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 300, endUT = 400, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(700000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Contains(logLines, l => l.Contains("PopulateTerminalOrbitFromLastSegment")
                && l.Contains("[WARN][Flight]")
                && l.Contains("healed stale cached terminal orbit")
                && l.Contains("previousBody=Kerbin")
                && l.Contains("previousSma=500000.0")
                && l.Contains("newBody=Kerbin")
                && l.Contains("newSma=700000.0"));
        }

        [Fact]
        public void SameBodyWithStaleTuple_HealLog_UsesInvariantCultureUnderCommaLocale()
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                var rec = new Recording
                {
                    RecordingId = "heal-stale-same-body-terminal-orbit-comma-locale",
                    TerminalOrbitBody = "Kerbin",
                    TerminalOrbitSemiMajorAxis = 500000,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 300, bodyName = "Mun" }
                    },
                    OrbitSegments = new List<OrbitSegment>
                    {
                        new OrbitSegment { startUT = 300, endUT = 400, bodyName = "Kerbin", semiMajorAxis = 700000 }
                    }
                };

                ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

                string logLine = logLines.Find(l => l.Contains("PopulateTerminalOrbitFromLastSegment")
                    && l.Contains("healed stale cached terminal orbit")
                    && l.Contains("heal-stale-same-body-terminal-orbit-comma-locale"));
                Assert.NotNull(logLine);
                Assert.Contains("previousSma=500000.0", logLine);
                Assert.Contains("newSma=700000.0", logLine);
                Assert.DoesNotContain("previousSma=500000,0", logLine);
                Assert.DoesNotContain("newSma=700000,0", logLine);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previousCulture;
            }
        }

        [Fact]
        public void MismatchedBody_WithPointEndpointAlignedLastSegment_Overwrites()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 500000,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 300, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 200, bodyName = "Kerbin", semiMajorAxis = 700000 },
                    new OrbitSegment { startUT = 200, endUT = 300, bodyName = "Mun", semiMajorAxis = 250000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(250000, rec.TerminalOrbitSemiMajorAxis);
        }

        [Fact]
        public void MismatchedBody_OrbitOnlyStaleCache_WithEndpointAlignedLastSegment_Overwrites()
        {
            var rec = new Recording
            {
                RecordingId = "heal-orbit-only-stale-terminal-orbit",
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 250000,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 300, endUT = 400, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(700000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Contains(logLines, l => l.Contains("PopulateTerminalOrbitFromLastSegment")
                && l.Contains("healed stale cached terminal orbit")
                && l.Contains("previousBody=Mun")
                && l.Contains("newBody=Kerbin"));
        }
        [Fact]
        public void MismatchedBody_WithOrbitEndpointAlignedLastSegment_Overwrites()
        {
            var rec = new Recording
            {
                RecordingId = "heal-stale-terminal-orbit",
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 250000,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 300, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 300, endUT = 400, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(700000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Contains(logLines, l => l.Contains("PopulateTerminalOrbitFromLastSegment")
                && l.Contains("healed stale cached terminal orbit")
                && l.Contains("previousBody=Mun")
                && l.Contains("newBody=Kerbin"));
        }

        [Fact]
        public void MismatchedBody_WithoutEndpointAlignedLastSegment_DoesNotOverwrite()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 500000,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 300, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 300, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(500000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Contains(logLines, l =>
                l.Contains("TryGetTerminalOrbitAlignedOrbitDecision")
                && l.Contains("terminalBody=Kerbin")
                && l.Contains("pointBody=Mun"));
        }

        [Fact]
        public void EmptyBody_WithoutEndpointAlignedLastSegment_DoesNotPopulate()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 300, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 300, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Null(rec.TerminalOrbitBody);
            Assert.Equal(0, rec.TerminalOrbitSemiMajorAxis);
        }

        [Fact]
        public void NullOrbitSegmentsList_DoesNothing()
        {
            var rec = new Recording();
            rec.OrbitSegments = null;

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Null(rec.TerminalOrbitBody);
        }

        [Fact]
        public void PreferredEndpointBodyName_WithoutInferredBody_FallsBackToKerbin()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };

            Assert.Equal("Kerbin", RecordingEndpointResolver.GetPreferredEndpointBodyName(rec));
        }
    }

    #endregion
}
