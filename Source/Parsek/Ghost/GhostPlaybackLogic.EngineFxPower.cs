using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Engine particle sources sized by stock's own rules, and the thrust share those rules read.
    internal static partial class GhostPlaybackLogic
    {
        #region Engine FX power

        /// <summary>A change of the thrust share below this is not written to the particles.</summary>
        internal const float EngineThrustFractionStep = 1f / 64f;

        /// <summary>The mach change below which the cached air reading stands.</summary>
        internal const float EngineAirMachStep = 0.02f;

        private const double KpaToAtmospheres = 0.009869232667160128;

        /// <summary>
        /// Writes every stock-ruled particle source of a lit engine for its throttle
        /// (<see cref="EngineGhostInfo.currentPower"/>) and thrust share. Absolute values from the
        /// asset baselines, so a repeated call is idempotent. Returns the sources written.
        /// </summary>
        internal static int ApplyEngineFxRules(EngineGhostInfo info)
        {
            if (info?.fxRules == null || info.currentPower <= 0f) return 0;
            int models = 0, prefabs = 0, legacy = 0;
            for (int i = 0; i < info.fxRules.Count; i++)
            {
                GhostEngineFxRule rule = info.fxRules[i];
                if (rule == null) continue;
                switch (rule.kind)
                {
                    case GhostEngineFxRuleKind.ModelEmitter:
                        WriteModelEmitter(rule, GhostEngineFxPower.ChannelPower(
                            rule.channel, info.currentPower, info.thrustFraction, rule.spoolIdle));
                        models++;
                        break;
                    case GhostEngineFxRuleKind.PrefabParticle:
                        WritePrefabParticle(rule, GhostEngineFxPower.ChannelPower(
                            rule.channel, info.currentPower, info.thrustFraction, rule.spoolIdle));
                        prefabs++;
                        break;
                    case GhostEngineFxRuleKind.LegacyRunning:
                        WriteLegacy(rule, GhostEngineFxPower.LegacyRunningPower(
                            info.currentPower * info.thrustFraction));
                        legacy++;
                        break;
                    default:
                        WriteLegacy(rule, GhostEngineFxPower.LegacyPowerGroupPower(info.currentPower));
                        legacy++;
                        break;
                }
            }
            if (models + prefabs + legacy > 0)
                ParsekLog.VerboseRateLimited("GhostVisual",
                    $"engine-fx-power-{info.partPersistentId}-{info.moduleIndex}",
                    $"Engine FX power: part='{info.partName}' pid={info.partPersistentId} midx={info.moduleIndex} " +
                    $"throttle={info.currentPower.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"thrustFraction={info.thrustFraction.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"models={models} prefabs={prefabs} legacy={legacy}", 10.0);
            return models + prefabs + legacy;
        }

        /// <summary>
        /// Writes every stock-ruled emitter of a firing RCS module for its recorded power: stock's
        /// <c>ModelMultiParticleFX.OnEvent(power, nozzle)</c>, from the asset baselines. Returns the
        /// emitters written.
        /// </summary>
        internal static int ApplyRcsFxRules(RcsGhostInfo info)
        {
            if (info?.fxRules == null || info.fxRules.Count == 0 || !(info.currentPower > 0f)) return 0;
            float power = GhostEngineFxPower.RcsNozzlePower(
                info.currentPower, info.recordedNozzles, info.activeNozzles);
            float emission = 0f;
            for (int i = 0; i < info.fxRules.Count; i++)
            {
                GhostEngineFxRule rule = info.fxRules[i];
                emission = GhostEngineFxPower.RcsEmission(
                    rule.curves.emission.Value(power), power, info.activeNozzles);
                WriteModelEmitter(rule, emission, power);
            }
            ParsekLog.VerboseRateLimited("GhostVisual",
                $"rcs-fx-power-{info.partPersistentId}-{info.moduleIndex}",
                $"RCS FX power: pid={info.partPersistentId} midx={info.moduleIndex} " +
                $"recorded={info.currentPower.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"nozzles={info.activeNozzles}/{info.recordedNozzles} " +
                $"power={power.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"emission={emission.ToString("F3", CultureInfo.InvariantCulture)} models={info.fxRules.Count}", 10.0);
            return info.fxRules.Count;
        }

        // Particle ECalls; NoInlining keeps them out of ApplyEngineFxRules, which a headless test
        // may call with no rules.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteModelEmitter(GhostEngineFxRule rule, float power)
            => WriteModelEmitter(rule, rule.curves.emission.Value(power), power);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteModelEmitter(GhostEngineFxRule rule, float emission, float power)
        {
            KSPParticleEmitter emitter = rule.emitter;
            if (emitter == null) return;
            float energy = rule.curves.energy.Value(power);
            emitter.minEmission = GhostEngineFxPower.EmissionRate(rule.minEmission, emission);
            emitter.maxEmission = GhostEngineFxPower.EmissionRate(rule.maxEmission, emission);
            emitter.minEnergy = rule.minEnergy * energy;
            emitter.maxEnergy = rule.maxEnergy * energy;
            emitter.localVelocity = ScaleEmitterLocalVelocity(
                rule.localVelocity, rule.curves.speed.Value(power), rule.useWorldSpace);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WritePrefabParticle(GhostEngineFxRule rule, float power)
        {
            ParticleSystem ps = rule.system;
            if (ps == null) return;
            // Stock reads the node's speed curve too and drops the result (it scales copies of the
            // velocity curves and never writes them back), so speed does not change with power.
            float emission = rule.curves.emission.Value(power);
            ParticleSystem.EmissionModule module = ps.emission;
            if (rule.hasBursts)
            {
                ParticleSystem.Burst burst = module.GetBurst(0);
                burst.minCount = (short)GhostEngineFxPower.EmissionRate(rule.minRate, emission);
                burst.maxCount = (short)GhostEngineFxPower.EmissionRate(rule.maxRate, emission);
                module.SetBurst(0, burst);
            }
            else
            {
                module.rateOverTime = GhostEngineFxPower.EmissionRate(rule.minRate, emission);
            }
            float energy = rule.curves.energy.Value(power);
            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(rule.minLife * energy, rule.maxLife * energy);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteLegacy(GhostEngineFxRule rule, float power)
        {
            ParticleSystem ps = rule.system;
            if (ps == null) return;
            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(rule.minLife * power, rule.maxLife * power);
            main.startSize = new ParticleSystem.MinMaxCurve(rule.minSize * power, rule.maxSize * power);
        }

        /// <summary>True when a lit engine of the ghost has a source that reads the thrust share.</summary>
        internal static bool HasLitThrustDrivenFx(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return false;
            foreach (EngineGhostInfo info in state.engineInfos.Values)
                if (info != null && info.currentPower > 0f && info.usesThrustFraction) return true;
            return false;
        }

        /// <summary>Whether the cached air can stand for the ghost's current place and speed.</summary>
        internal static bool CanReuseEngineAir(
            string cachedBodyName, double cachedAltitude, float cachedMach,
            string bodyName, double altitude, float mach)
            => CanReuseSmokeTrailDensity(cachedBodyName, cachedAltitude, bodyName, altitude)
               && Math.Abs(mach - cachedMach) < EngineAirMachStep;

        /// <summary>Whether a new thrust share is far enough from the applied one to be written.</summary>
        internal static bool ShouldApplyThrustFraction(float applied, float fraction)
            => Math.Abs(fraction - applied) >= EngineThrustFractionStep;

        /// <summary>
        /// Stock sizes a <c>powerEffectName</c> group and a legacy running group by
        /// <c>finalThrust / maxThrust</c>; a recording holds the throttle. The share of full thrust
        /// the engine makes at the ghost's own pressure, density and mach is the module's own
        /// arithmetic over its prefab curves. Called once per visible, unsuppressed frame; returns
        /// at the first check for a ghost with no such engine lit, and evaluates no curve while the
        /// air and the throttle are unchanged. An unknown place leaves the share as it was (1 at
        /// build). A ghost whose ground speed is unknown keeps its last mach; before any is known,
        /// an engine whose thrust follows mach keeps its share, since a standstill is not a guess a
        /// fast jet can take.
        /// </summary>
        internal static void UpdateEngineThrustFractions(GhostPlaybackState state)
        {
            if (!HasLitThrustDrivenFx(state)) return;
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;
            if (string.IsNullOrEmpty(bodyName) || double.IsNaN(altitude)) return;
            bool speedKnown = state.lastInterpolatedSurfaceVelocitySource != GhostSurfaceVelocitySource.None;
            float speed = speedKnown ? state.lastInterpolatedSurfaceVelocity.magnitude : 0f;

            GhostEngineAir air = state.engineAir;
            float mach = ResolveEngineMach(speedKnown, speed, air.soundSpeed, air.mach);
            bool machKnown = speedKnown || air.machKnown;
            if (machKnown != air.machKnown
                || !CanReuseEngineAir(air.bodyName, air.altitude, air.mach, bodyName, altitude, mach))
            {
                float lastMach = air.mach;
                if (!TryReadEngineAir(bodyName, altitude, out air))
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"engine-air-nobody-{bodyName}",
                        $"Engine FX power: body '{bodyName}' not resolved; thrust share left as is", 30.0);
                    return;
                }
                air.mach = ResolveEngineMach(speedKnown, speed, air.soundSpeed, lastMach);
                air.machKnown = machKnown;
                state.engineAir = air;
                state.engineAirSerial++;
            }

            foreach (EngineGhostInfo info in state.engineInfos.Values)
            {
                if (info == null || info.currentPower <= 0f || !info.usesThrustFraction) continue;
                if (info.thrustAirSerial == state.engineAirSerial && info.thrustThrottle == info.currentPower)
                    continue;
                info.thrustAirSerial = state.engineAirSerial;
                info.thrustThrottle = info.currentPower;
                if (!air.machKnown && NeedsMach(info.engine)) continue;
                float fraction = ComputeEngineThrustFraction(info.engine, air, info.currentPower);
                if (!ShouldApplyThrustFraction(info.thrustFraction, fraction)) continue;
                info.thrustFraction = fraction;
                ApplyEngineFxRules(info);
            }
        }

        /// <summary>The mach to read the curves at: from a known ground speed, else the last one.
        /// 0 where there is no air.</summary>
        internal static float ResolveEngineMach(bool speedKnown, float speed, double soundSpeed, float lastMach)
        {
            if (!speedKnown) return lastMach;
            return soundSpeed > 0.0 ? (float)(speed / soundSpeed) : 0f;
        }

        internal static bool NeedsMach(ModuleEngines engine)
            => !ReferenceEquals(engine, null) && (engine.useVelCurve || engine.useVelCurveIsp);

        // FloatCurve / Unity-object reads of the prefab module.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float ComputeEngineThrustFraction(ModuleEngines engine, GhostEngineAir air, float throttle)
        {
            if (engine == null || engine.atmosphereCurve == null) return 1f;
            float density01 = (float)(air.density * GhostEngineFxPower.FlowPerDensity);
            return GhostEngineFxPower.ThrustFraction(new GhostEngineFxPower.ThrustInputs
            {
                ispHere = engine.atmosphereCurve.Evaluate((float)air.pressureAtm),
                ispVacuum = engine.atmosphereCurve.Evaluate(0f),
                throttlingIsp = engine.useThrottleIspCurve
                    ? engine.GetThrottlingMult((float)air.pressureAtm, throttle) : 1f,
                atmChangeFlow = engine.atmChangeFlow,
                airDensity = air.density,
                useAtmCurve = engine.useAtmCurve,
                atmCurve = engine.atmChangeFlow && engine.useAtmCurve
                    ? engine.atmCurve.Evaluate(density01) : 1f,
                velCurve = engine.useVelCurve ? engine.velCurve.Evaluate(air.mach) : 1f,
                atmCurveIsp = engine.useAtmCurveIsp ? engine.atmCurveIsp.Evaluate(density01) : 1f,
                velCurveIsp = engine.useVelCurveIsp ? engine.velCurveIsp.Evaluate(air.mach) : 1f,
                flowMultCap = engine.flowMultCap,
                flowMultCapSharpness = engine.flowMultCapSharpness,
                clamp = engine.CLAMP,
                multFlow = engine.multFlow,
                multIsp = engine.multIsp
            });
        }

        // CelestialBody / FlightGlobals reads (mono runs FlightGlobals' initializer at JIT time).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadEngineAir(string bodyName, double altitude, out GhostEngineAir air)
        {
            air = new GhostEngineAir { bodyName = bodyName, altitude = altitude };
            CelestialBody body = null;
            var bodies = FlightGlobals.Bodies;
            if (bodies != null)
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i] != null && bodies[i].name == bodyName) { body = bodies[i]; break; }
            if (body == null) return false;
            if (!IsInsideAtmosphere(body.atmosphere, body.atmosphereDepth, altitude)) return true;
            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure <= 0.0 || double.IsNaN(temperature) || temperature <= 0.0)
                return true;
            double density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0.0) return true;
            air.pressureAtm = pressure * KpaToAtmospheres;
            air.density = density;
            air.soundSpeed = body.GetSpeedOfSound(pressure, density);
            if (double.IsNaN(air.soundSpeed) || air.soundSpeed < 0.0) air.soundSpeed = 0.0;
            return true;
        }

        #endregion
    }
}
