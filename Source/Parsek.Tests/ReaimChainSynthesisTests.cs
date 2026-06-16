using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P0 + P1 of the re-aim whole-chain synthesis fix (reaim-fix-plan.md). These are the
    /// regression-critical scaffolding tests: the feature is default-OFF and changes NO runtime behavior
    /// yet, and the load-bearing guarantee is that the flag-OFF path is byte-identical to today.
    ///
    /// <para>P0 covers the real-topology Duna One fixture (the 14-segment Ike-threaded chain, NOT the
    /// 3-leg idealization) and the pure UT-tiling continuity harness. P1 covers the
    /// <see cref="ReaimChainSynthesis"/> feature flag (default false, settings-backed, test override) and
    /// the FLAG-OFF IDENTITY proof at the pure <see cref="ReaimSegmentAssembler.AssembleWindowChain"/>
    /// seam.</para>
    ///
    /// <para>Touches shared static state (<see cref="ReaimChainSynthesis.ForceEnabledForTesting"/>,
    /// <c>ParsekSettings.CurrentOverrideForTesting</c>, the settings store), so it runs in the
    /// Sequential collection and resets every override in <see cref="Dispose"/>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ReaimChainSynthesisTests : System.IDisposable
    {
        public ReaimChainSynthesisTests()
        {
            ReaimChainSynthesis.Reset();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            ReaimChainSynthesis.Reset();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
        }

        // Strict per-field OrbitSegment value equality. The flag-OFF identity proof is load-bearing, so
        // this compares EVERY field, not just the few a typical re-aim test asserts.
        private static void AssertSegmentValueEqual(OrbitSegment expected, OrbitSegment actual)
        {
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.startUT, actual.startUT, 9);
            Assert.Equal(expected.endUT, actual.endUT, 9);
            Assert.Equal(expected.inclination, actual.inclination, 9);
            Assert.Equal(expected.eccentricity, actual.eccentricity, 9);
            Assert.Equal(expected.semiMajorAxis, actual.semiMajorAxis, 9);
            Assert.Equal(expected.longitudeOfAscendingNode, actual.longitudeOfAscendingNode, 9);
            Assert.Equal(expected.argumentOfPeriapsis, actual.argumentOfPeriapsis, 9);
            Assert.Equal(expected.meanAnomalyAtEpoch, actual.meanAnomalyAtEpoch, 9);
            Assert.Equal(expected.epoch, actual.epoch, 9);
            Assert.Equal(expected.isPredicted, actual.isPredicted);
            Assert.Equal(expected.orbitalFrameRotation, actual.orbitalFrameRotation);
            Assert.Equal(expected.angularVelocity, actual.angularVelocity);
        }

        private static void AssertListValueEqual(
            IReadOnlyList<OrbitSegment> expected, IReadOnlyList<OrbitSegment> actual)
        {
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
                AssertSegmentValueEqual(expected[i], actual[i]);
        }

        // =====================================================================================
        // P0 - real-topology fixture
        // =====================================================================================

        [Fact]
        public void DunaOneFixture_HasRealChainTopology_NotThreeLegIdealization()
        {
            var member = ReaimChainSynthesisFixtures.BuildDunaOneTransferMember();

            // Parking + escape + 3 Sun coasts + 2 Duna capture + 2 Ike + 2 Duna recapture + 4 descent = 15.
            Assert.Equal(15, member.Count);

            // A circular Kerbin parking orbit (ecc 0) then the ESCAPE HYPERBOLA (sma<0, ecc>1).
            Assert.Equal("Kerbin", member[0].bodyName);
            Assert.True(member[0].semiMajorAxis > 0.0 && member[0].eccentricity < 1.0, "parking is elliptic");
            Assert.Equal("Kerbin", member[1].bodyName);
            Assert.True(member[1].semiMajorAxis < 0.0 && member[1].eccentricity > 1.0, "escape is hyperbolic");

            // THREE Sun heliocentric coasts (seg#9/10/11).
            int sunCoasts = 0;
            foreach (var s in member)
                if (s.bodyName == "Sun") sunCoasts++;
            Assert.Equal(3, sunCoasts);

            // A Duna CAPTURE HYPERBOLA (sma<0) follows the heliocentric run.
            Assert.Equal("Duna", member[5].bodyName);
            Assert.True(member[5].semiMajorAxis < 0.0 && member[5].eccentricity > 1.0, "capture is hyperbolic");

            // An IKE-SOI thread (the secondary moon - the Phase-4 multi-moon case the capture side fails
            // closed for). Two Ike segments are present.
            int ikeSegs = 0;
            foreach (var s in member)
                if (s.bodyName == "Ike") ikeSegs++;
            Assert.Equal(2, ikeSegs);

            // A Duna DESCENT: at least one elliptic (sma>0, ecc<1) Duna segment after the captures.
            bool hasDescentEllipse = false;
            for (int i = 6; i < member.Count; i++)
                if (member[i].bodyName == "Duna" && member[i].semiMajorAxis > 0.0 && member[i].eccentricity < 1.0)
                    hasDescentEllipse = true;
            Assert.True(hasDescentEllipse, "fixture has a Duna descent ellipse");
        }

        [Fact]
        public void DunaOneFixture_FreshListPerCall_NoSharedMutableState()
        {
            var a = ReaimChainSynthesisFixtures.BuildDunaOneTransferMember();
            var b = ReaimChainSynthesisFixtures.BuildDunaOneTransferMember();
            Assert.NotSame(a, b);
            // Mutating one copy must not affect the other.
            a[0] = new OrbitSegment { bodyName = "Mutated" };
            Assert.NotEqual("Mutated", b[0].bodyName);
        }

        // =====================================================================================
        // P0 - pure UT-tiling continuity harness
        // =====================================================================================

        [Fact]
        public void TilingHarness_ContiguousList_ReportsZeroGapsAndOverlaps()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 200 },
                new OrbitSegment { bodyName = "Sun", startUT = 200, endUT = 500 },
                new OrbitSegment { bodyName = "Duna", startUT = 500, endUT = 900 },
            };
            var report = SegmentTilingHarness.ComputeTiling(segs);

            Assert.Equal(2, report.Boundaries.Count);
            Assert.Equal(0, report.GapCount);
            Assert.Equal(0, report.OverlapCount);
            Assert.Equal(0.0, report.MaxGapSeconds, 9);
            Assert.Equal(0.0, report.MaxOverlapSeconds, 9);
            Assert.Equal(0.0, report.MaxAbsDiscontinuitySeconds, 9);
        }

        [Fact]
        public void TilingHarness_DetectsGapAndOverlap_WithMagnitudes()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "A", startUT = 100, endUT = 200 },
                new OrbitSegment { bodyName = "B", startUT = 250, endUT = 400 }, // +50 gap
                new OrbitSegment { bodyName = "C", startUT = 390, endUT = 600 }, // -10 overlap
            };
            var report = SegmentTilingHarness.ComputeTiling(segs);

            Assert.Equal(1, report.GapCount);
            Assert.Equal(1, report.OverlapCount);
            Assert.Equal(50.0, report.MaxGapSeconds, 9);
            Assert.Equal(10.0, report.MaxOverlapSeconds, 9);
            Assert.Equal(50.0, report.MaxAbsDiscontinuitySeconds, 9);

            Assert.True(report.Boundaries[0].IsGap);
            Assert.Equal(50.0, report.Boundaries[0].Discontinuity, 9);
            Assert.True(report.Boundaries[1].IsOverlap);
            Assert.Equal(-10.0, report.Boundaries[1].Discontinuity, 9);
        }

        [Fact]
        public void TilingHarness_NullOrSingle_NoBoundaries()
        {
            var empty = SegmentTilingHarness.ComputeTiling(null);
            Assert.Empty(empty.Boundaries);
            Assert.Equal(0, empty.GapCount);

            var single = SegmentTilingHarness.ComputeTiling(
                new List<OrbitSegment> { new OrbitSegment { startUT = 0, endUT = 1 } });
            Assert.Empty(single.Boundaries);
        }

        [Fact]
        public void TilingHarness_DunaOneFixture_HasOnlySmallRecordedSamplingGaps_NoOverlaps()
        {
            // The RAW recorded Duna One topology carries the small inter-segment sampling gaps the recorder
            // leaves at recording-mode / segment transitions (e.g. seg#9->#10 is a 50s gap). It is NOT
            // contiguous by construction - that is the recorded reality the fixture faithfully reproduces.
            // What the harness asserts here is that those are SMALL forward gaps (sub-100s sampling
            // artifacts, never large dead spans) and there are NO overlaps. The zero-gap contiguity is a
            // property the chain-synthesis ASSEMBLER must produce inside its synth span (guarantee 7),
            // proven separately on a contiguous list (TilingHarness_ContiguousList_ReportsZeroGapsAndOverlaps);
            // the raw recording is allowed its sampling gaps.
            var member = ReaimChainSynthesisFixtures.BuildDunaOneTransferMember();
            var report = SegmentTilingHarness.ComputeTiling(member);

            Assert.Equal(0, report.OverlapCount); // no segment claims the same UT as another
            Assert.True(report.GapCount > 0, "the raw recording has the recorder's sampling gaps");
            Assert.True(report.MaxGapSeconds < 100.0,
                $"recorded sampling gaps are small (max was {report.MaxGapSeconds}s)");
        }

        // =====================================================================================
        // P1 - feature flag
        // =====================================================================================

        [Fact]
        public void Flag_DefaultsOff_WhenNoOverrideAndNoSettings()
        {
            ReaimChainSynthesis.ForceEnabledForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null; // Current resolves null in xUnit (no game)
            Assert.False(ReaimChainSynthesis.IsEnabled);
        }

        [Fact]
        public void Flag_SettingDefault_IsFalse()
        {
            // The backing CustomParameterUI bool defaults false (the byte-identical-baseline default).
            var settings = new ParsekSettings();
            Assert.False(settings.reaimChainSynthesis);
        }

        [Fact]
        public void Flag_FollowsSettingWhenNoOverride()
        {
            ReaimChainSynthesis.ForceEnabledForTesting = null;

            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { reaimChainSynthesis = false };
            Assert.False(ReaimChainSynthesis.IsEnabled);

            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { reaimChainSynthesis = true };
            Assert.True(ReaimChainSynthesis.IsEnabled);
        }

        [Fact]
        public void Flag_OverrideWinsOverSetting()
        {
            // Override forces a value regardless of the setting (the A/B test seam).
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { reaimChainSynthesis = false };
            ReaimChainSynthesis.ForceEnabledForTesting = true;
            Assert.True(ReaimChainSynthesis.IsEnabled);

            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { reaimChainSynthesis = true };
            ReaimChainSynthesis.ForceEnabledForTesting = false;
            Assert.False(ReaimChainSynthesis.IsEnabled);
        }

        [Fact]
        public void Flag_Reset_ClearsOverride()
        {
            ReaimChainSynthesis.ForceEnabledForTesting = true;
            ReaimChainSynthesis.Reset();
            Assert.Null(ReaimChainSynthesis.ForceEnabledForTesting);
        }

        // =====================================================================================
        // P1 - persistence round-trip (mirrors the mapRenderTracing / ledgerTracing persistence tests)
        // =====================================================================================

        [Fact]
        public void Persistence_DefaultsNull()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredReaimChainSynthesis());
        }

        [Fact]
        public void Persistence_SetRoundTrips()
        {
            ParsekSettingsPersistence.SetStoredReaimChainSynthesisForTesting(true);
            Assert.True(ParsekSettingsPersistence.GetStoredReaimChainSynthesis().Value);
        }

        [Fact]
        public void Persistence_RecordUpdatesInMemoryStore()
        {
            ParsekSettingsPersistence.RecordReaimChainSynthesis(true);
            Assert.True(ParsekSettingsPersistence.GetStoredReaimChainSynthesis().Value);
        }

        [Fact]
        public void Persistence_ResetForTesting_Clears()
        {
            ParsekSettingsPersistence.SetStoredReaimChainSynthesisForTesting(true);
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredReaimChainSynthesis());
        }

        [Fact]
        public void Persistence_ApplyTo_RestoresStoredValue()
        {
            ParsekSettingsPersistence.SetStoredReaimChainSynthesisForTesting(true);
            var settings = new ParsekSettings { reaimChainSynthesis = false };

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.True(settings.reaimChainSynthesis);
        }

        // =====================================================================================
        // P1 - FLAG-OFF IDENTITY proof at the pure seam (the load-bearing regression guarantee)
        // =====================================================================================

        // Three representative windows: the existing 3-leg idealization, the real Ike-threaded Duna One
        // topology, and a mid-course-correction (two Sun coasts) shape. Each runs AssembleWindowChain with
        // the flag OFF and compares per-field to ReplaceHeliocentricLeg's direct output.
        public static IEnumerable<object[]> IdentityFixtures()
        {
            // (1) the 3-leg idealization the legacy ReaimSegmentAssemblerTests use.
            yield return new object[]
            {
                "three-leg-idealization",
                new List<OrbitSegment>
                {
                    new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 600, epoch = 300, semiMajorAxis = 1e7, eccentricity = 0.1, inclination = 5 },
                    new OrbitSegment { bodyName = "Sun", startUT = 600, endUT = 2600, epoch = 1000, semiMajorAxis = 1e9, eccentricity = 0.1, inclination = 5 },
                    new OrbitSegment { bodyName = "Duna", startUT = 2600, endUT = 5000, epoch = 3000, semiMajorAxis = 1e7, eccentricity = 0.1, inclination = 5 },
                },
                new OrbitSegment { bodyName = "Sun", semiMajorAxis = 2e10, eccentricity = 0.2, inclination = 5 },
                "Sun", 600.0, 2600.0,
            };
            // (2) the real Ike-threaded Duna One topology (P0 fixture).
            yield return new object[]
            {
                "duna-one-real-topology",
                ReaimChainSynthesisFixtures.BuildDunaOneTransferMember(),
                ReaimChainSynthesisFixtures.BuildReaimedTransferSegment(),
                ReaimChainSynthesisFixtures.CommonAncestor,
                ReaimChainSynthesisFixtures.RecordedDepartureUT,
                ReaimChainSynthesisFixtures.RecordedArrivalUT,
            };
            // (3) a mid-course-correction shape (two Sun coasts collapse into one arc).
            yield return new object[]
            {
                "mid-course-correction",
                new List<OrbitSegment>
                {
                    new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 600, epoch = 300, semiMajorAxis = 1e7, eccentricity = 0.1, inclination = 5 },
                    new OrbitSegment { bodyName = "Sun", startUT = 600, endUT = 1500, epoch = 800, semiMajorAxis = 1e9, eccentricity = 0.1, inclination = 5 },
                    new OrbitSegment { bodyName = "Sun", startUT = 1600, endUT = 2600, epoch = 2000, semiMajorAxis = 1.1e9, eccentricity = 0.1, inclination = 5 },
                    new OrbitSegment { bodyName = "Duna", startUT = 2600, endUT = 5000, epoch = 3000, semiMajorAxis = 1e7, eccentricity = 0.1, inclination = 5 },
                },
                new OrbitSegment { bodyName = "Sun", semiMajorAxis = 2e10, eccentricity = 0.2, inclination = 5 },
                "Sun", 600.0, 2600.0,
            };
        }

        [Theory]
        [MemberData(nameof(IdentityFixtures))]
        public void AssembleWindowChain_FlagOff_IsValueEqualToReplaceHeliocentricLeg(
            string label,
            List<OrbitSegment> member,
            OrbitSegment transfer,
            string commonAncestor,
            double recordedDepartureUT,
            double recordedArrivalUT)
        {
            _ = label; // identifies the fixture in test output on failure

            var baseline = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, commonAncestor, recordedDepartureUT, recordedArrivalUT,
                double.NaN, double.NaN);

            var flagOff = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: false,
                member, transfer, commonAncestor, recordedDepartureUT, recordedArrivalUT,
                double.NaN, double.NaN);

            // Byte-identical (per-field value-equal) to today's baseline.
            AssertListValueEqual(baseline, flagOff);
        }

        [Theory]
        [MemberData(nameof(IdentityFixtures))]
        public void AssembleWindowChain_FlagOn_NoChainInputs_FailsClosedToFlagOff(
            string label,
            List<OrbitSegment> member,
            OrbitSegment transfer,
            string commonAncestor,
            double recordedDepartureUT,
            double recordedArrivalUT)
        {
            _ = label;

            // P3 fail-closed contract at the seam: when AssembleWindowChain is called with NO escape /
            // capture legs and NO chain spans (the legacy call shape, escapeSeg/captureSeg null + NaN
            // spans), ReplaceTransferChain synthesizes ONLY the transfer leg (the escape/capture sides
            // both fail their span guards) and so produces the SAME single-leg replacement as flag OFF.
            // This proves the ON path is never worse than the baseline when the chain inputs are absent.
            var flagOff = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: false,
                member, transfer, commonAncestor, recordedDepartureUT, recordedArrivalUT,
                double.NaN, double.NaN);

            var flagOn = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: true,
                member, transfer, commonAncestor, recordedDepartureUT, recordedArrivalUT,
                double.NaN, double.NaN);

            AssertListValueEqual(flagOff, flagOn);
        }

        // Non-throwing per-field list comparison (the bool form of AssertListValueEqual), used by the
        // protective guard below.
        private static bool SegmentListsValueEqual(List<OrbitSegment> a, List<OrbitSegment> b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                OrbitSegment x = a[i], y = b[i];
                if (x.bodyName != y.bodyName || x.startUT != y.startUT || x.endUT != y.endUT
                    || x.inclination != y.inclination || x.eccentricity != y.eccentricity
                    || x.semiMajorAxis != y.semiMajorAxis
                    || x.longitudeOfAscendingNode != y.longitudeOfAscendingNode
                    || x.argumentOfPeriapsis != y.argumentOfPeriapsis
                    || x.meanAnomalyAtEpoch != y.meanAnomalyAtEpoch || x.epoch != y.epoch
                    || x.isPredicted != y.isPredicted)
                    return false;
            }
            return true;
        }

        // Regression guard for the #1167 merge: the live-frame park re-phase (parkDeltaLonDeg) must thread
        // through the AssembleWindowChain seam UNCHANGED on both flag states. Uses a heliocentric-parking
        // departure (a pre-departure Sun coast that ReplaceHeliocentricLeg rotates) so a dropped param is
        // observable - the rotated output differs from the parkDeltaLonDeg=0 output.
        [Fact]
        public void AssembleWindowChain_ForwardsParkDeltaLonDeg_OnBothFlagStates()
        {
            var member = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 600, epoch = 300, semiMajorAxis = 1e7, eccentricity = 0.1, inclination = 5 },
                // pre-departure heliocentric park coast (endUT == recordedDepartureUT -> re-phased):
                new OrbitSegment { bodyName = "Sun", startUT = 600, endUT = 1000, epoch = 700, semiMajorAxis = 8e9, eccentricity = 0.05, inclination = 5, longitudeOfAscendingNode = 30 },
                // in-window heliocentric transfer leg (replaced by the transfer segment):
                new OrbitSegment { bodyName = "Sun", startUT = 1000, endUT = 2600, epoch = 1200, semiMajorAxis = 1e9, eccentricity = 0.1, inclination = 5 },
                new OrbitSegment { bodyName = "Duna", startUT = 2600, endUT = 5000, epoch = 3000, semiMajorAxis = 1e7, eccentricity = 0.1, inclination = 5 },
            };
            var transfer = new OrbitSegment { bodyName = "Sun", semiMajorAxis = 2e10, eccentricity = 0.2, inclination = 5 };
            const string commonAncestor = "Sun";
            const double depUT = 1000.0, arrUT = 2600.0, parkDeltaLonDeg = 17.0;

            var baselineRephased = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, commonAncestor, depUT, arrUT, double.NaN, double.NaN, parkDeltaLonDeg);
            var baselineNoRephase = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, commonAncestor, depUT, arrUT, double.NaN, double.NaN, 0.0);

            // The chosen parkDeltaLonDeg must actually change the output, else the forwarding assertions
            // below would pass even if AssembleWindowChain dropped the parameter.
            Assert.False(SegmentListsValueEqual(baselineRephased, baselineNoRephase),
                "parkDeltaLonDeg=17 must rotate the pre-departure Sun park coast (otherwise this guard is not protective)");

            var flagOff = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: false, member, transfer, commonAncestor, depUT, arrUT, double.NaN, double.NaN, parkDeltaLonDeg);
            var flagOn = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: true, member, transfer, commonAncestor, depUT, arrUT, double.NaN, double.NaN, parkDeltaLonDeg);

            // Both flag states reproduce the re-phased baseline exactly (parkDeltaLonDeg threaded through).
            AssertListValueEqual(baselineRephased, flagOff);
            AssertListValueEqual(baselineRephased, flagOn);
        }

        [Fact]
        public void AssembleWindowChain_FlagOff_NoHeliocentricLeg_ReturnsNullLikeBaseline()
        {
            // A member with no Sun leg in the window (a launch-only / arrival-only chained member) returns
            // null on both paths -> the member stays faithful. This preserves the declined/faithful
            // contract through the flag seam.
            var launchOnly = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 600, epoch = 300 },
            };
            var transfer = new OrbitSegment { bodyName = "Sun" };

            Assert.Null(ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                launchOnly, transfer, "Sun", 600.0, 2600.0, double.NaN, double.NaN));
            Assert.Null(ReaimSegmentAssembler.AssembleWindowChain(
                useChain: false, launchOnly, transfer, "Sun", 600.0, 2600.0, double.NaN, double.NaN));
            Assert.Null(ReaimSegmentAssembler.AssembleWindowChain(
                useChain: true, launchOnly, transfer, "Sun", 600.0, 2600.0, double.NaN, double.NaN));
        }

        // =====================================================================================
        // P2 - STEP 4 uniform-shift phase-consistency proof
        // =====================================================================================

        // The three synthesized legs (escape, transfer, capture) have natural epochs set at THREE different
        // absolute UTs (launchSoiExitUT, departureUT, soiEntryUT). STEP 4 applies ONE uniform shift to all
        // three; this preserves relative phase ONLY because each leg's epoch was set in the SAME absolute
        // frame (all three are absolute live-frame UTs of the same window solve). The pure proxy for "each
        // leg's sampled position at its shared seam UT is unchanged relative to its neighbor": ShiftInTime
        // moves startUT/endUT/epoch by the SAME delta, so (t - epoch) - the mean-anomaly clock argument - is
        // invariant at every UT, AND a shared seam UT (escape.endUT == transfer.startUT) stays shared after
        // the shift. A non-uniform shift (different deltas per leg) would break BOTH, which this guards.
        [Fact]
        public void UniformShift_PreservesPerLegPhaseAndSeamContiguity()
        {
            // Three legs with epochs at three DIFFERENT absolute UTs (the escape/transfer/capture pattern),
            // tiled contiguously: escape [1000,2000] epoch 1000, transfer [2000,9000] epoch 2000,
            // capture [9000,9500] epoch 9000.
            var escape = new OrbitSegment { bodyName = "Kerbin", startUT = 1000, endUT = 2000, epoch = 1000, meanAnomalyAtEpoch = 0.3, semiMajorAxis = -3.8e6, eccentricity = 1.19 };
            var transfer = new OrbitSegment { bodyName = "Sun", startUT = 2000, endUT = 9000, epoch = 2000, meanAnomalyAtEpoch = 0.5, semiMajorAxis = 1.76e10, eccentricity = 0.21 };
            var capture = new OrbitSegment { bodyName = "Duna", startUT = 9000, endUT = 9500, epoch = 9000, meanAnomalyAtEpoch = 0.7, semiMajorAxis = -5.6e5, eccentricity = 1.05 };

            const double shift = -54321.0; // an arbitrary recorded-span shift (RecordedDepartureUT - departureUT)

            // Per-leg phase clock argument (t - epoch) at each leg's OWN seam UTs, BEFORE the shift.
            double escEndPhaseBefore = escape.endUT - escape.epoch;       // transfer-start seam, escape side
            double xferStartPhaseBefore = transfer.startUT - transfer.epoch; // transfer-start seam, transfer side
            double xferEndPhaseBefore = transfer.endUT - transfer.epoch;  // capture-start seam, transfer side
            double capStartPhaseBefore = capture.startUT - capture.epoch; // capture-start seam, capture side

            var escS = ReaimSegmentAssembler.ShiftInTime(escape, shift);
            var xferS = ReaimSegmentAssembler.ShiftInTime(transfer, shift);
            var capS = ReaimSegmentAssembler.ShiftInTime(capture, shift);

            // 1) Each leg's phase clock argument (t - epoch) is UNCHANGED at its seams (the mean anomaly,
            //    and so the sampled position, at the shifted seam UT equals that at the original seam UT).
            Assert.Equal(escEndPhaseBefore, escS.endUT - escS.epoch, 6);
            Assert.Equal(xferStartPhaseBefore, xferS.startUT - xferS.epoch, 6);
            Assert.Equal(xferEndPhaseBefore, xferS.endUT - xferS.epoch, 6);
            Assert.Equal(capStartPhaseBefore, capS.startUT - capS.epoch, 6);

            // 2) The shared seams stay contiguous (escape.endUT == transfer.startUT, transfer.endUT ==
            //    capture.startUT) after the uniform shift - the neighbors still meet at the SAME UT.
            Assert.Equal(escS.endUT, xferS.startUT, 6);
            Assert.Equal(xferS.endUT, capS.startUT, 6);

            // 3) The mean-anomaly-at-epoch (the orbit's phase identity) is untouched by the shift.
            Assert.Equal(escape.meanAnomalyAtEpoch, escS.meanAnomalyAtEpoch, 9);
            Assert.Equal(transfer.meanAnomalyAtEpoch, xferS.meanAnomalyAtEpoch, 9);
            Assert.Equal(capture.meanAnomalyAtEpoch, capS.meanAnomalyAtEpoch, 9);
        }

        [Fact]
        public void UniformShift_NonUniformShiftWouldBreakSeam_Contrast()
        {
            // Contrast proof: applying DIFFERENT shifts to adjacent legs breaks the shared seam, which is
            // exactly why STEP 4 mandates ONE uniform shift. (Documents the failure mode the uniform shift
            // avoids; not a behavior under test, a negative control.)
            var escape = new OrbitSegment { bodyName = "Kerbin", startUT = 1000, endUT = 2000, epoch = 1000 };
            var transfer = new OrbitSegment { bodyName = "Sun", startUT = 2000, endUT = 9000, epoch = 2000 };

            var escS = ReaimSegmentAssembler.ShiftInTime(escape, -1000.0);
            var xferS = ReaimSegmentAssembler.ShiftInTime(transfer, -2000.0); // a DIFFERENT shift

            Assert.NotEqual(escS.endUT, xferS.startUT); // the seam opened a gap -> the chain would discontinuous
        }

        // =====================================================================================
        // P3 - ReplaceTransferChain (the whole-chain assembly): pinned, contiguous UTs +
        //      capture/escape fail-closed + all-or-nothing fallback. (reaim-fix-plan.md STEP 3/4/5.)
        // =====================================================================================

        // Distinct synth-leg shapes so a test can tell the synth leg from the recorded one it replaced.
        // The escape leg is launch-body (Kerbin) relative, the capture leg target-body (Duna) relative.
        // startUT/endUT/epoch are arbitrary here - the assembler re-stamps them to the recorded-span runs.
        private const double SynthEscapeSma = -4242424.0;   // distinct from the recorded escape -3818300
        private const double SynthTransferSma = 18200000000.0; // distinct from the recorded coasts 1.76e10
        private const double SynthCaptureSma = -777777.0;   // distinct from the recorded capture -563351

        private static OrbitSegment SynthEscape() => new OrbitSegment
        {
            bodyName = "Kerbin", semiMajorAxis = SynthEscapeSma, eccentricity = 1.3,
            inclination = 6, longitudeOfAscendingNode = 31, argumentOfPeriapsis = 46,
            meanAnomalyAtEpoch = 0.51, epoch = 64044033.0, isPredicted = false,
        };
        private static OrbitSegment SynthTransfer() => new OrbitSegment
        {
            bodyName = "Sun", semiMajorAxis = SynthTransferSma, eccentricity = 0.23,
            inclination = 6, longitudeOfAscendingNode = 31, argumentOfPeriapsis = 46,
            meanAnomalyAtEpoch = 0.51, epoch = 64044033.0, isPredicted = false,
        };
        private static OrbitSegment SynthCapture() => new OrbitSegment
        {
            bodyName = "Duna", semiMajorAxis = SynthCaptureSma, eccentricity = 1.1,
            inclination = 6, longitudeOfAscendingNode = 31, argumentOfPeriapsis = 46,
            meanAnomalyAtEpoch = 0.51, epoch = 70898646.0, isPredicted = false,
        };

        // A clean cross-parent transfer member with a SYNTHESIZABLE capture (no Ike thread, no descent):
        // circular parking + escape hyperbola + heliocentric coast + a SINGLE Duna capture hyperbola.
        // Contiguous so the assembler's synth span tiles with zero gaps.
        private static List<OrbitSegment> CleanCrossParentMember() => new List<OrbitSegment>
        {
            new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 600, epoch = 300, semiMajorAxis = 700000, eccentricity = 0.0, inclination = 5, longitudeOfAscendingNode = 30, argumentOfPeriapsis = 45, meanAnomalyAtEpoch = 0.5 },
            new OrbitSegment { bodyName = "Kerbin", startUT = 600, endUT = 1000, epoch = 700, semiMajorAxis = -3818300, eccentricity = 1.19, inclination = 5, longitudeOfAscendingNode = 30, argumentOfPeriapsis = 45, meanAnomalyAtEpoch = 0.5 },
            new OrbitSegment { bodyName = "Sun", startUT = 1000, endUT = 5000, epoch = 1200, semiMajorAxis = 1.76e10, eccentricity = 0.21, inclination = 5, longitudeOfAscendingNode = 30, argumentOfPeriapsis = 45, meanAnomalyAtEpoch = 0.5 },
            new OrbitSegment { bodyName = "Duna", startUT = 5000, endUT = 5600, epoch = 5100, semiMajorAxis = -563351, eccentricity = 1.05, inclination = 5, longitudeOfAscendingNode = 30, argumentOfPeriapsis = 45, meanAnomalyAtEpoch = 0.5 },
            new OrbitSegment { bodyName = "Duna", startUT = 5600, endUT = 6000, epoch = 5700, semiMajorAxis = 400000, eccentricity = 0.1, inclination = 5, longitudeOfAscendingNode = 30, argumentOfPeriapsis = 45, meanAnomalyAtEpoch = 0.5 }, // capture parking
        };

        // The clean member's run boundaries (recorded-span UTs).
        private const double CleanEscapeStartUT = 600.0;
        private const double CleanEscapeEndUT = 1000.0;     // == transfer-run start (RecordedDepartureUT)
        private const double CleanDepartureUT = 1000.0;
        private const double CleanArrivalUT = 5000.0;       // == first-capture start
        private const double CleanFirstCaptureStartUT = 5000.0;
        private const double CleanFirstCaptureEndUT = 5600.0;

        private static List<OrbitSegment> AssembleClean(
            OrbitSegment? escape, OrbitSegment? capture, bool captureSynthesizable,
            bool escapeRunIsParkingOnly = false, double parkDeltaLonDeg = 0.0)
        {
            return ReaimSegmentAssembler.ReplaceTransferChain(
                CleanCrossParentMember(),
                escape, SynthTransfer(), capture,
                "Sun", "Kerbin", "Duna",
                CleanDepartureUT, CleanArrivalUT,
                CleanEscapeStartUT, CleanEscapeEndUT,
                CleanFirstCaptureStartUT, CleanFirstCaptureEndUT,
                escapeRunIsParkingOnly, captureSynthesizable, parkDeltaLonDeg);
        }

        [Fact]
        public void ReplaceTransferChain_CleanArrival_SplicesAllThreeLegs_ContiguousTiling()
        {
            var segs = AssembleClean(SynthEscape(), SynthCapture(), captureSynthesizable: true);
            Assert.NotNull(segs);

            // Shape: circular parking + synth escape + synth transfer + synth capture + capture parking = 5.
            Assert.Equal(5, segs.Count);

            // [0] circular parking (verbatim, body-relative).
            Assert.Equal("Kerbin", segs[0].bodyName);
            Assert.Equal(100.0, segs[0].startUT, 6);
            Assert.Equal(600.0, segs[0].endUT, 6);
            Assert.Equal(700000.0, segs[0].semiMajorAxis, 0);

            // [1] synth escape stamped to the escape-run span [600,1000].
            Assert.Equal("Kerbin", segs[1].bodyName);
            Assert.Equal(CleanEscapeStartUT, segs[1].startUT, 6);
            Assert.Equal(CleanEscapeEndUT, segs[1].endUT, 6);
            Assert.Equal(SynthEscapeSma, segs[1].semiMajorAxis, 0);

            // [2] synth transfer stamped to the full transfer span [1000,5000].
            Assert.Equal("Sun", segs[2].bodyName);
            Assert.Equal(CleanDepartureUT, segs[2].startUT, 6);
            Assert.Equal(CleanArrivalUT, segs[2].endUT, 6);
            Assert.Equal(SynthTransferSma, segs[2].semiMajorAxis, 0);

            // [3] synth capture pinned at the SOI-entry boundary [5000, firstCaptureEnd].
            Assert.Equal("Duna", segs[3].bodyName);
            Assert.Equal(CleanArrivalUT, segs[3].startUT, 6);
            Assert.Equal(CleanFirstCaptureEndUT, segs[3].endUT, 6);
            Assert.Equal(SynthCaptureSma, segs[3].semiMajorAxis, 0);

            // [4] capture parking (verbatim, body-relative).
            Assert.Equal("Duna", segs[4].bodyName);
            Assert.Equal(5600.0, segs[4].startUT, 6);
            Assert.Equal(6000.0, segs[4].endUT, 6);
            Assert.Equal(400000.0, segs[4].semiMajorAxis, 0);

            // Zero-gap tiling at ALL boundaries (guarantee 7): no UT gap and no overlap inside the synth span.
            var report = SegmentTilingHarness.ComputeTiling(segs);
            Assert.Equal(0, report.GapCount);
            Assert.Equal(0, report.OverlapCount);
            Assert.Equal(0.0, report.MaxAbsDiscontinuitySeconds, 6);
        }

        [Fact]
        public void ReplaceTransferChain_CapturePin_StartsAtCompressedClockArrivalBoundary()
        {
            // STEP 4: the synth capture's SOI-entry is pinned to recordedArrivalUT (== the
            // heliocentric->capture boundary the loop clock feeds to CompressSpanUT), NOT to the synth
            // capture's own geometric SOI UT or its raw startUT.
            var segs = AssembleClean(SynthEscape(), SynthCapture(), captureSynthesizable: true);
            OrbitSegment capture = segs.Find(s => s.semiMajorAxis == SynthCaptureSma);
            Assert.Equal(CleanArrivalUT, capture.startUT, 6);
            // The transfer ENDS exactly where the capture STARTS (transfer.endUT == capture.startUT).
            OrbitSegment transfer = segs.Find(s => s.semiMajorAxis == SynthTransferSma);
            Assert.Equal(transfer.endUT, capture.startUT, 6);
        }

        [Fact]
        public void ReplaceTransferChain_CaptureFailClosed_KeepsRecordedCaptureVerbatim()
        {
            // CaptureSynthesizable=false (Ike-thread / atmospheric-direct): the escape + transfer synthesize,
            // the recorded capture run stays byte-identical to the baseline arrival (guarantee 8). Pass a
            // (would-be) capture leg to prove it is IGNORED when the flag is false.
            var withCap = AssembleClean(SynthEscape(), SynthCapture(), captureSynthesizable: false);
            Assert.NotNull(withCap);

            // No synth capture present; the recorded Duna capture hyperbola (-563351) is kept verbatim.
            Assert.DoesNotContain(withCap, s => s.semiMajorAxis == SynthCaptureSma);
            OrbitSegment recordedCapture = withCap.Find(s => s.bodyName == "Duna" && s.semiMajorAxis == -563351);
            Assert.Equal(5000.0, recordedCapture.startUT, 6); // recorded span unchanged
            Assert.Equal(5600.0, recordedCapture.endUT, 6);
            Assert.Equal(1.05, recordedCapture.eccentricity, 6);

            // The escape + transfer still synthesized (the escape-side improvement ships).
            Assert.Contains(withCap, s => s.semiMajorAxis == SynthEscapeSma);
            Assert.Contains(withCap, s => s.semiMajorAxis == SynthTransferSma);

            // A null captureSeg with captureSynthesizable=true also keeps the recorded capture verbatim.
            var nullCap = AssembleClean(SynthEscape(), null, captureSynthesizable: true);
            Assert.DoesNotContain(nullCap, s => s.semiMajorAxis == SynthCaptureSma);
            Assert.Contains(nullCap, s => s.bodyName == "Duna" && s.semiMajorAxis == -563351);
        }

        [Fact]
        public void ReplaceTransferChain_IkeThreadedDunaOne_FailsClosedOnCapture_EscapeAndTransferSynth()
        {
            // The REAL Duna One topology threads Ike (CaptureSynthesizable=false): the capture side fails
            // closed (recorded Duna/Ike/descent verbatim) and only the escape + transfer synthesize.
            var member = ReaimChainSynthesisFixtures.BuildDunaOneTransferMember();
            int recordedCount = member.Count;

            var segs = ReaimSegmentAssembler.ReplaceTransferChain(
                member,
                SynthEscape(), SynthTransfer(), SynthCapture(),
                "Sun", "Kerbin", "Duna",
                ReaimChainSynthesisFixtures.RecordedDepartureUT, ReaimChainSynthesisFixtures.RecordedArrivalUT,
                escapeRunStartUT: 63966986.0, escapeRunEndUT: 64044033.0,        // seg#8 escape span
                firstCaptureStartUT: 70898646.0, firstCaptureEndUT: 70912684.0,  // seg#12 first capture
                escapeRunIsParkingOnly: false, captureSynthesizable: false);
            Assert.NotNull(segs);

            // The synth capture is NOT present (capture fail-closed); the recorded captures + Ike + descent
            // are all kept verbatim.
            Assert.DoesNotContain(segs, s => s.semiMajorAxis == SynthCaptureSma);
            Assert.Contains(segs, s => s.bodyName == "Ike");
            // The first recorded Duna capture (-563351) survives untouched.
            Assert.Contains(segs, s => s.bodyName == "Duna" && s.semiMajorAxis == -563351 && s.startUT == 70898646.0);

            // The escape (seg#8) + the 3 Sun coasts collapse into the synth escape + synth transfer.
            Assert.Contains(segs, s => s.semiMajorAxis == SynthEscapeSma);
            Assert.Contains(segs, s => s.semiMajorAxis == SynthTransferSma);
            Assert.Single(segs, s => s.bodyName == "Sun"); // 3 recorded coasts -> 1 synth transfer

            // The circular parking orbit (700000) is kept verbatim (selected by index, never synthesized).
            Assert.Contains(segs, s => s.bodyName == "Kerbin" && s.semiMajorAxis == 700000.0);

            // No overlaps introduced; the synth escape/transfer span tiles into the recorded capture run.
            var report = SegmentTilingHarness.ComputeTiling(segs);
            Assert.Equal(0, report.OverlapCount);

            // The recorded count drops only by the legs collapsed (escape seg#8 + 3 Sun coasts -> 2 synth);
            // everything from the first capture onward is preserved.
            Assert.True(segs.Count < recordedCount);
        }

        [Fact]
        public void ReplaceTransferChain_EscapeParkingOnly_KeepsEscapeRunVerbatim_TransferSynth()
        {
            // EscapeRunIsParkingOnly (a direct SOI exit, no separate escape hyperbola): the escape side is
            // NOT synthesized. The launch-body run stays verbatim; only the transfer (and capture) splice.
            // Use a member whose launch-body predecessor of the heliocentric leg IS the circular parking.
            var member = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 1000, epoch = 300, semiMajorAxis = 700000, eccentricity = 0.0, inclination = 5 },
                new OrbitSegment { bodyName = "Sun", startUT = 1000, endUT = 5000, epoch = 1200, semiMajorAxis = 1.76e10, eccentricity = 0.21, inclination = 5 },
                new OrbitSegment { bodyName = "Duna", startUT = 5000, endUT = 5600, epoch = 5100, semiMajorAxis = -563351, eccentricity = 1.05, inclination = 5 },
            };

            var segs = ReaimSegmentAssembler.ReplaceTransferChain(
                member,
                SynthEscape(), SynthTransfer(), SynthCapture(),
                "Sun", "Kerbin", "Duna",
                1000.0, 5000.0,
                escapeRunStartUT: 100.0, escapeRunEndUT: 1000.0,
                firstCaptureStartUT: 5000.0, firstCaptureEndUT: 5600.0,
                escapeRunIsParkingOnly: true, captureSynthesizable: true);
            Assert.NotNull(segs);

            // No synth escape (escape parking-only); the recorded Kerbin parking (700000) is kept verbatim.
            Assert.DoesNotContain(segs, s => s.semiMajorAxis == SynthEscapeSma);
            Assert.Contains(segs, s => s.bodyName == "Kerbin" && s.semiMajorAxis == 700000.0 && s.startUT == 100.0);

            // The transfer + capture synthesized.
            Assert.Contains(segs, s => s.semiMajorAxis == SynthTransferSma);
            Assert.Contains(segs, s => s.semiMajorAxis == SynthCaptureSma);
        }

        [Fact]
        public void ReplaceTransferChain_EscapeNotColocatedWithTransfer_KeepsEscapeVerbatim()
        {
            // STEP 3 co-location gate: when the escape run does NOT end at the transfer-run start (a recorded
            // heliocentric park sits between them, so the escape-run end is hours/days before the burn), the
            // escape leg is kept verbatim (synth capture-side only) rather than placing the synth escape's
            // end far from the SOI edge.
            var member = CleanCrossParentMember();
            // escapeRunEndUT (1000) deliberately far from the burn (recordedDepartureUT 50000): not co-located.
            var segs = ReaimSegmentAssembler.ReplaceTransferChain(
                member,
                SynthEscape(), SynthTransfer(), SynthCapture(),
                "Sun", "Kerbin", "Duna",
                recordedDepartureUT: 1000.0, recordedArrivalUT: 5000.0,
                escapeRunStartUT: 600.0, escapeRunEndUT: 1000.0 - (ReaimSegmentAssembler.EscapeHandoffToleranceSeconds + 1.0),
                firstCaptureStartUT: 5000.0, firstCaptureEndUT: 5600.0,
                escapeRunIsParkingOnly: false, captureSynthesizable: true);
            Assert.NotNull(segs);

            // The escape run was NOT synthesized (the gate rejected the non-co-located handoff): the recorded
            // escape hyperbola (-3818300) survives.
            Assert.DoesNotContain(segs, s => s.semiMajorAxis == SynthEscapeSma);
            Assert.Contains(segs, s => s.bodyName == "Kerbin" && s.semiMajorAxis == -3818300);
            // The transfer still synthesized (escape verbatim, transfer synth).
            Assert.Contains(segs, s => s.semiMajorAxis == SynthTransferSma);
        }

        [Fact]
        public void ReplaceTransferChain_UntouchedSegments_AreByValueIdenticalToRecorded()
        {
            // The verbatim pass-through segments (circular parking, capture parking, Ike, descent) must be
            // value-identical to the recorded input (guarantee 4): the chain reads the adjacent recorded
            // segments but writes them only into the returned copy, never mutating the recording.
            var member = CleanCrossParentMember();
            var segs = AssembleClean(SynthEscape(), null, captureSynthesizable: false); // capture verbatim too

            // Circular parking [0] unchanged.
            OrbitSegment parkOut = segs.Find(s => s.bodyName == "Kerbin" && s.semiMajorAxis == 700000.0);
            AssertSegmentValueEqual(member[0], parkOut);
            // Capture hyperbola (recorded, kept verbatim) unchanged.
            OrbitSegment capOut = segs.Find(s => s.bodyName == "Duna" && s.semiMajorAxis == -563351);
            AssertSegmentValueEqual(member[3], capOut);
            // Capture parking unchanged.
            OrbitSegment capParkOut = segs.Find(s => s.bodyName == "Duna" && s.semiMajorAxis == 400000.0);
            AssertSegmentValueEqual(member[4], capParkOut);
        }

        [Fact]
        public void ReplaceTransferChain_NoHeliocentricLeg_ReturnsNull_AllOrNothing()
        {
            // A member with no Sun leg in the window (a launch-only / arrival-only chained member): the
            // transfer cannot be replaced, so the WHOLE chain returns null (all-or-nothing) -> the caller
            // falls back to today's heliocentric-only baseline (which also returns null -> faithful).
            var launchOnly = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 1000, epoch = 300, semiMajorAxis = 700000, eccentricity = 0.0 },
            };
            var result = ReaimSegmentAssembler.ReplaceTransferChain(
                launchOnly,
                SynthEscape(), SynthTransfer(), SynthCapture(),
                "Sun", "Kerbin", "Duna",
                1000.0, 5000.0, 600.0, 1000.0, 5000.0, 5600.0,
                escapeRunIsParkingOnly: false, captureSynthesizable: true);
            Assert.Null(result);
        }

        [Fact]
        public void ReplaceTransferChain_NaNOrMisorderedWindow_ReturnsNull()
        {
            // Fail-closed on degenerate window bounds.
            Assert.Null(ReaimSegmentAssembler.ReplaceTransferChain(
                CleanCrossParentMember(), SynthEscape(), SynthTransfer(), SynthCapture(),
                "Sun", "Kerbin", "Duna",
                double.NaN, 5000.0, 600.0, 1000.0, 5000.0, 5600.0, false, true));
            Assert.Null(ReaimSegmentAssembler.ReplaceTransferChain(
                CleanCrossParentMember(), SynthEscape(), SynthTransfer(), SynthCapture(),
                "Sun", "Kerbin", "Duna",
                5000.0, 1000.0, 600.0, 1000.0, 5000.0, 5600.0, false, true)); // departure > arrival
        }

        [Fact]
        public void AssembleWindowChain_FlagOn_ChainSynthesisFails_FallsBackToReplaceHeliocentricLeg()
        {
            // All-or-nothing fallback through the flag seam: flag ON with a member that has NO heliocentric
            // leg (ReplaceTransferChain returns null) falls back to ReplaceHeliocentricLeg's result (also
            // null here -> faithful). The fallback path must not throw / return garbage.
            var launchOnly = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 1000, epoch = 300, semiMajorAxis = 700000 },
            };
            var baseline = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                launchOnly, SynthTransfer(), "Sun", 1000.0, 5000.0, double.NaN, double.NaN);
            var flagOn = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: true, launchOnly, SynthTransfer(), "Sun", 1000.0, 5000.0,
                double.NaN, double.NaN, 0.0,
                SynthEscape(), SynthCapture(), "Kerbin", "Duna",
                600.0, 1000.0, 5000.0, 5600.0, false, true);
            Assert.Null(baseline);
            Assert.Null(flagOn);
        }

        [Fact]
        public void AssembleWindowChain_FlagOn_FullChainInputs_ProducesChain_DistinctFromFlagOff()
        {
            // Flag ON with FULL chain inputs on the clean cross-parent member produces the spliced chain
            // (escape + transfer + capture), which DIFFERS from flag OFF (the single-leg heliocentric
            // replacement, which leaves the recorded escape + capture verbatim). Proves the flag actually
            // engages the chain synthesis when inputs are present (guarantee 13's pure analogue).
            var member = CleanCrossParentMember();
            var flagOff = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: false, member, SynthTransfer(), "Sun", CleanDepartureUT, CleanArrivalUT,
                double.NaN, double.NaN);
            var flagOn = ReaimSegmentAssembler.AssembleWindowChain(
                useChain: true, member, SynthTransfer(), "Sun", CleanDepartureUT, CleanArrivalUT,
                double.NaN, double.NaN, 0.0,
                SynthEscape(), SynthCapture(), "Kerbin", "Duna",
                CleanEscapeStartUT, CleanEscapeEndUT, CleanFirstCaptureStartUT, CleanFirstCaptureEndUT,
                false, true);

            // Flag OFF keeps the recorded escape (-3818300) + recorded capture (-563351) verbatim.
            Assert.Contains(flagOff, s => s.semiMajorAxis == -3818300);
            Assert.Contains(flagOff, s => s.semiMajorAxis == -563351);
            Assert.DoesNotContain(flagOff, s => s.semiMajorAxis == SynthEscapeSma);

            // Flag ON replaces them with the synth escape + synth capture.
            Assert.Contains(flagOn, s => s.semiMajorAxis == SynthEscapeSma);
            Assert.Contains(flagOn, s => s.semiMajorAxis == SynthCaptureSma);
            Assert.DoesNotContain(flagOn, s => s.semiMajorAxis == -3818300);
            Assert.DoesNotContain(flagOn, s => s.semiMajorAxis == -563351);

            Assert.False(SegmentListsValueEqual(flagOff, flagOn));
        }

        [Fact]
        public void ReplaceTransferChain_ParkRephase_RotatesOnlyRecordedHeliocentricPark()
        {
            // parkDeltaLonDeg re-phases the recorded heliocentric PARK (a Sun coast ENDING at/before the
            // burn) identically to ReplaceHeliocentricLeg, while the synth transfer + body-relative legs are
            // untouched. Use a member with a pre-burn Sun park.
            var member = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 600, epoch = 300, semiMajorAxis = 700000, eccentricity = 0.0, inclination = 5, longitudeOfAscendingNode = 30 },
                new OrbitSegment { bodyName = "Kerbin", startUT = 600, endUT = 1000, epoch = 700, semiMajorAxis = -3818300, eccentricity = 1.19, inclination = 5, longitudeOfAscendingNode = 30 },
                // pre-burn heliocentric park (endUT == recordedDepartureUT -> re-phased):
                new OrbitSegment { bodyName = "Sun", startUT = 1000, endUT = 1500, epoch = 1100, semiMajorAxis = 1.4e10, eccentricity = 0.05, inclination = 5, longitudeOfAscendingNode = 30 },
                // in-window transfer (replaced):
                new OrbitSegment { bodyName = "Sun", startUT = 1500, endUT = 5000, epoch = 1700, semiMajorAxis = 1.76e10, eccentricity = 0.21, inclination = 5, longitudeOfAscendingNode = 30 },
                new OrbitSegment { bodyName = "Duna", startUT = 5000, endUT = 5600, epoch = 5100, semiMajorAxis = -563351, eccentricity = 1.05, inclination = 5, longitudeOfAscendingNode = 30 },
            };
            const double depUT = 1500.0, arrUT = 5000.0, parkDelta = 90.0;

            // The escape run ends at 1000, far from the burn (1500) -> escape kept verbatim; only the
            // transfer + capture splice. The park [1000,1500] is re-phased.
            var segs = ReaimSegmentAssembler.ReplaceTransferChain(
                member,
                SynthEscape(), SynthTransfer(), SynthCapture(),
                "Sun", "Kerbin", "Duna",
                depUT, arrUT,
                escapeRunStartUT: 600.0, escapeRunEndUT: 1000.0,
                firstCaptureStartUT: 5000.0, firstCaptureEndUT: 5600.0,
                escapeRunIsParkingOnly: false, captureSynthesizable: true,
                parkDeltaLonDeg: parkDelta);
            Assert.NotNull(segs);

            // The recorded park (1.4e10) had its LAN rotated 30 -> 120.
            OrbitSegment park = segs.Find(s => s.semiMajorAxis == 1.4e10);
            Assert.Equal(120.0, park.longitudeOfAscendingNode, 6);
            // The synth transfer is NOT rotated (its LAN stays as built, 31).
            OrbitSegment transfer = segs.Find(s => s.semiMajorAxis == SynthTransferSma);
            Assert.Equal(31.0, transfer.longitudeOfAscendingNode, 6);
        }

        [Fact]
        public void IsFiniteOrderedSpan_GuardsNaNInfAndOrdering()
        {
            Assert.True(ReaimSegmentAssembler.IsFiniteOrderedSpan(100.0, 200.0));
            Assert.False(ReaimSegmentAssembler.IsFiniteOrderedSpan(200.0, 100.0));
            Assert.False(ReaimSegmentAssembler.IsFiniteOrderedSpan(100.0, 100.0));
            Assert.False(ReaimSegmentAssembler.IsFiniteOrderedSpan(double.NaN, 200.0));
            Assert.False(ReaimSegmentAssembler.IsFiniteOrderedSpan(100.0, double.PositiveInfinity));
        }
    }
}
