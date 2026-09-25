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

        private void LaunchFromEditorImpl(ParsedCommand cmd)
        {
            string sceneName = HighLogic.LoadedScene.ToString();
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            EditorLogic editor = scene == TestCommandScene.Editor ? EditorLogic.fetch : null;
            int parts = editor != null && editor.ship != null ? editor.ship.parts.Count : 0;
            bool unlocked = InputLockManager.IsUnlocked(ControlTypes.EDITOR_LAUNCH);

            string siteArg = ArgOrNull(cmd, TestCommandEditorRoute.SiteArg);
            string site = siteArg ?? (editor != null ? editor.launchSiteName : null);
            bool siteValid = !string.IsNullOrEmpty(site) && EditorDriver.ValidLaunchSite(site);
            int obstructing = 0;
            string obstructor = null;
            if (siteValid && HighLogic.CurrentGame != null && HighLogic.CurrentGame.flightState != null)
            {
                // Stock's own obstruction predicate - LaunchSiteClear.Test calls the same
                // ShipConstruction.FindVesselsLandedAt against the same flight state.
                List<ProtoVessel> found = ShipConstruction.FindVesselsLandedAt(HighLogic.CurrentGame.flightState, site);
                obstructing = found != null ? found.Count : 0;
                if (obstructing > 0 && found[0] != null) obstructor = found[0].vesselName;
            }

            LaunchFromEditorGate gate = TestCommandEditorRoute.DecideLaunchGate(
                scene, editor != null, parts, unlocked, siteValid, obstructing);
            if (gate != LaunchFromEditorGate.Proceed)
            {
                string reason = TestCommandEditorRoute.GateReason(gate);
                string why = "scene=" + sceneName + " parts=" + Int(parts) + " unlocked=" + Bool(unlocked)
                    + " site=" + (site ?? "-") + " obstructing=" + Int(obstructing)
                    + (obstructor != null ? " first=" + obstructor.Replace(' ', '_') : string.Empty);
                ParsekLog.Warn(Tag, $"launchfromeditor refused reason={reason} gate={gate} {why}");
                SetExecResult("REJECTED", null, reason + " " + why);
                return;
            }

            VesselCrewManifest manifest = CrewAssignmentDialog.Instance != null
                ? CrewAssignmentDialog.Instance.GetManifest() : null;
            int crew = manifest != null ? manifest.CrewCount : 0;
            ParsekLog.Info(Tag, TestCommandEditorRoute.FormatLaunchStartLine(
                    editor.ship.shipName, parts, site, crew, sceneName));

            launchFromEditorSite = site;
            // The Launch button's handler (launchBtn.onClick -> launchVessel()), or the
            // launch-site picker's launchVessel(siteName) when the spec chose a site.
            if (siteArg != null) editor.launchVessel(siteArg);
            else editor.launchVessel();
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteLaunchFromEditor(double now)
        {
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
