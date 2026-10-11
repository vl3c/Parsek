# Parsek Vessel and Mission Names - Design Document

*Design specification for keeping vessel names consistent with stock KSP renames across every recording, ghost, spawn and Parsek window, and for keeping mission names separate.*

**Status:** DRAFT, owner rulings recorded (2026-10-10 / 2026-10-11), revised after a clean review (2026-10-11). No code yet.
**Tracks:** todo GC-7 (`docs/dev/todo-and-known-bugs.md`, "Renaming a vessel mid-mission (no recorder hook)").
**Out of scope:** mission / group naming mechanics (owned by `docs/parsek-missions-design.md`; this doc only changes which surfaces read the mission name); Logistics route names (route-owned).
**Related docs:** `docs/parsek-missions-design.md`, `docs/dev/design-gui-inventory.md`, `docs/dev/research/origin-proof-partner-identity-memo.md`, `docs/parsek-rewind-to-separation-design.md`.

---

## 1. Introduction

Parsek stores a vessel name on every recording segment (`Recording.VesselName`), on every vessel snapshot (`VESSEL.name` in `<id>_vessel.craft` / `_ghost.craft`), on the tree (`RecordingTree.TreeName`), and derives group and mission names from the tree. Before this design none of these followed a stock rename: `Source/Parsek` had no `GameEvents.onVesselRename` subscriber, `Recording.VesselName` was written once when a segment was created, and Parsek's own Recordings-tab rename changed one segment's label and nothing else.

### 1.1 Owner rulings (2026-10-10)

1. **One current name per physical vessel since launch.** It is the last name the player chose for that vessel, applied to every recording leg, ghost and replay of it, and it survives rewinds, quickloads and other reloads. No rename history is kept.
2. **Mission names are separate and Parsek-owned** (missions do not exist in stock). A vessel rename never changes a mission name.
3. **Parsek's vessel rename writes through to stock**: the live vessel, a Parsek-spawned vessel, and the name a later spawn gets.

Stock renaming stays the main authority: a name the player gives in the stock rename dialog is the same kind of decision as a Parsek rename.

Follow-up rulings (2026-10-11):

4. **Only player renames propagate.** Automatic stock names (split child, the old vessel renamed after staging, undock restore, a part naming taking over after a dock) label the legs recorded after them and never rename an identity's earlier legs.
5. **Revert drops stock renames made inside the reverted flight.** Renames made in Parsek's UI always survive; rewind and F9 keep every rename.
6. **EVA rows refuse a Parsek rename** (their name is the kerbal's name).
7. **One name since launch includes the launch leg**: the vessel that keeps the launch identity renames its launch leg too, even when that leg carried other vessels that later separated (worked example, UT 2400).

### 1.2 What the player sees

| Action | Before | After |
|---|---|---|
| Rename the active vessel in flight while recording | Earlier legs, ghost, Missions row, Timeline keep the old name; the next segment and the end spawn take the new one; one probe can show as two vessels | Every leg of that vessel, its ghost, map / Tracking Station entry and every Parsek window show the new name |
| Rename a probe in the Tracking Station after the mission was committed | Nothing in Parsek changes; a rewind respawns it under the old name | All legs and ghosts of that probe show the new name; after a rewind the respawned / reloaded probe carries the new name |
| Rename a row in the Recordings tab | One segment relabelled; spawned vessel keeps the old name while "Vessel 'X' has appeared!" prints the new one | The whole vessel is renamed, in Parsek and in stock (live or spawned vessel, future spawns) |
| Rename a mission | Mission and folder renamed; merge dialog and Logistics labels keep the launch name | Unchanged ownership; those labels read the mission name |

### 1.3 Worked example

Mission "Mission1" launches at UT 1000 carrying probes P1 and P2. The player set per-part Vessel Naming on P2's probe core in the editor ("P2").

| UT | Event | Stock name(s) | Parsek identity and name |
|---|---|---|---|
| 1000 | Launch | "Mission1" (guid A) | identity A, no player decision; legs show "Mission1" |
| 1300 | Stage: lower stage drops | booster gets "Mission1 Debris" (new guid D) | identity D (debris) |
| 2000 | Decouple P2 | new vessel named "P2" by its naming part (guid B) | identity B; its first leg shows "P2" |
| 2400 | Player renames the continuing vessel to "P1" in the stock dialog | guid A now "P1" | player decision: identity A = "P1". Every leg of A, INCLUDING the launch leg 1000-2000 that carried P2, now shows "P1" (ruling 1: one name per identity since launch). The mission stays "Mission1" (ruling 2) |
| 3000 | Commit; P1 lands, P2 orbits | | both spawn at their ends with their current names |
| later | Tracking Station: player renames P2 to "Relay-2" | guid B' (Parsek-spawned vessel, new guid) | B' links to identity B through the spawn link; identity B = "Relay-2"; its ghost entry becomes "Ghost: Relay-2" |
| later | Rewind to UT 1500 | quicksave vessels carry "Mission1" | after load, live vessels of identity A are renamed "P1" again; the ghost of B replays as "Relay-2" |

---

## 2. Design Philosophy

1. **Identity first, name second.** A name is an attribute of a vessel identity and never a key. Every code path that currently matches vessels by name is converted to identity before names start moving (section 7.9).
2. **Only player decisions propagate.** Stock also names vessels automatically (split children, the old vessel after a staging split, undock restore, EVA). Those names are captured per leg exactly as stock gives them. Only a player decision (stock dialog, stock part naming that renames the vessel, Parsek rename) becomes the identity's current name and is applied to all its legs. Ruling 4.
3. **One authority, derived copies.** The current name of an identity lives in one store. `Recording.VesselName`, snapshot names, ghost proto names and CommNet node names are derived copies, re-applied from the store at defined points. Nothing compares or merges derived copies to decide a name.
4. **Write-through behaves like a stock player rename.** Parsek renames a live vessel the way the stock dialog does (name, naming part, `onVesselRename` event), so contracts, the PAW and stock UI stay in step.
5. **Parsek's knowledge outlives stock reloads.** A stock quicksave carries the name as of the quicksave; after any load Parsek re-applies the current names to live vessels.
6. **Strict matching for writes.** Renaming the wrong ship is worse than missing a legacy leg: a write requires a positive launch-identity match, never a bare persistent id.
7. **No new UI.** No new window, badge or message. Existing rename affordances keep their look; only what they change grows.
8. **One answer to "same vessel".** `VesselIdentity` becomes the shared resolver. `MissionVesselNaming`'s per-tree union and the identity matches converted in Phase 0 call it instead of keeping their own heuristics, so the feature removes parallel identity notions rather than adding one.

---

## 3. Terminology

| Term | Meaning | Code |
|---|---|---|
| Launch guid | KSP `Vessel.id`, fresh per launch, not stored in the `.craft` | `Recording.RecordedVesselGuid`, `VesselLaunchIdentity` |
| Vessel identity | One physical vessel since its launch (or since the split / spawn that created it), across segments, trees, rewinds and re-keys | new `VesselIdentity` resolver (section 6) |
| Identity guids | Every launch guid an identity's legs and links carry; a decision is indexed under all of them | `VesselNameStore` guid index |
| Re-key | KSP giving a vessel a fresh guid while it stays the same ship: Parsek spawn (`VesselSpawner.RegenerateVesselIdentity`), undock of the absorbed half | link rows (section 6.2) |
| Player decision | A name the player chose: stock rename dialog accept, stock part naming accept that renames the vessel, Parsek Recordings-tab rename | `VesselNameDecision` |
| Captured name | The stock name a leg had when it was recorded | `Recording.VesselName` when no decision exists |
| Display name | Decision name if the identity has one, else the captured name | `Recording.VesselName` after the apply pass |

---

## 4. Mental Model

```
 stock rename dialog ----+
 stock part naming ------+--> VesselNameStore (identity guids -> decision) <-- Parsek row rename
                         |          |                 ^
                         |          | apply           | resolve identity
                         |          v                 |
                         |   Recording.VesselName   VesselIdentity resolver
                         |   (all legs of identity)   (guid, chain, links)
                         |          |
                         |          +--> ghosts (map / TS proto, CommNet, labels)
                         |          +--> Parsek windows (version-keyed caches)
                         |          +--> spawn node VESSEL.name
                         |
 load / rewind / F9 -----+--> re-apply decisions to live stock vessels (write-through)
```

A leg never learns its name from another leg. It either keeps the stock name it captured, or takes its identity's decision.

---

## 5. Existing Systems (findings the design builds on)

### 5.1 Stock KSP 1.12.5 (decompiled)

- `GameEvents.onVesselRename` is `EventData<HostedFromToAction<Vessel, string>>` (host, from, to).
- Fired by `Vessel.onVesselRenameAccept` (private, `Vessel.cs:11240` in the decompile; the rename dialog callback, reached from PAW "Rename Vessel" on `ModuleCommand` / `KerbalEVA` and the map / Tracking Station vessel info header) and by `Vessel.RunVesselNamingUpdates` (part-naming driven). Also fired with `from == to` as a UI ping by `ModuleAsteroid`, `ModuleComet`, `CometVessel`, `DeployedScience`, `ModuleGroundExpControl`, `ModuleGroundSciencePart`, and on a type-only change. There is no KSC-scene rename path.
- `onVesselRenameAccept` sets `vesselName`, `vesselType`, updates `vesselNamedBy.vesselNaming` when non-null, then fires the event. `vesselNamedBy` is only set by `RunVesselNamingUpdates` and is null after a load, so a dialog rename right after a load does not update a part naming, and the next `onVesselWasModified` can revert the name to the part's naming (inferred from code, not reproduced live).
- Part naming: `Part.SetVesselNaming` opens the dialog, `Part.onVesselNamingAccept(string, VesselType, int)` writes `part.vesselNaming` and fires `onPartVesselNamingChanged`, which may rename the vessel through `UpdateVesselNaming`.
- Most automatic namings write `vesselName` directly with NO event: a split child with no naming part gets `Vessel.AutoRename(child, parentName)` (`Part.cs:5982`); undock restores the departing half's pre-dock name from `DockedVesselInfo.name` (`Part.cs:7409`, stored at dock time by `ModuleDockingNode.cs:4424-4428`) or `AutoRename`; EVA sets the kerbal name; after a staging split where the control part left, `ModuleDecouple.WaitAndSwitchFocus` renames and retypes the OLD vessel one Update later (`ModuleDecouple.cs:350-361`). Automatic namings driven by a naming part DO fire `onVesselRename` (from != to) through `RunVesselNamingUpdates` (`Vessel.cs:11376-11451`): a split child with a naming part (`Part.cs:5971`), a dock where a higher-priority naming part takes over, a naming part leaving, and the post-load `vesselNamedBy == null` revert. So `onVesselRename` is not a player signal.
- A split child's name is final before `onPartDeCoupleComplete`, `onPartDeCoupleNewVesselComplete` and `onVesselsUndocking` (null at `onVesselCreate`). The old vessel's post-staging rename may land after the next FixedUpdate.
- Dock merge: the dominant vessel survives with its name (`Vessel.GetDominantVessel`: type, then mass, then id).
- Persistence: in planetarium scenes `Game.Updated` rebuilds `flightState` from `Vessel.BackupVessel`, which reads `Vessel.vesselName` for loaded and unloaded vessels, so writing `Vessel.vesselName` is enough there. A ProtoVessel-only entry needs `ProtoVessel.vesselName`.
- Display: the Tracking Station list and map labels read the name live every frame through `DiscoveryInfo` callbacks; no refresh event is needed. `SpaceTracking` does not subscribe to `onVesselRename`.
- Names can be raw localization tags (`#autoLOC_501224` in stock craft); compare raw, display through `Localizer.Format`.
- No public stock API renames a vessel with a given name: Parsek must emulate `onVesselRenameAccept`.

### 5.2 Parsek today

- Capture: root `TreeName` and root leg name from the live vessel at record start (`ParsekFlight.cs:13496`, `:13511`); split / EVA / dock / switch legs from the live vessel one frame after the event (`ParsekFlight.cs:5986-5991`, `:6761`, `SwitchSegmentBuilder.cs:563`); background parent continuation COPIES the parent's stored name (`BackgroundRecorder.cs:1747`); the stop-time capture (`FlightRecorder.cs:8296`) is dropped by `AppendCapturedDataToRecording` in tree mode.
- Copies between recordings: optimizer split (`RecordingStore.Optimization.cs:304`), splitter TIP (`RecordingTreeSplitter.cs:771`), session merge (`SessionMerger.cs:121`), hydration repair (`ParsekScenario.HydrationRepair.cs:1026`, `:1210`), Re-Fly fork (`RewindInvoker.cs:322`). Optimizer merge (`RecordingOptimizer.MergeInto`) keeps the earlier leg's name and takes the later leg's snapshot.
- Overwrites: `RecordingStore.TryUnionActiveReFlyTreeIntoCommitted` swaps in the Re-Fly session's older recordings by reference (caveat at `RecordingStore.cs:1855-1866`); a replace commit of a copy-on-write clone overwrites the committed copy; F9 restores the quicksave tree's names (`ParsekScenario.HydrationRepair.cs:56-106`).
- Spawn: the spawned vessel takes the snapshot `name`; `RegenerateVesselIdentity` (`VesselSpawner.cs:5619`) gives it a fresh guid and pid, and nothing records that guid. Chain-tip spawns keep the identity (`VesselSpawner.cs:797-804`).
- Resume of a spawned vessel rewrites the recording's pid, clears `SpawnedVesselPersistentId` and keeps its old guid (`ParsekFlight.cs:15595-15610`); the post-resume legs capture the live (spawned) guid.
- Recovery and terminal stamping REQUIRE a name match: `ParsekScenario.MatchesVessel` (`ParsekScenario.cs:8481-8497`) returns false unless `identity.MatchesName(rec.VesselName)`; it gates `UpdateRecordingsForTerminalEvent` (Recovered / Destroyed stamps), `HasPendingLedgerRecordingForVessel` (recovery funds outside FLIGHT) and `RecoveryDispatchScope.ResolveOwner`. `LedgerOrchestrator.PickRecoveryRecordingId` (`LedgerOrchestrator.cs:6774`) has a name fallback.
- The stop-time capture names come from the ACTIVE vessel (`FlightRecorder.cs:8620`, `:8650`) or a fallback (`:9155`), not necessarily the recorded vessel.
- Display: map / TS ghost proto `"Ghost: " + VesselName` baked at build (`GhostMapPresence.cs:11459`); CommNet node name set once (`GhostCommNetManager.cs:745`); `MissionVesselNaming.NameVersion` is read only by the Missions-tab cache; the Mission Log, Timeline, Kerbals window, `CommittedFutureIndex`, `DockEventGraphCache` (and the Missions digest / chapter caches keyed on it) and the Recordings name sort never refresh on a rename.
- Per-tree vessel grouping exists for display only: `MissionVesselNaming.Build` (chain, shared launch, re-pidded continuation by part pids) and `EffectiveState.WalkSlotVessel` (forward walk within one tree). Neither crosses trees or maps a live vessel.

---

## 6. Data Model

### 6.1 VesselNameStore (new, static, owned by `ParsekScenario`)

```
VesselNameDecision (class)
  guids: List<string>        - every normalized launch guid in the identity at decision time
  name: string               - raw name the player chose (trimmed, non-empty)
  source: VesselNameSource   - StockDialog=0, StockPartNaming=1, ParsekRename=2
  decidedUT: double          - game UT of the decision (diagnostics, conflict rule)
  reachEndUT: double         - NaN while open; else the UT of the first AUTOMATIC stock rename
                               of this identity after the decision (ruling 4): legs starting at
                               or after it keep their captured names
  flightEpoch: int           - FlightEpoch at decision time (revert rule, 7.6); 0 = outside a flight

VesselIdentityLink (struct)
  guid: string               - a re-keyed launch guid
  toGuid: string             - a guid of the identity it continues
  kind: VesselIdentityLinkKind - Spawn=0, Resume=1, UndockReturn=2

DockAbsorbRow (struct)
  rootPartUId: uint          - root part flightID of the absorbed vessel at dock time
  guid: string               - the absorbed identity's launch guid

VesselNameStore (static)
  decisions: List<VesselNameDecision>, indexed guid -> decision
  links: Dictionary<string guid, VesselIdentityLink>
  dockAbsorbs: Dictionary<uint rootPartUId, DockAbsorbRow>
  FlightEpoch: int           - bumped when stock creates a new revert target (FlightDriver.PostInitState)
  revertUndo: per-epoch list of (decision replaced, decision added) - memory only, never saved
  Version: int               - bumped on any change (feeds DisplayNames.Version)
```

Resolution of an identity's decision: collect every guid of the identity's component (its recordings' guids plus link guids); look each up in the guid index; when several decisions are found (two components joined by a later link), the latest `decidedUT` wins, the loser is dropped, and a Warn names both. A decision is written under every guid present at decision time, so the key never depends on which guid was "first", and adding, trimming (F9) or re-linking recordings cannot orphan it. A decision is REFUSED for an identity with no known guid (legacy recordings): a Parsek rename of such a row is refused with the existing refusal path, which also keeps the legacy name fallbacks of 7.9 valid (their names never move).

Why a separate store instead of per-recording names: recordings are split, merged, cloned, swapped by reference (Re-Fly union) and restored from older quicksaves; a name living only on `Recording` is overwritten by every one of those paths (5.2). The store is touched by nothing but decisions, links and their lifecycle.

### 6.2 Identity resolution (new, pure: `VesselIdentity.cs`)

Union-find over every recording visible to the resolver (committed, pending, live active tree; the live tree may be a DeepClone of a committed tree with the same ids, both resolve to the same keys). Edges, strongest first:

1. Same normalized `RecordedVesselGuid`, both known (across trees; guid alone, so the resumed-spawn mixed key at `ParsekFlight.cs:15602` stays linked).
2. Same `ChainId` (chain members share pid and guid by construction).
3. `VesselSwitchContinuation` sole child (the classifier `WalkSlotVessel` already uses).
4. Link rows: a leg whose guid has a link joins the identity of the link's `toGuid` (spawn, resume, undock return).
5. Legacy legs with no guid on either side: pid match ONLY inside one tree's topology (parent / continuation), never across trees, never between two launch roots.
6. In-tree re-pidded continuation (moved from `MissionVesselNaming.MergeRepiddedContinuations`): the fallback for undocks recorded before DockAbsorbRows existed. In-tree only, because craft-baked part pids collide across launches.

EVA legs are kerbal identities (key = kerbal name) and excluded from vessel naming, as today (`MissionVesselNaming.cs:277`). Debris legs are vessel identities like any other.

Live vessel -> identity: the live `Vessel.id` (or `VesselLaunchIdentity.ReadLaunchGuid(ProtoVessel)` in TS / KSC) looked up in the guid index (every guid any member carried plus link guids); ghost map pids are skipped (`GhostMapPresence.IsGhostMapVessel`). A write requires this positive guid match; a bare pid never resolves.

Links for vessels that already exist when the feature ships, or that the resume path re-keys:
- Spawn link backfill on load: a live vessel that matches a recording's `SpawnedVesselPersistentId` through the GENUINE-spawn branch of `VesselLaunchIdentity.LiveVesselIsRecordedSpawn` (never the adoption-stamp branch, where the spawn pid equals the recorded pid) gets a Spawn link from its guid to the recording's guid. This covers every Parsek spawn made before the upgrade.
- Resume link: `TryTakeCommittedTreeForSpawnedVesselRestore` (`ParsekFlight.cs:15595-15610`) records a Resume link (live guid -> resumed recording's guid) BEFORE it clears `SpawnedVesselPersistentId`.

`VesselIdentity` and the apply pass read raw committed recordings, superseded ones included (a decision names the physical vessel, whatever the timeline): both files get `[ERS-exempt]` entries in `scripts/ers-els-audit-allowlist.txt` (physical-identity correlation).

### 6.3 Changes to existing types

- `Recording.VesselName`: meaning becomes "display name": the identity's decision when one exists, else the captured stock name. Key, layout and codec unchanged.
- `MissionVesselNaming.NameVersion` becomes `DisplayNames.Version` (global), with a static `DisplayNamesChanged` event. Bumped by: decision / link change, `ApplyRecordingRename` (now a decision), `MissionStore.RenameMission` / `MissionGroupLink.RenameMissionGroup`, hydration repair.

### 6.4 Serialization

New `VESSEL_NAMES` node inside the ParsekScenario `.sfs` node:

```
VESSEL_NAMES
{
    DECISION
    {
        guid = 6f1c...            // one line per guid
        guid = 2b70...
        name = Relay-2
        source = StockDialog
        ut = 123456.7             // R, InvariantCulture
        reachEnd = 130001.2       // absent while open
    }
    LINK
    {
        guid = 9a2e...
        to = 6f1c...
        kind = Spawn
    }
    DOCK_ABSORB
    {
        rootUid = 4123456789
        guid = 2b70...
    }
}
```

`flightEpoch` and the revert undo list are memory only (a revert is never available across a cold load). Additive: absent node = empty store. No recording schema generation bump (no recording key renamed, added or removed). Loaded on the cold path next to `MissionStore`; kept in memory across in-session loads (section 7.6).

---

## 7. Behavior

### 7.1 Stock player rename captured

Trigger: Harmony postfix on `Vessel.onVesselRenameAccept(string, VesselType)` and on `Part.onVesselNamingAccept(string, VesselType, int)` (both private; signatures from the decompile, to be confirmed by `ilspycmd` before patching). `onVesselRename` alone cannot tell a player decision from part-naming automation.

Order: resolve the vessel's identity (6.2); if none (a vessel Parsek never recorded), do nothing. If the name changed (raw compare; `from == to` and type-only changes are ignored): record a decision (source StockDialog / StockPartNaming), update the vessel's priority naming part so the post-load `vesselNamedBy == null` trap cannot revert it (5.1), run the apply pass (7.4), bump the version. Ghost map vessels are filtered first.

Automatic renames end a decision's reach (ruling 4). Two signals: (a) `onVesselRename` with from != to that is NOT inside either accept postfix and NOT inside Parsek's own writer (7.3), for a vessel whose identity has an open decision and whose new name differs from it; (b) a leg captured after `decidedUT` (7.4 capture refresh) whose captured stock name differs from the open decision (the no-event automatic renames: `ModuleDecouple` old-vessel rename, undock restore, AutoRename). Either sets `reachEndUT` to the current UT (or the leg's start UT for b) once; a later player decision on the identity replaces the decision and opens a new reach.

The postfix for part naming records a decision only when the accept actually changed `vessel.vesselName` (the part became the priority naming part); a part naming that only pre-sets a future split name is not a decision for the current vessel: the future child captures it as its first name when it separates.

### 7.2 Parsek row rename (write-through)

`MissionVesselNaming.ApplyRecordingRename` (Recordings tab commit, seam `op=edit field=recordingname`) becomes a decision for the row's identity (source ParsekRename), then: apply pass (7.4), then write-through (7.3) to every live vessel of the identity. Refusals: blank, unchanged, EVA row (ruling 6). Mission and group names are untouched.

### 7.3 Write-through to a live stock vessel

One helper, `VesselRenameWriter.ApplyStockName(Vessel v, string name)`, emulating `onVesselRenameAccept` without changing the type:

1. `Vessel.IsValidVesselName(name)` else refuse (log).
2. `from = v.vesselName; v.vesselName = name`; if the vessel is unloaded also `v.protoVessel.vesselName = name`.
3. Update the priority naming part: loaded `VesselNaming.FindPriorityNamePart(v)`; unloaded the highest-priority `ProtoPartSnapshot.vesselNaming` (priority > 0).
4. Fire `GameEvents.onVesselRename` (contracts' `SpecificVesselParameter`, PAW, info header) inside a re-entry guard so Parsek's own capture ignores it.

Callers: 7.2, the load re-apply (7.6), the spawn stamp (7.5), undock return (7.7). Never for ghost map vessels (those use 7.8).

### 7.4 Apply pass (store -> derived copies)

`VesselNameStore.ApplyToRecordings()`: for each recording whose identity has a decision, whose start UT is before the decision's `reachEndUT` (all legs while the reach is open), and whose `VesselName` differs, set it; legs at or after `reachEndUT` keep their captured names; bump `DisplayNames.Version` once if anything changed; one summary log line with counts. Runs: after any decision, at the ParsekScenario OnLoad tail (after hydration repair and the journal finisher), after every commit / merge / Re-Fly union / optimizer pass, and self-heals on a `RecordingStore.StateVersion` change in the per-frame host (O(recordings), only when the version moved). This covers every overwrite path in 5.2 without touching them individually.

Captured names are refreshed at the leg's close ONLY from the recorded vessel itself, resolved by pid + guid; when that vessel is gone or not positively matched (a dock-merge stop, where the absorbed vessel no longer exists; a split stop, where the active vessel may already be the new pod) the leg keeps its existing name. The stop-time `CaptureAtStop.VesselName` is NOT copied (it is the active vessel's name, `FlightRecorder.cs:8620`). The background parent continuation reads its live parent vessel's name instead of the parent recording's copy (`BackgroundRecorder.cs:1747`).

### 7.5 Spawns

- `VesselSpawner` stamps `VESSEL.name` in the spawn node from the identity's display name, on both the regenerate and the `preserveIdentity` path. Snapshot sidecars are NOT rewritten (a name-only rewrite would need `MarkFilesDirty`, whose `.prec` epoch advance is the F9 stale-sidecar trap).
- `RegenerateVesselIdentity` records a Spawn link (new guid -> source identity). This replaces relying on `SpawnedVesselPersistentId`, which about ten sites reset to 0.
- The "Vessel 'X' has appeared!" message and the spawned vessel now read the same name.

### 7.6 Loads, rewinds, quickloads

The store is static memory like `RecordingStore`: cold load reads the save; every in-session load (plain rewind, Re-Fly start, Discard Re-fly, stock revert, F9, scene change) keeps memory. New `LoadReconcilePolicy` category `VesselNames`, NOT consumed early (`DecideEarly` throws when the three refined in-session kinds disagree, `LoadReconcilePolicy.cs:371-391`): Cold = Save; QuickloadFlight and every other in-session kind = Memory; StockRevert = a new action `MemoryUndoRevertedFlight`, applied after `RevertDetector.Consume`.

Revert (ruling 5): `FlightEpoch` is bumped when stock creates a new revert target (`FlightDriver.PostInitState`, hooked where Parsek already observes it; to confirm at implementation). Every decision recorded while an epoch is current pushes an undo entry (the decision it replaced, possibly none, and the one it added) onto that epoch's list. On StockRevert, walk the reverted epoch's list newest first: for a StockDialog / StockPartNaming entry, restore the replaced decision (or remove the added one); ParsekRename entries are kept, and a ParsekRename made after a stock one on the same identity keeps the ParsekRename as current. Example: TS rename "Relay-2", then a stock rename "Relay-X" in a flight that is reverted: the decision returns to "Relay-2", which is also what the reverted live vessel carries.

After a load the live stock vessels carry the quicksave's names. Re-apply decisions:
1. OnLoad tail: walk `HighLogic.CurrentGame.flightState.protoVessels`, set `ProtoVessel.vesselName` (and naming part snapshots) for every proto that resolves to an identity with a decision (covers TS, KSC, unloaded vessels).
2. FLIGHT `OnFlightReady`: walk loaded `FlightGlobals.Vessels` with 7.3 (a FLIGHT->FLIGHT revert restores loaded vessels OnLoad does not see; precedent at `ParsekScenario.cs:4110-4113`).
3. Ordering: the loaded-vessel pass runs once, after the post-load strip has finished: from the end of `RewindInvoker.RunStripActivateMarker` when a rewind or Re-Fly is consuming the load (its strip is deferred to `onFlightReady`, `RewindInvoker.cs:1179-1200`), else from the first host tick after `OnFlightReady`. The OnLoad-tail proto pass (step 1) may run before a strip: that is safe because the strip / kill paths match by identity after Phase 0, and their remaining name fallbacks apply only to guid-less legacy recordings, which never carry a decision (6.1).
4. Decisions with a closed reach are re-applied only when the current UT is before `reachEndUT` (the live vessel predates the automatic rename); otherwise the live vessel keeps its later automatic stock name.

### 7.7 Splits, docks, undocks

- Split child: a new identity named by stock (AutoRename or its naming part). No decision is inherited: P2 does not take the parent's decision.
- Continuing vessel keeps its guid and identity; if it has a decision, the new leg captures the live name, which already equals the decision.
- Old vessel renamed one Update after a staging split (5.1): an automatic name, captured on the leg's close (7.4), never a decision.
- Undock mechanics (decompiled `ModuleDockingNode.Undock` / `Part.Undock`): stock always undocks the CHILD side of the coupling joint (`if (parent != otherNode.part) otherNode.Undock()`), and the absorbed vessel was attached as the child at `Part.Couple` (`ModuleDockingNode.cs:4441`). `Part.Undock` builds the leaving vessel with `vessel.id = Guid.NewGuid()`, re-roots it on the pre-dock root part when `DockedVesselInfo.rootPartUId` is in its subtree, and names it from `DockedVesselInfo.name` (the name at dock time). The half left behind keeps the merged vessel's guid, which is the dominant's. A ship built docked in the editor (`undockPreAttached`) leaves through `Decouple`, a plain split. So the ordinary dock / undock round trip ALWAYS re-keys the returning ship: without the UndockReturn link its post-undock legs are a different identity from its pre-dock legs, and a rename after any undock would leave every pre-dock leg on the old name (ruling 1). The link is required, not an edge case.
- Dock merge: the merged leg belongs to the dominant identity (stock keeps its name). The absorbed identity's legs keep their names; its decision (if any) stays dormant. A rename while docked is a decision for the dominant identity only (same as stock). At the dock event Parsek records a DockAbsorbRow (absorbed vessel's root part flightID -> its guid). A naming part taking over the merged vessel's name is automatic: it ends the dominant decision's reach (7.1 signal a).
- Undock: stock restores the departing half's pre-dock name. At `onVesselsUndocking` Parsek looks the departing vessel's root part flightID up in `dockAbsorbs`; on a hit it adds an UndockReturn link (departing guid -> absorbed guid) and writes the absorbed identity's decision through (7.3) when it differs (a Parsek rename made while it was docked). Docks recorded before the upgrade have no row: fall back to the in-tree re-pidded-continuation rule `MissionVesselNaming.MergeRepiddedContinuations` already applies (a leg that starts after one of the tree's own vessels ended and shares part pids with it, best match per hop), moved into `VesselIdentity` as an in-tree-only edge; otherwise the departing half is a new identity (for example a ship built docked in the editor). Chained docks work unchanged: a tanker docked to the ferry stays inside the leaving vessel and keeps its own row for its own later undock. Parsek does not read `DockedVesselInfo` (both nodes carry one, `ModuleDockingNode.cs:4424-4430`).

### 7.8 Ghosts

- Map / TS ghost protos: in the per-tick host (`UpdateFlightMapGhostLifecycle`, `UpdateTrackingStationGhostLifecycle`), when `DisplayNames.Version` moved, walk `vesselsByRecordingIndex`, `overlapInstanceVessels` and `vesselsByChainPid` and set `vesselName` / `protoVessel.vesselName` to `GhostVesselNamePrefix + display name` in place. No event, no rebuild (a rebuild trips line-blink / icon-jump and drops targets). Stock list and labels read the name live (5.1). Resolve the recording index by id at apply time; never cache it across frames (committed-list index contract).
- CommNet: the per-tick refresh loop (`GhostCommNetManager.cs:621-640`) copies a changed name into `Node.displayName`; derive the name from `rec.VesselName` first, the snapshot second.
- Flight ghost labels, map markers and the watch label already read `rec.VesselName` per frame.

### 7.9 Name-as-identity sites converted (phase 0, before names move)

| Site | Today | Change | Risk if left |
|---|---|---|---|
| `RewindInvoker.ResolveInPlaceContinuationDebrisToKill` (`:3822-3866`) | kills left-alone quicksave vessels whose name matches a recording | guid + pid (as `StripOrphanedSpawnedVessels` does) | high: `Die()` on a same-named ship, or a doubled vessel |
| `RecordingStore.RewindOwnerStrip` (`:86-164`), legacy `FromNames` overload | name-only fallback when unanchored | guid-gated; name fallback only for guid-less legacy | medium: strips another launch renamed to the owner's name |
| `VesselSpawner.ShouldRecoverBlockerVessel` (`:2959-2973`) | name equality before the spawn-pid check | drop the name gate; spawn pid + Spawn link guid | medium: #112 recovery silently skipped, walkback instead |
| `RecordingStore.SupersedeTerminalSpawn.cs:233` | same-name + UT contiguity fallback | `RecordingsShareLaunch` / identity | medium: missed supersede, double spawn |
| `ParsekFlight.cs:14286`, `:14321` quickload restore | name fallback after pid | pid + guid only | low-medium |
| `ParsekFlight.cs:17367` `RecoverTimelineSpawnedVessel` | matches by leaf name | spawn pid / Spawn link | medium |
| `GhostPlaybackEngine.cs:3138-3167` watch handoff | prefers members with the watched name | ChainId / identity | low (camera) |
| `PostLoadStripper.FindTreeNameCollisions` (`:304`) | name collision warning | identity, or keep as diagnostic | none (log) |
| `ParsekScenario.MatchesVessel` (`:8481-8497`): `UpdateRecordingsForTerminalEvent` (`:8315`), `HasPendingLedgerRecordingForVessel` (`:8438`), `RecoveryDispatchScope.ResolveOwner` (`RecoveryDispatchScope.cs:283`) | name REQUIRED, then pid / guid | positive pid + guid match is sufficient; name required only when the guid is unknown | high: a renamed vessel's recovery misses the Recovered stamp and its recovery-cost actions |
| `LedgerOrchestrator.PickRecoveryRecordingId` (`:6774`) | name fallback after guid | name fallback only for guid-less legacy | medium |
| `LedgerRolloutAdoption.cs:229`, `:688` | name + site when pid is 0 | keep (history, legacy only) | low |

### 7.10 Mission names and TreeName

Mission names stay Parsek-owned (`MissionGroupLink`). `TreeName` stays the raw launch name for its non-display roles (root group generation, default mission seed, seam lookup, persistence, about 60 log sites and 21 harness logContract regexes that pin `tree='...'`). Display readers switch to the mission display name (`MissionStore.FindOriginalMission(treeId)?.Name ?? TreeName`): `MergeDialog.cs:209-210`, `:387-388`, `:672`, `MergeDialog.DialogText.cs:171`, `LogisticsWindowUI.cs:1319`, `:2505`, `Logistics/RouteRunPrompt.cs:177`, `RecordingStore.cs:5802`, `MissionVesselNaming.cs:283`. Fix the two false comments (`RecordingStore.cs:5772-5774`, `RecordingsTableUI.cs:4911-4912`).

### 7.11 Display refresh and numbering

Every cache that bakes a vessel or mission name keys on `DisplayNames.Version` or subscribes to `DisplayNamesChanged`: Missions naming cache, Mission Log `ComputeChangeSignature`, Timeline `InvalidateCache`, Kerbals `cachedVM`, `CommittedFutureIndexCache`, Recordings name sort, `DockEventGraphCache` (and the Missions digest / chapter caches and dock-partner tooltip memo keyed on it), TS atmospheric focus target.

Numbering: `MissionVesselNaming` groups legs by vessel first and then numbers per name (today it buckets by name first, which splits one vessel with two names). Logistics "Flights used" and near-miss labels use `MissionVesselNaming` numbering instead of `NumberRepeatedNames`. Map, TS, watch, screen messages, Kerbals and Timeline show raw display names. A `[n]` suffix is never written to stock.

---

## 8. Edge Cases

1. **Rename to a name another vessel already has.** Allowed (stock allows it); identity keeps them apart; Missions numbering disambiguates within a mission.
2. **Localization tag names.** Store and compare raw; display through `Recording.ResolveLocalizedName` / `Localizer.Format`. A player-entered name is literal.
3. **`from == to` events, type-only changes, asteroid / comet / deployed-science pings.** Ignored (7.1 only reacts to the two accept methods).
4. **Rename of a vessel Parsek never recorded.** No identity, nothing stored. If it is recorded later, its legs capture the live name.
5. **Rename during a Re-Fly session.** The decision lives in the store; the union swap at commit restores older `VesselName`s and the apply pass re-applies the decision.
6. **Rename then F9 to an earlier quicksave.** Decision kept (ruling 1); live vessels renamed again by 7.6.
7. **Rename during a flight that is then reverted.** A StockDialog / StockPartNaming decision stamped with the reverted flight's session is dropped on StockRevert; ParsekRename decisions survive (ruling 5).
8. **Rename of an absorbed (docked) vessel's past legs in Parsek.** Decision stored; no live target while docked (the merged vessel is the dominant identity); written through at undock (7.7).
9. **Optimizer merge of two legs with different captured names.** Same identity; with a decision both show it; without one the earlier leg's name wins as today.
10. **Split child with a pre-set naming part.** Child's first leg captures the part's name (the player's pre-set intent); no decision record needed.
11. **Two vessels of one legacy, guid-less launch.** Union only inside one tree's topology; a rename may miss legacy legs in other trees (accepted: strict writes).
12. **Spawned vessel renamed, then a rewind past its spawn UT.** The vessel is removed by the rewind (identity strip) and later respawned with the stamped decision name.
13. **Ghost map vessel reached by a stock rename UI.** Filtered (`IsGhostMapVessel`); its name is Parsek-owned.
14. **Mission clone.** Vessel names are per identity, so clones show the same vessel names; mission names untouched.
15. **Name validity.** Stock rejects only null / empty; Parsek trims and rejects blank, matching `ApplyRecordingRename`.
16. **The launch leg of a stack shows a later decision.** Expected by ruling 1 (worked example UT 2400).
17. **Staging where the control part leaves on the new vessel.** The guid (identity A) stays with the booster; the pod is a new identity named by stock. A decision on the pod does not rename the launch leg. Recorded faithfully, not "fixed".

---

## 9. What Doesn't Change

- Mission, group and route ownership and their rename UI.
- Ledger history text (`RecoveredVesselName`), rollout dedup keys, route-proof seam half names (hashed by `RouteProofHasher.cs:279`; changing them would flag every route), `RouteOriginProof.StartDockedOriginVesselName` (display may resolve live later; out of scope).
- Recording schema generation, sidecar layout, snapshot files on disk.
- Ghost visuals, playback math, trajectory data.
- Stock behavior for vessels Parsek does not know.

---

## 10. Out of Scope

- Rename history or per-UT names (ruling 1).
- Renaming kerbals or EVA vessels from Parsek (ruling 6).
- Changing vessel type or priority from Parsek.
- A new rename UI anywhere.

---

## 11. Backward Compatibility

- Saves without `VESSEL_NAMES` load with an empty store; behavior equals today until the first decision.
- Existing per-leg names are kept as captured names; no migration guesses a decision from diverging legs.
- Legacy recordings without a guid resolve by in-tree topology only.

---

## 12. Performance Budget

- Identity resolution: rebuilt only when `StateVersion` or `VesselNameStore.Version` moves; O(recordings) union-find.
- Apply pass and ghost renames: only on a version change, never per frame otherwise.
- No new per-frame work in recording or playback.

---

## 13. Error Recovery

- Failed write-through (invalid name, vessel gone): Warn, decision kept, retried on the next load re-apply.
- Store node unreadable: Warn, empty store, game continues (names fall back to captured names).
- Harmony patch target missing (KSP update): Warn once at patch time; Parsek rename and load re-apply still work, stock renames are not captured.

---

## 14. Diagnostic Logging

Subsystem tag `VesselNames`:
- Info: decision recorded (identity, old -> new, source, vessel pid / guid, UT); write-through applied (vessel, old -> new, reason: rename / load / spawn / undock); link added (kind, guid -> identity).
- Verbose: apply-pass summary (recordings changed, identities with decisions, version); load re-apply summary (protos / vessels scanned, renamed, skipped-ghost, no-identity); ghost rename summary.
- Warn: write-through refused / failed; ambiguous identity (two candidate identities for one live vessel, skipped); patch target missing.
- Existing `[UI] Recording 'X' renamed to 'Y'` stays, extended with the identity.

---

## 15. Test Plan

### 15.1 Unit tests
- `VesselIdentity` union: guid across trees, chain, switch continuation, Spawn link, UndockReturn link, legacy in-tree pid, two launches of one craft stay apart, EVA excluded.
- `VesselNameStore`: decision set / replace / no-op on blank or unchanged; codec round trip (InvariantCulture); empty node.
- Apply pass: decision overrides every leg of the identity; legs of other identities untouched; version bumps once; re-applies after a simulated Re-Fly union swap and an optimizer merge.
- Each converted site in 7.9: a renamed recording still matches by identity; a same-named different launch no longer matches.
- `LoadReconcilePolicy`: the new category's cells (the table tests enforce every kind x category).
- Cache keys: Mission Log signature, `CommittedFutureIndexCache`, `DockEventGraphCache`, sort cache move on `DisplayNames.Version`.
- `MissionVesselNaming`: one vessel with two captured names is one row; Logistics numbering matches Missions numbering.
- Reach (ruling 4): decision then the control part leaves at staging (no-event AutoRename captured on the next leg) -> earlier legs keep the decision, later legs the automatic name, and the load re-apply after the reach leaves a later live vessel alone; decision then a naming-part takeover at a dock (`onVesselRename` outside accept) ends the reach.
- Revert (ruling 5): TS rename "Relay-2", in-flight stock rename "Relay-X", revert -> "Relay-2"; a ParsekRename made in the reverted flight survives; the policy cell is `MemoryUndoRevertedFlight` and the category is not consumed early.
- Recovery after a rename: a vessel renamed after its legs were captured still gets Recovered and its recovery-cost actions (each `MatchesVessel` caller).
- Key stability: a decision survives adding a leg with a new guid, an F9 trim of the founding leg, and a new link joining two components (latest decision wins, Warn logged).
- Pre-upgrade spawn: a vessel spawned before the feature resolves through the backfilled Spawn link; an adoption-stamp vessel does not; a resumed spawned vessel resolves through the Resume link and its post-resume legs join the identity.
- Capture refresh: a split stop and a dock-merge stop keep the leg's name when the recorded vessel is not the active one / is gone.
- Log assertions for the 14 events.

### 15.2 In-game tests (new category `VesselNames`, FLIGHT)
- Stock-equivalent rename of the active recorded vessel: every leg, ghost proto name, CommNet name updated.
- Write-through: Parsek rename renames the live vessel, fires `onVesselRename` once, and is not re-captured.
- Naming part updated so an `onVesselWasModified` does not revert the name.

### 15.3 Harness lanes (fly only on request)
New seam verb `RenameVessel pid=<pid> name=<name>` calling the stock-equivalent path (adds the verb to `hlib.IMPLEMENTED_SEAM_VERBS` and the role tables, byte-equal with `TestCommandVerbs.ImplementedVerbs`). Lanes: rename mid-flight then commit; background probe rename; TS rename of a spawned vessel then rewind (no duplicate in saveParse `vesselNames`); `op=edit field=recordingname` writes through; rename then F9. A new spec pins `total=` and is registered in `INTERIM_PIN_IDS` until its first flight. GUI-25 commits a recording rename on `interbody-route-recorded`: with write-through it may also rename a live vessel there; re-read its later captures.

---

## 16. Implementation Phasing

| Phase | Content | Player-visible |
|---|---|---|
| 0 | Convert name-as-identity sites (7.9), the recovery / terminal cluster first; fix the false comments; capture refresh from the recorded vessel only (7.4 last paragraph) | no (safety) |
| 1 | `VesselIdentity` resolver, `VesselNameStore` + codec + policy cell, apply pass, `DisplayNames.Version` + cache plumbing (7.11), Parsek row rename as identity-wide decision | yes: rename is consistent inside Parsek |
| 2 | Harmony capture of stock renames and reach detection (7.1), write-through helper (7.3), spawn stamp + Spawn / Resume links + backfill (6.2, 7.5), load re-apply (7.6), revert undo (ruling 5; must ship with the re-apply, or a reverted stock rename is written back), ghost / CommNet in-place rename (7.8) | yes: stock is the authority |
| 3 | Dock absorb rows + undock return links (7.7, required: the ordinary dock / undock round trip re-keys the ship), TreeName display readers (7.10), numbering unification | yes |

Phase 1 renames every leg of an identity without renaming the live vessel; it is safe only because Phase 0 already removed every name-required match (the recovery cluster in particular). Each phase lands as its own PR with its CHANGELOG / todo updates; GC-7 closes with phase 2 or 3.

---

## 17. Open Questions

None open. The three questions of the 2026-10-10 draft (automatic names, revert, EVA rows) are rulings 4-6 (section 1.1).

---

## 18. Code Layout

| New / changed | Role |
|---|---|
| `Source/Parsek/VesselIdentity.cs` (new, pure) | union-find resolver, live-vessel lookup |
| `Source/Parsek/VesselNameStore.cs` (new) | decisions, links, codec, apply pass, version |
| `Source/Parsek/VesselRenameWriter.cs` (new) | stock-equivalent write-through |
| `Source/Parsek/Patches/VesselRenameCapturePatch.cs` (new) | postfixes on the two accept methods |
| `MissionVesselNaming.cs` | rename becomes a decision; vessel-first grouping |
| `ParsekScenario.cs` | store load / save, OnLoad tail re-apply |
| `ParsekFlight.cs` | OnFlightReady re-apply, capture fixes |
| `VesselSpawner.cs` | spawn name stamp, Spawn link |
| `GhostMapPresence.cs`, `GhostCommNetManager.cs` | in-place ghost renames |
| `LoadReconcilePolicy.cs` | `VesselNames` category |
| caches listed in 7.11 | version keys |
| `scripts/ers-els-audit-allowlist.txt` | `[ERS-exempt]` entries for `VesselIdentity.cs` and `VesselNameStore.cs` |

---

## References / Related Docs

- Research reports (2026-10-10 session): stock decompile of KSP 1.12.5 naming, Parsek identity model, load lifecycle, refresh plumbing.
- `docs/dev/research/origin-proof-partner-identity-memo.md` (dock dominance, undock re-key).
- `.claude/CLAUDE.md` gotcha "`persistentId` is craft-baked, NOT launch-unique".
