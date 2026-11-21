using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// todo GHOST-LADDER-SPAWNS-DEPLOYED: a stock retractable ladder recorded stowed must spawn
    /// stowed on its ghost, and one recorded extended must spawn extended. The part parity sweep
    /// (run 2026-11-21_1645) measured the one-part telescopicLadder: real box 0.571x0.224x0.630,
    /// ghost box 0.651x1.535x0.630, i.e. the ghost drew the ladder extended. Stock holds the
    /// clip at normalizedTime 1 when Retracted and 0 when Extended; the headless half
    /// (RetractableLadderGhostPoseTests) pins that decision, and this cell measures the posed
    /// ghost meshes against a live PartLoader, which no headless test can load.
    /// </summary>
    public class LadderGhostInGameTests
    {
        private readonly InGameTestRunner runner;
        public LadderGhostInGameTests(InGameTestRunner runner) { this.runner = runner; }

        private const uint FixturePartPid = 100000u;

        internal static readonly string[] LadderParts = { "telescopicLadder", "telescopicLadderBay" };

        /// <summary>
        /// A retracted telescopic ladder's mesh box is well under half the extended one's
        /// diagonal (measured 0.88 m vs 1.79 m); 0.8 leaves room for mesh and pose noise while
        /// still failing when both poses read the same.
        /// </summary>
        private const float MaxRetractedToExtendedDiagonalRatio = 0.8f;

        [InGameTest(Category = "LadderGhost", Scene = GameScenes.FLIGHT,
            Description = "A stock retractable ladder ghost spawns retracted from a Retracted snapshot and extended from an Extended one")]
        public void RetractableLadderGhostSpawnsInTheSnapshotState()
        {
            var failures = new List<string>();
            var summary = new List<string>();
            int tested = 0;
            foreach (string partName in LadderParts)
            {
                if (PartLoader.getPartInfoByName(partName) == null)
                {
                    summary.Add(partName + "=absent");
                    continue;
                }
                tested++;
                Vector3 retracted, extended;
                string err = MeasureSpawnBox(partName, "Retracted", out retracted);
                if (err == null)
                    err = MeasureSpawnBox(partName, "Extended", out extended);
                else
                    extended = Vector3.zero;
                if (err != null)
                {
                    failures.Add(partName + ": " + err);
                    continue;
                }
                float ratio = extended.magnitude > 0f ? retracted.magnitude / extended.magnitude : 1f;
                summary.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} retracted={1:F3}x{2:F3}x{3:F3} extended={4:F3}x{5:F3}x{6:F3} ratio={7:F3}",
                    partName, retracted.x, retracted.y, retracted.z,
                    extended.x, extended.y, extended.z, ratio));
                if (ratio > MaxRetractedToExtendedDiagonalRatio)
                    failures.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}: retracted spawn box diagonal {1:F3} is not under {2} of the extended {3:F3}",
                        partName, retracted.magnitude, MaxRetractedToExtendedDiagonalRatio, extended.magnitude));
            }

            ParsekLog.Info("InGameTest", "LadderGhost: " + string.Join(" | ", summary.ToArray())
                + (failures.Count > 0 ? " failures=" + failures.Count : ""));
            if (tested == 0)
            {
                InGameAssert.Skip("PartLoader knows none of " + string.Join(",", LadderParts));
                return;
            }
            InGameAssert.AreEqual(0, failures.Count,
                "ladder ghosts not spawned in their snapshot state: " + string.Join("; ", failures.ToArray()));
        }

        /// <summary>
        /// Builds the one-part ghost with the given persisted StateName, runs the real spawn
        /// pipeline (stow baseline, then the snapshot baseline), and returns the mesh box in the
        /// ghost root's local frame. Returns an error string, or null on success.
        /// </summary>
        private static string MeasureSpawnBox(string partName, string stateName, out Vector3 size)
        {
            size = Vector3.zero;
            GhostBuildResult build = null;
            try
            {
                Recording rec = BuildFixture(partName, stateName);
                build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(rec, "ParsekTest_ladder_" + partName);
                if (build == null || build.root == null)
                    return "no ghost built (" + stateName + ")";
                var state = new GhostPlaybackState
                {
                    vesselName = partName,
                    recordingId = rec.RecordingId,
                    ghost = build.root
                };
                GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, build, rec);
                DeployableGhostInfo info;
                if (state.deployableInfos == null
                    || !state.deployableInfos.TryGetValue(FixturePartPid, out info)
                    || info == null || info.transforms == null || info.transforms.Count == 0)
                    return "no ladder deployable info (" + stateName + ")";

                if (!TryMeasureLocalMeshBox(build.root.transform, out size))
                    return "no meshes to measure (" + stateName + ")";
                return null;
            }
            finally
            {
                if (build != null && build.root != null)
                    Object.Destroy(build.root);
            }
        }

        private static bool TryMeasureLocalMeshBox(Transform root, out Vector3 size)
        {
            size = Vector3.zero;
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds b = mf.sharedMesh.bounds;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3(
                        (c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any) { min = p; max = p; any = true; }
                    else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                }
            }
            if (any) size = max - min;
            return any;
        }

        private static Recording BuildFixture(string partName, string stateName)
        {
            string idPrefix = "parsektest-ladder-" + partName + "-" + stateName;
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", idPrefix);
            ConfigNode partNode = snapshot.AddNode("PART");
            partNode.AddValue("name", partName);
            partNode.AddValue("persistentId", FixturePartPid.ToString(CultureInfo.InvariantCulture));
            ConfigNode module = partNode.AddNode("MODULE");
            module.AddValue("name", "RetractableLadder");
            module.AddValue("isEnabled", "True");
            module.AddValue("StateName", stateName);
            double t0 = Planetarium.GetUniversalTime();
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
    }
}
