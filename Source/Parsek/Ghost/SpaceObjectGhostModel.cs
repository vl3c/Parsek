using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>Which stock procedural space object a ghost part stands for.</summary>
    internal enum SpaceObjectKind
    {
        None = 0,
        Asteroid = 1,
        Comet = 2,
    }

    /// <summary>How a ghost asteroid / comet part got its geometry.</summary>
    internal enum RockGhostPath
    {
        /// The rock mesh stock's own generator builds from the recorded seed.
        StockGenerator = 0,
        /// The part prefab's placeholder lump, scaled to the rock's radius.
        ScaledPrefab = 1,
        /// The part prefab's placeholder lump at scale 1 (no radius could be derived).
        PrefabLump = 2,
    }

    /// <summary>
    /// The persisted rock fields of one ghost snapshot part: what stock's ModuleAsteroid /
    /// ModuleComet need to rebuild the same rock (seed, procedural prefab URL, secondary state).
    /// </summary>
    internal sealed class SpaceObjectRockSpec
    {
        public SpaceObjectKind Kind;
        public int Seed;
        public string PrefabUrl;
        public string PrefabUrlSource;
        public bool IsSecondary;
    }

    /// <summary>
    /// Pure decisions for the ghost geometry of a grabbed asteroid or comet. Stock builds the
    /// rock mesh at runtime in ModuleAsteroid.OnStart / ModuleComet.OnStart (decompiled, KSP
    /// 1.12.5): it seeds UnityEngine.Random with the persisted <c>seed</c>, draws
    /// <c>radius = prefab.radius * Random.Range(minRadiusMultiplier, maxRadiusMultiplier)</c>,
    /// loads the procedural prefab at the persisted <c>prefabBaseURL</c>
    /// (<c>Procedural/PA_&lt;class&gt;</c> / <c>Procedural/PC_&lt;class&gt;</c>), calls its
    /// <c>Generate(seed, radius, ...)</c> and destroys the part's <c>potatoroid</c> placeholder.
    /// A ghost clones the placeholder unless it repeats that; this class holds the snapshot
    /// parse, the path choice and the scale math, with no Unity call.
    /// </summary>
    internal static class SpaceObjectGhostModel
    {
        internal const string AsteroidModuleName = "ModuleAsteroid";
        internal const string CometModuleName = "ModuleComet";

        /// The placeholder transform stock's OnStart destroys (part.FindModelTransform("potatoroid")).
        internal const string StockPlaceholderTransformName = "potatoroid";

        internal const string AsteroidUrlPrefix = "Procedural/PA_";
        internal const string CometUrlPrefix = "Procedural/PC_";

        /// Stock's DiscoveryInfo default objectSize when nothing set one.
        internal const string DefaultSizeClass = "C";

        /// Stock's ModuleAsteroid / ModuleComet KSPField defaults for the radius draw.
        internal const float DefaultMinRadiusMultiplier = 0.75f;
        internal const float DefaultMaxRadiusMultiplier = 1.25f;

        /// <summary>
        /// The radius-multiplier range to draw from: the part prefab module's own config values
        /// (what stock draws from), else stock's KSPField defaults when there is no module or a
        /// value is unusable.
        /// </summary>
        internal static void ResolveRadiusMultipliers(bool haveModule, float configMin, float configMax,
            out float min, out float max)
        {
            bool usable = haveModule
                && !float.IsNaN(configMin) && !float.IsNaN(configMax)
                && !float.IsInfinity(configMin) && !float.IsInfinity(configMax)
                && configMin > 0f && configMax >= configMin;
            min = usable ? configMin : DefaultMinRadiusMultiplier;
            max = usable ? configMax : DefaultMaxRadiusMultiplier;
        }

        /// Stock's persisted <c>currentState</c> value for a secondary (rare, glowing) rock.
        internal const int SecondaryState = 2;

        internal static string ModuleNameFor(SpaceObjectKind kind)
        {
            switch (kind)
            {
                case SpaceObjectKind.Asteroid: return AsteroidModuleName;
                case SpaceObjectKind.Comet: return CometModuleName;
                default: return null;
            }
        }

        internal static string KindToken(SpaceObjectKind kind)
        {
            switch (kind)
            {
                case SpaceObjectKind.Asteroid: return "asteroid";
                case SpaceObjectKind.Comet: return "comet";
                default: return "none";
            }
        }

        internal static string PathToken(RockGhostPath path)
        {
            switch (path)
            {
                case RockGhostPath.StockGenerator: return "stock-generator";
                case RockGhostPath.ScaledPrefab: return "scaled-prefab";
                default: return "prefab-lump";
            }
        }

        /// <summary>
        /// Reads the rock fields from the part's snapshot MODULE node (stock writes them as
        /// KSPField isPersistant values: <c>seed</c>, <c>prefabBaseURL</c>, <c>currentState</c>).
        /// Fails with a reason token when the module node is missing or carries no usable seed
        /// (<c>seed = -1</c> is stock's "never started" value: the rock was never generated).
        /// An empty URL falls back to the class stock would read off the vessel's discovery
        /// info; the ghost part has none of its own, so stock's default class C is used.
        /// </summary>
        internal static bool TryParseRockSpec(ConfigNode partNode, SpaceObjectKind kind,
            out SpaceObjectRockSpec spec, out string reason)
        {
            spec = null;
            reason = null;
            string moduleName = ModuleNameFor(kind);
            if (moduleName == null)
            {
                reason = "not-a-space-object";
                return false;
            }
            ConfigNode module = FindModuleNode(partNode, moduleName);
            if (module == null)
            {
                reason = "no-module-node";
                return false;
            }

            int seed;
            string rawSeed = module.GetValue("seed");
            if (rawSeed == null
                || !int.TryParse(rawSeed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
            {
                reason = "no-seed";
                return false;
            }
            if (seed == -1)
            {
                reason = "seed-unset";
                return false;
            }

            int state = 0;
            string rawState = module.GetValue("currentState");
            if (rawState != null)
                int.TryParse(rawState.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out state);

            string source;
            string url = ResolvePrefabUrl(kind, module.GetValue("prefabBaseURL"), out source);
            spec = new SpaceObjectRockSpec
            {
                Kind = kind,
                Seed = seed,
                PrefabUrl = url,
                PrefabUrlSource = source,
                IsSecondary = state == SecondaryState,
            };
            return true;
        }

        internal static ConfigNode FindModuleNode(ConfigNode partNode, string moduleName)
        {
            if (partNode == null || moduleName == null) return null;
            ConfigNode[] modules = partNode.GetNodes("MODULE");
            if (modules == null) return null;
            for (int i = 0; i < modules.Length; i++)
            {
                if (string.Equals(modules[i].GetValue("name"), moduleName, StringComparison.Ordinal))
                    return modules[i];
            }
            return null;
        }

        /// <summary>
        /// The procedural prefab URL stock loads: the persisted one, else stock's own default
        /// (<c>prefix + objectSize</c>) at class C.
        /// </summary>
        internal static string ResolvePrefabUrl(SpaceObjectKind kind, string storedUrl, out string source)
        {
            if (!string.IsNullOrEmpty(storedUrl) && storedUrl.Trim().Length > 0)
            {
                source = "snapshot";
                return storedUrl.Trim();
            }
            source = "default-class-" + DefaultSizeClass;
            return (kind == SpaceObjectKind.Comet ? CometUrlPrefix : AsteroidUrlPrefix) + DefaultSizeClass;
        }

        /// <summary>
        /// Whether stock would run its procedural generator for this URL. ModuleAsteroid always
        /// calls ProceduralAsteroid.Generate; ModuleComet calls ProceduralComet.Generate only for
        /// a <c>Procedural/PC_</c> URL and otherwise instantiates a hand-made comet prefab, which
        /// the ghost does not reproduce.
        /// </summary>
        internal static bool UsesStockGenerator(SpaceObjectKind kind, string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (kind == SpaceObjectKind.Asteroid) return true;
            if (kind == SpaceObjectKind.Comet)
                return url.IndexOf(CometUrlPrefix, StringComparison.Ordinal) >= 0;
            return false;
        }

        /// <summary>
        /// The path a rock ghost takes: stock's generator when it produced a mesh, the scaled
        /// placeholder when only the radius is known, the plain placeholder otherwise.
        /// </summary>
        internal static RockGhostPath ChoosePath(bool generatorProducedMesh, bool radiusKnown)
        {
            if (generatorProducedMesh) return RockGhostPath.StockGenerator;
            if (radiusKnown) return RockGhostPath.ScaledPrefab;
            return RockGhostPath.PrefabLump;
        }

        /// <summary>
        /// The uniform scale that brings a placeholder mesh whose bounds half-extents are
        /// (<paramref name="ex"/>, <paramref name="ey"/>, <paramref name="ez"/>) to
        /// <paramref name="targetRadius"/>: target over the mean half-extent. 1 when either side
        /// is unusable, so a bad input never shrinks the part to nothing.
        /// </summary>
        internal static float ComputePlaceholderScale(float targetRadius, float ex, float ey, float ez)
        {
            float mean = (Math.Abs(ex) + Math.Abs(ey) + Math.Abs(ez)) / 3f;
            if (float.IsNaN(targetRadius) || float.IsInfinity(targetRadius) || targetRadius <= 0f)
                return 1f;
            if (float.IsNaN(mean) || float.IsInfinity(mean) || mean <= 1e-4f)
                return 1f;
            return targetRadius / mean;
        }

        /// <summary>
        /// Cache key for one generated rock: the mesh is a pure function of the URL, the seed and
        /// the secondary flag (the radius is drawn from the seed). The part name keeps an
        /// asteroid and a comet with the same seed apart even if they shared a URL.
        /// </summary>
        internal static string BuildCacheKey(string partName, SpaceObjectRockSpec spec)
        {
            if (spec == null) return null;
            return string.Concat(
                partName ?? "",
                "|", spec.PrefabUrl ?? "",
                "|", spec.Seed.ToString(CultureInfo.InvariantCulture),
                "|", spec.IsSecondary ? "2" : "1");
        }

        /// <summary>
        /// Whether a prefab renderer transform is stock's placeholder (or under it), which the
        /// ghost leaves out when the generated rock replaces it, exactly as stock destroys it.
        /// </summary>
        internal static bool IsPlaceholderName(string transformName)
        {
            return string.Equals(transformName, StockPlaceholderTransformName, StringComparison.Ordinal);
        }

        /// <summary>The one diagnostic line per built rock ghost part.</summary>
        internal static string FormatBuiltLine(RockGhostPath path, SpaceObjectKind kind, string partName,
            uint persistentId, SpaceObjectRockSpec spec, float radius, bool radiusKnown, string cacheToken,
            string reason, string cometVfx)
        {
            var ic = CultureInfo.InvariantCulture;
            string line = "RockGhost built: path=" + PathToken(path)
                + " kind=" + KindToken(kind)
                + " part='" + (partName ?? "") + "'"
                + " pid=" + persistentId.ToString(ic)
                + " seed=" + (spec != null ? spec.Seed.ToString(ic) : "?")
                + " url=" + (spec != null ? (spec.PrefabUrl ?? "") : "?")
                + " urlSource=" + (spec != null ? (spec.PrefabUrlSource ?? "") : "?")
                + " secondary=" + (spec != null && spec.IsSecondary ? "true" : "false")
                + " radius=" + (radiusKnown ? radius.ToString("F2", ic) : "?")
                + " cache=" + (cacheToken ?? "none");
            if (!string.IsNullOrEmpty(reason))
                line += " reason=" + reason;
            if (!string.IsNullOrEmpty(cometVfx))
                line += " cometVfx=" + cometVfx;
            return line;
        }
    }
}
