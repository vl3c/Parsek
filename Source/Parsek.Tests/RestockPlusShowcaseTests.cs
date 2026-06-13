using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the install-conditional ReStock+ part showcases: well-formedness of the
    /// generated rows (pids, names, parts), the dedicated RS+ line band sitting behind
    /// the stock showcase lines, and the install-probe seam.
    /// </summary>
    [Collection("Sequential")]
    public class RestockPlusShowcaseTests : IDisposable
    {
        private const uint RestockPlusPidBase = 99500000;

        public RestockPlusShowcaseTests()
        {
            SyntheticRecordingTests.RestockPlusInstalledOverrideForTesting = null;
        }

        public void Dispose()
        {
            SyntheticRecordingTests.RestockPlusInstalledOverrideForTesting = null;
        }

        [Fact]
        public void RestockPlusShowcaseRecordings_RowsAreWellFormed()
        {
            var rows = SyntheticRecordingTests.RestockPlusShowcaseRecordings(baseUT: 0);

            Assert.Equal(36, rows.Length);

            var names = new HashSet<string>(StringComparer.Ordinal);
            var pids = new HashSet<uint>();
            for (int i = 0; i < rows.Length; i++)
            {
                string vesselName = rows[i].BuildV3Metadata().GetValue("vesselName");
                Assert.StartsWith("Part Showcase - RS+", vesselName);
                Assert.True(names.Add(vesselName), $"duplicate vessel name '{vesselName}'");

                ConfigNode snap = rows[i].GetGhostVisualSnapshot();
                Assert.NotNull(snap);
                uint pid = uint.Parse(snap.GetValue("persistentId"), CultureInfo.InvariantCulture);
                Assert.InRange(pid, RestockPlusPidBase, RestockPlusPidBase + (uint)rows.Length - 1);
                Assert.True(pids.Add(pid), $"duplicate snapshot pid {pid} ('{vesselName}')");

                ConfigNode[] partNodes = snap.GetNodes("PART");
                Assert.True(partNodes.Length > 0, $"'{vesselName}' snapshot has no PART nodes");
                for (int p = 0; p < partNodes.Length; p++)
                {
                    Assert.StartsWith("restock-", partNodes[p].GetValue("name"));
                }
            }
        }

        [Fact]
        public void RestockPlusShowcaseRows_SitBehindStockEngineShowcaseLines()
        {
            // The RS+ band must not overlap the three stock lines: every RS+ row's
            // longitude (distance from pad) lies strictly beyond every stock engine
            // showcase row's longitude. Engine rows are a sufficient stand-in for all
            // stock categories: every stock builder shares ShowcaseDistanceFromPadMeters
            // and the same three-line lineIndex domain, and the engine rows include
            // lineIndex 2 (the farthest stock line).
            double maxStockLon = double.MinValue;
            foreach (var stock in SyntheticRecordingTests.EngineShowcaseRecordings(baseUT: 0))
            {
                double lon = FirstPointLon(stock);
                if (lon > maxStockLon)
                    maxStockLon = lon;
            }

            foreach (var row in SyntheticRecordingTests.RestockPlusShowcaseRecordings(baseUT: 0))
            {
                string vesselName = row.BuildV3Metadata().GetValue("vesselName");
                Assert.True(FirstPointLon(row) > maxStockLon,
                    $"'{vesselName}' sits on or before the stock showcase lines");
            }
        }

        [Fact]
        public void RestockPlusInstallProbe_OverrideAndDefault()
        {
            Assert.False(SyntheticRecordingTests.IsRestockPlusInstalled(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parsek-no-such-ksp-root")),
                "probe must be false for a root without GameData/ReStockPlus");

            SyntheticRecordingTests.RestockPlusInstalledOverrideForTesting = () => true;
            Assert.True(SyntheticRecordingTests.IsRestockPlusInstalled("ignored"));

            SyntheticRecordingTests.RestockPlusInstalledOverrideForTesting = () => false;
            Assert.False(SyntheticRecordingTests.IsRestockPlusInstalled("ignored"));
        }

        private static double FirstPointLon(Parsek.Tests.Generators.RecordingBuilder builder)
        {
            ConfigNode traj = builder.BuildTrajectoryNode();
            ConfigNode[] points = traj.GetNodes("POINT");
            Assert.True(points.Length > 0, "recording has no trajectory points");
            return double.Parse(points[0].GetValue("lon"), CultureInfo.InvariantCulture);
        }
    }
}
