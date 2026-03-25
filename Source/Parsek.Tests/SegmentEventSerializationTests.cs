using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for SegmentEvent serialization/deserialization in RecordingStore.
    /// Verifies round-trip fidelity, backward compatibility, and error handling.
    /// </summary>
    [Collection("Sequential")]
    public class SegmentEventSerializationTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public SegmentEventSerializationTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
        }

        #region Round-trip tests

        [Fact]
        public void RoundTrip_ThreeEvents_AllFieldsPreserved()
        {
            var events = new List<SegmentEvent>
            {
                new SegmentEvent { ut = 1000.5, type = SegmentEventType.ControllerChange, details = "decoupled stage 1" },
                new SegmentEvent { ut = 2000.75, type = SegmentEventType.PartRemoved, details = "Kerbin -> Mun" },
                new SegmentEvent { ut = 3000.125, type = SegmentEventType.ControllerEnabled, details = "lithobraking" }
            };

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Equal(3, deserialized.Count);

            Assert.Equal(1000.5, deserialized[0].ut);
            Assert.Equal(SegmentEventType.ControllerChange, deserialized[0].type);
            Assert.Equal("decoupled stage 1", deserialized[0].details);

            Assert.Equal(2000.75, deserialized[1].ut);
            Assert.Equal(SegmentEventType.PartRemoved, deserialized[1].type);
            Assert.Equal("Kerbin -> Mun", deserialized[1].details);

            Assert.Equal(3000.125, deserialized[2].ut);
            Assert.Equal(SegmentEventType.ControllerEnabled, deserialized[2].type);
            Assert.Equal("lithobraking", deserialized[2].details);
        }

        [Fact]
        public void RoundTrip_NullDetails_SerializesWithoutDetailsKey()
        {
            var events = new List<SegmentEvent>
            {
                new SegmentEvent { ut = 500.0, type = SegmentEventType.CrewTransfer, details = null }
            };

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            // Verify no details key was written
            var seNodes = node.GetNodes("SEGMENT_EVENT");
            Assert.Single(seNodes);
            Assert.Null(seNodes[0].GetValue("details"));

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Single(deserialized);
            Assert.Equal(500.0, deserialized[0].ut);
            Assert.Equal(SegmentEventType.CrewTransfer, deserialized[0].type);
            Assert.Null(deserialized[0].details);
        }

        [Fact]
        public void RoundTrip_EmptyDetails_SerializesWithoutDetailsKey()
        {
            var events = new List<SegmentEvent>
            {
                new SegmentEvent { ut = 600.0, type = SegmentEventType.CrewLost, details = "" }
            };

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            // Empty string treated same as null — no details key
            var seNodes = node.GetNodes("SEGMENT_EVENT");
            Assert.Single(seNodes);
            Assert.Null(seNodes[0].GetValue("details"));

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Single(deserialized);
            Assert.Null(deserialized[0].details);
        }

        [Fact]
        public void RoundTrip_AllEventTypes_PreservedExactly()
        {
            var events = new List<SegmentEvent>();
            foreach (SegmentEventType t in Enum.GetValues(typeof(SegmentEventType)))
            {
                events.Add(new SegmentEvent { ut = 100.0 + (int)t, type = t, details = t.ToString() });
            }

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Equal(events.Count, deserialized.Count);
            for (int i = 0; i < events.Count; i++)
            {
                Assert.Equal(events[i].ut, deserialized[i].ut);
                Assert.Equal(events[i].type, deserialized[i].type);
                Assert.Equal(events[i].details, deserialized[i].details);
            }
        }

        #endregion

        #region Empty / backward compat

        [Fact]
        public void EmptyList_NoSegmentEventNodes()
        {
            var events = new List<SegmentEvent>();

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            var seNodes = node.GetNodes("SEGMENT_EVENT");
            Assert.Empty(seNodes);

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Empty(deserialized);
        }

        [Fact]
        public void BackwardCompat_OldPrecWithoutSegmentEventNodes_EmptyList()
        {
            // Simulate an old .prec file that has POINT and PART_EVENT but no SEGMENT_EVENT
            var node = new ConfigNode("ROOT");
            var pt = node.AddNode("POINT");
            pt.AddValue("ut", "100");
            pt.AddValue("lat", "0"); pt.AddValue("lon", "0"); pt.AddValue("alt", "100");
            pt.AddValue("rotX", "0"); pt.AddValue("rotY", "0");
            pt.AddValue("rotZ", "0"); pt.AddValue("rotW", "1");
            pt.AddValue("body", "Kerbin");
            pt.AddValue("velX", "0"); pt.AddValue("velY", "0"); pt.AddValue("velZ", "0");
            pt.AddValue("funds", "0"); pt.AddValue("science", "0"); pt.AddValue("rep", "0");

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Empty(deserialized);
        }

        #endregion

        #region Error handling

        [Fact]
        public void UnknownType_SkippedGracefully()
        {
            var node = new ConfigNode("ROOT");
            var se = node.AddNode("SEGMENT_EVENT");
            se.AddValue("ut", "1000.0");
            se.AddValue("type", "99");
            se.AddValue("details", "future event type");

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var deserialized = new List<SegmentEvent>();
                RecordingStore.DeserializeSegmentEvents(node, deserialized);

                Assert.Empty(deserialized);
                Assert.Contains(logLines, l =>
                    l.Contains("unknown type=99"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                RecordingStore.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void MissingUT_SkippedGracefully()
        {
            var node = new ConfigNode("ROOT");
            var se = node.AddNode("SEGMENT_EVENT");
            // No ut key
            se.AddValue("type", "0");
            se.AddValue("details", "no timestamp");

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var deserialized = new List<SegmentEvent>();
                RecordingStore.DeserializeSegmentEvents(node, deserialized);

                Assert.Empty(deserialized);
                Assert.Contains(logLines, l =>
                    l.Contains("missing or unparseable ut"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                RecordingStore.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void MixedValidAndInvalid_OnlyValidDeserialized()
        {
            var node = new ConfigNode("ROOT");

            // Valid event
            var se1 = node.AddNode("SEGMENT_EVENT");
            se1.AddValue("ut", "100.0");
            se1.AddValue("type", "0");
            se1.AddValue("details", "good event");

            // Bad: unknown type
            var se2 = node.AddNode("SEGMENT_EVENT");
            se2.AddValue("ut", "200.0");
            se2.AddValue("type", "99");

            // Bad: missing ut
            var se3 = node.AddNode("SEGMENT_EVENT");
            se3.AddValue("type", "1");

            // Valid event
            var se4 = node.AddNode("SEGMENT_EVENT");
            se4.AddValue("ut", "400.0");
            se4.AddValue("type", "3");

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Equal(2, deserialized.Count);
            Assert.Equal(100.0, deserialized[0].ut);
            Assert.Equal(SegmentEventType.ControllerChange, deserialized[0].type);
            Assert.Equal(400.0, deserialized[1].ut);
            Assert.Equal(SegmentEventType.CrewLost, deserialized[1].type);
        }

        #endregion

        #region Ordering

        [Fact]
        public void OrderingPreserved_SerializeDeserialize()
        {
            var events = new List<SegmentEvent>
            {
                new SegmentEvent { ut = 300.0, type = SegmentEventType.PartDestroyed, details = "third by ut" },
                new SegmentEvent { ut = 100.0, type = SegmentEventType.ControllerChange, details = "first by ut" },
                new SegmentEvent { ut = 200.0, type = SegmentEventType.PartAdded, details = "second by ut" }
            };

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            // Ordering must match serialization order, not UT order
            Assert.Equal(3, deserialized.Count);
            Assert.Equal(300.0, deserialized[0].ut);
            Assert.Equal(100.0, deserialized[1].ut);
            Assert.Equal(200.0, deserialized[2].ut);
        }

        #endregion

        #region Log assertions

        // Serialize_LogsEventCount removed: per-recording verbose log replaced with batch summary

        [Fact]
        public void UnknownType_LogsWarningWithBadValue()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var node = new ConfigNode("ROOT");
                var se = node.AddNode("SEGMENT_EVENT");
                se.AddValue("ut", "500.0");
                se.AddValue("type", "42");

                var deserialized = new List<SegmentEvent>();
                RecordingStore.DeserializeSegmentEvents(node, deserialized);

                Assert.Contains(logLines, l =>
                    l.Contains("WARN") && l.Contains("unknown type=42"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                RecordingStore.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        #endregion

        #region InvariantCulture precision

        [Fact]
        public void InvariantCulture_HighPrecisionUT_SurvivesRoundTrip()
        {
            // Use a UT with many significant digits to test "R" format round-trip
            double preciseUT = 17042.123456789012345;
            var events = new List<SegmentEvent>
            {
                new SegmentEvent { ut = preciseUT, type = SegmentEventType.PartRemoved, details = "precision test" }
            };

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Single(deserialized);
            // "R" format guarantees exact double round-trip
            Assert.Equal(preciseUT, deserialized[0].ut);
        }

        [Fact]
        public void InvariantCulture_NegativeUT_SurvivesRoundTrip()
        {
            // Edge case: negative UT (shouldn't happen in KSP but serialization must handle it)
            double negativeUT = -123.456789;
            var events = new List<SegmentEvent>
            {
                new SegmentEvent { ut = negativeUT, type = SegmentEventType.CrewTransfer }
            };

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeSegmentEvents(node, events);

            var deserialized = new List<SegmentEvent>();
            RecordingStore.DeserializeSegmentEvents(node, deserialized);

            Assert.Single(deserialized);
            Assert.Equal(negativeUT, deserialized[0].ut);
        }

        #endregion

        #region Integration with DeserializeTrajectoryFrom

        [Fact]
        public void DeserializeTrajectoryFrom_IncludesSegmentEvents()
        {
            // Build a trajectory node with points and segment events
            var node = new ConfigNode("ROOT");

            var pt = node.AddNode("POINT");
            pt.AddValue("ut", "100");
            pt.AddValue("lat", "0"); pt.AddValue("lon", "0"); pt.AddValue("alt", "100");
            pt.AddValue("rotX", "0"); pt.AddValue("rotY", "0");
            pt.AddValue("rotZ", "0"); pt.AddValue("rotW", "1");
            pt.AddValue("body", "Kerbin");
            pt.AddValue("velX", "0"); pt.AddValue("velY", "0"); pt.AddValue("velZ", "0");
            pt.AddValue("funds", "0"); pt.AddValue("science", "0"); pt.AddValue("rep", "0");

            var se = node.AddNode("SEGMENT_EVENT");
            se.AddValue("ut", "150.0");
            se.AddValue("type", ((int)SegmentEventType.ControllerChange).ToString(IC));
            se.AddValue("details", "stage separation");

            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.Points);
            Assert.Single(rec.SegmentEvents);
            Assert.Equal(150.0, rec.SegmentEvents[0].ut);
            Assert.Equal(SegmentEventType.ControllerChange, rec.SegmentEvents[0].type);
            Assert.Equal("stage separation", rec.SegmentEvents[0].details);
        }

        [Fact]
        public void SerializeTrajectoryInto_IncludesSegmentEvents()
        {
            var rec = new Recording();
            rec.SegmentEvents.Add(new SegmentEvent
            {
                ut = 250.0,
                type = SegmentEventType.PartAdded,
                details = "port A -> port B"
            });

            var node = new ConfigNode("ROOT");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            var seNodes = node.GetNodes("SEGMENT_EVENT");
            Assert.Single(seNodes);
            Assert.Equal("250", seNodes[0].GetValue("ut"));
            Assert.Equal("7", seNodes[0].GetValue("type"));
            Assert.Equal("port A -> port B", seNodes[0].GetValue("details"));
        }

        #endregion
    }
}
