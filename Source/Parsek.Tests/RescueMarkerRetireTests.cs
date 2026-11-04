using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RESCUE-PLACED-MARKER-NEVER-RETIRES: the saved #615 rescue marker retires when a
    /// flight recorded after the rescue holds the kerbal (a new reservation), and the save
    /// leaves out a marker whose vessel no longer exists. The guard must still fire on every
    /// walk that re-derives the rescue's own reservation.
    /// </summary>
    [Collection("Sequential")]
    public class RescueMarkerRetireTests : IDisposable
    {
        private const ulong RescuedVesselPid = 100UL;
        private const ulong GoneVesselPid = 300UL;
        private const double RescueUT = 200.0;
        private const string Jeb = "Jebediah Kerman";
        private const string Bill = "Bill Kerman";

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly KerbalsModule priorKerbalsModule;

        public RescueMarkerRetireTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorKerbalsModule = LedgerOrchestrator.Kerbals;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetKerbalsForTesting(priorKerbalsModule);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        // ---------- Fixture helpers ----------------------------------------

        private static void AddRecording(string id, double startUT, double endUT, params string[] crew)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Ship " + id,
                MergeState = MergeState.Immutable,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                CrewEndStates = new Dictionary<string, KerbalEndState>(),
            };
            var snap = new ConfigNode("VESSEL");
            var part = snap.AddNode("PART");
            foreach (string c in crew)
            {
                rec.CrewEndStates[c] = KerbalEndState.Aboard;
                part.AddValue("crew", c);
            }
            rec.GhostVisualSnapshot = snap;
            rec.VesselSnapshot = snap;
            RecordingStore.AddRecordingWithTreeForTesting(rec);
        }

        private static GameAction Assignment(string kerbal, string trait, string recordingId, double startUT)
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbal,
                KerbalRole = trait,
                StartUT = (float)startUT,
                EndUT = (float)(startUT + 100.0),
                KerbalEndStateField = KerbalEndState.Aboard,
                UT = startUT,
            };
        }

        /// <summary>
        /// A module whose slots already hold the depth-0 stand-ins (Erilan for Jeb, Debgas
        /// for Bill), as persisted across walks by LoadSlots.
        /// </summary>
        private static KerbalsModule BuildModule()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            AppendSlot(slotsNode, Jeb, "Pilot", "Erilan Kerman");
            AppendSlot(slotsNode, Bill, "Engineer", "Debgas Kerman");
            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);
            return module;
        }

        private static void AppendSlot(ConfigNode slotsNode, string owner, string trait, string standIn)
        {
            var slot = slotsNode.AddNode("SLOT");
            slot.AddValue("owner", owner);
            slot.AddValue("trait", trait);
            var entry = slot.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", standIn);
        }

        /// <summary>One recalculation walk: the reservations are rebuilt from the rows.</summary>
        private static void Walk(KerbalsModule module, List<GameAction> actions)
        {
            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();
        }

        /// <summary>
        /// The rescued state: the capsule flight (0..200) and an earlier flight of Jeb
        /// (rec-early, 50..150, also before the rescue) hold Jeb and Bill; the rescue at
        /// UT 200 placed both on the rescue vessel and the spawn removed their stand-ins.
        /// </summary>
        private (KerbalsModule module, List<GameAction> actions, RescueCompletionGuardTests.GuardFakeRoster roster)
            BuildRescuedState()
        {
            var module = BuildModule();
            AddRecording("rec-capsule", 0, 200, Jeb, Bill);
            AddRecording("rec-early", 50, 150, Jeb);
            var actions = new List<GameAction>
            {
                Assignment(Jeb, "Pilot", "rec-capsule", 0),
                Assignment(Bill, "Engineer", "rec-capsule", 0),
                Assignment(Jeb, "Pilot", "rec-early", 50),
            };
            CrewReservationManager.MarkRescuePlaced(Jeb, RescuedVesselPid, RescueUT);
            CrewReservationManager.MarkRescuePlaced(Bill, RescuedVesselPid, RescueUT);
            var roster = new RescueCompletionGuardTests.GuardFakeRoster();
            roster.MarkOnVessel(Jeb, RescuedVesselPid);
            roster.MarkOnVessel(Bill, RescuedVesselPid);
            return (module, actions, roster);
        }

        // ---------- ApplyToRoster: retire on a new reservation -------------

        // catches: a retirement keyed on anything a re-derivation changes (a walk rebuilds
        // the reservation dict from scratch every pass), which would retire the marker on
        // the second walk and bring back the #615 stand-in churn.
        [Fact]
        public void MultipleWalks_PreRescueRowsOnly_GuardFiresEveryWalk_MarkerKept()
        {
            var (module, actions, roster) = BuildRescuedState();

            for (int walk = 1; walk <= 3; walk++)
            {
                logLines.Clear();
                Walk(module, actions);
                module.ApplyToRoster(roster);

                Assert.False(roster.Contains("Erilan Kerman"), $"walk {walk}: Erilan must not be recreated");
                Assert.False(roster.Contains("Debgas Kerman"), $"walk {walk}: Debgas must not be recreated");
                Assert.True(CrewReservationManager.IsRescuePlaced(Jeb), $"walk {walk}: Jeb marker kept");
                Assert.True(CrewReservationManager.IsRescuePlaced(Bill), $"walk {walk}: Bill marker kept");
                Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                    && l.Contains("Rescue-completion guard summary")
                    && l.Contains("fired=2")
                    && l.Contains("retiredForNewReservation=0"));
                Assert.DoesNotContain(logLines, l => l.Contains("Retired rescue-placed marker"));
            }
        }

        // catches: the documented #615 limit outliving the rescue: a flight recorded after
        // the rescue (starting after its UT) holds Jeb while he is still aboard the
        // rescue vessel pid, and without the retirement the guard keeps suppressing that
        // new reservation's stand-in forever.
        [Fact]
        public void FlightRecordedAfterRescue_RetiresOnlyThatKerbalsMarker_StandInRecreated()
        {
            var (module, actions, roster) = BuildRescuedState();
            Walk(module, actions);
            module.ApplyToRoster(roster);
            Assert.True(CrewReservationManager.IsRescuePlaced(Jeb));

            // Jeb flies the rescue vessel again, recorded from UT 250 (after the rescue).
            AddRecording("rec-after", 250, 300, Jeb);
            actions.Add(Assignment(Jeb, "Pilot", "rec-after", 250));

            logLines.Clear();
            Walk(module, actions);
            module.ApplyToRoster(roster);

            Assert.False(CrewReservationManager.IsRescuePlaced(Jeb), "Jeb's marker retires");
            Assert.True(CrewReservationManager.IsRescuePlaced(Bill), "Bill has no new reservation");
            Assert.True(roster.Contains("Erilan Kerman"), "Jeb's stand-in is recreated");
            Assert.False(roster.Contains("Debgas Kerman"), "Bill stays guarded");
            Assert.Contains(logLines, l => l.Contains("[CrewReservation]")
                && l.Contains("Retired rescue-placed marker")
                && l.Contains(Jeb)
                && l.Contains("vesselPid=" + RescuedVesselPid)
                && l.Contains("markUT=200.0")
                && l.Contains("newestRowStartUT=250.0"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard summary")
                && l.Contains("fired=1")
                && l.Contains("retiredForNewReservation=1"));

            // The next walk: Jeb has no marker (his stand-in exists), Bill still guarded.
            logLines.Clear();
            Walk(module, actions);
            module.ApplyToRoster(roster);
            Assert.False(CrewReservationManager.IsRescuePlaced(Jeb));
            Assert.True(CrewReservationManager.IsRescuePlaced(Bill));
            Assert.False(roster.Contains("Debgas Kerman"));
            Assert.DoesNotContain(logLines, l => l.Contains("Retired rescue-placed marker"));
        }

        // catches: a marker with no rescue UT (a save written before the UT was recorded)
        // read as "every reservation is new" and retired on the first walk.
        [Fact]
        public void MarkerWithoutRescueUT_NeverRetiredByReservation()
        {
            var (module, actions, roster) = BuildRescuedState();
            CrewReservationManager.ResetReplacementsForTesting();
            CrewReservationManager.MarkRescuePlaced(Jeb, RescuedVesselPid);
            AddRecording("rec-after", 250, 300, Jeb);
            actions.Add(Assignment(Jeb, "Pilot", "rec-after", 250));

            Walk(module, actions);
            module.ApplyToRoster(roster);

            Assert.True(CrewReservationManager.IsRescuePlaced(Jeb));
            Assert.False(roster.Contains("Erilan Kerman"));
        }

        // ---------- Pure helpers -------------------------------------------

        [Theory]
        [InlineData(200.0, 250.0, true)]   // flight started after the rescue
        [InlineData(200.0, 200.0, false)]  // began in the rescue's own frame: not a later flight
        [InlineData(200.0, 199.9, false)]  // started before: the rescue's own reservation
        [InlineData(double.NaN, 250.0, false)]
        [InlineData(200.0, double.NaN, false)]
        [InlineData(double.PositiveInfinity, 250.0, false)]
        [InlineData(200.0, double.PositiveInfinity, false)]
        public void IsReservationNewerThanRescue_TruthTable(double markUT, double rowStartUT, bool expected)
        {
            Assert.Equal(expected, CrewReservationManager.IsReservationNewerThanRescue(markUT, rowStartUT));
        }

        [Theory]
        [InlineData(double.NaN, 10.0, 10.0)]
        [InlineData(10.0, 20.0, 20.0)]
        [InlineData(20.0, 10.0, 20.0)]
        [InlineData(10.0, double.NaN, 10.0)]
        [InlineData(10.0, double.PositiveInfinity, 10.0)]
        [InlineData(double.NaN, double.NaN, double.NaN)]
        public void LaterRowStartUT_KeepsTheLatestFiniteStart(double current, double candidate, double expected)
        {
            Assert.Equal(expected, KerbalsModule.LaterRowStartUT(current, candidate));
        }

        [Fact]
        public void MarkRescuePlaced_RemarkWithoutUT_KeepsTheRescueUT_RemarkWithUT_ReplacesIt()
        {
            CrewReservationManager.MarkRescuePlaced(Jeb, RescuedVesselPid, RescueUT);
            CrewReservationManager.MarkRescuePlaced(Jeb, RescuedVesselPid);
            Assert.True(CrewReservationManager.TryGetRescuePlacedMarker(Jeb, out var kept));
            Assert.Equal(RescueUT, kept.MarkUT);

            CrewReservationManager.MarkRescuePlaced(Jeb, GoneVesselPid, 400.0);
            Assert.True(CrewReservationManager.TryGetRescuePlacedMarker(Jeb, out var replaced));
            Assert.Equal(400.0, replaced.MarkUT);
            Assert.Equal(GoneVesselPid, replaced.VesselPid);
        }

        [Theory]
        [InlineData(GameScenes.FLIGHT, true)]
        [InlineData(GameScenes.SPACECENTER, true)]
        [InlineData(GameScenes.TRACKSTATION, true)]
        [InlineData(GameScenes.EDITOR, true)]
        [InlineData(GameScenes.MAINMENU, false)]
        [InlineData(GameScenes.LOADING, false)]
        public void SceneHasReliableVesselLists_TruthTable(GameScenes scene, bool expected)
        {
            Assert.Equal(expected, CrewReservationManager.SceneHasReliableVesselLists(scene));
        }

        [Fact]
        public void CollectSavableRescueMarkers_DropsOnlyMarkersWhoseVesselIsGone()
        {
            var markers = new Dictionary<string, CrewReservationManager.RescuePlacedMarker>
            {
                [Jeb] = new CrewReservationManager.RescuePlacedMarker(RescuedVesselPid, RescueUT),
                [Bill] = new CrewReservationManager.RescuePlacedMarker(GoneVesselPid, RescueUT),
            };

            var kept = CrewReservationManager.CollectSavableRescueMarkers(
                markers, pid => pid == RescuedVesselPid, out int pruned);
            Assert.Equal(1, pruned);
            Assert.Equal(new[] { Jeb }, kept.Select(k => k.Key).ToArray());

            // No reliable vessel source: keep everything.
            var all = CrewReservationManager.CollectSavableRescueMarkers(markers, null, out int prunedNone);
            Assert.Equal(0, prunedNone);
            Assert.Equal(2, all.Count);
        }

        // ---------- Save / load --------------------------------------------

        // catches: stale markers piling up in the save for good.
        [Fact]
        public void Save_LeavesOutMarkerWhoseVesselIsGone_MemoryKeepsIt()
        {
            CrewReservationManager.MarkRescuePlaced(Jeb, RescuedVesselPid, RescueUT);
            CrewReservationManager.MarkRescuePlaced(Bill, GoneVesselPid, RescueUT);
            CrewReservationManager.SaveVesselPidExistsOverrideForTesting = pid => pid == RescuedVesselPid;

            var node = new ConfigNode("PARSEK");
            CrewReservationManager.SaveCrewReplacements(node);

            ConfigNode[] entries = node.GetNode(CrewReservationManager.RescuePlacedNodeName).GetNodes("ENTRY");
            Assert.Single(entries);
            Assert.Equal(Jeb, entries[0].GetValue("name"));
            Assert.True(CrewReservationManager.IsRescuePlaced(Bill), "the save does not mutate memory");
            Assert.Contains(logLines, l => l.Contains("[CrewReservation]")
                && l.Contains("Saved 1 rescue-placed marker(s), left out 1"));
        }

        [Fact]
        public void Save_AllMarkersGone_WritesNoNode()
        {
            CrewReservationManager.MarkRescuePlaced(Bill, GoneVesselPid, RescueUT);
            CrewReservationManager.SaveVesselPidExistsOverrideForTesting = pid => false;

            var node = new ConfigNode("PARSEK");
            CrewReservationManager.SaveCrewReplacements(node);

            Assert.Null(node.GetNode(CrewReservationManager.RescuePlacedNodeName));
        }

        // catches: a prune in a scene with no reliable vessel list dropping a marker whose
        // vessel exists. Headless xUnit has no game scene, so the live resolver must skip.
        [Fact]
        public void Save_NoReliableVesselSource_KeepsEveryMarker_AndLogsTheSkip()
        {
            CrewReservationManager.MarkRescuePlaced(Jeb, RescuedVesselPid, RescueUT);
            CrewReservationManager.MarkRescuePlaced(Bill, GoneVesselPid, RescueUT);

            var node = new ConfigNode("PARSEK");
            CrewReservationManager.SaveCrewReplacements(node);

            Assert.Equal(2, node.GetNode(CrewReservationManager.RescuePlacedNodeName).GetNodes("ENTRY").Length);
            Assert.Contains(logLines, l => l.Contains("[CrewReservation]")
                && l.Contains("Rescue-placed marker prune skipped"));
        }

        [Fact]
        public void MarkUT_SurvivesSaveAndLoad()
        {
            CrewReservationManager.MarkRescuePlaced(Jeb, RescuedVesselPid, 1234.5678);
            var node = new ConfigNode("PARSEK");
            CrewReservationManager.SaveCrewReplacements(node);

            CrewReservationManager.ResetReplacementsForTesting();
            CrewReservationManager.LoadCrewReplacements(node);

            Assert.True(CrewReservationManager.TryGetRescuePlacedMarker(Jeb, out var marker));
            Assert.Equal(RescuedVesselPid, marker.VesselPid);
            Assert.Equal(1234.5678, marker.MarkUT);
        }

        [Fact]
        public void ScenarioWriterSave_WithAndWithoutMarkUT_Loads()
        {
            ConfigNode scenario = new Parsek.Tests.Generators.ScenarioWriter()
                .AddRescuePlacedKerbal(Jeb, RescuedVesselPid, RescueUT)
                .AddRescuePlacedKerbal(Bill, RescuedVesselPid)
                .BuildScenarioNode();

            CrewReservationManager.LoadCrewReplacements(scenario);

            Assert.True(CrewReservationManager.TryGetRescuePlacedMarker(Jeb, out var jeb));
            Assert.Equal(RescueUT, jeb.MarkUT);
            Assert.True(CrewReservationManager.TryGetRescuePlacedMarker(Bill, out var bill));
            Assert.True(double.IsNaN(bill.MarkUT), "an entry without markUT reads NaN");
        }
    }
}
