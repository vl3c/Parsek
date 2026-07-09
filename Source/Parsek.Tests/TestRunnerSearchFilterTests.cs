using System.Collections.Generic;
using System.Reflection;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure incremental-search filter behind the in-game test-runner
    /// search box. The IMGUI code only calls FilterCategories; this locks the
    /// match semantics (case-insensitivity, empty/whitespace = show all,
    /// category-name vs test-name match, and object-identity preservation).
    /// </summary>
    public class TestRunnerSearchFilterTests
    {
        private static void GraphicsSmokeTest()
        {
        }

        private static InGameTestInfo Test(string name)
        {
            return new InGameTestInfo { Name = name };
        }

        private static List<KeyValuePair<string, List<InGameTestInfo>>> Groups(
            params KeyValuePair<string, List<InGameTestInfo>>[] entries)
        {
            return new List<KeyValuePair<string, List<InGameTestInfo>>>(entries);
        }

        private static KeyValuePair<string, List<InGameTestInfo>> Category(
            string name,
            params string[] testNames)
        {
            var tests = new List<InGameTestInfo>();
            foreach (var t in testNames)
                tests.Add(Test(t));
            return new KeyValuePair<string, List<InGameTestInfo>>(name, tests);
        }

        // --- Matches primitive -------------------------------------------------

        [Fact]
        public void Matches_EmptyQuery_MatchesEverything()
        {
            Assert.True(TestRunnerSearchFilter.Matches("Anything", ""));
            Assert.True(TestRunnerSearchFilter.Matches(null, ""));
        }

        [Fact]
        public void Matches_WhitespaceQuery_MatchesEverything()
        {
            Assert.True(TestRunnerSearchFilter.Matches("Anything", "   "));
        }

        [Fact]
        public void Matches_IsCaseInsensitive()
        {
            Assert.True(TestRunnerSearchFilter.Matches("GraphicsSmoke", "graph"));
            Assert.True(TestRunnerSearchFilter.Matches("graphicssmoke", "GRAPH"));
        }

        [Fact]
        public void Matches_TrimsQueryWhitespace()
        {
            Assert.True(TestRunnerSearchFilter.Matches("Graphics", "  graph  "));
        }

        [Fact]
        public void Matches_NonEmptyQuery_NeverMatchesEmptyTarget()
        {
            Assert.False(TestRunnerSearchFilter.Matches("", "graph"));
            Assert.False(TestRunnerSearchFilter.Matches(null, "graph"));
        }

        [Fact]
        public void Matches_NoSubstring_ReturnsFalse()
        {
            Assert.False(TestRunnerSearchFilter.Matches("Recording", "graph"));
        }

        // --- GetSearchableTestName --------------------------------------------

        [Fact]
        public void GetSearchableTestName_PrefersMethodNameOverName()
        {
            MethodInfo method = typeof(TestRunnerSearchFilterTests).GetMethod(
                nameof(GraphicsSmokeTest),
                BindingFlags.NonPublic | BindingFlags.Static);
            var test = new InGameTestInfo { Name = "Fixture.GraphicsSmokeTest", Method = method };

            Assert.Equal("GraphicsSmokeTest", TestRunnerSearchFilter.GetSearchableTestName(test));
        }

        [Fact]
        public void GetSearchableTestName_FallsBackToName_WhenNoMethod()
        {
            Assert.Equal("MyTest", TestRunnerSearchFilter.GetSearchableTestName(Test("MyTest")));
        }

        [Fact]
        public void GetSearchableTestName_NullTest_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, TestRunnerSearchFilter.GetSearchableTestName(null));
        }

        // --- FilterCategories --------------------------------------------------

        [Fact]
        public void FilterCategories_EmptyQuery_ReturnsSameListReference()
        {
            var groups = Groups(Category("Graphics", "A", "B"));

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "");

            // Byte-identical to no filter: same reference, untouched.
            Assert.Same(groups, filtered);
        }

        [Fact]
        public void FilterCategories_WhitespaceQuery_ReturnsSameListReference()
        {
            var groups = Groups(Category("Graphics", "A"));

            Assert.Same(groups, TestRunnerSearchFilter.FilterCategories(groups, "   "));
        }

        [Fact]
        public void FilterCategories_NullGroups_ReturnsEmpty()
        {
            var filtered = TestRunnerSearchFilter.FilterCategories(null, "graph");

            Assert.NotNull(filtered);
            Assert.Empty(filtered);
        }

        [Fact]
        public void FilterCategories_CategoryNameMatch_KeepsWholeCategory()
        {
            var graphics = Category("Graphics", "AlphaCheck", "BetaCheck");
            var recording = Category("Recording", "SaveLoad");
            var groups = Groups(graphics, recording);

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "graph");

            Assert.Single(filtered);
            Assert.Equal("Graphics", filtered[0].Key);
            // Category name match shows ALL its tests, unfiltered.
            Assert.Equal(2, filtered[0].Value.Count);
            // And keeps the very same inner list reference (no clone).
            Assert.Same(graphics.Value, filtered[0].Value);
        }

        [Fact]
        public void FilterCategories_CaseInsensitiveCategoryMatch()
        {
            var groups = Groups(Category("Graphics", "A"));

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "GRAPH");

            Assert.Single(filtered);
            Assert.Equal("Graphics", filtered[0].Key);
        }

        [Fact]
        public void FilterCategories_TestNameMatch_KeepsOnlyMatchingTests()
        {
            var recording = Category("Recording", "GraphSample", "SaveLoad", "GraphReplay");
            var groups = Groups(recording);

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "graph");

            Assert.Single(filtered);
            Assert.Equal("Recording", filtered[0].Key);
            Assert.Equal(2, filtered[0].Value.Count);
            Assert.Contains(filtered[0].Value, t => t.Name == "GraphSample");
            Assert.Contains(filtered[0].Value, t => t.Name == "GraphReplay");
            Assert.DoesNotContain(filtered[0].Value, t => t.Name == "SaveLoad");
        }

        [Fact]
        public void FilterCategories_TestNameMatch_PreservesTestObjectReferences()
        {
            var match = Test("GraphSample");
            var noMatch = Test("SaveLoad");
            var groups = Groups(new KeyValuePair<string, List<InGameTestInfo>>(
                "Recording", new List<InGameTestInfo> { match, noMatch }));

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "graph");

            // The surviving row must reference the exact same test object so the
            // play button still runs it (filter is display-only).
            Assert.Same(match, filtered[0].Value[0]);
        }

        [Fact]
        public void FilterCategories_NoMatch_ReturnsEmpty()
        {
            var groups = Groups(
                Category("Graphics", "AlphaCheck"),
                Category("Recording", "SaveLoad"));

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "zzz");

            Assert.Empty(filtered);
        }

        [Fact]
        public void FilterCategories_CategoryDroppedWhenNeitherNameNorTestsMatch()
        {
            var groups = Groups(
                Category("Graphics", "GraphSample"),
                Category("Recording", "SaveLoad"));

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "graph");

            // Recording has no name match and no matching test -> dropped entirely.
            Assert.Single(filtered);
            Assert.Equal("Graphics", filtered[0].Key);
        }

        [Fact]
        public void FilterCategories_MatchesMethodName()
        {
            MethodInfo method = typeof(TestRunnerSearchFilterTests).GetMethod(
                nameof(GraphicsSmokeTest),
                BindingFlags.NonPublic | BindingFlags.Static);
            // Name deliberately lacks "smoke" so ONLY the method name can match it;
            // this proves method-name precedence, not a Name-substring coincidence.
            var test = new InGameTestInfo { Name = "Fixture.Alpha", Method = method };
            var groups = Groups(new KeyValuePair<string, List<InGameTestInfo>>(
                "Misc", new List<InGameTestInfo> { test }));

            // "smoke" is only in the method name GraphicsSmokeTest.
            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "smoke");

            Assert.Single(filtered);
            Assert.Same(test, filtered[0].Value[0]);
        }

        [Fact]
        public void FilterCategories_CategoryNameMatch_ShowsAllTests_EvenWhenSomeTestsAlsoMatch()
        {
            var graphics = Category("Graphics", "GraphSample", "SaveLoad");
            var groups = Groups(graphics);

            // "graph" matches the category name AND one test name. The category-name
            // branch must win and return the FULL inner list (same reference), not
            // the test-name-filtered subset.
            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "graph");

            Assert.Single(filtered);
            Assert.Same(graphics.Value, filtered[0].Value);
            Assert.Equal(2, filtered[0].Value.Count);
        }

        [Fact]
        public void FilterCategories_TrimsQuery()
        {
            var groups = Groups(Category("Graphics", "A"));

            var filtered = TestRunnerSearchFilter.FilterCategories(groups, "  graph  ");

            Assert.Single(filtered);
            Assert.Equal("Graphics", filtered[0].Key);
        }
    }
}
