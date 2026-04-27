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
    }
}
