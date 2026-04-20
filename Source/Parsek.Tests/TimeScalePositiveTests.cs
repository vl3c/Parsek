using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    public class TimeScalePositiveTests
    {
        [Fact]
        public void ClassifyTimeScalePositiveSamples_PassesWhenRecoveryFollowsOnlyStockPauseSamples()
        {
            var outcome = RuntimeTests.ClassifyTimeScalePositiveSamples(
                new List<RuntimeTests.TimeScalePositiveProbeSample>
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = true,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = true,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 1f,
                        FlightDriverPause = false,
                    },
                });

            Assert.Equal(RuntimeTests.TimeScalePositiveProbeOutcome.Passed, outcome);
        }

        [Fact]
        public void ClassifyTimeScalePositiveSamples_SkipsWhenEverySampleShowsStockPause()
        {
            var outcome = RuntimeTests.ClassifyTimeScalePositiveSamples(
                new List<RuntimeTests.TimeScalePositiveProbeSample>
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = true,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = true,
                    },
                });

            Assert.Equal(RuntimeTests.TimeScalePositiveProbeOutcome.SkipStockPause, outcome);
        }

        [Fact]
        public void ClassifyTimeScalePositiveSamples_FailsWhenTimeScaleStaysZeroWithoutPause()
        {
            var outcome = RuntimeTests.ClassifyTimeScalePositiveSamples(
                new List<RuntimeTests.TimeScalePositiveProbeSample>
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = true,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = false,
                    },
                });

            Assert.Equal(RuntimeTests.TimeScalePositiveProbeOutcome.FailZeroWithoutPause, outcome);
        }

        [Fact]
        public void ClassifyTimeScalePositiveSamples_FailsWhenTransientZeroWithoutPauseLaterRecovers()
        {
            var outcome = RuntimeTests.ClassifyTimeScalePositiveSamples(
                new List<RuntimeTests.TimeScalePositiveProbeSample>
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = true,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = false,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 1f,
                        FlightDriverPause = false,
                    },
                });

            Assert.Equal(RuntimeTests.TimeScalePositiveProbeOutcome.FailZeroWithoutPause, outcome);
        }

        [Fact]
        public void ClassifyTimeScalePositiveSamples_ReturnsPauseProbeUnavailableWhenPauseStateIsUnavailable()
        {
            var outcome = RuntimeTests.ClassifyTimeScalePositiveSamples(
                new List<RuntimeTests.TimeScalePositiveProbeSample>
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = null,
                    },
                });

            Assert.Equal(
                RuntimeTests.TimeScalePositiveProbeOutcome.SkipPauseProbeUnavailable,
                outcome);
        }

        [Fact]
        public void ClassifyTimeScalePositiveSamples_PrefersStockPauseWhenConfirmedPauseExists()
        {
            var outcome = RuntimeTests.ClassifyTimeScalePositiveSamples(
                new List<RuntimeTests.TimeScalePositiveProbeSample>
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = true,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = null,
                    },
                });

            Assert.Equal(RuntimeTests.TimeScalePositiveProbeOutcome.SkipStockPause, outcome);
        }

        [Fact]
        public void ClassifyTimeScalePositiveSamples_DoesNotPassWhenRecoveryFollowsUnavailablePauseState()
        {
            var outcome = RuntimeTests.ClassifyTimeScalePositiveSamples(
                new List<RuntimeTests.TimeScalePositiveProbeSample>
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = null,
                    },
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 1f,
                        FlightDriverPause = false,
                    },
                });

            Assert.Equal(
                RuntimeTests.TimeScalePositiveProbeOutcome.SkipPauseProbeUnavailable,
                outcome);
        }

        [Fact]
        public void FormatTimeScalePositiveProbeSummary_ContainsKeyDiagnosticFields()
        {
            string summary = RuntimeTests.FormatTimeScalePositiveProbeSummary(
                new[]
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        SampleIndex = 3,
                        FrameCount = 42,
                        RealtimeSinceStartup = 12.5f,
                        TimeScale = 0f,
                        FlightDriverPause = true,
                        KspLoaderLastUpdate = "1234",
                        RunnerCoroutineState = "isRunning=True batch=active inner=active",
                        SceneName = "SPACECENTER",
                    }
                });

            Assert.Contains("sample=3", summary);
            Assert.Contains("frame=42", summary);
            Assert.Contains("realtime=12.50", summary);
            Assert.Contains("timeScale=0.000", summary);
            Assert.Contains("FlightDriver.Pause=True", summary);
            Assert.Contains("KSPLoader.lastUpdate=1234", summary);
            Assert.Contains("runner=isRunning=True batch=active inner=active", summary);
            Assert.Contains("scene=SPACECENTER", summary);
        }

        [Fact]
        public void FormatTimeScalePositiveProbeSummary_ReportsUnavailablePauseState()
        {
            string summary = RuntimeTests.FormatTimeScalePositiveProbeSummary(
                new[]
                {
                    new RuntimeTests.TimeScalePositiveProbeSample
                    {
                        TimeScale = 0f,
                        FlightDriverPause = null,
                    }
                });

            Assert.Contains("FlightDriver.Pause=unavailable", summary);
        }
    }
}
