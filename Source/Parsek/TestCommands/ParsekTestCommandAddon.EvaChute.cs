// ParsekTestCommandAddon partial: the EvaChuteDeploy seam-verb body (EVA-4).
// =====================================================================================
// Lane A (thin Unity applier) for the kerbal personal-parachute verb. Every decision is
// delegated to the pure sibling TestCommandEvaChuteDeploy (Lane B, xUnit-covered); this
// file only samples live KSP state, calls the real public stock entry point
// (ModuleEvaChute.Deploy(), inherited from ModuleParachute - the SAME method both the
// EVA_ChuteDeploy keybind path and the PAW "Deploy Chute" event call), and stashes the
// verdict via SetExecResult / the PENDING sentinel.
//
// No reflection is needed here: ModuleEvaChute / ModuleParachute.deploymentState /
// ModuleParachute.Deploy() / PartModule.moduleIsEnabled / KerbalEVA.JetpackDeployed are
// all public on Assembly-CSharp.
//
// TWO-STAGE COMPLETION (both stages bounded, both named on timeout):
//   Stage A - CANOPY: Deploy() only ARMS (STOWED -> ACTIVE, synchronously). The module's
//     own FixedUpdate opens the canopy once static pressure > minAirPressureToOpen (0.04
//     atm) or altitude < deployAltitude (1000 m), speed > 1 m/s, and DeploySafe reads SAFE
//     (which it never does for the first 1.0 s after the part unpacks). So the canopy is a
//     BOUNDED WAIT on the observed deployment state, latched the first poll it is open.
//   Stage B - DOWN (opt-in, awaitDown=true): hold until the kerbal's situation is LANDED
//     or SPLASHED with the kerbal still ALIVE. Same opt-in shape as EvaExit's release /
//     settleSeconds stages, so the verb stays single-responsibility ("get this kerbal down
//     under its own canopy") without a second verb for the descent.
// =====================================================================================
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    public partial class ParsekTestCommandAddon
    {
        // ----- EvaChuteDeploy two-phase state (re-armed wholesale at each EvaChuteDeployImpl) -----
        private string evaChuteKerbalName;
        private Vessel evaChuteVessel;
        private ModuleEvaChute evaChuteModule;
        private bool evaChuteAwaitDown;
        // LATCHED verified-canopy-open. Never re-read at completion: the stock module
        // auto-CUTS below autoCutSpeed (0.5 m/s), so a landed kerbal reads Cut and a
        // re-read would fail the very landing this verb exists to prove.
        private bool evaChuteCanopyVerified;
        private bool evaChuteCanopyLogged;
        // Consecutive polls that read the kerbal gone (debounced so a single-frame
        // unreadable sample cannot red an otherwise-good descent).
        private int evaChuteGonePolls;

        // =====================================================================================
        // EvaChuteDeploy (two-phase, irreversible)
        // =====================================================================================
        private void EvaChuteDeployImpl(ParsedCommand cmd)
        {
            bool awaitDown = string.Equals(ArgOrNull(cmd, "awaitDown"), "true",
                System.StringComparison.OrdinalIgnoreCase);

            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA evaCtl = active != null ? active.FindPartModuleImplementing<KerbalEVA>() : null;
            ModuleEvaChute chute = active != null ? active.FindPartModuleImplementing<ModuleEvaChute>() : null;

            bool isEva = evaCtl != null && active != null && active.isEVA;
            bool hasModule = chute != null;
            bool moduleEnabled = chute != null && chute.moduleIsEnabled;
            EvaChuteState state = ReadChuteState(chute);
            bool jetpackDeployed = evaCtl != null && evaCtl.JetpackDeployed;

            string refusal = TestCommandEvaChuteDeploy.DecideArmRefusal(
                isEva, hasModule, moduleEnabled, state, jetpackDeployed);
            if (refusal != null)
            {
                ParsekLog.Warn(Tag,
                    $"evachutedeploy refused reason={refusal} isEva={Bool(isEva)} hasModule={Bool(hasModule)} " +
                    $"moduleEnabled={Bool(moduleEnabled)} state={state} jetpack={Bool(jetpackDeployed)}");
                SetExecResult("REJECTED", null, refusal);
                return;
            }

            string kerbal = ResolveEvaKerbalName(active);
            uint evaPid = active.persistentId;
            ParsekLog.Info(Tag,
                $"evachutedeploy start kerbal={kerbal ?? string.Empty} evaPid={evaPid.ToString(CultureInfo.InvariantCulture)} " +
                $"alt={FormatMetres(active.altitude)} vspeed={FormatMetres(active.verticalSpeed)} " +
                $"situation={active.situation} awaitDown={Bool(awaitDown)}");

            // The stock call. Deploy() is synchronous (STOWED -> ACTIVE in-call) and returns
            // silently when the part is shielded from airstream, so the arm is VERIFIED by
            // re-reading the state - never assumed from the call having been made.
            try
            {
                chute.Deploy();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error(Tag, $"evachutedeploy Deploy() threw: {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, "eva-chute-deploy-threw");
                return;
            }

            EvaChuteState postState = ReadChuteState(chute);
            if (!TestCommandEvaChuteDeploy.ArmTook(postState))
            {
                // Stock refused with NO side effect (shielded / not deployable): REJECTED family.
                ParsekLog.Warn(Tag,
                    $"evachutedeploy refused reason={TestCommandEvaChuteDeploy.RefusalArmRefused} " +
                    $"state={postState} (Deploy() left the chute STOWED)");
                SetExecResult("REJECTED", null, TestCommandEvaChuteDeploy.RefusalArmRefused);
                return;
            }

            ParsekLog.Info(Tag, $"evachutedeploy armed kerbal={kerbal ?? string.Empty} state={postState}");

            evaChuteKerbalName = kerbal;
            evaChuteVessel = active;
            evaChuteModule = chute;
            evaChuteAwaitDown = awaitDown;
            evaChuteCanopyVerified = TestCommandEvaChuteDeploy.IsCanopyOpen(postState);
            evaChuteCanopyLogged = false;
            evaChuteGonePolls = 0;
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEvaChuteDeploy(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("EvaChuteDeploy");

            Vessel v = evaChuteVessel;
            bool aliveThisPoll = IsEvaKerbalAlive(v, evaChuteKerbalName);
            evaChuteGonePolls = aliveThisPoll ? 0 : evaChuteGonePolls + 1;
            bool alive = TestCommandEvaChuteDeploy.KerbalTreatedAlive(
                aliveThisPoll, evaChuteGonePolls, TestCommandEvaChuteDeploy.KerbalLossDebouncePolls);
            if (!aliveThisPoll)
            {
                ParsekLog.Warn(Tag,
                    $"evachutedeploy kerbal read gone kerbal={evaChuteKerbalName ?? string.Empty} " +
                    $"gonePolls={evaChuteGonePolls.ToString(CultureInfo.InvariantCulture)}/" +
                    $"{TestCommandEvaChuteDeploy.KerbalLossDebouncePolls.ToString(CultureInfo.InvariantCulture)}");
            }
            EvaChuteState state = ReadChuteState(evaChuteModule);

            // Latch the canopy the FIRST poll it is observed open (the stock module auto-cuts
            // below 0.5 m/s once the kerbal is down, so this observation is not re-readable).
            if (!evaChuteCanopyVerified && TestCommandEvaChuteDeploy.IsCanopyOpen(state))
                evaChuteCanopyVerified = true;
            if (evaChuteCanopyVerified && !evaChuteCanopyLogged)
            {
                evaChuteCanopyLogged = true;
                ParsekLog.Info(Tag,
                    $"evachutedeploy canopy verified kerbal={evaChuteKerbalName ?? string.Empty} state={state} " +
                    $"alt={FormatMetres(v != null ? v.altitude : double.NaN)} " +
                    $"vspeed={FormatMetres(v != null ? v.verticalSpeed : double.NaN)} " +
                    $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            }

            string situation = v != null ? v.situation.ToString() : string.Empty;
            bool down = TestCommandEvaChuteDeploy.IsDownSituation(situation);
            bool settled = settleCounter == 0 && !sceneTransitioning;

            // Per-poll diagnosability (rate-limited): the descent is minutes long, and a stall
            // must be readable from the log without a re-flight.
            ParsekLog.VerboseRateLimited(Tag, "evachutedeploy-wait",
                $"evachutedeploy wait elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s " +
                $"state={state} canopy={Bool(evaChuteCanopyVerified)} " +
                $"alt={FormatMetres(v != null ? v.altitude : double.NaN)} " +
                $"vspeed={FormatMetres(v != null ? v.verticalSpeed : double.NaN)} " +
                $"situation={situation} alive={Bool(aliveThisPoll)} aliveDebounced={Bool(alive)} " +
                $"settled={Bool(settled)}");

            // Both aliveness bits go in: the DEBOUNCED one gates KerbalLost (a transient
            // unreadable sample must not red a good descent), the RAW one is a required
            // CompleteOk conjunct (a death INSIDE the debounce window must not green out).
            EvaChuteCompletionDecision decision = TestCommandEvaChuteDeploy.DecideChuteCompletion(
                elapsed, alive, aliveThisPoll, evaChuteCanopyVerified, evaChuteAwaitDown,
                down, settled, budget);
            if (decision == EvaChuteCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string kerbal = evaChuteKerbalName;
            uint evaPid = v != null ? v.persistentId : 0u;
            bool canopy = evaChuteCanopyVerified;
            ClearTwoPhase();

            if (decision == EvaChuteCompletionDecision.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandEvaChuteDeploy.BuildCompletePayload(
                        kerbal, evaPid, canopy, state, down, situation);
                ParsekLog.Info(Tag,
                    $"evachutedeploy complete kerbal={kerbal ?? string.Empty} evaPid={evaPid.ToString(CultureInfo.InvariantCulture)} " +
                    $"canopy={Bool(canopy)} chuteState={state} down={Bool(down)} situation={situation} alive=true");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                return;
            }

            string msg = TestCommandEvaChuteDeploy.ErrorTokenFor(decision);
            // The diagnostics Timeout receipt is for the two genuine BUDGET terminals only.
            // KerbalLost is not a timeout - it is an immediate, evidence-driven failure - so
            // it rides the Error line below without a spurious timeout receipt.
            if (decision != EvaChuteCompletionDecision.KerbalLost)
                TestCommandDiagnostics.Timeout(id, verb, elapsed, msg);
            ParsekLog.Error(Tag,
                $"evachutedeploy {msg} kerbal={kerbal ?? string.Empty} canopy={Bool(canopy)} chuteState={state} " +
                $"situation={situation} alive={Bool(aliveThisPoll)} aliveDebounced={Bool(alive)} " +
                $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, msg, dequeueHead: true);
        }

        // ----- Live-state readers (thin; every decision over them is pure) -----

        // Map the live stock deployment state onto the pure enum. A null module or an
        // unmapped value reads Unknown, which the pure surface treats as canopy-CLOSED.
        private static EvaChuteState ReadChuteState(ModuleEvaChute chute)
        {
            if (chute == null) return EvaChuteState.Unknown;
            switch (chute.deploymentState)
            {
                case ModuleParachute.deploymentStates.STOWED: return EvaChuteState.Stowed;
                case ModuleParachute.deploymentStates.ACTIVE: return EvaChuteState.Active;
                case ModuleParachute.deploymentStates.SEMIDEPLOYED: return EvaChuteState.SemiDeployed;
                case ModuleParachute.deploymentStates.DEPLOYED: return EvaChuteState.Deployed;
                case ModuleParachute.deploymentStates.CUT: return EvaChuteState.Cut;
                default: return EvaChuteState.Unknown;
            }
        }

        // The kerbal's name on an EVA vessel (crew[0] of the single kerbalEVA part), or the
        // vessel name as the fallback (KSP names the EVA vessel after the kerbal).
        private static string ResolveEvaKerbalName(Vessel v)
        {
            if (v == null) return null;
            List<ProtoCrewMember> crew = v.GetVesselCrew();
            if (crew != null && crew.Count > 0 && crew[0] != null)
                return crew[0].name;
            return v.vesselName;
        }

        // ALIVE = the EVA vessel still exists with parts, still carries the kerbal, and that
        // roster entry is not Dead/Missing. A kerbal killed on impact loses its part (the
        // vessel goes away) and/or flips to Dead, so this is the survivability evidence the
        // DOWN terminal is gated on - "landed" with a dead kerbal must never read OK.
        private static bool IsEvaKerbalAlive(Vessel v, string kerbalName)
        {
            if (v == null || v.parts == null || v.parts.Count == 0) return false;
            List<ProtoCrewMember> crew = v.GetVesselCrew();
            if (crew == null || crew.Count == 0) return false;
            for (int i = 0; i < crew.Count; i++)
            {
                ProtoCrewMember pcm = crew[i];
                if (pcm == null) continue;
                if (!string.IsNullOrEmpty(kerbalName)
                    && !string.Equals(pcm.name, kerbalName, System.StringComparison.Ordinal))
                    continue;
                return pcm.rosterStatus != ProtoCrewMember.RosterStatus.Dead
                    && pcm.rosterStatus != ProtoCrewMember.RosterStatus.Missing;
            }
            return false;
        }

        // Compact, InvariantCulture, NaN-safe metric formatter for the diagnostics lines.
        private static string FormatMetres(double value)
            => double.IsNaN(value) || double.IsInfinity(value)
                ? "?"
                : value.ToString("F1", CultureInfo.InvariantCulture);
    }
}
