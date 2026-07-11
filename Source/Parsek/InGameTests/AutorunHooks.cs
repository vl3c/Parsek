using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsek.InGameTests
{
    // Pure decision core for the autorun hooks (module M-A3, design
    // docs/dev/design-autotest-autorun-hooks.md). Every method here is a pure,
    // Unity-free static function so the whole external contract (env parsing,
    // scene-settle gate, single-fire gate, exit decision) is xUnit-testable without
    // a live KSP. The live wiring in TestRunnerShortcut / InGameTestRunner /
    // ParsekScenario (Phases 3-6) reads process env once, feeds these functions live
    // state, and acts on the returned decisions - it holds no policy of its own.

    /// <summary>
    /// The parsed autorun env contract (design "Env var surface"). Read once at
    /// addon startup so a mid-process env mutation cannot change behavior (env is a
    /// launch-time contract, edge case 14).
    /// </summary>
    internal struct AutorunConfig
    {
        /// <summary>H1 will auto-run a batch. False = fully inert.</summary>
        public bool Enabled;

        /// <summary>Selector was the literal "all" -> runner.RunAll().</summary>
        public bool IsAll;

        /// <summary>
        /// Parsed, trimmed, non-empty category tokens (design multi-category). Empty
        /// for the "all" selector and for an inert config.
        /// </summary>
        public IReadOnlyList<string> Categories;

        /// <summary>H2 armed: quit after teardown+export (PARSEK_AUTORUN_EXIT=="1").</summary>
        public bool ExitArmed;

        /// <summary>The raw PARSEK_AUTORUN_TESTS value, kept for the startup log line.</summary>
        public string RawSelector;

        /// <summary>
        /// WARN lines the startup log should emit for a misconfiguration (edge cases
        /// 2, 9). Never null; empty on a clean config.
        /// </summary>
        public IReadOnlyList<string> Warnings;
    }

    internal static class AutorunHooks
    {
        private static readonly IReadOnlyList<string> NoCategories = new string[0];

        internal const string WarnZeroCategories =
            "autorun selector parsed to zero categories; H1 inert";
        internal const string WarnExitWithoutTests =
            "PARSEK_AUTORUN_EXIT set but PARSEK_AUTORUN_TESTS unset; nothing will auto-run or auto-quit";

        /// <summary>
        /// Parses the two autorun env vars into an <see cref="AutorunConfig"/>
        /// (design "Env var surface", edge cases 1, 2, 3, 9).
        ///
        /// - null / empty tests var  -> inert (edge 1), no selector warning.
        /// - "all"                   -> Enabled, IsAll (runner.RunAll()).
        /// - "A,B,C" / " A , B " / "A,,B" / ",A," -> trim tokens + drop empties (edge 2).
        /// - non-empty but parses to zero tokens (whitespace / commas only) -> inert
        ///   + WarnZeroCategories (edge 2).
        /// - a single unknown category (e.g. "Nope") is kept verbatim; the
        ///   "matched 0 discovered tests" signal is a runtime concern, not a parse
        ///   error (edge 3).
        /// - exit var "1" -> ExitArmed; combined with an unset tests var it adds
        ///   WarnExitWithoutTests (edge 9). Category match is Ordinal (case-sensitive)
        ///   to mirror the runner's category comparison.
        /// </summary>
        internal static AutorunConfig Parse(string testsVar, string exitVar)
        {
            bool exitArmed = string.Equals(exitVar, "1", StringComparison.Ordinal);

            // Truly unset/empty tests var: fully inert (edge 1). Only the exit-without-
            // tests misconfiguration warns here (edge 9).
            if (string.IsNullOrEmpty(testsVar))
            {
                var warnings0 = new List<string>();
                if (exitArmed)
                    warnings0.Add(WarnExitWithoutTests);
                return new AutorunConfig
                {
                    Enabled = false,
                    IsAll = false,
                    Categories = NoCategories,
                    ExitArmed = exitArmed,
                    RawSelector = testsVar,
                    Warnings = warnings0,
                };
            }

            if (string.Equals(testsVar.Trim(), "all", StringComparison.Ordinal))
            {
                return new AutorunConfig
                {
                    Enabled = true,
                    IsAll = true,
                    Categories = NoCategories,
                    ExitArmed = exitArmed,
                    RawSelector = testsVar,
                    Warnings = new List<string>(),
                };
            }

            var categories = testsVar
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (categories.Count == 0)
            {
                // Non-empty but nothing survived the trim/drop (whitespace / commas
                // only): inert + WARN (edge 2). This is distinct from truly unset, so
                // it does NOT also emit the exit-without-tests warning.
                var warnings1 = new List<string> { WarnZeroCategories };
                return new AutorunConfig
                {
                    Enabled = false,
                    IsAll = false,
                    Categories = NoCategories,
                    ExitArmed = exitArmed,
                    RawSelector = testsVar,
                    Warnings = warnings1,
                };
            }

            return new AutorunConfig
            {
                Enabled = true,
                IsAll = false,
                Categories = categories,
                ExitArmed = exitArmed,
                RawSelector = testsVar,
                Warnings = new List<string>(),
            };
        }
    }
}
