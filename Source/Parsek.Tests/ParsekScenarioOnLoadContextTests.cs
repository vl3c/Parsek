using System.Diagnostics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The load context <c>ParsekScenario.OnLoad</c> threads through its flow methods: the
    /// phase setter keeps every assignment for the once-per-load trace line, and the trace
    /// names the most specific load kind known.
    /// </summary>
    public class ParsekScenarioOnLoadContextTests
    {
        [Fact]
        public void Phase_KeepsTheLatestValue_AndAppendsEveryAssignment()
        {
            var ctx = new ParsekScenario.OnLoadContext(new ConfigNode("SCENARIO"), null);
            Assert.Null(ctx.Phase);

            ctx.Phase = "entry";
            ctx.Phase = "settings";
            ctx.Phase = "settings";

            Assert.Equal("settings", ctx.Phase);
            Assert.Equal(
                "Load phases: kind=unclassified status=<none> count=3 [entry@0ms > settings@0ms > settings@0ms]",
                ctx.FormatPhaseTrace());
        }

        [Fact]
        public void FormatPhaseTrace_BeforeClassification_SaysUnclassified()
        {
            var ctx = new ParsekScenario.OnLoadContext(new ConfigNode("SCENARIO"), null);
            ctx.Phase = "entry";
            ctx.Status = "running";

            Assert.Equal("Load phases: kind=unclassified status=running count=1 [entry@0ms]",
                ctx.FormatPhaseTrace());
        }

        [Fact]
        public void FormatPhaseTrace_WithNoPhaseOrStatus_IsStillOneWellFormedLine()
        {
            var ctx = new ParsekScenario.OnLoadContext(null, null);
            Assert.Equal("Load phases: kind=unclassified status=<none> count=0 []", ctx.FormatPhaseTrace());
        }

        [Fact]
        public void FormatPhaseTrace_NamesTheEarlyKind_UntilARefinedKindIsSet()
        {
            var ctx = new ParsekScenario.OnLoadContext(new ConfigNode("SCENARIO"), Stopwatch.StartNew());
            ctx.Phase = "entry";
            ctx.Classify(default(ParsekScenario.LoadClassificationFacts), EarlyLoadKind.InSession, null);
            ctx.Phase = "load-classify";
            ctx.Status = "completed";

            Assert.True(ctx.Classified);
            Assert.Equal(EarlyLoadKind.InSession, ctx.EarlyLoadKind);
            string early = ctx.FormatPhaseTrace();
            Assert.StartsWith("Load phases: kind=InSession status=completed count=2 [entry@", early);
            Assert.Contains("ms > load-classify@", early);
            Assert.EndsWith("ms]", early);

            ctx.RefinedLoadKind = LoadKind.QuickloadFlight;
            Assert.StartsWith("Load phases: kind=QuickloadFlight ", ctx.FormatPhaseTrace());
        }

        [Fact]
        public void Classify_KeepsTheColdReason()
        {
            var ctx = new ParsekScenario.OnLoadContext(new ConfigNode("SCENARIO"), null);
            ctx.Classify(default(ParsekScenario.LoadClassificationFacts), EarlyLoadKind.Cold, "boot");

            Assert.Equal(EarlyLoadKind.Cold, ctx.EarlyLoadKind);
            Assert.Equal("boot", ctx.ColdReason);
            Assert.Null(ctx.RefinedLoadKind);
            Assert.False(ctx.StoppedInPrologue);
            Assert.Equal("Load phases: kind=Cold status=<none> count=0 []", ctx.FormatPhaseTrace());
        }
    }
}
