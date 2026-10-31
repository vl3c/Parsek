using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of <see cref="RetiredRouteCargoStore"/> (the
    /// RETIRED_ROUTE_CARGO node): every field of its rows, their snapshot tags, the watermarks
    /// and the snapshot captures, for each of the three cargo row types the store keeps. The
    /// store's static state is enumerated, so a new static field reds
    /// <see cref="Store_EveryStaticFieldIsClassified"/> until it is persisted or classified.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(RetiredRouteCargoStore))]
    public class RetiredRouteCargoSaveRoundTripTests : IDisposable
    {
        public RetiredRouteCargoSaveRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RetiredRouteCargoStore.ResetForTesting();
        }

        public void Dispose()
        {
            RetiredRouteCargoStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        internal static readonly Dictionary<string, StaticFieldRule> Statics =
            new Dictionary<string, StaticFieldRule>(StringComparer.Ordinal)
            {
                ["rows"] = StaticFieldRule.Persisted(),
                ["watermarks"] = StaticFieldRule.Persisted(),
                ["captures"] = StaticFieldRule.Persisted(),
                ["IC"] = StaticFieldRule.Constant(),
            };

        // Only the three cargo-moving event types are kept (IsCargoRowType); load drops any other.
        internal static FieldSpec[] Specs() => new[]
        {
            new FieldSpec(typeof(RetiredRouteCargoRow))
                .Fill("Type", () => RouteEventType.RouteCargoDelivered),
        };

        public static IEnumerable<object[]> CargoTypes() =>
            Enum.GetValues(typeof(RouteEventType)).Cast<RouteEventType>()
                .Where(RetiredRouteCargoStore.IsCargoRowType)
                .Select(t => new object[] { (int)t });

        /// <summary>Fills the store's own lists with two of each entry, rows of <paramref name="type"/>.</summary>
        internal static void Populate(FieldRoundTrip rt, RouteEventType type)
        {
            var lists = StaticFieldRule.StateFields(typeof(RetiredRouteCargoStore))
                .ToDictionary(FieldRoundTrip.LogicalName, f => f.GetValue(null));
            for (int i = 0; i < 2; i++)
            {
                var row = rt.Build<RetiredRouteCargoRow>();
                row.Type = type;
                ((List<RetiredRouteCargoRow>)lists["rows"]).Add(row);
                ((List<RetiredRouteCargoWatermark>)lists["watermarks"]).Add(rt.Build<RetiredRouteCargoWatermark>());
                ((List<RetiredRouteCargoSnapshotCapture>)lists["captures"]).Add(rt.Build<RetiredRouteCargoSnapshotCapture>());
            }
        }

        [Fact]
        public void Store_EveryStaticFieldIsClassified()
        {
            StaticFieldRule.AssertClassified(typeof(RetiredRouteCargoStore), Statics);
        }

        [Fact]
        public void CargoTypes_AreTheThreeCargoMovingKinds()
        {
            Assert.Equal(3, CargoTypes().Count());
        }

        [Theory]
        [MemberData(nameof(CargoTypes))]
        public void Store_EveryFieldRoundTrips(int typeValue)
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(Specs(), seed: typeValue * 100);
                Populate(rt, (RouteEventType)typeValue);
                var written = StaticFieldRule.Snapshot(typeof(RetiredRouteCargoStore), Statics);

                var parsek = new ConfigNode("PARSEK");
                RetiredRouteCargoStore.SerializeInto(parsek);
                RetiredRouteCargoStore.ResetForTesting();
                int malformed = RetiredRouteCargoStore.LoadFrom(parsek);
                var read = StaticFieldRule.Snapshot(typeof(RetiredRouteCargoStore), Statics);

                Assert.Equal(0, malformed);
                Assert.NotEmpty(RetiredRouteCargoStore.Rows[0].Tips);
                Assert.NotEmpty(RetiredRouteCargoStore.Rows[0].Resources);
                var problems = StaticFieldRule.Diff(rt, typeof(RetiredRouteCargoStore), Statics, written, read);
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RetiredRouteCargoStore", problems));
            });
        }

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            var rt = new FieldRoundTrip(Specs(), seed: 5);
            Populate(rt, RouteEventType.RouteCargoPickedUp);
            var written = StaticFieldRule.Snapshot(typeof(RetiredRouteCargoStore), Statics);
            var parsek = new ConfigNode("PARSEK");
            RetiredRouteCargoStore.SerializeInto(parsek);
            foreach (ConfigNode row in parsek.GetNode(RetiredRouteCargoStore.NodeName).GetNodes("ROW"))
                row.RemoveValue("endpointRootUid");
            RetiredRouteCargoStore.ResetForTesting();
            RetiredRouteCargoStore.LoadFrom(parsek);
            var read = StaticFieldRule.Snapshot(typeof(RetiredRouteCargoStore), Statics);
            var problems = StaticFieldRule.Diff(rt, typeof(RetiredRouteCargoStore), Statics, written, read);
            Assert.Contains(problems, p => p.StartsWith("RetiredRouteCargoStore.rows[0].EndpointRootPartUId:", StringComparison.Ordinal));
        }
    }
}
