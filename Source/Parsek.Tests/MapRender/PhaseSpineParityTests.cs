using System.Collections.Generic;
using Parsek;
using Parsek.Display;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-3 guard for THE SPINE SWAP (migration plan §5): the typed-spine sampler/director path
    /// (<see cref="ChainSampler.Sample(PhaseChain, double, GhostPlaybackLogic.LoopUnitSet)"/> +
    /// <see cref="GhostRenderDirector.Decide(PhaseChain, double, GhostPlaybackLogic.LoopUnitSet,
    /// GhostRenderIntent, string)"/>) must produce the SAME <see cref="GhostRenderIntent"/> as the legacy
    /// <see cref="GhostRenderChain"/> spine for the same geometry, across the whole live-UT sweep. This is
    /// the headless analogue of the in-game parity-oracle gate: the factory geometry byte-matches the
    /// assembler (proven by <c>PhaseFactoryTests</c>), and here we prove the SAMPLER + DIRECTOR consuming
    /// that factory chain emit a byte-identical intent (visible / treatment / driveUT / body / coverage),
    /// so flag-ON renders identically to flag-OFF.
    ///
    /// A regression here is a subtle sampling / coverage / gap-hold difference between the two spines —
    /// exactly the HIGH-RISK failure mode the plan calls out for Phase 3.
    /// </summary>
    public class PhaseSpineParityTests
    {
        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        // ascent (Kerbin points 0..8, TracedPath) -> orbit (Kerbin 10..30, StockConic)
        //  -> arrival (Mun points 32..36, TracedPath). The exact fixture the assembler/factory parity
        // tests use, so the geometry round-trip is already proven; here we sweep the intent.
        private static MockTrajectory FaithfulChain()
            => new MockTrajectory
            {
                RecordingId = "rec-spine",
                VesselName = "Jeb's Ride",
                Points = new List<TrajectoryPoint>
                {
                    Pt(0, "Kerbin"), Pt(2, "Kerbin"), Pt(4, "Kerbin"), Pt(6, "Kerbin"), Pt(8, "Kerbin"),
                    Pt(32, "Mun"), Pt(34, "Mun"), Pt(36, "Mun"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0 },
                },
            };

        private static GhostPlaybackLogic.LoopUnitSet NonLooped => GhostPlaybackLogic.LoopUnitSet.Empty;

        private static GhostRenderChain BuildAssemblerChain(IPlaybackTrajectory traj, double wStart, double wEnd)
            => ChainAssembler.Build(
                traj, committedIndex: 0, instanceKey: 0, wStart, wEnd,
                faithfulFallback: false, surface: null);

        private static PhaseChain BuildFactoryChain(IPlaybackTrajectory traj, double wStart, double wEnd)
            => PhaseFactory.BuildPhaseChain(
                traj, committedIndex: 0, instanceKey: 0, wStart, wEnd,
                faithfulFallback: false, surface: null);

        // Override-/fallback-aware overloads: both spines must be built from the SAME inputs (the re-aimed
        // override + ancestor, or the faithfulFallback flag) so the intent parity proof exercises the exact
        // geometry the production GetOrBuildChain feeds (it passes the SAME override + ancestor to BOTH
        // ChainAssembler.Build and PhaseFactory.BuildPhaseChain).
        private static GhostRenderChain BuildAssemblerChain(
            IPlaybackTrajectory traj, double wStart, double wEnd,
            IReadOnlyList<OrbitSegment> overrideSegs, string ancestor, bool faithfulFallback)
            => ChainAssembler.Build(
                traj, committedIndex: 0, instanceKey: 0, wStart, wEnd,
                faithfulFallback, surface: null,
                orbitSegmentsOverride: overrideSegs, reaimAncestorBody: ancestor);

        private static PhaseChain BuildFactoryChain(
            IPlaybackTrajectory traj, double wStart, double wEnd,
            IReadOnlyList<OrbitSegment> overrideSegs, string ancestor, bool faithfulFallback)
            => PhaseFactory.BuildPhaseChain(
                traj, committedIndex: 0, instanceKey: 0, wStart, wEnd,
                faithfulFallback, surface: null,
                orbitSegmentsOverride: overrideSegs, reaimAncestorBody: ancestor);

        // Assert the two spines' intents are byte-identical on the load-bearing fields (the only ones that
        // flow downstream into the side-channel stamps + draw): Visible, Treatment, DriveUT, FrameBodyName,
        // and the conic payload (HasConic + the elements). Prior intent is shared so the gap-hold matches.
        private static void AssertIntentParity(GhostRenderIntent assembler, GhostRenderIntent factory)
        {
            Assert.Equal(assembler.Visible, factory.Visible);
            Assert.Equal(assembler.Treatment, factory.Treatment);
            Assert.Equal(assembler.DriveUT, factory.DriveUT);
            Assert.Equal(assembler.FrameBodyName, factory.FrameBodyName);
            Assert.Equal(assembler.Payload.HasConic, factory.Payload.HasConic);
            if (assembler.Payload.HasConic)
            {
                Assert.Equal(assembler.Payload.Conic.semiMajorAxis, factory.Payload.Conic.semiMajorAxis);
                Assert.Equal(assembler.Payload.Conic.eccentricity, factory.Payload.Conic.eccentricity);
                Assert.Equal(assembler.Payload.Conic.startUT, factory.Payload.Conic.startUT);
                Assert.Equal(assembler.Payload.Conic.endUT, factory.Payload.Conic.endUT);
                Assert.Equal(assembler.Payload.Conic.bodyName, factory.Payload.Conic.bodyName);
            }
        }

        // ---- ChainSampler: PhaseChain overload matches the GhostRenderChain overload (same GhostSample) ----

        [Fact]
        public void Sampler_NullPhaseChain_IsOutside()
        {
            Assert.Equal(Coverage.OutsideWindow, ChainSampler.Sample((PhaseChain)null, 105, NonLooped).Coverage);
        }

        [Theory]
        [InlineData(4.0)]   // ascent traced run (Kerbin)
        [InlineData(20.0)]  // orbit conic (Kerbin)
        [InlineData(31.0)]  // interior gap [30,32]
        [InlineData(34.0)]  // Mun arrival traced run
        [InlineData(50.0)]  // past window end
        public void Sampler_PhaseChain_MatchesGhostRenderChain_Sample(double liveUT)
        {
            var traj = FaithfulChain();
            GhostRenderChain assembler = BuildAssemblerChain(traj, 0, 40);
            PhaseChain factory = BuildFactoryChain(traj, 0, 40);

            GhostSample a = ChainSampler.Sample(assembler, liveUT, NonLooped);
            GhostSample f = ChainSampler.Sample(factory, liveUT, NonLooped);

            Assert.Equal(a.Coverage, f.Coverage);
            Assert.Equal(a.Treatment, f.Treatment);
            Assert.Equal(a.DriveUT, f.DriveUT);
            Assert.Equal(a.FrameBodyName, f.FrameBodyName);
            Assert.Equal(a.SegmentIndex, f.SegmentIndex);
            Assert.Equal(a.Segment.Payload.HasConic, f.Segment.Payload.HasConic);
        }

        // ---- GhostRenderDirector: PhaseChain Decide matches the GhostSample Decide across the sweep ----

        [Theory]
        [InlineData(4.0)]
        [InlineData(20.0)]
        [InlineData(34.0)]
        [InlineData(50.0)]
        public void Director_PhaseChain_MatchesGhostRenderChain_Intent_NoPrior(double liveUT)
        {
            var traj = FaithfulChain();
            GhostRenderChain assembler = BuildAssemblerChain(traj, 0, 40);
            PhaseChain factory = BuildFactoryChain(traj, 0, 40);

            GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                ChainSampler.Sample(assembler, liveUT, NonLooped), default(GhostRenderIntent), traj.VesselName);
            GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                factory, liveUT, NonLooped, default(GhostRenderIntent), traj.VesselName);

            AssertIntentParity(aIntent, fIntent);
        }

        [Fact]
        public void Director_PhaseChain_HoldsAcrossInteriorGap_LikeAssembler()
        {
            // The HIGH-RISK gap-hold contract: both spines, fed the same visible prior, must HOLD it across
            // the [30,32] interior gap (not blink/retire). Drive both with the same prior built at UT 20.
            var traj = FaithfulChain();
            GhostRenderChain assembler = BuildAssemblerChain(traj, 0, 40);
            PhaseChain factory = BuildFactoryChain(traj, 0, 40);

            GhostRenderIntent aPrior = GhostRenderDirector.Decide(
                ChainSampler.Sample(assembler, 20.0, NonLooped), default(GhostRenderIntent), traj.VesselName);
            GhostRenderIntent fPrior = GhostRenderDirector.Decide(
                factory, 20.0, NonLooped, default(GhostRenderIntent), traj.VesselName);
            AssertIntentParity(aPrior, fPrior); // the priors themselves must already match

            GhostRenderIntent aHeld = GhostRenderDirector.Decide(
                ChainSampler.Sample(assembler, 31.0, NonLooped), aPrior, traj.VesselName);
            GhostRenderIntent fHeld = GhostRenderDirector.Decide(
                factory, 31.0, NonLooped, fPrior, traj.VesselName);

            Assert.True(aHeld.Visible); // sanity: the assembler spine holds
            AssertIntentParity(aHeld, fHeld);
        }

        // ---- Full live-UT sweep: the two spines never disagree on a frame (the parity-oracle analogue) ----

        [Fact]
        public void FullSweep_TwoSpines_EmitIdenticalIntents_WithSharedPrior()
        {
            // Walk the whole [-5, 45] live-UT span at 0.5s steps, threading the SAME prior intent through
            // BOTH spines exactly as RunFrame does (one priorIntentByPid map). A divergence on ANY frame
            // (a coverage flip, a one-frame treatment swap, a held-vs-retired gap) fails here — the headless
            // proof that flag-ON is byte-identical to flag-OFF over a continuous run.
            var traj = FaithfulChain();
            GhostRenderChain assembler = BuildAssemblerChain(traj, 0, 40);
            PhaseChain factory = BuildFactoryChain(traj, 0, 40);

            GhostRenderIntent aPrior = default(GhostRenderIntent);
            GhostRenderIntent fPrior = default(GhostRenderIntent);
            for (double ut = -5.0; ut <= 45.0; ut += 0.5)
            {
                GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                    ChainSampler.Sample(assembler, ut, NonLooped), aPrior, traj.VesselName);
                GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                    factory, ut, NonLooped, fPrior, traj.VesselName);

                AssertIntentParity(aIntent, fIntent);

                aPrior = aIntent;
                fPrior = fIntent;
            }
        }

        // ---- Looped / overlap member: the shared span clock keeps the two spines glued (no per-instance) ----

        private static GhostPlaybackLogic.LoopUnitSet OverlapUnitFullSpan()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, spanStartUT: 0, spanEndUT: 40, cadenceSeconds: 40,
                phaseAnchorUT: 0, overlapCadenceSeconds: 10);
            Assert.True(GhostPlaybackLogic.UnitMemberOverlaps(unit));
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } };
            var ownerByIndex = new Dictionary<int, int> { { 0, 0 } };
            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        [Theory]
        [InlineData(60.0)]  // cycle 1, phase 20 -> loopUT 20 (in orbit segment)
        [InlineData(44.0)]  // cycle 1, phase 4 -> loopUT 4 (ascent traced)
        [InlineData(50.0)]  // before the anchor-resolved span yields a hidden/outside frame for both
        public void OverlapMember_TwoSpines_MatchAtSpanClockHead(double liveUT)
        {
            // Both spines route the overlap member through the SAME span clock
            // (ResolveTrackingStationSampleUT), so they pick the identical selected-cycle head-UT and emit
            // the same intent. The map shows ONE ghost at that head, never N instances — and the two spines
            // never diverge on which UT that is.
            var traj = FaithfulChain();
            var units = OverlapUnitFullSpan();
            GhostRenderChain assembler = BuildAssemblerChain(traj, 0, 40);
            PhaseChain factory = BuildFactoryChain(traj, 0, 40);

            GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                ChainSampler.Sample(assembler, liveUT, units), default(GhostRenderIntent), traj.VesselName);
            GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                factory, liveUT, units, default(GhostRenderIntent), traj.VesselName);

            AssertIntentParity(aIntent, fIntent);
        }

        // ---- RE-AIM intent-layer parity (SHOULD 1): close the highest-risk gap ----
        // Re-aim is the highest-risk geometry and a Phase-3-contract-enumerated representative, but the
        // sweeps above only cover FAITHFUL chains (no override). Here BOTH spines are built from the SAME
        // re-aimed override + ancestor (exactly as production GetOrBuildChain feeds them), so the new-spine
        // intent must EQUAL the legacy-spine intent across the UT sweep. The path is correct by construction
        // (the factory geometry byte-matches the assembler under the override - proven by
        // PhaseFactoryTests.ReaimedMember_ClassifiesSynthesizedHeliocentricTransfer); these tests prove it at
        // the INTENT layer (visible / treatment / driveUT / body / conic), removing the transitive argument.

        // The exact re-aim fixture PhaseFactoryTests uses: a recorded heliocentric (Sun) conic, re-synthesized
        // into a reference-distinct override aimed at the target's current position (sma differs, isPredicted
        // false => the generated transfer), with the plan's common ancestor "Sun".
        private static MockTrajectory ReaimedHeliocentricRecording()
            => new MockTrajectory
            {
                RecordingId = "rec-reaim",
                VesselName = "Kerbal X",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 9e9, eccentricity = 0.2 },
                },
            };

        private static List<OrbitSegment> ReaimedOverride()
            => new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 7.777e9, eccentricity = 0.5, isPredicted = false },
            };

        [Theory]
        [InlineData(5.0)]   // before the transfer window -> outside/hidden for both
        [InlineData(20.0)]  // on the re-aimed heliocentric transfer conic (the high-risk leg)
        [InlineData(50.0)]  // past the window end -> hidden for both
        public void Reaimed_TwoSpines_EmitIdenticalIntents(double liveUT)
        {
            var traj = ReaimedHeliocentricRecording();
            List<OrbitSegment> overrideSegs = ReaimedOverride();
            Assert.NotSame(traj.OrbitSegments, overrideSegs); // sanity: reference-distinct == re-aimed

            GhostRenderChain assembler = BuildAssemblerChain(
                traj, 0, 40, overrideSegs, ancestor: "Sun", faithfulFallback: false);
            PhaseChain factory = BuildFactoryChain(
                traj, 0, 40, overrideSegs, ancestor: "Sun", faithfulFallback: false);

            GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                ChainSampler.Sample(assembler, liveUT, NonLooped), default(GhostRenderIntent), traj.VesselName);
            GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                factory, liveUT, NonLooped, default(GhostRenderIntent), traj.VesselName);

            AssertIntentParity(aIntent, fIntent);
        }

        [Fact]
        public void Reaimed_FullSweep_TwoSpines_EmitIdenticalIntents_WithSharedPrior()
        {
            // The continuous-run re-aim proof: walk [-5, 45] at 0.5s threading the SAME prior through both
            // spines (as RunFrame does), under the re-aimed override. A divergence on ANY frame (the conic the
            // re-aim emits, the gap on either side of the trimmed transfer, the held-vs-retired choice) fails
            // here - the headless proof that flag-ON renders the re-aimed geometry identically to flag-OFF.
            var traj = ReaimedHeliocentricRecording();
            List<OrbitSegment> overrideSegs = ReaimedOverride();
            GhostRenderChain assembler = BuildAssemblerChain(
                traj, 0, 40, overrideSegs, ancestor: "Sun", faithfulFallback: false);
            PhaseChain factory = BuildFactoryChain(
                traj, 0, 40, overrideSegs, ancestor: "Sun", faithfulFallback: false);

            GhostRenderIntent aPrior = default(GhostRenderIntent);
            GhostRenderIntent fPrior = default(GhostRenderIntent);
            bool sawVisible = false;
            for (double ut = -5.0; ut <= 45.0; ut += 0.5)
            {
                GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                    ChainSampler.Sample(assembler, ut, NonLooped), aPrior, traj.VesselName);
                GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                    factory, ut, NonLooped, fPrior, traj.VesselName);

                AssertIntentParity(aIntent, fIntent);
                sawVisible |= aIntent.Visible;
                aPrior = aIntent;
                fPrior = fIntent;
            }
            Assert.True(sawVisible, "the re-aimed transfer leg must render visible somewhere in the sweep");
        }

        // ---- faithfulFallback parity (SHOULD 1): the producer-declined window ----
        [Theory]
        [InlineData(4.0)]
        [InlineData(20.0)]
        [InlineData(34.0)]
        [InlineData(50.0)]
        public void FaithfulFallback_TwoSpines_EmitIdenticalIntents(double liveUT)
        {
            // A producer-declined window assembles the recorded trajectory as-is (design §6.9 / §7). Both
            // spines carry faithfulFallback=true and must still emit byte-identical intents across the sweep.
            var traj = FaithfulChain();
            GhostRenderChain assembler = BuildAssemblerChain(
                traj, 0, 40, overrideSegs: null, ancestor: null, faithfulFallback: true);
            PhaseChain factory = BuildFactoryChain(
                traj, 0, 40, overrideSegs: null, ancestor: null, faithfulFallback: true);
            Assert.True(factory.IsFaithfulFallback); // sanity: the flag propagated to the typed chain

            GhostRenderIntent aIntent = GhostRenderDirector.Decide(
                ChainSampler.Sample(assembler, liveUT, NonLooped), default(GhostRenderIntent), traj.VesselName);
            GhostRenderIntent fIntent = GhostRenderDirector.Decide(
                factory, liveUT, NonLooped, default(GhostRenderIntent), traj.VesselName);

            AssertIntentParity(aIntent, fIntent);
        }
    }
}
