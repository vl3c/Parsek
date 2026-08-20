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
    /// visible box of exactly TWO wrapped lines: text needing a third line clips
    /// silently, with no visual cue that the player is reading a truncated sentence.
    /// The strip spans the window's full content width, so how much text fits is a
    /// property of the WINDOW, not of the control - a sentence that fits the 1556 px
    /// Logistics window clips in the 280 px Settings window.</para>
    ///
    /// <para><b>How the per-file budget is derived.</b> For a window whose first-open
    /// width is <c>W</c>: subtract ~20 px of window chrome (the skin window style's
    /// left+right padding) and ~10 px of box padding, then divide by a deliberately
    /// pessimistic ~7 px average character advance for KSP's default IMGUI label font
    /// (~13 px; the real average is nearer 6.2, so the budget has headroom), and allow
    /// two lines: <c>budget = floor(2 * (W - 30) / 7)</c>. Word wrap can only make a
    /// line end EARLY, so this is an upper bound on what two lines hold, which is
    /// exactly the direction a guard wants.</para>
    ///
    /// <para><b>What this gate covers.</b> Every <c>new GUIContent(label, tooltip)</c>
    /// in the six strip-hosting windows whose tooltip argument is a plain string
    /// literal (or a concatenation of them) - the population that a copy edit can
    /// actually regress. Tooltips assembled at runtime from data (recording names,
    /// hold reasons, in-game test descriptions) cannot be pinned by a source scan;
    /// the pure builders that feed the longest of them are asserted directly below
    /// instead. A parser regression that silently matched nothing would make this
    /// gate vacuous, so each file also pins a minimum literal-tooltip count.</para>
    ///
    /// <para>Hard newlines are rejected outright: an explicit <c>\n</c> spends one of
    /// the two lines regardless of how short the text is, so a two-clause tooltip with
    /// a break in it clips as soon as either clause wraps.</para>
    /// </summary>
    public class TooltipEchoBudgetTests
    {
        /// <summary>Window chrome (skin window padding) plus the strip box's own padding, in px.</summary>
        private const float StripPaddingPx = 30f;

        /// <summary>Pessimistic average character advance for the default IMGUI label font, in px.</summary>
        private const float AvgCharWidthPx = 7f;

        /// <summary>The strip is exactly this many wrapped text lines tall.</summary>
        private const int StripLines = 2;

        /// <summary>
        /// The six strip-hosting windows: source path, the window's first-open width in
        /// px, and the minimum number of literal GUIContent tooltips the scan must find
        /// in that file (a floor, not a target - raise it only if the parser is proven).
        /// </summary>
        public static IEnumerable<object[]> StripWindows()
        {
            // Settings: DrawIfOpen seeds new Rect(..., 280, 600) on first open.
            yield return new object[] { "UI/SettingsWindowUI.cs", 280f, 15 };
            // Recordings: DefaultCollapsedWindowWidth = 1205 + ColW_Rewind(60) + ColW_ReFly(90).
            yield return new object[] { "UI/RecordingsTableUI.cs", 1355f, 3 };
            // Logistics: first-open default new Rect(..., 1556, 500).
            yield return new object[] { "UI/LogisticsWindowUI.cs", 1556f, 20 };
            // Real Spawn Control: first-open default new Rect(..., 750, 200).
            yield return new object[] { "UI/SpawnControlUI.cs", 750f, 0 };
            // Test runner (Settings-launched) and the global Ctrl+Shift+T twin: 440.
            yield return new object[] { "UI/TestRunnerUI.cs", 440f, 3 };
            yield return new object[] { "InGameTests/TestRunnerShortcut.cs", 440f, 2 };
        }

        internal static int BudgetChars(float windowWidthPx)
        {
            return (int)(StripLines * (windowWidthPx - StripPaddingPx) / AvgCharWidthPx);
        }

        // catches: a hover help text growing past what two wrapped lines hold in its own
        // window, which the strip swallows silently instead of reporting.
        [Theory]
        [MemberData(nameof(StripWindows))]
        public void LiteralGuiContentTooltips_FitTheTwoLineStrip(
            string relPath, float windowWidthPx, int minTooltips)
        {
            int budget = BudgetChars(windowWidthPx);
            string src = ReadParsekSource(relPath);

            int found = 0;
            foreach (var tip in ExtractLiteralGuiContentTooltips(src))
            {
                found++;
                Assert.False(tip.Text.Contains("\n"),
                    string.Format(
                        "{0} line {1}: tooltip contains a hard newline. The help strip is exactly "
                        + "two wrapped lines - a \\n spends one of them and clips the rest. "
                        + "Tooltip: \"{2}\"",
                        relPath, tip.Line, tip.Text));
                Assert.True(tip.Text.Length <= budget,
                    string.Format(
                        "{0} line {1}: tooltip is {2} chars but the {3}px-wide window holds about "
                        + "{4} in the two-line help strip - the tail clips silently in game. "
                        + "Shorten it (keep what the control does plus the key consequence). "
                        + "Tooltip: \"{5}\"",
                        relPath, tip.Line, tip.Text.Length, windowWidthPx, budget, tip.Text));
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
            int budget = BudgetChars(280f);
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

        // catches: the Supply-Run cost explanation growing past the Logistics strip. The
        // candidate row prefixes the detail LINE to the tooltip, so the worst case is
        // "no recovery" (the recover-to-reduce hint is appended) with a large launch cost.
        [Fact]
        public void RouteRunCostTooltip_FitsTheLogisticsStrip()
        {
            int budget = BudgetChars(1556f);
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
                string.Format("FormatDetailTooltip is {0} chars, over the {1}-char budget: \"{2}\"",
                    tip.Length, budget, tip));

            // The candidate-row composition (LogisticsWindowUI's wouldDeliverTip) joins the
            // two with spaces, never "\n" - the whole thing must still fit two wrapped lines.
            string combined = LogisticsCostPresentation.FormatDetailLine(noRecovery) + "  " + tip;
            Assert.True(combined.Length <= budget,
                string.Format(
                    "FormatDetailLine + FormatDetailTooltip is {0} chars, over the {1}-char "
                    + "Logistics help-strip budget: \"{2}\"",
                    combined.Length, budget, combined));
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
                if (!TryDecodeLiteralRun(args[1], out text))
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
