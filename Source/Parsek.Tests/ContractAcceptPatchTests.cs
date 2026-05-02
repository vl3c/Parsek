using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ContractAcceptPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorGameStateStoreSuppress;
        private readonly bool priorRecordingStoreSuppress;
        private bool dialogHookCalled;

        public ContractAcceptPatchTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorGameStateStoreSuppress = GameStateStore.SuppressLogging;
            priorRecordingStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;

            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { blockCommittedActions = true };

            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogHookCalled = true;
            };
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;

            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            GameStateStore.SuppressLogging = priorGameStateStoreSuppress;
            RecordingStore.SuppressLogging = priorRecordingStoreSuppress;
        }

        /// <summary>
        /// Allows accept when not committed. Fails if the patch blocks contracts outside the committed helper set.
        /// </summary>
        [Fact]
        public void ContractAcceptPatch_AllowsAcceptWhenNotCommitted_NoDialogLogAndReturnsTrue()
        {
            string key = Guid.NewGuid().ToString();

            bool allowed = ContractAcceptPatch.ShouldAllowAccept(key, "Uncommitted Contract");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
            Assert.DoesNotContain(logLines, line => line.Contains("[ContractAcceptPatch]") && line.Contains("blocking"));
        }

        /// <summary>
        /// Blocks when committed. Fails if the patch does not return false or stops logging the block before showing the dialog.
        /// </summary>
        [Fact]
        public void ContractAcceptPatch_BlocksWhenCommitted_LogsAndReturnsFalse()
        {
            string key = Guid.NewGuid().ToString();
            AddMilestone(Event(GameStateEventType.ContractAccepted, key, ut: 12345.0));

            bool allowed = ContractAcceptPatch.ShouldAllowAccept(key, "Committed Contract");

            Assert.False(allowed);
            Assert.True(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][ContractAcceptPatch]") &&
                line.Contains("blocking accept for guid=" + key) &&
                line.Contains("committed at UT 12345"));
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][CommittedAction]") &&
                line.Contains("Blocked action: Cannot accept \"Committed Contract\""));
        }

        /// <summary>
        /// Bypasses block when GameStateRecorder.IsReplayingActions is true. Fails if Parsek's own replay cannot accept committed contracts.
        /// </summary>
        [Fact]
        public void ContractAcceptPatch_BypassesWhenReplayingActions_LogsAndReturnsTrue()
        {
            string key = Guid.NewGuid().ToString();
            AddMilestone(Event(GameStateEventType.ContractAccepted, key, ut: 23456.0));
            GameStateRecorder.IsReplayingActions = true;

            bool allowed = ContractAcceptPatch.ShouldAllowAccept(key, "Replay Contract");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][ContractAcceptPatch]") &&
                line.Contains("bypass") &&
                line.Contains("replay in progress"));
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
        }

        /// <summary>
        /// Edge case E11 from §9. Fails if disabling committed-action click-blocks still blocks a committed contract.
        /// </summary>
        [Fact]
        public void ContractAcceptPatch_ClickBlockSettingDisabled_AllowsCommittedAndLogs()
        {
            string key = Guid.NewGuid().ToString();
            AddMilestone(Event(GameStateEventType.ContractAccepted, key, ut: 45678.0));
            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { blockCommittedActions = false };

            bool allowed = ContractAcceptPatch.ShouldAllowAccept(key, "Disabled Setting Contract");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][ContractAcceptPatch]") &&
                line.Contains("feature disabled by ParsekSettings"));
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
        }

        /// <summary>
        /// Invariant test for §3/§8.3. Fails if the patch stops using GetCommittedContractAcceptIds as the exact predicate source.
        /// </summary>
        [Fact]
        public void ContractAcceptPatch_PredicateUsesCommittedContractAcceptIdsRawKey()
        {
            Guid guid = Guid.NewGuid();
            string recorderShape = guid.ToString();
            string noHyphenShape = guid.ToString("N");
            AddMilestone(Event(GameStateEventType.ContractAccepted, recorderShape, ut: 34567.0));

            bool recorderShapeAllowed = ContractAcceptPatch.ShouldAllowAccept(recorderShape, "Recorder Shape");
            bool noHyphenAllowed = ContractAcceptPatch.ShouldAllowAccept(noHyphenShape, "No Hyphen Shape");

            Assert.False(recorderShapeAllowed);
            Assert.True(noHyphenAllowed);
            Assert.Contains(MilestoneStore.GetCommittedContractAcceptIds(), id => id == recorderShape);
            Assert.DoesNotContain(MilestoneStore.GetCommittedContractAcceptIds(), id => id == noHyphenShape);
        }

        private static GameStateEvent Event(
            GameStateEventType type,
            string key,
            double ut = 100.0)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key
            };
        }

        private static void AddMilestone(GameStateEvent ev)
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = Guid.NewGuid().ToString("N"),
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent> { ev }
            });
        }
    }
}
