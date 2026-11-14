using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>ParsekScenario</c> is one partial class. <c>scripts/refactor/split_parsekscenario.py</c>
    /// splits the text of <c>ParsekScenario.cs</c> into <c>ParsekScenario.cs</c> (every field,
    /// constant, auto-property, the Unity / ScenarioModule callbacks OnAwake, Update, OnSave,
    /// OnLoad and OnDestroy, the instance and test seams and the state-version bumps) and the
    /// <c>ParsekScenario.&lt;Area&gt;.cs</c> files it writes, listed in
    /// <see cref="SplitFileNames"/>. A source gate that read "ParsekScenario.cs" as a whole reads
    /// exactly that set as one text: the main file first, then the area files in ordinal
    /// file-name order, each file whole, so a member's body is never split across the join. The
    /// older siblings in <see cref="OtherPartialFileNames"/> were never part of that file and are
    /// NOT read, so a gate's scope stays what it was. Every <c>ParsekScenario.*.cs</c> on disk
    /// must be named in one of the two lists, or <see cref="Paths"/> throws naming the new file.
    /// A gate that pins which FILES hold a token (a walk over every source file) names the file
    /// that holds it now.
    /// </summary>
    internal static class ParsekScenarioSourceFiles
    {
        internal const string MainFileName = "ParsekScenario.cs";

        internal static readonly string[] SplitFileNames =
        {
            "ParsekScenario.AutoCommit.cs",
            "ParsekScenario.CommittedTreeCarry.cs",
            "ParsekScenario.Diagnostics.cs",
            "ParsekScenario.InertPassthrough.cs",
            "ParsekScenario.LoadLedgerRecalc.cs",
            "ParsekScenario.LoadSteps.cs",
            "ParsekScenario.QuickloadDiscard.cs",
            "ParsekScenario.RewindLoad.cs",
            "ParsekScenario.RewindStaging.cs",
            "ParsekScenario.Save.cs",
            "ParsekScenario.SceneTransitions.cs",
            "ParsekScenario.StockActionIntent.cs",
            "ParsekScenario.TestBatchReconcile.cs",
            "ParsekScenario.TreeLoad.cs",
            "ParsekScenario.TreeRestore.cs",
            "ParsekScenario.VesselLifecycle.cs",
            "ParsekScenario.VesselStrip.cs",
        };

        /// <summary>
        /// The older sibling partials, never part of the split file and not read. Listed so a
        /// NEW <c>ParsekScenario.*.cs</c> fails <see cref="Paths"/> until it is declared.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames =
        {
            "ParsekScenario.DiscardedFlightRestore.cs",
            "ParsekScenario.HydrationRepair.cs",
            "ParsekScenario.InSessionHandoff.cs",
            "ParsekScenario.LoadDroppedRecordings.cs",
            "ParsekScenario.RemovedReplayFlags.cs",
            "ParsekScenario.RevertLedgerSnapshot.cs",
            "ParsekScenario.RewindVesselHold.cs",
            "ParsekScenario.SessionReset.cs",
            "ParsekScenario.Trim.cs",
        };

        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            return PartialClassSourceFiles.Paths(
                parsekSourceDir,
                MainFileName,
                SplitFileNames,
                OtherPartialFileNames,
                nameof(ParsekScenarioSourceFiles));
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir).Select(File.ReadAllText));
        }

        /// <summary>
        /// <see cref="File.ReadAllText(string)"/> for a source reader that takes a path: the
        /// path of <c>ParsekScenario.cs</c> reads <see cref="ReadAll"/> (the text that was
        /// ParsekScenario.cs) with OnLoad's flow methods inlined (<see cref="InlineOnLoadFlow"/>),
        /// so OnLoad reads as the one method it was; any other path reads what
        /// <see cref="ParsekFlightSourceFiles.ReadText"/> reads (that one file, or the text that
        /// was ParsekFlight.cs). For a reader of ONE named file, never for a walk over every
        /// source file (a walk reads each file on its own).
        /// </summary>
        internal static string ReadText(string path)
        {
            if (string.Equals(Path.GetFileName(path), MainFileName, StringComparison.Ordinal))
                return InlineOnLoadFlow(ReadAll(Path.GetDirectoryName(path)));
            return ParsekFlightSourceFiles.ReadText(path);
        }

        /// <summary>Prefix of the methods OnLoad's body is split into (prologue, branches, tail).</summary>
        internal const string OnLoadFlowPrefix = "OnLoadFlow";

        private static readonly Regex FlowDeclaration = new Regex(
            @"(?m)^[ \t]*(?:private|internal)\s+(?:bool|void)\s+(" + OnLoadFlowPrefix + @"\w+)\s*\(");

        private static readonly Regex FlowCall = new Regex(@"\b" + OnLoadFlowPrefix + @"\w+\s*\(");

        /// <summary>
        /// The class text with every <c>OnLoadFlow*</c> call inside <c>OnLoad</c> replaced by
        /// that method's body (recursively, braces included) and the flow methods' own
        /// declarations (with their doc comments) removed, so a gate that reads OnLoad's body
        /// reads the whole load in execution order, as one method, the way it was written
        /// before the split. Works on raw and on comment-stripped / literal-masked text.
        /// </summary>
        internal static string InlineOnLoadFlow(string text)
        {
            string masked = ParsekScenarioOnLoadCharacterizationTests.Mask(text);
            var bodies = new Dictionary<string, Tuple<int, int>>(StringComparer.Ordinal);
            var removals = new List<Tuple<int, int>>();
            foreach (Match decl in FlowDeclaration.Matches(masked))
            {
                string name = decl.Groups[1].Value;
                int open = masked.IndexOf('{', MatchParen(masked, decl.Index + decl.Length - 1));
                int close = MatchBrace(masked, open);
                if (bodies.ContainsKey(name))
                    throw new InvalidOperationException(name + " is declared twice");
                bodies[name] = Tuple.Create(open, close);
                int start = LineStart(masked, decl.Index);
                // The declaration's doc comment goes with it (raw text keeps comments).
                while (start > 0)
                {
                    int prev = LineStart(text, start - 1);
                    if (!text.Substring(prev, start - prev).TrimStart().StartsWith("///", StringComparison.Ordinal))
                        break;
                    start = prev;
                }
                int end = close + 1;
                if (end < text.Length && text[end] == '\r') end++;
                if (end < text.Length && text[end] == '\n') end++;
                removals.Add(Tuple.Create(start, end));
            }
            if (bodies.Count == 0)
                return text;

            int sig = text.IndexOf(ParsekScenarioOnLoadCharacterizationTests.OnLoadSignature, StringComparison.Ordinal);
            if (sig < 0)
                return text;
            int onLoadOpen = masked.IndexOf('{', MatchParen(masked, masked.IndexOf('(', sig)));
            int onLoadClose = MatchBrace(masked, onLoadOpen);
            string expanded = Expand(text, masked, onLoadOpen, onLoadClose, bodies, new Stack<string>());

            var sb = new StringBuilder();
            int at = 0;
            var cuts = new List<Tuple<int, int, string>> { Tuple.Create(onLoadOpen, onLoadClose + 1, expanded) };
            foreach (var r in removals) cuts.Add(Tuple.Create(r.Item1, r.Item2, ""));
            foreach (var cut in cuts.OrderBy(c => c.Item1))
            {
                if (cut.Item1 < at)
                    throw new InvalidOperationException("an OnLoadFlow method overlaps OnLoad");
                sb.Append(text, at, cut.Item1 - at).Append(cut.Item3);
                at = cut.Item2;
            }
            sb.Append(text, at, text.Length - at);
            return sb.ToString();
        }

        private static string Expand(
            string text, string masked, int open, int close,
            Dictionary<string, Tuple<int, int>> bodies, Stack<string> active)
        {
            var sb = new StringBuilder();
            int at = open;
            foreach (Match call in FlowCall.Matches(masked.Substring(0, close + 1), open))
            {
                string name = call.Value.Substring(0, call.Value.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '(' }));
                Tuple<int, int> body;
                if (!bodies.TryGetValue(name, out body))
                    throw new InvalidOperationException("OnLoad calls " + name + ", which is not declared");
                if (active.Contains(name))
                    throw new InvalidOperationException(name + " recurses");
                int callEnd = MatchParen(masked, call.Index + call.Length - 1);
                sb.Append(text, at, call.Index - at);
                active.Push(name);
                sb.Append(Expand(text, masked, body.Item1, body.Item2, bodies, active));
                active.Pop();
                at = callEnd + 1;
            }
            sb.Append(text, at, close + 1 - at);
            return sb.ToString();
        }

        private static int LineStart(string s, int i)
        {
            while (i > 0 && s[i - 1] != '\n') i--;
            return i;
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

        private static int MatchBrace(string masked, int open)
        {
            int depth = 0;
            for (int i = open; i < masked.Length; i++)
            {
                if (masked[i] == '{') depth++;
                else if (masked[i] == '}' && --depth == 0) return i;
            }
            throw new InvalidOperationException("unbalanced braces from " + open);
        }
    }
}
