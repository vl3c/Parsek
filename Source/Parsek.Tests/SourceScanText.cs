using System;
using System.Collections.Generic;
using System.Text;

namespace Parsek.Tests
{
    /// <summary>
    /// Shared text preparation for the source-scanning test cells (the ones that assert a
    /// PRODUCT call site exists, because the site itself is an IMGUI draw path with no
    /// headless seam).
    ///
    /// <para><b>Why this exists.</b> A raw substring / regex scan over source text reads
    /// COMMENTS as code. Every such cell is therefore vacuously green the moment the needle
    /// it looks for appears in a comment near the site - which is exactly what a comment
    /// explaining the guard says. The house rule ("guards that derive a set from source walk
    /// the AST, never regex over source text") has no Roslyn in this test assembly, so the
    /// next best thing is this: blank the comments out first, so what the scan matches is
    /// code.</para>
    ///
    /// <para><b>Length is preserved.</b> Every helper here replaces characters one-for-one
    /// with spaces (newlines kept as newlines), so an index into the prepared text is the
    /// SAME index in the original file. Line numbers, line-based slicing and offsets taken
    /// from one can be used against the other.</para>
    /// </summary>
    internal static class SourceScanText
    {
        /// <summary>
        /// Returns <paramref name="source"/> with every <c>//</c> line comment (<c>///</c>
        /// doc comments included) and every <c>/* */</c> block comment replaced by spaces,
        /// leaving string and character literals untouched: <c>"http://x"</c> and
        /// <c>"/* not a comment */"</c> survive whole. Verbatim (<c>@"..."</c>, <c>$@"..."</c>,
        /// <c>@$"..."</c>) and interpolated strings are handled; newlines are preserved, so
        /// the result has the same length and the same line count as the input.
        /// </summary>
        internal static string StripCSharpComments(string source)
        {
            if (string.IsNullOrEmpty(source)) return source ?? string.Empty;

            var sb = new StringBuilder(source.Length);
            int i = 0;
            int n = source.Length;
            while (i < n)
            {
                char c = source[i];
                char d = i + 1 < n ? source[i + 1] : '\0';

                if (c == '/' && d == '/')
                {
                    while (i < n && source[i] != '\n' && source[i] != '\r') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && d == '*')
                {
                    sb.Append("  ");
                    i += 2;
                    while (i < n && !(source[i] == '*' && i + 1 < n && source[i + 1] == '/'))
                    {
                        sb.Append(source[i] == '\n' || source[i] == '\r' ? source[i] : ' ');
                        i++;
                    }
                    if (i < n) { sb.Append("  "); i += 2; }
                    continue;
                }
                if (IsVerbatimStringStart(source, i, out int verbatimPrefix))
                {
                    for (int k = 0; k < verbatimPrefix; k++) { sb.Append(source[i]); i++; }
                    sb.Append(source[i]); i++;                       // the opening quote
                    while (i < n)
                    {
                        if (source[i] == '"')
                        {
                            // "" is an escaped quote INSIDE a verbatim string, not the end.
                            if (i + 1 < n && source[i + 1] == '"') { sb.Append('"').Append('"'); i += 2; continue; }
                            sb.Append('"'); i++; break;
                        }
                        sb.Append(source[i]); i++;
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    sb.Append(c); i++;
                    while (i < n)
                    {
                        if (source[i] == '\\' && i + 1 < n)
                        {
                            sb.Append(source[i]).Append(source[i + 1]); i += 2; continue;
                        }
                        if (source[i] == quote) { sb.Append(quote); i++; break; }
                        // A non-verbatim literal cannot span a line. Bailing here keeps an
                        // apostrophe inside a comment (already blanked) or a stray quote from
                        // swallowing the rest of the file.
                        if (source[i] == '\n') break;
                        sb.Append(source[i]); i++;
                    }
                    continue;
                }

                sb.Append(c); i++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Returns <paramref name="source"/> with the CONTENT of every string / character
        /// literal replaced by spaces (the quotes stay). Length-preserving like
        /// <see cref="StripCSharpComments"/>. Use it before counting braces: an interpolated
        /// string's <c>{hole}</c> and a literal <c>"}"</c> otherwise unbalance the count.
        /// </summary>
        internal static string MaskStringLiteralContents(string source)
        {
            if (string.IsNullOrEmpty(source)) return source ?? string.Empty;

            var sb = new StringBuilder(source.Length);
            int i = 0;
            int n = source.Length;
            while (i < n)
            {
                char c = source[i];
                if (IsVerbatimStringStart(source, i, out int verbatimPrefix))
                {
                    for (int k = 0; k < verbatimPrefix; k++) { sb.Append(source[i]); i++; }
                    sb.Append(source[i]); i++;
                    while (i < n)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < n && source[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                            sb.Append('"'); i++; break;
                        }
                        sb.Append(source[i] == '\n' || source[i] == '\r' ? source[i] : ' ');
                        i++;
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    sb.Append(c); i++;
                    while (i < n)
                    {
                        if (source[i] == '\\' && i + 1 < n) { sb.Append("  "); i += 2; continue; }
                        if (source[i] == quote) { sb.Append(quote); i++; break; }
                        if (source[i] == '\n') break;
                        sb.Append(' '); i++;
                    }
                    continue;
                }
                sb.Append(c); i++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Comments blanked AND literal contents masked - the form to count braces over.
        /// </summary>
        internal static string StripCommentsAndMaskLiterals(string source)
        {
            return MaskStringLiteralContents(StripCSharpComments(source));
        }

        /// <summary>
        /// The positions of the <c>{</c> characters still OPEN at <paramref name="index"/>,
        /// outermost first. In a conventional file that is
        /// <c>[namespace, class, method, ...inner blocks]</c>, so <c>[2]</c> is the enclosing
        /// method's body brace - derived from the file's block structure rather than from a
        /// declaration regex, so modifiers, attribute lines and wrapped signatures do not
        /// matter. <paramref name="prepared"/> must come from
        /// <see cref="StripCommentsAndMaskLiterals"/>.
        /// </summary>
        internal static List<int> OpenBlockStack(string prepared, int index)
        {
            var open = new List<int>();
            for (int i = 0; i < index && i < prepared.Length; i++)
            {
                if (prepared[i] == '{') open.Add(i);
                else if (prepared[i] == '}' && open.Count > 0) open.RemoveAt(open.Count - 1);
            }
            return open;
        }

        /// <summary>
        /// The enclosing method body's opening brace position for <paramref name="index"/>,
        /// or -1 when the block structure is not namespace -> type -> method (which the
        /// caller should assert on rather than silently pass).
        /// </summary>
        internal static int EnclosingMethodBodyStart(string prepared, int index)
        {
            List<int> open = OpenBlockStack(prepared, index);
            return open.Count >= 3 ? open[2] : -1;
        }

        /// <summary>
        /// Every <c>if (...)</c> condition in <paramref name="prepared"/>, as source text with
        /// the outer parentheses stripped. Parentheses are balanced, so a condition containing
        /// a call keeps its whole argument list. Used to assert that a gate is READ as a
        /// branch condition, not merely mentioned: an assignment whose result is never
        /// branched on appears nowhere in this list.
        /// </summary>
        internal static List<string> IfConditions(string prepared)
        {
            var conditions = new List<string>();
            var matches = System.Text.RegularExpressions.Regex.Matches(
                prepared ?? string.Empty, @"\bif\s*\(");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                int openParen = m.Index + m.Length - 1;
                int depth = 0;
                for (int i = openParen; i < prepared.Length; i++)
                {
                    if (prepared[i] == '(') depth++;
                    else if (prepared[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            conditions.Add(prepared.Substring(openParen + 1, i - openParen - 1));
                            break;
                        }
                    }
                }
            }
            return conditions;
        }

        /// <summary>Whole-word (C# identifier boundary) containment test.</summary>
        internal static bool ContainsIdentifier(string text, string identifier)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(identifier)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                text, @"(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(identifier)
                + @"(?![A-Za-z0-9_])");
        }

        /// <summary>
        /// True when <paramref name="i"/> starts a verbatim string literal opener - one of
        /// <c>@"</c>, <c>$@"</c>, <c>@$"</c> - and reports how many characters precede the
        /// opening quote.
        /// </summary>
        private static bool IsVerbatimStringStart(string source, int i, out int prefixLength)
        {
            prefixLength = 0;
            int n = source.Length;
            if (i + 1 < n && source[i] == '@' && source[i + 1] == '"') { prefixLength = 1; return true; }
            if (i + 2 < n && source[i + 1] == '@' && source[i + 2] == '"'
                && (source[i] == '$')) { prefixLength = 2; return true; }
            if (i + 2 < n && source[i] == '@' && source[i + 1] == '$' && source[i + 2] == '"')
            { prefixLength = 2; return true; }
            return false;
        }
    }
}
