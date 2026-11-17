using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The part-name substitute seam of the timeline ghost build (R5-01): with no options
    /// or no dresser the build looks up exactly the recorded name and logs nothing, so a
    /// single-player ghost builds as it always did.
    /// </summary>
    [Collection("Sequential")]
    public class GhostBuildOptionsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostBuildOptionsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class MapDresser : IGhostPartDresser
        {
            private readonly Dictionary<string, string> map;
            public readonly List<string> Asked = new List<string>();

            public MapDresser(Dictionary<string, string> map) { this.map = map; }

            public string SubstitutePartName(string recordedPartName)
            {
                Asked.Add(recordedPartName);
                return map.TryGetValue(recordedPartName, out string used) ? used : null;
            }
        }

        private bool AnySubstitutionLine()
            => logLines.Any(l => l.Contains("Ghost part substituted"));

        [Fact]
        public void NullOptions_KeepsRecordedName_AndLogsNothing()
        {
            string used = GhostVisualBuilder.ResolveBuildPartName("kerbalEVA", null, out bool substituted);

            Assert.Equal("kerbalEVA", used);
            Assert.False(substituted);
            Assert.Empty(logLines);
        }

        [Fact]
        public void NullDresser_KeepsRecordedName_AndLogsNothing()
        {
            string used = GhostVisualBuilder.ResolveBuildPartName(
                "mk1pod.v2", new GhostBuildOptions(null), out bool substituted);

            Assert.Equal("mk1pod.v2", used);
            Assert.False(substituted);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Substitute_IsUsed_AndLogged()
        {
            var dresser = new MapDresser(new Dictionary<string, string>
            {
                { "kerbalEVAParsekMissing", "kerbalEVA" },
            });

            string used = GhostVisualBuilder.ResolveBuildPartName(
                "kerbalEVAParsekMissing", new GhostBuildOptions(dresser), out bool substituted);

            Assert.Equal("kerbalEVA", used);
            Assert.True(substituted);
            Assert.Equal(new[] { "kerbalEVAParsekMissing" }, dresser.Asked);
            Assert.Contains(logLines, l => l.Contains("[GhostVisual]")
                && l.Contains("Ghost part substituted: recorded=kerbalEVAParsekMissing used=kerbalEVA"));
        }

        [Fact]
        public void EmptyOrNullSubstitute_KeepsRecordedName_AndLogsNothing()
        {
            var dresser = new MapDresser(new Dictionary<string, string>
            {
                { "kerbalEVAfemale", "" },
            });
            var options = new GhostBuildOptions(dresser);

            string emptyCase = GhostVisualBuilder.ResolveBuildPartName("kerbalEVAfemale", options, out bool s1);
            string nullCase = GhostVisualBuilder.ResolveBuildPartName("fuelTank", options, out bool s2);

            Assert.Equal("kerbalEVAfemale", emptyCase);
            Assert.Equal("fuelTank", nullCase);
            Assert.False(s1);
            Assert.False(s2);
            Assert.False(AnySubstitutionLine());
        }

        [Fact]
        public void SubstituteEqualToRecorded_IsNotASubstitution()
        {
            var dresser = new MapDresser(new Dictionary<string, string> { { "kerbalEVA", "kerbalEVA" } });

            string used = GhostVisualBuilder.ResolveBuildPartName(
                "kerbalEVA", new GhostBuildOptions(dresser), out bool substituted);

            Assert.Equal("kerbalEVA", used);
            Assert.False(substituted);
            Assert.False(AnySubstitutionLine());
        }

        [Fact]
        public void EmptyRecordedName_NeverAsksTheDresser()
        {
            var dresser = new MapDresser(new Dictionary<string, string>());

            string used = GhostVisualBuilder.ResolveBuildPartName(
                null, new GhostBuildOptions(dresser), out bool substituted);

            Assert.Null(used);
            Assert.False(substituted);
            Assert.Empty(dresser.Asked);
        }

        [Fact]
        public void RepeatedPair_LogsOncePerRateWindow()
        {
            var dresser = new MapDresser(new Dictionary<string, string> { { "a", "b" } });
            var options = new GhostBuildOptions(dresser);

            GhostVisualBuilder.ResolveBuildPartName("a", options, out _);
            GhostVisualBuilder.ResolveBuildPartName("a", options, out bool substituted);

            Assert.True(substituted);
            Assert.Equal(1, logLines.Count(l => l.Contains("Ghost part substituted: recorded=a used=b")));
        }

        // Every existing caller passes no options, so it must reach the neutral null path.
        [Theory]
        [InlineData(nameof(GhostVisualBuilder.BuildTimelineGhostFromSnapshot))]
        [InlineData(nameof(GhostVisualBuilder.TryBeginTimelineGhostBuild))]
        public void EntryPoints_DefaultOptionsToNull(string methodName)
        {
            MethodInfo method = typeof(GhostVisualBuilder).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(method);

            ParameterInfo options = method.GetParameters().Last();
            Assert.Equal(typeof(GhostBuildOptions), options.ParameterType);
            Assert.True(options.IsOptional);
            Assert.Null(options.DefaultValue);
        }
    }
}
