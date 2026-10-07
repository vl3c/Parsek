using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation for the Logistics window's route tables after the Model 1
    /// redesign: the Basic / Advanced tuning gate, the merged Status cell (one colour-coded
    /// word plus a short reason, replacing the old Status + Delivery columns), the Next
    /// countdown (the Missions form: amber "T- " + two units, " (!)" when warned), the
    /// read-only Every cell, the Runs cell, the two-line Route cell's from/to line, which
    /// table a route sits in, and the dated sentences of the expanded detail block.
    /// <para>Unity-free and side-effect-free so every rule is unit tested directly. Dates
    /// come in through a formatter argument (production passes
    /// <see cref="ReservationExplanation.DefaultDateFormatter"/>; a null formatter prints
    /// an invariant "UT n" through <see cref="ReservationExplanation.FormatDate"/>), and
    /// every number formats with <see cref="CultureInfo.InvariantCulture"/>.</para>
    /// <para>Player text here never says cycle, dispatch, ghost, tree, recording or loop:
    /// a route makes RUNS, and the flights it copies are FLIGHTS.</para>
    /// </summary>
    internal static class LogisticsRoutePresentation
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------
        // Basic / Advanced gate
        // ------------------------------------------------------------------

        /// <summary>
        /// True when the route TUNING controls draw (the Every stepper, the Runs column,
        /// Priority, Link round-trip, Recent runs, Flights used). Derived from
        /// <see cref="UiSurfaceVisibility.IsVisible"/> like
        /// <c>MissionsWindowUI.ShowsLoopAuthoringControls</c>; every draw site passes the
        /// frame-latched <see cref="ParsekUI.AppliedUiComplexityMode"/>.
        /// </summary>
        internal static bool ShowsRouteTuning(UiComplexityMode mode)
        {
            return UiSurfaceVisibility.IsVisible(UiSurface.LogisticsRouteTuning, mode);
        }

        /// <summary>
        /// The sort column the tables actually sort by this frame. A column with no header
        /// in the current mode (Runs is Advanced-only) falls back to the Route name, so a
        /// player who sorted by Runs in Advanced and switched to Basic never reads a table
        /// sorted by an invisible key. The STORED sort is never changed: switching back to
        /// Advanced restores it.
        /// </summary>
        internal static LogisticsRouteSortColumn EffectiveSortColumn(
            LogisticsRouteSortColumn stored, bool showsTuning)
        {
            if (!showsTuning && stored == LogisticsRouteSortColumn.Cycles)
                return LogisticsRouteSortColumn.Name;
            return stored;
        }

        // ------------------------------------------------------------------
        // Section title bars
        // ------------------------------------------------------------------

        /// <summary>The Active section's name (also its collapse-state key).</summary>
        internal const string ActiveSectionName = "Active Routes";

        /// <summary>The Paused section's name (also its collapse-state key).</summary>
        internal const string PausedSectionName = "Paused Routes";

        /// <summary>The Candidates section's name (also its collapse-state key).</summary>
        internal const string CandidatesSectionName = "Candidates";

        /// <summary>How many points the section title is over the shared section header.</summary>
        internal const int SectionTitleFontStep = 2;

        /// <summary>The height of the section accent bar, in pixels.</summary>
        internal const float SectionAccentHeight = 2f;

        /// <summary>
        /// A section's accent bar colour: green Active Routes, soft violet Paused Routes,
        /// cyan Candidates; grey for any other name.
        /// </summary>
        internal static ParsekUI.StatusColorKind SectionAccent(string sectionName)
        {
            switch (sectionName)
            {
                case ActiveSectionName: return ParsekUI.StatusColorKind.Green;
                case PausedSectionName: return ParsekUI.StatusColorKind.Violet;
                case CandidatesSectionName: return ParsekUI.StatusColorKind.Cyan;
                default: return ParsekUI.StatusColorKind.Grey;
            }
        }

        // ------------------------------------------------------------------
        // Detail-block steppers (Every / Priority): one fixed grid
        // ------------------------------------------------------------------

        /// <summary>The label column ("Every:" / "Priority:") both steppers share.</summary>
        internal const float StepperLabelWidth = 70f;

        /// <summary>The width of every stepper "-" and "+" button.</summary>
        internal const float StepperButtonWidth = 24f;

        /// <summary>Space added to the widest measured value so the text never touches "+".</summary>
        internal const float StepperValuePadding = 8f;

        /// <summary>The narrowest the shared value cell gets, whatever the measure says.</summary>
        internal const float StepperValueMinWidth = 110f;

        /// <summary>The largest multiplier the width samples cover (two digits).</summary>
        internal const int StepperSampleMaxMultiplier = 99;

        /// <summary>The Every readout on a flat schedule: "2x (~4.0d)".</summary>
        internal const string CadenceReadoutFormat = "{0}x (~{1})";

        /// <summary>
        /// The value texts the shared stepper cell must hold: every windowed Every readout up
        /// to <see cref="StepperSampleMaxMultiplier"/> ("1x (every window)", "23x (every 23rd
        /// window)"), the flat readout with a two-digit multiplier and a four-digit day count
        /// ("99x (~9999.9d)"), and a three-digit Priority. The window measures each with its
        /// label style once, so both steppers get one value width.
        /// </summary>
        internal static List<string> StepperValueSamples()
        {
            var samples = new List<string>(StepperSampleMaxMultiplier + 2);
            for (int n = 1; n <= StepperSampleMaxMultiplier; n++)
                samples.Add(RouteWindowBasisPresentation.FormatWindowedCadence(n));
            samples.Add(string.Format(IC, CadenceReadoutFormat, StepperSampleMaxMultiplier, "9999.9d"));
            samples.Add("999");
            return samples;
        }

        /// <summary>
        /// The one value-cell width both steppers use: the widest of
        /// <see cref="StepperValueSamples"/> under <paramref name="measure"/> (the label
        /// style's width of a text) plus <see cref="StepperValuePadding"/>, rounded up, never
        /// under <see cref="StepperValueMinWidth"/>. So both "-" buttons share one column and
        /// both "+" buttons another, whatever the values.
        /// </summary>
        internal static float StepperValueCellWidth(Func<string, float> measure)
        {
            float widest = 0f;
            if (measure != null)
            {
                List<string> samples = StepperValueSamples();
                for (int i = 0; i < samples.Count; i++)
                {
                    float w = measure(samples[i]);
                    if (w > widest && !float.IsNaN(w) && !float.IsInfinity(w))
                        widest = w;
                }
            }
            return Math.Max(StepperValueMinWidth, (float)Math.Ceiling(widest + StepperValuePadding));
        }

        /// <summary>
        /// The one height both stepper rows take: the taller of a detail line that carries a
        /// slot button (<paramref name="slotRowHeight"/>, e.g. "Link round-trip...") and a
        /// plain label line (<paramref name="labelRowHeight"/>), rounded up. Whether the
        /// block's slot column reaches the Every line depends on how many lines precede it,
        /// so a fixed height keeps the two stepper rows equal in every block.
        /// </summary>
        internal static float StepperRowHeight(float slotRowHeight, float labelRowHeight)
        {
            float h = Math.Max(Sanitize(slotRowHeight), Sanitize(labelRowHeight));
            return (float)Math.Ceiling(h);
        }

        private static float Sanitize(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? 0f : v;
        }

        /// <summary>
        /// A section title bar's text: the caret (the route rows' glyphs, down when
        /// expanded, right when collapsed), the name and its count, "\u25bc Active Routes (2)".
        /// </summary>
        internal static string FormatSectionTitle(string name, int count, bool expanded)
        {
            return (expanded ? "\u25bc " : "\u25b6 ") + (name ?? string.Empty)
                + " (" + Math.Max(0, count).ToString(IC) + ")";
        }

        // ------------------------------------------------------------------
        // Which table a route sits in
        // ------------------------------------------------------------------

        /// <summary>
        /// True when a route belongs in the Paused table: a Paused route only. A route
        /// armed by Send sits in ACTIVE while it sends (the countdown to its window, then
        /// the run in flight); when the run completes the backend's PauseAfterCurrentCycle
        /// returns it to Paused, and the row moves with it. Running, held, pause-armed and
        /// broken routes all sit in Active.
        /// </summary>
        internal static bool BelongsInPausedTable(RouteStatus status)
        {
            return status == RouteStatus.Paused;
        }

        // ------------------------------------------------------------------
        // Merged Status cell
        // ------------------------------------------------------------------

        /// <summary>The one word a route's Status cell leads with.</summary>
        internal enum StatusWord
        {
            /// <summary>Running on its schedule and has delivered at least once (green).</summary>
            Delivering = 0,

            /// <summary>Running on its schedule, nothing delivered yet (white).</summary>
            Scheduled = 1,

            /// <summary>Its last run was held back for a missing condition (yellow).</summary>
            Held = 2,

            /// <summary>Paused after running before (grey).</summary>
            Paused = 3,

            /// <summary>Paused and never run (cyan).</summary>
            New = 4,

            /// <summary>Armed by Send: one run, then paused again (cyan).</summary>
            SendingOnce = 5,

            /// <summary>Armed by Pause while a run is in flight (cyan).</summary>
            PausingAfterRun = 6,

            /// <summary>Cannot run until the player acts (red).</summary>
            Broken = 7,
        }

        /// <summary>The visible Status cell: its word, its full text and its colour.</summary>
        internal struct StatusCell
        {
            internal StatusWord Word;

            /// <summary>The cell text, e.g. "Delivering", "Held: B short 108.8 LiquidFuel".</summary>
            internal string Text;

            /// <summary>The shared palette colour, or null for the label's default white.</summary>
            internal ParsekUI.StatusColorKind? Color;
        }

        /// <summary>Visible text of the Delivering word.</summary>
        internal const string DeliveringText = "Delivering";

        /// <summary>Visible text of the Scheduled word.</summary>
        internal const string ScheduledText = "Scheduled";

        /// <summary>Visible text of the Paused word.</summary>
        internal const string PausedText = "Paused";

        /// <summary>Visible text of the New word.</summary>
        internal const string NewText = "New";

        /// <summary>Visible text of a route armed by Send.</summary>
        internal const string SendingOnceText = "Sending one run";

        /// <summary>Visible text of a route armed by Pause while a run is in flight.</summary>
        internal const string PausingAfterRunText = "Pausing after this run";

        /// <summary>
        /// Classifies the merged Status cell. Precedence: a hard-broken status first (it
        /// cannot run whatever else is true), then the two armed states (the player's last
        /// click), then a displayable hold (yellow, on Paused rows too: a route that was
        /// refused twice must not read "New"), then the three wait statuses with no hold
        /// recorded, then Paused / New by whether it ever delivered, then Delivering /
        /// Scheduled.
        /// </summary>
        /// <param name="status">The route's status.</param>
        /// <param name="sendingOnce">Armed by Send (one run, then paused again).</param>
        /// <param name="pausingAfterRun">Armed by Pause while a run is in flight.</param>
        /// <param name="holdCellText">The compact "Held: ..." text when a hold displays
        /// (<see cref="LogisticsHoldPresentation.StatusCellText"/> behind
        /// <see cref="LogisticsHoldPresentation.ShouldDisplayHold"/>), else null.</param>
        /// <param name="completedRuns">Runs that delivered.</param>
        /// <param name="originLost">An EndpointLost route whose ORIGIN (not destination)
        /// vessel is gone.</param>
        internal static StatusCell ClassifyStatus(
            RouteStatus status, bool sendingOnce, bool pausingAfterRun,
            string holdCellText, int completedRuns, bool originLost)
        {
            switch (status)
            {
                case RouteStatus.EndpointLost:
                    return Cell(StatusWord.Broken,
                        originLost ? BrokenPrefix + "origin lost" : BrokenPrefix + "destination lost",
                        ParsekUI.StatusColorKind.Red);
                case RouteStatus.MissingSourceRecording:
                    return Cell(StatusWord.Broken, BrokenPrefix + "flight missing", ParsekUI.StatusColorKind.Red);
                case RouteStatus.SourceChanged:
                    return Cell(StatusWord.Broken, BrokenPrefix + "flight changed", ParsekUI.StatusColorKind.Red);
            }

            if (sendingOnce)
                return Cell(StatusWord.SendingOnce, SendingOnceText, ParsekUI.StatusColorKind.Cyan);
            if (pausingAfterRun)
                return Cell(StatusWord.PausingAfterRun, PausingAfterRunText, ParsekUI.StatusColorKind.Cyan);

            if (!string.IsNullOrEmpty(holdCellText))
                return Cell(StatusWord.Held, holdCellText, ParsekUI.StatusColorKind.Yellow);

            switch (status)
            {
                case RouteStatus.WaitingForResources:
                    return Cell(StatusWord.Held, "Held: origin short of cargo", ParsekUI.StatusColorKind.Yellow);
                case RouteStatus.WaitingForFunds:
                    return Cell(StatusWord.Held, "Held: not enough funds", ParsekUI.StatusColorKind.Yellow);
                case RouteStatus.DestinationFull:
                    return Cell(StatusWord.Held, "Held: destination full", ParsekUI.StatusColorKind.Yellow);
                case RouteStatus.Paused:
                    return completedRuns > 0
                        ? Cell(StatusWord.Paused, PausedText, ParsekUI.StatusColorKind.Grey)
                        : Cell(StatusWord.New, NewText, ParsekUI.StatusColorKind.Cyan);
            }

            return completedRuns > 0
                ? Cell(StatusWord.Delivering, DeliveringText, ParsekUI.StatusColorKind.Green)
                : Cell(StatusWord.Scheduled, ScheduledText, null);
        }

        private static StatusCell Cell(StatusWord word, string text, ParsekUI.StatusColorKind? color)
        {
            return new StatusCell { Word = word, Text = text, Color = color };
        }

        /// <summary>
        /// The Status cell's hover: one full sentence, participle first, with an exact date
        /// where the stored value supports one. <paramref name="heldSentence"/> is the
        /// detail block's dated hold line (<see cref="LogisticsHoldPresentation.FormatHoldDetailLine"/>) and wins for a Held
        /// or Broken word that has one; <paramref name="nextRunUT"/> is the countdown's
        /// target (NaN when none). Never names the raw status enum.
        /// </summary>
        internal static string StatusTooltip(
            StatusCell cell, RouteStatus status, string heldSentence, int completedRuns,
            double createdUT, double nextRunUT, bool originLost, Func<double, string> formatDate)
        {
            switch (cell.Word)
            {
                case StatusWord.Delivering:
                    return string.Format(IC, "Running on its schedule. {0} delivered so far.",
                        Plural(completedRuns, "run", "runs"));
                case StatusWord.Scheduled:
                    return "Running on its schedule. No run has delivered yet.";
                case StatusWord.Paused:
                    return "Paused: not running on its schedule. Activate resumes it; Send makes one run.";
                case StatusWord.New:
                    return createdUT >= 0.0
                        ? "Created on " + ReservationExplanation.FormatDate(createdUT, formatDate)
                          + ", not run yet. Send makes one run as a test."
                        : "Not run yet. Send makes one run as a test.";
                case StatusWord.SendingOnce:
                    return IsUsableUT(nextRunUT)
                        ? "Sending one run at the next window on "
                          + ReservationExplanation.FormatDate(nextRunUT, formatDate)
                          + ", then pausing again."
                        : "Sending one run at the next window, then pausing again.";
                case StatusWord.PausingAfterRun:
                    return "Pausing after the run in flight lands.";
                case StatusWord.Held:
                    if (!string.IsNullOrEmpty(heldSentence))
                        return heldSentence;
                    switch (status)
                    {
                        case RouteStatus.WaitingForFunds:
                            return "Held: not enough funds for a run. Runs again once funds allow.";
                        case RouteStatus.DestinationFull:
                            return "Held: the destination has no room for the delivery. Runs again once it fits.";
                        default:
                            return "Held: the origin is short of the cargo for a run. Runs again once it has the full amount.";
                    }
                case StatusWord.Broken:
                default:
                    switch (status)
                    {
                        case RouteStatus.EndpointLost:
                            return originLost
                                ? "Stopped: the origin vessel is gone (recovered or destroyed). Delete this route and create it again from the mission."
                                : "Stopped: the destination vessel is gone (recovered or destroyed). Use Re-scan to find it, or delete this route.";
                        case RouteStatus.SourceChanged:
                            return "Stopped: a flight this route copies has changed. Delete this route and create it again from the mission.";
                        default:
                            return "Stopped: a flight this route copies is no longer available. Delete this route and create it again from the mission.";
                    }
            }
        }

        /// <summary>
        /// The merged Status sort key: the cell text, so the column sorts by exactly what
        /// the player reads. The retired Delivery column's sort member aliases it.
        /// </summary>
        internal static string StatusSortKey(StatusCell cell)
        {
            return cell.Text ?? string.Empty;
        }

        private const string BrokenPrefix = "Broken: ";

        /// <summary>
        /// The short reason of a Broken Status cell ("destination lost", "flight missing",
        /// "flight changed"), or null for any other word. The Send button's greyed reason
        /// reads it.
        /// </summary>
        internal static string BrokenShortReason(StatusCell cell)
        {
            if (cell.Word != StatusWord.Broken || string.IsNullOrEmpty(cell.Text))
                return null;
            return cell.Text.StartsWith(BrokenPrefix, StringComparison.Ordinal)
                ? cell.Text.Substring(BrokenPrefix.Length)
                : cell.Text;
        }

        // ------------------------------------------------------------------
        // Next column (the Missions countdown, reused exactly)
        // ------------------------------------------------------------------

        /// <summary>
        /// The Next cell: the house countdown <see cref="ParsekTimeFormat.FormatCountdown"/>
        /// ("T- 2d 4h", the Missions summary's form; a moment already reached reads "T- 0s")
        /// with its " (!)" when warned, or
        /// "-" when no run is scheduled. <paramref name="runScheduled"/> is false for a
        /// Paused route (a Send-armed route sits in Active and keeps its countdown, to the
        /// window and then to the arrival). The window draws it in <c>ParsekUI.CountdownTextColor</c>.
        /// </summary>
        internal static string FormatNextCell(
            LogisticsCountdownPresentation.CountdownBranch branch, double seconds,
            bool runScheduled, bool warned)
        {
            if (!runScheduled || branch == LogisticsCountdownPresentation.CountdownBranch.None)
                return "-";
            return ParsekTimeFormat.FormatCountdown(seconds > 0 ? seconds : 0, warned);
        }

        /// <summary>
        /// The Next cell's hover: the exact date of what it counts down to. A countdown to a
        /// recheck says so, because a held route's next date is when it tries again.
        /// </summary>
        internal static string FormatNextTooltip(
            LogisticsCountdownPresentation.CountdownBranch branch, double targetUT,
            bool runScheduled, bool warned, Func<double, string> formatDate)
        {
            if (!runScheduled || branch == LogisticsCountdownPresentation.CountdownBranch.None
                || !IsUsableUT(targetUT))
                return "No run scheduled. Activate runs this route on its schedule; Send makes one run.";
            string date = ReservationExplanation.FormatDate(targetUT, formatDate);
            string head;
            switch (branch)
            {
                case LogisticsCountdownPresentation.CountdownBranch.RechecksIn:
                    head = "Tries the held run again on " + date + ".";
                    break;
                case LogisticsCountdownPresentation.CountdownBranch.NextWindow:
                    head = "Next launch window on " + date + ".";
                    break;
                default:
                    head = "Next delivery on " + date + ".";
                    break;
            }
            return warned ? head + " (!) The last run was held; see Status." : head;
        }

        // ------------------------------------------------------------------
        // Every / Runs cells
        // ------------------------------------------------------------------

        /// <summary>
        /// The read-only Every cell ("every 4.0d"; "every window" / "every 2nd window" for a
        /// route that runs on launch windows). Basic's form of the Advanced stepper, and the
        /// form every mode shows for a Send-armed route, whose schedule does not apply to
        /// its single run. <paramref name="formattedInterval"/> is the window's decimal
        /// duration (the same text the stepper field holds).
        /// </summary>
        internal static string FormatEveryReadOnly(bool windowedBasis, int multiplier, string formattedInterval)
        {
            if (windowedBasis)
            {
                int n = Route.ClampCadenceMultiplier(multiplier);
                return n == 1
                    ? "every window"
                    : "every " + RouteWindowBasisPresentation.Ordinal(n) + " window";
            }
            if (string.IsNullOrEmpty(formattedInterval) || formattedInterval == "-")
                return "-";
            return "every " + formattedInterval;
        }

        /// <summary>The read-only Every cell's hover.</summary>
        internal static string EveryTooltip(bool windowedBasis, string basisLabel)
        {
            return windowedBasis
                ? "How often this route runs: on launch windows " + (basisLabel ?? string.Empty)
                  + ". Advanced mode can change it."
                : "How often this route runs. Advanced mode can change it.";
        }

        /// <summary>
        /// The Advanced Runs cell: runs that delivered, plus ", N held" when runs were held
        /// back for a missing condition.
        /// </summary>
        internal static string FormatRunsCell(int completed, int held)
        {
            string done = Math.Max(0, completed).ToString(IC);
            return held > 0 ? done + ", " + held.ToString(IC) + " held" : done;
        }

        /// <summary>The Runs header and cell hover.</summary>
        internal const string RunsTooltip =
            "Runs that delivered, and runs held back for a missing condition.";

        // ------------------------------------------------------------------
        // Route cell second line
        // ------------------------------------------------------------------

        /// <summary>
        /// The origin's short name for the Route cell's second line: "KSC" for a
        /// funds-paid launch, "Harvested" for a harvest route, else the resolved origin
        /// vessel name, else "-".
        /// </summary>
        internal static string FormatOriginShort(bool kscOrigin, bool harvestOrigin, string originVesselName)
        {
            if (kscOrigin) return "KSC";
            if (harvestOrigin) return "Harvested";
            return string.IsNullOrEmpty(originVesselName) ? "-" : originVesselName;
        }

        /// <summary>
        /// The arrow between a route's two ends: U+2192, the glyph the fallback route names
        /// draw ("Route: KSC [U+2192] Duna", <c>RouteCreationFormatters</c>), so such a
        /// name and the line under it read alike in the same font.
        /// </summary>
        internal const string FromToArrow = " \u2192 ";

        /// <summary>The Route cell's grey second line: "KSC [U+2192] Depot Station Duna I".</summary>
        internal static string FormatFromTo(string originShort, string destination)
        {
            return (string.IsNullOrEmpty(originShort) ? "-" : originShort)
                + FromToArrow + (string.IsNullOrEmpty(destination) ? "-" : destination);
        }

        // ------------------------------------------------------------------
        // Expanded detail block sentences
        // ------------------------------------------------------------------

        /// <summary>"Delivers each run: LiquidFuel 257.8, Oxidizer 315.1 to Depot Station Duna I."</summary>
        internal static string FormatDeliversEachRun(string manifest, string destination)
        {
            string what = string.IsNullOrEmpty(manifest) ? "(nothing)" : manifest;
            return string.IsNullOrEmpty(destination) || destination == "-"
                ? "Delivers each run: " + what + "."
                : "Delivers each run: " + what + " to " + destination + ".";
        }

        /// <summary>
        /// The detail block's schedule line: "Next launch window on Y2, D110, 03:12; arrives
        /// 1y 29d later." / "Next delivery on ...". <paramref name="formattedTransit"/> is
        /// <see cref="ParsekTimeFormat.FormatDuration"/> of the route's transit (null or "0s"
        /// drops the clause). Returns null when no run is scheduled.
        /// </summary>
        internal static string FormatNextLine(
            LogisticsCountdownPresentation.CountdownBranch branch, double targetUT,
            bool runScheduled, string formattedTransit, Func<double, string> formatDate)
        {
            if (!runScheduled || branch == LogisticsCountdownPresentation.CountdownBranch.None
                || !IsUsableUT(targetUT))
                return null;
            string date = ReservationExplanation.FormatDate(targetUT, formatDate);
            bool hasTransit = !string.IsNullOrEmpty(formattedTransit) && formattedTransit != "0s";
            switch (branch)
            {
                case LogisticsCountdownPresentation.CountdownBranch.RechecksIn:
                    return "Tries the held run again on " + date + ".";
                case LogisticsCountdownPresentation.CountdownBranch.NextWindow:
                    return hasTransit
                        ? "Next launch window on " + date + "; arrives " + formattedTransit + " later."
                        : "Next launch window on " + date + ".";
                default:
                    return "Next delivery on " + date + ".";
            }
        }

        /// <summary>
        /// "Last delivered on Y1, D05, 22:40: delivered 200.0 LiquidFuel. Delivered so far:
        /// 200.0 LiquidFuel in 1 run." from the route's RouteCargoDelivered ledger rows (the
        /// only reliable record of a delivery; the route stores no delivered UT). Returns
        /// null when nothing was delivered.
        /// </summary>
        internal static string FormatLastDeliveredLine(
            bool hasDeliveries, double lastUT, string lastText, string cumulativeText,
            int completedRuns, Func<double, string> formatDate)
        {
            if (!hasDeliveries || string.IsNullOrEmpty(lastText))
                return null;
            var sb = new StringBuilder("Last delivered");
            if (IsUsableUT(lastUT))
                sb.Append(" on ").Append(ReservationExplanation.FormatDate(lastUT, formatDate));
            sb.Append(": ").Append(EndSentence(lastText));
            if (!string.IsNullOrEmpty(cumulativeText))
            {
                sb.Append(" Delivered so far: ").Append(cumulativeText);
                if (completedRuns > 0)
                    sb.Append(" in ").Append(Plural(completedRuns, "run", "runs"));
                sb.Append('.');
            }
            return sb.ToString();
        }

        /// <summary>
        /// The information line a detail block too short to host its Interact buttons gets
        /// (never an empty line): "Delivered so far: 400.0 LiquidFuel." when the ledger has a
        /// delivery, else "Not run yet.".
        /// </summary>
        internal static string FormatFillerInfoLine(bool hasDeliveries, string cumulativeText)
        {
            return hasDeliveries && !string.IsNullOrEmpty(cumulativeText)
                ? "Delivered so far: " + cumulativeText + "."
                : "Not run yet.";
        }

        /// <summary>
        /// "Built from mission 'Duna Supply 1'." In Advanced the manual-looping clause
        /// follows ("Manual looping of that mission is off while this route exists.");
        /// Basic has no loop control for it to explain.
        /// </summary>
        internal static string FormatBuiltFromMission(string missionName, bool showsTuning)
        {
            string name = string.IsNullOrEmpty(missionName) ? "<unknown mission>" : missionName;
            string head = "Built from mission '" + name + "'.";
            return showsTuning
                ? head + " Manual looping of that mission is off while this route exists."
                : head;
        }

        /// <summary>
        /// The Advanced "Flights used:" line: the flights the route copies, in order, with
        /// a repeated name numbered "Name [1]", "Name [2]" (the Mission Log convention)
        /// instead of the old "rec N of tree 'X'" clauses. Null / empty names fall back to
        /// <paramref name="fallbacks"/> (the short ids) entry by entry.
        /// </summary>
        internal static string FormatFlightsUsed(IReadOnlyList<string> names, IReadOnlyList<string> fallbacks)
        {
            int count = names?.Count ?? 0;
            if (count == 0)
                return "Flights used: -";
            var resolved = new string[count];
            for (int i = 0; i < count; i++)
            {
                string n = names[i];
                if (string.IsNullOrEmpty(n))
                    n = fallbacks != null && i < fallbacks.Count && !string.IsNullOrEmpty(fallbacks[i])
                        ? fallbacks[i] : "<unknown>";
                resolved[i] = n;
            }
            return "Flights used: " + string.Join(", ",
                LogisticsNearMissPresentation.NumberRepeatedNames(resolved));
        }

        /// <summary>
        /// The Route History's change signature from its inputs: the ledger version, the
        /// tombstone version, whether the route resolves and its name. Pure; an in-memory
        /// compare value, never persisted.
        /// </summary>
        internal static int RouteHistoryChangeSignature(
            int ledgerVersion, int tombstoneVersion, bool routeFound, string routeName)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + ledgerVersion;
                h = h * 31 + tombstoneVersion;
                h = h * 31 + (routeFound ? 1 : 0);
                h = h * 31 + (routeName != null ? StringComparer.Ordinal.GetHashCode(routeName) : 0);
                return h;
            }
        }

        // ------------------------------------------------------------------
        // Interact column
        // ------------------------------------------------------------------

        /// <summary>The greyed line-1 label of a route armed by Pause.</summary>
        internal const string PausingButtonLabel = "Pausing...";

        /// <summary>Line-1 state label of a paused route.</summary>
        internal const string ActivateButtonLabel = "Activate";

        /// <summary>Line-1 state label of an active route.</summary>
        internal const string PauseButtonLabel = "Pause";

        /// <summary>Line-1 Send label (one run, then stay Paused).</summary>
        internal const string SendButtonLabel = "Send";

        /// <summary>Line-2 label: the mission the route repeats, on the Missions tab. Spelled
        /// "Go to", as every window spells the cross-link.</summary>
        internal const string GoToButtonLabel = "Go to";

        /// <summary>Line-2 label: the route's Route History window.</summary>
        internal const string LogButtonLabel = "Log";

        /// <summary>Every label the Interact grid can draw; the pair cell is sized to the
        /// widest of them in the skin's pair-button style.</summary>
        internal static readonly string[] InteractGridLabels =
        {
            ActivateButtonLabel, PauseButtonLabel, CancelButtonLabel, DeliveringButtonLabel, PausingButtonLabel,
            SendButtonLabel, GoToButtonLabel, LogButtonLabel,
        };

        /// <summary>
        /// One grid cell's width from the widest measured label: never narrower than the
        /// Missions pair half, and rounded up to a whole pixel plus 2 px so the measured
        /// label never clips. Pure.
        /// </summary>
        internal static float InteractPairWidth(float widestLabelWidth)
        {
            float w = (float)Math.Ceiling(Math.Max(0f, widestLabelWidth)) + 2f;
            return Math.Max(MissionsWindowUI.InteractPairButtonWidth, w);
        }

        /// <summary>The Interact column's width for a pair cell: two cells, the gap and the
        /// two insets, so the 2x2 grid fills it exactly. Pure.</summary>
        internal static float InteractColumnWidth(float pairWidth)
        {
            return 2f * pairWidth + MissionsWindowUI.InteractButtonGap
                + 2f * MissionsWindowUI.InteractCellInset;
        }

        /// <summary>Why Go to is greyed, or empty when it is live: it needs a recording of
        /// the route's source mission that is still in the effective set.</summary>
        internal static string GoToDisabledReason(bool sourceMissionFound)
        {
            return sourceMissionFound
                ? string.Empty
                : "The mission this route was built from no longer exists";
        }

        // ----- Update parts (owner ruling 2026-10-07) -----

        /// <summary>The detail block's Update parts single: re-captures every endpoint's
        /// current parts (<see cref="RouteEndpointPartAdoption"/>). Short enough for the
        /// block's 100 px single; the hover explains it.</summary>
        internal const string UpdatePartsButtonLabel = "Update parts";

        /// <summary>The Update parts hover: what the press counts, that an endpoint docked into
        /// a bigger craft is refused (it keeps its own parts), and that any other docked ship is
        /// taken with the station (so undock visitors first).</summary>
        internal const string UpdatePartsTooltip =
            "Counts every part now docked to the origin and each stop as its own, so later modules "
            + "get cargo; a stop docked into a bigger station keeps its own parts. Undock visiting ships first.";

        /// <summary>Why Update parts is greyed while a run is under way.</summary>
        internal const string UpdatePartsInFlightReason =
            "A run is under way; update parts after it has delivered";

        /// <summary>
        /// True while a run of <paramref name="route"/> is under way, i.e. its cargo was
        /// gated (and the origin debited) against the part sets the route held at dispatch
        /// and some of it is still to be written: a self-timer run in transit or with its
        /// arrival pending delivery, or a multi-stop loop cycle that has fired some of its
        /// stops but not all (each stop keeps the cycle it last fired; they agree between
        /// cycles). The self-timer's cycle-start stamp is not read: a delivery never clears
        /// it. A single-stop loop run fires in one tick and is never under way here, and a
        /// paused loop route's half-fired cycle is never finished (Activate resets every
        /// stop's cursor), so it is not under way either.
        /// </summary>
        internal static bool IsRunInFlight(Route route)
        {
            if (route == null)
                return false;
            if (route.Status == RouteStatus.InTransit || route.PendingDeliveryUT.HasValue)
                return true;
            if (route.Status == RouteStatus.Paused || route.Stops == null || route.Stops.Count < 2)
                return false;
            bool any = false;
            long lowest = long.MaxValue;
            long highest = long.MinValue;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null)
                    continue;
                any = true;
                if (stop.LastFiredCycleIndex < lowest) lowest = stop.LastFiredCycleIndex;
                if (stop.LastFiredCycleIndex > highest) highest = stop.LastFiredCycleIndex;
            }
            return any && highest > lowest;
        }

        /// <summary>Why Update parts is greyed, or empty when it is live. Live on every
        /// status (a held, paused or broken route can still have its stations updated);
        /// greyed only while a run is under way (<see cref="IsRunInFlight"/>).</summary>
        internal static string UpdatePartsDisabledReason(Route route)
        {
            return IsRunInFlight(route) ? UpdatePartsInFlightReason : string.Empty;
        }

        /// <summary>The Update parts hover, plus "Last updated on &lt;date&gt;." when the route
        /// has an adoption (<paramref name="lastAdoptedUT"/> usable): the window has no
        /// outcome line, so the hover is where the last press shows.</summary>
        internal static string FormatUpdatePartsTooltip(double lastAdoptedUT, Func<double, string> formatDate)
        {
            if (!IsUsableUT(lastAdoptedUT))
                return UpdatePartsTooltip;
            return UpdatePartsTooltip + " Last updated on "
                + ReservationExplanation.FormatDate(lastAdoptedUT, formatDate) + ".";
        }

        /// <summary>The live line-1 label of a Send-armed route before its run launches.</summary>
        internal const string CancelButtonLabel = "Cancel";

        /// <summary>The greyed line-1 label of a Send-armed route whose run is in flight.</summary>
        internal const string DeliveringButtonLabel = "Delivering...";

        /// <summary>The Cancel hover: the arm is cleared, nothing was dispatched.</summary>
        internal const string CancelButtonTooltip = "Cancels the run before launch; nothing is spent.";

        /// <summary>Candidate Interact line 1: make the supply run a stored route.</summary>
        internal const string CreateRouteButtonLabel = "Create route";

        /// <summary>The Create route hover.</summary>
        internal const string CreateRouteButtonTooltip =
            "Make this supply run a route (created Paused; use Send to test it, then Activate).";

        /// <summary>Candidate Interact line 2, and each mission row of the near-miss list.</summary>
        internal const string DismissButtonLabel = "Dismiss";

        /// <summary>The Dismiss hover.</summary>
        internal const string DismissButtonTooltip =
            "Hide this mission from the Candidates section. Restore it any time from the Hidden missions list below.";

        /// <summary>
        /// Cuts a runtime-composed hover to the single-line strip: unchanged when it fits
        /// (or <paramref name="maxChars"/> is 0 or less), else cut at the last word that
        /// fits and ended with "...". Never longer than <paramref name="maxChars"/>.
        /// </summary>
        internal static string CapToStrip(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
                return text ?? string.Empty;
            const string Ellipsis = "...";
            if (maxChars <= Ellipsis.Length)
                return Ellipsis.Substring(0, maxChars);
            int keep = maxChars - Ellipsis.Length;
            int space = text.LastIndexOf(' ', keep);
            if (space > keep / 2)
                keep = space;
            return text.Substring(0, keep).TrimEnd(' ', ',', ';', '-') + Ellipsis;
        }

        /// <summary>The candidates' Transit header and cell hover.</summary>
        internal const string CandidateTransitTooltip =
            "How long one run takes, from launch to undock.";

        /// <summary>What an armed route's Interact line 1 draws. All three are ONE 100 px
        /// button in the same control slot, so a phase change never changes the control
        /// count.</summary>
        internal enum ArmedLine1
        {
            /// <summary>Not armed: Activate / Pause.</summary>
            None = 0,

            /// <summary>Send-armed, countdown phase: a live Cancel (routes to TryPause, which
            /// clears the pending arm with nothing dispatched).</summary>
            Cancel = 1,

            /// <summary>Send-armed, run in flight: a greyed Delivering... (a launched run is
            /// paid for and on the timeline; there is no abort path).</summary>
            Delivering = 2,

            /// <summary>Pause-armed while a run is in flight: a greyed Pausing...</summary>
            Pausing = 3,
        }

        /// <summary>
        /// Picks an armed route's Interact line 1. A Send arm is in its countdown phase
        /// until the run launches (status InTransit), then in flight.
        /// </summary>
        internal static ArmedLine1 ResolveArmedLine1(bool sendingOnce, bool pausingAfterRun, RouteStatus status)
        {
            if (sendingOnce)
                return status == RouteStatus.InTransit ? ArmedLine1.Delivering : ArmedLine1.Cancel;
            return pausingAfterRun ? ArmedLine1.Pausing : ArmedLine1.None;
        }

        /// <summary>
        /// The greyed Delivering... hover (and the in-flight Status hover): "Launched on
        /// &lt;date&gt;; arrives on &lt;date&gt;, then pauses again. A launched run cannot be
        /// called back." An unknown launch or arrival UT drops its clause.
        /// </summary>
        internal static string FormatDeliveringTooltip(double launchUT, double arriveUT, Func<double, string> formatDate)
        {
            var sb = new StringBuilder();
            if (IsUsableUT(launchUT))
                sb.Append("Launched on ").Append(ReservationExplanation.FormatDate(launchUT, formatDate)).Append("; ");
            sb.Append(IsUsableUT(arriveUT)
                ? "arrives on " + ReservationExplanation.FormatDate(arriveUT, formatDate) + ", then pauses again."
                : "arrives, then pauses again.");
            sb.Append(" A launched run cannot be called back.");
            string text = sb.ToString();
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>
        /// Why the Send button is greyed, or empty when it is live. Send makes one run of a
        /// paused route; a broken route cannot run at all (<paramref name="brokenReason"/>,
        /// the short reason of its "Broken: ..." Status cell), a running route is already on
        /// its schedule, and an armed one is already doing what Send would ask.
        /// </summary>
        internal static string SendDisabledReason(bool inActiveTable, bool armed, string brokenReason = null,
            bool sendingOnce = false)
        {
            if (!string.IsNullOrEmpty(brokenReason))
                return "Stopped: " + brokenReason + ". Fix or delete the route first";
            if (sendingOnce)
                return "Already sending one run";
            if (armed)
                return "Already armed: this route finishes the run it is making first";
            if (inActiveTable)
                return "Already running on its schedule; pause it first to send a single run";
            return string.Empty;
        }

        // The sortable header hovers. They reach the strip through
        // ParsekUI.DrawSortableHeaderCore's plain-string tooltip argument, which the
        // literal scanner cannot see, so they live here where the budget test reads them.

        /// <summary>The Route header hover.</summary>
        internal const string RouteHeaderTooltip = "The route's name, and where it runs from and to.";

        /// <summary>The Every header hover.</summary>
        internal const string EveryHeaderTooltip = "How often the route runs.";

        /// <summary>The Next header hover.</summary>
        internal const string NextHeaderTooltip = "Time until the route's next run. Hover a row for the exact date.";

        /// <summary>The Status header hover.</summary>
        internal const string StatusHeaderTooltip = "What the route is doing now. Hover a row for the full sentence.";

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static bool IsUsableUT(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut) && ut >= 0.0;
        }

        private static string EndSentence(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text[text.Length - 1] == '.' ? text : text + ".";
        }

        private static string Plural(int n, string one, string many)
        {
            return n.ToString(IC) + " " + (n == 1 ? one : many);
        }
    }
}
