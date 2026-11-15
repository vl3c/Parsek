using System;
using System.Collections.Generic;
using Parsek.Rendering;
using UnityEngine;

namespace Parsek
{
    public static partial class TrajectoryMath
    {
        /// <summary>
        /// Computes the anchor-local offset used by RELATIVE sections.
        /// Pure static method for testability.
        /// </summary>
        internal static Vector3d ComputeRelativeLocalOffset(
            Vector3d focusedPosition,
            Vector3d anchorPosition,
            Quaternion anchorWorldRotation)
        {
            Vector3 worldOffset = (Vector3)(focusedPosition - anchorPosition);
            Quaternion inverseAnchor = PureInverse(PureNormalize(anchorWorldRotation));
            Vector3 localOffset = PureRotateVector(inverseAnchor, worldOffset);
            return new Vector3d(localOffset.x, localOffset.y, localOffset.z);
        }

        /// <summary>
        /// Computes world position from anchor position and a format-v6 anchor-local
        /// offset. Pure static for testability.
        /// </summary>
        internal static Vector3d ApplyRelativeLocalOffset(
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRotation,
            double dx,
            double dy,
            double dz)
        {
            Vector3 localOffset = new Vector3((float)dx, (float)dy, (float)dz);
            Vector3 worldOffset = PureRotateVector(
                PureNormalize(anchorWorldRotation),
                localOffset);
            return anchorWorldPos + (Vector3d)worldOffset;
        }

        /// <summary>
        /// Computes the anchor-local rotation used by format-v6 RELATIVE sections.
        /// Pure static method for testability.
        /// </summary>
        internal static Quaternion ComputeRelativeLocalRotation(
            Quaternion focusWorldRotation,
            Quaternion anchorWorldRotation)
        {
            Quaternion anchorInverse = PureInverse(PureNormalize(anchorWorldRotation));
            return SanitizeQuaternion(PureMultiply(anchorInverse, focusWorldRotation));
        }

        /// <summary>
        /// Reconstructs world rotation from a format-v6 anchor-local RELATIVE rotation.
        /// Pure static for testability.
        /// </summary>
        internal static Quaternion ApplyRelativeLocalRotation(
            Quaternion anchorWorldRotation,
            Quaternion relativeLocalRotation)
        {
            return SanitizeQuaternion(
                PureMultiply(PureNormalize(anchorWorldRotation), relativeLocalRotation));
        }

        /// <summary>
        /// Resolves a RELATIVE-frame anchor-local position offset to world space.
        /// </summary>
        internal static Vector3d ResolveRelativePlaybackPosition(
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRotation,
            double dx,
            double dy,
            double dz)
        {
            return ApplyRelativeLocalOffset(anchorWorldPos, anchorWorldRotation, dx, dy, dz);
        }

        /// <summary>
        /// Resolves a RELATIVE-frame rotation to world space. RELATIVE sections store the
        /// anchor-local rotation <c>Inverse(anchor) * focus</c>, and this resolver
        /// reconstitutes the focus world rotation with <c>anchor * stored</c>.
        /// </summary>
        internal static Quaternion ResolveRelativePlaybackRotation(
            Quaternion anchorWorldRotation,
            Quaternion storedRelativeRotation)
        {
            return ApplyRelativeLocalRotation(
                anchorWorldRotation,
                SanitizeQuaternion(storedRelativeRotation));
        }
    }
}
