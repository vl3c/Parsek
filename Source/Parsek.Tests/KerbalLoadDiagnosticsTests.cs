using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KerbalLoadDiagnosticsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalLoadDiagnosticsTests()
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
        public void OnKspLoad_WithRemapAndTouristSkip_LogsSingleSummary()
        {
            var scenarioNode = new ConfigNode("SCENARIO");
            var replacementsNode = scenarioNode.AddNode("CREW_REPLACEMENTS");
            var replacementEntry = replacementsNode.AddNode("ENTRY");
            replacementEntry.AddValue("original", "Jebediah Kerman");
            replacementEntry.AddValue("replacement", "Hanley Kerman");

            var slotRepair = MakeGhostOnlyRepairRecording(
                "rec-slot-repair",
                "Crew Ship",
                "Hanley Kerman",
                "Jebediah Kerman",
                KerbalEndState.Recovered);
            RecordingStore.AddRecordingWithTreeForTesting(slotRepair);

            var touristRec = MakeSnapshotRecording(
                "rec-tourist",
                "Tour Bus",
                new[] { "Tourist Kerman" },
                TerminalState.Recovered,
                30);
            RecordingStore.AddRecordingWithTreeForTesting(touristRec);

            var baseline = new GameStateBaseline();
            baseline.crewEntries.Add(new GameStateBaseline.CrewEntry
            {
                name = "Tourist Kerman",
                trait = "Tourist",
                status = "Available"
            });
            GameStateStore.AddBaseline(baseline);

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
            Ledger.AddAction(new GameAction
            {
                UT = 20.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-tourist",
                KerbalName = "Tourist Kerman",
                KerbalRole = "Tourist",
                StartUT = 20,
                EndUT = 30,
                KerbalEndStateField = KerbalEndState.Recovered,
                Sequence = 1
            });

            KerbalLoadRepairDiagnostics.Begin();
            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);
            LedgerOrchestrator.OnKspLoad(CollectRecordingIds(), 100.0);

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:")
                && l.Contains("slotSource=legacy-creplacements")
                && l.Contains("remappedRows=1")
                && l.Contains("touristRowsSkipped=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair samples:")
                && l.Contains("remap rec-slot-repair Hanley Kerman->Jebediah Kerman"));
        }

        [Fact]
        public void ApplyToRoster_FacadePath_RecordsRetiredRecreated_AndDeletedUnused()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");

            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var jebSlot = slotsNode.AddNode("SLOT");
            jebSlot.AddValue("owner", "Jeb");
            jebSlot.AddValue("trait", "Pilot");
            var jebFirst = jebSlot.AddNode("CHAIN_ENTRY");
            jebFirst.AddValue("name", "Hanley");
            var jebSecond = jebSlot.AddNode("CHAIN_ENTRY");
            jebSecond.AddValue("name", "Kirrim");

            var valSlot = slotsNode.AddNode("SLOT");
            valSlot.AddValue("owner", "Val");
            valSlot.AddValue("trait", "Pilot");
            var valFirst = valSlot.AddNode("CHAIN_ENTRY");
            valFirst.AddValue("name", "Leia");
            var valSecond = valSlot.AddNode("CHAIN_ENTRY");
            valSecond.AddValue("name", "Padme");

            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            var ownerRec = MakeSnapshotRecording("rec-owner", "Owner", new[] { "Jeb" },
                TerminalState.Recovered, 20);
            RecordingStore.AddRecordingWithTreeForTesting(ownerRec);

            var valRec = MakeSnapshotRecording("rec-val", "Val Ship", new[] { "Val" },
                TerminalState.Recovered, 20);
            RecordingStore.AddRecordingWithTreeForTesting(valRec);

            var historicalStandIn = MakeGhostOnlyRepairRecording(
                "rec-standin",
                "Stand-In Ship",
                "Kirrim",
                "Jeb",
                KerbalEndState.Recovered);
            RecordingStore.AddRecordingWithTreeForTesting(historicalStandIn);

            var actions = new List<GameAction>();
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-owner", 0.0, 20.0));
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-val", 0.0, 20.0));
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-standin", 0.0, 20.0));

            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();

            var fakeRoster = new FakeRoster();
            fakeRoster.Add("Hanley", ProtoCrewMember.RosterStatus.Available);
            fakeRoster.Add("Leia", ProtoCrewMember.RosterStatus.Available);
            fakeRoster.Add("Padme", ProtoCrewMember.RosterStatus.Available);

            KerbalLoadRepairDiagnostics.Begin();
            module.ApplyToRoster(fakeRoster);
            KerbalLoadRepairDiagnostics.EmitAndReset();

            Assert.True(fakeRoster.Contains("Kirrim"));
            Assert.True(fakeRoster.Contains("Leia"));
            Assert.False(fakeRoster.Contains("Padme"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:")
                && l.Contains("retiredRecreated=1")
                && l.Contains("deletedUnused=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("retired-recreated Kirrim"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("deleted-unused Padme"));
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

        private static Recording MakeSnapshotRecording(
            string recordingId, string vesselName, string[] crew, TerminalState terminal, double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);

            var endCrewSet = new HashSet<string>(crew);
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                VesselSnapshot = snapshot,
                TerminalStateValue = terminal,
                ExplicitStartUT = 0,
                ExplicitEndUT = endUT,
                CrewEndStates = new Dictionary<string, KerbalEndState>()
            };

            for (int i = 0; i < crew.Length; i++)
            {
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(
                    crew[i], terminal, endCrewSet);
            }

            return rec;
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

        private sealed class FakeRoster : KerbalsModule.IKerbalRosterFacade
        {
            private readonly Dictionary<string, ProtoCrewMember.RosterStatus> members
                = new Dictionary<string, ProtoCrewMember.RosterStatus>();
            private int generatedCounter;

            public void Add(string name, ProtoCrewMember.RosterStatus status)
            {
                members[name] = status;
            }

            public bool Contains(string name)
            {
                return members.ContainsKey(name);
            }

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                return members.TryGetValue(name, out status);
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                generatedCounter++;
                generatedName = "Generated-" + generatedCounter;
                members[generatedName] = ProtoCrewMember.RosterStatus.Available;
                return true;
            }

            public bool TryRecreateStandIn(string desiredName, string trait)
            {
                members[desiredName] = ProtoCrewMember.RosterStatus.Available;
                return true;
            }

            public bool TryRemove(string name)
            {
                return members.Remove(name);
            }
        }
    }
}
