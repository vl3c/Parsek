using UnityEngine;

namespace Parsek
{
    // S3 launch dust, part parity P9 Y3: the ghost's dust emitters (built, stopped, destroyed).
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// S3: builds a ghost's launch dust emitter, parented under the ghost root. The driver
        /// (<c>GhostPlaybackEngine.DriveLaunchDustFrame</c>) puts it at the combined hit point of
        /// the engines' stock <c>ModuleSurfaceFX</c> raycasts every frame and lays the disc flat on
        /// the surface. Parsek authors this system outright, which is why the driver may write its
        /// emission and size directly instead of going through the captured-baseline machinery S1
        /// needs for cloned KSP assets.
        ///
        /// Returns null when the particle shader is unavailable; the caller then never re-tries.
        /// </summary>
        internal static LaunchDustEmitter TryBuildLaunchDust(GameObject ghostRoot, string vesselName)
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
            return new LaunchDustEmitter
            {
                root = dustObj,
                dust = ps,
                dustColor = main.startColor.color,
                source = "built",
                material = dustMat,
                generatedTexture = softCircle
            };
        }

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
        }
    }
}
