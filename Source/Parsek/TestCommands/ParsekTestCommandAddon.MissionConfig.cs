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
    /// the enable so the anchor stamp sees the final configuration (and ONLY
    /// on an enable: a <c>loop=false</c> round trip must not rewrite
    /// persisted mission config it is switching off). The
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

            if (TestCommandMissionConfig.ShouldApplyInterval(loopOn, intervalSeconds))
            {
                mission.LoopIntervalSeconds = intervalSeconds;
                mission.LoopTimeUnit = LoopTimeUnit.Sec;
            }
            double currentUT = Planetarium.GetUniversalTime();
            MissionStore.SetLoopEnabled(mission, loopOn, currentUT,
                RecordingStore.CommittedTrees);

            // Build the loop unit through the PRODUCTION single-selection door
            // (TryBuildLoopUnitForSelection: same pipeline the render seams
            // run) so the response carries the unit's ACTUAL span clock.
            // Load-bearing for the dwell lane, measured on the first V2
            // reading flight: a re-aim-ENGAGED unit phase-locks its anchor to
            // the NEXT faithful launch window (phaseAnchorUT ~24.3M on a save
            // whose arm-time LoopAnchorUT was ~9.16M), so a consumer that
            // dwells at LoopAnchorUT-relative windows watches an empty map.
            // Parameter sourcing mirrors ParsekFlight.DriveMissionLoopUnits
            // exactly (settings-derived interval + rotation mode, the live
            // body seam).
            bool unitBuilt = false;
            double phaseAnchorUt = double.NaN;
            double spanStartUt = double.NaN;
            double cadenceSeconds = double.NaN;
            if (loopOn)
            {
                double autoLoopIntervalSeconds =
                    ParsekSettings.Current?.autoLoopIntervalSeconds
                    ?? LoopTiming.DefaultLoopIntervalSeconds;
                TransitedBodyRotationMode tbrMode =
                    ParsekSettings.Current?.TransitedBodyRotationMode
                    ?? TransitedBodyRotationMode.Loose;
                bool forceFaithful =
                    ParsekSettings.Current?.forceFaithfulLoopPlayback ?? false;
                // [ERS-exempt] The raw CommittedRecordings read below (the
                // audited pattern; the CommittedTrees reads here and at the
                // SetLoopEnabled call are not audited) feeds the loop-unit
                // builder, whose member indices are committed-LIST indices -
                // the RouteOrchestrator list-alignment rationale; an
                // ERS-filtered list would re-index members under the builder.
                // File allowlisted in scripts/ers-els-audit-allowlist.txt.
                unitBuilt = MissionLoopUnitBuilder.TryBuildLoopUnitForSelection(
                    mission, RecordingStore.CommittedTrees,
                    RecordingStore.CommittedRecordings, autoLoopIntervalSeconds,
                    FlightGlobalsBodyInfo.Instance, tbrMode, forceFaithful,
                    out GhostPlaybackLogic.LoopUnit unit);
                if (unitBuilt)
                {
                    phaseAnchorUt = unit.PhaseAnchorUT;
                    spanStartUt = unit.SpanStartUT;
                    cadenceSeconds = unit.CadenceSeconds;
                }
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "missionconfig applied: mission='{0}' tree={1} loop={2} " +
                "intervalSeconds={3} anchorUt={4} unitBuilt={5} phaseAnchorUt={6}",
                mission.Name, treeArg, mission.LoopPlayback,
                mission.LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture),
                mission.LoopAnchorUT.ToString("R", CultureInfo.InvariantCulture),
                unitBuilt,
                phaseAnchorUt.ToString("R", CultureInfo.InvariantCulture)));

            SetExecResult("OK", Payload(
                Kv("mission", mission.Name ?? string.Empty),
                Kv("tree", treeArg),
                Kv("loop", mission.LoopPlayback ? "true" : "false"),
                Kv("intervalSeconds",
                    mission.LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)),
                Kv("anchorUt",
                    mission.LoopAnchorUT.ToString("R", CultureInfo.InvariantCulture)),
                Kv("unitBuilt", unitBuilt ? "true" : "false"),
                Kv("phaseAnchorUt",
                    phaseAnchorUt.ToString("R", CultureInfo.InvariantCulture)),
                Kv("spanStartUt",
                    spanStartUt.ToString("R", CultureInfo.InvariantCulture)),
                Kv("cadenceSeconds",
                    cadenceSeconds.ToString("R", CultureInfo.InvariantCulture))), null);
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

        /// <summary>The interval is configuration for the loop being SWITCHED
        /// ON; a disable round trip must not rewrite persisted mission config
        /// it is switching off.</summary>
        internal static bool ShouldApplyInterval(bool loopOn, double seconds)
        {
            return loopOn && seconds > 0.0;
        }
    }
}
