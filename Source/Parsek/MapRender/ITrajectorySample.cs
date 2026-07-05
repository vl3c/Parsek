using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §5.3: a typed read-VIEW that splits the frame-overloaded
    /// <c>TrajectoryPoint</c>. The persisted struct's <c>latitude/longitude/altitude</c> mean DEGREES
    /// in Absolute frames but METRES along the anchor's local axes in Relative frames, with no
    /// discriminator on the struct — the documented "ghost inside the planet" trap (CLAUDE.md). The
    /// persisted struct stays as-is (design §13); the typed view the phase layer reads is discriminated
    /// so no downstream reader can mis-read a Relative sample as lat/lon.
    ///
    /// <para><b>NEW, additive, NOT wired in Phase 1.</b> The view is produced once, at chain-assembly
    /// time, by resolving <c>TrackSection.referenceFrame</c> for each sample (a later phase); the
    /// resolution helper is parameterized so the discrimination is itself unit-testable here.</para>
    /// </summary>
    internal interface ITrajectorySample
    {
        /// <summary>The sample's universal-time stamp (assembled-chain clock).</summary>
        double Ut { get; }

        /// <summary>Which frame the sample's spatial fields live in (the discriminator the struct lacks).</summary>
        ReferenceFrame Frame { get; }

        /// <summary>The discriminator kind — drives the resolve dispatch (design §5.3).</summary>
        TrajectorySampleKind Kind { get; }
    }

    /// <summary>The kind discriminator for an <see cref="ITrajectorySample"/>.</summary>
    internal enum TrajectorySampleKind
    {
        /// <summary>Body-fixed degrees lat/lon/alt — resolves via <c>body.GetWorldSurfacePosition</c>.</summary>
        Absolute = 0,
        /// <summary>Anchor-local metres x/y/z — resolves via <c>ApplyRelativeLocalOffset</c>.</summary>
        Relative = 1,
        /// <summary>Keplerian elements (a thin wrapper over <c>OrbitSegment</c>).</summary>
        Orbital = 2,
    }

    /// <summary>
    /// design §5.3: an ABSOLUTE sample — body-fixed DEGREES. <see cref="Latitude"/>/<see cref="Longitude"/>
    /// are degrees and <see cref="Altitude"/> is metres above the datum; the live resolver feeds these to
    /// <c>body.GetWorldSurfacePosition(lat, lon, alt)</c>. <see cref="SrfRelRotation"/> is the recorded
    /// surface-relative rotation (resolved as <c>body.bodyTransform.rotation * srfRelRotation</c> at
    /// playback). This is the surface the <see cref="AnchorFrame.ParentAnchoredChild"/> dual-routing
    /// treats as PRIMARY (bodyFixedFrames → AbsoluteSample, design §5.2).
    /// </summary>
    internal readonly struct AbsoluteSample : ITrajectorySample
    {
        public double Ut { get; }
        public ReferenceFrame Frame => ReferenceFrame.Absolute;
        public TrajectorySampleKind Kind => TrajectorySampleKind.Absolute;

        internal double Latitude { get; }
        internal double Longitude { get; }
        internal double Altitude { get; }
        internal string BodyName { get; }
        internal UnityEngine.Quaternion SrfRelRotation { get; }

        internal AbsoluteSample(
            double ut, double latitude, double longitude, double altitude,
            string bodyName, UnityEngine.Quaternion srfRelRotation)
        {
            Ut = ut;
            Latitude = latitude;
            Longitude = longitude;
            Altitude = altitude;
            BodyName = bodyName;
            SrfRelRotation = srfRelRotation;
        }

        public override string ToString()
            => string.Format(
                CultureInfo.InvariantCulture,
                "Abs UT={0:F1} lat={1:F4} lon={2:F4} alt={3:F1} body={4}",
                Ut, Latitude, Longitude, Altitude, BodyName ?? "?");
    }

    /// <summary>
    /// design §5.3: a RELATIVE sample — METRES along the anchor's local x/y/z axes (NOT lat/lon, the
    /// documented trap). The live resolver feeds these to <c>ApplyRelativeLocalOffset</c> against the
    /// anchor's world rotation/position. This is the surface the
    /// <see cref="AnchorFrame.ParentAnchoredChild"/> dual-routing treats as SECONDARY (frames →
    /// RelativeSample, design §5.2).
    /// </summary>
    internal readonly struct RelativeSample : ITrajectorySample
    {
        public double Ut { get; }
        public ReferenceFrame Frame => ReferenceFrame.Relative;
        public TrajectorySampleKind Kind => TrajectorySampleKind.Relative;

        /// <summary>Anchor-local offset (metres along the anchor's local x axis).</summary>
        internal double LocalX { get; }
        /// <summary>Anchor-local offset (metres along the anchor's local y axis).</summary>
        internal double LocalY { get; }
        /// <summary>Anchor-local offset (metres along the anchor's local z axis).</summary>
        internal double LocalZ { get; }
        /// <summary>Anchor-local world rotation: <c>Inverse(anchor.rotation) * focusWorldRotation</c>.</summary>
        internal UnityEngine.Quaternion LocalRotation { get; }

        internal RelativeSample(
            double ut, double localX, double localY, double localZ,
            UnityEngine.Quaternion localRotation)
        {
            Ut = ut;
            LocalX = localX;
            LocalY = localY;
            LocalZ = localZ;
            LocalRotation = localRotation;
        }

        public override string ToString()
            => string.Format(
                CultureInfo.InvariantCulture,
                "Rel UT={0:F1} dx={1:F1} dy={2:F1} dz={3:F1}",
                Ut, LocalX, LocalY, LocalZ);
    }

    /// <summary>
    /// design §5.3: an ORBITAL sample — a thin, immutable WRAPPER over the existing
    /// <see cref="OrbitSegment"/> Keplerian currency (kept as the universal orbital representation,
    /// design §13). It exposes the segment as an <see cref="ITrajectorySample"/> so a phase's
    /// geometry can be sampled uniformly; the wrapped <see cref="Segment"/> is the source of truth and
    /// is value-copied (a struct), never mutated.
    /// </summary>
    internal readonly struct OrbitalState : ITrajectorySample
    {
        public double Ut { get; }
        public ReferenceFrame Frame => ReferenceFrame.OrbitalCheckpoint;
        public TrajectorySampleKind Kind => TrajectorySampleKind.Orbital;

        /// <summary>The wrapped recorded / generated orbit (Kepler elements, value-copied).</summary>
        internal OrbitSegment Segment { get; }

        internal OrbitalState(double ut, OrbitSegment segment)
        {
            Ut = ut;
            Segment = segment;
        }

        /// <summary>Wrap a segment at its own <c>startUT</c> (the natural epoch for a segment-as-sample).</summary>
        internal static OrbitalState FromSegment(OrbitSegment segment)
            => new OrbitalState(segment.startUT, segment);

        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "Orb UT={0:F1} [{1}]", Ut, Segment);
    }
}
