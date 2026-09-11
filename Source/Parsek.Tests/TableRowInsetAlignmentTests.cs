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
        }

        private static readonly TableSite[] Tables =
        {
            new TableSite
            {
                File = Path.Combine("UI", "SpawnControlUI.cs"),
                HeaderMethod = "DrawSpawnControlWindow",
                RowMethod = "DrawSpawnCandidateRows",
                HeaderPinnedOutsideScrollView = true,
            },
            new TableSite
            {
                File = Path.Combine("UI", "StructureListWindowUI.cs"),
                HeaderMethod = "DrawWindow",
                RowMethod = "DrawWindow",
                HeaderPinnedOutsideScrollView = true,
            },
            new TableSite
            {
                File = Path.Combine("UI", "CareerStateWindowUI.cs"),
                HeaderMethod = "DrawContractsColumnHeader",
                RowMethod = "DrawContractRow",
                HeaderPinnedOutsideScrollView = false,
            },
            new TableSite
            {
                File = Path.Combine("UI", "CareerStateWindowUI.cs"),
                HeaderMethod = "DrawStrategiesColumnHeader",
                RowMethod = "DrawStrategyRow",
                HeaderPinnedOutsideScrollView = false,
            },
            new TableSite
            {
                File = Path.Combine("UI", "CareerStateWindowUI.cs"),
                HeaderMethod = "DrawFacilitiesColumnHeader",
                RowMethod = "DrawFacilityRow",
                HeaderPinnedOutsideScrollView = false,
            },
            new TableSite
            {
                File = Path.Combine("UI", "CareerStateWindowUI.cs"),
                HeaderMethod = "DrawMilestonesColumnHeader",
                RowMethod = "DrawMilestoneRow",
                HeaderPinnedOutsideScrollView = false,
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
                string row = t.RowMethod == t.HeaderMethod
                    ? header
                    : MethodBody(prepared, t.RowMethod, t.File);

                // A single-method table (the Structure window draws header AND rows in
                // one method) is split at its BeginScrollView so the two halves can be
                // compared; every other table has one method per half.
                if (t.RowMethod == t.HeaderMethod)
                {
                    int cut = header.IndexOf("BeginScrollView", StringComparison.Ordinal);
                    Assert.True(cut > 0,
                        t.File + ": " + t.HeaderMethod
                        + " draws header and rows in one method but has no BeginScrollView "
                        + "to split them at - this cell can no longer tell the halves apart.");
                    row = header.Substring(cut);
                    header = header.Substring(0, cut);
                }

                List<string> headerWidths = OrderedWidthConstants(header);
                List<string> rowWidths = OrderedWidthConstants(row);

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
                string row = t.RowMethod == t.HeaderMethod
                    ? header
                    : MethodBody(prepared, t.RowMethod, t.File);

                if (t.RowMethod == t.HeaderMethod)
                {
                    int cut = header.IndexOf("BeginScrollView", StringComparison.Ordinal);
                    row = header.Substring(cut);
                    header = header.Substring(0, cut);
                }

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
                         Path.Combine("UI", "CareerStateWindowUI.cs"),
                     })
            {
                string prepared = ReadPreparedSource(file);
                Assert.DoesNotContain("GUILayout.BeginVertical(GUI.skin.box)", prepared);
                Assert.Contains("GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle())", prepared);
            }
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

        // ───────────────────────────── helpers ─────────────────────────────

        /// <summary>
        /// The ordered fixed-column layout a method declares. Runs of the SAME constant
        /// collapse to one entry: a cell drawn in either of two mutually exclusive
        /// branches (Real Spawn Control's State column, tinted or not) spells its width
        /// twice for one column. The cost is that two genuinely adjacent columns sharing
        /// one width constant read as a single column here - which still catches a width
        /// changing on one side, just not a duplicated column.
        /// </summary>
        private static List<string> OrderedWidthConstants(string span)
        {
            var all = WidthConstant.Matches(span).Cast<Match>().Select(m => m.Value);
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
