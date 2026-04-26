using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #585 follow-up: a recording whose sidecar load failed (most often
    /// bug #270's stale-sidecar-epoch mitigation on a Re-Fly quicksave) sits
    /// in memory with empty trajectory + null snapshots, while the on-disk
    /// .prec still holds the original mission's data. PR #558 fixed this
    /// for the active recording (the recorder rebinds and repopulates) but
    /// did nothing for siblings; the playtest at
    /// <c>logs/2026-04-25_2210_refly-bugs/</c> caught a sibling launch
    /// recording (22c28f04…) being overwritten with
    /// <c>points=0 orbitSegments=0 trackSections=0 wroteVessel=False</c> on
    /// scene exit. <see cref="RecordingStore.ShouldSkipSaveToPreserveStaleSidecar"/>
    /// guards the saver so the empty in-memory state cannot clobber the
    /// authoritative on-disk .prec.
    /// </summary>
    [Collection("Sequential")]
    public class Bug585FollowupSaveSkipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public Bug585FollowupSaveSkipTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek-bug585-followup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        // --- ShouldSkipSaveToPreserveStaleSidecar predicate tests ---

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_NullRec_ReturnsFalse()
        {
            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(null));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagFalse_ReturnsFalse()
        {
            // No hydration failure -- normal save path applies.
            var rec = new Recording { RecordingId = "happy" };

            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagSetButPointsPresent_ReturnsFalse()
        {
            // Active recording case: load failed, recorder rebinds and adds
            // new trajectory points. The in-memory state is the legitimate
            // continuation of the recording -- save MUST proceed so the new
            // data lands on disk.
            var rec = new Recording
            {
                RecordingId = "active-recovered",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });

            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagSetButOrbitSegmentsPresent_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "active-recovered-orbit",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
            };
            rec.OrbitSegments.Add(new OrbitSegment());

            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagSetButTrackSectionsPresent_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "active-recovered-track",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
            };
            rec.TrackSections.Add(new TrackSection());

            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagSetButVesselSnapshotPresent_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "active-recovered-vessel",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
                VesselSnapshot = new ConfigNode("VESSEL"),
            };

            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagSetButGhostSnapshotPresent_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "active-recovered-ghost",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
            };

            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagSetAndEverythingEmpty_ReturnsTrue()
        {
            // The 2026-04-25 playtest's sibling-recording case: the launch
            // recording (22c28f04...) loaded with stale sidecar, no recorder
            // rebound to it, in-memory state stays empty. Save must skip.
            var rec = new Recording
            {
                RecordingId = "stale-sibling",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
            };

            Assert.True(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        [Fact]
        public void ShouldSkipSaveToPreserveStaleSidecar_FlagSetWithPartEvents_ReturnsFalse()
        {
            // Defensive: even non-trajectory data (PartEvents from a brief
            // recorder bind) should keep the save proceeding so the in-memory
            // state lands.
            var rec = new Recording
            {
                RecordingId = "active-with-part-events",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
            };
            rec.PartEvents.Add(new PartEvent());

            Assert.False(RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec));
        }

        // --- End-to-end save-skip behaviour ---

        [Fact]
        public void SaveRecordingFiles_StaleSidecarLoadFailureWithEmptyState_PreservesOriginalPrec()
        {
            // STEP 1: write a "real" recording to disk (the original mission
            // data the user accumulated before the Re-Fly).
            var original = new Recording
            {
                RecordingId = "bug585-followup-original",
                RecordingFormatVersion = 3,
                SidecarEpoch = 5,
            };
            var p0 = new TrajectoryPoint
            {
                ut = 7.6, latitude = -0.1, longitude = -74.5, altitude = 1100,
                bodyName = "Kerbin",
            };
            var p1 = new TrajectoryPoint
            {
                ut = 10.5, latitude = -0.1, longitude = -74.5, altitude = 5000,
                bodyName = "Kerbin",
            };
            original.Points.Add(p0);
            original.Points.Add(p1);
            original.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = p0.ut,
                endUT = p1.ut,
                source = TrackSectionSource.Active,
                frames = new List<TrajectoryPoint> { p0, p1 },
                checkpoints = new List<OrbitSegment>(),
            });

            string precPath = Path.Combine(tempDir, original.RecordingId + ".prec");
            string vesselPath = Path.Combine(tempDir, original.RecordingId + "_vessel.craft");
            string ghostPath = Path.Combine(tempDir, original.RecordingId + "_ghost.craft");

            bool firstSave = RecordingStore.SaveRecordingFilesToPathsForTesting(
                original, precPath, vesselPath, ghostPath, incrementEpoch: true);
            Assert.True(firstSave,
                "Initial save of the 'original' recording must succeed; logLines=\n  " +
                string.Join("\n  ", logLines));

            long originalPrecLen = new FileInfo(precPath).Length;
            DateTime originalPrecMtime = File.GetLastWriteTimeUtc(precPath);
            int originalEpoch = original.SidecarEpoch;

            // STEP 2: simulate a Re-Fly load -- fresh recording instance with
            // the bug #270 stale-sidecar-load-failure flag set and empty
            // trajectory.
            var stale = new Recording
            {
                RecordingId = original.RecordingId,
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
                FilesDirty = true,
                SidecarEpoch = 2, // mirrors the .sfs's older epoch
            };

            logLines.Clear();
            // Sleep so any mtime change would be observable on FAT-style filesystems.
            System.Threading.Thread.Sleep(50);

            // STEP 3: SaveRecordingFiles must SKIP, not overwrite.
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                stale, precPath, vesselPath, ghostPath, incrementEpoch: true));

            // STEP 4: assert the on-disk .prec is byte-for-byte unchanged --
            // length, mtime, and the original recording's data round-trip.
            Assert.Equal(originalPrecLen, new FileInfo(precPath).Length);
            Assert.Equal(originalPrecMtime, File.GetLastWriteTimeUtc(precPath));

            var restored = new Recording { RecordingId = original.RecordingId };
            Assert.True(RecordingStore.LoadTrajectorySidecarForTesting(precPath, restored));
            Assert.Equal(2, restored.Points.Count);
            Assert.Equal(7.6, restored.Points[0].ut, 6);
            Assert.Equal(10.5, restored.Points[1].ut, 6);
            // The on-disk .prec must still hold the ORIGINAL recording's sidecar
            // epoch (TryProbeTrajectorySidecar reads the field directly from the
            // file). The skipped save left it untouched -- the in-memory `stale`
            // recording's epoch=2 must not have been written to disk.
            TrajectorySidecarProbe diskProbe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out diskProbe));
            Assert.Equal(originalEpoch, diskProbe.SidecarEpoch);

            // STEP 5: log assertion -- the skip emitted a structured WARN so
            // the on-disk-preservation decision is diagnosable in playtest logs.
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SaveRecordingFiles: skipping write") &&
                l.Contains("bug585-followup-original") &&
                l.Contains("SidecarLoadFailed=True") &&
                l.Contains("preserving on-disk .prec"));
        }

        [Fact]
        public void SaveRecordingFiles_StaleSidecarFlagButRecorderAddedPoints_StillWrites()
        {
            // The active-recording case from PR #558: the Re-Fly target's
            // sidecar also failed to load, but the recorder rebound and
            // started adding new trajectory points. The save MUST proceed
            // so the new flight data lands on disk -- the in-memory state
            // is the legitimate continuation, not stale-empty.
            var fresh = new Recording
            {
                RecordingId = "bug585-followup-active-recovered",
                RecordingFormatVersion = 3,
                VesselName = "Kerbal X Probe",
                SidecarEpoch = 0,
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "stale-sidecar-epoch",
                FilesDirty = true,
            };
            var fp0 = new TrajectoryPoint { ut = 165.0, altitude = 53000, bodyName = "Kerbin" };
            var fp1 = new TrajectoryPoint { ut = 200.0, altitude = 70000, bodyName = "Kerbin" };
            fresh.Points.Add(fp0);
            fresh.Points.Add(fp1);
            fresh.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = fp0.ut,
                endUT = fp1.ut,
                source = TrackSectionSource.Active,
                frames = new List<TrajectoryPoint> { fp0, fp1 },
                checkpoints = new List<OrbitSegment>(),
            });

            string precPath = Path.Combine(tempDir, fresh.RecordingId + ".prec");
            string vesselPath = Path.Combine(tempDir, fresh.RecordingId + "_vessel.craft");
            string ghostPath = Path.Combine(tempDir, fresh.RecordingId + "_ghost.craft");

            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                fresh, precPath, vesselPath, ghostPath, incrementEpoch: true));

            // The .prec must exist with the new data (recorder's repopulation).
            Assert.True(File.Exists(precPath));
            var restored = new Recording { RecordingId = fresh.RecordingId };
            Assert.True(RecordingStore.LoadTrajectorySidecarForTesting(precPath, restored));
            Assert.Equal(2, restored.Points.Count);
            Assert.Equal(165.0, restored.Points[0].ut, 6);
            Assert.Equal(200.0, restored.Points[1].ut, 6);

            // Save MUST NOT have logged the skip warn for this rec.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("SaveRecordingFiles: skipping write") &&
                l.Contains(fresh.RecordingId));
        }
    }
}
