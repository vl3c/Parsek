using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KerbalLoadPipelineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalLoadPipelineTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KerbalLoadRepairDiagnostics.Reset();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            KerbalLoadRepairDiagnostics.Reset();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ColdStartLoadPath_RepairsStandInRows_AndPopulatesEvaRows()
        {
            var scenarioNode = BuildScenarioNodeWithSlotsAndReplacements();

            var slotRepair = MakeGhostOnlyRepairRecording(
                "rec-slot-repair",
                "Crew Ship",
                "Hanley Kerman",
                "Jebediah Kerman",
                KerbalEndState.Recovered);
            RecordingStore.AddRecordingWithTreeForTesting(slotRepair);

            var evaOnly = new Recording
            {
                RecordingId = "rec-eva-only",
                VesselName = "Bill Kerman",
                EvaCrewName = "Bill Kerman",
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 30,
                ExplicitEndUT = 45
            };
            RecordingStore.AddRecordingWithTreeForTesting(evaOnly);

            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-slot-repair",
                KerbalName = "Hanley Kerman",
                KerbalRole = "Pilot",
                StartUT = 10,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Unknown,
                Sequence = 1
            });

            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);
            LedgerOrchestrator.OnKspLoad(CollectRecordingIds(), 100.0);

            Assert.NotNull(LedgerOrchestrator.Kerbals);
            Assert.True(LedgerOrchestrator.Kerbals.Slots.ContainsKey("Jebediah Kerman"));
            Assert.Single(LedgerOrchestrator.Kerbals.Slots["Jebediah Kerman"].Chain);
            Assert.Equal("Hanley Kerman",
                LedgerOrchestrator.Kerbals.Slots["Jebediah Kerman"].Chain[0]);

            Assert.Contains(Ledger.Actions,
                a => a.RecordingId == "rec-slot-repair"
                    && a.KerbalName == "Jebediah Kerman"
                    && a.KerbalEndStateField == KerbalEndState.Recovered);
            Assert.Contains(Ledger.Actions,
                a => a.RecordingId == "rec-eva-only"
                    && a.KerbalName == "Bill Kerman"
                    && a.KerbalEndStateField == KerbalEndState.Dead);
        }

        [Fact]
        public void ColdStartLoadPath_SecondPass_DoesNotRewriteEquivalentRows()
        {
            var scenarioNode = BuildScenarioNodeWithSlotsAndReplacements();

            var slotRepair = MakeGhostOnlyRepairRecording(
                "rec-slot-repair",
                "Crew Ship",
                "Hanley Kerman",
                "Jebediah Kerman",
                KerbalEndState.Recovered);
            RecordingStore.AddRecordingWithTreeForTesting(slotRepair);

            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-slot-repair",
                KerbalName = "Hanley Kerman",
                KerbalRole = "Pilot",
                StartUT = 10,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Unknown,
                Sequence = 1
            });

            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);
            LedgerOrchestrator.OnKspLoad(CollectRecordingIds(), 100.0);

            logLines.Clear();
            LedgerOrchestrator.OnKspLoad(CollectRecordingIds(), 100.0);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("MigrateKerbalAssignments: repaired"));
        }

        private static HashSet<string> CollectRecordingIds()
        {
            var validIds = new HashSet<string>();
            var recordings = RecordingStore.CommittedRecordings;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (!string.IsNullOrEmpty(recordings[i].RecordingId))
                    validIds.Add(recordings[i].RecordingId);
            }
            return validIds;
        }

        private static ConfigNode BuildScenarioNodeWithSlotsAndReplacements()
        {
            var scenarioNode = new ConfigNode("SCENARIO");

            var slotsNode = scenarioNode.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jebediah Kerman");
            slotNode.AddValue("trait", "Pilot");
            var chainEntry = slotNode.AddNode("CHAIN_ENTRY");
            chainEntry.AddValue("name", "Hanley Kerman");

            var replacementsNode = scenarioNode.AddNode("CREW_REPLACEMENTS");
            var replacementEntry = replacementsNode.AddNode("ENTRY");
            replacementEntry.AddValue("original", "Jebediah Kerman");
            replacementEntry.AddValue("replacement", "Hanley Kerman");

            return scenarioNode;
        }

        private static Recording MakeGhostOnlyRepairRecording(
            string recordingId,
            string vesselName,
            string rawCrewName,
            string logicalCrewName,
            KerbalEndState endState)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", rawCrewName);

            return new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 10,
                ExplicitEndUT = 20,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { logicalCrewName, endState }
                }
            };
        }

        private static void InvokeLoadCrewAndGroupState(
            ConfigNode node, bool initialLoadDoneValue)
        {
            FieldInfo initialLoadDoneField = typeof(ParsekScenario).GetField(
                "initialLoadDone", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo loadMethod = typeof(ParsekScenario).GetMethod(
                "LoadCrewAndGroupState", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(initialLoadDoneField);
            Assert.NotNull(loadMethod);

            bool previousInitialLoadDone = (bool)initialLoadDoneField.GetValue(null);
            try
            {
                initialLoadDoneField.SetValue(null, initialLoadDoneValue);
                loadMethod.Invoke(null, new object[] { node });
            }
            finally
            {
                initialLoadDoneField.SetValue(null, previousInitialLoadDone);
            }
        }
    }
}
