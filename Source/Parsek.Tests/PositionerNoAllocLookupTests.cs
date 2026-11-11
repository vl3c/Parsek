using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The two per-frame lookups of the ghost positioner that stopped allocating:
    /// <see cref="ParsekFlightGhostPositioner.FindFirstMatching{T}"/> (the closure-free
    /// <c>List.Find</c> behind every body lookup) and the orbit segment index pair
    /// <see cref="TrajectoryMath.FindOrbitSegmentIndex"/> /
    /// <see cref="TrajectoryMath.ResolveOrbitSegmentIndexOfFound"/> (the orbit cache key offset
    /// that used to come from <c>List.IndexOf</c>). The body lookup is tested over a stand-in
    /// type so no test touches a CelestialBody or FlightGlobals.
    /// </summary>
    public class PositionerNoAllocLookupTests
    {
        private sealed class FakeBody
        {
            public readonly string Name;
            public readonly string BodyName;

            public FakeBody(string name, string bodyName = null)
            {
                Name = name;
                BodyName = bodyName ?? name;
            }
        }

        // Same predicate shapes as the positioner's three matchers.
        private static readonly Func<FakeBody, string, bool> NameMatches =
            (b, name) => b.Name == name;
        private static readonly Func<FakeBody, string, bool> LiveBodyNameMatches =
            (b, name) => b != null && b.BodyName == name;
        private static readonly Func<FakeBody, string, bool> EitherNameOrdinalMatches =
            (b, name) => !object.ReferenceEquals(b, null)
                && (string.Equals(b.BodyName, name, StringComparison.Ordinal)
                    || string.Equals(b.Name, name, StringComparison.Ordinal));

        // --- FindFirstMatching ---

        [Fact]
        public void FindFirstMatching_ReturnsTheFirstMatchInListOrder()
        {
            var first = new FakeBody("Kerbin");
            var second = new FakeBody("Kerbin");
            var list = new List<FakeBody> { new FakeBody("Sun"), first, new FakeBody("Mun"), second };

            Assert.Same(first, ParsekFlightGhostPositioner.FindFirstMatching(list, "Kerbin", NameMatches));
        }

        [Fact]
        public void FindFirstMatching_NoMatchOrEmptyList_ReturnsNull()
        {
            var list = new List<FakeBody> { new FakeBody("Sun"), new FakeBody("Kerbin") };

            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(list, "Duna", NameMatches));
            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(new List<FakeBody>(), "Kerbin", NameMatches));
        }

        [Fact]
        public void FindFirstMatching_NameIsCompareOrdinal()
        {
            var list = new List<FakeBody> { new FakeBody("kerbin"), new FakeBody("Kerbin ") };

            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(list, "Kerbin", NameMatches));
        }

        [Fact]
        public void FindFirstMatching_NullList_ThrowsLikeACallOnANullReceiver()
        {
            Assert.Throws<NullReferenceException>(() =>
                ParsekFlightGhostPositioner.FindFirstMatching<FakeBody>(null, "Kerbin", NameMatches));
        }

        [Fact]
        public void FindFirstMatching_NullEntryBeforeTheMatch_ThrowsWhereTheLambdaThrew()
        {
            var list = new List<FakeBody> { new FakeBody("Sun"), null, new FakeBody("Kerbin") };

            Assert.Throws<NullReferenceException>(() => list.Find(b => b.Name == "Kerbin"));
            Assert.Throws<NullReferenceException>(() =>
                ParsekFlightGhostPositioner.FindFirstMatching(list, "Kerbin", NameMatches));
        }

        [Fact]
        public void FindFirstMatching_NullEntryAfterTheMatch_IsNeverVisited()
        {
            var kerbin = new FakeBody("Kerbin");
            var list = new List<FakeBody> { kerbin, null };

            Assert.Same(kerbin, ParsekFlightGhostPositioner.FindFirstMatching(list, "Kerbin", NameMatches));
        }

        [Fact]
        public void FindFirstMatching_NullSkippingMatcher_SkipsNullEntries()
        {
            var mun = new FakeBody("Mun");
            var list = new List<FakeBody> { null, new FakeBody("Sun"), null, mun };

            Assert.Same(mun, ParsekFlightGhostPositioner.FindFirstMatching(list, "Mun", LiveBodyNameMatches));
            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(list, null, LiveBodyNameMatches));
        }

        [Fact]
        public void FindFirstMatching_PassesTheNameItWasGiven()
        {
            var seen = new List<string>();
            var list = new List<FakeBody> { new FakeBody("Sun"), new FakeBody("Kerbin") };

            ParsekFlightGhostPositioner.FindFirstMatching(list, "Duna", (b, name) =>
            {
                seen.Add(name);
                return false;
            });

            Assert.Equal(new[] { "Duna", "Duna" }, seen);
        }

        [Fact]
        public void FindFirstMatching_AgreesWithListFindForEveryMatcherAndName()
        {
            // Names include null and empty: the replaced lambdas compared with ==, so a null name
            // matches a null-named body under the plain matcher and none under the null-skipping one.
            var bodies = new List<FakeBody>
            {
                new FakeBody("Sun"),
                new FakeBody(null, "Kerbol"),
                new FakeBody("Kerbin", "Kerbin"),
                new FakeBody("", "Eve"),
                new FakeBody("Mun", "Kerbin"),
                new FakeBody("Kerbin", "Kerbin2"),
            };
            var withNull = new List<FakeBody>(bodies);
            withNull.Insert(3, null);
            var names = new[] { null, "", "Sun", "Kerbol", "Kerbin", "Eve", "Mun", "Kerbin2", "Duna" };
            var matchers = new[] { NameMatches, LiveBodyNameMatches, EitherNameOrdinalMatches };

            int compared = 0;
            foreach (var matcher in matchers)
            {
                foreach (var list in new[] { bodies, withNull })
                {
                    foreach (string name in names)
                    {
                        FakeBody expected = null;
                        Type expectedThrow = null;
                        try { expected = list.Find(b => matcher(b, name)); }
                        catch (Exception ex) { expectedThrow = ex.GetType(); }

                        if (expectedThrow != null)
                        {
                            Assert.Throws(expectedThrow, () =>
                                ParsekFlightGhostPositioner.FindFirstMatching(list, name, matcher));
                        }
                        else
                        {
                            Assert.Same(expected,
                                ParsekFlightGhostPositioner.FindFirstMatching(list, name, matcher));
                        }
                        compared++;
                    }
                }
            }
            Assert.Equal(3 * 2 * 9, compared);
        }

        // --- orbit segment index ---

        private static OrbitSegment Seg(double startUT, double endUT, string body = "Kerbin", double sma = 700000)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = body,
                semiMajorAxis = sma,
                eccentricity = 0.01,
                inclination = 1,
                epoch = startUT,
            };
        }

        [Fact]
        public void FindOrbitSegmentIndex_NullOrEmpty_IsMinusOne()
        {
            Assert.Equal(-1, TrajectoryMath.FindOrbitSegmentIndex(null, 10));
            Assert.Equal(-1, TrajectoryMath.FindOrbitSegmentIndex(new List<OrbitSegment>(), 10));
        }

        [Theory]
        [InlineData(99.0, -1)]   // before the first segment
        [InlineData(100.0, 0)]   // first start, inclusive
        [InlineData(199.0, 0)]
        [InlineData(200.0, 1)]   // interior end is exclusive: the next segment owns its start
        [InlineData(250.0, -1)]  // the gap between segments 1 and 2
        [InlineData(300.0, 2)]
        [InlineData(400.0, 2)]   // the last segment is closed at its end
        [InlineData(400.5, -1)]
        public void FindOrbitSegmentIndex_HalfOpenExceptTheLast(double ut, int expected)
        {
            var segments = new List<OrbitSegment> { Seg(100, 200), Seg(200, 250), Seg(300, 400) };

            Assert.Equal(expected, TrajectoryMath.FindOrbitSegmentIndex(segments, ut));
        }

        [Fact]
        public void FindOrbitSegmentIndex_OverlappingSegments_FirstWins()
        {
            var segments = new List<OrbitSegment> { Seg(100, 300, sma: 1), Seg(150, 200, sma: 2) };

            Assert.Equal(0, TrajectoryMath.FindOrbitSegmentIndex(segments, 160));
        }

        [Fact]
        public void FindOrbitSegment_IsTheSegmentAtTheFoundIndex()
        {
            var segments = new List<OrbitSegment> { Seg(100, 200, sma: 1), Seg(200, 250, sma: 2), Seg(300, 400, sma: 3) };

            for (double ut = 90; ut <= 410; ut += 5)
            {
                int index = TrajectoryMath.FindOrbitSegmentIndex(segments, ut);
                OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(segments, ut);
                Assert.Equal(index >= 0, seg.HasValue);
                if (index >= 0)
                    Assert.Equal(segments[index].semiMajorAxis, seg.Value.semiMajorAxis);
            }
        }

        [Fact]
        public void ResolveIndexOfFound_DistinctSegments_IsTheFoundIndex()
        {
            var segments = new List<OrbitSegment> { Seg(100, 200), Seg(200, 300), Seg(300, 400) };

            foreach (double ut in new[] { 100.0, 199.0, 200.0, 300.0, 399.0, 400.0 })
            {
                int found = TrajectoryMath.FindOrbitSegmentIndex(segments, ut);
                Assert.Equal(found, TrajectoryMath.ResolveOrbitSegmentIndexOfFound(segments, found, ut));
            }
        }

        [Fact]
        public void ResolveIndexOfFound_DuplicateLastSegmentAtItsEnd_KeepsIndexOfsEarlierIndex()
        {
            // The one case where the forward scan and IndexOf disagree: the last segment repeats
            // an earlier one field for field, and the UT is its endUT. The half-open test misses
            // the earlier copy, the closed test on the last one hits, and IndexOf (which the
            // orbit cache key used) returns the earlier copy.
            OrbitSegment a = Seg(100, 200);
            var segments = new List<OrbitSegment> { a, a };

            int found = TrajectoryMath.FindOrbitSegmentIndex(segments, 200);
            Assert.Equal(1, found);
            Assert.Equal(0, segments.IndexOf(segments[found]));
            Assert.Equal(0, TrajectoryMath.ResolveOrbitSegmentIndexOfFound(segments, found, 200));

            // Inside the range the forward scan already stops at the first copy.
            Assert.Equal(0, TrajectoryMath.FindOrbitSegmentIndex(segments, 150));
            Assert.Equal(0, TrajectoryMath.ResolveOrbitSegmentIndexOfFound(segments, 0, 150));
        }

        [Fact]
        public void ResolveIndexOfFound_MatchesIndexOfOverListsWithDuplicates()
        {
            // A small pool so random lists repeat segments often, probed at every start, end and
            // midpoint, against the IndexOf result the orbit cache key used before.
            var pool = new[]
            {
                Seg(100, 200, sma: 1), Seg(100, 200, sma: 2), Seg(200, 300, sma: 1),
                Seg(150, 250, sma: 3), Seg(100, 100, sma: 4),
            };
            var rng = new Random(20261110);
            int checkedHits = 0, edgeHits = 0;
            for (int trial = 0; trial < 400; trial++)
            {
                int count = 1 + rng.Next(5);
                var segments = new List<OrbitSegment>();
                for (int i = 0; i < count; i++)
                    segments.Add(pool[rng.Next(pool.Length)]);

                var probes = new List<double>();
                foreach (OrbitSegment s in segments)
                {
                    probes.Add(s.startUT);
                    probes.Add(s.endUT);
                    probes.Add((s.startUT + s.endUT) / 2);
                }
                foreach (double ut in probes)
                {
                    int found = TrajectoryMath.FindOrbitSegmentIndex(segments, ut);
                    if (found < 0)
                        continue;
                    int indexOf = segments.IndexOf(segments[found]);
                    Assert.Equal(indexOf, TrajectoryMath.ResolveOrbitSegmentIndexOfFound(segments, found, ut));
                    checkedHits++;
                    if (indexOf != found)
                        edgeHits++;
                }
            }
            Assert.True(checkedHits > 1000, "checked " + checkedHits);
            Assert.True(edgeHits > 0, "the random lists never reached the IndexOf edge");
        }

        // --- the positioner's own three matchers, through FindFirstMatching ---
        // A list of null entries is the one input that tells the matchers apart without a real
        // CelestialBody: the null-guarded two must skip every entry and find nothing, while the
        // unguarded name matcher must dereference the first entry and throw, exactly as the
        // List.Find lambda it replaced did. A null receiver fails the managed null check before
        // any native code runs, so these cells are safe on the Windows test host.

        [Fact]
        public void BodyNameMatches_NullEntryThrowsLikeTheReplacedLambda()
        {
            // Mono and a Debug build throw NullReferenceException at the callvirt. ThrowsAny
            // keeps the cell honest on a Windows Release host, where the JIT may inline
            // Object.get_name and surface its native call first; a null-guarded matcher would
            // return without throwing either way, which is the change this cell must catch.
            var bodies = new List<CelestialBody> { null, null };
            Assert.ThrowsAny<Exception>(() =>
                ParsekFlightGhostPositioner.FindFirstMatching(
                    bodies, "Kerbin", ParsekFlightGhostPositioner.BodyNameMatches));
        }

        [Fact]
        public void BodyNameMatches_EmptyListFindsNothing()
        {
            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(
                new List<CelestialBody>(), "Kerbin", ParsekFlightGhostPositioner.BodyNameMatches));
        }

        [Fact]
        public void LiveBodyBodyNameMatches_SkipsNullEntries()
        {
            var bodies = new List<CelestialBody> { null, null, null };
            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(
                bodies, "Kerbin", ParsekFlightGhostPositioner.LiveBodyBodyNameMatches));
            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(
                bodies, null, ParsekFlightGhostPositioner.LiveBodyBodyNameMatches));
        }

        [Fact]
        public void TailLiftTerminalBodyNameMatches_SkipsNullEntries()
        {
            var bodies = new List<CelestialBody> { null, null, null };
            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(
                bodies, "Kerbin", ParsekFlightGhostPositioner.TailLiftTerminalBodyNameMatches));
            Assert.Null(ParsekFlightGhostPositioner.FindFirstMatching(
                bodies, null, ParsekFlightGhostPositioner.TailLiftTerminalBodyNameMatches));
        }

        [Fact]
        public void MatcherDelegates_AreThreeDistinctDelegates()
        {
            Assert.NotNull(ParsekFlightGhostPositioner.BodyNameMatches);
            Assert.NotNull(ParsekFlightGhostPositioner.LiveBodyBodyNameMatches);
            Assert.NotNull(ParsekFlightGhostPositioner.TailLiftTerminalBodyNameMatches);
            Assert.NotSame(ParsekFlightGhostPositioner.BodyNameMatches,
                ParsekFlightGhostPositioner.LiveBodyBodyNameMatches);
            Assert.NotSame(ParsekFlightGhostPositioner.LiveBodyBodyNameMatches,
                ParsekFlightGhostPositioner.TailLiftTerminalBodyNameMatches);
        }
    }
}
