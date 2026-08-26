using System;
using System.Globalization;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests.MapRender
{
    /// <summary>
    /// The inter-cycle-tail FALLBACK close
    /// (bug <c>V6M-CYCLE0-ARRIVALLOITER-DWELL-CLOSE-RECORD-LOST</c>).
    ///
    /// <para>THE SUBJECT, measured rather than imagined. A dwell is closed by
    /// <see cref="RenderCompositionManifest.ObserveDwellFrame"/> only when a later frame arrives for
    /// the same pid with a CHANGED identity. On 1 of 6 archived V6M map-open flights
    /// (<c>2026-08-26_1840</c>) the cycle-0 ghost stopped being sampled while its ArrivalLoiter dwell
    /// was open - it was observed ZERO times in the <c>7 -&gt; -1</c> successor state, against
    /// 4/6/7/7/10/11/15 frames on the green flights - so no close ever ran, the dwell reached export
    /// as <c>openAtExport</c>, and the verifier (which keeps open dwells out of the CLOSED
    /// population by design) read the cycle as having had no ArrivalLoiter at all. A correctly
    /// rendered loop became an RC-CYCLE non-isomorphism.</para>
    ///
    /// <para>The cells below pin BOTH halves of the fix: that it fires on the red shape, and - the
    /// half that actually costs something to get wrong - that it is inert on every green one.</para>
    /// </summary>
    public class RenderCompositionTailCloseFallbackTests
    {
        private const int Owner = 0;
        /// <summary>The ending cycle's start: every fixture dwell below opens after it.</summary>
        private const double CycleStart = 50.0;
        private const int OtherOwner = 4;

        private static RenderCompositionManifest.DwellSample Sample(
            uint pid, int ownerIndex, int segmentIndex, string phaseKind, double ut)
        {
            var s = default(RenderCompositionManifest.DwellSample);
            s.Pid = pid;
            s.RecId = "rec-" + pid.ToString(CultureInfo.InvariantCulture);
            s.CommittedIndex = 1;
            s.ChainSignature = "sig-A";
            s.SegmentIndex = segmentIndex;
            s.PhaseKind = phaseKind;
            s.Treatment = segmentIndex < 0 ? "None" : "StockConic";
            s.Visible = segmentIndex >= 0;
            s.Coverage = "InSegment";
            s.FrameBody = "Mun";
            s.CurrentUT = ut;
            s.HeadUT = ut;
            s.WarpRate = 1.0;
            s.PhysicsWarp = false;
            s.HasOwnerIndex = true;
            s.OwnerIndex = ownerIndex;
            return s;
        }

        // =====================================================================================
        //  The two pure predicates
        // =====================================================================================

        [Theory]
        // Sampled BEFORE the event: the pid never reached its successor state -> stale.
        [InlineData(99.0, 100.0, true)]
        [InlineData(0.5, 100.0, true)]
        // Sampled AT the event: the render path is handling this pid on this very frame. Leaving it
        // alone is the double-close guard, and the reason the comparison is strict.
        [InlineData(100.0, 100.0, false)]
        // Sampled AFTER the event: plainly live.
        [InlineData(101.0, 100.0, false)]
        public void DwellIsStaleAtClockEvent_IsStrictlyBefore(
            double lastObserved, double eventUT, bool expected)
        {
            Assert.Equal(expected,
                RenderCompositionManifest.DwellIsStaleAtClockEvent(lastObserved, eventUT));
        }

        [Fact]
        public void DwellIsStaleAtClockEvent_RefusesUnusableInstants()
        {
            // An unusable instant is not a licence to retire a record.
            Assert.False(RenderCompositionManifest.DwellIsStaleAtClockEvent(double.NaN, 100.0));
            Assert.False(RenderCompositionManifest.DwellIsStaleAtClockEvent(99.0, double.NaN));
            Assert.False(RenderCompositionManifest.DwellIsStaleAtClockEvent(
                double.NegativeInfinity, 100.0));
            Assert.False(RenderCompositionManifest.DwellIsStaleAtClockEvent(
                99.0, double.PositiveInfinity));
        }

        [Theory]
        // A STRICTLY LATER frame than the one that armed it - the whole safety argument.
        [InlineData(100.0, 100.5, true)]
        [InlineData(100.0, 100.0000001, true)]
        // The arming frame itself: the recorder's Update and the Director render path have no pinned
        // relative order, so on this frame a still-open dwell may be one the render path is about to
        // close and emit a TRANSITION for. Never fire here.
        [InlineData(100.0, 100.0, false)]
        // Clock went backwards (a rewind / reload): not due.
        [InlineData(100.0, 99.0, false)]
        public void TailCloseFallbackIsDue_OnlyOnAStrictlyLaterFrame(
            double pendingUT, double nowUT, bool expected)
        {
            Assert.Equal(expected,
                RenderCompositionRecorder.TailCloseFallbackIsDue(pendingUT, nowUT));
        }

        [Fact]
        public void TailCloseFallbackIsDue_RefusesUnusableInstants()
        {
            Assert.False(RenderCompositionRecorder.TailCloseFallbackIsDue(double.NaN, 100.0));
            Assert.False(RenderCompositionRecorder.TailCloseFallbackIsDue(100.0, double.NaN));
            Assert.False(RenderCompositionRecorder.TailCloseFallbackIsDue(
                double.PositiveInfinity, 100.0));
            Assert.False(RenderCompositionRecorder.TailCloseFallbackIsDue(
                100.0, double.NegativeInfinity));
        }

        // =====================================================================================
        //  THE RED SHAPE - the defect the fallback exists for
        // =====================================================================================

        [Fact]
        public void StaleDwell_IsClosedAtTheEventUT_AndBecomesAClosedRecord()
        {
            var m = new RenderCompositionManifest();

            // The ghost is sampled through its arrival-loiter dwell and then never again - the
            // 2026-08-26_1840 shape exactly.
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 101.0));
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 102.0));
            Assert.Equal(0, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);

            int closed = m.FallbackCloseStaleOwnerDwells(
                Owner, 110.0, CycleStart, out uint firstPid, out string firstPhaseKind);

            Assert.Equal(1, closed);
            Assert.Equal(7u, firstPid);
            Assert.Equal("arrival-loiter", firstPhaseKind);
            Assert.Equal(1, m.ClosedDwellCount);
            Assert.Equal(0, m.OpenDwellCount);

            // THE CLOSE IS STAMPED AT THE EVENT INSTANT, not at the last observed frame: that is what
            // makes the recovered record indistinguishable from the frame-sampled close that should
            // have run (on the green flights it landed at exactly the tail event's UT).
            ConfigNode root = m.BuildFileNode(new RenderCompositionManifest.ManifestHeader(
                500.0, "verb", "FLIGHT", "s", true, false, true))
                .GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode[] dwells = root.GetNode("OBSERVED").GetNodes("DWELL");
            ConfigNode d = Assert.Single(dwells);
            Assert.Equal("arrival-loiter", d.GetValue("phaseKind"));
            Assert.Equal(110.0, double.Parse(d.GetValue("closeUT"), CultureInfo.InvariantCulture), 6);
            // And it is NOT reported as still-open, which is the whole point: an open dwell is kept
            // out of the closed population the cycle-structure rule reads.
            Assert.True(string.IsNullOrEmpty(d.GetValue("openAtExport"))
                || d.GetValue("openAtExport") == "False");
        }

        // =====================================================================================
        //  THE GREEN SHAPES - byte-invariance, which is the expensive half to get wrong
        // =====================================================================================

        [Fact]
        public void DwellSampledAtTheEventInstant_IsLeftAlone()
        {
            var m = new RenderCompositionManifest();

            // The successor state WAS observed, on the event's own frame: this is every green
            // flight, where the frame-sampled close lands at exactly the tail UT. The fallback must
            // not touch it - closing here would steal the TRANSITION the render path is about to
            // emit.
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));
            m.ObserveDwellFrame(Sample(7u, Owner, -1, "none", 110.0));
            Assert.Equal(1, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);
            Assert.Equal(1, m.TransitionCount);

            int closed = m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _);

            Assert.Equal(0, closed);
            Assert.Equal(1, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);
            Assert.Equal(1, m.TransitionCount);
        }

        [Fact]
        public void AlreadyFrameClosedDwell_CannotBeDoubleClosed()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));
            m.ObserveDwellFrame(Sample(7u, Owner, -1, "none", 110.0));
            int before = m.ClosedDwellCount;

            // Two applications in a row: the first is a no-op (live successor dwell), and a second
            // cannot resurrect and re-close the record the frame path already retired.
            m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _);
            m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _);

            Assert.Equal(before, m.ClosedDwellCount);
        }

        [Fact]
        public void FallbackIsIdempotent_OnTheRedShape()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));

            Assert.Equal(1, m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _));
            // Second call finds nothing open: the record left openDwellByPid on the first close.
            Assert.Equal(0, m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _));
            Assert.Equal(1, m.ClosedDwellCount);
        }

        [Fact]
        public void ADwellOfAnotherOwner_IsNeverSwept()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(7u, OtherOwner, 7, "arrival-loiter", 100.0));

            Assert.Equal(0, m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _));
            Assert.Equal(0, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);
        }

        [Fact]
        public void ADwellFromAnEarlierCycle_IsNeverSwept()
        {
            // THE REGRESSION THAT THREE PROOF FLIGHTS CAUGHT, and the reason the sweep is bounded
            // below by the ending cycle's start rather than by owner alone.
            //
            // A per-cycle ghost that stops being sampled in its "no segment" tail state leaves an
            // open dwell behind BY DESIGN - on every archived green flight those never close. Swept
            // by owner only, the NEXT cycle's tail event retires them, which moves an extra record
            // into the closed population and hands the cycle-structure rule a role (here `none`)
            // that the sibling cycle has no counterpart for. The first cut of this fix did exactly
            // that and reproduced `role structures differ ... vs ((1,'ArrivalLoiter'),
            // (1,'Descent'), (1,'None'))` on all three flights.
            var m = new RenderCompositionManifest();

            // Cycle 0's ghost: ends in the tail state and is then never sampled again.
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));
            m.ObserveDwellFrame(Sample(7u, Owner, -1, "none", 110.0));
            // Cycle 1's ghost, a different pid, running its own cycle.
            m.ObserveDwellFrame(Sample(8u, Owner, 7, "arrival-loiter", 210.0));

            int closedBefore = m.ClosedDwellCount;

            // Cycle 1's tail event. Cycle 0's leftover `-1` dwell is stale by every other test here,
            // but it opened BEFORE cycle 1 began, so it is not this tail's to retire.
            int closed = m.FallbackCloseStaleOwnerDwells(
                Owner, 220.0, cycleStartUT: 200.0, firstPid: out _, firstPhaseKind: out _);

            Assert.Equal(1, closed);                          // only cycle 1's own dwell
            Assert.Equal(closedBefore + 1, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);                // cycle 0's leftover, still open
        }

        [Fact]
        public void ADwellOpenedAfterTheEvent_IsLeftAlone()
        {
            var m = new RenderCompositionManifest();
            // Opened AFTER the event instant: closing it at the event would invert its own interval.
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 120.0));

            Assert.Equal(0, m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _));
            Assert.Equal(1, m.OpenDwellCount);
        }

        [Fact]
        public void ADwellWithNoOwnerStamp_IsNeverSwept()
        {
            var m = new RenderCompositionManifest();
            var s = Sample(7u, Owner, 7, "arrival-loiter", 100.0);
            s.HasOwnerIndex = false;
            m.ObserveDwellFrame(s);

            // Unattributed dwells are not this owner's to retire.
            Assert.Equal(0, m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _));
            Assert.Equal(1, m.OpenDwellCount);
        }

        [Fact]
        public void AnUnusableEventInstant_ClosesNothing()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));

            Assert.Equal(0, m.FallbackCloseStaleOwnerDwells(Owner, double.NaN, CycleStart, out _, out _));
            Assert.Equal(0, m.FallbackCloseStaleOwnerDwells(
                Owner, double.PositiveInfinity, CycleStart, out _, out _));
            Assert.Equal(1, m.OpenDwellCount);
        }

        [Fact]
        public void AnEmptyManifest_ClosesNothing()
        {
            var m = new RenderCompositionManifest();
            Assert.Equal(0, m.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out uint pid, out string kind));
            Assert.Equal(0u, pid);
            Assert.Null(kind);
        }

        // =====================================================================================
        //  The whole race, end to end over the manifest core
        // =====================================================================================

        [Fact]
        public void TheTwoFlightShapes_DifferOnlyInWhetherTheSuccessorStateWasSampled()
        {
            // GREEN: the ghost gets frames in the successor state, so the frame path closes the
            // arrival-loiter dwell itself and the fallback (deferred a frame, then applied) is inert.
            var green = new RenderCompositionManifest();
            green.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));
            green.ObserveDwellFrame(Sample(7u, Owner, -1, "none", 110.0));
            green.ObserveDwellFrame(Sample(7u, Owner, -1, "none", 110.04));
            Assert.True(RenderCompositionRecorder.TailCloseFallbackIsDue(110.0, 110.04));
            int greenClosedByFallback = green.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _);

            // RED: identical up to the tail, then the ghost is simply never sampled again.
            var red = new RenderCompositionManifest();
            red.ObserveDwellFrame(Sample(7u, Owner, 7, "arrival-loiter", 100.0));
            Assert.True(RenderCompositionRecorder.TailCloseFallbackIsDue(110.0, 110.04));
            int redClosedByFallback = red.FallbackCloseStaleOwnerDwells(Owner, 110.0, CycleStart, out _, out _);

            Assert.Equal(0, greenClosedByFallback);
            Assert.Equal(1, redClosedByFallback);

            // AND THE POINT OF THE WHOLE FIX: both runs now carry the arrival-loiter dwell in the
            // CLOSED population, which is the population the cycle-structure rule reads. Before the
            // fallback the red one carried it open and the cycle read as if it never happened.
            Assert.Equal(1, green.ClosedDwellCount);
            Assert.Equal(1, red.ClosedDwellCount);
        }
    }
}
