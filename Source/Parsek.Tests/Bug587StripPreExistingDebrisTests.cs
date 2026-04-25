using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #587: pre-existing debris kill supplement to <c>PostLoadStripper.Strip</c>
    /// for the in-place continuation Re-Fly path. The stripper's PidSlotMap can't
    /// see prior-career debris carried in the rewind quicksave's protoVessels; for
    /// an in-place continuation re-fly, leftover debris named after a Destroyed
    /// recording in the same tree confuses KSP-stock patched conics into a
    /// phantom encounter + 50x warp cap.
    /// </summary>
    [Collection("Sequential")]
    public class Bug587StripPreExistingDebrisTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug587StripPreExistingDebrisTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree MakeTree(
            string treeId, params (string id, string vesselName, TerminalState? terminal, uint pid)[] recs)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = treeId };
            foreach (var r in recs)
            {
                tree.AddOrReplaceRecording(new Recording
                {
                    RecordingId = r.id,
                    VesselName = r.vesselName,
                    TerminalStateValue = r.terminal,
                    VesselPersistentId = r.pid,
                    TreeId = treeId,
                });
            }
            return tree;
        }

        [Fact]
        public void ResolveDebris_NullMarker_ReturnsEmpty()
        {
            var trees = new List<RecordingTree> { MakeTree("tree-1") };
            var leftAlone = new List<(uint, string)> { (100u, "Kerbal X Debris") };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker: null,
                trees: trees,
                leftAlonePids: leftAlone,
                protectedPids: null);

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDebris_PlaceholderPattern_ReturnsEmpty()
        {
            // Placeholder pattern keeps the live pre-rewind active vessel in
            // scene; killing matching debris there would risk taking the
            // player's actively-re-flown vessel.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-fresh-provisional",
                OriginChildRecordingId = "rec-origin",
                TreeId = "tree-1",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-debris", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };
            var leftAlone = new List<(uint, string)> { (100u, "Kerbal X Debris") };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null);

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDebris_InPlaceMarker_KillsDebrisMatchingDestroyedRec()
        {
            // The 2026-04-25 playtest's exact case.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_587_test",
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-booster", "Kerbal X Probe", TerminalState.Orbiting, 200u),
                    ("rec-debris-1", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (100u, "Ast. ABC-123"),         // unrelated -- keep
                (101u, "Kerbal X Debris"),      // matches Destroyed rec -- kill
                (102u, "Kerbal X Debris"),      // another debris -- kill
            };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null);

            Assert.Equal(2, kill.Count);
            Assert.Contains(101u, kill);
            Assert.Contains(102u, kill);
            Assert.DoesNotContain(100u, kill);
        }

        [Fact]
        public void ResolveDebris_NameMatchesNonDestroyedRec_KeepsAlive()
        {
            // A live "Kerbal X" in a parallel save, terminal=Orbiting, must not be killed.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-booster", "Kerbal X Probe", TerminalState.Orbiting, 200u),
                    ("rec-mission", "Kerbal X", TerminalState.Orbiting, 100u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X"), // matches Orbiting recording -- keep alive
            };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null);

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDebris_ProtectedPidNotKilled()
        {
            // #573 contract: never kill the actively re-flown vessel even if its
            // name matches a Destroyed recording (defense-in-depth).
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-booster", "Kerbal X Probe", TerminalState.Orbiting, 200u),
                    ("rec-debris", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (200u, "Kerbal X Debris"), // pid is the active vessel even though name matches -- defended
                (101u, "Kerbal X Debris"), // legitimate debris -- kill
            };
            var protectedSet = new HashSet<uint> { 200u };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, protectedSet);

            Assert.Single(kill);
            Assert.Contains(101u, kill);
            Assert.DoesNotContain(200u, kill);
        }

        [Fact]
        public void ResolveDebris_TreeIdMismatch_ReturnsEmpty()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-OTHER",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-debris", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };
            var leftAlone = new List<(uint, string)> { (101u, "Kerbal X Debris") };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null);

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDebris_NoDestroyedRecsInTree_ReturnsEmpty()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-booster", "Kerbal X Probe", TerminalState.Orbiting, 200u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Debris"),
            };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null);

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDebris_EmptyLeftAlone_ReturnsEmpty()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-debris", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, new List<(uint, string)>(), null);

            Assert.Empty(kill);
        }
    }
}
