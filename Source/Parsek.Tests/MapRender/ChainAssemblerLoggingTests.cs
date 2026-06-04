using System;
using System.Collections.Generic;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 log-assertion guard (design §13): the assembler MUST emit one MapRender assembly
    /// summary per build carrying segment count, per-treatment counts, the window, and the
    /// faithful-fallback flag. This is the diagnostic line the otherwise-undebuggable map/TS render
    /// path relies on; a regression here is silent loss of the only assembly trace.
    ///
    /// What makes it fail: the assembler stops logging, drops a field, or mislabels the
    /// treatment/fallback counts. Touches shared ParsekLog static state, so [Collection("Sequential")]
    /// + a sink reset in Dispose.
    /// </summary>
    [Collection("Sequential")]
    public class ChainAssemblerLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainAssemblerLoggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true; // assembly summary is logged at Verbose
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        // ascent (Kerbin points) → loiter (Kerbin orbit) → arrival (Mun points): 1 conic + 2 traced.
        private static MockTrajectory FullChain()
            => new MockTrajectory
            {
                RecordingId = "rec-1",
                Points = new List<TrajectoryPoint>
                {
                    Pt(0, "Kerbin"), Pt(2, "Kerbin"), Pt(4, "Kerbin"),
                    Pt(32, "Mun"), Pt(34, "Mun"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0 },
                },
            };

        [Fact]
        public void Build_EmitsAssemblySummaryLine_WithCountsAndWindow()
        {
            ChainAssembler.Build(FullChain(), committedIndex: 5, instanceKey: 0,
                windowStartUT: 0, windowEndUT: 40);

            Assert.Contains(logLines, l =>
                l.Contains("[MapRender]") &&
                l.Contains("assembled chain rec=rec-1") &&
                l.Contains("idx=5") &&
                l.Contains("segs=3") &&
                l.Contains("conic=1") &&
                l.Contains("traced=2") &&
                l.Contains("window=[0.0,40.0]") &&
                l.Contains("faithfulFallback=False"));
        }

        [Fact]
        public void Build_FaithfulFallback_IsReflectedInTheAssemblyLine()
        {
            ChainAssembler.Build(FullChain(), 0, 0, 0, 40, faithfulFallback: true);

            Assert.Contains(logLines, l =>
                l.Contains("[MapRender]") &&
                l.Contains("assembled chain") &&
                l.Contains("faithfulFallback=True"));
        }
    }
}
