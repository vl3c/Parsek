using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // One-shot and transient FX: explosions (stock FXMonger and the built fallback), part puffs, launch dust and the EVA jetpack plume.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Spawns a small smoke puff + spark burst at a part's world position.
        /// Used when a Decoupled or Destroyed part event is applied during ghost playback.
        /// Much smaller than the vessel explosion — just visual feedback for part separation.
        /// The returned GameObject auto-destroys after particles expire.
        /// </summary>
        internal static GameObject SpawnPartPuffFx(Vector3 worldPosition, float partScale)
        {
            Shader alphaShader = Shader.Find("KSP/Particles/Alpha Blended");
            if (alphaShader == null)
            {
                ParsekLog.Warn("PartPuffFx", "Shader 'KSP/Particles/Alpha Blended' not found — puff will not be created");
                return null;
            }

            // Target: ~1/4 the visual impact of the vessel explosion.
            // Explosion uses vesselLength (10-30m) as scale; parts are much smaller.
            // Use a minimum of 2m so the puff is always visible.
            float scale = Mathf.Clamp(partScale, 2f, 10f);

            var obj = new GameObject("GhostPartPuffFx");
            obj.transform.position = worldPosition;

            // --- Smoke puff ---
            var smokePs = BuildPuffSmokeBurst(obj, alphaShader, scale);

            // --- Small spark burst (additive) ---
            BuildPuffSparkBurst(obj, scale);

            // World-space and on its own: without this it rides along with a flying active vessel.
            GhostWorldSpaceEmitter.AttachOwn(obj);

            smokePs.Play();
            Object.Destroy(obj, 3f);

            GhostTransientFxRecord.Note(
                GhostTransientFxKind.PartPuff, worldPosition, double.NaN, partScale, obj);
            return obj;
        }

        /// <summary>
        /// Builds the smoke puff particle system on the root GameObject for part puff FX.
        /// Returns the ParticleSystem so the caller can trigger Play().
        /// </summary>
        private static ParticleSystem BuildPuffSmokeBurst(GameObject obj, Shader alphaShader, float scale)
        {
            var smokePs = obj.AddComponent<ParticleSystem>();
            var smokeMain = smokePs.main;
            smokeMain.simulationSpace = ParticleSystemSimulationSpace.World;
            smokeMain.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
            smokeMain.startSpeed = new ParticleSystem.MinMaxCurve(scale * 0.3f, scale * 0.8f);
            smokeMain.startSize = new ParticleSystem.MinMaxCurve(scale * 0.15f, scale * 0.4f);
            smokeMain.maxParticles = 100;
            smokeMain.playOnAwake = false;
            smokeMain.loop = false;
            smokeMain.gravityModifier = 0.03f;
            smokeMain.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.4f, 0.4f, 0.4f, 0.6f),
                new Color(0.25f, 0.22f, 0.2f, 0.5f));

            var smokeShape = smokePs.shape;
            smokeShape.shapeType = ParticleSystemShapeType.Sphere;
            smokeShape.radius = scale * 0.1f;

            var smokeEmission = smokePs.emission;
            smokeEmission.enabled = true;
            smokeEmission.rateOverTime = 0f;
            smokeEmission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0f, 40, 60)
            });

            var smokeColor = smokePs.colorOverLifetime;
            smokeColor.enabled = true;
            var smokeGradient = new Gradient();
            smokeGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.5f, 0.45f, 0.4f), 0f),
                    new GradientColorKey(new Color(0.3f, 0.28f, 0.25f), 0.5f),
                    new GradientColorKey(new Color(0.2f, 0.18f, 0.16f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.5f, 0.1f),
                    new GradientAlphaKey(0.3f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            smokeColor.color = smokeGradient;

            var smokeSize = smokePs.sizeOverLifetime;
            smokeSize.enabled = true;
            smokeSize.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.5f),
                    new Keyframe(0.5f, 1.5f),
                    new Keyframe(1f, 2.5f)));

            if (cachedExplosionTexture == null)
                cachedExplosionTexture = CreateSoftCircleTexture(32);
            var smokeMat = new Material(alphaShader);
            smokeMat.mainTexture = cachedExplosionTexture;
            smokeMat.SetColor("_TintColor", new Color(0.35f, 0.3f, 0.25f, 0.5f));
            var smokeRenderer = obj.GetComponent<ParticleSystemRenderer>();
            smokeRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            smokeRenderer.maxParticleSize = 10f;
            smokeRenderer.material = smokeMat;

            var smokeCleanup = obj.AddComponent<MaterialCleanup>();
            smokeCleanup.material = smokeMat;

            return smokePs;
        }

        /// <summary>
        /// Builds a small spark burst child particle system on the puff FX root.
        /// Uses additive shader. No-ops if the shader is unavailable.
        /// </summary>
        private static void BuildPuffSparkBurst(GameObject parent, float scale)
        {
            Shader additiveShader = Shader.Find("KSP/Particles/Additive");
            if (additiveShader == null) return;

            var sparkObj = new GameObject("Sparks");
            sparkObj.transform.SetParent(parent.transform, false);

            var sparkPs = sparkObj.AddComponent<ParticleSystem>();
            var sparkMain = sparkPs.main;
            sparkMain.simulationSpace = ParticleSystemSimulationSpace.World;
            sparkMain.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
            sparkMain.startSpeed = new ParticleSystem.MinMaxCurve(scale * 0.5f, scale * 1.5f);
            sparkMain.startSize = new ParticleSystem.MinMaxCurve(scale * 0.03f, scale * 0.1f);
            sparkMain.maxParticles = 50;
            sparkMain.playOnAwake = false;
            sparkMain.loop = false;
            sparkMain.gravityModifier = 0.3f;
            sparkMain.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.9f, 0.5f, 0.8f),
                new Color(1f, 0.6f, 0.2f, 0.6f));

            var sparkShape = sparkPs.shape;
            sparkShape.shapeType = ParticleSystemShapeType.Sphere;
            sparkShape.radius = scale * 0.08f;

            var sparkEmission = sparkPs.emission;
            sparkEmission.enabled = true;
            sparkEmission.rateOverTime = 0f;
            sparkEmission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0f, 15, 30)
            });

            var sparkColor = sparkPs.colorOverLifetime;
            sparkColor.enabled = true;
            var sparkGradient = new Gradient();
            sparkGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f),
                    new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.5f),
                    new GradientColorKey(new Color(0.5f, 0.2f, 0.05f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.8f, 0f),
                    new GradientAlphaKey(0.3f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            sparkColor.color = sparkGradient;

            var sparkMat = new Material(additiveShader);
            sparkMat.mainTexture = cachedExplosionTexture;
            sparkMat.SetColor("_TintColor", new Color(1f, 0.7f, 0.3f, 0.5f));
            var sparkRenderer = sparkObj.GetComponent<ParticleSystemRenderer>();
            sparkRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            sparkRenderer.maxParticleSize = 8f;
            sparkRenderer.material = sparkMat;

            var sparkCleanup = sparkObj.AddComponent<MaterialCleanup>();
            sparkCleanup.material = sparkMat;

            sparkPs.Play();
        }

        // Returns the live FXMonger singleton or null. Centralises the reflection
        // accessor so callers that need the instance (to read `explosions[]`) and
        // callers that just need an availability check share one cached lookup.
        internal static FXMonger ResolveLiveFxMonger()
        {
            if (fxMongerFetchField == null)
            {
                fxMongerFetchField = typeof(FXMonger).GetField(
                    "fetch",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (fxMongerFetchField == null)
                    return null;
            }
            // Unity's == operator treats destroyed objects as null, so this stays correct
            // across scene transitions that clear FXMonger.fetch in OnDestroy.
            return fxMongerFetchField.GetValue(null) as FXMonger;
        }

        internal static bool IsFxMongerLive()
        {
            return ResolveLiveFxMonger() != null;
        }

        /// <summary>
        /// Reflectively reads FXMonger's protected `explosionObjects` list on the live singleton
        /// so the pause/unpause path can iterate every in-flight stock explosion GameObject and
        /// pause their AudioSources. Returns null if the singleton is gone or the field has been
        /// renamed in a future KSP version (caller logs once and falls open — the pause won't
        /// reach FXMonger sources but the rest of Parsek's tracked sources will still pause).
        /// </summary>
        internal static List<FXObject> ResolveFxMongerExplosionObjects()
        {
            FXMonger fetch = ResolveLiveFxMonger();
            if (fetch == null)
                return null;
            if (fxMongerExplosionObjectsField == null)
            {
                fxMongerExplosionObjectsField = typeof(FXMonger).GetField(
                    "explosionObjects",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (fxMongerExplosionObjectsField == null)
                    return null;
            }
            return fxMongerExplosionObjectsField.GetValue(fetch) as List<FXObject>;
        }

        /// <summary>
        /// Tries to queue KSP's stock explosion effect.
        /// Returns false when the stock controller is unavailable or the call throws.
        /// </summary>
        internal static bool TryTriggerStockExplosionFx(
            Vector3 worldPosition,
            double power,
            out string failureReason,
            System.Func<bool> isFxMongerAvailable = null,
            System.Action<Vector3, double> explode = null)
        {
            bool stockFxAvailable = isFxMongerAvailable != null
                ? isFxMongerAvailable()
                : IsFxMongerLive();
            if (!stockFxAvailable)
            {
                failureReason = "no live FXMonger instance";
                return false;
            }

            try
            {
                if (explode != null)
                    explode(worldPosition, power);
                else
                    // Passing null for `source` is safe: decompiled FXMonger only stores the Part
                    // in its queued ProtoExplosion.sources list for merge bookkeeping and never
                    // dereferences it. Ghost explosions have no owning Part to supply.
                    FXMonger.Explode(null, worldPosition, power);
            }
            catch (System.Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }

            GhostTransientFxRecord.Note(
                GhostTransientFxKind.StockExplosion, worldPosition, power, float.NaN, null);
            failureReason = null;
            return true;
        }

        /// <summary>
        /// The explosion a ghost crash draws when <c>FXMonger.Explode</c> cannot be used (the Space
        /// Center view, the flight pause menu, a stock call that failed): a silent copy of stock's
        /// own explosion for <paramref name="power"/>, kept from the flight scene by
        /// <see cref="StockExplosionPrefabCache"/>, so the crash looks the same as in flight. Only a
        /// session that has never been in flight (no copies kept) draws Parsek's hand-built
        /// <see cref="SpawnExplosionFx"/>.
        /// </summary>
        internal static GameObject SpawnFallbackExplosionFx(Vector3 worldPosition, double power, float vesselLength)
        {
            GameObject blast = StockExplosionPrefabCache.TrySpawn(worldPosition, power, out string why);
            if (blast != null)
            {
                GhostTransientFxRecord.Note(
                    GhostTransientFxKind.StockCopyExplosion, worldPosition, power, vesselLength, blast);
                return blast;
            }
            ParsekLog.VerboseRateLimited("ExplosionFx", "stock-copy-unavailable",
                $"Stock explosion copy unavailable ({why}); drawing Parsek's hand-built explosion", 10.0);
            return SpawnExplosionFx(worldPosition, vesselLength, power);
        }

        /// <summary>
        /// Parsek's hand-built explosion, drawn only when no silent copy of stock's explosion is
        /// kept (see <see cref="SpawnFallbackExplosionFx"/>). It copies the stock explosion
        /// FXMonger would pick for <paramref name="power"/> (<see cref="FallbackExplosionLook"/>):
        /// a fireball and a flash only for the bigger blasts, white streak sparks, and a cloud of
        /// dark and lighter grey smoke, each at stock's measured size for that blast. A NaN power
        /// falls back to the craft length's power.
        /// The returned GameObject destroys itself once its particles are gone.
        /// </summary>
        internal static GameObject SpawnExplosionFx(Vector3 worldPosition, float vesselLength, double power)
        {
            Shader additiveShader = Shader.Find("KSP/Particles/Additive");
            Shader alphaShader = Shader.Find("KSP/Particles/Alpha Blended");
            if (additiveShader == null && alphaShader == null)
            {
                ParsekLog.Warn("ExplosionFx", "Shaders 'KSP/Particles/Additive' and 'KSP/Particles/Alpha Blended' not found - explosion will not be created");
                return null;
            }

            double usedPower = FallbackExplosionLook.ResolvePower(power, vesselLength);
            FallbackExplosionLook look = FallbackExplosionLook.Resolve(usedPower);

            if (cachedExplosionTexture == null)
                cachedExplosionTexture = CreateSoftCircleTexture(32);

            var obj = new GameObject("GhostExplosionFx");
            obj.transform.position = worldPosition;

            // The smoke and the fireball are alpha-blended (the fire sorted over the smoke); the
            // flash and the sparks are additive on top.
            var systems = new List<ParticleSystem>(4);
            int built = 0;
            if (alphaShader != null)
            {
                built += AddIfBuilt(systems, BuildFallbackExplosionSmoke(obj, alphaShader, look));
                if (look.HasFire) built += AddIfBuilt(systems, BuildFallbackExplosionFire(obj, alphaShader, look));
            }
            if (additiveShader != null)
            {
                if (look.HasFlash) built += AddIfBuilt(systems, BuildFallbackExplosionFlash(obj, additiveShader, look));
                if (look.HasSparks) built += AddIfBuilt(systems, BuildFallbackExplosionSparks(obj, additiveShader, look));
            }

            // Stock moves its own explosions (FXMonger.offsetPositions); this one is Parsek's.
            GhostWorldSpaceEmitter.AttachOwn(obj);

            for (int i = 0; i < systems.Count; i++)
                systems[i].Play();

            Object.Destroy(obj, look.DestroyAfterSeconds);

            ParsekLog.Verbose("ExplosionFx",
                $"Spawned hand-built explosion at ({worldPosition.x:F1},{worldPosition.y:F1},{worldPosition.z:F1}) " +
                $"vesselLength={vesselLength:F1}m power={usedPower.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"{look.Describe()} systems={built.ToString(CultureInfo.InvariantCulture)}" +
                (alphaShader == null ? " (no smoke/fire: alpha shader missing)" : "") +
                (additiveShader == null ? " (no flash/sparks: additive shader missing)" : ""));

            GhostTransientFxRecord.Note(
                GhostTransientFxKind.CustomExplosion, worldPosition, usedPower, vesselLength, obj);
            return obj;
        }

        private static int AddIfBuilt(List<ParticleSystem> systems, ParticleSystem ps)
        {
            if (ps == null) return 0;
            systems.Add(ps);
            return 1;
        }

        // One child particle system with a soft-circle material and its cleanup; the caller sets
        // its modules. World space, one burst, no loop.
        private static ParticleSystem NewExplosionPart(GameObject parent, string name, Shader shader,
            Color tint, int maxParticles, out ParticleSystemRenderer renderer)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            var ps = child.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.playOnAwake = false;
            main.prewarm = false;
            main.loop = false;

            renderer = child.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var mat = new Material(shader);
            mat.mainTexture = cachedExplosionTexture;
            mat.SetColor("_TintColor", tint);
            renderer.material = mat;
            var cleanup = child.AddComponent<MaterialCleanup>();
            cleanup.material = mat;
            return ps;
        }

        private static void SetSingleBurst(ParticleSystem ps, float time, int count)
        {
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(time, (short)count, (short)count) });
        }

        /// <summary>
        /// The smoke cloud: dense puffs picked between a dark and a lighter grey that swell, drift
        /// up a little and fade, about <see cref="FallbackExplosionLook.SmokeDiameter"/> across.
        /// The neutral 0.5 tint leaves the particle colours as they are (the KSP particle shaders
        /// double the tint), so the cloud stays grey, never brown. Its alpha rises after the
        /// fireball has had its moment, as stock's cloud takes over from its fire.
        /// </summary>
        private static ParticleSystem BuildFallbackExplosionSmoke(GameObject parent, Shader alphaShader, FallbackExplosionLook look)
        {
            var ps = NewExplosionPart(parent, "Smoke", alphaShader, new Color(0.5f, 0.5f, 0.5f, 0.5f),
                look.SmokeCount, out ParticleSystemRenderer renderer);
            renderer.maxParticleSize = 3f;

            float d = look.SmokeDiameter;
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(look.SmokeLifetimeMin, look.SmokeLifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(d * 0.08f, d * 0.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(d * 0.35f, d * 0.5f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = -0.03f;
            float dark = look.SmokeDarkGrey, light = look.SmokeLightGrey;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(dark, dark, dark, 0.9f),
                new Color(light, light, light, 0.8f));

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = d * 0.15f;

            SetSingleBurst(ps, 0.05f, look.SmokeCount);

            // Under a fireball the cloud shows once the fire burns down; alone it shows at once.
            float rise = look.HasFire ? Mathf.Clamp01(look.FireLifetime * 0.6f / look.SmokeLifetimeMin) : 0.1f;
            var color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.95f, rise),
                    new GradientAlphaKey(0.75f, Mathf.Lerp(rise, 1f, 0.5f)),
                    new GradientAlphaKey(0f, 1f)
                });
            color.color = gradient;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.3f)));

            var velocity = ps.limitVelocityOverLifetime;
            velocity.enabled = true;
            velocity.dampen = 0.15f;
            velocity.limit = d * 0.05f;
            return ps;
        }

        /// <summary>
        /// The fireball: dense yellow puffs that swell to about
        /// <see cref="FallbackExplosionLook.FireDiameter"/> and burn through orange to a dark
        /// red-brown as they fade. Alpha-blended, so it stays a saturated yellow against a bright
        /// sky (an additive one washes out to white), and sorted over the smoke.
        /// </summary>
        private static ParticleSystem BuildFallbackExplosionFire(GameObject parent, Shader alphaShader, FallbackExplosionLook look)
        {
            var ps = NewExplosionPart(parent, "Fire", alphaShader, new Color(0.5f, 0.5f, 0.5f, 0.5f),
                look.FireCount, out ParticleSystemRenderer renderer);
            renderer.maxParticleSize = 3f;
            renderer.sortingFudge = -20f;

            float d = look.FireDiameter;
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(look.FireLifetime * 0.7f, look.FireLifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(d * 0.15f, d * 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(d * 0.4f, d * 0.55f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.97f, 0.6f, 1f), new Color(1f, 0.82f, 0.35f, 1f));

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = d * 0.12f;

            SetSingleBurst(ps, 0f, look.FireCount);

            var color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(1f, 0.75f, 0.35f), 0.45f),
                    new GradientColorKey(new Color(0.55f, 0.3f, 0.22f), 0.8f),
                    new GradientColorKey(new Color(0.35f, 0.3f, 0.28f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.5f),
                    new GradientAlphaKey(0.6f, 0.8f),
                    new GradientAlphaKey(0f, 1f)
                });
            color.color = gradient;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.45f), new Keyframe(0.4f, 1f), new Keyframe(1f, 1.1f)));

            var velocity = ps.limitVelocityOverLifetime;
            velocity.enabled = true;
            velocity.dampen = 0.2f;
            velocity.limit = d * 0.08f;
            return ps;
        }

        /// <summary>The opening flash: a few large white glows that swell and fade fast.</summary>
        private static ParticleSystem BuildFallbackExplosionFlash(GameObject parent, Shader additiveShader, FallbackExplosionLook look)
        {
            var ps = NewExplosionPart(parent, "Flash", additiveShader, new Color(0.5f, 0.5f, 0.5f, 0.5f),
                4, out ParticleSystemRenderer renderer);
            renderer.maxParticleSize = 3f;
            renderer.sortingFudge = -40f;

            float d = look.FlashDiameter;
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(look.FlashLifetime * 0.8f, look.FlashLifetime);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(d * 0.9f, d * 1.2f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 1f, 0.95f, 1f), new Color(1f, 0.95f, 0.8f, 1f));

            var shape = ps.shape;
            shape.enabled = false;

            SetSingleBurst(ps, 0f, 3);

            var color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(1f, 0.9f, 0.7f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });
            color.color = gradient;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.5f, 1f), new Keyframe(1f, 1.1f)));
            return ps;
        }

        /// <summary>
        /// The sparks: thin white streaks (velocity-stretched billboards) flung outward, slowing
        /// and falling a little, as stock's explosions throw them.
        /// </summary>
        private static ParticleSystem BuildFallbackExplosionSparks(GameObject parent, Shader additiveShader, FallbackExplosionLook look)
        {
            var ps = NewExplosionPart(parent, "Sparks", additiveShader, new Color(0.5f, 0.5f, 0.5f, 0.5f),
                look.SparkCount, out ParticleSystemRenderer renderer);
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.1f;
            renderer.lengthScale = 1f;
            renderer.maxParticleSize = 0.5f;

            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(look.SparkLifetime * 0.5f, look.SparkLifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(look.SparkSpeedMin, look.SparkSpeedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(0.1f, 0.2f);
            main.gravityModifier = 0.25f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 1f, 1f, 1f), new Color(1f, 0.92f, 0.75f, 1f));

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.3f;

            SetSingleBurst(ps, 0f, look.SparkCount);

            var color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(1f, 0.9f, 0.75f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.7f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });
            color.color = gradient;

            var velocity = ps.limitVelocityOverLifetime;
            velocity.enabled = true;
            velocity.dampen = 0.08f;
            velocity.limit = look.SparkSpeedMax * 0.35f;
            return ps;
        }

        /// <summary>
        /// Creates a soft-circle texture at runtime for additive particle rendering.
        /// White center fading to transparent edges — avoids sprite sheet issues.
        /// </summary>
        /// <summary>
        /// S3: builds the ONE Parsek-owned launch-dust particle system a ghost gets, parented under
        /// the ghost root. The driver lays it flat on the ground below the ghost every frame
        /// (<c>GhostPlaybackEngine.PlaceLaunchDustOnGround</c>), so the disc built here is a wide,
        /// low sheet running outward across the ground, as stock's ground dust does. Parsek authors this system outright — exactly like
        /// the reentry <c>fireParticles</c> — which is why the driver may write
        /// <c>emission.rateOverTimeMultiplier</c> and <c>main.startSizeMultiplier</c> directly
        /// instead of going through the captured-baseline machinery S1 needs for CLONED KSP assets.
        ///
        /// Returns null when the additive particle shader is unavailable, which is the same
        /// condition that makes reentry FX unbuildable; the caller then never re-tries.
        /// </summary>
        internal static LaunchDustInfo TryBuildLaunchDust(GameObject ghostRoot, string vesselName)
        {
            if (ghostRoot == null) return null;

            Shader particleShader = Shader.Find("KSP/Particles/Additive");
            if (particleShader == null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", "launch-dust-noshader",
                    $"Launch dust unavailable for '{vesselName}': KSP/Particles/Additive not found", 60.0);
                return null;
            }

            var dustObj = new GameObject("ParsekLaunchDust");
            dustObj.transform.SetParent(ghostRoot.transform, false);
            dustObj.transform.localPosition = Vector3.zero;
            dustObj.transform.localRotation = Quaternion.identity;

            ParticleSystem ps = dustObj.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.playOnAwake = false;
            main.prewarm = false;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 3.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 13f);
            main.startSize = 2.5f;
            main.maxParticles = 400;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new Color(0.65f, 0.60f, 0.52f, 0.35f);

            // A flat disc spraying outward in its own plane: the ground-bounce plume, not a downward
            // jet (which the engine FX already draws). A Circle emits in its local XY plane, so the
            // driver turns local Z onto the surface normal to lay it on the ground.
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 4f;
            shape.radiusThickness = 1f;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTimeMultiplier = 0f;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(1f, 1.6f)));

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.72f, 0.66f, 0.56f), 0f),
                    new GradientColorKey(new Color(0.55f, 0.51f, 0.45f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.5f, 0.2f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            var psRenderer = dustObj.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.maxParticleSize = 6f;

            Texture2D softCircle = CreateSoftCircleTexture(32);
            var dustMat = new Material(particleShader) { mainTexture = softCircle };
            dustMat.SetColor("_TintColor", new Color(0.7f, 0.65f, 0.55f, 0.4f));
            psRenderer.material = dustMat;

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(true);

            // The dust it has sprayed stays on the ground when the floating origin shifts.
            GhostWorldSpaceEmitter.AttachOwn(dustObj);

            ParsekLog.Verbose("GhostVisual", $"Launch dust built for '{vesselName}'");
            return new LaunchDustInfo
            {
                dustObject = dustObj,
                particles = ps,
                material = dustMat,
                generatedTexture = softCircle
            };
        }

        /// <summary>
        /// S4: the ONE Parsek-owned jetpack puff a ghost kerbal gets, parented at the ghost root
        /// and offset BACKWARD so the particles leave from behind the pack rather than out of the
        /// kerbal's chest.
        ///
        /// Deliberately small and short-lived. A jetpack is a few newtons of cold gas, so a plume
        /// scaled like an engine's would read as a rocket strapped to a spacesuit. What has to be
        /// legible at playback speed is simply "he is under power", which a brief puff carries.
        ///
        /// Returns null when the additive particle shader is unavailable — the same condition that
        /// makes launch dust and reentry FX unbuildable, and the caller then never re-tries.
        /// </summary>
        internal static EvaJetpackPlumeInfo TryBuildEvaJetpackPlume(GameObject ghostRoot, string vesselName)
        {
            if (ghostRoot == null) return null;

            Shader particleShader = Shader.Find("KSP/Particles/Additive");
            if (particleShader == null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", "eva-plume-noshader",
                    $"EVA jetpack plume unavailable for '{vesselName}': KSP/Particles/Additive not found", 60.0);
                return null;
            }

            var plumeObj = new GameObject("ParsekEvaJetpackPlume");
            plumeObj.transform.SetParent(ghostRoot.transform, false);
            // Behind and slightly below the kerbal's origin: roughly where the pack's nozzles sit.
            // A kerbal is ~1.8 m of which the pack occupies the upper back, and the ghost root is at
            // the part origin, so these are deliberately small numbers.
            plumeObj.transform.localPosition = new Vector3(0f, 0.15f, -0.25f);
            plumeObj.transform.localRotation = Quaternion.identity;

            ParticleSystem ps = plumeObj.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.playOnAwake = false;
            main.prewarm = false;
            main.loop = true;
            main.startLifetime = 0.35f;
            main.startSpeed = 3.5f;
            main.startSize = 0.18f;
            main.maxParticles = 60;
            // LOCAL space, unlike launch dust's world space: the puff belongs to a kerbal who is
            // himself moving, and world-space particles would leave a trail hanging in the air
            // behind him rather than a jet leaving the pack.
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startColor = new Color(0.85f, 0.88f, 0.95f, 0.45f);

            // A narrow cone pointing BACKWARD along the ghost's local -Z.
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 14f;
            shape.radius = 0.05f;
            plumeObj.transform.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTimeMultiplier = EvaJetpackPlumeEmissionRate;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.2f)));

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.92f, 0.95f, 1f), 0f),
                    new GradientColorKey(new Color(0.75f, 0.80f, 0.90f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.55f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            var psRenderer = plumeObj.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.maxParticleSize = 0.5f;

            Texture2D softCircle = CreateSoftCircleTexture(32);
            var plumeMat = new Material(particleShader) { mainTexture = softCircle };
            plumeMat.SetColor("_TintColor", new Color(0.85f, 0.9f, 1f, 0.4f));
            psRenderer.material = plumeMat;

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(true);

            ParsekLog.Verbose("GhostVisual", $"EVA jetpack plume built for '{vesselName}'");
            return new EvaJetpackPlumeInfo
            {
                plumeObject = plumeObj,
                particles = ps,
                material = plumeMat,
                generatedTexture = softCircle
            };
        }

        /// <summary>Destroys a ghost's EVA jetpack plume and the two assets it owns.</summary>
        internal static void DestroyEvaJetpackPlume(EvaJetpackPlumeInfo info)
        {
            if (info == null) return;
            if (info.material != null) Object.Destroy(info.material);
            if (info.generatedTexture != null) Object.Destroy(info.generatedTexture);
            if (info.plumeObject != null) Object.Destroy(info.plumeObject);
            info.material = null;
            info.generatedTexture = null;
            info.particles = null;
            info.plumeObject = null;
        }

        /// <summary>Destroys a ghost's launch-dust system and the two assets it owns.</summary>
        internal static void DestroyLaunchDust(LaunchDustInfo info)
        {
            if (info == null) return;
            if (info.material != null) Object.Destroy(info.material);
            if (info.generatedTexture != null) Object.Destroy(info.generatedTexture);
            if (info.dustObject != null) Object.Destroy(info.dustObject);
            info.material = null;
            info.generatedTexture = null;
            info.particles = null;
            info.dustObject = null;
        }

        private static Texture2D CreateSoftCircleTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float center = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center)) / center;
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha *= alpha; // quadratic falloff for soft edges
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply(false, true); // makeNoLongerReadable for GPU-only
            return tex;
        }
    }
}
