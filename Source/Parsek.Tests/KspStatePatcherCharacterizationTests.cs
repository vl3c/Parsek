using System;
using System.Collections.Generic;
using Contracts;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for the pure contract and unlock classifiers of
    /// <see cref="KspStatePatcher"/> that no test called directly: the terminal outcome to
    /// finished-state map, the finished-contract partition and the per-part / per-upgrade
    /// unlock classifiers. No live KSP state is touched.
    /// </summary>
    public class KspStatePatcherCharacterizationTests
    {
        // ---------- TryMapOutcomeToFinishedState --------------------------------

        [Theory]
        [InlineData((int)ContractTerminalOutcome.Completed, Contract.State.Completed)]
        [InlineData((int)ContractTerminalOutcome.Failed, Contract.State.Failed)]
        [InlineData((int)ContractTerminalOutcome.DeadlineExpired, Contract.State.DeadlineExpired)]
        [InlineData((int)ContractTerminalOutcome.Cancelled, Contract.State.Cancelled)]
        public void TryMapOutcomeToFinishedState_FinishedListOutcomes_Map(
            int outcome, Contract.State expected)
        {
            Assert.True(KspStatePatcher.TryMapOutcomeToFinishedState(
                (ContractTerminalOutcome)outcome, out Contract.State state));
            Assert.Equal(expected, state);
        }

        [Fact]
        public void TryMapOutcomeToFinishedState_RequirementsLost_NoFinishedState()
        {
            // Stock drops an OfferExpired contract instead of filing it as finished.
            Assert.False(KspStatePatcher.TryMapOutcomeToFinishedState(
                ContractTerminalOutcome.RequirementsLost, out Contract.State state));
            Assert.Equal(Contract.State.Active, state);
        }

        // ---------- PartitionFinishedContractsForPatch --------------------------

        private static KspStatePatcher.ContractFilterEntry Entry(
            Guid id, Contract.State state, bool terminal = true)
        {
            return new KspStatePatcher.ContractFilterEntry
            {
                Id = id,
                IsTerminal = terminal,
                State = state
            };
        }

        [Fact]
        public void PartitionFinishedContractsForPatch_NullEntries_EmptyOutputs()
        {
            KspStatePatcher.PartitionFinishedContractsForPatch(
                null, new HashSet<Guid>(), null, new HashSet<Guid>(),
                out List<Guid> toRemove, out HashSet<Guid> surviving);
            Assert.Empty(toRemove);
            Assert.Empty(surviving);
        }

        [Fact]
        public void PartitionFinishedContractsForPatch_SurvivingRemovableAndIgnoredEntries()
        {
            Guid survivingId = Guid.NewGuid();
            Guid mismatchedId = Guid.NewGuid();
            Guid removableId = Guid.NewGuid();
            Guid notRemovableId = Guid.NewGuid();
            Guid nonTerminalId = Guid.NewGuid();

            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                // In the terminal set with a compatible outcome: survives.
                Entry(survivingId, Contract.State.Completed),
                // In the terminal set but the stock state disagrees with the outcome:
                // not surviving, and removable because it is in the removable set.
                Entry(mismatchedId, Contract.State.Failed),
                // Not in the terminal set, in the removable set: removed.
                Entry(removableId, Contract.State.Cancelled),
                // Not in the terminal set, not removable: left alone.
                Entry(notRemovableId, Contract.State.DeadlineExpired),
                // Non-terminal entries are skipped outright.
                Entry(nonTerminalId, Contract.State.Active, terminal: false),
            };
            var terminalIds = new HashSet<Guid> { survivingId, mismatchedId, nonTerminalId };
            var outcomes = new Dictionary<Guid, ContractTerminalOutcome>
            {
                [survivingId] = ContractTerminalOutcome.Completed,
                [mismatchedId] = ContractTerminalOutcome.Completed,
            };
            var removable = new HashSet<Guid> { mismatchedId, removableId, nonTerminalId };

            KspStatePatcher.PartitionFinishedContractsForPatch(
                entries, terminalIds, outcomes, removable,
                out List<Guid> toRemove, out HashSet<Guid> surviving);

            Assert.Equal(new[] { survivingId }, surviving);
            Assert.Equal(new List<Guid> { mismatchedId, removableId }, toRemove);
        }

        [Fact]
        public void PartitionFinishedContractsForPatch_NullOutcomeMap_TerminalSetAloneSurvives()
        {
            Guid id = Guid.NewGuid();
            KspStatePatcher.PartitionFinishedContractsForPatch(
                new List<KspStatePatcher.ContractFilterEntry> { Entry(id, Contract.State.Failed) },
                new HashSet<Guid> { id }, null, new HashSet<Guid> { id },
                out List<Guid> toRemove, out HashSet<Guid> surviving);
            Assert.Contains(id, surviving);
            Assert.Empty(toRemove);
        }

        [Fact]
        public void PartitionFinishedContractsForPatch_NullTerminalAndRemovableSets_NothingRemoved()
        {
            Guid id = Guid.NewGuid();
            KspStatePatcher.PartitionFinishedContractsForPatch(
                new List<KspStatePatcher.ContractFilterEntry> { Entry(id, Contract.State.Completed) },
                null, null, null,
                out List<Guid> toRemove, out HashSet<Guid> surviving);
            Assert.Empty(surviving);
            Assert.Empty(toRemove);
        }

        [Fact]
        public void PartitionFinishedContractsForPatch_RequirementsLostOutcome_MatchesOfferExpiredOnly()
        {
            Guid expired = Guid.NewGuid();
            Guid cancelled = Guid.NewGuid();
            KspStatePatcher.PartitionFinishedContractsForPatch(
                new List<KspStatePatcher.ContractFilterEntry>
                {
                    Entry(expired, Contract.State.OfferExpired),
                    Entry(cancelled, Contract.State.Cancelled),
                },
                new HashSet<Guid> { expired, cancelled },
                new Dictionary<Guid, ContractTerminalOutcome>
                {
                    [expired] = ContractTerminalOutcome.RequirementsLost,
                    [cancelled] = ContractTerminalOutcome.RequirementsLost,
                },
                new HashSet<Guid>(),
                out _, out HashSet<Guid> surviving);
            Assert.Equal(new[] { expired }, surviving);
        }

        // ---------- ClassifyPartPurchaseForPatch --------------------------------

        [Fact]
        public void ClassifyPartPurchaseForPatch_EachOutcome()
        {
            Func<string, string> techOf = p => p == "known" ? "basicRocketry" : null;
            Func<string, bool> researched = t => t == "basicRocketry";

            Assert.Equal(KspStatePatcher.PartPurchasePatchOutcome.UnknownPart,
                KspStatePatcher.ClassifyPartPurchaseForPatch("ghost", techOf, researched, _ => false));
            Assert.Equal(KspStatePatcher.PartPurchasePatchOutcome.UnknownPart,
                KspStatePatcher.ClassifyPartPurchaseForPatch("known", null, researched, _ => false));
            Assert.Equal(KspStatePatcher.PartPurchasePatchOutcome.TechNotResearched,
                KspStatePatcher.ClassifyPartPurchaseForPatch("known", techOf, _ => false, _ => false));
            Assert.Equal(KspStatePatcher.PartPurchasePatchOutcome.TechNotResearched,
                KspStatePatcher.ClassifyPartPurchaseForPatch("known", techOf, null, _ => false));
            Assert.Equal(KspStatePatcher.PartPurchasePatchOutcome.AlreadyPurchased,
                KspStatePatcher.ClassifyPartPurchaseForPatch("known", techOf, researched, _ => true));
            Assert.Equal(KspStatePatcher.PartPurchasePatchOutcome.Added,
                KspStatePatcher.ClassifyPartPurchaseForPatch("known", techOf, researched, _ => false));
            // A null purchased-probe reads as "not purchased".
            Assert.Equal(KspStatePatcher.PartPurchasePatchOutcome.Added,
                KspStatePatcher.ClassifyPartPurchaseForPatch("known", techOf, researched, null));
        }

        [Fact]
        public void ClassifyPartPurchaseForPatch_ResearchProbeReceivesTheTechIdNotThePartName()
        {
            string probed = null;
            KspStatePatcher.ClassifyPartPurchaseForPatch(
                "part.a", p => "tech.of.a", t => { probed = t; return true; }, _ => false);
            Assert.Equal("tech.of.a", probed);
        }

        // ---------- ClassifyUpgradeUnlockForPatch -------------------------------

        [Fact]
        public void ClassifyUpgradeUnlockForPatch_EachOutcome()
        {
            Func<string, bool> known = u => u == "engineUpgrade";

            Assert.Equal(KspStatePatcher.UpgradeUnlockPatchOutcome.UnknownUpgrade,
                KspStatePatcher.ClassifyUpgradeUnlockForPatch("other", known, _ => true, _ => false));
            Assert.Equal(KspStatePatcher.UpgradeUnlockPatchOutcome.UnknownUpgrade,
                KspStatePatcher.ClassifyUpgradeUnlockForPatch("engineUpgrade", null, _ => true, _ => false));
            Assert.Equal(KspStatePatcher.UpgradeUnlockPatchOutcome.TechNotResearched,
                KspStatePatcher.ClassifyUpgradeUnlockForPatch("engineUpgrade", known, _ => false, _ => false));
            Assert.Equal(KspStatePatcher.UpgradeUnlockPatchOutcome.TechNotResearched,
                KspStatePatcher.ClassifyUpgradeUnlockForPatch("engineUpgrade", known, null, _ => false));
            Assert.Equal(KspStatePatcher.UpgradeUnlockPatchOutcome.AlreadyUnlocked,
                KspStatePatcher.ClassifyUpgradeUnlockForPatch("engineUpgrade", known, _ => true, _ => true));
            Assert.Equal(KspStatePatcher.UpgradeUnlockPatchOutcome.Added,
                KspStatePatcher.ClassifyUpgradeUnlockForPatch("engineUpgrade", known, _ => true, null));
        }
    }
}
