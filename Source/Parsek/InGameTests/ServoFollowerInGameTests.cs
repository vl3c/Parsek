using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Parts bolted to a Breaking Ground servo's moving half must move with it on the ghost.
    // The xUnit suite proves the pure halves (ServoFollowerPlannerTests: which parts follow;
    // ServoFollowerPoseTests: the pose math); this cell drives the real ghost build and the
    // real robotic event applier over live transforms, which no headless test can reach.
    //
    // Fixture independence: the cell authors its own three-part snapshot over a stock
    // servo part found through PartLoader, and skips (naming the requirement) when the
    // Breaking Ground expansion is not installed.
    public class ServoFollowerInGameTests
    {
        private readonly InGameTestRunner runner;

        public ServoFollowerInGameTests(InGameTestRunner runner)
        {
            this.runner = runner;
        }

        private const uint ServoPid = 100000;
        private const uint MovingSidePid = 101111;
        private const uint BaseSidePid = 102222;
        private const float PositionToleranceMeters = 0.001f;
        private const float RotationToleranceDegrees = 0.05f;
        private const float MinMovedDegrees = 0.5f;
        private const float MinMovedMeters = 0.01f;

        private static ConfigNode PartNode(ConfigNode vessel, string partName, uint pid,
            string position, params string[] keyValuePairs)
        {
            ConfigNode part = vessel.AddNode("PART");
            part.AddValue("name", partName);
            part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            part.AddValue("parent", "0");
            part.AddValue("position", position);
            part.AddValue("rotation", "0,0,0,1");
            for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
                part.AddValue(keyValuePairs[i], keyValuePairs[i + 1]);
            return part;
        }

        [InGameTest(Category = "ServoFollowers", Scene = GameScenes.FLIGHT,
            Description = "A part on a servo's moving side follows the servo transform on the ghost; "
                + "a part on its base side does not, and the follower returns to its snapshot pose")]
        public void MovingSidePartFollowsTheServo()
        {
            AvailablePart candidate = null;
            string moduleName = null;
            string movingNode = null;
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count && candidate == null; i++)
                {
                    AvailablePart ap = loaded[i];
                    if (ap?.partPrefab?.Modules == null || string.IsNullOrEmpty(ap.name)) continue;
                    for (int m = 0; m < ap.partPrefab.Modules.Count; m++)
                    {
                        PartModule module = ap.partPrefab.Modules[m];
                        string name = module?.moduleName;
                        // Posed families only: a rotor turns by RPM, not by a recorded pose.
                        if (!ServoFollowerPlanner.IsBreakingGroundServoModuleName(name)
                            || name == "ModuleRoboticServoRotor")
                            continue;
                        string nodes = module.Fields["servoAttachNodes"]?.GetValue(module) as string;
                        ServoAttachConfig config = ServoAttachConfig.FromFieldStrings(nodes, null);
                        if (config.movingNodeIds.Count == 0) continue;
                        foreach (string id in config.movingNodeIds) { movingNode = id; break; }
                        candidate = ap;
                        moduleName = name;
                        break;
                    }
                }
            }

            if (candidate == null)
            {
                InGameAssert.Skip("no posed Breaking Ground servo (hinge / rotation servo / piston) "
                    + "with a moving attach node is installed; the Breaking Ground expansion is required");
                return;
            }

            // Part 0: the servo (root), holding part 1 on its moving node. Part 2 hangs off the
            // servo by a surface attach on a mesh that is not on the moving list (base side).
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "ParsekTest_ServoFollowers");
            vessel.AddValue("persistentId", "999001");
            PartNode(vessel, candidate.name, ServoPid, "0,0,0",
                "attN", movingNode + ", 1");
            PartNode(vessel, candidate.name, MovingSidePid, "0,1,0",
                "attN", "parsekTestNode, 0");
            PartNode(vessel, candidate.name, BaseSidePid, "0,-1,0",
                "srfN", "srfAttach, 0, parsekBaseSideMesh");

            PendingGhostVisualBuild build = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                null, vessel, "ParsekTest_ServoFollowers", HeaviestSpawnBuildType.RecordingStartSnapshot);
            InGameAssert.IsNotNull(build, $"'{candidate.name}' snapshot should start a ghost build");
            GameObject cleanupRoot = build.root;
            GhostVisualBuilder.AdvanceTimelineGhostBuild(build, long.MaxValue);
            GhostBuildResult result = GhostVisualBuilder.CompleteTimelineGhostBuild(build, null);
            if (result == null || result.root == null)
            {
                if (cleanupRoot != null && runner != null) runner.TrackForCleanup(cleanupRoot);
                InGameAssert.Skip($"'{candidate.name}' produced no ghost visuals");
                return;
            }
            if (runner != null) runner.TrackForCleanup(result.root);

            var state = new GhostPlaybackState { vesselName = "ServoFollowerProbe", ghost = result.root };
            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            ulong key = FlightRecorder.EncodeEngineKey(ServoPid, 0);
            RoboticGhostInfo info = null;
            InGameAssert.IsTrue(state.roboticInfos != null
                && state.roboticInfos.TryGetValue(key, out info) && info?.servoTransform != null,
                $"'{candidate.name}' should expose a servo transform for '{moduleName}'");
            InGameAssert.IsTrue(info.followers != null && info.followers.Count == 1
                && info.followers[0].partPersistentId == MovingSidePid,
                $"exactly the moving-side part should follow the servo, got "
                + $"{(info.followers == null ? 0 : info.followers.Count)} follower(s)");

            Transform moving = GhostVisualBuilder.FindGhostPartTransform(state.ghost, MovingSidePid);
            Transform baseSide = GhostVisualBuilder.FindGhostPartTransform(state.ghost, BaseSidePid);
            InGameAssert.IsNotNull(moving, "moving-side ghost part should exist");
            InGameAssert.IsNotNull(baseSide, "base-side ghost part should exist");
            Vector3 movingRestPos = moving.localPosition;
            Quaternion movingRestRot = moving.localRotation;
            Vector3 baseRestPos = baseSide.localPosition;
            Quaternion baseRestRot = baseSide.localRotation;

            float target = info.visualMode == RoboticVisualMode.Linear ? 0.5f : 45f;
            GhostPlaybackLogic.ApplyRoboticEvent(state, new PartEvent
            {
                ut = 1.0,
                partPersistentId = ServoPid,
                moduleIndex = 0,
                eventType = PartEventType.RoboticPositionSample,
                value = target,
            }, 1.0);

            float turned = Quaternion.Angle(movingRestRot, moving.localRotation);
            float shifted = (moving.localPosition - movingRestPos).magnitude;
            InGameAssert.IsTrue(turned > MinMovedDegrees || shifted > MinMovedMeters,
                $"the moving-side part should follow a {target} servo move "
                + $"(turned {turned:F3} deg, shifted {shifted:F4} m)");
            InGameAssert.IsLessThan((baseSide.localPosition - baseRestPos).magnitude, PositionToleranceMeters,
                "the base-side part must not move with the servo");
            InGameAssert.IsLessThan(Quaternion.Angle(baseRestRot, baseSide.localRotation), RotationToleranceDegrees,
                "the base-side part must not turn with the servo");

            GhostPlaybackLogic.ApplyRoboticEvent(state, new PartEvent
            {
                ut = 2.0,
                partPersistentId = ServoPid,
                moduleIndex = 0,
                eventType = PartEventType.RoboticMotionStopped,
                value = 0f,
            }, 2.0);

            InGameAssert.IsLessThan((moving.localPosition - movingRestPos).magnitude, PositionToleranceMeters,
                "back at its spawn pose, the servo should return the follower to its snapshot position");
            InGameAssert.IsLessThan(Quaternion.Angle(movingRestRot, moving.localRotation), RotationToleranceDegrees,
                "back at its spawn pose, the servo should return the follower to its snapshot rotation");

            ParsekLog.Info("TestRunner",
                $"ServoFollowers: '{candidate.name}' ({moduleName}, moving node '{movingNode}') carried its "
                + $"moving-side part through {target} (turned {turned:F2} deg, shifted {shifted:F3} m); "
                + "base side held");
        }
    }
}
