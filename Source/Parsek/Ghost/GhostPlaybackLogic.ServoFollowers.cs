using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Parts riding a Breaking Ground servo's moving half. Every ghost part stays a direct
    // child of the part container (pid lookups, visibility walks, the camera pivot and the
    // loop rewind all assume that), so a follower is not re-parented: its local pose is
    // recomputed from the servo transform's chain each time the servo transform is written
    // (an event, a spawn / loop baseline, a rotor step). Cost is per servo write, O(followers),
    // with no allocation, and zero for a ghost whose servos do not move or that has no servo.
    internal static partial class GhostPlaybackLogic
    {
        private const int MaxServoFollowerNesting = 32;

        /// <summary>
        /// Binds the plan's followers to their servos. Runs once per build, AFTER the
        /// snapshot baselines have posed the servos: each servo is put at the pose the
        /// snapshot was saved at (the frame the followers' snapshot poses are in), every
        /// follower's pose is captured in its servo's frame, the servos go back to their
        /// baseline pose, and the followers are then re-posed to match it.
        /// Returns the number of followers bound.
        /// </summary>
        internal static int BindServoFollowers(GhostPlaybackState state, ServoFollowerPlan plan)
        {
            // Pure early-out: a ghost with no servo plan (every ghost without Breaking Ground
            // robotics, and every headless test) never reaches the Unity-touching core.
            if (plan == null || plan.links.Count == 0 || state == null
                || state.roboticInfos == null || state.roboticInfos.Count == 0)
                return 0;
            return BindServoFollowersCore(state, plan);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int BindServoFollowersCore(GhostPlaybackState state, ServoFollowerPlan plan)
        {
            if (state.ghost == null) return 0;

            Transform container = GhostVisualBuilder.GetGhostPartContainer(state.ghost.transform);
            if (container == null) return 0;

            var servoByPid = new Dictionary<uint, RoboticGhostInfo>();
            foreach (var kvp in state.roboticInfos)
            {
                RoboticGhostInfo info = kvp.Value;
                if (info?.servoTransform == null || info.servoAttachConfig == null) continue;
                // A re-populate of the same infos must not stack a second follower set.
                info.followers = null;
                info.nestedServos = null;
                if (!servoByPid.ContainsKey(info.partPersistentId))
                    servoByPid[info.partPersistentId] = info;
            }
            if (servoByPid.Count == 0) return 0;

            var savedPos = new Dictionary<RoboticGhostInfo, Vector3>();
            var savedRot = new Dictionary<RoboticGhostInfo, Quaternion>();
            int geometryPosed = 0;
            foreach (RoboticGhostInfo servo in servoByPid.Values)
            {
                savedPos[servo] = servo.servoTransform.localPosition;
                savedRot[servo] = servo.servoTransform.localRotation;
                if (servo.hasSnapshotGeometryPose)
                {
                    servo.servoTransform.localPosition = servo.snapshotGeometryPos;
                    servo.servoTransform.localRotation = servo.snapshotGeometryRot;
                    geometryPosed++;
                }
            }

            int bound = 0;
            int missingServo = 0;
            int missingPart = 0;
            int notUnderContainer = 0;
            var followerByPid = new Dictionary<uint, ServoFollower>();
            for (int i = 0; i < plan.links.Count; i++)
            {
                ServoFollowerLink link = plan.links[i];
                if (!servoByPid.TryGetValue(link.servoPid, out RoboticGhostInfo servo))
                {
                    missingServo++;
                    continue;
                }
                Transform part = GhostVisualBuilder.FindGhostPartTransform(state.ghost, link.followerPid);
                if (part == null || part.parent != container)
                {
                    missingPart++;
                    continue;
                }
                if (!TryComposeChainPose(servo.servoTransform, container,
                        out Vector3 chainPos, out Quaternion chainRot, out float chainScale))
                {
                    notUnderContainer++;
                    continue;
                }

                ComputeServoFollowerRelativePose(chainPos, chainRot, chainScale,
                    part.localPosition, part.localRotation,
                    out Vector3 relPos, out Quaternion relRot);
                var follower = new ServoFollower
                {
                    partPersistentId = link.followerPid,
                    part = part,
                    relPos = relPos,
                    relRot = relRot,
                };
                if (servo.followers == null) servo.followers = new List<ServoFollower>();
                servo.followers.Add(follower);
                followerByPid[link.followerPid] = follower;
                bound++;
            }

            foreach (RoboticGhostInfo servo in servoByPid.Values)
            {
                servo.servoTransform.localPosition = savedPos[servo];
                servo.servoTransform.localRotation = savedRot[servo];
            }

            // A servo whose part rides another servo re-poses its own followers after the
            // carrier's followers (its own part among them) have moved.
            var carrierByFollowerPid = new Dictionary<uint, RoboticGhostInfo>();
            for (int i = 0; i < plan.links.Count; i++)
                carrierByFollowerPid[plan.links[i].followerPid] = servoByPid.TryGetValue(
                    plan.links[i].servoPid, out RoboticGhostInfo c) ? c : null;
            int nested = 0;
            foreach (var kvp in servoByPid)
            {
                if (kvp.Value.followers == null || !followerByPid.ContainsKey(kvp.Key)) continue;
                if (carrierByFollowerPid.TryGetValue(kvp.Key, out RoboticGhostInfo carrier)
                    && carrier != null && carrier != kvp.Value)
                {
                    if (carrier.nestedServos == null) carrier.nestedServos = new List<RoboticGhostInfo>();
                    carrier.nestedServos.Add(kvp.Value);
                    nested++;
                }
            }

            // The baseline re-pose below is not playback: mark every servo as already logged so
            // the first-playback-re-pose line cannot fire inside the bind, then re-arm it after.
            foreach (RoboticGhostInfo servo in servoByPid.Values)
                servo.followerReposeLogged = true;
            int carryingServos = 0;
            foreach (var kvp in servoByPid)
            {
                if (kvp.Value.followers == null) continue;
                carryingServos++;
                if (!followerByPid.ContainsKey(kvp.Key))
                    PropagateServoFollowers(kvp.Value);
            }
            // Re-arm: the next re-pose comes from playback.
            foreach (RoboticGhostInfo servo in servoByPid.Values)
                servo.followerReposeLogged = false;

            ParsekLog.VerboseRateLimited("GhostVisual", $"servo_followers_{state.ghost.name}",
                $"Servo followers bound: followers={bound} servos={servoByPid.Count} " +
                $"carryingServos={carryingServos} nestedServos={nested} geometryPosed={geometryPosed} " +
                $"missingServo={missingServo} missingPart={missingPart} " +
                $"notUnderContainer={notUnderContainer} (vessel='{state.vesselName ?? "unknown"}')", 60.0);
            return bound;
        }

        /// <summary>
        /// Re-poses every part riding <paramref name="servo"/> from the servo transform's
        /// current pose, then cascades into servos carried by those parts.
        /// </summary>
        internal static void PropagateServoFollowers(RoboticGhostInfo servo)
        {
            PropagateServoFollowers(servo, 0);
        }

        private static void PropagateServoFollowers(RoboticGhostInfo servo, int depth)
        {
            if (servo?.followers == null || servo.servoTransform == null)
                return;
            if (depth > MaxServoFollowerNesting)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", "servo_followers_depth",
                    $"Servo follower cascade stopped at nesting depth {depth} (pid={servo.partPersistentId})");
                return;
            }
            List<ServoFollower> followers = servo.followers;
            Transform container = followers.Count > 0 && followers[0].part != null
                ? followers[0].part.parent
                : null;
            if (container == null
                || !TryComposeChainPose(servo.servoTransform, container,
                    out Vector3 chainPos, out Quaternion chainRot, out float chainScale))
            {
                ParsekLog.VerboseRateLimited("GhostVisual", "servo_followers_chain_unresolved",
                    $"Servo follower re-pose skipped: servo chain does not resolve under the part " +
                    $"container (pid={servo.partPersistentId})");
                return;
            }

            for (int i = 0; i < followers.Count; i++)
            {
                ServoFollower f = followers[i];
                if (f?.part == null) continue;
                ComputeServoFollowerPose(chainPos, chainRot, chainScale, f.relPos, f.relRot,
                    out Vector3 pos, out Quaternion rot);
                f.part.localPosition = pos;
                f.part.localRotation = rot;
            }

            if (ConsumeFirstFollowerRepose(servo))
                ParsekLog.Verbose("GhostVisual", FormatFollowerReposeLine(
                    servo.partPersistentId, servo.moduleName, followers.Count, servo.currentValue));

            List<RoboticGhostInfo> nested = servo.nestedServos;
            if (nested != null)
            {
                for (int n = 0; n < nested.Count; n++)
                    PropagateServoFollowers(nested[n], depth + 1);
            }
        }

        /// <summary>
        /// True exactly once per bind: the first time playback re-poses
        /// <paramref name="servo"/>'s followers. One line per servo per ghost build, so the
        /// per-write propagation stays silent and allocation-free after it.
        /// </summary>
        internal static bool ConsumeFirstFollowerRepose(RoboticGhostInfo servo)
        {
            if (servo == null || servo.followerReposeLogged) return false;
            servo.followerReposeLogged = true;
            return true;
        }

        /// <summary>The first-playback-re-pose line (invariant culture).</summary>
        internal static string FormatFollowerReposeLine(uint servoPid, string moduleName,
            int followers, float servoValue)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Servo followers re-posed in playback: servoPid={0} module={1} followers={2} " +
                "value={3:F2} (first since bind)",
                servoPid, moduleName ?? "unknown", followers, servoValue);
        }

        /// <summary>
        /// The follower's pose in the servo transform's frame, given the servo transform's
        /// pose in the part container (<paramref name="chainPos"/>, <paramref name="chainRot"/>,
        /// uniform <paramref name="chainScale"/>) and the follower's pose in the same container.
        /// Pure: the inverse of a unit quaternion is its conjugate, so no Unity ECall.
        /// </summary>
        internal static void ComputeServoFollowerRelativePose(
            Vector3 chainPos, Quaternion chainRot, float chainScale,
            Vector3 partPos, Quaternion partRot,
            out Vector3 relPos, out Quaternion relRot)
        {
            Quaternion inverse = UnitQuaternionInverse(chainRot);
            float scale = chainScale > 1e-6f ? chainScale : 1f;
            relPos = (inverse * (partPos - chainPos)) / scale;
            relRot = inverse * partRot;
        }

        /// <summary>
        /// Inverse of <see cref="ComputeServoFollowerRelativePose"/>: the follower's pose in the
        /// part container for the servo transform's CURRENT container pose.
        /// </summary>
        internal static void ComputeServoFollowerPose(
            Vector3 chainPos, Quaternion chainRot, float chainScale,
            Vector3 relPos, Quaternion relRot,
            out Vector3 pos, out Quaternion rot)
        {
            pos = chainPos + chainRot * (relPos * chainScale);
            rot = chainRot * relRot;
        }

        /// <summary>
        /// One step of composing a child frame onto its parent's: given the accumulated pose of
        /// a node in frame F (<paramref name="pos"/>, <paramref name="rot"/>,
        /// <paramref name="scale"/>), returns its pose in F's parent, where F has the local
        /// pose (<paramref name="localPos"/>, <paramref name="localRot"/>, uniform
        /// <paramref name="localScale"/>). Pure.
        /// </summary>
        internal static void ComposeServoChainStep(
            ref Vector3 pos, ref Quaternion rot, ref float scale,
            Vector3 localPos, Quaternion localRot, float localScale)
        {
            pos = localPos + localRot * (pos * localScale);
            rot = localRot * rot;
            scale *= localScale;
        }

        internal static Quaternion UnitQuaternionInverse(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        /// <summary>
        /// The pose of <paramref name="node"/>'s frame in <paramref name="ancestor"/>'s frame,
        /// composed from local transforms only (no world round trip, so a ghost far from the
        /// floating origin loses no precision). Scale is taken as uniform (KSP part and model
        /// scales are). False when <paramref name="ancestor"/> is not an ancestor or the chain
        /// collapses to zero scale.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryComposeChainPose(Transform node, Transform ancestor,
            out Vector3 pos, out Quaternion rot, out float scale)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            scale = 1f;
            Transform t = node;
            int guard = 0;
            while (t != null && t != ancestor && guard++ < 256)
            {
                Vector3 s = t.localScale;
                ComposeServoChainStep(ref pos, ref rot, ref scale,
                    t.localPosition, t.localRotation, (s.x + s.y + s.z) / 3f);
                t = t.parent;
            }
            return t == ancestor && t != null && scale > 1e-6f;
        }
    }
}
