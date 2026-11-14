using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins of the module set the recalculation walks: the order production registers the
    /// eight modules in, that production's set walks exactly like the fuzzer's mirrored
    /// registration, and a digest of every walk outcome over the fuzz corpus through all
    /// four engine entry points (the visible walk, the balance walk over clones and the two
    /// probes). The digest is the differential witness for changes to how the engine is
    /// handed its modules: it must not move unless a ledger result does.
    /// </summary>
    public partial class LedgerStateFuzzerTests
    {
        // Sha-256 over the corpus below (numbers rounded to 3 decimals so a last-bit
        // difference between runtimes cannot move it). Recompute by reading the failure
        // message; a change here is a change to a ledger result.
        private const string WalkDigestPin =
            "bab6e3a7266234d2daeefeb73416eb4685d8ce0aab662ac0e526863c3f15de22";
        private const int DigestIterations = 120;
        private const int MirrorIterations = 40;

        private static readonly string[] ProductionRegistrationLines =
        {
            "Registered first-tier module: MilestonesModule, total=1",
            "Registered first-tier module: ContractsModule, total=2",
            "Registered first-tier module: ScienceModule, total=3",
            "Registered first-tier module: KerbalsModule, total=4",
            "Registered strategy transform module: StrategiesModule",
            "Registered second-tier module: FundsModule, total=1",
            "Registered second-tier module: ReputationModule, total=2",
            "Registered facilities module: FacilitiesModule",
        };

        [Fact]
        public void ProductionInitialize_RegistersTheEightModulesInTierOrder()
        {
            var lines = new List<string>();
            try
            {
                ParsekLog.TestSinkForTesting = line => lines.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                LedgerOrchestrator.ResetForTesting();
                lines.Clear();

                LedgerOrchestrator.Initialize();

                var registrations = lines
                    .Where(l => l.Contains("[RecalcEngine]") && l.Contains("Registered "))
                    .Select(l => l.Substring(l.IndexOf("Registered ", StringComparison.Ordinal)))
                    .ToArray();
                Assert.Equal(ProductionRegistrationLines, registrations);
                Assert.Contains(lines, l => l.Contains("[RecalcEngine]") && l.Contains("ClearModules: removed=0"));
                Assert.Contains(lines, l => l.Contains("[LedgerOrchestrator]")
                    && l.Contains("Initialized: 8 modules registered"));

                // Initialize is idempotent: a second call registers nothing.
                lines.Clear();
                var initialized = LedgerOrchestrator.Modules;
                LedgerOrchestrator.Initialize();
                Assert.DoesNotContain(lines, l => l.Contains("Registered "));
                Assert.Same(initialized, LedgerOrchestrator.Modules);
                Assert.Equal(8, initialized.Count);

                // A reset drops the set for a fresh empty one and logs what it removed; the
                // set a caller still holds is not emptied under it.
                lines.Clear();
                LedgerOrchestrator.ResetForTesting();
                Assert.NotSame(initialized, LedgerOrchestrator.Modules);
                Assert.Equal(0, LedgerOrchestrator.Modules.Count);
                Assert.Equal(8, initialized.Count);
                Assert.Contains(lines, l => l.Contains("[RecalcEngine]") && l.Contains("ClearModules: removed=8"));
            }
            finally
            {
                LedgerOrchestrator.ResetForTesting();
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }

        [Fact]
        public void ProductionModuleSet_WalksLikeTheMirroredRegistration()
        {
            int compared = 0;
            try
            {
                for (int iteration = 0; iteration < MirrorIterations; iteration++)
                {
                    var ledger = BuildLedger(iteration);
                    foreach (double? cutoff in CutoffsFor(ledger))
                    {
                        modules.Clear();
                        RegisterProductionModules();
                        RecalculationEngine.Recalculate(modules, ledger.Actions, cutoff);
                        string mirrored = WalkOutcome(ledger.Actions, funds, science, reputation);

                        LedgerOrchestrator.ResetForTesting();
                        LedgerOrchestrator.Initialize();
                        RecalculationEngine.Recalculate(LedgerOrchestrator.Modules, ledger.Actions, cutoff);
                        string production = WalkOutcome(ledger.Actions,
                            LedgerOrchestrator.Funds, LedgerOrchestrator.Science, LedgerOrchestrator.Reputation);

                        Assert.True(mirrored == production,
                            "production's module set walked differently from the mirrored registration (iteration="
                            + iteration.ToString(IC) + ", cutoff="
                            + (cutoff.HasValue ? cutoff.Value.ToString("R", IC) : "null") + ")\nmirrored:   "
                            + mirrored + "\nproduction: " + production);
                        compared++;
                    }
                }
            }
            finally
            {
                LedgerOrchestrator.ResetForTesting();
                LedgerOrchestrator.SetKerbalsForTesting(kerbals);
            }
            Assert.Equal(MirrorIterations * 4, compared);
        }

        [Fact]
        public void ModuleSet_WalkDigestOverTheFuzzCorpus_IsPinned()
        {
            var sb = new StringBuilder();
            int clonedSamples = 0;
            int probedPools = 0;
            for (int iteration = 0; iteration < DigestIterations; iteration++)
            {
                var ledger = BuildLedger(iteration);
                sb.Append("#it=").Append(iteration.ToString(IC)).Append('\n');
                foreach (double? cutoff in CutoffsFor(ledger))
                {
                    RecalculationEngine.Recalculate(modules, ledger.Actions, cutoff);
                    sb.Append("cut=").Append(cutoff.HasValue ? Q(cutoff.Value) : "null").Append('\n');
                    sb.Append(Snapshot(Q)).Append('\n');
                    sb.Append(DerivedFields(ledger.Actions)).Append('\n');
                }

                var walk = RecalculationEngine.WalkBalancesOnClones(modules, ledger.Actions, ledger.MaxUT);
                sb.Append("clones science=").Append(walk.ScienceWalked ? 1 : 0).Append(" samples=");
                foreach (var s in walk.Samples)
                    sb.Append(Q(s.UT)).Append(':').Append(Q(s.Funds)).Append('/')
                      .Append(Q(s.Science)).Append('/').Append(Q(s.Reputation)).Append(';');
                clonedSamples += walk.Samples.Count;
                AppendFlags(sb, " affordable=", walk.AffordableByActionId);
                AppendFlags(sb, " effective=", walk.EffectiveByActionId);
                sb.Append(" notCounted=");
                foreach (var kv in walk.NotCountedByActionId.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sb.Append(kv.Key).Append(':').Append(kv.Value).Append(';');
                sb.Append('\n');

                double probeUT = ledger.MaxUT * 0.5;
                var fundsProbe = RecalculationEngine.ProbeModuleAtCutoff<FundsModule>(modules, ledger.Actions, probeUT);
                var scienceProbe = RecalculationEngine.ProbeModuleAtCutoff<ScienceModule>(modules, ledger.Actions, probeUT);
                sb.Append("probe funds=")
                  .Append(fundsProbe == null ? "null" : Q(fundsProbe.GetRunningBalance()) + "/" + Q(fundsProbe.GetAvailableFunds()))
                  .Append(" science=")
                  .Append(scienceProbe == null ? "null" : Q(scienceProbe.GetRunningScience()) + "/" + Q(scienceProbe.GetAvailableScience()))
                  .Append('\n');

                var click = RecalculationEngine.ProbeClickReservationAtCutoff(modules, ledger.Actions, probeUT);
                sb.Append("click modules=").Append(string.Join(",", click.Modules.Select(m => m.GetType().Name).ToArray()))
                  .Append(" pools=");
                foreach (var p in click.Pools)
                {
                    sb.Append(p.Pool).Append(':').Append(p.CostAhead ? 1 : 0).Append('/')
                      .Append(p.CostRowsAhead.ToString(IC)).Append('/').Append(Q(p.Need)).Append(';');
                    probedPools++;
                }
                sb.Append('\n');
            }

            // Vacuity guards: the clone walk sampled and the click probe saw the floor pools.
            Assert.True(clonedSamples >= DigestIterations * 30, "clone walk sampled " + clonedSamples);
            Assert.True(probedPools >= DigestIterations * 3, "click probe measured " + probedPools + " pools");

            string digest = Sha256Hex(sb.ToString());
            Assert.True(digest == WalkDigestPin,
                "walk digest over the fuzz corpus moved: got " + digest + ", pinned " + WalkDigestPin
                + " (a moved digest is a changed ledger result)");
        }

        [Fact]
        public void TwoModuleSets_WalkIndependently()
        {
            // The engine keeps no module state: walking another set (here an empty one and
            // a second production-shaped one) leaves this class's modules exactly as its
            // own walk left them.
            var ledger = BuildLedger(7);
            RecalculationEngine.Recalculate(modules, ledger.Actions, null);
            string own = Snapshot(Q);
            string ownDerived = DerivedFields(ledger.Actions);

            RecalculationEngine.Recalculate(new RecalculationModuleSet(), ledger.Actions, null);
            Assert.Equal(own, Snapshot(Q));

            var other = new RecalculationModuleSet();
            var otherFunds = new FundsModule();
            other.Register(new ScienceModule(), RecalculationModuleSet.ModuleTier.FirstTier);
            other.Register(otherFunds, RecalculationModuleSet.ModuleTier.SecondTier);
            RecalculationEngine.Recalculate(other, BuildLedger(8).Actions, null);
            Assert.Equal(own, Snapshot(Q));
            Assert.True(otherFunds.HasSeed, "the other set's funds module walked its own ledger");

            RecalculationEngine.Recalculate(modules, ledger.Actions, null);
            Assert.Equal(own, Snapshot(Q));
            Assert.Equal(ownDerived, DerivedFields(ledger.Actions));
        }

        private static string WalkOutcome(
            List<GameAction> actions, FundsModule f, ScienceModule s, ReputationModule r)
        {
            return "funds=" + Q(f.GetRunningBalance()) + "/" + Q(f.GetAvailableFunds())
                + " science=" + Q(s.GetRunningScience()) + "/" + Q(s.GetAvailableScience())
                + " rep=" + Q(r.GetRunningRep()) + "\n" + DerivedFields(actions);
        }

        private static string DerivedFields(List<GameAction> actions)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                sb.Append(a.ActionId).Append('{')
                  .Append(a.Effective ? 1 : 0).Append(',').Append(a.NotCountedReason).Append(',')
                  .Append(a.Affordable ? 1 : 0).Append(',')
                  .Append(a.UnaffordableRunningScience.HasValue ? Q(a.UnaffordableRunningScience.Value) : "-").Append(',')
                  .Append(Q(a.EffectiveScience)).Append(',').Append(Q(a.EffectiveRep)).Append(',')
                  .Append(Q(a.TransformedFundsReward)).Append(',').Append(Q(a.TransformedScienceReward)).Append(',')
                  .Append(Q(a.TransformedRepReward)).Append(',')
                  .Append(a.RecordPartlyCredited ? 1 : 0).Append(',')
                  .Append(Q(a.RecordCreditedFunds)).Append(',').Append(Q(a.RecordCreditedRep)).Append(',')
                  .Append(Q(a.RecordCreditedScience)).Append(',')
                  .Append(a.RecordThresholdsCredited.ToString(IC)).Append('/').Append(a.RecordThresholdsTotal.ToString(IC)).Append(',')
                  .Append(Q(a.TakeBackFunds)).Append(',').Append(Q(a.TakeBackRep)).Append(',')
                  .Append(Q(a.TakeBackScience)).Append(',').Append(a.TakeBackWitness ?? "-").Append(',')
                  .Append(Q(a.ConversionShare)).Append("};");
            }
            return sb.ToString();
        }

        private static void AppendFlags(StringBuilder sb, string label, Dictionary<string, bool> flags)
        {
            sb.Append(label);
            foreach (var kv in flags.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(kv.Value ? 1 : 0).Append(';');
        }

        private static string Q(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "+Inf";
            if (double.IsNegativeInfinity(value)) return "-Inf";
            double rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
            if (rounded == 0.0) rounded = 0.0;
            return rounded.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
