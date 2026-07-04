using System.Collections.Generic;
using Parsek;
using Parsek.Display;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-2 guard for <see cref="PhaseFactory"/> (migration plan §4): the factory builds a typed
    /// <see cref="PhaseChain"/> from the SAME inputs as <see cref="ChainAssembler.Build"/>, and its
    /// emitted GEOMETRY must byte-match the assembler's <see cref="GhostRenderChain"/>. These tests cover
    /// (1) the per-phase-kind classification (faithful env-class vs re-aimed plan), (2) the park /
    /// BG-on-rails / single-recording / null edge cases, and (3) the geometry byte-parity round-trip per
    /// fixture. The byte-parity comparator itself is tested in <c>GeometryParityComparatorTests</c>.
    ///
    /// Each assertion states the bug it catches: a wrong subclass/kind would mis-label a phase (caught by
    /// the explicit kind asserts, which are NOT the parity gate); a wrong geometry handling (window,
    /// override, coalesce, body-split) would diverge from the assembler (caught by the byte-parity
    /// asserts, which ARE the gate).
    /// </summary>
    public class PhaseFactoryTests
    {
        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        private static TrackSection Sec(double s, double e, SegmentEnvironment env)
            => new TrackSection { startUT = s, endUT = e, environment = env };

        // ascent (Kerbin points 0..8) -> loiter (Kerbin orbit 10..30) -> arrival (Mun points 32..36)
        private static MockTrajectory FullChain()
            => new MockTrajectory
            {
                RecordingId = "rec-1",
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

        // Assert the factory's projected geometry byte-matches the assembler's chain for one fixture.
        private static void AssertByteParity(
            IPlaybackTrajectory traj, double wStart, double wEnd,
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface = null,
            IReadOnlyList<OrbitSegment> overrideSegs = null, string ancestor = null,
            bool faithfulFallback = false)
        {
            var assembler = ChainAssembler.Build(
                traj, committedIndex: 3, instanceKey: 0, wStart, wEnd, faithfulFallback,
                surface, overrideSegs, ancestor);
            var factory = PhaseFactory.BuildPhaseChain(
                traj, committedIndex: 3, instanceKey: 0, wStart, wEnd, faithfulFallback,
                surface, overrideSegs, ancestor);

            var result = GeometryParityComparator.Compare(factory, assembler);
            Assert.True(result.IsMatch, "geometry parity divergence: " + result);
        }

        // ---- Geometry byte-parity per fixture (the GATE) ----

        [Fact]
        public void FullChain_FactoryGeometry_ByteMatchesAssembler()
        {
            AssertByteParity(FullChain(), 0, 40);
        }

        [Fact]
        public void BelowSurfaceDescent_FactoryGeometry_ByteMatchesAssembler()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "rec-desc",
                Points = new List<TrajectoryPoint>
                {
                    Pt(12, "Kerbin"), Pt(16, "Kerbin"), Pt(20, "Kerbin"), Pt(24, "Kerbin"), Pt(28, "Kerbin"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 500000, eccentricity = 0 },
                },
            };
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider kerbin =
                (string b, out GhostTrajectoryPolylineRenderer.BodySurfaceInfo info) =>
                { info = new GhostTrajectoryPolylineRenderer.BodySurfaceInfo { radius = 600000 }; return b == "Kerbin"; };
            AssertByteParity(traj, 0, 40, surface: kerbin);
        }

        [Fact]
        public void Coalesce_FactoryGeometry_ByteMatchesAssembler()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "rec-frag",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 100, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0.001, epoch = 10 },
                    new OrbitSegment { startUT = 150, endUT = 300, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0.001, epoch = 150 },
                    new OrbitSegment { startUT = 311, endUT = 351, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0.001, epoch = 311 },
                    new OrbitSegment { startUT = 400, endUT = 5000, bodyName = "Kerbin", semiMajorAxis = -380000, eccentricity = 1.2, epoch = 400 },
                },
            };
            AssertByteParity(traj, 0, 6000);
        }

        [Fact]
        public void FaithfulFallbackFlag_FactoryGeometry_ByteMatchesAssembler()
        {
            AssertByteParity(FullChain(), 0, 40, faithfulFallback: true);
        }

        // ---- Per-phase-kind classification (NOT the parity gate; documents the typed identity) ----

        [Fact]
        public void FullChain_ClassifiesAscentLoiterArrival()
        {
            // env-class: ascent (Kerbin atmo) -> departure loiter (Kerbin orbit) -> arrival (Mun).
            var traj = FullChain();
            traj.TrackSections = new List<TrackSection>
            {
                Sec(0, 8, SegmentEnvironment.Atmospheric),     // ascent run
                Sec(10, 30, SegmentEnvironment.ExoBallistic),  // Kerbin parking
                Sec(32, 36, SegmentEnvironment.Approach),      // Mun approach
            };
            var chain = PhaseFactory.BuildPhaseChain(traj, 0, 0, 0, 40);

            Assert.Equal(3, chain.PhaseCount);
            Assert.IsType<AscentPhase>(chain.Phases[0]);
            Assert.Equal(PhaseKind.Ascent, chain.Phases[0].Kind);
            Assert.IsType<DepartureLoiterPhase>(chain.Phases[1]);
            Assert.Equal(PhaseKind.DepartureLoiter, chain.Phases[1].Kind);
            // The Mun arrival traced run after the orbit + an approach env => SoiArrival is conic-only here;
            // a traced Mun run after the first conic classifies as Descent unless surface/approach. With an
            // Approach env on the Mun run, the traced kind is approach -> not surface, after first conic =>
            // Descent. The leaf kind is non-parity; assert it is a traced phase on Mun.
            Assert.Equal(Treatment.TracedPath, chain.Phases[2].ResolveTreatment());
            Assert.Equal("Mun", ((AnchorFrame.BodyAnchor)chain.Phases[2].Anchor).BodyName);
        }

        [Fact]
        public void SurfaceEnv_ClassifiesSurfacePhase()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "rec-surf",
                Points = new List<TrajectoryPoint> { Pt(0, "Kerbin"), Pt(2, "Kerbin"), Pt(4, "Kerbin") },
                TrackSections = new List<TrackSection> { Sec(0, 4, SegmentEnvironment.SurfaceStationary) },
            };
            var chain = PhaseFactory.BuildPhaseChain(traj, 0, 0, 0, 10);
            Assert.Single(chain.Phases);
            Assert.IsType<SurfacePhase>(chain.Phases[0]);
            Assert.Equal(PhaseKind.Surface, chain.Phases[0].Kind);
        }

        [Fact]
        public void ReaimedMember_ClassifiesSynthesizedHeliocentricTransfer()
        {
            // A re-aimed override (reference-distinct from recorded) whose conic is the ancestor body and
            // isPredicted=false => the synthesized heliocentric transfer (HeliocentricTransferPhase,
            // Synthesized provenance). Geometry must still byte-match the assembler.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-reaim",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 9e9, eccentricity = 0.2 },
                },
            };
            var reaimed = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 7.777e9, eccentricity = 0.5, isPredicted = false },
            };
            var chain = PhaseFactory.BuildPhaseChain(
                traj, 0, 0, 0, 40, orbitSegmentsOverride: reaimed, reaimAncestorBody: "Sun");

            var transfer = Assert.Single(chain.Phases);
            Assert.IsType<HeliocentricTransferPhase>(transfer);
            Assert.Equal(PhaseKind.HeliocentricTransfer, transfer.Kind);
            Assert.Equal(SegmentProvenance.Synthesized, transfer.Provenance);
            // and byte-parity (the override conic flows through unchanged)
            AssertByteParity(traj, 0, 40, overrideSegs: reaimed, ancestor: "Sun");
        }

        [Fact]
        public void HeliocentricParkVariant_UnreachableThroughAssembler_ByteParityOnly()
        {
            // A re-aimed member with a recorded park copy on the ancestor (star) body that is NOT the
            // generated transfer (isPredicted=true so the isPredicted heuristic + non-ancestor-only-match
            // would NOT mark it Transfer; but it IS on the ancestor body) => synthesized DepartureLoiter
            // (the s15 heliocentric-park variant). We feed an override that does NOT mark this segment
            // generated (the ancestor match marks generated only via the override's matching ancestor body
            // path - so to isolate the park, the override's segment must be isPredicted=true).
            var traj = new MockTrajectory
            {
                RecordingId = "rec-park",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 9e9, eccentricity = 0.01, isPredicted = true },
                },
            };
            // Override reference-distinct but same shape, isPredicted=true so the assembler does NOT mark it
            // generated even with a matching ancestor (the ancestor-match marks generated; so to get a park
            // we must NOT match the ancestor). Use a NON-matching ancestor so the segment stays Loiter, and
            // the factory's park rule (re-aimed + conic body == ancestor) needs the ancestor to match -
            // so instead assert the byte-parity holds and the phase is a conic loiter.
            var reaimed = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 9e9, eccentricity = 0.01, isPredicted = true },
            };
            var chain = PhaseFactory.BuildPhaseChain(
                traj, 0, 0, 0, 40, orbitSegmentsOverride: reaimed, reaimAncestorBody: "Sun");

            // ancestor matches "Sun", segment not generated (isPredicted true, ancestor-match marks
            // generated only in ChainAssembler when isPredicted=false? No - ChainAssembler marks generated
            // when isPredicted OR ancestor-match) -> so this conic is generated -> HeliocentricTransfer.
            // The park-only case (non-generated ancestor conic) cannot arise from the assembler's marking,
            // so the realistic synthesized-departure-loiter is exercised by the dedicated ClassifySegment
            // test below. Here we only assert byte-parity.
            AssertByteParity(traj, 0, 40, overrideSegs: reaimed, ancestor: "Sun");
            Assert.Single(chain.Phases);
        }

        [Fact]
        public void ClassifySegment_ReaimedNonGeneratedAncestorConic_IsSynthesizedDepartureLoiter()
        {
            // Directly exercise the park rule in ClassifySegment: a re-aimed member, a non-generated conic
            // whose frame body == the ancestor => synthesized DepartureLoiterPhase (s15).
            var conic = new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 9e9 };
            var seg = new RenderSegment(
                SegmentKind.Loiter, Treatment.StockConic, 10, 30, "Sun",
                SegmentPayload.ForConic(conic), isGenerated: false);
            var traj = new MockTrajectory { RecordingId = "rec-park2" };
            var phase = PhaseFactory.ClassifySegment(
                seg, ordinal: 0, traj, instanceKey: 0, isReaimedMember: true, reaimAncestorBody: "Sun");

            Assert.IsType<DepartureLoiterPhase>(phase);
            Assert.Equal(SegmentProvenance.Synthesized, phase.Provenance);
        }

        // ---- Review S14: ResolveProvenance splits the assembler's IsGenerated over-mark ----
        // ChainAssembler marks generated = isPredicted OR re-aim-ancestor-match (parity-load-bearing,
        // must-not-fix #1), so a ballistic-extrapolated predicted RECORDED tail arrives IsGenerated=true.
        // The factory layer must label it FinalizedPredicted + a loiter phase, never Synthesized +
        // HeliocentricTransfer; re-aimed members keep Synthesized everywhere (re-aim semantics untouched).

        private static RenderSegment ConicSeg(
            double s, double e, string body, bool generated, bool predicted)
            => new RenderSegment(
                generated ? SegmentKind.Transfer : SegmentKind.Loiter,
                Treatment.StockConic, s, e, body,
                SegmentPayload.ForConic(new OrbitSegment
                {
                    startUT = s, endUT = e, bodyName = body,
                    semiMajorAxis = 700000, isPredicted = predicted,
                }),
                isGenerated: generated);

        [Fact]
        public void ResolveProvenance_GeneratedNonPredicted_IsSynthesized()
        {
            // The re-aimed synthesized transfer (isPredicted=false by construction, ancestor-marked).
            Assert.Equal(SegmentProvenance.Synthesized,
                PhaseFactory.ResolveProvenance(
                    ConicSeg(10, 30, "Sun", generated: true, predicted: false), isReaimedMember: true));
        }

        [Fact]
        public void ResolveProvenance_OverMarkedPredictedTail_NonReaimed_IsFinalizedPredicted()
        {
            // THE S14 case: a non-re-aimed member's predicted recorded tail, over-marked generated by the
            // assembler's isPredicted heuristic. Pre-S14 this returned Synthesized (and the classifier
            // made it a HeliocentricTransferPhase); the FinalizedPredicted branch was unreachable.
            Assert.Equal(SegmentProvenance.FinalizedPredicted,
                PhaseFactory.ResolveProvenance(
                    ConicSeg(10, 30, "Kerbin", generated: true, predicted: true), isReaimedMember: false));
        }

        [Fact]
        public void ResolveProvenance_GeneratedPredicted_ReaimedMember_StaysSynthesized()
        {
            // Conservative rule: a re-aimed member's generated segments stay Synthesized even when
            // predicted - re-aim semantics are untouched by the S14 split.
            Assert.Equal(SegmentProvenance.Synthesized,
                PhaseFactory.ResolveProvenance(
                    ConicSeg(10, 30, "Duna", generated: true, predicted: true), isReaimedMember: true));
        }

        [Fact]
        public void ResolveProvenance_RecordedPredictedConic_IsFinalizedPredicted()
        {
            // A predicted conic that is NOT generated-marked (direct classification path).
            Assert.Equal(SegmentProvenance.FinalizedPredicted,
                PhaseFactory.ResolveProvenance(
                    ConicSeg(10, 30, "Kerbin", generated: false, predicted: true), isReaimedMember: false));
        }

        [Fact]
        public void ResolveProvenance_PlainRecordedConic_IsRecorded()
        {
            Assert.Equal(SegmentProvenance.Recorded,
                PhaseFactory.ResolveProvenance(
                    ConicSeg(10, 30, "Kerbin", generated: false, predicted: false), isReaimedMember: false));
        }

        [Fact]
        public void ClassifySegment_OverMarkedPredictedTail_NonReaimed_IsLoiterNotTransfer()
        {
            // The classification half of S14: the over-marked predicted tail must classify as a LOITER
            // phase carrying FinalizedPredicted, never HeliocentricTransferPhase. Kind is non-parity
            // (GeometryParityComparator ignores it), so this is a label fix, not a geometry change.
            var traj = new MockTrajectory { RecordingId = "rec-predtail" };
            var phase = PhaseFactory.ClassifySegment(
                ConicSeg(10, 30, "Kerbin", generated: true, predicted: true),
                ordinal: 0, traj, instanceKey: 0, isReaimedMember: false, reaimAncestorBody: null);

            Assert.IsNotType<HeliocentricTransferPhase>(phase);
            Assert.IsType<DepartureLoiterPhase>(phase);
            Assert.Equal(SegmentProvenance.FinalizedPredicted, phase.Provenance);
        }

        // ---- Review N4: IsArrivalConic coalesces the raw list before the last-in-time check ----

        [Fact]
        public void IsArrivalConic_FragmentedDestinationPark_CoalescesBeforeLastInTimeCheck()
        {
            // The arrival park recorded as TWO contiguous same-orbit fragments: the assembler coalesces
            // before building the RenderSegment, so the factory sees ONE Duna conic [100,130] while the
            // RAW list's last fragment starts at 115. Pre-N4 the raw-list comparison read
            // seg.StartUT(100) < lastStart(115) and false-negatived the arrival promotion.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-frag",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000 },
                    new OrbitSegment { startUT = 100, endUT = 115, bodyName = "Duna", semiMajorAxis = 320000 },
                    new OrbitSegment { startUT = 115, endUT = 130, bodyName = "Duna", semiMajorAxis = 320000 },
                },
            };
            Assert.True(PhaseFactory.IsArrivalConic(Conic(100, 130, "Duna"), traj));
        }

        // ---- BG-on-rails: no env-class sections -> all-orbital chain, tolerated (no assert) ----

        [Fact]
        public void BgOnRails_NoTrackSections_AllOrbitalChain_NoSurfaceOrDescent()
        {
            // A BG on-rails recording emits orbit-bridge OrbitSegments and NO env-classified TrackSections.
            // The factory must build an all-orbital (conic) chain with NO Descent/Surface phase and must
            // NOT assert on the absent SegmentPhase data (design §11.3).
            var traj = new MockTrajectory
            {
                RecordingId = "rec-bg",
                Points = new List<TrajectoryPoint>(),       // no per-frame points
                TrackSections = new List<TrackSection>(),   // BG on-rails: no env-class sections
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 1000, bodyName = "Kerbin", semiMajorAxis = 800000, eccentricity = 0.6 },
                    new OrbitSegment { startUT = 1000, endUT = 5000, bodyName = "Sun", semiMajorAxis = 1.3e10, eccentricity = 0.1 },
                },
            };
            var chain = PhaseFactory.BuildPhaseChain(traj, 0, 0, 0, 6000);

            Assert.Equal(2, chain.PhaseCount);
            Assert.All(chain.Phases, p => Assert.Equal(Treatment.StockConic, p.ResolveTreatment()));
            Assert.DoesNotContain(chain.Phases, p => p.Kind == PhaseKind.Descent || p.Kind == PhaseKind.Surface);
            // body-change conic-to-conic byte-parity (Kerbin -> Sun FlexibleSoi seam)
            AssertByteParity(traj, 0, 6000);
        }

        // ---- single-recording empty Points ----

        [Fact]
        public void SingleRecording_EmptyPoints_ConicOnlyChain()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "rec-empty",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000 },
                },
            };
            var chain = PhaseFactory.BuildPhaseChain(traj, 0, 0, 0, 40);
            Assert.Single(chain.Phases);
            Assert.Equal(Treatment.StockConic, chain.Phases[0].ResolveTreatment());
            AssertByteParity(traj, 0, 40);
        }

        [Fact]
        public void SingleRecording_FullyEmpty_EmptyChain()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "rec-none",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
            };
            var chain = PhaseFactory.BuildPhaseChain(traj, 0, 0, 0, 40);
            Assert.Equal(0, chain.PhaseCount);
            AssertByteParity(traj, 0, 40);
        }

        // ---- null trajectory ----

        [Fact]
        public void NullTrajectory_EmptyChain_ByteMatchesAssembler()
        {
            var chain = PhaseFactory.BuildPhaseChain(null, 0, 0, 0, 40);
            Assert.Equal(0, chain.PhaseCount);
            Assert.Null(chain.RecordingId);
            // assembler also returns an empty chain for null traj
            var assembler = ChainAssembler.Build(null, 0, 0, 0, 40);
            Assert.True(GeometryParityComparator.Compare(chain, assembler).IsMatch);
        }

        // ---- chain-level fields propagate (window + faithful-fallback) ----

        [Fact]
        public void ChainLevelFields_PropagateFromAssembler()
        {
            var chain = PhaseFactory.BuildPhaseChain(FullChain(), 0, 0, 5, 35, faithfulFallback: true);
            Assert.Equal(5, chain.WindowStartUt);
            Assert.Equal(35, chain.WindowEndUt);
            Assert.True(chain.IsFaithfulFallback);
            Assert.Equal("rec-1", chain.RecordingId);
        }

        // ---- loud-assertion: parent-anchored child never handed a re-aimed override ----

        [Fact]
        public void ParentAnchoredChild_WithReaimedOverride_FallsBackToFaithful_LoudWarn()
        {
            // A controlled-decoupled child (ParentAnchorRecordingId != null) handed a re-aimed override
            // must NOT be silently body-framed onto a generated arc - the factory strips the override and
            // builds the faithful chain (the RenderSegment.cs:94-98 loud-assertion carry-forward).
            var traj = new MockTrajectory
            {
                RecordingId = "rec-child",
                ParentAnchorRecordingId = "rec-parent",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 9e9 },
                },
            };
            var reaimed = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Sun", semiMajorAxis = 7e9, isPredicted = false },
            };
            var chain = PhaseFactory.BuildPhaseChain(
                traj, 0, 0, 0, 40, orbitSegmentsOverride: reaimed, reaimAncestorBody: "Sun");

            // The faithful (recorded) conic (sma 9e9) is used, NOT the re-aimed override (7e9).
            var phase = Assert.Single(chain.Phases);
            Assert.True(phase is ConicPhase || phase is DepartureLoiterPhase || phase is ArrivalLoiterPhase);
            // The anchor is the parent-anchored-child frame, never a body-framed generated arc.
            Assert.IsType<AnchorFrame.ParentAnchoredChild>(phase.Anchor);
            // geometry uses the recorded conic
            var conicPhase = (ConicPhase)phase;
            Assert.Equal(9e9, conicPhase.Conic.semiMajorAxis);
        }

        [Fact]
        public void FaithfulParentAnchoredChild_WithTracedLeg_ByteMatchesAssembler()
        {
            // The direct proof of the FrameBodyName round-trip fix: a FAITHFUL (no override) parent-anchored
            // controlled-decoupled child (IsDebris=false, ParentAnchorRecordingId != null) with a TracedPath
            // leg gets a ParentAnchoredChild anchor (NOT a BodyAnchor), so the traced phase has no BodyAnchor
            // payload to resolve its frame body from. Before the fix, ProjectGeometry handed null as the
            // SampleContext frame body and TracedPhase.ResolveFrameBodyName returned null while the assembler
            // stamped the real recorded body ("Mun") -> a FrameBodyName false-fire. The geometry is genuinely
            // identical (the assembler stamps FrameBodyName from the recorded point's body regardless of
            // anchor), so the projection must reproduce it losslessly: this asserts byte-parity.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-faithful-child",
                ParentAnchorRecordingId = "rec-parent",
                IsDebris = false,
                // A traced leg on Mun (the parent-anchored child near its parent) + a Mun conic.
                Points = new List<TrajectoryPoint>
                {
                    Pt(0, "Mun"), Pt(2, "Mun"), Pt(4, "Mun"), Pt(6, "Mun"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Mun", semiMajorAxis = 200000, eccentricity = 0 },
                },
                TrackSections = new List<TrackSection>
                {
                    Sec(0, 6, SegmentEnvironment.Atmospheric),
                    Sec(10, 30, SegmentEnvironment.ExoBallistic),
                },
            };

            // The traced leg projects with the real body name even though the anchor is ParentAnchoredChild.
            var chain = PhaseFactory.BuildPhaseChain(traj, 3, 0, 0, 40);
            var traced = Assert.Single(chain.Phases, p => p.ResolveTreatment() == Treatment.TracedPath);
            Assert.IsType<AnchorFrame.ParentAnchoredChild>(traced.Anchor);
            var projected = GeometryParityComparator.ProjectGeometry(chain);
            Assert.Contains(projected, s => s.Treatment == Treatment.TracedPath && s.FrameBodyName == "Mun");

            // The gate: factory geometry byte-matches the assembler (no FrameBodyName divergence).
            AssertByteParity(traj, 0, 40);
        }

        // ---- Phase 7: BuildOrderedRecordedBodies (the pure body sequence the fail-closed classifier reads) ----

        [Fact]
        public void BuildOrderedRecordedBodies_CollapsesAdjacentDuplicates_KeepsNonAdjacentRepeat()
        {
            // Adjacent same-body orbits collapse to ONE run (a multi-orbit stay is not an SOI crossing); a
            // non-adjacent repeat (Kerbin -> Mun -> Kerbin) is a real return crossing and is kept. A mutation
            // that dropped the adjacent-collapse, used a non-Ordinal compare, or reordered, fails here.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-bodies",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 0, endUT = 10, bodyName = "Kerbin" },
                    new OrbitSegment { startUT = 10, endUT = 20, bodyName = "Kerbin" }, // adjacent dup -> collapsed
                    new OrbitSegment { startUT = 20, endUT = 30, bodyName = "Mun" },
                    new OrbitSegment { startUT = 30, endUT = 40, bodyName = "Kerbin" }, // non-adjacent return -> kept
                },
            };
            Assert.Equal(new[] { "Kerbin", "Mun", "Kerbin" }, PhaseFactory.BuildOrderedRecordedBodies(traj));
        }

        [Fact]
        public void BuildOrderedRecordedBodies_SkipsNullAndEmptyBodyNames()
        {
            // A null / empty bodyName orbit is skipped (not appended, and does not break the adjacent-collapse
            // chain). A mutation that dropped the IsNullOrEmpty skip would emit null/"" entries.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-bodies-empty",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 0, endUT = 10, bodyName = null },
                    new OrbitSegment { startUT = 10, endUT = 20, bodyName = "" },
                    new OrbitSegment { startUT = 20, endUT = 30, bodyName = "Duna" },
                },
            };
            Assert.Equal(new[] { "Duna" }, PhaseFactory.BuildOrderedRecordedBodies(traj));
        }

        [Fact]
        public void BuildOrderedRecordedBodies_NullOrEmptyOrbitList_IsEmpty()
        {
            // A no-orbit recording is never a multi-body tour; null-tolerant (no NRE).
            Assert.Empty(PhaseFactory.BuildOrderedRecordedBodies(
                new MockTrajectory { RecordingId = "r", OrbitSegments = null }));
            Assert.Empty(PhaseFactory.BuildOrderedRecordedBodies(
                new MockTrajectory { RecordingId = "r", OrbitSegments = new List<OrbitSegment>() }));
        }

        // ---- ClassifyTracedKind / ResolveEnvPhaseForWindow / IsArrivalConic internal helpers ----
        // These exercise the branching faithful-leaf-identity helpers directly (PhaseFactory.cs ~330/350/389).
        // They are NOT the byte-parity gate (kind/env are non-parity classification), so they assert the
        // gameplay LABEL the helper resolves - the bug each catches is a wrong ascent/descent/surface/arrival
        // split, which would mislabel a phase in the typed spine.

        private static RenderSegment Traced(double s, double e, string body)
            => new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, s, e, body, SegmentPayload.Traced);

        private static RenderSegment Conic(double s, double e, string body)
            => new RenderSegment(
                SegmentKind.Loiter, Treatment.StockConic, s, e, body,
                SegmentPayload.ForConic(new OrbitSegment
                {
                    startUT = s, endUT = e, bodyName = body, semiMajorAxis = 700000, eccentricity = 0,
                }));

        [Fact]
        public void ClassifyTracedKind_AscentBeforeFirstConic_DescentAfter()
        {
            // The recording has one above-surface conic at [10,30]. A traced run that STARTS before that
            // first conic is an Ascent (the launch leg); one that starts at/after it is a Descent (the
            // deorbit/landing leg). With no surface env on either run the split is purely positional. A
            // mutation that dropped the FirstConicStartUT comparison (or flipped before/after) fails here.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-asc-desc",
                Points = new List<TrajectoryPoint>
                {
                    Pt(0, "Kerbin"), Pt(4, "Kerbin"), Pt(35, "Kerbin"), Pt(40, "Kerbin"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0 },
                },
                // No TrackSections: the env phase is null, so the kind is the positional ascent/descent split.
            };

            Assert.Equal(PhaseKind.Ascent,
                PhaseFactory.ClassifyTracedKind(Traced(0, 4, "Kerbin"), traj));   // before the [10,30] conic
            Assert.Equal(PhaseKind.Descent,
                PhaseFactory.ClassifyTracedKind(Traced(35, 40, "Kerbin"), traj)); // after the conic
        }

        [Fact]
        public void ClassifyTracedKind_SurfaceEnv_WinsOverAscentDescent()
        {
            // A traced run whose recorded env-class is SURFACE classifies Surface, EVEN when it starts AFTER
            // the first conic (where the positional rule would otherwise say Descent). The surface env is the
            // terminal landed state, which must win - a mutation that ran the ascent/descent split first
            // (ignoring the surface env) would mislabel a landed run as Descent.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-surf-wins",
                Points = new List<TrajectoryPoint> { Pt(35, "Kerbin"), Pt(40, "Kerbin") },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0 },
                },
                TrackSections = new List<TrackSection>
                {
                    // The traced run [35,40] overlaps a SurfaceStationary section -> "surface".
                    Sec(34, 41, SegmentEnvironment.SurfaceStationary),
                },
            };

            // Positionally this run is AFTER the conic (would be Descent), but the surface env wins.
            Assert.Equal(PhaseKind.Surface,
                PhaseFactory.ClassifyTracedKind(Traced(35, 40, "Kerbin"), traj));
        }

        [Fact]
        public void ResolveEnvPhaseForWindow_MultiSection_UsesLastOverlapping()
        {
            // When several TrackSections overlap the window, the LAST overlapping section's environment is
            // returned (a descent run ending in atmo/surface reads its TERMINAL class). A mutation that
            // returned the FIRST overlap, or stopped at the first match, would read "exo" here instead of
            // "surface".
            var traj = new MockTrajectory
            {
                RecordingId = "rec-multi-env",
                TrackSections = new List<TrackSection>
                {
                    Sec(0, 10, SegmentEnvironment.ExoBallistic),   // overlaps -> "exo"
                    Sec(10, 20, SegmentEnvironment.Atmospheric),   // overlaps -> "atmo"
                    Sec(20, 30, SegmentEnvironment.SurfaceStationary), // overlaps -> "surface" (LAST)
                    Sec(40, 50, SegmentEnvironment.Approach),      // does NOT overlap [5,25]
                },
            };

            // Window [5,25] overlaps the first three sections; the LAST overlapping (SurfaceStationary) wins.
            Assert.Equal("surface", PhaseFactory.ResolveEnvPhaseForWindow(traj, 5, 25));
            // A null/empty TrackSection list returns null (BG-on-rails tolerated, no assert).
            Assert.Null(PhaseFactory.ResolveEnvPhaseForWindow(
                new MockTrajectory { RecordingId = "r", TrackSections = null }, 0, 100));
        }

        [Theory]
        [InlineData(true)]   // ApproachEnv: a traced/conic window with an "approach" env is an arrival
        [InlineData(false)]  // no env, departure body conic: not an arrival
        public void IsArrivalConic_ApproachEnv_True(bool approach)
        {
            // A recorded conic whose env-class is "approach" is an ARRIVAL loiter regardless of body. The
            // approach env is the destination-arrival signal; a mutation that dropped the approach short-
            // circuit would mislabel a destination approach as DepartureLoiter.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-approach",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000 },
                },
                TrackSections = approach
                    ? new List<TrackSection> { Sec(10, 30, SegmentEnvironment.Approach) }
                    : new List<TrackSection> { Sec(10, 30, SegmentEnvironment.ExoBallistic) },
            };

            // The conic on the SAME (departure) body: approach env -> arrival; exo env -> not arrival.
            Assert.Equal(approach,
                PhaseFactory.IsArrivalConic(Conic(10, 30, "Kerbin"), traj));
        }

        [Fact]
        public void IsArrivalConic_DifferentBodyLastInTime_True()
        {
            // The first conic's body is the DEPARTURE body. A later conic on a DIFFERENT body that is the
            // LAST conic in time is the destination park -> arrival. A mutation that dropped the
            // different-body OR the last-in-time gate would mislabel the destination park.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-arrival",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000 }, // departure (first)
                    new OrbitSegment { startUT = 100, endUT = 130, bodyName = "Duna", semiMajorAxis = 400000 }, // arrival (last)
                },
            };

            // The Duna conic at [100,130] is on a different body AND last in time -> arrival.
            Assert.True(PhaseFactory.IsArrivalConic(Conic(100, 130, "Duna"), traj));
        }

        [Fact]
        public void IsArrivalConic_DepartureBodyConic_False()
        {
            // The departure-body conic (the FIRST conic's body, not last in time) is a DEPARTURE loiter, not
            // arrival - even when a later different-body conic exists. The different-body gate must reject a
            // conic that is ON the first (departure) body.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-departure",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000 }, // departure (first)
                    new OrbitSegment { startUT = 100, endUT = 130, bodyName = "Duna", semiMajorAxis = 400000 }, // a later arrival
                },
            };

            // The Kerbin departure conic at [10,30]: same body as the first conic -> NOT arrival.
            Assert.False(PhaseFactory.IsArrivalConic(Conic(10, 30, "Kerbin"), traj));
        }
    }
}
