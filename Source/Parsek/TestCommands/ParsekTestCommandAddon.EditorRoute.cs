using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two editor scene-route verbs, <c>GoToEditor</c> and
    /// <c>LaunchFromEditor</c>. Every decision is in the pure
    /// <see cref="TestCommandEditorRoute"/>; this file only samples live state, reaches the
    /// editor through stock's own entry points, and polls the settled scene.
    ///
    /// <para>STOCK PATHS, NOT SHORTCUTS. The Space Center building's own click handler
    /// (<c>SpaceCenterBuilding.OnLeftClick</c>: damage check, <c>EnterBuilding</c> ->
    /// <c>OnClicked</c>, which saves persistent and calls <c>EditorDriver.StartEditor</c>)
    /// enters the editor; the craft browser's Normal-load body
    /// (<c>EditorLogic.LoadShipFromFile</c>) loads a craft; the Launch button's own
    /// handler (<c>EditorLogic.launchVessel</c>, which runs stock's pre-flight checks and
    /// then <c>FlightDriver.StartWithNewLaunch</c>) launches. Parsek sees exactly the scene
    /// sequence a player's clicks produce.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private sealed class GoToEditorPending
        {
            internal EditorRouteFacility Facility;
            internal string Craft;
            internal string CraftPath;
            internal string ExpectedShipName;
            internal bool LoadIssued;
            internal int PhaseStartFrame;
        }

        private GoToEditorPending goToEditorPending;
        private string launchFromEditorSite;

        // ----- GoToEditor (two-phase) -----

        private void GoToEditorImpl(ParsedCommand cmd)
        {
            string sceneName = HighLogic.LoadedScene.ToString();
            if (!TestCommandEditorRoute.TryParseGoToEditor(
                    ArgOrNull(cmd, TestCommandEditorRoute.FacilityArg),
                    ArgOrNull(cmd, TestCommandEditorRoute.CraftArg),
                    out EditorRouteFacility facility, out string craft,
                    out string reject, out string detail))
            {
                ParsekLog.Warn(Tag, $"goeditor rejected reason={reject} {detail}");
                SetExecResult("REJECTED", null, reject + " " + detail);
                return;
            }

            if (MapScene(HighLogic.LoadedScene) != TestCommandScene.SpaceCenter)
            {
                ParsekLog.Warn(Tag, $"goeditor rejected reason={TestCommandEditorRoute.GoWrongSceneReason} scene={sceneName} " +
                    "- the editor buildings are entered from the Space Center");
                SetExecResult("REJECTED", null, TestCommandEditorRoute.GoWrongSceneReason + " scene=" + sceneName);
                return;
            }

            string craftPath = null;
            string expectedShip = null;
            if (craft != null)
            {
                string root = KSPUtil.ApplicationRootPath;
                string saveShips = Path.Combine(Path.Combine(Path.Combine(root, "saves"), HighLogic.SaveFolder), "Ships");
                string stockShips = Path.Combine(root, "Ships");
                craftPath = TestCommandEditorRoute.ResolveCraftPath(
                    saveShips, stockShips, facility, craft, File.Exists);
                if (craftPath == null)
                {
                    int tried = TestCommandEditorRoute.CraftCandidates(saveShips, stockShips, facility, craft).Count;
                    ParsekLog.Warn(Tag, $"goeditor rejected reason={TestCommandEditorRoute.CraftNotFoundReason} craft={craft} candidates={Int(tried)}");
                    SetExecResult("REJECTED", null, TestCommandEditorRoute.CraftNotFoundReason + " craft=" + craft);
                    return;
                }
                try
                {
                    ConfigNode craftNode = ConfigNode.Load(craftPath);
                    // Stock craft name themselves with a localization tag
                    // (`ship = #autoLOC_501232`); the editor's ShipConstruct carries the
                    // localized name, so compare against the formatted string.
                    string header = craftNode != null ? craftNode.GetValue("ship") : null;
                    expectedShip = string.IsNullOrEmpty(header) ? null : KSP.Localization.Localizer.Format(header);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, $"goeditor craft header unreadable path={craftPath}: {ex.GetType().Name} - readiness falls back to a non-empty ship");
                }
            }

            SpaceCenterBuilding building = facility == EditorRouteFacility.VAB
                ? (SpaceCenterBuilding)Object.FindObjectOfType<VehicleAssemblyBuilding>()
                : Object.FindObjectOfType<SpacePlaneHangarBuilding>();
            if (building == null)
            {
                ParsekLog.Warn(Tag, $"goeditor rejected reason={TestCommandEditorRoute.BuildingNotFoundReason} facility={TestCommandEditorRoute.FacilityToken(facility)}");
                SetExecResult("REJECTED", null, TestCommandEditorRoute.BuildingNotFoundReason
                    + " facility=" + TestCommandEditorRoute.FacilityToken(facility));
                return;
            }

            // The three states in which the building's own click opens a modal instead of
            // the editor: the facility locked by the game parameters (the "FacilityLocked"
            // popup), a mission/scenario that closes it, and a building damaged past the
            // 70% threshold (OnLeftClick opens the repair context menu). None is
            // answerable by a seam verb, so refuse before clicking.
            GameParameters.SpaceCenterParams sc = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Parameters.SpaceCenter : null;
            bool allowed = sc == null || (facility == EditorRouteFacility.VAB ? sc.CanGoInVAB : sc.CanGoInSPH);
            float damage = building.GetStructureDamage();
            if (!building.IsOpen() || !allowed || damage >= 70f)
            {
                string why = "open=" + Bool(building.IsOpen()) + " allowed=" + Bool(allowed)
                    + " damage=" + damage.ToString("F0", CultureInfo.InvariantCulture);
                ParsekLog.Warn(Tag, $"goeditor rejected reason={TestCommandEditorRoute.FacilityClosedReason} " +
                    $"facility={TestCommandEditorRoute.FacilityToken(facility)} {why}");
                SetExecResult("REJECTED", null, TestCommandEditorRoute.FacilityClosedReason + " " + why);
                return;
            }

            ParsekLog.Info(Tag, TestCommandEditorRoute.FormatGoStartLine(facility, craft, sceneName, craftPath));

            // The mouse click on the building: stock's damage check, then EnterBuilding ->
            // OnClicked, which writes persistent.sfs and loads the EDITOR scene.
            building.OnLeftClick();

            goToEditorPending = new GoToEditorPending
            {
                Facility = facility,
                Craft = craft,
                CraftPath = craftPath,
                ExpectedShipName = expectedShip,
                LoadIssued = false,
                PhaseStartFrame = Time.frameCount,
            };
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteGoToEditor(double now)
        {
            GoToEditorPending p = goToEditorPending;
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandEditorRoute.GoToEditorVerb);
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            if (p == null)
            {
                FinishGoToEditor(null, "ERROR", TestCommandEditorRoute.GoNotSettledReason + " no-pending-state", elapsed);
                return;
            }

            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            EditorLogic editor = HighLogic.LoadedScene == GameScenes.EDITOR ? EditorLogic.fetch : null;
            bool editorUp = editor != null && editor.ship != null && EditorDriver.fetch != null
                            && !EditorPanelsTransitioning();
            int parts = editor != null && editor.ship != null ? editor.ship.parts.Count : 0;
            bool onStage = p.LoadIssued && parts > 0
                && (string.IsNullOrEmpty(p.ExpectedShipName)
                    || string.Equals(editor.ship.shipName, p.ExpectedShipName, StringComparison.Ordinal));

            GoToEditorPollOutcome outcome = TestCommandEditorRoute.DecideGoToEditorPoll(
                scene, editorUp, p.Craft != null, p.LoadIssued, onStage,
                Time.frameCount - p.PhaseStartFrame, expired);

            switch (outcome)
            {
                case GoToEditorPollOutcome.NotYet:
                    return;
                case GoToEditorPollOutcome.IssueCraftLoad:
                    ParsekLog.Info(Tag, $"goeditor loading craft={p.Craft} path={p.CraftPath} " +
                        $"facility={TestCommandEditorRoute.FacilityToken(p.Facility)} editorParts={Int(parts)} " +
                        "- through the craft browser's Normal load (EditorLogic.LoadShipFromFile)");
                    EditorLogic.LoadShipFromFile(p.CraftPath);
                    p.LoadIssued = true;
                    p.PhaseStartFrame = Time.frameCount;
                    return;
                case GoToEditorPollOutcome.ReturnedToMenu:
                    FinishGoToEditor(p, "ERROR", TestCommandEditorRoute.GoReturnedToMenuReason, elapsed);
                    return;
                case GoToEditorPollOutcome.TimedOut:
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, elapsed, TestCommandEditorRoute.GoNotSettledReason);
                    FinishGoToEditor(p, "ERROR", TestCommandEditorRoute.GoNotSettledReason
                        + " scene=" + HighLogic.LoadedScene + " editorUp=" + Bool(editorUp)
                        + " loadIssued=" + Bool(p.LoadIssued) + " parts=" + Int(parts)
                        + " ship=" + (editor != null && editor.ship != null ? (editor.ship.shipName ?? "-").Replace(' ', '_') : "-")
                        + " expected=" + (p.ExpectedShipName ?? "-").Replace(' ', '_'), elapsed);
                    return;
                default:
                    FinishGoToEditor(p, "OK", null, elapsed);
                    return;
            }
        }

        private void FinishGoToEditor(GoToEditorPending p, string verdict, string msg, double elapsed)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            goToEditorPending = null;
            ClearTwoPhase();
            if (verdict != "OK" || p == null)
            {
                ParsekLog.Error(Tag, $"goeditor error {msg} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, msg, dequeueHead: true);
                return;
            }
            EditorLogic editor = EditorLogic.fetch;
            int parts = editor != null && editor.ship != null ? editor.ship.parts.Count : 0;
            string shipName = editor != null && editor.ship != null ? editor.ship.shipName : null;
            string sceneName = HighLogic.LoadedScene.ToString();
            ParsekLog.Info(Tag, TestCommandEditorRoute.FormatGoCompleteLine(
                    p.Facility, p.Craft, sceneName, parts, shipName, elapsed));
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandEditorRoute.BuildGoPayload(p.Facility, p.Craft, sceneName, parts, shipName),
                null, dequeueHead: true);
        }

        // ----- LaunchFromEditor (two-phase) -----

        // One live look at everything the launch gate reads.
        private sealed class LaunchGateSample
        {
            internal string SceneName;
            internal EditorLogic Editor;
            internal int Parts;
            internal bool Unlocked;
            internal string Site;
            internal int Obstructing;
            internal string Obstructor;
            internal LaunchFromEditorGate Gate;
            internal bool LockIsOnlyBlocker;
        }

        // Set while the verb waits out an EDITOR_LAUNCH lock that is the only blocker; the
        // Launch button has NOT been pressed yet. Cleared once it is pressed or refused.
        private bool launchFromEditorLockWait;
        private string launchFromEditorSiteArg;
        private int launchFromEditorLockWaitStartFrame;

        private LaunchGateSample SampleLaunchGate(string siteArg)
        {
            var g = new LaunchGateSample();
            g.SceneName = HighLogic.LoadedScene.ToString();
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            g.Editor = scene == TestCommandScene.Editor ? EditorLogic.fetch : null;
            g.Parts = g.Editor != null && g.Editor.ship != null ? g.Editor.ship.parts.Count : 0;
            g.Unlocked = InputLockManager.IsUnlocked(ControlTypes.EDITOR_LAUNCH);

            g.Site = siteArg ?? (g.Editor != null ? g.Editor.launchSiteName : null);
            bool siteValid = !string.IsNullOrEmpty(g.Site) && EditorDriver.ValidLaunchSite(g.Site);
            if (siteValid && HighLogic.CurrentGame != null && HighLogic.CurrentGame.flightState != null)
            {
                // Stock's own obstruction predicate - LaunchSiteClear.Test calls the same
                // ShipConstruction.FindVesselsLandedAt against the same flight state.
                List<ProtoVessel> found = ShipConstruction.FindVesselsLandedAt(HighLogic.CurrentGame.flightState, g.Site);
                g.Obstructing = found != null ? found.Count : 0;
                if (g.Obstructing > 0 && found[0] != null) g.Obstructor = found[0].vesselName;
            }

            g.Gate = TestCommandEditorRoute.DecideLaunchGate(
                scene, g.Editor != null, g.Parts, g.Unlocked, siteValid, g.Obstructing);
            g.LockIsOnlyBlocker = TestCommandEditorRoute.LaunchLockIsOnlyBlocker(
                scene, g.Editor != null, g.Parts, g.Unlocked, siteValid, g.Obstructing);
            return g;
        }

        // The refusal detail (the sampled state), plus the EDITOR_LAUNCH holders from
        // stock's lockStack when the lock is what refused. The response msg is the gate
        // reason, a space, then this.
        private static string LaunchRefusalDetail(LaunchGateSample g, double waitedSeconds)
        {
            string msg = "scene=" + g.SceneName + " parts=" + Int(g.Parts) + " unlocked=" + Bool(g.Unlocked)
                + " site=" + (g.Site ?? "-") + " obstructing=" + Int(g.Obstructing)
                + (g.Obstructor != null ? " first=" + g.Obstructor.Replace(' ', '_') : string.Empty);
            if (g.Gate == LaunchFromEditorGate.LaunchLocked)
            {
                msg += " " + TestCommandEditorRoute.FormatLockHolders(
                    InputLockManager.lockStack, TestCommandEditorRoute.EditorLaunchLockBit,
                    TestCommandEditorRoute.MaxLockHoldersListed);
                if (waitedSeconds > 0)
                    msg += " waited=" + waitedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
            }
            return msg;
        }

        private void LaunchFromEditorImpl(ParsedCommand cmd)
        {
            string siteArg = ArgOrNull(cmd, TestCommandEditorRoute.SiteArg);
            LaunchGateSample g = SampleLaunchGate(siteArg);
            LaunchLockWaitOutcome outcome = TestCommandEditorRoute.DecideLaunchLockWait(
                g.Gate, g.LockIsOnlyBlocker, 0.0, TestCommandEditorRoute.LaunchLockWaitSeconds);

            if (outcome == LaunchLockWaitOutcome.Refuse)
            {
                string reason = TestCommandEditorRoute.GateReason(g.Gate);
                string why = LaunchRefusalDetail(g, 0.0);
                ParsekLog.Warn(Tag, $"launchfromeditor refused reason={reason} gate={g.Gate} {why}");
                SetExecResult("REJECTED", null, reason + " " + why);
                return;
            }

            if (outcome == LaunchLockWaitOutcome.Wait)
            {
                // Only the EDITOR_LAUNCH lock blocks. Go two-phase WITHOUT pressing Launch;
                // TryCompleteLaunchFromEditor re-samples the gate every frame and presses
                // the button the frame the lock clears, or refuses at the window's end.
                launchFromEditorLockWait = true;
                launchFromEditorSiteArg = siteArg;
                launchFromEditorLockWaitStartFrame = Time.frameCount;
                ParsekLog.Verbose(Tag, "launchfromeditor lock-wait start - EDITOR_LAUNCH is the only blocker; " +
                    "re-checking every frame for up to "
                    + TestCommandEditorRoute.LaunchLockWaitSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s "
                    + TestCommandEditorRoute.FormatLockHolders(
                        InputLockManager.lockStack, TestCommandEditorRoute.EditorLaunchLockBit,
                        TestCommandEditorRoute.MaxLockHoldersListed));
                SetExecResult(PendingVerdict, null, null);
                return;
            }

            PressLaunch(g, siteArg);
            SetExecResult(PendingVerdict, null, null);
        }

        private void PressLaunch(LaunchGateSample g, string siteArg)
        {
            VesselCrewManifest manifest = CrewAssignmentDialog.Instance != null
                ? CrewAssignmentDialog.Instance.GetManifest() : null;
            int crew = manifest != null ? manifest.CrewCount : 0;
            ParsekLog.Info(Tag, TestCommandEditorRoute.FormatLaunchStartLine(
                    g.Editor.ship.shipName, g.Parts, g.Site, crew, g.SceneName));

            launchFromEditorSite = g.Site;
            // The Launch button's handler (launchBtn.onClick -> launchVessel()), or the
            // launch-site picker's launchVessel(siteName) when the spec chose a site.
            if (siteArg != null) g.Editor.launchVessel(siteArg);
            else g.Editor.launchVessel();
        }

        // The lock-wait poll: one gate sample per frame until it opens (press Launch, then
        // the FLIGHT wait below runs on later frames), another blocker appears, or the
        // window ends (REJECTED with the gate's reason - nothing was clicked).
        private void TryCompleteLaunchLockWait(double now)
        {
            double waited = now - completionStartedAt;
            string siteArg = launchFromEditorSiteArg;
            LaunchGateSample g = SampleLaunchGate(siteArg);
            LaunchLockWaitOutcome outcome = TestCommandEditorRoute.DecideLaunchLockWait(
                g.Gate, g.LockIsOnlyBlocker, waited, TestCommandEditorRoute.LaunchLockWaitSeconds);
            if (outcome == LaunchLockWaitOutcome.Wait)
                return;

            int frames = Time.frameCount - launchFromEditorLockWaitStartFrame;
            launchFromEditorLockWait = false;
            launchFromEditorSiteArg = null;

            if (outcome == LaunchLockWaitOutcome.Proceed)
            {
                ParsekLog.Info(Tag, TestCommandEditorRoute.FormatLockWaitClearedLine(waited, frames));
                PressLaunch(g, siteArg);
                return;
            }

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            launchFromEditorSite = null;
            ClearTwoPhase();
            string reason = TestCommandEditorRoute.GateReason(g.Gate);
            string why = LaunchRefusalDetail(g, waited);
            ParsekLog.Warn(Tag, $"launchfromeditor refused reason={reason} gate={g.Gate} " +
                $"after lock-wait frames={Int(frames)} {why}");
            EmitExecutedTerminal(id, seq, verb, "REJECTED", null, reason + " " + why, dequeueHead: true);
        }

        private void TryCompleteLaunchFromEditor(double now)
        {
            if (launchFromEditorLockWait)
            {
                TryCompleteLaunchLockWait(now);
                return;
            }
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandEditorRoute.LaunchFromEditorVerb);
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool gameLoaded = HighLogic.CurrentGame != null;
            Vessel active = scene == TestCommandScene.Flight ? ReadActiveVesselForEditorRoute() : null;
            bool activeReady = active != null && active.loaded;
            LoadCompletionDecision decision = TestCommandEditorRoute.DecideLaunchCompletion(
                elapsed, scene, gameLoaded, activeReady, budget);
            if (decision == LoadCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string site = launchFromEditorSite;
            launchFromEditorSite = null;
            ClearTwoPhase();
            string el = elapsed.ToString("F1", CultureInfo.InvariantCulture);

            switch (decision)
            {
                case LoadCompletionDecision.CompleteOk:
                {
                    string situation = active.situation.ToString();
                    ParsekLog.Info(Tag, TestCommandEditorRoute.FormatLaunchCompleteLine(
                            active.vesselName, active.persistentId, site, situation, elapsed));
                    EmitExecutedTerminal(id, seq, verb, "OK",
                        TestCommandEditorRoute.BuildLaunchPayload(active.vesselName, active.persistentId, site, situation),
                        null, dequeueHead: true);
                    break;
                }
                case LoadCompletionDecision.LoadFailedMenu:
                    ParsekLog.Error(Tag, $"launchfromeditor failed-returned-to-menu elapsed={el}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandEditorRoute.LaunchReturnedToMenuReason, dequeueHead: true);
                    break;
                default:
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandEditorRoute.LaunchNotSettledReason);
                    ParsekLog.Error(Tag, $"launchfromeditor timeout scene={HighLogic.LoadedScene} activeVessel={Bool(active != null)} " +
                        $"elapsed={el}s - the launch never reached FLIGHT (a stock pre-flight dialog the gate could not foresee, " +
                        "or a foreign LoadScene prefix)");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandEditorRoute.LaunchNotSettledReason + " scene=" + HighLogic.LoadedScene, dequeueHead: true);
                    break;
            }
        }

        // FlightGlobals is read only here, in a method of its own, so a headless caller of
        // the pure half never JITs a FlightGlobals reference.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Vessel ReadActiveVesselForEditorRoute()
        {
            if (!FlightGlobals.ready) return null;
            return FlightGlobals.ActiveVessel;
        }
    }
}
