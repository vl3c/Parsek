using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KerbalsWindowUITests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalsWindowUITests()
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

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        private static KerbalsModule.KerbalSlot Slot(
            string owner, string trait, List<string> chain, bool permanentlyGone = false)
        {
            return new KerbalsModule.KerbalSlot
            {
                OwnerName = owner,
                OwnerTrait = trait,
                OwnerPermanentlyGone = permanentlyGone,
                Chain = chain ?? new List<string>()
            };
        }

        private static KerbalsModule.KerbalReservation Res(
            string name, double untilUT, bool permanent = false)
        {
            return new KerbalsModule.KerbalReservation
            {
                KerbalName = name,
                ReservedUntilUT = untilUT,
                IsPermanent = permanent
            };
        }

        // Mirrors KerbalsModule.GetActiveChainIndex(KerbalSlot) semantics for the
        // pure Build() call. Kept in sync with KerbalsModule lines 1046-1067.
        private static KerbalsWindowUI.ActiveChainIndexFunc ActiveChainIndexLike(
            IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> reservations)
        {
            return slot =>
            {
                if (slot == null) return KerbalsModule.NoActiveChainOccupant;
                if (slot.OwnerPermanentlyGone) return KerbalsModule.NoActiveChainOccupant;
                if (!reservations.ContainsKey(slot.OwnerName)) return KerbalsModule.ActiveOwnerIndex;
                for (int i = 0; i < slot.Chain.Count; i++)
                {
                    string s = slot.Chain[i];
                    if (s == null || !reservations.ContainsKey(s))
                        return i;
                }
                return slot.Chain.Count;
            };
        }

        // ──────────────────────────────────────────────────────────────────
        // Tests
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_Empty_ReturnsEmptySections()
        {
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>();
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>();
            var retired = new List<string>();

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Empty(vm.Reserved);
            Assert.Empty(vm.Active);
            Assert.Empty(vm.Retired);
        }

        [Fact]
        public void Build_RetiredOnly_PopulatesRetiredOnly()
        {
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>();
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>();
            var retired = new List<string> { "Bill Kerman", "Hanley Kerman" };

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Empty(vm.Reserved);
            Assert.Empty(vm.Active);
            Assert.Equal(2, vm.Retired.Count);
            Assert.Equal("Bill Kerman", vm.Retired[0].StandIn);
            Assert.Equal("", vm.Retired[0].FormerOwner);
            Assert.Equal("", vm.Retired[0].Trait);
            Assert.Equal("Hanley Kerman", vm.Retired[1].StandIn);
            Assert.Equal("", vm.Retired[1].FormerOwner);
        }

        [Fact]
        public void Build_RetiredUnsorted_ReturnsOrdinallySorted()
        {
            // KerbalsModule.GetRetiredKerbals() returns a snapshot of a HashSet, so iteration
            // order is nondeterministic. Build() must sort for stable save-over-save display.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>();
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>();
            var retired = new List<string>
            {
                "Hanley Kerman",
                "Bill Kerman",
                "Adara Kerman",
                "Zim Kerman"
            };

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Equal(
                new[] { "Adara Kerman", "Bill Kerman", "Hanley Kerman", "Zim Kerman" },
                vm.Retired.Select(e => e.StandIn).ToArray());
        }

        [Fact]
        public void Build_RetiredLinkedToFormerOwner_IncludesTraitAndOwner()
        {
            // Retired stand-in name appears in a slot's Chain → enrich the retired row
            // with the owner's name and trait.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman", "Hanley Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) }
            };
            // Bill retired; Hanley is the active stand-in (not reserved).
            var retired = new List<string> { "Bill Kerman" };

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Single(vm.Retired);
            Assert.Equal("Bill Kerman", vm.Retired[0].StandIn);
            Assert.Equal("Jebediah Kerman", vm.Retired[0].FormerOwner);
            Assert.Equal("Pilot", vm.Retired[0].Trait);
        }

        [Fact]
        public void FormatRetiredRow_RendersTraitAndOwnerWhenPresent()
        {
            var orphan = new KerbalsWindowUI.RetiredEntry { StandIn = "Bill Kerman" };
            Assert.Equal("Bill Kerman", KerbalsWindowUI.FormatRetiredRow(orphan));

            var linked = new KerbalsWindowUI.RetiredEntry
            {
                StandIn = "Bill Kerman",
                FormerOwner = "Jebediah Kerman",
                Trait = "Pilot"
            };
            Assert.Equal(
                "Bill Kerman [Pilot] \u2014 stood in for Jebediah Kerman",
                KerbalsWindowUI.FormatRetiredRow(linked));
        }

        [Fact]
        public void Build_SlotWithActiveStandIn_PopulatesReservedAndActive()
        {
            // Jeb reserved, Bill is Jeb's stand-in (not reserved) → Bill is the active occupant.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) }
            };
            var retired = new List<string>();

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Single(vm.Reserved);
            Assert.Equal("Jebediah Kerman", vm.Reserved[0].Owner);
            Assert.Equal("Pilot", vm.Reserved[0].Trait);
            Assert.Equal(1000.0, vm.Reserved[0].UntilUT);

            Assert.Single(vm.Active);
            Assert.Equal("Bill Kerman", vm.Active[0].StandIn);
            Assert.Equal("Jebediah Kerman", vm.Active[0].Owner);
            Assert.Equal("Pilot", vm.Active[0].Trait);
        }

        [Fact]
        public void Build_OwnerReturnedNoActiveStandIn()
        {
            // Owner not reserved → owner is back in seat, no stand-in active.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>();
            var retired = new List<string> { "Bill Kerman" };

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Empty(vm.Reserved);
            Assert.Empty(vm.Active);
            Assert.Single(vm.Retired);
            Assert.Equal("Bill Kerman", vm.Retired[0].StandIn);
            // Owner walked back into the seat but the slot record still links Bill to Jeb.
            Assert.Equal("Jebediah Kerman", vm.Retired[0].FormerOwner);
            Assert.Equal("Pilot", vm.Retired[0].Trait);
        }

        [Fact]
        public void Build_DeepChainReservedTip_NoActiveEntry()
        {
            // Jeb AND Bill reserved, but chain depth is only 1 → no active occupant;
            // activeIndex == Chain.Count signals "chain needs more depth".
            // This explicitly covers the inverted rule from an earlier plan draft.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) },
                { "Bill Kerman", Res("Bill Kerman", 1500.0) }
            };
            var retired = new List<string>();

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Single(vm.Reserved);
            Assert.Equal("Jebediah Kerman", vm.Reserved[0].Owner);
            Assert.Empty(vm.Active);
        }

        [Fact]
        public void Build_PermanentReservationExcludesFromReserved()
        {
            // Dead kerbal (permanent reservation + OwnerPermanentlyGone) is filtered out in v1.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string>(), permanentlyGone: true) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", double.PositiveInfinity, permanent: true) }
            };
            var retired = new List<string>();

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Empty(vm.Reserved);
            Assert.Empty(vm.Active);
        }

        [Fact]
        public void Build_NullChainEntrySkipped()
        {
            // Null chain entry from mid-EnsureChainDepth state must not produce an Active row.
            // Inject an activeChainIndex that returns 0 to exercise the null-skip guard.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { null }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) }
            };
            var retired = new List<string>();

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                slot => 0);

            Assert.Single(vm.Reserved);
            Assert.Empty(vm.Active);
        }

        [Fact]
        public void Build_MultipleSlots_DeterministicOrdering()
        {
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Valentina Kerman", Slot("Valentina Kerman", "Pilot",
                    new List<string> { "Hanley Kerman" }) },
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman" }) },
                { "Bob Kerman", Slot("Bob Kerman", "Engineer",
                    new List<string> { "Ted Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Valentina Kerman", Res("Valentina Kerman", 2000.0) },
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) },
                { "Bob Kerman", Res("Bob Kerman", 1500.0) }
            };
            var retired = new List<string>();

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Equal(3, vm.Reserved.Count);
            Assert.Equal("Bob Kerman", vm.Reserved[0].Owner);
            Assert.Equal("Jebediah Kerman", vm.Reserved[1].Owner);
            Assert.Equal("Valentina Kerman", vm.Reserved[2].Owner);

            Assert.Equal(3, vm.Active.Count);
            Assert.Equal("Bob Kerman", vm.Active[0].Owner);
            Assert.Equal("Jebediah Kerman", vm.Active[1].Owner);
            Assert.Equal("Valentina Kerman", vm.Active[2].Owner);
        }

        // ──────────────────────────────────────────────────────────────────
        // Per-recording crew end-states (todo #415)
        // ──────────────────────────────────────────────────────────────────

        private static Recording RecWithEndStates(
            string id,
            string vesselName,
            double endUT,
            Dictionary<string, KerbalEndState> endStates,
            bool resolved = true)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                ExplicitEndUT = endUT,
                CrewEndStates = endStates,
                CrewEndStatesResolved = resolved
            };
        }

        [Fact]
        public void Build_EndStates_EmptyWhenNoCommittedRecordings()
        {
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>();
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>();

            var vm = KerbalsWindowUI.Build(slots, reservations, new List<string>(),
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Empty(vm.EndStates);
        }

        [Fact]
        public void Build_EndStates_SingleKerbalMultipleMissions_GroupedAndChronological()
        {
            // Jeb flew two missions: Mun Hopper recovered at UT 12045; Duna Return died
            // at UT 88432. Rows should be grouped under Jeb and sorted by EndUT ascending.
            var recs = new List<Recording>
            {
                RecWithEndStates("duna-1", "Duna Return", 88432.0,
                    new Dictionary<string, KerbalEndState>
                    {
                        { "Jebediah Kerman", KerbalEndState.Dead }
                    }),
                RecWithEndStates("mun-1", "Mun Hopper", 12045.0,
                    new Dictionary<string, KerbalEndState>
                    {
                        { "Jebediah Kerman", KerbalEndState.Recovered }
                    })
            };

            var vm = KerbalsWindowUI.Build(
                new Dictionary<string, KerbalsModule.KerbalSlot>(),
                new Dictionary<string, KerbalsModule.KerbalReservation>(),
                new List<string>(),
                recs,
                slot => KerbalsModule.NoActiveChainOccupant);

            Assert.Equal(2, vm.EndStates.Count);
            Assert.Equal("Jebediah Kerman", vm.EndStates[0].KerbalName);
            Assert.Equal("Mun Hopper", vm.EndStates[0].RecordingName);
            Assert.Equal(KerbalEndState.Recovered, vm.EndStates[0].EndState);
            Assert.Equal(12045.0, vm.EndStates[0].EndUT);

            Assert.Equal("Jebediah Kerman", vm.EndStates[1].KerbalName);
            Assert.Equal("Duna Return", vm.EndStates[1].RecordingName);
            Assert.Equal(KerbalEndState.Dead, vm.EndStates[1].EndState);
            Assert.Equal(88432.0, vm.EndStates[1].EndUT);
        }

        [Fact]
        public void Build_EndStates_MultipleKerbals_GroupedByNameOrdinally()
        {
            // Mun Hopper had Jeb + Bill aboard. Grouping must be ordinal-stable on kerbal name.
            var recs = new List<Recording>
            {
                RecWithEndStates("mun-1", "Mun Hopper", 12045.0,
                    new Dictionary<string, KerbalEndState>
                    {
                        { "Jebediah Kerman", KerbalEndState.Recovered },
                        { "Bill Kerman", KerbalEndState.Recovered }
                    })
            };

            var vm = KerbalsWindowUI.Build(
                new Dictionary<string, KerbalsModule.KerbalSlot>(),
                new Dictionary<string, KerbalsModule.KerbalReservation>(),
                new List<string>(),
                recs,
                slot => KerbalsModule.NoActiveChainOccupant);

            Assert.Equal(2, vm.EndStates.Count);
            Assert.Equal("Bill Kerman", vm.EndStates[0].KerbalName);
            Assert.Equal("Jebediah Kerman", vm.EndStates[1].KerbalName);
        }

        [Fact]
        public void Build_EndStates_SkipsUnresolvedRecordings()
        {
            // Recording has a populated dict but CrewEndStatesResolved==false — still pending,
            // don't surface it yet. The resolved recording alone should contribute rows.
            var recs = new List<Recording>
            {
                RecWithEndStates("pending", "In-Flight Mission", 500.0,
                    new Dictionary<string, KerbalEndState>
                    {
                        { "Valentina Kerman", KerbalEndState.Aboard }
                    },
                    resolved: false),
                RecWithEndStates("done", "Recovered Mission", 1000.0,
                    new Dictionary<string, KerbalEndState>
                    {
                        { "Bob Kerman", KerbalEndState.Recovered }
                    })
            };

            var vm = KerbalsWindowUI.Build(
                new Dictionary<string, KerbalsModule.KerbalSlot>(),
                new Dictionary<string, KerbalsModule.KerbalReservation>(),
                new List<string>(),
                recs,
                slot => KerbalsModule.NoActiveChainOccupant);

            Assert.Single(vm.EndStates);
            Assert.Equal("Bob Kerman", vm.EndStates[0].KerbalName);
            Assert.Equal("Recovered Mission", vm.EndStates[0].RecordingName);
        }

        [Fact]
        public void Build_EndStates_SkipsRecordingsWithNullDict()
        {
            // Resolved == true but CrewEndStates == null (e.g. solo EVA with no crew). Skip.
            var recs = new List<Recording>
            {
                new Recording
                {
                    RecordingId = "eva-1",
                    VesselName = "Jeb (EVA)",
                    ExplicitEndUT = 200.0,
                    CrewEndStates = null,
                    CrewEndStatesResolved = true
                }
            };

            var vm = KerbalsWindowUI.Build(
                new Dictionary<string, KerbalsModule.KerbalSlot>(),
                new Dictionary<string, KerbalsModule.KerbalReservation>(),
                new List<string>(),
                recs,
                slot => KerbalsModule.NoActiveChainOccupant);

            Assert.Empty(vm.EndStates);
        }

        [Fact]
        public void FormatEndStateRow_RendersRecordingNameStateAndUT()
        {
            var entry = new KerbalsWindowUI.CrewEndStateEntry
            {
                KerbalName = "Jebediah Kerman",
                RecordingName = "Mun Hopper",
                EndUT = 12045.0,
                EndState = KerbalEndState.Recovered
            };
            Assert.Equal(
                "Mun Hopper \u2014 Recovered at UT 12045",
                KerbalsWindowUI.FormatEndStateRow(entry));

            var unnamed = new KerbalsWindowUI.CrewEndStateEntry
            {
                KerbalName = "Bill Kerman",
                RecordingName = "",
                EndUT = 50.0,
                EndState = KerbalEndState.Dead
            };
            Assert.Equal(
                "(unnamed) \u2014 Dead at UT 50",
                KerbalsWindowUI.FormatEndStateRow(unnamed));
        }

        [Fact]
        public void Build_EmitsVerboseLog_WithSectionCounts()
        {
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) }
            };
            var retired = new List<string> { "Hanley Kerman", "Barney Kerman" };

            KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("KerbalsWindow: built VM")
                && l.Contains("reserved=1")
                && l.Contains("active=1")
                && l.Contains("retired=2")
                && l.Contains("endStates=0"));
        }
    }
}
