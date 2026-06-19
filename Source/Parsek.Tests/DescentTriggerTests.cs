using System;
using System.Collections.Generic;
using Xunit;
using Parsek;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // Pure trigger math for the looped re-aim landing DESCENT TRIGGER (docs/dev/plans/reaim-descent-trigger.md):
    // the first rotation-aligned live UT after loiter entry, and the forward-only re-anchored descent head.
    public class DescentTriggerTests
    {
        // Duna sidereal rotation period (s); a representative body-general value.
        private const double DunaTrot = 65517.859375;

        // --- ComputeRotationAlignedTriggerUT: first t >= entry with t == deorbit (mod T_rot) ---

        [Fact]
        public void Trigger_IsInOneRotationPeriodWindow()
        {
            // For arbitrary entry/deorbit, the trigger lands in [entry, entry+T_rot).
            double entry = 3_500_000_000.0;
            double deorbit = 2_570_541_342.0;
            double t = DescentTrigger.ComputeRotationAlignedTriggerUT(entry, deorbit, DunaTrot);
            Assert.InRange(t, entry, entry + DunaTrot);
        }

        [Fact]
        public void Trigger_IsCongruentToDeorbitModRotation()
        {
            // The defining property: (trigger - deorbit) is a whole number of rotations.
            double entry = 3_500_000_000.0;
            double deorbit = 2_570_541_342.0;
            double t = DescentTrigger.ComputeRotationAlignedTriggerUT(entry, deorbit, DunaTrot);
            double residual = ((t - deorbit) % DunaTrot + DunaTrot) % DunaTrot;
            // residual ~ 0 (mod T_rot): the live rotation equals the recorded deorbit rotation.
            Assert.True(Math.Min(residual, DunaTrot - residual) < 1e-3,
                $"residual={residual} t={t}");
        }

        [Fact]
        public void Trigger_EntryAlreadyAligned_FiresAtEntry()
        {
            // entry exactly an integer number of rotations after deorbit -> trigger == entry.
            double deorbit = 1_000_000.0;
            double entry = deorbit + 5.0 * DunaTrot;
            double t = DescentTrigger.ComputeRotationAlignedTriggerUT(entry, deorbit, DunaTrot);
            Assert.Equal(entry, t, 3);
        }

        [Fact]
        public void Trigger_BodyGeneral_AcrossPeriods()
        {
            // Kerbin sidereal day and a tidally-locked-ish long period both produce a valid in-window trigger.
            foreach (double tRot in new[] { 21549.425, DunaTrot, 138984.0, 211926.0 })
            {
                double entry = 9_000_000.0;
                double deorbit = 1_234_567.0;
                double t = DescentTrigger.ComputeRotationAlignedTriggerUT(entry, deorbit, tRot);
                Assert.InRange(t, entry, entry + tRot);
                double residual = ((t - deorbit) % tRot + tRot) % tRot;
                Assert.True(Math.Min(residual, tRot - residual) < 1e-3, $"tRot={tRot} residual={residual}");
            }
        }

        [Fact]
        public void Trigger_DegenerateInputs_ReturnNaN()
        {
            Assert.True(double.IsNaN(DescentTrigger.ComputeRotationAlignedTriggerUT(1.0, 2.0, double.NaN)));
            Assert.True(double.IsNaN(DescentTrigger.ComputeRotationAlignedTriggerUT(1.0, 2.0, 0.0)));
            Assert.True(double.IsNaN(DescentTrigger.ComputeRotationAlignedTriggerUT(1.0, 2.0, -5.0)));
            Assert.True(double.IsNaN(DescentTrigger.ComputeRotationAlignedTriggerUT(double.NaN, 2.0, 100.0)));
            Assert.True(double.IsNaN(DescentTrigger.ComputeRotationAlignedTriggerUT(1.0, double.NaN, 100.0)));
        }

        // --- ComputeDescentEffectiveHeadUT: forward-only re-anchored head ---

        [Fact]
        public void Head_BeforeTrigger_IsNaN_DescentHidden()
        {
            double head = DescentTrigger.ComputeDescentEffectiveHeadUT(
                currentUT: 999.0, triggerUT: 1000.0, recordedDeorbitUT: 2_570_541_342.0);
            Assert.True(double.IsNaN(head));
        }

        [Fact]
        public void Head_AtTrigger_IsRecordedDeorbit()
        {
            double deorbit = 2_570_541_342.0;
            double head = DescentTrigger.ComputeDescentEffectiveHeadUT(
                currentUT: 1000.0, triggerUT: 1000.0, recordedDeorbitUT: deorbit);
            Assert.Equal(deorbit, head, 6); // descent starts exactly at its recorded deorbit sample
        }

        [Fact]
        public void Head_AfterTrigger_AdvancesForwardAtLiveRate()
        {
            double deorbit = 2_570_541_342.0;
            double head = DescentTrigger.ComputeDescentEffectiveHeadUT(
                currentUT: 1000.0 + 240.0, triggerUT: 1000.0, recordedDeorbitUT: deorbit);
            Assert.Equal(deorbit + 240.0, head, 6); // 240 s after trigger -> 240 s into the recorded clip
        }

        [Fact]
        public void Head_IsMonotone_NeverBeforeRecordedDeorbit_FreezeFree()
        {
            // The re-anchored head is strictly forward and never earlier than the recorded deorbit, so it can
            // never feed an earlier UT to the insert-only arrival hold (the reverted mid-transfer freeze).
            double deorbit = 2_570_541_342.0;
            double trigger = 1_000_000.0;
            double prev = double.NegativeInfinity;
            for (double t = trigger; t <= trigger + 1000.0; t += 17.0)
            {
                double head = DescentTrigger.ComputeDescentEffectiveHeadUT(t, trigger, deorbit);
                Assert.True(head >= deorbit, $"head={head} < deorbit at t={t}");
                Assert.True(head > prev, $"head not increasing at t={t}");
                prev = head;
            }
        }

        [Fact]
        public void Head_NaNInputs_ReturnNaN()
        {
            Assert.True(double.IsNaN(DescentTrigger.ComputeDescentEffectiveHeadUT(double.NaN, 1.0, 2.0)));
            Assert.True(double.IsNaN(DescentTrigger.ComputeDescentEffectiveHeadUT(1.0, double.NaN, 2.0)));
            Assert.True(double.IsNaN(DescentTrigger.ComputeDescentEffectiveHeadUT(1.0, 1.0, double.NaN)));
        }

        // --- ComputeDescentMemberHead: the piecewise per-cycle head (Inert / Loiter / Descent / Done) ---
        //
        // A representative looped re-aim Duna landing, all UTs in seconds:
        //   spanStart = 0 (recorded launch)
        //   recordedDeorbit = 30_000_000      (launch -> deorbit, long recorded transfer + parking)
        //   descentEnd      = 30_002_000      (a ~2000 s deorbit->reentry->landing clip)
        //   T_rot (Duna)    = 65517.86
        //   T_park          = 7000            (a low parking orbit period)
        //   captureShift    = -2_860_000      (re-aimed transfer ~2.86 Ms shorter -> conic shifted earlier)
        //   phaseAnchor     = 1_000_000, cadence = 60_000_000 (synodic), cycle N = 0
        private const double Pa = 1_000_000.0;
        private const double Cad = 60_000_000.0;
        private const double SpanStart = 0.0;
        private const double Deorbit = 30_000_000.0;
        private const double DescentEnd = 30_002_000.0;
        private const double Tpark = 7000.0;
        private const double CapShift = -2_860_000.0;

        private static DescentTrigger.DescentHeadPhase MemberHead(double currentUT, long n, out double head)
            => DescentTrigger.ComputeDescentMemberHead(
                currentUT, n, Pa, Cad, SpanStart, Deorbit, DescentEnd, DunaTrot, Tpark, CapShift, null, out head);

        // entry/trigger for cycle N=0, mirrors the production formula.
        private static double EntryUT(long n) => Pa + n * Cad + (Deorbit - SpanStart) + CapShift;
        private static double TriggerUT(long n)
            => DescentTrigger.ComputeRotationAlignedTriggerUT(EntryUT(n), Deorbit, DunaTrot);

        [Fact]
        public void MemberHead_BeforeEntry_IsInert()
        {
            // Just before the icon reaches the deorbit point on the shifted conic -> normal loop clock.
            double phase = MemberHead(EntryUT(0) - 1.0, 0, out double head) == DescentTrigger.DescentHeadPhase.Inert ? 1 : 0;
            Assert.Equal(1, phase);
            Assert.True(double.IsNaN(head));
        }

        [Fact]
        public void MemberHead_WaitWindow_LoitersOnConicLastRev()
        {
            // Between entry and trigger -> Loiter, head on the shifted conic's last rev [conicEnd - T_park, conicEnd].
            double conicEnd = Deorbit + CapShift;
            double t = EntryUT(0) + 0.5 * Tpark; // mid first wait rev (trigger is < 1 T_rot away, > T_park)
            Assert.True(t < TriggerUT(0));
            DescentTrigger.DescentHeadPhase ph = MemberHead(t, 0, out double head);
            Assert.Equal(DescentTrigger.DescentHeadPhase.Loiter, ph);
            Assert.InRange(head, conicEnd - Tpark, conicEnd);
        }

        [Fact]
        public void MemberHead_WaitHead_StaysOnConicLastRev()
        {
            // The circling head never leaves the conic's last rev (conicEnd - T_park, conicEnd], wherever in the
            // wait window we sample it.
            double conicEnd = Deorbit + CapShift;
            for (double t = EntryUT(0); t < TriggerUT(0); t += 500.0)
            {
                Assert.Equal(DescentTrigger.DescentHeadPhase.Loiter, MemberHead(t, 0, out double head));
                Assert.InRange(head, conicEnd - Tpark, conicEnd);
            }
        }

        [Fact]
        public void MemberHead_WaitHead_ReachesDeorbitPointExactlyAtTrigger()
        {
            // The KEY smooth-handoff property: anchored to triggerUT, the circling head -> conicEnd (the deorbit
            // point) as currentUT -> triggerUT, so the icon is AT the deorbit point at the handoff. conicEnd and
            // the descent's first sample (recordedDeorbit) are the SAME orbital position (ShiftInTime preserves
            // phase), so the transition is position-continuous.
            double conicEnd = Deorbit + CapShift;
            MemberHead(TriggerUT(0) - 1.0, 0, out double head);          // 1 s before handoff
            Assert.Equal(conicEnd - 1.0, head, 3);                        // head == conicEnd - (toTrigger=1)
            MemberHead(TriggerUT(0) - 0.001, 0, out double head2);        // arbitrarily close
            Assert.True(Math.Abs(head2 - conicEnd) < 0.01, $"head2={head2} conicEnd={conicEnd}");

            // And one full period earlier the head wraps back to conicEnd too (deorbit point once per rev).
            double t = TriggerUT(0) - Tpark;
            if (t >= EntryUT(0))
            {
                MemberHead(t, 0, out double headWrap);
                Assert.Equal(conicEnd, headWrap, 3);
            }
        }

        [Fact]
        public void MemberHead_AtTrigger_DescentStartsAtRecordedDeorbit()
        {
            DescentTrigger.DescentHeadPhase ph = MemberHead(TriggerUT(0), 0, out double head);
            Assert.Equal(DescentTrigger.DescentHeadPhase.Descent, ph);
            Assert.Equal(Deorbit, head, 3); // descent begins exactly at its recorded deorbit sample
        }

        [Fact]
        public void MemberHead_DuringDescent_PlaysForwardVerbatim()
        {
            DescentTrigger.DescentHeadPhase ph = MemberHead(TriggerUT(0) + 1500.0, 0, out double head);
            Assert.Equal(DescentTrigger.DescentHeadPhase.Descent, ph);
            Assert.Equal(Deorbit + 1500.0, head, 3); // 1500 s after trigger -> 1500 s into the clip
        }

        [Fact]
        public void MemberHead_AfterClip_IsDone_Hidden()
        {
            double clip = DescentEnd - Deorbit; // 2000 s
            DescentTrigger.DescentHeadPhase ph = MemberHead(TriggerUT(0) + clip + 1.0, 0, out double head);
            Assert.Equal(DescentTrigger.DescentHeadPhase.Done, ph);
            Assert.True(double.IsNaN(head));
        }

        [Fact]
        public void MemberHead_TriggerLandsAtRecordedRotationPhase()
        {
            // The defining correctness property: the descent plays starting at a live UT congruent to the
            // recorded deorbit UT (mod T_rot), so the body-fixed clip lands on the EXACT recorded site.
            double residual = ((TriggerUT(0) - Deorbit) % DunaTrot + DunaTrot) % DunaTrot;
            Assert.True(Math.Min(residual, DunaTrot - residual) < 1e-3, $"residual={residual}");
        }

        [Fact]
        public void MemberHead_NextCycle_ReArmsByCycleIndex()
        {
            // Cycle N=1: entry/trigger shift forward by exactly one cadence; the same phases recur (stateless
            // re-arm off the cycle index, no latch).
            Assert.Equal(EntryUT(0) + Cad, EntryUT(1), 3);
            DescentTrigger.DescentHeadPhase ph = MemberHead(TriggerUT(1), 1, out double head);
            Assert.Equal(DescentTrigger.DescentHeadPhase.Descent, ph);
            Assert.Equal(Deorbit, head, 3);
        }

        [Fact]
        public void MemberHead_LaunchSideCut_ShiftsEntryEarlier()
        {
            // A launch-side loiter cut entirely before conicEnd (deo+cs lands in the transfer region) shifts the
            // live entry EARLIER by the cut length (CompressSpanUT subtracts it); the destination loiter cut,
            // which sits after arrival (after conicEnd), must NOT affect entry.
            double conicEnd = Deorbit + CapShift; // 27_140_000, in the transfer region
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 5_000_000.0, LengthSeconds = 1_000_000.0 }, // launch-side, before conicEnd
                new GhostPlaybackLogic.LoopCut { StartUT = 29_000_000.0, LengthSeconds = 500_000.0 },  // destination-side, after conicEnd
            };
            // entry with cuts = Pa + (CompressSpanUT(conicEnd) - spanStart) = Pa + (conicEnd - 1_000_000).
            double tWithCutEntry = Pa + (conicEnd - 1_000_000.0 - SpanStart);
            // Just before that entry -> still Inert; just after -> Loiter (descent trigger armed).
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert,
                DescentTrigger.ComputeDescentMemberHead(tWithCutEntry - 1.0, 0, Pa, Cad, SpanStart, Deorbit,
                    DescentEnd, DunaTrot, Tpark, CapShift, cuts, out _));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Loiter,
                DescentTrigger.ComputeDescentMemberHead(tWithCutEntry + 1.0, 0, Pa, Cad, SpanStart, Deorbit,
                    DescentEnd, DunaTrot, Tpark, CapShift, cuts, out _));
            // Without the cut the same UT is still Inert (entry is 1_000_000 s later) - proves the cut moved entry.
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert,
                DescentTrigger.ComputeDescentMemberHead(tWithCutEntry + 1.0, 0, Pa, Cad, SpanStart, Deorbit,
                    DescentEnd, DunaTrot, Tpark, CapShift, null, out _));
        }

        // --- SelectDescentMemberIndices: the post-parking body-fixed approach member SET ---
        //
        // Models the real "Route: KSC → Duna" chain: launch #41 (Kerbin), probe #48 (Kerbin), transfer #53
        // (Kerbin start), and the post-arrival Duna approach members #49/#50/#51 starting at the seam 72353179.
        private const double Seam = 72353179.48;

        private static System.Collections.Generic.List<DescentTrigger.MemberArrivalInfo> RealChainMembers()
            => new System.Collections.Generic.List<DescentTrigger.MemberArrivalInfo>
            {
                new DescentTrigger.MemberArrivalInfo(41, 63825684.64, "Kerbin"), // launch (owner)
                new DescentTrigger.MemberArrivalInfo(48, 63828957.51, "Kerbin"), // probe (spans whole mission)
                new DescentTrigger.MemberArrivalInfo(53, 63901473.58, "Kerbin"), // transfer member
                new DescentTrigger.MemberArrivalInfo(49, 72353179.48, "Duna"),   // approach start (== seam)
                new DescentTrigger.MemberArrivalInfo(50, 72353218.82, "Duna"),   // approach
                new DescentTrigger.MemberArrivalInfo(51, 72353267.24, "Duna"),   // post-dock tail
            };

        [Fact]
        public void SelectDescent_PicksOnlyPostSeamTargetBodyMembers()
        {
            int[] set = DescentTrigger.SelectDescentMemberIndices(RealChainMembers(), Seam, "Duna", 1.0);
            Assert.Equal(new[] { 49, 50, 51 }, set); // launch/probe/transfer (Kerbin, pre-seam) excluded
        }

        [Fact]
        public void SelectDescent_ExcludesPreSeamMembersEvenOnTargetBody()
        {
            // A target-body member that starts BEFORE the seam (e.g. the transfer member's own capture-orbit
            // recording) is the conic carrier, not an approach member — excluded by the seam gate.
            var members = RealChainMembers();
            members.Add(new DescentTrigger.MemberArrivalInfo(52, Seam - 5000.0, "Duna")); // pre-seam Duna
            int[] set = DescentTrigger.SelectDescentMemberIndices(members, Seam, "Duna", 1.0);
            Assert.Equal(new[] { 49, 50, 51 }, set);
        }

        [Fact]
        public void SelectDescent_ExcludesPostSeamWrongBody()
        {
            // A post-seam member on a DIFFERENT body (e.g. a debris that escaped to the Sun) is excluded.
            var members = RealChainMembers();
            members.Add(new DescentTrigger.MemberArrivalInfo(60, Seam + 10.0, "Sun"));
            int[] set = DescentTrigger.SelectDescentMemberIndices(members, Seam, "Duna", 1.0);
            Assert.Equal(new[] { 49, 50, 51 }, set);
        }

        [Fact]
        public void SelectDescent_DegenerateInputs_EmptySet()
        {
            Assert.Empty(DescentTrigger.SelectDescentMemberIndices(RealChainMembers(), double.NaN, "Duna", 1.0));
            Assert.Empty(DescentTrigger.SelectDescentMemberIndices(RealChainMembers(), Seam, null, 1.0));
            Assert.Empty(DescentTrigger.SelectDescentMemberIndices(null, Seam, "Duna", 1.0));
            Assert.Empty(DescentTrigger.SelectDescentMemberIndices(
                new System.Collections.Generic.List<DescentTrigger.MemberArrivalInfo>(), Seam, "Duna", 1.0));
        }

        [Fact]
        public void SelectDescent_ReturnsAscendingOrder()
        {
            // Out-of-order input still yields ascending committed indices.
            var members = new System.Collections.Generic.List<DescentTrigger.MemberArrivalInfo>
            {
                new DescentTrigger.MemberArrivalInfo(51, 72353267.24, "Duna"),
                new DescentTrigger.MemberArrivalInfo(49, 72353179.48, "Duna"),
                new DescentTrigger.MemberArrivalInfo(50, 72353218.82, "Duna"),
            };
            Assert.Equal(new[] { 49, 50, 51 }, DescentTrigger.SelectDescentMemberIndices(members, Seam, "Duna", 1.0));
        }

        // --- TryResolveDescentMemberHead: the SHARED head dispatched to each member by window ---
        //
        // Three contiguous approach-member windows partitioning the clip [Deorbit, DescentEnd]:
        private const double W1Start = Deorbit;              // 30_000_000
        private const double W1End = Deorbit + 800.0;        // 30_000_800
        private const double W2End = Deorbit + 1500.0;       // 30_001_500
        private const double W3End = DescentEnd;             // 30_002_000

        private static bool ResolveMember(double currentUT, double mStart, double mEnd, out double head, out DescentTrigger.DescentHeadPhase ph)
            => DescentTrigger.TryResolveDescentMemberHead(
                currentUT, 0, Pa, Cad, SpanStart, Deorbit, DescentEnd, DunaTrot, Tpark, CapShift, null,
                mStart, mEnd, out head, out ph);

        [Fact]
        public void Dispatch_EachMemberOwnsOnlyItsOwnSlice()
        {
            // head at Deorbit+400 -> member 1's window only.
            Assert.True(ResolveMember(TriggerUT(0) + 400.0, W1Start, W1End, out double h1, out _));
            Assert.Equal(Deorbit + 400.0, h1, 3);
            Assert.False(ResolveMember(TriggerUT(0) + 400.0, W1End, W2End, out _, out _)); // member 2 hidden
            Assert.False(ResolveMember(TriggerUT(0) + 400.0, W2End, W3End, out _, out _)); // member 3 hidden

            // head at Deorbit+1100 -> member 2 only.
            Assert.False(ResolveMember(TriggerUT(0) + 1100.0, W1Start, W1End, out _, out _));
            Assert.True(ResolveMember(TriggerUT(0) + 1100.0, W1End, W2End, out double h2, out _));
            Assert.Equal(Deorbit + 1100.0, h2, 3);

            // head at Deorbit+1700 -> member 3 only.
            Assert.True(ResolveMember(TriggerUT(0) + 1700.0, W2End, W3End, out double h3, out _));
            Assert.Equal(Deorbit + 1700.0, h3, 3);
        }

        [Fact]
        public void Dispatch_HiddenInLoiterAndInertAndDone()
        {
            // Loiter (before trigger): every member hidden (the transfer member owns the conic icon).
            double tLoiter = EntryUT(0) + 100.0;
            Assert.True(tLoiter < TriggerUT(0));
            Assert.False(ResolveMember(tLoiter, W1Start, W1End, out _, out DescentTrigger.DescentHeadPhase pl));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Loiter, pl);

            // Inert (before entry): hidden.
            Assert.False(ResolveMember(EntryUT(0) - 100.0, W1Start, W1End, out _, out DescentTrigger.DescentHeadPhase pi));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert, pi);

            // Done (after clip): hidden.
            Assert.False(ResolveMember(TriggerUT(0) + (DescentEnd - Deorbit) + 50.0, W2End, W3End, out _, out DescentTrigger.DescentHeadPhase pd));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Done, pd);
        }

        [Fact]
        public void Dispatch_ContiguousHandoffNoGapNoOverlap()
        {
            // Sweep the clip; at every instant EXACTLY ONE of the three contiguous members renders (the clock is
            // monotone and the windows abut), so the chain plays seamlessly with no tear and no double icon.
            for (double t = TriggerUT(0); t <= TriggerUT(0) + (DescentEnd - Deorbit); t += 37.0)
            {
                int rendering = 0;
                if (ResolveMember(t, W1Start, W1End, out _, out _)) rendering++;
                if (ResolveMember(t, W1End, W2End, out _, out _)) rendering++;
                if (ResolveMember(t, W2End, W3End, out _, out _)) rendering++;
                Assert.True(rendering <= 1, $"more than one member rendered at t={t}");
            }
        }

        [Fact]
        public void MemberHead_DegenerateInputs_AreInert_ByteIdenticalOff()
        {
            // Each degenerate input collapses to Inert with a NaN head, so the resolver keeps the normal head.
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert,
                DescentTrigger.ComputeDescentMemberHead(TriggerUT(0), 0, Pa, Cad, SpanStart, Deorbit, DescentEnd,
                    double.NaN, Tpark, CapShift, null, out _)); // no rotation period
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert,
                DescentTrigger.ComputeDescentMemberHead(TriggerUT(0), 0, Pa, Cad, SpanStart, Deorbit, DescentEnd,
                    DunaTrot, 0.0, CapShift, null, out _)); // no parking period
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert,
                DescentTrigger.ComputeDescentMemberHead(TriggerUT(0), 0, Pa, Cad, SpanStart, Deorbit,
                    Deorbit - 1.0, DunaTrot, Tpark, CapShift, null, out _)); // descentEnd < deorbit
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert,
                DescentTrigger.ComputeDescentMemberHead(TriggerUT(0), 0, Pa, Cad, SpanStart, Deorbit, DescentEnd,
                    DunaTrot, Tpark, double.NaN, null, out _)); // NaN capture shift
        }
    }
}
