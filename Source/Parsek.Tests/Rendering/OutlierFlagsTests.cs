using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 8 <see cref="OutlierFlags"/> packed-bitmap POCO
    /// (design doc §17.3.1, plan §3). The bit layout MUST be LSB-first per
    /// sample so the writer / reader / classifier all agree.
    /// </summary>
    public class OutlierFlagsTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(100)]
        [InlineData(1000)]
        public void PackedBitmap_RoundTrip_AtVariousLengths(int sampleCount)
        {
            // What makes it fail: an off-by-one in the bit/byte mapping
            // leaves the LSB-first contract broken; on round-trip, sample
            // 0 ends up as bit 7 of byte 0 (or worse, pack/unpack disagree
            // and the bitmap reads garbage at boundaries).
            bool[] perSample = new bool[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                perSample[i] = (i % 3) == 0; // every third sample rejected
            byte[] packed = OutlierFlags.BuildPackedBitmap(perSample);
            // Length matches the (count + 7) / 8 contract.
            Assert.Equal((sampleCount + 7) / 8, packed.Length);
            bool[] unpacked = OutlierFlags.UnpackBitmap(packed, sampleCount);
            Assert.Equal(sampleCount, unpacked.Length);
            for (int i = 0; i < sampleCount; i++)
                Assert.Equal(perSample[i], unpacked[i]);
        }

        [Fact]
        public void IsRejected_BoundsCheck_OutOfRangeReturnsFalse()
        {
            // What makes it fail: a missing bounds-check throws on out-of-
            // range reads, breaking the playback consumer that may probe a
            // section index before knowing the bitmap size.
            var flags = new OutlierFlags
            {
                SectionIndex = 0,
                ClassifierMask = 0,
                PackedBitmap = OutlierFlags.BuildPackedBitmap(new[] { true, false, true }),
                RejectedCount = 2,
                SampleCount = 3,
            };
            Assert.False(flags.IsRejected(-1));
            Assert.False(flags.IsRejected(3));
            Assert.False(flags.IsRejected(int.MaxValue));
            Assert.True(flags.IsRejected(0));
            Assert.False(flags.IsRejected(1));
            Assert.True(flags.IsRejected(2));
        }

        [Fact]
        public void Empty_AllFalse_ZeroRejectionsZeroMask()
        {
            // What makes it fail: a non-zero mask or non-zero rejected count
            // on the Empty placeholder would falsely signal that the
            // classifier ran and found something — exactly the wrong signal
            // for HR-9 visibility.
            OutlierFlags empty = OutlierFlags.Empty(7, 12);
            Assert.Equal(7, empty.SectionIndex);
            Assert.Equal(0, empty.ClassifierMask);
            Assert.Equal(0, empty.RejectedCount);
            Assert.Equal(12, empty.SampleCount);
            Assert.NotNull(empty.PackedBitmap);
            for (int b = 0; b < empty.PackedBitmap.Length; b++)
                Assert.Equal(0, empty.PackedBitmap[b]);
            for (int i = 0; i < empty.SampleCount; i++)
                Assert.False(empty.IsRejected(i));
        }

        [Fact]
        public void RejectedSampleIndices_EnumeratesAscending()
        {
            // What makes it fail: enumeration in the wrong order would
            // confuse downstream consumers that assume ascending indices.
            var flags = new OutlierFlags
            {
                SectionIndex = 0,
                ClassifierMask = 1,
                PackedBitmap = OutlierFlags.BuildPackedBitmap(new[]
                    { false, true, false, true, true, false, true, false, false, true }),
                RejectedCount = 5,
                SampleCount = 10,
            };
            var indices = new System.Collections.Generic.List<int>();
            foreach (int i in flags.RejectedSampleIndices) indices.Add(i);
            Assert.Equal(new[] { 1, 3, 4, 6, 9 }, indices);
        }

        [Fact]
        public void BuildPackedBitmap_NullInput_ReturnsEmptyArray()
        {
            byte[] packed = OutlierFlags.BuildPackedBitmap(null);
            Assert.NotNull(packed);
            Assert.Empty(packed);
        }
    }
}
