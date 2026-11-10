using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.EnsureInitialReputationSeed</c>, written
    /// against the method as it stood before the wave-2 W7b extraction and kept unchanged
    /// through it. The method is private, so the test reaches it by reflection. Scenarios: a
    /// seed already present; a career-start baseline (with an inside-stamped row flipped back);
    /// an unread baseline and no baseline beside reputation history (the refusal branch); an
    /// unenriched milestone; the two live-pool deferrals; and the live-pool seed with no,
    /// some, empty, throwing and clockless uncommitted-recording reads, in and out of OnLoad.
    /// For each it records the return value, the origin, every ledger row (seeded ids printed,
    /// fresh ones masked), the ledger state version delta and every log line.
    /// </summary>
    [Collection("Sequential")]
    public class EnsureInitialReputationSeedCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        private static readonly MethodInfo Ensure = typeof(LedgerOrchestrator).GetMethod(
            "EnsureInitialReputationSeed", BindingFlags.Static | BindingFlags.NonPublic);

        public EnsureInitialReputationSeedCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RuntimeHelpers.RunClassConstructor(typeof(ParsekSettings).TypeHandle);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            Thread.CurrentThread.CurrentCulture = savedCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            CurrencyScenarioReadiness.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            ParsekLog.ResetRateLimitsForTesting();
            LedgerOrchestrator.ExpectedCurrencySingletonsForTesting = () => CurrencySingletons.All;
            LedgerOrchestrator.NowUtProviderForTesting = () => 352.75;
        }

        private static LedgerOrchestrator.CurrencyPoolProbe Pool(bool present, float value)
        {
            return new LedgerOrchestrator.CurrencyPoolProbe
            {
                FundingPresent = true,
                FundingLoaded = true,
                Funds = 25000.0,
                ScienceSingletonPresent = true,
                ScienceSingletonLoaded = true,
                Science = 10f,
                ReputationSingletonPresent = present,
                ReputationValue = value,
            };
        }

        private static GameStateBaseline Baseline(double ut, float reputation, bool? reputationKnown)
        {
            return new GameStateBaseline
            {
                ut = ut,
                reputation = reputation,
                reputationKnown = reputationKnown,
            };
        }

        private static void AddInsideDeathRow(string actionId)
        {
            Ledger.AddAction(new GameAction
            {
                ActionId = actionId,
                UT = 40.0,
                Type = GameActionType.ReputationPenalty,
                RecordingId = "rec-death",
                RepPenaltySource = ReputationPenaltySource.KerbalDeath,
                NominalPenalty = 10f,
                InsideReputationSeed = true,
                Sequence = 1,
            });
        }

        private static void AddReputationEarningRow(string actionId)
        {
            Ledger.AddAction(new GameAction
            {
                ActionId = actionId,
                UT = 30.0,
                Type = GameActionType.ReputationEarning,
                RecordingId = "rec-earn",
                NominalRep = 4f,
                Sequence = 2,
            });
        }

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*)(.*)$");

        private void Case(StringBuilder sb, string label, bool hasBaseline, GameStateBaseline baseline)
        {
            logLines.Clear();
            ParsekLog.ResetRateLimitsForTesting();
            int versionBefore = Ledger.StateVersion;
            var args = new object[] { hasBaseline, baseline, null };
            bool done = (bool)Ensure.Invoke(null, args);
            int versionDelta = Ledger.StateVersion - versionBefore;

            sb.Append("== ").Append(label).Append('\n');
            sb.Append("return ").Append(done ? "true" : "false")
                .Append(" origin=").Append(args[2])
                .Append(" stateVersionDelta=").Append(versionDelta.ToString(CultureInfo.InvariantCulture))
                .Append('\n');

            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var parent = new ConfigNode("ROOT");
                actions[i].SerializeInto(parent);
                foreach (string raw in parent.ToString().Replace("\r", "").Split('\n'))
                {
                    string line = raw.TrimEnd();
                    if (line.Length == 0) continue;
                    var m = ActionIdLine.Match(line);
                    if (m.Success && !m.Groups[2].Value.StartsWith("seed-", StringComparison.Ordinal))
                        line = m.Groups[1].Value + "<fresh>";
                    sb.Append("row[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ")
                        .Append(line).Append('\n');
                }
            }

            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');
        }

        private void Reset()
        {
            ResetAll();
            logLines.Clear();
        }

        private static void AssertGolden(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-actual");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".txt");
            File.WriteAllText(path, actual);
            Assert.True(false, "golden mismatch for " + name + "; actual transcript written to " + path);
        }

        [Fact]
        public void EnsureInitialReputationSeed_Golden()
        {
            Assert.NotNull(Ensure);
            var sb = new StringBuilder();

            Reset();
            Ledger.SeedInitialReputation(5f);
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 9f);
            Case(sb, "R01 seed already present", true, Baseline(0.0, 12.5f, true));

            Reset();
            AddInsideDeathRow("seed-death");
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 9f);
            Case(sb, "R02 career-start baseline, inside row flipped back", true, Baseline(0.0, 12.5f, true));

            Reset();
            AddReputationEarningRow("seed-earn");
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 9f);
            Case(sb, "R03 unread baseline beside reputation history", true, Baseline(0.5, 0f, false));

            Reset();
            AddReputationEarningRow("seed-earn");
            AddInsideDeathRow("seed-death");
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 9f);
            Case(sb, "R04 no baseline beside reputation history", false, null);

            Reset();
            AddInsideDeathRow("seed-death");
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 7.25f);
            Case(sb, "R05 only an inside death row, live pool", false, null);

            Reset();
            Ledger.AddAction(new GameAction
            {
                ActionId = "seed-milestone",
                UT = 20.0,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Sequence = 1,
            });
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 9f);
            Case(sb, "R06 unenriched milestone defers", false, null);

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(false, 0f);
            Case(sb, "R07 no reputation instance defers", false, null);

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 0.005f);
            Case(sb, "R08 pool about zero defers", true, Baseline(0.25, 0f, null));

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 7.25f);
            Case(sb, "R09 live pool, no uncommitted recordings", false, null);

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 7.25f);
            LedgerOrchestrator.UncommittedRecordingIdsForTesting =
                () => new List<string> { "rec-pending-a", "rec-pending-b" };
            Case(sb, "R10 live pool, uncommitted recordings, live clock", false, null);

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 7.25f);
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string> { "rec-pending-a" };
            LedgerOrchestrator.LoadedFlightStateUTForTesting = () => 120.75;
            ParsekScenario.SetOnLoadInProgressForTesting(true);
            Case(sb, "R11 live pool, uncommitted recordings, inside OnLoad", false, null);

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 7.25f);
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string>();
            Case(sb, "R12 live pool, empty uncommitted list", false, null);

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 7.25f);
            LedgerOrchestrator.UncommittedRecordingIdsForTesting =
                () => { throw new InvalidOperationException("read failed"); };
            Case(sb, "R13 live pool, uncommitted read throws", false, null);

            Reset();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => Pool(true, 7.25f);
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string> { "rec-pending-a" };
            LedgerOrchestrator.NowUtProviderForTesting = () => double.NaN;
            Case(sb, "R14 live pool, uncommitted recordings, clock unreadable", false, null);

            AssertGolden("EnsureInitialReputationSeed", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== R01 seed already present",
            "return true origin=PreExisting stateVersionDelta=0",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\tinitialReputation = 5",
            "row[0] \t}",
            "row[0] }",
            "== R02 career-start baseline, inside row flipped back",
            "return true origin=CreatedThisCommitFromCareerBaseline stateVersionDelta=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 40",
            "row[0] \t\ttype = 10",
            "row[0] \t\tactionId = seed-death",
            "row[0] \t\trecordingId = rec-death",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tnominalPenalty = 10",
            "row[0] \t\trepPenaltySource = 2",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 0",
            "row[1] \t\ttype = 22",
            "row[1] \t\tactionId = <fresh>",
            "row[1] \t\tinitialReputation = 12.5",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=12.5, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] SeedInitialReputation: 1 reputation row(s) were stamped inside a seed that was then read from the career-start baseline; re-stamped outside so the walk applies them",
            "== R03 unread baseline beside reputation history",
            "return true origin=CreatedThisCommitFromRefusalFallback stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 30",
            "row[0] \t\ttype = 9",
            "row[0] \t\tactionId = seed-earn",
            "row[0] \t\trecordingId = rec-earn",
            "row[0] \t\tseq = 2",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tnominalRep = 4",
            "row[0] \t\trepSource = 0",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 0",
            "row[1] \t\ttype = 22",
            "row[1] \t\tactionId = <fresh>",
            "row[1] \t\tinitialReputation = 0",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][LedgerOrchestrator] SeedInitial: career-start baseline at ut=0.5 carries no reputation reading (its scenario module had not loaded at capture) - not seeding reputation from it; the live pool decides instead",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and the career-start baseline carries no reputation reading (captured before its scenario module loaded) - seeding career start 0",
            "== R04 no baseline beside reputation history",
            "return true origin=CreatedThisCommitFromRefusalFallback stateVersionDelta=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 30",
            "row[0] \t\ttype = 9",
            "row[0] \t\tactionId = seed-earn",
            "row[0] \t\trecordingId = rec-earn",
            "row[0] \t\tseq = 2",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tnominalRep = 4",
            "row[0] \t\trepSource = 0",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 40",
            "row[1] \t\ttype = 10",
            "row[1] \t\tactionId = seed-death",
            "row[1] \t\trecordingId = rec-death",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tnominalPenalty = 10",
            "row[1] \t\trepPenaltySource = 2",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 0",
            "row[2] \t\ttype = 22",
            "row[2] \t\tactionId = <fresh>",
            "row[2] \t\tinitialReputation = 0",
            "row[2] \t}",
            "row[2] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=3",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][LedgerOrchestrator] SeedInitialReputation: 1 reputation row(s) were stamped inside a seed that was then read from career start; re-stamped outside so the walk applies them",
            "== R05 only an inside death row, live pool",
            "return true origin=CreatedThisCommitFromLivePool stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 40",
            "row[0] \t\ttype = 10",
            "row[0] \t\tactionId = seed-death",
            "row[0] \t\trecordingId = rec-death",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tinsideRepSeed = True",
            "row[0] \t\tnominalPenalty = 10",
            "row[0] \t\trepPenaltySource = 2",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 0",
            "row[1] \t\ttype = 22",
            "row[1] \t\tactionId = <fresh>",
            "row[1] \t\tinitialReputation = 7.25",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=7.25, total=2",
            "== R06 unenriched milestone defers",
            "return false origin=NotYetCaptured stateVersionDelta=0",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 20",
            "row[0] \t\ttype = 4",
            "row[0] \t\tactionId = seed-milestone",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tmilestoneId = FirstLaunch",
            "row[0] \t\tmilestoneFundsAwarded = 0",
            "row[0] \t\tmilestoneRepAwarded = 0",
            "row[0] \t\tmilestoneSciAwarded = 0",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - 1 milestone row(s) still carry zero rewards, so stock has not applied the award yet and the live pool is the PRE-award figure",
            "== R07 no reputation instance defers",
            "return false origin=NotYetCaptured stateVersionDelta=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "== R08 pool about zero defers",
            "return false origin=NotYetCaptured stateVersionDelta=0",
            "log [Parsek][INFO][LedgerOrchestrator] SeedInitial: career-start baseline at ut=0.25 carries no reputation reading (unrecorded flag, 0 beside an empty researched-tech list) - not seeding reputation from it; the live pool decides instead",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - pool ~0, deferring (reputation=0.005)",
            "== R09 live pool, no uncommitted recordings",
            "return true origin=CreatedThisCommitFromLivePool stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\tinitialReputation = 7.25",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=7.25, total=1",
            "== R10 live pool, uncommitted recordings, live clock",
            "return true origin=CreatedThisCommitFromLivePool stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\tinitialReputation = 7.25",
            "row[0] \t\tseedCaptureUT = 352.75",
            "row[0] \t\tseedPendingRec = rec-pending-a",
            "row[0] \t\tseedPendingRec = rec-pending-b",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=7.25, total=1, uncommittedRecordings=2 captureUT=352.75",
            "== R11 live pool, uncommitted recordings, inside OnLoad",
            "return true origin=CreatedThisCommitFromLivePool stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\tinitialReputation = 7.25",
            "row[0] \t\tseedCaptureUT = 120.75",
            "row[0] \t\tseedPendingRec = rec-pending-a",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=7.25, total=1, uncommittedRecordings=1 captureUT=120.75",
            "== R12 live pool, empty uncommitted list",
            "return true origin=CreatedThisCommitFromLivePool stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\tinitialReputation = 7.25",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=7.25, total=1",
            "== R13 live pool, uncommitted read throws",
            "return true origin=CreatedThisCommitFromLivePool stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\tinitialReputation = 7.25",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][WARN][LedgerOrchestrator] EnsureInitialReputationSeed: could not read the uncommitted recordings or the clock at the live-pool seed (InvalidOperationException); the seed records none",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=7.25, total=1",
            "== R14 live pool, uncommitted recordings, clock unreadable",
            "return true origin=CreatedThisCommitFromLivePool stateVersionDelta=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\tinitialReputation = 7.25",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=7.25, total=1",
        }) + "\n";
    }
}
