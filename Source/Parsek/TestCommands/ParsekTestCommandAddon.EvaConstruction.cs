// ParsekTestCommandAddon partial: the EvaGroundScience construction actions (lane EVA-11).
// =====================================================================================
// Thin Unity applier for `EvaGroundScience action=attach` and `action=detach`. Decisions
// live in the pure sibling TestCommandEvaConstruction (xUnit-covered); this file samples
// live KSP state and drives stock's own EVA construction mode:
//
//   ATTACH - open the construction panel (EVAConstructionModeController.OpenConstructionPanel,
//            the panel button), click the kerbal's inventory slot holding the part
//            (UIPartActionInventorySlot.OnPointerClick, the click a player makes: stock
//            builds the part and holds it), then pose the held part each frame so its
//            chosen stack node sits on the target node, run stock's own CheckAttach on that
//            pose, and press the mouse once. Stock's AttachInput then runs AttachPart and
//            fires OnEVAConstructionModePartAttached(parentVessel, part).
//   DETACH - open the panel, answer the hovered part as the target on one armed frame
//            and press the mouse once: stock's DetachInput runs CanPartBeEdited and
//            DetachPart, fires OnEVAConstructionModePartDetached(vessel, part) and holds
//            the part; then click an empty slot of the kerbal's own inventory, which stores
//            it (UIPartActionInventorySlot -> StorePartInEmptySlot).
//
// THE INJECTION is runtime-only (installed for the press, removed as soon as stock acted,
// and by ClearTwoPhase), never a [HarmonyPatch] class. Two transpilers swap each mouse /
// pointer / hover read inside AttachInput and DetachInput for a shim that answers like a
// click on the armed frame and passes the real read through on every other frame; each
// read is swapped only when the method reads it exactly once (pre-scanned with
// PatchProcessor.ReadMethodBody, refused `injection-site-count` otherwise). A postfix on
// UpdatePartPlacementPosition writes the posed attachment: it sets the private
// vesselRotation / selectedPartOriginalRotation, calls the private CheckAttach with surface
// attachment off (a mouse-ray surface hit would override the node match), and passes the
// result to AttachInput only when it names the target part and node pair, is in stock's
// range and the held part's trigger colliders touch nothing else (stock's own veto).
// Every other guard - the weight check, CanPartBeEdited, the range tests - stays stock's,
// and the seam fires no GameEvent.
//
// NEVER CLOSE THE PANEL WHILE A PART IS HELD: decompiled OnConstructionMode(false) re-attaches
// the last attached part through RestoreLastAttachedPart (firing the attach event again), and
// ClosePanel drops a held part on the ground as a new vessel. Both actions close it only with
// nothing held.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Parsek.TestCommands
{
    /// <summary>The posed-attach request the placement postfix serves, and what it saw.</summary>
    internal sealed class EvaConstructionAttachPose
    {
        internal Part Target;
        internal AttachNode TargetNode;
        internal string TargetNodeId;
        internal string HeldNodeId;
        internal int Checks;
        internal int ConsecutiveReady;
        internal ConstructionCheckVerdict LastVerdict = ConstructionCheckVerdict.NoHeldPart;
        internal bool HaveVerdict;
        internal string LastLine;
        internal string LastError;
    }

    /// <summary>
    /// The armed-frame mouse press behind <c>EvaGroundScience action=attach|detach</c>.
    /// Deliberately NOT a <c>[HarmonyPatch]</c> class: <c>ParsekHarmony</c> applies every
    /// attributed class at startup, and these patches must exist only while a seam command
    /// is pressing.
    /// </summary>
    internal static class EvaConstructionInputInjection
    {
        internal const string HarmonyId = "com.parsek.testseam.evaconstruction";
        internal const string AttachInputName = "AttachInput";
        internal const string DetachInputName = "DetachInput";
        internal const string PlacementName = "UpdatePartPlacementPosition";
        internal const string CheckAttachName = "CheckAttach";

        private static Harmony harmony;
        internal static bool Installed { get; private set; }

        /// <summary>The frame the press is live on; -1 when none is armed.</summary>
        internal static int ArmedFrame = -1;

        /// <summary>Detach only: the part the hovered-part read answers on the armed frame.</summary>
        internal static Part HoverTarget;

        /// <summary>Attach only: the pose the placement postfix holds.</summary>
        internal static EvaConstructionAttachPose Pose;

        internal static int MouseUpAnswered;
        internal static int PointerAnswered;
        internal static int PaneAnswered;
        internal static int HoverAnswered;
        internal static int HoveredPartAnswered;

        private static readonly FieldInfo AttachmentField =
            AccessTools.Field(typeof(EVAConstructionModeEditor), "attachment");
        private static readonly FieldInfo AttachmentPossibleField =
            AccessTools.Field(typeof(EVAConstructionModeEditor), "attachmentPossible");
        private static readonly FieldInfo AllowSrfAttachmentField =
            AccessTools.Field(typeof(EVAConstructionModeEditor), "allowSrfAttachment");
        private static readonly FieldInfo VesselRotationField =
            AccessTools.Field(typeof(EVAConstructionModeEditor), "vesselRotation");
        private static readonly FieldInfo OriginalRotationField =
            AccessTools.Field(typeof(EVAConstructionModeEditor), "selectedPartOriginalRotation");
        private static readonly MethodInfo CheckAttachMethod =
            AccessTools.Method(typeof(EVAConstructionModeEditor), CheckAttachName);
        private static readonly FieldInfo CargoCollidersField =
            AccessTools.Field(typeof(Part), "cargoColliders");

        // Stock's Attachment class is internal; its public fields are read by reflection.
        private static readonly Type AttachmentType = AccessTools.TypeByName("Attachment");
        private static readonly FieldInfo AttPossible = Fld("possible");
        private static readonly FieldInfo AttPotentialParent = Fld("potentialParent");
        private static readonly FieldInfo AttCallerNode = Fld("callerPartNode");
        private static readonly FieldInfo AttOtherNode = Fld("otherPartNode");
        private static readonly FieldInfo AttPosition = Fld("position");
        private static readonly FieldInfo AttRotation = Fld("rotation");

        private static FieldInfo Fld(string name)
            => AttachmentType != null ? AccessTools.Field(AttachmentType, name) : null;

        internal static MethodInfo Method(string name)
            => AccessTools.Method(typeof(EVAConstructionModeEditor), name);

        /// <summary>The members the attach pose reads or writes that did not resolve, comma
        /// separated; null when all did.</summary>
        internal static string MissingAttachMembers()
        {
            var missing = new List<string>();
            if (AttachmentField == null) missing.Add("attachment");
            if (AttachmentPossibleField == null) missing.Add("attachmentPossible");
            if (AllowSrfAttachmentField == null) missing.Add("allowSrfAttachment");
            if (VesselRotationField == null) missing.Add("vesselRotation");
            if (OriginalRotationField == null) missing.Add("selectedPartOriginalRotation");
            if (CheckAttachMethod == null) missing.Add(CheckAttachName);
            if (CargoCollidersField == null) missing.Add("Part.cargoColliders");
            if (AttachmentType == null) missing.Add("Attachment");
            if (AttPossible == null || AttPotentialParent == null || AttCallerNode == null
                || AttOtherNode == null || AttPosition == null || AttRotation == null)
                missing.Add("Attachment.fields");
            if (Method(PlacementName) == null) missing.Add(PlacementName);
            return missing.Count == 0 ? null : string.Join(",", missing.ToArray());
        }

        /// <summary>Reads <paramref name="method"/>'s IL and checks every site is read
        /// exactly once. False with <paramref name="badSite"/> (and the counts) otherwise.</summary>
        internal static bool PreScan(MethodInfo method, IList<EvaIlSite> sites,
            out string badSite, out string counts)
        {
            badSite = null;
            counts = "<unread>";
            if (method == null)
            {
                badSite = "method-missing";
                return false;
            }
            Dictionary<string, int> c = TestCommandEvaConstruction.CountSites(
                PatchProcessor.ReadMethodBody(method), sites);
            counts = TestCommandEvaConstruction.FormatSiteCounts(c, sites);
            badSite = TestCommandEvaConstruction.FirstBadSite(c, sites);
            return badSite == null;
        }

        internal static bool Install(bool attach, out string reason)
        {
            reason = null;
            if (Installed) Remove();
            MethodInfo target = Method(attach ? AttachInputName : DetachInputName);
            EvaIlSite[] sites = attach
                ? TestCommandEvaConstruction.AttachInputSites
                : TestCommandEvaConstruction.DetachInputSites;
            if (!PreScan(target, sites, out string badSite, out string counts))
            {
                reason = "injection-site-count";
                ParsekLog.Warn("TestCommands", "evagroundscience construction injection refused "
                    + "reason=" + reason + " method=" + (attach ? AttachInputName : DetachInputName)
                    + " site=" + badSite + " counts=" + counts);
                return false;
            }
            MethodInfo transpiler = typeof(EvaConstructionInputInjection).GetMethod(
                attach ? nameof(AttachTranspiler) : nameof(DetachTranspiler),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = attach
                ? typeof(EvaConstructionInputInjection).GetMethod(nameof(PlacementPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic)
                : null;
            MethodInfo placement = attach ? Method(PlacementName) : null;
            if (transpiler == null || (attach && (postfix == null || placement == null)))
            {
                reason = "injection-target-missing";
                ParsekLog.Warn("TestCommands", "evagroundscience construction injection refused "
                    + "reason=" + reason + " transpiler=" + (transpiler != null)
                    + " postfix=" + (postfix != null) + " placement=" + (placement != null));
                return false;
            }
            try
            {
                if (harmony == null) harmony = new Harmony(HarmonyId);
                harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                if (attach) harmony.Patch(placement, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                reason = "injection-patch-threw";
                ParsekLog.Warn("TestCommands", "evagroundscience construction injection patch threw: "
                    + ex.GetType().Name + ": " + ex.Message);
                try { harmony.UnpatchAll(HarmonyId); } catch (Exception) { }
                return false;
            }
            Installed = true;
            ArmedFrame = -1;
            MouseUpAnswered = 0;
            PointerAnswered = 0;
            PaneAnswered = 0;
            HoverAnswered = 0;
            HoveredPartAnswered = 0;
            ParsekLog.Info("TestCommands", "evagroundscience construction injection installed id="
                + HarmonyId + " method=" + (attach ? AttachInputName : DetachInputName)
                + " sites=" + counts + " postfix=" + (attach ? "true" : "false"));
            return true;
        }

        internal static void Remove()
        {
            ArmedFrame = -1;
            HoverTarget = null;
            Pose = null;
            if (!Installed) return;
            try
            {
                harmony.UnpatchAll(HarmonyId);
                Installed = false;
                ParsekLog.Info("TestCommands", "evagroundscience construction injection removed id="
                    + HarmonyId + " answered mouseUp=" + MouseUpAnswered.ToString(CultureInfo.InvariantCulture)
                    + " pointer=" + PointerAnswered.ToString(CultureInfo.InvariantCulture)
                    + " pane=" + PaneAnswered.ToString(CultureInfo.InvariantCulture)
                    + " hover=" + HoverAnswered.ToString(CultureInfo.InvariantCulture)
                    + " hoveredPart=" + HoveredPartAnswered.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestCommands", "evagroundscience construction injection unpatch failed: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Armed()
            => TestCommandEvaGroundScience.IsInjectedPressFrame(ArmedFrame, Time.frameCount);

        private static IEnumerable<CodeInstruction> AttachTranspiler(IEnumerable<CodeInstruction> instructions)
            => Swap(instructions, TestCommandEvaConstruction.AttachInputSites);

        private static IEnumerable<CodeInstruction> DetachTranspiler(IEnumerable<CodeInstruction> instructions)
            => Swap(instructions, TestCommandEvaConstruction.DetachInputSites);

        // Each matched read becomes a static call with the same stack shape: the instance
        // reads (callvirt / ldfld) pass the instance as the shim's one argument; the
        // static ones take what they took. The instruction object is edited in place, so
        // its labels and exception blocks stay where they were.
        internal static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions,
            IList<EvaIlSite> sites)
        {
            foreach (CodeInstruction ins in instructions)
            {
                for (int s = 0; s < sites.Count; s++)
                {
                    if (!TestCommandEvaConstruction.MatchesSite(ins.opcode, ins.operand, sites[s]))
                        continue;
                    MethodInfo shim = ShimFor(sites[s].Name);
                    if (shim != null)
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = shim;
                    }
                    break;
                }
                yield return ins;
            }
        }

        internal static MethodInfo ShimFor(string siteName)
        {
            string name;
            if (siteName == TestCommandEvaConstruction.MouseButtonUpSite.Name) name = nameof(MouseButtonUpShim);
            else if (siteName == TestCommandEvaConstruction.PointerOverGameObjectSite.Name) name = nameof(PointerOverShim);
            else if (siteName == TestCommandEvaConstruction.CursorOverPaneSite.Name) name = nameof(CursorOverPaneShim);
            else if (siteName == TestCommandEvaConstruction.PanelHoverSite.Name) name = nameof(PanelHoverShim);
            else if (siteName == TestCommandEvaConstruction.HoveredPartSite.Name) name = nameof(HoveredPartShim);
            else return null;
            return typeof(EvaConstructionInputInjection).GetMethod(name, BindingFlags.Static | BindingFlags.Public);
        }

        public static bool MouseButtonUpShim(int button)
        {
            if (button == 0 && Armed())
            {
                MouseUpAnswered++;
                return true;
            }
            return Input.GetMouseButtonUp(button);
        }

        public static bool PointerOverShim(EventSystem eventSystem)
        {
            if (Armed())
            {
                PointerAnswered++;
                return false;
            }
            return eventSystem.IsPointerOverGameObject();
        }

        public static bool CursorOverPaneShim(UIPartActionControllerInventory inventory)
        {
            if (Armed())
            {
                PaneAnswered++;
                return false;
            }
            return inventory.IsCursorOverAnyPAWOrCargoPane;
        }

        public static bool PanelHoverShim(EVAConstructionModeController controller)
        {
            if (Armed())
            {
                HoverAnswered++;
                return false;
            }
            return controller.Hover;
        }

        public static Part HoveredPartShim()
        {
            if (Armed() && HoverTarget != null)
            {
                HoveredPartAnswered++;
                return HoverTarget;
            }
            return Mouse.HoveredPart;
        }

        private static void PlacementPostfix(EVAConstructionModeEditor __instance)
        {
            EvaConstructionAttachPose pose = Pose;
            if (pose == null || __instance == null) return;
            try
            {
                PoseAndCheck(__instance, pose);
            }
            catch (Exception ex)
            {
                pose.ConsecutiveReady = 0;
                pose.LastError = ex.GetType().Name + ": " + ex.Message;
                try { AttachmentPossibleField.SetValue(__instance, false); } catch (Exception) { }
                ParsekLog.WarnRateLimited("TestCommands", "evaconstruction-pose-threw",
                    "evagroundscience attach pose threw: " + pose.LastError);
            }
        }

        private static void PoseAndCheck(EVAConstructionModeEditor editor, EvaConstructionAttachPose pose)
        {
            pose.Checks++;
            Part held = editor.SelectedPart;
            Part target = pose.Target;
            AttachNode targetNode = pose.TargetNode;
            AttachNode heldNode = held != null ? held.FindAttachNode(pose.HeldNodeId) : null;
            Vessel active = FlightGlobals.ActiveVessel;
            if (held == null || target == null || targetNode == null || heldNode == null || active == null)
            {
                AttachmentPossibleField.SetValue(editor, false);
                Record(pose, held == null ? ConstructionCheckVerdict.NoHeldPart : ConstructionCheckVerdict.WrongNode,
                    false, null, 0u, null, null, double.NaN, "<none>");
                return;
            }

            // Turn the held part so its node faces the target node, then slide it so the two
            // node points meet. vesselRotation is what CheckAttach composes its rotation from
            // and what stock re-poses the part with next frame.
            Vector3 targetDir = target.transform.TransformDirection(targetNode.orientation).normalized;
            Vector3 targetPoint = target.transform.TransformPoint(targetNode.position + targetNode.offset);
            Vector3 heldDir = held.transform.TransformDirection(heldNode.orientation).normalized;
            Quaternion partRotation = Quaternion.FromToRotation(heldDir, -targetDir) * held.transform.rotation;
            Quaternion vesselRotation = partRotation * Quaternion.Inverse(held.attRotation);
            VesselRotationField.SetValue(editor, vesselRotation);
            OriginalRotationField.SetValue(editor, vesselRotation);
            held.transform.rotation = vesselRotation * held.attRotation;
            held.transform.position += targetPoint - held.transform.TransformPoint(heldNode.position);

            object attachment;
            bool allowSrf = (bool)AllowSrfAttachmentField.GetValue(editor);
            AllowSrfAttachmentField.SetValue(editor, false);
            try
            {
                attachment = CheckAttachMethod.Invoke(editor, new object[] { held });
            }
            finally
            {
                AllowSrfAttachmentField.SetValue(editor, allowSrf);
            }
            bool possible = attachment != null && (bool)AttPossible.GetValue(attachment);
            Part parent = attachment != null ? AttPotentialParent.GetValue(attachment) as Part : null;
            AttachNode caller = attachment != null ? AttCallerNode.GetValue(attachment) as AttachNode : null;
            AttachNode other = attachment != null ? AttOtherNode.GetValue(attachment) as AttachNode : null;
            Vector3 position = attachment != null ? (Vector3)AttPosition.GetValue(attachment) : Vector3.zero;
            Quaternion rotation = attachment != null ? (Quaternion)AttRotation.GetValue(attachment) : Quaternion.identity;
            double distance = possible
                ? Vector3.Distance(position, active.ReferenceTransform.position)
                : double.NaN;
            bool inRange = possible && distance < GameSettings.EVA_CONSTRUCTION_RANGE;
            bool parentIsTarget = ReferenceEquals(parent, target);
            if (possible && parentIsTarget)
            {
                // Stock's own pose for a possible attachment (UpdatePartPlacementPosition).
                held.attRotation0 = rotation;
                held.attPos0 = position;
                held.transform.SetParent(null, true);
                held.transform.rotation = rotation * held.attRotation;
                held.transform.position = position;
            }

            // Stock's collider veto: any trigger contact that is not the target or the held
            // part itself (a contact with no part - terrain, a building - counts too).
            int blockers = 0;
            var blockerNames = new List<string>();
            var colliders = CargoCollidersField.GetValue(held) as List<Collider>;
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Count; i++)
                {
                    Collider c = colliders[i];
                    if (c == null) continue;
                    Part owner = FlightGlobals.GetPartUpwardsCached(c.gameObject);
                    if (owner != null && (owner.persistentId == target.persistentId
                        || owner.persistentId == held.persistentId))
                        continue;
                    blockers++;
                    if (blockerNames.Count < 4)
                        blockerNames.Add(owner != null ? owner.partInfo != null ? owner.partInfo.name : owner.name
                            : c.gameObject.name);
                }
            }

            ConstructionCheckVerdict verdict = TestCommandEvaConstruction.DecideCheck(true, possible,
                parentIsTarget, ReferenceEquals(other, targetNode), ReferenceEquals(caller, heldNode),
                inRange, blockers);
            bool ready = verdict == ConstructionCheckVerdict.Ready;
            AttachmentField.SetValue(editor, attachment);
            AttachmentPossibleField.SetValue(editor, ready);
            held.potentialParent = ready ? parent : null;
            Record(pose, verdict, possible, parent, parent != null ? parent.persistentId : 0u,
                caller != null ? caller.id : null, other != null ? other.id : null, distance,
                blockerNames.Count == 0 ? null : string.Join("+", blockerNames.ToArray()));
        }

        private static void Record(EvaConstructionAttachPose pose, ConstructionCheckVerdict verdict,
            bool possible, Part parent, uint parentPid, string callerNode, string otherNode,
            double distance, string blockers)
        {
            pose.ConsecutiveReady = verdict == ConstructionCheckVerdict.Ready ? pose.ConsecutiveReady + 1 : 0;
            string line = TestCommandEvaConstruction.FormatAttachCheckLine(pose.Checks, verdict, possible,
                parent != null && parent.partInfo != null ? parent.partInfo.name : null, parentPid,
                callerNode, otherNode, pose.Target != null && pose.Target.partInfo != null ? pose.Target.partInfo.name : null,
                pose.TargetNodeId, pose.HeldNodeId, distance, GameSettings.EVA_CONSTRUCTION_RANGE, blockers);
            pose.LastLine = line;
            // The verdict changing is logged at once; a steady verdict at most once a second.
            if (!pose.HaveVerdict || pose.LastVerdict != verdict)
                ParsekLog.Info("TestCommands", line);
            else
                ParsekLog.VerboseRateLimited("TestCommands", "evaconstruction-check", line, 1.0);
            pose.HaveVerdict = true;
            pose.LastVerdict = verdict;
        }
    }

    public partial class ParsekTestCommandAddon
    {
        private enum ConstructionPhase
        {
            OpenPanel,
            WaitHeld,
            Pressing,
            Weld,
            Stow,
            WaitStored,
            Settle,
        }

        // ----- construction state (re-armed wholesale in ResetConstructionState) -----
        private ConstructionPhase constructionPhase;
        private int constructionPhaseFrame;
        private string constructionTargetName;
        private string constructionNodeId;
        private string constructionHeldNodeRequest;
        private Part constructionTargetPart;
        private AttachNode constructionTargetNode;
        private Vessel constructionTargetVessel;
        private uint constructionTargetVesselPid;
        private int constructionPresses;
        private int constructionLastPressFrame;
        private int constructionReadyFrames;
        private int constructionDoneFrame;
        private Part constructionResultPart;
        private int constructionKerbalSlot;
        private bool constructionWeldCapped;

        private void ResetConstructionState()
        {
            constructionPhase = ConstructionPhase.OpenPanel;
            constructionPhaseFrame = Time.frameCount;
            constructionTargetName = null;
            constructionNodeId = null;
            constructionHeldNodeRequest = null;
            constructionTargetPart = null;
            constructionTargetNode = null;
            constructionTargetVessel = null;
            constructionTargetVesselPid = 0;
            constructionPresses = 0;
            constructionLastPressFrame = -1;
            constructionReadyFrames = 0;
            constructionDoneFrame = -1;
            constructionResultPart = null;
            constructionKerbalSlot = -1;
            constructionWeldCapped = false;
            EvaConstructionInputInjection.Remove();
        }

        private void EnterConstructionPhase(ConstructionPhase phase)
        {
            constructionPhase = phase;
            constructionPhaseFrame = Time.frameCount;
        }

        private int FramesInConstructionPhase() => Time.frameCount - constructionPhaseFrame;

        private static string PartName(Part p)
            => p == null ? "<none>" : p.partInfo != null ? p.partInfo.name : p.name;

        /// <summary>The checks both construction actions share before anything is opened:
        /// an Engineer on EVA (stock's own panel gate), the panel controller, the stock IL
        /// the press swaps. Refuses and returns false.</summary>
        private bool ConstructionPreconditions(Vessel kerbal, bool attach)
        {
            EVAConstructionModeController controller = EVAConstructionModeController.Instance;
            if (controller == null || controller.evaEditor == null)
            {
                ParsekLog.Warn(Tag, "evagroundscience refused reason=no-construction-controller "
                    + $"controller={Bool(controller != null)}");
                SetExecResult("REJECTED", null, "no-construction-controller");
                return false;
            }
            double skill = kerbal.VesselValues != null && kerbal.VesselValues.RepairSkill != null
                ? kerbal.VesselValues.RepairSkill.value : -1;
            if (skill < 0)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=not-engineer kerbal={kerbal.vesselName} "
                    + $"repairSkill={skill.ToString("F0", CultureInfo.InvariantCulture)}");
                SetExecResult("REJECTED", null, "not-engineer");
                return false;
            }
            if (attach)
            {
                string missing = EvaConstructionInputInjection.MissingAttachMembers();
                if (missing != null)
                {
                    ParsekLog.Warn(Tag, $"evagroundscience refused reason=stock-member-missing members={missing}");
                    SetExecResult("REJECTED", null, "stock-member-missing");
                    return false;
                }
            }
            MethodInfo input = EvaConstructionInputInjection.Method(attach
                ? EvaConstructionInputInjection.AttachInputName
                : EvaConstructionInputInjection.DetachInputName);
            if (!EvaConstructionInputInjection.PreScan(input,
                    attach ? TestCommandEvaConstruction.AttachInputSites : TestCommandEvaConstruction.DetachInputSites,
                    out string badSite, out string counts))
            {
                ParsekLog.Warn(Tag, "evagroundscience refused reason=injection-site-count method="
                    + (attach ? EvaConstructionInputInjection.AttachInputName : EvaConstructionInputInjection.DetachInputName)
                    + $" site={badSite} counts={counts}");
                SetExecResult("REJECTED", null, "injection-site-count");
                return false;
            }
            if (EventSystem.current == null)
            {
                ParsekLog.Warn(Tag, "evagroundscience refused reason=no-event-system");
                SetExecResult("REJECTED", null, "no-event-system");
                return false;
            }
            return true;
        }

        private void StartConstructionAttach(ParsedCommand cmd, Vessel kerbal, ModuleInventoryPart inv, string part)
        {
            string target = TestCommandEvaGroundScience.NormalizePartName(ArgOrNull(cmd, TestCommandEvaConstruction.TargetArg));
            string node = ArgOrNull(cmd, TestCommandEvaConstruction.NodeArg);
            if (!TestCommandEvaConstruction.TryValidateAttachArgs(part, target, node, out string argError))
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason={argError}");
                SetExecResult("REJECTED", null, argError);
                return;
            }
            if (!ConstructionPreconditions(kerbal, true)) return;
            int slot = TestCommandEvaGroundScience.FindSlotHolding(InventorySlotNames(inv), part);
            if (slot < 0)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=part-not-in-inventory part={part} "
                    + $"inventory={DescribeInventory(inv)}");
                SetExecResult("REJECTED", null, "part-not-in-inventory");
                return;
            }

            // The target: the nearest part of that name on a loaded non-EVA vessel whose
            // named node exists; refused when none, or when its node is taken.
            Part best = null;
            AttachNode bestNode = null;
            double bestDistance = double.MaxValue;
            int named = 0;
            int withNode = 0;
            Vector3 from = kerbal.transform.position;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == kerbal || v.isEVA || v.parts == null) continue;
                foreach (Part p in v.parts)
                {
                    if (p == null || PartName(p) != target) continue;
                    named++;
                    AttachNode n = p.FindAttachNode(node);
                    if (n == null) continue;
                    withNode++;
                    if (n.attachedPart != null) continue;
                    double d = DistanceToPart(p, from);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = p;
                        bestNode = n;
                    }
                }
            }
            if (best == null)
            {
                string reason = named == 0 ? "attach-target-not-found"
                    : withNode == 0 ? "attach-node-not-found" : "attach-node-taken";
                ParsekLog.Warn(Tag, $"evagroundscience refused reason={reason} target={target} node={node} "
                    + $"named={named.ToString(CultureInfo.InvariantCulture)} withNode={withNode.ToString(CultureInfo.InvariantCulture)}");
                SetExecResult("REJECTED", null, reason);
                return;
            }

            groundSciSlot = slot;
            constructionTargetName = target;
            constructionNodeId = node;
            constructionHeldNodeRequest = ArgOrNull(cmd, TestCommandEvaConstruction.HeldNodeArg);
            constructionTargetPart = best;
            constructionTargetNode = bestNode;
            constructionTargetVessel = best.vessel;
            constructionTargetVesselPid = best.vessel != null ? best.vessel.persistentId : 0u;
            groundSciDistance = bestDistance;
            ParsekLog.Info(Tag, $"evagroundscience attach start kerbal={kerbal.vesselName} part={part} "
                + $"slot={slot.ToString(CultureInfo.InvariantCulture)} inventory={DescribeInventory(inv)} "
                + $"target={target} targetPid={best.persistentId} node={node} "
                + $"vessel='{(best.vessel != null ? best.vessel.vesselName : "?")}' vesselPid={constructionTargetVesselPid} "
                + $"vesselParts={(best.vessel != null ? best.vessel.parts.Count : 0).ToString(CultureInfo.InvariantCulture)} "
                + $"distance={Fmt2(bestDistance)} range={Fmt2(GameSettings.EVA_CONSTRUCTION_RANGE)}");
            OpenConstructionPanelOrFail(kerbal);
        }

        private void StartConstructionDetach(ParsedCommand cmd, Vessel kerbal, ModuleInventoryPart inv)
        {
            string target = TestCommandEvaGroundScience.NormalizePartName(ArgOrNull(cmd, TestCommandEvaConstruction.TargetArg));
            if (!TestCommandEvaConstruction.TryValidateDetachArgs(target, out string argError))
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason={argError}");
                SetExecResult("REJECTED", null, argError);
                return;
            }
            groundSciPart = target;
            if (!ConstructionPreconditions(kerbal, false)) return;

            // Stock measures the hovered part's transform from the kerbal (DetachInput).
            Vector3 from = kerbal.GetWorldPos3D();
            var parts = new List<Part>();
            var candidates = new List<DetachCandidate>();
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == kerbal || v.isEVA || v.parts == null) continue;
                foreach (Part p in v.parts)
                {
                    if (p == null) continue;
                    candidates.Add(new DetachCandidate
                    {
                        Index = parts.Count,
                        PartName = PartName(p),
                        DistanceMeters = Vector3.Distance(p.transform.position, from),
                        IsRoot = p == v.rootPart,
                        ChildCount = p.children != null ? p.children.Count : 0,
                        HasCargoModule = p.FindModuleImplementing<ModuleCargoPart>() != null,
                        HasCrew = p.protoModuleCrew != null && p.protoModuleCrew.Count > 0,
                    });
                    parts.Add(p);
                }
            }
            DetachTargetChoice choice = TestCommandEvaConstruction.ChooseDetachTarget(
                candidates, target, GameSettings.EVA_CONSTRUCTION_RANGE);
            if (choice.Decision != DetachTargetDecision.Found)
            {
                string reason = TestCommandEvaConstruction.DetachRefusalReason(choice.Decision);
                ParsekLog.Warn(Tag, $"evagroundscience refused reason={reason} target={target} "
                    + $"nearest={(choice.NearestDistanceMeters == double.MaxValue ? "none" : Fmt2(choice.NearestDistanceMeters))} "
                    + $"range={Fmt2(GameSettings.EVA_CONSTRUCTION_RANGE)}");
                SetExecResult("REJECTED", null, reason);
                return;
            }
            int kerbalSlot = inv.FirstEmptySlot();
            if (kerbalSlot < 0)
            {
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=inventory-full inventory={DescribeInventory(inv)}");
                SetExecResult("REJECTED", null, "inventory-full");
                return;
            }
            Part chosen = parts[choice.Candidate.Index];
            constructionTargetName = target;
            constructionTargetPart = chosen;
            constructionTargetVessel = chosen.vessel;
            constructionTargetVesselPid = chosen.vessel != null ? chosen.vessel.persistentId : 0u;
            constructionKerbalSlot = kerbalSlot;
            groundSciTargetPartPid = chosen.persistentId;
            groundSciDistance = choice.Candidate.DistanceMeters;
            ParsekLog.Info(Tag, $"evagroundscience detach start kerbal={kerbal.vesselName} target={target} "
                + $"partPid={chosen.persistentId} vessel='{(chosen.vessel != null ? chosen.vessel.vesselName : "?")}' "
                + $"vesselPid={constructionTargetVesselPid} "
                + $"vesselParts={(chosen.vessel != null ? chosen.vessel.parts.Count : 0).ToString(CultureInfo.InvariantCulture)} "
                + $"distance={Fmt2(choice.Candidate.DistanceMeters)} range={Fmt2(GameSettings.EVA_CONSTRUCTION_RANGE)} "
                + $"kerbalSlot={kerbalSlot.ToString(CultureInfo.InvariantCulture)} inventory={DescribeInventory(inv)}");
            OpenConstructionPanelOrFail(kerbal);
        }

        private void OpenConstructionPanelOrFail(Vessel kerbal)
        {
            EVAConstructionModeController controller = EVAConstructionModeController.Instance;
            try
            {
                if (!controller.IsOpen) controller.OpenConstructionPanel();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"evagroundscience construction panel open threw: {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, "panel-threw");
                return;
            }
            if (!controller.IsOpen || controller.panelMode != EVAConstructionModeController.PanelMode.Construction)
            {
                // Stock's own gate (CanOpenConstructionPanel) said no; name what it reads.
                ParsekLog.Warn(Tag, $"evagroundscience refused reason=panel-refused isOpen={Bool(controller.IsOpen)} "
                    + $"panelMode={controller.panelMode} scene={HighLogic.LoadedScene} map={Bool(MapView.MapIsEnabled)} "
                    + $"isEva={Bool(kerbal.isEVA)} pause={Bool(FlightDriver.Pause)}");
                SetExecResult("REJECTED", null, "panel-refused");
                return;
            }
            ParsekLog.Info(Tag, $"evagroundscience construction panel open mode={controller.evaEditor.EVAConstructionMode} "
                + $"held={PartName(controller.evaEditor.SelectedPart)} "
                + $"softLock={Bool(InputLockManager.IsLocked(ControlTypes.EDITOR_SOFT_LOCK))}");
            EnterConstructionPhase(ConstructionPhase.OpenPanel);
            SetExecResult(PendingVerdict, null, null);
        }

        /// <summary>The kerbal's inventory slot button in the construction panel (stock builds
        /// one UIPartActionInventory per inventory in reach, SetupConstruction).</summary>
        private static UIPartActionInventorySlot FindConstructionSlot(ModuleInventoryPart inv, int slot,
            out int inventoriesSeen)
        {
            inventoriesSeen = 0;
            foreach (UIPartActionInventory ui in UnityEngine.Object.FindObjectsOfType<UIPartActionInventory>())
            {
                if (ui == null) continue;
                inventoriesSeen++;
                if (ui.inventoryPartModule != inv || ui.slotButton == null) continue;
                for (int i = 0; i < ui.slotButton.Count; i++)
                    if (ui.slotButton[i] != null && ui.slotButton[i].slotIndex == slot)
                        return ui.slotButton[i];
            }
            return null;
        }

        private bool ClickConstructionSlot(ModuleInventoryPart inv, int slot, string action, out string diag)
        {
            UIPartActionInventorySlot button = FindConstructionSlot(inv, slot, out int seen);
            diag = $"inventoriesSeen={seen.ToString(CultureInfo.InvariantCulture)} slot={slot.ToString(CultureInfo.InvariantCulture)}";
            if (button == null || EventSystem.current == null) return false;
            var click = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            button.OnPointerClick(click);
            ParsekLog.Info(Tag, $"evagroundscience {action} slot clicked {diag} "
                + $"volumeCapacityReached={Bool(inv.volumeCapacityReached)} massCapacityReached={Bool(inv.massCapacityReached)} "
                + $"held={PartName(EVAConstructionModeController.Instance != null ? EVAConstructionModeController.Instance.evaEditor.SelectedPart : null)}");
            return true;
        }

        private string ConstructionDiag()
        {
            EVAConstructionModeController c = EVAConstructionModeController.Instance;
            EVAConstructionModeEditor e = c != null ? c.evaEditor : null;
            EvaConstructionAttachPose pose = EvaConstructionInputInjection.Pose;
            return $"phase={constructionPhase} framesInPhase={FramesInConstructionPhase().ToString(CultureInfo.InvariantCulture)} "
                + $"panelOpen={Bool(c != null && c.IsOpen)} mode={(e != null ? e.EVAConstructionMode.ToString() : "?")} "
                + $"held={PartName(e != null ? e.SelectedPart : null)} presses={constructionPresses.ToString(CultureInfo.InvariantCulture)} "
                + $"softLock={Bool(InputLockManager.IsLocked(ControlTypes.EDITOR_SOFT_LOCK))} "
                + $"answered=mouseUp:{EvaConstructionInputInjection.MouseUpAnswered.ToString(CultureInfo.InvariantCulture)}"
                + $",pointer:{EvaConstructionInputInjection.PointerAnswered.ToString(CultureInfo.InvariantCulture)}"
                + $",hoveredPart:{EvaConstructionInputInjection.HoveredPartAnswered.ToString(CultureInfo.InvariantCulture)} "
                + $"lastCheck='{(pose != null ? pose.LastLine ?? "<none>" : "<none>")}' "
                + $"poseError='{(pose != null ? pose.LastError ?? "<none>" : "<none>")}'";
        }

        private void TryCompleteConstruction(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("EvaGroundScience");
            bool attach = groundSciAction == EvaGroundScienceAction.Attach;
            string verb = attach ? "attach" : "detach";
            Vessel kerbal = groundSciKerbal;
            ModuleInventoryPart inv = groundSciInventory;
            EVAConstructionModeController controller = EVAConstructionModeController.Instance;
            EVAConstructionModeEditor editor = controller != null ? controller.evaEditor : null;
            if (kerbal == null || inv == null || editor == null)
            {
                FinishConstruction("ERROR", null, "kerbal-lost", elapsed);
                return;
            }
            if (elapsed >= budget && constructionPhase != ConstructionPhase.Settle)
            {
                FinishConstruction("ERROR", null, verb + "-timeout", elapsed, ConstructionDiag());
                return;
            }
            try
            {
                if (attach) StepAttach(controller, editor, kerbal, inv, elapsed, budget);
                else StepDetach(controller, editor, kerbal, inv, elapsed, budget);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"evagroundscience {verb} threw: {ex.GetType().Name}: {ex.Message}");
                FinishConstruction("ERROR", null, verb + "-threw", elapsed, ConstructionDiag());
            }
        }

        private void StepAttach(EVAConstructionModeController controller, EVAConstructionModeEditor editor,
            Vessel kerbal, ModuleInventoryPart inv, double elapsed, double budget)
        {
            switch (constructionPhase)
            {
                case ConstructionPhase.OpenPanel:
                {
                    if (!controller.IsOpen)
                    {
                        FinishConstruction("ERROR", null, "panel-closed", elapsed, ConstructionDiag());
                        return;
                    }
                    if (editor.SelectedPart != null)
                    {
                        FinishConstruction("ERROR", null, "already-holding", elapsed, ConstructionDiag());
                        return;
                    }
                    if (ClickConstructionSlot(inv, groundSciSlot, "attach", out string diag))
                    {
                        EnterConstructionPhase(ConstructionPhase.WaitHeld);
                        return;
                    }
                    if (FramesInConstructionPhase() >= TestCommandEvaConstruction.UiWaitFrames)
                        FinishConstruction("ERROR", null, "construction-slot-missing", elapsed, diag + " " + ConstructionDiag());
                    return;
                }
                case ConstructionPhase.WaitHeld:
                {
                    Part held = editor.SelectedPart;
                    if (held == null || PartName(held) != groundSciPart)
                    {
                        if (FramesInConstructionPhase() >= TestCommandEvaConstruction.UiWaitFrames)
                            FinishConstruction("ERROR", null, "construction-pick-failed", elapsed, ConstructionDiag());
                        return;
                    }
                    var free = new List<string>();
                    if (held.attachNodes != null)
                        foreach (AttachNode n in held.attachNodes)
                            if (n != null && n.attachedPart == null && n.nodeType == AttachNode.NodeType.Stack)
                                free.Add(n.id);
                    string heldNode = TestCommandEvaConstruction.ChooseHeldNode(free, constructionNodeId,
                        constructionHeldNodeRequest);
                    ParsekLog.Info(Tag, $"evagroundscience attach held part={groundSciPart} heldPid={held.persistentId} "
                        + $"freeNodes={(free.Count == 0 ? "<none>" : string.Join(",", free.ToArray()))} "
                        + $"heldNode={heldNode ?? "<none>"} requested={constructionHeldNodeRequest ?? "<none>"}");
                    if (heldNode == null)
                    {
                        // Holding a part with no usable node: leave it held (closing the panel
                        // would drop it on the ground) and fail loudly.
                        FinishConstruction("ERROR", null, "held-node-unavailable", elapsed, ConstructionDiag());
                        return;
                    }
                    if (!EvaConstructionInputInjection.Install(true, out string reason))
                    {
                        FinishConstruction("ERROR", null, reason, elapsed, ConstructionDiag());
                        return;
                    }
                    EvaConstructionInputInjection.Pose = new EvaConstructionAttachPose
                    {
                        Target = constructionTargetPart,
                        TargetNode = constructionTargetNode,
                        TargetNodeId = constructionNodeId,
                        HeldNodeId = heldNode,
                    };
                    EnterConstructionPhase(ConstructionPhase.Pressing);
                    return;
                }
                case ConstructionPhase.Pressing:
                {
                    AttachNode node = constructionTargetNode;
                    Part attached = node != null ? node.attachedPart : null;
                    if (attached != null && PartName(attached) == groundSciPart && editor.SelectedPart == null)
                    {
                        constructionResultPart = attached;
                        constructionDoneFrame = Time.frameCount;
                        EvaConstructionAttachPose pose = EvaConstructionInputInjection.Pose;
                        string lastCheck = pose != null ? pose.LastLine : null;
                        EvaConstructionInputInjection.Remove();
                        Vessel v = constructionTargetPart != null ? constructionTargetPart.vessel : null;
                        ParsekLog.Info(Tag, $"evagroundscience attach attached part={groundSciPart} partPid={attached.persistentId} "
                            + $"target={constructionTargetName} node={constructionNodeId} vesselPid={(v != null ? v.persistentId : 0u)} "
                            + $"vesselParts={(v != null ? v.parts.Count : 0).ToString(CultureInfo.InvariantCulture)} "
                            + $"presses={constructionPresses.ToString(CultureInfo.InvariantCulture)} lastCheck='{lastCheck ?? "<none>"}'");
                        EnterConstructionPhase(ConstructionPhase.Weld);
                        return;
                    }
                    if (editor.SelectedPart == null)
                    {
                        FinishConstruction("ERROR", null, "held-part-lost", elapsed, ConstructionDiag());
                        return;
                    }
                    EvaConstructionAttachPose current = EvaConstructionInputInjection.Pose;
                    int ready = current != null ? current.ConsecutiveReady : 0;
                    int sinceLast = constructionLastPressFrame < 0 ? int.MaxValue : Time.frameCount - constructionLastPressFrame;
                    ConstructionPressDecision d = TestCommandEvaConstruction.DecidePress(ready, constructionPresses, sinceLast);
                    if (d == ConstructionPressDecision.Press)
                    {
                        constructionPresses++;
                        constructionLastPressFrame = Time.frameCount;
                        EvaConstructionInputInjection.ArmedFrame = Time.frameCount + 1;
                        ParsekLog.Info(Tag, $"evagroundscience attach press armed n={constructionPresses.ToString(CultureInfo.InvariantCulture)} "
                            + $"frame={EvaConstructionInputInjection.ArmedFrame.ToString(CultureInfo.InvariantCulture)} "
                            + $"consecutiveReady={ready.ToString(CultureInfo.InvariantCulture)} lastCheck='{(current != null ? current.LastLine : "<none>")}'");
                    }
                    else if (d == ConstructionPressDecision.GiveUp)
                    {
                        FinishConstruction("ERROR", null, "attach-not-taken", elapsed, ConstructionDiag());
                    }
                    return;
                }
                case ConstructionPhase.Weld:
                {
                    KerbalEVA eva = kerbal.FindPartModuleImplementing<KerbalEVA>();
                    bool welding = eva != null && eva.fsm != null && eva.fsm.CurrentState != null
                        && (ReferenceEquals(eva.fsm.CurrentState, eva.st_weld)
                            || ReferenceEquals(eva.fsm.CurrentState, eva.st_weldAcquireHeading));
                    int since = Time.frameCount - constructionDoneFrame;
                    if (!TestCommandEvaConstruction.IsWeldSettled(welding, since, out bool capped)) return;
                    constructionWeldCapped = capped;
                    ParsekLog.Info(Tag, $"evagroundscience attach weld settled frames={since.ToString(CultureInfo.InvariantCulture)} "
                        + $"capped={Bool(capped)} welding={Bool(welding)}");
                    CloseConstructionPanel(controller, editor, "attach");
                    EnterConstructionPhase(ConstructionPhase.Settle);
                    return;
                }
                case ConstructionPhase.Settle:
                    SettleConstruction(controller, editor, elapsed, budget,
                        constructionTargetNode != null && constructionTargetNode.attachedPart == constructionResultPart
                        && constructionResultPart != null);
                    return;
            }
        }

        private void StepDetach(EVAConstructionModeController controller, EVAConstructionModeEditor editor,
            Vessel kerbal, ModuleInventoryPart inv, double elapsed, double budget)
        {
            switch (constructionPhase)
            {
                case ConstructionPhase.OpenPanel:
                {
                    if (!controller.IsOpen)
                    {
                        FinishConstruction("ERROR", null, "panel-closed", elapsed, ConstructionDiag());
                        return;
                    }
                    if (editor.SelectedPart != null)
                    {
                        FinishConstruction("ERROR", null, "already-holding", elapsed, ConstructionDiag());
                        return;
                    }
                    if (!EvaConstructionInputInjection.Install(false, out string reason))
                    {
                        FinishConstruction("ERROR", null, reason, elapsed, ConstructionDiag());
                        return;
                    }
                    EvaConstructionInputInjection.HoverTarget = constructionTargetPart;
                    constructionReadyFrames = 0;
                    EnterConstructionPhase(ConstructionPhase.Pressing);
                    return;
                }
                case ConstructionPhase.Pressing:
                {
                    Part held = editor.SelectedPart;
                    if (held != null && ReferenceEquals(held, constructionTargetPart))
                    {
                        constructionDoneFrame = Time.frameCount;
                        EvaConstructionInputInjection.Remove();
                        Vessel v = constructionTargetVessel;
                        ParsekLog.Info(Tag, $"evagroundscience detach detached part={groundSciPart} partPid={held.persistentId} "
                            + $"vesselPid={constructionTargetVesselPid} "
                            + $"vesselParts={(v != null && v.parts != null ? v.parts.Count : 0).ToString(CultureInfo.InvariantCulture)} "
                            + $"presses={constructionPresses.ToString(CultureInfo.InvariantCulture)}");
                        EnterConstructionPhase(ConstructionPhase.Stow);
                        return;
                    }
                    if (held != null)
                    {
                        FinishConstruction("ERROR", null, "detach-held-other", elapsed, ConstructionDiag());
                        return;
                    }
                    if (constructionTargetPart == null)
                    {
                        FinishConstruction("ERROR", null, "detach-target-lost", elapsed, ConstructionDiag());
                        return;
                    }
                    constructionReadyFrames++;
                    int sinceLast = constructionLastPressFrame < 0 ? int.MaxValue : Time.frameCount - constructionLastPressFrame;
                    ConstructionPressDecision d = TestCommandEvaConstruction.DecidePress(constructionReadyFrames,
                        constructionPresses, sinceLast);
                    if (d == ConstructionPressDecision.Press)
                    {
                        constructionPresses++;
                        constructionLastPressFrame = Time.frameCount;
                        EvaConstructionInputInjection.ArmedFrame = Time.frameCount + 1;
                        Vector3 from = kerbal.GetWorldPos3D();
                        ParsekLog.Info(Tag, $"evagroundscience detach press armed n={constructionPresses.ToString(CultureInfo.InvariantCulture)} "
                            + $"frame={EvaConstructionInputInjection.ArmedFrame.ToString(CultureInfo.InvariantCulture)} "
                            + $"target={groundSciPart} partPid={constructionTargetPart.persistentId} "
                            + $"distance={Fmt2(Vector3.Distance(constructionTargetPart.transform.position, from))}");
                    }
                    else if (d == ConstructionPressDecision.GiveUp)
                    {
                        FinishConstruction("ERROR", null, "detach-not-taken", elapsed, ConstructionDiag());
                    }
                    return;
                }
                case ConstructionPhase.Stow:
                {
                    if (!inv.IsSlotEmpty(constructionKerbalSlot))
                        constructionKerbalSlot = inv.FirstEmptySlot();
                    if (constructionKerbalSlot >= 0
                        && ClickConstructionSlot(inv, constructionKerbalSlot, "detach", out string diag))
                    {
                        EnterConstructionPhase(ConstructionPhase.WaitStored);
                        return;
                    }
                    if (FramesInConstructionPhase() >= TestCommandEvaConstruction.UiWaitFrames)
                        FinishConstruction("ERROR", null, "stow-slot-missing", elapsed, ConstructionDiag());
                    return;
                }
                case ConstructionPhase.WaitStored:
                {
                    int slot = TestCommandEvaGroundScience.FindSlotHolding(InventorySlotNames(inv), groundSciPart);
                    if (editor.SelectedPart == null && slot >= 0)
                    {
                        groundSciSlot = slot;
                        ParsekLog.Info(Tag, $"evagroundscience detach stowed part={groundSciPart} slot={slot.ToString(CultureInfo.InvariantCulture)} "
                            + $"inventory={DescribeInventory(inv)}");
                        CloseConstructionPanel(controller, editor, "detach");
                        EnterConstructionPhase(ConstructionPhase.Settle);
                        return;
                    }
                    if (FramesInConstructionPhase() >= TestCommandEvaConstruction.UiWaitFrames)
                        FinishConstruction("ERROR", null, "stow-failed", elapsed,
                            $"inventory={DescribeInventory(inv)} volumeCapacityReached={Bool(inv.volumeCapacityReached)} "
                            + $"massCapacityReached={Bool(inv.massCapacityReached)} " + ConstructionDiag());
                    return;
                }
                case ConstructionPhase.Settle:
                    SettleConstruction(controller, editor, elapsed, budget,
                        TestCommandEvaGroundScience.FindSlotHolding(InventorySlotNames(inv), groundSciPart) >= 0);
                    return;
            }
        }

        private void CloseConstructionPanel(EVAConstructionModeController controller, EVAConstructionModeEditor editor,
            string action)
        {
            if (editor.SelectedPart != null)
            {
                // Never reached by the phase order; closing now would drop or re-attach it.
                ParsekLog.Warn(Tag, $"evagroundscience {action} panel left open held={PartName(editor.SelectedPart)}");
                return;
            }
            if (controller.IsOpen) controller.ClosePanel();
            ParsekLog.Info(Tag, $"evagroundscience {action} panel closed isOpen={Bool(controller.IsOpen)}");
        }

        private void SettleConstruction(EVAConstructionModeController controller, EVAConstructionModeEditor editor,
            double elapsed, double budget, bool stateHolds)
        {
            bool attach = groundSciAction == EvaGroundScienceAction.Attach;
            bool done = stateHolds && editor.SelectedPart == null && !controller.IsOpen;
            groundSciSettledFrames = done ? groundSciSettledFrames + 1 : 0;
            GroundScienceCompletionDecision d = TestCommandEvaConstruction.DecideCompletion(
                elapsed, budget, done, groundSciSettledFrames);
            if (d == GroundScienceCompletionDecision.StillWaiting) return;
            if (d == GroundScienceCompletionDecision.Timeout)
            {
                FinishConstruction("ERROR", null, (attach ? "attach" : "detach") + "-settle-timeout", elapsed,
                    $"stateHolds={Bool(stateHolds)} " + ConstructionDiag());
                return;
            }
            Vessel v = constructionTargetVessel;
            int parts = v != null && v.parts != null ? v.parts.Count : 0;
            uint partPid = attach
                ? (constructionResultPart != null ? constructionResultPart.persistentId : 0u)
                : groundSciTargetPartPid;
            ParsekLog.Info(Tag, $"evagroundscience {(attach ? "attach" : "detach")} complete part={groundSciPart} "
                + $"partPid={partPid} vesselPid={constructionTargetVesselPid} vesselParts={parts.ToString(CultureInfo.InvariantCulture)} "
                + $"presses={constructionPresses.ToString(CultureInfo.InvariantCulture)} slot={groundSciSlot.ToString(CultureInfo.InvariantCulture)} "
                + $"weldCapped={Bool(constructionWeldCapped)} inventory={DescribeInventory(groundSciInventory)}");
            FinishConstruction("OK", TestCommandEvaConstruction.BuildCompletePayload(groundSciAction, groundSciPart,
                partPid, constructionTargetVesselPid, groundSciSlot, constructionPresses, groundSciDistance, parts),
                null, elapsed);
        }

        private void FinishConstruction(string verdict, List<KeyValuePair<string, string>> payload, string msg,
            double elapsed, string diag = null)
        {
            EvaConstructionInputInjection.Remove();
            FinishGroundScience(verdict, payload, msg, elapsed, diag);
        }
    }
}
