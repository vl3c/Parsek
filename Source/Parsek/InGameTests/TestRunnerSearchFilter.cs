using System;
using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure filter logic for the in-game test-runner search box. Decides which
    /// categories/tests a query keeps VISIBLE; it never touches the runner's
    /// actual run sets, so filtering the display can never change which test a
    /// row button runs.
    /// </summary>
    internal static class TestRunnerSearchFilter
    {
        /// <summary>
        /// Case-insensitive substring test. An empty or whitespace query is "no
        /// filter" and matches everything. A non-empty query never matches a
        /// null/empty target.
        /// </summary>
        internal static bool Matches(string text, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;
            if (string.IsNullOrEmpty(text))
                return false;
            return text.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The name a test row is searched by: the primary name
        /// <see cref="TestRunnerPresentation.BuildTestLabel"/> renders (method
        /// name preferred, else <see cref="InGameTestInfo.Name"/>), without the
        /// [isolated]/[single]/duration decorations.
        /// </summary>
        internal static string GetSearchableTestName(InGameTestInfo test)
        {
            if (test == null)
                return string.Empty;

            return !string.IsNullOrEmpty(test.Method?.Name)
                ? test.Method.Name
                : (test.Name ?? string.Empty);
        }

        /// <summary>
        /// Filters the discovered category/test groups for DISPLAY ONLY.
        /// <list type="bullet">
        /// <item>Empty/whitespace query: returns the input list unchanged (same
        /// reference), so an empty box behaves byte-identically to no filter.</item>
        /// <item>Category name matches: the whole category (all its tests) is
        /// shown.</item>
        /// <item>Otherwise: only the tests whose name matches are shown; a
        /// category with neither a name match nor a matching test is dropped.</item>
        /// </list>
        /// The returned groups reference the SAME <see cref="InGameTestInfo"/>
        /// objects (never cloned or reindexed), so callers keep running exactly
        /// the test/category each visible row maps to.
        /// </summary>
        internal static List<KeyValuePair<string, List<InGameTestInfo>>> FilterCategories(
            List<KeyValuePair<string, List<InGameTestInfo>>> groups,
            string query)
        {
            if (groups == null)
                return new List<KeyValuePair<string, List<InGameTestInfo>>>();

            if (string.IsNullOrWhiteSpace(query))
                return groups;

            string trimmed = query.Trim();
            var result = new List<KeyValuePair<string, List<InGameTestInfo>>>();

            foreach (var group in groups)
            {
                // Category name match keeps the entire group unchanged.
                if (Matches(group.Key, trimmed))
                {
                    result.Add(group);
                    continue;
                }

                // Otherwise keep only the tests whose name matches.
                var tests = group.Value;
                List<InGameTestInfo> matchingTests = null;
                if (tests != null)
                {
                    for (int i = 0; i < tests.Count; i++)
                    {
                        var test = tests[i];
                        if (Matches(GetSearchableTestName(test), trimmed))
                        {
                            if (matchingTests == null)
                                matchingTests = new List<InGameTestInfo>();
                            matchingTests.Add(test);
                        }
                    }
                }

                if (matchingTests != null)
                    result.Add(new KeyValuePair<string, List<InGameTestInfo>>(group.Key, matchingTests));
            }

            return result;
        }
    }
}
