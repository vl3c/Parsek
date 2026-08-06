using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Arrival-validation-lane partial: the thin Unity applier for the
    /// single-phase <c>MissionConfig</c> verb (the SECOND reserved-name
    /// promotion since M-C1, after R12's SimulateStockSwitchClick; the wire
    /// token is byte-identical before and after, only the response changes).
    ///
    /// <para>
    /// WHY THIS VERB EXISTS. Re-aim engagement is gated on MISSION-level loop
    /// (<c>Mission.LoopPlayback</c>; <c>MissionLoopUnitBuilder.TryBuildMissionUnit</c>
    /// skips non-looping missions), and NOTHING an unattended run can reach
    /// arms it: the SetSetting whitelist has no loop knob, the per-recording
    /// loop flag is a different switch entirely, and the Missions-window
    /// toggle is a human UI. The Tier-2 map-dwell lane (the playback half
    /// M1/M2 deliberately do not cover) needs the committed duna-direct
    /// mission LOOPING before it can observe the loop's replay across the
    /// recorded SOI handoffs -- this verb is that arming switch, driven
    /// through the PRODUCTION path (<c>MissionStore.SetLoopEnabled</c>: the
    /// same anchor stamping and one-loop-per-tree conflict clearing the UI
    /// toggle runs) rather than a field poke.
    /// </para>
    ///
    /// <para>
    /// ARGS. <c>tree=&lt;treeId&gt;</c> (the committed RECORDING_TREE id; the
    /// stable handle a fixture pins) + <c>loop=&lt;true|false&gt;</c>, plus
    /// optional <c>intervalSeconds=&lt;positive double&gt;</c> applied before
    /// the enable so the anchor stamp sees the final configuration. The
    /// mission is resolved via <c>MissionStore.FindOriginalMission(treeId)</c>
    /// (the default mission EnsureDefaultsForTrees seeds at OnLoad for every
    /// committed tree, so a fixture needs no MISSION node of its own).
    /// SINGLE-PHASE: SetLoopEnabled is synchronous state mutation; there is
    /// nothing to defer for.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void MissionConfigImpl(ParsedCommand cmd)
        {
            string treeArg = ArgOrNull(cmd, "tree");
            string loopArg = ArgOrNull(cmd, "loop");
            string intervalArg = ArgOrNull(cmd, "intervalSeconds");

            if (string.IsNullOrEmpty(treeArg))
            {
                ParsekLog.Warn(Tag, "missionconfig rejected reason=tree-arg-missing");
                SetExecResult("REJECTED", null, "tree-arg-missing");
                return;
            }
            bool loopOn;
            if (!TestCommandMissionConfig.TryParseLoopArg(loopArg, out loopOn))
            {
                ParsekLog.Warn(Tag,
                    $"missionconfig rejected reason=loop-arg-invalid loop={loopArg ?? string.Empty}");
                SetExecResult("REJECTED", null, "loop-arg-invalid");
                return;
            }
            double intervalSeconds;
            if (!TestCommandMissionConfig.TryParseIntervalArg(
                    intervalArg, out intervalSeconds))
            {
                ParsekLog.Warn(Tag,
                    $"missionconfig rejected reason=interval-arg-invalid intervalSeconds={intervalArg ?? string.Empty}");
                SetExecResult("REJECTED", null, "interval-arg-invalid");
                return;
            }

            Mission mission = MissionStore.FindOriginalMission(treeArg);
            if (mission == null)
            {
                ParsekLog.Warn(Tag,
                    $"missionconfig error reason=unknown-tree tree={treeArg}");
                SetExecResult("ERROR", null, "unknown-tree");
                return;
            }

            if (intervalSeconds > 0.0)
            {
                mission.LoopIntervalSeconds = intervalSeconds;
                mission.LoopTimeUnit = LoopTimeUnit.Sec;
            }
            double currentUT = Planetarium.GetUniversalTime();
            MissionStore.SetLoopEnabled(mission, loopOn, currentUT,
                RecordingStore.CommittedTrees);

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "missionconfig applied: mission='{0}' tree={1} loop={2} " +
                "intervalSeconds={3} anchorUt={4}",
                mission.Name, treeArg, mission.LoopPlayback,
                mission.LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture),
                mission.LoopAnchorUT.ToString("R", CultureInfo.InvariantCulture)));

            SetExecResult("OK", Payload(
                Kv("mission", mission.Name ?? string.Empty),
                Kv("tree", treeArg),
                Kv("loop", mission.LoopPlayback ? "true" : "false"),
                Kv("intervalSeconds",
                    mission.LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)),
                Kv("anchorUt",
                    mission.LoopAnchorUT.ToString("R", CultureInfo.InvariantCulture))), null);
        }
    }

    /// <summary>
    /// Pure decision half of the MissionConfig verb (headlessly testable; the
    /// applier above owns the Unity/state calls only).
    /// </summary>
    internal static class TestCommandMissionConfig
    {
        /// <summary>Strict bool: exactly "true" or "false" (the seam's
        /// SetSetting convention; anything else is the REJECTED path).</summary>
        internal static bool TryParseLoopArg(string raw, out bool loopOn)
        {
            loopOn = false;
            if (raw == "true") { loopOn = true; return true; }
            if (raw == "false") { return true; }
            return false;
        }

        /// <summary>Optional positive interval: null/empty means "leave the
        /// mission's configured interval alone" (parsed as 0, the no-op
        /// sentinel); a present value must be a finite positive
        /// InvariantCulture double.</summary>
        internal static bool TryParseIntervalArg(string raw, out double seconds)
        {
            seconds = 0.0;
            if (string.IsNullOrEmpty(raw))
                return true;
            if (!double.TryParse(raw, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out seconds))
                return false;
            return !double.IsNaN(seconds) && !double.IsInfinity(seconds)
                && seconds > 0.0;
        }
    }
}
