using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// One-shot diagnostic probe attached to every cloned ghost engine FX instance.
    /// Once the instance has live particles, it logs the MEASURED mean particle
    /// velocity direction in world space (mechanism-agnostic: works for
    /// KSPParticleEmitter EmitParams velocities and shuriken self-emission alike),
    /// then destroys itself. Purpose: FX orientation issues kept being diagnosed
    /// from ASSUMED emission axes (instance +Y, authored localVelocity), and the
    /// round-4 log proved those assumptions wrong (every smokeTrail's +Y points
    /// world-up, on correct-looking jets and wrong-looking engines alike). This
    /// probe measures reality, so a single showroom pass names every misaimed
    /// part in the log: grep "[FxEmissionProbe]" and read angleFromDown
    /// (showroom fixtures stand upright: ~0 = flows down/correct, ~180 = flows up).
    /// Logged once per (part, midx, fx) per scene session; timeout logs a
    /// no-particles line so never-emitting instances are visible too.
    /// </summary>
    internal class GhostFxEmissionProbe : MonoBehaviour
    {
        private const int MinParticlesForSample = 6;
        private const float TimeoutSeconds = 45f;

        private static readonly HashSet<string> loggedKeys = new HashSet<string>(System.StringComparer.Ordinal);

        internal string partName;
        internal int moduleIndex;
        internal string fxName;

        private ParticleSystem[] systems;
        private ParticleSystem.Particle[] buffer;
        private float startTime;

        internal static void AttachIfNew(GameObject fxInstance, string partName, int moduleIndex, string fxName)
        {
            if (fxInstance == null)
                return;
            if (loggedKeys.Contains(BuildKey(partName, moduleIndex, fxName)))
                return;

            var probe = fxInstance.AddComponent<GhostFxEmissionProbe>();
            probe.partName = partName ?? "?";
            probe.moduleIndex = moduleIndex;
            probe.fxName = fxName ?? "?";
        }

        internal static string BuildKey(string partName, int moduleIndex, string fxName)
        {
            return $"{partName}|{moduleIndex}|{fxName}";
        }

        /// <summary>
        /// Pure mean-direction computation over world-space velocities. Returns false
        /// when the sample is empty or the mean is degenerate (velocities cancel).
        /// </summary>
        internal static bool TryComputeMeanVelocity(
            List<Vector3> worldVelocities, out Vector3 meanDirection, out float meanSpeed)
        {
            meanDirection = Vector3.zero;
            meanSpeed = 0f;
            if (worldVelocities == null || worldVelocities.Count == 0)
                return false;

            Vector3 sum = Vector3.zero;
            float speedSum = 0f;
            for (int i = 0; i < worldVelocities.Count; i++)
            {
                sum += worldVelocities[i];
                speedSum += worldVelocities[i].magnitude;
            }

            meanSpeed = speedSum / worldVelocities.Count;
            if (sum.magnitude < 0.01f)
                return false;

            meanDirection = sum.normalized;
            return true;
        }

        /// <summary>Pure log-line formatting (xUnit-testable).</summary>
        internal static string BuildProbeLogLine(
            string partName, int moduleIndex, string fxName, int particleCount,
            Vector3 meanDirection, float meanSpeed, float angleFromDown,
            Quaternion instanceLocalRotation, Vector3 parentUpWorld,
            Vector3 worldPosition, string rootName, int rootEnabledRenderers)
        {
            // Raw quaternion components, not eulerAngles (a native ECall, untestable in xUnit).
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return $"measured: part='{partName}' midx={moduleIndex} fx='{fxName}' " +
                $"particles={particleCount} meanDirWorld=({meanDirection.x.ToString("F2", ic)}," +
                $"{meanDirection.y.ToString("F2", ic)},{meanDirection.z.ToString("F2", ic)}) " +
                $"meanSpeed={meanSpeed.ToString("F2", ic)} " +
                $"angleFromDown={angleFromDown.ToString("F1", ic)} " +
                $"instLocalRotQ=({instanceLocalRotation.x.ToString("F2", ic)}," +
                $"{instanceLocalRotation.y.ToString("F2", ic)}," +
                $"{instanceLocalRotation.z.ToString("F2", ic)}," +
                $"{instanceLocalRotation.w.ToString("F2", ic)}) " +
                $"parentUpWorld=({parentUpWorld.x.ToString("F2", ic)}," +
                $"{parentUpWorld.y.ToString("F2", ic)},{parentUpWorld.z.ToString("F2", ic)}) " +
                // Orphan-FX hunt fields: where this FX lives and whether its ghost root
                // has ANY visible geometry. rootRenderers=0 means smoke with no part.
                $"posWorld=({worldPosition.x.ToString("F1", ic)}," +
                $"{worldPosition.y.ToString("F1", ic)},{worldPosition.z.ToString("F1", ic)}) " +
                $"root='{rootName}' rootRenderers={rootEnabledRenderers}";
        }

        /// <summary>
        /// Counts enabled mesh-bearing renderers (MeshRenderer/SkinnedMeshRenderer, not
        /// particle renderers) under the topmost ancestor of a transform. Zero means the
        /// FX instance belongs to a ghost with NO visible geometry: an orphan effect.
        /// </summary>
        internal static int CountRootEnabledMeshRenderers(Transform t, out string rootName)
        {
            rootName = "?";
            if (t == null)
                return 0;
            Transform root = t;
            while (root.parent != null)
                root = root.parent;
            rootName = root.name;

            int enabled = 0;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || renderers[i] is ParticleSystemRenderer)
                    continue;
                if (renderers[i].enabled)
                    enabled++;
            }
            return enabled;
        }

        private void Start()
        {
            systems = GetComponentsInChildren<ParticleSystem>(true);
            startTime = Time.time;
            if (systems == null || systems.Length == 0)
            {
                Destroy(this);
            }
        }

        private void Update()
        {
            string key = BuildKey(partName, moduleIndex, fxName);
            if (loggedKeys.Contains(key))
            {
                Destroy(this);
                return;
            }

            var velocities = new List<Vector3>();
            for (int s = 0; s < systems.Length; s++)
            {
                ParticleSystem ps = systems[s];
                if (ps == null || ps.particleCount == 0)
                    continue;

                if (buffer == null || buffer.Length < ps.main.maxParticles)
                    buffer = new ParticleSystem.Particle[ps.main.maxParticles];
                int count = ps.GetParticles(buffer);
                bool localSpace = ps.main.simulationSpace == ParticleSystemSimulationSpace.Local;
                for (int i = 0; i < count; i++)
                {
                    Vector3 v = buffer[i].velocity;
                    velocities.Add(localSpace ? ps.transform.rotation * v : v);
                }
            }

            if (velocities.Count >= MinParticlesForSample &&
                TryComputeMeanVelocity(velocities, out Vector3 meanDir, out float meanSpeed))
            {
                loggedKeys.Add(key);
                float angleFromDown = Vector3.Angle(meanDir, Vector3.down);
                int rootRenderers = CountRootEnabledMeshRenderers(transform, out string rootName);
                ParsekLog.Verbose("FxEmissionProbe", BuildProbeLogLine(
                    partName, moduleIndex, fxName, velocities.Count, meanDir, meanSpeed,
                    angleFromDown, transform.localRotation,
                    transform.parent != null ? transform.parent.up : Vector3.zero,
                    transform.position, rootName, rootRenderers));
                Destroy(this);
                return;
            }

            if (Time.time - startTime > TimeoutSeconds)
            {
                loggedKeys.Add(key);
                ParsekLog.Verbose("FxEmissionProbe",
                    $"timeout: part='{partName}' midx={moduleIndex} fx='{fxName}' " +
                    $"never sampled {MinParticlesForSample}+ live particles in {TimeoutSeconds:F0}s " +
                    "(instance never emitted, or emits below the sample threshold)");
                Destroy(this);
            }
        }

        internal static void ResetForTesting()
        {
            loggedKeys.Clear();
        }
    }
}
