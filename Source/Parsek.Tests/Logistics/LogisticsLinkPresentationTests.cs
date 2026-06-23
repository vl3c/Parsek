using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// M4c: the pure presentation helper behind the round-trip link picker. Tests the
    /// eligible-partner filter (exclude self + already-linked + id-less routes,
    /// preserve order) and the detail-panel pairing note formatting, off the IMGUI path.
    /// </summary>
    public class LogisticsLinkPresentationTests
    {
        private static Route Route(string id, string name = null, string linkedTo = null)
        {
            return new RouteFixtureBuilder()
                .WithId(id)
                .WithName(name ?? id)
                .WithLinkedRouteId(linkedTo)
                .Build();
        }

        [Fact]
        public void BuildLinkCandidates_ExcludesSelf()
        {
            var routes = new List<Route> { Route("a"), Route("b"), Route("c") };

            var result = LogisticsLinkPresentation.BuildLinkCandidates(routes, "a");

            Assert.Equal(2, result.Count);
            Assert.DoesNotContain(result, c => c.Id == "a");
            Assert.Contains(result, c => c.Id == "b");
            Assert.Contains(result, c => c.Id == "c");
        }

        [Fact]
        public void BuildLinkCandidates_ExcludesAlreadyLinkedRoutes()
        {
            // b is already linked to c; only the unlinked routes are eligible partners.
            var routes = new List<Route>
            {
                Route("a"),
                Route("b", linkedTo: "c"),
                Route("c", linkedTo: "b"),
                Route("d"),
            };

            var result = LogisticsLinkPresentation.BuildLinkCandidates(routes, "a");

            Assert.Single(result);
            Assert.Equal("d", result[0].Id);
        }

        [Fact]
        public void BuildLinkCandidates_SkipsNullAndEmptyIdRoutes()
        {
            var routes = new List<Route> { Route("a"), null, Route(""), Route("b") };

            var result = LogisticsLinkPresentation.BuildLinkCandidates(routes, "a");

            Assert.Single(result);
            Assert.Equal("b", result[0].Id);
        }

        [Fact]
        public void BuildLinkCandidates_PreservesInputOrder()
        {
            var routes = new List<Route> { Route("z"), Route("a"), Route("m") };

            var result = LogisticsLinkPresentation.BuildLinkCandidates(routes, "a");

            Assert.Equal(2, result.Count);
            Assert.Equal("z", result[0].Id);
            Assert.Equal("m", result[1].Id);
        }

        [Fact]
        public void BuildLinkCandidates_UsesIdWhenNameEmpty()
        {
            var routes = new List<Route> { Route("a"), new RouteFixtureBuilder().WithId("b").WithName("").Build() };

            var result = LogisticsLinkPresentation.BuildLinkCandidates(routes, "a");

            Assert.Single(result);
            Assert.Equal("b", result[0].Id);
            Assert.Equal("b", result[0].Name); // falls back to id
        }

        [Fact]
        public void BuildLinkCandidates_NullRoutesOrEmptySource_ReturnsEmpty()
        {
            Assert.Empty(LogisticsLinkPresentation.BuildLinkCandidates(null, "a"));
            Assert.Empty(LogisticsLinkPresentation.BuildLinkCandidates(new List<Route> { Route("a") }, null));
            Assert.Empty(LogisticsLinkPresentation.BuildLinkCandidates(new List<Route> { Route("a") }, ""));
        }

        [Fact]
        public void FormatLinkedNote_NamesPartner()
        {
            string note = LogisticsLinkPresentation.FormatLinkedNote("Munar Return");
            Assert.Contains("Munar Return", note);
            Assert.Contains("Round-trip linked", note);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatLinkedNote_NullOrEmpty_RendersUnnamed(string name)
        {
            string note = LogisticsLinkPresentation.FormatLinkedNote(name);
            Assert.Contains("<unnamed>", note);
        }
    }
}
