using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    // Build-time inputs for parts that ride a Breaking Ground servo's moving half: the
    // servo configs by PART index, each servo's snapshot geometry pose, and the plan.
    internal static partial class GhostVisualBuilder
    {
        private static void CollectServoFollowerInputs(
            PendingGhostVisualBuild build, int partIndex, ConfigNode partNode,
            List<RoboticGhostInfo> partRoboticInfos)
        {
            if (build == null || partRoboticInfos == null) return;
            for (int i = 0; i < partRoboticInfos.Count; i++)
            {
                RoboticGhostInfo info = partRoboticInfos[i];
                if (info?.servoAttachConfig == null) continue;
                if (!build.servoAttachConfigsByPartIndex.ContainsKey(partIndex))
                    build.servoAttachConfigsByPartIndex[partIndex] = info.servoAttachConfig;
                if (TryReadSnapshotServoGeometryPose(partNode, info.moduleIndex, info.moduleName,
                        out Vector3 pos, out Quaternion rot))
                {
                    info.hasSnapshotGeometryPose = true;
                    info.snapshotGeometryPos = pos;
                    info.snapshotGeometryRot = rot;
                }
            }
        }

        /// <summary>
        /// Reads the servo's persisted moving-transform pose (<c>servoTransformPosition</c> /
        /// <c>servoTransformRotation</c>, both <c>[KSPField(isPersistant = true)]</c> on
        /// <c>BaseServo</c>) from the MODULE node at robotic ordinal
        /// <paramref name="roboticOrdinal"/>. The module name must match, so a mod-set drift in
        /// the ordinal reads nothing rather than another module's pose. A missing,
        /// unparseable, non-finite or non-unit pose returns false.
        /// </summary>
        internal static bool TryReadSnapshotServoGeometryPose(
            ConfigNode partNode, int roboticOrdinal, string moduleName,
            out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (partNode == null || roboticOrdinal < 0) return false;
            ConfigNode[] modules = partNode.GetNodes("MODULE");
            if (modules == null) return false;

            int ordinal = 0;
            for (int i = 0; i < modules.Length; i++)
            {
                string name = modules[i]?.GetValue("name");
                if (!FlightRecorder.IsRoboticModuleName(name)) continue;
                if (ordinal++ != roboticOrdinal) continue;
                if (!string.Equals(name, moduleName, StringComparison.Ordinal)) return false;

                if (!TryParseVector3(modules[i].GetValue("servoTransformPosition"), out Vector3 p)
                    || !TryParseQuaternion(modules[i].GetValue("servoTransformRotation"), out Quaternion q))
                    return false;
                if (!IsFinite(p.x) || !IsFinite(p.y) || !IsFinite(p.z)) return false;
                float norm2 = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (!IsFinite(norm2) || norm2 < 0.98f || norm2 > 1.02f) return false;
                pos = p;
                rot = q;
                return true;
            }
            return false;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static ServoFollowerPlan ResolveServoFollowerPlan(
            PendingGhostVisualBuild build, IPlaybackTrajectory rec)
        {
            if (build == null || build.servoAttachConfigsByPartIndex.Count == 0)
                return null;

            ServoFollowerPlan plan = ServoFollowerPlanner.Resolve(
                build.partNodes, build.servoAttachConfigsByPartIndex);
            ParsekLog.VerboseRateLimited("GhostVisual", $"servo_plan_{build.rootName}",
                $"Servo follower plan for '{rec?.VesselName ?? build.rootName}': " +
                $"servos={build.servoAttachConfigsByPartIndex.Count} followers={plan.links.Count} " +
                $"invertedServos={plan.invertedServoPids.Count}" +
                (plan.invertedServoPids.Count > 0
                    ? $" (parent on the moving side, base swing not modelled: pids={string.Join(",", plan.invertedServoPids)})"
                    : ""), 60.0);
            return plan.links.Count > 0 ? plan : null;
        }
    }
}
