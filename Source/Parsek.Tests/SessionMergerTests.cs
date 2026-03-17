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
            double endLat = double.NaN, double endLon = double.NaN, double endAlt = double.NaN)
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
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                },
                new TrajectoryPoint
                {
                    ut = endUT,
                    latitude = endLat, longitude = endLon, altitude = endAlt,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                }
            };

            return new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
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
    }
}
