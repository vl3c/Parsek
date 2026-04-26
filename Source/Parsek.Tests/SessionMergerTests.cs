using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SessionMergerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SessionMergerTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region Helpers

        /// <summary>
        /// Creates a TrackSection with trajectory frames at start and end UT.
        /// Position is specified as (lat, lon, alt) for boundary discontinuity testing.
        /// </summary>
        private static TrackSection MakeSection(
            double startUT, double endUT,
            TrackSectionSource source = TrackSectionSource.Active,
            double lat = 0.0, double lon = 0.0, double alt = 70000.0,
            double endLat = double.NaN, double endLon = double.NaN, double endAlt = double.NaN,
            string bodyName = "Kerbin",
            ReferenceFrame referenceFrame = ReferenceFrame.Absolute)
        {
            if (double.IsNaN(endLat)) endLat = lat;
            if (double.IsNaN(endLon)) endLon = lon;
            if (double.IsNaN(endAlt)) endAlt = alt;

            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = startUT,
                    latitude = lat, longitude = lon, altitude = alt,
                    bodyName = bodyName,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                },
                new TrajectoryPoint
                {
                    ut = endUT,
                    latitude = endLat, longitude = endLon, altitude = endAlt,
                    bodyName = bodyName,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                }
            };

            return new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = referenceFrame,
                startUT = startUT,
                endUT = endUT,
                source = source,
                sampleRateHz = 10f,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };
        }

        /// <summary>
        /// Creates a TrackSection with no trajectory frames (checkpoint-only).
        /// </summary>
        private static TrackSection MakeSectionNoFrames(
            double startUT, double endUT,
            TrackSectionSource source = TrackSectionSource.Checkpoint)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = startUT,
                endUT = endUT,
                source = source,
                sampleRateHz = 0f,
                frames = null,
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };
        }

        private static PartEvent MakePartEvent(
            double ut, uint pid, PartEventType eventType, string partName = "part")
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = pid,
                eventType = eventType,
                partName = partName,
                value = 0f,
                moduleIndex = 0
            };
        }

        private static Recording MakeRecording(
            string id, string vesselName, List<TrackSection> sections,
            List<PartEvent> events = null)
        {
            var rec = new Recording();
            rec.RecordingId = id;
            rec.VesselName = vesselName;
            rec.TreeId = "test-tree";
            rec.VesselPersistentId = 12345;
            rec.TrackSections = sections ?? new List<TrackSection>();
            rec.PartEvents = events ?? new List<PartEvent>();
            return rec;
        }

        private static RecordingTree MakeTree(
            string name, params Recording[] recordings)
        {
            var tree = new RecordingTree();
            tree.Id = "tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            tree.TreeName = name;
            for (int i = 0; i < recordings.Length; i++)
            {
                tree.Recordings[recordings[i].RecordingId] = recordings[i];
            }
            if (recordings.Length > 0)
            {
                tree.RootRecordingId = recordings[0].RecordingId;
                tree.ActiveRecordingId = recordings[0].RecordingId;
            }
            return tree;
        }

        #endregion

        #region ResolveOverlaps — no overlap

        [Fact]
        public void ResolveOverlaps_NoOverlap_ThreeSections_OutputUnchanged()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(100, 200, TrackSectionSource.Background),
                MakeSection(200, 300, TrackSectionSource.Checkpoint)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(3, result.Count);
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(100, result[0].endUT);
            Assert.Equal(TrackSectionSource.Active, result[0].source);

            Assert.Equal(100, result[1].startUT);
            Assert.Equal(200, result[1].endUT);
            Assert.Equal(TrackSectionSource.Background, result[1].source);

            Assert.Equal(200, result[2].startUT);
            Assert.Equal(300, result[2].endUT);
            Assert.Equal(TrackSectionSource.Checkpoint, result[2].source);
        }

        #endregion

        #region ResolveOverlaps — Active beats Background

        [Fact]
        public void ResolveOverlaps_ActiveBeatsBackground()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(50, 150, TrackSectionSource.Background)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(2, result.Count);
            // Active [0-100] preserved
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(100, result[0].endUT);
            Assert.Equal(TrackSectionSource.Active, result[0].source);
            // Background trimmed to [100-150]
            Assert.Equal(100, result[1].startUT);
            Assert.Equal(150, result[1].endUT);
            Assert.Equal(TrackSectionSource.Background, result[1].source);
        }

        #endregion

        #region ResolveOverlaps — Active beats Checkpoint

        [Fact]
        public void ResolveOverlaps_ActiveBeatsCheckpoint()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(0, 200, TrackSectionSource.Checkpoint)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(2, result.Count);
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(100, result[0].endUT);
            Assert.Equal(TrackSectionSource.Active, result[0].source);

            Assert.Equal(100, result[1].startUT);
            Assert.Equal(200, result[1].endUT);
            Assert.Equal(TrackSectionSource.Checkpoint, result[1].source);
        }

        #endregion

        #region ResolveOverlaps — Background beats Checkpoint

        [Fact]
        public void ResolveOverlaps_BackgroundBeatsCheckpoint()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Background),
                MakeSection(0, 200, TrackSectionSource.Checkpoint)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(2, result.Count);
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(100, result[0].endUT);
            Assert.Equal(TrackSectionSource.Background, result[0].source);

            Assert.Equal(100, result[1].startUT);
            Assert.Equal(200, result[1].endUT);
            Assert.Equal(TrackSectionSource.Checkpoint, result[1].source);
        }

        #endregion

        #region ResolveOverlaps — Active island in Background

        [Fact]
        public void ResolveOverlaps_ActiveIslandInBackground()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 200, TrackSectionSource.Background),
                MakeSection(50, 150, TrackSectionSource.Active)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(3, result.Count);

            // Background [0-50]
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(50, result[0].endUT);
            Assert.Equal(TrackSectionSource.Background, result[0].source);

            // Active [50-150]
            Assert.Equal(50, result[1].startUT);
            Assert.Equal(150, result[1].endUT);
            Assert.Equal(TrackSectionSource.Active, result[1].source);

            // Background [150-200]
            Assert.Equal(150, result[2].startUT);
            Assert.Equal(200, result[2].endUT);
            Assert.Equal(TrackSectionSource.Background, result[2].source);
        }

        [Fact]
        public void ResolveOverlaps_TrimsRelativeAbsoluteShadowWithFrames()
        {
            TrackSection background = MakeSection(
                0, 200, TrackSectionSource.Background,
                referenceFrame: ReferenceFrame.Relative);
            background.anchorVesselId = 1234u;
            background.frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 0, latitude = 0, longitude = 0, altitude = 0, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 50, latitude = 1, longitude = 0, altitude = 0, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 150, latitude = 2, longitude = 0, altitude = 0, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 200, latitude = 3, longitude = 0, altitude = 0, bodyName = "Kerbin" }
            };
            background.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 0, latitude = -0.1, longitude = -74.6, altitude = 70000, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 50, latitude = -0.2, longitude = -74.5, altitude = 71000, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 150, latitude = -0.3, longitude = -74.4, altitude = 72000, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 200, latitude = -0.4, longitude = -74.3, altitude = 73000, bodyName = "Kerbin" }
            };

            var sections = new List<TrackSection>
            {
                background,
                MakeSection(50, 150, TrackSectionSource.Active)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(3, result.Count);
            Assert.Equal(new[] { 0.0, 50.0 }, result[0].absoluteFrames.Select(p => p.ut).ToArray());
            Assert.Equal(new[] { 150.0, 200.0 }, result[2].absoluteFrames.Select(p => p.ut).ToArray());
        }

        #endregion

        #region ResolveOverlaps — gap between sections

        [Fact]
        public void ResolveOverlaps_GapBetweenSections_Preserved()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(200, 300, TrackSectionSource.Background)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(2, result.Count);
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(100, result[0].endUT);
            Assert.Equal(200, result[1].startUT);
            Assert.Equal(300, result[1].endUT);
        }

        #endregion

        #region ResolveOverlaps — empty input

        [Fact]
        public void ResolveOverlaps_EmptyInput_EmptyOutput()
        {
            var result = SessionMerger.ResolveOverlaps(new List<TrackSection>());
            Assert.Empty(result);
        }

        [Fact]
        public void ResolveOverlaps_NullInput_EmptyOutput()
        {
            var result = SessionMerger.ResolveOverlaps(null);
            Assert.Empty(result);
        }

        #endregion

        #region ResolveOverlaps — single section

        [Fact]
        public void ResolveOverlaps_SingleSection_OutputUnchanged()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(17000, 17100, TrackSectionSource.Active)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Single(result);
            Assert.Equal(17000, result[0].startUT);
            Assert.Equal(17100, result[0].endUT);
            Assert.Equal(TrackSectionSource.Active, result[0].source);
        }

        #endregion

        #region Boundary discontinuity computation

        [Fact]
        public void ComputeBoundaryDiscontinuity_DifferentPositions_CorrectMeters()
        {
            // Two sections with different end/start positions
            var prev = MakeSection(0, 100, TrackSectionSource.Active,
                lat: 0.0, lon: 0.0, alt: 70000,
                endLat: 0.0, endLon: 0.0, endAlt: 70000);
            var next = MakeSection(100, 200, TrackSectionSource.Background,
                lat: 0.0, lon: 0.0, alt: 70100);

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);

            // Altitude difference of 100m — should be ~100m
            Assert.True(disc > 90f && disc < 110f,
                $"Expected ~100m discontinuity, got {disc}m");
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_SamePosition_ZeroMeters()
        {
            var prev = MakeSection(0, 100, TrackSectionSource.Active,
                lat: 0.0, lon: 0.0, alt: 70000,
                endLat: 0.0, endLon: 0.0, endAlt: 70000);
            var next = MakeSection(100, 200, TrackSectionSource.Background,
                lat: 0.0, lon: 0.0, alt: 70000);

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);

            Assert.Equal(0f, disc);
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_NoFramesPrev_ReturnsZero()
        {
            var prev = MakeSectionNoFrames(0, 100);
            var next = MakeSection(100, 200, TrackSectionSource.Active);

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);

            Assert.Equal(0f, disc);
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_NoFramesNext_ReturnsZero()
        {
            var prev = MakeSection(0, 100, TrackSectionSource.Active);
            var next = MakeSectionNoFrames(100, 200);

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);

            Assert.Equal(0f, disc);
        }

        #endregion

        #region ResolveOverlaps — boundary discontinuity set at junctions

        [Fact]
        public void ResolveOverlaps_ComputesDiscontinuityAtJunctions()
        {
            // Two adjacent sections with different positions → discontinuity computed
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active,
                    lat: 0.0, lon: 0.0, alt: 70000,
                    endLat: 0.0, endLon: 0.0, endAlt: 70000),
                MakeSection(100, 200, TrackSectionSource.Background,
                    lat: 0.0, lon: 0.0, alt: 70500)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(2, result.Count);
            Assert.Equal(0f, result[0].boundaryDiscontinuityMeters); // First section: no prior
            Assert.True(result[1].boundaryDiscontinuityMeters > 400f,
                $"Expected >400m discontinuity, got {result[1].boundaryDiscontinuityMeters}m");
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_SharedBoundaryPoint_ZeroGap()
        {
            // When the last frame of section N and first frame of section N+1 are
            // the same point (boundary seeding fix #283), discontinuity should be 0.
            var sharedPoint = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = -0.0972, longitude = -74.5575, altitude = 70000,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            };
            var prev = MakeSection(0, 100);
            prev.frames[prev.frames.Count - 1] = sharedPoint;

            var next = MakeSection(100, 200);
            next.frames[0] = sharedPoint;

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);
            Assert.Equal(0f, disc);
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_CrossReferenceFrame_ReturnsZero()
        {
            // ABSOLUTE→RELATIVE boundary: lat/lon/alt have different semantics,
            // so comparison is skipped (#283).
            var prev = MakeSection(0, 100, TrackSectionSource.Active,
                lat: -0.0972, lon: -74.5575, alt: 70000,
                referenceFrame: ReferenceFrame.Absolute);
            var next = MakeSection(100, 200, TrackSectionSource.Active,
                lat: 50.0, lon: 20.0, alt: 100.0,  // relative offsets (meters)
                referenceFrame: ReferenceFrame.Relative);

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);
            Assert.Equal(0f, disc);
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_SameReferenceFrame_ComputesDistance()
        {
            // Same reference frame (both ABSOLUTE) with real position gap → non-zero.
            var prev = MakeSection(0, 100, TrackSectionSource.Active,
                lat: 0, lon: 0, alt: 70000,
                endLat: 0, endLon: 0, endAlt: 70000);
            var next = MakeSection(100, 200, TrackSectionSource.Active,
                lat: 0, lon: 0, alt: 70500);

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);
            Assert.True(disc > 400f,
                $"Expected >400m for 500m altitude gap, got {disc}m");
        }

        [Fact]
        public void ResolveOverlaps_CrossReferenceFrameBoundary_ZeroDiscontinuity()
        {
            // ABSOLUTE section followed by RELATIVE section — discontinuity should be 0
            // because cross-frame comparisons are skipped.
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active,
                    lat: -0.0972, lon: -74.5575, alt: 70000,
                    referenceFrame: ReferenceFrame.Absolute),
                MakeSection(100, 200, TrackSectionSource.Active,
                    lat: 50.0, lon: 20.0, alt: 100.0,
                    referenceFrame: ReferenceFrame.Relative)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Equal(2, result.Count);
            Assert.Equal(0f, result[1].boundaryDiscontinuityMeters);
        }

        #endregion

        #region PartEvent deduplication

        [Fact]
        public void MergePartEvents_DuplicateEvent_Deduplicated()
        {
            var evA = new List<PartEvent>
            {
                MakePartEvent(100.0, 42, PartEventType.EngineIgnited),
                MakePartEvent(200.0, 42, PartEventType.EngineShutdown)
            };
            var evB = new List<PartEvent>
            {
                MakePartEvent(100.0, 42, PartEventType.EngineIgnited), // duplicate
                MakePartEvent(300.0, 43, PartEventType.Decoupled)
            };

            var result = SessionMerger.MergePartEvents(evA, evB);

            Assert.Equal(3, result.Count); // 4 input - 1 duplicate = 3
        }

        [Fact]
        public void MergePartEvents_SameUtDifferentParts_BothKept()
        {
            var evA = new List<PartEvent>
            {
                MakePartEvent(100.0, 42, PartEventType.EngineIgnited)
            };
            var evB = new List<PartEvent>
            {
                MakePartEvent(100.0, 43, PartEventType.EngineIgnited) // different pid
            };

            var result = SessionMerger.MergePartEvents(evA, evB);

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void MergePartEvents_SameUtSamePartDifferentType_BothKept()
        {
            var evA = new List<PartEvent>
            {
                MakePartEvent(100.0, 42, PartEventType.EngineIgnited)
            };
            var evB = new List<PartEvent>
            {
                MakePartEvent(100.0, 42, PartEventType.EngineShutdown) // different type
            };

            var result = SessionMerger.MergePartEvents(evA, evB);

            Assert.Equal(2, result.Count);
        }

        #endregion

        #region PartEvent ordering

        [Fact]
        public void MergePartEvents_Ordering_SortedByUT()
        {
            var evA = new List<PartEvent>
            {
                MakePartEvent(300.0, 42, PartEventType.EngineShutdown),
                MakePartEvent(100.0, 42, PartEventType.EngineIgnited)
            };
            var evB = new List<PartEvent>
            {
                MakePartEvent(200.0, 43, PartEventType.Decoupled)
            };

            var result = SessionMerger.MergePartEvents(evA, evB);

            Assert.Equal(3, result.Count);
            Assert.True(result[0].ut <= result[1].ut);
            Assert.True(result[1].ut <= result[2].ut);
            Assert.Equal(100.0, result[0].ut);
            Assert.Equal(200.0, result[1].ut);
            Assert.Equal(300.0, result[2].ut);
        }

        #endregion

        #region MergeTree — two recordings

        [Fact]
        public void MergeTree_TwoRecordings_BothMerged()
        {
            var rec1 = MakeRecording("rec-1", "Vessel Alpha",
                new List<TrackSection> { MakeSection(0, 100, TrackSectionSource.Active) },
                new List<PartEvent> { MakePartEvent(50, 42, PartEventType.EngineIgnited) });

            var rec2 = MakeRecording("rec-2", "Vessel Beta",
                new List<TrackSection> { MakeSection(0, 200, TrackSectionSource.Background) },
                new List<PartEvent> { MakePartEvent(100, 43, PartEventType.Decoupled) });

            var tree = MakeTree("Test Mission", rec1, rec2);

            var result = SessionMerger.MergeTree(tree);

            Assert.Equal(2, result.Count);
            Assert.True(result.ContainsKey("rec-1"));
            Assert.True(result.ContainsKey("rec-2"));

            Assert.Single(result["rec-1"].TrackSections);
            Assert.Single(result["rec-1"].PartEvents);
            Assert.Single(result["rec-2"].TrackSections);
            Assert.Single(result["rec-2"].PartEvents);
        }

        #endregion

        #region MergeTree — null and empty

        [Fact]
        public void MergeTree_NullTree_EmptyResult()
        {
            var result = SessionMerger.MergeTree(null);
            Assert.Empty(result);
        }

        [Fact]
        public void MergeTree_EmptyTree_EmptyResult()
        {
            var tree = new RecordingTree();
            tree.Id = "empty";
            tree.TreeName = "Empty";

            var result = SessionMerger.MergeTree(tree);
            Assert.Empty(result);
        }

        #endregion

        #region MergeTree — preserves recording metadata

        [Fact]
        public void MergeTree_PreservesVesselNameAndId()
        {
            var rec = MakeRecording("rec-x", "My Rocket",
                new List<TrackSection> { MakeSection(0, 100) });
            rec.VesselPersistentId = 99999;
            rec.TreeId = "tree-abc";
            rec.TerminalStateValue = TerminalState.Orbiting;

            var tree = MakeTree("Metadata Test", rec);

            var result = SessionMerger.MergeTree(tree);

            Assert.Single(result);
            var merged = result["rec-x"];
            Assert.Equal("My Rocket", merged.VesselName);
            Assert.Equal("rec-x", merged.RecordingId);
            Assert.Equal((uint)99999, merged.VesselPersistentId);
            Assert.Equal("tree-abc", merged.TreeId);
            Assert.Equal(TerminalState.Orbiting, merged.TerminalStateValue);
        }

        [Fact]
        public void MergeTree_RebuildsFlatTrajectoryFromAbsoluteTrackSections()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active, lat: 0.0, lon: 0.0, alt: 1000.0),
                MakeSection(150, 200, TrackSectionSource.Background, lat: 0.1, lon: 0.1, alt: 1200.0)
            };
            var rec = MakeRecording("rec-sync", "Sync Vessel", sections);
            rec.Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 999, latitude = 9, longitude = 9, altitude = 9, bodyName = "Kerbin" }
            };

            var tree = MakeTree("Flat Sync", rec);

            var result = SessionMerger.MergeTree(tree);
            var merged = result["rec-sync"];

            Assert.Equal(4, merged.Points.Count);
            Assert.Equal(0.0, merged.Points[0].ut);
            Assert.Equal(100.0, merged.Points[1].ut);
            Assert.Equal(150.0, merged.Points[2].ut);
            Assert.Equal(200.0, merged.Points[3].ut);
        }

        [Fact]
        public void MergeTree_PreservesFlatTailWhenTrackSectionsAreStale()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 10, TrackSectionSource.Active),
                MakeSection(10, 15, TrackSectionSource.Background)
            };
            var rec = MakeRecording("rec-stale", "Tail Vessel", sections);
            rec.Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 0, latitude = 0, longitude = 0, altitude = 70000,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                },
                new TrajectoryPoint
                {
                    ut = 10, latitude = 0, longitude = 0, altitude = 70000,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                },
                new TrajectoryPoint
                {
                    ut = 15, latitude = 0, longitude = 0, altitude = 70000,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                },
                new TrajectoryPoint
                {
                    ut = 20, latitude = 0, longitude = 0, altitude = 70000,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                }
            };

            var tree = MakeTree("Stale Sections", rec);

            var result = SessionMerger.MergeTree(tree);
            var merged = result["rec-stale"];

            Assert.Equal(4, merged.Points.Count);
            Assert.Equal(0.0, merged.Points[0].ut);
            Assert.Equal(10.0, merged.Points[1].ut);
            Assert.Equal(15.0, merged.Points[2].ut);
            Assert.Equal(20.0, merged.Points[3].ut);
            Assert.Equal(2, merged.TrackSections.Count);
        }

        [Fact]
        public void MergeTree_NonMonotonicFlatTail_RebuildsFromTrackSectionsInsteadOfPreservingBadCopy()
        {
            var section = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0.0,
                endUT = 20.0,
                source = TrackSectionSource.Background,
                sampleRateHz = 10f,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 0.0,
                        latitude = 0.0,
                        longitude = 0.0,
                        altitude = 100.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = 10.0,
                        latitude = 0.1,
                        longitude = 0.1,
                        altitude = 110.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = 20.0,
                        latitude = 0.2,
                        longitude = 0.2,
                        altitude = 120.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    }
                },
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };

            var rec = MakeRecording("rec-bad-tail", "Bad Tail Vessel", new List<TrackSection> { section });
            rec.Points = new List<TrajectoryPoint>(section.frames);
            rec.Points.AddRange(section.frames);

            var tree = MakeTree("Bad Tail", rec);

            var result = SessionMerger.MergeTree(tree);
            var merged = result["rec-bad-tail"];

            Assert.Equal(new[] { 0.0, 10.0, 20.0 }, merged.Points.Select(p => p.ut).ToArray());
            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") &&
                l.Contains("recording='rec-bad-tail'") &&
                l.Contains("flatSync=track-sections"));
        }

        #endregion

        #region Log assertions — overlap count

        [Fact]
        public void MergeTree_LogsOverlapCount()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(50, 150, TrackSectionSource.Background)
            };
            var rec = MakeRecording("rec-log", "Logger Vessel", sections);
            var tree = MakeTree("Log Test", rec);

            SessionMerger.MergeTree(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") && l.Contains("overlapsResolved="));
        }

        #endregion

        #region Log assertions — source distribution

        [Fact]
        public void MergeTree_LogsSourceDistribution()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(100, 200, TrackSectionSource.Background),
                MakeSection(200, 300, TrackSectionSource.Checkpoint)
            };
            var rec = MakeRecording("rec-dist", "Dist Vessel", sections);
            var tree = MakeTree("Distribution Test", rec);

            SessionMerger.MergeTree(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") &&
                l.Contains("active=1") &&
                l.Contains("background=1") &&
                l.Contains("checkpoint=1"));
        }

        #endregion

        #region Log assertions — discontinuity warning

        [Fact]
        public void MergeTree_DiscontiniutyAbove1m_LogsWarning()
        {
            // Two adjacent sections with a 500m altitude gap
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active,
                    lat: 0, lon: 0, alt: 70000,
                    endLat: 0, endLon: 0, endAlt: 70000),
                MakeSection(100, 200, TrackSectionSource.Background,
                    lat: 0, lon: 0, alt: 70500)
            };
            var rec = MakeRecording("rec-disc", "Disc Vessel", sections);
            var tree = MakeTree("Disc Test", rec);

            SessionMerger.MergeTree(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity=") &&
                l.Contains("prevRef=") && l.Contains("nextRef=") &&
                l.Contains("prevSrc=") && l.Contains("nextSrc="));
        }

        [Fact]
        public void MergeTree_CrossReferenceFrameBoundary_NoDiscontinuityWarning()
        {
            // ABSOLUTE→RELATIVE boundary with large coordinate difference.
            // Should NOT produce a warning because cross-frame comparisons are skipped (#283).
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active,
                    lat: -0.0972, lon: -74.5575, alt: 70000,
                    referenceFrame: ReferenceFrame.Absolute),
                MakeSection(100, 200, TrackSectionSource.Active,
                    lat: 999.0, lon: 999.0, alt: 999.0,
                    referenceFrame: ReferenceFrame.Relative)
            };
            var rec = MakeRecording("rec-xref", "CrossRef Vessel", sections);
            var tree = MakeTree("CrossRef Test", rec);

            SessionMerger.MergeTree(tree);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity="));
        }

        #endregion

        #region Log assertions — no discontinuity warning for small gap

        [Fact]
        public void MergeTree_SmallDiscontinuity_NoWarning()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active,
                    lat: 0, lon: 0, alt: 70000,
                    endLat: 0, endLon: 0, endAlt: 70000),
                MakeSection(100, 200, TrackSectionSource.Background,
                    lat: 0, lon: 0, alt: 70000.5) // 0.5m gap — below threshold
            };
            var rec = MakeRecording("rec-small", "Small Gap", sections);
            var tree = MakeTree("Small Gap Test", rec);

            SessionMerger.MergeTree(tree);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity="));
        }

        #endregion

        #region #449 — boundary discontinuity classification

        /// <summary>
        /// Builds a TrackSection with a single in-section frame at <paramref name="ut"/>
        /// carrying a velocity. Used by the #449 classifier tests so we can drive
        /// dt and expectedFromVel deterministically.
        /// </summary>
        private static TrackSection MakeSectionWithFrame(
            double ut, double lat, double lon, double alt, Vector3 velocity,
            string bodyName = "Kerbin",
            ReferenceFrame referenceFrame = ReferenceFrame.Absolute,
            TrackSectionSource source = TrackSectionSource.Active)
        {
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = ut,
                    latitude = lat, longitude = lon, altitude = alt,
                    bodyName = bodyName,
                    rotation = Quaternion.identity,
                    velocity = velocity
                }
            };
            return new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = referenceFrame,
                startUT = ut,
                endUT = ut,
                source = source,
                sampleRateHz = 10f,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_NoPrev_TaggedNoPrev()
        {
            var next = MakeSectionWithFrame(100, 0, 0, 70000, Vector3.zero);
            SessionMerger.ClassifyBoundaryDiscontinuity(
                default(TrackSection), next, hasPrev: false, discMeters: 50f,
                out double dt, out double expected, out string cause);
            Assert.Equal(0.0, dt);
            Assert.Equal(0.0, expected);
            Assert.Equal("no-prev", cause);
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_ZeroDt_TaggedFrameMismatch()
        {
            // Same UT on both sides — interpolation/source-frame bug, not motion.
            var prev = MakeSectionWithFrame(100, 0, 0, 70000, new Vector3(130, 0, 0));
            var next = MakeSectionWithFrame(100, 0, 0, 70050, new Vector3(130, 0, 0));
            SessionMerger.ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev: true, discMeters: 50f,
                out double dt, out double expected, out string cause);
            Assert.Equal(0.0, dt);
            Assert.Equal("frame-mismatch", cause);
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_GapMatchesVelocity_TaggedUnrecordedGap()
        {
            // #449 reproducer: ~1s of unrecorded vessel motion at ~130 m/s after a
            // quickload-resume produces a ~130 m boundary gap. Should be classified
            // as unrecorded-gap, not as a sample-skip bug.
            var prev = MakeSectionWithFrame(4003.9, 0, 0, 70000, new Vector3(130, 0, 0));
            var next = MakeSectionWithFrame(4004.92, 0, 0.001183, 70000, Vector3.zero);
            SessionMerger.ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev: true, discMeters: 132.82f,
                out double dt, out double expected, out string cause);
            Assert.InRange(dt, 1.01, 1.03);
            Assert.InRange(expected, 130.0, 135.0);
            Assert.Equal("unrecorded-gap", cause);
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_SmallGapLargeJump_TaggedSampleSkip()
        {
            // Tight time gap, but the position jumps far beyond what velocity could
            // explain — points at a dropped-sample / drift bug or src-tag mismatch.
            var prev = MakeSectionWithFrame(100, 0, 0, 70000, new Vector3(10, 0, 0));
            var next = MakeSectionWithFrame(100.5, 0, 0, 70000, Vector3.zero);
            SessionMerger.ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev: true, discMeters: 500f,
                out double dt, out double expected, out string cause);
            Assert.InRange(dt, 0.4, 0.6);
            Assert.InRange(expected, 4.0, 6.0);
            Assert.Equal("sample-skip", cause);
        }

        [Fact]
        public void LooksLikeSaveLoadTeleportBoundary_OverlappingActiveSections_ReturnsTrue()
        {
            var trimmedFutureTail = MakeSection(
                100.0, 110.0, TrackSectionSource.Active,
                alt: 70000.0, endAlt: 70000.0);
            var resumedOverlap = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 105.0,
                endUT = 120.0,
                source = TrackSectionSource.Active,
                sampleRateHz = 10f,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 105.0,
                        altitude = 70005.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = 110.5,
                        altitude = 70500.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = 120.0,
                        altitude = 70510.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    }
                },
                checkpoints = new List<OrbitSegment>()
            };
            var mergedHead = MakeSection(
                110.0, 120.0, TrackSectionSource.Active,
                alt: 70500.0, endAlt: 70510.0);

            Assert.True(SessionMerger.LooksLikeSaveLoadTeleportBoundary(
                new List<TrackSection> { trimmedFutureTail, resumedOverlap },
                trimmedFutureTail,
                mergedHead));
        }

        [Fact]
        public void LooksLikeSaveLoadTeleportBoundary_RunwaySurfaceToAtmoBoundary_ReturnsFalse()
        {
            var runwaySurface = MakeSection(
                100.0, 107.0, TrackSectionSource.Active,
                alt: 70.0, endAlt: 70.0);
            runwaySurface.environment = SegmentEnvironment.SurfaceStationary;

            var takeoffAtmo = MakeSection(
                107.0, 130.0, TrackSectionSource.Active,
                alt: 70.0, endAlt: 1200.0);
            takeoffAtmo.environment = SegmentEnvironment.Atmospheric;

            Assert.False(SessionMerger.LooksLikeSaveLoadTeleportBoundary(
                new List<TrackSection> { runwaySurface, takeoffAtmo },
                runwaySurface,
                takeoffAtmo));
        }

        [Fact]
        public void MergeTree_DiscontinuityWarning_OverlapSeam_TaggedSaveLoadTeleport()
        {
            var futureTail = MakeSection(
                100.0, 110.0, TrackSectionSource.Active,
                alt: 70000.0, endAlt: 70000.0);
            futureTail.frames[futureTail.frames.Count - 1] = new TrajectoryPoint
            {
                ut = 110.0,
                altitude = 70000.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = new Vector3(10f, 0f, 0f)
            };

            var resumed = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 105.0,
                endUT = 120.0,
                source = TrackSectionSource.Active,
                sampleRateHz = 10f,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 105.0,
                        altitude = 70005.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = 110.5,
                        altitude = 70500.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = 120.0,
                        altitude = 70510.0,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero
                    }
                },
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };

            var rec = MakeRecording(
                "rec-486-teleport",
                "Teleport Vessel",
                new List<TrackSection> { futureTail, resumed });
            var tree = MakeTree("486 Teleport", rec);

            SessionMerger.MergeTree(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity=") &&
                l.Contains("cause=save-load-teleport"));
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_StationaryWithFloor_TaggedUnrecordedGap()
        {
            // Stationary vessel (vMag=0) with a small jump under the 5m floor:
            // the floor swallows pure quantization noise so this stays tagged
            // as unrecorded-gap instead of false-positive sample-skip.
            var prev = MakeSectionWithFrame(100, 0, 0, 70000, Vector3.zero);
            var next = MakeSectionWithFrame(101, 0, 0, 70003, Vector3.zero);
            SessionMerger.ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev: true, discMeters: 3f,
                out double dt, out double expected, out string cause);
            Assert.Equal(0.0, expected);
            Assert.Equal("unrecorded-gap", cause);
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_NaNVelocity_TaggedInvalidData()
        {
            // NaN-component velocity means the boundary data itself is broken. Do not
            // fall through to a normal bucket — emit invalid-data so the WARN stays
            // clearly suspicious instead of looking like a recorder gap.
            var prev = MakeSectionWithFrame(100, 0, 0, 70000,
                new Vector3(float.NaN, 0, 0));
            var next = MakeSectionWithFrame(101, 0, 0, 70050, Vector3.zero);
            SessionMerger.ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev: true, discMeters: 50f,
                out double dt, out double expected, out string cause);
            Assert.InRange(dt, 0.99, 1.01);
            Assert.True(double.IsNaN(expected),
                $"Expected NaN expectedMeters with NaN velocity, got {expected}");
            Assert.Equal("invalid-data", cause);
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_InfinityVelocity_TaggedInvalidData()
        {
            // Infinity-component velocity previously forced any discontinuity into the
            // benign unrecorded-gap bucket because the inferred tolerance became
            // Infinity. That hides corrupted boundary data as "expected motion".
            var prev = MakeSectionWithFrame(100, 0, 0, 70000,
                new Vector3(float.PositiveInfinity, 0, 0));
            var next = MakeSectionWithFrame(101, 0, 0, 70050, Vector3.zero);
            SessionMerger.ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev: true, discMeters: 50f,
                out double dt, out double expected, out string cause);
            Assert.InRange(dt, 0.99, 1.01);
            Assert.True(double.IsNaN(expected),
                $"Expected NaN expectedMeters with +Infinity velocity, got {expected}");
            Assert.Equal("invalid-data", cause);
        }

        [Fact]
        public void ClassifyBoundaryDiscontinuity_NonFiniteUT_TaggedInvalidData()
        {
            var prev = MakeSectionWithFrame(100, 0, 0, 70000, new Vector3(130, 0, 0));
            var next = MakeSectionWithFrame(double.NaN, 0, 0, 70050, Vector3.zero);
            SessionMerger.ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev: true, discMeters: 50f,
                out double dt, out double expected, out string cause);
            Assert.True(double.IsNaN(dt),
                $"Expected NaN dt with non-finite next.ut, got {dt}");
            Assert.True(double.IsNaN(expected),
                $"Expected NaN expectedMeters with non-finite UT, got {expected}");
            Assert.Equal("invalid-data", cause);
        }

        // Parameterised so a regression that hard-codes a single cause (e.g. always
        // emits "cause=no-prev") fails for the other rows instead of slipping past
        // a substring-only assertion.
        [Theory]
        [InlineData("unrecorded-gap", 100f, 1.0, 110f)]
        [InlineData("sample-skip",     10f, 0.5, 500f)]
        [InlineData("frame-mismatch", 130f, 0.0,  50f)]
        public void MergeTree_DiscontinuityWarning_IncludesClassification(
            string expectedCause, float prevVelX, double dtSeconds, float altGapMeters)
        {
            // Two adjacent Active sections — the WARN should carry dt=,
            // expectedFromVel=, and the exact cause= value matching the scenario.
            // Boundary UT is where prev ends and next starts; prev's last frame is
            // at boundaryUT, next's first frame is at boundaryUT + dtSeconds, so the
            // computed dt between frames equals dtSeconds.
            const double boundaryUT = 1.0;
            var prev = MakeSectionWithFrame(boundaryUT, 0, 0, 70000,
                new Vector3(prevVelX, 0, 0));
            var next = MakeSectionWithFrame(boundaryUT + dtSeconds, 0, 0,
                70000 + altGapMeters, Vector3.zero, source: TrackSectionSource.Active);
            // MakeSectionWithFrame leaves startUT==endUT (zero duration). Widen each
            // so ResolveOverlaps doesn't drop them as zero-duration sections. The
            // sections must be non-overlapping and ordered (prev before next).
            var prevTail = prev;
            prevTail.startUT = 0.0;
            prevTail.endUT = boundaryUT;
            var nextHead = next;
            nextHead.startUT = boundaryUT + dtSeconds;
            nextHead.endUT = boundaryUT + dtSeconds + 1.0;
            // For dt=0 the two sections share the same boundary UT (prev.endUT ==
            // next.startUT == boundaryUT) — adjacent, not overlapping.
            var rec = MakeRecording("rec-449", "Diag Vessel",
                new List<TrackSection> { prevTail, nextHead });
            var tree = MakeTree("449 Test", rec);

            SessionMerger.MergeTree(tree);

            // Strict: substring match for the exact cause= value (not just "cause=").
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity=") &&
                l.Contains("dt=") &&
                l.Contains("expectedFromVel=") &&
                l.Contains("cause=" + expectedCause));
        }

        [Fact]
        public void MergeTree_DiscontinuityWarning_InvalidData_UsesExactCause()
        {
            const double boundaryUT = 1.0;
            var prev = MakeSectionWithFrame(boundaryUT, 0, 0, 70000,
                new Vector3(float.PositiveInfinity, 0, 0));
            var next = MakeSectionWithFrame(boundaryUT + 1.0, 0, 0, 70050, Vector3.zero);
            var prevTail = prev;
            prevTail.startUT = 0.0;
            prevTail.endUT = boundaryUT;
            var nextHead = next;
            nextHead.startUT = boundaryUT + 1.0;
            nextHead.endUT = boundaryUT + 2.0;
            var rec = MakeRecording("rec-449-invalid", "Diag Vessel",
                new List<TrackSection> { prevTail, nextHead });
            var tree = MakeTree("449 Invalid Data Test", rec);

            SessionMerger.MergeTree(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity=") &&
                l.Contains("expectedFromVel=NaN") &&
                l.Contains("cause=invalid-data"));
        }

        #endregion

        #region #580 — Background to Active unrecorded-gap healing

        [Fact]
        public void MergeTree_BackgroundToActiveUnrecordedGap_InsertsSharedBoundaryPoint()
        {
            // Regresses the largest 2026-04-25 Kerbal X handoff citation:
            // section[1] ut=193985.09, dt=3.36s, discontinuity=4029.13m,
            // expectedFromVel=4128.99m, prevSrc=Background, nextSrc=Active.
            const double boundaryUT = 193985.090668523;
            const double bgTailUT = 193981.75066852474;
            const double activeHeadUT = 193985.11066852298;
            const double bgAlt = 26477.944626131211;
            const double activeAlt = bgAlt - 4029.13013;

            var background = MakeSectionWithFrame(
                bgTailUT, lat: 0, lon: 0, alt: bgAlt,
                velocity: new Vector3(1228.866f, 0f, 0f),
                source: TrackSectionSource.Background);
            background.startUT = 193981.73066852475;
            background.endUT = boundaryUT;

            var active = MakeSectionWithFrame(
                activeHeadUT, lat: 0, lon: 0, alt: activeAlt,
                velocity: Vector3.zero,
                source: TrackSectionSource.Active);
            active.startUT = boundaryUT;
            active.endUT = boundaryUT + 120.0;

            var rec = MakeRecording("rec-580", "Kerbal X",
                new List<TrackSection> { background, active });
            var tree = MakeTree("580 Handoff", rec);

            var mergedById = SessionMerger.MergeTree(tree);

            var merged = mergedById["rec-580"];
            Assert.Equal(2, merged.TrackSections.Count);
            Assert.True(merged.TrackSections[0].frames.Count >= 2);
            Assert.Equal(boundaryUT, merged.TrackSections[0].frames.Last().ut, 6);
            Assert.Equal(boundaryUT, merged.TrackSections[1].frames[0].ut, 6);
            Assert.InRange(merged.TrackSections[1].boundaryDiscontinuityMeters, 0f, 0.01f);
            Assert.Equal(3, merged.Points.Count);
            Assert.Contains(merged.Points, p => Math.Abs(p.ut - boundaryUT) <= 1e-6);

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Merger]") &&
                l.Contains("healed unrecorded-gap") &&
                l.Contains("section[1]") &&
                l.Contains("prevSrc=Background") &&
                l.Contains("nextSrc=Active") &&
                l.Contains("cause=unrecorded-gap #580"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity=") &&
                l.Contains("vessel='Kerbal X'") &&
                l.Contains("cause=unrecorded-gap"));
        }

        [Fact]
        public void MergeTree_BackgroundToActiveUnrecordedGap_PreservesValidatedFlatTail()
        {
            const double boundaryUT = 2.0;

            var background = MakeSectionWithFrame(
                0.0, lat: 0, lon: 0, alt: 100.0,
                velocity: new Vector3(50f, 0f, 0f),
                source: TrackSectionSource.Background);
            background.startUT = 0.0;
            background.endUT = boundaryUT;

            var active = MakeSectionWithFrame(
                3.0, lat: 0, lon: 0, alt: 250.0,
                velocity: Vector3.zero,
                source: TrackSectionSource.Active);
            active.startUT = boundaryUT;
            active.endUT = 4.0;

            var tailPoint = new TrajectoryPoint
            {
                ut = 5.0,
                latitude = 0,
                longitude = 0,
                altitude = 280.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };

            var rec = MakeRecording("rec-580-tail", "Kerbal X",
                new List<TrackSection> { background, active });
            rec.Points = new List<TrajectoryPoint>
            {
                background.frames[0],
                active.frames[0],
                tailPoint
            };
            var tree = MakeTree("580 Handoff Tail", rec);

            var mergedById = SessionMerger.MergeTree(tree);

            var merged = mergedById["rec-580-tail"];
            Assert.Equal(new[] { 0.0, boundaryUT, 3.0, 5.0 },
                merged.Points.Select(p => p.ut).ToArray());
            Assert.Equal(2, merged.TrackSections[0].frames.Count);
            Assert.Equal(2, merged.TrackSections[1].frames.Count);
            Assert.Single(rec.TrackSections[0].frames);
            Assert.Single(rec.TrackSections[1].frames);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Merger]") &&
                l.Contains("healed unrecorded-gap") &&
                l.Contains("section[1]") &&
                l.Contains("cause=unrecorded-gap #580"));
            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") &&
                l.Contains("recording='rec-580-tail'") &&
                l.Contains("flatSync=healed-track-sections-preserved-flat-tail"));
        }

        [Fact]
        public void MergeTree_BackgroundToActiveSampleSkip_LeavesBoundaryWarningUnhealed()
        {
            const double boundaryUT = 1.0;
            var background = MakeSectionWithFrame(
                boundaryUT, lat: 0, lon: 0, alt: 100.0,
                velocity: new Vector3(1f, 0f, 0f),
                source: TrackSectionSource.Background);
            background.startUT = 0.0;
            background.endUT = boundaryUT;

            var active = MakeSectionWithFrame(
                2.0, lat: 0, lon: 0, alt: 1000.0,
                velocity: Vector3.zero,
                source: TrackSectionSource.Active);
            active.startUT = boundaryUT;
            active.endUT = 3.0;

            var rec = MakeRecording("rec-580-sample-skip", "Kerbal X",
                new List<TrackSection> { background, active });
            var tree = MakeTree("580 Sample Skip", rec);

            var merged = SessionMerger.MergeTree(tree)["rec-580-sample-skip"];

            Assert.Single(merged.TrackSections[0].frames);
            Assert.Single(merged.TrackSections[1].frames);
            Assert.Equal(2.0, merged.TrackSections[1].frames[0].ut);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Merger]") &&
                l.Contains("healed unrecorded-gap") &&
                l.Contains("rec-580-sample-skip"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Merger]") &&
                l.Contains("boundary discontinuity=") &&
                l.Contains("vessel='Kerbal X'") &&
                l.Contains("prevSrc=Background") &&
                l.Contains("nextSrc=Active") &&
                l.Contains("cause=sample-skip"));
        }

        [Fact]
        public void MergeTree_BackgroundToActiveRelativeAnchorMismatch_SkipsHeal()
        {
            const double boundaryUT = 1.0;
            var background = MakeSectionWithFrame(
                boundaryUT, lat: 0, lon: 0, alt: 0.0,
                velocity: new Vector3(100f, 0f, 0f),
                referenceFrame: ReferenceFrame.Relative,
                source: TrackSectionSource.Background);
            background.startUT = 0.0;
            background.endUT = boundaryUT;
            background.anchorVesselId = 101;

            var active = MakeSectionWithFrame(
                2.0, lat: 0, lon: 0, alt: 50.0,
                velocity: Vector3.zero,
                referenceFrame: ReferenceFrame.Relative,
                source: TrackSectionSource.Active);
            active.startUT = boundaryUT;
            active.endUT = 3.0;
            active.anchorVesselId = 202;

            var rec = MakeRecording("rec-580-anchor", "Anchor Vessel",
                new List<TrackSection> { background, active });
            var tree = MakeTree("580 Anchor Mismatch", rec);

            var merged = SessionMerger.MergeTree(tree)["rec-580-anchor"];

            Assert.Single(merged.TrackSections[0].frames);
            Assert.Single(merged.TrackSections[1].frames);
            Assert.Equal(2.0, merged.TrackSections[1].frames[0].ut);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Merger]") &&
                l.Contains("healed unrecorded-gap") &&
                l.Contains("rec-580-anchor"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Merger]") &&
                l.Contains("vessel='Anchor Vessel'") &&
                l.Contains("prevRef=Relative") &&
                l.Contains("nextRef=Relative") &&
                l.Contains("cause=unrecorded-gap"));
        }

        [Fact]
        public void MergeTree_BackgroundToActiveUnrecordedGap_HealsMultipleBoundaries()
        {
            var background1 = MakeSectionWithFrame(
                0.0, lat: 0, lon: 0, alt: 100.0,
                velocity: new Vector3(100f, 0f, 0f),
                source: TrackSectionSource.Background);
            background1.startUT = 0.0;
            background1.endUT = 1.0;

            var active1 = MakeSectionWithFrame(
                2.0, lat: 0, lon: 0, alt: 200.0,
                velocity: Vector3.zero,
                source: TrackSectionSource.Active);
            active1.startUT = 1.0;
            active1.endUT = 3.0;

            var background2 = MakeSectionWithFrame(
                3.0, lat: 0, lon: 0, alt: 200.0,
                velocity: new Vector3(100f, 0f, 0f),
                source: TrackSectionSource.Background);
            background2.startUT = 3.0;
            background2.endUT = 4.0;

            var active2 = MakeSectionWithFrame(
                5.0, lat: 0, lon: 0, alt: 350.0,
                velocity: Vector3.zero,
                source: TrackSectionSource.Active);
            active2.startUT = 4.0;
            active2.endUT = 6.0;

            var rec = MakeRecording("rec-580-multi", "Multi Vessel",
                new List<TrackSection> { background1, active1, background2, active2 });
            var tree = MakeTree("580 Multiple Handoffs", rec);

            var merged = SessionMerger.MergeTree(tree)["rec-580-multi"];

            Assert.Equal(4, merged.TrackSections.Count);
            Assert.Equal(1.0, merged.TrackSections[0].frames.Last().ut);
            Assert.Equal(1.0, merged.TrackSections[1].frames[0].ut);
            Assert.Equal(4.0, merged.TrackSections[2].frames.Last().ut);
            Assert.Equal(4.0, merged.TrackSections[3].frames[0].ut);
            Assert.InRange(merged.TrackSections[1].boundaryDiscontinuityMeters, 0f, 0.01f);
            Assert.InRange(merged.TrackSections[3].boundaryDiscontinuityMeters, 0f, 0.01f);
            Assert.Contains(merged.Points, p => Math.Abs(p.ut - 1.0) <= 1e-6);
            Assert.Contains(merged.Points, p => Math.Abs(p.ut - 4.0) <= 1e-6);
            Assert.Equal(2, logLines.Count(l =>
                l.Contains("[INFO]") &&
                l.Contains("[Merger]") &&
                l.Contains("recId=rec-580-multi") &&
                l.Contains("healed unrecorded-gap") &&
                l.Contains("cause=unrecorded-gap #580")));
        }

        [Fact]
        public void MergeTree_BackgroundToActiveUnrecordedGap_LogsUnhealableSeam()
        {
            const double boundaryUT = 1.0;
            var background = MakeSectionWithFrame(
                boundaryUT, lat: 0, lon: 0, alt: 100.0,
                velocity: new Vector3(100f, 0f, 0f),
                bodyName: "Kerbin",
                source: TrackSectionSource.Background);
            background.startUT = 0.0;
            background.endUT = boundaryUT;

            var active = MakeSectionWithFrame(
                2.0, lat: 0, lon: 0, alt: 150.0,
                velocity: Vector3.zero,
                bodyName: "Mun",
                source: TrackSectionSource.Active);
            active.startUT = boundaryUT;
            active.endUT = 3.0;

            var rec = MakeRecording("rec-580-body-mismatch", "Body Mismatch",
                new List<TrackSection> { background, active });
            var tree = MakeTree("580 Body Mismatch", rec);

            var merged = SessionMerger.MergeTree(tree)["rec-580-body-mismatch"];

            Assert.Single(merged.TrackSections[0].frames);
            Assert.Single(merged.TrackSections[1].frames);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Merger]") &&
                l.Contains("rec-580-body-mismatch") &&
                l.Contains("healed unrecorded-gap"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Merger]") &&
                l.Contains("unable to heal unrecorded-gap") &&
                l.Contains("recId=rec-580-body-mismatch") &&
                l.Contains("reason=body-mismatch"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Merger]") &&
                l.Contains("vessel='Body Mismatch'") &&
                l.Contains("boundary discontinuity=") &&
                l.Contains("cause=unrecorded-gap"));
        }

        #endregion

        #region ResolveOverlaps — frame trimming

        [Fact]
        public void ResolveOverlaps_FramesTrimmedToResultBoundaries()
        {
            // Background section [0-200] with frames at 0, 25, 50, 75, 100, 125, 150, 175, 200
            var bgFrames = new List<TrajectoryPoint>();
            for (int i = 0; i <= 8; i++)
            {
                bgFrames.Add(new TrajectoryPoint
                {
                    ut = i * 25.0,
                    latitude = 0, longitude = 0, altitude = 70000,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                });
            }
            var bgSection = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0,
                endUT = 200,
                source = TrackSectionSource.Background,
                sampleRateHz = 10f,
                frames = bgFrames,
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };

            // Active section [50-150] with frames at 50, 75, 100, 125, 150
            var activeFrames = new List<TrajectoryPoint>();
            for (int i = 2; i <= 6; i++)
            {
                activeFrames.Add(new TrajectoryPoint
                {
                    ut = i * 25.0,
                    latitude = 0, longitude = 0, altitude = 71000,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                });
            }
            var activeSection = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 50,
                endUT = 150,
                source = TrackSectionSource.Active,
                sampleRateHz = 10f,
                frames = activeFrames,
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };

            var sections = new List<TrackSection> { bgSection, activeSection };

            var result = SessionMerger.ResolveOverlaps(sections);

            // Expected: Background[0-50] + Active[50-150] + Background[150-200]
            Assert.Equal(3, result.Count);

            // Background [0-50]: frames at 0, 25, 50
            Assert.Equal(TrackSectionSource.Background, result[0].source);
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(50, result[0].endUT);
            Assert.NotNull(result[0].frames);
            Assert.Equal(3, result[0].frames.Count);
            Assert.Equal(0, result[0].frames[0].ut);
            Assert.Equal(50, result[0].frames[result[0].frames.Count - 1].ut);

            // Active [50-150]: all 5 frames preserved
            Assert.Equal(TrackSectionSource.Active, result[1].source);
            Assert.Equal(50, result[1].startUT);
            Assert.Equal(150, result[1].endUT);
            Assert.NotNull(result[1].frames);
            Assert.Equal(5, result[1].frames.Count);
            Assert.Equal(50, result[1].frames[0].ut);
            Assert.Equal(150, result[1].frames[result[1].frames.Count - 1].ut);

            // Background [150-200]: frames at 150, 175, 200
            Assert.Equal(TrackSectionSource.Background, result[2].source);
            Assert.Equal(150, result[2].startUT);
            Assert.Equal(200, result[2].endUT);
            Assert.NotNull(result[2].frames);
            Assert.Equal(3, result[2].frames.Count);
            Assert.Equal(150, result[2].frames[0].ut);
            Assert.Equal(200, result[2].frames[result[2].frames.Count - 1].ut);
        }

        #endregion

        #region ResolveOverlaps — equal priority (same source)

        [Fact]
        public void ResolveOverlaps_SameSourceOverlap_FirstOneWins()
        {
            // Two Active sections overlapping — first one (earlier start) takes precedence
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(50, 150, TrackSectionSource.Active)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            // First Active keeps [0-100], second trimmed to [100-150]
            Assert.Equal(2, result.Count);
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(100, result[0].endUT);
            Assert.Equal(100, result[1].startUT);
            Assert.Equal(150, result[1].endUT);
        }

        #endregion

        #region ResolveOverlaps — complete coverage by higher priority

        [Fact]
        public void ResolveOverlaps_LowerPriorityFullyCovered_Dropped()
        {
            // Checkpoint [0-100] entirely covered by Active [0-100]
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(0, 100, TrackSectionSource.Checkpoint)
            };

            var result = SessionMerger.ResolveOverlaps(sections);

            Assert.Single(result);
            Assert.Equal(TrackSectionSource.Active, result[0].source);
            Assert.Equal(0, result[0].startUT);
            Assert.Equal(100, result[0].endUT);
        }

        #endregion

        #region MergePartEvents — empty inputs

        [Fact]
        public void MergePartEvents_BothEmpty_EmptyOutput()
        {
            var result = SessionMerger.MergePartEvents(
                new List<PartEvent>(), new List<PartEvent>());
            Assert.Empty(result);
        }

        [Fact]
        public void MergePartEvents_OneEmpty_CopiesOther()
        {
            var events = new List<PartEvent>
            {
                MakePartEvent(100, 42, PartEventType.EngineIgnited)
            };
            var result = SessionMerger.MergePartEvents(events, new List<PartEvent>());
            Assert.Single(result);
        }

        #endregion

        #region MergeTree — with overlapping sections in recording

        [Fact]
        public void MergeTree_WithOverlappingSections_ResolvesCorrectly()
        {
            var sections = new List<TrackSection>
            {
                MakeSection(0, 200, TrackSectionSource.Checkpoint),
                MakeSection(50, 150, TrackSectionSource.Background),
                MakeSection(80, 120, TrackSectionSource.Active)
            };
            var rec = MakeRecording("rec-complex", "Complex Vessel", sections);
            var tree = MakeTree("Complex Test", rec);

            var result = SessionMerger.MergeTree(tree);
            var merged = result["rec-complex"];

            // Expected: Checkpoint[0-50] + Background[50-80] + Active[80-120] +
            //           Background[120-150] + Checkpoint[150-200]
            Assert.Equal(5, merged.TrackSections.Count);

            Assert.Equal(TrackSectionSource.Checkpoint, merged.TrackSections[0].source);
            Assert.Equal(0, merged.TrackSections[0].startUT);
            Assert.Equal(50, merged.TrackSections[0].endUT);

            Assert.Equal(TrackSectionSource.Background, merged.TrackSections[1].source);
            Assert.Equal(50, merged.TrackSections[1].startUT);
            Assert.Equal(80, merged.TrackSections[1].endUT);

            Assert.Equal(TrackSectionSource.Active, merged.TrackSections[2].source);
            Assert.Equal(80, merged.TrackSections[2].startUT);
            Assert.Equal(120, merged.TrackSections[2].endUT);

            Assert.Equal(TrackSectionSource.Background, merged.TrackSections[3].source);
            Assert.Equal(120, merged.TrackSections[3].startUT);
            Assert.Equal(150, merged.TrackSections[3].endUT);

            Assert.Equal(TrackSectionSource.Checkpoint, merged.TrackSections[4].source);
            Assert.Equal(150, merged.TrackSections[4].startUT);
            Assert.Equal(200, merged.TrackSections[4].endUT);
        }

        #endregion

        #region Log assertion — MergeTree start/completion

        [Fact]
        public void MergeTree_LogsStartAndCompletion()
        {
            var rec = MakeRecording("rec-lifecycle", "Lifecycle Vessel",
                new List<TrackSection> { MakeSection(0, 100) });
            var tree = MakeTree("Lifecycle Test", rec);

            SessionMerger.MergeTree(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") && l.Contains("starting merge"));
            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") && l.Contains("completed merge"));
        }

        #endregion

        #region ComputeBoundaryDiscontinuity — body radius (#48)

        [Fact]
        public void ComputeBoundaryDiscontinuity_MunFrames_UsesMunRadius()
        {
            // Mun radius = 200,000m. A 1-degree latitude difference on Mun should
            // produce roughly (pi/180)*200000 = ~3491m, not the ~10472m that Kerbin
            // (600,000m) would give.
            var prev = MakeSection(0, 100, TrackSectionSource.Active,
                lat: 0.0, lon: 0.0, alt: 0.0,
                endLat: 0.0, endLon: 0.0, endAlt: 0.0,
                bodyName: "Mun");
            var next = MakeSection(100, 200, TrackSectionSource.Background,
                lat: 1.0, lon: 0.0, alt: 0.0,
                bodyName: "Mun");

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);

            // Expected ~3491m for Mun, would be ~10472m with Kerbin radius
            Assert.True(disc > 3000f && disc < 4000f,
                $"Expected Mun-scaled distance ~3491m, got {disc}m");
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_NullBodyName_FallsBackToKerbin()
        {
            // Null bodyName should fall back to Kerbin radius (600,000m)
            var prev = MakeSection(0, 100, TrackSectionSource.Active,
                lat: 0.0, lon: 0.0, alt: 0.0,
                endLat: 0.0, endLon: 0.0, endAlt: 0.0,
                bodyName: null);
            var next = MakeSection(100, 200, TrackSectionSource.Background,
                lat: 1.0, lon: 0.0, alt: 0.0,
                bodyName: null);

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);

            // Expected ~10472m using Kerbin radius fallback
            Assert.True(disc > 10000f && disc < 11000f,
                $"Expected Kerbin-fallback distance ~10472m, got {disc}m");
        }

        [Fact]
        public void ComputeBoundaryDiscontinuity_UsesLastPrevBody_NotFirstNext()
        {
            // lastPrev is on Mun, firstNext is on Kerbin. The method should use
            // lastPrev's body (Mun, 200,000m), not firstNext's (Kerbin, 600,000m).
            var prev = MakeSection(0, 100, TrackSectionSource.Active,
                lat: 0.0, lon: 0.0, alt: 0.0,
                endLat: 0.0, endLon: 0.0, endAlt: 0.0,
                bodyName: "Mun");
            var next = MakeSection(100, 200, TrackSectionSource.Background,
                lat: 1.0, lon: 0.0, alt: 0.0,
                bodyName: "Kerbin");

            float disc = SessionMerger.ComputeBoundaryDiscontinuity(prev, next);

            // Should use Mun radius (~3491m), not Kerbin radius (~10472m)
            Assert.True(disc > 3000f && disc < 4000f,
                $"Expected Mun-radius distance ~3491m (lastPrev body), got {disc}m");
        }

        #endregion
    }
}
