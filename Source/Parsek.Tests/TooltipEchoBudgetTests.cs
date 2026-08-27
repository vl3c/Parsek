using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the shared hover-help strip's TEXT budget.
    ///
    /// <para><b>Why a budget exists.</b> <see cref="TooltipEchoBox"/> is a permanently
    /// visible box of exactly ONE or TWO wrapped lines (per-window choice): text beyond
    /// that clips, and only the strip's overflow marquee reveals it again over time -
    /// so a sentence that fits its window's budget reads instantly, no scrolling.
    /// The strip spans the window's full content width, so how much text fits is a
    /// property of the WINDOW, not of the control - a sentence that fits the 1556 px
    /// Logistics window clips in the 280 px Settings window.</para>
    ///
    /// <para><b>How the per-file budget is derived.</b> For a window whose first-open
    /// width is <c>W</c> and whose strip is <c>L</c> lines tall: subtract ~20 px of
    /// window chrome (the skin window style's left+right padding) and ~10 px of box
    /// padding, then divide by a deliberately pessimistic ~7 px average character
    /// advance for KSP's default IMGUI label font (~13 px; the real average is nearer
    /// 6.2, so the budget has headroom), and allow L lines:
    /// <c>budget = floor(L * (W - 30) / 7)</c>. Word wrap can only make a line end
    /// EARLY, so this is an upper bound on what L lines hold, which is exactly the
    /// direction a guard wants.</para>
    ///
    /// <para><b>Which windows are single-line.</b> The wide windows whose ENTIRE help
    /// corpus fits one wrapped line at their first-open width - Career State, Timeline,
    /// Logistics, Real Spawn Control and Recordings (with the Missions tab it hosts) -
    /// construct their <see cref="TooltipEchoBox"/> with
    /// <see cref="TooltipEchoBox.SingleLine"/> and are budgeted at L=1 below; every other
    /// window keeps the two-line strip. A copy edit that pushes any literal past ITS OWN
    /// window's line count fails here, and a switch of a window's strip height without
    /// re-budgeting its texts fails too.</para>
    ///
    /// <para><b>What this gate covers.</b> Every <c>new GUIContent(label, tooltip)</c>
    /// in the strip-hosting windows whose tooltip argument is a plain string
    /// literal (or a concatenation of them), or a bare identifier naming a same-file
    /// <c>const string</c> whose initializer is such a literal run (the shape long
    /// tooltips use to stay off the draw path, e.g. Logistics'
    /// <c>DormantSectionTooltip</c>) - the population a copy edit can actually
    /// regress. Tooltips assembled at runtime from data (recording names, hold
    /// reasons, in-game test descriptions) cannot be pinned by a source scan; the
    /// pure builders that feed the longest of them are asserted directly below
    /// instead. A parser regression that silently matched nothing would make this
    /// gate vacuous, so each file also pins a minimum literal-tooltip count.
    /// The per-row strip-height column is itself source-checked: each row's declared
    /// line count must match the <c>new TooltipEchoBox(...)</c> argument in the
    /// window it budgets (<c>StripHeightColumn_MatchesTheWindowConstructor</c>).</para>
    ///
    /// <para>Hard newlines are rejected outright: an explicit <c>\n</c> spends one of
    /// the strip's lines regardless of how short the text is, so a two-clause tooltip
    /// with a break in it clips as soon as either clause wraps.</para>
    /// </summary>
    public class TooltipEchoBudgetTests
    {
        /// <summary>Window chrome (skin window padding) plus the strip box's own padding, in px.</summary>
        private const float StripPaddingPx = 30f;

        /// <summary>Pessimistic average character advance for the default IMGUI label font, in px.</summary>
        private const float AvgCharWidthPx = 7f;

        /// <summary>
        /// The strip-hosting windows: source path, the window's first-open width in
        /// px, the minimum number of literal GUIContent tooltips the scan must find
        /// in that file (a floor, not a target - raise it only if the parser is
        /// proven), and the strip height that window constructs (1 or 2 lines).
        ///
        /// <para>The narrowest entry is the main window at 250 px, which is also the mod's
        /// entry point: every launcher in it must say what its window is for in about 62
        /// characters. Width is the WINDOW's, never the control's - the strip spans the
        /// content width, so a sentence that fits Logistics clips in the main window.</para>
        /// </summary>
        public static IEnumerable<object[]> StripWindows()
        {
            // Main window: both hosts (ParsekFlight, ParsekKSC) pin GUILayout.Width(250).
            yield return new object[] { "ParsekUI.cs", 250f, 8, TooltipEchoBox.DoubleLine };
            // Settings: DrawIfOpen seeds new Rect(..., 280, 600) on first open. The floor
            // dropped 15 -> 10 with the 2026-08-27 settings simplification (the Recording,
            // Stock UI, auto-backup, landing-body-alignment and force-faithful controls
            // and their tooltips were retired).
            yield return new object[] { "UI/SettingsWindowUI.cs", 280f, 10, TooltipEchoBox.DoubleLine };
            // Gloops Flight Recorder: first-open DefaultWindowWidth = 280.
            yield return new object[] { "UI/GloopsRecorderUI.cs", 280f, 3, TooltipEchoBox.DoubleLine };
            // Kerbals: DefaultWindowWidth = 410 (half of Career's 820, side by side).
            yield return new object[] { "UI/KerbalsWindowUI.cs", 410f, 5, TooltipEchoBox.DoubleLine };
            // Career State: DefaultWindowWidth = 820. Single-line strip: longest help
            // text is 76 chars against a 112-char one-line budget.
            yield return new object[] { "UI/CareerStateWindowUI.cs", 820f, 14, TooltipEchoBox.SingleLine };
            // Timeline: DefaultWindowWidth = CareerStateWindowUI.DefaultWindowWidth (820).
            // Single-line strip: longest literal is 93 chars; watch-button tooltips cap
            // at 77 ("No active ghost - recording is in the past/future ...").
            yield return new object[] { "UI/TimelineWindowUI.cs", 820f, 14, TooltipEchoBox.SingleLine };
            // Recordings: DefaultCollapsedWindowWidth = 1205 + ColW_Rewind(60) + ColW_ReFly(90).
            // Single-line strip: the window's whole help corpus was trimmed to fit one
            // wrapped line at 1355px = 189 chars (the loop-period header tooltip, formerly
            // 204, is the text that used to need the second line).
            yield return new object[] { "UI/RecordingsTableUI.cs", 1355f, 40, TooltipEchoBox.SingleLine };
            // Missions is not its own window: MissionsWindowUI.DrawMissionsTabContent draws
            // INSIDE the Recordings window, so its tooltips echo in that window's strip and
            // are budgeted at that window's width and height - including its SINGLE-line
            // height. Floor 0 on purpose - every tooltip it renders today comes from
            // MissionPresentation (a const or a runtime build), so the scan finds none; the
            // row exists to budget the first literal added here.
            yield return new object[] { "UI/MissionsWindowUI.cs", 1355f, 0, TooltipEchoBox.SingleLine };
            // Logistics: first-open default new Rect(..., 1556, 500). Single-line strip:
            // the composed Supply-Run cost tooltip was rewritten constant-length (no
            // embedded funds amounts), the Nx cadence tooltip and the status-cell hold
            // tooltip were trimmed to one line; all pinned by the runtime-builder gates.
            yield return new object[] { "UI/LogisticsWindowUI.cs", 1556f, 20, TooltipEchoBox.SingleLine };
            // Real Spawn Control: first-open default new Rect(..., 750, 200). Single-line
            // strip: one tooltipped control whose fixed text is short; long runtime
            // vessel names overflow into the marquee.
            yield return new object[] { "UI/SpawnControlUI.cs", 750f, 0, TooltipEchoBox.SingleLine };
            // Test runner (Settings-launched) and the global Ctrl+Shift+T twin: 440.
            yield return new object[] { "UI/TestRunnerUI.cs", 440f, 3, TooltipEchoBox.DoubleLine };
            yield return new object[] { "InGameTests/TestRunnerShortcut.cs", 440f, 2, TooltipEchoBox.DoubleLine };
        }

        internal static int BudgetChars(float windowWidthPx, int stripLines)
        {
            return (int)(stripLines * (windowWidthPx - StripPaddingPx) / AvgCharWidthPx);
        }

        // catches: a hover help text growing past what its own window's strip holds
        // (one or two wrapped lines per the row above), which the strip swallows
        // silently instead of reporting - and a host switching its strip height
        // without re-budgeting its texts.
        [Theory]
        [MemberData(nameof(StripWindows))]
        public void LiteralGuiContentTooltips_FitTheHelpStrip(
            string relPath, float windowWidthPx, int minTooltips, int stripLines)
        {
            int budget = BudgetChars(windowWidthPx, stripLines);
            string src = ReadParsekSource(relPath);

            int found = 0;
            foreach (var tip in ExtractLiteralGuiContentTooltips(src))
            {
                found++;
                Assert.False(tip.Text.Contains("\n"),
                    string.Format(
                        "{0} line {1}: tooltip contains a hard newline. The help strip is "
                        + "{2} wrapped line(s) tall - a \\n spends one of them on whatever "
                        + "sits before it and clips the rest. Tooltip: \"{3}\"",
                        relPath, tip.Line, stripLines, tip.Text));
                Assert.True(tip.Text.Length <= budget,
                    string.Format(
                        "{0} line {1}: tooltip is {2} chars but the {3}px-wide window holds "
                        + "about {4} in its {5}-line help strip - the tail would scroll in "
                        + "via the marquee instead of reading at a glance. Shorten it "
                        + "(keep what the control does plus the key consequence). "
                        + "Tooltip: \"{6}\"",
                        relPath, tip.Line, tip.Text.Length, windowWidthPx, budget,
                        stripLines, tip.Text));
            }

            Assert.True(found >= minTooltips,
                string.Format(
                    "{0}: found only {1} literal GUIContent tooltips, expected at least {2}. "
                    + "Either the tooltips moved out of the file or the scanner stopped matching - "
                    + "a vacuous budget gate is worse than none.",
                    relPath, found, minTooltips));
        }

        // catches: the sampling-density helper (fed into the Settings window's density
        // buttons through a method call, so the source scan above cannot see it)
        // growing past the Settings strip.
        [Fact]
        public void DensityTooltips_FitTheSettingsStrip()
        {
            int budget = BudgetChars(280f, TooltipEchoBox.DoubleLine);
            foreach (SamplingDensity level in
                new[] { SamplingDensity.Low, SamplingDensity.Medium, SamplingDensity.High })
            {
                string tip = ParsekSettings.DensityTooltip(level);
                Assert.DoesNotContain("\n", tip);
                Assert.True(tip.Length <= budget,
                    string.Format(
                        "ParsekSettings.DensityTooltip({0}) is {1} chars, over the {2}-char "
                        + "Settings help-strip budget. Tooltip: \"{3}\"",
                        level, tip.Length, budget, tip));
            }
        }

        // catches: the Supply-Run cost explanation growing past the Logistics strip.
        // The tooltip is CONSTANT LENGTH by contract - it embeds no funds amounts
        // (those live in the visible "Cost/run:" detail line and the candidate cell
        // suffix), so one worst-case shape pins every route.
        [Fact]
        public void RouteRunCostTooltip_FitsTheLogisticsStrip()
        {
            int budget = BudgetChars(1556f, TooltipEchoBox.SingleLine);
            var noRecovery = new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = true,
                CostKnown = true,
                LaunchCost = 1234567.0,
                RecoveredCredits = 0.0,
                NetCost = 1234567.0,
                RecoveryEventCount = 0
            };

            string tip = LogisticsCostPresentation.FormatDetailTooltip(noRecovery);
            Assert.DoesNotContain("\n", tip);
            Assert.True(tip.Length <= budget,
                string.Format("FormatDetailTooltip is {0} chars, over the {1}-char single-line "
                    + "Logistics help-strip budget: \"{2}\"",
                    tip.Length, budget, tip));
        }

        // catches: any of the Logistics window's runtime-composed tooltips growing past
        // its SINGLE-line strip (the source scan cannot see these - they are builders,
        // not literals). The Nx cadence tooltip is the longest composed text in the
        // window; the status-cell hold tooltip must stay one hard-newline-free line.
        [Fact]
        public void RuntimeComposedLogisticsTooltips_FitTheSingleLineStrip()
        {
            int budget = BudgetChars(1556f, TooltipEchoBox.SingleLine);

            string flatNx = LogisticsIntervalPresentation.BuildNxCellTooltip(
                windowedBasis: false, multiplier: 4, basisLabel: null, formattedTransit: "123.4h");
            string windowedNx = LogisticsIntervalPresentation.BuildNxCellTooltip(
                windowedBasis: true, multiplier: 3, basisLabel: "Kerbin-Mun synodic", formattedTransit: "12.5d");
            Assert.DoesNotContain("\n", flatNx);
            Assert.DoesNotContain("\n", windowedNx);
            Assert.True(flatNx.Length <= budget,
                string.Format("BuildNxCellTooltip(flat) is {0} chars, over the {1}-char "
                    + "single-line Logistics help-strip budget: \"{2}\"",
                    flatNx.Length, budget, flatNx));
            Assert.True(windowedNx.Length <= budget,
                string.Format("BuildNxCellTooltip(windowed) is {0} chars, over the {1}-char "
                    + "single-line Logistics help-strip budget: \"{2}\"",
                    windowedNx.Length, budget, windowedNx));

            // Status-cell hold tooltip, fed REAL DescribeHold productions - a
            // hand-typed clause can drift from what the builder actually emits (it
            // once omitted the raw token DescribeHold appends in parentheses). The
            // unresolved-pickup-source family is the longest FIXED wording and must
            // fit the one-line budget outright.
            string unresolvedClause = LogisticsHoldPresentation.DescribeHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "pickup-source-unresolved:origin", 0.0);
            Assert.Contains("could not be found", unresolvedClause);
            string statusTip = LogisticsHoldPresentation.StatusCellTooltip(
                RouteStatus.Active, unresolvedClause);
            Assert.DoesNotContain("\n", statusTip);
            Assert.True(statusTip.Length <= budget,
                string.Format("StatusCellTooltip with the unresolved-source clause is {0} "
                    + "chars, over the {1}-char single-line Logistics help-strip budget: \"{2}\"",
                    statusTip.Length, budget, statusTip));

            // Hold clauses that interpolate USER-CHOSEN names (source vessel,
            // reserving route) are unbounded - no fixed budget can pin them, and the
            // strip's overflow marquee is the recovery path for that long tail. What
            // MUST hold for them is newline-freedom: a \n pushes the clause onto a
            // clipped line no horizontal scroll can ever reveal.
            string reservedClause = LogisticsHoldPresentation.DescribeHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "origin-lacks-source-reserved:12345:Minmus Flats Refinery Complex Alpha"
                + ":Ore:Duna Transfer Window Resupply Run 3",
                0.0);
            Assert.Contains("Minmus Flats Refinery Complex Alpha", reservedClause);
            string reservedTip = LogisticsHoldPresentation.StatusCellTooltip(
                RouteStatus.Active, reservedClause);
            Assert.DoesNotContain("\n", reservedTip);
        }

        // catches: a Recordings sort-header tooltip growing past that window's SINGLE-line
        // strip, or gaining a hard newline. These texts never reach a
        // `new GUIContent(label, tooltip)` in the file - they are passed as the trailing
        // plain-string argument of DrawSortableHeader(...) and handed to a GUIContent built
        // in ParsekUI - so the literal scanner above cannot see them and they shipped
        // unbudgeted and un-newline-checked.
        [Fact]
        public void SortableHeaderTooltips_FitTheRecordingsStrip()
        {
            const string RelPath = "UI/RecordingsTableUI.cs";
            int budget = BudgetChars(1355f, TooltipEchoBox.SingleLine);
            string src = ReadParsekSource(RelPath);

            const string Needle = "DrawSortableHeader(";
            int found = 0;
            int i = 0;
            while (true)
            {
                int start = src.IndexOf(Needle, i, StringComparison.Ordinal);
                if (start < 0)
                    break;
                int open = start + Needle.Length;
                int end = FindMatchingParen(src, open);
                if (end < 0)
                    break;
                i = end + 1;

                List<string> args = SplitTopLevelArgs(src.Substring(open, end - open));
                if (args.Count == 0)
                    continue;
                // The tooltip is the LAST parameter. The method's own declaration
                // ("string tooltip = null") and any call passing a non-literal fail to
                // decode as a literal run and are skipped, same as the GUIContent scan.
                string tip;
                if (!TryDecodeLiteralRun(args[args.Count - 1], out tip))
                    continue;

                found++;
                int line = CountLines(src, start);
                Assert.False(tip.Contains("\n"),
                    string.Format(
                        "{0} line {1}: sort-header tooltip contains a hard newline. The "
                        + "Recordings help strip is one wrapped line tall and its overflow "
                        + "marquee scrolls horizontally only - the tail after a \\n can never "
                        + "be read. Tooltip: \"{2}\"",
                        RelPath, line, tip));
                Assert.True(tip.Length <= budget,
                    string.Format(
                        "{0} line {1}: sort-header tooltip is {2} chars but the 1355px-wide "
                        + "Recordings window holds about {3} in its single-line help strip - "
                        + "the tail would scroll in via the marquee instead of reading at a "
                        + "glance. Tooltip: \"{4}\"",
                        RelPath, line, tip.Length, budget, tip));
            }

            Assert.True(found >= 6,
                string.Format(
                    "{0}: found only {1} literal DrawSortableHeader tooltips, expected at "
                    + "least 6 (#-less sortable columns: Name, Phase, Site, Launch, Duration, "
                    + "Status). Either they moved or the parser stopped matching - a vacuous "
                    + "budget gate is worse than none.",
                    RelPath, found));
        }

        // catches: a window switching its TooltipEchoBox construction to a different
        // strip height without updating its StripWindows row - the dangerous
        // direction (constructor goes SingleLine, row stays DoubleLine) would keep
        // budgeting that window's texts at TWICE the real strip capacity with the
        // suite green, which is exactly the drift this gate exists to prevent.
        [Theory]
        [MemberData(nameof(StripWindows))]
        public void StripHeightColumn_MatchesTheWindowConstructor(
            string relPath, float windowWidthPx, int minTooltips, int stripLines)
        {
            string src = ReadParsekSource(relPath);
            List<int> constructed = ExtractTooltipEchoBoxLineArgs(src);

            if (relPath == "UI/MissionsWindowUI.cs")
            {
                // Hosted content: draws inside the Recordings window and owns no
                // TooltipEchoBox of its own. If one ever appears here, this row's
                // hand-me-down height stops being derived from the host - re-point
                // the row at the strip the file actually constructs.
                Assert.True(constructed.Count == 0,
                    relPath + " now constructs its own TooltipEchoBox - update its "
                    + "StripWindows row to that strip's height instead of the host's.");
                return;
            }

            Assert.True(constructed.Count == 1,
                string.Format(
                    "{0} ({1}px, floor {2}): expected exactly one 'new TooltipEchoBox(' "
                    + "construction, found {3} - the strip-height column cannot be "
                    + "cross-checked.",
                    relPath, windowWidthPx, minTooltips, constructed.Count));
            Assert.True(stripLines == constructed[0],
                string.Format(
                    "{0}: the StripWindows row budgets {1} line(s) but the window constructs "
                    + "a {2}-line strip - re-budget the row (and its texts) to match.",
                    relPath, stripLines, constructed[0]));
        }

        // ------------------------------------------------------------------
        // Scanner
        // ------------------------------------------------------------------

        internal struct LiteralTooltip
        {
            public int Line;
            public string Text;
        }

        /// <summary>
        /// Yields the decoded second argument of every <c>new GUIContent(a, b)</c> whose
        /// second argument is a plain string literal or a <c>+</c> concatenation of them.
        /// Calls whose tooltip is a variable, a method call or an interpolation are
        /// skipped - a source scan cannot know their runtime length.
        /// </summary>
        internal static List<LiteralTooltip> ExtractLiteralGuiContentTooltips(string src)
        {
            var result = new List<LiteralTooltip>();
            const string Needle = "new GUIContent(";
            int i = 0;
            while (true)
            {
                int start = src.IndexOf(Needle, i, StringComparison.Ordinal);
                if (start < 0)
                    break;
                int open = start + Needle.Length;
                int end = FindMatchingParen(src, open);
                if (end < 0)
                    break;
                i = end + 1;

                List<string> args = SplitTopLevelArgs(src.Substring(open, end - open));
                if (args.Count < 2)
                    continue;
                string text;
                if (!TryDecodeLiteralRun(args[1], out text)
                    && !TryResolveSameFileConst(src, args[1], out text))
                    continue;

                result.Add(new LiteralTooltip
                {
                    Line = CountLines(src, start),
                    Text = text
                });
            }
            return result;
        }

        /// <summary>Index of the ')' closing the '(' that precedes <paramref name="from"/>.</summary>
        private static int FindMatchingParen(string src, int from)
        {
            int depth = 1;
            bool inString = false;
            bool verbatim = false;
            bool escaped = false;
            for (int k = from; k < src.Length; k++)
            {
                char c = src[k];
                if (inString)
                {
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (k + 1 < src.Length && src[k + 1] == '"') { k++; continue; }
                            inString = false;
                            verbatim = false;
                        }
                    }
                    else if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    escaped = false;
                    verbatim = k > from && src[k - 1] == '@';
                }
                else if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                        return k;
                }
            }
            return -1;
        }

        private static List<string> SplitTopLevelArgs(string inner)
        {
            var args = new List<string>();
            var cur = new StringBuilder();
            int depth = 0;
            bool inString = false;
            bool verbatim = false;
            bool escaped = false;
            for (int k = 0; k < inner.Length; k++)
            {
                char c = inner[k];
                if (inString)
                {
                    cur.Append(c);
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (k + 1 < inner.Length && inner[k + 1] == '"')
                            {
                                cur.Append(inner[k + 1]);
                                k++;
                                continue;
                            }
                            inString = false;
                            verbatim = false;
                        }
                    }
                    else if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    escaped = false;
                    verbatim = k > 0 && inner[k - 1] == '@';
                    cur.Append(c);
                }
                else if (c == '(' || c == '[' || c == '{') { depth++; cur.Append(c); }
                else if (c == ')' || c == ']' || c == '}') { depth--; cur.Append(c); }
                else if (c == ',' && depth == 0)
                {
                    args.Add(cur.ToString());
                    cur.Length = 0;
                }
                else cur.Append(c);
            }
            if (cur.ToString().Trim().Length > 0)
                args.Add(cur.ToString());
            return args;
        }

        /// <summary>
        /// True when <paramref name="arg"/> is nothing but string literals joined by '+',
        /// in which case <paramref name="text"/> receives the decoded concatenation.
        /// </summary>
        private static bool TryDecodeLiteralRun(string arg, out string text)
        {
            text = null;
            string a = StripLineComments(arg).Trim();
            var sb = new StringBuilder();
            int k = 0;
            bool any = false;
            while (k < a.Length)
            {
                // Only whitespace or a single '+' may sit between literals.
                while (k < a.Length && char.IsWhiteSpace(a[k])) k++;
                if (any)
                {
                    if (k >= a.Length) break;
                    if (a[k] != '+') return false;
                    k++;
                    while (k < a.Length && char.IsWhiteSpace(a[k])) k++;
                }
                if (k >= a.Length) break;
                if (a[k] != '"') return false;
                k++;
                while (k < a.Length && a[k] != '"')
                {
                    if (a[k] == '\\' && k + 1 < a.Length)
                    {
                        char esc = a[k + 1];
                        if (esc == 'n') sb.Append('\n');
                        else if (esc == 't') sb.Append('\t');
                        else if (esc == 'r') sb.Append('\r');
                        else if (esc == 'u' && k + 5 < a.Length)
                        {
                            sb.Append((char)Convert.ToInt32(a.Substring(k + 2, 4), 16));
                            k += 4;
                        }
                        else sb.Append(esc);
                        k += 2;
                        continue;
                    }
                    sb.Append(a[k]);
                    k++;
                }
                if (k >= a.Length) return false;
                k++; // closing quote
                any = true;
            }
            if (!any) return false;
            text = sb.ToString();
            return true;
        }

        /// <summary>
        /// Resolves <paramref name="arg"/> when it is a bare identifier naming a
        /// <c>const string</c> declared in the same file whose initializer is a plain
        /// literal run. Method calls, member accesses and cross-file consts still
        /// cannot be scanned.
        /// </summary>
        private static bool TryResolveSameFileConst(string src, string arg, out string text)
        {
            text = null;
            string id = StripLineComments(arg).Trim();
            if (id.Length == 0 || (!char.IsLetter(id[0]) && id[0] != '_'))
                return false;
            foreach (char c in id)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            }

            string decl = "const string " + id;
            int at = 0;
            while (true)
            {
                at = src.IndexOf(decl, at, StringComparison.Ordinal);
                if (at < 0)
                    return false;
                int after = at + decl.Length;
                // Reject a longer identifier that merely starts with ours.
                if (after < src.Length
                    && (char.IsLetterOrDigit(src[after]) || src[after] == '_'))
                {
                    at = after;
                    continue;
                }
                int eq = src.IndexOf('=', after);
                if (eq < 0)
                    return false;
                int semi = FindStatementEnd(src, eq + 1);
                if (semi < 0)
                    return false;
                return TryDecodeLiteralRun(src.Substring(eq + 1, semi - eq - 1), out text);
            }
        }

        /// <summary>Index of the ';' ending the initializer at <paramref name="from"/>, string-aware.</summary>
        private static int FindStatementEnd(string src, int from)
        {
            bool inString = false, verbatim = false, escaped = false;
            for (int k = from; k < src.Length; k++)
            {
                char c = src[k];
                if (inString)
                {
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (k + 1 < src.Length && src[k + 1] == '"') { k++; continue; }
                            inString = false;
                            verbatim = false;
                        }
                    }
                    else if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    escaped = false;
                    verbatim = k > from && src[k - 1] == '@';
                }
                else if (c == ';')
                    return k;
            }
            return -1;
        }

        /// <summary>
        /// The strip height of every <c>new TooltipEchoBox(...)</c> in
        /// <paramref name="src"/>: the (spacing, lines) overload's second argument read
        /// by its SingleLine/DoubleLine token; the zero- and one-argument overloads
        /// default to <see cref="TooltipEchoBox.DoubleLine"/>; anything unclassifiable
        /// yields -1 so the cross-check fails loudly instead of guessing.
        /// </summary>
        internal static List<int> ExtractTooltipEchoBoxLineArgs(string src)
        {
            var result = new List<int>();
            string code = StripLineComments(src);
            const string Needle = "new TooltipEchoBox(";
            int i = 0;
            while (true)
            {
                int start = code.IndexOf(Needle, i, StringComparison.Ordinal);
                if (start < 0)
                    break;
                int open = start + Needle.Length;
                int end = FindMatchingParen(code, open);
                if (end < 0)
                    break;
                i = end + 1;
                List<string> args = SplitTopLevelArgs(code.Substring(open, end - open));
                if (args.Count < 2)
                {
                    result.Add(TooltipEchoBox.DoubleLine);
                    continue;
                }
                string lineArg = args[1].Trim();
                if (lineArg.EndsWith("SingleLine", StringComparison.Ordinal))
                    result.Add(TooltipEchoBox.SingleLine);
                else if (lineArg.EndsWith("DoubleLine", StringComparison.Ordinal))
                    result.Add(TooltipEchoBox.DoubleLine);
                else
                    result.Add(-1);
            }
            return result;
        }

        private static string StripLineComments(string s)
        {
            var sb = new StringBuilder();
            bool inString = false;
            bool escaped = false;
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (inString)
                {
                    sb.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '/' && k + 1 < s.Length && s[k + 1] == '/')
                {
                    while (k < s.Length && s[k] != '\n') k++;
                    sb.Append('\n');
                    continue;
                }
                if (c == '"') { inString = true; escaped = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int CountLines(string src, int offset)
        {
            int line = 1;
            for (int k = 0; k < offset && k < src.Length; k++)
                if (src[k] == '\n') line++;
            return line;
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
