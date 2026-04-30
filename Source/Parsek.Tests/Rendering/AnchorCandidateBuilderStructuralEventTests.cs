using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 9 (design doc §12, §15.17, §18 Phase 9) tests for
    /// <see cref="AnchorCandidateBuilder.TryFindFlaggedSampleAtUT"/>. The helper
    /// is the single seam used by the propagator (RenderSessionState) and the
    /// boundary resolver (ProductionAnchorWorldFrameResolver) to prefer a
    /// StructuralEventSnapshot-flagged sample over the legacy nearest-neighbour
    /// at a candidate UT. Tests pin both directions:
    ///
    /// <list type="bullet">
    ///   <item>Flagged sample exact at UT → returned.</item>
    ///   <item>Flagged sample within tolerance → returned.</item>
    ///   <item>No flagged sample → returns -1 (caller falls back to legacy
    ///       interpolation, §15.17).</item>
    ///   <item>Flagged sample outside tolerance → returns -1.</item>
    ///   <item>Unflagged samples never returned regardless of proximity.</item>
    ///   <item>Multiple flagged samples → closest to UT wins.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class AnchorCandidateBuilderStructuralEventTests
    {
        private static TrajectoryPoint MakePoint(double ut, byte flags = 0)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = ut,           // distinct payload per UT so callers
                longitude = ut * 0.5,    // can prove the right index was returned
                altitude = 1000.0 + ut,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
                bodyName = "Kerbin",
                flags = flags,
            };
        }

        // ----- Phase 9 prefer-flagged behaviour -----

        [Fact]
        public void TryFindFlaggedSampleAtUT_ExactMatch_ReturnsFlaggedIndex()
        {
            // Regular tick at UT=9.95 + flagged sample at UT=10.0 + regular tick at UT=10.05.
            // Caller queries UT=10.0 — the flagged one is at exact alignment.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(9.95),
                MakePoint(10.0, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
                MakePoint(10.05),
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 1.0);

            Assert.Equal(1, idx);
            Assert.Equal(10.0, frames[idx].ut);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_FlaggedWithinTolerance_ReturnsFlaggedIndex()
        {
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(9.95),
                MakePoint(10.05, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 0.5);

            Assert.Equal(1, idx);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_NoFlaggedSamples_ReturnsMinusOne()
        {
            // §15.17 backwards-compat: legacy recordings (no flag) miss every
            // flagged-sample lookup; caller falls back to TryFindFirstPointAtOrAfter.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(9.95),
                MakePoint(10.0),
                MakePoint(10.05),
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 1.0);

            Assert.Equal(-1, idx);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_FlaggedOutsideTolerance_ReturnsMinusOne()
        {
            // Flagged sample at UT=20 is outside the 1s tolerance from UT=10.
            // The helper must NOT return it — that would let a far-away
            // structural event hijack an unrelated candidate.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(10.0),
                MakePoint(20.0, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 1.0);

            Assert.Equal(-1, idx);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_MultipleFlaggedSamples_ReturnsClosestToUT()
        {
            // Both flagged samples are within tolerance. The closest wins —
            // which guards against the case where two structural events on
            // the same recording (e.g. dock followed by undock) both lie
            // within the search window.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(9.5, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
                MakePoint(10.2, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 1.0);

            Assert.Equal(1, idx); // 10.2 is closer to 10.0 than 9.5
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_UnflaggedClosestSampleIgnored_FlaggedFartherWins()
        {
            // Critical test: the unflagged sample is exactly at UT=10.0, but
            // a flagged sample is also within tolerance at UT=10.05. The
            // helper must prefer the flagged one — that's the entire point
            // of Phase 9. Without this preference, AnchorCandidateBuilder
            // would still pick the closest sample (today's bug behaviour).
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(10.0),                                                          // unflagged, exact
                MakePoint(10.05, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot), // flagged, within tolerance
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 1.0);

            Assert.Equal(1, idx);
            Assert.Equal(10.05, frames[idx].ut);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_NullFrames_ReturnsMinusOne()
        {
            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(null, 10.0, tolerance: 1.0);
            Assert.Equal(-1, idx);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_EmptyFrames_ReturnsMinusOne()
        {
            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(
                new List<TrajectoryPoint>(), 10.0, tolerance: 1.0);
            Assert.Equal(-1, idx);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_NegativeTolerance_ClampsToZero()
        {
            // Defensive: tolerance < 0 should be clamped to 0 (exact match
            // only). The helper must not throw or pick an arbitrary sample.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(9.95, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
                MakePoint(10.0, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
                MakePoint(10.05, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(
                frames, 10.0, tolerance: -1.0);

            // Only the exact-match sample at UT=10.0 qualifies under tolerance=0.
            Assert.Equal(1, idx);
        }

        // ----- Sentinel-bit semantics -----

        [Fact]
        public void TryFindFlaggedSampleAtUT_FutureBitOnlyNoStructuralEventBit_DoesNotMatch()
        {
            // A point with bit 1+ set but bit 0 (StructuralEventSnapshot) clear
            // must NOT be picked up. This pins the bit-0 contract for future
            // additive flag bits.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(10.0, flags: 0x02), // hypothetical future bit, not StructuralEventSnapshot
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 1.0);

            Assert.Equal(-1, idx);
        }

        [Fact]
        public void TryFindFlaggedSampleAtUT_FutureBitAndStructuralEventBit_Matches()
        {
            // Bit 0 (StructuralEventSnapshot) set alongside a hypothetical future bit.
            // The helper must still match — it tests bit 0 in isolation.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(10.0, flags: (byte)((byte)TrajectoryPointFlags.StructuralEventSnapshot | 0x02)),
            };

            int idx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(frames, 10.0, tolerance: 1.0);

            Assert.Equal(0, idx);
        }
    }
}
