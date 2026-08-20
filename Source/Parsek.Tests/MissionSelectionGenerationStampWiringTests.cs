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

            // Both selection-writing toggles route their stamp through the shared
            // StampSelectionEdit helper (which performs the actual assignment), so the
            // mutation-stamp pairing is mechanical rather than copy-paste.
            int helper = src.IndexOf(
                "mission.SelectionSchemaGeneration = Mission.CurrentSelectionSchemaGeneration;",
                StringComparison.Ordinal);
            Assert.True(helper >= 0, "StampSelectionEdit no longer assigns the generation stamp");

            int mutation = src.IndexOf(
                "mission.ExcludedIntervalKeys.Add(node.HeadLegId);", StringComparison.Ordinal);
            Assert.True(mutation >= 0, "interval-exclusion mutation site not found");

            int stamp = src.IndexOf("StampSelectionEdit(mission);", mutation,
                StringComparison.Ordinal);
            Assert.True(stamp >= 0, "generation stamp missing after the exclusion mutation");
            // Same handler block, not some far-away coincidental call.
            Assert.True(stamp - mutation < 800,
                "generation stamp is not adjacent to the exclusion mutation (same toggle block)");

            // The T2.2 per-vessel toggle pairs its key-set write with the same stamp.
            int vesselMutation = src.IndexOf(
                "MissionVesselRowBuilder.ApplyVesselInclusion(", StringComparison.Ordinal);
            Assert.True(vesselMutation >= 0, "per-vessel inclusion write site not found");
            int vesselStamp = src.IndexOf("StampSelectionEdit(mission);", vesselMutation,
                StringComparison.Ordinal);
            Assert.True(vesselStamp >= 0 && vesselStamp - vesselMutation < 800,
                "per-vessel toggle does not stamp the selection generation in its block");
        }
    }
}
