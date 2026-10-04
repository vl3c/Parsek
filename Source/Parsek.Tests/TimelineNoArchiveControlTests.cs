using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source-text gate: the Timeline window carries no archive control and does not read
    /// the Recordings tab's archive filter (`docs/dev/design-ui-basic-advanced.md` section
    /// 4.4). Archive lives only in the Recordings tab; the Timeline never lists archived
    /// recordings (pinned behaviourally by `TimelineArchivedRowsTests`).
    ///
    /// <para>No headless cell can run the IMGUI draw, so this reads the source with
    /// comments stripped (string literals kept, so a returning "Archived" label is seen).
    /// xUnit runs from `Source/Parsek.Tests/bin/Debug/net472/`, hence the 5 ".." segments
    /// to the repo root (precedent: `ChainSaveLoadTests`).</para>
    /// </summary>
    public class TimelineNoArchiveControlTests
    {
        private static string ReadTimelineWindowSourceWithoutComments()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "TimelineWindowUI.cs");
            Assert.True(File.Exists(path), $"TimelineWindowUI.cs not found at {path}");
            return SourceScanText.StripCSharpComments(File.ReadAllText(path));
        }

        // catches: the removed Timeline "Archived" filter button coming back.
        [Fact]
        public void TimelineDrawsNoArchivedControl()
        {
            string src = ReadTimelineWindowSourceWithoutComments();

            Assert.DoesNotContain("\"Archived\"", src);
            Assert.DoesNotContain("DrawArchivedToggle", src);
            Assert.DoesNotContain("ShowArchivedRecordings", src);
        }

        // catches: the Timeline reading or writing the Recordings tab's archive filter, which
        // would make that tab's Archive header checkbox change the Timeline's rows again.
        [Fact]
        public void TimelineDoesNotTouchTheRecordingsTabArchiveFilter()
        {
            string src = ReadTimelineWindowSourceWithoutComments();

            Assert.DoesNotContain("GroupHierarchyStore.HideActive", src);
        }

        // catches: an archived-row marker left behind with no way to reveal such a row.
        [Fact]
        public void RowDrawCarriesNoArchivedMarker()
        {
            string src = ReadTimelineWindowSourceWithoutComments();

            Assert.DoesNotContain("[archived]", src);
            Assert.DoesNotContain("IsArchivedRecording", src);
        }
    }
}
