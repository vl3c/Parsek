using System;
using System.Collections.Generic;
using KSP.UI;
using KSP.UI.Screens;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KerbalHirePatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorGameStateStoreSuppress;
        private readonly bool priorRecordingStoreSuppress;
        private bool dialogHookCalled;

        public KerbalHirePatchTests()
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
        /// Allows hire when not committed. Fails if the patch blocks applicants outside the committed helper set.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_AllowsHireWhenNotCommitted_NoDialogLogAndReturnsTrue()
        {
            bool allowed = KerbalHirePatch.ShouldAllowHire("Uncommitted Kerman");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
            Assert.DoesNotContain(logLines, line => line.Contains("[KerbalHirePatch]") && line.Contains("blocking"));
        }

        /// <summary>
        /// Blocks when committed. Fails if the patch does not return false or stops logging the block before showing the dialog.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_BlocksWhenCommitted_LogsAndReturnsFalse()
        {
            AddMilestone(Event(GameStateEventType.CrewHired, "Future Kerman", ut: 12345.0));

            bool allowed = KerbalHirePatch.ShouldAllowHire("Future Kerman");

            Assert.False(allowed);
            Assert.True(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][KerbalHirePatch]") &&
                line.Contains("blocking hire for name=Future Kerman") &&
                line.Contains("committed at UT 12345"));
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][CommittedAction]") &&
                line.Contains("Blocked action: Cannot hire \"Future Kerman\""));
        }

        /// <summary>
        /// Bypasses block when GameStateRecorder.IsReplayingActions is true. Fails if Parsek's own replay cannot hire committed kerbals.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_BypassesWhenReplayingActions_LogsAndReturnsTrue()
        {
            AddMilestone(Event(GameStateEventType.CrewHired, "Replay Kerman", ut: 23456.0));
            GameStateRecorder.IsReplayingActions = true;

            bool allowed = KerbalHirePatch.ShouldAllowHire("Replay Kerman");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][KerbalHirePatch]") &&
                line.Contains("bypass") &&
                line.Contains("replay in progress"));
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
        }

        /// <summary>
        /// Edge case E11 from §9. Fails if disabling committed-action click-blocks still blocks a committed hire.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_ClickBlockSettingDisabled_AllowsCommittedAndLogs()
        {
            AddMilestone(Event(GameStateEventType.CrewHired, "Disabled Setting Kerman", ut: 45678.0));
            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { blockCommittedActions = false };

            bool allowed = KerbalHirePatch.ShouldAllowHire("Disabled Setting Kerman");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][KerbalHirePatch]") &&
                line.Contains("feature disabled by ParsekSettings"));
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
        }

        /// <summary>
        /// Invariant test for §3/§8.3. Fails if the patch uses KerbalsModule.IsManaged instead of GetCommittedKerbalHireNames.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_PredicateUsesCommittedKerbalHireNames_NotReservations()
        {
            AddMilestone(Event(GameStateEventType.CrewHired, "Future Hire Kerman", ut: 34567.0));

            var reservedOnlyRec = MakeRecording(
                "Reservation Ship",
                new[] { "Reserved Only Kerman" },
                TerminalState.Recovered,
                2000.0);
            RecordingStore.AddRecordingWithTreeForTesting(reservedOnlyRec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            bool futureHireAllowed = KerbalHirePatch.ShouldAllowHire("Future Hire Kerman");
            bool reservedOnlyAllowed = KerbalHirePatch.ShouldAllowHire("Reserved Only Kerman");

            Assert.False(futureHireAllowed);
            Assert.True(reservedOnlyAllowed);
            Assert.True(kerbals.IsManaged("Reserved Only Kerman"));
            Assert.Contains(MilestoneStore.GetCommittedKerbalHireNames(), name => name == "Future Hire Kerman");
            Assert.DoesNotContain(MilestoneStore.GetCommittedKerbalHireNames(), name => name == "Reserved Only Kerman");
        }

        /// <summary>
        /// Stock Astronaut Complex mutates its applicant/enlisted rows before KerbalRoster.HireApplicant(). Fails if the early stock-UI prefix can no longer find AstronautComplex.HireRecruit().
        /// </summary>
        [Fact]
        public void AstronautComplexHireRecruitPatch_TargetsStockHireRecruitBeforeUiMutation()
        {
            var method = AstronautComplexHireRecruitPatch.ResolveTargetMethodForTesting();

            Assert.NotNull(method);
            Assert.Equal(typeof(AstronautComplex), method.DeclaringType);
            Assert.Equal("HireRecruit", method.Name);

            var parameters = method.GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(UIList), parameters[0].ParameterType);
            Assert.Equal(typeof(UIList), parameters[1].ParameterType);
            Assert.Equal(typeof(UIListItem), parameters[2].ParameterType);
        }

        private static Recording MakeRecording(
            string vesselName,
            string[] crew,
            TerminalState terminal,
            double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);

            var rec = new Recording
            {
                VesselName = vesselName,
                VesselSnapshot = snapshot,
                TerminalStateValue = terminal,
                ExplicitStartUT = 0,
                ExplicitEndUT = endUT,
                CrewEndStates = new Dictionary<string, KerbalEndState>()
            };

            var endCrewSet = new HashSet<string>(crew);
            for (int i = 0; i < crew.Length; i++)
            {
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(
                    crew[i], terminal, endCrewSet);
            }

            return rec;
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
