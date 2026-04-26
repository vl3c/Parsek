using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for internal static methods on RecordingsTableUI.
    /// These methods were extracted from ParsekUI and are tested both directly
    /// and via the ParsekUI forwarders.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableUITests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingsTableUITests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording MakeRec(double startUT, double endUT, string name = "Test")
        {
            var rec = new Recording { VesselName = name };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            if (endUT > startUT)
                rec.Points.Add(new TrajectoryPoint { ut = endUT });
            return rec;
        }

        private static Recording MakeRecWithId(string id, string name = "Test")
        {
            var rec = new Recording { RecordingId = id, VesselName = name };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            return rec;
        }

        private static ChildSlot MakeSlot(
            int slotIndex, string recId, bool disabled = false, string disabledReason = null)
        {
            return new ChildSlot
            {
                SlotIndex = slotIndex,
                OriginChildRecordingId = recId,
                Controllable = true,
                Disabled = disabled,
                DisabledReason = disabledReason
            };
        }

        private static ParsekScenario InstallScenarioWithRp(
            RewindPoint rp, List<RecordingSupersedeRelation> supersedes = null)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static Recording MakeDisplayRec(
            double startUT, double endUT, string name, string groupName = null,
            string treeId = null, uint pid = 0, string chainId = null, int chainIndex = -1)
        {
            var rec = MakeRec(startUT, endUT, name);
            rec.TreeId = treeId;
            rec.VesselPersistentId = pid;
            rec.ChainId = chainId;
            rec.ChainIndex = chainIndex;
            if (groupName != null)
                rec.RecordingGroups = new List<string> { groupName };
            return rec;
        }

        [Fact]
        public void TryResolveUnfinishedFlightRewindPoint_NormalRowFindsRpSlot()
        {
            // Regression for the Kerbal X Probe staging case: the normal
            // recordings list row for an RP-backed unfinished child must route
            // to the child slot, not fall through to RecordingStore's tree-root
            // launch rewind.
            var probe = new Recording
            {
                RecordingId = "rec_probe",
                VesselName = "Kerbal X Probe",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp_stage"
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp_stage",
                ChildSlots = new List<ChildSlot>
                {
                    MakeSlot(0, "rec_upper"),
                    MakeSlot(1, "rec_probe")
                }
            };
            InstallScenarioWithRp(rp);

            bool resolved = RecordingsTableUI.TryResolveUnfinishedFlightRewindPoint(
                probe, out RewindPoint resolvedRp, out int slotListIndex);
            var route = RecordingsTableUI.ResolveUnfinishedFlightRewindRoute(
                probe, out RewindPoint routeRp, out int routeSlotListIndex, out string reason);

            Assert.True(resolved);
            Assert.Same(rp, resolvedRp);
            Assert.Equal(1, slotListIndex);
            Assert.Equal("rec_probe", resolvedRp.ChildSlots[slotListIndex].OriginChildRecordingId);
            Assert.Equal(RecordingsTableUI.UnfinishedFlightRewindRoute.Resolved, route);
            Assert.Same(rp, routeRp);
            Assert.Equal(1, routeSlotListIndex);
            Assert.Null(reason);
        }

        [Fact]
        public void TryResolveUnfinishedFlightRewindPoint_CommittedProvisionalRowFindsRpSlot()
        {
            var probe = new Recording
            {
                RecordingId = "rec_probe",
                VesselName = "Kerbal X Probe",
                MergeState = MergeState.CommittedProvisional,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp_stage"
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp_stage",
                ChildSlots = new List<ChildSlot>
                {
                    MakeSlot(0, "rec_probe")
                }
            };
            InstallScenarioWithRp(rp);

            bool resolved = RecordingsTableUI.TryResolveUnfinishedFlightRewindPoint(
                probe, out RewindPoint resolvedRp, out int slotListIndex);
            var route = RecordingsTableUI.ResolveUnfinishedFlightRewindRoute(
                probe, out RewindPoint routeRp, out int routeSlotListIndex, out string reason);

            Assert.True(resolved);
            Assert.Same(rp, resolvedRp);
            Assert.Equal(0, slotListIndex);
            Assert.Equal(RecordingsTableUI.UnfinishedFlightRewindRoute.Resolved, route);
            Assert.Same(rp, routeRp);
            Assert.Equal(0, routeSlotListIndex);
            Assert.Null(reason);
        }

        [Fact]
        public void TryResolveUnfinishedFlightRewindPoint_NonCrashedChildDoesNotPreemptLegacyButtons()
        {
            var landed = new Recording
            {
                RecordingId = "rec_landed",
                VesselName = "Safe Child",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed,
                ParentBranchPointId = "bp_stage"
            };
            InstallScenarioWithRp(new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp_stage",
                ChildSlots = new List<ChildSlot> { MakeSlot(0, "rec_landed") }
            });

            bool resolved = RecordingsTableUI.TryResolveUnfinishedFlightRewindPoint(
                landed, out RewindPoint resolvedRp, out int slotListIndex);

            Assert.False(resolved);
            Assert.Null(resolvedRp);
            Assert.Equal(-1, slotListIndex);
        }

        [Fact]
        public void ResolveUnfinishedFlightRewindRoute_SupersededOriginDoesNotPreemptLegacyButtons()
        {
            // The normal recordings list is backed by raw committed recordings,
            // unlike the virtual group. A superseded crashed origin may still
            // be present in that raw list, but it must not expose an RP button.
            var oldCrash = new Recording
            {
                RecordingId = "rec_old_crash",
                VesselName = "Old Booster Crash",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp_stage"
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rel_old_new",
                    OldRecordingId = "rec_old_crash",
                    NewRecordingId = "rec_new_landed",
                    UT = 200.0
                }
            };
            InstallScenarioWithRp(new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp_stage",
                ChildSlots = new List<ChildSlot> { MakeSlot(0, "rec_old_crash") }
            }, supersedes);

            var route = RecordingsTableUI.ResolveUnfinishedFlightRewindRoute(
                oldCrash, out RewindPoint rp, out int slotListIndex, out string reason);

            Assert.Equal(RecordingsTableUI.UnfinishedFlightRewindRoute.NotUnfinishedFlight, route);
            Assert.Null(rp);
            Assert.Equal(-1, slotListIndex);
            Assert.Contains("superseded", reason);
        }

        [Fact]
        public void ResolveUnfinishedFlightRewindRoute_MissingSlotDoesNotExposeUnfinishedFlightButton()
        {
            var probe = new Recording
            {
                RecordingId = "rec_probe",
                VesselName = "Kerbal X Probe",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp_stage"
            };
            InstallScenarioWithRp(new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp_stage",
                ChildSlots = new List<ChildSlot> { MakeSlot(0, "rec_upper") }
            });

            var route = RecordingsTableUI.ResolveUnfinishedFlightRewindRoute(
                probe, out RewindPoint rp, out int slotListIndex, out string reason);

            Assert.Equal(RecordingsTableUI.UnfinishedFlightRewindRoute.NotUnfinishedFlight, route);
            Assert.Null(rp);
            Assert.Equal(-1, slotListIndex);
            Assert.Contains("slot", reason);
        }

        [Fact]
        public void ResolveSlotListIndexForRecording_ReturnsListIndexNotSlotId()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_sparse",
                BranchPointId = "bp_sparse",
                ChildSlots = new List<ChildSlot>
                {
                    MakeSlot(10, "rec_upper"),
                    MakeSlot(20, "rec_probe")
                }
            };
            var probe = new Recording { RecordingId = "rec_probe" };

            int slotListIndex = RecordingsTableUI.ResolveSlotListIndexForRecording(rp, probe);

            Assert.Equal(1, slotListIndex);
        }

        [Fact]
        public void TryResolveRewindPointForRecording_ActiveParentOfBreakup_ResolvesViaChildBranchPointId()
        {
            // Review item 13 regression guard: the breakup author records
            // BOTH the surviving active parent AND each break child as
            // controllable outputs in the RP slot list (see
            // ParsekFlight.TryAuthorRewindPointForBreakup +
            // AuthorRewindPointFromVesselRecordings). The active parent
            // references the breakup branch via ChildBranchPointId (it's the
            // split it produced), not ParentBranchPointId. The UI lookup must
            // resolve the active-parent row to the same RP so its
            // Rewind-to-Staging button works after the active parent later
            // crashes. Pre-029f549a, this lookup only matched
            // ParentBranchPointId — the active-parent slot was unreachable.
            var activeParent = new Recording
            {
                RecordingId = "rec_active_parent",
                VesselName = "Kerbal X",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                // No ParentBranchPointId — active parent's branch link to the
                // breakup BP is via ChildBranchPointId only.
                ChildBranchPointId = "bp_breakup"
            };
            var breakChild = new Recording
            {
                RecordingId = "rec_break_child",
                VesselName = "Kerbal X Booster",
                MergeState = MergeState.Immutable,
                ParentBranchPointId = "bp_breakup"
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_breakup",
                BranchPointId = "bp_breakup",
                ChildSlots = new List<ChildSlot>
                {
                    MakeSlot(0, "rec_active_parent"),
                    MakeSlot(1, "rec_break_child")
                }
            };
            InstallScenarioWithRp(rp);

            bool resolvedActive = RecordingsTableUI.TryResolveRewindPointForRecording(
                activeParent, out var rpFromActive, out int slotIdxActive);
            bool resolvedChild = RecordingsTableUI.TryResolveRewindPointForRecording(
                breakChild, out var rpFromChild, out int slotIdxChild);

            Assert.True(resolvedActive);
            Assert.Same(rp, rpFromActive);
            Assert.Equal(0, slotIdxActive);
            Assert.True(resolvedChild);
            Assert.Same(rp, rpFromChild);
            Assert.Equal(1, slotIdxChild);
        }

        [Fact]
        public void CanInvokeRewindPointSlot_DisabledSlotBlocksBeforeGlobalPreconditions()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_disabled",
                ChildSlots = new List<ChildSlot>
                {
                    MakeSlot(0, "rec_probe", disabled: true, disabledReason: "no-live-vessel")
                }
            };

            bool canInvoke = RecordingsTableUI.CanInvokeRewindPointSlot(
                rp, 0, out string reason);

            Assert.False(canInvoke);
            Assert.Contains("no-live-vessel", reason);
        }

        // ── PruneStaleWatchEntries (bug #279 follow-up) ──

        [Fact]
        public void PruneStaleWatchEntries_RemovesEntriesForDeletedRecordings()
        {
            // Bug #279 follow-up: a rewound/truncated recording's id leaves
            // the committed list. The transition cache must drop the entry
            // so that (a) the dict doesn't grow unbounded over a long
            // session and (b) a future recording with a similar id (or a
            // future code path that resurrects the id) doesn't see stale
            // canWatch state from the deleted recording.
            var perRow = new Dictionary<string, bool>
            {
                ["live-1"] = true,
                ["live-2"] = false,
                ["deleted-3"] = true,
                ["deleted-4"] = false,
            };
            var perGroup = new Dictionary<string, bool>
            {
                ["GroupA/live-1"] = true,
                ["GroupB/deleted-3"] = false,
            };
            var committed = new List<Recording>
            {
                MakeRecWithId("live-1"),
                MakeRecWithId("live-2"),
            };

            RecordingsTableUI.PruneStaleWatchEntries(perRow, perGroup, committed);

            Assert.Equal(2, perRow.Count);
            Assert.True(perRow.ContainsKey("live-1"));
            Assert.True(perRow.ContainsKey("live-2"));
            Assert.False(perRow.ContainsKey("deleted-3"));
            Assert.False(perRow.ContainsKey("deleted-4"));

            Assert.Single(perGroup);
            Assert.True(perGroup.ContainsKey("GroupA/live-1"));
            Assert.False(perGroup.ContainsKey("GroupB/deleted-3"));
        }

        [Fact]
        public void PruneStaleWatchEntries_EmptyCommitted_ClearsBothDicts()
        {
            // Edge: the committed list is empty (e.g., user truncated
            // everything). Both dicts should be cleared so we don't carry
            // stale state into the next session of recordings.
            var perRow = new Dictionary<string, bool> { ["a"] = true, ["b"] = false };
            var perGroup = new Dictionary<string, bool> { ["G/a"] = true };
            var committed = new List<Recording>();

            RecordingsTableUI.PruneStaleWatchEntries(perRow, perGroup, committed);

            Assert.Empty(perRow);
            Assert.Empty(perGroup);
        }

        [Fact]
        public void PruneStaleWatchEntries_NullCommitted_ClearsBothDicts()
        {
            // Defensive: a null committed list (e.g., during a teardown
            // window) should not throw. Clear the dicts and return.
            var perRow = new Dictionary<string, bool> { ["a"] = true };
            var perGroup = new Dictionary<string, bool> { ["G/a"] = true };

            RecordingsTableUI.PruneStaleWatchEntries(perRow, perGroup, null);

            Assert.Empty(perRow);
            Assert.Empty(perGroup);
        }

        [Fact]
        public void PruneStaleWatchEntries_GroupKeyWithoutSlash_DroppedDefensively()
        {
            // Defensive: any group dict key that doesn't follow the
            // "{groupName}/{recordingId}" convention is dropped on the
            // next prune so a code-bug that produces malformed keys
            // doesn't permanently leak entries.
            var perRow = new Dictionary<string, bool>();
            var perGroup = new Dictionary<string, bool>
            {
                ["malformed-no-slash"] = true,
                ["G/live-1"] = true,
            };
            var committed = new List<Recording> { MakeRecWithId("live-1") };

            RecordingsTableUI.PruneStaleWatchEntries(perRow, perGroup, committed);

            Assert.Single(perGroup);
            Assert.True(perGroup.ContainsKey("G/live-1"));
        }

        [Fact]
        public void PruneStaleWatchEntries_NullDicts_DoesNotThrow()
        {
            // Defensive: callers may pass null dicts (e.g. during early
            // initialization before the field is assigned). Should no-op.
            var committed = new List<Recording> { MakeRecWithId("live-1") };
            RecordingsTableUI.PruneStaleWatchEntries(null, null, committed);
            // No assertion — just verifying no exception.
        }

        [Fact]
        public void PruneStaleWatchEntries_StaleGroupCursorEntries_Removed()
        {
            // Bug #382: the per-group rotation cursor dict (keyed by group
            // name, valued by last-entered RecordingId) must drop entries
            // whose stored RecordingId is no longer live. Without this, a
            // group whose previously-watched vessel is rewound away would
            // keep probing a dead id and fall back to first-eligible on
            // every draw, breaking the advance semantics.
            var cursor = new Dictionary<string, string>
            {
                ["Alpha"] = "live-id",
                ["Beta"] = "dead-id"
            };
            var committed = new List<Recording> { MakeRecWithId("live-id", "A") };
            RecordingsTableUI.PruneStaleWatchEntries(
                new Dictionary<string, bool>(),
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                cursor,
                committed);
            Assert.Single(cursor);
            Assert.Equal("live-id", cursor["Alpha"]);
            Assert.False(cursor.ContainsKey("Beta"));
        }

        [Fact]
        public void PruneStaleWatchEntries_EmptyCommitted_ClearsGroupCursor()
        {
            // Edge: empty committed list also clears the rotation cursor dict.
            var cursor = new Dictionary<string, string> { ["Alpha"] = "x" };
            RecordingsTableUI.PruneStaleWatchEntries(
                new Dictionary<string, bool>(),
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                cursor,
                new List<Recording>());
            Assert.Empty(cursor);
        }

        [Fact]
        public void WatchTransitionLogging_BothCallSitesGuardNullRecordingId_PinnedBySourceInspection()
        {
            // Bug #279 follow-up review: the per-row site already guards
            // null/empty RecordingId via the IsNullOrEmpty(watchKey) check
            // at the dict-lookup site, but a previous version of the group
            // site fell back to "{groupName}/" via the ?? "" coalesce —
            // which would have produced a spam loop when paired with
            // PruneStaleWatchEntries (cache "GroupName/" → log → prune drops
            // it because trailing recId is empty → next draw re-adds → log
            // → prune → ...). The fix mirrors the per-row guard at the
            // group site. This test pins both guards via source inspection
            // so a future refactor that moves the guard or removes it
            // produces a deliberate test failure rather than a silent log
            // spam regression.
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            // Per-row guard: the watchKey local must be routed through
            // UpdateWatchButtonTransitionCache, which guards null/empty keys
            // internally (covered by
            // UpdateWatchButtonTransitionCache_EmptyKey_DoesNotMutateCache).
            // Otherwise the empty-id case falls into the spam loop pattern.
            Assert.Contains("string watchKey = rec.RecordingId;", uiSrc);
            Assert.Contains("UpdateWatchButtonTransitionCache(lastCanWatchByRecId, watchKey", uiSrc);

            // Group guard: same shape — mainRecId must be non-empty before
            // we add to the dict. The fix replaced a "?? \"\"" coalesce with
            // an explicit IsNullOrEmpty guard, so any future re-introduction
            // of the coalesce pattern is also a regression that this assert
            // would not catch directly. We test for the explicit guard
            // instead, which is the safer pattern.
            Assert.Contains("string mainRecId = committed[mainIdx].RecordingId;", uiSrc);
            Assert.Contains("if (!string.IsNullOrEmpty(mainRecId))", uiSrc);
            // And the dangerous coalesce pattern must be GONE.
            Assert.DoesNotContain("groupName + \"/\" + (mainRecId ?? \"\")", uiSrc);
        }

        [Fact]
        public void TemporalButtons_RpBackedUnfinishedRowsPreemptLegacyRewind_PinnedBySourceInspection()
        {
            // The row-level RP route must run for normal list rows, not only
            // while unfinishedFlightRowDepth > 0, otherwise a crashed staging
            // child can inherit the tree root launch save and rewind to launch.
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            int rowStart = uiSrc.IndexOf("// Rewind / Fast-forward button", StringComparison.Ordinal);
            int rowEnd = uiSrc.IndexOf("// Hide checkbox", rowStart, StringComparison.Ordinal);
            string rowBlock = uiSrc.Substring(rowStart, rowEnd - rowStart);

            int rpRoute = rowBlock.IndexOf("DrawUnfinishedFlightRewindButton(rec, ri", StringComparison.Ordinal);
            int legacyRoute = rowBlock.IndexOf("RecordingStore.CanRewind(rec, out rewindReason", StringComparison.Ordinal);

            Assert.True(rpRoute >= 0, "Row block should try RP-backed unfinished-flight rewind first.");
            Assert.True(legacyRoute > rpRoute, "Legacy tree-root rewind must remain a fallback after RP routing.");
            Assert.DoesNotContain("unfinishedFlightRowDepth > 0 && DrawUnfinishedFlightRewindButton", rowBlock);
            Assert.Contains("DrawDisabledUnfinishedFlightRewindButton(", uiSrc);
            Assert.Contains("ResolveUnfinishedFlightRewindRoute(", uiSrc);
        }

        [Fact]
        public void TemporalButtons_RemainIndependentFromWatchState_PinnedBySourceInspection()
        {
            // T60: the watch button uses ghost presence/body/range, but R/FF must stay
            // coupled only to recording timing/save/runtime state. Pin that separation
            // in both row and group call sites so a future refactor can't silently wire
            // watch-distance variables into the temporal controls.
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            int rowStart = uiSrc.IndexOf("// Rewind / Fast-forward button", StringComparison.Ordinal);
            int rowEnd = uiSrc.IndexOf("// Hide checkbox", rowStart, StringComparison.Ordinal);
            string rowBlock = uiSrc.Substring(rowStart, rowEnd - rowStart);

            Assert.Contains("RecordingStore.CanFastForward(rec, out ffReason, isRecording: isRecording)", rowBlock);
            Assert.Contains("RecordingStore.CanRewind(rec, out rewindReason, isRecording: isRecording)", rowBlock);
            Assert.DoesNotContain("canWatch", rowBlock);
            Assert.DoesNotContain("hasGhost", rowBlock);
            Assert.DoesNotContain("sameBody", rowBlock);
            Assert.DoesNotContain("inRange", rowBlock);

            int groupStart = uiSrc.IndexOf("// Rewind / Fast-forward button — targets main recording", StringComparison.Ordinal);
            int groupEnd = uiSrc.IndexOf("// Hide group checkbox", groupStart, StringComparison.Ordinal);
            string groupBlock = uiSrc.Substring(groupStart, groupEnd - groupStart);

            Assert.Contains("RecordingStore.CanFastForward(mainRec, out ffReason, isRecording: isRecording)", groupBlock);
            Assert.Contains("RecordingStore.CanRewind(mainRec, out rewindReason, isRecording: isRecording)", groupBlock);
            Assert.DoesNotContain("canWatch", groupBlock);
            Assert.DoesNotContain("hasGhost", groupBlock);
            Assert.DoesNotContain("sameBody", groupBlock);
            Assert.DoesNotContain("inRange", groupBlock);
        }

        [Fact]
        public void GetWatchButtonReason_PrioritizesDebris()
        {
            string reason = RecordingsTableUI.GetWatchButtonReason(
                canWatch: false, hasGhost: false, sameBody: false, inRange: false, isDebris: true);

            Assert.Equal("disabled (debris)", reason);
        }

        [Fact]
        public void IsWatchButtonEnabled_RequiresGhostSameBodyRangeAndNonDebris()
        {
            Assert.True(RecordingsTableUI.IsWatchButtonEnabled(
                hasGhost: true, sameBody: true, inRange: true, isDebris: false));
            Assert.False(RecordingsTableUI.IsWatchButtonEnabled(
                hasGhost: false, sameBody: true, inRange: true, isDebris: false));
            Assert.False(RecordingsTableUI.IsWatchButtonEnabled(
                hasGhost: true, sameBody: false, inRange: true, isDebris: false));
            Assert.False(RecordingsTableUI.IsWatchButtonEnabled(
                hasGhost: true, sameBody: true, inRange: false, isDebris: false));
            Assert.False(RecordingsTableUI.IsWatchButtonEnabled(
                hasGhost: true, sameBody: true, inRange: true, isDebris: true));
        }

        [Fact]
        public void ShouldEnableWatchButton_KeepsWatchedUnavailableRowsClickable()
        {
            Assert.True(RecordingsTableUI.ShouldEnableWatchButton(
                canWatch: false, isWatching: true));
            Assert.False(RecordingsTableUI.ShouldEnableWatchButton(
                canWatch: false, isWatching: false));
        }

        [Fact]
        public void UpdateWatchButtonTransitionCache_TracksFirstChangeAndSuppressesDuplicates()
        {
            var cache = new Dictionary<string, bool>();

            Assert.True(RecordingsTableUI.UpdateWatchButtonTransitionCache(cache, "rec-1", true));
            Assert.False(RecordingsTableUI.UpdateWatchButtonTransitionCache(cache, "rec-1", true));
            Assert.True(RecordingsTableUI.UpdateWatchButtonTransitionCache(cache, "rec-1", false));
            Assert.False(cache["rec-1"]);
        }

        [Fact]
        public void UpdateWatchButtonTransitionCache_EmptyKey_DoesNotMutateCache()
        {
            var cache = new Dictionary<string, bool>();

            Assert.False(RecordingsTableUI.UpdateWatchButtonTransitionCache(cache, string.Empty, true));
            Assert.Empty(cache);
        }

        [Fact]
        public void GetWatchButtonTooltip_ExplainsNoGhost()
        {
            string tooltip = RecordingsTableUI.GetWatchButtonTooltip(
                isWatching: false, hasGhost: false, sameBody: true, inRange: true, isDebris: false);

            Assert.Contains("No active ghost", tooltip);
        }

        [Fact]
        public void GetWatchButtonTooltip_WatchingPrioritizesExit()
        {
            string tooltip = RecordingsTableUI.GetWatchButtonTooltip(
                isWatching: true, hasGhost: false, sameBody: false, inRange: false, isDebris: false);

            Assert.Equal("Exit watch mode", tooltip);
        }

        [Fact]
        public void WatchTransitionLogging_IncludesEligibilityAndFocusObservability_PinnedBySourceInspection()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            // Per-row site still uses (flight, ri).
            Assert.Contains("BuildWatchObservabilitySuffix(flight, ri)", uiSrc);
            // Bug #382: group site now passes nextTargetIdx (the cycle's next
            // vessel) instead of resolvedWatchIdx, but the 3-arg observability
            // helper signature (flight, sourceIndex, resolvedOrNextIndex) is
            // unchanged so the log still names the watched/source/next pair.
            Assert.Contains("BuildWatchObservabilitySuffix(flight, mainIdx, nextTargetIdx)", uiSrc);
            Assert.Contains("beforeFocus={beforeFocus} afterFocus={flight.DescribeWatchFocusForLogs()}", uiSrc);
        }

        [Fact]
        public void GroupWatchUsesRotationCursor_PinnedBySourceInspection()
        {
            // Bug #382: group W button no longer routes through a single
            // ResolveEffectiveWatchTargetIndex call — it builds a per-press
            // rotation via GhostPlaybackLogic.AdvanceGroupWatchCursor and
            // stores the cursor in groupWatchCursorByGroupName. Pin that
            // contract so a future refactor can't silently fall back to the
            // old "always resolve the main index" behavior. The per-row W
            // button continues to use the exact row index (no resolve) and
            // that invariant is pinned below.
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            int rowWatchStart = uiSrc.IndexOf("// Watch button (flight only)", StringComparison.Ordinal);
            int rowWatchEnd = uiSrc.IndexOf("// Rewind / Fast-forward button", rowWatchStart, StringComparison.Ordinal);
            string rowWatchBlock = uiSrc.Substring(rowWatchStart, rowWatchEnd - rowWatchStart);

            Assert.DoesNotContain("ResolveEffectiveWatchTargetIndex", rowWatchBlock);
            Assert.Contains("flight.WatchedRecordingIndex == ri", rowWatchBlock);

            int groupWatchStart = uiSrc.IndexOf("// Watch button (flight only) — Bug #382", StringComparison.Ordinal);
            int groupWatchEnd = uiSrc.IndexOf("// Rewind / Fast-forward button — targets main recording", groupWatchStart, StringComparison.Ordinal);
            string groupWatchBlock = uiSrc.Substring(groupWatchStart, groupWatchEnd - groupWatchStart);

            Assert.Contains("GhostPlaybackLogic.AdvanceGroupWatchCursor", groupWatchBlock);
            Assert.Contains("groupWatchCursorByGroupName", groupWatchBlock);
            Assert.Contains("rotation.NextRecordingId", groupWatchBlock);
            Assert.DoesNotContain("ResolveEffectiveWatchTargetIndex", groupWatchBlock);
        }

        // ── GetRecordingSortKey ──

        [Fact]
        public void GetRecordingSortKey_LaunchTime_ReturnsStartUT()
        {
            var rec = MakeRec(250, 400);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.LaunchTime, 0, 0);
            Assert.Equal(250, key);
        }

        [Fact]
        public void GetRecordingSortKey_Duration_ReturnsDuration()
        {
            var rec = MakeRec(100, 350);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Duration, 0, 0);
            Assert.Equal(250, key);
        }

        [Fact]
        public void GetRecordingSortKey_Status_Future()
        {
            var rec = MakeRec(500, 600);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Status, 100, 0);
            Assert.Equal(0, key); // future
        }

        [Fact]
        public void GetRecordingSortKey_Status_Active()
        {
            var rec = MakeRec(100, 300);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Status, 200, 0);
            Assert.Equal(1, key); // active
        }

        [Fact]
        public void GetRecordingSortKey_Status_Past()
        {
            var rec = MakeRec(100, 200);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Status, 500, 0);
            Assert.Equal(2, key); // past
        }

        [Fact]
        public void GetRecordingSortKey_Index_ReturnsRowFallback()
        {
            var rec = MakeRec(100, 200);
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Index, 0, 42);
            Assert.Equal(42, key);
        }

        [Fact]
        public void GetRecordingSortKey_Phase_ReturnsRowFallback()
        {
            var rec = MakeRec(100, 200);
            rec.SegmentPhase = "atmo";
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Phase, 0, 11);
            Assert.Equal(11, key); // Phase uses default branch = rowFallback
        }

        [Fact]
        public void GetPhaseStyleKey_SuppressedEvaBoundary_UsesLastSectionClass()
        {
            var rec = MakeRec(100, 200);
            rec.EvaCrewName = "Bill Kerman";
            rec.SegmentPhase = "exo";
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary
            });

            Assert.Equal("surface", RecordingsTableUI.GetPhaseStyleKey(rec));
        }

        [Fact]
        public void GetPhaseStyleKey_NonSuppressed_ReturnsSegmentPhase()
        {
            var rec = MakeRec(100, 200);
            rec.SegmentPhase = "exo";
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic
            });

            Assert.Equal("exo", RecordingsTableUI.GetPhaseStyleKey(rec));
        }

        [Fact]
        public void GetRecordingSortKey_Name_ReturnsRowFallback()
        {
            var rec = MakeRec(100, 200, "Vessel");
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.Name, 0, 5);
            Assert.Equal(5, key); // Name uses default branch = rowFallback
        }

        // ── GetChainSortKey ──

        [Fact]
        public void GetChainSortKey_LaunchTime_ReturnsEarliestStartUT()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, 400),
                MakeRec(150, 250),
                MakeRec(200, 350)
            };
            var members = new List<int> { 0, 1, 2 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.LaunchTime, 0);
            Assert.Equal(150, key);
        }

        [Fact]
        public void GetChainSortKey_Duration_ReturnsSumOfPositiveDurations()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 160),  // 60s
                MakeRec(200, 280),  // 80s
                MakeRec(300, 300)   // 0s (single point), not added
            };
            var members = new List<int> { 0, 1, 2 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Duration, 0);
            Assert.Equal(140, key);
        }

        [Fact]
        public void GetChainSortKey_Status_ReturnsBestAmongMembers()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),  // past at now=600
                MakeRec(400, 700),  // active at now=600
                MakeRec(800, 900)   // future at now=600
            };
            var members = new List<int> { 0, 1, 2 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Status, 600);
            Assert.Equal(0, key); // future is "best" (lowest order)
        }

        [Fact]
        public void GetChainSortKey_DefaultColumn_ReturnsZero()
        {
            var committed = new List<Recording> { MakeRec(100, 200) };
            var members = new List<int> { 0 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.Name, 0);
            Assert.Equal(0, key);
        }

        // ── GetGroupEarliestStartUT ──

        [Fact]
        public void GetGroupEarliestStartUT_EmptyDescendants_ReturnsMaxValue()
        {
            var result = ParsekUI.GetGroupEarliestStartUT(new HashSet<int>(), new List<Recording>());
            Assert.Equal(double.MaxValue, result);
        }

        [Fact]
        public void GetGroupEarliestStartUT_Single_ReturnsStartUT()
        {
            var committed = new List<Recording> { MakeRec(500, 600) };
            var result = ParsekUI.GetGroupEarliestStartUT(new HashSet<int> { 0 }, committed);
            Assert.Equal(500, result);
        }

        [Fact]
        public void GetGroupEarliestStartUT_Multiple_ReturnsMinimum()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, 400),
                MakeRec(100, 200),
                MakeRec(200, 300)
            };
            var result = ParsekUI.GetGroupEarliestStartUT(new HashSet<int> { 0, 1, 2 }, committed);
            Assert.Equal(100, result);
        }

        // ── GetGroupTotalDuration ──

        [Fact]
        public void GetGroupTotalDuration_EmptyDescendants_ReturnsZero()
        {
            var result = ParsekUI.GetGroupTotalDuration(new HashSet<int>(), new List<Recording>());
            Assert.Equal(0, result);
        }

        [Fact]
        public void GetGroupTotalDuration_SingleRecording_ReturnsDuration()
        {
            var committed = new List<Recording> { MakeRec(100, 250) };
            var result = ParsekUI.GetGroupTotalDuration(new HashSet<int> { 0 }, committed);
            Assert.Equal(150, result);
        }

        [Fact]
        public void GetGroupTotalDuration_SkipsZeroDuration()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 100),  // 0s (single point, EndUT==StartUT)
                MakeRec(200, 350)   // 150s
            };
            var result = ParsekUI.GetGroupTotalDuration(new HashSet<int> { 0, 1 }, committed);
            Assert.Equal(150, result);
        }

        [Fact]
        public void GetGroupTotalDuration_SumsAllPositive()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),  // 100s
                MakeRec(300, 420),  // 120s
                MakeRec(500, 530)   // 30s
            };
            var result = ParsekUI.GetGroupTotalDuration(new HashSet<int> { 0, 1, 2 }, committed);
            Assert.Equal(250, result);
        }

        // ── FindGroupMainRecordingIndex ──

        [Fact]
        public void FindGroupMainRecordingIndex_EmptyDescendants_ReturnsNegativeOne()
        {
            Assert.Equal(-1, ParsekUI.FindGroupMainRecordingIndex(
                new HashSet<int>(), new List<Recording>()));
        }

        [Fact]
        public void FindGroupMainRecordingIndex_SingleNonDebris_ReturnsThatIndex()
        {
            var committed = new List<Recording> { MakeRec(100, 200, "Vessel") };
            Assert.Equal(0, ParsekUI.FindGroupMainRecordingIndex(
                new HashSet<int> { 0 }, committed));
        }

        [Fact]
        public void FindGroupMainRecordingIndex_AllDebris_ReturnsNegativeOne()
        {
            var d1 = MakeRec(100, 200, "Stage1"); d1.IsDebris = true;
            var d2 = MakeRec(150, 250, "Stage2"); d2.IsDebris = true;
            var committed = new List<Recording> { d1, d2 };
            Assert.Equal(-1, ParsekUI.FindGroupMainRecordingIndex(
                new HashSet<int> { 0, 1 }, committed));
        }

        [Fact]
        public void FindGroupMainRecordingIndex_MixedTypes_ReturnsEarliestNonDebris()
        {
            var debris = MakeRec(50, 100, "Booster"); debris.IsDebris = true;
            var laterVessel = MakeRec(200, 300, "Lander");
            var earlierVessel = MakeRec(100, 200, "Rocket");
            var committed = new List<Recording> { debris, laterVessel, earlierVessel };
            Assert.Equal(2, ParsekUI.FindGroupMainRecordingIndex(
                new HashSet<int> { 0, 1, 2 }, committed));
        }

        [Fact]
        public void FindGroupMainRecordingIndex_OutOfRangeIndex_Skipped()
        {
            var committed = new List<Recording> { MakeRec(100, 200, "Vessel") };
            // Index 5 is out of range, should be skipped without crash
            Assert.Equal(0, ParsekUI.FindGroupMainRecordingIndex(
                new HashSet<int> { 0, 5 }, committed));
        }

        // ── GetGroupStatus ──

        [Fact]
        public void GetGroupStatus_EmptyDescendants_ReturnsDash()
        {
            ParsekUI.GetGroupStatus(new HashSet<int>(), new List<Recording>(),
                500, out string text, out int order);
            Assert.Equal("-", text);
            Assert.Equal(2, order);
        }

        [Fact]
        public void GetGroupStatus_AllFuture_ReturnsFutureOrder()
        {
            var committed = new List<Recording>
            {
                MakeRec(700, 800),
                MakeRec(600, 700)
            };
            ParsekUI.GetGroupStatus(new HashSet<int> { 0, 1 }, committed,
                500, out string text, out int order);
            Assert.Equal(0, order);
        }

        [Fact]
        public void GetGroupStatus_ActivePresent_ReturnsActiveOrder()
        {
            var committed = new List<Recording>
            {
                MakeRec(400, 600),  // active at now=500
                MakeRec(100, 200)   // past
            };
            ParsekUI.GetGroupStatus(new HashSet<int> { 0, 1 }, committed,
                500, out string text, out int order);
            Assert.Equal(1, order);
        }

        [Fact]
        public void GetGroupStatus_AllPast_ReturnsPast()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),
                MakeRec(250, 350)
            };
            ParsekUI.GetGroupStatus(new HashSet<int> { 0, 1 }, committed,
                500, out string text, out int order);
            Assert.Equal(2, order);
            Assert.Equal("past", text);
        }

        // ── GetGroupSortKey ──

        [Fact]
        public void GetGroupSortKey_EmptyDescendants_ReturnsMaxValue()
        {
            double key = ParsekUI.GetGroupSortKey(new HashSet<int>(), new List<Recording>(),
                ParsekUI.SortColumn.LaunchTime, 0);
            Assert.Equal(double.MaxValue, key);
        }

        [Fact]
        public void GetGroupSortKey_LaunchTime_DelegatesToEarliestStartUT()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, 400),
                MakeRec(150, 250)
            };
            double key = ParsekUI.GetGroupSortKey(new HashSet<int> { 0, 1 }, committed,
                ParsekUI.SortColumn.LaunchTime, 0);
            Assert.Equal(150, key);
        }

        [Fact]
        public void GetGroupSortKey_Duration_DelegatesToTotalDuration()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, 200),  // 100s
                MakeRec(300, 370)   // 70s
            };
            double key = ParsekUI.GetGroupSortKey(new HashSet<int> { 0, 1 }, committed,
                ParsekUI.SortColumn.Duration, 0);
            Assert.Equal(170, key);
        }

        [Fact]
        public void GetGroupSortKey_Status_DelegatesToGetGroupStatus()
        {
            var committed = new List<Recording>
            {
                MakeRec(400, 600)   // active at now=500
            };
            double key = ParsekUI.GetGroupSortKey(new HashSet<int> { 0 }, committed,
                ParsekUI.SortColumn.Status, 500);
            Assert.Equal(1, key);
        }

        [Fact]
        public void GetGroupSortKey_Name_ReturnsZero()
        {
            var committed = new List<Recording> { MakeRec(100, 200) };
            double key = ParsekUI.GetGroupSortKey(new HashSet<int> { 0 }, committed,
                ParsekUI.SortColumn.Name, 0);
            Assert.Equal(0, key);
        }

        [Fact]
        public void GetGroupSortKey_Phase_ReturnsZero()
        {
            var committed = new List<Recording> { MakeRec(100, 200) };
            double key = ParsekUI.GetGroupSortKey(new HashSet<int> { 0 }, committed,
                ParsekUI.SortColumn.Phase, 0);
            Assert.Equal(0, key);
        }

        [Fact]
        public void GetGroupSortKey_Index_ReturnsNegativeOne()
        {
            var committed = new List<Recording> { MakeRec(100, 200) };
            double key = ParsekUI.GetGroupSortKey(new HashSet<int> { 0 }, committed,
                ParsekUI.SortColumn.Index, 0);
            Assert.Equal(-1, key);
        }

        // ── UnitSuffix ──

        [Theory]
        [InlineData(LoopTimeUnit.Sec, "s")]
        [InlineData(LoopTimeUnit.Min, "m")]
        [InlineData(LoopTimeUnit.Hour, "h")]
        [InlineData(LoopTimeUnit.Auto, "s")]  // Auto falls through to default "s"
        public void UnitSuffix_ReturnsCorrectSuffix(LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.UnitSuffix(unit));
        }

        // ── CycleRecordingUnit ──

        [Fact]
        public void CycleRecordingUnit_FullCycle()
        {
            var u = LoopTimeUnit.Sec;
            u = ParsekUI.CycleRecordingUnit(u);
            Assert.Equal(LoopTimeUnit.Min, u);

            u = ParsekUI.CycleRecordingUnit(u);
            Assert.Equal(LoopTimeUnit.Hour, u);

            u = ParsekUI.CycleRecordingUnit(u);
            Assert.Equal(LoopTimeUnit.Auto, u);

            u = ParsekUI.CycleRecordingUnit(u);
            Assert.Equal(LoopTimeUnit.Sec, u);
        }

        [Fact]
        public void ComputeDisplayedLoopPeriod_StoredAboveFloor_NotClamped()
        {
            bool clamped;
            double effective = RecordingsTableUI.ComputeDisplayedLoopPeriod(
                storedSeconds: 30.0, loopDurationSeconds: 60.0, cap: 10, out clamped);

            Assert.Equal(30.0, effective);
            Assert.False(clamped);
        }

        [Fact]
        public void ComputeDisplayedLoopPeriod_StoredBelowFloor_ClampedAndFlagged()
        {
            bool clamped;
            double effective = RecordingsTableUI.ComputeDisplayedLoopPeriod(
                storedSeconds: 5.0, loopDurationSeconds: 267.78, cap: 10, out clamped);

            Assert.Equal(26.778, effective, 6);
            Assert.True(clamped);
        }

        [Fact]
        public void ComputeDisplayedLoopPeriod_ZeroDuration_ReturnsStored_NotClamped()
        {
            bool clamped;
            double effective = RecordingsTableUI.ComputeDisplayedLoopPeriod(
                storedSeconds: 30.0, loopDurationSeconds: 0.0, cap: 10, out clamped);

            Assert.Equal(30.0, effective);
            Assert.False(clamped);
        }

        [Fact]
        public void FormatLoopPeriodDisplayText_ClampedSeconds_ShowsFractionalCadence()
        {
            string text = RecordingsTableUI.FormatLoopPeriodDisplayText(
                displayedSeconds: 26.778, unit: LoopTimeUnit.Sec, preserveSecondResolution: true);

            Assert.Equal("26.778", text);
        }

        [Fact]
        public void FormatLoopPeriodDisplayText_ClampedSeconds_NearIntegerPreservesPrecision()
        {
            string text = RecordingsTableUI.FormatLoopPeriodDisplayText(
                displayedSeconds: 10.00001, unit: LoopTimeUnit.Sec, preserveSecondResolution: true);

            Assert.Equal("10.00001", text);
        }

        [Fact]
        public void FormatLoopPeriodDisplayText_ClampedMinutes_UsesExtraPrecision()
        {
            string text = RecordingsTableUI.FormatLoopPeriodDisplayText(
                displayedSeconds: 26.778, unit: LoopTimeUnit.Min, preserveSecondResolution: true);

            Assert.Equal("0.4463", text);
        }

        [Fact]
        public void FormatLoopPeriodDisplayText_ClampedHours_UsesExtraPrecision()
        {
            string text = RecordingsTableUI.FormatLoopPeriodDisplayText(
                displayedSeconds: 26.778, unit: LoopTimeUnit.Hour, preserveSecondResolution: true);

            Assert.Equal("0.007438", text);
        }

        [Fact]
        public void FormatLoopPeriodEditStartText_UsesStoredRawValue()
        {
            string text = RecordingsTableUI.FormatLoopPeriodEditStartText(
                storedSeconds: 5.0, unit: LoopTimeUnit.Sec);

            Assert.Equal("5", text);
        }

        [Fact]
        public void FormatLoopPeriodEditStartText_Minutes_RoundTripsStoredRawValue()
        {
            string text = RecordingsTableUI.FormatLoopPeriodEditStartText(
                storedSeconds: 5.0, unit: LoopTimeUnit.Min);

            Assert.True(ParsekUI.TryParseLoopInput(text, LoopTimeUnit.Min, out double parsed));
            Assert.Equal(5.0, ParsekUI.ConvertToSeconds(parsed, LoopTimeUnit.Min), 9);
        }

        [Fact]
        public void FormatLoopPeriodEditStartText_Hours_RoundTripsStoredRawValue()
        {
            string text = RecordingsTableUI.FormatLoopPeriodEditStartText(
                storedSeconds: 26.778, unit: LoopTimeUnit.Hour);

            Assert.True(ParsekUI.TryParseLoopInput(text, LoopTimeUnit.Hour, out double parsed));
            Assert.Equal(26.778, ParsekUI.ConvertToSeconds(parsed, LoopTimeUnit.Hour), 9);
        }

        [Fact]
        public void FormatLoopPeriodEditStartText_InvalidMinutes_FallsBackToExactMinCycle()
        {
            string text = RecordingsTableUI.FormatLoopPeriodEditStartText(
                storedSeconds: double.NaN, unit: LoopTimeUnit.Min);

            Assert.True(ParsekUI.TryParseLoopInput(text, LoopTimeUnit.Min, out double parsed));
            Assert.Equal(5.0, ParsekUI.ConvertToSeconds(parsed, LoopTimeUnit.Min), 9);
        }

        [Fact]
        public void BuildLoopPeriodClampTooltip_ContainsKeyNumbers()
        {
            string tooltip = RecordingsTableUI.BuildLoopPeriodClampTooltip(
                storedSeconds: 5.0, effectiveSeconds: 26.778,
                loopDurationSeconds: 267.78, cap: 10);

            Assert.Contains("26.778", tooltip);
            Assert.Contains("<= 10", tooltip);
            Assert.Contains("requested: 5", tooltip);
            Assert.Contains("duration: 267.78", tooltip);
        }

        [Fact]
        public void BuildLoopPeriodClampTooltip_MinCycleOnly_DoesNotMentionCap()
        {
            string tooltip = RecordingsTableUI.BuildLoopPeriodClampTooltip(
                storedSeconds: 1.0, effectiveSeconds: 5.0,
                loopDurationSeconds: 6.0, cap: 10);

            Assert.Contains("minimum period is 5", tooltip);
            Assert.DoesNotContain("<= 10", tooltip);
        }

        // ── ApplyAutoLoopRange ──

        [Fact]
        public void ApplyAutoLoopRange_Enable_WithTrimmableSections_SetsRange()
        {
            var rec = new Recording
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection { environment = SegmentEnvironment.SurfaceStationary, startUT = 50, endUT = 100 },
                    new TrackSection { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 },
                    new TrackSection { environment = SegmentEnvironment.ExoBallistic, startUT = 200, endUT = 500 }
                }
            };
            ParsekUI.ApplyAutoLoopRange(rec, true);
            Assert.Equal(100, rec.LoopStartUT);
            Assert.Equal(200, rec.LoopEndUT);
        }

        [Fact]
        public void ApplyAutoLoopRange_Enable_NoTrimmableSections_LeavesNaN()
        {
            var rec = new Recording
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 },
                    new TrackSection { environment = SegmentEnvironment.ExoPropulsive, startUT = 200, endUT = 300 }
                }
            };
            ParsekUI.ApplyAutoLoopRange(rec, true);
            Assert.True(double.IsNaN(rec.LoopStartUT));
            Assert.True(double.IsNaN(rec.LoopEndUT));
        }

        [Fact]
        public void ApplyAutoLoopRange_Disable_ClearsExistingRange()
        {
            var rec = new Recording
            {
                LoopStartUT = 100,
                LoopEndUT = 200
            };
            ParsekUI.ApplyAutoLoopRange(rec, false);
            Assert.True(double.IsNaN(rec.LoopStartUT));
            Assert.True(double.IsNaN(rec.LoopEndUT));
        }

        [Fact]
        public void ApplyAutoLoopRange_Disable_AlreadyNaN_NoLogEmitted()
        {
            var rec = new Recording(); // LoopStartUT/EndUT default to NaN
            logLines.Clear();
            ParsekUI.ApplyAutoLoopRange(rec, false);
            // No "Loop range cleared" log because they were already NaN
            Assert.DoesNotContain(logLines, l => l.Contains("Loop range cleared"));
        }

        [Fact]
        public void ApplyAutoLoopRange_Enable_LogsAutoRange()
        {
            ParsekLog.SuppressLogging = false;
            var rec = new Recording
            {
                VesselName = "TestShip",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection { environment = SegmentEnvironment.SurfaceStationary, startUT = 50, endUT = 100 },
                    new TrackSection { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 },
                    new TrackSection { environment = SegmentEnvironment.ExoBallistic, startUT = 200, endUT = 500 }
                }
            };
            rec.Points.Add(new TrajectoryPoint { ut = 50 });
            rec.Points.Add(new TrajectoryPoint { ut = 500 });
            logLines.Clear();
            ParsekUI.ApplyAutoLoopRange(rec, true);
            Assert.Contains(logLines, l => l.Contains("Auto loop range") && l.Contains("TestShip"));
        }

        // ── BuildGroupTreeData ──

        [Fact]
        public void BuildGroupTreeData_EmptyInput_ProducesEmptyOutputs()
        {
            ParsekUI.BuildGroupTreeData(
                new List<Recording>(), new int[0], new List<string>(),
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Empty(grpToRecs);
            Assert.Empty(chainToRecs);
            Assert.Empty(grpChildren);
            Assert.Empty(rootGrps);
            Assert.Empty(rootChainIds);
        }

        [Fact]
        public void BuildGroupTreeData_UngroupedRecording_NotInAnyGroup()
        {
            var committed = new List<Recording> { new Recording { VesselName = "Solo" } };
            ParsekUI.BuildGroupTreeData(
                committed, new int[] { 0 }, new List<string>(),
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Empty(grpToRecs);
            Assert.Empty(rootGrps);
        }

        [Fact]
        public void BuildGroupTreeData_GroupedRecording_AppearsInGroup()
        {
            var rec = new Recording
            {
                VesselName = "Rocket",
                RecordingGroups = new List<string> { "Launch" }
            };
            ParsekUI.BuildGroupTreeData(
                new List<Recording> { rec }, new int[] { 0 }, new List<string>(),
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.True(grpToRecs.ContainsKey("Launch"));
            Assert.Contains(0, grpToRecs["Launch"]);
            Assert.Contains("Launch", rootGrps);
        }

        [Fact]
        public void BuildGroupTreeData_ChainWithNoGroups_IsRootChain()
        {
            var committed = new List<Recording>
            {
                new Recording { VesselName = "Seg0", ChainId = "chain-1", ChainIndex = 0 },
                new Recording { VesselName = "Seg1", ChainId = "chain-1", ChainIndex = 1 }
            };
            ParsekUI.BuildGroupTreeData(
                committed, new int[] { 0, 1 }, new List<string>(),
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.True(chainToRecs.ContainsKey("chain-1"));
            Assert.Equal(2, chainToRecs["chain-1"].Count);
            Assert.Contains("chain-1", rootChainIds);
        }

        [Fact]
        public void BuildGroupTreeData_ChainWithGroupMember_NotRootChain()
        {
            var committed = new List<Recording>
            {
                new Recording
                {
                    VesselName = "Seg0", ChainId = "chain-g", ChainIndex = 0,
                    RecordingGroups = new List<string> { "Flights" }
                },
                new Recording { VesselName = "Seg1", ChainId = "chain-g", ChainIndex = 1 }
            };
            ParsekUI.BuildGroupTreeData(
                committed, new int[] { 0, 1 }, new List<string>(),
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.DoesNotContain("chain-g", rootChainIds);
        }

        [Fact]
        public void BuildGroupDisplayBlocks_TreeVesselMergesStandaloneAndChainSegments()
        {
            const string groupName = "Kerbal X";
            var committed = new List<Recording>
            {
                MakeDisplayRec(10, 20, "Kerbal X", groupName, treeId: "tree-kx", pid: 42),
                MakeDisplayRec(20, 30, "Kerbal X", groupName, treeId: "tree-kx", pid: 42),
                MakeDisplayRec(30, 40, "Kerbal X", groupName, treeId: "tree-kx", pid: 42),
                MakeDisplayRec(40, 50, "Kerbal X", groupName, treeId: "tree-kx", pid: 42, chainId: "chain-kx", chainIndex: 0),
                MakeDisplayRec(50, 60, "Kerbal X", groupName, treeId: "tree-kx", pid: 42, chainId: "chain-kx", chainIndex: 1),
                MakeDisplayRec(60, 70, "Kerbal X", groupName, treeId: "tree-kx", pid: 42),
            };

            var blocks = RecordingsTableUI.BuildGroupDisplayBlocks(
                groupName,
                new List<int> { 0, 1, 2, 3, 4, 5 },
                committed,
                new Dictionary<string, List<int>>
                {
                    { "chain-kx", new List<int> { 3, 4 } }
                });

            var block = Assert.Single(blocks);
            Assert.Equal("Kerbal X", block.DisplayName);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, block.Members);
        }

        [Fact]
        public void BuildGroupDisplayBlocks_CrewGroupKeepsSingletonCrewRowsFlat()
        {
            const string groupName = "Kerbal X / Crew";
            var committed = new List<Recording>
            {
                MakeDisplayRec(10, 20, "Bob Kerman", groupName, treeId: "tree-kx", pid: 100),
                MakeDisplayRec(20, 30, "Bill Kerman", groupName, treeId: "tree-kx", pid: 200),
                MakeDisplayRec(30, 40, "Jebediah Kerman", groupName, treeId: "tree-kx", pid: 300, chainId: "chain-jeb", chainIndex: 0),
                MakeDisplayRec(40, 50, "Jebediah Kerman", groupName, treeId: "tree-kx", pid: 300, chainId: "chain-jeb", chainIndex: 1),
            };

            var blocks = RecordingsTableUI.BuildGroupDisplayBlocks(
                groupName,
                new List<int> { 0, 1, 2, 3 },
                committed,
                new Dictionary<string, List<int>>
                {
                    { "chain-jeb", new List<int> { 2, 3 } }
                });

            Assert.Equal(3, blocks.Count);
            Assert.Equal(new[] { 0 }, blocks[0].Members);
            Assert.Equal(new[] { 1 }, blocks[1].Members);
            Assert.Equal(new[] { 2, 3 }, blocks[2].Members);
        }

        [Fact]
        public void BuildGroupDisplayBlocks_GroupedChainKeepsFullChainVisible()
        {
            const string groupName = "Flights";
            var committed = new List<Recording>
            {
                MakeDisplayRec(10, 20, "Seg0", groupName, chainId: "chain-1", chainIndex: 0),
                MakeDisplayRec(20, 30, "Seg1", chainId: "chain-1", chainIndex: 1),
            };

            var blocks = RecordingsTableUI.BuildGroupDisplayBlocks(
                groupName,
                new List<int> { 0 },
                committed,
                new Dictionary<string, List<int>>
                {
                    { "chain-1", new List<int> { 0, 1 } }
                });

            var block = Assert.Single(blocks);
            Assert.Equal(new[] { 0, 1 }, block.Members);
        }

        [Fact]
        public void BuildGroupDisplayBlocks_GroupedLaterChainSegmentKeepsChainHeadFirst()
        {
            const string groupName = "Flights";
            var committed = new List<Recording>
            {
                MakeDisplayRec(10, 20, "Seg0", chainId: "chain-late", chainIndex: 0),
                MakeDisplayRec(20, 30, "Seg1", groupName, chainId: "chain-late", chainIndex: 1),
            };

            var blocks = RecordingsTableUI.BuildGroupDisplayBlocks(
                groupName,
                new List<int> { 1 },
                committed,
                new Dictionary<string, List<int>>
                {
                    { "chain-late", new List<int> { 0, 1 } }
                });

            var block = Assert.Single(blocks);
            Assert.Equal("Seg0", block.DisplayName);
            Assert.Equal(new[] { 0, 1 }, block.Members);
        }

        // ── LaunchSite sorting ──

        private static Recording MakeRecWithSite(double startUT, double endUT, string site, string name = "Test")
        {
            var rec = MakeRec(startUT, endUT, name);
            rec.LaunchSiteName = site;
            return rec;
        }

        [Fact]
        public void CompareRecordings_LaunchSite_SortsBySiteName()
        {
            var ra = MakeRecWithSite(100, 200, "Runway");
            var rb = MakeRecWithSite(100, 200, "LaunchPad");
            int cmp = ParsekUI.CompareRecordings(ra, rb,
                ParsekUI.SortColumn.LaunchSite, true, 0);
            Assert.True(cmp > 0); // Runway > LaunchPad alphabetically
        }

        [Fact]
        public void CompareRecordings_LaunchSite_SameSite_TiebreaksByUT()
        {
            var ra = MakeRecWithSite(300, 400, "LaunchPad");
            var rb = MakeRecWithSite(100, 200, "LaunchPad");
            int cmp = ParsekUI.CompareRecordings(ra, rb,
                ParsekUI.SortColumn.LaunchSite, true, 0);
            Assert.True(cmp > 0); // Same site, ra has later StartUT
        }

        [Fact]
        public void CompareRecordings_LaunchSite_NullSite_SortsBeforeNamed()
        {
            var ra = MakeRec(100, 200); // no site (null)
            var rb = MakeRecWithSite(100, 200, "LaunchPad");
            int cmp = ParsekUI.CompareRecordings(ra, rb,
                ParsekUI.SortColumn.LaunchSite, true, 0);
            Assert.True(cmp < 0); // "" < "LaunchPad"
        }

        [Fact]
        public void CompareRecordings_LaunchSite_CaseInsensitive()
        {
            var ra = MakeRecWithSite(100, 200, "launchpad");
            var rb = MakeRecWithSite(100, 200, "LaunchPad");
            int cmp = ParsekUI.CompareRecordings(ra, rb,
                ParsekUI.SortColumn.LaunchSite, true, 0);
            // Same site name (case-insensitive), same UT → tiebreak returns 0
            Assert.Equal(0, cmp);
        }

        [Fact]
        public void CompareRecordings_LaunchSite_Descending_ReversesOrder()
        {
            var ra = MakeRecWithSite(100, 200, "LaunchPad");
            var rb = MakeRecWithSite(100, 200, "Runway");
            int ascCmp = ParsekUI.CompareRecordings(ra, rb,
                ParsekUI.SortColumn.LaunchSite, true, 0);
            int descCmp = ParsekUI.CompareRecordings(ra, rb,
                ParsekUI.SortColumn.LaunchSite, false, 0);
            Assert.Equal(-ascCmp, descCmp);
        }

        [Fact]
        public void BuildSortedIndices_LaunchSite_GroupsBySiteThenUT()
        {
            var committed = new List<Recording>
            {
                MakeRecWithSite(200, 300, "Runway", "R2"),
                MakeRecWithSite(100, 200, "LaunchPad", "L1"),
                MakeRecWithSite(300, 400, "LaunchPad", "L2"),
                MakeRecWithSite(100, 200, "Runway", "R1")
            };
            var indices = ParsekUI.BuildSortedIndices(committed,
                ParsekUI.SortColumn.LaunchSite, true, 0);
            // Expected order: LaunchPad(UT=100), LaunchPad(UT=300), Runway(UT=100), Runway(UT=200)
            Assert.Equal(1, indices[0]); // L1 (LaunchPad, UT=100)
            Assert.Equal(2, indices[1]); // L2 (LaunchPad, UT=300)
            Assert.Equal(3, indices[2]); // R1 (Runway, UT=100)
            Assert.Equal(0, indices[3]); // R2 (Runway, UT=200)
        }

        [Fact]
        public void GetRecordingSortKey_LaunchSite_ReturnsRowFallback()
        {
            var rec = MakeRecWithSite(100, 200, "LaunchPad");
            double key = ParsekUI.GetRecordingSortKey(rec, ParsekUI.SortColumn.LaunchSite, 0, 7);
            Assert.Equal(7, key); // string-based column, returns rowFallback
        }

        [Fact]
        public void GetChainSortKey_LaunchSite_ReturnsZero()
        {
            var committed = new List<Recording> { MakeRecWithSite(100, 200, "LaunchPad") };
            var members = new List<int> { 0 };
            double key = ParsekUI.GetChainSortKey(members, committed,
                ParsekUI.SortColumn.LaunchSite, 0);
            Assert.Equal(0, key);
        }

        [Fact]
        public void GetGroupSortKey_LaunchSite_ReturnsZero()
        {
            var committed = new List<Recording> { MakeRecWithSite(100, 200, "LaunchPad") };
            double key = ParsekUI.GetGroupSortKey(new HashSet<int> { 0 }, committed,
                ParsekUI.SortColumn.LaunchSite, 0);
            Assert.Equal(0, key);
        }

        // ── FilterUnfinishedFlightRowsForRegularTree (todo item 19) ──

        [Fact]
        public void FilterUnfinishedFlightRowsForRegularTree_RemovesUFMembers_AndLogs()
        {
            // Regression: a tree's auto-generated root group must not render
            // an Unfinished Flight row directly when the nested virtual UF
            // subgroup is also being drawn — the duplicate appearance was the
            // 2026-04-25_1047 playtest's "UF appears as main mission also".
            // The trim helper preserves non-UF members and logs a Verbose
            // summary keyed by group name.
            var ufRec = new Recording
            {
                RecordingId = "rec_uf",
                VesselName = "Kerbal X Probe",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp_breakup",
                TreeId = "tree_X"
            };
            ufRec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            var normalRec = new Recording
            {
                RecordingId = "rec_normal",
                VesselName = "Kerbal X",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Recovered,
                TreeId = "tree_X"
            };
            normalRec.Points.Add(new TrajectoryPoint { ut = 0.0 });
            normalRec.Points.Add(new TrajectoryPoint { ut = 50.0 });

            // Install a scenario whose RewindPoints make ufRec satisfy
            // IsUnfinishedFlight (terminal=Destroyed AND matching RP).
            var rp = new RewindPoint
            {
                RewindPointId = "rp_breakup",
                BranchPointId = "bp_breakup",
                ChildSlots = new List<ChildSlot> { MakeSlot(0, "rec_uf") }
            };
            InstallScenarioWithRp(rp);

            var committed = new List<Recording> { normalRec, ufRec };
            var directMembers = new List<int> { 0, 1 };

            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();

            var displayMembers = RecordingsTableUI.FilterUnfinishedFlightRowsForRegularTree(
                directMembers, committed, "Kerbal X");

            Assert.NotNull(displayMembers);
            Assert.Single(displayMembers);
            Assert.Equal(0, displayMembers[0]); // normalRec survived; ufRec removed
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("filtered 1 UF row(s) from regular tree 'Kerbal X'"));
        }

        [Fact]
        public void FilterUnfinishedFlightRowsForRegularTree_NoUFMembers_ReturnsInputUnchanged()
        {
            // Regression: in the common (no-UF) case the helper must not
            // allocate or log — DrawGroupTree calls this on every frame for
            // every visible tree group. Returning the same list saves
            // allocations and the log silence keeps the audit trail clean.
            var rec1 = new Recording
            {
                RecordingId = "rec_1",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Recovered,
                TreeId = "tree_X"
            };
            rec1.Points.Add(new TrajectoryPoint { ut = 0.0 });
            rec1.Points.Add(new TrajectoryPoint { ut = 50.0 });
            var rec2 = new Recording
            {
                RecordingId = "rec_2",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed,
                TreeId = "tree_X"
            };
            rec2.Points.Add(new TrajectoryPoint { ut = 0.0 });
            rec2.Points.Add(new TrajectoryPoint { ut = 75.0 });

            // No RewindPoints → no UF members.
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();

            var committed = new List<Recording> { rec1, rec2 };
            var directMembers = new List<int> { 0, 1 };

            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();

            var displayMembers = RecordingsTableUI.FilterUnfinishedFlightRowsForRegularTree(
                directMembers, committed, "Kerbal X");

            Assert.NotNull(displayMembers);
            Assert.Equal(2, displayMembers.Count);
            Assert.Equal(0, displayMembers[0]);
            Assert.Equal(1, displayMembers[1]);
            // The "filtered N UF row(s)" log line must NOT appear when no
            // members were removed — otherwise the audit trail is noisy.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("filtered"));
        }

        [Fact]
        public void FilterUnfinishedFlightRowsForRegularTree_OutOfRangeIndices_PassThroughUnchanged()
        {
            // Defensive: a malformed grpToRecs (negative or out-of-bounds
            // index) must not silently disappear behind the UF filter.
            // Pass-through preserves visibility at the row layer where the
            // bug becomes obvious instead of being swallowed here.
            var rec = new Recording
            {
                RecordingId = "rec_1",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 0.0 });
            var committed = new List<Recording> { rec };
            var directMembers = new List<int> { -1, 0, 99 };

            var displayMembers = RecordingsTableUI.FilterUnfinishedFlightRowsForRegularTree(
                directMembers, committed, "Kerbal X");

            Assert.NotNull(displayMembers);
            Assert.Equal(3, displayMembers.Count);
            Assert.Contains(-1, displayMembers);
            Assert.Contains(0, displayMembers);
            Assert.Contains(99, displayMembers);
        }

        [Fact]
        public void FilterUnfinishedFlightRowsForRegularTree_NullDirectMembers_ReturnsNull()
        {
            // Null-safe: callers in DrawGroupTree pass through a TryGetValue
            // result that may be null when the group has no direct members.
            var displayMembers = RecordingsTableUI.FilterUnfinishedFlightRowsForRegularTree(
                null, new List<Recording>(), "Kerbal X");
            Assert.Null(displayMembers);
        }

        [Fact]
        public void CompareRootItemsForSort_GloopsGroupAlwaysSortsFirst()
        {
            foreach (RecordingsTableUI.SortColumn column in Enum.GetValues(typeof(RecordingsTableUI.SortColumn)))
            {
                foreach (bool ascending in new[] { true, false })
                {
                    var rootItems = new List<(bool IsGroup, string GroupName, string SortName, double SortKey)>
                    {
                        (true, "Manual Group", "Zulu", 999.0),
                        (false, null, "Chain Alpha", -100.0),
                        (true, RecordingStore.GloopsGroupName, "Ghosts", 50.0),
                        (false, null, "Standalone", -200.0)
                    };

                    rootItems.Sort((a, b) => RecordingsTableUI.CompareRootItemsForSort(
                        a.IsGroup, a.GroupName, a.SortName, a.SortKey,
                        b.IsGroup, b.GroupName, b.SortName, b.SortKey,
                        column, ascending));

                    Assert.True(rootItems[0].IsGroup);
                    Assert.Equal(RecordingStore.GloopsGroupName, rootItems[0].GroupName);
                }
            }
        }
    }
}
