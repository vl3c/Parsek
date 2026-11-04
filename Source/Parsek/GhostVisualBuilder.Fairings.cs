using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // Procedural fairings: internal structure hiding, cone mesh generation and the fairing visual.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Extracts rootObject values from all ModuleStructuralNode MODULE nodes
        /// in the given part config. These are the internal truss/cap transforms
        /// that should be hidden when procedural fairings are intact.
        /// Returns an empty set if no structural node modules exist.
        /// </summary>
        internal static HashSet<string> GetFairingInternalStructureNames(ConfigNode partConfig)
        {
            var names = new HashSet<string>();
            if (partConfig == null) return names;

            var modules = partConfig.GetNodes("MODULE");
            if (modules == null) return names;

            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") != "ModuleStructuralNode")
                    continue;
                string rootObject = modules[i].GetValue("rootObject");
                if (!string.IsNullOrEmpty(rootObject))
                    names.Add(rootObject);
            }
            return names;
        }

        /// <summary>
        /// Finds already-cloned transforms in the ghost model hierarchy that match
        /// fairing internal structure names (truss/cap objects from ModuleStructuralNode)
        /// and permanently hides them. Prefab Cap/Truss meshes are at placeholder scale
        /// (2000,10,2000) — the procedural truss mesh replaces them visually.
        /// </summary>
        internal static void HideFairingInternalStructure(
            FairingGhostInfo fairingInfo, ConfigNode partConfig,
            Transform ghostModelNode, uint persistentId, string partName)
        {
            if (fairingInfo == null || ghostModelNode == null) return;

            HashSet<string> structureNames = GetFairingInternalStructureNames(partConfig);
            if (structureNames.Count == 0)
            {
                return;
            }

            // Permanently hide prefab Cap/Truss clones — they are at meaningless prefab default scale
            // (2000,10,2000). The procedural truss mesh on FairingGhostInfo.trussStructureObject
            // provides the correct post-jettison visual instead.
            var hidden = new List<GameObject>();
            CollectMatchingTransforms(ghostModelNode, structureNames, hidden);

            for (int i = 0; i < hidden.Count; i++)
                hidden[i].SetActive(false);

        }

        /// <summary>
        /// Recursively walks a transform hierarchy collecting GameObjects whose name
        /// matches one of the given names.
        /// </summary>
        private static void CollectMatchingTransforms(Transform root, HashSet<string> names, List<GameObject> results)
        {
            if (root == null) return;
            if (names.Contains(root.name))
                results.Add(root.gameObject);
            for (int i = 0; i < root.childCount; i++)
                CollectMatchingTransforms(root.GetChild(i), names, results);
        }

        internal static Mesh GenerateFairingConeMesh(
            List<(float h, float r)> sections, int nSides, Vector3 pivot, Vector3 axis)
        {
            // Sort sections by height
            sections.Sort((a, b) => a.h.CompareTo(b.h));

            nSides = Mathf.Min(nSides, 24);
            if (nSides < 3) nSides = 3;

            Quaternion axisRot = Quaternion.FromToRotation(Vector3.up, axis.normalized);

            // Build vertices and UVs: (nSides+1) verts per ring (duplicated seam), plus apex if last r ≈ 0
            int ringCount = sections.Count;
            bool hasApex = sections[ringCount - 1].r < 0.01f;
            int ringsToGenerate = hasApex ? ringCount - 1 : ringCount;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();

            for (int ring = 0; ring < ringsToGenerate; ring++)
            {
                float h = sections[ring].h;
                float r = sections[ring].r;
                float vCoord = ringCount > 1 ? (float)ring / (ringCount - 1) : 0f;

                for (int s = 0; s <= nSides; s++)
                {
                    float angle = (float)s / nSides * Mathf.PI * 2f;
                    float x = Mathf.Cos(angle) * r;
                    float z = Mathf.Sin(angle) * r;

                    Vector3 localPos = new Vector3(x, h, z);
                    Vector3 rotatedPos = axisRot * localPos + pivot;
                    vertices.Add(rotatedPos);

                    float uCoord = (float)s / nSides;
                    uvs.Add(new Vector2(uCoord, vCoord));
                }
            }

            // Apex vertex
            int apexIndex = -1;
            if (hasApex)
            {
                float apexH = sections[ringCount - 1].h;
                Vector3 apexLocal = new Vector3(0f, apexH, 0f);
                Vector3 apexPos = axisRot * apexLocal + pivot;
                apexIndex = vertices.Count;
                vertices.Add(apexPos);
                uvs.Add(new Vector2(0.5f, 1f));
            }

            // Build triangles
            var triangles = new List<int>();
            int vertsPerRing = nSides + 1;

            // Connect adjacent rings with triangle strips (CW winding for outward-facing normals)
            for (int ring = 0; ring < ringsToGenerate - 1; ring++)
            {
                int ringBase = ring * vertsPerRing;
                int nextBase = (ring + 1) * vertsPerRing;

                for (int s = 0; s < nSides; s++)
                {
                    int bl = ringBase + s;
                    int br = ringBase + s + 1;
                    int tl = nextBase + s;
                    int tr = nextBase + s + 1;

                    // CW winding (outward-facing normals from outside)
                    triangles.Add(bl);
                    triangles.Add(tl);
                    triangles.Add(br);

                    triangles.Add(br);
                    triangles.Add(tl);
                    triangles.Add(tr);
                }
            }

            // Connect last ring to apex (pointy nosecone)
            if (hasApex && ringsToGenerate > 0)
            {
                int lastRingBase = (ringsToGenerate - 1) * vertsPerRing;
                for (int s = 0; s < nSides; s++)
                {
                    int bl = lastRingBase + s;
                    int br = lastRingBase + s + 1;

                    triangles.Add(bl);
                    triangles.Add(apexIndex);
                    triangles.Add(br);
                }
            }

            // Conical cap for truncated cone (top ring has non-zero radius, e.g. capRadius).
            // Creates a pointed cone from the last ring to an apex above it, matching the
            // shape of a real fairing nosecone. Apex height = lastR * 1.5 above last ring.
            if (!hasApex && ringsToGenerate > 0)
            {
                float capH = sections[ringCount - 1].h;
                float capR = sections[ringCount - 1].r;
                float coneApexH = capH + capR * 1.5f;
                Vector3 coneApex = axisRot * new Vector3(0f, coneApexH, 0f) + pivot;
                int coneApexIdx = vertices.Count;
                vertices.Add(coneApex);
                uvs.Add(new Vector2(0.5f, 1f));

                int lastRingBase = (ringsToGenerate - 1) * vertsPerRing;
                for (int s = 0; s < nSides; s++)
                {
                    int bl = lastRingBase + s;
                    int br = lastRingBase + s + 1;

                    triangles.Add(bl);
                    triangles.Add(coneApexIdx);
                    triangles.Add(br);
                }
            }

            Mesh mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static FairingGhostInfo BuildFairingVisual(
            ConfigNode partNode, Part prefab, Transform modelNode,
            uint persistentId, string partName)
        {
            ConfigNode fairingModule = FindModuleNode(partNode, "ModuleProceduralFairing");
            if (fairingModule == null) return null;

            // Skip mesh for already-deployed fairings
            string fsmState = fairingModule.GetValue("fsm");
            if (fsmState == "st_flight_deployed") return null;

            var xNodes = fairingModule.GetNodes("XSECTION");
            if (xNodes == null || xNodes.Length < 2)
            {
                return null;
            }

            var sections = new List<(float h, float r)>();
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < xNodes.Length; i++)
            {
                float h, r;
                if (float.TryParse(xNodes[i].GetValue("h"), NumberStyles.Float, ic, out h) &&
                    float.TryParse(xNodes[i].GetValue("r"), NumberStyles.Float, ic, out r))
                    sections.Add((h, r));
            }
            if (sections.Count < 2) return null;

            // Read geometry params from prefab module
            var fairingPrefab = prefab.FindModuleImplementing<ModuleProceduralFairing>();
            int nSides = fairingPrefab != null ? Mathf.Min(fairingPrefab.nSides, 24) : 24;
            Vector3 pivot = fairingPrefab != null ? fairingPrefab.pivot : Vector3.zero;
            Vector3 axis = fairingPrefab != null ? fairingPrefab.axis : Vector3.up;

            Mesh mesh = GenerateFairingConeMesh(sections, nSides, pivot, axis);

            GameObject go = new GameObject("fairing_panels");
            go.transform.SetParent(modelNode, false);
            go.AddComponent<MeshFilter>().mesh = mesh;

            // Use the base adapter's material (includes variant textures) instead of hardcoded gray.
            // The first active MeshRenderer on the ghost model node is the base adapter mesh
            // (Cap/Truss are further down in the hierarchy).
            var mr = go.AddComponent<MeshRenderer>();
            var baseMr = modelNode.GetComponentInChildren<MeshRenderer>();
            if (baseMr != null && baseMr.sharedMaterial != null)
            {
                mr.sharedMaterial = baseMr.sharedMaterial;
            }
            else
            {
                mr.material = new Material(Shader.Find("KSP/Diffuse"))
                {
                    color = new Color(0.85f, 0.85f, 0.85f)
                };
            }

            return new FairingGhostInfo
            {
                partPersistentId = persistentId,
                fairingMeshObject = go
            };
        }
    }
}
