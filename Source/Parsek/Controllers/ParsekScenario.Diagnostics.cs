using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Lifecycle diagnostics: the exception wrapper messages, the state descriptions they log, the
    // load and save timing lines and the scenario log helper.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Captures a recorder state snapshot from any scene. In flight scenes the
        /// active <see cref="ParsekFlight"/> instance contributes its live recorder /
        /// activeTree / chain manager state; in non-flight scenes those fields are
        /// null and only the static <see cref="RecordingStore"/> pending slots
        /// populate the snapshot. Used by OnSave/OnLoad lifecycle <c>[RecState]</c>
        /// emit sites.
        /// </summary>
        private static RecorderStateSnapshot CaptureScenarioRecorderState()
        {
            var flight = ParsekFlight.Instance;
            if (flight != null)
                return flight.CaptureRecorderState();
            return RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,
                recorder: null,
                pendingTree: PendingTreeSlot.PendingTree,
                pendingTreeState: PendingTreeSlot.PendingTreeStateValue,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                currentUT: Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                loadedScene: HighLogic.LoadedScene);
        }

        internal string BuildScenarioLifecycleExceptionMessageForTesting(
            string lifecycle, string phase, Exception ex)
        {
            return BuildScenarioLifecycleExceptionMessage(lifecycle, phase, ex);
        }

        internal void ExecuteScenarioLifecyclePhaseForTesting(
            string lifecycle, string phase, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                LogScenarioLifecycleException(lifecycle, phase, ex);
                throw;
            }
        }

        internal void ExecuteOnSaveLifecyclePhaseForTesting(string phase, Action body)
        {
            ExecuteScenarioLifecyclePhaseForTesting("OnSave", phase, body);
        }

        private void LogScenarioLifecycleException(string lifecycle, string phase, Exception ex)
        {
            try
            {
                ParsekLog.Error("Scenario",
                    BuildScenarioLifecycleExceptionMessage(lifecycle, phase, ex));
            }
            catch (Exception logEx)
            {
                ParsekLog.Error("Scenario",
                    $"{(string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle)}: exception logging failed " +
                    $"phase={phase ?? "<none>"} original={FormatExceptionForLog(ex)} " +
                    $"logEx={FormatExceptionForLog(logEx)}");
            }

            try
            {
                RecorderStateLog.RecState(
                    $"{(string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle)}:exception",
                    CaptureScenarioRecorderState());
            }
            catch (Exception recStateEx)
            {
                ParsekLog.Warn("Scenario",
                    $"{(string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle)}: exception RecState capture failed " +
                    $"phase={phase ?? "<none>"} ex={FormatExceptionForLog(recStateEx)}");
            }
        }

        private string BuildScenarioLifecycleExceptionMessage(
            string lifecycle, string phase, Exception ex)
        {
            RecorderStateSnapshot snap = SafeCaptureScenarioRecorderState();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}: exception phase={1} scene={2} saveFolder={3} scenarioSaveFolder={4} " +
                "committedRecordings={5} committedTrees={6} pendingTree={7} " +
                "activeMode={8} activeTree={9} activeRec={10} marker={11} journal={12} ex={13}",
                string.IsNullOrEmpty(lifecycle) ? "Scenario" : lifecycle,
                string.IsNullOrEmpty(phase) ? "<none>" : phase,
                SafeLoadedScene(),
                SafeSaveFolder(),
                string.IsNullOrEmpty(scenarioSaveFolder) ? "<null>" : scenarioSaveFolder,
                SafeCommittedRecordingCount(),
                SafeCommittedTreeCount(),
                DescribePendingTreeForLog(),
                snap.mode,
                string.IsNullOrEmpty(snap.treeId) ? "<none>" : snap.treeId,
                string.IsNullOrEmpty(snap.activeRecId) ? "<none>" : snap.activeRecId,
                DescribeMarkerForLog(ActiveReFlySessionMarker),
                DescribeJournalForLog(ActiveMergeJournal),
                FormatExceptionForLog(ex));
        }

        private static RecorderStateSnapshot SafeCaptureScenarioRecorderState()
        {
            try
            {
                return CaptureScenarioRecorderState();
            }
            catch
            {
                return default(RecorderStateSnapshot);
            }
        }

        private static string DescribePendingTreeForLog()
        {
            RecordingTree tree = PendingTreeSlot.PendingTree;
            if (tree == null)
                return "none";

            int count = tree.Recordings != null ? tree.Recordings.Count : 0;
            return string.Format(
                CultureInfo.InvariantCulture,
                "id={0},state={1},recordings={2}",
                string.IsNullOrEmpty(tree.Id) ? "<no-id>" : tree.Id,
                PendingTreeSlot.PendingTreeStateValue,
                count);
        }

        private static string DescribeMarkerForLog(ReFlySessionMarker marker)
        {
            if (marker == null)
                return "none";

            return string.Format(
                CultureInfo.InvariantCulture,
                "sess={0},tree={1},active={2},origin={3},rp={4}",
                string.IsNullOrEmpty(marker.SessionId) ? "<no-session>" : marker.SessionId,
                string.IsNullOrEmpty(marker.TreeId) ? "<no-tree>" : marker.TreeId,
                string.IsNullOrEmpty(marker.ActiveReFlyRecordingId) ? "<no-active>" : marker.ActiveReFlyRecordingId,
                string.IsNullOrEmpty(marker.OriginChildRecordingId) ? "<no-origin>" : marker.OriginChildRecordingId,
                string.IsNullOrEmpty(marker.RewindPointId) ? "<no-rp>" : marker.RewindPointId);
        }

        private static string DescribeJournalForLog(MergeJournal journal)
        {
            if (journal == null)
                return "none";

            return string.Format(
                CultureInfo.InvariantCulture,
                "journal={0},sess={1},tree={2},phase={3}",
                string.IsNullOrEmpty(journal.JournalId) ? "<no-journal>" : journal.JournalId,
                string.IsNullOrEmpty(journal.SessionId) ? "<no-session>" : journal.SessionId,
                string.IsNullOrEmpty(journal.TreeId) ? "<no-tree>" : journal.TreeId,
                string.IsNullOrEmpty(journal.Phase) ? "<no-phase>" : journal.Phase);
        }

        private static string FormatExceptionForLog(Exception ex)
        {
            if (ex == null)
                return "<none>";

            return ex.GetType().Name + ":" + (ex.Message ?? string.Empty);
        }

        private static int SafeCommittedRecordingCount()
        {
            try { return RecordingStore.CommittedRecordings?.Count ?? 0; }
            catch { return -1; }
        }

        private static int SafeCommittedTreeCount()
        {
            try { return RecordingStore.CommittedTrees?.Count ?? 0; }
            catch { return -1; }
        }

        private static string SafeLoadedScene()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch (Exception ex) { return "<scene-error:" + ex.GetType().Name + ">"; }
        }

        private static string SafeSaveFolder()
        {
            try { return string.IsNullOrEmpty(HighLogic.SaveFolder) ? "<null>" : HighLogic.SaveFolder; }
            catch (Exception ex) { return "<save-error:" + ex.GetType().Name + ">"; }
        }

        internal static string FormatLoadTimingMessageForTesting(
            long elapsedMilliseconds, int recordingCount, string phase, string status)
        {
            return FormatLoadTimingMessage(elapsedMilliseconds, recordingCount, phase, status);
        }

        private static string FormatLoadTimingMessage(
            long elapsedMilliseconds, int recordingCount, string phase, string status)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "OnLoad: {0}ms ({1} recordings) phase={2} status={3}",
                elapsedMilliseconds,
                recordingCount,
                string.IsNullOrEmpty(phase) ? "<none>" : phase,
                string.IsNullOrEmpty(status) ? "<none>" : status);
        }

        /// <summary>
        /// Writes OnLoad timing to DiagnosticsState and logs a summary.
        /// Called exactly once from OnLoad's finally block so branch-specific
        /// returns cannot double-count or miss timing.
        /// </summary>
        private static void WriteLoadTiming(
            Stopwatch sw, int recordingCount, string phase, string status)
        {
            if (sw.IsRunning)
                sw.Stop();
            DiagnosticsState.lastLoadTiming = new SaveLoadTiming
            {
                totalMilliseconds = sw.ElapsedMilliseconds,
                recordingsProcessed = recordingCount,
                dirtyRecordingsWritten = 0
            };
            ParsekLog.Verbose("Diagnostics",
                FormatLoadTimingMessage(sw.ElapsedMilliseconds, recordingCount, phase, status));
        }

        /// <summary>
        /// Writes OnSave timing to DiagnosticsState and logs a summary.
        /// Called from the finally block of OnSave to ensure timing is captured
        /// even if an exception occurs during the save process.
        /// </summary>
        private static void WriteSaveTiming(Stopwatch sw, int recordingCount, int dirtyCount)
        {
            sw.Stop();
            DiagnosticsState.lastSaveTiming = new SaveLoadTiming
            {
                totalMilliseconds = sw.ElapsedMilliseconds,
                recordingsProcessed = recordingCount,
                dirtyRecordingsWritten = dirtyCount
            };
            ParsekLog.Verbose("Diagnostics",
                string.Format(CultureInfo.InvariantCulture,
                    "OnSave: {0}ms ({1} dirty of {2})",
                    sw.ElapsedMilliseconds, dirtyCount, recordingCount));
        }

        private static void ScenarioLog(string message)
        {
            const string legacyPrefix = "[Parsek Scenario] ";
            string clean = message ?? "(empty)";
            if (clean.StartsWith(legacyPrefix, StringComparison.Ordinal))
                clean = clean.Substring(legacyPrefix.Length);

            if (clean.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = clean.IndexOf(':');
                string trimmed = idx >= 0 ? clean.Substring(idx + 1).TrimStart() : clean;
                ParsekLog.Warn("Scenario", trimmed);
                return;
            }

            ParsekLog.Info("Scenario", clean);
        }
    }
}
