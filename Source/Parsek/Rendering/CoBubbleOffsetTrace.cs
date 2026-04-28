namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 5 co-bubble offset trace (design doc §6.5 / §10.2 / §17.3.1).
    /// One trace per overlap window between two recordings that shared a
    /// physics bubble at recording time. Persisted into the <c>.pann</c>
    /// <c>CoBubbleOffsetTraces</c> block alongside splines and anchor
    /// candidates; consumed at playback by <c>CoBubbleBlender</c> which
    /// resolves the per-UT offset against the designated primary's standalone
    /// world position.
    ///
    /// <para>
    /// Per-axis offset semantics (frame-tag dependent):
    /// <list type="bullet">
    ///   <item><c>FrameTag == 0</c> (body-fixed) — <c>(Dx, Dy, Dz)</c> is the
    ///   world-frame translation between primary and peer at the sample's UT.
    ///   The body's rotation factors out because both world positions were
    ///   taken in the same body-fixed frame at the same UT.</item>
    ///   <item><c>FrameTag == 1</c> (inertial) — <c>(Dx, Dy, Dz)</c> is the
    ///   peer-minus-primary translation in the body's inertial frame at the
    ///   sample's UT. Playback re-lifts/lowers via the body's rotation phase
    ///   at the playback UT (<c>FrameTransform.LowerOffsetFromInertialToWorld</c>).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Per-trace peer validation fields (<see cref="PeerSourceFormatVersion"/>,
    /// <see cref="PeerSidecarEpoch"/>, <see cref="PeerContentSignature"/>) gate
    /// trace freshness against the peer's own <c>.prec</c> at load time. Drift
    /// drops only the affected trace, not the whole <c>.pann</c> file
    /// (design doc §17.3.1 "Per-Trace Peer Validation"). HR-9: the consumer
    /// falls back to standalone Stages 1+2+3+4 on any miss; HR-10: drift on
    /// the peer side flips the per-trace cache key.
    /// </para>
    /// </summary>
    internal sealed class CoBubbleOffsetTrace
    {
        /// <summary>The other recording's id (the peer side of the pair).
        /// The owning recording is implicit — every <c>.pann</c> file holds
        /// traces whose offset axis is centred on its own recording (the
        /// "primary in the recording-time hint" — see
        /// <see cref="PrimaryDesignation"/>).</summary>
        public string PeerRecordingId;

        /// <summary>Peer's <c>RecordingFormatVersion</c> at trace-build time.
        /// Compared against the live peer at load — drift drops the trace
        /// (HR-10).</summary>
        public int PeerSourceFormatVersion;

        /// <summary>Peer's <c>SidecarEpoch</c> at trace-build time. Drift
        /// drops the trace.</summary>
        public int PeerSidecarEpoch;

        /// <summary>SHA-256 over the peer's raw sample bytes inside
        /// [<see cref="StartUT"/>, <see cref="EndUT"/>] at trace-build time.
        /// Drift drops the trace. 32 bytes; never null in a populated trace
        /// (the writer rejects null/zero-length signatures).</summary>
        public byte[] PeerContentSignature;

        /// <summary>Window start UT.</summary>
        public double StartUT;

        /// <summary>Window end UT.</summary>
        public double EndUT;

        /// <summary>0 = body-fixed (Atmospheric / Surface*), 1 = inertial
        /// (ExoPropulsive / ExoBallistic). Pinned by the primary's segment
        /// at recording time.</summary>
        public byte FrameTag;

        /// <summary>Commit-time hint: 0 = self (the recording owning this
        /// <c>.pann</c> was primary at recording time), 1 = peer was primary.
        /// The session-time <c>CoBubblePrimarySelector</c> is authoritative;
        /// this hint is for diagnostics and lazy-recompute symmetry only.</summary>
        public byte PrimaryDesignation;

        /// <summary>Resampled UTs (monotonically increasing).</summary>
        public double[] UTs;

        /// <summary>Per-sample x-axis offset (peer minus primary, primary's
        /// frame).</summary>
        public float[] Dx;

        /// <summary>Per-sample y-axis offset.</summary>
        public float[] Dy;

        /// <summary>Per-sample z-axis offset.</summary>
        public float[] Dz;

        /// <summary>Name of the celestial body the offset is centred on
        /// (matches <see cref="CelestialBody.bodyName"/>). Captured at
        /// detect-time from the overlap window's body and persisted in the
        /// <c>.pann</c> CoBubbleOffsetTraces block (P1-A fix). The runtime
        /// blender resolves this via <see cref="FlightGlobals.Bodies"/> to
        /// drive the inertial→world rotation lower for FrameTag=1 traces;
        /// without it the production lower silently became a no-op.
        /// May be null only for legacy traces written before the field
        /// landed (alg-stamp v6 invalidates them on first load).</summary>
        public string BodyName;
    }
}
