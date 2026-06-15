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
        public void AssembleWindowChain_FlagOn_IsPlaceholderIdenticalToFlagOff(
            string label,
            List<OrbitSegment> member,
            OrbitSegment transfer,
            string commonAncestor,
            double recordedDepartureUT,
            double recordedArrivalUT)
        {
            _ = label;

            // P1 placeholder: the ON path currently returns the SAME result as the OFF path (no chain
            // synthesis until P3). Flipping the flag changes no behavior yet, by design.
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
    }
}
