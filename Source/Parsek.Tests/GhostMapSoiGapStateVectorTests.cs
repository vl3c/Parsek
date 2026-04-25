using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostMapSoiGapStateVectorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostMapSoiGapStateVectorTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_SoiGapCheckpointFallbackAccepted_ReturnsMunStateVectorSource()
        {
            Recording rec = MakeSoiGapRecording(
                "soi-gap-accepted",
                checkpointBody: "Mun",
                futureSegmentBody: "Mun");

            GhostMapPresence.TrackingStationGhostSource source = Resolve(
                rec,
                currentUT: 160.0,
                allowSoiGapStateVectorFallback: true,
                expectedSoiGapBody: "Mun",
                out _,
                out TrajectoryPoint point,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.StateVectorSoiGap, source);
            Assert.Equal("Mun", point.bodyName);
            Assert.Equal(GhostMapPresence.SoiGapStateVectorFallbackReason, skipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("source-resolve:")
                && l.Contains("source=StateVectorSoiGap")
                && l.Contains("body=Mun")
                && l.Contains("reason=" + GhostMapPresence.SoiGapStateVectorFallbackReason));
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("test-soi-gap-state-vector:")
                && l.Contains("orbitalCheckpointFallback=accept")
                && l.Contains("fallbackReason=" + GhostMapPresence.SoiGapStateVectorFallbackReason));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_NormalCheckpointStateVectorStillRejects()
        {
            Recording rec = MakeSoiGapRecording(
                "checkpoint-not-soi-gap",
                checkpointBody: "Mun",
                futureSegmentBody: "Mun");

            GhostMapPresence.TrackingStationGhostSource source = Resolve(
                rec,
                currentUT: 160.0,
                allowSoiGapStateVectorFallback: false,
                expectedSoiGapBody: "Mun",
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap, skipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("source-resolve:")
                && l.Contains("source=None")
                && l.Contains("reason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap));
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("test-soi-gap-state-vector:")
                && l.Contains("fallbackReason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap)
                && l.Contains("isSoiGapRecovery=False"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_SoiGapCheckpointFallbackRejectsBodyMismatch()
        {
            Recording rec = MakeSoiGapRecording(
                "checkpoint-body-mismatch",
                checkpointBody: "Kerbin",
                futureSegmentBody: "Mun");

            GhostMapPresence.TrackingStationGhostSource source = Resolve(
                rec,
                currentUT: 160.0,
                allowSoiGapStateVectorFallback: true,
                expectedSoiGapBody: "Mun",
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectBodyMismatch, skipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("source-resolve:")
                && l.Contains("reason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectBodyMismatch));
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("test-soi-gap-state-vector:")
                && l.Contains("fallbackReason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectBodyMismatch)
                && l.Contains("stateVectorBody=Kerbin")
                && l.Contains("expectedBody=Mun")
                && l.Contains("bodyMatches=False"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_SoiGapCheckpointFallbackRejectsOutsidePlaybackWindow()
        {
            Recording rec = MakeSoiGapRecording(
                "checkpoint-outside-window",
                checkpointBody: "Mun",
                futureSegmentBody: "Mun");
            TrajectoryPoint outsideWindowPoint = rec.TrackSections[0].frames[0];
            outsideWindowPoint.ut = 50.0;
            rec.TrackSections[0].frames[0] = outsideWindowPoint;

            GhostMapPresence.TrackingStationGhostSource source = Resolve(
                rec,
                currentUT: 160.0,
                allowSoiGapStateVectorFallback: true,
                expectedSoiGapBody: "Mun",
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectOutsideWindow, skipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("source-resolve:")
                && l.Contains("source=None")
                && l.Contains("reason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectOutsideWindow));
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("test-soi-gap-state-vector:")
                && l.Contains("pointUT=50.0")
                && l.Contains("fallbackReason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectOutsideWindow)
                && l.Contains("withinPlaybackWindow=False"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_SameBodyGapIsNotSoiRecovery()
        {
            Recording rec = MakeSoiGapRecording(
                "same-body-gap",
                checkpointBody: "Kerbin",
                futureSegmentBody: "Kerbin");

            GhostMapPresence.TrackingStationGhostSource source = Resolve(
                rec,
                currentUT: 160.0,
                allowSoiGapStateVectorFallback: true,
                expectedSoiGapBody: "Kerbin",
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap, skipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("test-soi-gap-state-vector:")
                && l.Contains("fallbackReason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap)
                && l.Contains("gapBodyTransition=False")
                && l.Contains("gapPreviousBody=Kerbin")
                && l.Contains("gapNextBody=Kerbin"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_UsesActualNextSegmentBodyOverStaleExpectedBody()
        {
            Recording rec = MakeSoiGapRecording(
                "stale-expected-body",
                checkpointBody: "Duna",
                futureSegmentBody: "Mun");

            GhostMapPresence.TrackingStationGhostSource source = Resolve(
                rec,
                currentUT: 160.0,
                allowSoiGapStateVectorFallback: true,
                expectedSoiGapBody: "Duna",
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectBodyMismatch, skipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("test-soi-gap-state-vector:")
                && l.Contains("fallbackReason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectBodyMismatch)
                && l.Contains("stateVectorBody=Duna")
                && l.Contains("expectedBody=Mun")
                && l.Contains("gapNextBody=Mun"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_CurrentSegmentWinsOverCheckpointFallback()
        {
            Recording rec = MakeSegmentAvailableRecording(
                "segment-wins",
                checkpointBody: "Mun");

            GhostMapPresence.TrackingStationGhostSource source = Resolve(
                rec,
                currentUT: 160.0,
                allowSoiGapStateVectorFallback: true,
                expectedSoiGapBody: "Mun",
                out OrbitSegment segment,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Equal("Mun", segment.bodyName);
            Assert.Null(skipReason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("source-resolve:")
                && l.Contains("source=Segment")
                && l.Contains("body=Mun"));
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("test-soi-gap-state-vector:")
                && l.Contains("segmentBody=Mun")
                && l.Contains("fallbackReason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectSaferSegment)
                && l.Contains("segmentSourceAvailable=True"));
        }

        private static GhostMapPresence.TrackingStationGhostSource Resolve(
            Recording rec,
            double currentUT,
            bool allowSoiGapStateVectorFallback,
            string expectedSoiGapBody,
            out OrbitSegment segment,
            out TrajectoryPoint point,
            out string skipReason)
        {
            int cachedStateVectorIndex = -1;
            return GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                isSuppressed: false,
                alreadyMaterialized: false,
                currentUT: currentUT,
                allowTerminalOrbitFallback: true,
                logOperationName: "test-soi-gap-state-vector",
                stateVectorCachedIndex: ref cachedStateVectorIndex,
                segment: out segment,
                stateVectorPoint: out point,
                skipReason: out skipReason,
                recordingIndex: 1,
                allowSoiGapStateVectorFallback: allowSoiGapStateVectorFallback,
                expectedSoiGapBody: expectedSoiGapBody);
        }

        private static Recording MakeSoiGapRecording(
            string recordingId,
            string checkpointBody,
            string futureSegmentBody)
        {
            var rec = MakeBaseRecording(recordingId);
            rec.OrbitSegments = new List<OrbitSegment>
            {
                Segment(100.0, 140.0, "Kerbin", 750000.0, 0.01),
                Segment(200.0, 300.0, futureSegmentBody, 250000.0, 0.2)
            };
            rec.TrackSections = new List<TrackSection>
            {
                OrbitalCheckpointSection(150.0, 190.0, checkpointBody)
            };
            return rec;
        }

        private static Recording MakeSegmentAvailableRecording(
            string recordingId,
            string checkpointBody)
        {
            var rec = MakeBaseRecording(recordingId);
            rec.OrbitSegments = new List<OrbitSegment>
            {
                Segment(100.0, 300.0, "Mun", 250000.0, 0.2)
            };
            rec.TrackSections = new List<TrackSection>
            {
                OrbitalCheckpointSection(150.0, 190.0, checkpointBody)
            };
            return rec;
        }

        private static Recording MakeBaseRecording(string recordingId)
        {
            return new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = 6,
                VesselName = "Kerbal X",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 250000.0,
                TerminalOrbitEccentricity = 0.2,
                TerminalOrbitInclination = 0.0,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = 100.0,
            };
        }

        private static TrackSection OrbitalCheckpointSection(
            double startUT,
            double endUT,
            string bodyName)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = startUT,
                        latitude = 1.0,
                        longitude = 2.0,
                        altitude = 47481.0,
                        bodyName = bodyName,
                        velocity = new Vector3(515.4f, 0.0f, 0.0f),
                    },
                    new TrajectoryPoint
                    {
                        ut = endUT,
                        latitude = 1.1,
                        longitude = 2.1,
                        altitude = 47500.0,
                        bodyName = bodyName,
                        velocity = new Vector3(515.4f, 0.0f, 0.0f),
                    }
                },
                checkpoints = new List<OrbitSegment>
                {
                    Segment(startUT, endUT, bodyName, 250000.0, 0.2)
                },
                sampleRateHz = 1.0f,
                minAltitude = 47481.0f,
                maxAltitude = 47500.0f,
            };
        }

        private static OrbitSegment Segment(
            double startUT,
            double endUT,
            string bodyName,
            double semiMajorAxis,
            double eccentricity)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = bodyName,
                semiMajorAxis = semiMajorAxis,
                eccentricity = eccentricity,
                inclination = 0.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
            };
        }
    }
}
