using System;
using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §6.1: the PURE classification + continuity decisions for <see cref="PhaseSeam"/>.
    /// Maps a pair of adjacent phase kinds to the seam kind the model demands, and provides the G1
    /// tangent-match math the descent re-stitch (Phase 6) enforces. Unity-vector math only (no ECalls /
    /// no KSP API), so it is directly unit-testable.
    ///
    /// <para>NOT wired in Phase 1 — the cross-member descent seam builder (Phase 6) is the first
    /// consumer of <see cref="IsRigidSeamTangentDiscontinuity"/>.</para>
    /// </summary>
    internal static class PhaseSeamClassifier
    {
        /// <summary>Grep-stable lowercase token for a <see cref="PhaseSeamKind"/>.</summary>
        internal static string KindToken(PhaseSeamKind kind)
        {
            switch (kind)
            {
                case PhaseSeamKind.Rigid: return "rigid";
                case PhaseSeamKind.FlexibleSoi: return "flexible-soi";
                case PhaseSeamKind.SwitchContinuation: return "switch-continuation";
                default: return "none";
            }
        }

        /// <summary>
        /// design §6.1: classify the seam KIND between two adjacent phases by their kinds and bodies.
        ///
        /// <list type="bullet">
        ///   <item>A body change (<paramref name="leadingBody"/> != <paramref name="trailingBody"/>) is
        ///     a <see cref="PhaseSeamKind.FlexibleSoi"/> SOI boundary (G0) — design §10.</item>
        ///   <item>An ascent↔orbit or orbit↔landing join WITHIN one body is
        ///     <see cref="PhaseSeamKind.Rigid"/> (G1) — design §6.1.</item>
        ///   <item>A <paramref name="isMemberSwitchBoundary"/> handoff is
        ///     <see cref="PhaseSeamKind.SwitchContinuation"/> (G0) — design §6.1.</item>
        ///   <item>Everything else is <see cref="PhaseSeamKind.None"/> (intra-phase contiguity / no
        ///     distinguished seam).</item>
        /// </list>
        /// A body change wins over the rigid classification: an SOI crossing is never a G1 rigid seam.
        /// </summary>
        internal static PhaseSeamKind Classify(
            PhaseKind leadingKind,
            PhaseKind trailingKind,
            string leadingBody,
            string trailingBody,
            bool isMemberSwitchBoundary)
        {
            if (isMemberSwitchBoundary)
                return PhaseSeamKind.SwitchContinuation;

            if (!string.Equals(leadingBody, trailingBody, StringComparison.Ordinal))
                return PhaseSeamKind.FlexibleSoi;

            if (IsRigidJoin(leadingKind, trailingKind))
                return PhaseSeamKind.Rigid;

            return PhaseSeamKind.None;
        }

        /// <summary>
        /// The within-body G1 joins (design §6.1): ascent↔orbit and orbit↔landing (the descent
        /// re-stitch). Symmetric.
        /// </summary>
        internal static bool IsRigidJoin(PhaseKind a, PhaseKind b)
        {
            return IsAscentOrbitJoin(a, b) || IsAscentOrbitJoin(b, a)
                   || IsOrbitLandingJoin(a, b) || IsOrbitLandingJoin(b, a);
        }

        private static bool IsAscentOrbitJoin(PhaseKind leading, PhaseKind trailing)
            => leading == PhaseKind.Ascent && IsOrbitalPhase(trailing);

        private static bool IsOrbitLandingJoin(PhaseKind leading, PhaseKind trailing)
            => IsOrbitalPhase(leading) && trailing == PhaseKind.Descent;

        /// <summary>An "orbital" phase a rigid seam can join to (loiter / transfer / arrival around a body).</summary>
        private static bool IsOrbitalPhase(PhaseKind kind)
        {
            switch (kind)
            {
                case PhaseKind.DepartureLoiter:
                case PhaseKind.SoiDeparture:
                case PhaseKind.HeliocentricTransfer:
                case PhaseKind.SoiArrival:
                case PhaseKind.ArrivalLoiter:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// design §6.1 / §9.1: default tolerance (radians) for the G1 tangent match. ~5.7° — a generous
        /// bound separating a continuous orbit↔landing handoff from a real discontinuity (the dossier's
        /// reconciler uses ~1° for icon-off-orbit; a stitched capture-orbit tangent vs a recorded-descent
        /// first-sample tangent is noisier).
        /// </summary>
        internal const double DefaultTangentToleranceRadians = 0.1;

        /// <summary>
        /// design §6.1 / §9.1: the <c>rigid-seam-tangent-discontinuity</c> predicate. Given the leaving
        /// phase's terminal velocity direction and the entering phase's first-sample velocity direction,
        /// return true iff the angle between them exceeds <paramref name="toleranceRadians"/>.
        ///
        /// <para>NaN/Inf-safe and degenerate-safe: a zero-length or non-finite tangent on either side
        /// yields NO anomaly (false) — an unmeasurable tangent is not a discontinuity (mirrors the
        /// oracle's "no false anomaly" contract). A non-finite / negative tolerance is treated as
        /// <see cref="DefaultTangentToleranceRadians"/>.</para>
        /// </summary>
        internal static bool IsRigidSeamTangentDiscontinuity(
            Vector3 leavingTangent,
            Vector3 enteringTangent,
            double toleranceRadians = DefaultTangentToleranceRadians)
        {
            double tol = (double.IsNaN(toleranceRadians) || double.IsInfinity(toleranceRadians) || toleranceRadians < 0.0)
                ? DefaultTangentToleranceRadians
                : toleranceRadians;

            if (!TryNormalize(leavingTangent, out Vector3 a) || !TryNormalize(enteringTangent, out Vector3 b))
                return false; // unmeasurable -> no anomaly

            float dot = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
            double angle = Math.Acos(dot);
            if (double.IsNaN(angle) || double.IsInfinity(angle))
                return false;
            return angle > tol;
        }

        private static bool TryNormalize(Vector3 v, out Vector3 normalized)
        {
            normalized = Vector3.zero;
            if (!IsFinite(v.x) || !IsFinite(v.y) || !IsFinite(v.z))
                return false;
            float mag = v.magnitude;
            if (mag <= 1e-9f || float.IsNaN(mag) || float.IsInfinity(mag))
                return false;
            normalized = v / mag;
            return true;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
