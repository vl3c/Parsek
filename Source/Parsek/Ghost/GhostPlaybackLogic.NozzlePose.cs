using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>What one clip does to one transform, sampled evenly over the clip. Only the channels
    /// the clip moves are set.</summary>
    internal sealed class NozzlePoseTrack
    {
        public Vector3[] positions;
        public Quaternion[] rotations;
        public Vector3[] scales;
    }

    /// <summary>
    /// The transform half of one FXModuleAnimateThrottle clip of a ghost part (a variable nozzle's
    /// petals). Its time is the level of the glow that carries it; its share of the pose is the
    /// weight stock gives a <c>weightOnOperational</c> clip, 1 for any other.
    /// </summary>
    internal sealed class NozzlePoseClip
    {
        public int layer;
        public bool weighted;
        public float weightRate;
        // The ghost's transforms the clip moves and, parallel, what it does to each.
        public List<Transform> transforms = new List<Transform>();
        public List<NozzlePoseTrack> tracks = new List<NozzlePoseTrack>();

        // Playback: the weight was startWeight at weightUT and approaches targetWeight since.
        public float startWeight = 1f;
        public float targetWeight = 1f;
        public double weightUT = double.NaN;
        public float writtenLevel = float.NaN;
        public float writtenWeight = float.NaN;
    }

    /// <summary>The pose clips of one ghost part, lowest layer first, and the transforms they move.</summary>
    internal sealed class NozzlePoseInfo
    {
        public uint partPersistentId;
        // Parallel: the glow whose level is the clip's time, and the clip (the glow's pose).
        public NozzleGlowInfo[] glows;
        public NozzlePoseClip[] clips;
        public Transform[] targets;
        // clipTracks[c][t]: what clip c does to target t, null when it does not move it.
        public NozzlePoseTrack[][] clipTracks;
        // Scratch of one update, one value a clip.
        public float[] levels;
        public float[] weights;
        // Nothing moves until the next event or restart.
        public bool settled;
    }

    /// <summary>
    /// A looping FXModuleAnimateThrottle clip (<c>affectTime</c> off) of a ghost part: stock moves
    /// its speed toward <c>baseAnimSpeed + throttle * baseAnimSpeedMult</c>, at most 1 (the clip's
    /// own rate), and the time runs on.
    /// </summary>
    internal sealed class EngineSpin
    {
        public float baseSpeed;
        public float speedMult;
        public float clipSeconds;
        public List<ConverterLoopTransformState> transforms;

        // Playback: the clip stood at startPhase when the glow's current segment began.
        public float startPhase;
        public float writtenPhase = float.NaN;
    }

    // Engine clips that move transforms: variable nozzles and the turbofan's fan.
    internal static partial class GhostPlaybackLogic
    {
        #region Nozzle poses and engine spin

        /// <summary>A smaller change of a pose clip's time or weight is not worth the writes.</summary>
        internal const float NozzlePoseWriteEpsilon = 0.002f;

        /// <summary>Stock clamps the speed of an <c>affectTime</c>-less clip at 1.</summary>
        internal const float EngineSpinMaxSpeed = 1f;

        /// <summary>One pose info per part, out of the glows that carry a pose clip.</summary>
        internal static void CollectNozzlePoses(GhostPlaybackState state)
        {
            List<NozzleGlowInfo> moving = state.nozzleGlows.FindAll(g => g.pose != null);
            while (moving.Count > 0)
            {
                uint pid = moving[0].partPersistentId;
                List<NozzleGlowInfo> part = moving.FindAll(g => g.partPersistentId == pid);
                moving.RemoveAll(g => g.partPersistentId == pid);
                if (state.nozzlePoses == null) state.nozzlePoses = new List<NozzlePoseInfo>();
                state.nozzlePoses.Add(NewNozzlePose(pid, part));
            }
        }

        private static NozzlePoseInfo NewNozzlePose(uint pid, List<NozzleGlowInfo> glows)
        {
            // Stable: clips of one layer keep the module order.
            for (int i = 1; i < glows.Count; i++)
                for (int j = i; j > 0 && glows[j - 1].pose.layer > glows[j].pose.layer; j--)
                {
                    NozzleGlowInfo swap = glows[j];
                    glows[j] = glows[j - 1];
                    glows[j - 1] = swap;
                }
            var targets = new List<Transform>();
            for (int c = 0; c < glows.Count; c++)
                for (int t = 0; t < glows[c].pose.transforms.Count; t++)
                    if (IndexOfReference(targets, glows[c].pose.transforms[t]) < 0)
                        targets.Add(glows[c].pose.transforms[t]);
            var pose = new NozzlePoseInfo
            {
                partPersistentId = pid,
                glows = glows.ToArray(),
                clips = new NozzlePoseClip[glows.Count],
                targets = targets.ToArray(),
                clipTracks = new NozzlePoseTrack[glows.Count][],
                levels = new float[glows.Count],
                weights = new float[glows.Count],
            };
            for (int c = 0; c < glows.Count; c++)
            {
                NozzlePoseClip clip = glows[c].pose;
                pose.clips[c] = clip;
                pose.clipTracks[c] = new NozzlePoseTrack[targets.Count];
                for (int t = 0; t < clip.transforms.Count; t++)
                    pose.clipTracks[c][IndexOfReference(targets, clip.transforms[t])] = clip.tracks[t];
            }
            return pose;
        }

        private static int IndexOfReference(List<Transform> list, Transform item)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        /// <summary>Whether the engine that drives the clip runs, as far as the recording tells.</summary>
        private static bool IsNozzlePoseRunning(NozzleGlowInfo glow)
            => ResolveNozzleGlowTarget(glow.modulePowers, 1f) > 0f;

        /// <summary>
        /// Puts the weights back to the recording start. Before any engine has run stock's weights
        /// have all decayed and the last one left, the lowest layer's, holds the pose.
        /// </summary>
        internal static void RestartNozzlePoses(GhostPlaybackState state)
        {
            List<NozzlePoseInfo> poses = state?.nozzlePoses;
            if (poses == null) return;
            for (int p = 0; p < poses.Count; p++)
            {
                NozzlePoseInfo pose = poses[p];
                bool any = AnyWeightedClipRuns(pose);
                bool lowest = true;
                for (int c = 0; c < pose.clips.Length; c++)
                {
                    NozzlePoseClip clip = pose.clips[c];
                    if (!clip.weighted) clip.targetWeight = 1f;
                    else if (any) clip.targetWeight = IsNozzlePoseRunning(pose.glows[c]) ? 1f : 0f;
                    else
                    {
                        clip.targetWeight = lowest ? 1f : 0f;
                        lowest = false;
                    }
                    clip.startWeight = clip.targetWeight;
                    clip.weightUT = state.nozzleGlowStartUT;
                    clip.writtenLevel = float.NaN;
                }
                pose.settled = false;
            }
        }

        private static bool AnyWeightedClipRuns(NozzlePoseInfo pose)
        {
            for (int c = 0; c < pose.clips.Length; c++)
                if (pose.clips[c].weighted && IsNozzlePoseRunning(pose.glows[c])) return true;
            return false;
        }

        /// <summary>
        /// An engine event on the part: the running engine's clips take the pose over (stock moves a
        /// weight by 3 x responseSpeed). While none runs the last one keeps it: a shut-down engine's
        /// clip is still the one the pose was left at, and an idle one's is at full weight; the
        /// recording does not tell the two apart. A row that begins with the engine running has no
        /// hand-over to show.
        /// </summary>
        internal static void ApplyNozzlePoseEvent(GhostPlaybackState state, PartEvent evt)
        {
            List<NozzlePoseInfo> poses = state?.nozzlePoses;
            if (poses == null) return;
            for (int p = 0; p < poses.Count; p++)
            {
                NozzlePoseInfo pose = poses[p];
                if (pose.partPersistentId != evt.partPersistentId) continue;
                pose.settled = false;
                if (!AnyWeightedClipRuns(pose)) continue;
                bool atStart = !(evt.ut > state.nozzleGlowStartUT + NozzleGlowSeedSlackSeconds);
                for (int c = 0; c < pose.clips.Length; c++)
                {
                    NozzlePoseClip clip = pose.clips[c];
                    if (!clip.weighted) continue;
                    float target = IsNozzlePoseRunning(pose.glows[c]) ? 1f : 0f;
                    if (target == clip.targetWeight && !atStart) continue;
                    double at = evt.ut < clip.weightUT ? clip.weightUT : evt.ut;
                    clip.startWeight = atStart ? target : NozzlePoseWeightAt(clip, at);
                    clip.targetWeight = target;
                    clip.weightUT = at;
                }
            }
        }

        internal static float NozzlePoseWeightAt(NozzlePoseClip clip, double ut)
            => ComputeNozzleGlowLevel(clip.startWeight, clip.targetWeight, clip.weightRate, ut - clip.weightUT);

        /// <summary>Per visible frame, after the levels moved: a settled part costs one flag read,
        /// a moving one a write per petal bone (Panther 6, Whiplash 3).</summary>
        internal static void UpdateNozzlePoses(GhostPlaybackState state, double currentUT)
        {
            List<NozzlePoseInfo> poses = state?.nozzlePoses;
            if (poses == null) return;
            for (int p = 0; p < poses.Count; p++)
            {
                NozzlePoseInfo pose = poses[p];
                if (pose.settled) continue;
                bool changed = false;
                bool rest = true;
                for (int c = 0; c < pose.clips.Length; c++)
                {
                    NozzlePoseClip clip = pose.clips[c];
                    float level = NozzleGlowScalarAt(pose.glows[c], currentUT);
                    float weight = NozzlePoseWeightAt(clip, currentUT);
                    pose.levels[c] = level;
                    pose.weights[c] = weight;
                    rest &= level == pose.glows[c].targetLevel && weight == clip.targetWeight;
                    changed |= float.IsNaN(clip.writtenLevel)
                        || Math.Abs(level - clip.writtenLevel) >= NozzlePoseWriteEpsilon
                        || Math.Abs(weight - clip.writtenWeight) >= NozzlePoseWriteEpsilon;
                }
                if (!changed && !rest) continue;
                for (int c = 0; c < pose.clips.Length; c++)
                {
                    pose.clips[c].writtenLevel = pose.levels[c];
                    pose.clips[c].writtenWeight = pose.weights[c];
                }
                pose.settled = rest;
                WriteNozzlePose(pose);
                if (!rest) continue;
                ParsekLog.VerboseRateLimited("GhostFx",
                    "nozzle-pose-" + pose.partPersistentId.ToString(CultureInfo.InvariantCulture),
                    $"Nozzle pose pid={pose.partPersistentId} settled: {DescribeNozzlePose(pose)} transforms={pose.targets.Length} " +
                    $"ut={currentUT.ToString("0.00", CultureInfo.InvariantCulture)}", 5.0);
            }
        }

        private static string DescribeNozzlePose(NozzlePoseInfo pose)
        {
            string text = string.Empty;
            for (int c = 0; c < pose.clips.Length; c++)
                text += (c > 0 ? ", " : string.Empty) + "'" + pose.glows[c].animationName + "' at "
                    + pose.levels[c].ToString("0.000", CultureInfo.InvariantCulture) + " weight "
                    + pose.weights[c].ToString("0.000", CultureInfo.InvariantCulture);
            return text;
        }

        // Transform writes are ECalls; NoInlining keeps them out of the method xUnit calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteNozzlePose(NozzlePoseInfo pose)
        {
            for (int t = 0; t < pose.targets.Length; t++)
            {
                Transform target = pose.targets[t];
                if (target == null) continue;
                if (BlendNozzlePosition(pose, t, scale: false, out Vector3 position)) target.localPosition = position;
                if (BlendNozzleRotation(pose, t, out Quaternion rotation)) target.localRotation = rotation;
                if (BlendNozzlePosition(pose, t, scale: true, out Vector3 scaled)) target.localScale = scaled;
            }
        }

        /// <summary>
        /// Pure: Unity's legacy layer blend of one vector channel: a higher layer takes its weight of
        /// the pose, the layers below share what it leaves, and the total is normalized. False when no
        /// clip of any weight moves the channel.
        /// </summary>
        internal static bool BlendNozzlePosition(NozzlePoseInfo pose, int target, bool scale, out Vector3 value)
        {
            float x = 0f, y = 0f, z = 0f, left = 1f, total = 0f;
            for (int c = pose.clips.Length - 1; c >= 0; c--)
            {
                NozzlePoseTrack track = pose.clipTracks[c][target];
                Vector3[] samples = track == null ? null : (scale ? track.scales : track.positions);
                float share = left * pose.weights[c];
                if (samples == null || !(share > 0f)) continue;
                LerpSampleIndex(samples.Length, pose.levels[c], out int i, out float f);
                Vector3 a = samples[i], b = samples[i + (f > 0f ? 1 : 0)];
                x += share * (a.x + (b.x - a.x) * f);
                y += share * (a.y + (b.y - a.y) * f);
                z += share * (a.z + (b.z - a.z) * f);
                total += share;
                left -= share;
            }
            value = total > 0f ? new Vector3(x / total, y / total, z / total) : default(Vector3);
            return total > 0f;
        }

        /// <summary>Pure: the same blend of the rotation channel, as normalized sums of quaternions
        /// on one side.</summary>
        internal static bool BlendNozzleRotation(NozzlePoseInfo pose, int target, out Quaternion value)
        {
            float x = 0f, y = 0f, z = 0f, w = 0f, left = 1f, total = 0f;
            for (int c = pose.clips.Length - 1; c >= 0; c--)
            {
                Quaternion[] samples = pose.clipTracks[c][target]?.rotations;
                float share = left * pose.weights[c];
                if (samples == null || !(share > 0f)) continue;
                LerpSampleIndex(samples.Length, pose.levels[c], out int i, out float f);
                AddQuaternion(samples[i], share * (1f - f), ref x, ref y, ref z, ref w);
                if (f > 0f) AddQuaternion(samples[i + 1], share * f, ref x, ref y, ref z, ref w);
                total += share;
                left -= share;
            }
            double length = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w);
            bool any = total > 0f && length > 1e-6;
            value = any
                ? new Quaternion((float)(x / length), (float)(y / length), (float)(z / length), (float)(w / length))
                : new Quaternion(0f, 0f, 0f, 1f);
            return any;
        }

        private static void AddQuaternion(
            Quaternion q, float share, ref float x, ref float y, ref float z, ref float w)
        {
            if (x * q.x + y * q.y + z * q.z + w * q.w < 0f) share = -share;
            x += share * q.x;
            y += share * q.y;
            z += share * q.z;
            w += share * q.w;
        }

        /// <summary>Pure: the sample below a level and how far it is to the next, 0 at the last one.</summary>
        internal static void LerpSampleIndex(int count, float level, out int index, out float fraction)
        {
            int last = count - 1;
            fraction = 0f;
            if (!(level > 0f) || last <= 0) { index = 0; return; }
            if (level >= 1f) { index = last; return; }
            float at = level * last;
            index = (int)at;
            fraction = at - index;
        }

        /// <summary>Pure: the speed stock moves a running engine's looping clip toward, in clip lengths
        /// per clip length; 0 for a stopped one. An idle engine's base speed (0.01 on the Goliath)
        /// counts as 0: its throttle reads as a shutdown in the recording.</summary>
        internal static float EngineSpinTarget(EngineSpin spin, float throttle)
            => throttle > 0f ? spin.baseSpeed + throttle * spin.speedMult : 0f;

        internal static void RestartEngineSpin(EngineSpin spin)
        {
            spin.startPhase = 0f;
            spin.writtenPhase = float.NaN;
        }

        /// <summary>
        /// Pure: the clip lengths a speed covers in <paramref name="elapsed"/> seconds of a clip of one
        /// second: it approaches <paramref name="target"/> from <paramref name="start"/> by stock's
        /// rate and is capped at <see cref="EngineSpinMaxSpeed"/>.
        /// </summary>
        internal static double EngineSpinDistance(float start, float target, float ratePerSecond, double elapsed)
        {
            if (double.IsNaN(elapsed) || elapsed <= 0.0) return 0.0;
            double s0 = Math.Min(start, EngineSpinMaxSpeed);
            if (!(ratePerSecond > 0f)) return s0 * elapsed;
            double end = Math.Min(target, EngineSpinMaxSpeed);
            if (float.IsPositiveInfinity(ratePerSecond)) return end * elapsed;
            double r = ratePerSecond;
            if (target <= EngineSpinMaxSpeed)
                return end * elapsed + (s0 - end) * (1.0 - Math.Exp(-r * elapsed)) / r;
            double capped = s0 < EngineSpinMaxSpeed
                ? Math.Log((target - s0) / (target - EngineSpinMaxSpeed)) / r
                : 0.0;
            double t = Math.Min(elapsed, capped);
            return target * t - (target - s0) * (1.0 - Math.Exp(-r * t)) / r
                + (elapsed - t) * EngineSpinMaxSpeed;
        }

        /// <summary>Where in its clip a spin stands, 0 to 1.</summary>
        internal static float EngineSpinPhaseAt(NozzleGlowInfo glow, double ut)
        {
            EngineSpin spin = glow.spin;
            double turns = spin.startPhase + EngineSpinDistance(
                glow.startLevel, glow.targetLevel, glow.approachRate, ut - glow.startUT) / spin.clipSeconds;
            float phase = (float)(turns - Math.Floor(turns));
            return phase < 1f ? phase : 0f;
        }

        private static void UpdateEngineSpin(NozzleGlowInfo glow, double currentUT)
        {
            float phase = EngineSpinPhaseAt(glow, currentUT);
            if (phase == glow.spin.writtenPhase) return;
            glow.spin.writtenPhase = phase;
            WriteEngineSpin(glow.spin, phase);
        }

        // Transform writes are ECalls; NoInlining keeps them out of the method xUnit calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteEngineSpin(EngineSpin spin, float phase)
        {
            if (spin.transforms == null) return;
            for (int t = 0; t < spin.transforms.Count; t++)
            {
                ConverterLoopTransformState ts = spin.transforms[t];
                if (ts?.t == null || ts.phases == null || ts.phases.Length == 0) continue;
                ResolveConverterLoopBlend(phase, ts.phases.Length, out int from, out int to, out float blend);
                ConverterLoopPose a = ts.phases[from];
                ConverterLoopPose b = ts.phases[to];
                ts.t.localPosition = Vector3.Lerp(a.pos, b.pos, blend);
                ts.t.localRotation = Quaternion.Slerp(a.rot, b.rot, blend);
                ts.t.localScale = Vector3.Lerp(a.scale, b.scale, blend);
            }
        }

        #endregion
    }
}
