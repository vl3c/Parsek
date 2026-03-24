using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class UniqueGroupNameTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UniqueGroupNameTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GroupHierarchyStore.ResetGroupsForTesting();
            ParsekScenario.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            ParsekScenario.ResetReplacementsForTesting();
        }

        [Fact]
        public void FirstUse_ReturnsBaseNameUnchanged()
        {
            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea", result);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("'Flea' is unique"));
        }

        [Fact]
        public void SecondUse_AppendsSuffix2()
        {
            // Commit a recording in the "Flea" group
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea (2)", result);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("using 'Flea (2)'"));
        }

        [Fact]
        public void ThirdUse_AppendsSuffix3()
        {
            // Two existing groups: "Flea" and "Flea (2)"
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea (2)" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea (3)", result);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("using 'Flea (3)'"));
        }

        [Fact]
        public void DifferentBaseName_DoesNotCollide()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Hopper");

            Assert.Equal("Hopper", result);
        }

        [Fact]
        public void NullBaseName_FallsBackToChain()
        {
            string result = RecordingStore.GenerateUniqueGroupName(null);

            Assert.Equal("Chain", result);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("baseName is null/empty"));
        }

        [Fact]
        public void EmptyBaseName_FallsBackToChain()
        {
            string result = RecordingStore.GenerateUniqueGroupName("");

            Assert.Equal("Chain", result);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("baseName is null/empty"));
        }

        [Fact]
        public void CaseInsensitive_DetectsCollision()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "flea",
                RecordingGroups = new List<string> { "flea" }
            });

            // "Flea" should collide with "flea" (case-insensitive)
            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea (2)", result);
        }

        [Fact]
        public void GapInSequence_FillsFirstAvailable()
        {
            // Existing: "Flea" and "Flea (3)" — gap at (2)
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea", "Flea (3)" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea (2)", result);
        }
    }
}
