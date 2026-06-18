using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Sub-PR 2 of the Approach-A self-overlap milestone: the resolver's per-member cache was re-keyed to
    // per-(member, window) so two concurrent windows (k and k+1) can be cached at once for overlapping
    // ghost instances, plus a new explicit-window overload (TryResolveWindowSegmentsForWindow) and a
    // bounded eviction band. These tests pin the cache keying / eviction / window-derivation contract.
    //
    // BuildWindowSegments is Unity-bound (FlightGlobals.Bodies + live orbit reconstruction), so it cannot
    // run headless. The resolver exposes BuilderOverrideForTesting (null in production; the live path is
    // byte-identical) which these tests set to a pure synthetic builder. That lets us exercise the cache
    // logic, the tuple key, the two-window concurrency, and the eviction band without Unity, and to pin
    // that the currentUT overload and the explicit-window overload resolve the SAME window/segments.
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
        // HasHeliocentricLegInWindow pre-check both overloads run before touching the cache).
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

        // The currentUT overload returns the builder's segments and an out windowIndex consistent with the
        // window the builder was asked for (the re-key did not change the live derivation -> caching path).
        [Fact]
        public void CurrentUtOverload_ReturnsBuiltSegments_AndConsistentWindow()
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

        // The same window resolves from the cache on a second frame (no rebuild) - the re-key preserves the
        // "solve once per window" caching semantics of the live path.
        [Fact]
        public void CurrentUtOverload_CachesPerWindow_NoRebuildSameWindow()
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

        // The PURPOSE of the re-key: window k AND window k+1 are cached CONCURRENTLY with DISTINCT geometry.
        // The old single-slot-per-member cache could not hold both at once (advancing the window evicted k).
        [Fact]
        public void ExplicitWindowOverload_HoldsTwoWindowsConcurrently_DistinctGeometry()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            var segs = HeliocentricMemberSegments();

            const long k = 3;
            Assert.True(resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, k, out var sk));
            Assert.True(resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, k + 1, out var sk1));

            // Distinct geometry (sma encodes the window).
            Assert.NotEqual(sk[0].semiMajorAxis, sk1[0].semiMajorAxis);
            Assert.Equal(1.0e10 + k, sk[0].semiMajorAxis, 6);
            Assert.Equal(1.0e10 + (k + 1), sk1[0].semiMajorAxis, 6);

            // BOTH windows are still cached: re-resolving each is a cache hit (no extra build).
            Assert.True(resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, k, out var skAgain));
            Assert.True(resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, k + 1, out _));
            Assert.Equal(1, fake.BuildCountByWindow[k]);
            Assert.Equal(1, fake.BuildCountByWindow[k + 1]);
            Assert.Equal(1.0e10 + k, skAgain[0].semiMajorAxis, 6);
        }

        // The explicit-window overload at window k returns the SAME segments the currentUT overload returns
        // at a currentUT that maps to window k (the two overloads share the build/cache core).
        [Fact]
        public void ExplicitWindow_MatchesCurrentUt_ForTheSameWindow()
        {
            var schedule = PlanSchedule();
            var plan = SupportedPlan();
            var segs = HeliocentricMemberSegments();

            // First learn which window the currentUT path derives at this UT.
            var fakeA = new FakeBuilder();
            var resolverA = NewResolverWith(fakeA);
            double currentUT = schedule.FirstDepartureUT + 10.0;
            Assert.True(resolverA.TryResolveWindowSegments(
                "mA", segs, plan, schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds, currentUT,
                out List<OrbitSegment> fromCurrentUt, out long windowIndex));

            // Now resolve the SAME window explicitly on a fresh resolver.
            var fakeB = new FakeBuilder();
            var resolverB = NewResolverWith(fakeB);
            Assert.True(resolverB.TryResolveWindowSegmentsForWindow(
                "mA", segs, plan, schedule, windowIndex, out List<OrbitSegment> fromExplicit));

            Assert.Equal(fromCurrentUt.Count, fromExplicit.Count);
            for (int i = 0; i < fromCurrentUt.Count; i++)
                Assert.Equal(fromCurrentUt[i].semiMajorAxis, fromExplicit[i].semiMajorAxis, 6);
        }

        // The eviction band keeps the dict bounded: advancing the window monotonically through 0..10 leaves
        // only the [current-1, current+1] band cached (3 windows max per member), never all 11.
        [Fact]
        public void EvictionBand_KeepsDictBounded_AsWindowsAdvance()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            var segs = HeliocentricMemberSegments();

            // Advance the window monotonically 0..10 (one build per window: 11 builds total).
            for (long w = 0; w <= 10; w++)
                Assert.True(resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, w, out _));

            // After the final insert (window 10 = band center), only the [9, 11] band survives. 11 was never
            // inserted, so windows 9 and 10 are the only cached entries; re-resolving them is a cache HIT
            // (still 1 build each). Windows 0..8 were evicted; re-resolving any rebuilds (+1 build).
            Assert.Equal(1, fake.BuildCountByWindow[9]);
            Assert.Equal(1, fake.BuildCountByWindow[10]);
            // Probe the eviction of an out-of-band window FIRST while 10 is still the center: window 8 must
            // have been evicted -> a rebuild. (Probing 8 makes 8 the new center, but we read its count after.)
            Assert.Equal(1, fake.BuildCountByWindow[8]); // built once in the loop, evicted, not yet rebuilt
            resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, 8, out _);
            Assert.Equal(2, fake.BuildCountByWindow[8]); // 8 was evicted -> rebuilt now
        }

        // Window 9 and 10 stay cached together after a monotonic advance to 10 (the band holds the two
        // overlap windows the milestone needs). Asserted on its own so the band-retention is unambiguous.
        [Fact]
        public void EvictionBand_RetainsCurrentAndPriorWindow()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            var segs = HeliocentricMemberSegments();

            for (long w = 0; w <= 10; w++)
                resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, w, out _);

            // Re-resolve 9 then 10: both cache hits (no rebuild) because both are inside the [9,11] band of
            // center 10. Note: re-resolving 9 makes 9 the center (band [8,10]), which still contains 10, so
            // the subsequent 10 re-resolve is also a hit.
            resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, 9, out _);
            resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, 10, out _);
            Assert.Equal(1, fake.BuildCountByWindow[9]);
            Assert.Equal(1, fake.BuildCountByWindow[10]);
        }

        // Eviction is per-member: one member advancing its window far never evicts another member's cached
        // window. The FakeBuilder counts per (window) across members, so use disjoint window ranges to keep
        // the build counts unambiguous (A uses window 0; B uses windows 100..110).
        [Fact]
        public void EvictionBand_IsPerMember_DoesNotDropOtherMembersWindows()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            var segs = HeliocentricMemberSegments();

            // Member A caches window 0 (1 build).
            Assert.True(resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, 0, out _));
            Assert.Equal(1, fake.BuildCountByWindow[0]);

            // Member B advances through a DISJOINT window range so its eviction band never overlaps A's 0.
            for (long w = 100; w <= 110; w++)
                resolver.TryResolveWindowSegmentsForWindow("mB", segs, plan, schedule, w, out _);

            // A's window 0 must still be cached (B's per-member eviction never touched it): re-resolving A's
            // window 0 is a cache HIT -> still exactly 1 build for window 0.
            Assert.True(resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, 0, out var aSeg));
            Assert.NotNull(aSeg);
            Assert.Equal(1, fake.BuildCountByWindow[0]);
        }

        // Unsupported plan / invalid schedule -> false (faithful), no build, on both overloads.
        [Fact]
        public void UnsupportedOrInvalid_ReturnsFalse_NoBuild()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var segs = HeliocentricMemberSegments();

            var unsupported = ReaimMissionPlan.Unsupported("Kerbin", "test");
            Assert.False(resolver.TryResolveWindowSegmentsForWindow("mA", segs, unsupported, schedule, 0, out _));

            var invalidSchedule = default(ReaimWindowPlanner.ReaimWindowSchedule); // Valid == false
            Assert.False(resolver.TryResolveWindowSegmentsForWindow("mA", segs, SupportedPlan(), invalidSchedule, 0, out _));

            Assert.Empty(fake.BuiltWindows);
        }

        // A member with no heliocentric leg -> the pre-check returns false before any build on both overloads.
        [Fact]
        public void NoHeliocentricLeg_ReturnsFalse_BeforeBuild()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            // A body-relative-only member (wrong body) has no common-ancestor leg in the window.
            var noHelio = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = RecordedDeparture, endUT = RecordedArrival, bodyName = "Kerbin", isPredicted = false },
            };

            Assert.False(resolver.TryResolveWindowSegmentsForWindow("mA", noHelio, plan, schedule, 0, out _));
            Assert.Empty(fake.BuiltWindows);
        }

        // Clear() drops all cached windows -> the next resolve rebuilds (a recording edit must not survive).
        [Fact]
        public void Clear_DropsAllCachedWindows()
        {
            var schedule = PlanSchedule();
            var fake = new FakeBuilder();
            var resolver = NewResolverWith(fake);
            var plan = SupportedPlan();
            var segs = HeliocentricMemberSegments();

            resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, 2, out _);
            Assert.Equal(1, fake.BuildCountByWindow[2]);
            resolver.Clear();
            resolver.TryResolveWindowSegmentsForWindow("mA", segs, plan, schedule, 2, out _);
            Assert.Equal(2, fake.BuildCountByWindow[2]); // rebuilt after Clear
        }
    }
}
