using System;
using System.Collections.Generic;
using Parsek;
using Parsek.MapRender;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-6 guard for <see cref="CrossMemberSeamStitcher"/> (migration plan §8 / design §9.1): the ONE
    /// cross-member geometric seam built in v1 — the orbit↔landing G1 descent re-stitch.
    ///
    /// Covers (1) the absorbed swept-deorbit-head / captureShift clock join (re-anchored descent head), (2)
    /// the per-leg head-gate, (3) the G1 tangent-match math + the <c>rigid-seam-tangent-discontinuity</c>
    /// predicate, (4) the compose-AFTER-the-remap ordering, and (5) the clean spine API
    /// <see cref="CrossMemberSeamStitcher.TryStitchDescentSeam"/> (descent member promoted to a visible
    /// first-class phase; non-descent / non-re-aim returns false byte-identically; out-of-window retires the
    /// sub-surface ghost rather than holding a stale below-surface sample).
    ///
    /// Each assertion states the bug it catches: a wrong clock would re-anchor the descent to the wrong UT
    /// (the icon-frozen / mis-rotation bug); a wrong tangent predicate would flag a continuous landing or
    /// miss a real kink; a missing retire would leave the documented sub-surface ghost; engaging on a
    /// non-descent unit would break flag-OFF parity.
    ///
    /// <para>The tracer-integration cases touch the shared <c>MapRenderTrace</c> / <c>ParsekLog</c> static
    /// state, so this class runs in the Sequential collection.</para>
    /// </summary>
    [Collection("Sequential")]
    public class CrossMemberSeamStitcherTests
    {
        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        private static TrackSection Sec(double s, double e, SegmentEnvironment env)
            => new TrackSection { startUT = s, endUT = e, environment = env };

        // A representative destination sidereal rotation period (Duna), as the existing DescentTriggerTests use.
        private const double DunaTrot = 65517.859375;

        // ---- Geometry chosen so cycle 0's LIVE window contains a real trigger window ----
        // conicEnd = recordedDeorbitUT + captureShift = 200 + (-150) = 50 (positive, inside cycle 0).
        // With Trot=300, 200 mod 300 = 200, so trigger = first t>=50 congruent to 200 (mod 300) = 200.
        // descentHead(liveUT) = 200 + (liveUT - 200); descent clip = [recordedDeorbitUT, descentEndUT] = [200,300].
        private const double RecDeorbit = 200.0;
        private const double DescentEnd = 300.0;
        private const double CaptureShift = -150.0;
        private const double TestTrot = 300.0;     // small period so the trigger lands at 200 (inside cycle 0)
        private const double Tpark = 80.0;
        private const double Cadence = 2000.0;     // single span instance; cycle 0 = liveUT [0, 2000)
        private const double SpanStart = 0.0;
        private const double SpanEnd = 1000.0;
        private const double PhaseAnchor = 0.0;
        private const int DescentMemberIdx = 0;
        private const int TransferMemberIdx = 2;

        // Build a descent-trigger LoopUnit + set whose descent set = {DescentMemberIdx}. Mirrors the working
        // fixture in GhostTrajectoryPolylineBuildTests.MakeDescentUnitSet (Supported plan + valid schedule =>
        // IsReaim; non-NaN periods + non-empty descent set => HasDescentTrigger).
        private static GhostPlaybackLogic.LoopUnitSet MakeDescentUnitSet(
            double recordedDeorbitUT = RecDeorbit, double descentEndUT = DescentEnd,
            double captureShift = CaptureShift, double trot = TestTrot, double tpark = Tpark)
        {
            var plan = new Parsek.Reaim.ReaimMissionPlan { Supported = true };
            var sched = new Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule { Valid = true };
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: TransferMemberIdx, memberIndices: new[] { DescentMemberIdx, TransferMemberIdx },
                spanStartUT: SpanStart, spanEndUT: SpanEnd, cadenceSeconds: Cadence, phaseAnchorUT: PhaseAnchor,
                overlapCadenceSeconds: Cadence, memberWindows: null, relaunchSchedule: null,
                reaimPlan: plan, reaimSchedule: sched,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: double.NaN, launchHoldEngaged: false, recordedSoiExitUT: double.NaN,
                descentMemberIndices: new[] { DescentMemberIdx }, recordedDeorbitUT: recordedDeorbitUT,
                descentEndUT: descentEndUT, destinationBodyRotationPeriodSeconds: trot,
                loiterPeriodSeconds: tpark, captureShiftSeconds: captureShift,
                transferMemberIndex: TransferMemberIdx);
            var ownerByIndex = new Dictionary<int, int>
            {
                { DescentMemberIdx, TransferMemberIdx }, { TransferMemberIdx, TransferMemberIdx },
            };
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { TransferMemberIdx, unit } }, ownerByIndex);
        }

        // The descent member's per-member trajectory: a Duna parking conic [150,200] then a body-fixed
        // atmospheric reentry/descent run [200..300] so the factory classifies the traced run as a
        // DescentPhase (a non-surface/non-approach traced run AFTER the first conic). The chain window is
        // [150,300].
        private static MockTrajectory DescentMemberTrajectory()
            => new MockTrajectory
            {
                RecordingId = "rec-descent",
                VesselName = "Duna Lander",
                Points = new List<TrajectoryPoint> { Pt(200, "Duna"), Pt(250, "Duna"), Pt(300, "Duna") },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 150, endUT = 200, bodyName = "Duna", semiMajorAxis = 400000, eccentricity = 0.01 },
                },
                TrackSections = new List<TrackSection>
                {
                    Sec(150, 200, SegmentEnvironment.ExoBallistic),
                    Sec(200, 300, SegmentEnvironment.Atmospheric),
                },
            };

        private static PhaseChain DescentMemberChain()
            => PhaseFactory.BuildPhaseChain(
                DescentMemberTrajectory(), DescentMemberIdx, instanceKey: 0,
                windowStartUT: 150, windowEndUT: 300);

        // ---- (1) The absorbed swept-deorbit-head clock: TryResolveDescentSeamHead ----

        [Fact]
        public void DescentSeamHead_TriggeredFrame_ReAnchorsToSweptDeorbitHead()
        {
            // The defining clock: at a live UT inside the clip the re-anchored head is
            // recordedDeorbitUT + (liveUT - triggerUT). With trigger=200, liveUT=250 => head=250.
            var units = MakeDescentUnitSet();
            Assert.True(units.TryGetUnitForMember(DescentMemberIdx, out GhostPlaybackLogic.LoopUnit unit));
            Assert.True(unit.HasDescentTrigger);

            // The same triggerUT the head re-anchors on (shared source).
            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, RecDeorbit, TestTrot, CaptureShift, null,
                out _, out _, out double triggerUT);
            Assert.Equal(200.0, triggerUT, 6); // congruent to deorbit (200) mod Trot (300), >= entry (50)

            double liveUT = triggerUT + 50.0; // inside the [200,300] clip
            bool ok = CrossMemberSeamStitcher.TryResolveDescentSeamHead(
                unit, DescentMemberIdx, unitCycle: 0, currentUT: liveUT,
                memberStartUT: 150, memberEndUT: 300,
                out double head, out Parsek.Reaim.DescentTrigger.DescentHeadPhase phase);

            Assert.True(ok);
            Assert.Equal(Parsek.Reaim.DescentTrigger.DescentHeadPhase.Descent, phase);
            Assert.Equal(RecDeorbit + (liveUT - triggerUT), head, 6); // swept deorbit head, forward-only
        }

        [Fact]
        public void DescentSeamHead_BeforeTrigger_IsLoiter_NotRendered()
        {
            // Before the trigger (entry <= liveUT < trigger) the unit is in Loiter: the descent member is
            // HIDDEN (the icon circles the transfer member's conic), so the stitcher does not render it.
            var units = MakeDescentUnitSet();
            units.TryGetUnitForMember(DescentMemberIdx, out GhostPlaybackLogic.LoopUnit unit);

            // entry = 50, trigger = 200 -> a UT strictly between is Loiter.
            bool ok = CrossMemberSeamStitcher.TryResolveDescentSeamHead(
                unit, DescentMemberIdx, unitCycle: 0, currentUT: 120.0,
                memberStartUT: 150, memberEndUT: 300, out double head, out var phase);

            Assert.False(ok);
            Assert.True(double.IsNaN(head));
            Assert.Equal(Parsek.Reaim.DescentTrigger.DescentHeadPhase.Loiter, phase);
        }

        [Fact]
        public void DescentSeamHead_NonDescentMember_ReturnsFalse_ByteIdentical()
        {
            // The transfer member (not a descent-set member) is never re-anchored by this clock.
            var units = MakeDescentUnitSet();
            units.TryGetUnitForMember(TransferMemberIdx, out GhostPlaybackLogic.LoopUnit unit);

            bool ok = CrossMemberSeamStitcher.TryResolveDescentSeamHead(
                unit, TransferMemberIdx, unitCycle: 0, currentUT: 250.0,
                memberStartUT: 0, memberEndUT: 1000, out double head, out _);

            Assert.False(ok);
            Assert.True(double.IsNaN(head));
        }

        // ---- (2) The per-leg head-gate (transfer-member deorbit-tail legs sweep to the seam) ----

        [Fact]
        public void DeorbitTailLegHead_EligibleLegBelowSeam_GatesOnSweptHead()
        {
            // A deorbit-tail leg (leg end at/below the seam) gates on the swept deorbit head; a non-eligible
            // leg keeps the loop head. This is the absorbed ResolveTransferLegHeadUT contract.
            double seamUT = 200.0, eps = 1.0, loopHead = 999.0, deorbitTailHead = 180.0;

            double belowSeam = CrossMemberSeamStitcher.ResolveDeorbitTailLegHead(
                legEndUT: 190.0, seamUT, eps, loopHead, deorbitTailHead, deorbitTailLegEligible: true);
            Assert.Equal(deorbitTailHead, belowSeam);

            double aboveSeam = CrossMemberSeamStitcher.ResolveDeorbitTailLegHead(
                legEndUT: 250.0, seamUT, eps, loopHead, deorbitTailHead, deorbitTailLegEligible: true);
            Assert.Equal(loopHead, aboveSeam);

            double notEligible = CrossMemberSeamStitcher.ResolveDeorbitTailLegHead(
                legEndUT: 190.0, seamUT, eps, loopHead, deorbitTailHead, deorbitTailLegEligible: false);
            Assert.Equal(loopHead, notEligible);
        }

        // ---- (3) The G1 tangent-match math + the rigid-seam-tangent-discontinuity predicate ----

        [Fact]
        public void TangentSeam_AlignedTangents_AreContinuous()
        {
            var leaving = new Vector3(1f, 0f, 0f);
            var entering = new Vector3(1f, 0.01f, 0f); // ~0.57deg, well within ~5.7deg tolerance
            Assert.True(CrossMemberSeamStitcher.IsTangentSeamContinuous(leaving, entering));
        }

        [Fact]
        public void TangentSeam_DivergentTangents_AreDiscontinuity()
        {
            var leaving = new Vector3(1f, 0f, 0f);
            var entering = new Vector3(0f, 1f, 0f); // 90deg kink => discontinuity
            Assert.False(CrossMemberSeamStitcher.IsTangentSeamContinuous(leaving, entering));
        }

        [Fact]
        public void TangentSeam_DegenerateTangent_IsContinuous_NoFalseAnomaly()
        {
            // An unmeasurable (zero-length / non-finite) tangent is NOT a discontinuity (no false anomaly).
            Assert.True(CrossMemberSeamStitcher.IsTangentSeamContinuous(Vector3.zero, new Vector3(1f, 0f, 0f)));
            Assert.True(CrossMemberSeamStitcher.IsTangentSeamContinuous(
                new Vector3(float.NaN, 0f, 0f), new Vector3(1f, 0f, 0f)));
        }

        [Fact]
        public void TangentSeam_RespectsCustomTolerance()
        {
            var leaving = new Vector3(1f, 0f, 0f);
            var entering = new Vector3(1f, 1f, 0f); // 45deg
            // 45deg is over the default ~5.7deg tolerance, but under a generous 60deg (1.05 rad) tolerance.
            Assert.False(CrossMemberSeamStitcher.IsTangentSeamContinuous(leaving, entering));
            Assert.True(CrossMemberSeamStitcher.IsTangentSeamContinuous(leaving, entering, toleranceRadians: 1.05));
        }

        [Fact]
        public void TangentFromPositions_FirstDifference_IsTheSegmentDirection()
        {
            Vector3 t = CrossMemberSeamStitcher.TangentFromPositions(
                new Vector3(0f, 0f, 0f), new Vector3(3f, 4f, 0f));
            Assert.Equal(3f, t.x, 4f);
            Assert.Equal(4f, t.y, 4f);
        }

        [Fact]
        public void TangentFromPositions_DegeneratePair_IsZero()
        {
            // Equal endpoints / non-finite => zero (an unmeasurable tangent, no false anomaly downstream).
            Assert.Equal(Vector3.zero, CrossMemberSeamStitcher.TangentFromPositions(
                new Vector3(1f, 1f, 1f), new Vector3(1f, 1f, 1f)));
            Assert.Equal(Vector3.zero, CrossMemberSeamStitcher.TangentFromPositions(
                new Vector3(0f, 0f, 0f), new Vector3(float.NaN, 0f, 0f)));
        }

        [Fact]
        public void OrbitLandingSeam_IsRigidG1_OnCamera()
        {
            // The descent re-stitch seam is the canonical Rigid + G1 + OnCamera contract (a tangent kink is a
            // real, visible discontinuity to surface).
            PhaseSeam seam = CrossMemberSeamStitcher.BuildOrbitLandingSeam();
            Assert.Equal(PhaseSeamKind.Rigid, seam.Kind);
            Assert.Equal(ContinuityOrder.G1, seam.Continuity);
            Assert.True(seam.RequiresTangentMatch);
            Assert.True(seam.OnCamera);
        }

        // ---- (4) Compose-AFTER-the-remap ordering ----

        [Fact]
        public void Stitch_ComposesAfterRemap_HeadIsReAnchored_NotTheRawSampleUT()
        {
            // The ordering contract (design §9.1): the stitcher composes AFTER the span-clock remap. The
            // stitched DriveUT is the RE-ANCHORED descent head (recordedDeorbitUT + (liveUT - triggerUT)),
            // NOT the raw post-remap sampleUT it is handed. Proven by passing a deliberately-wrong sampleUT:
            // the stitcher ignores it and re-anchors off liveUT, so the result is independent of sampleUT.
            var units = MakeDescentUnitSet();
            PhaseChain chain = DescentMemberChain();

            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, RecDeorbit, TestTrot, CaptureShift, null,
                out _, out _, out double triggerUT);
            double liveUT = triggerUT + 50.0;
            double expectedHead = RecDeorbit + (liveUT - triggerUT); // 250

            bool a = CrossMemberSeamStitcher.TryStitchDescentSeam(
                chain, sampleUT: 999.0 /* wrong on purpose */, liveUT, units, out GhostSample sa);
            bool b = CrossMemberSeamStitcher.TryStitchDescentSeam(
                chain, sampleUT: -777.0 /* also wrong */, liveUT, units, out GhostSample sb);

            Assert.True(a);
            Assert.True(b);
            Assert.Equal(expectedHead, sa.DriveUT, 6);
            Assert.Equal(sa.DriveUT, sb.DriveUT); // independent of the (wrong) sampleUT => re-anchored, not raw
        }

        // ---- (5) The spine API: TryStitchDescentSeam ----

        [Fact]
        public void Stitch_TriggeredDescentMember_PromotesVisibleTracedPathDescent()
        {
            var units = MakeDescentUnitSet();
            PhaseChain chain = DescentMemberChain();

            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, RecDeorbit, TestTrot, CaptureShift, null,
                out _, out _, out double triggerUT);
            double liveUT = triggerUT + 50.0; // head = 250, inside the descent run [200,300]

            bool ok = CrossMemberSeamStitcher.TryStitchDescentSeam(
                chain, sampleUT: liveUT, liveUT, units, out GhostSample stitched);

            Assert.True(ok);
            Assert.Equal(Coverage.InSegment, stitched.Coverage);
            Assert.Equal(Treatment.TracedPath, stitched.Treatment); // the promoted body-fixed descent
            Assert.Equal("Duna", stitched.FrameBodyName);
            Assert.Equal(250.0, stitched.DriveUT, 6);

            // The promoted descent carries the cross-member orbit↔landing seam: the stitcher re-stamps the
            // segment's LEADING seam to Rigid (the factory leaves it None; the seam is the stitcher's to own).
            // A None leading seam here would be the bug — the orbit↔landing join would render with no seam
            // contract and the G1 discontinuity check downstream would have nothing to assert against.
            Assert.Equal(SeamKind.Rigid, stitched.Segment.LeadingSeam);

            // The promoted phase covering the head is a first-class DescentPhase (no longer hidden in the
            // transfer member).
            Assert.True(chain.TryGetPhase(stitched.DriveUT, out TrajectoryPhase phase, out _));
            Assert.IsType<DescentPhase>(phase);
        }

        [Fact]
        public void StampOrbitLandingSeam_SetsRigidLeading_PreservesEverythingElse()
        {
            // The stitcher owns the cross-member seam: StampOrbitLandingSeam re-stamps ONLY the leading seam
            // to Rigid (BuildOrbitLandingSeam mapped to legacy SeamKind) and carries every other field through.
            var original = new RenderSegment(
                SegmentKind.Landing, Treatment.TracedPath, 200.0, 300.0, "Duna",
                SegmentPayload.Traced, isGenerated: false,
                leadingSeam: SeamKind.None, trailingSeam: SeamKind.FlexibleSoi);

            RenderSegment seamed = CrossMemberSeamStitcher.StampOrbitLandingSeam(original);

            Assert.Equal(SeamKind.Rigid, seamed.LeadingSeam);          // the new orbit↔landing seam
            Assert.Equal(SeamKind.FlexibleSoi, seamed.TrailingSeam);   // unchanged
            Assert.Equal(original.Kind, seamed.Kind);
            Assert.Equal(original.Treatment, seamed.Treatment);
            Assert.Equal(original.StartUT, seamed.StartUT);
            Assert.Equal(original.EndUT, seamed.EndUT);
            Assert.Equal(original.FrameBodyName, seamed.FrameBodyName);
            Assert.Equal(original.IsGenerated, seamed.IsGenerated);
        }

        [Fact]
        public void Stitch_NonReaimUnit_ReturnsFalse_ByteIdentical()
        {
            // An Empty (non-looped) unit set has no descent trigger => the stitcher never engages, so the
            // base coverage path renders unchanged (flag-OFF parity preserved when the flag is on).
            PhaseChain chain = DescentMemberChain();
            // The bool false IS the decline signal; the caller (ChainSampler) discards the out-param on a
            // false return and falls through to its own coverage path, so an out-param assertion here would be
            // tautological (default vs default). The load-bearing check is the bool.
            bool ok = CrossMemberSeamStitcher.TryStitchDescentSeam(
                chain, sampleUT: 250.0, liveUT: 250.0, GhostPlaybackLogic.LoopUnitSet.Empty, out _);
            Assert.False(ok);
        }

        [Fact]
        public void Stitch_NonDescentMember_OfDescentUnit_ReturnsFalse()
        {
            // The transfer member of a descent-trigger unit is NOT a descent-set member, so the stitcher
            // declines (the transfer member renders through its own path).
            var units = MakeDescentUnitSet();
            PhaseChain transferChain = PhaseFactory.BuildPhaseChain(
                DescentMemberTrajectory(), TransferMemberIdx, instanceKey: 0, windowStartUT: 150, windowEndUT: 300);
            bool ok = CrossMemberSeamStitcher.TryStitchDescentSeam(
                transferChain, sampleUT: 250.0, liveUT: 250.0, units, out _);
            Assert.False(ok);
        }

        [Fact]
        public void Stitch_PastDescentEnd_RetiresSubSurfaceGhost_NoHeldSample()
        {
            // SUB-SURFACE-GHOST-RETIRES (the documented bug closed): once the clip is Done (liveUT past the
            // descent window), the stitcher returns FALSE WITHOUT a held sample, so the descent member's
            // intent falls to Hidden and the ghost RETIRES rather than clamping to a stale below-surface
            // sample. Pick a liveUT well past the trigger + clip duration (100s clip), inside cycle 0.
            var units = MakeDescentUnitSet();
            PhaseChain chain = DescentMemberChain();

            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, RecDeorbit, TestTrot, CaptureShift, null,
                out _, out _, out double triggerUT);
            double pastEnd = triggerUT + 150.0; // > trigger + 100s clip => Done

            bool ok = CrossMemberSeamStitcher.TryResolveDescentSeamHead(
                units.TryGetUnitForMember(DescentMemberIdx, out var u) ? u : default,
                DescentMemberIdx, 0, pastEnd, 150, 300, out _, out var phase);
            Assert.False(ok);
            Assert.Equal(Parsek.Reaim.DescentTrigger.DescentHeadPhase.Done, phase);

            // RETIRE is signaled by the bool false WITHOUT a held sample: the caller (ChainSampler) discards
            // the out-param on a false return and renders nothing (GhostSample.Outside), so the sub-surface
            // ghost retires. Asserting the unread out-param (default vs default) would be tautological; the
            // bool is the contract.
            bool stitchOk = CrossMemberSeamStitcher.TryStitchDescentSeam(
                chain, sampleUT: pastEnd, liveUT: pastEnd, units, out _);
            Assert.False(stitchOk);
        }

        [Fact]
        public void Stitch_NullChain_OrNullUnits_ReturnsFalse()
        {
            Assert.False(CrossMemberSeamStitcher.TryStitchDescentSeam(
                null, 0, 0, MakeDescentUnitSet(), out _));
            Assert.False(CrossMemberSeamStitcher.TryStitchDescentSeam(
                DescentMemberChain(), 0, 0, null, out _));
        }

        // ---- (6) MapRenderTrace integration (the standing priority: "full logging coverage for the map
        // render tracer (integration with it)") ----

        [Fact]
        public void DescentStitchedDetails_CarryMemberHeadPhaseAndRigidG1Seam()
        {
            // The Tier-A DescentStitched structural line carries the promoted member, the re-anchored head,
            // the head phase, and the Rigid+G1 seam tag. Asserting the PURE builder (no global sink) is
            // deterministic in the full parallel suite, unlike a ParsekLog.TestSinkForTesting round-trip
            // (ParsekLog's [ThreadStatic] sink + SuppressLogging are poisoned by sibling tests).
            string details = CrossMemberSeamStitcher.BuildDescentStitchedDetails(
                committedIndex: 0, reAnchoredHead: 250.0,
                phase: Parsek.Reaim.DescentTrigger.DescentHeadPhase.Descent);

            Assert.Contains("member=0", details);
            Assert.Contains("reAnchoredHead=250", details);
            Assert.Contains("phase=Descent", details);
            Assert.Contains("seam=rigid+G1", details);
            Assert.Contains("onCamera=true", details);
        }

        [Fact]
        public void TangentDiscontinuity_TokenDetailsAndRaiseDecision()
        {
            // The Tier-C anomaly token is stable + grep-able, its detail line carries the angle, tolerance,
            // and both tangents, and the raise DECISION is gated on IsTangentSeamContinuous (raise ONLY on
            // divergence). Pure asserts (no global sink) - deterministic in the full parallel suite.
            Assert.Equal("rigid-seam-tangent-discontinuity", MapRenderTrace.AnomalyRigidSeamTangentDiscontinuity);

            var leaving = new Vector3(1f, 0f, 0f);
            var divergent = new Vector3(0f, 1f, 0f); // 90deg kink -> would raise
            Assert.False(CrossMemberSeamStitcher.IsTangentSeamContinuous(leaving, divergent));

            string details = CrossMemberSeamStitcher.BuildTangentDiscontinuityDetails(
                leaving, divergent, measuredAngleRadians: Math.PI / 2.0);
            Assert.Contains("angle=1.5708rad", details);
            Assert.Contains("tol=", details);
            Assert.Contains("leaving=(1.00,0.00,0.00)", details);
            Assert.Contains("entering=(0.00,1.00,0.00)", details);

            var aligned = new Vector3(1f, 0.01f, 0f); // ~0.57deg -> would NOT raise
            Assert.True(CrossMemberSeamStitcher.IsTangentSeamContinuous(leaving, aligned));
        }

        [Fact]
        public void Stitch_Triggered_RecordsDescentStitchTraceOnce_OnChange()
        {
            // WIRING + rate-limit (deterministic, no ParsekLog global): a triggered stitch reaches the Tier-A
            // emit gate exactly once, and a second same-frame/same-phase stitch is suppressed by the
            // once-per-event guard (no per-frame spam). Asserting via the MapRenderTrace signature-dict seam
            // (which the stitcher's EmitDescentStitchedTraceOnChange populates only when it actually reaches
            // the emit) proves the wiring without depending on the shared ParsekLog.TestSinkForTesting (whose
            // cross-test global routing is the established flake source - see project memory).
            var units = MakeDescentUnitSet();
            PhaseChain chain = DescentMemberChain();
            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, RecDeorbit, TestTrot, CaptureShift, null,
                out _, out _, out double triggerUT);
            double liveUT = triggerUT + 50.0;

            bool prevForce = MapRenderTrace.ForceEnabledForTesting;
            try
            {
                MapRenderTrace.Reset(); // clear the per-pid once-per-event signature dict
                MapRenderTrace.ForceEnabledForTesting = true;
                MapRenderTrace.FrameCounterOverrideForTesting = () => 0; // Time.frameCount is a Unity ECall
                Assert.Equal(0, MapRenderTrace.DescentStitchSignatureCountForTesting);

                bool a = CrossMemberSeamStitcher.TryStitchDescentSeam(chain, liveUT, liveUT, units, out _);
                Assert.True(a);
                Assert.Equal(1, MapRenderTrace.DescentStitchSignatureCountForTesting); // wired: gate reached once

                bool b = CrossMemberSeamStitcher.TryStitchDescentSeam(chain, liveUT, liveUT, units, out _);
                Assert.True(b);
                Assert.Equal(1, MapRenderTrace.DescentStitchSignatureCountForTesting); // unchanged onset => no dup
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForce;
                MapRenderTrace.FrameCounterOverrideForTesting = null;
                MapRenderTrace.Reset();
            }
        }
    }
}
