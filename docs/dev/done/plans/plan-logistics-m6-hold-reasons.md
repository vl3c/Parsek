# Plan: Logistics M6 slice - live route hold-reason legibility

**Branch:** `logistics-m6-hold-reasons` (origin/main tip, post-M1 PR #1118).
**Spec:** `docs/parsek-logistics-supply-routes-design.md` 19.4 M6 first bullet (near-misses shipped; LIVE hold states remaining). Flow-closure reasons are M3 - out of scope.

## 1. Summary + scope

A live route that cannot dispatch currently explains itself only in KSP.log (the `BLOCKED kind=... reason=...` line). This slice persists the last hold reason on the `Route`, captures it at the existing block chokepoints (zero new computation), and renders it in player language in the Logistics window: a yellow detail-panel line plus a status-cell tooltip augmentation. A new pure formatter maps `EligibilityFailureKind` + the existing reason tokens to plain-ASCII player text, sitting beside `LogisticsRejectPresentation`.

**In scope**
- Additive sparse `Route` fields + codec: last hold kind / detail token / shortfall / UT.
- Capture at the loop-path blocked branch, the legacy wait/endpoint-lost appliers, and the endpoint-lost-at-delivery branch; clear on successful cycle and player Activate.
- New pure `LogisticsHoldPresentation` formatter + detail-panel line + status-cell tooltip; hold text built in the existing ~1 Hz legibility cache.
- xUnit: codec round-trip, blocked-crossing capture/clear fixtures, formatter table.

**Out of scope**
- Flow-closure rejection reasons (M3 territory per the spec).
- Near-miss list rework (already shipped; untouched).
- Route-row visible-text/column redesign (stretch note in section 3.4; the detail panel + tooltip suffice for v1).
- `MissingSourceRecording` / `SourceChanged` extra detail: `RouteStore.RevalidateSources` already transitions with a descriptive cause (`Source/Parsek/Logistics/RouteStore.cs:452`) and `StatusReason` already names the player action; no new storage for them.
- Missions/ layer (LOCKED), M-MIS-9, ledger row changes (recorded data immutable), ERS/ELS allowlist changes.

## 2. Current-state findings

Authoritative spec: `docs/parsek-logistics-supply-routes-design.md:1641-1645` (M6 first bullet) and `:1683` (19.6 doctrine rule 9, "every non-dispatching route names its reason in player language"). Phase 4 edits 19.4's M6 bullet (`:1645`), not 19.5.

**Where the hold decision lands per tick**
- Loop path (every v0 route; `Route.IsLoopRoute`, `Source/Parsek/Logistics/Route.cs:274`): `RouteOrchestrator.ProcessLoopRoute` calls `RouteDispatchEvaluator.CheckEligibility` at `Source/Parsek/Logistics/RouteOrchestrator.cs:666-667`; the blocked branch (`:669-691`) flushes the recovery credit, bumps `SkippedCycles` (`:683`), snaps `LastObservedLoopCycleIndex` (`:684`), and logs the `BLOCKED kind={elig.Kind} reason={elig.Reason} shortfall={elig.Shortfall}` Info line (`:686-690`). **It does NOT transition status** - a blocked loop route stays `Active`. The eligible branch fires `EmitLoopCycle` (`:697`) and logs FIRED (`:710-713`) or replay-backstop (`:717-721`).
- Legacy self-timer path (dead for v0 loop routes but kept, `Route.cs:266-273`): `EvaluateRoute` maps `CheckEligibility` failures to decisions (`Source/Parsek/Logistics/RouteDispatchEvaluator.cs:114-145`); `ApplyWait` (`RouteOrchestrator.cs:1399-1415`) and `ApplyEndpointLost` (`:1424-1446`) call `TransitionTo(decision.NextStatus, decision.Reason)` - the reason reaches only the log. `ApplyDelivery`'s endpoint-lost-at-delivery branch transitions at `:1571`.
- `Route.TransitionTo` (`Route.cs:324-336`) logs the reason and discards it - verified, nothing stored.
- One-status-one-chokepoint check: `WaitingForResources`/`WaitingForFunds`/`DestinationFull`/`EndpointLost` all funnel through `ApplyWait`/`ApplyEndpointLost` on the legacy path; on the loop path there is no status change at all - the single capture point is the blocked branch. `Paused` is a player action (`TryPause`, `:280`); `SourceChanged`/`MissingSourceRecording` come from `RouteStore.RevalidateSources` (`RouteStore.cs:452`). There is no single shared chokepoint; there are exactly four orchestrator capture sites (loop blocked, ApplyWait, ApplyEndpointLost, endpoint-lost-at-delivery).

**Reason detail available per kind** (`RouteDispatchEvaluator.cs:161-223`, `EligibilityResult` `:42-58`):

| Kind | `Reason` token at the block site | Extra |
|---|---|---|
| `SourcesStale` | `"sources-stale"` (also defensive `"null-route"`/`"null-env"`) | - |
| `EndpointLost` | `"stop-{i}-{epReason}"` (`:186`) or `"origin-{originReason}"` (`:194`); resolver reasons: `body-unresolved`, `pid-miss-no-surface-fallback`, `no-live-vessels`, `no-surface-candidate`, `no-vessel-within-radius` (`Source/Parsek/Logistics/RouteEndpointResolver.cs:70,91,123,155,160`) | - |
| `OriginLacksCargo` | the bare short resource name, or `"inventory-origin-debit-unsupported"`, or `"origin-unresolved:<reason>"` (`Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs:118,143,159`) | have/need are computed only inside the env's log line (`:160-171`), NOT returned through `EligibilityResult` - surfacing them would require an `IRouteRuntimeEnvironment` signature change touching every fake; **skip for v1** (state resource name only) |
| `FundsShort` | `"funds-short"` | `EligibilityResult.Shortfall` carries the funds shortfall (`:210`) - free to display |
| `DestinationFull` | the full resource name | production env is a v0 stub returning `true` unconditionally (`LiveRouteRuntimeEnvironment.cs:202-221`; capacity is enforced at apply time by partial fill). The kind is reachable only via test fakes / a future un-stub - the formatter must still map it (named + unnamed resource) |

Legacy-path tokens differ: `RouteDispatchDecision` factories prefix them - `"origin-lacks-{X}"`, `"funds-shortfall-{N}"`, `"destination-full-{X}"` (`Source/Parsek/Logistics/RouteDispatchDecision.cs:86-97`); the delivery-time loss uses `"endpoint-destroyed-at-delivery:<reason>"` (`RouteOrchestrator.cs:1565`). The formatter must be total over both shapes.

**Persistence conventions** (`Source/Parsek/Logistics/RouteCodec.cs`): sparse additive scalars - `dispatchPriority` written only when != 0 (`:113-116`), read with default + clamp (`:253-256`); `pendingRecoveryCreditCycleId`/`...DispatchUT` written only when set (`:130-138`), read with null/-1 defaults (`:273-281`); enum-by-name with warn-and-default parse (`ParseStatusOrWarn`, `:669-684`); doubles `ToString("R", InvariantCulture)` throughout.

**UI surface** (`Source/Parsek/UI/LogisticsWindowUI.cs`):
- Status cell renders `StatusReason(route.Status)` with the raw enum as tooltip (`:744-757`); detail panel repeats it (`:1189`); `StatusReason` is the pure per-status map (`:2907-2922`) - generic text, no per-route detail.
- `DrawRouteDetail` (`:1178-1252`) is the M1-pattern home: status line `:1189`, `DestinationFull` capacity context line `:1202-1203`, countdown `:1216-1219`, `DrawPriorityStepper` (`:1636-1669`, deferred-mutation idiom `:1846-1856`). This slice is display-only - no new deferred mutation needed.
- Legibility cache: `RouteLegibility` struct (`:126-170`), rebuilt wholesale at ~1 Hz (`RefreshLegibilityCacheIfDue` `:2202-2240`), per-route build in `ComputeRouteLegibility(route, currentUT)` (`:2251-2325`), draw path reads only (`GetLegibility` `:2554-2575`, cache-miss default must not flash wrong state). Player actions force recompute via `routeStateMutated` (`:1793-1795`, `:1858-1859`) - orchestrator-side mutations (like a new hold) are picked up by the 1 Hz timer with no invalidation change, same as `SkippedCycles`/badge today. Sort keys read `StatusReason` (`:2659-2661`) - unchanged by this slice.
- Formatter siblings: `RouteCreationFormatters.FormatRejectMessage` (`Source/Parsek/Logistics/RouteCreationFormatters.cs:174-195`) and `LogisticsRejectPresentation.DescribeNearMiss` (`Source/Parsek/UI/LogisticsRejectPresentation.cs:40-51`) - pure `internal static`, Unity-free, player-language, unit-tested (`Source/Parsek.Tests/Logistics/LogisticsRejectPresentationTests.cs`).

**Tests to mirror**
- `Source/Parsek.Tests/Logistics/RouteLoopDeliveryFireTests.cs`: `BlockedEnv` fake (`:159-178`, note `KscFundsAvailable` currently hardwired true), blocked-crossing fixture asserting `SkippedCycles` + BLOCKED log (`:418-440`), fired-crossing fixture (`:188-226`).
- `Source/Parsek.Tests/Logistics/RouteCodecTests.cs`: `RoundTrip_FullyPopulated` (`:173+`) + builder `.WithDispatchPriority(2)` (`:156`) - the M1 sparse-field template.
- `Source/Parsek.Tests/Logistics/RouteOrchestratorTests.cs` (legacy-path appliers), `RouteEntityTests.cs`, presentation-test siblings for the formatter.

## 3. Design decisions

### 3.1 Persistence shape (additive, sparse)

Four new public fields on `Route` (after `SkippedCycles`, `Route.cs:184`):

| Field | Type | Default | Codec key (sparse rule) |
|---|---|---|---|
| `LastHoldKind` | `RouteDispatchEvaluator.EligibilityFailureKind` | `None` | `lastHoldKind`, name string, written only when != None; unknown string on load -> Warn + `None` (mirrors `ParseStatusOrWarn`) |
| `LastHoldDetail` | `string` | `null` | `lastHoldDetail`, written only when non-empty (the raw evaluator token, verbatim) |
| `LastHoldShortfall` | `double` | `0.0` | `lastHoldShortfall`, `"R"` invariant, written only when > 0 (funds only) |
| `LastHoldUT` | `double` | `-1.0` | `lastHoldUT`, `"R"` invariant, written only when >= 0 |

Reusing the existing `EligibilityFailureKind` (nested in `RouteDispatchEvaluator`) avoids a parallel enum; it is serialized by name so no ordinal pinning is needed. Two small mutators on `Route` keep the audit-trail discipline of `TransitionTo`:
- `internal void RecordHold(EligibilityFailureKind kind, string detail, double shortfall, double ut)` - writes the four fields; logs Verbose **only when kind or detail changed** (UT alone refreshing is silent), so a route re-blocking on the same reason every crossing does not spam.
- `internal void ClearHold(string reason)` - resets to defaults; logs Verbose only when something was actually cleared.

### 3.2 Staleness semantics

- **Persisted across save/load** (a blocked route must still explain itself after reload).
- **Cleared on a successful crossing**: both the fired and the replay-backstop branches of `ProcessLoopRoute` (the crossing was eligible either way), and legacy `ApplyDispatch`.
- **Cleared on player Activate** (`TryActivate`): activation resets loop observation (`LastObservedLoopCycleIndex = -1`, `RouteOrchestrator.cs:188-192`), so a prior-session reason must not present as current. NOT cleared on Pause (it answers "why wasn't this delivering") and NOT cleared on Send Once (the armed crossing refreshes or clears it). Note (plan-review finding 5): `TryActivate` only accepts `Paused` routes (`:164-169`), and an `EndpointLost` loop route is skipped by the `GhostDriving` status gate (`:571`, `RouteStatusPolicy.cs:79-101`) so it never re-evaluates - the player recovery for broken routes is the Pause -> Activate two-step, which is exactly where the clear lands. Do NOT add a redundant clear to `TryPause`; that would defeat the keep-on-Pause semantics.
- **Display always carries age**: the detail line appends "checked {duration} ago" from `currentUT - LastHoldUT`, so even a reason held across a long warp reads as historical fact, not a live claim.

### 3.3 Capture sites (all in `RouteOrchestrator`; zero new computation)

| Site | Action |
|---|---|
| `ProcessLoopRoute` blocked branch (`:683-691`) | `route.RecordHold(elig.Kind, elig.Reason, elig.Shortfall, currentUT)` next to the SkippedCycles bump |
| `ProcessLoopRoute` eligible path | `route.ClearHold("crossing-eligible")` placed IMMEDIATELY AFTER the `if (!elig.Eligible)` block and BEFORE `EmitLoopCycle` (plan-review BLOCKER 1: clearing in the fired branch AFTER EmitLoopCycle returns would erase the endpoint-lost-at-delivery hold that `ApplyDelivery` records inside the same call - `EmitLoopCycle` returns true unconditionally at `:1018` even on that branch). One clear covers both the fired and replay branches; order-safe against site 5's RecordHold. |
| `ApplyWait` (`:1399`) | `RecordHold(HoldKindForOutcome(decision.Outcome), decision.Reason, 0, currentUT)` - new tiny pure map `WaitResources->OriginLacksCargo`, `WaitFunds->FundsShort`, `WaitDestinationFull->DestinationFull` (needs `currentUT` threaded in: change the private `ApplyWait(route, decision)` signature to take it; the caller at `:499` has it) |
| `ApplyEndpointLost` (`:1424`) | `RecordHold(EligibilityFailureKind.EndpointLost, decision.Reason, 0, currentUT)` |
| `ApplyDelivery` endpoint-lost-at-delivery (`:1559-1575`) | `RecordHold(EndpointLost, "endpoint-destroyed-at-delivery:" + reason, 0, currentUT)` |
| `ApplyDispatch` (`:1204`) | `ClearHold("dispatched")` |
| `TryActivate` (`:194`) | `ClearHold("player-activate")` |

`RouteStore.RevalidateSources` is deliberately untouched (scope-out above).

### 3.4 Display surface

v1 = **detail panel + tooltips; no row column/text change** (row treatment is the stretch note below).

- New `RouteLegibility` fields: `HoldText` (full detail-line sentence incl. age) and `HoldShort` (one-clause version for the tooltip). Built in `ComputeRouteLegibility` (`:2251`) on the ~1 Hz pass - it already receives `currentUT`. Null when `LastHoldKind == None`. **Status display gate (plan-review MAJOR 2): also null when `route.Status` is `MissingSourceRecording` or `SourceChanged`** - those statuses already explain themselves and a persisted older hold (e.g. an OriginLacksCargo from before the source changed) would actively mislead; mirrors the existing `CapacityContext` status gate (`:2296`). Holds DO display for `Active`, the three wait states, `EndpointLost`, `InTransit`, and `Paused` (keep-on-Pause answers "why wasn't this delivering"). Persistence stays unconditional - only display gates. `GetLegibility`'s cache-miss default leaves both null (no flash). No new cache invalidation: the 1 Hz timer already covers orchestrator-side mutations. The detail line must condition ONLY on the cached `leg.HoldText`, never on live `route.*` state combined with it (IMGUI layout/repaint control-count stability, plan-review NIT 8). New comments are tagged "M6 hold reasons" - the bare "M6" tag is already used in this file for unrelated Send-Once-provenance work (`:177`, `:771-773`, `:1094`, `:2211`).
- `DrawRouteDetail`: after the Status line (`:1189`), when `leg.HoldText != null`, draw `DetailLine(leg.HoldText, statusStyleYellow)` - e.g. `Last cycle blocked: origin is out of LiquidFuel (checked 2m ago)`.
- Status cell (`:750` and `:755`): when `leg.HoldShort != null`, tooltip becomes `route.Status + "\n" + leg.HoldShort` (visible text and styles unchanged; sort keys unchanged).
- **Stretch note (not in this slice):** replace the Active-with-hold Status-cell visible text ("Dispatching on schedule") with a yellow `Blocked: {short reason}`. The yellow `FlyingNotDelivering` badge already flags the condition at row level, so v1 legibility does not depend on it.

### 3.5 Formatter mapping table

New pure static `Source/Parsek/UI/LogisticsHoldPresentation.cs` (sibling of `LogisticsRejectPresentation`; plain ASCII, no em dashes, InvariantCulture):

`internal static string DescribeHold(EligibilityFailureKind kind, string detail, double shortfall)` -> short clause (null for `None`); `internal static string FormatHoldDetailLine(string describe, double ageSeconds)` -> `"Last cycle blocked: {describe} (checked {age} ago)"` (age via the existing duration formatter; omit the suffix when age < 0).

| Kind | Detail token | Player text |
|---|---|---|
| `OriginLacksCargo` | bare resource name (loop) or `origin-lacks-X` (legacy, prefix-stripped) | `origin is out of {X} - delivers when the origin has the full amount` |
| `OriginLacksCargo` | `inventory-origin-debit-unsupported` | `this route carries stored inventory parts, which docked-origin routes cannot debit yet` |
| `OriginLacksCargo` | `origin-unresolved:*` | `origin vessel could not be found - it may have moved, been recovered, or been destroyed` (raw token kept in the detail-line tail) |
| `FundsShort` | any (`funds-short` / `funds-shortfall-N`) | shortfall > 0: `not enough funds at KSC - short {N} funds for this dispatch` (F0); else `not enough funds at KSC for this dispatch`. Accepted degradation (plan-review finding 4): the legacy `ApplyWait` capture stores shortfall 0 (`RouteDispatchDecision` has no shortfall field; the number lives only inside the `funds-shortfall-N` token), so legacy holds render the generic text - do NOT parse the token suffix; the legacy path is dead for v0 loop routes. Pin both shapes in `DescribeHold_LegacyPrefixedTokens`. |
| `DestinationFull` | resource name / `destination-full-X` | `destination has no room for {X}`; empty -> `destination has no room for the delivery` |
| `EndpointLost` | `origin-*` | `origin vessel could not be found` |
| `EndpointLost` | `stop-N-*` / `endpoint-destroyed-at-delivery:*` / other | `destination vessel could not be found - re-target or recreate the route` |
| `SourcesStale` | any | `route source recordings are unavailable right now` |
| any other / unknown | any | `route is blocked ({kind}: {token})` - total fallback, never throws, never blank |

## 4. Implementation steps (each phase ends green: `cd Source/Parsek && dotnet build`, `cd Source/Parsek.Tests && dotnet test`)

**Phase 1 - model + codec (commit 1)**
1. `Source/Parsek/Logistics/Route.cs`: four fields (3.1) + `RecordHold` / `ClearHold` with on-change Verbose logging, XML docs noting the sparse-codec + staleness contract.
2. `Source/Parsek/Logistics/RouteCodec.cs`: sparse serialize after the dispatchPriority block (`:113-116`); deserialize with defaults after `:256`; `ParseHoldKindOrNone` helper mirroring `ParseStatusOrWarn`.
3. Tests (section 5, group A).

**Phase 2 - capture wiring (commit 2)**
4. `Source/Parsek/Logistics/RouteOrchestrator.cs`: the seven set/clear sites from 3.3, plus the `internal static EligibilityFailureKind HoldKindForOutcome(RouteDispatchOutcome)` map (internal for direct testing) and the `ApplyWait` signature change to accept `currentUT`.
5. Tests (group B).

**Phase 3 - formatter + UI (commit 3)**
6. New `Source/Parsek/UI/LogisticsHoldPresentation.cs` (3.5).
7. `Source/Parsek/UI/LogisticsWindowUI.cs`: `RouteLegibility.HoldText/HoldShort` fields; build them in `ComputeRouteLegibility`; detail line in `DrawRouteDetail`; tooltip augmentation at `:750`/`:755`.
8. Tests (group C).

**Phase 4 - docs (folded into commit 3 or a final docs commit per the per-commit docs rule)** - see section 7.

## 5. Test plan

**A. Codec / model** (`RouteCodecTests.cs`, `RouteEntityTests.cs`)
- `RoundTrip_FullyPopulated` extended: builder gains `.WithLastHold(kind, detail, shortfall, ut)`; assert all four round-trip.
- `Defaults_WriteNoHoldKeys` - a default route's serialized node contains none of the four keys (sparse).
- `MissingHoldKeys_ReadAsDefaults` - pre-M6 save shape loads to None/null/0/-1.
- `UnknownHoldKind_MapsToNone_WithWarn` (log-sink assert).
- `RecordHold_SetsFieldsAndLogsOnChange` / `RecordHold_SameKindAndDetail_LogsOnce` / `ClearHold_ResetsAndLogsOnlyWhenSet` (canonical `ParsekLog.TestSinkForTesting` pattern, `[Collection("Sequential")]`).

**B. Orchestrator capture** (`RouteLoopDeliveryFireTests.cs`, `RouteOrchestratorTests.cs`)
- `BlockedCrossing_RecordsHoldReason` - extend the existing blocked fixture (`:418+`): after the blocked tick, `LastHoldKind == OriginLacksCargo`, `LastHoldDetail == "LiquidFuel"`, `LastHoldUT == tick UT`.
- `BlockedCrossing_FundsShort_RecordsShortfall` - extend `BlockedEnv` with a failing `KscFundsAvailable` (currently hardwired true at `:174`) that emits a NONZERO shortfall out-value, AND set `env.IsCareer = true` (settable at `:161`) - the funds gate only runs when `env.IsCareer && route.IsKscOrigin` (`RouteDispatchEvaluator.cs:205`); `OriginHasCargoResult` defaults true (`:162`) and `BuildLoopRoute` defaults `isKscOrigin: true` (`:104`), so the gate is reachable (plan-review finding 3 - without IsCareer the gate silently skips and the test passes vacuously).
- `FiredCrossing_ClearsHold` and `ReplayBackstop_ClearsHold` - pre-seed a hold, run an eligible crossing, assert cleared.
- `EndpointLostAtDelivery_HoldSurvivesEligibleCrossing` (plan-review BLOCKER 1 pin) - eligible crossing whose delivery half hits the endpoint-lost branch (fake env resolving the origin but failing `TryResolveEndpointVessel` for the destination at delivery time, or the seam equivalent); assert the `EndpointLost` hold recorded inside `EmitLoopCycle` is NOT wiped by the post-crossing clear.
- `ApplyWait_LegacyPath_RecordsHold` (non-loop route, `WaitResources` decision -> kind `OriginLacksCargo`, detail `origin-lacks-LiquidFuel`), `ApplyEndpointLost_RecordsHold`, `ApplyDispatch_ClearsHold`, `TryActivate_ClearsHold`.
- `HoldKindForOutcome_MapsAllWaitOutcomes` (pure map totality).

**C. Formatter / presentation** (new `LogisticsHoldPresentationTests.cs`)
- One test per table row in 3.5 (`DescribeHold_OriginShortResource`, `DescribeHold_InventoryUnsupported`, `DescribeHold_OriginUnresolved`, `DescribeHold_FundsShort_NamesShortfall`, `DescribeHold_DestinationFull_Named/Unnamed`, `DescribeHold_EndpointLost_OriginVsStop`, `DescribeHold_LegacyPrefixedTokens`, `DescribeHold_UnknownKindOrToken_FallsBack`, `DescribeHold_None_ReturnsNull`).
- Status display gate (MAJOR 2 pin): legibility/pure-helper test asserting `HoldText`/`HoldShort` are null for `MissingSourceRecording` and `SourceChanged` even with hold fields populated, and non-null for `Active`/wait states/`EndpointLost`/`Paused`.
- `FormatHoldDetailLine_AppendsAge` / `_OmitsNegativeAge`.
- `AllHoldStrings_ArePlainAscii` (guards the no-em-dash constraint).

## 6. Logging plan

- **No new Info lines and no duplication**: the loop BLOCKED line (`RouteOrchestrator.cs:686-690`), the legacy Wait/EndpointLost lines (`:1410-1414`, `:1442-1445`), and `TransitionTo` audit lines stay byte-identical.
- `Route.RecordHold`: one `ParsekLog.Verbose("Route", ...)` **on change of kind/detail only** (`hold recorded kind=... detail=... shortfall=R ut=R`); silent when only the UT refreshes - the VerboseOnChange-style guard is the field comparison itself, no keyed dict.
- `Route.ClearHold`: one Verbose on actual clear (`hold cleared reason=...`), silent when already clear.
- UI: no per-frame logging; the hold text rides the existing ~1 Hz legibility pass (optionally extend its batch-summary line with a `withHold=N` counter, matching the batch-counting convention).

## 7. Docs plan (per commit)

- `CHANGELOG.md` (Unreleased > Features, one line, plain ASCII): "The Logistics window now says why a route is not delivering: a blocked route's detail panel names the exact hold reason (origin out of a named resource, origin vessel missing, not enough funds, destination full, or stored-part cargo not yet supported) with how long ago it was checked."
- `docs/parsek-logistics-supply-routes-design.md`: M6 first bullet (`:1645`) flips to partially-shipped-with-as-built-note - live hold states now name their reason from the persisted last-hold fields (loop-path blocked crossings + legacy wait states); remaining: flow-closure rejection naming (M3-coupled) and the row-level visible treatment.
- `docs/dev/todo-and-known-bugs.md`: `## Done` entry describing capture/clear semantics.

## 8. Risks

1. **Token-shape drift between paths** - loop path stores bare tokens, legacy path stores prefixed ones; mitigated by the formatter's total fallback row + tests pinning both shapes. New tokens added later degrade to the readable fallback, never blank/throw.
2. **Stale-reason misread** - a route blocked once then warped past many crossings shows an old reason; mitigated by clear-on-eligible-crossing + clear-on-activate + the mandatory age suffix.
3. **`Route` referencing the evaluator-nested enum** - mild layering smell (data class -> evaluator type). Acceptable (same assembly, already-shipped enum); the alternative (hoisting `EligibilityFailureKind` to its own file) is a wider rename and not needed for correctness.
4. **`DestinationFull` is unreachable in production** (env stub returns true, `LiveRouteRuntimeEnvironment.cs:202-221`) - the mapping row could rot; pinned by formatter tests, and apply-time partial fills already surface elsewhere, so no player-facing gap.
5. **Save-format additions** - four sparse keys; pre-M6 saves load to defaults (tested), default routes write nothing, so no bloat and no migration. Enum-by-name keeps forward compatibility.
6. **UI regression surface** - changes are read-only draws + tooltip strings; sort keys and deferred-mutation paths untouched; `GetLegibility` default keeps null hold text so the first-frame flash class of bug cannot occur.
7. **ERS/ELS grep gate** - all new reads are `Route` fields; no new raw committed/ledger reads, allowlist untouched.
