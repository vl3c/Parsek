using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of the route event log: one <see cref="RouteEvent"/>
    /// per <see cref="RouteEventType"/> through its EVENT codec, and the whole
    /// <see cref="RouteEventLog"/> through the ROUTE_EVENT_LOG node. The event codec is not
    /// kind-specific (every field is written sparsely for every kind), so each kind must carry
    /// every field back. The log's static state is enumerated, so a new static field reds
    /// <see cref="EventLog_EveryStaticFieldIsClassified"/> until it is persisted or classified.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(RouteEvent))]
    [SaveRoundTripCovers(typeof(RouteEventLog))]
    public class RouteEventSaveRoundTripTests : IDisposable
    {
        public RouteEventSaveRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RouteEventLog.ResetForTesting();
        }

        public void Dispose()
        {
            RouteEventLog.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>Every static field of <see cref="RouteEventLog"/> and how a save treats it.</summary>
        internal static readonly Dictionary<string, StaticFieldRule> EventLogStatics =
            new Dictionary<string, StaticFieldRule>(StringComparer.Ordinal)
            {
                ["events"] = StaticFieldRule.Persisted(),
                ["StateVersion"] = StaticFieldRule.NotPersisted(
                    "change counter for caches keyed on the log; a load bumps it, never restores it"),
                ["IC"] = StaticFieldRule.Constant(),
            };

        public static IEnumerable<object[]> EventTypes() =>
            Enum.GetValues(typeof(RouteEventType)).Cast<object>().Select(v => new object[] { Convert.ToInt32(v) });

        private static RouteEvent BuildEvent(FieldRoundTrip rt, RouteEventType type)
        {
            var e = rt.Build<RouteEvent>();
            e.Type = type;
            return e;
        }

        [Theory]
        [MemberData(nameof(EventTypes))]
        public void Event_EveryKindRoundTripsEveryField(int typeValue)
        {
            var type = (RouteEventType)typeValue;
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: typeValue * 1000);
                RouteEvent written = BuildEvent(rt, type);

                var parent = new ConfigNode(RouteEventLog.NodeName);
                written.SerializeInto(parent);
                RouteEvent read = RouteEvent.DeserializeFrom(parent.GetNode(RouteEvent.NodeName), out string reject);

                Assert.True(read != null, "rejected: " + reject);
                var problems = rt.Diff(written, read, "RouteEvent." + type);
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RouteEvent " + type, problems));
            });
        }

        [Fact]
        public void Event_FillerReachesEveryNestedType()
        {
            var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: 50);
            RouteEvent e = BuildEvent(rt, RouteEventType.RouteCargoDebited);
            Assert.NotEmpty(e.RouteResourceManifest);
            Assert.NotEmpty(e.RouteRequestedResourceManifest);
            Assert.NotEmpty(e.RouteInventoryManifest);
            Assert.NotEmpty(e.RouteRequestedInventoryManifest);
            Assert.NotEmpty(e.RouteInventoryManifest[0].StoredResources);
            Assert.NotEqual(RouteDispatchEvaluator.EligibilityFailureKind.None, e.RouteHoldKind);
        }

        [Fact]
        public void EventLog_EveryStaticFieldIsClassified()
        {
            StaticFieldRule.AssertClassified(typeof(RouteEventLog), EventLogStatics);
        }

        [Fact]
        public void EventLog_RoundTripsOneEventOfEveryKindInOrder()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: 9000);
                foreach (RouteEventType type in Enum.GetValues(typeof(RouteEventType)))
                    RouteEventLog.Add(BuildEvent(rt, type));
                var written = StaticFieldRule.Snapshot(typeof(RouteEventLog), EventLogStatics);

                var parsek = new ConfigNode("PARSEK");
                RouteEventLog.SaveInto(parsek);
                List<RouteEvent> decoded = RouteEventLog.ReadSavedEvents(parsek);
                RouteEventLog.ResetForTesting();
                RouteEventLog.LoadFrom(parsek);
                var read = StaticFieldRule.Snapshot(typeof(RouteEventLog), EventLogStatics);

                var problems = StaticFieldRule.Diff(rt, typeof(RouteEventLog), EventLogStatics, written, read);
                problems.AddRange(rt.Diff(written["events"], decoded, "RouteEventLog.ReadSavedEvents"));
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RouteEventLog", problems));
                Assert.Equal(Enum.GetValues(typeof(RouteEventType)).Length, RouteEventLog.Events.Count);
            });
        }

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: 77);
            RouteEvent written = BuildEvent(rt, RouteEventType.RouteHeld);
            var parent = new ConfigNode(RouteEventLog.NodeName);
            written.SerializeInto(parent);
            parent.GetNode(RouteEvent.NodeName).RemoveValue("routeHoldShortfall");
            RouteEvent read = RouteEvent.DeserializeFrom(parent.GetNode(RouteEvent.NodeName), out _);
            var problems = rt.Diff(written, read, "RouteEvent");
            Assert.Contains(problems, p => p.StartsWith("RouteEvent.RouteHoldShortfall:", StringComparison.Ordinal));
        }
    }
}
