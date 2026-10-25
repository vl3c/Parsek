using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TAIL-TRIM-UNWITNESSED-RESOURCE-CHANGES, the decoupled-converter missed trim: a running
    /// converter part that leaves the recorded vessel (decouple, undock, death) gets no off edge
    /// from the poll, which walks only the vessel's own parts, so the recording read it as running
    /// to its end and its tail never trimmed. The flight and background polls now write a
    /// ConverterDeactivated for every tracked running converter pid that is no longer on the vessel.
    /// </summary>
    [Collection("Sequential")]
    public class DepartedConverterStopTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DepartedConverterStopTests()
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

        private static PartEvent Activated(double ut, uint pid, string name)
        {
            return new PartEvent
            {
                ut = ut, partPersistentId = pid,
                eventType = PartEventType.ConverterActivated, partName = name
            };
        }

        [Fact]
        public void Collect_AllTrackedPartsPresent_WritesNothing()
        {
            var active = new HashSet<uint> { 10u, 20u };
            var present = new HashSet<uint> { 10u, 20u, 30u };

            var stops = FlightRecorder.CollectDepartedConverterStops(active, present, new List<PartEvent>(), 100.0);

            Assert.Empty(stops);
            Assert.Equal(2, active.Count);
        }

        [Fact]
        public void Collect_DepartedPart_StopsItAndDropsItFromTheSet()
        {
            var active = new HashSet<uint> { 10u, 20u };
            var present = new HashSet<uint> { 10u, 30u };
            var recorded = new List<PartEvent> { Activated(50.0, 20u, "RadialDrill"), Activated(51.0, 10u, "ISRU") };

            var stops = FlightRecorder.CollectDepartedConverterStops(active, present, recorded, 100.0);

            var stop = Assert.Single(stops);
            Assert.Equal(PartEventType.ConverterDeactivated, stop.eventType);
            Assert.Equal(20u, stop.partPersistentId);
            Assert.Equal(100.0, stop.ut);
            Assert.Equal("RadialDrill", stop.partName);
            Assert.Equal(new HashSet<uint> { 10u }, active);
        }

        [Fact]
        public void Collect_SeveralDeparted_PidOrder_NameFallsBackToUnknown()
        {
            var active = new HashSet<uint> { 30u, 10u, 20u };
            var present = new HashSet<uint> { 99u };
            var recorded = new List<PartEvent> { Activated(50.0, 20u, "RadialDrill") };

            var stops = FlightRecorder.CollectDepartedConverterStops(active, present, recorded, 100.0);

            Assert.Equal(new uint[] { 10u, 20u, 30u },
                new[] { stops[0].partPersistentId, stops[1].partPersistentId, stops[2].partPersistentId });
            Assert.Equal("unknown", stops[0].partName);
            Assert.Equal("RadialDrill", stops[1].partName);
            Assert.Empty(active);
        }

        [Fact]
        public void Collect_EmptyPresentSet_ProvesNothing_WritesNothing()
        {
            // A vessel read with no parts (an unloaded or half-built read) must never stop every
            // running converter: that would erase the background-leg converter witness.
            var active = new HashSet<uint> { 10u };

            Assert.Empty(FlightRecorder.CollectDepartedConverterStops(active, new HashSet<uint>(), null, 100.0));
            Assert.Empty(FlightRecorder.CollectDepartedConverterStops(active, null, null, 100.0));
            Assert.Single(active);
        }

        [Fact]
        public void Collect_NullOrEmptyActiveSet_WritesNothing()
        {
            var present = new HashSet<uint> { 1u };
            Assert.Empty(FlightRecorder.CollectDepartedConverterStops(null, present, null, 100.0));
            Assert.Empty(FlightRecorder.CollectDepartedConverterStops(new HashSet<uint>(), present, null, 100.0));
        }

        [Fact]
        public void Core_AppendsStops_AndLogsOneSummary()
        {
            var active = new HashSet<uint> { 10u, 20u };
            var present = new HashSet<uint> { 10u };
            var recorded = new List<PartEvent> { Activated(50.0, 20u, "RadialDrill") };

            int written = FlightRecorder.AppendDepartedConverterStopsCore(
                active, present, recorded, 150.25, "Recorder", "vessel pid=4242");

            Assert.Equal(1, written);
            Assert.Equal(2, recorded.Count);
            Assert.Equal(PartEventType.ConverterDeactivated, recorded[1].eventType);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Converter part(s) left the vessel while running")
                && l.Contains("vessel pid=4242")
                && l.Contains("stopped=1")
                && l.Contains("pids=[20]")
                && l.Contains("ut=150.25"));
        }

        [Fact]
        public void Core_NothingDeparted_NoEventsNoLog()
        {
            var active = new HashSet<uint> { 10u };
            var recorded = new List<PartEvent> { Activated(50.0, 10u, "ISRU") };

            int written = FlightRecorder.AppendDepartedConverterStopsCore(
                active, new HashSet<uint> { 10u }, recorded, 150.0, "BgRecorder", "bg vessel pid=1");

            Assert.Equal(0, written);
            Assert.Single(recorded);
            Assert.DoesNotContain(logLines, l => l.Contains("left the vessel while running"));
        }

        [Fact]
        public void DepartedStop_EndsTheTailTrimConverterWitness()
        {
            // The parent recording's converter run ends at the departure, so the witness no
            // longer counts it at the recording's end.
            var rec = new Recording();
            rec.PartEvents.Add(Activated(17035, 20u, "RadialDrill"));
            rec.Points.Add(new TrajectoryPoint { ut = 17000 });
            rec.Points.Add(new TrajectoryPoint { ut = 17630 });
            Assert.Equal(1, RecordingOptimizer.CountConvertersRunningAtEnd(rec));

            var active = new HashSet<uint> { 20u };
            FlightRecorder.AppendDepartedConverterStopsCore(
                active, new HashSet<uint> { 1u, 2u }, rec.PartEvents, 17100, "Recorder", "vessel pid=1");

            Assert.Equal(0, RecordingOptimizer.CountConvertersRunningAtEnd(rec));
            Assert.True(double.IsNaN(RecordingOptimizer.FindLastResourceChangeUT(rec)));
        }
    }
}
