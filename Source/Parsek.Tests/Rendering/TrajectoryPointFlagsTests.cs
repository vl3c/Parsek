using System;
using System.IO;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9) basic flag-bitset tests.
    /// Pins the bit assignments, and that the binary codec round-trips the raw
    /// byte so future bits stay additive (StructuralEventSnapshot at bit 0 must
    /// keep its meaning across phases).
    /// </summary>
    [Collection("Sequential")]
    public class TrajectoryPointFlagsTests
    {
        [Fact]
        public void None_IsZero()
        {
            Assert.Equal((byte)0, (byte)TrajectoryPointFlags.None);
        }

        [Fact]
        public void StructuralEventSnapshot_IsBitZero()
        {
            Assert.Equal((byte)1, (byte)TrajectoryPointFlags.StructuralEventSnapshot);
        }

        [Fact]
        public void StructuralEventSnapshot_WithReservedBit_RoundTripsThroughSidecarCodec()
        {
            // The byte rides at the end of every binary point record, so a reserved bit
            // set alongside bit 0 (0x81) must read back as the same raw byte, with bit 0
            // still detected; a reserved bit alone (0x80) must not read as bit 0.
            string dir = Path.Combine(Path.GetTempPath(), "parsek-point-flags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rec = new Recording
                {
                    RecordingId = "point-flags-reserved-bit",
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                };
                rec.Points.Add(MakePoint(100.0, (byte)((byte)TrajectoryPointFlags.StructuralEventSnapshot | 0x80)));
                rec.Points.Add(MakePoint(101.0, 0x80));

                string path = Path.Combine(dir, "flags.prec");
                TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);
                Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
                var restored = new Recording();
                TrajectorySidecarBinary.Read(path, restored, probe);

                Assert.Equal(2, restored.Points.Count);
                Assert.Equal((byte)0x81, restored.Points[0].flags);
                Assert.Equal(TrajectoryPointFlags.StructuralEventSnapshot,
                    (TrajectoryPointFlags)restored.Points[0].flags & TrajectoryPointFlags.StructuralEventSnapshot);
                Assert.Equal((byte)0x80, restored.Points[1].flags);
                Assert.Equal(TrajectoryPointFlags.None,
                    (TrajectoryPointFlags)restored.Points[1].flags & TrajectoryPointFlags.StructuralEventSnapshot);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static TrajectoryPoint MakePoint(double ut, byte flags)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.1,
                longitude = -74.5,
                altitude = 80.0,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(2f, 0f, 0f),
                bodyName = "Kerbin",
                recordedGroundClearance = double.NaN,
                flags = flags,
            };
        }

        [Fact]
        public void DefaultTrajectoryPoint_FlagsIsZero()
        {
            // The Phase 9 binary codec's "default flags=0 on legacy reads"
            // contract relies on TrajectoryPoint's value-typed initialization.
            var pt = new TrajectoryPoint();
            Assert.Equal((byte)0, pt.flags);
            Assert.True(((TrajectoryPointFlags)pt.flags & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.None);
        }
    }
}
