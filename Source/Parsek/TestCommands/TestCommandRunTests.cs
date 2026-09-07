using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure result-payload builder for the two-phase <c>RunTests</c> verb (P5.6). When
    /// the owned <c>InGameTestRunner</c> batch stops running, the addon reads its
    /// Passed / Failed / Skipped counts and feeds them here; the exported results file
    /// name is a fixed contract the orchestrator tails. Kept pure so the payload shape
    /// is xUnit-covered without Unity.
    /// </summary>
    internal static class TestCommandRunTests
    {
        /// <summary>The fixed results file name (in the KSP root) the runner exports to.</summary>
        internal const string ResultsFileName = "parsek-test-results.txt";

        /// <summary>Reject reason for an <c>isolated</c> arg that is not exactly "true"/"false".</summary>
        internal const string IsolatedArgInvalidReason = "isolated-arg-invalid";

        /// <summary>Reject reason for a <c>strict</c> arg that is not exactly "true"/"false".</summary>
        internal const string StrictArgInvalidReason = "strict-arg-invalid";

        /// <summary>Reject reason for a <c>category</c> arg that is present but empty.</summary>
        internal const string CategoryArgEmptyReason = "category-arg-empty";

        /// <summary>
        /// Reject reason for a comma-list <c>category</c> selector with an empty token
        /// (<c>A,,B</c>, a leading or trailing comma) or a repeated token (<c>A,A</c>).
        /// </summary>
        internal const string CategorySelectorMalformedReason = "category-selector-malformed";

        /// <summary>
        /// Splits the <c>category</c> arg into the categories one <c>RunTests</c> drives.
        /// ABSENT (null) is the RunAll shape and yields an EMPTY list; a single token is
        /// the single-category batch every pre-2026-09-07 spec drives; a comma list is
        /// the MULTI-CATEGORY batch, run sequentially by the addon exactly as the M-A3
        /// autorun driver runs its comma list (one RunCategory batch per token, each
        /// emitting its own BATCH_COMPLETE line, then one category=multi:N aggregate).
        ///
        /// FAIL-CLOSED on shape. An empty token between commas is a typo that would
        /// silently run ALL categories (RunCategory("") is the RunAll arm), and a
        /// repeated token would run one category twice while the aggregate's
        /// multi:N still counted it once - so both are rejected with
        /// <see cref="CategorySelectorMalformedReason"/> and a problem string that names
        /// the token. Tokens are trimmed, because the wire form is
        /// <c>category=A, B</c> only if a spec author writes it that way and the
        /// harness's own parser (hlib.parse_batch_selector_categories) trims too; the two
        /// halves must agree on what a token is. An EMPTY or whitespace arg is not this
        /// helper's case - <see cref="IsEmptyCategoryArg"/> rejects it first.
        /// </summary>
        internal static bool TryParseCategorySelector(
            string raw, out List<string> categories, out string problem)
        {
            categories = new List<string>();
            problem = null;
            if (raw == null)
                return true;
            string[] parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0)
                {
                    problem = "empty token at position " + (i + 1).ToString(CultureInfo.InvariantCulture);
                    categories.Clear();
                    return false;
                }
                for (int j = 0; j < categories.Count; j++)
                {
                    if (string.Equals(categories[j], token, System.StringComparison.Ordinal))
                    {
                        problem = "duplicate token " + token;
                        categories.Clear();
                        return false;
                    }
                }
                categories.Add(token);
            }
            return true;
        }

        /// <summary>
        /// True when a <c>category</c> arg was WRITTEN but is empty or whitespace, which
        /// is a typo rather than an omission and must be rejected.
        ///
        /// ABSENT (null) stays the RunAll shape - that is the documented meaning of
        /// `RunTests` with no `category` arg and every pre-R5 caller relies on it.
        /// `category=` (an empty value, which TestCommandProtocol.TrySplitToken really
        /// does produce) previously fell into the same branch, so a typo silently ran
        /// the WHOLE assembly. R5 made that materially worse: with `isolated=true` the
        /// same typo runs all 539 declarations through the isolated filter, quickload
        /// -restoring a flight baseline after each destructive FLIGHT test. It also
        /// contradicted this verb's own other argument, where an empty value is already
        /// rejected as a typo. Same convention for both, fail-closed for both.
        /// </summary>
        internal static bool IsEmptyCategoryArg(string raw)
        {
            return raw != null && raw.Trim().Length == 0;
        }

        /// <summary>
        /// Parses the optional <c>isolated</c> arg of the <c>RunTests</c> verb (R5).
        /// Returns false when the token is present but unrecognized, which the handler
        /// turns into a terminal REJECTED verdict.
        ///
        /// FAIL-CLOSED, and deliberately strict about case. The value reaches the wire
        /// through <c>run.py::encode_value</c>, which is <c>str(value)</c>: a spec author
        /// writing the TOML bool <c>isolated = true</c> would send the token
        /// <c>isolated=True</c>, and a lenient parse would silently accept it while
        /// <c>hlib.validate_spec</c> is written to reject that same spelling. Accepting
        /// only the lowercase literals keeps the two halves of the contract identical and
        /// makes the misspelling a loud REJECTED rather than a quiet route change. An
        /// ABSENT arg is the ordinary (non-isolated) path, which is what every spec
        /// written before R5 means; an EMPTY value is a typo, not an omission, so it is
        /// rejected rather than read as absent.
        /// </summary>
        internal static bool TryParseIsolatedArg(string raw, out bool isolated)
        {
            isolated = false;
            if (raw == null)
                return true;
            if (raw == "false")
                return true;
            if (raw == "true")
            {
                isolated = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Parses the optional <c>strict</c> arg of the <c>RunTests</c> verb (career-ledger
        /// B.4). It is the per-scenario seam for
        /// <c>LedgerGroundTruthDiff.StrictPerIdentityForTesting</c>, which promotes the
        /// ground-truth diff's REPORT-ONLY per-identity divergences to hard failures.
        ///
        /// WHY A VERB ARG AND NOT A WHITELISTED SETTING. Every name in
        /// <c>SettingWhitelist</c> is a real <c>ParsekSettings</c> field a player can see
        /// and toggle. Strictness of a diff that only ever runs inside one in-game test
        /// category is not a player-facing preference, and promoting a test seam to a
        /// shipped setting is the "temporary scaffold is not a feature" trap. The arg
        /// keeps the flag exactly where it belongs - on the command that starts the batch
        /// that reads it - and adds no settings surface, no sidecar key and no UI row.
        ///
        /// PARSE CONTRACT IS <see cref="TryParseIsolatedArg"/>'s, VERBATIM: absent is the
        /// ordinary (non-strict) path, only the exact lowercase wire literals are
        /// accepted, and an empty or mis-cased value is a REJECTED verdict rather than a
        /// silent fallback. Same reason - the value reaches the wire through
        /// <c>run.py::encode_value</c> == <c>str(value)</c>, so a TOML bool would arrive
        /// as <c>strict=True</c>, and a lenient parse would let a spec that reads as armed
        /// run un-armed.
        ///
        /// ASSIGNMENT IS UNCONDITIONAL AT THE CALL SITE, which is what makes the flag
        /// per-scenario rather than per-process-sticky: every <c>RunTests</c> writes the
        /// parsed value (absent = false) into the static, so a second batch in the same
        /// run cannot inherit strictness from the first.
        /// </summary>
        internal static bool TryParseStrictArg(string raw, out bool strict)
        {
            strict = false;
            if (raw == null)
                return true;
            if (raw == "false")
                return true;
            if (raw == "true")
            {
                strict = true;
                return true;
            }
            return false;
        }

        internal static List<KeyValuePair<string, string>> BuildResultPayload(int passed, int failed, int skipped)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("passed", passed.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("failed", failed.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("skipped", skipped.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("results", ResultsFileName),
            };
    }
}
