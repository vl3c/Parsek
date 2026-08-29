using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the TS-FLUSHED-SAVE-DROPS-DEBRIS-TERMINALSTATE fix.
    ///
    /// <para>The fix is a PAIR of statements in two different loops of
    /// <c>ParsekScenario.OnLoad</c> — an instance method that only runs under Unity and
    /// cannot be driven headlessly. Every behavioural cell in
    /// <see cref="SceneChangeTerminalStatePreservationTests"/> calls the two helpers
    /// directly, so the whole xUnit suite stays green under at least five separate
    /// unwirings that each restore the measured defect: clearing the id set between the
    /// loops, commenting out the restore call, renaming the saved-node lookup the restore
    /// reads, wrapping the restore loop in a gate that never opens, or hoisting the restore
    /// above the reset that fills the set. This gate is what closes that hole.</para>
    ///
    /// <para>Mechanism mirrors <see cref="SoiSeamProducerWiringGateTests"/>: read the
    /// source, strip BOTH comment forms (a block comment around the call is one of the
    /// mutations), brace-match the OnLoad body, and assert over that scope only. On top of
    /// the substring pins it walks the enclosing block headers of each site, so an extra
    /// wrapper or a moved loop shows up as a changed chain or a changed depth rather than
    /// passing unnoticed. Headers are whitespace-collapsed, so re-indenting or joining
    /// lines cannot false-red it.</para>
    /// </summary>
    public class SceneChangeTerminalStateWiringGateTests
    {
        private const string ScenarioPath = "ParsekScenario.cs";
        private const string OnLoadSignature = "public override void OnLoad(ConfigNode node)";

        private const string CaptureAnchor =
            "clearedPostSpawnTerminalIds.Add(recordings[i].RecordingId);";
        private const string ConsumeAnchor =
            "RestoreClearedPostSpawnTerminalState(";
        private const string ResidueAnchor =
            "post-spawn terminal verdict(s) ";

        private const string RecordingsLoopHeader =
            "for (int i = 0; i < recordings.Count; i++)";

        // ────────────────────────────────────────────────────────────
        //  Capture leg
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ResetLoopCapturesTheIdsItRetracted()
        {
            string body = OnLoadBody();

            Assert.Equal(1, Occurrences(body, CaptureAnchor));
            Assert.Contains(
                "if (ClearPostSpawnTerminalState(recordings[i], \"tree recording\") "
                + "&& !string.IsNullOrEmpty(recordings[i].RecordingId)) "
                + CaptureAnchor,
                Flatten(body));

            // The capture must sit inside the per-recording reset loop — a capture hoisted
            // out of it would record nothing.
            IReadOnlyList<string> chain = EnclosingHeaders(body, body.IndexOf(CaptureAnchor, StringComparison.Ordinal));
            Assert.Equal(RecordingsLoopHeader, chain[chain.Count - 1]);
        }

        // ────────────────────────────────────────────────────────────
        //  Consumption leg
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void RestoreRunsInsideTheSavedNodeMatchLoop_GatedOffRevert()
        {
            string body = OnLoadBody();

            Assert.Equal(1, Occurrences(body, ConsumeAnchor));
            Assert.Contains(
                "if (!isRevert) RestoreClearedPostSpawnTerminalState( "
                + "recordings[i], savedTreeRecNode, "
                + "clearedPostSpawnTerminalIds, \"tree recording\");",
                Flatten(body));

            // The exact block chain the restore must sit in. A gate that never opens, a
            // relocated call, or a different matching loop all change this list.
            IReadOnlyList<string> chain = EnclosingHeaders(body, body.IndexOf(ConsumeAnchor, StringComparison.Ordinal));
            Assert.Equal(
                new[]
                {
                    "if (savedTreeNodes.Length > 0)",
                    "foreach (var savedTreeNode in savedTreeNodes)",
                    "foreach (var savedTreeRecNode in savedTreeRecNodes)",
                    RecordingsLoopHeader,
                    "if (recordings[i].RecordingId == savedRecId)",
                },
                Tail(chain, 5));
        }

        [Fact]
        public void RestoreReadsTheSavedRecordingNodesByTheirRealNames()
        {
            // Renaming either lookup silently empties the restore loop while every
            // behavioural cell (which hands the helper a hand-built node) stays green.
            string flat = Flatten(OnLoadBody());

            Assert.Contains("ConfigNode[] savedTreeNodes = node.GetNodes(\"RECORDING_TREE\");", flat);
            Assert.Contains("ConfigNode[] savedTreeRecNodes = savedTreeNode.GetNodes(\"RECORDING\");", flat);
        }

        // ────────────────────────────────────────────────────────────
        //  The pair: order, reachability, and nothing wiping the set
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void TheSetIsFilledBeforeItIsConsumed_AndTheRestoreGainsNoExtraWrapper()
        {
            string body = OnLoadBody();
            int capture = body.IndexOf(CaptureAnchor, StringComparison.Ordinal);
            int consume = body.IndexOf(ConsumeAnchor, StringComparison.Ordinal);

            Assert.True(capture >= 0 && consume >= 0, "wiring gate: both anchors must exist");
            Assert.True(capture < consume,
                "wiring gate: the reset loop must FILL clearedPostSpawnTerminalIds before the "
                + "restore loop reads it. Hoisting the restore above the reset leaves the set "
                + "empty at read time and silently restores TS-FLUSHED-SAVE-DROPS-DEBRIS-TERMINALSTATE.");

            // Reset loop and restore block are siblings: the restore sits exactly four
            // blocks deeper than the capture (savedTreeNodes gate, two foreach, the
            // recordings loop, the id match — minus the capture's own recordings loop).
            // Any additional enclosing block — notably a gate that never opens — moves this.
            int captureDepth = EnclosingHeaders(body, capture).Count;
            int consumeDepth = EnclosingHeaders(body, consume).Count;
            Assert.True(consumeDepth == captureDepth + 4,
                "wiring gate: expected the restore to sit 4 blocks deeper than the capture, "
                + $"got capture={captureDepth} consume={consumeDepth}. An extra enclosing block "
                + "(a dead outer gate) or a moved loop breaks reachability without breaking any "
                + "behavioural cell.");
        }

        [Fact]
        public void NothingWipesOrRebindsTheSetBetweenTheTwoLegs()
        {
            string body = OnLoadBody();

            // Exactly one binding: the declaration. A second assignment between the loops
            // hands the restore a fresh empty set.
            Assert.Equal(1, Occurrences(Flatten(body), "clearedPostSpawnTerminalIds = "));
            Assert.Contains("var clearedPostSpawnTerminalIds = new HashSet<string>();", Flatten(body));

            // No bulk mutator anywhere in OnLoad. `.Add` (capture) and the pass-by-reference
            // into the restore helper are the only legitimate mutations; the helper's own
            // `.Remove` lives in the helper, not here.
            foreach (string mutator in new[]
                     {
                         ".Clear()", ".Remove(", ".RemoveWhere(",
                         ".ExceptWith(", ".IntersectWith(", ".SymmetricExceptWith(",
                     })
            {
                Assert.False(body.Contains("clearedPostSpawnTerminalIds" + mutator),
                    "wiring gate: OnLoad must not call clearedPostSpawnTerminalIds" + mutator
                    + " — emptying the set between the reset and the restore restores "
                    + "TS-FLUSHED-SAVE-DROPS-DEBRIS-TERMINALSTATE with every behavioural cell green.");
            }
        }

        [Fact]
        public void TheUnrestoredResidueIsReportedAfterTheRestoreLoop()
        {
            // CLAUDE.md logging requirement, and the only in-log evidence of a verdict that
            // stayed cleared. It must run AFTER the restore loop (else it reports the
            // pre-restore set) and at the branch's own level, not buried inside a loop.
            string body = OnLoadBody();
            int consume = body.IndexOf(ConsumeAnchor, StringComparison.Ordinal);
            int residue = body.IndexOf(ResidueAnchor, StringComparison.Ordinal);

            Assert.True(residue > consume,
                "wiring gate: the residue summary must be emitted after the restore loop.");
            Assert.Contains("if (clearedPostSpawnTerminalIds.Count > 0)", Flatten(body));
            Assert.Contains("isRevert=", Flatten(body));

            int residueDepth = EnclosingHeaders(body, residue).Count;
            int captureDepth = EnclosingHeaders(body, body.IndexOf(CaptureAnchor, StringComparison.Ordinal)).Count;
            Assert.True(residueDepth == captureDepth - 1,
                "wiring gate: the residue summary must sit at the in-session branch's own "
                + $"level (expected depth {captureDepth - 1}, got {residueDepth}).");
        }

        // ---- helpers (mirror SoiSeamProducerWiringGateTests) ----

        private static string OnLoadBody()
        {
            string src = StripComments(ReadParsekSource(ScenarioPath));

            int sigIdx = src.IndexOf(OnLoadSignature, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "wiring gate: OnLoad signature not found in " + ScenarioPath);
            Assert.True(src.IndexOf(OnLoadSignature, sigIdx + 1, StringComparison.Ordinal) < 0,
                "wiring gate: OnLoad signature is ambiguous in " + ScenarioPath);

            int open = src.IndexOf('{', sigIdx);
            Assert.True(open >= 0, "wiring gate: no OnLoad body found");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return src.Substring(open, i - open + 1);
                }
            }

            Assert.True(false, "wiring gate: unbalanced braces in OnLoad");
            return null;
        }

        /// <summary>
        /// The chain of enclosing block headers at <paramref name="index"/>, outermost
        /// first, each whitespace-collapsed. Semicolons at paren depth 0 reset the pending
        /// header so a `for (a; b; c)` header survives intact. String literals are left in
        /// place deliberately: interpolation holes and `{{`/`}}` escapes are brace-balanced,
        /// so they cannot shift the depth, and keeping them lets the pins above assert on
        /// the real node-name literals.
        /// </summary>
        private static IReadOnlyList<string> EnclosingHeaders(string body, int index)
        {
            var stack = new List<string>();
            int last = 0, paren = 0;
            for (int i = 0; i < index && i < body.Length; i++)
            {
                char c = body[i];
                if (c == '(') paren++;
                else if (c == ')') { if (paren > 0) paren--; }
                else if (c == '{')
                {
                    stack.Add(Flatten(body.Substring(last, i - last)));
                    last = i + 1;
                }
                else if (c == '}')
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    last = i + 1;
                }
                else if (c == ';' && paren == 0) last = i + 1;
            }
            return stack;
        }

        private static string[] Tail(IReadOnlyList<string> chain, int count)
        {
            Assert.True(chain.Count >= count,
                $"wiring gate: expected at least {count} enclosing blocks, got {chain.Count}");
            var tail = new string[count];
            for (int i = 0; i < count; i++)
                tail[i] = chain[chain.Count - count + i];
            return tail;
        }

        private static string Flatten(string s)
        {
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static int Occurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
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

        /// <summary>
        /// Strips BOTH comment forms. The line-comment strip is what
        /// <see cref="SoiSeamProducerWiringGateTests"/> does; the BLOCK strip is load-bearing
        /// here, because `/* ... */` around the restore call is one of the unwirings this
        /// gate exists to catch, and a comment-blind scan would still see the text.
        /// </summary>
        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            int i = 0, n = source.Length;
            while (i < n)
            {
                if (source[i] == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    int j = source.IndexOf('\n', i);
                    i = j < 0 ? n : j;
                    continue;
                }
                if (source[i] == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int j = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = j < 0 ? n : j + 2;
                    continue;
                }
                sb.Append(source[i]);
                i++;
            }
            return sb.ToString();
        }
    }
}
