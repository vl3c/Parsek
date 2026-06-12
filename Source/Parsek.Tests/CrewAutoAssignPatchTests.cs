using System.Collections.Generic;
using System.Linq;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for CrewAutoAssignPatch.DecideSlotAction — the pure decision method
    /// that determines whether a crew slot should be kept, swapped with a stand-in,
    /// or cleared when the crew assignment dialog is refreshed.
    /// </summary>
    [Collection("Sequential")]
    public class CrewAutoAssignPatchTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewAutoAssignPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
        }

        private Recording MakeRecording(string vesselName, string[] crew,
            TerminalState terminal, double endUT)
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
            };

            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            var endCrewSet = new HashSet<string>(crew);
            for (int i = 0; i < crew.Length; i++)
            {
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(
                    crew[i], terminal, endCrewSet);
            }

            return rec;
        }

        [Fact]
        public void DecideSlotAction_NullOrEmptyName_ReturnsKeep()
        {
            var kerbals = new KerbalsModule();
            var replacements = new Dictionary<string, string>();

            var action1 = CrewAutoAssignPatch.DecideSlotAction(
                null, kerbals, replacements, out _);
            var action2 = CrewAutoAssignPatch.DecideSlotAction(
                "", kerbals, replacements, out _);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Keep, action1);
            Assert.Equal(CrewAutoAssignPatch.SlotAction.Keep, action2);
        }

        [Fact]
        public void DecideSlotAction_NullKerbalsModule_ReturnsKeep()
        {
            var replacements = new Dictionary<string, string>();

            var action = CrewAutoAssignPatch.DecideSlotAction(
                "Jeb", null, replacements, out _);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Keep, action);
        }

        [Fact]
        public void DecideSlotAction_UnreservedKerbal_ReturnsKeep()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            var replacements = new Dictionary<string, string> { { "Jeb", "StandIn1" } };

            // Val is not reserved
            var action = CrewAutoAssignPatch.DecideSlotAction(
                "Val", kerbals, replacements, out string standIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Keep, action);
            Assert.Null(standIn);
        }

        [Fact]
        public void DecideSlotAction_ReservedWithStandIn_ReturnsSwap()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            var replacements = new Dictionary<string, string> { { "Jeb", "StandIn1" } };

            var action = CrewAutoAssignPatch.DecideSlotAction(
                "Jeb", kerbals, replacements, out string standIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Swap, action);
            Assert.Equal("StandIn1", standIn);
        }

        [Fact]
        public void DecideSlotAction_ReservedNoStandIn_ReturnsClear()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            var replacements = new Dictionary<string, string>(); // no stand-in

            var action = CrewAutoAssignPatch.DecideSlotAction(
                "Jeb", kerbals, replacements, out string standIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Clear, action);
            Assert.Null(standIn);
        }

        [Fact]
        public void DecideSlotAction_ReservedNullReplacements_ReturnsClear()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            var action = CrewAutoAssignPatch.DecideSlotAction(
                "Jeb", kerbals, null, out string standIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Clear, action);
            Assert.Null(standIn);
        }

        [Fact]
        public void DecideSlotAction_MultipleCrew_CorrectlyClassifiesEach()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb", "Bill" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            // Only Jeb has a stand-in; Bill does not
            var replacements = new Dictionary<string, string> { { "Jeb", "StandIn1" } };

            var jebAction = CrewAutoAssignPatch.DecideSlotAction(
                "Jeb", kerbals, replacements, out string jebStandIn);
            var billAction = CrewAutoAssignPatch.DecideSlotAction(
                "Bill", kerbals, replacements, out string billStandIn);
            var valAction = CrewAutoAssignPatch.DecideSlotAction(
                "Val", kerbals, replacements, out string valStandIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Swap, jebAction);
            Assert.Equal("StandIn1", jebStandIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Clear, billAction);
            Assert.Null(billStandIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Keep, valAction);
            Assert.Null(valStandIn);
        }

        [Fact]
        public void DecideSlotAction_ReservedWithEmptyStandInName_ReturnsClear()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            // Stand-in name is empty string — treat as no stand-in
            var replacements = new Dictionary<string, string> { { "Jeb", "" } };

            var action = CrewAutoAssignPatch.DecideSlotAction(
                "Jeb", kerbals, replacements, out string standIn);

            Assert.Equal(CrewAutoAssignPatch.SlotAction.Clear, action);
            Assert.Null(standIn);
        }

        // ── DecideStandInPlacement — roster-state gate on a Swap-decided slot ──
        //
        // DecideSlotAction decides WHETHER a registered stand-in exists;
        // DecideStandInPlacement decides whether that stand-in can actually
        // take the seat. An Assigned stand-in is aboard a live vessel from an
        // earlier launch — placing them into a new manifest seats the same
        // kerbal on two vessels at once (the Barton Kerman duplication bug).

        [Fact]
        public void DecideStandInPlacement_AvailableNotInManifest_Places()
        {
            var placement = CrewAutoAssignPatch.DecideStandInPlacement(
                standInInRoster: true,
                standInStatus: ProtoCrewMember.RosterStatus.Available,
                standInAlreadyInManifest: false);

            Assert.Equal(CrewAutoAssignPatch.StandInPlacement.Place, placement);
        }

        [Fact]
        public void DecideStandInPlacement_AssignedStandIn_ClearsNotAvailable()
        {
            // The Barton case: stand-in already aboard another live vessel.
            var placement = CrewAutoAssignPatch.DecideStandInPlacement(
                standInInRoster: true,
                standInStatus: ProtoCrewMember.RosterStatus.Assigned,
                standInAlreadyInManifest: false);

            Assert.Equal(CrewAutoAssignPatch.StandInPlacement.ClearNotAvailable, placement);
        }

        [Theory]
        [InlineData(ProtoCrewMember.RosterStatus.Dead)]
        [InlineData(ProtoCrewMember.RosterStatus.Missing)]
        public void DecideStandInPlacement_DeadOrMissingStandIn_ClearsNotAvailable(
            ProtoCrewMember.RosterStatus status)
        {
            var placement = CrewAutoAssignPatch.DecideStandInPlacement(
                standInInRoster: true,
                standInStatus: status,
                standInAlreadyInManifest: false);

            Assert.Equal(CrewAutoAssignPatch.StandInPlacement.ClearNotAvailable, placement);
        }

        [Fact]
        public void DecideStandInPlacement_NotInRoster_ClearsNotInRoster()
        {
            var placement = CrewAutoAssignPatch.DecideStandInPlacement(
                standInInRoster: false,
                standInStatus: ProtoCrewMember.RosterStatus.Available,
                standInAlreadyInManifest: false);

            Assert.Equal(CrewAutoAssignPatch.StandInPlacement.ClearNotInRoster, placement);
        }

        [Fact]
        public void DecideStandInPlacement_AlreadyInManifest_ClearsAlreadyInManifest()
        {
            var placement = CrewAutoAssignPatch.DecideStandInPlacement(
                standInInRoster: true,
                standInStatus: ProtoCrewMember.RosterStatus.Available,
                standInAlreadyInManifest: true);

            Assert.Equal(CrewAutoAssignPatch.StandInPlacement.ClearAlreadyInManifest, placement);
        }

        [Fact]
        public void DecideStandInPlacement_AssignedWinsOverManifestPresence()
        {
            // Status is checked before manifest occupancy: an Assigned stand-in
            // reports ClearNotAvailable even if they also appear in the manifest.
            var placement = CrewAutoAssignPatch.DecideStandInPlacement(
                standInInRoster: true,
                standInStatus: ProtoCrewMember.RosterStatus.Assigned,
                standInAlreadyInManifest: true);

            Assert.Equal(CrewAutoAssignPatch.StandInPlacement.ClearNotAvailable, placement);
        }
    }
}
