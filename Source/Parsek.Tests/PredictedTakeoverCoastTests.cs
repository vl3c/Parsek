using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The takeover gap (owner ruling Q1b, the "a ghost never just vanishes" principle): when
    /// the player flies a stage again, its recording drops the predicted ending. A vessel that
    /// stayed on the predicted conic up to the takeover (on rails, where stock moves it on that
    /// drag-free conic) keeps the coast to the takeover UT as recorded history, so the replay
    /// does not lose it between the last real sample and the takeover; one that left the conic
    /// (it flew in physics through air, or burned) keeps the cut at the last real sample.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTakeoverCoastTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private readonly List<string> logLines = new List<string>();

        public PredictedTakeoverCoastTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            KerbalsModule.EnsureTerminalStampObserverInstalled();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static OrbitSegment Conic(double sma, string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = 160.0, endUT = 400.0, bodyName = body,
                semiMajorAxis = sma, eccentricity = 0.05, inclination = 3.0,
                longitudeOfAscendingNode = 40.0, argumentOfPeriapsis = 70.0,
                meanAnomalyAtEpoch = 0.3, epoch = 160.0, isPredicted = true,
            };
        }

        // A stage Parsek stopped watching at UT 160 with a predicted fall to UT 400.
        private static Recording Stage()
        {
            var rec = new Recording { RecordingId = "rec_stage", VesselName = "Stage", VesselPersistentId = 77u };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 60000 });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin", altitude = 70000 });
            rec.OrbitSegments.Add(Conic(700000.0));
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            rec.VesselDestroyed = true;
            rec.ExplicitEndUT = 400.0;
            return rec;
        }

        private static TakeoverCoastEvidence Live(OrbitSegment orbit)
        {
            orbit.isPredicted = false;
            return new TakeoverCoastEvidence { LiveOrbit = orbit, GravParameter = KerbinMu };
        }

        // catches: the takeover cutting an on-rails stage at its last real sample, so the
        // replay shows it vanish at UT 160 and reappear at the takeover (UT 250).
        [Fact]
        public void OnRailsUntilTakeover_KeepsTheCoastToTheTakeover_AsRecordedHistory()
        {
            Recording rec = Stage();

            PredictedEndingDropResult result = PredictedEndingTakeover.DropPredictedEndingForTakeover(
                rec, 250.0, "test", Live(Conic(700000.0)));

            Assert.True(result.Dropped);
            Assert.True(result.CoastKeptToTakeover);
            Assert.Equal(250.0, result.CutUT);
            Assert.Single(rec.OrbitSegments);
            Assert.False(rec.OrbitSegments[0].isPredicted);
            Assert.Equal(250.0, rec.OrbitSegments[0].endUT);
            Assert.Equal(250.0, rec.EndUT);
            Assert.Null(rec.TerminalStateValue);
            Assert.False(PredictedEndingTakeover.HasPredictedEnding(rec));
            Assert.Contains(logLines, l => l.Contains("[PredictedTakeover]")
                && l.Contains("coast=kept-to-takeover") && l.Contains("coastReason=on-the-predicted-conic"));
        }

        // catches: keeping a coast the vessel never flew - a stage that was loaded and in
        // physics through the air lost speed to drag, so the conic is not what it did.
        [Fact]
        public void OffTheConicAtTakeover_CutsAtTheLastRealSample()
        {
            Recording rec = Stage();

            PredictedEndingDropResult result = PredictedEndingTakeover.DropPredictedEndingForTakeover(
                rec, 250.0, "test", Live(Conic(690000.0)));

            Assert.True(result.Dropped);
            Assert.False(result.CoastKeptToTakeover);
            Assert.Equal(160.0, result.CutUT);
            Assert.Empty(rec.OrbitSegments);
            Assert.Equal(160.0, rec.EndUT);
            Assert.True(result.CoastMissMeters > 1000.0);
            Assert.Contains(logLines, l => l.Contains("[PredictedTakeover]")
                && l.Contains("coast=cut-at-last-real") && l.Contains("coastReason=off-the-predicted-conic"));
        }

        [Fact]
        public void NoEvidenceOrAnotherBody_CutsAtTheLastRealSample()
        {
            Recording a = Stage();
            PredictedEndingDropResult none = PredictedEndingTakeover.DropPredictedEndingForTakeover(a, 250.0, "test");
            Assert.False(none.CoastKeptToTakeover);
            Assert.Equal("no-live-evidence", none.CoastDecision);
            Assert.Equal(160.0, a.EndUT);

            Recording b = Stage();
            PredictedEndingDropResult otherBody = PredictedEndingTakeover.DropPredictedEndingForTakeover(
                b, 250.0, "test", Live(Conic(700000.0, "Mun")));
            Assert.False(otherBody.CoastKeptToTakeover);
            Assert.Equal("body-mismatch", otherBody.CoastDecision);
            Assert.Equal(160.0, b.EndUT);
        }

        // A takeover before the last real sample (or at it) has no gap to keep.
        [Fact]
        public void TakeoverNotAfterTheLastRealSample_KeepsTheEarlierCut()
        {
            Recording rec = Stage();
            PredictedEndingDropResult result = PredictedEndingTakeover.DropPredictedEndingForTakeover(
                rec, 150.0, "test", Live(Conic(700000.0)));
            Assert.False(result.CoastKeptToTakeover);
            Assert.Equal("takeover-not-after-last-real", result.CoastDecision);
            Assert.Equal(150.0, result.CutUT);
        }

        [Fact]
        public void CoastFollowedPredictedConic_ToleranceAndMisses()
        {
            Recording rec = Stage();
            Assert.True(PredictedEndingTakeover.CoastFollowedPredictedConic(
                rec, 250.0, Live(Conic(700000.0)), out double same, out _));
            Assert.True(same < 1.0);
            // 10 m of sma: numerical noise, inside the tolerance.
            Assert.True(PredictedEndingTakeover.CoastFollowedPredictedConic(
                rec, 250.0, Live(Conic(700010.0)), out _, out _));
            Assert.False(PredictedEndingTakeover.CoastFollowedPredictedConic(
                rec, 500.0, Live(Conic(700000.0)), out _, out string pastTail));
            Assert.Equal("no-predicted-segment-at-takeover", pastTail);
            Assert.False(PredictedEndingTakeover.CoastFollowedPredictedConic(
                rec, 250.0, new TakeoverCoastEvidence { LiveOrbit = Conic(700000.0), GravParameter = 0.0 },
                out _, out string noMu));
            Assert.Equal("orbit-not-propagatable", noMu);
        }
    }
}
