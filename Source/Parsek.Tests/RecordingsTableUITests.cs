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

            // Per-row guard: the watchKey local must be tested for null/empty
            // BEFORE the cache lookup, otherwise the empty-id case falls into
            // the spam loop pattern.
            Assert.Contains("string watchKey = rec.RecordingId;", uiSrc);
            Assert.Contains("!string.IsNullOrEmpty(watchKey)", uiSrc);

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
        public void GetWatchButtonTooltip_ExplainsNoGhost()
        {
            string tooltip = RecordingsTableUI.GetWatchButtonTooltip(
                isWatching: false, hasGhost: false, sameBody: true, inRange: true, isDebris: false);

            Assert.Contains("No active ghost", tooltip);
        }

        [Fact]
        public void WatchTransitionLogging_IncludesEligibilityAndFocusObservability_PinnedBySourceInspection()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            Assert.Contains("BuildWatchObservabilitySuffix(flight, ri)", uiSrc);
            Assert.Contains("BuildWatchObservabilitySuffix(flight, mainIdx, resolvedWatchIdx)", uiSrc);
            Assert.Contains("beforeFocus={beforeFocus} afterFocus={flight.DescribeWatchFocusForLogs()}", uiSrc);
        }

        [Fact]
        public void GroupWatchUsesResolvedTargetWhileRowsStayExact_PinnedBySourceInspection()
        {
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

            int groupWatchStart = uiSrc.IndexOf("// Watch button (flight only) — follows the group's current live continuation", StringComparison.Ordinal);
            int groupWatchEnd = uiSrc.IndexOf("// Rewind / Fast-forward button — targets main recording", groupWatchStart, StringComparison.Ordinal);
            string groupWatchBlock = uiSrc.Substring(groupWatchStart, groupWatchEnd - groupWatchStart);

            Assert.Contains("ResolveEffectiveWatchTargetIndex", groupWatchBlock);
            Assert.Contains("flight.WatchedRecordingIndex == resolvedWatchIdx", groupWatchBlock);
            Assert.Contains("resolvedWatchIdx", groupWatchBlock);
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
    }
}
