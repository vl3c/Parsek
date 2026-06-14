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

— is **byte-identical** across at least four files, plus several thin `Route`-typed
wrappers:

| Site | Member (as-of-audit) |
|------|------|
| `Logistics/RouteStore.cs` | `ShortId(string)` @224 |
| `Logistics/RouteTreeGuard.cs` | `ShortId(string)` @310 |
| `Logistics/RouteRunCostCalculator.cs` | `ShortId(string)` @505 (+ `ShortId(Route)` @500) |
| `Logistics/RouteOrchestrator.cs` | `ShortIdForLog` |
| `Logistics/Route.cs` | `ShortIdForLog` @500 (on `this.Id`) |
| `Logistics/LiveRouteRuntimeEnvironment.cs` | `ShortIdForRoute` @305 |

Confirm each body is byte-identical before folding (checklist item 10).

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

`IsFinite(double)` / `IsFinite(Vector3d)` is triplicated across `OrbitReseed`,
`OrbitSeedResolver`, and `OrbitalCheckpointDensifier`. A `OrbitMathUtil.IsFinite`
owner would fold it. Very low value — include only if a maintainer wants the tidy;
same wrapper-delegation discipline as above. Confirm byte-identical bodies first.
