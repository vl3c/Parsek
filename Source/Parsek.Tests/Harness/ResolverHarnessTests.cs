using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Harness
{
    /// <summary>
    /// Resolver-level regression harness tests (Step 1 of the recording &amp;
    /// ghost policies refactor plan).
    ///
    /// Each scenario hashes the deterministic outputs of
    /// <see cref="RelativeAnchorResolver.TryResolveRecordingPose"/> over a UT
    /// span (default N=32 samples) and compares against a locked SHA-256
    /// baseline. The baselines were captured against <c>main</c>'s current
    /// behavior — they encode "the resolver returns this pose at this UT
    /// today." Any subsequent change that perturbs a sample (different
    /// position, different rotation, resolved-vs-unresolved flip) flips the
    /// hash and fails the test.
    ///
    /// Per the plan's Step 1 §"Hand-off criterion": scenarios 1, 2, 6, 9
    /// should remain stable across PR 3a/3b/3c. If one of these flips, stop
    /// and investigate before merging — it indicates a regression in the
    /// non-debris resolver chain. Scenarios 4, 5, 7, 10 (PR 2) deliberately
    /// capture today's broken debris behavior; PR 3b will reset those
    /// baselines with justification.
    ///
    /// Diagnostic recipe when a baseline fails:
    /// 1. Print <see cref="ResolverPoseHasher.DumpResolverPoses"/> for the
    ///    failing scenario (uncomment the dump line below).
    /// 2. Diff the per-sample dump against the prior expectation.
    /// 3. Decide: is the change intentional (then update the baseline with
    ///    a code-review note) or a regression (revert)?
    /// </summary>
    [Collection("Sequential")]
    public class ResolverHarnessTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ResolverHarnessTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Locked SHA-256 baselines (per scenario). These are captured
        /// against <c>main</c>'s current behavior and must remain stable
        /// across PR 3 unless a deliberate behavior change resets them with
        /// a justification comment in the diff.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Baselines =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Captured against main on 2026-05-07 (PR 1 — harness skeleton).
                // Each scenario's per-sample dump is stable to the bit, so any
                // future change that perturbs (pos, rot) at any sampled UT
                // flips the hash. Updating these baselines requires a code
                // review note explaining what changed and why it's expected.
                ["scenario-1-absolute-single"] =
                    "4b600ea20d8c4c2fff7cda411cfafeafb2916f3b9aeb89886839631be5b5c863",
                ["scenario-2-relative-fixed-anchor"] =
                    "fbbe0becdd9e33042df2151531464cdb12b8637360d65cc6909420b4a8fc855e",
                ["scenario-6-refly-walk-back"] =
                    "da72f033954edbf4781eda0d36294e5ce6bc757fda0f4b9d79b909967a73bc9b",
                ["scenario-9-loop-anchor-rejection"] =
                    "49613c44e81b0ce987485db7b2466c4b593d13ab28133c8d450b614067632bba",
            };

        [Fact]
        public void Scenario1_AbsoluteSingle_HashMatchesBaseline()
        {
            HarnessScenario s = HarnessScenarios.BuildScenario1_AbsoluteSingle();
            AssertScenarioHash(s);
        }

        [Fact]
        public void Scenario2_RelativeFixedAnchor_HashMatchesBaseline()
        {
            HarnessScenario s = HarnessScenarios.BuildScenario2_RelativeSingleFixedAnchor();
            AssertScenarioHash(s);
        }

        [Fact]
        public void Scenario6_ReFlyWalkBack_HashMatchesBaseline()
        {
            HarnessScenario s = HarnessScenarios.BuildScenario6_ReFlyProvisionalSupersedesOrigin();
            AssertScenarioHash(s);
        }

        [Fact]
        public void Scenario9_LoopAnchorRejection_HashMatchesBaseline()
        {
            HarnessScenario s = HarnessScenarios.BuildScenario9_LoopAnchorRejection();
            AssertScenarioHash(s);

            // Sanity: the resolver MUST emit a loop-rejection warning at
            // every sampled UT. If this stops happening, scenario 9's
            // tripwire would silently drift to "child rendered fine."
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]")
                && l.Contains("reason=loop-anchor-out-of-scope"));
        }

        private void AssertScenarioHash(HarnessScenario s)
        {
            string actual = ResolverPoseHasher.HashResolverPoses(
                s.Context, s.Target, s.StartUT, s.EndUT);
            string expected;
            if (!Baselines.TryGetValue(s.Name, out expected))
            {
                throw new InvalidOperationException(
                    "No locked baseline for scenario " + s.Name +
                    ". Capture by running the harness once and pasting the " +
                    "computed hash into the Baselines dictionary.");
            }

            if (!StringComparer.Ordinal.Equals(actual, expected))
            {
                // The dump is verbose but the only useful diagnostic when a
                // hash flips. Print it so CI logs show which sample drifted.
                string dump = ResolverPoseHasher.DumpResolverPoses(
                    s.Context, s.Target, s.StartUT, s.EndUT);
                throw new Xunit.Sdk.XunitException(
                    "Resolver hash for scenario '" + s.Name +
                    "' does not match locked baseline. " +
                    "Expected " + expected + ", got " + actual + ".\n" +
                    "Sample dump:\n" + dump);
            }
        }
    }
}
