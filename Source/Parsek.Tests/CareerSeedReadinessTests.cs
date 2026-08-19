using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the two "Parsek's OnLoad runs before KSP finished loading its currency
    /// ScenarioModules" defects found by the `L3-career-science-recover` flight 3
    /// (run 2026-08-19_1912):
    ///
    /// <list type="bullet">
    /// <item>CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE - the deferred ledger seed ran
    /// synchronously inside OnLoad (a coroutine body runs up to its first yield on the
    /// calling frame) with only <c>Funding</c> loaded, so science and reputation were
    /// never seeded.</item>
    /// <item>CAREER-TRANSMIT-SCIENCE-EMITS-NO-CORROBORATING-EVENT - the recorder's
    /// resource baselines were left NaN for the same reason, and an unseeded baseline
    /// silently eats the first real currency change of the scene.</item>
    /// </list>
    ///
    /// <para>
    /// The seeding DECISION is pure and tested directly. The WIRING runs inside
    /// coroutines on a ScenarioModule and reads Unity singletons, so it is held by
    /// source-text gates - the same pattern the other ParsekScenario hookup tests use
    /// (see <c>RouteGoBackRewindReconcileTests.HandleRewindOnLoad_*</c>).
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class CareerSeedReadinessTests
    {
        // ------------------------------------------------------------------
        // The pure seeding decision
        // ------------------------------------------------------------------

        [Fact]
        public void SeedBaselineIfUnseeded_FillsOnlyUnseededBaselines()
        {
            // Unseeded + singleton present: take the live value.
            Assert.Equal(103.0, GameStateRecorder.SeedBaselineIfUnseeded(
                currentBaseline: double.NaN, singletonPresent: true, singletonValue: 103.0));

            // Already seeded: leave it alone. Overwriting here would discard every change
            // between the old baseline and now - the exact swallow this seeding prevents.
            Assert.Equal(103.0, GameStateRecorder.SeedBaselineIfUnseeded(
                currentBaseline: 103.0, singletonPresent: true, singletonValue: 106.6));
        }

        [Fact]
        public void SeedBaselineIfUnseeded_AbsentSingletonLeavesBaselineUnseeded()
        {
            // A value read from an absent singleton would be a fabricated zero, which is a
            // large false delta on the next real change. Stay NaN and retry later.
            Assert.True(double.IsNaN(GameStateRecorder.SeedBaselineIfUnseeded(
                currentBaseline: double.NaN, singletonPresent: false, singletonValue: 0.0)));
        }

        [Fact]
        public void SeedBaselineIfUnseeded_ZeroIsARealBaselineNotAnUnseededOne()
        {
            // A career that genuinely starts at reputation 0 / science 0 must end up SEEDED
            // at 0, not stuck unseeded - otherwise its first award is swallowed too.
            double seeded = GameStateRecorder.SeedBaselineIfUnseeded(
                currentBaseline: double.NaN, singletonPresent: true, singletonValue: 0.0);
            Assert.Equal(0.0, seeded);
            Assert.False(double.IsNaN(seeded));

            // And a seeded zero is never re-seeded.
            Assert.Equal(0.0, GameStateRecorder.SeedBaselineIfUnseeded(
                currentBaseline: 0.0, singletonPresent: true, singletonValue: 55.0));
        }

        // ------------------------------------------------------------------
        // Wiring gates
        // ------------------------------------------------------------------

        [Fact]
        public void DeferredSeed_YieldsBeforeProbingAndWaitsForEveryCurrencySingleton()
        {
            string body = ReadMethodBody(
                "private IEnumerator DeferredSeedAndRecalculate()",
                "Route through the guarded helper");

            int firstYield = body.IndexOf("yield return null;", StringComparison.Ordinal);

            // The first PROBE is whichever readiness read appears earliest - the
            // all-present helper or a bare singleton read. Anchoring on only one of them
            // would let the gate pass while the other one still ran before any yield.
            int firstProbe = EarliestIndexOf(body,
                "AllCurrencySingletonsPresent()",
                "Funding.Instance",
                "ResearchAndDevelopment.Instance",
                "Reputation.Instance");

            Assert.True(firstYield >= 0,
                "DeferredSeedAndRecalculate must yield at least once");
            Assert.True(firstProbe >= 0,
                "DeferredSeedAndRecalculate must probe the currency singletons");
            Assert.True(firstYield < firstProbe,
                "DeferredSeedAndRecalculate must yield BEFORE its first readiness probe. " +
                "StartCoroutine runs the body synchronously up to the first yield, and OnLoad " +
                "is called from the middle of KSP's ScenarioRunner.LoadModules - so a probe " +
                "before the yield sees whichever currency modules happen to precede Parsek's " +
                "in the save and seeds only those.");

            Assert.Contains("AllCurrencySingletonsPresent()", body);
            Assert.DoesNotContain(
                "&& Funding.Instance == null\n                   && ResearchAndDevelopment.Instance == null",
                body.Replace("\r\n", "\n"));
        }

        [Fact]
        public void AllCurrencySingletonsPresent_RequiresAllThreeNotAnyOne()
        {
            string body = ReadMethodBody(
                "private static bool AllCurrencySingletonsPresent()",
                "</summary>");

            // Every singleton is required (&&). An || here would restore the "any one is
            // enough" gate that dropped the science seed.
            Assert.Contains("Funding.Instance != null", body);
            Assert.Contains("ResearchAndDevelopment.Instance != null", body);
            Assert.Contains("Reputation.Instance != null", body);
            Assert.DoesNotContain("||", body);
        }

        [Fact]
        public void OnLoad_StartsTheRecorderBaselineTopUpRightAfterSubscribe()
        {
            string source = ReadParsekScenarioSource();

            int subscribeIdx = source.IndexOf(
                "stateRecorder.Subscribe();", StringComparison.Ordinal);
            Assert.True(subscribeIdx >= 0, "stateRecorder.Subscribe() call site missing");

            int topUpIdx = source.IndexOf(
                "StartCoroutine(SeedRecorderResourceBaselinesWhenReady(stateRecorder));",
                StringComparison.Ordinal);
            Assert.True(topUpIdx > subscribeIdx,
                "OnLoad must start SeedRecorderResourceBaselinesWhenReady AFTER Subscribe(): " +
                "Subscribe seeds from whatever singletons exist at that instant, and any " +
                "baseline it leaves NaN eats the first currency change of the scene.");
        }

        [Fact]
        public void RecoveryProcessingCompletion_IsSubscribedAndUnsubscribed()
        {
            string source = ReadParsekScenarioSource();

            Assert.Contains(
                "GameEvents.onVesselRecoveryProcessingComplete.Add(OnVesselRecoveryProcessingComplete);",
                source);
            // Subscribed once, removed on both the re-subscribe path and OnDestroy.
            Assert.Equal(2, CountOccurrences(source,
                "GameEvents.onVesselRecoveryProcessingComplete.Remove(OnVesselRecoveryProcessingComplete);"));
        }

        // ------------------------------------------------------------------

        private static string ReadParsekScenarioSource()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");
            if (!File.Exists(scenarioPath))
            {
                scenarioPath = Path.Combine(projectRoot, "Parsek", "ParsekScenario.cs");
            }
            Assert.True(File.Exists(scenarioPath),
                $"ParsekScenario.cs not found at {scenarioPath}");
            return File.ReadAllText(scenarioPath);
        }

        private static string ReadMethodBody(string signature, string endAnchor)
        {
            string source = ReadParsekScenarioSource();
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{signature}' not found in ParsekScenario.cs");
            int end = source.IndexOf(endAnchor, start, StringComparison.Ordinal);
            Assert.True(end > start,
                $"end anchor '{endAnchor}' not found after '{signature}'");
            return source.Substring(start, end - start);
        }

        private static int EarliestIndexOf(string haystack, params string[] needles)
        {
            int best = -1;
            for (int i = 0; i < needles.Length; i++)
            {
                int idx = haystack.IndexOf(needles[i], StringComparison.Ordinal);
                if (idx < 0) continue;
                if (best < 0 || idx < best) best = idx;
            }
            return best;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
