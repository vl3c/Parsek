namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 1 smoothing spline (design doc §17.3.1, §6.1). Pure POCO — math
    /// lives in <see cref="Parsek.TrajectoryMath.CatmullRomFit"/>. Fields are
    /// laid out to match the <c>SmoothingSplineList</c> entry in the
    /// <c>.pann</c> binary format so write / read can iterate in order.
    ///
    /// <para>
    /// Spline controls hold per-section lat / lon / alt values. The exact
    /// frame those values are in is pinned by <see cref="FrameTag"/> — see
    /// the field's documentation for the per-tag contract (design doc §6.2).
    /// </para>
    /// </summary>
    internal struct SmoothingSpline
    {
        /// <summary>Spline family. 0 = Catmull-Rom (Phase 1 only).</summary>
        public byte SplineType;

        /// <summary>Tension parameter for Catmull-Rom (0.5 = canonical).</summary>
        public float Tension;

        /// <summary>Knot UTs in monotonically increasing order.</summary>
        public double[] KnotsUT;

        /// <summary>X-axis controls (latitude in degrees, both frames).</summary>
        public float[] ControlsX;

        /// <summary>Y-axis controls (longitude in degrees, frame depends on <see cref="FrameTag"/>).</summary>
        public float[] ControlsY;

        /// <summary>Z-axis controls (altitude in metres, both frames).</summary>
        public float[] ControlsZ;

        /// <summary>
        /// Coordinate frame for ControlsX/Y/Z values:
        ///   0 = body-fixed (latitude deg / longitude deg / altitude m). Used for Atmospheric, Surface*.
        ///   1 = inertial-longitude (latitude deg / inertialLongitude deg / altitude m). Used for ExoPropulsive, ExoBallistic.
        ///       inertialLongitude = bodyFixedLongitude + RotationAngleAtUT(body, recordingUT) [wrapped to (-180,180]]
        ///       Re-lower at playback UT via TrajectoryMath.FrameTransform.LowerFromInertialToWorld.
        /// </summary>
        public byte FrameTag;

        /// <summary>True when the spline is populated and safe to evaluate.</summary>
        public bool IsValid;
    }
}
