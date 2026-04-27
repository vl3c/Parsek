using System;

namespace Parsek.Rendering
{
    /// <summary>
    /// Anchor source taxonomy (design doc §7.1 — §7.10). The ten members map
    /// one-to-one to the anchor types the rendering pipeline can identify; the
    /// numeric values are persisted in the <c>.pann</c> file format
    /// (<c>AnchorCandidatesList</c> entry, see §17.3.1) as <c>byte</c>, so they
    /// must remain stable. <see cref="AnchorPriority"/>'s priority order
    /// (§7.11) is encoded by lookup, not by enum order.
    /// </summary>
    /// <remarks>
    /// Phase 2 only emits <see cref="LiveSeparation"/>; the remaining members
    /// are reserved by §18 Phase 6 ("Anchor Taxonomy Completion"). Adding the
    /// values now keeps the persisted byte layout stable across phases — a
    /// later rename would force a <c>.pann</c> algorithm-stamp bump and cache
    /// invalidation across every install.
    /// </remarks>
    internal enum AnchorSource : byte
    {
        LiveSeparation     = 0,
        DockOrMerge        = 1,
        RelativeBoundary   = 2,
        OrbitalCheckpoint  = 3,
        SoiTransition      = 4,
        BubbleEntry        = 5,
        BubbleExit         = 6,
        CoBubblePeer       = 7,
        SurfaceContinuous  = 8,
        Loop               = 9
    }

    /// <summary>
    /// Which side of the segment an anchor lives on. Phase 2 only ever stores
    /// <see cref="Start"/>; Phase 3 introduces end anchors for the lerp case
    /// (design doc §6.4).
    /// </summary>
    internal enum AnchorSide : byte
    {
        Start = 0,
        End   = 1
    }

    /// <summary>
    /// One rigid-translation correction <c>ε</c> applied to a smoothed segment
    /// at a single endpoint UT (design doc §6.3). Computed once per re-fly
    /// session entry and stored in <see cref="RenderSessionState"/>; the
    /// renderer adds it to the smoothed body-fixed position to produce the
    /// final ghost world position. <c>ε</c> is a translation only — rotation
    /// remains the canonical recorded rotation per HR-1.
    /// </summary>
    /// <remarks>
    /// Readonly value type — instances are pinned in <see cref="RenderSessionState"/>
    /// after construction and never mutated. <see cref="UT"/> is the segment-
    /// endpoint UT, NOT the marker's invocation UT; for Phase 2 this is the
    /// branch-point UT where the live and ghost siblings separated.
    /// </remarks>
    internal readonly struct AnchorCorrection
    {
        /// <summary>Recording id this correction applies to.</summary>
        public readonly string RecordingId;

        /// <summary>Section index inside the recording's <c>TrackSections</c>.</summary>
        public readonly int SectionIndex;

        /// <summary>Which endpoint of the segment the correction lives at.</summary>
        public readonly AnchorSide Side;

        /// <summary>Universal time of the anchor (segment-endpoint UT).</summary>
        public readonly double UT;

        /// <summary>
        /// Translation correction in body-frame world space (metres). Added to
        /// the smoothed-spline body-fixed position at render time.
        /// </summary>
        public readonly Vector3d Epsilon;

        /// <summary>Which §7 anchor type produced this correction.</summary>
        public readonly AnchorSource Source;

        public AnchorCorrection(
            string recordingId,
            int sectionIndex,
            AnchorSide side,
            double ut,
            Vector3d epsilon,
            AnchorSource source)
        {
            RecordingId = recordingId;
            SectionIndex = sectionIndex;
            Side = side;
            UT = ut;
            Epsilon = epsilon;
            Source = source;
        }
    }

    /// <summary>
    /// Composite key into <see cref="RenderSessionState"/>'s anchor map. A
    /// single segment may have one correction per side (§6.4); Phase 2 only
    /// writes <see cref="AnchorSide.Start"/>.
    /// </summary>
    internal readonly struct AnchorKey : IEquatable<AnchorKey>
    {
        public readonly string RecordingId;
        public readonly int SectionIndex;
        public readonly AnchorSide Side;

        public AnchorKey(string recordingId, int sectionIndex, AnchorSide side)
        {
            RecordingId = recordingId;
            SectionIndex = sectionIndex;
            Side = side;
        }

        public bool Equals(AnchorKey other)
        {
            return string.Equals(RecordingId, other.RecordingId, StringComparison.Ordinal)
                && SectionIndex == other.SectionIndex
                && Side == other.Side;
        }

        public override bool Equals(object obj)
        {
            return obj is AnchorKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            // net472-compatible stable combiner. The 397 prime mirrors the
            // ReSharper-style pattern used elsewhere in the codebase; HashCode
            // .Combine is unavailable on net472 without an extra package.
            unchecked
            {
                int hash = RecordingId != null
                    ? StringComparer.Ordinal.GetHashCode(RecordingId)
                    : 0;
                hash = (hash * 397) ^ SectionIndex;
                hash = (hash * 397) ^ (int)Side;
                return hash;
            }
        }
    }
}
