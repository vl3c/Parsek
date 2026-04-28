using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 5 P1-D regression tests for
    /// <see cref="ParsekFlight.TryComputeStandaloneWorldPositionForRecording"/>.
    /// The bug: the helper read <c>before.latitude</c> / <c>longitude</c> /
    /// <c>altitude</c> directly and called <c>body.GetWorldSurfacePosition</c>
    /// without consulting the section's <see cref="ReferenceFrame"/>. v6+
    /// RELATIVE sections store metre offsets (dx/dy/dz) in those fields; the
    /// raw call produced a position deep inside the planet.
    /// </summary>
    [Collection("Sequential")]
    public class CoBubbleStandalonePrimaryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CoBubbleStandalonePrimaryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void StandaloneWorldPosition_RelativeFramePrimary_DispatchesCorrectly()
        {
            // Build a recording whose section at UT=105 is RELATIVE-frame and
            // v6+ (the post-RelativeLocalFrameFormatVersion contract). The
            // before/after points carry metre-scale dx/dy/dz, NOT lat/lon/alt
            // (the field name is a v5-era misnomer per CLAUDE.md "Rotation /
            // world frame"). The pre-fix code path read them as lat/lon/alt
            // and silently produced a sub-planetary position; the fix routes
            // RELATIVE sections through TryResolveRelativeWorldPosition (or
            // returns false when no ParsekFlight.Instance is available, as
            // is the case in xUnit).
            var rec = new Recording
            {
                RecordingId = "primary-relative",
                RecordingFormatVersion = RecordingStore.RelativeLocalFrameFormatVersion, // v6 → metre-offset contract
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        latitude = 12.5,    // metres, NOT degrees, per v6 RELATIVE
                        longitude = 7.0,    // metres
                        altitude = 3.0,     // metres
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero,
                    },
                    new TrajectoryPoint
                    {
                        ut = 110.0,
                        latitude = 13.0,
                        longitude = 7.5,
                        altitude = 3.2,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero,
                    },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 110.0,
                        referenceFrame = ReferenceFrame.Relative,
                        source = TrackSectionSource.Active,
                        anchorVesselId = 4242,  // synthetic anchor PID
                        sampleRateHz = 4.0f,
                    }
                }
            };

            RecordingStore.AddCommittedInternal(rec);

            // Without a live ParsekFlight.Instance the v6 RELATIVE path
            // can't resolve the anchor pose; the helper must return false
            // (HR-9 visible failure) rather than silently lat/lon/alt-as-
            // degrees the offset to a sub-planetary position. The pre-fix
            // path returned TRUE with a garbage worldPos.
            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                "primary-relative", 105.0, fallbackBody: null,
                out Vector3d worldPos);

            Assert.False(ok);
            Assert.Equal(0.0, worldPos.x);
            Assert.Equal(0.0, worldPos.y);
            Assert.Equal(0.0, worldPos.z);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("TryComputeStandaloneRelativeWorldPosition")
                && l.Contains("ParsekFlight.Instance null"));
        }

        [Fact]
        public void StandaloneWorldPosition_OrbitalCheckpointSection_ReturnsFalseWithVerbose()
        {
            // Checkpoint sections aren't valid primary substrates — they're
            // orbital propagation outputs, not bubble-frame positions. The
            // helper must surface the unsupported case as a Verbose log and
            // return false (HR-9 visible failure).
            var rec = new Recording
            {
                RecordingId = "primary-checkpoint",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0, latitude = 0, longitude = 0, altitude = 70000,
                        bodyName = "Kerbin", rotation = Quaternion.identity,
                    },
                    new TrajectoryPoint
                    {
                        ut = 110.0, latitude = 0, longitude = 0, altitude = 70000,
                        bodyName = "Kerbin", rotation = Quaternion.identity,
                    },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 110.0,
                        referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                        source = TrackSectionSource.Checkpoint,
                        sampleRateHz = 1.0f,
                    }
                }
            };
            RecordingStore.AddCommittedInternal(rec);

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                "primary-checkpoint", 105.0, fallbackBody: null,
                out Vector3d worldPos);
            Assert.False(ok);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("OrbitalCheckpoint section unsupported"));
        }

        [Fact]
        public void StandaloneWorldPosition_RecordingMissing_ReturnsFalse()
        {
            // No-op guard: an unknown recording id returns false silently
            // (HR-9: missing primary is normal state for the lazy-recompute
            // boundary, not a failure).
            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                "no-such-recording", 105.0, fallbackBody: null,
                out Vector3d worldPos);
            Assert.False(ok);
            Assert.Equal(0.0, worldPos.x);
            Assert.Equal(0.0, worldPos.y);
            Assert.Equal(0.0, worldPos.z);
        }
    }
}
