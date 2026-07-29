# Test Coverage Audit — 2026-07-29

Full-stack audit of the three testing systems (xUnit unit tests, in-game runtime tests, automated flight harness) against the production surface at HEAD (`83e7ce5`). Successor to `test-coverage-audit-2026-04-19.md`; this one also audits the harness and the design documents' testing contracts, which did not exist / were not covered in April.

Method: eleven parallel investigation passes (one per subsystem slice, plus design-doc contract extraction, game-mode/craft feasibility, and visual-validation solution engineering), mechanically grepped and spot-verified at HEAD. Companion document: `design-testing-unified.md` (what the systems are, how they compose, and what to build next — recommendations live THERE, facts live here).

---

## 1. Executive summary

**The stack is unit-rich and live-proof-poor.** ~19,000 xUnit cases cover 77% of the 3,179 pure `internal static` methods, and the harness/seam machinery itself is the best-tested code in the repo — but the coverage thins out in exactly the order a player experiences the mod: pure decision (strong) → live KSP behavior (patchy) → flown scenario (narrow) → visuals (near zero).

Headline numbers:

| Metric | Value |
|---|---|
| xUnit: files / cases | 848 files, 16,266 `[Fact]` + 493 `[Theory]` (~19k cases), one explicitly-skipped Fact |
| xUnit: pure-method coverage | 2,463 / 3,179 `internal static` methods named in tests (77%) |
| In-game: declarations / categories | 539 `[InGameTest]` in 97 categories |
| In-game: categories driven unattended | **23 of 97** — 336 declarations in the 74 undriven categories execute in zero automated runs |
| Harness: committed scenarios | 55 (32 nightly, 18 daily, 5 operator) |
| Harness: registry coverage | **97 / 242 dimension cells claimed** (and claimed ≠ green: last measured 58 green of 70 claimed) |
| Harness: cost per claimed cell | batch lane 5.8–17.2 s/cell; flight lane 39.8–235.4 s/cell (4–20× cheaper in batch) |
| Nightly wall | 4.01 h, 67% of it in 5 flown scenarios |

The five findings that matter most:

1. **The largest coverage asset is already written and simply never executed.** The `Rewind` category (37 tests), 38 of 47 `Logistics` tests, `GhostLifecycle` (17), `CrewReservation` (15), `TrackingStation` (10, scene-unreachable), and the 17 `WaterfallCompat`/`ReStockCompat` tests (no modded instance) all pass under Ctrl+Shift+T and run in **zero** unattended runs. Roadmap R5 (isolated batches) shipped 2026-07-27 and unlocked 68 of them; only 1 of 13 unlocked categories has been wired since.
2. **Rewind-to-Separation, the v0.9 headline feature, has essentially no live proof.** 15 of 16 D9 registry cells have no live-run backing; `RecordingTreeSplitter` (1,391 LOC, the 13-step transactional HEAD/TIP splitter) has never executed in a live KSP process; the design doc's 8 named in-game acceptance tests (one marked "Critical regression guard") were never written.
3. **Several verification legs are structurally fail-open.** The ledger oracle's stock-award capture regexes cannot match real KSP log lines (a documented no-op); the anomaly sweep's `icon-jump` token is dead while the real `icon-teleport`/`icon-off-orbit` producers are ungated; 52 of 55 specs run with all tracers off, making their `allowedAnomalies = []` vacuously green; no spec forbids any raw Unity exception pattern (forbidden lists carry only Parsek-authored tokens: `[Parsek][ERROR]` everywhere, plus scenario-specific tokens on EVA-3 and the S1.6/S1.7 parity summaries), so a `NullReferenceException`/IMGUI exception storm passes every committed scenario; and B4's chute assertion is still a commanded latch (the same class that let B1 ship four months of green nightlies on a chute that never opened).
4. **Three code paths can destroy user data and have zero tests**: `FileIOUtils.SafeWriteConfigNode` ignores `ConfigNode.Save()`'s bool return and deletes the destination before the move (disk-full/locked-file ⇒ original sidecar destroyed, nothing written); the schema-reject → `PruneRejectedRecordingReferences` → save → orphan-quarantine chain is untested end-to-end; and `SaveActiveTreeIfAny`'s whole-node skip can orphan a switch-segment sidecar (known, unfixed, untested).
5. **The visual dimension — the one the sole developer must currently validate by eye — has no automated coverage at all**: zero screenshot capability, 14 of 15 IMGUI windows never drawn by any test, playback/watch mode not startable from the automation seam (verbs reserved but unimplemented), and the flight-scene render tracer unreachable from the harness (armed by zero specs, emits no sweep-matchable anomaly lines).

---

## 2. Coverage by system

### 2.1 xUnit unit tests (`Source/Parsek.Tests/`)

Strong overall; the convention of extracting pure `internal static` decision cores is genuinely followed (the biggest MonoBehaviour files have the *most* extraction — `ParsekFlight.cs` 148/172 statics named). Verified-dark files (zero references of any kind): `WarpToTimeController.cs`, `RecordedRelativeAnchorPoseResolver.cs` (Relative-frame anchor pose — the ghost-inside-the-planet hazard, in-game-only net), `TechResearchPatch.cs` + `FacilityUpgradePatch.cs` (both **block real career spends**), `OrbitArcSampler.cs`, `OverlayBadge.cs`, `ParsekHarmony.cs`, `ParsekToolbarRegistration.cs`, `KspFacilityIds.cs`. (`FloatingOriginSetOffsetPatch.cs` is symbol-dark too, but its one-line delegate `RecordFloatingOriginShift` is tested — not a behavior gap.)

Riskiest thin spots (full list of 20 in §5):

- The 8-method reflection classifier family (`TryClassifyAeroSurfaceState` … `TryGetRoboticPositionValue`) probes `PartModule` fields **by string name** — 0 xUnit, 0 in-game; a KSP/mod field rename fails silently.
- `FlightRecorder.ProcessRcsDebounce` has a fully pure signature and zero tests — the single cheapest high-value gap found.
- `PostWalkActionReconciler.ClassifyPostWalk` (8 action types, ~30 branches) is reached only through a facade, and its primary net asserts WARN-line presence rather than reconciled values.
- `KerbalsModule.cs`: 1,922 LOC, **1 direct unit fact** — worst of the 9 ledger resource modules by an order of magnitude.
- `RecalculationFuzzer` registers only `ContractsModule` and asserts sort-call counts — **no property test anywhere asserts resulting career state** (finiteness, permutation-invariance, idempotence, ELS ⊆ Ledger).
- The supersede/chain/closure graph walkers (`EffectiveState`) are cycle-prone and tested only with hand-built fixtures of ≤4 nodes; there is no randomized-tree invariant harness in the repo.

Generator ceilings limit what tests are *writable*: `RecordingBuilder.BuildV3Metadata` emits only ~30 of the codec's 83 keys, leaving ~50 no fixture can carry (no `recordedVesselGuid` on disk ⇒ no guid-gating round-trip; no `mergeState`/`supersedeTargetId`/`tOrb*`/tree-topology keys ⇒ no persisted Re-Fly or terminal-orbit fixtures); `VesselSnapshotBuilder` cannot emit a `MODULE` node (no docking/engine/inventory/chute fixtures) and hardcodes identity rotation (exactly the field the untested spawn-rotation code operates on); `ScenarioWriter` covers only a fraction of what `ParsekScenario.OnSave` writes — 11 node types (SUPERSEDES, TOMBSTONES, session markers, merge journal, routes, kerbal slots…) are producible by no generator, so ~40 test files hand-roll them.

Infrastructure notes: 72% of test files sit in the `Sequential` collection (suite effectively serial); `ReentryPotentialTests` leaks `TestSinkForTesting` (no `IDisposable`); 53 files assert on production source *text* (legitimate wiring gates, but several are the sole coverage for their subject).

### 2.2 In-game runtime tests (`Source/Parsek/InGameTests/` + assembly-wide)

539 declarations / 97 categories; authoritative per-category table at `docs/dev/autotest-ingame-category-inventory.md` (machine-derived, gated). Scene split: FLIGHT 363, AnyScene 104, SPACECENTER 46, TRACKSTATION 25, MAINMENU 1, EDITOR 0 (enforced ban). 44 of 97 categories hold ≤2 tests (72 declarations) — fragmentation that costs one KSP boot per category under the one-batch-one-category rule (R13 addresses).

Dimension gaps (things no in-game test exercises):

- **Live docking**: zero `ModuleDockingNode` references — no test docks or undocks a real port (the mod's most gotcha-dense KSP interaction; only claw `Couple()` and xUnit/flight coverage exist).
- **Warp regimes**: no rate sweep, zero `physicsWarp` references, no rails↔physics transition matrix; `GhostPlaybackLogic.WarpLoopPolicy.cs` has zero in-game references; the one high-warp canary is `AllowBatchExecution=false` so no batch ever drives real high warp.
- **UI**: 14 of 15 IMGUI window hosts never drawn by any test; the only real Layout+Repaint drive in the repo is `LogisticsTooltipEchoImguiTest` (whose probe-MonoBehaviour pattern is the proven template for a general window smoke test).
- **Settings**: 5 of 13 toggles have zero exercise, including `blockCommittedActions` and `autoBackupExistingSaves` — both act on the player's real campaign. `PreParsekBackup.cs` (the one-shot pre-Parsek save backup) has headless unit coverage of its pure helpers (`PreParsekBackupTests`, 24 tests) but zero in-game references — the live one-shot backup path against a real save directory is exercised nowhere.
- **Harmony patches**: 28 of the ~40 patch classes have zero in-game references — and a patch is the archetypal runtime-only construct (compiles clean and unit-tests green when silently broken). `SwitchIntentPatch`'s `*_RegisteredWithHarmony` smoke cells are the extendable template.
- **Stress/soak**: ghost cap is asserted (200), per-frame cost never measured; zero long-session/memory-growth tests.
- **Fixture ceilings**: roughly a third of the 816 `InGameAssert.Skip` sites trace to fixture shape (`gloops-airshow`'s vessel is a 1-part engineless capsule); `RecordingBuilder.WithSpawnedPid` has zero callers so the corpus carries no spawned-endpoint recording (`CrewReservationLive` permanently inert); no fixture puts two loaded vessels in physics range (precondition for every live dock/undock/delivery/grapple test).
- **Vacuous-pass trap**: ~19 silent early-return sites report PASSED (not Skipped) over an empty store — invisible to the anti-vacuity gate by proven construction; only the fixture defends.

### 2.3 Automated flight harness (`harness/`)

The orchestration itself is the best-tested code in the audit (404 xUnit cells on the seam verbs alone, fake-KSP full-run smokes, fail-closed spec validation, anti-vacuity by construction, orthogonal mission-vs-Parsek classification). The gaps are in what the 55 scenarios *assert* and *reach*:

- **Registry**: 97/242 cells claimed. Uncovered concentrations: D17 mod-compat 0/6, D15 UI 0/1 (the only 1-of-1 dimension), D18 paradox-prevention 2/12, D12 crew 2/10, D7 part-events 4/16, D6 playback 5/16, D3 reference frames 3/7 (including `absolute` — the most common frame in the product), D14 environment 14/32 (no Jool system, no atmosphere-entry cell, no scene-map/ts/editor).
- **Structural blind spots**: log expectations are bare `re.search` (occurrence counts and ordering inexpressible — a regression dropping one of two board merges passes every contract); the only save-content assertion is an integer recording-count window; the three reserved expectation families (`route`/`rewind`/`loop`) are parsed and SKIPPED (S4.1 already declares supersede/tombstone asserts that do nothing); `[expectations.world]` is implemented and used by zero specs. R9 (structural save-content expectations) is the single highest-leverage unbuilt item — without it, additional flights buy proportionally less.
- **Reach**: vessel switching is structurally impossible (kRPC bypasses `StockActionIntentMarker`; `SimulateStockSwitchClick` reserved, unimplemented); TRACKSTATION/MAP/EDITOR scenes unreachable (`DecideLoadRoute` is two-valued), stranding 7 categories; playback/watch verbs reserved, unimplemented.
- **Missions**: 18 shells but only ~5 distinct phase machines; 9 of 18 are parameter aliases over `b5_decide` (huge sweep leverage). No mission exists for: planes, rovers, Jool system, aerobraking (B15 deliberately avoids Eve's atmosphere), rendezvous-without-docking, asteroid/claw, N-vessel assembly, ISRU, high-part-count stress, revert/quickload, or a long career arc.
- **Modes**: 48 specs SANDBOX, 6 CAREER (5 vessel-less seam specs + CL-1, the only flown career mission), 1 SCIENCE. Career fixtures are clean-slate by construction; nothing in the repo templates *mid-career* progression (feasible — see the design doc — but unbuilt).
- **Detection classes that escape entirely**: visual glitches (zero screenshot capability), UI faults, FPS/perf, memory, frame-interleaved warp thrash, commanded-vs-observed drift, mutation adequacy (no mutation tooling exists; every "this cell bites" claim is prose).

---

## 3. Coverage by subsystem

Abbreviated verdicts; the per-slice detail lives in the section references.

| Subsystem | Unit | In-game | Harness | Standout gap |
|---|---|---|---|---|
| Core recorder (`FlightRecorder`) | good (37/137 members dark) | thin (`Recording` 1, `AutoRecord` 10 undriven) | flights exercise it | part-state classifiers + on-rails/SOI hooks untested anywhere; `ReFlyCanonicalization` 5/6 dark |
| Background recorder | good (30 files) | 2 tests, undriven | **none** | weakest high-traffic subsystem; BG SOI-change/off-rails transitions unnamed in any test |
| Ghost playback engine | good | `GhostPlayback` 42 driven (S1.4) | S1.4 | no SOI-crossing playback proof; loops/overlap/zone-transition cells uncovered |
| Trajectory math | **best-covered file in repo** (0/24 dark, H7, 8 in-game) | driven | driven | — |
| Rewind / Re-Fly / merge | strong units (`LoadTimeSweep` 49/49) | 37 tests **undriven**; key tests auto-skip | 15/16 D9 cells no live proof | splitter never ran live; no rewind-across-SOI test anywhere; RP slot ambiguity silently resolves to slot 0 |
| Ledger / career | ~700 facts, but `KerbalsModule` 1 fact; 4 reconcilers no dedicated suite | 1 non-circular test (GroundTruth), unwired | 7 career specs, pool-arithmetic only | crew-death→tombstone→rep chain: zero automated proof at any tier |
| Logistics | 1,368 cells | 47 tests, 38 batch-disabled, **0 driven** | H6 only | largest coverage-to-execution gap in repo; hold-reason vocabulary never asserted by name |
| Re-aim / periodicity | healthiest slice | 11 driven (M2) | M1/M2/S1.7 | "gates the SOLVER, not the playback"; `ReaimOrbitSegmentConverter` no test of any kind |
| Map/TS render | 56 test files, strong | S1.6/S1.7 parity with negative controls | driven | flight-scene tracer unreachable; icon-teleport/off-orbit ungated |
| UI windows | pure cores good (`Settings`/`StructureList` weak) | 14/15 windows never drawn | D15 0/1 | IMGUI exception storm passes everything |
| FX (engine/RCS/Waterfall/ReStock) | good units | 17 compat tests skip on stock | **0** — modded instance never provisioned | entire fallback machinery never ran where the mods exist |
| TestCommands seam | best-tested slice (404 cells + fake-KSP) | hollow category (2 unconditional skips) | all 55 specs exercise it | addon shells (3,300 LOC) no direct owner |
| Timeline | 122 facts | 0 | 0 | D15 `timeline-projection` = the only fully-uncovered single-cell dimension (D17's six cells are likewise all uncovered) |

---

## 4. Design-promised-but-untested contracts

From the design-document pass (each verified by grep at HEAD). Top 10 of 15; full ranking with citations in the findings register below.

1. Rewind v0.9.1 stable-leaf **8 named in-game acceptance tests do not exist** (`parsek-rewind-to-separation-design.md` §13.2 → scenarios S1–S22 in `research/extending-rewind-to-stable-leaves.md` §8); collapsed into one synthetic cell.
2. Ledger-oracle stock-award capture leg is a structural no-op (`STOCK_AWARD_PATTERNS` never match real logs; operator capture session outstanding).
3. Anomaly sweep fails open (`icon-jump` dead token; 9 producers raised-ungated).
4. `[expectations.rewind]`/`[expectations.loop]` verifiers reserved-never-landed; the rewind surface has zero automated post-run save assertions.
5. `KspStatePatcher` live mutation is the audit-documented "highest-risk gap": xUnit runs with Unity calls suppressed; `LedgerGroundTruthDiff.StrictPerIdentityForTesting` is never set by any spec, so per-identity facets are permanently report-only.
6. H5's negative control has no in-game half (the malformed-injection FAIL case cannot catch an ERS-filtered in-game model builder — the exact regression the design names).
7. Ghost-rendering §20.4 pipeline fixtures + `RegenerateFixtures` target absent; `Pipeline-Determinism` test absent.
8. Command-seam strict-FIFO ordering test required by its own test plan, conceded absent (R4).
9. INV8(b) headless career-diff reconstruction never proven — the offline analyzer has no career verdict (WARN-capped `reconstruction-not-available`).
10. Dock/undock §9's six binding invariants (two of which produced the measured `DOCK_ENDPOINT_RESOURCES 400/800` over-count) have no named regression tests.

The same pass produced a register of **~23 documented dead ends and forbidden testing shortcuts** (never route H5 through ERS; never read Parsek-derived numbers into the oracle; commanded latch ≠ compliance; token presence ≠ reachability; claim ≠ gate; etc.). These are reproduced in `design-testing-unified.md` §7 so future test work doesn't re-litigate them, and several stale design-doc registers (analyzer namespace/location, `AnalyzerVersion` 2-vs-3, seam reserved-verb table, rewind §13.1 class names) are listed there for cleanup.

---

## 5. Consolidated ranked gap register

Deduplicated across all slices, ranked by (player consequence × absence of any net × cheapness of the fix). The build-order recommendation derived from this list lives in `design-testing-unified.md` §8.

**Tier A — data loss / career corruption, no net at all**

| # | Gap | Cheapest system |
|---|---|---|
| A1 | `FileIOUtils.SafeWriteConfigNode` destroys the original file on failed `ConfigNode.Save` (bool ignored, delete-then-move); zero tests on all `SafeWrite*`/`SafeMove`; D16 `safe-write` uncovered | unit |
| A2 | Crew-death → ledger row → tombstone → rep-penalty chain has zero automated proof at any tier (CL-1's ledger half cut; both supersede in-game tests auto-skip; `ReputationPenaltySource.KerbalDeath` never constructed and the reconciler maps the wrong stock key at `PostWalkActionReconciler.cs:213`) | unit now; in-game after R12's out-of-Flight commit verb. PARTIALLY CLOSED: the wrong-key half is fixed (`KerbalDeath -> "VesselLoss"`) and the reconciler now has a per-case unit suite, `Source/Parsek.Tests/PostWalkActionReconcilerTests.cs`; the never-constructed action and the flown end-to-end chain remain open |
| A3 | Schema-reject → `PruneRejectedRecordingReferences` → save → quarantine chain: no end-to-end test; the prune is named in zero tests | unit |
| A4 | `RecordingTreeSplitter` (HEAD/TIP split) never executed live; D9 `head-tip-split` officially "NOBODY" | in-game + spec |
| A5 | S4.1-IDLE-DISCARD: live re-fly session's tree destroyed by idle auto-discard; open, unfixed, no regression test; currently flakes S4.1 at 0.75 | unit (~30 min) |
| A6 | Rewind across an SOI boundary: zero tests anywhere | unit |
| A7 | Foreign/unmodelled currency delta through a rewind (drawdown guard bypassed under `IsAuthoritativeReduction`; read-back guard warn-only) | unit, then modded-compat spec |

**Tier B — fail-open verification (green that proves nothing)**

| # | Gap | Fix shape |
|---|---|---|
| B1 | Raw Unity exceptions invisible: no spec forbids any Unity exception pattern (forbidden lists carry only Parsek-authored tokens) | spec-only (calibrate + allowlist) |
| B2 | `STOCK_AWARD_PATTERNS` dead vs real logs | harness-unit (rewrite from archived CL-1 log) |
| B3 | `icon-jump` dead token; `icon-teleport`/`icon-off-orbit` ungated; count budgets absent | hlib + spec |
| B4 | 52/55 specs tracer-off ⇒ anomaly rows vacuously green; `ghostRenderTracing` armed by zero specs; `GhostRenderTrace` emits no sweep-matchable lines | C# (small) + spec |
| B5 | B4 `chuteDeployed` commanded latch (audit-debt gate 7) | spec (grep part events from a B4 recording) |
| B6 | ~19 silent early-return PASS sites in in-game tests | mechanical conversion to `InGameAssert.Skip` |
| B7 | ERS/ELS grep gate holes: `CommittedTrees` unpoliced (6 files), partial-class paths escape exact-path allowlist, silently skips without pwsh | script/unit |
| B8 | Log expectations can't express counts/ordering; recording assertions are one integer window | R9 (structural expectations) |

**Tier C — written-but-never-executed coverage (cheapest absolute wins)**

| # | Gap | Cost |
|---|---|---|
| C1 | ~68 reachable-but-undriven in-game categories (Rewind 37, GhostLifecycle 17, CrewReservation 15, GhostAudio 9, Optimizer, BackgroundSeeder, LedgerGroundTruth, D13 spawn categories…) | ~50–70 s/boot; ~89 min for the whole fan-out (R6–R8) |
| C2 | S1.5 + S4.1 unattended (operator-tier rationale contradicted by measurement) | 2 boots → 7 D9 cells |
| C3 | `modded-compat` instance provision + one spec (17 compat tests + D17 + FX fingerprint A/B) | one provision + one spec (R14) |
| C4 | TS scene route (`DecideLoadRoute` third route or scene verb) → 9 stranded TrackingStation tests + 8 untested TS patches | one seam verb (R12) |
| C5 | `WithSpawnedPid` corpus fixture change → `CrewReservationLive` + `SpawnHealth` cells | one C# fixture line |
| C6 | Marginal tokens on already-flying scenarios: staging-debris TTL/promotion on B2, BG-vessel SOI section on B7, F5/F9 seam steps mid-ascent, SOI-crossing playback in S1.4 corpus | spec-only (verify token reachability in archived logs first) |

**Tier D — absent test classes**

| # | Gap |
|---|---|
| D1 | Property/fuzz testing: extend `RecalculationFuzzer` to all 9 modules + invariants; new seeded random-tree fuzzer for the supersede/chain/closure walkers (termination, closure ⊆ committed, idempotence) |
| D2 | Perf/scale: no cap or budget on recording length (largest fixture 2,000 points); `ComputeERS` O(supersedes)-per-call on 57 call sites unmeasured at the 272-recording corpus; no playback-at-scale cost test; no soak/memory test |
| D3 | Crash-matrix completeness: journal fault-injection is hand-picked `[Fact]`s (a 12th phase reds nothing; `Split` has no fault test); `RewindInvoker.StartInvoke` has no per-step interruption seam; cross-process recovery proven at exactly one phase |
| D4 | Visual validation: zero screenshot capability; 14/15 windows undrawn; playback/watch verbs unimplemented; golden/structural render assertions absent (solutions in `design-testing-unified.md` §6) |
| D5 | Mutation adequacy: no mutation tooling anywhere; every "this cell bites" claim is hand-verified prose |

**Tier E — thin high-value units** (each ≤ a day): `ProcessRcsDebounce` (pure, zero tests); reflection-classifier interpretation split; `BallisticExtrapolator` Kepler core vs closed form; `RecordedRelativeAnchorPoseResolver` with injected body-pose provider; `ReFlyCanonicalization` round-trip < 1 mm; spawn-rotation family vs the two-rotation contract; `TryPassTerminalOrbitSpawnSafety` vs the BUG-C geometry; RP-slot ambiguity detection; `KerbalsModule` end-state suite; `PostWalkActionReconciler` per-case suite (first assertion: the rep-source mapping, currently wrong); career-spend-blocking patch decision extraction; `RouteDispatchDecision` hold-reason vocabulary; `ReaimOrbitSegmentConverter` deg/rad round-trip; sidecar-codec heal family; `RouteProofMetadata` clone helpers; `MergeCrashRecoveryMatrixTests` → `[Theory]` over the phase enum.

---

## 6. Known bugs without regression tests

From `todo-and-known-bugs.md` + `autotest-status.md`, verified open at HEAD: S4.1-IDLE-DISCARD (A5); looped re-aim interplanetary no-encounter / ~62° SOI teleport (blocked on a clean direct Kerbin→Duna looped fixture that does not exist); `Ensure(markDirty:false)` in-memory mutation of committed recordings; `.pann` section-index desync; INV2 double-cover residual (pending-operator flight); HARNESS-MIDMISSION-COMMIT-BYPASS; HLIB-ALLOWBATCH-NONLITERAL-FAILS-OPEN; R2 registry defects (`D1 stop-on-switch`, `D3 surface-body-fixed` name nonexistent symbols); B7 flaky (Ike captures ~half of approaches); the six visual defects listed in §2.3 of the render slice (icon-off-orbit residual, create-frame transient, swap-settle window, TS proto gap, loop-shift destination drift, SOI dead-end line) — every one visual, none tested.

Also latent-by-observation: `_quarantine/` has no reaper and appears in no disk diagnostic; `ReFlyProvisionalBinding` is observation-only (nothing prevents the unbound-provisional state); `EffectiveState.cs:109`'s cross-tree walk TODO has no behavior-pinning test.

---

## 7. What is genuinely healthy

Worth stating so the gap list reads in proportion: the pure-core extraction convention and its 77% coverage; `TrajectoryMath` (fully covered at all three tiers); `LoadTimeSweep` (49/49); the analyzer (all 14 rules 1:1 tested, loader/core split enforced); the command seam (best-tested slice in the repo); the harness's own fail-closed spec validation, anti-vacuity-by-construction, orthogonal mission classification, and S1.6/S1.7's parity oracles with negative controls (the only place a zero-drift assertion is proven able to fail); quicksave/quickload and revert-during-re-fly decision layers; orphan-sidecar quarantine safety; path-traversal validation; and the fixture-drift byte-identity gates on generated saves.
