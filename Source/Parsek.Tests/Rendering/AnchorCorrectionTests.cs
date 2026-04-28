using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 2 <see cref="AnchorCorrection"/> + <see cref="AnchorKey"/>
    /// value types (design doc §17.3.1, §18 Phase 2 row, §6.3 / §6.4). Pure value-type
    /// behavior — no static state, so this class does NOT need
    /// <c>[Collection("Sequential")]</c>.
    /// </summary>
    public class AnchorCorrectionTests
    {
        [Fact]
        public void AnchorCorrection_Constructor_PreservesAllFields()
        {
            // What makes it fail: a constructor that drops or zeroes a field
            // (e.g. forgets to assign Epsilon) would silently render every
            // ghost at the smoothed-only position, masking the entire Phase 2
            // win on the first re-fly.
            var ac = new AnchorCorrection(
                recordingId: "rec-abc",
                sectionIndex: 3,
                side: AnchorSide.Start,
                ut: 12345.6789,
                epsilon: new Vector3d(1.5, -2.25, 0.125),
                source: AnchorSource.LiveSeparation);

            Assert.Equal("rec-abc", ac.RecordingId);
            Assert.Equal(3, ac.SectionIndex);
            Assert.Equal(AnchorSide.Start, ac.Side);
            Assert.Equal(12345.6789, ac.UT);
            Assert.Equal(1.5, ac.Epsilon.x);
            Assert.Equal(-2.25, ac.Epsilon.y);
            Assert.Equal(0.125, ac.Epsilon.z);
            Assert.Equal(AnchorSource.LiveSeparation, ac.Source);
        }

        [Fact]
        public void AnchorCorrection_Default_HasZeroEpsilon()
        {
            // What makes it fail: if the readonly struct's default value
            // produced a non-zero Epsilon (e.g. from a static initializer
            // bug), uninitialized lookups in RenderSessionState.TryLookup
            // out-params would inject phantom translations into ghost
            // positioning — exactly the silent-failure class HR-9 forbids.
            AnchorCorrection ac = default;

            // Compare component-wise: Vector3d's IEquatable uses an epsilon-
            // based KSP convention that doesn't always satisfy xUnit's
            // Assert.Equal contract on net472 / mock-Unity, so pin each axis
            // explicitly.
            Assert.Equal(0.0, ac.Epsilon.x);
            Assert.Equal(0.0, ac.Epsilon.y);
            Assert.Equal(0.0, ac.Epsilon.z);
            Assert.Equal(0.0, ac.UT);
            Assert.Null(ac.RecordingId);
            Assert.Equal(0, ac.SectionIndex);
            Assert.Equal(AnchorSide.Start, ac.Side);
            Assert.Equal(AnchorSource.LiveSeparation, ac.Source);
        }

        [Fact]
        public void AnchorSource_AllValuesUnderByte()
        {
            // What makes it fail: §17.3.1's AnchorCandidatesList serializes the
            // source byte-by-byte in the .pann binary. If a future contributor
            // accidentally widens the enum or appends an 11th value past 255,
            // the next .pann write would truncate or panic. This test pins the
            // contract before the file format is bumped.
            foreach (AnchorSource src in System.Enum.GetValues(typeof(AnchorSource)))
            {
                int v = (int)src;
                Assert.InRange(v, 0, 255);
            }

            // AnchorSide is also a byte today; pin it the same way.
            foreach (AnchorSide side in System.Enum.GetValues(typeof(AnchorSide)))
            {
                int v = (int)side;
                Assert.InRange(v, 0, 255);
            }
        }

        [Fact]
        public void AnchorKey_EqualKeys_AreEqual()
        {
            // What makes it fail: a hash that drops one of the three fields, or
            // an Equals that uses reference equality on RecordingId, would
            // produce dictionary collisions / misses inside RenderSessionState
            // and silently break anchor lookup.
            var a = new AnchorKey("rec-abc", 3, AnchorSide.Start);
            var b = new AnchorKey("rec-abc", 3, AnchorSide.Start);

            Assert.Equal(a, b);
            Assert.True(a.Equals(b));
            Assert.True(((object)a).Equals(b));
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void AnchorKey_DifferentSection_AreNotEqual()
        {
            // What makes it fail: ignoring SectionIndex in equality would make
            // every section in the same recording collapse to one entry — the
            // anchor for section 3 would overwrite the one for section 0.
            var a = new AnchorKey("rec-abc", 3, AnchorSide.Start);
            var b = new AnchorKey("rec-abc", 4, AnchorSide.Start);

            Assert.NotEqual(a, b);
            Assert.False(a.Equals(b));
        }

        [Fact]
        public void AnchorKey_DifferentSide_AreNotEqual()
        {
            // What makes it fail: ignoring Side in equality would make Phase 3
            // end-anchor lookups silently return start-anchor entries (or
            // vice versa), wrecking the lerp endpoints.
            var a = new AnchorKey("rec-abc", 3, AnchorSide.Start);
            var b = new AnchorKey("rec-abc", 3, AnchorSide.End);

            Assert.NotEqual(a, b);
            Assert.False(a.Equals(b));
        }
    }
}
