using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.Patches;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FAR-SWITCH-INTENT-TTL-RACE: a Switch-To to an UNLOADED vessel arms no MapSwitchTo
    /// intent. Stock's unloaded branch saves the scenario (twice) before it returns, so an
    /// armed intent would cross the FLIGHT reload and start a segment whenever the reload beat
    /// the 2 s TTL; the design outcome for that switch is no segment. A LOADED target still
    /// arms (the in-scene same-frame consume).
    /// </summary>
    [Collection("Sequential")]
    public class FarSwitchIntentArmTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FarSwitchIntentArmTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ----- the predicate -----

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ShouldArmIntentForSwitchTarget_ArmsOnlyALoadedTarget(bool targetLoaded, bool expected)
        {
            // Fails if: an unloaded target arms again (the far-scene race returns), or a
            // loaded target stops arming (every in-bubble switch segment is lost).
            Assert.Equal(expected, MapFocusObjectOnSelectPatch.ShouldArmIntentForSwitchTarget(targetLoaded));
        }

        // ----- the per-site helper and its log line -----

        [Fact]
        public void DeclineArm_UnloadedTarget_DeclinesAndLogsOneInfoLine()
        {
            bool declined = MapFocusObjectOnSelectPatch.DeclineArmForUnloadedTarget(
                targetLoaded: false, targetPid: 4267147709u, label: "pre-switch-dialog");

            Assert.True(declined);
            var hits = logLines.Where(l => l.Contains("[SwitchIntentPatch]")
                && l.Contains("pre-switch-dialog intent not armed: reason=far-switch-target-unloaded")
                && l.Contains("targetPid=4267147709")).ToList();
            Assert.Single(hits);
            Assert.Contains("[INFO]", hits[0]);
            Assert.Equal("far-switch-target-unloaded", MapFocusObjectOnSelectPatch.FarSwitchNotArmedReason);
        }

        [Fact]
        public void DeclineArm_LoadedTarget_DoesNotDeclineAndLogsNothing()
        {
            // The mirror case: an in-bubble switch keeps arming with the 2 s TTL.
            bool declined = MapFocusObjectOnSelectPatch.DeclineArmForUnloadedTarget(
                targetLoaded: true, targetPid: 807470910u, label: "Map Switch-To");

            Assert.False(declined);
            Assert.DoesNotContain(logLines, l => l.Contains("intent not armed"));
            Assert.Equal(2.0, StockActionIntentMarker.MapSwitchToTtlSeconds);
        }

        [Fact]
        public void ClearUnconsumedIntent_NothingArmed_ClearsNothingAndLogsNothing()
        {
            // An unloaded target hands the Postfix Guid.Empty; the refund must not log a
            // refused-no-switch clear for a marker that was never armed.
            MapFocusObjectOnSelectPatch.ClearUnconsumedIntent(Guid.Empty, "Map Switch-To");
            Assert.DoesNotContain(logLines, l => l.Contains("refused-no-switch"));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]"));
        }

        [Fact]
        public void SeamGate_UnloadedTargetRefuses_ThroughTheSamePredicate()
        {
            // The automation seam's plain path is the third MapSwitchTo arm site; it refuses
            // an unloaded target instead of arming, read from the same predicate.
            Assert.Equal(SwitchClickGateDecision.RefusedTargetUnloaded,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.NoPriorSession,
                    targetIsLoaded: false));
            Assert.Equal(SwitchClickGateDecision.Proceed,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.NoPriorSession,
                    targetIsLoaded: true));
        }

        // ----- source gates: both arm sites read the predicate before arming -----

        [Fact]
        public void PrepareOwnedVesselSwitch_DeclinesUnloadedBeforeItArms()
        {
            string body = MethodBody(MapPatchCode(), "internal static bool PrepareOwnedVesselSwitch(");
            int decline = IndexOfCall(body, "DeclineArmForUnloadedTarget(vessel.loaded, vessel.persistentId, label)");
            int arm = IndexOfCall(body, "scenario.ArmStockActionIntent(");
            Assert.True(decline >= 0, "PrepareOwnedVesselSwitch must call DeclineArmForUnloadedTarget");
            Assert.True(arm > decline, "the decline must precede the arm");
            // The decline returns true (stock still switches) and arms nothing.
            string between = body.Substring(decline, arm - decline);
            int ret = between.IndexOf("return true;", StringComparison.Ordinal);
            Assert.True(ret > 0, "a declined target must return true before the arm");
            Assert.DoesNotContain("armedIntentId =", between.Substring(0, ret));
        }

        [Fact]
        public void ArmIntentAndSwitchTo_ArmsOnlyInsideTheNotDeclinedBlock_AndAlwaysSwitches()
        {
            string body = MethodBody(MapPatchCode(), "private static void ArmIntentAndSwitchTo(");
            const string guard = "if (!DeclineArmForUnloadedTarget(target.loaded, target.persistentId, \"pre-switch-dialog\"))";
            int g = IndexOfCall(body, guard);
            Assert.True(g >= 0, "ArmIntentAndSwitchTo must guard its arm with DeclineArmForUnloadedTarget");
            string block = BlockAfter(body, g + guard.Length);
            Assert.Contains("scenario.ArmStockActionIntent(marker);", block);
            Assert.DoesNotContain("SetActiveVessel", block);
            string after = body.Substring(g + guard.Length);
            Assert.Contains("FlightGlobals.SetActiveVessel(target);", after.Substring(after.IndexOf(block, StringComparison.Ordinal) + block.Length));
            Assert.Equal(1, CountOf(body, "ArmStockActionIntent("));
        }

        [Fact]
        public void DeclineHelper_ReadsThePredicate()
        {
            string body = MethodBody(MapPatchCode(), "internal static bool DeclineArmForUnloadedTarget(");
            Assert.Contains("ShouldArmIntentForSwitchTarget(targetLoaded)", body);
            string seam = ParsekDialogNamePrefixSourceGateTests.StripComments(File.ReadAllText(
                Path.Combine(RepoRoot(), "Source", "Parsek", "TestCommands", "TestCommandSimulateSwitchClick.cs")));
            Assert.Contains("MapFocusObjectOnSelectPatch.ShouldArmIntentForSwitchTarget(targetIsLoaded)",
                MethodBody(seam, "internal static SwitchClickGateDecision DecideSwitchGate("));
        }

        [Fact]
        public void EveryIntentArmSite_IsKnown()
        {
            // A new ArmStockActionIntent call site must be reviewed against this rule: a
            // MapSwitchTo arm reads ShouldArmIntentForSwitchTarget. The TS / KSC Fly sites
            // arm their cross-scene markers on purpose and are not in scope.
            var expected = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Patches/AlarmJumpSwitchPatch.cs", 1 },          // TS / KSC Fly (non-FLIGHT Jump)
                { "Patches/GhostTrackingStationPatch.cs", 1 },     // TS Fly
                { "Patches/KscVesselMarkerFlyPatch.cs", 1 },       // KSC marker Fly
                { "Patches/MapFocusObjectOnSelectPatch.cs", 2 },   // the two MapSwitchTo sites
                { "TestCommands/ParsekTestCommandAddon.SimulateSwitchClick.cs", 1 }, // seam, gated
            };
            string src = Path.Combine(RepoRoot(), "Source", "Parsek");
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Substring(src.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("InGameTests/", StringComparison.Ordinal)) continue;
                string code = ParsekDialogNamePrefixSourceGateTests.StripComments(File.ReadAllText(file));
                int n = CountOf(code, ".ArmStockActionIntent(");
                if (n > 0) found[rel] = n;
            }
            Assert.Equal(expected.OrderBy(k => k.Key), found.OrderBy(k => k.Key));
        }

        // ----- helpers -----

        private static string MapPatchCode()
        {
            return ParsekDialogNamePrefixSourceGateTests.StripComments(File.ReadAllText(
                Path.Combine(RepoRoot(), "Source", "Parsek", "Patches", "MapFocusObjectOnSelectPatch.cs")));
        }

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", ".."));
        }

        private static int IndexOfCall(string code, string token)
        {
            return code.IndexOf(token, StringComparison.Ordinal);
        }

        private static int CountOf(string code, string token)
        {
            int n = 0, at = 0;
            while ((at = code.IndexOf(token, at, StringComparison.Ordinal)) >= 0) { n++; at += token.Length; }
            return n;
        }

        /// <summary>The body of the method whose signature starts at <paramref name="signature"/>
        /// in comment-free code, to its matching close brace.</summary>
        private static string MethodBody(string code, string signature)
        {
            int at = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "method signature not found: " + signature);
            Assert.Equal(-1, code.IndexOf(signature, at + 1, StringComparison.Ordinal));
            return BlockAfter(code, at);
        }

        /// <summary>The brace block that opens at the first <c>{</c> at or after
        /// <paramref name="from"/>, without its outer braces.</summary>
        private static string BlockAfter(string code, int from)
        {
            int open = code.IndexOf('{', from);
            Assert.True(open >= 0, "no block after offset " + from);
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open + 1, i - open - 1);
            }
            throw new InvalidOperationException("unbalanced braces after offset " + from);
        }
    }
}
