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
        public void AnchorSourceAndSide_AreByteBacked()
        {
            // What makes it fail: §17.3.1's AnchorCandidatesList serializes the
            // source and side into ONE persisted type byte in the .pann binary
            // (AnchorCandidate.ToTypeByte casts the source to byte). Widening
            // either enum's backing type would let a member value outgrow that
            // byte without a compile error, so the backing type itself is the
            // contract. The values staying below the side bit is pinned by
            // AnchorCandidateBuilderTests.TypeByte_PacksSourceAndSide_Roundtrip.
            Assert.Equal(typeof(byte), System.Enum.GetUnderlyingType(typeof(AnchorSource)));
            Assert.Equal(typeof(byte), System.Enum.GetUnderlyingType(typeof(AnchorSide)));
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
