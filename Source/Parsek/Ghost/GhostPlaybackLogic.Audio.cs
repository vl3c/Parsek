using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Ghost audio: looped engine audio and its playback cap, explosion one-shots, mute / pause.
    internal static partial class GhostPlaybackLogic
    {
        #region Ghost Audio Control

        internal static double ResolveAudioPriorityDistance(GhostPlaybackState state)
        {
            if (state == null)
                return 0.0;

            double distanceMeters = state.lastRenderDistance;
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0.0)
                distanceMeters = state.lastDistance;
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0.0)
                return 0.0;

            return distanceMeters;
        }

        internal static void UpdateLoopedAudioPriority(GhostPlaybackState state, AudioGhostInfo info)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;

            int priority = GhostAudioPresets.ComputeRuntimePriority(
                info.priorityClass,
                ResolveAudioPriorityDistance(state));
            if (info.audioSource.priority != priority)
                info.audioSource.priority = priority;
        }

        internal static List<AudioGhostInfo> SelectHighestPriorityActiveLoopedGhostAudioSources(
            IList<AudioGhostInfo> audioInfos, int maxSources)
        {
            var result = new List<AudioGhostInfo>();
            if (audioInfos == null || maxSources <= 0)
                return result;

            AudioGhostInfo first = null;
            AudioGhostInfo second = null;
            AudioGhostInfo third = null;
            AudioGhostInfo fourth = null;
            int firstOrder = int.MaxValue;
            int secondOrder = int.MaxValue;
            int thirdOrder = int.MaxValue;
            int fourthOrder = int.MaxValue;
            for (int i = 0; i < audioInfos.Count; i++)
            {
                AudioGhostInfo info = audioInfos[i];
                if (info == null || info.currentPower <= 0f)
                    continue;

                InsertLoopedAudioSelectionCandidate(
                    info,
                    i,
                    maxSources,
                    ref first,
                    ref firstOrder,
                    ref second,
                    ref secondOrder,
                    ref third,
                    ref thirdOrder,
                    ref fourth,
                    ref fourthOrder);
            }

            AppendIfNotNull(result, first);
            if (maxSources > 1) AppendIfNotNull(result, second);
            if (maxSources > 2) AppendIfNotNull(result, third);
            if (maxSources > 3) AppendIfNotNull(result, fourth);

            return result;
        }

        internal static void EnforceLoopedAudioPlaybackCapWithTestingOverride(GhostPlaybackState state)
        {
            var overrideForTesting = EnforceLoopedAudioPlaybackCapOverrideForTesting;
            if (overrideForTesting != null && overrideForTesting(state))
                return;

            EnforceLoopedAudioPlaybackCap(state);
        }

        internal static void EnforceLoopedAudioPlaybackCap(GhostPlaybackState state)
        {
            if (state?.audioInfos == null)
                return;

            if (state.audioPaused)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info == null || ReferenceEquals(info.audioSource, null))
                        continue;

                    if (info.currentPower <= 0f)
                        StopLoopedGhostAudio(info, "paused-power=0", force: true);
                    else if (info.audioSource.isPlaying)
                        info.audioSource.Pause();
                }
                return;
            }

            if (state.audioMuted || state.atmosphereFactor < 0.001f)
            {
                string stopReason = state.audioMuted ? "muted" : "vacuum";
                foreach (var info in state.audioInfos.Values)
                {
                    if (info != null && !ReferenceEquals(info.audioSource, null) && info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, stopReason);
                }
                return;
            }

            AudioGhostInfo first = null;
            AudioGhostInfo second = null;
            AudioGhostInfo third = null;
            AudioGhostInfo fourth = null;
            int firstOrder = int.MaxValue;
            int secondOrder = int.MaxValue;
            int thirdOrder = int.MaxValue;
            int fourthOrder = int.MaxValue;
            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || ReferenceEquals(info.audioSource, null))
                    continue;

                UpdateLoopedAudioPriority(state, info);

                if (info.currentPower > 0f)
                {
                    InsertLoopedAudioSelectionCandidate(
                        info,
                        info.selectionOrder,
                        GhostAudioPresets.MaxAudioSourcesPerGhost,
                        ref first,
                        ref firstOrder,
                        ref second,
                        ref secondOrder,
                        ref third,
                        ref thirdOrder,
                        ref fourth,
                        ref fourthOrder);
                }
                else if (info.audioSource.isPlaying)
                {
                    StopLoopedGhostAudio(info, "power=0");
                }
            }

            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || ReferenceEquals(info.audioSource, null) || info.currentPower <= 0f)
                    continue;

                float volume = ComputeGhostAudioVolume(
                    info.volumeCurve.Evaluate(info.currentPower),
                    state.atmosphereFactor);
                if (!IsLoopedAudioSelectedForPlayback(info, first, second, third, fourth))
                {
                    if (info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "capped");
                    continue;
                }
                if (volume <= 0f)
                {
                    if (info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "volume=0");
                    continue;
                }

                if (info.audioSource.volume != volume)
                    info.audioSource.volume = volume;

                float pitch = info.pitchCurve.Evaluate(info.currentPower);
                if (info.audioSource.pitch != pitch)
                    info.audioSource.pitch = pitch;

                if (!ReferenceEquals(info.audioSource.clip, info.clip))
                    info.audioSource.clip = info.clip;

                if (!info.audioSource.isPlaying && CanStartLoopedGhostAudio(info.audioSource))
                    StartLoopedGhostAudio(info, volume, pitch);
            }
        }

        /// <summary>
        /// Set engine audio volume/pitch from recorded throttle power.
        /// Called alongside SetEngineEmission for EngineIgnited/Throttle/Shutdown events.
        /// </summary>
        /// <summary>
        /// P8 step 1 precondition classifier (pure). Mirrors this method's only two
        /// `return false` paths exactly - a missing audio dictionary and a missing entry
        /// for the engine key - so the family line can name which, rather than folding
        /// both into a bare boolean. Everything past those two guards returns true.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyEngineAudioApply(
            GhostPlaybackState state, PartEvent evt)
        {
            if (state.audioInfos == null) return GhostPartEventOutcome.NoFamilyState;
            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            return state.audioInfos.ContainsKey(key)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoInfoForPart;
        }

        internal static bool SetEngineAudio(
            GhostPlaybackState state,
            PartEvent evt,
            float power,
            bool enforcePlaybackCap = true)
        {
            if (state.audioInfos == null) return false;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            AudioGhostInfo info;
            if (!state.audioInfos.TryGetValue(key, out info)) return false;

            info.currentPower = power;
            if (state.audioPaused)
            {
                if (!ReferenceEquals(info.audioSource, null) && power <= 0f)
                    StopLoopedGhostAudio(info, "paused-power=0", force: true);
                return true;
            }
            if (state.audioMuted)
            {
                // Keep tracked power in sync during warp so unmute resumes the correct state.
                if (!ReferenceEquals(info.audioSource, null))
                    StopLoopedGhostAudio(info, "muted");
                return true;
            }
            if (ShouldEnforceLoopedAudioPlaybackCap(
                    hasAudioSource: !ReferenceEquals(info.audioSource, null),
                    enforcePlaybackCap: enforcePlaybackCap))
            {
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
            }
            return true;
        }

        /// <summary>
        /// Whether a <see cref="SetEngineAudio"/> call that reached the play/stop stage should
        /// run the looped-audio playback cap. BOTH terms are required: a ghost part with no
        /// AudioSource has nothing to cap, and a DEFERRED batch member passes
        /// <c>enforcePlaybackCap: false</c> so the cap runs once after the batch instead of on
        /// every member. Extracted from the two guards it replaces (byte-identical: the old
        /// null-source early return did nothing but skip this cap) so the flag is testable
        /// headlessly - an AudioGhostInfo cannot carry a Unity AudioSource in xUnit, which is
        /// why the flag used to be unreachable from any cell.
        /// </summary>
        internal static bool ShouldEnforceLoopedAudioPlaybackCap(
            bool hasAudioSource, bool enforcePlaybackCap)
        {
            return hasAudioSource && enforcePlaybackCap;
        }

        internal static bool CanStartLoopedGhostAudio(bool sourceExists, bool sourceIsActiveAndEnabled)
        {
            return sourceExists && sourceIsActiveAndEnabled;
        }

        internal static bool CanStartLoopedGhostAudio(AudioSource audioSource)
        {
            return CanStartLoopedGhostAudio(
                sourceExists: !ReferenceEquals(audioSource, null),
                sourceIsActiveAndEnabled: !ReferenceEquals(audioSource, null) && audioSource.isActiveAndEnabled);
        }

        /// <summary>
        /// Stop all audio sources for a given part (by PID).
        /// Used defensively on decouple/destroy.
        /// </summary>
        internal static void StopAudioForPart(GhostPlaybackState state, uint partPersistentId)
        {
            ClearTrackedAudioPowerForPart(state, partPersistentId);
            if (state?.audioInfos == null) return;
            foreach (var info in state.audioInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                if (info.audioSource != null && info.audioSource.isPlaying)
                    StopLoopedGhostAudio(info, "part-removed");
            }
        }

        /// <summary>
        /// Triggers KSP's stock terminal explosion FX (visual + audio) for a destroyed ghost.
        /// FXMonger handles its own audio mixing — multiple concurrent explosions get fresh
        /// AudioSources per call (decompiled FXMonger.LateUpdate ~line 553-573) and stock spatial
        /// coalescing merges blasts within 10 m. Returns true when FXMonger queued the FX,
        /// false when FXMonger was unavailable or threw and the custom particle fallback ran.
        ///
        /// No temporal gate: the previous "audio gate" implementation reserved the global gate
        /// for the full <c>sound_explosion_large</c> clip duration (~8.6 s) which then suppressed
        /// every following terminal-vessel explosion inside the window — multi-debris breakups
        /// in watch mode were nearly silent because all but the first ghost fell to a
        /// visual-only fallback. Stock KSP itself does not gate temporally; it lets the mixer
        /// handle simultaneous voices and relies on 3D rolloff to attenuate distant ones.
        /// </summary>
        internal static bool TryTriggerStockExplosionFxOrCustom(
            Vector3 worldPosition,
            double power,
            float vesselLength,
            string contextDescription,
            TryTriggerStockExplosionFxDelegate triggerStockExplosionFx = null,
            Action<Vector3, float> spawnExplosionFx = null,
            Action<StockExplosionFxResult> recordResult = null)
        {
            string context = string.IsNullOrEmpty(contextDescription)
                ? "ghost explosion"
                : contextDescription;
            TryTriggerStockExplosionFxDelegate triggerStock = triggerStockExplosionFx
                ?? ((Vector3 pos, double pwr, out string failure) =>
                    GhostVisualBuilder.TryTriggerStockExplosionFx(pos, pwr, out failure));
            Action<Vector3, float> spawnCustom =
                spawnExplosionFx ?? ((pos, len) => GhostVisualBuilder.SpawnExplosionFx(pos, len));

            if (triggerStock(worldPosition, power, out string stockFxFailure))
            {
                recordResult?.Invoke(StockExplosionFxResult.StockQueued);
                return true;
            }

            ParsekLog.Warn("ExplosionFx",
                $"FXMonger.Explode did not queue stock FX for {context}; " +
                $"falling back to custom FX: {stockFxFailure}");
            spawnCustom(worldPosition, vesselLength);
            recordResult?.Invoke(StockExplosionFxResult.StockFailedCustomVisualSpawned);
            return false;
        }

        /// <summary>
        /// Plays an independent explosion one-shot at <paramref name="worldPosition"/>. Used by the
        /// KSC playback path where FXMonger isn't loaded (it lives in the flight scene only),
        /// so Parsek owns the AudioSource. Spawns a fresh <c>AudioSource</c> per call (the source
        /// auto-destroys after the clip finishes), so concurrent voices don't stack on a single
        /// shared source — Unity's mixer plus the source's 3D rolloff handle simultaneous KSC ghost
        /// explosions the same way stock FXMonger handles concurrent flight-scene explosions.
        ///
        /// <paramref name="power"/> drives clip selection through <see cref="GhostAudioPresets.ResolveDestroyedClipByPower"/>
        /// so size-appropriate clips play here too: a small KSC ghost picks the shorter
        /// `sound_explosion_debris1`, a heavy lifter picks `sound_explosion_large`, mirroring how
        /// stock FXMonger's `explosionSounds[]` array is indexed in the flight-scene path.
        /// </summary>
        internal static bool TryPlayIndependentExplosionOneShot(
            Vector3 worldPosition,
            float atmosphereFactor,
            double distanceMeters,
            double power,
            string contextDescription,
            ResolveExplosionOneShotAudioCandidateDelegate resolveExplosionAudioCandidate = null,
            PlayExplosionOneShotAudioDelegate playExplosionAudio = null)
        {
            string context = string.IsNullOrEmpty(contextDescription)
                ? "ghost explosion"
                : contextDescription;
            ResolveExplosionOneShotAudioCandidateDelegate resolveCandidate =
                resolveExplosionAudioCandidate
                ?? (() => ResolveExplosionOneShotAudioCandidate(atmosphereFactor, distanceMeters, power));

            ExplosionOneShotAudioCandidate candidate = resolveCandidate();
            if (!candidate.canPlay)
            {
                ParsekLog.Warn("GhostAudio",
                    $"Explosion one-shot unavailable for {context}: " +
                    $"{(string.IsNullOrEmpty(candidate.failureReason) ? "unknown reason" : candidate.failureReason)}");
                return false;
            }

            try
            {
                PlayExplosionOneShotAudioDelegate playAudio = playExplosionAudio ?? QueueExplosionOneShotAudio;
                playAudio(worldPosition, candidate);
                ParsekLog.Verbose("GhostAudio",
                    $"Explosion one-shot queued for {context}: clip='{candidate.clipPath}' " +
                    $"vol={candidate.volume.ToString("F2", CultureInfo.InvariantCulture)}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GhostAudio",
                    $"Explosion one-shot queue failed for {context}: {ex.Message}");
                return false;
            }
        }

        internal static ExplosionOneShotAudioCandidate ResolveExplosionOneShotAudioCandidate(
            float atmosphereFactor,
            double distanceMeters,
            double power)
        {
            string clipPath = GhostAudioPresets.ResolveDestroyedClipByPower(power);
            if (clipPath == null)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    failureReason = "no explosion clip configured"
                };
            }

            AudioClip clip = null;
            try
            {
                clip = GameDatabase.Instance != null
                    ? GameDatabase.Instance.GetAudioClip(clipPath)
                    : null;
            }
            catch (Exception ex)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = clipPath,
                    failureReason = $"clip lookup failed ({ex.GetType().Name}: {ex.Message})"
                };
            }

            if (clip == null)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = clipPath,
                    failureReason = $"AudioClip not found: '{clipPath}'"
                };
            }

            float volume = ComputeGhostAudioVolume(GhostAudioPresets.OneShotVolumeScale, atmosphereFactor);
            if (volume <= 0f)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = clipPath,
                    clip = clip,
                    clipLengthSeconds = NormalizeOneShotDurationSeconds(clip.length),
                    failureReason = "computed volume is zero"
                };
            }

            return new ExplosionOneShotAudioCandidate
            {
                canPlay = true,
                clipPath = clipPath,
                clip = clip,
                clipLengthSeconds = NormalizeOneShotDurationSeconds(clip.length),
                volume = volume,
                priority = GhostAudioPresets.ComputeRuntimePriority(
                    GhostAudioPresets.ClassifyOneShotPriority(PartEventType.Destroyed),
                    distanceMeters)
            };
        }

        internal static void QueueExplosionOneShotAudio(
            Vector3 worldPosition,
            ExplosionOneShotAudioCandidate candidate)
        {
            if (candidate.clip == null)
                throw new InvalidOperationException("candidate has no AudioClip");

            GameObject sourceObject = null;
            try
            {
                sourceObject = new GameObject(ExplosionOneShotAudioObjectName);
                sourceObject.transform.position = worldPosition;
                var source = sourceObject.AddComponent<AudioSource>();
                source.clip = candidate.clip;
                source.spatialBlend = GhostVisualBuilder.GhostAudioSpatialBlend;
                source.panStereo = 0f;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = DistanceThresholds.GhostAudio.RolloffMinDistanceMeters;
                source.maxDistance = DistanceThresholds.GhostAudio.RolloffMaxDistanceMeters;
                source.priority = candidate.priority;
                source.loop = false;
                source.playOnAwake = false;
                source.volume = 1f;
                TrackExplosionOneShotAudioSource(source);
                source.PlayOneShot(candidate.clip, candidate.volume);
                UnityEngine.Object.Destroy(
                    sourceObject,
                    NormalizeOneShotDurationSeconds(candidate.clipLengthSeconds) + 0.25f);
            }
            catch
            {
                if (sourceObject != null)
                    UnityEngine.Object.Destroy(sourceObject);
                throw;
            }
        }

        internal static int PauseExplosionOneShotAudio()
        {
            PruneExplosionOneShotAudioSources();
            int paused = 0;
            for (int i = 0; i < activeExplosionOneShotAudioSources.Count; i++)
            {
                AudioSource source = activeExplosionOneShotAudioSources[i];
                if (source != null && source.isPlaying)
                {
                    source.Pause();
                    if (!ContainsAudioSourceReference(pausedExplosionOneShotAudioSources, source))
                        pausedExplosionOneShotAudioSources.Add(source);
                    paused++;
                }
            }

            // Also pause any FXMonger-spawned explosion AudioSources still playing in-flight.
            // FXMonger.LateUpdate spawns a fresh AudioSource per ProtoExplosion (decompiled
            // FXMonger.LateUpdate ~line 553-573 / 781-803 / 882-904) and PlayOneShots SHIP_VOLUME
            // — those sources are owned by FXMonger, not Parsek's per-vessel audioInfos, so
            // PauseAllAudio's per-source loop above doesn't reach them. Without this walk, opening
            // the Esc menu mid-explosion would leave the FXMonger PlayOneShot voices playing
            // through the pause (regressing the pre-fix tracked-oneShotAudio pause behavior, where
            // PauseAllAudio's `state.oneShotAudio.audioSource.Pause()` covered the per-vessel
            // PlayOneShot voice). Tracked sources pile into `pausedExplosionOneShotAudioSources`
            // so UnpauseExplosionOneShotAudio's existing iteration resumes them via UnPause().
            paused += PauseFxMongerExplosionAudioSources();

            return paused;
        }

        private static int PauseFxMongerExplosionAudioSources()
        {
            List<FXObject> objects = GhostVisualBuilder.ResolveFxMongerExplosionObjects();
            if (objects == null)
                return 0;

            int paused = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                FXObject fxObj = objects[i];
                GameObject effectObj = fxObj?.effectObj;
                if (effectObj == null) continue;

                AudioSource[] sources = effectObj.GetComponentsInChildren<AudioSource>(includeInactive: false);
                for (int s = 0; s < sources.Length; s++)
                {
                    AudioSource source = sources[s];
                    if (source == null || !source.isPlaying) continue;
                    source.Pause();
                    if (!ContainsAudioSourceReference(pausedExplosionOneShotAudioSources, source))
                        pausedExplosionOneShotAudioSources.Add(source);
                    paused++;
                }
            }
            return paused;
        }

        internal static int UnpauseExplosionOneShotAudio()
        {
            PruneExplosionOneShotAudioSources();
            int unpaused = 0;
            for (int i = 0; i < pausedExplosionOneShotAudioSources.Count; i++)
            {
                AudioSource source = pausedExplosionOneShotAudioSources[i];
                if (source != null)
                {
                    source.UnPause();
                    unpaused++;
                }
            }

            pausedExplosionOneShotAudioSources.Clear();
            return unpaused;
        }

        private static void TrackExplosionOneShotAudioSource(AudioSource source)
        {
            if (source == null)
                return;

            PruneExplosionOneShotAudioSources();
            activeExplosionOneShotAudioSources.Add(source);
        }

        private static void PruneExplosionOneShotAudioSources()
        {
            PruneMissingAudioSources(activeExplosionOneShotAudioSources);
            PruneMissingAudioSources(pausedExplosionOneShotAudioSources);
        }

        private static void PruneMissingAudioSources(List<AudioSource> sources)
        {
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                if (sources[i] == null)
                    sources.RemoveAt(i);
            }
        }

        private static bool ContainsAudioSourceReference(List<AudioSource> sources, AudioSource source)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                if (object.ReferenceEquals(sources[i], source))
                    return true;
            }

            return false;
        }

        internal static float NormalizeOneShotDurationSeconds(float clipLengthSeconds)
        {
            if (float.IsNaN(clipLengthSeconds) || float.IsInfinity(clipLengthSeconds) || clipLengthSeconds <= 0f)
                return GhostAudioPresets.ExplosionOneShotFallbackDurationSeconds;
            return clipLengthSeconds;
        }

        /// <summary>
        /// Mute all ghost audio sources (during high warp or ghost hidden).
        /// </summary>
        internal static void MuteAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (state.audioMuted) return;
            state.audioMuted = true;

            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null && info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "muted");
                }
            }
        }

        /// <summary>
        /// Unmute ghost audio. Active engines will resume on next throttle event.
        /// </summary>
        internal static void UnmuteAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (!state.audioMuted) return;
            state.audioMuted = false;
            // Audio restores naturally via next ApplyPartEvents cycle.
        }

        /// <summary>
        /// Pause all ghost audio sources for this state, preserving playback position.
        /// Used by the game pause handler so ESC menu mutes ghost audio.
        /// Unlike MuteAllAudio which calls Stop() (resetting position), this calls
        /// Pause() so UnPauseAllAudio can resume exactly where it left off.
        /// </summary>
        internal static void PauseAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            state.audioPaused = true;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource == null)
                        continue;

                    if (info.currentPower <= 0f)
                        StopLoopedGhostAudio(info, "paused-power=0", force: true);
                    else if (info.audioSource.isPlaying)
                        info.audioSource.Pause();
                }
            }
        }

        /// <summary>
        /// Resume all ghost audio sources paused by PauseAllAudio.
        /// </summary>
        internal static void UnpauseAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            state.audioPaused = false;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource == null)
                        continue;

                    if (info.currentPower <= 0f)
                        StopLoopedGhostAudio(info, "power=0", force: true);
                    else
                        info.audioSource.UnPause();
                }
            }
        }

        /// <summary>
        /// Compute the ghost audio volume for a given power level and atmosphere state.
        /// Centralizes the volume formula so SetEngineAudio, the KSC independent explosion
        /// one-shot path, and UpdateAudioAtmosphere all use the same calculation.
        /// </summary>
        internal static float ComputeGhostAudioVolume(float curveValue, float atmosphereFactor)
        {
            float settingsVolume = ParsekSettings.Current?.ghostAudioVolume ?? 1.0f;
            return curveValue * settingsVolume * GameSettings.SHIP_VOLUME * atmosphereFactor;
        }

        /// <summary>
        /// Compute atmosphere attenuation factor for ghost audio.
        /// Returns 0 in vacuum (no atmosphere or above atmosphere depth), 1 at sea level,
        /// with smooth quadratic falloff at high altitude.
        /// Uses cached CelestialBody on state to avoid per-frame linear search.
        /// </summary>
        internal static float ComputeAtmosphereFactor(GhostPlaybackState state)
        {
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName)) return 0f;

            // Cache the CelestialBody lookup — body only changes on SOI transitions.
            CelestialBody body = state.cachedAudioBody;
            if (body == null || state.cachedAudioBodyName != bodyName)
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                state.cachedAudioBody = body;
                state.cachedAudioBodyName = bodyName;
            }

            if (body == null || !body.atmosphere) return 0f;
            if (altitude >= body.atmosphereDepth) return 0f;
            if (altitude <= 0) return 1f;

            // Quadratic falloff: factor = (1 - alt/depth)^2
            // Sea level: 1.0. Half depth: 0.25. Edge: 0.0.
            float ratio = (float)(altitude / body.atmosphereDepth);
            float factor = (1f - ratio) * (1f - ratio);
            return factor;
        }

        /// <summary>
        /// Per-frame update of atmosphere factor and volume adjustment for all playing audio sources.
        /// Ensures smooth fade as ghost ascends/descends through atmosphere.
        /// </summary>
        internal static void UpdateAudioAtmosphere(GhostPlaybackState state)
        {
            if (state == null || state.audioInfos == null || state.audioMuted || state.audioPaused) return;

            float newFactor = ComputeAtmosphereFactor(state);

            // Log transitions (vacuum / atmosphere)
            bool wasInVacuum = state.atmosphereFactor < 0.001f;
            bool nowInVacuum = newFactor < 0.001f;
            if (wasInVacuum != nowInVacuum)
            {
                ParsekLog.VerboseRateLimited("GhostAudio", $"atm-transition-{state.vesselName}",
                    nowInVacuum
                        ? $"Ghost '{state.vesselName}' entered vacuum — audio silent"
                        : $"Ghost '{state.vesselName}' entered atmosphere — audio enabled (factor={newFactor:F3})",
                    2.0);
            }

            state.atmosphereFactor = newFactor;

            EnforceLoopedAudioPlaybackCap(state);
        }

        private static void AppendIfNotNull(List<AudioGhostInfo> result, AudioGhostInfo info)
        {
            if (info != null)
                result.Add(info);
        }

        private static bool ShouldLoopedAudioCandidatePrecede(
            AudioGhostInfo candidate, int candidateOrder, AudioGhostInfo current, int currentOrder)
        {
            if (candidate == null)
                return false;
            if (current == null)
                return true;

            int priorityCompare = GhostAudioPresets.GetBasePriority(candidate.priorityClass)
                .CompareTo(GhostAudioPresets.GetBasePriority(current.priorityClass));
            if (priorityCompare != 0)
                return priorityCompare < 0;

            int powerCompare = candidate.currentPower.CompareTo(current.currentPower);
            if (powerCompare != 0)
                return powerCompare > 0;

            return candidateOrder < currentOrder;
        }

        private static void InsertLoopedAudioSelectionCandidate(
            AudioGhostInfo candidate,
            int candidateOrder,
            int maxSources,
            ref AudioGhostInfo first,
            ref int firstOrder,
            ref AudioGhostInfo second,
            ref int secondOrder,
            ref AudioGhostInfo third,
            ref int thirdOrder,
            ref AudioGhostInfo fourth,
            ref int fourthOrder)
        {
            if (candidate == null || maxSources <= 0)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, first, firstOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                if (maxSources > 2)
                {
                    third = second;
                    thirdOrder = secondOrder;
                }
                if (maxSources > 1)
                {
                    second = first;
                    secondOrder = firstOrder;
                }
                first = candidate;
                firstOrder = candidateOrder;
                return;
            }

            if (maxSources <= 1)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, second, secondOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                if (maxSources > 2)
                {
                    third = second;
                    thirdOrder = secondOrder;
                }
                second = candidate;
                secondOrder = candidateOrder;
                return;
            }

            if (maxSources <= 2)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, third, thirdOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                third = candidate;
                thirdOrder = candidateOrder;
                return;
            }

            if (maxSources <= 3)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, fourth, fourthOrder))
            {
                fourth = candidate;
                fourthOrder = candidateOrder;
            }
        }

        private static bool IsLoopedAudioSelectedForPlayback(
            AudioGhostInfo info,
            AudioGhostInfo first,
            AudioGhostInfo second,
            AudioGhostInfo third,
            AudioGhostInfo fourth)
        {
            return ReferenceEquals(info, first)
                || ReferenceEquals(info, second)
                || ReferenceEquals(info, third)
                || ReferenceEquals(info, fourth);
        }

        private static void StartLoopedGhostAudio(AudioGhostInfo info, float volume, float pitch)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;

            info.audioSource.Play();
            ParsekLog.VerboseRateLimited("GhostAudio",
                $"audio-start-{info.partPersistentId}-{info.moduleIndex}",
                $"Engine audio started: pid={info.partPersistentId} midx={info.moduleIndex} " +
                $"power={info.currentPower:F2} vol={volume:F2} pitch={pitch:F2}",
                5.0);
        }

        private static void StopLoopedGhostAudio(AudioGhostInfo info, string reason)
        {
            StopLoopedGhostAudio(info, reason, force: false);
        }

        private static void StopLoopedGhostAudio(AudioGhostInfo info, string reason, bool force)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;
            bool wasPlaying = info.audioSource.isPlaying;
            if (!force && !wasPlaying)
                return;

            info.audioSource.Stop();
            if (!wasPlaying)
                return;

            ParsekLog.VerboseRateLimited("GhostAudio",
                $"audio-stop-{info.partPersistentId}-{info.moduleIndex}",
                $"Engine audio stopped: pid={info.partPersistentId} midx={info.moduleIndex} reason={reason}",
                5.0);
        }

        #endregion
    }
}
