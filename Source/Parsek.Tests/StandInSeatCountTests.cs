using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using KSP.UI.Screens;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// STAND-INS-EXCEED-THE-CREW-LIMIT (owner ruling 2026-09-26, option (a)): a held owner
    /// and the active stand-in Parsek generated for his seat count as ONE active kerbal in
    /// stock's <c>KerbalRoster.GetActiveCrewCount</c>. Cells for the pure seat decision, the
    /// tooltip sentence gate, the Harmony target, and the hire-cost argument flow.
    /// </summary>
    [Collection("Sequential")]
    public class StandInSeatCountTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly KerbalsModule priorKerbalsModule;

        public StandInSeatCountTests()
        {
            priorKerbalsModule = LedgerOrchestrator.Kerbals;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.SetKerbalsForTesting(priorKerbalsModule);
        }

        // ---------------- helpers ----------------

        private static KerbalsModule.KerbalSlot Slot(string owner, params string[] chain)
        {
            return new KerbalsModule.KerbalSlot
            {
                OwnerName = owner,
                OwnerTrait = "Pilot",
                Chain = new List<string>(chain)
            };
        }

        private static Func<string, bool> Set(params string[] names)
        {
            var set = new HashSet<string>(names, StringComparer.Ordinal);
            return n => n != null && set.Contains(n);
        }

        private static List<StandInSeatCount.SeatPair> Pairs(
            IEnumerable<KerbalsModule.KerbalSlot> slots, Func<string, bool> reserved, Func<string, bool> counted,
            out int count)
        {
            var pairs = new List<StandInSeatCount.SeatPair>();
            count = StandInSeatCount.CollectSeatSharingPairs(slots, reserved, counted, pairs);
            Assert.Equal(count, pairs.Count);
            return pairs;
        }

        // ---------------- stock's counting rule ----------------

        [Theory]
        [InlineData(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Available, true)]
        [InlineData(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Assigned, true)]
        [InlineData(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Missing, true)]
        [InlineData(ProtoCrewMember.KerbalType.Crew, ProtoCrewMember.RosterStatus.Dead, false)]
        [InlineData(ProtoCrewMember.KerbalType.Applicant, ProtoCrewMember.RosterStatus.Available, false)]
        [InlineData(ProtoCrewMember.KerbalType.Tourist, ProtoCrewMember.RosterStatus.Assigned, false)]
        [InlineData(ProtoCrewMember.KerbalType.Unowned, ProtoCrewMember.RosterStatus.Available, false)]
        public void IsCountedByStock_MirrorsGetActiveCrewCount(
            ProtoCrewMember.KerbalType type, ProtoCrewMember.RosterStatus status, bool expected)
        {
            Assert.Equal(expected, StandInSeatCount.IsCountedByStock(type, status));
        }

        // ---------------- the seat decision ----------------

        [Fact]
        public void OneHoldWithActiveStandIn_CountsAsOneSeat()
        {
            // The GUI-28 F9 shape: Bill held, Leoly standing in, both on the roster.
            var slots = new[] { Slot("Bill Kerman", "Leoly Kerman") };
            var pairs = Pairs(slots, Set("Bill Kerman"), Set("Bill Kerman", "Leoly Kerman"), out int n);

            Assert.Equal(1, n);
            Assert.Equal("Bill Kerman", pairs[0].Owner);
            Assert.Equal("Leoly Kerman", pairs[0].StandIn);
            Assert.Equal(5, StandInSeatCount.ApplySeatSharing(6, n));
        }

        [Fact]
        public void OwnerBackInHisSeat_DisplacedStandInStillCounts()
        {
            // Mirror direction: the hold ended (release UT passed / recovery), the owner is
            // the active occupant again, and the displaced or retired stand-in counts as
            // stock counts him.
            var slots = new[] { Slot("Bill Kerman", "Leoly Kerman") };
            Pairs(slots, Set(), Set("Bill Kerman", "Leoly Kerman"), out int n);

            Assert.Equal(0, n);
            Assert.Equal(6, StandInSeatCount.ApplySeatSharing(6, n));
        }

        [Fact]
        public void DeeperDisplacedMember_IsNotSubtracted()
        {
            // Jeb held -> Hanley active -> Kirrim displaced metadata (still on the roster).
            var slots = new[] { Slot("Jeb", "Hanley", "Kirrim") };
            var pairs = Pairs(slots, Set("Jeb"), Set("Jeb", "Hanley", "Kirrim"), out int n);

            Assert.Equal(1, n);
            Assert.Equal("Hanley", pairs[0].StandIn);
        }

        [Fact]
        public void OwnerPermanentlyGone_NoActiveOccupant_NotSubtracted()
        {
            var slot = Slot("Jeb", "Hanley");
            slot.OwnerPermanentlyGone = true;
            // Even if a stale roster status still counted Jeb, a slot with no active
            // occupant has no pair.
            Pairs(new[] { slot }, Set("Jeb"), Set("Jeb", "Hanley"), out int n);

            Assert.Equal(0, n);
        }

        [Fact]
        public void OwnerNotCountedByStock_NotSubtracted()
        {
            var slots = new[] { Slot("Jeb", "Hanley") };
            // Jeb absent from the counted set (Dead status, or not in the roster).
            Pairs(slots, Set("Jeb"), Set("Hanley"), out int n);
            Assert.Equal(0, n);
        }

        [Fact]
        public void StandInNotCountedByStock_OrPendingGeneration_NotSubtracted()
        {
            Pairs(new[] { Slot("Jeb", "Hanley") }, Set("Jeb"), Set("Jeb"), out int missing);
            Assert.Equal(0, missing);

            Pairs(new[] { Slot("Jeb", new string[] { null }) }, Set("Jeb"), Set("Jeb"), out int pending);
            Assert.Equal(0, pending);
        }

        [Fact]
        public void EveryChainMemberHeld_NoActiveStandIn_NotSubtracted()
        {
            var slots = new[] { Slot("Jeb", "Hanley") };
            Pairs(slots, Set("Jeb", "Hanley"), Set("Jeb", "Hanley"), out int n);
            Assert.Equal(0, n);
        }

        [Fact]
        public void NestedReservedStandIn_OwnsItsOwnSlot_EachPairCountedOnce()
        {
            // Jeb held; his depth-0 stand-in Debwig flew and is held too, so Zed is Jeb's
            // active stand-in and Debwig's own slot has Yul standing in. Two seats with a
            // hold each: stock's 4 read as 2 (Jeb+Zed, Debwig+Yul).
            var slots = new[] { Slot("Jeb", "Debwig", "Zed"), Slot("Debwig", "Yul") };
            var counted = Set("Jeb", "Debwig", "Zed", "Yul");
            var pairs = Pairs(slots, Set("Jeb", "Debwig"), counted, out int n);

            Assert.Equal(2, n);
            Assert.Contains(pairs, p => p.Owner == "Jeb" && p.StandIn == "Zed");
            Assert.Contains(pairs, p => p.Owner == "Debwig" && p.StandIn == "Yul");
            Assert.Equal(2, StandInSeatCount.ApplySeatSharing(4, n));
        }

        [Fact]
        public void ActiveStandInOfOneSlot_AndFreeOwnerOfAnother_NoDoubleSubtraction()
        {
            // Debwig stands in for Jeb and owns a slot of his own from an earlier hold that
            // has ended: he is in his own seat there, so only Jeb+Debwig collapses.
            var slots = new[] { Slot("Jeb", "Debwig"), Slot("Debwig", "Yul") };
            var pairs = Pairs(slots, Set("Jeb"), Set("Jeb", "Debwig", "Yul"), out int n);

            Assert.Equal(1, n);
            Assert.Equal("Debwig", pairs[0].StandIn);
            Assert.Equal(2, StandInSeatCount.ApplySeatSharing(3, n));
        }

        [Fact]
        public void OneKerbalListedActiveInTwoChains_IsPairedOnce()
        {
            // A damaged save listing Hanley in two chains must not subtract twice.
            var slots = new[] { Slot("Jeb", "Hanley"), Slot("Bill", "Hanley") };
            Pairs(slots, Set("Jeb", "Bill"), Set("Jeb", "Bill", "Hanley"), out int n);

            Assert.Equal(1, n);
            Assert.Equal(2, StandInSeatCount.ApplySeatSharing(3, n));
        }

        [Fact]
        public void MultipleIndependentHolds_OnePerSlot()
        {
            var slots = new[]
            {
                Slot("Jebediah Kerman", "Debwig Kerman"),
                Slot("Bill Kerman", "Leoly Kerman"),
                Slot("Bob Kerman", "Kirrim Kerman")
            };
            // Bob is back in his seat; Kirrim is displaced and counts.
            var counted = Set("Jebediah Kerman", "Debwig Kerman", "Bill Kerman", "Leoly Kerman",
                "Bob Kerman", "Kirrim Kerman", "Valentina Kerman");
            Pairs(slots, Set("Jebediah Kerman", "Bill Kerman"), counted, out int n);

            Assert.Equal(2, n);
            Assert.Equal(5, StandInSeatCount.ApplySeatSharing(7, n));
        }

        [Fact]
        public void ApplySeatSharing_NeverBelowZero_AndNoPairsIsStock()
        {
            Assert.Equal(0, StandInSeatCount.ApplySeatSharing(1, 3));
            Assert.Equal(4, StandInSeatCount.ApplySeatSharing(4, 0));
            Assert.Equal(4, StandInSeatCount.ApplySeatSharing(4, -1));
        }

        [Fact]
        public void NullInputs_NoPairs()
        {
            Assert.Equal(0, StandInSeatCount.CollectSeatSharingPairs(null, Set(), Set(), null));
            Assert.Equal(0, StandInSeatCount.CollectSeatSharingPairs(
                new[] { Slot("Jeb", "Hanley") }, Set("Jeb"), null, null));
            Assert.Equal(0, StandInSeatCount.CollectSeatSharingPairs(
                new KerbalsModule.KerbalSlot[] { null }, Set("Jeb"), Set("Jeb"), null));
        }

        [Fact]
        public void LiveModule_ActiveOccupantRule_DrivesThePair_AndReleaseRestoresStock()
        {
            // The real module's slots and IsReservedNow, the inputs the postfix gathers.
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotNode = parent.AddNode("KERBAL_SLOTS").AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            slotNode.AddNode("CHAIN_ENTRY").AddValue("name", "Hanley");
            slotNode.AddNode("CHAIN_ENTRY").AddValue("name", "Kirrim");
            module.LoadSlots(parent);
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("Ship", new[] { "Jeb" }, 2000));

            var kerbals = KerbalsTestHelper.RecalculateModule(module);
            var counted = Set("Jeb", "Hanley", "Kirrim");
            var pairs = Pairs(kerbals.Slots.Values, kerbals.IsReservedNow, counted, out int n);

            Assert.Equal(1, n);
            Assert.Equal("Hanley", pairs[0].StandIn);
            // The tooltip gate and the Kerbals window's "Stand-in for" agree.
            Assert.Equal(kerbals.FindActiveStandInOwner("Hanley"),
                StandInSeatCount.SeatSharedOwner("Hanley", kerbals.Slots.Values, kerbals.IsReservedNow, counted));

            // No committed flight holds Jeb any more: the count is stock's again.
            RecordingStore.ResetForTesting();
            kerbals = KerbalsTestHelper.RecalculateModule(module);
            Pairs(kerbals.Slots.Values, kerbals.IsReservedNow, counted, out int after);
            Assert.Equal(0, after);
        }

        [Fact]
        public void AdjustLiveCount_NoModuleOrRoster_KeepsStockCount()
        {
            LedgerOrchestrator.SetKerbalsForTesting(null);
            Assert.Equal(6, StandInSeatCount.AdjustLiveCount(null, 6));
        }

        [Fact]
        public void FormatCountLine_IsInvariant()
        {
            Assert.Equal("Active crew count: raw=6 seatSharingPairs=1 result=5",
                StandInSeatCount.FormatCountLine(6, 1, 5));
        }

        // ---------------- the tooltip sentence ----------------

        [Fact]
        public void SeatSharedOwner_OnlyForTheSubtractedStandIn()
        {
            var slots = new[] { Slot("Jeb", "Hanley", "Kirrim") };
            var reserved = Set("Jeb");
            var counted = Set("Jeb", "Hanley", "Kirrim");

            Assert.Equal("Jeb", StandInSeatCount.SeatSharedOwner("Hanley", slots, reserved, counted));
            Assert.Null(StandInSeatCount.SeatSharedOwner("Kirrim", slots, reserved, counted));
            Assert.Null(StandInSeatCount.SeatSharedOwner("Jeb", slots, reserved, counted));
            Assert.Null(StandInSeatCount.SeatSharedOwner(null, slots, reserved, counted));
            // Owner not counted by stock: no subtraction, so no sentence either.
            Assert.Null(StandInSeatCount.SeatSharedOwner("Hanley", slots, reserved, Set("Hanley")));
        }

        [Fact]
        public void AppendSeatSharedSentence_OneSentence_Idempotent()
        {
            string once = StandInSeatCount.AppendSeatSharedSentence("Refused.", "Bill Kerman");
            Assert.Equal("Refused. They share Bill Kerman's seat and do not count against the Astronaut Complex limit.", once);
            Assert.Equal(once, StandInSeatCount.AppendSeatSharedSentence(once, "Bill Kerman"));
            Assert.Equal("Refused.", StandInSeatCount.AppendSeatSharedSentence("Refused.", null));
            Assert.All(once, c => Assert.True(c < 128, "ASCII only"));
        }

        [Fact]
        public void AstronautComplex_ActiveStandIn_TooltipGainsTheSentence_OnlyWhenSubtracted()
        {
            string refusal = KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged);
            var rows = new[]
            {
                new StockUiItem("Leoly Kerman", "Available"),
                new StockUiItem("Kirrim Kerman", "Available"),
                new StockUiItem("Bob Kerman", "Available")
            };
            var context = new AstronautComplexContext
            {
                DismissalRefusal = n => n == "Bob Kerman" ? null : refusal,
                ActiveStandInOwner = n => n == "Leoly Kerman" || n == "Kirrim Kerman" ? "Bill Kerman" : null,
                // Leoly's pair is subtracted; Kirrim is an active stand-in whose owner stock
                // does not count, so his tooltip stays as it was.
                SeatSharedOwner = n => n == "Leoly Kerman" ? "Bill Kerman" : null,
                IsLoopingRecording = id => false
            };
            var d = StockUiDecorationQuery.ForAstronautComplex(CommittedFutureIndex.Empty, 100, rows, context,
                ut => "D1");

            var leoly = d.Single(x => x.Id == "Leoly Kerman");
            Assert.Equal(StockUiDecorationKind.KerbalStandIn, leoly.Kind);
            Assert.Equal(refusal + " " + StandInSeatCount.SeatSharedSentence("Bill Kerman"), leoly.Why);
            var row = StockUiAstronautDecoration.Decide(leoly, "Available", "Available", true, refusal);
            Assert.True(row.DisableButton);
            Assert.Equal(leoly.Why, row.DisabledCaption);

            var kirrim = d.Single(x => x.Id == "Kirrim Kerman");
            Assert.Equal(StockUiDecorationKind.KerbalStandIn, kirrim.Kind);
            Assert.Equal(refusal, kirrim.Why);
            Assert.Equal(refusal, StockUiAstronautDecoration.Decide(kirrim, "Available", "Available", true, refusal).DisabledCaption);

            Assert.Null(d.Single(x => x.Id == "Bob Kerman").Why);
        }

        [Fact]
        public void AstronautComplex_ReservedKerbal_NeverGetsTheSentence()
        {
            // A held kerbal reads his reservation, never the stand-in seat sentence, even
            // if a stale delegate answered for him.
            var reservation = new KerbalsModule.KerbalReservation { KerbalName = "Leoly Kerman", ReservedUntilUT = 13000 };
            var context = new AstronautComplexContext
            {
                ReservationKind = _ => KerbalReservationKind.ReservedActive,
                Reservation = _ => reservation,
                SlotOwner = _ => "Bill Kerman",
                DismissalRefusal = _ => "refused",
                ActiveStandInOwner = _ => "Bill Kerman",
                SeatSharedOwner = _ => "Bill Kerman",
                IsLoopingRecording = id => false
            };
            var d = StockUiDecorationQuery.ForAstronautComplex(CommittedFutureIndex.Empty, 100,
                new[] { new StockUiItem("Leoly Kerman", "Available") }, context, ut => "D1").Single();

            Assert.Equal(StockUiDecorationKind.KerbalOnFlight, d.Kind);
            Assert.DoesNotContain("do not count against", d.Why ?? "");
        }

        // ---------------- Harmony target + hire-cost flow ----------------

        [Fact]
        public void ActiveCrewCountPatch_TargetsStockGetActiveCrewCount()
        {
            var method = ActiveCrewCountPatch.ResolveTargetMethodForTesting();

            Assert.NotNull(method);
            Assert.Equal(typeof(KerbalRoster), method.DeclaringType);
            Assert.Equal("GetActiveCrewCount", method.Name);
            Assert.Empty(method.GetParameters());
            Assert.Equal(typeof(int), method.ReturnType);
            Assert.False(method.IsStatic);

            var postfix = typeof(ActiveCrewCountPatch).GetMethod("Postfix",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(postfix);
            var ps = postfix.GetParameters();
            Assert.Equal(2, ps.Length);
            Assert.Equal(typeof(KerbalRoster), ps[0].ParameterType);
            Assert.Equal("__result", ps[1].Name);
            Assert.Equal(typeof(int).MakeByRefType(), ps[1].ParameterType);
        }

        [Fact]
        public void HireCost_StockChargeAndRecordedHireCost_ReadThePatchedCount()
        {
            // Stock HireApplicant fires OnCrewmemberHired(ap, GetActiveCrewCount()): the
            // argument is the patched method's return value.
            var hire = ILCallSet.Method(typeof(KerbalRoster), "HireApplicant");
            Assert.True(ILCallSet.Calls(hire, typeof(KerbalRoster), "GetActiveCrewCount"));

            // Stock's charge prices from that argument, never a re-read of the count.
            var charge = ILCallSet.Method(typeof(Funding), "onCrewHired");
            Assert.True(ILCallSet.Calls(charge, typeof(GameVariables), "GetRecruitHireCost"));
            Assert.False(ILCallSet.Calls(charge, typeof(KerbalRoster), "GetActiveCrewCount"));

            // Parsek's recorded HireCost prices from the SAME argument (ldarg.2 =
            // activeCrewCount straight into ComputeHireCost), so the two agree.
            var recorder = ILCallSet.Method(typeof(GameStateRecorder), "OnCrewmemberHired");
            Assert.False(ILCallSet.Calls(recorder, typeof(KerbalRoster), "GetActiveCrewCount"));
            var body = HarmonyLib.PatchProcessor.ReadMethodBody(recorder).ToList();
            int call = body.FindIndex(i =>
                (i.Key == OpCodes.Call || i.Key == OpCodes.Callvirt)
                && i.Value is System.Reflection.MethodBase m && m.Name == nameof(GameStateRecorder.ComputeHireCost));
            Assert.True(call > 0, "OnCrewmemberHired must price through ComputeHireCost");
            Assert.Equal(OpCodes.Ldarg_2, body[call - 1].Key);
        }

        // Crew-end-state shape copied from KerbalReservationTests: a Recovered flight whose
        // crew stays aboard to the end.
        private static Recording MakeRecording(string vesselName, string[] crew, double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);
            var rec = new Recording
            {
                VesselName = vesselName,
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Recovered,
                ExplicitStartUT = 0,
                ExplicitEndUT = endUT,
            };
            var endCrewSet = new HashSet<string>(crew);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            for (int i = 0; i < crew.Length; i++)
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(crew[i], TerminalState.Recovered, endCrewSet);
            return rec;
        }
    }
}
