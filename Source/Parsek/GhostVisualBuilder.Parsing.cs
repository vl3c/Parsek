using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostVisualBuilder
    {
        internal static bool TryGetSnapshotCenterOfMass(ConfigNode snapshotNode, out Vector3 centerOfMass)
        {
            centerOfMass = Vector3.zero;
            if (snapshotNode == null)
                return false;

            return TryParseVector3(snapshotNode.GetValue("CoM"), out centerOfMass);
        }

        internal static bool TryGetSnapshotRootPartInfo(
            ConfigNode snapshotNode,
            out string partName,
            out uint persistentId,
            out Vector3 localPosition,
            out Quaternion localRotation)
        {
            partName = null;
            persistentId = 0;
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            if (snapshotNode == null)
                return false;

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
                return false;

            int rootIndex = 0;
            string rootIndexRaw = snapshotNode.GetValue("root");
            if (!string.IsNullOrEmpty(rootIndexRaw))
                int.TryParse(rootIndexRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out rootIndex);
            if (rootIndex < 0 || rootIndex >= partNodes.Length)
                rootIndex = 0;

            ConfigNode rootPartNode = partNodes[rootIndex];
            if (rootPartNode == null)
                return false;

            partName = TryExtractPartName(rootPartNode.GetValue("name") ?? rootPartNode.GetValue("part"));
            string persistentIdRaw = rootPartNode.GetValue("persistentId");
            if (!string.IsNullOrEmpty(persistentIdRaw))
                uint.TryParse(persistentIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out persistentId);

            Vector3 parsedPosition;
            if (TryParseVector3(GetPartPositionRaw(rootPartNode), out parsedPosition))
                localPosition = parsedPosition;

            Quaternion parsedRotation;
            if (TryParseQuaternion(GetPartRotationRaw(rootPartNode), out parsedRotation))
                localRotation = parsedRotation;

            return true;
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

        internal static AvailablePart ResolveAvailablePart(string partName)
        {
            if (string.IsNullOrEmpty(partName))
                return null;

            string trimmed = partName.Trim();
            if (trimmed.Length == 0)
                return null;

            AvailablePart ap = TryGetAvailablePartByName(trimmed);
            if (ap != null)
                return ap;

            string dotted = trimmed.Replace('_', '.');
            if (!string.Equals(dotted, trimmed, System.StringComparison.Ordinal))
            {
                ap = TryGetAvailablePartByName(dotted);
                if (ap != null)
                    return ap;
            }

            string underscored = trimmed.Replace('.', '_');
            if (!string.Equals(underscored, trimmed, System.StringComparison.Ordinal))
            {
                ap = TryGetAvailablePartByName(underscored);
                if (ap != null)
                    return ap;
            }

            List<AvailablePart> loadedParts = PartLoader.LoadedPartsList;
            if (loadedParts == null || loadedParts.Count == 0)
                return null;

            for (int i = 0; i < loadedParts.Count; i++)
            {
                AvailablePart candidate = loadedParts[i];
                if (candidate == null || candidate.partPrefab == null)
                    continue;

                if (PartNameMatches(candidate, trimmed) ||
                    PartNameMatches(candidate, dotted) ||
                    PartNameMatches(candidate, underscored))
                {
                    return candidate;
                }
            }

            ParsekLog.Warn("GhostVisual", $"ResolveAvailablePart failed for '{partName}' — all lookups exhausted (direct, dotted, underscored, brute-force)");
            return null;
        }

        private static AvailablePart TryGetAvailablePartByName(string lookupName)
        {
            if (string.IsNullOrEmpty(lookupName))
                return null;

            AvailablePart ap = PartLoader.getPartInfoByName(lookupName);
            return (ap != null && ap.partPrefab != null) ? ap : null;
        }

        private static bool PartNameMatches(AvailablePart candidate, string lookupName)
        {
            if (candidate == null || string.IsNullOrEmpty(lookupName))
                return false;

            if (string.Equals(candidate.name, lookupName, System.StringComparison.OrdinalIgnoreCase))
                return true;

            string infoName = candidate.partPrefab?.partInfo?.name;
            if (!string.IsNullOrEmpty(infoName) &&
                string.Equals(infoName, lookupName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefabName = candidate.partPrefab?.name;
            if (!string.IsNullOrEmpty(prefabName) &&
                string.Equals(prefabName, lookupName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
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

        internal static bool TryParseFxLocalRotation(string value, out Quaternion result)
        {
            result = Quaternion.identity;
            if (string.IsNullOrEmpty(value))
                return false;

            string[] parts = value.Split(',');
            if (parts.Length == 3)
            {
                if (!TryParseVector3(value, out Vector3 euler))
                    return false;
                result = Quaternion.Euler(euler);
                return true;
            }

            if (parts.Length >= 4 &&
                float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ay) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float az) &&
                float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float angle))
            {
                Vector3 axis = new Vector3(ax, ay, az);
                result = axis.sqrMagnitude > 0.0001f
                    ? Quaternion.AngleAxis(angle, axis)
                    : Quaternion.identity;
                return true;
            }

            return false;
        }

        internal static string GetPartPositionRaw(ConfigNode partNode)
        {
            if (partNode == null) return null;
            return partNode.GetValue("pos") ?? partNode.GetValue("position");
        }

        internal static string GetPartRotationRaw(ConfigNode partNode)
        {
            if (partNode == null) return null;
            return partNode.GetValue("rot") ?? partNode.GetValue("rotation");
        }

        internal static Dictionary<uint, List<uint>> BuildPartSubtreeMap(ConfigNode snapshotNode)
        {
            var map = new Dictionary<uint, List<uint>>();
            if (snapshotNode == null) return map;

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0) return map;

            // First pass: collect persistentIds in order (index → pid)
            var pidByIndex = new uint[partNodes.Length];
            for (int i = 0; i < partNodes.Length; i++)
            {
                string pidStr = partNodes[i].GetValue("persistentId");
                if (pidStr != null)
                    uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pidByIndex[i]);
            }

            // Second pass: build parent → children map
            for (int i = 0; i < partNodes.Length; i++)
            {
                string parentStr = partNodes[i].GetValue("parent");
                if (parentStr == null) continue;

                int parentIdx;
                if (!int.TryParse(parentStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out parentIdx))
                    continue;
                if (parentIdx < 0 || parentIdx >= partNodes.Length) continue;
                if (parentIdx == i) continue; // root part references itself

                uint parentPid = pidByIndex[parentIdx];
                uint childPid = pidByIndex[i];
                if (parentPid == 0 || childPid == 0) continue;

                List<uint> children;
                if (!map.TryGetValue(parentPid, out children))
                {
                    children = new List<uint>();
                    map[parentPid] = children;
                }
                children.Add(childPid);
            }

            return map;
        }

        internal static HashSet<uint> BuildSnapshotPartIdSet(ConfigNode snapshotNode)
        {
            var ids = new HashSet<uint>();
            if (snapshotNode == null) return ids;

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0) return ids;

            for (int i = 0; i < partNodes.Length; i++)
            {
                string pidStr = partNodes[i].GetValue("persistentId");
                uint persistentId;
                if (pidStr != null &&
                    uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out persistentId) &&
                    persistentId != 0)
                    ids.Add(persistentId);
            }

            return ids;
        }

        private static ConfigNode FindModuleNode(ConfigNode partNode, string moduleName)
        {
            if (partNode == null || string.IsNullOrEmpty(moduleName))
                return null;
            var modules = partNode.GetNodes("MODULE");
            if (modules == null) return null;
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") == moduleName)
                    return modules[i];
            }
            return null;
        }

        /// <summary>
        /// Extracts damagedTransformName values from all ModuleWheelDamage MODULE nodes
        /// in the given part config. Returns an empty set if no damage modules exist or
        /// if none specify a damagedTransformName.
        /// </summary>
        internal static HashSet<string> GetDamagedWheelTransformNames(ConfigNode partConfig)
        {
            var names = new HashSet<string>();
            if (partConfig == null) return names;

            var modules = partConfig.GetNodes("MODULE");
            if (modules == null) return names;

            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") != "ModuleWheelDamage")
                    continue;
                string damagedName = modules[i].GetValue("damagedTransformName");
                if (!string.IsNullOrEmpty(damagedName))
                    names.Add(damagedName);
            }
            return names;
        }

        /// <summary>
        /// Extracts maskTransform values from depth-mask MODULE nodes (ReStock's
        /// ModuleRestockDepthMask and any module whose name contains "DepthMask",
        /// the common community pattern). Depth-mask meshes write depth only at
        /// render queue ~1999 under the live plugin's queue management; cloned onto
        /// a ghost they occlude everything behind them, punching a see-through hole
        /// to the sky/ocean. Empty on stock installs (no such modules).
        /// </summary>
        internal static HashSet<string> GetDepthMaskTransformNames(ConfigNode partConfig)
        {
            var names = new HashSet<string>();
            if (partConfig == null) return names;

            var modules = partConfig.GetNodes("MODULE");
            if (modules == null) return names;

            for (int i = 0; i < modules.Length; i++)
            {
                string moduleName = modules[i].GetValue("name");
                if (string.IsNullOrEmpty(moduleName) ||
                    moduleName.IndexOf("DepthMask", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                string maskName = modules[i].GetValue("maskTransform");
                if (!string.IsNullOrEmpty(maskName))
                    names.Add(maskName);
            }
            return names;
        }

        /// <summary>
        /// True when a shader name identifies a depth-mask shader (writes depth only).
        /// Second detection layer beside the config transform names: catches mask
        /// meshes whose material already carries the depth shader on the prefab.
        /// </summary>
        internal static bool IsDepthMaskShaderName(string shaderName)
        {
            return !string.IsNullOrEmpty(shaderName) &&
                shaderName.IndexOf("DepthMask", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Depth-mask renderer detection for the ghost clone loop: the renderer sits on
        /// (or under) a config-named mask transform, or its material uses a depth-mask
        /// shader. The shader branch is only consulted when the PART carries a
        /// DepthMask module (depthMaskNames non-empty): pre-feature ghosts have always
        /// cloned any natively depth-mask-shaded stock meshes without complaint, so
        /// skipping must never engage on module-less parts (stock-safe by
        /// construction, not just by the current install's part set; review M1).
        /// </summary>
        internal static bool IsDepthMaskRenderer(Renderer renderer, HashSet<string> depthMaskNames)
        {
            if (renderer == null || depthMaskNames == null || depthMaskNames.Count == 0)
                return false;
            if (IsRendererOnDamagedTransform(renderer.transform, depthMaskNames))
                return true;

            Material mat = renderer.sharedMaterial;
            return mat != null && mat.shader != null && IsDepthMaskShaderName(mat.shader.name);
        }

        /// <summary>
        /// Checks whether a renderer's transform (or any of its ancestors up to but not
        /// including the hierarchy root) has a name matching one of the damaged wheel
        /// transform names. The damaged mesh may be a child of the named transform.
        /// </summary>
        internal static bool IsRendererOnDamagedTransform(Transform rendererTransform, HashSet<string> damagedNames)
        {
            if (rendererTransform == null || damagedNames == null || damagedNames.Count == 0)
                return false;

            Transform cur = rendererTransform;
            while (cur != null)
            {
                if (damagedNames.Contains(cur.name))
                    return true;
                cur = cur.parent;
            }
            return false;
        }

        private static string FirstNonEmptyConfigValue(ConfigNode node, params string[] keys)
        {
            if (node == null || keys == null || keys.Length == 0)
                return null;

            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                if (string.IsNullOrEmpty(key))
                    continue;

                string raw = node.GetValue(key);
                if (string.IsNullOrEmpty(raw))
                    continue;

                string trimmed = raw.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    return trimmed;
            }

            return null;
        }

        private static bool TryParseLooseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(raw))
                return false;

            string t = raw.Trim();
            if (bool.TryParse(t, out value))
                return true;

            if (string.Equals(t, "1", System.StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            if (string.Equals(t, "0", System.StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            return false;
        }

        internal static bool TryParseKspColor(string raw, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(raw))
                return false;

            string trimmed = raw.Trim();

            if (trimmed.StartsWith("#", System.StringComparison.Ordinal))
                return ColorUtility.TryParseHtmlString(trimmed, out color);

            string[] parts = trimmed.Split(',');
            if (parts.Length < 3)
                return false;

            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
                return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g))
                return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                return false;

            float a = 1f;
            if (parts.Length >= 4)
                float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a);

            color = new Color(r, g, b, a);
            return true;
        }

        /// <summary>
        /// For multi-MODEL parts, transform names include the full GameDatabase path
        /// (e.g. "SquadExpansion/MakingHistory/Parts/SharedAssets/Shroud3x0(Clone)").
        /// Extracts the short name ("Shroud3x0") by stripping the path prefix and "(Clone)" suffix.
        /// Returns null if the name doesn't contain a path separator (already short).
        /// </summary>
        internal static string ExtractShortTransformName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            // Only apply to path-like names (contain '/')
            int lastSlash = fullName.LastIndexOf('/');
            if (lastSlash < 0)
                return null;

            string segment = fullName.Substring(lastSlash + 1);

            // Strip "(Clone)" suffix if present
            if (segment.EndsWith("(Clone)"))
                segment = segment.Substring(0, segment.Length - 7);

            return segment.Length > 0 ? segment : null;
        }
    }
}
