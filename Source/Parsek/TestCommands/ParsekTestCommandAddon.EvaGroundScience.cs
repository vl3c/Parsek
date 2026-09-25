// ParsekTestCommandAddon partial: the EvaGroundScience seam-verb body (coverage wave 10).
// =====================================================================================
// Thin Unity applier for the EVA ground-science place / pick-up verb. Decisions live in the
// pure sibling TestCommandEvaGroundScience (xUnit-covered); this file samples live KSP
// state and calls the stock player entry points:
//
//   PLACE  - ModuleInventoryPart.DeployInventoryItem(slot), the method the inventory PAW
//            slot icon calls (UIPartActionInventory.StartPartPlacement), then ONE frame of
//            the EVA jump key, the binding ModuleInventoryPart.OnUpdate reads to confirm a
//            placement. The press is a Harmony prefix on KeyBinding.GetKeyDown answering
//            true for the GameSettings.EVA_Jump instance on the armed frame only (and never
//            through a held input lock); it is installed for the press and removed as soon
//            as the preview is gone. Stock then runs DeployGroundPart -> AddVessel, and the
//            new vessel's ModuleGroundSciencePart.OnStart fires onGroundSciencePartDeployed.
//   PICKUP - the ground part's own "Pick Up" KSPEvent (ModuleGroundPart.RetrievePart),
//            invoked through its BaseEvent exactly as the PAW button does. Stock fires
//            onGroundSciencePartRemoved from inside it and then kills the ground vessel.
//
// The seam fires NO GameEvent itself: the recorder witness is whatever stock fires.
// Private stock fields (selectedPart, partFullyCreated, placementonTerrain,
// placementInsideCap) are READ by reflection for the confirm gate and the timeout
// diagnostics; nothing private is written or called.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The one-frame EVA jump key press behind <c>EvaGroundScience action=place</c>.
    /// Deliberately NOT a <c>[HarmonyPatch]</c> class: <c>ParsekHarmony</c> applies every
    /// attributed class at startup, and this prefix must exist only while a seam command
    /// is pressing the key.
    /// </summary>
    internal static class EvaJumpKeyPressInjection
    {
        internal const string HarmonyId = "com.parsek.testseam.evajump";

        private static Harmony harmony;
        internal static bool Installed { get; private set; }

        /// <summary>The frame the press is live on; -1 when none is armed.</summary>
        internal static int ArmedFrame = -1;

        /// <summary>How many GetKeyDown(EVA_Jump) calls the press answered true.</summary>
        internal static int AnsweredCount;

        private static MethodInfo Target()
            => AccessTools.Method(typeof(KeyBinding), nameof(KeyBinding.GetKeyDown),
                new[] { typeof(bool) });

        internal static bool Install()
        {
            if (Installed) return true;
            MethodInfo target = Target();
            MethodInfo prefix = typeof(EvaJumpKeyPressInjection).GetMethod(nameof(Prefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null)
            {
                ParsekLog.Warn("TestCommands", "evagroundscience key injection target missing "
                    + "target=" + (target != null) + " prefix=" + (prefix != null));
                return false;
            }
            if (harmony == null) harmony = new Harmony(HarmonyId);
            harmony.Patch(target, new HarmonyMethod(prefix));
            Installed = true;
            ParsekLog.Info("TestCommands", "evagroundscience key injection installed id=" + HarmonyId);
            return true;
        }

        internal static void Remove()
        {
            ArmedFrame = -1;
            if (!Installed) return;
            try
            {
                harmony.UnpatchAll(HarmonyId);
                Installed = false;
                ParsekLog.Info("TestCommands", "evagroundscience key injection removed id=" + HarmonyId);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestCommands", "evagroundscience key injection unpatch failed: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Prefix(KeyBinding __instance, bool ignoreInputLock, ref bool __result)
        {
            if (!TestCommandEvaGroundScience.IsInjectedPressFrame(ArmedFrame, Time.frameCount))
                return true;
            if (!ReferenceEquals(__instance, GameSettings.EVA_Jump))
                return true;
            // A real key press is silent through a held input lock; so is this one.
            if (__instance.IsLocked() && !ignoreInputLock)
                return true;
            __result = true;
            AnsweredCount++;
            return false;
        }
    }

    public partial class ParsekTestCommandAddon
    {
        private static readonly FieldInfo InvSelectedPartField =
            typeof(ModuleInventoryPart).GetField("selectedPart", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InvPartFullyCreatedField =
            typeof(ModuleInventoryPart).GetField("partFullyCreated", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InvOnTerrainField =
            typeof(ModuleInventoryPart).GetField("placementonTerrain", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InvInsideCapField =
            typeof(ModuleInventoryPart).GetField("placementInsideCap", BindingFlags.Instance | BindingFlags.NonPublic);

        // ----- EvaGroundScience two-phase state (re-armed wholesale in EvaGroundScienceImpl) -----
        private EvaGroundScienceAction groundSciAction;
        private string groundSciPart;
        private Vessel groundSciKerbal;
        private ModuleInventoryPart groundSciInventory;
        private int groundSciSlot;
        private bool groundSciDeployCalled;
        private int groundSciDeployFrame;
        private int groundSciPresses;
        private int groundSciLastPressFrame;
        private bool groundSciPreviewSeen;
        private bool groundSciPreviewGone;
        private HashSet<uint> groundSciPreExistingVessels;
        private Vessel groundSciTarget;
        private uint groundSciTargetPartPid;
        private uint groundSciTargetVesselPid;
        private double groundSciDistance;
        private int groundSciSettledFrames;
        private bool groundSciFaceAway;
        private int groundSciTurnFrame;

        private void EvaGroundScienceImpl(ParsedCommand cmd)
        {
            string actionArg = ArgOrNull(cmd, TestCommandEvaGroundScience.ActionArg);
            if (!TestCommandEvaGroundScience.TryParseAction(actionArg, out EvaGroundScienceAction action))
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=bad-action action={actionArg ?? "<none>"}");
                SetExecResult("REJECTED", null, "bad-action");
                return;
            }
            string part = TestCommandEvaGroundScience.NormalizePartName(
                ArgOrNull(cmd, TestCommandEvaGroundScience.PartArg));
            if (string.IsNullOrEmpty(part))
            {
                ParsekLog.Warn(Tag, "evagroundscience refused reason=missing-part");
                SetExecResult("REJECTED", null, "missing-part");
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA evaCtl = active != null ? active.FindPartModuleImplementing<KerbalEVA>() : null;
            ModuleInventoryPart inv = active != null ? active.FindPartModuleImplementing<ModuleInventoryPart>() : null;
            if (evaCtl == null)
            {
                ParsekLog.Warn(Tag, "evagroundscience refused reason=not-eva");
                SetExecResult("REJECTED", null, "not-eva");
                return;
            }
            if (inv == null || inv.storedParts == null)
            {
                ParsekLog.Warn(Tag, "evagroundscience refused reason=no-inventory");
                SetExecResult("REJECTED", null, "no-inventory");
                return;
            }

            groundSciAction = action;
            groundSciPart = part;
            groundSciKerbal = active;
            groundSciInventory = inv;
            groundSciSlot = -1;
            groundSciDeployCalled = false;
            groundSciDeployFrame = -1;
            groundSciPresses = 0;
            groundSciLastPressFrame = -1;
            groundSciPreviewSeen = false;
            groundSciPreviewGone = false;
            groundSciPreExistingVessels = new HashSet<uint>();
            groundSciTarget = null;
            groundSciTargetPartPid = 0;
            groundSciTargetVesselPid = 0;
            groundSciDistance = 0.0;
            groundSciSettledFrames = 0;
            groundSciFaceAway = ArgOrNull(cmd, TestCommandEvaGroundScience.FaceAwayArg) == "true";
            groundSciTurnFrame = -1;
            EvaJumpKeyPressInjection.Remove();
            EvaJumpKeyPressInjection.AnsweredCount = 0;

            if (action == EvaGroundScienceAction.Place)
                StartGroundPlace(active, inv, part);
            else
                StartGroundPickup(active, inv, part);
        }

        private void StartGroundPlace(Vessel active, ModuleInventoryPart inv, string part)
        {
            int slot = TestCommandEvaGroundScience.FindSlotHolding(InventorySlotNames(inv), part);
            if (slot < 0)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=part-not-in-inventory part={part} "
                    + $"inventory={DescribeInventory(inv)}");
                SetExecResult("REJECTED", null, "part-not-in-inventory");
                return;
            }
            // InventoryItemCanBeDeployed is stock's own test (a stored ground-deployable and
            // no preview already up); a part that fails it is never deployable from this
            // slot, so this is a stable refusal rather than a wait.
            if (!inv.InventoryItemCanBeDeployed(slot))
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=not-deployable part={part} slot={slot}");
                SetExecResult("REJECTED", null, "not-deployable");
                return;
            }
            groundSciSlot = slot;
            foreach (Vessel v in FlightGlobals.Vessels)
                if (v != null) groundSciPreExistingVessels.Add(v.persistentId);
            ParsekLog.Info(Tag, $"evagroundscience place start kerbal={active.vesselName} part={part} "
                + $"slot={slot} inventory={DescribeInventory(inv)}");
            SetExecResult(PendingVerdict, null, null);
        }

        private void StartGroundPickup(Vessel active, ModuleInventoryPart inv, string part)
        {
            Part target = null;
            ModuleGroundPart targetModule = null;
            double best = double.MaxValue;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == active || v.parts == null) continue;
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null || p.partInfo == null || p.partInfo.name != part) continue;
                    ModuleGroundPart m = p.FindModuleImplementing<ModuleGroundPart>();
                    if (m == null) continue;
                    double d = Vector3d.Distance(p.transform.position, active.transform.position);
                    if (d < best)
                    {
                        best = d;
                        target = p;
                        targetModule = m;
                    }
                }
            }
            if (target == null)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=no-ground-part part={part}");
                SetExecResult("REJECTED", null, "no-ground-part");
                return;
            }
            BaseEvent retrieve = targetModule.Events != null ? targetModule.Events["RetrievePart"] : null;
            if (retrieve == null)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=no-retrieve-event part={part}");
                SetExecResult("REJECTED", null, "no-retrieve-event");
                return;
            }
            // The two conditions stock's own UpdateModuleUI shows the Pick Up button on (an
            // EVA kerbal with a free inventory slot) plus the event's unfocused range, which
            // is what stands between a player and the button.
            if (best > retrieve.unfocusedRange)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=out-of-range part={part} "
                    + $"distance={best.ToString("F2", CultureInfo.InvariantCulture)} "
                    + $"range={retrieve.unfocusedRange.ToString("F2", CultureInfo.InvariantCulture)}");
                SetExecResult("REJECTED", null, "out-of-range");
                return;
            }
            if (inv.FirstEmptySlot() < 0)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=inventory-full part={part} "
                    + $"inventory={DescribeInventory(inv)}");
                SetExecResult("REJECTED", null, "inventory-full");
                return;
            }

            groundSciTarget = target.vessel;
            groundSciTargetPartPid = target.persistentId;
            groundSciTargetVesselPid = target.vessel != null ? target.vessel.persistentId : 0u;
            groundSciDistance = best;
            ParsekLog.Info(Tag, $"evagroundscience pickup start kerbal={active.vesselName} part={part} "
                + $"partPid={groundSciTargetPartPid} vesselPid={groundSciTargetVesselPid} "
                + $"distance={best.ToString("F2", CultureInfo.InvariantCulture)}");
            try
            {
                retrieve.Invoke();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"evagroundscience pickup threw: {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, "pickup-threw");
                return;
            }
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEvaGroundScience(double now)
        {
            if (groundSciAction == EvaGroundScienceAction.Place)
                TryCompleteGroundPlace(now);
            else
                TryCompleteGroundPickup(now);
        }

        private void TryCompleteGroundPlace(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("EvaGroundScience");
            ModuleInventoryPart inv = groundSciInventory;
            Vessel kerbal = groundSciKerbal;
            if (inv == null || kerbal == null)
            {
                FinishGroundScience("ERROR", null, "kerbal-lost", elapsed);
                return;
            }

            // Phase A: the slot click, once the kerbal can place (AbleToPlaceParts is stock's
            // own gate: EVA, landed, active, no preview up). Also wait for the inventory UI
            // controller, which DeployInventoryItem touches first.
            if (!groundSciDeployCalled)
            {
                KerbalEVA evaCtl = kerbal.FindPartModuleImplementing<KerbalEVA>();
                bool able = inv.AbleToPlaceParts;
                bool uiReady = UIPartActionControllerInventory.Instance != null;
                bool standing = evaCtl != null && !evaCtl.isRagdoll && !evaCtl.OnALadder;
                if (able && uiReady && standing && groundSciFaceAway && groundSciTurnFrame < 0)
                {
                    TurnKerbalAwayFromNearestVessel(kerbal);
                    groundSciTurnFrame = Time.frameCount;
                    return;
                }
                bool turnSettled = groundSciTurnFrame < 0
                    || Time.frameCount - groundSciTurnFrame >= TestCommandEvaGroundScience.SettleFrames;
                if (able && uiReady && standing && turnSettled)
                {
                    inv.DeployInventoryItem(groundSciSlot);
                    groundSciDeployCalled = true;
                    groundSciDeployFrame = Time.frameCount;
                    ParsekLog.Info(Tag, $"evagroundscience place slot clicked part={groundSciPart} slot={groundSciSlot}");
                }
                else if (elapsed >= budget)
                {
                    FinishGroundScience("ERROR", null, "place-gate-timeout", elapsed,
                        $"able={Bool(able)} uiReady={Bool(uiReady)} standing={Bool(standing)} "
                        + $"situation={kerbal.situation}");
                }
                else
                {
                    ParsekLog.VerboseRateLimited(Tag, "evagroundscience-place-gate",
                        $"evagroundscience place gate wait able={Bool(able)} uiReady={Bool(uiReady)} "
                        + $"standing={Bool(standing)} situation={kerbal.situation}");
                }
                return;
            }

            // Phase B: the preview, then the confirm key.
            Part preview = InvSelectedPartField != null ? InvSelectedPartField.GetValue(inv) as Part : null;
            bool previewUp = preview != null;
            if (!groundSciPreviewGone)
            {
                if (previewUp) groundSciPreviewSeen = true;
                if (!previewUp && groundSciPreviewSeen)
                {
                    groundSciPreviewGone = true;
                    EvaJumpKeyPressInjection.Remove();
                    ParsekLog.Info(Tag, $"evagroundscience place confirmed part={groundSciPart} "
                        + $"presses={groundSciPresses} answered={EvaJumpKeyPressInjection.AnsweredCount}");
                }
                else if (!previewUp && Time.frameCount - groundSciDeployFrame > 120)
                {
                    // CreatePartObject aborts before building the preview when
                    // KerbalEVA.SetPartPlacementMode refuses (ragdoll, ladder, not landed).
                    FinishGroundScience("ERROR", null, "placement-mode-refused", elapsed);
                    return;
                }
                else if (previewUp)
                {
                    bool built = InvPartFullyCreatedField != null && InvPartFullyCreatedField.GetValue(inv) is bool b && b;
                    bool onTerrain = InvOnTerrainField != null && InvOnTerrainField.GetValue(inv) is bool t && t;
                    bool insideCap = InvInsideCapField != null && InvInsideCapField.GetValue(inv) is bool c && c;
                    int collisions = preview.currentCollisions != null ? preview.currentCollisions.Count : 0;
                    bool placeable = onTerrain && insideCap && collisions == 0;
                    int sincePress = groundSciLastPressFrame < 0 ? int.MaxValue : Time.frameCount - groundSciLastPressFrame;
                    GroundPlaceConfirmDecision d = TestCommandEvaGroundScience.DecideConfirm(
                        true, built, placeable, groundSciPresses, sincePress);
                    string diag = $"built={Bool(built)} onTerrain={Bool(onTerrain)} insideCap={Bool(insideCap)} "
                        + $"collisions={collisions} presses={groundSciPresses}";
                    if (d == GroundPlaceConfirmDecision.Press)
                    {
                        if (!EvaJumpKeyPressInjection.Install())
                        {
                            inv.CancelPartPlacementMode();
                            FinishGroundScience("ERROR", null, "key-injection-unavailable", elapsed);
                            return;
                        }
                        EvaJumpKeyPressInjection.ArmedFrame = Time.frameCount + 1;
                        groundSciPresses++;
                        groundSciLastPressFrame = Time.frameCount;
                        ParsekLog.Info(Tag, $"evagroundscience place press n={groundSciPresses} "
                            + $"frame={EvaJumpKeyPressInjection.ArmedFrame} {diag}");
                        return;
                    }
                    if (d == GroundPlaceConfirmDecision.GiveUp || elapsed >= budget)
                    {
                        inv.CancelPartPlacementMode();
                        FinishGroundScience("ERROR", null,
                            d == GroundPlaceConfirmDecision.GiveUp ? "placement-not-accepted" : "placement-timeout",
                            elapsed, diag);
                        return;
                    }
                    ParsekLog.VerboseRateLimited(Tag, "evagroundscience-place-preview",
                        "evagroundscience place preview wait " + diag);
                    return;
                }
                if (!groundSciPreviewGone)
                {
                    if (elapsed >= budget)
                        FinishGroundScience("ERROR", null, "preview-timeout", elapsed);
                    return;
                }
            }

            // Phase C: the stock-built ground vessel.
            Vessel placed = null;
            Part placedPart = null;
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || groundSciPreExistingVessels.Contains(v.persistentId) || v.parts == null) continue;
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null && p.partInfo != null && p.partInfo.name == groundSciPart)
                    {
                        placed = v;
                        placedPart = p;
                        break;
                    }
                }
                if (placed != null) break;
            }
            bool loaded = placed != null && placed.loaded && !placed.packed;
            bool slotCleared = inv.IsSlotEmpty(groundSciSlot);
            groundSciSettledFrames = loaded && slotCleared ? groundSciSettledFrames + 1 : 0;
            GroundScienceCompletionDecision done = TestCommandEvaGroundScience.DecidePlaceCompletion(
                elapsed, budget, groundSciPreviewGone, slotCleared, loaded, groundSciSettledFrames);
            if (done == GroundScienceCompletionDecision.StillWaiting) return;
            if (done == GroundScienceCompletionDecision.Timeout)
            {
                FinishGroundScience("ERROR", null, "placed-vessel-timeout", elapsed,
                    $"found={Bool(placed != null)} loaded={Bool(loaded)} slotCleared={Bool(slotCleared)}");
                return;
            }
            double dist = Vector3d.Distance(placedPart.transform.position, kerbal.transform.position);
            ParsekLog.Info(Tag, $"evagroundscience place complete part={groundSciPart} partPid={placedPart.persistentId} "
                + $"vesselPid={placed.persistentId} vessel={placed.vesselName} type={placed.vesselType} "
                + $"distance={dist.ToString("F2", CultureInfo.InvariantCulture)}");
            FinishGroundScience("OK", TestCommandEvaGroundScience.BuildCompletePayload(
                EvaGroundScienceAction.Place, groundSciPart, placedPart.persistentId, placed.persistentId,
                groundSciSlot, groundSciPresses, dist), null, elapsed);
        }

        private void TryCompleteGroundPickup(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("EvaGroundScience");
            ModuleInventoryPart inv = groundSciInventory;
            // A killed vessel reads Unity-null here.
            bool vesselGone = groundSciTarget == null || !FlightGlobals.Vessels.Contains(groundSciTarget);
            int slot = inv != null ? TestCommandEvaGroundScience.FindSlotHolding(InventorySlotNames(inv), groundSciPart) : -1;
            bool holds = slot >= 0;
            groundSciSettledFrames = vesselGone && holds ? groundSciSettledFrames + 1 : 0;
            GroundScienceCompletionDecision done = TestCommandEvaGroundScience.DecidePickupCompletion(
                elapsed, budget, vesselGone, holds, groundSciSettledFrames);
            if (done == GroundScienceCompletionDecision.StillWaiting) return;
            if (done == GroundScienceCompletionDecision.Timeout)
            {
                FinishGroundScience("ERROR", null, "pickup-timeout", elapsed,
                    $"vesselGone={Bool(vesselGone)} inventoryHoldsPart={Bool(holds)} "
                    + $"inventory={(inv != null ? DescribeInventory(inv) : "<none>")}");
                return;
            }
            ParsekLog.Info(Tag, $"evagroundscience pickup complete part={groundSciPart} partPid={groundSciTargetPartPid} "
                + $"vesselPid={groundSciTargetVesselPid} slot={slot}");
            FinishGroundScience("OK", TestCommandEvaGroundScience.BuildCompletePayload(
                EvaGroundScienceAction.Pickup, groundSciPart, groundSciTargetPartPid, groundSciTargetVesselPid,
                slot, 0, groundSciDistance), null, elapsed);
        }

        private void FinishGroundScience(string verdict, List<KeyValuePair<string, string>> payload,
            string msg, double elapsed, string diag = null)
        {
            EvaJumpKeyPressInjection.Remove();
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            ClearTwoPhase();
            if (verdict != "OK")
            {
                if (msg != null && msg.EndsWith("timeout", StringComparison.Ordinal))
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, msg);
                ParsekLog.Error(Tag, $"evagroundscience failed reason={msg} part={groundSciPart} "
                    + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s {diag ?? string.Empty}");
            }
            EmitExecutedTerminal(id, seq, verb, verdict, payload, msg, dequeueHead: true);
        }

        // The player's own move before placing next to a hull: turn the kerbal. Stock puts
        // the preview straight ahead of the kerbal (vesselTransform.forward * spawnDistance)
        // and refuses a spot whose terrain raycast lands on a vessel collider, and a kerbal
        // just off a ladder is facing the hull it climbed. The turn is about the local up
        // axis only, away from the nearest other loaded vessel; KerbalEVA.UpdateHeading
        // leaves an idle kerbal's heading alone, so the new facing holds.
        private void TurnKerbalAwayFromNearestVessel(Vessel kerbal)
        {
            Vessel nearest = null;
            double best = double.MaxValue;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == kerbal) continue;
                double d = Vector3d.Distance(v.transform.position, kerbal.transform.position);
                if (d < best) { best = d; nearest = v; }
            }
            if (nearest == null)
            {
                ParsekLog.Info(Tag, "evagroundscience faceaway skipped reason=no-other-vessel");
                return;
            }
            Vector3 up = (kerbal.transform.position - kerbal.mainBody.position).normalized;
            Vector3 away = Vector3.ProjectOnPlane(kerbal.transform.position - nearest.transform.position, up);
            if (away.sqrMagnitude < 1e-4f)
            {
                ParsekLog.Info(Tag, "evagroundscience faceaway skipped reason=directly-above");
                return;
            }
            Vector3 before = kerbal.transform.forward;
            kerbal.SetRotation(Quaternion.LookRotation(away.normalized, up)
                * Quaternion.Inverse(Quaternion.LookRotation(kerbal.transform.forward, kerbal.transform.up))
                * kerbal.transform.rotation);
            ParsekLog.Info(Tag, $"evagroundscience faceaway turned from={nearest.vesselName} "
                + $"distance={best.ToString("F2", CultureInfo.InvariantCulture)} "
                + $"angle={Vector3.Angle(before, away).ToString("F1", CultureInfo.InvariantCulture)}");
        }

        private static List<KeyValuePair<int, string>> InventorySlotNames(ModuleInventoryPart inv)
        {
            var slots = new List<KeyValuePair<int, string>>();
            if (inv == null || inv.storedParts == null) return slots;
            for (int slot = 0; slot < inv.InventorySlots; slot++)
            {
                if (!inv.storedParts.ContainsKey(slot)) continue;
                StoredPart sp = inv.storedParts[slot];
                if (sp == null || sp.IsEmpty) continue;
                slots.Add(new KeyValuePair<int, string>(slot, sp.partName));
            }
            return slots;
        }

        private static string DescribeInventory(ModuleInventoryPart inv)
        {
            var parts = new List<string>();
            foreach (var kv in InventorySlotNames(inv))
                parts.Add(kv.Key.ToString(CultureInfo.InvariantCulture) + ":" + kv.Value);
            return parts.Count == 0 ? "<empty>" : string.Join(",", parts.ToArray());
        }
    }
}
