using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
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
            ParsekFlight.ActiveInPlaceReFlyPrimaryReadModelResolverForTesting = null;
            ParsekFlight.AnchorResolverWorldPositionResolverForTesting = null;
            ParsekFlight.AnchorResolverBodyResolverForTesting = null;
            FlightRecorder.FrameCountProviderForTesting = null;
            ParsekFlight.SetInstanceForTesting(null);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            ParsekFlight.ActiveInPlaceReFlyPrimaryReadModelResolverForTesting = null;
            ParsekFlight.AnchorResolverWorldPositionResolverForTesting = null;
            ParsekFlight.AnchorResolverBodyResolverForTesting = null;
            FlightRecorder.FrameCountProviderForTesting = null;
            ParsekFlight.SetInstanceForTesting(null);
            ParsekScenario.SetInstanceForTesting(null);
            TestBodyRegistry.Reset();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ParsekFlight InstallFlightInstanceForReadModel(
            RecordingTree tree,
            FlightRecorder recorder = null)
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            host.SetActiveReFlyPrimaryReadModelInputsForTesting(tree, recorder);
            ParsekFlight.SetInstanceForTesting(host);
            return host;
        }

        private static TrajectoryPoint AbsolutePoint(
            double ut,
            double latitude,
            double longitude,
            double altitude,
            string bodyName = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                bodyName = bodyName,
                rotation = Quaternion.identity,
            };
        }

        private static TrackSection AbsoluteSection(params TrajectoryPoint[] frames)
        {
            return new TrackSection
            {
                startUT = frames[0].ut,
                endUT = frames[frames.Length - 1].ut,
                referenceFrame = ReferenceFrame.Absolute,
                frames = new List<TrajectoryPoint>(frames),
            };
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
            // RELATIVE sections through the recording-id anchor resolver and
            // fails closed when legacy data has no anchorRecordingId.
            var rec = new Recording
            {
                RecordingId = "primary-relative",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion, // v6 → metre-offset contract
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

            // Legacy v6 data has no recording-id anchor. The helper must
            // return false (HR-9 visible failure) rather than silently
            // lat/lon/alt-as-degrees the offset to a sub-planetary position.
            // The pre-fix path returned TRUE with a garbage worldPos.
            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                "primary-relative", 105.0, fallbackBody: null,
                out Vector3d worldPos);

            Assert.False(ok);
            Assert.Equal(0.0, worldPos.x);
            Assert.Equal(0.0, worldPos.y);
            Assert.Equal(0.0, worldPos.z);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("TryComputeStandaloneRelativeWorldPosition")
                && l.Contains("anchor-recording-id-missing"));
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
        public void StandaloneWorldPosition_ParentAnchoredDebrisAfterAuthoredFrames_ReturnsFalseWithoutResolverWarn()
        {
            var relativeFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100.0, latitude = 12.5, longitude = 7.0, altitude = 3.0,
                    bodyName = "Kerbin", rotation = Quaternion.identity,
                },
                new TrajectoryPoint
                {
                    ut = 110.0, latitude = 13.0, longitude = 7.5, altitude = 3.2,
                    bodyName = "Kerbin", rotation = Quaternion.identity,
                },
            };
            var rec = new Recording
            {
                RecordingId = "parent-anchored-debris-stale",
                VesselName = "Parent Anchored Debris",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
                Points = relativeFrames,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 140.0,
                        referenceFrame = ReferenceFrame.Relative,
                        source = TrackSectionSource.Active,
                        anchorRecordingId = "parent-rec",
                        sampleRateHz = 4.0f,
                        frames = relativeFrames,
                        bodyFixedFrames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                            new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                        },
                    }
                }
            };
            RecordingStore.AddCommittedInternal(rec);

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                rec.RecordingId, 120.0, fallbackBody: null, out Vector3d worldPos);

            Assert.False(ok);
            Assert.Equal(0.0, worldPos.x);
            Assert.Equal(0.0, worldPos.y);
            Assert.Equal(0.0, worldPos.z);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("body-fixed primary failed closed")
                && l.Contains("recorded-relative fallback suppressed")
                && l.Contains("routeReason=body-fixed-primary-unavailable")
                && l.Contains("coverageReason=relative-and-body-fixed-frames-out-of-range")
                && l.Contains(rec.RecordingId));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RelativeAnchorResolver]")
                || l.Contains("chain resolver failed"));
        }

        [Fact]
        public void StandaloneWorldPosition_ParentAnchoredDebrisBodyFixedMiss_FailsClosedWithoutResolverWarn()
        {
            var relativeFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100.0, latitude = 12.5, longitude = 7.0, altitude = 3.0,
                    bodyName = "Kerbin", rotation = Quaternion.identity,
                },
                new TrajectoryPoint
                {
                    ut = 130.0, latitude = 13.0, longitude = 7.5, altitude = 3.2,
                    bodyName = "Kerbin", rotation = Quaternion.identity,
                },
            };
            var rec = new Recording
            {
                RecordingId = "parent-anchored-debris-body-miss",
                VesselName = "Parent Anchored Debris Body Miss",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
                Points = relativeFrames,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 130.0,
                        referenceFrame = ReferenceFrame.Relative,
                        source = TrackSectionSource.Active,
                        anchorRecordingId = "parent-rec",
                        sampleRateHz = 4.0f,
                        frames = relativeFrames,
                        bodyFixedFrames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100.0, bodyName = "NoSuchBody", rotation = Quaternion.identity },
                            new TrajectoryPoint { ut = 130.0, bodyName = "NoSuchBody", rotation = Quaternion.identity },
                        },
                    }
                }
            };
            RecordingStore.AddCommittedInternal(rec);

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                rec.RecordingId, 120.0, fallbackBody: null, out Vector3d worldPos);

            Assert.False(ok);
            Assert.Equal(0.0, worldPos.x);
            Assert.Equal(0.0, worldPos.y);
            Assert.Equal(0.0, worldPos.z);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("body-fixed primary failed closed")
                && l.Contains("recorded-relative fallback suppressed")
                && l.Contains("routeReason=body-fixed-primary-position-failed")
                && l.Contains("coverageReason=covered-by-body-fixed-primary")
                && l.Contains(rec.RecordingId));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RelativeAnchorResolver]")
                || l.Contains("chain resolver failed"));
        }

        [Fact]
        public void StandaloneWorldPosition_InheritedLoopAnchoredDebrisWithoutBodyFixed_ReachesRecordedRelativeResolver()
        {
            var relativeFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100.0, latitude = 12.5, longitude = 7.0, altitude = 3.0,
                    bodyName = "Kerbin", rotation = Quaternion.identity,
                },
                new TrajectoryPoint
                {
                    ut = 110.0, latitude = 13.0, longitude = 7.5, altitude = 3.2,
                    bodyName = "Kerbin", rotation = Quaternion.identity,
                },
            };
            var rec = new Recording
            {
                RecordingId = "loop-chain-debris",
                VesselName = "Loop Chain Debris",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                IsDebris = true,
                DebrisParentRecordingId = "loop-chain-parent",
                Points = relativeFrames,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 110.0,
                        referenceFrame = ReferenceFrame.Relative,
                        source = TrackSectionSource.Active,
                        anchorRecordingId = "loop-chain-parent",
                        sampleRateHz = 4.0f,
                        frames = relativeFrames,
                    }
                }
            };
            var parent = new Recording
            {
                RecordingId = "loop-chain-parent",
                VesselName = "Loop Chain Parent",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                LoopAnchorVesselId = 77u,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 110.0,
                        referenceFrame = ReferenceFrame.Relative,
                        source = TrackSectionSource.Active,
                        anchorVesselId = 77u,
                        sampleRateHz = 4.0f,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                            new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                        },
                    }
                }
            };
            var tree = new RecordingTree
            {
                Id = "loop-chain-tree",
                TreeName = "Loop Chain Tree",
                RootRecordingId = parent.RecordingId,
                ActiveRecordingId = rec.RecordingId,
            };
            tree.Recordings[rec.RecordingId] = rec;
            tree.Recordings[parent.RecordingId] = parent;
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(rec);

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                rec.RecordingId, 105.0, fallbackBody: null, out Vector3d worldPos);

            Assert.False(ok);
            Assert.Equal(0.0, worldPos.x);
            Assert.Equal(0.0, worldPos.y);
            Assert.Equal(0.0, worldPos.z);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("TryComputeStandaloneRelativeWorldPosition: chain resolver failed")
                && l.Contains(rec.RecordingId));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("relative-only-without-body-fixed-primary")
                && l.Contains(rec.RecordingId));
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

        [Fact]
        public void StandaloneWorldPosition_InPlaceReFlyOriginPrimary_UsesLiveProvisionalReadModel()
        {
            CelestialBody kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            const string originId = "committed-origin";
            const string activeId = "active-provisional";

            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = originId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        latitude = 0.0,
                        longitude = 0.0,
                        altitude = 0.0,
                        bodyName = "NoSuchBody",
                        rotation = Quaternion.identity,
                    }
                }
            });

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-live-primary",
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                InPlaceContinuation = true,
            };
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = marker,
            });

            ParsekFlight.ActiveInPlaceReFlyPrimaryReadModelResolverForTesting =
                (string requestedRecordingId, ReFlySessionMarker activeMarker, double ut,
                    out Recording recording,
                    out ParsekFlight.ActiveReFlyPrimaryReadModelStats stats,
                    out string reason) =>
                {
                    reason = null;
                    stats = new ParsekFlight.ActiveReFlyPrimaryReadModelStats
                    {
                        OriginRecordingId = activeMarker.OriginChildRecordingId,
                        ActiveRecordingId = activeMarker.ActiveReFlyRecordingId,
                        RecorderPointCount = 2,
                        SnapshotPointCount = 2,
                    };
                    recording = new Recording
                    {
                        RecordingId = activeId,
                        RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                        Points = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint
                            {
                                ut = 100.0,
                                latitude = 10.0,
                                longitude = 20.0,
                                altitude = 1000.0,
                                bodyName = "Kerbin",
                                rotation = Quaternion.identity,
                            },
                            new TrajectoryPoint
                            {
                                ut = 110.0,
                                latitude = 10.0,
                                longitude = 20.0,
                                altitude = 1000.0,
                                bodyName = "Kerbin",
                                rotation = Quaternion.identity,
                            },
                        },
                    };
                    return true;
                };

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                originId,
                105.0,
                kerbin,
                out Vector3d worldPos);

            Assert.True(ok, string.Join("\n", logLines));
            Assert.False(double.IsNaN(worldPos.x));
            Assert.False(double.IsInfinity(worldPos.x));
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("Active Re-Fly origin primary resolved from live recorded trajectory")
                && l.Contains(originId)
                && l.Contains(activeId));
        }

        [Fact]
        public void StandaloneWorldPosition_InPlaceReFlyOriginPrimary_LiveMissFailsClosed()
        {
            CelestialBody kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            const string originId = "committed-origin-with-valid-points";
            const string activeId = "active-provisional-empty";

            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = originId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        latitude = 0.0,
                        longitude = 0.0,
                        altitude = 0.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                    },
                    new TrajectoryPoint
                    {
                        ut = 110.0,
                        latitude = 0.0,
                        longitude = 0.0,
                        altitude = 0.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                    },
                },
            });

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-live-primary-miss",
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                InPlaceContinuation = true,
            };
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = marker,
            });

            ParsekFlight.ActiveInPlaceReFlyPrimaryReadModelResolverForTesting =
                (string requestedRecordingId, ReFlySessionMarker activeMarker, double ut,
                    out Recording recording,
                    out ParsekFlight.ActiveReFlyPrimaryReadModelStats stats,
                    out string reason) =>
                {
                    recording = null;
                    reason = "live-recording-has-no-points";
                    stats = new ParsekFlight.ActiveReFlyPrimaryReadModelStats
                    {
                        OriginRecordingId = activeMarker.OriginChildRecordingId,
                        ActiveRecordingId = activeMarker.ActiveReFlyRecordingId,
                    };
                    return false;
                };

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                originId,
                105.0,
                kerbin,
                out Vector3d worldPos);

            Assert.False(ok);
            Assert.Equal(0.0, worldPos.x);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("Active Re-Fly origin primary live trajectory unavailable")
                && l.Contains("live-recording-has-no-points")
                && l.Contains(originId)
                && l.Contains(activeId));
        }

        [Fact]
        public void StandaloneWorldPosition_InPlaceReFlyOriginPrimary_ProductionPathUsesActiveTreePrefixAfterReload()
        {
            CelestialBody kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            FlightRecorder.FrameCountProviderForTesting = () => 10;
            const string originId = "committed-origin-reload";
            const string activeId = "active-provisional-reload";

            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = originId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    AbsolutePoint(100.0, 0.0, 0.0, 0.0, "NoSuchBody"),
                },
            });

            var activeRec = new Recording
            {
                RecordingId = activeId,
                TreeId = "tree-reload",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    AbsolutePoint(100.0, 10.0, 20.0, 1000.0),
                    AbsolutePoint(110.0, 10.0, 20.0, 1000.0),
                },
                TrackSections = new List<TrackSection>
                {
                    AbsoluteSection(
                        AbsolutePoint(100.0, 10.0, 20.0, 1000.0),
                        AbsolutePoint(110.0, 10.0, 20.0, 1000.0)),
                },
            };
            var tree = new RecordingTree
            {
                Id = "tree-reload",
                ActiveRecordingId = activeId,
            };
            tree.AddOrReplaceRecording(activeRec);
            InstallFlightInstanceForReadModel(tree);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess-reload",
                    ActiveReFlyRecordingId = activeId,
                    OriginChildRecordingId = originId,
                    SupersedeTargetId = originId,
                    InPlaceContinuation = true,
                },
            });

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                originId,
                105.0,
                kerbin,
                out Vector3d worldPos);

            Assert.True(ok, string.Join("\n", logLines));
            Assert.False(double.IsNaN(worldPos.x));
            Assert.DoesNotContain(logLines, l => l.Contains("live-recording-has-no-points"));
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("Active Re-Fly origin primary resolved from live recorded trajectory")
                && l.Contains("treePoints=2")
                && l.Contains("recorderPoints=0"));
        }

        [Fact]
        public void StandaloneWorldPosition_InPlaceReFlyOriginPrimary_ProductionPathCachesReadModelPerFrame()
        {
            CelestialBody kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            int frame = 20;
            FlightRecorder.FrameCountProviderForTesting = () => frame;
            const string originId = "committed-origin-cache";
            const string activeId = "active-provisional-cache";

            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = originId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    AbsolutePoint(100.0, 0.0, 0.0, 0.0, "NoSuchBody"),
                },
            });

            var activeRec = new Recording
            {
                RecordingId = activeId,
                TreeId = "tree-cache",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    AbsolutePoint(100.0, 10.0, 20.0, 1000.0),
                    AbsolutePoint(110.0, 10.0, 20.0, 1000.0),
                },
            };
            var tree = new RecordingTree
            {
                Id = "tree-cache",
                ActiveRecordingId = activeId,
            };
            tree.AddOrReplaceRecording(activeRec);
            ParsekFlight host = InstallFlightInstanceForReadModel(tree);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess-cache",
                    ActiveReFlyRecordingId = activeId,
                    OriginChildRecordingId = originId,
                    SupersedeTargetId = originId,
                    InPlaceContinuation = true,
                },
            });

            bool firstOk = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                originId,
                105.0,
                kerbin,
                out Vector3d firstWorld);

            activeRec.Points[0] = AbsolutePoint(100.0, -10.0, -20.0, 2000.0);
            activeRec.Points[1] = AbsolutePoint(110.0, -10.0, -20.0, 2000.0);

            bool sameFrameOk = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                originId,
                105.0,
                kerbin,
                out Vector3d sameFrameWorld);

            frame++;
            bool nextFrameOk = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                originId,
                105.0,
                kerbin,
                out Vector3d nextFrameWorld);

            Assert.True(firstOk, string.Join("\n", logLines));
            Assert.True(sameFrameOk, string.Join("\n", logLines));
            Assert.True(nextFrameOk, string.Join("\n", logLines));
            Assert.Equal(2, host.ActiveReFlyPrimaryReadModelCacheBuildCountForTesting);
            Assert.Equal(firstWorld.x, sameFrameWorld.x, precision: 6);
            Assert.Equal(firstWorld.y, sameFrameWorld.y, precision: 6);
            Assert.Equal(firstWorld.z, sameFrameWorld.z, precision: 6);
        }

        [Fact]
        public void StandaloneWorldPosition_InPlaceReFlyOriginPrimary_ProductionPathResolvesRelativeSection()
        {
            ParsekFlight.AnchorResolverWorldPositionResolverForTesting =
                point => new Vector3d(1000.0, 0.0, 0.0);
            FlightRecorder.FrameCountProviderForTesting = () => 30;
            const string originId = "committed-origin-relative-live";
            const string activeId = "active-provisional-relative-live";
            const string anchorId = "anchor-recording-relative-live";

            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = originId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    AbsolutePoint(100.0, 0.0, 0.0, 0.0, "NoSuchBody"),
                },
            });

            TrajectoryPoint anchorA = AbsolutePoint(100.0, 0.0, 0.0, 1000.0);
            TrajectoryPoint anchorB = AbsolutePoint(110.0, 0.0, 0.0, 1000.0);
            var anchorRec = new Recording
            {
                RecordingId = anchorId,
                TreeId = "tree-relative-live",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint> { anchorA, anchorB },
                TrackSections = new List<TrackSection>
                {
                    AbsoluteSection(anchorA, anchorB),
                },
            };

            var relativeFrames = new List<TrajectoryPoint>
            {
                AbsolutePoint(100.0, 100.0, 0.0, 0.0),
                AbsolutePoint(110.0, 100.0, 0.0, 0.0),
            };
            var activeRec = new Recording
            {
                RecordingId = activeId,
                TreeId = "tree-relative-live",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>(relativeFrames),
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 110.0,
                        referenceFrame = ReferenceFrame.Relative,
                        anchorRecordingId = anchorId,
                        frames = relativeFrames,
                    },
                },
            };
            var tree = new RecordingTree
            {
                Id = "tree-relative-live",
                ActiveRecordingId = activeId,
            };
            tree.AddOrReplaceRecording(anchorRec);
            tree.AddOrReplaceRecording(activeRec);
            InstallFlightInstanceForReadModel(tree);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess-relative-live",
                    ActiveReFlyRecordingId = activeId,
                    OriginChildRecordingId = originId,
                    SupersedeTargetId = originId,
                    InPlaceContinuation = true,
                },
            });

            bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                originId,
                105.0,
                fallbackBody: null,
                out Vector3d worldPos);

            Assert.True(ok, string.Join("\n", logLines));
            Assert.True(Vector3d.Distance(new Vector3d(1100.0, 0.0, 0.0), worldPos) < 0.001);
        }

        [Fact]
        public void ActiveInPlaceReFlyPrimarySnapshot_MergesTreePrefixRecorderTailAndOpenSection()
        {
            const string activeId = "active-provisional-snapshot";
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-snapshot",
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = "origin-snapshot",
                InPlaceContinuation = true,
            };
            var treeRec = new Recording
            {
                RecordingId = activeId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = 101.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 101.0,
                        referenceFrame = ReferenceFrame.Absolute,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                            new TrajectoryPoint { ut = 101.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                        },
                    },
                },
            };
            var recorderPoints = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 101.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                new TrajectoryPoint { ut = 102.0, bodyName = "Kerbin", rotation = Quaternion.identity },
            };
            var recorderSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 101.0,
                    endUT = 102.0,
                    referenceFrame = ReferenceFrame.Absolute,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 101.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                        new TrajectoryPoint { ut = 102.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                    },
                },
            };
            var openSection = new TrackSection
            {
                startUT = 102.0,
                endUT = 0.0,
                referenceFrame = ReferenceFrame.Absolute,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 102.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = 103.0, bodyName = "Kerbin", rotation = Quaternion.identity },
                },
            };

            bool ok = ParsekFlight.TryBuildActiveInPlaceReFlyPrimarySnapshot(
                marker,
                treeRec,
                recorderPoints,
                recorderSections,
                openSection,
                out Recording snapshot,
                out ParsekFlight.ActiveReFlyPrimaryReadModelStats stats,
                out string reason);

            Assert.True(ok, reason);
            Assert.Equal(activeId, snapshot.RecordingId);
            Assert.Equal(new[] { 100.0, 101.0, 102.0 }, snapshot.Points.ConvertAll(p => p.ut));
            Assert.Equal(3, snapshot.TrackSections.Count);
            Assert.Equal(103.0, snapshot.TrackSections[2].endUT);
            Assert.Equal(2, stats.TreePointCount);
            Assert.Equal(2, stats.RecorderPointCount);
            Assert.Equal(2, stats.OpenSectionPointCount);
            Assert.Equal(3, stats.SnapshotPointCount);
        }

        [Fact]
        public void TryResolveActiveReFlyPidStatic_FromCommittedRecordings_ReturnsPid()
        {
            // P1-C support test: the static helper used by the standalone
            // RELATIVE resolver must resolve the active re-fly target's PID
            // by walking RecordingStore — the instance-method
            // TryResolveActiveReFlyPid is unreachable from the static
            // standalone path (no ParsekFlight.Instance in xUnit / scene-
            // less environments).
            const uint expectedPid = 8675309u;
            var rec = new Recording
            {
                RecordingId = "active-refly-rec",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselPersistentId = expectedPid,
            };
            RecordingStore.AddCommittedInternal(rec);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-refly-pid-test",
                ActiveReFlyRecordingId = rec.RecordingId,
                OriginChildRecordingId = rec.RecordingId,
            };
            bool resolved = ParsekFlight.TryResolveActiveReFlyPidStatic(marker, out uint pid);
            Assert.True(resolved);
            Assert.Equal(expectedPid, pid);
        }

        [Fact]
        public void StandaloneWorldPosition_RelativeFrameActiveReFlyPrimary_UsesBodyFixedPrimary()
        {
            // P1-C regression: when the section's anchorVesselId matches
            // the active re-fly target's PID, the standalone resolver
            // must NOT call the live-anchor relative resolver (which
            // would drag the primary ghost with the player's controls
            // — the Naive Relative Trap §3.4). Instead it must lerp
            // the section's bodyFixedFrames shadow.
            //
            // This test passes a section whose anchorVesselId matches an
            // active re-fly target's PID. The recording has
            // bodyFixedFrames at sentinel coordinates that, when world-
            // resolved through the Kerbin body, produce a deterministic
            // position. We assert the helper returns true with that
            // position — proving it took the body-fixed-primary path
            // BEFORE the Instance null-check (the test has no
            // ParsekFlight.Instance, so any other path would return
            // false).
            const uint reflyPid = 12345u;

            // Active re-fly target recording (its presence in
            // CommittedRecordings + its VesselPersistentId is what the
            // static PID resolver looks up via the marker's
            // ActiveReFlyRecordingId).
            var reflyRec = new Recording
            {
                RecordingId = "active-refly-target",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselPersistentId = reflyPid,
            };
            RecordingStore.AddCommittedInternal(reflyRec);

            // Primary recording with a v7 RELATIVE section + bodyFixedFrames
            // shadow. The bodyFixedFrames bracketing UT=105 carry the
            // body-fixed lat/lon/alt the resolver lerps + lifts to world.
            var shadowBefore = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 10.0, longitude = 20.0, altitude = 30000.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
            };
            var shadowAfter = new TrajectoryPoint
            {
                ut = 110.0,
                latitude = 10.0, longitude = 20.0, altitude = 30000.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
            };
            // RELATIVE-frame metre-offsets in the main Points (so a
            // pre-fix lat/lon/alt-as-degrees lift would land underground;
            // we compare the result to the shadow-derived position to
            // prove the shadow path won, not the offset misread).
            var rec = new Recording
            {
                RecordingId = "primary-active-refly-relative",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        latitude = 12.5, longitude = 7.0, altitude = 3.0, // metres dx/dy/dz
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                    },
                    new TrajectoryPoint
                    {
                        ut = 110.0,
                        latitude = 13.0, longitude = 7.5, altitude = 3.2,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
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
                        anchorVesselId = reflyPid,    // SAME PID as active re-fly target
                        sampleRateHz = 4.0f,
                        bodyFixedFrames = new List<TrajectoryPoint> { shadowBefore, shadowAfter },
                    }
                }
            };
            RecordingStore.AddCommittedInternal(rec);

            // Wire the active re-fly marker.
            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess-p1c",
                    ActiveReFlyRecordingId = reflyRec.RecordingId,
                    OriginChildRecordingId = reflyRec.RecordingId,
                    TreeId = "tree-p1c",
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            try
            {
                bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                    rec.RecordingId, 105.0, fallbackBody: null,
                    out Vector3d worldPos);

                // FlightGlobals.Bodies is unavailable in xUnit, so the
                // body resolver inside TryComputeStandaloneBodyFixedPrimary
                // returns null and the helper returns false — but the
                // critical assertion is that the live-anchor path was NOT
                // taken. v11 data without anchorRecordingId is fenced as a
                // format bug after trying the caller-owned shadow.
                Assert.False(ok);
                Assert.DoesNotContain(logLines, l => l.Contains("[Pipeline-CoBubble]")
                    && l.Contains("ParsekFlight.Instance null")
                    && l.Contains(rec.RecordingId));
                Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                    && l.Contains("anchor-recording-id-missing")
                    && l.Contains(rec.RecordingId));
                Assert.DoesNotContain(logLines, l => l.Contains("[Pipeline-CoBubble]")
                    && l.Contains("legacyAnchorPid=12345"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
            }
        }

        [Fact]
        public void StandaloneBodyFixedPrimary_UTPastEnd_FailsClosed()
        {
            // Phase 5 review-pass-3 P2-1 regression: the linear search in
            // TryComputeStandaloneBodyFixedPrimaryWorldPosition (and its
            // siblings TryComputeStandaloneAbsoluteFallbackWorldPosition
            // / TryComputeStandaloneWorldPositionForRecording's lat/lon
            // branch) used `idx <= 0` to detect both at-or-before-start
            // (idx == 0) and past-end (idx == -1). When ut > the last
            // sample's UT, the loop completes without breaking and idx
            // stays -1; the pre-fix code clamped to shadow[0], producing
            // an early-recording position when the caller asked for a
            // late one — a silent time jump.
            //
            // Fix: distinguish the two cases. idx == -1 fails closed with
            // a Verbose; idx == 0 keeps the existing at-or-before-start
            // clamp. This test exercises the body-fixed-primary path
            // through the active-re-fly branch with a synthetic shadow
            // whose last sample is at ut=30 and a query at ut=35.
            const uint reflyPid = 4242u;

            var reflyRec = new Recording
            {
                RecordingId = "active-refly-target-pastend",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselPersistentId = reflyPid,
            };
            RecordingStore.AddCommittedInternal(reflyRec);

            // Section spans UTs [10, 50] (so the query at 35 selects this
            // RELATIVE section), but the body-fixed-primary ends at 30 — the
            // canonical bug case where the recorder stopped capturing
            // shadow points partway through the section. Shadow walk at
            // ut=35 hits idx==-1 (past last body-fixed sample), and the fix
            // must fail closed instead of clamping to shadow[0].
            var rec = new Recording
            {
                RecordingId = "primary-pastend",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 10.0, latitude = 0, longitude = 0, altitude = 0,
                        bodyName = "Kerbin", rotation = Quaternion.identity,
                    },
                    new TrajectoryPoint
                    {
                        ut = 50.0, latitude = 0, longitude = 0, altitude = 0,
                        bodyName = "Kerbin", rotation = Quaternion.identity,
                    },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 10.0,
                        endUT = 50.0,
                        referenceFrame = ReferenceFrame.Relative,
                        source = TrackSectionSource.Active,
                        anchorVesselId = reflyPid,
                        sampleRateHz = 4.0f,
                        bodyFixedFrames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 10.0, latitude = 0, longitude = 0, altitude = 0, bodyName = "Kerbin", rotation = Quaternion.identity },
                            new TrajectoryPoint { ut = 20.0, latitude = 0, longitude = 0, altitude = 0, bodyName = "Kerbin", rotation = Quaternion.identity },
                            new TrajectoryPoint { ut = 30.0, latitude = 0, longitude = 0, altitude = 0, bodyName = "Kerbin", rotation = Quaternion.identity },
                        },
                    }
                }
            };
            RecordingStore.AddCommittedInternal(rec);

            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess-pastend",
                    ActiveReFlyRecordingId = reflyRec.RecordingId,
                    OriginChildRecordingId = reflyRec.RecordingId,
                    TreeId = "tree-pastend",
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            try
            {
                bool ok = ParsekFlight.TryComputeStandaloneWorldPositionForRecording(
                    rec.RecordingId, ut: 35.0, fallbackBody: null,
                    out Vector3d worldPos);

                // Past end → fail closed.
                Assert.False(ok);
                // The shadow-past-end Verbose must fire (canonical pin:
                // pre-fix the code clamped to shadow[0] and returned
                // true with a wrong position; post-fix it returns false
                // and emits this log). Match the visible log text:
                // "body-fixed primary exhausted: recording=...".
                Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                    && l.Contains("body-fixed primary exhausted")
                    && l.Contains(rec.RecordingId));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
            }
        }
    }
}
