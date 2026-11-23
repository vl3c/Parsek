using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>Pure coverage for the SeatGhostKerbal and LiveGhostReentryFx in-game cores.</summary>
    public class GhostKerbalVisibilityTests
    {
        private static GhostKerbalVisibility.Occurrence O(string crew, int idx, bool drawn)
            => new GhostKerbalVisibility.Occurrence { Crew = crew, GhostIndex = idx, Drawn = drawn };

        [Theory]
        [InlineData("kerbalEVA", true)]
        [InlineData("kerbalEVAfemale", true)]
        [InlineData("kerbalEVAVintage", true)]
        [InlineData("seatExternalCmd", false)]
        [InlineData("mk1pod.v2", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsKerbalPartName(string name, bool expected)
        {
            Assert.Equal(expected, GhostKerbalVisibility.IsKerbalPartName(name));
        }

        [Fact]
        public void ClassifyFrame_OnceTwiceMissing()
        {
            var frame = GhostKerbalVisibility.ClassifyFrame(new List<GhostKerbalVisibility.Occurrence>
            {
                O("Jeb", 3, true),
                O("Val", 3, true), O("Val", 5, true),
                O("Bob", 4, false),
                O(null, 6, true),
            }, out var drawn);
            Assert.Equal(GhostKerbalVisibility.FrameVerdict.Once, frame["Jeb"]);
            Assert.Equal(GhostKerbalVisibility.FrameVerdict.Twice, frame["Val"]);
            Assert.Equal(GhostKerbalVisibility.FrameVerdict.Missing, frame["Bob"]);
            Assert.Equal(3, frame.Count);
            Assert.Equal(2, drawn["Val"]);
            Assert.Equal(0, drawn["Bob"]);
        }

        [Fact]
        public void ClassifyFrame_SeatedAndHiddenEvaCopyIsOnce()
        {
            // The seated part drawn on the vehicle ghost, an EVA ghost of the same kerbal hidden:
            // one drawn copy is the correct picture.
            var frame = GhostKerbalVisibility.ClassifyFrame(new List<GhostKerbalVisibility.Occurrence>
            {
                O("Jeb", 1, true), O("Jeb", 2, false),
            }, out _);
            Assert.Equal(GhostKerbalVisibility.FrameVerdict.Once, frame["Jeb"]);
        }

        private static SortedDictionary<string, GhostKerbalVisibility.FrameVerdict> F(
            string crew, GhostKerbalVisibility.FrameVerdict v)
            => new SortedDictionary<string, GhostKerbalVisibility.FrameVerdict> { [crew] = v };

        [Fact]
        public void Accumulate_RunsResetOnGoodFramesAndOnAVerdictChange()
        {
            var runs = new Dictionary<string, KeyValuePair<GhostKerbalVisibility.FrameVerdict, int>>();
            int twice = 0, missing = 0;
            for (int i = 0; i < 4; i++)
                GhostKerbalVisibility.Accumulate(F("Jeb", GhostKerbalVisibility.FrameVerdict.Twice), runs,
                    ref twice, ref missing);
            Assert.Equal(4, twice);
            GhostKerbalVisibility.Accumulate(F("Jeb", GhostKerbalVisibility.FrameVerdict.Once), runs,
                ref twice, ref missing);
            Assert.Empty(runs);
            GhostKerbalVisibility.Accumulate(F("Jeb", GhostKerbalVisibility.FrameVerdict.Twice), runs,
                ref twice, ref missing);
            GhostKerbalVisibility.Accumulate(F("Jeb", GhostKerbalVisibility.FrameVerdict.Missing), runs,
                ref twice, ref missing);
            Assert.Equal(1, runs["Jeb"].Value);
            Assert.Equal(1, missing);
            Assert.Equal(4, twice);
            // A frame with no carrier of the kerbal ends the run.
            GhostKerbalVisibility.Accumulate(new SortedDictionary<string, GhostKerbalVisibility.FrameVerdict>(),
                runs, ref twice, ref missing);
            Assert.Empty(runs);
        }

        [Fact]
        public void DecideOutcome()
        {
            int min = GhostKerbalVisibility.MinBadRunFrames;
            Assert.Equal(GhostKerbalVisibility.CellOutcome.Skip, GhostKerbalVisibility.DecideOutcome(0, 0, 0));
            Assert.Equal(GhostKerbalVisibility.CellOutcome.Pass, GhostKerbalVisibility.DecideOutcome(5, min - 1, min - 1));
            Assert.Equal(GhostKerbalVisibility.CellOutcome.Fail, GhostKerbalVisibility.DecideOutcome(5, min, 0));
            Assert.Equal(GhostKerbalVisibility.CellOutcome.Fail, GhostKerbalVisibility.DecideOutcome(0, 0, min));
        }

        [Fact]
        public void SummaryLine()
        {
            Assert.Equal("SeatGhostKerbal summary scene=FLIGHT frames=10 framesWithKerbal=8 once=8 twice=0 "
                + "missing=0 longestTwice=0 longestMissing=0 kerbals=Jebediah Kerman",
                GhostKerbalVisibility.FormatSummary("FLIGHT", 10, 8, 8, 0, 0, 0, 0, "Jebediah Kerman"));
            Assert.EndsWith("kerbals=none", GhostKerbalVisibility.FormatSummary("FLIGHT", 1, 0, 0, 0, 0, 0, 0, ""));
        }

        // ----- LiveGhostReentryFx -----

        [Fact]
        public void Reentry_HotLitAndDefectRun()
        {
            Assert.True(LiveGhostReentryFx.IsHot(LiveGhostReentryFx.HotRawIntensity, false));
            Assert.False(LiveGhostReentryFx.IsHot(0.5f, true));
            Assert.False(LiveGhostReentryFx.IsHot(0.05f, false));
            Assert.False(LiveGhostReentryFx.IsHot(float.NaN, false));
            Assert.True(LiveGhostReentryFx.IsLit(true, 0.2f));
            Assert.False(LiveGhostReentryFx.IsLit(true, 0f));
            Assert.False(LiveGhostReentryFx.IsLit(false, 0.2f));
            Assert.False(LiveGhostReentryFx.IsDefectRun(LiveGhostReentryFx.MinUnlitRunFrames - 1, 5f));
            Assert.False(LiveGhostReentryFx.IsDefectRun(100, LiveGhostReentryFx.MinUnlitRunSeconds - 0.01f));
            Assert.True(LiveGhostReentryFx.IsDefectRun(LiveGhostReentryFx.MinUnlitRunFrames,
                LiveGhostReentryFx.MinUnlitRunSeconds));
        }

        [Fact]
        public void Reentry_DecideOutcome()
        {
            Assert.Equal(LiveGhostReentryFx.CellOutcome.Skip, LiveGhostReentryFx.DecideOutcome(0, false));
            Assert.Equal(LiveGhostReentryFx.CellOutcome.Pass, LiveGhostReentryFx.DecideOutcome(3, false));
            Assert.Equal(LiveGhostReentryFx.CellOutcome.Fail, LiveGhostReentryFx.DecideOutcome(3, true));
            Assert.Equal(LiveGhostReentryFx.CellOutcome.Fail, LiveGhostReentryFx.DecideOutcome(0, true));
        }

        [Fact]
        public void Reentry_SummaryIsInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("LiveGhostReentryFx summary scene=FLIGHT frames=40 ghosts=1 inAtmosphere=40 hot=40 "
                    + "hotLit=38 hotUnlit=2 longestUnlitFrames=2 maxRaw=0.734 vessels=B4 Capsule",
                    LiveGhostReentryFx.FormatSummary("FLIGHT", 40, 1, 40, 40, 38, 2, 2, 0.7341f, "B4 Capsule"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
