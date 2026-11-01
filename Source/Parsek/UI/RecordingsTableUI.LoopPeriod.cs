using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Loop playback: the loop toggle and period disabled reasons, loop aggregates and bulk toggles, route binding, the period cell (view, edit, units, clamp hover, commit).
    internal partial class RecordingsTableUI
    {
        /// <summary>
        /// Why a Loop toggle is greyed out. Being driven by a supply route is the only
        /// cause - the route owns the schedule, so a hand-set loop period would fight it.
        ///
        /// <para>A per-recording row knows WHICH route took it over and names it; the
        /// select-all, folder and chain toggles cover many recordings at once and only
        /// know that at least one of them is route-bound, so they state the fact without
        /// a name. Fits the narrowest strip that renders it. Pure for unit testing.</para>
        /// </summary>
        /// <param name="boundRouteName">
        /// Display name of the route driving THIS recording, or null/empty for an
        /// aggregate toggle that spans more than one recording.
        /// </param>
        internal static string LoopToggleDisabledReason(string boundRouteName)
        {
            if (string.IsNullOrEmpty(boundRouteName))
                return "A supply route already drives one of these flights";
            return "Looped by route: " + boundRouteName;
        }

        /// <summary>
        /// Why the loop-period cell is not editable. The period only means something
        /// while the recording actually loops, and in Auto mode it is the global figure
        /// from Settings rather than a per-recording one. Pure for unit testing.
        /// </summary>
        internal static string LoopPeriodDisabledReason(bool loopPlayback, bool autoUnit)
        {
            if (!loopPlayback)
                return "Turn Loop on for this flight to set its period";
            if (autoUnit)
                return "Auto uses the shared period from Settings";
            return string.Empty;
        }

        /// <summary>
        /// Hover text of a Period cell drawn BLANK because the row's Loop is off: the
        /// loop-off reason, so the empty cell still says why it is empty. Null-safe; a
        /// looping recording yields the empty string (its cell is the live editor, which
        /// carries its own tooltips). Pure for unit testing.
        /// </summary>
        internal static string LoopPeriodBlankCellTooltip(Recording rec)
        {
            bool looping = rec != null && rec.LoopPlayback;
            if (looping) return string.Empty;
            return LoopPeriodDisabledReason(
                false, rec != null && rec.LoopTimeUnit == LoopTimeUnit.Auto);
        }

        // --- Loop helpers exclusive to recordings table ---

        /// <summary>
        /// Returns true when the per-row loop checkbox and period cell should
        /// be hidden for a recording. Cases: (1) the row is inside the virtual
        /// Unfinished Flights group (a re-fly TODO surface where playback
        /// configuration is misleading); (2) the recording is not viewable by
        /// the player as a flying ghost — i.e. it is not "logically loopable"
        /// per <see cref="Recording.IsLoopableRecording"/> (debris, or a pure
        /// orbital coast with no watchable takeoff / landing / approach phase).
        /// Debris is subsumed by the loopable check: its playback rides its
        /// parent's loop clock via <c>GhostPlaybackEngine.TryUpdateLoopSyncedDebris</c>
        /// so its own LoopPlayback flag has no effect.
        /// </summary>
        internal static bool ShouldSuppressRowLoopUi(Recording rec, bool insideUnfinishedFlightsGroup)
            => insideUnfinishedFlightsGroup || !Recording.IsLoopableRecording(rec);

        /// <summary>
        /// Loop-aggregate UI state for a group/chain/header row. Non-loopable
        /// members (debris, or pure orbital coasts that are not viewable by the
        /// player) are excluded from both the count and the bulk write because
        /// they have no per-row loop toggle (see <see cref="ShouldSuppressRowLoopUi"/>
        /// and <see cref="Recording.IsLoopableRecording"/>).
        /// When <see cref="SuppressToggle"/> is true the caller renders an
        /// empty cell (e.g. the auto-generated "X / Debris" subgroup).
        /// <see cref="AllLoop"/> and <see cref="SuppressToggle"/> are read-only
        /// properties derived from the two count inputs so a constructed state
        /// cannot drift from its counts.
        /// </summary>
        internal struct LoopAggregateState
        {
            // Count of loopable (player-viewable) members — non-debris recordings
            // with a watchable takeoff / landing / approach / docking / relative
            // track (see Recording.IsLoopableRecording). Pure orbital coasts and
            // debris are excluded, matching the per-row loop-UI suppression.
            public int LoopableCount;
            public int LoopCount;
            public bool AllLoop => LoopableCount > 0 && LoopCount == LoopableCount;
            public bool SuppressToggle => LoopableCount == 0;
        }

        /// <summary>
        /// Header overload: aggregate loop state across every committed
        /// recording without forcing the caller to materialise an index
        /// collection. The previous form passed <c>Enumerable.Range(0, Count)</c>
        /// which allocates an iterator object on every OnGUI invocation
        /// (Unity calls OnGUI once per event: Layout + Repaint at minimum,
        /// plus one per input event).
        /// </summary>
        internal static LoopAggregateState ComputeLoopAggregate(IReadOnlyList<Recording> committed)
        {
            int total = 0;
            int loops = 0;
            if (committed != null)
            {
                int n = committed.Count;
                for (int i = 0; i < n; i++)
                {
                    var rec = committed[i];
                    if (!Recording.IsLoopableRecording(rec)) continue;
                    total++;
                    if (rec.LoopPlayback) loops++;
                }
            }
            return new LoopAggregateState { LoopableCount = total, LoopCount = loops };
        }

        internal static LoopAggregateState ComputeLoopAggregate(
            IReadOnlyList<Recording> committed, IEnumerable<int> indices)
        {
            int total = 0;
            int loops = 0;
            if (committed != null && indices != null)
            {
                foreach (int idx in indices)
                {
                    if (idx < 0 || idx >= committed.Count) continue;
                    var rec = committed[idx];
                    if (!Recording.IsLoopableRecording(rec)) continue;
                    total++;
                    if (rec.LoopPlayback) loops++;
                }
            }
            return new LoopAggregateState { LoopableCount = total, LoopCount = loops };
        }

        /// <summary>
        /// Shared bulk-write for header / group / chain / block aggregate Loop
        /// toggles. Sets <c>LoopPlayback = value</c> on every loopable
        /// (player-viewable) recording at the given indices (or all of committed
        /// when indices is null, for the header path) — see
        /// <see cref="Recording.IsLoopableRecording"/>. Non-loopable rows (debris
        /// and pure orbital coasts) are skipped so the bulk write stays consistent
        /// with the per-row loop-UI suppression. When applyAutoRange is true,
        /// also calls <see cref="ApplyAutoLoopRange"/> on each written row
        /// (auto-narrow on enable, clear on disable): this preserves the
        /// pre-refactor behavior where chain / grouped-block bulk writes and
        /// per-row toggles narrowed the loop window, while the header and
        /// group bulk writes left a user-customized LoopStartUT/LoopEndUT
        /// untouched. Returns the count actually written (for log lines).
        /// </summary>
        internal static int BulkSetLoopPlayback(
            IReadOnlyList<Recording> committed, IEnumerable<int> indices,
            bool value, bool applyAutoRange)
        {
            if (committed == null) return 0;
            int written = 0;
            if (indices == null)
            {
                int n = committed.Count;
                for (int i = 0; i < n; i++)
                {
                    var r = committed[i];
                    if (!Recording.IsLoopableRecording(r)) continue;
                    r.LoopPlayback = value;
                    if (applyAutoRange) ApplyAutoLoopRange(r, value);
                    written++;
                }
                return written;
            }
            foreach (int idx in indices)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var r = committed[idx];
                if (!Recording.IsLoopableRecording(r)) continue;
                r.LoopPlayback = value;
                if (applyAutoRange) ApplyAutoLoopRange(r, value);
                written++;
            }
            return written;
        }

        // --- Route mutual-exclusion (design §0.6) helpers ---
        //
        // A tree is EITHER a supply route OR a manually looped recording, never both.
        // Every Recordings-tab loop control (per-recording / group / chain / bulk) that
        // affects a recording whose tree is bound to a route is greyed OFF and its commit
        // is blocked. The Recordings-tab loop bypasses MissionStore entirely (it writes
        // Recording.LoopPlayback directly), so the commit guard here is load-bearing.

        // True when the recording's tree is bound to a committed supply route. Null
        // recordings / empty tree ids are never bound (returns false).
        private static bool IsRecordingRouteBound(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.TreeId))
                return false;
            return RouteTreeGuard.IsTreeBoundToActiveRoute(rec.TreeId);
        }

        // True when ANY recording at the given committed indices lives on a route-bound
        // tree. A bulk / group / chain control spanning even one route-bound recording
        // greys (safe default per the confirmed decision: "keep it safe") rather than
        // partially applying. A null indices set means "all committed" (the select-all
        // header path).
        private static bool AnyRecordingRouteBound(
            IReadOnlyList<Recording> committed, IEnumerable<int> indices)
        {
            if (committed == null)
                return false;
            if (indices == null)
            {
                for (int i = 0; i < committed.Count; i++)
                    if (IsRecordingRouteBound(committed[i]))
                        return true;
                return false;
            }
            foreach (int idx in indices)
            {
                if (idx < 0 || idx >= committed.Count)
                    continue;
                if (IsRecordingRouteBound(committed[idx]))
                    return true;
            }
            return false;
        }

        // Resolves the binding route's display name for the greyed "Looped by route: <name>"
        // tooltip on a single recording's row. Falls back to "route" when unresolved.
        private static string RouteBindingTooltipName(Recording rec)
        {
            if (rec != null && !string.IsNullOrEmpty(rec.TreeId)
                && RouteTreeGuard.RouteBindingFor(rec.TreeId, out Route r)
                && r != null && !string.IsNullOrEmpty(r.Name))
            {
                return r.Name;
            }
            return "route";
        }

        internal static string UnitSuffix(LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? "m"
             : unit == LoopTimeUnit.Hour ? "h"
             : "s";

        internal static LoopTimeUnit CycleRecordingUnit(LoopTimeUnit u)
            => u == LoopTimeUnit.Sec ? LoopTimeUnit.Min
             : u == LoopTimeUnit.Min ? LoopTimeUnit.Hour
             : u == LoopTimeUnit.Hour ? LoopTimeUnit.Auto
             : LoopTimeUnit.Sec;

        /// <summary>
        /// Sets or clears LoopStartUT/LoopEndUT when the loop toggle changes.
        /// On toggle-on: auto-selects the interesting portion (trims boring bookends).
        /// On toggle-off: clears the range back to NaN (full recording).
        /// </summary>
        internal static void ApplyAutoLoopRange(Recording rec, bool loopEnabled)
        {
            if (loopEnabled)
            {
                var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(rec.TrackSections);
                if (!double.IsNaN(start))
                {
                    rec.LoopStartUT = start;
                    rec.LoopEndUT = end;
                    ParsekLog.Info("UI", $"Auto loop range: '{rec.VesselName}' " +
                        $"narrowed to [{start:F1}..{end:F1}] " +
                        $"(trimmed from [{rec.StartUT:F1}..{rec.EndUT:F1}])");
                }
            }
            else
            {
                if (!double.IsNaN(rec.LoopStartUT) || !double.IsNaN(rec.LoopEndUT))
                {
                    rec.LoopStartUT = double.NaN;
                    rec.LoopEndUT = double.NaN;
                    ParsekLog.Info("UI", $"Loop range cleared for '{rec.VesselName}'");
                }
            }
        }

        // --- Loop period cell ---

        private void DrawLoopPeriodCell(Recording rec, int ri)
        {
            // All states use same [value area][unit button] layout. val + Space(4) + unit fills
            // ColW_Period - BodyCellButtonLeftInset. The wrapping BeginHorizontal at the call
            // site provides the Space(BodyCellButtonLeftInset) on the left so val+unit start
            // 10 px into the Period cell.
            // Only a LOOPING row reaches this: DrawRecordingRow draws a blank cell (with the
            // loop-off reason as its hover) while Loop is off.
            const float unitBtnW = 40f;
            float valueBtnW = ColW_Period - unitBtnW - 4f - BodyCellButtonLeftInset;

            if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
            {
                // Auto mode: disabled text field showing global value + "auto" unit button
                var settings = ParsekSettings.Current;
                double globalVal = settings != null
                    ? ParsekUI.ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit)
                    : LoopTiming.DefaultLoopIntervalSeconds;
                GUI.enabled = false;
                var globalDisplayUnit = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                GUILayout.TextField(ParsekUI.FormatLoopValue(globalVal, globalDisplayUnit) + UnitSuffix(globalDisplayUnit), bodyCellTextFieldFlush, GUILayout.Width(valueBtnW));
                DisabledHoverEcho.CarryLastControl(false, LoopPeriodDisabledReason(true, true));
                GUI.enabled = true;
            }
            else
            {
                // Manual mode: editable text field with edit buffer. The read-out is the
                // cadence the engine flies (overlap cap applied); the edit buffer below
                // still seeds from the stored typed value.
                LoopPeriodCellView view = BuildLoopPeriodCellView(
                    rec.LoopIntervalSeconds,
                    GhostPlaybackEngine.EffectiveLoopDuration(rec),
                    rec.LoopTimeUnit,
                    GhostPlayback.MaxOverlapGhostsPerRecording);
                bool displayClamped = view.Clamped;
                string displayText = view.Text;
                string clampTooltip = view.Tooltip;
                if (loopPeriodFocusedRi != ri)
                {
                    // Not editing: show runtime-effective value, but seed the edit buffer
                    // from the stored raw value when the field gains focus.
                    string controlName = "LoopPeriod_" + ri;
                    GUI.SetNextControlName(controlName);
                    Color prevContentColor = GUI.contentColor;
                    if (displayClamped)
                        GUI.contentColor = LoopPeriodClampColor;
                    try
                    {
                        GUILayout.TextField(displayText, bodyCellTextFieldFlush, GUILayout.Width(valueBtnW));
                    }
                    finally
                    {
                        GUI.contentColor = prevContentColor;
                    }
                    Rect valueRect = GUILayoutUtility.GetLastRect();
                    if (displayClamped && valueRect.Contains(Event.current.mousePosition))
                        recordingsWindowTooltipText = clampTooltip;
                    if (GUI.GetNameOfFocusedControl() == controlName)
                    {
                        loopPeriodEditText = FormatLoopPeriodEditStartText(
                            rec.LoopIntervalSeconds, rec.LoopTimeUnit);
                        loopPeriodFocusedRi = ri;
                        loopPeriodEditRect = valueRect;
                        ParsekLog.Verbose("UI",
                            $"Recording '{rec.VesselName}' loop period edit started: " +
                            $"value='{loopPeriodEditText}' unit={ParsekUI.UnitLabel(rec.LoopTimeUnit)}");
                    }
                }
                else
                {
                    // Enter key -> commit (check before TextField, which consumes KeyDown)
                    bool submitPeriod = Event.current.type == EventType.KeyDown &&
                        (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                    // Editing: use buffer, track rect for click-outside
                    GUI.SetNextControlName("LoopPeriod_" + ri);
                    string newText = GUILayout.TextField(loopPeriodEditText, bodyCellTextFieldFlush, GUILayout.Width(valueBtnW));
                    loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                    if (newText != loopPeriodEditText)
                        loopPeriodEditText = newText;

                    if (submitPeriod)
                    {
                        // [ERS-exempt] reason: loopPeriodFocusedRi is an index
                        // into the raw committed list used by the recordings
                        // table. See TODO(phase 6+) on DrawRecordingsWindow.
                        CommitLoopPeriodEdit(RecordingStore.CommittedRecordings);
                        Event.current.Use();
                    }
                }
            }

            // Unit cycling button (shared by both auto and manual modes)
            GUILayout.Space(4f);
            if (GUILayout.Button(
                    new GUIContent(ParsekUI.UnitLabel(rec.LoopTimeUnit),
                        "Click to cycle the period unit: seconds, minutes, hours, then auto. Auto follows the interval set in Settings > Looping."),
                    bodyCellButtonFlush, GUILayout.Width(unitBtnW)))
            {
                var newUnit = CycleRecordingUnit(rec.LoopTimeUnit);
                rec.LoopTimeUnit = newUnit;
                GUIUtility.keyboardControl = 0;
                loopPeriodFocusedRi = -1;
                ParsekLog.Info("UI",
                    $"Recording '{rec.VesselName}' loop unit changed to {newUnit}");
            }
        }

        internal static double ComputeDisplayedLoopPeriod(
            double storedSeconds, double loopDurationSeconds, int cap, out bool clamped)
        {
            double effectiveSeconds = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                storedSeconds, loopDurationSeconds, cap);
            clamped = double.IsNaN(storedSeconds)
                || double.IsInfinity(storedSeconds)
                || Math.Abs(effectiveSeconds - storedSeconds) > 1e-6;
            return effectiveSeconds;
        }

        internal static string FormatLoopPeriodDisplayText(
            double displayedSeconds, LoopTimeUnit unit, bool preserveSecondResolution)
        {
            double displayValue = ParsekUI.ConvertFromSeconds(displayedSeconds, unit);
            if (!preserveSecondResolution)
                return ParsekUI.FormatLoopValue(displayValue, unit);

            return displayValue.ToString("0.######", CultureInfo.InvariantCulture);
        }

        internal static string FormatLoopPeriodEditStartText(
            double storedSeconds, LoopTimeUnit unit)
        {
            double displayValue = ParsekUI.ConvertFromSeconds(storedSeconds, unit);
            if (double.IsNaN(displayValue) || double.IsInfinity(displayValue))
                displayValue = ParsekUI.ConvertFromSeconds(LoopTiming.MinCycleDuration, unit);
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return displayValue.ToString("G17", CultureInfo.InvariantCulture);

            return ParsekUI.FormatLoopValue(displayValue, unit);
        }

        /// <summary>
        /// What a manual-mode Period cell shows while it is NOT being edited: the read-out
        /// text, whether it differs from the stored typed value (amber tint), and the hover
        /// text explaining the difference (empty when it does not differ).
        /// </summary>
        internal struct LoopPeriodCellView
        {
            public string Text;
            public bool Clamped;
            public string Tooltip;
        }

        /// <summary>
        /// Composes the manual-mode Period read-out from the stored typed period. The value
        /// shown is <see cref="GhostPlaybackLogic.ComputeEffectiveLaunchCadence"/> - the same
        /// call the engine makes before flying an overlap loop - so the cell states the cadence
        /// actually flown. An uncapped period renders exactly as the plain stored value. Pure.
        /// </summary>
        internal static LoopPeriodCellView BuildLoopPeriodCellView(
            double storedSeconds, double loopDurationSeconds, LoopTimeUnit unit, int cap)
        {
            bool clamped;
            double displayedSeconds = ComputeDisplayedLoopPeriod(
                storedSeconds, loopDurationSeconds, cap, out clamped);
            return new LoopPeriodCellView
            {
                Text = FormatLoopPeriodDisplayText(displayedSeconds, unit, clamped),
                Clamped = clamped,
                Tooltip = clamped
                    ? BuildLoopPeriodClampTooltip(
                        storedSeconds, displayedSeconds, loopDurationSeconds, cap, unit)
                    : string.Empty,
            };
        }

        /// <summary>
        /// Hover text of a Period cell whose read-out differs from the typed value. Names the
        /// typed value and the value flown in the row's own unit, and why (the overlap cap of
        /// <paramref name="cap"/> live copies, or the minimum period). Same shape as the
        /// Missions tab's clamped period (<see cref="MissionPresentation.PeriodTooltipClamped"/>).
        /// Empty when the two agree. Pure; invariant culture.
        /// </summary>
        internal static string BuildLoopPeriodClampTooltip(
            double storedSeconds, double effectiveSeconds, double loopDurationSeconds, int cap,
            LoopTimeUnit unit)
        {
            bool invalidStored = double.IsNaN(storedSeconds) || double.IsInfinity(storedSeconds);
            if (!invalidStored && Math.Abs(effectiveSeconds - storedSeconds) <= 1e-6)
                return string.Empty;

            double minAdjustedSeconds = invalidStored
                ? LoopTiming.MinCycleDuration
                : Math.Max(storedSeconds, LoopTiming.MinCycleDuration);
            bool capAdjusted = loopDurationSeconds > 0.0 && cap > 0
                && effectiveSeconds - minAdjustedSeconds > 1e-6;

            string effectiveText = FormatLoopPeriodWithUnit(effectiveSeconds, unit);

            if (invalidStored)
            {
                return capAdjusted
                    ? string.Format(CultureInfo.InvariantCulture,
                        "Period repaired to {0} - the stored value was invalid, and at most {1} copies of this flight can play at once.",
                        effectiveText, cap)
                    : string.Format(CultureInfo.InvariantCulture,
                        "Period repaired to {0} - the stored value was invalid.",
                        effectiveText);
            }

            string typedText = FormatLoopPeriodWithUnit(storedSeconds, unit);
            if (capAdjusted)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Period raised from {0} to {1} to fit the overlap cap - at most {2} copies of this flight can play at once.",
                    typedText, effectiveText, cap);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "Period raised from {0} to {1} - the shortest period allowed is {2}.",
                typedText, effectiveText,
                FormatLoopPeriodWithUnit(LoopTiming.MinCycleDuration, unit));
        }

        // A period in the row's unit with its suffix ("6s", "0.1m"), at the clamped read-out's
        // resolution so neither the typed nor the flown value is rounded away.
        private static string FormatLoopPeriodWithUnit(double seconds, LoopTimeUnit unit)
            => FormatLoopPeriodDisplayText(seconds, unit, preserveSecondResolution: true)
                + UnitSuffix(unit);

        private void CommitLoopPeriodEdit(IReadOnlyList<Recording> committed)
        {
            if (loopPeriodFocusedRi < 0 || loopPeriodFocusedRi >= committed.Count) { loopPeriodFocusedRi = -1; return; }
            var rec = committed[loopPeriodFocusedRi];
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double parsed;
            if (ParsekUI.TryParseLoopInput(loopPeriodEditText, rec.LoopTimeUnit, out parsed))
            {
                double newSeconds = ParsekUI.ConvertToSeconds(parsed, rec.LoopTimeUnit);
                // #381: period is launch-to-launch; negatives are rejected outright.
                if (newSeconds < 0)
                {
                    ParsekLog.Warn("UI",
                        $"Recording '{rec.VesselName}' loop period edit rejected: " +
                        $"negative value {newSeconds.ToString("F1", ic)}s " +
                        "(period must be >= 0 under launch-to-launch semantics #381)");
                }
                else
                {
                    // Defensively clamp below-minimum to MinCycleDuration.
                    if (newSeconds < LoopTiming.MinCycleDuration)
                    {
                        ParsekLog.Info("UI",
                            $"Recording '{rec.VesselName}' loop period clamped from " +
                            $"{newSeconds.ToString("F1", ic)}s to " +
                            $"{LoopTiming.MinCycleDuration.ToString("F1", ic)}s " +
                            "(MinCycleDuration)");
                        newSeconds = LoopTiming.MinCycleDuration;
                    }
                    rec.LoopIntervalSeconds = newSeconds;
                    ParsekLog.Info("UI",
                        $"Recording '{rec.VesselName}' loop period updated to " +
                        rec.LoopIntervalSeconds.ToString("F1", ic) +
                        $"s (display: {ParsekUI.FormatLoopValue(ParsekUI.ConvertFromSeconds(newSeconds, rec.LoopTimeUnit), rec.LoopTimeUnit)} " +
                        $"{ParsekUI.UnitLabel(rec.LoopTimeUnit)})");
                }
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Recording '{rec.VesselName}' loop period edit rejected: " +
                    $"invalid input '{loopPeriodEditText}' for unit {rec.LoopTimeUnit}");
            }
            loopPeriodFocusedRi = -1;
            loopPeriodEditRect = default;
            GUIUtility.keyboardControl = 0;
        }
    }
}
