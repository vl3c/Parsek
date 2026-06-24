using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // The resolver's per-member cache is keyed per-(member, window) and resolves ONE window per frame from
    // the live clock (cadence is a whole synodic multiple, so each relaunch lands on a real window and the
    // mission fits one cadence - a single instance, no overlap). These tests pin the LIVE currentUT path:
    // the (member, window) keying, the per-window caching ("solve once per window"), the unsupported /
    // no-heliocentric-leg fail-closed paths, and Clear().
    //
    // BuildWindowSegments is Unity-bound (FlightGlobals.Bodies + live orbit reconstruction), so it cannot
    // run headless. The resolver exposes BuilderOverrideForTesting (null in production; the live path is
    // byte-identical) which these tests set to a pure synthetic builder. That lets us exercise the cache
    // logic + tuple key without Unity, all through the live TryResolveWindowSegments(currentUT) entry point.
    public class ReaimPlaybackResolverTests
    {
        // Stock-ish Kerbin->Duna numbers so the pure ReaimWindowPlanner produces a valid schedule.
        private const double KerbinPeriod = 9203545.0, DunaPeriod = 17315400.0;
        private const double SpanStart = 1000.0, SpanEnd = 5000.0;
        private const double RecordedDeparture = 2000.0, RecordedArrival = 5000.0, RecordedTof = 3000.0;
        private const string CommonAncestor = "Sun";

        private static ReaimWindowPlanner.ReaimWindowSchedule PlanSchedule(double referenceUT = 100_000.0)
        {
            return ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, RecordedDeparture, RecordedTof, SpanStart, SpanEnd, referenceUT);
        }

        // A Supported plan with a heliocentric (common-ancestor) leg so HasHeliocentricLegInWindow passes.
        private static ReaimMissionPlan SupportedPlan()
        {
            return new ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = "Kerbin",
                TargetBody = "Duna",
                CommonAncestor = CommonAncestor,
                RecordedDepartureUT = RecordedDeparture,
                RecordedArrivalUT = RecordedArrival,
                RecordedTransferTofSeconds = RecordedTof,
            };
        }

        // One non-predicted common-ancestor segment that spans the transfer window (satisfies the
        // HasHeliocentricLegInWindow pre-check the live path runs before touching the cache).
        private static List<OrbitSegment> HeliocentricMemberSegments()
        {
            return new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = RecordedDeparture - 100.0,
                    endUT = RecordedArrival + 100.0,
                    bodyName = CommonAncestor,
                    isPredicted = false,
                    semiMajorAxis = 1.5e10,
                },
            };
        }

        // A pure synthetic builder: produces a distinct single-segment list per window (sma encodes the
        // window) so two windows are provably DISTINCT geometry, and counts how often the real build ran
        // per window (proving cache hits vs misses). captureShift left 0.
        private sealed class FakeBuilder
        {
            public readonly Dictionary<long, int> BuildCountByWindow = new Dictionary<long, int>();
            public readonly List<long> BuiltWindows = new List<long>();

            public List<OrbitSegment> Build(
                string memberId, IReadOnlyList<OrbitSegment> memberSegments, ReaimMissionPlan plan,
                ReaimWindowPlanner.ReaimWindowSchedule schedule, long window, out double captureShiftSeconds)
            {
                captureShiftSeconds = 0.0;
                BuiltWindows.Add(window);
                BuildCountByWindow.TryGetValue(window, out int n);
                BuildCountByWindow[window] = n + 1;
                // Distinct geometry per window: encode the window in the sma so two windows never compare equal.
                return new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = RecordedDeparture,
                        endUT = RecordedArrival,
                        bodyName = CommonAncestor,
                        isPredicted = false,
                        semiMajorAxis = 1.0e10 + window,
                    },
                };
            }
        }

        private static ReaimPlaybackResolver NewResolverWith(FakeBuilder fake)
        {
            var r = new ReaimPlaybackResolver();
            r.BuilderOverrideForTesting = fake.Build;
            return r;
        }

        // The currentUT path returns the builder's segments and an out windowIndex consistent with the
        // window the builder was asked for (the live derivation -> caching path resolves one window).
        [Fact]
        public void CurrentUtPath_ReturnsBuiltSegments_AndConsistentWindow()
        {
            var schedule = PlanSchedule();
            Assert.True(schedule.Valid, schedule.Reason);
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);

            // A UT well inside the first window's replay so TryComputeSpanLoopUT resolves a window >= 0.
            double currentUT = schedule.FirstDepartureUT + 10.0;
            bool ok = resolver.TryResolveWindowSegments(
                "mA", HeliocentricMemberSegments(), SupportedPlan(), schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out List<OrbitSegment> segments, out long windowIndex);

            Assert.True(ok);
            Assert.NotNull(segments);
            Assert.Single(fake.BuiltWindows);
            // The builder was asked for exactly the resolved window (out-param matches the build request).
            Assert.Equal(windowIndex, fake.BuiltWindows[0]);
            // Segments are the builder's output (sma encodes the window).
            Assert.Single(segments);
            Assert.Equal(1.0e10 + windowIndex, segments[0].semiMajorAxis, 6);
        }

        // The same window resolves from the cache on a second frame (no rebuild) - the (member, window)
        // key preserves the "solve once per window" caching semantics on the live path.
        [Fact]
        public void CurrentUtPath_CachesPerWindow_NoRebuildSameWindow()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            double currentUT = schedule.FirstDepartureUT + 10.0;

            resolver.TryResolveWindowSegments(
                "mA", HeliocentricMemberSegments(), SupportedPlan(), schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out _, out long w0);
            // Second frame, same window.
            resolver.TryResolveWindowSegments(
                "mA", HeliocentricMemberSegments(), SupportedPlan(), schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT + 5.0,
                out _, out long w1);

            Assert.Equal(w0, w1);
            Assert.Equal(1, fake.BuildCountByWindow[w0]); // built exactly once across the two frames
        }

        // Advancing the live clock by one cadence resolves the NEXT window with DISTINCT geometry and a
        // fresh build (the per-window key does not collide with the prior window).
        [Fact]
        public void CurrentUtPath_AdvancingWindow_ResolvesNextWindow_DistinctGeometry()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();

            double utK = schedule.FirstDepartureUT + 10.0;
            Assert.True(resolver.TryResolveWindowSegments(
                "mA", HeliocentricMemberSegments(), plan, schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, utK,
                out List<OrbitSegment> segK, out long wK));

            // One cadence later -> the next window.
            double utK1 = utK + schedule.CadenceSeconds;
            Assert.True(resolver.TryResolveWindowSegments(
                "mA", HeliocentricMemberSegments(), plan, schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, utK1,
                out List<OrbitSegment> segK1, out long wK1));

            Assert.Equal(wK + 1, wK1);
            Assert.NotEqual(segK[0].semiMajorAxis, segK1[0].semiMajorAxis);
            Assert.Equal(1.0e10 + wK, segK[0].semiMajorAxis, 6);
            Assert.Equal(1.0e10 + wK1, segK1[0].semiMajorAxis, 6);
        }

        // Unsupported plan / invalid schedule -> false (faithful), no build, on the live path.
        [Fact]
        public void UnsupportedOrInvalid_ReturnsFalse_NoBuild()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var segs = HeliocentricMemberSegments();
            double currentUT = schedule.FirstDepartureUT + 10.0;

            var unsupported = ReaimMissionPlan.Unsupported("Kerbin", "test");
            Assert.False(resolver.TryResolveWindowSegments(
                "mA", segs, unsupported, schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out _, out _));

            var invalidSchedule = default(ReaimWindowPlanner.ReaimWindowSchedule); // Valid == false
            Assert.False(resolver.TryResolveWindowSegments(
                "mA", segs, SupportedPlan(), invalidSchedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out _, out _));

            Assert.Empty(fake.BuiltWindows);
        }

        // A member with no heliocentric leg -> the pre-check returns false before any build on the live path.
        [Fact]
        public void NoHeliocentricLeg_ReturnsFalse_BeforeBuild()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            double currentUT = schedule.FirstDepartureUT + 10.0;
            // A body-relative-only member (wrong body) has no common-ancestor leg in the window.
            var noHelio = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = RecordedDeparture, endUT = RecordedArrival, bodyName = "Kerbin", isPredicted = false },
            };

            Assert.False(resolver.TryResolveWindowSegments(
                "mA", noHelio, plan, schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out _, out _));
            Assert.Empty(fake.BuiltWindows);
        }

        // Clear() drops all cached windows -> the next resolve rebuilds (a recording edit must not survive).
        [Fact]
        public void Clear_DropsCachedWindow_NextResolveRebuilds()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            double currentUT = schedule.FirstDepartureUT + 10.0;

            Assert.True(resolver.TryResolveWindowSegments(
                "mA", HeliocentricMemberSegments(), plan, schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out _, out long w));
            Assert.Equal(1, fake.BuildCountByWindow[w]);

            resolver.Clear();
            Assert.True(resolver.TryResolveWindowSegments(
                "mA", HeliocentricMemberSegments(), plan, schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out _, out long w2));
            Assert.Equal(w, w2);
            Assert.Equal(2, fake.BuildCountByWindow[w]); // rebuilt after Clear
        }
    }
}
