# Refactor Review Checklist

## Review Agent Requirements

- **Model:** Always Opus (`model: "opus"`) — non-negotiable for review reliability
- **Clean context:** Every review agent must be a fresh agent (no `resume`, no accumulated context from the refactoring). The reviewer must have zero knowledge of what was "supposed to" change — it only sees the diff and the checklist.
- **Prompt:** Give the agent ONLY:
  1. The path to this checklist file
  2. The commit hash(es) to review
  3. The file path(s) that were modified
  4. A brief description of what was claimed (e.g., "11 methods extracted, 7-copy deduplication")
- **Do NOT** tell the review agent the extraction rules, the plan, or any justification for the changes. It should discover whether the changes are correct independently.
- **Subagent type:** Use default (general-purpose), NOT Plan — the reviewer needs full tool access to read files and run git commands.

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

## Example review agent dispatch

```
Agent(
  model: "opus",
  description: "Review XYZ.cs refactor",
  prompt: """
    You are a code review agent. Your ONLY job is to verify that a refactoring
    introduced NO logic changes — only structural reshuffling and logging additions.

    Working directory: C:/Users/vlad3/Documents/Code/Parsek/Parsek-code-refactor/

    1. Read docs/dev/plans/refactor-review-checklist.md for the full checklist
    2. Run: git show <COMMIT_HASH>
    3. Read every diff hunk. For each change, verify it against the checklist.
    4. For deduplication claims, read BOTH original blocks and verify they were identical.
    5. Report PASS/FAIL per checklist item with exact line numbers for any issues.

    File reviewed: Source/Parsek/XYZ.cs
    Commit: <HASH>
    Claimed changes: <brief description>
  """,
  run_in_background: true
)
```
