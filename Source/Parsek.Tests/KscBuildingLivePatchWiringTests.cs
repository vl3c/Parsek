using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Wiring fence for the live KSC building patch (PR #1784 review). The pure fold
    /// (<c>ComputeBuildingDestroyedAtUt</c>) and decision (<c>ResolveLiveDestructionPatch</c>)
    /// are unit-tested; what no unit test can see is WHAT FEEDS the Unity-bound
    /// <c>PatchDestructionState</c>. If a caller handed it the walk's state, or folded the
    /// ledger at <c>double.MaxValue</c> / the walk cutoff instead of live UT, future rows would
    /// reach live buildings again with the suite green. So this reads Source/Parsek
    /// (comment-stripped, CRLF-normalized) and pins, across the WHOLE mod source:
    ///   - exactly one call site of <c>PatchDestructionState(</c>, inside
    ///     <c>PatchLiveDestructionState</c>, whose first argument is the fold over the ELS at
    ///     <c>liveUt</c>;
    ///   - exactly one call site of <c>ComputeBuildingDestroyedAtUt(</c>, that same one;
    ///   - <c>liveUt</c> in that method comes from <c>ReadLiveUniversalTime()</c> and nothing
    ///     there mentions <c>MaxValue</c> or the walk's module.
    /// Declarations are excluded by matching the declaration shape, not by line.
    /// </summary>
    public class KscBuildingLivePatchWiringTests
    {
        private static string SourceRoot()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(projectRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "source not found at " + root);
            return root;
        }

        private static string Normalize(string src)
        {
            src = src.Replace("\r\n", "\n");
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(src, @"//[^\n]*", " ");
        }

        private static Dictionary<string, string> AllSources()
        {
            var root = SourceRoot();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal))
                    continue;
                result[rel] = Normalize(File.ReadAllText(f));
            }
            Assert.True(result.Count > 100, "source walk found only " + result.Count + " files");
            return result;
        }

        /// <summary>The argument text of the call whose '(' is at <paramref name="open"/>.</summary>
        private static string ArgsAt(string src, int open)
        {
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '(') depth++;
                else if (src[i] == ')' && --depth == 0)
                    return Regex.Replace(src.Substring(open + 1, i - open - 1), @"\s+", " ").Trim();
            }
            throw new InvalidOperationException("unbalanced call at " + open);
        }

        /// <summary>
        /// Every call site of <paramref name="name"/> as (file, args, enclosing-method-name).
        /// A match preceded by a return type + name shape (<c>void Name(</c>,
        /// <c>Dictionary&lt;string, bool&gt; Name(</c>) is the declaration and is skipped.
        /// </summary>
        private static List<Tuple<string, string, string>> CallSites(string name)
        {
            var sites = new List<Tuple<string, string, string>>();
            var callRe = new Regex(@"(?<![A-Za-z0-9_])" + name + @"\s*\(");
            var declRe = new Regex(@"(void|>|bool|string|double|int)\s+" + name + @"\s*\($");
            var methodRe = new Regex(@"(?:internal|private|public|protected)\s+(?:static\s+)?[\w<>,\s\[\]\?]+?\s(\w+)\s*\(");
            foreach (var kv in AllSources())
            {
                string src = kv.Value;
                foreach (Match m in callRe.Matches(src))
                {
                    int open = m.Index + m.Length - 1;
                    string head = src.Substring(Math.Max(0, m.Index - 60), m.Index - Math.Max(0, m.Index - 60) + m.Length);
                    if (declRe.IsMatch(head.TrimEnd()))
                        continue;
                    string before = src.Substring(0, m.Index);
                    var methods = methodRe.Matches(before);
                    string enclosing = methods.Count > 0 ? methods[methods.Count - 1].Groups[1].Value : "";
                    sites.Add(Tuple.Create(kv.Key, ArgsAt(src, open), enclosing));
                }
            }
            return sites;
        }

        [Fact]
        public void PatchDestructionState_IsFedOnlyFromTheFoldAtLiveUt()
        {
            var sites = CallSites("PatchDestructionState");
            var site = Assert.Single(sites);
            Assert.Equal("GameActions/FacilityStatePatcher.cs", site.Item1);
            Assert.Equal("PatchLiveDestructionState", site.Item3);
            Assert.Equal("ComputeBuildingDestroyedAtUt(EffectiveState.ComputeELS(), liveUt), liveUt", site.Item2);
        }

        [Fact]
        public void TheFold_IsComputedOnlyOnce_AtLiveUt()
        {
            var site = Assert.Single(CallSites("ComputeBuildingDestroyedAtUt"));
            Assert.Equal("PatchLiveDestructionState", site.Item3);
            Assert.Equal("EffectiveState.ComputeELS(), liveUt", site.Item2);
        }

        [Fact]
        public void LiveUt_ComesFromTheLiveClock_AndTheMethodNeverSeesTheWalk()
        {
            string src = AllSources()["GameActions/FacilityStatePatcher.cs"];
            int start = src.IndexOf("internal static void PatchLiveDestructionState()", StringComparison.Ordinal);
            Assert.True(start >= 0, "PatchLiveDestructionState declaration not found");
            int open = src.IndexOf('{', start);
            int depth = 0, end = open;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) { end = i; break; }
            }
            string body = src.Substring(open, end - open);

            Assert.Contains("double liveUt = ReadLiveUniversalTime();", body);
            Assert.Single(Regex.Matches(body, @"\bliveUt\s*=").Cast<Match>());
            Assert.DoesNotContain("MaxValue", body);
            Assert.DoesNotContain("FacilitiesModule", body);
            Assert.DoesNotContain("GetAllFacilities", body);
            Assert.DoesNotContain("allFacilities", body);
        }
    }
}
