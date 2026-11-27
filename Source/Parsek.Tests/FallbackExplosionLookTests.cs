using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The hand-built ghost explosion's look (<see cref="FallbackExplosionLook"/>): the tier is
    /// stock FXMonger's own prefab pick, fire and a flash only for the bigger blasts, and the
    /// smoke is grey.
    /// </summary>
    public class FallbackExplosionLookTests
    {
        [Theory]
        [InlineData(0.0, 0)]
        [InlineData(0.10, 0)]      // a 2 m craft: stock's Thud
        [InlineData(0.17, 1)]
        [InlineData(0.34, 2)]
        [InlineData(0.5, 3)]
        [InlineData(0.67, 4)]
        [InlineData(0.84, 5)]
        [InlineData(1.0, 6)]
        [InlineData(7.0, 6)]
        [InlineData(-3.0, 0)]
        [InlineData(double.NaN, 0)]
        public void Tier_IsTheStockPrefabIndex(double power, int tier)
        {
            Assert.Equal(tier, FallbackExplosionLook.ResolveTier(power));
            Assert.Equal(tier, FallbackExplosionLook.Resolve(power).Tier);
        }

        [Fact]
        public void Tier_FollowsFxMongersRuleAtEveryPower()
        {
            for (double p = 0.0; p <= 1.0001; p += 0.01)
            {
                int stock = StockExplosionPrefabCache.ResolvePrefabIndex(p, FallbackExplosionLook.StockPrefabCount);
                Assert.Equal(stock, FallbackExplosionLook.ResolveTier(p));
            }
        }

        [Fact]
        public void FireAndFlash_OnlyForTheBlastsStockDrawsFireFor()
        {
            for (int tier = 0; tier < FallbackExplosionLook.StockPrefabCount; tier++)
            {
                FallbackExplosionLook look = FallbackExplosionLook.Resolve(PowerForTier(tier));
                Assert.Equal(tier, look.Tier);
                bool fiery = tier >= 2;
                Assert.Equal(fiery, look.HasFlash);
                Assert.Equal(fiery, look.HasFire);
                Assert.True(look.SmokeCount > 0, $"tier {tier} has smoke");
                Assert.True(look.SmokeSizeMin > 0f && look.SmokeSizeMax >= look.SmokeSizeMin);
                Assert.True(look.SmokeLifetimeMax >= look.SmokeLifetimeMin && look.SmokeLifetimeMin > 0f);
                if (look.HasSparks)
                    Assert.True(look.SparkSpeedMax >= look.SparkSpeedMin && look.SparkLifetime > 0f);
                Assert.True(look.DestroyAfterSeconds > look.SmokeLifetimeMax);
                Assert.True(look.DestroyAfterSeconds <= 5f, "gone within stock's few seconds");
            }
        }

        [Fact]
        public void SparksAndCloud_MatchStocksPerPrefabShapes()
        {
            // Stock's Explosion 4 is a flash turning into a cloud with no streaks; the rest throw sparks.
            Assert.False(FallbackExplosionLook.Resolve(PowerForTier(4)).HasSparks);
            foreach (int tier in new[] { 0, 1, 2, 3, 5, 6 })
                Assert.True(FallbackExplosionLook.Resolve(PowerForTier(tier)).HasSparks, $"tier {tier}");
            // The biggest blasts make the biggest cloud and flash.
            Assert.True(FallbackExplosionLook.Resolve(1.0).SmokeSizeMax
                > FallbackExplosionLook.Resolve(0.0).SmokeSizeMax * 3f);
            Assert.True(FallbackExplosionLook.Resolve(PowerForTier(5)).FlashSize
                > FallbackExplosionLook.Resolve(PowerForTier(2)).FlashSize);
        }

        [Theory]
        [InlineData(0.3, 12f, 0.3)]          // a real power wins over the length
        [InlineData(double.NaN, 10f, 0.5)]   // NaN: the length over the 20 m divisor
        [InlineData(double.NaN, 40f, 1.0)]   // clamped
        [InlineData(double.NaN, 0f, 0.0)]
        [InlineData(double.NaN, float.NaN, 0.0)]
        public void Power_FallsBackToTheCraftLength(double power, float length, double expected)
        {
            Assert.Equal(expected, FallbackExplosionLook.ResolvePower(power, length), 6);
        }

        [Fact]
        public void Describe_IsInvariant()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("tier=0 flash=none fire=0 sparks=12 smoke=14 size=0.8-1.6m",
                    FallbackExplosionLook.Resolve(0.0).Describe());
                Assert.Equal("tier=6 flash=9.0m fire=18 sparks=90 smoke=40 size=4.0-7.0m",
                    FallbackExplosionLook.Resolve(1.0).Describe());
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void HandBuiltExplosion_BuildsEachPartOnlyWhenTheLookHasIt()
        {
            string fx = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                Root(), "Source", "Parsek", "Ghost", "GhostVisualBuilder.TransientFx.cs")));
            int start = fx.IndexOf("internal static GameObject SpawnExplosionFx(", StringComparison.Ordinal);
            Assert.True(start >= 0);
            string body = fx.Substring(start, fx.IndexOf("private static int AddIfBuilt(", start, StringComparison.Ordinal) - start);
            Assert.Contains("FallbackExplosionLook.Resolve(usedPower)", body);
            Assert.Matches(@"if \(look\.HasFlash\)[^;]*BuildFallbackExplosionFlash\(", body);
            Assert.Matches(@"if \(look\.HasFire\)[^;]*BuildFallbackExplosionFlames\(", body);
            Assert.Matches(@"if \(look\.HasSparks\)[^;]*BuildFallbackExplosionSparks\(", body);
            Assert.Contains("Object.Destroy(obj, look.DestroyAfterSeconds);", body);
            // The cloud is grey: a neutral tint, a dark and a light grey start colour, no brown.
            int smoke = fx.IndexOf("private static ParticleSystem BuildFallbackExplosionSmoke(", StringComparison.Ordinal);
            string smokeBody = fx.Substring(smoke, fx.IndexOf("private static ParticleSystem BuildFallbackExplosionFlash(", smoke, StringComparison.Ordinal) - smoke);
            Assert.Contains("new Color(0.5f, 0.5f, 0.5f, 0.5f)", smokeBody);
            foreach (Match m in Regex.Matches(smokeBody, @"new Color\(([0-9.]+)f, ([0-9.]+)f, ([0-9.]+)f"))
            {
                double r = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                double g = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                double b = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(Math.Abs(r - g) < 0.02 && Math.Abs(g - b) < 0.02, $"grey, not tinted: {m.Value}");
                Assert.True(r >= 0.3, $"not near black: {m.Value}");
            }
        }

        private static double PowerForTier(int tier) => (tier + 0.5) / (FallbackExplosionLook.StockPrefabCount - 1);

        private static string Root()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }
    }
}
