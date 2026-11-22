using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling P-STOCK for a predicted final orbit (Q8) whose vessel stock still holds:
    /// the stock-fate pass compares stock's live orbit with the prediction
    /// (<see cref="HeldFinalOrbitComparison"/>) and, when stock holds it on another body or
    /// orbit on two passes, drops the prediction as a contradicted touchdown is dropped, so the
    /// ending follows the live vessel. Held where predicted, or not comparable, it is left alone.
    /// </summary>
    [Collection("Sequential")]
    public class HeldFinalOrbitReconcileTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private const double MunMu = 6.5138398e10;
        private const double SunMu = 1.1723328e18;
        private const string OrbiterId = "rec-orbiter";
        private readonly List<string> logLines = new List<string>();

        public HeldFinalOrbitReconcileTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ResetRateLimitsForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.EnsureTerminalStampObserverInstalled();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            PredictedTouchdownStockFate.ResetForTesting();
        }

        public void Dispose()
        {
            PredictedTouchdownStockFate.ResetForTesting();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------ fixtures

        private static OrbitSegment Seg(double startUT, double endUT, string body, double sma, double ecc,
            bool predicted = true, double meanAnomaly = 0.0)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = body,
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = 5.0,
                longitudeOfAscendingNode = 30.0,
                argumentOfPeriapsis = 40.0,
                meanAnomalyAtEpoch = meanAnomaly,
                epoch = startUT,
                isPredicted = predicted,
            };
        }

        /// <summary>Stock holds the vessel on exactly this conic.</summary>
        internal static TakeoverCoastEvidence LiveOn(OrbitSegment seg)
        {
            seg.isPredicted = false;
            return new TakeoverCoastEvidence { LiveOrbit = seg, GravParameter = MuOf(seg.bodyName) };
        }

        private static double MuOf(string body)
        {
            switch (body)
            {
                case "Kerbin": return KerbinMu;
                case "Mun": return MunMu;
                case "Sun": return SunMu;
                default: return double.NaN;
            }
        }

        // A coast around Kerbin to 2500, then a final conic around the Mun to 3000.
        private static Recording CoastThenMunOrbit()
        {
            var rec = new Recording { RecordingId = OrbiterId };
            rec.OrbitSegments.Add(Seg(1000.0, 2500.0, "Kerbin", 700000.0, 0.01));
            rec.OrbitSegments.Add(Seg(2500.0, 3000.0, "Mun", 700000.0, 0.01));
            return rec;
        }

        private static HeldFinalOrbitResult Compare(Recording rec, double nowUT, TakeoverCoastEvidence? live,
            bool landed = false, Func<string, double> mu = null)
        {
            return HeldFinalOrbitComparison.Compare(rec, nowUT, landed, live, mu);
        }

        // ------------------------------------------------------------------ the comparison

        [Fact]
        public void Compare_OnThePredictedCoast_Agrees()
        {
            Recording rec = CoastThenMunOrbit();
            HeldFinalOrbitResult r = Compare(rec, 1700.0, LiveOn(rec.OrbitSegments[0]));
            Assert.Equal(HeldFinalOrbitVerdict.Agrees, r.Verdict);
            Assert.Equal("on-the-predicted-coast", r.Reason);
            Assert.True(r.MissMeters < 1.0, "miss=" + r.MissMeters);
            Assert.Equal(2510.0, r.NextCheckUT, 6);
        }

        // catches: stock's own sphere-change timing and the seconds before packing (a velocity
        // error of about 1e-4) read as a contradiction that would throw the predicted tail away
        [Fact]
        public void Compare_SmallDrift_IsWithinTolerance()
        {
            Recording rec = CoastThenMunOrbit();
            OrbitSegment drifted = rec.OrbitSegments[0];
            drifted.semiMajorAxis *= 1.0 + 1e-4;
            HeldFinalOrbitResult r = Compare(rec, 1700.0, LiveOn(drifted));
            Assert.Equal(HeldFinalOrbitVerdict.Agrees, r.Verdict);
            Assert.True(r.MissMeters > 1.0, "the drift is measured: miss=" + r.MissMeters);
            Assert.True(r.MissMeters <= r.ToleranceMeters);
        }

        [Fact]
        public void Compare_AnotherOrbitOnTheSameBody_Differs()
        {
            Recording rec = CoastThenMunOrbit();
            OrbitSegment burned = rec.OrbitSegments[0];
            burned.semiMajorAxis = 900000.0;
            HeldFinalOrbitResult r = Compare(rec, 1700.0, LiveOn(burned));
            Assert.Equal(HeldFinalOrbitVerdict.Differs, r.Verdict);
            Assert.Equal("off-the-predicted-coast", r.Reason);
            Assert.True(r.MissMeters > r.ToleranceMeters);
            // 1% of the distance from the body centre (about 700 km here)
            Assert.InRange(r.ToleranceMeters, 6900.0, 7100.0);
        }

        // catches: a sphere change the predictor never modelled (stock moved the vessel into
        // another body's sphere) leaving the replay to spawn it on the predicted orbit
        [Fact]
        public void Compare_AnotherBodyDuringTheCoast_Differs()
        {
            Recording rec = CoastThenMunOrbit();
            HeldFinalOrbitResult r = Compare(rec, 1700.0, LiveOn(Seg(1600.0, 1600.0, "Mun", 400000.0, 0.2)));
            Assert.Equal(HeldFinalOrbitVerdict.Differs, r.Verdict);
            Assert.Equal("body-differs", r.Reason);
            Assert.Equal("Kerbin", r.PredictedBody);
            Assert.Equal("Mun", r.LiveBody);
        }

        [Fact]
        public void Compare_StockHasItOnTheGround_Differs()
        {
            HeldFinalOrbitResult r = Compare(CoastThenMunOrbit(), 1700.0, null, landed: true);
            Assert.Equal(HeldFinalOrbitVerdict.Differs, r.Verdict);
            Assert.Equal("stock-has-it-down", r.Reason);
        }

        [Fact]
        public void Compare_NothingToCompare_IsNotCompared()
        {
            Recording rec = CoastThenMunOrbit();
            Assert.Equal("no-live-orbit", Compare(rec, 1700.0, null).Reason);
            Assert.Equal("before-the-prediction",
                Compare(rec, 900.0, LiveOn(rec.OrbitSegments[0])).Reason);
            Assert.Equal("no-predicted-segment",
                Compare(new Recording { RecordingId = "x" }, 1700.0, LiveOn(rec.OrbitSegments[0])).Reason);
            Assert.Equal(HeldFinalOrbitVerdict.NotCompared, Compare(rec, 1700.0, null).Verdict);
        }

        // Past the predicted end the final conic is propagated on: stock on that conic agrees,
        // a different one differs, up to one period after the conic starts.
        [Fact]
        public void Compare_PastTheEnd_JudgesTheFinalConicWithinOnePeriod()
        {
            Recording rec = CoastThenMunOrbit();
            OrbitSegment final = rec.OrbitSegments[1];
            double period = 2.0 * Math.PI * Math.Sqrt(Math.Pow(700000.0, 3) / MunMu);

            HeldFinalOrbitResult on = Compare(rec, 3000.0 + 0.5 * period, LiveOn(final));
            Assert.Equal(HeldFinalOrbitVerdict.Agrees, on.Verdict);
            Assert.Equal("on-the-final-conic", on.Reason);
            Assert.True(double.IsPositiveInfinity(on.NextCheckUT));

            OrbitSegment other = final;
            other.semiMajorAxis = 500000.0;
            HeldFinalOrbitResult off = Compare(rec, 3000.0 + 0.5 * period, LiveOn(other));
            Assert.Equal(HeldFinalOrbitVerdict.Differs, off.Verdict);
            Assert.Equal("off-the-final-conic", off.Reason);

            // an encounter after the span the predictor checked is the vessel's own future
            HeldFinalOrbitResult late = Compare(rec, 2500.0 + 1.5 * period, LiveOn(other));
            Assert.Equal(HeldFinalOrbitVerdict.NotCompared, late.Verdict);
            Assert.Equal("past-the-final-conic-horizon", late.Reason);
        }

        [Fact]
        public void Compare_PastTheEnd_OnAnotherBody_NeedsTheFinalBodysGravity()
        {
            Recording rec = CoastThenMunOrbit();
            TakeoverCoastEvidence kerbin = LiveOn(Seg(3100.0, 3100.0, "Kerbin", 9000000.0, 0.3));
            HeldFinalOrbitResult unknown = Compare(rec, 3200.0, kerbin);
            Assert.Equal(HeldFinalOrbitVerdict.NotCompared, unknown.Verdict);
            Assert.Equal("final-conic-period-unknown", unknown.Reason);

            HeldFinalOrbitResult known = Compare(rec, 3200.0, kerbin, mu: MuOf);
            Assert.Equal(HeldFinalOrbitVerdict.Differs, known.Verdict);
            Assert.Equal("body-differs", known.Reason);
        }

        // An open final conic around the Sun (ruling Q10) has no period: it is compared at any UT.
        [Fact]
        public void Compare_OpenFinalConic_HasNoHorizon()
        {
            var rec = new Recording { RecordingId = OrbiterId };
            rec.OrbitSegments.Add(Seg(1000.0, 1060.0, "Sun", -2.0e10, 1.5));
            HeldFinalOrbitResult r = Compare(rec, 1.0e8, LiveOn(rec.OrbitSegments[0]));
            Assert.Equal(HeldFinalOrbitVerdict.Agrees, r.Verdict);
            Assert.Equal("on-the-final-conic", r.Reason);
        }

        // ------------------------------------------------------------------ the decision

        [Fact]
        public void DecideHeldFinalOrbit_EveryVerdict()
        {
            var live = new PredictedCrashLiveFacts { Found = true };
            var differs = new HeldFinalOrbitResult { Verdict = HeldFinalOrbitVerdict.Differs };
            Assert.Equal(PredictedTouchdownOutcome.AwaitingConfirmation,
                PredictedTouchdownStockFate.DecideHeldFinalOrbit(live, differs, confirmed: false));
            Assert.Equal(PredictedTouchdownOutcome.Contradicted,
                PredictedTouchdownStockFate.DecideHeldFinalOrbit(live, differs, confirmed: true));
            Assert.Equal(PredictedTouchdownOutcome.FinalOrbitHeld,
                PredictedTouchdownStockFate.DecideHeldFinalOrbit(
                    live, new HeldFinalOrbitResult { Verdict = HeldFinalOrbitVerdict.Agrees }, true));
            Assert.Equal(PredictedTouchdownOutcome.FinalOrbitNotCompared,
                PredictedTouchdownStockFate.DecideHeldFinalOrbit(live, default(HeldFinalOrbitResult), true));
            // stock is still moving it in physics (drag, the player nearby): no verdict yet
            var inPhysics = live;
            inPhysics.InPhysics = true;
            Assert.Equal(PredictedTouchdownOutcome.StockStillSimulating,
                PredictedTouchdownStockFate.DecideHeldFinalOrbit(inPhysics, differs, true));
        }

        // ------------------------------------------------------------------ the pass and the apply

        private static PredictedCrashLiveFacts HeldOn(OrbitSegment seg)
        {
            return new PredictedCrashLiveFacts
            {
                Found = true, Eccentricity = seg.eccentricity, PeriapsisAltitude = 100000.0,
                LiveOrbit = LiveOn(seg),
            };
        }

        private static OrbitSegment FirstPredicted(Recording rec)
        {
            foreach (OrbitSegment seg in rec.OrbitSegments)
                if (seg.isPredicted)
                    return seg;
            throw new InvalidOperationException("no predicted segment");
        }

        // catches: a held final orbit stock holds elsewhere left as committed, so a replay after
        // a rewind spawns the vessel on an orbit stock never had it on
        [Fact]
        public void EvaluatePass_DiffersTwice_IsContradicted_AgreesIsScheduled()
        {
            Recording rec = StockFateReconcileReviewFixTests.PredictedFinalOrbitStage();
            rec.StockHeldVesselUT = 1200.0;
            var recs = new List<Recording> { rec };
            OrbitSegment burned = FirstPredicted(rec);
            burned.semiMajorAxis = 1200000.0;
            var observed = new Dictionary<string, double>();

            PredictedTouchdownPassResult first = PredictedTouchdownStockFate.EvaluatePass(
                recs, null, null, r => HeldOn(burned), 1500.0, true, 10.0, observed);
            Assert.Equal(1, first.Count(PredictedTouchdownOutcome.AwaitingConfirmation));
            Assert.True(first.AwaitingConfirmation);
            Assert.Empty(first.Contradicted);
            Assert.Contains(logLines, l => l.Contains("[PredictedTouchdownReconcile]")
                && l.Contains("Held final orbit differs from stock") && l.Contains("reason=off-the-predicted-coast"));

            PredictedTouchdownPassResult second = PredictedTouchdownStockFate.EvaluatePass(
                recs, null, null, r => HeldOn(burned), 1505.0, true, 13.0, observed);
            Assert.Single(second.Contradicted);

            // held where predicted: left alone, and looked at again after the predicted sphere change
            PredictedTouchdownPassResult agrees = PredictedTouchdownStockFate.EvaluatePass(
                recs, null, null, r => HeldOn(FirstPredicted(rec)), 1500.0, true, 20.0,
                new Dictionary<string, double>());
            Assert.Equal(1, agrees.Count(PredictedTouchdownOutcome.FinalOrbitHeld));
            Assert.Empty(agrees.Contradicted);
            Assert.Equal(2510.0, agrees.NextDueUT, 6);
        }

        [Fact]
        public void ApplyContradiction_FinalOrbit_EndingFollowsTheLiveVessel_CrewStayAboard()
        {
            Recording stage = StockFateReconcileReviewFixTests.PredictedFinalOrbitStage();
            RecordingTree committed = PredictedTouchdownStockFateTests.CommitTreeWith(
                PredictedTouchdownStockFateTests.Core(), stage);
            OrbitSegment live = FirstPredicted(stage);
            live.semiMajorAxis = 1200000.0;

            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyContradiction(
                committed, OrbiterId, 1500.0, LiveOn(live),
                stampLiveEnding: rec =>
                {
                    rec.StampTerminalState(TerminalState.Orbiting, "test-live");
                    rec.TerminalOrbitBody = "Kerbin";
                    rec.TerminalOrbitSemiMajorAxis = 1200000.0;
                },
                context: "test");

            Assert.True(result.Applied, result.Reason);
            Assert.False(result.Drop.CoastKeptToTakeover);
            Recording after = RecordingStore.FindRecordingById(OrbiterId);
            Assert.NotSame(stage, after);
            Assert.Equal(TerminalState.Orbiting, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.False(PredictedTouchdownStockFate.IsStockFatePrediction(after));
            Assert.Equal(1200000.0, after.TerminalOrbitSemiMajorAxis, 3);
            Assert.Equal(1000.0, after.EndUT, 6);
            foreach (OrbitSegment seg in after.OrbitSegments)
                Assert.False(seg.isPredicted && seg.endUT > 1000.0 + 1e-6, "a predicted segment past the cut stayed");
            Assert.True(stage.TerminalPredicted);
            Assert.Contains(logLines, l => l.Contains("Predicted final orbit contradicted by stock") && l.Contains(OrbiterId));
        }
    }
}
