using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C2 coverage for the PlantFlag pure surfaces (<see cref="TestCommandPlantFlag"/>).
    /// DecidePlantGateWait (F1): a transiently-closed gate is NEVER a terminal reject (the
    /// mid-fall / EVA-1 near-deterministic regression); a stably-closed AC lock rejects; the
    /// budget expiry with the gate never open times out; an open gate proceeds. Fails if a
    /// transiently closed gate terminally rejects or a stably locked plant waits the whole
    /// budget pointlessly. DecideFlagPlantCompletion: dialogAnswered=false is never CompleteOk
    /// even with the flag vessel present (the false-OK-over-unanswered-dialog guard).
    /// </summary>
    public class TestCommandPlantFlagTests
    {
        private const double Budget = 180.0;

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- DecidePlantGateWait -----

        [Fact]
        public void Gate_ClosedNotStablyLocked_KeepWaiting()
        {
            // The gate is transiently closed (mid-fall / stumble / ragdoll recovery): WAIT,
            // never a terminal reject.
            Assert.Equal(PlantGateDecision.KeepWaiting,
                TestCommandPlantFlag.DecidePlantGateWait(5.0, gateOpen: false, stableLockClosed: false, Budget));
        }

        [Fact]
        public void Gate_Open_ProceedToPlant()
        {
            Assert.Equal(PlantGateDecision.ProceedToPlant,
                TestCommandPlantFlag.DecidePlantGateWait(5.0, gateOpen: true, stableLockClosed: false, Budget));
        }

        [Fact]
        public void Gate_StablyLocked_RejectStableLock()
        {
            Assert.Equal(PlantGateDecision.RejectStableLock,
                TestCommandPlantFlag.DecidePlantGateWait(5.0, gateOpen: false, stableLockClosed: true, Budget));
        }

        [Fact]
        public void Gate_OpenBeatsStableLock()
        {
            // An open gate proceeds even if the (contradictory) stable-lock bit were set: the
            // positive transition wins so the plant is never spuriously refused.
            Assert.Equal(PlantGateDecision.ProceedToPlant,
                TestCommandPlantFlag.DecidePlantGateWait(5.0, gateOpen: true, stableLockClosed: true, Budget));
        }

        [Fact]
        public void Gate_BudgetExpiredNeverOpen_GateTimeout()
        {
            Assert.Equal(PlantGateDecision.GateTimeout,
                TestCommandPlantFlag.DecidePlantGateWait(Budget, gateOpen: false, stableLockClosed: false, Budget));
        }

        [Fact]
        public void Gate_StableLockBeatsTimeout()
        {
            // A stably-closed lock is the more actionable signal past budget (an instant refuse,
            // not a pointless full-budget wait).
            Assert.Equal(PlantGateDecision.RejectStableLock,
                TestCommandPlantFlag.DecidePlantGateWait(Budget + 10.0, gateOpen: false, stableLockClosed: true, Budget));
        }

        // ----- DecideFlagPlantCompletion -----

        [Fact]
        public void FlagComplete_AllTrue_Ok()
        {
            Assert.Equal(FlagPlantCompletionDecision.CompleteOk,
                TestCommandPlantFlag.DecideFlagPlantCompletion(
                    5.0, flagSiteVesselExists: true, dialogAnswered: true, sceneSettled: true, Budget));
        }

        [Fact]
        public void FlagComplete_DialogNotAnswered_NeverOk_EvenWithFlagVessel()
        {
            // The false-OK-over-unanswered-dialog guard: a flag vessel exists but the dialog is
            // not answered -> Parsek never captured the FlagEvent, so NOT complete.
            Assert.Equal(FlagPlantCompletionDecision.StillWaiting,
                TestCommandPlantFlag.DecideFlagPlantCompletion(
                    5.0, flagSiteVesselExists: true, dialogAnswered: false, sceneSettled: true, Budget));
        }

        [Fact]
        public void FlagComplete_NoFlagVessel_StillWaiting()
        {
            Assert.Equal(FlagPlantCompletionDecision.StillWaiting,
                TestCommandPlantFlag.DecideFlagPlantCompletion(
                    5.0, flagSiteVesselExists: false, dialogAnswered: true, sceneSettled: true, Budget));
        }

        [Fact]
        public void FlagComplete_NotSettled_StillWaiting()
        {
            Assert.Equal(FlagPlantCompletionDecision.StillWaiting,
                TestCommandPlantFlag.DecideFlagPlantCompletion(
                    5.0, flagSiteVesselExists: true, dialogAnswered: true, sceneSettled: false, Budget));
        }

        [Fact]
        public void FlagComplete_BudgetExpired_FlagTimeout()
        {
            Assert.Equal(FlagPlantCompletionDecision.FlagTimeout,
                TestCommandPlantFlag.DecideFlagPlantCompletion(
                    Budget, flagSiteVesselExists: false, dialogAnswered: false, sceneSettled: true, Budget));
        }

        [Fact]
        public void FlagComplete_PositiveBeatsBudget()
        {
            Assert.Equal(FlagPlantCompletionDecision.CompleteOk,
                TestCommandPlantFlag.DecideFlagPlantCompletion(
                    Budget + 10.0, flagSiteVesselExists: true, dialogAnswered: true, sceneSettled: true, Budget));
        }

        // ----- IsPlantGateOpen (live-gate defect, EVA-1 pad-flag 2026-07-24) -----

        [Fact]
        public void LiveGate_CanPlantAndPlantableState_Open()
        {
            // The pad-flag fix: the gate opens on the LIVE CanPlantFlag() while the kerbal is
            // in a plantable fsm state - NOT on the stale Events["PlantFlag"].active cache.
            Assert.True(TestCommandPlantFlag.IsPlantGateOpen(canPlantFlag: true, inPlantableFsmState: true));
        }

        [Fact]
        public void LiveGate_CanPlantButWrongState_Closed()
        {
            // CanPlantFlag() can be true (ground contact + flag items) while the kerbal is
            // mid-walk / mid-fall where On_flagPlantStart is not registered; firing PlantFlag()
            // there would decrement flagItems without planting, so the gate stays closed.
            Assert.False(TestCommandPlantFlag.IsPlantGateOpen(canPlantFlag: true, inPlantableFsmState: false));
        }

        [Fact]
        public void LiveGate_PlantableStateButCannotPlant_Closed()
        {
            Assert.False(TestCommandPlantFlag.IsPlantGateOpen(canPlantFlag: false, inPlantableFsmState: true));
        }

        [Fact]
        public void LiveGate_NeitherConjunct_Closed()
        {
            Assert.False(TestCommandPlantFlag.IsPlantGateOpen(canPlantFlag: false, inPlantableFsmState: false));
        }

        // ----- DescribePlantGateBlock (self-explaining timeout diagnostic) -----

        [Fact]
        public void GateDiag_AllMet_Open()
        {
            Assert.Equal("open", TestCommandPlantFlag.DescribePlantGateBlock(
                inPlantableFsmState: true, vesselActive: true, groundContact: true,
                flagItemsPositive: true, notRagdoll: true, flagUnlocked: true,
                notConstruction: true, fsmStateName: "Idle_Grounded"));
        }

        [Fact]
        public void GateDiag_NoGroundContact_NamesIt()
        {
            // The EVA-1 pad-flag signature: standing on the pad, ground contact still
            // registering -> the timeout must SAY so, not just gateOpen=false.
            Assert.Equal("no-ground-contact", TestCommandPlantFlag.DescribePlantGateBlock(
                inPlantableFsmState: true, vesselActive: true, groundContact: false,
                flagItemsPositive: true, notRagdoll: true, flagUnlocked: true,
                notConstruction: true, fsmStateName: "Idle_Grounded"));
        }

        [Fact]
        public void GateDiag_WrongState_NamesFsmState()
        {
            var diag = TestCommandPlantFlag.DescribePlantGateBlock(
                inPlantableFsmState: false, vesselActive: true, groundContact: true,
                flagItemsPositive: true, notRagdoll: true, flagUnlocked: true,
                notConstruction: true, fsmStateName: "Landing");
            Assert.Equal("fsm=Landing", diag);
        }

        [Fact]
        public void GateDiag_MultipleClosed_CommaJoined()
        {
            var diag = TestCommandPlantFlag.DescribePlantGateBlock(
                inPlantableFsmState: false, vesselActive: true, groundContact: false,
                flagItemsPositive: false, notRagdoll: false, flagUnlocked: true,
                notConstruction: true, fsmStateName: "Ragdoll");
            Assert.Equal("fsm=Ragdoll,no-ground-contact,no-flag-items,ragdoll", diag);
        }

        [Fact]
        public void GateDiag_EmptyStateName_Placeholder()
        {
            var diag = TestCommandPlantFlag.DescribePlantGateBlock(
                inPlantableFsmState: false, vesselActive: true, groundContact: true,
                flagItemsPositive: true, notRagdoll: true, flagUnlocked: true,
                notConstruction: true, fsmStateName: null);
            Assert.Equal("fsm=?", diag);
        }

        [Fact]
        public void GateDiag_AcLocked_NamesIt()
        {
            Assert.Equal("ac-flag-locked", TestCommandPlantFlag.DescribePlantGateBlock(
                inPlantableFsmState: true, vesselActive: true, groundContact: true,
                flagItemsPositive: true, notRagdoll: true, flagUnlocked: false,
                notConstruction: true, fsmStateName: "Idle_Grounded"));
        }

        // ----- BuildCompletePayload -----

        [Fact]
        public void Payload_CarriesSiteBodyLatLon()
        {
            var p = TestCommandPlantFlag.BuildCompletePayload("Flag", "Kerbin", -0.0972, 285.373);
            Assert.Equal("Flag", Val(p, "flagSite"));
            Assert.Equal("Kerbin", Val(p, "body"));
            Assert.Equal(new[] { "flagSite", "body", "lat", "lon" }, p.Select(kv => kv.Key).ToArray());
            // Round-trip parse (invariant-culture R format) rather than exact string match.
            Assert.Equal(-0.0972, double.Parse(Val(p, "lat"), System.Globalization.CultureInfo.InvariantCulture), 6);
            Assert.Equal(285.373, double.Parse(Val(p, "lon"), System.Globalization.CultureInfo.InvariantCulture), 3);
        }

        [Fact]
        public void Payload_NullSiteBody_EmptyStrings()
        {
            var p = TestCommandPlantFlag.BuildCompletePayload(null, null, 0.0, 0.0);
            Assert.Equal(string.Empty, Val(p, "flagSite"));
            Assert.Equal(string.Empty, Val(p, "body"));
        }
    }
}
