using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>One material colour a nozzle glow clip animates, sampled evenly over the clip.</summary>
    internal struct NozzleGlowColor
    {
        public Material material;
        public string property;
        public Color[] samples;
    }

    /// <summary>
    /// One FXModuleAnimateThrottle / FXModuleAnimateRCS glow clip of a ghost part: stock moves the
    /// clip's time toward the engine's throttle (or the nozzle's force) a little every physics
    /// step, so a nozzle heats up slowly and stays warm after the shutdown. Or one ModuleColorChanger
    /// an FXModuleThrottleEffects drives, whose colour curves take the clip's place.
    /// </summary>
    internal sealed class NozzleGlowInfo
    {
        public uint partPersistentId;
        public string moduleName;
        public string animationName;
        public bool isRcs;
        // Recorder module keys (FlightRecorder.EncodeEngineKey) that drive the clip, and the last
        // recorded power of each: a multi-mode engine's clip follows whichever mode runs.
        public ulong[] moduleKeys;
        public float[] modulePowers;
        public float powerScale = 1f;
        public float approachRate;
        // The clip time the recorded vessel persisted (its nozzle was still warm), or 0, and the
        // UT it was persisted at (the snapshot's lastUT; NaN = unknown).
        public float initialLevel;
        public double initialUT = double.NaN;
        // Power the module runs at before its first recorded event: 1 for an engine the orphan
        // auto-start lights, else 0.
        public float basePower;
        public List<NozzleGlowColor> colors = new List<NozzleGlowColor>();

        // A useRate ModuleColorChanger: SetScalar turns the level into a switch (animState, on from
        // 0.5) and the module's own Update, which runs after the setter's, shows a ramp toward that
        // end at rampRate per second. With settledShowsScalar (KSPCommunityFixes) the module writes
        // only while the ramp moves, so once it stands at its end the setter's level shows.
        public bool ramps;
        public float rampRate;
        public bool settledShowsScalar;
        public float initialRamp;
        public float startRamp;

        // Playback: the level was startLevel at startUT and approaches targetLevel since.
        public float startLevel;
        public float targetLevel;
        public double startUT = double.NaN;
        public float writtenLevel = float.NaN;
    }

    // Engine and RCS nozzle glow (FXModuleAnimateThrottle / FXModuleAnimateRCS).
    internal static partial class GhostPlaybackLogic
    {
        #region Nozzle glow

        /// <summary>Stock's per-step factor is <c>responseSpeed * 25 * fixedDeltaTime</c>.</summary>
        internal const float NozzleGlowResponseScale = 25f;
        private const double NozzleGlowPhysicsStepSeconds = 0.02;
        /// <summary>A level this close to its target is there (stock snaps at 0.00005).</summary>
        internal const float NozzleGlowSettledEpsilon = 0.0005f;
        /// <summary>A smaller level change is not worth a material write.</summary>
        internal const float NozzleGlowWriteEpsilon = 0.002f;

        /// <summary>
        /// Pure: stock's approach (decompiled FXModuleAnimateThrottle / FXModuleAnimateRCS
        /// FixedUpdate: <c>Lerp(animState, target, responseSpeed * 25 * fixedDeltaTime)</c> once
        /// per 0.02 s step) as a continuous rate per second. Infinite when one step reaches the
        /// target, 0 when it never moves.
        /// </summary>
        internal static float ComputeNozzleGlowRate(float responseSpeed)
        {
            double step = responseSpeed * (double)NozzleGlowResponseScale * NozzleGlowPhysicsStepSeconds;
            if (double.IsNaN(step) || step <= 0.0) return 0f;
            if (step >= 1.0) return float.PositiveInfinity;
            return (float)(-Math.Log(1.0 - step) / NozzleGlowPhysicsStepSeconds);
        }

        /// <summary>Pure: the clip time <paramref name="elapsedSeconds"/> after it was
        /// <paramref name="startLevel"/>, approaching <paramref name="targetLevel"/>.</summary>
        internal static float ComputeNozzleGlowLevel(
            float startLevel, float targetLevel, float ratePerSecond, double elapsedSeconds)
        {
            if (double.IsNaN(elapsedSeconds) || elapsedSeconds <= 0.0 || ratePerSecond <= 0f)
                return startLevel;
            if (float.IsPositiveInfinity(ratePerSecond)) return targetLevel;
            double left = (startLevel - targetLevel) * Math.Exp(-ratePerSecond * elapsedSeconds);
            return Math.Abs(left) < NozzleGlowSettledEpsilon ? targetLevel : (float)(targetLevel + left);
        }

        /// <summary>The level from which stock's <c>ModuleColorChanger.SetScalar</c> switches on.</summary>
        internal const float NozzleGlowRampThreshold = 0.5f;

        /// <summary>Pure: the end a ramping colour changer moves to for a level.</summary>
        internal static float NozzleGlowRampEnd(float targetLevel)
            => targetLevel >= NozzleGlowRampThreshold ? 1f : 0f;

        /// <summary>
        /// Pure: stock's <c>currentRateState</c> (decompiled ModuleColorChanger.FixedUpdate)
        /// <paramref name="elapsedSeconds"/> after it was <paramref name="startRamp"/>. The switch is
        /// taken at once: every FXModuleThrottleEffects ReStock ships answers in two physics steps.
        /// </summary>
        internal static float ComputeNozzleGlowRamp(
            float startRamp, float targetLevel, float ratePerSecond, double elapsedSeconds)
        {
            if (double.IsNaN(elapsedSeconds) || elapsedSeconds <= 0.0 || !(ratePerSecond > 0f))
                return startRamp;
            float end = NozzleGlowRampEnd(targetLevel);
            double moved = ratePerSecond * elapsedSeconds;
            return moved >= Math.Abs(end - startRamp)
                ? end
                : (float)(startRamp + (end > startRamp ? moved : -moved));
        }

        /// <summary>Pure: the clip time the recorded powers ask for: the strongest module's power
        /// through the module's scale, clamped to the clip.</summary>
        internal static float ResolveNozzleGlowTarget(float[] modulePowers, float powerScale)
        {
            float strongest = 0f;
            if (modulePowers != null)
                for (int i = 0; i < modulePowers.Length; i++)
                    if (modulePowers[i] > strongest) strongest = modulePowers[i];
            float target = strongest * powerScale;
            if (float.IsNaN(target) || target <= 0f) return 0f;
            return target > 1f ? 1f : target;
        }

        /// <summary>Pure: the clip colour at <paramref name="level"/>, between evenly spaced samples.</summary>
        internal static Color ResolveNozzleGlowColor(Color[] samples, float level)
        {
            if (samples == null || samples.Length == 0) return Color.black;
            int last = samples.Length - 1;
            if (level <= 0f || last == 0) return samples[0];
            if (level >= 1f) return samples[last];
            float at = level * last;
            int i = (int)at;
            float f = at - i;
            Color a = samples[i], b = samples[i + 1];
            return new Color(a.r + (b.r - a.r) * f, a.g + (b.g - a.g) * f,
                a.b + (b.b - a.b) * f, a.a + (b.a - a.a) * f);
        }

        /// <summary>Pure: whether a level needs a material write: the first one, a visible change,
        /// or the last small step onto the target.</summary>
        internal static bool ShouldWriteNozzleGlow(float writtenLevel, float level, float targetLevel)
        {
            if (float.IsNaN(writtenLevel)) return true;
            if (level == writtenLevel) return false;
            return level == targetLevel || Math.Abs(level - writtenLevel) >= NozzleGlowWriteEpsilon;
        }

        /// <summary>A snapshot this much older than its recording's start was not taken there (it
        /// is the copy a row cut hands the later row); this much newer, it is a later state.</summary>
        internal const double NozzleGlowSnapshotSlackSeconds = 5.0;
        private const int NozzleGlowHistoryMaxRows = 64;

        /// <summary>
        /// Binds the ghost's glows at spawn and puts each at its level when the recording starts.
        /// A persisted level counts from its snapshot's UT; a row cut off an earlier one carries
        /// that row's snapshot, so the recorded powers in between are replayed first.
        /// </summary>
        internal static void BindNozzleGlows(GhostPlaybackState state, IPlaybackTrajectory traj)
            => BindNozzleGlows(state, traj, RecordingChainQueries.GetChainPredecessor);

        internal static void BindNozzleGlows(
            GhostPlaybackState state, IPlaybackTrajectory traj, Func<Recording, Recording> predecessorOf)
        {
            if (state == null) return;
            state.nozzleGlows = null;
            state.nozzleGlowStartUT = traj != null ? traj.StartUT : double.NaN;
            if (state.heatInfos == null) return;
            foreach (var kvp in state.heatInfos)
            {
                List<NozzleGlowInfo> glows = kvp.Value?.nozzleGlows;
                if (glows == null || glows.Count == 0) continue;
                if (state.nozzleGlows == null) state.nozzleGlows = new List<NozzleGlowInfo>();
                state.nozzleGlows.AddRange(glows);
            }
            if (state.nozzleGlows == null) return;
            SettlePersistedGlowLevels(state, traj, predecessorOf);
            RestartNozzleGlows(state);
            UpdateNozzleGlows(state, state.nozzleGlowStartUT);
        }

        /// <summary>
        /// Brings every persisted level to the recording start: dropped when its snapshot is a
        /// later state, replayed through the earlier rows when the snapshot is an earlier row's.
        /// </summary>
        private static void SettlePersistedGlowLevels(
            GhostPlaybackState state, IPlaybackTrajectory traj, Func<Recording, Recording> predecessorOf)
        {
            double startUT = state.nozzleGlowStartUT;
            if (double.IsNaN(startUT)) return;
            double snapshotUT = double.NaN;
            for (int i = 0; i < state.nozzleGlows.Count; i++)
            {
                NozzleGlowInfo g = state.nozzleGlows[i];
                if (double.IsNaN(g.initialUT)) continue;
                if (g.initialUT > startUT + NozzleGlowSnapshotSlackSeconds)
                {
                    g.initialLevel = 0f;
                    g.initialRamp = 0f;
                    g.initialUT = double.NaN;
                    continue;
                }
                snapshotUT = g.initialUT;
            }
            if (double.IsNaN(snapshotUT) || snapshotUT >= startUT - NozzleGlowSnapshotSlackSeconds) return;

            List<Recording> rows = CollectNozzleGlowHistory(traj as Recording, snapshotUT, predecessorOf);
            if (rows.Count == 0) return;
            RestartNozzleGlows(state);
            int events = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                List<PartEvent> rowEvents = rows[r].PartEvents;
                if (rowEvents == null) continue;
                for (int e = 0; e < rowEvents.Count && rowEvents[e].ut <= startUT; e++)
                {
                    ApplyNozzleGlowEvent(state, rowEvents[e], log: false);
                    events++;
                }
            }
            for (int i = 0; i < state.nozzleGlows.Count; i++)
            {
                NozzleGlowInfo g = state.nozzleGlows[i];
                if (double.IsNaN(g.initialUT)) continue;
                g.initialLevel = NozzleGlowScalarAt(g, startUT);
                g.initialRamp = NozzleGlowRampAt(g, startUT);
                g.initialUT = startUT;
            }
            ParsekLog.VerboseRateLimited("GhostFx", "nozzle-glow-history-" + (state.recordingId ?? string.Empty),
                $"Nozzle glow history: vessel='{state.vesselName ?? "unknown"}' rows={rows.Count} events={events} " +
                $"snapshotUT={snapshotUT.ToString("0.0", CultureInfo.InvariantCulture)} " +
                $"startUT={startUT.ToString("0.0", CultureInfo.InvariantCulture)}", 5.0);
        }

        /// <summary>Pure: the chain rows before <paramref name="rec"/> back to the one the
        /// snapshot was taken in, oldest first; empty when there is none.</summary>
        internal static List<Recording> CollectNozzleGlowHistory(
            Recording rec, double snapshotUT, Func<Recording, Recording> predecessorOf)
        {
            var rows = new List<Recording>();
            if (rec == null || predecessorOf == null) return rows;
            Recording row = rec;
            while (rows.Count < NozzleGlowHistoryMaxRows && row.StartUT > snapshotUT + NozzleGlowSnapshotSlackSeconds)
            {
                row = predecessorOf(row);
                if (row == null || row == rec || rows.Contains(row)) break;
                rows.Add(row);
            }
            rows.Reverse();
            return rows;
        }

        /// <summary>
        /// Puts every glow back to the recording start, so a replay of the part events from the
        /// first one reaches the same level whatever the playback did before (loop cycle, scrub).
        /// The next write is forced: a loop cycle's cold heat write has been on the material.
        /// </summary>
        internal static void RestartNozzleGlows(GhostPlaybackState state)
        {
            List<NozzleGlowInfo> glows = state?.nozzleGlows;
            if (glows == null) return;
            for (int i = 0; i < glows.Count; i++)
            {
                NozzleGlowInfo g = glows[i];
                if (g.modulePowers != null)
                    for (int m = 0; m < g.modulePowers.Length; m++) g.modulePowers[m] = g.basePower;
                g.startLevel = g.initialLevel;
                g.startRamp = g.initialRamp;
                g.startUT = double.IsNaN(g.initialUT) ? state.nozzleGlowStartUT : g.initialUT;
                g.targetLevel = ResolveNozzleGlowTarget(g.modulePowers, g.powerScale);
                g.writtenLevel = float.NaN;
            }
        }

        /// <summary>The orphan auto-start's engine runs at full power from the recording start.</summary>
        internal static void SetNozzleGlowBasePower(GhostPlaybackState state, ulong engineKey, float power)
        {
            List<NozzleGlowInfo> glows = state?.nozzleGlows;
            if (glows == null) return;
            bool any = false;
            for (int i = 0; i < glows.Count; i++)
            {
                NozzleGlowInfo g = glows[i];
                if (g.isRcs || g.moduleKeys == null || Array.IndexOf(g.moduleKeys, engineKey) < 0) continue;
                g.basePower = power;
                any = true;
            }
            if (any) RestartNozzleGlows(state);
        }

        /// <summary>Pure: whether an event carries an engine / RCS power, and which.</summary>
        internal static bool TryReadGlowPower(PartEvent evt, out float power, out bool rcs)
        {
            power = 0f;
            rcs = false;
            switch (evt.eventType)
            {
                case PartEventType.EngineIgnited:
                case PartEventType.EngineThrottle:
                    power = evt.value;
                    return true;
                case PartEventType.EngineShutdown:
                    return true;
                case PartEventType.RCSActivated:
                case PartEventType.RCSThrottle:
                    power = evt.value;
                    rcs = true;
                    return true;
                case PartEventType.RCSStopped:
                    rcs = true;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The part-event walk's glow half: every consumed event goes through here.</summary>
        internal static void ApplyNozzleGlowEvent(GhostPlaybackState state, PartEvent evt, bool log = true)
        {
            if (state?.nozzleGlows == null) return;
            if (TryReadGlowPower(evt, out float power, out bool rcs))
                ApplyNozzleGlowPower(state, evt, power, rcs, log);
        }

        /// <summary>
        /// A recorded engine / RCS power change: the glows it drives approach the new level from
        /// the one they had at the event's UT. Pure in the recorded UT, so a catch-up or a scrub
        /// reaches the same level as real-time playback.
        /// </summary>
        internal static void ApplyNozzleGlowPower(
            GhostPlaybackState state, PartEvent evt, float power, bool rcs, bool log = true)
        {
            List<NozzleGlowInfo> glows = state?.nozzleGlows;
            if (glows == null) return;
            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            for (int i = 0; i < glows.Count; i++)
            {
                NozzleGlowInfo g = glows[i];
                if (g.isRcs != rcs || g.moduleKeys == null) continue;
                int slot = Array.IndexOf(g.moduleKeys, key);
                if (slot < 0) continue;
                // An event from before the persisted level changes the power, never the level.
                double at = evt.ut < g.startUT ? g.startUT : evt.ut;
                g.startLevel = NozzleGlowScalarAt(g, at);
                g.startRamp = NozzleGlowRampAt(g, at);
                g.startUT = at;
                g.modulePowers[slot] = power;
                g.targetLevel = ResolveNozzleGlowTarget(g.modulePowers, g.powerScale);
                if (!log) continue;
                ParsekLog.VerboseRateLimited("GhostFx",
                    "nozzle-glow-" + g.partPersistentId.ToString(CultureInfo.InvariantCulture)
                        + "-" + g.animationName,
                    $"Nozzle glow pid={g.partPersistentId} {g.moduleName} '{g.animationName}': " +
                    $"level={g.startLevel.ToString("0.000", CultureInfo.InvariantCulture)} -> " +
                    $"target={g.targetLevel.ToString("0.000", CultureInfo.InvariantCulture)} " +
                    $"rate={g.approachRate.ToString("0.####", CultureInfo.InvariantCulture)}/s " +
                    (g.ramps
                        ? $"ramp={g.startRamp.ToString("0.000", CultureInfo.InvariantCulture)} " +
                          $"rampRate={g.rampRate.ToString("0.####", CultureInfo.InvariantCulture)}/s "
                        : string.Empty) +
                    $"ut={evt.ut.ToString("0.00", CultureInfo.InvariantCulture)}", 5.0);
            }
        }

        /// <summary>The level the module is at: the clip time, or what the setter hands a colour changer.</summary>
        internal static float NozzleGlowScalarAt(NozzleGlowInfo glow, double ut)
            => ComputeNozzleGlowLevel(glow.startLevel, glow.targetLevel, glow.approachRate, ut - glow.startUT);

        internal static float NozzleGlowRampAt(NozzleGlowInfo glow, double ut)
            => glow.ramps
                ? ComputeNozzleGlowRamp(glow.startRamp, glow.targetLevel, glow.rampRate, ut - glow.startUT)
                : 0f;

        /// <summary>The level that shows.</summary>
        internal static float NozzleGlowLevelAt(NozzleGlowInfo glow, double ut)
        {
            if (!glow.ramps) return NozzleGlowScalarAt(glow, ut);
            float ramp = NozzleGlowRampAt(glow, ut);
            return glow.settledShowsScalar && ramp == NozzleGlowRampEnd(glow.targetLevel)
                ? NozzleGlowScalarAt(glow, ut)
                : ramp;
        }

        /// <summary>The level that shows once nothing moves any more.</summary>
        internal static float NozzleGlowRestLevel(NozzleGlowInfo glow)
            => glow.ramps && !glow.settledShowsScalar ? NozzleGlowRampEnd(glow.targetLevel) : glow.targetLevel;

        /// <summary>Per visible frame: writes the glows whose level moved. A settled glow costs
        /// a few compares; a ghost with no glow one null check.</summary>
        internal static void UpdateNozzleGlows(GhostPlaybackState state, double currentUT)
        {
            List<NozzleGlowInfo> glows = state?.nozzleGlows;
            if (glows == null) return;
            for (int i = 0; i < glows.Count; i++)
            {
                NozzleGlowInfo g = glows[i];
                float rest = NozzleGlowRestLevel(g);
                if (g.writtenLevel == rest && g.startLevel == g.targetLevel
                    && (!g.ramps || g.startRamp == NozzleGlowRampEnd(g.targetLevel))) continue;
                if (double.IsNaN(g.startUT))
                {
                    // Latched on the state, so a restart of the event walk keeps it.
                    if (double.IsNaN(state.nozzleGlowStartUT)) state.nozzleGlowStartUT = currentUT;
                    g.startUT = state.nozzleGlowStartUT;
                }
                float level = NozzleGlowLevelAt(g, currentUT);
                if (NozzleGlowScalarAt(g, currentUT) == g.targetLevel) g.startLevel = g.targetLevel;
                if (g.ramps && NozzleGlowRampAt(g, currentUT) == NozzleGlowRampEnd(g.targetLevel))
                    g.startRamp = NozzleGlowRampEnd(g.targetLevel);
                if (!ShouldWriteNozzleGlow(g.writtenLevel, level, rest)) continue;
                g.writtenLevel = level;
                if (g.colors != null && g.colors.Count > 0) WriteNozzleGlowColors(g.colors, level);
            }
        }

        // Material.SetColor is an ECall; NoInlining keeps it out of the methods xUnit calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteNozzleGlowColors(List<NozzleGlowColor> colors, float level)
        {
            for (int c = 0; c < colors.Count; c++)
            {
                NozzleGlowColor color = colors[c];
                if (color.material == null || string.IsNullOrEmpty(color.property)) continue;
                color.material.SetColor(color.property, ResolveNozzleGlowColor(color.samples, level));
            }
        }

        #endregion
    }
}
