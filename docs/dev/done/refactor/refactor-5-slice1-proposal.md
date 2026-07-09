# Refactor-5 Slice 1 Proposal — Pure Same-File Method Extractions

**Date:** 2026-06-14.
**Status:** Proposal. Implementation-ready, but NOT yet implemented. Every change
here is **behavior-preserving (zero logic change)** and must pass the validation +
clean-context review gate in §5 before it lands.
**Parent audit:** `docs/dev/refactor-5/refactor-5-inventory.md` (Slice 1).
**Rules of record:** `docs/dev/refactor-guidelines.md` (the checklist a clean
reviewer applies) and the Refactor-4 plan's Extraction Rules.

## 1. Hard Constraint — Zero Logic Change

This is a **Pass 1** slice in the refactor methodology's sense: contiguous blocks
lifted into well-named **private/internal static helpers in the same file**, with
**no behavior change of any kind**. Concretely, for every extraction:

- No condition added, removed, reordered, or its comparison changed.
- No call reordered. Extracted code is invoked from the exact original position.
- **No existing `ParsekLog` line changed** — same level, same tag, same format
  string, same interpolated fields, same call order. (Log text is part of the
  behavioral contract here; changing it is a logic change, not an observational
  one. New observational logs are out of scope for this slice.)
- Embedded `return`s preserved via the `if (TryX(...) is { } reject) return reject;`
  / `out`-result pattern (checklist item 4), never by moving the return.
- No access modifier changed on any pre-existing member (checklist item 7).
- Static-correctness preserved (extracted helpers that touch no instance state are
  `static`; checklist item 12).
- If an extraction would need a flag/mode enum/large parameter bag or a struct that
  is harder to read than the original, **skip it and record why** (Conflict
  Resolution Principle). A `private` method that can't be unit-tested is better
  than an `internal` one bought by breaking a rule.

These three files were chosen because they are **pure** (no Unity / IMGUI / Harmony
/ save-load coupling), so the full headless xUnit gate is a real safety net.

## 2. Target A — `Logistics/RouteBuilder.BuildRoute` (canary)

`BuildRoute` is a single ~520-line pure method (verified by full read). It builds a
`Route` from an analysis result + tree + inputs, with 8 fail-fast reject gates
interleaved with construction phases. It has no static mutable state (only
`private const string Tag`) and no Unity coupling.

**Important correction to the audit note:** the 8 reject gates do **not** share a
log line — each `ParsekLog.Info` is distinct (different fields). So a generic
`Reject(reason)` factory that logs is **rejected** (it would change log text). At
most, the bare `return new RouteBuildOutcome { RejectReason = "…" };` construction
could be a trivial helper, but that saves ~2 lines per site for near-zero value —
**not worth it**. The value is in **phase extraction**, leaving every reject gate
and its exact log in place.

Proposed extractions (each contiguous, call-order-preserving):

| Helper (new `private static`) | Source lines | Returns | Embedded return? |
|------|------|------|------|
| `TryResolveRouteOrigin(...)` | 298–397 | origin discovery → `out RouteEndpoint origin, out string originLabel, out bool isHarvestOrigin`; returns the `endpoint-missing` `RouteBuildOutcome` or `null` | Yes — the `endpoint-missing` reject at 388–397. Caller: `if (TryResolveRouteOrigin(...) is { } reject) return reject;` |
| `BuildRouteSourceRefs(...)` | 226–289 | `(List<RouteSourceRef> sourceRefs, List<string> recordingIds)` (or `out` pair) | No |
| `BuildRouteCostManifests(...)` | 420–459 | `(Dictionary<string,double> costManifest, List<InventoryPayloadItem> inventoryCostManifest)` | No |
| `LogBuiltRoute(...)` | 543–568 | void; the final summary `ParsekLog.Info` verbatim | No |

Deliberately left inline:
- All 8 reject gates + their distinct logs (lines 61–72, 75–83, 116–123, 158–169,
  171–176, 186–202, 388–397, 476–483).
- The cadence round-then-rederive (204–224) and the dock-span guard (468–485) —
  short, dense, and load-bearing; extraction adds no clarity.
- `creationTreeRecordingIds` build (496–504) — already trivial.
- The final `Route { … }` object initializer (506–541).

`ReduceCostManifestByHarvested`, `IsSurfaceSituation`, `IsDockUTWithinSpan`,
`DefaultIdFactory` already exist as helpers — untouched. Note `IsSurfaceSituation`
is an int-bitmask check and is **not** equivalent to
`RouteEndpointResolver.IsSurfaceSituation(Vessel.Situations)` — do not dedup them.

**Net effect:** `BuildRoute` drops from ~520 to roughly ~300 lines, with four named
phases. Pure; no signature change to `BuildRoute` itself.

**Validation:** `dotnet test --filter "FullyQualifiedName~RouteBuilderTests"`, then
the slice-wide `FullyQualifiedName~Logistics` + the full non-injection gate (§5).

## 3. Target B — `MissionLoopUnitBuilder.TryBuildMissionUnit`

A single ~537-line pure method with numbered phases 1–8 (per the audit read).
Highest readability win, but **order-sensitive**: each phase mutates running locals
(`phaseAnchorUT`, `effectiveCadence`, the schedule/plan/hold), so the review must
enforce checklist items 2 (extraction position) and 5 (grouped-block mutations
don't change which later block fires). Boundaries below are from the audit read and
**must be re-confirmed against the code at implementation time**.

Proposed extractions:

| Helper | Approx source | Notes |
|------|------|------|
| `TryApplyReaim(... out …)` | ~327–572 (the `if (!phaseLocked)` body) | The big one. Mutates anchor/cadence/schedule/plan/hold via `out`/`ref`. Single contiguous block; **no internal reordering**. |
| `LogReaimDiagDump(...)` | ~387–414 | Pure diagnostic log block, verbatim. |
| `LogReaimPerCutDump(...)` | ~460–468 | Pure diagnostic log block, verbatim. |
| `LogMissionUnitSummary(...)` | ~592–645 | The PhaseLock APPLIED/SKIPPED summary, verbatim. |

Leave the digest cluster (`BuildSignature` / `AppendTransitedBodyDigest` /
`AppendStationAnchorDigest`) untouched — already factored.

**Validation:** `dotnet test --filter "FullyQualifiedName~MissionLoopUnitBuilderTests|FullyQualifiedName~MissionZeroDriftScheduleTests|FullyQualifiedName~MissionLoiterKnobTests|FullyQualifiedName~MissionScheduleGuardTests"`,
then the full non-injection gate. The order-sensitivity makes the review the
load-bearing gate — do this file **after** the canary proves the workflow.

## 4. Target C — `Rendering/RenderSessionState.RebuildFromMarker`

A ~370-line pure/headless method (delegate-injected `treeLookup` /
`liveWorldPositionProvider` / `SurfaceLookupOverrideForTesting`). It is a **strict
ordered guard cascade** (marker-null → no-recordings → origin-missing →
no-parent-BP → no-siblings → live-vessel-missing → live-no-point → live-body) then a
cohesive sibling-anchor write loop, each guard with its own `Pipeline-Session` /
`Pipeline-Anchor` log + `Clear(reason)`.

Proposed extractions:
- Lift each guard block into a `bool`/`Try*` helper that returns "handled →
  stop" (preserving the `Clear(reason)` + log exactly). Caller becomes a readable
  cascade of `if (TryX(...)) return;`.
- **Keep the sibling-anchor write loop whole** (no loop splitting — checklist
  item 8).
- Secondary, same pass: `TryEvaluatePerSegmentWorldPositions` (~178 lines, @1028).

The logging tests pin the exact `Pipeline-Session` / `Pipeline-Anchor` lines and a
one-shot L18 audit, so any accidental log/flow change fails a test immediately —
the strongest behavior lock of the three.

**Validation:** `dotnet test --filter "FullyQualifiedName~RenderSessionStateTests|FullyQualifiedName~RenderSessionStateLoggingTests"`,
then the full non-injection gate.

## 5. Validation + Review Gate (per file, in order)

Sequence: **C-canary discipline applies** — land **A first** (lowest risk: pure,
not order-sensitive), confirm the workflow, then **C**, then **B** (order-sensitive,
highest review burden). One file per commit.

For each file:

1. `dotnet build Source/Parsek/Parsek.csproj` — succeeds, no new warnings from the
   change.
2. The file's focused filter (above) — all green.
3. `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`
   — the full non-injection gate stays green.
4. **Clean-context review agent** (Opus, fresh context, given ONLY the checklist
   path + commit hash + file + a one-line claim — see `refactor-guidelines.md`
   §"Example review agent dispatch"). It independently verifies zero logic change.
5. Commit (one logical commit per file). Update `refactor-5-inventory.md` status.

If a step is red, do not patch forward — revert the unit and retry with a narrower
extraction (Rollback Strategy).

## 6. Out Of Scope For This Slice

- The repeated-block dedups (Slice 2), the `RouteIds.Short` owner (Slice 3), and
  all Pass2 cross-file owners (Slice 4+).
- Any runtime/IMGUI/Harmony extraction (needs in-game validation, not this slice).
- Any new logging, magic-value centralization, or signature changes to the public
  `BuildRoute` / `TryBuildMissionUnit` / `RebuildFromMarker` entry points.

## 7. Environment Note

The remote audit container has **no .NET SDK / mono / msbuild**, so the §5 build +
test gate cannot run here — it must run in a checkout that can compile Parsek. Code
edits made without that gate are unverified and must not be merged until the gate is
green. This proposal is therefore the landed deliverable for the no-build
environment; implementation proceeds where validation is possible.
