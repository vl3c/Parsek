using System;
using System.Collections.Generic;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-7 guard for <see cref="NestedSoiSubtree"/> (migration plan §9 / design §10): the typed
    /// nested-SOI (moon-rich, Jool) body-tree model the fail-closed classifier reads. DEFINE-ONLY — the
    /// type carries the body tree + per-leg crossings so the future recursive producer + the tracer have a
    /// structured identity; it produces NO synthetic geometry.
    ///
    /// Covers the nesting DECISION (a sibling moon hop under a non-root ancestor is nested; a single-level
    /// Kerbin-Mun or interplanetary chain is NOT), the crossing list, and the summary token. Each
    /// assertion states the bug it catches: mis-detecting a single-level mission as nested would needlessly
    /// fail-close it; missing a real Jool tour would hand it to a non-existent producer.
    /// </summary>
    public class NestedSoiSubtreeTests
    {
        private static readonly Dictionary<string, string> StockParents =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Sun", null },
                { "Kerbin", "Sun" }, { "Duna", "Sun" }, { "Jool", "Sun" },
                { "Mun", "Kerbin" }, { "Minmus", "Kerbin" },
                { "Laythe", "Jool" }, { "Vall", "Jool" }, { "Tylo", "Jool" },
                { "Ike", "Duna" },
            };

        private static string Parent(string body)
            => StockParents.TryGetValue(body, out string p) ? p : null;

        // The LIVE KSP convention: the root body (Sun) is SELF-REFERENTIAL - CelestialBody.referenceBody for
        // the Sun returns the Sun itself, so the live IBodyInfo.ReferenceBodyName("Sun") returns "Sun", NOT
        // null. The headless StockParents fake (Sun -> null) masked the in-game fail-closed false-positive on
        // ordinary interplanetary transfers; this fake reproduces the live tree so the regression is headless.
        private static readonly Dictionary<string, string> LiveSunParents =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Sun", "Sun" }, // self-referential root (live KSP)
                { "Kerbin", "Sun" }, { "Duna", "Sun" }, { "Jool", "Sun" },
                { "Mun", "Kerbin" }, { "Minmus", "Kerbin" },
                { "Laythe", "Jool" }, { "Vall", "Jool" }, { "Tylo", "Jool" },
                { "Ike", "Duna" },
            };

        private static string LiveSunParent(string body)
            => LiveSunParents.TryGetValue(body, out string p) ? p : null;

        [Fact]
        public void TryBuild_JoolMoonTour_IsNested_WithCrossingsAndRoot()
        {
            // Jool -> Laythe -> Tylo -> Jool: Laythe + Tylo are siblings under Jool (a non-root planet), the
            // canonical moon tour. Distinct visited bodies = {Jool, Laythe, Tylo}; crossings = each adjacent
            // hop (3).
            var bodies = new List<string> { "Jool", "Laythe", "Tylo", "Jool" };
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(bodies, Parent);

            Assert.NotNull(subtree);
            Assert.True(subtree.IsNested);
            Assert.Equal("Jool", subtree.RootBody);
            Assert.Equal(3, subtree.VisitedBodies.Count); // Jool, Laythe, Tylo (distinct)
            Assert.Equal(3, subtree.CrossingCount);       // Jool->Laythe, Laythe->Tylo, Tylo->Jool
        }

        [Fact]
        public void TryBuild_KerbinMunMinmus_IsNested_TwoSiblingMoons()
        {
            // Mun + Minmus are siblings under Kerbin (a non-root planet) -> nested. (Visiting two of a
            // planet's moons is the same structural case as a Jool tour.)
            var bodies = new List<string> { "Kerbin", "Mun", "Kerbin", "Minmus" };
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(bodies, Parent);

            Assert.NotNull(subtree);
            Assert.True(subtree.IsNested);
            Assert.Equal("Kerbin", subtree.RootBody);
        }

        [Fact]
        public void TryBuild_SingleLevelKerbinMun_IsNotNested()
        {
            // Kerbin -> Mun -> Kerbin: only ONE moon under Kerbin -> no sibling hop -> NOT nested.
            var bodies = new List<string> { "Kerbin", "Mun", "Kerbin" };
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(bodies, Parent));
        }

        [Fact]
        public void TryBuild_InterplanetaryChain_IsNotNested_SunIsRoot()
        {
            // Kerbin -> Sun -> Duna: Kerbin + Duna are siblings, but under the SUN (the root, ReferenceBody
            // null) -> interplanetary, NOT a nested-SOI moon tour.
            var bodies = new List<string> { "Kerbin", "Sun", "Duna" };
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(bodies, Parent));
        }

        [Fact]
        public void TryBuild_InterplanetaryChain_LiveSelfReferentialSun_IsNotNested()
        {
            // The LIVE-tree regression for the in-game fail-closed false-positive: with the self-referential
            // Sun (Parent("Sun") == "Sun", as live KSP reports), an ordinary Kerbin -> Sun -> Duna transfer
            // must STILL be NOT nested. Before the self-reference guard, the Sun (>= 2 visited children) had a
            // non-null grandparent ("Sun") and was wrongly returned as a nested root -> every interplanetary
            // transfer fail-closed. Also covers the Sun-omitted variant a recording's body list may produce.
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Kerbin", "Sun", "Duna" }, LiveSunParent));
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Kerbin", "Duna" }, LiveSunParent));
        }

        [Fact]
        public void TryBuild_JoolMoonTour_LiveSelfReferentialSun_StillNested()
        {
            // The guard must NOT over-correct: a real moon tour is still nested under the live self-referential
            // Sun. Jool's parent is the Sun (not itself), so Jool remains a valid non-root nested ancestor.
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Jool", "Laythe", "Tylo" }, LiveSunParent);
            Assert.NotNull(subtree);
            Assert.True(subtree.IsNested);
            Assert.Equal("Jool", subtree.RootBody);
        }

        [Fact]
        public void TryBuild_DunaIke_IsNotNested_SingleMoonUnderDuna()
        {
            // Duna -> Ike -> Duna: Ike under Duna; this is a single moon under Duna so it is NOT two
            // siblings -> NOT nested (mirrors the Kerbin-Mun single-level case).
            var bodies = new List<string> { "Duna", "Ike", "Duna" };
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(bodies, Parent));
        }

        [Fact]
        public void TryBuild_NullOrSingle_OrNullDelegate_ReturnsNull()
        {
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(null, Parent));
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(new List<string> { "Jool" }, Parent));
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Jool", "Laythe", "Tylo" }, referenceBodyName: null));
        }

        [Fact]
        public void TryBuild_DelegateThatThrows_IsTolerated_NotNested()
        {
            // A throwing parent-chain probe must not propagate (it is treated as "cannot resolve" -> not
            // nested, never an NRE in the render path).
            Func<string, string> thrower = _ => throw new InvalidOperationException("boom");
            Assert.Null(NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Jool", "Laythe", "Tylo" }, thrower));
        }

        [Fact]
        public void SummaryToken_NamesRootVisitedAndCrossings()
        {
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Jool", "Laythe", "Tylo" }, Parent);
            string token = subtree.ToSummaryToken();

            Assert.Contains("root=Jool", token);
            Assert.Contains("visited=3", token);
            Assert.Contains("crossings=2", token);
            Assert.Contains("Jool/Laythe/Tylo", token);
        }

        [Fact]
        public void Constructor_NullArgs_DefaultToEmptyLists()
        {
            var subtree = new NestedSoiSubtree("Jool", null, null);
            Assert.NotNull(subtree.VisitedBodies);
            Assert.NotNull(subtree.Crossings);
            Assert.Empty(subtree.VisitedBodies);
            Assert.False(subtree.IsNested);
        }

        // ---- Review S15: the payload is scoped to the ROOT'S SOI HIERARCHY ----

        [Fact]
        public void TryBuild_KerbinDepartureJoolTour_PayloadScopedToSubtree()
        {
            // THE S15 case: a real Kerbin-departure Jool tour. The nesting DECISION still fires (Laythe +
            // Tylo are siblings under Jool), but the PAYLOAD must contain only the subtree: Kerbin (the
            // departure body) and Sun (the interplanetary transfer) are NOT part of the Jool hierarchy, so
            // VisitedBodies excludes them and the Kerbin->Sun / Sun->Jool legs are NOT intra-subtree
            // crossings. Pre-S15 both leaked in, so every consumer (the Tier-A fail-closed detail line,
            // the future recursive producer) read an interplanetary transfer as a moon hop.
            var bodies = new List<string> { "Kerbin", "Sun", "Jool", "Laythe", "Tylo" };
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(bodies, Parent);

            Assert.NotNull(subtree);
            Assert.Equal("Jool", subtree.RootBody);
            Assert.Equal(new[] { "Jool", "Laythe", "Tylo" }, subtree.VisitedBodies);
            Assert.Equal(2, subtree.CrossingCount); // Jool->Laythe, Laythe->Tylo only
            foreach (SoiCrossing c in subtree.Crossings)
            {
                Assert.NotEqual("Kerbin", c.FromBody);
                Assert.NotEqual("Sun", c.FromBody);
                Assert.NotEqual("Kerbin", c.ToBody);
                Assert.NotEqual("Sun", c.ToBody);
            }
        }

        [Fact]
        public void TryBuild_MoonsOnlyTour_PayloadStartsAtFirstVisitedMoon()
        {
            // A tour that never orbits Jool itself (moon-to-moon only after the transfer): the subtree
            // payload is just the visited moons, in first-visit order, and the single intra-subtree
            // crossing is the moon hop.
            var bodies = new List<string> { "Kerbin", "Sun", "Laythe", "Tylo" };
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(bodies, Parent);

            Assert.NotNull(subtree);
            Assert.Equal("Jool", subtree.RootBody);
            Assert.Equal(new[] { "Laythe", "Tylo" }, subtree.VisitedBodies);
            Assert.Equal(1, subtree.CrossingCount); // Laythe->Tylo
        }

        // ---- Review N11: FindNestedRoot resolves deterministically (first-visit order) ----

        [Fact]
        public void TryBuild_TwoQualifyingParents_RootIsFirstVisitedParent()
        {
            // Two qualifying nested roots in one sequence (Mun+Minmus under Kerbin, Laythe+Tylo under
            // Jool): the root must resolve by FIRST-VISIT order of the visited bodies, never by
            // Dictionary iteration order (insertion-order-ish today, but not contractual across runtimes).
            // Forward order picks Kerbin; reversed picks Jool - deterministic both ways.
            NestedSoiSubtree forward = NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Mun", "Minmus", "Laythe", "Tylo" }, Parent);
            Assert.NotNull(forward);
            Assert.Equal("Kerbin", forward.RootBody);
            Assert.Equal(new[] { "Mun", "Minmus" }, forward.VisitedBodies);
            Assert.Equal(1, forward.CrossingCount); // Mun->Minmus only

            NestedSoiSubtree reversed = NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Laythe", "Tylo", "Mun", "Minmus" }, Parent);
            Assert.NotNull(reversed);
            Assert.Equal("Jool", reversed.RootBody);
            Assert.Equal(new[] { "Laythe", "Tylo" }, reversed.VisitedBodies);
        }
    }
}
