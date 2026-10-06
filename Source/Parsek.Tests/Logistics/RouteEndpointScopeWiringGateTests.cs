using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Source gate for the two route endpoint fixes whose production wiring has no headless
    /// seam (every site resolves a live <c>Vessel</c>):
    /// ROUTE-DELIVERY-INTO-DOCKED-VISITOR (every live probe / writer is built with the
    /// endpoint's part scope, honours it in every part loop, and every multi-stop gate shares
    /// probes per vessel AND scope) and ROUTE-ENDPOINT-CHAIN-GHOST-PROXIMITY-REBIND (the
    /// resolver's proximity step asks the chain hold first and returns on a hold). The pure
    /// halves are pinned by <c>RouteEndpointPartScopeTests</c>, <c>RouteScopedProbeSharingTests</c>
    /// and <c>RouteEndpointChainHoldTests</c>; without this file the scope parameters' null
    /// defaults, or a deleted hold call, would leave every one of those cells green.
    ///
    /// <para>Mechanism: <see cref="SourceScanText"/> (comments stripped, string-literal
    /// contents masked, brace-matched method bodies), the house substitute for an AST walk in
    /// this Roslyn-free test assembly, so a comment or a log string naming a call cannot
    /// satisfy a gate.</para>
    /// </summary>
    public class RouteEndpointScopeWiringGateTests
    {
        /// <summary>Each scoped reader / writer and the argument count of its scoped
        /// constructor (the scope is the last argument).</summary>
        private static readonly Dictionary<string, int> ScopedTypes = new Dictionary<string, int>
        {
            { "LiveDeliveryCapacityProbe", 3 },
            { "LiveDeliveryWriters", 5 },
            { "LiveOriginCargoProbe", 3 },
            { "LiveOriginDebitWriters", 5 },
            { "LiveInventoryPickupWriter", 3 },
        };

        private static readonly Regex ScopedConstruction = new Regex(
            @"\bnew\s+(" + string.Join("|", ScopedTypes.Keys) + @")\s*\(");

        // catches: a production site built without the scope (the null default parameter
        // silently restores the whole-vessel behaviour), a literal null scope, or a scope
        // variable that is not the endpoint's part scope.
        [Fact]
        public void EveryProductionReaderAndWriterIsBuiltWithTheEndpointScope()
        {
            var problems = new List<string>();
            int sites = 0;
            foreach (KeyValuePair<string, string> file in ProductionSources())
            {
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(file.Value);
                foreach (Match m in ScopedConstruction.Matches(prepared))
                {
                    sites++;
                    string type = m.Groups[1].Value;
                    int openParen = m.Index + m.Length - 1;
                    List<string> args = TopLevelArguments(prepared, openParen);
                    string where = file.Key + ":" + LineOf(prepared, m.Index) + " new " + type;
                    if (args.Count != ScopedTypes[type])
                    {
                        problems.Add(where + " passes " + args.Count + " argument(s), the scoped constructor takes "
                            + ScopedTypes[type]);
                        continue;
                    }
                    string scopeArg = args[args.Count - 1].Trim();
                    if (!IsIdentifier(scopeArg))
                    {
                        problems.Add(where + " passes '" + scopeArg + "' as the scope, not a scope variable");
                        continue;
                    }
                    int bodyOpen = SourceScanText.EnclosingMethodBodyStart(prepared, m.Index);
                    if (bodyOpen < 0)
                    {
                        problems.Add(where + ": enclosing method body not found");
                        continue;
                    }
                    string body = SourceScanText.BraceMatchedBlock(prepared, bodyOpen);
                    if (!AssignsFromScopeBuilder(body, scopeArg))
                        problems.Add(where + ": scope '" + scopeArg + "' is not assigned from "
                            + "RouteEndpointPartScope.ForEndpoint / ForEndpointOfRoute in the enclosing method");
                }
            }

            // Non-vacuity: the production sites this fix wired (delivery, origin debit + its
            // inventory half, pickup debit, inventory pickup debit, origin gate + its inventory
            // half, pickup gate probe + inventory, destination gate, two Logistics window
            // probes).
            Assert.True(sites >= 12, "scope wiring gate found only " + sites + " construction site(s)");
            Assert.True(problems.Count == 0,
                "endpoint part-scope wiring: " + string.Join("; ", problems));
        }

        // catches: a reader / writer that accepts the scope but ignores it in one of its part
        // loops (or stops storing it), which would keep filling or draining a docked visitor.
        [Fact]
        public void EveryPartLoopOfTheScopedClassesHonoursTheScope()
        {
            var loop = new Regex(
                @"for\s*\(\s*int\s+i\s*=\s*0\s*;\s*i\s*<\s*(vessel\.parts|pv\.protoPartSnapshots)\.Count");
            var guardedLoop = new Regex(
                @"for\s*\(\s*int\s+i\s*=\s*0\s*;\s*i\s*<\s*(vessel\.parts|pv\.protoPartSnapshots)\.Count[^)]*\)\s*\{\s*"
                + @"if\s*\(\s*!\s*EndpointPartScope\s*\.\s*Includes\s*\(\s*partScope\s*,\s*i\s*\)\s*\)\s*continue\s*;");
            var storesScope = new Regex(
                @"this\s*\.\s*partScope\s*=\s*EndpointPartScope\s*\.\s*ForBranch\s*\(\s*partScope\s*,");

            var problems = new List<string>();
            int loops = 0;
            foreach (string type in ScopedTypes.Keys)
            {
                string rel = "Logistics/" + type + ".cs";
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource(rel));
                int all = loop.Matches(prepared).Count;
                int guarded = guardedLoop.Matches(prepared).Count;
                loops += all;
                if (all == 0)
                    problems.Add(rel + ": no part loop found (the gate's loop pattern drifted)");
                if (guarded != all)
                    problems.Add(rel + ": " + guarded + " of " + all + " part loop(s) open with the scope guard");
                if (!storesScope.IsMatch(prepared))
                    problems.Add(rel + ": the constructor does not store the scope through EndpointPartScope.ForBranch");
            }

            // The delivery writer resolves inventory slots by address; both resolvers refuse a
            // slot outside the scope.
            string writers = SourceScanText.StripCommentsAndMaskLiterals(
                ReadParsekSource("Logistics/LiveDeliveryWriters.cs"));
            int slotGuards = Regex.Matches(writers,
                @"EndpointPartScope\s*\.\s*Includes\s*\(\s*partScope\s*,\s*slot\s*\.\s*PartIndex\s*\)").Count;
            if (slotGuards != 2)
                problems.Add("LiveDeliveryWriters: " + slotGuards + " slot-address scope guard(s), expected 2");

            Assert.True(loops >= 22, "part-loop gate found only " + loops + " loop(s)");
            Assert.True(problems.Count == 0, "endpoint part-scope honouring: " + string.Join("; ", problems));
        }

        // catches: a multi-stop gate going back to one probe per vessel pid (built with the
        // first stop's scope), which plans a second endpoint's cargo against the first one's
        // tanks and then drops what the second one's writer cannot take.
        [Fact]
        public void MultiStopGatesSharePerVesselAndScope()
        {
            AssertSharesThroughScopedCache("Logistics/LiveRouteRuntimeEnvironment.cs",
                "public bool DestinationHasCapacity(");
            AssertSharesThroughScopedCache("UI/LogisticsWindowUI.cs",
                "private static int FindFullStopIndex(");

            // The pickup gate groups sources by pid plus the resolution's scope key.
            string env = SourceScanText.StripCommentsAndMaskLiterals(
                ReadParsekSource("Logistics/LiveRouteRuntimeEnvironment.cs"));
            string pickup = MethodBody(env, "private bool PickupSourcesHaveCargo(");
            Assert.True(Regex.IsMatch(pickup,
                    @"\.\s*WithPartScopeKey\s*\(\s*EndpointPartScope\s*\.\s*KeyOf\s*\(\s*sourceScope\s*\)\s*\)"),
                "pickup gate: the source resolution must carry its part scope key");

            string gate = SourceScanText.StripCommentsAndMaskLiterals(
                ReadParsekSource("Logistics/RoutePickupSourceGate.cs"));
            Assert.True(Regex.IsMatch(gate, @"res\s*\.\s*PartScopeKey"),
                "pickup gate: sources must group by the resolution's PartScopeKey");
        }

        // catches: the resolver's proximity step losing the chain hold (or running it after
        // the proximity search / the rebind), which re-opens the permanent re-point of a
        // chain-ghosted base's route to a parked neighbour.
        [Fact]
        public void ResolverProximityStepAsksTheChainHoldFirst()
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                ReadParsekSource("Logistics/RouteEndpointResolver.cs"));
            string body = MethodBody(prepared, "internal static bool TryResolveEndpoint(");

            string holdCondition = SourceScanText.IfConditions(body)
                .FirstOrDefault(c => Regex.IsMatch(c, @"RouteEndpointChainHold\s*\.\s*IsEndpointHeldLive\s*\("));
            Assert.True(holdCondition != null,
                "resolver: the chain hold must be read as a branch condition in TryResolveEndpoint");
            Assert.False(Regex.IsMatch(holdCondition, @"\bfalse\s*&&|&&\s*false\b|^\s*!"),
                "resolver: the chain hold condition is short-circuited or negated: " + holdCondition);

            int hold = Regex.Match(body, @"RouteEndpointChainHold\s*\.\s*IsEndpointHeldLive\s*\(").Index;
            int pidStep = IndexOrFail(body, @"step\s*==\s*EndpointResolutionStep\s*\.\s*Pid");
            int search = IndexOrFail(body, @"\bTrySurfaceFallbackPure\s*\(");
            int rebind = IndexOrFail(body, @"RouteEndpointTransfer\s*\.\s*ApplyTransfers\s*\(");
            Assert.True(pidStep < hold && hold < search && search < rebind,
                "resolver: the hold must sit in the proximity step, before the search and the rebind "
                + "(pidStep=" + pidStep + " hold=" + hold + " search=" + search + " rebind=" + rebind + ")");

            // The held branch returns false with the hold reason.
            int ifAt = body.LastIndexOf("if", hold, StringComparison.Ordinal);
            int open = body.IndexOf('{', hold);
            string held = SourceScanText.BraceMatchedBlock(body, open);
            Assert.True(ifAt >= 0 && open < search, "resolver: the held branch block was not found");
            Assert.Matches(@"reason\s*=\s*RouteEndpointChainHold\s*\.\s*HoldReason\s*;", held);
            Assert.Matches(@"return\s+false\s*;", held);
        }

        // ------------------------------------------------------------------

        private static void AssertSharesThroughScopedCache(string rel, string signature)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource(rel));
            string body = MethodBody(prepared, signature);
            Match probe = Regex.Match(body,
                @"new\s+LiveDeliveryCapacityProbe\s*\(\s*[^,()]+,\s*[^,()]+,\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)");
            Assert.True(probe.Success, rel + " " + signature + ": scoped probe construction not found");
            string scope = probe.Groups[1].Value;
            Assert.True(Regex.IsMatch(body,
                    @"new\s+EndpointScopedCache\s*<\s*IDeliveryCapacityProbe\s*>\s*\("),
                rel + " " + signature + ": shared probes must live in an EndpointScopedCache");
            Assert.True(Regex.IsMatch(body,
                    @"\.\s*GetOrAdd\s*\(\s*[^,()]+\.persistentId\s*,\s*" + Regex.Escape(scope) + @"\s*,"),
                rel + " " + signature + ": the shared probe must be keyed by the vessel pid AND the same scope '"
                + scope + "' the probe reads through");
        }

        private static bool AssignsFromScopeBuilder(string body, string identifier)
        {
            var assign = new Regex(
                @"(?<![A-Za-z0-9_.])" + Regex.Escape(identifier) + @"\s*=(?!=)([^;]*);");
            foreach (Match m in assign.Matches(body))
            {
                if (Regex.IsMatch(m.Groups[1].Value,
                        @"RouteEndpointPartScope\s*\.\s*(ForEndpoint|ForEndpointOfRoute)\s*\("))
                    return true;
            }
            return false;
        }

        private static bool IsIdentifier(string text)
        {
            return Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_]*$") && text != "null";
        }

        /// <summary>The comma-separated top-level arguments of the call whose '(' is at
        /// <paramref name="openParen"/> (nested parentheses, brackets and braces kept whole).</summary>
        private static List<string> TopLevelArguments(string prepared, int openParen)
        {
            var args = new List<string>();
            int depth = 0;
            int start = openParen + 1;
            for (int i = openParen; i < prepared.Length; i++)
            {
                char c = prepared[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        string last = prepared.Substring(start, i - start);
                        if (args.Count > 0 || last.Trim().Length > 0) args.Add(last);
                        return args;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add(prepared.Substring(start, i - start));
                    start = i + 1;
                }
            }
            throw new InvalidOperationException("unbalanced argument list at " + openParen);
        }

        private static string MethodBody(string prepared, string signatureFragment)
        {
            int sig = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found: " + signatureFragment);
            Assert.True(prepared.IndexOf(signatureFragment, sig + 1, StringComparison.Ordinal) < 0,
                "signature is ambiguous: " + signatureFragment);
            int open = prepared.IndexOf('{', sig);
            Assert.True(open >= 0, "no body for " + signatureFragment);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int IndexOrFail(string body, string pattern)
        {
            Match m = Regex.Match(body, pattern);
            Assert.True(m.Success, "pattern not found: " + pattern);
            return m.Index;
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        /// <summary>Every production .cs file under Source/Parsek, keyed by its relative path.
        /// The in-game test framework is excluded: its cells build probes over a live test
        /// vessel on purpose, with no route endpoint to scope to.</summary>
        private static IEnumerable<KeyValuePair<string, string>> ProductionSources()
        {
            string root = ParsekSourceRoot();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("InGameTests/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yield return new KeyValuePair<string, string>(rel, File.ReadAllText(path));
            }
        }

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }

        private static string ReadParsekSource(string relPath)
        {
            string path = Path.Combine(ParsekSourceRoot(), relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
