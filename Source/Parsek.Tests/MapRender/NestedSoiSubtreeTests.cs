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
    }
}
