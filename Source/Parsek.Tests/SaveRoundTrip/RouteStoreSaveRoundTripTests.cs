using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of the whole supply-route state through
    /// <see cref="RouteStore.SaveRoutesTo"/> / <see cref="RouteStore.LoadRoutesFrom"/>: committed
    /// and dormant routes (every field, see <see cref="RouteSaveRoundTripTests"/>), the
    /// dismissed and prompted candidate sets, the route state floor, and the route event log and
    /// retired route cargo the store saves beside them. Every static field of the store is
    /// enumerated, so a new one reds <see cref="Store_EveryStaticFieldIsClassified"/> until it
    /// is persisted or classified with a reason.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(RouteStore))]
    public class RouteStoreSaveRoundTripTests : IDisposable
    {
        public RouteStoreSaveRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
        }

        private static void ResetAll()
        {
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
            RetiredRouteCargoStore.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        internal static readonly Dictionary<string, StaticFieldRule> Statics =
            new Dictionary<string, StaticFieldRule>(StringComparer.Ordinal)
            {
                ["StateFloorUT"] = StaticFieldRule.Persisted(),
                ["committedRoutes"] = StaticFieldRule.Persisted(),
                ["dormantRoutes"] = StaticFieldRule.Persisted(),
                ["dismissedCandidateTreeIds"] = StaticFieldRule.Persisted(),
                ["promptedCandidateTreeIds"] = StaticFieldRule.Persisted(),
                ["cargoEscrow"] = StaticFieldRule.NotPersisted(
                    "RAM-only within-tick reservation, re-established from pending route state by the "
                    + "next orchestrator tick (RouteOrchestrator.ReEstablishEscrowForUnfiredWindows)"),
            };

        private static T StaticValue<T>(string name) =>
            (T)StaticFieldRule.StateFields(typeof(RouteStore))
                .Single(f => FieldRoundTrip.LogicalName(f) == name).GetValue(null);

        /// <summary>Fills every persisted static of the store (and the two stores it saves).</summary>
        private static void Populate(FieldRoundTrip rt)
        {
            var committed = StaticValue<List<Route>>("committedRoutes");
            var dormant = StaticValue<List<Route>>("dormantRoutes");
            committed.Add(rt.Build<Route>());
            committed.Add(rt.Build<Route>());
            dormant.Add(rt.Build<Route>());

            // Load sweeps candidate ids of trees that no longer exist, so the ids name live trees.
            foreach (string set in new[] { "dismissedCandidateTreeIds", "promptedCandidateTreeIds" })
            {
                for (int i = 0; i < 2; i++)
                {
                    string treeId = set + "-tree" + i;
                    RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = treeId });
                    StaticValue<HashSet<string>>(set).Add(treeId);
                }
            }
            RouteStore.StateFloorUT = 12345.678;

            foreach (RouteEventType type in Enum.GetValues(typeof(RouteEventType)))
            {
                var e = rt.Build<RouteEvent>();
                e.Type = type;
                RouteEventLog.Add(e);
            }
            var cargoRt = new FieldRoundTrip(RetiredRouteCargoSaveRoundTripTests.Specs(), seed: 50000);
            RetiredRouteCargoSaveRoundTripTests.Populate(cargoRt, RouteEventType.RouteCargoDelivered);

            StaticValue<Dictionary<string, Dictionary<uint, Dictionary<string, double>>>>("cargoEscrow")
                ["escrowRoute"] = new Dictionary<uint, Dictionary<string, double>>
                {
                    [7u] = new Dictionary<string, double> { ["LiquidFuel"] = 3.5 },
                };
        }

        [Fact]
        public void Store_EveryStaticFieldIsClassified()
        {
            StaticFieldRule.AssertClassified(typeof(RouteStore), Statics);
        }

        [Fact]
        public void Store_EveryFieldRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: 20000);
                Populate(rt);
                var written = StaticFieldRule.Snapshot(typeof(RouteStore), Statics);
                var writtenEvents = StaticFieldRule.Snapshot(typeof(RouteEventLog), RouteEventSaveRoundTripTests.EventLogStatics);
                var writtenCargo = StaticFieldRule.Snapshot(typeof(RetiredRouteCargoStore), RetiredRouteCargoSaveRoundTripTests.Statics);

                var parsek = new ConfigNode("PARSEK");
                RouteStore.SaveRoutesTo(parsek);

                // The read-only decoders must agree with what the store wrote.
                var problems = new List<string>();
                problems.AddRange(rt.Diff(written["committedRoutes"], RouteStore.ReadSavedCommittedRoutes(parsek),
                    "RouteStore.ReadSavedCommittedRoutes"));
                problems.AddRange(rt.Diff(written["StateFloorUT"], RouteStore.ReadSavedRouteStateFloorUT(parsek),
                    "RouteStore.ReadSavedRouteStateFloorUT"));

                // Reset the static stores (not the committed trees the candidate sweep reads).
                RouteStore.ResetForTesting();
                RouteEventLog.ResetForTesting();
                RetiredRouteCargoStore.ResetForTesting();
                RouteStore.LoadRoutesFrom(parsek);

                var read = StaticFieldRule.Snapshot(typeof(RouteStore), Statics);
                problems.AddRange(StaticFieldRule.Diff(rt, typeof(RouteStore), Statics, written, read));
                problems.AddRange(StaticFieldRule.Diff(rt, typeof(RouteEventLog), RouteEventSaveRoundTripTests.EventLogStatics,
                    writtenEvents, StaticFieldRule.Snapshot(typeof(RouteEventLog), RouteEventSaveRoundTripTests.EventLogStatics)));
                problems.AddRange(StaticFieldRule.Diff(rt, typeof(RetiredRouteCargoStore), RetiredRouteCargoSaveRoundTripTests.Statics,
                    writtenCargo, StaticFieldRule.Snapshot(typeof(RetiredRouteCargoStore), RetiredRouteCargoSaveRoundTripTests.Statics)));
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RouteStore", problems));

                Assert.Equal(2, RouteStore.CommittedRoutes.Count);
                Assert.Single(RouteStore.DormantRoutes);
                Assert.Equal(2, RouteStore.DismissedCandidateTreeIds.Count);
                Assert.Equal(2, RouteStore.PromptedCandidateTreeIds.Count);
            });
        }

        [Fact]
        public void Harness_DetectsAFieldTheStoreDrops()
        {
            var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: 30000);
            Populate(rt);
            var written = StaticFieldRule.Snapshot(typeof(RouteStore), Statics);
            var parsek = new ConfigNode("PARSEK");
            RouteStore.SaveRoutesTo(parsek);
            parsek.RemoveNodes("DORMANT_ROUTES");
            RouteStore.ResetForTesting();
            RouteStore.LoadRoutesFrom(parsek);
            var problems = StaticFieldRule.Diff(rt, typeof(RouteStore), Statics, written,
                StaticFieldRule.Snapshot(typeof(RouteStore), Statics));
            Assert.Contains(problems, p => p.StartsWith("RouteStore.dormantRoutes:", StringComparison.Ordinal));
        }
    }
}
