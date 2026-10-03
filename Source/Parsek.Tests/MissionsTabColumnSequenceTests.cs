using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Missions tab's column header and every kind of row drawn under it must lay out
    /// the SAME columns, in the same order, with the same mode guards - in Basic and in
    /// Advanced alike. A column one side draws in a mode the other side skips moves every
    /// column right of it (a header over the wrong values), and nothing headless can call
    /// an IMGUI draw path, so the witness is a source scan of <c>UI/MissionsWindowUI.cs</c>
    /// with comments blanked and literals masked (<see cref="SourceScanText"/>).
    ///
    /// <para>The tab sits outside <see cref="TableRowInsetAlignmentTests"/>' table list
    /// (its rows use its own styles, GUI-MISSIONS-WINDOW-MERGED-FIRST-HEADER-CELL), so this
    /// gate is its own: width constants, the expanding name column and the helper-drawn
    /// columns in source order, each tagged <c>adv:</c> when it sits under a
    /// loop-authoring guard with no else branch (a column only Advanced draws). An if/else
    /// pair that draws the same width in both branches (the include checkbox or its blank
    /// cell) is one unconditional column.</para>
    /// </summary>
    public class MissionsTabColumnSequenceTests
    {
        private static readonly string File = Path.Combine("UI", "MissionsWindowUI.cs");

        private const string NameToken = "<name>";
        private const string AdvancedTag = "adv:";

        /// <summary>The row opener every Missions-tab table row starts with.</summary>
        private const string RowOpener =
            "GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight))";

        /// <summary>Helper calls that draw exactly one fixed column, by width.</summary>
        private static readonly Dictionary<string, string> CallWidths =
            new Dictionary<string, string>
            {
                { "DrawStartEventCell(", "ColW_StartEvent" },
                // The Interact column's cells: each draws exactly one ColW_Interact container.
                { "DrawInteractBlank(", "ColW_Interact" },
                { "DrawInteractReFly(", "ColW_Interact" },
                { "DrawInteractGoTo(", "ColW_Interact" },
                { "DrawInteractWatch(", "ColW_Interact" },
                { "DrawInteractRewindForward(", "ColW_Interact" },
                // The mission's collapse caret: one ColW_Index cell (line 2 of the # column).
                { "DrawMissionCollapseCaret(", "ColW_Index" },
            };

        private static readonly string[] GuardOpeners =
        {
            "if (loopAuthoring)",
            "if (ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode))",
        };

        private static readonly Regex WidthConstant =
            new Regex(@"\bColW_[A-Za-z0-9_]+\b", RegexOptions.Compiled);

        private static readonly Regex NameMarker = new Regex(
            @"DrawSortableHeaderCore\([^;]*?MissionSortColumn\.Name\b"
            + @"|DrawWideRowCell\("
            + @"|DrawMissionNameCell\("
            + @"|DrawMissionSummaryNameCell\("
            + @"|GUILayout\.ExpandWidth\(\s*true\s*\)",
            RegexOptions.Compiled);

        /// <summary>The row methods whose first table row must match the header. The
        /// mission bar's first line is one of them since Model 1 (2026-09-29): its values
        /// sit under the column headings.</summary>
        public static IEnumerable<object[]> RowMethods()
        {
            yield return new object[] { "DrawMissionValueRow" };
            yield return new object[] { "DrawMissionActionLine" };
            yield return new object[] { "DrawVesselRow" };
            yield return new object[] { "DrawCompositionRow" };
            yield return new object[] { "DrawChapterHeaderRow" };
            yield return new object[] { "DrawForeignDockLinkRows" };
            yield return new object[] { "DrawRecordedDockPartnerRow" };
        }

        [Theory]
        [MemberData(nameof(RowMethods))]
        public void EveryRowKindDrawsTheHeaderColumnSequenceInBothModes(string method)
        {
            string prepared = ReadPreparedSource();
            List<string> header = HeaderSequence(prepared);

            // Non-vacuous: the header carries the name column and the date columns.
            Assert.Contains(NameToken, header);
            Assert.Contains("ColW_StartTime", header);
            Assert.Contains("ColW_Interact", header);
            Assert.DoesNotContain("ColW_ReFly", header);
            Assert.DoesNotContain("ColW_Archive", header);

            string row = RowSpan(MethodBody(prepared, method), RowOpener, method);
            List<string> actual = ColumnSequence(prepared, row);
            Assert.True(header.SequenceEqual(actual),
                "MissionsWindowUI." + method + " draws a different column sequence than the "
                + "column header (a column drawn in one mode on one side only shifts every "
                + "column right of it). header=[" + string.Join(", ", header) + "] row=["
                + string.Join(", ", actual) + "]");
        }

        // catches: the Next launch column coming back on one side only. Missions Model 1
        // retired it (the countdown lives in the mission summary), so no width constant for it
        // may survive anywhere in the file.
        [Fact]
        public void TheNextLaunchColumnIsGone()
        {
            string prepared = ReadPreparedSource();
            Assert.DoesNotContain("ColW_TMinus", prepared);
            Assert.DoesNotContain("DrawTMinusVesselCell", prepared);
            Assert.DoesNotContain(AdvancedTag + "ColW_", string.Join(",", HeaderSequence(prepared)));
        }

        // The owner's Interact sizing rule: Watch, Rewind / Forward, Go to and Log share ONE
        // single-button width; the one two-button pair left (Fly / Stash + Seal) spans exactly
        // one single with the usual gap, so the column's button edges line up on every row; and
        // the column is that single plus its inset on both sides.
        [Fact]
        public void InteractButtonWidthsFormOneSystem()
        {
            Assert.Equal(MissionsWindowUI.InteractButtonWidth,
                2f * MissionsWindowUI.InteractPairButtonWidth + MissionsWindowUI.InteractButtonGap);
            Assert.Equal(MissionsWindowUI.ColW_Interact,
                MissionsWindowUI.InteractButtonWidth + 2f * MissionsWindowUI.InteractCellInset);

            // Every single-width button in the file names the one constant: Log, Go to,
            // Watch and Rewind / Forward, and nothing spells a literal Interact width.
            string prepared = ReadPreparedSource();
            string nameCell = MethodBody(prepared, "DrawMissionNameCell");
            Assert.Contains("GUILayout.Width(InteractButtonWidth)", nameCell);
            foreach (string method in new[] { "DrawInteractGoTo", "DrawInteractWatch",
                                              "DrawInteractRewindForward" })
            {
                string body = MethodBody(prepared, method);
                Assert.Contains("InteractButtonWidth", body);
                Assert.DoesNotContain("InteractPairButtonWidth", body);
            }
            // The Re-Fly pair is drawn by the Recordings tab's cell at the single width with
            // no inset of its own, which splits it into two pair halves.
            Assert.Contains("InteractButtonWidth, 0f", MethodBody(prepared, "DrawInteractReFly"));
        }

        // The mission bar's two lines (owner ruling 2026-10-03): the collapse caret is a clickable
        // glyph in the # column under the index number, not a framed button in the Interact
        // column, and Watch (line 1) and Rewind / Forward (line 2) stack as two singles at the
        // same x instead of sharing line 1 as a pair.
        [Fact]
        public void TheCollapseCaretSitsUnderTheIndexAndWatchStacksOverRewind()
        {
            string prepared = ReadPreparedSource();

            // Line 2: the caret is the # cell, after the blank enable slot and before the
            // summary's name cell; the blank index label it replaced is gone.
            string line2 = MethodBody(prepared, "DrawMissionActionLine");
            int enable = line2.IndexOf("GUILayout.Width(ColW_Enable)", StringComparison.Ordinal);
            int caret = line2.IndexOf("DrawMissionCollapseCaret(mission, collapsed)", StringComparison.Ordinal);
            int summary = line2.IndexOf("DrawMissionSummaryNameCell(", StringComparison.Ordinal);
            Assert.True(enable >= 0 && caret > enable && summary > caret,
                "the collapse caret must be line 2's # cell, between the enable slot and the summary");
            Assert.DoesNotContain("GUILayout.Width(ColW_Index)", line2);
            Assert.Contains("DrawInteractRewindForward(view)", line2);
            Assert.DoesNotContain("DrawInteractWatch(", line2);

            // Line 1: Watch alone in the Interact column, the index number in the # column.
            string line1 = MethodBody(prepared, "DrawMissionValueRow");
            Assert.Contains("DrawInteractWatch(mission, view)", line1);
            Assert.DoesNotContain("DrawInteractRewindForward(", line1);
            Assert.Contains("GUILayout.Width(ColW_Index)", line1);

            // The caret: a Button in the frameless label style, exactly the # column wide and at
            // least a row tall, whose glyph and hover follow the latched state through
            // MissionPresentation; the click flips Mission.Collapsed (the persisted flag).
            string caretBody = MethodBody(prepared, "DrawMissionCollapseCaret");
            Assert.Contains("GUILayout.Button(content, missionCaretStyle", caretBody);
            Assert.Contains("GUILayout.Width(ColW_Index)", caretBody);
            Assert.Contains("GUILayout.MinHeight(CompositionRowMinHeight)", caretBody);
            Assert.Contains("collapsed ? MissionCaretCollapsedContent : MissionCaretExpandedContent", caretBody);
            Assert.Contains("mission.Collapsed = nowCollapsed", caretBody);
            Assert.DoesNotContain("GUILayout.Toggle", caretBody);
            Assert.Matches(@"missionCaretStyle\s*=\s*new GUIStyle\(GUI\.skin\.label\)", prepared);
            Assert.Matches(@"MissionCaretCollapsedContent\s*=\s*new GUIContent\(\s*"
                + @"MissionPresentation\.MissionCollapseCaretGlyph\(true\),\s*"
                + @"MissionPresentation\.MissionCollapseCaretTooltip\(true\)\)", prepared);
            Assert.Matches(@"MissionCaretExpandedContent\s*=\s*new GUIContent\(\s*"
                + @"MissionPresentation\.MissionCollapseCaretGlyph\(false\),\s*"
                + @"MissionPresentation\.MissionCollapseCaretTooltip\(false\)\)", prepared);

            // The hit area: the # column is at least 20 px wide.
            Match indexWidth = Regex.Match(prepared, @"\bColW_Index\s*=\s*([0-9.]+)f\s*;");
            Assert.True(indexWidth.Success, "ColW_Index is never declared, this gate is vacuous.");
            Assert.True(float.Parse(indexWidth.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture) >= 20f,
                "the collapse caret's hit area (ColW_Index) must be at least 20 px wide");

            // The old framed Collapse / Expand button is gone from the Interact column, and line
            // 2's Interact cell reserves a button-sized rect when neither Rewind nor Forward
            // applies, so the line keeps its height in every state.
            Assert.DoesNotContain("DrawInteractCollapse", prepared);
            Assert.DoesNotContain("DrawInteractWatchRewind", prepared);
            Assert.DoesNotContain("interactToggleButtonStyle", prepared);
            string rewind = MethodBody(prepared, "DrawInteractRewindForward");
            Assert.Contains("MissionRewindForwardVisible(", rewind);
            Assert.Contains("GUILayoutUtility.GetRect(InteractReservedSlotContent, interactButtonStyle", rewind);
        }

        // The Advanced loop grid (owner mock-up 2026-09-30, aligned 2026-10-01): Clone / Delete
        // share column A, "Warp to..." fills column B and is ALWAYS drawn (greyed when it cannot
        // act), Log follows it, and the line-2 loop row spans column B plus the Log slot, so its
        // left edge is Warp's left edge and its right edge is Log's right edge. Inside the row,
        // "Loop [x] every" fills column B under Warp and the period fills the Log slot under Log.
        [Fact]
        public void TheLoopGridColumnsAreFixedAndLineUpAcrossBothLines()
        {
            // Both lines are right-aligned in the same name cell, so with every grid control at
            // zero horizontal margin their right edges coincide at the cell's right edge R.
            const float R = 1000f;
            float logRight = R;
            float logLeft = logRight - MissionsWindowUI.InteractButtonWidth;
            float warpRight = logLeft;
            float warpLeft = warpRight - MissionsWindowUI.LoopGridColumnBWidth;
            float rowRight = R;
            float rowLeft = rowRight - MissionsWindowUI.LoopCellWidth;
            Assert.Equal(warpLeft, rowLeft);
            Assert.Equal(logRight, rowRight);
            // Column B of the row ("Loop", the checkbox slot, "every") ends where Warp ends, so
            // the period starts at Log's left edge and ends at Log's right edge.
            float everyRight = rowLeft + MissionsWindowUI.LoopRowLabelWidth
                + MissionsWindowUI.LoopRowToggleSlotWidth + MissionsWindowUI.LoopRowEveryWidth;
            Assert.Equal(warpRight, everyRight);
            float periodRight = everyRight + MissionsWindowUI.LoopPeriodValueWidth
                + MissionsWindowUI.LoopPeriodGap + MissionsWindowUI.LoopPeriodUnitButtonWidth;
            Assert.Equal(logRight, periodRight);

            // The widths themselves (a change here moves the grid against the summary).
            Assert.Equal(92f, MissionsWindowUI.LoopGridColumnBWidth);
            Assert.Equal(192f, MissionsWindowUI.LoopCellWidth);
            Assert.Equal(28f, MissionsWindowUI.LoopRowLabelWidth);
            Assert.Equal(22f, MissionsWindowUI.LoopRowToggleSlotWidth);
            Assert.Equal(42f, MissionsWindowUI.LoopRowEveryWidth);
            Assert.Equal(56f, MissionsWindowUI.LoopPeriodValueWidth);
            Assert.Equal(40f, MissionsWindowUI.LoopPeriodUnitButtonWidth);

            string prepared = ReadPreparedSource();
            string line1 = MethodBody(prepared, "DrawMissionNameCell");
            string line2 = MethodBody(prepared, "DrawMissionSummaryNameCell");
            Assert.Contains("GUILayout.Width(LoopGridColumnAWidth)", line1);   // Clone
            Assert.Contains("GUILayout.Width(InteractButtonWidth)", line1);    // Log
            // Warp is drawn in every Advanced state: no loop-state branch and no blank slot.
            Assert.Contains("DrawMissionWarpToWindowButton(", line1);
            Assert.DoesNotContain("GUILayout.Space(LoopGridColumnBWidth)", line1);
            Assert.DoesNotContain("LoopPlayback", line1);
            Assert.Contains("GUILayout.Width(LoopGridColumnBWidth)",
                MethodBody(prepared, "DrawMissionWarpToWindowButton"));
            Assert.Contains("GUILayout.Width(LoopGridColumnAWidth)", line2);   // Delete
            Assert.Contains("DrawMissionLoopRow(", line2);

            string row = MethodBody(prepared, "DrawMissionLoopRow");
            Assert.Contains("GUILayout.Width(LoopCellWidth)", row);
            Assert.Contains("DrawLoopedByRouteLabel(bindingRoute, LoopCellWidth)", row);
            string toggle = MethodBody(prepared, "DrawMissionLoopToggle");
            Assert.Contains("GUILayout.Width(LoopRowLabelWidth)", toggle);
            Assert.Contains("BeginLoopCellSlot(LoopRowToggleSlotWidth)", toggle);
            Assert.Contains("GUILayout.Width(LoopRowEveryWidth)", toggle);
            string period = MethodBody(prepared, "DrawMissionLoopPeriodCell");
            Assert.Contains("= LoopPeriodValueWidth", period);
            Assert.Contains("= LoopPeriodUnitButtonWidth", period);
            Assert.Contains("GUILayout.Space(LoopPeriodGap)", period);
            Assert.Contains("GUILayout.Width(InteractButtonWidth)", period);  // locked value
            // A content-sized control would make the row's width depend on its text.
            Assert.DoesNotContain("ExpandWidth(false)", period);

            // Every control style in the row has zero horizontal margin, so the fixed widths
            // above are the controls' exact rects (a skin margin would push the period past
            // Log's right edge).
            foreach (string style in new[] { "loopCellRowStyle", "loopCellLabelStyle",
                                             "loopCellToggleStyle", "loopCellFieldStyle",
                                             "loopCellUnitButtonStyle", "loopGridButtonStyle",
                                             "interactButtonStyle" })
                AssertZeroHorizontalMarginStyle(prepared, style);
        }

        // catches: the loop row's state (and so the transition log) disagreeing with the
        // period cell's own branch - the locked value draws only while the mission loops.
        [Theory]
        // (routeBound, looping, lockedOrReaim, autoUnit, expected)
        [InlineData(true, true, true, true, (int)MissionsWindowUI.LoopRowKind.RouteBound)]
        [InlineData(true, false, false, false, (int)MissionsWindowUI.LoopRowKind.RouteBound)]
        [InlineData(false, true, true, false, (int)MissionsWindowUI.LoopRowKind.LockedPeriod)]
        [InlineData(false, true, true, true, (int)MissionsWindowUI.LoopRowKind.LockedPeriod)]
        [InlineData(false, false, true, false, (int)MissionsWindowUI.LoopRowKind.ManualPeriod)]
        [InlineData(false, false, true, true, (int)MissionsWindowUI.LoopRowKind.AutoPeriod)]
        [InlineData(false, true, false, true, (int)MissionsWindowUI.LoopRowKind.AutoPeriod)]
        [InlineData(false, true, false, false, (int)MissionsWindowUI.LoopRowKind.ManualPeriod)]
        public void TheLoopRowStateFollowsThePeriodCellBranch(
            bool routeBound, bool looping, bool lockedOrReaim, bool autoUnit,
            int expected)
        {
            Assert.Equal((MissionsWindowUI.LoopRowKind)expected,
                MissionsWindowUI.ResolveLoopRowKind(routeBound, looping, lockedOrReaim, autoUnit));
        }

        private static void AssertZeroHorizontalMarginStyle(string prepared, string style)
        {
            Match m = Regex.Match(prepared, @"\b" + Regex.Escape(style) + @"\s*=\s*new GUIStyle\(([^)]*)\)");
            Assert.True(m.Success, File + ": style " + style + " is never built, this gate is vacuous.");
            string ctorArg = m.Groups[1].Value.Trim();
            int end = prepared.IndexOf(';', m.Index);
            string init = prepared.Substring(m.Index, end - m.Index);
            if (Regex.IsMatch(init, @"margin\s*=\s*new RectOffset\(\s*0\s*,\s*0\s*,"))
                return;
            // A copy of a style this gate already holds to zero margin keeps it.
            Assert.True(!init.Contains("margin") && (ctorArg == "interactButtonStyle"),
                File + ": " + style + " must zero its left / right margin");
        }

        // The mutation this gate exists for, run against a synthetic pair so the cell above
        // cannot pass by reading nothing: a row that draws a column unconditionally under a
        // header that draws it only in Advanced must read as a different sequence.
        [Fact]
        public void AnUnguardedRowCellUnderAnAdvancedOnlyHeaderReadsAsDifferent()
        {
            string header =
                "{ if (ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode))\n"
                + "    GUILayout.Label(\"\", GUILayout.Width(ColW_TMinus));\n"
                + "  GUILayout.Label(\"\", GUILayout.Width(ColW_StartTime)); }";
            string row =
                "{ GUILayout.Label(\"\", GUILayout.Width(ColW_TMinus));\n"
                + "  GUILayout.Label(\"\", GUILayout.Width(ColW_StartTime)); }";
            string rowGuarded =
                "{ if (loopAuthoring)\n"
                + "    GUILayout.Label(\"\", GUILayout.Width(ColW_TMinus));\n"
                + "  GUILayout.Label(\"\", GUILayout.Width(ColW_StartTime)); }";
            string rowIfElse =
                "{ if (loopAuthoring)\n"
                + "  {\n"
                + "    GUILayout.Label(\"\", GUILayout.Width(ColW_TMinus));\n"
                + "  }\n"
                + "  else\n"
                + "  {\n"
                + "    GUILayout.Label(\"\", GUILayout.Width(ColW_TMinus));\n"
                + "  }\n"
                + "  GUILayout.Label(\"\", GUILayout.Width(ColW_StartTime)); }";

            List<string> h = ColumnSequence(header, header);
            Assert.Equal(new[] { "adv:ColW_TMinus", "ColW_StartTime" }, h);
            Assert.False(h.SequenceEqual(ColumnSequence(row, row)));
            Assert.True(h.SequenceEqual(ColumnSequence(rowGuarded, rowGuarded)));
            Assert.Equal(new[] { "ColW_TMinus", "ColW_StartTime" }, ColumnSequence(rowIfElse, rowIfElse));
        }

        // ------------------------------------------------------------------ helpers

        private static List<string> HeaderSequence(string prepared)
        {
            string header = MethodBody(prepared, "DrawColumnHeader");
            // The merged [enable + index] container's own width names both constants; the
            // two cells inside it are what line up with a row's two leading cells.
            header = Regex.Replace(header,
                @"GUILayout\.Width\(\s*ColW_Enable\s*\+\s*ColW_Index\s*\+\s*8f\s*\)", "");
            return ColumnSequence(prepared, header);
        }

        /// <summary>
        /// The ordered column sequence of a span: width constants, the name column and the
        /// helper-drawn columns, runs of one token collapsed (a cell drawn by one of several
        /// exclusive branches names its width once per branch). A helper method named in
        /// <paramref name="prepared"/> whose body draws several blank cells
        /// (<c>DrawBlankDataCells</c>) is expanded in place.
        /// </summary>
        private static List<string> ColumnSequence(string prepared, string span)
        {
            var guards = new List<Tuple<int, int>>();
            foreach (string opener in GuardOpeners)
                AddUnpairedGuardSpans(span, opener, guards);

            // An else block that draws the same columns as its if block is the other branch
            // of ONE column (a checkbox or its blank cell, a Fly cell or its blank cell), so
            // its tokens are dropped rather than read as a second copy of the column. An
            // else that draws something different is kept, and reads as a mismatch.
            List<Tuple<int, int>> dropped = EquivalentElseBlocks(prepared, span);

            var hits = new List<Tuple<int, string>>();
            foreach (Match m in WidthConstant.Matches(span))
                hits.Add(Tuple.Create(m.Index, m.Value));
            foreach (Match m in NameMarker.Matches(span))
                hits.Add(Tuple.Create(m.Index, NameToken));
            foreach (var kv in CallWidths)
            {
                int at = 0;
                while ((at = span.IndexOf(kv.Key, at, StringComparison.Ordinal)) >= 0)
                {
                    hits.Add(Tuple.Create(at, kv.Value));
                    at += kv.Key.Length;
                }
            }
            hits = hits.Where(h => !dropped.Any(d => h.Item1 > d.Item1 && h.Item1 < d.Item2))
                .ToList();

            var tokens = new List<string>();
            foreach (var hit in hits.OrderBy(h => h.Item1))
            {
                bool advanced = guards.Any(g => hit.Item1 > g.Item1 && hit.Item1 < g.Item2);
                tokens.Add((advanced ? AdvancedTag : "") + hit.Item2);
            }

            // Expand the blank-cells helper where a row calls it.
            int helperAt = span.IndexOf(BlankCellsHelper + "(", StringComparison.Ordinal);
            if (helperAt >= 0 && prepared.Contains("void " + BlankCellsHelper + "("))
            {
                List<string> helper = ColumnSequence(prepared,
                    MethodBody(prepared, BlankCellsHelper));
                int insertAt = hits.Count(h => h.Item1 < helperAt);
                tokens.InsertRange(insertAt, helper);
            }

            var collapsed = new List<string>();
            foreach (string t in tokens)
                if (collapsed.Count == 0 || collapsed[collapsed.Count - 1] != t)
                    collapsed.Add(t);
            return collapsed;
        }

        /// <summary>The helper that draws a row's run of blank data cells.</summary>
        private const string BlankCellsHelper = "DrawBlankDataCells";

        /// <summary>
        /// The ranges of every braced <c>else { ... }</c> whose column sequence equals that of
        /// the braced <c>if (...) { ... }</c> block right before it.
        /// </summary>
        private static List<Tuple<int, int>> EquivalentElseBlocks(string prepared, string span)
        {
            var result = new List<Tuple<int, int>>();
            var elseOpen = new Regex(@"\}\s*else\s*\{");
            foreach (Match m in elseOpen.Matches(span))
            {
                int ifClose = m.Index;
                int ifOpen = MatchingOpen(span, ifClose);
                int elseStart = span.IndexOf('{', m.Index + 1);
                int elseEnd = MatchingClose(span, elseStart);
                if (ifOpen < 0 || elseEnd < 0)
                    continue;
                string ifBlock = span.Substring(ifOpen + 1, ifClose - ifOpen - 1);
                string elseBlock = span.Substring(elseStart + 1, elseEnd - elseStart - 1);
                if (ColumnSequence(prepared, ifBlock).SequenceEqual(
                        ColumnSequence(prepared, elseBlock)))
                    result.Add(Tuple.Create(elseStart, elseEnd));
            }
            return result;
        }

        private static int MatchingOpen(string s, int close)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (s[i] == '}') depth++;
                else if (s[i] == '{' && --depth == 0) return i;
            }
            return -1;
        }

        private static int MatchingClose(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Every <paramref name="guard"/> in the span with NO else branch, as the
        /// range its body covers: a braced block, or the single statement to its
        /// semicolon.</summary>
        private static void AddUnpairedGuardSpans(string span, string guard,
            List<Tuple<int, int>> into)
        {
            int at = 0;
            while ((at = span.IndexOf(guard, at, StringComparison.Ordinal)) >= 0)
            {
                int bodyStart = at + guard.Length;
                while (bodyStart < span.Length && char.IsWhiteSpace(span[bodyStart])) bodyStart++;
                int bodyEnd;
                if (bodyStart < span.Length && span[bodyStart] == '{')
                {
                    int depth = 0;
                    bodyEnd = span.Length;
                    for (int i = bodyStart; i < span.Length; i++)
                    {
                        if (span[i] == '{') depth++;
                        else if (span[i] == '}' && --depth == 0) { bodyEnd = i + 1; break; }
                    }
                }
                else
                {
                    bodyEnd = span.IndexOf(';', bodyStart);
                    bodyEnd = bodyEnd < 0 ? span.Length : bodyEnd + 1;
                }

                int next = bodyEnd;
                while (next < span.Length && char.IsWhiteSpace(span[next])) next++;
                bool hasElse = string.CompareOrdinal(span, next, "else", 0, 4) == 0;
                if (!hasElse)
                    into.Add(Tuple.Create(at, bodyEnd));
                at = bodyEnd;
            }
        }

        private static string RowSpan(string body, string opener, string where)
        {
            int start = body.IndexOf(opener, StringComparison.Ordinal);
            Assert.True(start >= 0, where + ": row opener not found, this gate is vacuous.");
            var marks = new Regex(@"GUILayout\.(Begin|End)Horizontal\(");
            int depth = 0;
            foreach (Match m in marks.Matches(body, start))
            {
                depth += m.Groups[1].Value == "Begin" ? 1 : -1;
                if (depth == 0)
                    return body.Substring(start, m.Index - start);
            }
            throw new InvalidOperationException(where + ": unbalanced horizontal groups");
        }

        private static string MethodBody(string prepared, string method)
        {
            var sig = new Regex(@"\b" + Regex.Escape(method) + @"\s*\([^;{}]*\)\s*\{",
                RegexOptions.Singleline);
            Match m = sig.Match(prepared);
            Assert.True(m.Success, File + ": method " + method + " not found, this gate is vacuous.");
            int open = prepared.IndexOf('{', m.Index);
            int depth = 0;
            for (int i = open; i < prepared.Length; i++)
            {
                if (prepared[i] == '{') depth++;
                else if (prepared[i] == '}' && --depth == 0)
                    return prepared.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException(File + ": unbalanced braces walking " + method);
        }

        private static string ReadPreparedSource()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    string path = Path.Combine(dir, "Source", "Parsek", File);
                    Assert.True(System.IO.File.Exists(path), "source file moved: " + path);
                    return SourceScanText.StripCommentsAndMaskLiterals(
                        System.IO.File.ReadAllText(path));
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }
    }
}
