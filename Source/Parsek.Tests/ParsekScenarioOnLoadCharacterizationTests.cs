using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <c>ParsekScenario.OnLoad</c>, pinned against the original
    /// single-method OnLoad before its stretches were extracted into named phase methods.
    ///
    /// <para><b>What is pinned.</b> OnLoad's body is reduced to an ordered token stream: every
    /// string literal (verbatim, as written), every invoked name (the dotted chain before a
    /// <c>(</c>, keywords excluded) and every assignment target (the dotted chain before
    /// <c>=</c>, <c>+=</c>, <c>-=</c>, <c>++</c>, <c>--</c>). Comments are dropped. A call to a
    /// method named <c>OnLoadPhase*</c> is replaced by that method's own token stream, so an
    /// extraction that moves a stretch verbatim into such a method leaves the expanded stream
    /// byte-identical, while a reordered call, a changed log text, a dropped write or a
    /// stretch moved into a method of any other name changes it. The expected stream is the
    /// golden file <c>Fixtures/SourceCharacterization/ParsekScenario.OnLoad.tokens.txt</c>,
    /// captured from the original method; on a mismatch the actual stream is written next to
    /// the test binaries and the first divergence is reported.</para>
    ///
    /// <para><b>What stays in OnLoad.</b> The phase skeleton is pinned on OnLoad's OWN body
    /// (no expansion): the ordered <c>loadPhase = "..."</c> values, and no phase method may
    /// touch <c>loadPhase</c>, <c>loadStatus</c> or <c>onLoadInProgress</c>, or hold a
    /// <c>try</c>, <c>catch</c>, <c>finally</c> or <c>yield</c>.</para>
    /// </summary>
    public class ParsekScenarioOnLoadCharacterizationTests
    {
        internal const string OnLoadSignature = "public override void OnLoad(ConfigNode node)";
        internal const string PhasePrefix = "OnLoadPhase";

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return",
            "nameof", "typeof", "sizeof", "default", "when", "fixed", "checked", "unchecked",
            "throw", "await", "in", "is", "as", "out", "ref", "var", "new", "else", "do", "case",
            "base", "this", "try", "finally",
        };

        // ---- the pinned characterization ----

        [Fact]
        public void OnLoadExpandedTokenStream_MatchesThePinnedOriginal()
        {
            string classText = ParsekScenarioSourceFiles.ReadAll(ParsekSourceRoot());
            List<string> actual = ExpandedOnLoadTokens(classText);

            string goldenPath = GoldenPath();
            Assert.True(File.Exists(goldenPath), "golden token stream missing: " + goldenPath);
            List<string> expected = File.ReadAllLines(goldenPath)
                .Where(l => l.Length > 0)
                .ToList();

            if (!expected.SequenceEqual(actual))
            {
                string actualPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "ParsekScenario.OnLoad.tokens.actual.txt");
                File.WriteAllLines(actualPath, actual);
                int i = 0;
                while (i < expected.Count && i < actual.Count && expected[i] == actual[i]) i++;
                Assert.True(false,
                    "OnLoad's expanded token stream diverges from the pinned original at token "
                    + i + ": expected " + (i < expected.Count ? "'" + expected[i] + "'" : "<end>")
                    + ", actual " + (i < actual.Count ? "'" + actual[i] + "'" : "<end>")
                    + " (expected " + expected.Count + " tokens, actual " + actual.Count
                    + "; actual stream written to " + actualPath + ")");
            }
        }

        [Fact]
        public void OnLoadOwnBody_KeepsThePhaseSkeleton()
        {
            string classText = ParsekScenarioSourceFiles.ReadAll(ParsekSourceRoot());
            string body = MethodBody(classText, OnLoadSignature);
            List<string> tokens = Tokenize(body);
            var phases = new List<string>();
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] == "A:loadPhase") phases.Add(tokens[i + 1]);
            }

            Assert.Equal(PinnedLoadPhases.Select(p => "S:" + p), phases);
            Assert.Contains("A:onLoadInProgress", tokens);
            Assert.Contains("A:loadStatus", tokens);
            string masked = Mask(body);
            foreach (string kw in new[] { "try", "catch", "finally" })
                Assert.True(SourceScanText.ContainsIdentifier(masked, kw), "OnLoad lost its '" + kw + "'");
        }

        [Fact]
        public void EveryOnLoadPhaseMethod_IsDeclaredOnceCalledOnceAndHoldsNoSkeleton()
        {
            string classText = ParsekScenarioSourceFiles.ReadAll(ParsekSourceRoot());
            string masked = Mask(classText);
            var declared = PhaseDeclarations(classText);
            foreach (var kv in declared)
            {
                string name = kv.Key;
                Assert.True(kv.Value.Count == 1, name + " is declared " + kv.Value.Count + " times");
                int calls = CountCalls(masked, name) - 1;   // minus the declaration itself
                Assert.True(calls == 1, name + " is called " + calls + " times; a phase runs once");

                string phaseBody = MethodBody(classText, kv.Value[0]);
                List<string> tokens = Tokenize(phaseBody);
                string phaseMasked = Mask(phaseBody);
                Assert.DoesNotContain("A:loadPhase", tokens);
                Assert.DoesNotContain("A:loadStatus", tokens);
                Assert.DoesNotContain("A:onLoadInProgress", tokens);
                foreach (string kw in new[] { "try", "catch", "finally", "yield" })
                {
                    Assert.False(SourceScanText.ContainsIdentifier(phaseMasked, kw),
                        name + " holds '" + kw + "'; the skeleton stays in OnLoad");
                }
            }

            // Every phase is reachable from OnLoad (no orphan named OnLoadPhase*).
            var reached = new HashSet<string>(StringComparer.Ordinal);
            ExpandedOnLoadTokens(classText, reached);
            Assert.Equal(
                declared.Keys.OrderBy(k => k, StringComparer.Ordinal),
                reached.OrderBy(k => k, StringComparer.Ordinal));
        }

        // ---- the tokenizer and the expansion on synthetic text ----

        [Fact]
        public void Tokenize_EmitsLiteralsCallsAndAssignmentTargets_AndDropsComments()
        {
            string src =
                "{\n"
                + "    // Foo(\"comment\") = 1;\n"
                + "    /* Bar(); */\n"
                + "    x.y = Baz(\"a\", 1);\n"
                + "    if (Qux<int>(z)) count++;\n"
                + "    ParsekLog.Info(\"S\", $\"v={F(\"in\")} n={n}\");\n"
                + "    var k = new Thing(@\"p\"\"q\");\n"
                + "    total += 2; a == b; c => d; e != f; g <= h;\n"
                + "    s?.Run();\n"
                + "    char ch = '\"';\n"
                + "}\n";
            Assert.Equal(new[]
            {
                "A:x.y", "C:Baz", "S:\"a\"",
                "C:Qux", "A:count",
                "C:ParsekLog.Info", "S:\"S\"", "S:$\"v={F(\"in\")} n={n}\"",
                "A:k", "C:Thing", "S:@\"p\"\"q\"",
                "A:total",
                "C:s?.Run",
                "A:ch",
            }, Tokenize(src));
        }

        [Fact]
        public void Expansion_InlinesPhaseBodiesInCallOrder()
        {
            string cls =
                "class C {\n"
                + "    public override void OnLoad(ConfigNode node)\n"
                + "    {\n"
                + "        A();\n"
                + "        if (!OnLoadPhaseOne(node, out bool b)) return;\n"
                + "        D(\"x\");\n"
                + "    }\n"
                + "    private bool OnLoadPhaseOne(ConfigNode node, out bool b)\n"
                + "    {\n"
                + "        B();\n"
                + "        b = OnLoadPhaseTwo();\n"
                + "        return true;\n"
                + "    }\n"
                + "    private bool OnLoadPhaseTwo() { C(); return false; }\n"
                + "}\n";
            var reached = new HashSet<string>(StringComparer.Ordinal);
            Assert.Equal(new[] { "C:A", "C:B", "A:b", "C:C", "C:D", "S:\"x\"" },
                ExpandedOnLoadTokens(cls, reached));
            Assert.Equal(new[] { "OnLoadPhaseOne", "OnLoadPhaseTwo" },
                reached.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void Expansion_SeesAStretchMovedIntoAnOrdinaryMethod()
        {
            string original =
                "class C {\n"
                + "    public override void OnLoad(ConfigNode node) { A(); B(); C(); }\n"
                + "}\n";
            string movedToPhase =
                "class C {\n"
                + "    public override void OnLoad(ConfigNode node) { A(); OnLoadPhaseMid(); C(); }\n"
                + "    private void OnLoadPhaseMid() { B(); }\n"
                + "}\n";
            string movedElsewhere =
                "class C {\n"
                + "    public override void OnLoad(ConfigNode node) { A(); Mid(); C(); }\n"
                + "    private void Mid() { B(); }\n"
                + "}\n";
            string reordered =
                "class C {\n"
                + "    public override void OnLoad(ConfigNode node) { A(); OnLoadPhaseMid(); }\n"
                + "    private void OnLoadPhaseMid() { C(); B(); }\n"
                + "}\n";
            var expected = ExpandedOnLoadTokens(original);
            Assert.Equal(expected, ExpandedOnLoadTokens(movedToPhase));
            Assert.NotEqual(expected, ExpandedOnLoadTokens(movedElsewhere));
            Assert.NotEqual(expected, ExpandedOnLoadTokens(reordered));
        }

        // ---- implementation ----

        private static readonly string[] PinnedLoadPhases =
        {
            "\"entry\"", "\"reset-dialog\"", "\"settings\"", "\"save-format\"", "\"hooks\"",
            "\"load-classify\"", "\"save-scoped-reset\"", "\"crew-groups\"",
            "\"refly-state-load\"", "\"stock-pool-reference-load\"", "\"ledger-held-window-load\"",
            "\"rewind-vessel-holds-load\"", "\"playback-replay-scope-load\"",
            "\"removed-replay-flags-load\"", "\"rewind-point-carry-over\"",
            "\"rewind-staged-lists-carry-over\"", "\"in-session-staged-handoff\"",
            "\"pre-parsek-backup\"", "\"game-state-recorder\"", "\"initial-baseline\"",
            "\"timeline-break\"", "\"rewind\"", "\"vessel-names-apply\"",
            "\"active-tree-restore\"", "\"in-session-handoff-step-b\"",
            "\"revert-classification\"", "\"vessel-names-revert-undo\"", "\"revert-cleanup\"",
            "\"tree-mutable-state\"", "\"revert-strip\"", "\"tree-state-restore\"",
            "\"discard-refly-vessel-holds\"", "\"revert-milestones\"", "\"scene-milestones\"",
            "\"limbo-dispatch\"", "\"pending-outside-flight\"", "\"route-load-reconcile\"",
            "\"ledger-recalculate\"", "\"sidecar-reconcile\"", "\"rewind-post-load\"",
            "\"merge-journal\"", "\"load-time-sweep\"", "\"load-time-sweep-recalculate\"",
            "\"test-batch-reconcile\"", "\"rewind-point-reap\"", "\"file-collection\"",
            "\"vessel-names-apply\"", "\"cold-start\"", "\"quarantine-restore\"",
            "\"durable-committed-id-hint\"", "\"stale-pending-discard\"", "\"clear-committed\"",
            "\"chain-validation\"", "\"vessel-names\"", "\"recording-trees\"",
            "\"sanitize-nonloopable-loop\"", "\"missions\"", "\"cold-active-tree-restore\"",
            "\"cold-attempt-events\"", "\"milestones\"", "\"ledger-load\"", "\"routes\"",
            "\"route-loop-reconcile\"", "\"deferred-seed\"", "\"load-summary\"",
            "\"optimization\"", "\"crew-auto-unreserve\"", "\"pending-outside-flight\"",
            "\"sidecar-reconcile\"", "\"rewind-post-load\"", "\"merge-journal\"",
            "\"load-time-sweep\"", "\"test-batch-reconcile\"", "\"rewind-point-reap\"",
            "\"quit-without-saving-reap\"", "\"file-collection\"", "\"vessel-names-apply\"",
        };

        internal static List<string> ExpandedOnLoadTokens(string classText)
        {
            return ExpandedOnLoadTokens(classText, new HashSet<string>(StringComparer.Ordinal));
        }

        internal static List<string> ExpandedOnLoadTokens(string classText, HashSet<string> reached)
        {
            var declarations = PhaseDeclarations(classText);
            var result = new List<string>();
            Expand(classText, MethodBody(classText, OnLoadSignature), declarations, reached,
                new Stack<string>(), result);
            return result;
        }

        private static void Expand(
            string classText, string body, Dictionary<string, List<int>> declarations,
            HashSet<string> reached, Stack<string> active, List<string> output)
        {
            foreach (string token in Tokenize(body))
            {
                string phase = PhaseName(token);
                if (phase == null)
                {
                    output.Add(token);
                    continue;
                }
                Assert.True(declarations.ContainsKey(phase), "phase " + phase + " is not declared");
                Assert.False(active.Contains(phase), "phase " + phase + " recurses");
                reached.Add(phase);
                active.Push(phase);
                Expand(classText, MethodBody(classText, declarations[phase][0]), declarations,
                    reached, active, output);
                active.Pop();
            }
        }

        private static string PhaseName(string token)
        {
            if (!token.StartsWith("C:", StringComparison.Ordinal)) return null;
            string chain = token.Substring(2);
            if (chain.StartsWith("this.", StringComparison.Ordinal)) chain = chain.Substring(5);
            if (chain.IndexOf('.') >= 0) return null;
            return chain.StartsWith(PhasePrefix, StringComparison.Ordinal) ? chain : null;
        }

        /// <summary>
        /// Declaration index (the start of the name) of every method named
        /// <c>OnLoadPhase*</c>: a name followed by a parameter list and then a <c>{</c>.
        /// </summary>
        internal static Dictionary<string, List<int>> PhaseDeclarations(string classText)
        {
            string masked = Mask(classText);
            var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            int from = 0;
            while (true)
            {
                int at = masked.IndexOf(PhasePrefix, from, StringComparison.Ordinal);
                if (at < 0) break;
                from = at + PhasePrefix.Length;
                if (at > 0 && IsIdentChar(masked[at - 1])) continue;
                int end = at;
                while (end < masked.Length && IsIdentChar(masked[end])) end++;
                int p = SkipSpace(masked, end);
                if (p >= masked.Length || masked[p] != '(') continue;
                int close = MatchParen(masked, p);
                int q = SkipSpace(masked, close + 1);
                if (q >= masked.Length || masked[q] != '{') continue;
                string name = masked.Substring(at, end - at);
                if (!result.TryGetValue(name, out var list)) result[name] = list = new List<int>();
                list.Add(at);
            }
            return result;
        }

        private static int CountCalls(string masked, string name)
        {
            int count = 0, from = 0;
            while (true)
            {
                int at = masked.IndexOf(name, from, StringComparison.Ordinal);
                if (at < 0) return count;
                from = at + name.Length;
                if (at > 0 && IsIdentChar(masked[at - 1])) continue;
                if (from < masked.Length && IsIdentChar(masked[from])) continue;
                int p = SkipSpace(masked, from);
                if (p < masked.Length && masked[p] == '(') count++;
            }
        }

        /// <summary>The body (braces included) of the method whose signature text is given.</summary>
        internal static string MethodBody(string classText, string signature)
        {
            int at = classText.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "signature not found: " + signature);
            Assert.True(classText.IndexOf(signature, at + 1, StringComparison.Ordinal) < 0,
                "signature found twice: " + signature);
            return MethodBody(classText, at);
        }

        private static string MethodBody(string classText, int declarationIndex)
        {
            string masked = Mask(classText);
            int paren = masked.IndexOf('(', declarationIndex);
            int close = MatchParen(masked, paren);
            int open = SkipSpace(masked, close + 1);
            Assert.True(open < masked.Length && masked[open] == '{',
                "no method body after the declaration at " + declarationIndex);
            int depth = 0;
            for (int i = open; i < masked.Length; i++)
            {
                if (masked[i] == '{') depth++;
                else if (masked[i] == '}' && --depth == 0)
                    return classText.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces from " + open);
        }

        private static int MatchParen(string masked, int open)
        {
            int depth = 0;
            for (int i = open; i < masked.Length; i++)
            {
                if (masked[i] == '(') depth++;
                else if (masked[i] == ')' && --depth == 0) return i;
            }
            throw new InvalidOperationException("unbalanced parentheses from " + open);
        }

        /// <summary>
        /// The ordered token stream of <paramref name="source"/>: <c>S:</c> + a string literal
        /// as written, <c>C:</c> + an invoked dotted chain, <c>A:</c> + an assigned dotted
        /// chain. Comments and character literals are skipped.
        /// </summary>
        internal static List<string> Tokenize(string source)
        {
            var tokens = new List<string>();
            int i = 0, n = source.Length;
            while (i < n)
            {
                char c = source[i];
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    while (i < n && source[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    continue;
                }
                if (c == '\'')
                {
                    i = SkipCharLiteral(source, i);
                    continue;
                }
                if (IsStringStart(source, i))
                {
                    int end = SkipString(source, i);
                    tokens.Add("S:" + source.Substring(i, end - i));
                    i = end;
                    continue;
                }
                if (IsIdentStart(c) && (i == 0 || !IsIdentChar(source[i - 1])))
                {
                    int chainEnd = ReadChain(source, i, out string chain, out string last);
                    int p = SkipSpace(source, chainEnd);
                    p = SkipGenericArgs(source, p);
                    if (p < n && source[p] == '(' && !Keywords.Contains(last))
                    {
                        tokens.Add("C:" + chain);
                    }
                    else if (!Keywords.Contains(last) && IsAssignmentAt(source, SkipSpace(source, chainEnd)))
                    {
                        tokens.Add("A:" + chain);
                    }
                    i = chainEnd;
                    continue;
                }
                i++;
            }
            return tokens;
        }

        private static bool IsAssignmentAt(string s, int p)
        {
            if (p >= s.Length) return false;
            char c = s[p];
            char d = p + 1 < s.Length ? s[p + 1] : '\0';
            if (c == '=') return d != '=' && d != '>';
            if ((c == '+' || c == '-') && (d == '=' || d == c)) return true;
            return false;
        }

        private static int ReadChain(string s, int i, out string chain, out string last)
        {
            var sb = new StringBuilder();
            int n = s.Length;
            int end;
            while (true)
            {
                int start = i;
                while (i < n && IsIdentChar(s[i])) i++;
                last = s.Substring(start, i - start);
                sb.Append(last);
                end = i;
                int p = SkipSpace(s, i);
                string sep = null;
                if (p < n && s[p] == '.') sep = ".";
                else if (p + 1 < n && s[p] == '?' && s[p + 1] == '.') sep = "?.";
                if (sep == null) break;
                int q = SkipSpace(s, p + sep.Length);
                if (q >= n || !IsIdentStart(s[q])) break;
                sb.Append(sep);
                i = q;
            }
            chain = sb.ToString();
            return end;
        }

        private static int SkipGenericArgs(string s, int p)
        {
            if (p >= s.Length || s[p] != '<') return p;
            int depth = 0;
            for (int i = p; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') depth++;
                else if (c == '>') { if (--depth == 0) return SkipSpace(s, i + 1); }
                else if (!(IsIdentChar(c) || c == ',' || c == '.' || c == ' ' || c == '[' || c == ']' || c == '?'))
                    return p;
            }
            return p;
        }

        private static bool IsStringStart(string s, int i)
        {
            char c = s[i];
            if (c == '"') return true;
            if (c == '$' || c == '@')
            {
                int j = i;
                while (j < s.Length && j < i + 2 && (s[j] == '$' || s[j] == '@')) j++;
                return j < s.Length && s[j] == '"';
            }
            return false;
        }

        /// <summary>End index (exclusive) of the string literal starting at i, interpolation holes included.</summary>
        private static int SkipString(string s, int i)
        {
            bool verbatim = false, interpolated = false;
            while (s[i] == '$' || s[i] == '@')
            {
                if (s[i] == '$') interpolated = true; else verbatim = true;
                i++;
            }
            i++; // opening quote
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (verbatim && c == '"')
                {
                    if (i + 1 < n && s[i + 1] == '"') { i += 2; continue; }
                    return i + 1;
                }
                if (!verbatim && c == '\\') { i += 2; continue; }
                if (!verbatim && c == '"') return i + 1;
                if (interpolated && c == '{')
                {
                    if (i + 1 < n && s[i + 1] == '{') { i += 2; continue; }
                    i = SkipHole(s, i + 1);
                    continue;
                }
                i++;
            }
            return n;
        }

        private static int SkipHole(string s, int i)
        {
            int depth = 1, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '\'') { i = SkipCharLiteral(s, i); continue; }
                if (IsStringStart(s, i)) { i = SkipString(s, i); continue; }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i + 1;
                i++;
            }
            return n;
        }

        private static int SkipCharLiteral(string s, int i)
        {
            int n = s.Length;
            i++;
            while (i < n)
            {
                if (s[i] == '\\') { i += 2; continue; }
                if (s[i] == '\'') return i + 1;
                if (s[i] == '\n') return i;
                i++;
            }
            return n;
        }

        /// <summary>
        /// Length-preserving copy with comments and the contents of string and character
        /// literals (interpolation holes included) blanked, for brace and keyword scans.
        /// </summary>
        internal static string Mask(string source)
        {
            var sb = new StringBuilder(source);
            int i = 0, n = source.Length;
            while (i < n)
            {
                char c = source[i];
                int end = -1;
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    end = i;
                    while (end < n && source[end] != '\n') end++;
                }
                else if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = close < 0 ? n : close + 2;
                }
                else if (c == '\'') end = SkipCharLiteral(source, i);
                else if (IsStringStart(source, i)) end = SkipString(source, i);
                if (end < 0) { i++; continue; }
                for (int k = i; k < end; k++)
                {
                    if (sb[k] != '\n' && sb[k] != '\r') sb[k] = ' ';
                }
                i = end;
            }
            return sb.ToString();
        }

        private static int SkipSpace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static string GoldenPath()
        {
            return Path.Combine(RepoRoot(), "Source", "Parsek.Tests", "Fixtures",
                "SourceCharacterization", "ParsekScenario.OnLoad.tokens.txt");
        }

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }

        private static string ParsekSourceRoot()
        {
            string root = Path.Combine(RepoRoot(), "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }
    }
}
