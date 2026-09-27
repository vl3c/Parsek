using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens.Flight;
using KSP.UI.Screens.Flight.Dialogs;
using KSP.UI.TooltipTypes;
using Parsek.Patches;
using TMPro;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// In-flight EVA / crew transfer of a held kerbal (block audit row K2,
    /// StockUiFlightCrewDecoration.cs, Patches/FlightCrewReservationPatches.cs): every patched
    /// stock member resolves with the parameter names the patches bind, the refusal predicate
    /// (the crew dialog's, narrowed to kerbals a committed flight holds and to vessels that
    /// do not continue a committed flight), and the pairing - the greyed set is exactly the
    /// set the EVA and transfer backstops refuse, with the crew dialog's own text.
    /// </summary>
    [Collection("Sequential")]
    public class StockUiFlightCrewTests : IDisposable
    {
        private const uint LiveVesselPid = 424242;
        private const uint SpawnedVesselPid = 5555;

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> dialogTitles = new List<string>();
        private readonly List<string> dialogReasons = new List<string>();

        public StockUiFlightCrewTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            StockUiCrewDialogDecoration.ResetForTesting();
            StockUiFlightCrewDecoration.ResetForTesting();
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogTitles.Add(action);
                dialogReasons.Add(reason);
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
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            StockUiCrewDialogDecoration.ResetForTesting();
            StockUiFlightCrewDecoration.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------- target resolution: every patched stock member ----------------

        private static MethodBase Resolve(Type patch, Type declaring)
        {
            Assert.NotEmpty(patch.GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo helper = patch.GetMethod("ResolveTargetMethodForTesting",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(helper);
            var method = helper.Invoke(null, null) as MethodBase;
            Assert.NotNull(method);
            Assert.Equal(declaring, method.DeclaringType);
            return method;
        }

        private static string[] ParamNames(MethodBase m) => m.GetParameters().Select(p => p.Name).ToArray();

        [Fact]
        public void Target_KerbalPortraitUpdate_ResolvesThePrivateFrameUpdate()
        {
            var m = Resolve(typeof(KerbalPortraitEvaPatch), typeof(KerbalPortrait));
            Assert.Empty(m.GetParameters());
            Assert.True(m.IsPrivate);
        }

        [Fact]
        public void Target_CrewHatchDialogCreateList_ResolvesTheRowBuilder()
        {
            var m = Resolve(typeof(CrewHatchDialogCreateListPatch), typeof(CrewHatchDialog));
            Assert.Equal(new[] { typeof(List<ProtoCrewMember>) }, m.GetParameters().Select(p => p.ParameterType).ToArray());
        }

        [Fact]
        public void Target_FlightEvaSpawn_Resolves_WithTheBoundParameterNames()
        {
            var m = Resolve(typeof(FlightEvaSpawnBackstopPatch), typeof(FlightEVA));
            Assert.Equal(new[] { "pCrew", "fromPart", "fromAirlock", "tryAllHatches" }, ParamNames(m));
            Assert.Equal(typeof(KerbalEVA), ((MethodInfo)m).ReturnType);
            Assert.False(m.IsStatic);
        }

        [Fact]
        public void Target_CrewTransferCreate_Resolves_WithTheBoundParameterNames()
        {
            var m = Resolve(typeof(CrewTransferCreateBackstopPatch), typeof(CrewTransfer));
            Assert.Equal(new[] { "srcPart", "crewMember", "onDialogDismiss" }, ParamNames(m));
            Assert.Equal(typeof(CrewTransfer), ((MethodInfo)m).ReturnType);
            Assert.True(m.IsStatic);
        }

        [Fact]
        public void TheStockMembers_TheApplicatorsRead_Exist()
        {
            // Hatch dialog rows.
            var widgets = AccessTools.Field(typeof(CrewHatchDialog), "widgets");
            Assert.NotNull(widgets);
            Assert.Equal(typeof(List<CrewHatchDialogWidget>), widgets.FieldType);
            Assert.Equal("Button", AccessTools.Field(typeof(CrewHatchDialogWidget), "btnEVA").FieldType.Name);
            Assert.Equal("Button", AccessTools.Field(typeof(CrewHatchDialogWidget), "btnTransfer").FieldType.Name);
            Assert.Equal(typeof(TextMeshProUGUI), AccessTools.Field(typeof(CrewHatchDialogWidget), "textCrewName").FieldType);
            Assert.NotNull(typeof(CrewHatchDialogWidget).GetField("protoCrewMember"));
            Assert.NotNull(typeof(CrewHatchDialog).GetProperty("Part"));
            // Portrait.
            Assert.Equal("Button", typeof(KerbalPortrait).GetField("evaButton").FieldType.Name);
            Assert.Equal(typeof(TooltipController_Text), typeof(KerbalPortrait).GetField("evaTooltip").FieldType);
            Assert.NotNull(typeof(KerbalPortrait).GetField("hoverArea"));
            Assert.NotNull(typeof(KerbalPortrait).GetProperty("PortraitMode"));
            Assert.NotNull(typeof(KerbalPortrait).GetProperty("crewMember"));
            // The static FlightEVA.SpawnEVA(Kerbal) the portrait calls forwards to the patched
            // instance method (decompile), so the backstop covers both stock EVA controls.
            Assert.NotNull(typeof(FlightEVA).GetMethod("SpawnEVA", new[] { typeof(Kerbal) }));
        }

        // ---------------- the predicate (pure) ----------------

        [Theory]
        [InlineData(true, "ReservedActive", false, true)]
        [InlineData(true, "ReservedActive", true, false)]   // aboard a vessel continuing a committed flight
        [InlineData(true, "ReservedRetired", false, false)] // retired stand-in: his flights are over
        [InlineData(false, "ReservedActive", false, false)] // live Re-Fly crew carve-out lives in the dialog predicate
        [InlineData(false, "NotManaged", false, false)]
        public void IsMoveRefused_OnlyAHeldKerbalOffAContinuationVessel(
            bool refusedByDialog, string kindName, bool continues, bool expected)
        {
            var kind = (KerbalReservationKind)Enum.Parse(typeof(KerbalReservationKind), kindName);
            Assert.Equal(expected, StockUiFlightCrewDecoration.IsMoveRefused(refusedByDialog, kind, continues));
        }

        [Fact]
        public void Decide_Refused_IsBlockedAndMarked_OnTheFlightCrewScreen()
        {
            var context = new AstronautComplexContext { Reservation = n => null };
            var d = StockUiFlightCrewDecoration.Decide("Jeb", true, KerbalReservationKind.ReservedActive, false,
                null, context, null);
            Assert.True(d.Blocked);
            Assert.True(d.Marked);
            Assert.Equal(StockUiScreen.FlightCrew, d.Screen);
            Assert.Equal(StockUiFlightCrewDecoration.CrewHatchTab, d.Tab);
            Assert.Equal(StockUiDecorationKind.KerbalOnFlight, d.Kind);
            Assert.False(string.IsNullOrEmpty(d.Why));
            AssertHouseStyle(d.Why);
        }

        [Fact]
        public void Decide_RetiredOrContinuing_IsUndecorated()
        {
            var retired = StockUiFlightCrewDecoration.Decide("Debwig", true, KerbalReservationKind.ReservedRetired, false,
                null, new AstronautComplexContext(), null, StockUiFlightCrewDecoration.PortraitTab);
            Assert.False(retired.Blocked);
            Assert.False(retired.Marked);
            Assert.Null(retired.Why);
            Assert.Equal(StockUiFlightCrewDecoration.PortraitTab, retired.Tab);

            var continuing = StockUiFlightCrewDecoration.Decide("Jeb", true, KerbalReservationKind.ReservedActive, true,
                null, new AstronautComplexContext(), null);
            Assert.False(continuing.Blocked);
        }

        // ---------------- pure text builders ----------------

        [Fact]
        public void PortraitTooltip_ReasonAloneWhenStockAllows_ElseAfterStocksReason()
        {
            Assert.Equal("why", StockUiFlightCrewDecoration.PortraitTooltip(true, "EVA", "why"));
            Assert.Equal("why", StockUiFlightCrewDecoration.PortraitTooltip(false, "", "why"));
            Assert.Equal("Cannot EVA while time warping\n<color=#f97306>why</color>",
                StockUiFlightCrewDecoration.PortraitTooltip(false, "Cannot EVA while time warping", "why"));
            // Stock rewrites its text every frame; a text that already carries the reason is not doubled.
            string once = StockUiFlightCrewDecoration.PortraitTooltip(false, "Locked", "why");
            Assert.Equal(once, StockUiFlightCrewDecoration.PortraitTooltip(false, once, "why"));
        }

        [Fact]
        public void HatchRowLabel_AndBlockedTitle()
        {
            Assert.Equal("Jebediah Kerman <color=#f97306>(Reserved)</color>",
                StockUiFlightCrewDecoration.HatchRowLabel("Jebediah Kerman", "Reserved"));
            Assert.Equal("Jebediah Kerman", StockUiFlightCrewDecoration.HatchRowLabel("Jebediah Kerman", null));
            Assert.Equal("Cannot EVA \"Jeb\"", StockUiFlightCrewDecoration.BlockedTitle(FlightCrewMove.Eva, "Jeb"));
            Assert.Equal("Cannot transfer \"Jeb\"", StockUiFlightCrewDecoration.BlockedTitle(FlightCrewMove.Transfer, "Jeb"));
        }

        // ---------------- the live predicate over the ledger ----------------

        [Fact]
        public void VesselContinuesCommittedFlight_ReadsTheSpawnedPidOfTheEffectiveRecordingSet()
        {
            var rec = MakeRecording("Mun Lander", new[] { "Jeb" }, TerminalState.Landed, 1000);
            rec.VesselPersistentId = 1111;
            rec.SpawnedVesselPersistentId = SpawnedVesselPid;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.True(StockUiFlightCrewDecoration.VesselContinuesCommittedFlight(SpawnedVesselPid, null));
            Assert.False(StockUiFlightCrewDecoration.VesselContinuesCommittedFlight(LiveVesselPid, null));
            Assert.False(StockUiFlightCrewDecoration.VesselContinuesCommittedFlight(0, null));
        }

        /// <summary>
        /// Jeb flies a committed flight that ends landed (open-ended hold), Bill one recovered
        /// later (finite hold), Val dies on one (loss), StandIn1 is Jeb's active stand-in and
        /// Free Kerman is nobody's. Aboard a live vessel that continues no committed flight,
        /// the held three are refused EVA and transfer - exactly the crew dialog's refused set
        /// - with the crew dialog's own text; aboard a Parsek-spawned vessel nobody is.
        /// </summary>
        [Fact]
        public void Pairing_RefusedEvaAndTransfer_AreTheHeldKerbals_WithTheCrewDialogsText()
        {
            SeedHeldKerbals();

            var ids = new[] { "Jeb", "Bill", "Val", "StandIn1", "Free Kerman" };
            var decisions = ids.Select(n => StockUiFlightCrewDecoration.DescribeCurrent(n, LiveVesselPid, null)).ToList();
            var greyed = decisions.Where(d => d.Blocked && d.Marked).Select(d => d.Id).ToArray();
            var evaRefused = ids.Where(n => !StockUiFlightCrewDecoration.ShouldAllowMove(
                n, LiveVesselPid, null, FlightCrewMove.Eva, "test")).ToArray();
            var transferRefused = ids.Where(n => !StockUiFlightCrewDecoration.ShouldAllowMove(
                n, LiveVesselPid, null, FlightCrewMove.Transfer, "test")).ToArray();
            var dialogRefused = ids.Where(StockUiCrewDialogDecoration.IsAssignmentRefused).ToArray();

            var expected = new[] { "Jeb", "Bill", "Val" };
            Assert.Equal(expected, greyed);
            Assert.Equal(expected, evaRefused);
            Assert.Equal(expected, transferRefused);
            Assert.Equal(expected, dialogRefused);

            // The greyed control's reason == the refusal dialog's text == the crew dialog's text.
            var whys = decisions.Where(d => d.Blocked).Select(d => d.Why).ToArray();
            Assert.Equal(whys.Concat(whys).ToArray(), dialogReasons.ToArray());
            foreach (var name in expected)
                Assert.Equal(StockUiCrewDialogDecoration.DescribeCurrent(name).Why,
                    decisions.Single(d => d.Id == name).Why);
            Assert.Equal(new[] { "Cannot EVA \"Jeb\"", "Cannot EVA \"Bill\"", "Cannot EVA \"Val\"",
                "Cannot transfer \"Jeb\"", "Cannot transfer \"Bill\"", "Cannot transfer \"Val\"" }, dialogTitles.ToArray());
            Assert.Equal(StockUiDecorationKind.KerbalOnFlight, decisions.Single(d => d.Id == "Jeb").Kind);
            Assert.Equal(StockUiDecorationKind.KerbalLost, decisions.Single(d => d.Id == "Val").Kind);

            Assert.Contains(logLines, l => l.Contains("[CrewMove]") && l.Contains("Blocked EVA of 'Jeb' via test")
                && l.Contains("vesselPid=424242"));
            Assert.Contains(logLines, l => l.Contains("[CrewMove]") && l.Contains("Blocked crew transfer of 'Bill' via test"));
            Assert.DoesNotContain(logLines, l => l.Contains("of 'StandIn1'"));
            Assert.DoesNotContain(logLines, l => l.Contains("of 'Free Kerman'"));
        }

        [Fact]
        public void Pairing_AboardAVesselContinuingACommittedFlight_NobodyIsRefused()
        {
            SeedHeldKerbals();
            StockUiFlightCrewDecoration.VesselContinuesProviderForTesting = (pid, guid) => pid == SpawnedVesselPid;

            foreach (var name in new[] { "Jeb", "Bill", "Val" })
            {
                Assert.False(StockUiFlightCrewDecoration.DescribeCurrent(name, SpawnedVesselPid, null).Blocked);
                Assert.True(StockUiFlightCrewDecoration.ShouldAllowMove(name, SpawnedVesselPid, null, FlightCrewMove.Eva, "test"));
                Assert.True(StockUiFlightCrewDecoration.ShouldAllowMove(name, SpawnedVesselPid, null, FlightCrewMove.Transfer, "test"));
                // The same kerbal on any other live vessel is refused.
                Assert.True(StockUiFlightCrewDecoration.DescribeCurrent(name, LiveVesselPid, null).Blocked);
            }
            Assert.Empty(dialogReasons);
        }

        [Fact]
        public void Backstops_NoCrewOrNoLedger_Allow_WithNoDialog()
        {
            Assert.True(StockUiFlightCrewDecoration.ShouldAllowEva(null, null));
            Assert.True(StockUiFlightCrewDecoration.ShouldAllowTransfer(null, null));
            Assert.True(StockUiFlightCrewDecoration.ShouldAllowMove(null, LiveVesselPid, null, FlightCrewMove.Eva, "test"));
            Assert.True(StockUiFlightCrewDecoration.ShouldAllowMove("Jeb", LiveVesselPid, null, FlightCrewMove.Eva, "test")); // no KerbalsModule
            Assert.Empty(dialogReasons);
        }

        [Fact]
        public void AReleasedHold_IsNeitherGreyedNorRefused()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Hopper", new[] { "Jeb" }, TerminalState.Recovered, 2000));
            LedgerOrchestrator.SetKerbalsForTesting(KerbalsTestHelper.RecalculateFromStore());
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            Assert.True(StockUiFlightCrewDecoration.DescribeCurrent("Jeb", LiveVesselPid, null).Blocked);

            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            LedgerOrchestrator.SetKerbalsForTesting(KerbalsTestHelper.RecalculateFromStore());
            CommittedFutureIndexCache.ResetForTesting();

            Assert.False(StockUiFlightCrewDecoration.DescribeCurrent("Jeb", LiveVesselPid, null).Blocked);
            Assert.True(StockUiFlightCrewDecoration.ShouldAllowMove("Jeb", LiveVesselPid, null, FlightCrewMove.Eva, "test"));
            Assert.Empty(dialogReasons);
        }

        // ---------------- helpers ----------------

        private void SeedHeldKerbals()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Mun Lander", new[] { "Jeb" }, TerminalState.Landed, 1000));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Hopper", new[] { "Bill" }, TerminalState.Recovered, 2000));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Doomed", new[] { "Val" }, TerminalState.Destroyed, 900));
            var kerbals = new KerbalsModule();
            var slotsNode = new ConfigNode("KERBAL_SLOTS");
            var slot = slotsNode.AddNode("SLOT");
            slot.AddValue("owner", "Jeb");
            slot.AddValue("trait", "Pilot");
            slot.AddNode("CHAIN_ENTRY").AddValue("name", "StandIn1");
            var parent = new ConfigNode();
            parent.AddNode(slotsNode);
            kerbals.LoadSlots(parent);
            kerbals = KerbalsTestHelper.RecalculateModule(kerbals);
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
        }

        private static void AssertHouseStyle(string text)
        {
            Assert.False(string.IsNullOrEmpty(text));
            Assert.True(text.All(c => c < 128), "non-ASCII in: " + text);
        }

        private static Recording MakeRecording(string vesselName, string[] crew, TerminalState terminal, double endUT)
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
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(crew[i], terminal, endCrewSet);
            return rec;
        }
    }
}
