using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless guards for the P8 part-event fidelity wave — the three recorded signals the
    /// 2026-08-09 part-action audit found MISSING (as opposed to wrong):
    ///
    ///   S6. A solar panel / antenna / radiator that BREAKS. The recorder skipped BROKEN
    ///       outright, so a snapped panel replayed intact forever. <see cref="PartEventType.DeployableBroken"/>
    ///       plus the un-hide-on-repair path.
    ///   S7. A running ISRU / drill. The recorder emitted no PartEvent at all for converter
    ///       activity, so a mining base replayed with a dead-still drill.
    ///   S4. An EVA kerbal's jetpack: deploy / stow, sustained thrust, and ragdoll.
    ///
    /// The corresponding WON'T verdicts (the science timeline) are documented in
    /// docs/dev/research/part-action-recording-audit-2026-08-09.md §2, not here — there is no
    /// code to guard for a signal deliberately not recorded.
    /// </summary>
    public class PartEventFidelityTests
    {
        // ------------------------------------------------------------------
        // Step 2 — enum stability
        // ------------------------------------------------------------------

        #region Enum stability

        [Fact]
        public void P8Members_AreExplicitlyNumbered36Through44_AndDoNotDisturbTheExistingTail()
        {
            // The wave's nine members, pinned by VALUE. These land in .prec sidecars as raw
            // ints (TrajectorySidecarBinary writes `(int)eventType`), so a renumber is a
            // silent cross-version data corruption, not a compile error.
            Assert.Equal(36, (int)PartEventType.DeployableBroken);
            Assert.Equal(37, (int)PartEventType.ConverterActivated);
            Assert.Equal(38, (int)PartEventType.ConverterDeactivated);
            Assert.Equal(39, (int)PartEventType.EvaJetpackDeployed);
            Assert.Equal(40, (int)PartEventType.EvaJetpackStowed);
            Assert.Equal(41, (int)PartEventType.EvaJetpackThrustStarted);
            Assert.Equal(42, (int)PartEventType.EvaJetpackThrustStopped);
            Assert.Equal(43, (int)PartEventType.EvaRagdollStarted);
            Assert.Equal(44, (int)PartEventType.EvaRagdollEnded);

            // The pre-P8 tail the append must not have moved.
            Assert.Equal(33, (int)PartEventType.ParachuteSemiDeployed);
            Assert.Equal(34, (int)PartEventType.ThermalAnimationMedium);
            Assert.Equal(35, (int)PartEventType.ParachuteRepacked);

            // 44 is the new highest member: nothing sits above it, so the next wave appends at 45.
            int[] all = Enum.GetValues(typeof(PartEventType)).Cast<int>().ToArray();
            Assert.Equal(44, all.Max());

            // Contiguous 0..44 with no gaps and no duplicates — the property that makes
            // "append at max+1" a safe rule for the next wave.
            Assert.Equal(45, all.Distinct().Count());
            for (int i = 0; i <= 44; i++)
                Assert.True(Enum.IsDefined(typeof(PartEventType), i), $"PartEventType {i} must be defined");
        }

        #endregion

        // ------------------------------------------------------------------
        // Step 2 — the two classifier gates
        // ------------------------------------------------------------------

        #region Classifier gates

        [Fact]
        public void OnlyDeployableBroken_IsAGhostingTrigger_TheOtherEightAreCosmetic()
        {
            // DeployableBroken hides a mesh subtree, which is a geometry change: the chain
            // walker must rebuild the ghost.
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.DeployableBroken));

            // The rest are overlays on a ghost that already exists. A rebuild for any of them
            // would be pure cost — and the cost is per-recording across a whole tree walk.
            foreach (PartEventType cosmetic in new[]
            {
                PartEventType.ConverterActivated,
                PartEventType.ConverterDeactivated,
                PartEventType.EvaJetpackDeployed,
                PartEventType.EvaJetpackStowed,
                PartEventType.EvaJetpackThrustStarted,
                PartEventType.EvaJetpackThrustStopped,
                PartEventType.EvaRagdollStarted,
                PartEventType.EvaRagdollEnded,
            })
            {
                Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(cosmetic),
                    $"{cosmetic} must not force a ghost rebuild");
            }
        }

        [Fact]
        public void OnlyDeployableBroken_PrewarmsAHiddenGhost()
        {
            // Same asymmetry, different consumer: a hidden ghost must be built early enough to
            // apply a subtree hide, but not for state that re-derives itself on the first
            // visible frame (the converter loop phase) or in the prefix replay (the EVA flags).
            Assert.True(GhostPlaybackEngine.ShouldPrewarmHiddenGhostForPartEvent(
                PartEventType.DeployableBroken));

            foreach (PartEventType noPrewarm in new[]
            {
                PartEventType.ConverterActivated,
                PartEventType.ConverterDeactivated,
                PartEventType.EvaJetpackDeployed,
                PartEventType.EvaJetpackStowed,
                PartEventType.EvaJetpackThrustStarted,
                PartEventType.EvaJetpackThrustStopped,
                PartEventType.EvaRagdollStarted,
                PartEventType.EvaRagdollEnded,
            })
            {
                Assert.False(GhostPlaybackEngine.ShouldPrewarmHiddenGhostForPartEvent(noPrewarm),
                    $"{noPrewarm} must not trigger a hidden-ghost prewarm");
            }
        }

        [Fact]
        public void NoP8Member_IsPermanent_BrokenIsReversibleByRepair()
        {
            // The whole reason DeployableBroken is a reversible-family member and not a
            // ForwardPermanentStateEvents type: stock eventRepairExternal takes a BROKEN panel
            // back to RETRACTED (ModuleDeployablePart.DoRepair), so "broken" is a state a
            // recording can leave, not a one-way door like ShroudJettisoned.
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.DeployableBroken));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ConverterActivated));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ConverterDeactivated));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.EvaJetpackDeployed));
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.EvaRagdollStarted));
        }

        #endregion
    }
}
