using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // M4b phasing-loiter knob (docs/dev/plans/mission-loiter-knob.md): the per-cycle keepRevs
    // re-timer. Covers the pure layers end to end: run detection (DetectRuns), the shiftable-group
    // schedule scan (TryFindNextScheduleK knob overload), per-launch timing materialization +
    // non-overlap spacing under extension (MissionRelaunchSchedule), the span-clock schedule branch
    // (cut skip, extension wrap sawtooth, per-launch effective-span tail), the constraint partition
    // rules in TryBuildRelaunchSchedule, the builder-side engagement rules
    // (BuildPhasingKnobInput), and the route dock-crossing sawtooth safety.
    [Collection("Sequential")]
    public class MissionLoiterKnobTests : IDisposable
    {
        private const double MuKerbin = 3.5316e12;
        private const double LkoA = 700000.0;
        private static readonly double LkoT = ReaimLoiterCompressor.OrbitalPeriod(LkoA, MuKerbin);

        private static Func<string, double> Mu => b => b == "Kerbin" ? MuKerbin : double.NaN;

        public MissionLoiterKnobTests()
        {
            MissionPeriodicity.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = true;
        }

        public void Dispose()
        {
            MissionPeriodicity.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        private static OrbitSegment Seg(string body, double start, double end, double a, bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = body, startUT = start, endUT = end, semiMajorAxis = a,
                eccentricity = 0.01, isPredicted = predicted
            };
        }

        private sealed class KnobFakeBodyInfo : IBodyInfo
        {
            public double RotationPeriod(string bodyName) => bodyName == "Kerbin" ? 21600.0 : double.NaN;
            public double OrbitPeriod(string bodyName) => double.NaN;
            public string ReferenceBodyName(string bodyName) => null;
            public double SoiRadius(string bodyName) => 8.4e7;
            public double OrbitalVelocity(string bodyName) => 9285.0;
            public double GravParameter(string bodyName) => bodyName == "Kerbin" ? MuKerbin : double.NaN;
            public bool TryGetVesselOrbit(
                uint vesselPid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            {
                periodSeconds = double.NaN;
                orbitBodyName = null;
                return false;
            }
        }

        // ─── DetectRuns ─────────────────────────────────────────────────────────

        [Fact]
        public void DetectRuns_ExposesRunMetadata()
        {
            double dur = 5.0 * LkoT + 10.0;
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0.0, 100.0, LkoA * 0.5),          // sub-period pass: not a run
                Seg("Kerbin", 100.0, 100.0 + dur, LkoA),        // 5-rev loiter
            };

            var runs = ReaimLoiterCompressor.DetectRuns(segs, Mu);

            Assert.Single(runs);
            Assert.Equal(100.0, runs[0].StartUT, 3);
            Assert.Equal(100.0 + dur, runs[0].EndUT, 3);
            Assert.Equal(LkoT, runs[0].PeriodSeconds, 3);
            Assert.Equal(5, runs[0].WholeRevs);
            Assert.Equal("Kerbin", runs[0].BodyName);
        }

        [Fact]
        public void ComputeCuts_DelegationMatchesRunMetadata()
        {
            double dur = 5.0 * LkoT + 10.0;
            var segs = new List<OrbitSegment> { Seg("Kerbin", 100.0, 100.0 + dur, LkoA) };

            var runs = ReaimLoiterCompressor.DetectRuns(segs, Mu);
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);

            Assert.Single(cuts);
            Assert.Equal(runs[0].StartUT, cuts[0].StartUT, 6);
            Assert.Equal((runs[0].WholeRevs - 1) * runs[0].PeriodSeconds, cuts[0].LengthSeconds, 6);
        }

        // ─── TryFindNextScheduleK: knob overload ────────────────────────────────

        [Fact]
        public void ScheduleK_EmptyShiftGroup_MatchesBaseOverload()
        {
            var periods = new[] { 2000.0, 3700.0 };
            var tols = new[] { 5.0, 9.0 };

            bool okBase = MissionPeriodicity.TryFindNextScheduleK(
                21600.0, periods, tols, 1, 64,
                out long kBase, out double rBase, out bool wBase);
            bool okKnob = MissionPeriodicity.TryFindNextScheduleK(
                21600.0, periods, tols, null, null, 0.0, 0, 0, 1, 64,
                out long kKnob, out long dKnob, out double rKnob, out bool wKnob);

            Assert.True(okBase);
            Assert.True(okKnob);
            Assert.Equal(kBase, kKnob);
            Assert.Equal(0, dKnob);
            Assert.Equal(rBase, rKnob, 9);
            Assert.Equal(wBase, wKnob);
        }

        [Fact]
        public void ScheduleK_KnobUnlocksWindowTheBaseScanRejects()
        {
            // Station P=2000 tol 5.6s; pad anchor 21600. At k=1 the unshifted error is
            // min(1600, 400) = 400s (rejected). With T_park=1900 the |d|-ascending enumeration
            // reaches d=-4: 21600 - 7600 = 14000 = 7*2000 exactly -> residual 0, accepted at k=1.
            var shiftP = new[] { 2000.0 };
            var shiftTol = new[] { 5.6 };

            bool okBase = MissionPeriodicity.TryFindNextScheduleK(
                21600.0, shiftP, shiftTol, 1, 4,
                out long kBase, out _, out bool wBase);
            bool okKnob = MissionPeriodicity.TryFindNextScheduleK(
                21600.0, null, null, shiftP, shiftTol, 1900.0, -9, 10, 1, 4,
                out long kKnob, out long d, out double resid, out bool within);

            Assert.True(okBase);
            Assert.False(wBase); // base scan: no within-tolerance k in the window
            Assert.True(okKnob);
            Assert.True(within);
            Assert.Equal(1, kKnob);
            Assert.Equal(-4, d);
            Assert.True(resid < 1e-6);
        }

        [Fact]
        public void ScheduleK_MagnitudeTie_PrefersTheCut()
        {
            // delta = 50; shift P=100, step 50: d=-1 and d=+1 both land exactly on a multiple.
            // The cut (d=-1, shorter cycle) must win the magnitude tie.
            bool ok = MissionPeriodicity.TryFindNextScheduleK(
                50.0, null, null, new[] { 100.0 }, new[] { 0.5 }, 50.0, -5, 5, 1, 4,
                out long k, out long d, out _, out bool within);

            Assert.True(ok);
            Assert.True(within);
            Assert.Equal(1, k);
            Assert.Equal(-1, d);
        }

        [Fact]
        public void ScheduleK_BoundsRespected_OuterScanCompensates()
        {
            // Same geometry but cutting is forbidden (shiftMin=0): d=+1 fits at k=1. And with the
            // whole shift range pinned to d=0, k=1 (err 50) fails but k=2 (delta=100, err 0)
            // accepts - the outer scan compensates, the never-accumulate shape.
            bool okExtend = MissionPeriodicity.TryFindNextScheduleK(
                50.0, null, null, new[] { 100.0 }, new[] { 0.5 }, 50.0, 0, 5, 1, 4,
                out long kE, out long dE, out _, out bool withinE);
            bool okPinned = MissionPeriodicity.TryFindNextScheduleK(
                50.0, null, null, new[] { 100.0 }, new[] { 0.5 }, 50.0, 0, 0, 1, 4,
                out long kP, out long dP, out _, out bool withinP);

            Assert.True(okExtend);
            Assert.True(withinE);
            Assert.Equal(1, kE);
            Assert.Equal(1, dE);
            Assert.True(okPinned);
            Assert.True(withinP);
            Assert.Equal(2, kP);
            Assert.Equal(0, dP);
        }

        [Fact]
        public void ScheduleK_NothingReachable_BoundedBestEarlierK()
        {
            // Incommensurate-ish: nothing within the tiny tolerance in a 2-step window; the
            // bounded-best is the k with the smaller min-over-d residual, ties to the earlier k.
            bool ok = MissionPeriodicity.TryFindNextScheduleK(
                1000.0, null, null, new[] { 633.0 }, new[] { 0.001 }, 251.0, -1, 1, 1, 2,
                out long k, out long d, out double resid, out bool within);

            Assert.True(ok);
            Assert.False(within);
            Assert.True(resid > 0.001);
            Assert.InRange(k, 1, 2);
            Assert.InRange(d, -1, 1);
        }

        // ─── MissionRelaunchSchedule: per-launch timing + spacing ───────────────

        // Shared geometry for the schedule/clock tests: ut0=0, anchor 1000s; one shiftable
        // constraint P=600 tol 1s; phasing run [100, 1350] T_park=250 R=5 (span 2000).
        //  k=1: 1000 + 250d = 0 (mod 600) at d=-4 -> cut 1000s, keptRevs 1, effSpan 1000.
        //  k=2: 2000 + 250d = 0 (mod 600) at d=+4 -> extension 1000s, keptRevs 9, effSpan 3000.
        //  k=5: 5000 + 250d = 0 (mod 600) at d=+4 (same shape as k=2).
        private static PhasingKnobConfig MakeKnobConfig()
        {
            return new PhasingKnobConfig
            {
                RunStartUT = 100.0,
                RunEndUT = 1350.0,
                PeriodSeconds = 250.0,
                RecordedRevs = 5,
                StaticCuts = new List<GhostPlaybackLogic.LoopCut>(),
                SpanSeconds = 2000.0,
                ShiftPeriods = new[] { 600.0 },
                ShiftTolerances = new[] { 1.0 },
                ShiftMin = -4,
                ShiftMax = 10,
            };
        }

        [Fact]
        public void Schedule_MaterializesPerLaunchTiming_CutAndExtensionShapes()
        {
            var sched = new MissionRelaunchSchedule(
                0.0, 1000.0, new double[0], new double[0], 0.0,
                MissionPeriodicity.ScheduleLookaheadMultiples, 0.0, MakeKnobConfig());

            Assert.True(sched.HasPhasingKnob);
            Assert.Equal(1000.0, sched.FirstLaunchUT, 6);

            Assert.True(sched.TryGetLaunchTiming(0, out LaunchTimingEntry t0));
            Assert.Equal(1, t0.KeptRevs);                     // d = -4
            Assert.True(t0.WithinTolerance);
            Assert.Single(t0.Cuts);
            Assert.Equal(100.0, t0.Cuts[0].StartUT, 6);
            Assert.Equal(1000.0, t0.Cuts[0].LengthSeconds, 6); // 4 revs * 250s
            Assert.Equal(0.0, t0.ExtensionSeconds, 6);
            Assert.Equal(1000.0, t0.EffectiveSpanSeconds, 6);  // 2000 - 1000

            Assert.True(sched.TryGetLaunchTiming(1, out LaunchTimingEntry t1));
            Assert.Equal(9, t1.KeptRevs);                      // d = +4
            Assert.True(t1.WithinTolerance);
            Assert.Empty(t1.Cuts);
            Assert.Equal(1000.0, t1.ExtensionSeconds, 6);
            Assert.Equal(1100.0, t1.ExtensionWrapStartUT, 6);  // RunEnd - T_park
            Assert.Equal(250.0, t1.ExtensionWrapPeriod, 6);
            Assert.Equal(3000.0, t1.EffectiveSpanSeconds, 6);  // 2000 + 1000
        }

        [Fact]
        public void Schedule_SpacingUnderExtension_NextLaunchClearsEffectiveSpan()
        {
            var sched = new MissionRelaunchSchedule(
                0.0, 1000.0, new double[0], new double[0], 0.0,
                MissionPeriodicity.ScheduleLookaheadMultiples, 0.0, MakeKnobConfig());

            // Launch 1 (UT 2000) extends to effSpan 3000, so launch 2 must be >= 5000 even with
            // minSpacing 0 - the per-launch spacing rule, not the player throttle.
            Assert.True(sched.TryResolveActiveLaunch(1500.0, out double l0, out long i0));
            Assert.Equal(1000.0, l0, 6);
            Assert.Equal(0, i0);
            Assert.True(sched.TryResolveActiveLaunch(2500.0, out double l1, out long i1));
            Assert.Equal(2000.0, l1, 6);
            Assert.Equal(1, i1);
            Assert.True(sched.TryResolveActiveLaunch(5500.0, out double l2, out long i2));
            Assert.Equal(5000.0, l2, 6);
            Assert.Equal(2, i2);
            Assert.True(sched.TryGetLaunchTiming(2, out LaunchTimingEntry t2));
            Assert.Equal(9, t2.KeptRevs); // k=5 resolves d=+4 too
        }

        [Fact]
        public void Schedule_KnobLess_HasNoTiming()
        {
            var sched = new MissionRelaunchSchedule(
                0.0, 1000.0, new[] { 600.0 }, new[] { 1.0 }, 0.0,
                MissionPeriodicity.ScheduleLookaheadMultiples, 0.0);

            Assert.False(sched.HasPhasingKnob);
            Assert.False(sched.TryGetLaunchTiming(0, out _));
        }

        // ─── Span clock: schedule branch with per-launch timing ─────────────────

        // minSpacing 4000 leaves an observable tail after launch 0: launches land at 1000
        // (d=-4, cut) and 5000 (k=5, d=+4, extension).
        private static MissionRelaunchSchedule MakeClockSchedule()
        {
            return new MissionRelaunchSchedule(
                0.0, 1000.0, new double[0], new double[0], 0.0,
                MissionPeriodicity.ScheduleLookaheadMultiples, 4000.0, MakeKnobConfig());
        }

        private static double ClockLoopUT(
            MissionRelaunchSchedule sched, double currentUT,
            out long cycleIndex, out bool tail)
        {
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, 1000.0, 0.0, 2000.0, 2000.0,
                out double loopUT, out cycleIndex, out tail, schedule: sched);
            Assert.True(ok);
            return loopUT;
        }

        [Fact]
        public void SpanClock_PerLaunchCut_SkipsTheCutWindow()
        {
            var sched = MakeClockSchedule();

            // Launch 0 at UT 1000, cut [100, 1100). Before the cut: identity. After: skipped.
            Assert.Equal(50.0, ClockLoopUT(sched, 1050.0, out long c0, out bool tail0), 6);
            Assert.Equal(0, c0);
            Assert.False(tail0);
            Assert.Equal(1150.0, ClockLoopUT(sched, 1150.0, out _, out _), 6); // 150 -> +1000
        }

        [Fact]
        public void SpanClock_PerLaunchTail_ParksAtSpanEndAtEffectiveSpan()
        {
            var sched = MakeClockSchedule();

            // Launch 0's effective span is 1000 (cut 4 revs); UT 2500 is phase 1500 -> parked tail
            // at spanEnd, well before the next launch at 5000.
            double loopUT = ClockLoopUT(sched, 2500.0, out long cycle, out bool tail);
            Assert.True(tail);
            Assert.Equal(2000.0, loopUT, 6);
            Assert.Equal(0, cycle);
        }

        [Fact]
        public void SpanClock_Extension_WrapsFinalRevThenResumes()
        {
            var sched = MakeClockSchedule();

            // Launch 1 at UT 5000, extension 1000s wrapping [1100, 1350) (T_park 250).
            // Before the wrap window: identity.
            Assert.Equal(1050.0, ClockLoopUT(sched, 6050.0, out long c1, out bool t1), 6);
            Assert.Equal(1, c1);
            Assert.False(t1);
            // Inside the wrap: phase 1150 -> 1100 + (50 % 250) = 1150.
            Assert.Equal(1150.0, ClockLoopUT(sched, 6150.0, out _, out _), 6);
            // Sawtooth: phase 1450 -> 1100 + (350 % 250) = 1200 (EARLIER than the prior sample).
            Assert.Equal(1200.0, ClockLoopUT(sched, 6450.0, out _, out _), 6);
            // After the extension: phase 2150 -> 2150 - 1000 = 1150 (the final rev plays for real).
            Assert.Equal(1150.0, ClockLoopUT(sched, 7150.0, out _, out _), 6);
            // Near the per-launch effective span end (3000): phase 2999 -> 1999.
            Assert.Equal(1999.0, ClockLoopUT(sched, 7999.0, out _, out bool tEnd), 6);
            Assert.False(tEnd);
        }

        [Fact]
        public void ApplyLoiterExtensionToPhase_Seams()
        {
            // insertPos 1100, ext 1000 (4 revs of 250).
            Assert.Equal(900.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(900.0, 1100.0, 1000.0, 250.0), 9);
            Assert.Equal(1100.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(1100.0, 1100.0, 1000.0, 250.0), 9);
            Assert.Equal(1349.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(1349.0, 1100.0, 1000.0, 250.0), 9);
            Assert.Equal(1100.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(1350.0, 1100.0, 1000.0, 250.0), 9); // wrap
            Assert.Equal(1100.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(2100.0, 1100.0, 1000.0, 250.0), 9); // exit seam
            Assert.Equal(1500.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(2500.0, 1100.0, 1000.0, 250.0), 9); // after
            // Identity for degenerate inputs.
            Assert.Equal(1500.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(1500.0, 1100.0, 0.0, 250.0), 9);
            Assert.Equal(1500.0, GhostPlaybackLogic.ApplyLoiterExtensionToPhase(1500.0, double.NaN, 1000.0, 250.0), 9);
        }

        // ─── TryBuildRelaunchSchedule: partition rules ──────────────────────────

        private static PhasingKnobInput MakeKnobInput()
        {
            return new PhasingKnobInput
            {
                RunStartUT = 100.0,
                RunEndUT = 1350.0,
                PeriodSeconds = 250.0,
                RecordedRevs = 5,
                StaticCuts = new List<GhostPlaybackLogic.LoopCut>(),
                SpanSeconds = 2000.0,
            };
        }

        private static List<PhaseConstraint> StationConstraints(
            double padOffset = 0.0, double stationOffset = 1500.0)
        {
            return new List<PhaseConstraint>
            {
                new PhaseConstraint
                {
                    Kind = ConstraintKind.Rotation, BodyName = "Kerbin",
                    PeriodSeconds = 21600.0, PhaseOffsetSeconds = padOffset,
                },
                new PhaseConstraint
                {
                    Kind = ConstraintKind.VesselOrbital, BodyName = "Kerbin",
                    PeriodSeconds = 2000.0, PhaseOffsetSeconds = stationOffset,
                    AnchorVesselPid = 42,
                },
            };
        }

        [Fact]
        public void BuildSchedule_StationShape_EngagesKnob()
        {
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                StationConstraints(), Support.Supported, 0.0, 0.0, new KnobFakeBodyInfo(),
                out MissionRelaunchSchedule sched, 0.0, "Kerbin",
                TransitedBodyRotationMode.Tight, MakeKnobInput());

            Assert.True(ok);
            Assert.True(sched.HasPhasingKnob);
        }

        [Fact]
        public void BuildSchedule_AnchorEventInsideRun_DisengagesKnob_Rule3()
        {
            // Pad reference event at UT 200 sits inside the phasing run [100, 1350]: the rev
            // shift would move a pinned-exact anchor, so the knob must disengage; the schedule
            // itself still builds (fail closed to today's behavior).
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                StationConstraints(padOffset: 200.0), Support.Supported, 0.0, 0.0,
                new KnobFakeBodyInfo(), out MissionRelaunchSchedule sched, 0.0, "Kerbin",
                TransitedBodyRotationMode.Tight, MakeKnobInput());

            Assert.True(ok);
            Assert.False(sched.HasPhasingKnob);
        }

        [Fact]
        public void BuildSchedule_NoEventAfterRun_DisengagesKnob_Rule4()
        {
            // Station reference event at UT 50 is BEFORE the run end: nothing is shiftable,
            // d has nothing to serve.
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                StationConstraints(stationOffset: 50.0), Support.Supported, 0.0, 0.0,
                new KnobFakeBodyInfo(), out MissionRelaunchSchedule sched, 0.0, "Kerbin",
                TransitedBodyRotationMode.Tight, MakeKnobInput());

            Assert.True(ok);
            Assert.False(sched.HasPhasingKnob);
        }

        [Fact]
        public void BuildSchedule_NullKnobInput_BehavesAsToday()
        {
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                StationConstraints(), Support.Supported, 0.0, 0.0, new KnobFakeBodyInfo(),
                out MissionRelaunchSchedule sched, 0.0, "Kerbin",
                TransitedBodyRotationMode.Tight);

            Assert.True(ok);
            Assert.False(sched.HasPhasingKnob);
        }

        // ─── BuildPhasingKnobInput: builder-side rules ──────────────────────────

        private static ConstraintExtraction MakeExtraction(
            double ut0 = 0.0, double stationOffset = 6000.0)
        {
            return new ConstraintExtraction
            {
                Constraints = new List<PhaseConstraint>
                {
                    new PhaseConstraint
                    {
                        Kind = ConstraintKind.VesselOrbital, BodyName = "Kerbin",
                        PeriodSeconds = 2000.0, PhaseOffsetSeconds = stationOffset,
                        AnchorVesselPid = 42,
                    },
                },
                Support = Support.Supported,
                LaunchBodyName = "Kerbin",
                UT0 = ut0,
            };
        }

        private static Recording OwnerWithLoiter(out double runStart, out double runEnd, out long revs)
        {
            runStart = 100.0;
            revs = 5;
            double dur = revs * LkoT + 10.0;
            runEnd = runStart + dur;
            var rec = new Recording();
            rec.OrbitSegments.Add(Seg("Kerbin", runStart, runEnd, LkoA));
            return rec;
        }

        [Fact]
        public void KnobInput_HappyPath_PicksRunBeforeRendezvous()
        {
            Recording owner = OwnerWithLoiter(out double runStart, out double runEnd, out long revs);
            double spanEnd = runEnd + 2000.0;

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { owner }, new[] { 0 }, 0,
                MakeExtraction(stationOffset: runEnd + 500.0),
                0.0, spanEnd, new KnobFakeBodyInfo(), "test");

            Assert.NotNull(input);
            Assert.Equal(runStart, input.RunStartUT, 3);
            Assert.Equal(runEnd, input.RunEndUT, 3);
            Assert.Equal(revs, input.RecordedRevs);
            Assert.Equal(LkoT, input.PeriodSeconds, 3);
            Assert.Empty(input.StaticCuts);
            Assert.Equal(spanEnd, input.SpanSeconds, 3);
        }

        [Fact]
        public void KnobInput_RendezvousBeforeRunEnd_NoPhasingRun_Rule2()
        {
            Recording owner = OwnerWithLoiter(out _, out double runEnd, out _);

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { owner }, new[] { 0 }, 0,
                MakeExtraction(stationOffset: runEnd - 100.0), // rendezvous DURING the loiter
                0.0, runEnd + 2000.0, new KnobFakeBodyInfo(), "test");

            Assert.Null(input);
        }

        [Fact]
        public void KnobInput_UT0SpanStartMismatch_Disengages_Rule5()
        {
            Recording owner = OwnerWithLoiter(out _, out double runEnd, out _);

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { owner }, new[] { 0 }, 0,
                MakeExtraction(ut0: 5.0, stationOffset: runEnd + 500.0),
                0.0, runEnd + 2000.0, new KnobFakeBodyInfo(), "test");

            Assert.Null(input);
        }

        [Fact]
        public void KnobInput_BodyChangeGuard_RunAfterSoiExit_Disengages()
        {
            // The only loiter run sits AFTER the first body change (a Mun parking orbit): the
            // SOI boundary guards it, no phasing run remains on the launch body.
            var rec = new Recording();
            rec.OrbitSegments.Add(Seg("Kerbin", 0.0, 100.0, LkoA * 0.5)); // sub-period launch arc
            rec.OrbitSegments.Add(Seg("Mun", 200.0, 200.0 + 5.0 * LkoT, LkoA));

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { rec }, new[] { 0 }, 0,
                MakeExtraction(stationOffset: 20000.0),
                0.0, 30000.0, new KnobFakeBodyInfo(), "test");

            Assert.Null(input);
        }

        [Fact]
        public void KnobInput_EarlierRuns_BecomeStaticCuts()
        {
            // Two compressible launch-body runs before the rendezvous: the LAST is the phasing
            // run; the earlier one gets a static keepRevs=1 cut.
            double run1Start = 100.0;
            double run1End = run1Start + 3.0 * LkoT + 5.0;
            // A sub-period segment on a clearly different orbit (a-step > 5%) separates the runs.
            double gapEnd = run1End + 200.0;
            double run2Start = gapEnd + 10.0;
            double run2End = run2Start + 5.0 * LkoT + 5.0;
            var rec = new Recording();
            rec.OrbitSegments.Add(Seg("Kerbin", run1Start, run1End, LkoA));
            rec.OrbitSegments.Add(Seg("Kerbin", run1End, gapEnd, LkoA * 1.2));
            rec.OrbitSegments.Add(Seg("Kerbin", run2Start, run2End, LkoA));

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { rec }, new[] { 0 }, 0,
                MakeExtraction(stationOffset: run2End + 500.0),
                0.0, run2End + 2000.0, new KnobFakeBodyInfo(), "test");

            Assert.NotNull(input);
            Assert.Equal(run2Start, input.RunStartUT, 3);
            Assert.Equal(5, input.RecordedRevs);
            Assert.Single(input.StaticCuts);
            Assert.Equal(run1Start, input.StaticCuts[0].StartUT, 3);
            Assert.Equal(2.0 * LkoT, input.StaticCuts[0].LengthSeconds, 3); // 3 revs kept to 1
        }

        [Fact]
        public void KnobInput_RunStartsBeforeSpanStart_Disengages()
        {
            // A trimmed mission whose render window starts MID-loiter: the run is not entirely
            // in-span, so a cut would reference out-of-span UTs the clock cannot represent.
            // Fail closed (review finding).
            Recording owner = OwnerWithLoiter(out double runStart, out double runEnd, out _);
            double spanStart = runStart + 100.0; // window opens inside the loiter

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { owner }, new[] { 0 }, 0,
                MakeExtraction(ut0: spanStart, stationOffset: runEnd + 500.0 - spanStart),
                spanStart, runEnd + 2000.0, new KnobFakeBodyInfo(), "test");

            Assert.Null(input);
        }

        [Fact]
        public void KnobInput_PreSpanRun_NeverBecomesAStaticCut()
        {
            // Render window opens between two loiter runs: the pre-span run must be skipped (no
            // out-of-span static cut), the in-span run still engages as the phasing run.
            double run1Start = 100.0;
            double run1End = run1Start + 3.0 * LkoT + 5.0;
            double gapEnd = run1End + 200.0;
            double run2Start = gapEnd + 10.0;
            double run2End = run2Start + 5.0 * LkoT + 5.0;
            var rec = new Recording();
            rec.OrbitSegments.Add(Seg("Kerbin", run1Start, run1End, LkoA));
            rec.OrbitSegments.Add(Seg("Kerbin", run1End, gapEnd, LkoA * 1.2));
            rec.OrbitSegments.Add(Seg("Kerbin", run2Start, run2End, LkoA));
            double spanStart = run2Start - 5.0; // window opens after run 1, before run 2

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { rec }, new[] { 0 }, 0,
                MakeExtraction(ut0: spanStart, stationOffset: run2End + 500.0 - spanStart),
                spanStart, run2End + 2000.0, new KnobFakeBodyInfo(), "test");

            Assert.NotNull(input);
            Assert.Equal(run2Start, input.RunStartUT, 3);
            Assert.Empty(input.StaticCuts); // run 1 is out-of-span: skipped, not cut
        }

        [Fact]
        public void KnobInput_ChainMission_FindsLoiterInContinuationSegment()
        {
            // The 2026-06-11 playtest miss: a CHAIN mission's owner (the tree root, earliest
            // member) carries NO OrbitSegments; the parking loiter lives in the same-launch
            // continuation segment, and the dock-merged partner (different pid) is parked through
            // the window. The knob must find the continuation's run via the launch-identity gate
            // and must never let the partner's parked run drive cuts.
            string selfGuid = "11111111-1111-1111-1111-111111111111";
            var root = new Recording { VesselPersistentId = 100, RecordedVesselGuid = selfGuid };
            var cont = new Recording { VesselPersistentId = 100, RecordedVesselGuid = selfGuid };
            double runStart = 3900.0;
            double runEnd = runStart + 4.0 * LkoT - 50.0; // ~3.97 revs -> R=3
            cont.OrbitSegments.Add(Seg("Kerbin", 500.0, 600.0, LkoA * 0.5)); // ascent arc
            cont.OrbitSegments.Add(Seg("Kerbin", runStart, runEnd, LkoA));
            var partner = new Recording
            {
                VesselPersistentId = 999,
                RecordedVesselGuid = "22222222-2222-2222-2222-222222222222",
            };
            // The partner's parked run ENDS AFTER the craft's loiter but before the rendezvous:
            // without the identity gate it would be picked as the (wrong) phasing run.
            partner.OrbitSegments.Add(Seg("Kerbin", 200.0, runEnd + 2.0 * LkoT, LkoA * 1.001));
            double rendezvousUT = runEnd + 3.0 * LkoT;

            var committed = new List<Recording> { root, cont, partner };
            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                committed, new[] { 0, 1, 2 }, 0,
                MakeExtraction(stationOffset: rendezvousUT),
                0.0, rendezvousUT + 2000.0, new KnobFakeBodyInfo(), "test");

            Assert.NotNull(input);
            Assert.Equal(runStart, input.RunStartUT, 3);   // the continuation's run, not the partner's
            Assert.Equal(runEnd, input.RunEndUT, 3);
            Assert.Equal(3, input.RecordedRevs);
            Assert.Empty(input.StaticCuts);                 // the partner's run never becomes a cut
        }

        [Fact]
        public void KnobInput_OwnerWithoutLoiter_Null()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(Seg("Kerbin", 0.0, 100.0, LkoA * 0.5)); // sub-period only

            PhasingKnobInput input = MissionLoopUnitBuilder.BuildPhasingKnobInput(
                new List<Recording> { rec }, new[] { 0 }, 0, MakeExtraction(),
                0.0, 10000.0, new KnobFakeBodyInfo(), "test");

            Assert.Null(input);
        }

        // ─── Route dock-crossing sawtooth safety ────────────────────────────────

        [Fact]
        public void RouteDockCrossing_ExtensionSawtooth_FiresOncePerCycle()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, 0.0, 2000.0, 2000.0, 1000.0, 2000.0, null, null, null, null);
            double dockUT = 1500.0; // after the loiter (guard rule), inside the span

            // Cycle 1, samples during the extension wrap: loopUT bounces inside [1100, 1350),
            // always BEFORE the dock -> dockCycleIndex stays 0, no fire against lastObserved 0.
            Assert.False(Parsek.Logistics.RouteLoopClock.IsDockCrossing(
                unit, 1150.0, 1, dockUT, 0, out long dc1));
            Assert.Equal(0, dc1);
            Assert.False(Parsek.Logistics.RouteLoopClock.IsDockCrossing(
                unit, 1200.0, 1, dockUT, 0, out _)); // sawtooth backward jump: still no fire
            // After the wrap the clock reaches the dock: exactly one fire, then the snap guards.
            Assert.True(Parsek.Logistics.RouteLoopClock.IsDockCrossing(
                unit, 1600.0, 1, dockUT, 0, out long dc2));
            Assert.Equal(1, dc2);
            Assert.False(Parsek.Logistics.RouteLoopClock.IsDockCrossing(
                unit, 1700.0, 1, dockUT, 1, out _)); // already delivered
        }
    }
}
