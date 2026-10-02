using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The DiscardTree seam verb's committed-spawned restore suppression
    /// (todo HARNESS-BOOT-DISCARD-RACES-COMMITTED-SPAWNED-RESTORE): after the verb tears
    /// down the tree a recorded-fixture boot restored, the 1 Hz Update retry must not
    /// re-clone and re-adopt the same vessel's committed tree until a live tree or
    /// recorder exists, the active vessel changes, or the scene ends.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedSpawnedRestoreSuppressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedSpawnedRestoreSuppressionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- IsCommittedSpawnedRestoreSuppressedForVessel -----

        [Fact]
        public void IsSuppressed_SameVessel_True()
        {
            Assert.True(ParsekFlight.IsCommittedSpawnedRestoreSuppressedForVessel(
                suppressedPid: 4242u, activeVesselPid: 4242u));
        }

        [Fact]
        public void IsSuppressed_OtherVesselOrNoneArmedOrZeroActive_False()
        {
            Assert.False(ParsekFlight.IsCommittedSpawnedRestoreSuppressedForVessel(
                suppressedPid: 4242u, activeVesselPid: 99u));
            Assert.False(ParsekFlight.IsCommittedSpawnedRestoreSuppressedForVessel(
                suppressedPid: 0u, activeVesselPid: 4242u));
            Assert.False(ParsekFlight.IsCommittedSpawnedRestoreSuppressedForVessel(
                suppressedPid: 0u, activeVesselPid: 0u));
        }

        // ----- ResolveCommittedSpawnedRestoreSuppressionClearReason -----

        [Fact]
        public void ClearReason_NoneArmed_Null()
        {
            Assert.Null(ParsekFlight.ResolveCommittedSpawnedRestoreSuppressionClearReason(
                suppressedPid: 0u, activeVesselPid: 7u, hasActiveTree: true, hasRecorder: true));
        }

        [Fact]
        public void ClearReason_SameVesselIdle_KeepsSuppression()
        {
            Assert.Null(ParsekFlight.ResolveCommittedSpawnedRestoreSuppressionClearReason(
                suppressedPid: 4242u, activeVesselPid: 4242u, hasActiveTree: false, hasRecorder: false));
        }

        [Fact]
        public void ClearReason_ZeroActivePidMidSwitch_KeepsSuppression()
        {
            Assert.Null(ParsekFlight.ResolveCommittedSpawnedRestoreSuppressionClearReason(
                suppressedPid: 4242u, activeVesselPid: 0u, hasActiveTree: false, hasRecorder: false));
        }

        [Fact]
        public void ClearReason_ActiveVesselChanged_Clears()
        {
            // The EVA case: the kerbal becomes the active vessel.
            Assert.Equal("active-vessel-changed",
                ParsekFlight.ResolveCommittedSpawnedRestoreSuppressionClearReason(
                    suppressedPid: 4242u, activeVesselPid: 99u, hasActiveTree: false, hasRecorder: false));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void ClearReason_LiveTreeOrRecorder_Clears(bool hasActiveTree, bool hasRecorder)
        {
            // StartRecording (or any adoption) on the same vessel ends the idle request.
            Assert.Equal("live-tree-or-recorder",
                ParsekFlight.ResolveCommittedSpawnedRestoreSuppressionClearReason(
                    suppressedPid: 4242u, activeVesselPid: 4242u,
                    hasActiveTree: hasActiveTree, hasRecorder: hasRecorder));
        }

        // ----- BeginCommittedSpawnedRestoreSuppression -----

        [Fact]
        public void Begin_IdleActiveVessel_ArmsAndLogs()
        {
            uint suppressed = 0u;
            bool armed = ParsekFlight.BeginCommittedSpawnedRestoreSuppression(
                ref suppressed, activeVesselPid: 4242u, activeVesselName: "Kerbal X",
                hasActiveTree: false, hasRecorder: false, reason: "test-command-discard");

            Assert.True(armed);
            Assert.Equal(4242u, suppressed);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("CommittedSpawnedRestoreSuppression: armed pid=4242")
                && l.Contains("vessel='Kerbal X'")
                && l.Contains("reason=test-command-discard")
                && l.Contains("previousPid=0"));
        }

        [Fact]
        public void Begin_NoActiveVessel_RefusesAndLogs()
        {
            uint suppressed = 0u;
            bool armed = ParsekFlight.BeginCommittedSpawnedRestoreSuppression(
                ref suppressed, activeVesselPid: 0u, activeVesselName: null,
                hasActiveTree: false, hasRecorder: false, reason: "test-command-discard");

            Assert.False(armed);
            Assert.Equal(0u, suppressed);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("CommittedSpawnedRestoreSuppression: not armed reason=test-command-discard")
                && l.Contains("(no active vessel)"));
        }

        [Fact]
        public void Begin_LiveTreeStillPresent_RefusesAndKeepsPrevious()
        {
            uint suppressed = 77u;
            bool armed = ParsekFlight.BeginCommittedSpawnedRestoreSuppression(
                ref suppressed, activeVesselPid: 4242u, activeVesselName: "Kerbal X",
                hasActiveTree: true, hasRecorder: false, reason: "test-command-discard");

            Assert.False(armed);
            Assert.Equal(77u, suppressed);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("CommittedSpawnedRestoreSuppression: not armed")
                && l.Contains("pid=4242")
                && l.Contains("live tree=True"));
        }

        // ----- TickCommittedSpawnedRestoreSuppression -----

        [Fact]
        public void Tick_SameVesselIdle_KeepsSilently()
        {
            uint suppressed = 4242u;
            string reason = ParsekFlight.TickCommittedSpawnedRestoreSuppression(
                ref suppressed, activeVesselPid: 4242u, hasActiveTree: false, hasRecorder: false);

            Assert.Null(reason);
            Assert.Equal(4242u, suppressed);
            Assert.DoesNotContain(logLines, l => l.Contains("CommittedSpawnedRestoreSuppression"));
        }

        [Fact]
        public void Tick_ActiveVesselChanged_ClearsAndLogsOnce()
        {
            uint suppressed = 4242u;
            string reason = ParsekFlight.TickCommittedSpawnedRestoreSuppression(
                ref suppressed, activeVesselPid: 99u, hasActiveTree: false, hasRecorder: false);

            Assert.Equal("active-vessel-changed", reason);
            Assert.Equal(0u, suppressed);

            // A second tick is a no-op: nothing armed, nothing logged.
            Assert.Null(ParsekFlight.TickCommittedSpawnedRestoreSuppression(
                ref suppressed, activeVesselPid: 99u, hasActiveTree: false, hasRecorder: false));

            Assert.Single(logLines, l => l.Contains("[Flight]")
                && l.Contains("CommittedSpawnedRestoreSuppression: cleared pid=4242")
                && l.Contains("reason=active-vessel-changed")
                && l.Contains("activePid=99"));
        }

        [Fact]
        public void Tick_LiveRecorder_Clears()
        {
            uint suppressed = 4242u;
            string reason = ParsekFlight.TickCommittedSpawnedRestoreSuppression(
                ref suppressed, activeVesselPid: 4242u, hasActiveTree: false, hasRecorder: true);

            Assert.Equal("live-tree-or-recorder", reason);
            Assert.Equal(0u, suppressed);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("CommittedSpawnedRestoreSuppression: cleared pid=4242")
                && l.Contains("reason=live-tree-or-recorder"));
        }

        [Fact]
        public void BootDiscardEvaSequence_SuppressesUntilTheKerbalIsActive()
        {
            // The EVA-6 shape: boot restore adopts the capsule (4242), the lane's
            // DiscardTree arms the suppression, the retry ticks while the capsule is still
            // active must not re-adopt it, and the EvaExit (kerbal 99 active) clears it so
            // the kerbal's StartRecording sees no stale tree.
            uint suppressed = 0u;
            Assert.True(ParsekFlight.BeginCommittedSpawnedRestoreSuppression(
                ref suppressed, 4242u, "Kerbal X", false, false, "test-command-discard"));

            for (int tick = 0; tick < 5; tick++)
            {
                Assert.Null(ParsekFlight.TickCommittedSpawnedRestoreSuppression(
                    ref suppressed, 4242u, hasActiveTree: false, hasRecorder: false));
                Assert.True(ParsekFlight.ShouldAttemptCommittedSpawnedRestoreInUpdate(
                    hasActiveTree: false, hasRecorder: false, isRestoringActiveTree: false,
                    hasPendingTree: false, restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                    currentUnscaledTime: 10f + tick, nextRetryAt: 0f));
                Assert.True(ParsekFlight.IsCommittedSpawnedRestoreSuppressedForVessel(
                    suppressed, 4242u));
            }

            Assert.Equal("active-vessel-changed",
                ParsekFlight.TickCommittedSpawnedRestoreSuppression(
                    ref suppressed, 99u, hasActiveTree: false, hasRecorder: false));
            Assert.False(ParsekFlight.IsCommittedSpawnedRestoreSuppressedForVessel(suppressed, 4242u));
            Assert.False(ParsekFlight.IsCommittedSpawnedRestoreSuppressedForVessel(suppressed, 99u));
        }

        // ----- Source gates: the wiring the pure cells cannot see -----

        [Fact]
        public void TryRestore_ChecksSuppressionBeforeTakingTheCommittedTree()
        {
            string src = ReadStripped("Source", "Parsek", "ParsekFlight.cs");
            string body = ExtractMethodBody(src, "private bool TryRestoreCommittedTreeForSpawnedActiveVessel()");

            int checkIdx = body.IndexOf("IsCommittedSpawnedRestoreSuppressedForVessel(", StringComparison.Ordinal);
            int takeIdx = body.IndexOf("TryTakeCommittedTreeForSpawnedVesselRestore(", StringComparison.Ordinal);
            Assert.True(checkIdx >= 0,
                "TryRestoreCommittedTreeForSpawnedActiveVessel no longer checks the DiscardTree suppression.");
            Assert.True(takeIdx >= 0,
                "TryRestoreCommittedTreeForSpawnedActiveVessel no longer calls TryTakeCommittedTreeForSpawnedVesselRestore.");
            Assert.True(checkIdx < takeIdx,
                "The suppression check must run before TryTakeCommittedTreeForSpawnedVesselRestore, " +
                "which clones the committed tree and arms its restore attempt.");
        }

        [Fact]
        public void HandleMissedVesselSwitchRecovery_TicksSuppressionBeforeTheRetry()
        {
            string src = ReadStripped("Source", "Parsek", "ParsekFlight.cs");
            string body = ExtractMethodBody(src, "private void HandleMissedVesselSwitchRecovery()");

            int tickIdx = body.IndexOf("TickCommittedSpawnedRestoreSuppression(", StringComparison.Ordinal);
            int retryIdx = body.IndexOf("ShouldAttemptCommittedSpawnedRestoreInUpdate(", StringComparison.Ordinal);
            Assert.True(tickIdx >= 0, "HandleMissedVesselSwitchRecovery no longer clears the suppression.");
            Assert.True(retryIdx >= 0, "HandleMissedVesselSwitchRecovery no longer runs the restore retry.");
            Assert.True(tickIdx < retryIdx,
                "The suppression clear must run before the retry so a vessel change is seen first.");
        }

        [Fact]
        public void DiscardTreeVerb_ArmsSuppressionAfterTheDiscard()
        {
            string src = ReadStripped("Source", "Parsek", "TestCommands", "ParsekTestCommandAddon.cs");
            string body = ExtractMethodBody(src, "private void DiscardTreeImpl(ParsedCommand cmd)");

            int discardIdx = body.IndexOf("AutoDiscardActiveTreeWithMessage(", StringComparison.Ordinal);
            int suppressIdx = body.IndexOf("SuppressCommittedSpawnedRestoreForActiveVessel(", discardIdx < 0 ? 0 : discardIdx, StringComparison.Ordinal);
            Assert.True(discardIdx >= 0, "DiscardTreeImpl no longer calls AutoDiscardActiveTreeWithMessage.");
            Assert.True(suppressIdx > discardIdx,
                "DiscardTreeImpl must arm the committed-spawned restore suppression AFTER the discard " +
                "(arming before it is refused while the tree is still live).");
        }

        [Fact]
        public void SuppressionSetter_HasExactlyOneProductionCaller_TheSeamVerb()
        {
            // Player discard paths must not idle a committed vessel: re-adoption is the
            // product rule there. Every caller lives in the seam verb.
            string root = LocateRepoRoot();
            string sourceDir = Path.Combine(root, "Source", "Parsek");
            var callers = new List<string>();
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = SourceScanText.StripCommentsAndMaskLiterals(
                    File.ReadAllText(file).Replace("\r\n", "\n"));
                if (text.Contains(".SuppressCommittedSpawnedRestoreForActiveVessel("))
                    callers.Add(Path.GetFileName(file));
            }
            Assert.Equal(new[] { "ParsekTestCommandAddon.cs" }, callers.ToArray());
        }

        private static string ReadStripped(params string[] relative)
        {
            string path = Path.Combine(LocateRepoRoot(), Path.Combine(relative));
            Assert.True(File.Exists(path), $"source not found at {path}");
            return SourceScanText.StripCommentsAndMaskLiterals(
                File.ReadAllText(path).Replace("\r\n", "\n"));
        }

        private static string LocateRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }

        private static string ExtractMethodBody(string src, string methodSignature)
        {
            int methodStart = src.IndexOf(methodSignature, StringComparison.Ordinal);
            Assert.True(methodStart >= 0, $"{methodSignature} not found.");
            int openBrace = src.IndexOf('{', methodStart);
            Assert.True(openBrace >= 0, $"{methodSignature} has no opening brace.");
            int depth = 0;
            for (int i = openBrace; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return src.Substring(methodStart, i - methodStart + 1);
                }
            }
            Assert.True(false, $"{methodSignature} has unbalanced braces.");
            return null;
        }
    }
}
