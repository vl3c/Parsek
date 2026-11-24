using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Stock's <c>FXCurve</c> (decompiled, KSP 1.12.5) without its native <c>AnimationCurve</c>, so
    /// it evaluates headless. An EFFECTS particle node writes a curve as repeated VALUES
    /// (<c>emission = 0.05 0.0</c>), never as a sub-node: no value leaves the default 1, one value
    /// is a constant (its first token), two or more are keys. Stock builds each key with
    /// <c>new Keyframe(time, value)</c>, whose tangents are 0, so a segment is the smoothstep between
    /// its two values, and the ends are clamped.
    /// </summary>
    internal sealed class GhostFxCurve
    {
        private static readonly char[] Delimiters = { ' ', ',', ';', '\t' };

        private readonly float constant = 1f;
        private readonly float[] times;
        private readonly float[] values;

        private GhostFxCurve(float constant) { this.constant = constant; }

        private GhostFxCurve(float[] times, float[] values)
        {
            this.times = times;
            this.values = values;
        }

        internal static GhostFxCurve Read(ConfigNode node, string valueName)
        {
            string[] lines = node != null ? node.GetValues(valueName) : null;
            if (lines == null || lines.Length == 0) return new GhostFxCurve(1f);
            if (lines.Length == 1)
            {
                string[] single = lines[0].Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);
                return new GhostFxCurve(
                    single.Length > 0 && TryParse(single[0], out float value) ? value : 1f);
            }

            var keys = new List<KeyValuePair<float, float>>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] tokens = lines[i].Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2 || !TryParse(tokens[0], out float time) || !TryParse(tokens[1], out float value))
                    continue;
                // AnimationCurve.AddKey keeps the first key of a time and drops a later one.
                int at = 0;
                bool duplicate = false;
                while (at < keys.Count && keys[at].Key <= time)
                {
                    if (keys[at].Key == time) { duplicate = true; break; }
                    at++;
                }
                if (!duplicate) keys.Insert(at, new KeyValuePair<float, float>(time, value));
            }
            var times = new float[keys.Count];
            var values = new float[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                times[i] = keys[i].Key;
                values[i] = keys[i].Value;
            }
            return new GhostFxCurve(times, values);
        }

        private static bool TryParse(string token, out float value)
            => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && !float.IsNaN(value) && !float.IsInfinity(value);

        internal float Value(float time)
        {
            if (times == null) return constant;
            int count = times.Length;
            if (count == 0) return 0f;
            if (!(time > times[0])) return values[0];
            if (time >= times[count - 1]) return values[count - 1];
            int i = 0;
            while (time >= times[i + 1]) i++;
            float u = (time - times[i]) / (times[i + 1] - times[i]);
            float s = u * u * (3f - 2f * u);
            return values[i] + (values[i + 1] - values[i]) * s;
        }
    }

    /// <summary>The three curves of one EFFECTS particle node.</summary>
    internal sealed class GhostEngineFxCurves
    {
        internal readonly GhostFxCurve emission;
        internal readonly GhostFxCurve energy;
        internal readonly GhostFxCurve speed;

        private GhostEngineFxCurves(ConfigNode node)
        {
            emission = GhostFxCurve.Read(node, "emission");
            energy = GhostFxCurve.Read(node, "energy");
            speed = GhostFxCurve.Read(node, "speed");
        }

        /// <summary>Null for a node type stock does not have (SmokeScreen's
        /// <c>MODEL_MULTI_PARTICLE_PERSIST</c>, whose curves are sub-nodes).</summary>
        internal static GhostEngineFxCurves ForNode(string nodeType, ConfigNode node)
        {
            bool stockNode = nodeType == "MODEL_MULTI_PARTICLE" || nodeType == "MODEL_PARTICLE"
                || nodeType == "PREFAB_PARTICLE";
            return stockNode && node != null ? new GhostEngineFxCurves(node) : null;
        }
    }

    /// <summary>What <c>ModuleEnginesFX.FXUpdate</c> feeds an EFFECTS group with.</summary>
    internal enum GhostEngineFxChannel
    {
        /// <summary><c>currentThrottle</c> (<c>runningEffectName</c>), also the stand-in when the
        /// group's role is unknown.</summary>
        Throttle,
        /// <summary><c>engineSpool</c> (<c>spoolEffectName</c>), which settles at the throttle or the
        /// module's <c>engineSpoolIdle</c>, whichever is larger.</summary>
        Spool,
        /// <summary><c>finalThrust / maxThrust</c> (<c>powerEffectName</c>).</summary>
        Thrust,
    }

    internal enum GhostEngineFxRuleKind
    {
        /// <summary>Stock <c>ModelMultiParticleFX</c> / <c>ModelParticleFX</c>.</summary>
        ModelEmitter,
        /// <summary>Stock <c>PrefabParticleFX</c>.</summary>
        PrefabParticle,
        /// <summary>A legacy <c>fx_*</c> key: <c>FXGroup.SetPower</c> of the running group.</summary>
        LegacyRunning,
        /// <summary>As above, of the power group.</summary>
        LegacyPower,
    }

    /// <summary>
    /// One ghost engine particle source and the stock rule that sizes it by engine power, with the
    /// asset values stock captures when it initializes the effect. Written only by
    /// <see cref="GhostPlaybackLogic.ApplyEngineFxRules"/>, so it needs no boost and no ratio.
    /// </summary>
    internal sealed class GhostEngineFxRule
    {
        internal GhostEngineFxRuleKind kind;
        internal GhostEngineFxChannel channel;
        internal float spoolIdle;
        internal GhostEngineFxCurves curves;

        // ModelEmitter
        internal KSPParticleEmitter emitter;
        internal int minEmission;
        internal int maxEmission;
        internal float minEnergy;
        internal float maxEnergy;
        internal Vector3 localVelocity;
        internal bool useWorldSpace;

        // PrefabParticle and Legacy
        internal ParticleSystem system;
        internal bool hasBursts;
        internal float minRate;
        internal float maxRate;
        internal float minLife;
        internal float maxLife;
        internal float minSize;
        internal float maxSize;

        internal bool UsesThrust
            => kind == GhostEngineFxRuleKind.LegacyRunning || channel == GhostEngineFxChannel.Thrust;
    }

    /// <summary>The air an engine breathes and pushes against, read once per place and speed.</summary>
    internal struct GhostEngineAir
    {
        internal string bodyName;
        internal double altitude;
        internal double pressureAtm;
        internal double density;
        internal double soundSpeed;
        internal float mach;
        /// <summary>False until the ghost's ground speed has been known once.</summary>
        internal bool machKnown;
    }

    /// <summary>The stock arithmetic, as pure functions.</summary>
    internal static class GhostEngineFxPower
    {
        /// <summary>
        /// The channel of an EFFECTS group. <c>FXUpdate</c> calls directThrottle, spool, running,
        /// power in that order, so a group two fields name ends on the later one. directThrottle's
        /// <c>requestedThrottle</c> is the throttle once the engine has followed it.
        /// </summary>
        internal static GhostEngineFxChannel ResolveChannel(
            string groupName, string powerEffectName, string runningEffectName, string spoolEffectName)
        {
            if (!string.IsNullOrEmpty(groupName))
            {
                if (Same(groupName, powerEffectName)) return GhostEngineFxChannel.Thrust;
                if (Same(groupName, runningEffectName)) return GhostEngineFxChannel.Throttle;
                if (Same(groupName, spoolEffectName)) return GhostEngineFxChannel.Spool;
            }
            return GhostEngineFxChannel.Throttle;
        }

        private static bool Same(string a, string b)
            => !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        internal static float ChannelPower(
            GhostEngineFxChannel channel, float throttle, float thrustFraction, float spoolIdle)
        {
            switch (channel)
            {
                case GhostEngineFxChannel.Thrust: return throttle * thrustFraction;
                case GhostEngineFxChannel.Spool: return Math.Max(throttle, spoolIdle);
                default: return throttle;
            }
        }

        /// <summary><c>Mathf.FloorToInt(baseline * emission.Value(power))</c>; 0 draws nothing.</summary>
        internal static int EmissionRate(float baseline, float emission)
        {
            float rate = baseline * emission;
            if (float.IsNaN(rate) || float.IsInfinity(rate)) return 0;
            return (int)Math.Floor(rate);
        }

        /// <summary>
        /// The power of an RCS module's active nozzles. The recorded one is the mean force share over
        /// every thruster transform, those of the other part variants included (RCSBlock.v2: 4 of 9
        /// can fire). Stock's floor of 0.1 (<c>ModuleRCS.FixedUpdate</c>) applies.
        /// </summary>
        internal static float RcsNozzlePower(float recordedPower, int recordedNozzles, int activeNozzles)
        {
            if (!(recordedPower > 0f)) return 0f;
            float power = activeNozzles > 0 && activeNozzles < recordedNozzles
                ? recordedPower * recordedNozzles / activeNozzles
                : recordedPower;
            return Math.Min(Math.Max(power, 0.1f), 1f);
        }

        /// <summary>
        /// The share of its baseline emission an RCS nozzle draws. One nozzle: stock's curve, the
        /// power is its own. Of several, the recording holds the mean: one nozzle of four at full
        /// reads 0.25, where stock's curve (0 up to 0.1, then a smoothstep to 1) gives 0.07 and the
        /// block would all but stop puffing. The mean itself keeps the total of nozzles that fire
        /// hard; the curve is above it from 0.55, where most of them must be firing.
        /// </summary>
        internal static float RcsEmission(float curveEmission, float nozzlePower, int activeNozzles)
            => activeNozzles > 1 ? Math.Max(curveEmission, nozzlePower) : curveEmission;

        /// <summary><c>ModuleEngines.FXUpdate</c>'s running-group power, which
        /// <c>FXGroup.SetPower</c> multiplies particle size and lifetime by.</summary>
        internal static float LegacyRunningPower(float thrustOverMax)
        {
            if (float.IsNaN(thrustOverMax)) return 0.5f;
            return Math.Max(Math.Min(thrustOverMax / 0.5f, 1.75f), 0.5f);
        }

        /// <summary>The power group's: <c>Mathf.Lerp(0.45f, 1.2f, currentThrottle)</c>.</summary>
        internal static float LegacyPowerGroupPower(float throttle)
        {
            float t = throttle > 0f ? Math.Min(throttle, 1f) : 0f;
            return 0.45f + (1.2f - 0.45f) * t;
        }

        /// <summary>
        /// <c>FXGroup.begin</c> keeps the larger constant of a curve and the distance between the
        /// two; <c>SetPower</c> writes both ends from them.
        /// </summary>
        internal static void LegacyRange(float constantMin, float constantMax, out float min, out float max)
        {
            max = Math.Max(constantMax, constantMin);
            min = max - Math.Abs(constantMax - constantMin);
        }

        /// <summary>The values of the module's curves at the ghost's air, for <see cref="ThrustFraction"/>.
        /// 1 for a curve the module does not use.</summary>
        internal struct ThrustInputs
        {
            internal float ispHere;
            internal float ispVacuum;
            internal float throttlingIsp;
            internal bool atmChangeFlow;
            internal double airDensity;
            internal bool useAtmCurve;
            internal float atmCurve;
            internal float velCurve;
            internal float atmCurveIsp;
            internal float velCurveIsp;
            internal float flowMultCap;
            internal float flowMultCapSharpness;
            internal float clamp;
            internal float multFlow;
            internal float multIsp;
        }

        /// <summary>Stock's density factor (1 / 1.225), as <c>ModifyFlow</c> multiplies it in.</summary>
        internal const double FlowPerDensity = 0.8163265147242933;

        /// <summary>
        /// <c>finalThrust / maxThrust</c> at full throttle: <c>ModuleEngines.RequiredPropellantMass</c>
        /// and <c>ModifyFlow</c> with <c>maxFuelFlow = maxThrust / (isp(0) * g)</c>. The thrust curve
        /// of a solid motor needs its propellant, which nothing records; it counts as 1.
        /// </summary>
        internal static float ThrustFraction(ThrustInputs m)
        {
            float flow = 1f;
            if (m.atmChangeFlow)
                flow = m.useAtmCurve ? m.atmCurve : (float)(m.airDensity * FlowPerDensity);
            flow *= m.velCurve;
            if (flow > m.flowMultCap)
            {
                float over = flow - m.flowMultCap;
                flow = m.flowMultCap + over / (m.flowMultCapSharpness + over / m.flowMultCap);
            }
            if (flow < m.clamp) flow = m.clamp;
            if (!(m.ispVacuum > 0f)) return 1f;
            float fraction = flow * m.multFlow * m.multIsp * m.throttlingIsp * m.atmCurveIsp
                * m.velCurveIsp * (m.ispHere / m.ispVacuum);
            if (float.IsNaN(fraction) || float.IsInfinity(fraction) || fraction < 0f) return 1f;
            return fraction;
        }
    }

    /// <summary>
    /// Stock <c>PrefabParticleFX.OnEvent(power)</c> ends with <c>emitter.Play()</c> on every physics
    /// step. Unity's <c>ParticleSystem.Play</c> (UnityPlayer 2019.4.18) restarts a system whose
    /// needs-restart flag is set, and only Pause or Simulate clears that flag, so each call zeroes
    /// the emission accumulator: between two steps a frame emits <c>floor(rate * dt)</c> particles
    /// and the remainder is lost. That is why a jet below about one particle a frame draws no
    /// smoke. A ghost draws the same density only by making the same call at the same cadence.
    /// </summary>
    internal sealed class GhostPrefabFxRestart : MonoBehaviour
    {
        internal ParticleSystem system;

        private void FixedUpdate()
        {
            // isEmitting is false after any stop, the lingering one included.
            if (system != null && system.isEmitting) system.Play();
        }
    }
}
