using System;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the automation-only two-phase <c>RevertToLaunch</c> verb; the
    /// contract is on <see cref="TestCommandRevertToLaunch"/>.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // The ParsekFlight instance the revert started from: the completion waits for a
        // different one, so it never reads the scene the revert is tearing down.
        private int revertToLaunchFromFlightInstanceId;
        // target=prelaunch (Revert to VAB / SPH): the completion waits for EDITOR instead.
        private bool revertToLaunchPrelaunch;
        private string revertToLaunchFacility;
        private int revertToLaunchEditorUpSinceFrame = -1;

        private void RevertToLaunchImpl(ParsedCommand cmd)
        {
            string targetReason = TestCommandRevertToLaunch.ParseTarget(
                ArgOrNull(cmd, TestCommandRevertToLaunch.TargetKey), out bool prelaunch);
            if (targetReason != null)
            {
                ParsekLog.Warn(Tag, $"reverttolaunch rejected reason={targetReason}");
                SetExecResult("REJECTED", null, targetReason);
                return;
            }
            ParsekScenario scenario = ParsekScenario.Instance;
            bool markerLive = scenario != null && scenario.ActiveReFlySessionMarker != null;
            Game game = HighLogic.CurrentGame;
            if (prelaunch)
            {
                RevertToPrelaunchImpl(markerLive, game);
                return;
            }
            revertToLaunchPrelaunch = false;
            bool canRestart = game != null && game.Parameters != null && game.Parameters.Flight != null
                && game.Parameters.Flight.CanRestart;
            string reason = TestCommandRevertToLaunch.DecideGate(markerLive,
                FlightDriver.CanRevertToPostInit, FlightDriver.PostInitState != null, canRestart);
            if (reason != null)
            {
                ParsekLog.Warn(Tag, $"reverttolaunch rejected reason={reason} markerLive={Bool(markerLive)} " +
                    $"canRevertToPostInit={Bool(FlightDriver.CanRevertToPostInit)} " +
                    $"postInitState={Bool(FlightDriver.PostInitState != null)} canRestart={Bool(canRestart)}");
                SetExecResult("REJECTED", null, reason);
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            Vessel v = FlightGlobals.ActiveVessel;
            revertToLaunchFromFlightInstanceId = flight != null ? flight.GetInstanceID() : 0;
            ParsekLog.Info(Tag, TestCommandRevertToLaunch.FormatStartLine(
                v != null ? v.persistentId : 0u, v != null ? v.vesselName : null,
                v != null ? v.situation.ToString() : null,
                flight != null && flight.IsRecording, flight != null && flight.HasActiveTree));
            try
            {
                // The Esc menu's Revert Flight > Revert to Launch callback.
                FlightDriver.RevertToLaunch();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "reverttolaunch error reason=" + TestCommandRevertToLaunch.ThrewReason
                    + $" {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, TestCommandRevertToLaunch.ThrewReason);
                return;
            }
            SetExecResult(PendingVerdict, null, null);
        }

        // Esc > Revert Flight > Revert to VAB / SPH, outside a Re-Fly: stock writes its
        // PreLaunchState to persistent.sfs, fires OnRevertToPrelaunchFlightState and starts the
        // editor, which reloads that file.
        private void RevertToPrelaunchImpl(bool markerLive, Game game)
        {
            bool canLeaveToEditor = game != null && game.Parameters != null && game.Parameters.Flight != null
                && game.Parameters.Flight.CanLeaveToEditor;
            EditorFacility shipType = ShipConstruction.ShipType;
            bool shipTypeIsEditor = shipType == EditorFacility.VAB || shipType == EditorFacility.SPH;
            bool hasShipConfig = ShipConstruction.ShipConfig != null;
            string reason = TestCommandRevertToLaunch.DecidePrelaunchGate(markerLive, canLeaveToEditor,
                FlightDriver.CanRevertToPrelaunch, hasShipConfig, shipTypeIsEditor);
            if (reason != null)
            {
                ParsekLog.Warn(Tag, $"reverttolaunch rejected reason={reason} target=prelaunch " +
                    $"markerLive={Bool(markerLive)} canLeaveToEditor={Bool(canLeaveToEditor)} " +
                    $"canRevertToPrelaunch={Bool(FlightDriver.CanRevertToPrelaunch)} " +
                    $"shipConfig={Bool(hasShipConfig)} shipType={shipType}");
                SetExecResult("REJECTED", null, reason);
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            Vessel v = FlightGlobals.ActiveVessel;
            revertToLaunchPrelaunch = true;
            revertToLaunchFacility = shipType.ToString();
            revertToLaunchEditorUpSinceFrame = -1;
            revertToLaunchFromFlightInstanceId = flight != null ? flight.GetInstanceID() : 0;
            ParsekLog.Info(Tag, TestCommandRevertToLaunch.FormatStartLine(
                v != null ? v.persistentId : 0u, v != null ? v.vesselName : null,
                v != null ? v.situation.ToString() : null,
                flight != null && flight.IsRecording, flight != null && flight.HasActiveTree,
                prelaunch: true) + " facility=" + shipType);
            try
            {
                FlightDriver.RevertToPrelaunch(shipType);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "reverttolaunch error reason=" + TestCommandRevertToLaunch.ThrewReason
                    + $" target=prelaunch {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, TestCommandRevertToLaunch.ThrewReason);
                return;
            }
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteRevertToPrelaunch(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandRevertToLaunch.Verb);
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool gameLoaded = HighLogic.CurrentGame != null;
            EditorLogic editor = HighLogic.LoadedScene == GameScenes.EDITOR ? EditorLogic.fetch : null;
            bool editorUp = editor != null && editor.ship != null && EditorDriver.fetch != null
                            && !EditorPanelsTransitioning();
            if (!editorUp) revertToLaunchEditorUpSinceFrame = -1;
            else if (revertToLaunchEditorUpSinceFrame < 0) revertToLaunchEditorUpSinceFrame = Time.frameCount;
            int upFrames = editorUp ? Time.frameCount - revertToLaunchEditorUpSinceFrame : 0;
            bool revertConsumed = RevertDetector.PendingKind == RevertKind.None;
            LoadCompletionDecision decision = TestCommandRevertToLaunch.DecidePrelaunchCompletion(
                elapsed, scene, gameLoaded, editorUp, upFrames, revertConsumed, budget);
            if (decision == LoadCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            ClearTwoPhase();
            revertToLaunchPrelaunch = false;
            string el = elapsed.ToString("F1", CultureInfo.InvariantCulture);
            switch (decision)
            {
                case LoadCompletionDecision.CompleteOk:
                {
                    string ship = editor.ship.shipName;
                    ParsekLog.Info(Tag, TestCommandRevertToLaunch.FormatPrelaunchCompleteLine(
                        revertToLaunchFacility, ship, elapsed));
                    EmitExecutedTerminal(id, seq, verb, "OK",
                        TestCommandRevertToLaunch.BuildPrelaunchCompletePayload(
                            HighLogic.LoadedScene.ToString(), revertToLaunchFacility, ship),
                        null, dequeueHead: true);
                    break;
                }
                case LoadCompletionDecision.LoadFailedMenu:
                    ParsekLog.Error(Tag, $"reverttolaunch failed-returned-to-menu target=prelaunch elapsed={el}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRevertToLaunch.ReturnedToMenuReason, dequeueHead: true);
                    break;
                default:
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandRevertToLaunch.TimeoutReason);
                    ParsekLog.Error(Tag, $"reverttolaunch timeout target=prelaunch scene={scene} " +
                        $"editorUp={Bool(editorUp)} upFrames={Int(upFrames)} revertConsumed={Bool(revertConsumed)} " +
                        $"elapsed={el}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRevertToLaunch.TimeoutReason, dequeueHead: true);
                    break;
            }
        }

        private void TryCompleteRevertToLaunch(double now)
        {
            if (revertToLaunchPrelaunch)
            {
                TryCompleteRevertToPrelaunch(now);
                return;
            }
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandRevertToLaunch.Verb);
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool gameLoaded = HighLogic.CurrentGame != null;
            ParsekFlight flight = scene == TestCommandScene.Flight ? ParsekFlight.Instance : null;
            bool changed = TestCommandLoadGame.IsFlightReadyForThisLoad(
                flight != null ? flight.GetInstanceID() : 0, revertToLaunchFromFlightInstanceId,
                flight != null && flight.FlightReadyObserved);
            Vessel active = changed ? FlightGlobals.ActiveVessel : null;
            bool activeReady = active != null && active.loaded && !active.packed;
            LoadCompletionDecision decision = TestCommandRevertToLaunch.DecideCompletion(
                elapsed, scene, gameLoaded, changed, activeReady, budget);
            if (decision == LoadCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            ClearTwoPhase();
            string el = elapsed.ToString("F1", CultureInfo.InvariantCulture);
            switch (decision)
            {
                case LoadCompletionDecision.CompleteOk:
                {
                    string situation = active.situation.ToString();
                    ParsekLog.Info(Tag, TestCommandRevertToLaunch.FormatCompleteLine(
                        active.persistentId, active.vesselName, situation, elapsed));
                    EmitExecutedTerminal(id, seq, verb, "OK",
                        TestCommandRevertToLaunch.BuildCompletePayload(HighLogic.LoadedScene.ToString(),
                            active.persistentId, active.vesselName, situation),
                        null, dequeueHead: true);
                    break;
                }
                case LoadCompletionDecision.LoadFailedMenu:
                    ParsekLog.Error(Tag, $"reverttolaunch failed-returned-to-menu elapsed={el}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRevertToLaunch.ReturnedToMenuReason, dequeueHead: true);
                    break;
                default:
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandRevertToLaunch.TimeoutReason);
                    ParsekLog.Error(Tag, $"reverttolaunch timeout scene={scene} flightChanged={Bool(changed)} " +
                        $"activeReady={Bool(activeReady)} elapsed={el}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRevertToLaunch.TimeoutReason, dequeueHead: true);
                    break;
            }
        }
    }
}
