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
        /// Builds the smoke child particle system for SpawnExplosionFx.
        /// Returns the created ParticleSystem, or null if the shader was not found.
        /// </summary>
        private static ParticleSystem BuildExplosionSmokeChild(
            GameObject parentObj, float vesselLength)
        {
            Shader alphaShader = Shader.Find("KSP/Particles/Alpha Blended");
            if (alphaShader == null)
            {
                ParsekLog.Warn("GhostVisual", "BuildExplosionSmokeChild shader 'KSP/Particles/Alpha Blended' not found — skipping smoke child");
                return null;
            }

            var smokeObj = new GameObject("Smoke");
            smokeObj.transform.SetParent(parentObj.transform, false);

            var smokePs = smokeObj.AddComponent<ParticleSystem>();
            var smokeMain = smokePs.main;
            smokeMain.simulationSpace = ParticleSystemSimulationSpace.World;
            smokeMain.startLifetime = new ParticleSystem.MinMaxCurve(2.0f, 3.5f);
            smokeMain.startSpeed = new ParticleSystem.MinMaxCurve(vesselLength * 0.25f, vesselLength * 1f);
            smokeMain.startSize = new ParticleSystem.MinMaxCurve(vesselLength * 0.3f, vesselLength * 0.8f);
            smokeMain.maxParticles = 400;
            smokeMain.playOnAwake = false;
            smokeMain.loop = false;
            smokeMain.gravityModifier = 0.03f;
            smokeMain.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.3f, 0.3f, 0.3f, 0.6f),
                new Color(0.15f, 0.12f, 0.1f, 0.5f));

            var smokeShape = smokePs.shape;
            smokeShape.shapeType = ParticleSystemShapeType.Sphere;
            smokeShape.radius = vesselLength * 0.3f;

            var smokeEmission = smokePs.emission;
            smokeEmission.enabled = true;
            smokeEmission.rateOverTime = 0f;
            smokeEmission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0.1f, 160, 240)
            });

            var smokeColor = smokePs.colorOverLifetime;
            smokeColor.enabled = true;
            var smokeGradient = new Gradient();
            smokeGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.4f, 0.35f, 0.3f), 0f),
                    new GradientColorKey(new Color(0.25f, 0.22f, 0.2f), 0.4f),
                    new GradientColorKey(new Color(0.15f, 0.13f, 0.12f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.0f, 0f),
                    new GradientAlphaKey(0.5f, 0.1f),
                    new GradientAlphaKey(0.4f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            smokeColor.color = smokeGradient;

            var smokeSize = smokePs.sizeOverLifetime;
            smokeSize.enabled = true;
            smokeSize.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.5f),
                    new Keyframe(0.3f, 1.5f),
                    new Keyframe(1f, 3.0f)));

            var smokeNoise = smokePs.noise;
            smokeNoise.enabled = true;
            smokeNoise.strength = vesselLength * 0.2f;
            smokeNoise.frequency = 1.5f;
            smokeNoise.scrollSpeed = 0.8f;
            smokeNoise.damping = true;

            var smokeRenderer = smokeObj.GetComponent<ParticleSystemRenderer>();
            smokeRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            smokeRenderer.maxParticleSize = 12f;

            if (cachedExplosionTexture == null)
                cachedExplosionTexture = CreateSoftCircleTexture(32);
            var smokeMat = new Material(alphaShader);
            smokeMat.mainTexture = cachedExplosionTexture;
            smokeMat.SetColor("_TintColor", new Color(0.3f, 0.25f, 0.2f, 0.5f));
            smokeRenderer.material = smokeMat;

            var smokeCleanup = smokeObj.AddComponent<MaterialCleanup>();
            smokeCleanup.material = smokeMat;

            return smokePs;
        }

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

            smokePs.Play();
            Object.Destroy(obj, 3f);

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

                failureReason = null;
                return true;
            }
            catch (System.Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Spawns a fire-and-forget explosion particle effect at the given world position.
        /// The returned GameObject auto-destroys after particles expire.
        /// Used when ghost playback reaches the end of a destroyed recording.
        /// </summary>
        internal static GameObject SpawnExplosionFx(Vector3 worldPosition, float vesselLength)
        {
            Shader particleShader = Shader.Find("KSP/Particles/Additive");
            if (particleShader == null)
            {
                ParsekLog.Warn("ExplosionFx", "Shader 'KSP/Particles/Additive' not found — explosion will not be created");
                return null;
            }

            var obj = new GameObject("GhostExplosionFx");
            obj.transform.position = worldPosition;

            var ps = obj.AddComponent<ParticleSystem>();

            // Main module
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(vesselLength * 1f, vesselLength * 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(vesselLength * 0.08f, vesselLength * 0.35f);
            main.maxParticles = 400;
            main.playOnAwake = false;
            main.prewarm = false;
            main.loop = false;
            main.gravityModifier = 0.15f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.9f, 0.5f, 0.9f),
                new Color(1f, 0.6f, 0.2f, 0.9f));

            // Shape: sphere burst
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = vesselLength * 0.2f;

            // Emission: single burst
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0f, 200, 300)
            });

            // Color over lifetime: bright yellow-white → orange → red-brown → fade
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.6f), 0f),
                    new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.25f),
                    new GradientColorKey(new Color(0.8f, 0.25f, 0.05f), 0.6f),
                    new GradientColorKey(new Color(0.3f, 0.1f, 0.02f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.9f, 0f),
                    new GradientAlphaKey(0.7f, 0.25f),
                    new GradientAlphaKey(0.2f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = gradient;

            // Size over lifetime: expanding fireball
            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 1f),
                    new Keyframe(0.4f, 1.5f),
                    new Keyframe(1f, 2.0f)));

            // Noise: turbulence for organic explosion look
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = vesselLength * 0.15f;
            noise.frequency = 2.5f;
            noise.scrollSpeed = 1.5f;
            noise.damping = true;

            // Renderer: additive shader with soft-circle texture
            var psRenderer = obj.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.maxParticleSize = 10f;

            if (cachedExplosionTexture == null)
                cachedExplosionTexture = CreateSoftCircleTexture(32);

            var mat = new Material(particleShader);
            mat.mainTexture = cachedExplosionTexture;
            mat.SetColor("_TintColor", new Color(1f, 0.7f, 0.3f, 0.5f));
            psRenderer.material = mat;

            // Attach self-cleanup so the Material is destroyed when the GO is destroyed
            var cleanup = obj.AddComponent<MaterialCleanup>();
            cleanup.material = mat;

            // --- Smoke child particle system ---
            var smokePs = BuildExplosionSmokeChild(obj, vesselLength);
            if (smokePs != null)
                smokePs.Play();

            ps.Play();

            // Auto-destroy after max smoke lifetime + buffer
            Object.Destroy(obj, 5.0f);

            ParsekLog.Verbose("ExplosionFx",
                $"Spawned at ({worldPosition.x:F1},{worldPosition.y:F1},{worldPosition.z:F1}) vesselLength={vesselLength:F1}m");

            return obj;
        }

        /// <summary>
        /// Creates a soft-circle texture at runtime for additive particle rendering.
        /// White center fading to transparent edges — avoids sprite sheet issues.
        /// </summary>
        /// <summary>
        /// S3: builds the ONE Parsek-owned launch-dust particle system a ghost gets, parented at
        /// the ghost root and pointed downward. Parsek authors this system outright — exactly like
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
            main.startLifetime = 2.5f;
            main.startSpeed = 6f;
            main.startSize = 2.5f;
            main.maxParticles = 400;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new Color(0.65f, 0.60f, 0.52f, 0.35f);

            // A flat disc at the ghost's base spraying outward: the ground-bounce plume, not a
            // downward jet (which the engine FX already draws).
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
                new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 2.2f)));

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
