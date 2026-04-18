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
                UT = 1742800.5,
                Reason = "supersede",
                CreatedRealTime = "2026-04-18T12:00:00Z"
            };

            var parent = new ConfigNode("LEDGER_TOMBSTONES");
            tomb.SaveInto(parent);
            var entry = parent.GetNode("ENTRY");
            Assert.NotNull(entry);

            var restored = LedgerTombstone.LoadFrom(entry);
            Assert.Equal("tomb_bcd1", restored.TombstoneId);
            Assert.Equal("act_09876", restored.ActionId);
            Assert.Equal(1742800.5, restored.UT);
            Assert.Equal("supersede", restored.Reason);
            Assert.Equal("2026-04-18T12:00:00Z", restored.CreatedRealTime);
        }

        [Fact]
        public void LedgerTombstone_DefaultReason_PersistsAsSupersede()
        {
            // Guards against silent Reason regressions: the default value must survive
            // save/load even when set via the field initializer.
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_1",
                ActionId = "act_1",
                UT = 0.0
            };

            var parent = new ConfigNode("LEDGER_TOMBSTONES");
            tomb.SaveInto(parent);
            var restored = LedgerTombstone.LoadFrom(parent.GetNode("ENTRY"));
            Assert.Equal("supersede", restored.Reason);
        }

        [Fact]
        public void LedgerTombstone_ExplicitReason_OverridesDefault()
        {
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_2",
                ActionId = "act_2",
                UT = 0.0,
                Reason = "rebundle"
            };

            var parent = new ConfigNode("LEDGER_TOMBSTONES");
            tomb.SaveInto(parent);
            var restored = LedgerTombstone.LoadFrom(parent.GetNode("ENTRY"));
            Assert.Equal("rebundle", restored.Reason);
        }
    }
}
