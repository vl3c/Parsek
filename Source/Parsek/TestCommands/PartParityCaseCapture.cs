using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>
    /// One case of the sweep, in the order of design-part-ghost-parity.md 4.2: the real
    /// renderer records and box BEFORE any ghost exists, the live vessel's start seeds, the
    /// ghost built through the engine's D2 seam with the seed prefix replayed, co-located
    /// under the real part, the framing rule, then one capture per view, then the files.
    /// Live; the runner calls each stage under its own exception guard.
    /// </summary>
    internal static class PartParityCaseCapture
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal const string FlagGhostNotBuilt = "ghost-not-built";
        internal const string FlagGhostRootOffset = "ghost-root-offset";
        internal const string FlagGhostLayerDiffers = "ghost-layer-differs";
        internal const string FlagClippedAtBorder = "clipped-at-border";
        internal const string FlagNoRenderedMesh = "no-rendered-mesh";
        internal const string FlagCaptureErrorPrefix = "capture-error:";
        internal const string FlagSeedEventsPrefix = "seed-events:";
        internal const string FlagSeedAppliedPrefix = "seed-applied:";
        internal const string FlagPlantedCubePrefix = "control-planted:";
        internal const string TagControl = "control";

        /// <summary>Ghost root offset above this (metres) is flagged (design 4.2 step 3).</summary>
        internal const double GhostRootOffsetToleranceM = 0.001;

        // ----- prepare -----

        internal static void Prepare(StepContext ctx, WaveCase wc)
        {
            Part part = wc.Real.rootPart;
            Transform partT = part.transform;
            wc.Skin = PartParitySkinRules.NewProof(wc.SkinOption, wc.RealVariant, wc.Entry?.B9Modules);
            wc.Skin.Real = PartParityCaseAuthoring.LiveSkinName(part, wc.Skin, out string realMember);
            wc.SkinRealMember = realMember;

            if (wc.ControlId == TestCommandPartParitySweep.ControlGhostMissingMesh) PlantCube(ctx, wc, part);
            RepairDepthMasks(wc, part);

            var realRenderers = new List<Renderer>(part.GetComponentsInChildren<Renderer>(true));
            wc.RealRecords = Records(partT, realRenderers, wc.RealLayers, null,
                GhostVisualBuilder.GetDepthMaskTransformNames(part.partInfo?.partConfig));

            if (BuildsGhost(wc.ControlId)) BuildGhost(wc, part);
            if (wc.ControlId == TestCommandPartParitySweep.ControlRealVsReal)
            {
                wc.GhostRecords = wc.RealRecords;
                wc.GhostLayers = wc.RealLayers;
            }
            else if (wc.Ghost != null)
            {
                CollectGhost(wc, partT);
            }
            else
            {
                wc.GhostRecords = new List<PartParityRendererRecord>();
            }
            Frame(wc);
        }

        /// <summary>
        /// Brings the real part's depth masks to the look the player sees before anything
        /// reads the real renderers (<see cref="PartParityDepthMaskRepair"/>): one summary line
        /// per case that has a depth-mask module, none for any other part.
        /// </summary>
        private static void RepairDepthMasks(WaveCase wc, Part part)
        {
            List<PartParityDepthMaskRepair.ModuleResult> results = PartParityDepthMaskRepair.Repair(part);
            if (results.Count == 0) return;
            string flag = PartParityDepthMaskRepair.CaseFlag(results);
            if (flag != null) wc.Flags.Add(flag);
            string line = PartParityDepthMaskRepair.SummaryLine(wc.CaseId, part.partInfo?.name, results);
            if (flag != null) ParsekLog.Info(PartParitySweepRunner.Tag, line);
            else ParsekLog.Verbose(PartParitySweepRunner.Tag, line);
        }

        /// <summary>The three controls that photograph no ghost.</summary>
        internal static bool BuildsGhost(string controlId)
            => controlId != TestCommandPartParitySweep.ControlEmpty
               && controlId != TestCommandPartParitySweep.ControlGhostAbsent
               && controlId != TestCommandPartParitySweep.ControlRealVsReal;

        private static void BuildGhost(WaveCase wc, Part part)
        {
            Vessel real = wc.Real;
            var sets = new PartTrackingSets();
            PartStateSeeder.SeedPartStates(real, sets, FlightRecorder.CacheEngineModules(real),
                FlightRecorder.CacheRcsModules(real), seedColorChangerLights: true, logTag: PartParitySweepRunner.Tag);
            var names = new Dictionary<uint, string>();
            foreach (Part p in real.parts)
                if (p != null && !names.ContainsKey(p.persistentId)) names[p.persistentId] = p.partInfo?.name ?? "unknown";
            double seedUT = Planetarium.GetUniversalTime();
            List<PartEvent> seeds = PartStateSeeder.EmitSeedEvents(sets, names, seedUT, PartParitySweepRunner.Tag);
            wc.SeedEvents = seeds.Count;

            Action<ConfigNode> mutate = wc.ControlId == TestCommandPartParitySweep.ControlWrongSkin && wc.GhostVariant != null
                ? (Action<ConfigNode>)(snap => SetSnapshotVariant(snap, wc.GhostVariant))
                : null;
            string recId = "pp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!GhostCoOrbitalRig.TryBuildGhostFromLiveVessel(real, recId, "ParsekPartParity_" + wc.CaseId,
                    seeds, mutate, out GhostCoOrbitalRig.LiveGhost live, out string fail))
            {
                wc.Flags.Add(FlagGhostNotBuilt);
                wc.Detail = wc.Detail ?? fail;
                return;
            }
            wc.Ghost = live;
            wc.SeedApplied = GhostCoOrbitalRig.ApplySeedPrefix(live, seedUT);
            ConfigNode snapPart = live.Snapshot != null ? live.Snapshot.GetNode("PART") : null;
            RecordSkinProof(wc, snapPart, live.Build);

            GameObject root = live.Build.root;
            root.transform.SetParent(part.transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = ControlRotation(wc.ControlId);
            root.transform.localScale = wc.ControlId == TestCommandPartParitySweep.ControlGhostScale90 ? Vector3.one * 0.9f : Vector3.one;
            root.SetActive(true);
            GhostCoOrbitalRig.NeutralisePhysics(root, out int _, out int _);
            wc.GhostRootOffsetM = GhostRootOffset(root.transform, part.transform);
            if (wc.ControlId == null && wc.GhostRootOffsetM.HasValue && wc.GhostRootOffsetM.Value > GhostRootOffsetToleranceM)
                wc.Flags.Add(FlagGhostRootOffset);
        }

        /// <summary>
        /// The snapshot and ghost halves of the skin proof (design 3.2): what
        /// <c>TryBackupSnapshot</c> wrote for the proof's module, and what the builder applied
        /// to the snapshot part (<see cref="GhostBuildResult.TryGetAppliedSkin"/>, keyed by the
        /// snapshot PART's persistentId); then the ghost-defect flags. One Verbose line names
        /// every value and the live member the real side was read from.
        /// </summary>
        private static void RecordSkinProof(WaveCase wc, ConfigNode snapPart, GhostBuildResult build)
        {
            PartParitySkinProof proof = wc.Skin;
            proof.Snapshot = PartParitySkinRules.SnapshotSkin(snapPart, proof);
            uint pid = 0;
            string pidText = snapPart != null ? snapPart.GetValue("persistentId") : null;
            if (pidText != null) uint.TryParse(pidText, NumberStyles.Integer, Ic, out pid);
            GhostAppliedSkin applied = null;
            bool recorded = build != null && build.TryGetAppliedSkin(pid, out applied);
            proof.Ghost = PartParitySkinRules.GhostSkin(proof, recorded ? applied.variantName : null);
            foreach (string f in PartParitySkinRules.GhostFlags(proof))
                if (!wc.Flags.Contains(f)) wc.Flags.Add(f);
            ParsekLog.Verbose(PartParitySweepRunner.Tag, $"case {wc.CaseId}: skin proof module={proof.Module ?? "<none>"} " +
                $"moduleId={proof.ModuleId ?? "<none>"} requested={proof.Requested ?? "<none>"} real={proof.Real ?? "<none>"} " +
                $"snapshot={proof.Snapshot ?? "<none>"} ghost={proof.Ghost ?? "<none>"} realMember={wc.SkinRealMember ?? "<none>"} " +
                $"ghostPid={pid.ToString(Ic)} ghostRecord={(recorded ? "true" : "false")} " +
                $"ghostPath={(recorded ? applied.variantResolutionPath ?? "<none>" : "<none>")} " +
                $"flags={string.Join(",", PartParitySkinRules.GhostFlags(proof).ToArray())}");
        }

        private static Quaternion ControlRotation(string controlId)
        {
            if (controlId == TestCommandPartParitySweep.ControlGhostRot90) return Quaternion.Euler(0f, 90f, 0f);
            if (controlId == TestCommandPartParitySweep.ControlGhostFlip) return Quaternion.Euler(180f, 0f, 0f);
            return Quaternion.identity;
        }

        /// <summary>Names another variant in both places KSP and the ghost builder read.</summary>
        private static void SetSnapshotVariant(ConfigNode snapshot, string variant)
        {
            foreach (ConfigNode p in snapshot.GetNodes("PART"))
            {
                p.SetValue("moduleVariantName", variant, true);
                foreach (ConfigNode m in p.GetNodes("MODULE"))
                    if (m.GetValue("name") == PartParityCaseRules.VariantModuleName)
                        m.SetValue("selectedVariant", variant, true);
            }
        }

        /// <summary>World distance between the first <c>ghost_part_*</c> transform and the real
        /// part (expected 0); null when the ghost has no such transform.</summary>
        private static double? GhostRootOffset(Transform ghostRoot, Transform partT)
        {
            foreach (Transform t in ghostRoot.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(PartParityStructure.GhostPartSegmentPrefix, StringComparison.Ordinal))
                    return (t.position - partT.position).magnitude;
            return null;
        }

        private static void CollectGhost(WaveCase wc, Transform partT)
        {
            GameObject root = wc.Ghost.Build.root;
            wc.GhostRenderers = new List<Renderer>(root.GetComponentsInChildren<Renderer>(true));
            var meshRenderers = new List<Renderer>();
            wc.GhostRecords = Records(partT, wc.GhostRenderers, wc.GhostLayers, meshRenderers);
        }

        /// <summary>
        /// The missing-mesh control's planted defect (design 4.5): a small cube parented to the
        /// REAL part, before its renderers and box are read, so the real side has a rendered
        /// mesh the ghost (built from the unchanged snapshot) lacks. Collider destroyed at once;
        /// the part's own first rendered material, so it lights like the part; the part's
        /// layer; centred on the +X face of the real renderer box at the box centre, wholly
        /// outside it (centre = face + edge / 2); edge from
        /// <see cref="PartParitySweepPlan.PlantedCubeEdge"/>. <c>DestroyCase</c> removes it on
        /// every exit (teardown and the step cleanup).
        /// </summary>
        private static void PlantCube(StepContext ctx, WaveCase wc, Part part)
        {
            Transform partT = part.transform;
            Renderer first = null;
            var box = new PartParityAabb();
            var renderers = new List<Renderer>(part.GetComponentsInChildren<Renderer>(true));
            foreach (PartParityRendererRecord r in Records(partT, renderers, null, null))
                if (r.Rendered) box.Encapsulate(PartParityFramingRule.BoundsOf(r));
            foreach (Renderer r in renderers)
            {
                if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.sharedMaterial == null) continue;
                first = r;
                break;
            }
            double edge = PartParitySweepPlan.PlantedCubeEdge(box.Size(), ctx.Capture.Tile);
            if (first == null || double.IsNaN(edge))
            {
                wc.Detail = wc.Detail ?? "planted cube: no rendered real renderer to size or light it";
                ParsekLog.Warn(PartParitySweepRunner.Tag, $"case {wc.CaseId}: planted cube not made ({wc.Detail})");
                return;
            }
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wc.PlantedCube = cube;
            Collider collider = cube.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            cube.name = PartParitySweepPlan.PlantedCubeName;
            cube.layer = first.gameObject.layer;
            cube.GetComponent<MeshRenderer>().sharedMaterial = first.sharedMaterial;
            double[] c = box.Center();
            var local = new Vector3((float)(box.Max[0] + edge / 2.0), (float)c[1], (float)c[2]);
            Transform t = cube.transform;
            t.SetParent(partT, false);
            t.localPosition = local;
            t.localRotation = Quaternion.identity;
            Vector3 ps = partT.lossyScale;
            float e = (float)edge;
            t.localScale = new Vector3(ps.x != 0f ? e / ps.x : e, ps.y != 0f ? e / ps.y : e, ps.z != 0f ? e / ps.z : e);
            string where = local.x.ToString("0.0000", Ic) + "," + local.y.ToString("0.0000", Ic) + "," + local.z.ToString("0.0000", Ic);
            wc.Flags.Add(FlagPlantedCubePrefix + "edge=" + edge.ToString("0.0000", Ic) + ";local=" + where);
            ParsekLog.Info(PartParitySweepRunner.Tag, $"case {wc.CaseId}: planted cube edge={edge.ToString("0.0000", Ic)}m " +
                $"local=({where}) box={FormatSize(box.Size())} material={first.sharedMaterial.name} layer={cube.layer.ToString(Ic)} " +
                $"tile={ctx.Capture.Tile.ToString(Ic)}");
        }

        private static void Frame(WaveCase wc)
        {
            wc.Framing = PartParityFramingRule.Decide(wc.RealRecords, wc.GhostRecords);
            foreach (var d in wc.Framing.Dropped) wc.Flags.Add(PartParityFramingRule.DroppedFlag(d));
            if (wc.Framing.GhostOutsideReal) wc.Flags.Add(PartParityFramingRule.FlagGhostOutsideRealBounds);
            if (wc.Ghost != null && !wc.RealLayers.SetEquals(wc.GhostLayers)) wc.Flags.Add(FlagGhostLayerDiffers);
            wc.Shared = wc.Framing.Shared.Copy();
            if (wc.Shared.IsEmpty)
            {
                wc.Flags.Add(FlagNoRenderedMesh);
                wc.Shared = PartParityAabb.FromMinMax(new[] { -0.5, -0.5, -0.5 }, new[] { 0.5, 0.5, 0.5 });
            }
            wc.OrthoSize = PartParityCaptureMath.OrthoSize(wc.Shared.Extents());
            int mask = 0;
            foreach (int l in wc.RealLayers) mask |= 1 << l;
            foreach (int l in wc.GhostLayers) mask |= 1 << l;
            wc.CullingMask = mask != 0 ? mask : 1;
        }

        // ----- renderer records -----

        /// <summary>One record per Mesh / SkinnedMesh renderer, part-local; every renderer's
        /// layer joins <paramref name="layers"/>. <paramref name="meshRenderersOut"/> (when
        /// given) receives the renderer of each record, in record order.</summary>
        internal static List<PartParityRendererRecord> Records(
            Transform partT, IList<Renderer> renderers, SortedSet<int> layers, List<Renderer> meshRenderersOut,
            HashSet<string> depthMaskNames = null)
        {
            var list = new List<PartParityRendererRecord>();
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                layers?.Add(r.gameObject.layer);
                PartParityRendererRecord rec = Record(partT, r);
                // The ghost builder's own skip rule (config mask transforms, then the
                // depth-mask shader), so a real renderer the builder leaves out by design is
                // marked as such whatever its shader is called.
                rec.DepthMask = GhostVisualBuilder.IsDepthMaskRenderer(r, depthMaskNames);
                list.Add(rec);
                meshRenderersOut?.Add(r);
            }
            return list;
        }

        private static PartParityRendererRecord Record(Transform partT, Renderer r)
        {
            Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            Material[] mats = r.sharedMaterials ?? new Material[0];
            var matNames = new string[mats.Length];
            var shaders = new string[mats.Length];
            var textures = new string[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                matNames[i] = m != null ? m.name : "<null>";
                shaders[i] = m != null && m.shader != null ? m.shader.name : "<null>";
                Texture t = m != null && m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                textures[i] = t != null ? t.name : "<none>";
            }
            Transform t0 = r.transform;
            Quaternion q = Quaternion.Inverse(partT.rotation) * t0.rotation;
            Vector3 lp = partT.InverseTransformPoint(t0.position);
            Vector3 ls = t0.lossyScale, ps = partT.lossyScale;
            var rec = new PartParityRendererRecord
            {
                Path = PartParityStructure.NormalizePath(RelativePath(partT, t0)),
                Kind = r is SkinnedMeshRenderer ? "SkinnedMeshRenderer" : "MeshRenderer",
                Mesh = mesh != null ? mesh.name : null,
                VertexCount = mesh != null ? mesh.vertexCount : 0,
                Materials = matNames,
                Shaders = shaders,
                MainTextures = textures,
                Rendered = r.gameObject.activeInHierarchy && r.enabled && !r.forceRenderingOff,
                LocalPosition = new double[] { lp.x, lp.y, lp.z },
                LocalRotation = new double[] { q.x, q.y, q.z, q.w },
                LocalScale = new double[] { Ratio(ls.x, ps.x), Ratio(ls.y, ps.y), Ratio(ls.z, ps.z) },
            };
            FillBounds(partT, r, mesh, rec);
            return rec;
        }

        private static double Ratio(float a, float b) => b != 0f ? a / (double)b : a;

        private static void FillBounds(Transform partT, Renderer r, Mesh mesh, PartParityRendererRecord rec)
        {
            bool skinned = r is SkinnedMeshRenderer;
            if (!skinned && mesh == null) return;
            Bounds b = skinned ? r.bounds : mesh.bounds;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = PartParityCaseAuthoring.Corner(b, i);
                // SkinnedMeshRenderers report world bounds (their local bounds are often stale).
                Vector3 local = partT.InverseTransformPoint(skinned ? c : r.transform.TransformPoint(c));
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
            rec.BoundsMin = new double[] { min.x, min.y, min.z };
            rec.BoundsMax = new double[] { max.x, max.y, max.z };
        }

        private static string RelativePath(Transform root, Transform t)
        {
            var segs = new List<string>();
            for (Transform cur = t; cur != null && cur != root; cur = cur.parent) segs.Add(cur.name);
            segs.Reverse();
            return string.Join("/", segs.ToArray());
        }

        // ----- one view -----

        internal static void CaptureView(StepContext ctx, WaveCase wc, string view)
        {
            Part part = wc.Real.rootPart;
            Transform ghostRoot = wc.Ghost != null && wc.Ghost.Build != null && wc.Ghost.Build.root != null
                ? wc.Ghost.Build.root.transform : null;
            // Re-read the real renderers at capture time (a module may have made one since the
            // records), leaving the co-located ghost's subtree out.
            var real = new List<Renderer>();
            foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                if (r != null && (ghostRoot == null || !r.transform.IsChildOf(ghostRoot))) real.Add(r);
            List<Renderer> showReal = wc.ControlId == TestCommandPartParitySweep.ControlEmpty ? new List<Renderer>() : real;
            List<Renderer> showGhost = wc.ControlId == TestCommandPartParitySweep.ControlRealVsReal ? real
                : wc.ControlId == TestCommandPartParitySweep.ControlEmpty ? new List<Renderer>()
                : wc.GhostRenderers;
            var all = new List<Renderer>(real);
            all.AddRange(wc.GhostRenderers);

            PartParityViewShot shot = ctx.Capture.CaptureView(part.transform, wc.Shared, wc.OrthoSize, view,
                all, showReal, showGhost, wc.CullingMask);
            if (shot.Error != null)
            {
                wc.Flags.Add(FlagCaptureErrorPrefix + view);
                wc.Detail = wc.Detail ?? view + ": " + shot.Error;
                ParsekLog.Warn(PartParitySweepRunner.Tag, $"case {wc.CaseId} view {view}: capture error {shot.Error}");
                return;
            }
            int tile = ctx.Capture.Tile;
            PartParityViewMeasures m = PartParityMetrics.Measure(shot.Real, shot.Ghost, tile, tile, shot.ProjectedBoxAreaPx);
            if ((m.TouchesBorderReal || m.TouchesBorderGhost) && !wc.Flags.Contains(FlagClippedAtBorder))
                wc.Flags.Add(FlagClippedAtBorder);
            wc.CapturedViews.Add(view);
            wc.RealTiles.Add(shot.Real);
            wc.GhostTiles.Add(shot.Ghost);
            wc.ViewRecords.Add(new PartParityViewRecord
            {
                View = view, Measures = m, RealMatte = shot.RealMatte, GhostMatte = shot.GhostMatte,
            });
        }

        // ----- files -----

        internal static string ResolveStatus(WaveCase wc)
        {
            if (wc.Status != null) return wc.Status;
            if (PartParitySkinRules.SkinNotApplied(wc.Skin))
                return TestCommandPartParitySweep.SetupSkinNotApplied;
            return wc.StillAnimating ? TestCommandPartParitySweep.SetupStillAnimating : TestCommandPartParitySweep.SetupOk;
        }

        internal static PartParityCaseSidecar BuildSidecar(StepContext ctx, WaveCase wc)
        {
            PartParityPartEntry e = wc.Entry;
            var s = new PartParityCaseSidecar
            {
                CaseId = wc.CaseId,
                Part = e?.Name ?? wc.Key?.Part,
                Title = e?.Title,
                Source = e?.Source ?? wc.Key?.Source,
                PartUrl = e?.PartUrl,
                Category = e?.Category,
                Skin = wc.Skin ?? PartParitySkinRules.NewProof(wc.SkinOption, wc.RealVariant, e?.B9Modules),
                State = wc.Key?.State ?? TestCommandPartParitySweep.DefaultState,
                Context = wc.Key?.Context ?? TestCommandPartParitySweep.DefaultContext,
                GhostRootOffsetM = wc.GhostRootOffsetM,
                LayersReal = ToArray(wc.RealLayers),
                LayersGhost = wc.Ghost != null || wc.ControlId == TestCommandPartParitySweep.ControlRealVsReal ? ToArray(wc.GhostLayers) : null,
                Views = wc.ViewRecords,
                Structure = wc.Framing?.Structure,
                Setup = new PartParitySetup { Status = ResolveStatus(wc), Detail = wc.Detail },
                Log = wc.Log,
            };
            if (e != null) { s.ModelUrls.AddRange(e.ModelUrls); s.Tags.AddRange(e.Tags); }
            if (wc.ControlId != null) s.Tags.Add(TagControl);
            s.Flags.AddRange(wc.Flags);
            if (wc.Ghost != null)
            {
                s.Flags.Add(FlagSeedEventsPrefix + wc.SeedEvents.ToString(Ic));
                s.Flags.Add(FlagSeedAppliedPrefix + wc.SeedApplied.ToString(Ic));
            }
            if (wc.Framing != null)
            {
                s.Framing = new PartParityFraming
                {
                    Real = wc.Framing.Real.ToBox(), Ghost = wc.Framing.Ghost.ToBox(),
                    Shared = wc.Shared?.ToBox(), OrthoSize = wc.OrthoSize,
                };
            }
            if (wc.RealTiles.Count > 0)
            {
                s.Atlas = new PartParityAtlas
                {
                    File = TestCommandPartParitySweep.AtlasFileName(wc.CaseId),
                    Thumb = TestCommandPartParitySweep.ThumbFileName(wc.CaseId),
                    Tile = ctx.Capture.Tile,
                    Rows = new[] { "real", "ghost" },
                    Cols = wc.CapturedViews.ToArray(),
                };
            }
            return s;
        }

        private static int[] ToArray(SortedSet<int> set)
        {
            if (set == null) return null;
            var a = new int[set.Count];
            set.CopyTo(a);
            return a;
        }

        /// <summary>The atlas, then the thumbnail; the caller writes the sidecar last.</summary>
        internal static void WritePictures(StepContext ctx, WaveCase wc, PartParityCaseSidecar s)
        {
            int tile = ctx.Capture.Tile;
            byte[] atlas = PartParityImage.BuildAtlas(
                new[] { wc.RealTiles.ToArray(), wc.GhostTiles.ToArray() }, wc.CapturedViews.Count, tile, out int w, out int h);
            FileIOUtils.SafeWriteBytes(PartParityPng.EncodeRgba(w, h, atlas),
                Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.AtlasFileName(wc.CaseId)), PartParitySweepRunner.Tag);
            int thumbView = Math.Max(0, wc.CapturedViews.IndexOf(TestCommandPartParitySweep.ViewSideA));
            byte[] thumb = PartParityImage.BuildThumbnail(wc.RealTiles[thumbView], wc.GhostTiles[thumbView], tile, out int tw, out int th);
            FileIOUtils.SafeWriteBytes(PartParityPng.EncodeRgba(tw, th, thumb),
                Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.ThumbFileName(wc.CaseId)), PartParitySweepRunner.Tag);
        }

        /// <summary>The per-case Verbose line.</summary>
        internal static string DescribeCase(WaveCase wc, PartParityCaseSidecar s)
        {
            var summary = PartParitySweepPlan.Summarize(wc.Index, s);
            var sb = new StringBuilder();
            sb.Append("case ").Append(wc.CaseId).Append(": status=").Append(s.Setup?.Status)
                .Append(" part=").Append(s.Part ?? "<none>")
                .Append(" skin=").Append(s.Skin?.Requested ?? "<default>").Append("/").Append(s.Skin?.Real ?? "<none>")
                .Append(" snapshotSkin=").Append(s.Skin?.Snapshot ?? "<none>")
                .Append(" ghostSkin=").Append(s.Skin?.Ghost ?? "<none>")
                .Append(" skinModule=").Append(s.Skin?.Module ?? "<none>")
                .Append(" alongTrack=").Append(wc.AlongTrackM.ToString("0", Ic)).Append("m")
                .Append(" views=").Append(wc.CapturedViews.Count.ToString(Ic))
                .Append(" seedEvents=").Append(wc.SeedEvents.ToString(Ic))
                .Append(" seedApplied=").Append(wc.SeedApplied.ToString(Ic));
            PartParityStructureDiff d = s.Structure;
            if (d != null)
                sb.Append(" realRenderers=").Append(d.RealRenderers.ToString(Ic))
                    .Append(" ghostRenderers=").Append(d.GhostRenderers.ToString(Ic))
                    .Append(" pairs=").Append(d.Pairs.ToString(Ic))
                    .Append(" missingRendered=").Append((summary.MissingRendered ?? 0).ToString(Ic))
                    .Append(" extraRendered=").Append((summary.ExtraRendered ?? 0).ToString(Ic));
            AppendFraming(sb, wc);
            sb.Append(" maxShapeBlob=").Append(summary.MaxShapeBlobPx.HasValue ? summary.MaxShapeBlobPx.Value.ToString(Ic) : "-")
                .Append(" maxColorBlob=").Append(summary.MaxColorBlobPx.HasValue ? summary.MaxColorBlobPx.Value.ToString(Ic) : "-")
                .Append(" minIou=").Append(summary.MinIou.HasValue ? summary.MinIou.Value.ToString("0.0000", Ic) : "-")
                .Append(" ghostRootOffset=").Append(wc.GhostRootOffsetM.HasValue ? wc.GhostRootOffsetM.Value.ToString("0.0000", Ic) : "-")
                .Append(" logLines=").Append(wc.Log.Count.ToString(Ic));
            return sb.ToString();
        }

        private static void AppendFraming(StringBuilder sb, WaveCase wc)
        {
            if (wc.Framing == null) return;
            sb.Append(" box=").Append(FormatSize(wc.Shared?.Size()))
                .Append(" realBox=").Append(FormatSize(wc.Framing.Real.Size()))
                .Append(" ghostBox=").Append(FormatSize(wc.Framing.Ghost.Size()))
                .Append(" ortho=").Append(wc.OrthoSize.ToString("0.000", Ic))
                .Append(" mask=0x").Append(wc.CullingMask.ToString("X", Ic))
                .Append(" dropped=").Append(wc.Framing.Dropped.Count.ToString(Ic));
            if (wc.Framing.Dropped.Count > 0)
            {
                sb.Append(" [");
                for (int i = 0; i < wc.Framing.Dropped.Count; i++)
                {
                    if (i > 0) sb.Append("; ");
                    var dr = wc.Framing.Dropped[i];
                    sb.Append(dr.Key).Append(' ').Append(FormatSize(dr.Size));
                }
                sb.Append(']');
            }
        }

        private static string FormatSize(double[] s)
            => s == null ? "none" : s[0].ToString("0.000", Ic) + "x" + s[1].ToString("0.000", Ic) + "x" + s[2].ToString("0.000", Ic);
    }

    /// <summary>Rebuilds the sweep manifest at the end of every step from every sidecar the
    /// step knows (read back from disk or written this step).</summary>
    internal static class PartParityManifestBuilder
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal static void Write(StepContext ctx, List<PartParityControlRecord> controls, bool done, int complete, int faults)
        {
            var mf = new PartParityManifest
            {
                Label = ctx.Args.Label,
                Profile = ProfileName(),
                DllHash = PartParitySweepRunner.FileSha256(typeof(PartParitySweepRunner).Assembly.Location),
                Tile = ctx.Args.Tile,
                Views = new List<string>(ctx.Args.Views),
                Controls = controls,
                Exclusions = ctx.Exclusions,
            };
            mf.GameDataMods.AddRange(GameDataMods());
            AddSettings(ctx, mf.Settings);
            var crashed = new List<string>();
            foreach (var p in ctx.Planned)
            {
                if (ctx.Known.TryGetValue(p.CaseId, out PartParityCaseSidecar s))
                {
                    PartParityCaseSummary summary = PartParitySweepPlan.Summarize(p.Index, s);
                    summary.Skin = p.Key.Skin;
                    mf.Cases.Add(summary);
                    if (s.Setup?.Status == TestCommandPartParitySweep.SetupCrashed) crashed.Add(p.CaseId);
                }
                else
                {
                    mf.Cases.Add(new PartParityCaseSummary
                    {
                        Index = p.Index, CaseId = p.CaseId, Part = p.Key.Part, Source = p.Key.Source,
                        Skin = p.Key.Skin, State = p.Key.State, Context = p.Key.Context,
                    });
                }
            }
            mf.Progress = new PartParityProgress
            {
                Complete = done, Cases = ctx.Planned.Count, CompleteCases = complete, SetupFaults = faults,
                Controls = TestCommandPartParitySweep.ControlsState(ctx.Args.Controls, OutcomesOf(controls)),
                Crashed = crashed,
                HostDamaged = ctx.HostDamage != null,
                HostDamage = ctx.HostDamage?.Reason,
                HostDamageDetail = ctx.HostDamage?.Detail,
            };
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.ManifestFileName);
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(PartParityJson.WriteManifest(mf)), path, PartParitySweepRunner.Tag);
            ParsekLog.Info(PartParitySweepRunner.Tag, $"manifest: {path} cases={mf.Cases.Count.ToString(Ic)} complete={complete.ToString(Ic)} " +
                $"controls={mf.Progress.Controls} exclusions={mf.Exclusions.Count.ToString(Ic)} mods={mf.GameDataMods.Count.ToString(Ic)} " +
                $"hostDamage={mf.Progress.HostDamage ?? PartParityHostIntegrity.DamageNone}");
        }

        private static List<string> OutcomesOf(List<PartParityControlRecord> controls)
        {
            var o = new List<string>();
            foreach (var c in controls) o.Add(c.Outcome);
            return o;
        }

        private static string ProfileName()
        {
            string root = (KSPUtil.ApplicationRootPath ?? string.Empty).TrimEnd('/', '\\');
            return Path.GetFileName(root);
        }

        private static List<string> GameDataMods()
        {
            var mods = new List<string>();
            string gd = Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty, "GameData");
            if (!Directory.Exists(gd)) return mods;
            foreach (string d in Directory.GetDirectories(gd)) mods.Add(Path.GetFileName(d));
            mods.Sort(StringComparer.Ordinal);
            return mods;
        }

        private static void AddSettings(StepContext ctx, List<KeyValuePair<string, string>> s)
        {
            PartParitySweepArgs a = ctx.Args;
            s.Add(Kv("filter", a.Filter));
            s.Add(Kv("source", a.Sources != null ? string.Join(",", a.Sources.ToArray()) : "*"));
            s.Add(Kv("skins", a.Skins == PartParitySkinSelection.All ? "all" : "default"));
            s.Add(Kv("shard", a.ShardK.ToString(Ic) + "/" + a.ShardN.ToString(Ic)));
            s.Add(Kv("limit", a.Limit.ToString(Ic)));
            s.Add(Kv("controls", a.Controls ? "true" : "false"));
            s.Add(Kv("maxSeconds", a.MaxSeconds.ToString(Ic)));
            s.Add(Kv("masterTextureLimit", QualitySettings.masterTextureLimit.ToString(Ic)));
            s.Add(Kv("pixelLightCount", QualitySettings.pixelLightCount.ToString(Ic)));
            s.Add(Kv("antiAliasing", QualitySettings.antiAliasing.ToString(Ic)));
            s.Add(Kv("colorSpace", QualitySettings.activeColorSpace.ToString()));
            s.Add(Kv("graphicsDevice", SystemInfo.graphicsDeviceType.ToString()));
            s.Add(Kv("unity", Application.unityVersion));
            s.Add(Kv("skinsNote", a.Skins == PartParitySkinSelection.All
                ? "every ModulePartVariants VARIANT and every ModuleB9PartSwitch SUBTYPE (one module at a time); other switchers default skin only"
                : "the base skin of each part only"));
        }

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v ?? string.Empty);
    }
}
