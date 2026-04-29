using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class OrbitSegmentTests : System.IDisposable
    {
        public OrbitSegmentTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private OrbitSegment MakeSegment(double startUT, double endUT, string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 28.5,
                eccentricity = 0.001,
                semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90,
                argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 1.23,
                epoch = startUT,
                bodyName = body
            };
        }

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0,
                    longitude = 0,
                    altitude = 100,
                    rotation = UnityEngine.Quaternion.identity,
                    velocity = UnityEngine.Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        #region FindOrbitSegment

        [Fact]
        public void FindOrbitSegment_EmptyList_ReturnsNull()
        {
            var segments = new List<OrbitSegment>();
            var result = TrajectoryMath.FindOrbitSegment(segments, 500);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_NullList_ReturnsNull()
        {
            var result = TrajectoryMath.FindOrbitSegment(null, 500);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_UTInRange_ReturnsSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 150);
            Assert.NotNull(result);
            Assert.Equal(100, result.Value.startUT);
            Assert.Equal(200, result.Value.endUT);
        }

        [Fact]
        public void FindOrbitSegment_UTBeforeRange_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 50);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_UTAfterRange_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 250);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_UTAtExactStart_ReturnsSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 100);
            Assert.NotNull(result);
        }

        [Fact]
        public void FindOrbitSegment_UTAtExactEnd_ReturnsSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 200);
            Assert.NotNull(result);
        }

        [Fact]
        public void FindOrbitSegment_MultipleSegments_FindsCorrectOne()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(300, 400, "Mun"),
                MakeSegment(500, 600, "Minmus")
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 350);
            Assert.NotNull(result);
            Assert.Equal("Mun", result.Value.bodyName);
        }

        [Fact]
        public void FindOrbitSegment_UTBetweenSegments_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200),
                MakeSegment(300, 400)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 250);
            Assert.Null(result);
        }

        // EvaluateOrbitSegmentAtUT — Phase 6 §7.5 / §7.7 shared helper.
        // The Kepler eval itself throws under headless xUnit
        // (Orbit.getPositionAtUT relies on KSP's body-orbit machinery),
        // so these tests focus on the guard paths AND the
        // partial-first / partial-last endpoint fallback semantics
        // (verified by capturing the bodyName the bodyResolver was
        // called with — the resolver intentionally returns null so
        // the helper short-circuits BEFORE reaching `Orbit`'s ctor,
        // letting xUnit observe the segment-selection decision
        // without the Kepler eval throwing).

        [Fact]
        public void EvaluateOrbitSegmentAtUT_NullCheckpoints_ReturnsNull()
        {
            Vector3d? result = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                checkpoints: null, ut: 150, bodyResolver: _ => null);
            Assert.Null(result);
        }

        [Fact]
        public void EvaluateOrbitSegmentAtUT_EmptyCheckpoints_ReturnsNull()
        {
            Vector3d? result = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                checkpoints: new List<OrbitSegment>(),
                ut: 150,
                bodyResolver: _ => null);
            Assert.Null(result);
        }

        [Fact]
        public void EvaluateOrbitSegmentAtUT_NullBodyResolver_ReturnsNull()
        {
            var checkpoints = new List<OrbitSegment> { MakeSegment(100, 200) };
            Vector3d? result = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                checkpoints, ut: 150, bodyResolver: null);
            Assert.Null(result);
        }

        [Fact]
        public void EvaluateOrbitSegmentAtUT_BodyResolverReturnsNull_ReturnsNull()
        {
            var checkpoints = new List<OrbitSegment> { MakeSegment(100, 200) };
            Vector3d? result = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                checkpoints, ut: 150, bodyResolver: _ => null);
            // Helper found the segment but couldn't resolve a real body
            // (xUnit can't instantiate one); fail-closed return.
            Assert.Null(result);
        }

        [Fact]
        public void EvaluateOrbitSegmentAtUT_PartialLastCheckpoint_FallsBackToLastSegmentByBodyName()
        {
            // P2-1 regression pin: §7.7 BubbleEntry candidate UT equals the
            // Checkpoint section's endUT; if the last sampled checkpoint's
            // endUT is a hair below that, FindOrbitSegment misses and the
            // helper must fall back to the LAST segment (where the candidate
            // logically lives), not return null.
            var checkpoints = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(200, 300, "Mun"),
            };
            string capturedBodyName = null;
            Vector3d? result = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                checkpoints,
                ut: 350,  // past the last segment's endUT
                bodyResolver: name => { capturedBodyName = name; return null; });

            // Body resolver returns null → helper short-circuits, but the
            // segment-selection decision is observable: the resolver was
            // called with the LAST segment's body name (the fallback target).
            Assert.Equal("Mun", capturedBodyName);
            Assert.Null(result);
        }

        [Fact]
        public void EvaluateOrbitSegmentAtUT_UTBeforeFirstCheckpoint_FallsBackToFirstSegmentByBodyName()
        {
            // Symmetric to the partial-last case: §7.5 / §7.7 BubbleExit
            // candidate UT equals the Checkpoint section's startUT; if the
            // first sampled checkpoint's startUT is a hair above that,
            // FindOrbitSegment misses and the helper must fall back to the
            // FIRST segment.
            var checkpoints = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(200, 300, "Mun"),
            };
            string capturedBodyName = null;
            Vector3d? result = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                checkpoints,
                ut: 50,  // before the first segment's startUT
                bodyResolver: name => { capturedBodyName = name; return null; });

            Assert.Equal("Kerbin", capturedBodyName);
            Assert.Null(result);
        }

        [Fact]
        public void EvaluateOrbitSegmentAtUT_UTInsideRange_PicksContainingSegmentByBodyName()
        {
            // Sanity: when FindOrbitSegment hits, the helper should NOT
            // engage the endpoint fallback. Pick the second segment by
            // putting its body name into the captured slot.
            var checkpoints = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(200, 300, "Mun"),
            };
            string capturedBodyName = null;
            Vector3d? result = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                checkpoints,
                ut: 250,  // inside the second segment
                bodyResolver: name => { capturedBodyName = name; return null; });

            Assert.Equal("Mun", capturedBodyName);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegmentForMapDisplay_UTInSameBodyGap_CarriesPreviousSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                new OrbitSegment
                {
                    startUT = 240,
                    endUT = 400,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 700000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 2.5,
                    epoch = 240,
                    bodyName = "Kerbin"
                }
            };

            var result = TrajectoryMath.FindOrbitSegmentForMapDisplay(segments, 220);

            Assert.NotNull(result);
            Assert.Equal(100, result.Value.startUT);
            Assert.Equal(200, result.Value.endUT);
            Assert.Equal("Kerbin", result.Value.bodyName);
        }

        [Fact]
        public void TryGetOrbitSegmentForMapDisplay_UTInSameBodyGap_MergesEquivalentWindowAcrossGap()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                new OrbitSegment
                {
                    startUT = 240,
                    endUT = 400,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 700000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 2.5,
                    epoch = 240,
                    bodyName = "Kerbin"
                },
                new OrbitSegment
                {
                    startUT = 430,
                    endUT = 600,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 700000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 3.1,
                    epoch = 430,
                    bodyName = "Kerbin"
                }
            };

            bool found = TrajectoryMath.TryGetOrbitSegmentForMapDisplay(
                segments, 220, out OrbitSegment segment, out double visibleStartUT, out double visibleEndUT);

            Assert.True(found);
            Assert.Equal(100, segment.startUT);
            Assert.Equal(100, visibleStartUT);
            Assert.Equal(600, visibleEndUT);
        }

        [Fact]
        public void TryGetOrbitSegmentForMapDisplay_UTInsideEquivalentSegmentChain_MergesPastAndFutureSegments()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                new OrbitSegment
                {
                    startUT = 240,
                    endUT = 400,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 700000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 2.5,
                    epoch = 240,
                    bodyName = "Kerbin"
                },
                new OrbitSegment
                {
                    startUT = 430,
                    endUT = 600,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 700000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 3.1,
                    epoch = 430,
                    bodyName = "Kerbin"
                }
            };

            bool found = TrajectoryMath.TryGetOrbitSegmentForMapDisplay(
                segments, 300, out OrbitSegment segment, out double visibleStartUT, out double visibleEndUT);

            Assert.True(found);
            Assert.Equal(240, segment.startUT);
            Assert.Equal(100, visibleStartUT);
            Assert.Equal(600, visibleEndUT);
        }

        [Fact]
        public void TryGetOrbitSegmentForMapDisplay_UTInsideEquivalentRun_StopsAtOrbitChangeBoundary()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                new OrbitSegment
                {
                    startUT = 240,
                    endUT = 400,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 700000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 2.5,
                    epoch = 240,
                    bodyName = "Kerbin"
                },
                new OrbitSegment
                {
                    startUT = 430,
                    endUT = 600,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 710000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 3.1,
                    epoch = 430,
                    bodyName = "Kerbin"
                }
            };

            bool found = TrajectoryMath.TryGetOrbitSegmentForMapDisplay(
                segments, 300, out OrbitSegment segment, out double visibleStartUT, out double visibleEndUT);

            Assert.True(found);
            Assert.Equal(240, segment.startUT);
            Assert.Equal(100, visibleStartUT);
            Assert.Equal(400, visibleEndUT);
        }

        [Fact]
        public void FindOrbitSegmentForMapDisplay_UTInSameBodyDifferentOrbitGap_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                new OrbitSegment
                {
                    startUT = 240,
                    endUT = 400,
                    inclination = 28.5,
                    eccentricity = 0.001,
                    semiMajorAxis = 710000,
                    longitudeOfAscendingNode = 90,
                    argumentOfPeriapsis = 45,
                    meanAnomalyAtEpoch = 2.5,
                    epoch = 240,
                    bodyName = "Kerbin"
                }
            };

            var result = TrajectoryMath.FindOrbitSegmentForMapDisplay(segments, 220);

            Assert.Null(result);
        }

        [Fact]
        public void AreOrbitSegmentsEquivalentForMapDisplay_IgnoresEpochAndMeanAnomaly()
        {
            var a = MakeSegment(100, 200, "Kerbin");
            var b = MakeSegment(240, 400, "Kerbin");
            b.meanAnomalyAtEpoch = 2.5;
            b.epoch = 240;

            Assert.True(TrajectoryMath.AreOrbitSegmentsEquivalentForMapDisplay(a, b));
        }

        [Fact]
        public void AreOrbitSegmentsEquivalentForMapDisplay_RejectsSameBodyOrbitChange()
        {
            var a = MakeSegment(100, 200, "Kerbin");
            var b = MakeSegment(240, 400, "Kerbin");
            b.semiMajorAxis = 710000;

            Assert.False(TrajectoryMath.AreOrbitSegmentsEquivalentForMapDisplay(a, b));
        }

        [Fact]
        public void FindOrbitSegmentForMapDisplay_UTInDifferentBodyGap_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(240, 400, "Mun")
            };

            var result = TrajectoryMath.FindOrbitSegmentForMapDisplay(segments, 220);

            Assert.Null(result);
        }

        [Fact]
        public void TryGetOrbitSegmentForMapDisplay_UTInDifferentBodyGap_ReturnsFalse()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(240, 400, "Mun")
            };

            bool found = TrajectoryMath.TryGetOrbitSegmentForMapDisplay(
                segments, 220, out OrbitSegment segment, out double visibleStartUT, out double visibleEndUT);

            Assert.False(found);
            Assert.Equal(default(OrbitSegment), segment);
            Assert.Equal(0, visibleStartUT);
            Assert.Equal(0, visibleEndUT);
        }

        [Fact]
        public void FindOrbitSegmentForMapDisplay_UTPastLastSegment_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(240, 400, "Kerbin")
            };

            var result = TrajectoryMath.FindOrbitSegmentForMapDisplay(segments, 450);

            Assert.Null(result);
        }

        #endregion

        #region OrbitSegment Serialization

        [Fact]
        public void FindOrbitSegment_AdjacentSegments_NoOverlap()
        {
            // Two segments sharing a boundary at ut=200
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(200, 300, "Mun")
            };

            // ut=200 is the exclusive end of first, inclusive start of second
            var result = TrajectoryMath.FindOrbitSegment(segments, 200);
            Assert.NotNull(result);
            Assert.Equal("Mun", result.Value.bodyName);
        }

        [Fact]
        public void FindOrbitSegment_LastSegment_InclusiveEnd()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(300, 400, "Mun")
            };

            // ut=400 at endUT of last segment — should match (inclusive)
            var result = TrajectoryMath.FindOrbitSegment(segments, 400);
            Assert.NotNull(result);
            Assert.Equal("Mun", result.Value.bodyName);
        }

        [Fact]
        public void FindOrbitSegment_NegativeUT_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, -50);
            Assert.Null(result);
        }

        [Fact]
        public void Recording_OnlyOrbitSegments_EmptyPoints()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(MakeSegment(500, 1000));

            Assert.Equal(500, rec.StartUT);
            Assert.Equal(1000, rec.EndUT);
        }

        [Fact]
        public void OrbitSegment_ToString_WhenPredicted_UsesKeyValueFormatting()
        {
            var seg = MakeSegment(100, 200);
            seg.isPredicted = true;

            string result = seg.ToString();

            Assert.Contains("predicted=true", result);
        }

        [Fact]
        public void FindOrbitSegment_SinglePointBoundary()
        {
            // startUT == endUT, a degenerate segment
            var segments = new List<OrbitSegment>
            {
                MakeSegment(150, 150)
            };

            // Last (only) segment uses inclusive end, so exact match works
            var result = TrajectoryMath.FindOrbitSegment(segments, 150);
            Assert.NotNull(result);
            Assert.Equal(150, result.Value.startUT);
        }

        #endregion

        #region Recording with OrbitSegments

        [Fact]
        public void Recording_OrbitSegments_InitializedEmpty()
        {
            var rec = new Recording();
            Assert.NotNull(rec.OrbitSegments);
            Assert.Empty(rec.OrbitSegments);
        }

        [Fact]
        public void Recording_WithOrbitSegments_PreservesStartEndUT()
        {
            var rec = new Recording();
            rec.Points = MakePoints(5, startUT: 100);
            rec.OrbitSegments.Add(MakeSegment(120, 130));

            // StartUT and EndUT come from trajectory points, not orbit segments
            Assert.Equal(100, rec.StartUT);
            Assert.Equal(140, rec.EndUT); // 100 + 4*10
        }

        [Fact]
        public void CreateRecordingFromFlightData_WithOrbitSegments_CopiesSegments()
        {
            var points = MakePoints(5);
            var segments = new List<OrbitSegment>
            {
                MakeSegment(110, 120),
                MakeSegment(130, 140)
            };

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "TestVessel", segments);

            Assert.NotNull(rec);
            Assert.Equal(2, rec.OrbitSegments.Count);
            Assert.Equal(110, rec.OrbitSegments[0].startUT);
            Assert.Equal(130, rec.OrbitSegments[1].startUT);
        }

        [Fact]
        public void CreateRecordingFromFlightData_WithNullSegments_InitializesEmptyList()
        {
            var points = MakePoints(5);

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "TestVessel", null);

            Assert.NotNull(rec);
            Assert.NotNull(rec.OrbitSegments);
            Assert.Empty(rec.OrbitSegments);
        }

        [Fact]
        public void CreateRecordingFromFlightData_WithoutSegmentsParam_InitializesEmptyList()
        {
            var points = MakePoints(5);

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "TestVessel");

            Assert.NotNull(rec);
            Assert.NotNull(rec.OrbitSegments);
            Assert.Empty(rec.OrbitSegments);
        }

        [Fact]
        public void CommitRecordingDirect_WithOrbitSegments_PreservesSegments()
        {
            var points = MakePoints(5);
            var segments = new List<OrbitSegment>
            {
                MakeSegment(110, 120, "Mun")
            };

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "Ship", segments);
            Assert.NotNull(rec);
            RecordingStore.CommitRecordingDirect(rec);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(RecordingStore.CommittedRecordings[0].OrbitSegments);
            Assert.Equal("Mun", RecordingStore.CommittedRecordings[0].OrbitSegments[0].bodyName);
        }

        #endregion

        #region HasOrbitalFrameRotation

        [Fact]
        public void HasOrbitalFrameRotation_DefaultSegment_ReturnsFalse()
        {
            var seg = MakeSegment(100, 200);
            Assert.False(TrajectoryMath.HasOrbitalFrameRotation(seg));
        }

        [Fact]
        public void HasOrbitalFrameRotation_WithRotation_ReturnsTrue()
        {
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(0, 1, 0, 0);
            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(seg));
        }

        [Fact]
        public void HasOrbitalFrameRotation_IdentityQuaternion_ReturnsTrue()
        {
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(0, 0, 0, 1);
            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(seg));
        }

        [Fact]
        public void HasOrbitalFrameRotation_AllZero_ReturnsFalse()
        {
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(0, 0, 0, 0);
            Assert.False(TrajectoryMath.HasOrbitalFrameRotation(seg));
        }

        [Fact]
        public void HasOrbitalFrameRotation_NegativeComponents_ReturnsTrue()
        {
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(-0.5f, 0, 0, 0.866f);
            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(seg));
        }

        #endregion

        #region IsSpinning

        [Fact]
        public void IsSpinning_DefaultSegment_ReturnsFalse()
        {
            var seg = MakeSegment(100, 200);
            Assert.False(TrajectoryMath.IsSpinning(seg));
        }

        [Fact]
        public void IsSpinning_AboveThreshold_ReturnsTrue()
        {
            var seg = MakeSegment(100, 200);
            seg.angularVelocity = new Vector3(0.1f, 0, 0);
            Assert.True(TrajectoryMath.IsSpinning(seg));
        }

        [Fact]
        public void IsSpinning_BelowThreshold_ReturnsFalse()
        {
            var seg = MakeSegment(100, 200);
            seg.angularVelocity = new Vector3(0.01f, 0, 0);
            Assert.False(TrajectoryMath.IsSpinning(seg));
        }

        #endregion

        #region ComputeOrbitalFrameRotation

        [Fact]
        public void ComputeOrbitalFrameRotation_Prograde_ReturnsIdentity()
        {
            // Vessel facing prograde: worldRot = PureLookRotation(velocity, radialOut)
            var velocity = new Vector3d(0, 0, 100);
            var radialOut = new Vector3d(0, 1, 0);
            Quaternion worldRot = TrajectoryMath.PureLookRotation((Vector3)velocity, (Vector3)radialOut);

            Quaternion result = TrajectoryMath.ComputeOrbitalFrameRotation(worldRot, velocity, radialOut);

            // Should be identity (within tolerance)
            Assert.True(Mathf.Abs(result.x) < 0.001f, $"x={result.x}");
            Assert.True(Mathf.Abs(result.y) < 0.001f, $"y={result.y}");
            Assert.True(Mathf.Abs(result.z) < 0.001f, $"z={result.z}");
            Assert.True(Mathf.Abs(result.w - 1f) < 0.001f, $"w={result.w}");
        }

        [Fact]
        public void ComputeOrbitalFrameRotation_Retrograde_Returns180Yaw()
        {
            // Vessel facing retrograde (negative velocity direction)
            var velocity = new Vector3d(0, 0, 100);
            var radialOut = new Vector3d(0, 1, 0);
            Quaternion worldRot = TrajectoryMath.PureLookRotation(-(Vector3)velocity, (Vector3)radialOut);

            Quaternion result = TrajectoryMath.ComputeOrbitalFrameRotation(worldRot, velocity, radialOut);

            // Applying result to forward should give -forward (retrograde)
            Vector3 forward = TrajectoryMath.PureRotateVector(result, Vector3.forward);
            Assert.True(Mathf.Abs(forward.x - 0f) < 0.01f, $"forward.x={forward.x}");
            Assert.True(Mathf.Abs(forward.y - 0f) < 0.01f, $"forward.y={forward.y}");
            Assert.True(Mathf.Abs(forward.z - (-1f)) < 0.01f, $"forward.z={forward.z}");
        }

        [Fact]
        public void ComputeOrbitalFrameRotation_ZeroVelocity_ReturnsIdentity()
        {
            var velocity = new Vector3d(0, 0, 0);
            var radialOut = new Vector3d(0, 1, 0);
            Quaternion worldRot = Quaternion.identity;

            Quaternion result = TrajectoryMath.ComputeOrbitalFrameRotation(worldRot, velocity, radialOut);

            Assert.True(Mathf.Abs(result.x) < 0.001f, $"x={result.x}");
            Assert.True(Mathf.Abs(result.y) < 0.001f, $"y={result.y}");
            Assert.True(Mathf.Abs(result.z) < 0.001f, $"z={result.z}");
            Assert.True(Mathf.Abs(result.w - 1f) < 0.001f, $"w={result.w}");
        }

        [Fact]
        public void ComputeOrbitalFrameRotation_NearParallelVectors_NoNaN()
        {
            // Velocity nearly parallel to radialOut (dot > 0.99)
            var velocity = new Vector3d(0, 1, 0.01);
            var radialOut = new Vector3d(0, 1, 0);
            Quaternion worldRot = Quaternion.identity;

            Quaternion result = TrajectoryMath.ComputeOrbitalFrameRotation(worldRot, velocity, radialOut);

            Assert.False(float.IsNaN(result.x), "x is NaN");
            Assert.False(float.IsNaN(result.y), "y is NaN");
            Assert.False(float.IsNaN(result.z), "z is NaN");
            Assert.False(float.IsNaN(result.w), "w is NaN");
        }

        [Fact]
        public void ComputeOrbitalFrameRotation_RoundTrip()
        {
            // Arbitrary world rotation (30,45,60 Euler via pure math)
            var velocity = new Vector3d(100, 0, 50);
            var radialOut = new Vector3d(0, 1, 0);
            Quaternion worldRot = TrajectoryMath.PureMultiply(
                TrajectoryMath.PureMultiply(
                    TrajectoryMath.PureAngleAxis(45, Vector3.up),
                    TrajectoryMath.PureAngleAxis(30, Vector3.right)),
                TrajectoryMath.PureAngleAxis(60, Vector3.forward));

            // Encode
            Quaternion stored = TrajectoryMath.ComputeOrbitalFrameRotation(worldRot, velocity, radialOut);

            // Decode: orbFrame * storedRot should recover worldRot
            Quaternion orbFrame = TrajectoryMath.PureLookRotation(
                ((Vector3)velocity).normalized, (Vector3)radialOut);
            Quaternion reconstructed = TrajectoryMath.PureMultiply(orbFrame, stored);

            // Quaternion sign ambiguity: q and -q represent the same rotation.
            // Flip reconstructed if dot product with worldRot is negative.
            float dot = reconstructed.x * worldRot.x + reconstructed.y * worldRot.y +
                        reconstructed.z * worldRot.z + reconstructed.w * worldRot.w;
            if (dot < 0)
                reconstructed = new Quaternion(-reconstructed.x, -reconstructed.y,
                                               -reconstructed.z, -reconstructed.w);

            // Compare component-wise (tolerance for float precision)
            float tolerance = 0.002f;
            Assert.True(Mathf.Abs(reconstructed.x - worldRot.x) < tolerance,
                $"x: reconstructed={reconstructed.x} original={worldRot.x}");
            Assert.True(Mathf.Abs(reconstructed.y - worldRot.y) < tolerance,
                $"y: reconstructed={reconstructed.y} original={worldRot.y}");
            Assert.True(Mathf.Abs(reconstructed.z - worldRot.z) < tolerance,
                $"z: reconstructed={reconstructed.z} original={worldRot.z}");
            Assert.True(Mathf.Abs(reconstructed.w - worldRot.w) < tolerance,
                $"w: reconstructed={reconstructed.w} original={worldRot.w}");
        }

        #endregion

        #region SpinForward

        [Fact]
        public void SpinForward_SingleAxis_CorrectAngle()
        {
            // Angular velocity around Y axis: 0.5 rad/s, dt = 2s
            // Expected angle: 0.5 * 2 * Rad2Deg = ~57.296 degrees
            Vector3 angVel = new Vector3(0, 0.5f, 0);
            float dt = 2f;
            Quaternion boundaryWorldRot = Quaternion.identity;

            // With identity boundary, worldAxis = angVel
            Vector3 worldAxis = TrajectoryMath.PureRotateVector(boundaryWorldRot, angVel);
            float angle = angVel.magnitude * dt * Mathf.Rad2Deg;
            Quaternion spunRot = TrajectoryMath.PureMultiply(
                TrajectoryMath.PureAngleAxis(angle, worldAxis), boundaryWorldRot);

            // For a pure Y rotation of ~57.3 degrees, verify via rotating Vector3.forward
            // The forward vector should rotate in the XZ plane by ~57.3 degrees around Y
            Vector3 fwd = TrajectoryMath.PureRotateVector(spunRot, Vector3.forward);
            float expectedAngleRad = 0.5f * 2f; // 1 radian
            float expectedX = Mathf.Sin(expectedAngleRad);
            float expectedZ = Mathf.Cos(expectedAngleRad);
            Assert.True(Mathf.Abs(fwd.x - expectedX) < 0.01f,
                $"fwd.x={fwd.x} expected~={expectedX}");
            Assert.True(Mathf.Abs(fwd.z - expectedZ) < 0.01f,
                $"fwd.z={fwd.z} expected~={expectedZ}");
            Assert.True(Mathf.Abs(fwd.y) < 0.01f, $"fwd.y={fwd.y} expected~=0");
        }

        [Fact]
        public void SpinForward_ZeroAngVel_FallsBackToOrbitalFrame()
        {
            // Segment with orbital-frame rotation but zero angular velocity
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(0, 0, 0, 1); // identity = prograde
            seg.angularVelocity = Vector3.zero;

            // IsSpinning should be false
            Assert.False(TrajectoryMath.IsSpinning(seg));
            // HasOrbitalFrameRotation should be true (w=1)
            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(seg));
        }

        #endregion

        #region Serialization Round-trips

        private Recording RoundTripSerialize(Recording rec)
        {
            var node = new ConfigNode("TRAJECTORY");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var result = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, result);
            return result;
        }

        [Fact]
        public void Serialization_RoundTrip_PreservesOrbitalFrameRotation()
        {
            var rec = new Recording();
            rec.Points = MakePoints(2);
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
            rec.OrbitSegments.Add(seg);

            var result = RoundTripSerialize(rec);

            Assert.Single(result.OrbitSegments);
            var resSeg = result.OrbitSegments[0];
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.x - 0.1f) < 0.0001f,
                $"x={resSeg.orbitalFrameRotation.x}");
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.y - 0.2f) < 0.0001f,
                $"y={resSeg.orbitalFrameRotation.y}");
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.z - 0.3f) < 0.0001f,
                $"z={resSeg.orbitalFrameRotation.z}");
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.w - 0.9f) < 0.0001f,
                $"w={resSeg.orbitalFrameRotation.w}");
        }

        [Fact]
        public void Serialization_RoundTrip_PreservesAngularVelocity()
        {
            var rec = new Recording();
            rec.Points = MakePoints(2);
            var seg = MakeSegment(100, 200);
            seg.angularVelocity = new Vector3(0.1f, 0.2f, 0.3f);
            rec.OrbitSegments.Add(seg);

            var result = RoundTripSerialize(rec);

            Assert.Single(result.OrbitSegments);
            var resSeg = result.OrbitSegments[0];
            Assert.True(Mathf.Abs(resSeg.angularVelocity.x - 0.1f) < 0.0001f,
                $"x={resSeg.angularVelocity.x}");
            Assert.True(Mathf.Abs(resSeg.angularVelocity.y - 0.2f) < 0.0001f,
                $"y={resSeg.angularVelocity.y}");
            Assert.True(Mathf.Abs(resSeg.angularVelocity.z - 0.3f) < 0.0001f,
                $"z={resSeg.angularVelocity.z}");
        }

        [Fact]
        public void Serialization_MissingOfrKeys_DefaultsToZero()
        {
            // Build a segment without ofr keys (simulating old recording)
            var rec = new Recording();
            rec.Points = MakePoints(2);
            var seg = MakeSegment(100, 200);
            // orbitalFrameRotation stays default (0,0,0,0) — won't be serialized
            rec.OrbitSegments.Add(seg);

            var result = RoundTripSerialize(rec);

            Assert.Single(result.OrbitSegments);
            var resSeg = result.OrbitSegments[0];
            Assert.Equal(0f, resSeg.orbitalFrameRotation.x);
            Assert.Equal(0f, resSeg.orbitalFrameRotation.y);
            Assert.Equal(0f, resSeg.orbitalFrameRotation.z);
            Assert.Equal(0f, resSeg.orbitalFrameRotation.w);
            Assert.False(TrajectoryMath.HasOrbitalFrameRotation(resSeg));
        }

        [Fact]
        public void Serialization_MissingAvKeys_DefaultsToZero()
        {
            // Build a segment without av keys (simulating no PersistentRotation)
            var rec = new Recording();
            rec.Points = MakePoints(2);
            var seg = MakeSegment(100, 200);
            // angularVelocity stays default (0,0,0) — won't be serialized
            rec.OrbitSegments.Add(seg);

            var result = RoundTripSerialize(rec);

            Assert.Single(result.OrbitSegments);
            var resSeg = result.OrbitSegments[0];
            Assert.Equal(0f, resSeg.angularVelocity.x);
            Assert.Equal(0f, resSeg.angularVelocity.y);
            Assert.Equal(0f, resSeg.angularVelocity.z);
            Assert.False(TrajectoryMath.IsSpinning(resSeg));
        }

        [Fact]
        public void Serialization_WithOfrKeys_ParsesCorrectly()
        {
            // Manually build a ConfigNode with specific ofrX/Y/Z/W values
            var node = new ConfigNode("TRAJECTORY");
            var segNode = node.AddNode("ORBIT_SEGMENT");
            segNode.AddValue("startUT", "100");
            segNode.AddValue("endUT", "200");
            segNode.AddValue("inc", "28.5");
            segNode.AddValue("ecc", "0.001");
            segNode.AddValue("sma", "700000");
            segNode.AddValue("lan", "90");
            segNode.AddValue("argPe", "45");
            segNode.AddValue("mna", "1.23");
            segNode.AddValue("epoch", "100");
            segNode.AddValue("body", "Kerbin");
            segNode.AddValue("ofrX", "0.123");
            segNode.AddValue("ofrY", "0.456");
            segNode.AddValue("ofrZ", "0.789");
            segNode.AddValue("ofrW", "0.321");

            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.OrbitSegments);
            var seg = rec.OrbitSegments[0];
            Assert.True(Mathf.Abs(seg.orbitalFrameRotation.x - 0.123f) < 0.001f,
                $"x={seg.orbitalFrameRotation.x}");
            Assert.True(Mathf.Abs(seg.orbitalFrameRotation.y - 0.456f) < 0.001f,
                $"y={seg.orbitalFrameRotation.y}");
            Assert.True(Mathf.Abs(seg.orbitalFrameRotation.z - 0.789f) < 0.001f,
                $"z={seg.orbitalFrameRotation.z}");
            Assert.True(Mathf.Abs(seg.orbitalFrameRotation.w - 0.321f) < 0.001f,
                $"w={seg.orbitalFrameRotation.w}");
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void SpinForward_HighAngularVelocity_NoOverflow()
        {
            // Very high angular velocity: 5 rad/s, dt = 1000s
            Vector3 angVel = new Vector3(5f, 0, 0);
            float dt = 1000f;
            Quaternion boundaryWorldRot = Quaternion.identity;

            Vector3 worldAxis = TrajectoryMath.PureRotateVector(boundaryWorldRot, angVel);
            float angle = angVel.magnitude * dt * Mathf.Rad2Deg;
            Quaternion spunRot = TrajectoryMath.PureMultiply(
                TrajectoryMath.PureAngleAxis(angle, worldAxis), boundaryWorldRot);

            // Must produce a valid quaternion: no NaN, no overflow
            Assert.False(float.IsNaN(spunRot.x), "x is NaN");
            Assert.False(float.IsNaN(spunRot.y), "y is NaN");
            Assert.False(float.IsNaN(spunRot.z), "z is NaN");
            Assert.False(float.IsNaN(spunRot.w), "w is NaN");
            Assert.False(float.IsInfinity(spunRot.x), "x is Infinity");
            Assert.False(float.IsInfinity(spunRot.y), "y is Infinity");
            Assert.False(float.IsInfinity(spunRot.z), "z is Infinity");
            Assert.False(float.IsInfinity(spunRot.w), "w is Infinity");
        }

        [Fact]
        public void ComputeOrbitalFrameRotation_OutputIsUnitQuaternion()
        {
            // Various input rotations — output must always be unit magnitude
            var inputs = new[]
            {
                (vel: new Vector3d(100, 0, 50), rad: new Vector3d(0, 1, 0),
                 rot: TrajectoryMath.PureAngleAxis(45, Vector3.up)),
                (vel: new Vector3d(-30, 10, 80), rad: new Vector3d(0.1, 0.99, 0),
                 rot: TrajectoryMath.PureAngleAxis(130, Vector3.right)),
                (vel: new Vector3d(0, 0, 1), rad: new Vector3d(0, 1, 0),
                 rot: TrajectoryMath.PureLookRotation(new Vector3(0, 0, -1), Vector3.up)), // retrograde
            };

            foreach (var (vel, rad, rot) in inputs)
            {
                Quaternion result = TrajectoryMath.ComputeOrbitalFrameRotation(rot, vel, rad);
                float mag = Mathf.Sqrt(result.x * result.x + result.y * result.y +
                                       result.z * result.z + result.w * result.w);
                Assert.True(Mathf.Abs(mag - 1f) < 0.01f,
                    $"Non-unit quaternion: mag={mag}, result={result}");
            }
        }

        [Fact]
        public void Serialization_RoundTrip_NegativeComponents()
        {
            // Real in-game data had negative quaternion components
            var rec = new Recording();
            rec.Points = MakePoints(2);
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(-0.6f, -0.3f, 0.6f, 0.3f);
            rec.OrbitSegments.Add(seg);

            var result = RoundTripSerialize(rec);

            var resSeg = result.OrbitSegments[0];
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.x - (-0.6f)) < 0.0001f,
                $"x={resSeg.orbitalFrameRotation.x}");
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.y - (-0.3f)) < 0.0001f,
                $"y={resSeg.orbitalFrameRotation.y}");
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.z - 0.6f) < 0.0001f,
                $"z={resSeg.orbitalFrameRotation.z}");
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.w - 0.3f) < 0.0001f,
                $"w={resSeg.orbitalFrameRotation.w}");
        }

        [Fact]
        public void Serialization_RoundTrip_CombinedOfrAndAngVel()
        {
            // Both orbital-frame rotation and angular velocity on the same segment
            var rec = new Recording();
            rec.Points = MakePoints(2);
            var seg = MakeSegment(100, 200);
            seg.orbitalFrameRotation = new Quaternion(0.1f, 0.7f, -0.1f, -0.7f);
            seg.angularVelocity = new Vector3(0.3f, -0.1f, 0.5f);
            rec.OrbitSegments.Add(seg);

            var result = RoundTripSerialize(rec);

            var resSeg = result.OrbitSegments[0];
            Assert.True(TrajectoryMath.HasOrbitalFrameRotation(resSeg));
            Assert.True(TrajectoryMath.IsSpinning(resSeg));
            Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.y - 0.7f) < 0.0001f);
            Assert.True(Mathf.Abs(resSeg.angularVelocity.z - 0.5f) < 0.0001f);
        }

        [Fact]
        public void Serialization_RoundTrip_MultipleSegmentsDifferentOFR()
        {
            // Simulates real in-game session: 6 orbit segments with different orientations
            var rec = new Recording();
            rec.Points = MakePoints(2);

            var rotations = new[]
            {
                new Quaternion(0.7f, 0.1f, -0.7f, -0.1f),
                new Quaternion(-0.6f, -0.3f, 0.6f, 0.3f),
                new Quaternion(-0.3f, 0.7f, 0.3f, -0.6f),
                new Quaternion(-0.2f, 0.7f, 0.2f, -0.7f),
                new Quaternion(0.1f, 0.7f, -0.1f, -0.7f),
                new Quaternion(0.1f, 0.7f, -0.1f, -0.7f),
            };

            for (int i = 0; i < rotations.Length; i++)
            {
                var seg = MakeSegment(100 + i * 100, 190 + i * 100);
                seg.orbitalFrameRotation = rotations[i];
                rec.OrbitSegments.Add(seg);
            }

            var result = RoundTripSerialize(rec);

            Assert.Equal(6, result.OrbitSegments.Count);
            for (int i = 0; i < rotations.Length; i++)
            {
                var resSeg = result.OrbitSegments[i];
                Assert.True(TrajectoryMath.HasOrbitalFrameRotation(resSeg),
                    $"Segment {i} lost OFR data");
                Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.x - rotations[i].x) < 0.0001f,
                    $"Segment {i} x: {resSeg.orbitalFrameRotation.x} != {rotations[i].x}");
                Assert.True(Mathf.Abs(resSeg.orbitalFrameRotation.y - rotations[i].y) < 0.0001f,
                    $"Segment {i} y: {resSeg.orbitalFrameRotation.y} != {rotations[i].y}");
            }
        }

        #endregion

        #region Log Assertions

        [Fact]
        public void ComputeOrbitalFrameRotation_ZeroVelocity_LogsDegenerate()
        {
            var lines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;

            try
            {
                var velocity = new Vector3d(0, 0, 0);
                var radialOut = new Vector3d(0, 1, 0);
                TrajectoryMath.ComputeOrbitalFrameRotation(Quaternion.identity, velocity, radialOut);

                Assert.Contains(lines, l => l.Contains("degenerate velocity"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void ComputeOrbitalFrameRotation_NearParallel_LogsWarning()
        {
            var lines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            try
            {
                // Velocity nearly parallel to radialOut
                var velocity = new Vector3d(0, 1, 0.01);
                var radialOut = new Vector3d(0, 1, 0);
                TrajectoryMath.ComputeOrbitalFrameRotation(Quaternion.identity, velocity, radialOut);

                Assert.Contains(lines, l => l.Contains("near-parallel"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        #endregion

        #region IsSurfaceAtUT

        [Fact]
        public void IsSurfaceAtUT_SurfaceMobile_ReturnsTrue()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { environment = SegmentEnvironment.SurfaceMobile, startUT = 100, endUT = 200 }
            };
            Assert.True(TrajectoryMath.IsSurfaceAtUT(sections, 150));
        }

        [Fact]
        public void IsSurfaceAtUT_SurfaceStationary_ReturnsTrue()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { environment = SegmentEnvironment.SurfaceStationary, startUT = 100, endUT = 200 }
            };
            Assert.True(TrajectoryMath.IsSurfaceAtUT(sections, 150));
        }

        [Fact]
        public void IsSurfaceAtUT_Atmospheric_ReturnsFalse()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 }
            };
            Assert.False(TrajectoryMath.IsSurfaceAtUT(sections, 150));
        }

        [Fact]
        public void IsSurfaceAtUT_ExoBallistic_ReturnsFalse()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { environment = SegmentEnvironment.ExoBallistic, startUT = 100, endUT = 200 }
            };
            Assert.False(TrajectoryMath.IsSurfaceAtUT(sections, 150));
        }

        [Fact]
        public void IsSurfaceAtUT_NullSections_ReturnsFalse()
        {
            Assert.False(TrajectoryMath.IsSurfaceAtUT(null, 150));
        }

        [Fact]
        public void IsSurfaceAtUT_EmptySections_ReturnsFalse()
        {
            Assert.False(TrajectoryMath.IsSurfaceAtUT(new List<TrackSection>(), 150));
        }

        [Fact]
        public void IsSurfaceAtUT_UTOutsideRange_ReturnsFalse()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { environment = SegmentEnvironment.SurfaceMobile, startUT = 100, endUT = 200 }
            };
            Assert.False(TrajectoryMath.IsSurfaceAtUT(sections, 300));
        }

        [Fact]
        public void IsSurfaceAtUT_MixedSections_CorrectlyIdentifiesSurface()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 },
                new TrackSection { environment = SegmentEnvironment.SurfaceMobile, startUT = 200, endUT = 300 },
                new TrackSection { environment = SegmentEnvironment.ExoBallistic, startUT = 300, endUT = 400 }
            };
            Assert.False(TrajectoryMath.IsSurfaceAtUT(sections, 150));
            Assert.True(TrajectoryMath.IsSurfaceAtUT(sections, 250));
            Assert.False(TrajectoryMath.IsSurfaceAtUT(sections, 350));
        }

        #endregion
    }
}
