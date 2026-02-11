using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    internal static class GhostVisualBuilder
    {
        private static readonly Regex trailingNumericSuffixRegex =
            new Regex(@"^(.*)_\d+$", RegexOptions.Compiled);

        internal static GameObject BuildTimelineGhostFromSnapshot(RecordingStore.Recording rec, string rootName)
        {
            if (rec == null || rec.VesselSnapshot == null)
                return null;

            var partNodes = rec.VesselSnapshot.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
                return null;

            GameObject root = new GameObject(rootName);
            bool addedAnyVisual = false;

            for (int i = 0; i < partNodes.Length; i++)
            {
                ConfigNode partNode = partNodes[i];
                string rawPart = partNode.GetValue("part");
                string partName = TryExtractPartName(rawPart);
                if (string.IsNullOrEmpty(partName))
                    continue;

                AvailablePart ap = PartLoader.getPartInfoByName(partName);
                if (ap == null || ap.partPrefab == null)
                    continue;

                bool partVisualAdded = AddPartVisuals(root.transform, partNode, ap.partPrefab);
                addedAnyVisual = addedAnyVisual || partVisualAdded;
            }

            if (!addedAnyVisual)
            {
                Object.Destroy(root);
                return null;
            }

            return root;
        }

        internal static string TryExtractPartName(string rawPart)
        {
            if (string.IsNullOrEmpty(rawPart))
                return null;

            // Snapshot format often uses "partName_123456789". Keep the full
            // name unless there is a trailing numeric suffix.
            var match = trailingNumericSuffixRegex.Match(rawPart);
            return match.Success ? match.Groups[1].Value : rawPart;
        }

        internal static bool TryParseVector3(string value, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrEmpty(value))
                return false;

            string[] parts = value.Split(',');
            if (parts.Length != 3)
                return false;

            float x, y, z;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;

            result = new Vector3(x, y, z);
            return true;
        }

        internal static bool TryParseQuaternion(string value, out Quaternion result)
        {
            result = Quaternion.identity;
            if (string.IsNullOrEmpty(value))
                return false;

            string[] parts = value.Split(',');
            if (parts.Length != 4)
                return false;

            float x, y, z, w;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            if (!float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return false;

            result = new Quaternion(x, y, z, w);
            return true;
        }

        private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab)
        {
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            // Known limitation for v1: parts that only use SkinnedMeshRenderer
            // (e.g. EVA models) are not reconstructed here and will fallback.
            if (renderers == null || renderers.Length == 0)
                return false;

            GameObject partRoot = new GameObject($"ghost_part_{prefab.partInfo?.name ?? "unknown"}");
            partRoot.transform.SetParent(root, false);

            Vector3 localPos;
            if (TryParseVector3(partNode.GetValue("pos"), out localPos))
                partRoot.transform.localPosition = localPos;

            Quaternion localRot;
            if (TryParseQuaternion(partNode.GetValue("rot"), out localRot))
                partRoot.transform.localRotation = localRot;

            bool added = false;
            Transform prefabRoot = prefab.transform;
            for (int r = 0; r < renderers.Length; r++)
            {
                var mr = renderers[r];
                if (mr == null) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                GameObject child = new GameObject(mr.gameObject.name);
                child.transform.SetParent(partRoot.transform, false);
                child.transform.localPosition = prefabRoot.InverseTransformPoint(mr.transform.position);
                child.transform.localRotation = Quaternion.Inverse(prefabRoot.rotation) * mr.transform.rotation;
                child.transform.localScale = mr.transform.localScale;

                var childMf = child.AddComponent<MeshFilter>();
                childMf.sharedMesh = mf.sharedMesh;
                var childMr = child.AddComponent<MeshRenderer>();
                childMr.sharedMaterials = mr.sharedMaterials;
                added = true;
            }

            if (!added)
            {
                Object.Destroy(partRoot);
                return false;
            }

            return true;
        }
    }
}
