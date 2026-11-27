using System;
using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of the throwaway far-watch proof verb: arg parsing, the 6.3 terrain floor,
    /// the warp ladder pick, the op time box and the observation line format.
    /// </summary>
    public class TestCommandFarWatchProofTests
    {
        private static Dictionary<string, string> A(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        [Fact]
        public void OpTokens_MatchTheEnumOrder()
        {
            Assert.Equal(Enum.GetValues(typeof(FarWatchProofOp)).Length, TestCommandFarWatchProof.OpTokens.Length);
            for (int i = 0; i < TestCommandFarWatchProof.OpTokens.Length; i++)
            {
                Assert.True(TestCommandFarWatchProof.TryParseOp(TestCommandFarWatchProof.OpTokens[i], out FarWatchProofOp op));
                Assert.Equal(i, (int)op);
                Assert.Equal(TestCommandFarWatchProof.OpTokens[i], TestCommandFarWatchProof.OpToken(op));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Arm")]
        [InlineData("teleport")]
        public void TryParseArgs_RefusesAnUnknownOp(string op)
        {
            var raw = op == null ? A() : A("op", op);
            Assert.Equal(TestCommandFarWatchProof.ArgReasonUnknownOp, TestCommandFarWatchProof.TryParseArgs(raw, out _));
        }

        [Fact]
        public void TryParseArgs_RefusesAnUnknownKey()
        {
            Assert.Equal(TestCommandFarWatchProof.ArgReasonUnknownKey + ":speed",
                TestCommandFarWatchProof.TryParseArgs(A("op", "build", "speed", "1"), out _));
        }

        [Theory]
        [InlineData("index", "-1")]
        [InlineData("index", "x")]
        [InlineData("untilUT", "nan")]
        [InlineData("seconds", "0")]
        [InlineData("seconds", "481")]
        [InlineData("watch", "yes")]
        [InlineData("strip", "True")]
        public void TryParseArgs_RefusesABadValue(string key, string value)
        {
            Assert.Equal(TestCommandFarWatchProof.ArgReasonBadValue + ":" + key,
                TestCommandFarWatchProof.TryParseArgs(A("op", "report", key, value), out _));
        }

        [Fact]
        public void TryParseArgs_SwitchNeedsATargetAndAdvanceAnEndUT()
        {
            Assert.Equal(TestCommandFarWatchProof.ArgReasonBadValue + ":target",
                TestCommandFarWatchProof.TryParseArgs(A("op", "switch"), out _));
            Assert.Null(TestCommandFarWatchProof.TryParseArgs(A("op", "switch", "target", "drone"), out _));
            Assert.Equal(TestCommandFarWatchProof.ArgReasonBadValue + ":untilUT",
                TestCommandFarWatchProof.TryParseArgs(A("op", "advance"), out _));
        }

        [Fact]
        public void TryParseArgs_DefaultsAndValues()
        {
            Assert.Null(TestCommandFarWatchProof.TryParseArgs(A("op", "follow", "tree", "abc", "watch", "true",
                "seconds", "12.5"), out TestCommandFarWatchProof.Args a));
            Assert.Equal(FarWatchProofOp.Follow, a.Op);
            Assert.Equal("abc", a.Tree);
            Assert.True(a.Watch);
            Assert.Equal(12.5, a.Seconds);
            Assert.Equal(-1, a.Index);
            Assert.True(a.Strip);
            Assert.True(double.IsNaN(a.UntilUT));
            Assert.True(double.IsNaN(a.Alt));

            Assert.Null(TestCommandFarWatchProof.TryParseArgs(A("op", "advance", "untilUT", "1266400",
                "maxRate", "1000", "pauses", "2"), out a));
            Assert.Equal(1266400.0, a.UntilUT);
            Assert.Equal(1000.0, a.MaxRate);
            Assert.Equal(2, a.Pauses);
        }

        [Fact]
        public void TerrainFloor_GrowsWithDescentAndGameStep()
        {
            // At rest and 1x (0.02 s step) the floor is the base plus a hair of gravity.
            Assert.Equal(20.0 + 0.5 * 9.81 * 0.02 * 0.02, TestCommandFarWatchProof.TerrainFloorMeters(0, 0.02, 9.81, 20), 9);
            // Falling at 100 m/s with a 1 s game step: 100 m of coast plus half g.
            Assert.Equal(20.0 + 100.0 + 0.5 * 1.63, TestCommandFarWatchProof.TerrainFloorMeters(-100, 1.0, 1.63, 20), 9);
            // Climbing adds no coast term.
            Assert.Equal(20.0, TestCommandFarWatchProof.TerrainFloorMeters(50, 0.0, 9.81, 20), 9);
            // Non-finite inputs fall back to the base.
            Assert.Equal(20.0, TestCommandFarWatchProof.TerrainFloorMeters(double.NaN, double.NaN, double.NaN, 20), 9);
        }

        [Fact]
        public void ClampAltitude_RaisesToTheFloorAboveGroundAndSeaLevel()
        {
            double alt = TestCommandFarWatchProof.ClampAltitudeToFloor(105, 100, false, 20, out bool clamped);
            Assert.True(clamped);
            Assert.Equal(120, alt);
            alt = TestCommandFarWatchProof.ClampAltitudeToFloor(500, 100, false, 20, out clamped);
            Assert.False(clamped);
            Assert.Equal(500, alt);
            // Over an ocean the ground is sea level, not the sea floor.
            alt = TestCommandFarWatchProof.ClampAltitudeToFloor(5, -300, true, 20, out clamped);
            Assert.True(clamped);
            Assert.Equal(20, alt);
        }

        [Fact]
        public void FloatGridStep_IsMagnitudeOverTwoToTheTwentyThree()
        {
            Assert.Equal(1.0, TestCommandFarWatchProof.FloatGridStepMeters(8388608.0), 12);
            Assert.Equal(0.125, TestCommandFarWatchProof.FloatGridStepMeters(-1048576.0), 12);
        }

        [Fact]
        public void HighestWarpIndex_KeepsTheLeadAndTheCeiling()
        {
            var rates = new List<float> { 1, 5, 10, 50, 100, 1000, 10000, 100000 };
            // 10000 s left with a 3 s real lead: 1000x needs 3000 s, 10000x needs 30000 s.
            Assert.Equal(5, TestCommandFarWatchProof.HighestWarpIndexBefore(rates, 0, 10000, double.NaN, 3.0));
            // A ceiling of 100x stops the ladder there.
            Assert.Equal(4, TestCommandFarWatchProof.HighestWarpIndexBefore(rates, 0, 10000, 100, 3.0));
            // Almost there: 1x.
            Assert.Equal(0, TestCommandFarWatchProof.HighestWarpIndexBefore(rates, 0, 2, double.NaN, 3.0));
            Assert.Equal(0, TestCommandFarWatchProof.HighestWarpIndexBefore(null, 0, 1e9, double.NaN, 3.0));
        }

        [Fact]
        public void OpTimedOut_UsesTheArgCappedByTheMaximum()
        {
            Assert.False(TestCommandFarWatchProof.OpTimedOut(0, 10, 20));
            Assert.True(TestCommandFarWatchProof.OpTimedOut(0, 20, 20));
            Assert.False(TestCommandFarWatchProof.OpTimedOut(0, 479, double.NaN));
            Assert.True(TestCommandFarWatchProof.OpTimedOut(0, 480, 1000));
        }

        [Fact]
        public void FormatObs_IsOneTokenPerPairAndInvariant()
        {
            var pairs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("vessel", "Kerbal X Probe"),
                new KeyValuePair<string, string>("empty", ""),
                new KeyValuePair<string, string>("ut", TestCommandFarWatchProof.F(1234.5)),
            };
            Assert.Equal("obs=switch vessel=Kerbal_X_Probe empty=- ut=1234.50",
                TestCommandFarWatchProof.FormatObs("switch", pairs));
            Assert.Equal("nan", TestCommandFarWatchProof.F(double.NaN));
            Assert.Equal("inf", TestCommandFarWatchProof.F(double.PositiveInfinity));
            Assert.Equal("true", TestCommandFarWatchProof.B(true));
        }
    }
}
