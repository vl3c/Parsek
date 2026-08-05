using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GS3-NUDGE-DROPS-UNFINISHED-FLIGHT. One stock map Switch-To click onto a
    /// deployed vessel used to close its Unfinished Flight and let the
    /// RewindPoint reap, against the design intent recorded in
    /// <c>docs/dev/research/extending-rewind-to-stable-leaves.md</c> §8 S17
    /// ("a briefly-nudged probe STAYS in the Unfinished Flights list").
    ///
    /// <para>
    /// The fix has two halves, both covered here:
    /// </para>
    /// <list type="number">
    ///   <item><description>DATA — a BG-member switch consumption must stamp a
    ///   classified terminal on the recording it closes
    ///   (<see cref="SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp"/>).</description></item>
    ///   <item><description>CLASSIFIER — a
    ///   <see cref="BranchPointType.VesselSwitchContinuation"/> downstream branch
    ///   point is a SAME-VESSEL continuation, not a consumption, so the
    ///   Unfinished-Flights gates walk through it
    ///   (<see cref="EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations"/>).</description></item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class SwitchContinuationTerminalWalkTests : IDisposable
    {
        private const string TreeId = "tree_gs3";
        private const string RpBpId = "bp_decouple";
        private const string SwitchBpId = "bp_switch_1";

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SwitchContinuationTerminalWalkTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ------------------------------------------------------------------
        // Half 2 — the walk helper.
        // ------------------------------------------------------------------

        [Fact]
        public void SingleSwitchHop_OrbitingSegment_OriginQualifiesStableLeafUnconcluded()
        {
            // The measured GS-3 shape: origin closed terminal-less by the switch,
            // one VesselSwitchContinuation segment carrying terminal=Orbiting.
            var tree = BuildGs3Tree(
                originTerminal: null,
                segmentTerminal: TerminalState.Orbiting);
            var rp = InstallGs3Scenario();
            var origin = tree.Recordings["rec_origin"];

            Recording walked = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                origin, tree);
            Assert.Equal("rec_segment", walked.RecordingId);

            logLines.Clear();
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                origin, rp.ChildSlots[1], rp, out string reason, tree));
            Assert.Equal("stableLeafUnconcluded", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_origin")
                && l.Contains("reason=stableLeafUnconcluded"));
        }

        [Fact]
        public void ChainedDoubleSwitch_WalksToLastSegmentTerminal()
        {
            // Two glances in a row: origin -> segment1 -> segment2.
            var tree = BuildGs3Tree(originTerminal: null, segmentTerminal: null);
            AddSwitchSegment(tree, "bp_switch_2", "rec_segment", "rec_segment_2",
                TerminalState.Orbiting);
            var rp = InstallGs3Scenario();
            var origin = tree.Recordings["rec_origin"];

            Recording walked = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                origin, tree);
            Assert.Equal("rec_segment_2", walked.RecordingId);

            Assert.True(UnfinishedFlightClassifier.TryQualify(
                origin, rp.ChildSlots[1], rp, out string reason, tree));
            Assert.Equal("stableLeafUnconcluded", reason);
        }

        [Fact]
        public void SwitchSegmentDestroyed_OriginQualifiesCrashed()
        {
            // The nudge crashed the probe: terminal Destroyed read through the
            // segment -> the origin is a crashed Unfinished Flight.
            var tree = BuildGs3Tree(
                originTerminal: null,
                segmentTerminal: TerminalState.Destroyed);
            var rp = InstallGs3Scenario();
            var origin = tree.Recordings["rec_origin"];

            Assert.True(UnfinishedFlightClassifier.TryQualify(
                origin, rp.ChildSlots[1], rp, out string reason, tree));
            Assert.Equal("crashed", reason);
        }

        [Fact]
        public void DownstreamDockBranchPoint_StillRejectsDownstreamBp()
        {
            // A REAL downstream split (Dock) stops the walk, so the pre-existing
            // consumption semantics are preserved verbatim.
            var tree = BuildGs3Tree(
                originTerminal: TerminalState.Orbiting,
                segmentTerminal: TerminalState.Orbiting,
                switchBpType: BranchPointType.Dock);
            var rp = InstallGs3Scenario();
            var origin = tree.Recordings["rec_origin"];

            Recording walked = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                origin, tree);
            Assert.Equal("rec_origin", walked.RecordingId);

            logLines.Clear();
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                origin, rp.ChildSlots[1], rp, out string reason, tree));
            Assert.Equal("downstreamBp", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_origin")
                && l.Contains("reason=downstreamBp"));
        }

        [Fact]
        public void DanglingSwitchBranchPoint_NoChildRecording_BehavesAsBeforeWithoutThrowing()
        {
            // The switch BP exists but no recording claims it as its parent.
            // The walk must stop at the origin (not crash), leaving the
            // pre-fix reject in place.
            var tree = BuildGs3Tree(
                originTerminal: TerminalState.Orbiting,
                segmentTerminal: TerminalState.Orbiting);
            tree.Recordings.Remove("rec_segment");
            var rp = InstallGs3Scenario();
            var origin = tree.Recordings["rec_origin"];

            Recording walked = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                origin, tree);
            Assert.Equal("rec_origin", walked.RecordingId);

            Assert.False(UnfinishedFlightClassifier.TryQualify(
                origin, rp.ChildSlots[1], rp, out string reason, tree));
            Assert.Equal("downstreamBp", reason);
        }

        [Fact]
        public void AmbiguousSwitchBranchPoint_TwoClaimants_StopsWalkRatherThanGuessing()
        {
            var tree = BuildGs3Tree(
                originTerminal: TerminalState.Orbiting,
                segmentTerminal: TerminalState.Orbiting);
            // A second recording claiming the same switch BP as its parent.
            var impostor = Rec("rec_impostor", TerminalState.Destroyed,
                parentBranchPointId: SwitchBpId);
            impostor.TreeId = TreeId;
            tree.AddOrReplaceRecording(impostor);
            RecordingStore.AddRecordingWithTreeForTesting(impostor);
            InstallGs3Scenario();

            Recording walked = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                tree.Recordings["rec_origin"], tree);
            Assert.Equal("rec_origin", walked.RecordingId);
        }

        [Fact]
        public void CyclicSwitchBranchPoints_TerminateWithoutHanging()
        {
            // rec_a --bp_ab--> rec_b --bp_ba--> rec_a. Both branch points are
            // VesselSwitchContinuation, so only the visited guard stops the walk.
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "GS3 cycle",
                RootRecordingId = "rec_a",
            };
            var a = Rec("rec_a", TerminalState.Orbiting,
                parentBranchPointId: "bp_ba", childBranchPointId: "bp_ab");
            var b = Rec("rec_b", TerminalState.Orbiting,
                parentBranchPointId: "bp_ab", childBranchPointId: "bp_ba");
            AddToTree(tree, a, b);
            tree.BranchPoints.Add(SwitchBp("bp_ab", "rec_a", "rec_b"));
            tree.BranchPoints.Add(SwitchBp("bp_ba", "rec_b", "rec_a"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            Recording walked = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                a, tree);
            // rec_a -> rec_b, then rec_b -> rec_a is refused by the visited set.
            Assert.Equal("rec_b", walked.RecordingId);
        }

        [Fact]
        public void NullRecording_WalkReturnsNull()
        {
            Assert.Null(EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(null, null));
        }

        // ------------------------------------------------------------------
        // Half 2 — the candidate-shape gate.
        // ------------------------------------------------------------------

        [Fact]
        public void CandidateShape_TerminallessOriginBehindSwitchBp_NowAccepted()
        {
            var tree = BuildGs3Tree(
                originTerminal: null,
                segmentTerminal: TerminalState.Orbiting);
            InstallGs3Scenario();
            var origin = tree.Recordings["rec_origin"];

            // The origin carries NO terminal of its own — the shape gate reads it
            // through the switch segment.
            Assert.False(origin.TerminalStateValue.HasValue);
            Assert.True(UnfinishedFlightClassifier.IsUnfinishedFlightCandidateShape(origin, tree));
        }

        [Fact]
        public void CandidateShape_TerminallessOriginBehindDockBp_StillRejected()
        {
            var tree = BuildGs3Tree(
                originTerminal: null,
                segmentTerminal: TerminalState.Orbiting,
                switchBpType: BranchPointType.Dock);
            InstallGs3Scenario();

            Assert.False(UnfinishedFlightClassifier.IsUnfinishedFlightCandidateShape(
                tree.Recordings["rec_origin"], tree));
        }

        // ------------------------------------------------------------------
        // End-to-end: the GS-3 A/B, at commit.
        // ------------------------------------------------------------------

        [Fact]
        public void CommitTree_NudgedProbeOrigin_PromotesAndKeepsRewindPointAlive()
        {
            var tree = BuildGs3Tree(
                originTerminal: null,
                segmentTerminal: TerminalState.Orbiting,
                registerCommittedTree: false);
            var rp = InstallGs3Scenario(sessionProvisional: false);

            logLines.Clear();
            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.CommittedProvisional,
                tree.Recordings["rec_origin"].MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("CommitTree promoted rec=rec_origin")
                && l.Contains("reason=stableLeafUnconcluded"));

            // The slot's effective tip is CommittedProvisional -> OPEN -> the
            // RewindPoint cannot reap, so the player keeps the row and the
            // re-fly route (S17).
            Assert.False(RewindPointReaper.IsReapEligible(
                rp, new List<RecordingSupersedeRelation>()));
        }

        // ------------------------------------------------------------------
        // Half 1 — the close-site stamp decision (pure).
        // ------------------------------------------------------------------

        [Fact]
        public void ClassifySwitchCloseTerminalStamp_TerminallessRecording_Stamps()
        {
            var rec = new Recording { RecordingId = "rec_bg" };
            Assert.Equal(SwitchCloseTerminalStampDecision.Stamp,
                SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp(rec));
        }

        [Fact]
        public void ClassifySwitchCloseTerminalStamp_AlreadyClassified_NeverOverwrites()
        {
            var rec = new Recording
            {
                RecordingId = "rec_bg",
                TerminalStateValue = TerminalState.Destroyed,
            };
            Assert.Equal(SwitchCloseTerminalStampDecision.SkipAlreadyClassified,
                SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp(rec));
        }

        [Fact]
        public void ClassifySwitchCloseTerminalStamp_NullRecording_Skips()
        {
            Assert.Equal(SwitchCloseTerminalStampDecision.SkipNoRecording,
                SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp(null));
        }

        // ------------------------------------------------------------------
        // Half 1 — the cache-apply veto (PR #1427 review F1).
        //
        // A ballistic-extrapolator cache can carry a PREDICTED Destroyed for a
        // vessel the player just switched TO, i.e. one that is provably alive.
        // ScopeFinalizationCacheToBackgroundEnd clamps that terminal to the
        // switch UT, so the applier's RejectedTerminalBeforeLastSample guard
        // never fires; applying it would fire a false playback explosion and,
        // if the segment is later discarded, become the row's terminal.
        // ------------------------------------------------------------------

        [Fact]
        public void ClassifySwitchCloseCacheApply_AliveVesselWithDestroyedCache_VetoesToLive()
        {
            Assert.Equal(SwitchCloseCacheApplyDecision.SkipPredictedDestroyedWhileAlive,
                SwitchSegmentBuilder.ClassifySwitchCloseCacheApply(
                    hasLiveVessel: true, cacheTerminal: TerminalState.Destroyed));
        }

        [Theory]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.SubOrbital)]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Splashed)]
        public void ClassifySwitchCloseCacheApply_AliveVesselWithNonDestroyedCache_Applies(
            TerminalState cacheTerminal)
        {
            // A cached non-Destroyed verdict is a reading of the same live
            // vessel and carries the recorder's own endpoint data; only
            // Destroyed is vetoed.
            Assert.Equal(SwitchCloseCacheApplyDecision.Apply,
                SwitchSegmentBuilder.ClassifySwitchCloseCacheApply(
                    hasLiveVessel: true, cacheTerminal: cacheTerminal));
        }

        [Fact]
        public void ClassifySwitchCloseCacheApply_NoVesselWithDestroyedCache_Applies()
        {
            // Defensive branch: the BG-member route dereferences the vessel to
            // reach the stamp, so a null vessel means the switch target could not
            // be resolved. The cache is then the ONLY evidence available and IS
            // applied, matching EndDebrisRecording's v == null semantics.
            Assert.Equal(SwitchCloseCacheApplyDecision.Apply,
                SwitchSegmentBuilder.ClassifySwitchCloseCacheApply(
                    hasLiveVessel: false, cacheTerminal: TerminalState.Destroyed));
        }

        [Fact]
        public void ClassifySwitchCloseCacheApply_NoCacheTerminal_Applies()
        {
            // "No cache resolved" is not the veto's business — the apply site
            // self-diagnoses reason=no-cache and falls through to live.
            Assert.Equal(SwitchCloseCacheApplyDecision.Apply,
                SwitchSegmentBuilder.ClassifySwitchCloseCacheApply(
                    hasLiveVessel: true, cacheTerminal: null));
            Assert.Equal(SwitchCloseCacheApplyDecision.Apply,
                SwitchSegmentBuilder.ClassifySwitchCloseCacheApply(
                    hasLiveVessel: false, cacheTerminal: null));
        }

        // ------------------------------------------------------------------
        // Fixtures.
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the measured GS-3 tree: a crewed focus leaf, the deployed
        /// probe's ORIGIN (parent BP = the RewindPoint's BP, child BP = the
        /// switch BP), and the switch continuation SEGMENT under it.
        /// </summary>
        private static RecordingTree BuildGs3Tree(
            TerminalState? originTerminal,
            TerminalState? segmentTerminal,
            BranchPointType switchBpType = BranchPointType.VesselSwitchContinuation,
            bool registerCommittedTree = true)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "GS3 tree",
                RootRecordingId = "rec_focus",
            };

            var focus = Rec("rec_focus", TerminalState.Orbiting,
                childBranchPointId: RpBpId);
            var origin = Rec("rec_origin", originTerminal,
                parentBranchPointId: RpBpId, childBranchPointId: SwitchBpId);
            var segment = Rec("rec_segment", segmentTerminal,
                parentBranchPointId: SwitchBpId);
            AddToTree(tree, focus, origin, segment);

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = RpBpId,
                UT = 100.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_focus" },
                ChildRecordingIds = new List<string> { "rec_focus", "rec_origin" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = SwitchBpId,
                UT = 150.0,
                Type = switchBpType,
                ParentRecordingIds = new List<string> { "rec_origin" },
                ChildRecordingIds = new List<string> { "rec_segment" },
            });

            if (registerCommittedTree)
                RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static void AddSwitchSegment(
            RecordingTree tree,
            string newBpId,
            string parentRecId,
            string newRecId,
            TerminalState? terminal)
        {
            tree.Recordings[parentRecId].ChildBranchPointId = newBpId;
            var seg = Rec(newRecId, terminal, parentBranchPointId: newBpId);
            AddToTree(tree, seg);
            tree.BranchPoints.Add(SwitchBp(newBpId, parentRecId, newRecId));
        }

        private static BranchPoint SwitchBp(string id, string parentRecId, string childRecId)
            => new BranchPoint
            {
                Id = id,
                UT = 200.0,
                Type = BranchPointType.VesselSwitchContinuation,
                ParentRecordingIds = new List<string> { parentRecId },
                ChildRecordingIds = new List<string> { childRecId },
            };

        private static void AddToTree(RecordingTree tree, params Recording[] recordings)
        {
            foreach (var rec in recordings)
            {
                rec.TreeId = tree.Id;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
        }

        private static Recording Rec(
            string id,
            TerminalState? terminal,
            string parentBranchPointId = null,
            string childBranchPointId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
            };
        }

        /// <summary>
        /// Installs the RewindPoint measured in GS-3: slot 0 is the crewed focus
        /// leaf (FocusSlotIndex = 0), slot 1 is the deployed probe's origin.
        /// </summary>
        private static RewindPoint InstallGs3Scenario(bool sessionProvisional = false)
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_gs3",
                BranchPointId = RpBpId,
                UT = 100.0,
                SessionProvisional = sessionProvisional,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    new ChildSlot { SlotIndex = 1, OriginChildRecordingId = "rec_origin", Controllable = true },
                },
            };

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return rp;
        }
    }
}
