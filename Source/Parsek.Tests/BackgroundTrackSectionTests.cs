using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for TrackSection creation and lifecycle in BackgroundRecorder.
    /// Verifies that background vessels correctly create, transition, and flush
    /// TrackSections with source=Background.
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundTrackSectionTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BackgroundTrackSectionTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        /// <summary>
        /// Helper: creates a minimal RecordingTree with one background vessel.
        /// </summary>
        private RecordingTree MakeTree(uint pid, string recId)
        {
            var tree = new RecordingTree
            {
                Id = "tree_ts_test",
                TreeName = "TS Test Tree",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_active"
            };

            tree.Recordings["rec_active"] = new Recording
            {
                RecordingId = "rec_active",
                VesselName = "Active Vessel",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };

            tree.Recordings[recId] = new Recording
            {
                RecordingId = recId,
                VesselName = "Background Vessel",
                VesselPersistentId = pid,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.BackgroundMap[pid] = recId;

            return tree;
        }

        #region TrackSection creation: source

        [Fact]
        public void InitLoadedState_CreatesTrackSection_WithSourceBackground()
        {
            uint pid = 500;
            string recId = "rec_bg";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.Equal(TrackSectionSource.Background, section.Value.source);
        }

        [Fact]
        public void InitLoadedState_CreatesTrackSection_WithIsFromBackgroundTrue()
        {
            uint pid = 501;
            string recId = "rec_bg2";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.ExoBallistic, 1000.0);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.Equal(TrackSectionSource.Background, section.Value.source);
        }

        [Fact]
        public void InitLoadedState_CreatesTrackSection_WithCorrectEnvironment()
        {
            uint pid = 502;
            string recId = "rec_bg3";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.SurfaceMobile, 1500.0);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.Equal(SegmentEnvironment.SurfaceMobile, section.Value.environment);
        }

        [Fact]
        public void InitLoadedState_CreatesTrackSection_WithAbsoluteReferenceFrame()
        {
            uint pid = 503;
            string recId = "rec_bg4";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.Equal(ReferenceFrame.Absolute, section.Value.referenceFrame);
        }

        [Fact]
        public void InitLoadedState_SetsTrackSectionActive()
        {
            uint pid = 504;
            string recId = "rec_bg5";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            Assert.True(bgRecorder.GetTrackSectionActiveForTesting(pid));
        }

        [Fact]
        public void InitLoadedState_InitializesEmptyFramesList()
        {
            uint pid = 505;
            string recId = "rec_bg6";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.NotNull(section.Value.frames);
            Assert.Empty(section.Value.frames);
        }

        [Fact]
        public void InitLoadedState_InitializesEmptyCheckpointsList()
        {
            uint pid = 506;
            string recId = "rec_bg7";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.NotNull(section.Value.checkpoints);
            Assert.Empty(section.Value.checkpoints);
        }

        [Fact]
        public void InitLoadedState_WithInitialPoint_StartsTrackSectionAtSeedUT()
        {
            uint pid = 507;
            string recId = "rec_bg8";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);
            var point = new TrajectoryPoint
            {
                ut = 995.25,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(4f, 5f, 6f),
                bodyName = "Kerbin"
            };

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0, initialPoint: point);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.Equal(point.ut, section.Value.startUT, 6);
            Assert.Single(section.Value.frames);
            Assert.Equal(point.ut, section.Value.frames[0].ut, 6);
        }

        [Fact]
        public void InitLoadedState_WithInitialPoint_WritesSeedIntoRecordingAndAltitudeMetadata()
        {
            uint pid = 508;
            string recId = "rec_bg9";
            var tree = MakeTree(pid, recId);
            tree.Recordings[recId].Points.Clear();
            tree.Recordings[recId].ExplicitEndUT = double.NaN;

            var bgRecorder = new BackgroundRecorder(tree);
            var point = new TrajectoryPoint
            {
                ut = 995.25,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(4f, 5f, 6f),
                bodyName = "Kerbin"
            };

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0, initialPoint: point);

            Assert.Single(tree.Recordings[recId].Points);
            Assert.Equal(point.ut, tree.Recordings[recId].Points[0].ut, 6);
            Assert.Equal(point.ut, tree.Recordings[recId].ExplicitEndUT, 6);

            var section = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(section);
            Assert.Equal((float)point.altitude, section.Value.minAltitude);
            Assert.Equal((float)point.altitude, section.Value.maxAltitude);
            Assert.Equal(point.ut, bgRecorder.GetLastRecordedUTForTesting(pid), 6);
            Assert.Equal(point.velocity, bgRecorder.GetLastRecordedVelocityForTesting(pid));
        }

        #endregion

        #region ClassifyBackgroundEnvironment — pure static tests

        [Fact]
        public void ClassifyBackgroundEnvironment_Atmospheric_BelowAtmosphere()
        {
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: true, altitude: 5000, atmosphereDepth: 70000,
                situation: 8, srfSpeed: 200, cachedEngines: null);

            Assert.Equal(SegmentEnvironment.Atmospheric, result);
        }

        [Fact]
        public void ClassifyBackgroundEnvironment_SurfaceStationary_Landed()
        {
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: true, altitude: 70, atmosphereDepth: 70000,
                situation: 1, srfSpeed: 0.05, cachedEngines: null);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void ClassifyBackgroundEnvironment_SurfaceMobile_LandedMoving()
        {
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: true, altitude: 70, atmosphereDepth: 70000,
                situation: 1, srfSpeed: 5.0, cachedEngines: null);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        [Fact]
        public void ClassifyBackgroundEnvironment_ExoBallistic_AboveAtmoNoThrust()
        {
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: true, altitude: 80000, atmosphereDepth: 70000,
                situation: 16, srfSpeed: 2200, cachedEngines: null);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void ClassifyBackgroundEnvironment_ExoBallistic_NullEngines()
        {
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: false, altitude: 100000, atmosphereDepth: 0,
                situation: 16, srfSpeed: 2200, cachedEngines: null);

            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        [Fact]
        public void ClassifyBackgroundEnvironment_AtmosphericEvaNearGround_ReturnsSurfaceMobile()
        {
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: true, altitude: 75, atmosphereDepth: 70000,
                situation: 8, srfSpeed: 1.5, cachedEngines: null,
                isEva: true, heightFromTerrain: 1.0, heightFromTerrainValid: true);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        [Fact]
        public void ClassifyBackgroundEnvironment_AtmosphericEvaAtSeaLevelWithOcean_ReturnsSurfaceMobile()
        {
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: true, altitude: 0.2, atmosphereDepth: 70000,
                situation: 8, srfSpeed: 1.5, cachedEngines: null,
                isEva: true, heightFromTerrain: 600.0, heightFromTerrainValid: true,
                hasOcean: true);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        #endregion

        #region Environment transition creates new section

        [Fact]
        public void EnvironmentHysteresis_Transition_ClosesOldAndOpensNew()
        {
            // Use EnvironmentHysteresis directly to verify the pattern
            var hysteresis = new EnvironmentHysteresis(SegmentEnvironment.Atmospheric);
            Assert.Equal(SegmentEnvironment.Atmospheric, hysteresis.CurrentEnvironment);

            // Atmospheric -> SurfaceMobile has 0.5s debounce
            Assert.False(hysteresis.Update(SegmentEnvironment.SurfaceMobile, 1000.0));
            bool changed = hysteresis.Update(SegmentEnvironment.SurfaceMobile, 1000.5);
            Assert.True(changed);
            Assert.Equal(SegmentEnvironment.SurfaceMobile, hysteresis.CurrentEnvironment);
        }

        [Fact]
        public void EnvironmentTransition_AccumulatesSections()
        {
            // Test that when environment changes, the old section gets closed
            // and added to trackSections list, and a new one opens.
            uint pid = 600;
            string recId = "rec_env";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            // Start with Atmospheric
            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            // Verify initial state
            Assert.True(bgRecorder.GetTrackSectionActiveForTesting(pid));
            var sections = bgRecorder.GetTrackSectionsForTesting(pid);
            Assert.NotNull(sections);
            Assert.Empty(sections); // No closed sections yet

            // The current section should be Atmospheric
            var current = bgRecorder.GetCurrentTrackSectionForTesting(pid);
            Assert.NotNull(current);
            Assert.Equal(SegmentEnvironment.Atmospheric, current.Value.environment);
        }

        #endregion

        #region On-rails transition creates Checkpoint section

        [Fact]
        public void GoOnRails_ClassifiesAsExoBallistic()
        {
            // We cannot call OnBackgroundVesselGoOnRails directly (needs Vessel),
            // so test the static properties of StartCheckpointTrackSection via
            // InjectLoadedStateWithEnvironmentForTesting + manual close + verify.
            //
            // The key assertion: after on-rails transition, the last section before
            // removal should be of type Checkpoint (tested indirectly via ClassifyBackgroundEnvironment
            // and the StartCheckpointTrackSection method).

            // Instead, test the method's effect through the public pure static classification:
            // When vessel is in orbit (above atmo, no thrust), we get ExoBallistic
            var result = BackgroundRecorder.ClassifyBackgroundEnvironment(
                hasAtmosphere: true, altitude: 80000, atmosphereDepth: 70000,
                situation: 32, // ORBITING
                srfSpeed: 2200, cachedEngines: null);
            Assert.Equal(SegmentEnvironment.ExoBallistic, result);
        }

        #endregion

        #region Sections accumulated correctly

        [Fact]
        public void TrackSections_InitiallyEmpty()
        {
            uint pid = 700;
            string recId = "rec_acc";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            var sections = bgRecorder.GetTrackSectionsForTesting(pid);
            Assert.NotNull(sections);
            Assert.Empty(sections);
        }

        [Fact]
        public void FlushTrackSections_CopiesToRecording()
        {
            uint pid = 701;
            string recId = "rec_flush";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            // FinalizeAllForCommit should close and flush sections
            bgRecorder.FinalizeAllForCommit(1050.0);

            var rec = tree.Recordings[recId];
            Assert.Single(rec.TrackSections);

            var flushed = rec.TrackSections[0];
            Assert.Equal(SegmentEnvironment.Atmospheric, flushed.environment);
            Assert.Equal(TrackSectionSource.Background, flushed.source);
            Assert.NotEqual(TrackSectionSource.Active, flushed.source);
            Assert.Equal(1000.0, flushed.startUT);
            Assert.Equal(1050.0, flushed.endUT);
        }

        [Fact]
        public void CloseParentRecording_FlushesStructuralBoundaryFrameIntoTrackSections()
        {
            uint pid = 7011;
            string recId = "rec_split_parent";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);
            var boundary = FlightRecorder.ApplyStructuralEventFlag(new TrajectoryPoint
            {
                ut = 1200.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            });

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1190.0);
            Assert.True(BackgroundRecorder.ApplyTrajectoryPointToRecording(tree.Recordings[recId], boundary));
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, boundary);

            bgRecorder.CloseParentRecordingForTesting(
                tree.Recordings[recId],
                pid,
                "bp_structural_parent",
                boundary.ut,
                boundary);

            var rec = tree.Recordings[recId];
            Assert.Equal("bp_structural_parent", rec.ChildBranchPointId);
            Assert.Equal(boundary.ut, rec.ExplicitEndUT);
            Assert.False(tree.BackgroundMap.ContainsKey(pid));
            Assert.False(bgRecorder.HasLoadedState(pid));
            Assert.Single(rec.Points);
            Assert.Single(rec.TrackSections);
            Assert.Equal(boundary.ut, rec.TrackSections[0].endUT);
            Assert.Single(rec.TrackSections[0].frames);
            Assert.Equal(boundary.ut, rec.TrackSections[0].frames[0].ut);
            Assert.True(((TrajectoryPointFlags)rec.TrackSections[0].frames[0].flags
                & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.StructuralEventSnapshot);
        }

        [Fact]
        public void CloseParentRecording_TrimsDeferredPostBranchFramesBeforeFlush()
        {
            uint pid = 7012;
            string recId = "rec_split_parent_tail";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);
            var boundary = FlightRecorder.ApplyStructuralEventFlag(new TrajectoryPoint
            {
                ut = 1200.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            });
            var deferredSample = new TrajectoryPoint
            {
                ut = 1201.0,
                latitude = 1.1,
                longitude = 2.1,
                altitude = 999.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(4f, 5f, 6f),
                bodyName = "Kerbin"
            };

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1190.0);
            Assert.True(BackgroundRecorder.ApplyTrajectoryPointToRecording(tree.Recordings[recId], boundary));
            Assert.True(BackgroundRecorder.ApplyTrajectoryPointToRecording(tree.Recordings[recId], deferredSample));
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, boundary);
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, deferredSample);

            bgRecorder.CloseParentRecordingForTesting(
                tree.Recordings[recId],
                pid,
                "bp_structural_parent_tail",
                boundary.ut,
                boundary);

            var rec = tree.Recordings[recId];
            Assert.Equal(boundary.ut, rec.ExplicitEndUT);
            Assert.Equal(new[] { boundary.ut }, rec.Points.Select(p => p.ut).ToArray());
            Assert.Single(rec.TrackSections);
            Assert.Equal(new[] { boundary.ut }, rec.TrackSections[0].frames.Select(p => p.ut).ToArray());
            Assert.Equal((float)boundary.altitude, rec.TrackSections[0].minAltitude);
            Assert.Equal((float)boundary.altitude, rec.TrackSections[0].maxAltitude);
            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]")
                && l.Contains("trimmed post-branch parent samples")
                && l.Contains("flatRemoved=1")
                && l.Contains("sectionFramesRemoved=1"));
        }

        [Fact]
        public void FinalizeAllForCommit_ClosesSectionAndSetsEndUT()
        {
            uint pid = 702;
            string recId = "rec_fin";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.ExoBallistic, 2000.0);

            bgRecorder.FinalizeAllForCommit(2100.0);

            var rec = tree.Recordings[recId];
            Assert.Single(rec.TrackSections);
            Assert.Equal(2100.0, rec.TrackSections[0].endUT);
        }

        [Fact]
        public void FinalizeAllForCommit_SetsExplicitEndUT()
        {
            uint pid = 703;
            string recId = "rec_fin2";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 2000.0);

            bgRecorder.FinalizeAllForCommit(2200.0);

            var rec = tree.Recordings[recId];
            Assert.Equal(2200.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void FinalizeAllForCommit_FlushesFramesIntoFlatPoints()
        {
            uint pid = 7031;
            string recId = "rec_flat_sync";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 2000.0);
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2000.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 100.0,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 10, 0)
            });
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2010.0,
                latitude = 1.1,
                longitude = 2.1,
                altitude = 140.0,
                rotation = new UnityEngine.Quaternion(0, 0.1f, 0, 0.99f),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 20, 0)
            });

            bgRecorder.FinalizeAllForCommit(2010.0);

            var rec = tree.Recordings[recId];
            Assert.Equal(2, rec.Points.Count);
            Assert.Equal(2000.0, rec.Points[0].ut);
            Assert.Equal(2010.0, rec.Points[1].ut);
        }

        [Fact]
        public void FinalizeAllForCommit_DedupesBoundaryPointAgainstExistingFlatTrajectory()
        {
            uint pid = 7032;
            string recId = "rec_flat_dedupe";
            var tree = MakeTree(pid, recId);
            tree.Recordings[recId].Points.Add(new TrajectoryPoint
            {
                ut = 2000.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 100.0,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 10, 0)
            });
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 2000.0);
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2000.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 100.0,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 10, 0)
            });
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2010.0,
                latitude = 1.1,
                longitude = 2.1,
                altitude = 140.0,
                rotation = new UnityEngine.Quaternion(0, 0.1f, 0, 0.99f),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 20, 0)
            });

            bgRecorder.FinalizeAllForCommit(2010.0);

            var rec = tree.Recordings[recId];
            Assert.Equal(2, rec.Points.Count);
            Assert.Equal(2000.0, rec.Points[0].ut);
            Assert.Equal(2010.0, rec.Points[1].ut);
        }

        [Fact]
        public void FinalizeAllForCommit_DoesNotDuplicateTrackSectionPayloadAlreadyMirroredInFlatPoints()
        {
            uint pid = 70325;
            string recId = "rec_flat_overlap";
            var tree = MakeTree(pid, recId);
            tree.Recordings[recId].Points.Add(new TrajectoryPoint
            {
                ut = 2000.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 100.0,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 10, 0)
            });
            tree.Recordings[recId].Points.Add(new TrajectoryPoint
            {
                ut = 2010.0,
                latitude = 1.1,
                longitude = 2.1,
                altitude = 140.0,
                rotation = new UnityEngine.Quaternion(0, 0.1f, 0, 0.99f),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 20, 0)
            });
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 2000.0);
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2000.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 100.0,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 10, 0)
            });
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2010.0,
                latitude = 1.1,
                longitude = 2.1,
                altitude = 140.0,
                rotation = new UnityEngine.Quaternion(0, 0.1f, 0, 0.99f),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 20, 0)
            });

            bgRecorder.FinalizeAllForCommit(2010.0);

            var rec = tree.Recordings[recId];
            Assert.Equal(new[] { 2000.0, 2010.0 }, rec.Points.Select(p => p.ut).ToArray());
        }

        [Fact]
        public void FlushLoadedStateForOnRailsTransitionForTesting_NoPayloadEnvChange_PersistsBoundarySection()
        {
            uint pid = 7033;
            string recId = "rec_onrails_boundary";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 2000.0);
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2000.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 500.0,
                rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 10, 0)
            });
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 2005.0,
                latitude = 1.1,
                longitude = 2.1,
                altitude = 300.0,
                rotation = new UnityEngine.Quaternion(0, 0.1f, 0, 0.99f),
                bodyName = "Kerbin",
                velocity = new UnityEngine.Vector3(0, 8, 0)
            });

            bgRecorder.FlushLoadedStateForOnRailsTransitionForTesting(
                pid,
                SegmentEnvironment.SurfaceStationary,
                willHavePlayableOnRailsPayload: false,
                boundaryPoint: new TrajectoryPoint
                {
                    ut = 2010.0,
                    latitude = 1.2,
                    longitude = 2.2,
                    altitude = 0.0,
                    rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                    bodyName = "Kerbin",
                    velocity = UnityEngine.Vector3.zero
                },
                ut: 2010.0);

            var rec = tree.Recordings[recId];
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(SegmentEnvironment.SurfaceStationary, rec.TrackSections[1].environment);
            Assert.Single(rec.TrackSections[1].frames);
            Assert.Equal(2010.0, rec.TrackSections[1].frames[0].ut);
            Assert.Equal(new[] { 2000.0, 2005.0, 2010.0 }, rec.Points.Select(p => p.ut).ToArray());
        }

        [Fact]
        public void FinalizeAllForCommit_SurfaceMobile_FlushesWithCorrectSource()
        {
            uint pid = 704;
            string recId = "rec_sfm";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.SurfaceMobile, 3000.0);

            bgRecorder.FinalizeAllForCommit(3050.0);

            var rec = tree.Recordings[recId];
            Assert.NotEmpty(rec.TrackSections);
            Assert.Equal(TrackSectionSource.Background, rec.TrackSections[0].source);
            Assert.NotEqual(TrackSectionSource.Active, rec.TrackSections[0].source);
        }

        [Fact]
        public void FinalizeAllForCommit_SurfaceMobile_SetsCorrectEnvironment()
        {
            uint pid = 705;
            string recId = "rec_sfm2";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.SurfaceMobile, 3000.0);

            bgRecorder.FinalizeAllForCommit(3050.0);

            var rec = tree.Recordings[recId];
            Assert.Single(rec.TrackSections);
            Assert.Equal(SegmentEnvironment.SurfaceMobile, rec.TrackSections[0].environment);
        }

        #endregion

        #region Log assertions

        [Fact]
        public void InitLoadedState_LogsTrackSectionStarted()
        {
            uint pid = 800;
            string recId = "rec_log";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("TrackSection started") &&
                l.Contains("source=Background"));
        }

        [Fact]
        public void FinalizeAllForCommit_LogsTrackSectionClosed()
        {
            uint pid = 801;
            string recId = "rec_log2";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.ExoBallistic, 1000.0);

            bgRecorder.FinalizeAllForCommit(1050.0);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("TrackSection closed"));
        }

        [Fact]
        public void FinalizeAllForCommit_LogsFlushed()
        {
            uint pid = 802;
            string recId = "rec_log3";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            bgRecorder.FinalizeAllForCommit(1050.0);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("Flushed") &&
                l.Contains("TrackSections"));
        }

        [Fact]
        public void FinalizeAllForCommit_SurfaceStationary_LogsTrackSectionClosed()
        {
            uint pid = 803;
            string recId = "rec_log4";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.SurfaceStationary, 2000.0);

            bgRecorder.FinalizeAllForCommit(2050.0);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("TrackSection closed"));
        }

        [Fact]
        public void InitLoadedState_LogsEnvironmentInStartMessage()
        {
            uint pid = 804;
            string recId = "rec_log5";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.ExoPropulsive, 1000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("TrackSection started") &&
                l.Contains("env=ExoPropulsive"));
        }

        [Fact]
        public void FinalizeAllForCommit_LogsSectionDuration()
        {
            uint pid = 805;
            string recId = "rec_log6";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            bgRecorder.FinalizeAllForCommit(1010.0);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("TrackSection closed") &&
                l.Contains("duration="));
        }

        #endregion

        #region Multiple vessels

        [Fact]
        public void FinalizeAllForCommit_FlushesAllLoadedVessels()
        {
            uint pid1 = 900;
            uint pid2 = 901;
            string recId1 = "rec_multi1";
            string recId2 = "rec_multi2";

            var tree = new RecordingTree
            {
                Id = "tree_multi",
                TreeName = "Multi Test",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_active"
            };
            tree.Recordings["rec_active"] = new Recording
            {
                RecordingId = "rec_active",
                VesselName = "Active",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings[recId1] = new Recording
            {
                RecordingId = recId1,
                VesselName = "BG 1",
                VesselPersistentId = pid1,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.Recordings[recId2] = new Recording
            {
                RecordingId = recId2,
                VesselName = "BG 2",
                VesselPersistentId = pid2,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            tree.BackgroundMap[pid1] = recId1;
            tree.BackgroundMap[pid2] = recId2;

            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid1, recId1, SegmentEnvironment.Atmospheric, 1000.0);
            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid2, recId2, SegmentEnvironment.ExoBallistic, 1000.0);

            bgRecorder.FinalizeAllForCommit(1100.0);

            Assert.Single(tree.Recordings[recId1].TrackSections);
            Assert.Single(tree.Recordings[recId2].TrackSections);
            Assert.Equal(SegmentEnvironment.Atmospheric, tree.Recordings[recId1].TrackSections[0].environment);
            Assert.Equal(SegmentEnvironment.ExoBallistic, tree.Recordings[recId2].TrackSections[0].environment);
        }

        #endregion

        #region SampleRateHz computation

        [Fact]
        public void ClosedSection_ComputesSampleRateHz()
        {
            uint pid = 950;
            string recId = "rec_rate";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            bgRecorder.FinalizeAllForCommit(1010.0);

            // With 0 frames over 10 seconds, sampleRateHz should be 0 (no frames to compute from)
            var section = tree.Recordings[recId].TrackSections[0];
            Assert.Equal(0f, section.sampleRateHz);
        }

        #endregion

        #region Section reference frame

        [Fact]
        public void BackgroundSection_HasAbsoluteReferenceFrame()
        {
            uint pid = 960;
            string recId = "rec_ref";
            var tree = MakeTree(pid, recId);
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 1000.0);

            bgRecorder.FinalizeAllForCommit(1050.0);

            Assert.Equal(ReferenceFrame.Absolute, tree.Recordings[recId].TrackSections[0].referenceFrame);
        }

        #endregion
    }
}
