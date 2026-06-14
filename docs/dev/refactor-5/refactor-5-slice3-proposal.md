# Refactor-5 Slice 3 Proposal — `RouteIds.Short` Cross-Cutting Owner

**Date:** 2026-06-14. **Status:** Proposal (not implemented).
**Roadmap:** `docs/dev/refactor-5/refactor-5-slices.md` (shared rules + validation gate).

Small, independent, **pure** Pass-2 dedup. Can land any time.

## Problem

The short-id truncation used for logging —

```
id == null || id.Length == 0  ->  "<no-id>"
id.Length > 8                  ->  id.Substring(0, 8)
else                           ->  id
```

— is **byte-identical** across (at least) the following `Logistics/` files. The
copies below were verified byte-identical at audit time; the table is the in-scope
fold set. (Find the full census at implementation with
`grep -rn "string ShortId" Source/Parsek` and body-compare each hit.)

| Site (in scope) | Member (as-of-audit) | Body verified |
|------|------|------|
| `Logistics/RouteStore.cs` | `ShortId(string)` @224 | ✅ identical |
| `Logistics/RouteCadence.cs` | `ShortId(string)` @182 | ✅ identical |
| `Logistics/RoutePriority.cs` | `ShortId(string)` @63 | ✅ identical |
| `Logistics/RouteTreeGuard.cs` | `ShortId(string)` @310 | confirm |
| `Logistics/RouteRunCostCalculator.cs` | `ShortId(string)` @505 (+ `ShortId(Route)` @500) | confirm |
| `Logistics/RouteOrchestrator.cs` | `ShortIdForLog(Route)` @2408 | confirm |
| `Logistics/Route.cs` | `ShortIdForLog()` @500 (on `this.Id`) | confirm |
| `Logistics/LiveRouteRuntimeEnvironment.cs` | `ShortIdForRoute(Route)` @305 | confirm |

Confirm each body is byte-identical before folding (checklist item 10).

### Excluded near-misses — do NOT fold into `RouteIds.Short`

These look similar but are **not** behavior-equivalent or are out of scope; folding
them would change output or touch deferred code (a zero-logic-change violation):

- `MilestoneStore.cs:25` `ShortId` — returns **`"?"`** for empty input, not
  `"<no-id>"` (and orders the ternary the other way). Different output string;
  leave it.
- `GhostRenderTrace.cs:1076` / `MapRenderTrace.cs:1090` `ShortId` — part of the
  tracer formatter set CLAUDE.md **explicitly defers** (and forbids touching
  `GhostRenderTrace.cs`). Out of scope regardless of body.
- `ReFlySettleStabilityTracker.cs`, `FlightRecorder.cs:6541`,
  `MissionRouteStructureList.cs:583`, `PlaybackTrace.cs:383`, `UI/LogisticsWindowUI.cs:2907`
  — same-shaped `ShortId`s outside `Logistics/`. Cross-subsystem; not part of this
  slice. If a later pass wants them, body-verify each (the empty sentinel varies)
  and treat as its own owner decision.

## Proposal

Add one tiny owner:

```csharp
internal static class RouteIds
{
    internal static string Short(string id);        // the exact truncation above
    internal static string Short(Route route);      // => Short(route?.Id)
}
```

Replace each site's body with a delegation to `RouteIds.Short(...)`. **Keep the
existing method names as thin wrappers** (`ShortId`/`ShortIdForLog`/`ShortIdForRoute`
just `return RouteIds.Short(...)`) so no call site outside these files changes and
no log output changes. Do not rename or inline at call sites in this slice.

## Constraints

- The output string for every input must be identical to today (it is the same
  algorithm) — the existing `Route` logs are unchanged.
- No access-modifier change on any existing member; the wrappers keep their current
  modifiers.
- This is the only behavior-neutral way to remove the duplication without touching
  ~30 call sites.

## Validation

`dotnet build`, then `--filter "FullyQualifiedName~Logistics"` (or
`FullyQualifiedName~Route`), then the full non-injection gate, then a clean-context
review. One commit. Because it touches several files, the review should confirm
every wrapper still returns the identical string and that no log line changed.

## Optional Micro-Follow-Up (separate, even smaller)

`IsFinite(double)` / `IsFinite(Vector3d)` / `IsFinite(Quaternion)` is duplicated
**pervasively** — ~30+ copies across the codebase (`TrajectoryMath`, `GhostMapPresence`,
`FlightRecorder`, `BackgroundRecorder`, `ParsekFlight`, `OrbitReseed`,
`OrbitSeedResolver`, `OrbitalCheckpointDensifier`, `RelativeAnchorResolver`,
`VesselSpawner`, `RecordingStore`, …). A shared `MathFiniteUtil.IsFinite` owner could
fold them, but the breadth makes this a large, low-value sweep (and several bodies
must be body-verified — not all are identical). **Very low priority; likely skip.**
Note `TerminalOrbitSpawnSafety.IsFinite` (@284) is already `internal static` if an
owner is ever wanted. Confirm byte-identical bodies before folding any subset.
