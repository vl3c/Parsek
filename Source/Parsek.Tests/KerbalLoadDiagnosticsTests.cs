using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
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
        public void OnKspLoad_WithMixedTouristRemapAndEndStateRewrite_LogsSingleSummary()
        {
            var scenarioNode = new ConfigNode("SCENARIO");
            var replacementsNode = scenarioNode.AddNode("CREW_REPLACEMENTS");
            var replacementEntry = replacementsNode.AddNode("ENTRY");
            replacementEntry.AddValue("original", "Jebediah Kerman");
            replacementEntry.AddValue("replacement", "Hanley Kerman");

            var mixedRepair = MakeGhostRepairRecording(
                "rec-mixed",
                "Crew Ship",
                new[] { "Tourist Kerman", "Hanley Kerman" },
                new Dictionary<string, KerbalEndState>
                {
                    { "Tourist Kerman", KerbalEndState.Recovered },
                    { "Jebediah Kerman", KerbalEndState.Recovered }
                });
            RecordingStore.AddRecordingWithTreeForTesting(mixedRepair);

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
                RecordingId = "rec-mixed",
                KerbalName = "Tourist Kerman",
                KerbalRole = "Tourist",
                StartUT = 10,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Recovered,
                Sequence = 1
            });
            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-mixed",
                KerbalName = "Hanley Kerman",
                KerbalRole = "Pilot",
                StartUT = 10,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Unknown,
                Sequence = 2
            });

            KerbalLoadRepairDiagnostics.Begin();
            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);
            LedgerOrchestrator.OnKspLoad(CollectRecordingIds(), 100.0);

            string summaryLine = Assert.Single(logLines.FindAll(l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:")));
            Assert.Contains("slotSource=legacy-creplacements", summaryLine);
            Assert.Contains("oldRows=2", summaryLine);
            Assert.Contains("newRows=1", summaryLine);
            Assert.Contains("remappedRows=1", summaryLine);
            Assert.Contains("endStateRewrites=1", summaryLine);
            Assert.Contains("touristRowsSkipped=1", summaryLine);

            string samplesLine = Assert.Single(logLines.FindAll(l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair samples:")));
            Assert.Contains("tourist-skip rec-mixed count=1", samplesLine);
            Assert.Contains("remap rec-mixed Hanley Kerman->Jebediah Kerman", samplesLine);
            Assert.Contains("end-state rec-mixed Jebediah Kerman Unknown->Recovered", samplesLine);
        }

        [Fact]
        public void OnKspLoad_WithSequenceOnlyRepair_DoesNotLogFalseRemapSamples()
        {
            var scenarioNode = new ConfigNode("SCENARIO");
            var slotsNode = scenarioNode.AddNode("KERBAL_SLOTS");

            var jebSlot = slotsNode.AddNode("SLOT");
            jebSlot.AddValue("owner", "Jebediah Kerman");
            jebSlot.AddValue("trait", "Pilot");
            var jebChain = jebSlot.AddNode("CHAIN_ENTRY");
            jebChain.AddValue("name", "Hanley Kerman");

            var billSlot = slotsNode.AddNode("SLOT");
            billSlot.AddValue("owner", "Bill Kerman");
            billSlot.AddValue("trait", "Engineer");
            var billChain = billSlot.AddNode("CHAIN_ENTRY");
            billChain.AddValue("name", "Kirrim Kerman");

            var reorderRecording = MakeSnapshotRecording(
                "rec-reorder",
                "Two Crew Ship",
                new[] { "Jebediah Kerman", "Bill Kerman" },
                TerminalState.Recovered,
                20);
            RecordingStore.AddRecordingWithTreeForTesting(reorderRecording);

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-reorder",
                KerbalName = "Bill Kerman",
                KerbalRole = "Pilot",
                StartUT = 0,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Recovered,
                Sequence = 2
            });
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-reorder",
                KerbalName = "Jebediah Kerman",
                KerbalRole = "Pilot",
                StartUT = 0,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Recovered,
                Sequence = 1
            });

            KerbalLoadRepairDiagnostics.Begin();
            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);
            LedgerOrchestrator.OnKspLoad(CollectRecordingIds(), 100.0);

            string summaryLine = Assert.Single(logLines.FindAll(l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:")));
            Assert.Contains("slotSource=KERBAL_SLOTS", summaryLine);
            Assert.Contains("oldRows=2", summaryLine);
            Assert.Contains("newRows=2", summaryLine);
            Assert.Contains("remappedRows=0", summaryLine);
            Assert.Contains("endStateRewrites=0", summaryLine);
            Assert.Contains("touristRowsSkipped=0", summaryLine);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair samples:"));
        }

        [Fact]
        public void OnKspLoad_WithNestedReservations_LogsChainExtensionSample()
        {
            var scenarioNode = new ConfigNode("SCENARIO");
            var slotsNode = scenarioNode.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jebediah Kerman");
            slotNode.AddValue("trait", "Pilot");
            var chainEntry = slotNode.AddNode("CHAIN_ENTRY");
            chainEntry.AddValue("name", "Hanley Kerman");

            var nestedSlotNode = slotsNode.AddNode("SLOT");
            nestedSlotNode.AddValue("owner", "Hanley Kerman");
            nestedSlotNode.AddValue("trait", "Pilot");
            var nestedChainEntry = nestedSlotNode.AddNode("CHAIN_ENTRY");
            nestedChainEntry.AddValue("name", "Leia Kerman");

            var ownerRec = MakeSnapshotRecording(
                "rec-owner",
                "Owner Ship",
                new[] { "Jebediah Kerman" },
                TerminalState.Recovered,
                20);
            RecordingStore.AddRecordingWithTreeForTesting(ownerRec);

            var standInRec = MakeSnapshotRecording(
                "rec-standin",
                "Stand-In Ship",
                new[] { "Hanley Kerman" },
                TerminalState.Recovered,
                20);
            RecordingStore.AddRecordingWithTreeForTesting(standInRec);

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-owner",
                KerbalName = "Jebediah Kerman",
                KerbalRole = "Pilot",
                StartUT = 0,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Recovered,
                Sequence = 1
            });
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-standin",
                KerbalName = "Hanley Kerman",
                KerbalRole = "Pilot",
                StartUT = 0,
                EndUT = 20,
                KerbalEndStateField = KerbalEndState.Recovered,
                Sequence = 1
            });

            KerbalLoadRepairDiagnostics.Begin();
            InvokeLoadCrewAndGroupState(scenarioNode, initialLoadDoneValue: false);
            LedgerOrchestrator.RecalculateAndPatch();
            KerbalLoadRepairDiagnostics.EmitAndReset();

            string summaryLine = Assert.Single(logLines.FindAll(l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:")));
            Assert.Contains("slotSource=KERBAL_SLOTS", summaryLine);
            Assert.Contains("chainExtensions=1", summaryLine);

            string samplesLine = Assert.Single(logLines.FindAll(l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair samples:")));
            Assert.Contains("chain Jebediah Kerman depth=1", samplesLine);
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

        [Fact]
        public void ApplyToRoster_FacadePath_WithFailedHistoricalRecreate_DoesNotLogFalseKeep()
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

            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            var ownerRec = MakeSnapshotRecording("rec-owner", "Owner", new[] { "Jeb" },
                TerminalState.Recovered, 20);
            RecordingStore.AddRecordingWithTreeForTesting(ownerRec);

            var historicalStandIn = MakeGhostOnlyRepairRecording(
                "rec-standin",
                "Stand-In Ship",
                "Kirrim",
                "Jeb",
                KerbalEndState.Recovered);
            RecordingStore.AddRecordingWithTreeForTesting(historicalStandIn);

            var actions = new List<GameAction>();
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-owner", 0.0, 20.0));
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-standin", 0.0, 20.0));

            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();

            var fakeRoster = new FakeRoster(allowRecreateStandIns: false);
            fakeRoster.Add("Hanley", ProtoCrewMember.RosterStatus.Assigned);

            logLines.Clear();
            KerbalLoadRepairDiagnostics.Begin();
            module.ApplyToRoster(fakeRoster);
            KerbalLoadRepairDiagnostics.EmitAndReset();

            Assert.False(fakeRoster.Contains("Kirrim"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:"));
        }

        [Fact]
        public void ApplyToRoster_WrapperPath_WithRetiredStandInOnly_DoesNotLogRepairSummary()
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

            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            var ownerRec = MakeSnapshotRecording("rec-owner", "Owner", new[] { "Jeb" },
                TerminalState.Recovered, 20);
            RecordingStore.AddRecordingWithTreeForTesting(ownerRec);

            var historicalStandIn = MakeGhostOnlyRepairRecording(
                "rec-standin",
                "Stand-In Ship",
                "Kirrim",
                "Jeb",
                KerbalEndState.Recovered);
            RecordingStore.AddRecordingWithTreeForTesting(historicalStandIn);

            var actions = new List<GameAction>();
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-owner", 0.0, 20.0));
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-standin", 0.0, 20.0));

            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();

            var roster = CreateRealRoster();
            AddToRealRoster(roster, "Hanley", ProtoCrewMember.RosterStatus.Assigned, "Pilot");
            AddToRealRoster(roster, "Kirrim", ProtoCrewMember.RosterStatus.Available, "Pilot");

            logLines.Clear();
            KerbalLoadRepairDiagnostics.Begin();
            module.ApplyToRoster(roster);
            KerbalLoadRepairDiagnostics.EmitAndReset();

            Assert.True(RosterContains(roster, "Kirrim"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:"));
        }

        [Fact]
        public void ApplyToRoster_WrapperPath_DeletesUnusedDisplacedStandIn()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");

            var slotsNode = parent.AddNode("KERBAL_SLOTS");

            // Val's slot: chain ["Hanley", "Padme"]
            var valSlot = slotsNode.AddNode("SLOT");
            valSlot.AddValue("owner", "Val");
            valSlot.AddValue("trait", "Pilot");
            var valFirst = valSlot.AddNode("CHAIN_ENTRY");
            valFirst.AddValue("name", "Hanley");
            var valSecond = valSlot.AddNode("CHAIN_ENTRY");
            valSecond.AddValue("name", "Padme");

            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            // rec-val: uses only Val (owner is active occupant; stand-ins are displaced)
            var valRec = MakeSnapshotRecording("rec-val", "Val Ship", new[] { "Val" },
                TerminalState.Recovered, 20);
            RecordingStore.AddRecordingWithTreeForTesting(valRec);

            // rec-hanley: uses Hanley (so Hanley is displaced BUT used in a recording = retired)
            var hanleyRec = MakeGhostOnlyRepairRecording(
                "rec-hanley",
                "Hanley Ship",
                "Hanley",
                "Val",
                KerbalEndState.Recovered);
            RecordingStore.AddRecordingWithTreeForTesting(hanleyRec);

            // No recording uses Padme — she is displaced AND unused = should be deleted

            var actions = new List<GameAction>();
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-val", 0.0, 20.0));
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-hanley", 0.0, 20.0));

            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();

            // Build real KerbalRoster via reflection
            var roster = CreateRealRoster();
            AddToRealRoster(roster, "Val", ProtoCrewMember.RosterStatus.Assigned, "Pilot");
            AddToRealRoster(roster, "Hanley", ProtoCrewMember.RosterStatus.Available, "Pilot");
            AddToRealRoster(roster, "Padme", ProtoCrewMember.RosterStatus.Available, "Pilot");

            logLines.Clear();
            KerbalLoadRepairDiagnostics.Begin();
            module.ApplyToRoster(roster);
            KerbalLoadRepairDiagnostics.EmitAndReset();

            // Padme: displaced + unused — should be removed from real roster
            Assert.False(RosterContains(roster, "Padme"), "Padme should be deleted (displaced, unused)");
            // Hanley: displaced + used in recording — retired, kept in roster
            Assert.True(RosterContains(roster, "Hanley"), "Hanley should survive (retired, used in recording)");
            // Val: owner, always preserved
            Assert.True(RosterContains(roster, "Val"), "Val should survive (slot owner)");

            // Assert log contains deletedUnused count
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalLoad]")
                && l.Contains("repair summary:")
                && l.Contains("deletedUnused=1"));
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
            return MakeGhostRepairRecording(
                recordingId,
                vesselName,
                new[] { rawCrewName },
                new Dictionary<string, KerbalEndState>
                {
                    { logicalCrewName, endState }
                });
        }

        private static Recording MakeGhostRepairRecording(
            string recordingId,
            string vesselName,
            string[] rawCrewNames,
            Dictionary<string, KerbalEndState> crewEndStates)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < rawCrewNames.Length; i++)
                part.AddValue("crew", rawCrewNames[i]);

            return new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 10,
                ExplicitEndUT = 20,
                CrewEndStates = crewEndStates
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

        private static KerbalRoster CreateRealRoster()
        {
            var roster = (KerbalRoster)FormatterServices.GetUninitializedObject(typeof(KerbalRoster));
            var kerbalsField = typeof(KerbalRoster).GetField(
                "kerbals", BindingFlags.Instance | BindingFlags.NonPublic);
            var modeField = typeof(KerbalRoster).GetField(
                "mode", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(kerbalsField);
            Assert.NotNull(modeField);

            kerbalsField.SetValue(roster, Activator.CreateInstance(kerbalsField.FieldType));
            modeField.SetValue(roster, Game.Modes.CAREER);
            return roster;
        }

        private static void AddToRealRoster(
            KerbalRoster roster, string name, ProtoCrewMember.RosterStatus status, string trait)
        {
            var kerbalsField = typeof(KerbalRoster).GetField(
                "kerbals", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(kerbalsField);

            object kerbals = kerbalsField.GetValue(roster);
            Assert.NotNull(kerbals);

            var addMethod = kerbals.GetType().GetMethod("Add");
            Assert.NotNull(addMethod);
            addMethod.Invoke(kerbals, new object[]
            {
                name,
                CreateCrewMember(name, status, trait)
            });
        }

        private static ProtoCrewMember CreateCrewMember(
            string name, ProtoCrewMember.RosterStatus status, string trait)
        {
            var crew = (ProtoCrewMember)FormatterServices.GetUninitializedObject(
                typeof(ProtoCrewMember));
            var crewType = typeof(ProtoCrewMember);

            var nameField = crewType.GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic);
            var statusField = crewType.GetField(
                "_rosterStatus", BindingFlags.Instance | BindingFlags.NonPublic);
            var traitField = crewType.GetField(
                "trait", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.NotNull(nameField);
            Assert.NotNull(statusField);
            Assert.NotNull(traitField);

            nameField.SetValue(crew, name);
            statusField.SetValue(crew, status);
            traitField.SetValue(crew, trait);
            return crew;
        }

        private static bool RosterContains(KerbalRoster roster, string name)
        {
            foreach (ProtoCrewMember crew in roster.Crew)
            {
                if (crew.name == name)
                    return true;
            }

            return false;
        }

        private sealed class FakeRoster : KerbalsModule.IKerbalRosterFacade
        {
            private readonly bool allowGeneratedStandIns;
            private readonly bool allowRecreateStandIns;
            private readonly Dictionary<string, ProtoCrewMember.RosterStatus> members
                = new Dictionary<string, ProtoCrewMember.RosterStatus>();
            private readonly HashSet<string> liveVesselCrew
                = new HashSet<string>(System.StringComparer.Ordinal);
            private int generatedCounter;

            public FakeRoster(bool allowGeneratedStandIns = true, bool allowRecreateStandIns = true)
            {
                this.allowGeneratedStandIns = allowGeneratedStandIns;
                this.allowRecreateStandIns = allowRecreateStandIns;
            }

            public void Add(string name, ProtoCrewMember.RosterStatus status)
            {
                members[name] = status;
            }

            public bool Contains(string name)
            {
                return members.ContainsKey(name);
            }

            /// <summary>
            /// Mark <paramref name="name"/> as currently seated on a loaded
            /// non-ghost vessel — equivalent to the rescue path having placed
            /// the original kerbal back into the spawned vessel.
            /// </summary>
            public void MarkOnLiveVessel(string name)
            {
                if (!string.IsNullOrEmpty(name)) liveVesselCrew.Add(name);
            }

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                return members.TryGetValue(name, out status);
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                if (!allowGeneratedStandIns)
                {
                    generatedName = null;
                    return false;
                }

                generatedCounter++;
                generatedName = "Generated-" + generatedCounter;
                members[generatedName] = ProtoCrewMember.RosterStatus.Available;
                return true;
            }

            public bool TryRecreateStandIn(string desiredName, string trait)
            {
                if (!allowRecreateStandIns)
                    return false;

                members[desiredName] = ProtoCrewMember.RosterStatus.Available;
                return true;
            }

            public bool TryRemove(string name)
            {
                return members.Remove(name);
            }

            public bool IsKerbalOnLiveVessel(string kerbalName)
            {
                return !string.IsNullOrEmpty(kerbalName) && liveVesselCrew.Contains(kerbalName);
            }

            // #615 P1 review (fourth pass): pid-scoped predicate. The
            // diagnostics fixture does not exercise the rescue-completion
            // guard path so a no-match implementation suffices; the
            // dedicated RescueCompletionGuardTests.GuardFakeRoster carries
            // the pid-scoped fixture used by the guard regressions.
            public bool IsKerbalOnVesselWithPid(string kerbalName, ulong vesselPersistentId)
            {
                return false;
            }
        }
    }
}
