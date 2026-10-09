using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless resolution of every UNTYPED reflection lookup the in-game tests make on a
    /// Parsek-owned type: <c>typeof(T).GetMethod("Name")</c> or
    /// <c>typeof(T).GetMethod("Name", flags)</c> with no parameter-type array. Such a
    /// lookup throws <see cref="AmbiguousMatchException"/> the moment production adds an
    /// overload of that name, and returns null when the member is renamed; either way the
    /// in-game cell fails or skips only inside a flight. Resolving the same lookup here
    /// reds the suite instead (the drift that cost lanes H38 / H39 / H40 / HV-1 when
    /// <c>RouteLedgerRetire.RetireFutureRouteActions</c> gained an overload).
    ///
    /// <para>The scan runs over comment-stripped, literal-masked source (length-preserving,
    /// <see cref="SourceScanText"/>), so a lookup quoted in a comment is not a lookup. A
    /// lookup whose type is not in the Parsek assembly (a stock KSP type) is out of scope:
    /// stock does not change under us.</para>
    /// </summary>
    public class InGameTestReflectionLookupTests
    {
        private static readonly Regex TypeofGetMethod = new Regex(
            @"typeof\(\s*(?<type>[A-Za-z_][\w\.]*)\s*\)\s*\.\s*GetMethod\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex FlagToken = new Regex(
            @"BindingFlags\s*\.\s*(?<flag>\w+)", RegexOptions.Compiled);

        private static readonly Regex NameArg = new Regex(
            @"^\s*(?:""(?<lit>[^""]+)""|nameof\(\s*(?:[\w\.]+\.)?(?<nameof>\w+)\s*\))\s*$",
            RegexOptions.Compiled);

        internal sealed class Lookup
        {
            public string File;
            public int Line;
            public string TypeName;
            public string MemberName;
            public BindingFlags Flags;
        }

        [Fact]
        public void EveryUntypedInGameGetMethodOnAParsekType_ResolvesToExactlyOneMethod()
        {
            string dir = Path.Combine(FindRepoRoot(), "Source", "Parsek", "InGameTests");
            Assert.True(Directory.Exists(dir), "InGameTests directory not found: " + dir);

            Assembly parsek = typeof(ParsekLog).Assembly;
            Type[] parsekTypes;
            try { parsekTypes = parsek.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { parsekTypes = ex.Types.Where(t => t != null).ToArray(); }

            var failures = new List<string>();
            int resolved = 0;
            foreach (string path in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Lookup lookup in ScanUntypedLookups(path, File.ReadAllText(path)))
                {
                    string shortName = lookup.TypeName.Contains(".")
                        ? lookup.TypeName.Substring(lookup.TypeName.LastIndexOf('.') + 1)
                        : lookup.TypeName;
                    Type[] candidates = parsekTypes.Where(t => t.Name == shortName).ToArray();
                    if (candidates.Length == 0)
                        continue; // not a Parsek type (stock KSP / Unity): out of scope
                    string where = Path.GetFileName(lookup.File) + ":" + lookup.Line
                        + " typeof(" + lookup.TypeName + ").GetMethod(\"" + lookup.MemberName + "\")";
                    if (candidates.Length > 1)
                    {
                        failures.Add(where + " - type name is ambiguous in the Parsek assembly ("
                            + string.Join(", ", candidates.Select(c => c.FullName)) + ")");
                        continue;
                    }

                    try
                    {
                        MethodInfo m = candidates[0].GetMethod(lookup.MemberName, lookup.Flags);
                        if (m == null)
                            failures.Add(where + " - resolves to null with flags " + lookup.Flags
                                + " (renamed or wrong binding flags; the in-game cell would skip or NRE)");
                        else
                            resolved++;
                    }
                    catch (AmbiguousMatchException)
                    {
                        failures.Add(where + " - AmbiguousMatchException with flags " + lookup.Flags
                            + " (the name is overloaded; pass the exact parameter types, or call it directly)");
                    }
                }
            }

            Assert.True(failures.Count == 0,
                "Untyped in-game reflection lookups that would fail in a flight:\n  "
                + string.Join("\n  ", failures));
            // Non-vacuity: the scan must actually find and resolve the known lookups
            // (ParsekFlight, RouteOrchestrator, MissionLoopUnitBuilder, ... at authoring time).
            Assert.True(resolved >= 5,
                "Expected at least 5 resolved untyped lookups on Parsek types; found " + resolved
                + " (has the scan regex stopped matching?)");
        }

        [Fact]
        public void Scan_FindsAnUntypedLookup_SkipsTypedOnesAndComments()
        {
            string src =
                "class C {\n"
                + "  // typeof(Foo).GetMethod(\"InComment\", BindingFlags.Static);\n"
                + "  object a = typeof(Foo).GetMethod(\n"
                + "      \"Untyped\",\n"
                + "      BindingFlags.Static | BindingFlags.NonPublic);\n"
                + "  object b = typeof(Foo).GetMethod(\"Typed\", BindingFlags.Static, null,\n"
                + "      new[] { typeof(int) }, null);\n"
                + "  object c = typeof(Bar).GetMethod(nameof(Bar.Named));\n"
                + "}\n";
            List<Lookup> found = ScanUntypedLookups("x.cs", src);
            Assert.Equal(2, found.Count);
            Assert.Equal("Foo", found[0].TypeName);
            Assert.Equal("Untyped", found[0].MemberName);
            Assert.Equal(3, found[0].Line);
            Assert.Equal(BindingFlags.Static | BindingFlags.NonPublic, found[0].Flags);
            Assert.Equal("Named", found[1].MemberName);
            Assert.Equal(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, found[1].Flags);
        }

        internal static List<Lookup> ScanUntypedLookups(string path, string raw)
        {
            var result = new List<Lookup>();
            string stripped = SourceScanText.StripCSharpComments(raw);
            string masked = SourceScanText.StripCommentsAndMaskLiterals(raw);

            foreach (Match match in TypeofGetMethod.Matches(masked))
            {
                int open = match.Index + match.Length - 1; // the '(' of GetMethod(
                var argSpans = new List<KeyValuePair<int, int>>();
                int depth = 0;
                int argStart = open + 1;
                int close = -1;
                for (int i = open; i < masked.Length; i++)
                {
                    char ch = masked[i];
                    if (ch == '(' || ch == '[' || ch == '{') depth++;
                    else if (ch == ')' || ch == ']' || ch == '}')
                    {
                        depth--;
                        if (depth == 0) { close = i; break; }
                    }
                    else if (ch == ',' && depth == 1)
                    {
                        argSpans.Add(new KeyValuePair<int, int>(argStart, i));
                        argStart = i + 1;
                    }
                }
                if (close < 0) continue;
                argSpans.Add(new KeyValuePair<int, int>(argStart, close));
                if (argSpans.Count > 2) continue; // typed lookup: parameter types pinned

                string nameText = stripped.Substring(argSpans[0].Key, argSpans[0].Value - argSpans[0].Key);
                Match nameMatch = NameArg.Match(nameText);
                if (!nameMatch.Success) continue; // a computed name: not resolvable statically
                string member = nameMatch.Groups["lit"].Success
                    ? nameMatch.Groups["lit"].Value
                    : nameMatch.Groups["nameof"].Value;

                BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
                if (argSpans.Count == 2)
                {
                    string flagText = stripped.Substring(argSpans[1].Key, argSpans[1].Value - argSpans[1].Key);
                    flags = BindingFlags.Default;
                    foreach (Match f in FlagToken.Matches(flagText))
                        flags |= (BindingFlags)Enum.Parse(typeof(BindingFlags), f.Groups["flag"].Value);
                }

                int line = 1;
                for (int i = 0; i < match.Index; i++)
                    if (raw[i] == '\n') line++;

                result.Add(new Lookup
                {
                    File = path,
                    Line = line,
                    TypeName = match.Groups["type"].Value,
                    MemberName = member,
                    Flags = flags,
                });
            }
            return result;
        }

        private static string FindRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "Source", "Parsek")))
                    return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new InvalidOperationException("repo root not found");
        }
    }
}
