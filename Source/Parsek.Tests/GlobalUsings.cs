// Project-wide usings for the Parsek.Tests assembly.
//
// The pure analyzer core (types, rules, registry, evaluator) lives in Parsek.dll
// under Parsek.Analyzer / Parsek.Analyzer.Rules (moved there by module M-A3 so the
// in-game H5 category can reuse it). The ~27 analyzer test files stay in this
// assembly under namespace Parsek.Tests.Analyzer(.Rules); these global usings let
// them reference the moved core by simple name without a per-file using edit.
global using Parsek.Analyzer;
