using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // Ghost audio: audio source build, watch-pivot anchoring, rolloff defaults and the missing-clip warning dedupe.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Build AudioSource components for engine modules on a ghost part.
        /// Returns a list of AudioGhostInfo (one per engine module). Playback later
        /// enforces the per-ghost concurrent looped-audio cap at runtime so engines
        /// that activate later in the recording still have a source available.
        /// </summary>
        internal static List<AudioGhostInfo> TryBuildAudioFX(
            Part prefab, uint persistentId, string partName, GameObject ghostRoot)
        {
            if (prefab == null || ghostRoot == null) return null;

            // Find the ghost part GO by persistentId
            Transform partTransform = FindGhostPartTransform(ghostRoot, persistentId);
            if (partTransform == null) return null;

            var result = new List<AudioGhostInfo>();
            // Keep one looped source per engine module. The 4-sound cap now lives in
            // GhostPlaybackLogic.EnforceLoopedAudioPlaybackCap so late-activating engines
            // are not discarded at build time.

            // Engine audio
            var engines = prefab.Modules.GetModules<ModuleEngines>();
            if (engines != null)
            {
                for (int i = 0; i < engines.Count; i++)
                {
                    GhostAudioPriorityClass priorityClass =
                        GhostAudioPresets.ClassifyLoopedPriority(engines[i].propellants);
                    string clipPath = GhostAudioPresets.ResolveEngineAudioClip(
                        prefab,
                        i,
                        out bool useQuietVolumeCurve);
                    if (clipPath == null) continue;

                    AudioClip clip = GameDatabase.Instance.GetAudioClip(clipPath);
                    if (clip == null)
                    {
                        // Dedupe per (ghost, pid, clipPath): a single ghost rebuilding its audio
                        // sources (multiple engines on same pid, loop rebuilds) should warn once
                        // per missing clip rather than per audio event. Cleared when the ghost is
                        // destroyed so a fresh spawn can warn again if the clip is still missing.
                        WarnMissingAudioClipOnce(ghostRoot?.name, persistentId, clipPath, partName);
                        continue;
                    }

                    var source = CreateGhostAudioSource(
                        partTransform.gameObject,
                        clip,
                        loop: true,
                        priorityClass);
                    result.Add(new AudioGhostInfo
                    {
                        partPersistentId = persistentId,
                        moduleIndex = i,
                        priorityClass = priorityClass,
                        audioSource = source,
                        clip = clip,
                        volumeCurve = GhostAudioPresets.BuildVolumeCurve(useQuietVolumeCurve),
                        pitchCurve = GhostAudioPresets.BuildDefaultPitchCurve(),
                        currentPower = 0f
                    });
                }
            }

            // RCS audio omitted — RCS sounds are too subtle to be audible at ghost distances.
            // Engine audio alone is sufficient for the "hear a rocket in the distance" goal.

            if (result.Count > 0)
            {
                ParsekLog.VerboseRateLimited("GhostAudio",
                    $"audio-build-{partName}-{persistentId}",
                    $"Built {result.Count} audio source(s) for '{partName}' pid={persistentId}",
                    5.0);
            }

            return result.Count > 0 ? result : null;
        }

        internal static void AttachGhostAudioToWatchPivot(GhostBuildResult result, Transform cameraPivot)
        {
            if (result == null || cameraPivot == null)
                return;

            int loopSourceCount = 0;
            if (result.audioInfos != null)
            {
                for (int i = 0; i < result.audioInfos.Count; i++)
                {
                    AudioSource source = result.audioInfos[i]?.audioSource;
                    if (source == null)
                        continue;

                    ReanchorGhostAudioSource(source, cameraPivot);
                    loopSourceCount++;
                }
            }

            if (loopSourceCount > 0)
            {
                string ghostName = result.root != null ? result.root.name : "<null-root>";
                ParsekLog.VerboseRateLimited("GhostAudio",
                    "watch-pivot-audio-" + ghostName,
                    $"Attached watch-pivot ghost audio on '{ghostName}' to '{cameraPivot.name}' " +
                    $"(loop={loopSourceCount}, " +
                    $"spatialBlend={GhostAudioSpatialBlend:0.##}, panStereo=0)",
                    2.0);
            }
        }

        private static AudioSource CreateGhostAudioSource(
            GameObject go, AudioClip clip, bool loop, GhostAudioPriorityClass priorityClass)
        {
            if (go == null)
                return null;

            // Keep audio on a dedicated child so re-anchoring to cameraPivot later
            // does not reparent the part visual GameObject itself.
            var sourceObject = new GameObject("ghost_audio_loop");
            sourceObject.transform.SetParent(go.transform, false);
            var source = sourceObject.AddComponent<AudioSource>();
            source.clip = clip;
            ApplyGhostAudioDefaults(
                source,
                loop,
                GhostAudioPresets.ComputeRuntimePriority(priorityClass, distanceMeters: 0.0));
            source.volume = 0f;
            return source;
        }

        private static void ApplyGhostAudioDefaults(AudioSource source, bool loop, int priority)
        {
            if (source == null)
                return;

            source.spatialBlend = GhostAudioSpatialBlend;
            source.panStereo = 0f;
            source.dopplerLevel = 0f;
            ConfigureGhostRolloff(source);
            source.priority = priority;
            source.loop = loop;
            source.playOnAwake = false;
        }

        private static void ReanchorGhostAudioSource(AudioSource source, Transform anchor)
        {
            if (source == null || anchor == null)
                return;

            source.transform.SetParent(anchor, false);
            source.transform.localPosition = Vector3.zero;
            source.transform.localRotation = Quaternion.identity;
            ApplyGhostAudioDefaults(source, source.loop, source.priority);
        }

        private static void ConfigureGhostRolloff(AudioSource source)
        {
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = DistanceThresholds.GhostAudio.RolloffMinDistanceMeters;
            source.maxDistance = DistanceThresholds.GhostAudio.RolloffMaxDistanceMeters;
        }

        internal static void WarnMissingAudioClipOnce(
            string ghostRootName, uint persistentId, string clipPath, string partName)
        {
            // Guard against null root name (defensive — ghostRoot was null-checked earlier).
            // Fall back to a sentinel bucket so we still dedupe a null-root ghost against itself.
            string bucketKey = ghostRootName ?? "<null-root>";
            string entryKey = persistentId.ToString(CultureInfo.InvariantCulture) + "|" + (clipPath ?? "");

            HashSet<string> bucket;
            if (!missingAudioClipWarnedByGhost.TryGetValue(bucketKey, out bucket))
            {
                bucket = new HashSet<string>(System.StringComparer.Ordinal);
                missingAudioClipWarnedByGhost[bucketKey] = bucket;
            }

            if (!bucket.Add(entryKey))
            {
                // Already warned for this (ghost, pid, clip) — silently drop.
                return;
            }

            ParsekLog.Warn("GhostAudio",
                $"AudioClip not found: '{clipPath}' for engine on '{partName}' pid={persistentId}");
        }

        internal static void ClearMissingAudioClipWarnings(string ghostRootName)
        {
            if (ghostRootName == null) return;
            missingAudioClipWarnedByGhost.Remove(ghostRootName);
        }

        internal static void ResetMissingAudioClipWarningsForTesting()
        {
            missingAudioClipWarnedByGhost.Clear();
        }
    }
}
