using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Ghost GEOMETRY under ReStock's models (the FX side is ReStockCompatTests). Two shapes
    /// the stock models never exercise:
    ///   - ReStock's LV-T45 (liquidEngine2) default variant enables meshes under sections it
    ///     disables; a ghost that let the nearest rule win drew a boattail frame and the
    ///     compact shroud on every booster.
    ///   - ReStock's Mk16-XL (parachuteLarge) canopy is a bone rig; a ghost that posed only the
    ///     canopy root showed the deployed chute folded and small.
    /// Both skip without ReStock (the modded-compat lane MC-2 runs them).
    /// </summary>
    public class ReStockGhostVisualTests
    {
        private const uint FixturePartPid = 100000u;

        private static bool IsReStockInstalled()
        {
            try
            {
                return System.IO.Directory.Exists(System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? string.Empty, "GameData/ReStock/Patches"));
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static Recording BuildSinglePartFixture(string partName, string idPrefix, double t0)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", idPrefix);
            ConfigNode partNode = snapshot.AddNode("PART");
            partNode.AddValue("name", partName);
            partNode.AddValue("persistentId", FixturePartPid.ToString());

            return new Recording
            {
                RecordingId = idPrefix + "-" + System.Guid.NewGuid().ToString("N").Substring(0, 8),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                VesselName = idPrefix,
                VesselPersistentId = FixturePartPid,
                ExplicitStartUT = t0,
                ExplicitEndUT = t0 + 100.0,
                GhostVisualSnapshot = snapshot.CreateCopy(),
                VesselSnapshot = snapshot.CreateCopy()
            };
        }

        // ReStock reuses bone names under different parents, so bones are keyed by their path
        // below the canopy.
        private static string PathBelow(Transform t, Transform root)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null && p != root; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }

        private static int CountMeshRenderersNamed(GameObject root, string name)
        {
            int count = 0;
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].gameObject.name == name)
                    count++;
            }
            return count;
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock LV-T45 ghost draws no mesh its default variant hides under a disabled section")]
        public void LvT45GhostHidesMeshesUnderDisabledVariantSections()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed");
                return;
            }
            AvailablePart ap = PartLoader.getPartInfoByName("liquidEngine2");
            Part prefab = ap?.partPrefab;
            if (prefab == null || prefab.FindModelTransform("LV_T45_Boattail") == null
                || prefab.FindModelTransform("Frame_Boat") == null)
            {
                InGameAssert.Skip("liquidEngine2 is not the ReStock swivel model " +
                    "(no LV_T45_Boattail / Frame_Boat transforms)");
                return;
            }

            Recording rec = BuildSinglePartFixture("liquidEngine2", "parsektest-restock-lvt45",
                Planetarium.GetUniversalTime());
            GhostBuildResult build = null;
            try
            {
                build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(rec, "ParsekTest_ReStockLvT45");
                InGameAssert.IsNotNull(build?.root, "no ghost built for liquidEngine2");

                // Contrast: the default (Size1) variant's own frame IS drawn, so a ghost that
                // dropped every mesh cannot pass.
                int frame = CountMeshRenderersNamed(build.root, "Frame");
                int frameBoat = CountMeshRenderersNamed(build.root, "Frame_Boat");
                int compactShroud = CountMeshRenderersNamed(build.root, "ShroudT45_CompactWhite");
                ParsekLog.Info("TestRunner",
                    $"ReStock LV-T45 ghost: Frame={frame} Frame_Boat={frameBoat} " +
                    $"ShroudT45_CompactWhite={compactShroud}");
                InGameAssert.IsTrue(frame > 0,
                    "the Size1 variant's Frame mesh (under the enabled LV_T45_Basic) must be on the ghost");
                InGameAssert.AreEqual(0, frameBoat,
                    "Frame_Boat is enabled by Size1 but sits under the disabled LV_T45_Boattail, " +
                    "so the live part hides it; the ghost must not draw it (booster skirt)");
                InGameAssert.AreEqual(0, compactShroud,
                    "ShroudT45_CompactWhite is enabled by Size1 but sits under the disabled " +
                    "ShroudT45_Compact / LV_T45_Compact; the ghost must not draw it");
            }
            finally
            {
                if (build != null && build.root != null)
                    Object.Destroy(build.root);
            }
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock Mk16-XL ghost poses every canopy bone at the end of the deploy animation")]
        public void Mk16XlGhostCanopyBonesReachTheDeployedPose()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed");
                return;
            }
            AvailablePart ap = PartLoader.getPartInfoByName("parachuteLarge");
            Part prefab = ap?.partPrefab;
            ModuleParachute chute = prefab != null ? prefab.FindModuleImplementing<ModuleParachute>() : null;
            if (chute == null || chute.canopyName != "B_ParachuteLargeRotator")
            {
                InGameAssert.Skip("parachuteLarge is not the ReStock rigged canopy " +
                    "(canopyName=" + (chute != null ? chute.canopyName : "<no chute>") + ")");
                return;
            }

            // Independent oracle: the prefab model with the deploy clip sampled at its end, the
            // way the live part ends up. Each animated bone's position in canopy-local space.
            var expected = new Dictionary<string, Vector3>();
            Transform prefabModel = prefab.transform.Find("model") ?? prefab.transform;
            GameObject oracle = Object.Instantiate(prefabModel.gameObject);
            try
            {
                Animation anim = oracle.GetComponentInChildren<Animation>(true);
                InGameAssert.IsNotNull(anim, "parachuteLarge model has no Animation");
                Transform canopy = GhostVisualBuilder.FindTransformRecursive(oracle.transform, chute.canopyName);
                InGameAssert.IsNotNull(canopy, "canopy transform missing on the oracle clone");
                var rest = new Dictionary<string, Vector3>();
                foreach (Transform t in canopy.GetComponentsInChildren<Transform>(true))
                    if (t != canopy) rest[PathBelow(t, canopy)] = canopy.InverseTransformPoint(t.position);

                foreach (string clip in new[] { chute.semiDeployedAnimation, chute.fullyDeployedAnimation })
                {
                    AnimationState st = string.IsNullOrEmpty(clip) ? null : anim[clip];
                    if (st == null) continue;
                    anim.Stop();
                    st.enabled = true;
                    st.speed = 0f;
                    st.normalizedTime = 1f;
                    st.weight = 1f;
                    anim.Play(clip);
                    anim.Sample();
                }
                foreach (Transform t in canopy.GetComponentsInChildren<Transform>(true))
                {
                    if (t == canopy) continue;
                    string key = PathBelow(t, canopy);
                    if (!rest.TryGetValue(key, out Vector3 r)) continue;
                    Vector3 p = canopy.InverseTransformPoint(t.position);
                    if ((p - r).magnitude > 0.01f)
                        expected[key] = p;
                }
            }
            finally
            {
                Object.DestroyImmediate(oracle);
            }
            InGameAssert.IsTrue(expected.Count > 0,
                "the oracle found no canopy bone the deploy clips move; the rig premise is wrong");

            double t0 = Planetarium.GetUniversalTime();
            Recording rec = BuildSinglePartFixture("parachuteLarge", "parsektest-restock-mk16xl", t0);
            rec.PartEvents.Add(new PartEvent
            {
                ut = t0 + 1.0, partPersistentId = FixturePartPid,
                eventType = PartEventType.ParachuteDeployed, partName = "parachuteLarge"
            });

            GhostBuildResult build = null;
            try
            {
                build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(rec, "ParsekTest_ReStockMk16Xl");
                InGameAssert.IsNotNull(build?.root, "no ghost built for parachuteLarge");
                var state = new GhostPlaybackState { ghost = build.root, vesselName = rec.VesselName };
                GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, build);
                ParachuteGhostInfo info = null;
                InGameAssert.IsTrue(state.parachuteInfos != null
                    && state.parachuteInfos.TryGetValue(FixturePartPid, out info)
                    && info != null && info.canopyTransform != null,
                    "the ghost carries no ParachuteGhostInfo with a canopy");
                InGameAssert.IsTrue(info.canopyBones != null && info.canopyBones.Count > 0,
                    "the rigged canopy must carry its animated bones");

                GhostPlaybackLogic.ApplyPartEvents(-1, rec, t0 + 2.0, state);

                Transform ghostCanopy = info.canopyTransform;
                var onGhost = new Dictionary<string, Transform>();
                foreach (Transform t in ghostCanopy.GetComponentsInChildren<Transform>(true))
                    if (t != ghostCanopy) onGhost[PathBelow(t, ghostCanopy)] = t;
                int checkedBones = 0, missing = 0;
                float worst = 0f;
                foreach (var kv in expected)
                {
                    if (!onGhost.TryGetValue(kv.Key, out Transform t))
                    {
                        missing++;
                        continue;
                    }
                    float err = (ghostCanopy.InverseTransformPoint(t.position) - kv.Value).magnitude;
                    if (err > worst) worst = err;
                    checkedBones++;
                }
                ParsekLog.Info("TestRunner",
                    $"ReStock Mk16-XL ghost canopy: animatedBones={expected.Count} onGhost={checkedBones} " +
                    $"missing={missing} ghostRig={info.canopyBones.Count} worstErr={worst:F4}");
                InGameAssert.IsTrue(checkedBones > 0, "no animated canopy bone exists on the ghost");
                InGameAssert.IsTrue(worst < 0.01f,
                    "every animated canopy bone on the ghost must sit where the deploy animation puts " +
                    "it (canopy-local); worst error " + worst.ToString("F4") + " m");
            }
            finally
            {
                if (build != null && build.root != null)
                    Object.Destroy(build.root);
            }
        }
    }
}
