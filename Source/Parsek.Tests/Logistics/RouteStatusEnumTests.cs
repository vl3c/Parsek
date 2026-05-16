using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    [Collection("Sequential")]
    public class RouteStatusEnumTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteStatusEnumTests()
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

        // catches: a future PR silently adding/reordering a value that breaks save compat.
        // RouteStatus is serialized by name today, but ordinal positions are still
        // pinned so anything that depends on numeric encoding cannot drift unnoticed.
        [Fact]
        public void EnumExhaustivenessPin()
        {
            var values = Enum.GetValues(typeof(RouteStatus))
                .Cast<RouteStatus>()
                .ToArray();

            Assert.Equal(9, values.Length);

            Assert.Equal(0, (int)RouteStatus.Active);
            Assert.Equal(1, (int)RouteStatus.InTransit);
            Assert.Equal(2, (int)RouteStatus.WaitingForResources);
            Assert.Equal(3, (int)RouteStatus.WaitingForFunds);
            Assert.Equal(4, (int)RouteStatus.DestinationFull);
            Assert.Equal(5, (int)RouteStatus.EndpointLost);
            Assert.Equal(6, (int)RouteStatus.MissingSourceRecording);
            Assert.Equal(7, (int)RouteStatus.SourceChanged);
            Assert.Equal(8, (int)RouteStatus.Paused);
        }

        // catches: a refactor that drops the transition log line.
        [Fact]
        public void TransitionTo_LogsFromAndTo()
        {
            var route = new Route { Id = "abcdef0123456789" };

            route.TransitionTo(RouteStatus.Paused, "test");

            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Contains(logLines,
                l => l.Contains("[RouteStore]")
                    && l.Contains("Active")
                    && l.Contains("Paused")
                    && l.Contains("reason=test"));
        }

        // catches: spurious info noise on no-op transitions.
        [Fact]
        public void TransitionTo_SameStatus_LogsVerbose_NotInfo()
        {
            var route = new Route { Id = "noopnoopnoop" }; // Status defaults to Active

            int beforeCount = logLines.Count;
            route.TransitionTo(RouteStatus.Active, "x");
            int added = logLines.Count - beforeCount;

            Assert.Equal(1, added);
            string line = logLines[logLines.Count - 1];
            Assert.Contains("[VERBOSE]", line);
            Assert.Contains("[RouteStore]", line);
            Assert.Contains("stay=Active", line);
            Assert.DoesNotContain("[INFO]", line);
        }
    }
}
