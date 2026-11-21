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
        internal const string FlagMissingMeshTargetPrefix = "control-hidden:";
        internal const string TagControl = "control";

        /// <summary>Ghost root offset above this (metres) is flagged (design 4.2 step 3).</summary>
        internal const double GhostRootOffsetToleranceM = 0.001;

        // ----- prepare -----

        internal static void Prepare(StepContext ctx, WaveCase wc)
        {
            Part part = wc.Real.rootPart;
            Transform partT = part.transform;
            wc.Skin = new PartParitySkinProof
            {
                Module = wc.RealVariant != null ? PartParityCaseRules.VariantModuleName : null,
                Requested = wc.RealVariant,
                Real = PartParityCaseAuthoring.LiveVariantName(part),
            };

            var realRenderers = new List<Renderer>(part.GetComponentsInChildren<Renderer>(true));
            wc.RealRecords = Records(partT, realRenderers, wc.RealLayers, null);

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
            wc.Skin.Snapshot = snapPart != null ? snapPart.GetValue("moduleVariantName") : null;

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
            wc.GhostMeshRenderers = meshRenderers;
        }

        /// <summary>
        /// The missing-mesh control's MEASURED pick (design 4.5), run after the ghost is built
        /// and framed, before any view, at the end of a frame: the whole ghost is drawn once
        /// per view through the capture primitive (same camera, box, light rig and clears as
        /// the views), and again with each rendered ghost renderer hidden in turn; a
        /// renderer's silhouette contribution is the difference. The pure size rule
        /// (<see cref="PartParitySweepPlan.ChooseContributionRenderer"/>) picks by those pixel
        /// counts only, never by an outcome. The pick is forced off and the ghost records
        /// re-read so the structure shows it; the views keep the box, ortho size and culling
        /// mask the counts were measured in. No renderer in the window (or a failure) flags
        /// the case <c>control-no-subject</c> with the counts.
        /// </summary>
        internal static void MeasuredHide(StepContext ctx, WaveCase wc)
        {
            string subject = wc.Entry?.Name ?? wc.Key?.Part;
            if (wc.Ghost?.Build?.root == null || wc.GhostMeshRenderers == null || wc.Shared == null)
            {
                NoMeasuredSubject(wc, subject, "no ghost to measure", null);
                return;
            }
            Part part = wc.Real.rootPart;
            Transform ghostRoot = wc.Ghost.Build.root.transform;
            var all = new List<Renderer>();
            foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                if (r != null && !r.transform.IsChildOf(ghostRoot)) all.Add(r);
            all.AddRange(wc.GhostRenderers);

            var candidates = new List<int>();
            var labels = new List<string>();
            for (int i = 0; i < wc.GhostRecords.Count; i++)
            {
                if (!wc.GhostRecords[i].Rendered) continue;
                candidates.Add(i);
                labels.Add(wc.GhostRecords[i].Path);
            }
            var sets = new List<IList<Renderer>> { wc.GhostRenderers };
            foreach (int i in candidates)
            {
                Renderer hidden = wc.GhostMeshRenderers[i];
                var rest = new List<Renderer>(wc.GhostRenderers.Count);
                foreach (Renderer r in wc.GhostRenderers) if (r != hidden) rest.Add(r);
                sets.Add(rest);
            }

            int views = wc.Views.Count;
            var full = new int[views];
            var without = new List<int[]>();
            for (int k = 0; k < candidates.Count; k++) without.Add(new int[views]);
            for (int v = 0; v < views; v++)
            {
                int[] px = ctx.Capture.MeasureCoverage(part.transform, wc.Shared, wc.OrthoSize, wc.Views[v],
                    all, sets, wc.CullingMask, out string error);
                if (px == null)
                {
                    NoMeasuredSubject(wc, subject, "measure " + wc.Views[v] + ": " + error, null);
                    return;
                }
                full[v] = px[0];
                for (int k = 0; k < candidates.Count; k++) without[k][v] = px[k + 1];
            }
            var contribution = new List<int[]>();
            foreach (int[] w in without) contribution.Add(PartParitySweepPlan.ContributionPx(full, w));
            string counts = PartParitySweepPlan.FormatMeasuredCounts(wc.Views, full, labels, contribution);
            ParsekLog.Verbose(PartParitySweepRunner.Tag, $"case {wc.CaseId}: missing-mesh contribution counts " +
                $"subject={subject ?? "<none>"} candidates={candidates.Count.ToString(Ic)} {counts}");

            int chosen = PartParitySweepPlan.ChooseContributionRenderer(contribution, full);
            if (chosen < 0)
            {
                NoMeasuredSubject(wc, subject, "no ghost renderer's silhouette contribution in the size window", counts);
                return;
            }
            int target = candidates[chosen];
            wc.GhostMeshRenderers[target].enabled = false;
            string pick = PartParitySweepPlan.FormatMeasuredPick(subject, wc.GhostRecords[target].Path, wc.Views,
                contribution[chosen], full, null);
            wc.Flags.Add(FlagMissingMeshTargetPrefix + wc.GhostRecords[target].Path);
            wc.Flags.Add(PartParitySweepPlan.FlagControlPickPrefix + pick);
            ParsekLog.Info(PartParitySweepRunner.Tag, $"case {wc.CaseId}: missing-mesh measured pick {pick} " +
                "(prefab order " + (wc.MissingMeshSubject != null ? wc.MissingMeshSubject.Describe(wc.Views) : "<none>") + ")");
            wc.GhostLayers.Clear();
            wc.GhostRecords = Records(part.transform, wc.GhostRenderers, wc.GhostLayers, null);
            wc.Framing = PartParityFramingRule.Decide(wc.RealRecords, wc.GhostRecords);
        }

        private static void NoMeasuredSubject(WaveCase wc, string subject, string why, string counts)
        {
            wc.Flags.Add(PartParitySweepPlan.FlagControlNoSubject);
            wc.Detail = wc.Detail ?? "no-subject: " + why;
            ParsekLog.Info(PartParitySweepRunner.Tag, $"case {wc.CaseId}: missing-mesh measured pick " +
                PartParitySweepPlan.FormatMeasuredPick(subject, null, wc.Views, null, null, why) +
                " (control bad, no-subject)" + (counts != null ? " counts " + counts : string.Empty));
        }

        /// <summary>
        /// One missing-mesh candidate from a part prefab (design 4.5): its renderer records in
        /// prefab-root space, with <c>Rendered</c> read from the prefab's OWN active chain (the
        /// prefab root is inactive, so <c>activeInHierarchy</c> is false everywhere), whether it
        /// carries any Animation, and its PartModule names (type names and module names).
        /// Null when the part has no prefab.
        /// </summary>
        internal static PartParityMissingMeshCandidate PrefabCandidate(string name, AvailablePart info)
        {
            Part prefab = info?.partPrefab;
            if (prefab == null) return null;
            Transform root = prefab.transform;
            var renderers = new List<Renderer>(prefab.GetComponentsInChildren<Renderer>(true));
            var meshRenderers = new List<Renderer>();
            List<PartParityRendererRecord> records = Records(root, renderers, null, meshRenderers);
            for (int i = 0; i < records.Count; i++)
            {
                Renderer r = meshRenderers[i];
                records[i].Rendered = r.enabled && !r.forceRenderingOff && ActiveBelow(r.transform, root);
            }
            var c = new PartParityMissingMeshCandidate
            {
                Subject = name,
                HasAnimation = prefab.GetComponentsInChildren<Animation>(true).Length > 0,
                Records = records,
            };
            foreach (PartModule m in prefab.GetComponents<PartModule>())
                if (m != null) c.ModuleNames.Add(m.GetType().Name);
            if (prefab.Modules != null)
                foreach (PartModule m in prefab.Modules)
                    if (m != null && !string.IsNullOrEmpty(m.moduleName) && !c.ModuleNames.Contains(m.moduleName))
                        c.ModuleNames.Add(m.moduleName);
            return c;
        }

        private static bool ActiveBelow(Transform t, Transform root)
        {
            for (Transform cur = t; cur != null && cur != root; cur = cur.parent)
                if (!cur.gameObject.activeSelf) return false;
            return true;
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
            Transform partT, IList<Renderer> renderers, SortedSet<int> layers, List<Renderer> meshRenderersOut)
        {
            var list = new List<PartParityRendererRecord>();
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                layers?.Add(r.gameObject.layer);
                list.Add(Record(partT, r));
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
            if (wc.Skin != null && wc.Skin.Requested != null && wc.Skin.Real != wc.Skin.Requested)
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
                Skin = wc.Skin ?? new PartParitySkinProof { Requested = wc.RealVariant },
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
            if (wc.ControlId != null && wc.GhostVariant != null) s.Skin.Ghost = wc.GhostVariant;
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
            };
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.ManifestFileName);
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(PartParityJson.WriteManifest(mf)), path, PartParitySweepRunner.Tag);
            ParsekLog.Info(PartParitySweepRunner.Tag, $"manifest: {path} cases={mf.Cases.Count.ToString(Ic)} complete={complete.ToString(Ic)} " +
                $"controls={mf.Progress.Controls} exclusions={mf.Exclusions.Count.ToString(Ic)} mods={mf.GameDataMods.Count.ToString(Ic)}");
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
            s.Add(Kv("skinsNote", "P2 captures the default skin only"));
        }

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v ?? string.Empty);
    }
}
