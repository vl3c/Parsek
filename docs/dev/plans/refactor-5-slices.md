# Refactor-5 Execution Roadmap (Slice Index)

**Date:** 2026-06-14.
**Status:** Planning roadmap. No production code has been changed. These slices are
to be implemented from a checkout that can build + run the xUnit gate (the remote
audit container has no .NET SDK). Each slice is a separate proposal doc.
**Parent audit:** `docs/dev/plans/refactor-5-inventory.md`.
**Rules of record:** `docs/dev/refactor-guidelines.md`.

## Hard Constraint (applies to every slice)

**Zero logic change.** Allowed work is contiguous block extraction into well-named
same-file helpers, true byte-identical deduplication, and (Pass 2 only) moving a
proven-identical block to a new owner behind compatibility wrappers. For every
change:

- No condition added/removed/reordered; no comparison changed; no new branch.
- No call reordered; extracted code runs from the exact original position.
- **No existing `ParsekLog` line changed** (level, tag, format, fields, order). Log
  text is behavioral here — changing it is a logic change. New observational logs
  are out of scope for these slices.
- Embedded `return`/`continue`/`break` preserved via `if (TryX(...)) return;` /
  `out`-result patterns (checklist item 4), never by moving the statement.
- No access modifier changed on any pre-existing member (item 7). If `internal`
  testability would require touching a pre-existing modifier, scale the extraction
  back to `private` (item 13).
- No loop split (item 8). No coroutine body restructured beyond tiny non-control
  helpers (item 6).
- If an extraction needs flags / mode enums / a large parameter bag / a struct
  harder to read than the original — **skip it and record why** (Conflict
  Resolution Principle).

## Universal Validation + Review Gate (per file / per logical unit)

1. `dotnet build Source/Parsek/Parsek.csproj` — succeeds, no new warnings from the
   change.
2. The unit's focused test filter (named in each slice) — all green.
3. `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`
   — the full non-injection gate stays green.
4. **Clean-context review agent** (Opus, fresh context) given ONLY: the checklist
   path, the commit hash, the file, and a one-line claim. It independently verifies
   zero logic change (see `refactor-guidelines.md` §"Example review agent dispatch").
5. One logical commit per file/unit. Update `refactor-5-inventory.md` status.

If any step is red, **do not patch forward** — revert the unit and retry narrower.

## Slice Ordering

Ordered by ascending risk. Land the pure, headless, single-file work first; defer
everything runtime/IMGUI/Harmony until a slice explicitly budgets in-game
validation.

| Slice | Doc | Scope | Risk | Validation |
|-------|-----|-------|------|------------|
| 1 | `refactor-5-slice1-proposal.md` | 3 big **pure** single-file method extractions (RouteBuilder / MissionLoopUnitBuilder / RenderSessionState) | Low–med (one order-sensitive) | xUnit (pure) |
| 2 | `refactor-5-slice2-proposal.md` | **Pure** repeated-block dedups (settings persistence, switch-segment refuse, route-codec loaders, recovered-credit sum, analysis reject) | Low | xUnit (pure) |
| 3 | `refactor-5-slice3-proposal.md` | `RouteIds.Short` cross-cutting owner (4 byte-identical copies) | Low (touches ~8 files) | xUnit (pure) |
| 4 | `refactor-5-slice4-proposal.md` | **Pass 2** cross-file owners (RouteNodeCodec, anchor world-frame factory, live tank iterator, IsFinite util, log-suppression scope) | Med–high (byte-order / runtime) | xUnit + in-game where noted |
| 5 | `refactor-5-slice5-proposal.md` | Remaining **pure** same-file phase extractions (Missions, anchor resolver, route store/harvest/analysis, terminal-orbit safety, sidecar/FX codecs) | Low–med | xUnit (pure) |
| 6 | `refactor-5-slice6-proposal.md` | **Runtime / IMGUI / Harmony** extractions — DEFERRED, require in-game validation | Med (no headless net) | in-game + pure-helper xUnit |

Dependency notes: Slice 3 (`RouteIds.Short`) is independent and can land any time.
Slice 4's `RouteNodeCodec` is the single riskiest item (two frozen on-disk
surfaces) — do it alone, gated on the serialization round-trip suites. Slices 2 and
5 are independent of each other and of Slice 1.

## Cross-Cutting "Leave Alone" List (do NOT refactor)

Recorded so a future pass doesn't re-litigate these:

- `Display/GhostTrajectoryPolylineRenderer` per-frame Driver orchestration — value <
  risk; runtime-only, visual-contract-bound (8b/8e/8f).
- `RecordingTreeSplitter` `SplitOriginAtRewindUT` / `RunPostSplitSteps` /
  `RollBackInMemory` — ordered crash-recovery; call order IS the contract.
- The Reaim numerical kernel (`UvLambert`, window planner, arrival solver) and
  `Rendering/OutlierClassifier.OutlierThresholds.Default` — math-/hash-sensitive;
  thresholds are hashed into the pannotations config-hash (moving them breaks
  serialization).
- The ledger-walk order in `RouteOrchestrator` / `GameActions/RouteModule` — row
  order + currency mutation timing are behavior-critical.
- The three render/ledger tracers' duplicated formatters (`GhostRenderTrace` /
  `MapRenderTrace` / `LedgerTrace`) — CLAUDE.md explicitly defers the shared
  `RenderTraceFormat` owner and forbids touching `GhostRenderTrace.cs`.
- `RouteProofHasher.ComputeRouteProofHashFromRecording` append order — a frozen
  fingerprint; reorder changes the hash.

## Scope Boundary

These slices cover **files created since refactor-4** only. The legacy giants that
kept growing (`ParsekFlight`, `GhostMapPresence`, `FlightRecorder`, …) remain
tracked by `refactor-remaining-opportunities.md` — a separate, higher-risk effort.
