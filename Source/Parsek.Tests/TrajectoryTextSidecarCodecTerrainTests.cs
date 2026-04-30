using System.Collections.Generic;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 7 (review follow-up): the readable / debug text mirror
    /// (<see cref="TrajectoryTextSidecarCodec"/>) must carry
    /// <c>recordedGroundClearance</c> alongside the binary codec so
    /// `.prec.txt` debug dumps remain useful for investigating Phase 7
    /// terrain-correction issues.
    ///
    /// <para>The previous codec wrote 15 fields per point (ut/lat/lon/alt/rot/
    /// body/vel/funds/science/rep) and the deserializer always defaulted
    /// clearance to NaN — so a debug-time round-trip silently stripped the
    /// SurfaceMobile clearance. These tests pin the symmetric contract:
    /// finite clearance round-trips, NaN clearance is omitted from the
    /// serialized form (keeps non-SurfaceMobile mirrors terse), legacy
    /// readers without the field default to NaN.</para>
    /// </summary>
    [Collection("Sequential")]
    public class TrajectoryTextSidecarCodecTerrainTests
    {
        // Recording that forces the FLAT serialization path (no
        // TrackSections, no section-authoritative header). Mirrors how a
        // legacy or simple debug-dump recording flows through the codec.
        private static Recording MakeFlatRecording(IEnumerable<TrajectoryPoint> points)
        {
            var rec = new Recording
            {
                RecordingId = "phase7-textcodec-clearance",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            foreach (var p in points) rec.Points.Add(p);
            return rec;
        }

        private static TrajectoryPoint MakePoint(
            double ut, double lat, double lon, double alt,
            double clearance)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(1.5f, 0f, 0f),
                bodyName = "Kerbin",
                funds = 1234.0,
                science = 56.0f,
                reputation = 7.0f,
                recordedGroundClearance = clearance,
            };
        }

        [Fact]
        public void TextRoundTrip_FiniteClearance_PreservesValue()
        {
            var rec = MakeFlatRecording(new[]
            {
                MakePoint(100.0, 0.5, 1.0,  150.0, clearance: 1.5),   // rover at 1.5 m
                MakePoint(101.0, 0.51, 1.01, 152.0, clearance: 1.7),  // small drift
                MakePoint(102.0, 0.52, 1.02, 100.0, clearance: 0.0),  // sitting flat
                MakePoint(103.0, 0.53, 1.03, 95.0, clearance: -5.0),  // mesh embedded
            });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal(rec.Points.Count, restored.Points.Count);
            for (int i = 0; i < rec.Points.Count; i++)
            {
                Assert.Equal(rec.Points[i].recordedGroundClearance,
                    restored.Points[i].recordedGroundClearance);
                // Every other field must round-trip too — guards against a
                // positional-stream desync on the new field.
                Assert.Equal(rec.Points[i].ut, restored.Points[i].ut);
                Assert.Equal(rec.Points[i].latitude, restored.Points[i].latitude);
                Assert.Equal(rec.Points[i].longitude, restored.Points[i].longitude);
                Assert.Equal(rec.Points[i].altitude, restored.Points[i].altitude);
                Assert.Equal(rec.Points[i].bodyName, restored.Points[i].bodyName);
            }
        }

        [Fact]
        public void TextRoundTrip_NaNClearance_RoundTripsAsNaN()
        {
            // Non-SurfaceMobile points carry NaN clearance and the writer
            // must skip the `clearance` value entirely (terse mirrors).
            // The reader's field-init default kicks in.
            var rec = MakeFlatRecording(new[]
            {
                MakePoint(100.0, 0.0, 0.0, 78000.0, clearance: double.NaN),
                MakePoint(101.0, 0.001, 0.001, 80000.0, clearance: double.NaN),
            });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            // Confirm no `clearance` value emitted on the wire.
            var pointNodes = node.GetNodes("POINT");
            Assert.Equal(2, pointNodes.Length);
            foreach (var pn in pointNodes)
            {
                Assert.False(pn.HasValue("clearance"),
                    "NaN clearance must NOT be serialized — keeps debug mirrors terse");
            }

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);
            Assert.Equal(2, restored.Points.Count);
            Assert.True(double.IsNaN(restored.Points[0].recordedGroundClearance));
            Assert.True(double.IsNaN(restored.Points[1].recordedGroundClearance));
        }

        [Fact]
        public void TextRead_LegacyMirrorWithoutClearanceField_DefaultsToNaN()
        {
            // Simulate a legacy `.prec.txt` produced before the Phase 7 text-
            // codec extension: every POINT lacks the `clearance` value. The
            // reader must default to NaN ⇒ legacy fall-through to stored
            // altitude at render time, exactly mirroring the binary codec's
            // v8-legacy contract.
            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            ConfigNode pt = node.AddNode("POINT");
            pt.AddValue("ut", "100.0");
            pt.AddValue("lat", "0.5");
            pt.AddValue("lon", "1.0");
            pt.AddValue("alt", "150.0");
            pt.AddValue("rotX", "0.0"); pt.AddValue("rotY", "0.0");
            pt.AddValue("rotZ", "0.0"); pt.AddValue("rotW", "1.0");
            pt.AddValue("body", "Kerbin");
            pt.AddValue("velX", "0.0"); pt.AddValue("velY", "0.0"); pt.AddValue("velZ", "0.0");
            pt.AddValue("funds", "0.0"); pt.AddValue("science", "0.0"); pt.AddValue("rep", "0.0");
            // No `clearance` value — legacy mirror.

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Single(restored.Points);
            Assert.True(double.IsNaN(restored.Points[0].recordedGroundClearance),
                "Legacy text mirror without `clearance` value must default to NaN");
            Assert.Equal(150.0, restored.Points[0].altitude);
        }

        [Fact]
        public void TextRoundTrip_MixedFiniteAndNaN_PreservesPerPointSemantics()
        {
            // A recording with mixed sections (some SurfaceMobile, others
            // ExoBallistic) flushes through the same text codec. Every point
            // must round-trip its own clearance (or NaN) independently.
            var rec = MakeFlatRecording(new[]
            {
                MakePoint(100.0, 0.5, 1.0, 150.0, clearance: 1.5),       // SurfaceMobile
                MakePoint(101.0, 0.51, 1.01, 200.0, clearance: 2.0),     // SurfaceMobile
                MakePoint(102.0, 0.0, 0.0, 78000.0, clearance: double.NaN), // ExoBallistic (cleared section)
                MakePoint(103.0, 0.7, 1.5, 50.0, clearance: 0.5),        // SurfaceMobile again
            });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal(4, restored.Points.Count);
            Assert.Equal(1.5, restored.Points[0].recordedGroundClearance);
            Assert.Equal(2.0, restored.Points[1].recordedGroundClearance);
            Assert.True(double.IsNaN(restored.Points[2].recordedGroundClearance));
            Assert.Equal(0.5, restored.Points[3].recordedGroundClearance);
        }
    }
}
