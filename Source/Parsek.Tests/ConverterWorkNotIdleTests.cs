using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A stationary vessel whose drills or ISRU the player started or stopped during the
    /// recording did work, so the idle-on-pad scene-exit auto-discard must not throw it away
    /// (MINE-6: a rover that mined for 1500 s never moved 30 m and lost its recording on the
    /// Space Center exit). A converter already running when the recording started (a start
    /// seed) is not player work on its own.
    /// </summary>
    [Collection("Sequential")]
    public class ConverterWorkNotIdleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ConverterWorkNotIdleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree RoverTree()
        {
            var rec = new Recording
            {
                RecordingId = "rover",
                VesselName = "Mining Rover RC",
                StartSituation = "Landed",
                MaxDistanceFromLaunch = 0.4
            };
            rec.Points.Add(new TrajectoryPoint { ut = 31.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 1570.0, bodyName = "Kerbin" });
            return new RecordingTree
            {
                Id = "tree-mine6",
                TreeName = "Mining Rover RC",
                Recordings = new Dictionary<string, Recording> { { "rover", rec } }
            };
        }

        private static void AddEvent(RecordingTree tree, double ut, PartEventType type, uint pid)
        {
            tree.Recordings["rover"].PartEvents.Add(new PartEvent
            {
                ut = ut, eventType = type, partPersistentId = pid, partName = "MiniDrill"
            });
        }

        [Fact]
        public void StationaryRoverWithNoConverterWork_StaysIdle()
        {
            RecordingTree tree = RoverTree();
            AddEvent(tree, 31.8, PartEventType.DeployableExtended, 1u);

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Equal(0, ParsekFlight.CountPlayerConverterToggles(tree));
        }

        [Fact]
        public void StationaryRoverThatMined_NotIdle()
        {
            RecordingTree tree = RoverTree();
            for (uint pid = 1; pid <= 4; pid++)
                AddEvent(tree, 59.9, PartEventType.ConverterActivated, pid);
            AddEvent(tree, 480.3, PartEventType.ConverterActivated, 5u);
            AddEvent(tree, 780.3, PartEventType.ConverterDeactivated, 5u);

            Assert.Equal(6, ParsekFlight.CountPlayerConverterToggles(tree));
            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - tree has 6 converter toggle(s)")
                && l.Contains("tree='Mining Rover RC'"));
        }

        [Fact]
        public void ConverterAlreadyRunningAtStart_IsASeed_StaysIdle()
        {
            // A fuel cell or drill running when recording began is seeded at the start UT.
            RecordingTree tree = RoverTree();
            AddEvent(tree, 31.0, PartEventType.ConverterActivated, 7u);

            Assert.Equal(0, ParsekFlight.CountPlayerConverterToggles(tree));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }
    }
}
