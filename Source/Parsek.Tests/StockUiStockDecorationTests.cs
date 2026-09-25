using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using Parsek.Patches;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The R&amp;D and Astronaut Complex annotations on stock mechanisms
    /// (StockUiRnDDecoration.cs, StockUiAstronautDecoration.cs and their Harmony patches):
    /// every patched stock member still resolves, the pure decisions (tint, research
    /// block, row label, locked button, tooltip append), and the pairing cells - the rows
    /// the screen disables are exactly the ones the click-block patches refuse, with the
    /// same text.
    /// </summary>
    [Collection("Sequential")]
    public class StockUiStockDecorationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> dialogReasons = new List<string>();

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public StockUiStockDecorationTests()
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
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) => dialogReasons.Add(reason);
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
            ParsekLog.ResetTestOverrides();
        }

        // ---------------- target resolution: every patched stock member ----------------

        private static MethodBase Resolve(Type patch)
        {
            Assert.NotEmpty(patch.GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo helper = patch.GetMethod("ResolveTargetMethodForTesting",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(helper);
            var method = helper.Invoke(null, null) as MethodBase;
            Assert.NotNull(method);
            return method;
        }

        [Fact]
        public void Target_RDNodeUpdateGraphics_Resolves()
        {
            var m = Resolve(typeof(RnDNodeMarkPatch));
            Assert.Equal(typeof(RDNode), m.DeclaringType);
            Assert.Equal("UpdateGraphics", m.Name);
            Assert.Empty(m.GetParameters());
        }

        [Fact]
        public void Target_RDNodeGetTooltipCaption_ResolvesThePrivateStringMethod()
        {
            var m = (MethodInfo)Resolve(typeof(RnDNodeTooltipPatch));
            Assert.Equal(typeof(RDNode), m.DeclaringType);
            Assert.Equal("GetTooltipCaption", m.Name);
            Assert.True(m.IsPrivate);
            Assert.Equal(typeof(string), m.ReturnType);
            Assert.Empty(m.GetParameters());
        }

        [Fact]
        public void Target_RDControllerUpdatePanel_Resolves()
        {
            var m = Resolve(typeof(RnDPanelBlockPatch));
            Assert.Equal(typeof(RDController), m.DeclaringType);
            Assert.Equal("UpdatePanel", m.Name);
            Assert.Empty(m.GetParameters());
            // The block writes this stock member.
            Assert.Equal(typeof(UIStateButton), typeof(RDController).GetField("actionButton").FieldType);
        }

        [Fact]
        public void Target_RDControllerShowNodePanel_Resolves_AndTheDescriptionFieldExists()
        {
            var m = Resolve(typeof(RnDPanelDescriptionPatch));
            Assert.Equal(typeof(RDController), m.DeclaringType);
            Assert.Equal("ShowNodePanel", m.Name);
            Assert.Equal(new[] { typeof(RDNode) }, m.GetParameters().Select(p => p.ParameterType).ToArray());
            Assert.Equal("node", m.GetParameters()[0].Name);
            Assert.NotNull(typeof(RDController).GetField("node_description",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        }

        [Fact]
        public void Target_RDTechTreeRefreshUI_Resolves()
        {
            var m = Resolve(typeof(RnDRefreshPassLogPatch));
            Assert.Equal(typeof(RDTechTree), m.DeclaringType);
            Assert.Equal("RefreshUI", m.Name);
        }

        [Fact]
        public void Target_AstronautComplexAddItem_AllFourResolve_WithACrewParameter()
        {
            Assert.NotEmpty(typeof(AstronautComplexAddItemPatch).GetCustomAttributes(typeof(HarmonyPatch), false));
            var methods = AstronautComplexAddItemPatch.ResolveTargetMethodsForTesting();

            Assert.Equal(AstronautComplexAddItemPatch.TargetNames.OrderBy(x => x),
                methods.Select(m => m.Name).OrderBy(x => x));
            Assert.All(methods, m =>
            {
                Assert.Equal(typeof(AstronautComplex), m.DeclaringType);
                Assert.Equal(typeof(void), ((MethodInfo)m).ReturnType);
                Assert.True(AstronautComplexAddItemPatch.HasCrewParameter(m), m.Name);
                Assert.NotNull(StockUiAstronautDecoration.TabFor(m.Name));
            });
        }

        [Fact]
        public void Target_AstronautComplexUpdateCrewCounts_Resolves()
        {
            var m = Resolve(typeof(AstronautComplexCrewCountsPatch));
            Assert.Equal(typeof(AstronautComplex), m.DeclaringType);
            Assert.Equal("UpdateCrewCounts", m.Name);
        }

        [Fact]
        public void Target_TooltipControllerCrewACSetTooltip_ResolvesTheThreeArgumentOverload()
        {
            var m = Resolve(typeof(CrewTooltipReservationPatch));
            Assert.Equal(typeof(TooltipController_CrewAC), m.DeclaringType);
            var ps = m.GetParameters();
            Assert.Equal(new[] { typeof(ProtoCrewMember), typeof(string), typeof(string) },
                ps.Select(p => p.ParameterType).ToArray());
            Assert.Equal("pcm", ps[0].Name);
        }

        [Fact]
        public void Target_KerbalRosterSackAvailable_Resolves()
        {
            var m = Resolve(typeof(KerbalSackPatch));
            Assert.Equal(typeof(KerbalRoster), m.DeclaringType);
            Assert.Equal("SackAvailable", m.Name);
            Assert.Equal("ap", m.GetParameters()[0].Name);
        }

        [Fact]
        public void Target_AstronautComplexDismissButton_Resolves()
        {
            var m = Resolve(typeof(AstronautComplexDismissPatch));
            Assert.Equal(typeof(AstronautComplex), m.DeclaringType);
            Assert.Equal("Xbutton_AvailableCrew", m.Name);
            Assert.Equal("clickItem", m.GetParameters()[1].Name);
        }

        [Fact]
        public void AstronautComplexListsAndCrewRowMembers_TheAnnotationReads_Exist()
        {
            // The annotation reads the four public list properties and the row's private
            // label / tooltip controller fields.
            foreach (var prop in new[] { "ScrollListApplicants", "ScrollListAvailable", "ScrollListAssigned", "ScrollListKia" })
                Assert.NotNull(typeof(AstronautComplex).GetProperty(prop));
            Assert.NotNull(typeof(CrewListItem).GetField("label", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(typeof(CrewListItem).GetField("tooltipController", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(typeof(CrewListItem).GetMethod("SetButtonEnabled",
                new[] { typeof(bool), typeof(string), typeof(string) }));
        }

        // ---------------- R&D pure decisions ----------------

        [Fact]
        public void IconColor_Marked_TintsAtTheStockAlpha()
        {
            Color next;
            Assert.True(StockUiRnDDecoration.TryComputeIconColor(true, new Color(1f, 1f, 1f, 1f), out next));
            Assert.True(StockUiRnDDecoration.SameRgb(next, StockUiRnDDecoration.MarkTint));
            Assert.Equal(1f, next.a);

            // FADED: stock wrote alpha 0.5 and kept the RGB.
            Assert.True(StockUiRnDDecoration.TryComputeIconColor(true, new Color(1f, 1f, 1f, 0.5f), out next));
            Assert.Equal(0.5f, next.a);

            // Already tinted: nothing to write.
            Assert.False(StockUiRnDDecoration.TryComputeIconColor(true,
                new Color(StockUiRnDDecoration.MarkTint.r, StockUiRnDDecoration.MarkTint.g, StockUiRnDDecoration.MarkTint.b, 0.5f),
                out next));
        }

        [Fact]
        public void IconColor_MarkCleared_OnAFadedNode_ResetsToWhiteAtTheStockAlpha()
        {
            // Stock's FADED branch keeps the previous RGB, so a cleared mark would survive.
            var staleTint = new Color(StockUiRnDDecoration.MarkTint.r, StockUiRnDDecoration.MarkTint.g,
                StockUiRnDDecoration.MarkTint.b, 0.5f);
            Color next;
            Assert.True(StockUiRnDDecoration.TryComputeIconColor(false, staleTint, out next));
            Assert.Equal(new Color(1f, 1f, 1f, 0.5f), next);
        }

        [Fact]
        public void IconColor_Unmarked_LeavesAnyOtherStockColourAlone()
        {
            Color next;
            Assert.False(StockUiRnDDecoration.TryComputeIconColor(false, new Color(1f, 1f, 1f, 1f), out next));
            Assert.False(StockUiRnDDecoration.TryComputeIconColor(false, new Color(0.2f, 0.4f, 0.9f, 0.5f), out next));
        }

        [Theory]
        [InlineData(true, false, false, true)]   // blocked, research state: disabled
        [InlineData(true, true, false, false)]   // researched: the button is "purchase all parts"
        [InlineData(true, false, true, false)]   // FADED: stock hides the button
        [InlineData(false, false, false, false)] // not blocked
        public void ShouldDisableResearch_OnlyInTheResearchState(bool blocked, bool researched, bool faded, bool expected)
        {
            Assert.Equal(expected, StockUiRnDDecoration.ShouldDisableResearch(blocked, researched, faded));
        }

        [Fact]
        public void AppendReason_AddsOneColouredLine_AndIsIdempotent()
        {
            string once = StockUiRnDDecoration.AppendReason("<color=#fff>45 science</color>", "Researched on D5.");
            Assert.Equal("<color=#fff>45 science</color>\n<color=" + StockUiRnDDecoration.ReasonColorHex
                + ">Researched on D5.</color>", once);
            Assert.Equal(once, StockUiRnDDecoration.AppendReason(once, "Researched on D5."));
            Assert.Equal("stock", StockUiRnDDecoration.AppendReason("stock", null));
            Assert.Equal("<color=" + StockUiRnDDecoration.ReasonColorHex + ">x</color>",
                StockUiRnDDecoration.AppendReason("", "x"));
        }

        // ---------------- Astronaut Complex pure decisions ----------------

        private static StockUiDecoration Deco(StockUiDecorationKind kind, string title, string why, bool blocked = false)
        {
            return new StockUiDecoration
            {
                Screen = StockUiScreen.AstronautComplex,
                Kind = kind,
                Id = "X Kerman",
                Marked = kind != StockUiDecorationKind.None,
                Blocked = blocked,
                Title = title,
                Why = why,
                UT = double.NaN
            };
        }

        [Fact]
        public void Decide_FutureHireApplicant_LabelsAndLocksHireWithTheExplanation()
        {
            var d = Deco(StockUiDecorationKind.KerbalHire, "Hired on D5", "Hired on D5 on your committed timeline. rule", blocked: true);
            var r = StockUiAstronautDecoration.Decide(d, "Applicants", "Applicant", rowActionable: true, dismissalRefusal: null);

            Assert.Equal("Hired on D5", r.Label);
            Assert.True(r.DisableButton);
            Assert.Equal("hire", r.BlockKind);
            Assert.Equal("Hired on D5", r.DisabledTitle);
            Assert.Equal(d.Why, r.DisabledCaption);
        }

        [Fact]
        public void Decide_ApplicantOverTheCrewLimit_KeepsStocksOwnLock()
        {
            var d = Deco(StockUiDecorationKind.KerbalHire, "Hired on D5", "why", blocked: true);
            var r = StockUiAstronautDecoration.Decide(d, "Applicants", "Applicant", rowActionable: false, dismissalRefusal: null);

            Assert.Equal("Hired on D5", r.Label);
            Assert.False(r.DisableButton);
        }

        [Fact]
        public void Decide_ReservedAvailableKerbal_LocksDismissWithTheHeldExplanation()
        {
            var d = Deco(StockUiDecorationKind.KerbalOnFlight, "Reserved until D9", "Flies 'Mun'. rule. Free after D9.", blocked: true);
            var r = StockUiAstronautDecoration.Decide(d, "Available", "Available", true, "Flies 'Mun'. rule. Free after D9.");

            Assert.Equal("Reserved until D9", r.Label);
            Assert.True(r.DisableButton);
            Assert.Equal("dismiss", r.BlockKind);
            Assert.Equal("Reserved until D9", r.DisabledTitle);
            Assert.Equal("Flies 'Mun'. rule. Free after D9.", r.DisabledCaption);
        }

        [Fact]
        public void Decide_UnmarkedStandIn_LocksDismissUnderTheManagedTitle_WithoutRelabelling()
        {
            var d = Deco(StockUiDecorationKind.None, null, null, blocked: true);
            var r = StockUiAstronautDecoration.Decide(d, "Available", "Available", true,
                KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged));

            Assert.Null(r.Label);
            Assert.True(r.DisableButton);
            Assert.Equal(StockUiAstronautDecoration.DismissBlockedTitle, r.DisabledTitle);
            Assert.Contains("stand-in", r.DisabledCaption);
        }

        [Theory]
        [InlineData("KerbalLost", "Lost", "Kia")]
        [InlineData("KerbalRetiredStandIn", "Retired", "Available")]
        [InlineData("KerbalRetire", "Dismissed on D7", "Available")]
        public void Decide_InformationalKinds_ChangeTheLabelOnly(string kindName, string title, string tab)
        {
            var kind = (StockUiDecorationKind)Enum.Parse(typeof(StockUiDecorationKind), kindName);
            var d = Deco(kind, title, "why");
            var r = StockUiAstronautDecoration.Decide(d, tab, "stock", rowActionable: true, dismissalRefusal: null);

            Assert.Equal(title, r.Label);
            Assert.False(r.DisableButton);
            Assert.Null(r.BlockKind);
            Assert.False(StockUiAstronautDecoration.AppendsTooltip(kind));
        }

        [Fact]
        public void Decide_AssignedRow_AppendsTheStatusToTheStockVesselLabel()
        {
            var d = Deco(StockUiDecorationKind.KerbalOnFlight, "Reserved", "why", blocked: true);
            var r = StockUiAstronautDecoration.Decide(d, "Assigned", "Mun Lander, Pilot seat", true, "why");

            Assert.Equal("Mun Lander, Pilot seat (Reserved)", r.Label);
            // No dismiss button on an Assigned row.
            Assert.False(r.DisableButton);
        }

        [Fact]
        public void Decide_Unmarked_Unblocked_LeavesTheRowStock()
        {
            var r = StockUiAstronautDecoration.Decide(Deco(StockUiDecorationKind.None, null, null),
                "Available", "Available", true, null);
            Assert.Null(r.Label);
            Assert.False(r.DisableButton);
        }

        [Fact]
        public void AppendsTooltip_OnlyTheClickableKinds()
        {
            Assert.True(StockUiAstronautDecoration.AppendsTooltip(StockUiDecorationKind.KerbalHire));
            Assert.True(StockUiAstronautDecoration.AppendsTooltip(StockUiDecorationKind.KerbalOnFlight));
            Assert.False(StockUiAstronautDecoration.AppendsTooltip(StockUiDecorationKind.None));
        }

        [Fact]
        public void ShouldAnnotateCrewTooltip_LeavesUnreservedKerbalsStock()
        {
            // The postfix also runs for the VAB/SPH crew dialog's rows: an unmarked kerbal,
            // or one with only an informational mark, must keep stock's tooltip (no forced
            // showTooltip).
            Assert.False(StockUiAstronautDecoration.ShouldAnnotateCrewTooltip(
                Deco(StockUiDecorationKind.None, null, null)));
            Assert.False(StockUiAstronautDecoration.ShouldAnnotateCrewTooltip(
                Deco(StockUiDecorationKind.KerbalRetiredStandIn, "Retired", "Retired stand-in (Parsek)")));
            Assert.False(StockUiAstronautDecoration.ShouldAnnotateCrewTooltip(
                Deco(StockUiDecorationKind.KerbalLost, "Lost", "Lost on the committed flight 'X'.")));
            Assert.True(StockUiAstronautDecoration.ShouldAnnotateCrewTooltip(
                Deco(StockUiDecorationKind.KerbalHire, "Hired on D5", "why")));
            Assert.True(StockUiAstronautDecoration.ShouldAnnotateCrewTooltip(
                Deco(StockUiDecorationKind.KerbalOnFlight, "Reserved", "why")));
        }

        [Fact]
        public void AppendTooltip_UsesStocksReasonBlock_AndIsIdempotent()
        {
            string once = StockUiAstronautDecoration.AppendTooltip("Pilot skills", "Hired on D5", "Hired on D5 because.");
            Assert.Equal("Pilot skills\n\n<b>Hired on D5</b>\nHired on D5 because.", once);
            Assert.Equal(once, StockUiAstronautDecoration.AppendTooltip(once, "Hired on D5", "Hired on D5 because."));
            Assert.Equal("<b>T</b>\nwhy", StockUiAstronautDecoration.AppendTooltip("", "T", "why"));
            Assert.Equal("stock", StockUiAstronautDecoration.AppendTooltip("stock", "T", null));
        }

        [Theory]
        [InlineData("AddItem_Applicants", "Applicants")]
        [InlineData("AddItem_Available", "Available")]
        [InlineData("AddItem_Assigned", "Assigned")]
        [InlineData("AddItem_Kia", "Kia")]
        [InlineData("AddItem", null)]
        public void TabFor_MapsEachAddItemTarget(string method, string tab)
        {
            Assert.Equal(tab, StockUiAstronautDecoration.TabFor(method));
        }

        // ---------------- pairing: the disabled set == the click-block's refused set ----------------

        private static GameAction Tech(double ut, string id) =>
            new GameAction { UT = ut, Type = GameActionType.ScienceSpending, NodeId = id, Cost = 10f };
        private static GameAction Hire(double ut, string name) =>
            new GameAction { UT = ut, Type = GameActionType.KerbalHire, KerbalName = name };

        [Fact]
        public void Pairing_TechResearch_DisabledNodesAreExactlyTheRefusedOnes_WithTheSameText()
        {
            Ledger.AddAction(Tech(500, "committedNode"));
            Ledger.AddAction(Tech(50, "pastNode"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var ids = new[] { "committedNode", "pastNode", "freeNode" };

            var decorations = StockUiDecorationQuery.ForRnD(CommittedFutureIndexCache.Current, 100, ids,
                ReservationExplanation.DefaultDateFormatter);
            var disabled = decorations
                .Where(d => d.Marked && StockUiRnDDecoration.ShouldDisableResearch(d.Blocked, false, false))
                .Select(d => d.Id).ToArray();
            var refused = ids.Where(id => TechResearchPatch.TryBlockCommittedTech(id, id)).ToArray();

            Assert.Equal(new[] { "committedNode" }, disabled);
            Assert.Equal(new[] { "committedNode" }, refused);
            Assert.Equal(decorations.Single(d => d.Id == "committedNode").Why, dialogReasons.Single());
            Assert.Contains(logLines, l => l.Contains("[TechResearchPatch]") && l.Contains("Blocking tech research: 'committedNode'"));
        }

        [Fact]
        public void Pairing_KerbalHire_LockedApplicantsAreExactlyTheRefusedOnes_WithTheSameText()
        {
            Ledger.AddAction(Hire(500, "Future Kerman"));
            Ledger.AddAction(Hire(50, "Past Kerman"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var ids = new[] { "Future Kerman", "Past Kerman", "Free Kerman" };

            var context = StockUiOverlayController.BuildLiveAstronautContext(new HashSet<string>());
            var decisions = StockUiDecorationQuery.ForAstronautComplex(CommittedFutureIndexCache.Current, 100,
                    ids.Select(id => new StockUiItem(id, "Applicants")), context, ReservationExplanation.DefaultDateFormatter)
                .Select(d => new { d.Id, R = StockUiAstronautDecoration.Decide(d, "Applicants", "Applicant", true, null) })
                .ToList();
            var locked = decisions.Where(x => x.R.DisableButton).Select(x => x.Id).ToArray();
            var refused = ids.Where(id => !KerbalHirePatch.ShouldAllowHire(id)).ToArray();

            Assert.Equal(new[] { "Future Kerman" }, locked);
            Assert.Equal(new[] { "Future Kerman" }, refused);
            Assert.Equal(decisions.Single(x => x.Id == "Future Kerman").R.DisabledCaption, dialogReasons.Single());
        }

        [Fact]
        public void Pairing_KerbalDismissal_LockedRowsAreExactlyTheRefusedOnes_WithTheSameText()
        {
            // Jeb flies a committed flight that ends landed: an open-ended hold.
            var rec = MakeRecording("Mun Lander", new[] { "Jeb" }, TerminalState.Landed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var ids = new[] { "Jeb", "Free Kerman" };

            var context = StockUiOverlayController.BuildLiveAstronautContext(new HashSet<string>(ids));
            var decorations = StockUiDecorationQuery.ForAstronautComplex(CommittedFutureIndexCache.Current, 100,
                ids.Select(id => new StockUiItem(id, "Available")), context, ReservationExplanation.DefaultDateFormatter);
            var decisions = decorations
                .Select(d => new
                {
                    d.Id,
                    d.Kind,
                    R = StockUiAstronautDecoration.Decide(d, "Available", "Available", true,
                        KerbalDismissalPatch.DescribeDismissalRefusal(kerbals, d.Id))
                })
                .ToList();
            var locked = decisions.Where(x => x.R.DisableButton).Select(x => x.Id).ToArray();
            var refused = ids.Where(id => !KerbalDismissalPatch.ShouldAllowDismissal(id, "test")).ToArray();

            Assert.Equal(new[] { "Jeb" }, locked);
            Assert.Equal(new[] { "Jeb" }, refused);
            var jeb = decisions.Single(x => x.Id == "Jeb");
            Assert.Equal(StockUiDecorationKind.KerbalOnFlight, jeb.Kind);
            Assert.StartsWith("Reserved", jeb.R.Label);
            // The locked button's caption, the refused click's dialog and the hover say the same thing.
            Assert.Equal(jeb.R.DisabledCaption, dialogReasons.Single());
            Assert.Equal(decorations.Single(d => d.Id == "Jeb").Why, jeb.R.DisabledCaption);
            Assert.Contains(logLines, l => l.Contains("[KerbalDismissal]") && l.Contains("Blocked dismissal of 'Jeb' via test"));
        }

        [Fact]
        public void Dismissal_BypassesDuringReplayAndParsekCrewSuppression_AndLogsIt()
        {
            var rec = MakeRecording("Mun Lander", new[] { "Jeb" }, TerminalState.Landed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            LedgerOrchestrator.SetKerbalsForTesting(KerbalsTestHelper.RecalculateFromStore());

            GameStateRecorder.IsReplayingActions = true;
            Assert.True(KerbalDismissalPatch.ShouldAllowDismissal("Jeb", "KerbalRoster.SackAvailable"));
            GameStateRecorder.IsReplayingActions = false;
            using (SuppressionGuard.Crew())
                Assert.True(KerbalDismissalPatch.ShouldAllowDismissal("Jeb", "KerbalRoster.SackAvailable"));

            Assert.Empty(dialogReasons);
            Assert.Contains(logLines, l => l.Contains("[KerbalDismissal]") && l.Contains("bypass for 'Jeb'")
                && l.Contains("action replay in progress"));
            Assert.Contains(logLines, l => l.Contains("[KerbalDismissal]") && l.Contains("bypass for 'Jeb'")
                && l.Contains("crew-event suppression"));
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
