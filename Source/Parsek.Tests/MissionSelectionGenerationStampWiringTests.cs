using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    // M-MIS-5 build-review R2 fix (the deferral-edit corruption window): the Missions-window
    // interval checkbox handler must stamp SelectionSchemaGeneration to current on every
    // exclusion edit. A gen-0 mission whose tree was uncommitted at load (reconcile stamp
    // DEFERRED) becomes editable once the tree commits mid-session; without the stamp the
    // next load's generation-0 reconcile would wrongly extend the fresh selection across
    // @dock sub-siblings the player deliberately kept. The handler is IMGUI (not xUnit
    // drivable), so this is the source-text wiring gate per the DestinationLoiterTrimWiringTests
    // idiom; the reconcile semantics themselves are covered in MissionStoreTests.
    public class MissionSelectionGenerationStampWiringTests
    {
        private static string ReadMissionsWindowSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "MissionsWindowUI.cs");
            Assert.True(File.Exists(path), $"MissionsWindowUI.cs not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void IntervalToggle_StampsSelectionSchemaGeneration()
        {
            string src = ReadMissionsWindowSource();

            int mutation = src.IndexOf(
                "mission.ExcludedIntervalKeys.Add(node.HeadLegId);", StringComparison.Ordinal);
            Assert.True(mutation >= 0, "interval-exclusion mutation site not found");

            int stamp = src.IndexOf(
                "mission.SelectionSchemaGeneration = Mission.CurrentSelectionSchemaGeneration;",
                mutation, StringComparison.Ordinal);
            Assert.True(stamp >= 0, "generation stamp missing after the exclusion mutation");
            // Same handler block, not some far-away coincidental assignment.
            Assert.True(stamp - mutation < 800,
                "generation stamp is not adjacent to the exclusion mutation (same toggle block)");
        }
    }
}
