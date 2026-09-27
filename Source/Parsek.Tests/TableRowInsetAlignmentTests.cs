using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GUI-TABLE-HEADERS-OFFSET-FROM-CELLS gate: a table's column-header row and its
    /// body rows must share ONE horizontal inset and ONE width per column.
    ///
    /// <para><b>What went wrong.</b> The 2026-09-11 GUI census measured every table
    /// window's rects out of the IMGUI tree dumps. Real Spawn Control drew its header
    /// directly in the window and its rows inside a scroll view plus a
    /// <c>GUI.skin.box</c> group, so every cell sat 8px left of its header and the
    /// expanding Craft column was 16px narrower than its header cell. Career State
    /// (header outside the body box) and the Structure window (header pinned outside
    /// the scroll view) were off by 4px the same way. Logistics was the only aligned
    /// table, because its header row and its body rows are siblings inside one box.</para>
    ///
    /// <para><b>The fix these cells pin.</b> Both rows open with an EXPLICIT shared
    /// container style built from <see cref="ParsekUI.TableRowHorizontalInsetPx"/>, and
    /// the body box contributes no horizontal inset of its own. A regression here is
    /// invisible in the product until someone reads a dump, and no headless seam can
    /// call an IMGUI draw path, so the witness is a source scan - comments blanked
    /// first through <see cref="SourceScanText"/>, because the draw sites' own comments
    /// discuss the very call shapes the scan looks for.</para>
    /// </summary>
    public class TableRowInsetAlignmentTests
    {
        /// <summary>One table: the file it lives in, its column-header method, one of
        /// its body-row methods, and whether its header is pinned OUTSIDE the body
        /// scroll view (which is what makes it reserve the scrollbar gutter).</summary>
        private sealed class TableSite
        {
            public string File;
            public string HeaderMethod;
            public string RowMethod;
            public bool HeaderPinnedOutsideScrollView;
            /// <summary>For a table whose pinned header and body scroll view share ONE
            /// body box: the method that opens that box around both. Null otherwise.</summary>
            public string BoxedTableMethod;
            /// <summary>For a table whose body cells are tinted: the style FIELDS its row
            /// method may draw with, each of which must be built from the shared table cell
            /// style. Null for the boxed tables, whose own cell covers them.</summary>
            public string[] CellStyles;
            /// <summary>Methods that pick one of <see cref="CellStyles"/> for a row (their
            /// every <c>return</c> must name one).</summary>
            public string[] CellStyleSelectors;
            /// <summary>For a table whose rows draw a column through a HELPER method rather
            /// than a <c>GUILayout.Width(ColW_...)</c> call in the row method itself: the
            /// helper call's text mapped to the width constant that helper draws, so the
            /// scan counts the column where the row calls it. Null for every other table.</summary>
            public Dictionary<string, string> CallWidths;
        }

        /// <summary>The Recordings tab's column helpers: each draws exactly one fixed
        /// column of the width named here (the Missions tab reuses the first two, which is
        /// why they are helpers).</summary>
        private static readonly Dictionary<string, string> RecordingsCallWidths =
            new Dictionary<string, string>
            {
                { "DrawLegacyRewindForwardCell(", "ColW_Rewind" },
                { "DrawReFlyColumnCell(", "ColW_ReFly" },
                { "DrawRewindColumnButton(", "ColW_Rewind" },
            };

        private static readonly string[] CareerCellStyles = { "cellStyle", "alertStyle", "nameCellStyle" };
        private static readonly string[] KerbalsCellStyles =
        {
            "cellPlainStyle", "cellGrayStyle", "cellDeadStyle", "cellRecoveredStyle",
            "cellAboardStyle", "cellStandInStyle",
        };
        private static readonly string[] KerbalsCellSelectors = { "StyleForRosterStatus", "StyleForEndState" };

        private static readonly TableSite[] Tables =
        {
            new TableSite
            {
                File = Path.Combine("UI", "SpawnControlUI.cs"),
                HeaderMethod = "DrawSpawnColumnHeader",
                RowMethod = "DrawSpawnCandidateRows",
                HeaderPinnedOutsideScrollView = true,
                BoxedTableMethod = "DrawSpawnCandidateTable",
            },
            new TableSite
            {
                File = Path.Combine("UI", "StructureListWindowUI.cs"),
                HeaderMethod = "DrawColumnHeader",
                RowMethod = "DrawStepRows",
                HeaderPinnedOutsideScrollView = true,
                BoxedTableMethod = "DrawStepTable",
            },
            new TableSite
            {
                File = Path.Combine("UI", "CareerStateWindowUI.cs"),
                HeaderMethod = "DrawContractsColumnHeader",
                RowMethod = "DrawContractRow",
                HeaderPinnedOutsideScrollView = false,
                CellStyles = CareerCellStyles,
            },
            new TableSite
            {
                File = Path.Combine("UI", "CareerStateWindowUI.cs"),
                HeaderMethod = "DrawStrategiesColumnHeader",
                RowMethod = "DrawStrategyRow",
                HeaderPinnedOutsideScrollView = false,
                CellStyles = CareerCellStyles,
            },
            // The Kerbals window's two tabs, rebuilt as column tables 2026-09-15. Both
            // headers are drawn INSIDE the window scroll view that holds their rows, so
            // like Career's two they take the body row container rather than the
            // gutter-reserving header variant.
            new TableSite
            {
                File = Path.Combine("UI", "KerbalsWindowUI.cs"),
                HeaderMethod = "DrawRosterColumnHeader",
                RowMethod = "DrawRosterRow",
                HeaderPinnedOutsideScrollView = false,
                CellStyles = KerbalsCellStyles,
                CellStyleSelectors = KerbalsCellSelectors,
            },
            new TableSite
            {
                File = Path.Combine("UI", "KerbalsWindowUI.cs"),
                HeaderMethod = "DrawFlightsColumnHeader",
                RowMethod = "DrawFlightRow",
                HeaderPinnedOutsideScrollView = false,
                CellStyles = KerbalsCellStyles,
                CellStyleSelectors = KerbalsCellSelectors,
            },
            // The Recordings tab (the Missions window's raw table), moved onto the house
            // table styles 2026-09-26. Its header is pinned ABOVE the body scroll view, so it
            // takes the gutter-reserving header row style. The Missions tab stays OFF this
            // list (GUI-MISSIONS-WINDOW-MERGED-FIRST-HEADER-CELL). This row pins the header
            // against the LEAF row; RecordingsTabEveryRowKindDrawsTheHeaderColumnSequence pins
            // it against all four row kinds, with the Info / flight-only columns' guards.
            new TableSite
            {
                File = Path.Combine("UI", "RecordingsTableUI.cs"),
                HeaderMethod = "DrawRecordingsTableHeader",
                RowMethod = "DrawRecordingRow",
                HeaderPinnedOutsideScrollView = true,
                CallWidths = RecordingsCallWidths,
            },
        };

        // A column-width constant: ColW_Foo / SpawnColW_Foo. The ORDERED sequence of
        // these inside a method body is that table's fixed-column layout.
        private static readonly Regex WidthConstant =
            new Regex(@"\b(?:[A-Za-z]*)ColW_[A-Za-z0-9_]+\b", RegexOptions.Compiled);

        // An expanding column, written two ways in the product: the GUILayout option,
        // and SpawnControlUI's own sortable-header helper whose trailing bool IS the
        // expand flag (it passes GUILayout.ExpandWidth(true) on for you).
        private static readonly Regex ExpandMarker =
            new Regex(@"GUILayout\.ExpandWidth\(\s*true\s*\)"
                      + @"|DrawSpawnSortableHeader\([^;]*?,\s*true\s*\)",
                      RegexOptions.Compiled);

        [Fact]
        public void EveryTableHeaderAndBodyRowDeclareTheSameColumnWidths()
        {
            foreach (TableSite t in Tables)
            {
                string prepared = ReadPreparedSource(t.File);
                string header = MethodBody(prepared, t.HeaderMethod, t.File);
                string row = MethodBody(prepared, t.RowMethod, t.File);

                List<string> headerWidths = OrderedWidthConstants(header, t.CallWidths);
                List<string> rowWidths = OrderedWidthConstants(row, t.CallWidths);

                Assert.True(headerWidths.Count > 0,
                    t.File + " / " + t.HeaderMethod
                    + ": no column-width constants found, this cell is vacuous.");
                Assert.Equal(headerWidths, rowWidths);

                int headerExpands = ExpandMarker.Matches(header).Count;
                int rowExpands = ExpandMarker.Matches(row).Count;
                Assert.True(headerExpands == rowExpands,
                    t.File + " / " + t.HeaderMethod + " vs " + t.RowMethod
                    + ": expanding-column count differs (header=" + headerExpands
                    + ", rows=" + rowExpands + "); an expanding column on one side only "
                    + "moves every column right of it.");
            }
        }

        [Fact]
        public void EveryTableHeaderAndBodyRowOpenWithTheSharedRowContainer()
        {
            foreach (TableSite t in Tables)
            {
                string prepared = ReadPreparedSource(t.File);
                string header = MethodBody(prepared, t.HeaderMethod, t.File);
                string row = MethodBody(prepared, t.RowMethod, t.File);

                string expectedHeaderStyle = t.HeaderPinnedOutsideScrollView
                    ? "GetTableHeaderRowStyle()"
                    : "GetTableRowStyle()";

                Assert.DoesNotContain("GUILayout.BeginHorizontal();", header);
                Assert.DoesNotContain("GUILayout.BeginHorizontal();", row);
                Assert.Contains(
                    "GUILayout.BeginHorizontal(parentUI." + expectedHeaderStyle + ")",
                    header);
                Assert.Contains("GUILayout.BeginHorizontal(parentUI.GetTableRowStyle())", row);
            }
        }

        /// <summary>
        /// The body list-area box must not reintroduce the inset the row containers
        /// removed: a bare <c>GUI.skin.box</c> carries L4/R4 margin AND L4/R4 padding
        /// (logged at runtime by RecordingsTableUI as "Rec table skin margins"), which
        /// is exactly the 8px Real Spawn Control was off by.
        /// </summary>
        [Fact]
        public void TableBodyBoxesUseTheZeroInsetBoxStyle()
        {
            foreach (string file in new[]
                     {
                         Path.Combine("UI", "SpawnControlUI.cs"),
                         Path.Combine("UI", "StructureListWindowUI.cs"),
                         Path.Combine("UI", "CareerStateWindowUI.cs"),
                         Path.Combine("UI", "KerbalsWindowUI.cs"),
                         Path.Combine("UI", "RecordingsTableUI.cs"),
                     })
            {
                string prepared = ReadPreparedSource(file);
                Assert.DoesNotContain("GUILayout.BeginVertical(GUI.skin.box)", prepared);
                // Prefix match: a box that also takes layout options is still this box.
                Assert.Contains("GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle()", prepared);
            }
        }

        /// <summary>
        /// Real Spawn Control and the Structure window keep their column header PINNED
        /// (outside the body scroll view) but inside the SAME dark body box as the rows.
        /// With the box around the rows only, the box's edge started 4px left of the
        /// header cells above it (the 2026-09-24 census, run
        /// 2026-09-24_2043_GUI-6-census-flight-playback: header cells from x=284, body
        /// box from x=280), so the table read as two misaligned blocks. The order the
        /// boxed-table method must keep: open the box, draw the header, open the scroll
        /// view (on the zero-horizontal-margin table scroll style, so it starts where the
        /// header row does), draw the rows, close the scroll view, close the box.
        /// </summary>
        [Fact]
        public void BoxedPinnedTablesDrawHeaderAndScrollViewInsideOneBodyBox()
        {
            int boxed = 0;
            foreach (TableSite t in Tables.Where(x => x.BoxedTableMethod != null))
            {
                boxed++;
                string prepared = ReadPreparedSource(t.File);
                string body = MethodBody(prepared, t.BoxedTableMethod, t.File);
                string where = t.File + " / " + t.BoxedTableMethod;

                int box = body.IndexOf("GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle()",
                    StringComparison.Ordinal);
                int head = body.IndexOf(t.HeaderMethod + "(", StringComparison.Ordinal);
                int scroll = body.IndexOf("BeginScrollView(", StringComparison.Ordinal);
                int rows = body.IndexOf(t.RowMethod + "(", StringComparison.Ordinal);
                int endScroll = body.IndexOf("GUILayout.EndScrollView()", StringComparison.Ordinal);
                int endBox = body.IndexOf("GUILayout.EndVertical()", StringComparison.Ordinal);

                Assert.True(box >= 0, where + ": does not open the shared body box.");
                Assert.True(head > box, where + ": the header is not drawn inside the body box.");
                Assert.True(scroll > head, where + ": the scroll view does not follow the header.");
                Assert.True(rows > scroll, where + ": the rows are not drawn inside the scroll view.");
                Assert.True(endScroll > rows && endBox > endScroll,
                    where + ": the scroll view and the box do not close after the rows, in that order.");

                string scrollCall = body.Substring(scroll,
                    body.IndexOf(';', scroll) - scroll);
                Assert.Contains("parentUI.GetTableScrollViewStyle()", scrollCall);
            }
            Assert.Equal(2, boxed);
        }

        /// <summary>
        /// Body cell TEXT, not just the cell rect, must start where its header's text does.
        /// The header cells are box-styled (box padding L4), so a body cell drawn with a
        /// bare <c>GUI.skin.label</c> (padding L0) put its text 4px left of the header text
        /// even with both rects at x=284 - which is what the 2026-09-24 census measured in
        /// Real Spawn Control on every column. Every label in these tables' row methods
        /// must pass the cell style derived from <c>ParsekUI.GetTableCellStyle()</c>.
        /// </summary>
        [Fact]
        public void BoxedPinnedTableRowsDrawEveryLabelWithTheSharedCellStyle()
        {
            var label = new Regex(@"GUILayout\.Label\((?<args>[^;]*)\)\s*;",
                RegexOptions.Compiled | RegexOptions.Singleline);
            foreach (TableSite t in Tables.Where(x => x.BoxedTableMethod != null))
            {
                string prepared = ReadPreparedSource(t.File);
                string row = MethodBody(prepared, t.RowMethod, t.File);
                MatchCollection labels = label.Matches(row);
                Assert.True(labels.Count > 0, t.File + " / " + t.RowMethod
                    + ": no labels found, this cell is vacuous.");
                foreach (Match m in labels)
                {
                    Assert.True(Regex.IsMatch(m.Groups["args"].Value, @"\bcellStyle\b"),
                        t.File + " / " + t.RowMethod + ": a body label does not pass the "
                        + "shared cell style, so its text sits one box padding left of its "
                        + "header: GUILayout.Label(" + m.Groups["args"].Value + ")");
                }
                // ... and that local really is the shared cell style (or a copy of it).
                Assert.Contains("GetTableCellStyle()", prepared);
            }
        }

        /// <summary>
        /// The same rule for the Career and Kerbals tables, whose body cells are TINTED
        /// (an overdue deadline, a lost kerbal) and include label-styled BUTTONS (the
        /// Career name links, the Kerbals Last-flight and Flights cells): every
        /// <c>GUILayout.Label</c> / <c>Button</c> / <c>Toggle</c> in the row method draws
        /// with one of the window's cell style fields (directly, or through a local picked
        /// by a selector method whose every return names one), never a bare skin style,
        /// and each of those fields is built from <c>ParsekUI.GetTableCellStyle()</c>. The
        /// 2026-09-25 census measured the Kerbals roster at header text x=289, body text
        /// x=285 before this.
        /// </summary>
        [Fact]
        public void TintedTableRowsDrawEveryCellWithACellStyleBuiltOnTheSharedOne()
        {
            var call = new Regex(@"GUILayout\.(?:Label|Button|Toggle)\(", RegexOptions.Compiled);
            var styleIdent = new Regex(@"\b(?!GUIStyle\b)[A-Za-z_][A-Za-z0-9_]*Style\b",
                RegexOptions.Compiled);
            int checkedSites = 0;
            foreach (TableSite t in Tables.Where(x => x.CellStyles != null))
            {
                checkedSites++;
                string prepared = ReadPreparedSource(t.File);
                string where = t.File + " / " + t.RowMethod;
                var allowed = new HashSet<string>(t.CellStyles, StringComparer.Ordinal);

                // Each allowed field is built on the shared cell style, through the one
                // local EnsureStyles reads it into.
                Assert.Contains("tableCell = parentUI.GetTableCellStyle()", prepared);
                foreach (string field in t.CellStyles)
                {
                    Assert.True(Regex.IsMatch(prepared,
                            @"\b" + Regex.Escape(field) + @"\s*=\s*(?:new GUIStyle\(\s*)?tableCell\b"),
                        t.File + ": cell style field " + field
                        + " is not built from the shared table cell style (tableCell).");
                }

                string row = MethodBody(prepared, t.RowMethod, t.File);
                var usedStyles = new HashSet<string>(StringComparer.Ordinal);
                int calls = 0;
                foreach (Match m in call.Matches(row))
                {
                    calls++;
                    string args = BalancedArgs(row, m.Index + m.Length - 1);
                    Assert.DoesNotContain("GUI.skin", args);
                    var names = styleIdent.Matches(args).Cast<Match>().Select(x => x.Value).ToList();
                    Assert.True(names.Count > 0, where + ": a body cell names no style: " + args);
                    foreach (string n in names) usedStyles.Add(n);
                }
                Assert.True(calls > 0, where + ": no cells found, this gate is vacuous.");

                foreach (string used in usedStyles)
                {
                    if (allowed.Contains(used)) continue;
                    // A row-local: its initializer may name only allowed fields and
                    // selector methods, and each selector returns only allowed fields.
                    Match local = Regex.Match(row,
                        @"GUIStyle\s+" + Regex.Escape(used) + @"\s*=(?<expr>[^;]*);");
                    Assert.True(local.Success, where + ": body cell style " + used
                        + " is neither a cell style field nor a row-local picked from one.");
                    string expr = local.Groups["expr"].Value;
                    foreach (Match n in styleIdent.Matches(expr))
                        Assert.True(allowed.Contains(n.Value),
                            where + ": " + used + " can be " + n.Value + ", which is not a cell style.");
                    Assert.NotNull(t.CellStyleSelectors);
                    foreach (string selector in t.CellStyleSelectors)
                    {
                        if (!expr.Contains(selector + "(")) continue;
                        string body = MethodBody(prepared, selector, t.File);
                        var returns = Regex.Matches(body, @"return\s+(?<v>[A-Za-z_][A-Za-z0-9_.]*)\s*;");
                        Assert.True(returns.Count > 0, t.File + " / " + selector + ": no returns.");
                        foreach (Match r in returns)
                            Assert.True(allowed.Contains(r.Groups["v"].Value),
                                t.File + " / " + selector + " returns " + r.Groups["v"].Value
                                + ", which is not a cell style.");
                    }
                }
            }
            Assert.Equal(4, checkedSites);
        }

        /// <summary>
        /// The other text drawn INSIDE those two windows' table bodies - the Kerbals
        /// fold rows (plain-kerbals fold, each Flights group) and Career's grey "none
        /// active now" line above pending rows - sits under the first column too, so it
        /// is built on the shared cell style as well.
        /// </summary>
        [Fact]
        public void TintedTableBodyFoldAndEmptyLinesUseTheSharedCellStyle()
        {
            string kerbals = ReadPreparedSource(Path.Combine("UI", "KerbalsWindowUI.cs"));
            Assert.Matches(@"\bgroupHeaderStyle\s*=\s*new GUIStyle\(\s*tableCell\b", kerbals);
            string career = ReadPreparedSource(Path.Combine("UI", "CareerStateWindowUI.cs"));
            Assert.Matches(@"\bgrayCellStyle\s*=\s*new GUIStyle\(\s*tableCell\b", career);
            foreach (string tab in new[] { "DrawContractsTab", "DrawStrategiesTab" })
            {
                string body = MethodBody(career, tab, "CareerStateWindowUI.cs");
                int box = body.IndexOf("GetTableBodyBoxStyle()", StringComparison.Ordinal);
                Assert.True(box > 0, tab + ": no body box.");
                Assert.Contains("grayCellStyle", body.Substring(box));
            }
        }

        /// <summary>
        /// The shared cell style takes the column header style's HORIZONTAL padding
        /// through the pure rule, so the text inset cannot drift from the header's.
        /// </summary>
        [Fact]
        public void TheSharedCellStyleTakesTheColumnHeaderHorizontalPadding()
        {
            string prepared = ReadPreparedSource("ParsekUI.cs");
            string ensure = Regex.Replace(
                MethodBody(prepared, "EnsureSharedHeaderStyles", "ParsekUI.cs"), @"\s+", " ");
            Assert.Contains("RectOffset hdrPad = sharedColumnHeaderStyle.padding;", ensure);
            Assert.Contains(
                "ComposeTableCellPadding( hdrPad.left, hdrPad.right, lblPad.top, lblPad.bottom)",
                ensure);
            string cell = AssignmentBlock(prepared, "sharedTableCellStyle");
            Assert.Contains("new GUIStyle(GUI.skin.label)", cell);
            Assert.Contains("cellPad[0]", cell);
        }

        /// <summary>The padding rule itself: horizontal from the header, vertical from
        /// the label, in {left, right, top, bottom} order.</summary>
        [Fact]
        public void ComposeTableCellPadding_TakesHeaderHorizontalAndLabelVertical()
        {
            Assert.Equal(new[] { 4, 4, 3, 3 }, ParsekUI.ComposeTableCellPadding(4, 4, 3, 3));
            Assert.Equal(new[] { 6, 2, 0, 1 }, ParsekUI.ComposeTableCellPadding(6, 2, 0, 1));
        }

        /// <summary>
        /// The text delta, on the numbers the 2026-09-24 census measured: header and body
        /// cells both at x=284 (0px rect delta), the boxed header padding L4. A bare label
        /// (L0) reads 4px left; the Structure window's old hand-set indent (L5) read 1px
        /// right; the header-derived padding reads 0.
        /// </summary>
        [Fact]
        public void HeaderToCellTextDelta_CensusNumbers()
        {
            const int cellX = 284, headerPad = 4;
            Assert.Equal(-4, ParsekUI.HeaderToCellTextDeltaPx(cellX, headerPad, cellX, 0));
            Assert.Equal(1, ParsekUI.HeaderToCellTextDeltaPx(cellX, headerPad, cellX, 5));
            int derived = ParsekUI.ComposeTableCellPadding(headerPad, headerPad, 0, 0)[0];
            Assert.Equal(0, ParsekUI.HeaderToCellTextDeltaPx(cellX, headerPad, cellX, derived));
        }

        /// <summary>
        /// A header pinned OUTSIDE its body scroll view reserves the vertical-scrollbar
        /// gutter as its own right padding. That reservation is only correct if the bar
        /// is always actually there, so those scroll views must FORCE it
        /// (<c>alwaysShowVertical: true</c>); with auto bars a short list shows none and
        /// the body runs a scrollbar-width wider than the header reserved for.
        /// </summary>
        [Fact]
        public void PinnedHeaderTablesForceTheVerticalScrollbar()
        {
            foreach (TableSite t in Tables.Where(x => x.HeaderPinnedOutsideScrollView))
            {
                string prepared = ReadPreparedSource(t.File);
                var forced = new Regex(
                    @"BeginScrollView\(\s*[A-Za-z0-9_.]+\s*,\s*false\s*,\s*true\s*,",
                    RegexOptions.Compiled);
                Assert.True(forced.IsMatch(prepared),
                    t.File + ": a table whose header reserves the scrollbar gutter must "
                    + "open its body scroll view with alwaysShowVertical: true.");
            }
        }

        /// <summary>
        /// The two shared row containers must carry the SAME left inset constant - that
        /// is the whole point of the pair - and the header variant must differ from the
        /// body variant only by adding the scrollbar gutter on the RIGHT.
        /// </summary>
        [Fact]
        public void TheSharedRowContainersCarryOneLeftInsetConstant()
        {
            Assert.Equal(0, ParsekUI.TableRowHorizontalInsetPx);

            string prepared = ReadPreparedSource("ParsekUI.cs");

            string rowStyle = AssignmentBlock(prepared, "sharedTableRowStyle");
            string headerStyle = AssignmentBlock(prepared, "sharedTableHeaderRowStyle");
            string boxStyle = AssignmentBlock(prepared, "sharedTableBodyBoxStyle");

            // Every horizontal edge in all three comes from the one constant; nothing
            // spells a literal inset of its own.
            foreach (var pair in new[]
                     {
                         Tuple.Create("sharedTableRowStyle", rowStyle),
                         Tuple.Create("sharedTableHeaderRowStyle", headerStyle),
                         Tuple.Create("sharedTableBodyBoxStyle", boxStyle),
                     })
            {
                Assert.Contains("TableRowHorizontalInsetPx", pair.Item2);
            }

            // The header variant is built FROM the body variant, so the left inset
            // cannot drift between them.
            Assert.Contains("new GUIStyle(sharedTableRowStyle)", headerStyle);
            // ... and its only horizontal difference is the gutter on the right.
            Assert.Contains("VerticalScrollbarGutterWidth()", headerStyle);
            Assert.DoesNotContain("VerticalScrollbarGutterWidth()", rowStyle);
            Assert.DoesNotContain("VerticalScrollbarGutterWidth()", boxStyle);
        }

        /// <summary>
        /// The reserved gutter is DERIVED from the skin, never a literal: a pinned
        /// header owes its body the scroll view's real scrollbar footprint
        /// (<c>fixedWidth + margin.left</c>, per decompiled
        /// <c>GUIScrollGroup.SetHorizontal</c>) PLUS one cell margin, because
        /// <c>GUILayoutGroup.SetHorizontal</c> takes
        /// <c>max(style.padding.right, lastChild.margin.right)</c> - so that padding
        /// REPLACES the body's own trailing cell margin instead of adding to it.
        /// Reserving <c>fixedWidth</c> alone is what PR #1679 shipped and what the
        /// 2026-09-11 re-flight measured 5px short.
        /// </summary>
        [Fact]
        public void TheReservedGutterIsDerivedFromTheSkinScrollbarAndOneCellMargin()
        {
            string prepared = ReadPreparedSource("ParsekUI.cs");

            string footprint = MethodBody(prepared, "VerticalScrollbarFootprintWidth",
                "ParsekUI.cs");
            // The footprint is BOTH scrollbar terms, read off the live skin.
            Assert.Contains("verticalScrollbar.fixedWidth", footprint);
            Assert.Contains("verticalScrollbar.margin.left", footprint);

            string gutter = MethodBody(prepared, "VerticalScrollbarGutterWidth",
                "ParsekUI.cs");
            Assert.Contains("VerticalScrollbarFootprintWidth()", gutter);
            Assert.Contains("TableCellHorizontalMarginPx()", gutter);
            // No digits in that body: any number there would be a hard-coded
            // reservation, which is the defect this cell exists for.
            Assert.False(Regex.IsMatch(gutter, @"\d"),
                "ParsekUI.VerticalScrollbarGutterWidth must derive the gutter from the "
                + "skin, not spell a literal: " + gutter);

            string cellMargin = MethodBody(prepared, "TableCellHorizontalMarginPx",
                "ParsekUI.cs");
            Assert.Contains("label.margin.right", cellMargin);

            // Fallbacks only, for a skin that has no scrollbar style at all. Pinned to
            // what the 2026-09-11 census measured (run
            // 2026-09-11_1706_GUI-6-census-flight-playback): a 15px bar plus its 1px
            // left margin stepped every scroll view's content in by 16 (Real Spawn
            // Control 730 -> 714, Structure 980 -> 964), and every cell style the
            // tables use reports margin R4.
            Assert.Equal(16f, ParsekUI.DefaultVerticalScrollbarFootprintWidth);
            Assert.Equal(4, ParsekUI.DefaultTableCellHorizontalMarginPx);
        }

        /// <summary>
        /// The Recordings tab owes its pinned header the SAME derived gutter as the other
        /// pinned-header tables, and since 2026-09-26 takes it the same way: through the
        /// header row style (<c>GetTableHeaderRowStyle</c>, whose right padding is
        /// <c>VerticalScrollbarGutterWidth()</c>, pinned above), not a trailing
        /// <c>GUILayout.Space</c> - which would now reserve it twice. Its header's last cell
        /// (the Archive container) has margin R0, so the padding form ends the header at the
        /// x the old Space form did.
        ///
        /// <para>The Missions tab is deliberately NOT here. The same census measured its
        /// rows at x=10 width 1319 - its body box spends NO padding - so its header owes
        /// the bare scrollbar footprint, not the gutter, and pointing it at this helper
        /// would walk its data columns from 1px off to 4px off the other way. It keeps
        /// its own reservation under GUI-MISSIONS-WINDOW-MERGED-FIRST-HEADER-CELL, whose
        /// fix is structural (the merged leading header cell).</para>
        /// </summary>
        [Fact]
        public void TheRecordingsTabGutterRoutesThroughTheSharedDerivation()
        {
            string recordings = ReadPreparedSource(Path.Combine("UI", "RecordingsTableUI.cs"));
            string header = MethodBody(recordings, "DrawRecordingsTableHeader",
                "RecordingsTableUI.cs");
            Assert.Contains("GUILayout.BeginHorizontal(parentUI.GetTableHeaderRowStyle())", header);
            // ... and reserves it exactly once: no trailing Space on top of the padding, and
            // no Space sized from a locally read fixedWidth.
            Assert.DoesNotContain(
                "GUILayout.Space(ParsekUI.VerticalScrollbarGutterWidth())", recordings);
            Assert.DoesNotContain("GUILayout.Space(scrollbarWidth)", recordings);

            string missions = ReadPreparedSource(Path.Combine("UI", "MissionsWindowUI.cs"));
            Assert.DoesNotContain(
                "GUILayout.Space(ParsekUI.VerticalScrollbarGutterWidth())", missions);
        }

        /// <summary>
        /// The arithmetic the fix rests on, run against the numbers the 2026-09-11
        /// census actually measured, with the header's right padding built the way
        /// <c>EnsureSharedHeaderStyles</c> builds it
        /// (<c>TableRowHorizontalInsetPx + (int)VerticalScrollbarGutterWidth()</c>) through
        /// the SHIPPED gutter rule <c>ParsekUI.ComposeScrollbarGutterWidth</c>, fed the
        /// measured skin terms (the live wrapper reads <c>GUI.skin</c>, which cannot run
        /// headless; <see cref="TheReservedGutterIsDerivedFromTheSkinScrollbarAndOneCellMargin"/>
        /// pins that the wrapper routes through the footprint and cell-margin readers).
        /// A gutter rule that takes the max of the two terms instead of their sum reds
        /// here. Unity reduces a styled group's content width by
        /// <c>max(padding.left, firstChild.margin.left)</c> on the left and
        /// <c>max(padding.right, lastChild.margin.right)</c> on the right (decompiled
        /// <c>GUILayoutGroup.SetHorizontal</c>, UnityEngine.IMGUIModule, KSP 1.12.5).
        /// </summary>
        [Fact]
        public void HeaderAndBodyContentWidthsMatchOnlyWithFootprintPlusCellMargin()
        {
            // Census-measured layout facts (run 2026-09-11_1706_GUI-6-census-flight-playback),
            // kept literal because Unity produced them, not Parsek: every cell style
            // reports margin R4, the bar is 15 wide, and each scroll view's content is one
            // 16px footprint narrower than the header row above it.
            const int cellMargin = 4;
            const int barFixedWidth = 15;   // what PR #1679 reserved
            var measuredHeaderAndBodyRectWidths = new[]
            {
                Tuple.Create(730, 714),     // Real Spawn Control
                Tuple.Create(980, 964),     // Structure window
            };

            foreach (var widths in measuredHeaderAndBodyRectWidths)
            {
                int headerRectWidth = widths.Item1;
                int footprint = widths.Item1 - widths.Item2;
                int shippedHeaderPaddingRight = ParsekUI.TableRowHorizontalInsetPx
                    + (int)ParsekUI.ComposeScrollbarGutterWidth(footprint, cellMargin);
                // Body row: the shared row style, zero horizontal padding, 4px cells.
                int bodyContent = ContentWidth(widths.Item2, 0, 0, cellMargin, cellMargin);

                Assert.Equal(
                    bodyContent,
                    ContentWidth(headerRectWidth, ParsekUI.TableRowHorizontalInsetPx,
                        shippedHeaderPaddingRight, cellMargin, cellMargin));

                // The two reservations that do NOT work, and by exactly how much: the
                // 5px measured after #1679 splits into 1px of scrollbar margin and the
                // 4px cell margin the max() replaces.
                Assert.Equal(
                    cellMargin + (footprint - barFixedWidth),
                    ContentWidth(headerRectWidth, 0, barFixedWidth, cellMargin, cellMargin)
                        - bodyContent);
                Assert.Equal(
                    cellMargin,
                    ContentWidth(headerRectWidth, 0, footprint, cellMargin, cellMargin)
                        - bodyContent);
            }
        }

        /// <summary>Content width Unity gives the children of a styled horizontal layout
        /// group.</summary>
        private static int ContentWidth(int rectWidth, int paddingLeft, int paddingRight,
            int firstChildMarginLeft, int lastChildMarginRight)
        {
            return rectWidth
                   - Math.Max(paddingLeft, firstChildMarginLeft)
                   - Math.Max(paddingRight, lastChildMarginRight);
        }

        /// <summary>
        /// Logistics is the table the census measured at zero offset, and the reason is
        /// structural: its column-header row and its body rows are siblings inside ONE
        /// <c>BeginVertical(GUI.skin.box)</c>, so they cannot diverge. Pinned here so a
        /// future refactor that lifts either header out of its bubble has to face this
        /// cell rather than silently earning the 4px offset every other table had.
        /// </summary>
        [Fact]
        public void LogisticsKeepsItsHeaderInsideTheSameBubbleAsItsRows()
        {
            string prepared = ReadPreparedSource(Path.Combine("UI", "LogisticsWindowUI.cs"));

            foreach (var pair in new[]
                     {
                         Tuple.Create("DrawRouteSectionBubble", "DrawRouteSortableHeader"),
                         Tuple.Create("DrawCandidateSectionBubble", "DrawCandidateColumnHeader"),
                     })
            {
                string body = MethodBody(prepared, pair.Item1,
                    Path.Combine("UI", "LogisticsWindowUI.cs"));
                int box = body.IndexOf("GUILayout.BeginVertical(GUI.skin.box)", StringComparison.Ordinal);
                int head = body.IndexOf(pair.Item2 + "(", StringComparison.Ordinal);
                int end = body.IndexOf("GUILayout.EndVertical()", StringComparison.Ordinal);
                Assert.True(box >= 0 && head > box && end > head,
                    "LogisticsWindowUI." + pair.Item1 + ": " + pair.Item2
                    + " must be called INSIDE the section bubble's box group (box=" + box
                    + ", header=" + head + ", end=" + end + ").");
            }
        }

        /// <summary>
        /// On a screen narrower than the Missions window's natural width (1355 px), its two
        /// tabs scroll horizontally, and the pinned column header must scroll WITH the body
        /// or every column is misaligned by the scroll offset. The shared
        /// <c>WideWindowScroll</c> wrapper does that by holding the header AND the body
        /// scroll view inside one horizontal scroll view, so both tabs must open it before
        /// the header, draw the body scroll view inside it, and close it after the body.
        /// Logistics already keeps its headers inside its one scroll view, so it takes the
        /// content floor INSIDE that scroll view instead.
        /// </summary>
        [Fact]
        public void WideWindowScrollWrapsTheHeaderAndTheBodyTogether()
        {
            foreach (var site in new[]
                     {
                         Tuple.Create(Path.Combine("UI", "RecordingsTableUI.cs"),
                             "DrawRecordingsWindow", "DrawRecordingsTableHeader("),
                         Tuple.Create(Path.Combine("UI", "MissionsWindowUI.cs"),
                             "DrawMissionsTabContent", "DrawColumnHeader("),
                     })
            {
                string body = MethodBody(ReadPreparedSource(site.Item1), site.Item2, site.Item1);
                string where = site.Item1 + " / " + site.Item2;
                int begin = body.IndexOf("BeginPinnedHeaderArea(", StringComparison.Ordinal);
                int head = body.IndexOf(site.Item3, StringComparison.Ordinal);
                int scroll = body.IndexOf("BeginScrollView(", head < 0 ? 0 : head, StringComparison.Ordinal);
                int endScroll = body.IndexOf("GUILayout.EndScrollView()", scroll < 0 ? 0 : scroll,
                    StringComparison.Ordinal);
                int end = body.IndexOf("EndPinnedHeaderArea()", StringComparison.Ordinal);
                Assert.True(begin >= 0, where + ": does not open the horizontal-scroll wrapper.");
                Assert.True(head > begin, where + ": the header is not inside the wrapper.");
                Assert.True(scroll > head, where + ": the body scroll view does not follow the header.");
                Assert.True(endScroll > scroll && end > endScroll,
                    where + ": the wrapper does not close after the body scroll view.");
            }

            string logistics = Path.Combine("UI", "LogisticsWindowUI.cs");
            string draw = MethodBody(ReadPreparedSource(logistics), "DrawWindow", logistics);
            int lScroll = draw.IndexOf("GUILayout.BeginScrollView(", StringComparison.Ordinal);
            int floor = draw.IndexOf("BeginContentFloor(", StringComparison.Ordinal);
            int section = draw.IndexOf("DrawRouteSectionBubble(", StringComparison.Ordinal);
            int endFloor = draw.IndexOf("EndContentFloor()", StringComparison.Ordinal);
            int lEnd = draw.IndexOf("GUILayout.EndScrollView()", StringComparison.Ordinal);
            Assert.True(lScroll >= 0 && floor > lScroll && section > floor
                        && endFloor > section && lEnd > endFloor,
                "LogisticsWindowUI.DrawWindow: the content floor must open inside the scroll "
                + "view before the first section and close before the scroll view does.");
        }

        /// <summary>
        /// The Recordings tab draws FOUR kinds of row under one header - the recording leaf
        /// (<c>DrawRecordingRow</c>), the folder (<c>DrawGroupTree</c>), the chain / grouped
        /// block (<c>DrawRecordingBlock</c>) and the STASH virtual group
        /// (<c>DrawVirtualUnfinishedFlightsGroup</c>) - and one column is CONDITIONAL: the
        /// Watch column draws only while <c>parentUI.InFlightMode</c>. Every row kind must
        /// draw the header's exact column sequence - name column included - with the Watch
        /// column behind the SAME guard, or in flight every column right of the gap shifts.
        /// (Until 2026-09-26 an Info toggle guarded a second band the same way; it is gone,
        /// and a stray <c>showExpandedStats</c> guard would now read as an untagged column
        /// only if the field came back.)
        /// The row is the span from its <c>BeginHorizontal(parentUI.GetTableRowStyle())</c>
        /// to the matching <c>EndHorizontal</c>; columns a row draws through a helper count
        /// where the helper is called (<see cref="RecordingsCallWidths"/>).
        /// </summary>
        [Fact]
        public void RecordingsTabEveryRowKindDrawsTheHeaderColumnSequence()
        {
            string file = Path.Combine("UI", "RecordingsTableUI.cs");
            string prepared = ReadPreparedSource(file);

            string header = RowSpan(MethodBody(prepared, "DrawRecordingsTableHeader", file),
                "GUILayout.BeginHorizontal(parentUI.GetTableHeaderRowStyle())", file);
            List<string> expected = TaggedColumnSequence(header);

            // Non-vacuous: the sequence carries the name column and both guarded groups.
            Assert.Contains(NameColumnToken, expected);
            Assert.Contains("ColW_Phase", expected);
            Assert.Contains("ColW_Site", expected);
            Assert.Contains("flight:ColW_Watch", expected);
            Assert.DoesNotContain(expected, t => t.Contains("MaxAlt") || t.Contains("MaxSpd"));
            Assert.Contains("ColW_Rewind", expected);
            Assert.True(expected.Count >= 15,
                "header column sequence is implausibly short: " + string.Join(", ", expected));

            foreach (string method in new[]
                     {
                         "DrawRecordingRow", "DrawGroupTree", "DrawRecordingBlock",
                         "DrawVirtualUnfinishedFlightsGroup",
                     })
            {
                // From the method's signature rather than MethodBody: DrawGroupTree's Watch
                // log lines nest quotes inside interpolation holes, which the literal masker
                // does not model, so its brace walk ends that body early. The row span only
                // counts Begin/EndHorizontal calls, which no literal carries.
                string row = RowSpan(TextFromMethod(prepared, method, file),
                    "GUILayout.BeginHorizontal(parentUI.GetTableRowStyle())", file + " / " + method);
                List<string> actual = TaggedColumnSequence(row);
                Assert.True(expected.SequenceEqual(actual),
                    "RecordingsTableUI." + method + " draws a different column sequence than the header."
                    + " header=[" + string.Join(", ", expected) + "] row=["
                    + string.Join(", ", actual) + "]");
            }
        }

        private const string NameColumnToken = "<name>";

        // What marks the expanding name column: the header's sortable Name cell (its
        // expand flag is the `true` after the zero width), the leaf's name helper, and a
        // folder / block / STASH row's own ExpandWidth label.
        private static readonly Regex NameColumnMarker = new Regex(
            @"DrawSortableHeader\([^;]*?SortColumn\.Name\s*,\s*0\s*,\s*true"
            + @"|DrawRecordingNameCell\("
            + @"|GUILayout\.ExpandWidth\(\s*true\s*\)",
            RegexOptions.Compiled);

        /// <summary>
        /// The column sequence of one row span: width constants, helper-drawn columns and the
        /// name column in source order, each tagged <c>flight:</c> when it sits under an
        /// <c>if (parentUI.InFlightMode)</c> guard,
        /// with runs of one column collapsed (a cell drawn by one of several exclusive
        /// branches names its width once per branch).
        /// </summary>
        private static List<string> TaggedColumnSequence(string span)
        {
            var guards = new List<Tuple<int, int, string>>();
            AddGuardSpans(span, "if (parentUI.InFlightMode)", "flight:", guards);

            var hits = WidthConstant.Matches(span).Cast<Match>()
                .Select(m => Tuple.Create(m.Index, m.Value)).ToList();
            foreach (Match m in NameColumnMarker.Matches(span))
                hits.Add(Tuple.Create(m.Index, NameColumnToken));
            foreach (var kv in RecordingsCallWidths)
            {
                int at = 0;
                while ((at = span.IndexOf(kv.Key, at, StringComparison.Ordinal)) >= 0)
                {
                    hits.Add(Tuple.Create(at, kv.Value));
                    at += kv.Key.Length;
                }
            }

            var collapsed = new List<string>();
            foreach (var hit in hits.OrderBy(h => h.Item1))
            {
                string tag = "";
                foreach (var g in guards)
                    if (hit.Item1 > g.Item1 && hit.Item1 < g.Item2) tag = g.Item3;
                string token = tag + hit.Item2;
                if (collapsed.Count == 0 || collapsed[collapsed.Count - 1] != token)
                    collapsed.Add(token);
            }
            return collapsed;
        }

        /// <summary>Every <paramref name="guard"/> in the span, as the range its body covers:
        /// a braced block, or the single statement up to its semicolon.</summary>
        private static void AddGuardSpans(string span, string guard, string tag,
            List<Tuple<int, int, string>> into)
        {
            int at = 0;
            while ((at = span.IndexOf(guard, at, StringComparison.Ordinal)) >= 0)
            {
                int bodyStart = at + guard.Length;
                while (bodyStart < span.Length && char.IsWhiteSpace(span[bodyStart])) bodyStart++;
                int bodyEnd;
                if (bodyStart < span.Length && span[bodyStart] == '{')
                {
                    // The closing brace is the first later line that is a lone "}" at the
                    // guard's own indent. By LINE rather than by brace count: the literal
                    // masker does not model quotes nested in interpolation holes (the folder
                    // row's Watch log lines have them), so a brace walk can end early.
                    int lineStart = span.LastIndexOf('\n', at) + 1;
                    int indent = 0;
                    while (lineStart + indent < at && span[lineStart + indent] == ' ') indent++;
                    var closer = new Regex(@"\n {" + indent + @"}\}[ \t]*\r?(\n|$)");
                    Match close = closer.Match(span, bodyStart + 1);
                    bodyEnd = close.Success ? close.Index + 1 + indent : span.Length;
                }
                else
                {
                    bodyEnd = span.IndexOf(';', bodyStart);
                    if (bodyEnd < 0) bodyEnd = span.Length;
                }
                into.Add(Tuple.Create(at, bodyEnd, tag));
                at = bodyEnd;
            }
        }

        /// <summary>The prepared text from a method's signature to the end of the file.</summary>
        private static string TextFromMethod(string prepared, string method, string file)
        {
            var sig = new Regex(@"\b" + Regex.Escape(method) + @"\s*\([^;{}]*\)\s*\{",
                RegexOptions.Singleline);
            Match m = sig.Match(prepared);
            Assert.True(m.Success, file + ": method " + method + " not found, this gate is vacuous.");
            return prepared.Substring(m.Index);
        }

        /// <summary>The text of one table row: from <paramref name="opener"/> to the
        /// <c>EndHorizontal</c> that closes it (nested horizontal groups balanced).</summary>
        private static string RowSpan(string body, string opener, string file)
        {
            int start = body.IndexOf(opener, StringComparison.Ordinal);
            Assert.True(start >= 0, file + ": row opener " + opener + " not found, this gate is vacuous.");
            var marks = new Regex(@"GUILayout\.(Begin|End)Horizontal\(");
            int depth = 0;
            foreach (Match m in marks.Matches(body, start))
            {
                depth += m.Groups[1].Value == "Begin" ? 1 : -1;
                if (depth == 0)
                    return body.Substring(start, m.Index - start);
            }
            throw new InvalidOperationException(file + ": unbalanced horizontal groups after " + opener);
        }

        // ───────────────────────────── helpers ─────────────────────────────

        /// <summary>
        /// The ordered fixed-column layout a method declares. Runs of the SAME constant
        /// collapse to one entry: a cell drawn in either of two mutually exclusive
        /// branches (Real Spawn Control's State column, tinted or not) spells its width
        /// twice for one column. The cost is that two genuinely adjacent columns sharing
        /// one width constant read as a single column here - which still catches a width
        /// changing on one side, just not a duplicated column.
        /// </summary>
        private static List<string> OrderedWidthConstants(string span,
            Dictionary<string, string> callWidths = null)
        {
            // Width constants and (for a table that has them) column-helper calls, merged
            // in source order.
            var hits = WidthConstant.Matches(span).Cast<Match>()
                .Select(m => Tuple.Create(m.Index, m.Value)).ToList();
            if (callWidths != null)
            {
                foreach (var kv in callWidths)
                {
                    int at = 0;
                    while ((at = span.IndexOf(kv.Key, at, StringComparison.Ordinal)) >= 0)
                    {
                        hits.Add(Tuple.Create(at, kv.Value));
                        at += kv.Key.Length;
                    }
                }
            }
            var all = hits.OrderBy(h => h.Item1).Select(h => h.Item2);
            var collapsed = new List<string>();
            foreach (string w in all)
            {
                if (collapsed.Count == 0 || !string.Equals(collapsed[collapsed.Count - 1], w,
                        StringComparison.Ordinal))
                {
                    collapsed.Add(w);
                }
            }
            return collapsed;
        }

        /// <summary>
        /// Body of the first method whose signature line names <paramref name="method"/>,
        /// from its opening brace to the matching close. Brace-balanced, which is safe
        /// because the text has already had comments blanked and literals masked.
        /// </summary>
        private static string MethodBody(string prepared, string method, string file)
        {
            var sig = new Regex(@"\b" + Regex.Escape(method) + @"\s*\([^;{}]*\)\s*\{",
                RegexOptions.Compiled | RegexOptions.Singleline);
            Match m = sig.Match(prepared);
            Assert.True(m.Success,
                file + ": method " + method + " not found, this gate is vacuous.");

            int open = prepared.IndexOf('{', m.Index);
            int depth = 0;
            for (int i = open; i < prepared.Length; i++)
            {
                if (prepared[i] == '{') depth++;
                else if (prepared[i] == '}')
                {
                    depth--;
                    if (depth == 0) return prepared.Substring(open, i - open + 1);
                }
            }
            throw new InvalidOperationException(
                file + ": unbalanced braces walking " + method);
        }

        /// <summary>The argument text of the call whose opening parenthesis is at
        /// <paramref name="openParen"/>, parentheses balanced (literals are masked).</summary>
        private static string BalancedArgs(string prepared, int openParen)
        {
            int depth = 0;
            for (int i = openParen; i < prepared.Length; i++)
            {
                if (prepared[i] == '(') depth++;
                else if (prepared[i] == ')')
                {
                    depth--;
                    if (depth == 0) return prepared.Substring(openParen + 1, i - openParen - 1);
                }
            }
            throw new InvalidOperationException("unbalanced parentheses at " + openParen);
        }

        /// <summary>Text of the <c>&lt;field&gt; = ...;</c> assignment for one shared
        /// style, so a cell can assert what that style is built out of.</summary>
        private static string AssignmentBlock(string prepared, string field)
        {
            int at = prepared.IndexOf(field + " = ", StringComparison.Ordinal);
            Assert.True(at > 0, "ParsekUI: no assignment for " + field);
            int end = prepared.IndexOf("};", at, StringComparison.Ordinal);
            Assert.True(end > at, "ParsekUI: unterminated assignment for " + field);
            return prepared.Substring(at, end - at);
        }

        private static string ReadPreparedSource(string relativePath)
        {
            string path = Path.Combine(ResolveRepoRoot(), "Source", "Parsek", relativePath);
            Assert.True(File.Exists(path),
                "source file moved, this gate is vacuous: " + path);
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
