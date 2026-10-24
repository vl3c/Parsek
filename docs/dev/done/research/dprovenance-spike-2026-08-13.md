# D-Provenance Spike (#8): route-window part-pid intersection - VERDICT: GO

Investigation for `docs/dev/design-dock-event-graph.md` section 9 (PR sequence step 8):
can "undock child X is made of dock-partner matter" be derived from data already on
disk, fixing (a) the partner-journey walk mis-following to the continuing stack at a
post-dock undock fork (the AB/CD analysis's D case), and (b) the digest's missing
"story continues in mission X" line? Run 2026-08-13 against source and the committed
BDOCK-1 recorded fixture. Thresholds per decided question Q10 (>= 90% departing /
<= 50% continuing).

## 1. Code findings (all verified in source)

1. `VesselSpawner.TryBackupSnapshot` (`VesselSpawner.cs:437-464`) is
   `vessel.BackupVessel()` -> `ProtoVessel.Save(node)` plus normalization touching
   names / terminal state / crew only. ProtoPartSnapshot serializes the LIVE
   `part.persistentId` into every PART node: snapshots preserve real part pids at
   capture.
2. `CollectPartPersistentIds` (`VesselSpawner.cs:6229-6244`) reads exactly
   `PART/persistentId`, so route-window part sets and snapshot pid sets share one
   identity space.
3. Pid re-minting (`RegeneratePartIdentities`, `VesselSpawner.cs:6251+`, #234) runs
   ONLY on the spawn path. Recorded snapshots keep original pids.
4. `CreateSplitBranch` (`ParsekFlight.cs:5411-5449`): BOTH undock children receive
   live snapshots assigned to `VesselSnapshot` + `GhostVisualSnapshot`, and
   `TryCompleteLatestRouteConnectionWindow(parentRec, branchUT, activeSnapshot,
   bgSnapshot)` completes the window from the SAME two snapshots. The classifier's
   two pid sets are coherent by construction at capture time.
5. Persistence: `VesselSnapshot` survives durably in the `<id>_vessel.craft` sidecar
   (`RecordingSidecarStore.cs:52`, written whenever non-null; re-hydrated on cold
   load, `ParsekFlight.cs:15016` / `ParsekKSC.cs:1898`). Sidecar format is
   `SnapshotSidecarCodec` (`PSN0` + deflate); the payload is the plain VESSEL
   ConfigNode with PART persistentIds intact.

## 2. Empirical measurement (BDOCK-1 recorded fixture)

Fixture: `harness/fixtures/saves/bdock-recorded` (the BDOCK-1-station-interceptor
harvest, run 2026-08-11_1606; two committed trees, single-parent Dock BP `4fe5e39e`
target pid 3620499050 at UT 8949.268 -> merged child `f049901e` -> Undock BP
`857e997a` at UT 8950.588 -> children `37d0dc07` (continuing, child[0]) and
`4af6cfd7` (departing)). Measurement: decompress each child's `_vessel.craft`
(PSN0/deflate), collect PART persistentIds, intersect with the parent window's
persisted `TRANSPORT_PART_PIDS` / `ENDPOINT_PART_PIDS`.

```
window:              transport n=28   endpoint n=28   intersection = 0
continuing 37d0dc07: parts=28  overlap(ENDPOINT)=100%  overlap(TRANSPORT)=0%
departing  4af6cfd7: parts=28  overlap(ENDPOINT)=0%    overlap(TRANSPORT)=100%
merged     f049901e: parts=56  overlap(ENDPOINT)=50%   overlap(TRANSPORT)=50%
```

Readings:
- The window sets are exactly partner-scoped and disjoint (invariant 9.4 of
  `dock-undock-recording-structure.md` holds on disk).
- Separation is PERFECT: 100%/0% both directions, far beyond the 90%/50% Q10 gate.
- The merged child's exact 50/50 confirms pid preservation end-to-end (56 = 28+28,
  no re-mint, no loss through commit + harvest).
- Note the direction on THIS fixture: the continuing child matched the ENDPOINT
  (partner) set - which side departs is a per-flight fact, exactly why a
  pid-convention walk cannot substitute for the matter test.

## 3. Verdict against the 9.4 criterion

GO on all three clauses: (a) split-child snapshots preserve live part pids exactly;
(b) the classifier separates unambiguously on the fixture; (c) the classification is
computable at graph build time from persisted data only (this measurement used
nothing else). Caveats carried into the addendum: scope every intersection to one
merged stack (same tree, the window on the direct dock ancestor, dock/undock
bracketing the child) so craft-baked pid collisions cannot enter; a recording with a
missing `_vessel.craft` or an incomplete window degrades to the current pid-based
walk (no classification, never a guess).

## 4. What happens next

The design addendum (design-dock-event-graph.md section 18) specifies
`ClassifyUndockChildProvenance` and its two consumers (journey-walk fork decision,
digest continuation line). Implementation is follow-up scope after PR sequence steps
4-6 land; #9 (recorded lineage) stays deferred and is now likely unnecessary for the
docked-partner case.
