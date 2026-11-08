using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Mission loop playback: the loop row and its kind, the loop toggle and its commit, the route-bound label, the cleared-loop announcement and the loop period cell.
    internal partial class MissionsWindowUI
    {
        // The loop row (Advanced, line 2 of the grid): a fixed LoopCellWidth x LoopCellHeight row
        // whose left edge is Warp's and whose right edge is Log's (see LoopGridColumnBWidth).
        //   not route-bound: column B = "Loop" [x] "every", the Log slot = the period
        //                    ("[10] [sec]", "[60s] [auto]" or the value-only "~13d-19d").
        //   route-bound:     "Looped by route" across the whole row. A route owns the tree's
        //                    loop, so the toggle and the period cell could only draw greyed and
        //                    inert (RouteTreeGuard clears the manual loop when a route binds), and
        //                    the label's hover names the route; the full-row label keeps both
        //                    edges where the other states put them.
        private void DrawMissionLoopRow(Mission mission, MissionThroughLineView view,
            MissionPeriodicityDisplay periodicity, bool missionRouteBound, Route bindingRoute)
        {
            LogMissionLoopRowTransition(mission, ResolveLoopRowKind(
                missionRouteBound,
                mission.LoopPlayback,
                periodicity.IsPhaseLockedConstrained || periodicity.IsReaim,
                mission.LoopTimeUnit == LoopTimeUnit.Auto));

            GUILayout.BeginHorizontal(loopCellRowStyle,
                GUILayout.Width(LoopCellWidth), GUILayout.Height(LoopCellHeight));
            if (missionRouteBound)
            {
                DrawLoopedByRouteLabel(bindingRoute, ParsekUI.AppliedUiComplexityMode, LoopCellWidth);
            }
            else
            {
                DrawMissionLoopToggle(mission, missionRouteBound, bindingRoute);
                // Periodicity (the Phase-1 / Tier-1 solution) is computed once per mission by
                // the draw loop and passed in; the period cell shows the faithful period P
                // (value only, basis on hover) when phase-locked, otherwise a period EDITOR (a
                // value field plus a unit-cycling button).
                DrawMissionLoopPeriodCell(mission, view, periodicity);
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>What the Advanced loop row draws, for the transition log.</summary>
        internal enum LoopRowKind
        {
            RouteBound,
            LockedPeriod,
            AutoPeriod,
            ManualPeriod,
        }

        /// <summary>
        /// Which of the loop row's four states draws: the route label when a supply route
        /// binds the tree, else "Loop [x] every" plus the period, which is a read-only value
        /// only while the mission loops on a phase-locked or re-aim cadence (the
        /// DrawMissionLoopPeriodCell branch), else the Auto or manual editor. Pure for unit
        /// testing.
        /// </summary>
        internal static LoopRowKind ResolveLoopRowKind(
            bool routeBound, bool looping, bool lockedOrReaim, bool autoUnit)
        {
            if (routeBound)
                return LoopRowKind.RouteBound;
            if (looping && lockedOrReaim)
                return LoopRowKind.LockedPeriod;
            return autoUnit ? LoopRowKind.AutoPeriod : LoopRowKind.ManualPeriod;
        }

        private void LogMissionLoopRowTransition(Mission mission, LoopRowKind kind)
        {
            if (mission == null || string.IsNullOrEmpty(mission.Id))
                return;
            string state = kind + (mission.LoopPlayback ? " looping" : " not-looping");
            if (lastMissionLoopRowState.TryGetValue(mission.Id, out string previous)
                && string.Equals(previous, state, System.StringComparison.Ordinal))
                return;
            lastMissionLoopRowState[mission.Id] = state;
            ParsekLog.Verbose("Mission",
                $"Loop row for '{mission.Name}' ({mission.Id}): {previous ?? "<first draw>"} -> {state}");
        }

        // One fixed-width, LoopCellHeight-tall slot of the loop row that centres a control
        // shorter than the line (the checkbox, the period field, the unit button) on it.
        private static void BeginLoopCellSlot(float width)
        {
            GUILayout.BeginVertical(GUIStyle.none,
                GUILayout.Width(width), GUILayout.Height(LoopCellHeight));
            GUILayout.FlexibleSpace();
        }

        private static void EndLoopCellSlot()
        {
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
        }

        // "Loop [x] every": the label, the checkbox and the word, filling column B under Warp,
        // all centred on the loop row's line.
        //
        // Mutual exclusion (design section 0.6): when this tree is bound to a supply route, the
        // manual Loop toggle is greyed OFF (a tree is EITHER a route OR a manually looped
        // mission). The loop row draws the route label instead of calling this, but the
        // GUI.enabled wrap and the belt-and-suspenders commit guard (CommitMissionLoopToggle)
        // stay, so no layout refactor can let a turn-ON reach MissionStore.SetLoopEnabled.
        /// <summary>
        /// Including a partner journey whose foreign tree is a supply route widens the mission
        /// onto recordings the route owns (design 0.6), so the mission's own loop goes, the way
        /// a Create Route takes over a looping mission. TRUE when it turned the loop off.
        /// </summary>
        private static bool YieldLoopToPartnerRoute(Mission mission, ForeignDockLink link)
        {
            if (!RouteTreeGuard.IsTreeBoundToActiveRoute(link.ForeignTreeId))
                return false;
            MissionStore.SetLoopEnabled(mission, false, Planetarium.GetUniversalTime());
            ParsekLog.ScreenMessage(
                LogisticsCreatePresentation.FormatMissionNowRepeatsOnRouteToast(mission.Name), 5f);
            ParsekLog.Info("Mission",
                $"Mission '{mission.Name}' loop turned off: included partner journey link={link.LinkId} " +
                $"spans route-bound tree={link.ForeignTreeId} (toast posted)");
            return true;
        }

        private void DrawMissionLoopToggle(Mission mission, bool missionRouteBound, Route bindingRoute)
        {
            bool prevGuiEnabled = GUI.enabled;
            if (missionRouteBound)
                GUI.enabled = false;
            GUILayout.Label(new GUIContent("Loop", MissionPresentation.LoopToggleTooltip),
                loopCellLabelStyle, GUILayout.Width(LoopRowLabelWidth),
                GUILayout.Height(LoopCellHeight));
            bool missionLoopDrawnEnabled = GUI.enabled;
            // The checkbox is shorter than the line: its slot centres it both ways.
            BeginLoopCellSlot(LoopRowToggleSlotWidth);
            GUILayout.BeginHorizontal(GUIStyle.none);
            GUILayout.FlexibleSpace();
            bool loopNow = GUILayout.Toggle(mission.LoopPlayback,
                new GUIContent("", MissionPresentation.LoopToggleTooltip),
                loopCellToggleStyle, GUILayout.ExpandWidth(false));
            // LoopToggleTooltip is the generic what-it-does sentence; when a supply
            // route owns the schedule the carrier names the route instead.
            DisabledHoverEcho.CarryLastControl(
                missionLoopDrawnEnabled,
                RecordingsTableUI.LoopToggleDisabledReason(
                    bindingRoute != null ? bindingRoute.Name : null));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EndLoopCellSlot();
            // "every" reads the row as one sentence: "Loop [x] every [10] [sec]".
            GUILayout.Label(new GUIContent("every", MissionPresentation.LoopToggleTooltip),
                loopCellWordStyle, GUILayout.Width(LoopRowEveryWidth),
                GUILayout.Height(LoopCellHeight));
            GUI.enabled = prevGuiEnabled;
            CommitMissionLoopToggle(mission, loopNow, missionRouteBound, bindingRoute);
        }

        /// <summary>
        /// The route-bound mission label for the frame-latched mode: "Run by route" in
        /// Basic, "Looped by route" in Advanced. Pure.
        /// </summary>
        internal static string RouteBoundLabel(UiComplexityMode mode)
        {
            return mode == UiComplexityMode.Basic ? RouteBoundLabelBasic : RouteBoundLabelAdvanced;
        }

        /// <summary>
        /// The route label's hover, naming the route: "Run on the schedule of route 'X'."
        /// in Basic, "Looped by route: X" in Advanced. A route with no name reads "route".
        /// Pure.
        /// </summary>
        internal static string RouteBoundTooltip(UiComplexityMode mode, string routeName)
        {
            string name = string.IsNullOrEmpty(routeName) ? "route" : routeName;
            return mode == UiComplexityMode.Basic
                ? "Run on the schedule of route '" + name + "'."
                : "Looped by route: " + name;
        }

        // Inline route label (ASCII only). NOT part of the loop authoring gate (owner
        // decision 2026-09-29), so a Basic player still reads why this mission's flights
        // repeat; its words follow the frame-latched mode (RouteBoundLabel), so both passes
        // of one frame draw the same text. Advanced draws it across the whole loop row (a
        // fixed width); Basic draws it content-sized after the summary.
        private void DrawLoopedByRouteLabel(Route bindingRoute, UiComplexityMode mode, float width = 0f)
        {
            var content = new GUIContent(RouteBoundLabel(mode),
                RouteBoundTooltip(mode, bindingRoute != null ? bindingRoute.Name : null));
            if (width > 0f)
                GUILayout.Label(content, loopCellLabelStyle, GUILayout.Width(width),
                    GUILayout.Height(LoopCellHeight));
            else
                GUILayout.Label(content, missionHeaderInlineLabel, GUILayout.ExpandWidth(false),
                    GUILayout.Height(LoopCellHeight));
        }

        // The other missions that are looping right now (T1.6): snapshotted BEFORE
        // MissionStore.SetLoopEnabled so the ones it clears can be named afterwards. Returns null
        // when nothing else is looping, so the common case allocates nothing.
        private static List<Mission> SnapshotOtherLoopingMissions(Mission target)
        {
            var missions = MissionStore.Missions;
            List<Mission> looping = null;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null || ReferenceEquals(m, target) || !m.LoopPlayback)
                    continue;
                if (looping == null)
                    looping = new List<Mission>(2);
                looping.Add(m);
            }
            return looping;
        }

        // Names the missions whose loop the just-enabled one took over, on screen and in the log
        // (T1.6). MissionStore logs the COUNTS at the clear site; this is the player-facing half.
        private static void AnnounceClearedLoops(Mission winner, List<Mission> wereLooping)
        {
            if (wereLooping == null || wereLooping.Count == 0)
                return;
            List<string> cleared = null;
            for (int i = 0; i < wereLooping.Count; i++)
            {
                Mission m = wereLooping[i];
                if (m == null || m.LoopPlayback)
                    continue;   // still looping (a disjoint tree) - nothing was taken from it
                if (cleared == null)
                    cleared = new List<string>(wereLooping.Count);
                cleared.Add(m.Name);
            }
            string message = MissionPresentation.BuildLoopMovedScreenMessage(
                winner != null ? winner.Name : null, cleared);
            if (message == null)
                return;
            ParsekLog.Info("Mission", message);
            ParsekLog.ScreenMessage(message, 5f);
        }

        // Commits a Loop-toggle click from the mission header. Extracted so the draw site stays a
        // single gated block (Basic never draws the toggle, so this never runs there) and so the
        // route commit guard is one named thing rather than a tail of the header method.
        //
        // Mutual exclusion (design §0.6): a route owns its tree's loop, so a manual turn-ON on a
        // route-bound tree is logged and dropped - belt and suspenders behind the GUI.enabled wrap
        // at the draw site, which already renders the toggle disabled.
        private void CommitMissionLoopToggle(Mission mission, bool loopNow,
            bool missionRouteBound, Route bindingRoute)
        {
            if (loopNow == mission.LoopPlayback)
                return;

            if (missionRouteBound)
            {
                ParsekLog.Info("RouteGuard",
                    $"Missions-tab Loop toggle blocked for tree={mission.TreeId} " +
                    $"(bound by route {(bindingRoute != null ? bindingRoute.Id : "<none>")}); manual loop " +
                    $"request={loopNow} ignored");
                return;
            }

            // T1.6: enabling a loop can clear the loop on a mission that shares this tree
            // (or a cross-tree-linked one). That used to be log-only - the other row simply
            // showed its box unticked on the next frame - so snapshot the losers around the
            // call and tell the player which mission's loop moved.
            List<Mission> wereLooping = loopNow ? SnapshotOtherLoopingMissions(mission) : null;
            // Trees passed so a cross-tree-linked mission also clears looping missions
            // on its linked foreign tree(s) (M-MIS-8 spanned-set rule).
            MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime(),
                RecordingStore.CommittedTrees);

            if (loopNow)
                AnnounceClearedLoops(mission, wereLooping);
            // R6 double-clock advisory (design 7.7), on ENABLE only and only AFTER the
            // enable succeeded - the enable itself clears every mission the hard rule
            // considers conflicting (announced just above), so what is left looping is
            // exactly the population the advisory is about. Advisory only: nothing is
            // switched off here.
            if (loopNow)
                PostDoubleClockAdvisoryIfAny(mission);
            // Turning loop off disables the period field; end any in-progress edit on it.
            if (!loopNow && loopPeriodFocusedMissionId == mission.Id)
                loopPeriodFocusedMissionId = null;
        }

        // Loop-period cell: a value text field plus a unit button cycling Sec/Min/Hour/
        // Auto, mirroring the recordings window's DrawLoopPeriodCell. When loop is off the
        // controls grey out (matching the recordings window). Reuses ParsekUI's shared
        // loop-time helpers so the parse/format/convert rules stay identical. Commit on
        // text change: parse via TryParseLoopInput, reject negatives, clamp below
        // MinCycleDuration (same contract as RecordingsTableUI.CommitLoopPeriodEdit).
        private void DrawMissionLoopPeriodCell(
            Mission mission, MissionThroughLineView view, MissionPeriodicityDisplay periodicity)
        {
            // The period fills the Log slot of the loop row (see LoopGridColumnBWidth): value
            // field + gap + unit button == Log's width, so the row ends at Log's right edge.
            const float unitBtnW = LoopPeriodUnitButtonWidth;
            const float valueW = LoopPeriodValueWidth;

            bool enabled = mission.LoopPlayback;
            bool auto = mission.LoopTimeUnit == LoopTimeUnit.Auto;

            string controlName = "MissionLoopPeriod_" + mission.Id;

            // Fixed-width period cell: every state fills exactly the Log slot (a read-only
            // value label of that width, or the value field + gap + unit button), so nothing in
            // the grid moves when the period's state changes. NO wrapping horizontal group of its
            // own: its controls are entries of the loop row directly; the field and the unit
            // button each sit in a centring slot (BeginLoopCellSlot) on the row's line.

            // Phase-locked + constrained (supported, P != MinCycleDuration): the cadence is
            // determined by physics (quantized to a multiple of P), not freely editable, so show the
            // faithful period P + its basis label ("~6h (Kerbin rot)" / "~1.6d (Mun window)") as a
            // read-only tinted cell instead of the raw overlap-cap cadence. The unit button is
            // dropped here (the value is a fixed physical period, not a user-typed value in a unit).
            // An unconstrained (continuous) or unsupported (not-aligned) config falls through to the
            // normal editable cell below. End any stale edit focus on this field first.
            if (enabled && (periodicity.IsPhaseLockedConstrained || periodicity.IsReaim))
            {
                if (loopPeriodFocusedMissionId == mission.Id)
                    loopPeriodFocusedMissionId = null;
                // Re-aim takes precedence: a cross-parent transfer relaunches on the (fixed) synodic
                // cadence, shown read-only like a phase-locked period. Zero-drift scheduled units show a
                // "varies" interval; otherwise the fixed faithful period P + basis.
                // The cell shows the VALUE only ("~13d-19d", "~6h", "~2.1y"); the full display
                // with its qualifier ("~13d-19d (Mun window, varies)") and the locked-state
                // sentence ride the hover (owner decision 2026-09-30: the value alone keeps the
                // loop grid narrow, so the mission summary keeps its width).
                string lockedFull = periodicity.IsReaim
                    ? BuildReaimPeriodCellDisplay(periodicity.ReaimSynodicSeconds, periodicity.ReaimTargetBody)
                    : periodicity.IsScheduled
                        ? BuildScheduledPeriodCellDisplay(
                            periodicity.ScheduledMinIntervalSeconds,
                            periodicity.ScheduledMaxIntervalSeconds, periodicity.DominantKind,
                            periodicity.DominantBodyName)
                        : BuildPeriodCellDisplay(
                            periodicity.Solution.P, periodicity.DominantKind, periodicity.DominantBodyName);
                string locked = periodicity.IsReaim
                    ? FormatPeriodCompact(periodicity.ReaimSynodicSeconds)
                    : periodicity.IsScheduled
                        ? FormatScheduledIntervalRange(
                            periodicity.ScheduledMinIntervalSeconds,
                            periodicity.ScheduledMaxIntervalSeconds)
                        : FormatPeriodCompact(periodicity.Solution.P);
                GUI.enabled = false;
                Color prevLocked = GUI.contentColor;
                GUI.contentColor = LoopPeriodClampColor;
                // loopCellLabelStyle (vertically centred) so "~6.4d" sits on the same line as
                // "every" before it, at the whole Log slot's width: "Loop [x] every ~6.4d".
                // T1.5: name the state the cell is in - the four period states are distinguished by
                // greyness, editability, and tint alone, and none of them says so.
                GUILayout.Label(
                    new GUIContent(locked, MissionPresentation.BuildLockedPeriodTooltip(lockedFull)),
                    loopCellLabelStyle, GUILayout.Width(InteractButtonWidth),
                    GUILayout.Height(LoopCellHeight));
                GUI.contentColor = prevLocked;
                GUI.enabled = true;
            }
            else
            {

            // The EFFECTIVE launch cadence: the requested period (manual value, or the global
            // auto-loop interval for Auto) raised by the overlap cap to keep ceil(span/cadence)
            // within MaxOverlapMissionInstances. When the period is short relative to the mission
            // span this is larger than what was typed, so the cell shows what is actually running
            // (e.g. a 10 s period on a 15-min mission runs at ~44 s). Tinted when raised.
            var settings = ParsekSettings.Current;
            double requestedSeconds = auto
                ? (settings != null ? settings.autoLoopIntervalSeconds : LoopTiming.DefaultLoopIntervalSeconds)
                : mission.LoopIntervalSeconds;
            // [ERS-exempt] reason: span uses the RAW committed list to match MissionLoopUnitBuilder
            // (which keys members by committed RecordingId), so the displayed effective cadence
            // equals what actually runs. Display-only; file allowlisted (see watch button).
            double span = MissionSpanSeconds(view, mission, RecordingStore.CommittedRecordings);
            double effectiveSeconds = span > 0
                ? GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                    requestedSeconds, span, GhostPlayback.MaxOverlapMissionInstances)
                : System.Math.Max(requestedSeconds, LoopTiming.MinCycleDuration);
            bool clamped = effectiveSeconds > requestedSeconds + 1e-6;
            // Only surface the capped value when the mission is actually looping (a cadence is
            // only "running" then); a greyed-off cell shows the configured value as typed.
            bool showEffective = clamped && enabled;

            if (auto)
            {
                // Auto mode: the value is inherited from Settings > Looping (and then capped by
                // the span). Show the EFFECTIVE cadence in the global display unit plus a unit
                // suffix (the unit button reads "auto", so the suffix gives the real unit). The
                // field is non-editable.
                var globalDisplayUnit = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                double autoShown = showEffective ? effectiveSeconds : requestedSeconds;
                string autoText = ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(autoShown, globalDisplayUnit), globalDisplayUnit)
                    + RecordingsTableUI.UnitSuffix(globalDisplayUnit);
                // A non-editable field cannot be the one being edited; drop any stale edit focus.
                if (loopPeriodFocusedMissionId == mission.Id)
                    loopPeriodFocusedMissionId = null;
                GUI.enabled = false;
                Color prevAuto = GUI.contentColor;
                if (showEffective) GUI.contentColor = LoopPeriodClampColor;
                BeginLoopCellSlot(valueW);
                GUILayout.TextField(autoText, loopCellFieldStyle, GUILayout.Width(valueW));
                EndLoopCellSlot();
                GUI.contentColor = prevAuto;
                GUI.enabled = true;
            }
            else if (loopPeriodFocusedMissionId != mission.Id)
            {
                // Manual mode, NOT editing this field: show the EFFECTIVE (capped) cadence (or the
                // raw stored value), and begin editing when the field gains keyboard focus (seeding
                // the buffer from the raw stored value). Commit happens on Enter / click-away, not
                // on every keystroke, so it does not rely on per-frame focus detection.
                string shownText = showEffective
                    ? ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(effectiveSeconds, mission.LoopTimeUnit), mission.LoopTimeUnit)
                    : ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(mission.LoopIntervalSeconds, mission.LoopTimeUnit), mission.LoopTimeUnit);

                GUI.enabled = enabled;
                GUI.SetNextControlName(controlName);
                Color prevManual = GUI.contentColor;
                if (showEffective) GUI.contentColor = LoopPeriodClampColor;
                BeginLoopCellSlot(valueW);
                GUILayout.TextField(shownText, loopCellFieldStyle, GUILayout.Width(valueW));
                GUI.contentColor = prevManual;
                loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                EndLoopCellSlot();
                if (enabled && GUI.GetNameOfFocusedControl() == controlName)
                {
                    loopPeriodFocusedMissionId = mission.Id;
                    loopPeriodEditText = ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(mission.LoopIntervalSeconds, mission.LoopTimeUnit),
                        mission.LoopTimeUnit);
                    ParsekLog.Verbose("Mission",
                        $"Loop period edit started for '{mission.Name}': value='{loopPeriodEditText}'");
                }
                GUI.enabled = true;
            }
            else
            {
                // Manual mode, editing THIS field: show the in-progress buffer. Commit on Enter
                // (checked before the TextField, which consumes the KeyDown); click-away commit is
                // handled at the window level in DrawMissionsTabContent.
                bool submit = Event.current.type == EventType.KeyDown &&
                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                GUI.enabled = enabled;
                GUI.SetNextControlName(controlName);
                BeginLoopCellSlot(valueW);
                string newText = GUILayout.TextField(
                    loopPeriodEditText, loopCellFieldStyle, GUILayout.Width(valueW));
                loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                EndLoopCellSlot();
                if (newText != loopPeriodEditText)
                    loopPeriodEditText = newText;
                if (submit)
                {
                    CommitMissionLoopPeriodEdit(mission);
                    Event.current.Use();
                }
                GUI.enabled = true;
            }

            GUILayout.Space(LoopPeriodGap);
            GUI.enabled = enabled;
            // T1.5: the unit button carries the cell's STATE tooltip (loop off / auto / raised by
            // the overlap cap) - a GUILayout.TextField takes no GUIContent, so the adjacent button
            // is where a hover can explain the field beside it.
            string periodStateTooltip = MissionPresentation.BuildPeriodStateTooltip(
                enabled, auto, showEffective) ?? string.Empty;
            bool unitButtonEnabled = GUI.enabled;
            BeginLoopCellSlot(unitBtnW);
            bool unitButtonClicked = GUILayout.Button(
                    new GUIContent(ParsekUI.UnitLabel(mission.LoopTimeUnit), periodStateTooltip),
                    loopCellUnitButtonStyle, GUILayout.Width(unitBtnW));
            DisabledHoverEcho.CarryLastControl(unitButtonEnabled, periodStateTooltip);
            EndLoopCellSlot();
            if (unitButtonClicked)
            {
                mission.LoopTimeUnit = RecordingsTableUI.CycleRecordingUnit(mission.LoopTimeUnit);
                if (loopPeriodFocusedMissionId == mission.Id)
                    loopPeriodFocusedMissionId = null;
                GUIUtility.keyboardControl = 0;
                ParsekLog.Info("Mission",
                    $"Loop unit for '{mission.Name}' changed to {mission.LoopTimeUnit}");
            }
            GUI.enabled = true;
            }
        }

        // Commits the in-progress loop-period buffer to the mission (parse / clamp via
        // CommitMissionLoopPeriod), then ends the edit (clears focus + keyboard control). Safe to
        // call with a null mission (just ends the edit).
        private void CommitMissionLoopPeriodEdit(Mission mission)
        {
            if (mission != null)
                CommitMissionLoopPeriod(mission, loopPeriodEditText);
            loopPeriodFocusedMissionId = null;
            loopPeriodEditText = "";
            GUIUtility.keyboardControl = 0;
        }

        // Parses an edited loop-period value and writes it to the Mission, mirroring
        // RecordingsTableUI.CommitLoopPeriodEdit: reject negatives, clamp below
        // MinCycleDuration. Invalid text is ignored (no write); the caller
        // (CommitMissionLoopPeriodEdit) then ends the edit and the field reverts to the stored
        // value display, exactly as the Recordings tab does on Enter / click-away. Auto unit
        // does not commit a value. (While editing, the TextField keeps whatever you type between
        // frames; this method only runs at the commit, not per keystroke.)
        private static void CommitMissionLoopPeriod(Mission mission, string text)
        {
            if (mission.LoopTimeUnit == LoopTimeUnit.Auto)
                return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (!ParsekUI.TryParseLoopInput(text, mission.LoopTimeUnit, out double parsed))
                return;
            double newSeconds = ParsekUI.ConvertToSeconds(parsed, mission.LoopTimeUnit);
            if (newSeconds < 0)
            {
                ParsekLog.Warn("Mission",
                    $"Loop period for '{mission.Name}' rejected: " +
                    $"negative value {newSeconds.ToString("F1", ic)}s");
                return;
            }
            if (newSeconds < LoopTiming.MinCycleDuration)
            {
                ParsekLog.Info("Mission",
                    $"Loop period for '{mission.Name}' clamped from " +
                    $"{newSeconds.ToString("F1", ic)}s to " +
                    $"{LoopTiming.MinCycleDuration.ToString("F1", ic)}s (MinCycleDuration)");
                newSeconds = LoopTiming.MinCycleDuration;
            }
            mission.LoopIntervalSeconds = newSeconds;
            ParsekLog.Info("Mission",
                $"Loop period for '{mission.Name}' updated to " +
                newSeconds.ToString("F1", ic) + "s");
        }
    }
}
