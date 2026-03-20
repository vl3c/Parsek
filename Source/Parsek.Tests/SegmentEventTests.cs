using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SegmentEventTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SegmentEventTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region Enum value stability

        [Theory]
        [InlineData(SegmentEventType.ControllerChange, 0)]
        [InlineData(SegmentEventType.ControllerDisabled, 1)]
        [InlineData(SegmentEventType.ControllerEnabled, 2)]
        [InlineData(SegmentEventType.CrewLost, 3)]
        [InlineData(SegmentEventType.CrewTransfer, 4)]
        [InlineData(SegmentEventType.PartDestroyed, 5)]
        [InlineData(SegmentEventType.PartRemoved, 6)]
        [InlineData(SegmentEventType.PartAdded, 7)]
        [InlineData(SegmentEventType.TimeJump, 8)]
        public void SegmentEventType_HasExpectedIntValue(SegmentEventType type, int expected)
        {
            Assert.Equal(expected, (int)type);
        }

        [Fact]
        public void SegmentEventType_AllValues_Contiguous_0_To_8()
        {
            var values = (SegmentEventType[])Enum.GetValues(typeof(SegmentEventType));
            Assert.Equal(9, values.Length);

            for (int i = 0; i < 9; i++)
            {
                Assert.True(Enum.IsDefined(typeof(SegmentEventType), i),
                    $"Expected SegmentEventType to have a value for integer {i}");
            }
        }

        [Fact]
        public void SegmentEventType_AllValues_RoundTripAsInts()
        {
            var ic = CultureInfo.InvariantCulture;
            foreach (SegmentEventType t in Enum.GetValues(typeof(SegmentEventType)))
            {
                string serialized = ((int)t).ToString(ic);
                int parsed;
                Assert.True(int.TryParse(serialized, NumberStyles.Integer, ic, out parsed));
                Assert.True(Enum.IsDefined(typeof(SegmentEventType), parsed));
                Assert.Equal(t, (SegmentEventType)parsed);
            }
        }

        #endregion

        #region Struct defaults

        [Fact]
        public void SegmentEvent_DefaultState_IsZeroed()
        {
            var evt = new SegmentEvent();

            Assert.Equal(0.0, evt.ut);
            Assert.Equal(SegmentEventType.ControllerChange, evt.type);
            Assert.Null(evt.details);
        }

        #endregion

        #region ToString

        [Fact]
        public void ToString_WithDetails_ProducesReadableOutput()
        {
            var evt = new SegmentEvent
            {
                ut = 17005.50,
                type = SegmentEventType.PartDestroyed,
                details = "pid=12345;part=fuelTank"
            };

            string s = evt.ToString();

            Assert.Contains("PartDestroyed", s);
            Assert.Contains("17005.50", s);
            Assert.Contains("pid=12345;part=fuelTank", s);
        }

        [Fact]
        public void ToString_NullDetails_ShowsNone()
        {
            var evt = new SegmentEvent
            {
                ut = 100.0,
                type = SegmentEventType.ControllerDisabled,
                details = null
            };

            string s = evt.ToString();

            Assert.Contains("ControllerDisabled", s);
            Assert.Contains("100.00", s);
            Assert.Contains("none", s);
        }

        [Fact]
        public void ToString_AllTypes_ContainTypeName()
        {
            foreach (SegmentEventType t in Enum.GetValues(typeof(SegmentEventType)))
            {
                var evt = new SegmentEvent { type = t, ut = 0 };
                string s = evt.ToString();
                Assert.Contains(t.ToString(), s);
            }
        }

        #endregion

        #region Log assertion

        [Fact]
        public void LoggedSegmentEvent_ContainsTypeNameAndUT()
        {
            var evt = new SegmentEvent
            {
                ut = 17042.75,
                type = SegmentEventType.CrewTransfer,
                details = "from=pod;to=lab;crew=Jeb"
            };

            ParsekLog.Info("SegmentEvent", evt.ToString());

            Assert.Contains(logLines, l =>
                l.Contains("[SegmentEvent]") &&
                l.Contains("CrewTransfer") &&
                l.Contains("17042.75"));
        }

        [Fact]
        public void LoggedSegmentEvent_NullDetails_LogsNone()
        {
            var evt = new SegmentEvent
            {
                ut = 500.0,
                type = SegmentEventType.ControllerChange,
                details = null
            };

            ParsekLog.Info("SegmentEvent", evt.ToString());

            Assert.Contains(logLines, l =>
                l.Contains("[SegmentEvent]") &&
                l.Contains("ControllerChange") &&
                l.Contains("none"));
        }

        #endregion
    }
}
