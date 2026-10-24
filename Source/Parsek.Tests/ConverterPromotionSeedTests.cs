using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TAIL-TRIM-UNWITNESSED-RESOURCE-CHANGES, the promotion residue: a recording started as
    /// a promotion into a fresh branch skips the full seed set, so a converter already running
    /// there carried no ConverterActivated and the tail-trim witness never saw it. The
    /// promotion path now emits converter seeds, gated like the engine seeds.
    /// </summary>
    [Collection("Sequential")]
    public class ConverterPromotionSeedTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ConverterPromotionSeedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void Gate_FreshBranchWithNoEvents_EmitsConverterSeeds()
        {
            Assert.True(FlightRecorder.ChainPromotionShouldEmitConverterSeeds(null, out int nullCount));
            Assert.Equal(0, nullCount);

            var fresh = new Recording();
            Assert.True(FlightRecorder.ChainPromotionShouldEmitConverterSeeds(fresh, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void Gate_OnlyNonConverterEvents_StillEmits()
        {
            var rec = new Recording();
            rec.PartEvents.Add(new PartEvent { ut = 10, partPersistentId = 1, eventType = PartEventType.EngineIgnited, value = 1f });
            rec.PartEvents.Add(new PartEvent { ut = 11, partPersistentId = 2, eventType = PartEventType.LightOn });

            Assert.True(FlightRecorder.ChainPromotionShouldEmitConverterSeeds(rec, out int count));
            Assert.Equal(0, count);
        }

        [Theory]
        [InlineData(PartEventType.ConverterActivated)]
        [InlineData(PartEventType.ConverterDeactivated)]
        public void Gate_RecordingWithAConverterEdge_KeepsItsOwnState(PartEventType edge)
        {
            var rec = new Recording();
            rec.PartEvents.Add(new PartEvent { ut = 10, partPersistentId = 7, eventType = edge });

            Assert.False(FlightRecorder.ChainPromotionShouldEmitConverterSeeds(rec, out int count));
            Assert.Equal(1, count);
        }

        [Fact]
        public void Emit_OneActivatedSeedPerRunningConverterPart()
        {
            var sets = new PartTrackingSets();
            sets.activeConverterParts.Add(100000u);
            sets.activeConverterParts.Add(101111u);
            var events = new List<PartEvent>();

            int emitted = PartStateSeeder.EmitConverterSeedEvents(
                sets, events, 250.0, "Recorder", pid => pid == 100000u ? "RadialDrill" : "ISRU");

            Assert.Equal(2, emitted);
            Assert.Equal(2, events.Count);
            Assert.All(events, e =>
            {
                Assert.Equal(PartEventType.ConverterActivated, e.eventType);
                Assert.Equal(250.0, e.ut);
                Assert.Equal(0, e.moduleIndex);
            });
            Assert.Contains(events, e => e.partPersistentId == 100000u && e.partName == "RadialDrill");
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Seed event: ConverterActivated pid=101111 part='ISRU' (promotion)"));
        }

        [Fact]
        public void Emit_NoRunningConverter_EmitsNothing()
        {
            var events = new List<PartEvent>();
            Assert.Equal(0, PartStateSeeder.EmitConverterSeedEvents(
                new PartTrackingSets(), events, 1.0, "Recorder", null));
            Assert.Equal(0, PartStateSeeder.EmitConverterSeedEvents(null, events, 1.0, "Recorder", null));
            Assert.Empty(events);
        }

        [Fact]
        public void PromotionSeed_WitnessesTheConverterForTailTrim()
        {
            // A fresh branch with a drill running and no later converter edge: the seed is
            // what makes CountConvertersRunningAtEnd keep the tail.
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = 400.0, bodyName = "Mun" });
            Assert.Equal(0, RecordingOptimizer.CountConvertersRunningAtEnd(rec));

            var sets = new PartTrackingSets();
            sets.activeConverterParts.Add(100000u);
            PartStateSeeder.EmitConverterSeedEvents(
                sets, rec.PartEvents, FlightRecorder.ResolveChainPromotionSeedUT(rec, 400.0), "Recorder", null);

            Assert.Equal(1, RecordingOptimizer.CountConvertersRunningAtEnd(rec));
            Assert.Equal(100.0, rec.PartEvents[0].ut);
        }
    }
}
