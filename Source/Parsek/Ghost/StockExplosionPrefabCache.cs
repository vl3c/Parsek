using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Silent copies of stock's own explosion effects (<c>FXMonger.explosions</c>), kept from the
    /// flight scene, so a ghost crash that cannot go through <c>FXMonger.Explode</c> still looks
    /// like stock's (owner ruling G.9 ruling 2, <c>docs/dev/design-part-ghost-parity.md</c>).
    /// Those cases are the Space Center view, which has no FXMonger, and the flight pause menu,
    /// where stock's one-shot explosion sound cannot be paused.
    ///
    /// Decompiled <c>FXMonger.LateUpdate</c> (KSP 1.12.5) plays a blast as
    /// <c>Instantiate(explosions[(int)(Clamp01(power) * (n - 1))])</c>, <c>SetActive(true)</c>,
    /// position, <c>transform.up = FlightGlobals.upAxis</c>, then a <c>PlayOneShot</c> of the
    /// matching <c>explosionSounds</c> clip on the instance's AudioSource. <see cref="TrySpawn"/>
    /// does the same minus the sound: every AudioSource on the kept copies is muted at zero volume
    /// (left enabled, because some prefabs play their source themselves and Unity warns on a
    /// disabled one), and the callers keep their own explosion audio decisions.
    ///
    /// The copies are instantiated under one inactive DontDestroyOnLoad holder, so no component
    /// on them wakes while they are kept, and they survive the flight scene unloading whether
    /// stock's array holds scene objects or project prefabs. A session that has never been in
    /// flight has no copies, and the caller falls back to Parsek's hand-built explosion.
    /// </summary>
    internal static class StockExplosionPrefabCache
    {
        /// <summary>A copy lives at most this long if its own prefab never destroys it.</summary>
        internal const float SafetyLifetimeSeconds = 45f;

        private static GameObject holder;
        private static GameObject[] copies;

        // This frame's copies, merged as FXMonger merges its queue (FxMongerFrameQueue).
        private static readonly FxMongerFrameQueue<GameObject> frameBlasts = new FxMongerFrameQueue<GameObject>();

        /// <summary>
        /// The stock explosion a blast of <paramref name="power"/> draws, the same index
        /// <c>FXMonger.LateUpdate</c> takes; -1 with no prefabs or a NaN power.
        /// </summary>
        internal static int ResolvePrefabIndex(double power, int prefabCount)
        {
            if (prefabCount <= 0 || double.IsNaN(power)) return -1;
            double p = power < 0.0 ? 0.0 : (power > 1.0 ? 1.0 : power);
            return (int)(p * (prefabCount - 1));
        }

        /// <summary>Whether copies are kept (the holder can be destroyed under us on quit).</summary>
        internal static bool HasCopies => holder != null && copies != null && copies.Length > 0;

        /// <summary>
        /// Keeps silent copies of the live FXMonger's explosion prefabs, once per process. Called on
        /// every flight-ready with <c>GhostVisualBuilder.ResolveLiveFxMonger()</c>; a no-op once
        /// copies are kept or when FXMonger is not live (null).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CaptureFrom(FXMonger monger)
        {
            if (HasCopies) return;
            if (monger == null)
            {
                ParsekLog.Verbose("ExplosionFx", "Stock explosion copies not kept: no live FXMonger");
                return;
            }
            GameObject[] prefabs = monger.explosions;
            if (prefabs == null || prefabs.Length == 0)
            {
                ParsekLog.Warn("ExplosionFx", "Stock explosion copies not kept: FXMonger.explosions is empty");
                return;
            }

            try
            {
                var keep = new GameObject("ParsekStockExplosionCopies");
                keep.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(keep);

                var kept = new GameObject[prefabs.Length];
                int keptCount = 0, mutedSources = 0;
                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (prefabs[i] == null) continue;
                    GameObject copy = UnityEngine.Object.Instantiate(prefabs[i], keep.transform, false);
                    copy.name = prefabs[i].name;
                    foreach (AudioSource src in copy.GetComponentsInChildren<AudioSource>(true))
                    {
                        src.playOnAwake = false;
                        src.mute = true;
                        src.volume = 0f;
                        mutedSources++;
                    }
                    kept[i] = copy;
                    keptCount++;
                }

                holder = keep;
                copies = kept;
                ParsekLog.Info("ExplosionFx",
                    $"Kept {keptCount.ToString(CultureInfo.InvariantCulture)}/" +
                    $"{prefabs.Length.ToString(CultureInfo.InvariantCulture)} stock explosion copies " +
                    $"for the Space Center and pause-menu ghost crashes " +
                    $"(audio sources muted={mutedSources.ToString(CultureInfo.InvariantCulture)}; " +
                    $"names={DescribeNames(kept)})");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ExplosionFx", $"Stock explosion copies not kept: {ex.Message}");
                holder = null;
                copies = null;
            }
        }

        /// <summary>
        /// Plays a silent copy of stock's explosion for <paramref name="power"/> at
        /// <paramref name="worldPosition"/>; null when no copy is kept for that power. A blast within
        /// 10 m of a copy spawned this frame merges into it as FXMonger merges its queue
        /// (<see cref="FxMongerBlastMerge"/>): that copy moves to the power-weighted mean, keeps its
        /// prefab, and is returned with <paramref name="merged"/> true.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GameObject TrySpawn(Vector3 worldPosition, double power, out string failureReason,
            out bool merged)
        {
            merged = false;
            if (!HasCopies)
            {
                failureReason = "no stock explosion copies kept (not in flight yet this session)";
                return null;
            }
            int frameNumber = Time.frameCount;
            if (frameBlasts.Offer(frameNumber, worldPosition, power, out Vector3d mergedAt, out GameObject into) >= 0)
            {
                if (into == null)
                {
                    failureReason = "merged into a stock explosion copy this frame that is already gone";
                    return null;
                }
                into.transform.position = (Vector3)mergedAt;
                merged = true;
                failureReason = null;
                ParsekLog.VerboseRateLimited("ExplosionFx", "stock-copy-merge",
                    $"Stock explosion copy merged a blast of power={power.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"at ({worldPosition.x:F1},{worldPosition.y:F1},{worldPosition.z:F1}) into this frame's copy, " +
                    $"now at ({mergedAt.x:F1},{mergedAt.y:F1},{mergedAt.z:F1})", 5.0);
                return into;
            }
            int index = ResolvePrefabIndex(power, copies.Length);
            GameObject source = index >= 0 ? copies[index] : null;
            if (source == null)
            {
                failureReason = $"no stock explosion copy at index {index.ToString(CultureInfo.InvariantCulture)}";
                return null;
            }

            try
            {
                // Placed by the Instantiate itself, so nothing on the copy wakes at the holder.
                Quaternion rotation = Quaternion.FromToRotation(Vector3.up, ResolveUp(worldPosition));
                GameObject blast = UnityEngine.Object.Instantiate(source, worldPosition, rotation);
                blast.name = "GhostStockExplosionFx";
                blast.SetActive(true);
                // The blast stays where it was made when the floating origin shifts.
                GhostWorldSpaceEmitter.AttachOwn(blast);
                UnityEngine.Object.Destroy(blast, SafetyLifetimeSeconds);
                frameBlasts.Add(frameNumber, worldPosition, power, blast);
                failureReason = null;
                ParsekLog.VerboseRateLimited("ExplosionFx", "stock-copy-spawn",
                    $"Stock explosion copy '{source.name}' (index {index.ToString(CultureInfo.InvariantCulture)}/" +
                    $"{copies.Length.ToString(CultureInfo.InvariantCulture)}) played silently at " +
                    $"({worldPosition.x:F1},{worldPosition.y:F1},{worldPosition.z:F1}) " +
                    $"power={power.ToString("F2", CultureInfo.InvariantCulture)}", 5.0);
                return blast;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return null;
            }
        }

        // Stock sets transform.up from FlightGlobals.upAxis (the active vessel's); a ghost's blast
        // takes the up of the body under it, which is the same thing beside the active vessel.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Vector3 ResolveUp(Vector3 worldPosition)
        {
            try
            {
                Vector3 up = (Vector3)FlightGlobals.getUpAxis((Vector3d)worldPosition);
                if (up.sqrMagnitude > 1e-6f) return up.normalized;
            }
            catch (Exception)
            {
                // No FlightGlobals in this scene: fall through to world up.
            }
            return Vector3.up;
        }

        private static string DescribeNames(GameObject[] kept)
        {
            var parts = new string[kept.Length];
            for (int i = 0; i < kept.Length; i++)
                parts[i] = kept[i] != null ? kept[i].name : "-";
            return string.Join(",", parts);
        }
    }
}
