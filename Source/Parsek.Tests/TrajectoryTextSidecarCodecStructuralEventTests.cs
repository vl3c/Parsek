using System.Collections.Generic;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 9 (review follow-up): the readable / debug text mirror
    /// (<see cref="TrajectoryTextSidecarCodec"/>) must carry the per-point
    /// <c>flags</c> byte alongside the binary codec so `.prec.txt` debug
    /// dumps remain useful for investigating Phase 9 structural-event
    /// alignment.
    ///
    /// <para>The previous codec wrote 15 fields per point (no `flags`) and
    /// the deserializer always defaulted <c>flags = 0</c> via the value-type
    /// initializer — so a debug-time round-trip silently stripped the
    /// <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/> bit. These
    /// tests pin the symmetric contract: flagged points round-trip the bit,
    /// flags=0 points are omitted from the serialized form (keeps debug
    /// mirrors terse — most points have no flags), legacy readers without
    /// the field default to 0.</para>
    /// </summary>
    [Collection("Sequential")]
    public class TrajectoryTextSidecarCodecStructuralEventTests
    {
        private static Recording MakeFlatRecording(IEnumerable<TrajectoryPoint> points)
        {
            var rec = new Recording
            {
                RecordingId = "phase9-textcodec-flags",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            foreach (var p in points) rec.Points.Add(p);
            return rec;
        }

        private static TrajectoryPoint MakePoint(double ut, byte flags)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.5,
                longitude = 1.0,
                altitude = 150.0,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(1.5f, 0f, 0f),
                bodyName = "Kerbin",
                funds = 1234.0,
                science = 56.0f,
                reputation = 7.0f,
                recordedGroundClearance = double.NaN,
                flags = flags,
            };
        }

        [Fact]
        public void TextRoundTrip_StructuralEventFlag_PreservesBit()
        {
            var rec = MakeFlatRecording(new[]
            {
                MakePoint(100.0, flags: 0),                  // regular tick
                MakePoint(101.0, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
                MakePoint(102.0, flags: 0),                  // regular tick
                MakePoint(103.0, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
            });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal(4, restored.Points.Count);
            Assert.Equal((byte)0, restored.Points[0].flags);
            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                restored.Points[1].flags);
            Assert.Equal((byte)0, restored.Points[2].flags);
            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                restored.Points[3].flags);
        }

        [Fact]
        public void TextSerialize_FlagsZero_OmitsFieldOnTheWire()
        {
            // flags=0 is the dominant case (every regular tick sample). The
            // writer must skip the `flags` value to keep debug mirrors terse
            // and make flagged points stand out at a glance.
            var rec = MakeFlatRecording(new[]
            {
                MakePoint(100.0, flags: 0),
                MakePoint(101.0, flags: 0),
            });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var pointNodes = node.GetNodes("POINT");
            Assert.Equal(2, pointNodes.Length);
            foreach (var pn in pointNodes)
            {
                Assert.False(pn.HasValue("flags"),
                    "flags=0 must NOT be serialized — keeps debug mirrors terse for the dominant case");
            }
        }

        [Fact]
        public void TextSerialize_FlaggedPoint_EmitsFlagsValue()
        {
            // Inverse of the above: flagged points MUST emit the value so the
            // debug mirror shows them.
            var rec = MakeFlatRecording(new[]
            {
                MakePoint(100.0, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot),
            });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var pointNodes = node.GetNodes("POINT");
            Assert.Single(pointNodes);
            Assert.True(pointNodes[0].HasValue("flags"),
                "Structural-event-flagged point must emit the `flags` value");
            Assert.Equal("1", pointNodes[0].GetValue("flags"));
        }

        [Fact]
        public void TextRead_LegacyMirrorWithoutFlagsField_DefaultsToZero()
        {
            // Simulate a legacy `.prec.txt` produced before the Phase 9 text-
            // codec extension: every POINT lacks the `flags` value. The
            // reader must default to 0 ⇒ AnchorCandidateBuilder's flagged-
            // sample lookup misses and falls through to interpolated event ε
            // (design doc §15.17), exactly mirroring the binary codec's
            // v9-legacy contract.
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
            // No `flags` value — legacy mirror.

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Single(restored.Points);
            Assert.Equal((byte)0, restored.Points[0].flags);
        }

        [Fact]
        public void TextRoundTrip_FutureBitAtBitOne_RoundsTrip()
        {
            // The `flags` byte has bits 1-7 reserved. A future codec that sets
            // bit 1 alongside bit 0 must round-trip both bits — the parser is
            // a plain byte parse, not a flag-name lookup.
            var rec = MakeFlatRecording(new[]
            {
                MakePoint(100.0, flags: 0x03),  // bit 0 + bit 1
                MakePoint(101.0, flags: 0x80),  // bit 7 only
            });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Equal((byte)0x03, restored.Points[0].flags);
            Assert.Equal((byte)0x80, restored.Points[1].flags);
        }

        [Fact]
        public void TextRoundTrip_ClearanceAndFlags_BothPreservedIndependently()
        {
            // A SurfaceMobile structural-event snapshot would carry BOTH a
            // finite clearance AND the StructuralEventSnapshot flag. Pin the
            // independence so future reorderings of the codec don't lose one
            // when the other is set.
            var pt = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 0.5,
                longitude = 1.0,
                altitude = 150.0,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 0f, 0f),
                bodyName = "Kerbin",
                funds = 0.0,
                science = 0.0f,
                reputation = 0.0f,
                recordedGroundClearance = 1.5,
                flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot,
            };
            var rec = MakeFlatRecording(new[] { pt });

            var node = new ConfigNode("PARSEK_TRAJECTORY_TEST");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var restored = new Recording();
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Single(restored.Points);
            Assert.Equal(1.5, restored.Points[0].recordedGroundClearance);
            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                restored.Points[0].flags);
        }
    }
}
