using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>EvaChuteDeploy</c> seam verb's decision core (EVA-4).
    /// Every cell here is a decision the live applier delegates: the arm refusal ladder
    /// (mirroring the stock keybind path's own guards), the synchronous arm verification,
    /// the canopy-open classification, the DOWN situation set, and the two-stage bounded
    /// completion (including the two-aliveness-bit split: the DEBOUNCED bit drives only
    /// KerbalLost, the RAW per-poll read is a required CompleteOk conjunct). No KSP types
    /// are touched.
    /// </summary>
    public class TestCommandEvaChuteDeployTests
    {
        // ----- IsCanopyOpen -----

        // NOTE: the EvaChuteState enum is internal, so it cannot appear in a public xUnit
        // theory signature (the same constraint TestCommandDispatchStateTests documents).
        // These cells enumerate the states inside a [Fact] instead of via [InlineData].
        [Fact]
        public void IsCanopyOpen_OnlySemiOrFull()
        {
            Assert.True(TestCommandEvaChuteDeploy.IsCanopyOpen(EvaChuteState.SemiDeployed));
            Assert.True(TestCommandEvaChuteDeploy.IsCanopyOpen(EvaChuteState.Deployed));
            Assert.False(TestCommandEvaChuteDeploy.IsCanopyOpen(EvaChuteState.Stowed));
            // ARMED is NOT open.
            Assert.False(TestCommandEvaChuteDeploy.IsCanopyOpen(EvaChuteState.Active));
            Assert.False(TestCommandEvaChuteDeploy.IsCanopyOpen(EvaChuteState.Cut));
            // Unreadable fails closed.
            Assert.False(TestCommandEvaChuteDeploy.IsCanopyOpen(EvaChuteState.Unknown));
        }

        // ----- IsDownSituation -----

        [Theory]
        [InlineData("LANDED", true)]
        [InlineData("SPLASHED", true)]
        [InlineData("FLYING", false)]
        [InlineData("SUB_ORBITAL", false)]
        [InlineData("PRELAUNCH", false)]
        [InlineData("landed", false)]   // ordinal, not case-insensitive
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsDownSituation_OnlyLandedOrSplashed(string situation, bool expected)
        {
            Assert.Equal(expected, TestCommandEvaChuteDeploy.IsDownSituation(situation));
        }

        // ----- DecideArmRefusal (the stock keybind path's guard order) -----

        [Fact]
        public void ArmRefusal_None_WhenEveryGateOpen()
        {
            Assert.Null(TestCommandEvaChuteDeploy.DecideArmRefusal(
                activeVesselIsEva: true, hasChuteModule: true, chuteModuleEnabled: true,
                state: EvaChuteState.Stowed, jetpackDeployed: false));
        }

        [Fact]
        public void ArmRefusal_NotEva_TakesPrecedence()
        {
            // Every other gate open, but the active vessel is a craft: not-eva wins.
            Assert.Equal(TestCommandEvaChuteDeploy.RefusalNotEva,
                TestCommandEvaChuteDeploy.DecideArmRefusal(
                    activeVesselIsEva: false, hasChuteModule: true, chuteModuleEnabled: true,
                    state: EvaChuteState.Stowed, jetpackDeployed: false));
        }

        [Fact]
        public void ArmRefusal_NoModule_BeforeUnavailable()
        {
            Assert.Equal(TestCommandEvaChuteDeploy.RefusalNoChuteModule,
                TestCommandEvaChuteDeploy.DecideArmRefusal(
                    activeVesselIsEva: true, hasChuteModule: false, chuteModuleEnabled: false,
                    state: EvaChuteState.Stowed, jetpackDeployed: false));
        }

        [Fact]
        public void ArmRefusal_Unavailable_WhenModuleSwitchedOff()
        {
            // The fixture-contract failure: the module exists but KerbalEVA.UpdatePackModels
            // left it disabled because there is no evaChute cargo part in the inventory.
            Assert.Equal(TestCommandEvaChuteDeploy.RefusalChuteUnavailable,
                TestCommandEvaChuteDeploy.DecideArmRefusal(
                    activeVesselIsEva: true, hasChuteModule: true, chuteModuleEnabled: false,
                    state: EvaChuteState.Stowed, jetpackDeployed: false));
        }

        [Fact]
        public void ArmRefusal_NotStowed_ForEveryNonStowedState()
        {
            EvaChuteState[] nonStowed =
            {
                EvaChuteState.Active, EvaChuteState.SemiDeployed, EvaChuteState.Deployed,
                EvaChuteState.Cut, EvaChuteState.Unknown,
            };
            foreach (EvaChuteState state in nonStowed)
            {
                Assert.Equal(TestCommandEvaChuteDeploy.RefusalNotStowed,
                    TestCommandEvaChuteDeploy.DecideArmRefusal(
                        activeVesselIsEva: true, hasChuteModule: true, chuteModuleEnabled: true,
                        state: state, jetpackDeployed: false));
            }
        }

        [Fact]
        public void ArmRefusal_JetpackDeployed_MirrorsStockRefusal()
        {
            // Stock KerbalEVA.cs:9575 posts #autoLOC_8004227 and does NOT call Deploy()
            // while the jetpack is out; the seam refuses instead of bypassing it.
            Assert.Equal(TestCommandEvaChuteDeploy.RefusalJetpackDeployed,
                TestCommandEvaChuteDeploy.DecideArmRefusal(
                    activeVesselIsEva: true, hasChuteModule: true, chuteModuleEnabled: true,
                    state: EvaChuteState.Stowed, jetpackDeployed: true));
        }

        // ----- ArmTook (the synchronous post-Deploy() verification) -----

        [Fact]
        public void ArmTook_RejectsStillStowed()
        {
            // Stock silently refused (shielded from airstream): the state never left STOWED.
            Assert.False(TestCommandEvaChuteDeploy.ArmTook(EvaChuteState.Stowed));
            // Unreadable fails closed.
            Assert.False(TestCommandEvaChuteDeploy.ArmTook(EvaChuteState.Unknown));
            // The normal arm, plus the fast-frame cases where the canopy already opened.
            Assert.True(TestCommandEvaChuteDeploy.ArmTook(EvaChuteState.Active));
            Assert.True(TestCommandEvaChuteDeploy.ArmTook(EvaChuteState.SemiDeployed));
            Assert.True(TestCommandEvaChuteDeploy.ArmTook(EvaChuteState.Deployed));
        }

        // ----- DecideChuteCompletion -----

        [Fact]
        public void Completion_Ok_WhenCanopyVerifiedAndDownAndSettled()
        {
            Assert.Equal(EvaChuteCompletionDecision.CompleteOk,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 200.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: true,
                    canopyVerified: true,
                    awaitDown: true, down: true, sceneSettled: true, budget: 420.0));
        }

        [Fact]
        public void Completion_Ok_AtCanopyOnly_WhenAwaitDownOff()
        {
            // awaitDown=false: the verb's contract ends at a verified canopy.
            Assert.Equal(EvaChuteCompletionDecision.CompleteOk,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 5.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: true,
                    canopyVerified: true,
                    awaitDown: false, down: false, sceneSettled: true, budget: 420.0));
        }

        [Fact]
        public void Completion_Waits_WhileChuteMerelyArmed()
        {
            // The module is ACTIVE but the canopy has not opened: NOT done, even settled
            // and even with the kerbal somehow already down.
            Assert.Equal(EvaChuteCompletionDecision.StillWaiting,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 3.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: true,
                    canopyVerified: false,
                    awaitDown: true, down: true, sceneSettled: true, budget: 420.0));
        }

        [Fact]
        public void Completion_Waits_WhileStillDescending()
        {
            Assert.Equal(EvaChuteCompletionDecision.StillWaiting,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 100.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: true,
                    canopyVerified: true,
                    awaitDown: true, down: false, sceneSettled: true, budget: 420.0));
        }

        [Fact]
        public void Completion_Waits_WhileSceneUnsettled()
        {
            Assert.Equal(EvaChuteCompletionDecision.StillWaiting,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 100.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: true,
                    canopyVerified: true,
                    awaitDown: true, down: true, sceneSettled: false, budget: 420.0));
        }

        [Fact]
        public void Completion_CanopyTimeout_NamesTheStalledStage()
        {
            // Budget gone with the canopy never observed: an armed-but-never-opened chute
            // must be an honest ERROR, never a false OK.
            Assert.Equal(EvaChuteCompletionDecision.CanopyTimeout,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 420.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: true,
                    canopyVerified: false,
                    awaitDown: true, down: false, sceneSettled: true, budget: 420.0));
            Assert.Equal(TestCommandEvaChuteDeploy.ErrorCanopyTimeout,
                TestCommandEvaChuteDeploy.ErrorTokenFor(EvaChuteCompletionDecision.CanopyTimeout));
        }

        [Fact]
        public void Completion_DownTimeout_WhenCanopyOpenedButNeverLanded()
        {
            Assert.Equal(EvaChuteCompletionDecision.DownTimeout,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 500.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: true,
                    canopyVerified: true,
                    awaitDown: true, down: false, sceneSettled: true, budget: 420.0));
            Assert.Equal(TestCommandEvaChuteDeploy.ErrorDownTimeout,
                TestCommandEvaChuteDeploy.ErrorTokenFor(EvaChuteCompletionDecision.DownTimeout));
        }

        // ----- KerbalTreatedAlive (loss debounce) -----

        [Fact]
        public void KerbalTreatedAlive_AliveReadIsAlwaysAlive()
        {
            Assert.True(TestCommandEvaChuteDeploy.KerbalTreatedAlive(
                aliveThisPoll: true, consecutiveGonePolls: 0, debouncePolls: 3));
            // Even an absurd stale counter cannot override a live alive read (the applier
            // resets the counter on every alive poll, but the decision fails safe anyway).
            Assert.True(TestCommandEvaChuteDeploy.KerbalTreatedAlive(
                aliveThisPoll: true, consecutiveGonePolls: 99, debouncePolls: 3));
        }

        [Fact]
        public void KerbalTreatedAlive_AbsorbsTransientGoneReads()
        {
            // A one- or two-frame unreadable sample must not red an otherwise-good descent.
            Assert.True(TestCommandEvaChuteDeploy.KerbalTreatedAlive(false, 1, 3));
            Assert.True(TestCommandEvaChuteDeploy.KerbalTreatedAlive(false, 2, 3));
        }

        [Fact]
        public void KerbalTreatedAlive_RealLossSurvivesTheDebounce()
        {
            // A destroyed part never comes back, so the run reaches the threshold and the
            // loss is reported. The debounce delays the verdict; it never masks it.
            Assert.False(TestCommandEvaChuteDeploy.KerbalTreatedAlive(false, 3, 3));
            Assert.False(TestCommandEvaChuteDeploy.KerbalTreatedAlive(false, 10, 3));
        }

        [Fact]
        public void Completion_KerbalLost_BeatsEverything()
        {
            // "Landed" with a dead kerbal is a FAILURE. The loss short-circuits ahead of
            // the positive completion even when every other conjunct is satisfied.
            Assert.Equal(EvaChuteCompletionDecision.KerbalLost,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 200.0, kerbalTreatedAlive: false, kerbalAliveThisPoll: false,
                    canopyVerified: true,
                    awaitDown: true, down: true, sceneSettled: true, budget: 420.0));
            Assert.Equal(TestCommandEvaChuteDeploy.ErrorKerbalLost,
                TestCommandEvaChuteDeploy.ErrorTokenFor(EvaChuteCompletionDecision.KerbalLost));
        }

        [Fact]
        public void Completion_NeverOk_OnThePollTheKerbalFirstReadsGone()
        {
            // THE loss-debounce hole: with awaitDown=false the canopy latch + a settled
            // scene satisfy every other conjunct, so a kerbal that dies INSIDE the 3-poll
            // debounce window (treated-alive still true, raw read already false) would
            // have completed OK on the very poll the loss began. The RAW bit is a required
            // CompleteOk conjunct, so that poll can only keep waiting.
            Assert.Equal(EvaChuteCompletionDecision.StillWaiting,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 5.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: false,
                    canopyVerified: true,
                    awaitDown: false, down: false, sceneSettled: true, budget: 420.0));
            // Same with the awaitDown=true shape EVA-4 actually ships.
            Assert.Equal(EvaChuteCompletionDecision.StillWaiting,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 200.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: false,
                    canopyVerified: true,
                    awaitDown: true, down: true, sceneSettled: true, budget: 420.0));
        }

        [Fact]
        public void Completion_LossWinsOnceTheDebounceExpires()
        {
            // The debounce DELAYS the verdict, it never masks it: once the run of gone
            // reads reaches the threshold the treated-alive bit flips false and the loss
            // short-circuits, even though every other conjunct is still satisfied.
            Assert.False(TestCommandEvaChuteDeploy.KerbalTreatedAlive(
                aliveThisPoll: false, consecutiveGonePolls: 3, debouncePolls: 3));
            Assert.Equal(EvaChuteCompletionDecision.KerbalLost,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 200.0, kerbalTreatedAlive: false, kerbalAliveThisPoll: false,
                    canopyVerified: true,
                    awaitDown: true, down: true, sceneSettled: true, budget: 420.0));
        }

        [Fact]
        public void Completion_TimesOutHonestly_WhenBudgetEndsMidLossDebounce()
        {
            // Budget gone while the raw read says gone but the debounce has not expired:
            // the two honest budget terminals still apply (never a false OK), and the
            // canopy latch picks which one names the stalled stage.
            Assert.Equal(EvaChuteCompletionDecision.DownTimeout,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 420.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: false,
                    canopyVerified: true,
                    awaitDown: true, down: true, sceneSettled: true, budget: 420.0));
            Assert.Equal(EvaChuteCompletionDecision.CanopyTimeout,
                TestCommandEvaChuteDeploy.DecideChuteCompletion(
                    elapsed: 420.0, kerbalTreatedAlive: true, kerbalAliveThisPoll: false,
                    canopyVerified: false,
                    awaitDown: true, down: false, sceneSettled: true, budget: 420.0));
        }

        [Fact]
        public void ErrorTokenFor_NullOnNonTerminalDecisions()
        {
            Assert.Null(TestCommandEvaChuteDeploy.ErrorTokenFor(EvaChuteCompletionDecision.StillWaiting));
            Assert.Null(TestCommandEvaChuteDeploy.ErrorTokenFor(EvaChuteCompletionDecision.CompleteOk));
        }

        // ----- Payload -----

        [Fact]
        public void CompletePayload_CarriesLatchedCanopyAndArrivalEvidence()
        {
            var payload = TestCommandEvaChuteDeploy.BuildCompletePayload(
                "Jebediah Kerman", 12345u,
                    canopyVerified: true,
                chuteState: EvaChuteState.Cut, down: true, situation: "LANDED");

            Assert.Contains(payload, kv => kv.Key == "kerbal" && kv.Value == "Jebediah Kerman");
            Assert.Contains(payload, kv => kv.Key == "evaPid" && kv.Value == "12345");
            // canopy=true even though the live state reads Cut: the stock module auto-cuts
            // below 0.5 m/s once the kerbal is down, which is exactly why it is latched.
            Assert.Contains(payload, kv => kv.Key == "canopy" && kv.Value == "true");
            Assert.Contains(payload, kv => kv.Key == "chuteState" && kv.Value == "Cut");
            Assert.Contains(payload, kv => kv.Key == "down" && kv.Value == "true");
            Assert.Contains(payload, kv => kv.Key == "situation" && kv.Value == "LANDED");
        }

        [Fact]
        public void CompletePayload_NullKerbalBecomesEmptyString()
        {
            var payload = TestCommandEvaChuteDeploy.BuildCompletePayload(
                null, 0u,
                    canopyVerified: false, chuteState: EvaChuteState.Unknown,
                down: false, situation: null);
            Assert.Contains(payload, kv => kv.Key == "kerbal" && kv.Value == "");
            Assert.Contains(payload, kv => kv.Key == "situation" && kv.Value == "");
        }

        // ----- Verb-table registration -----

        [Fact]
        public void EvaChuteDeploy_IsImplementedNotReserved()
        {
            Assert.Equal(TestCommandVerbClass.Implemented,
                TestCommandVerbs.Classify("EvaChuteDeploy"));
            Assert.Contains("EvaChuteDeploy", TestCommandVerbs.ImplementedVerbNames);
            Assert.DoesNotContain("EvaChuteDeploy", TestCommandVerbs.ReservedVerbNames);
        }
    }
}
