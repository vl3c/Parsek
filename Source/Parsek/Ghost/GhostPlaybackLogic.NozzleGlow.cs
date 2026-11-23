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

        // The clip follows the VESSEL's throttle (no dependOnThrottle, the Whiplash and the wet
        // Panther) and its engine does not (independent throttle or throttle lock), so no recorded
        // power tells it: the level is vesselThrottle, the snapshot's main throttle (NaN = unknown),
        // while the engine runs.
        public bool vesselThrottleUnrecorded;
        public float vesselThrottle = float.NaN;

        // The glow is a colour curve on materials of its own, not a clip: a throttle-driven
        // ModuleColorChanger, or ReStock's RCS glow. samples is its curve.
        public bool isColorChanger;
        public Color[] samples;
        // The other throttle-driven colour changers that write the same property of the same
        // materials, in frame order, all before this one; null for the only writer.
        public NozzleGlowInfo[] under;
        // Where the setter call stands among the part's (a later one overwrites an earlier one).
        public int setterOrder;
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
        public NozzleGlowInfo writtenBy;
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

        /// <summary>The level the glow's recorded powers ask for now.</summary>
        internal static float NozzleGlowTarget(NozzleGlowInfo glow)
        {
            if (float.IsNaN(glow.vesselThrottle))
                return ResolveNozzleGlowTarget(glow.modulePowers, glow.powerScale);
            if (ResolveNozzleGlowTarget(glow.modulePowers, 1f) <= 0f || glow.vesselThrottle <= 0f) return 0f;
            return glow.vesselThrottle > 1f ? 1f : glow.vesselThrottle;
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
        /// <summary>A row's engine seeds are stamped up to this long after its start (a dock's or a
        /// board's by up to 0.2 s).</summary>
        internal const double NozzleGlowSeedSlackSeconds = 0.5;

        /// <summary>
        /// Binds the ghost's glows at spawn and puts each at its level when the recording starts.
        /// A persisted level counts from its snapshot's UT; a row cut off an earlier one carries
        /// that row's snapshot, so the recorded powers in between are replayed first.
        /// </summary>
        internal static void BindNozzleGlows(GhostPlaybackState state, IPlaybackTrajectory traj)
            => BindNozzleGlows(state, traj, RecordingChainQueries.GetChainPredecessor, GetNozzleGlowSplitParent);

        internal static void BindNozzleGlows(
            GhostPlaybackState state, IPlaybackTrajectory traj, Func<Recording, Recording> predecessorOf,
            Func<Recording, Recording> splitParentOf = null)
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
            SettlePersistedGlowLevels(state, traj, predecessorOf, splitParentOf);
            RestartNozzleGlows(state);
            UpdateNozzleGlows(state, state.nozzleGlowStartUT);
        }

        /// <summary>
        /// Brings every persisted level to the recording start: dropped when its snapshot is a
        /// later state, replayed through the earlier rows when the snapshot is an earlier row's.
        /// </summary>
        private static void SettlePersistedGlowLevels(
            GhostPlaybackState state, IPlaybackTrajectory traj, Func<Recording, Recording> predecessorOf,
            Func<Recording, Recording> splitParentOf)
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
            if (double.IsNaN(snapshotUT) || snapshotUT >= startUT - NozzleGlowSnapshotSlackSeconds)
            {
                SettleRampsAtSplit(state, traj as Recording, predecessorOf, splitParentOf);
                return;
            }

            List<Recording> rows = CollectNozzleGlowHistory(traj as Recording, snapshotUT, predecessorOf);
            if (rows.Count == 0) return;
            if (rows[0].StartUT <= snapshotUT + NozzleGlowSnapshotSlackSeconds)
                SettleRampsAtSplit(state, rows[0], predecessorOf, splitParentOf);
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
        /// The ramps of a row that begins at a split. A snapshot holds a ramp's switch, not where it
        /// stood, and stock starts it at the switch's end when it LOADS the part; a split loads
        /// nothing, so a dropped stage's bell is still warm and a running one may be half hot. Where
        /// it stood is in the engine events recorded before the split (a part keeps its
        /// persistentId), replayed from the first row of the line; a ramp whose engine has none
        /// keeps the snapshot's end.
        /// </summary>
        private static void SettleRampsAtSplit(
            GhostPlaybackState state, Recording row,
            Func<Recording, Recording> predecessorOf, Func<Recording, Recording> splitParentOf)
        {
            List<NozzleGlowInfo> glows = state.nozzleGlows;
            bool anyRamp = false;
            for (int i = 0; i < glows.Count && !anyRamp; i++) anyRamp = glows[i].ramps;
            if (!anyRamp || row == null) return;
            List<Recording> line = CollectNozzleGlowSplitLine(row, predecessorOf, splitParentOf);
            if (line.Count == 0) return;

            double headUT = line[0].StartUT;
            double splitUT = row.StartUT;
            var seen = new bool[glows.Count];
            for (int i = 0; i < glows.Count; i++)
            {
                NozzleGlowInfo g = glows[i];
                for (int m = 0; g.modulePowers != null && m < g.modulePowers.Length; m++) g.modulePowers[m] = 0f;
                g.startLevel = 0f;
                g.targetLevel = 0f;
                g.startRamp = 0f;
                g.startUT = headUT;
            }
            for (int r = 0; r < line.Count; r++)
            {
                List<PartEvent> rowEvents = line[r].PartEvents;
                for (int e = 0; rowEvents != null && e < rowEvents.Count && rowEvents[e].ut <= splitUT; e++)
                {
                    if (!TryReadGlowPower(rowEvents[e], out float power, out bool rcs)) continue;
                    ApplyNozzleGlowPower(state, rowEvents[e], power, rcs, log: false);
                    ulong key = FlightRecorder.EncodeEngineKey(rowEvents[e].partPersistentId, rowEvents[e].moduleIndex);
                    for (int i = 0; i < glows.Count; i++)
                    {
                        NozzleGlowInfo g = glows[i];
                        if (seen[i] || g.isRcs != rcs || g.moduleKeys == null || Array.IndexOf(g.moduleKeys, key) < 0)
                            continue;
                        seen[i] = true;
                        // Running already when the line's first row began (an idle engine is seeded
                        // off): that row's snapshot puts the ramp at its end.
                        if (r == 0 && rowEvents[e].ut <= headUT + NozzleGlowSeedSlackSeconds)
                            g.startRamp = NozzleGlowRampEnd(g.targetLevel);
                    }
                }
            }
            int settled = 0;
            for (int i = 0; i < glows.Count; i++)
            {
                NozzleGlowInfo g = glows[i];
                if (!g.ramps || !seen[i]) continue;
                g.initialRamp = NozzleGlowRampAt(g, double.IsNaN(g.initialUT) ? splitUT : g.initialUT);
                settled++;
            }
            ParsekLog.VerboseRateLimited("GhostFx", "nozzle-glow-split-" + (state.recordingId ?? string.Empty),
                $"Nozzle glow at a split: vessel='{state.vesselName ?? "unknown"}' rows={line.Count} " +
                $"ramps={settled}/{glows.Count} headUT={headUT.ToString("0.0", CultureInfo.InvariantCulture)} " +
                $"splitUT={splitUT.ToString("0.0", CultureInfo.InvariantCulture)}", 5.0);
        }

        /// <summary>
        /// Pure: the rows a row's parts were recorded in before the split it begins at, oldest
        /// first; empty when it does not begin at one.
        /// </summary>
        internal static List<Recording> CollectNozzleGlowSplitLine(
            Recording row, Func<Recording, Recording> predecessorOf, Func<Recording, Recording> splitParentOf)
        {
            var line = new List<Recording>();
            Recording next = row != null && splitParentOf != null ? splitParentOf(row) : null;
            while (next != null && next != row && line.Count < NozzleGlowHistoryMaxRows && !line.Contains(next))
            {
                line.Add(next);
                next = predecessorOf?.Invoke(next) ?? splitParentOf(next);
            }
            line.Reverse();
            return line;
        }

        private static Recording GetNozzleGlowSplitParent(Recording rec)
            => ResolveNozzleGlowSplitParent(rec, FindTreeById(RecordingStore.CommittedTrees, rec?.TreeId));

        /// <summary>
        /// Pure: the row a recording's parts came out of at the split that began it. Null when it
        /// began some other way (a launch, a dock, a switch) or stock loaded its vessel there: a
        /// Re-Fly attempt flies a vessel loaded from a rewind point's save.
        /// </summary>
        internal static Recording ResolveNozzleGlowSplitParent(Recording rec, RecordingTree tree)
        {
            if (rec == null || tree?.Recordings == null || !string.IsNullOrEmpty(rec.CreatingSessionId)) return null;
            BranchPoint split = FindBranchPointById(tree, rec.ParentBranchPointId);
            if (split?.ParentRecordingIds == null || split.ParentRecordingIds.Count != 1) return null;
            if (split.Type != BranchPointType.Undock && split.Type != BranchPointType.EVA
                && split.Type != BranchPointType.JointBreak && split.Type != BranchPointType.Breakup)
                return null;
            return tree.Recordings.TryGetValue(split.ParentRecordingIds[0], out Recording parent) ? parent : null;
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
                g.targetLevel = NozzleGlowTarget(g);
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
                g.targetLevel = NozzleGlowTarget(g);
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

        /// <summary>
        /// Whether the module's own Update writes its ramp at <paramref name="ut"/>: stock's every
        /// frame, KSPCommunityFixes' only while the ramp moves. Without a ramp never (the setter does).
        /// </summary>
        private static bool NozzleGlowRampWrites(NozzleGlowInfo glow, double ut)
            => glow.ramps && (!glow.settledShowsScalar
                              || NozzleGlowRampAt(glow, ut) != NozzleGlowRampEnd(glow.targetLevel));

        /// <summary>The writer whose setter call comes last in the frame.</summary>
        private static NozzleGlowInfo LastNozzleGlowSetter(NozzleGlowInfo glow)
        {
            NozzleGlowInfo last = glow;
            for (int i = 0; glow.under != null && i < glow.under.Length; i++)
                if (glow.under[i].setterOrder > last.setterOrder) last = glow.under[i];
            return last;
        }

        /// <summary>
        /// The writer whose value shows, and the value. In one frame every setter writes its level,
        /// then every module's Update its ramp, each in module order, onto the same renderers: the
        /// last Update that writes wins, and with none the last setter.
        /// </summary>
        internal static NozzleGlowInfo ShownNozzleGlow(NozzleGlowInfo glow, double ut, out float level)
        {
            int below = glow.under != null ? glow.under.Length : 0;
            for (int i = below; i >= 0; i--)
            {
                NozzleGlowInfo writer = i == below ? glow : glow.under[i];
                if (!NozzleGlowRampWrites(writer, ut)) continue;
                level = NozzleGlowRampAt(writer, ut);
                return writer;
            }
            NozzleGlowInfo setter = LastNozzleGlowSetter(glow);
            level = NozzleGlowScalarAt(setter, ut);
            return setter;
        }

        /// <summary>The writer and the level that show once nothing moves any more.</summary>
        internal static NozzleGlowInfo RestingNozzleGlow(NozzleGlowInfo glow, out float level)
        {
            int below = glow.under != null ? glow.under.Length : 0;
            for (int i = below; i >= 0; i--)
            {
                NozzleGlowInfo writer = i == below ? glow : glow.under[i];
                if (!writer.ramps || writer.settledShowsScalar) continue;
                level = NozzleGlowRampEnd(writer.targetLevel);
                return writer;
            }
            NozzleGlowInfo setter = LastNozzleGlowSetter(glow);
            level = setter.targetLevel;
            return setter;
        }

        private static bool IsNozzleGlowAtRest(NozzleGlowInfo glow)
        {
            if (!IsNozzleGlowWriterAtRest(glow)) return false;
            for (int i = 0; glow.under != null && i < glow.under.Length; i++)
                if (!IsNozzleGlowWriterAtRest(glow.under[i])) return false;
            return true;
        }

        private static bool IsNozzleGlowWriterAtRest(NozzleGlowInfo glow)
            => glow.startLevel == glow.targetLevel
               && (!glow.ramps || glow.startRamp == NozzleGlowRampEnd(glow.targetLevel));

        /// <summary>Per visible frame: writes the glows whose level moved. A settled glow costs
        /// a few compares; a ghost with no glow one null check.</summary>
        internal static void UpdateNozzleGlows(GhostPlaybackState state, double currentUT)
        {
            List<NozzleGlowInfo> glows = state?.nozzleGlows;
            if (glows == null) return;
            for (int i = 0; i < glows.Count; i++)
            {
                NozzleGlowInfo g = glows[i];
                NozzleGlowInfo resting = RestingNozzleGlow(g, out float rest);
                if (g.writtenLevel == rest && g.writtenBy == resting && IsNozzleGlowAtRest(g)) continue;
                if (double.IsNaN(g.startUT))
                {
                    // Latched on the state, so a restart of the event walk keeps it.
                    if (double.IsNaN(state.nozzleGlowStartUT)) state.nozzleGlowStartUT = currentUT;
                    g.startUT = state.nozzleGlowStartUT;
                }
                NozzleGlowInfo writer = ShownNozzleGlow(g, currentUT, out float level);
                if (NozzleGlowScalarAt(g, currentUT) == g.targetLevel) g.startLevel = g.targetLevel;
                if (g.ramps && NozzleGlowRampAt(g, currentUT) == NozzleGlowRampEnd(g.targetLevel))
                    g.startRamp = NozzleGlowRampEnd(g.targetLevel);
                if (g.writtenBy != writer) g.writtenLevel = float.NaN;
                if (!ShouldWriteNozzleGlow(g.writtenLevel, level, rest)) continue;
                g.writtenLevel = level;
                g.writtenBy = writer;
                if (g.colors != null && g.colors.Count > 0)
                    WriteNozzleGlowColors(g.colors, writer.samples, level);
            }
        }

        // Material.SetColor is an ECall; NoInlining keeps it out of the methods xUnit calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteNozzleGlowColors(List<NozzleGlowColor> colors, Color[] curve, float level)
        {
            for (int c = 0; c < colors.Count; c++)
            {
                NozzleGlowColor color = colors[c];
                if (color.material == null || string.IsNullOrEmpty(color.property)) continue;
                color.material.SetColor(color.property, ResolveNozzleGlowColor(curve ?? color.samples, level));
            }
        }

        #endregion
    }
}
