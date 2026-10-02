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
        // Which table a route sits in
        // ------------------------------------------------------------------

        /// <summary>
        /// True when a route belongs in the Paused table: a Paused route, and a route armed
        /// by Send to make ONE run (it is a paused route doing a single run, so it stays in
        /// the Paused table before, during and after that run and never jumps sections).
        /// Everything else (running, held, broken, pause-armed) sits in Active.
        /// </summary>
        internal static bool BelongsInPausedTable(RouteStatus status, bool sendingOnce)
        {
            return status == RouteStatus.Paused || sendingOnce;
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
                        originLost ? "Broken: origin lost" : "Broken: destination lost",
                        ParsekUI.StatusColorKind.Red);
                case RouteStatus.MissingSourceRecording:
                    return Cell(StatusWord.Broken, "Broken: flight missing", ParsekUI.StatusColorKind.Red);
                case RouteStatus.SourceChanged:
                    return Cell(StatusWord.Broken, "Broken: flight changed", ParsekUI.StatusColorKind.Red);
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

        // ------------------------------------------------------------------
        // Next column (the Missions countdown, reused exactly)
        // ------------------------------------------------------------------

        /// <summary>
        /// The Next cell: "T- " + <c>MissionsWindowUI.FormatCountdownCompact</c> (two units,
        /// the Missions summary's countdown) plus
        /// <see cref="MissionPresentation.SummaryCountdownWarningMarker"/> when warned, or
        /// "-" when no run is scheduled. <paramref name="runScheduled"/> is false for a
        /// Paused route that is not armed by Send (the R6 rule: a Send-armed route keeps its
        /// countdown in the Paused table). The window draws it in the Missions amber.
        /// </summary>
        internal static string FormatNextCell(
            LogisticsCountdownPresentation.CountdownBranch branch, double seconds,
            bool runScheduled, bool warned)
        {
            if (!runScheduled || branch == LogisticsCountdownPresentation.CountdownBranch.None)
                return "-";
            return "T- " + MissionsWindowUI.FormatCountdownCompact(seconds)
                + (warned ? MissionPresentation.SummaryCountdownWarningMarker : string.Empty);
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

        /// <summary>The Route cell's grey second line: "KSC -> Depot Station Duna I".</summary>
        internal static string FormatFromTo(string originShort, string destination)
        {
            return (string.IsNullOrEmpty(originShort) ? "-" : originShort)
                + " -> " + (string.IsNullOrEmpty(destination) ? "-" : destination);
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
            if (!string.IsNullOrEmpty(cumulativeText) && cumulativeText != "(none)")
            {
                sb.Append(" Delivered so far: ").Append(cumulativeText);
                if (completedRuns > 0)
                    sb.Append(" in ").Append(Plural(completedRuns, "run", "runs"));
                sb.Append('.');
            }
            return sb.ToString();
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
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string n = names[i];
                if (string.IsNullOrEmpty(n))
                    n = fallbacks != null && i < fallbacks.Count && !string.IsNullOrEmpty(fallbacks[i])
                        ? fallbacks[i] : "<unknown>";
                resolved[i] = n;
                totals.TryGetValue(n, out int t);
                totals[n] = t + 1;
            }
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var sb = new StringBuilder("Flights used: ");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                string n = resolved[i];
                sb.Append(n);
                if (totals[n] > 1)
                {
                    seen.TryGetValue(n, out int k);
                    k++;
                    seen[n] = k;
                    sb.Append(" [").Append(k.ToString(IC)).Append(']');
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Interact column
        // ------------------------------------------------------------------

        /// <summary>The greyed line-1 label of a route armed by Send.</summary>
        internal const string SendingButtonLabel = "Sending...";

        /// <summary>The greyed line-1 label of a route armed by Pause.</summary>
        internal const string PausingButtonLabel = "Pausing...";

        /// <summary>
        /// Why the Send button is greyed, or empty when it is live. Send makes one run of a
        /// paused route; a running route is already on its schedule, and an armed one is
        /// already doing what Send would ask.
        /// </summary>
        internal static string SendDisabledReason(bool inActiveTable, bool armed)
        {
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
