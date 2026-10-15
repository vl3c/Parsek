using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the forwarded permanent-seed list that RecordingOptimizer.ForwardPermanentStateEvents
    /// appends to its INFO line. The harness facet `logContracts.forwardedSeeds`
    /// (harness/lib/hlib.py parse_forwarded_seed_lines) parses this exact text, so the
    /// prefix, the `: [` separator, the `Type:partName#pid` entry grammar and the canonical
    /// order are a contract with the harness, not cosmetics.
    /// </summary>
    [Collection("Sequential")]
    public class ForwardedSeedListLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ForwardedSeedListLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static PartEvent Evt(PartEventType type, string name, uint pid, double ut = 10.0)
        {
            return new PartEvent { ut = ut, eventType = type, partName = name, partPersistentId = pid };
        }

        [Fact]
        public void Forward_LogsCountThenCanonicallySortedSeedList()
        {
            var firstHalf = new List<PartEvent>
            {
                Evt(PartEventType.EngineIgnited, "liquidEngine2", 7),
                Evt(PartEventType.ShroudJettisoned, "liquidEngine2", 300),
                Evt(PartEventType.Decoupled, "radialDecoupler1-2", 50),
                Evt(PartEventType.Destroyed, "R8winglet", 2908259249),
                Evt(PartEventType.Decoupled, "R8winglet", 2908259249),
                Evt(PartEventType.Decoupled, "launchClamp1", 20),
                Evt(PartEventType.Decoupled, "launchClamp1", 10),
                Evt(PartEventType.ShroudJettisoned, "HeatShield2", 400),
            };
            var secondHalf = new List<PartEvent>();

            RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, secondHalf, 173.84);

            Assert.Equal(7, secondHalf.Count);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]") && l.Contains(
                "Forwarded 7 permanent state event(s) as seeds at UT=173.8: ["
                + "Decoupled:R8winglet#2908259249, "
                + "Decoupled:launchClamp1#10, "
                + "Decoupled:launchClamp1#20, "
                + "Decoupled:radialDecoupler1-2#50, "
                + "Destroyed:R8winglet#2908259249, "
                + "ShroudJettisoned:HeatShield2#400, "
                + "ShroudJettisoned:liquidEngine2#300]"));
        }

        [Fact]
        public void Forward_SeedListIsIndependentOfRecordedOrder()
        {
            var a = new List<PartEvent>
            {
                Evt(PartEventType.Decoupled, "b", 2),
                Evt(PartEventType.FairingJettisoned, "a", 1),
                Evt(PartEventType.Decoupled, "a", 9),
            };
            var b = new List<PartEvent> { a[2], a[0], a[1] };

            Assert.Equal(RecordingOptimizer.FormatForwardedSeedList(a),
                         RecordingOptimizer.FormatForwardedSeedList(b));
            Assert.Equal("[Decoupled:a#9, Decoupled:b#2, FairingJettisoned:a#1]",
                         RecordingOptimizer.FormatForwardedSeedList(a));
        }

        [Fact]
        public void Format_NullPartNamePrintsQuestionMark_EmptyPrintsBrackets()
        {
            Assert.Equal("[]", RecordingOptimizer.FormatForwardedSeedList(new List<PartEvent>()));
            Assert.Equal("[]", RecordingOptimizer.FormatForwardedSeedList(null));
            Assert.Equal("[ParachuteDestroyed:?#5]", RecordingOptimizer.FormatForwardedSeedList(
                new List<PartEvent> { Evt(PartEventType.ParachuteDestroyed, null, 5) }));
        }

        [Fact]
        public void Forward_NoPermanentEvents_LogsNothing()
        {
            var firstHalf = new List<PartEvent> { Evt(PartEventType.EngineIgnited, "e", 1) };
            RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, new List<PartEvent>(), 50.0);
            Assert.DoesNotContain(logLines, l => l.Contains("Forwarded"));
        }

        [Fact]
        public void Forward_LineIsCultureInvariant()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var firstHalf = new List<PartEvent> { Evt(PartEventType.Decoupled, "launchClamp1", 1234567) };
                RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, new List<PartEvent>(), 1234.56);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }

            Assert.Contains(logLines, l => l.Contains(
                "Forwarded 1 permanent state event(s) as seeds at UT=1234.6: [Decoupled:launchClamp1#1234567]"));
        }
    }
}
