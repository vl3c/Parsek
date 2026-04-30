using System;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the per-recording / per-section in-memory annotation store
    /// (design doc §17.3.1, Phase 1). Touches static state, so runs in the
    /// "Sequential" collection and resets between cases.
    /// </summary>
    [Collection("Sequential")]
    public class SectionAnnotationStoreTests : IDisposable
    {
        public SectionAnnotationStoreTests()
        {
            SectionAnnotationStore.ResetForTesting();
        }

        public void Dispose()
        {
            SectionAnnotationStore.ResetForTesting();
        }

        private static SmoothingSpline MakeSpline(double knot0, double knot1, float ctrlSeed)
        {
            return new SmoothingSpline
            {
                SplineType = 0,
                Tension = 0.5f,
                KnotsUT = new[] { knot0, knot1 },
                ControlsX = new[] { ctrlSeed, ctrlSeed + 1f },
                ControlsY = new[] { ctrlSeed + 2f, ctrlSeed + 3f },
                ControlsZ = new[] { ctrlSeed + 4f, ctrlSeed + 5f },
                FrameTag = 0,
                IsValid = true,
            };
        }

        [Fact]
        public void PutGet_RoundTrip()
        {
            // What makes it fail: a corrupted Put / Get pair would either lose the
            // spline or return a different value, breaking the cache contract.
            var input = MakeSpline(10.0, 20.0, 1f);
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, input);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("recA", 0, out var output));
            Assert.True(output.IsValid);
            Assert.Equal(input.KnotsUT, output.KnotsUT);
            Assert.Equal(input.ControlsX, output.ControlsX);
            Assert.Equal(input.ControlsY, output.ControlsY);
            Assert.Equal(input.ControlsZ, output.ControlsZ);
            Assert.Equal(input.SplineType, output.SplineType);
            Assert.Equal(input.Tension, output.Tension);
            Assert.Equal(input.FrameTag, output.FrameTag);
        }

        [Fact]
        public void Get_Missing_ReturnsFalse()
        {
            // What makes it fail: empty store returning a phantom spline would
            // silently feed stale geometry into Stage 1 evaluation.
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("missing", 0, out var spline));
            Assert.False(spline.IsValid);
        }

        [Fact]
        public void Get_DifferentRecording_ReturnsFalse()
        {
            // What makes it fail: a recordingId mismatch returning another
            // recording's spline would cross-contaminate annotations across
            // recordings (HR-14 violation in spirit).
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("recB", 0, out _));
        }

        [Fact]
        public void Put_OverwritesSilently()
        {
            // What makes it fail: a Put that appends instead of overwriting
            // would let stale annotations linger past their cache-key window.
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            var v2 = MakeSpline(2.0, 3.0, 9f);
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, v2);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline("recA", 0, out var output));
            Assert.Equal(v2.KnotsUT, output.KnotsUT);
            Assert.Equal(v2.ControlsX, output.ControlsX);
        }

        [Fact]
        public void RemoveRecording_ClearsAllSections()
        {
            // What makes it fail: a Remove that drops only one section would
            // leave orphan annotations after the recording was unloaded.
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            SectionAnnotationStore.PutSmoothingSpline("recA", 1, MakeSpline(1.0, 2.0, 2f));
            SectionAnnotationStore.PutSmoothingSpline("recA", 2, MakeSpline(2.0, 3.0, 3f));
            Assert.Equal(3, SectionAnnotationStore.GetSplineCountForRecording("recA"));

            SectionAnnotationStore.RemoveRecording("recA");
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("recA"));
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("recA", 0, out _));
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("recA", 1, out _));
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("recA", 2, out _));
        }

        [Fact]
        public void Clear_EmptiesEverything()
        {
            // What makes it fail: a Clear that scopes to a single recording
            // would leave other recordings' annotations behind across
            // scene-exit invalidation.
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            SectionAnnotationStore.PutSmoothingSpline("recB", 0, MakeSpline(0.0, 1.0, 2f));

            SectionAnnotationStore.Clear();
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("recA"));
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("recB"));
        }

        [Fact]
        public void ResetForTesting_ClearsState()
        {
            // What makes it fail: ResetForTesting that no-ops would let a
            // previous test's annotations leak into the next test, hiding
            // determinism (HR-3) regressions.
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            SectionAnnotationStore.ResetForTesting();
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("recA"));
        }

        [Fact]
        public void RemoveRecording_NoOpOnMissing()
        {
            // What makes it fail: a Remove that throws on absent ids would
            // make scene-exit cleanup brittle when recordings haven't been
            // annotated yet.
            SectionAnnotationStore.RemoveRecording("never-stored");
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("never-stored"));
        }

        // -------------------------------------------------------------------
        //  Phase 6 candidate map round-trip (design doc §17.3.1)
        // -------------------------------------------------------------------

        [Fact]
        public void PutGet_AnchorCandidates_RoundTrip()
        {
            // What makes it fail: the candidate dict has the same Put/Get
            // contract as the spline dict. A storage bug would lose Side
            // or UT on round-trip and feed wrong inputs to the propagator.
            var c0 = new AnchorCandidate(50.0, AnchorSource.RelativeBoundary, AnchorSide.End);
            var c1 = new AnchorCandidate(75.0, AnchorSource.OrbitalCheckpoint, AnchorSide.Start);
            SectionAnnotationStore.PutAnchorCandidates("recA", 0, new[] { c0, c1 });

            Assert.True(SectionAnnotationStore.TryGetAnchorCandidates("recA", 0, out var arr));
            Assert.Equal(2, arr.Length);
            Assert.Equal(c0, arr[0]);
            Assert.Equal(c1, arr[1]);
        }

        [Fact]
        public void Get_AnchorCandidates_Missing_ReturnsFalse()
        {
            Assert.False(SectionAnnotationStore.TryGetAnchorCandidates("missing", 0, out _));
        }

        [Fact]
        public void RemoveRecording_ClearsBothSplineAndCandidateMaps()
        {
            // What makes it fail: Phase 6 added a parallel map; if Remove
            // forgets to clear it, the candidate set would leak across
            // recompute cycles even after the splines were correctly
            // dropped.
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            SectionAnnotationStore.PutAnchorCandidates("recA", 0, new[]
            {
                new AnchorCandidate(10.0, AnchorSource.Loop, AnchorSide.Start),
            });

            SectionAnnotationStore.RemoveRecording("recA");

            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline("recA", 0, out _));
            Assert.False(SectionAnnotationStore.TryGetAnchorCandidates("recA", 0, out _));
        }

        [Fact]
        public void Clear_ClearsBothSplineAndCandidateMaps()
        {
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            SectionAnnotationStore.PutAnchorCandidates("recA", 0, new[]
            {
                new AnchorCandidate(10.0, AnchorSource.Loop, AnchorSide.Start),
            });

            SectionAnnotationStore.Clear();

            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("recA"));
            Assert.Equal(0, SectionAnnotationStore.GetAnchorCandidateSectionCountForRecording("recA"));
        }

        // -------------------------------------------------------------------
        //  Phase 8 outlier-flags map round-trip (design doc §17.3.1)
        // -------------------------------------------------------------------

        private static OutlierFlags MakeOutlierFlags(int sectionIndex, int sampleCount, byte mask)
        {
            bool[] perSample = new bool[sampleCount];
            int rejected = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                if ((i % 4) == 1) { perSample[i] = true; rejected++; }
            }
            return new OutlierFlags
            {
                SectionIndex = sectionIndex,
                ClassifierMask = mask,
                PackedBitmap = OutlierFlags.BuildPackedBitmap(perSample),
                RejectedCount = rejected,
                SampleCount = sampleCount,
            };
        }

        [Fact]
        public void PutGet_OutlierFlags_RoundTrip()
        {
            // What makes it fail: a corrupted Put / Get pair would lose the
            // bitmap or return a different value, breaking the cache contract.
            OutlierFlags input = MakeOutlierFlags(0, 12, mask: 1);
            SectionAnnotationStore.PutOutlierFlags("recA", 0, input);
            Assert.True(SectionAnnotationStore.TryGetOutlierFlags("recA", 0, out var output));
            Assert.NotNull(output);
            Assert.Equal(input.SectionIndex, output.SectionIndex);
            Assert.Equal(input.ClassifierMask, output.ClassifierMask);
            Assert.Equal(input.PackedBitmap, output.PackedBitmap);
            Assert.Equal(input.RejectedCount, output.RejectedCount);
            Assert.Equal(input.SampleCount, output.SampleCount);
        }

        [Fact]
        public void PutOutlierFlags_OverwritesExisting()
        {
            SectionAnnotationStore.PutOutlierFlags("recA", 0, MakeOutlierFlags(0, 8, mask: 1));
            OutlierFlags v2 = MakeOutlierFlags(0, 16, mask: 4);
            SectionAnnotationStore.PutOutlierFlags("recA", 0, v2);
            Assert.True(SectionAnnotationStore.TryGetOutlierFlags("recA", 0, out var output));
            Assert.Equal(v2.SampleCount, output.SampleCount);
            Assert.Equal(v2.ClassifierMask, output.ClassifierMask);
        }

        [Fact]
        public void Get_OutlierFlags_Missing_ReturnsFalse()
        {
            Assert.False(SectionAnnotationStore.TryGetOutlierFlags("missing", 0, out _));
        }

        [Fact]
        public void RemoveRecording_ClearsOutlierFlags()
        {
            SectionAnnotationStore.PutOutlierFlags("recA", 0, MakeOutlierFlags(0, 8, mask: 1));
            SectionAnnotationStore.PutOutlierFlags("recA", 1, MakeOutlierFlags(1, 8, mask: 2));
            Assert.Equal(2, SectionAnnotationStore.GetOutlierFlagsCountForRecording("recA"));

            SectionAnnotationStore.RemoveRecording("recA");

            Assert.Equal(0, SectionAnnotationStore.GetOutlierFlagsCountForRecording("recA"));
            Assert.False(SectionAnnotationStore.TryGetOutlierFlags("recA", 0, out _));
        }

        [Fact]
        public void Clear_ClearsAllFourMaps()
        {
            // What makes it fail: Phase 8 added a fourth map; if Clear
            // forgets it, the outlier flags would leak across scene-exit
            // invalidation.
            SectionAnnotationStore.PutSmoothingSpline("recA", 0, MakeSpline(0.0, 1.0, 1f));
            SectionAnnotationStore.PutAnchorCandidates("recA", 0, new[]
            {
                new AnchorCandidate(10.0, AnchorSource.Loop, AnchorSide.Start),
            });
            SectionAnnotationStore.PutOutlierFlags("recA", 0, MakeOutlierFlags(0, 8, mask: 1));

            SectionAnnotationStore.Clear();

            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording("recA"));
            Assert.Equal(0, SectionAnnotationStore.GetAnchorCandidateSectionCountForRecording("recA"));
            Assert.Equal(0, SectionAnnotationStore.GetOutlierFlagsCountForRecording("recA"));
        }

        [Fact]
        public void GetOutlierFlagsCountForRecording_ReturnsZeroWhenAbsent()
        {
            Assert.Equal(0, SectionAnnotationStore.GetOutlierFlagsCountForRecording("never-stored"));
        }
    }
}
