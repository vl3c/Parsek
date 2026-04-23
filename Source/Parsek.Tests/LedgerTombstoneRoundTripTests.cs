using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against LedgerTombstone save/load drift (design doc section 5.6).
    /// </summary>
    public class LedgerTombstoneRoundTripTests
    {
        [Fact]
        public void LedgerTombstone_AllFields_RoundTrips()
        {
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_bcd1",
                ActionId = "act_09876",
                RetiringRecordingId = "rec_03333",
                UT = 1742800.5,
                CreatedRealTime = "2026-04-18T12:00:00Z"
            };

            var parent = new ConfigNode("LEDGER_TOMBSTONES");
            tomb.SaveInto(parent);
            var entry = parent.GetNode("ENTRY");
            Assert.NotNull(entry);

            var restored = LedgerTombstone.LoadFrom(entry);
            Assert.Equal("tomb_bcd1", restored.TombstoneId);
            Assert.Equal("act_09876", restored.ActionId);
            Assert.Equal("rec_03333", restored.RetiringRecordingId);
            Assert.Equal(1742800.5, restored.UT);
            Assert.Equal("2026-04-18T12:00:00Z", restored.CreatedRealTime);
        }

        [Fact]
        public void LedgerTombstone_NoRetiringRecordingId_RoundTripsAsNull()
        {
            // A tombstone written without a retiring recording id (e.g. a partial
            // save) must round-trip — loader returns null for missing values.
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_1",
                ActionId = "act_1",
                UT = 0.0
            };

            var parent = new ConfigNode("LEDGER_TOMBSTONES");
            tomb.SaveInto(parent);
            var restored = LedgerTombstone.LoadFrom(parent.GetNode("ENTRY"));
            Assert.Null(restored.RetiringRecordingId);
        }

        [Fact]
        public void LedgerTombstone_RetiringRecordingId_RoundTripsExactly()
        {
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_2",
                ActionId = "act_2",
                RetiringRecordingId = "rec_new",
                UT = 0.0
            };

            var parent = new ConfigNode("LEDGER_TOMBSTONES");
            tomb.SaveInto(parent);
            var restored = LedgerTombstone.LoadFrom(parent.GetNode("ENTRY"));
            Assert.Equal("rec_new", restored.RetiringRecordingId);
        }
    }
}
