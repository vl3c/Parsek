using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The race mark's read codec (multiplayer R1-03, design 6.1): every required key, every
    /// refusal reason, the format classes and the invariant parse. The writer lands with R1-10,
    /// so the nodes here are built by hand in the shape it will write.
    /// </summary>
    public class RaceLinkCodecTests
    {
        internal static ConfigNode BuildLinkNode(string format = "1")
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", RaceFormat.ScenarioModuleName);
            node.AddValue("scene", "7, 8, 5, 6");
            if (format != null)
                node.AddValue(RaceLinkCodec.KeyRaceLinkFormat, format);
            node.AddValue(RaceLinkCodec.KeyRaceId, "race_0123456789abcdef0123456789abcdef");
            node.AddValue(RaceLinkCodec.KeyPlayerId, "p_fedcba9876543210fedcba9876543210");
            node.AddValue(RaceLinkCodec.KeySiteSlot, "2");
            node.AddValue(RaceLinkCodec.KeySiteTableId, RaceFormat.SiteTableKerbinEquator1);
            node.AddValue(RaceLinkCodec.KeyLadderId, RaceFormat.LadderStandard1);
            node.AddValue(RaceLinkCodec.KeyDateUnit, RaceFormat.DateUnitSecond);
            node.AddValue(RaceLinkCodec.KeyCalendar, "KERBIN");
            node.AddValue(RaceLinkCodec.KeyAlternateSites, "True");
            node.AddValue(RaceLinkCodec.KeyGameMode, "CAREER");
            node.AddValue(RaceLinkCodec.KeyDifficultyDigest, "d1g35t");
            return node;
        }

        internal static readonly string[] RequiredKeys =
        {
            RaceLinkCodec.KeyRaceLinkFormat, RaceLinkCodec.KeyRaceId, RaceLinkCodec.KeyPlayerId,
            RaceLinkCodec.KeySiteSlot, RaceLinkCodec.KeySiteTableId, RaceLinkCodec.KeyLadderId,
            RaceLinkCodec.KeyDateUnit, RaceLinkCodec.KeyCalendar, RaceLinkCodec.KeyAlternateSites,
            RaceLinkCodec.KeyGameMode, RaceLinkCodec.KeyDifficultyDigest,
        };

        [Fact]
        public void ValidNode_ReadsEveryField()
        {
            RaceLink link;
            string reason;
            Assert.True(RaceLinkCodec.TryRead(BuildLinkNode(), out link, out reason), reason);
            Assert.Null(reason);
            Assert.Equal(1, link.RaceLinkFormat);
            Assert.Equal("race_0123456789abcdef0123456789abcdef", link.RaceId);
            Assert.Equal("p_fedcba9876543210fedcba9876543210", link.PlayerId);
            Assert.Equal(2, link.SiteSlot);
            Assert.Equal("kerbin-equator-1", link.SiteTableId);
            Assert.Equal("standard-1", link.LadderId);
            Assert.Equal("SECOND", link.DateUnit);
            Assert.Equal("KERBIN", link.Calendar);
            Assert.True(link.AlternateSites);
            Assert.Equal("CAREER", link.GameMode);
            Assert.Equal("d1g35t", link.DifficultyDigest);
        }

        [Fact]
        public void NullNode_IsRefusedWithNoNode()
        {
            RaceLink link;
            string reason;
            Assert.False(RaceLinkCodec.TryRead(null, out link, out reason));
            Assert.Null(link);
            Assert.Equal("no-node", reason);
        }

        public static TheoryData<string> RequiredKeyData()
        {
            var data = new TheoryData<string>();
            foreach (var key in RequiredKeys)
                data.Add(key);
            return data;
        }

        [Theory]
        [MemberData(nameof(RequiredKeyData))]
        public void EachMissingRequiredKey_GivesItsReason(string key)
        {
            var node = BuildLinkNode();
            node.RemoveValue(key);
            RaceLink link;
            string reason;
            Assert.False(RaceLinkCodec.TryRead(node, out link, out reason));
            Assert.Null(link);
            Assert.Equal("missing:" + key, reason);
        }

        [Theory]
        [MemberData(nameof(RequiredKeyData))]
        public void EachEmptyRequiredKey_ReadsAsMissing(string key)
        {
            var node = BuildLinkNode();
            node.SetValue(key, "  ");
            RaceLink link;
            string reason;
            Assert.False(RaceLinkCodec.TryRead(node, out link, out reason));
            Assert.Equal("missing:" + key, reason);
        }

        [Theory]
        [InlineData("raceLinkFormat", "one")]
        [InlineData("siteSlot", "2.5")]
        [InlineData("siteSlot", "4")]
        [InlineData("siteSlot", "-1")]
        [InlineData("alternateSites", "yes")]
        public void BadValues_AreRefusedNamingTheKey(string key, string value)
        {
            var node = BuildLinkNode();
            node.SetValue(key, value);
            RaceLink link;
            string reason;
            Assert.False(RaceLinkCodec.TryRead(node, out link, out reason));
            Assert.Equal("bad-value:" + key, reason);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("1")]
        [InlineData("3")]
        public void EverySlotOfTheFourSeats_Reads(string slot)
        {
            var node = BuildLinkNode();
            node.SetValue(RaceLinkCodec.KeySiteSlot, slot);
            RaceLink link;
            string reason;
            Assert.True(RaceLinkCodec.TryRead(node, out link, out reason), reason);
            Assert.Equal(int.Parse(slot, CultureInfo.InvariantCulture), link.SiteSlot);
        }

        [Fact]
        public void NewerFormat_IsRefusedAsNewer_EvenWithNoOtherKey()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue(RaceLinkCodec.KeyRaceLinkFormat, "2");
            RaceLink link;
            string reason;
            Assert.False(RaceLinkCodec.TryRead(node, out link, out reason));
            Assert.Equal("format-newer", reason);
        }

        [Fact]
        public void OlderFormat_IsRefusedAsOlder()
        {
            RaceLink link;
            string reason;
            Assert.False(RaceLinkCodec.TryRead(BuildLinkNode("0"), out link, out reason));
            Assert.Equal("format-older", reason);
        }

        [Fact]
        public void MissingFormat_IsRefusedAsMissingBeforeAnyOtherKey()
        {
            var node = BuildLinkNode(format: null);
            node.RemoveValue(RaceLinkCodec.KeyRaceId);
            RaceLink link;
            string reason;
            Assert.False(RaceLinkCodec.TryRead(node, out link, out reason));
            Assert.Equal("missing:raceLinkFormat", reason);
        }

        [Fact]
        public void UnknownKeysAndChildren_AreIgnoredOnRead()
        {
            var node = BuildLinkNode();
            node.AddValue("someFutureKey", "x");
            node.AddNode("SOME_FUTURE_CHILD").AddValue("a", "1");
            RaceLink link;
            string reason;
            Assert.True(RaceLinkCodec.TryRead(node, out link, out reason), reason);
            Assert.Equal(2, link.SiteSlot);
        }

        [Fact]
        public void LowercaseBool_Reads()
        {
            var node = BuildLinkNode();
            node.SetValue(RaceLinkCodec.KeyAlternateSites, "false");
            RaceLink link;
            string reason;
            Assert.True(RaceLinkCodec.TryRead(node, out link, out reason), reason);
            Assert.False(link.AlternateSites);
        }

        [Fact]
        public void Read_IsCultureInvariant_UnderDeDe()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var node = BuildLinkNode("+1");
                node.SetValue(RaceLinkCodec.KeySiteSlot, "+3");
                RaceLink link;
                string reason;
                Assert.True(RaceLinkCodec.TryRead(node, out link, out reason), reason);
                Assert.Equal(1, link.RaceLinkFormat);
                Assert.Equal(3, link.SiteSlot);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
