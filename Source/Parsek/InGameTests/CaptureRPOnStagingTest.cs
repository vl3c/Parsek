using System.Collections;
using System.IO;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Rewind-to-Staging (Phase 4, design §5.1 + §6.1 + §7.1 + §7.2 + §7.19):
    /// verifies that a multi-controllable split creates a RewindPoint, writes
    /// the .sfs quicksave under <c>Parsek/RewindPoints/</c>, and populates both
    /// PID maps.
    ///
    /// <para>
    /// Prerequisite: the active vessel has >=2 command pods (or probe cores)
    /// with a decoupler between them. The test skips gracefully when that
    /// precondition is not met; the user is expected to configure the vessel
    /// in the VAB before pressing Ctrl+Shift+T.
    /// </para>
    /// </summary>
    public class CaptureRPOnStagingTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — drives stock staging which permanently mutates the vessel.",
            Description = "Staging a multi-controllable vessel creates a RewindPoint + .sfs under Parsek/RewindPoints/")]
        public IEnumerator CaptureRPOnStaging()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("No active vessel");
                yield break;
            }

            // Count command modules across the vessel — need >=2 so that after
            // the upcoming stage both resulting vessels remain controllable.
            int commandModuleCount = 0;
            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                if (part.FindModuleImplementing<ModuleCommand>() != null)
                    commandModuleCount++;
            }
            if (commandModuleCount < 2)
            {
                InGameAssert.Skip("Needs 2+ command pods");
                yield break;
            }

            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance must be live in FLIGHT");

            int rpCountBefore = scenario.RewindPoints?.Count ?? 0;
            ParsekLog.Info("RewindTest",
                $"CaptureRPOnStaging: start — rpCountBefore={rpCountBefore}, commandModules={commandModuleCount}, " +
                $"parts={vessel.parts.Count}, pid={vessel.persistentId}");

            // Trigger stock staging. StageManager advances the stage pointer and
            // activates engaged parts, which in turn fires the decouple + joint
            // break event that our Phase 4 wiring in ProcessBreakupEvent hooks.
            StageManager.ActivateNextStage();
            ParsekLog.Info("RewindTest", "CaptureRPOnStaging: StageManager.ActivateNextStage() called");

            // Allow a handful of frames for: joint break -> deferred classification
            // -> crash coalescer window -> BREAKUP emit -> RP Begin + deferred save
            // to complete. 120 frames at 60fps is ~2s; the coalescing window is 0.5s.
            const int maxWaitFrames = 600;
            int frame = 0;
            while (frame++ < maxWaitFrames)
            {
                int rpCountNow = scenario.RewindPoints?.Count ?? 0;
                if (rpCountNow > rpCountBefore)
                {
                    var latest = scenario.RewindPoints[rpCountNow - 1];
                    // Wait for the deferred coroutine to populate the maps.
                    if (latest.PidSlotMap != null && latest.PidSlotMap.Count > 0)
                        break;
                }
                yield return null;
            }

            int rpCountAfter = scenario.RewindPoints?.Count ?? 0;
            InGameAssert.IsGreaterThan(rpCountAfter, rpCountBefore,
                $"Expected a RewindPoint after staging (rpCountBefore={rpCountBefore}, rpCountAfter={rpCountAfter})");

            var rp = scenario.RewindPoints[rpCountAfter - 1];
            InGameAssert.IsNotNull(rp, "Latest RewindPoint is null");
            InGameAssert.IsNotNull(rp.RewindPointId, "RewindPointId must be set");
            InGameAssert.IsTrue(rp.PidSlotMap != null && rp.PidSlotMap.Count > 0,
                $"PidSlotMap expected to be populated; got count={(rp.PidSlotMap?.Count ?? 0)}");
            InGameAssert.IsTrue(rp.RootPartPidMap != null && rp.RootPartPidMap.Count > 0,
                $"RootPartPidMap expected to be populated; got count={(rp.RootPartPidMap?.Count ?? 0)}");

            // Assert file exists at saves/<save>/Parsek/RewindPoints/<rpId>.sfs
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            string expectedPath = Path.GetFullPath(Path.Combine(
                root, "saves", saveFolder, "Parsek", "RewindPoints", rp.RewindPointId + ".sfs"));
            InGameAssert.IsTrue(File.Exists(expectedPath),
                $"RewindPoint quicksave missing at '{expectedPath}'");

            // The branch point carrying this RP should have its RewindPointId set.
            bool foundBpLink = false;
            if (RecordingStore.CommittedRecordings != null)
            {
                // Branch points also live on the active tree; a defensive search of all
                // known branch points (committed recordings list + active tree) covers
                // both the just-committed and in-tree cases.
            }
            var activeTree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (activeTree != null && activeTree.BranchPoints != null)
            {
                for (int i = 0; i < activeTree.BranchPoints.Count; i++)
                {
                    var bp = activeTree.BranchPoints[i];
                    if (bp != null && bp.Id == rp.BranchPointId)
                    {
                        foundBpLink = (bp.RewindPointId == rp.RewindPointId);
                        break;
                    }
                }
            }
            InGameAssert.IsTrue(foundBpLink,
                $"BranchPoint {rp.BranchPointId} should have RewindPointId={rp.RewindPointId} set");

            ParsekLog.Info("RewindTest",
                $"CaptureRPOnStaging: PASS rp={rp.RewindPointId} slots={rp.ChildSlots.Count} " +
                $"pidMap={rp.PidSlotMap.Count} rootMap={rp.RootPartPidMap.Count}");
        }
    }
}
