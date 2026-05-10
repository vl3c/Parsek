using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for the phantom-engine-audio-after-decouple bug.
    ///
    /// When a decoupler fires mid-recording, KSP separates its entire subtree from
    /// the parent vessel. The old playback path stopped FX/audio for ONLY the
    /// decoupler's pid, so child engines/RCS in the decoupled subtree kept their
    /// AudioGhostInfo / EngineGhostInfo / RcsGhostInfo entries on the parent ghost.
    /// Audio sources had been reanchored to the ghost's cameraPivot at spawn (so
    /// the per-frame visual hide via HidePartSubtree did NOT take them with it),
    /// and the parent ghost kept playing the engine sound until it was destroyed.
    /// In watch mode, the user heard the parent's "phantom" engine continuing past
    /// the moment the (now-debris) ghost holding that same pid hit the ground and
    /// exploded.
    ///
    /// These tests cover the pure subtree-walk side of the fix — the audio /
    /// particle / emitter ECall side is Unity-only and is covered by an in-game
    /// test in <c>RuntimeTests</c>.
    /// </summary>
    public class DecoupledSubtreeAudioStopTests
    {
        [Fact]
        public void CollectSubtreePids_DecouplerWithEngineAndRcsChildren_ReturnsAllThree()
        {
            const uint decouplerPid = 100;
            const uint enginePid = 200;
            const uint rcsPid = 201;
            var tree = new Dictionary<uint, List<uint>>
            {
                { decouplerPid, new List<uint> { enginePid, rcsPid } }
            };

            var pids = GhostPlaybackLogic.CollectSubtreePids(decouplerPid, tree);

            Assert.Equal(3, pids.Count);
            Assert.Contains(decouplerPid, pids);
            Assert.Contains(enginePid, pids);
            Assert.Contains(rcsPid, pids);
        }

        [Fact]
        public void CollectSubtreePids_DeepSubtree_VisitsAllDescendants()
        {
            const uint decouplerPid = 100;
            const uint stagePid = 150;
            const uint enginePid = 200;
            const uint nozzlePid = 250;
            var tree = new Dictionary<uint, List<uint>>
            {
                { decouplerPid, new List<uint> { stagePid } },
                { stagePid, new List<uint> { enginePid } },
                { enginePid, new List<uint> { nozzlePid } }
            };

            var pids = GhostPlaybackLogic.CollectSubtreePids(decouplerPid, tree);

            // Pre-fix single-pid stop would have left the deeply-nested engine
            // and nozzle pids untouched.
            Assert.Equal(4, pids.Count);
            Assert.Contains(enginePid, pids);
            Assert.Contains(nozzlePid, pids);
        }

        [Fact]
        public void CollectSubtreePids_NullTree_ReturnsRootOnly()
        {
            var pids = GhostPlaybackLogic.CollectSubtreePids(rootPid: 100, tree: null);

            // Matches HidePartSubtree's null-tree branch (single-part fallback).
            Assert.Single(pids);
            Assert.Equal(100u, pids[0]);
        }

        [Fact]
        public void CollectSubtreePids_TreeMissingRootEntry_ReturnsRootOnly()
        {
            // A leaf decoupler whose pid doesn't appear as a key in the tree map
            // (no children registered). Behaves like the null-tree fallback for
            // that subtree.
            var tree = new Dictionary<uint, List<uint>>
            {
                { 999, new List<uint> { 1000 } }  // unrelated entry
            };

            var pids = GhostPlaybackLogic.CollectSubtreePids(rootPid: 100, tree);

            Assert.Single(pids);
            Assert.Equal(100u, pids[0]);
        }

        [Fact]
        public void CollectSubtreePids_TreeWithSiblingBranch_DoesNotCrossBranches()
        {
            // Tree shape:
            //   root (1)
            //   ├── decoupler (100)
            //   │   └── engine (200)
            //   └── unrelated (300)
            //       └── tank (400)
            // Walking from `decoupler` must not touch the unrelated branch.
            var tree = new Dictionary<uint, List<uint>>
            {
                { 1, new List<uint> { 100, 300 } },
                { 100, new List<uint> { 200 } },
                { 300, new List<uint> { 400 } }
            };

            var pids = GhostPlaybackLogic.CollectSubtreePids(rootPid: 100, tree);

            Assert.Equal(2, pids.Count);
            Assert.Contains(100u, pids);
            Assert.Contains(200u, pids);
            Assert.DoesNotContain(300u, pids);
            Assert.DoesNotContain(400u, pids);
        }

        [Fact]
        public void StopFxAndAudioForSubtree_NullState_NoThrow()
        {
            // Null-state guard: the fast-path early-return must not enter the
            // Unity-touching loop body. Other integration paths are covered in
            // RuntimeTests.cs (Unity-runtime).
            GhostPlaybackLogic.StopFxAndAudioForSubtree(state: null, rootPid: 100, tree: null);
        }
    }
}
