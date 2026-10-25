using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the automation-only <c>AlarmJump</c> verb; the contract is on
    /// <see cref="TestCommandAlarmJump"/>. SINGLE-PHASE: the alarm is added, fired, read, its
    /// Jump pressed and its dialog closed inside the call. A Jump that stock turns into a scene
    /// reload only REQUESTS the load inside the call (<c>sceneLoadRequested=true</c>).
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private const string AlarmJumpTitle = "Parsek seam AlarmJump";

        // An hour ahead: the scenario's own update loop fires an alarm whose time has passed,
        // and a second fire of the same alarm would spawn a second dialog.
        private const double AlarmJumpLeadSeconds = 3600.0;

        private void AlarmJumpImpl(ParsedCommand cmd)
        {
            string rawVessel = ArgOrNull(cmd, TestCommandAlarmJump.VesselKey);
            string rawPid = ArgOrNull(cmd, TestCommandAlarmJump.PidKey);
            string rawPress = ArgOrNull(cmd, TestCommandAlarmJump.JumpKey);
            string reason = TestCommandAlarmJump.ParseArgs(rawVessel, rawPid, rawPress,
                out string vesselName, out uint wantPid, out string press);
            if (reason != null)
            {
                RejectAlarmJump(reason, $"vessel={rawVessel ?? string.Empty} pid={rawPid ?? string.Empty} " +
                    $"jump={rawPress ?? string.Empty}");
                return;
            }

            if (AlarmClockScenario.Instance == null)
            {
                RejectAlarmJump(TestCommandAlarmJump.NoAlarmClockReason,
                    $"AlarmClockScenario.Instance is null scene={HighLogic.LoadedScene}");
                return;
            }

            // Ghost map vessels are counted as candidates: an alarm on a ghost is a case the
            // fix refuses, so the spec may name one.
            Vessel target = null;
            int matches = 0;
            List<Vessel> all = FlightGlobals.Vessels;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Vessel v = all[i];
                    if (v == null) continue;
                    bool match = vesselName != null
                        ? string.Equals(v.vesselName, vesselName, StringComparison.Ordinal)
                        : v.persistentId == wantPid;
                    if (!match) continue;
                    matches++;
                    if (target == null) target = v;
                }
            }
            string resolveReason = TestCommandAlarmJump.ClassifyMatches(matches);
            if (resolveReason != null || target == null)
            {
                RejectAlarmJump(resolveReason ?? TestCommandAlarmJump.TargetNotFoundReason,
                    $"vessel={vesselName ?? string.Empty} pid={wantPid} matches={Int(matches)} " +
                    $"vesselsScanned={(all != null ? Int(all.Count) : "0")}");
                return;
            }

            uint pid = target.persistentId;
            bool targetIsGhost = GhostMapPresence.IsGhostMapVessel(pid);
            bool targetLoaded = target.loaded;
            uint before = ActiveVesselPidOrZero();
            bool pidIsLive = FlightGlobals.PersistentVesselIds != null
                && FlightGlobals.PersistentVesselIds.Contains(pid);
            bool stockWouldShow = TestCommandAlarmJump.StockWouldShowJump(pid, pidIsLive, before);
            string decision = ParsekGameModeGate.IsInertForCurrentGame
                ? TestCommandAlarmJump.InertDecision
                : TestCommandAlarmJump.DecisionToken(AlarmJumpSwitchPatch.DecideLive(pid, log: false));
            ParsekLog.Info(Tag, $"alarmjump start targetPid={pid} vessel='{target.vesselName}' " +
                $"targetIsGhost={Bool(targetIsGhost)} targetLoaded={Bool(targetLoaded)} active={before} " +
                $"stockWouldShowJump={Bool(stockWouldShow)} decision={decision} jump={press}");

            // ----- Add the alarm (stock's own AddAlarm) -----
            var alarm = new AlarmTypeRaw
            {
                title = AlarmJumpTitle,
                description = "Automation-only alarm on " + target.vesselName,
                ut = Planetarium.GetUniversalTime() + AlarmJumpLeadSeconds,
                vesselId = pid,
                vesselName = target.vesselName,
                actions = new AlarmActions(AlarmActions.WarpEnum.DoNothing,
                    AlarmActions.MessageEnum.Yes, false, true),
            };
            bool added;
            try
            {
                added = AlarmClockScenario.AddAlarm(alarm);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"alarmjump AddAlarm threw: {ex.GetType().Name}: {ex.Message}");
                added = false;
            }
            if (!added)
            {
                FailAlarmJump(TestCommandAlarmJump.AddFailedReason, $"alarmId={alarm.Id} targetPid={pid}", alarm.Id);
                return;
            }

            // ----- Fire it (stock's own TriggerAlarm -> AlarmClockMessageDialog.Spawn) -----
            AlarmClockMessageDialog dialog = null;
            try
            {
                MethodInfo trigger = AccessTools.Method(typeof(AlarmTypeBase), "TriggerAlarm");
                if (trigger == null)
                    throw new MissingMethodException(nameof(AlarmTypeBase), "TriggerAlarm");
                trigger.Invoke(alarm, null);
                dialog = Traverse.Create(alarm).Field("dialogObject").GetValue<AlarmClockMessageDialog>();
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                FailAlarmJump(TestCommandAlarmJump.TriggerFailedReason,
                    $"{inner.GetType().Name}: {inner.Message} alarmId={alarm.Id} targetPid={pid}", alarm.Id);
                return;
            }
            if (dialog == null)
            {
                FailAlarmJump(TestCommandAlarmJump.NoDialogReason,
                    $"TriggerAlarm spawned no message dialog alarmId={alarm.Id} targetPid={pid}", alarm.Id);
                return;
            }

            // ----- Read the Jump button as the player sees it (after Parsek's Spawn postfix) -----
            bool jumpShown = false;
            try
            {
                Component button = Traverse.Create(dialog).Field("jumpButton").GetValue<Component>();
                jumpShown = button != null && button.gameObject.activeSelf;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"alarmjump jump button read threw: {ex.GetType().Name}: {ex.Message}");
            }

            // ----- Press Jump (stock's own OnJump, Harmony-patched) -----
            bool pressed = TestCommandAlarmJump.ShouldPress(press, jumpShown);
            bool sceneLoadRequested = false;
            if (pressed)
            {
                EventData<GameScenes>.OnEvent onLoad = scene => sceneLoadRequested = true;
                GameEvents.onGameSceneLoadRequested.Add(onLoad);
                try
                {
                    MethodInfo onJump = AccessTools.Method(typeof(AlarmClockMessageDialog), "OnJump");
                    if (onJump == null)
                        throw new MissingMethodException(nameof(AlarmClockMessageDialog), "OnJump");
                    ParsekLog.Info(Tag, $"alarmjump press OnJump alarmId={alarm.Id} targetPid={pid} " +
                        $"jumpShown={Bool(jumpShown)}");
                    onJump.Invoke(dialog, null);
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    GameEvents.onGameSceneLoadRequested.Remove(onLoad);
                    CloseAlarmJumpDialog(dialog, alarm.Id);
                    FailAlarmJump(TestCommandAlarmJump.JumpThrewReason,
                        $"{inner.GetType().Name}: {inner.Message} alarmId={alarm.Id} targetPid={pid}", alarm.Id);
                    return;
                }
                GameEvents.onGameSceneLoadRequested.Remove(onLoad);
            }
            uint after = ActiveVesselPidOrZero();

            // ----- Close the dialog the player's way (Delete and close), leaving no alarm -----
            bool alarmRemoved = CloseAlarmJumpDialog(dialog, alarm.Id);

            var payload = TestCommandAlarmJump.BuildOkPayload(pid, targetIsGhost, targetLoaded, alarm.Id,
                stockWouldShow, jumpShown, decision, press, pressed, before, after, sceneLoadRequested,
                alarmRemoved);
            ParsekLog.Info(Tag, TestCommandAlarmJump.FormatResultLine(payload));
            SetExecResult("OK", payload, null);
        }

        /// <summary>
        /// Closes the alarm dialog through stock's <c>OnDeleteOnClose</c> (which deletes the alarm
        /// through <c>ActionAlarm</c>) and deletes the alarm directly if it is still listed.
        /// True when no alarm with <paramref name="alarmId"/> remains.
        /// </summary>
        private static bool CloseAlarmJumpDialog(AlarmClockMessageDialog dialog, uint alarmId)
        {
            try
            {
                if (dialog != null)
                {
                    MethodInfo close = AccessTools.Method(typeof(AlarmClockMessageDialog), "OnDeleteOnClose");
                    if (close != null)
                        close.Invoke(dialog, null);
                }
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                ParsekLog.Warn(Tag, $"alarmjump dialog close threw: {inner.GetType().Name}: {inner.Message} " +
                    $"alarmId={alarmId}");
            }
            return RemoveAlarmJumpAlarm(alarmId);
        }

        private static bool RemoveAlarmJumpAlarm(uint alarmId)
        {
            try
            {
                if (AlarmClockScenario.Instance == null) return true;
                if (AlarmClockScenario.AlarmExists(alarmId))
                    AlarmClockScenario.DeleteAlarm(alarmId);
                return !AlarmClockScenario.AlarmExists(alarmId);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"alarmjump alarm delete threw: {ex.GetType().Name}: {ex.Message} " +
                    $"alarmId={alarmId}");
                return false;
            }
        }

        private void RejectAlarmJump(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"alarmjump rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }

        private void FailAlarmJump(string reason, string detail, uint alarmId)
        {
            bool removed = RemoveAlarmJumpAlarm(alarmId);
            ParsekLog.Warn(Tag, $"alarmjump failed reason={reason} {detail} alarmRemoved={Bool(removed)}");
            SetExecResult("ERROR", null, reason);
        }
    }
}
