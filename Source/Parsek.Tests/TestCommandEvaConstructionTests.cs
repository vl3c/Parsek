using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Parsek.TestCommands;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the <c>EvaGroundScience action=attach|detach</c> construction seam (lane
    /// EVA-11): the pure decision core, the stock members the applier reaches by reflection,
    /// and the stock IL the armed-frame press swaps (each read must exist exactly once, or
    /// the swap would replace an unknown set of calls). The internal enums cannot appear in a
    /// public theory signature, so the cells enumerate inside facts.
    /// </summary>
    public class TestCommandEvaConstructionTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ---------------------------------------------------------------- parse

        [Fact]
        public void TryParseAction_AcceptsAttachAndDetach()
        {
            Assert.True(TestCommandEvaGroundScience.TryParseAction("attach", out var a));
            Assert.Equal(EvaGroundScienceAction.Attach, a);
            Assert.True(TestCommandEvaGroundScience.TryParseAction("detach", out a));
            Assert.Equal(EvaGroundScienceAction.Detach, a);
            Assert.False(TestCommandEvaGroundScience.TryParseAction("Attach", out _));
            Assert.False(TestCommandEvaGroundScience.TryParseAction("construct", out _));
            Assert.Equal("attach", TestCommandEvaGroundScience.ActionToken(EvaGroundScienceAction.Attach));
            Assert.Equal("detach", TestCommandEvaGroundScience.ActionToken(EvaGroundScienceAction.Detach));
        }

        [Fact]
        public void TryValidateAttachArgs_NamesTheMissingArg()
        {
            Assert.True(TestCommandEvaConstruction.TryValidateAttachArgs("batteryBankMini", "dockingPort2", "top", out string e));
            Assert.Null(e);
            Assert.False(TestCommandEvaConstruction.TryValidateAttachArgs(null, "dockingPort2", "top", out e));
            Assert.Equal("missing-part", e);
            Assert.False(TestCommandEvaConstruction.TryValidateAttachArgs("-", "dockingPort2", "top", out e));
            Assert.Equal("missing-part", e);
            Assert.False(TestCommandEvaConstruction.TryValidateAttachArgs("batteryBankMini", "", "top", out e));
            Assert.Equal("attach-target-missing", e);
            Assert.False(TestCommandEvaConstruction.TryValidateAttachArgs("batteryBankMini", "dockingPort2", null, out e));
            Assert.Equal("attach-node-missing", e);
        }

        [Fact]
        public void TryValidateDetachArgs_RequiresATarget()
        {
            Assert.True(TestCommandEvaConstruction.TryValidateDetachArgs("solarPanels5", out string e));
            Assert.Null(e);
            Assert.False(TestCommandEvaConstruction.TryValidateDetachArgs(null, out e));
            Assert.Equal("detach-target-missing", e);
            Assert.False(TestCommandEvaConstruction.TryValidateDetachArgs("", out e));
            Assert.Equal("detach-target-missing", e);
        }

        [Fact]
        public void TryParseDetachFollowUp_DefaultsToStow_AcceptsDrop_RefusesTheRest()
        {
            Assert.True(TestCommandEvaConstruction.TryParseDetachFollowUp(null, out DetachFollowUp f, out string e));
            Assert.Equal(DetachFollowUp.Stow, f);
            Assert.Null(e);
            Assert.True(TestCommandEvaConstruction.TryParseDetachFollowUp("", out f, out e));
            Assert.Equal(DetachFollowUp.Stow, f);
            Assert.True(TestCommandEvaConstruction.TryParseDetachFollowUp("stow", out f, out e));
            Assert.Equal(DetachFollowUp.Stow, f);
            Assert.True(TestCommandEvaConstruction.TryParseDetachFollowUp("Drop", out f, out e));
            Assert.Equal(DetachFollowUp.Drop, f);
            Assert.False(TestCommandEvaConstruction.TryParseDetachFollowUp("throw", out f, out e));
            Assert.Equal("detach-then-unknown", e);
            Assert.Equal(DetachFollowUp.Stow, f);
        }

        [Fact]
        public void IsDropSettled_NeedsTheHandEmptyAndOneNewSinglePartVesselOfThatPart()
        {
            var dropped = new DroppedVesselCandidate { VesselPid = 77u, PartCount = 1, FirstPartName = "solarPanels5" };
            var other = new DroppedVesselCandidate { VesselPid = 88u, PartCount = 1, FirstPartName = "batteryBankMini" };
            var wreck = new DroppedVesselCandidate { VesselPid = 99u, PartCount = 3, FirstPartName = "solarPanels5" };

            Assert.True(TestCommandEvaConstruction.IsDropSettled(false,
                new List<DroppedVesselCandidate> { other, wreck, dropped }, "solarPanels5", out uint pid));
            Assert.Equal(77u, pid);

            // Still held: stock has not dropped it yet.
            Assert.False(TestCommandEvaConstruction.IsDropSettled(true,
                new List<DroppedVesselCandidate> { dropped }, "solarPanels5", out pid));
            Assert.Equal(0u, pid);
            // No new vessel of that part (another part, or a multi-part vessel).
            Assert.False(TestCommandEvaConstruction.IsDropSettled(false,
                new List<DroppedVesselCandidate> { other, wreck }, "solarPanels5", out pid));
            Assert.False(TestCommandEvaConstruction.IsDropSettled(false,
                new List<DroppedVesselCandidate>(), "solarPanels5", out pid));
            Assert.False(TestCommandEvaConstruction.IsDropSettled(false, null, "solarPanels5", out pid));
            Assert.False(TestCommandEvaConstruction.IsDropSettled(false,
                new List<DroppedVesselCandidate> { dropped }, null, out pid));
        }

        // ---------------------------------------------------------------- held node

        [Fact]
        public void ChooseHeldNode_RequestedThenOppositeThenFirst()
        {
            var both = new List<string> { "top", "bottom" };
            Assert.Equal("bottom", TestCommandEvaConstruction.ChooseHeldNode(both, "top", null));
            Assert.Equal("top", TestCommandEvaConstruction.ChooseHeldNode(both, "bottom", null));
            Assert.Equal("top", TestCommandEvaConstruction.ChooseHeldNode(both, "side", null));
            Assert.Equal("top", TestCommandEvaConstruction.ChooseHeldNode(both, "top", "top"));
            // A requested node the held part does not have free is never swapped for another.
            Assert.Null(TestCommandEvaConstruction.ChooseHeldNode(both, "top", "left"));
            Assert.Equal("top", TestCommandEvaConstruction.ChooseHeldNode(new List<string> { "top" }, "top", null));
            Assert.Null(TestCommandEvaConstruction.ChooseHeldNode(new List<string>(), "top", null));
            Assert.Null(TestCommandEvaConstruction.ChooseHeldNode(null, "top", null));
        }

        // ---------------------------------------------------------------- check verdict

        [Fact]
        public void DecideCheck_FollowsStockOrder()
        {
            Assert.Equal(ConstructionCheckVerdict.Ready,
                TestCommandEvaConstruction.DecideCheck(true, true, true, true, true, true, 0));
            Assert.Equal(ConstructionCheckVerdict.NoHeldPart,
                TestCommandEvaConstruction.DecideCheck(false, true, true, true, true, true, 0));
            Assert.Equal(ConstructionCheckVerdict.NotPossible,
                TestCommandEvaConstruction.DecideCheck(true, false, false, false, false, false, 3));
            Assert.Equal(ConstructionCheckVerdict.WrongParent,
                TestCommandEvaConstruction.DecideCheck(true, true, false, true, true, true, 0));
            Assert.Equal(ConstructionCheckVerdict.WrongNode,
                TestCommandEvaConstruction.DecideCheck(true, true, true, false, true, true, 0));
            Assert.Equal(ConstructionCheckVerdict.WrongNode,
                TestCommandEvaConstruction.DecideCheck(true, true, true, true, false, true, 0));
            Assert.Equal(ConstructionCheckVerdict.OutOfRange,
                TestCommandEvaConstruction.DecideCheck(true, true, true, true, true, false, 0));
            Assert.Equal(ConstructionCheckVerdict.Blocked,
                TestCommandEvaConstruction.DecideCheck(true, true, true, true, true, true, 1));
        }

        [Fact]
        public void DecidePress_WaitsForReadyThenPacesAndCaps()
        {
            int need = TestCommandEvaConstruction.ReadyChecksBeforePress;
            int gap = TestCommandEvaConstruction.FramesBetweenPresses;
            int max = TestCommandEvaConstruction.MaxPresses;
            Assert.Equal(ConstructionPressDecision.Wait, TestCommandEvaConstruction.DecidePress(need - 1, 0, int.MaxValue));
            Assert.Equal(ConstructionPressDecision.Press, TestCommandEvaConstruction.DecidePress(need, 0, int.MaxValue));
            Assert.Equal(ConstructionPressDecision.Wait, TestCommandEvaConstruction.DecidePress(need, 1, gap - 1));
            Assert.Equal(ConstructionPressDecision.Press, TestCommandEvaConstruction.DecidePress(need, 1, gap));
            Assert.Equal(ConstructionPressDecision.Wait, TestCommandEvaConstruction.DecidePress(need, max, gap - 1));
            Assert.Equal(ConstructionPressDecision.GiveUp, TestCommandEvaConstruction.DecidePress(need, max, gap));
            Assert.Equal(ConstructionPressDecision.GiveUp, TestCommandEvaConstruction.DecidePress(0, max, gap));
        }

        // ---------------------------------------------------------------- detach target

        private static DetachCandidate Cand(int index, string name, double d, bool root = false, int children = 0,
            bool cargo = true, bool crew = false)
            => new DetachCandidate
            {
                Index = index, PartName = name, DistanceMeters = d, IsRoot = root,
                ChildCount = children, HasCargoModule = cargo, HasCrew = crew,
            };

        [Fact]
        public void ChooseDetachTarget_NearestDetachableInRange()
        {
            var c = new[]
            {
                Cand(0, "solarPanels5", 3.0),
                Cand(1, "solarPanels5", 2.0),
                Cand(2, "solarPanels5", 2.0),
                Cand(3, "roverWheel1", 1.0),
            };
            DetachTargetChoice ch = TestCommandEvaConstruction.ChooseDetachTarget(c, "solarPanels5", 7.0);
            Assert.Equal(DetachTargetDecision.Found, ch.Decision);
            Assert.Equal(1, ch.Candidate.Index);
            Assert.Null(TestCommandEvaConstruction.DetachRefusalReason(ch.Decision));
        }

        [Fact]
        public void ChooseDetachTarget_SkipsWhatStockRefusesToDetach()
        {
            var c = new[]
            {
                Cand(0, "solarPanels5", 1.0, root: true),
                Cand(1, "solarPanels5", 1.5, children: 1),
                Cand(2, "solarPanels5", 2.0, cargo: false),
                Cand(3, "solarPanels5", 2.5, crew: true),
                Cand(4, "solarPanels5", 6.5),
            };
            DetachTargetChoice ch = TestCommandEvaConstruction.ChooseDetachTarget(c, "solarPanels5", 7.0);
            Assert.Equal(DetachTargetDecision.Found, ch.Decision);
            Assert.Equal(4, ch.Candidate.Index);
        }

        [Fact]
        public void ChooseDetachTarget_NamesEachRefusal()
        {
            DetachTargetChoice none = TestCommandEvaConstruction.ChooseDetachTarget(
                new[] { Cand(0, "roverWheel1", 1.0) }, "solarPanels5", 7.0);
            Assert.Equal(DetachTargetDecision.NotFound, none.Decision);
            Assert.Equal("detach-target-not-found", TestCommandEvaConstruction.DetachRefusalReason(none.Decision));

            DetachTargetChoice stuck = TestCommandEvaConstruction.ChooseDetachTarget(
                new[] { Cand(0, "solarPanels5", 1.0, root: true) }, "solarPanels5", 7.0);
            Assert.Equal(DetachTargetDecision.NotDetachable, stuck.Decision);
            Assert.Equal("detach-target-not-detachable", TestCommandEvaConstruction.DetachRefusalReason(stuck.Decision));

            DetachTargetChoice far = TestCommandEvaConstruction.ChooseDetachTarget(
                new[] { Cand(0, "solarPanels5", 7.0), Cand(1, "solarPanels5", 9.0) }, "solarPanels5", 7.0);
            Assert.Equal(DetachTargetDecision.OutOfRange, far.Decision);
            Assert.Equal(7.0, far.NearestDistanceMeters);
            Assert.Equal("out-of-range", TestCommandEvaConstruction.DetachRefusalReason(far.Decision));

            Assert.Equal(DetachTargetDecision.NotFound,
                TestCommandEvaConstruction.ChooseDetachTarget(null, "solarPanels5", 7.0).Decision);
            Assert.Equal(DetachTargetDecision.NotFound,
                TestCommandEvaConstruction.ChooseDetachTarget(new[] { Cand(0, "x", 1) }, null, 7.0).Decision);
        }

        // ---------------------------------------------------------------- weld / completion

        [Fact]
        public void IsWeldSettled_WaitsAMinimumThenTheWeldThenTheCap()
        {
            int min = TestCommandEvaConstruction.MinWeldFrames;
            int cap = TestCommandEvaConstruction.WeldWaitCapFrames;
            Assert.False(TestCommandEvaConstruction.IsWeldSettled(false, min - 1, out bool capped));
            Assert.False(capped);
            Assert.True(TestCommandEvaConstruction.IsWeldSettled(false, min, out capped));
            Assert.False(capped);
            Assert.False(TestCommandEvaConstruction.IsWeldSettled(true, cap - 1, out capped));
            Assert.True(TestCommandEvaConstruction.IsWeldSettled(true, cap, out capped));
            Assert.True(capped);
            Assert.True(TestCommandEvaConstruction.IsWeldSettled(false, cap, out capped));
            Assert.False(capped);
        }

        [Fact]
        public void DecideCompletion_NeedsTheSettleWindow()
        {
            int settle = TestCommandEvaConstruction.SettleFrames;
            Assert.Equal(GroundScienceCompletionDecision.CompleteOk,
                TestCommandEvaConstruction.DecideCompletion(5, 60, true, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaConstruction.DecideCompletion(5, 60, true, settle - 1));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaConstruction.DecideCompletion(5, 60, false, settle));
            Assert.Equal(GroundScienceCompletionDecision.Timeout,
                TestCommandEvaConstruction.DecideCompletion(60, 60, false, 0));
        }

        // ---------------------------------------------------------------- log + payload

        [Fact]
        public void FormatAttachCheckLine_IsGrepStableAndInvariant()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = TestCommandEvaConstruction.FormatAttachCheckLine(3, ConstructionCheckVerdict.Blocked,
                    true, "dockingPort2", 42u, "bottom", "top", "dockingPort2", "top", "bottom", 2.345, 7.0,
                    "roverWheel1");
                Assert.Equal("evagroundscience attach check n=3 verdict=Blocked possible=true "
                    + "potentialParent=dockingPort2 potentialParentPid=42 callerNode=bottom otherNode=top "
                    + "want=dockingPort2/top heldNode=bottom attachDistance=2.35 range=7.00 blockers=roverWheel1", line);
                string empty = TestCommandEvaConstruction.FormatAttachCheckLine(1, ConstructionCheckVerdict.NoHeldPart,
                    false, null, 0u, null, null, null, null, null, double.NaN, 7.0, null);
                Assert.Contains("potentialParent=<none>", empty);
                Assert.Contains("attachDistance=nan", empty);
                Assert.Contains("blockers=<none>", empty);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Fact]
        public void BuildCompletePayload_AddsTheVesselPartCount()
        {
            List<KeyValuePair<string, string>> p = TestCommandEvaConstruction.BuildCompletePayload(
                EvaGroundScienceAction.Detach, "solarPanels5", 7u, 99u, 1, 1, 3.5, 15);
            Assert.Contains(p, kv => kv.Key == "action" && kv.Value == "detach");
            Assert.Contains(p, kv => kv.Key == "vesselParts" && kv.Value == "15");
            Assert.Equal("vesselParts", p.Last().Key);
        }

        // ---------------------------------------------------------------- IL site matching

        [Fact]
        public void CountSites_CountsEachSiteAndFlagsAnyNotReadOnce()
        {
            // UnityEngine.Input lives in a module the test project does not reference.
            Type input = AccessTools.TypeByName("UnityEngine.Input");
            Assert.NotNull(input);
            MethodInfo mouseUp = input.GetMethod("GetMouseButtonUp", new[] { typeof(int) });
            MethodInfo mouseDown = input.GetMethod("GetMouseButtonDown", new[] { typeof(int) });
            Assert.NotNull(mouseUp);
            var body = new List<KeyValuePair<OpCode, object>>
            {
                new KeyValuePair<OpCode, object>(OpCodes.Ldc_I4_0, null),
                new KeyValuePair<OpCode, object>(OpCodes.Call, mouseUp),
                new KeyValuePair<OpCode, object>(OpCodes.Call, mouseDown),
                // The right member read through the wrong opcode is not a match.
                new KeyValuePair<OpCode, object>(OpCodes.Ldftn, mouseUp),
            };
            var sites = new[] { TestCommandEvaConstruction.MouseButtonUpSite, TestCommandEvaConstruction.PointerOverGameObjectSite };
            Dictionary<string, int> counts = TestCommandEvaConstruction.CountSites(body, sites);
            Assert.Equal(1, counts["GetMouseButtonUp"]);
            Assert.Equal(0, counts["IsPointerOverGameObject"]);
            Assert.Equal("IsPointerOverGameObject", TestCommandEvaConstruction.FirstBadSite(counts, sites));
            Assert.Equal("GetMouseButtonUp:1,IsPointerOverGameObject:0",
                TestCommandEvaConstruction.FormatSiteCounts(counts, sites));

            body.Add(new KeyValuePair<OpCode, object>(OpCodes.Call, mouseUp));
            counts = TestCommandEvaConstruction.CountSites(body, new[] { TestCommandEvaConstruction.MouseButtonUpSite });
            Assert.Equal(2, counts["GetMouseButtonUp"]);
            Assert.Equal("GetMouseButtonUp",
                TestCommandEvaConstruction.FirstBadSite(counts, new[] { TestCommandEvaConstruction.MouseButtonUpSite }));
            Assert.Equal("no-sites", TestCommandEvaConstruction.FirstBadSite(null, null));
        }

        // ---------------------------------------------------------------- stock IL

        private static MethodInfo EditorMethod(string name)
        {
            MethodInfo m = AccessTools.Method(typeof(EVAConstructionModeEditor), name);
            Assert.True(m != null, "EVAConstructionModeEditor." + name + " missing from stock");
            return m;
        }

        [Fact]
        public void StockAttachInput_ReadsEverySwappedSiteExactlyOnce()
        {
            MethodInfo m = EditorMethod(EvaConstructionInputInjection.AttachInputName);
            var sites = TestCommandEvaConstruction.AttachInputSites;
            Dictionary<string, int> counts = TestCommandEvaConstruction.CountSites(PatchProcessor.ReadMethodBody(m), sites);
            Assert.Equal(1, counts["GetMouseButtonUp"]);
            Assert.True(TestCommandEvaConstruction.FirstBadSite(counts, sites) == null,
                "AttachInput site counts: " + TestCommandEvaConstruction.FormatSiteCounts(counts, sites));
        }

        [Fact]
        public void StockDetachInput_ReadsEverySwappedSiteExactlyOnce()
        {
            MethodInfo m = EditorMethod(EvaConstructionInputInjection.DetachInputName);
            var sites = TestCommandEvaConstruction.DetachInputSites;
            Dictionary<string, int> counts = TestCommandEvaConstruction.CountSites(PatchProcessor.ReadMethodBody(m), sites);
            Assert.Equal(1, counts["GetMouseButtonUp"]);
            Assert.True(TestCommandEvaConstruction.FirstBadSite(counts, sites) == null,
                "DetachInput site counts: " + TestCommandEvaConstruction.FormatSiteCounts(counts, sites));
        }

        [Fact]
        public void PreScan_PassesOnStockAndRefusesAMethodWithoutTheSites()
        {
            Assert.True(EvaConstructionInputInjection.PreScan(EditorMethod("AttachInput"),
                TestCommandEvaConstruction.AttachInputSites, out string bad, out string counts));
            Assert.Null(bad);
            Assert.Equal("GetMouseButtonUp:1,IsPointerOverGameObject:1", counts);
            // CheckAttach reads none of the swapped sites: refused, naming the first.
            Assert.False(EvaConstructionInputInjection.PreScan(EditorMethod("CheckAttach"),
                TestCommandEvaConstruction.AttachInputSites, out bad, out _));
            Assert.Equal("GetMouseButtonUp", bad);
            Assert.False(EvaConstructionInputInjection.PreScan(null,
                TestCommandEvaConstruction.AttachInputSites, out bad, out _));
            Assert.Equal("method-missing", bad);
        }

        /// <summary>
        /// The swap replaces each read with a static shim of the same stack shape: an
        /// instance read (callvirt / ldfld) becomes a call taking the instance, a static read
        /// a call taking what it took, and the shim returns what the read pushed. Driven over
        /// stock's real instructions, so a shim drifting from its read reds here, not in a
        /// flight's InvalidProgramException.
        /// </summary>
        [Fact]
        public void Swap_OverStockIl_ReplacesEachSiteWithAStackCompatibleShim()
        {
            foreach (bool attach in new[] { true, false })
            {
                MethodInfo m = EditorMethod(attach ? "AttachInput" : "DetachInput");
                var sites = attach ? TestCommandEvaConstruction.AttachInputSites : TestCommandEvaConstruction.DetachInputSites;
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(m);
                var originals = original.Select(i => new KeyValuePair<OpCode, object>(i.opcode, i.operand)).ToList();
                List<CodeInstruction> swapped = EvaConstructionInputInjection.Swap(
                    original.Select(i => i.Clone()).ToList(), sites).ToList();
                Assert.Equal(originals.Count, swapped.Count);

                int replaced = 0;
                for (int i = 0; i < swapped.Count; i++)
                {
                    EvaIlSite? hit = null;
                    foreach (EvaIlSite s in sites)
                        if (TestCommandEvaConstruction.MatchesSite(originals[i].Key, originals[i].Value, s)) hit = s;
                    if (hit == null)
                    {
                        Assert.Equal(originals[i].Key, swapped[i].opcode);
                        continue;
                    }
                    replaced++;
                    MethodInfo shim = EvaConstructionInputInjection.ShimFor(hit.Value.Name);
                    Assert.NotNull(shim);
                    Assert.True(shim.IsStatic && shim.IsPublic);
                    Assert.Equal(OpCodes.Call, swapped[i].opcode);
                    Assert.Same(shim, swapped[i].operand);

                    MemberInfo read = (MemberInfo)originals[i].Value;
                    var expectedParams = new List<Type>();
                    Type pushed;
                    if (read is FieldInfo f)
                    {
                        if (!f.IsStatic) expectedParams.Add(f.DeclaringType);
                        pushed = f.FieldType;
                    }
                    else
                    {
                        var mi = (MethodInfo)read;
                        if (!mi.IsStatic) expectedParams.Add(mi.DeclaringType);
                        expectedParams.AddRange(mi.GetParameters().Select(p => p.ParameterType));
                        pushed = mi.ReturnType;
                    }
                    Assert.Equal(expectedParams, shim.GetParameters().Select(p => p.ParameterType).ToList());
                    Assert.Equal(pushed, shim.ReturnType);
                }
                Assert.Equal(sites.Length, replaced);
            }
        }

        // ---------------------------------------------------------------- stock member pins

        [Fact]
        public void StockMembers_TheApplierReachesResolve()
        {
            Assert.Null(EvaConstructionInputInjection.MissingAttachMembers());

            Type editor = typeof(EVAConstructionModeEditor);
            Assert.Equal(typeof(bool), AccessTools.Field(editor, "attachmentPossible").FieldType);
            Assert.Equal(typeof(bool), AccessTools.Field(editor, "allowSrfAttachment").FieldType);
            Assert.Equal(typeof(Quaternion), AccessTools.Field(editor, "vesselRotation").FieldType);
            Assert.Equal(typeof(Quaternion), AccessTools.Field(editor, "selectedPartOriginalRotation").FieldType);
            Assert.Equal("Attachment", AccessTools.Field(editor, "attachment").FieldType.FullName);
            MethodInfo check = AccessTools.Method(editor, "CheckAttach");
            Assert.Equal(new[] { typeof(Part) }, check.GetParameters().Select(p => p.ParameterType).ToArray());
            Assert.Equal("Attachment", check.ReturnType.FullName);
            Assert.Empty(AccessTools.Method(editor, "UpdatePartPlacementPosition").GetParameters());
            Assert.NotNull(editor.GetProperty("SelectedPart"));

            Type attachment = AccessTools.TypeByName("Attachment");
            Assert.Equal(typeof(bool), attachment.GetField("possible").FieldType);
            Assert.Equal(typeof(Part), attachment.GetField("potentialParent").FieldType);
            Assert.Equal(typeof(AttachNode), attachment.GetField("callerPartNode").FieldType);
            Assert.Equal(typeof(AttachNode), attachment.GetField("otherPartNode").FieldType);
            Assert.Equal(typeof(Vector3), attachment.GetField("position").FieldType);
            Assert.Equal(typeof(Quaternion), attachment.GetField("rotation").FieldType);

            Type cargoColliders = AccessTools.Field(typeof(Part), "cargoColliders").FieldType;
            Assert.Equal(typeof(List<>), cargoColliders.GetGenericTypeDefinition());
            Assert.Equal("UnityEngine.Collider", cargoColliders.GetGenericArguments()[0].FullName);
            Assert.NotNull(typeof(Part).GetField("potentialParent"));
            Assert.NotNull(typeof(Part).GetField("attRotation"));
            Assert.NotNull(typeof(Part).GetField("attRotation0"));
            Assert.NotNull(typeof(Part).GetField("attPos0"));
            Assert.NotNull(typeof(Part).GetField("cargoColliders", AnyInstance));

            Type controller = typeof(EVAConstructionModeController);
            Assert.NotNull(controller.GetField("Instance", BindingFlags.Static | BindingFlags.Public));
            Assert.NotNull(controller.GetField("evaEditor"));
            Assert.NotNull(controller.GetField("panelMode"));
            Assert.NotNull(controller.GetProperty("IsOpen"));
            Assert.NotNull(controller.GetProperty("Hover"));
            Assert.NotNull(controller.GetMethod("OpenConstructionPanel", Type.EmptyTypes));
            Assert.NotNull(controller.GetMethod("ClosePanel", Type.EmptyTypes));

            Assert.NotNull(typeof(KerbalEVA).GetField("st_weld"));
            Assert.NotNull(typeof(KerbalEVA).GetField("st_weldAcquireHeading"));

            Assert.NotNull(typeof(UIPartActionInventory).GetField("slotButton"));
            Assert.NotNull(typeof(UIPartActionInventory).GetField("inventoryPartModule"));
            Assert.NotNull(typeof(UIPartActionInventorySlot).GetField("slotIndex"));
            MethodInfo click = typeof(UIPartActionInventorySlot).GetMethod("OnPointerClick");
            Assert.NotNull(click);
            Assert.Equal("UnityEngine.EventSystems.PointerEventData", click.GetParameters().Single().ParameterType.FullName);

            Assert.NotNull(typeof(ModuleInventoryPart).GetMethod("FirstEmptySlot", Type.EmptyTypes));
            Assert.NotNull(typeof(ModuleInventoryPart).GetMethod("IsSlotEmpty", new[] { typeof(int) }));
            Assert.NotNull(typeof(ModuleInventoryPart).GetField("volumeCapacityReached"));
            Assert.NotNull(typeof(ModuleInventoryPart).GetField("massCapacityReached"));
        }

        [Fact]
        public void Injection_IsNeverAnAttributedPatch()
        {
            Assert.Empty(typeof(EvaConstructionInputInjection).GetCustomAttributes(typeof(HarmonyPatch), false));
            Assert.Equal("com.parsek.testseam.evaconstruction", EvaConstructionInputInjection.HarmonyId);
        }
    }
}
