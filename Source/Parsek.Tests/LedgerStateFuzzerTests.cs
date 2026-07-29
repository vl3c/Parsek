using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Seeded property/fuzz suite over the FULL nine-module recalculation stack
    /// (audit Tier D1, design-testing-unified section 8 Phase 2 item 5).
    ///
    /// <para>
    /// <see cref="RecalculationFuzzerTests"/> registers only <see cref="ContractsModule"/>
    /// and asserts sort-call counts - a structural property of the engine, not of the
    /// resulting career state. This suite registers all nine production modules in the
    /// production tier order (pinned against <c>LedgerOrchestrator.Initialize</c> by
    /// <see cref="ModuleRegistration_MirrorsProductionTierOrder"/>) and asserts the
    /// state the walk actually produces:
    /// </para>
    /// <list type="number">
    ///   <item>every pool and module total is finite (no NaN / Infinity leaks);</item>
    ///   <item>permuting the input order of equal-UT actions leaves the state identical;</item>
    ///   <item>running <c>Recalculate</c> twice on the same ledger is a no-op;</item>
    ///   <item>seed rows survive every UT cutoff, including cutoffs before the seeds;</item>
    ///   <item>ELS is an order-preserving subset of <c>Ledger.Actions</c>, antitone in the
    ///         tombstone set and monotone under ledger append.</item>
    /// </list>
    ///
    /// <para>
    /// All randomness is drawn from fixed seeds (<see cref="BaseSeed"/> plus the
    /// iteration index) so a failure reproduces exactly. Iteration counts are sized for
    /// the suite budget; set <c>PARSEK_FUZZ_ITERATIONS</c> to run a deeper sweep locally.
    /// </para>
    ///
    /// <para>
    /// TIE-BREAK NOTE. The engine's sort key is (UT, earning-before-spending, Sequence),
    /// so equal-UT permutation invariance holds only while <c>Sequence</c> separates rows
    /// that share a UT and a class - the generator therefore assigns a unique monotone
    /// <c>Sequence</c> to every row, exactly as <c>GameStateEventConverter</c> does in
    /// production. The one place the product creates rows WITHOUT a distinct sequence is
    /// <c>ContractsModule.PrePass</c>, whose synthetic <c>ContractFail</c> rows all carry
    /// the default <c>Sequence = 0</c>; those are still deterministic because the pre-pass
    /// walks the already-SORTED list, so its injection order is a function of the sort, not
    /// of the caller's input order. <see cref="SyntheticContractFail_CollidingDeadlines_ResolveDeterministically"/>
    /// pins that property directly, because reputation (unlike funds) is applied through a
    /// state-dependent curve and would otherwise be order-sensitive.
    /// </para>
    ///
    /// <para>
    /// KNOWN BLIND SPOT. Deleting <c>RecalculationEngine.ResetDerivedFields</c> does NOT red
    /// any property here, verified by mutation. Every consumer of the per-action derived
    /// fields writes them absolutely, <c>TransformContractReward</c> is currently a
    /// documented identity no-op, and the one sticky value (<c>Effective = false</c>) is only
    /// ever assigned to the LATER of two duplicate rows - which is also the row a UT cutoff
    /// drops first, so a cutoff walk can never need to resurrect it. The reset is real
    /// defence for future modules; it is simply not observable from module state today. Do
    /// not read a green run here as coverage of it.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class LedgerStateFuzzerTests : IDisposable
    {
        private const int BaseSeed = 0x1EDBEEF;
        // 500 iterations x 4 cutoffs x ~45 actions runs the five property tests in
        // roughly 3 s. PARSEK_FUZZ_ITERATIONS raises it for a deeper local sweep
        // (1,500 costs about 8 s).
        private const int DefaultIterations = 500;
        private const string IterationEnvVar = "PARSEK_FUZZ_ITERATIONS";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly bool priorLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly KerbalsModule priorOrchestratorKerbals;

        private readonly MilestonesModule milestones = new MilestonesModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly KerbalsModule kerbals = new KerbalsModule();
        private readonly StrategiesModule strategies = new StrategiesModule();
        private readonly FundsModule funds = new FundsModule();
        private readonly RouteModule routes = new RouteModule();
        private readonly ReputationModule reputation = new ReputationModule();
        private readonly FacilitiesModule facilities = new FacilitiesModule();

        public LedgerStateFuzzerTests()
        {
            priorLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();

            // Sibling suites leak LedgerOrchestrator.Kerbals through
            // SetKerbalsForTesting; pin it here and restore in Dispose so this
            // suite neither inherits nor exports a stale module.
            priorOrchestratorKerbals = LedgerOrchestrator.Kerbals;
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            RecalculationEngine.ClearModules();
            RegisterProductionModules();
            SeedCommittedRecordings();
        }

        /// <summary>
        /// <c>KerbalsModule.PrePass</c> builds its metadata cache from
        /// <c>RecordingStore.CommittedRecordings</c> and <c>ProcessAction</c> drops any
        /// <c>KerbalAssignment</c> whose <c>RecordingId</c> has no entry there. Without
        /// these rows the kerbals leg of every invariant below would be vacuously true.
        /// </summary>
        private void SeedCommittedRecordings()
        {
            for (int i = 0; i < RecordingIds.Length; i++)
            {
                string id = RecordingIds[i];
                if (string.IsNullOrEmpty(id)) continue;

                RecordingStore.AddRecordingWithTreeForTesting(new Recording
                {
                    RecordingId = id,
                    VesselName = "Fuzz " + id,
                    ExplicitStartUT = 0.0,
                    ExplicitEndUT = 1000.0 + 500.0 * i,
                    TerminalStateValue = TerminalState.Recovered
                });
            }
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            RecalculationEngine.ResetSortActionsCallCountForTesting();
            LedgerOrchestrator.SetKerbalsForTesting(priorOrchestratorKerbals);

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
        }

        /// <summary>
        /// Mirrors <c>LedgerOrchestrator.Initialize</c> exactly. Kept in one place so the
        /// source-text pin below has a single list to compare against.
        /// </summary>
        private void RegisterProductionModules()
        {
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(kerbals, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(routes, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(facilities, RecalculationEngine.ModuleTier.Facilities);
        }

        /// <summary>
        /// The registration list this suite fuzzes, in order. Names/tiers are compared
        /// against the production source text so a tier move in
        /// <c>LedgerOrchestrator.Initialize</c> cannot silently leave the fuzzer walking
        /// a different pipeline than the game does.
        /// </summary>
        private static readonly string[] ExpectedRegistrationOrder =
        {
            "milestonesModule|FirstTier",
            "contractsModule|FirstTier",
            "scienceModule|FirstTier",
            "kerbalsModule|FirstTier",
            "strategiesModule|Strategy",
            "fundsModule|SecondTier",
            "routeModule|SecondTier",
            "reputationModule|SecondTier",
            "facilitiesModule|Facilities"
        };

        // =====================================================================
        // Registration pin
        // =====================================================================

        [Fact]
        public void ModuleRegistration_MirrorsProductionTierOrder()
        {
            string source = ReadParsekSource("GameActions/LedgerOrchestrator.cs");

            var actual = new List<string>();
            foreach (var rawLine in source.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("RecalculationEngine.RegisterModule(", StringComparison.Ordinal))
                    continue;

                int open = line.IndexOf('(');
                int close = line.LastIndexOf(')');
                Assert.True(close > open, "Unparseable RegisterModule call: " + line);

                string[] args = line.Substring(open + 1, close - open - 1).Split(',');
                Assert.Equal(2, args.Length);

                string module = args[0].Trim();
                string tier = args[1].Trim();
                const string tierPrefix = "RecalculationEngine.ModuleTier.";
                Assert.StartsWith(tierPrefix, tier);
                actual.Add(module + "|" + tier.Substring(tierPrefix.Length));
            }

            Assert.Equal(ExpectedRegistrationOrder.Length, actual.Count);
            Assert.Equal(ExpectedRegistrationOrder, actual.ToArray());
        }

        // =====================================================================
        // Invariant 1 - finiteness
        // =====================================================================

        [Fact]
        public void Fuzz_AllPoolsAndModuleTotalsStayFinite()
        {
            int iterations = ResolveIterations();
            int cutoffWalks = 0;

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var ledger = BuildLedger(iteration);
                foreach (double? cutoff in CutoffsFor(ledger))
                {
                    RecalculationEngine.Recalculate(ledger.Actions, cutoff);
                    AssertFinite(iteration, cutoff);
                    cutoffWalks++;
                }
            }

            // Vacuity guard: the loop must actually have walked something.
            Assert.True(cutoffWalks >= iterations * 4,
                "expected at least four walks per iteration, saw " + cutoffWalks);
        }

        private void AssertFinite(int iteration, double? cutoff)
        {
            string where = " (iteration=" + iteration.ToString(IC)
                + ", cutoff=" + (cutoff.HasValue ? cutoff.Value.ToString("R", IC) : "null") + ")";

            AssertFiniteDouble(funds.GetRunningBalance(), "funds.runningBalance" + where);
            AssertFiniteDouble(funds.GetAvailableFunds(), "funds.availableFunds" + where);
            AssertFiniteDouble(funds.GetInitialFunds(), "funds.initialFunds" + where);
            AssertFiniteDouble(funds.GetTotalEarnings(), "funds.totalEarnings" + where);
            AssertFiniteDouble(funds.GetTotalCommittedSpendings(), "funds.totalSpendings" + where);

            AssertFiniteDouble(science.GetRunningScience(), "science.runningScience" + where);
            AssertFiniteDouble(science.GetAvailableScience(), "science.availableScience" + where);
            AssertFiniteDouble(science.GetTotalEffectiveEarnings(), "science.totalEarnings" + where);
            AssertFiniteDouble(science.GetTotalCommittedSpendings(), "science.totalSpendings" + where);
            foreach (var kvp in science.GetAllSubjects())
            {
                AssertFiniteDouble(kvp.Value.CreditedTotal, "science.subject[" + kvp.Key + "].credited" + where);
                AssertFiniteDouble(kvp.Value.MaxValue, "science.subject[" + kvp.Key + "].max" + where);
            }

            AssertFiniteFloat(reputation.GetRunningRep(), "reputation.runningRep" + where);

            // Kerbal reservations legitimately carry PositiveInfinity as the
            // "never freed" sentinel; NaN is always a defect.
            foreach (var kvp in kerbals.Reservations)
            {
                Assert.False(double.IsNaN(kvp.Value.ReservedUntilUT),
                    "kerbals.reservation[" + kvp.Key + "].reservedUntilUT is NaN" + where);
            }
        }

        private static void AssertFiniteDouble(double value, string label)
        {
            Assert.False(double.IsNaN(value), label + " is NaN");
            Assert.False(double.IsInfinity(value), label + " is Infinity");
        }

        private static void AssertFiniteFloat(float value, string label)
        {
            Assert.False(float.IsNaN(value), label + " is NaN");
            Assert.False(float.IsInfinity(value), label + " is Infinity");
        }

        // =====================================================================
        // Invariant 2 - permutation invariance over equal-UT collision groups
        // =====================================================================

        [Fact]
        public void Fuzz_EqualUtPermutation_ProducesIdenticalState()
        {
            int iterations = ResolveIterations();
            int collisionGroups = 0;

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var ledger = BuildLedger(iteration);
                collisionGroups += ledger.EqualUtCollisionGroups;

                foreach (double? cutoff in CutoffsFor(ledger))
                {
                    RecalculationEngine.Recalculate(ledger.Actions, cutoff);
                    string baseline = Snapshot();

                    var permuted = PermuteWithinEqualUtGroups(ledger.Actions, new Random(BaseSeed + iteration));
                    Assert.Equal(ledger.Actions.Count, permuted.Count);

                    RecalculationEngine.Recalculate(permuted, cutoff);
                    string shuffled = Snapshot();

                    Assert.True(baseline == shuffled,
                        "equal-UT permutation changed career state (iteration=" + iteration.ToString(IC)
                        + ", cutoff=" + (cutoff.HasValue ? cutoff.Value.ToString("R", IC) : "null") + ")\n"
                        + "baseline: " + baseline + "\nshuffled: " + shuffled);
                }
            }

            // Vacuity guard: permutation invariance is meaningless if the generator
            // never produced two actions sharing a UT.
            Assert.True(collisionGroups >= iterations,
                "expected at least one equal-UT collision group per iteration, saw " + collisionGroups);
        }

        // =====================================================================
        // Invariant 3 - double-recalculate idempotence
        // =====================================================================

        [Fact]
        public void Fuzz_DoubleRecalculate_IsIdempotent()
        {
            int iterations = ResolveIterations();

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var ledger = BuildLedger(iteration);
                foreach (double? cutoff in CutoffsFor(ledger))
                {
                    RecalculationEngine.Recalculate(ledger.Actions, cutoff);
                    string first = Snapshot();

                    RecalculationEngine.Recalculate(ledger.Actions, cutoff);
                    string second = Snapshot();

                    Assert.True(first == second,
                        "second Recalculate changed career state (iteration=" + iteration.ToString(IC)
                        + ", cutoff=" + (cutoff.HasValue ? cutoff.Value.ToString("R", IC) : "null") + ")\n"
                        + "first:  " + first + "\nsecond: " + second);
                }

                // The caller's list must never be mutated by a walk (the engine injects
                // synthetic ContractFail rows into its own sorted copy).
                Assert.Equal(ledger.ActionCountAtBuild, ledger.Actions.Count);
            }
        }

        // =====================================================================
        // Invariant 4 - seed rows survive every cutoff
        // =====================================================================

        [Fact]
        public void Fuzz_SeedRowsSurviveEveryCutoff()
        {
            int iterations = ResolveIterations();

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var ledger = BuildLedger(iteration);
                foreach (double? cutoff in CutoffsFor(ledger))
                {
                    RecalculationEngine.Recalculate(ledger.Actions, cutoff);

                    string where = " (iteration=" + iteration.ToString(IC)
                        + ", cutoff=" + (cutoff.HasValue ? cutoff.Value.ToString("R", IC) : "null") + ")";

                    Assert.True(funds.HasSeed, "FundsInitial did not survive the cutoff" + where);
                    Assert.True(science.HasSeed, "ScienceInitial did not survive the cutoff" + where);
                    Assert.True(reputation.HasSeed, "ReputationInitial did not survive the cutoff" + where);

                    Assert.Equal(ledger.SeedFunds, funds.GetInitialFunds(), 3);
                }

                // A cutoff strictly before every action leaves the seeds and nothing
                // else: the balances must collapse onto the seeded baseline exactly.
                RecalculationEngine.Recalculate(ledger.Actions, PreEverythingCutoff);
                Assert.Equal(ledger.SeedFunds, funds.GetRunningBalance(), 3);
                Assert.Equal(ledger.SeedScience, science.GetRunningScience(), 3);
                Assert.Equal((double)ledger.SeedReputation, (double)reputation.GetRunningRep(), 3);
                Assert.Equal(0.0, funds.GetTotalEarnings(), 6);
                Assert.Equal(0.0, funds.GetTotalCommittedSpendings(), 6);
            }
        }

        // =====================================================================
        // Invariant 5 - ELS is an order-preserving subset of the Ledger
        // =====================================================================

        /// <summary>
        /// <c>EffectiveState.ComputeELS</c> takes no UT cutoff - its only filter is the
        /// tombstone set (design section 3.2). The cutoff-monotonicity idea therefore maps onto
        /// the two dimensions ELS actually has: it is ANTITONE in the tombstone set
        /// (adding tombstones only ever removes rows) and MONOTONE under ledger append
        /// (appending actions never removes an already-effective row).
        /// </summary>
        [Fact]
        public void Fuzz_Els_IsOrderPreservingSubsetAntitoneInTombstonesAndMonotoneUnderAppend()
        {
            int iterations = ResolveIterations();
            int tombstonedRowsSeen = 0;

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                Ledger.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
                ParsekScenario.ResetInstanceForTesting();

                var rng = new Random(BaseSeed + 7919 * iteration);
                var ledger = BuildLedger(iteration);
                Ledger.AddActions(ledger.Actions);

                // ---- no tombstones: ELS == Ledger, order preserved ----
                SetScenarioTombstones(new List<LedgerTombstone>());
                var elsAll = EffectiveState.ComputeELS();
                AssertOrderPreservingSubset(elsAll, Ledger.Actions, iteration, "no-tombstones");
                Assert.Equal(Ledger.Actions.Count, elsAll.Count);

                // ---- tombstone set T1 ----
                var t1 = PickTombstones(Ledger.Actions, rng, 0.15);
                SetScenarioTombstones(t1);
                var els1 = EffectiveState.ComputeELS();
                AssertOrderPreservingSubset(els1, Ledger.Actions, iteration, "T1");

                var t1Ids = new HashSet<string>(t1.Select(t => t.ActionId), StringComparer.Ordinal);
                Assert.Equal(Ledger.Actions.Count - t1Ids.Count, els1.Count);
                Assert.DoesNotContain(els1, a => t1Ids.Contains(a.ActionId));
                tombstonedRowsSeen += t1Ids.Count;

                // ---- antitone: T2 superset of T1 => ELS(T2) subset of ELS(T1) ----
                var t2 = new List<LedgerTombstone>(t1);
                t2.AddRange(PickTombstones(Ledger.Actions, rng, 0.15)
                    .Where(t => !t1Ids.Contains(t.ActionId)));
                SetScenarioTombstones(t2);
                var els2 = EffectiveState.ComputeELS();
                AssertOrderPreservingSubset(els2, Ledger.Actions, iteration, "T2");

                var els1Set = new HashSet<GameAction>(els1);
                foreach (var row in els2)
                {
                    Assert.True(els1Set.Contains(row),
                        "adding tombstones ADDED an effective row (" + row.ActionId
                        + ") at iteration " + iteration.ToString(IC));
                }

                // ---- monotone under append: extend the ledger, keep T2 ----
                var appended = BuildLedger(iteration + 100_000);
                Ledger.AddActions(appended.Actions);
                SetScenarioTombstones(t2);
                var els3 = EffectiveState.ComputeELS();
                AssertOrderPreservingSubset(els3, Ledger.Actions, iteration, "T2+append");

                var els3Set = new HashSet<GameAction>(els3);
                foreach (var row in els2)
                {
                    Assert.True(els3Set.Contains(row),
                        "appending ledger rows REMOVED an effective row (" + row.ActionId
                        + ") at iteration " + iteration.ToString(IC));
                }
            }

            Assert.True(tombstonedRowsSeen > 0,
                "no tombstoned rows were generated; the ELS filter was never exercised");
        }

        private static void AssertOrderPreservingSubset(
            IReadOnlyList<GameAction> els, IReadOnlyList<GameAction> source, int iteration, string label)
        {
            string where = " (iteration=" + iteration.ToString(IC) + ", " + label + ")";

            // Reference containment plus order preservation in a single walk: every
            // ELS row must be findable in the source AFTER the previously matched row.
            int cursor = 0;
            for (int i = 0; i < els.Count; i++)
            {
                var row = els[i];
                Assert.NotNull(row);

                bool found = false;
                while (cursor < source.Count)
                {
                    if (ReferenceEquals(source[cursor], row))
                    {
                        cursor++;
                        found = true;
                        break;
                    }
                    cursor++;
                }

                Assert.True(found,
                    "ELS row " + i.ToString(IC) + " (" + row.ActionId
                    + ") is not a same-order member of Ledger.Actions" + where);
            }
        }

        private static List<LedgerTombstone> PickTombstones(
            IReadOnlyList<GameAction> source, Random rng, double rate)
        {
            var picked = new List<LedgerTombstone>();
            for (int i = 0; i < source.Count; i++)
            {
                if (rng.NextDouble() >= rate) continue;
                picked.Add(new LedgerTombstone
                {
                    TombstoneId = "tomb_" + i.ToString(IC),
                    ActionId = source[i].ActionId,
                    UT = source[i].UT
                });
            }
            return picked;
        }

        private static void SetScenarioTombstones(List<LedgerTombstone> tombstones)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = tombstones,
                RewindPoints = new List<RewindPoint>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
        }

        // =====================================================================
        // Pinned order ambiguity - synthetic ContractFail injection
        // =====================================================================

        /// <summary>
        /// The narrowest tie-break the engine has to survive: two contracts whose deadlines
        /// land on the SAME UT produce two synthetic <c>ContractFail</c> rows that both carry
        /// the default <c>Sequence = 0</c>, so the (UT, class, Sequence) sort cannot separate
        /// them and their relative order is whatever <c>ContractsModule.PrePass</c> injected.
        ///
        /// <para>
        /// That is still deterministic - the pre-pass walks the ALREADY-SORTED list, so its
        /// <c>tracked</c> insertion order is a function of the sort, not of the caller's
        /// input order. This fact pins that property where it actually bites: funds penalties
        /// are summed (commutative, would pass either way), but reputation goes through
        /// <c>ApplyReputationCurve</c>, which is state-dependent and therefore NOT
        /// commutative. If the pre-pass ever starts scanning the caller's raw list, or
        /// synthetic rows start inheriting a nondeterministic sequence, the rep assertion
        /// below reds.
        /// </para>
        /// </summary>
        [Fact]
        public void SyntheticContractFail_CollidingDeadlines_ResolveDeterministically()
        {
            const float sharedDeadline = 500f;

            List<GameAction> Build(bool aFirst)
            {
                var seedRows = new List<GameAction>
                {
                    Seed("collide", GameActionType.FundsInitial, 10000f, 0f, 0f, 0),
                    Seed("collide", GameActionType.ScienceInitial, 0f, 0f, 0f, 1),
                    Seed("collide", GameActionType.ReputationInitial, 0f, 0f, 500f, 2)
                };

                var a = new GameAction
                {
                    ActionId = "act_collide_a",
                    UT = 10.0,
                    Sequence = 10,
                    Type = GameActionType.ContractAccept,
                    ContractId = "collide-a",
                    DeadlineUT = sharedDeadline,
                    FundsPenalty = 300f,
                    RepPenalty = 40f
                };
                var b = new GameAction
                {
                    ActionId = "act_collide_b",
                    UT = 10.0,
                    Sequence = 11,
                    Type = GameActionType.ContractAccept,
                    ContractId = "collide-b",
                    DeadlineUT = sharedDeadline,
                    FundsPenalty = 700f,
                    RepPenalty = 200f
                };
                var tail = new GameAction
                {
                    ActionId = "act_collide_tail",
                    UT = 900.0,
                    Sequence = 12,
                    Type = GameActionType.FundsEarning,
                    FundsAwarded = 1f
                };

                seedRows.Add(aFirst ? a : b);
                seedRows.Add(aFirst ? b : a);
                seedRows.Add(tail);
                return seedRows;
            }

            RecalculationEngine.Recalculate(Build(true));
            double fundsAFirst = funds.GetRunningBalance();
            float repAFirst = reputation.GetRunningRep();
            var terminalAFirst = contracts.GetTerminalContractIds().OrderBy(x => x, StringComparer.Ordinal).ToList();

            RecalculationEngine.Recalculate(Build(false));
            double fundsBFirst = funds.GetRunningBalance();
            float repBFirst = reputation.GetRunningRep();
            var terminalBFirst = contracts.GetTerminalContractIds().OrderBy(x => x, StringComparer.Ordinal).ToList();

            // Both deadline-expired contracts fail either way, and the funds outcome is
            // commutative (penalties are summed).
            Assert.Equal(new[] { "collide-a", "collide-b" }, terminalAFirst.ToArray());
            Assert.Equal(terminalAFirst, terminalBFirst);
            Assert.Equal(fundsAFirst, fundsBFirst, 6);

            // The load-bearing assertion: reputation is applied through a state-dependent
            // curve, so an order-sensitive tie-break would show up here and nowhere else.
            Assert.True(repAFirst < 500f,
                "expected a net reputation loss, got " + repAFirst.ToString("R", IC));
            Assert.Equal((double)repAFirst, (double)repBFirst, 6);
        }

        // =====================================================================
        // Canonical state snapshot across all nine modules
        // =====================================================================

        private string Snapshot()
        {
            var sb = new StringBuilder();

            sb.Append("funds{seed=").Append(funds.HasSeed ? 1 : 0)
              .Append(",initial=").Append(R(funds.GetInitialFunds()))
              .Append(",balance=").Append(R(funds.GetRunningBalance()))
              .Append(",available=").Append(R(funds.GetAvailableFunds()))
              .Append(",earnings=").Append(R(funds.GetTotalEarnings()))
              .Append(",spendings=").Append(R(funds.GetTotalCommittedSpendings()))
              .Append('}');

            sb.Append("|science{seed=").Append(science.HasSeed ? 1 : 0)
              .Append(",running=").Append(R(science.GetRunningScience()))
              .Append(",available=").Append(R(science.GetAvailableScience()))
              .Append(",earnings=").Append(R(science.GetTotalEffectiveEarnings()))
              .Append(",spendings=").Append(R(science.GetTotalCommittedSpendings()))
              .Append(",subjects=[");
            foreach (var key in science.GetAllSubjects().Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var state = science.GetAllSubjects()[key];
                sb.Append(key).Append(':').Append(R(state.CreditedTotal))
                  .Append('/').Append(R(state.MaxValue)).Append(';');
            }
            sb.Append("]}");

            sb.Append("|rep{seed=").Append(reputation.HasSeed ? 1 : 0)
              .Append(",running=").Append(R(reputation.GetRunningRep())).Append('}');

            sb.Append("|milestones{count=").Append(milestones.GetCreditedCount()).Append(",ids=[");
            foreach (var id in milestones.GetCreditedMilestoneIds().OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(id).Append(':').Append(milestones.GetEffectiveMilestoneCount(id)).Append(';');
            sb.Append("]}");

            sb.Append("|contracts{active=").Append(contracts.GetActiveContractCount())
              .Append(",slots=").Append(contracts.GetAvailableSlots()).Append(",activeIds=[");
            foreach (var id in contracts.GetActiveContractIds().OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(id).Append(';');
            sb.Append("],terminal=[");
            var outcomes = contracts.GetTerminalContractOutcomes();
            var terminalActions = contracts.GetTerminalContractActionTypes();
            foreach (var id in contracts.GetTerminalContractIds().OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append(id).Append(':');
                ContractTerminalOutcome outcome;
                sb.Append(outcomes.TryGetValue(id, out outcome) ? outcome.ToString() : "?");
                sb.Append('/');
                GameActionType actionType;
                sb.Append(terminalActions.TryGetValue(id, out actionType) ? actionType.ToString() : "?");
                sb.Append(';');
            }
            sb.Append("]}");

            sb.Append("|strategies{active=").Append(strategies.GetActiveStrategyCount())
              .Append(",slots=").Append(strategies.GetAvailableSlots()).Append('}');

            sb.Append("|facilities{count=").Append(facilities.FacilityCount).Append(",states=[");
            foreach (var key in facilities.GetAllFacilities().Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var state = facilities.GetAllFacilities()[key];
                sb.Append(key).Append(':').Append(state.Level)
                  .Append('/').Append(state.Destroyed ? 1 : 0).Append(';');
            }
            sb.Append("]}");

            sb.Append("|routes{count=").Append(routes.GetWalkStateForTesting().Count).Append(",states=[");
            var routeStates = routes.GetWalkStateForTesting();
            foreach (var key in routeStates.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var state = routeStates[key];
                sb.Append(key).Append(':')
                  .Append(state.DispatchedCycles).Append('/')
                  .Append(state.DeliveredStops).Append('/')
                  .Append(state.CreditedCycles).Append('/')
                  .Append(state.PhysicalDebits).Append('/')
                  .Append(state.PhysicalPickups).Append('/')
                  .Append(state.Paused ? 1 : 0).Append('/')
                  .Append(state.EndpointLost ? 1 : 0).Append('/')
                  .Append(R(state.LastActionUT)).Append(';');
            }
            sb.Append("]}");

            sb.Append("|kerbals{reservations=[");
            foreach (var key in kerbals.Reservations.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var reservation = kerbals.Reservations[key];
                sb.Append(key).Append(':').Append(R(reservation.ReservedUntilUT))
                  .Append('/').Append(reservation.IsPermanent ? 1 : 0).Append(';');
            }
            sb.Append("],slots=[");
            foreach (var key in kerbals.Slots.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var slot = kerbals.Slots[key];
                sb.Append(key).Append(':').Append(slot.Chain.Count)
                  .Append('/').Append(slot.OwnerPermanentlyGone ? 1 : 0).Append(';');
            }
            sb.Append("],retired=[");
            foreach (var name in kerbals.RetiredKerbals.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(name).Append(';');
            sb.Append("],created=[");
            foreach (var name in kerbals.LedgerCreatedKerbals.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(name).Append(';');
            sb.Append("]}");

            return sb.ToString();
        }

        private static string R(double value)
        {
            return value.ToString("R", IC);
        }

        // =====================================================================
        // Generator
        // =====================================================================

        private const double PreEverythingCutoff = -1.0;

        private sealed class FuzzLedger
        {
            internal List<GameAction> Actions;
            internal int ActionCountAtBuild;
            internal float SeedFunds;
            internal float SeedScience;
            internal float SeedReputation;
            internal int EqualUtCollisionGroups;
            internal double MaxUT;
        }

        /// <summary>
        /// Every non-seed action type. The three seed types are emitted exactly once each
        /// by the generator so the seed-survival invariant has an unambiguous expectation
        /// (<c>FundsModule.ProcessFundsInitial</c> overwrites the seed but accumulates the
        /// balance, so a second seed row would make "the" seed value ill-defined).
        /// </summary>
        private static readonly GameActionType[] NonSeedTypes = Enum
            .GetValues(typeof(GameActionType))
            .Cast<GameActionType>()
            .Where(t => !RecalculationEngine.IsSeedType(t))
            .OrderBy(t => (int)t)
            .ToArray();

        [Fact]
        public void Generator_CoversEveryNonSeedActionType()
        {
            // The fuzz corpus must span all nine modules' action vocabulary; a new
            // GameActionType landing without a generator payload reds here.
            var seen = new HashSet<GameActionType>();
            for (int iteration = 0; iteration < 40; iteration++)
            {
                foreach (var action in BuildLedger(iteration).Actions)
                    seen.Add(action.Type);
            }

            foreach (var type in Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>())
                Assert.Contains(type, seen);
        }

        private FuzzLedger BuildLedger(int iteration)
        {
            var rng = new Random(BaseSeed + iteration);
            var actions = new List<GameAction>();
            int sequence = 0;

            float seedFunds = 5000f + rng.Next(0, 50000);
            float seedScience = rng.Next(0, 500);
            float seedReputation = rng.Next(0, 400);

            string idPrefix = iteration.ToString(IC);
            actions.Add(Seed(idPrefix, GameActionType.FundsInitial, seedFunds, 0f, 0f, sequence++));
            actions.Add(Seed(idPrefix, GameActionType.ScienceInitial, 0f, seedScience, 0f, sequence++));
            actions.Add(Seed(idPrefix, GameActionType.ReputationInitial, 0f, 0f, seedReputation, sequence++));

            // A small pool of shared UTs so equal-UT collision groups are guaranteed,
            // mixed with free-floating UTs across the whole timeline.
            double[] collisionUTs =
            {
                100.0 + rng.Next(0, 50),
                900.0 + rng.Next(0, 50),
                2600.0 + rng.Next(0, 50)
            };

            // Deadline UTs are handed out uniquely: two synthetic ContractFail rows at
            // one UT are order-ambiguous by construction (see the class remarks and
            // SyntheticContractFail_CollidingDeadlines_OrderIsInputDependent).
            double nextDeadline = 400.0;

            // Every non-seed type appears at least once per ledger, plus a random tail
            // so the corpus is not a fixed shape.
            int extra = 12 + rng.Next(0, 20);
            var plan = new List<GameActionType>(NonSeedTypes);
            for (int i = 0; i < extra; i++)
                plan.Add(NonSeedTypes[rng.Next(NonSeedTypes.Length)]);

            for (int i = 0; i < plan.Count; i++)
            {
                double ut = rng.Next(0, 100) < 35
                    ? collisionUTs[rng.Next(collisionUTs.Length)]
                    : 10.0 + rng.Next(0, 4000);

                var action = CreateAction(plan[i], ut, sequence++, iteration, rng, ref nextDeadline);
                actions.Add(action);
            }

            double maxUT = actions.Max(a => a.UT);

            var utGroups = actions.GroupBy(a => a.UT).Count(g => g.Count() > 1);

            return new FuzzLedger
            {
                Actions = actions,
                ActionCountAtBuild = actions.Count,
                SeedFunds = seedFunds,
                SeedScience = seedScience,
                SeedReputation = seedReputation,
                EqualUtCollisionGroups = utGroups,
                MaxUT = maxUT
            };
        }

        /// <summary>
        /// Cutoff sweep: no cutoff, a mid-timeline cutoff that straddles the action set,
        /// a cutoff past the end, and a cutoff before everything (seeds only).
        /// </summary>
        private static IEnumerable<double?> CutoffsFor(FuzzLedger ledger)
        {
            yield return null;
            yield return ledger.MaxUT * 0.5;
            yield return ledger.MaxUT + 1000.0;
            yield return PreEverythingCutoff;
        }

        private static GameAction Seed(
            string idPrefix, GameActionType type,
            float initialFunds, float initialScience, float initialRep, int sequence)
        {
            return new GameAction
            {
                ActionId = "act_seed_" + idPrefix + "_" + type.ToString(),
                UT = 0.0,
                Sequence = sequence,
                Type = type,
                InitialFunds = initialFunds,
                InitialScience = initialScience,
                InitialReputation = initialRep
            };
        }

        private static GameAction CreateAction(
            GameActionType type, double ut, int sequence, int iteration, Random rng, ref double nextDeadline)
        {
            string tag = iteration.ToString(IC) + "-" + sequence.ToString(IC);
            var action = new GameAction
            {
                ActionId = "act_" + tag,
                UT = ut,
                Sequence = sequence,
                Type = type,
                RecordingId = RecordingIds[rng.Next(RecordingIds.Length)]
            };

            switch (type)
            {
                case GameActionType.ScienceEarning:
                    action.SubjectId = "subject-" + rng.Next(0, 6).ToString(IC);
                    action.ScienceAwarded = Amount(rng, 1f, 40f);
                    action.SubjectMaxValue = Amount(rng, 40f, 120f);
                    break;
                case GameActionType.ScienceSpending:
                    action.NodeId = "node-" + rng.Next(0, 5).ToString(IC);
                    action.Cost = Amount(rng, 1f, 60f);
                    break;
                case GameActionType.FundsEarning:
                    action.FundsAwarded = Amount(rng, 10f, 5000f);
                    break;
                case GameActionType.FundsSpending:
                    action.FundsSpent = Amount(rng, 10f, 3000f);
                    break;
                case GameActionType.MilestoneAchievement:
                    action.MilestoneId = "milestone-" + rng.Next(0, 6).ToString(IC);
                    action.MilestoneFundsAwarded = Amount(rng, 0f, 800f);
                    action.MilestoneRepAwarded = Amount(rng, 0f, 12f);
                    action.MilestoneScienceAwarded = Amount(rng, 0f, 10f);
                    break;
                case GameActionType.ContractAccept:
                    action.ContractId = "contract-" + tag;
                    // Half the accepts carry a live deadline (drives ContractsModule's
                    // synthetic-fail pre-pass injection), half carry NaN (no deadline).
                    if (rng.Next(0, 2) == 0)
                    {
                        action.DeadlineUT = (float)nextDeadline;
                        nextDeadline += 37.0;
                    }
                    action.AdvanceFunds = Amount(rng, 0f, 500f);
                    action.FundsPenalty = Amount(rng, 0f, 900f);
                    action.RepPenalty = Amount(rng, 0f, 20f);
                    break;
                case GameActionType.ContractComplete:
                    action.ContractId = "contract-" + tag;
                    action.FundsReward = Amount(rng, 0f, 2000f);
                    action.ScienceReward = Amount(rng, 0f, 30f);
                    action.RepReward = Amount(rng, 0f, 25f);
                    break;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    action.ContractId = "contract-" + tag;
                    action.FundsPenalty = Amount(rng, 0f, 600f);
                    action.RepPenalty = Amount(rng, 0f, 15f);
                    break;
                case GameActionType.ReputationEarning:
                    action.NominalRep = Amount(rng, 0f, 30f);
                    break;
                case GameActionType.ReputationPenalty:
                    action.NominalPenalty = Amount(rng, 0f, 30f);
                    break;
                case GameActionType.KerbalAssignment:
                    action.KerbalName = KerbalNames[rng.Next(KerbalNames.Length)];
                    action.KerbalRole = KerbalRoles[rng.Next(KerbalRoles.Length)];
                    action.KerbalEndStateField = KerbalEndStates[rng.Next(KerbalEndStates.Length)];
                    break;
                case GameActionType.KerbalHire:
                    action.KerbalName = KerbalNames[rng.Next(KerbalNames.Length)];
                    action.KerbalRole = KerbalRoles[rng.Next(KerbalRoles.Length)];
                    action.HireCost = Amount(rng, 0f, 4000f);
                    break;
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                    action.KerbalName = KerbalNames[rng.Next(KerbalNames.Length)];
                    action.KerbalRole = KerbalRoles[rng.Next(KerbalRoles.Length)];
                    break;
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                    action.FacilityId = FacilityIds[rng.Next(FacilityIds.Length)];
                    action.ToLevel = rng.Next(0, 3);
                    action.FacilityCost = Amount(rng, 0f, 5000f);
                    break;
                case GameActionType.FacilityDestruction:
                    action.FacilityId = FacilityIds[rng.Next(FacilityIds.Length)];
                    break;
                case GameActionType.StrategyActivate:
                    action.StrategyId = "strategy-" + rng.Next(0, 4).ToString(IC);
                    action.SourceResource = StrategyResource.Funds;
                    action.TargetResource = StrategyResource.Science;
                    action.Commitment = Amount(rng, 0f, 1f);
                    action.SetupCost = Amount(rng, 0f, 700f);
                    action.SetupScienceCost = Amount(rng, 0f, 10f);
                    action.SetupReputationCost = Amount(rng, 0f, 8f);
                    break;
                case GameActionType.StrategyDeactivate:
                    action.StrategyId = "strategy-" + rng.Next(0, 4).ToString(IC);
                    break;
                case GameActionType.RouteDispatched:
                    ApplyRouteIdentity(action, rng);
                    action.RouteSendOnce = rng.Next(0, 4) == 0;
                    break;
                case GameActionType.RouteCargoDebited:
                    ApplyRouteIdentity(action, rng);
                    action.RouteKscFundsCost = Amount(rng, 0f, 900f);
                    action.RouteResourceManifest = new Dictionary<string, double>
                    {
                        { "Ore", rng.Next(1, 40) }
                    };
                    break;
                case GameActionType.RouteCargoDelivered:
                    ApplyRouteIdentity(action, rng);
                    action.RouteStopIndex = rng.Next(0, 3);
                    action.RouteResourceManifest = new Dictionary<string, double>
                    {
                        { "Ore", rng.Next(1, 40) }
                    };
                    break;
                case GameActionType.RouteCargoPickedUp:
                    ApplyRouteIdentity(action, rng);
                    action.RouteOriginVesselPid = (uint)rng.Next(1, 10000);
                    action.RouteResourceManifest = new Dictionary<string, double>
                    {
                        { "Ore", rng.Next(1, 20) }
                    };
                    break;
                case GameActionType.RouteRecoveryCredited:
                    ApplyRouteIdentity(action, rng);
                    action.RouteKscFundsCost = Amount(rng, 0f, 700f);
                    break;
                case GameActionType.RoutePaused:
                    ApplyRouteIdentity(action, rng);
                    action.RouteEndpointReason = "player-pause";
                    break;
                case GameActionType.RouteResumed:
                    ApplyRouteIdentity(action, rng);
                    action.RouteEndpointReason = "player-activate";
                    break;
                case GameActionType.RouteEndpointLost:
                    ApplyRouteIdentity(action, rng);
                    action.RouteEndpointReason = "endpoint-lost";
                    break;
            }

            return action;
        }

        private static void ApplyRouteIdentity(GameAction action, Random rng)
        {
            action.RouteId = "route-" + rng.Next(0, 4).ToString(IC);
            action.RouteCycleId = action.RouteId + "-cycle-" + rng.Next(0, 2000).ToString(IC);
        }

        /// <summary>
        /// Amounts stay in a plausible career band. Feeding NaN / 1e38 in would make the
        /// finiteness invariant true by construction of the INPUT rather than a statement
        /// about the walk.
        /// </summary>
        private static float Amount(Random rng, float min, float max)
        {
            return min + (float)(rng.NextDouble() * (max - min));
        }

        private static readonly string[] RecordingIds = { "rec-alpha", "rec-beta", "rec-gamma", null };
        private static readonly string[] KerbalNames =
            { "Jebediah Kerman", "Bill Kerman", "Bob Kerman", "Valentina Kerman", "Tourist Kerman" };
        private static readonly string[] KerbalRoles = { "Pilot", "Engineer", "Scientist", "Tourist" };
        private static readonly KerbalEndState[] KerbalEndStates =
        {
            KerbalEndState.Unknown, KerbalEndState.Aboard,
            KerbalEndState.Recovered, KerbalEndState.Dead
        };
        private static readonly string[] FacilityIds =
            { "LaunchPad", "Runway", "VehicleAssemblyBuilding", "TrackingStation" };

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>
        /// Fisher-Yates within each equal-UT group only; the relative order of distinct
        /// UTs is left alone so the permutation targets exactly the tie-break the engine's
        /// sort has to resolve.
        /// </summary>
        private static List<GameAction> PermuteWithinEqualUtGroups(List<GameAction> actions, Random rng)
        {
            var indicesByUT = new Dictionary<double, List<int>>();
            for (int i = 0; i < actions.Count; i++)
            {
                List<int> bucket;
                if (!indicesByUT.TryGetValue(actions[i].UT, out bucket))
                {
                    bucket = new List<int>();
                    indicesByUT[actions[i].UT] = bucket;
                }
                bucket.Add(i);
            }

            var permuted = new List<GameAction>(actions);
            foreach (var bucket in indicesByUT.Values)
            {
                if (bucket.Count < 2) continue;

                var members = bucket.Select(i => actions[i]).ToList();
                for (int i = members.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    var swap = members[i];
                    members[i] = members[j];
                    members[j] = swap;
                }

                for (int i = 0; i < bucket.Count; i++)
                    permuted[bucket[i]] = members[i];
            }

            return permuted;
        }

        private static int ResolveIterations()
        {
            string raw = Environment.GetEnvironmentVariable(IterationEnvVar);
            int parsed;
            if (!string.IsNullOrEmpty(raw)
                && int.TryParse(raw, NumberStyles.Integer, IC, out parsed)
                && parsed > 0)
            {
                return parsed;
            }
            return DefaultIterations;
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
