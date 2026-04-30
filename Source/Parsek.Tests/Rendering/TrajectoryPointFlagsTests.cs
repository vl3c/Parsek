using Parsek;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9) basic flag-bitset tests.
    /// Pins the bit assignments and the [Flags] arithmetic semantics so future
    /// bits stay additive (StructuralEventSnapshot at bit 0 must keep its
    /// meaning across phases — the binary codec round-trips the raw byte).
    /// </summary>
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
        public void StructuralEventSnapshot_HasFlagDetectsSetBit()
        {
            byte raw = (byte)TrajectoryPointFlags.StructuralEventSnapshot;
            Assert.True(((TrajectoryPointFlags)raw & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.StructuralEventSnapshot);
        }

        [Fact]
        public void None_HasFlagDetectsNoSetBit()
        {
            byte raw = (byte)TrajectoryPointFlags.None;
            Assert.True(((TrajectoryPointFlags)raw & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.None);
        }

        [Fact]
        public void OrCombines_PreservesBitZero()
        {
            // Future-bit composition: setting bit 0 alongside any other bit
            // must keep bit 0 readable in isolation. Pin the round-trip at
            // the byte level so a future bit assignment cannot accidentally
            // collide with bit 0.
            byte raw = (byte)((byte)TrajectoryPointFlags.StructuralEventSnapshot | 0x80);
            Assert.True(((TrajectoryPointFlags)raw & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.StructuralEventSnapshot);
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
