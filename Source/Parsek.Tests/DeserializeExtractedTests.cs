using System.Collections.Generic;
using System.Globalization;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for extracted deserialization methods in RecordingStore:
    /// DeserializePoints, DeserializeOrbitSegments, DeserializePartEvents.
    /// These are internal static methods that operate on ConfigNode + Recording,
    /// testable without Unity runtime.
    /// </summary>
    [Collection("Sequential")]
    public class DeserializeExtractedTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public DeserializeExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region DeserializePoints

        [Fact]
        public void DeserializePoints_ParsesBasicPoint()
        {
            var node = new ConfigNode("ROOT");
            var pt = node.AddNode("POINT");
            pt.AddValue("ut", "100.5");
            pt.AddValue("lat", "-0.0972");
            pt.AddValue("lon", "-74.5575");
            pt.AddValue("alt", "77.3");
            pt.AddValue("rotX", "0.1");
            pt.AddValue("rotY", "0.2");
            pt.AddValue("rotZ", "0.3");
            pt.AddValue("rotW", "0.9");
            pt.AddValue("body", "Kerbin");
            pt.AddValue("velX", "10.0");
            pt.AddValue("velY", "20.0");
            pt.AddValue("velZ", "30.0");
            pt.AddValue("funds", "50000.0");
            pt.AddValue("science", "12.5");
            pt.AddValue("rep", "3.0");

            var rec = new Recording();
            RecordingStore.DeserializePoints(node, rec);

            Assert.Single(rec.Points);
            var p = rec.Points[0];
            Assert.Equal(100.5, p.ut, 5);
            Assert.Equal(-0.0972, p.latitude, 4);
            Assert.Equal(-74.5575, p.longitude, 4);
            Assert.Equal(77.3, p.altitude, 1);
            Assert.Equal(0.1, (double)p.rotation.x, 4);
            Assert.Equal(0.2, (double)p.rotation.y, 4);
            Assert.Equal(0.3, (double)p.rotation.z, 4);
            Assert.Equal(0.9, (double)p.rotation.w, 4);
            Assert.Equal("Kerbin", p.bodyName);
            Assert.Equal(10.0, (double)p.velocity.x, 4);
            Assert.Equal(20.0, (double)p.velocity.y, 4);
            Assert.Equal(30.0, (double)p.velocity.z, 4);
            Assert.Equal(50000.0, p.funds, 1);
            Assert.Equal(12.5, (double)p.science, 4);
            Assert.Equal(3.0, (double)p.reputation, 4);
        }

        [Fact]
        public void DeserializePoints_MultiplePoints_PreservesOrder()
        {
            var node = new ConfigNode("ROOT");
            for (int i = 0; i < 3; i++)
            {
                var pt = node.AddNode("POINT");
                pt.AddValue("ut", (100.0 + i * 10).ToString("R", IC));
                pt.AddValue("lat", "0");
                pt.AddValue("lon", "0");
                pt.AddValue("alt", "100");
                pt.AddValue("rotX", "0"); pt.AddValue("rotY", "0");
                pt.AddValue("rotZ", "0"); pt.AddValue("rotW", "1");
                pt.AddValue("body", "Kerbin");
                pt.AddValue("velX", "0"); pt.AddValue("velY", "0"); pt.AddValue("velZ", "0");
                pt.AddValue("funds", "0"); pt.AddValue("science", "0"); pt.AddValue("rep", "0");
            }

            var rec = new Recording();
            RecordingStore.DeserializePoints(node, rec);

            Assert.Equal(3, rec.Points.Count);
            Assert.Equal(100.0, rec.Points[0].ut, 5);
            Assert.Equal(110.0, rec.Points[1].ut, 5);
            Assert.Equal(120.0, rec.Points[2].ut, 5);
        }

        [Fact]
        public void DeserializePoints_NoPointNodes_LeavesEmpty()
        {
            var node = new ConfigNode("ROOT");
            var rec = new Recording();

            RecordingStore.DeserializePoints(node, rec);

            Assert.Empty(rec.Points);
        }

        [Fact]
        public void DeserializePoints_MissingBody_DefaultsToKerbin()
        {
            var node = new ConfigNode("ROOT");
            var pt = node.AddNode("POINT");
            pt.AddValue("ut", "100");
            pt.AddValue("lat", "0"); pt.AddValue("lon", "0"); pt.AddValue("alt", "0");
            pt.AddValue("rotX", "0"); pt.AddValue("rotY", "0");
            pt.AddValue("rotZ", "0"); pt.AddValue("rotW", "1");
            // No body value
            pt.AddValue("velX", "0"); pt.AddValue("velY", "0"); pt.AddValue("velZ", "0");
            pt.AddValue("funds", "0"); pt.AddValue("science", "0"); pt.AddValue("rep", "0");

            var rec = new Recording();
            RecordingStore.DeserializePoints(node, rec);

            Assert.Single(rec.Points);
            Assert.Equal("Kerbin", rec.Points[0].bodyName);
        }

        [Fact]
        public void DeserializePoints_LogsWarningForUnparseableUT()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var node = new ConfigNode("ROOT");
                var pt = node.AddNode("POINT");
                pt.AddValue("ut", "NOT_A_NUMBER");
                pt.AddValue("lat", "0"); pt.AddValue("lon", "0"); pt.AddValue("alt", "0");
                pt.AddValue("rotX", "0"); pt.AddValue("rotY", "0");
                pt.AddValue("rotZ", "0"); pt.AddValue("rotW", "1");
                pt.AddValue("body", "Kerbin");
                pt.AddValue("velX", "0"); pt.AddValue("velY", "0"); pt.AddValue("velZ", "0");
                pt.AddValue("funds", "0"); pt.AddValue("science", "0"); pt.AddValue("rep", "0");

                var rec = new Recording();
                RecordingStore.DeserializePoints(node, rec);

                Assert.Single(rec.Points);
                Assert.Contains(logLines, l =>
                    l.Contains("unparseable UT"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                RecordingStore.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        #endregion

        #region DeserializeOrbitSegments

        [Fact]
        public void DeserializeOrbitSegments_ParsesBasicSegment()
        {
            var node = new ConfigNode("ROOT");
            var seg = node.AddNode("ORBIT_SEGMENT");
            seg.AddValue("startUT", "1000");
            seg.AddValue("endUT", "2000");
            seg.AddValue("inc", "28.5");
            seg.AddValue("ecc", "0.01");
            seg.AddValue("sma", "700000");
            seg.AddValue("lan", "90.0");
            seg.AddValue("argPe", "45.0");
            seg.AddValue("mna", "0.5");
            seg.AddValue("epoch", "1000");
            seg.AddValue("body", "Kerbin");

            var rec = new Recording();
            RecordingStore.DeserializeOrbitSegments(node, rec);

            Assert.Single(rec.OrbitSegments);
            var s = rec.OrbitSegments[0];
            Assert.Equal(1000.0, s.startUT, 1);
            Assert.Equal(2000.0, s.endUT, 1);
            Assert.Equal(28.5, s.inclination, 1);
            Assert.Equal(0.01, s.eccentricity, 4);
            Assert.Equal(700000.0, s.semiMajorAxis, 1);
            Assert.Equal(90.0, s.longitudeOfAscendingNode, 1);
            Assert.Equal(45.0, s.argumentOfPeriapsis, 1);
            Assert.Equal(0.5, s.meanAnomalyAtEpoch, 4);
            Assert.Equal(1000.0, s.epoch, 1);
            Assert.Equal("Kerbin", s.bodyName);
        }

        [Fact]
        public void DeserializeOrbitSegments_ParsesOrbitalFrameRotation()
        {
            var node = new ConfigNode("ROOT");
            var seg = node.AddNode("ORBIT_SEGMENT");
            seg.AddValue("startUT", "1000"); seg.AddValue("endUT", "2000");
            seg.AddValue("inc", "0"); seg.AddValue("ecc", "0"); seg.AddValue("sma", "700000");
            seg.AddValue("lan", "0"); seg.AddValue("argPe", "0"); seg.AddValue("mna", "0");
            seg.AddValue("epoch", "0"); seg.AddValue("body", "Kerbin");
            seg.AddValue("ofrX", "0.1");
            seg.AddValue("ofrY", "0.2");
            seg.AddValue("ofrZ", "0.3");
            seg.AddValue("ofrW", "0.9");

            var rec = new Recording();
            RecordingStore.DeserializeOrbitSegments(node, rec);

            Assert.Single(rec.OrbitSegments);
            var s = rec.OrbitSegments[0];
            Assert.Equal(0.1, (double)s.orbitalFrameRotation.x, 4);
            Assert.Equal(0.2, (double)s.orbitalFrameRotation.y, 4);
            Assert.Equal(0.3, (double)s.orbitalFrameRotation.z, 4);
            Assert.Equal(0.9, (double)s.orbitalFrameRotation.w, 4);
        }

        [Fact]
        public void DeserializeOrbitSegments_ParsesAngularVelocity()
        {
            var node = new ConfigNode("ROOT");
            var seg = node.AddNode("ORBIT_SEGMENT");
            seg.AddValue("startUT", "1000"); seg.AddValue("endUT", "2000");
            seg.AddValue("inc", "0"); seg.AddValue("ecc", "0"); seg.AddValue("sma", "700000");
            seg.AddValue("lan", "0"); seg.AddValue("argPe", "0"); seg.AddValue("mna", "0");
            seg.AddValue("epoch", "0"); seg.AddValue("body", "Kerbin");
            seg.AddValue("avX", "0.5");
            seg.AddValue("avY", "1.0");
            seg.AddValue("avZ", "1.5");

            var rec = new Recording();
            RecordingStore.DeserializeOrbitSegments(node, rec);

            Assert.Single(rec.OrbitSegments);
            var s = rec.OrbitSegments[0];
            Assert.Equal(0.5, (double)s.angularVelocity.x, 4);
            Assert.Equal(1.0, (double)s.angularVelocity.y, 4);
            Assert.Equal(1.5, (double)s.angularVelocity.z, 4);
        }

        [Fact]
        public void DeserializeOrbitSegments_NoSegmentNodes_LeavesEmpty()
        {
            var node = new ConfigNode("ROOT");
            var rec = new Recording();

            RecordingStore.DeserializeOrbitSegments(node, rec);

            Assert.Empty(rec.OrbitSegments);
        }

        #endregion

        #region DeserializePartEvents

        [Fact]
        public void DeserializePartEvents_ParsesBasicEvent()
        {
            var node = new ConfigNode("ROOT");
            var evt = node.AddNode("PART_EVENT");
            evt.AddValue("ut", "150.5");
            evt.AddValue("pid", "100000");
            evt.AddValue("type", ((int)PartEventType.EngineIgnited).ToString());
            evt.AddValue("part", "liquidEngine1-2");
            evt.AddValue("value", "0.75");
            evt.AddValue("midx", "1");

            var rec = new Recording();
            RecordingStore.DeserializePartEvents(node, rec);

            Assert.Single(rec.PartEvents);
            var e = rec.PartEvents[0];
            Assert.Equal(150.5, e.ut, 5);
            Assert.Equal(100000u, e.partPersistentId);
            Assert.Equal(PartEventType.EngineIgnited, e.eventType);
            Assert.Equal("liquidEngine1-2", e.partName);
            Assert.Equal(0.75, (double)e.value, 4);
            Assert.Equal(1, e.moduleIndex);
        }

        [Fact]
        public void DeserializePartEvents_MultipleEvents_PreservesOrder()
        {
            var node = new ConfigNode("ROOT");
            for (int i = 0; i < 3; i++)
            {
                var evt = node.AddNode("PART_EVENT");
                evt.AddValue("ut", (100.0 + i * 5).ToString("R", IC));
                evt.AddValue("pid", (100000 + i).ToString());
                evt.AddValue("type", ((int)PartEventType.Decoupled).ToString());
                evt.AddValue("part", $"part{i}");
                evt.AddValue("value", "0");
                evt.AddValue("midx", "0");
            }

            var rec = new Recording();
            RecordingStore.DeserializePartEvents(node, rec);

            Assert.Equal(3, rec.PartEvents.Count);
            Assert.Equal(100.0, rec.PartEvents[0].ut, 5);
            Assert.Equal(105.0, rec.PartEvents[1].ut, 5);
            Assert.Equal(110.0, rec.PartEvents[2].ut, 5);
        }

        [Fact]
        public void DeserializePartEvents_NoEventNodes_LeavesEmpty()
        {
            var node = new ConfigNode("ROOT");
            var rec = new Recording();

            RecordingStore.DeserializePartEvents(node, rec);

            Assert.Empty(rec.PartEvents);
        }

        [Fact]
        public void DeserializePartEvents_MissingOptionalFields_DefaultsToZero()
        {
            var node = new ConfigNode("ROOT");
            var evt = node.AddNode("PART_EVENT");
            evt.AddValue("ut", "200.0");
            evt.AddValue("pid", "100000");
            evt.AddValue("type", ((int)PartEventType.Destroyed).ToString());
            evt.AddValue("part", "somePart");
            // No value or midx fields

            var rec = new Recording();
            RecordingStore.DeserializePartEvents(node, rec);

            Assert.Single(rec.PartEvents);
            Assert.Equal(0.0, (double)rec.PartEvents[0].value, 4);
            Assert.Equal(0, rec.PartEvents[0].moduleIndex);
        }

        [Fact]
        public void DeserializePartEvents_UnknownEventType_LogsWarning()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var node = new ConfigNode("ROOT");
                var evt = node.AddNode("PART_EVENT");
                evt.AddValue("ut", "200.0");
                evt.AddValue("pid", "100000");
                evt.AddValue("type", "99999"); // Unknown type
                evt.AddValue("part", "somePart");
                evt.AddValue("value", "0");
                evt.AddValue("midx", "0");

                var rec = new Recording();
                RecordingStore.DeserializePartEvents(node, rec);

                Assert.Empty(rec.PartEvents); // unknown types are now skipped
                Assert.Contains(logLines, l =>
                    l.Contains("Skipping unknown PartEvent type") && l.Contains("99999"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                RecordingStore.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        #endregion

        #region DeserializeTrajectoryFrom roundtrip

        [Fact]
        public void SerializeDeserialize_RoundTrip_PreservesAllData()
        {
            // Build a recording with known data
            var original = new Recording();
            original.Points.Add(new TrajectoryPoint
            {
                ut = 17000.0,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 77.3,
                rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                bodyName = "Kerbin",
                velocity = new Vector3(10, 20, 30),
                funds = 50000,
                science = 12.5f,
                reputation = 3.0f
            });
            original.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 17100,
                endUT = 17200,
                inclination = 28.5,
                eccentricity = 0.01,
                semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = 17100,
                bodyName = "Kerbin"
            });
            original.PartEvents.Add(new PartEvent
            {
                ut = 17050,
                partPersistentId = 100000,
                eventType = PartEventType.EngineIgnited,
                partName = "liquidEngine1-2",
                value = 1.0f,
                moduleIndex = 0
            });

            // Serialize
            var serialized = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(serialized, original);

            // Deserialize into fresh recording
            var restored = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(serialized, restored);

            // Verify points
            Assert.Single(restored.Points);
            Assert.Equal(original.Points[0].ut, restored.Points[0].ut, 10);
            Assert.Equal(original.Points[0].latitude, restored.Points[0].latitude, 10);
            Assert.Equal(original.Points[0].funds, restored.Points[0].funds, 1);

            // Verify orbit segments
            Assert.Single(restored.OrbitSegments);
            Assert.Equal(original.OrbitSegments[0].inclination, restored.OrbitSegments[0].inclination, 5);
            Assert.Equal(original.OrbitSegments[0].bodyName, restored.OrbitSegments[0].bodyName);

            // Verify part events
            Assert.Single(restored.PartEvents);
            Assert.Equal(original.PartEvents[0].ut, restored.PartEvents[0].ut, 5);
            Assert.Equal(original.PartEvents[0].partPersistentId, restored.PartEvents[0].partPersistentId);
            Assert.Equal(original.PartEvents[0].eventType, restored.PartEvents[0].eventType);
            Assert.Equal((double)original.PartEvents[0].value, (double)restored.PartEvents[0].value, 4);
            Assert.Equal(original.PartEvents[0].moduleIndex, restored.PartEvents[0].moduleIndex);
        }

        #endregion
    }
}
