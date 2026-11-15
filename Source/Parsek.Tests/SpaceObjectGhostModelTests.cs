using System.Globalization;
using System.Threading;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    // The pure half of the asteroid / comet ghost geometry (ROCK-GHOST-PREFAB-MESH): the
    // snapshot MODULE parse that feeds stock's generator, the path choice, the radius-multiplier
    // source and the placeholder scale. The live half (stock's ProceduralAsteroid / ProceduralComet
    // Generate on a detached object) is covered in-game by ClawCoupleInGameTest.
    public class SpaceObjectGhostModelTests
    {
        private static ConfigNode AsteroidPart(int seed = 27182818, string sizeClass = "E", int state = 1)
        {
            ConfigNode snapshot = VesselSnapshotBuilder
                .ClawedAsteroidShip("Grabber", "Jebediah Kerman", 987654321u,
                    asteroidSeed: seed, sizeClass: sizeClass, currentState: state)
                .Build();
            return snapshot.GetNodes("PART")[2];
        }

        private static ConfigNode CometPart(int seed = 16180339, string sizeClass = "C", int state = 1)
        {
            ConfigNode snapshot = VesselSnapshotBuilder
                .LoneComet("Comet Smith", 4242u, cometSeed: seed, sizeClass: sizeClass, currentState: state)
                .Build();
            return snapshot.GetNodes("PART")[0];
        }

        [Fact]
        public void Generator_WritesStockModuleAsteroidFields()
        {
            ConfigNode part = AsteroidPart();
            Assert.Equal("PotatoRoid", part.GetValue("name"));
            ConfigNode module = SpaceObjectGhostModel.FindModuleNode(part, "ModuleAsteroid");
            Assert.NotNull(module);
            Assert.Equal("27182818", module.GetValue("seed"));
            Assert.Equal("Procedural/PA_E", module.GetValue("prefabBaseURL"));
            Assert.Equal("1", module.GetValue("currentState"));
            Assert.Equal("True", module.GetValue("stagingEnabled"));
            Assert.NotNull(module.GetNode("EVENTS"));
        }

        [Fact]
        public void TryParseRockSpec_Asteroid_ReadsSeedUrlAndState()
        {
            Assert.True(SpaceObjectGhostModel.TryParseRockSpec(
                AsteroidPart(), SpaceObjectKind.Asteroid, out SpaceObjectRockSpec spec, out string reason));
            Assert.Null(reason);
            Assert.Equal(SpaceObjectKind.Asteroid, spec.Kind);
            Assert.Equal(27182818, spec.Seed);
            Assert.Equal("Procedural/PA_E", spec.PrefabUrl);
            Assert.Equal("snapshot", spec.PrefabUrlSource);
            Assert.False(spec.IsSecondary);
        }

        [Fact]
        public void TryParseRockSpec_SecondaryState_IsSecondary()
        {
            Assert.True(SpaceObjectGhostModel.TryParseRockSpec(
                AsteroidPart(state: 2), SpaceObjectKind.Asteroid, out SpaceObjectRockSpec spec, out _));
            Assert.True(spec.IsSecondary);
        }

        [Fact]
        public void TryParseRockSpec_NegativeSeed_IsKept()
        {
            // Stock draws seeds from [-1e8, 1e8); only -1 means "never generated".
            Assert.True(SpaceObjectGhostModel.TryParseRockSpec(
                AsteroidPart(seed: -55443322), SpaceObjectKind.Asteroid, out SpaceObjectRockSpec spec, out _));
            Assert.Equal(-55443322, spec.Seed);
        }

        [Fact]
        public void TryParseRockSpec_Comet_ReadsCometModule()
        {
            Assert.True(SpaceObjectGhostModel.TryParseRockSpec(
                CometPart(), SpaceObjectKind.Comet, out SpaceObjectRockSpec spec, out _));
            Assert.Equal(SpaceObjectKind.Comet, spec.Kind);
            Assert.Equal(16180339, spec.Seed);
            Assert.Equal("Procedural/PC_C", spec.PrefabUrl);
        }

        [Fact]
        public void TryParseRockSpec_WrongModuleForKind_Fails()
        {
            Assert.False(SpaceObjectGhostModel.TryParseRockSpec(
                CometPart(), SpaceObjectKind.Asteroid, out SpaceObjectRockSpec spec, out string reason));
            Assert.Null(spec);
            Assert.Equal("no-module-node", reason);
        }

        [Fact]
        public void TryParseRockSpec_KindNone_Fails()
        {
            Assert.False(SpaceObjectGhostModel.TryParseRockSpec(
                AsteroidPart(), SpaceObjectKind.None, out _, out string reason));
            Assert.Equal("not-a-space-object", reason);
        }

        [Fact]
        public void TryParseRockSpec_SeedMinusOne_IsUnset()
        {
            Assert.False(SpaceObjectGhostModel.TryParseRockSpec(
                AsteroidPart(seed: -1), SpaceObjectKind.Asteroid, out _, out string reason));
            Assert.Equal("seed-unset", reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("abc")]
        [InlineData("")]
        public void TryParseRockSpec_MissingOrBadSeed_Fails(string rawSeed)
        {
            ConfigNode part = AsteroidPart();
            ConfigNode module = SpaceObjectGhostModel.FindModuleNode(part, "ModuleAsteroid");
            module.RemoveValue("seed");
            if (rawSeed != null)
                module.AddValue("seed", rawSeed);
            Assert.False(SpaceObjectGhostModel.TryParseRockSpec(
                part, SpaceObjectKind.Asteroid, out _, out string reason));
            Assert.Equal("no-seed", reason);
        }

        [Fact]
        public void TryParseRockSpec_EmptyUrl_FallsBackToStockDefaultClass()
        {
            ConfigNode part = AsteroidPart();
            ConfigNode module = SpaceObjectGhostModel.FindModuleNode(part, "ModuleAsteroid");
            module.SetValue("prefabBaseURL", "");
            Assert.True(SpaceObjectGhostModel.TryParseRockSpec(
                part, SpaceObjectKind.Asteroid, out SpaceObjectRockSpec spec, out _));
            Assert.Equal("Procedural/PA_C", spec.PrefabUrl);
            Assert.Equal("default-class-C", spec.PrefabUrlSource);

            ConfigNode comet = CometPart();
            SpaceObjectGhostModel.FindModuleNode(comet, "ModuleComet").RemoveValue("prefabBaseURL");
            Assert.True(SpaceObjectGhostModel.TryParseRockSpec(
                comet, SpaceObjectKind.Comet, out SpaceObjectRockSpec cometSpec, out _));
            Assert.Equal("Procedural/PC_C", cometSpec.PrefabUrl);
        }

        [Fact]
        public void TryParseRockSpec_MissingStateKey_IsPrimary()
        {
            ConfigNode part = AsteroidPart(state: 2);
            SpaceObjectGhostModel.FindModuleNode(part, "ModuleAsteroid").RemoveValue("currentState");
            Assert.True(SpaceObjectGhostModel.TryParseRockSpec(
                part, SpaceObjectKind.Asteroid, out SpaceObjectRockSpec spec, out _));
            Assert.False(spec.IsSecondary);
        }

        [Theory]
        [InlineData(1, "Procedural/PA_A", true)]
        [InlineData(1, "Custom/MyRock", true)]
        [InlineData(2, "Procedural/PC_I", true)]
        [InlineData(2, "Missions/HandMadeComet", false)]
        [InlineData(2, "", false)]
        [InlineData(0, "Procedural/PA_A", false)]
        public void UsesStockGenerator_MirrorsStockOnStartBranch(int kind, string url, bool expected)
        {
            // kind: 0 None, 1 Asteroid, 2 Comet (the enum is internal to Parsek).
            Assert.Equal(expected, SpaceObjectGhostModel.UsesStockGenerator((SpaceObjectKind)kind, url));
        }

        [Theory]
        [InlineData(true, true, 0)]
        [InlineData(true, false, 0)]
        [InlineData(false, true, 1)]
        [InlineData(false, false, 2)]
        public void ChoosePath_TruthTable(bool produced, bool radiusKnown, int expected)
        {
            // expected: 0 StockGenerator, 1 ScaledPrefab, 2 PrefabLump.
            Assert.Equal((RockGhostPath)expected, SpaceObjectGhostModel.ChoosePath(produced, radiusKnown));
        }

        [Fact]
        public void ResolveRadiusMultipliers_UsesModuleConfig()
        {
            SpaceObjectGhostModel.ResolveRadiusMultipliers(true, 0.5f, 2f, out float min, out float max);
            Assert.Equal(0.5f, min);
            Assert.Equal(2f, max);
        }

        [Theory]
        [InlineData(false, 0.5f, 2f)]
        [InlineData(true, float.NaN, 2f)]
        [InlineData(true, 0.5f, float.PositiveInfinity)]
        [InlineData(true, 0f, 2f)]
        [InlineData(true, 2f, 0.5f)]
        public void ResolveRadiusMultipliers_FallsBackToStockDefaults(bool haveModule, float cfgMin, float cfgMax)
        {
            SpaceObjectGhostModel.ResolveRadiusMultipliers(haveModule, cfgMin, cfgMax, out float min, out float max);
            Assert.Equal(0.75f, min);
            Assert.Equal(1.25f, max);
        }

        [Fact]
        public void ComputePlaceholderScale_MapsMeanExtentToRadius()
        {
            Assert.Equal(10f, SpaceObjectGhostModel.ComputePlaceholderScale(20f, 2f, 2f, 2f), 1e-4f);
            Assert.Equal(5f, SpaceObjectGhostModel.ComputePlaceholderScale(15f, 2f, 3f, 4f), 1e-4f);
        }

        [Theory]
        [InlineData(0f, 1f, 1f, 1f)]
        [InlineData(-3f, 1f, 1f, 1f)]
        [InlineData(float.NaN, 1f, 1f, 1f)]
        [InlineData(10f, 0f, 0f, 0f)]
        [InlineData(10f, float.NaN, 1f, 1f)]
        public void ComputePlaceholderScale_UnusableInput_KeepsScaleOne(float target, float ex, float ey, float ez)
        {
            Assert.Equal(1f, SpaceObjectGhostModel.ComputePlaceholderScale(target, ex, ey, ez));
        }

        [Fact]
        public void BuildCacheKey_SeparatesSeedStateUrlAndPart()
        {
            SpaceObjectGhostModel.TryParseRockSpec(AsteroidPart(), SpaceObjectKind.Asteroid, out var a, out _);
            SpaceObjectGhostModel.TryParseRockSpec(AsteroidPart(), SpaceObjectKind.Asteroid, out var same, out _);
            SpaceObjectGhostModel.TryParseRockSpec(AsteroidPart(seed: 5), SpaceObjectKind.Asteroid, out var seed, out _);
            SpaceObjectGhostModel.TryParseRockSpec(AsteroidPart(state: 2), SpaceObjectKind.Asteroid, out var sec, out _);
            SpaceObjectGhostModel.TryParseRockSpec(AsteroidPart(sizeClass: "I"), SpaceObjectKind.Asteroid, out var cls, out _);

            string key = SpaceObjectGhostModel.BuildCacheKey("PotatoRoid", a);
            Assert.Equal(key, SpaceObjectGhostModel.BuildCacheKey("PotatoRoid", same));
            Assert.NotEqual(key, SpaceObjectGhostModel.BuildCacheKey("PotatoRoid", seed));
            Assert.NotEqual(key, SpaceObjectGhostModel.BuildCacheKey("PotatoRoid", sec));
            Assert.NotEqual(key, SpaceObjectGhostModel.BuildCacheKey("PotatoRoid", cls));
            Assert.NotEqual(key, SpaceObjectGhostModel.BuildCacheKey("PotatoComet", a));
            Assert.Null(SpaceObjectGhostModel.BuildCacheKey("PotatoRoid", null));
        }

        [Theory]
        [InlineData("potatoroid", true)]
        [InlineData("PotatoRoid", false)]
        [InlineData("model", false)]
        [InlineData(null, false)]
        public void IsPlaceholderName_MatchesStockFindModelTransformName(string name, bool expected)
        {
            Assert.Equal(expected, SpaceObjectGhostModel.IsPlaceholderName(name));
        }

        [Fact]
        public void FormatBuiltLine_IsCultureInvariantAndNamesThePath()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                SpaceObjectGhostModel.TryParseRockSpec(CometPart(), SpaceObjectKind.Comet, out var spec, out _);
                string line = SpaceObjectGhostModel.FormatBuiltLine(RockGhostPath.StockGenerator,
                    SpaceObjectKind.Comet, "PotatoComet", 100000u, spec, 123.456f, true, "miss", null,
                    "skipped-needs-live-comet-vessel");
                Assert.Contains("RockGhost built: path=stock-generator kind=comet", line);
                Assert.Contains("seed=16180339", line);
                Assert.Contains("url=Procedural/PC_C", line);
                Assert.Contains("radius=123.46", line);
                Assert.Contains("cache=miss", line);
                Assert.Contains("cometVfx=skipped-needs-live-comet-vessel", line);
                Assert.DoesNotContain("reason=", line);

                string fallback = SpaceObjectGhostModel.FormatBuiltLine(RockGhostPath.PrefabLump,
                    SpaceObjectKind.Asteroid, "PotatoRoid", 7u, null, 0f, false, "none", "seed-unset", null);
                Assert.Contains("path=prefab-lump kind=asteroid", fallback);
                Assert.Contains("seed=? url=?", fallback);
                Assert.Contains("radius=?", fallback);
                Assert.Contains("reason=seed-unset", fallback);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }
    }
}
