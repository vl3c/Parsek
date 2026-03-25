using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FlagEventTests : IDisposable
    {
        public FlagEventTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void FlagEvent_SerializationRoundTrip()
        {
            var rec = new Recording();
            rec.FlagEvents.Add(new FlagEvent
            {
                ut = 12345.678,
                flagSiteName = "First Landing",
                placedBy = "Jeb Kerman",
                plaqueText = "One small step",
                flagURL = "Squad/Flags/default",
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 66.5,
                rotX = 0.1f,
                rotY = 0.2f,
                rotZ = 0.3f,
                rotW = 0.9f,
                bodyName = "Kerbin"
            });
            rec.FlagEvents.Add(new FlagEvent
            {
                ut = 12400.0,
                flagSiteName = "Second Flag",
                placedBy = "Bill Kerman",
                plaqueText = "",
                flagURL = "Squad/Flags/stiny",
                latitude = 1.5,
                longitude = 80.0,
                altitude = 1200.0,
                rotX = 0f,
                rotY = 0f,
                rotZ = 0f,
                rotW = 1f,
                bodyName = "Mun"
            });

            // Serialize
            var targetNode = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(targetNode, rec);

            // Deserialize
            var deserialized = new Recording();
            RecordingStore.DeserializeFlagEvents(targetNode, deserialized);

            Assert.Equal(2, deserialized.FlagEvents.Count);

            var evt0 = deserialized.FlagEvents[0];
            Assert.Equal(12345.678, evt0.ut);
            Assert.Equal("First Landing", evt0.flagSiteName);
            Assert.Equal("Jeb Kerman", evt0.placedBy);
            Assert.Equal("One small step", evt0.plaqueText);
            Assert.Equal("Squad/Flags/default", evt0.flagURL);
            Assert.Equal(-0.0972, evt0.latitude, 6);
            Assert.Equal(-74.5575, evt0.longitude, 6);
            Assert.Equal(66.5, evt0.altitude, 6);
            Assert.Equal(0.1f, evt0.rotX);
            Assert.Equal(0.2f, evt0.rotY);
            Assert.Equal(0.3f, evt0.rotZ);
            Assert.Equal(0.9f, evt0.rotW);
            Assert.Equal("Kerbin", evt0.bodyName);

            var evt1 = deserialized.FlagEvents[1];
            Assert.Equal("Second Flag", evt1.flagSiteName);
            Assert.Equal("Bill Kerman", evt1.placedBy);
            Assert.Equal("Mun", evt1.bodyName);
        }

        [Fact]
        public void FlagEvent_EmptyRecording_NoFlagEvents()
        {
            var rec = new Recording();

            var targetNode = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(targetNode, rec);

            var deserialized = new Recording();
            RecordingStore.DeserializeFlagEvents(targetNode, deserialized);

            Assert.Empty(deserialized.FlagEvents);
        }

        [Fact]
        public void FlagEvent_SpecialCharactersInText()
        {
            var rec = new Recording();
            rec.FlagEvents.Add(new FlagEvent
            {
                ut = 100.0,
                flagSiteName = "Flag with \"quotes\"",
                placedBy = "Val Kerman",
                plaqueText = "Line1\nLine2",
                flagURL = "MyMod/Flags/custom flag",
                latitude = 0,
                longitude = 0,
                altitude = 0,
                rotW = 1f,
                bodyName = "Eve"
            });

            var targetNode = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(targetNode, rec);

            var deserialized = new Recording();
            RecordingStore.DeserializeFlagEvents(targetNode, deserialized);

            Assert.Single(deserialized.FlagEvents);
            Assert.Equal("Flag with \"quotes\"", deserialized.FlagEvents[0].flagSiteName);
            // ConfigNode strips newlines — verify the rest survives
            Assert.Contains("Line1", deserialized.FlagEvents[0].plaqueText);
            Assert.Contains("Line2", deserialized.FlagEvents[0].plaqueText);
            Assert.Equal("MyMod/Flags/custom flag", deserialized.FlagEvents[0].flagURL);
            Assert.Equal("Eve", deserialized.FlagEvents[0].bodyName);
        }

        [Fact]
        public void RecordingBuilder_AddFlagEvent_ProducesConfigNode()
        {
            var builder = new RecordingBuilder("Test Vessel")
                .AddPoint(100, 0, 0, 70)
                .AddPoint(110, 0, 0, 70)
                .AddFlagEvent(105, "Test Flag", "Jeb Kerman", "Hello!",
                    "Squad/Flags/default", -0.1, -74.5, 66.0,
                    body: "Kerbin");

            var node = builder.Build();
            var flagNodes = node.GetNodes("FLAG_EVENT");
            Assert.Single(flagNodes);

            var ic = CultureInfo.InvariantCulture;
            Assert.Equal("Test Flag", flagNodes[0].GetValue("name"));
            Assert.Equal("Jeb Kerman", flagNodes[0].GetValue("placedBy"));
            Assert.Equal("Hello!", flagNodes[0].GetValue("plaqueText"));
            Assert.Equal("Squad/Flags/default", flagNodes[0].GetValue("flagURL"));
            Assert.Equal("Kerbin", flagNodes[0].GetValue("body"));

            double ut;
            double.TryParse(flagNodes[0].GetValue("ut"), NumberStyles.Float, ic, out ut);
            Assert.Equal(105.0, ut);
        }

        [Fact]
        public void RecordingBuilder_FlagEvent_InTrajectoryNode()
        {
            var builder = new RecordingBuilder("Test Vessel")
                .AddPoint(100, 0, 0, 70)
                .AddPoint(110, 0, 0, 70)
                .AddFlagEvent(105, "Test Flag", "Jeb Kerman", "plaque",
                    "Squad/Flags/default", 0, 0, 70);

            var trajNode = builder.BuildTrajectoryNode();
            var flagNodes = trajNode.GetNodes("FLAG_EVENT");
            Assert.Single(flagNodes);
            Assert.Equal("Test Flag", flagNodes[0].GetValue("name"));
        }

        [Fact]
        public void StashPending_WithFlagEvents_Preserved()
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < 5; i++)
                points.Add(new TrajectoryPoint { ut = 100 + i * 10, latitude = i * 0.01 });

            var flagEvents = new List<FlagEvent>
            {
                new FlagEvent { ut = 120, flagSiteName = "Flag1", placedBy = "Jeb", bodyName = "Kerbin" }
            };

            RecordingStore.StashPending(points, "TestVessel", flagEvents: flagEvents);
            Assert.True(RecordingStore.HasPending);
            Assert.Single(RecordingStore.Pending.FlagEvents);
            Assert.Equal("Flag1", RecordingStore.Pending.FlagEvents[0].flagSiteName);
        }

        [Fact]
        public void StashPending_FlagEvents_RetimeBeforeTrim()
        {
            // Create points where the first few are stationary (same position, low speed)
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 70, velocity = new UnityEngine.Vector3(0, 0, 0) },
                new TrajectoryPoint { ut = 110, latitude = 0, longitude = 0, altitude = 70, velocity = new UnityEngine.Vector3(0, 0, 0) },
                new TrajectoryPoint { ut = 120, latitude = 0.01, longitude = 0, altitude = 80, velocity = new UnityEngine.Vector3(0, 10, 0) },
                new TrajectoryPoint { ut = 130, latitude = 0.02, longitude = 0, altitude = 100, velocity = new UnityEngine.Vector3(0, 20, 0) },
                new TrajectoryPoint { ut = 140, latitude = 0.03, longitude = 0, altitude = 130, velocity = new UnityEngine.Vector3(0, 30, 0) },
            };

            var flagEvents = new List<FlagEvent>
            {
                new FlagEvent { ut = 105, flagSiteName = "EarlyFlag", bodyName = "Kerbin" }
            };

            RecordingStore.StashPending(points, "TrimTest", flagEvents: flagEvents);

            // If trimming occurred, the flag event UT should be >= the first point's UT
            double firstPointUT = RecordingStore.Pending.Points[0].ut;
            Assert.True(RecordingStore.Pending.FlagEvents[0].ut >= firstPointUT,
                $"Flag event UT {RecordingStore.Pending.FlagEvents[0].ut} should be >= first point UT {firstPointUT}");
        }

        [Fact]
        public void FlagEvent_ToString_FormatsReadably()
        {
            var evt = new FlagEvent
            {
                ut = 12345.67,
                flagSiteName = "My Flag",
                placedBy = "Jeb",
                latitude = -0.1,
                longitude = -74.5,
                altitude = 66.0,
                bodyName = "Kerbin"
            };
            string s = evt.ToString();
            Assert.Contains("My Flag", s);
            Assert.Contains("Jeb", s);
            Assert.Contains("Kerbin", s);
        }

        [Fact]
        public void Recording_FlagEvents_DefaultsToEmptyList()
        {
            var rec = new Recording();
            Assert.NotNull(rec.FlagEvents);
            Assert.Empty(rec.FlagEvents);
        }

        [Fact]
        public void FlagEvent_MultipleFlags_AllSerialized()
        {
            var rec = new Recording();
            for (int i = 0; i < 5; i++)
            {
                rec.FlagEvents.Add(new FlagEvent
                {
                    ut = 100 + i * 50,
                    flagSiteName = $"Flag {i}",
                    placedBy = "Jeb Kerman",
                    plaqueText = $"Message {i}",
                    flagURL = "Squad/Flags/default",
                    latitude = i * 0.1,
                    longitude = i * 1.0,
                    altitude = 70 + i,
                    rotW = 1f,
                    bodyName = "Kerbin"
                });
            }

            var targetNode = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(targetNode, rec);

            var deserialized = new Recording();
            RecordingStore.DeserializeFlagEvents(targetNode, deserialized);

            Assert.Equal(5, deserialized.FlagEvents.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal($"Flag {i}", deserialized.FlagEvents[i].flagSiteName);
                Assert.Equal($"Message {i}", deserialized.FlagEvents[i].plaqueText);
            }
        }
        [Fact]
        public void ShouldRecordFlagEvent_NullPlacedBy_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldRecordFlagEvent(null, null));
        }

        [Fact]
        public void ShouldRecordFlagEvent_EmptyPlacedBy_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldRecordFlagEvent("", null));
        }

        [Fact]
        public void ShouldRecordFlagEvent_NullVessel_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldRecordFlagEvent("Jeb Kerman", null));
        }

        [Fact]
        public void FormatPlaqueWithDate_AppendsDateToText()
        {
            string result = ParsekFlight.FormatPlaqueWithDate("First landing!", "Year 1, Day 12, 3:45:30");
            Assert.Equal("First landing! - Year 1, Day 12, 3:45:30", result);
        }

        [Fact]
        public void FormatPlaqueWithDate_EmptyText_ReturnsDateOnly()
        {
            string result = ParsekFlight.FormatPlaqueWithDate("", "Year 1, Day 1, 0:00:00");
            Assert.Equal("Year 1, Day 1, 0:00:00", result);
        }

        [Fact]
        public void FormatPlaqueWithDate_NullText_ReturnsDateOnly()
        {
            string result = ParsekFlight.FormatPlaqueWithDate(null, "Year 1, Day 1, 0:00:00");
            Assert.Contains("Year 1, Day 1, 0:00:00", result);
        }

        [Fact]
        public void FormatPlaqueWithDate_EmptyDate_ReturnsOriginalText()
        {
            string result = ParsekFlight.FormatPlaqueWithDate("Hello", "");
            Assert.Equal("Hello", result);
        }

        [Fact]
        public void FormatPlaqueWithDate_BothEmpty_ReturnsEmpty()
        {
            string result = ParsekFlight.FormatPlaqueWithDate("", "");
            Assert.Equal("", result);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }
    }
}
