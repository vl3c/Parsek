using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §B/#404 of career-earnings-bundle plan plus broad Re-Fly
    /// tombstones: PatchContracts must preserve Offered/declined stock entries
    /// while removing Active and terminal contracts that are no longer in the
    /// ledger. The old code unconditionally cleared buckets, which wiped Mission
    /// Control's Offered list and contract history on every recalc.
    ///
    /// The real PatchContracts method needs a live KSP ContractSystem. This test
    /// exercises the pure <see cref="KspStatePatcher.PartitionContractsForPatch"/>
    /// helper which implements the filtering rules.
    /// </summary>
    public class PatchContractsPreservationTests
    {
        private static KspStatePatcher.ContractFilterEntry Active(Guid g) =>
            new KspStatePatcher.ContractFilterEntry { Id = g, IsActive = true };

        private static KspStatePatcher.ContractFilterEntry Offered(Guid g) =>
            new KspStatePatcher.ContractFilterEntry { Id = g, IsActive = false };

        private static KspStatePatcher.ContractFilterEntry Terminal(Guid g) =>
            new KspStatePatcher.ContractFilterEntry { Id = g, IsTerminal = true };

        [Fact]
        public void Partition_ActiveStillInLedger_Preserved()
        {
            var g1 = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry> { Active(g1) };
            var ledgerActive = new HashSet<Guid> { g1 };

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive,
                out var toRemove, out var surviving);

            Assert.Empty(toRemove);
            Assert.Contains(g1, surviving);
        }

        [Fact]
        public void Partition_ActiveNotInLedger_Removed()
        {
            var stale = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry> { Active(stale) };
            var ledgerActive = new HashSet<Guid>();

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive,
                out var toRemove, out var surviving);

            Assert.Contains(stale, toRemove);
            Assert.Empty(surviving);
        }

        [Fact]
        public void Partition_TombstonedContractAccept_RemovesStaleActiveContract()
        {
            // A Re-Fly tombstone removes the ContractAccept row from ELS, so the
            // ledger active set no longer includes the stock Active contract id.
            var tombstonedAcceptId = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Active(tombstonedAcceptId),
            };
            var ledgerActive = new HashSet<Guid>();

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive,
                out var toRemove, out var surviving);

            Assert.Contains(tombstonedAcceptId, toRemove);
            Assert.Empty(surviving);
        }

        [Fact]
        public void Partition_OfferedOrDeclined_NeverTouched()
        {
            var offered = Guid.NewGuid();
            var declined = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Offered(offered),
                Offered(declined),
            };
            var ledgerActive = new HashSet<Guid>();

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive,
                out var toRemove, out var surviving);

            Assert.Empty(toRemove);
            Assert.Empty(surviving);
        }

        [Fact]
        public void Partition_TerminalStillInLedger_Preserved()
        {
            var completed = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Terminal(completed),
            };
            var ledgerActive = new HashSet<Guid>();
            var ledgerTerminal = new HashSet<Guid> { completed };

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive, ledgerTerminal,
                out var toRemove, out var survivingActive, out var survivingTerminal);

            Assert.Empty(toRemove);
            Assert.Empty(survivingActive);
            Assert.Contains(completed, survivingTerminal);
        }

        [Fact]
        public void Partition_TombstonedTerminalContract_Removed()
        {
            var tombstonedComplete = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Terminal(tombstonedComplete),
            };
            var ledgerActive = new HashSet<Guid>();
            var ledgerTerminal = new HashSet<Guid>();

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive, ledgerTerminal,
                out var toRemove, out var survivingActive, out var survivingTerminal);

            Assert.Contains(tombstonedComplete, toRemove);
            Assert.Empty(survivingActive);
            Assert.Empty(survivingTerminal);
        }

        [Fact]
        public void Partition_TombstonedFinishedTerminalContract_RemovedFromFinishedBucket()
        {
            var tombstonedComplete = Guid.NewGuid();
            var currentEntries = new List<KspStatePatcher.ContractFilterEntry>();
            var finishedEntries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Terminal(tombstonedComplete),
            };
            var ledgerActive = new HashSet<Guid>();
            var ledgerTerminal = new HashSet<Guid>();

            KspStatePatcher.PartitionContractsForPatch(
                currentEntries,
                finishedEntries,
                ledgerActive,
                ledgerTerminal,
                out var toRemoveCurrent,
                out var toRemoveFinished,
                out var survivingActive,
                out var survivingTerminal);

            Assert.Empty(toRemoveCurrent);
            Assert.Contains(tombstonedComplete, toRemoveFinished);
            Assert.Empty(survivingActive);
            Assert.Empty(survivingTerminal);
        }

        [Fact]
        public void Partition_FinishedTerminalStillInLedger_Preserved()
        {
            var completed = Guid.NewGuid();
            var currentEntries = new List<KspStatePatcher.ContractFilterEntry>();
            var finishedEntries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Terminal(completed),
            };
            var ledgerActive = new HashSet<Guid>();
            var ledgerTerminal = new HashSet<Guid> { completed };

            KspStatePatcher.PartitionContractsForPatch(
                currentEntries,
                finishedEntries,
                ledgerActive,
                ledgerTerminal,
                out var toRemoveCurrent,
                out var toRemoveFinished,
                out var survivingActive,
                out var survivingTerminal);

            Assert.Empty(toRemoveCurrent);
            Assert.Empty(toRemoveFinished);
            Assert.Empty(survivingActive);
            Assert.Contains(completed, survivingTerminal);
        }

        [Fact]
        public void Partition_MixedBucket_RemovesOnlyUnsupportedActiveAndTerminal()
        {
            var activeInLedger = Guid.NewGuid();
            var activeStale = Guid.NewGuid();
            var offered = Guid.NewGuid();
            var terminalInLedger = Guid.NewGuid();
            var terminalStale = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Active(activeInLedger),
                Active(activeStale),
                Offered(offered),
                Terminal(terminalInLedger),
                Terminal(terminalStale),
            };
            var ledgerActive = new HashSet<Guid> { activeInLedger };
            var ledgerTerminal = new HashSet<Guid> { terminalInLedger };

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive, ledgerTerminal,
                out var toRemove, out var surviving, out var survivingTerminal);

            Assert.Equal(2, toRemove.Count);
            Assert.Contains(activeStale, toRemove);
            Assert.Contains(terminalStale, toRemove);
            Assert.Single(surviving);
            Assert.Contains(activeInLedger, surviving);
            Assert.Single(survivingTerminal);
            Assert.Contains(terminalInLedger, survivingTerminal);

            Assert.DoesNotContain(offered, toRemove);
            Assert.DoesNotContain(offered, surviving);
            Assert.DoesNotContain(offered, survivingTerminal);
        }

        [Fact]
        public void Partition_EmptyLedger_RemovesAllActiveButPreservesOffered()
        {
            var activeA = Guid.NewGuid();
            var activeB = Guid.NewGuid();
            var offered = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Active(activeA),
                Active(activeB),
                Offered(offered),
            };
            var ledgerActive = new HashSet<Guid>();

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive,
                out var toRemove, out var surviving);

            Assert.Equal(2, toRemove.Count);
            Assert.Contains(activeA, toRemove);
            Assert.Contains(activeB, toRemove);
            Assert.Empty(surviving);
            Assert.DoesNotContain(offered, toRemove);
        }

        [Fact]
        public void Partition_EmptyEntries_NoOp()
        {
            KspStatePatcher.PartitionContractsForPatch(
                new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<Guid>(),
                out var toRemove, out var surviving);

            Assert.Empty(toRemove);
            Assert.Empty(surviving);
        }

        [Fact]
        public void Partition_NullEntries_NoThrow()
        {
            KspStatePatcher.PartitionContractsForPatch(null, new HashSet<Guid>(),
                out var toRemove, out var surviving);

            Assert.Empty(toRemove);
            Assert.Empty(surviving);
        }
    }
}
