using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class SectionWarpRunsTests
    {
        private static TrajectoryPoint Pt(double ut) => new TrajectoryPoint { ut = ut };

        [Theory]
        [InlineData(false, false, 1.0f, 1.0f)]   // 1x
        [InlineData(false, true, 1.0f, 1.0f)]    // LOW mode at rung 0 is still 1x
        [InlineData(false, true, 1.0004f, 1.0f)] // a lerp settle just above 1 is 1x
        [InlineData(false, true, 2.0f, 2.0f)]    // physics warp keeps its rate
        [InlineData(false, true, 4.0f, 4.0f)]
        [InlineData(false, false, 5.0f, -1.0f)]  // rails rate while still off rails
        [InlineData(true, true, 2.0f, -1.0f)]    // an on-rails sample is rails whatever the mode
        [InlineData(false, true, float.NaN, 1.0f)]
        public void EncodeFrameRate_ClassifiesEveryState(bool onRails, bool lowMode, float rate, float expected)
        {
            Assert.Equal(expected, SectionWarpRuns.EncodeFrameRate(onRails, lowMode, rate));
        }

        [Theory]
        [InlineData(1.0f, SectionWarpRuns.KindNormal)]
        [InlineData(1.5f, SectionWarpRuns.KindPhysics)]
        [InlineData(-1.0f, SectionWarpRuns.KindRails)]
        public void ClassifyEncodedRate_MapsToKind(float encoded, string kind)
        {
            Assert.Equal(kind, SectionWarpRuns.ClassifyEncodedRate(encoded));
        }

        [Fact]
        public void Build_FoldsContiguousKindsAndTracksPhysicsMaxRate()
        {
            var frames = new List<TrajectoryPoint> { Pt(10), Pt(11), Pt(12.04), Pt(15.08), Pt(16), Pt(17) };
            var rates = new List<float> { 1f, 1f, 1.5f, 2f, 1f, -1f };

            List<SectionWarpRuns.Run> runs = SectionWarpRuns.Build(frames, rates);

            Assert.Equal(4, runs.Count);
            Assert.Equal(SectionWarpRuns.KindNormal, runs[0].Kind);
            Assert.Equal(2, runs[0].Count);
            Assert.Equal(SectionWarpRuns.KindPhysics, runs[1].Kind);
            Assert.Equal(2, runs[1].Count);
            Assert.Equal(12.04, runs[1].FirstUT);
            Assert.Equal(15.08, runs[1].LastUT);
            Assert.Equal(2f, runs[1].MaxRate);
            Assert.Equal(SectionWarpRuns.KindNormal, runs[2].Kind);
            Assert.Equal(SectionWarpRuns.KindRails, runs[3].Kind);
            Assert.Equal(2, SectionWarpRuns.PhysicsFrameCount(runs));
        }

        [Fact]
        public void Append_FoldsInPlaceWithOneEntryPerRun()
        {
            var runs = new List<SectionWarpRuns.Run>();
            SectionWarpRuns.Append(runs, 1.0, 1f);
            SectionWarpRuns.Append(runs, 1.02, 1f);
            SectionWarpRuns.Append(runs, 1.06, 2f);
            SectionWarpRuns.Append(runs, 1.14, 4f);
            SectionWarpRuns.Append(runs, 1.22, 3f);
            SectionWarpRuns.Append(null, 1.0, 1f); // tolerated
            Assert.Equal(2, runs.Count);
            Assert.Equal(2, runs[0].Count);
            Assert.Equal(3, runs[1].Count);
            Assert.Equal(1.06, runs[1].FirstUT);
            Assert.Equal(1.22, runs[1].LastUT);
            Assert.Equal(4f, runs[1].MaxRate);
        }

        [Fact]
        public void TrimToFrames_DropsLaterRunsAndRecounts()
        {
            var frames = new List<TrajectoryPoint> { Pt(10), Pt(11), Pt(12), Pt(13), Pt(14) };
            var runs = SectionWarpRuns.Build(frames, new List<float> { 1f, 1f, 2f, 2f, 1f });
            Assert.Equal(3, runs.Count);

            frames.RemoveRange(3, 2); // trimmed back to UT 12
            SectionWarpRuns.TrimToFrames(runs, frames);

            Assert.Equal(2, runs.Count);
            Assert.Equal(2, runs[0].Count);
            Assert.Equal(1, runs[1].Count);
            Assert.Equal(12.0, runs[1].LastUT);
            Assert.Equal("1x:2:10.000-11.000,phys:1:12.000-12.000:max=2.000", SectionWarpRuns.Format(runs));

            frames.Clear();
            SectionWarpRuns.TrimToFrames(runs, frames);
            Assert.Empty(runs);
        }

        [Fact]
        public void Build_MisalignedInputs_ReportsNothing()
        {
            var frames = new List<TrajectoryPoint> { Pt(1), Pt(2) };
            Assert.Empty(SectionWarpRuns.Build(frames, new List<float> { 1f }));
            Assert.Empty(SectionWarpRuns.Build(null, new List<float>()));
            Assert.Empty(SectionWarpRuns.Build(frames, null));
        }

        [Fact]
        public void Format_IsInvariantAndCarriesPhysicsMax()
        {
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                var frames = new List<TrajectoryPoint> { Pt(83.14), Pt(142.46), Pt(142.5), Pt(212.18) };
                var rates = new List<float> { 1f, 1f, 2f, 2f };
                string text = SectionWarpRuns.Format(SectionWarpRuns.Build(frames, rates));
                Assert.Equal("1x:2:83.140-142.460,phys:2:142.500-212.180:max=2.000", text);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Fact]
        public void Format_Empty_IsDash()
        {
            Assert.Equal("-", SectionWarpRuns.Format(new List<SectionWarpRuns.Run>()));
            Assert.Equal("-", SectionWarpRuns.Format(null));
        }

        // The recorder spaces samples by GAME UT. Under physics warp one physics frame
        // advances game time by 0.02 * rate (TimeWarp.updateRate sets fixedDeltaTime =
        // 0.02 * timeScale), so a steady-state coast samples at the first frame whose
        // elapsed game time reaches maxInterval: every gap lands in
        // [maxInterval, maxInterval + one frame). This pins that contract for the three
        // density presets at 1x, 2x and 4x.
        [Theory]
        [InlineData(SamplingDensity.Low, 1f)]
        [InlineData(SamplingDensity.Low, 4f)]
        [InlineData(SamplingDensity.Medium, 1f)]
        [InlineData(SamplingDensity.Medium, 2f)]
        [InlineData(SamplingDensity.Medium, 4f)]
        [InlineData(SamplingDensity.High, 1f)]
        [InlineData(SamplingDensity.High, 4f)]
        public void ShouldRecordPoint_GameTimeSpacingHoldsUnderPhysicsWarp(SamplingDensity density, float rate)
        {
            float min = ParsekSettings.GetMinSampleInterval(density);
            float max = ParsekSettings.GetMaxSampleInterval(density);
            float dir = ParsekSettings.GetVelocityDirThreshold(density);
            float spd = ParsekSettings.GetSpeedChangeThreshold(density) / 100f;
            double tick = 0.02 * rate;
            var vel = new Vector3(100f, 0f, 0f);

            double last = -1;
            var uts = new List<double>();
            for (int i = 0; i < 20000; i++)
            {
                double ut = 100.0 + i * tick;
                if (TrajectoryMath.ShouldRecordPoint(vel, vel, ut, last, min, max, dir, spd))
                {
                    uts.Add(ut);
                    last = ut;
                }
            }

            Assert.True(uts.Count > 10);
            for (int i = 1; i < uts.Count; i++)
            {
                double gap = uts[i] - uts[i - 1];
                Assert.True(gap >= max - 1e-9, $"gap {gap} below max {max} at rate {rate}");
                Assert.True(gap < max + tick + 1e-9, $"gap {gap} beyond max+tick at rate {rate}");
            }
        }

        // A steady turn fires the direction trigger at the first frame past the
        // threshold, whatever the rate: the overshoot is at most one frame of turn.
        [Theory]
        [InlineData(SamplingDensity.Medium, 1f)]
        [InlineData(SamplingDensity.Medium, 4f)]
        [InlineData(SamplingDensity.High, 4f)]
        public void ShouldRecordPoint_DirectionTriggerFiresWithinOneFrameUnderPhysicsWarp(
            SamplingDensity density, float rate)
        {
            float min = ParsekSettings.GetMinSampleInterval(density);
            float max = ParsekSettings.GetMaxSampleInterval(density);
            float dir = ParsekSettings.GetVelocityDirThreshold(density);
            float spd = ParsekSettings.GetSpeedChangeThreshold(density) / 100f;
            double tick = 0.02 * rate;
            const double turnDegPerSecond = 3.0;

            double last = -1;
            Vector3 lastVel = Vector3.zero;
            double lastAngle = 0;
            int triggered = 0;
            for (int i = 0; i < 20000; i++)
            {
                double ut = 100.0 + i * tick;
                double angle = (ut - 100.0) * turnDegPerSecond;
                double rad = angle * System.Math.PI / 180.0;
                var vel = new Vector3((float)(100.0 * System.Math.Cos(rad)), 0f,
                    (float)(100.0 * System.Math.Sin(rad)));
                if (TrajectoryMath.ShouldRecordPoint(vel, lastVel, ut, last, min, max, dir, spd))
                {
                    if (last >= 0)
                    {
                        double swept = angle - lastAngle;
                        double gap = ut - last;
                        if (gap < max - 1e-9)
                        {
                            triggered++;
                            Assert.True(swept <= dir + turnDegPerSecond * tick + 1e-3,
                                $"swept {swept} beyond threshold+one frame at rate {rate}");
                        }
                    }
                    last = ut;
                    lastVel = vel;
                    lastAngle = angle;
                }
            }
            Assert.True(triggered > 0, "direction trigger never fired");
        }
    }
}
