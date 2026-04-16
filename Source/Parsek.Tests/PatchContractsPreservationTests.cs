using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §B/#404 of career-earnings-bundle plan: PatchContracts must preserve the
    /// Offered + Finished buckets and only remove Active contracts that are no longer
    /// in the ledger. The old code unconditionally cleared both buckets, which wiped
    /// Mission Control's Offered list and ContractsFinished game history on every
    /// recalc.
    ///
    /// The real PatchContracts method needs a live KSP ContractSystem. This test
    /// exercises the pure <see cref="KspStatePatcher.PartitionContractsForPatch"/>
    /// helper which implements the filtering rules.
    /// </summary>
    public class PatchContractsPreservationTests
    {
        private static KspStatePatcher.ContractFilterEntry Active(Guid g) =>
            new KspStatePatcher.ContractFilterEntry { Id = g, IsActive = true };
        private static KspStatePatcher.ContractFilterEntry Inactive(Guid g) =>
            new KspStatePatcher.ContractFilterEntry { Id = g, IsActive = false };

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
        public void Partition_NonActive_NeverTouched()
        {
            // This is the Offered/Declined/Finished preservation guarantee.
            var offered = Guid.NewGuid();
            var finished = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Inactive(offered),
                Inactive(finished),
            };
            var ledgerActive = new HashSet<Guid>();

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive,
                out var toRemove, out var surviving);

            Assert.Empty(toRemove);     // nothing to remove
            Assert.Empty(surviving);    // nothing to preserve in-place
            // The caller's contract: anything not in toRemove AND not in surviving
            // MUST be left alone. Both non-Actives satisfy that.
        }

        [Fact]
        public void Partition_MixedBucket_OnlyStaleActivesRemoved()
        {
            var activeInLedger = Guid.NewGuid();
            var activeStale = Guid.NewGuid();
            var offered = Guid.NewGuid();
            var finished = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Active(activeInLedger),
                Active(activeStale),
                Inactive(offered),
                Inactive(finished),
            };
            var ledgerActive = new HashSet<Guid> { activeInLedger };

            KspStatePatcher.PartitionContractsForPatch(entries, ledgerActive,
                out var toRemove, out var surviving);

            Assert.Single(toRemove);
            Assert.Contains(activeStale, toRemove);
            Assert.Single(surviving);
            Assert.Contains(activeInLedger, surviving);

            // Non-Actives are not in either list — preservation invariant.
            Assert.DoesNotContain(offered, toRemove);
            Assert.DoesNotContain(finished, toRemove);
            Assert.DoesNotContain(offered, surviving);
            Assert.DoesNotContain(finished, surviving);
        }

        [Fact]
        public void Partition_EmptyLedger_RemovesAllActiveButPreservesNonActive()
        {
            var activeA = Guid.NewGuid();
            var activeB = Guid.NewGuid();
            var offered = Guid.NewGuid();
            var entries = new List<KspStatePatcher.ContractFilterEntry>
            {
                Active(activeA),
                Active(activeB),
                Inactive(offered),
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
