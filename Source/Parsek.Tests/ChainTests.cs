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
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
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
        public void DecideOnVesselSwitch_VesselToOther_ReturnsTransitionToBackground()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_VesselToEva_NotStartedAsEva_ReturnsTransitionToBackground()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, true, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
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

        #region CreateRecordingFromFlightData edge cases

        [Fact]
        public void CreateRecordingFromFlightData_NullPoints_ReturnsNull()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(null, "Test");
            Assert.Null(rec);
        }

        [Fact]
        public void CreateRecordingFromFlightData_OnePoint_ReturnsNull()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(1), "Test");
            Assert.Null(rec);
        }

        [Fact]
        public void CreateRecordingFromFlightData_TwoPoints_CreatesRecording()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(2), "Test");
            Assert.NotNull(rec);
            Assert.Equal("Test", rec.VesselName);
            Assert.Equal(2, rec.Points.Count);
        }

        [Fact]
        public void CreateRecordingFromFlightData_CustomId_UsesIt()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship", recordingId: "custom-id");
            Assert.NotNull(rec);
            Assert.Equal("custom-id", rec.RecordingId);
        }

        [Fact]
        public void CreateRecordingFromFlightData_NullId_GeneratesId()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship", recordingId: null);
            Assert.NotNull(rec);
            Assert.False(string.IsNullOrEmpty(rec.RecordingId));
        }

        [Fact]
        public void CreateRecordingFromFlightData_WithOrbitSegments_PreservesThem()
        {
            var segs = new List<OrbitSegment> { new OrbitSegment { startUT = 100, endUT = 200 } };
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship", orbitSegments: segs);
            Assert.NotNull(rec);
            Assert.Single(rec.OrbitSegments);
        }

        [Fact]
        public void CreateRecordingFromFlightData_WithPartEvents_PreservesThem()
        {
            var events = new List<PartEvent> { new PartEvent { ut = 100, eventType = PartEventType.Decoupled } };
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship", partEvents: events);
            Assert.NotNull(rec);
            Assert.Single(rec.PartEvents);
        }

        #endregion

        #region CommitRecordingDirect / Clear

        [Fact]
        public void CommitRecordingDirect_NullRecording_DoesNothing()
        {
            RecordingStore.CommitRecordingDirect(null);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void CommitRecordingDirect_AddsToCommitted()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Ship", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void Clear_RemovesAll()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Rec1");
            Assert.NotNull(rec1);
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Rec2");
            Assert.NotNull(rec2);
            RecordingStore.CommitRecordingDirect(rec2);

            RecordingStore.Clear();

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void ClearCommitted_OnlyClearsCommitted()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "A");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            RecordingStore.ClearCommitted();

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        #endregion

        #region ValidateChains

        [Fact]
        public void ValidateChains_ValidChain_PreservesFields()
        {
            for (int i = 0; i < 3; i++)
            {
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Seg{i}");
                Assert.NotNull(rec);
                rec.ChainId = "chain-valid";
                rec.ChainIndex = i;
                RecordingStore.CommitRecordingDirect(rec);
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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "chain-gap";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg2");
            Assert.NotNull(rec2);
            rec2.ChainId = "chain-gap";
            rec2.ChainIndex = 2;
            RecordingStore.CommitRecordingDirect(rec2);

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
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Seg{i}");
                Assert.NotNull(rec);
                rec.ChainId = "chain-dup";
                rec.ChainIndex = 0; // Both index 0
                RecordingStore.CommitRecordingDirect(rec);
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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "chain-ut";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg1");
            Assert.NotNull(rec2);
            rec2.ChainId = "chain-ut";
            rec2.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec2);

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
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Good{i}");
                Assert.NotNull(rec);
                rec.ChainId = "chain-good";
                rec.ChainIndex = i;
                RecordingStore.CommitRecordingDirect(rec);
            }

            // Invalid chain (gap)
            var bad1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "Bad0");
            Assert.NotNull(bad1);
            bad1.ChainId = "chain-bad";
            bad1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(bad1);

            var bad2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 400), "Bad2");
            Assert.NotNull(bad2);
            bad2.ChainId = "chain-bad";
            bad2.ChainIndex = 2;
            RecordingStore.CommitRecordingDirect(bad2);

            RecordingStore.ValidateChains();

            Assert.Equal("chain-good", RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal("chain-good", RecordingStore.CommittedRecordings[1].ChainId);
            Assert.Null(RecordingStore.CommittedRecordings[2].ChainId);
            Assert.Null(RecordingStore.CommittedRecordings[3].ChainId);
        }

        [Fact]
        public void ValidateChains_StandaloneRecordings_Unaffected()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Standalone");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            RecordingStore.ValidateChains();

            Assert.Null(RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal(-1, RecordingStore.CommittedRecordings[0].ChainIndex);
        }

        [Fact]
        public void ValidateChains_SingleSegmentChain_Valid()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Solo");
            Assert.NotNull(rec);
            rec.ChainId = "chain-solo";
            rec.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec);

            RecordingStore.ValidateChains();

            Assert.Equal("chain-solo", RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal(0, RecordingStore.CommittedRecordings[0].ChainIndex);
        }

        [Fact]
        public void ValidateChains_EqualStartUT_Valid()
        {
            // Boundary-anchored segments can have equal StartUT
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "chain-eq-ut";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg1");
            Assert.NotNull(rec2);
            rec2.ChainId = "chain-eq-ut";
            rec2.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec2);

            RecordingStore.ValidateChains();

            Assert.Equal("chain-eq-ut", RecordingStore.CommittedRecordings[0].ChainId);
            Assert.Equal("chain-eq-ut", RecordingStore.CommittedRecordings[1].ChainId);
        }

        [Fact]
        public void ValidateChains_StartsAtIndex1_Degraded()
        {
            // Chain starts at index 1, missing index 0
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg1");
            Assert.NotNull(rec1);
            rec1.ChainId = "chain-no-zero";
            rec1.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg2");
            Assert.NotNull(rec2);
            rec2.ChainId = "chain-no-zero";
            rec2.ChainIndex = 2;
            RecordingStore.CommitRecordingDirect(rec2);

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
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            Assert.Null(RecordingStore.GetChainRecordings("nonexistent"));
        }

        [Fact]
        public void GetChainRecordings_ReturnsSortedByIndex()
        {
            // Add in reverse order
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg2");
            Assert.NotNull(rec1);
            rec1.ChainId = "chain-sort";
            rec1.ChainIndex = 2;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec2);
            rec2.ChainId = "chain-sort";
            rec2.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "Seg1");
            Assert.NotNull(rec3);
            rec3.ChainId = "chain-sort";
            rec3.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec3);

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
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Solo");
            Assert.NotNull(rec);
            rec.ChainId = "chain-single";
            rec.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec);

            var chain = RecordingStore.GetChainRecordings("chain-single");
            Assert.NotNull(chain);
            Assert.Single(chain);
            Assert.Equal(0, chain[0].ChainIndex);
        }

        [Fact]
        public void GetChainRecordings_IgnoresOtherChains()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "A0");
            Assert.NotNull(rec1);
            rec1.ChainId = "chain-a";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B0");
            Assert.NotNull(rec2);
            rec2.ChainId = "chain-b";
            rec2.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "A1");
            Assert.NotNull(rec3);
            rec3.ChainId = "chain-a";
            rec3.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec3);

            var chain = RecordingStore.GetChainRecordings("chain-a");
            Assert.NotNull(chain);
            Assert.Equal(2, chain.Count);
            Assert.Equal("A0", chain[0].VesselName);
            Assert.Equal("A1", chain[1].VesselName);
        }

        [Fact]
        public void RemoveChainRecordings_RemovesMatchingOnly()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Chain1");
            Assert.NotNull(rec1);
            rec1.ChainId = "remove-me";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Standalone");
            Assert.NotNull(rec2);
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "Chain2");
            Assert.NotNull(rec3);
            rec3.ChainId = "remove-me";
            rec3.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec3);

            RecordingStore.RemoveChainRecordings("remove-me");

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Standalone", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void RemoveChainRecordings_NullId_DoesNothing()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            RecordingStore.RemoveChainRecordings(null);

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveChainRecordings_EmptyId_DoesNothing()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            RecordingStore.RemoveChainRecordings("");

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveChainRecordings_NoMatch_DoesNothing()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            RecordingStore.RemoveChainRecordings("nonexistent");

            Assert.Single(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void RemoveChainRecordings_AllSegments_LeavesEmpty()
        {
            for (int i = 0; i < 3; i++)
            {
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Seg{i}");
                Assert.NotNull(rec);
                rec.ChainId = "remove-all";
                rec.ChainIndex = i;
                RecordingStore.CommitRecordingDirect(rec);
            }

            RecordingStore.RemoveChainRecordings("remove-all");

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        #endregion

        #region IsChainMidSegment

        [Fact]
        public void IsChainMidSegment_MidSegment_ReturnsTrue()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "mid-test";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg1");
            Assert.NotNull(rec2);
            rec2.ChainId = "mid-test";
            rec2.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec2);

            Assert.True(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_LastSegment_ReturnsFalse()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "last-test";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg1");
            Assert.NotNull(rec2);
            rec2.ChainId = "last-test";
            rec2.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec2);

            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[1]));
        }

        [Fact]
        public void IsChainMidSegment_Standalone_ReturnsFalse()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Solo");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_NullChainId_ReturnsFalse()
        {
            var rec = new Recording { ChainId = null, ChainIndex = 0 };
            Assert.False(RecordingStore.IsChainMidSegment(rec));
        }

        [Fact]
        public void IsChainMidSegment_EmptyChainId_ReturnsFalse()
        {
            var rec = new Recording { ChainId = "", ChainIndex = 0 };
            Assert.False(RecordingStore.IsChainMidSegment(rec));
        }

        [Fact]
        public void IsChainMidSegment_NegativeChainIndex_ReturnsFalse()
        {
            var rec = new Recording { ChainId = "some-chain", ChainIndex = -1 };
            Assert.False(RecordingStore.IsChainMidSegment(rec));
        }

        [Fact]
        public void IsChainMidSegment_SingleSegmentChain_ReturnsFalse()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Solo");
            Assert.NotNull(rec);
            rec.ChainId = "chain-one";
            rec.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec);

            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_ThreeSegments_MiddleIsTrue()
        {
            for (int i = 0; i < 3; i++)
            {
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Seg{i}");
                Assert.NotNull(rec);
                rec.ChainId = "mid-three";
                rec.ChainIndex = i;
                RecordingStore.CommitRecordingDirect(rec);
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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "end-test";
            rec1.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg1");
            Assert.NotNull(rec2);
            rec2.ChainId = "end-test";
            rec2.ChainIndex = 1;
            RecordingStore.CommitRecordingDirect(rec2);

            var seg0 = RecordingStore.CommittedRecordings[0];
            var seg1 = RecordingStore.CommittedRecordings[1];

            Assert.Equal(seg1.EndUT, RecordingStore.GetChainEndUT(seg0));
            Assert.Equal(seg1.EndUT, RecordingStore.GetChainEndUT(seg1));
        }

        [Fact]
        public void GetChainEndUT_Standalone_ReturnsOwnEndUT()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Solo");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            var committed = RecordingStore.CommittedRecordings[0];
            Assert.Equal(committed.EndUT, RecordingStore.GetChainEndUT(committed));
        }

        [Fact]
        public void GetChainEndUT_NullChainId_ReturnsOwnEndUT()
        {
            var rec = new Recording();
            rec.Points.AddRange(MakePoints(3, 100));
            Assert.Equal(rec.EndUT, RecordingStore.GetChainEndUT(rec));
        }

        [Fact]
        public void GetChainEndUT_SingleSegmentChain_ReturnsOwnEndUT()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Solo");
            Assert.NotNull(rec);
            rec.ChainId = "chain-one";
            rec.ChainIndex = 0;
            RecordingStore.CommitRecordingDirect(rec);

            var committed = RecordingStore.CommittedRecordings[0];
            Assert.Equal(committed.EndUT, RecordingStore.GetChainEndUT(committed));
        }

        [Fact]
        public void GetChainEndUT_ThreeSegments_AllReturnLastEndUT()
        {
            for (int i = 0; i < 3; i++)
            {
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Seg{i}");
                Assert.NotNull(rec);
                rec.ChainId = "end-three";
                rec.ChainIndex = i;
                RecordingStore.CommitRecordingDirect(rec);
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
            // V(0) -> EVA(1) -> V(2): Jeb went EVA then boarded back.
            // The final vessel (seg2) has Jeb on board -- don't exclude.
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Vessel1");
            Assert.NotNull(rec1);
            rec1.ChainId = "crew-chain";
            rec1.ChainIndex = 0;
            rec1.RecordingId = "seg0";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "EVA Jeb");
            Assert.NotNull(rec2);
            rec2.ChainId = "crew-chain";
            rec2.ChainIndex = 1;
            rec2.EvaCrewName = "Jebediah Kerman";
            rec2.RecordingId = "seg1";
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Vessel2");
            Assert.NotNull(rec3);
            rec3.ChainId = "crew-chain";
            rec3.ChainIndex = 2;
            rec3.RecordingId = "seg2";
            RecordingStore.CommitRecordingDirect(rec3);

            var finalSeg = RecordingStore.CommittedRecordings[2];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(finalSeg);

            // EVA crew boarded back (vessel segment after EVA) -- don't exclude
            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_ChainEvaExit_ExcludesCrewStillOnEva()
        {
            // V(0) -> EVA(1): Jeb went EVA and didn't board back.
            // Parent vessel should exclude Jeb (he spawns separately as EVA).
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Vessel");
            Assert.NotNull(rec1);
            rec1.ChainId = "eva-exit";
            rec1.ChainIndex = 0;
            rec1.RecordingId = "seg0";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "EVA Jeb");
            Assert.NotNull(rec2);
            rec2.ChainId = "eva-exit";
            rec2.ChainIndex = 1;
            rec2.EvaCrewName = "Jebediah Kerman";
            rec2.RecordingId = "seg1";
            RecordingStore.CommitRecordingDirect(rec2);

            var parentVessel = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(parentVessel);

            // Jeb is still on EVA (no vessel segment after EVA) -- exclude from parent spawn
            Assert.NotNull(excludeSet);
            Assert.Contains("Jebediah Kerman", excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_LegacyParentChild_ExcludesChildCrew()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Parent");
            Assert.NotNull(rec1);
            rec1.RecordingId = "parent-id";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "EVA Child");
            Assert.NotNull(rec2);
            rec2.ParentRecordingId = "parent-id";
            rec2.EvaCrewName = "Bill Kerman";
            RecordingStore.CommitRecordingDirect(rec2);

            var parent = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(parent);

            Assert.NotNull(excludeSet);
            Assert.Contains("Bill Kerman", excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_ChainEvaSegment_ReturnsNull_NeverExcludesOwnCrew()
        {
            // V(0) -> EVA(1): calling BuildExcludeCrewSet on the EVA segment itself
            // should return null -- EVA segments never exclude their own crew.
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Vessel");
            Assert.NotNull(rec1);
            rec1.ChainId = "eva-self";
            rec1.ChainIndex = 0;
            rec1.RecordingId = "seg0";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "EVA Jeb");
            Assert.NotNull(rec2);
            rec2.ChainId = "eva-self";
            rec2.ChainIndex = 1;
            rec2.EvaCrewName = "Jebediah Kerman";
            rec2.RecordingId = "seg1";
            RecordingStore.CommitRecordingDirect(rec2);

            var evaSeg = RecordingStore.CommittedRecordings[1];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(evaSeg);

            // EVA segment should not exclude its own crew
            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_Standalone_ReturnsNull()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Solo");
            Assert.NotNull(rec);
            rec.RecordingId = "solo-id";
            RecordingStore.CommitRecordingDirect(rec);

            var committed = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(committed);

            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_EmptyRecordingId_ReturnsNull()
        {
            var rec = new Recording { RecordingId = "" };
            Assert.Null(VesselSpawner.BuildExcludeCrewSet(rec));
        }

        [Fact]
        public void BuildExcludeCrewSet_MultiEvaChain_OnlyExcludesUnboarded()
        {
            // V(0) -> EVA_Jeb(1) -> V(2) -> EVA_Bill(3)
            // Jeb boarded back (vessel segment 2 after EVA 1) -- NOT excluded
            // Bill still on EVA (no vessel after EVA 3) -- excluded
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Vessel1");
            Assert.NotNull(rec1);
            rec1.ChainId = "multi-eva";
            rec1.ChainIndex = 0;
            rec1.RecordingId = "v0";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "EVA Jeb");
            Assert.NotNull(rec2);
            rec2.ChainId = "multi-eva";
            rec2.ChainIndex = 1;
            rec2.EvaCrewName = "Jebediah Kerman";
            rec2.RecordingId = "e1";
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Vessel2");
            Assert.NotNull(rec3);
            rec3.ChainId = "multi-eva";
            rec3.ChainIndex = 2;
            rec3.RecordingId = "v2";
            RecordingStore.CommitRecordingDirect(rec3);

            var rec4 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 250), "EVA Bill");
            Assert.NotNull(rec4);
            rec4.ChainId = "multi-eva";
            rec4.ChainIndex = 3;
            rec4.EvaCrewName = "Bill Kerman";
            rec4.RecordingId = "e3";
            RecordingStore.CommitRecordingDirect(rec4);

            // Check vessel segment 0 -- Bill excluded (EVA index 3 > vessel index 2)
            var vessel0 = RecordingStore.CommittedRecordings[0];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(vessel0);
            Assert.NotNull(excludeSet);
            Assert.Contains("Bill Kerman", excludeSet);
            Assert.DoesNotContain("Jebediah Kerman", excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_ChainVesselNoEva_ReturnsNull()
        {
            // V(0) -> V(1): vessel-only chain (no EVA segments)
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Vessel1");
            Assert.NotNull(rec1);
            rec1.ChainId = "no-eva";
            rec1.ChainIndex = 0;
            rec1.RecordingId = "v0";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Vessel2");
            Assert.NotNull(rec2);
            rec2.ChainId = "no-eva";
            rec2.ChainIndex = 1;
            rec2.RecordingId = "v1";
            RecordingStore.CommitRecordingDirect(rec2);

            var excludeSet = VesselSpawner.BuildExcludeCrewSet(RecordingStore.CommittedRecordings[0]);
            Assert.Null(excludeSet);
        }

        [Fact]
        public void BuildExcludeCrewSet_LegacyMultipleChildren_ExcludesAll()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Parent");
            Assert.NotNull(rec1);
            rec1.RecordingId = "parent-multi";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "EVA Jeb");
            Assert.NotNull(rec2);
            rec2.ParentRecordingId = "parent-multi";
            rec2.EvaCrewName = "Jebediah Kerman";
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "EVA Bill");
            Assert.NotNull(rec3);
            rec3.ParentRecordingId = "parent-multi";
            rec3.EvaCrewName = "Bill Kerman";
            RecordingStore.CommitRecordingDirect(rec3);

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
        public void Recording_StartUT_EndUT_WithPoints()
        {
            var rec = new Recording();
            rec.Points.AddRange(MakePoints(5, 100));
            Assert.Equal(100, rec.StartUT);
            Assert.Equal(140, rec.EndUT); // 100 + 4*10
        }

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesChainFields()
        {
            var source = new Recording
            {
                ChainId = "test-chain",
                ChainIndex = 2,
                VesselName = "Source"
            };

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("test-chain", target.ChainId);
            Assert.Equal(2, target.ChainIndex);
        }

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesAllFields()
        {
            var source = new Recording
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

            var target = new Recording();
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
            var target = new Recording { ChainId = "keep" };
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

            // Segment 0 has VesselSnapshot (continuation extends trajectory -- spawns at chain end)
            Assert.NotNull(nodes[0].GetNode("VESSEL_SNAPSHOT"));
            // Segment 1 is ghost-only (EVA -- no vessel spawn)
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
        public void IsChainMidSegment_ReturnsFalse_WhenOnlyOneSegmentCommitted()
        {
            // When CommitChainSegment commits the vessel segment but the EVA segment
            // hasn't been committed yet, IsChainMidSegment returns false.
            // This is the window where spawning must be suppressed by the activeChainId guard.
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(10, 100), "Vessel Seg");
            Assert.NotNull(rec);
            rec.ChainId = "incomplete-chain";
            rec.ChainIndex = 0;
            rec.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitRecordingDirect(rec);

            var vessel = RecordingStore.CommittedRecordings[0];

            // With only segment 0 committed, it's NOT detected as mid-chain
            // because there's no segment with a higher index.
            Assert.False(RecordingStore.IsChainMidSegment(vessel));

            // This means the activeChainId guard in UpdateTimelinePlayback is ESSENTIAL
            // to prevent spawning during chain building.
        }

        [Fact]
        public void ContinuationAppendedPoints_ExtendEndUT()
        {
            // Appending trajectory points to a committed recording extends its EndUT
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(5, 100), "Test Vessel");
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            var committed = RecordingStore.CommittedRecordings[0];
            double originalEndUT = committed.EndUT; // 140 (100 + 4*10)
            Assert.Equal(140, originalEndUT);

            // Simulate continuation: append points with later UTs
            committed.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 100,
                rotation = Quaternion.identity, velocity = Vector3.zero,
                bodyName = "Kerbin"
            });

            Assert.Equal(200, committed.EndUT);
            Assert.True(committed.EndUT > originalEndUT);

            // Chain EndUT also updates (it reads EndUT from the recording)
            committed.ChainId = "extend-test";
            committed.ChainIndex = 0;
            Assert.Equal(200, RecordingStore.GetChainEndUT(committed));
        }

        #endregion

        #region Chain loop helper

        [Fact]
        public void IsChainLooping_LoopEnabled_ReturnsTrue()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "Test1");
            Assert.NotNull(rec);
            rec.ChainId = "chain1";
            rec.ChainIndex = 0;
            rec.ChainBranch = 0;
            rec.PlaybackEnabled = true;
            rec.LoopPlayback = true;
            RecordingStore.CommitRecordingDirect(rec);

            Assert.True(RecordingStore.IsChainLooping("chain1"));
        }

        [Fact]
        public void IsChainLooping_DisabledLoopSegment_StillLooping()
        {
            // Bug #433 invariant: disabling a recording visually must not change
            // whether its chain is considered looping. The chain is a career-state
            // property (it determines whether the vessel spawns at tip).
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "DisabledLoop");
            Assert.NotNull(rec);
            rec.ChainId = "chain-disabled-loop";
            rec.ChainIndex = 0;
            rec.ChainBranch = 0;
            rec.PlaybackEnabled = false;
            rec.LoopPlayback = true;
            RecordingStore.CommitRecordingDirect(rec);

            Assert.True(RecordingStore.IsChainLooping("chain-disabled-loop"));
        }

        #endregion
    }
}
