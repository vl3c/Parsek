# Plan: Dock-side baseline is post-couple (false MixedPickupDelivery reject)

## Problem

A clean delivery Supply Run can be falsely rejected at route creation as
`MixedPickupDelivery`. The route analyzer's pickup gate
(`RouteAnalysisEngine.HasResourcePickup`) computes, per resource:

```
transportGain = UndockTransportResources[name] - DockTransportResources[name]
```

and rejects the run when `transportGain > ResourceEpsilon` (the transport appears
to have GAINED a resource across the docked window, i.e. a pickup). A pure delivery
run should only ever LOSE resources from the transport, so `transportGain` should be
`<= 0`.

The bug is in the DOCK-side baseline, not the gate. `DockTransportResources` is
extracted from the **post-couple merged vessel snapshot** (`mergedSnapshot` in
`ParsekFlight.CreateMergeBranch`, passed as `dockedSnapshot` to
`RouteProofCapture.BuildDockRouteConnectionWindow`, lines ~5521-5530). By the time
that snapshot is taken the parts are physically joined, so any same-frame stock
crossfeed equalisation between transport and endpoint tanks of the same resource has
already drained the transport tank into the depot. The dock baseline is therefore
DEFLATED. At undock the transport reads higher than the deflated dock baseline, so
`transportGain > 0` trips the gate on a resource the player never intended to move.

## Root cause is already half-fixed on the endpoint side

The symmetric endpoint problem was already solved. `BuildDockRouteConnectionWindow`
takes an optional `endpointPreCoupleSnapshot` (default null); when present it is used
as the endpoint baseline instead of the merged snapshot
(`RouteProofCapture.cs:307-333`), so `DockEndpointResources` is not inflated by the
transport's tank. That snapshot is captured in `ParsekFlight.OnPartCouple` while
`data.from` / `data.to` still reference DISTINCT vessels (KSP has not reparented
yet), stored in `pendingDockPartnerSnapshot` / `pendingDockPartnerSnapshotPid`
(`ParsekFlight.cs:10544-10578`, retroactive path `:10696-10712`), and threaded into
the merge build (`:5456-5461`, `:5530`).

The transport side has NO equivalent: `DockTransportResources` /
`DockTransportInventory` always come from the merged `dockedSnapshot`. This plan adds
the symmetric pre-couple TRANSPORT snapshot.

## Fix (chosen: pre-couple transport snapshot, mirrors the proven endpoint path)

Capture the recorder's own (transport) vessel snapshot at the same proven pre-couple
moment the partner snapshot is captured, and feed it as the transport baseline.

### 1. New fields (`ParsekFlight.cs`, next to the partner fields ~760)
```csharp
// Pre-couple SELF (transport / recorded vessel) snapshot, captured in OnPartCouple
// while data.from / data.to still reference DISTINCT vessels. Used by
// CreateMergeBranch to build the route window's transport baseline from the
// transport's pre-dock state, so a same-frame stock crossfeed equalisation that
// drained the transport tank into the depot does not deflate DOCK_TRANSPORT_RESOURCES
// and trip the MixedPickupDelivery pickup gate. Mirrors pendingDockPartnerSnapshot.
private ConfigNode pendingDockSelfSnapshot;
private uint pendingDockSelfSnapshotPid;
```

### 2. Capture in `OnPartCouple` (both the main and retroactive snapshot blocks)
In the main block (`:10544-10578`), alongside the partner snapshot, when one of
`data.from.vessel` / `data.to.vessel` is the recorder's vessel
(`persistentId == recorder.RecordingVesselId`), snapshot that vessel:
```csharp
pendingDockSelfSnapshot = null;
pendingDockSelfSnapshotPid = 0u;
Vessel self = ResolveSelfVessel(data, recorder.RecordingVesselId);
if (self != null && self.parts != null && self.parts.Count > 0) {
    pendingDockSelfSnapshot = VesselSpawner.TryBackupSnapshot(self);
    pendingDockSelfSnapshotPid = self.persistentId;
    ParsekLog.Verbose("Flight", $"OnPartCouple: captured pre-couple self snapshot selfPid={...} parts={...}");
}
```
Same in the retroactive block (`:10696-10712`), guarded by the same `from != to`
distinctness check; if already reparented it stays null (accepted fallback, same as
the endpoint snapshot's documented §5.1 risk).

### 3. `BuildDockRouteConnectionWindow` (`RouteProofCapture.cs:272`)
Add a `ConfigNode transportPreCoupleSnapshot = null` parameter (after
`endpointPreCoupleSnapshot`). Select the transport baseline self-validatingly, so a
stale/mismatched snapshot can never produce a wrong manifest:
```csharp
ConfigNode transportSnapshotForBaseline =
    (transportPreCoupleSnapshot != null
     && SnapshotContainsAnyPartPersistentId(transportPreCoupleSnapshot, transportPids))
        ? transportPreCoupleSnapshot
        : dockedSnapshot;
```
Use `transportSnapshotForBaseline` for `DockTransportResources` and
`DockTransportInventory` (currently both read `dockedSnapshot`). Endpoint side
unchanged. (Self-validation is slightly more defensive than the endpoint side's bare
`?? dockedSnapshot`; this is intentional because the call site passes the field
directly rather than pre-gating on a pid match.)

### 4. Call site (`ParsekFlight.cs:5447-5530`)
Resolve `transportPreCoupleSnapshot` from the new field and pass it as the new last
argument:
```csharp
ConfigNode transportPreCoupleSnapshot = pendingDockSelfSnapshot;
...
RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
    ..., endpointPreCoupleSnapshot, transportPreCoupleSnapshot);
```
The field is cleared each couple, so it always belongs to this couple; the
self-validation in step 3 is the correctness backstop.

### 5. Clear the new fields wherever the partner fields are cleared
`:10544`, `:10696` (reset-then-capture), and the pure clears at `:11762`, `:11899`,
`:11974`. Add `pendingDockSelfSnapshot = null; pendingDockSelfSnapshotPid = 0u;` to
each so a stale snapshot never leaks across couples / dock attempts.

## Rejected alternative

Fix candidate 2 from the todo (detect "approximate equalisation" in
`HasResourcePickup`: per-resource `transportGain` approximately equal to
`endpointLoss` within tolerance, treat as not-a-pickup) is NOT taken. It is a
heuristic that could misclassify a genuine pickup that happens to net out, and it
patches the symptom in the gate rather than the wrong INPUT data. The pre-couple
snapshot is exact and reuses a proven mechanism.

## Dependency / scope note

The fix is exactly as reliable as the existing endpoint mechanism: both assume the
OnPartCouple snapshot block runs BEFORE the same-frame crossfeed equalisation pass.
The shipped, working endpoint fix validates that timing. If equalisation ran before
OnPartCouple, neither snapshot would help, but that is not the observed behaviour.
This is a route-creation eligibility fix only: no change to dispatch, delivery,
serialization, or the route data model. `RouteConnectionWindow` already serializes
`DockTransportResources` / `DockTransportInventory`; this only changes which snapshot
they are extracted FROM at capture time.

## Files touched

- `Source/Parsek/RouteProofCapture.cs` — `BuildDockRouteConnectionWindow` new param +
  self-validating transport baseline selection.
- `Source/Parsek/ParsekFlight.cs` — two new fields, two capture blocks, call-site arg,
  clear sites.
- `Source/Parsek.Tests/Logistics/RouteProofDockBaselineTests.cs` (new) — see Tests.
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md` (mark the open item done), and
  `docs/roadmap.md` (mark the Tier-1 item shipped).

## Tests (xUnit, headless)

`BuildDockRouteConnectionWindow` is `internal static` and takes ConfigNode snapshots,
so it is directly unit-testable. Snapshots are `VESSEL` nodes with `PART` children
(each `persistentId`) carrying `RESOURCE` children (`name` + `amount` + `maxAmount`),
the format `VesselSpawner.ExtractResourceManifest` reads.

1. **Transport baseline uses the pre-couple snapshot when provided.** Merged snapshot
   has the transport tank DEFLATED (e.g. LiquidFuel 200 after equalisation); the
   pre-couple transport snapshot has the true value (e.g. 500). Assert
   `window.DockTransportResources["LiquidFuel"] == 500` (pre-couple), not 200.
2. **Falls back to the merged snapshot when the pre-couple snapshot is null** (current
   behaviour preserved): assert `DockTransportResources` reflects the merged value.
3. **Falls back when the pre-couple snapshot does not contain the transport PIDs**
   (self-validation): pass a snapshot whose PART persistentIds don't intersect
   `transportPids`; assert the merged value is used.
4. **Inventory parity:** the same pre-couple selection applies to
   `DockTransportInventory` (one stored-part case).
5. **End-to-end gate behaviour (the actual bug):** build a `RouteConnectionWindow`
   two ways for a clean delivery run that crossfed LiquidFuel during the dock:
   - post-couple baseline (deflated dock transport) -> `HasResourcePickup` /
     `RouteAnalysisEngine.AnalyzeRecording` returns `MixedPickupDelivery` (repro).
   - pre-couple baseline (true dock transport) -> NOT rejected (delivery-only).
   This pins that the fix flips the classification. (`HasResourcePickup` is private;
   drive it through `AnalyzeRecording` / the public analysis entry, or assert on the
   built `DockTransportResources` feeding a directly-constructed window through the
   analyzer.)
6. **Log assertion:** the capture path logs the pre-couple self snapshot line; assert
   the `[Flight]` verbose line is emitted (canonical `TestSinkForTesting` pattern,
   `[Collection("Sequential")]`).

## Version / docs (confirm-point)

Main is `0.10.1`. This is a route-creation correctness fix and coordinates with the
in-flight structure-window PR #1107, which already bumps to `0.11.0`. Plan: target
`0.11.0` (bump `AssemblyInfo.cs` + `KSPAssembly` tag + `Parsek.version`, add a Bug
Fixes entry under `## 0.11.0`). When both PRs land, the second resolves a trivial
CHANGELOG/version overlap (same target version). Confirm 0.11.0 vs folding into a
different release.

CHANGELOG wording (user-facing, no em dashes):
> Fixed a bug where a clean cargo-delivery Supply Run could be wrongly rejected as a
> mixed pickup/delivery run when the transport and the destination shared a fuel type
> that stock crossfeed equalised during docking. Route eligibility now reads the
> transport's fuel level from just before docking, so the equalisation no longer looks
> like the transport picking cargo back up.
