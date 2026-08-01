using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source-text gate for the Timeline's archive-reveal wiring
    /// (`docs/dev/design-ui-basic-advanced.md` section 4.4).
    ///
    /// <para>The decisions themselves are pure and covered by
    /// `TimelineArchivedRowsTests`: the builder's filter arm, the stamp, the filter's
    /// polarity, the rebuild predicate. What no headless cell can reach is the IMGUI
    /// wiring in `DrawTimelineWindow` / `DrawFilterBar` / `DrawEntryRow`, and the
    /// regression that matters is a silent one: drop the argument at the `Build` call
    /// site and the toggle keeps rendering and keeps storing its value while the row set
    /// never changes. So this reads the source, the established pattern here
    /// (`SettingsSectionGateWiringTests`, `DestinationLoiterTrimWiringTests`).</para>
    ///
    /// <para>Deliberately loose: plain substring assertions on the load-bearing tokens,
    /// no multi-line regex over IMGUI blocks. A layout edit must not red this file; only
    /// removing a wire should.</para>
    ///
    /// <para>xUnit runs from `Source/Parsek.Tests/bin/Debug/net472/`, hence the 5 ".."
    /// segments to the repo root (precedent: `ChainSaveLoadTests`).</para>
    /// </summary>
    public class TimelineArchiveFilterWiringTests
    {
        private static string ReadTimelineWindowSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "TimelineWindowUI.cs");
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", "UI", "TimelineWindowUI.cs");
            Assert.True(File.Exists(path), $"TimelineWindowUI.cs not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void FilterBarDrawsTheArchivedToggleBoundToTheSharedFilter()
        {
            string src = ReadTimelineWindowSource();

            Assert.Contains("\"Archived\"", src);
            Assert.Contains("ShowArchivedRecordings = newShowArchived;", src);
        }

        [Fact]
        public void BuildCallPassesTheArchiveFilterThrough()
        {
            string src = ReadTimelineWindowSource();

            // The reveal is worthless if the flag is read, stored, and then not handed
            // to the builder - the toggle would look live and change nothing.
            Assert.Contains("bool showArchivedRows = ShowArchivedRecordings;", src);
            Assert.Contains("showArchivedRows);", src);
            Assert.Contains("cachedTimelineShowedArchived = showArchivedRows;", src);
        }

        [Fact]
        public void CacheRebuildRoutesThroughTheSharedPredicate()
        {
            string src = ReadTimelineWindowSource();

            // Pin the CALL SITE, not the bare method name: `ShouldRebuildTimeline(`
            // alone is satisfied by the method's own definition, so it would stay green
            // with the call deleted. A rebuild condition that drops the archive arm
            // leaves the Recordings tab's write of the shared filter invisible to a warm
            // cache, which is silent - the rows simply stay as they were.
            Assert.Contains("if (ShouldRebuildTimeline(", src);
        }

        [Fact]
        public void RevealedArchivedRowsAreMarkedInTheRowDraw()
        {
            string src = ReadTimelineWindowSource();

            Assert.Contains("entry.IsArchivedRecording", src);
            Assert.Contains("[archived]", src);
        }
    }
}
