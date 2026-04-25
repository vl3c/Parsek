using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MissedVesselSwitchRecoveryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissedVesselSwitchRecoveryTests()
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
        public void ShouldRecoverMissedVesselSwitch_RecorderPidMismatch_ReturnsTrue()
        {
            bool shouldRecover = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: false,
                recorderVesselPid: 100,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            Assert.True(shouldRecover);
        }

        [Fact]
        public void ShouldRecoverMissedVesselSwitch_OutsiderReturnToTrackedTreeVessel_ReturnsTrue()
        {
            bool shouldRecover = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: false,
                recorderIsRecording: false,
                recorderChainToVesselPending: false,
                recorderVesselPid: 0,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: true,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            Assert.True(shouldRecover);
        }

        [Fact]
        public void ShouldRecoverMissedVesselSwitch_SamePid_ReturnsFalse()
        {
            bool shouldRecover = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: false,
                recorderVesselPid: 100,
                activeVesselPid: 100,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            Assert.False(shouldRecover);
        }

        [Fact]
        public void ShouldRecoverMissedVesselSwitch_PendingTransientState_ReturnsFalse()
        {
            bool splitGuard = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: true,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: false,
                recorderVesselPid: 100,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            bool dockGuard = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: true,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: false,
                recorderVesselPid: 100,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            bool chainGuard = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: true,
                recorderVesselPid: 100,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            Assert.False(splitGuard);
            Assert.False(dockGuard);
            Assert.False(chainGuard);
        }

        [Fact]
        public void ShouldRecoverMissedVesselSwitch_RequiresStableActiveTreeContext()
        {
            bool noTree = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: false,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: false,
                recorderVesselPid: 100,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            bool restoring = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: true,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: false,
                recorderVesselPid: 100,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            bool zeroActivePid = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: true,
                recorderIsRecording: true,
                recorderChainToVesselPending: false,
                recorderVesselPid: 100,
                activeVesselPid: 0,
                activeVesselTrackedInBackground: false,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: false);

            Assert.False(noTree);
            Assert.False(restoring);
            Assert.False(zeroActivePid);
        }

        [Fact]
        public void ShouldRecoverMissedVesselSwitch_ArmedTrackedVessel_ReturnsFalse()
        {
            bool shouldRecover = ParsekFlight.ShouldRecoverMissedVesselSwitch(
                isRestoringActiveTree: false,
                hasActiveTree: true,
                pendingTreeDockMerge: false,
                hasPendingSplitRecorder: false,
                pendingSplitInProgress: false,
                hasRecorder: false,
                recorderIsRecording: false,
                recorderChainToVesselPending: false,
                recorderVesselPid: 0,
                activeVesselPid: 200,
                activeVesselTrackedInBackground: true,
                activeVesselAlreadyArmedForPostSwitchAutoRecord: true);

            Assert.False(shouldRecover);
        }

        [Fact]
        public void OnVesselSwitchRecState_NormalBoundary_LogsEveryEntryAndPost()
        {
            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            var context = default(ParsekFlight.MissedVesselSwitchRecoveryDiagnosticContext);
            var snapshot = BuildRecStateSnapshot(activeVesselPid: 100, activeRecId: "recA");

            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", snapshot, context);
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:post", snapshot, context);
            now = 1001.0;
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", snapshot, context);
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:post", snapshot, context);

            List<string> recStateLines = RecStateLines();
            Assert.Equal(4, recStateLines.Count);
            Assert.Equal(2, recStateLines.FindAll(l => l.Contains("[OnVesselSwitchComplete:entry]")).Count);
            Assert.Equal(2, recStateLines.FindAll(l => l.Contains("[OnVesselSwitchComplete:post]")).Count);
            Assert.All(recStateLines, l => Assert.DoesNotContain("suppressed=", l));
        }

        [Fact]
        public void OnVesselSwitchRecState_RecoveryLoopSameFingerprint_CoalescesEntryAndPost()
        {
            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            var context = BuildRecoveryContext(activeVesselPid: 736156658, recorderVesselPid: 0);
            var snapshot = BuildRecStateSnapshot(
                activeVesselPid: 736156658,
                activeRecId: "recBob",
                vesselName: "Bob Kerman");

            for (int i = 0; i < 3; i++)
            {
                ParsekFlight.LogOnVesselSwitchCompleteRecState(
                    "OnVesselSwitchComplete:entry", snapshot, context);
                ParsekFlight.LogOnVesselSwitchCompleteRecState(
                    "OnVesselSwitchComplete:post", snapshot, context);
                now += 1.0;
            }

            List<string> recStateLines = RecStateLines();
            Assert.Equal(2, recStateLines.Count);
            Assert.Single(recStateLines.FindAll(l => l.Contains("[OnVesselSwitchComplete:entry]")));
            Assert.Single(recStateLines.FindAll(l => l.Contains("[OnVesselSwitchComplete:post]")));
            Assert.Contains("Bob Kerman", recStateLines[0]);
            Assert.Contains("pid=736156658", recStateLines[0]);
            Assert.DoesNotContain("suppressed=", recStateLines[0]);
        }

        [Fact]
        public void OnVesselSwitchRecState_RecoveryLoopSummary_PreservesSuppressedCountCadence()
        {
            double now = 2000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            var context = BuildRecoveryContext(activeVesselPid: 736156658, recorderVesselPid: 0);
            var snapshot = BuildRecStateSnapshot(activeVesselPid: 736156658);

            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", snapshot, context);
            now = 2001.0;
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", snapshot, context);
            now = 2002.0;
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", snapshot, context);
            now = 2005.1;
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", snapshot, context);

            List<string> recStateLines = RecStateLines();
            Assert.Equal(2, recStateLines.Count);
            Assert.DoesNotContain("suppressed=", recStateLines[0]);
            Assert.Contains("suppressed=2", recStateLines[1]);
        }

        [Fact]
        public void OnVesselSwitchRecState_RecoveryFingerprintChange_AllowsFreshDiagnostic()
        {
            double now = 3000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            var originalContext = BuildRecoveryContext(activeVesselPid: 736156658, recorderVesselPid: 0);
            var originalSnapshot = BuildRecStateSnapshot(activeVesselPid: 736156658);
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", originalSnapshot, originalContext);

            now = 3001.0;
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", originalSnapshot, originalContext);

            var newActiveContext = BuildRecoveryContext(activeVesselPid: 42, recorderVesselPid: 0);
            var newActiveSnapshot = BuildRecStateSnapshot(
                activeVesselPid: 42,
                activeRecId: "recNew",
                vesselName: "New Vessel");
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", newActiveSnapshot, newActiveContext);

            var chainPendingContext = BuildRecoveryContext(
                activeVesselPid: 736156658,
                recorderVesselPid: 0,
                recorderChainToVesselPending: true);
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", originalSnapshot, chainPendingContext);

            var newRecorderContext = BuildRecoveryContext(
                activeVesselPid: 736156658,
                recorderVesselPid: 123,
                hasRecorder: true,
                recorderIsRecording: true,
                activeVesselTrackedInBackground: false);
            var newRecorderSnapshot = BuildRecStateSnapshot(
                activeVesselPid: 123,
                activeRecId: "recLive",
                vesselName: "Live Recorder");
            ParsekFlight.LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry", newRecorderSnapshot, newRecorderContext);

            List<string> recStateLines = RecStateLines();
            Assert.Equal(4, recStateLines.Count);
            Assert.Contains("pid=736156658", recStateLines[0]);
            Assert.Contains("pid=42", recStateLines[1]);
            Assert.Contains("pid=736156658", recStateLines[2]);
            Assert.Contains("pid=123", recStateLines[3]);
        }

        [Fact]
        public void ShouldAttemptCommittedSpawnedRestoreInUpdate_WhenStableAndDue_ReturnsTrue()
        {
            bool shouldAttempt = ParsekFlight.ShouldAttemptCommittedSpawnedRestoreInUpdate(
                hasActiveTree: false,
                hasRecorder: false,
                isRestoringActiveTree: false,
                hasPendingTree: false,
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                currentUnscaledTime: 10f,
                nextRetryAt: 9f);

            Assert.True(shouldAttempt);
        }

        [Fact]
        public void ShouldAttemptCommittedSpawnedRestoreInUpdate_WhenBlockedOrThrottled_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldAttemptCommittedSpawnedRestoreInUpdate(
                hasActiveTree: true,
                hasRecorder: false,
                isRestoringActiveTree: false,
                hasPendingTree: false,
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                currentUnscaledTime: 10f,
                nextRetryAt: 0f));

            Assert.False(ParsekFlight.ShouldAttemptCommittedSpawnedRestoreInUpdate(
                hasActiveTree: false,
                hasRecorder: false,
                isRestoringActiveTree: false,
                hasPendingTree: true,
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                currentUnscaledTime: 10f,
                nextRetryAt: 0f));

            Assert.False(ParsekFlight.ShouldAttemptCommittedSpawnedRestoreInUpdate(
                hasActiveTree: false,
                hasRecorder: false,
                isRestoringActiveTree: false,
                hasPendingTree: false,
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.VesselSwitch,
                currentUnscaledTime: 10f,
                nextRetryAt: 0f));

            Assert.False(ParsekFlight.ShouldAttemptCommittedSpawnedRestoreInUpdate(
                hasActiveTree: false,
                hasRecorder: false,
                isRestoringActiveTree: false,
                hasPendingTree: false,
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                currentUnscaledTime: 10f,
                nextRetryAt: 11f));
        }

        [Fact]
        public void Update_CallsMissedVesselSwitchRecoveryBeforeTreeHandlers()
        {
            string parsekFlightPath = LocateParsekFlightSource();
            Assert.True(File.Exists(parsekFlightPath),
                $"ParsekFlight.cs not found at {parsekFlightPath}");

            string src = File.ReadAllText(parsekFlightPath);
            string methodBody = ExtractMethodBody(src, "void Update()");

            int clearIdx = methodBody.IndexOf("ClearStaleConfirmations();", StringComparison.Ordinal);
            int recoverIdx = methodBody.IndexOf("HandleMissedVesselSwitchRecovery();", StringComparison.Ordinal);
            int dockIdx = methodBody.IndexOf("HandleTreeDockMerge();", StringComparison.Ordinal);

            Assert.True(clearIdx >= 0, "Update() no longer calls ClearStaleConfirmations().");
            Assert.True(recoverIdx >= 0,
                "Update() no longer calls HandleMissedVesselSwitchRecovery().");
            Assert.True(dockIdx >= 0, "Update() no longer calls HandleTreeDockMerge().");
            Assert.True(clearIdx < recoverIdx && recoverIdx < dockIdx,
                "REGRESSION: missed vessel switch recovery must run immediately after " +
                "ClearStaleConfirmations() and before tree transition handlers so a " +
                "missed onVesselChange is reconciled before merge/background logic runs.");
        }

        private static string LocateParsekFlightSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", "ParsekFlight.cs"));
        }

        private static string ExtractMethodBody(string src, string methodSignature)
        {
            int methodStart = src.IndexOf(methodSignature, StringComparison.Ordinal);
            Assert.True(methodStart >= 0,
                $"{methodSignature} not found in ParsekFlight.cs.");

            int openBrace = src.IndexOf('{', methodStart);
            Assert.True(openBrace >= 0,
                $"{methodSignature} has no opening brace after its signature.");

            int depth = 0;
            int closeBrace = -1;
            for (int i = openBrace; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        break;
                    }
                }
            }

            Assert.True(closeBrace >= 0,
                $"{methodSignature} has unbalanced braces in ParsekFlight.cs.");
            return src.Substring(methodStart, closeBrace - methodStart + 1);
        }

        private List<string> RecStateLines()
        {
            return logLines.FindAll(l => l.Contains("[RecState]"));
        }

        private static RecorderStateSnapshot BuildRecStateSnapshot(
            uint activeVesselPid,
            string activeRecId = "recA",
            string vesselName = "Bob Kerman",
            bool recorderExists = true,
            bool isRecording = true,
            bool isBackgrounded = false)
        {
            var snapshot = default(RecorderStateSnapshot);
            snapshot.mode = RecorderMode.Tree;
            snapshot.treeId = "treeA";
            snapshot.treeName = "Recovery Tree";
            snapshot.activeRecId = activeRecId;
            snapshot.activeVesselName = vesselName;
            snapshot.activeVesselPid = activeVesselPid;
            snapshot.recorderExists = recorderExists;
            snapshot.isRecording = isRecording;
            snapshot.isBackgrounded = isBackgrounded;
            snapshot.bufferedPoints = 3;
            snapshot.bufferedPartEvents = 1;
            snapshot.bufferedOrbitSegments = 0;
            snapshot.lastRecordedUT = 123.4;
            snapshot.treeRecordingCount = 1;
            snapshot.treeBackgroundMapCount = 1;
            snapshot.currentUT = 130.0;
            snapshot.loadedScene = GameScenes.FLIGHT;
            return snapshot;
        }

        private static ParsekFlight.MissedVesselSwitchRecoveryDiagnosticContext BuildRecoveryContext(
            uint activeVesselPid,
            uint recorderVesselPid,
            bool hasRecorder = false,
            bool recorderIsRecording = false,
            bool recorderIsBackgrounded = false,
            bool recorderChainToVesselPending = false,
            bool activeVesselTrackedInBackground = true,
            bool activeVesselAlreadyArmedForPostSwitchAutoRecord = false,
            string activeTreeRecordingId = "recA",
            int activeTreeBackgroundMapCount = 1)
        {
            return new ParsekFlight.MissedVesselSwitchRecoveryDiagnosticContext
            {
                IsRecovery = true,
                ActiveVesselPid = activeVesselPid,
                RecorderVesselPid = recorderVesselPid,
                HasRecorder = hasRecorder,
                RecorderIsRecording = recorderIsRecording,
                RecorderIsBackgrounded = recorderIsBackgrounded,
                RecorderChainToVesselPending = recorderChainToVesselPending,
                ActiveVesselTrackedInBackground = activeVesselTrackedInBackground,
                ActiveVesselAlreadyArmedForPostSwitchAutoRecord =
                    activeVesselAlreadyArmedForPostSwitchAutoRecord,
                ActiveTreeRecordingId = activeTreeRecordingId,
                ActiveTreeBackgroundMapCount = activeTreeBackgroundMapCount
            };
        }
    }
}
