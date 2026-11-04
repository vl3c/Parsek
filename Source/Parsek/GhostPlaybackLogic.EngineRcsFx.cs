using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Engine and RCS particle FX: emission, plume magnitude scaling and deferred power restores.
    internal static partial class GhostPlaybackLogic
    {
        #region Engine FX

        internal static string BuildEngineFxEmissionDiagnostic(
            string partName,
            uint partPersistentId,
            int moduleIndex,
            float power,
            string particleName,
            string parentName,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 worldPosition,
            Vector3 worldForward,
            Vector3 worldUp,
            float emissionRate,
            float startSpeed,
            bool isPlaying)
        {
            string safePartName = string.IsNullOrEmpty(partName) ? "<unknown>" : partName;
            string safeParticleName = string.IsNullOrEmpty(particleName) ? "<unknown>" : particleName;
            string safeParentName = string.IsNullOrEmpty(parentName) ? "<none>" : parentName;
            string localRotationRaw =
                $"({localRotation.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.z.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.w.ToString("F4", CultureInfo.InvariantCulture)})";

            return $"Engine FX emission diag: part='{safePartName}' pid={partPersistentId} midx={moduleIndex} " +
                $"power={power.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"ps='{safeParticleName}' parent='{safeParentName}' " +
                $"localPos={FormatVector3Invariant(localPosition)} localRot={localRotationRaw} " +
                $"worldPos={FormatVector3Invariant(worldPosition)} " +
                $"worldFwd={FormatVector3Invariant(worldForward)} " +
                $"worldUp={FormatVector3Invariant(worldUp)} " +
                $"rate={emissionRate.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"speed={startSpeed.ToString("F2", CultureInfo.InvariantCulture)} playing={isPlaying}";
        }

        internal static string FormatVector3Invariant(Vector3 value)
        {
            return $"({value.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.z.ToString("F4", CultureInfo.InvariantCulture)})";
        }

        internal static void SetEngineEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            SetEngineEmissionWithOutcome(state, evt, power);
        }

        /// <summary>
        /// P8 step 1: the real body, reporting WHICH early-return it took so
        /// <see cref="ApplyPartEvents"/> can name the skip class. The historical void
        /// signature above is a thin wrapper, so no existing caller changed and there
        /// is no second copy of these guards to drift out of step.
        ///
        /// "Applied" here means the ghost's engine info for this (pid, moduleIndex)
        /// took the new power. Whether any PARTICLE moved is a separate, already-logged
        /// fact (`FX magnitude (engine) pid=...`) and is deliberately not folded in:
        /// that line is suppressed when nothing scaled, so a reader who wants emitter
        /// proof reads it, and a reader who wants "the event reached a live engine
        /// info" reads this one.
        /// </summary>
        /// <summary>
        /// P8 step 1: the applier's guards, as a PURE function. Two reasons the split is
        /// this shape rather than a bool inside the handler:
        ///
        /// 1. It is the SAME code path - the handler's early return IS this call, so
        ///    there is no second copy of a predicate to drift (the standing house rule
        ///    about re-deriving scope rather than duplicating it).
        /// 2. It is the only shape that is testable at all. A method whose BODY names a
        ///    Unity ECall (a Transform / GameObject / Light write) cannot even be JIT'd
        ///    under xUnit - it throws `SecurityException: ECall methods must be packaged
        ///    into a system module` before the first guard runs - so a guard living
        ///    inside such a body is unreachable from a headless test. A classifier that
        ///    only reads managed dictionaries JITs and runs.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyEngineEmissionApply(
            GhostPlaybackState state, PartEvent evt)
        {
            if (state.engineInfos == null) return GhostPartEventOutcome.NoFamilyState;
            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            return state.engineInfos.ContainsKey(key)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoInfoForPart;
        }

        internal static GhostPartEventOutcome SetEngineEmissionWithOutcome(
            GhostPlaybackState state, PartEvent evt, float power)
        {
            GhostPartEventOutcome precondition = ClassifyEngineEmissionApply(state, evt);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            EngineGhostInfo info = state.engineInfos[key];

            // The ignition witness is decided on the power the info held BEFORE this event,
            // and costs nothing unless it is an edge and verbose logging is on.
            bool witnessIgnition = GhostEngineFxStartLog.IsIgnitionEdge(info.currentPower, power)
                && ParsekLog.IsVerboseEnabled;
            info.currentPower = power;

            // S1: MAGNITUDE. Scale the plume to the throttle before flipping the boolean gate.
            // Only on the way UP (power > 0): the fields are persistent, and the one caller that
            // does NOT route through here — RestoreAllRcsEmissions' sibling for RCS, and
            // RestoreActiveEngineFx here — relies on the last active scale still being written
            // when it re-enables emitters directly. Writing a zero-scale at power 0 would restore
            // an invisible plume.
            if (power > 0f)
            {
                ApplyFxMagnitudeScale(
                    info.kspEmitters, info.particleSystems, info.particleBaselines,
                    ComputeFxMagnitudeRatio(
                        ComputeEngineEmissionRate(info.emissionCurve, power),
                        ComputeEngineEmissionRate(info.emissionCurve, 1f), power),
                    ComputeFxMagnitudeRatio(
                        ComputeEngineSpeed(info.speedCurve, power),
                        ComputeEngineSpeed(info.speedCurve, 1f), power),
                    "engine", info.partPersistentId, info.moduleIndex, power);
            }

            // Control KSPParticleEmitter.emit via reflection — this is the ONLY particle
            // creation source. Unity's emission module is permanently disabled (bug #105).
            SetKspEmittersEnabled(info.kspEmitters, power > 0f);

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                if (power > 0f)
                {
                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();
                }
                else
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

            }

            if (witnessIgnition)
            {
                int playing = 0;
                int effectsPlaying = 0;
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    var ps = info.particleSystems[i];
                    if (ps == null || !ps.isPlaying) continue;
                    playing++;
                    if (i < info.effectsNodeSystemCount) effectsPlaying++;
                }
                GhostEngineFxStartLog.Emit(info, effectsPlaying, playing, power);
            }

            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// Stop all engine FX particle systems for a given part (by PID).
        /// Used defensively on decouple/destroy to ensure no orphaned engine glow.
        /// </summary>
        internal static void StopEngineFxForPart(GhostPlaybackState state, uint partPersistentId)
        {
            ClearTrackedEnginePowerForPart(state, partPersistentId);
            if (state?.engineInfos == null) return;
            foreach (var info in state.engineInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    var ps = info.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Stop all RCS FX particle systems for a given part (by PID).
        /// Used defensively on decouple/destroy to ensure no orphaned RCS glow.
        /// </summary>
        internal static void StopRcsFxForPart(GhostPlaybackState state, uint partPersistentId)
        {
            ClearTrackedRcsPowerForPart(state, partPersistentId);
            if (state?.rcsInfos == null) return;
            foreach (var info in state.rcsInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    var ps = info.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Stop and clear all engine FX particle systems across every engine info in the state.
        /// Used during ghost teardown to ensure no orphaned particle effects remain.
        /// </summary>
        internal static void StopAllEngineFx(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return;
            foreach (var kv in state.engineInfos)
            {
                SetKspEmittersEnabled(kv.Value.kspEmitters, false);
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Re-applies engine FX (plume / smoke emitters + particle systems) for every engine
        /// at its last recorded throttle (<see cref="EngineGhostInfo.currentPower"/>) after a
        /// distance / warp FX suppression is lifted. The symmetric partner to
        /// <see cref="StopAllEngineFx"/>.
        ///
        /// Engine FX are event-driven (recorded EngineThrottle threshold crossings), so unlike
        /// RCS and audio they have no per-frame driver that turns them back on. Without this a
        /// ghost that crosses the FX-LOD range during a steady burn (canonical case: a looping
        /// aircraft that repeatedly flies past the anchor and back) and returns keeps a dead
        /// plume until its next recorded throttle change. Only engines with
        /// <c>currentPower &gt; 0</c> are restored, so shut-down engines stay dark.
        ///
        /// Must run after <see cref="ApplyPartEvents"/> has caught the throttle cursor up to the
        /// current UT (the <c>ApplyFrameVisuals</c> call order guarantees this), so a throttle-down
        /// that occurred while FX were suppressed has already reset <c>currentPower</c> and is not
        /// re-ignited here.
        /// </summary>
        internal static void RestoreActiveEngineFx(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return;

            int restored = 0;
            foreach (var restore in CollectDeferredEnginePowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetEngineEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
                restored++;
            }

            if (restored > 0)
                ParsekLog.Verbose("Visual",
                    $"RestoreActiveEngineFx: re-applied {restored} engine FX after FX suppression lifted");
        }

        /// <summary>
        /// Stop and clear all RCS FX particle systems across every RCS info in the state.
        /// Used during ghost teardown to ensure no orphaned particle effects remain.
        /// </summary>
        internal static void StopAllRcsFx(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            foreach (var kv in state.rcsInfos)
            {
                SetKspEmittersEnabled(kv.Value.kspEmitters, false);
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Detaches active particle systems from the ghost hierarchy so they can linger
        /// and fade out naturally after the ghost is destroyed (#107). Stops emission,
        /// unparents, and schedules delayed destruction.
        /// </summary>
        internal static void DetachAndLingerParticleSystems(
            List<ParticleSystem> particleSystems, List<KspEmitterRef> kspEmitters, float lingerSeconds = 8f)
        {
            if (kspEmitters != null)
                SetKspEmittersEnabled(kspEmitters, false);
            if (particleSystems == null) return;

            for (int i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;

                // Only detach if particles are alive (no point lingering an empty system)
                if (ps.particleCount == 0)
                {
                    UnityEngine.Object.Destroy(ps.gameObject);
                    continue;
                }

                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                ps.transform.SetParent(null, true);
                UnityEngine.Object.Destroy(ps.gameObject, lingerSeconds);
            }
            particleSystems.Clear();
        }

        internal static void StopAndClearParticleSystems(
            List<ParticleSystem> particleSystems, List<KspEmitterRef> kspEmitters)
        {
            if (kspEmitters != null)
                SetKspEmittersEnabled(kspEmitters, false);
            if (particleSystems == null) return;

            for (int i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(true);
                SetParticleRenderersEnabled(ps, false);
            }
            particleSystems.Clear();
        }

        internal static void SetParticleRenderersEnabled(ParticleSystem ps, bool enabled)
        {
            if (ps == null)
                return;

            ParticleSystemRenderer[] renderers = ps.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = enabled;
            }
        }

        /// <summary>
        /// Enable or disable KSPParticleEmitter.emit on all captured emitters via reflection.
        /// KSPParticleEmitter is the ONLY particle creation source on ghost FX objects —
        /// Unity's emission module is permanently disabled to prevent bubble artifacts (bug #105).
        /// </summary>
        private static void SetKspEmittersEnabled(List<KspEmitterRef> kspEmitters, bool enabled)
        {
            if (kspEmitters == null) return;
            for (int i = 0; i < kspEmitters.Count; i++)
            {
                var r = kspEmitters[i];
                if (r.emitter == null || r.emitField == null) continue;
                r.emitField.SetValue(r.emitter, enabled);
            }
        }

        #endregion

        #region RCS FX

        internal static void SetRcsEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            SetRcsEmissionWithOutcome(state, evt, power);
        }

        /// <summary>
        /// P8 step 1: the RCS mirror of <see cref="SetEngineEmissionWithOutcome"/>.
        /// Same wrapper shape, same meaning of "applied" (the ghost's RCS info for this
        /// engine key took the new power; emitter movement is the separate
        /// `FX magnitude (rcs) pid=...` line).
        /// </summary>
        /// <summary>P8 step 1: the RCS sibling of <see cref="ClassifyEngineEmissionApply"/>.</summary>
        internal static GhostPartEventOutcome ClassifyRcsEmissionApply(
            GhostPlaybackState state, PartEvent evt)
        {
            if (state.rcsInfos == null) return GhostPartEventOutcome.NoFamilyState;
            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            return state.rcsInfos.ContainsKey(key)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoInfoForPart;
        }

        internal static GhostPartEventOutcome SetRcsEmissionWithOutcome(
            GhostPlaybackState state, PartEvent evt, float power)
        {
            GhostPartEventOutcome precondition = ClassifyRcsEmissionApply(state, evt);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            RcsGhostInfo info = state.rcsInfos[key];

            info.currentPower = power;

            // S1: MAGNITUDE. Same choke point as the engine side, but the ratio runs through
            // ComputeScaledRcsEmissionRate / ComputeScaledRcsSpeed so the showcase visibility
            // FLOORS survive: at low power the floor lifts the numerator, which lifts the ratio,
            // which is exactly what "stays visible on a showcase rig" means once the write is
            // relative rather than absolute.
            if (power > 0f)
            {
                ApplyFxMagnitudeScale(
                    info.kspEmitters, info.particleSystems, info.particleBaselines,
                    ComputeFxMagnitudeRatio(
                        ComputeScaledRcsEmissionRate(info.emissionCurve, power, info.emissionScale),
                        ComputeScaledRcsEmissionRate(info.emissionCurve, 1f, info.emissionScale),
                        power),
                    ComputeFxMagnitudeRatio(
                        ComputeScaledRcsSpeed(info.speedCurve, power, info.speedScale),
                        ComputeScaledRcsSpeed(info.speedCurve, 1f, info.speedScale),
                        power),
                    "rcs", info.partPersistentId, info.moduleIndex, power);
            }

            // Control KSPParticleEmitter.emit via reflection — this is the ONLY particle
            // creation source. Unity's emission module is permanently disabled (bug #105).
            SetKspEmittersEnabled(info.kspEmitters, power > 0f);

            int configuredSystems = 0;
            int enabledRenderers = 0;
            int playingSystems = 0;
            float sampleSpeed = 0f;
            float sampleSize = 0f;
            float sampleLifetime = 0f;

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                configuredSystems++;
                if (power > 0f)
                {
                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();

                    if (sampleSpeed <= 0f)
                    {
                        var main = ps.main;
                        sampleSpeed = main.startSpeedMultiplier;
                        sampleSize = main.startSizeMultiplier;
                        sampleLifetime = main.startLifetimeMultiplier;
                    }
                }
                else
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

                if (ps.isPlaying) playingSystems++;
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.enabled) enabledRenderers++;
            }

            return GhostPartEventOutcome.Applied;
        }

        #region S1 — plume magnitude (ratio of captured baseline)

        /// <summary>
        /// The lowest fraction of the full-power baseline a LIT plume is allowed to shrink to.
        /// A plume at 5% throttle must still read as "this engine is running" at playback speed —
        /// the binary on/off it replaces was at least legible. Also the floor that keeps a curve
        /// with a near-zero low end from producing a plume made of nothing.
        /// </summary>
        internal const float FxMagnitudeMinVisibleRatio = 0.2f;

        /// <summary>
        /// The engine siblings of <see cref="ComputeScaledRcsEmissionRate"/> /
        /// <see cref="ComputeScaledRcsSpeed"/>: same curve-else-linear fallback, no showcase scale
        /// and therefore no floor (engine FX carry no per-asset scale field). Kept as named
        /// functions rather than inlined curve reads so the RATIO's numerator and denominator are
        /// provably the same expression evaluated at two powers.
        /// </summary>
        internal static float ComputeEngineEmissionRate(FloatCurve emissionCurve, float power)
        {
            if (power <= 0f) return 0f;
            return emissionCurve != null ? emissionCurve.Evaluate(power) : power * 100f;
        }

        internal static float ComputeEngineSpeed(FloatCurve speedCurve, float power)
        {
            if (power <= 0f) return 0f;
            return speedCurve != null ? speedCurve.Evaluate(power) : power * 10f;
        }

        /// <summary>
        /// Turns a magnitude at the current power and the same magnitude at FULL power into the
        /// fraction of the captured build-time baseline to write. Pure floats in, pure float out —
        /// no Unity call, no FloatCurve — so the whole decision is headless-testable and the
        /// callers own the (KSP-side) curve evaluation.
        ///
        /// Degradation is deliberate and one-directional: anything that makes the ratio
        /// unknowable (no usable full-power reference, a non-finite reading, a negative magnitude)
        /// answers 1.0, i.e. "write the baseline back unchanged", which is byte-for-byte the
        /// pre-S1 boolean behaviour. It never answers 0 for a lit engine.
        /// </summary>
        internal static float ComputeFxMagnitudeRatio(
            float magnitudeAtPower, float magnitudeAtFullPower, float power)
        {
            if (power <= 0f) return 0f;
            if (power >= 1f) return 1f;

            if (float.IsNaN(magnitudeAtPower) || float.IsInfinity(magnitudeAtPower)
                || float.IsNaN(magnitudeAtFullPower) || float.IsInfinity(magnitudeAtFullPower)
                || magnitudeAtFullPower <= 0f || magnitudeAtPower < 0f)
            {
                return 1f;
            }

            float ratio = magnitudeAtPower / magnitudeAtFullPower;
            if (ratio >= 1f) return 1f;
            return Math.Max(ratio, FxMagnitudeMinVisibleRatio);
        }

        internal static void ResetFxMagnitudeWriteFailureCountForTesting()
            => FxMagnitudeWriteFailureCount = 0;

        /// <summary>
        /// Boxes a scaled magnitude as the EXACT type of the field it is about to be written to.
        ///
        /// THE 2026-08-11 DEFECT. <c>KSPParticleEmitter.minEmission</c> and <c>maxEmission</c> are
        /// declared <c>int</c> (verified by decompiling Assembly-CSharp: <c>minSize</c>/<c>maxSize</c>
        /// are float, <c>localVelocity</c> is Vector3, but the two EMISSION-RATE fields are int).
        /// <c>FieldInfo.SetValue</c> does no numeric conversion — it demands an instance of the
        /// field's own type — so handing it a boxed <c>float</c> threw
        /// "Object of type 'System.Single' cannot be converted to type 'System.Int32'" on every
        /// emitter of every engine and thruster, making the whole of S1 a silent no-op in game.
        ///
        /// Rounding is nearest-with-a-NONZERO-FLOOR rather than a truncation, and that floor is the
        /// contract, not a nicety: <see cref="ComputeFxMagnitudeRatio"/> guarantees it never answers
        /// zero for a lit engine, and truncating 0.4 particles/s to 0 would break that guarantee at
        /// the quantisation boundary for exactly the low-rate dense assets (ReStock SRB smoke) the
        /// ratio was tuned around. The floor keeps the SIGN, because stock reads a negative
        /// <c>maxEmission</c> as "this emitter does not emit" (<c>KSPParticleEmitter.Update</c>
        /// early-returns on it) and flipping that sentinel positive would light a dead emitter.
        ///
        /// Returns false for a type this cannot express (and for a non-finite value), which the
        /// callers degrade into "leave the field at its baseline" — the pre-S1 boolean plume.
        /// </summary>
        internal static bool TryConvertMagnitudeForField(
            Type fieldType, float value, out object converted)
        {
            converted = null;
            if (fieldType == null || float.IsNaN(value) || float.IsInfinity(value))
                return false;

            if (fieldType == typeof(float)) { converted = value; return true; }
            if (fieldType == typeof(double)) { converted = (double)value; return true; }

            if (fieldType == typeof(int) || fieldType == typeof(uint)
                || fieldType == typeof(long) || fieldType == typeof(ulong)
                || fieldType == typeof(short) || fieldType == typeof(ushort)
                || fieldType == typeof(byte) || fieldType == typeof(sbyte))
            {
                double rounded = Math.Round((double)value, MidpointRounding.AwayFromZero);
                if (rounded == 0.0 && value > 0f) rounded = 1.0;
                else if (rounded == 0.0 && value < 0f) rounded = -1.0;

                try
                {
                    converted = Convert.ChangeType(rounded, fieldType, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    // Out of the target type's range, or a negative into an unsigned field. Both
                    // degrade to "no scaling" rather than to a wrapped-around magnitude.
                    converted = null;
                    return false;
                }
                return true;
            }

            return false;
        }

        /// <summary>True when <see cref="TryConvertMagnitudeForField"/> can express a scaled scalar
        /// magnitude as <paramref name="fieldType"/>. Used at CAPTURE time so an unwritable field is
        /// dropped before it ever reaches the applier.</summary>
        internal static bool IsSupportedMagnitudeScalarFieldType(Type fieldType)
            => TryConvertMagnitudeForField(fieldType, 1f, out _);

        /// <summary>The vector magnitude surface is exactly one type; anything else is a KSP version
        /// having changed the field out from under us and degrades to "no scaling".</summary>
        internal static bool IsSupportedMagnitudeVectorFieldType(Type fieldType)
            => fieldType == typeof(Vector3);

        private static bool TryWriteMagnitudeScalarField(
            System.Reflection.FieldInfo field, object target, float value, ref string failure)
        {
            if (field == null) return false;
            if (!TryConvertMagnitudeForField(field.FieldType, value, out object boxed))
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) cannot hold " +
                    value.ToString("R", CultureInfo.InvariantCulture));
                return false;
            }
            try
            {
                field.SetValue(target, boxed);
                return true;
            }
            catch (Exception ex)
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) write threw: {ex.Message}");
                return false;
            }
        }

        private static bool TryWriteMagnitudeVectorField(
            System.Reflection.FieldInfo field, object target, Vector3 value, ref string failure)
        {
            if (field == null) return false;
            if (!IsSupportedMagnitudeVectorFieldType(field.FieldType))
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) is not a Vector3");
                return false;
            }
            try
            {
                field.SetValue(target, value);
                return true;
            }
            catch (Exception ex)
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) write threw: {ex.Message}");
                return false;
            }
        }

        private static string AppendFailure(string existing, string addition)
            => string.IsNullOrEmpty(existing) ? addition : existing + "; " + addition;

        /// <summary>
        /// The emitter <c>localVelocity</c> to write for one throttle ratio: <c>baseline * ratio</c>,
        /// except that a WORLD-SPACE emitter never drops below the minimum-flow floor its baseline
        /// was given at build time.
        ///
        /// Why the special case. <c>GhostVisualBuilder.ApplyWorldSpaceEmitterVelocityFloor</c> lifts a
        /// near-static world-space emitter to <c>WorldSpaceEmitterFloorSpeed</c> (6 m/s) precisely
        /// because such an asset paints its trail by MOTION, and a ghost that is standing still would
        /// otherwise pool every particle at the nozzle. <c>CaptureFxMagnitudeBaselines</c> captures
        /// AFTER that lift, so the floored 6 m/s IS the baseline — and a plain ratio write at, say,
        /// 0.2 throttle would put 1.2 m/s back on the emitter, under the 4 m/s threshold the floor
        /// exists to clear, re-creating the pooling at partial throttle. Narrow but real: ReStock's
        /// world-space SRB smoke rigs at genuine partial throttle.
        ///
        /// The clamp is a floor, never a boost: the result is capped at the baseline's own magnitude,
        /// so an emitter whose real velocity was already above the floor keeps its exact ratio down to
        /// the floor and no further. Local-space emitters (every stock FX asset) are untouched, and a
        /// zero / negative ratio is left alone — the callers only scale a LIT engine, and the one
        /// place a zero would arrive is a shutdown, where the emitters are being gated off anyway.
        /// </summary>
        internal static Vector3 ScaleEmitterLocalVelocity(
            Vector3 baselineLocalVelocity, float speedRatio, bool useWorldSpace)
        {
            Vector3 scaled = baselineLocalVelocity * speedRatio;
            if (!useWorldSpace) return scaled;
            if (float.IsNaN(speedRatio) || float.IsInfinity(speedRatio) || speedRatio <= 0f)
                return scaled;

            float baselineMagnitude = baselineLocalVelocity.magnitude;
            if (baselineMagnitude <= 0.001f) return scaled;

            float floor = Math.Min(
                baselineMagnitude, GhostVisualBuilder.WorldSpaceEmitterFloorSpeed);
            if (scaled.magnitude >= floor) return scaled;

            return baselineLocalVelocity * (floor / baselineMagnitude);
        }

        /// <summary>
        /// Writes <c>baseline * ratio</c> into the two magnitude surfaces a ghost plume actually
        /// has: the <c>KSPParticleEmitter</c> clone's own rate / velocity fields (the ONLY particle
        /// creation source on a ghost — Unity's emission module is permanently off, bug #105), and
        /// the <c>ParticleSystem</c>'s speed / size multipliers.
        ///
        /// Ratio-of-baseline rather than absolute writes is what makes this composable: the #383
        /// size boost, the per-part FX tunings and the world-space velocity floor are all already
        /// baked into the captured baseline, so scaling multiplies them instead of overwriting
        /// them, and repeated events at the same power are idempotent instead of compounding.
        /// An uncaptured baseline (build failed, or a KSP version renamed a field) writes nothing.
        ///
        /// The one composition the ratio cannot express is the world-space minimum-flow floor, which
        /// is a THRESHOLD rather than a magnitude; <see cref="ScaleEmitterLocalVelocity"/> owns that
        /// exception and every other write here stays a plain ratio.
        ///
        /// EVERY write goes through <see cref="TryConvertMagnitudeForField"/>, because the emitter's
        /// fields are NOT all floats — see that method's contract.
        /// </summary>
        internal static void ApplyFxMagnitudeScale(
            List<KspEmitterRef> emitters, List<ParticleSystem> systems,
            List<GhostFxMagnitudeBaseline> baselines,
            float emissionRatio, float speedRatio,
            string kind, uint partPersistentId, int moduleIndex, float power)
        {
            int scaledEmitters = 0;
            int scaledSystems = 0;

            if (emitters != null)
            {
                for (int i = 0; i < emitters.Count; i++)
                {
                    KspEmitterRef r = emitters[i];
                    if (!r.magnitudeBaselineCaptured || r.emitter == null) continue;

                    // Each field is written INDEPENDENTLY. The pre-fix code wrote all three inside
                    // one try, so the first failure (minEmission, always) skipped the other two as
                    // collateral — the localVelocity write was never even attempted.
                    string failure = null;
                    bool wrote = TryWriteMagnitudeScalarField(
                        r.minEmissionField, r.emitter,
                        r.baselineMinEmission * emissionRatio, ref failure);
                    wrote |= TryWriteMagnitudeScalarField(
                        r.maxEmissionField, r.emitter,
                        r.baselineMaxEmission * emissionRatio, ref failure);
                    wrote |= TryWriteMagnitudeVectorField(
                        r.localVelocityField, r.emitter,
                        ScaleEmitterLocalVelocity(
                            r.baselineLocalVelocity, speedRatio, r.baselineUseWorldSpace),
                        ref failure);

                    if (wrote) scaledEmitters++;

                    if (failure != null)
                    {
                        // COUNTED as well as logged. The log line is rate-limited (a per-frame,
                        // per-emitter failure would otherwise drown the log), so the count is the
                        // only faithful measure of how wide the breakage is — and it is what the
                        // in-game plume cell quotes in its own failure message, so a red there is
                        // one read rather than a log hunt.
                        FxMagnitudeWriteFailureCount++;
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"fx-magnitude-write-fail-{kind}-{partPersistentId}-{moduleIndex}",
                            $"FX magnitude write failed ({kind} pid={partPersistentId} " +
                            $"midx={moduleIndex}): {failure}; plume stays at its baseline", 60.0);
                    }
                }
            }

            if (systems != null && baselines != null)
            {
                int count = Math.Min(systems.Count, baselines.Count);
                for (int i = 0; i < count; i++)
                {
                    ParticleSystem ps = systems[i];
                    GhostFxMagnitudeBaseline b = baselines[i];
                    if (ps == null || !b.captured) continue;
                    // Size rides the SPEED ratio on purpose - neither the engine nor the RCS path
                    // carries a separate size curve, and a throttled-down plume is shorter and
                    // thinner in the same proportion its exhaust slows. Do NOT "fix" this to the
                    // emission ratio: that one is a particle COUNT, and scaling size by it shrinks
                    // a deliberately low-rate dense asset (ReStock SRB smoke) to nothing.
                    var main = ps.main;
                    main.startSpeedMultiplier = b.startSpeedMultiplier * speedRatio;
                    main.startSizeMultiplier = b.startSizeMultiplier * speedRatio;
                    scaledSystems++;
                }
            }

            if (scaledEmitters == 0 && scaledSystems == 0)
                return;

            ParsekLog.VerboseRateLimited("GhostVisual",
                $"fx-magnitude-{kind}-{partPersistentId}-{moduleIndex}",
                $"FX magnitude ({kind}) pid={partPersistentId} midx={moduleIndex} " +
                $"power={power.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"emissionRatio={emissionRatio.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"speedRatio={speedRatio.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"emitters={scaledEmitters} systems={scaledSystems}", 10.0);
        }

        #endregion

        internal static float ComputeScaledRcsEmissionRate(
            FloatCurve emissionCurve, float power, float emissionScale)
        {
            if (power <= 0f) return 0f;

            float emRate = emissionCurve != null ? emissionCurve.Evaluate(power) : power * 100f;
            emRate *= emissionScale > 0f ? emissionScale : 1f;
            if (emissionScale > 1f)
                emRate = Math.Max(emRate, 60f);

            return emRate;
        }

        internal static float ComputeScaledRcsSpeed(
            FloatCurve speedCurve, float power, float speedScale)
        {
            if (power <= 0f) return 0f;

            float spd = speedCurve != null ? speedCurve.Evaluate(power) : power * 10f;
            spd *= speedScale > 0f ? speedScale : 1f;
            if (speedScale > 1f)
                spd = Math.Max(spd, 4f);

            return spd;
        }

        internal static void StopAllRcsEmissions(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            if (state.rcsSuppressed) return;
            state.rcsSuppressed = true;
            int suppressedCount = 0;
            foreach (var info in state.rcsInfos.Values)
            {
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int j = 0; j < info.particleSystems.Count; j++)
                {
                    var ps = info.particleSystems[j];
                    if (ps != null)
                    {
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(true);
                    }
                }
                suppressedCount++;
            }
        }

        internal static void RestoreAllRcsEmissions(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            if (!state.rcsSuppressed) return;
            state.rcsSuppressed = false;
            int restoredCount = 0;
            foreach (var info in state.rcsInfos.Values)
            {
                // Only restore emission for RCS that had active KSP emitters.
                // Check if any KSPParticleEmitter was playing before suppression
                // by looking at whether the particle system was playing (ps.isPlaying
                // stays true after Stop with StopEmittingAndClear until particles expire).
                // Since we call Clear(), isPlaying is false after suppress. Instead, check
                // if any renderers are enabled — SetRcsEmission enables renderers when active.
                bool wasActive = false;
                for (int j = 0; j < info.particleSystems.Count; j++)
                {
                    var ps = info.particleSystems[j];
                    if (ps == null) continue;
                    var renderer = ps.GetComponent<ParticleSystemRenderer>();
                    if (renderer != null && renderer.enabled)
                    {
                        wasActive = true;
                        break;
                    }
                }
                if (wasActive)
                {
                    SetKspEmittersEnabled(info.kspEmitters, true);
                    for (int j = 0; j < info.particleSystems.Count; j++)
                    {
                        var ps = info.particleSystems[j];
                        if (ps != null && !ps.isPlaying)
                            ps.Play();
                    }
                    restoredCount++;
                }
            }
            ParsekLog.Info("Flight", $"Restored RCS emissions for {restoredCount} modules");
        }

        internal static List<(ulong key, float power)> CollectDeferredEnginePowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.engineInfos == null)
                return restores;

            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedEnginePowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.engineInfos == null)
                return;

            foreach (var info in state.engineInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static List<(ulong key, float power)> CollectDeferredRcsPowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.rcsInfos == null)
                return restores;

            foreach (var kvp in state.rcsInfos)
            {
                RcsGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedRcsPowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.rcsInfos == null)
                return;

            foreach (var info in state.rcsInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static List<(ulong key, float power)> CollectDeferredAudioPowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.audioInfos == null || state.audioMuted || state.audioPaused)
                return restores;

            foreach (var kvp in state.audioInfos)
            {
                AudioGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedAudioPowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.audioInfos == null)
                return;

            foreach (var info in state.audioInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static void RestoreDeferredRuntimeFxState(GhostPlaybackState state)
        {
            if (state == null)
                return;

            foreach (var restore in CollectDeferredEnginePowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetEngineEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
            }

            foreach (var restore in CollectDeferredRcsPowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetRcsEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
            }

            var audioRestores = CollectDeferredAudioPowerRestores(state);
            for (int i = 0; i < audioRestores.Count; i++)
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(audioRestores[i].key, out partPersistentId, out moduleIndex);
                SetEngineAudio(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, audioRestores[i].power, enforcePlaybackCap: false);
            }
            if (audioRestores.Count > 0)
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
        }

        #endregion
    }
}
