using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Rewind-to-Staging design doc section 6.4 + section 5.1 RootPartPidMap
    /// preconditions: <c>Part.persistentId</c> must be stable across a
    /// save/load cycle for the same physical part. If it is NOT, the fallback
    /// root-part identity key in <see cref="RewindPoint.RootPartPidMap"/> has
    /// no grounding and the entire strip/activate pipeline needs a different
    /// fallback — promoted to a design-doc amendment rather than implemented
    /// against a broken foundation.
    ///
    /// This test saves the current flight scene, reloads it, and verifies
    /// that the HashSet of <c>Part.persistentId</c> values on the active
    /// vessel matches before and after. A failure emits a CRITICAL-tagged
    /// Error so the regression shows up in log-validation as an escalation
    /// trigger.
    ///
    /// Scene: FLIGHT. Requires the active vessel to have at least 3 parts so
    /// a mismatch cannot accidentally pass by coincidence on a single-part
    /// pod. Marked <c>RunLast</c> and <c>AllowBatchExecution = false</c>
    /// because the stock save/load touches KSP's global scene state.
    /// </summary>
    public class PartPersistentIdStabilityTests
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — executes a real stock SaveGame/LoadGame round-trip; excluded from Run All to avoid disrupting the live FLIGHT session.",
            Description = "Part.persistentId is stable across save/load (precondition for RewindPoint.RootPartPidMap)")]
        public IEnumerator PartPersistentIdStableAcrossSaveLoad()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("No active vessel");
                yield break;
            }

            if (vessel.parts == null || vessel.parts.Count < 3)
            {
                InGameAssert.Skip("Needs >=3 parts");
                yield break;
            }

            // Snapshot pre-save persistentIds. Using a HashSet so reordering between
            // runs is ignored — only identity presence matters for the RootPartPidMap
            // lookup in RewindPoint.Strip.
            var prePids = new HashSet<uint>();
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                var part = vessel.parts[i];
                if (part == null) continue;
                prePids.Add(part.persistentId);
            }

            int preFlightInstanceId = ParsekFlight.Instance != null
                ? ParsekFlight.Instance.GetInstanceID()
                : -1;

            const string testSaveName = "Parsek_PartIdStabilityTest";

            string saveResult = GamePersistence.SaveGame(
                testSaveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            InGameAssert.IsTrue(!string.IsNullOrEmpty(saveResult),
                $"SaveGame('{testSaveName}') returned empty result");

            yield return null;

            Game loaded = GamePersistence.LoadGame(
                testSaveName, HighLogic.SaveFolder, true, false);
            InGameAssert.IsNotNull(loaded,
                $"LoadGame('{testSaveName}') returned null");

            HighLogic.CurrentGame = loaded;
            HighLogic.LoadScene(GameScenes.FLIGHT);

            yield return Helpers.QuickloadResumeHelpers.WaitForFlightReady(
                preFlightInstanceId, 30f);

            var postVessel = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(postVessel, "Active vessel missing after reload");
            InGameAssert.IsTrue(postVessel.parts != null && postVessel.parts.Count > 0,
                "Active vessel has no parts after reload");

            var postPids = new HashSet<uint>();
            for (int i = 0; i < postVessel.parts.Count; i++)
            {
                var part = postVessel.parts[i];
                if (part == null) continue;
                postPids.Add(part.persistentId);
            }

            bool match = prePids.SetEquals(postPids);
            ParsekLog.Info("Rewind",
                $"PartPersistentIdStability: prePids={prePids.Count} postPids={postPids.Count} " +
                $"match={match}");

            if (!match)
            {
                var missing = new HashSet<uint>(prePids);
                missing.ExceptWith(postPids);
                var added = new HashSet<uint>(postPids);
                added.ExceptWith(prePids);
                ParsekLog.Error("Rewind",
                    "[CRITICAL] Part.persistentId unstable across save/load — " +
                    $"pre={prePids.Count} post={postPids.Count} missing={missing.Count} " +
                    $"added={added.Count}. RootPartPidMap fallback DESIGN INVALID — " +
                    "escalate to design-doc amendment before continuing");
            }

            InGameAssert.IsTrue(match,
                $"Part.persistentId HashSet differs after save/load " +
                $"(pre={prePids.Count}, post={postPids.Count})");
        }
    }
}
