using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The pure framing and view geometry of the FX capture (design-part-ghost-parity.md E.3,
    /// E.4), all in PART-LOCAL metres and unit vectors as double[3]: the FX boxes (near, bottom,
    /// far) stretched along the exhaust axis, the camera direction and up of each FX view around
    /// that axis, and the projections a tile pixel needs. Plain doubles, so xUnit drives every
    /// one; the live half that turns them into a camera is <see cref="PartParityFxDriver"/>.
    /// </summary>
    internal static class PartParityFxFraming
    {
        /// <summary>The FX views' ortho margin over the projected half extents (the E0 spike's
        /// 1.08, so a plume's soft edge is not cut).</summary>
        internal const double OrthoMargin = 1.08;

        /// <summary>The smallest half extent an FX view frames (metres), so a point-like part
        /// still has a scale.</summary>
        internal const double MinHalfExtentM = 0.05;

        // ----- vectors -----

        internal static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        internal static double[] Cross(double[] a, double[] b)
            => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        internal static double Length(double[] a) => Math.Sqrt(Dot(a, a));

        /// <summary>Unit vector of <paramref name="a"/>; null when it is zero or not finite.</summary>
        internal static double[] Unit(double[] a)
        {
            if (a == null || a.Length != 3) return null;
            double l = Length(a);
            if (!(l > 1e-9) || double.IsInfinity(l)) return null;
            return new[] { a[0] / l, a[1] / l, a[2] / l };
        }

        private static double[] Add(double[] a, double[] b, double scale)
            => new[] { a[0] + b[0] * scale, a[1] + b[1] * scale, a[2] + b[2] * scale };

        // ----- view frames -----

        /// <summary>
        /// The camera direction (from the box toward the camera) and the image up of one FX
        /// view, both in part-local axes, for a part whose exhaust runs along
        /// <paramref name="exhaust"/> (downstream). A side view looks ACROSS the axis, up
        /// against the flow, so a plume points down the image whichever way the nozzle faces;
        /// <c>fx-side-a</c> is the stowed capture's side-a direction (part -Z) turned to be
        /// perpendicular to the axis (part +X when the axis runs along part Z), <c>fx-side-b</c>
        /// its quarter turn and <c>fx-far</c> <c>fx-side-a</c>'s; <c>fx-bottom</c> looks straight
        /// up the flow. False for an unknown view or a zero axis.
        /// </summary>
        internal static bool TryViewFrame(string view, double[] exhaust, out double[] dir, out double[] up)
        {
            dir = null;
            up = null;
            double[] u = Unit(exhaust);
            if (u == null) return false;
            double[] a = SideDirection(u);
            double[] against = { -u[0], -u[1], -u[2] };
            switch (view)
            {
                case PartParityFxRules.ViewFxSideA:
                case PartParityFxRules.ViewFxFar:
                    dir = a;
                    up = against;
                    return true;
                case PartParityFxRules.ViewFxSideB:
                    dir = Cross(a, against);
                    up = against;
                    return true;
                case PartParityFxRules.ViewFxBottom:
                    dir = u;
                    up = a;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The part -Z direction made perpendicular to the axis; part +X (made
        /// perpendicular) when the axis runs along Z.</summary>
        internal static double[] SideDirection(double[] u)
        {
            foreach (double[] seed in new[] { new[] { 0.0, 0.0, -1.0 }, new[] { 1.0, 0.0, 0.0 } })
            {
                double d = Dot(seed, u);
                if (Math.Abs(d) > 0.9) continue;
                return Unit(Add(seed, u, -d));
            }
            return new[] { 1.0, 0.0, 0.0 };
        }

        // ----- boxes -----

        /// <summary>The length of a box along a unit axis: its projected size.</summary>
        internal static double AlongSize(PartParityAabb box, double[] u)
        {
            double[] s = box.Size();
            return Math.Abs(u[0]) * s[0] + Math.Abs(u[1]) * s[1] + Math.Abs(u[2]) * s[2];
        }

        /// <summary>The box's larger size across the axis, over the two perpendicular view
        /// directions (the width a camera facing it sees).</summary>
        internal static double AcrossSize(PartParityAabb box, double[] u)
        {
            double[] a = SideDirection(u);
            return Math.Max(AlongSize(box, a), AlongSize(box, Cross(a, u)));
        }

        /// <summary>The box grown to hold itself moved <paramref name="metres"/> along the axis.</summary>
        internal static PartParityAabb Stretched(PartParityAabb box, double[] u, double metres)
        {
            PartParityAabb r = box.Copy();
            r.Encapsulate(Moved(box, u, metres));
            return r;
        }

        /// <summary>The near box (E.3): the body box stretched along the exhaust axis to
        /// <see cref="PartParityFxRules.FramingBodyLengths"/> body lengths.</summary>
        internal static PartParityAabb NearBox(PartParityAabb body, double[] u)
            => Stretched(body, u, AlongSize(body, u) * (PartParityFxRules.FramingBodyLengths - 1.0));

        /// <summary>The bottom box: the near box grown to
        /// <see cref="PartParityFxRules.FramingBottomWiden"/> times its width across the axis
        /// (the flame's cross-section is wider than the body), its length unchanged.</summary>
        internal static PartParityAabb BottomBox(PartParityAabb near, double[] u)
        {
            double extra = (PartParityFxRules.FramingBottomWiden - 1.0) * 0.5 * AcrossSize(near, u);
            double[] a = SideDirection(u);
            double[] b = Cross(a, u);
            PartParityAabb r = near.Copy();
            r.Encapsulate(Moved(near, a, extra));
            r.Encapsulate(Moved(near, a, -extra));
            r.Encapsulate(Moved(near, b, extra));
            r.Encapsulate(Moved(near, b, -extra));
            return r;
        }

        private static PartParityAabb Moved(PartParityAabb box, double[] u, double metres)
            => PartParityAabb.FromMinMax(Add(box.Min, u, metres), Add(box.Max, u, metres));

        /// <summary>
        /// The far box (E.3): the body box stretched along the axis to where the 95th percentile
        /// of the near particles sits (<paramref name="alongM"/> from the nozzle point), clamped
        /// at <see cref="PartParityFxRules.FramingClampM"/> (<paramref name="clamped"/>), never
        /// shorter than the near box, and widened across it to at least the body's width and
        /// <paramref name="acrossM"/> on each side.
        /// </summary>
        internal static PartParityAabb FarBox(PartParityAabb body, double[] nozzle, double[] u,
            double? alongM, double? acrossM, out bool clamped)
        {
            clamped = false;
            double reach = alongM.HasValue && !double.IsNaN(alongM.Value) ? alongM.Value : 0.0;
            if (reach > PartParityFxRules.FramingClampM)
            {
                reach = PartParityFxRules.FramingClampM;
                clamped = true;
            }
            reach = Math.Max(reach, AlongSize(body, u) * (PartParityFxRules.FramingBodyLengths - 1.0));
            double across = Math.Max(acrossM.HasValue && !double.IsNaN(acrossM.Value) ? acrossM.Value : 0.0,
                AcrossSize(body, u) * 0.5);
            double[] a = SideDirection(u);
            double[] b = Cross(a, u);
            PartParityAabb r = body.Copy();
            foreach (double[] at in new[] { nozzle, Add(nozzle, u, reach) })
                foreach (double sa in new[] { across, -across })
                    foreach (double sb in new[] { across, -across })
                    {
                        double[] p = Add(Add(at, a, sa), b, sb);
                        r.Encapsulate(PartParityAabb.FromMinMax(p, p));
                    }
            return r;
        }

        /// <summary>The nozzle diameter of an FX case, metres: the spread of the thrust points
        /// across the axis, at least half the body's width across it (a one-nozzle engine has no
        /// spread). It is the unit of the cross-section offset (E.4).</summary>
        internal static double NozzleDiameterM(IList<double[]> points, double[] nozzle, double[] u, PartParityAabb body)
        {
            double spread = 0.0;
            if (points != null)
                foreach (double[] p in points)
                {
                    double[] d = { p[0] - nozzle[0], p[1] - nozzle[1], p[2] - nozzle[2] };
                    double t = Dot(d, u);
                    spread = Math.Max(spread, 2.0 * Math.Sqrt(Math.Max(0.0, Dot(d, d) - t * t)));
                }
            return Math.Max(spread, 0.5 * AcrossSize(body, u));
        }

        // ----- camera -----

        /// <summary>The half width, half height and half depth a camera facing along
        /// <paramref name="dir"/> (from the box toward the camera, image up
        /// <paramref name="up"/>) sees of the box: each axis's projected half extent.</summary>
        internal static void ProjectBox(PartParityAabb box, double[] dir, double[] up,
            out double halfWidth, out double halfHeight, out double halfDepth)
        {
            double[][] axes = { Cross(up, dir), up, dir };
            double[] e = box.Extents();
            var half = new double[3];
            for (int k = 0; k < 3; k++)
                half[k] = Math.Abs(axes[k][0]) * e[0] + Math.Abs(axes[k][1]) * e[1] + Math.Abs(axes[k][2]) * e[2];
            halfWidth = half[0];
            halfHeight = half[1];
            halfDepth = half[2];
        }

        /// <summary>The ortho size (half the image height) that frames the box with the margin.</summary>
        internal static double OrthoSize(double halfWidth, double halfHeight)
            => Math.Max(MinHalfExtentM, Math.Max(halfWidth, halfHeight)) * OrthoMargin;

        /// <summary>The tile pixel [x, y] (y down) of a point given by its camera-space right and
        /// up offsets from the image centre, metres.</summary>
        internal static double[] ToTilePx(double right, double up, double orthoSize, int tile)
            => new[] { (right / (2.0 * orthoSize) + 0.5) * tile, (0.5 - up / (2.0 * orthoSize)) * tile };

        /// <summary>The axis block of one FX view (E.4): the projected nozzle point (camera-space
        /// right / up offsets), the projected exhaust direction (downstream) and the nozzle
        /// diameter in tile pixels. The axis view gets no direction: the axis projects to a
        /// point.</summary>
        internal static PartParityFxAxis AxisFor(string view, double[] nozzleInCam, double[] axisInCam,
            double orthoSize, int tile, double nozzleDiameterM)
        {
            var a = new PartParityFxAxis
            {
                OriginPx = ToTilePx(nozzleInCam[0], nozzleInCam[1], orthoSize, tile),
                NozzleDiameterPx = nozzleDiameterM > 0.0 ? (double?)(nozzleDiameterM * tile / (2.0 * orthoSize)) : null,
            };
            if (PartParityFxRules.IsAxisView(view)) return a;
            double x = axisInCam[0] * tile / (2.0 * orthoSize), y = -axisInCam[1] * tile / (2.0 * orthoSize);
            if (Math.Sqrt(x * x + y * y) > 1e-9) a.DirPx = new[] { x, y };
            return a;
        }
    }
}
