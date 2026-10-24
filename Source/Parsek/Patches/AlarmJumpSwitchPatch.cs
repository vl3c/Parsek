using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// The stock Alarm Clock's fire dialog has a Jump button that switches to the alarm's
    /// vessel (GC-13). Decompiled <c>AlarmClockMessageDialog.OnJump</c> (KSP 1.12.5): in FLIGHT
    /// it calls <c>FlightGlobals.SetActiveVessel(alarm.Vessel)</c>; in any other scene it calls
    /// <c>GamePersistence.SaveGame("persistent", ...)</c> and then
    /// <c>FlightDriver.StartAndFocusVessel("persistent", FlightGlobals.Vessels.IndexOf(alarm.Vessel))</c>,
    /// the same save-and-index switch as the Tracking Station's Fly. <c>Spawn</c> shows the
    /// button whenever the alarm's pid is a live vessel other than the active one.
    ///
    /// <para>
    /// None of Parsek's switch patches sits on that path, so the Prefix routes it through
    /// the same rules as the stock controls it imitates:
    /// a ghost map vessel is never flown (an alarm set on a ghost's orbit node);
    /// during a Re-Fly session only the session's vessels are reachable
    /// (<see cref="ReFlySwitchGate"/>, design section 2.12 Rule 3);
    /// in FLIGHT the switch takes the map Switch-To gates
    /// (<see cref="MapFocusObjectOnSelectPatch.PrepareOwnedVesselSwitch"/>: the pre-switch
    /// Merge / Discard dialog and the intent arm), because stock switches with the same
    /// <c>SetActiveVessel</c>;
    /// outside FLIGHT the ghosts are stripped from the live list before stock's
    /// <c>IndexOf</c> + save (the saved file never holds them, so a ghost ahead of the target
    /// would land the player in the wrong vessel) and the scene's Fly intent is armed, as the
    /// Tracking Station and KSC marker Fly patches do.
    /// </para>
    ///
    /// <para>
    /// The <c>Spawn</c> Postfix hides the button for an alarm the Prefix would refuse (stock's
    /// own way of saying the button does not apply), so the mark and the block read the same
    /// predicate.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(AlarmClockMessageDialog), "OnJump")]
    internal static class AlarmJumpSwitchPatch
    {
        internal const string Tag = "AlarmJump";
        internal const string IntentLabel = "Alarm Jump";

        internal enum JumpDecision
        {
            LeaveToStock,
            RefuseGhost,
            RefuseReFly,
        }

        /// <summary>Pure: what a Jump to the alarm's vessel may do.</summary>
        internal static JumpDecision Decide(bool targetIsGhost, bool reFlyRefused)
        {
            if (targetIsGhost) return JumpDecision.RefuseGhost;
            if (reFlyRefused) return JumpDecision.RefuseReFly;
            return JumpDecision.LeaveToStock;
        }

        /// <summary>
        /// Pure: the Fly intent a non-FLIGHT Jump arms, mirroring the scene's own Fly control.
        /// False for a scene with no Fly intent (the editor, the main menu).
        /// </summary>
        internal static bool TryMapSceneToIntent(
            GameScenes scene, out StockActionType action, out StockActionSourceScene source)
        {
            switch (scene)
            {
                case GameScenes.TRACKSTATION:
                    action = StockActionType.TrackingStationFly;
                    source = StockActionSourceScene.TrackingStation;
                    return true;
                case GameScenes.SPACECENTER:
                    action = StockActionType.KscMarkerFly;
                    source = StockActionSourceScene.SpaceCenter;
                    return true;
                default:
                    action = StockActionType.TrackingStationFly;
                    source = StockActionSourceScene.TrackingStation;
                    return false;
            }
        }

        internal static AlarmTypeBase ReadAlarm(AlarmClockMessageDialog dialog)
        {
            if (dialog == null) return null;
            try
            {
                return Traverse.Create(dialog).Field("alarm").GetValue<AlarmTypeBase>();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Alarm read failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The live decision for <paramref name="targetPid"/>.</summary>
        internal static JumpDecision DecideLive(uint targetPid, bool log)
        {
            bool ghost = GhostMapPresence.IsGhostMapVessel(targetPid);
            bool refused = !ghost
                && ReFlySwitchGate.IsRefused(targetPid, ReFlySwitchGate.PathAlarmJump, log);
            return Decide(ghost, refused);
        }

        static bool Prefix(AlarmClockMessageDialog __instance, out Guid __state)
        {
            __state = default(Guid);
            if (ParsekGameModeGate.CheckInert("AlarmJumpSwitchPatch.Prefix")) return true; // S9 game-mode gate

            AlarmTypeBase alarm = ReadAlarm(__instance);
            Vessel target = alarm != null ? alarm.Vessel : null;
            if (target == null)
                return true; // stock logs its own "Vessel reference is null" and returns

            uint pid = target.persistentId;
            bool inFlight = HighLogic.LoadedSceneIsFlight;
            // In FLIGHT the shared Switch-To gates run the Re-Fly refusal (and log it) below.
            JumpDecision decision = DecideLive(pid, log: !inFlight);
            if (decision == JumpDecision.RefuseGhost)
            {
                ParsekLog.Info(Tag,
                    $"Jump refused: alarm vessel is a ghost map vessel pid={pid} " +
                    $"name='{target.vesselName}' scene={HighLogic.LoadedScene}");
                return false;
            }
            if (!inFlight && decision == JumpDecision.RefuseReFly)
                return false;

            if (inFlight)
            {
                bool runStock = MapFocusObjectOnSelectPatch.PrepareOwnedVesselSwitch(
                    target, ReFlySwitchGate.PathAlarmJump, IntentLabel, out __state);
                ParsekLog.Info(Tag,
                    $"Jump in FLIGHT targetPid={pid} runStock={runStock} " +
                    $"intentArmed={__state != Guid.Empty}");
                return runStock;
            }

            GhostMapPresence.RemoveAllGhostVesselsBeforeStockFly(pid);

            StockActionType action;
            StockActionSourceScene source;
            var scenario = ParsekScenario.Instance;
            if (!TryMapSceneToIntent(HighLogic.LoadedScene, out action, out source))
            {
                ParsekLog.Info(Tag,
                    $"Jump from scene={HighLogic.LoadedScene} targetPid={pid}: ghosts stripped, no Fly intent for this scene");
                return true;
            }
            if (scenario == null)
            {
                ParsekLog.Warn(Tag,
                    $"{IntentLabel} intent not armed: ParsekScenario.Instance is null targetPid={pid}");
                return true;
            }
            var marker = new StockActionIntentMarker
            {
                IntentId = Guid.NewGuid(),
                Action = action,
                TargetVesselPersistentId = pid,
                SourceScene = source,
                CapturedRealtime = Time.realtimeSinceStartup,
                CapturedUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                ProcessSessionId = ParsekProcess.ProcessSessionId,
            };
            scenario.ArmStockActionIntent(marker);
            ParsekLog.Info(Tag,
                $"{IntentLabel} intent armed: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={pid} sourceScene={marker.SourceScene} " +
                $"capturedUT={marker.CapturedUT.ToString("R", CultureInfo.InvariantCulture)}");
            return true;
        }

        static void Postfix(Guid __state)
        {
            // Only a FLIGHT Jump hands back an intent id; a non-FLIGHT Jump leaves the scene,
            // and its marker is consumed by the FLIGHT load or expires like a TS Fly marker.
            if (__state == Guid.Empty) return;
            MapFocusObjectOnSelectPatch.ClearUnconsumedIntent(__state, IntentLabel);
        }
    }

    /// <summary>
    /// Hides the alarm dialog's Jump button for an alarm whose Jump
    /// <see cref="AlarmJumpSwitchPatch"/> would refuse (stock already hides it when the
    /// vessel is not live or is the active one).
    /// </summary>
    [HarmonyPatch(typeof(AlarmClockMessageDialog), "Spawn")]
    internal static class AlarmJumpButtonMarkPatch
    {
        static void Postfix(AlarmClockMessageDialog __result)
        {
            if (ParsekGameModeGate.CheckInert("AlarmJumpButtonMarkPatch.Postfix")) return; // S9 game-mode gate
            if (__result == null) return;
            AlarmTypeBase alarm = AlarmJumpSwitchPatch.ReadAlarm(__result);
            if (alarm == null || alarm.vesselId == 0u) return;

            var decision = AlarmJumpSwitchPatch.DecideLive(alarm.vesselId, log: false);
            if (decision == AlarmJumpSwitchPatch.JumpDecision.LeaveToStock) return;

            try
            {
                Component button = Traverse.Create(__result).Field("jumpButton").GetValue<Component>();
                if (button != null)
                    button.gameObject.SetActive(false);
                ParsekLog.Info(AlarmJumpSwitchPatch.Tag,
                    $"Jump button hidden: decision={decision} alarmVesselPid={alarm.vesselId} " +
                    $"buttonFound={button != null}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(AlarmJumpSwitchPatch.Tag,
                    $"Jump button hide failed: {ex.GetType().Name}: {ex.Message} alarmVesselPid={alarm.vesselId}");
            }
        }
    }
}
