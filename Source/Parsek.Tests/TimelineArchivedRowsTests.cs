using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Archived recordings on the Timeline (`docs/dev/design-ui-basic-advanced.md`
    /// section 4.4).
    ///
    /// <para>What it protects: `Recording.Hidden` (player-facing "Archive") is written and
    /// filtered only by the Recordings tab, which carries the one archive control. The
    /// Timeline has no archive control of its own and never lists an archived recording's
    /// rows, whatever the Recordings tab's Archive header filter says.</para>
    ///
    /// <para>The invariant these cells pin: the row set depends only on the recordings
    /// passed in, never on the Recordings tab's filter state and never on the UI complexity
    /// mode. `TimelineBuilder` reads no store, so there is no state to read.</para>
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

        private static List<TimelineEntry> Build(IReadOnlyList<Recording> recordings)
        {
            return TimelineBuilder.Build(
                recordings,
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);
        }

        [Fact]
        public void ArchivedRecordingContributesNoRows()
        {
            var archived = MakeRecording("Mun Lander", 100, 500, archived: true);

            Assert.Empty(Build(new List<Recording> { archived }));
        }

        [Fact]
        public void UnarchivedRecordingStillContributesItsRows()
        {
            // Control for the cell above: the same recording without the flag does emit
            // rows, so the empty result there is the archive skip and not a fixture that
            // emits nothing anyway.
            var plain = MakeRecording("Mun Lander", 100, 500, archived: false);

            var result = Build(new List<Recording> { plain });

            Assert.Contains(result, e => e.Type == TimelineEntryType.RecordingStart);
            Assert.Contains(result, e => e.Type == TimelineEntryType.VesselSpawn);
        }

        [Fact]
        public void MixedListKeepsExactlyTheUnarchivedFlight()
        {
            var plain = MakeRecording("Flea I", 100, 200, archived: false);
            var archived = MakeRecording("Flea II", 300, 400, archived: true);

            var mixed = Build(new List<Recording> { plain, archived });
            var plainAlone = Build(new List<Recording> { plain });

            Assert.NotEmpty(mixed);
            Assert.All(mixed, e => Assert.Equal("Flea I", e.VesselName));
            Assert.Equal(plainAlone.Count, mixed.Count);
        }

        // catches: the Timeline following the Recordings tab's Archive header filter again
        // (the removed Timeline "Archived" toggle wrote that shared filter).
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheRecordingsTabArchiveFilterDoesNotReachTheTimeline(bool hideActive)
        {
            GroupHierarchyStore.HideActive = hideActive;
            var archived = MakeRecording("Flea II", 300, 400, archived: true);

            Assert.Empty(Build(new List<Recording> { archived }));
        }

        [Fact]
        public void CollectorLogsTheArchivedSkipCount()
        {
            var recordings = new List<Recording>
            {
                MakeRecording("Flea I", 100, 200, archived: false),
                MakeRecording("Flea II", 300, 400, archived: true),
            };

            Build(recordings);

            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("Recording collector:")
                && l.Contains("hidden=1"));
            Assert.DoesNotContain(logLines, l => l.Contains("archivedShown="));
        }
    }
}
