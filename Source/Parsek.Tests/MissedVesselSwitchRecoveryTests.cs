using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MissedVesselSwitchRecoveryTests
    {
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
                activeVesselTrackedInBackground: false);

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
                activeVesselTrackedInBackground: true);

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
                activeVesselTrackedInBackground: false);

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
                activeVesselTrackedInBackground: false);

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
                activeVesselTrackedInBackground: false);

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
                activeVesselTrackedInBackground: false);

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
                activeVesselTrackedInBackground: false);

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
                activeVesselTrackedInBackground: false);

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
                activeVesselTrackedInBackground: false);

            Assert.False(noTree);
            Assert.False(restoring);
            Assert.False(zeroActivePid);
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
    }
}
