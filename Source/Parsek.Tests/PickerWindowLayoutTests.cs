using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The small selector windows (the group picker in its Manage Groups and Set Parent
    /// Group modes, and the Logistics round-trip link picker) share
    /// <see cref="PickerWindowLayout"/>: placement next to the clicked control or centred
    /// over the parent window, clamped on screen, and the main windows' table look.
    ///
    /// <para>The placement cells are pure. The look cells are source scans (comments
    /// blanked, literals masked) because no headless seam can run an IMGUI draw path: they
    /// pin that both pickers draw their entries inside the shared dark table body box, in
    /// shared table rows, under a shared section heading, with Missions-width buttons, and
    /// that each picker places its first-open rect BEFORE the resize/screen fit (the order
    /// whose reversal opened both pickers shrunk at the screen's top-left corner: the fit
    /// widened the unplaced zero rect to the minimum width at the origin, so the placement
    /// branch never ran).</para>
    /// </summary>
    public class PickerWindowLayoutTests
    {
        private const float SW = 1920f;
        private const float SH = 1080f;

        // ----- ClampOnScreen -----

        [Fact]
        public void ClampOnScreen_MovesARectHangingOffTheRightAndBottomFullyOnScreen()
        {
            Rect r = PickerWindowLayout.ClampOnScreen(new Rect(1800f, 1000f, 320f, 360f), SW, SH);
            Assert.Equal(new Rect(1600f, 720f, 320f, 360f), r);
        }

        [Fact]
        public void ClampOnScreen_MovesANegativeOriginToZeroAndNeverResizes()
        {
            Rect r = PickerWindowLayout.ClampOnScreen(new Rect(-50f, -20f, 320f, 360f), SW, SH);
            Assert.Equal(new Rect(0f, 0f, 320f, 360f), r);
        }

        [Fact]
        public void ClampOnScreen_PinsARectLargerThanTheScreenToTheOrigin()
        {
            Rect r = PickerWindowLayout.ClampOnScreen(new Rect(100f, 100f, 2000f, 1200f), SW, SH);
            Assert.Equal(new Rect(0f, 0f, 2000f, 1200f), r);
        }

        [Fact]
        public void ClampOnScreen_LeavesARectThatFitsAlone()
        {
            var inside = new Rect(400f, 300f, 320f, 360f);
            Assert.Equal(inside, PickerWindowLayout.ClampOnScreen(inside, SW, SH));
        }

        [Theory]
        [InlineData(0f, 1080f)]
        [InlineData(1920f, 0f)]
        [InlineData(-1f, -1f)]
        public void ClampOnScreen_IsANoOpWithoutAScreenSize(float sw, float sh)
        {
            var r = new Rect(-50f, 5000f, 320f, 360f);
            Assert.Equal(r, PickerWindowLayout.ClampOnScreen(r, sw, sh));
        }

        // ----- PlaceNextToClick -----

        [Fact]
        public void PlaceNextToClick_OpensRightOfTheClickWithItsTopLevelWithIt()
        {
            Rect r = PickerWindowLayout.PlaceNextToClick(new Vector2(900f, 300f), 320f, 360f, SW, SH);
            Assert.Equal(900f + PickerWindowLayout.ClickOffsetPx, r.x);
            Assert.Equal(300f - PickerWindowLayout.ClickOffsetPx, r.y);
            Assert.Equal(320f, r.width);
            Assert.Equal(360f, r.height);
        }

        [Fact]
        public void PlaceNextToClick_FlipsLeftOfAClickNearTheRightEdge()
        {
            // The Logistics Interact column sits near the right edge of a 1920 screen.
            Rect r = PickerWindowLayout.PlaceNextToClick(new Vector2(1700f, 250f), 360f, 320f, SW, SH);
            Assert.Equal(1700f - PickerWindowLayout.ClickOffsetPx - 360f, r.x);
            Assert.True(r.xMax <= 1700f, "the flipped picker must not cover the click point");
        }

        [Fact]
        public void PlaceNextToClick_ClampsAClickNearTheBottomUpOntoTheScreen()
        {
            Rect r = PickerWindowLayout.PlaceNextToClick(new Vector2(900f, 1050f), 320f, 360f, SW, SH);
            Assert.Equal(SH - 360f, r.y);
            Assert.True(r.yMax <= SH);
        }

        [Fact]
        public void PlaceNextToClick_KeepsTheWholePickerOnScreenOnANarrowScreen()
        {
            // 1280x720, a click near the left: right side fits.
            Rect r = PickerWindowLayout.PlaceNextToClick(new Vector2(5f, 700f), 320f, 360f, 1280f, 720f);
            Assert.True(r.x >= 0f && r.xMax <= 1280f && r.y >= 0f && r.yMax <= 720f, r.ToString());
        }

        // ----- CenterOver -----

        [Fact]
        public void CenterOver_CentresThePickerOverItsParentWindow()
        {
            var parent = new Rect(0f, 8f, 1355f, 700f);
            Rect r = PickerWindowLayout.CenterOver(parent, 320f, 360f, SW, SH);
            Assert.Equal((double)parent.center.x, (double)r.center.x, 3);
            Assert.Equal((double)parent.center.y, (double)r.center.y, 3);
        }

        [Fact]
        public void CenterOver_ClampsAParentHalfOffScreen()
        {
            var parent = new Rect(1700f, 900f, 600f, 400f);
            Rect r = PickerWindowLayout.CenterOver(parent, 360f, 320f, SW, SH);
            Assert.Equal(SW - 360f, r.x);
            Assert.Equal(SH - 320f, r.y);
        }

        [Fact]
        public void CenterOver_AParentWithNoSizeCentresOnTheScreen()
        {
            Rect r = PickerWindowLayout.CenterOver(new Rect(0f, 0f, 0f, 0f), 320f, 360f, SW, SH);
            Assert.Equal((double)(SW / 2f), (double)r.center.x, 3);
            Assert.Equal((double)(SH / 2f), (double)r.center.y, 3);
        }

        // ----- PlaceOnOpen dispatch -----

        [Fact]
        public void PlaceOnOpen_NoClickPointCentresOverTheParent()
        {
            var parent = new Rect(100f, 50f, 1410f, 700f);
            Assert.False(PickerWindowLayout.HasClickPoint(PickerWindowLayout.NoClickPoint));
            Assert.Equal(
                PickerWindowLayout.CenterOver(parent, 360f, 320f, SW, SH),
                PickerWindowLayout.PlaceOnOpen(PickerWindowLayout.NoClickPoint, parent, 360f, 320f, SW, SH));
        }

        [Fact]
        public void PlaceOnOpen_AClickPointOpensNextToIt()
        {
            var parent = new Rect(100f, 50f, 1410f, 700f);
            var click = new Vector2(900f, 300f);
            Assert.True(PickerWindowLayout.HasClickPoint(click));
            Assert.True(PickerWindowLayout.HasClickPoint(Vector2.zero));
            Assert.Equal(
                PickerWindowLayout.PlaceNextToClick(click, 320f, 360f, SW, SH),
                PickerWindowLayout.PlaceOnOpen(click, parent, 320f, 360f, SW, SH));
        }

        [Fact]
        public void HasClickPoint_RejectsInfinity()
        {
            Assert.False(PickerWindowLayout.HasClickPoint(new Vector2(float.PositiveInfinity, 3f)));
            Assert.False(PickerWindowLayout.HasClickPoint(new Vector2(3f, float.NaN)));
        }

        // ----- the regression: placement must run before the screen fit -----

        [Fact]
        public void TheScreenFitShrinksAnUnplacedRectIntoTheTopLeftCorner()
        {
            // Why the order matters: a zero rect handed to the fit first is widened to the
            // minimum at the origin, after which the "width < 1" first-open branch never runs.
            var unplaced = new Rect(0f, 0f, 0f, 0f);
            Assert.True(WideWindowLayout.FitToScreen(ref unplaced, GroupPickerUI.GroupPopupMinW, SW, SH));
            Assert.Equal(new Rect(0f, 0f, GroupPickerUI.GroupPopupMinW, 0f), unplaced);
        }

        [Theory]
        [InlineData(GroupPickerUI.GroupPopupDefaultW, GroupPickerUI.GroupPopupDefaultH,
            GroupPickerUI.GroupPopupMinW, GroupPickerUI.GroupPopupMinH)]
        [InlineData(LogisticsWindowUI.LinkPickerDefaultW, LogisticsWindowUI.LinkPickerDefaultH,
            LogisticsWindowUI.LinkPickerMinW, LogisticsWindowUI.LinkPickerMinH)]
        public void APlacedFirstOpenRectSurvivesTheScreenFitUntouched(
            float defaultW, float defaultH, float minW, float minH)
        {
            Assert.True(defaultW >= minW && defaultH >= minH);
            foreach (Rect placed in new[]
                     {
                         PickerWindowLayout.PlaceOnOpen(PickerWindowLayout.NoClickPoint,
                             new Rect(0f, 8f, 1355f, 700f), defaultW, defaultH, SW, SH),
                         PickerWindowLayout.PlaceOnOpen(new Vector2(1890f, 1070f),
                             new Rect(0f, 8f, 1355f, 700f), defaultW, defaultH, SW, SH),
                         PickerWindowLayout.PlaceOnOpen(new Vector2(10f, 10f),
                             new Rect(0f, 8f, 1355f, 700f), defaultW, defaultH, 1280f, 720f),
                     })
            {
                Rect fitted = placed;
                Assert.False(WideWindowLayout.FitToScreen(ref fitted, minW, SW, SH), placed.ToString());
                Assert.Equal(placed, fitted);
            }
        }

        [Fact]
        public void EveryPickerMinimumHoldsItsTwoFullWidthButtons()
        {
            foreach (float minW in new[] { GroupPickerUI.GroupPopupMinW, LogisticsWindowUI.LinkPickerMinW })
                Assert.True(minW >= 2f * PickerWindowLayout.ButtonWidth + PickerWindowLayout.ButtonGap,
                    "minimum width " + minW.ToString(CultureInfo.InvariantCulture)
                    + " cannot hold two Missions-width buttons");
        }

        [Fact]
        public void PickerButtonsUseTheMissionsInteractWidths()
        {
            Assert.Equal(MissionsWindowUI.InteractButtonWidth, PickerWindowLayout.ButtonWidth);
            Assert.Equal(MissionsWindowUI.InteractPairButtonWidth, PickerWindowLayout.SmallButtonWidth);
            Assert.Equal(MissionsWindowUI.InteractButtonGap, PickerWindowLayout.ButtonGap);
            Assert.Equal(100f, PickerWindowLayout.ButtonWidth);
            Assert.Equal(48f, PickerWindowLayout.SmallButtonWidth);
        }

        [Fact]
        public void FormatPlacementLog_IsInvariantAndNamesTheAnchor()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string centred = PickerWindowLayout.FormatPlacementLog("Group picker",
                    PickerWindowLayout.NoClickPoint, new Rect(518f, 178f, 320f, 360f), SW, SH);
                Assert.Equal(
                    "Group picker placed centred over its parent window: x=518 y=178 w=320 h=360 screen=1920x1080",
                    centred);
                string next = PickerWindowLayout.FormatPlacementLog("Logistics link picker",
                    new Vector2(1290.4f, 250f), new Rect(922f, 242f, 360f, 320f), SW, SH);
                Assert.Contains("next to click (1290,250)", next);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ----- the shared look, by source scan -----

        [Fact]
        public void EntryListIsTheSharedDarkTableBodyBoxAroundTheSharedTableScrollView()
        {
            string src = Prepared("UI", "PickerWindowLayout.cs");
            string begin = MethodBody(src, "BeginEntryList");
            int box = begin.IndexOf("GUILayout.BeginVertical(ui.GetTableBodyBoxStyle()", StringComparison.Ordinal);
            int scroll = begin.IndexOf("GUILayout.BeginScrollView(", StringComparison.Ordinal);
            Assert.True(box >= 0 && scroll > box, "BeginEntryList must open the body box, then the scroll view in it");
            Assert.Contains("ui.GetTableScrollViewStyle()", begin.Substring(scroll));

            string end = MethodBody(src, "EndEntryList");
            int endScroll = end.IndexOf("GUILayout.EndScrollView()", StringComparison.Ordinal);
            int endBox = end.IndexOf("GUILayout.EndVertical()", StringComparison.Ordinal);
            Assert.True(endScroll >= 0 && endBox > endScroll);

            string row = MethodBody(src, "BeginEntryRow");
            Assert.Contains("GUILayout.BeginHorizontal(ui.GetTableRowStyle()", row);
            Assert.Contains("GUILayout.MinHeight(RowMinHeight)", row);
        }

        [Theory]
        [InlineData("GroupPickerUI.cs", "DrawGroupPopupContents")]
        [InlineData("LogisticsWindowUI.cs", "DrawLinkPickerContents")]
        public void BothPickersDrawTheHouseTableLook(string file, string method)
        {
            string body = MethodBody(Prepared("UI", file), method);
            int gap = body.IndexOf("PickerWindowLayout.DrawTitleGap()", StringComparison.Ordinal);
            int heading = body.IndexOf("parentUI.GetTableSectionHeaderStyle()", StringComparison.Ordinal);
            Assert.True(gap >= 0 && heading > gap,
                file + "." + method + ": the title-bar gap must be drawn before the heading (gap="
                + gap + " heading=" + heading + ")");
            Assert.True(body.IndexOf("GUILayout.", StringComparison.Ordinal) >= gap,
                file + "." + method + ": nothing may be laid out above the title-bar gap");
            int list = body.IndexOf("PickerWindowLayout.BeginEntryList(parentUI", StringComparison.Ordinal);
            int endList = body.IndexOf("PickerWindowLayout.EndEntryList()", StringComparison.Ordinal);
            Assert.True(heading >= 0 && list > heading && endList > list,
                file + "." + method + ": heading, then the entry list, then its close (heading="
                + heading + " list=" + list + " end=" + endList + ")");
            // Entries live inside the list, each in a shared row.
            Assert.Contains("PickerWindowLayout.BeginEntryRow(parentUI)",
                file == "GroupPickerUI.cs"
                    ? body + MethodBody(Prepared("UI", file), "DrawGroupPopupNode")
                    : body.Substring(list, endList - list));
            // No bare scroll view (the entries would sit on the window background again)
            // and no ad-hoc button widths.
            Assert.DoesNotContain("GUILayout.BeginScrollView(", body);
            Assert.Matches(new Regex(@"GUILayout\.Width\(PickerWindowLayout\.ButtonWidth\)[\s\S]*GUILayout\.Width\(PickerWindowLayout\.ButtonWidth\)"), body);
            Assert.DoesNotMatch(new Regex(@"GUILayout\.Width\(\s*\d+f?\s*\)"), body);
        }

        /// <summary>
        /// The pickers leave the same gap under their title bar as the main windows. Every
        /// main window opens its body with a GUILayout.Space of that height; this reads the
        /// FIRST Space in each window function and requires it to equal
        /// <see cref="ParsekUI.WindowContentTopGapPx"/>, which is what DrawTitleGap draws.
        /// </summary>
        [Theory]
        [InlineData("RecordingsTableUI.cs", "DrawRecordingsWindow")]
        [InlineData("LogisticsWindowUI.cs", "DrawWindow")]
        [InlineData("KerbalsWindowUI.cs", "DrawKerbalsWindow")]
        [InlineData("SettingsWindowUI.cs", "DrawSettingsWindow")]
        [InlineData("StructureListWindowUI.cs", "DrawWindow")]
        public void PickerTitleGapIsTheMainWindowsTitleGap(string file, string method)
        {
            string gap = MethodBody(Prepared("UI", "PickerWindowLayout.cs"), "DrawTitleGap");
            Assert.Contains("GUILayout.Space(ParsekUI.WindowContentTopGapPx)", gap);

            string body = MethodBody(Prepared("UI", file), method);
            Match first = Regex.Match(body, @"GUILayout\.Space\(\s*([^)]*?)\s*\)");
            Assert.True(first.Success, file + "." + method + ": no GUILayout.Space, this gate is vacuous.");
            string arg = first.Groups[1].Value;
            float value = arg == "ParsekUI.WindowContentTopGapPx" || arg == "WindowContentTopGapPx"
                ? ParsekUI.WindowContentTopGapPx
                : float.Parse(arg.TrimEnd('f', 'F'), CultureInfo.InvariantCulture);
            Assert.Equal(ParsekUI.WindowContentTopGapPx, value);
        }

        [Fact]
        public void GroupPickerEntryTextUsesTheSharedTableCellStyle()
        {
            string node = MethodBody(Prepared("UI", "GroupPickerUI.cs"), "DrawGroupPopupNode");
            Assert.Contains("PickerWindowLayout.BeginEntryRow(parentUI)", node);
            Assert.Contains("parentUI.GetTableCellStyle()", node);
        }

        [Theory]
        [InlineData("GroupPickerUI.cs", "Draw", "groupPopupRect")]
        [InlineData("LogisticsWindowUI.cs", "DrawLinkPicker", "linkPickerRect")]
        public void BothPickersPlaceTheirFirstOpenRectBeforeTheScreenFit(
            string file, string method, string rectField)
        {
            string body = MethodBody(Prepared("UI", file), method);
            int place = body.IndexOf(rectField + " = PickerWindowLayout.PlaceOnOpen(", StringComparison.Ordinal);
            int fit = body.IndexOf("ParsekUI.HandleResizeDrag(ref " + rectField, StringComparison.Ordinal);
            Assert.True(place >= 0 && fit > place,
                file + "." + method + ": the first-open placement must precede HandleResizeDrag (place="
                + place + " fit=" + fit + ")");
            Assert.DoesNotContain("Mathf.Clamp(", body);
            Assert.DoesNotContain("Screen.width -", body);
        }

        [Fact]
        public void SeamOpenersPassNoClickPointSoThePickerCentresOverItsWindow()
        {
            string recordings = Prepared("UI", "RecordingsTableUI.cs");
            foreach (string opener in new[]
                     {
                         "TryOpenGroupPickerForGroupForTesting", "TryOpenGroupPickerForRecordingForTesting",
                     })
                Assert.Contains("PickerWindowLayout.NoClickPoint", MethodBody(recordings, opener));
            Assert.Contains("groupPicker.Draw(recordingsWindowRect)", recordings);

            string logistics = Prepared("UI", "LogisticsWindowUI.cs");
            Assert.Contains("PickerWindowLayout.NoClickPoint",
                MethodBody(logistics, "OpenLinkPickerForTesting"));
            // The production Link round-trip click converts its GUI-space click to screen
            // space, as the Recordings G buttons do, so the picker opens next to the button.
            Assert.Contains("OpenLinkPicker(route, GUIUtility.GUIToScreenPoint(Event.current.mousePosition))",
                logistics);
        }

        // ----- helpers -----

        private static string Prepared(string dir, string file)
        {
            string path = Path.Combine(RepoRoot(), "Source", "Parsek", dir, file);
            Assert.True(File.Exists(path), "source file moved, this gate is vacuous: " + path);
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
        }

        private static string MethodBody(string prepared, string method)
        {
            var sig = new Regex(@"\b" + Regex.Escape(method) + @"\s*\([^;{}]*\)\s*\{",
                RegexOptions.Singleline);
            Match m = sig.Match(prepared);
            Assert.True(m.Success, "method " + method + " not found, this gate is vacuous.");
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', m.Index));
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
