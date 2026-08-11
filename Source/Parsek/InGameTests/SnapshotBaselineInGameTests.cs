using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    // The M1 snapshot-baseline category: the ONE thing the xUnit suite structurally
    // cannot reach. `SnapshotBaselineParserTests` proves the ConfigNode parse and
    // `SnapshotBaselineApplicationTests` proves the pure action resolution, but every
    // applier that actually MOVES something is Unity-coupled - and calling
    // GhostPlaybackLogic.SetLightState or a canopy applier from xUnit throws
    // SecurityException ("ECall methods must be packaged into a system module") before
    // it runs a line. So the question "does a snapshot that says EXTENDED actually put
    // the ghost's transforms somewhere other than the prefab's stowed pose" has no
    // headless answer. These cells answer it against a live PartLoader and live
    // transforms.
    //
    // Fixture independence: every cell authors its OWN one-part snapshot ConfigNode
    // over a stock part it discovers through PartLoader, so nothing here depends on
    // the committed recording corpus or on what the batch happens to be flying. A cell
    // that cannot find the part class it needs calls InGameAssert.Skip and NAMES the
    // requirement rather than asserting against an assumed install (Breaking Ground
    // robotics is the one that legitimately goes missing on a stock-minimal profile).
    //
    // FLIGHT-scoped because the ghost builder mirrors prefab transform hierarchies and
    // the batch's own isolation contract is written for a FLIGHT host.
    public class SnapshotBaselineInGameTests
    {
        private readonly InGameTestRunner runner;

        public SnapshotBaselineInGameTests(InGameTestRunner runner)
        {
            this.runner = runner;
        }

        /// <summary>
        /// Maximum candidate parts inspected when looking for one whose ghost build
        /// yields a deployable/robotic surface with separated poses. Bounded so a cell
        /// cannot turn into a whole-catalogue sweep inside one frame.
        /// </summary>
        private const int MaxCandidateParts = 16;

        /// <summary>
        /// Rotation tolerance in degrees for a pose comparison. NOT a world-position
        /// epsilon (those must scale — see InGameFixtureMath.SceneFloatGridToleranceMeters,
        /// used for the positional half below): a local rotation is written into and read
        /// back out of the same float quaternion with no floating-origin round trip, so
        /// the residual is a few ULPs. This sits far above that and far below the
        /// half-degree separation a candidate part must show to qualify at all.
        /// </summary>
        private const float PoseRotationToleranceDegrees = 0.05f;

        /// <summary>Minimum separation between a candidate's stowed and deployed poses.</summary>
        private const float MinCandidatePoseSeparationMeters = 0.01f;
        private const float MinCandidatePoseSeparationDegrees = 0.5f;

        #region Snapshot authoring helpers

        /// <summary>
        /// Authors a one-part VESSEL snapshot node the ghost builder accepts: PART name +
        /// persistentId + placement, plus whatever MODULE nodes the caller wants persisted.
        /// This is the same shape ProtoVessel.Save writes (which is what a real
        /// GhostVisualSnapshot is), narrowed to the keys the builder and the M1 parser read.
        /// </summary>
        private static ConfigNode OnePartSnapshot(
            string partName, uint persistentId, params ConfigNode[] moduleNodes)
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "ParsekTest_SnapshotBaseline");
            vessel.AddValue("persistentId", "999000");
            ConfigNode part = vessel.AddNode("PART");
            part.AddValue("name", partName);
            part.AddValue("persistentId", persistentId.ToString(CultureInfo.InvariantCulture));
            part.AddValue("parent", "0");
            part.AddValue("position", "0,0,0");
            part.AddValue("rotation", "0,0,0,1");
            if (moduleNodes != null)
            {
                for (int i = 0; i < moduleNodes.Length; i++)
                {
                    if (moduleNodes[i] != null)
                        part.AddNode(moduleNodes[i]);
                }
            }
            return vessel;
        }

        /// <summary>Authors one MODULE node from flattened key/value pairs.</summary>
        private static ConfigNode ModuleNode(string moduleName, params string[] keyValuePairs)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", moduleName);
            node.AddValue("isEnabled", "True");
            if (keyValuePairs != null)
            {
                for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
                    node.AddValue(keyValuePairs[i], keyValuePairs[i + 1]);
            }
            return node;
        }

        /// <summary>
        /// Builds a ghost from an authored snapshot and runs the real spawn pipeline over
        /// it (PopulateGhostInfoDictionaries: stow/cold baselines, then the M1 snapshot
        /// baselines). Returns null when the part produced no visuals at all.
        /// </summary>
        private GhostPlaybackState BuildGhostAndPopulate(
            ConfigNode snapshot, string rootName, out GhostBuildResult result)
        {
            result = null;
            PendingGhostVisualBuild build = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                null, snapshot, rootName, HeaviestSpawnBuildType.RecordingStartSnapshot);
            if (build == null)
                return null;

            GameObject cleanupRoot = build.root;
            GhostVisualBuilder.AdvanceTimelineGhostBuild(build, long.MaxValue);
            result = GhostVisualBuilder.CompleteTimelineGhostBuild(build, null);
            if (result == null || result.root == null)
            {
                if (cleanupRoot != null && runner != null)
                    runner.TrackForCleanup(cleanupRoot);
                return null;
            }

            if (runner != null)
                runner.TrackForCleanup(result.root);

            var state = new GhostPlaybackState
            {
                vesselName = "SnapshotBaselineProbe",
                ghost = result.root,
            };
            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);
            return state;
        }

        private static bool PoseIsSeparated(DeployableTransformState ts)
        {
            return (ts.deployedPos - ts.stowedPos).magnitude > MinCandidatePoseSeparationMeters
                || Quaternion.Angle(ts.stowedRot, ts.deployedRot) > MinCandidatePoseSeparationDegrees;
        }

        private static bool HasSeparatedPose(DeployableGhostInfo info)
        {
            if (info == null || info.transforms == null) return false;
            for (int i = 0; i < info.transforms.Count; i++)
            {
                if (info.transforms[i].t != null && PoseIsSeparated(info.transforms[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Finds a stock part whose ghost build yields a deployable surface with a
        /// MEASURABLY separated stowed-vs-deployed pose, using the caller's MODULE nodes
        /// as the snapshot state. Without the separation check a cell could "pass" over a
        /// part whose animation moves nothing, which is the vacuity trap this whole
        /// category exists to avoid.
        /// </summary>
        private bool TryFindDeployableCandidate(
            System.Func<Part, bool> prefabFilter,
            System.Func<string, ConfigNode[]> modulesFor,
            string rootPrefix,
            out GhostPlaybackState state,
            out DeployableGhostInfo info,
            out string partName)
        {
            state = null;
            info = null;
            partName = null;
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            if (loaded == null) return false;

            int inspected = 0;
            for (int i = 0; i < loaded.Count && inspected < MaxCandidateParts; i++)
            {
                AvailablePart ap = loaded[i];
                if (ap == null || ap.partPrefab == null || string.IsNullOrEmpty(ap.name)) continue;
                if (!prefabFilter(ap.partPrefab)) continue;

                inspected++;
                ConfigNode snapshot = OnePartSnapshot(ap.name, 100000, modulesFor(ap.name));
                GhostBuildResult result;
                GhostPlaybackState candidate = BuildGhostAndPopulate(
                    snapshot, $"ParsekTest_{rootPrefix}_{inspected}", out result);
                if (candidate?.deployableInfos == null) continue;

                DeployableGhostInfo candidateInfo;
                if (!candidate.deployableInfos.TryGetValue(100000, out candidateInfo)) continue;
                if (!HasSeparatedPose(candidateInfo)) continue;

                state = candidate;
                info = candidateInfo;
                partName = ap.name;
                return true;
            }

            return false;
        }

        private static void AssertTransformsAt(
            DeployableGhostInfo info, bool deployed, string partName, string what)
        {
            int checkedCount = 0;
            for (int i = 0; i < info.transforms.Count; i++)
            {
                DeployableTransformState ts = info.transforms[i];
                if (ts.t == null) continue;
                Vector3 wantPos = deployed ? ts.deployedPos : ts.stowedPos;
                Quaternion wantRot = deployed ? ts.deployedRot : ts.stowedRot;
                Vector3 gotPos = ts.t.localPosition;

                float maxAbsAxis = Mathf.Max(
                    Mathf.Abs(wantPos.x), Mathf.Max(Mathf.Abs(wantPos.y), Mathf.Abs(wantPos.z)));
                float tolerance = (float)InGameFixtureMath.SceneFloatGridToleranceMeters(maxAbsAxis);
                InGameAssert.ApproxEqual(wantPos, gotPos, tolerance,
                    $"{what}: '{partName}' transform '{ts.t.name}' should sit at its " +
                    $"{(deployed ? "deployed" : "stowed")} local position " +
                    $"{wantPos} but sits at {gotPos} (tolerance {tolerance:F4} m)");

                float angle = Quaternion.Angle(wantRot, ts.t.localRotation);
                InGameAssert.IsLessThan(angle, PoseRotationToleranceDegrees,
                    $"{what}: '{partName}' transform '{ts.t.name}' should carry its " +
                    $"{(deployed ? "deployed" : "stowed")} local rotation, off by " +
                    $"{angle:F4} deg");
                checkedCount++;
            }

            InGameAssert.IsGreaterThan(checkedCount, 0,
                $"{what}: '{partName}' resolved no live deployable transforms to assert on");
        }

        #endregion

        [InGameTest(Category = "SnapshotBaseline", Scene = GameScenes.FLIGHT,
            Description = "M1: a snapshot MODULE saying deployState=EXTENDED puts the ghost's "
                + "deployable transforms at their DEPLOYED pose, not the prefab stow pose")]
        public void DeployStateExtended_PosesTheGhostDeployed()
        {
            GhostPlaybackState state;
            DeployableGhostInfo info;
            string partName;
            bool found = TryFindDeployableCandidate(
                prefab => prefab.FindModuleImplementing<ModuleDeployablePart>() != null,
                partNodeName => new[]
                {
                    ModuleNode("ModuleDeployableSolarPanel", "deployState", "EXTENDED"),
                },
                "DeployExtended", out state, out info, out partName);

            if (!found)
            {
                InGameAssert.Skip(
                    "no stock part with a ModuleDeployablePart built a ghost whose deployable "
                    + "transforms have a measurably separated stowed-vs-deployed pose "
                    + $"(inspected up to {MaxCandidateParts} candidates); a parts install with "
                    + "an animated solar panel / antenna / radiator is required");
                return;
            }

            // The MODULE node name is a stock ModuleDeployablePart subclass, but the parser
            // matches on the persisted deployState KEY, so this holds for a mod subclass too.
            AssertTransformsAt(info, deployed: true, partName, "EXTENDED baseline");
            ParsekLog.Info("TestRunner",
                $"SnapshotBaseline: '{partName}' deployed at spawn from deployState=EXTENDED "
                + $"({info.transforms.Count} transform(s))");
        }

        [InGameTest(Category = "SnapshotBaseline", Scene = GameScenes.FLIGHT,
            Description = "M1 control: deployState=RETRACTED leaves the ghost at the stowed pose, "
                + "so the EXTENDED cell is not passing on a part whose animation moves nothing")]
        public void DeployStateRetracted_LeavesTheGhostStowed()
        {
            GhostPlaybackState state;
            DeployableGhostInfo info;
            string partName;
            bool found = TryFindDeployableCandidate(
                prefab => prefab.FindModuleImplementing<ModuleDeployablePart>() != null,
                partNodeName => new[]
                {
                    ModuleNode("ModuleDeployableSolarPanel", "deployState", "RETRACTED"),
                },
                "DeployRetracted", out state, out info, out partName);

            if (!found)
            {
                InGameAssert.Skip(
                    "no stock part with a ModuleDeployablePart built a ghost with separated "
                    + "deployable poses; same requirement as the EXTENDED cell");
                return;
            }

            AssertTransformsAt(info, deployed: false, partName, "RETRACTED baseline");
        }

        [InGameTest(Category = "SnapshotBaseline", Scene = GameScenes.FLIGHT,
            Description = "M1: a mid-travel deployState (EXTENDING) is NO OPINION - the ghost keeps "
                + "the pre-M1 all-stowed spawn baseline")]
        public void DeployStateMidTravel_KeepsTheStowBaseline()
        {
            GhostPlaybackState state;
            DeployableGhostInfo info;
            string partName;
            bool found = TryFindDeployableCandidate(
                prefab => prefab.FindModuleImplementing<ModuleDeployablePart>() != null,
                partNodeName => new[]
                {
                    ModuleNode("ModuleDeployableSolarPanel", "deployState", "EXTENDING"),
                },
                "DeployMidTravel", out state, out info, out partName);

            if (!found)
            {
                InGameAssert.Skip(
                    "no stock part with a ModuleDeployablePart built a ghost with separated "
                    + "deployable poses; same requirement as the EXTENDED cell");
                return;
            }

            AssertTransformsAt(info, deployed: false, partName, "EXTENDING (no-opinion) baseline");
            // And the parse itself must have declined rather than guessed.
            InGameAssert.IsTrue(
                state.snapshotBaselines == null || !state.snapshotBaselines.ContainsKey(100000),
                $"a mid-travel deployState should produce no baseline entry for '{partName}'");
        }

        [InGameTest(Category = "SnapshotBaseline", Scene = GameScenes.FLIGHT,
            Description = "M1: a snapshot ModuleWheelDeployment saying stateString=Deployed puts the "
                + "ghost's gear transforms at their deployed pose")]
        public void GearStateDeployed_PosesTheGhostDeployed()
        {
            GhostPlaybackState state;
            DeployableGhostInfo info;
            string partName;
            bool found = TryFindDeployableCandidate(
                prefab => prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null,
                partNodeName => new[]
                {
                    ModuleNode("ModuleWheelDeployment", "stateString", "Deployed"),
                },
                "GearDeployed", out state, out info, out partName);

            if (!found)
            {
                InGameAssert.Skip(
                    "no stock part with a ModuleWheelDeployment built a ghost whose gear "
                    + "transforms have a measurably separated stowed-vs-deployed pose "
                    + $"(inspected up to {MaxCandidateParts} candidates); an animated landing "
                    + "gear / leg part is required");
                return;
            }

            AssertTransformsAt(info, deployed: true, partName, "gear Deployed baseline");
        }

        [InGameTest(Category = "SnapshotBaseline", Scene = GameScenes.FLIGHT,
            Description = "M1: a snapshot servo angle poses the ghost's servo transform away from the "
                + "prefab pose, and is recorded as that servo's spawn baseline")]
        public void SnapshotServoAngle_PosesTheGhostServo()
        {
            AvailablePart candidate = null;
            string roboticModuleName = null;
            string poseKey = null;
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count && candidate == null; i++)
                {
                    AvailablePart ap = loaded[i];
                    if (ap?.partPrefab?.Modules == null || string.IsNullOrEmpty(ap.name)) continue;
                    for (int m = 0; m < ap.partPrefab.Modules.Count; m++)
                    {
                        string name = ap.partPrefab.Modules[m]?.moduleName;
                        if (string.IsNullOrEmpty(name)) continue;
                        // POSED families only. The wheel families are robotic-CLASSIFIED (so
                        // they hold the ordinal) but carry no persisted pose; rotors carry no
                        // persisted SPIN key at all, so they are deliberately baseline-less.
                        // Asking the resolver rather than re-deriving the rule keeps this
                        // cell honest if the family set ever moves.
                        if (!GhostVisualBuilder.TryResolveSnapshotRoboticPoseKey(
                                name, out string resolvedKey))
                            continue;
                        candidate = ap;
                        roboticModuleName = name;
                        poseKey = resolvedKey;
                        break;
                    }
                }
            }

            if (candidate == null)
            {
                InGameAssert.Skip(
                    "no part with a POSED robotic module (ModuleRoboticServoHinge / "
                    + "ModuleRoboticRotationServo / ModuleRoboticServoPiston) is installed; "
                    + "the Breaking Ground expansion is required for the end-to-end servo "
                    + "pose path, and rotors do not qualify (no persisted spin key)");
                return;
            }

            // The authored key MUST be the one a real ProtoVessel snapshot carries — i.e. an
            // isPersistant KSPField/KSPAxisField — or this cell would prove the pose path
            // over a key production never writes. That was the original defect: it authored
            // the LIVE probe plan's primary key (currentAngle / currentRPM / currentPosition),
            // none of which persist.
            const float pose = 37.5f;
            ConfigNode snapshot = OnePartSnapshot(
                candidate.name, 100000,
                ModuleNode(roboticModuleName,
                    poseKey, pose.ToString("R", CultureInfo.InvariantCulture)));

            GhostBuildResult result;
            GhostPlaybackState state = BuildGhostAndPopulate(
                snapshot, "ParsekTest_ServoPose", out result);
            if (state?.roboticInfos == null || state.roboticInfos.Count == 0)
            {
                InGameAssert.Skip(
                    $"'{candidate.name}' carries '{roboticModuleName}' but its ghost build "
                    + "resolved no servo transform, so there is nothing to pose");
                return;
            }

            ulong key = FlightRecorder.EncodeEngineKey(100000, 0);
            RoboticGhostInfo info;
            InGameAssert.IsTrue(state.roboticInfos.TryGetValue(key, out info) && info != null,
                $"'{candidate.name}' should expose robotic ordinal 0 for '{roboticModuleName}'");
            InGameAssert.IsTrue(info.hasSnapshotBaseline,
                $"'{candidate.name}' ordinal 0 should have taken the snapshot pose as its baseline");
            InGameAssert.ApproxEqual(pose, info.spawnValue, 0.001f,
                "the servo's spawn baseline should be the snapshot pose");
            InGameAssert.ApproxEqual(pose, info.currentValue, 0.001f,
                "the servo's live value should start at the snapshot pose");

            if (info.servoTransform == null)
            {
                InGameAssert.Skip(
                    $"'{candidate.name}' servo info resolved without a live transform");
                return;
            }

            // The pose is applied as an absolute offset from the build-time stowed pose, so a
            // non-zero angle/extension MUST have moved the transform off it.
            if (info.visualMode == RoboticVisualMode.Linear)
            {
                float moved = (info.servoTransform.localPosition - info.stowedPos).magnitude;
                InGameAssert.IsGreaterThan(moved, MinCandidatePoseSeparationMeters,
                    $"a {pose} m piston baseline should move the servo transform off its stowed "
                    + $"position, moved {moved:F4} m");
            }
            else if (info.visualMode == RoboticVisualMode.Rotational)
            {
                float turned = Quaternion.Angle(info.stowedRot, info.servoTransform.localRotation);
                InGameAssert.IsGreaterThan(turned, MinCandidatePoseSeparationDegrees,
                    $"a {pose} deg servo baseline should rotate the servo transform off its "
                    + $"stowed rotation, turned {turned:F4} deg");
            }
            else
            {
                // Unreachable by construction: the candidate search only accepts modules the
                // resolver gives a pose key for, and the only RotorRpm family has none. If a
                // part ever maps a posed family onto RotorRpm, say so rather than passing.
                InGameAssert.Fail(
                    $"'{candidate.name}' resolved '{roboticModuleName}' to the RPM-driven "
                    + "visual mode, but the snapshot resolver handed it a pose key "
                    + $"('{poseKey}') — the two classifications have drifted apart");
            }

            ParsekLog.Info("TestRunner",
                $"SnapshotBaseline: '{candidate.name}' servo '{roboticModuleName}' posed to "
                + $"{pose} via persisted key '{poseKey}' at spawn (mode={info.visualMode})");
        }

        [InGameTest(Category = "SnapshotBaseline", Scene = GameScenes.FLIGHT,
            Description = "M1/M4 loop restore: RestoreRoboticSpawnBaselines puts a servo transform "
                + "moved during a cycle back to its spawn pose")]
        public void LoopRestore_ReturnsTheServoTransformToItsSpawnPose()
        {
            // ANY robotic-classified module will do here, wheel families included: the cell
            // is about the restore putting the TRANSFORM back, not about where the spawn
            // value came from. That keeps it exercised on a stock-only install, where the
            // end-to-end servo-pose cell above legitimately skips.
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            GhostPlaybackState state = null;
            string partName = null;
            int inspected = 0;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count && state == null && inspected < MaxCandidateParts; i++)
                {
                    AvailablePart ap = loaded[i];
                    if (ap?.partPrefab?.Modules == null || string.IsNullOrEmpty(ap.name)) continue;
                    bool hasRobotic = false;
                    for (int m = 0; m < ap.partPrefab.Modules.Count && !hasRobotic; m++)
                    {
                        string name = ap.partPrefab.Modules[m]?.moduleName;
                        hasRobotic = !string.IsNullOrEmpty(name)
                            && FlightRecorder.IsRoboticModuleName(name);
                    }
                    if (!hasRobotic) continue;

                    inspected++;
                    GhostBuildResult result;
                    GhostPlaybackState candidate = BuildGhostAndPopulate(
                        OnePartSnapshot(ap.name, 100000),
                        $"ParsekTest_LoopRestore_{inspected}", out result);
                    if (candidate?.roboticInfos == null || candidate.roboticInfos.Count == 0)
                        continue;

                    foreach (var kvp in candidate.roboticInfos)
                    {
                        if (kvp.Value?.servoTransform == null) continue;
                        state = candidate;
                        partName = ap.name;
                        break;
                    }
                }
            }

            if (state == null)
            {
                InGameAssert.Skip(
                    "no part with a robotic module (including the wheel suspension / steering / "
                    + "motor families) built a ghost with a live servo transform "
                    + $"(inspected up to {MaxCandidateParts} candidates)");
                return;
            }

            int moved = 0;
            var spawnPositions = new Dictionary<ulong, Vector3>();
            var spawnRotations = new Dictionary<ulong, Quaternion>();
            foreach (var kvp in state.roboticInfos)
            {
                RoboticGhostInfo info = kvp.Value;
                if (info?.servoTransform == null) continue;
                spawnPositions[kvp.Key] = info.servoTransform.localPosition;
                spawnRotations[kvp.Key] = info.servoTransform.localRotation;

                // Simulate a cycle that left the servo somewhere else: the same absolute
                // displacement a RoboticPositionSample would have applied.
                info.servoTransform.localPosition = info.servoTransform.localPosition
                    + new Vector3(0f, 1.25f, 0f);
                info.servoTransform.localRotation = info.servoTransform.localRotation
                    * Quaternion.AngleAxis(43f, Vector3.up);
                info.currentValue = 91f;
                info.active = true;
                info.lastUpdateUT = 1234.0;
                moved++;
            }

            InGameAssert.IsGreaterThan(moved, 0,
                $"'{partName}' exposed no live servo transform to displace");

            int restored = GhostPlaybackLogic.RestoreRoboticSpawnBaselines(state);
            InGameAssert.AreEqual(moved, restored,
                $"every displaced servo on '{partName}' should be restored");

            foreach (var kvp in state.roboticInfos)
            {
                RoboticGhostInfo info = kvp.Value;
                if (info?.servoTransform == null) continue;
                Vector3 wantPos = spawnPositions[kvp.Key];
                float maxAbsAxis = Mathf.Max(
                    Mathf.Abs(wantPos.x), Mathf.Max(Mathf.Abs(wantPos.y), Mathf.Abs(wantPos.z)));
                float tolerance = (float)InGameFixtureMath.SceneFloatGridToleranceMeters(maxAbsAxis);

                if (info.visualMode == RoboticVisualMode.RotorRpm)
                {
                    // A rotor is not posed by the restore (it is RPM-driven), so the contract
                    // is the bookkeeping: back to its spawn rate, parked when that rate did
                    // not come from a snapshot.
                    InGameAssert.ApproxEqual(info.spawnValue, info.currentValue, 0.001f,
                        $"'{partName}' rotor should be back at its spawn rate");
                    InGameAssert.IsFalse(info.active,
                        $"'{partName}' rotor with no snapshot baseline should be parked");
                    continue;
                }

                if (info.visualMode == RoboticVisualMode.Linear)
                {
                    InGameAssert.ApproxEqual(wantPos, info.servoTransform.localPosition, tolerance,
                        $"'{partName}' piston should be back at its spawn position "
                        + $"(tolerance {tolerance:F4} m)");
                }
                else
                {
                    float off = Quaternion.Angle(
                        spawnRotations[kvp.Key], info.servoTransform.localRotation);
                    InGameAssert.IsLessThan(off, PoseRotationToleranceDegrees,
                        $"'{partName}' servo should be back at its spawn rotation, off by "
                        + $"{off:F4} deg");
                }

                InGameAssert.ApproxEqual(info.spawnValue, info.currentValue, 0.001f,
                    $"'{partName}' servo should be back at its spawn value");
                InGameAssert.IsFalse(info.active,
                    $"'{partName}' servo should be parked after the restore");
            }

            ParsekLog.Info("TestRunner",
                $"SnapshotBaseline: loop restore returned {restored} servo(s) on '{partName}' "
                + "to their spawn pose");
        }

        [InGameTest(Category = "SnapshotBaseline", Scene = GameScenes.FLIGHT,
            Description = "M1 inertness: a snapshot PART with no MODULE nodes produces no baseline at "
                + "all, so a legacy recording keeps its pre-M1 all-stowed spawn look")]
        public void SnapshotWithoutModules_ProducesNoBaseline()
        {
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            GhostPlaybackState state = null;
            string partName = null;
            int inspected = 0;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count && state == null && inspected < MaxCandidateParts; i++)
                {
                    AvailablePart ap = loaded[i];
                    if (ap?.partPrefab == null || string.IsNullOrEmpty(ap.name)) continue;
                    inspected++;
                    GhostBuildResult result;
                    GhostPlaybackState candidate = BuildGhostAndPopulate(
                        OnePartSnapshot(ap.name, 100000),
                        $"ParsekTest_NoModules_{inspected}", out result);
                    if (candidate == null) continue;
                    state = candidate;
                    partName = ap.name;
                }
            }

            if (state == null)
            {
                InGameAssert.Skip(
                    $"no stock part among the first {MaxCandidateParts} inspected built any ghost "
                    + "visual, so there is nothing to assert inertness over");
                return;
            }

            InGameAssert.IsNull(state.snapshotBaselines,
                $"a MODULE-less snapshot PART must yield no baseline dictionary at all "
                + $"(part '{partName}')");

            if (state.deployableInfos != null
                && state.deployableInfos.TryGetValue(100000, out DeployableGhostInfo info)
                && HasSeparatedPose(info))
            {
                // Bonus discriminator when the part happens to be animated: with no
                // baseline the pre-M1 stow baseline is what must stand.
                AssertTransformsAt(info, deployed: false, partName, "no-baseline spawn");
            }
        }
    }
}
