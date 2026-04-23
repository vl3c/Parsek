using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
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
        // Topology tests
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_Topology_EmptyInputs_ReturnsEmptyTopology()
        {
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>();
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>();
            var retired = new List<string>();

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Empty(vm.Topology);
            Assert.Empty(vm.OrphanRetired);
        }

        [Fact]
        public void Build_Topology_SingleSlotNoChain_RendersAsLeaf()
        {
            // Jeb reserved, empty chain → one topology entry with zero chain members;
            // header will be "[Jeb] — reserved until UT …", no arrow, no count.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot", new List<string>()) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 18230.0) }
            };

            var vm = KerbalsWindowUI.Build(slots, reservations, new List<string>(),
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Single(vm.Topology);
            var e = vm.Topology[0];
            Assert.Equal("Jebediah Kerman", e.OwnerName);
            Assert.Equal("Pilot", e.OwnerTrait);
            Assert.True(e.OwnerReserved);
            Assert.False(e.OwnerPermanentlyGone);
            Assert.Equal(18230.0, e.OwnerReservedUntilUT);
            Assert.Empty(e.Chain);
        }

        [Fact]
        public void Build_Topology_SlotWithActiveStandIn_LabelsActive()
        {
            // Jeb reserved, Bill is Jeb's stand-in (not reserved) → Bill is the active
            // chain occupant. Classification: Active (not Displaced, not Retired).
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) }
            };

            var vm = KerbalsWindowUI.Build(slots, reservations, new List<string>(),
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Single(vm.Topology);
            var e = vm.Topology[0];
            Assert.Single(e.Chain);
            Assert.Equal("Bill Kerman", e.Chain[0].Name);
            Assert.Equal(0, e.Chain[0].ChainIndex);
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Active, e.Chain[0].Status);
        }

        [Fact]
        public void Build_Topology_SlotWithRetiredAndActive()
        {
            // Chain [Bill, Hanley]: Bill retired (seen in recordings, displaced), Hanley
            // the active stand-in for Jeb. activeIdx==1 (Hanley is the first non-reserved
            // occupant after Bill, but retired-first dominates for Bill).
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Bill Kerman", "Hanley Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) }
            };
            // activeChainIndex injected so Hanley's slot[1] is Active.
            KerbalsWindowUI.ActiveChainIndexFunc activeFn = slot => 1;
            var retired = new List<string> { "Bill Kerman" };

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                activeFn);

            Assert.Single(vm.Topology);
            var e = vm.Topology[0];
            Assert.Equal(2, e.Chain.Count);
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Retired, e.Chain[0].Status);
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Active, e.Chain[1].Status);
            Assert.Empty(vm.OrphanRetired);
        }

        [Fact]
        public void Build_Topology_DeepChainAllReserved_EntriesAreDisplaced()
        {
            // Jeb and Bill both reserved; chain [Bill]. activeChainIndexOf returns
            // Chain.Count==1, so no chain slot is flagged Active, and none are retired.
            // Expected: Bill → Displaced.
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

            var vm = KerbalsWindowUI.Build(slots, reservations, new List<string>(),
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Single(vm.Topology);
            var e = vm.Topology[0];
            Assert.Single(e.Chain);
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Displaced, e.Chain[0].Status);
        }

        [Fact]
        public void Build_Topology_DeadOwner_IncludedWithFlag()
        {
            // Dead Val with a historical chain entry. Entry stays in topology — the
            // owner header renders as deceased, chain members preserved so the player
            // can still see who flew in Val's slot.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Valentina Kerman", Slot("Valentina Kerman", "Pilot",
                    new List<string> { "Hanley Kerman" }, permanentlyGone: true) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Valentina Kerman", Res("Valentina Kerman", double.PositiveInfinity, permanent: true) }
            };

            var vm = KerbalsWindowUI.Build(slots, reservations, new List<string>(),
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Single(vm.Topology);
            var e = vm.Topology[0];
            Assert.True(e.OwnerPermanentlyGone);
            Assert.False(e.OwnerReserved);
            Assert.Single(e.Chain);
            Assert.Equal("Hanley Kerman", e.Chain[0].Name);
        }

        [Fact]
        public void Build_Topology_OrphanRetired_LandsInOrphanList()
        {
            // Retired Bill with no slot referencing him → orphan. Adara too. Orphans
            // are ordinal-sorted so the UI is stable across runs.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>();
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>();
            var retired = new List<string> { "Bill Kerman", "Adara Kerman" };

            var vm = KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Empty(vm.Topology);
            Assert.Equal(new[] { "Adara Kerman", "Bill Kerman" },
                vm.OrphanRetired.ToArray());
        }

        [Fact]
        public void Build_Topology_MultipleSlots_OrdinalOrdering()
        {
            // Dictionary enumeration order is implementation-defined; Build() must sort
            // owner names ordinally so the rendered list is stable save-over-save.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Valentina Kerman", Slot("Valentina Kerman", "Pilot", new List<string>()) },
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot", new List<string>()) },
                { "Bob Kerman", Slot("Bob Kerman", "Engineer", new List<string>()) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Valentina Kerman", Res("Valentina Kerman", 2000.0) },
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) },
                { "Bob Kerman", Res("Bob Kerman", 1500.0) }
            };

            var vm = KerbalsWindowUI.Build(slots, reservations, new List<string>(),
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Equal(3, vm.Topology.Count);
            Assert.Equal("Bob Kerman", vm.Topology[0].OwnerName);
            Assert.Equal("Jebediah Kerman", vm.Topology[1].OwnerName);
            Assert.Equal("Valentina Kerman", vm.Topology[2].OwnerName);
        }

        [Fact]
        public void Build_Topology_NullChainEntrySkipped()
        {
            // Mid-EnsureChainDepth state leaves nulls in the chain. Those must not
            // appear as ChainMember rows (nor contribute to the count badge).
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { null, "Bill Kerman" }) }
            };
            var reservations = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1000.0) }
            };

            var vm = KerbalsWindowUI.Build(slots, reservations, new List<string>(),
                committedRecordings: null,
                slot => 1);

            Assert.Single(vm.Topology);
            var e = vm.Topology[0];
            Assert.Single(e.Chain);
            Assert.Equal("Bill Kerman", e.Chain[0].Name);
            Assert.Equal(1, e.Chain[0].ChainIndex);
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Active, e.Chain[0].Status);
        }

        [Fact]
        public void Build_Topology_RetiredAlsoInChain_NotDuplicatedAsOrphan()
        {
            // Bill is in both `retired` and Jeb's Chain. He must appear exactly once,
            // inside Jeb's topology entry as Retired, and NOT as an orphan.
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

            Assert.Single(vm.Topology);
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Retired, vm.Topology[0].Chain[0].Status);
            Assert.Empty(vm.OrphanRetired);
        }

        [Fact]
        public void CountExpandableChainEntries_SkipsNullNames()
        {
            var chain = new List<KerbalsWindowUI.ChainMember>
            {
                new KerbalsWindowUI.ChainMember { Name = "Bill Kerman" },
                new KerbalsWindowUI.ChainMember { Name = null },
                new KerbalsWindowUI.ChainMember { Name = "" },
                new KerbalsWindowUI.ChainMember { Name = "Hanley Kerman" }
            };
            Assert.Equal(2, KerbalsWindowUI.CountExpandableChainEntries(chain));
            Assert.Equal(0, KerbalsWindowUI.CountExpandableChainEntries(null));
        }

        // ──────────────────────────────────────────────────────────────────
        // FormatOwnerHeader
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void FormatOwnerHeader_ReservedUntilUT_FormatsInvariant()
        {
            // Thread set to German so a non-invariant format would insert a thousands
            // separator or decimal comma. Result must remain locale-neutral.
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var entry = new KerbalsWindowUI.SlotTopologyEntry
                {
                    OwnerName = "Jebediah Kerman",
                    OwnerTrait = "Pilot",
                    OwnerReserved = true,
                    OwnerReservedUntilUT = 18230.0
                };
                Assert.Equal(
                    "Jebediah Kerman [Pilot] - reserved until UT 18230",
                    KerbalsWindowUI.FormatOwnerHeader(entry));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void FormatOwnerHeader_DeadOwner_IncludesDeceasedSuffix()
        {
            var entry = new KerbalsWindowUI.SlotTopologyEntry
            {
                OwnerName = "Valentina Kerman",
                OwnerTrait = "Pilot",
                OwnerPermanentlyGone = true
            };
            Assert.Equal(
                "Valentina Kerman [Pilot] - deceased",
                KerbalsWindowUI.FormatOwnerHeader(entry));
        }

        [Fact]
        public void FormatOwnerHeader_ActiveOwner_NoReservationSuffix()
        {
            var entry = new KerbalsWindowUI.SlotTopologyEntry
            {
                OwnerName = "Bob Kerman",
                OwnerTrait = "Engineer",
                OwnerReserved = false
            };
            Assert.Equal(
                "Bob Kerman [Engineer] - active",
                KerbalsWindowUI.FormatOwnerHeader(entry));
        }

        [Fact]
        public void FormatOwnerHeader_PermanentReservation_RendersReservedWithoutUT()
        {
            // Open-ended reservations (ReservedUntilUT == +Inf, !IsPermanent would be a
            // data bug, but the formatter defends against it) render as just "reserved".
            var entry = new KerbalsWindowUI.SlotTopologyEntry
            {
                OwnerName = "Jebediah Kerman",
                OwnerTrait = "Pilot",
                OwnerReserved = true,
                OwnerReservedUntilUT = double.PositiveInfinity
            };
            Assert.Equal(
                "Jebediah Kerman [Pilot] - reserved",
                KerbalsWindowUI.FormatOwnerHeader(entry));
        }

        [Fact]
        public void FormatChainMember_RendersNameAndStatusTag()
        {
            Assert.Equal("Bill Kerman (retired)", KerbalsWindowUI.FormatChainMember(
                new KerbalsWindowUI.ChainMember { Name = "Bill Kerman", Status = KerbalsWindowUI.ChainMemberStatus.Retired }));
            Assert.Equal("Hanley Kerman (active)", KerbalsWindowUI.FormatChainMember(
                new KerbalsWindowUI.ChainMember { Name = "Hanley Kerman", Status = KerbalsWindowUI.ChainMemberStatus.Active }));
            Assert.Equal("Sam Kerman (displaced)", KerbalsWindowUI.FormatChainMember(
                new KerbalsWindowUI.ChainMember { Name = "Sam Kerman", Status = KerbalsWindowUI.ChainMemberStatus.Displaced }));
        }

        // ──────────────────────────────────────────────────────────────────
        // Per-recording crew end-states (#415 Per-Recording Fates)
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
                "Mun Hopper - Recovered at UT 12045",
                KerbalsWindowUI.FormatEndStateRow(entry));

            var unnamed = new KerbalsWindowUI.CrewEndStateEntry
            {
                KerbalName = "Bill Kerman",
                RecordingName = "",
                EndUT = 50.0,
                EndState = KerbalEndState.Dead
            };
            Assert.Equal(
                "(unnamed) - Dead at UT 50",
                KerbalsWindowUI.FormatEndStateRow(unnamed));
        }

        // ──────────────────────────────────────────────────────────────────
        // Subitem-indent parity (Roster State ↔ Mission Outcomes)
        //
        // These tests lock down the shared leading-indent convention used by
        // both tabs' subitem rows. If someone refactors one tab's render code
        // and silently changes the indent, the parity test fails before the
        // drift reaches the player. This covers a regression we already hit
        // twice during #416 UI polish.
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void SubitemIndent_IsFourSpaces()
        {
            // Regression: fails if anyone "cleans up" the indent constant to a
            // different value, silently shifting both tabs' subitem rendering.
            Assert.Equal("    ", KerbalsWindowUI.SubitemIndent);
        }

        [Fact]
        public void FormatMissionOutcomeSubitemText_StartsWithSubitemIndent()
        {
            // Regression: fails if Mission Outcomes subitem composition stops
            // using the shared indent and, e.g., reverts to a BeginHorizontal
            // + GUILayout.Space pattern (which historically produced the
            // "subitems not aligned with parent" bug).
            var entry = new KerbalsWindowUI.CrewEndStateEntry
            {
                KerbalName = "Jeb Kerman",
                RecordingName = "Mun Hopper",
                EndUT = 12045.0,
                EndState = KerbalEndState.Recovered
            };
            string text = KerbalsWindowUI.FormatMissionOutcomeSubitemText(entry);
            Assert.StartsWith(KerbalsWindowUI.SubitemIndent, text, StringComparison.Ordinal);
            // And the body itself (after the indent) is the unchanged FormatEndStateRow output.
            Assert.Equal(
                KerbalsWindowUI.SubitemIndent + KerbalsWindowUI.FormatEndStateRow(entry),
                text);
        }

        [Fact]
        public void FormatRosterChainMemberText_StartsWithSubitemIndent_AndUsesBranchGlyph()
        {
            // Regression: fails if the Roster chain-member render path drops the
            // shared indent, or picks the wrong tree-branch glyph between the
            // last-child (└─) and mid-child (├─) cases.
            var member = new KerbalsWindowUI.ChainMember
            {
                Name = "Bill Kerman",
                ChainIndex = 0,
                Status = KerbalsWindowUI.ChainMemberStatus.Retired
            };

            string mid = KerbalsWindowUI.FormatRosterChainMemberText(member, isLast: false);
            string last = KerbalsWindowUI.FormatRosterChainMemberText(member, isLast: true);

            Assert.StartsWith(KerbalsWindowUI.SubitemIndent, mid, StringComparison.Ordinal);
            Assert.StartsWith(KerbalsWindowUI.SubitemIndent, last, StringComparison.Ordinal);
            Assert.Contains("\u251c\u2500", mid);   // mid-child branch char (├─)
            Assert.Contains("\u2514\u2500", last);  // last-child branch char (└─)
            Assert.Contains(KerbalsWindowUI.FormatChainMember(member), mid);
            Assert.Contains(KerbalsWindowUI.FormatChainMember(member), last);
        }

        [Fact]
        public void MissionOutcomesAndRosterUseSameSubitemIndent()
        {
            // Parity test — the whole reason for extracting SubitemIndent into a
            // shared constant. Fails if either tab's helper starts building its
            // own indent from a literal string, letting the two drift apart.
            var entry = new KerbalsWindowUI.CrewEndStateEntry
            {
                KerbalName = "Jeb Kerman",
                RecordingName = "Mun Hopper",
                EndUT = 12045.0,
                EndState = KerbalEndState.Recovered
            };
            var member = new KerbalsWindowUI.ChainMember
            {
                Name = "Bill Kerman",
                ChainIndex = 0,
                Status = KerbalsWindowUI.ChainMemberStatus.Retired
            };

            string outcomeText = KerbalsWindowUI.FormatMissionOutcomeSubitemText(entry);
            string rosterText = KerbalsWindowUI.FormatRosterChainMemberText(member, isLast: true);

            // Both texts must begin with the same leading run of spaces.
            Assert.StartsWith(KerbalsWindowUI.SubitemIndent, outcomeText, StringComparison.Ordinal);
            Assert.StartsWith(KerbalsWindowUI.SubitemIndent, rosterText, StringComparison.Ordinal);
            // The indent length must be identical (defensive check in case a future
            // SubitemIndent becomes non-space; then startswith + equal-length still
            // holds but the content must match exactly).
            Assert.Equal(KerbalsWindowUI.SubitemIndent.Length,
                outcomeText.Length - KerbalsWindowUI.FormatEndStateRow(entry).Length);
        }

        // ──────────────────────────────────────────────────────────────────
        // FormatKerbalSummary (#415-1 fold toggle)
        // ──────────────────────────────────────────────────────────────────

        private static KerbalsWindowUI.CrewEndStateEntry EndStateEntry(
            string kerbalName, KerbalEndState state)
        {
            return new KerbalsWindowUI.CrewEndStateEntry
            {
                KerbalName = kerbalName,
                RecordingName = "",
                RecordingId = "",
                EndUT = 0.0,
                EndState = state
            };
        }

        [Fact]
        public void FormatKerbalSummary_SingleMissionRecovered_RendersSingular()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                EndStateEntry("Bill Kerman", KerbalEndState.Recovered)
            };

            string result = KerbalsWindowUI.FormatKerbalSummary(
                "Bill Kerman", entries, 0, entries.Count);

            Assert.Equal("Bill Kerman (1 mission - 1 Recovered)", result);
        }

        [Fact]
        public void FormatKerbalSummary_MultipleMissions_MixedStates_OrdersDeadRecoveredAboard()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                EndStateEntry("Jebediah Kerman", KerbalEndState.Recovered),
                EndStateEntry("Jebediah Kerman", KerbalEndState.Aboard),
                EndStateEntry("Jebediah Kerman", KerbalEndState.Dead),
                EndStateEntry("Jebediah Kerman", KerbalEndState.Recovered)
            };

            string result = KerbalsWindowUI.FormatKerbalSummary(
                "Jebediah Kerman", entries, 0, entries.Count);

            Assert.Equal(
                "Jebediah Kerman (4 missions - 1 Dead, 2 Recovered, 1 Aboard)",
                result);
        }

        [Fact]
        public void FormatKerbalSummary_OmitsZeroCountStates()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                EndStateEntry("Bill Kerman", KerbalEndState.Dead),
                EndStateEntry("Bill Kerman", KerbalEndState.Dead),
                EndStateEntry("Bill Kerman", KerbalEndState.Dead)
            };

            string result = KerbalsWindowUI.FormatKerbalSummary(
                "Bill Kerman", entries, 0, entries.Count);

            Assert.Equal("Bill Kerman (3 missions - 3 Dead)", result);
            Assert.DoesNotContain("Recovered", result);
            Assert.DoesNotContain("Aboard", result);
            Assert.DoesNotContain("Unknown", result);
        }

        [Fact]
        public void FormatKerbalSummary_UnknownStateBucketed()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                EndStateEntry("Hanley Kerman", KerbalEndState.Aboard),
                EndStateEntry("Hanley Kerman", KerbalEndState.Unknown)
            };

            string result = KerbalsWindowUI.FormatKerbalSummary(
                "Hanley Kerman", entries, 0, entries.Count);

            Assert.Equal(
                "Hanley Kerman (2 missions - 1 Aboard, 1 Unknown)",
                result);
        }

        [Fact]
        public void FormatKerbalSummary_UsesSliceBoundaries()
        {
            // Interleaved entries for three kerbals. Only Bill's slice [2, 5) should be
            // counted — the slice is 2 Recovered + 1 Dead.
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                EndStateEntry("Adara Kerman", KerbalEndState.Aboard),
                EndStateEntry("Adara Kerman", KerbalEndState.Recovered),
                EndStateEntry("Bill Kerman", KerbalEndState.Recovered),
                EndStateEntry("Bill Kerman", KerbalEndState.Dead),
                EndStateEntry("Bill Kerman", KerbalEndState.Recovered),
                EndStateEntry("Jebediah Kerman", KerbalEndState.Dead),
                EndStateEntry("Jebediah Kerman", KerbalEndState.Dead)
            };

            string result = KerbalsWindowUI.FormatKerbalSummary(
                "Bill Kerman", entries, 2, 5);

            Assert.Equal(
                "Bill Kerman (3 missions - 1 Dead, 2 Recovered)",
                result);
        }

        [Fact]
        public void FormatMissionOutcomeHeaderText_Unfolded_BoldsOnlyKerbalName()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                EndStateEntry("Bill Kerman", KerbalEndState.Recovered)
            };

            string result = KerbalsWindowUI.FormatMissionOutcomeHeaderText(
                "Bill Kerman", entries, 0, entries.Count, folded: false);

            Assert.Equal("<b>Bill Kerman</b>", result);
        }

        [Fact]
        public void FormatMissionOutcomeHeaderText_Folded_BoldsOnlyKerbalName()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                EndStateEntry("Jebediah Kerman", KerbalEndState.Recovered),
                EndStateEntry("Jebediah Kerman", KerbalEndState.Aboard),
                EndStateEntry("Jebediah Kerman", KerbalEndState.Dead)
            };

            string result = KerbalsWindowUI.FormatMissionOutcomeHeaderText(
                "Jebediah Kerman", entries, 0, entries.Count, folded: true);

            Assert.Equal(
                "<b>Jebediah Kerman</b> (3 missions - 1 Dead, 1 Recovered, 1 Aboard)",
                result);
            Assert.DoesNotContain("<b> (3 missions", result);
        }

        [Fact]
        public void ToggleFold_WhenNotFolded_AddsToSetAndLogsFolded()
        {
            var folded = new HashSet<string>(StringComparer.Ordinal);

            bool nowFolded = KerbalsWindowUI.ToggleFold(folded, "Bill Kerman", 3);

            Assert.True(nowFolded);
            Assert.Contains("Bill Kerman", folded);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals fold toggled")
                && l.Contains("'Bill Kerman'")
                && l.Contains("-> folded")
                && l.Contains("(3 missions)"));
        }

        [Fact]
        public void ToggleFold_WhenAlreadyFolded_RemovesFromSetAndLogsUnfolded()
        {
            var folded = new HashSet<string>(StringComparer.Ordinal) { "Jebediah Kerman" };

            bool nowFolded = KerbalsWindowUI.ToggleFold(folded, "Jebediah Kerman", 5);

            Assert.False(nowFolded);
            Assert.DoesNotContain("Jebediah Kerman", folded);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals fold toggled")
                && l.Contains("'Jebediah Kerman'")
                && l.Contains("-> unfolded")
                && l.Contains("(5 missions)"));
        }

        [Fact]
        public void InvalidateCache_DoesNotClearFoldedKerbals()
        {
            // Fold state is transient UI preference — it must survive cachedVM invalidation
            // (e.g. after a ledger recalc) so the user doesn't silently lose their fold
            // choices mid-session.
            var ui = new KerbalsWindowUI(null);
            ui.foldedKerbals.Add("Bill Kerman");
            ui.foldedKerbals.Add("Jebediah Kerman");

            ui.InvalidateCache();

            Assert.Contains("Bill Kerman", ui.foldedKerbals);
            Assert.Contains("Jebediah Kerman", ui.foldedKerbals);
        }

        // ──────────────────────────────────────────────────────────────────
        // OnFatesRowClicked (#416 Phase 4 — Fates → Timeline scroll companion)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void OnFatesRowClicked_InvokesCallbackWithRecordingId()
        {
            // Regression: fails if the hookup is wired to the wrong id (e.g. group id
            // instead of recording id).
            string captured = null;
            KerbalsWindowUI.OnFatesRowClicked(id => captured = id, "rec-42");
            Assert.Equal("rec-42", captured);
        }

        [Fact]
        public void OnFatesRowClicked_LogsRecordingId()
        {
            // Regression: fails silently if the click handler forgets to log.
            KerbalsWindowUI.OnFatesRowClicked(_ => { }, "rec-42");
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals Fates \u2192 Timeline scroll")
                && l.Contains("recordingId=rec-42"));
        }

        [Fact]
        public void OnFatesRowClicked_NullCallback_NoOpAndLogsOnce()
        {
            // Regression: E14 — the pure helper must not NRE when the production
            // callback (GetTimelineUI().ScrollToRecording) is missing, e.g. during
            // a cold-start scene transition.
            var ex = Record.Exception(() =>
                KerbalsWindowUI.OnFatesRowClicked(null, "rec-stale"));
            Assert.Null(ex);
            int matches = logLines.Count(l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals Fates \u2192 Timeline scroll")
                && l.Contains("recordingId=rec-stale"));
            Assert.Equal(1, matches);
        }

        [Fact]
        public void Build_EmitsVerboseLog_WithNewCounters()
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
            // Hanley is retired with no slot → orphan. Bill is retired AND in Jeb's chain →
            // counted under topology, not orphans.
            var retired = new List<string> { "Bill Kerman", "Hanley Kerman" };

            KerbalsWindowUI.Build(slots, reservations, retired,
                committedRecordings: null,
                ActiveChainIndexLike(reservations));

            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("KerbalsWindow: built VM")
                && l.Contains("topology=1")
                && l.Contains("orphans=1")
                && l.Contains("endStates=0"));
        }
    }
}
