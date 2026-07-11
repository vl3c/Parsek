using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV10 codec round-trips. The trajectory seam writes to a scratch temp file
    // (never a save); the tree-record and manifest seams round-trip through
    // in-memory ConfigNodes. Each test names the regression it guards.
    public class Inv10CodecRoundtripTests
    {
        private static AnalyzerModel ModelWith(params Recording[] recs)
        {
            return new AnalyzerModel { SaveName = "inv10", Recordings = recs.ToList() };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv10CodecRoundtrip().Evaluate(model).ToList();
        }

        private static TrajectoryPoint P(double ut, double lat, double lon, double alt) =>
            new TrajectoryPoint { ut = ut, latitude = lat, longitude = lon, altitude = alt, bodyName = "Kerbin" };

        // Guards: a fully-populated recording (chain metadata, flat Points,
        // OrbitSegments, PartEvents, resource manifest) round-trips at every codec
        // seam -> zero INV10 FAIL. Fails if a codec silently drops a field on save
        // or load.
        [Fact]
        public void PopulatedRecording_RoundTrips_NoFail()
        {
            var rec = new Recording
            {
                RecordingId = "ok0",
                ChainId = "chain-a",
                ChainIndex = 2,
                ChainBranch = 1,
                ParentRecordingId = "parent0",
                Points = new List<TrajectoryPoint> { P(100, 0, 0, 1000), P(110, 0.01, 0.02, 1500) },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 120, endUT = 200, semiMajorAxis = 700000, eccentricity = 0.01,
                        inclination = 5, bodyName = "Kerbin", isPredicted = false,
                    },
                },
                PartEvents = new List<PartEvent>
                {
                    new PartEvent { ut = 105, partPersistentId = 100000, eventType = PartEventType.Decoupled, partName = "decoupler" },
                },
                StartResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 100.0, maxAmount = 100.0 },
                },
                EndResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 5.0, maxAmount = 100.0 },
                },
            };

            List<Finding> findings = Run(ModelWith(rec));

            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Warn);
        }

        // Guards: a manifest resource the codec drops on round-trip (empty-name
        // entry) -> INV10 FAIL naming the resource field. Fails if the round-trip
        // comparison is too shallow to notice the drift.
        [Fact]
        public void ManifestFieldDrift_FailsNamingField()
        {
            var rec = new Recording
            {
                RecordingId = "bad0",
                StartResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 100.0, maxAmount = 100.0 },
                    [""] = new ResourceAmount { amount = 3.0, maxAmount = 3.0 },
                },
            };

            List<Finding> findings = Run(ModelWith(rec));

            Assert.Contains(findings, f =>
                f.RuleId == Inv10CodecRoundtrip.RuleIdConst
                && f.Level == VerdictLevel.Fail
                && f.Message.Contains("field=StartResources")
                && f.Message.Contains("resource-drift"));
        }

        // Guards (edge case 25): an OrbitSegment carrying a NaN element round-trips
        // equal under NaN-aware equality -> no FAIL. Fails if naive == flags a
        // stable NaN, producing permanent noise on predicted-tail segments.
        [Fact]
        public void NaNOrbitElement_RoundTripsEqual_NoFail()
        {
            var rec = new Recording
            {
                RecordingId = "nan0",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 0, endUT = 10, semiMajorAxis = double.NaN, eccentricity = 0.0,
                        inclination = 0, bodyName = "Kerbin", isPredicted = false,
                    },
                },
            };

            List<Finding> findings = Run(ModelWith(rec));

            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }
    }
}
