using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Per-value matrix for <see cref="RouteStatusPolicy.BindsTree"/> and
    /// <see cref="RouteStatusPolicy.GhostDriving"/>. Both predicates are
    /// pinned exhaustively so an enum append fails loudly: the count pin trips,
    /// and any unclassified value throws via the switch default arm.
    /// </summary>
    /// <remarks>
    /// Plan: Phase 0 task 1 / Tests. RouteStatusPolicy is pure (no Unity, no
    /// shared static state), so this class does not strictly need the
    /// Sequential collection, but it is kept consistent with the other route
    /// status tests.
    ///
    /// Theory rows pass enum values as <c>int</c> ordinals, not as
    /// <see cref="RouteStatus"/> directly: the enum is <c>internal</c> and an
    /// xUnit test method must be <c>public</c>, which forbids an internal
    /// parameter type (CS0051). The ordinal is cast back inside the test, and
    /// <see cref="RouteStatusEnumTests"/> pins each ordinal-to-name mapping.
    /// </remarks>
    [Collection("Sequential")]
    public class RouteStatusPolicyTests
    {
        // Catches: an enum append that nobody classified in the policy table.
        // The per-value Theory rows below each name an explicit ordinal, so
        // they cannot drift to cover a newly added value; this count pin is the
        // tripwire that forces the new value into both matrices.
        [Fact]
        public void EnumValueCount_IsNine_SoAppendsTripThePinnedMatrices()
        {
            int count = Enum.GetValues(typeof(RouteStatus)).Length;
            Assert.Equal(9, count);
        }

        // BindsTree is TRUE for ALL 9 values: a route binds its tree until
        // explicitly removed (broken routes still bind, preventing the
        // self-heal double-owner collision).
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.InTransit)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        [InlineData((int)RouteStatus.EndpointLost)]
        [InlineData((int)RouteStatus.MissingSourceRecording)]
        [InlineData((int)RouteStatus.SourceChanged)]
        [InlineData((int)RouteStatus.Paused)]
        public void BindsTree_IsTrue_ForEveryStatus(int statusOrdinal)
        {
            var status = (RouteStatus)statusOrdinal;
            Assert.True(RouteStatusPolicy.BindsTree(status));
        }

        // Belt-and-suspenders: every enumerated value binds, computed over the
        // live enum set rather than a hand-list (so the count of TRUEs equals
        // the count of values).
        [Fact]
        public void BindsTree_IsTrue_ForAllEnumeratedValues()
        {
            var values = Enum.GetValues(typeof(RouteStatus)).Cast<RouteStatus>().ToArray();
            Assert.All(values, s => Assert.True(RouteStatusPolicy.BindsTree(s)));
        }

        // GhostDriving is TRUE only for the five live-render states.
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.InTransit)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        public void GhostDriving_IsTrue_ForLiveRenderStates(int statusOrdinal)
        {
            var status = (RouteStatus)statusOrdinal;
            Assert.True(RouteStatusPolicy.GhostDriving(status));
        }

        // GhostDriving is FALSE for Paused and the three broken states.
        [Theory]
        [InlineData((int)RouteStatus.Paused)]
        [InlineData((int)RouteStatus.EndpointLost)]
        [InlineData((int)RouteStatus.MissingSourceRecording)]
        [InlineData((int)RouteStatus.SourceChanged)]
        public void GhostDriving_IsFalse_ForPausedAndBrokenStates(int statusOrdinal)
        {
            var status = (RouteStatus)statusOrdinal;
            Assert.False(RouteStatusPolicy.GhostDriving(status));
        }

        // Full-matrix snapshot of the two predicates, so the exact partition is
        // documented in one place and any reclassification is a visible diff.
        [Fact]
        public void FullMatrix_PinsBothPredicates()
        {
            var expectedGhostDriving = new Dictionary<RouteStatus, bool>
            {
                { RouteStatus.Active, true },
                { RouteStatus.InTransit, true },
                { RouteStatus.WaitingForResources, true },
                { RouteStatus.WaitingForFunds, true },
                { RouteStatus.DestinationFull, true },
                { RouteStatus.EndpointLost, false },
                { RouteStatus.MissingSourceRecording, false },
                { RouteStatus.SourceChanged, false },
                { RouteStatus.Paused, false },
            };

            var values = Enum.GetValues(typeof(RouteStatus)).Cast<RouteStatus>().ToArray();

            foreach (var s in values)
            {
                Assert.True(RouteStatusPolicy.BindsTree(s), $"BindsTree({s}) should be TRUE");
                Assert.True(
                    expectedGhostDriving.ContainsKey(s),
                    $"RouteStatus.{s} is not pinned in the GhostDriving matrix");
                Assert.Equal(expectedGhostDriving[s], RouteStatusPolicy.GhostDriving(s));
            }

            // Every pinned key is a real enum value (catches a stale matrix row).
            foreach (var key in expectedGhostDriving.Keys)
            {
                Assert.Contains(key, values);
            }
        }
    }
}
