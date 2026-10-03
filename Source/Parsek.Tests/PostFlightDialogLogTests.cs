using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class PostFlightDialogLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PostFlightDialogLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            PostFlightDialogLog.ResetForTesting();
        }

        public void Dispose()
        {
            PostFlightDialogLog.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private List<string> TagLines() =>
            logLines.Where(l => l.Contains("[" + PostFlightDialogLog.Tag + "]")).ToList();

        [Fact]
        public void FormatWallSeconds_IsInvariantAndClampsNegative()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("1130.50", PostFlightDialogLog.FormatWallSeconds(1130.5));
                Assert.Equal("0.00", PostFlightDialogLog.FormatWallSeconds(-3.0));
                Assert.Equal("0.00", PostFlightDialogLog.FormatWallSeconds(double.NaN));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Quote_HandlesNullNewlinesAndQuotes()
        {
            Assert.Equal("\"?\"", PostFlightDialogLog.Quote(null));
            Assert.Equal("\"?\"", PostFlightDialogLog.Quote("  "));
            Assert.Equal("\"a b 'c'\"", PostFlightDialogLog.Quote("a\nb \"c\""));
        }

        [Theory]
        [InlineData("Flea Recovery Dialog Handler", "Flea")]
        [InlineData("Kerbal X Mk2 Recovery Dialog Handler", "Kerbal X Mk2")]
        [InlineData("MissionRecoveryDialog(Clone)", null)]
        [InlineData(" Recovery Dialog Handler", null)]
        [InlineData(null, null)]
        public void ParseVesselFromRecoveryHandlerName_ReadsStockNameShape(string name, string expected)
        {
            Assert.Equal(expected, PostFlightDialogLog.ParseVesselFromRecoveryHandlerName(name));
        }

        [Fact]
        public void FlightResults_ShownThenClosed_LogsBothWithDuration()
        {
            PostFlightDialogLog.NoteFlightResultsShown(
                100.0, "FLIGHT", "Flea", paused: true, exitControls: true,
                outcome: "Outcome: Catastrophic Failure!");
            PostFlightDialogLog.NoteFlightResultsDismissed(1230.25, "Close", paused: false);

            var lines = TagLines();
            Assert.Equal(2, lines.Count);
            Assert.Contains("[INFO][PostFlightDialog] FlightResultsDialog shown: scene=FLIGHT vessel=\"Flea\" "
                + "paused=true exitControls=true outcome=\"Outcome: Catastrophic Failure!\"", lines[0]);
            Assert.Contains("[INFO][PostFlightDialog] FlightResultsDialog dismissed: scene=FLIGHT vessel=\"Flea\" "
                + "via=Close onScreenWallSeconds=1130.25 paused=false", lines[1]);
            Assert.False(PostFlightDialogLog.IsFlightResultsShown);
        }

        [Fact]
        public void FlightResults_DestroyAfterClose_IsSilent()
        {
            PostFlightDialogLog.NoteFlightResultsShown(10.0, "FLIGHT", null, true, false, "Flight Status: Flying");
            PostFlightDialogLog.NoteFlightResultsDismissed(12.0, "Close", false);
            PostFlightDialogLog.NoteFlightResultsDismissed(12.0, "Destroyed", false);

            var lines = TagLines();
            Assert.Equal(2, lines.Count);
            Assert.Contains("vessel=\"?\"", lines[0]);
            Assert.Contains("exitControls=false", lines[0]);
            Assert.DoesNotContain(lines, l => l.Contains("via=Destroyed"));
        }

        [Fact]
        public void FlightResults_DismissWithNothingShown_IsSilent()
        {
            PostFlightDialogLog.NoteFlightResultsDismissed(5.0, "Close", false);
            Assert.Empty(TagLines());
        }

        [Fact]
        public void FlightResults_ReDisplayKeepsOriginalStart()
        {
            PostFlightDialogLog.NoteFlightResultsShown(10.0, "FLIGHT", "Flea", true, true, "first");
            PostFlightDialogLog.NoteFlightResultsShown(15.0, "FLIGHT", "Flea", true, true, "second");
            PostFlightDialogLog.NoteFlightResultsDismissed(40.0, "Destroyed", true);

            var lines = TagLines();
            Assert.Equal(3, lines.Count);
            Assert.Contains("[VERBOSE][PostFlightDialog] FlightResultsDialog re-displayed while shown: "
                + "outcome=\"second\" sinceShownWallSeconds=5.00", lines[1]);
            Assert.Contains("via=Destroyed onScreenWallSeconds=30.00 paused=true", lines[2]);
        }

        [Fact]
        public void Recovery_ShownWaitsForVesselThenDismissedWithDuration()
        {
            PostFlightDialogLog.NoteRecoverySpawned(7, 50.0, "SPACECENTER");
            Assert.Empty(TagLines());
            Assert.Equal(1, PostFlightDialogLog.PendingRecoveryShownCount);

            PostFlightDialogLog.NoteRecoveryVessel(7, "Flea");
            PostFlightDialogLog.NoteRecoveryVessel(7, "Other");
            Assert.Equal(0, PostFlightDialogLog.PendingRecoveryShownCount);

            PostFlightDialogLog.NoteRecoveryDespawned(7, 112.0, "SPACECENTER", "Fallback");

            var lines = TagLines();
            Assert.Equal(2, lines.Count);
            Assert.Contains("[INFO][PostFlightDialog] MissionRecoveryDialog shown: scene=SPACECENTER vessel=\"Flea\"",
                lines[0]);
            Assert.Contains("[INFO][PostFlightDialog] MissionRecoveryDialog dismissed: scene=SPACECENTER "
                + "vessel=\"Flea\" onScreenWallSeconds=62.00", lines[1]);
        }

        [Fact]
        public void Recovery_DespawnBeforeVessel_LogsShownFromFallbackThenDismissed()
        {
            PostFlightDialogLog.NoteRecoverySpawned(3, 1.0, "TRACKSTATION");
            PostFlightDialogLog.NoteRecoveryDespawned(3, 2.5, "SPACECENTER", "Probe");

            var lines = TagLines();
            Assert.Equal(2, lines.Count);
            Assert.Contains("MissionRecoveryDialog shown: scene=TRACKSTATION vessel=\"Probe\"", lines[0]);
            Assert.Contains("MissionRecoveryDialog dismissed: scene=TRACKSTATION vessel=\"Probe\" "
                + "onScreenWallSeconds=1.50", lines[1]);
            Assert.Equal(0, PostFlightDialogLog.PendingRecoveryShownCount);
        }

        [Fact]
        public void Recovery_DespawnOfUnseenDialog_LogsUnknownDuration()
        {
            PostFlightDialogLog.NoteRecoveryDespawned(99, 5.0, "SPACECENTER", null);

            var lines = TagLines();
            Assert.Single(lines);
            Assert.Contains("MissionRecoveryDialog dismissed: scene=SPACECENTER vessel=\"?\" "
                + "onScreenWallSeconds=unknown", lines[0]);
        }

        [Fact]
        public void Recovery_PendingIdsTrackUnloggedEntriesOnly()
        {
            PostFlightDialogLog.NoteRecoverySpawned(1, 0.0, "SPACECENTER");
            PostFlightDialogLog.NoteRecoverySpawned(2, 0.0, "SPACECENTER");
            PostFlightDialogLog.NoteRecoveryVessel(1, "A");

            var ids = new List<int>();
            PostFlightDialogLog.CollectPendingRecoveryIds(ids);
            Assert.Equal(new[] { 2 }, ids);
            Assert.Equal(1, PostFlightDialogLog.PendingRecoveryShownCount);

            // A re-spawn under a pending id replaces the entry without double-counting.
            PostFlightDialogLog.NoteRecoverySpawned(2, 1.0, "SPACECENTER");
            Assert.Equal(1, PostFlightDialogLog.PendingRecoveryShownCount);
        }
    }
}
