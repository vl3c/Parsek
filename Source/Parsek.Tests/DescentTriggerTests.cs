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

        // --- TryComputeTransferDeorbitHead: the transfer member's re-anchored deorbit-tail head (I1) ---
        //
        // I1: the deorbit/approach legs DOWN TO the seam live inside the transfer member (not the descent set),
        // so they must be swept by a pre-seam re-anchored head during the Loiter phase so the line draws
        // continuous loiter -> deorbit -> entry -> surface.

        private static bool TransferDeorbitHead(double currentUT, long n, out double head)
            => DescentTrigger.TryComputeTransferDeorbitHead(
                currentUT, n, Pa, Cad, SpanStart, Deorbit, DescentEnd, DunaTrot, Tpark, CapShift, null, out head);

        [Fact]
        public void TransferDeorbitHead_Inert_ReturnsFalse_NaN()
        {
            // Before the icon reaches the deorbit point (Inert) there is no re-anchored deorbit head.
            Assert.False(TransferDeorbitHead(EntryUT(0) - 1.0, 0, out double head));
            Assert.True(double.IsNaN(head));
        }

        [Fact]
        public void TransferDeorbitHead_Loiter_HeadBelowSeam()
        {
            // During Loiter (entry <= t < trigger) the deorbit head is BELOW the seam (recordedDeorbit) - it
            // sweeps the PRE-seam deorbit legs - and increases toward the seam as currentUT -> triggerUT.
            double tEarly = EntryUT(0) + 100.0;
            double tLate = TriggerUT(0) - 100.0;
            Assert.True(tEarly < TriggerUT(0) && tLate < TriggerUT(0));
            Assert.True(TransferDeorbitHead(tEarly, 0, out double hEarly));
            Assert.True(TransferDeorbitHead(tLate, 0, out double hLate));
            Assert.True(hEarly < Deorbit, $"hEarly={hEarly} not below seam {Deorbit}");
            Assert.True(hLate < Deorbit, $"hLate={hLate} not below seam {Deorbit}");
            Assert.True(hLate > hEarly, "deorbit head must rise toward the seam over the loiter");
        }

        [Fact]
        public void TransferDeorbitHead_AtTriggerMinus_ApproachesSeam()
        {
            // As currentUT -> triggerUT the deorbit head -> recordedDeorbit (= seam), the SAME value the descent
            // member's first head takes at the trigger (ComputeDescentEffectiveHeadUT(trigger,trigger,deorbit) ==
            // deorbit), so the deorbit-tail legs join the entry leg with seam continuity.
            Assert.True(TransferDeorbitHead(TriggerUT(0) - 0.001, 0, out double head));
            Assert.True(Math.Abs(head - Deorbit) < 0.01, $"head={head} seam={Deorbit}");
            double descentFirstHead = DescentTrigger.ComputeDescentEffectiveHeadUT(TriggerUT(0), TriggerUT(0), Deorbit);
            Assert.Equal(Deorbit, descentFirstHead, 6); // continuity target
        }

        [Fact]
        public void TransferDeorbitHead_Descent_ReturnsFalse()
        {
            // During Descent the transfer member is HIDDEN by the handoff and the descent member owns the entry
            // leg, so the pre-seam deorbit head must NOT activate (it would be unreachable + unnecessary).
            Assert.False(TransferDeorbitHead(TriggerUT(0) + 500.0, 0, out double head));
            Assert.True(double.IsNaN(head));
        }

        [Fact]
        public void TransferDeorbitHead_Done_ReturnsFalse()
        {
            double clip = DescentEnd - Deorbit;
            Assert.False(TransferDeorbitHead(TriggerUT(0) + clip + 1.0, 0, out double head));
            Assert.True(double.IsNaN(head));
        }

        [Fact]
        public void TransferDeorbitHead_Degenerate_ReturnsFalse()
        {
            // Any degenerate input collapses to Inert via ComputeDescentMemberHead -> false (byte-identical-off).
            Assert.False(DescentTrigger.TryComputeTransferDeorbitHead(
                TriggerUT(0) - 100.0, 0, Pa, Cad, SpanStart, Deorbit, DescentEnd,
                double.NaN, Tpark, CapShift, null, out _)); // no rotation period
            Assert.False(DescentTrigger.TryComputeTransferDeorbitHead(
                TriggerUT(0) - 100.0, 0, Pa, Cad, SpanStart, Deorbit, DescentEnd,
                DunaTrot, 0.0, CapShift, null, out _)); // no parking period
            Assert.False(DescentTrigger.TryComputeTransferDeorbitHead(
                TriggerUT(0) - 100.0, 0, Pa, Cad, SpanStart, Deorbit, Deorbit - 1.0,
                DunaTrot, Tpark, CapShift, null, out _)); // descentEnd < deorbit
            Assert.False(DescentTrigger.TryComputeTransferDeorbitHead(
                TriggerUT(0) - 100.0, 0, Pa, Cad, SpanStart, Deorbit, DescentEnd,
                DunaTrot, Tpark, double.NaN, null, out _)); // NaN capture shift
        }

        // --- ResolveTransferLegHeadUT: per-leg head pairing (deorbit-tail uses deorbitHead, others loop head) ---

        [Fact]
        public void ResolveTransferLegHeadUT_DeorbitTailUsesDeorbitHead_OthersUseLoopHead()
        {
            const double seam = Deorbit;
            const double eps = 1.0;
            const double loopHead = 27_000_000.0;
            const double deorbitHead = 29_500_000.0;

            // A deorbit-tail leg (eligible) whose end is at/below the seam -> uses deorbitHead.
            Assert.Equal(deorbitHead,
                DescentTrigger.ResolveTransferLegHeadUT(seam, seam, eps, loopHead, deorbitHead, true), 6);
            Assert.Equal(deorbitHead,
                DescentTrigger.ResolveTransferLegHeadUT(seam - 5000.0, seam, eps, loopHead, deorbitHead, true), 6);

            // A leg ending ABOVE the seam (beyond eps) -> loop head even when eligible.
            Assert.Equal(loopHead,
                DescentTrigger.ResolveTransferLegHeadUT(seam + 10.0, seam, eps, loopHead, deorbitHead, true), 6);

            // Not eligible (no deorbit head, or not a deorbit-tail leg) -> always loop head (byte-identical-off).
            Assert.Equal(loopHead,
                DescentTrigger.ResolveTransferLegHeadUT(seam, seam, eps, loopHead, deorbitHead, false), 6);
            Assert.Equal(loopHead,
                DescentTrigger.ResolveTransferLegHeadUT(seam - 5000.0, seam, eps, loopHead, deorbitHead, false), 6);
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
        public void Dispatch_ContiguousClip_ExactlyOneMemberCoversEveryInstant()
        {
            // FULL COVERAGE (review m1): sweep the whole clip at non-boundary samples (37 s never lands exactly on
            // a window seam) and assert EXACTLY ONE of the three contiguous members renders at every instant -
            // catching both a coverage HOLE (rendering 0 = blackout) and an off-seam double (rendering 2). The
            // chain plays seamlessly with no tear. (Exact-seam double is covered separately below.)
            for (double t = TriggerUT(0); t <= TriggerUT(0) + (DescentEnd - Deorbit); t += 37.0)
            {
                int rendering = 0;
                if (ResolveMember(t, W1Start, W1End, out _, out _)) rendering++;
                if (ResolveMember(t, W1End, W2End, out _, out _)) rendering++;
                if (ResolveMember(t, W2End, W3End, out _, out _)) rendering++;
                Assert.True(rendering == 1, $"expected exactly one member at t={t}, got {rendering}");
            }
        }

        [Fact]
        public void Dispatch_ExactSeam_RendersAtLeastOne_DoubleIsAcceptedSamePosition()
        {
            // At an EXACT shared seam (head == W1End) the doubly-inclusive epsilon lets BOTH adjacent members
            // match - an accepted, harmless same-position frame (review m2). The invariant that matters is NO
            // BLACKOUT: at least one renders, and both return the same head value (same world position).
            double tSeam = TriggerUT(0) + (W1End - Deorbit);
            bool a = ResolveMember(tSeam, W1Start, W1End, out double ha, out _);
            bool b = ResolveMember(tSeam, W1End, W2End, out double hb, out _);
            Assert.True(a || b, "the seam must be covered by at least one member (no blackout)");
            if (a && b) Assert.Equal(ha, hb, 6); // both at the same head -> same position -> reads as one icon
        }

        [Fact]
        public void Dispatch_RealGap_HidesDuringTheGap()
        {
            // If the member windows are NON-contiguous (the builder declines to engage on this, review B1, but the
            // dispatch must still fail safe), the shared head in the gap renders NO member - a hidden frame, never
            // a wrong-position render. Windows: [W1Start,W1End], GAP, [W1End+300, W3End].
            double t = TriggerUT(0) + (W1End + 150.0 - Deorbit); // head = W1End + 150, inside the 300 s gap
            Assert.False(ResolveMember(t, W1Start, W1End, out _, out _));
            Assert.False(ResolveMember(t, W1End + 300.0, W3End, out _, out _));
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

        // --- EvaluateEngage: the build-time engage/decline safety gate (extracted from MissionLoopUnitBuilder) ---

        private const double EngEps = 1.0;

        private static GhostPlaybackLogic.LoopUnit.MemberWindow MW(double s, double e)
            => new GhostPlaybackLogic.LoopUnit.MemberWindow(s, e);

        // A contiguous descent set tiling [Deorbit(seam), DescentEnd], first window beginning at the seam.
        private static List<GhostPlaybackLogic.LoopUnit.MemberWindow> ContiguousWindows()
            => new List<GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                MW(Deorbit, Deorbit + 800.0),
                MW(Deorbit + 800.0, Deorbit + 1500.0),
                MW(Deorbit + 1500.0, DescentEnd),
            };

        private static DescentTrigger.DescentEngageDecision Eval(
            List<GhostPlaybackLogic.LoopUnit.MemberWindow> w, double seam, double capShift,
            double trot, double soiExit, bool found = true)
            => DescentTrigger.EvaluateEngage(found, w, w.Count, seam, capShift, trot, soiExit, EngEps);

        [Fact]
        public void Engage_AllGatesPass_Engages()
        {
            var d = Eval(ContiguousWindows(), Deorbit, CapShift, DunaTrot, double.NaN);
            Assert.True(d.Engage);
            Assert.True(d.Contiguous && d.StartMatchesSeam && d.ConicInRegion);
            Assert.Equal(DescentEnd, d.SetEndUT, 3);
            Assert.Equal(Deorbit, d.SetMinStartUT, 3);
        }

        [Fact]
        public void Engage_GapBetweenWindows_Declines()
        {
            var w = new List<GhostPlaybackLogic.LoopUnit.MemberWindow>
            { MW(Deorbit, Deorbit + 800.0), MW(Deorbit + 1500.0, DescentEnd) }; // 800 -> 1500 gap > eps
            var d = Eval(w, Deorbit, CapShift, DunaTrot, double.NaN);
            Assert.False(d.Contiguous);
            Assert.False(d.Engage);
        }

        [Fact]
        public void Engage_FirstWindowAfterSeam_Declines()
        {
            var w = new List<GhostPlaybackLogic.LoopUnit.MemberWindow>
            { MW(Deorbit + 50.0, Deorbit + 800.0), MW(Deorbit + 800.0, DescentEnd) }; // starts 50s after seam
            var d = Eval(w, Deorbit, CapShift, DunaTrot, double.NaN);
            Assert.False(d.StartMatchesSeam);
            Assert.False(d.Engage);
        }

        [Fact]
        public void Engage_ConicBeforeSoiExit_Declines()
        {
            // conicEnd = seam + captureShift; put the SOI exit just AFTER it so conicInRegion is false.
            double soiExit = Deorbit + CapShift + 1000.0;
            var d = Eval(ContiguousWindows(), Deorbit, CapShift, DunaTrot, soiExit);
            Assert.False(d.ConicInRegion);
            Assert.False(d.Engage);
        }

        [Fact]
        public void Engage_ConicAtOrAfterSoiExit_Engages()
        {
            double soiExit = Deorbit + CapShift - 1000.0; // conicEnd >= soiExit
            var d = Eval(ContiguousWindows(), Deorbit, CapShift, DunaTrot, soiExit);
            Assert.True(d.ConicInRegion);
            Assert.True(d.Engage);
        }

        [Fact]
        public void Engage_WindowCountMismatch_Declines()
        {
            var w = ContiguousWindows(); // 3 windows, but claim 4 members -> count mismatch
            var d = DescentTrigger.EvaluateEngage(true, w, w.Count + 1, Deorbit, CapShift, DunaTrot, double.NaN, EngEps);
            Assert.False(d.Contiguous);
            Assert.False(d.Engage);
        }

        [Fact]
        public void Engage_DegenerateInputs_Decline()
        {
            var w = ContiguousWindows();
            Assert.False(Eval(w, Deorbit, CapShift, DunaTrot, double.NaN, found: false).Engage); // run not found
            Assert.False(Eval(w, Deorbit, 100.0, DunaTrot, double.NaN).Engage);                  // positive captureShift
            Assert.False(Eval(w, Deorbit, double.NaN, DunaTrot, double.NaN).Engage);             // NaN captureShift
            Assert.False(Eval(w, Deorbit, CapShift, 0.0, double.NaN).Engage);                    // non-positive rotation
            Assert.False(Eval(w, Deorbit, CapShift, double.PositiveInfinity, double.NaN).Engage); // infinite rotation
            Assert.False(Eval(w, double.NaN, CapShift, DunaTrot, double.NaN).Engage);            // NaN seam
            var empty = new List<GhostPlaybackLogic.LoopUnit.MemberWindow>();
            Assert.False(DescentTrigger.EvaluateEngage(true, empty, 0, Deorbit, CapShift, DunaTrot, double.NaN, EngEps).Engage);
        }

        // --- Resolver integration: GhostPlaybackLogic.ResolveTrackingStationSampleUT / …Frame (review M1) ---
        //
        // Builds a re-aim LoopUnit (IsReaim true) with a 3-member descent set {49,50,51} partitioning
        // [Deorbit, DescentEnd], plus a non-descent owner member 0. Drives the actual resolver seam — the
        // per-phase renderHidden/UT, the SpanClockUnresolved early-out, byte-identical-off for a sibling member,
        // and the R6 secondary suppression — none of which the pure-helper tests exercise.
        private static GhostPlaybackLogic.LoopUnitSet BuildDescentUnit(bool engage)
            => BuildDescentUnit(engage, overlapDescentWindows: false);

        // overlapDescentWindows: make the three descent members 49/50/51 ALL span [W1Start, W3End] (so the same
        // Descent-phase head is "in window" for all three at once) - models the real mission's shared surface
        // window (rec 42 lander-clip + rec 43 EVA landing) used to verify the flight icon-carrier tie-break picks
        // exactly ONE member (the highest index), so no double icon.
        private static GhostPlaybackLogic.LoopUnitSet BuildDescentUnit(bool engage, bool overlapDescentWindows)
        {
            var windows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                { 0, new GhostPlaybackLogic.LoopUnit.MemberWindow(0.0, 500.0) },        // owner (non-descent, span start)
                // Transfer/loiter member (non-descent): its window covers the shifted-parking region the icon rides
                // during the loiter (conicEnd = Deorbit + CapShift = 27,140,000 .. the seam Deorbit), so it RENDERS
                // both while loitering AND while the descent plays — the double-icon the handoff hide must resolve.
                { 5, new GhostPlaybackLogic.LoopUnit.MemberWindow(27_000_000.0, Deorbit) },
                { 49, new GhostPlaybackLogic.LoopUnit.MemberWindow(W1Start, overlapDescentWindows ? W3End : W1End) },
                { 50, new GhostPlaybackLogic.LoopUnit.MemberWindow(overlapDescentWindows ? W1Start : W1End, overlapDescentWindows ? W3End : W2End) },
                { 51, new GhostPlaybackLogic.LoopUnit.MemberWindow(overlapDescentWindows ? W1Start : W2End, W3End) },
            };
            var plan = new Parsek.Reaim.ReaimMissionPlan { Supported = true };
            var sched = new Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule { Valid = true };
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, 5, 49, 50, 51 }, SpanStart, DescentEnd, Cad, Pa, Cad, windows, null, plan, sched,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: double.NaN, launchHoldEngaged: false,
                recordedSoiExitUT: double.NaN,
                descentMemberIndices: engage ? new[] { 49, 50, 51 } : null,
                recordedDeorbitUT: engage ? Deorbit : double.NaN,
                descentEndUT: engage ? DescentEnd : double.NaN,
                destinationBodyRotationPeriodSeconds: engage ? DunaTrot : double.NaN,
                loiterPeriodSeconds: engage ? Tpark : double.NaN,
                captureShiftSeconds: engage ? CapShift : double.NaN);
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 }, { 5, 0 }, { 49, 0 }, { 50, 0 }, { 51, 0 } });
        }

        private static double Resolve(int i, double wStart, double wEnd, double liveUT, GhostPlaybackLogic.LoopUnitSet units, out bool hidden)
            => GhostPlaybackLogic.ResolveTrackingStationSampleUT(i, wStart, wEnd, liveUT, units, out hidden);

        [Fact]
        public void Resolver_DescentMember_RendersOnlyItsOwnSlice()
        {
            var units = BuildDescentUnit(engage: true);
            double t = TriggerUT(0) + 400.0; // shared head = Deorbit + 400 -> in member 49's window only

            double u49 = Resolve(49, W1Start, W1End, t, units, out bool h49);
            Assert.False(h49);
            Assert.Equal(Deorbit + 400.0, u49, 3);

            Resolve(50, W1End, W2End, t, units, out bool h50);
            Resolve(51, W2End, W3End, t, units, out bool h51);
            Assert.True(h50, "member 50 hidden outside its slice");
            Assert.True(h51, "member 51 hidden outside its slice");
        }

        [Fact]
        public void Resolver_DescentMember_HiddenInInertLoiterDone()
        {
            var units = BuildDescentUnit(engage: true);
            Resolve(49, W1Start, W1End, EntryUT(0) - 100.0, units, out bool inert);   // Inert
            Resolve(49, W1Start, W1End, EntryUT(0) + 100.0, units, out bool loiter);  // Loiter
            Resolve(49, W1Start, W1End, TriggerUT(0) + (DescentEnd - Deorbit) + 50.0, units, out bool done); // Done
            Assert.True(inert, "descent member hidden during Inert (never rides the raw loop clock)");
            Assert.True(loiter, "descent member hidden during Loiter (transfer member owns the conic icon)");
            Assert.True(done, "descent member hidden after the clip ends");
        }

        [Fact]
        public void Resolver_NonDescentMember_ByteIdenticalWithTriggerOnOrOff()
        {
            var on = BuildDescentUnit(engage: true);
            var off = BuildDescentUnit(engage: false);
            // The owner member 0 is NOT in the descent set, so the override must never touch it: its result is
            // identical whether the unit carries a descent trigger or not, across launch / hidden / inter-cycle.
            foreach (double t in new[] { Pa + 100.0, Pa + 700.0, Pa + 40_000_000.0, Pa - 50.0 })
            {
                double uOn = Resolve(0, 0.0, 500.0, t, on, out bool hOn);
                double uOff = Resolve(0, 0.0, 500.0, t, off, out bool hOff);
                Assert.Equal(hOff, hOn);
                if (!hOff && !double.IsNaN(uOff)) Assert.Equal(uOff, uOn, 6);
            }
        }

        [Fact]
        public void Resolver_Frame_DescentMember_SuppressesSecondary()
        {
            var units = BuildDescentUnit(engage: true);
            double t = TriggerUT(0) + 400.0;
            GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                49, W1Start, W1End, t, units, out bool _, out bool hasSecondary, out double _, out long _);
            Assert.False(hasSecondary, "a descent member must not emit an un-remapped boundary-overlap secondary (R6)");
        }

        [Fact]
        public void Resolver_TransferMember_HiddenDuringDescent_CleanHandoffNoDoubleIcon()
        {
            // The 2026-06-20 bug: when the descent fires, the transfer/loiter member keeps drawing its parking icon
            // on the raw loop clock WHILE the descent member draws the clip — two icons, and the user tracks the
            // loiter one. Member 5's window covers the shifted-parking region, so it RENDERS through both phases.
            var units = BuildDescentUnit(engage: true);

            // LOITER (before the trigger): the transfer member SHOULD draw — it carries the only icon while the
            // descent member is still hidden.
            double tLoiter = EntryUT(0) + 100.0;
            Resolve(5, 27_000_000.0, Deorbit, tLoiter, units, out bool hLoiterTransfer);
            Resolve(49, W1Start, W1End, tLoiter, units, out bool hLoiterDescent);
            Assert.False(hLoiterTransfer, "transfer member draws the loiter icon while the descent still waits");
            Assert.True(hLoiterDescent, "descent member hidden during the loiter");

            // DESCENT PLAYING: the transfer member must HAND OFF — hidden — so the descent member's icon is the only
            // one (no double icon).
            double tDescent = TriggerUT(0) + 400.0;
            Resolve(5, 27_000_000.0, Deorbit, tDescent, units, out bool hDescentTransfer);
            Resolve(49, W1Start, W1End, tDescent, units, out bool hDescentDescent);
            Assert.True(hDescentTransfer, "transfer member hidden once the descent plays (hand off, no double icon)");
            Assert.False(hDescentDescent, "the descent member is the single visible icon during the descent");

            // With the trigger OFF the transfer member is byte-identical (renders in both phases — no handoff).
            var off = BuildDescentUnit(engage: false);
            Resolve(5, 27_000_000.0, Deorbit, tDescent, off, out bool hOff);
            Assert.False(hOff, "trigger OFF: no descent phase, so the transfer member is never hidden by the handoff");
        }

        [Fact]
        public void Resolver_DescentMember_ReArmsAtCycle1()
        {
            var units = BuildDescentUnit(engage: true);
            double t = TriggerUT(1) + 400.0; // cycle 1, shared head = Deorbit + 400 -> member 49 again
            double u49 = Resolve(49, W1Start, W1End, t, units, out bool h49);
            Assert.False(h49);
            Assert.Equal(Deorbit + 400.0, u49, 3);
        }

        // --- I1 wiring: GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember ---
        //
        // Resolves the transfer member's deorbit-tail head via the production DecideUnitMemberRender cycle path
        // (member 5 is the non-descent transfer member in BuildDescentUnit). Loiter-only; conicEnd/seam exposed.

        [Fact]
        public void TransferDeorbitHeadForMember_TransferMember_LoiterOnly()
        {
            var units = BuildDescentUnit(engage: true);
            var unit = DescentUnitFor(units);

            // Loiter -> true, head below seam, conicEnd + seam reported.
            double tLoiter = EntryUT(0) + 100.0;
            Assert.True(tLoiter < TriggerUT(0));
            Assert.True(GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember(
                unit, 5, tLoiter, 27_000_000.0, Deorbit, out double head, out double conicEnd, out double seam));
            Assert.True(head < Deorbit, $"head={head} not below seam");
            Assert.Equal(Deorbit, seam, 3);
            Assert.Equal(Deorbit + CapShift, conicEnd, 3);

            // Inert / Descent / Done -> false.
            Assert.False(GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember(
                unit, 5, EntryUT(0) - 100.0, 27_000_000.0, Deorbit, out _, out _, out _)); // Inert
            Assert.False(GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember(
                unit, 5, TriggerUT(0) + 400.0, 27_000_000.0, Deorbit, out _, out _, out _)); // Descent
            Assert.False(GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember(
                unit, 5, TriggerUT(0) + (DescentEnd - Deorbit) + 50.0, 27_000_000.0, Deorbit, out _, out _, out _)); // Done
        }

        [Fact]
        public void TransferDeorbitHeadForMember_DescentMemberAndTriggerOff_ReturnFalse()
        {
            var on = BuildDescentUnit(engage: true);
            var onUnit = DescentUnitFor(on);
            // A DESCENT-set member (49) carries no deorbit tail (its own trigger-gated pass owns the clip).
            Assert.False(GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember(
                onUnit, 49, EntryUT(0) + 100.0, W1Start, W1End, out _, out _, out _));

            // Trigger OFF: the transfer member yields no deorbit head (byte-identical-off).
            var off = BuildDescentUnit(engage: false);
            var offUnit = DescentUnitFor(off);
            Assert.False(GhostPlaybackLogic.TryResolveTransferDeorbitHeadForMember(
                offUnit, 5, EntryUT(0) + 100.0, 27_000_000.0, Deorbit, out _, out _, out _));
        }

        // --- COSMETIC 2: GhostPlaybackLogic.IsTransferMemberDescentContinuation (post-landing suborbital ghost) ---
        //
        // The transfer member's map/TS orbit ghost must RETIRE once the shared descent has handed off (Descent)
        // or landed (Done) — that is the window where, with no covering segment past the parking conic, the
        // create resolver would synthesize a sub-surface EndpointTail looping ghost. It must NOT retire during
        // Inert/Loiter (the icon is still riding the shifted parking conic — that conic MUST keep rendering).
        // Member 5 is the non-descent transfer member; 49 is a descent-set member. Phase boundaries match the
        // I1 test above (Inert/Loiter/Descent/Done off EntryUT/TriggerUT).

        [Fact]
        public void TransferMemberDescentContinuation_DescentAndDone_True_RetiresTheGhost()
        {
            var unit = DescentUnitFor(BuildDescentUnit(engage: true));

            // DESCENT: the descent set owns the visual; the transfer ghost retires (no sub-surface tail).
            Assert.True(GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                unit, 5, TriggerUT(0) + 400.0, 27_000_000.0, Deorbit));
            // DONE (post-landing): the handoff no longer hides the transfer member, so the spurious tail would
            // appear here — retire it. This is the exact post-landing bug window.
            Assert.True(GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                unit, 5, TriggerUT(0) + (DescentEnd - Deorbit) + 50.0, 27_000_000.0, Deorbit));
        }

        [Fact]
        public void TransferMemberDescentContinuation_InertAndLoiter_False_PreservesLoiterConic()
        {
            var unit = DescentUnitFor(BuildDescentUnit(engage: true));

            // INERT (icon still launching/transferring/riding the parking conic) -> keep the normal source.
            Assert.False(GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                unit, 5, EntryUT(0) - 100.0, 27_000_000.0, Deorbit));
            // LOITER (icon circling the SHIFTED PARKING conic) -> must NOT retire, or the loiter conic vanishes.
            double tLoiter = EntryUT(0) + 100.0;
            Assert.True(tLoiter < TriggerUT(0));
            Assert.False(GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                unit, 5, tLoiter, 27_000_000.0, Deorbit));
        }

        [Fact]
        public void TransferMemberDescentContinuation_DescentSetMember_AlwaysFalse()
        {
            var unit = DescentUnitFor(BuildDescentUnit(engage: true));
            // A descent-SET member (49) is governed by its own trigger-gated render; this transfer-only gate
            // never touches it, in any phase.
            foreach (double t in new[] { EntryUT(0) - 100.0, EntryUT(0) + 100.0, TriggerUT(0) + 400.0,
                TriggerUT(0) + (DescentEnd - Deorbit) + 50.0 })
                Assert.False(GhostPlaybackLogic.IsTransferMemberDescentContinuation(unit, 49, t, W1Start, W1End));
        }

        [Fact]
        public void TransferMemberDescentContinuation_TriggerOff_AlwaysFalse_ByteIdentical()
        {
            var unit = DescentUnitFor(BuildDescentUnit(engage: false));
            // No descent trigger: the transfer member is never retired by this gate, in any phase.
            foreach (double t in new[] { Pa + 100.0, Pa + 700.0, Pa + 30_000_000.0, TriggerUT(0) + 400.0 })
                Assert.False(GhostPlaybackLogic.IsTransferMemberDescentContinuation(unit, 5, t, 27_000_000.0, Deorbit));
        }

        [Fact]
        public void TransferMemberDescentContinuation_SetWrapper_NullAndNonMember_False()
        {
            var units = BuildDescentUnit(engage: true);
            double tDescent = TriggerUT(0) + 400.0;
            // Wrapper resolves the unit for a real member (matches the LoopUnit overload).
            Assert.True(GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                units, 5, tDescent, 27_000_000.0, Deorbit));
            // Null set / non-member index -> false (byte-identical-off; no NRE).
            Assert.False(GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                (GhostPlaybackLogic.LoopUnitSet)null, 5, tDescent, 27_000_000.0, Deorbit));
            Assert.False(GhostPlaybackLogic.IsTransferMemberDescentContinuation(
                units, 999, tDescent, 27_000_000.0, Deorbit));
        }

        // --- OBSERVABILITY: GhostPlaybackLogic.TryGetDescentUnitRenderPhase (descent render-window tracing) ---
        //
        // Reports the unit-level descent phase for ANY member, gating the per-frame map-scene snapshot to the
        // loiter (Loiter) + descent-to-landing (Descent) windows. Member-agnostic; false / Inert when off.

        [Fact]
        public void DescentUnitRenderPhase_TransferMember_AllFourPhases()
        {
            var unit = DescentUnitFor(BuildDescentUnit(engage: true));
            Assert.True(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                unit, 5, EntryUT(0) - 100.0, 27_000_000.0, Deorbit, out var inert));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert, inert);
            Assert.True(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                unit, 5, EntryUT(0) + 100.0, 27_000_000.0, Deorbit, out var loiter));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Loiter, loiter);
            Assert.True(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                unit, 5, TriggerUT(0) + 400.0, 27_000_000.0, Deorbit, out var descent));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Descent, descent);
            Assert.True(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                unit, 5, TriggerUT(0) + (DescentEnd - Deorbit) + 50.0, 27_000_000.0, Deorbit, out var done));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Done, done);
        }

        [Fact]
        public void DescentUnitRenderPhase_IsMemberAgnostic()
        {
            // The phase is unit-level: a descent-SET member (49) reports the SAME phase as the transfer member (5).
            var unit = DescentUnitFor(BuildDescentUnit(engage: true));
            double t = TriggerUT(0) + 400.0; // Descent
            Assert.True(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(unit, 49, t, W1Start, W1End, out var p49));
            Assert.True(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(unit, 5, t, 27_000_000.0, Deorbit, out var p5));
            Assert.Equal(p5, p49);
            Assert.Equal(DescentTrigger.DescentHeadPhase.Descent, p49);
        }

        [Fact]
        public void DescentUnitRenderPhase_TriggerOffNullNonMember_FalseInert()
        {
            var off = DescentUnitFor(BuildDescentUnit(engage: false));
            Assert.False(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                off, 5, TriggerUT(0) + 400.0, 27_000_000.0, Deorbit, out var pOff));
            Assert.Equal(DescentTrigger.DescentHeadPhase.Inert, pOff);

            var units = BuildDescentUnit(engage: true);
            Assert.False(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                (GhostPlaybackLogic.LoopUnitSet)null, 5, TriggerUT(0) + 400.0, 27_000_000.0, Deorbit, out _));
            Assert.False(GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                units, 999, TriggerUT(0) + 400.0, 27_000_000.0, Deorbit, out _));
        }

        // --- C1: flight descent icon carrier (GhostPlaybackLogic.IsActiveDescentCarrierMember / ResolveFlightDescentIconCarrier) ---
        //
        // Names the descent-set member whose descent leg is drawn THIS frame so the flight-map chain-tip skip
        // can exempt it (the in-window descent member is usually not the chain index-tip). Single carrier per
        // frame (highest index on a shared window) => no double icon.

        [Fact]
        public void ActiveDescentCarrierMember_OnlyTheInWindowDescentMemberInDescentPhase()
        {
            var units = BuildDescentUnit(engage: true);
            double tDescent = TriggerUT(0) + 400.0; // shared head = Deorbit + 400 -> in member 49's window only

            // Member 49 owns this slice this frame -> carrier.
            Assert.True(GhostPlaybackLogic.IsActiveDescentCarrierMember(units, 49, tDescent, W1Start, W1End));
            // Members 50/51 are descent members but their slices are not current -> not the carrier.
            Assert.False(GhostPlaybackLogic.IsActiveDescentCarrierMember(units, 50, tDescent, W1End, W2End));
            Assert.False(GhostPlaybackLogic.IsActiveDescentCarrierMember(units, 51, tDescent, W2End, W3End));
            // The non-descent transfer member (5) is never a descent carrier.
            Assert.False(GhostPlaybackLogic.IsActiveDescentCarrierMember(units, 5, tDescent, 27_000_000.0, Deorbit));
            // Loiter: no descent member renders yet -> no carrier.
            Assert.False(GhostPlaybackLogic.IsActiveDescentCarrierMember(units, 49, EntryUT(0) + 100.0, W1Start, W1End));
            // Trigger off: byte-identical, never a carrier.
            var off = BuildDescentUnit(engage: false);
            Assert.False(GhostPlaybackLogic.IsActiveDescentCarrierMember(off, 49, tDescent, W1Start, W1End));
        }

        [Fact]
        public void ResolveFlightDescentIconCarrier_PicksTheSingleInWindowMember()
        {
            var committed = BuildCommittedListForCarrier();
            var units = BuildDescentUnit(engage: true);

            // Descent phase, head in member 49's window -> carrier 49.
            Assert.Equal(49, GhostPlaybackLogic.ResolveFlightDescentIconCarrier(committed, TriggerUT(0) + 400.0, units));
            // Loiter -> no carrier (-1).
            Assert.Equal(-1, GhostPlaybackLogic.ResolveFlightDescentIconCarrier(committed, EntryUT(0) + 100.0, units));
            // Trigger off -> -1 (byte-identical; the chain-tip skip is unchanged).
            Assert.Equal(-1, GhostPlaybackLogic.ResolveFlightDescentIconCarrier(committed, TriggerUT(0) + 400.0, BuildDescentUnit(engage: false)));
            // Null inputs -> -1, no NRE.
            Assert.Equal(-1, GhostPlaybackLogic.ResolveFlightDescentIconCarrier(null, TriggerUT(0) + 400.0, units));
            Assert.Equal(-1, GhostPlaybackLogic.ResolveFlightDescentIconCarrier(committed, TriggerUT(0) + 400.0, null));
        }

        [Fact]
        public void ResolveFlightDescentIconCarrier_SharedWindow_PicksHighestIndex_NoDoubleIcon()
        {
            // Members 49/50/51 ALL span the surface window (like rec 42 + rec 43), so all three are "in window"
            // for the same Descent-phase head. The carrier must be exactly ONE - the highest index (51) - so the
            // flight chain-tip exemption draws a single descent icon, not three.
            var committed = BuildCommittedListForCarrier();
            var overlap = BuildDescentUnit(engage: true, overlapDescentWindows: true);
            double tDescent = TriggerUT(0) + 400.0;
            Assert.True(GhostPlaybackLogic.IsActiveDescentCarrierMember(overlap, 49, tDescent, W1Start, W3End));
            Assert.True(GhostPlaybackLogic.IsActiveDescentCarrierMember(overlap, 50, tDescent, W1Start, W3End));
            Assert.True(GhostPlaybackLogic.IsActiveDescentCarrierMember(overlap, 51, tDescent, W1Start, W3End));
            Assert.Equal(51, GhostPlaybackLogic.ResolveFlightDescentIconCarrier(committed, tDescent, overlap));
        }

        // Committed list covering indices 0..51; the descent members' windows come from the unit (MemberStartUT
        // overrides the rec UTs), so the rec UTs here are placeholders.
        private static System.Collections.Generic.List<Recording> BuildCommittedListForCarrier()
        {
            var committed = new System.Collections.Generic.List<Recording>();
            for (int i = 0; i <= 51; i++)
                committed.Add(new Recording { RecordingId = "carrier-rec-" + i });
            return committed;
        }

        // --- FLIGHT-engine integration: GhostPlaybackLogic.ResolveDescentMemberEngineRender (Defect 3) ---
        //
        // The engine-side complement of the resolver branch: the same TryResolveDescentMemberHead remap,
        // surfaced as render-flag + head + phase so the flight engine can drive a live ghost (vs the resolver's
        // UT + renderHidden). These assert the engine helper produces the SAME render/head decision as the
        // resolver for the descent set, so the flight scene renders descent members identically to the map/TS.

        private static GhostPlaybackLogic.LoopUnit DescentUnitFor(GhostPlaybackLogic.LoopUnitSet units)
        {
            Assert.True(units.TryGetUnitForMember(49, out GhostPlaybackLogic.LoopUnit unit));
            return unit;
        }

        [Fact]
        public void EngineRender_DescentMember_RendersOnlyItsOwnSlice()
        {
            var units = BuildDescentUnit(engage: true);
            var unit = DescentUnitFor(units);
            double t = TriggerUT(0) + 400.0; // shared head = Deorbit + 400 -> in member 49's window only

            var r49 = GhostPlaybackLogic.ResolveDescentMemberEngineRender(unit, 49, t, 0, W1Start, W1End);
            Assert.True(r49.Render);
            Assert.Equal(Parsek.Reaim.DescentTrigger.DescentHeadPhase.Descent, r49.Phase);
            Assert.Equal(Deorbit + 400.0, r49.Head, 3);

            var r50 = GhostPlaybackLogic.ResolveDescentMemberEngineRender(unit, 50, t, 0, W1End, W2End);
            var r51 = GhostPlaybackLogic.ResolveDescentMemberEngineRender(unit, 51, t, 0, W2End, W3End);
            Assert.False(r50.Render);
            Assert.False(r51.Render);
            // Hidden members carry NaN heads (never positioned on the raw loop clock).
            Assert.True(double.IsNaN(r50.Head));
            Assert.True(double.IsNaN(r51.Head));
        }

        [Fact]
        public void EngineRender_DescentMember_HiddenInInertLoiterDone()
        {
            var units = BuildDescentUnit(engage: true);
            var unit = DescentUnitFor(units);

            var inert = GhostPlaybackLogic.ResolveDescentMemberEngineRender(
                unit, 49, EntryUT(0) - 100.0, 0, W1Start, W1End);
            var loiter = GhostPlaybackLogic.ResolveDescentMemberEngineRender(
                unit, 49, EntryUT(0) + 100.0, 0, W1Start, W1End);
            var done = GhostPlaybackLogic.ResolveDescentMemberEngineRender(
                unit, 49, TriggerUT(0) + (DescentEnd - Deorbit) + 50.0, 0, W1Start, W1End);

            Assert.False(inert.Render);
            Assert.Equal(Parsek.Reaim.DescentTrigger.DescentHeadPhase.Inert, inert.Phase);
            Assert.False(loiter.Render);
            Assert.Equal(Parsek.Reaim.DescentTrigger.DescentHeadPhase.Loiter, loiter.Phase);
            Assert.False(done.Render);
            Assert.Equal(Parsek.Reaim.DescentTrigger.DescentHeadPhase.Done, done.Phase);
            Assert.True(double.IsNaN(inert.Head));
            Assert.True(double.IsNaN(loiter.Head));
            Assert.True(double.IsNaN(done.Head));
        }

        [Fact]
        public void EngineRender_MatchesResolver_HeadAndRenderFlag()
        {
            var units = BuildDescentUnit(engage: true);
            var unit = DescentUnitFor(units);
            // Across Inert / Loiter / Descent(in-slice + out-of-slice) / Done the engine render flag mirrors the
            // resolver's !renderHidden, and the rendered head equals the resolver's returned UT.
            foreach (double t in new[]
            {
                EntryUT(0) - 100.0,                         // Inert
                EntryUT(0) + 100.0,                         // Loiter
                TriggerUT(0) + 400.0,                       // Descent, in member 49's slice
                TriggerUT(0) + (DescentEnd - Deorbit) + 50.0 // Done
            })
            {
                double resolverUT = Resolve(49, W1Start, W1End, t, units, out bool resolverHidden);
                var eng = GhostPlaybackLogic.ResolveDescentMemberEngineRender(unit, 49, t, 0, W1Start, W1End);
                Assert.Equal(!resolverHidden, eng.Render);
                if (eng.Render)
                    Assert.Equal(resolverUT, eng.Head, 6);
            }
        }

        [Fact]
        public void EngineRender_ReArmsAtCycle1()
        {
            var units = BuildDescentUnit(engage: true);
            var unit = DescentUnitFor(units);
            double t = TriggerUT(1) + 400.0; // cycle 1, shared head = Deorbit + 400 -> member 49 again
            var r = GhostPlaybackLogic.ResolveDescentMemberEngineRender(unit, 49, t, 1, W1Start, W1End);
            Assert.True(r.Render);
            Assert.Equal(Deorbit + 400.0, r.Head, 3);
            Assert.Equal(1L, r.CycleIndex);
        }

        // --- DescribeLoopMemberRoleForTeardown: the teardown-reason tag that ties a [GhostMap] destroy to the
        //     descent member (observability, used at the renderHidden ghost-teardown sites) ---

        [Fact]
        public void TeardownRole_DescentMember_TagsWithIndex()
        {
            var units = BuildDescentUnit(engage: true);
            // A descent member's teardown reason carries " descent-member={i}" for correlation.
            string r49 = GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(49, units);
            Assert.Equal(" descent-member=49", r49);
            Assert.Equal(" descent-member=51", GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(51, units));
        }

        [Fact]
        public void TeardownRole_NonDescentAndNonMember_AreEmpty()
        {
            var on = BuildDescentUnit(engage: true);
            // The owner member 0 is a unit member but NOT in the descent set -> empty (byte-identical reason).
            Assert.Equal(string.Empty, GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(0, on));
            // An index not in the unit at all -> empty.
            Assert.Equal(string.Empty, GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(999, on));
            // A null unit set -> empty (no loop, no role).
            Assert.Equal(string.Empty, GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(49, null));
            // The SAME descent index but with the trigger NOT engaged (no descent trigger) -> empty.
            var off = BuildDescentUnit(engage: false);
            Assert.Equal(string.Empty, GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(49, off));
        }

        // --- ComputeDescentTiming: the window bounds shared by the head dispatch and the observability trace ---

        [Fact]
        public void Timing_MatchesTheHeadDispatchEntryAndTrigger()
        {
            DescentTrigger.ComputeDescentTiming(
                0, Pa, Cad, SpanStart, Deorbit, DunaTrot, CapShift, null,
                out double conicEnd, out double entryUT, out double triggerUT);
            Assert.Equal(Deorbit + CapShift, conicEnd, 3);
            Assert.Equal(EntryUT(0), entryUT, 3);     // same entry the head dispatch uses
            Assert.Equal(TriggerUT(0), triggerUT, 3); // same trigger -> the trace's window agrees with the render
        }

        // --- ClassifyDescentRenderEvent: the per-cycle render/skip lifecycle (pure, stateful via ref) ---

        [Fact]
        public void Lifecycle_LoiterThenDescent_EmitsWindowThenRendered_OncEach()
        {
            var s = default(DescentTrigger.DescentTraceState);
            Assert.Equal(DescentTrigger.DescentRenderEvent.WindowOpened,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter));
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,  // idempotent across per-frame call sites
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter));
            Assert.Equal(DescentTrigger.DescentRenderEvent.Rendered,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Descent));
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Descent));
            // Reaching Done after a real render does NOT emit Skipped.
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Done));
        }

        [Fact]
        public void Lifecycle_LoiterThenDone_NoDescent_EmitsSkippedOnce()
        {
            var s = default(DescentTrigger.DescentTraceState);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 3, DescentTrigger.DescentHeadPhase.Loiter); // window
            Assert.Equal(DescentTrigger.DescentRenderEvent.Skipped, // warp stepped Loiter -> Done over the window
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 3, DescentTrigger.DescentHeadPhase.Done));
            Assert.Equal(DescentTrigger.DescentRenderEvent.None, // only once
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 3, DescentTrigger.DescentHeadPhase.Done));
        }

        [Fact]
        public void Lifecycle_NewCycle_ReArms()
        {
            var s = default(DescentTrigger.DescentTraceState);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Descent);
            // Next cycle: the window opens again (flags reset on the cycle change).
            Assert.Equal(DescentTrigger.DescentRenderEvent.WindowOpened,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 1, DescentTrigger.DescentHeadPhase.Loiter));
            Assert.Equal(DescentTrigger.DescentRenderEvent.Rendered,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 1, DescentTrigger.DescentHeadPhase.Descent));
        }

        [Fact]
        public void Lifecycle_Inert_EmitsNothing()
        {
            var s = default(DescentTrigger.DescentTraceState);
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Inert));
            // Even repeated Inert (the long launch/transfer/parking stretch) stays silent.
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Inert));
        }

        // --- Reverted: the descent rendered then fell back to Loiter/Inert mid-clip (the user-reported
        //     "icon moved back onto the loiter trajectory" bug). One event per cycle; a clean
        //     Descent -> Done finish is NOT a revert. ---

        [Fact]
        public void Lifecycle_RenderedThenLoiter_EmitsRevertedOnce()
        {
            var s = default(DescentTrigger.DescentTraceState);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter); // window
            Assert.Equal(DescentTrigger.DescentRenderEvent.Rendered,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Descent));
            // The head leaves the descent clip back to Loiter mid-cycle -> Reverted (the bug).
            Assert.Equal(DescentTrigger.DescentRenderEvent.Reverted,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter));
            // Only once per cycle, even if it oscillates back and forth.
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Descent));
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter));
        }

        [Fact]
        public void Lifecycle_RenderedThenInert_EmitsReverted()
        {
            var s = default(DescentTrigger.DescentTraceState);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 2, DescentTrigger.DescentHeadPhase.Loiter);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 2, DescentTrigger.DescentHeadPhase.Descent);
            // Falling all the way back to Inert after rendering is also a revert.
            Assert.Equal(DescentTrigger.DescentRenderEvent.Reverted,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 2, DescentTrigger.DescentHeadPhase.Inert));
        }

        [Fact]
        public void Lifecycle_CleanDescentToDone_DoesNotEmitReverted()
        {
            var s = default(DescentTrigger.DescentTraceState);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter);
            DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Descent);
            // The normal landing: Descent -> Done is the expected finish, NOT a revert (Done is excluded).
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Done));
        }

        [Fact]
        public void Lifecycle_LoiterBeforeRender_DoesNotEmitReverted()
        {
            var s = default(DescentTrigger.DescentTraceState);
            // Loiter BEFORE any Descent frame opens the window but is not a revert (nothing rendered yet).
            Assert.Equal(DescentTrigger.DescentRenderEvent.WindowOpened,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter));
            Assert.Equal(DescentTrigger.DescentRenderEvent.None,
                DescentTrigger.ClassifyDescentRenderEvent(ref s, 0, DescentTrigger.DescentHeadPhase.Loiter));
        }
    }
}
