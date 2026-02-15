using System.Collections.Generic;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ChainTests
    {
        public ChainTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0, longitude = 0, altitude = 100,
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        #region DecideOnVesselSwitch

        [Fact]
        public void DecideOnVesselSwitch_SameVessel_ReturnsNone()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 100, false, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.None, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_SameVessel_EvaFlags_StillNone()
        {
            // Same vessel PID overrides everything
            var result = FlightRecorder.DecideOnVesselSwitch(100, 100, true, true);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.None, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_EvaToEva_StartedAsEva_ReturnsContinueOnEva()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, true, true);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ContinueOnEva, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_EvaToVessel_StartedAsEva_ReturnsChainToVessel()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, true);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ChainToVessel, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_VesselToOther_ReturnsStop()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_VesselToEva_NotStartedAsEva_ReturnsStop()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, true, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, result);
        }

        #endregion

        #region ShouldRefreshSnapshot

        [Fact]
        public void ShouldRefreshSnapshot_Force_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldRefreshSnapshot(100, 101, 10, true));
        }

        [Fact]
        public void ShouldRefreshSnapshot_NeverRefreshed_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldRefreshSnapshot(double.MinValue, 100, 10, false));
        }

        [Fact]
        public void ShouldRefreshSnapshot_IntervalNotPassed_ReturnsFalse()
        {
            Assert.False(FlightRecorder.ShouldRefreshSnapshot(100, 105, 10, false));
        }

        [Fact]
        public void ShouldRefreshSnapshot_IntervalPassed_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldRefreshSnapshot(100, 111, 10, false));
        }

        [Fact]
        public void ShouldRefreshSnapshot_ExactInterval_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldRefreshSnapshot(100, 110, 10, false));
        }

        #endregion

        #region GetRecommendedAction

        [Fact]
        public void GetRecommendedAction_DestroyedNearPad_ReturnsRecover()
        {
            var result = RecordingStore.GetRecommendedAction(50, destroyed: true, hasSnapshot: false);
            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetRecommendedAction_DestroyedFarAway_ReturnsMergeOnly()
        {
            var result = RecordingStore.GetRecommendedAction(500, destroyed: true, hasSnapshot: false);
            Assert.Equal(RecordingStore.MergeDefault.MergeOnly, result);
        }

        [Fact]
        public void GetRecommendedAction_NoSnapshot_FarAway_ReturnsMergeOnly()
        {
            var result = RecordingStore.GetRecommendedAction(500, destroyed: false, hasSnapshot: false);
            Assert.Equal(RecordingStore.MergeDefault.MergeOnly, result);
        }

        [Fact]
        public void GetRecommendedAction_IntactNearPad_ShortDuration_ReturnsRecover()
        {
            var result = RecordingStore.GetRecommendedAction(50, destroyed: false, hasSnapshot: true,
                duration: 5, maxDistance: 50);
            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetRecommendedAction_IntactFarAway_ReturnsPersist()
        {
            var result = RecordingStore.GetRecommendedAction(500, destroyed: false, hasSnapshot: true,
                duration: 60, maxDistance: 500);
            Assert.Equal(RecordingStore.MergeDefault.Persist, result);
        }

        [Fact]
        public void GetRecommendedAction_IntactNearPad_LongDuration_HighMaxDist_ReturnsPersist()
        {
            // Near pad now but traveled far — still persist
            var result = RecordingStore.GetRecommendedAction(50, destroyed: false, hasSnapshot: true,
                duration: 60, maxDistance: 5000);
            Assert.Equal(RecordingStore.MergeDefault.Persist, result);
        }

        #endregion

        #region StashPending edge cases

        [Fact]
        public void StashPending_NullPoints_NoPending()
        {
            RecordingStore.StashPending(null, "Test");
            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void StashPending_OnePoint_NoPending()
        {
            RecordingStore.StashPending(MakePoints(1), "Test");
            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void StashPending_TwoPoints_CreatesPending()
        {
            RecordingStore.StashPending(MakePoints(2), "Test");
            Assert.True(RecordingStore.HasPending);
            Assert.Equal("Test", RecordingStore.Pending.VesselName);
            Assert.Equal(2, RecordingStore.Pending.Points.Count);
        }

        [Fact]
        public void StashPending_CustomId_UsesIt()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship", recordingId: "custom-id");
            Assert.Equal("custom-id", RecordingStore.Pending.RecordingId);
        }

        [Fact]
        public void StashPending_NullId_GeneratesId()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship", recordingId: null);
            Assert.False(string.IsNullOrEmpty(RecordingStore.Pending.RecordingId));
        }

        [Fact]
        public void StashPending_WithOrbitSegments_PreservesThem()
        {
            var segs = new List<OrbitSegment> { new OrbitSegment { startUT = 100, endUT = 200 } };
            RecordingStore.StashPending(MakePoints(3), "Ship", orbitSegments: segs);
            Assert.Single(RecordingStore.Pending.OrbitSegments);
        }

        [Fact]
        public void StashPending_WithPartEvents_PreservesThem()
        {
            var events = new List<PartEvent> { new PartEvent { ut = 100, eventType = PartEventType.Decoupled } };
            RecordingStore.StashPending(MakePoints(3), "Ship", partEvents: events);
            Assert.Single(RecordingStore.Pending.PartEvents);
        }

        #endregion

        #region CommitPending / DiscardPending / Clear

        [Fact]
        public void CommitPending_NoPending_DoesNothing()
        {
            RecordingStore.CommitPending();
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void CommitPending_MovesPendingToCommitted()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");
            RecordingStore.CommitPending();
            Assert.False(RecordingStore.HasPending);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Ship", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void DiscardPending_ClearsPending()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");
            Assert.True(RecordingStore.HasPending);
            RecordingStore.DiscardPending();
            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void DiscardPending_NoPending_DoesNothing()
        {
            RecordingStore.DiscardPending(); // Should not throw
            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void Clear_RemovesAll()
        {
            RecordingStore.StashPending(MakePoints(3), "Pending");
            RecordingStore.CommitPending();
            RecordingStore.StashPending(MakePoints(3), "Pending2");

            RecordingStore.Clear();

            Assert.False(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void ClearCommitted_OnlyClearsCommitted()
        {
            RecordingStore.StashPending(MakePoints(3), "A");
            RecordingStore.CommitPending();
            RecordingStore.StashPending(MakePoints(3), "Pending");

            RecordingStore.ClearCommitted();

            Assert.True(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        #endregion

        #region ValidateChains

        [Fact]
        public void ValidateChains_ValidChain_PreservesFields()
        {
            for (int i = 0; i < 3; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Seg{i}");
                RecordingStore.Pending.ChainId = "chain-valid";
                RecordingStore.Pending.ChainIndex = i;
                RecordingStore.CommitPending();
            }

            RecordingStore.ValidateChains();

            var committed = RecordingStore.CommittedRecordings;
            Assert.Equal(3, committed.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal("chain-valid", committed[i].ChainId);
                Assert.Equal(i, committed[i].ChainIndex);
            }
        }

        [Fact]
        public void ValidateChains_GapInIndices_DegradesToStandalone()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "chain-gap";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg2");
            RecordingStore.Pending.ChainId = "chain-gap";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
            }
        }

        [Fact]
        public void ValidateChains_DuplicateIndices_DegradesToStandalone()
        {
            for (int i = 0; i < 2; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Seg{i}");
                RecordingStore.Pending.ChainId = "chain-dup";
                RecordingStore.Pending.ChainIndex = 0; // Both index 0
                RecordingStore.CommitPending();
            }

            RecordingStore.ValidateChains();

            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
            }
        }

        [Fact]
        public void ValidateChains_NonMonotonicUT_DegradesToStandalone()
        {
            RecordingStore.StashPending(MakePoints(3, 200), "Seg0");
            RecordingStore.Pending.ChainId = "chain-ut";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 100), "Seg1");
            RecordingStore.Pending.ChainId = "chain-ut";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
            }
        }

        [Fact]
        public void ValidateChains_MixedValidAndInvalid_OnlyInvalidDegraded()
        {
            // Valid chain
            for (int i = 0; i < 2; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Good{i}");
                RecordingStore.Pending.ChainId = "chain-good";
                RecordingStore.Pending.ChainIndex = i;
                RecordingStore.CommitPending();
            }

            // Invalid chain (gap)
            RecordingStore.StashPending(MakePoints(3, 300), "Bad0");
            RecordingStore.Pending.ChainId = "chain-bad";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 400), "Bad2");
            RecordingStore.Pending.ChainId = "chain-bad";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            Assert.Equal("chain-good", RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal("chain-good", RecordingStore.CommittedRecordings[1].ChainId);
            Assert.Null(RecordingStore.CommittedRecordings[2].ChainId);
            Assert.Null(RecordingStore.CommittedRecordings[3].ChainId);
        }

        [Fact]
        public void ValidateChains_StandaloneRecordings_Unaffected()
        {
            RecordingStore.StashPending(MakePoints(3), "Standalone");
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            Assert.Null(RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal(-1, RecordingStore.CommittedRecordings[0].ChainIndex);
        }

        [Fact]
        public void ValidateChains_SingleSegmentChain_Valid()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Solo");
            RecordingStore.Pending.ChainId = "chain-solo";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            Assert.Equal("chain-solo", RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal(0, RecordingStore.CommittedRecordings[0].ChainIndex);
        }

        [Fact]
        public void ValidateChains_EqualStartUT_Valid()
        {
            // Boundary-anchored segments can have equal StartUT
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "chain-eq-ut";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 100), "Seg1");
            RecordingStore.Pending.ChainId = "chain-eq-ut";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            Assert.Equal("chain-eq-ut", RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal("chain-eq-ut", RecordingStore.CommittedRecordings[1].ChainId);
        }

        [Fact]
        public void ValidateChains_StartsAtIndex1_Degraded()
        {
            // Chain starts at index 1, missing index 0
            RecordingStore.StashPending(MakePoints(3, 100), "Seg1");
            RecordingStore.Pending.ChainId = "chain-no-zero";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg2");
            RecordingStore.Pending.ChainId = "chain-no-zero";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
            }
        }

        [Fact]
        public void ValidateChains_NoCommittedRecordings_NoError()
        {
            RecordingStore.ValidateChains(); // Should not throw
        }

        #endregion

        #region GetChainRecordings / RemoveChainRecordings

        [Fact]
        public void GetChainRecordings_NullId_ReturnsNull()
        {
            Assert.Null(RecordingStore.GetChainRecordings(null));
        }

        [Fact]
        public void GetChainRecordings_EmptyId_ReturnsNull()
        {
            Assert.Null(RecordingStore.GetChainRecordings(""));
        }

        [Fact]
        public void GetChainRecordings_NoMatches_ReturnsNull()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");
            RecordingStore.CommitPending();

            Assert.Null(RecordingStore.GetChainRecordings("nonexistent"));
        }

        [Fact]
        public void GetChainRecordings_ReturnsSortedByIndex()
        {
            // Add in reverse order
            RecordingStore.StashPending(MakePoints(3, 200), "Seg2");
            RecordingStore.Pending.ChainId = "chain-sort";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "chain-sort";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 150), "Seg1");
            RecordingStore.Pending.ChainId = "chain-sort";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            var chain = RecordingStore.GetChainRecordings("chain-sort");
            Assert.NotNull(chain);
            Assert.Equal(3, chain.Count);
            Assert.Equal(0, chain[0].ChainIndex);
            Assert.Equal(1, chain[1].ChainIndex);
            Assert.Equal(2, chain[2].ChainIndex);
        }

        [Fact]
        public void GetChainRecordings_SingleMatch_ReturnsList()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Solo");
            RecordingStore.Pending.ChainId = "chain-single";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            var chain = RecordingStore.GetChainRecordings("chain-single");
            Assert.NotNull(chain);
            Assert.Single(chain);
            Assert.Equal(0, chain[0].ChainIndex);
        }

        [Fact]
        public void GetChainRecordings_IgnoresOtherChains()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "A0");
            RecordingStore.Pending.ChainId = "chain-a";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "B0");
            RecordingStore.Pending.ChainId = "chain-b";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 300), "A1");
            RecordingStore.Pending.ChainId = "chain-a";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            var chain = RecordingStore.GetChainRecordings("chain-a");
            Assert.NotNull(chain);
            Assert.Equal(2, chain.Count);
            Assert.Equal("A0", chain[0].VesselName);
            Assert.Equal("A1", chain[1].VesselName);
        }

        [Fact]
        public void RemoveChainRecordings_RemovesMatchingOnly()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Chain1");
            RecordingStore.Pending.ChainId = "remove-me";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Standalone");
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 300), "Chain2");
            RecordingStore.Pending.ChainId = "remove-me";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            RecordingStore.RemoveChainRecordings("remove-me");

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Standalone", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void RemoveChainRecordings_NullId_DoesNothing()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");
            RecordingStore.CommitPending();

            RecordingStore.RemoveChainRecordings(null);

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveChainRecordings_EmptyId_DoesNothing()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");
            RecordingStore.CommitPending();

            RecordingStore.RemoveChainRecordings("");

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveChainRecordings_NoMatch_DoesNothing()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");
            RecordingStore.CommitPending();

            RecordingStore.RemoveChainRecordings("nonexistent");

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveChainRecordings_AllSegments_LeavesEmpty()
        {
            for (int i = 0; i < 3; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Seg{i}");
                RecordingStore.Pending.ChainId = "remove-all";
                RecordingStore.Pending.ChainIndex = i;
                RecordingStore.CommitPending();
            }

            RecordingStore.RemoveChainRecordings("remove-all");

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        #endregion

        #region IsChainMidSegment

        [Fact]
        public void IsChainMidSegment_MidSegment_ReturnsTrue()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "mid-test";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg1");
            RecordingStore.Pending.ChainId = "mid-test";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            Assert.True(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_LastSegment_ReturnsFalse()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "last-test";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg1");
            RecordingStore.Pending.ChainId = "last-test";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[1]));
        }

        [Fact]
        public void IsChainMidSegment_Standalone_ReturnsFalse()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Solo");
            RecordingStore.CommitPending();

            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_NullChainId_ReturnsFalse()
        {
            var rec = new RecordingStore.Recording { ChainId = null, ChainIndex = 0 };
            Assert.False(RecordingStore.IsChainMidSegment(rec));
        }

        [Fact]
        public void IsChainMidSegment_EmptyChainId_ReturnsFalse()
        {
            var rec = new RecordingStore.Recording { ChainId = "", ChainIndex = 0 };
            Assert.False(RecordingStore.IsChainMidSegment(rec));
        }

        [Fact]
        public void IsChainMidSegment_NegativeChainIndex_ReturnsFalse()
        {
            var rec = new RecordingStore.Recording { ChainId = "some-chain", ChainIndex = -1 };
            Assert.False(RecordingStore.IsChainMidSegment(rec));
        }

        [Fact]
        public void IsChainMidSegment_SingleSegmentChain_ReturnsFalse()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Solo");
            RecordingStore.Pending.ChainId = "chain-one";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_ThreeSegments_MiddleIsTrue()
        {
            for (int i = 0; i < 3; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Seg{i}");
                RecordingStore.Pending.ChainId = "mid-three";
                RecordingStore.Pending.ChainIndex = i;
                RecordingStore.CommitPending();
            }

            Assert.True(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
            Assert.True(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[1]));
            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[2]));
        }

        #endregion

        #region GetChainEndUT

        [Fact]
        public void GetChainEndUT_ReturnsMaxEndUT()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "end-test";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg1");
            RecordingStore.Pending.ChainId = "end-test";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            var seg0 = RecordingStore.CommittedRecordings[0];
            var seg1 = RecordingStore.CommittedRecordings[1];

            Assert.Equal(seg1.EndUT, RecordingStore.GetChainEndUT(seg0));
            Assert.Equal(seg1.EndUT, RecordingStore.GetChainEndUT(seg1));
        }

        [Fact]
        public void GetChainEndUT_Standalone_ReturnsOwnEndUT()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Solo");
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            Assert.Equal(rec.EndUT, RecordingStore.GetChainEndUT(rec));
        }

        [Fact]
        public void GetChainEndUT_NullChainId_ReturnsOwnEndUT()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.AddRange(MakePoints(3, 100));
            Assert.Equal(rec.EndUT, RecordingStore.GetChainEndUT(rec));
        }

        [Fact]
        public void GetChainEndUT_SingleSegmentChain_ReturnsOwnEndUT()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Solo");
            RecordingStore.Pending.ChainId = "chain-one";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            Assert.Equal(rec.EndUT, RecordingStore.GetChainEndUT(rec));
        }

        [Fact]
        public void GetChainEndUT_ThreeSegments_AllReturnLastEndUT()
        {
            for (int i = 0; i < 3; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Seg{i}");
                RecordingStore.Pending.ChainId = "end-three";
                RecordingStore.Pending.ChainIndex = i;
                RecordingStore.CommitPending();
            }

            double lastEndUT = RecordingStore.CommittedRecordings[2].EndUT;
            for (int i = 0; i < 3; i++)
                Assert.Equal(lastEndUT, RecordingStore.GetChainEndUT(RecordingStore.CommittedRecordings[i]));
        }

        #endregion

        #region BuildExcludeCrewSet

        [Fact]
        public void BuildExcludeCrewSet_ChainWithBoarding_ReturnsNull_CrewBoardedBack()
        {
            // V(0) → EVA(1) → V(2): Jeb went EVA then boarded back.
            // The final vessel (seg2) has Jeb on board — don't exclude.
            RecordingStore.StashPending(MakePoints(3, 100), "Vessel1");
            RecordingStore.Pending.ChainId = "crew-chain";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.RecordingId = "seg0";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 150), "EVA Jeb");
            RecordingStore.Pending.ChainId = "crew-chain";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.EvaCrewName = "Jebediah Kerman";
            RecordingStore.Pending.RecordingId = "seg1";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Vessel2");
            RecordingStore.Pending.ChainId = "crew-chain";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.RecordingId = "seg2";
            RecordingStore.CommitPending();

            var finalSeg = RecordingStore.CommittedRecordings[2];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(finalSeg);

            // EVA crew boarded back (vessel segment after EVA) — don't exclude
            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_ChainEvaExit_ExcludesCrewStillOnEva()
        {
            // V(0) → EVA(1): Jeb went EVA and didn't board back.
            // Parent vessel should exclude Jeb (he spawns separately as EVA).
            RecordingStore.StashPending(MakePoints(3, 100), "Vessel");
            RecordingStore.Pending.ChainId = "eva-exit";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.RecordingId = "seg0";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 150), "EVA Jeb");
            RecordingStore.Pending.ChainId = "eva-exit";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.EvaCrewName = "Jebediah Kerman";
            RecordingStore.Pending.RecordingId = "seg1";
            RecordingStore.CommitPending();

            var parentVessel = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(parentVessel);

            // Jeb is still on EVA (no vessel segment after EVA) — exclude from parent spawn
            Assert.NotNull(excludeSet);
            Assert.Contains("Jebediah Kerman", excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_LegacyParentChild_ExcludesChildCrew()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Parent");
            RecordingStore.Pending.RecordingId = "parent-id";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 150), "EVA Child");
            RecordingStore.Pending.ParentRecordingId = "parent-id";
            RecordingStore.Pending.EvaCrewName = "Bill Kerman";
            RecordingStore.CommitPending();

            var parent = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(parent);

            Assert.NotNull(excludeSet);
            Assert.Contains("Bill Kerman", excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_ChainEvaSegment_ReturnsNull_NeverExcludesOwnCrew()
        {
            // V(0) → EVA(1): calling BuildExcludeCrewSet on the EVA segment itself
            // should return null — EVA segments never exclude their own crew.
            RecordingStore.StashPending(MakePoints(3, 100), "Vessel");
            RecordingStore.Pending.ChainId = "eva-self";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.RecordingId = "seg0";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 150), "EVA Jeb");
            RecordingStore.Pending.ChainId = "eva-self";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.EvaCrewName = "Jebediah Kerman";
            RecordingStore.Pending.RecordingId = "seg1";
            RecordingStore.CommitPending();

            var evaSeg = RecordingStore.CommittedRecordings[1];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(evaSeg);

            // EVA segment should not exclude its own crew
            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_Standalone_ReturnsNull()
        {
            RecordingStore.StashPending(MakePoints(3), "Solo");
            RecordingStore.Pending.RecordingId = "solo-id";
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(rec);

            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_EmptyRecordingId_ReturnsNull()
        {
            var rec = new RecordingStore.Recording { RecordingId = "" };
            Assert.Null(VesselSpawner.BuildExcludeCrewSet(rec));
        }

        [Fact]
        public void BuildExcludeCrewSet_MultiEvaChain_OnlyExcludesUnboarded()
        {
            // V(0) → EVA_Jeb(1) → V(2) → EVA_Bill(3)
            // Jeb boarded back (vessel segment 2 after EVA 1) — NOT excluded
            // Bill still on EVA (no vessel after EVA 3) — excluded
            RecordingStore.StashPending(MakePoints(3, 100), "Vessel1");
            RecordingStore.Pending.ChainId = "multi-eva";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.RecordingId = "v0";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 150), "EVA Jeb");
            RecordingStore.Pending.ChainId = "multi-eva";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.EvaCrewName = "Jebediah Kerman";
            RecordingStore.Pending.RecordingId = "e1";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Vessel2");
            RecordingStore.Pending.ChainId = "multi-eva";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.RecordingId = "v2";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 250), "EVA Bill");
            RecordingStore.Pending.ChainId = "multi-eva";
            RecordingStore.Pending.ChainIndex = 3;
            RecordingStore.Pending.EvaCrewName = "Bill Kerman";
            RecordingStore.Pending.RecordingId = "e3";
            RecordingStore.CommitPending();

            // Check vessel segment 0 — Bill excluded (EVA index 3 > vessel index 2)
            var vessel0 = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(vessel0);
            Assert.NotNull(excludeSet);
            Assert.Contains("Bill Kerman", excludeSet);
            Assert.DoesNotContain("Jebediah Kerman", excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_ChainVesselNoEva_ReturnsNull()
        {
            // V(0) → V(1): vessel-only chain (no EVA segments)
            RecordingStore.StashPending(MakePoints(3, 100), "Vessel1");
            RecordingStore.Pending.ChainId = "no-eva";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.RecordingId = "v0";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Vessel2");
            RecordingStore.Pending.ChainId = "no-eva";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.RecordingId = "v1";
            RecordingStore.CommitPending();

            var excludeSet = VesselSpawner.BuildExcludeCrewSet(RecordingStore.CommittedRecordings[0]);
            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_LegacyMultipleChildren_ExcludesAll()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Parent");
            RecordingStore.Pending.RecordingId = "parent-multi";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 150), "EVA Jeb");
            RecordingStore.Pending.ParentRecordingId = "parent-multi";
            RecordingStore.Pending.EvaCrewName = "Jebediah Kerman";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "EVA Bill");
            RecordingStore.Pending.ParentRecordingId = "parent-multi";
            RecordingStore.Pending.EvaCrewName = "Bill Kerman";
            RecordingStore.CommitPending();

            var parent = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(parent);

            Assert.NotNull(excludeSet);
            Assert.Equal(2, excludeSet.Count);
            Assert.Contains("Jebediah Kerman", excludeSet);
            Assert.Contains("Bill Kerman", excludeSet);
        }

        #endregion

        #region Chain fields in Recording

        [Fact]
        public void Recording_ChainFields_DefaultValues()
        {
            var rec = new RecordingStore.Recording();
            Assert.Null(rec.ChainId);
            Assert.Equal(-1, rec.ChainIndex);
        }

        [Fact]
        public void Recording_DefaultValues_AllFields()
        {
            var rec = new RecordingStore.Recording();
            Assert.False(string.IsNullOrEmpty(rec.RecordingId));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
            Assert.Empty(rec.Points);
            Assert.Empty(rec.OrbitSegments);
            Assert.Empty(rec.PartEvents);
            Assert.Null(rec.ParentRecordingId);
            Assert.Null(rec.EvaCrewName);
            Assert.Null(rec.VesselSnapshot);
            Assert.Null(rec.GhostVisualSnapshot);
            Assert.False(rec.VesselSpawned);
            Assert.False(rec.VesselDestroyed);
            Assert.False(rec.TakenControl);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void Recording_StartUT_EndUT_EmptyPoints()
        {
            var rec = new RecordingStore.Recording();
            Assert.Equal(0, rec.StartUT);
            Assert.Equal(0, rec.EndUT);
        }

        [Fact]
        public void Recording_StartUT_EndUT_WithPoints()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.AddRange(MakePoints(5, 100));
            Assert.Equal(100, rec.StartUT);
            Assert.Equal(140, rec.EndUT); // 100 + 4*10
        }

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesChainFields()
        {
            var source = new RecordingStore.Recording
            {
                ChainId = "test-chain",
                ChainIndex = 2,
                VesselName = "Source"
            };

            var target = new RecordingStore.Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("test-chain", target.ChainId);
            Assert.Equal(2, target.ChainIndex);
        }

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesAllFields()
        {
            var source = new RecordingStore.Recording
            {
                RecordingId = "src-id",
                ChainId = "test-chain",
                ChainIndex = 2,
                ParentRecordingId = "parent-1",
                EvaCrewName = "Jeb",
                VesselDestroyed = true,
                DistanceFromLaunch = 500.0,
                MaxDistanceFromLaunch = 1000.0,
                VesselSituation = "Landed on Kerbin"
            };

            var target = new RecordingStore.Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("src-id", target.RecordingId);
            Assert.Equal("test-chain", target.ChainId);
            Assert.Equal(2, target.ChainIndex);
            Assert.Equal("parent-1", target.ParentRecordingId);
            Assert.Equal("Jeb", target.EvaCrewName);
            Assert.True(target.VesselDestroyed);
            Assert.Equal(500.0, target.DistanceFromLaunch);
            Assert.Equal(1000.0, target.MaxDistanceFromLaunch);
            Assert.Equal("Landed on Kerbin", target.VesselSituation);
        }

        [Fact]
        public void ApplyPersistenceArtifacts_NullSource_DoesNothing()
        {
            var target = new RecordingStore.Recording { ChainId = "keep" };
            target.ApplyPersistenceArtifactsFrom(null);
            Assert.Equal("keep", target.ChainId);
        }

        #endregion

        #region RecordingBuilder chain support

        [Fact]
        public void RecordingBuilder_ChainFields_InV2Build()
        {
            var builder = new RecordingBuilder("Test")
                .WithChainId("my-chain")
                .WithChainIndex(1)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.Build();

            Assert.Equal("my-chain", node.GetValue("chainId"));
            Assert.Equal("1", node.GetValue("chainIndex"));
        }

        [Fact]
        public void RecordingBuilder_ChainFields_InV3Metadata()
        {
            var builder = new RecordingBuilder("Test")
                .WithRecordingId("v3test")
                .WithChainId("my-chain")
                .WithChainIndex(2)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.BuildV3Metadata();

            Assert.Equal("my-chain", node.GetValue("chainId"));
            Assert.Equal("2", node.GetValue("chainIndex"));
        }

        [Fact]
        public void RecordingBuilder_NoChain_OmitsChainFields()
        {
            var builder = new RecordingBuilder("Test")
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.Build();

            Assert.Null(node.GetValue("chainId"));
            Assert.Null(node.GetValue("chainIndex"));
        }

        [Fact]
        public void RecordingBuilder_EvaFields()
        {
            var builder = new RecordingBuilder("EVA")
                .WithParentRecordingId("parent-1")
                .WithEvaCrewName("Jeb")
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.Build();

            Assert.Equal("parent-1", node.GetValue("parentRecordingId"));
            Assert.Equal("Jeb", node.GetValue("evaCrewName"));
        }

        [Fact]
        public void RecordingBuilder_GetRecordingId_GeneratesIfNotSet()
        {
            var builder = new RecordingBuilder("Test").AddPoint(100, 0, 0, 0);
            string id = builder.GetRecordingId();
            Assert.False(string.IsNullOrEmpty(id));
            // Calling again returns the same
            Assert.Equal(id, builder.GetRecordingId());
        }

        [Fact]
        public void RecordingBuilder_GetRecordingId_UsesExplicit()
        {
            var builder = new RecordingBuilder("Test")
                .WithRecordingId("explicit-id")
                .AddPoint(100, 0, 0, 0);
            Assert.Equal("explicit-id", builder.GetRecordingId());
        }

        #endregion

        #region Synthetic chain recordings

        [Fact]
        public void EvaBoardChain_BuildsThreeLinkedSegments()
        {
            var segments = SyntheticRecordingTests.EvaBoardChain();

            Assert.Equal(3, segments.Length);

            var nodes = new ConfigNode[3];
            for (int i = 0; i < 3; i++)
                nodes[i] = segments[i].Build();

            // All share the same chain ID
            string chainId = nodes[0].GetValue("chainId");
            Assert.False(string.IsNullOrEmpty(chainId));
            for (int i = 1; i < 3; i++)
                Assert.Equal(chainId, nodes[i].GetValue("chainId"));

            // Indices are 0, 1, 2
            Assert.Equal("0", nodes[0].GetValue("chainIndex"));
            Assert.Equal("1", nodes[1].GetValue("chainIndex"));
            Assert.Equal("2", nodes[2].GetValue("chainIndex"));

            // Segments 0 and 1 are ghost-only (no VESSEL_SNAPSHOT)
            Assert.Null(nodes[0].GetNode("VESSEL_SNAPSHOT"));
            Assert.Null(nodes[1].GetNode("VESSEL_SNAPSHOT"));

            // Segment 2 has VESSEL_SNAPSHOT (spawns!)
            Assert.NotNull(nodes[2].GetNode("VESSEL_SNAPSHOT"));

            // Segments 0 and 1 have ghost visual snapshots
            Assert.NotNull(nodes[0].GetNode("GHOST_VISUAL_SNAPSHOT"));
            Assert.NotNull(nodes[1].GetNode("GHOST_VISUAL_SNAPSHOT"));

            // Segment 1 has EVA crew name
            Assert.Equal("Jebediah Kerman", nodes[1].GetValue("evaCrewName"));

            // All have trajectory points
            Assert.True(nodes[0].GetNodes("POINT").Length > 2);
            Assert.True(nodes[1].GetNodes("POINT").Length > 2);
            Assert.True(nodes[2].GetNodes("POINT").Length > 2);
        }

        [Fact]
        public void EvaWalkChain_BuildsTwoLinkedSegments()
        {
            var segments = SyntheticRecordingTests.EvaWalkChain();

            Assert.Equal(2, segments.Length);

            var nodes = new ConfigNode[2];
            for (int i = 0; i < 2; i++)
                nodes[i] = segments[i].Build();

            // All share the same chain ID
            string chainId = nodes[0].GetValue("chainId");
            Assert.False(string.IsNullOrEmpty(chainId));
            Assert.Equal(chainId, nodes[1].GetValue("chainId"));

            // Indices are 0, 1
            Assert.Equal("0", nodes[0].GetValue("chainIndex"));
            Assert.Equal("1", nodes[1].GetValue("chainIndex"));

            // Segment 0 has VesselSnapshot (continuation extends trajectory — spawns at chain end)
            Assert.NotNull(nodes[0].GetNode("VESSEL_SNAPSHOT"));
            // Segment 1 is ghost-only (EVA — no vessel spawn)
            Assert.Null(nodes[1].GetNode("VESSEL_SNAPSHOT"));

            // Both have ghost visual snapshots
            Assert.NotNull(nodes[0].GetNode("GHOST_VISUAL_SNAPSHOT"));
            Assert.NotNull(nodes[1].GetNode("GHOST_VISUAL_SNAPSHOT"));

            // Segment 0 is a vessel (FleaRocket ghost)
            var ghost0 = nodes[0].GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.Equal("Ship", ghost0.GetValue("type"));

            // Segment 1 is EVA
            Assert.Equal("Bill Kerman", nodes[1].GetValue("evaCrewName"));
            var ghost1 = nodes[1].GetNode("GHOST_VISUAL_SNAPSHOT");
            Assert.Equal("EVA", ghost1.GetValue("type"));

            // Both have trajectory points
            Assert.True(nodes[0].GetNodes("POINT").Length >= 10);
            Assert.True(nodes[1].GetNodes("POINT").Length >= 5);

            // Boundary anchor: segment 1's first point UT matches segment 0's last point UT
            var seg0Points = nodes[0].GetNodes("POINT");
            var seg1Points = nodes[1].GetNodes("POINT");
            string seg0LastUT = seg0Points[seg0Points.Length - 1].GetValue("ut");
            string seg1FirstUT = seg1Points[0].GetValue("ut");
            Assert.Equal(seg0LastUT, seg1FirstUT);

            // Segment 0 has engine events
            Assert.True(nodes[0].GetNodes("PART_EVENT").Length >= 2);
        }

        #endregion

        #region Chain spawn safety

        [Fact]
        public void MidChainVesselSegment_WithSnapshot_SpawnDeferredByPastChainEnd()
        {
            // Mid-chain vessel segments can have VesselSnapshot when continuation
            // sampling extends the trajectory. Spawning is deferred to pastChainEnd
            // by the UpdateTimelinePlayback condition (pastChainEnd && needsSpawn).
            RecordingStore.StashPending(MakePoints(10, 100), "Vessel Seg");
            RecordingStore.Pending.ChainId = "spawn-test";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.Pending.GhostVisualSnapshot = new ConfigNode("GHOST");
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(5, 200), "EVA Seg");
            RecordingStore.Pending.ChainId = "spawn-test";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var vessel = RecordingStore.CommittedRecordings[0];
            var eva = RecordingStore.CommittedRecordings[1];

            Assert.True(RecordingStore.IsChainMidSegment(vessel));
            Assert.False(RecordingStore.IsChainMidSegment(eva));

            // With continuation, vessel segment keeps VesselSnapshot → needsSpawn = true
            bool needsSpawn = vessel.VesselSnapshot != null && !vessel.VesselSpawned && !vessel.VesselDestroyed;
            Assert.True(needsSpawn);

            // But spawning only occurs when pastChainEnd is true (UT > chain's last segment EndUT)
            double chainEndUT = RecordingStore.GetChainEndUT(vessel);
            Assert.Equal(eva.EndUT, chainEndUT);

            // Before chain end: UT 150 is within vessel's range but before chain end
            double utBeforeChainEnd = 150;
            Assert.True(utBeforeChainEnd < chainEndUT);
            // → ghost plays, no spawn yet

            // After chain end: UT 250 is past the EVA segment's EndUT
            double utAfterChainEnd = 250;
            Assert.True(utAfterChainEnd > chainEndUT);
            // → vessel spawns at its final recorded position
        }

        [Fact]
        public void IsChainMidSegment_ReturnsFalse_WhenOnlyOneSegmentCommitted()
        {
            // When CommitChainSegment commits the vessel segment but the EVA segment
            // hasn't been committed yet, IsChainMidSegment returns false.
            // This is the window where spawning must be suppressed by the activeChainId guard.
            RecordingStore.StashPending(MakePoints(10, 100), "Vessel Seg");
            RecordingStore.Pending.ChainId = "incomplete-chain";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var vessel = RecordingStore.CommittedRecordings[0];

            // With only segment 0 committed, it's NOT detected as mid-chain
            // because there's no segment with a higher index.
            Assert.False(RecordingStore.IsChainMidSegment(vessel));

            // This means the activeChainId guard in UpdateTimelinePlayback is ESSENTIAL
            // to prevent spawning during chain building.
        }

        [Fact]
        public void ChainSpawnDecision_NeedsSpawn_IsFalse_WhenSnapshotNull()
        {
            // Verify the spawn decision logic: needsSpawn requires VesselSnapshot != null
            RecordingStore.StashPending(MakePoints(5, 100), "Ghost Seg");
            RecordingStore.Pending.ChainId = "spawn-decision";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.VesselSnapshot = null; // ghost-only
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;
            Assert.False(needsSpawn);
        }

        [Fact]
        public void ChainSpawnDecision_NeedsSpawn_IsFalse_WhenAlreadySpawned()
        {
            RecordingStore.StashPending(MakePoints(5, 100), "Spawned Seg");
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.Pending.VesselSpawned = true;
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;
            Assert.False(needsSpawn);
        }

        [Fact]
        public void ChainSpawnDecision_NeedsSpawn_IsFalse_WhenDestroyed()
        {
            RecordingStore.StashPending(MakePoints(5, 100), "Destroyed Seg");
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.Pending.VesselDestroyed = true;
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;
            Assert.False(needsSpawn);
        }

        [Fact]
        public void ChainSpawnDecision_FinalSegment_CanSpawn()
        {
            // Final chain segment keeps its VesselSnapshot and CAN spawn
            RecordingStore.StashPending(MakePoints(10, 100), "Vessel Seg");
            RecordingStore.Pending.ChainId = "final-test";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.VesselSnapshot = null; // ghost-only (nulled by CommitChainSegment)
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(5, 200), "EVA Final");
            RecordingStore.Pending.ChainId = "final-test";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL"); // final keeps snapshot
            RecordingStore.CommitPending();

            var eva = RecordingStore.CommittedRecordings[1];
            Assert.False(RecordingStore.IsChainMidSegment(eva));
            bool needsSpawn = eva.VesselSnapshot != null && !eva.VesselSpawned && !eva.VesselDestroyed;
            Assert.True(needsSpawn);
        }

        [Fact]
        public void ActiveChainGuard_SuppressesSpawn_WhenChainIdMatches()
        {
            // Simulates the activeChainId guard in UpdateTimelinePlayback:
            // if (activeChainId != null && rec.ChainId == activeChainId) needsSpawn = false;
            string activeChainId = "building-chain";

            RecordingStore.StashPending(MakePoints(10, 100), "Vessel Seg");
            RecordingStore.Pending.ChainId = "building-chain";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;
            Assert.True(needsSpawn); // would spawn without the guard

            // Apply the guard
            if (activeChainId != null && rec.ChainId == activeChainId)
                needsSpawn = false;

            Assert.False(needsSpawn); // suppressed by guard
        }

        [Fact]
        public void ContinuationStop_OnBoarding_NullsVesselSnapshot()
        {
            // Simulates the boarding flow: vessel segment committed with VesselSnapshot,
            // then EVA segment committed → continuation nulls the vessel's VesselSnapshot.
            // V(0) → EVA(1) → V(2) boarding case.
            RecordingStore.StashPending(MakePoints(10, 100), "Vessel1");
            RecordingStore.Pending.ChainId = "board-test";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var vesselRec = RecordingStore.CommittedRecordings[0];
            Assert.NotNull(vesselRec.VesselSnapshot); // present before boarding

            // Simulate boarding: continuation nulls the vessel segment's VesselSnapshot
            vesselRec.VesselSnapshot = null;
            Assert.Null(vesselRec.VesselSnapshot);

            // Spawn decision is now false for the vessel segment
            bool needsSpawn = vesselRec.VesselSnapshot != null && !vesselRec.VesselSpawned && !vesselRec.VesselDestroyed;
            Assert.False(needsSpawn);

            // The new vessel segment (index 2) handles spawning instead
            RecordingStore.StashPending(MakePoints(5, 150), "EVA");
            RecordingStore.Pending.ChainId = "board-test";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.EvaCrewName = "Jeb";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(5, 200), "Vessel2");
            RecordingStore.Pending.ChainId = "board-test";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var finalVessel = RecordingStore.CommittedRecordings[2];
            Assert.False(RecordingStore.IsChainMidSegment(finalVessel));
            bool finalNeedsSpawn = finalVessel.VesselSnapshot != null && !finalVessel.VesselSpawned && !finalVessel.VesselDestroyed;
            Assert.True(finalNeedsSpawn);
        }

        [Fact]
        public void MidChainVesselWithSnapshot_SpawnsAtChainEnd()
        {
            // V→EVA chain where vessel segment keeps VesselSnapshot (continuation).
            // Spawn should be gated by pastChainEnd, not just pastEnd of the vessel segment.
            RecordingStore.StashPending(MakePoints(10, 100), "Vessel");
            RecordingStore.Pending.ChainId = "spawn-chain";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(5, 250), "EVA");
            RecordingStore.Pending.ChainId = "spawn-chain";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.CommitPending();

            var vessel = RecordingStore.CommittedRecordings[0];
            double vesselEndUT = vessel.EndUT;   // 190 (100 + 9*10)
            double chainEndUT = RecordingStore.GetChainEndUT(vessel); // EVA segment EndUT = 290

            // needsSpawn is true because VesselSnapshot is present
            bool needsSpawn = vessel.VesselSnapshot != null && !vessel.VesselSpawned && !vessel.VesselDestroyed;
            Assert.True(needsSpawn);

            // At UT 200: past vessel's own EndUT (190) but NOT past chain end (290)
            // → ghost holds at final position, no spawn
            bool pastEnd = 200 > vesselEndUT;
            bool pastChainEnd = 200 > chainEndUT;
            Assert.True(pastEnd);
            Assert.False(pastChainEnd);

            // At UT 300: past chain end (290) → vessel spawns
            bool pastChainEnd2 = 300 > chainEndUT;
            Assert.True(pastChainEnd2);
        }

        [Fact]
        public void ContinuationAppendedPoints_ExtendEndUT()
        {
            // Appending trajectory points to a committed recording extends its EndUT
            RecordingStore.StashPending(MakePoints(5, 100), "Test Vessel");
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            double originalEndUT = rec.EndUT; // 140 (100 + 4*10)
            Assert.Equal(140, originalEndUT);

            // Simulate continuation: append points with later UTs
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 100,
                rotation = Quaternion.identity, velocity = Vector3.zero,
                bodyName = "Kerbin"
            });

            Assert.Equal(200, rec.EndUT);
            Assert.True(rec.EndUT > originalEndUT);

            // Chain EndUT also updates (it reads EndUT from the recording)
            rec.ChainId = "extend-test";
            rec.ChainIndex = 0;
            Assert.Equal(200, RecordingStore.GetChainEndUT(rec));
        }

        [Fact]
        public void ActiveChainGuard_DoesNotAffect_UnrelatedRecordings()
        {
            string activeChainId = "building-chain";

            // Unrelated recording (different chain or standalone)
            RecordingStore.StashPending(MakePoints(10, 100), "Other Vessel");
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;
            Assert.True(needsSpawn);

            // Guard doesn't suppress — ChainId is null, doesn't match activeChainId
            if (activeChainId != null && rec.ChainId == activeChainId)
                needsSpawn = false;

            Assert.True(needsSpawn); // still true — not suppressed
        }

        #endregion
    }
}
