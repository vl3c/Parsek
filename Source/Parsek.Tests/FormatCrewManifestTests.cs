using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class FormatCrewManifestTests
    {
        [Fact]
        public void BothNull_ReturnsNull()
        {
            var result = RecordingsTableUI.FormatCrewManifest(null, null);

            Assert.Null(result);
        }

        [Fact]
        public void StartOnly_ShowsStartFormat()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Engineer"] = 2
            };

            var result = RecordingsTableUI.FormatCrewManifest(start, null);

            Assert.NotNull(result);
            Assert.StartsWith("Crew at start:", result);
            Assert.Contains("Pilot: 1", result);
            Assert.Contains("Engineer: 2", result);
        }

        [Fact]
        public void BothStartAndEnd_ShowsDeltaFormat()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Engineer"] = 2
            };
            var end = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Engineer"] = 0
            };

            var result = RecordingsTableUI.FormatCrewManifest(start, end);

            Assert.NotNull(result);
            Assert.StartsWith("Crew:", result);
            Assert.Contains("Pilot: 1 \u2192 1 (+0)", result);
            Assert.Contains("Engineer: 2 \u2192 0 (-2)", result);
        }

        [Fact]
        public void Unchanged_ZeroDelta()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 1
            };
            var end = new Dictionary<string, int>
            {
                ["Pilot"] = 1
            };

            var result = RecordingsTableUI.FormatCrewManifest(start, end);

            Assert.NotNull(result);
            Assert.Contains("Pilot: 1 \u2192 1 (+0)", result);
        }

        [Fact]
        public void SingleTrait()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 3
            };

            var result = RecordingsTableUI.FormatCrewManifest(start, null);

            Assert.NotNull(result);
            Assert.Contains("Pilot: 3", result);
            // Only one trait line + header
            var lines = result.Split('\n');
            Assert.Equal(2, lines.Length);
        }

        [Fact]
        public void GainedCrew_PositiveDelta()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 0
            };
            var end = new Dictionary<string, int>
            {
                ["Pilot"] = 2
            };

            var result = RecordingsTableUI.FormatCrewManifest(start, end);

            Assert.NotNull(result);
            Assert.Contains("Pilot: 0 \u2192 2 (+2)", result);
        }
    }
}
