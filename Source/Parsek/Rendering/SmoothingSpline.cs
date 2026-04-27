namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 1 smoothing spline (design doc §17.3.1, §6.1). Pure POCO — math
    /// lives in <see cref="Parsek.TrajectoryMath.CatmullRomFit"/>. Fields are
    /// laid out to match the <c>SmoothingSplineList</c> entry in the
    /// <c>.pann</c> binary format so write / read can iterate in order.
    ///
    /// <para>
    /// In Phase 1 the spline holds body-fixed lat / lon / alt control values
    /// (degrees, degrees, metres) for ABSOLUTE-frame body-fixed segments.
    /// <see cref="FrameTag"/> reserves byte 0 for body-fixed; Phase 4 will
    /// introduce the inertial variant under tag 1.
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

        /// <summary>X-axis controls (latitude in degrees for body-fixed Phase 1).</summary>
        public float[] ControlsX;

        /// <summary>Y-axis controls (longitude in degrees for body-fixed Phase 1).</summary>
        public float[] ControlsY;

        /// <summary>Z-axis controls (altitude in metres for body-fixed Phase 1).</summary>
        public float[] ControlsZ;

        /// <summary>0 = body-fixed (Phase 1 always); 1 = inertial (reserved, Phase 4).</summary>
        public byte FrameTag;

        /// <summary>True when the spline is populated and safe to evaluate.</summary>
        public bool IsValid;
    }
}
