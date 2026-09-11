using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// WITNESSES for the APPLIER half of the GUI-census ops.
    ///
    /// <para>The pure halves (<c>TestCommandUiAction</c>, <c>TestCommandUiState</c>,
    /// <c>TestCommandUiFind</c>, <c>TestCommandUiPointer</c>, <c>TestCommandUiDialog</c>)
    /// are exercised directly by <c>TestCommandUiCensusOpsTests</c>. The APPLIER lives on a
    /// <c>MonoBehaviour</c> partial that reaches into <c>ParsekUI</c> and the scene hosts,
    /// so xUnit cannot call it - which left three per-window tables with no cell on them at
    /// all. A mutation to any of them (a window row wired to the wrong class's minimum, an
    /// expansion prefix dropped from a window's set, a teardown that dismisses one of two
    /// dialog names) survived the whole suite.</para>
    ///
    /// <para>These cells close that by deriving the applier's tables FROM ITS SOURCE and
    /// comparing them against the pure side's own tables - the shape
    /// <c>hlib.GuiCensusSeamVerbTests.test_the_ops_needing_a_window_mirror_the_c_sharp_predicate</c>
    /// uses on the Python side. Comments are stripped first, with the line indices kept
    /// (see <see cref="ParsekDialogNamePrefixSourceGateTests.StripComments"/>): these
    /// methods' own headers discuss the rows they deliberately do NOT have ("NO minimum:
    /// both hosts pass a fixed GUILayout.Width"), so a scan that read comments would report
    /// minimums that are not wired and pass against a source saying the opposite.</para>
    /// </summary>
    public class GuiCensusApplierSourceGateTests
    {
        private static string ReadSource(params string[] parts)
        {
            string path = Path.Combine(
                new[] { ResolveRepoRoot(), "Source", "Parsek" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), "source file moved, this gate is vacuous: " + path);
            return ParsekDialogNamePrefixSourceGateTests.StripComments(File.ReadAllText(path));
        }

        // ----- ResolveExpandSets vs TestCommandUiState.ExpandPrefixesFor -----

        /// <summary>Maps the C# prefix CONSTANT name to its value, so the scan compares
        /// values rather than spellings. Read off the constants themselves, which is what
        /// makes a renamed constant a compile error here instead of a silent miss.</summary>
        private static readonly Dictionary<string, string> PrefixConstants =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "GroupKeyPrefix", TestCommandUiState.GroupKeyPrefix },
                { "ChainKeyPrefix", TestCommandUiState.ChainKeyPrefix },
                { "VesselKeyPrefix", TestCommandUiState.VesselKeyPrefix },
                { "LegKeyPrefix", TestCommandUiState.LegKeyPrefix },
                { "DigestKeyPrefix", TestCommandUiState.DigestKeyPrefix },
                { "RowKeyPrefix", TestCommandUiState.RowKeyPrefix },
            };

        [Fact]
        public void ResolveExpandSets_WiresExactlyThePrefixesTheParseAccepts()
        {
            // The hole this closes: TryParseExpandKey validates `key=<prefix>:<value>`
            // against ExpandPrefixesFor, and the APPLIER then looks the prefix up in the
            // sets ResolveExpandSets built. A prefix in one and not the other is either a
            // key the seam accepts and then cannot drive (expand-key-unknown after a whole
            // KSP boot) or a live surface no lane can name at all.
            string code = ReadSource("TestCommands", "ParsekTestCommandAddon.UiState.cs");
            Dictionary<string, List<string>> wired = ParseExpandSetPrefixes(code);

            foreach (string window in new[] { TestCommandUiAction.MissionsWindow,
                                              TestCommandUiAction.LogisticsWindow })
            {
                string[] parsed = TestCommandUiState.ExpandPrefixesFor(window);
                Assert.True(wired.ContainsKey(window),
                    "ResolveExpandSets has no branch for window '" + window
                    + "', but TryParseExpandKey accepts prefixes for it: "
                    + string.Join(",", parsed));
                // ORDERED: the sets are walked in order for `key=all` / `key=none`, and the
                // op's `expanded=` count is summed across them in the same order, so a
                // reordering is visible on the wire.
                Assert.Equal(parsed, wired[window].ToArray());
            }
            // And no branch exists for a window the parse refuses: op=expand against it
            // answers expand-unsupported-window, and a wired-but-unreachable set would be
            // dead code that reads as coverage.
            foreach (string window in wired.Keys)
            {
                Assert.True(TestCommandUiState.ExpandPrefixesFor(window) != null,
                    "ResolveExpandSets wires window '" + window + "', which "
                    + "TryParseExpandKey refuses as expand-unsupported-window");
            }
        }

        [Fact]
        public void TheExpandSetParseIsNotVacuous()
        {
            // Anti-vacuity, over a SYNTHETIC source: the real method's neighbourhood names
            // prefixes in prose and the window constants in comments, so a scan that read
            // comments would report a set that is not wired.
            string synthetic = ParsekDialogNamePrefixSourceGateTests.StripComments(
                string.Join("\n", new[]
                {
                    "// if (window == TestCommandUiAction.TimelineWindow)",
                    "// Prefix = TestCommandUiState.DigestKeyPrefix,",
                    "if (window == TestCommandUiAction.MissionsWindow)",
                    "{",
                    "    sets.Add(new UiExpandSet { Prefix = TestCommandUiState.GroupKeyPrefix, });",
                    "    sets.Add(new UiExpandSet { Prefix = TestCommandUiState.ChainKeyPrefix, });",
                    "    return sets;",
                    "}",
                    "if (window == TestCommandUiAction.LogisticsWindow)",
                    "{",
                    "    sets.Add(new UiExpandSet { Prefix = TestCommandUiState.RowKeyPrefix, });",
                    "    return sets;",
                    "}",
                }));
            Dictionary<string, List<string>> wired = ParseExpandSetPrefixes(synthetic);
            Assert.Equal(2, wired.Count);
            Assert.Equal(new[] { "group", "chain" }, wired["missions"].ToArray());
            Assert.Equal(new[] { "row" }, wired["logistics"].ToArray());
        }

        /// <summary>Per-window ordered prefix VALUES out of a comment-free
        /// <c>ResolveExpandSets</c>. Window branches are <c>if (window ==
        /// TestCommandUiAction.&lt;Const&gt;)</c> and each set names its prefix as
        /// <c>Prefix = TestCommandUiState.&lt;Const&gt;</c>; both constants are resolved to
        /// their values so a rename is a compile error rather than a silent miss.</summary>
        private static Dictionary<string, List<string>> ParseExpandSetPrefixes(string code)
        {
            var windowConstants = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "MissionsWindow", TestCommandUiAction.MissionsWindow },
                { "LogisticsWindow", TestCommandUiAction.LogisticsWindow },
                { "TimelineWindow", TestCommandUiAction.TimelineWindow },
                { "SettingsWindow", TestCommandUiAction.SettingsWindow },
            };
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string current = null;
            foreach (Match m in Regex.Matches(
                code,
                @"window\s*==\s*TestCommandUiAction\.(\w+)"
                + @"|Prefix\s*=\s*TestCommandUiState\.(\w+)"))
            {
                if (m.Groups[1].Success)
                {
                    string value;
                    current = windowConstants.TryGetValue(m.Groups[1].Value, out value)
                        ? value : m.Groups[1].Value;
                    if (!result.ContainsKey(current)) result[current] = new List<string>();
                    continue;
                }
                if (current == null) continue;
                string prefix;
                Assert.True(PrefixConstants.TryGetValue(m.Groups[2].Value, out prefix),
                    "ResolveExpandSets names an expand prefix constant this gate does not "
                    + "know: TestCommandUiState." + m.Groups[2].Value
                    + " - add it to PrefixConstants.");
                result[current].Add(prefix);
            }
            return result;
        }

        // ----- ResolveWindowHandle's per-window MinW / MinH -----

        /// <summary>The windows deliberately wired with NO minimum, each with the reason
        /// stated at its own row: <c>main</c>'s size is host-owned (both hosts pass a fixed
        /// <c>GUILayout.Width(250)</c> and zero the height every frame), and
        /// <c>settings</c> / <c>gloops</c> have no resize handle at all, so there is no drag
        /// floor to reproduce. Listed here so "this row has no minimum" is a CLAIM the gate
        /// checks, not an absence it tolerates.</summary>
        private static readonly string[] WindowsWithoutMinimums =
        {
            TestCommandUiAction.MainWindow,
            TestCommandUiAction.SettingsWindow,
            TestCommandUiAction.GloopsWindow,
        };

        [Fact]
        public void ResolveWindowHandle_TakesEachWindowsMinimumsFromThatWindowsOwnClass()
        {
            // The mutation this kills: a row wired to ANOTHER window's minimum (or to a
            // literal). op=rect clamps the commanded rect to MinW/MinH, so a wrong floor
            // silently photographs a layout no player can produce - the very defect the
            // clamp was added for, reintroduced one row at a time.
            string code = ReadSource("TestCommands", "ParsekTestCommandAddon.UiAction.cs");
            List<WindowRow> rows = ParseWindowHandleRows(code);

            string[] tableOrder = TestCommandUiAction.Windows.Select(w => w.Name).ToArray();
            // ORDERED and COMPLETE against the pure table: a window in the vocabulary with
            // no resolver row throws ArgumentOutOfRangeException at run time (the default
            // arm), and a resolver row for a window the table does not carry is unreachable.
            Assert.Equal(tableOrder, rows.Select(r => r.Window).ToArray());

            foreach (WindowRow row in rows)
            {
                bool expectMin = !WindowsWithoutMinimums.Contains(row.Window);
                if (!expectMin)
                {
                    Assert.True(row.MinW == null && row.MinH == null,
                        "window '" + row.Window + "' is declared minimum-less but the "
                        + "resolver wires minW=" + (row.MinW ?? "-") + " minH="
                        + (row.MinH ?? "-"));
                    continue;
                }
                Assert.True(row.MinW != null && row.MinH != null,
                    "window '" + row.Window + "' has no minimum wired, so op=rect would "
                    + "clamp it to 0 and a commanded rect below its resize floor would be "
                    + "applied verbatim");
                // BOTH axes, and BOTH from the class whose live fields the same row reads.
                Assert.Equal(row.OwnerType + ".MinWindowWidth", row.MinW);
                Assert.Equal(row.OwnerType + ".MinWindowHeight", row.MinH);
            }
        }

        [Fact]
        public void TheWindowHandleRowParseIsNotVacuous()
        {
            // Anti-vacuity over a SYNTHETIC source, with a commented decoy row: the real
            // method's rows carry "NO minimum: ..." comments that name MinWindowWidth in
            // prose, so a scan reading comments would report a wired minimum on a row that
            // has none - passing against a source that says the opposite.
            string synthetic = ParsekDialogNamePrefixSourceGateTests.StripComments(
                string.Join("\n", new[]
                {
                    "case TestCommandUiAction.MainWindow:",
                    "    // NO minimum: host-owned. Not KerbalsWindowUI.MinWindowWidth.",
                    "    return Handle(ReadHostShowUi, WriteHostShowUi);",
                    "case TestCommandUiAction.KerbalsWindow:",
                    "{",
                    "    KerbalsWindowUI w = ui.GetKerbalsUI();",
                    "    return Handle(() => w.IsOpen, v => w.IsOpen = v,",
                    "                  KerbalsWindowUI.MinWindowWidth,",
                    "                  KerbalsWindowUI.MinWindowHeight);",
                    "}",
                }));
            List<WindowRow> rows = ParseWindowHandleRows(synthetic);
            Assert.Equal(2, rows.Count);
            Assert.Null(rows[0].MinW);
            Assert.Null(rows[0].MinH);
            Assert.Equal("KerbalsWindowUI.MinWindowWidth", rows[1].MinW);
            Assert.Equal("KerbalsWindowUI.MinWindowHeight", rows[1].MinH);
            Assert.Equal("KerbalsWindowUI", rows[1].OwnerType);
        }

        private struct WindowRow
        {
            internal string Window;
            /// <summary>The UI class the row reads live state from, or null for the two
            /// rows that reach the scene host instead.</summary>
            internal string OwnerType;
            internal string MinW;
            internal string MinH;
        }

        /// <summary>One row per <c>case TestCommandUiAction.&lt;Const&gt;:</c> of a
        /// comment-free <c>ResolveWindowHandle</c>, in source order.</summary>
        private static List<WindowRow> ParseWindowHandleRows(string code)
        {
            var byConstant = new HashSet<string>(
                TestCommandUiAction.Windows.Select(w => w.Name), StringComparer.Ordinal);
            var rows = new List<WindowRow>();
            MatchCollection cases = Regex.Matches(
                code, @"case\s+TestCommandUiAction\.(\w+)\s*:");
            for (int i = 0; i < cases.Count; i++)
            {
                int start = cases[i].Index + cases[i].Length;
                int end = i + 1 < cases.Count ? cases[i + 1].Index : code.Length;
                string body = code.Substring(start, end - start);
                string token = ResolveWindowToken(cases[i].Groups[1].Value);
                Assert.True(byConstant.Contains(token),
                    "ResolveWindowHandle has a case for an unknown window token: " + token);
                rows.Add(new WindowRow
                {
                    Window = token,
                    OwnerType = FirstGroup(body, @"(\w+UI)\s+w\s*="),
                    MinW = FirstGroup(body, @"(?:minW:\s*)?((?:\w+\.)?MinWindowWidth)\b"),
                    MinH = FirstGroup(body, @"(?:minH:\s*)?((?:\w+\.)?MinWindowHeight)\b"),
                });
            }
            return rows;
        }

        private static string FirstGroup(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Resolves a <c>TestCommandUiAction.&lt;Const&gt;</c> spelling to the
        /// token value. Keeps the gate comparing VALUES, so a renamed constant is a compile
        /// error in this table rather than a silent pass.</summary>
        private static string ResolveWindowToken(string constantName)
        {
            switch (constantName)
            {
                case "MainWindow": return TestCommandUiAction.MainWindow;
                case "MissionsWindow": return TestCommandUiAction.MissionsWindow;
                case "TimelineWindow": return TestCommandUiAction.TimelineWindow;
                case "KerbalsWindow": return TestCommandUiAction.KerbalsWindow;
                case "CareerWindow": return TestCommandUiAction.CareerWindow;
                case "LogisticsWindow": return TestCommandUiAction.LogisticsWindow;
                case "StructureWindow": return TestCommandUiAction.StructureWindow;
                case "SettingsWindow": return TestCommandUiAction.SettingsWindow;
                case "SpawnControlWindow": return TestCommandUiAction.SpawnControlWindow;
                case "GloopsWindow": return TestCommandUiAction.GloopsWindow;
                case "TestRunnerWindow": return TestCommandUiAction.TestRunnerWindow;
                default: return constantName;
            }
        }

        // ----- MergeDialog teardown dismisses BOTH dialog names -----

        [Fact]
        public void DismissAndClearPendingFlag_DismissesBothDialogNames()
        {
            // The mutation that survived: dismissing only ONE of the two names. This is the
            // non-button cleanup path for every dialog MergeDialog spawns, and the
            // pre-switch popup now carries its own name - so dismissing the merge name
            // alone leaves a pre-switch popup standing over the input lock the same method
            // then RELEASES. That is the stealth state (a modal with no lock behind it) the
            // pre-switch Esc-respawn contract exists to prevent.
            //
            // Source-derived because the method calls PopupDialog.DismissPopup, a Unity
            // uGUI entry point xUnit cannot drive.
            string code = ReadSource("MergeDialog.cs");
            string body = MethodBody(code, "internal static void DismissAndClearPendingFlag(");

            foreach (string name in new[] { "DialogName", "PreSwitchDialogName" })
                Assert.True(Regex.IsMatch(body, @"\b" + name + @"\b"),
                    "DismissAndClearPendingFlag must dismiss " + name
                    + "; its body names only: " + body.Trim());
            // ONE DismissPopup CALL over both, rather than two hand-written calls: a
            // second literal call site is how the third dialog name would get forgotten.
            // Anchored on the qualified form, because the method's own Warn line quotes an
            // unqualified `DismissPopup('...')` inside a string and must not be counted.
            int qualifiedCalls =
                Regex.Matches(body, @"PopupDialog\.DismissPopup\s*\(").Count;
            Assert.Equal(1, qualifiedCalls);
            Assert.True(Regex.IsMatch(body, @"foreach\s*\(\s*string\s+\w+\s+in\b"),
                "the dismissal must iterate the name list, not branch per name: " + body);
            // And the two names really are different popups (a gate over one name spelled
            // twice would pass every assertion above).
            Assert.NotEqual(MergeDialog.DialogName, MergeDialog.PreSwitchDialogName);
            Assert.True(TestCommandUiDialog.IsParsekDialogName(MergeDialog.DialogName));
            Assert.True(TestCommandUiDialog.IsParsekDialogName(
                MergeDialog.PreSwitchDialogName));
        }

        [Fact]
        public void AnswerMergeDialog_RoutesItsDialogArgThroughTheClosedSetParse()
        {
            // The applier-side half of the dialog= arg: the pure parse refuses an unknown
            // value (TestCommandUiCensusOpsTests covers that), and this is the witness that
            // the verb CALLS it and REJECTS on the answer rather than defaulting.
            string code = ReadSource("TestCommands", "ParsekTestCommandAddon.cs");
            Match call = Regex.Match(
                code,
                @"if\s*\(\s*!\s*TestCommandUiDialog\.TryParseAnswerDialog\((?s).{0,600}?"
                + @"SetExecResult\(""REJECTED""");
            Assert.True(call.Success,
                "AnswerMergeDialog must refuse on TryParseAnswerDialog's rejection; no "
                + "`if (!TryParseAnswerDialog(...)) ... SetExecResult(\"REJECTED\"` found");
            Assert.Contains(TestCommandUiDialog.DialogArg, call.Value);
        }

        /// <summary>The body of one method of a comment-free source, from its signature to
        /// the matching close brace.</summary>
        private static string MethodBody(string code, string signature)
        {
            int at = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "method signature not found: " + signature);
            int open = code.IndexOf('{', at);
            Assert.True(open > at, "method has no body: " + signature);
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0) return code.Substring(open + 1, i - open - 1);
                }
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
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
