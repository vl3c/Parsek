using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    public class AutoRecordDecisionTests
    {
        [Fact]
        public void EvaluateAutoRecordLaunchDecision_RecordingInProgress_SkipsAlreadyRecording()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: true,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 0,
                currentUt: 10,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipAlreadyRecording, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_NonActiveVessel_SkipsInactiveVessel()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: false,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 0,
                currentUt: 10,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipInactiveVessel, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_PrelaunchAndEnabled_StartsFromPrelaunch()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: -1,
                currentUt: 10,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.StartFromPrelaunch, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_PrelaunchButDisabled_SkipsDisabled()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: false,
                lastLandedUt: -1,
                currentUt: 10,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipDisabled, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_LandedBelowSettleThreshold_SkipsBounce()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 96.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipBounce, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_LandedWithoutSeededSettleTime_SkipsBounce()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: -1.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipBounce, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_SettledLandedAndEnabled_StartsFromSettledLanded()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 95.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.StartFromSettledLanded, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_SettledLandedButDisabled_SkipsDisabled()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: false,
                lastLandedUt: 95.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipDisabled, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_OtherTransition_SkipsNotLaunchTransition()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.FLYING,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 95.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: false);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipNotLaunchTransition, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_TimeJumpTransient_SkipsAutoStart()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: -1.0,
                currentUt: 102.9,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: true);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipTimeJumpTransient, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_TimeJumpTransient_NonActiveVessel_StillReportsTransient()
        {
            // During a Real Spawn Control / Timeline FF jump the playback policy can spawn synthetic
            // vessels mid-window. Their stock-KSP situation flickers (e.g. 0 -> ORBITING) hit
            // OnVesselSituationChange even though they are not the focused vessel. Reporting them as
            // SkipInactiveVessel hides the time-jump origin and starves the in-game pad canaries of
            // the [INFO][Flight] "suppressing time-jump transient" log they assert on.
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: false,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: -1.0,
                currentUt: 102.9,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: true);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipTimeJumpTransient, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_AlreadyRecording_TakesPrecedenceOverTimeJumpTransient()
        {
            // Defence in depth: even when a jump-window flicker arrives for the active vessel, an
            // already-running recording must short-circuit before any other branch so we do not
            // double-emit StartRecording or log a misleading suppression line.
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: true,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: -1.0,
                currentUt: 102.9,
                landedSettleThreshold: 5.0,
                suppressForTimeJumpTransient: true);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipAlreadyRecording, decision);
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(false, false, false)]
        public void ShouldQueueAutoRecordOnEva_RequiresSourceVesselAndEnabledSetting(
            bool hasSourceVessel,
            bool autoRecordOnEvaEnabled,
            bool expected)
        {
            bool result = ParsekFlight.ShouldQueueAutoRecordOnEva(
                hasSourceVessel,
                autoRecordOnEvaEnabled);

            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(true, false, true, true, true)]
        [InlineData(false, false, true, true, false)]
        [InlineData(true, true, true, true, false)]
        [InlineData(true, false, false, false, false)]
        [InlineData(true, false, true, false, false)]
        public void ShouldStartDeferredAutoRecordEva_RequiresPendingIdleActiveEva(
            bool pendingAutoRecord,
            bool isRecording,
            bool hasActiveVessel,
            bool activeVesselIsEva,
            bool expected)
        {
            bool result = ParsekFlight.ShouldStartDeferredAutoRecordEva(
                pendingAutoRecord,
                isRecording,
                hasActiveVessel,
                activeVesselIsEva);

            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public void ShouldShowStartRecordingScreenMessage_RequiresFreshUnsuppressedStart(
            bool isPromotion,
            bool suppressStartScreenMessage,
            bool expected)
        {
            bool result = FlightRecorder.ShouldShowStartRecordingScreenMessage(
                isPromotion,
                suppressStartScreenMessage);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void LaunchAutoRecord_UsesCustomToastWithoutGenericStartToast()
        {
            string source = File.ReadAllText(FindParsekFlightSource());
            string methodBody = ExtractMethodBody(source, "void OnVesselSituationChange");

            AssertCustomToastSuppressesGenericStart(
                methodBody,
                "ScreenMessage(\"Recording STARTED (auto)\", 2f);",
                "Launch auto-record");
        }

        [Fact]
        public void PostSwitchFreshAutoRecord_UsesCustomToastWithoutGenericStartToast()
        {
            string source = File.ReadAllText(FindParsekFlightSource());
            string methodBody = ExtractMethodBody(source, "private bool TryStartPostSwitchAutoRecord");

            AssertCustomToastSuppressesGenericStart(
                methodBody,
                "ScreenMessage(\"Recording STARTED (auto - post switch)\", 2f);",
                "Post-switch fresh auto-record");
        }

        [Fact]
        public void EvaFromPadAutoRecord_UsesCustomToastWithoutGenericStartToast()
        {
            string source = File.ReadAllText(FindParsekFlightSource());
            string methodBody = ExtractMethodBody(source, "private void HandleDeferredAutoRecordEva");

            AssertCustomToastSuppressesGenericStart(
                methodBody,
                "ScreenMessage(\"Recording STARTED (auto - EVA from pad)\", 2f);",
                "EVA-from-pad auto-record");
        }

        [Fact]
        public void ShouldIgnoreFlightReadyReset_LiveRecorderWithoutPendingRestore_ReturnsTrue()
        {
            bool result = ParsekFlight.ShouldIgnoreFlightReadyReset(
                hasActiveRecorder: true,
                hasActiveTree: true,
                hasPendingTree: false,
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None);

            Assert.True(result);
        }

        [Theory]
        [InlineData(false, true, false, (int)ParsekScenario.ActiveTreeRestoreMode.None)]
        [InlineData(true, false, false, (int)ParsekScenario.ActiveTreeRestoreMode.None)]
        [InlineData(true, true, true, (int)ParsekScenario.ActiveTreeRestoreMode.None)]
        [InlineData(true, true, false, (int)ParsekScenario.ActiveTreeRestoreMode.Quickload)]
        [InlineData(true, true, false, (int)ParsekScenario.ActiveTreeRestoreMode.VesselSwitch)]
        public void ShouldIgnoreFlightReadyReset_RestoreOrMissingLiveState_ReturnsFalse(
            bool hasActiveRecorder,
            bool hasActiveTree,
            bool hasPendingTree,
            int restoreMode)
        {
            bool result = ParsekFlight.ShouldIgnoreFlightReadyReset(
                hasActiveRecorder,
                hasActiveTree,
                hasPendingTree,
                (ParsekScenario.ActiveTreeRestoreMode)restoreMode);

            Assert.False(result);
        }

        private static string FindParsekFlightSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", "ParsekFlight.cs"));
        }

        private static void AssertCustomToastSuppressesGenericStart(
            string methodBody,
            string customToastCall,
            string context)
        {
            int startIdx = methodBody.IndexOf(
                "StartRecording(suppressStartScreenMessage: true);",
                StringComparison.Ordinal);
            int toastIdx = methodBody.IndexOf(customToastCall, StringComparison.Ordinal);

            Assert.True(startIdx >= 0,
                $"{context} must suppress the generic FlightRecorder start toast.");
            Assert.True(toastIdx >= 0,
                $"{context} should keep its custom start toast.");
            Assert.True(startIdx < toastIdx,
                $"{context} must suppress the generic toast before posting the custom one.");
        }

        private static string ExtractMethodBody(string source, string methodSignature)
        {
            // This is intentionally a lightweight source-level guard for UI glue that is
            // hard to instantiate headlessly; production behavior remains covered by the
            // pure decision tests above.
            int methodStart = source.IndexOf(methodSignature, StringComparison.Ordinal);
            Assert.True(methodStart >= 0,
                $"{methodSignature} not found in ParsekFlight.cs.");

            int openBrace = source.IndexOf('{', methodStart);
            Assert.True(openBrace >= 0,
                $"{methodSignature} has no opening brace in ParsekFlight.cs.");

            int depth = 0;
            for (int i = openBrace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(methodStart, i - methodStart + 1);
                }
            }

            Assert.True(false,
                $"{methodSignature} has unbalanced braces in ParsekFlight.cs.");
            return string.Empty;
        }
    }
}
