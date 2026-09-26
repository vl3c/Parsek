using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FlushDirtyFiles is an OUT-OF-BAND sidecar write: it runs from the load-time
    /// optimization pass (ParsekScenario.OnLoad, loadPhase "optimization") and from the
    /// commit paths, never from OnSave. The .sfs on disk therefore still carries the
    /// epoch of the last OnSave. When the flush advanced the .prec epoch, a load that was
    /// followed by a quit, crash or kill before the next save left .prec = .sfs + 1, and
    /// the next cold load rejected the sidecar as stale (bug #270's detector) and dropped
    /// the recording - the whole tree when it was the root.
    ///
    /// These cells drive the real writer through the real directory seam
    /// (<see cref="RecordingPaths.SaveRootOverrideForTesting"/>) and read back the epoch
    /// the .prec actually carries, so the verdict comes from the bytes on disk.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerFlushSidecarEpochTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;

        public OptimizerFlushSidecarEpochTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            saveRoot = Path.Combine(
                Path.GetTempPath(), "parsek-optflush-epoch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(saveRoot);
            RecordingPaths.SaveRootOverrideForTesting = saveRoot;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            try
            {
                if (Directory.Exists(saveRoot))
                    Directory.Delete(saveRoot, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        private static Recording MakeRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                VesselName = "Epoch Probe"
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                latitude = 0,
                longitude = 0,
                altitude = 1000,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 120, 0),
                bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 110,
                latitude = 0.01,
                longitude = 0.02,
                altitude = 1500,
                rotation = new Quaternion(0, 0.1f, 0, 0.99f),
                velocity = new Vector3(0, 200, 0),
                bodyName = "Kerbin"
            });
            return rec;
        }

        /// <summary>Writes the .sfs half of an OnSave: the RECORDING node as it is on disk.</summary>
        private static ConfigNode SnapshotSfs(Recording rec)
        {
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            return node;
        }

        /// <summary>A cold load of that .sfs: metadata from the node, bulk data from disk.</summary>
        private static Recording ColdLoad(ConfigNode sfs, out bool hydrated)
        {
            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(sfs, rec);
            hydrated = RecordingStore.LoadRecordingFiles(rec);
            return rec;
        }

        private string PrecPath(string id)
        {
            return RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(id));
        }

        private int ProbePrecEpoch(string id)
        {
            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(PrecPath(id), out probe));
            return probe.SidecarEpoch;
        }

        [Fact]
        public void LoadOptimizeQuitWithoutSave_NextColdLoadStillHydratesTheSidecar()
        {
            const string id = "optflushepoch0load";

            // OnSave: epoch advances, .prec written, then the .sfs carries the same epoch.
            var flown = MakeRecording(id);
            Assert.True(RecordingStore.SaveRecordingFiles(flown, incrementEpoch: true));
            ConfigNode persistentSfs = SnapshotSfs(flown);
            int sfsEpoch = flown.SidecarEpoch;
            Assert.Equal(1, sfsEpoch);
            Assert.Equal(sfsEpoch, ProbePrecEpoch(id));

            // Cold load, then the load-time optimization pass flushes a dirtied recording
            // (the checkpoint-bridge Ensure and the optimizer's own normalization both
            // mark dirty at load).
            bool hydrated;
            Recording loaded = ColdLoad(persistentSfs, out hydrated);
            Assert.True(hydrated, "first cold load must hydrate: " + loaded.SidecarLoadFailureReason);
            RecordingStore.AddCommittedInternal(loaded);
            loaded.MarkFilesDirty();

            RecordingStore.RunOptimizationPass();

            Assert.False(loaded.FilesDirty);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("FlushDirtyFiles: saved 1, failed 0")
                && l.Contains("epochPreserved=1 epochSeeded=0"));

            // Quit to desktop / crash / kill: no OnSave, so the .sfs on disk is unchanged.
            int precEpochAfterFlush = ProbePrecEpoch(id);
            Recording reloaded = ColdLoad(persistentSfs, out hydrated);
            Assert.True(hydrated,
                "next cold load rejected the sidecar: reason=" +
                (reloaded.SidecarLoadFailureReason ?? "<none>") +
                " sfsEpoch=" + sfsEpoch + " precEpoch=" + precEpochAfterFlush);
            Assert.Equal(2, reloaded.Points.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("Sidecar epoch mismatch"));
            Assert.Equal(sfsEpoch, precEpochAfterFlush);
            Assert.Equal(sfsEpoch, loaded.SidecarEpoch);
        }

        [Fact]
        public void FlushWithoutBump_ThenOnSave_StillDetectsAnOlderSaveAsStale()
        {
            // Mirror direction: not advancing on the flush must not reopen bug #270. The
            // next OnSave that rewrites the recording still advances, so a quicksave taken
            // before it reads the newer .prec as stale.
            const string id = "optflushepoch1mirror";

            var flown = MakeRecording(id);
            Assert.True(RecordingStore.SaveRecordingFiles(flown, incrementEpoch: true));
            ConfigNode quicksave = SnapshotSfs(flown);

            RecordingStore.AddCommittedInternal(flown);
            flown.MarkFilesDirty();
            RecordingStore.RunOptimizationPass();
            Assert.Equal(1, flown.SidecarEpoch);

            flown.MarkFilesDirty();
            Assert.True(RecordingStore.SaveRecordingFiles(flown, incrementEpoch: true));
            Assert.Equal(2, ProbePrecEpoch(id));

            bool hydrated;
            Recording stale = ColdLoad(quicksave, out hydrated);
            Assert.False(hydrated);
            Assert.Equal("stale-sidecar-epoch", stale.SidecarLoadFailureReason);
        }

        [Fact]
        public void FlushOfANeverSavedRecording_SeedsEpochOne_SoStaleDetectionIsArmed()
        {
            // Mirror direction for a recording no .sfs has ever carried an epoch for
            // (epoch 0: new commits, split second halves). Epoch 0 disables the stale
            // check on load (ShouldSkipStaleSidecar's backward-compat branch), so a flush
            // that preserved 0 would leave the recording unprotected for good. Seeding 1
            // cannot strand anything: every .sfs that names it holds 0 or nothing.
            const string id = "optflushepoch2seed";

            var committed = MakeRecording(id);
            Assert.Equal(0, committed.SidecarEpoch);
            RecordingStore.CommitGloopsRecording(committed);

            Assert.False(committed.FilesDirty);
            Assert.Equal(1, committed.SidecarEpoch);
            Assert.Equal(1, ProbePrecEpoch(id));

            ConfigNode firstSave = SnapshotSfs(committed);
            committed.MarkFilesDirty();
            Assert.True(RecordingStore.SaveRecordingFiles(committed, incrementEpoch: true));

            bool hydrated;
            Recording stale = ColdLoad(firstSave, out hydrated);
            Assert.False(hydrated);
            Assert.Equal("stale-sidecar-epoch", stale.SidecarLoadFailureReason);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(-1, true)]
        [InlineData(1, false)]
        [InlineData(7, false)]
        public void ShouldAdvanceSidecarEpochOnFlush_OnlySeedsUnepochedRecordings(int epoch, bool expected)
        {
            Assert.Equal(expected,
                RecordingStore.ShouldAdvanceSidecarEpochOnFlush(new Recording { SidecarEpoch = epoch }));
            Assert.False(RecordingStore.ShouldAdvanceSidecarEpochOnFlush(null));
        }
    }
}
