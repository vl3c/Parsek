using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the pure routing classifier
    /// <see cref="ParsekScenario.ClassifyAutoCommitFidelity"/>, which decides how an
    /// outside-Flight auto-commit disposes of the pending tree's vessel snapshots. The
    /// file keeps the name of the pre-2026-08-29 predicate it tested
    /// (<c>ShouldSilentFullFidelityCommit</c>, deleted with the fix) so the history stays
    /// greppable. See
    /// docs/dev/plans/silent-full-fidelity-autocommit.md and the
    /// AUTOMERGE-ON-BY-DEFAULT entry in docs/dev/todo-and-known-bugs.md.
    ///
    /// <para>The 2026-08-29 fix moved ONE cell of the matrix below: a non-re-fly
    /// Limbo / LimboVesselSwitch stash at a real scene used to route to
    /// <see cref="ParsekScenario.AutoCommitFidelity.GhostOnly"/> (which nulls EVERY
    /// snapshot) and now routes to
    /// <see cref="ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity"/>.
    /// The three ghost-only justifications — autoMerge off, an active re-fly, and
    /// MAINMENU — are untouched, and the matrix below is exhaustive precisely so a
    /// future change to one of them cannot move silently.</para>
    /// </summary>
    public class SilentFullFidelityCommitDecisionTests
    {
        // -------------------------------------------------------------------
        // THE WHOLE MATRIX. isAutoMerge {2} x pendingState {3} x reFlyActive {2}
        // x scene {3} = 36 rows, every one written out with its expected route and
        // its expected ghost-only reason. Enumerated rather than generated against
        // an oracle on purpose: an oracle would be a second copy of the predicate
        // and would agree with it by construction, including when both are wrong.
        //
        // Scene coverage: SPACECENTER and TRACKSTATION are the two the site can
        // actually reach (its caller is gated `LoadedScene != FLIGHT`), and
        // MAINMENU is the one carve-out. They are the three distinct behaviours.
        // -------------------------------------------------------------------

        // --- autoMerge OFF: ghost-only everywhere, and `not-automerge` wins over
        //     every other reason (the player is being asked, or the site is a
        //     safety net; either way it must not commit at fidelity silently).
        [Theory]
        [InlineData(PendingTreeState.Finalized, false, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.Finalized, false, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.Finalized, false, GameScenes.MAINMENU)]
        [InlineData(PendingTreeState.Finalized, true, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.Finalized, true, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.Finalized, true, GameScenes.MAINMENU)]
        [InlineData(PendingTreeState.Limbo, false, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.Limbo, false, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.Limbo, false, GameScenes.MAINMENU)]
        [InlineData(PendingTreeState.Limbo, true, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.Limbo, true, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.Limbo, true, GameScenes.MAINMENU)]
        [InlineData(PendingTreeState.LimboVesselSwitch, false, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.LimboVesselSwitch, false, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.LimboVesselSwitch, false, GameScenes.MAINMENU)]
        [InlineData(PendingTreeState.LimboVesselSwitch, true, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.LimboVesselSwitch, true, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.LimboVesselSwitch, true, GameScenes.MAINMENU)]
        public void AutoMergeOff_IsAlwaysGhostOnly_WithNotAutoMergeReason(
            PendingTreeState state, bool reFlyActive, GameScenes scene)
        {
            AssertRoute(
                isAutoMerge: false, state: state, reFlyActive: reFlyActive, scene: scene,
                expected: ParsekScenario.AutoCommitFidelity.GhostOnly,
                expectedReason: "not-automerge");
        }

        // --- autoMerge ON + an active re-fly: ghost-only for EVERY tree state and
        //     scene. This is the plan-§4.2/§10 carve-out and the 2026-08-29 fix must
        //     not leak into it: a silent MergeCommit here would run
        //     TryCommitReFlySupersede, writing supersede rows and flipping MergeState
        //     with no dialog. These six rows are the leak detector.
        [Theory]
        [InlineData(PendingTreeState.Finalized, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.Finalized, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.Finalized, GameScenes.MAINMENU)]
        [InlineData(PendingTreeState.Limbo, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.Limbo, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.Limbo, GameScenes.MAINMENU)]
        [InlineData(PendingTreeState.LimboVesselSwitch, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.LimboVesselSwitch, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.LimboVesselSwitch, GameScenes.MAINMENU)]
        public void ReFlyActive_IsAlwaysGhostOnly_WhateverTheTreeState(
            PendingTreeState state, GameScenes scene)
        {
            AssertRoute(
                isAutoMerge: true, state: state, reFlyActive: true, scene: scene,
                expected: ParsekScenario.AutoCommitFidelity.GhostOnly,
                expectedReason: "re-fly-active");
        }

        // --- autoMerge ON, no re-fly, MAINMENU: ghost-only for every tree state.
        //     The game is unloading; there is no destination scene, spawn-at-end
        //     never runs, and a quicksave during unload is unsafe.
        [Theory]
        [InlineData(PendingTreeState.Finalized)]
        [InlineData(PendingTreeState.Limbo)]
        [InlineData(PendingTreeState.LimboVesselSwitch)]
        public void MainMenu_IsAlwaysGhostOnly_WhateverTheTreeState(PendingTreeState state)
        {
            AssertRoute(
                isAutoMerge: true, state: state, reFlyActive: false, scene: GameScenes.MAINMENU,
                expected: ParsekScenario.AutoCommitFidelity.GhostOnly,
                expectedReason: "mainmenu");
        }

        // --- autoMerge ON, no re-fly, a real scene, Finalized: the plan-§4.1 route,
        //     unchanged by the fix.
        [Theory]
        [InlineData(GameScenes.SPACECENTER)]
        [InlineData(GameScenes.TRACKSTATION)]
        public void Finalized_AtARealScene_TakesTheSilentFullFidelityRoute(GameScenes scene)
        {
            AssertRoute(
                isAutoMerge: true, state: PendingTreeState.Finalized, reFlyActive: false,
                scene: scene,
                expected: ParsekScenario.AutoCommitFidelity.SilentFullFidelity,
                expectedReason: "");
        }

        // --- THE FIXED CELL. autoMerge ON, no re-fly, a real scene, a resume stash.
        //     Pre-fix this was GhostOnly with reason `state=Limbo` — the branch the
        //     S0.9 (cold) and S0.10 (warm) flights measured destroying both
        //     snapshots. Both non-Finalized states are named separately so a change
        //     that only fixes one cannot pass this file.
        [Theory]
        [InlineData(PendingTreeState.Limbo, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.Limbo, GameScenes.TRACKSTATION)]
        [InlineData(PendingTreeState.LimboVesselSwitch, GameScenes.SPACECENTER)]
        [InlineData(PendingTreeState.LimboVesselSwitch, GameScenes.TRACKSTATION)]
        public void NonReFlyResumeStash_AtARealScene_PreservesFidelityInsteadOfGhostOnly(
            PendingTreeState state, GameScenes scene)
        {
            AssertRoute(
                isAutoMerge: true, state: state, reFlyActive: false, scene: scene,
                expected: ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity,
                expectedReason: "");
        }

        // -------------------------------------------------------------------
        // The reason vocabulary. `state=` used to be a ghost-only reason and no
        // longer is — the harness specs that grep this token need that to be an
        // asserted fact rather than a reading of the source.
        // -------------------------------------------------------------------

        [Theory]
        [InlineData(PendingTreeState.Limbo)]
        [InlineData(PendingTreeState.LimboVesselSwitch)]
        public void TheTreeStateIsNoLongerAGhostOnlyReason(PendingTreeState state)
        {
            string reason;
            ParsekScenario.ClassifyAutoCommitFidelity(
                isAutoMerge: true,
                pendingState: state,
                reFlyActive: false,
                loadedScene: GameScenes.SPACECENTER,
                ghostOnlyReason: out reason);
            Assert.DoesNotContain("state=", reason);

            // And when a resume stash DOES reach the ghost-only branch, the reason
            // names the real justification (the carve-out), never the tree state.
            ParsekScenario.ClassifyAutoCommitFidelity(
                isAutoMerge: true,
                pendingState: state,
                reFlyActive: false,
                loadedScene: GameScenes.MAINMENU,
                ghostOnlyReason: out reason);
            Assert.Equal("mainmenu", reason);
        }

        private static void AssertRoute(
            bool isAutoMerge,
            PendingTreeState state,
            bool reFlyActive,
            GameScenes scene,
            ParsekScenario.AutoCommitFidelity expected,
            string expectedReason)
        {
            string reason;
            var actual = ParsekScenario.ClassifyAutoCommitFidelity(
                isAutoMerge, state, reFlyActive, scene, out reason);
            Assert.Equal(expected, actual);
            Assert.Equal(expectedReason, reason);
        }
    }
}
