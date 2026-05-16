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
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
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
        public void SecondUse_AppendsHashSuffix2()
        {
            // Commit a recording in the "Flea" group
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            // Hash-prefix suffix avoids the "(N) (count)" visual collision in
            // the recordings-table button label — see GenerateUniqueGroupName
            // doc comment.
            Assert.Equal("Flea #2", result);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("using 'Flea #2'"));
        }

        [Fact]
        public void ThirdUse_AppendsHashSuffix3()
        {
            // Two existing groups: "Flea" and "Flea #2"
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea #2" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea #3", result);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("using 'Flea #3'"));
        }

        [Fact]
        public void DifferentBaseName_DoesNotCollide()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "flea",
                RecordingGroups = new List<string> { "flea" }
            });

            // "Flea" should collide with "flea" (case-insensitive)
            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea #2", result);
        }

        [Fact]
        public void GapInSequence_FillsFirstAvailable()
        {
            // Existing: "Flea" and "Flea #3" — gap at #2
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea", "Flea #3" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea #2", result);
        }

        [Fact]
        public void LegacyParensFormatInExistingNames_SkippedToKeepSequenceCoherent()
        {
            // Defense-in-depth: a save persisted before the format flip can
            // carry "Flea (2)" group names. GenerateUniqueGroupName must not
            // hand out "Flea #2" alongside "Flea (2)" — the user would read
            // them as two different missions but they were meant to be the
            // same numeric sequence. Skip the legacy variant the same way
            // we skip the new-form variant, so the next bumped name is the
            // first sequence slot free of BOTH forms.
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea", "Flea (2)" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea #3", result);
        }

        [Fact]
        public void LegacyParensFormat_OnlyLegacyVariant_BasenameStillUnique()
        {
            // A save that holds only "Flea (2)" (no plain "Flea") still leaves
            // the basename "Flea" available — the legacy-only collision set
            // must not poison the basename short-circuit. The very first call
            // with baseName="Flea" returns "Flea" unchanged.
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea (2)" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea", result);
        }

        [Fact]
        public void DualFormSameSlot_BothFormsSkipped()
        {
            // Pin the dual-form skip semantics when BOTH the new "#3" and
            // legacy "(3)" forms are present at the same sequence slot. The
            // loop should hit n=2 first (free) and return "Flea #2", leaving
            // both "Flea #3" and "Flea (3)" alone. The skip predicate is `OR`,
            // not `AND`, so a candidate is rejected as long as either form
            // exists at that n.
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea", "Flea #3", "Flea (3)" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea #2", result);
        }

        [Fact]
        public void DualFormMixed_NewAndLegacyAtDifferentSlots_BothSkipped()
        {
            // Pin the full mixed-save case: "Flea" + "Flea #2" + "Flea (3)".
            // The loop must skip n=2 (new form exists), skip n=3 (legacy
            // form exists), and return "Flea #4".
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Flea",
                RecordingGroups = new List<string> { "Flea", "Flea #2", "Flea (3)" }
            });

            string result = RecordingStore.GenerateUniqueGroupName("Flea");

            Assert.Equal("Flea #4", result);
        }
    }
}
