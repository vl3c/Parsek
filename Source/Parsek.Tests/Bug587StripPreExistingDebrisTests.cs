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
        public void ResolveDebris_InPlaceMarker_KillsNameMatchingSuppressedSubtreeRec()
        {
            // Bug #587 follow-up (2026-04-25 playtest): a non-Destroyed
            // recording that's IN the session-suppressed subtree (i.e., being
            // superseded by this in-place continuation) must also be a kill
            // target. The original predicate only considered Destroyed-terminal
            // recordings, leaving non-Destroyed phantoms in scene as the
            // "second Kerbal X-shaped object" the user saw.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_587_followup_test",
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var trees = new List<RecordingTree>
            {
                MakeTree("tree-1",
                    ("rec-booster", "Kerbal X Probe", TerminalState.Orbiting, 200u),
                    // 'Kerbal X Capsule' is in the suppressed subtree but its
                    // terminal is Landed (not Destroyed) -- under the original
                    // predicate, the matching live vessel would be left in scene.
                    ("rec-supersededchild", "Kerbal X Capsule", TerminalState.Landed, 0u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Capsule"),    // matches suppressed-subtree rec, must kill
                (102u, "Ast. ABC-123"),        // unrelated, keep
            };
            var suppressedSubtree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec-booster",
                "rec-supersededchild",
            };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null, suppressedSubtree);

            Assert.Single(kill);
            Assert.Contains(101u, kill);
            Assert.DoesNotContain(102u, kill);
        }

        [Fact]
        public void ResolveDebris_InPlaceMarker_KillsNameMatchingParentChainRec()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_parent_chain_test",
                ActiveReFlyRecordingId = "rec-probe",
                OriginChildRecordingId = "rec-probe",
                TreeId = "tree-1",
            };
            var tree = MakeTree("tree-1",
                ("rec-upper", "Kerbal X", TerminalState.Orbiting, 300u),
                ("rec-probe", "Kerbal X Probe", null, 200u));
            tree.Recordings["rec-probe"].ParentBranchPointId = "bp-decouple";
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-decouple",
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec-upper" },
                ChildRecordingIds = new List<string> { "rec-probe" },
            });
            var trees = new List<RecordingTree> { tree };
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X"),
                (200u, "Kerbal X Probe"),
            };
            var protectedSet = new HashSet<uint> { 200u };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, protectedSet);

            Assert.Single(kill);
            Assert.Contains(101u, kill);
            Assert.DoesNotContain(200u, kill);
        }

        [Fact]
        public void ResolveDebris_NullSuppressedSubtree_FallsBackToDestroyedTerminalOnly()
        {
            // Backwards compatibility: when the new sessionSuppressedRecordingIds
            // parameter is null/omitted, the predicate must behave exactly like
            // the original Destroyed-terminal-only predicate.
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
                    ("rec-supersededchild", "Kerbal X Capsule", TerminalState.Landed, 0u),
                    ("rec-debris", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Capsule"),    // non-Destroyed, suppression unknown -> keep
                (102u, "Kerbal X Debris"),     // Destroyed-terminal -> kill
            };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null, sessionSuppressedRecordingIds: null);

            Assert.Single(kill);
            Assert.Contains(102u, kill);
            Assert.DoesNotContain(101u, kill);
        }

        [Fact]
        public void ResolveDebris_SuppressedSubtreeAndDestroyedRecsBoth_KillsAllMatching()
        {
            // Composition: the kill set is the UNION of Destroyed-terminal and
            // suppressed-subtree-matching recordings.
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
                    ("rec-supersededchild", "Kerbal X Capsule", TerminalState.Landed, 0u),
                    ("rec-debris", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Capsule"),    // suppressed-subtree match -> kill
                (102u, "Kerbal X Debris"),     // Destroyed -> kill
                (103u, "Ast. ABC-123"),        // unrelated -> keep
            };
            var suppressedSubtree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec-booster",
                "rec-supersededchild",
            };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null, suppressedSubtree);

            Assert.Equal(2, kill.Count);
            Assert.Contains(101u, kill);
            Assert.Contains(102u, kill);
            Assert.DoesNotContain(103u, kill);
        }

        [Fact]
        public void ResolveDebris_SuppressedSubtreeKill_RespectsProtectedPids()
        {
            // The #573 contract still applies: a protected pid (e.g. the
            // selected slot vessel) must NOT be killed even when its name
            // matches a suppressed-subtree recording.
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
                    ("rec-supersededchild", "Kerbal X Capsule", TerminalState.Landed, 0u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (200u, "Kerbal X Capsule"),    // pid is the protected active vessel
                (101u, "Kerbal X Capsule"),    // legitimate kill target
            };
            var suppressedSubtree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec-booster",
                "rec-supersededchild",
            };
            var protectedSet = new HashSet<uint> { 200u };

            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, protectedSet, suppressedSubtree);

            Assert.Single(kill);
            Assert.Contains(101u, kill);
            Assert.DoesNotContain(200u, kill);
        }

        [Fact]
        public void ResolveDebris_LogsKillEligibleCounters_WhenMatchesFound()
        {
            // Pin the kill-summary diagnostic so playtest logs surface
            // destroyedTerminal vs suppressedSubtree counts independently --
            // useful for triaging which predicate path caught a kill.
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
                    ("rec-supersededchild", "Kerbal X Capsule", TerminalState.Landed, 0u),
                    ("rec-debris", "Kerbal X Debris", TerminalState.Destroyed, 0u))
            };
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Capsule"),
                (102u, "Kerbal X Debris"),
            };
            var suppressedSubtree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec-booster",
                "rec-supersededchild",
            };

            ParsekLog.VerboseOverrideForTesting = true;
            var kill = RewindInvoker.ResolveInPlaceContinuationDebrisToKill(
                marker, trees, leftAlone, null, suppressedSubtree);

            Assert.Equal(2, kill.Count);
            string allLogs = string.Join("\n  ", logLines);
            Assert.True(
                logLines.Exists(l =>
                    l.Contains("ResolveInPlaceContinuationDebrisToKill") &&
                    l.Contains("matched 2 pid(s)") &&
                    l.Contains("destroyedTerminal=1") &&
                    l.Contains("suppressedSubtree=1")),
                "Expected kill-summary log line; got:\n  " + allLogs);
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

        // -------------------------------------------------------------
        // PR #558 P2 review follow-up: SnapshotKillTargets must build a
        // stable snapshot of kill targets before any Die() runs against
        // the live source list, so that source-list mutations during
        // iteration cannot skip consecutive matching debris.
        // -------------------------------------------------------------

        private sealed class FakeVessel
        {
            public uint Pid;
            public string Name;
            public FakeVessel(uint pid, string name) { Pid = pid; Name = name; }
        }

        [Fact]
        public void SnapshotKillTargets_NullSource_ReturnsEmpty()
        {
            var snap = RewindInvoker.SnapshotKillTargets<FakeVessel>(
                liveSource: null,
                killPids: new HashSet<uint> { 1u },
                pidGetter: v => v.Pid);

            Assert.NotNull(snap);
            Assert.Empty(snap);
        }

        [Fact]
        public void SnapshotKillTargets_NullKillSet_ReturnsEmpty()
        {
            var live = new List<FakeVessel> { new FakeVessel(1u, "a") };
            var snap = RewindInvoker.SnapshotKillTargets<FakeVessel>(
                live, killPids: null, pidGetter: v => v.Pid);

            Assert.Empty(snap);
        }

        [Fact]
        public void SnapshotKillTargets_EmptyKillSet_ReturnsEmpty()
        {
            var live = new List<FakeVessel> { new FakeVessel(1u, "a") };
            var snap = RewindInvoker.SnapshotKillTargets<FakeVessel>(
                live, killPids: new HashSet<uint>(), pidGetter: v => v.Pid);

            Assert.Empty(snap);
        }

        [Fact]
        public void SnapshotKillTargets_NullPidGetter_ReturnsEmpty()
        {
            var live = new List<FakeVessel> { new FakeVessel(1u, "a") };
            var snap = RewindInvoker.SnapshotKillTargets<FakeVessel>(
                live, new HashSet<uint> { 1u }, pidGetter: null);

            Assert.Empty(snap);
        }

        [Fact]
        public void SnapshotKillTargets_FiltersByPidAndSkipsZeroAndNull()
        {
            var live = new List<FakeVessel>
            {
                new FakeVessel(1u, "alive"),
                null,                                  // null entry skipped
                new FakeVessel(0u, "zero-pid"),       // 0 pid skipped
                new FakeVessel(2u, "kill-A"),         // matches
                new FakeVessel(3u, "alive"),
                new FakeVessel(4u, "kill-B"),         // matches
            };
            var snap = RewindInvoker.SnapshotKillTargets<FakeVessel>(
                live, new HashSet<uint> { 2u, 4u }, v => v == null ? 0u : v.Pid);

            Assert.Equal(2, snap.Count);
            Assert.Contains(snap, v => v.Pid == 2u);
            Assert.Contains(snap, v => v.Pid == 4u);
        }

        [Fact]
        public void SnapshotKillTargets_SourceMutatedDuringConsumption_AllTargetsKilled()
        {
            // The exact failure mode of the pre-fix loop: walking
            // FlightGlobals.Vessels while calling Vessel.Die() removes
            // entries and shifts indices, skipping consecutive matches.
            // With the snapshot pattern, mutating the source list after
            // the snapshot has no effect on the iteration.
            var live = new List<FakeVessel>
            {
                new FakeVessel(1u, "alive"),
                new FakeVessel(2u, "kill-A"),
                new FakeVessel(3u, "kill-B"), // adjacent kill -- pre-fix would skip this
                new FakeVessel(4u, "alive"),
            };
            var killPids = new HashSet<uint> { 2u, 3u };

            var snap = RewindInvoker.SnapshotKillTargets<FakeVessel>(
                live, killPids, v => v == null ? 0u : v.Pid);

            Assert.Equal(2, snap.Count);

            // Simulate Die(): remove each target from the live list one at a time.
            var killed = new List<uint>();
            foreach (var v in snap)
            {
                bool removed = live.Remove(v);
                Assert.True(removed, $"snapshot target pid={v.Pid} missing from live list");
                killed.Add(v.Pid);
            }

            Assert.Contains(2u, killed);
            Assert.Contains(3u, killed);
            Assert.Equal(2, live.Count);
            Assert.Contains(live, v => v.Pid == 1u);
            Assert.Contains(live, v => v.Pid == 4u);
        }

        [Fact]
        public void SnapshotKillTargets_NoneMatch_ReturnsEmpty()
        {
            var live = new List<FakeVessel>
            {
                new FakeVessel(1u, "alive"),
                new FakeVessel(2u, "alive"),
            };
            var snap = RewindInvoker.SnapshotKillTargets<FakeVessel>(
                live, new HashSet<uint> { 99u, 100u }, v => v.Pid);

            Assert.Empty(snap);
        }

        // 2026-04-25 log-hygiene: WarnOnLeftAloneNameCollisions misreported
        // "Strip left N pre-existing vessel(s)" because it summed the live
        // vessel count from a stale pre-supplement-kill list and used the
        // deduped name count as the vessel-instance count. The new contract:
        // vessels=N is the actual instance count from the live survey,
        // collidingNames=M is the unique-name count, and an all-killed
        // colliding set produces no WARN at all.
        //
        // PR #577 P2 review: the live-name-only resurvey caught false
        // positives (active re-fly vessel, ghost ProtoVessels, freshly
        // stripped pids whose Die() event lagged). The new contract scopes
        // the resurvey to the (pid, name) pairs the stripper actually left
        // alone (PostLoadStripResult.LeftAlonePidNames), then defensively
        // excludes SelectedPid / StrippedPids / GhostMap pids before
        // counting survivors.

        private static RewindInvoker.LeftAloneSurveyResult Survey(
            IReadOnlyList<(uint pid, string name)> leftAlonePidNames,
            HashSet<string> collisionNames,
            HashSet<uint> liveVesselPids,
            uint selectedPid = 0u,
            IList<uint> strippedPids = null,
            Func<uint, bool> isGhostMapVessel = null)
        {
            return RewindInvoker.SurveyLiveLeftAloneCollisions(
                leftAlonePidNames,
                collisionNames,
                liveVesselPids,
                selectedPid,
                strippedPids,
                isGhostMapVessel ?? (_ => false));
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_AllLeftAlonePidsKilled_ReturnsZero()
        {
            // 3 leftAlone vessels existed pre-strip; the post-supplement
            // kill drained all three, so liveVesselPids no longer contains
            // any of them. Result must be 0 instance count + 0 unique
            // names, and the leftAlonePidsAlive counter must be 0.
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Debris"),
                (102u, "Kerbal X Debris"),
                (103u, "Kerbal X Debris"),
            };
            var collisions = new HashSet<string>(StringComparer.Ordinal) { "Kerbal X Debris" };
            // No leftAlone pid is still live.
            var live = new HashSet<uint> { 999u };

            var survey = Survey(leftAlone, collisions, live);

            Assert.Equal(0, survey.LiveCollidingVesselCount);
            Assert.Empty(survey.StillPresentNames);
            Assert.Equal(0, survey.LeftAlonePidsAliveCount);
            Assert.Equal(0, survey.ExcludedSelectedCount);
            Assert.Equal(0, survey.ExcludedStrippedCount);
            Assert.Equal(0, survey.ExcludedGhostMapCount);
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_PartialKill_ReportsSurvivorInstanceAndNameCount()
        {
            // 3 leftAlone vessels; 2 die between Strip and warn. The 1
            // still-alive matches the colliding name set, so vessels=1,
            // collidingNames=1.
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Debris"),
                (102u, "Kerbal X Debris"),
                (103u, "Kerbal X Debris"),
            };
            var collisions = new HashSet<string>(StringComparer.Ordinal) { "Kerbal X Debris" };
            var live = new HashSet<uint> { 103u }; // only one survived

            var survey = Survey(leftAlone, collisions, live);

            Assert.Equal(1, survey.LiveCollidingVesselCount);
            Assert.Single(survey.StillPresentNames);
            Assert.Equal("Kerbal X Debris", survey.StillPresentNames[0]);
            Assert.Equal(1, survey.LeftAlonePidsAliveCount);
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_MultipleInstancesSameName_CountsInstancesNotNames()
        {
            // 3 leftAlone "Kerbal X Debris" all alive and colliding. Result
            // is 3 instances under 1 unique name.
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Debris"),
                (102u, "Kerbal X Debris"),
                (103u, "Kerbal X Debris"),
            };
            var collisions = new HashSet<string>(StringComparer.Ordinal) { "Kerbal X Debris" };
            var live = new HashSet<uint> { 101u, 102u, 103u };

            var survey = Survey(leftAlone, collisions, live);

            Assert.Equal(3, survey.LiveCollidingVesselCount);
            Assert.Single(survey.StillPresentNames);
            Assert.Equal("Kerbal X Debris", survey.StillPresentNames[0]);
            Assert.Equal(3, survey.LeftAlonePidsAliveCount);
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_SelectedPidExcluded_DoesNotCountAsLeftover()
        {
            // PR #577 P2 defense case 1: even if SelectedPid somehow leaks
            // into LeftAlonePidNames (it shouldn't), the survey excludes
            // it. The active re-fly vessel sharing a name with a
            // recording is the WHOLE POINT of a re-fly — never a
            // "leftover".
            var leftAlone = new List<(uint, string)>
            {
                (200u, "Kerbal X Probe"),   // simulates active re-fly vessel
                (101u, "Kerbal X Probe"),   // legitimate same-name leftover
            };
            var collisions = new HashSet<string>(StringComparer.Ordinal) { "Kerbal X Probe" };
            var live = new HashSet<uint> { 200u, 101u };

            var survey = Survey(
                leftAlone, collisions, live,
                selectedPid: 200u);

            Assert.Equal(1, survey.LiveCollidingVesselCount);
            Assert.Single(survey.StillPresentNames);
            Assert.Equal(1, survey.LeftAlonePidsAliveCount);
            Assert.Equal(1, survey.ExcludedSelectedCount);
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_StrippedPidExcluded_DoesNotCountAsLeftover()
        {
            // PR #577 P2 defense case 1b: a freshly stripped pid whose
            // Die() event hasn't drained from FlightGlobals yet must not
            // re-enter the survey via the live-pid set.
            var leftAlone = new List<(uint, string)>
            {
                (101u, "Kerbal X Debris"), // freshly stripped, still in live list one frame later
                (102u, "Kerbal X Debris"), // legitimate leftover
            };
            var collisions = new HashSet<string>(StringComparer.Ordinal) { "Kerbal X Debris" };
            // Both still appear in the live snapshot due to lag.
            var live = new HashSet<uint> { 101u, 102u };

            var survey = Survey(
                leftAlone, collisions, live,
                strippedPids: new List<uint> { 101u });

            Assert.Equal(1, survey.LiveCollidingVesselCount);
            Assert.Single(survey.StillPresentNames);
            Assert.Equal(1, survey.LeftAlonePidsAliveCount);
            Assert.Equal(1, survey.ExcludedStrippedCount);
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_GhostMapPidExcluded_DoesNotCountAsLeftover()
        {
            // PR #577 P2 defense case 2: a Parsek ghost ProtoVessel
            // registered in FlightGlobals.Vessels must never count as a
            // "pre-existing leftover" — it's the re-fly's own playback,
            // not a prior-career relic.
            var ghostPid = 9001u;
            var leftAlone = new List<(uint, string)>
            {
                (ghostPid, "Kerbal X Probe"),  // simulates ghost ProtoVessel name overlap
                (101u, "Kerbal X Probe"),      // legitimate leftover
            };
            var collisions = new HashSet<string>(StringComparer.Ordinal) { "Kerbal X Probe" };
            var live = new HashSet<uint> { ghostPid, 101u };

            var survey = Survey(
                leftAlone, collisions, live,
                isGhostMapVessel: pid => pid == ghostPid);

            Assert.Equal(1, survey.LiveCollidingVesselCount);
            Assert.Single(survey.StillPresentNames);
            Assert.Equal(1, survey.LeftAlonePidsAliveCount);
            Assert.Equal(1, survey.ExcludedGhostMapCount);
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_AllExcludedAndAllKilled_ReturnsZero()
        {
            // Composite: one selected, one stripped, one ghost, one
            // already-killed leftover. Nothing legitimate survives -> zero
            // count, every exclusion counter populated.
            var leftAlone = new List<(uint, string)>
            {
                (200u, "Kerbal X Probe"),
                (101u, "Kerbal X Debris"),
                (9001u, "Ghost: Kerbal X"),
                (300u, "Kerbal X Debris"),
            };
            var collisions = new HashSet<string>(StringComparer.Ordinal)
            {
                "Kerbal X Probe", "Kerbal X Debris", "Ghost: Kerbal X",
            };
            // 200, 101, 9001 still live; 300 already gone.
            var live = new HashSet<uint> { 200u, 101u, 9001u };

            var survey = Survey(
                leftAlone, collisions, live,
                selectedPid: 200u,
                strippedPids: new List<uint> { 101u },
                isGhostMapVessel: pid => pid == 9001u);

            Assert.Equal(0, survey.LiveCollidingVesselCount);
            Assert.Empty(survey.StillPresentNames);
            Assert.Equal(0, survey.LeftAlonePidsAliveCount);
            Assert.Equal(1, survey.ExcludedSelectedCount);
            Assert.Equal(1, survey.ExcludedStrippedCount);
            Assert.Equal(1, survey.ExcludedGhostMapCount);
        }

        [Fact]
        public void SurveyLiveLeftAloneCollisions_NullInputs_AreDefensive()
        {
            // Null leftAlone -> empty result. Null live set is treated as
            // "nothing alive" (defensive — the caller's snapshot helper
            // returns an empty set, not null, in production).
            var survey = Survey(
                leftAlonePidNames: null,
                collisionNames: new HashSet<string> { "X" },
                liveVesselPids: new HashSet<uint> { 1u });
            Assert.Equal(0, survey.LiveCollidingVesselCount);

            var leftAlone = new List<(uint, string)> { (1u, "X") };
            survey = Survey(
                leftAlonePidNames: leftAlone,
                collisionNames: null,
                liveVesselPids: new HashSet<uint> { 1u });
            Assert.Equal(0, survey.LiveCollidingVesselCount);
            Assert.Equal(1, survey.LeftAlonePidsAliveCount);

            survey = Survey(
                leftAlonePidNames: leftAlone,
                collisionNames: new HashSet<string> { "X" },
                liveVesselPids: null);
            Assert.Equal(0, survey.LiveCollidingVesselCount);
            Assert.Equal(0, survey.LeftAlonePidsAliveCount);
        }

        // -------------------------------------------------------------
        // EmitStripLeftAloneWarn structured log shape pinning.
        // -------------------------------------------------------------

        private static RewindInvoker.LeftAloneSurveyResult MakeSurvey(
            int liveCollidingVesselCount,
            List<string> stillPresentNames,
            int leftAlonePidsAlive = 0,
            int excludedSelected = 0,
            int excludedStripped = 0,
            int excludedGhostMap = 0)
        {
            return new RewindInvoker.LeftAloneSurveyResult
            {
                LiveCollidingVesselCount = liveCollidingVesselCount,
                StillPresentNames = stillPresentNames,
                LeftAlonePidsAliveCount = leftAlonePidsAlive,
                ExcludedSelectedCount = excludedSelected,
                ExcludedStrippedCount = excludedStripped,
                ExcludedGhostMapCount = excludedGhostMap,
            };
        }

        [Fact]
        public void EmitStripLeftAloneWarn_AllKilled_LogsVerboseAndNoWarn()
        {
            var collisions = new List<string> { "Kerbal X Debris" };
            // 2026-04-25 playtest scenario: post-supplement strip killed all
            // 3 Kerbal X Debris, so liveVesselCount=0 and the WARN must
            // not fire. The original code emitted a misleading
            // "Strip left 1 pre-existing vessel(s)" WARN here.
            RewindInvoker.EmitStripLeftAloneWarn(
                collisions,
                MakeSurvey(0, new List<string>()));

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Rewind]") && l.Contains("Strip left"));
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Rewind]")
                && l.Contains("Strip left no live pre-existing vessel(s)")
                && l.Contains("collidingNames=1")
                && l.Contains("leftAlonePidsAlive=0")
                && l.Contains("excludedSelected=0")
                && l.Contains("excludedStripped=0")
                && l.Contains("excludedGhostMap=0"));
        }

        [Fact]
        public void EmitStripLeftAloneWarn_LiveVesselsRemain_LogsSeparateInstanceAndNameCounts()
        {
            var collisions = new List<string> { "Kerbal X Debris" };
            // 3 still-live Kerbal X Debris under the same colliding name.
            RewindInvoker.EmitStripLeftAloneWarn(
                collisions,
                MakeSurvey(3, new List<string> { "Kerbal X Debris" }, leftAlonePidsAlive: 3));

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Rewind]")
                && l.Contains("Strip left vessels=3 collidingNames=1")
                && l.Contains("leftAlonePidsAlive=3")
                && l.Contains("excludedSelected=0")
                && l.Contains("excludedStripped=0")
                && l.Contains("excludedGhostMap=0")
                && l.Contains("[Kerbal X Debris]")
                && l.Contains("not related to the re-fly"));
        }

        [Fact]
        public void EmitStripLeftAloneWarn_PartialKill_ReportsSurvivors()
        {
            // 2 colliding names; one was killed (only "Foo" survives), one
            // had two instances ("Bar" twice). vessels=3, collidingNames=2.
            var collisions = new List<string> { "Foo", "Bar" };
            RewindInvoker.EmitStripLeftAloneWarn(
                collisions,
                MakeSurvey(3, new List<string> { "Foo", "Bar" }, leftAlonePidsAlive: 3));

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Rewind]")
                && l.Contains("Strip left vessels=3 collidingNames=2")
                && l.Contains("leftAlonePidsAlive=3"));
        }

        [Fact]
        public void EmitStripLeftAloneWarn_NoCollidingNames_NoLog()
        {
            // Defensive: empty colliding list should not log anything.
            RewindInvoker.EmitStripLeftAloneWarn(
                new List<string>(),
                MakeSurvey(0, new List<string>()));

            Assert.DoesNotContain(logLines, l => l.Contains("Strip left"));
        }

        [Fact]
        public void EmitStripLeftAloneWarn_AllExcluded_LogsVerboseWithExclusionCounters()
        {
            // PR #577 P2 review: when every leftAlone pid was excluded
            // (selected/stripped/ghost) and nothing remains to flag, the
            // VERBOSE diagnostic must report the per-class exclusion
            // counts so post-mortem analysis can see WHY no WARN fired.
            var collisions = new List<string> { "Kerbal X Probe" };
            RewindInvoker.EmitStripLeftAloneWarn(
                collisions,
                MakeSurvey(
                    liveCollidingVesselCount: 0,
                    stillPresentNames: new List<string>(),
                    leftAlonePidsAlive: 0,
                    excludedSelected: 1,
                    excludedStripped: 2,
                    excludedGhostMap: 1));

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Rewind]") && l.Contains("Strip left"));
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Rewind]")
                && l.Contains("Strip left no live pre-existing vessel(s)")
                && l.Contains("excludedSelected=1")
                && l.Contains("excludedStripped=2")
                && l.Contains("excludedGhostMap=1"));
        }
    }
}
