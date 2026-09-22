using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.TestCommands;
using Parsek.UI.Gallery;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI proof of the ONE thing no headless cell can reach: that a mocked view
    /// model, installed into a real window, is actually DRAWN - and that clearing it puts
    /// the real model back.
    ///
    /// <para><b>Why this cannot be a unit test.</b> The headless suite covers the whole
    /// pure half: the catalogue builds against the real model types, every witness set is
    /// derived through the real per-column formatters, the session lifecycle and every
    /// refusal are exercised directly, three source gates keep the applier's tables
    /// honest, and the write-set gate keeps the crash-safety claim structural. What none
    /// of it can witness is the SUPPRESSION-PLUS-DRAW path: whether the cache suppression
    /// actually holds across a real frame, whether the window's own draw method renders
    /// the mocked rows, and whether the restore leaves the real model rebuildable. Those
    /// are measurements on a running KSP, which is what design section 13 means by "the
    /// only place the suppression-plus-draw path can be proven".</para>
    ///
    /// <para><b>ITS OWN CATEGORY, deliberately.</b> Adding a cell to an existing category
    /// moves the <c>BATCH_COMPLETE</c> tally that committed harness specs pin, and
    /// <c>CommittedBatchTallySourceSyncTests</c> reds the harness suite for it. A category
    /// no spec drives keeps every pinned tally honest until someone flies this one - the
    /// same reasoning <c>GuiTree</c> and <c>DisabledHoverEcho</c> carry.</para>
    ///
    /// <para><b>NEVER FLOWN.</b> P1 ships no lane and the machine is operator-gated, so
    /// every assertion here is a prediction. Each failure message prints what it measured
    /// rather than only what it wanted, so the first flight corrects a pin instead of
    /// guessing at one.</para>
    ///
    /// <para><b>It drives the applier's own path rather than a copy of it.</b> The cells
    /// call <c>GuiMockSession</c> and the window accessors directly - there is no seam
    /// channel inside a test batch - but they install through the SAME members the applier
    /// writes and assert through the SAME witness derivation, so a divergence between this
    /// and the op would be a divergence in one of those two shared halves.</para>
    /// </summary>
    public sealed class GuiMockApplyInGameTest
    {
        private const string CaptureLabel = "parsek-guimock-probe";

        /// <summary>Frames to let the window draw before the capture is read. The recorder
        /// flushes from <c>LateUpdate</c> one frame after the armed pass, so a capture
        /// needs at least two; the extra frames are slack for a scene that is still
        /// settling.</summary>
        private const int DrawFrames = 6;

        [InGameTest(Category = "GuiMock", Scene = GameScenes.SPACECENTER,
            Description = "Applying a catalogue state to the Kerbals window makes the real "
                + "draw code render the mocked rows (witnessed in a captured GUI tree "
                + "scoped to that window), the suppression holds across a live-crew "
                + "GameEvent, and the clear leaves the real model rebuildable")]
        public IEnumerator Apply_MakesTheRealDrawCodeRenderTheMockedKerbalsRows()
        {
            yield return DriveOneState("kerbals.roster.lost");
        }

        [InGameTest(Category = "GuiMock", Scene = GameScenes.SPACECENTER,
            Description = "The same for Career State, whose cached view model has TWO "
                + "writers - so this is also the live proof that the ledger-invalidate "
                + "suppression holds and the scope is not marked broken")]
        public IEnumerator Apply_MakesTheRealDrawCodeRenderTheMockedCareerRows()
        {
            yield return DriveOneState("career.banner.divergent");
        }

        [InGameTest(Category = "GuiMock", Scene = GameScenes.SPACECENTER,
            Description = "The same for the Structure List, whose rows are real-builder "
                + "output over detached inputs - so this is the live proof that a "
                + "builder-produced Event / Status / Location cell draws verbatim")]
        public IEnumerator Apply_MakesTheRealDrawCodeRenderTheMockedStructureRows()
        {
            yield return DriveOneState("structure.mission.terminal-splashed");
        }

        [InGameTest(Category = "GuiMock", Scene = GameScenes.SPACECENTER,
            Description = "A SaveGame attempt while a mock scope is live is refused "
                + "save-refused-gui-mock, and the scope survives the refusal")]
        public IEnumerator SaveGame_IsRefusedWhileAScopeIsLive()
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                InGameAssert.Skip("no live ParsekUI in this scene");
                yield break;
            }
            GuiMockSession.ResetForTesting();

            GuiMockState state = GuiMockCatalogue.ById("kerbals.roster.lost");
            InGameAssert.IsTrue(state != null, "the catalogue has no kerbals.roster.lost");
            KerbalsWindowUI kerbals = ui.GetKerbalsUI();
            KerbalsWindowUI.KerbalsViewModel? payloadVm = state.Build().Kerbals;
            kerbals.CachedViewModelForTesting = payloadVm;
            InGameAssert.IsTrue(
                GuiMockSession.Begin(state.Id, GuiMockSession.KerbalsWindow,
                                     Time.frameCount, "in-game",
                                     () => { kerbals.CachedViewModelForTesting = null; }),
                "could not open a mock scope");

            try
            {
                // The refusal is LANE HYGIENE rather than the data guard, so what this
                // cell proves is that the predicate the verb reads is the live one - not
                // that data was at risk. It never is: nothing injected is read by a save
                // path, which is the whole structural argument.
                InGameAssert.IsTrue(GuiMockSession.IsLive,
                    "the scope did not open");
                InGameAssert.AreEqual("save-refused-gui-mock",
                    TestCommandSaveGame.RefusedGuiMockReason,
                    "the refusal token moved");
                yield return null;
                InGameAssert.IsTrue(GuiMockSession.IsLive,
                    "the scope did not survive a frame");
            }
            finally
            {
                GuiMockSession.Clear("in-game-teardown", -1);
            }
        }

        [InGameTest(Category = "GuiMock", Scene = GameScenes.SPACECENTER,
            Description = "Every declared suppression site is INERT with no scope live, "
                + "measured on a running game rather than headlessly - the property that "
                + "makes a player build behave exactly as it did before this shipped")]
        public IEnumerator EverySuppressionSiteIsInertWithNoScope()
        {
            GuiMockSession.ResetForTesting();
            InGameAssert.IsFalse(GuiMockSession.IsLive, "a scope was already live");
            foreach (GuiMockSuppressionSite site in GuiMockSession.SuppressionSites)
            {
                InGameAssert.IsFalse(GuiMockSession.Suppressed(site),
                    "site " + site.Site + " suppressed with NO scope live");
            }

            // And the two windows really do rebuild: invalidate, draw a frame, and the
            // caches come back non-null off live data.
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                InGameAssert.Skip("no live ParsekUI in this scene");
                yield break;
            }
            KerbalsWindowUI kerbals = ui.GetKerbalsUI();
            CareerStateWindowUI career = ui.GetCareerStateUI();
            bool kerbalsWasOpen = kerbals.IsOpen;
            bool careerWasOpen = career.IsOpen;
            kerbals.IsOpen = true;
            career.IsOpen = true;
            kerbals.InvalidateCache();
            career.InvalidateCache();
            for (int i = 0; i < DrawFrames; i++) yield return null;
            InGameAssert.IsTrue(kerbals.CachedViewModelForTesting != null,
                "the Kerbals window did not rebuild its view model after an "
                + "un-suppressed invalidate");
            kerbals.IsOpen = kerbalsWasOpen;
            career.IsOpen = careerWasOpen;
        }

        /// <summary>
        /// Applies one catalogue state, captures the frame it drew, asserts the state's
        /// own derived witnesses appear in THAT WINDOW'S subtree, clears, and asserts the
        /// real model is rebuildable.
        /// </summary>
        private IEnumerator DriveOneState(string stateId)
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                InGameAssert.Skip("no live ParsekUI in this scene");
                yield break;
            }
            GuiMockState state = GuiMockCatalogue.ById(stateId);
            if (state == null)
            {
                InGameAssert.Skip("the catalogue has no state " + stateId);
                yield break;
            }
            if (!GuiMockCatalogue.IsMockableInMode(state.Window,
                                                   ParsekUI.AppliedUiComplexityMode))
            {
                // Basic hides the Kerbals and Career launchers, so the batch's own mode
                // decides whether this cell can run at all. A SKIP naming the mode is the
                // honest answer, per the FLIGHT-test rule.
                InGameAssert.Skip("window " + state.Window + " is hidden in the current "
                                  + "complexity mode; run this batch in Advanced");
                yield break;
            }

            GuiMockSession.ResetForTesting();
            GuiMockPayload payload = state.Build();
            List<string> witnesses = GuiMockWitness.Expected(payload, state.Tab,
                                                             state.Covers);
            InGameAssert.IsTrue(witnesses.Count > 0,
                "state " + stateId + " derived no witness, so this cell would be vacuous");

            // Open and size the window, install the data, and open the scope - the same
            // three steps the applier takes, through the same members.
            Action restore = InstallForProbe(ui, state, payload);
            InGameAssert.IsTrue(
                GuiMockSession.Begin(state.Id, state.Window, Time.frameCount, "in-game",
                                     restore),
                "could not open a mock scope for " + stateId);

            try
            {
                // A LIVE GameEvent that would ordinarily drop the cache. This is the half
                // headless cells cannot reach: the suppression has to hold across a real
                // event dispatch, not across a direct call.
                GameEvents.onVesselChange.Fire(FlightGlobals.ActiveVessel);
                // The ledger hook, fired through its own delegate - which is how
                // ParsekUI.OnTimelineDataChanged reaches BOTH windows' InvalidateCache.
                // This is the event that broke a Career scope before the
                // career-invalidate suppression existed.
                Action timelineHook = LedgerOrchestrator.OnTimelineDataChanged;
                if (timelineHook != null) timelineHook();
                yield return null;

                InGameAssert.IsFalse(GuiMockSession.IsBroken,
                    "the scope was marked BROKEN after a live invalidate, so a "
                    + "suppression site is missing: " + GuiMockSession.BrokenReason);

                string armPath = GuiTreeRecorder.ArmForNextRepaint(
                    CaptureLabel, writeToDisk: false);
                InGameAssert.IsTrue(!string.IsNullOrEmpty(armPath),
                    "the GUI-tree recorder refused the arm: "
                    + (GuiTreeRecorder.LastArmRefusalReason ?? "unknown"));
                for (int i = 0; i < DrawFrames && GuiTreeRecorder.HasPendingWork; i++)
                    yield return null;

                GuiTreeResult tree = GuiTreeRecorder.LastTree;
                InGameAssert.IsTrue(tree != null, "no GUI tree was captured");

                List<string> drawn = DrawnTextsOf(tree, WindowIdOf(ui, state.Window));
                InGameAssert.IsTrue(drawn.Count > 0,
                    "window " + state.Window + " drew NO text in the captured frame "
                    + "(windows in capture: "
                    + tree.WindowCount.ToString(CultureInfo.InvariantCulture) + ")");

                string missing;
                bool applied = TestCommandUiMock.WitnessesDrawn(witnesses, drawn,
                                                                out missing);
                InGameAssert.IsTrue(applied,
                    "the mocked model was NOT drawn: witness '" + (missing ?? "-")
                    + "' is absent from window " + state.Window + "'s subtree, which drew "
                    + drawn.Count.ToString(CultureInfo.InvariantCulture) + " text node(s)");
            }
            finally
            {
                GuiMockSession.Clear("in-game-teardown", -1);
            }

            // RESTORE: the window rebuilds its real model on the next frame, which is the
            // property that makes the scope safe to end mid-boot.
            for (int i = 0; i < DrawFrames; i++) yield return null;
            InGameAssert.IsFalse(GuiMockSession.IsLive, "the scope did not clear");
        }

        private static Action InstallForProbe(ParsekUI ui, GuiMockState state,
                                              GuiMockPayload payload)
        {
            if (string.Equals(state.Window, GuiMockSession.KerbalsWindow,
                              StringComparison.Ordinal))
            {
                KerbalsWindowUI w = ui.GetKerbalsUI();
                bool wasOpen = w.IsOpen;
                int wasTab = w.SelectedTabForTesting;
                w.IsOpen = true;
                w.SelectedTabForTesting = state.Tab == "outcomes" ? 1 : 0;
                w.CachedViewModelForTesting = payload.Kerbals;
                var expanded = new List<string>();
                for (int i = 0; i < state.ExpandKeys.Length; i++)
                {
                    if (w.SetRosterExpandedForTesting(state.ExpandKeys[i], true))
                        expanded.Add(state.ExpandKeys[i]);
                }
                return () =>
                {
                    for (int i = 0; i < expanded.Count; i++)
                        w.SetRosterExpandedForTesting(expanded[i], false);
                    w.CachedViewModelForTesting = null;
                    w.SelectedTabForTesting = wasTab;
                    w.IsOpen = wasOpen;
                };
            }
            if (string.Equals(state.Window, GuiMockSession.CareerWindow,
                              StringComparison.Ordinal))
            {
                CareerStateWindowUI w = ui.GetCareerStateUI();
                bool wasOpen = w.IsOpen;
                int wasTab = w.SelectedTabForTesting;
                w.IsOpen = true;
                w.SelectedTabForTesting = TabIndexOf(state.Tab);
                w.CachedVMForTesting = payload.Career;
                return () =>
                {
                    w.CachedVMForTesting = null;
                    w.SelectedTabForTesting = wasTab;
                    w.IsOpen = wasOpen;
                };
            }

            StructureListWindowUI s = ui.GetStructureListUI();
            StructureListWindowUI.GalleryTargetSnapshot prev = s.CaptureGalleryTarget();
            s.OpenWithGallerySteps(payload.Structure.RouteMode, payload.Structure.Title,
                                   payload.Structure.Steps);
            return () => s.RestoreGalleryTarget(prev);
        }

        private static int TabIndexOf(string tab)
        {
            switch (tab)
            {
                case "strategies": return 1;
                case "facilities": return 2;
                case "milestones": return 3;
                default: return 0;
            }
        }

        private static int WindowIdOf(ParsekUI ui, string window)
        {
            if (string.Equals(window, GuiMockSession.KerbalsWindow,
                              StringComparison.Ordinal))
                return KerbalsWindowUI.WindowIdKey.GetHashCode();
            if (string.Equals(window, GuiMockSession.CareerWindow,
                              StringComparison.Ordinal))
                return CareerStateWindowUI.WindowIdKey.GetHashCode();
            return StructureListWindowUI.WindowIdKey.GetHashCode();
        }

        /// <summary>Every non-empty text inside ONE window's subtree. Window-scoped for
        /// the applier's reason: a whole-tree walk would accept a witness some other
        /// window happened to draw.</summary>
        private static List<string> DrawnTextsOf(GuiTreeResult tree, int windowId)
        {
            var into = new List<string>();
            GuiTreeNode windowNode = FindWindow(tree, windowId);
            if (windowNode != null) Collect(windowNode, into);
            return into;
        }

        private static GuiTreeNode FindWindow(GuiTreeResult tree, int windowId)
        {
            if (tree == null) return null;
            for (int i = 0; i < tree.Roots.Count; i++)
            {
                GuiTreeNode hit = FindWindow(tree.Roots[i], windowId);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GuiTreeNode FindWindow(GuiTreeNode node, int windowId)
        {
            if (node == null) return null;
            if (node.Kind == GuiNodeKind.Window && node.WindowId.HasValue
                && node.WindowId.Value == windowId)
            {
                return node;
            }
            for (int i = 0; i < node.Children.Count; i++)
            {
                GuiTreeNode hit = FindWindow(node.Children[i], windowId);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void Collect(GuiTreeNode node, List<string> into)
        {
            if (node == null) return;
            if (!string.IsNullOrEmpty(node.Text)) into.Add(node.Text);
            if (!string.IsNullOrEmpty(node.TextValue)) into.Add(node.TextValue);
            for (int i = 0; i < node.Children.Count; i++) Collect(node.Children[i], into);
        }
    }
}
