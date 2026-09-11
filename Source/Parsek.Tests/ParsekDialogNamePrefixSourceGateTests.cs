using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SOURCE-DERIVED gate over every <c>MultiOptionDialog</c> Parsek spawns: each popup's
    /// name must pass <see cref="TestCommandUiDialog.IsParsekDialogName"/>.
    ///
    /// <para><b>THE DEFECT THIS EXISTS FOR.</b> <c>UiAction op=dialog</c> finds the live
    /// Parsek popup by scanning for the <c>"Parsek"</c> NAME PREFIX, deliberately rather
    /// than by a hand-kept list of spawn sites - a list goes stale the first time a dialog
    /// is added, and the failure mode of a stale list is "no dialog open" reported over a
    /// live modal. The prefix scan has the same failure mode against a spawn site that
    /// simply does not carry the prefix, and one did:
    /// <c>Patches/GhostVesselLoadPatch.cs</c> named the flight-map ghost icon menu
    /// <c>"GhostIconMenu"</c>. A census step would have photographed that menu and had
    /// <c>open=false</c> beside the picture. Renaming it fixes today; this gate is what
    /// keeps the premise of the prefix scan mechanically TRUE, so the next dialog cannot
    /// reintroduce the hole.</para>
    ///
    /// <para><b>WHY THE PARSE STRIPS COMMENTS FIRST.</b> Several of these files discuss
    /// dialog names in prose (this very repo's design docs quote <c>"GhostIconMenu"</c>,
    /// and the seam's own headers name popups they do NOT spawn). A regex over raw source
    /// reads that prose as code: it would report names that are not spawned - failing on a
    /// correct tree - and, in the mirror direction, a commented-out decoy declaration would
    /// let a real violation hide behind it. Comment bytes are replaced with SPACES rather
    /// than deleted so every reported line index still points at the real line.</para>
    ///
    /// <para><b>BOTH ARGUMENT SHAPES.</b> The first constructor argument is a string
    /// literal at 16 of the 22 live sites and a <c>const string</c> identifier at the other
    /// six (<c>MergeDialog.DialogName</c> and friends). An identifier is resolved against
    /// its OWN file's <c>const string</c> declarations; an identifier that cannot be
    /// resolved fails the gate naming the site, rather than being skipped - a silent skip
    /// is how a prefix-less name would get back in.</para>
    /// </summary>
    public class ParsekDialogNamePrefixSourceGateTests
    {
        // Anti-vacuity floor for the live scan. Well under today's count (22 sites), high
        // enough that a parse which silently stopped matching would red instead of passing
        // over an empty set.
        private const int MinimumExpectedSites = 15;

        [Fact]
        public void EveryMultiOptionDialogSpawnedBySourceParsekCarriesTheParsekPrefix()
        {
            string sourceRoot = Path.Combine(ResolveRepoRoot(), "Source", "Parsek");
            Assert.True(Directory.Exists(sourceRoot), "source root not found: " + sourceRoot);

            var sites = new List<DialogSite>();
            foreach (string path in EnumerateSourceFiles(sourceRoot))
            {
                string text = File.ReadAllText(path);
                foreach (DialogSite site in FindDialogSites(text))
                {
                    sites.Add(new DialogSite
                    {
                        Name = site.Name,
                        Identifier = site.Identifier,
                        Line = site.Line,
                        File = MakeRelative(sourceRoot, path),
                    });
                }
            }

            Assert.True(sites.Count >= MinimumExpectedSites,
                "the MultiOptionDialog parse found only " + sites.Count + " spawn sites "
                + "(floor " + MinimumExpectedSites + "): the constructor moved or the "
                + "parse broke, and this gate would be vacuous.");

            var unresolved = sites
                .Where(s => s.Name == null)
                .Select(s => s.File + ":" + s.Line + " -> " + s.Identifier)
                .ToList();
            Assert.True(unresolved.Count == 0,
                "a MultiOptionDialog name argument is an identifier this gate cannot "
                + "resolve to a literal in its own file. Declare it as a `const string` "
                + "beside the spawn site (or pass the literal), so the prefix stays "
                + "checkable:\n  " + string.Join("\n  ", unresolved));

            var violations = sites
                .Where(s => !TestCommandUiDialog.IsParsekDialogName(s.Name))
                .Select(s => s.File + ":" + s.Line + " -> \"" + s.Name + "\"")
                .ToList();
            Assert.True(violations.Count == 0,
                "every Parsek MultiOptionDialog name must start with \""
                + TestCommandUiDialog.ParsekDialogNamePrefix + "\" or `UiAction op=dialog` "
                + "reports open=false over a live modal:\n  "
                + string.Join("\n  ", violations));
        }

        [Fact]
        public void TheRenamedGhostIconMenuIsTheOneThisGateWasWrittenFor()
        {
            // Named explicitly rather than left to the sweep above: the sweep passes on an
            // empty set of violations, which is also what it would report if the ghost icon
            // menu were deleted. This states the positive fact.
            string path = Path.Combine(ResolveRepoRoot(), "Source", "Parsek", "Patches",
                                       "GhostVesselLoadPatch.cs");
            Assert.True(File.Exists(path), "ghost icon menu spawn site moved: " + path);
            List<DialogSite> sites = FindDialogSites(File.ReadAllText(path));
            Assert.Single(sites);
            Assert.Equal("ParsekGhostIconMenu", sites[0].Name);
            Assert.True(TestCommandUiDialog.IsParsekDialogName(sites[0].Name));
        }

        // ----- anti-vacuity: the parse, driven over synthetic sources -----

        [Fact]
        public void ADecoyNameInAComment_IsNotASpawnSite()
        {
            // The green half. All four comment shapes carry a prefix-less name; a parse
            // that read them would fail this gate against a source that spawns nothing
            // wrong, which is the failure that makes a gate get deleted.
            string synthetic = string.Join("\n", new[]
            {
                "// new MultiOptionDialog(\"BadLineComment\", ...)",
                "/// <summary>new MultiOptionDialog(\"BadDocComment\") once existed.</summary>",
                "/* new MultiOptionDialog(\"BadBlockComment\") */",
                "void Spawn() {",
                "    /* leading */ var d = new MultiOptionDialog(\"ParsekReal\", \"\", \"t\");",
                "}",
            });
            List<DialogSite> sites = FindDialogSites(synthetic);
            Assert.Single(sites);
            Assert.Equal("ParsekReal", sites[0].Name);
            // Line indices survive comment stripping (1-based; the real spawn is line 5).
            Assert.Equal(5, sites[0].Line);
        }

        [Fact]
        public void ADecoyNameInCode_IsASpawnSiteAndFailsThePredicate()
        {
            // The red half, and it is the one that proves the gate can fail at all: same
            // name, same file, moved out of the comment.
            string synthetic = string.Join("\n", new[]
            {
                "// A comment about new MultiOptionDialog(\"BadBlockComment\").",
                "void Spawn() {",
                "    var d = new MultiOptionDialog(\"BadBlockComment\", \"\", \"t\");",
                "}",
            });
            List<DialogSite> sites = FindDialogSites(synthetic);
            Assert.Single(sites);
            Assert.Equal("BadBlockComment", sites[0].Name);
            Assert.False(TestCommandUiDialog.IsParsekDialogName(sites[0].Name));
            Assert.Equal(3, sites[0].Line);
        }

        [Fact]
        public void AStringLiteralIsNeverTreatedAsAComment()
        {
            // The mirror direction of the comment strip: a `//` or `/*` INSIDE a string
            // must not open a comment, or the stripper would swallow the rest of the file
            // and the gate would go quietly vacuous.
            string synthetic = string.Join("\n", new[]
            {
                "string url = \"http://example.com/*not a comment*/\";",
                "var d = new MultiOptionDialog(\"ParsekAfterTheUrl\", \"\", \"t\");",
            });
            List<DialogSite> sites = FindDialogSites(synthetic);
            Assert.Single(sites);
            Assert.Equal("ParsekAfterTheUrl", sites[0].Name);
            Assert.Equal(2, sites[0].Line);
        }

        [Fact]
        public void AConstIdentifierArgumentIsResolvedAgainstItsOwnFile()
        {
            string synthetic = string.Join("\n", new[]
            {
                "// private const string DialogName = \"DecoyNotParsek\";",
                "internal const string DialogName = \"ParsekViaConst\";",
                "var d = new MultiOptionDialog(",
                "    DialogName,",
                "    \"\", \"t\");",
            });
            List<DialogSite> sites = FindDialogSites(synthetic);
            Assert.Single(sites);
            Assert.Equal("ParsekViaConst", sites[0].Name);
            Assert.Equal("DialogName", sites[0].Identifier);
        }

        [Fact]
        public void AnUnresolvableIdentifierArgumentIsReportedRatherThanSkipped()
        {
            string synthetic = "var d = new MultiOptionDialog(SomeUnknownName, \"\", \"t\");";
            List<DialogSite> sites = FindDialogSites(synthetic);
            Assert.Single(sites);
            Assert.Null(sites[0].Name);
            Assert.Equal("SomeUnknownName", sites[0].Identifier);
        }

        // ----- the parse -----

        internal struct DialogSite
        {
            /// <summary>The resolved popup name, or null when the argument was an
            /// identifier no <c>const string</c> in the same file declares.</summary>
            internal string Name;

            /// <summary>The identifier the argument named, or null when it was a literal.
            /// Kept so an unresolved site can be reported by the name a reader will
            /// grep.</summary>
            internal string Identifier;

            /// <summary>1-based line of the <c>new MultiOptionDialog(</c> token.</summary>
            internal int Line;

            internal string File;
        }

        private const string CtorToken = "new MultiOptionDialog(";

        internal static List<DialogSite> FindDialogSites(string text)
        {
            string code = StripComments(text);
            Dictionary<string, string> consts = ParseStringConstants(code);
            var sites = new List<DialogSite>();
            int at = 0;
            while (true)
            {
                at = code.IndexOf(CtorToken, at, StringComparison.Ordinal);
                if (at < 0) return sites;
                int line = LineOf(code, at);
                int i = at + CtorToken.Length;
                while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
                string name = null;
                string identifier = null;
                if (i < code.Length && code[i] == '"')
                {
                    name = ReadStringLiteral(code, i);
                }
                else
                {
                    int start = i;
                    while (i < code.Length
                           && (char.IsLetterOrDigit(code[i]) || code[i] == '_'
                               || code[i] == '.'))
                        i++;
                    if (i > start)
                    {
                        identifier = code.Substring(start, i - start);
                        // A qualified `Type.Member` resolves on its last segment: the
                        // constant table is keyed by declared name.
                        string key = identifier.Substring(identifier.LastIndexOf('.') + 1);
                        string resolved;
                        if (consts.TryGetValue(key, out resolved)) name = resolved;
                    }
                }
                sites.Add(new DialogSite { Name = name, Identifier = identifier, Line = line });
                at += CtorToken.Length;
            }
        }

        /// <summary>Every <c>const string NAME = "literal";</c> in one comment-free source,
        /// by declared name. A name declared twice in one file resolves to nothing, so an
        /// ambiguous site is REPORTED rather than resolved to whichever came first.</summary>
        private static Dictionary<string, string> ParseStringConstants(string code)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            const string marker = "const string ";
            int at = 0;
            while (true)
            {
                at = code.IndexOf(marker, at, StringComparison.Ordinal);
                if (at < 0) break;
                int i = at + marker.Length;
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                    i++;
                string name = code.Substring(start, i - start);
                while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
                if (name.Length > 0 && i < code.Length && code[i] == '=')
                {
                    i++;
                    while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
                    if (i < code.Length && code[i] == '"')
                    {
                        string value = ReadStringLiteral(code, i);
                        if (found.ContainsKey(name) && found[name] != value)
                            ambiguous.Add(name);
                        else
                            found[name] = value;
                    }
                }
                at += marker.Length;
            }
            foreach (string name in ambiguous) found.Remove(name);
            return found;
        }

        /// <summary>Reads the string literal starting at <paramref name="open"/> (a
        /// <c>"</c>), honouring backslash escapes. Returns null for an unterminated
        /// one.</summary>
        private static string ReadStringLiteral(string code, int open)
        {
            var sb = new StringBuilder();
            for (int i = open + 1; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '\\')
                {
                    if (i + 1 < code.Length) sb.Append(code[++i]);
                    continue;
                }
                if (c == '"') return sb.ToString();
                if (c == '\n') return null;
                sb.Append(c);
            }
            return null;
        }

        /// <summary>
        /// Replaces every <c>//</c>, <c>///</c> and <c>/* */</c> comment byte with a space,
        /// leaving newlines (and therefore every line index) exactly where they were.
        /// String and char literals are tracked so a <c>//</c> inside one does not open a
        /// comment - see <c>AStringLiteralIsNeverTreatedAsAComment</c>.
        /// </summary>
        internal static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    // Verbatim string: runs to a `"` that is not doubled.
                    sb.Append(c).Append('"');
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                sb.Append("\"\"");
                                i += 2;
                                continue;
                            }
                            sb.Append('"');
                            i++;
                            break;
                        }
                        sb.Append(text[i]);
                        i++;
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    sb.Append(c);
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            sb.Append(text[i]).Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        sb.Append(text[i]);
                        bool closed = text[i] == quote || text[i] == '\n';
                        i++;
                        if (closed) break;
                    }
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    sb.Append("  ");
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                        {
                            sb.Append("  ");
                            i += 2;
                            break;
                        }
                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        // ----- plumbing -----

        private static IEnumerable<string> EnumerateSourceFiles(string sourceRoot)
        {
            foreach (string path in Directory.GetFiles(sourceRoot, "*.cs",
                                                       SearchOption.AllDirectories))
            {
                string rel = MakeRelative(sourceRoot, path);
                if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return path;
            }
        }

        private static string MakeRelative(string root, string path)
        {
            string rel = path.Substring(root.Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Replace(Path.DirectorySeparatorChar, '/');
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
