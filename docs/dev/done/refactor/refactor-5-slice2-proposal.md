# Refactor-5 Slice 2 Proposal — Pure Repeated-Block Dedups

**Date:** 2026-06-14. **Status:** Proposal (not implemented).
**Roadmap:** `docs/dev/refactor-5/refactor-5-slices.md` (shared rules + the universal
validation/review gate apply here verbatim).

Scope: byte-identical repeated blocks within a single file, folded into one private
helper. All targets are **pure** (ConfigNode / string / funds math; no Unity).
Line numbers are as-of-audit (2026-06-14) and must be confirmed against the code at
implementation time. **Deduplicate only after reading both blocks and confirming
they are semantically identical** (checklist item 10); where a block's log text or
mutated target differs, the difference must become a helper parameter, never be
flattened away.

## Targets

### 2.1 `ParsekSettingsPersistence.cs` — highest dedup density in the audit

Four patterns, each repeated ~6×:

- `LoadIfNeeded` (@87, ~117): the per-key "read value → `bool.TryParse` → store or
  verbose-default" block ×6 → `private static void TryLoadBool(ConfigNode root,
  string key, ref bool? stored)`.
- `ApplyTo` (@246): the per-setting "if `stored.HasValue && != current`: capture
  prev, assign, Info-log" block ×6 → `ApplyBoolOverride(string name, bool? stored,
  …)` (needs a setter; if a setter delegate makes it harder to read than the
  original, keep inline and record why).
- The three `RecordGhost/Map/LedgerTracing` methods are byte-identical except the
  backing field + key → `RecordTracingFlag(ref bool? stored, bool value, string
  name)`.
- `Save` (@394): the ~10 near-identical `if (x.HasValue) AddValue(...)` lines —
  fold only if a helper reads cleaner than the originals.

Pure; headless via the existing `*ForTesting` seams. The `int?` path is already
`ParseStoredInt`. `loaded`/`stored*` static state has `ResetForTesting`.
**Validate:** `--filter "FullyQualifiedName~SettingsPersistence|FullyQualifiedName~ParsekSettingsPersistence"`.

### 2.2 `SwitchSegmentBuilder.CreateSwitchContinuationSegment` (@344, ~160)

A 5-line precondition-failure block repeated 6×: `result.FailureReason = "…";
LogCreationRefused(result, …8 args…); return result;` → one
`private static SwitchContinuationCreationResult Refuse(result, reason, <payload>)`.
The 8-arg `LogCreationRefused` payload is identical across the sites; only `reason`
varies. Leave the build phases (3–10) inline. Pure, headless.
**Validate:** `--filter "FullyQualifiedName~SwitchSegmentBuilder|FullyQualifiedName~SwitchContinuation"`.

### 2.3 `Logistics/RouteCodec.DeserializeFrom` (@238)

- The "GetValue then if-empty-set-null" idiom for `LinkedRouteId`,
  `BackingMissionTreeId`, `DockMemberRecordingId`, `PendingRecoveryCreditCycleId`,
  `LastHoldDetail` (5 sites) → `private static string NullIfEmpty(string)`.
- The two byte-identical repeated-value loaders (`EXCLUDED_INTERVALS`,
  `CREATION_TREE_RECORDINGS`) → `LoadStringList(ConfigNode node, string nodeName,
  string valueKey, ICollection<string> target)`.

**Do NOT** touch the scalar `AddValue`/`GetValue` ordering in either codec half —
byte-order contract (gen-4 schema, no migrations). Pure.
**Validate:** `--filter "FullyQualifiedName~RouteCodec"` (round-trip + reject tests).

### 2.4 `RouteProofCodec.cs` — value-key constants (mechanical tidy)

Host the inline value-key literals (`"WINDOW"`, `"ITEM"`, `"RESOURCE"`, `"pid"`,
`"name"`, `"amount"`, `"maxAmount"`, `"STORED_RESOURCES"`) in a constants block, to
match `RouteCodec`'s documented "rename touches one place" convention. This is a
literal-hoist only — every string value stays identical. Pure.
**Validate:** `--filter "FullyQualifiedName~RouteProofSerialization"`.

### 2.5 `Logistics/RouteRunCostCalculator.cs`

- `SumRecoveredCredits` (@99, Route-keyed) vs `SumRecoveredCreditsForCandidate`
  (@337, Route-less) are documented as "identical predicate, logged without a route
  id" → share a core taking the tree-id set + a log-context string.
- `ResolveTreeRecordingIds(Route)` (@154) and `ResolveTreeRecordingIds(RecordingTree)`
  (@311) share the foreach-into-HashSet tail → make the `Route` overload resolve
  the tree then delegate to the tree overload.

Pure funds math. **Validate:** `--filter "FullyQualifiedName~RouteRunCost"`.

### 2.6 `Logistics/RouteAnalysisEngine.AnalyzeWindow` (@207, optional)

~6 `Diag(...) + return new RouteAnalysisResult{Status, SourceRecording=source,
ConnectionWindow=window}` reject blocks → a `RejectResult(RouteAnalysisStatus,
source, window)` factory. **Only fold the bare result construction** — if the
`Diag` messages differ per reject (confirm at implementation), they stay inline
exactly as written. Low value; include only if the blocks prove identical.
**Validate:** `--filter "FullyQualifiedName~RouteAnalysisEngine"`.

## Commit Strategy

One commit per file (2.1, 2.2, 2.3+2.4 together as RouteCodec/RouteProofCodec
codec-tidy, 2.5, optionally 2.6). Each gets its own focused filter + the full
non-injection gate + a clean-context review.

## Explicitly Skipped

- `RouteProofHasher` append order (frozen fingerprint).
- The codec big-method scalar lists (`SerializeInto`/`DeserializeFrom` field order).
- `LedgerGroundTruthDiff` seeded-pool comparators — near-identical but per-facet
  `Has*`/`Detail` strings differ; keep deliberately duplicated until owner tests
  exist (RecordingsTableUI precedent).
