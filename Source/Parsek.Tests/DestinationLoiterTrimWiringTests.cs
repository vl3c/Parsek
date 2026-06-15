using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    // M-MIS-2 P4.2 source-text gate (the plan's B3 regression fence): assert MissionLoopUnitBuilder
    // wires the destination-loiter trim into the re-aim branch AND preserves the byte-identical
    // fallback (the shipped ComputeArrivalHold path when the trim declines). The Applied-path math is
    // covered by the pure DestinationLoiterTrimTests; the byte-identical-off property is structural
    // (the else branch is the verbatim shipped call), proven here by its continued presence.
    // Pattern: ChainSaveLoadTests.ChainStateNotPersistedInScenario (xUnit runs from
    // Source/Parsek.Tests/bin/Debug/net472/ -> 5 ".." segments to the repo root).
    public class DestinationLoiterTrimWiringTests
    {
        private static string ReadBuilderSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "MissionLoopUnitBuilder.cs");
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", "MissionLoopUnitBuilder.cs");
            Assert.True(File.Exists(path), $"MissionLoopUnitBuilder.cs not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void Builder_WiresDestinationLoiterTrim()
        {
            string src = ReadBuilderSource();
            // The re-aim branch calls the P4 joint solve and assembles its cut into loiterCuts.
            Assert.Contains("TrySolveDestinationLoiterTrim(", src);
            Assert.Contains("DestinationLoiterTrim.SolveTrimAndHold(", src);
            Assert.Contains("BuildLaunchSideKeepOneCuts(", src);
            Assert.Contains("loiterCuts = p4Cuts", src);
        }

        [Fact]
        public void Builder_PreservesByteIdenticalFallback()
        {
            string src = ReadBuilderSource();
            // The trim-declined branch must still take the shipped ComputeArrivalHold path verbatim
            // (the byte-identical-off invariant: None => today's behavior).
            Assert.Contains("arrivalHold = ArrivalHoldPlanner.ComputeArrivalHold(", src);
            // And the full keepRevs=1 cut set is still computed as the fallback loiterCuts.
            Assert.Contains("ReaimLoiterCompressor.ComputeCuts(transferSegments", src);
        }
    }
}
