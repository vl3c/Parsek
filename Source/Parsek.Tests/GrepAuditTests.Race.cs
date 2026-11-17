using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The race grep-audit gates (multiplayer design 15.4, plan R1-06), all on ONE shared managed
    /// scanner, <see cref="RaceSourceScan"/>, which later race gates reuse.
    ///
    /// <para>The race files are every <c>.cs</c> file under <c>Source/Parsek/RaceCore/</c>,
    /// <c>RaceExport/</c> and <c>RaceWorld/</c> (each folder that exists; the last two arrive with
    /// R3E-02 and R3W-02 and are picked up with no edit here) and
    /// <c>Source/Parsek/Controllers/ParsekRace*.cs</c>. The scan lexes each file: comments become
    /// blanks, string and char literal CONTENTS become blanks (interpolation holes stay code), so a
    /// comment that explains a rule or a log message that names a store never reads as a
    /// reference. A <c>using X = ...</c> alias and a backslash in code (a unicode-escaped
    /// identifier) are refused outright in every gate: either defeats a name-based gate.</para>
    ///
    /// <para>Gates: <c>scripts/grep-audit-race-core-no-store.ps1</c> (RaceCore names no type whose
    /// name ends in <c>Store</c>), <c>scripts/grep-audit-race-no-writer.ps1</c> with
    /// <c>scripts/race-no-writer-allowlist.txt</c> (no race file names a store of the save or a
    /// persistence writer of the save, except a listed file for a listed reason) and
    /// <c>scripts/grep-audit-race-no-recorder.ps1</c> (no race file names a recorder, the
    /// recording store, the pending-tree slot or the tree commit and discard services). Each runs
    /// its pwsh script when pwsh is on PATH and the managed scan here otherwise. The fourth rule of
    /// design 15.4, "Recording types only in the loader", is an architecture-suite cell
    /// (<c>scripts/arch/test_archview.py</c>, <c>RaceLoaderFilesTests</c>), because a grep cannot
    /// know which module a type belongs to. The race log tag list is <see cref="RaceLogTags"/>.</para>
    /// </summary>
    public partial class GrepAuditTests
    {
        // ------------------------------------------------------------------ the scan set

        /// <summary>The race module folders, each scanned when it exists. Mirrored by
        /// <c>$raceModuleFolders</c> in <c>scripts/race-source-scan.ps1</c>.</summary>
        internal static readonly string[] RaceModuleFolders = { "RaceCore", "RaceExport", "RaceWorld" };

        /// <summary>The race entry-layer files in <c>Controllers/</c>. Mirrored by
        /// <c>$raceControllerPattern</c> in <c>scripts/race-source-scan.ps1</c>.</summary>
        internal const string RaceControllerPattern = "ParsekRace*.cs";

        /// <summary>The fewest <c>Type.</c> references the whole scan may see before it is called
        /// broken (measured 2026-11-17: 30 over the four R1-03 files; it only grows as the race
        /// modules fill). Mirrored by the <c>MinTypeReferences</c> default of the three scripts.</summary>
        internal const int RaceMinTypeReferences = 24;

        /// <summary>The fewest RaceCore files the scan may see (R1-03 created three).</summary>
        internal const int RaceMinRaceCoreFiles = 3;

        /// <summary>The recorder ban (design 15.4, plan OV-12). Mirrored by
        /// <c>$recorderTypes</c> in <c>scripts/grep-audit-race-no-recorder.ps1</c>.</summary>
        internal static readonly string[] RaceRecorderTypes =
        {
            "FlightRecorder", "BackgroundRecorder", "RecordingStore",
            "PendingTreeSlot", "TreeCommitService", "TreeDiscardService",
        };

        /// <summary>A RaceCore identifier that names a store. Mirrored by <c>$storePattern</c> in
        /// <c>scripts/grep-audit-race-core-no-store.ps1</c>.</summary>
        internal const string RaceStorePattern = @"(?<![A-Za-z0-9_])([A-Z][A-Za-z0-9_]*Store)(?![A-Za-z0-9_])";

        /// <summary>The race log tags (plan OV-57): one plain tag per subsystem, never a nested
        /// form. A task that first logs under a new tag adds it here. In-game test files
        /// (<c>InGameTests/</c>) are tooling, not race files, and keep their own tags.</summary>
        internal static readonly string[] RaceLogTags = { "Race", "RivalLane" };

        internal const string RaceNoWriterAllowlist = "race-no-writer-allowlist.txt";

        private static readonly Regex RaceAliasUsing = new Regex(
            @"^\s*(?:global\s+)?using\s+@?([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.CultureInvariant);

        internal sealed class RaceFile
        {
            public string Rel;
            public string Module;
            public string MaskedText;
            public string KeptText;
            public string[] Masked;
            public string[] Raw;   // the file's text, line for line with Masked (BOM stripped, CR normalized)
            // Line index -> directive word of each preprocessor line whose directive is not allowed.
            public Dictionary<int, string> RefusedDirectives = new Dictionary<int, string>();
        }

        /// <summary>The preprocessor directives a race file may use. A conditional directive
        /// (<c>#if</c>, <c>#elif</c>, <c>#else</c>, <c>#endif</c>) is refused because the compiler
        /// does not lex a disabled branch while the lexer cannot know which branch is disabled: a
        /// <c>/*</c> or <c>@"</c> inside <c>#if false</c> would open a comment or string over the
        /// active code after <c>#endif</c>. Every other directive word is refused too (an unknown
        /// one is a compile error anyway). Mirrored by <c>$raceAllowedDirectives</c> in
        /// <c>scripts/race-source-scan.ps1</c>.</summary>
        internal static readonly string[] RaceAllowedDirectives = { "region", "endregion", "pragma", "nullable" };

        internal sealed class RaceScanResult
        {
            public readonly List<RaceFile> Files = new List<RaceFile>();
            public int ModuleFiles;
            public int ControllerFiles;
            public int TypeReferences;
        }

        /// <summary>
        /// THE shared race scan: enumerates the race files of <paramref name="repoRoot"/> and lexes
        /// each one. Throws on a file the lexer cannot model (a raw string literal, an unterminated
        /// literal or comment), so a gate fails closed rather than reading half a file.
        /// </summary>
        internal static RaceScanResult RaceSourceScan(string repoRoot)
        {
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            if (!Directory.Exists(sourceRoot))
                throw new InvalidOperationException("race scan: source root not found: " + sourceRoot);
            string rootNorm = repoRoot.Replace('\\', '/').TrimEnd('/');
            var paths = new List<KeyValuePair<string, string>>();
            foreach (string folder in RaceModuleFolders)
            {
                string dir = Path.Combine(sourceRoot, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (string p in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                    paths.Add(new KeyValuePair<string, string>(p, folder));
            }
            string controllers = Path.Combine(sourceRoot, "Controllers");
            if (Directory.Exists(controllers))
            {
                foreach (string p in Directory.EnumerateFiles(controllers, RaceControllerPattern, SearchOption.TopDirectoryOnly))
                    paths.Add(new KeyValuePair<string, string>(p, "Controllers"));
            }

            var result = new RaceScanResult();
            foreach (var entry in paths)
            {
                string rel = entry.Key.Replace('\\', '/');
                if (rel.StartsWith(rootNorm + "/", StringComparison.Ordinal))
                    rel = rel.Substring(rootNorm.Length + 1);
                result.Files.Add(RaceLexFile(rel, entry.Value, RaceReadText(entry.Key)));
            }
            result.Files.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
            foreach (RaceFile f in result.Files)
            {
                if (f.Module == "Controllers") result.ControllerFiles++;
                else result.ModuleFiles++;
                foreach (string line in f.Masked)
                    result.TypeReferences += GuiMockTypeRef.Matches(line).Count;
            }
            return result;
        }

        /// <summary>A race file's text: its bytes decoded as UTF-8 (an invalid sequence becomes
        /// U+FFFD, which the raw check refuses), one leading U+FEFF byte-order mark stripped.
        /// Mirrored by <c>Get-RaceText</c> in <c>scripts/race-source-scan.ps1</c>.</summary>
        internal static string RaceReadText(string path)
        {
            string text = new UTF8Encoding(false, false).GetString(File.ReadAllBytes(path));
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        /// <summary>True for a character a race file may hold: tab, LF, CR and 0x20..0x7E. The
        /// compiler reads more characters than the lexer models as line ends (U+0085, U+2028,
        /// U+2029) and as blanks (U+001A, form feed, vertical tab, the Unicode spaces), and drops
        /// format characters (U+00AD) from identifiers; refusing every other character on the RAW
        /// text, before any masking, covers all of them at once.</summary>
        internal static bool RaceAllowedChar(char c)
        {
            return c == '\t' || c == '\n' || c == '\r' || (c >= ' ' && c <= '~');
        }

        /// <summary>The refusal of the first character of a raw line the race files may not hold
        /// (<c>[control] U+XXXX</c> below 0x80, <c>[non-ascii] U+XXXX</c> above), or null.</summary>
        internal static string RaceRawRefusal(string rawLine)
        {
            foreach (char ch in rawLine)
            {
                if (RaceAllowedChar(ch)) continue;
                return (ch < 0x80 ? "[control] U+" : "[non-ascii] U+")
                    + ((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
            }
            return null;
        }

        internal static RaceFile RaceLexFile(string rel, string module, string text)
        {
            string raw = text.Replace("\r\n", "\n").Replace('\r', '\n');
            // A refused character is a blank to the lexer (it is reported on its raw line, so the
            // gate is red whatever the lexer then reads); line breaks and length are unchanged.
            var lexInput = new StringBuilder(raw.Length);
            foreach (char ch in raw) lexInput.Append(RaceAllowedChar(ch) ? ch : ' ');
            string masked, kept;
            List<KeyValuePair<int, string>> directives;
            RaceLex(lexInput.ToString(), out masked, out kept, out directives, rel);
            var refused = new Dictionary<int, string>();
            foreach (var d in directives)
            {
                if (Array.IndexOf(RaceAllowedDirectives, d.Value) >= 0) continue;
                int line = 0;
                for (int j = 0; j < d.Key; j++) if (raw[j] == '\n') line++;
                if (!refused.ContainsKey(line)) refused.Add(line, d.Value);
            }
            return new RaceFile
            {
                Rel = rel,
                Module = module,
                MaskedText = masked,
                KeptText = kept,
                Masked = masked.Split('\n'),
                Raw = raw.Split('\n'),
                RefusedDirectives = refused,
            };
        }

        // ------------------------------------------------------------------ the lexer

        /// <summary>
        /// Lexes C# source into two texts of the same length and the same line breaks: in
        /// <paramref name="masked"/> comments and the contents of string and char literals are
        /// blanks (quotes and interpolation holes stay); in <paramref name="kept"/> only the
        /// comments are blanks. The column of a token is the same in both, which is how the log
        /// tag cell reads a literal argument found in the masked text. Mirrored character for
        /// character by <c>ConvertTo-RaceLexed</c> in <c>scripts/race-source-scan.ps1</c>.
        /// </summary>
        internal static void RaceLex(string text, out string masked, out string kept, string rel = "<input>")
        {
            List<KeyValuePair<int, string>> directives;
            RaceLex(text, out masked, out kept, out directives, rel);
        }

        /// <summary><see cref="RaceLex(string, out string, out string, string)"/>, also returning each
        /// preprocessor line the lexer met in code state: its position and its directive word
        /// (the letters after the '#' and any blanks; empty when there are none).</summary>
        internal static void RaceLex(string text, out string masked, out string kept,
            out List<KeyValuePair<int, string>> directives, string rel)
        {
            var lexer = new RaceLexer(text.Replace("\r\n", "\n").Replace('\r', '\n'), rel);
            lexer.Code(false);
            masked = lexer.M.ToString();
            kept = lexer.K.ToString();
            directives = lexer.Directives;
        }

        private sealed class RaceLexer
        {
            private static readonly char[] Special = { '/', '\'', '"', '$', '@', '{', '}', '#' };
            // In a hole the brackets and ':' matter too: a ':' outside every bracket starts the
            // format clause, which is literal text up to the closing '}'.
            private static readonly char[] HoleSpecial = { '/', '\'', '"', '$', '@', '{', '}', '#', '(', ')', '[', ']', ':' };
            private readonly string t;
            private readonly string rel;
            private int i;
            internal readonly StringBuilder M;
            internal readonly StringBuilder K;
            internal readonly List<KeyValuePair<int, string>> Directives = new List<KeyValuePair<int, string>>();

            internal RaceLexer(string text, string rel)
            {
                t = text;
                this.rel = rel;
                M = new StringBuilder(text.Length);
                K = new StringBuilder(text.Length);
            }

            private InvalidDataException Fail(string what)
            {
                int line = 1;
                for (int j = 0; j < i && j < t.Length; j++) if (t[j] == '\n') line++;
                return new InvalidDataException("race scan: " + what + " at " + rel + ":" + line
                    + "; the lexer does not model it, so the gate fails closed");
            }

            private void Both(char c) { M.Append(c); K.Append(c); }

            private void Blank(char c) { M.Append(c == '\n' ? '\n' : ' '); K.Append(c); }

            private char At(int j) { return j < t.Length ? t[j] : '\0'; }

            // True when only blanks stand between the start of the line and position p.
            private bool AtLineStart(int p)
            {
                int j = p - 1;
                while (j >= 0 && (t[j] == ' ' || t[j] == '\t' || t[j] == '\v' || t[j] == '\f')) j--;
                return j < 0 || t[j] == '\n';
            }

            private void BlankToEndOfLine()
            {
                while (i < t.Length && t[i] != '\n') { M.Append(' '); K.Append(' '); i++; }
            }

            // Code until the end of the text, or (in a hole) until an unmatched '}', not consumed.
            // A preprocessor line (a '#' first on its line) is blanked like a comment: read as
            // code, `#region notes /*` would open a block comment over the real code below it.
            internal void Code(bool inHole)
            {
                int depth = 0;
                char[] special = inHole ? HoleSpecial : Special;
                while (i < t.Length)
                {
                    int next = t.IndexOfAny(special, i);
                    if (next < 0) next = t.Length;
                    if (next > i)
                    {
                        M.Append(t, i, next - i);
                        K.Append(t, i, next - i);
                        i = next;
                        continue;
                    }
                    char c = t[i];
                    char n = At(i + 1);
                    if (c == '#' && AtLineStart(i))
                    {
                        int w = i + 1;
                        while (w < t.Length && (t[w] == ' ' || t[w] == '\t')) w++;
                        int start = w;
                        while (w < t.Length && ((t[w] >= 'a' && t[w] <= 'z') || (t[w] >= 'A' && t[w] <= 'Z'))) w++;
                        Directives.Add(new KeyValuePair<int, string>(i, t.Substring(start, w - start)));
                        BlankToEndOfLine();
                        continue;
                    }
                    if (c == '/' && n == '/')
                    {
                        BlankToEndOfLine();
                        continue;
                    }
                    if (c == '/' && n == '*')
                    {
                        int end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (end < 0) throw Fail("an unterminated block comment");
                        for (; i < end + 2; i++)
                        {
                            char b = t[i] == '\n' ? '\n' : ' ';
                            M.Append(b);
                            K.Append(b);
                        }
                        continue;
                    }
                    if (c == '\'') { CharLiteral(); continue; }
                    if (c == '"' || c == '$' || c == '@')
                    {
                        if (TryString()) continue;
                    }
                    if (inHole)
                    {
                        if (c == '{' || c == '(' || c == '[') depth++;
                        else if (c == ')' || c == ']') { if (depth > 0) depth--; }
                        else if (c == '}')
                        {
                            if (depth == 0) return;
                            depth--;
                        }
                        else if (c == ':' && depth == 0)
                        {
                            // Even `::`: the compiler starts the format clause at the first
                            // top-level ':' (`{global::X}` does not compile), so `{x::/*}` is a
                            // hole whose format text is `:/*`.
                            FormatClause();
                            return;
                        }
                    }
                    Both(c);
                    i++;
                }
                if (inHole) throw Fail("an unterminated interpolation hole");
            }

            // The format clause of a hole: the ':' and everything up to the hole's closing '}'
            // (not consumed) is literal text. A conditional operator in a hole compiles only in
            // brackets, so a ':' outside every bracket always starts this clause.
            private void FormatClause()
            {
                Both(t[i]);
                i++;
                while (true)
                {
                    if (i >= t.Length) throw Fail("an unterminated interpolation format clause");
                    char c = t[i];
                    if (c == '}') return;
                    if (c == '"' || c == '\n') throw Fail("a quote or line break in an interpolation format clause");
                    Blank(c);
                    i++;
                }
            }

            private void CharLiteral()
            {
                int j = i + 1;
                if (At(j) == '\\') j += 2; else j += 1;
                while (j < t.Length && t[j] != '\'' && t[j] != '\n' && j - i <= 10) j++;
                if (j >= t.Length || t[j] != '\'') throw Fail("a char literal");
                Both('\'');
                for (int k = i + 1; k < j; k++) Blank(t[k]);
                Both('\'');
                i = j + 1;
            }

            private bool TryString()
            {
                int j = i;
                int dollars = 0;
                bool verbatim = false;
                while (j - i < 3 && (At(j) == '$' || At(j) == '@'))
                {
                    if (t[j] == '$') dollars++; else verbatim = true;
                    j++;
                }
                if (At(j) != '"') return false;
                if (dollars > 1 || j - i > 2) throw Fail("a raw interpolated string literal");
                if (At(j + 1) == '"' && At(j + 2) == '"') throw Fail("a raw string literal");
                bool interpolated = dollars == 1;
                for (; i <= j; i++) Both(t[i]);
                while (true)
                {
                    if (i >= t.Length) throw Fail("an unterminated string literal");
                    char c = t[i];
                    char n = At(i + 1);
                    if (!verbatim && c == '\\')
                    {
                        if (i + 1 >= t.Length || n == '\n') throw Fail("an escape at the end of a line");
                        Blank(c); Blank(n); i += 2;
                        continue;
                    }
                    if (verbatim && c == '"' && n == '"') { Blank(c); Blank(n); i += 2; continue; }
                    if (c == '"') { Both(c); i++; return true; }
                    if (interpolated && (c == '{' || c == '}') && n == c) { Blank(c); Blank(n); i += 2; continue; }
                    if (interpolated && c == '{')
                    {
                        Both(c);
                        i++;
                        Code(true);
                        Both('}');
                        i++;
                        continue;
                    }
                    if (!verbatim && c == '\n') throw Fail("an unterminated string literal");
                    Blank(c);
                    i++;
                }
            }
        }

        // ------------------------------------------------------------------ the gates (pure)

        /// <summary>The structural refusals every race gate shares, for one line: a character the
        /// race files may not hold (read on the raw line), a preprocessor directive outside
        /// <see cref="RaceAllowedDirectives"/>, an alias or a backslash (read on the masked line).
        /// Returns true (and adds the violation) when the line is refused.</summary>
        private static bool RaceStructuralViolation(RaceFile f, int index, List<string> violations)
        {
            string refusal = RaceRawRefusal(f.Raw[index]);
            if (refusal != null)
            {
                violations.Add(f.Rel + ":" + (index + 1) + ": " + refusal);
                return true;
            }
            string directive;
            if (f.RefusedDirectives.TryGetValue(index, out directive))
            {
                violations.Add(f.Rel + ":" + (index + 1) + ": [directive] #" + directive);
                return true;
            }
            string line = f.Masked[index];
            Match alias = RaceAliasUsing.Match(line);
            if (alias.Success)
            {
                violations.Add(f.Rel + ":" + (index + 1) + ": [alias] " + alias.Groups[1].Value);
                return true;
            }
            if (line.IndexOf('\\') >= 0)
            {
                violations.Add(f.Rel + ":" + (index + 1) + ": [escape] \\");
                return true;
            }
            return false;
        }

        private static void RaceRequireNotVacuous(RaceScanResult scan, string gate, int minTypeReferences, bool needRaceCore)
        {
            int raceCore = scan.Files.Count(f => f.Module == "RaceCore");
            if (needRaceCore && raceCore == 0)
                throw new InvalidOperationException(gate + ": no RaceCore file in the scan; this gate is vacuous");
            if (scan.ModuleFiles == 0)
                throw new InvalidOperationException(gate + ": no race module file in the scan; this gate is vacuous");
            if (scan.ControllerFiles == 0)
                throw new InvalidOperationException(gate + ": no Controllers/" + RaceControllerPattern
                    + " file in the scan; the glob found none and this gate is vacuous");
            if (scan.TypeReferences < minTypeReferences)
                throw new InvalidOperationException(gate + ": only " + scan.TypeReferences
                    + " type reference(s) parsed; the parse broke and this gate is vacuous");
        }

        /// <summary>Managed mirror of <c>scripts/grep-audit-race-core-no-store.ps1</c>.</summary>
        internal static List<string> RaceCoreNoStoreViolations(string repoRoot, int minTypeReferences = RaceMinTypeReferences)
        {
            RaceScanResult scan = RaceSourceScan(repoRoot);
            RaceRequireNotVacuous(scan, "race-core-no-store", minTypeReferences, true);
            var store = new Regex(RaceStorePattern, RegexOptions.CultureInvariant);
            var violations = new List<string>();
            foreach (RaceFile f in scan.Files)
            {
                if (f.Module != "RaceCore") continue;
                for (int i = 0; i < f.Masked.Length; i++)
                {
                    if (RaceStructuralViolation(f, i, violations)) continue;
                    foreach (Match m in store.Matches(f.Masked[i]))
                        violations.Add(f.Rel + ":" + (i + 1) + ": [store] " + m.Groups[1].Value);
                }
            }
            violations.Sort(StringComparer.Ordinal);
            return violations;
        }

        /// <summary>Managed mirror of <c>scripts/grep-audit-race-no-recorder.ps1</c>.</summary>
        internal static List<string> RaceNoRecorderViolations(string repoRoot, int minTypeReferences = RaceMinTypeReferences)
        {
            RaceScanResult scan = RaceSourceScan(repoRoot);
            RaceRequireNotVacuous(scan, "race-no-recorder", minTypeReferences, false);
            var banned = new Regex(
                @"(?<![A-Za-z0-9_])(" + string.Join("|", RaceRecorderTypes) + @")(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant);
            var violations = new List<string>();
            foreach (RaceFile f in scan.Files)
            {
                for (int i = 0; i < f.Masked.Length; i++)
                {
                    if (RaceStructuralViolation(f, i, violations)) continue;
                    foreach (Match m in banned.Matches(f.Masked[i]))
                        violations.Add(f.Rel + ":" + (i + 1) + ": [recorder] " + m.Groups[1].Value);
                }
            }
            violations.Sort(StringComparer.Ordinal);
            return violations;
        }

        internal sealed class RaceWriterEntry
        {
            public string Kind;   // "type" or "member"
            public string Name;
            public string Reason;
            public string Key { get { return Kind == "member" ? "." + Name : Name; } }
        }

        internal sealed class RaceWriterAllow
        {
            public string File;
            public string Key;
            public int Line;
        }

        /// <summary>Parses <c>scripts/race-no-writer-allowlist.txt</c>: <c>deny type|member Name
        /// reason</c> and <c>allow file Name|.Name reason</c>. A malformed line is a violation.</summary>
        internal static void RaceParseNoWriterAllowlist(string[] lines, List<RaceWriterEntry> deny,
            List<RaceWriterAllow> allow, List<string> violations)
        {
            const string source = "scripts/" + RaceNoWriterAllowlist;
            var ident = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
            for (int n = 0; n < lines.Length; n++)
            {
                string entry = lines[n].Trim();
                if (entry.Length == 0 || entry.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = entry.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                bool ok = parts.Length >= 4;
                if (ok && parts[0] == "deny" && (parts[1] == "type" || parts[1] == "member") && ident.IsMatch(parts[2]))
                {
                    deny.Add(new RaceWriterEntry
                    {
                        Kind = parts[1], Name = parts[2], Reason = string.Join(" ", parts, 3, parts.Length - 3),
                    });
                    continue;
                }
                if (ok && parts[0] == "allow" && parts[1].StartsWith("Source/Parsek/", StringComparison.Ordinal)
                    && ident.IsMatch(parts[2].TrimStart('.')) && parts[2].LastIndexOf('.') <= 0)
                {
                    allow.Add(new RaceWriterAllow { File = parts[1], Key = parts[2], Line = n + 1 });
                    continue;
                }
                violations.Add(source + ":" + (n + 1) + ": [bad-line] " + parts[0]);
            }
        }

        /// <summary>Managed mirror of <c>scripts/grep-audit-race-no-writer.ps1</c>.</summary>
        internal static List<string> RaceNoWriterViolations(string repoRoot, int minTypeReferences = RaceMinTypeReferences)
        {
            const string source = "scripts/" + RaceNoWriterAllowlist;
            string allowPath = Path.Combine(repoRoot, "scripts", RaceNoWriterAllowlist);
            if (!File.Exists(allowPath))
                throw new InvalidOperationException("race-no-writer: allowlist not found: " + allowPath);
            var deny = new List<RaceWriterEntry>();
            var allow = new List<RaceWriterAllow>();
            var violations = new List<string>();
            RaceParseNoWriterAllowlist(File.ReadAllLines(allowPath), deny, allow, violations);
            if (deny.Count == 0)
                throw new InvalidOperationException("race-no-writer: the allowlist denies nothing; this gate is vacuous");

            RaceScanResult scan = RaceSourceScan(repoRoot);
            RaceRequireNotVacuous(scan, "race-no-writer", minTypeReferences, false);

            var denyKeys = new HashSet<string>(deny.Select(d => d.Key), StringComparer.Ordinal);
            var allowed = new HashSet<string>(allow.Select(a => a.File + " " + a.Key), StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var patterns = deny.Select(d => new KeyValuePair<string, Regex>(d.Key, new Regex(
                d.Kind == "member"
                    ? @"\.\s*@?" + d.Name + @"(?![A-Za-z0-9_])"
                    : @"(?<![A-Za-z0-9_])" + d.Name + @"(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant))).ToList();
            foreach (RaceFile f in scan.Files)
            {
                for (int i = 0; i < f.Masked.Length; i++)
                {
                    if (RaceStructuralViolation(f, i, violations)) continue;
                    foreach (var p in patterns)
                    {
                        int hits = p.Value.Matches(f.Masked[i]).Count;
                        if (hits == 0) continue;
                        string pair = f.Rel + " " + p.Key;
                        if (allowed.Contains(pair)) { used.Add(pair); continue; }
                        for (int h = 0; h < hits; h++)
                            violations.Add(f.Rel + ":" + (i + 1) + ": [writer] " + p.Key);
                    }
                }
            }
            foreach (RaceWriterAllow a in allow)
            {
                if (!denyKeys.Contains(a.Key))
                    violations.Add(source + ":" + a.Line + ": [bad-allow] " + a.Key);
                else if (!used.Contains(a.File + " " + a.Key))
                    violations.Add(source + ":" + a.Line + ": [stale-allow] " + a.Key);
            }
            violations.Sort(StringComparer.Ordinal);
            return violations;
        }

        private static Action<string> RaceManagedGate(Func<string, int, List<string>> gate, string label, string hint)
        {
            return repoRoot =>
            {
                List<string> violations = gate(repoRoot, RaceMinTypeReferences);
                Assert.True(violations.Count == 0,
                    "managed " + label + " audit failed. " + hint + "\n" + string.Join("\n", violations));
            };
        }

        // ------------------------------------------------------------------ the gate facts

        [Fact]
        public void GrepAudit_RaceCoreNamesNoStore()
        {
            RunGrepAuditScript("grep-audit-race-core-no-store.ps1", RaceManagedGate(
                RaceCoreNoStoreViolations, "race-core-no-store",
                "RaceCore is the rules module (design 15.4): it names no store. A rule that needs a "
                + "store's data takes it as plain rows from RaceExport or RaceWorld."));
        }

        [Fact]
        public void GrepAudit_RaceFilesNameNoRecorder()
        {
            RunGrepAuditScript("grep-audit-race-no-recorder.ps1", RaceManagedGate(
                RaceNoRecorderViolations, "race-no-recorder",
                "No race file names the recorders, the recording store, the pending-tree slot or the "
                + "tree commit and discard services (design 15.4). RaceExport reads the own "
                + "recordings through EffectiveState only."));
        }

        [Fact]
        public void GrepAudit_RaceFilesNameNoWriterOfTheOwnModel()
        {
            RunGrepAuditScript("grep-audit-race-no-writer.ps1", RaceManagedGate(
                RaceNoWriterViolations, "race-no-writer",
                "No race file names a store of the save or a persistence writer of the save "
                + "(design 15.4). A race file that must name one gets an 'allow' line with its "
                + "reason in scripts/" + RaceNoWriterAllowlist + "."));
        }

        // ------------------------------------------------------------------ anti-vacuity

        [Fact]
        public void RaceSourceScan_IsNotVacuous()
        {
            // The scan's file set equals an independent walk of the race folders that exist, so a
            // folder that appears (RaceWorld with R3W-02, RaceExport with R3E-02) is scanned the
            // day it lands; the controller glob is counted on its own, so a glob that matches
            // nothing reds even while the module folders keep the total up.
            string repoRoot = ResolveRepoRoot();
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            RaceScanResult scan = RaceSourceScan(repoRoot);

            int walked = 0;
            foreach (string dir in Directory.EnumerateDirectories(sourceRoot))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("Race", StringComparison.Ordinal)) continue;
                Assert.True(RaceModuleFolders.Contains(name),
                    "a Race* module folder the race scan does not know: " + name
                    + "; add it to RaceModuleFolders and scripts/race-source-scan.ps1");
                walked += Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Count();
            }
            Assert.Equal(walked, scan.ModuleFiles);
            Assert.True(scan.Files.Count(f => f.Module == "RaceCore") >= RaceMinRaceCoreFiles,
                "the race scan sees fewer RaceCore files than R1-03 created");

            int controllers = Directory.EnumerateFiles(Path.Combine(sourceRoot, "Controllers"), "*.cs")
                .Count(p => Path.GetFileName(p).StartsWith("ParsekRace", StringComparison.Ordinal));
            Assert.True(controllers >= 1, "no Controllers/ParsekRace*.cs file on disk");
            Assert.Equal(controllers, scan.ControllerFiles);
            Assert.Contains(scan.Files, f => f.Rel == "Source/Parsek/Controllers/ParsekRaceScenario.cs");

            Assert.True(scan.TypeReferences >= RaceMinTypeReferences,
                "the race scan parsed only " + scan.TypeReferences + " type reference(s)");
            // Every line of every file survives the lex (same line count, same length).
            foreach (RaceFile f in scan.Files)
            {
                Assert.Equal(f.MaskedText.Length, f.KeptText.Length);
                string raw = RaceReadText(Path.Combine(repoRoot, f.Rel)).Replace("\r\n", "\n");
                Assert.Equal(raw.Split('\n').Length, f.Masked.Length);
                Assert.Equal(f.Raw.Length, f.Masked.Length);
                // No race file holds a character outside tab, CR, LF and printable ASCII today.
                for (int i = 0; i < f.Raw.Length; i++)
                    Assert.True(RaceRawRefusal(f.Raw[i]) == null, f.Rel + ":" + (i + 1) + ": " + RaceRawRefusal(f.Raw[i]));
                // Nor a preprocessor directive outside the allowed set.
                Assert.True(f.RefusedDirectives.Count == 0, f.Rel + " uses refused directive(s): "
                    + string.Join(", ", f.RefusedDirectives.Select(d => (d.Key + 1) + ": #" + d.Value)));
            }
        }

        [Fact]
        public void RaceLex_CommentsAndLiteralsAreNotCode_HolesAre()
        {
            string masked, kept;
            RaceLex("a // MilestoneStore.X\nb /* GamePersistence\n RecordingStore */ c\n", out masked, out kept);
            Assert.DoesNotContain("Store", masked);
            Assert.DoesNotContain("Persistence", masked);
            Assert.Equal(3, masked.Split('\n').Length - 1);
            Assert.Equal(masked, kept);

            RaceLex("x(\"RecordingStore.X\", @\"a \"\" MilestoneStore\", '\"', '\\'');", out masked, out kept);
            Assert.DoesNotContain("Store", masked);
            Assert.Contains("RecordingStore.X", kept);
            Assert.Equal(masked.Length, kept.Length);

            // An interpolation hole is code: hiding it would be a way past every gate.
            RaceLex("s = $\"n={MilestoneStore.Count} {{lit}} {f(\"q\")}\";", out masked, out kept);
            Assert.Contains("MilestoneStore.Count", masked);
            Assert.DoesNotContain("lit", masked);
            Assert.DoesNotContain("q", masked.Substring(masked.IndexOf('f')));
            RaceLex("s = $@\"a\n{RecordingStore.X}\"\"\";", out masked, out kept);
            Assert.Contains("RecordingStore.X", masked);

            // A preprocessor line is blanked like a comment: `#region notes /*` opens no block
            // comment over the code below it, and an apostrophe in a region name is no char literal.
            RaceLex("    #region notes /*\nint o = RecordingStore.X;\n    #endregion */\n", out masked, out kept);
            Assert.Contains("int o = RecordingStore.X;", masked);
            Assert.DoesNotContain("notes", masked);
            Assert.DoesNotContain("region", kept);
            RaceLex("#region Don't touch\nx();\n#endregion\n", out masked, out kept);
            Assert.Contains("x();", masked);
            // The lexer reports each directive it meets in code state with its word (blanks after
            // the '#' allowed, as the compiler does); the gates refuse every word outside
            // RaceAllowedDirectives (R1-06 third review: a disabled `#if` branch is not lexed by
            // the compiler, so a `/*` or `@"` in it would otherwise hide the active code after).
            List<KeyValuePair<int, string>> dirs;
            RaceLex("#if false\n#elif X\n# else\n#endif\n#define Y\n#pragma warning disable\n#nullable enable\n"
                + "  #region a b\n#endregion\n#\nx = a #b;\ns = @\"\n#if q\";\n", out masked, out kept, out dirs, "<input>");
            Assert.Equal(new[] { "if", "elif", "else", "endif", "define", "pragma", "nullable", "region", "endregion", "" },
                dirs.Select(d => d.Value).ToArray());
            RaceLex("#if false\n/*\n#endif\nobject o = typeof(RecordingStore);\n#if false\n*/\n#endif\n", out masked, out kept, out dirs, "<input>");
            Assert.Equal(new[] { "if", "endif" }, dirs.Select(d => d.Value).ToArray());
            Assert.DoesNotContain("RecordingStore", masked);   // hidden by the lexer, so the directives must red

            // A '#' that is not first on its line, or that sits in a verbatim string, is not a directive.
            RaceLex("s = @\"a\n#region RecordingStore\";\n", out masked, out kept);
            Assert.DoesNotContain("RecordingStore", masked);
            Assert.Contains("#region RecordingStore", kept);

            // The format clause of a hole (after a ':' outside every bracket) is literal text, so
            // a comment opener in it opens nothing.
            RaceLex("s = $\"{x:/*}\";\nint o = RecordingStore.X;\n// */}\"\n", out masked, out kept);
            Assert.Contains("int o = RecordingStore.X;", masked);
            RaceLex("s = $@\"{x://}\";\nint o = RecordingStore.X; char q = '\"'; // '\n", out masked, out kept);
            Assert.Contains("int o = RecordingStore.X;", masked);
            RaceLex("s = $\"{x,5:D3} {x:MilestoneStore}\";", out masked, out kept);
            Assert.DoesNotContain("D3", masked);
            Assert.DoesNotContain("MilestoneStore", masked);
            Assert.Contains("MilestoneStore", kept);
            // A ':' inside brackets is the conditional operator: it stays code.
            RaceLex("s = $\"{(a ? RecordingStore.X : b)} {d[k ? 1 : 2]}\";", out masked, out kept);
            Assert.Contains("(a ? RecordingStore.X : b)", masked);
            Assert.Contains("d[k ? 1 : 2]", masked);
            // Even `::` at the top of a hole starts the format clause at its first ':' (the
            // compiler does the same: `{global::X}` does not compile, `{x::/*}` does, with format
            // text `:/*`), so the `/*` opens nothing (R1-06 re-review).
            RaceLex("s = $\"{x::/*}\";\nobject p = typeof(RecordingStore);\n// */}\"\n", out masked, out kept);
            Assert.Contains("object p = typeof(RecordingStore);", masked);
            RaceLex("s = $\"{global::Parsek.MilestoneStore.Count}\";", out masked, out kept);
            Assert.DoesNotContain("MilestoneStore", masked);
            Assert.Throws<InvalidDataException>(() => RaceLex("s = $@\"{x:a\nb}\";", out masked, out kept));
            Assert.Throws<InvalidDataException>(() => RaceLex("s = $\"{x:a\" + RecordingStore.X + \"}\";", out masked, out kept));

            // The reviewer's positive controls (R1-06 review): every RecordingStore in a literal or
            // a comment is masked, and exactly the two real ones in code remain.
            string controls =
                "static string a = @\"q \"\" RecordingStore // /* \" + $@\"{x}\"\" {{RecordingStore}} {(x > 0 ? \"a\" : @\"b\"\"c\")}\";\n"
                + "static string b = @$\"{x,5:D3} {{ }} // RecordingStore\";\n"
                + "static char c1 = '\"', c2 = '\\'', c3 = '\\\\', c4 = '\\u0041', c5 = '/';\n"
                + "static string d = $\"{$\"{\"RecordingStore\"}\"}\";\n"
                + "// \"RecordingStore\" '\n"
                + "/* \"RecordingStore */\n"
                + "static int o = nameof(RecordingStore).Length + typeof(global::Parsek.RecordingStore).Name.Length;\n";
            RaceLex(controls, out masked, out kept);
            Assert.Equal(2, Regex.Matches(masked, @"(?<![A-Za-z0-9_])RecordingStore(?![A-Za-z0-9_])").Count);
            Assert.Equal(controls.Length, masked.Length);

            // What the lexer does not model fails closed.
            Assert.Throws<InvalidDataException>(() => RaceLex("s = \"\"\"raw\"\"\";", out masked, out kept));
            Assert.Throws<InvalidDataException>(() => RaceLex("s = $$\"x\";", out masked, out kept));
            Assert.Throws<InvalidDataException>(() => RaceLex("s = \"open\n", out masked, out kept));
            Assert.Throws<InvalidDataException>(() => RaceLex("a /* open", out masked, out kept));
        }

        // ------------------------------------------------------------------ planted reds

        /// <summary>Copies the real race files and the no-writer allowlist into a temp repo root,
        /// so a planted line runs the gate's real enumeration, lexer and allowlist.</summary>
        private static string RaceTempCopy()
        {
            string repoRoot = ResolveRepoRoot();
            string temp = Path.Combine(Path.GetTempPath(), "parsek-race-gates-" + Guid.NewGuid().ToString("N"));
            foreach (RaceFile f in RaceSourceScan(repoRoot).Files)
            {
                string dest = Path.Combine(temp, f.Rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(Path.Combine(repoRoot, f.Rel), dest);
            }
            Directory.CreateDirectory(Path.Combine(temp, "scripts"));
            File.Copy(Path.Combine(repoRoot, "scripts", RaceNoWriterAllowlist),
                Path.Combine(temp, "scripts", RaceNoWriterAllowlist));
            return temp;
        }

        private static void RacePlant(string root, string rel, string line)
        {
            string path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, "\n" + line + "\n", new UTF8Encoding(false));
        }

        private static void RaceDeleteTemp(string temp)
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Fact]
        public void RaceCoreNoStore_PlantedStoreInATempCopy_IsRed()
        {
            string temp = RaceTempCopy();
            try
            {
                Assert.Empty(RaceCoreNoStoreViolations(temp));
                // The same name in a comment and in a literal stays green.
                RacePlant(temp, "Source/Parsek/RaceCore/RaceLink.cs",
                    "// MilestoneStore.Milestones is never read here; \"GroupHierarchyStore.X\"");
                Assert.Empty(RaceCoreNoStoreViolations(temp));
                RacePlant(temp, "Source/Parsek/RaceCore/RaceLink.cs", "int n = MilestoneStore.Count;");
                List<string> red = RaceCoreNoStoreViolations(temp);
                Assert.Contains(red, v => v.StartsWith("Source/Parsek/RaceCore/RaceLink.cs:", StringComparison.Ordinal)
                    && v.EndsWith("[store] MilestoneStore", StringComparison.Ordinal));
                Assert.Single(red);
                // A fully qualified name is caught too.
                RacePlant(temp, "Source/Parsek/RaceCore/RaceFormat.cs", "var g = Parsek.GroupHierarchyStore.Groups;");
                Assert.Contains(RaceCoreNoStoreViolations(temp), v => v.EndsWith("[store] GroupHierarchyStore", StringComparison.Ordinal));
                // A store named in a controller file is outside this gate (the rules module only).
                string temp2 = RaceTempCopy();
                try
                {
                    RacePlant(temp2, "Source/Parsek/Controllers/ParsekRaceScenario.cs", "// x\nvar m = MilestoneStore.Count;");
                    Assert.Empty(RaceCoreNoStoreViolations(temp2));
                }
                finally { RaceDeleteTemp(temp2); }
            }
            finally { RaceDeleteTemp(temp); }
        }

        [Fact]
        public void RaceNoRecorder_PlantedRecordingStoreInATempRaceWorldFile_IsRed()
        {
            string temp = RaceTempCopy();
            try
            {
                Assert.Empty(RaceNoRecorderViolations(temp));
                // A new RaceWorld folder is scanned with no edit to the gate.
                RacePlant(temp, "Source/Parsek/RaceWorld/RivalProbe.cs", "var all = RecordingStore.CommittedRecordings;");
                List<string> red = RaceNoRecorderViolations(temp);
                Assert.Equal(new[] { "Source/Parsek/RaceWorld/RivalProbe.cs:2: [recorder] RecordingStore" }, red.ToArray());
                foreach (string name in RaceRecorderTypes)
                {
                    string t2 = RaceTempCopy();
                    try
                    {
                        RacePlant(t2, "Source/Parsek/Controllers/ParsekRaceScenario.cs", "object o = typeof(" + name + ");");
                        Assert.Contains(RaceNoRecorderViolations(t2), v => v.EndsWith("[recorder] " + name, StringComparison.Ordinal));
                    }
                    finally { RaceDeleteTemp(t2); }
                }
            }
            finally { RaceDeleteTemp(temp); }
        }

        [Fact]
        public void RaceNoWriter_PlantedWriterInATempCopy_IsRed()
        {
            string temp = RaceTempCopy();
            try
            {
                Assert.Empty(RaceNoWriterViolations(temp));
                const string core = "Source/Parsek/RaceCore/RaceScenarioChild.cs";
                RacePlant(temp, core, "GamePersistence.SaveGame(\"persistent\", \"x\", SaveMode.OVERWRITE);");
                RacePlant(temp, core, "other.OnSave(node);");
                RacePlant(temp, core, "Ledger.AddAction(action);");
                List<string> red = RaceNoWriterViolations(temp);
                Assert.Contains(red, v => v.StartsWith(core + ":", StringComparison.Ordinal) && v.EndsWith("[writer] GamePersistence", StringComparison.Ordinal));
                Assert.Contains(red, v => v.EndsWith("[writer] .OnSave", StringComparison.Ordinal));
                Assert.Contains(red, v => v.EndsWith("[writer] Ledger", StringComparison.Ordinal));
                Assert.Equal(3, red.Count);
            }
            finally { RaceDeleteTemp(temp); }

            // The allow mechanism: a listed file may name a listed entry; a line with no hit reds.
            temp = RaceTempCopy();
            try
            {
                const string scenario = "Source/Parsek/Controllers/ParsekRaceScenario.cs";
                RacePlant(temp, scenario, "GamePersistence.SaveGame(\"persistent\", \"x\", SaveMode.OVERWRITE);");
                File.AppendAllText(Path.Combine(temp, "scripts", RaceNoWriterAllowlist),
                    "\nallow " + scenario + " GamePersistence planted for the test\n"
                    + "allow " + scenario + " .OnSave planted, stale\n"
                    + "allow " + scenario + " NotDenied planted, not a deny entry\n");
                List<string> red = RaceNoWriterViolations(temp);
                Assert.DoesNotContain(red, v => v.EndsWith("[writer] GamePersistence", StringComparison.Ordinal));
                Assert.Contains(red, v => v.EndsWith("[stale-allow] .OnSave", StringComparison.Ordinal));
                Assert.Contains(red, v => v.EndsWith("[bad-allow] NotDenied", StringComparison.Ordinal));
                Assert.Equal(2, red.Count);
            }
            finally { RaceDeleteTemp(temp); }
        }

        [Fact]
        public void RaceGates_AliasAndEscape_AreRedInEveryGate()
        {
            var gates = new Func<string, int, List<string>>[]
            {
                RaceCoreNoStoreViolations, RaceNoRecorderViolations, RaceNoWriterViolations,
            };
            foreach (var gate in gates)
            {
                string temp = RaceTempCopy();
                try
                {
                    RacePlant(temp, "Source/Parsek/RaceCore/RaceLink.cs", "using RS = Parsek.RecordingStore;");
                    RacePlant(temp, "Source/Parsek/RaceCore/RaceFormat.cs", "var x = Recording\\u0053tore.CommittedRecordings;");
                    List<string> red = gate(temp, RaceMinTypeReferences);
                    Assert.Contains(red, v => v.EndsWith("[alias] RS", StringComparison.Ordinal));
                    Assert.Contains(red, v => v.EndsWith("[escape] \\", StringComparison.Ordinal));
                }
                finally { RaceDeleteTemp(temp); }
            }
        }

        [Fact]
        public void RaceGates_ReviewBypasses_AreRedInEveryGate()
        {
            // The five R1-06 review inputs, each compilable C# with a real store reference that the
            // first lexer read as a comment, a literal or a different name. Each one alone, planted
            // in a temp copy, reds every gate it is in scope of.
            const string link = "Source/Parsek/RaceCore/RaceLink.cs";
            var lexerShapes = new Dictionary<string, string>
            {
                ["preprocessor"] = "    #region notes /*\nstatic int o = RecordingStore.CommittedRecordings;\n    #endregion */",
                ["format-block"] = "static string s = $\"{x:/*}\";\nstatic int o = RecordingStore.CommittedRecordings;\n// */}\"",
                ["format-line"] = "static string s = $@\"{x://}\";\n}\nstatic class C2 {\nstatic int o = RecordingStore.CommittedRecordings;\nstatic char q = '\"'; // '",
                // R1-06 re-review: the format clause starts at the first ':' even before a second.
                ["format-double-colon"] = "static int x = 1;\nstatic string s = $\"{x::/*}\";\nstatic object p = typeof(RecordingStore);\n// */}\"",
            };
            var gates = new Dictionary<string, KeyValuePair<Func<string, int, List<string>>, string>>
            {
                ["no-store"] = new KeyValuePair<Func<string, int, List<string>>, string>(RaceCoreNoStoreViolations, "[store] RecordingStore"),
                ["no-recorder"] = new KeyValuePair<Func<string, int, List<string>>, string>(RaceNoRecorderViolations, "[recorder] RecordingStore"),
                ["no-writer"] = new KeyValuePair<Func<string, int, List<string>>, string>(RaceNoWriterViolations, "[writer] RecordingStore"),
            };
            foreach (var shape in lexerShapes)
            {
                foreach (var gate in gates)
                {
                    string temp = RaceTempCopy();
                    try
                    {
                        RacePlant(temp, link, shape.Value);
                        List<string> red = gate.Value.Key(temp, RaceMinTypeReferences);
                        Assert.True(red.Count == 1 && red[0].StartsWith(link + ":", StringComparison.Ordinal)
                                && red[0].EndsWith(gate.Value.Value, StringComparison.Ordinal),
                            shape.Key + " in the " + gate.Key + " gate gave: " + string.Join(" | ", red));
                    }
                    finally { RaceDeleteTemp(temp); }
                }
            }

            // A format character inside an identifier (C# drops it: this is RecordingStore).
            foreach (var gate in gates)
            {
                string temp = RaceTempCopy();
                try
                {
                    RacePlant(temp, link, "static int o = Recording\u00ADStore.CommittedRecordings;");
                    List<string> red = gate.Value.Key(temp, RaceMinTypeReferences);
                    Assert.True(red.Count == 1 && red[0].StartsWith(link + ":", StringComparison.Ordinal)
                            && red[0].EndsWith("[non-ascii] U+00AD", StringComparison.Ordinal),
                        "soft hyphen in the " + gate.Key + " gate gave: " + string.Join(" | ", red));
                }
                finally { RaceDeleteTemp(temp); }
            }

            // Characters the compiler reads as line ends (U+2028, U+0085) or as a blank before a
            // directive (U+001A) are refused on the raw text, before the lexer could misread them
            // (R1-06 re-review). The U+001A case also keeps its real hit: the lexer reads the
            // refused character as a blank, so the directive lines are directives.
            var rawShapes = new[]
            {
                new KeyValuePair<string, string[]>("// note\u2028static object o = RecordingStore.CommittedRecordings;",
                    new[] { "[non-ascii] U+2028" }),
                new KeyValuePair<string, string[]>("// note\u0085static object o = RecordingStore.CommittedRecordings;",
                    new[] { "[non-ascii] U+0085" }),
                new KeyValuePair<string, string[]>("// note\u2029static object o = RecordingStore.CommittedRecordings;",
                    new[] { "[non-ascii] U+2029" }),
                new KeyValuePair<string, string[]>("\u001A#region notes /*\nstatic object o = RecordingStore.CommittedRecordings;\n\u001A#endregion */",
                    new[] { "[control] U+001A", "[control] U+001A", null }),
                new KeyValuePair<string, string[]>("static object o =\fRecordingStore.CommittedRecordings;", new[] { "[control] U+000C" }),
                new KeyValuePair<string, string[]>("static object o =\vRecordingStore.CommittedRecordings;", new[] { "[control] U+000B" }),
            };
            foreach (var shape in rawShapes)
            {
                foreach (var gate in gates)
                {
                    string temp = RaceTempCopy();
                    try
                    {
                        RacePlant(temp, link, shape.Key);
                        List<string> red = gate.Value.Key(temp, RaceMinTypeReferences);
                        var want = shape.Value.Select(w => w ?? gate.Value.Value).OrderBy(w => w, StringComparer.Ordinal).ToList();
                        var got = red.Select(v => v.Substring(v.IndexOf(": ", StringComparison.Ordinal) + 2))
                            .OrderBy(w => w, StringComparer.Ordinal).ToList();
                        Assert.True(red.All(v => v.StartsWith(link + ":", StringComparison.Ordinal)) && want.SequenceEqual(got),
                            "raw shape U+" + ((int)shape.Key.First(c => !RaceAllowedChar(c))).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
                            + " in the " + gate.Key + " gate gave: " + string.Join(" | ", red));
                    }
                    finally { RaceDeleteTemp(temp); }
                }
            }

            // One leading byte-order mark is stripped; a second is a character like any other.
            string tb = RaceTempCopy();
            try
            {
                string path = Path.Combine(tb, link);
                byte[] body = File.ReadAllBytes(path);
                File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());
                foreach (var gate in gates) Assert.Empty(gate.Value.Key(tb, RaceMinTypeReferences));
                File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());
                foreach (var gate in gates)
                    Assert.Equal(new[] { link + ":1: [non-ascii] U+FEFF" }, gate.Value.Key(tb, RaceMinTypeReferences).ToArray());
            }
            finally { RaceDeleteTemp(tb); }

            // A conditional directive (R1-06 third review): the compiler does not lex a disabled
            // branch, so a `/*` or `@"` in `#if false` hides the active code after `#endif` from the
            // lexer. Every directive outside region / endregion / pragma / nullable reds.
            var directiveShapes = new Dictionary<string, string>
            {
                ["block"] = "#if false\n/*\n#endif\n        static object o = typeof(RecordingStore);\n#if false\n*/\n#endif",
                ["verbatim"] = "#if false\n        static string s = @\"\n#endif\n        static object o = typeof(RecordingStore);\n#if false\n\";\n#endif",
                ["blank-after-hash"] = "# if false\n/*\n#endif\n        static object o = typeof(RecordingStore);\n#if false\n*/\n #  endif",
                ["else"] = "#if DEBUG\n#else\n/*\n#endif\n        static object o = typeof(RecordingStore);\n#if DEBUG\n#else\n*/\n#endif",
            };
            foreach (var shape in directiveShapes)
            {
                foreach (var gate in gates)
                {
                    string temp = RaceTempCopy();
                    try
                    {
                        RacePlant(temp, link, shape.Value);
                        List<string> red = gate.Value.Key(temp, RaceMinTypeReferences);
                        Assert.True(red.Count >= 2 && red.All(v => v.StartsWith(link + ":", StringComparison.Ordinal)
                                && v.Contains(": [directive] #")),
                            shape.Key + " in the " + gate.Key + " gate gave: " + string.Join(" | ", red));
                    }
                    finally { RaceDeleteTemp(temp); }
                }
            }
            string td = RaceTempCopy();
            try
            {
                RacePlant(td, link, "#region a\n#pragma warning disable CS0168\n#nullable enable\n#endregion\n  # pragma warning restore CS0168");
                foreach (var gate in gates) Assert.Empty(gate.Value.Key(td, RaceMinTypeReferences));
            }
            finally { RaceDeleteTemp(td); }

            // A verbatim identifier past the member deny: `.@OnSave` is `.OnSave`.
            string t2 = RaceTempCopy();
            try
            {
                RacePlant(t2, link, "void G(ScenarioModule m, ConfigNode n) { base.@OnSave(n); m. @OnSave(n); }");
                List<string> red = RaceNoWriterViolations(t2);
                Assert.Equal(2, red.Count);
                Assert.All(red, v => Assert.EndsWith("[writer] .OnSave", v));
            }
            finally { RaceDeleteTemp(t2); }

            // And no false red: a region name with an apostrophe, and a verbatim alias still refused.
            string t3 = RaceTempCopy();
            try
            {
                RacePlant(t3, link, "#region Don't touch\n#endregion");
                foreach (var gate in gates) Assert.Empty(gate.Value.Key(t3, RaceMinTypeReferences));
                RacePlant(t3, link, "using @RS = Parsek.RecordingStore;");
                foreach (var gate in gates)
                    Assert.Contains(gate.Value.Key(t3, RaceMinTypeReferences), v => v.EndsWith("[alias] RS", StringComparison.Ordinal));
            }
            finally { RaceDeleteTemp(t3); }
        }

        // ------------------------------------------------------------------ mirror

        private static List<string> RaceScriptList(string scriptFile, string variable)
        {
            string path = Path.Combine(ResolveRepoRoot(), "scripts", scriptFile);
            Assert.True(File.Exists(path), "the script moved: " + path);
            string src = File.ReadAllText(path).Replace("\r\n", "\n");
            int at = src.IndexOf("\n" + variable + " = @(", StringComparison.Ordinal);
            Assert.True(at >= 0, variable + " initializer moved in " + scriptFile);
            int end = src.IndexOf(")", at + variable.Length + 6, StringComparison.Ordinal);
            var values = new List<string>();
            foreach (Match m in Regex.Matches(src.Substring(at, end - at), @"'([^']*)'"))
                values.Add(m.Groups[1].Value);
            return values;
        }

        private static string RaceScriptScalar(string scriptFile, string variable)
        {
            string src = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "scripts", scriptFile)).Replace("\r\n", "\n");
            Match m = Regex.Match(src, "\n" + Regex.Escape(variable) + @" = '([^']*)'\n");
            Assert.True(m.Success, variable + " assignment moved in " + scriptFile);
            return m.Groups[1].Value;
        }

        [Fact]
        public void RaceGates_ScriptsMirrorTheManagedScan()
        {
            // Static half, runs everywhere: the declarative parts of the scripts equal the managed
            // constants (on a runner without pwsh only the managed scan runs, so a drift would go
            // unseen there).
            Assert.Equal(RaceModuleFolders, RaceScriptList("race-source-scan.ps1", "$raceModuleFolders").ToArray());
            Assert.Equal(RaceControllerPattern, RaceScriptScalar("race-source-scan.ps1", "$raceControllerPattern"));
            Assert.Equal(RaceAllowedDirectives, RaceScriptList("race-source-scan.ps1", "$raceAllowedDirectives").ToArray());
            Assert.Equal(RaceRecorderTypes, RaceScriptList("grep-audit-race-no-recorder.ps1", "$recorderTypes").ToArray());
            Assert.Equal(RaceStorePattern, RaceScriptScalar("grep-audit-race-core-no-store.ps1", "$storePattern"));
            foreach (string script in new[]
                     {
                         "grep-audit-race-core-no-store.ps1", "grep-audit-race-no-recorder.ps1", "grep-audit-race-no-writer.ps1",
                     })
            {
                string src = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "scripts", script));
                Assert.Contains("[int]$MinTypeReferences = " + RaceMinTypeReferences, src);
                Assert.Contains("race-source-scan.ps1", src);
            }

            // Dynamic half: the managed arm on one fixed input gives the pinned lines (every
            // lexer shape, every violation kind, the five R1-06 review bypasses); where pwsh
            // exists the scripts give the same.
            const string core = "Source/Parsek/RaceCore/MirrorRules.cs";
            const string ctl = "Source/Parsek/Controllers/ParsekRaceMirror.cs";
            const string world = "Source/Parsek/RaceWorld/MirrorWorld.cs";
            const string bypass = "Source/Parsek/RaceCore/MirrorBypass.cs";
            const string raw = "Source/Parsek/RaceCore/MirrorRaw.cs";
            const string directive = "Source/Parsek/RaceCore/MirrorDirective.cs";
            const string list = "scripts/" + RaceNoWriterAllowlist;
            var expected = new Dictionary<string, string[]>
            {
                ["grep-audit-race-core-no-store.ps1"] = new[]
                {
                    bypass + ":11: [store] GroupHierarchyStore",
                    bypass + ":15: [non-ascii] U+00AD",
                    bypass + ":20: [alias] RV",
                    bypass + ":22: [store] GroupHierarchyStore",
                    bypass + ":6: [store] MilestoneStore",
                    directive + ":11: [directive] #endif",
                    directive + ":14: [directive] #if",
                    directive + ":18: [directive] #endif",
                    directive + ":5: [directive] #if",
                    raw + ":5: [non-ascii] U+2028",
                    raw + ":6: [non-ascii] U+0085",
                    raw + ":7: [control] U+001A",
                    raw + ":8: [store] GroupHierarchyStore",
                    raw + ":9: [control] U+001A",
                    core + ":10: [store] MilestoneStore",
                    core + ":11: [store] GroupHierarchyStore",
                    core + ":12: [escape] \\",
                    core + ":2: [alias] RS",
                },
                ["grep-audit-race-no-recorder.ps1"] = new[]
                {
                    ctl + ":6: [recorder] FlightRecorder",
                    ctl + ":6: [recorder] TreeCommitService",
                    bypass + ":14: [recorder] TreeDiscardService",
                    bypass + ":15: [non-ascii] U+00AD",
                    bypass + ":20: [alias] RV",
                    bypass + ":22: [recorder] FlightRecorder",
                    directive + ":11: [directive] #endif",
                    directive + ":14: [directive] #if",
                    directive + ":18: [directive] #endif",
                    directive + ":5: [directive] #if",
                    raw + ":5: [non-ascii] U+2028",
                    raw + ":6: [non-ascii] U+0085",
                    raw + ":7: [control] U+001A",
                    raw + ":8: [recorder] TreeCommitService",
                    raw + ":9: [control] U+001A",
                    core + ":12: [escape] \\",
                    core + ":13: [recorder] PendingTreeSlot",
                    core + ":2: [alias] RS",
                    world + ":3: [recorder] RecordingStore",
                },
                ["grep-audit-race-no-writer.ps1"] = new[]
                {
                    ctl + ":5: [writer] GamePersistence",
                    bypass + ":11: [writer] GroupHierarchyStore",
                    bypass + ":15: [non-ascii] U+00AD",
                    bypass + ":16: [writer] .OnSave",
                    bypass + ":16: [writer] .OnSave",
                    bypass + ":20: [alias] RV",
                    bypass + ":22: [writer] GroupHierarchyStore",
                    directive + ":11: [directive] #endif",
                    directive + ":14: [directive] #if",
                    directive + ":18: [directive] #endif",
                    directive + ":5: [directive] #if",
                    raw + ":5: [non-ascii] U+2028",
                    raw + ":6: [non-ascii] U+0085",
                    raw + ":7: [control] U+001A",
                    raw + ":8: [writer] GroupHierarchyStore",
                    raw + ":9: [control] U+001A",
                    core + ":11: [writer] .OnSave",
                    core + ":11: [writer] GroupHierarchyStore",
                    core + ":12: [escape] \\",
                    core + ":2: [alias] RS",
                    list + ":7: [stale-allow] GamePersistence",
                    list + ":8: [bad-line] bogus",
                },
            };
            var gates = new Dictionary<string, Func<string, int, List<string>>>
            {
                ["grep-audit-race-core-no-store.ps1"] = RaceCoreNoStoreViolations,
                ["grep-audit-race-no-recorder.ps1"] = RaceNoRecorderViolations,
                ["grep-audit-race-no-writer.ps1"] = RaceNoWriterViolations,
            };
            string pwsh;
            bool havePwsh = TryFindExecutable("pwsh", out pwsh) || TryFindExecutable("pwsh.exe", out pwsh);
            string temp = Path.Combine(Path.GetTempPath(), "parsek-race-mirror-" + Guid.NewGuid().ToString("N"));
            try
            {
                RaceWriteMirrorFixture(temp);
                foreach (var gate in gates)
                {
                    Assert.Equal(expected[gate.Key], gate.Value(temp, 0).ToArray());
                    if (havePwsh)
                        Assert.Equal(expected[gate.Key], RaceRunScript(pwsh, gate.Key, temp).ToArray());
                }
            }
            finally { RaceDeleteTemp(temp); }
        }

        /// <summary>The fixed mirror input: every lexer shape and every violation kind.</summary>
        private static void RaceWriteMirrorFixture(string root)
        {
            Action<string, string> write = (rel, text) =>
            {
                string p = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                // Explicit bytes, so both arms read the same thing: UTF-8, no byte-order mark
                // unless the text itself starts with U+FEFF.
                File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n")));
            };
            write("Source/Parsek/RaceCore/MirrorRules.cs",
                "using System;\n"
                + "using RS = Parsek.RecordingStore;\n"
                + "namespace Parsek\n{\n"
                + "    // MilestoneStore.X in a comment\n"
                + "    /* GamePersistence\n       FlightRecorder */ internal static class MirrorRules\n    {\n"
                + "        static string a = \"RecordingStore.X\" + @\"q \"\" MilestoneStore\" + '\"' + '\\'';\n"
                + "        static string b = $\"n={MilestoneStore.Count} {{GroupHierarchyStore}} {F(\"Ledger\")}\";\n"
                + "        static void G(ScenarioModule m, ConfigNode n) { m.OnSave(n); GroupHierarchyStore.Clear(); }\n"
                + "        static object h = Recording\\u0053tore.X;\n"
                + "        static object r = Parsek.PendingTreeSlot.Current;\n"
                + "    }\n}\n");
            // The R1-06 review bypasses: a preprocessor line, a format clause holding a comment
            // opener (block and line), a format character in an identifier, a verbatim member,
            // a verbatim alias; plus the shapes that must stay code or stay literal.
            write("Source/Parsek/RaceCore/MirrorBypass.cs",
                "namespace Parsek\n{\n"
                + "    #region notes /* a region name is not code\n"
                + "    static class MirrorBypass\n    {\n"
                + "        static object a = MilestoneStore.Count;\n"
                + "    #endregion */\n"
                + "        #region Don't touch\n"
                + "        #endregion\n"
                + "        static int x = 1; static string b = $\"{x:/*}\";\n"
                + "        static object c = GroupHierarchyStore.Groups;\n"
                + "        // */}\"\n"
                + "        static string d = $@\"{x://}\";\n"
                + "        static object e = TreeDiscardService.X; static char q = '\"'; // '\n"
                + "        static object f = Recording\u00ADStore.CommittedRecordings;\n"
                + "        static void G(ScenarioModule m, ConfigNode n) { m.@OnSave(n); base.@OnSave(n); }\n"
                + "        static string h = $\"{(x > 0 ? \"a\" : \"b\")} {global::Parsek.PendingTreeSlot.Current} {x,5:D3} {x:MissionStore}\";\n"
                + "    }\n}\n"
                + "using @RV = Parsek.RecordingStore;\n"
                // The format clause starts at the FIRST top-level ':' (R1-06 re-review), so line
                // 17's `{global::...}` is format text and line 22 is code.
                + "static class MirrorColon { static int x = 1; static string s = $\"{x::/*}\";\n"
                + "    static object p = typeof(FlightRecorder); static object q = GroupHierarchyStore.Count;\n"
                + "    // */}\"\n"
                + "}\n");
            // Directives outside region / endregion / pragma / nullable (R1-06 third review): the
            // `/*` and `@"` in the disabled branches hide line 8 from the lexer, the directive
            // lines red; `#pragma` and `#nullable` stay green.
            write("Source/Parsek/RaceCore/MirrorDirective.cs",
                "namespace Parsek\n{\n"
                + "    static class MirrorDirective\n    {\n"
                + "#if false\n"
                + "/*\n"
                + "#endif\n"
                + "        static object o = typeof(TreeCommitService); static object g = GroupHierarchyStore.Groups;\n"
                + "#if false\n"
                + "*/\n"
                + "# endif\n"
                + "#pragma warning disable CS0168\n"
                + "#nullable enable\n"
                + "#if DEBUG\n"
                + "        static string s = @\"\n"
                + "#else\n"
                + "\";\n"
                + "#endif\n"
                + "    }\n}\n");
            // Characters refused on the raw text (R1-06 re-review): U+2028 and U+0085 end a line
            // for the compiler, U+001A is a blank before a directive. A leading byte-order mark
            // is stripped, not refused.
            write("Source/Parsek/RaceCore/MirrorRaw.cs",
                "\uFEFFnamespace Parsek\n{\n"
                + "    static class MirrorRaw\n    {\n"
                + "        // note\u2028static object a = typeof(RecordingStore);\n"
                + "        // note\u0085static object b = typeof(RecordingStore);\n"
                + "\u001A#region notes /*\n"
                + "        static object c = GroupHierarchyStore.Groups; static object d = typeof(TreeCommitService);\n"
                + "\u001A#endregion */\n"
                + "    }\n}\n");
            write("Source/Parsek/Controllers/ParsekRaceMirror.cs",
                "namespace Parsek\n{\n    public sealed class ParsekRaceMirror : ScenarioModule\n    {\n"
                + "        public override void OnSave(ConfigNode node) { GamePersistence.SaveGame(\"a\", \"b\", SaveMode.OVERWRITE); }\n"
                + "        void F() { var f = typeof(FlightRecorder); TreeCommitService.Commit(); }\n"
                + "    }\n}\n");
            write("Source/Parsek/RaceWorld/MirrorWorld.cs",
                "namespace Parsek\n{\n    static class MirrorWorld { static object o = RecordingStore.CommittedRecordings; static object m = MissionStore.Missions; }\n}\n");
            write("scripts/" + RaceNoWriterAllowlist,
                "# mirror fixture\n"
                + "deny type GamePersistence the save writer\n"
                + "deny type GroupHierarchyStore a store of the save\n"
                + "deny type MissionStore a store of the save\n"
                + "deny member OnSave a module's save\n"
                + "allow Source/Parsek/RaceWorld/MirrorWorld.cs MissionStore read only, fixture\n"
                + "allow Source/Parsek/RaceWorld/MirrorWorld.cs GamePersistence stale, fixture\n"
                + "bogus line here\n");
        }

        private static List<string> RaceRunScript(string pwsh, string script, string root)
        {
            string scriptPath = Path.Combine(ResolveRepoRoot(), "scripts", script);
            var psi = new ProcessStartInfo
            {
                FileName = pwsh,
                Arguments = string.Format("-NoProfile -File \"{0}\" -RepoRoot \"{1}\" -MinTypeReferences 0", scriptPath, root),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                Assert.True(proc.WaitForExit(60_000), script + " did not finish within 60s");
                proc.WaitForExit();
                Assert.True(proc.ExitCode == 1, script + " on the mirror fixture exited " + proc.ExitCode
                    + " (expected 1)\nstdout:\n" + stdout + "\nstderr:\n" + stderr);
            }
            var lines = new List<string>();
            var shape = new Regex(@"^\S+:\d+: \[[a-z-]+\] ", RegexOptions.CultureInvariant);
            foreach (string line in stdout.ToString().Replace("\r\n", "\n").Split('\n'))
            {
                if (shape.IsMatch(line)) lines.Add(line);
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        // ------------------------------------------------------------------ T-15's cell

        /// <summary>The short names of every type <c>scripts/arch/lifetimes.toml</c> gives a
        /// <c>save</c> lifetime, by default or on a field. Parsed from the data file's
        /// <c>[[type]]</c> blocks (TOML data, not source) in the file's real shape: a
        /// <c>[[type]]</c> header (a trailing comment allowed) opens a type, its
        /// <c>name</c> / <c>default</c> / <c>reason</c> keys follow, and the
        /// <c>[type.fields]</c> table after it holds that SAME type's fields, so a type whose
        /// only save state is a field still counts. Any other header leaves the type section.
        /// A line inside a type or its fields that is not a one-line <c>key = "value"</c>
        /// throws rather than being skipped (fail closed).</summary>
        internal static SortedSet<string> RaceSaveLifetimeTypes(string[] tomlLines)
        {
            var header = new Regex(@"^(\[\[?)\s*([A-Za-z0-9_.]+)\s*(\]\]?)\s*(?:#.*)?$", RegexOptions.CultureInvariant);
            var keyValue = new Regex(@"^(""[^""]*""|[A-Za-z0-9_-]+)\s*=\s*""([^""]*)""\s*(?:#.*)?$", RegexOptions.CultureInvariant);
            var types = new List<KeyValuePair<string, bool>>();
            string name = null;
            bool save = false;
            bool open = false;
            string section = null; // "type", "fields" or null (some other table)
            Func<string, bool> isSave = v =>
            {
                int colon = v.IndexOf(':');
                return (colon < 0 ? v : v.Substring(0, colon)).Trim() == "save";
            };
            Action close = () =>
            {
                if (open) types.Add(new KeyValuePair<string, bool>(name, save));
                open = false;
                name = null;
                save = false;
            };
            for (int n = 0; n < tomlLines.Length; n++)
            {
                string line = tomlLines[n].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    Match h = header.Match(line);
                    if (!h.Success || (h.Groups[1].Value.Length != h.Groups[3].Value.Length))
                        throw new InvalidDataException("lifetimes.toml line " + (n + 1) + ": unreadable table header: " + line);
                    bool array = h.Groups[1].Value.Length == 2;
                    string table = h.Groups[2].Value;
                    if (array && table == "type")
                    {
                        close();
                        open = true;
                        section = "type";
                    }
                    else if (!array && table == "type.fields")
                    {
                        // TOML: [type.fields] is a sub-table of the LAST [[type]] element.
                        if (!open)
                            throw new InvalidDataException("lifetimes.toml line " + (n + 1) + ": [type.fields] before any [[type]]");
                        section = "fields";
                    }
                    else
                    {
                        section = null;
                    }
                    continue;
                }
                if (section == null) continue;
                Match m = keyValue.Match(line);
                if (!m.Success)
                    throw new InvalidDataException("lifetimes.toml line " + (n + 1) + ": not a one-line key = \"value\" inside a type: " + line);
                string key = m.Groups[1].Value.Trim('"');
                string value = m.Groups[2].Value;
                if (section == "type")
                {
                    if (key == "name") name = value;
                    else if (key == "default") save |= isSave(value);
                }
                else
                {
                    save |= isSave(value);
                }
            }
            close();
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, bool> t in types)
            {
                if (t.Key == null)
                    throw new InvalidDataException("lifetimes.toml: a [[type]] block with no name");
                if (t.Value) result.Add(t.Key.Substring(t.Key.LastIndexOf('.') + 1));
            }
            return result;
        }

        [Fact]
        public void RaceNoWriter_EverySaveLifetimeTypeIsDenied()
        {
            // T-15's cell: the save-lifetime state is the save; every type that holds it is on the
            // no-writer gate's denylist, so a store that gains a save lifetime reds here until the
            // gate denies it.
            string repoRoot = ResolveRepoRoot();
            SortedSet<string> saveTypes = RaceSaveLifetimeTypes(
                File.ReadAllLines(Path.Combine(repoRoot, "scripts", "arch", "lifetimes.toml")));
            Assert.True(saveTypes.Count >= 20,
                "lifetimes.toml parse found only " + saveTypes.Count + " save-lifetime type(s); the parse broke");
            Assert.Contains("RecordingStore", saveTypes);
            Assert.Contains("Ledger", saveTypes);

            var deny = new List<RaceWriterEntry>();
            var allow = new List<RaceWriterAllow>();
            var bad = new List<string>();
            RaceParseNoWriterAllowlist(File.ReadAllLines(Path.Combine(repoRoot, "scripts", RaceNoWriterAllowlist)),
                deny, allow, bad);
            Assert.Empty(bad);
            var denied = new HashSet<string>(deny.Where(d => d.Kind == "type").Select(d => d.Name), StringComparer.Ordinal);
            List<string> missing = saveTypes.Where(t => !denied.Contains(t)).ToList();
            Assert.True(missing.Count == 0,
                "save-lifetime type(s) in scripts/arch/lifetimes.toml missing from the no-writer denylist (add a "
                + "'deny type <Name> save lifetime (lifetimes.toml)' line to scripts/" + RaceNoWriterAllowlist + "): "
                + string.Join(", ", missing));

            // The persistence writers of the save, named by design 15.4.
            foreach (string writer in new[] { "GamePersistence", "ScenarioRunner", "ProtoScenarioModule" })
                Assert.Contains(writer, denied);
            Assert.Contains(deny, d => d.Kind == "member" && d.Name == "OnSave");

            // The parse reads the file's real shape: a type whose only save state is a field in its
            // [type.fields] table counts, a trailing comment on a header is read, a commented-out
            // block adds nothing, a field named `name` does not rename the type, and a `save` key
            // outside a type ([lifetimes]) is not a type.
            SortedSet<string> shaped = RaceSaveLifetimeTypes(new[]
            {
                "[lifetimes]", "save = \"held in the save\"",
                "# [[type]]", "# name = \"Parsek.Ghosted\"", "# default = \"save\"",
                "[[type]]  # a header with a trailing comment", "name = \"Parsek.FieldOnly\"",
                "default = \"process\"", "reason = \"save: the reason text is not a lifetime\"",
                "", "[type.fields]", "name = \"process: a field called name\"",
                "liveList = \"save: cleared by the cold branch, then filled from the save\"",
                "[[type]]", "name = \"Parsek.ProcessOnly\"", "default = \"process\"",
                "[type.fields]", "\"quoted\" = \"test-hook\"", "cache = \"process: x\"",
                "[[type]]", "name = \"Parsek.ByDefault\"", "default = \"save\"",
                "[cold_load.roots]", "\"Parsek.Other\" = \"save\"",
            });
            Assert.Equal(new[] { "ByDefault", "FieldOnly" }, shaped.ToArray());
            Assert.Throws<InvalidDataException>(() => RaceSaveLifetimeTypes(new[]
            {
                "[[type]]", "name = \"Parsek.X\"", "[type.fields]", "multi = \"\"\"save\"",
            }));

            // And the other way: every deny line that cites lifetimes.toml names a save type of it.
            List<string> stale = deny.Where(d => d.Kind == "type"
                    && d.Reason.IndexOf("lifetimes.toml", StringComparison.Ordinal) >= 0
                    && !saveTypes.Contains(d.Name))
                .Select(d => d.Name).ToList();
            Assert.True(stale.Count == 0,
                "deny line(s) citing lifetimes.toml for a type it gives no save lifetime: " + string.Join(", ", stale));
        }

        // ------------------------------------------------------------------ race log tags

        /// <summary>Every <c>ParsekLog</c> member that takes a subsystem tag, with the tag's
        /// argument index, read by reflection (a parameter named <c>subsystem</c>).</summary>
        internal static Dictionary<string, int> RaceParsekLogTagArgument()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MethodInfo method in typeof(ParsekLog).GetMethods(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] ps = method.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].Name != "subsystem") continue;
                    int known;
                    if (map.TryGetValue(method.Name, out known) && known != i)
                        throw new InvalidOperationException("ParsekLog." + method.Name
                            + " takes its subsystem at two positions; the tag cell cannot read it");
                    map[method.Name] = i;
                }
            }
            return map;
        }

        /// <summary>
        /// Every race-file <c>ParsekLog</c> call whose tag is not in <paramref name="tags"/> or cannot
        /// be read: the tag argument must be a string literal or a <c>const string</c> declared in a
        /// race file (unqualified: in the same file; qualified <c>Type.Name</c>: in the race file
        /// declaring <c>Type</c>). A <c>using static</c> of ParsekLog and a method group are refused,
        /// since either hides the call.
        /// </summary>
        internal static List<string> RaceLogTagViolations(IList<RaceFile> files, ICollection<string> tags)
        {
            Dictionary<string, int> tagArg = RaceParsekLogTagArgument();
            var call = new Regex(@"(?<![A-Za-z0-9_])ParsekLog\s*\.\s*@?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
            var usingStatic = new Regex(@"^\s*(?:global\s+)?using\s+static\s+[A-Za-z0-9_.]*ParsekLog\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var constDecl = new Regex(@"\bconst\s+string\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*""([^""\\]*)""\s*;", RegexOptions.CultureInvariant);
            var typeDecl = new Regex(@"\b(?:class|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
            var literal = new Regex(@"^@?""([^""\\]*)""$", RegexOptions.CultureInvariant);
            var path = new Regex(@"^(?:([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?([A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);

            var constsByFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var fileOfType = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (RaceFile f in files)
            {
                var consts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Match m in constDecl.Matches(f.KeptText))
                {
                    // Only a declaration the masked text sees as code (not inside a literal).
                    if (f.MaskedText.Substring(m.Index, 5) != "const") continue;
                    consts[m.Groups[1].Value] = m.Groups[2].Value;
                }
                constsByFile[f.Rel] = consts;
                foreach (Match m in typeDecl.Matches(f.MaskedText))
                    fileOfType[m.Groups[1].Value] = f.Rel;
            }

            var violations = new List<string>();
            foreach (RaceFile f in files)
            {
                Func<int, int> lineOf = pos => f.MaskedText.Take(pos).Count(ch => ch == '\n') + 1;
                foreach (Match m in usingStatic.Matches(f.MaskedText))
                    violations.Add(f.Rel + ":" + lineOf(m.Index) + ": [log-tag] using static ParsekLog");
                foreach (Match m in call.Matches(f.MaskedText))
                {
                    string member = m.Groups[1].Value;
                    int argIndex;
                    if (!tagArg.TryGetValue(member, out argIndex)) continue;
                    int pos = m.Index + m.Length;
                    while (pos < f.MaskedText.Length && char.IsWhiteSpace(f.MaskedText[pos])) pos++;
                    if (pos >= f.MaskedText.Length || f.MaskedText[pos] != '(')
                    {
                        violations.Add(f.Rel + ":" + lineOf(m.Index) + ": [log-tag] method group ParsekLog." + member);
                        continue;
                    }
                    int start = pos + 1, depth = 0, arg = 0, argStart = start, argEnd = -1;
                    for (int p = start; p < f.MaskedText.Length; p++)
                    {
                        char c = f.MaskedText[p];
                        if (c == '(' || c == '[' || c == '{') depth++;
                        else if ((c == ')' || c == ']' || c == '}') && depth > 0) depth--;
                        else if ((c == ',' || c == ')') && depth == 0)
                        {
                            if (arg == argIndex) { argEnd = p; break; }
                            if (c == ')') break;
                            arg++;
                            argStart = p + 1;
                        }
                    }
                    string tag = null;
                    if (argEnd > argStart)
                    {
                        string text = f.KeptText.Substring(argStart, argEnd - argStart).Trim();
                        Match lit = literal.Match(text);
                        Match id = path.Match(text);
                        string where;
                        if (lit.Success) tag = lit.Groups[1].Value;
                        else if (id.Success)
                        {
                            string owner = id.Groups[1].Success
                                ? (fileOfType.TryGetValue(id.Groups[1].Value, out where) ? where : null)
                                : f.Rel;
                            Dictionary<string, string> consts;
                            string value;
                            if (owner != null && constsByFile.TryGetValue(owner, out consts)
                                && consts.TryGetValue(id.Groups[2].Value, out value))
                                tag = value;
                        }
                    }
                    if (tag == null)
                        violations.Add(f.Rel + ":" + lineOf(m.Index) + ": [log-tag] unreadable tag in ParsekLog." + member);
                    else if (!tags.Contains(tag))
                        violations.Add(f.Rel + ":" + lineOf(m.Index) + ": [log-tag] " + tag);
                }
            }
            return violations;
        }

        [Fact]
        public void RaceLogTags_EveryRaceLogCallUsesAListedTag()
        {
            RaceScanResult scan = RaceSourceScan(ResolveRepoRoot());
            List<string> violations = RaceLogTagViolations(scan.Files, RaceLogTags);
            Assert.True(violations.Count == 0,
                "a race file logs under a tag that is not in GrepAuditTests.RaceLogTags (plan OV-57: one plain "
                + "tag per subsystem; a task that first logs under a new tag adds it there):\n"
                + string.Join("\n", violations));

            // Anti-vacuity: the cell reads the race files' real calls (R1-03's scenario logs three
            // lines under its const Tag = "Race"), and the reflection finds the tagged members.
            Dictionary<string, int> tagArg = RaceParsekLogTagArgument();
            Assert.Equal(0, tagArg["Info"]);
            Assert.Equal(1, tagArg["Write"]);
            Assert.True(tagArg.Count >= 8, "ParsekLog reflection found only " + tagArg.Count + " tagged member(s)");
            Assert.Contains("Race", RaceLogTags);
            List<string> without = RaceLogTagViolations(scan.Files, new[] { "RivalLane" });
            Assert.True(without.Count(v => v.EndsWith("[log-tag] Race", StringComparison.Ordinal)) >= 3,
                "the tag cell no longer sees the race scenario's log calls:\n" + string.Join("\n", without));
        }

        [Fact]
        public void RaceLogTags_PlantedOffListTag_IsRed()
        {
            var files = new List<RaceFile>
            {
                RaceLexFile("Source/Parsek/RaceWorld/A.cs", "RaceWorld",
                    "class A {\n"
                    + "  const string Tag = \"RivalLane\";\n"
                    + "  void F() {\n"
                    + "    ParsekLog.Info(Tag, \"ok\");\n"
                    + "    ParsekLog.Verbose(\"Race\", \"ok\");\n"
                    + "    ParsekLog.InfoRateLimited(B.Tag, \"k\", \"ok\");\n"
                    + "    // ParsekLog.Info(\"Commented\", \"x\");\n"
                    + "    ParsekLog.Warn(\"RaceImport\", \"off the list\");\n"
                    + "    ParsekLog.Info(\"Race\" + \"Sync\", \"concatenated\");\n"
                    + "    ParsekLog.Write(\"INFO\", \"Rival\", \"x\");\n"
                    + "    Action<string, string> log = ParsekLog.Info;\n"
                    + "    ParsekLog.Info(string.Format(\"{0}\", Tag), $\"x {Tag}\");\n"
                    + "    ParsekLog.ScreenMessage(\"not tagged\", 2f);\n"
                    + "  }\n}\n"),
                RaceLexFile("Source/Parsek/RaceWorld/B.cs", "RaceWorld",
                    "using static Parsek.ParsekLog;\nclass B { internal const string Tag = \"Race\"; }\n"),
            };
            List<string> red = RaceLogTagViolations(files, RaceLogTags);
            Assert.Equal(new[]
            {
                "Source/Parsek/RaceWorld/A.cs:8: [log-tag] RaceImport",
                "Source/Parsek/RaceWorld/A.cs:9: [log-tag] unreadable tag in ParsekLog.Info",
                "Source/Parsek/RaceWorld/A.cs:10: [log-tag] Rival",
                "Source/Parsek/RaceWorld/A.cs:11: [log-tag] method group ParsekLog.Info",
                "Source/Parsek/RaceWorld/A.cs:12: [log-tag] unreadable tag in ParsekLog.Info",
                "Source/Parsek/RaceWorld/B.cs:1: [log-tag] using static ParsekLog",
            }, red.ToArray());
        }
    }
}
