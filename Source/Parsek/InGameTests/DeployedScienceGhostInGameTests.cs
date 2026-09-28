using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Every Breaking Ground deployed-science part builds a ghost whose deploy animation is
    /// sampled, so a replayed ground-part cluster shows each part unfolding rather than
    /// popping in stowed or already open. The operator's run `logs/2026-09-28_2231_deployables`
    /// measured the ladder on all eight (`Ladder 'DeployedRTG': sampled 3 animated transforms
    /// from 'RTGDeploy' (stowed=time0)`, SolarPanel 15, SeismicSensor 36, CentralStation 13,
    /// GoExOb 11, IONExp 3, WeatherStn 19, SatDish 11); this cell pins that shape per part
    /// against the live PartLoader, which no headless test can load.
    /// </summary>
    public class DeployedScienceGhostInGameTests
    {
        private readonly InGameTestRunner runner;
        public DeployedScienceGhostInGameTests(InGameTestRunner runner) { this.runner = runner; }

        private const uint FixturePartPid = 100000u;

        // The eight SquadExpansion/Serenity/Parts/DeployedScience parts, runtime names.
        internal static readonly string[] DeployedScienceParts =
        {
            "DeployedCentralStation", "DeployedRTG", "DeployedSolarPanel", "DeployedSeismicSensor",
            "DeployedGoExOb", "DeployedIONExp", "DeployedWeatherStn", "DeployedSatDish",
        };

        [InGameTest(Category = "DeployedScienceGhost", Scene = GameScenes.FLIGHT,
            Description = "Every Breaking Ground deployed-science part builds a ghost with its deploy animation sampled (stowed and deployed poses differ)")]
        public void EveryDeployedSciencePartBuildsAGhostWithItsDeployAnimationSampled()
        {
            var missing = new List<string>();
            foreach (string partName in DeployedScienceParts)
                if (PartLoader.getPartInfoByName(partName) == null)
                    missing.Add(partName);
            if (missing.Count == DeployedScienceParts.Length)
            {
                InGameAssert.Skip("Breaking Ground (SquadExpansion/Serenity) is not installed: "
                    + "PartLoader knows none of " + string.Join(",", DeployedScienceParts));
                return;
            }
            InGameAssert.AreEqual(0, missing.Count,
                "Serenity is partly loaded: PartLoader lacks " + string.Join(",", missing.ToArray()));

            var failures = new List<string>();
            var summary = new List<string>();
            foreach (string partName in DeployedScienceParts)
            {
                GhostBuildResult build = null;
                try
                {
                    Recording rec = BuildSinglePartFixture(partName, "parsektest-bg-" + partName);
                    build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(rec, "ParsekTest_bg_" + partName);
                    if (build == null || build.root == null)
                    {
                        failures.Add(partName + ": no ghost built");
                        continue;
                    }
                    var state = new GhostPlaybackState
                    {
                        vesselName = partName,
                        recordingId = rec.RecordingId,
                        ghost = build.root
                    };
                    GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, build, rec);

                    DeployableGhostInfo info = null;
                    if (state.deployableInfos == null
                        || !state.deployableInfos.TryGetValue(FixturePartPid, out info)
                        || info == null || info.transforms == null || info.transforms.Count == 0)
                    {
                        failures.Add(partName + ": no deployable info with sampled transforms");
                        continue;
                    }
                    int moving = CountMovingTransforms(info);
                    if (moving == 0)
                    {
                        failures.Add(partName + ": " + info.transforms.Count
                            + " sampled transforms but stowed == deployed on every one");
                        continue;
                    }
                    summary.Add(partName + "=" + info.transforms.Count + "/" + moving);
                }
                finally
                {
                    if (build != null && build.root != null)
                        Object.Destroy(build.root);
                }
            }

            ParsekLog.Info("InGameTest", "DeployedScienceGhost: sampled/moving per part "
                + string.Join(" ", summary.ToArray())
                + (failures.Count > 0 ? " failures=" + failures.Count : ""));
            InGameAssert.AreEqual(0, failures.Count,
                "deployed-science ghosts without a sampled deploy animation: "
                + string.Join("; ", failures.ToArray()));
        }

        private static int CountMovingTransforms(DeployableGhostInfo info)
        {
            int moving = 0;
            for (int i = 0; i < info.transforms.Count; i++)
            {
                DeployableTransformState s = info.transforms[i];
                if ((s.deployedPos - s.stowedPos).sqrMagnitude > 1e-8f
                    || Quaternion.Angle(s.stowedRot, s.deployedRot) > 0.01f
                    || (s.deployedScale - s.stowedScale).sqrMagnitude > 1e-8f)
                    moving++;
            }
            return moving;
        }

        private static Recording BuildSinglePartFixture(string partName, string idPrefix)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", idPrefix);
            ConfigNode partNode = snapshot.AddNode("PART");
            partNode.AddValue("name", partName);
            partNode.AddValue("persistentId", FixturePartPid.ToString());
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
