using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The R&amp;D and Astronaut Complex reservation annotations on STOCK mechanisms
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, section 10 step 2):
    /// the tinted node icon, the explanation in the stock tooltip and description, the
    /// non-interactable Research button, the Astronaut Complex row labels and the
    /// locked-with-reason hire / dismiss buttons. Each cell drives a real stock rebuild
    /// (a tree refresh, a crew list re-list, <c>UpdateCrewCounts</c> re-unlocking the
    /// applicants) and asserts the annotation survives it, then removes the committed
    /// fixture and asserts the stock state comes back.
    /// </summary>
    public partial class FlightIntegrationTests
    {
        private static readonly MethodInfo AstronautUpdateCrewCountsMethod =
            typeof(AstronautComplex).GetMethod("UpdateCrewCounts",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo AstronautCreateApplicantListMethod =
            typeof(AstronautComplex).GetMethod("CreateApplicantList",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo AstronautCreateAvailableListMethod =
            typeof(AstronautComplex).GetMethod("CreateAvailableList",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo CrewListItemTooltipField =
            typeof(CrewListItem).GetField("tooltipController",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // ---------------- R&D ----------------

        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI annotation: a committed-future R&D node gets the tinted stock icon and the explanation in its stock tooltip; both survive a stock tree refresh and clear when the committed row goes.")]
        public IEnumerator RnDStockMarkAndTooltipSurviveTreeRefresh()
        {
            yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
            yield return WaitForStockUiOverlayController(5f);
            if (!CheckTechResearchCapableMode()) yield break;
            yield return WaitForRdControllerClosed(8f);

            Recording recording = null;
            string recordingId = null;
            RDController controller = null;
            try
            {
                if (!TryEnterSpaceCenterBuilding<RnDBuilding>("R&D", out _))
                    yield break;
                yield return WaitForRdController(15f);
                controller = RDController.Instance ?? Object.FindObjectOfType<RDController>();
                if (!TryPickUnresearchedRdNode(controller, requireResearchable: false,
                        out RDNode node, out string techId, out string reason))
                {
                    InGameAssert.Skip(reason);
                    yield break;
                }

                recordingId = "stockui-rnd-mark-" + System.Guid.NewGuid().ToString("N");
                recording = AddCommittedOverlayFixture(recordingId, GameStateEventType.TechResearched, techId,
                    "Stock-UI R&D mark test");
                NotifyTimelineDataChangedForOverlayTest();

                yield return WaitUntilTrue(() => IsRdNodeMarked(node),
                    $"R&D node '{techId}' should carry the tinted icon and the tooltip explanation after the timeline change", 5f);

                // A stock refresh re-runs SetButtonState (which rewrites the icon colour)
                // and rebuilds the tooltip text from GetTooltipCaption.
                controller.techTree.RefreshUI();
                InGameAssert.IsTrue(IsRdNodeMarked(node),
                    $"R&D node '{techId}' annotation should survive RDTechTree.RefreshUI (tint={node.graphics.GetIconColor()})");
                InGameAssert.AreEqual(1, CountOccurrences(RdNodeTooltipText(node), ReservationExplanation.TimelineRule),
                    "The stock tooltip should carry the explanation exactly once after a refresh");

                RemoveCommittedOverlayFixture(recordingId, recording);
                recordingId = null;
                recording = null;
                NotifyTimelineDataChangedForOverlayTest();
                yield return WaitUntilTrue(
                    () => !StockUiRnDDecoration.SameRgb(node.graphics.GetIconColor(), StockUiRnDDecoration.MarkTint)
                        && !RdNodeTooltipText(node).Contains(ReservationExplanation.TimelineRule),
                    $"R&D node '{techId}' tint and tooltip explanation should clear once the committed row is gone (state={node.state})",
                    5f);
            }
            finally
            {
                RemoveCommittedOverlayFixture(recordingId, recording);
                CloseRnDForOverlayTest(controller);
            }
            yield return WaitForRdControllerClosed(8f);
        }

        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI annotation: selecting a committed-future R&D node shows a non-interactable, greyed Research button with the explanation in the stock side-panel description, through a panel refresh; the button re-enables with its stock colours when the committed row goes.")]
        public IEnumerator RnDResearchButtonDisabledShowsItsReason()
        {
            yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
            yield return WaitForStockUiOverlayController(5f);
            if (!CheckTechResearchCapableMode()) yield break;
            yield return WaitForRdControllerClosed(8f);

            Recording recording = null;
            string recordingId = null;
            RDController controller = null;
            try
            {
                if (!TryEnterSpaceCenterBuilding<RnDBuilding>("R&D", out _))
                    yield break;
                yield return WaitForRdController(15f);
                controller = RDController.Instance ?? Object.FindObjectOfType<RDController>();
                if (!TryPickUnresearchedRdNode(controller, requireResearchable: true,
                        out RDNode node, out string techId, out string reason))
                {
                    InGameAssert.Skip(reason);
                    yield break;
                }

                List<Color> actionColorsBefore = SnapshotButtonColors(controller.actionButton.Button);

                recordingId = "stockui-rnd-block-" + System.Guid.NewGuid().ToString("N");
                recording = AddCommittedOverlayFixture(recordingId, GameStateEventType.TechResearched, techId,
                    "Stock-UI R&D block test");
                NotifyTimelineDataChangedForOverlayTest();
                yield return WaitUntilTrue(() => IsRdNodeMarked(node),
                    $"R&D node '{techId}' should be marked before it is selected", 5f);

                controller.node_selected = node;
                controller.ShowNodePanel(node);
                InGameAssert.AreEqual("research", controller.actionButton.currentState,
                    "The selected unresearched node should show the Research state of the action button");
                InGameAssert.IsFalse(controller.actionButton.Button.interactable,
                    $"The Research button for committed node '{techId}' should be non-interactable");
                AssertButtonLooksDisabled(controller.actionButton.Button, "Research", "after selecting the committed node");
                string description = RdPanelDescription(controller);
                InGameAssert.IsTrue(description.Contains(ReservationExplanation.TimelineRule),
                    $"The side-panel description should carry the reason beside the disabled button; got '{description}'");

                // Stock's refresh re-enables Research in UpdatePanel; the block must come back.
                controller.techTree.RefreshUI();
                InGameAssert.IsFalse(controller.actionButton.Button.interactable,
                    "The Research button should stay disabled through RDTechTree.RefreshUI");
                InGameAssert.AreEqual(1, CountOccurrences(RdPanelDescription(controller), ReservationExplanation.TimelineRule),
                    "The side-panel reason should appear exactly once after a refresh");

                RemoveCommittedOverlayFixture(recordingId, recording);
                recordingId = null;
                recording = null;
                NotifyTimelineDataChangedForOverlayTest();
                yield return WaitUntilTrue(
                    () => controller.actionButton.Button.interactable
                        && !RdPanelDescription(controller).Contains(ReservationExplanation.TimelineRule),
                    "The Research button should re-enable and the reason clear once the committed row is gone", 5f);
                AssertButtonColorsRestored(controller.actionButton.Button, actionColorsBefore, "Research",
                    "once the committed row is gone");
            }
            finally
            {
                RemoveCommittedOverlayFixture(recordingId, recording);
                if (controller != null)
                {
                    controller.node_selected = null;
                    try { controller.ShowNothingPanel(); }
                    catch (System.Exception ex)
                    {
                        ParsekLog.Warn("TestRunner", $"Stock-UI R&D panel reset threw: {ex.Message}");
                    }
                }
                CloseRnDForOverlayTest(controller);
            }
            yield return WaitForRdControllerClosed(8f);
        }

        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI annotation: across two R&D open/close cycles with a marked node, Parsek adds no GameObject to the stock screen and no tree node outlives the close.")]
        public IEnumerator RnDStockAnnotationLeavesNothingAcrossOpenClose()
        {
            yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
            yield return WaitForStockUiOverlayController(5f);
            if (!CheckTechResearchCapableMode()) yield break;
            yield return WaitForRdControllerClosed(8f);
            int rdNodesBefore = Object.FindObjectsOfType<RDNode>().Length;

            for (int cycle = 0; cycle < 2; cycle++)
            {
                Recording recording = null;
                string recordingId = null;
                RDController controller = null;
                try
                {
                    if (!TryEnterSpaceCenterBuilding<RnDBuilding>("R&D", out _))
                        yield break;
                    yield return WaitForRdController(15f);
                    controller = RDController.Instance ?? Object.FindObjectOfType<RDController>();
                    if (!TryPickUnresearchedRdNode(controller, requireResearchable: false,
                            out RDNode node, out string techId, out string reason))
                    {
                        InGameAssert.Skip(reason);
                        yield break;
                    }

                    recordingId = "stockui-rnd-cycle-" + cycle + "-" + System.Guid.NewGuid().ToString("N");
                    recording = AddCommittedOverlayFixture(recordingId, GameStateEventType.TechResearched, techId,
                        "Stock-UI R&D open/close test");
                    NotifyTimelineDataChangedForOverlayTest();
                    yield return WaitUntilTrue(() => IsRdNodeMarked(node),
                        $"R&D cycle {cycle + 1}: node '{techId}' should be marked", 5f);

                    InGameAssert.AreEqual(0, CountParsekStockScreenObjects(),
                        $"R&D cycle {cycle + 1}: the annotation must not add Parsek GameObjects (badges) to the stock screen");
                }
                finally
                {
                    RemoveCommittedOverlayFixture(recordingId, recording);
                    CloseRnDForOverlayTest(controller);
                }

                yield return WaitForRdControllerClosed(8f);
                yield return WaitUntilTrue(() => Object.FindObjectsOfType<RDNode>().Length == rdNodesBefore,
                    $"R&D cycle {cycle + 1}: no RDNode should outlive the close (before={rdNodesBefore})", 5f);
                InGameAssert.AreEqual(0, CountParsekStockScreenObjects(),
                    $"R&D cycle {cycle + 1}: no Parsek stock-screen object should exist after close");
            }
        }

        // ---------------- Astronaut Complex ----------------

        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI annotation: Astronaut Complex rows of a committed-future hire and a reserved kerbal carry the reservation in their own stock label, through UpdateCrewCounts and a stock re-list, and the stock label returns when the reservation goes.")]
        public IEnumerator AstronautStockLabelsSurviveRelist()
        {
            AstronautFixture fx = new AstronautFixture();
            yield return SetUpAstronautFixture(fx, "Lbl");
            if (!fx.Ready) yield break;
            try
            {
                yield return WaitUntilTrue(() => AstronautRowsDecorated(fx),
                    $"Astronaut rows should be labelled: future='{RowLabel(FindAstronautRow(fx.FutureName))}' reserved='{RowLabel(FindAstronautRow(fx.ReservedName))}'",
                    8f);

                AstronautComplex complex = Object.FindObjectOfType<AstronautComplex>();
                AstronautUpdateCrewCountsMethod.Invoke(complex, null);
                InGameAssert.IsTrue(AstronautRowsDecorated(fx),
                    "Row labels should survive UpdateCrewCounts (which re-unlocks every applicant)");

                // A stock re-list destroys and rebuilds the rows (the AddItem_* path). The rows
                // are read back through the stock lists, so a destroyed old row cannot answer.
                CrewListItem oldFuture = FindAstronautRow(fx.FutureName);
                AstronautCreateApplicantListMethod.Invoke(complex, null);
                AstronautCreateAvailableListMethod.Invoke(complex, null);
                InGameAssert.IsFalse(ReferenceEquals(oldFuture, FindAstronautRow(fx.FutureName)),
                    "The re-list should have rebuilt the applicant row");
                InGameAssert.IsTrue(AstronautRowsDecorated(fx),
                    "Rebuilt rows should be labelled by the AddItem_* postfix before any refresh");
                AstronautUpdateCrewCountsMethod.Invoke(complex, null);
                InGameAssert.IsTrue(AstronautRowsDecorated(fx),
                    "Rebuilt rows should stay labelled after UpdateCrewCounts");

                fx.ReleaseReservations();
                NotifyTimelineDataChangedForOverlayTest();
                yield return WaitUntilTrue(
                    () => !RowLabel(FindAstronautRow(fx.FutureName)).StartsWith("Hired on")
                        && !RowLabel(FindAstronautRow(fx.ReservedName)).StartsWith("Reserved"),
                    "The stock row labels should return once the reservations are gone", 8f);
            }
            finally
            {
                fx.TearDown();
                CloseAstronautForOverlayTest();
            }
            yield return WaitForAstronautComplexClosed(8f);
        }

        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI annotation: the hire button of a committed-future hire and the dismiss button of a reserved kerbal are in stock's locked-with-reason state, the stock crew tooltip carries the reason, the hire lock survives UpdateCrewCounts, and both unlock when the reservations go.")]
        public IEnumerator AstronautButtonsDisabledShowTheirReason()
        {
            AstronautFixture fx = new AstronautFixture();
            yield return SetUpAstronautFixture(fx, "Btn");
            if (!fx.Ready) yield break;
            try
            {
                yield return WaitUntilTrue(() => AstronautRowsDecorated(fx),
                    "Astronaut rows should be decorated before the button checks", 8f);

                AstronautComplex complex = Object.FindObjectOfType<AstronautComplex>();
                bool underCrewLimit = IsUnderCrewLimit();

                CrewListItem futureRow = FindAstronautRow(fx.FutureName);
                CrewListItem reservedRow = FindAstronautRow(fx.ReservedName);
                InGameAssert.IsFalse(futureRow.MouseoverEnabled,
                    "The future-hire applicant's hire button should be locked (stock SetButtonEnabled(false))");
                InGameAssert.IsFalse(reservedRow.MouseoverEnabled,
                    "The reserved kerbal's dismiss button should be locked (stock SetButtonEnabled(false))");
                AssertTooltipCarries(futureRow, ReservationExplanation.TimelineRule, "future-hire applicant");
                AssertTooltipCarries(reservedRow, ReservationExplanation.CrewRule, "reserved kerbal");

                // Stock re-unlocks every applicant here; the hire lock must come back.
                AstronautUpdateCrewCountsMethod.Invoke(complex, null);
                futureRow = FindAstronautRow(fx.FutureName);
                InGameAssert.IsFalse(futureRow.MouseoverEnabled,
                    "The hire lock should survive UpdateCrewCounts re-unlocking the applicants");
                AssertTooltipCarries(futureRow, ReservationExplanation.TimelineRule, "future-hire applicant after UpdateCrewCounts");

                fx.ReleaseReservations();
                NotifyTimelineDataChangedForOverlayTest();
                yield return WaitUntilTrue(
                    () => FindAstronautRow(fx.ReservedName) != null
                        && FindAstronautRow(fx.ReservedName).MouseoverEnabled
                        && !TooltipText(FindAstronautRow(fx.ReservedName)).Contains(ReservationExplanation.CrewRule),
                    "The dismiss button should unlock and the reason clear once the reservation is gone", 8f);
                futureRow = FindAstronautRow(fx.FutureName);
                InGameAssert.IsFalse(TooltipText(futureRow).Contains(ReservationExplanation.TimelineRule),
                    "The hire reason should clear from the stock tooltip once the committed hire is gone");
                if (underCrewLimit)
                    InGameAssert.IsTrue(futureRow.MouseoverEnabled,
                        "Under the crew limit the applicant's hire button should unlock once the committed hire is gone");
                else
                    ParsekLog.Info("TestRunner",
                        "AstronautButtonsDisabledShowTheirReason: at the crew limit, stock keeps applicants locked; unlock not asserted");
            }
            finally
            {
                fx.TearDown();
                CloseAstronautForOverlayTest();
            }
            yield return WaitForAstronautComplexClosed(8f);
        }

        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI annotation: across two Astronaut Complex open/close cycles with a labelled applicant, Parsek adds no GameObject to the stock screen and no crew row outlives the close.")]
        public IEnumerator AstronautStockAnnotationLeavesNothingAcrossOpenClose()
        {
            yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
            yield return WaitForAstronautComplexClosed(8f);
            int crewRowsBefore = Object.FindObjectsOfType<CrewListItem>().Length;
            for (int cycle = 0; cycle < 2; cycle++)
            {
                AstronautFixture fx = new AstronautFixture();
                yield return SetUpAstronautFixture(fx, "Cyc" + cycle);
                if (!fx.Ready) yield break;
                try
                {
                    yield return WaitUntilTrue(() => AstronautRowsDecorated(fx),
                        $"Astronaut cycle {cycle + 1}: rows should be labelled", 8f);
                    InGameAssert.AreEqual(0, CountParsekStockScreenObjects(),
                        $"Astronaut cycle {cycle + 1}: the annotation must not add Parsek GameObjects (badges) to the stock screen");
                }
                finally
                {
                    fx.TearDown();
                    CloseAstronautForOverlayTest();
                }
                yield return WaitForAstronautComplexClosed(8f);
                yield return WaitUntilTrue(() => Object.FindObjectsOfType<CrewListItem>().Length == crewRowsBefore,
                    $"Astronaut cycle {cycle + 1}: no crew row should outlive the close (before={crewRowsBefore})", 5f);
                InGameAssert.AreEqual(0, CountParsekStockScreenObjects(),
                    $"Astronaut cycle {cycle + 1}: no Parsek stock-screen object should exist after close");
            }
        }

        // ---------------- helpers ----------------

        private static bool CheckTechResearchCapableMode()
        {
            if (HighLogic.CurrentGame == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                return false;
            }
            if (HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX)
            {
                // RnDBuilding.EnterBuilding() in pure Sandbox instantiates no RDController.
                InGameAssert.Skip(
                    $"R&D annotation verification needs a tech-research-capable mode (mode={HighLogic.CurrentGame.Mode})");
                return false;
            }
            return true;
        }

        /// <summary>
        /// An unresearched tree node with a tech id. With <paramref name="requireResearchable"/>
        /// the node must show the Research button (stock state RESEARCHABLE: its cost is under
        /// the R&amp;D level's science cap, independent of the current science pool).
        /// </summary>
        private static bool TryPickUnresearchedRdNode(
            RDController controller, bool requireResearchable,
            out RDNode node, out string techId, out string reason)
        {
            node = null;
            techId = null;
            reason = null;
            if (controller == null || controller.nodes == null)
            {
                reason = "RDController or its node list is unavailable";
                return false;
            }
            int unresearched = 0;
            for (int i = 0; i < controller.nodes.Count; i++)
            {
                RDNode candidate = controller.nodes[i];
                if (candidate == null || candidate.tech == null || !candidate.treeNode) continue;
                if (string.IsNullOrEmpty(candidate.tech.techID) || candidate.IsResearched) continue;
                if (candidate.state == RDNode.State.HIDDEN) continue;
                // A node the save's own committed timeline already researches would never clear.
                if (StockUiReservationPredicates.IsTechResearchBlocked(CommittedFutureIndexCache.Current,
                        candidate.tech.techID, CommittedFutureIndexCache.CurrentUT())) continue;
                unresearched++;
                if (requireResearchable && candidate.state != RDNode.State.RESEARCHABLE) continue;
                node = candidate;
                techId = candidate.tech.techID;
                return true;
            }
            reason = requireResearchable
                ? $"No unresearched RESEARCHABLE R&D node (unresearched={unresearched}); the Research button is hidden on FADED nodes"
                : "No unresearched R&D tree node with a tech id";
            return false;
        }

        private static string RdNodeTooltipText(RDNode node)
        {
            return node != null && node.graphics != null && node.graphics.tooltip != null
                ? node.graphics.tooltip.textString ?? ""
                : "";
        }

        private static bool IsRdNodeMarked(RDNode node)
        {
            return node != null && node.graphics != null
                && StockUiRnDDecoration.SameRgb(node.graphics.GetIconColor(), StockUiRnDDecoration.MarkTint)
                && RdNodeTooltipText(node).Contains(ReservationExplanation.TimelineRule);
        }

        private static string RdPanelDescription(RDController controller)
        {
            return StockUiText.Get(StockUiText.LabelField(controller, typeof(RDController), "node_description")) ?? "";
        }

        private static int CountOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// Parsek-created GameObjects on the open R&amp;D and Astronaut Complex screens: any
        /// transform named with the <c>Parsek_</c> prefix under either screen, plus the
        /// retired badge names anywhere. The annotation writes only stock state, so this is 0.
        /// </summary>
        private static int CountParsekStockScreenObjects()
        {
            int count = CountGlobalNamedTransforms(StockUiOverlayTechObjectName)
                + CountGlobalNamedTransforms(StockUiOverlayKerbalObjectName);
            var roots = new List<Transform>();
            RDController rd = RDController.Instance ?? Object.FindObjectOfType<RDController>();
            if (rd != null) roots.Add(rd.transform);
            AstronautComplex ac = Object.FindObjectOfType<AstronautComplex>();
            if (ac != null) roots.Add(ac.transform);
            for (int r = 0; r < roots.Count; r++)
            {
                Transform[] all = roots[r].GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].name.StartsWith("Parsek_", System.StringComparison.Ordinal)
                        && all[i].name != StockUiOverlayTechObjectName
                        && all[i].name != StockUiOverlayKerbalObjectName)
                        count++;
                }
            }
            return count;
        }

        private sealed class AstronautFixture
        {
            internal bool Ready;
            internal KerbalRoster Roster;
            internal string FutureName;
            internal string ReservedName;
            internal ProtoCrewMember FutureApplicant;
            internal ProtoCrewMember ReservedCrew;
            internal KerbalsModule PriorKerbals;
            internal bool KerbalsSwapped;
            internal string RecordingId;
            internal Recording Recording;

            internal void ReleaseReservations()
            {
                RemoveCommittedOverlayFixture(RecordingId, Recording);
                RecordingId = null;
                Recording = null;
                if (KerbalsSwapped)
                {
                    LedgerOrchestrator.SetKerbalsForTesting(PriorKerbals);
                    KerbalsSwapped = false;
                }
            }

            internal void TearDown()
            {
                ReleaseReservations();
                using (SuppressionGuard.Crew())
                {
                    RemoveKerbalForOverlayTest(Roster, FutureApplicant);
                    RemoveKerbalForOverlayTest(Roster, ReservedCrew);
                }
                FutureApplicant = null;
                ReservedCrew = null;
            }
        }

        /// <summary>
        /// A future-hire applicant (a committed CrewHired row) and a reserved Available crew
        /// kerbal (a test KerbalsModule holding him), then opens the Astronaut Complex.
        /// <see cref="AstronautFixture.Ready"/> is false after a Skip.
        /// </summary>
        private IEnumerator SetUpAstronautFixture(AstronautFixture fx, string tag)
        {
            yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
            yield return WaitForStockUiOverlayController(5f);
            if (HighLogic.CurrentGame == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                yield break;
            }
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip($"Astronaut Complex annotation verification is career-only (mode={HighLogic.CurrentGame.Mode})");
                yield break;
            }
            fx.Roster = HighLogic.CurrentGame.CrewRoster;
            if (fx.Roster == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame.CrewRoster is null");
                yield break;
            }
            if (AstronautUpdateCrewCountsMethod == null || AstronautCreateApplicantListMethod == null
                || AstronautCreateAvailableListMethod == null || CrewListItemTooltipField == null)
            {
                InGameAssert.Fail("A stock AstronautComplex / CrewListItem member these cells drive was not found");
                yield break;
            }
            yield return WaitForAstronautComplexClosed(8f);

            string suffix = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            fx.FutureName = "PrskFut" + tag + suffix + " Kerman";
            fx.ReservedName = "PrskRes" + tag + suffix + " Kerman";
            using (SuppressionGuard.Crew())
            {
                fx.FutureApplicant = CreateApplicantForOverlayTest(fx.Roster, fx.FutureName);
                fx.ReservedCrew = fx.Roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                InGameAssert.IsNotNull(fx.ReservedCrew, $"Could not create crew '{fx.ReservedName}'");
                fx.ReservedCrew.ChangeName(fx.ReservedName);
                fx.ReservedCrew.rosterStatus = ProtoCrewMember.RosterStatus.Available;
            }

            var testKerbals = new KerbalsModule();
            if (!TryInstallReservedKerbalForOverlayTest(testKerbals, fx.ReservedName))
            {
                fx.TearDown();
                yield break;
            }
            fx.PriorKerbals = LedgerOrchestrator.Kerbals;
            LedgerOrchestrator.SetKerbalsForTesting(testKerbals);
            fx.KerbalsSwapped = true;

            fx.RecordingId = "stockui-astronaut-" + tag + "-" + System.Guid.NewGuid().ToString("N");
            fx.Recording = AddCommittedOverlayFixture(fx.RecordingId, GameStateEventType.CrewHired, fx.FutureName,
                "Stock-UI Astronaut Complex test");

            if (!TryEnterSpaceCenterBuilding<AstronautComplexFacility>("Astronaut Complex", out _))
            {
                fx.TearDown();
                yield break;
            }
            yield return WaitForAstronautComplex(8f);
            fx.Ready = true;
        }

        /// <summary>The live row for a kerbal, read through the four stock lists (not the
        /// transform tree, which still holds rows a re-list has destroyed this frame).</summary>
        private static CrewListItem FindAstronautRow(string name)
        {
            AstronautComplex complex = Object.FindObjectOfType<AstronautComplex>();
            if (complex == null || string.IsNullOrEmpty(name)) return null;
            string[] tabs =
            {
                StockUiDecorationQuery.AstronautApplicantsTab,
                StockUiDecorationQuery.AstronautAvailableTab,
                StockUiDecorationQuery.AstronautAssignedTab,
                StockUiDecorationQuery.AstronautKiaTab
            };
            for (int t = 0; t < tabs.Length; t++)
            {
                UIList list = StockUiAstronautDecoration.ListFor(complex, tabs[t]);
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    UIListItem item = list.GetUilistItemAt(i);
                    CrewListItem row = item != null ? item.GetComponent<CrewListItem>() : null;
                    if (row != null && string.Equals(StockUiAstronautDecoration.SafeName(row), name, System.StringComparison.Ordinal))
                        return row;
                }
            }
            return null;
        }

        private static string RowLabel(CrewListItem row)
        {
            if (row == null) return "";
            // The line the player reads: an applicant row's status line is hidden by its
            // prefab, so its status is on the trait line (GUI-28 F2).
            return StockUiAstronautDecoration.ShownStatusText(row);
        }

        private static string TooltipText(CrewListItem row)
        {
            var tooltip = row != null ? CrewListItemTooltipField.GetValue(row) as TooltipController_CrewAC : null;
            return tooltip != null ? tooltip.descriptionString ?? "" : "";
        }

        private static void AssertTooltipCarries(CrewListItem row, string needle, string who)
        {
            var tooltip = CrewListItemTooltipField.GetValue(row) as TooltipController_CrewAC;
            InGameAssert.IsNotNull(tooltip, $"The {who} row should have a stock crew tooltip controller");
            InGameAssert.IsTrue(tooltip.showTooltip, $"The {who} stock crew tooltip should be enabled");
            InGameAssert.IsTrue((tooltip.descriptionString ?? "").Contains(needle),
                $"The {who} stock crew tooltip should carry the reason; got '{tooltip.descriptionString}'");
        }

        private static bool AstronautRowsDecorated(AstronautFixture fx)
        {
            return RowLabel(FindAstronautRow(fx.FutureName)).StartsWith("Hired on")
                && RowLabel(FindAstronautRow(fx.ReservedName)).StartsWith("Reserved");
        }

        private static bool IsUnderCrewLimit()
        {
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null || GameVariables.Instance == null) return false;
            int limit = GameVariables.Instance.GetActiveCrewLimit(
                ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex));
            return roster.GetActiveCrewCount() < limit;
        }
    }
}
