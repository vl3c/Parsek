using System.Collections.Generic;
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
            ConfigNode snapshotNode = GetGhostSnapshot(rec);
            if (snapshotNode == null)
                return null;

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
                return null;

            GameObject root = new GameObject(rootName);
            bool addedAnyVisual = false;
            int visualCount = 0;
            int skippedName = 0;
            int skippedPrefab = 0;
            int skippedMesh = 0;

            for (int i = 0; i < partNodes.Length; i++)
            {
                ConfigNode partNode = partNodes[i];
                // Proto snapshots use "name" for part ID in PART nodes.
                // Some synthetic builders may also emit "part"; support both.
                string rawPart = partNode.GetValue("name") ?? partNode.GetValue("part");
                string partName = TryExtractPartName(rawPart);
                if (string.IsNullOrEmpty(partName))
                {
                    skippedName++;
                    continue;
                }

                // Read persistentId for ghost part naming (enables O(1) lookup during playback)
                string pidStr = partNode.GetValue("persistentId");
                uint persistentId = 0;
                if (pidStr != null)
                    uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out persistentId);

                AvailablePart ap = PartLoader.getPartInfoByName(partName);
                if (ap == null || ap.partPrefab == null)
                {
                    skippedPrefab++;
                    continue;
                }

                int meshCount = 0;
                bool partVisualAdded = AddPartVisuals(root.transform, partNode, ap.partPrefab, persistentId, partName, out meshCount);
                if (partVisualAdded)
                    visualCount++;
                else
                    skippedMesh++;
                addedAnyVisual = addedAnyVisual || partVisualAdded;
            }

            ParsekLog.Log($"Ghost built: {visualCount}/{partNodes.Length} parts with visuals" +
                (skippedName > 0 ? $", {skippedName} bad name" : "") +
                (skippedPrefab > 0 ? $", {skippedPrefab} no prefab" : "") +
                (skippedMesh > 0 ? $", {skippedMesh} no mesh" : ""));

            if (!addedAnyVisual)
            {
                Object.Destroy(root);
                return null;
            }

            return root;
        }

        internal static ConfigNode GetGhostSnapshot(RecordingStore.Recording rec)
        {
            if (rec == null) return null;
            return rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
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

        /// <summary>
        /// Find the visual model root transform for a part prefab.
        /// KSP stores visual content under a "model" child. KerbalEVA uses "model01".
        /// Falls back to the prefab root if neither exists.
        /// </summary>
        private static Transform FindModelRoot(Part prefab)
        {
            Transform modelRoot = prefab.transform.Find("model");
            if (modelRoot != null) return modelRoot;
            modelRoot = prefab.transform.Find("model01");
            if (modelRoot != null) return modelRoot;
            return prefab.transform;
        }

        /// <summary>
        /// Mirror the transform hierarchy from modelRoot down to a renderer's transform,
        /// creating intermediate GameObjects under partRoot with matching local transforms.
        /// Uses cloneMap to deduplicate shared parent nodes.
        /// Returns the cloned leaf transform.
        /// </summary>
        private static Transform MirrorTransformChain(Transform rendererTransform, Transform modelRoot,
            Transform partRootTransform, Dictionary<Transform, Transform> cloneMap)
        {
            // Build path from renderer up to modelRoot
            var chain = new List<Transform>();
            Transform cur = rendererTransform;
            while (cur != null && cur != modelRoot)
            {
                chain.Add(cur);
                cur = cur.parent;
            }
            chain.Reverse(); // root-first order

            Transform parent = partRootTransform;
            for (int i = 0; i < chain.Count; i++)
            {
                Transform src = chain[i];
                if (cloneMap.TryGetValue(src, out Transform existing))
                {
                    parent = existing;
                    continue;
                }
                GameObject clone = new GameObject(src.gameObject.name);
                clone.transform.SetParent(parent, false);
                clone.transform.localPosition = src.localPosition;
                clone.transform.localRotation = src.localRotation;
                clone.transform.localScale = src.localScale;
                cloneMap[src] = clone.transform;
                parent = clone.transform;
            }

            return parent;
        }

        /// <summary>
        /// Create a fake parachute canopy (flattened hemisphere) and parent it to
        /// the ghost part identified by persistentId. Returns the canopy GameObject.
        /// </summary>
        internal static GameObject CreateFakeCanopy(GameObject ghostRoot, uint partPersistentId)
        {
            if (ghostRoot == null) return null;

            Transform partTransform = ghostRoot.transform.Find($"ghost_part_{partPersistentId}");
            if (partTransform == null) return null;

            GameObject canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            canopy.name = $"fake_canopy_{partPersistentId}";

            // Disable collider
            var col = canopy.GetComponent<Collider>();
            if (col != null) col.enabled = false;

            // Scale: wide and flat (parachute dome shape)
            canopy.transform.SetParent(partTransform, false);
            canopy.transform.localPosition = new Vector3(0f, 4f, 0f); // above the part
            canopy.transform.localScale = new Vector3(5.2f, 2f, 5.2f);

            // Orange-white parachute color
            var renderer = canopy.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    Material mat = new Material(shader);
                    mat.color = new Color(1f, 0.6f, 0.2f, 1f);
                    mat.SetColor("_EmissiveColor", new Color(1f, 0.6f, 0.2f, 0.3f));
                    renderer.material = mat;
                }
            }

            return canopy;
        }

        private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab,
            uint persistentId, string partName, out int meshCount)
        {
            meshCount = 0;
            Transform modelRoot = FindModelRoot(prefab);
            var meshRenderers = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
            var skinnedRenderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if ((meshRenderers == null || meshRenderers.Length == 0) &&
                (skinnedRenderers == null || skinnedRenderers.Length == 0))
                return false;

            int totalMR = meshRenderers != null ? meshRenderers.Length : 0;
            int totalSMR = skinnedRenderers != null ? skinnedRenderers.Length : 0;
            ParsekLog.Log($"  Part '{partName}' pid={persistentId}: modelRoot='{modelRoot.name}', " +
                $"{totalMR} MeshRenderers, {totalSMR} SkinnedMeshRenderers");

            // Name by persistentId for O(1) lookup during playback; fall back to part name
            string partLabel = persistentId != 0
                ? persistentId.ToString()
                : (prefab.partInfo?.name ?? "unknown");
            GameObject partRoot = new GameObject($"ghost_part_{partLabel}");
            partRoot.transform.SetParent(root, false);

            Vector3 localPos;
            if (TryParseVector3(GetPartPositionRaw(partNode), out localPos))
                partRoot.transform.localPosition = localPos;

            Quaternion localRot;
            if (TryParseQuaternion(GetPartRotationRaw(partNode), out localRot))
                partRoot.transform.localRotation = localRot;

            bool added = false;

            // Clone map: maps prefab transforms → ghost cloned transforms.
            // Deduplicates shared parent nodes across multiple renderers.
            var cloneMap = new Dictionary<Transform, Transform>();

            // Preserve the model root's own local transform (position/rotation/scale).
            // Many KSP parts have non-identity transforms on the "model" child.
            // partRoot already has snapshot pos/rot, so add an intermediate node.
            GameObject modelNode = new GameObject(modelRoot.name);
            modelNode.transform.SetParent(partRoot.transform, false);
            modelNode.transform.localPosition = modelRoot.localPosition;
            modelNode.transform.localRotation = modelRoot.localRotation;
            modelNode.transform.localScale = modelRoot.localScale;
            cloneMap[modelRoot] = modelNode.transform;

            for (int r = 0; r < meshRenderers.Length; r++)
            {
                var mr = meshRenderers[r];
                if (mr == null) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                Transform leaf = MirrorTransformChain(mr.transform, modelRoot, partRoot.transform, cloneMap);
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                meshCount++;
                ParsekLog.Log($"    MR[{r}] '{mr.gameObject.name}' mesh={mf.sharedMesh.name} " +
                    $"localPos={leaf.localPosition} localScale={leaf.localScale}");
                added = true;
            }

            for (int r = 0; r < skinnedRenderers.Length; r++)
            {
                var smr = skinnedRenderers[r];
                if (smr == null || smr.sharedMesh == null) continue;

                Transform leaf = MirrorTransformChain(smr.transform, modelRoot, partRoot.transform, cloneMap);
                // Use shared bind-pose mesh as a static ghost visual.
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = smr.sharedMesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
                meshCount++;
                ParsekLog.Log($"    SMR[{r}] '{smr.gameObject.name}' mesh={smr.sharedMesh.name} " +
                    $"localPos={leaf.localPosition} localScale={leaf.localScale}");
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
