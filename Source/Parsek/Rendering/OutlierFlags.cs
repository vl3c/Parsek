using System;
using System.Collections.Generic;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 8 per-section outlier annotation (design doc §14, §17.3.1
    /// `OutlierFlagsList` block, §18 Phase 8). Built by
    /// <see cref="OutlierClassifier.Classify"/> before the smoothing spline
    /// is fit. Persisted in the <c>.pann</c> sidecar so a re-load on the
    /// same configuration recovers the exact same rejection set without
    /// re-classifying (HR-3, HR-10).
    /// </summary>
    /// <remarks>
    /// Bit packing: for sample index <c>s</c>, byte = <c>s / 8</c>, bit =
    /// <c>s % 8</c> (LSB-first). Bit value 1 = rejected, 0 = kept. The
    /// <see cref="SampleCount"/> field is in-memory only; the sidecar
    /// schema does not persist it because the loader can re-derive it from
    /// <c>section.frames.Count</c> at install time.
    /// </remarks>
    internal sealed class OutlierFlags
    {
        /// <summary>Section index inside the owning recording's
        /// <c>TrackSections</c> list.</summary>
        public int SectionIndex;

        /// <summary>Bitwise OR of <see cref="OutlierClassifier.ClassifierBit"/>
        /// values that fired anywhere in this section. The
        /// <see cref="OutlierClassifier.ClassifierBit.Cluster"/> bit is a
        /// section-wide attribute and never appears in the per-sample
        /// <see cref="PackedBitmap"/>.</summary>
        public byte ClassifierMask;

        /// <summary>Per-sample reject bitmap, LSB-first. Length =
        /// <c>(SampleCount + 7) / 8</c>.</summary>
        public byte[] PackedBitmap;

        /// <summary>Number of samples whose bit is set in
        /// <see cref="PackedBitmap"/>.</summary>
        public int RejectedCount;

        /// <summary>Length of the section's frames list at classify time. NOT
        /// persisted in the sidecar — the reader rebuilds it from
        /// <c>section.frames.Count</c>.</summary>
        public int SampleCount;

        /// <summary>Returns true when the sample at
        /// <paramref name="sampleIndex"/> was rejected. Out-of-range indices
        /// return false (defensive — callers should pass a valid index but
        /// the contract favours not throwing on Phase 8 lookups).</summary>
        internal bool IsRejected(int sampleIndex)
        {
            if (sampleIndex < 0 || sampleIndex >= SampleCount) return false;
            if (PackedBitmap == null) return false;
            int byteIndex = sampleIndex >> 3;
            if (byteIndex >= PackedBitmap.Length) return false;
            int bitIndex = sampleIndex & 0x7;
            return (PackedBitmap[byteIndex] & (1 << bitIndex)) != 0;
        }

        /// <summary>Enumerates rejected sample indices in ascending order.</summary>
        internal IEnumerable<int> RejectedSampleIndices
        {
            get
            {
                if (PackedBitmap == null || SampleCount <= 0) yield break;
                for (int s = 0; s < SampleCount; s++)
                {
                    if (IsRejected(s)) yield return s;
                }
            }
        }

        /// <summary>Builds an "all kept" placeholder for a section the
        /// classifier inspected but found clean. Stores nothing per-sample
        /// (zero-byte bitmap) so writers can drop empty entries without
        /// losing signal.</summary>
        internal static OutlierFlags Empty(int sectionIndex, int sampleCount)
        {
            return new OutlierFlags
            {
                SectionIndex = sectionIndex,
                ClassifierMask = 0,
                PackedBitmap = new byte[(sampleCount + 7) / 8],
                RejectedCount = 0,
                SampleCount = sampleCount,
            };
        }

        /// <summary>Pack a per-sample boolean into the LSB-first byte layout.</summary>
        internal static byte[] BuildPackedBitmap(bool[] perSample)
        {
            if (perSample == null) return new byte[0];
            int count = perSample.Length;
            byte[] result = new byte[(count + 7) / 8];
            for (int s = 0; s < count; s++)
            {
                if (!perSample[s]) continue;
                int byteIndex = s >> 3;
                int bitIndex = s & 0x7;
                result[byteIndex] |= (byte)(1 << bitIndex);
            }
            return result;
        }

        /// <summary>Unpack a packed bitmap into a per-sample boolean array of
        /// length <paramref name="sampleCount"/>. Bits beyond the bitmap's
        /// byte length are returned as false; bits beyond
        /// <paramref name="sampleCount"/> in the bitmap are ignored.</summary>
        internal static bool[] UnpackBitmap(byte[] packed, int sampleCount)
        {
            if (sampleCount < 0) sampleCount = 0;
            bool[] result = new bool[sampleCount];
            if (packed == null) return result;
            for (int s = 0; s < sampleCount; s++)
            {
                int byteIndex = s >> 3;
                if (byteIndex >= packed.Length) break;
                int bitIndex = s & 0x7;
                result[s] = (packed[byteIndex] & (1 << bitIndex)) != 0;
            }
            return result;
        }
    }
}
