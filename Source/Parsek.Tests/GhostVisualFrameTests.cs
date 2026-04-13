using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostVisualFrameTests
    {
        [Fact]
        public void ComputeSnapshotVisualRootLocalOffset_DebrisUsesNegativeSnapshotCom()
        {
            var snapshot = BuildSnapshot("1.25,-2.5,3.75");
            var traj = new MockTrajectory { IsDebris = true };

            Vector3 offset = GhostVisualBuilder.ComputeSnapshotVisualRootLocalOffset(traj, snapshot);

            AssertVector3Close(new Vector3(-1.25f, 2.5f, -3.75f), offset);
        }

        [Fact]
        public void ComputeSnapshotVisualRootLocalOffset_NonDebrisLeavesVisualsUnshifted()
        {
            var snapshot = BuildSnapshot("1.25,-2.5,3.75");
            var traj = new MockTrajectory { IsDebris = false };

            Vector3 offset = GhostVisualBuilder.ComputeSnapshotVisualRootLocalOffset(traj, snapshot);

            AssertVector3Close(Vector3.zero, offset);
        }

        [Fact]
        public void TryGetSnapshotRootPartInfo_UsesSnapshotRootIndex()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("root", "1");
            snapshot.AddValue("CoM", "0,0,0");

            var nonRoot = snapshot.AddNode("PART");
            nonRoot.AddValue("name", "probeCoreSphere");
            nonRoot.AddValue("persistentId", "10");
            nonRoot.AddValue("parent", "0");
            nonRoot.AddValue("position", "0,0,0");
            nonRoot.AddValue("rotation", "0,0,0,1");

            var root = snapshot.AddNode("PART");
            root.AddValue("name", "fuelTank");
            root.AddValue("persistentId", "42");
            root.AddValue("parent", "0");
            root.AddValue("position", "1,2,3");
            root.AddValue("rotation", "0.1,0.2,0.3,0.9");

            bool parsed = GhostVisualBuilder.TryGetSnapshotRootPartInfo(
                snapshot,
                out string partName,
                out uint persistentId,
                out Vector3 localPosition,
                out Quaternion localRotation);

            Assert.True(parsed);
            Assert.Equal("fuelTank", partName);
            Assert.Equal((uint)42, persistentId);
            AssertVector3Close(new Vector3(1f, 2f, 3f), localPosition);
            AssertQuaternionClose(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), localRotation);
        }

        [Fact]
        public void TryGetSnapshotCenterOfMass_MissingValue_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");

            bool parsed = GhostVisualBuilder.TryGetSnapshotCenterOfMass(snapshot, out Vector3 centerOfMass);

            Assert.False(parsed);
            AssertVector3Close(Vector3.zero, centerOfMass);
        }

        [Fact]
        public void DescribeAppearanceActiveSection_RelativeFrameIncludesAnchorPid()
        {
            var traj = new MockTrajectory
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 19.74,
                        endUT = 22.00,
                        anchorVesselId = 77
                    }
                }
            };

            string summary = GhostPlaybackEngine.DescribeAppearanceActiveSection(traj, 20.00);

            Assert.Contains("activeFrame=Relative", summary);
            Assert.Contains("sectionUT=19.74-22.00", summary);
            Assert.Contains("anchorPid=77", summary);
        }

        [Fact]
        public void DescribeAppearanceRecordingStartPoint_RelativeFrameLogsOffsetInsteadOfWorldPosition()
        {
            var traj = new MockTrajectory
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 19.76,
                        latitude = 1.25,
                        longitude = -2.5,
                        altitude = 3.75,
                        bodyName = "Kerbin"
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 19.74,
                        endUT = 22.00,
                        anchorVesselId = 88
                    }
                }
            };

            string summary = GhostPlaybackEngine.DescribeAppearanceRecordingStartPoint(
                traj, new Vector3d(10.0, 20.0, 30.0));

            Assert.Contains("recordingStart@19.76", summary);
            Assert.Contains("frame=Relative", summary);
            Assert.Contains("offset=(1.25,-2.50,3.75)", summary);
            Assert.Contains("anchorPid=88", summary);
            Assert.DoesNotContain("world=", summary);
        }

        [Fact]
        public void TryGetGhostActivationStartUT_PrefersFirstPlayableFrameOverExplicitStartAndSectionBoundary()
        {
            var rec = new Recording
            {
                ExplicitStartUT = 19.72,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 20.24,
                        endUT = 32.56,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 20.26, bodyName = "Kerbin" },
                            new TrajectoryPoint { ut = 20.68, bodyName = "Kerbin" }
                        }
                    }
                }
            };

            bool resolved = rec.TryGetGhostActivationStartUT(out double activationStartUT);

            Assert.True(resolved);
            Assert.Equal(20.26, activationStartUT, 2);
        }

        [Fact]
        public void ResolveGhostActivationStartUT_UsesRecordingActivationStartInsteadOfSemanticStart()
        {
            var rec = new Recording
            {
                ExplicitStartUT = 27.58,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 28.12, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 28.70, bodyName = "Kerbin" }
                }
            };

            double activationStartUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(rec);

            Assert.Equal(28.12, activationStartUT, 2);
        }

        [Fact]
        public void TryGetGhostActivationStartUT_UsesSeededBoundaryPointBeforeLaterOrbitSegment()
        {
            var rec = new Recording
            {
                ExplicitStartUT = 70.04,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 70.04, bodyName = "Kerbin" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 70.56, endUT = 122.60, bodyName = "Kerbin" }
                }
            };

            bool resolved = rec.TryGetGhostActivationStartUT(out double activationStartUT);

            Assert.True(resolved);
            Assert.Equal(70.04, activationStartUT, 2);
        }

        private static ConfigNode BuildSnapshot(string coM)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("root", "0");
            snapshot.AddValue("CoM", coM);

            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            part.AddValue("persistentId", "1");
            part.AddValue("parent", "0");
            part.AddValue("position", "0,0,0");
            part.AddValue("rotation", "0,0,0,1");

            return snapshot;
        }

        private static void AssertVector3Close(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
        {
            Assert.InRange(Mathf.Abs(expected.x - actual.x), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.y - actual.y), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.z - actual.z), 0f, epsilon);
        }

        private static void AssertQuaternionClose(Quaternion expected, Quaternion actual, float epsilon = 1e-4f)
        {
            Assert.InRange(Mathf.Abs(expected.x - actual.x), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.y - actual.y), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.z - actual.z), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.w - actual.w), 0f, epsilon);
        }
    }
}
