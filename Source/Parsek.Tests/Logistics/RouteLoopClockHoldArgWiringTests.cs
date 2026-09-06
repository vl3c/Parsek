using System;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ONE UNIT, ONE CLOCK: pins that <see cref="RouteLoopClock.TryGetRouteLoopState"/> - the
    /// DELIVERY clock - resolves the same <c>loopUT</c> / <c>cycleIndex</c> /
    /// <c>isInInterCycleTail</c> as the RENDER clock does for the same
    /// <see cref="GhostPlaybackLogic.LoopUnit"/>.
    ///
    /// <para>Closes ROUTE-DELIVERY-CLOCK-OMITS-THE-HOLD-ARGS. The seam used to forward two of
    /// the span clock's eleven optional arguments (the relaunch schedule and the loiter cuts)
    /// and let the other nine - the arrival hold, the launch-alignment trio and the three
    /// arrival-joint knobs - keep their defaults, so on any unit that carried one the two
    /// clocks parted by the held seconds and a dock crossing could be attributed to the wrong
    /// cycle.</para>
    ///
    /// <para>THE CELLS ARE WRITTEN AS EQUALITIES AGAINST THE RENDER CALL, not as a restatement
    /// of the argument list: an equality is what actually reds when a future argument is added
    /// to the span clock and forwarded on one side only. Each equality is paired with an
    /// INEQUALITY against the old schedule-only call at a discriminating UT, so a cell cannot
    /// pass by both sides being trivially identical - which is exactly what the hold-free
    /// mirror case IS, and it is pinned separately for that reason.</para>
    ///
    /// <para>WHAT THE EQUALITIES CANNOT DO is catch an argument added to the span clock and
    /// forwarded by NOBODY: <see cref="RenderClock"/> is a hand-written replica of the same
    /// list (there is no shared forwarding helper - production hand-copies it at four sites),
    /// so a defaulted-everywhere argument defaults on both sides and every equality passes.
    /// That is section 4's reflection cell, which pins the span clock's optional-parameter
    /// names against the forwarded set.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteLoopClockHoldArgWiringTests : IDisposable
    {
        private const double Tol = 1e-6;

        public RouteLoopClockHoldArgWiringTests()
        {
            ParsekLog.SuppressLogging = true; // value tests, not log assertions
            GhostPlaybackLogic.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetForTesting();
        }

        // The ArrivalAlignHoldTests fixture: span [0,1000], cadence 2000, a 200 s arrival hold
        // at the recorded boundary 600. At currentUT 700 the held clock reads 600 and the
        // hold-free one reads 700, so the two are 100 s apart on a single sample.
        private const double HoldSpanStart = 0.0;
        private const double HoldSpanEnd = 1000.0;
        private const double HoldCadence = 2000.0;
        private const double HoldAnchor = 0.0;
        private const double HoldSeconds = 200.0;
        private const double HoldAtUT = 600.0;

        // The BoundaryOverlapClockTests ZERO-SLACK launch-hold fixture: span [0,1000],
        // cadence == span (slack 0), phase anchor 300, body rotation period 700, SOI exit 600.
        // The borrow-at-launch advance is positive on every cycle, so the launch-alignment
        // trio bites at every sample rather than only at a chosen one.
        private const double LaunchAnchor = 300.0;
        private const double LaunchSpanStart = 0.0;
        private const double LaunchSpanEnd = 1000.0;
        private const double LaunchCadence = 1000.0;
        private const double LaunchRotationPeriod = 700.0;
        private const double LaunchSoiExit = 600.0;

        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT,
            double spanEndUT,
            double cadenceSeconds,
            double phaseAnchorUT,
            double arrivalHoldSeconds = 0.0,
            double arrivalHoldAtUT = double.NaN,
            double arrivalAlignPeriodSeconds = double.NaN,
            double launchBodyRotationPeriodSeconds = double.NaN,
            bool launchHoldEngaged = false,
            double recordedSoiExitUT = double.NaN,
            double arrivalJointSecondaryPeriodSeconds = double.NaN,
            double arrivalJointSecondaryToleranceSeconds = double.NaN,
            int arrivalJointMaxWholeHoldPeriods = 0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: spanStartUT,
                spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds,
                phaseAnchorUT: phaseAnchorUT,
                overlapCadenceSeconds: cadenceSeconds,
                memberWindows: null,
                relaunchSchedule: null,
                reaimPlan: null,
                reaimSchedule: null,
                loiterCuts: null,
                arrivalHoldSeconds: arrivalHoldSeconds,
                arrivalHoldAtUT: arrivalHoldAtUT,
                arrivalAlignPeriodSeconds: arrivalAlignPeriodSeconds,
                arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: launchBodyRotationPeriodSeconds,
                launchHoldEngaged: launchHoldEngaged,
                recordedSoiExitUT: recordedSoiExitUT,
                arrivalJointSecondaryPeriodSeconds: arrivalJointSecondaryPeriodSeconds,
                arrivalJointSecondaryToleranceSeconds: arrivalJointSecondaryToleranceSeconds,
                arrivalJointMaxWholeHoldPeriods: arrivalJointMaxWholeHoldPeriods);
        }

        /// <summary>The RENDER call: every optional argument off the unit, exactly as
        /// <c>GhostPlaybackEngine</c> passes them.</summary>
        private static bool RenderClock(
            GhostPlaybackLogic.LoopUnit unit, double currentUT,
            out double loopUT, out long cycleIndex, out bool tail)
        {
            return GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                out loopUT, out cycleIndex, out tail,
                unit.RelaunchSchedule,
                unit.LoiterCuts,
                unit.ArrivalHoldSeconds,
                unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds,
                unit.LaunchBodyRotationPeriodSeconds,
                unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT,
                unit.ArrivalJointSecondaryPeriodSeconds,
                unit.ArrivalJointSecondaryToleranceSeconds,
                unit.ArrivalJointMaxWholeHoldPeriods);
        }

        /// <summary>The PRE-FIX call: schedule + loiter cuts only, every hold argument
        /// defaulted. Kept as an explicit second clock so the cells can assert the two
        /// DISAGREE on a hold-carrying unit - without that half an equality cell would pass
        /// on a build that still dropped the arguments.</summary>
        private static bool ScheduleOnlyClock(
            GhostPlaybackLogic.LoopUnit unit, double currentUT,
            out double loopUT, out long cycleIndex, out bool tail)
        {
            return GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                out loopUT, out cycleIndex, out tail,
                schedule: unit.RelaunchSchedule,
                loiterCuts: unit.LoiterCuts);
        }

        private static void AssertSameClock(GhostPlaybackLogic.LoopUnit unit, double currentUT)
        {
            bool deliveryOk = RouteLoopClock.TryGetRouteLoopState(
                unit, currentUT, out double deliveryUT, out long deliveryCycle, out bool deliveryTail);
            bool renderOk = RenderClock(
                unit, currentUT, out double renderUT, out long renderCycle, out bool renderTail);

            Assert.Equal(renderOk, deliveryOk);
            Assert.Equal(renderUT, deliveryUT, 6);
            Assert.Equal(renderCycle, deliveryCycle);
            Assert.Equal(renderTail, deliveryTail);
        }

        // ==================================================================
        // 1. HOLD PRESENT - the direction the entry named
        // ==================================================================

        // catches: the delivery clock defaulting arrivalHoldSeconds / arrivalHoldAtUT. On this
        // unit the held clock lags the hold-free one by up to the full 200 s hold, so a dropped
        // argument shows up as a plain value mismatch at every sample inside and after the hold.
        [Fact]
        public void ArrivalHoldUnit_DeliveryClockEqualsTheRenderClock()
        {
            var unit = BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                arrivalHoldSeconds: HoldSeconds, arrivalHoldAtUT: HoldAtUT);

            // Before the boundary, inside the hold, after it, and past the effective span.
            foreach (double ut in new[] { 100.0, 500.0, 650.0, 700.0, 800.0, 900.0, 1150.0, 1300.0 })
                AssertSameClock(unit, ut);
        }

        // catches: an equality cell that passes because BOTH sides dropped the arguments. The
        // pre-fix clock and the fixed one must actually disagree here, and by the hold.
        [Fact]
        public void ArrivalHoldUnit_DeliveryClockDivergesFromTheScheduleOnlyClock()
        {
            var unit = BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                arrivalHoldSeconds: HoldSeconds, arrivalHoldAtUT: HoldAtUT);

            Assert.True(RouteLoopClock.TryGetRouteLoopState(
                unit, 700.0, out double deliveryUT, out _, out _));
            Assert.True(ScheduleOnlyClock(unit, 700.0, out double preFixUT, out _, out _));

            // Held at the recorded boundary vs. the un-held identity reading.
            Assert.Equal(600.0, deliveryUT, 6);
            Assert.Equal(700.0, preFixUT, 6);
            Assert.NotEqual(preFixUT, deliveryUT);
        }

        // catches: the launch-alignment trio (rotation period / engaged flag / SOI exit) being
        // dropped while the arrival hold is threaded. The zero-slack fixture engages the
        // borrow-at-launch advance on EVERY cycle, so the two clocks differ at every sample.
        [Fact]
        public void LaunchHoldUnit_DeliveryClockEqualsTheRenderClock_AndDivergesFromScheduleOnly()
        {
            var unit = BuildUnit(
                LaunchSpanStart, LaunchSpanEnd, LaunchCadence, LaunchAnchor,
                launchBodyRotationPeriodSeconds: LaunchRotationPeriod,
                launchHoldEngaged: true,
                recordedSoiExitUT: LaunchSoiExit);

            foreach (double ut in new[] { 400.0, 700.0, 1100.0, 1600.0, 2400.0, 3300.0 })
                AssertSameClock(unit, ut);

            // At least one sample must actually separate the fixed clock from the pre-fix one,
            // or this cell would be green on a build that forwards nothing.
            bool anyDivergence = false;
            foreach (double ut in new[] { 400.0, 700.0, 1100.0, 1600.0, 2400.0, 3300.0 })
            {
                RouteLoopClock.TryGetRouteLoopState(unit, ut, out double fixedUT, out _, out _);
                ScheduleOnlyClock(unit, ut, out double preFixUT, out _, out _);
                if (Math.Abs(fixedUT - preFixUT) > Tol) anyDivergence = true;
            }
            Assert.True(anyDivergence,
                "the launch-hold fixture must separate the two clocks somewhere, or the equality " +
                "above proves nothing about the launch-alignment arguments");
        }

        // ==================================================================
        // 2. HOLD ABSENT - the mirror direction
        // ==================================================================

        // catches: a fix that changed what a v0 same-body route computes. A faithful backing
        // mission leaves every hold field at its default, so all three clocks must agree
        // EXACTLY - the pre-fix reading included.
        [Fact]
        public void HoldFreeUnit_DeliveryRenderAndScheduleOnlyClocksAllAgree()
        {
            var unit = BuildUnit(HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor);

            foreach (double ut in new[] { 0.0, 100.0, 500.0, 999.0, 1000.0, 1500.0, 2100.0, 3700.0 })
            {
                AssertSameClock(unit, ut);

                bool deliveryOk = RouteLoopClock.TryGetRouteLoopState(
                    unit, ut, out double deliveryUT, out long deliveryCycle, out bool deliveryTail);
                bool preFixOk = ScheduleOnlyClock(
                    unit, ut, out double preFixUT, out long preFixCycle, out bool preFixTail);
                Assert.Equal(preFixOk, deliveryOk);
                Assert.Equal(preFixUT, deliveryUT, 6);
                Assert.Equal(preFixCycle, deliveryCycle);
                Assert.Equal(preFixTail, deliveryTail);
            }
        }

        // ==================================================================
        // 3. CarriesHoldArgs / DescribeHoldArgs - the log's own decisions
        // ==================================================================

        [Fact]
        public void CarriesHoldArgs_IsFalseOnAFaithfulUnit_AndTrueOnEachHoldFieldAlone()
        {
            Assert.False(RouteLoopClock.CarriesHoldArgs(
                BuildUnit(HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor)));

            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor, arrivalHoldSeconds: 5.0)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor, arrivalHoldAtUT: 5.0)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor, arrivalAlignPeriodSeconds: 5.0)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                launchBodyRotationPeriodSeconds: 5.0)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor, launchHoldEngaged: true)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor, recordedSoiExitUT: 5.0)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                arrivalJointSecondaryPeriodSeconds: 5.0)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                arrivalJointSecondaryToleranceSeconds: 5.0)));
            Assert.True(RouteLoopClock.CarriesHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                arrivalJointMaxWholeHoldPeriods: 2)));
        }

        [Fact]
        public void HoldArgsChangeKey_IsTheConstantNone_OnlyWhenNothingIsCarried()
        {
            Assert.Equal("none", RouteLoopClock.HoldArgsChangeKey(
                BuildUnit(HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor)));

            var held = BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                arrivalHoldSeconds: HoldSeconds, arrivalHoldAtUT: HoldAtUT);
            Assert.Equal(RouteLoopClock.DescribeHoldArgs(held), RouteLoopClock.HoldArgsChangeKey(held));
            Assert.NotEqual("none", RouteLoopClock.HoldArgsChangeKey(held));
        }

        // catches: a culture-dependent formatter. The xUnit host runs under the OS culture and
        // this string is read by a test, so it must be InvariantCulture at the production site
        // (CLAUDE.md's serialization / test-read rule).
        [Fact]
        public void DescribeHoldArgs_IsInvariantUnderAComma_DecimalCulture()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string described = RouteLoopClock.DescribeHoldArgs(BuildUnit(
                    HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor,
                    arrivalHoldSeconds: 200.5, arrivalHoldAtUT: 600.25,
                    launchHoldEngaged: true, arrivalJointMaxWholeHoldPeriods: 3));

                Assert.Equal(
                    "arrivalHoldSeconds=200.5 arrivalHoldAtUT=600.25 arrivalHoldAlignPeriod=NaN "
                    + "launchBodyRotationPeriod=NaN launchHoldEngaged=1 soiExitAtUT=NaN "
                    + "arrivalJointSecondaryPeriod=NaN arrivalJointSecondaryTolerance=NaN "
                    + "arrivalJointMaxWholeHoldPeriods=3",
                    described);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        // ==================================================================
        // 4. COMPLETENESS - the gate the equalities CANNOT be
        // ==================================================================

        // catches: a TWELFTH optional argument on the span clock, defaulted at every
        // forwarding site.
        //
        // WHY THE EQUALITY CELLS DO NOT CATCH THAT. There is no shared forwarding helper:
        // the span clock's optional surface is hand-copied at four production sites
        // (GhostPlaybackEngine twice, ReaimPlaybackResolver with a deliberate
        // schedule: null, and RouteLoopClock.TryGetRouteLoopState) and a fifth time by
        // RenderClock above. A new optional that nobody forwards therefore defaults on BOTH
        // sides of every equality and the whole class stays green while the delivery clock
        // and the render clock have silently stopped being the unit's whole surface. This
        // cell is the completeness half: it reads the span clock's signature by reflection
        // and pins the optional-parameter NAMES against the set this file's contract says is
        // forwarded - the schedule, the loiter cuts, and exactly the nine tokens
        // DescribeHoldArgs emits (the token names are the parameter names, deliberately, so
        // the log line and the signature cannot drift apart either).
        //
        // Failing here is not a defect by itself: it means someone added an argument and
        // must decide, per site, whether to forward it - then extend this list.
        [Fact]
        public void SpanClockOptionalArguments_AreExactlyTheForwardedSet()
        {
            System.Reflection.MethodInfo clock = typeof(GhostPlaybackLogic).GetMethod(
                "TryComputeSpanLoopUT",
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            Assert.NotNull(clock);

            var optionalNames = new System.Collections.Generic.List<string>();
            foreach (System.Reflection.ParameterInfo p in clock.GetParameters())
            {
                if (p.IsOptional) optionalNames.Add(p.Name);
            }

            // The two structural arguments Phase 6 already threaded, then the nine hold /
            // launch-alignment ones, in the order the span clock declares them.
            var expected = new System.Collections.Generic.List<string>
            {
                "schedule",
                "loiterCuts",
                "arrivalHoldSeconds",
                "arrivalHoldAtUT",
                "arrivalHoldAlignPeriod",
                "launchBodyRotationPeriod",
                "launchHoldEngaged",
                "soiExitAtUT",
                "arrivalJointSecondaryPeriod",
                "arrivalJointSecondaryTolerance",
                "arrivalJointMaxWholeHoldPeriods",
            };
            Assert.Equal(expected, optionalNames);

            // And the nine hold tokens the log line emits ARE those nine parameter names,
            // so DescribeHoldArgs cannot describe a clock the seam does not run.
            string[] pairs = RouteLoopClock.DescribeHoldArgs(BuildUnit(
                HoldSpanStart, HoldSpanEnd, HoldCadence, HoldAnchor)).Split(' ');
            var described = new System.Collections.Generic.List<string>();
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                Assert.True(eq > 0, "hold-arg token is not key=value: " + pair);
                described.Add(pair.Substring(0, eq));
            }
            Assert.Equal(expected.GetRange(2, expected.Count - 2), described);
        }
    }
}
