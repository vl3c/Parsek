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

    /// <summary>
    /// Discriminator for the three valid <see cref="AnchorCorrectionInterval"/>
    /// configurations described in design doc §6.4. Stored as an explicit
    /// field on the interval struct so callers do not need to reason about
    /// <c>default(AnchorCorrection)</c> sentinels — both <c>UT == 0</c> and a
    /// zero <c>Epsilon</c> are valid real values.
    /// </summary>
    internal enum AnchorIntervalKind : byte
    {
        StartOnly = 0,
        EndOnly   = 1,
        Both      = 2
    }

    /// <summary>
    /// Phase 3 (design doc §6.4 Stage 4 / §8 / §18 Phase 3) interval
    /// abstraction over the start- and end-side <see cref="AnchorCorrection"/>s
    /// belonging to a single <c>(recordingId, sectionIndex)</c> segment. The
    /// renderer queries <see cref="RenderSessionState.LookupForSegmentInterval"/>
    /// once per ghost-position frame and evaluates ε at the current playback
    /// UT via <see cref="EvaluateAt"/>.
    ///
    /// <para>
    /// Representation: this struct is an explicit tagged union — the
    /// <see cref="Kind"/> field selects between the three §6.4 cases, and the
    /// unused side stores <c>default(AnchorCorrection)</c>. We deliberately
    /// avoid using a nullable side field as the discriminator because UT=0 and
    /// Epsilon=zero are both legal real values and would collide with sentinel
    /// detection. The factory methods (<see cref="StartOnly"/>,
    /// <see cref="EndOnly"/>, <see cref="Both"/>) are the only construction
    /// path; the public constructor is for legacy / interop use only.
    /// </para>
    ///
    /// <para>
    /// HR-7 (design doc §26.1): the interval is keyed by
    /// <c>(recordingId, sectionIndex)</c> in <see cref="RenderSessionState"/>,
    /// and the consumer hook always queries with the section that owns the
    /// current playback UT — so the lerp is naturally bounded to one section
    /// and CANNOT cross a hard discontinuity.
    /// </para>
    /// </summary>
    internal readonly struct AnchorCorrectionInterval
    {
        /// <summary>Which §6.4 case this interval represents.</summary>
        public readonly AnchorIntervalKind Kind;

        /// <summary>Start-side anchor (valid when <see cref="Kind"/> is StartOnly or Both).</summary>
        public readonly AnchorCorrection Start;

        /// <summary>End-side anchor (valid when <see cref="Kind"/> is EndOnly or Both).</summary>
        public readonly AnchorCorrection End;

        /// <summary>
        /// Both-end ε divergence threshold (design doc §8 / §19.2 Stage 4 row).
        /// When <c>|ε_end − ε_start|</c> exceeds this, the segment is flagged
        /// as suspect — the lerp still runs (HR-9: keep the value, surface the
        /// failure) but a Pipeline-Lerp Warn fires once per session per key.
        /// </summary>
        public const double DivergenceWarnThresholdM = 50.0;

        /// <summary>
        /// Direct constructor. Prefer the <see cref="StartOnly"/> /
        /// <see cref="EndOnly"/> / <see cref="Both"/> factories — they
        /// validate and document the chosen kind. Kept internal for completeness.
        /// </summary>
        internal AnchorCorrectionInterval(
            AnchorIntervalKind kind,
            AnchorCorrection start,
            AnchorCorrection end)
        {
            Kind = kind;
            Start = start;
            End = end;
        }

        /// <summary>
        /// "Anchor at start only" row of §6.4. <see cref="EvaluateAt"/> returns
        /// <c>start.Epsilon</c> for every UT (constant ε across the segment).
        /// </summary>
        internal static AnchorCorrectionInterval StartOnly(AnchorCorrection start)
        {
            return new AnchorCorrectionInterval(AnchorIntervalKind.StartOnly, start, default);
        }

        /// <summary>
        /// "Anchor at end only" row of §6.4. <see cref="EvaluateAt"/> returns
        /// <c>end.Epsilon</c> for every UT. Phase 3 does not produce these in
        /// production code paths — Phase 6 anchor types (dock, RELATIVE
        /// boundary, orbital checkpoint, SOI, bubble exit) populate the End
        /// side; Phase 3 ships only the math + test seam.
        /// </summary>
        internal static AnchorCorrectionInterval EndOnly(AnchorCorrection end)
        {
            return new AnchorCorrectionInterval(AnchorIntervalKind.EndOnly, default, end);
        }

        /// <summary>
        /// "Anchors at both ends" row of §6.4 — the multi-anchor lerp case
        /// that motivates Phase 3. <see cref="EvaluateAt"/> linearly
        /// interpolates between <c>start.Epsilon</c> and <c>end.Epsilon</c>
        /// using the normalized UT position inside <c>[start.UT, end.UT]</c>.
        /// </summary>
        internal static AnchorCorrectionInterval Both(AnchorCorrection start, AnchorCorrection end)
        {
            return new AnchorCorrectionInterval(AnchorIntervalKind.Both, start, end);
        }

        /// <summary>
        /// Evaluates ε at the supplied UT per §6.4. Behaviour:
        /// <list type="bullet">
        ///   <item><description><see cref="AnchorIntervalKind.StartOnly"/> →
        ///   <c>Start.Epsilon</c> (constant).</description></item>
        ///   <item><description><see cref="AnchorIntervalKind.EndOnly"/> →
        ///   <c>End.Epsilon</c> (constant).</description></item>
        ///   <item><description><see cref="AnchorIntervalKind.Both"/> with
        ///   <c>End.UT &gt; Start.UT</c> → linear lerp,
        ///   <c>t_norm = clamp((ut − Start.UT) / (End.UT − Start.UT), 0, 1)</c>;
        ///   the clamp prevents extrapolation past either endpoint (HR-7
        ///   would otherwise be violated when the consumer queries with a UT
        ///   outside the section's range during a one-frame race).</description></item>
        ///   <item><description><see cref="AnchorIntervalKind.Both"/> with
        ///   degenerate span (<c>End.UT &lt;= Start.UT</c>) → emits a
        ///   <c>[Pipeline-Lerp]</c> Warn (HR-9: visible failure) and returns
        ///   <c>Start.Epsilon</c>; the Warn dedup key is the
        ///   <c>(recordingId, sectionIndex, "degenerate")</c> tuple so
        ///   per-frame queries do not spam.</description></item>
        /// </list>
        /// </summary>
        public Vector3d EvaluateAt(double ut)
        {
            switch (Kind)
            {
                case AnchorIntervalKind.StartOnly:
                    return Start.Epsilon;
                case AnchorIntervalKind.EndOnly:
                    return End.Epsilon;
                case AnchorIntervalKind.Both:
                {
                    double span = End.UT - Start.UT;
                    if (span <= 0.0)
                    {
                        // Degenerate-span Warn (HR-9). De-duplicate per
                        // (recordingId, sectionIndex) so per-frame queries do
                        // not spam — RenderSessionState owns the dedup set.
                        RenderSessionState.NotifyDegenerateLerpSpan(
                            Start.RecordingId, Start.SectionIndex, ut, Start.UT, End.UT);
                        return Start.Epsilon;
                    }
                    double tRaw = (ut - Start.UT) / span;
                    double tNorm = tRaw;
                    if (tNorm < 0.0) tNorm = 0.0;
                    else if (tNorm > 1.0) tNorm = 1.0;
                    if (tNorm != tRaw)
                    {
                        // HR-7 implies the consumer never queries outside
                        // [Start.UT, End.UT] in production (per-section
                        // dispatch). If a clamp does fire, surface it once
                        // per session per (recordingId, sectionIndex) so a
                        // boundary bug does not silently mask itself.
                        RenderSessionState.NotifyLerpClampOut(
                            Start.RecordingId, Start.SectionIndex, ut, Start.UT, End.UT);
                    }
                    return Start.Epsilon + (End.Epsilon - Start.Epsilon) * tNorm;
                }
                default:
                    return Vector3d.zero;
            }
        }

        /// <summary>
        /// Returns true when this is a <see cref="AnchorIntervalKind.Both"/>
        /// interval AND the magnitude of <c>End.Epsilon - Start.Epsilon</c>
        /// exceeds <see cref="DivergenceWarnThresholdM"/>. The caller (the
        /// renderer) emits the <c>[Pipeline-Lerp]</c> divergence Warn once
        /// per session per key — see <see cref="RenderSessionState.NotifyLerpDivergenceCheck"/>.
        /// </summary>
        public bool HasSignificantDivergence(out double magnitudeM)
        {
            magnitudeM = 0.0;
            if (Kind != AnchorIntervalKind.Both) return false;
            magnitudeM = (End.Epsilon - Start.Epsilon).magnitude;
            return magnitudeM > DivergenceWarnThresholdM;
        }
    }
}
