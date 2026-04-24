using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingFinalizationCacheTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingFinalizationCacheTests()
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
        public void TryApply_AppendsTailAfterLastRealSample_AndClampsFirstSegment()
        {
            var rec = MakeRecording("rec-tail", 42u, 100.0);
            var cache = MakeCache(
                "rec-tail",
                42u,
                TerminalState.Destroyed,
                terminalUT: 220.0,
                Segment(90.0, 150.0),
                Segment(150.0, 220.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-tail"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.True(result.Applied);
            Assert.Equal(2, result.AppendedSegmentCount);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(220.0, rec.ExplicitEndUT);
            Assert.Equal(100.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(150.0, rec.OrbitSegments[0].endUT);
            Assert.Equal(150.0, rec.OrbitSegments[1].startUT);
            Assert.Equal(220.0, rec.OrbitSegments[1].endUT);
            Assert.All(rec.OrbitSegments, seg => Assert.True(seg.isPredicted));
            Assert.True(rec.FilesDirty);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][INFO][FinalizerCache]")
                && line.Contains("Apply accepted")
                && line.Contains("rec-tail")
                && line.Contains("appendedSegments=2"));
        }

        [Fact]
        public void TryApply_DropsSegmentsBeforeLastRealSample_AndTrimsAtTerminalUT()
        {
            var rec = MakeRecording("rec-trim", 42u, 100.0);
            var cache = MakeCache(
                "rec-trim",
                42u,
                TerminalState.Destroyed,
                terminalUT: 190.0,
                Segment(40.0, 90.0),
                Segment(90.0, 150.0),
                Segment(150.0, 250.0),
                Segment(250.0, 300.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-trim"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(2, result.AppendedSegmentCount);
            Assert.Equal(100.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(150.0, rec.OrbitSegments[0].endUT);
            Assert.Equal(150.0, rec.OrbitSegments[1].startUT);
            Assert.Equal(190.0, rec.OrbitSegments[1].endUT);
        }

        [Fact]
        public void TryApply_RejectsTerminalBeforeLastAuthoredSample()
        {
            var rec = MakeRecording("rec-retrograde", 42u, 100.0);
            var cache = MakeCache(
                "rec-retrograde",
                42u,
                TerminalState.Destroyed,
                terminalUT: 99.0,
                Segment(90.0, 99.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-retrograde"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(
                RecordingFinalizationCacheApplyStatus.RejectedTerminalBeforeLastSample,
                result.Status);
            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Empty(rec.OrbitSegments);
            Assert.False(rec.FilesDirty);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][WARN][FinalizerCache]")
                && line.Contains("RejectedTerminalBeforeLastSample")
                && line.Contains("rec-retrograde"));
        }

        [Fact]
        public void TryApply_RejectsMismatchedRecordingId()
        {
            var rec = MakeRecording("rec-a", 42u, 100.0);
            var cache = MakeCache("rec-b", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-id"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(RecordingFinalizationCacheApplyStatus.RejectedMismatchedRecording, result.Status);
            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Contains(logLines, line =>
                line.Contains("RejectedMismatchedRecording")
                && line.Contains("cacheRec=rec-b"));
        }

        [Fact]
        public void TryApply_RejectsMissingRecordingId()
        {
            var rec = MakeRecording("rec-missing-id", 42u, 100.0);
            rec.RecordingId = null;
            var cache = MakeCache("rec-missing-id", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-null-recording-id"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(RecordingFinalizationCacheApplyStatus.RejectedMismatchedRecording, result.Status);
            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Contains(logLines, line =>
                line.Contains("RejectedMismatchedRecording")
                && line.Contains("consumer=unit-null-recording-id"));
        }

        [Fact]
        public void TryApply_RejectsMismatchedVesselPid()
        {
            var rec = MakeRecording("rec-pid", 42u, 100.0);
            var cache = MakeCache("rec-pid", 99u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-pid"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(RecordingFinalizationCacheApplyStatus.RejectedMismatchedVessel, result.Status);
            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Contains(logLines, line => line.Contains("RejectedMismatchedVessel"));
        }

        [Fact]
        public void TryApply_RejectsStaleCacheByDefault()
        {
            var rec = MakeRecording("rec-stale", 42u, 100.0);
            var cache = MakeCache("rec-stale", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));
            cache.Status = FinalizationCacheStatus.Stale;

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-stale"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(RecordingFinalizationCacheApplyStatus.RejectedStale, result.Status);
            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.False(rec.FilesDirty);
            Assert.Contains(logLines, line => line.Contains("RejectedStale"));
        }

        [Fact]
        public void TryApply_AllowsStaleCacheWhenConsumerExplicitlyPermitsIt()
        {
            var rec = MakeRecording("rec-stale-allowed", 42u, 100.0);
            var cache = MakeCache("rec-stale-allowed", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));
            cache.Status = FinalizationCacheStatus.Stale;

            RecordingFinalizationCacheApplyOptions options = Options("unit-stale-allowed");
            options.AllowStale = true;

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                options,
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.True(result.Applied);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Contains(logLines, line =>
                line.Contains("Apply accepted")
                && line.Contains("staleAllowed=True"));
        }

        [Fact]
        public void TryApply_AllowsUnsetVesselPidAsLegacyWildcard()
        {
            var recordingPidUnknown = MakeRecording("rec-legacy-pid-a", 0u, 100.0);
            var cacheWithPid = MakeCache(
                "rec-legacy-pid-a",
                42u,
                TerminalState.Destroyed,
                160.0,
                Segment(100.0, 160.0));

            bool appliedUnknownRecordingPid = RecordingFinalizationCacheApplier.TryApply(
                recordingPidUnknown,
                cacheWithPid,
                Options("unit-legacy-recording-pid"),
                out RecordingFinalizationCacheApplyResult resultUnknownRecordingPid);

            var recordingWithPid = MakeRecording("rec-legacy-pid-b", 42u, 100.0);
            var cachePidUnknown = MakeCache(
                "rec-legacy-pid-b",
                0u,
                TerminalState.Destroyed,
                160.0,
                Segment(100.0, 160.0));

            bool appliedUnknownCachePid = RecordingFinalizationCacheApplier.TryApply(
                recordingWithPid,
                cachePidUnknown,
                Options("unit-legacy-cache-pid"),
                out RecordingFinalizationCacheApplyResult resultUnknownCachePid);

            Assert.True(appliedUnknownRecordingPid);
            Assert.True(resultUnknownRecordingPid.Applied);
            Assert.True(appliedUnknownCachePid);
            Assert.True(resultUnknownCachePid.Applied);
        }

        [Fact]
        public void TryApply_RejectsAlreadyFinalizedByDefault()
        {
            var rec = MakeRecording("rec-finalized", 42u, 100.0);
            rec.TerminalStateValue = TerminalState.Landed;
            var cache = MakeCache("rec-finalized", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-finalized"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(RecordingFinalizationCacheApplyStatus.RejectedAlreadyFinalized, result.Status);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.Empty(rec.OrbitSegments);
            Assert.Contains(logLines, line => line.Contains("RejectedAlreadyFinalized"));
        }

        [Fact]
        public void TryApply_PreservesAuthoredData_AndUsesTrackSectionEndUtAsLastAuthoredUT()
        {
            var rec = MakeRecording("rec-authored", 42u, 100.0);
            rec.OrbitSegments.Add(Segment(80.0, 100.0, isPredicted: false));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = 100.0,
                endUT = 120.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin" }
                },
                checkpoints = new List<OrbitSegment>()
            });
            var cache = MakeCache("rec-authored", 42u, TerminalState.Destroyed, 180.0, Segment(100.0, 180.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-authored"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Single(rec.Points);
            Assert.Equal(2, rec.OrbitSegments.Count);
            Assert.False(rec.OrbitSegments[0].isPredicted);
            Assert.True(rec.OrbitSegments[1].isPredicted);
            Assert.Equal(120.0, rec.OrbitSegments[1].startUT);
            Assert.Equal(120.0, result.LastAuthoredUT);
        }

        [Fact]
        public void TryApply_ReplacesExistingPredictedTailInRepairMode()
        {
            var rec = MakeRecording("rec-repair", 42u, 100.0);
            rec.TerminalStateValue = TerminalState.SubOrbital;
            rec.OrbitSegments.Add(Segment(80.0, 100.0, isPredicted: false));
            rec.OrbitSegments.Add(Segment(100.0, 150.0, isPredicted: true));
            rec.OrbitSegments.Add(Segment(150.0, 200.0, isPredicted: true));
            var cache = MakeCache("rec-repair", 42u, TerminalState.Destroyed, 180.0, Segment(100.0, 180.0));
            RecordingFinalizationCacheApplyOptions options = Options("unit-repair");
            options.AllowAlreadyFinalizedRepair = true;

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                options,
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(2, result.RemovedPredictedSegmentCount);
            Assert.Single(rec.OrbitSegments.Where(seg => !seg.isPredicted));
            Assert.Single(rec.OrbitSegments.Where(seg => seg.isPredicted));
            Assert.False(rec.OrbitSegments[0].isPredicted);
            Assert.True(rec.OrbitSegments[1].isPredicted);
            Assert.Equal(180.0, rec.OrbitSegments[1].endUT);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Contains(logLines, line => line.Contains("removedPredictedSegments=2"));
        }

        [Fact]
        public void TryApply_ClearsStaleTerminalOrbitWhenRepairingToDestroyed()
        {
            var rec = MakeRecording("rec-repair-terminal-orbit", 42u, 100.0);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Mun";
            rec.TerminalOrbitSemiMajorAxis = 255000.0;
            rec.TerminalOrbitEccentricity = 0.04;
            rec.TerminalOrbitInclination = 6.0;
            rec.OrbitSegments.Add(Segment(80.0, 100.0, isPredicted: false, body: "Mun"));
            rec.OrbitSegments.Add(Segment(100.0, 200.0, isPredicted: true, body: "Mun"));
            var cache = MakeCache(
                "rec-repair-terminal-orbit",
                42u,
                TerminalState.Destroyed,
                180.0,
                Segment(100.0, 180.0));
            RecordingFinalizationCacheApplyOptions options = Options("unit-repair-terminal-orbit");
            options.AllowAlreadyFinalizedRepair = true;

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                options,
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Null(rec.TerminalOrbitBody);
            Assert.Equal(0.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.0, rec.TerminalOrbitEccentricity);
            Assert.Equal(0.0, rec.TerminalOrbitInclination);
        }

        [Fact]
        public void TryApply_TerminalUtReplacesStaleExplicitEndWithoutShrinkingAuthoredData()
        {
            var rec = MakeRecording("rec-explicit", 42u, 100.0);
            rec.ExplicitEndUT = 500.0;
            var cache = MakeCache("rec-explicit", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-explicit"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(160.0, rec.ExplicitEndUT);
            Assert.Equal(160.0, rec.EndUT);
            Assert.Equal(500.0, result.OldExplicitEndUT);
            Assert.Equal(160.0, result.NewExplicitEndUT);
        }

        [Fact]
        public void TryApply_RejectsWhenOnlyExistingOrbitSegmentsArePredicted()
        {
            var rec = new Recording
            {
                RecordingId = "rec-predicted-only",
                VesselPersistentId = 42u
            };
            rec.OrbitSegments.Add(Segment(100.0, 200.0, isPredicted: true));
            var cache = MakeCache("rec-predicted-only", 42u, TerminalState.Destroyed, 260.0, Segment(200.0, 260.0));

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-predicted-only"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(RecordingFinalizationCacheApplyStatus.RejectedNoAuthoredData, result.Status);
            Assert.Null(rec.TerminalStateValue);
            Assert.Single(rec.OrbitSegments);
            Assert.Contains(logLines, line => line.Contains("RejectedNoAuthoredData"));
        }

        [Fact]
        public void TryApply_LogsRemainingRejectionClasses()
        {
            AssertRejectsAndLogs(
                RecordingFinalizationCacheApplyStatus.RejectedNullRecording,
                null,
                MakeCache("rec-null-recording", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0)),
                "unit-null-recording");

            AssertRejectsAndLogs(
                RecordingFinalizationCacheApplyStatus.RejectedNullCache,
                MakeRecording("rec-null-cache", 42u, 100.0),
                null,
                "unit-null-cache");

            var emptyCache = MakeCache("rec-empty", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));
            emptyCache.Status = FinalizationCacheStatus.Empty;
            AssertRejectsAndLogs(
                RecordingFinalizationCacheApplyStatus.RejectedEmpty,
                MakeRecording("rec-empty", 42u, 100.0),
                emptyCache,
                "unit-empty");

            var failedCache = MakeCache("rec-failed", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));
            failedCache.Status = FinalizationCacheStatus.Failed;
            AssertRejectsAndLogs(
                RecordingFinalizationCacheApplyStatus.RejectedFailed,
                MakeRecording("rec-failed", 42u, 100.0),
                failedCache,
                "unit-failed");

            var unsetTerminal = MakeCache("rec-unset-terminal", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));
            unsetTerminal.TerminalState = null;
            AssertRejectsAndLogs(
                RecordingFinalizationCacheApplyStatus.RejectedUnsetTerminalState,
                MakeRecording("rec-unset-terminal", 42u, 100.0),
                unsetTerminal,
                "unit-unset-terminal");

            var invalidTerminal = MakeCache("rec-invalid-terminal", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));
            invalidTerminal.TerminalState = (TerminalState)999;
            AssertRejectsAndLogs(
                RecordingFinalizationCacheApplyStatus.RejectedInvalidTerminalState,
                MakeRecording("rec-invalid-terminal", 42u, 100.0),
                invalidTerminal,
                "unit-invalid-terminal");

            var invalidTerminalUT = MakeCache("rec-invalid-ut", 42u, TerminalState.Destroyed, 160.0, Segment(100.0, 160.0));
            invalidTerminalUT.TerminalUT = double.NaN;
            AssertRejectsAndLogs(
                RecordingFinalizationCacheApplyStatus.RejectedInvalidTerminalUT,
                MakeRecording("rec-invalid-ut", 42u, 100.0),
                invalidTerminalUT,
                "unit-invalid-ut");
        }

        [Fact]
        public void TryApply_StampsTerminalOrbitFromCache()
        {
            var rec = MakeRecording("rec-orbit", 42u, 100.0);
            var cache = MakeCache("rec-orbit", 42u, TerminalState.Orbiting, 300.0, Segment(100.0, 300.0, body: "Mun"));
            cache.TerminalOrbit = new RecordingFinalizationTerminalOrbit
            {
                bodyName = "Mun",
                semiMajorAxis = 255000.0,
                eccentricity = 0.04,
                inclination = 6.0,
                longitudeOfAscendingNode = 12.0,
                argumentOfPeriapsis = 34.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 300.0
            };

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-orbit"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(255000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.04, rec.TerminalOrbitEccentricity);
            Assert.Equal(6.0, rec.TerminalOrbitInclination);
            Assert.Equal(RecordingEndpointPhase.OrbitSegment, rec.EndpointPhase);
            Assert.Equal("Mun", rec.EndpointBodyName);
        }

        [Theory]
        [InlineData(TerminalState.SubOrbital)]
        [InlineData(TerminalState.Docked)]
        public void TryApply_StampsTerminalOrbitFromCache_ForOrbitalTerminalStates(TerminalState terminalState)
        {
            var rec = MakeRecording($"rec-orbit-{terminalState}", 42u, 100.0);
            var cache = MakeCache($"rec-orbit-{terminalState}", 42u, terminalState, 300.0, Segment(100.0, 300.0, body: "Mun"));
            cache.TerminalOrbit = new RecordingFinalizationTerminalOrbit
            {
                bodyName = "Mun",
                semiMajorAxis = 255000.0,
                eccentricity = 0.04,
                inclination = 6.0,
                longitudeOfAscendingNode = 12.0,
                argumentOfPeriapsis = 34.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 300.0
            };

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options($"unit-orbit-{terminalState}"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(terminalState, rec.TerminalStateValue);
            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(255000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.04, rec.TerminalOrbitEccentricity);
            Assert.Equal(6.0, rec.TerminalOrbitInclination);
        }

        [Fact]
        public void TryApply_OrbitingWithoutTailOrTerminalOrbit_AppliesWithoutStampingOrbit()
        {
            var rec = MakeRecording("rec-orbit-empty", 42u, 100.0);
            var cache = MakeCache("rec-orbit-empty", 42u, TerminalState.Orbiting, 160.0);

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-orbit-empty"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.True(result.Applied);
            Assert.Equal(0, result.AppendedSegmentCount);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.Equal(160.0, rec.ExplicitEndUT);
            Assert.Null(rec.TerminalOrbitBody);
            Assert.Equal(0.0, rec.TerminalOrbitSemiMajorAxis);
        }

        [Fact]
        public void TryApply_AppliesSurfaceMetadata()
        {
            var rec = MakeRecording("rec-surface", 42u, 100.0);
            var terminalPosition = MakeSurfacePosition(SurfaceSituation.Landed, altitude: 12.0);
            var cache = MakeCache("rec-surface", 42u, TerminalState.Landed, 140.0);
            cache.TerminalPosition = terminalPosition;
            cache.TerrainHeightAtEnd = 8.5;

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-surface"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal(12.0, rec.TerminalPosition.Value.altitude);
            Assert.Equal(8.5, rec.TerrainHeightAtEnd);
            Assert.Equal(RecordingEndpointPhase.TerminalPosition, rec.EndpointPhase);
            Assert.Equal("Kerbin", rec.EndpointBodyName);
        }

        [Fact]
        public void TryApply_AppliesSplashedSurfaceMetadata()
        {
            var rec = MakeRecording("rec-splashed", 42u, 100.0);
            var terminalPosition = MakeSurfacePosition(SurfaceSituation.Splashed, altitude: -0.25);
            var cache = MakeCache("rec-splashed", 42u, TerminalState.Splashed, 140.0);
            cache.TerminalPosition = terminalPosition;
            cache.TerrainHeightAtEnd = -1.5;

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-splashed"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Splashed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal(SurfaceSituation.Splashed, rec.TerminalPosition.Value.situation);
            Assert.Equal(-0.25, rec.TerminalPosition.Value.altitude);
            Assert.Equal(-1.5, rec.TerrainHeightAtEnd);
            Assert.Equal(RecordingEndpointPhase.TerminalPosition, rec.EndpointPhase);
            Assert.Equal("Kerbin", rec.EndpointBodyName);
        }

        [Fact]
        public void TryApply_SurfaceStateWithoutCachePosition_PreservesExistingSurfaceMetadata()
        {
            var rec = MakeRecording("rec-surface-preserve", 42u, 100.0);
            rec.TerminalPosition = MakeSurfacePosition(SurfaceSituation.Landed, altitude: 31.0);
            rec.TerrainHeightAtEnd = 7.25;
            var cache = MakeCache("rec-surface-preserve", 42u, TerminalState.Landed, 140.0);
            cache.TerrainHeightAtEnd = 999.0;

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-surface-preserve"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal(31.0, rec.TerminalPosition.Value.altitude);
            Assert.Equal(7.25, rec.TerrainHeightAtEnd);
            Assert.False(double.IsNaN(rec.TerrainHeightAtEnd));
        }

        [Fact]
        public void TryApply_SurfaceStateWithoutCachePosition_ClearsMismatchedExistingSurfaceState()
        {
            var rec = MakeRecording("rec-surface-state-mismatch", 42u, 100.0);
            rec.TerminalPosition = MakeSurfacePosition(SurfaceSituation.Splashed, altitude: -1.0);
            rec.TerrainHeightAtEnd = -3.0;
            var cache = MakeCache("rec-surface-state-mismatch", 42u, TerminalState.Landed, 140.0);

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-surface-state-mismatch"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.False(rec.TerminalPosition.HasValue);
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));
            Assert.NotEqual(RecordingEndpointPhase.TerminalPosition, rec.EndpointPhase);
        }

        [Fact]
        public void TryApply_SurfaceStateWithoutCachePosition_ClearsMismatchedExistingSurfaceBody()
        {
            var rec = MakeRecording("rec-surface-body-mismatch", 42u, 100.0);
            rec.TerminalPosition = MakeSurfacePosition(SurfaceSituation.Landed, altitude: 31.0);
            rec.TerrainHeightAtEnd = 7.25;
            var cache = MakeCache("rec-surface-body-mismatch", 42u, TerminalState.Landed, 140.0);
            cache.TerminalBodyName = "Mun";

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-surface-body-mismatch"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.False(rec.TerminalPosition.HasValue);
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));
            Assert.NotEqual(RecordingEndpointPhase.TerminalPosition, rec.EndpointPhase);
        }

        [Fact]
        public void TryApply_SurfaceStateWithoutCachePosition_ClearsBodylessExistingSurfaceMetadata()
        {
            var rec = MakeRecording("rec-surface-bodyless", 42u, 100.0);
            SurfacePosition terminalPosition = MakeSurfacePosition(SurfaceSituation.Landed, altitude: 31.0);
            terminalPosition.body = null;
            rec.TerminalPosition = terminalPosition;
            rec.TerrainHeightAtEnd = 7.25;
            var cache = MakeCache("rec-surface-bodyless", 42u, TerminalState.Landed, 140.0);

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options("unit-surface-bodyless"),
                out RecordingFinalizationCacheApplyResult result);

            Assert.True(applied);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.False(rec.TerminalPosition.HasValue);
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));
            Assert.NotEqual(RecordingEndpointPhase.TerminalPosition, rec.EndpointPhase);
        }

        private static Recording MakeRecording(string id, uint vesselPid, params double[] pointUTs)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                VesselPersistentId = vesselPid
            };

            foreach (double pointUT in pointUTs)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = pointUT,
                    bodyName = "Kerbin"
                });
            }

            return rec;
        }

        private static RecordingFinalizationCache MakeCache(
            string recordingId,
            uint vesselPid,
            TerminalState terminalState,
            double terminalUT,
            params OrbitSegment[] segments)
        {
            return new RecordingFinalizationCache
            {
                RecordingId = recordingId,
                VesselPersistentId = vesselPid,
                Owner = FinalizationCacheOwner.ActiveRecorder,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = Math.Max(0.0, terminalUT - 10.0),
                RefreshReason = "unit-test",
                LastObservedUT = Math.Max(0.0, terminalUT - 10.0),
                TailStartsAtUT = segments != null && segments.Length > 0 ? segments[0].startUT : terminalUT,
                TerminalUT = terminalUT,
                TerminalState = terminalState,
                TerminalBodyName = "Kerbin",
                PredictedSegments = segments != null
                    ? segments.ToList()
                    : new List<OrbitSegment>()
            };
        }

        private static OrbitSegment Segment(
            double startUT,
            double endUT,
            bool isPredicted = false,
            string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = body,
                semiMajorAxis = body == "Mun" ? 255000.0 : 700000.0,
                eccentricity = 0.01,
                inclination = 1.0,
                longitudeOfAscendingNode = 2.0,
                argumentOfPeriapsis = 3.0,
                meanAnomalyAtEpoch = 0.4,
                epoch = startUT,
                isPredicted = isPredicted
            };
        }

        private static SurfacePosition MakeSurfacePosition(SurfaceSituation situation, double altitude)
        {
            return new SurfacePosition
            {
                body = "Kerbin",
                latitude = 1.25,
                longitude = -74.5,
                altitude = altitude,
                rotation = Quaternion.identity,
                rotationRecorded = true,
                situation = situation
            };
        }

        private static RecordingFinalizationCacheApplyOptions Options(string consumer)
        {
            return new RecordingFinalizationCacheApplyOptions
            {
                ConsumerPath = consumer
            };
        }

        private void AssertRejectsAndLogs(
            RecordingFinalizationCacheApplyStatus expectedStatus,
            Recording rec,
            RecordingFinalizationCache cache,
            string consumer)
        {
            logLines.Clear();
            bool applied = RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                Options(consumer),
                out RecordingFinalizationCacheApplyResult result);

            Assert.False(applied);
            Assert.Equal(expectedStatus, result.Status);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][WARN][FinalizerCache]")
                && line.Contains(expectedStatus.ToString())
                && line.Contains($"consumer={consumer}"));
        }
    }
}
