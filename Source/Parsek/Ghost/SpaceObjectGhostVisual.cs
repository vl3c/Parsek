using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// What the ghost builder does with one asteroid / comet part: the chosen path, the rock's
    /// radius when known, and for the stock-generator path the shared mesh and material.
    /// </summary>
    internal sealed class RockGhostPlan
    {
        public SpaceObjectKind Kind;
        public SpaceObjectRockSpec Spec;
        public RockGhostPath Path;
        public float Radius;
        public bool RadiusKnown;
        public Mesh Mesh;
        public Material Material;
        public MaterialPropertyBlock PropertyBlock;
        public string CacheToken;
        public string Reason;

        /// The generated rock replaces stock's placeholder, as stock's OnStart destroys it.
        public bool ReplacesPlaceholder => Path == RockGhostPath.StockGenerator;
    }

    /// <summary>
    /// The live half of the asteroid / comet ghost geometry (pure half:
    /// <see cref="SpaceObjectGhostModel"/>). On a cache miss it repeats stock's
    /// ModuleAsteroid / ModuleComet OnStart draw on a DETACHED object: seed Unity's Random with
    /// the recorded seed, draw the radius, call the procedural prefab's public
    /// <c>Generate(seed, radius, parent: null, ...)</c>, copy the visual mesh, and destroy the
    /// generated object before its collider coroutine (SpaceObjectCollider.Setup starts one
    /// that first yields a frame) can run, so no collider is ever built. Unity's Random state
    /// is restored around the draw. One mesh per (part, URL, seed, secondary) is cached for
    /// the process and shared by every ghost of that rock; the live part is never touched.
    /// A comet's tail and coma are scaled-space VFX driven by a live CometVessel
    /// (CometVFXController reads hostVessel, its orbit driver and the sun), so a comet ghost
    /// gets the rock at the right size without them.
    /// </summary>
    internal static class SpaceObjectGhostVisual
    {
        internal const string RockObjectName = "ParsekRock";
        private const string Tag = "GhostVisual";
        private const string CometVfxSkipped = "skipped-needs-live-comet-vessel";

        private sealed class CachedRock
        {
            public Mesh Mesh;
            public Material Material;
            public MaterialPropertyBlock PropertyBlock;
            public float Radius;
            // A generation that failed is remembered too, so a rebuilt ghost of the same rock
            // goes straight to the fallback instead of re-running the generator.
            public bool Failed;
            public bool RadiusKnown;
            public string FailReason;
        }

        private static readonly Dictionary<string, CachedRock> rockCache =
            new Dictionary<string, CachedRock>();

        // ProceduralComet.Generate sorts its mod list AFTER using it, so the first call on a
        // prefab in a session can draw in a different order from every later one. One throwaway
        // generation per comet prefab puts the ghost on the order stock's own comets use.
        private static readonly HashSet<string> warmedCometPrefabs = new HashSet<string>();

        /// <summary>Which stock space-object module the part prefab carries, with its radius range.</summary>
        internal static SpaceObjectKind ClassifyPrefab(Part prefab, out float minMultiplier, out float maxMultiplier)
        {
            minMultiplier = SpaceObjectGhostModel.DefaultMinRadiusMultiplier;
            maxMultiplier = SpaceObjectGhostModel.DefaultMaxRadiusMultiplier;
            if (prefab == null) return SpaceObjectKind.None;
            ModuleAsteroid asteroid = prefab.FindModuleImplementing<ModuleAsteroid>();
            if (asteroid != null)
            {
                SpaceObjectGhostModel.ResolveRadiusMultipliers(true,
                    asteroid.minRadiusMultiplier, asteroid.maxRadiusMultiplier,
                    out minMultiplier, out maxMultiplier);
                return SpaceObjectKind.Asteroid;
            }
            ModuleComet comet = prefab.FindModuleImplementing<ModuleComet>();
            if (comet != null)
            {
                SpaceObjectGhostModel.ResolveRadiusMultipliers(true,
                    comet.minRadiusMultiplier, comet.maxRadiusMultiplier,
                    out minMultiplier, out maxMultiplier);
                return SpaceObjectKind.Comet;
            }
            return SpaceObjectKind.None;
        }

        /// <summary>
        /// Plans the ghost geometry of one part. Null for a part that is not an asteroid or comet.
        /// </summary>
        internal static RockGhostPlan TryPlan(Part prefab, ConfigNode partNode, string partName)
        {
            float minMultiplier, maxMultiplier;
            SpaceObjectKind kind = ClassifyPrefab(prefab, out minMultiplier, out maxMultiplier);
            if (kind == SpaceObjectKind.None)
                return null;

            var plan = new RockGhostPlan { Kind = kind, Path = RockGhostPath.PrefabLump, CacheToken = "none" };
            SpaceObjectRockSpec spec;
            string reason;
            if (!SpaceObjectGhostModel.TryParseRockSpec(partNode, kind, out spec, out reason))
            {
                plan.Reason = reason;
                return plan;
            }
            plan.Spec = spec;

            string key = SpaceObjectGhostModel.BuildCacheKey(partName, spec);
            CachedRock cached;
            if (rockCache.TryGetValue(key, out cached) && cached != null)
            {
                if (cached.Failed)
                {
                    plan.Radius = cached.Radius;
                    plan.RadiusKnown = cached.RadiusKnown;
                    plan.Reason = cached.FailReason;
                    plan.CacheToken = "hit-failed";
                    plan.Path = SpaceObjectGhostModel.ChoosePath(false, cached.RadiusKnown);
                    return plan;
                }
                if (cached.Mesh != null)
                {
                    FillFromCache(plan, cached, "hit");
                    return plan;
                }
            }

            if (!SpaceObjectGhostModel.UsesStockGenerator(kind, spec.PrefabUrl))
            {
                plan.Reason = "non-procedural-prefab";
                return plan;
            }

            ProceduralSpaceObject proceduralPrefab = LoadProceduralPrefab(kind, spec.PrefabUrl);
            if (proceduralPrefab == null)
            {
                plan.Reason = "procedural-prefab-not-found";
                return plan;
            }

            CachedRock built = GenerateRock(kind, proceduralPrefab, spec, minMultiplier, maxMultiplier, plan);
            if (built != null)
            {
                rockCache[key] = built;
                FillFromCache(plan, built, "miss");
            }
            else
            {
                rockCache[key] = new CachedRock
                {
                    Failed = true,
                    Radius = plan.Radius,
                    RadiusKnown = plan.RadiusKnown,
                    FailReason = plan.Reason,
                };
                plan.CacheToken = "miss";
            }
            plan.Path = SpaceObjectGhostModel.ChoosePath(plan.Mesh != null, plan.RadiusKnown);
            return plan;
        }

        private static void FillFromCache(RockGhostPlan plan, CachedRock cached, string token)
        {
            plan.Mesh = cached.Mesh;
            plan.Material = cached.Material;
            plan.PropertyBlock = cached.PropertyBlock;
            plan.Radius = cached.Radius;
            plan.RadiusKnown = true;
            plan.CacheToken = token;
            plan.Path = RockGhostPath.StockGenerator;
        }

        private static ProceduralSpaceObject LoadProceduralPrefab(SpaceObjectKind kind, string url)
        {
            if (kind == SpaceObjectKind.Comet)
                return Resources.Load<ProceduralComet>(url);
            return Resources.Load<ProceduralAsteroid>(url);
        }

        /// <summary>
        /// Stock's OnStart draw on a detached object. Sets plan.Radius / RadiusKnown as soon as
        /// the radius is drawn, so a generator failure can still fall back to the scaled
        /// placeholder. Returns null when no mesh came out.
        /// </summary>
        private static CachedRock GenerateRock(SpaceObjectKind kind, ProceduralSpaceObject proceduralPrefab,
            SpaceObjectRockSpec spec, float minMultiplier, float maxMultiplier, RockGhostPlan plan)
        {
            UnityEngine.Random.State savedRandom = UnityEngine.Random.state;
            PSpaceObject generated = null;
            try
            {
                if (kind == SpaceObjectKind.Comet && !warmedCometPrefabs.Contains(spec.PrefabUrl))
                {
                    PSpaceObject warm = InvokeGenerator(kind, proceduralPrefab, spec.Seed, proceduralPrefab.radius, false);
                    DiscardGenerated(warm);
                    warmedCometPrefabs.Add(spec.PrefabUrl);
                }

                UnityEngine.Random.InitState(spec.Seed);
                float radius = proceduralPrefab.radius * UnityEngine.Random.Range(minMultiplier, maxMultiplier);
                plan.Radius = radius;
                plan.RadiusKnown = !float.IsNaN(radius) && radius > 0f;

                generated = InvokeGenerator(kind, proceduralPrefab, spec.Seed, radius, spec.IsSecondary);
                return CaptureVisual(generated, spec, radius, plan);
            }
            catch (Exception ex)
            {
                plan.Reason = "generator-threw-" + ex.GetType().Name;
                ParsekLog.WarnRateLimited(Tag, "rock-generator-threw-" + spec.PrefabUrl,
                    "RockGhost generator failed for url=" + spec.PrefabUrl + " seed="
                    + spec.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally
            {
                DiscardGenerated(generated);
                UnityEngine.Random.state = savedRandom;
            }
        }

        private static PSpaceObject InvokeGenerator(SpaceObjectKind kind, ProceduralSpaceObject proceduralPrefab,
            int seed, float radius, bool isSecondary)
        {
            // The rangefinder only feeds SpaceObjectCollider's coroutine, which never runs here.
            Func<Transform, float> rangefinder = t => float.MaxValue;
            Callback onComplete = () => { };
            if (kind == SpaceObjectKind.Comet)
            {
                return ((ProceduralComet)proceduralPrefab).Generate(
                    seed, radius, null, rangefinder, onComplete, false, isSecondary);
            }
            return ((ProceduralAsteroid)proceduralPrefab).Generate(
                seed, radius, null, rangefinder, onComplete, isSecondary);
        }

        private static CachedRock CaptureVisual(PSpaceObject generated, SpaceObjectRockSpec spec, float radius,
            RockGhostPlan plan)
        {
            if (generated == null)
            {
                plan.Reason = "generator-returned-null";
                return null;
            }
            Renderer visualRenderer = generated.VisualRenderer;
            MeshFilter visualFilter = visualRenderer != null ? visualRenderer.GetComponent<MeshFilter>() : null;
            if (visualFilter == null || visualFilter.sharedMesh == null)
            {
                plan.Reason = "generator-made-no-visual";
                return null;
            }

            Mesh copy = UnityEngine.Object.Instantiate(visualFilter.sharedMesh);
            copy.name = "ParsekRockGhost_" + spec.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            copy.hideFlags = HideFlags.DontUnloadUnusedAsset;

            MaterialPropertyBlock block = null;
            if (spec.IsSecondary)
            {
                block = new MaterialPropertyBlock();
                visualRenderer.GetPropertyBlock(block);
                if (block.isEmpty)
                    block = null;
            }

            return new CachedRock
            {
                Mesh = copy,
                Material = visualRenderer.sharedMaterial,
                PropertyBlock = block,
                Radius = radius,
            };
        }

        private static void DiscardGenerated(PSpaceObject generated)
        {
            if (generated == null) return;
            // Deactivating stops the collider coroutine at once; the destroy then frees the
            // generated meshes (PSpaceObject.OnDestroy destroys them).
            generated.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(generated.gameObject);
        }

        /// <summary>Whether a prefab renderer sits on or under stock's placeholder transform.</summary>
        internal static bool IsUnderPlaceholder(Transform rendererTransform, Transform modelRoot)
        {
            Transform cur = rendererTransform;
            while (cur != null && cur != modelRoot)
            {
                if (SpaceObjectGhostModel.IsPlaceholderName(cur.name))
                    return true;
                cur = cur.parent;
            }
            return false;
        }

        /// <summary>
        /// Applies the plan to a ghost part and logs the one RockGhost line. Returns true when
        /// it added the rock as a visual of its own (the stock-generator path); the scaled and
        /// plain placeholder paths ride on the placeholder clone the builder already made.
        /// </summary>
        internal static bool Attach(RockGhostPlan plan, Transform partRoot, Transform modelNode,
            string partName, uint persistentId)
        {
            if (plan == null || partRoot == null)
                return false;

            bool addedVisual = false;
            if (plan.Path == RockGhostPath.StockGenerator && plan.Mesh != null)
            {
                // Stock generates the rock at the part transform with identity pose and then
                // keeps its world pose when reparenting, so it sits at the part origin.
                var rock = new GameObject(RockObjectName);
                rock.transform.SetParent(partRoot, false);
                rock.AddComponent<MeshFilter>().sharedMesh = plan.Mesh;
                var renderer = rock.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = plan.Material;
                if (plan.PropertyBlock != null)
                    renderer.SetPropertyBlock(plan.PropertyBlock);
                addedVisual = true;
            }
            else if (plan.Path == RockGhostPath.ScaledPrefab)
            {
                ScalePlaceholderClone(plan, modelNode);
            }

            string line = SpaceObjectGhostModel.FormatBuiltLine(plan.Path, plan.Kind, partName, persistentId,
                plan.Spec, plan.Radius, plan.RadiusKnown, plan.CacheToken, plan.Reason,
                plan.Kind == SpaceObjectKind.Comet ? CometVfxSkipped : null);
            if (plan.CacheToken == "miss" || plan.Path != RockGhostPath.StockGenerator)
                ParsekLog.InfoRateLimited(Tag, "rock-ghost-" + persistentId + "-"
                    + SpaceObjectGhostModel.PathToken(plan.Path) + "-" + (plan.CacheToken ?? ""), line, 60.0);
            else
                ParsekLog.VerboseRateLimited(Tag, "rock-ghost-hit-" + persistentId, line, 60.0);
            return addedVisual;
        }

        private static Transform FindPlaceholder(Transform node)
        {
            if (node == null) return null;
            if (SpaceObjectGhostModel.IsPlaceholderName(node.name)) return node;
            for (int i = 0; i < node.childCount; i++)
            {
                Transform found = FindPlaceholder(node.GetChild(i));
                if (found != null) return found;
            }
            return null;
        }

        private static void ScalePlaceholderClone(RockGhostPlan plan, Transform modelNode)
        {
            Transform placeholder = FindPlaceholder(modelNode);
            MeshFilter filter = placeholder != null ? placeholder.GetComponent<MeshFilter>() : null;
            if (filter == null || filter.sharedMesh == null)
            {
                plan.Reason = (plan.Reason ?? "") + (plan.Reason != null ? "," : "") + "no-placeholder-clone";
                return;
            }
            Vector3 extents = Vector3.Scale(filter.sharedMesh.bounds.extents, placeholder.lossyScale);
            float factor = SpaceObjectGhostModel.ComputePlaceholderScale(plan.Radius, extents.x, extents.y, extents.z);
            placeholder.localScale = placeholder.localScale * factor;
        }
    }
}
