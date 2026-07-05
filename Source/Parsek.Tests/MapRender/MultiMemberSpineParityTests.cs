using System.Collections.Generic;
using Parsek;
using Parsek.Display;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-10 A2 (headless half): the end-to-end flag-ON-vs-OFF differential the 5-lens audit found
    /// missing. The existing parity suites cover SINGLE-member faithful CIRCULAR fixtures
    /// (<c>PhaseFactoryTests</c> byte-parity per fixture; <c>PhaseSpineParityTests</c> intent sweeps over
    /// the equatorial <c>FaithfulChain</c>); none runs a realistic MULTI-segment recording
    /// (StockConic + TracedPath + an SOI crossing in ONE chain) through BOTH spines and asserts the full
    /// differential.
    ///
    /// <para>This fixture is one recording whose assembled chain spans:
    /// <list type="number">
    ///   <item>Kerbin ASCENT (recorded points 0..8, not orbit-covered) -> TracedPath, body Kerbin;</item>
    ///   <item>Kerbin PARKING (orbit 10..30, inclined+nonzero-LAN) -> StockConic, body Kerbin;</item>
    ///   <item>Mun ARRIVAL ORBIT (orbit 40..60, different elements) -> StockConic, body Mun
    ///     (the Kerbin->Mun body change is the SOI CROSSING: the assembler stamps a FlexibleSoi seam
    ///     between the two conics);</item>
    ///   <item>Mun DESCENT (recorded points 62..70, not orbit-covered) -> TracedPath, body Mun.</item>
    /// </list>
    /// So the chain carries BOTH treatments AND a real body-frame SOI crossing.</para>
    ///
    /// <para><b>What it asserts (the differential):</b>
    /// <list type="bullet">
    ///   <item>FULL geometry byte-parity: <c>PhaseFactory.BuildPhaseChain</c> projects byte-identically to
    ///     <c>ChainAssembler.Build</c> (<see cref="GeometryParityComparator"/>) — every segment's
    ///     treatment / UTs / frame body / full conic Kepler payload, the chain window, faithful-fallback,
    ///     and the segment count.</item>
    ///   <item>FULL intent parity for the faithful members across a continuous live-UT sweep: the typed
    ///     spine (factory -> sampler -> director) and the legacy spine (assembler -> sampler -> director)
    ///     emit byte-identical <c>GhostRenderIntent</c>s, threading the SAME prior intent as
    ///     <c>ShadowRenderDriver.RunFrame</c> does, so a coverage flip / treatment swap / held-vs-retired
    ///     gap (incl. the interior gaps on either side of each segment and across the SOI seam) on ANY
    ///     frame fails here.</item>
    /// </list></para>
    ///
    /// <para>Maps the §11.5 rows the audit flagged as headless-thin: "Single-level SOI transition", "Mixed
    /// faithful + re-aimed members" (the faithful half), and the StockConic+TracedPath chain shape. The
    /// re-aimed (synthesized) half stays in <see cref="PhaseSpineParityTests"/>'s re-aim sweeps; here the
    /// whole chain is FAITHFUL (no override), which is the flag-ON==flag-OFF byte-identical contract.</para>
    /// </summary>
    public class MultiMemberSpineParityTests
    {
        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        private static TrackSection Sec(double s, double e, SegmentEnvironment env)
            => new TrackSection { startUT = s, endUT = e, environment = env };

        private const double WindowStart = 0.0;
        private const double WindowEnd = 80.0;

        // The realistic multi-segment recording. Above-surface conics on TWO bodies (Kerbin then Mun) with
        // recorded ascent points before the first conic and recorded descent points after the last conic, so
        // the assembled chain is TracedPath -> StockConic -> StockConic(SOI crossing) -> TracedPath. Elements
        // are inclined + nonzero-LAN so the full-Kepler intent parity is genuinely exercised (not 0==0).
        private static MockTrajectory MultiMemberChain()
            => new MockTrajectory
            {
                RecordingId = "rec-multi",
                VesselName = "Mun Express",
                Points = new List<TrajectoryPoint>
                {
                    // Kerbin ascent (before the parking orbit) -> TracedPath on Kerbin.
                    Pt(0, "Kerbin"), Pt(2, "Kerbin"), Pt(4, "Kerbin"), Pt(6, "Kerbin"), Pt(8, "Kerbin"),
                    // Mun descent (after the Mun arrival orbit) -> TracedPath on Mun.
                    Pt(62, "Mun"), Pt(64, "Mun"), Pt(66, "Mun"), Pt(68, "Mun"), Pt(70, "Mun"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    // Kerbin parking orbit (inclined, nonzero LAN/argPe/MnA/epoch).
                    new OrbitSegment
                    {
                        startUT = 10, endUT = 30, bodyName = "Kerbin",
                        semiMajorAxis = 900000, eccentricity = 0.12,
                        inclination = 28.5, longitudeOfAscendingNode = 75.0,
                        argumentOfPeriapsis = 40.0, meanAnomalyAtEpoch = 1.2, epoch = 12.5,
                    },
                    // Mun arrival orbit (different body -> SOI crossing; different elements).
                    new OrbitSegment
                    {
                        startUT = 40, endUT = 60, bodyName = "Mun",
                        semiMajorAxis = 250000, eccentricity = 0.30,
                        inclination = 12.0, longitudeOfAscendingNode = 200.0,
                        argumentOfPeriapsis = 110.0, meanAnomalyAtEpoch = 0.6, epoch = 41.0,
                    },
                },
                // Env-class sections so the faithful leaf classification (ascent / parking / arrival /
                // descent) resolves; these do not affect the byte-parity geometry but exercise the realistic
                // classification path the factory runs.
                TrackSections = new List<TrackSection>
                {
                    Sec(0, 8, SegmentEnvironment.Atmospheric),     // Kerbin ascent
                    Sec(10, 30, SegmentEnvironment.ExoBallistic),  // Kerbin parking
                    Sec(40, 60, SegmentEnvironment.ExoBallistic),  // Mun arrival orbit
                    Sec(62, 70, SegmentEnvironment.Atmospheric),   // Mun descent
                },
            };

        private static GhostPlaybackLogic.LoopUnitSet NonLooped => GhostPlaybackLogic.LoopUnitSet.Empty;

        private static GhostRenderChain BuildAssembler(IPlaybackTrajectory traj)
            => ChainAssembler.Build(
                traj, committedIndex: 0, instanceKey: 0, WindowStart, WindowEnd,
                faithfulFallback: false, surface: null);

        private static PhaseChain BuildFactory(IPlaybackTrajectory traj)
            => PhaseFactory.BuildPhaseChain(
                traj, committedIndex: 0, instanceKey: 0, WindowStart, WindowEnd,
                faithfulFallback: false, surface: null);

        // The widened full-element intent parity (mirrors PhaseSpineParityTests.AssertIntentParity, kept
        // self-contained here so the A2 differential is readable end-to-end in one file).
        private static void AssertIntentParity(GhostRenderIntent assembler, GhostRenderIntent factory)
        {
            Assert.Equal(assembler.Visible, factory.Visible);
            Assert.Equal(assembler.Treatment, factory.Treatment);
            Assert.Equal(assembler.DriveUT, factory.DriveUT);
            Assert.Equal(assembler.FrameBodyName, factory.FrameBodyName);
            Assert.Equal(assembler.Payload.HasConic, factory.Payload.HasConic);
            if (assembler.Payload.HasConic)
            {
                OrbitSegment a = assembler.Payload.Conic;
                OrbitSegment f = factory.Payload.Conic;
                Assert.Equal(a.startUT, f.startUT);
                Assert.Equal(a.endUT, f.endUT);
                Assert.Equal(a.semiMajorAxis, f.semiMajorAxis);
                Assert.Equal(a.eccentricity, f.eccentricity);
                Assert.Equal(a.inclination, f.inclination);
                Assert.Equal(a.longitudeOfAscendingNode, f.longitudeOfAscendingNode);
                Assert.Equal(a.argumentOfPeriapsis, f.argumentOfPeriapsis);
                Assert.Equal(a.meanAnomalyAtEpoch, f.meanAnomalyAtEpoch);
                Assert.Equal(a.epoch, f.epoch);
                Assert.Equal(a.bodyName, f.bodyName);
                Assert.Equal(a.isPredicted, f.isPredicted);
            }
        }

        // ---- The fixture really is the multi-segment shape we claim (guard against a vacuous chain) ----

        [Fact]
        public void Fixture_AssemblesExpectedMultiSegmentChain_WithSoiCrossing()
        {
            GhostRenderChain assembler = BuildAssembler(MultiMemberChain());

            // 2 TracedPath (Kerbin ascent, Mun descent) + 2 StockConic (Kerbin parking, Mun arrival).
            int traced = 0, conic = 0;
            for (int i = 0; i < assembler.SegmentCount; i++)
            {
                if (assembler.Segments[i].Treatment == Treatment.TracedPath) traced++;
                if (assembler.Segments[i].Treatment == Treatment.StockConic) conic++;
            }
            Assert.Equal(2, traced);
            Assert.Equal(2, conic);

            // The SOI crossing: a body-frame change Kerbin -> Mun between two adjacent StockConic segments
            // -> a FlexibleSoi seam (the assembler stamps it; the factory leaves it None, which is exactly
            // why A4 keeps seams out of the byte-parity set). Prove the body change exists.
            bool sawKerbinConic = false, sawMunConic = false;
            for (int i = 0; i < assembler.SegmentCount; i++)
            {
                RenderSegment s = assembler.Segments[i];
                if (s.Treatment != Treatment.StockConic) continue;
                if (s.FrameBodyName == "Kerbin") sawKerbinConic = true;
                if (s.FrameBodyName == "Mun") sawMunConic = true;
            }
            Assert.True(sawKerbinConic, "expected a Kerbin parking StockConic");
            Assert.True(sawMunConic, "expected a Mun arrival StockConic (the SOI crossing target body)");
        }

        // ---- A2 part 1: FULL geometry byte-parity (factory projects == assembler) ----

        [Fact]
        public void MultiMemberChain_FactoryGeometry_ByteMatchesAssembler()
        {
            GhostRenderChain assembler = BuildAssembler(MultiMemberChain());
            PhaseChain factory = BuildFactory(MultiMemberChain());

            GeometryParityComparator.ParityResult result =
                GeometryParityComparator.Compare(factory, assembler);

            Assert.True(result.IsMatch, "multi-member geometry parity divergence: " + result);
            Assert.False(result.CountMismatch);
        }

        // ---- A2 part 2: FULL intent parity across a continuous live-UT sweep (the flag-ON==flag-OFF run) ----

        [Fact]
        public void MultiMemberChain_FullSweep_TwoSpines_EmitIdenticalIntents_WithSharedPrior()
        {
            var traj = MultiMemberChain();
            GhostRenderChain assembler = BuildAssembler(traj);
            PhaseChain factory = BuildFactory(traj);

            GhostRenderIntent aPrior = default(GhostRenderIntent);
            GhostRenderIntent fPrior = default(GhostRenderIntent);
            bool sawTraced = false, sawKerbinConic = false, sawMunConic = false;

            // Step 0.5s across [-5, 85] so every segment + every interior gap (ascent->parking, parking->Mun
            // SOI seam, Mun->descent) + the pre-launch / past-end tails is visited, threading the SAME prior
            // through both spines exactly as RunFrame does.
            for (double ut = -5.0; ut <= 85.0; ut += 0.5)
            {
                GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                    ChainSampler.Sample(assembler, ut, NonLooped), aPrior, traj.VesselName);
                GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                    factory, ut, NonLooped, fPrior, traj.VesselName);

                AssertIntentParity(aIntent, fIntent);

                if (aIntent.Visible && aIntent.Treatment == Treatment.TracedPath)
                    sawTraced = true;
                if (aIntent.Visible && aIntent.Treatment == Treatment.StockConic
                    && aIntent.FrameBodyName == "Kerbin")
                    sawKerbinConic = true;
                if (aIntent.Visible && aIntent.Treatment == Treatment.StockConic
                    && aIntent.FrameBodyName == "Mun")
                    sawMunConic = true;

                aPrior = aIntent;
                fPrior = fIntent;
            }

            // The sweep genuinely exercised both treatments AND both sides of the SOI crossing (else the
            // parity assertions would be vacuous).
            Assert.True(sawTraced, "the sweep must render a TracedPath leg (ascent/descent)");
            Assert.True(sawKerbinConic, "the sweep must render the Kerbin parking StockConic");
            Assert.True(sawMunConic, "the sweep must render the Mun arrival StockConic (post-SOI-crossing)");
        }

        // ---- Spot frames: one assertion per segment + the interior gaps, so a failure localizes ----

        // NOTE: the treatment is passed as a string (not the internal Treatment enum) because a public
        // [Theory] method cannot take an internal-typed parameter (CS0051). It is mapped to the enum inside.
        [Theory]
        [InlineData(4.0, true, "TracedPath", "Kerbin")]    // Kerbin ascent traced
        [InlineData(20.0, true, "StockConic", "Kerbin")]   // Kerbin parking conic
        [InlineData(50.0, true, "StockConic", "Mun")]      // Mun arrival conic (post-crossing)
        [InlineData(66.0, true, "TracedPath", "Mun")]      // Mun descent traced
        public void MultiMemberChain_SpotFrame_TwoSpines_Agree(
            double liveUT, bool expectVisible, string expectTreatment, string expectBody)
        {
            var traj = MultiMemberChain();
            GhostRenderChain assembler = BuildAssembler(traj);
            PhaseChain factory = BuildFactory(traj);

            GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                ChainSampler.Sample(assembler, liveUT, NonLooped), default(GhostRenderIntent), traj.VesselName);
            GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                factory, liveUT, NonLooped, default(GhostRenderIntent), traj.VesselName);

            // The two spines agree (the gate)...
            AssertIntentParity(aIntent, fIntent);
            // ...and the agreed intent is the one the fixture intends (so the parity is on the right thing).
            Assert.Equal(expectVisible, aIntent.Visible);
            Assert.Equal(expectTreatment, aIntent.Treatment.ToString());
            Assert.Equal(expectBody, aIntent.FrameBodyName);
        }
    }
}
