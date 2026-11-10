using System;
using System.Collections.Generic;
using Xunit;
using C = Parsek.Tests.RecordingsTableClickHandlerCharacterizationTests;
using G = Parsek.Tests.RecordingsTableGroupTreeCharacterizationTests;
using R = Parsek.Tests.RecordingsTableRecordingRowCharacterizationTests;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the decision helpers extracted from the Recordings tab's leaf row,
    /// <c>RecordingsTableUI.DrawRecordingRow</c>. Every sweep of
    /// <see cref="RecordingsTableRecordingRowCharacterizationTests"/> runs through the shipped
    /// helper and must print the golden the ORIGINAL block (its verbatim oracle) pinned, and
    /// the oracle's own print when both run on fresh stores.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableRecordingRowHelperTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingsTableRecordingRowHelperTests()
        {
            C.ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            C.ResetAll();
        }

        private sealed class ShippedUnits : R.IUnits
        {
            public bool SkipRow(Recording rec, IReadOnlyList<RecordingSupersedeRelation> s)
                => RecordingsTableUI.ShouldSkipRecordingRow(rec, s);
            public void CrossLink(RecordingsTableUI ui, Recording rec) => ui.TrackCrossLinkRow(rec);
            public void ApplyEnabled(Recording rec, bool e) => RecordingsTableUI.ApplyRecordingPlaybackEnabledToggle(rec, e);
            public bool VisualStatus(RecordingVisualKind k, ref string t, bool term)
                => RecordingsTableUI.ApplyRecordingVisualStatusText(k, ref t, term);
            public string StatusTooltip(string p, string s, string m, string v, string c, string d)
                => RecordingsTableUI.ComposeRecordingStatusTooltip(p, s, m, v, c, d);
            public void LoopToggle(RecordingsTableUI ui, Recording rec, int ri, bool loop, bool bound)
                => ui.ApplyRecordingLoopToggle(rec, ri, loop, bound);
            public int PeriodCell(bool s, bool l) => (int)RecordingsTableUI.ResolveRowPeriodCell(s, l);
            public string WatchLabel(bool w) => RecordingsTableUI.RecordingWatchLabel(w);
            public void AlignRowCapture(RecordingsTableUI ui) => ui.FinishAlignmentDebugRowCapture();
        }

        private string Run(string name, R.IUnits u)
        {
            C.ResetAll();
            logLines.Clear();
            return R.RunSweep(name, u, logLines);
        }

        [Theory]
        [MemberData(nameof(R.Sweeps), MemberType = typeof(R))]
        public void ShippedHelper_MatchesTheOriginalBlock(string name)
        {
            string oracle = Run(name, new R.OracleUnits());
            string shipped = Run(name, new ShippedUnits());

            Assert.True(R.Goldens[name] == shipped,
                name + ": golden differs; the shipped helper prints\n<<<" + shipped + ">>>");
            Assert.True(oracle == shipped,
                name + ": oracle and shipped differ\n--- oracle\n" + oracle + "\n--- shipped\n" + shipped);
        }

        // The enum's order is the oracle's branch numbering (0 blank, 1 blank + hover, 2 value).
        [Fact]
        public void PeriodCell_EnumOrderIsTheBranchOrder()
        {
            Assert.Equal(0, (int)RecordingsTableUI.RowPeriodCell.Blank);
            Assert.Equal(1, (int)RecordingsTableUI.RowPeriodCell.BlankWithHover);
            Assert.Equal(2, (int)RecordingsTableUI.RowPeriodCell.Value);
        }

        // ---- Readable direct checks, one behaviour each ----

        [Fact]
        public void SkipRow_ArchivedRowShowsOnceTheArchiveFilterIsOff()
        {
            var a = C.MakeRec("a", "A");
            a.Hidden = true;
            C.Commit(a);

            GroupHierarchyStore.HideActive = true;
            Assert.True(RecordingsTableUI.ShouldSkipRecordingRow(a, null));
            GroupHierarchyStore.HideActive = false;
            Assert.False(RecordingsTableUI.ShouldSkipRecordingRow(a, null));
            GroupHierarchyStore.HideActive = true;
        }

        [Fact]
        public void SkipRow_SupersededRowIsSkipped()
        {
            var a = C.MakeRec("a", "A");
            C.Commit(a, C.MakeRec("n", "N"));
            var rel = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation { RelationId = "r", OldRecordingId = "a", NewRecordingId = "n" },
            };

            Assert.True(RecordingsTableUI.ShouldSkipRecordingRow(a, rel));
            Assert.False(RecordingsTableUI.ShouldSkipRecordingRow(a, new List<RecordingSupersedeRelation>()));
        }

        [Fact]
        public void CrossLink_TargetRowRecordsItsIndexAndClearsTheRequest()
        {
            var ui = C.MakeTable();
            G.Set(ui, "pendingScrollToRecordingId", "b");

            ui.TrackCrossLinkRow(C.MakeRec("a", "Alpha"));
            ui.TrackCrossLinkRow(C.MakeRec("b", "Beta"));

            Assert.Equal(1, G.Get<int>(ui, "pendingScrollRowIndex"));
            Assert.Null(ui.PendingScrollToRecordingIdForTesting);
            Assert.Equal(2, G.Get<int>(ui, "renderedRowCounter"));
            Assert.Contains(logLines, l => l.Contains("[UI]") && l.Contains("Cross-link: found \"Beta\" at rendered row 1"));
        }

        [Fact]
        public void ApplyEnabled_NamesTheSegmentPhase()
        {
            var a = C.MakeRec("a", "Alpha");
            a.SegmentPhase = "atmo";
            C.Commit(a);

            RecordingsTableUI.ApplyRecordingPlaybackEnabledToggle(a, false);

            Assert.False(a.PlaybackEnabled);
            Assert.Contains(logLines, l => l.Contains("[UI]")
                && l.Contains("Recording 'Alpha' playback disabled (segment: ")
                && l.Contains("atmo"));
        }

        [Fact]
        public void VisualStatus_EndingWordKeepsItsStyleAndTakesTheSuffix()
        {
            string text = "Landed";
            Assert.False(RecordingsTableUI.ApplyRecordingVisualStatusText(
                RecordingVisualKind.StaticPlaceholder, ref text, true));
            Assert.Equal("Landed still", text);

            text = "In flight";
            Assert.True(RecordingsTableUI.ApplyRecordingVisualStatusText(
                RecordingVisualKind.StaticPlaceholder, ref text, false));
            Assert.Equal("static", text);

            text = "In flight";
            Assert.False(RecordingsTableUI.ApplyRecordingVisualStatusText(
                RecordingVisualKind.Normal, ref text, false));
            Assert.Equal("In flight", text);
        }

        [Fact]
        public void StatusTooltip_JoinsClausesOnOneLineAndFallsBackWhenEmpty()
        {
            Assert.Equal("Ends: Kerbin - Used: 10 LF - Docked Station",
                RecordingsTableUI.ComposeRecordingStatusTooltip("Ends: Kerbin", null, "Used: 10 LF", null, "", "Docked Station"));
            string fallback = RecordingsTableUI.ComposeRecordingStatusTooltip(null, null, null, null, "", null);
            Assert.StartsWith("How this flight is doing", fallback);
            Assert.DoesNotContain("\n", fallback);
            Assert.True(fallback.Length <= TooltipEchoBox.BudgetChars(1355f, TooltipEchoBox.SingleLine));
        }

        [Fact]
        public void LoopToggle_RouteBoundIsRefusedAndLogged()
        {
            var a = C.MakeRec("a", "Alpha");
            a.TreeId = "tree-1";
            C.Commit(a);
            var ui = C.MakeTable();

            ui.ApplyRecordingLoopToggle(a, 0, true, true);

            Assert.False(a.LoopPlayback);
            Assert.Contains(logLines, l => l.Contains("[RouteGuard]")
                && l.Contains("tree=tree-1 bound by route") && l.Contains("request=True ignored"));
        }

        [Fact]
        public void LoopToggle_OffReleasesThePeriodFieldFocusedOnThisRowOnly()
        {
            var a = C.MakeRec("a", "Alpha");
            a.LoopPlayback = true;
            C.Commit(a);
            var ui = C.MakeTable();
            G.Set(ui, "loopPeriodFocusedRi", 0);

            ui.ApplyRecordingLoopToggle(a, 1, false, false);
            Assert.Equal(0, G.Get<int>(ui, "loopPeriodFocusedRi"));

            a.LoopPlayback = true;
            ui.ApplyRecordingLoopToggle(a, 0, false, false);
            Assert.Equal(-1, G.Get<int>(ui, "loopPeriodFocusedRi"));
            Assert.False(a.LoopPlayback);
        }

        [Fact]
        public void PeriodCell_SuppressionWinsOverLoop()
        {
            Assert.Equal(RecordingsTableUI.RowPeriodCell.Blank, RecordingsTableUI.ResolveRowPeriodCell(true, true));
            Assert.Equal(RecordingsTableUI.RowPeriodCell.BlankWithHover, RecordingsTableUI.ResolveRowPeriodCell(false, false));
            Assert.Equal(RecordingsTableUI.RowPeriodCell.Value, RecordingsTableUI.ResolveRowPeriodCell(false, true));
        }

        [Fact]
        public void WatchLabel_StarsTheWatchedRow()
        {
            Assert.Equal("W*", RecordingsTableUI.RecordingWatchLabel(true));
            Assert.Equal("W", RecordingsTableUI.RecordingWatchLabel(false));
        }

        [Fact]
        public void AlignRowCapture_DisarmsOnlyOnceTheHeaderIsCaptured()
        {
            var ui = C.MakeTable();
            ui.ArmAlignmentDebug();

            ui.FinishAlignmentDebugRowCapture();
            Assert.True(G.Get<bool>(ui, "alignmentDebugRowCaptured"));
            Assert.True(G.Get<bool>(ui, "alignmentDebugArmed"));

            G.Set(ui, "alignmentDebugHeaderCaptured", true);
            ui.FinishAlignmentDebugRowCapture();
            Assert.False(G.Get<bool>(ui, "alignmentDebugArmed"));
            Assert.Contains(logLines, l => l.Contains("[UI]") && l.Contains("AlignDbg ROW: "));
        }
    }
}
