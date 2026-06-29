using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Unit tests for the pure Rec-1 retire predicate + list filter
    /// (<see cref="RouteLedgerRetire"/>). No shared static state is touched, so this
    /// is a plain class (no <c>[Collection("Sequential")]</c>).
    ///
    /// Plan: <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c> Phase 1.
    /// </summary>
    public class RouteLedgerRetireTests
    {
        private static readonly GameActionType[] RouteTypes =
        {
            GameActionType.RouteDispatched,        // 23
            GameActionType.RouteCargoDebited,      // 24
            GameActionType.RouteCargoDelivered,    // 25
            GameActionType.RoutePaused,            // 26
            GameActionType.RouteEndpointLost,      // 27
            GameActionType.RouteRecoveryCredited,  // 28
            GameActionType.RouteCargoPickedUp,     // 29
        };

        private static readonly GameActionType[] NonRouteTypes =
        {
            GameActionType.FundsEarning,
            GameActionType.MilestoneAchievement,
            GameActionType.ContractComplete,
            GameActionType.FundsInitial,
        };

        private static GameAction Route(GameActionType t, double ut, string routeId = "route-1")
            => new GameAction { Type = t, UT = ut, RouteId = routeId };

        // ---- IsRouteActionType ----

        [Fact]
        public void IsRouteActionType_TrueForEverySevenRouteType()
        {
            foreach (var t in RouteTypes)
                Assert.True(RouteLedgerRetire.IsRouteActionType(t), $"{t} should be a route type");
        }

        [Fact]
        public void IsRouteActionType_FalseForNonRouteTypes()
        {
            foreach (var t in NonRouteTypes)
                Assert.False(RouteLedgerRetire.IsRouteActionType(t), $"{t} must NOT be a route type");
        }

        // ---- ShouldRetireRouteActionAtRewind: boundary + Type + RouteId gating ----

        [Fact]
        public void ShouldRetire_FutureRouteRow_True_ForEveryRouteType()
        {
            foreach (var t in RouteTypes)
                Assert.True(RouteLedgerRetire.ShouldRetireRouteActionAtRewind(Route(t, 3000.0), 2500.0),
                    $"{t} at UT 3000 > cutoff 2500 should retire");
        }

        [Fact]
        public void ShouldRetire_AtCutoff_Kept_StrictGreaterThan()
        {
            // Strict > : a row stamped exactly at the cutoff is at/before RP and is KEPT
            // (its physical effect is in the quicksave; see the predicate XML doc).
            foreach (var t in RouteTypes)
                Assert.False(RouteLedgerRetire.ShouldRetireRouteActionAtRewind(Route(t, 2500.0), 2500.0),
                    $"{t} exactly at cutoff must be kept (strict >)");
        }

        [Fact]
        public void ShouldRetire_BeforeCutoff_Kept()
        {
            foreach (var t in RouteTypes)
                Assert.False(RouteLedgerRetire.ShouldRetireRouteActionAtRewind(Route(t, 1000.0), 2500.0),
                    $"{t} before cutoff must be kept");
        }

        [Fact]
        public void ShouldRetire_NonRouteRow_NeverRetired_RegardlessOfUT()
        {
            foreach (var t in NonRouteTypes)
            {
                var a = new GameAction { Type = t, UT = 9000.0 };
                Assert.False(RouteLedgerRetire.ShouldRetireRouteActionAtRewind(a, 2500.0),
                    $"non-route {t} must never retire");
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ShouldRetire_RouteTypeWithEmptyRouteId_NotRetired(string routeId)
        {
            // Belt-and-suspenders: a route Type with no RouteId is not a genuine
            // free-standing route row and is left alone.
            var a = new GameAction { Type = GameActionType.RouteDispatched, UT = 3000.0, RouteId = routeId };
            Assert.False(RouteLedgerRetire.ShouldRetireRouteActionAtRewind(a, 2500.0));
        }

        [Fact]
        public void ShouldRetire_NullAction_False()
        {
            Assert.False(RouteLedgerRetire.ShouldRetireRouteActionAtRewind(null, 2500.0));
        }

        // ---- RetireFutureRouteActions: order, count, +inf, interleaving ----

        [Fact]
        public void Retire_DropsFutureRouteRows_KeepsPastAndNonRoute_PreservesOrder()
        {
            var src = new List<GameAction>
            {
                new GameAction { Type = GameActionType.FundsInitial, UT = 0.0 },        // keep (non-route)
                Route(GameActionType.RouteCargoDebited, 2000.0),                         // keep (<= cutoff)
                new GameAction { Type = GameActionType.FundsEarning, UT = 3000.0 },      // keep (non-route, future)
                Route(GameActionType.RouteDispatched, 3000.0),                           // drop (> cutoff)
                Route(GameActionType.RouteCargoDelivered, 3000.0),                       // drop (> cutoff)
                Route(GameActionType.RouteRecoveryCredited, 3500.0),                     // drop (> cutoff)
            };

            var kept = RouteLedgerRetire.RetireFutureRouteActions(src, 2500.0, out int retired);

            Assert.Equal(3, retired);
            Assert.Equal(3, kept.Count);
            // order preserved, route-future rows removed, non-route + past kept
            Assert.Equal(GameActionType.FundsInitial, kept[0].Type);
            Assert.Equal(GameActionType.RouteCargoDebited, kept[1].Type);
            Assert.Equal(GameActionType.FundsEarning, kept[2].Type);
        }

        [Fact]
        public void Retire_PositiveInfinityCutoff_RetiresNothing()
        {
            var src = new List<GameAction>
            {
                Route(GameActionType.RouteDispatched, 1e9),
                Route(GameActionType.RouteCargoDelivered, 1e12),
                new GameAction { Type = GameActionType.FundsEarning, UT = 5.0 },
            };

            var kept = RouteLedgerRetire.RetireFutureRouteActions(src, double.PositiveInfinity, out int retired);

            Assert.Equal(0, retired);
            Assert.Equal(src.Count, kept.Count);
        }

        [Fact]
        public void Retire_NullSource_EmptyList_NoThrow()
        {
            var kept = RouteLedgerRetire.RetireFutureRouteActions(null, 100.0, out int retired);
            Assert.Empty(kept);
            Assert.Equal(0, retired);
        }
    }
}
