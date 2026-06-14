using System;
using System.Collections.Generic;
using Xunit;
using Parsek;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // Re-aim Phase 4, implementation Phase 4 (P4): the PURE destination-loiter pre-landing trim
    // (DestinationLoiterTrim.SolveTrimAndHold) that replaces ArrivalHoldPlanner's destination-loiter
    // refusal. The joint (keepRevs, hold) solve picks the kept-revolution count that minimizes the
    // frozen-ghost arrival hold W, anchored at the deorbit. Gated shapes fail closed (None) so the
    // caller takes the byte-identical shipped path. No engine wiring exercised here.
    // docs/dev/plans/reaim-destination-loiter-pretrim.md.
    [Collection("Sequential")]
    public class DestinationLoiterTrimTests : IDisposable
    {
        public void Dispose() => ParsekLog.ResetTestOverrides();

        private const string Target = "Duna";
        private const string Launch = "Kerbin";

        private static ReaimLoiterCompressor.LoiterRun Run(
            string body, double startUT, double period, long wholeRevs)
            => new ReaimLoiterCompressor.LoiterRun
            {
                BodyName = body,
                StartUT = startUT,
                EndUT = startUT + wholeRevs * period,
                PeriodSeconds = period,
                WholeRevs = wholeRevs,
            };

        private static PhaseConstraint Rotation(string body, double period)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = false };

        private static DestinationConstraintExtractor.DestinationConstraintSet Landing(
            bool supported = true, bool hasStation = false, bool hasLandingRotation = true)
            => new DestinationConstraintExtractor.DestinationConstraintSet
            {
                Supported = supported,
                HasStation = hasStation,
                HasLandingRotation = hasLandingRotation,
            };

        // Only RotationPeriod / ScheduleToleranceSecondsFor reach bodyInfo; everything else is NaN/null.
        private sealed class TrimFake : IBodyInfo
        {
            public double RotationPeriod(string b) => b == Target ? 100.0 : double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) => b == Target ? "Sun" : null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => double.NaN;
            public bool TryGetVesselOrbit(uint pid, string guid, out double p, out string ob)
            { p = double.NaN; ob = null; return false; }
        }

        // Shared geometry for the "intermediate keepRevs minimizes the hold" scenario:
        //   T_rot=100, T_loiter=35, WholeRevs=5 -> run [200,375]; arrival 150; deorbit D=400;
        //   phaseAnchor=50, spanStart=0. W(r) = ((5-r)*35 - 50) mod 100:
        //   r1=90 r2=55 r3=20 r4=85 r5=50 -> min at r=3 (the algorithm walks 1->2->3, each strict).
        private static DestinationLoiterTrim.DestinationLoiterTrimResult SolveIntermediate(
            double spanSeconds = 1.0e6, int maxKeepRevs = 10,
            TransitedBodyRotationMode mode = TransitedBodyRotationMode.Loose)
        {
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            return DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(), Rotation(Target, 100.0), Launch, Target,
                recordedArrivalUT: 150.0, recordedDestSurfaceUT: 400.0, rotationPeriod: 100.0,
                phaseAnchorUT: 50.0, spanStartUT: 0.0, spanSeconds: spanSeconds, mode: mode,
                maxKeepRevs: maxKeepRevs, bodyInfo: new TrimFake());
        }

        [Fact]
        public void LongLoiter_IntermediateKeepRevs_MinimizesHold()
        {
            var r = SolveIntermediate();
            Assert.True(r.Applied);
            Assert.Equal(3, r.DestinationKeepRevs);
            Assert.Equal(5L, r.DestinationWholeRevs);
            Assert.True(r.HasDestinationCut);
            // The cut excises (5-3)=2 whole periods from the run START.
            Assert.Equal(200.0, r.DestinationCut.StartUT, 9);
            Assert.Equal(70.0, r.DestinationCut.LengthSeconds, 9); // 2 * 35
            Assert.Equal(20.0, r.HoldSeconds, 6);
            Assert.Equal(150.0, r.HoldAtUT, 9);   // inserted at the SOI entry, not the deorbit
            Assert.Equal(100.0, r.AlignPeriodSeconds, 9);
        }

        [Fact]
        public void Applied_AlignmentInvariant_DeorbitLandsOnRecordedPhase()
        {
            // After the solve, liveSurface(bestR) + HoldSeconds == D (mod T_rot): the hold finishes
            // exactly aligning the deorbit. Re-derive liveSurface from the chosen cut.
            var r = SolveIntermediate();
            const double D = 400.0, phaseAnchor = 50.0, spanStart = 0.0, tRot = 100.0;
            var cuts = new List<GhostPlaybackLogic.LoopCut>();
            if (r.HasDestinationCut) cuts.Add(r.DestinationCut);
            double liveSurface = phaseAnchor + (GhostPlaybackLogic.CompressSpanUT(D, cuts) - spanStart);
            double aligned = ((liveSurface + r.HoldSeconds) - D) % tRot;
            if (aligned < 0) aligned += tRot;
            Assert.Equal(0.0, aligned, 6);
        }

        [Fact]
        public void NoDestinationLoiter_ReturnsNone()
        {
            // Only a launch-side run (Kerbin, ends before arrival) -> nothing to trim.
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Launch, 10.0, 20.0, 4) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(), Rotation(Target, 100.0), Launch, Target,
                150.0, 400.0, 100.0, 50.0, 0.0, 1.0e6, TransitedBodyRotationMode.Loose, 10, new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void ShortLoiter_OneRev_ReturnsNone()
        {
            // A 1-rev "loiter" produces no cut even at keepRevs=1, so the shipped hold already aligns
            // it (P4 only engages when the run WOULD produce a cut, i.e. >= 2 revs).
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 1) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(), Rotation(Target, 100.0), Launch, Target,
                150.0, 240.0, 100.0, 50.0, 0.0, 1.0e6, TransitedBodyRotationMode.Loose, 10, new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void DropMode_ReturnsNone()
        {
            var r = SolveIntermediate(mode: TransitedBodyRotationMode.Drop);
            Assert.False(r.Applied);
        }

        [Fact]
        public void NotSupported_ReturnsNone()
        {
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(supported: false), Rotation(Target, 100.0), Launch, Target,
                150.0, 400.0, 100.0, 50.0, 0.0, 1.0e6, TransitedBodyRotationMode.Loose, 10, new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void HasStation_ReturnsNone()
        {
            // The station path is owned by the shipped ComputeArrivalHold; P4 stays out of it.
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(hasStation: true), Rotation(Target, 100.0), Launch, Target,
                150.0, 400.0, 100.0, 50.0, 0.0, 1.0e6, TransitedBodyRotationMode.Loose, 10, new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void OrbitOnly_NoLandingRotation_ReturnsNone()
        {
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(hasLandingRotation: false), Rotation(Target, 100.0), Launch, Target,
                150.0, 400.0, 100.0, 50.0, 0.0, 1.0e6, TransitedBodyRotationMode.Loose, 10, new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void DegenerateRotationPeriod_ReturnsNone()
        {
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(), Rotation(Target, 100.0), Launch, Target,
                150.0, 400.0, double.NaN, 50.0, 0.0, 1.0e6, TransitedBodyRotationMode.Loose, 10, new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void DeorbitNotAfterEntry_ReturnsNone()
        {
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(), Rotation(Target, 100.0), Launch, Target,
                recordedArrivalUT: 500.0, recordedDestSurfaceUT: 400.0, rotationPeriod: 100.0,
                phaseAnchorUT: 50.0, spanStartUT: 0.0, spanSeconds: 1.0e6,
                mode: TransitedBodyRotationMode.Loose, maxKeepRevs: 10, bodyInfo: new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void NanInput_ReturnsNone()
        {
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, null, Landing(), Rotation(Target, 100.0), Launch, Target,
                double.NaN, 400.0, 100.0, 50.0, 0.0, 1.0e6, TransitedBodyRotationMode.Loose, 10, new TrimFake());
            Assert.False(r.Applied);
        }

        [Fact]
        public void SpanGuard_SkipsTooLargeCut_FallsBackToLessCompression()
        {
            // span=50: r1 cut=140, r2=105, r3=70 all >= 50 (skipped); r4 cut=35 < 50, r5 no cut.
            // Baseline becomes r=4 (W=85); r=5 W=50 < 85 - tol -> bestR=5 (full loiter, no cut).
            var r = SolveIntermediate(spanSeconds: 50.0);
            Assert.True(r.Applied);
            Assert.Equal(5, r.DestinationKeepRevs);
            Assert.False(r.HasDestinationCut);
            Assert.Equal(50.0, r.HoldSeconds, 6);
        }

        [Fact]
        public void SpanGuard_AllCandidatesExceedSpan_ReturnsNone()
        {
            // span=30: r5 keeps the full loiter (no cut), so its trial cut length is 0 < 30 and it is
            // always valid; force a real all-fail by capping keepRevs to 4 so r5 is unreachable and
            // every reachable r (1..4) emits a cut >= 30.
            var r = SolveIntermediate(spanSeconds: 30.0, maxKeepRevs: 4);
            // r4 cut = (5-4)*35 = 35 >= 30 -> skipped; r1..r3 cuts larger -> all skipped -> None.
            Assert.False(r.Applied);
        }

        [Fact]
        public void MaxKeepRevs_CapsTheKeptRevolutions()
        {
            // With maxKeepRevs=2, only r in {1,2} are searched. W(1)=90, W(2)=55 -> bestR=2 (a cut of
            // (5-2)*35 = 105). The full-loiter r=5 (W=50) is unreachable under the cap.
            var r = SolveIntermediate(maxKeepRevs: 2);
            Assert.True(r.Applied);
            Assert.Equal(2, r.DestinationKeepRevs);
            Assert.True(r.HasDestinationCut);
            Assert.Equal(105.0, r.DestinationCut.LengthSeconds, 9);
            Assert.Equal(55.0, r.HoldSeconds, 6);
        }

        [Fact]
        public void LaunchSideAndDestinationCut_CombineInTrialMap_ShiftsOptimalKeepRevs()
        {
            // A non-empty launch-side cut (a compressed Kerbin parking loiter) folds into the trial map
            // alongside the destination cut: same destination run as SolveIntermediate, plus a launch
            // cut of 130s at StartUT=20. W(r) = ((5-r)*35 + (130-50)) mod 100 -> min at r=4 (W=15),
            // versus r=3 without the launch cut. Exercises trialCuts.AddRange(launchSideCuts) + the
            // combined CompressSpanUT (the only path the all-null-launchSideCuts cases never reached).
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Target, 200.0, 35.0, 5) };
            var launchSideCuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 20.0, LengthSeconds = 130.0 },
            };
            var r = DestinationLoiterTrim.SolveTrimAndHold(
                runs, launchSideCuts, Landing(), Rotation(Target, 100.0), Launch, Target,
                recordedArrivalUT: 150.0, recordedDestSurfaceUT: 400.0, rotationPeriod: 100.0,
                phaseAnchorUT: 50.0, spanStartUT: 0.0, spanSeconds: 1.0e6,
                mode: TransitedBodyRotationMode.Loose, maxKeepRevs: 10, bodyInfo: new TrimFake());
            Assert.True(r.Applied);
            Assert.Equal(4, r.DestinationKeepRevs);
            Assert.True(r.HasDestinationCut);
            Assert.Equal(35.0, r.DestinationCut.LengthSeconds, 9); // (5-4) * 35
            Assert.Equal(15.0, r.HoldSeconds, 6);

            // Alignment invariant with BOTH cuts assembled (launch-side + the re-timed destination cut).
            const double D = 400.0, phaseAnchor = 50.0, spanStart = 0.0, tRot = 100.0;
            var allCuts = new List<GhostPlaybackLogic.LoopCut>(launchSideCuts) { r.DestinationCut };
            double liveSurface = phaseAnchor + (GhostPlaybackLogic.CompressSpanUT(D, allCuts) - spanStart);
            double aligned = ((liveSurface + r.HoldSeconds) - D) % tRot;
            if (aligned < 0) aligned += tRot;
            Assert.Equal(0.0, aligned, 6);
        }

        // === Selector ====================================================================

        [Fact]
        public void Selector_PicksPostArrivalTargetRun_IgnoresPreArrivalSameBodyRun()
        {
            // A same-target pre-capture run that ends before SOI entry must not be selected.
            var runs = new List<ReaimLoiterCompressor.LoiterRun>
            {
                Run(Target, 10.0, 20.0, 3),    // EndUT 70 < arrival 150 (pre-entry, excluded)
                Run(Target, 200.0, 35.0, 5),   // EndUT 375 (the real destination parking)
            };
            bool ok = DestinationLoiterTrim.TrySelectDestinationRun(
                runs, Target, 150.0, 400.0, out ReaimLoiterCompressor.LoiterRun sel);
            Assert.True(ok);
            Assert.Equal(200.0, sel.StartUT, 9);
            Assert.Equal(375.0, sel.EndUT, 9);
        }

        [Fact]
        public void Selector_TwoPostArrivalRuns_PicksTheOneImmediatelyBeforeDeorbit()
        {
            // Capture-park then deorbit-prep-park, both post-arrival and at/before the deorbit: pick the
            // later-ending one (immediately preceding the deorbit).
            var runs = new List<ReaimLoiterCompressor.LoiterRun>
            {
                Run(Target, 200.0, 35.0, 3),   // EndUT 305 (capture park)
                Run(Target, 320.0, 20.0, 4),   // EndUT 400 (deorbit-prep park, immediately before D)
            };
            bool ok = DestinationLoiterTrim.TrySelectDestinationRun(
                runs, Target, 150.0, 400.0, out ReaimLoiterCompressor.LoiterRun sel);
            Assert.True(ok);
            Assert.Equal(320.0, sel.StartUT, 9);
        }

        [Fact]
        public void Selector_NoTargetRun_ReturnsFalse()
        {
            var runs = new List<ReaimLoiterCompressor.LoiterRun> { Run(Launch, 200.0, 35.0, 5) };
            bool ok = DestinationLoiterTrim.TrySelectDestinationRun(
                runs, Target, 150.0, 400.0, out _);
            Assert.False(ok);
        }

        // === Logging =====================================================================

        [Fact]
        public void Applied_EmitsOneSummaryLine()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = l => lines.Add(l);
            bool prevSuppress = MissionPeriodicity.SuppressLogging;
            MissionPeriodicity.SuppressLogging = false;
            try
            {
                var r = SolveIntermediate();
                Assert.True(r.Applied);
                Assert.Contains(lines, l =>
                    l.Contains("[Reaim]") && l.Contains("dest-trim dest=Duna")
                    && l.Contains("keepRevs=3/5"));
            }
            finally
            {
                MissionPeriodicity.SuppressLogging = prevSuppress;
            }
        }
    }
}
