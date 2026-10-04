using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="LogisticsRoutePresentation"/>, the pure half of the Logistics Model 1
    /// redesign: the Basic / Advanced tuning gate, which table a route sits in, the merged
    /// Status cell, the Missions-form Next countdown, the read-only Every and the Runs
    /// cells, and the dated detail sentences. Unity-free, so exercised directly.
    /// </summary>
    public class LogisticsRoutePresentationTests
    {
        private static readonly Func<double, string> Date = ut => "Y1, D06, 14:05";

        // ------------------------------------------------------------------
        // Gate
        // ------------------------------------------------------------------

        // catches: the tuning controls leaking into Basic, or vanishing from Advanced.
        [Fact]
        public void RouteTuning_HiddenInBasic_ShownInAdvanced()
        {
            Assert.False(LogisticsRoutePresentation.ShowsRouteTuning(UiComplexityMode.Basic));
            Assert.True(LogisticsRoutePresentation.ShowsRouteTuning(UiComplexityMode.Advanced));
        }

        // catches: Basic sorting by the Runs column it does not draw (an invisible key),
        // or the fallback rewriting a sort the player chose for a visible column.
        [Fact]
        public void EffectiveSortColumn_RunsFallsBackToNameOnlyInBasic()
        {
            Assert.Equal(LogisticsRouteSortColumn.Name,
                LogisticsRoutePresentation.EffectiveSortColumn(LogisticsRouteSortColumn.Cycles, false));
            Assert.Equal(LogisticsRouteSortColumn.Cycles,
                LogisticsRoutePresentation.EffectiveSortColumn(LogisticsRouteSortColumn.Cycles, true));
            foreach (LogisticsRouteSortColumn col in new[]
                     {
                         LogisticsRouteSortColumn.Name, LogisticsRouteSortColumn.Interval,
                         LogisticsRouteSortColumn.NextDelivery, LogisticsRouteSortColumn.Status,
                     })
                Assert.Equal(col, LogisticsRoutePresentation.EffectiveSortColumn(col, false));
        }

        // ------------------------------------------------------------------
        // Table membership (decision 3b)
        // ------------------------------------------------------------------

        // catches: a Send-armed route leaving the Active table while it sends. It sits in
        // Active through the countdown and the run in flight; only Paused routes sit in
        // the Paused table (the backend's PauseAfterCurrentCycle returns it there).
        [Fact]
        public void OnlyPausedRoutesSitInThePausedTable()
        {
            Assert.True(LogisticsRoutePresentation.BelongsInPausedTable(RouteStatus.Paused));
            foreach (RouteStatus status in new[]
                     {
                         RouteStatus.Active, RouteStatus.InTransit, RouteStatus.WaitingForResources,
                         RouteStatus.WaitingForFunds, RouteStatus.DestinationFull, RouteStatus.EndpointLost,
                         RouteStatus.MissingSourceRecording, RouteStatus.SourceChanged,
                     })
                Assert.False(LogisticsRoutePresentation.BelongsInPausedTable(status));
        }

        // ------------------------------------------------------------------
        // Send-armed Interact line 1: Cancel before launch, Delivering... in flight
        // ------------------------------------------------------------------

        // catches: a Send arm that cannot be taken back before launch, or an in-flight run
        // offering a Cancel the backend cannot honour.
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        public void SendArmed_CountdownPhase_IsALiveCancel(int status)
        {
            Assert.Equal(LogisticsRoutePresentation.ArmedLine1.Cancel,
                LogisticsRoutePresentation.ResolveArmedLine1(true, false, (RouteStatus)status));
            Assert.Equal("Cancel", LogisticsRoutePresentation.CancelButtonLabel);
            Assert.Equal("Cancels the run before launch; nothing is spent.",
                LogisticsRoutePresentation.CancelButtonTooltip);
        }

        [Fact]
        public void SendArmed_InFlight_IsAGreyedDeliveringWithNoCancel()
        {
            Assert.Equal(LogisticsRoutePresentation.ArmedLine1.Delivering,
                LogisticsRoutePresentation.ResolveArmedLine1(true, false, RouteStatus.InTransit));
            Assert.Equal("Delivering...", LogisticsRoutePresentation.DeliveringButtonLabel);
            Assert.Equal(LogisticsRoutePresentation.ArmedLine1.Pausing,
                LogisticsRoutePresentation.ResolveArmedLine1(false, true, RouteStatus.InTransit));
            Assert.Equal(LogisticsRoutePresentation.ArmedLine1.None,
                LogisticsRoutePresentation.ResolveArmedLine1(false, false, RouteStatus.Active));
        }

        [Fact]
        public void DeliveringTooltip_DatesLaunchAndArrival()
        {
            Assert.Equal(
                "Launched on Y1, D06, 14:05; arrives on Y1, D06, 14:05, then pauses again. A launched run cannot be called back.",
                LogisticsRoutePresentation.FormatDeliveringTooltip(10.0, 20.0, Date));
            Assert.Equal(
                "Arrives, then pauses again. A launched run cannot be called back.",
                LogisticsRoutePresentation.FormatDeliveringTooltip(double.NaN, double.NaN, Date));
        }

        // The Cancel click goes through the same TryPause path a Pause click does: on an
        // un-launched Send arm it clears the arm and pauses, dispatching nothing.
        [Fact]
        public void Cancel_RoutesToTryPause_WhichClearsTheArmAndPauses()
        {
            var route = new Route
            {
                Id = "cancel-test", Name = "Cancel test", Status = RouteStatus.Active,
                PauseAfterCurrentCycle = true, SendOnceArmed = true,
            };
            Assert.True(RouteOrchestrator.TryPause(route, -1.0, null));
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.SendOnceArmed);
            Assert.False(route.PauseAfterCurrentCycle);
        }

        // ------------------------------------------------------------------
        // Merged Status cell
        // ------------------------------------------------------------------

        private static LogisticsRoutePresentation.StatusCell Classify(
            RouteStatus status, bool sendingOnce = false, bool pausing = false,
            string hold = null, int runs = 0, bool originLost = false)
        {
            return LogisticsRoutePresentation.ClassifyStatus(status, sendingOnce, pausing, hold, runs, originLost);
        }

        [Fact]
        public void Status_RunningRoute_DeliveringOnceItDelivered_ElseScheduled()
        {
            var delivering = Classify(RouteStatus.Active, runs: 3);
            Assert.Equal(LogisticsRoutePresentation.StatusWord.Delivering, delivering.Word);
            Assert.Equal("Delivering", delivering.Text);
            Assert.Equal(ParsekUI.StatusColorKind.Green, delivering.Color);

            var scheduled = Classify(RouteStatus.InTransit, runs: 0);
            Assert.Equal(LogisticsRoutePresentation.StatusWord.Scheduled, scheduled.Word);
            Assert.Equal("Scheduled", scheduled.Text);
            Assert.Null(scheduled.Color); // the label's default white
        }

        // catches: P2 - a paused route that was refused twice reading cyan "New".
        [Fact]
        public void Status_HeldPausedRoute_ReadsHeldNotNew()
        {
            var cell = Classify(RouteStatus.Paused, hold: "Held: B short 108.8 LiquidFuel", runs: 0);
            Assert.Equal(LogisticsRoutePresentation.StatusWord.Held, cell.Word);
            Assert.Equal("Held: B short 108.8 LiquidFuel", cell.Text);
            Assert.Equal(ParsekUI.StatusColorKind.Yellow, cell.Color);
        }

        [Fact]
        public void Status_PausedRoute_NewUntilItDelivered_ThenPaused()
        {
            var fresh = Classify(RouteStatus.Paused, runs: 0);
            Assert.Equal("New", fresh.Text);
            Assert.Equal(ParsekUI.StatusColorKind.Cyan, fresh.Color);
            var paused = Classify(RouteStatus.Paused, runs: 2);
            Assert.Equal("Paused", paused.Text);
            Assert.Equal(ParsekUI.StatusColorKind.Grey, paused.Color);
        }

        // catches: the armed word losing to a hold or to the raw status; the player's last
        // click (Send / Pause) is what the row must say.
        [Fact]
        public void Status_ArmedStates_WinOverHoldAndStatus()
        {
            var sending = Classify(RouteStatus.InTransit, sendingOnce: true, hold: "Held: x", runs: 4);
            Assert.Equal("Sending one run", sending.Text);
            Assert.Equal(ParsekUI.StatusColorKind.Cyan, sending.Color);
            var pausing = Classify(RouteStatus.InTransit, pausing: true, runs: 4);
            Assert.Equal("Pausing after this run", pausing.Text);
        }

        // catches: a broken route reading as anything but red, or naming the wrong vessel.
        [Fact]
        public void Status_BrokenStates_AreRedAndWinOverEverything()
        {
            var dest = Classify(RouteStatus.EndpointLost, sendingOnce: true, hold: "Held: x");
            Assert.Equal("Broken: destination lost", dest.Text);
            Assert.Equal(ParsekUI.StatusColorKind.Red, dest.Color);
            Assert.Equal("Broken: origin lost", Classify(RouteStatus.EndpointLost, originLost: true).Text);
            Assert.Equal("Broken: flight missing", Classify(RouteStatus.MissingSourceRecording).Text);
            Assert.Equal("Broken: flight changed", Classify(RouteStatus.SourceChanged).Text);
        }

        [Fact]
        public void Status_WaitStatusWithoutHold_StillReadsHeld()
        {
            Assert.Equal("Held: not enough funds", Classify(RouteStatus.WaitingForFunds).Text);
            Assert.Equal("Held: destination full", Classify(RouteStatus.DestinationFull).Text);
            Assert.Equal("Held: origin short of cargo", Classify(RouteStatus.WaitingForResources).Text);
        }

        // catches: player text regressing to the raw enum or to cycle / dispatch / ghost /
        // tree / recording / loop words, in any status and any hover.
        [Fact]
        public void Status_EveryCellAndHover_UsesPlayerWords()
        {
            string[] banned = { "cycle", "dispatch", "ghost", "tree", "recording", "loop" };
            foreach (RouteStatus status in (RouteStatus[])Enum.GetValues(typeof(RouteStatus)))
            {
                foreach (bool sending in new[] { false, true })
                foreach (int runs in new[] { 0, 2 })
                foreach (bool lost in new[] { false, true })
                {
                    var cell = Classify(status, sendingOnce: sending, runs: runs, originLost: lost);
                    string tip = LogisticsRoutePresentation.StatusTooltip(
                        cell, status, null, runs, 10.0, 20.0, lost, Date);
                    Assert.False(string.IsNullOrEmpty(cell.Text));
                    Assert.False(string.IsNullOrEmpty(tip));
                    if (status != RouteStatus.Paused) // "Paused" is both the enum and the word
                        Assert.NotEqual(status.ToString(), cell.Text);
                    foreach (string w in banned)
                    {
                        Assert.DoesNotContain(w, cell.Text.ToLowerInvariant());
                        Assert.DoesNotContain(w, tip.ToLowerInvariant());
                    }
                }
            }
        }

        // catches: the held hover dropping the dated hold sentence for a generic line.
        [Fact]
        public void StatusTooltip_HeldUsesTheDatedHoldSentence()
        {
            var cell = Classify(RouteStatus.Active, hold: "Held: origin out of LiquidFuel");
            string held = LogisticsHoldPresentation.FormatHoldDetailLine(
                "origin is out of LiquidFuel - delivers when the origin has the full amount", 50.0, Date);
            Assert.Equal(
                "Last run held on Y1, D06, 14:05: origin is out of LiquidFuel - delivers when the origin has the full amount.",
                LogisticsRoutePresentation.StatusTooltip(cell, RouteStatus.Active, held, 0, -1.0, double.NaN, false, Date));
        }

        // catches: the Send-armed hover losing the date of the run it is about to make.
        [Fact]
        public void StatusTooltip_SendingOnce_DatesTheRun()
        {
            var cell = Classify(RouteStatus.Active, sendingOnce: true);
            Assert.Equal("Sending one run at the next window on Y1, D06, 14:05, then pausing again.",
                LogisticsRoutePresentation.StatusTooltip(cell, RouteStatus.Active, null, 0, 1.0, 500.0, false, Date));
            Assert.Equal("Sending one run at the next window, then pausing again.",
                LogisticsRoutePresentation.StatusTooltip(cell, RouteStatus.Active, null, 0, 1.0, double.NaN, false, Date));
        }

        [Fact]
        public void StatusTooltip_NewDatesCreation_DeliveringCountsRuns()
        {
            Assert.Equal("Created on Y1, D06, 14:05, not run yet. Send makes one run as a test.",
                LogisticsRoutePresentation.StatusTooltip(Classify(RouteStatus.Paused), RouteStatus.Paused,
                    null, 0, 100.0, double.NaN, false, Date));
            Assert.Equal("Running on its schedule. 1 run delivered so far.",
                LogisticsRoutePresentation.StatusTooltip(Classify(RouteStatus.Active, runs: 1), RouteStatus.Active,
                    null, 1, 100.0, double.NaN, false, Date));
        }

        // The merged sort key is the cell text, and the retired Delivery member aliases it.
        [Fact]
        public void StatusSortKey_IsTheCellText()
        {
            var cell = Classify(RouteStatus.Paused, hold: "Held: z");
            Assert.Equal("Held: z", LogisticsRoutePresentation.StatusSortKey(cell));
        }

        // ------------------------------------------------------------------
        // Next column (decision 3a + R6: a Send-armed row keeps its countdown)
        // ------------------------------------------------------------------

        [Fact]
        public void NextCell_UsesTheMissionsCountdownExactly()
        {
            double secs = 3.0 * 86400.0 + 7200.0; // any value; compare against the shared formatter
            Assert.Equal(ParsekTimeFormat.FormatCountdown(secs),
                LogisticsRoutePresentation.FormatNextCell(
                    LogisticsCountdownPresentation.CountdownBranch.NextWindow, secs, true, false));
        }

        [Fact]
        public void NextCell_WarnedCarriesTheMissionsMarker()
        {
            string cell = LogisticsRoutePresentation.FormatNextCell(
                LogisticsCountdownPresentation.CountdownBranch.RechecksIn, 90.0, true, true);
            Assert.EndsWith(MissionPresentation.SummaryCountdownWarningMarker, cell);
            Assert.StartsWith("T- ", cell);
        }

        // R6: Next reads "-" only when no run is scheduled (Paused and not Send-armed).
        [Fact]
        public void NextCell_UnscheduledDash_ScheduledCountdown()
        {
            Assert.Equal("-", LogisticsRoutePresentation.FormatNextCell(
                LogisticsCountdownPresentation.CountdownBranch.NextDelivery, 60.0, runScheduled: false, warned: false));
            Assert.Equal("T- 1m 0s", LogisticsRoutePresentation.FormatNextCell(
                LogisticsCountdownPresentation.CountdownBranch.NextDelivery, 60.0, runScheduled: true, warned: false));
        }

        // catches: the hover losing the exact date, or a culture-dependent fallback.
        [Fact]
        public void NextTooltip_ExactDate_InvariantFallback()
        {
            Assert.Equal("Next launch window on Y1, D06, 14:05.",
                LogisticsRoutePresentation.FormatNextTooltip(
                    LogisticsCountdownPresentation.CountdownBranch.NextWindow, 5000.0, true, false, Date));
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("Next delivery on UT 12346.",
                    LogisticsRoutePresentation.FormatNextTooltip(
                        LogisticsCountdownPresentation.CountdownBranch.NextDelivery, 12345.6, true, false, null));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
            Assert.StartsWith("No run scheduled.",
                LogisticsRoutePresentation.FormatNextTooltip(
                    LogisticsCountdownPresentation.CountdownBranch.NextDelivery, 5000.0, false, false, Date));
        }

        // ------------------------------------------------------------------
        // Every / Runs (decision 2b: Basic reads the interval, Advanced edits it)
        // ------------------------------------------------------------------

        [Fact]
        public void EveryReadOnly_FlatAndWindowed()
        {
            Assert.Equal("every 4.0d", LogisticsRoutePresentation.FormatEveryReadOnly(false, 1, "4.0d"));
            Assert.Equal("every window", LogisticsRoutePresentation.FormatEveryReadOnly(true, 1, "394.8d"));
            Assert.Equal("every 2nd window", LogisticsRoutePresentation.FormatEveryReadOnly(true, 2, "789.6d"));
            Assert.Equal("-", LogisticsRoutePresentation.FormatEveryReadOnly(false, 1, "-"));
        }

        [Theory]
        [InlineData(3, 1, "3, 1 held")]
        [InlineData(3, 0, "3")]
        [InlineData(0, 2, "0, 2 held")]
        [InlineData(1000, 250, "1000, 250 held")]
        public void RunsCell_CountsDeliveredAndHeldRuns(int completed, int held, string expected)
        {
            Assert.Equal(expected, LogisticsRoutePresentation.FormatRunsCell(completed, held));
        }

        // ------------------------------------------------------------------
        // Route cell second line and detail sentences
        // ------------------------------------------------------------------

        [Fact]
        public void FromTo_ShortOriginAndDestination()
        {
            Assert.Equal("KSC \u2192 Depot Station Duna I",
                LogisticsRoutePresentation.FormatFromTo(
                    LogisticsRoutePresentation.FormatOriginShort(true, false, null), "Depot Station Duna I"));
            Assert.Equal("Harvested", LogisticsRoutePresentation.FormatOriginShort(false, true, null));
            Assert.Equal("Depot A", LogisticsRoutePresentation.FormatOriginShort(false, false, "Depot A"));
            Assert.Equal("- \u2192 -", LogisticsRoutePresentation.FormatFromTo(null, ""));
        }

        [Fact]
        public void DeliversEachRun_NamesTheDestination()
        {
            Assert.Equal("Delivers each run: LiquidFuel 257.8, Oxidizer 315.1 to Depot Station Duna I.",
                LogisticsRoutePresentation.FormatDeliversEachRun("LiquidFuel 257.8, Oxidizer 315.1", "Depot Station Duna I"));
            Assert.Equal("Delivers each run: (nothing).",
                LogisticsRoutePresentation.FormatDeliversEachRun(null, "-"));
        }

        // Decision 5b: Basic names the mission only; the repeat clause is Advanced.
        [Fact]
        public void BuiltFromMission_RepeatClauseOnlyInAdvanced()
        {
            Assert.Equal("Built from mission 'Duna Supply 1'.",
                LogisticsRoutePresentation.FormatBuiltFromMission("Duna Supply 1", showsTuning: false));
            string adv = LogisticsRoutePresentation.FormatBuiltFromMission("Duna Supply 1", showsTuning: true);
            Assert.StartsWith("Built from mission 'Duna Supply 1'. ", adv);
            Assert.Contains("looping", adv);
            Assert.DoesNotContain("tree", adv);
        }

        // R1: the last delivery comes only from the ledger; nothing delivered draws no line.
        [Fact]
        public void LastDeliveredLine_DatedFromTheLedger()
        {
            Assert.Equal(
                "Last delivered on Y1, D06, 14:05: delivered 200.0 LiquidFuel. Delivered so far: 400.0 LiquidFuel in 2 runs.",
                LogisticsRoutePresentation.FormatLastDeliveredLine(
                    true, 900.0, "delivered 200.0 LiquidFuel", "400.0 LiquidFuel", 2, Date));
            Assert.Null(LogisticsRoutePresentation.FormatLastDeliveredLine(
                false, double.NaN, null, "(none)", 0, Date));
        }

        // "rec N of tree" replaced by Mission Log numbering of repeated names.
        [Fact]
        public void FlightsUsed_NumbersRepeatedNamesAndDropsTreeWords()
        {
            string line = LogisticsRoutePresentation.FormatFlightsUsed(
                new List<string> { "Duna Supply 1", "Probe", "Depot", "Depot", null },
                new List<string> { "a", "b", "c", "d", "e1234567" });
            Assert.Equal("Flights used: Duna Supply 1, Probe, Depot [1], Depot [2], e1234567", line);
            Assert.DoesNotContain("tree", line);
            Assert.DoesNotContain("rec ", line);
            Assert.Equal("Flights used: -", LogisticsRoutePresentation.FormatFlightsUsed(null, null));
        }

        // ------------------------------------------------------------------
        // Interact column
        // ------------------------------------------------------------------

        // catches: Send live on a running row (it would arm a single run on top of the
        // schedule) or greyed on a plain Paused row.
        [Fact]
        public void SendDisabledReason_LiveOnlyOnAnUnarmedPausedRow()
        {
            Assert.Equal(string.Empty, LogisticsRoutePresentation.SendDisabledReason(false, false));
            Assert.StartsWith("Already running on its schedule;",
                LogisticsRoutePresentation.SendDisabledReason(true, false));
            Assert.StartsWith("Already armed",
                LogisticsRoutePresentation.SendDisabledReason(false, true));
            Assert.Equal("Already sending one run",
                LogisticsRoutePresentation.SendDisabledReason(true, true, null, sendingOnce: true));
        }

        // catches: a broken route in the Active table explaining its greyed Send with
        // "Already running on its schedule" - it is not running at all.
        [Fact]
        public void SendDisabledReason_BrokenRouteSaysStoppedAndWins()
        {
            var cell = LogisticsRoutePresentation.ClassifyStatus(
                RouteStatus.EndpointLost, false, false, null, 3, false);
            string broken = LogisticsRoutePresentation.BrokenShortReason(cell);
            Assert.Equal("destination lost", broken);
            Assert.Equal("Stopped: destination lost. Fix or delete the route first",
                LogisticsRoutePresentation.SendDisabledReason(true, false, broken));
            Assert.Equal("flight missing", LogisticsRoutePresentation.BrokenShortReason(
                LogisticsRoutePresentation.ClassifyStatus(RouteStatus.MissingSourceRecording, false, false, null, 0, false)));
            Assert.Equal("flight changed", LogisticsRoutePresentation.BrokenShortReason(
                LogisticsRoutePresentation.ClassifyStatus(RouteStatus.SourceChanged, false, false, null, 0, false)));
            Assert.Null(LogisticsRoutePresentation.BrokenShortReason(
                LogisticsRoutePresentation.ClassifyStatus(RouteStatus.Active, false, false, null, 3, false)));
        }

        // The from/to line uses the same arrow glyph as the default route names.
        [Fact]
        public void FromTo_UsesTheRouteNameArrow()
        {
            Assert.Contains("\u2192", LogisticsRoutePresentation.FromToArrow);
            Assert.DoesNotContain("->", LogisticsRoutePresentation.FormatFromTo("KSC", "Depot"));
        }

        // The detail block's Interact slots: Rename and Delete in both modes, Link
        // round-trip in Advanced, so a Basic block needs two lines and an Advanced one three.
        [Fact]
        public void DetailSlots_RenameDeleteThenLinkInAdvanced()
        {
            Assert.Equal(0, LogisticsWindowUI.RenameSlot);
            Assert.Equal(1, LogisticsWindowUI.DeleteSlot);
            Assert.Equal(2, LogisticsWindowUI.LinkSlot);
            Assert.Equal(2, LogisticsWindowUI.RouteDetailSlotCount(false));
            Assert.Equal(3, LogisticsWindowUI.RouteDetailSlotCount(true));
        }

        // ------------------------------------------------------------------
        // Section title bars, the Interact grid, Go to, the filler line
        // ------------------------------------------------------------------

        [Fact]
        public void SectionTitle_CaretNameAndCount()
        {
            Assert.Equal("\u25bc Active Routes (2)",
                LogisticsRoutePresentation.FormatSectionTitle(LogisticsRoutePresentation.ActiveSectionName, 2, true));
            Assert.Equal("\u25b6 Paused Routes (0)",
                LogisticsRoutePresentation.FormatSectionTitle(LogisticsRoutePresentation.PausedSectionName, 0, false));
            Assert.Equal("\u25bc Candidates (3)",
                LogisticsRoutePresentation.FormatSectionTitle(LogisticsRoutePresentation.CandidatesSectionName, 3, true));
        }

        // The grid cell fits the widest measured label, never below the Missions pair half,
        // and the column is exactly two cells, the gap and the two insets.
        [Fact]
        public void InteractGrid_CellFitsTheWidestLabel_ColumnFitsTwoCells()
        {
            Assert.Equal(MissionsWindowUI.InteractPairButtonWidth, LogisticsRoutePresentation.InteractPairWidth(10f));
            Assert.Equal(83f, LogisticsRoutePresentation.InteractPairWidth(80.4f));
            float pair = 83f;
            Assert.Equal(2f * pair + MissionsWindowUI.InteractButtonGap + 2f * MissionsWindowUI.InteractCellInset,
                LogisticsRoutePresentation.InteractColumnWidth(pair));
            foreach (string label in new[] { "Activate", "Pause", "Cancel", "Delivering...", "Pausing...", "Send", "Go to", "Log" })
                Assert.Contains(label, LogisticsRoutePresentation.InteractGridLabels);
        }

        [Fact]
        public void GoTo_GreysWithAReasonWhenTheMissionIsGone()
        {
            Assert.Equal(string.Empty, LogisticsRoutePresentation.GoToDisabledReason(true));
            Assert.Equal("The mission this route was built from no longer exists",
                LogisticsRoutePresentation.GoToDisabledReason(false));
            Assert.Equal("Go to", LogisticsRoutePresentation.GoToButtonLabel);
        }

        // A block short of lines for its buttons gets an information line, never an empty one.
        [Fact]
        public void FillerInfoLine_DeliveredTotalElseNotRunYet()
        {
            Assert.Equal("Delivered so far: 400.0 LiquidFuel.",
                LogisticsRoutePresentation.FormatFillerInfoLine(true, "400.0 LiquidFuel"));
            Assert.Equal("Not run yet.", LogisticsRoutePresentation.FormatFillerInfoLine(false, null));
            Assert.Equal("Not run yet.", LogisticsRoutePresentation.FormatFillerInfoLine(true, ""));
        }

        // The armed labels fit the 100 px single (the long words live in the Status cell).
        [Fact]
        public void ArmedButtonLabels_AreShort()
        {
            Assert.Equal("Delivering...", LogisticsRoutePresentation.DeliveringButtonLabel);
            Assert.Equal("Pausing...", LogisticsRoutePresentation.PausingButtonLabel);
            Assert.True(LogisticsRoutePresentation.DeliveringButtonLabel.Length <= 13);
        }
    }
}
