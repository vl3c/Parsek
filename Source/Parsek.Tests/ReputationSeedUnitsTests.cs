using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cases for the three units split out of
    /// <c>LedgerOrchestrator.EnsureInitialReputationSeed</c>:
    /// <see cref="LedgerOrchestrator.LogReputationSeedMilestoneDeferral"/>,
    /// <see cref="LedgerOrchestrator.LogReputationSeedPoolDeferral"/> and
    /// <see cref="LedgerOrchestrator.SeedInitialReputationFromLivePool"/>. They run under
    /// de-DE to prove the lines and the seed format invariantly.
    /// </summary>
    [Collection("Sequential")]
    public class ReputationSeedUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public ReputationSeedUnitsTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static LedgerOrchestrator.CurrencyPoolProbe Rep(bool present, float value) =>
            new LedgerOrchestrator.CurrencyPoolProbe
            {
                ReputationSingletonPresent = present,
                ReputationValue = value,
            };

        private GameAction OnlySeed()
        {
            Assert.Single(Ledger.Actions);
            var seed = Ledger.Actions[0];
            Assert.Equal(GameActionType.ReputationInitial, seed.Type);
            return seed;
        }

        [Fact]
        public void MilestoneDeferral_NamesTheCount()
        {
            LedgerOrchestrator.LogReputationSeedMilestoneDeferral(3);

            Assert.Contains(logLines, l => l.Contains("[VERBOSE][LedgerOrchestrator]")
                && l.Contains("EnsureInitialReputationSeed: deferring the reputation seed - 3 milestone row(s) still carry zero rewards"));
        }

        [Fact]
        public void PoolDeferral_NullInstance_NamesTheSingleton()
        {
            LedgerOrchestrator.LogReputationSeedPoolDeferral(true, Rep(false, 4.5f));

            Assert.Contains(logLines, l => l.EndsWith(
                "EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
                StringComparison.Ordinal));
        }

        [Fact]
        public void PoolDeferral_ZeroPool_PrintsTheValueInvariantly()
        {
            LedgerOrchestrator.LogReputationSeedPoolDeferral(false, Rep(true, 0.005f));

            Assert.Contains(logLines, l => l.EndsWith(
                "EnsureInitialReputationSeed: deferring the reputation seed - pool ~0, deferring (reputation=0.005)",
                StringComparison.Ordinal));
        }

        [Fact]
        public void LivePool_NoUncommittedRecordings_SeedsThePlainValue()
        {
            LedgerOrchestrator.SeedInitialReputationFromLivePool(Rep(true, 7.25f));

            var seed = OnlySeed();
            Assert.Equal(7.25f, seed.InitialReputation);
            Assert.Null(seed.SeedPendingRecordingIds);
        }

        [Fact]
        public void LivePool_UncommittedRecordings_RecordsThemAndTheClock()
        {
            LedgerOrchestrator.UncommittedRecordingIdsForTesting =
                () => new List<string> { "rec-a", "rec-b" };
            LedgerOrchestrator.NowUtProviderForTesting = () => 352.75;

            LedgerOrchestrator.SeedInitialReputationFromLivePool(Rep(true, 7.25f));

            var seed = OnlySeed();
            Assert.Equal(7.25f, seed.InitialReputation);
            Assert.Equal(new[] { "rec-a", "rec-b" }, seed.SeedPendingRecordingIds);
            Assert.Equal(352.75, seed.SeedCaptureUT);
            Assert.Contains(logLines, l => l.Contains("uncommittedRecordings=2 captureUT=352.75"));
        }

        [Fact]
        public void LivePool_ReadThrows_WarnsAndSeedsThePlainValue()
        {
            LedgerOrchestrator.UncommittedRecordingIdsForTesting =
                () => { throw new InvalidOperationException("boom"); };

            LedgerOrchestrator.SeedInitialReputationFromLivePool(Rep(true, 2f));

            var seed = OnlySeed();
            Assert.Equal(2f, seed.InitialReputation);
            Assert.Null(seed.SeedPendingRecordingIds);
            Assert.Contains(logLines, l => l.Contains("[WARN][LedgerOrchestrator]")
                && l.Contains("(InvalidOperationException); the seed records none"));
        }
    }
}
