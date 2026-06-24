using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// M4c: the round-trip link/unlink store mutators (<see cref="RouteStore.LinkRoutes"/>,
    /// <see cref="RouteStore.UnlinkRoute"/>) and the dangling-partner cleanup added to
    /// <see cref="RouteStore.RemoveRoute"/>. These back the detail-panel "Link
    /// round-trip..." / "Unlink" controls; the invariants they enforce (bidirectional
    /// link, cursor reset to the clean seed state, no dangling back-reference, no
    /// self/3-way link) are what the dispatch partner gate and the deadlock seed assume.
    /// </summary>
    [Collection("Sequential")]
    public class RouteLinkMutatorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteLinkMutatorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Route Add(string id, string name = null, string linkedTo = null, int cursor = 0)
        {
            Route r = new RouteFixtureBuilder()
                .WithId(id)
                .WithName(name ?? id)
                .WithLinkedRouteId(linkedTo)
                .WithLastConsumedPartnerCycle(cursor)
                .Build();
            RouteStore.AddRoute(r);
            return r;
        }

        // ==================================================================
        // LinkRoutes
        // ==================================================================

        [Fact]
        public void LinkRoutes_SetsBothDirections_AndResetsCursors()
        {
            Route a = Add("a");
            Route b = Add("b");

            bool ok = RouteStore.LinkRoutes("a", "b");

            Assert.True(ok);
            Assert.Equal("b", a.LinkedRouteId);
            Assert.Equal("a", b.LinkedRouteId);
            Assert.Equal(0, a.LastConsumedPartnerCycle);
            Assert.Equal(0, b.LastConsumedPartnerCycle);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("linked as round-trip"));
        }

        [Fact]
        public void LinkRoutes_ResetsNonZeroCursors_OnRelink()
        {
            // A re-link of routes that previously alternated must zero both cursors so
            // the deadlock seed governs the fresh cold start (a stale cursor would
            // either deadlock or double-dispatch).
            Add("a", cursor: 5);
            Add("b", cursor: 3);

            Assert.True(RouteStore.LinkRoutes("a", "b"));

            Assert.True(RouteStore.TryGetRoute("a", out Route a));
            Assert.True(RouteStore.TryGetRoute("b", out Route b));
            Assert.Equal(0, a.LastConsumedPartnerCycle);
            Assert.Equal(0, b.LastConsumedPartnerCycle);
        }

        [Fact]
        public void LinkRoutes_SelfLink_Rejected()
        {
            Route a = Add("a");

            bool ok = RouteStore.LinkRoutes("a", "a");

            Assert.False(ok);
            Assert.Null(a.LinkedRouteId);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("to itself"));
        }

        [Theory]
        [InlineData(null, "b")]
        [InlineData("", "b")]
        [InlineData("a", null)]
        [InlineData("a", "")]
        public void LinkRoutes_NullOrEmptyId_Rejected(string idA, string idB)
        {
            Add("a");
            Add("b");

            bool ok = RouteStore.LinkRoutes(idA, idB);

            Assert.False(ok);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("null or empty"));
        }

        [Fact]
        public void LinkRoutes_UnknownId_Rejected()
        {
            Add("a");

            bool ok = RouteStore.LinkRoutes("a", "ghost");

            Assert.False(ok);
            Assert.True(RouteStore.TryGetRoute("a", out Route a));
            Assert.Null(a.LinkedRouteId);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("not found"));
        }

        [Fact]
        public void LinkRoutes_AlreadyLinkedToDifferentRoute_Rejected()
        {
            Add("a");
            Add("b");
            Add("c");
            Assert.True(RouteStore.LinkRoutes("a", "b")); // a <-> b
            logLines.Clear();

            // Linking c with a (already paired to b) must be rejected, not steal a.
            bool ok = RouteStore.LinkRoutes("c", "a");

            Assert.False(ok);
            Assert.True(RouteStore.TryGetRoute("a", out Route a));
            Assert.True(RouteStore.TryGetRoute("b", out Route b));
            Assert.True(RouteStore.TryGetRoute("c", out Route c));
            Assert.Equal("b", a.LinkedRouteId); // unchanged
            Assert.Equal("a", b.LinkedRouteId);
            Assert.Null(c.LinkedRouteId);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("already linked to a different route"));
        }

        [Fact]
        public void LinkRoutes_RelinkSamePair_Idempotent_PreservesLiveCursors()
        {
            Route a = Add("a");
            Route b = Add("b");
            Assert.True(RouteStore.LinkRoutes("a", "b"));
            // Simulate the pair having alternated for a while.
            a.LastConsumedPartnerCycle = 5;
            b.LastConsumedPartnerCycle = 4;

            // Re-linking the SAME already-correct pair (in either order) is an
            // idempotent success that must NOT zero the live alternation cursors
            // (that would reintroduce a one-cycle double-dispatch).
            Assert.True(RouteStore.LinkRoutes("a", "b"));
            Assert.True(RouteStore.LinkRoutes("b", "a"));

            Assert.Equal("b", a.LinkedRouteId);
            Assert.Equal("a", b.LinkedRouteId);
            Assert.Equal(5, a.LastConsumedPartnerCycle); // preserved, not zeroed
            Assert.Equal(4, b.LastConsumedPartnerCycle);
        }

        [Fact]
        public void LinkRoutes_RepairsHalfLink_AndResetsCursors()
        {
            // An inconsistent half-link (a -> b, but b -> null) is NOT the "already
            // correct" no-op case: LinkRoutes must repair it to a full bidirectional
            // link and reset both cursors (a genuine link transition).
            Route a = Add("a", linkedTo: "b", cursor: 9);
            Route b = Add("b"); // b.LinkedRouteId == null (half-link)

            Assert.True(RouteStore.LinkRoutes("a", "b"));

            Assert.Equal("b", a.LinkedRouteId);
            Assert.Equal("a", b.LinkedRouteId); // back-reference repaired
            Assert.Equal(0, a.LastConsumedPartnerCycle); // reset on the genuine transition
            Assert.Equal(0, b.LastConsumedPartnerCycle);
        }

        // ==================================================================
        // UnlinkRoute
        // ==================================================================

        [Fact]
        public void UnlinkRoute_ClearsBothAndResetsCursors()
        {
            Route a = Add("a");
            Route b = Add("b");
            Assert.True(RouteStore.LinkRoutes("a", "b"));
            // Simulate alternation having advanced before the unlink.
            a.LastConsumedPartnerCycle = 4;
            b.LastConsumedPartnerCycle = 4;
            logLines.Clear();

            bool ok = RouteStore.UnlinkRoute("a");

            Assert.True(ok);
            Assert.Null(a.LinkedRouteId);
            Assert.Null(b.LinkedRouteId);
            Assert.Equal(0, a.LastConsumedPartnerCycle);
            Assert.Equal(0, b.LastConsumedPartnerCycle);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("unlinked"));
        }

        [Fact]
        public void UnlinkRoute_NotLinked_NoOp()
        {
            Route a = Add("a");

            bool ok = RouteStore.UnlinkRoute("a");

            Assert.False(ok);
            Assert.Null(a.LinkedRouteId);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("not linked"));
        }

        [Fact]
        public void UnlinkRoute_PartnerPointsElsewhere_ClearsSelfOnly()
        {
            // A half-link / inconsistent state: a -> b, but b -> c. Unlinking a must
            // clear a only and must NOT clobber b's link to c.
            Route a = Add("a", linkedTo: "b");
            Route b = Add("b", linkedTo: "c");
            Add("c", linkedTo: "b");

            bool ok = RouteStore.UnlinkRoute("a");

            Assert.True(ok);
            Assert.Null(a.LinkedRouteId);
            Assert.Equal("c", b.LinkedRouteId); // untouched
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("did not point back"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void UnlinkRoute_NullOrEmpty_Rejected(string id)
        {
            bool ok = RouteStore.UnlinkRoute(id);
            Assert.False(ok);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("null or empty"));
        }

        [Fact]
        public void UnlinkRoute_UnknownId_Rejected()
        {
            bool ok = RouteStore.UnlinkRoute("ghost");
            Assert.False(ok);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("not found"));
        }

        // ==================================================================
        // RemoveRoute dangling-partner cleanup
        // ==================================================================

        [Fact]
        public void RemoveRoute_ClearsDanglingPartnerLink()
        {
            Route a = Add("a");
            Add("b");
            Assert.True(RouteStore.LinkRoutes("a", "b"));
            a.LastConsumedPartnerCycle = 2; // pretend it ran
            logLines.Clear();

            Assert.True(RouteStore.RemoveRoute("b"));

            // A's back-reference to the now-removed B must be cleared and its cursor
            // reset, so the codec never persists a dangling link.
            Assert.Null(a.LinkedRouteId);
            Assert.Equal(0, a.LastConsumedPartnerCycle);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("partner link"));
        }

        [Fact]
        public void RemoveRoute_StandalonePartner_SerializesSparse_ByteIdenticalToNeverLinked()
        {
            // After a linked route is removed, the surviving partner must serialize
            // EXACTLY like a route that was never linked: linkedRouteId and
            // lastConsumedPartnerCycle both sparse-omitted. Pins the contract that the
            // RemoveRoute cursor-reset yields a clean standalone shape (a future codec
            // change that started writing lastConsumedPartnerCycle=0 would fail here).
            Route a = Add("a");
            Add("b");
            Assert.True(RouteStore.LinkRoutes("a", "b"));
            a.LastConsumedPartnerCycle = 7; // pretend it alternated before B was deleted

            Assert.True(RouteStore.RemoveRoute("b"));

            var node = new ConfigNode("ROUTE");
            RouteCodec.SerializeInto(a, node);
            Assert.Null(node.GetValue("linkedRouteId"));
            Assert.Null(node.GetValue("lastConsumedPartnerCycle"));

            // And a genuinely never-linked route serializes the same way (control).
            Route fresh = new RouteFixtureBuilder().WithId("fresh").Build();
            var freshNode = new ConfigNode("ROUTE");
            RouteCodec.SerializeInto(fresh, freshNode);
            Assert.Null(freshNode.GetValue("linkedRouteId"));
            Assert.Null(freshNode.GetValue("lastConsumedPartnerCycle"));
        }

        [Fact]
        public void RemoveRoute_Unlinked_NoPartnerCleanupNoise()
        {
            Add("a");
            Add("b"); // never linked

            Assert.True(RouteStore.RemoveRoute("b"));

            Assert.True(RouteStore.TryGetRoute("a", out Route a));
            Assert.Null(a.LinkedRouteId);
            // The "cleared N partner link(s)" suffix must NOT appear for an unlinked
            // removal.
            Assert.DoesNotContain(logLines, l => l.Contains("[Route]") && l.Contains("partner link"));
        }
    }
}
