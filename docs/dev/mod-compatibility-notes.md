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
