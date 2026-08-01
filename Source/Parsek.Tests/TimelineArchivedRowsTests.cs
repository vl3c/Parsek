using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Timeline's archive-reveal filter (`docs/dev/design-ui-basic-advanced.md`
    /// section 4.4).
    ///
    /// <para>What it protects: `Recording.Hidden` (player-facing "Archive") is written
    /// ONLY from the Recordings tab, which Basic UI mode hides. Before this filter the
    /// Timeline consumed the flag unconditionally, so an archive made in Advanced was
    /// permanent for a Basic player - the row was gone and no reachable control could
    /// bring it back. The fix keeps the flag's meaning identical in both modes and gives
    /// the Timeline its own reveal, sharing the one archive-filter state.</para>
    ///
    /// <para>The mode invariant these cells pin: the row set depends on the ARCHIVE
    /// FILTER, never on the UI complexity mode. `TimelineBuilder` takes a bool and reads
    /// no store, so there is no mode to read.</para>
    /// </summary>
    [Collection("Sequential")]
    public class TimelineArchivedRowsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool originalHideActive;

        public TimelineArchivedRowsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            originalHideActive = GroupHierarchyStore.HideActive;
        }

        public void Dispose()
        {
            GroupHierarchyStore.HideActive = originalHideActive;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording MakeRecording(
            string vesselName, double startUT, double endUT, bool archived)
        {
            var rec = new Recording();
            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
            rec.VesselName = vesselName;
            rec.RecordingId = Guid.NewGuid().ToString("N");
            rec.PlaybackEnabled = true;
            rec.Hidden = archived;
            rec.ChainIndex = -1;
            return rec;
        }

        private static List<TimelineEntry> Build(
            IReadOnlyList<Recording> recordings, bool includeArchived)
        {
            return TimelineBuilder.Build(
                recordings,
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true,
                null,
                includeArchived);
        }

        // ================================================================
        // The filter itself
        // ================================================================

        [Fact]
        public void ArchivedRecordingIsExcludedByDefault()
        {
            var archived = MakeRecording("Mun Lander", 100, 500, archived: true);

            // The no-argument overload is what every pre-existing caller uses; the
            // default must stay "hide archived" or this change would silently
            // re-clutter every Timeline.
            var result = TimelineBuilder.Build(
                new List<Recording> { archived },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Empty(result);
        }

        [Fact]
        public void ArchivedRecordingIsIncludedWhenTheFilterIsOff()
        {
            var archived = MakeRecording("Mun Lander", 100, 500, archived: true);

            var result = Build(new List<Recording> { archived }, includeArchived: true);

            Assert.NotEmpty(result);
            Assert.Contains(result, e => e.Type == TimelineEntryType.RecordingStart);
            Assert.Contains(result, e => e.Type == TimelineEntryType.VesselSpawn);
            Assert.All(result, e => Assert.Equal("Mun Lander", e.VesselName));
        }

        [Fact]
        public void RevealingArchivedRowsLeavesUnarchivedRowsUntouched()
        {
            var plain = MakeRecording("Flea I", 100, 200, archived: false);
            var archived = MakeRecording("Flea II", 300, 400, archived: true);
            var recordings = new List<Recording> { plain, archived };

            var filtered = Build(recordings, includeArchived: false);
            var revealed = Build(recordings, includeArchived: true);
            // The archived flight's own contribution, measured rather than assumed: a
            // literal factor (the two fixtures happen to emit the same row count) would
            // encode fixture symmetry instead of the additive property under test.
            var archivedAlone = Build(new List<Recording> { archived }, includeArchived: true);

            // Filtered keeps exactly the unarchived flight; revealing ADDS, never
            // replaces. Order is not asserted: the builder sorts every entry by UT, so
            // interleaving by time is the correct result, not a defect.
            Assert.All(filtered, e => Assert.Equal("Flea I", e.VesselName));
            Assert.NotEmpty(archivedAlone);
            Assert.Equal(filtered.Count + archivedAlone.Count, revealed.Count);
            foreach (var e in filtered)
                Assert.Contains(revealed, r => r.Type == e.Type && r.UT == e.UT);
        }

        // ================================================================
        // The revealed-row marker
        // ================================================================

        [Fact]
        public void RevealedArchivedEntriesAreStampedAsArchived()
        {
            var archived = MakeRecording("Mun Lander", 100, 500, archived: true);

            var result = Build(new List<Recording> { archived }, includeArchived: true);

            Assert.NotEmpty(result);
            Assert.All(result, e => Assert.True(
                e.IsArchivedRecording,
                $"{e.Type} entry from an archived recording must be stamped so the row draw can mark it"));
        }

        [Fact]
        public void UnarchivedEntriesAreNeverStamped()
        {
            var plain = MakeRecording("Flea I", 100, 200, archived: false);
            var archived = MakeRecording("Flea II", 300, 400, archived: true);

            var result = Build(new List<Recording> { plain, archived }, includeArchived: true);

            // The stamp must follow the recording, not the build flag: with the filter
            // off, a mixed list still has to distinguish the two populations or the
            // marker means nothing.
            Assert.All(
                result.Where(e => e.VesselName == "Flea I"),
                e => Assert.False(e.IsArchivedRecording));
            Assert.All(
                result.Where(e => e.VesselName == "Flea II"),
                e => Assert.True(e.IsArchivedRecording));
        }

        // ================================================================
        // Diagnostics
        // ================================================================

        [Fact]
        public void CollectorLogsWhetherArchivedRecordingsWereSkippedOrShown()
        {
            var recordings = new List<Recording>
            {
                MakeRecording("Flea I", 100, 200, archived: false),
                MakeRecording("Flea II", 300, 400, archived: true),
            };

            Build(recordings, includeArchived: false);
            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("Recording collector:")
                && l.Contains("hidden=1") && l.Contains("archivedShown=0"));

            logLines.Clear();

            Build(recordings, includeArchived: true);
            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("Recording collector:")
                && l.Contains("hidden=0") && l.Contains("archivedShown=1"));
        }

        // ================================================================
        // The shared filter state and its polarity
        // ================================================================

        [Fact]
        public void ShowArchivedRecordingsIsTheRecordingsTabFilterInverted()
        {
            // The Recordings tab header checkbox means "hide archived"; every Timeline
            // filter toggle means "show this". One stored bool, two labels - if the
            // inversion is ever dropped, the Timeline toggle reads backwards.
            GroupHierarchyStore.HideActive = true;
            Assert.False(TimelineWindowUI.ShowArchivedRecordings);

            GroupHierarchyStore.HideActive = false;
            Assert.True(TimelineWindowUI.ShowArchivedRecordings);
        }

        [Fact]
        public void SettingShowArchivedRecordingsWritesTheSharedFilter()
        {
            // Writing through the Timeline must land on the SAME state the Recordings
            // tab owns - that sharing is what makes the Basic-reachable toggle a real
            // un-archive path rather than a Timeline-private view flag.
            GroupHierarchyStore.HideActive = true;

            TimelineWindowUI.ShowArchivedRecordings = true;
            Assert.False(GroupHierarchyStore.HideActive);

            TimelineWindowUI.ShowArchivedRecordings = false;
            Assert.True(GroupHierarchyStore.HideActive);
        }

        [Fact]
        public void ArchiveFilterDefaultsToHidingArchivedRows()
        {
            GroupHierarchyStore.ResetForTesting();

            // GroupHierarchyStore's own default (hideActive = true) is what makes an
            // untouched save behave exactly as it did before this filter existed.
            Assert.True(GroupHierarchyStore.HideActive);
            Assert.False(TimelineWindowUI.ShowArchivedRecordings);
        }

        // ================================================================
        // Cache invalidation
        // ================================================================

        [Fact]
        public void TimelineRebuildsWhenTheArchiveFilterFlipsUnderAWarmCache()
        {
            // The Recordings tab writes the shared filter directly and calls no Timeline
            // invalidation, so a warm cache would otherwise keep showing the old row set.
            Assert.True(TimelineWindowUI.ShouldRebuildTimeline(
                dirty: false, cacheMissing: false, cachedShowedArchived: false, showArchivedNow: true));
            Assert.True(TimelineWindowUI.ShouldRebuildTimeline(
                dirty: false, cacheMissing: false, cachedShowedArchived: true, showArchivedNow: false));
        }

        [Fact]
        public void TimelineDoesNotRebuildWhenNothingChanged()
        {
            Assert.False(TimelineWindowUI.ShouldRebuildTimeline(
                dirty: false, cacheMissing: false, cachedShowedArchived: false, showArchivedNow: false));
            Assert.False(TimelineWindowUI.ShouldRebuildTimeline(
                dirty: false, cacheMissing: false, cachedShowedArchived: true, showArchivedNow: true));
        }

        [Fact]
        public void TimelineStillRebuildsOnTheOriginalTriggers()
        {
            Assert.True(TimelineWindowUI.ShouldRebuildTimeline(
                dirty: true, cacheMissing: false, cachedShowedArchived: true, showArchivedNow: true));
            Assert.True(TimelineWindowUI.ShouldRebuildTimeline(
                dirty: false, cacheMissing: true, cachedShowedArchived: true, showArchivedNow: true));
        }
    }
}
