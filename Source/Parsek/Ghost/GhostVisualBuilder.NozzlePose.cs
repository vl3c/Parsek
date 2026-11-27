using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    // Engine clips that move transforms: variable nozzles (FXModuleAnimateThrottle) and the
    // turbofan's fan (the same module with affectTime off).
    internal static partial class GhostVisualBuilder
    {
        private struct NozzlePoseSample
        {
            // Index among the model's transforms, inactive ones included.
            public int transformIndex;
            public NozzlePoseTrack track;
        }

        private sealed class NozzleClipSamples
        {
            public List<NozzleGlowColorSample> colors = new List<NozzleGlowColorSample>();
            public List<NozzlePoseSample> poses = new List<NozzlePoseSample>();
        }

        /// <summary>Samples per pose clip, evenly spaced from 0 to 1. More than a colour clip's:
        /// the Whiplash's nozzle closes and opens again within one clip, with its key at 0.167.</summary>
        internal const int NozzlePoseSampleCount = 33;

        // Stock's clips share layers with the model's other clips; on a layer of its own the sampled
        // one shows alone.
        private const int NozzlePoseSampleLayer = 1000;

        private static void SampleNozzlePoses(
            Transform cloneRoot, List<Animation> animations, List<AnimationState> states, List<NozzlePoseSample> result)
        {
            Transform[] transforms = cloneRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < states.Count; i++) states[i].layer = NozzlePoseSampleLayer;
            var poses = new (Vector3 pos, Quaternion rot, Vector3 scale)[NozzlePoseSampleCount][];
            for (int n = 0; n < NozzlePoseSampleCount; n++)
            {
                SampleAt(animations, states, n / (float)(NozzlePoseSampleCount - 1));
                poses[n] = CaptureLocalPoses(transforms);
            }
            for (int t = 0; t < transforms.Length; t++)
            {
                bool moves = false, turns = false, grows = false;
                for (int n = 1; n < NozzlePoseSampleCount; n++)
                {
                    moves |= (poses[n][t].pos - poses[0][t].pos).sqrMagnitude > 1e-6f;
                    turns |= Quaternion.Angle(poses[n][t].rot, poses[0][t].rot) > 0.05f;
                    grows |= (poses[n][t].scale - poses[0][t].scale).sqrMagnitude > 1e-6f;
                }
                if (!moves && !turns && !grows) continue;
                var track = new NozzlePoseTrack
                {
                    positions = moves ? new Vector3[NozzlePoseSampleCount] : null,
                    rotations = turns ? new Quaternion[NozzlePoseSampleCount] : null,
                    scales = grows ? new Vector3[NozzlePoseSampleCount] : null,
                };
                for (int n = 0; n < NozzlePoseSampleCount; n++)
                {
                    if (moves) track.positions[n] = poses[n][t].pos;
                    if (turns) track.rotations[n] = poses[n][t].rot;
                    if (grows) track.scales[n] = poses[n][t].scale;
                }
                result.Add(new NozzlePoseSample { transformIndex = t, track = track });
            }
        }

        /// <summary>Binds a clip's moved transforms to the ghost's; false when it moves none of them.</summary>
        private static bool BuildNozzlePoseClip(
            NozzleGlowInfo glow, NozzleGlowPlan plan, NozzleClipSamples samples,
            Transform modelRoot, Dictionary<Transform, Transform> cloneMap,
            ref List<NozzlePoseClip> clips)
        {
            if (samples.poses.Count == 0) return false;
            Transform[] modelTransforms = modelRoot.GetComponentsInChildren<Transform>(true);
            var clip = new NozzlePoseClip
            {
                layer = plan.layer,
                weighted = plan.weighted,
                weightRate = GhostPlaybackLogic.ComputeNozzleGlowRate(plan.responseSpeed * NozzlePoseWeightResponseScale),
            };
            for (int i = 0; i < samples.poses.Count; i++)
            {
                int index = samples.poses[i].transformIndex;
                if (index >= modelTransforms.Length || modelTransforms[index] == modelRoot) continue;
                if (!cloneMap.TryGetValue(modelTransforms[index], out Transform ghost) || ghost == null) continue;
                clip.transforms.Add(ghost);
                clip.tracks.Add(samples.poses[i].track);
            }
            if (clip.transforms.Count == 0) return false;
            glow.pose = clip;
            if (clips == null) clips = new List<NozzlePoseClip>();
            clips.Add(clip);
            return true;
        }

        /// <summary>Stock moves a <c>weightOnOperational</c> weight three times as fast as the time.</summary>
        internal const float NozzlePoseWeightResponseScale = 3f;

        private static int CountNozzlePoseTransforms(List<NozzlePoseClip> clips)
        {
            var seen = new HashSet<Transform>();
            for (int c = 0; clips != null && c < clips.Count; c++)
                seen.UnionWith(clips[c].transforms);
            return seen.Count;
        }

        /// <summary>
        /// A looping clip whose speed follows the engine (the Goliath's fan): its transforms on the
        /// ghost and stock's speed law. The level of the glow info is the speed.
        /// </summary>
        private static bool BuildEngineSpin(
            Part prefab, NozzleGlowPlan plan, Transform ghostModelNode,
            ref HeatGhostInfo heatInfo, uint persistentId)
        {
            var samples = SampleAnimationLoopStates(prefab, plan.animationName, "EngineSpin", out float clipSeconds);
            if (samples == null) return false;
            var transforms = new List<ConverterLoopTransformState>();
            for (int i = 0; i < samples.Count; i++)
            {
                Transform ghost = FindTransformByPath(ghostModelNode, samples[i].path);
                if (ghost == null) continue;
                transforms.Add(new ConverterLoopTransformState { t = ghost, phases = samples[i].phases });
            }
            if (transforms.Count == 0) return false;
            AddNozzleGlow(ref heatInfo, NewEngineSpin(persistentId, plan, clipSeconds, transforms), persistentId);
            return true;
        }

        /// <summary>Pure: the glow info of a looping clip. Stock's per-step factor here is
        /// <c>responseSpeed * fixedDeltaTime</c>, without the clip time's 25.</summary>
        internal static NozzleGlowInfo NewEngineSpin(
            uint persistentId, NozzleGlowPlan plan, float clipSeconds, List<ConverterLoopTransformState> transforms)
        {
            var glow = new NozzleGlowInfo
            {
                partPersistentId = persistentId,
                moduleName = plan.moduleName,
                animationName = plan.animationName,
                moduleKeys = new ulong[plan.moduleIndices.Length],
                modulePowers = new float[plan.moduleIndices.Length],
                powerScale = plan.powerScale,
                approachRate = GhostPlaybackLogic.ComputeNozzleGlowRate(
                    plan.responseSpeed / GhostPlaybackLogic.NozzleGlowResponseScale),
                initialLevel = plan.initialLevel,
                spin = new EngineSpin
                {
                    baseSpeed = plan.spinBaseSpeed,
                    speedMult = plan.spinSpeedMult,
                    clipSeconds = clipSeconds,
                    transforms = transforms,
                },
            };
            for (int k = 0; k < plan.moduleIndices.Length; k++)
                glow.moduleKeys[k] = FlightRecorder.EncodeEngineKey(persistentId, plan.moduleIndices[k]);
            return glow;
        }
    }
}
