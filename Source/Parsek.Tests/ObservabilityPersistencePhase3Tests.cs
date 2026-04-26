using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ObservabilityPersistencePhase3Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;
        private readonly GameScenes previousScene;

        public ObservabilityPersistencePhase3Tests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = false;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            RecordingStore.ResetForTesting();
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            SidecarFileCommitBatch.DeleteTransientArtifactOverrideForTesting = null;
            ParsekScenario.ResetInstanceForTesting();

            previousScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-observability-phase3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = previousScene;
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            SidecarFileCommitBatch.DeleteTransientArtifactOverrideForTesting = null;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { }
            }
        }

        [Fact]
        public void OnLoadTimingMessage_IncludesPhaseAndStatus()
        {
            string message = ParsekScenario.FormatLoadTimingMessageForTesting(
                elapsedMilliseconds: 42,
                recordingCount: 3,
                phase: "active-tree-restore",
                status: "completed");

            Assert.Equal(
                "OnLoad: 42ms (3 recordings) phase=active-tree-restore status=completed",
                message);
        }

        [Fact]
        public void OnLoadSource_HasSingleTimingCallSite()
        {
            string repoRoot = ResolveRepoRoot();
            string sourcePath = Path.Combine(repoRoot, "Source", "Parsek", "ParsekScenario.cs");
            string source = File.ReadAllText(sourcePath);
            int onLoadStart = source.IndexOf("public override void OnLoad", StringComparison.Ordinal);
            int nextMethod = source.IndexOf(
                "private static void DispatchRewindPostLoadIfPending",
                onLoadStart,
                StringComparison.Ordinal);

            Assert.True(onLoadStart >= 0, "OnLoad method not found.");
            Assert.True(nextMethod > onLoadStart, "OnLoad end marker not found.");

            string onLoadBody = source.Substring(onLoadStart, nextMethod - onLoadStart);
            Assert.Equal(1, CountOccurrences(onLoadBody, "WriteLoadTiming("));
        }

        [Fact]
        public void ScenarioLifecycleExceptionLog_IncludesTopLevelContextAndRecState()
        {
            var pendingTree = new RecordingTree
            {
                Id = "tree_pending",
                TreeName = "Pending"
            };
            pendingTree.Recordings["rec_pending"] = new Recording
            {
                RecordingId = "rec_pending",
                VesselName = "Pending Vessel"
            };
            RecordingStore.StashPendingTree(pendingTree, PendingTreeState.Limbo);

            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_log",
                    TreeId = "tree_live",
                    ActiveReFlyRecordingId = "rec_active",
                    OriginChildRecordingId = "rec_origin",
                    RewindPointId = "rp_log"
                },
                ActiveMergeJournal = new MergeJournal
                {
                    JournalId = "journal_log",
                    SessionId = "sess_log",
                    TreeId = "tree_live",
                    Phase = MergeJournal.Phases.Durable1Done
                }
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                scenario.ExecuteScenarioLifecyclePhaseForTesting(
                    "OnLoad",
                    "active-tree-restore",
                    () => throw new InvalidOperationException("boom")));

            Assert.Equal("boom", ex.Message);
            Assert.Contains(logLines, line =>
                line.Contains("[ERROR][Scenario]") &&
                line.Contains("OnLoad: exception") &&
                line.Contains("phase=active-tree-restore") &&
                line.Contains("pendingTree=id=tree_pending,state=Limbo,recordings=1") &&
                line.Contains("marker=sess=sess_log,tree=tree_live,active=rec_active,origin=rec_origin,rp=rp_log") &&
                line.Contains("journal=journal_log,sess=sess_log,tree=tree_live,phase=Durable1Done") &&
                line.Contains("ex=InvalidOperationException:boom"));
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][RecState]") &&
                line.Contains("[OnLoad:exception]"));
        }

        [Fact]
        public void SnapshotSidecarInvalidLog_IncludesRecordingPathEpochAndProbeContext()
        {
            string vesselPath = Path.Combine(tempDir, "rec_sidecar_vessel.craft");
            File.WriteAllBytes(vesselPath, Encoding.ASCII.GetBytes("PRKS"));
            var rec = new Recording
            {
                RecordingId = "rec_sidecar",
                SidecarEpoch = 7,
                GhostSnapshotMode = GhostSnapshotMode.Separate
            };

            RecordingStore.SnapshotSidecarLoadSummary summary =
                RecordingStore.LoadSnapshotSidecarsFromPaths(rec, vesselPath, ghostPath: null);

            Assert.Equal(RecordingStore.SnapshotSidecarLoadState.Invalid, summary.VesselState);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][RecordingStore]") &&
                line.Contains("invalid vessel snapshot sidecar") &&
                line.Contains("id=rec_sidecar") &&
                line.Contains("epoch=7") &&
                line.Contains("ghostSnapshotMode=Separate") &&
                line.Contains("fileKind=vessel") &&
                line.Contains(vesselPath) &&
                line.Contains("failure='binary header truncated'"));
        }

        [Fact]
        public void SaveRecordingFilesFailureLog_IncludesAllSidecarPaths()
        {
            string invalidFinalPath = Path.Combine(tempDir, "as-directory.prec");
            Directory.CreateDirectory(invalidFinalPath);
            string vesselPath = Path.Combine(tempDir, "rec_save_vessel.craft");
            string ghostPath = Path.Combine(tempDir, "rec_save_ghost.craft");
            var rec = new Recording
            {
                RecordingId = "rec_save",
                SidecarEpoch = 4,
                GhostSnapshotMode = GhostSnapshotMode.Unspecified
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 2.0 });

            Assert.False(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec,
                invalidFinalPath,
                vesselPath,
                ghostPath,
                incrementEpoch: true));

            string failureLine = logLines.FirstOrDefault(line =>
                line.Contains("[ERROR][RecordingStore]") &&
                line.Contains("SaveRecordingFiles failed"));
            Assert.NotNull(failureLine);
            Assert.Contains("id=rec_save", failureLine);
            Assert.Contains("epoch=4", failureLine);
            Assert.Contains("stagedFiles=", failureLine);
            Assert.Contains("trajectoryPath=", failureLine);
            Assert.Contains(invalidFinalPath, failureLine);
            Assert.Contains("vesselPath=", failureLine);
            Assert.Contains(vesselPath, failureLine);
            Assert.Contains("ghostPath=", failureLine);
            Assert.Contains(ghostPath, failureLine);
            Assert.Contains("ex=", failureLine);
            Assert.Equal(4, rec.SidecarEpoch);
        }

        [Fact]
        public void ResolveSaveScopedPathMissingContext_LogsVerboseWithRelativePath()
        {
            Assert.Null(RecordingPaths.ResolveSaveScopedPath(null));

            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][Paths]") &&
                line.Contains("ResolveSaveScopedPath missing context") &&
                line.Contains("relativeSet=") &&
                line.Contains("relativePath="));
        }

        [Fact]
        public void EnsureDirectoryMissingContext_LogsWarn()
        {
            Assert.Null(RecordingPaths.EnsureRecordingsDirectory());

            Assert.Contains(logLines, line =>
                line.Contains("[WARN][Paths]") &&
                line.Contains("EnsureRecordingsDirectory missing context") &&
                line.Contains("relativeSet=") &&
                line.Contains("relativePath="));
        }

        [Fact]
        public void SidecarTransientCleanupFailure_LogsSummary()
        {
            string stagedPath = Path.Combine(tempDir, "cleanup.stage");
            File.WriteAllText(stagedPath, "staged");
            var changes = new List<SidecarFileCommitBatch.StagedChange>
            {
                new SidecarFileCommitBatch.StagedChange
                {
                    FinalPath = Path.Combine(tempDir, "cleanup.final"),
                    StagedPath = stagedPath
                }
            };
            SidecarFileCommitBatch.DeleteTransientArtifactOverrideForTesting =
                path => throw new IOException("locked");

            SidecarFileCommitBatch.CleanupStagedArtifacts(changes, () => false);

            Assert.Contains(logLines, line =>
                line.Contains("[WARN][RecordingStore]") &&
                line.Contains("Sidecar transient cleanup failed") &&
                line.Contains("count=1") &&
                line.Contains(stagedPath) &&
                line.Contains("ex=IOException:locked"));
        }

        [Fact]
        public void CanInvoke_LogsOnlyWhenDecisionReasonChanges()
        {
            string quicksavePath = WriteQuicksave("GAME\n{\n  FLIGHTSTATE\n  {\n  }\n}\n");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_caninvoke",
                QuicksaveFilename = "Parsek/RewindPoints/rp_caninvoke.sfs"
            };
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = _ => quicksavePath;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => true;

            Assert.True(RewindInvoker.CanInvoke(rp, out string reason));
            Assert.Null(reason);
            Assert.True(RewindInvoker.CanInvoke(rp, out reason));
            Assert.Null(reason);

            var canInvokeLines = LogLinesContaining("CanInvoke:");
            Assert.Single(canInvokeLines);
            Assert.Contains("enabled rp=rp_caninvoke", canInvokeLines[0]);

            rp.Corrupted = true;
            Assert.False(RewindInvoker.CanInvoke(rp, out reason));
            Assert.Equal("Rewind point is marked corrupted", reason);
            Assert.False(RewindInvoker.CanInvoke(rp, out reason));

            canInvokeLines = LogLinesContaining("CanInvoke:");
            Assert.Equal(2, canInvokeLines.Count);
            Assert.Contains("disabled rp=rp_caninvoke", canInvokeLines[1]);
            Assert.Contains("reason='Rewind point is marked corrupted'", canInvokeLines[1]);
        }

        [Fact]
        public void CanInvoke_LogsDeepParseFailureReason()
        {
            string quicksavePath = WriteQuicksave(
                "GAME\n{\n  FLIGHTSTATE\n  {\n    VESSEL\n    {\n      PART\n      {\n        name = missingPart\n      }\n    }\n  }\n}\n");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_missingpart",
                QuicksaveFilename = "Parsek/RewindPoints/rp_missingpart.sfs"
            };
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = _ => quicksavePath;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => false;

            Assert.False(RewindInvoker.CanInvoke(rp, out string reason));

            Assert.Contains("Missing parts: missingPart", reason);
            Assert.True(rp.Corrupted);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][Rewind]") &&
                line.Contains("Precondition failed") &&
                line.Contains("missingPart"));
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][Rewind]") &&
                line.Contains("CanInvoke: disabled rp=rp_missingpart") &&
                line.Contains("reason='Missing parts: missingPart'"));
        }

        [Fact]
        public void RewindSlotCanInvoke_LogsDisabledSlotReasonWithoutRepeatSpam()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_slot",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 3,
                        OriginChildRecordingId = "rec_child",
                        Disabled = true,
                        DisabledReason = "no-live-vessel"
                    }
                }
            };

            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out string reason));
            Assert.Equal("rewind slot disabled: no-live-vessel", reason);
            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out reason));

            var slotLines = LogLinesContaining("CanInvokeSlot:");
            Assert.Single(slotLines);
            Assert.Contains("slot-disabled rp=rp_slot", slotLines[0]);
            Assert.Contains("slot=3", slotLines[0]);
            Assert.Contains("origin=rec_child", slotLines[0]);
            Assert.Contains("reason='rewind slot disabled: no-live-vessel'", slotLines[0]);
        }

        [Fact]
        public void RewindSlotCanInvokeLogState_ClearAllowsRemovedRpIdentityToRelease()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_clear",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 3,
                        OriginChildRecordingId = "rec_child",
                        Disabled = true,
                        DisabledReason = "no-live-vessel"
                    }
                }
            };

            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out _));
            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out _));

            Assert.Equal(1, RecordingsTableUI.ClearRewindSlotCanInvokeLogState("rp_clear"));
            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out _));

            Assert.Equal(2, LogLinesContaining("CanInvokeSlot:").Count);
        }

        [Fact]
        public void RewindSlotCanInvoke_LogsSlotOkAndGlobalFailureContext()
        {
            string quicksavePath = WriteQuicksave("GAME\n{\n  FLIGHTSTATE\n  {\n  }\n}\n");
            var slot = new ChildSlot
            {
                SlotIndex = 7,
                OriginChildRecordingId = "rec_child"
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_slot_ok",
                QuicksaveFilename = "Parsek/RewindPoints/rp_slot_ok.sfs",
                ChildSlots = new List<ChildSlot> { slot }
            };
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = _ => quicksavePath;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => true;

            Assert.True(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out string reason));
            Assert.Null(reason);

            rp.Corrupted = true;
            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out reason));
            Assert.Equal("Rewind point is marked corrupted", reason);

            var slotLines = LogLinesContaining("CanInvokeSlot:");
            Assert.Equal(2, slotLines.Count);
            Assert.Contains("slot-ok rp=rp_slot_ok", slotLines[0]);
            Assert.Contains("slot=7", slotLines[0]);
            Assert.Contains("origin=rec_child", slotLines[0]);
            Assert.Contains("global-blocked rp=rp_slot_ok", slotLines[1]);
            Assert.Contains("listIndex=0", slotLines[1]);
            Assert.Contains("reason='Rewind point is marked corrupted'", slotLines[1]);
        }

        // Regression: production log 2026-04-26_1025 showed 1389 identical
        // slot-ok emits over 6 seconds for the same rp/slot. The existing
        // 2-call test was insufficient. Drive 200 consecutive calls and
        // assert at most one emit fires.
        [Fact]
        public void RewindSlotCanInvoke_ManyConsecutiveCalls_EmitsOnceForStableSlotOk()
        {
            string quicksavePath = WriteQuicksave("GAME\n{\n  FLIGHTSTATE\n  {\n  }\n}\n");
            var slot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin"
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_spam_repro",
                QuicksaveFilename = "Parsek/RewindPoints/rp_spam_repro.sfs",
                ChildSlots = new List<ChildSlot> { slot }
            };
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = _ => quicksavePath;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => true;

            for (int i = 0; i < 200; i++)
            {
                Assert.True(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out _));
            }

            Assert.Single(LogLinesContaining("CanInvokeSlot:"));
        }

        private string WriteQuicksave(string contents)
        {
            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".sfs");
            File.WriteAllText(path, contents);
            return path;
        }

        private List<string> LogLinesContaining(string marker)
        {
            return logLines.Where(line => line.Contains(marker)).ToList();
        }

        private static int CountOccurrences(string value, string marker)
        {
            int count = 0;
            int index = 0;
            while (index >= 0 && index < value.Length)
            {
                index = value.IndexOf(marker, index, StringComparison.Ordinal);
                if (index < 0)
                    break;
                count++;
                index += marker.Length;
            }

            return count;
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }

            throw new InvalidOperationException(
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
