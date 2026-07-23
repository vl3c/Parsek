// ParsekTestCommandAddon partial: the EVA seam-verb executor bodies (M-C2).
// =====================================================================================
// The addon (ParsekTestCommandAddon) owns the three M-C2 EVA verb bodies + their two-phase
// completions: EvaExit / PlantFlag / EvaBoard. This split follows the design's Lane A (thin
// Unity applier on the addon) / Lane B (pure, xUnit-covered decision core) separation: every
// decision is delegated to the sibling pure surfaces (TestCommandEvaExit / TestCommandPlantFlag
// / TestCommandEvaBoard), so this file only samples live KSP state, calls the real public
// stock EVA / board / plant entry points, and stashes the verdict via SetExecResult / the
// PENDING sentinel. The two internal read-only accessors it reads
// (ParsekFlight.StructuralSplitPending / BoardMergeQuiescent) are the ONLY source touches
// outside TestCommands/ (design "What Doesn't Change").
//
// spawnEVA + FlightEVA.fetch + Part.airlock are not accessible at compile time (verified by
// the in-game EVA runtime tests, which reflect them the same way), so they are reached through
// the reflection handles below; every other surface (KerbalEVA.BoardPart / PlantFlag / fsm /
// Events / OnALadder) is public and called directly.
// =====================================================================================
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Parsek.TestCommands
{
    public partial class ParsekTestCommandAddon
    {
        // ----- Reflection handles for the non-compile-accessible EVA-spawn surfaces -----
        // Mirror the in-game EVA test bindings (InGameTests/RuntimeTests.cs): FlightEVA is a
        // FLIGHT-scene singleton whose spawnEVA is not compile-visible, and Part.airlock is a
        // non-public field.
        private static readonly System.Type FlightEvaType =
            typeof(Part).Assembly.GetType("FlightEVA", false);
        private static readonly FieldInfo FlightEvaFetchField =
            FlightEvaType?.GetField("fetch", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo FlightEvaSpawnMethod =
            FlightEvaType?.GetMethod("spawnEVA",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(ProtoCrewMember), typeof(Part), typeof(Transform), typeof(bool) }, null);
        private static readonly FieldInfo PartAirlockField =
            typeof(Part).GetField("airlock", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // ----- EvaExit two-phase state (re-armed wholesale at each EvaExitImpl) -----
        private string evaExitKerbalName;
        private Vessel evaExitVessel;
        private bool evaExitReleaseRequested;
        private bool evaExitReleaseApplied;
        private double evaExitSettleSeconds;
        private double evaExitSettleStartedAt;

        // In-memory (non-durable, cleared on process restart) pid of the vessel the LAST
        // EvaExit left from; EvaBoard's default target (F9).
        private uint lastEvaExitFromPid;

        // ----- PlantFlag two-phase state -----
        private KerbalEVA plantFlagController;
        private bool plantFlagFired;
        private bool plantFlagDialogAnswered;
        private bool plantFlagLastGateOpen;
        private HashSet<uint> plantFlagPreExistingFlagPids;
        private Vessel plantFlagVessel;

        // ----- EvaBoard two-phase state -----
        private string evaBoardKerbalName;
        private Vessel evaBoardEvaVessel;
        private Vessel evaBoardTargetVessel;
        private uint evaBoardTargetPid;

        // =====================================================================================
        // EvaExit (two-phase, irreversible)
        // =====================================================================================
        private void EvaExitImpl(ParsedCommand cmd)
        {
            string kerbalArg = ArgOrNull(cmd, "kerbal");
            bool releaseRequested = string.Equals(ArgOrNull(cmd, "release"), "true", System.StringComparison.OrdinalIgnoreCase);
            double settleSeconds = ParseSettleSeconds(ArgOrNull(cmd, "settleSeconds"));

            Vessel active = FlightGlobals.ActiveVessel;
            List<string> crewNames = active != null
                ? active.GetVesselCrew().Where(c => c != null).Select(c => c.name).ToList()
                : new List<string>();

            string resolved = TestCommandEvaExit.ResolveKerbalArg(kerbalArg, crewNames, out string resolveError);
            if (resolveError != null)
            {
                ParsekLog.Warn(Tag, $"evaexit refused reason={resolveError} kerbal={kerbalArg ?? string.Empty}");
                SetExecResult("REJECTED", null, resolveError);
                return;
            }

            // Resolve the crew member's part + its airlock transform.
            ProtoCrewMember pcm = null;
            Part fromPart = null;
            foreach (Part part in active.parts)
            {
                if (part?.protoModuleCrew == null) continue;
                foreach (ProtoCrewMember c in part.protoModuleCrew)
                {
                    if (c != null && string.Equals(c.name, resolved, System.StringComparison.Ordinal))
                    {
                        pcm = c; fromPart = part; break;
                    }
                }
                if (pcm != null) break;
            }
            if (pcm == null || fromPart == null)
            {
                ParsekLog.Warn(Tag, $"evaexit refused reason=kerbal-not-aboard kerbal={resolved}");
                SetExecResult("REJECTED", null, "kerbal-not-aboard");
                return;
            }

            Transform airlock = PartAirlockField != null ? PartAirlockField.GetValue(fromPart) as Transform : null;
            if (airlock == null)
            {
                ParsekLog.Warn(Tag, $"evaexit refused reason=no-airlock kerbal={resolved} part={fromPart.partInfo?.name ?? string.Empty}");
                SetExecResult("REJECTED", null, "no-airlock");
                return;
            }

            uint fromPid = fromPart.vessel != null ? fromPart.vessel.persistentId : 0u;
            ParsekLog.Info(Tag, $"evaexit start kerbal={resolved} fromPid={fromPid.ToString(CultureInfo.InvariantCulture)} release={Bool(releaseRequested)}");

            object flightEvaFetch = FlightEvaFetchField != null ? FlightEvaFetchField.GetValue(null) : null;
            if (FlightEvaSpawnMethod == null || flightEvaFetch == null)
            {
                ParsekLog.Error(Tag, "evaexit flighteva-unavailable (FlightEVA.fetch or spawnEVA not reflectable)");
                SetExecResult("ERROR", null, "flighteva-unavailable");
                return;
            }

            object spawned;
            try
            {
                // tryAllHatches: true - the stock UI's own shape (ground truth 1).
                spawned = FlightEvaSpawnMethod.Invoke(flightEvaFetch, new object[] { pcm, fromPart, airlock, true });
            }
            catch (TargetInvocationException ex)
            {
                ParsekLog.Error(Tag, $"evaexit spawnEVA threw: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
                SetExecResult("ERROR", null, "eva-spawn-threw");
                return;
            }

            KerbalEVA evaCtl = spawned as KerbalEVA;
            if (evaCtl == null || evaCtl.vessel == null)
            {
                // Null return: a stock refusal with NO side effect (obstructed hatch / fairing /
                // mod veto). Rides the no-side-effect REJECTED family.
                ParsekLog.Warn(Tag, "evaexit refused reason=eva-refused (spawnEVA returned null)");
                SetExecResult("REJECTED", null, "eva-refused");
                return;
            }

            lastEvaExitFromPid = fromPid;
            evaExitKerbalName = resolved;
            evaExitVessel = evaCtl.vessel;
            evaExitReleaseRequested = releaseRequested;
            evaExitReleaseApplied = false;
            evaExitSettleSeconds = settleSeconds;
            evaExitSettleStartedAt = -1.0;
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEvaExit(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("EvaExit");

            bool exists = evaExitVessel != null;
            bool active = exists && ReferenceEquals(FlightGlobals.ActiveVessel, evaExitVessel);
            bool settled = settleCounter == 0 && !sceneTransitioning;

            // Apply the ladder release once the EVA vessel is active (F: not gated on ground
            // contact - the kerbal may complete mid-fall).
            if (evaExitReleaseRequested && !evaExitReleaseApplied && active)
                ApplyLadderRelease();

            bool releaseSatisfied = !evaExitReleaseRequested || evaExitReleaseApplied;
            bool baseConjuncts = exists && active && settled && releaseSatisfied;

            bool settleElapsed;
            if (evaExitSettleSeconds <= 0.0)
            {
                settleElapsed = true;
            }
            else
            {
                if (baseConjuncts && evaExitSettleStartedAt < 0.0)
                    evaExitSettleStartedAt = now;
                settleElapsed = baseConjuncts && evaExitSettleStartedAt >= 0.0
                    && (now - evaExitSettleStartedAt) >= evaExitSettleSeconds;
            }

            EvaExitCompletionDecision decision = TestCommandEvaExit.DecideEvaExitCompletion(
                elapsed, exists, active, settled, evaExitReleaseRequested, evaExitReleaseApplied,
                settleElapsed, budget);
            if (decision == EvaExitCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string kerbal = evaExitKerbalName;
            uint evaPid = exists ? evaExitVessel.persistentId : 0u;
            bool released = evaExitReleaseApplied;
            ClearTwoPhase();

            if (decision == EvaExitCompletionDecision.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandEvaExit.BuildCompletePayload(kerbal, evaPid, released);
                ParsekLog.Info(Tag, $"evaexit complete kerbal={kerbal ?? string.Empty} evaPid={evaPid.ToString(CultureInfo.InvariantCulture)} released={Bool(released)}");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
            }
            else // ExitTimeout
            {
                TestCommandDiagnostics.Timeout(id, verb, elapsed, "eva-exit-timeout");
                ParsekLog.Error(Tag, $"evaexit timeout kerbal={kerbal ?? string.Empty} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, "eva-exit-timeout", dequeueHead: true);
            }
        }

        // Run the public fsm ladder let-go once the EVA vessel is active. A kerbal already off
        // the ladder marks releaseApplied without the event (logged release=noop).
        private void ApplyLadderRelease()
        {
            KerbalEVA evaCtl = evaExitVessel != null ? evaExitVessel.FindPartModuleImplementing<KerbalEVA>() : null;
            if (evaCtl == null)
            {
                // Nothing to release against yet; leave releaseApplied false and retry next poll.
                return;
            }

            if (evaCtl.OnALadder)
            {
                try
                {
                    evaCtl.fsm.RunEvent(evaCtl.On_ladderLetGo);
                    evaExitReleaseApplied = true;
                    ParsekLog.Info(Tag, $"evaexit release applied kerbal={evaExitKerbalName ?? string.Empty}");
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn(Tag, $"evaexit release threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                // Not on the ladder (already dropped): treat as applied without the FSM event.
                evaExitReleaseApplied = true;
                ParsekLog.Info(Tag, $"evaexit release=noop kerbal={evaExitKerbalName ?? string.Empty} (not on ladder)");
            }
        }

        // =====================================================================================
        // PlantFlag (two-phase, bounded-wait gate, irreversible, dialog-answering)
        // =====================================================================================
        private void PlantFlagImpl(ParsedCommand cmd)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA evaCtl = active != null ? active.FindPartModuleImplementing<KerbalEVA>() : null;
            if (evaCtl == null)
            {
                // Instant refusal (stably-closed cause): the active vessel is not an EVA kerbal.
                ParsekLog.Warn(Tag, "plantflag refused reason=not-eva");
                SetExecResult("REJECTED", null, "not-eva");
                return;
            }

            if (ReadFlagLockStable())
            {
                ParsekLog.Warn(Tag, "plantflag refused reason=flag-lock-stable");
                SetExecResult("REJECTED", null, "flag-lock-stable");
                return;
            }

            ParsekLog.Info(Tag, $"plantflag start kerbal={active.vesselName ?? string.Empty}");

            // Snapshot the pre-existing flag vessels so the plant's new FlagSite vessel is
            // detectable (v1 sandbox fixtures start flag-free, but be robust).
            plantFlagController = evaCtl;
            plantFlagFired = false;
            plantFlagDialogAnswered = false;
            plantFlagLastGateOpen = false;
            plantFlagVessel = null;
            plantFlagPreExistingFlagPids = new HashSet<uint>();
            foreach (Vessel v in FlightGlobals.Vessels)
                if (v != null && v.vesselType == VesselType.Flag)
                    plantFlagPreExistingFlagPids.Add(v.persistentId);

            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompletePlantFlag(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("PlantFlag");

            // Phase A: bounded-wait plant gate (F1). Poll the stock gate until it opens, then
            // fire PlantFlag() exactly once on the transition.
            if (!plantFlagFired)
            {
                bool gateOpen = ReadPlantGate(plantFlagController);
                plantFlagLastGateOpen = gateOpen;
                bool stableLockClosed = ReadFlagLockStable();
                PlantGateDecision g = TestCommandPlantFlag.DecidePlantGateWait(elapsed, gateOpen, stableLockClosed, budget);
                switch (g)
                {
                    case PlantGateDecision.KeepWaiting:
                        ParsekLog.VerboseRateLimited("TestCommands", "plantflag-gate-wait",
                            $"plantflag gate wait elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s gateOpen={Bool(gateOpen)}");
                        return;
                    case PlantGateDecision.RejectStableLock:
                    {
                        string rid = completionId; long rseq = completionSeq; string rverb = completionVerb;
                        ClearTwoPhase();
                        ParsekLog.Warn(Tag, "plantflag refused reason=flag-lock-stable");
                        EmitExecutedTerminal(rid, rseq, rverb, "REJECTED", null, "flag-lock-stable", dequeueHead: true);
                        return;
                    }
                    case PlantGateDecision.GateTimeout:
                    {
                        string tid = completionId; long tseq = completionSeq; string tverb = completionVerb;
                        ClearTwoPhase();
                        TestCommandDiagnostics.Timeout(tid, tverb, elapsed, "flag-gate-timeout");
                        ParsekLog.Error(Tag, $"plantflag failed reason=flag-gate-timeout lastGateOpen={Bool(gateOpen)} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                        EmitExecutedTerminal(tid, tseq, tverb, "ERROR", null, "flag-gate-timeout", dequeueHead: true);
                        return;
                    }
                    case PlantGateDecision.ProceedToPlant:
                        try
                        {
                            plantFlagController.PlantFlag();
                            plantFlagFired = true;
                            ParsekLog.Info(Tag, "plantflag gate open - planting");
                        }
                        catch (System.Exception ex)
                        {
                            string eid = completionId; long eseq = completionSeq; string everb = completionVerb;
                            ClearTwoPhase();
                            ParsekLog.Error(Tag, $"plantflag PlantFlag threw: {ex.GetType().Name}: {ex.Message}");
                            EmitExecutedTerminal(eid, eseq, everb, "ERROR", null, "flag-plant-threw", dequeueHead: true);
                        }
                        return; // move to the dialog phase next poll
                }
            }

            // Phase B: dialog answer + completion. The FSM spawns the FlagSite vessel and opens
            // the "SiteRename" popup; answer it via the dismiss button's own callback (the
            // afterFlagPlanted fires synchronously in that callback -> Parsek captures the
            // FlagEvent). dialogAnswered is set ONLY when we actually invoke the button (never
            // inferred from popup absence), except the documented external-answer edge case.
            Vessel flagVessel = FindNewFlagVessel();
            if (flagVessel != null) plantFlagVessel = flagVessel;
            bool flagVesselExists = plantFlagVessel != null;

            if (!plantFlagDialogAnswered)
            {
                PopupDialog popup = FindSiteRenamePopup();
                if (popup != null)
                {
                    if (TryInvokeSiteRenameDismiss(popup))
                    {
                        plantFlagDialogAnswered = true;
                        ParsekLog.Info(Tag, $"plantflag dialog answered site={(plantFlagVessel != null ? plantFlagVessel.vesselName : string.Empty)}");
                    }
                }
                else if (flagVesselExists)
                {
                    // Edge case 10: flag vessel exists, popup gone without our invoke -> treated
                    // as answered externally (unreachable unattended).
                    plantFlagDialogAnswered = true;
                    ParsekLog.Info(Tag, "plantflag dialog-answered-externally");
                }
            }

            bool settled = settleCounter == 0 && !sceneTransitioning;
            FlagPlantCompletionDecision decision = TestCommandPlantFlag.DecideFlagPlantCompletion(
                elapsed, flagVesselExists, plantFlagDialogAnswered, settled, budget);
            if (decision == FlagPlantCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            Vessel fv = plantFlagVessel;
            ClearTwoPhase();

            if (decision == FlagPlantCompletionDecision.CompleteOk)
            {
                string site = fv != null ? fv.vesselName : string.Empty;
                string body = fv != null && fv.mainBody != null ? fv.mainBody.bodyName : string.Empty;
                double lat = fv != null ? fv.latitude : 0.0;
                double lon = fv != null ? fv.longitude : 0.0;
                List<KeyValuePair<string, string>> payload =
                    TestCommandPlantFlag.BuildCompletePayload(site, body, lat, lon);
                ParsekLog.Info(Tag, $"plantflag complete flagSite={site} body={body}");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
            }
            else // FlagTimeout
            {
                TestCommandDiagnostics.Timeout(id, verb, elapsed, "flag-timeout");
                ParsekLog.Error(Tag, $"plantflag failed reason=flag-timeout elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, "flag-timeout", dequeueHead: true);
            }
        }

        // Read the stock plant gate (Events["PlantFlag"].active), which the FSM keeps equal to
        // CanPlantFlag() (active vessel + part ground contact + flagItems>0 + not ragdoll + AC
        // flag unlock + not in construction mode). Null-safe.
        private static bool ReadPlantGate(KerbalEVA evaCtl)
        {
            if (evaCtl == null) return false;
            try
            {
                BaseEvent plantEvent = evaCtl.Events?["PlantFlag"];
                return plantEvent != null && plantEvent.active;
            }
            catch { return false; }
        }

        // The AC flag unlock (GameVariables.UnlockedEVAFlags at the AC level) is the ONE
        // stably-closed cause of a refused plant that cannot flip mid-mission. Read defensively
        // by reflection so there is no compile-time coupling to the exact API: if the read is
        // unavailable we treat the flag as UNLOCKED (not stably closed) so a sandbox mission
        // (flags always unlocked) never falsely rejects; a stably-closed AC lock in a career
        // fixture surfaces the reject.
        private static bool ReadFlagLockStable()
        {
            try
            {
                GameVariables gv = GameVariables.Instance;
                if (gv == null) return false;
                MethodInfo levelMethod = typeof(ScenarioUpgradeableFacilities).GetMethod(
                    "GetFacilityLevel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(SpaceCenterFacility) }, null);
                MethodInfo unlockMethod = typeof(GameVariables).GetMethod(
                    "UnlockedEVAFlags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(float) }, null);
                if (levelMethod == null || unlockMethod == null) return false;
                object levelObj = levelMethod.Invoke(null, new object[] { SpaceCenterFacility.AstronautComplex });
                if (!(levelObj is float acLevel)) return false;
                object unlockObj = unlockMethod.Invoke(gv, new object[] { acLevel });
                return unlockObj is bool unlocked && !unlocked;
            }
            catch { return false; }
        }

        // Detect the newly spawned FlagSite vessel (a VesselType.Flag not present before the plant).
        private Vessel FindNewFlagVessel()
        {
            if (plantFlagPreExistingFlagPids == null) return null;
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || v.vesselType != VesselType.Flag) continue;
                if (!plantFlagPreExistingFlagPids.Contains(v.persistentId)) return v;
            }
            return null;
        }

        // Locate the live "SiteRename" popup by name (mirrors FindReFlyMergePopup). Returns null
        // when no such popup is live or the reflection bind failed.
        private static PopupDialog FindSiteRenamePopup()
        {
            if (PopupDialogToDisplayField == null || MultiOptionDialogNameField == null)
                return null;
            PopupDialog[] popups = UnityEngine.Object.FindObjectsOfType<PopupDialog>();
            if (popups == null) return null;
            for (int i = 0; i < popups.Length; i++)
            {
                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popups[i]) as MultiOptionDialog;
                if (dialog == null) continue;
                string name = MultiOptionDialogNameField.GetValue(dialog) as string;
                if (name == "SiteRename") return popups[i];
            }
            return null;
        }

        // Invoke the DISMISS button's OWN callback (the deterministic default site name path;
        // accept is gated on a typed-in name the seam does not supply). The dismiss button is
        // the LAST option (mirroring the merge dialog's discard = last-button convention). The
        // afterFlagPlanted event fires inside that callback, synchronously in this frame.
        private bool TryInvokeSiteRenameDismiss(PopupDialog popup)
        {
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            if (buttons.Count == 0 || DialogGuiButtonOptionSelectedMethod == null)
                return false;
            try
            {
                DialogGuiButtonOptionSelectedMethod.Invoke(buttons[buttons.Count - 1], null);
                return true;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, $"plantflag dismiss-invoke threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // =====================================================================================
        // EvaBoard (two-phase, irreversible)
        // =====================================================================================
        private void EvaBoardImpl(ParsedCommand cmd)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA evaCtl = active != null ? active.FindPartModuleImplementing<KerbalEVA>() : null;
            if (evaCtl == null)
            {
                ParsekLog.Warn(Tag, "evaboard refused reason=not-eva");
                SetExecResult("REJECTED", null, "not-eva");
                return;
            }

            // Resolve the target vessel: explicit targetPid, else the last EvaExit source if
            // still loaded (F9), else the nearest loaded non-EVA vessel.
            string targetArg = ArgOrNull(cmd, "targetPid");
            Vessel target = ResolveBoardTarget(active, targetArg, out uint resolvedPid, out string resolveError);
            if (resolveError != null)
            {
                ParsekLog.Warn(Tag, $"evaboard refused reason={resolveError} targetPid={targetArg ?? string.Empty}");
                SetExecResult("REJECTED", null, resolveError);
                return;
            }

            // Find a boardable part (airlock + free crew capacity), nearest to the kerbal.
            Part boardPart = SelectBoardablePart(active, target, out double distance, out string partError);
            if (partError != null)
            {
                ParsekLog.Warn(Tag, $"evaboard refused reason={partError} targetPid={resolvedPid.ToString(CultureInfo.InvariantCulture)}");
                SetExecResult("REJECTED", null, partError);
                return;
            }

            if (!TestCommandEvaBoard.IsWithinBoardRange(distance))
            {
                ParsekLog.Warn(Tag, $"evaboard refused reason=not-near-target dist={distance.ToString("F2", CultureInfo.InvariantCulture)}m");
                SetExecResult("REJECTED", null, "not-near-target");
                return;
            }

            ParsekLog.Info(Tag, $"evaboard start kerbal={active.vesselName ?? string.Empty} targetPid={resolvedPid.ToString(CultureInfo.InvariantCulture)} dist={distance.ToString("F2", CultureInfo.InvariantCulture)}");

            evaBoardKerbalName = active.vesselName;
            evaBoardEvaVessel = active;
            evaBoardTargetVessel = target;
            evaBoardTargetPid = resolvedPid;

            try
            {
                // BoardPart is void and refuses via screen message only; nothing concluded here.
                evaCtl.BoardPart(boardPart);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error(Tag, $"evaboard BoardPart threw: {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, "board-threw");
                return;
            }

            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEvaBoard(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("EvaBoard");

            bool evaVesselGone = evaBoardEvaVessel == null;
            bool crewAboard = evaBoardTargetVessel != null
                && evaBoardKerbalName != null
                && evaBoardTargetVessel.GetVesselCrew().Any(c => c != null && string.Equals(c.name, evaBoardKerbalName, System.StringComparison.Ordinal));
            bool targetActive = evaBoardTargetVessel != null && ReferenceEquals(FlightGlobals.ActiveVessel, evaBoardTargetVessel);
            bool quiescent = ParsekFlight.Instance == null || ParsekFlight.Instance.BoardMergeQuiescent;
            bool settled = settleCounter == 0 && !sceneTransitioning;

            BoardCompletionDecision decision = TestCommandEvaBoard.DecideBoardCompletion(
                elapsed, evaVesselGone, crewAboard, targetActive, quiescent, settled, budget);
            if (decision == BoardCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string kerbal = evaBoardKerbalName;
            uint boardedPid = evaBoardTargetVessel != null ? evaBoardTargetVessel.persistentId : evaBoardTargetPid;
            ClearTwoPhase();

            if (decision == BoardCompletionDecision.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandEvaBoard.BuildCompletePayload(kerbal, boardedPid);
                ParsekLog.Info(Tag, $"evaboard complete kerbal={kerbal ?? string.Empty} boardedPid={boardedPid.ToString(CultureInfo.InvariantCulture)}");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
            }
            else // BoardTimeout
            {
                TestCommandDiagnostics.Timeout(id, verb, elapsed, "board-timeout");
                ParsekLog.Error(Tag, $"evaboard timeout kerbal={kerbal ?? string.Empty} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, "board-timeout", dequeueHead: true);
            }
        }

        // Resolve the board target: explicit targetPid -> that loaded vessel (unknown-target if
        // absent); no arg -> lastEvaExitFromPid if still loaded (F9), else the nearest loaded
        // non-EVA vessel.
        private Vessel ResolveBoardTarget(Vessel evaVessel, string targetArg, out uint resolvedPid, out string error)
        {
            resolvedPid = 0u;
            error = null;

            if (!string.IsNullOrEmpty(targetArg))
            {
                if (!uint.TryParse(targetArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wanted))
                {
                    error = "unknown-target";
                    return null;
                }
                Vessel found = FindLoadedVesselByPid(wanted);
                if (found == null) { error = "unknown-target"; return null; }
                resolvedPid = wanted;
                return found;
            }

            // Default: the last EvaExit source if still loaded.
            if (lastEvaExitFromPid != 0u)
            {
                Vessel prior = FindLoadedVesselByPid(lastEvaExitFromPid);
                if (prior != null) { resolvedPid = lastEvaExitFromPid; return prior; }
            }

            // Else the nearest loaded non-EVA vessel.
            Vessel nearest = null;
            double best = double.MaxValue;
            Vector3d evaPos = evaVessel != null ? evaVessel.GetWorldPos3D() : Vector3d.zero;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.isEVA || ReferenceEquals(v, evaVessel)) continue;
                double d = (v.GetWorldPos3D() - evaPos).magnitude;
                if (d < best) { best = d; nearest = v; }
            }
            if (nearest == null) { error = "unknown-target"; return null; }
            resolvedPid = nearest.persistentId;
            return nearest;
        }

        private static Vessel FindLoadedVesselByPid(uint pid)
        {
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
                if (v != null && v.persistentId == pid) return v;
            return null;
        }

        // Select the nearest boardable part (airlock + free crew capacity). Distinguishes
        // no-boardable-part (no crewable-with-airlock part at all) from target-full (crewable
        // parts exist but every one is at capacity).
        private static Part SelectBoardablePart(Vessel evaVessel, Vessel target, out double distance, out string error)
        {
            distance = double.MaxValue;
            error = null;
            if (target == null || target.parts == null)
            {
                error = "no-boardable-part";
                return null;
            }

            Vector3d evaPos = evaVessel != null ? evaVessel.GetWorldPos3D() : Vector3d.zero;
            Part best = null;
            bool anyCrewableWithAirlock = false;
            bool anyFreeCapacity = false;

            foreach (Part part in target.parts)
            {
                if (part == null || part.CrewCapacity <= 0) continue;
                Transform airlock = PartAirlockField != null ? PartAirlockField.GetValue(part) as Transform : null;
                if (airlock == null) continue;
                anyCrewableWithAirlock = true;

                int aboard = part.protoModuleCrew != null ? part.protoModuleCrew.Count : 0;
                if (aboard >= part.CrewCapacity) continue;
                anyFreeCapacity = true;

                double d = (((Vector3d)part.transform.position) - evaPos).magnitude;
                if (d < distance) { distance = d; best = part; }
            }

            if (best == null)
            {
                error = anyCrewableWithAirlock && !anyFreeCapacity ? "target-full" : "no-boardable-part";
                return null;
            }
            return best;
        }

        // The EvaExit dispatch readiness bit: FlightEVA.fetch != null (the FLIGHT-scene
        // singleton has settled in). Read only by the EvaExit defer; false-safe when the
        // FlightEVA reflection handles failed to bind.
        private static bool IsFlightEvaPresent()
        {
            if (FlightEvaFetchField == null) return false;
            try { return FlightEvaFetchField.GetValue(null) != null; }
            catch { return false; }
        }

        // Parse the optional settleSeconds arg; a missing / unparseable value defaults to 0
        // (the Parsek-agnostic default; opt-in per mission).
        private static double ParseSettleSeconds(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return 0.0;
            if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0.0)
                return v;
            return 0.0;
        }
    }
}
