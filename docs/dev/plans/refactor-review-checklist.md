# Refactor Review Checklist

Dispatch a clean-context review agent after every file is refactored. The agent reads the git diff and verifies each item below.

## Checklist

1. **Condition inversions** — verify De Morgan's law applied correctly on every guard-clause inversion (`if (A && B) { body }` → `if (!A || !B) return; body`). Check short-circuit evaluation order (null checks before member access).

2. **Extraction position** — extracted code is called from the EXACT same position in the original method. No reordering of calls.

3. **No logic changes** — no conditions added, removed, or reordered. No new branches. No changed comparisons.

4. **Control flow preserved** — break/continue/return semantics intact. Methods that had `return` from the parent must correctly propagate (e.g., `if (TryHandle()) return;` pattern).

5. **Grouped blocks** — when multiple sequential blocks are grouped into one method, verify mutations in block N don't cause block N+1 to fire when it shouldn't have in the original code.

6. **Coroutines untouched** — IEnumerator methods have zero structural changes (logging additions only).

7. **Access modifiers unchanged** — no pre-existing method had its access modifier changed. Only newly extracted methods get access modifiers.

8. **No loop splitting** — single loops not split into multiple method calls.

9. **Logging is observational** — added ParsekLog calls have no side effects, no control flow impact, no allocations in hot paths without guards.

10. **Deduplication correctness** — when code was deduplicated into a shared method, verify both original blocks were semantically identical.

11. **Parameter passing** — no instance fields converted to method parameters unnecessarily. No closures changed to parameters. `ref`/`out` only when the caller needs the mutation back.

12. **Static correctness** — methods marked `private static` or `internal static` don't access instance state. Methods that access instance fields are `private` (non-static).

## How to review

```
git log --oneline -N  # find the commit(s) to review
git show <hash>       # full diff for each commit
```

For each diff hunk:
- Find the original code (the `-` lines)
- Find the new method (the `+` lines)
- Verify semantic equivalence

Report: PASS/FAIL per item, exact line numbers for any failures, concerns even if not outright bugs.
