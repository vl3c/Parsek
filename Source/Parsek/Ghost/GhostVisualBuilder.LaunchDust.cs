using System;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Parsek
{
    // S3 launch dust, part parity P9 Y3: the ghost's dust emitters (built, stopped, destroyed).
    internal static partial class GhostVisualBuilder
    {
        /// <summary>The stock prefab names <c>ModuleSurfaceFX.Start</c> loads (decompiled, KSP 1.12.5).</summary>
        internal const string StockSurfaceFxPrefabName = "SurfaceFX";
        internal const string StockWaterFxPrefabName = "WaterFX";
        /// <summary>The name of every launch dust root, which the parity dust cases look for.</summary>
        internal const string LaunchDustObjectName = "ParsekLaunchDust";

        private const BindingFlags SurfaceFxFieldFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>
        /// S3: builds one launch dust emitter of a ghost, parented under the ghost root. First
        /// choice is a copy of stock's own prefab, the one <c>ModuleSurfaceFX</c> draws on that
        /// surface (<c>AssetBase.GetPrefab("SurfaceFX")</c>, <c>"WaterFX"</c> over water): its
        /// cloud, dust and wake systems as stock built them, with the stock <c>SurfaceFX</c>
        /// component removed before it ever wakes (it would join stock's own merge list and read
        /// the active vessel), so Parsek drives the systems with stock's own rule
        /// (<c>GhostPlaybackEngine.DriveLaunchDustEmitter</c>). When the prefab is missing or not
        /// the expected shape, the faint alpha-blended disc built here stands in.
        ///
        /// Returns null when neither can be built; the caller then never re-tries.
        /// </summary>
        internal static LaunchDustEmitter TryBuildLaunchDust(GameObject ghostRoot, string vesselName, bool water)
        {
            if (ghostRoot == null) return null;
            string prefabName = water ? StockWaterFxPrefabName : StockSurfaceFxPrefabName;
            string why;
            LaunchDustEmitter e = TryCopyStockSurfaceFx(ghostRoot, prefabName, out why);
            if (e != null)
            {
                ParsekLog.Verbose("GhostVisual",
                    $"Launch dust built for '{vesselName}': stock {prefabName} prefab copy " +
                    $"(cloud={(e.cloud != null)} dust={(e.dust != null)} wake={(e.wake != null)})");
                return e;
            }
            e = TryBuildFallbackLaunchDust(ghostRoot, water);
            ParsekLog.Verbose("GhostVisual",
                $"Launch dust for '{vesselName}': stock {prefabName} prefab not used ({why}); " +
                (e != null ? "built faint fallback" : "fallback unavailable too (no particle shader), no dust"));
            return e;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static LaunchDustEmitter TryCopyStockSurfaceFx(GameObject ghostRoot, string prefabName, out string why)
        {
            why = null;
            GameObject prefab;
            try { prefab = AssetBase.GetPrefab(prefabName); }
            catch (Exception ex) { why = "lookup threw " + ex.GetType().Name; return null; }
            if (prefab == null) { why = "no such prefab"; return null; }

            // Instantiated under an inactive holder, so nothing on the copy wakes until the stock
            // component is gone.
            var holder = new GameObject("ParsekLaunchDustHolder");
            holder.SetActive(false);
            GameObject copy = null;
            try
            {
                copy = Object.Instantiate(prefab, holder.transform, false);
                SurfaceFX stock = copy.GetComponentInChildren<SurfaceFX>(true);
                if (stock == null) { why = "no SurfaceFX component"; return Discard(copy); }
                var e = new LaunchDustEmitter
                {
                    cloud = ReadSurfaceFxSystem(stock, "srfCloud"),
                    dust = ReadSurfaceFxSystem(stock, "srfDust"),
                    wake = ReadSurfaceFxSystem(stock, "srfWake"),
                    stockPrefab = true,
                    source = prefabName,
                };
                if (e.dust == null) { why = "SurfaceFX has no dust system"; return Discard(copy); }

                foreach (MonoBehaviour mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null) Object.DestroyImmediate(mb);
                foreach (AudioSource src in copy.GetComponentsInChildren<AudioSource>(true))
                {
                    src.playOnAwake = false;
                    src.mute = true;
                    src.volume = 0f;
                }
                int maxParticles = 0;
                foreach (ParticleSystem ps in copy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.playOnAwake = false;
                    if (ps == e.dust || ps == e.cloud) maxParticles = Math.Max(maxParticles, main.maxParticles);
                }
                e.cloudColor = e.cloud != null ? e.cloud.main.startColor.color : Color.white;
                e.dustColor = e.dust.main.startColor.color;
                e.wakeColor = e.wake != null ? e.wake.main.startColor.color : Color.white;
                // Stock's GetParticles scratch is twice the system's maxParticles (SurfaceFX.Start).
                e.buffer = new ParticleSystem.Particle[Math.Max(1, maxParticles * 2)];

                copy.name = LaunchDustObjectName;
                copy.transform.SetParent(ghostRoot.transform, false);
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                if (!copy.activeSelf) copy.SetActive(true);
                foreach (ParticleSystem ps in copy.GetComponentsInChildren<ParticleSystem>(true))
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                GhostWorldSpaceEmitter.AttachOwn(copy);
                e.root = copy;
                copy = null;
                return e;
            }
            catch (Exception ex)
            {
                why = "copy threw " + ex.GetType().Name + ": " + ex.Message;
                return Discard(copy);
            }
            finally
            {
                Object.Destroy(holder);
            }
        }

        private static LaunchDustEmitter Discard(GameObject copy)
        {
            if (copy != null) Object.Destroy(copy);
            return null;
        }

        private static ParticleSystem ReadSurfaceFxSystem(SurfaceFX stock, string fieldName)
        {
            FieldInfo f = typeof(SurfaceFX).GetField(fieldName, SurfaceFxFieldFlags);
            return f?.GetValue(stock) as ParticleSystem;
        }

        /// <summary>
        /// The stand-in when stock's prefab cannot be copied: a low disc of soft alpha-blended
        /// puffs spreading outward along the ground, driven by the same rule as the stock copy
        /// (constant emission, start alpha <c>0.5 * fxScale</c>). The colour, sizes, speeds and
        /// lifetimes are Parsek's guesses at stock's faint grey-brown haze, not read from stock.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static LaunchDustEmitter TryBuildFallbackLaunchDust(GameObject ghostRoot, bool water)
        {
            Shader shader = Shader.Find("KSP/Particles/Alpha Blended");
            if (shader == null) return null;

            var dustObj = new GameObject(LaunchDustObjectName);
            dustObj.transform.SetParent(ghostRoot.transform, false);
            ParticleSystem ps = dustObj.AddComponent<ParticleSystem>();
            Color rgb = water ? LaunchDustFallbackWaterColor : LaunchDustFallbackColor;

            var main = ps.main;
            main.playOnAwake = false;
            main.prewarm = false;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(4f, 7f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.maxParticles = 300;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new Color(rgb.r, rgb.g, rgb.b, 0f);

            // A Circle emits in its local XY plane; the driver turns the root's local Y onto the
            // surface normal (stock's LookRotation(push, normal)), so the shape lies in XZ.
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 2f;
            shape.radiusThickness = 1f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 70f;

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.05f;
            limit.limit = 3f;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(1f, 2.5f)));

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = gradient;

            var psRenderer = dustObj.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.maxParticleSize = 8f;
            Texture2D softCircle = CreateSoftCircleTexture(32);
            var mat = new Material(shader) { mainTexture = softCircle };
            psRenderer.material = mat;

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            GhostWorldSpaceEmitter.AttachOwn(dustObj);
            return new LaunchDustEmitter
            {
                root = dustObj,
                dust = ps,
                dustColor = main.startColor.color,
                buffer = new ParticleSystem.Particle[main.maxParticles * 2],
                stockPrefab = false,
                source = "built",
                material = mat,
                generatedTexture = softCircle,
            };
        }

        /// <summary>The fallback's colours (guessed: a light grey-brown dust, a white spray).</summary>
        internal static readonly Color LaunchDustFallbackColor = new Color(0.62f, 0.58f, 0.52f, 1f);
        internal static readonly Color LaunchDustFallbackWaterColor = new Color(0.92f, 0.95f, 0.97f, 1f);

        /// <summary>Stops every launch dust emitter of a ghost and clears what it emitted (the loop-cycle reset).</summary>
        internal static void StopAndClearLaunchDust(LaunchDustInfo info)
        {
            if (info == null) return;
            StopAndClear(info.terrain);
            StopAndClear(info.water);
        }

        private static void StopAndClear(LaunchDustEmitter e)
        {
            if (e == null) return;
            e.fxScale = 0f;
            e.emitting = false;
            if (e.root == null) return;
            ParticleSystem[] systems = e.root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
                systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        /// <summary>Destroys a ghost's launch dust emitters and the assets each owns.</summary>
        internal static void DestroyLaunchDust(LaunchDustInfo info)
        {
            if (info == null) return;
            DestroyLaunchDustEmitter(info.terrain);
            DestroyLaunchDustEmitter(info.water);
            info.terrain = null;
            info.water = null;
        }

        private static void DestroyLaunchDustEmitter(LaunchDustEmitter e)
        {
            if (e == null) return;
            if (e.material != null) Object.Destroy(e.material);
            if (e.generatedTexture != null) Object.Destroy(e.generatedTexture);
            if (e.root != null) Object.Destroy(e.root);
            e.material = null;
            e.generatedTexture = null;
            e.root = null;
            e.cloud = e.dust = e.wake = null;
            e.buffer = null;
        }
    }
}
