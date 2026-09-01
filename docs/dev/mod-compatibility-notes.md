# Mod Compatibility Notes

## CustomBarnKit
CustomBarnKit modifies facility upgrade costs and tier counts. Parsek's FacilitiesModule stores facility levels as integers from KSP's normalized values. The dynamic slot limit mapping in LedgerOrchestrator assumes stock 3-tier progression (levels 0/1/2). CustomBarnKit compatibility requires verifying that the normalized-to-integer level conversion still produces correct values with non-standard tier counts.

**Status:** Untested. The conversion formula `(int)Math.Round(valueAfter * 2)` may produce incorrect levels with CustomBarnKit's modified tier structure.

## Strategia
Strategia completely replaces the stock strategy system. StrategiesModule tracks strategy activate/deactivate actions and applies contract reward transforms. If Strategia uses different strategy IDs or transform mechanics, the module may produce incorrect results.

**Status:** Untested. StrategiesModule uses stock strategy semantics (source/target resource, commitment percentage). Strategia compatibility requires investigation.

## Contract Configurator
Contract Configurator adds custom contract types with complex parameters. Parsek captures contract ConfigNode snapshots at accept time. CC contracts may not round-trip correctly if CC version changes between capture and restore.

**Status:** Untested. Contract state patching (Task 33) is scaffolded but not implemented, partly due to CC compatibility concerns.

## Better Time Warp (BetterTimeWarpContinued)
Better Time Warp raises KSP's warp ceiling by swapping the `TimeWarp.fetch.warpRates` / `physicsWarpRates` rate tables and overriding every body's `timeWarpAltitudeLimits`. It uses no Harmony patches and never programmatically drives warp or time; Parsek never patches `TimeWarp` either, so there is no patch collision.

Parsek reads warp state in only two safe ways: value-threshold comparisons (`ShouldSuppressVisualFx` at >10×, `ShouldSuppressGhosts` at >50×, map-reseed at >1×), which stay correct at any rate the mod unlocks (higher rates simply suppress ghost FX/meshes more, as intended); and index/`>0` checks (`CurrentRateIndex`, `SetRate(0)`, save-and-restore `SetRate(index)` in time-jump / rewind), which are unaffected because the mod preserves the 8-rails / 4-physics index structure. There are no hardcoded rate tables or "max = 100000×" assumptions in the runtime.

The one concrete interaction was benign: `FlightRecorder.ComputeApproachAltitude` uses `body.timeWarpAltitudeLimits[4]` as the airless-body recording-split threshold, and Better Time Warp zeroes that index at `MainMenu` startup, which forced Parsek's radius fallback. `StockWarpAltitudeLimits` now snapshots each body's stock array on `PSystemManager.OnPSystemReady` (during the `PSystemSpawn` phase, before the mod's `MainMenu` override) so the split altitude keeps its stock value. The snapshot is fail-safe: if it never runs, the cache is empty and behavior is identical to before (live array → radius fallback).

**Status:** Compatible. No code change is required for Parsek to coexist with Better Time Warp; the `StockWarpAltitudeLimits` snapshot is a small correctness hardening for the one altitude-limit interaction. Caveat (pre-existing, not mod-specific): the higher rates this mod unlocks amplify the known map-render-at-high-warp issues (warp-reseed lag, icon teleport) and make on-rails recording sample more coarsely — both already occur at stock 100000×. Verify in-game by grepping `KSP.log` for `[StockWarpLimits] captured stock timeWarpAltitudeLimits for N bodies` (N > 0 with the mod installed).

## Waterfall (incl. Stock Waterfall Effects) and ReStock / ReStock+ - ghost FX recovery

Fully compatible; player-facing behavior is described in the README. The implementation contract
below was moved here from `.claude/CLAUDE.md` on 2026-09-01; it is the authority for anyone touching
`WaterfallCompat.cs`, `PristinePartFxResolver.cs`, `ReStockPatchFxIndex.cs`, `EngineFxBuilder`, or
`GhostVisualBuilder`'s RCS FX path.

- `WaterfallCompat.cs` is the per-part gate for the pristine-config ghost FX fallback (name check, no
  compile-time Waterfall reference). Gate closed = stock installs are behavior-identical.
- `PristinePartFxResolver.cs` recovers a part's pre-ModuleManager EFFECTS node, per-ordinal engine/RCS
  effect names, and legacy `fx_*` keys from the pristine on-disk .cfg (MM patches GameDatabase in
  memory only; disk PART names are matched via `diskName.Replace('_','.')`). Consumed by
  `EngineFxBuilder.TryApplyPristineEngineFxFallback` and `GhostVisualBuilder.TryApplyPristineRcsFxFallback`
  when Waterfall config packs (SWE) delete the stock particle definitions.
- Legacy `fx_*` names resolve through KSP's builtin `Effects/{name}` Resources path so exact
  size-variant flames win. NEVER add `Effects/` to the shared `TryResolveFxPrefabExact` probes: stock
  paths rely on the deliberate `fxPrefabFallbacks` substitutions. A wanted flame that never resolves
  gets the white-flame fallback.
- Stock Swivel / Poodle / Mainsail are LEGACY parts (plain ModuleEngines + top-level `fx_*` keys, NO
  EFFECTS node); Spark / RAPIER have real pristine EFFECTS particles.
- `ReStockPatchFxIndex.cs` is a lazy per-session index of the EFFECTS definitions ReStock authors for
  stock parts, parsed from ReStock's MM patch FILES on disk (all three roots: Patches + PatchesMH +
  PatchesLegacy; MM strips patch nodes from GameDatabase post-patch). Patch node names are matched by
  prefix; the part target is the token between the FIRST `[` and `]`, and the wildcard skip checks
  THAT TOKEN ONLY. Fresh `EFFECTS` is matched by EXACT name (never `!EFFECTS`); per-ordinal names are
  read through MM value prefixes (`%` / `@` / `&` accepted, `!` / `-` / `#` skipped). An absent
  directory = a permanently empty index = stock-install no-op.
- Consumers of the index: `EngineFxBuilder.TryScanReStockEffectsEntries`,
  `GhostVisualBuilder.TryApplyPristineRcsFxFallback`, and `HasAuthoredEffectsFor`, which stands down the
  hardcoded stock per-part FX tunings whenever ReStock authored the part's EFFECTS (ReStock-presence
  gated, NOT Waterfall-gated).
- Parity instruments: `GhostFxFingerprint.cs` emits canonical per-part `[FxFingerprint]` Verbose lines
  after every engine/RCS FX build (stock AND Waterfall installs) for mechanical A/B diffs, paired with
  the in-game `AllEnginesPristineFxResolveExactly` sweep (WaterfallCompat category).
  `GhostFxEmissionProbe.cs` logs the MEASURED mean particle velocity direction of every cloned ghost
  engine FX instance (`[FxEmissionProbe] measured:`) - transform-axis assumptions were proven unreliable
  for smokeTrail prefabs; showroom fixtures stand upright, so angleFromDown ~0 = correct, ~180 = inverted.
