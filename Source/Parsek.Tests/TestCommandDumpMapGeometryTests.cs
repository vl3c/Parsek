using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>DumpMapGeometry</c> seam verb
    /// (<see cref="TestCommandDumpMapGeometry"/>): the label parse (the screenshot rule,
    /// shared on purpose), the scene admission, the poll decision and its ordering, the path
    /// and payload, the suffix pin against the recorder, and the OK / truncated log lines.
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandDumpMapGeometryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TestCommandDumpMapGeometryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ----- label -----

        [Fact]
        public void MissingLabel_IsTheScreenshotMissingToken()
        {
            Assert.False(TestCommandDumpMapGeometry.TryParseLabel(null, out string label, out string reason));
            Assert.Null(label);
            Assert.Equal(TestCommandCaptureScreenshot.LabelArgMissingReason, reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("dir/label")]
        [InlineData("a b")]
        public void UnsafeLabel_IsTheScreenshotInvalidToken(string raw)
        {
            Assert.False(TestCommandDumpMapGeometry.TryParseLabel(raw, out _, out string reason));
            Assert.Equal(TestCommandCaptureScreenshot.LabelArgInvalidReason, reason);
        }

        [Theory]
        [InlineData("ts-kerbin")]
        [InlineData("flight_map.mun")]
        public void SafeLabel_Parses_AndAgreesWithCaptureScreenshot(string raw)
        {
            Assert.True(TestCommandDumpMapGeometry.TryParseLabel(raw, out string label, out string reason));
            Assert.Equal(raw, label);
            Assert.Null(reason);
            Assert.Equal(TestCommandCaptureScreenshot.IsValidLabel(raw), TestCommandDumpMapGeometry.IsValidLabel(raw));
        }

        // ----- path -----

        [Fact]
        public void Suffix_IsTheRecordersOwn()
        {
            Assert.Equal(MapGeometryRecorder.OutputSuffix, TestCommandDumpMapGeometry.DumpSuffix);
            Assert.Equal(".map.json", TestCommandDumpMapGeometry.DumpSuffix);
        }

        [Fact]
        public void RelativePath_IsInTheScreenshotsFolder()
        {
            Assert.Equal("Screenshots/ts-kerbin.map.json", TestCommandDumpMapGeometry.RelativePathFor("ts-kerbin"));
        }

        // ----- admission -----

        [Theory]
        [InlineData("TrackingStation", false, null)]
        [InlineData("TrackingStation", true, null)]
        [InlineData("Flight", true, null)]
        [InlineData("Flight", false, "map-view-closed")]
        [InlineData("SpaceCenter", true, "not-in-map-scene")]
        [InlineData("MainMenu", false, "not-in-map-scene")]
        [InlineData("Editor", false, "not-in-map-scene")]
        public void DecideAdmission(string sceneName, bool map, string expected)
        {
            var scene = (TestCommandScene)Enum.Parse(typeof(TestCommandScene), sceneName);
            Assert.Equal(expected, TestCommandDumpMapGeometry.DecideAdmission(scene, map));
        }

        [Fact]
        public void TheNotMapSceneToken_IsTheDispatchersDeferToken()
        {
            Assert.Equal(TestCommandDispatcher.NotInMapSceneReason, TestCommandDumpMapGeometry.NotMapSceneReason);
        }

        [Fact]
        public void SceneToken_NamesOnlyTheTwoMapScenes()
        {
            Assert.Equal("FLIGHT", TestCommandDumpMapGeometry.SceneToken(TestCommandScene.Flight));
            Assert.Equal("TRACKSTATION", TestCommandDumpMapGeometry.SceneToken(TestCommandScene.TrackingStation));
            Assert.Null(TestCommandDumpMapGeometry.SceneToken(TestCommandScene.SpaceCenter));
        }

        // ----- poll -----

        [Theory]
        [InlineData(false, false, false, "NotYet")]
        [InlineData(true, false, false, "Written")]
        [InlineData(true, false, true, "Written")]     // written on the expiry frame is OK
        [InlineData(false, true, false, "Failed")]
        [InlineData(false, true, true, "Failed")]
        [InlineData(false, false, true, "TimedOut")]
        public void DecidePoll(bool written, bool failed, bool expired, string expected)
        {
            Assert.Equal(expected, TestCommandDumpMapGeometry.DecidePoll(written, failed, expired).ToString());
        }

        [Fact]
        public void TerminalTokens_AreDistinct()
        {
            var tokens = new[]
            {
                TestCommandDumpMapGeometry.MapViewClosedReason,
                TestCommandDumpMapGeometry.NotMapSceneReason,
                TestCommandDumpMapGeometry.CaptureFailedReason,
                TestCommandDumpMapGeometry.TimeoutReason,
                TestCommandDumpMapGeometry.LabelArgMissingReason,
                TestCommandDumpMapGeometry.LabelArgInvalidReason,
            };
            Assert.Equal(tokens.Length, tokens.Distinct().Count());
        }

        [Fact]
        public void ReasonTables_AreTheConstants_InOrder()
        {
            Assert.Equal(new[]
            {
                TestCommandDumpMapGeometry.LabelArgMissingReason,
                TestCommandDumpMapGeometry.LabelArgInvalidReason,
                TestCommandDumpMapGeometry.MapViewClosedReason,
                TestCommandDumpMapGeometry.NotMapSceneReason,
            }, TestCommandDumpMapGeometry.Reasons);
            Assert.Equal(new[]
            {
                TestCommandDumpMapGeometry.CaptureFailedReason,
                TestCommandDumpMapGeometry.TimeoutReason,
            }, TestCommandDumpMapGeometry.ErrorReasons);
        }

        // ----- payload + log -----

        [Fact]
        public void BuildPayload_IsInvariantAndComplete()
        {
            var p = TestCommandDumpMapGeometry.BuildPayload("ts-a", "TRACKSTATION", 12345, 77, 3, 9, 1234567, true);
            var d = p.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal("ts-a", d["label"]);
            Assert.Equal("Screenshots/ts-a.map.json", d["path"]);
            Assert.Equal("TRACKSTATION", d["scene"]);
            Assert.Equal("12345", d["bytes"]);
            Assert.Equal("77", d["frame"]);
            Assert.Equal("3", d["ghosts"]);
            Assert.Equal("9", d["lines"]);
            Assert.Equal("1234567", d["points"]);
            Assert.Equal("true", d["truncated"]);
        }

        [Fact]
        public void LogWritten_OneInfoLine_AndNoWarnWhenUnderEveryCap()
        {
            var counts = new MapGeometryCounts { Ghosts = 2, Lines = 5, Points = 300 };
            TestCommandDumpMapGeometry.LogWritten("ts-a", "TRACKSTATION", 900, 42, counts, "0.1");
            Assert.Contains(logLines, l => l.Contains("[TestCommands]") && l.Contains("dumpmapgeometry ok label=ts-a")
                && l.Contains("scene=TRACKSTATION") && l.Contains("ghosts=2") && l.Contains("lines=5")
                && l.Contains("points=300") && l.Contains("truncated=false"));
            Assert.DoesNotContain(logLines, l => l.Contains("dumpmapgeometry truncated"));
        }

        [Fact]
        public void LogWritten_ATruncatedDump_WarnsWithEveryDroppedTotal()
        {
            var counts = new MapGeometryCounts
            {
                Ghosts = 512, Truncated = true, DroppedGhosts = 4, DroppedLines = 6,
            };
            TestCommandDumpMapGeometry.LogWritten("big", "FLIGHT", 1, 1, counts, "0.0");
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[TestCommands]")
                && l.Contains("dumpmapgeometry truncated label=big") && l.Contains("droppedGhosts=4")
                && l.Contains("droppedLines=6")
                && l.Contains("droppedBodies=0"));
        }
    }
}
