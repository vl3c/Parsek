using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class EvaBoundaryMergeRoundTripTests : IDisposable
    {
        private readonly string tempDir;

        public EvaBoundaryMergeRoundTripTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-eva-boundary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }


        // Bug #371: regression test for PR #248 NormalizeContinuousEvaBoundaryMerge.
        //
        // PR #248 added boundary-frame trimming to MergeInto continuous-EVA path. The merged
        // TrackSections are the authoritative payload on disk for RecordingFormatVersion >= 1
        // (PR #230). This test proves that after the optimizer boundary-merge normalizer runs
        // on loaded recordings, saving and reloading the target yields byte-identical Points
        // and OrbitSegments -- i.e. the trimmed sections serialize and round-trip cleanly.
        //
        // Flow:
        //   1. Build two EVA-tagged section-authoritative recordings (atmo + surface, Kerbin).
        //   2. Save both as binary v3 sidecars.
        //   3. Load both back.
        //   4. Call RecordingOptimizer.MergeInto(target=atmo, absorbed=surface), which triggers
        //      CanMergeContinuousEvaAtmoSurfaceBoundary -> NormalizeContinuousEvaBoundaryMerge
        //      -> TrimOverlappingSectionFrames -> TrySyncFlatTrajectoryFromTrackSections.
        //   5. Deep-copy the post-merge Points / OrbitSegments.
        //   6. Save the merged target, load it again.
        //   7. Assert the second-load Points / OrbitSegments exactly match the snapshot from (5).
        [Fact]
        public void MergeInto_ContinuousEvaBoundary_V3Roundtrip_PointsAndOrbitSegmentsStable()
        {
            const double t0 = 21000.0;
            const uint pid = 7301u;
            const string crewName = "Bill Kerman";
            const string parentId = "eva-parent-371";

            var atmoBoundary = new TrajectoryPoint
            {
                ut = t0 + 10,
                latitude = -0.0940,
                longitude = -74.5512,
                altitude = 400.0,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, -35f, 0),
                bodyName = "Kerbin"
            };

            var surfaceBoundary = new TrajectoryPoint
            {
                ut = t0 + 10,
                latitude = -0.0940,
                longitude = -74.5512,
                altitude = 400.0,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 0f, 0),
                bodyName = "Kerbin"
            };

            var target = BuildEvaSectionAuthoritativeRecording(
                recordingId: "eva-371-atmo",
                phase: "atmo",
                env: SegmentEnvironment.Atmospheric,
                startUT: t0,
                midUT: t0 + 5,
                endUT: t0 + 10,
                boundaryPoint: atmoBoundary,
                crewName: crewName,
                parentId: parentId,
                pid: pid,
                chainIndex: 0);

            var absorbed = BuildEvaSectionAuthoritativeRecording(
                recordingId: "eva-371-surface",
                phase: "surface",
                env: SegmentEnvironment.SurfaceMobile,
                startUT: t0 + 10,
                midUT: t0 + 13,
                endUT: t0 + 20,
                boundaryPoint: surfaceBoundary,
                crewName: crewName,
                parentId: parentId,
                pid: pid,
                chainIndex: 1);

            // Sanity check: CanMergeContinuousEvaAtmoSurfaceBoundary must return true --
            // otherwise MergeInto would not trigger NormalizeContinuousEvaBoundaryMerge.
            Assert.True(RecordingOptimizer.CanMergeContinuousEvaAtmoSurfaceBoundary(target, absorbed),
                "Test fixture must trigger the continuous-EVA boundary merge normalizer.");

            string targetPath = Path.Combine(tempDir, "eva-371-atmo.prec");
            string absorbedPath = Path.Combine(tempDir, "eva-371-surface.prec");

            RecordingStore.WriteTrajectorySidecar(targetPath, target, sidecarEpoch: 1);
            RecordingStore.WriteTrajectorySidecar(absorbedPath, absorbed, sidecarEpoch: 1);


            TrajectorySidecarProbe targetProbe;
            TrajectorySidecarProbe absorbedProbe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(targetPath, out targetProbe));
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(absorbedPath, out absorbedProbe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, targetProbe.Encoding);
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, absorbedProbe.Encoding);

            var loadedTarget = new Recording
            {
                RecordingId = target.RecordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                EvaCrewName = crewName,
                ParentRecordingId = parentId,
                VesselPersistentId = pid,
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                ChainId = "eva-chain-371",
                ChainIndex = 0
            };
            var loadedAbsorbed = new Recording
            {
                RecordingId = absorbed.RecordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                EvaCrewName = crewName,
                ParentRecordingId = parentId,
                VesselPersistentId = pid,
                SegmentPhase = "surface",
                SegmentBodyName = "Kerbin",
                ChainId = "eva-chain-371",
                ChainIndex = 1
            };
            RecordingStore.DeserializeTrajectorySidecar(targetPath, targetProbe, loadedTarget);
            RecordingStore.DeserializeTrajectorySidecar(absorbedPath, absorbedProbe, loadedAbsorbed);

            // Sanity: sections survive round-trip, duplicate boundary frame still present.
            Assert.Single(loadedTarget.TrackSections);
            Assert.Single(loadedAbsorbed.TrackSections);
            Assert.Equal(3, loadedTarget.TrackSections[0].frames.Count);
            Assert.Equal(3, loadedAbsorbed.TrackSections[0].frames.Count);
            Assert.Equal(atmoBoundary.ut, loadedTarget.TrackSections[0].frames[2].ut);
            Assert.Equal(surfaceBoundary.ut, loadedAbsorbed.TrackSections[0].frames[0].ut);

            // Trigger the code path PR #248 added.
            RecordingOptimizer.MergeInto(loadedTarget, loadedAbsorbed);

            // Deep-copy post-merge trajectory state so a subsequent round-trip cannot alias it.
            var mergedPointsSnapshot = DeepCopyPoints(loadedTarget.Points);
            var mergedOrbitSegmentsSnapshot = DeepCopyOrbitSegments(loadedTarget.OrbitSegments);

            string mergedPath = Path.Combine(tempDir, "eva-371-merged.prec");
            RecordingStore.WriteTrajectorySidecar(mergedPath, loadedTarget, sidecarEpoch: 2);

            TrajectorySidecarProbe mergedProbe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(mergedPath, out mergedProbe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, mergedProbe.Encoding);

            var reloadedMerged = new Recording
            {
                RecordingId = loadedTarget.RecordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                EvaCrewName = crewName,
                ParentRecordingId = parentId,
                VesselPersistentId = pid,
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin"
            };
            RecordingStore.DeserializeTrajectorySidecar(mergedPath, mergedProbe, reloadedMerged);

            Assert.Equal(mergedPointsSnapshot.Count, reloadedMerged.Points.Count);
            for (int i = 0; i < mergedPointsSnapshot.Count; i++)
                AssertPointEqual(mergedPointsSnapshot[i], reloadedMerged.Points[i]);

            Assert.Equal(mergedOrbitSegmentsSnapshot.Count, reloadedMerged.OrbitSegments.Count);
            for (int i = 0; i < mergedOrbitSegmentsSnapshot.Count; i++)
                AssertOrbitSegmentEqual(mergedOrbitSegmentsSnapshot[i], reloadedMerged.OrbitSegments[i]);

            // Before TrimOverlappingSectionFrames: 3 atmo frames + 3 surface frames = 6 total,
            // with the surface section first frame duplicating the atmo section last frame.
            // After trimming the duplicate, the total is 5.
            int totalSectionFrames = 0;
            for (int i = 0; i < reloadedMerged.TrackSections.Count; i++)
                totalSectionFrames += reloadedMerged.TrackSections[i].frames.Count;
            Assert.Equal(5, totalSectionFrames);
            Assert.Equal(5, reloadedMerged.Points.Count);
        }


        // Bug #371 companion: the todo entry asks whether a continuous-EVA recording that
        // crosses an orbital-checkpoint boundary is even possible.
        // CanMergeContinuousEvaAtmoSurfaceBoundary requires IsAtmoSurfacePhasePair -- i.e.
        // both sides are "atmo" or "surface". Orbital segments use phase "exo", so the
        // normalizer OrbitalCheckpoint-section path is unreachable via this flow: the
        // optimizer rejects the merge before TrimOverlappingSectionFrames ever touches an
        // OrbitalCheckpoint section. This test documents that assumption -- if it changes,
        // this test fails and the invariant needs to be re-examined.
        [Fact]
        public void CanMergeContinuousEvaAtmoSurfaceBoundary_OrbitalPhasePair_IsNotAllowed()
        {
            const double t0 = 22000.0;
            const uint pid = 7302u;
            const string crewName = "Valentina Kerman";
            const string parentId = "eva-parent-371-orbital";

            var a = new Recording
            {
                RecordingId = "eva-371-orbital-a",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                EvaCrewName = crewName,
                ParentRecordingId = parentId,
                VesselPersistentId = pid,
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin",
                ChainId = "eva-chain-371-orbital",
                ChainIndex = 0
            };
            a.Points.Add(new TrajectoryPoint
            {
                ut = t0,
                latitude = 0,
                longitude = 0,
                altitude = 80000,
                rotation = new Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin"
            });
            a.Points.Add(new TrajectoryPoint
            {
                ut = t0 + 10,
                latitude = 0.01,
                longitude = 0.01,
                altitude = 80100,
                rotation = new Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin"
            });

            var b = new Recording
            {
                RecordingId = "eva-371-orbital-b",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                EvaCrewName = crewName,
                ParentRecordingId = parentId,
                VesselPersistentId = pid,
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin",
                ChainId = "eva-chain-371-orbital",
                ChainIndex = 1
            };
            b.Points.Add(new TrajectoryPoint
            {
                ut = t0 + 10,
                latitude = 0.01,
                longitude = 0.01,
                altitude = 80100,
                rotation = new Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin"
            });
            b.Points.Add(new TrajectoryPoint
            {
                ut = t0 + 20,
                latitude = 0.02,
                longitude = 0.02,
                altitude = 80200,
                rotation = new Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin"
            });

            Assert.False(RecordingOptimizer.CanMergeContinuousEvaAtmoSurfaceBoundary(a, b));
        }


        private static Recording BuildEvaSectionAuthoritativeRecording(
            string recordingId,
            string phase,
            SegmentEnvironment env,
            double startUT,
            double midUT,
            double endUT,
            TrajectoryPoint boundaryPoint,
            string crewName,
            string parentId,
            uint pid,
            int chainIndex)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "EVA Kerbal",
                EvaCrewName = crewName,
                ParentRecordingId = parentId,
                VesselPersistentId = pid,
                SegmentPhase = phase,
                SegmentBodyName = "Kerbin",
                StartBodyName = "Kerbin",
                ChainId = "eva-chain-371",
                ChainIndex = chainIndex,
                ChainBranch = 0,
                PlaybackEnabled = true,
                LoopPlayback = false,
                LoopIntervalSeconds = 10.0,
                Hidden = false
            };

            TrajectoryPoint early;
            TrajectoryPoint late;
            if (phase == "atmo")
            {
                early = new TrajectoryPoint
                {
                    ut = startUT,
                    latitude = -0.0970,
                    longitude = -74.5570,
                    altitude = 1000.0,
                    rotation = new Quaternion(0, 0, 0, 1),
                    velocity = new Vector3(0, -80f, 0),
                    bodyName = "Kerbin"
                };
                late = new TrajectoryPoint
                {
                    ut = midUT,
                    latitude = -0.0955,
                    longitude = -74.5540,
                    altitude = 700.0,
                    rotation = new Quaternion(0, 0, 0, 1),
                    velocity = new Vector3(0, -55f, 0),
                    bodyName = "Kerbin"
                };
            }
            else
            {
                early = new TrajectoryPoint
                {
                    ut = midUT,
                    latitude = -0.0938,
                    longitude = -74.5508,
                    altitude = 400.0,
                    rotation = new Quaternion(0, 0, 0, 1),
                    velocity = new Vector3(0.5f, 0f, 0),
                    bodyName = "Kerbin"
                };
                late = new TrajectoryPoint
                {
                    ut = endUT,
                    latitude = -0.0933,
                    longitude = -74.5500,
                    altitude = 400.0,
                    rotation = new Quaternion(0, 0, 0, 1),
                    velocity = new Vector3(0.25f, 0f, 0),
                    bodyName = "Kerbin"
                };
            }

            var frames = phase == "atmo"
                ? new List<TrajectoryPoint> { early, late, boundaryPoint }
                : new List<TrajectoryPoint> { boundaryPoint, early, late };

            rec.Points.AddRange(frames);

            rec.TrackSections.Add(new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 5.0f,
                minAltitude = 400f,
                maxAltitude = 1000f,
                frames = frames,
                checkpoints = new List<OrbitSegment>()
            });

            return rec;
        }


        private static List<TrajectoryPoint> DeepCopyPoints(List<TrajectoryPoint> source)
        {
            var copy = new List<TrajectoryPoint>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                var p = source[i];
                copy.Add(new TrajectoryPoint
                {
                    ut = p.ut,
                    latitude = p.latitude,
                    longitude = p.longitude,
                    altitude = p.altitude,
                    rotation = new Quaternion(p.rotation.x, p.rotation.y, p.rotation.z, p.rotation.w),
                    velocity = new Vector3(p.velocity.x, p.velocity.y, p.velocity.z),
                    bodyName = p.bodyName,
                    funds = p.funds,
                    science = p.science,
                    reputation = p.reputation
                });
            }
            return copy;
        }

        private static List<OrbitSegment> DeepCopyOrbitSegments(List<OrbitSegment> source)
        {
            var copy = new List<OrbitSegment>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                var o = source[i];
                copy.Add(new OrbitSegment
                {
                    startUT = o.startUT,
                    endUT = o.endUT,
                    inclination = o.inclination,
                    eccentricity = o.eccentricity,
                    semiMajorAxis = o.semiMajorAxis,
                    longitudeOfAscendingNode = o.longitudeOfAscendingNode,
                    argumentOfPeriapsis = o.argumentOfPeriapsis,
                    meanAnomalyAtEpoch = o.meanAnomalyAtEpoch,
                    epoch = o.epoch,
                    bodyName = o.bodyName,
                    orbitalFrameRotation = new Quaternion(
                        o.orbitalFrameRotation.x, o.orbitalFrameRotation.y,
                        o.orbitalFrameRotation.z, o.orbitalFrameRotation.w),
                    angularVelocity = new Vector3(
                        o.angularVelocity.x, o.angularVelocity.y, o.angularVelocity.z)
                });
            }
            return copy;
        }

        private static void AssertPointEqual(TrajectoryPoint expected, TrajectoryPoint actual)
        {
            Assert.Equal(expected.ut, actual.ut);
            Assert.Equal(expected.latitude, actual.latitude);
            Assert.Equal(expected.longitude, actual.longitude);
            Assert.Equal(expected.altitude, actual.altitude);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.velocity.x, actual.velocity.x);
            Assert.Equal(expected.velocity.y, actual.velocity.y);
            Assert.Equal(expected.velocity.z, actual.velocity.z);
            Assert.Equal(expected.rotation.x, actual.rotation.x);
            Assert.Equal(expected.rotation.y, actual.rotation.y);
            Assert.Equal(expected.rotation.z, actual.rotation.z);
            Assert.Equal(expected.rotation.w, actual.rotation.w);
            Assert.Equal(expected.funds, actual.funds);
            Assert.Equal(expected.science, actual.science);
            Assert.Equal(expected.reputation, actual.reputation);
        }

        private static void AssertOrbitSegmentEqual(OrbitSegment expected, OrbitSegment actual)
        {
            Assert.Equal(expected.startUT, actual.startUT);
            Assert.Equal(expected.endUT, actual.endUT);
            Assert.Equal(expected.inclination, actual.inclination);
            Assert.Equal(expected.eccentricity, actual.eccentricity);
            Assert.Equal(expected.semiMajorAxis, actual.semiMajorAxis);
            Assert.Equal(expected.longitudeOfAscendingNode, actual.longitudeOfAscendingNode);
            Assert.Equal(expected.argumentOfPeriapsis, actual.argumentOfPeriapsis);
            Assert.Equal(expected.meanAnomalyAtEpoch, actual.meanAnomalyAtEpoch);
            Assert.Equal(expected.epoch, actual.epoch);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.orbitalFrameRotation.x, actual.orbitalFrameRotation.x);
            Assert.Equal(expected.orbitalFrameRotation.y, actual.orbitalFrameRotation.y);
            Assert.Equal(expected.orbitalFrameRotation.z, actual.orbitalFrameRotation.z);
            Assert.Equal(expected.orbitalFrameRotation.w, actual.orbitalFrameRotation.w);
            Assert.Equal(expected.angularVelocity.x, actual.angularVelocity.x);
            Assert.Equal(expected.angularVelocity.y, actual.angularVelocity.y);
            Assert.Equal(expected.angularVelocity.z, actual.angularVelocity.z);
        }
    }
}

