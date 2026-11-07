using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Procedural fairings: the stock-built panel shell (generated cone outside FLIGHT), and the
    // stock interstage truss / cap structure nodes driven by stock's own visibility rule.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Pure: the stock ModuleStructuralNode inputs of one part, in module order. Config
        /// fields (rootObject, attachNodeNames, spawnManually, reverseVisibility) come from the
        /// prefab PART config, because a snapshot MODULE node carries only persistent fields;
        /// the persistent pair (visibilityState, spawnState) comes from the snapshot's own
        /// ModuleStructuralNode nodes, matched by ordinal. ModuleStructuralNodeToggle.showMesh,
        /// when the part has the toggle, overrides visibilityState exactly as the toggle's
        /// UpdateNodes does at Start. Defaults are the stock KSPField defaults.
        /// </summary>
        internal static List<FairingStructureNodeGhostInfo> ParseFairingStructureNodes(
            ConfigNode partConfig, ConfigNode partNode)
        {
            var result = new List<FairingStructureNodeGhostInfo>();
            if (partConfig == null) return result;
            ConfigNode[] configModules = partConfig.GetNodes("MODULE");
            if (configModules == null) return result;

            var snapshotNodes = new List<ConfigNode>();
            ConfigNode[] snapshotModules = partNode != null ? partNode.GetNodes("MODULE") : null;
            if (snapshotModules != null)
            {
                for (int i = 0; i < snapshotModules.Length; i++)
                    if (snapshotModules[i].GetValue("name") == "ModuleStructuralNode")
                        snapshotNodes.Add(snapshotModules[i]);
            }

            bool hasToggle = false;
            bool toggleShowMesh = true;
            ConfigNode toggleNode = FindModuleNode(partNode, "ModuleStructuralNodeToggle");
            if (toggleNode != null)
            {
                hasToggle = true;
                toggleShowMesh = ParseBoolOr(toggleNode.GetValue("showMesh"), true);
            }
            else if (FindModuleNode(partConfig, "ModuleStructuralNodeToggle") != null)
            {
                hasToggle = true;
                toggleShowMesh = ParseBoolOr(
                    FindModuleNode(partConfig, "ModuleStructuralNodeToggle").GetValue("showMesh"), true);
            }

            Dictionary<string, int> attachMap = ParseAttachNodeIndices(partNode);
            int ordinal = 0;
            for (int i = 0; i < configModules.Length; i++)
            {
                ConfigNode cfg = configModules[i];
                if (cfg.GetValue("name") != "ModuleStructuralNode")
                    continue;
                ConfigNode snap = ordinal < snapshotNodes.Count ? snapshotNodes[ordinal] : null;
                ordinal++;

                var info = new FairingStructureNodeGhostInfo
                {
                    rootObject = cfg.GetValue("rootObject") ?? "Fairing",
                    spawnManually = ParseBoolOr(cfg.GetValue("spawnManually"), false),
                    reverseVisibility = ParseBoolOr(cfg.GetValue("reverseVisibility"), false),
                    visibilityState = ParseBoolOr(snap != null ? snap.GetValue("visibilityState") : null, true),
                    spawnState = ParseBoolOr(snap != null ? snap.GetValue("spawnState") : null, false),
                };
                if (hasToggle)
                    info.visibilityState = toggleShowMesh;

                string attachNames = cfg.GetValue("attachNodeNames") ?? "bottom";
                var indices = new List<int>();
                string[] names = attachNames.Split(',');
                for (int n = 0; n < names.Length; n++)
                {
                    int idx;
                    if (attachMap.TryGetValue(names[n].Trim(), out idx) && !indices.Contains(idx))
                        indices.Add(idx);
                }
                info.attachedPartIndices = indices.ToArray();
                result.Add(info);
            }
            return result;
        }

        /// <summary>
        /// Pure: the snapshot's "attN = nodeId, partIndex" rows as nodeId -> partIndex,
        /// keeping only real attachments (stock writes partIndex -1 for an empty node).
        /// </summary>
        internal static Dictionary<string, int> ParseAttachNodeIndices(ConfigNode partNode)
        {
            var map = new Dictionary<string, int>();
            if (partNode == null) return map;
            string[] rows = partNode.GetValues("attN");
            if (rows == null) return map;
            for (int i = 0; i < rows.Length; i++)
            {
                if (string.IsNullOrEmpty(rows[i])) continue;
                string[] fields = rows[i].Split(',');
                if (fields.Length < 2) continue;
                int idx;
                if (!int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)
                    || idx < 0)
                    continue;
                string id = fields[0].Trim();
                if (id.Length > 0 && !map.ContainsKey(id))
                    map[id] = idx;
            }
            return map;
        }

        /// <summary>
        /// Pure transcription of stock ModuleStructuralNode.CheckDisplay (KSP 1.12.5): a
        /// hidden toggle hides the structure outright; otherwise it shows when spawned
        /// manually or when any of its attach nodes holds a part, inverted by
        /// reverseVisibility.
        /// </summary>
        internal static bool ComputeStructureNodeVisible(
            bool visibilityState, bool spawnManually, bool spawnState,
            bool reverseVisibility, bool anyAttached)
        {
            if (!visibilityState) return false;
            bool flag = spawnManually ? spawnState : anyAttached;
            return reverseVisibility ? !flag : flag;
        }

        /// <summary>
        /// Pure: whether the structure node currently sees an attached part. Before pid
        /// resolution (attachedPartPids null) the snapshot attachment counts; afterwards a
        /// pid counts while it is still in the ghost's logical part set (an empty or null set
        /// means "no pruning has happened", the convention the compound-part refresh uses).
        /// </summary>
        internal static bool StructureNodeHasAttachedPart(
            FairingStructureNodeGhostInfo node, HashSet<uint> logicalPartIds)
        {
            if (node == null) return false;
            if (node.attachedPartPids == null)
                return node.attachedPartIndices != null && node.attachedPartIndices.Length > 0;
            for (int i = 0; i < node.attachedPartPids.Length; i++)
            {
                uint pid = node.attachedPartPids[i];
                if (pid == 0) continue;
                if (logicalPartIds == null || logicalPartIds.Count == 0 || logicalPartIds.Contains(pid))
                    return true;
            }
            return false;
        }

        internal static bool ComputeStructureNodeVisible(
            FairingStructureNodeGhostInfo node, HashSet<uint> logicalPartIds)
        {
            if (node == null) return false;
            return ComputeStructureNodeVisible(
                node.visibilityState, node.spawnManually, node.spawnState,
                node.reverseVisibility, StructureNodeHasAttachedPart(node, logicalPartIds));
        }

        /// <summary>
        /// Pure: maps the snapshot part indices a structure node is attached to onto the
        /// persistentIds the ghost names its parts by. An index outside the PART list or a
        /// part without a persistentId resolves to 0, which never counts as attached.
        /// </summary>
        internal static uint[] ResolveAttachedPartPids(int[] indices, ConfigNode[] partNodes)
        {
            if (indices == null) return new uint[0];
            var pids = new uint[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                int idx = indices[i];
                uint pid = 0;
                if (partNodes != null && idx >= 0 && idx < partNodes.Length)
                {
                    string raw = partNodes[idx].GetValue("persistentId");
                    if (raw != null)
                        uint.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
                }
                pids[i] = pid;
            }
            return pids;
        }

        /// <summary>
        /// Resolves every structure node's attached part indices to persistentIds once the
        /// whole snapshot PART list is known (the per-part builder sees one PART node only).
        /// </summary>
        internal static void ResolveFairingStructureAttachPids(
            FairingGhostInfo fairingInfo, ConfigNode[] partNodes)
        {
            if (fairingInfo == null || fairingInfo.structureNodes == null) return;
            for (int i = 0; i < fairingInfo.structureNodes.Count; i++)
            {
                FairingStructureNodeGhostInfo node = fairingInfo.structureNodes[i];
                if (node == null) continue;
                node.attachedPartPids = ResolveAttachedPartPids(node.attachedPartIndices, partNodes);
            }
        }

        private static bool ParseBoolOr(string raw, bool fallback)
        {
            bool parsed;
            return raw != null && bool.TryParse(raw.Trim(), out parsed) ? parsed : fallback;
        }

        /// <summary>
        /// Binds the part's stock ModuleStructuralNode objects (the AutoTruss Truss1..6 and
        /// Cap1..6 on stock fairing bases) to their cloned ghost transforms and applies the
        /// stock visibility rule from the snapshot. The clones keep the prefab hierarchy,
        /// including the "Hider" parent whose (0.0005, 0.1, 0.0005) scale cancels the
        /// children's (2000, 10, 2000) scale, so a shown structure renders at exactly stock size: stock itself
        /// never rescales these objects, it only SetActive's them.
        /// </summary>
        internal static List<FairingStructureNodeGhostInfo> BuildFairingStructureNodes(
            ConfigNode partConfig, ConfigNode partNode,
            Transform ghostModelNode, uint persistentId, string partName)
        {
            if (ghostModelNode == null) return null;
            List<FairingStructureNodeGhostInfo> nodes = ParseFairingStructureNodes(partConfig, partNode);
            if (nodes.Count == 0) return null;

            int bound = 0, shown = 0, attached = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                FairingStructureNodeGhostInfo node = nodes[i];
                Transform t = FindTransformByName(ghostModelNode, node.rootObject);
                if (t == null) continue;
                node.structureObject = t.gameObject;
                bound++;
                if (node.attachedPartIndices.Length > 0) attached++;
                bool visible = ComputeStructureNodeVisible(node, null);
                node.structureObject.SetActive(visible);
                if (visible) shown++;
            }

            ParsekLog.VerboseRateLimited("GhostVisual", $"fairing_struct_{partName}_{persistentId}",
                $"  Fairing structure nodes: part='{partName}' pid={persistentId} nodes={nodes.Count} " +
                $"bound={bound} withAttachment={attached} shown={shown} " +
                $"visibilityState={(nodes[0].visibilityState ? "on" : "off")}", 60.0);
            return nodes;
        }

        private static Transform FindTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindTransformByName(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
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

        /// <summary>
        /// Pure: parses the snapshot's XSECTION rows into (h, r) pairs, sorted by height the
        /// way stock OnLoad sorts its FairingXSection list. Unparseable rows are skipped.
        /// </summary>
        internal static List<(float h, float r)> ParseFairingSections(ConfigNode fairingModule)
        {
            var sections = new List<(float h, float r)>();
            if (fairingModule == null) return sections;
            ConfigNode[] xNodes = fairingModule.GetNodes("XSECTION");
            if (xNodes == null) return sections;
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < xNodes.Length; i++)
            {
                float h, r;
                if (float.TryParse(xNodes[i].GetValue("h"), NumberStyles.Float, ic, out h) &&
                    float.TryParse(xNodes[i].GetValue("r"), NumberStyles.Float, ic, out r))
                    sections.Add((h, r));
            }
            sections.Sort((a, b) => a.h.CompareTo(b.h));
            return sections;
        }

        /// <summary>
        /// Pure: the panel count around the fairing. Stock persists the player's "nArcs"
        /// choice on the module and builds Mathf.RoundToInt(nArcs) panels per ring; a
        /// missing or unparseable value falls back to the part's configured default.
        /// </summary>
        internal static float ResolveFairingArcCount(ConfigNode fairingModule, float prefabDefault)
        {
            float parsed;
            string raw = fairingModule != null ? fairingModule.GetValue("nArcs") : null;
            if (raw != null
                && float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                && parsed >= 1f && parsed <= 64f)
                return parsed;
            return prefabDefault;
        }

        private static FairingGhostInfo BuildFairingVisual(
            ConfigNode partNode, Part prefab, Transform modelNode,
            uint persistentId, string partName)
        {
            ConfigNode fairingModule = FindModuleNode(partNode, "ModuleProceduralFairing");
            if (fairingModule == null) return null;

            var info = new FairingGhostInfo { partPersistentId = persistentId };

            var fairingPrefab = prefab.FindModuleImplementing<ModuleProceduralFairing>();

            // The base ring stays on after the shell goes, so its variant colour applies to
            // deployed fairings as well.
            ApplyFairingVariantToBase(fairingPrefab, prefab, partNode, modelNode, persistentId, partName);

            // The interstage truss and cap plates follow stock's own rule whether or not the
            // shell is still on: a ghost recorded after jettison still shows the truss of an
            // attached stage, and never the five unattached ones.
            info.structureNodes = BuildFairingStructureNodes(
                prefab.partInfo?.partConfig, partNode, modelNode, persistentId, partName);

            // No shell for an already-deployed fairing.
            string fsmState = fairingModule.GetValue("fsm");
            List<(float h, float r)> sections = ParseFairingSections(fairingModule);
            if (fsmState == "st_flight_deployed" || sections.Count < 2)
                return info.structureNodes != null ? info : null;

            string declineReason;
            info.shellMeshes = new List<Mesh>();
            GameObject stockShell = TryBuildStockFairingPanels(
                fairingModule, fairingPrefab, prefab, partNode, modelNode, sections,
                persistentId, partName, info.shellMeshes, out declineReason);
            if (stockShell != null)
            {
                info.fairingMeshObject = stockShell;
                info.shellBuiltByStockPanels = true;
                return info;
            }

            ParsekLog.VerboseRateLimited("GhostVisual", $"fairing_legacy_{partName}",
                $"  Fairing shell: part='{partName}' pid={persistentId} using generated cone " +
                $"(stock panel builder declined: {declineReason})", 60.0);

            int nSides = fairingPrefab != null ? Mathf.Min(fairingPrefab.nSides, 24) : 24;
            Vector3 pivot = fairingPrefab != null ? fairingPrefab.pivot : Vector3.zero;
            Vector3 axis = fairingPrefab != null ? fairingPrefab.axis : Vector3.up;

            Mesh mesh = GenerateFairingConeMesh(sections, nSides, pivot, axis);
            info.shellMeshes.Add(mesh);

            GameObject go = new GameObject("fairing_panels");
            go.transform.SetParent(modelNode, false);
            go.AddComponent<MeshFilter>().mesh = mesh;

            // Use the base adapter's material (includes variant textures) instead of hardcoded gray.
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

            info.fairingMeshObject = go;
            return info;
        }

        // Shared flight materials for stock-built ghost fairing panels, keyed by part name,
        // variant and role. Ghost parts share prefab materials the same way.
        private static readonly Dictionary<string, Material> fairingPanelMaterialCache =
            new Dictionary<string, Material>();

        /// <summary>
        /// Builds the fairing shell with stock's own ProceduralFairings.FairingPanel code, fed
        /// the snapshot's XSECTIONs and the part prefab's ModuleProceduralFairing as the
        /// geometry host (nSides, pivot, axis, noseTip, edgeSlide, edgeWarp, capRadius,
        /// aberrantNormalLimit are config fields, identical on prefab and live part). This is
        /// the same sequence as stock SpawnMeshes in flight: SetupPanelArray (which adds the
        /// nose cone only when the last section's radius equals capRadius), then per panel
        /// Spawn under the "model" transform and BuildMesh against the panel one ring below.
        /// The panels' materials follow stock OnStart (FairingsFlightMat, TextureURL /
        /// CapTextureURL) and ApplyVariantToPanel (the selected variant's FairingsTextureURL,
        /// shaderName and extra info on the non-cap panels).
        ///
        /// The panel code reads only config fields off the host (the player's persistent nArcs
        /// reaches it as the nSegments argument; host.nArcs is passed to SetArcPoints but
        /// unused there), so the prefab is read, never written.
        ///
        /// FLIGHT only: in other scenes stock BuildMesh skips the nose-cone ring setup (it
        /// branches on LoadedSceneIsFlight / LoadedSceneIsEditor), so the caller keeps the
        /// generated cone there. Any exception declines to the generated cone as well.
        /// </summary>
        private static GameObject TryBuildStockFairingPanels(
            ConfigNode fairingModule, ModuleProceduralFairing host, Part prefab, ConfigNode partNode,
            Transform modelNode, List<(float h, float r)> sections,
            uint persistentId, string partName, List<Mesh> createdMeshes, out string declineReason)
        {
            declineReason = null;
            if (host == null) { declineReason = "no-prefab-module"; return null; }
            if (!HighLogic.LoadedSceneIsFlight) { declineReason = "scene-not-flight"; return null; }

            float nArcs = ResolveFairingArcCount(fairingModule, host.nArcs);
            int nSegments = Mathf.RoundToInt(nArcs);
            if (nSegments < 1) { declineReason = "no-panels"; return null; }

            string variantName;
            PartVariant variant = ResolveFairingVariant(prefab, partNode, out variantName);
            Material panelMat = GetFairingPanelMaterial(host, prefab, partName, variant, variantName, cone: false);
            Material coneMat = GetFairingPanelMaterial(host, prefab, partName, variant, variantName, cone: true);
            if (panelMat == null || coneMat == null) { declineReason = "no-material"; return null; }

            var xSections = new List<ProceduralFairings.FairingXSection>(sections.Count);
            for (int i = 0; i < sections.Count; i++)
                xSections.Add(new ProceduralFairings.FairingXSection { h = sections[i].h, r = sections[i].r });
            xSections[xSections.Count - 1].isLast = true;

            GameObject container = null;
            try
            {
                List<ProceduralFairings.FairingPanel> panels =
                    ProceduralFairings.FairingPanel.SetupPanelArray(
                        nSegments, panelMat, coneMat, xSections, host, host.capRadius);
                if (panels == null || panels.Count == 0)
                {
                    declineReason = "no-panels";
                    return null;
                }

                container = new GameObject("fairing_panels");
                container.transform.SetParent(modelNode, false);
                int capPanels = 0;
                for (int i = 0; i < panels.Count; i++)
                {
                    ProceduralFairings.FairingPanel panel = panels[i];
                    panel.Spawn(container.transform);
                    panel.BuildMesh(true, i < nSegments ? null : panels[i - nSegments]);
                    // Spawn gives every panel its own material instance; the ghost shares
                    // one per part type and variant instead.
                    MeshRenderer mr = panel.go != null ? panel.go.GetComponent<MeshRenderer>() : null;
                    if (mr != null)
                    {
                        Material instance = mr.sharedMaterial;
                        mr.sharedMaterial = panel.isCap ? coneMat : panelMat;
                        if (instance != null && instance != panelMat && instance != coneMat)
                            UnityEngine.Object.Destroy(instance);
                    }
                    if (panel.isCap) capPanels++;
                    if (panel.mesh != null) createdMeshes.Add(panel.mesh);
                    panel.CleanUp();
                }

                ParsekLog.VerboseRateLimited("GhostVisual", $"fairing_stock_{partName}",
                    $"  Fairing shell: part='{partName}' pid={persistentId} built by stock panel builder " +
                    $"panels={panels.Count} capPanels={capPanels} nArcs={nArcs.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"sections={sections.Count} topR={sections[sections.Count - 1].r.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"capRadius={host.capRadius.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"variant='{variantName ?? "(base)"}'", 60.0);
                return container;
            }
            catch (System.Exception ex)
            {
                if (container != null) UnityEngine.Object.Destroy(container);
                DestroyMeshes(createdMeshes);
                declineReason = "threw:" + ex.GetType().Name;
                ParsekLog.Warn("GhostVisual",
                    $"Fairing shell: stock panel builder threw for part='{partName}' pid={persistentId}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void DestroyMeshes(List<Mesh> meshes)
        {
            if (meshes == null) return;
            for (int i = 0; i < meshes.Count; i++)
                if (meshes[i] != null) UnityEngine.Object.Destroy(meshes[i]);
            meshes.Clear();
        }

        /// <summary>
        /// Destroys the meshes every fairing shell of a ghost created (a GameObject destroy
        /// leaves its MeshFilter's mesh alive). Called from ghost teardown.
        /// </summary>
        internal static void DestroyFairingShellMeshes(Dictionary<uint, FairingGhostInfo> fairingInfos)
        {
            if (fairingInfos == null) return;
            foreach (var kvp in fairingInfos)
                if (kvp.Value != null) DestroyMeshes(kvp.Value.shellMeshes);
        }

        /// <summary>
        /// Pure: the shader stock puts on the fairing base ring. onVariantApplied reads the
        /// variant's shaderName (default "KSP/Bumped Specular (Cutoff)") and swaps that
        /// default for "KSP/Bumped Specular" on the base; any other name is used as given.
        /// </summary>
        internal static string ResolveFairingBaseShaderName(string variantShaderName)
        {
            const string cutoff = "KSP/Bumped Specular (Cutoff)";
            string text = string.IsNullOrEmpty(variantShaderName) ? cutoff : variantShaderName;
            return text == cutoff ? "KSP/Bumped Specular" : text;
        }

        /// <summary>
        /// Pure: whether a material is the one stock re-textures as the fairing base. Stock
        /// matches the variant's materials by name prefix against BaseMaterialName; a clone's
        /// " (Instance)" suffix is ignored.
        /// </summary>
        internal static bool IsFairingBaseMaterialName(string materialName, string baseMaterialName)
        {
            if (string.IsNullOrEmpty(materialName) || string.IsNullOrEmpty(baseMaterialName))
                return false;
            return materialName.Replace(" (Instance)", "")
                .StartsWith(baseMaterialName, System.StringComparison.Ordinal);
        }

        // Shared base-ring materials, keyed by part name and variant.
        private static readonly Dictionary<string, Material> fairingBaseMaterialCache =
            new Dictionary<string, Material>();

        /// <summary>
        /// Gives the ghost's fairing base ring the selected variant's look, as stock
        /// ModuleProceduralFairing.onVariantApplied does for the renderer on the child named
        /// BaseModelTransformName: the variant material whose name starts with the EXTRA_INFO
        /// BaseMaterialName gets the variant shader, BaseTextureName as main texture,
        /// BaseNormalsName as bump map (only when the material has one) and the remaining
        /// extra info (spec maps, floats) through UpdateMaterialFromExtraInfo. The variant
        /// TEXTURE rules only name FairingsMat, so without this the ghost base keeps the
        /// prefab's default texture whatever colour the player picked.
        /// </summary>
        private static void ApplyFairingVariantToBase(
            ModuleProceduralFairing host, Part prefab, ConfigNode partNode, Transform modelNode,
            uint persistentId, string partName)
        {
            if (host == null || modelNode == null || string.IsNullOrEmpty(host.BaseModelTransformName))
                return;
            string variantName;
            PartVariant variant = ResolveFairingVariant(prefab, partNode, out variantName);
            if (variant == null) return;
            string baseMaterialName = variant.GetExtraInfoValue("BaseMaterialName");
            if (string.IsNullOrEmpty(baseMaterialName)) return;

            Transform baseTransform = FindTransformByName(modelNode, host.BaseModelTransformName);
            Renderer baseRenderer = baseTransform != null ? baseTransform.GetComponent<Renderer>() : null;
            if (baseRenderer == null || baseRenderer.sharedMaterial == null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"fairing_base_missing_{partName}",
                    $"  Fairing base: part='{partName}' pid={persistentId} variant='{variantName}' " +
                    $"no renderer on '{host.BaseModelTransformName}', base keeps its prefab look", 60.0);
                return;
            }

            string key = partName + "|" + variantName;
            Material mat;
            if (!fairingBaseMaterialCache.TryGetValue(key, out mat) || mat == null)
            {
                mat = BuildFairingBaseMaterial(variant, baseMaterialName, baseRenderer.sharedMaterial);
                if (mat == null)
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"fairing_base_nomat_{partName}",
                        $"  Fairing base: part='{partName}' pid={persistentId} variant='{variantName}' " +
                        $"no material named '{baseMaterialName}', base keeps its prefab look", 60.0);
                    return;
                }
                fairingBaseMaterialCache[key] = mat;
            }
            baseRenderer.sharedMaterial = mat;
            ParsekLog.VerboseRateLimited("GhostVisual", $"fairing_base_{partName}_{variantName}",
                $"  Fairing base: part='{partName}' pid={persistentId} variant='{variantName}' " +
                $"material='{mat.name}' shader='{(mat.shader != null ? mat.shader.name : "null")}' " +
                $"texture='{variant.GetExtraInfoValue("BaseTextureName") ?? "(kept)"}'", 60.0);
        }

        private static Material BuildFairingBaseMaterial(
            PartVariant variant, string baseMaterialName, Material rendererMaterial)
        {
            // Stock's source is the variant's own copy of the part material; the ghost
            // renderer's prefab material is the same material when the variant carries none.
            Material source = null;
            List<Material> variantMaterials = variant.Materials;
            if (variantMaterials != null)
            {
                for (int i = 0; i < variantMaterials.Count; i++)
                {
                    if (variantMaterials[i] != null
                        && IsFairingBaseMaterialName(variantMaterials[i].name, baseMaterialName))
                    {
                        source = variantMaterials[i];
                        break;
                    }
                }
            }
            if (source == null && IsFairingBaseMaterialName(rendererMaterial.name, baseMaterialName))
                source = rendererMaterial;
            if (source == null) return null;

            bool hasBump = rendererMaterial.HasProperty("_BumpMap");
            Texture mainTex = rendererMaterial.mainTexture;
            Texture bumpTex = hasBump ? rendererMaterial.GetTexture("_BumpMap") : null;

            string texUrl = variant.GetExtraInfoValue("BaseTextureName");
            if (!string.IsNullOrEmpty(texUrl))
            {
                Texture2D t = GameDatabase.Instance.GetTexture(texUrl, false);
                if (t != null) mainTex = t;
            }
            string normalUrl = variant.GetExtraInfoValue("BaseNormalsName");
            if (!string.IsNullOrEmpty(normalUrl) && hasBump)
            {
                Texture2D n = GameDatabase.Instance.GetTexture(normalUrl, true);
                if (n != null) bumpTex = n;
            }

            var mat = new Material(source) { name = source.name.Replace(" (Instance)", "") };
            Shader shader = Shader.Find(ResolveFairingBaseShaderName(variant.GetExtraInfoValue("shaderName")));
            if (shader != null) mat.shader = shader;
            mat.mainTexture = mainTex;
            if (mat.HasProperty("_BumpMap"))
                mat.SetTexture("_BumpMap", bumpTex);
            // Keeps the stock name while it runs: it routes texture-valued extra info (the
            // base _SpecMap) by matching the URL against the material name.
            variant.UpdateMaterialFromExtraInfo(mat);
            return mat;
        }

        private static PartVariant ResolveFairingVariant(Part prefab, ConfigNode partNode, out string variantName)
        {
            variantName = null;
            ConfigNode variantNode;
            if (!TryFindSelectedVariantNode(prefab, partNode, out variantNode, out variantName))
                return null;
            var variantModule = prefab.FindModuleImplementing<ModulePartVariants>();
            if (variantModule == null || variantModule.variantList == null) return null;
            for (int i = 0; i < variantModule.variantList.Count; i++)
            {
                PartVariant v = variantModule.variantList[i];
                if (v != null && v.Name == variantName)
                    return v;
            }
            return null;
        }

        /// <summary>
        /// The flight material stock gives a fairing panel: FairingsFlightMat with the part's
        /// TextureURL / TextureNormalURL (cone: CapTextureURL / CapTextureNormalURL) on the
        /// Opaque (Cutoff) shaders, the base variant's material copy, and for side panels the
        /// selected variant's ApplyVariantToPanel overrides.
        /// </summary>
        private static Material GetFairingPanelMaterial(
            ModuleProceduralFairing host, Part prefab, string partName,
            PartVariant variant, string variantName, bool cone)
        {
            string key = partName + "|" + (variantName ?? "") + (cone ? "|cone" : "|panel");
            Material cached;
            if (fairingPanelMaterialCache.TryGetValue(key, out cached) && cached != null)
                return cached;

            Material source = Resources.Load("Procedural/FairingsFlightMat") as Material;
            if (source == null) return null;
            var mat = new Material(source);

            string texUrl = cone ? host.CapTextureURL : host.TextureURL;
            string normalUrl = cone ? host.CapTextureNormalURL : host.TextureNormalURL;
            if (!string.IsNullOrEmpty(texUrl))
            {
                mat.mainTexture = GameDatabase.Instance.GetTexture(texUrl, false);
                if (!string.IsNullOrEmpty(normalUrl))
                {
                    mat.SetTexture("_BumpMap", GameDatabase.Instance.GetTexture(normalUrl, true));
                    mat.shader = Shader.Find("KSP/Bumped Specular Opaque (Cutoff)");
                }
                else
                {
                    mat.shader = Shader.Find("KSP/Specular Opaque (Cutoff)");
                }
            }
            if (!cone && prefab.baseVariant != null)
                prefab.baseVariant.TryCopyMaterial(mat);

            if (!cone && variant != null)
            {
                string shaderName = variant.GetExtraInfoValue("shaderName");
                string vTex = variant.GetExtraInfoValue("FairingsTextureURL");
                string vNormal = variant.GetExtraInfoValue("FairingsNormalURL");
                if (string.IsNullOrEmpty(shaderName))
                    shaderName = "KSP/Bumped Specular Opaque (Cutoff)";
                if (string.IsNullOrEmpty(vTex))
                {
                    vTex = host.TextureURL;
                    vNormal = host.TextureNormalURL;
                }
                Shader shader = Shader.Find(shaderName);
                if (shader != null) mat.shader = shader;
                // Stock loads both through GetTexture(url, asNormalMap: false).
                mat.mainTexture = GameDatabase.Instance.GetTexture(vTex, false);
                mat.SetTexture("_BumpMap", GameDatabase.Instance.GetTexture(vNormal, false));
                variant.UpdateMaterialFromExtraInfo(mat);
            }

            mat.name = "ParsekGhostFairing_" + key;
            fairingPanelMaterialCache[key] = mat;
            return mat;
        }
    }
}
