using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static class DiagnosticFormatters
    {
        internal static string FormatVector3(Vector3 value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F3},{1:F3},{2:F3})",
                value.x,
                value.y,
                value.z);
        }

        internal static string FormatVector3d(Vector3d value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F3},{1:F3},{2:F3})",
                value.x,
                value.y,
                value.z);
        }

        internal static string FormatQuaternion(Quaternion value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F3},{1:F3},{2:F3},{3:F3})",
                value.x,
                value.y,
                value.z,
                value.w);
        }

        internal static string DescribeSurfaceAttachNode(Part part)
        {
            AttachNode node = part?.srfAttachNode;
            if (node == null)
                return "none";

            string world = part.transform != null
                ? FormatVector3d(part.transform.TransformPoint(node.position))
                : "no-transform";
            uint attachedPid = node.attachedPart?.persistentId ?? 0u;
            return string.Format(
                CultureInfo.InvariantCulture,
                "local={0} world={1} orient={2} attachedPid={3}",
                FormatVector3(node.position),
                world,
                FormatVector3(node.orientation),
                attachedPid);
        }
    }
}
