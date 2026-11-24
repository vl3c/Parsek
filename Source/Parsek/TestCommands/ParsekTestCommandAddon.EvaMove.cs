using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the two-phase <c>EvaMove</c> verb; the contract is on
    /// <see cref="TestCommandEvaMove"/>. It deploys the jetpack through stock's own
    /// <c>KerbalEVA.ToggleJetpack()</c> when needed, arms <see cref="EvaMoveInputPatch"/> with
    /// the direction frozen in world space, and the completion releases the input at the
    /// step's end UT, presses the stow when asked and answers once the scene settles.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // The move in progress. Only one can exist: the two-phase head holds the FIFO.
        private Vessel evaMoveVessel;
        private KerbalEVA evaMoveKerbal;
        private string evaMoveKerbalName;
        private uint evaMovePid;
        private TestCommandEvaMove.Args evaMoveArgs;
        private EvaMovePhase evaMovePhase;
        private double evaMoveStartUT;
        private double evaMoveEndUT;
        private double evaMoveReleasedUT;
        private Vector3d evaMoveStartBodyRel;
        private bool evaMoveEffectSeen;
        private int evaMoveStowFires;
        private int evaMovePollsSinceStow;
        private Vessel evaMoveRef;
        private uint evaMoveRefPid;
        private double evaMoveRefDistStart;

        private void EvaMoveImpl(ParsedCommand cmd)
        {
            ResetEvaMoveState();
            string argReason = TestCommandEvaMove.TryParseArgs(
                ArgOrNull(cmd, TestCommandEvaMove.ModeKey), ArgOrNull(cmd, TestCommandEvaMove.DirKey),
                ArgOrNull(cmd, TestCommandEvaMove.SecondsKey), ArgOrNull(cmd, TestCommandEvaMove.StowKey),
                out TestCommandEvaMove.Args args);
            if (argReason != null)
            {
                RejectEvaMove(argReason, $"mode={ArgOrNull(cmd, TestCommandEvaMove.ModeKey) ?? string.Empty} "
                    + $"dir={ArgOrNull(cmd, TestCommandEvaMove.DirKey) ?? string.Empty} "
                    + $"seconds={ArgOrNull(cmd, TestCommandEvaMove.SecondsKey) ?? string.Empty} "
                    + $"stow={ArgOrNull(cmd, TestCommandEvaMove.StowKey) ?? string.Empty}");
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA evaCtl = active != null && active.isEVA
                ? active.FindPartModuleImplementing<KerbalEVA>() : null;
            bool isEva = evaCtl != null && evaCtl.part != null;
            bool ragdoll = isEva && evaCtl.isRagdoll;
            bool hasJetpack = isEva && evaCtl.HasJetpack;
            double fuel = isEva ? ReadEvaFuel(evaCtl) : 0.0;
            bool onGround = active != null && active.LandedOrSplashed;
            string refusal = TestCommandEvaMove.DecideArmRefusal(args.Mode, isEva, ragdoll, hasJetpack, fuel, onGround);
            if (refusal != null)
            {
                RejectEvaMove(refusal, $"active={(active != null ? active.vesselName : "none")} "
                    + $"isEva={Bool(isEva)} ragdoll={Bool(ragdoll)} hasJetpack={Bool(hasJetpack)} "
                    + $"fuel={fuel.ToString("F3", CultureInfo.InvariantCulture)} onGround={Bool(onGround)}");
                return;
            }

            bool packBefore = evaCtl.JetpackDeployed;
            if (args.Mode == EvaMoveMode.Jetpack && !packBefore)
            {
                // Stock's own toggle: a silent no-op outside the four idle FSM states, so the
                // press is verified by re-reading the flag it flips in the same call.
                try
                {
                    evaCtl.ToggleJetpack();
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(Tag, $"evamove ToggleJetpack threw: {ex.GetType().Name}: {ex.Message}");
                    SetExecResult("ERROR", null, TestCommandEvaMove.ThrewReason);
                    return;
                }
                if (!evaCtl.JetpackDeployed)
                {
                    RejectEvaMove(TestCommandEvaMove.PackRefusedReason,
                        $"kerbal={active.vesselName} state={(evaCtl.fsm != null && evaCtl.fsm.CurrentState != null ? evaCtl.fsm.CurrentState.name : "?")}");
                    return;
                }
            }

            TestCommandEvaMove.AxisSigns(args.Dir, out int f, out int r, out int u);
            Transform t = evaCtl.transform;
            Vector3 worldDir = (t.forward * f + t.right * r + t.up * u).normalized;

            evaMoveVessel = active;
            evaMoveKerbal = evaCtl;
            evaMoveKerbalName = ResolveEvaKerbalName(active);
            evaMovePid = active.persistentId;
            evaMoveArgs = args;
            evaMovePhase = EvaMovePhase.Driving;
            evaMoveStartUT = Planetarium.GetUniversalTime();
            evaMoveEndUT = evaMoveStartUT + args.Seconds;
            evaMoveReleasedUT = evaMoveEndUT;
            evaMoveStartBodyRel = BodyRelativePosition(active);
            evaMoveRef = FindEvaMoveReference(active, out evaMoveRefDistStart);
            evaMoveRefPid = evaMoveRef != null ? evaMoveRef.persistentId : 0u;

            EvaMoveInputPatch.Arm(evaCtl.part.flightID, args.Mode == EvaMoveMode.Jetpack, worldDir);
            ParsekLog.Info(Tag, TestCommandEvaMove.FormatStartLine(evaMoveKerbalName, evaMovePid, args,
                evaMoveStartUT, packBefore, evaMoveRefPid, evaMoveRefDistStart));
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEvaMove(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandEvaMove.Verb);
            Vessel v = evaMoveVessel;
            KerbalEVA k = evaMoveKerbal;
            bool alive = v != null && k != null && v.state != Vessel.State.DEAD
                && v.parts != null && v.parts.Count > 0;
            bool ragdoll = alive && k.isRagdoll;
            bool packDeployed = alive && k.JetpackDeployed;
            double ut = Planetarium.GetUniversalTime();
            double moved = alive ? (BodyRelativePosition(v) - evaMoveStartBodyRel).magnitude : double.NaN;
            if (alive && !evaMoveEffectSeen)
            {
                evaMoveEffectSeen = evaMoveArgs.Mode == EvaMoveMode.Jetpack
                    ? k.JetpackIsThrusting
                    : TestCommandEvaMove.WalkMoved(moved);
            }
            if (evaMoveStowFires > 0) evaMovePollsSinceStow++;
            bool settled = settleCounter == 0 && !sceneTransitioning;

            EvaMoveDecision decision = TestCommandEvaMove.Decide(evaMovePhase, alive, ragdoll, ut, evaMoveEndUT,
                evaMoveEffectSeen, evaMoveArgs.Stow, packDeployed, evaMoveStowFires, evaMovePollsSinceStow,
                settled, elapsed, budget);
            switch (decision)
            {
                case EvaMoveDecision.KeepWaiting:
                    ParsekLog.VerboseRateLimited(Tag, "evamove-wait",
                        $"evamove waiting phase={evaMovePhase} ut={ut.ToString("F2", CultureInfo.InvariantCulture)} "
                        + $"endUT={evaMoveEndUT.ToString("F2", CultureInfo.InvariantCulture)} "
                        + $"effect={Bool(evaMoveEffectSeen)} moved={TestCommandEvaMove.FormatMeters(moved)} "
                        + $"pack={Bool(packDeployed)} frames={Int(EvaMoveInputPatch.FramesApplied)} "
                        + $"stowFires={Int(evaMoveStowFires)} settled={Bool(settled)}");
                    return;
                case EvaMoveDecision.ReleaseDrive:
                    int frames = EvaMoveInputPatch.FramesApplied;
                    EvaMoveInputPatch.Disarm();
                    evaMovePhase = EvaMovePhase.Released;
                    evaMoveReleasedUT = ut;
                    ParsekLog.Info(Tag, $"evamove released kerbal='{evaMoveKerbalName ?? string.Empty}' "
                        + $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} frames={Int(frames)} "
                        + $"effect={Bool(evaMoveEffectSeen)} moved={TestCommandEvaMove.FormatMeters(moved)}");
                    return;
                case EvaMoveDecision.FireStow:
                    FireEvaMoveStow();
                    return;
            }

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string kerbal = evaMoveKerbalName;
            uint evaPid = evaMovePid;
            TestCommandEvaMove.Args a = evaMoveArgs;
            double startUT = evaMoveStartUT;
            double endUT = evaMoveReleasedUT;
            bool effect = evaMoveEffectSeen;
            uint refPid = evaMoveRefPid;
            double refDistStart = evaMoveRefDistStart;
            double refDistEnd = -1.0, refSpeed = -1.0;
            Vessel refVessel = evaMoveRef;
            if (alive && refVessel != null && refVessel.state != Vessel.State.DEAD)
            {
                refDistEnd = (v.GetWorldPos3D() - refVessel.GetWorldPos3D()).magnitude;
                refSpeed = (v.obt_velocity - refVessel.obt_velocity).magnitude;
            }
            int stowFires = evaMoveStowFires;
            ClearTwoPhase();

            if (decision == EvaMoveDecision.CompleteOk)
            {
                ParsekLog.Info(Tag, $"evamove complete kerbal='{kerbal ?? string.Empty}' "
                    + $"evaPid={evaPid.ToString(CultureInfo.InvariantCulture)} mode={TestCommandEvaMove.ModeToken(a.Mode)} "
                    + $"dir={TestCommandEvaMove.DirToken(a.Dir)} effect={Bool(effect)} deployed={Bool(packDeployed)} "
                    + $"moved={TestCommandEvaMove.FormatMeters(moved)} refPid={refPid.ToString(CultureInfo.InvariantCulture)} "
                    + $"refDistEnd={TestCommandEvaMove.FormatMeters(refDistEnd)} "
                    + $"refSpeed={TestCommandEvaMove.FormatMeters(refSpeed)} stowFires={Int(stowFires)}");
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandEvaMove.BuildOkPayload(kerbal, evaPid, a, startUT, endUT, effect, packDeployed,
                        refPid, refDistStart, refDistEnd, refSpeed),
                    null, dequeueHead: true);
                return;
            }

            string reason = TestCommandEvaMove.ErrorTokenFor(decision) ?? TestCommandEvaMove.TimeoutReason;
            if (decision == EvaMoveDecision.Timeout)
                TestCommandDiagnostics.Timeout(id, verb, elapsed, reason);
            ParsekLog.Error(Tag, "evamove error reason=" + reason
                + $" kerbal='{kerbal ?? string.Empty}' alive={Bool(alive)} ragdoll={Bool(ragdoll)} "
                + $"effect={Bool(effect)} pack={Bool(packDeployed)} stowFires={Int(stowFires)} "
                + $"moved={TestCommandEvaMove.FormatMeters(moved)} "
                + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, reason, dequeueHead: true);
        }

        // One press of stock's own jetpack toggle to stow the pack after the input is released.
        private void FireEvaMoveStow()
        {
            evaMoveStowFires++;
            evaMovePollsSinceStow = 0;
            ParsekLog.Info(Tag, $"evamove stow press kerbal='{evaMoveKerbalName ?? string.Empty}' "
                + $"fire={Int(evaMoveStowFires)}");
            try
            {
                evaMoveKerbal.ToggleJetpack();
            }
            catch (Exception ex)
            {
                string id = completionId; long seq = completionSeq; string verb = completionVerb;
                string kerbal = evaMoveKerbalName;
                ClearTwoPhase();
                ParsekLog.Error(Tag, "evamove error reason=" + TestCommandEvaMove.ThrewReason
                    + $" kerbal='{kerbal ?? string.Empty}' {ex.GetType().Name}: {ex.Message}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, TestCommandEvaMove.ThrewReason, dequeueHead: true);
            }
        }

        // The nearest loaded, non-EVA, non-ghost vessel: what a later EvaBoard needs to reach.
        private static Vessel FindEvaMoveReference(Vessel kerbal, out double distance)
        {
            distance = -1.0;
            Vessel best = null;
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            if (kerbal == null || loaded == null) return null;
            Vector3d pos = kerbal.GetWorldPos3D();
            for (int i = 0; i < loaded.Count; i++)
            {
                Vessel v = loaded[i];
                if (v == null || v == kerbal || v.isEVA || v.state == Vessel.State.DEAD) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                double d = (v.GetWorldPos3D() - pos).magnitude;
                if (best == null || d < distance)
                {
                    best = v;
                    distance = d;
                }
            }
            return best;
        }

        // Body-relative position: immune to the floating-origin shifts a world position takes.
        private static Vector3d BodyRelativePosition(Vessel v)
            => v.mainBody != null ? v.GetWorldPos3D() - v.mainBody.position : v.GetWorldPos3D();

        // KerbalEVA.Fuel reads the jetpack's propellant resource, which is absent on a kerbal
        // with no jetpack; such a kerbal reads 0.
        private static double ReadEvaFuel(KerbalEVA evaCtl)
        {
            try
            {
                return evaCtl.Fuel;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        // A move must never leave its input drive or stale references behind.
        private void ResetEvaMoveState()
        {
            EvaMoveInputPatch.Disarm();
            evaMoveVessel = null;
            evaMoveKerbal = null;
            evaMoveKerbalName = null;
            evaMovePid = 0u;
            evaMoveArgs = default(TestCommandEvaMove.Args);
            evaMovePhase = EvaMovePhase.Driving;
            evaMoveStartUT = 0.0;
            evaMoveEndUT = 0.0;
            evaMoveReleasedUT = 0.0;
            evaMoveStartBodyRel = Vector3d.zero;
            evaMoveEffectSeen = false;
            evaMoveStowFires = 0;
            evaMovePollsSinceStow = 0;
            evaMoveRef = null;
            evaMoveRefPid = 0u;
            evaMoveRefDistStart = -1.0;
        }

        private void RejectEvaMove(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"evamove rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
