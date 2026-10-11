# Parsek Vessel and Mission Names - Design Document

*Design specification for keeping vessel names consistent with stock KSP renames across every recording, ghost, spawn and Parsek window, and for keeping mission names separate.*

**Status:** owner rulings recorded (2026-10-10 / 2026-10-11), revised after two clean reviews (2026-10-11). Phase 0 implemented (branch `vessel-naming-sync`, PR #2070), Phase 1 on top of it (branch `vessel-names-store`, PR #2071), Phase 2a on top of that (branch `vessel-names-stock`, 2026-10-11) and Phase 2b on top of 2a (branch `vessel-names-links`, 2026-10-11); phase 3 not started (section 16).
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
8. **Discard Re-fly and Retry cancel the attempt's stock renames** (2026-10-11), like a stock revert cancels the reverted flight's (ruling 5); Parsek renames made in the attempt stay.

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
- Part naming: `Part.SetVesselNaming` opens the dialog, `Part.onVesselNamingAccept(string, VesselType, int)` writes `part.vesselNaming` and fires `onPartVesselNamingChanged`, which may rename the vessel through `UpdateVesselNaming` (synchronously, inside the accept body). `RunVesselNamingUpdates` compares the new part's name with `vesselNamedBy`'s, which is the SAME object when that part already names the vessel, so renaming the part that already names the vessel does not rename the vessel (and records no decision).
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
  seq: long                  - store-wide monotonic decision number (persisted): the ORDER of player
                               choices. Game UT is never used for ordering (it moves backwards on
                               rewind and F9)
  guids: List<string>        - every normalized launch guid in the identity at decision time
  name: string               - the name the player chose (literal, trimmed, non-empty)
  priorNames: List<string>   - the names the identity's legs and live vessel carried just before
                               the decision (the names it replaced)
  coveredLegIds: Set<string>   - legs that take the decision name
  uncoveredLegIds: Set<string> - legs created after the decision that stock named automatically
  source: VesselNameSource   - StockDialog=0, StockPartNaming=1, ParsekRename=2
  decidedUT: double          - diagnostics only
  flightEpoch: int           - FlightEpoch at decision time (revert rule, 7.6); memory only

VesselIdentityLink (struct)
  guid: string               - a re-keyed launch guid
  toGuid: string             - a guid of the identity it continues
  kind: VesselIdentityLinkKind - Spawn=0, Resume=1, UndockReturn=2

DockAbsorbRow (struct)
  rootPartUId: uint          - root part flightID of the absorbed vessel at dock time
  guid: string               - the absorbed identity's launch guid

VesselNameStore (static)
  decisions: List<VesselNameDecision>, indexed guid -> decision
  nextSeq: long              - persisted
  links: Dictionary<string guid, VesselIdentityLink>
  dockAbsorbs: Dictionary<uint rootPartUId, DockAbsorbRow>
  FlightEpoch: int           - see 7.6; memory only
  revertUndo: per-epoch list of (decision replaced, decision added), full copies - memory only
  Version: int               - bumped on any change (feeds DisplayNames.Version)
```

**Coverage (ruling 4).** When a decision is recorded, every leg the identity has at that moment is covered (ruling 7: the launch leg too). A leg that appears later is classified ONCE, by id, the first time the apply pass (7.4) sees it, from its own captured stock name, which the pass has not yet touched: covered when that name is the decision name or one of `priorNames` (the leg continues the vessel the player named: normal staging, a resume, an undocked ship whose stock-restored pre-dock name is stale, a leg captured before a write-through landed); uncovered otherwise (stock named it automatically: AutoRename after the control part left at staging, a naming part taking over at a dock). Later legs derived from it capture the same automatic name and stay uncovered without any lineage tracking. Because classification is by leg id and by name, it does not depend on UT, on rewinds or F9, or on later copies overwriting `VesselName` (Re-Fly union, hydration repair, replace commit). Legs derived by copying (optimizer split, Re-Fly fork, splitter HEAD / TIP) copy the post-apply name and classify the same way as their source. A new player decision on the identity replaces the decision (new seq, all existing legs covered again; the new decision takes only the identity's current guids, and a guid of the replaced decision the identity no longer carries keeps the replaced decision, so renaming one vessel never erases another's name); its `priorNames` also keep the replaced decision's name and prior names, because until the live write rule ships (Phase 2a) the live vessel still carries its original stock name and a later leg captures it.

**Resolution.** Collect every guid of the identity's component (its recordings' guids plus link guids) and look each up in the guid index; when several decisions are found (two components joined by a later link) the highest `seq` wins and a Warn names both. The loser is folded into the winner rather than lost: its guids are re-indexed to the winner, its covered and uncovered legs join the winner's sets, and its name and prior names become the winner's prior names, so the legs the player named under the losing decision take the winning name. The same fold resolves a guid two loaded decisions both claim. A decision is written under every guid present at decision time, so adding, trimming (F9) or re-linking recordings cannot orphan it. A decision is REFUSED for an identity with no known guid (legacy recordings): a Parsek rename of such a row is refused through the existing refusal path, which also keeps the legacy name fallbacks of 7.9 valid (their names never move).

Why a separate store instead of per-recording names: recordings are split, merged, cloned, swapped by reference (Re-Fly union) and restored from older quicksaves; a name living only on `Recording` is overwritten by every one of those paths (5.2). The store is touched by nothing but decisions, links and their lifecycle.

### 6.2 Identity resolution (new, pure: `VesselIdentity.cs`)

Union-find over every recording visible to the resolver (committed, pending, live active tree; the live tree may be a DeepClone of a committed tree with the same ids, both resolve to the same keys). Edges, strongest first:

1. Same normalized `RecordedVesselGuid`, both known (across trees; guid alone, so the resumed-spawn mixed key at `ParsekFlight.cs:15602` stays linked).
2. Same `ChainId` (chain members share pid and guid by construction).
3. `VesselSwitchContinuation` sole child (the classifier `WalkSlotVessel` already uses).
4. Link rows: a leg whose guid has a link joins the identity of the link's `toGuid` (spawn, resume, undock return).
5. Inside one tree only: same pid and launch guids that do not conclusively differ (`VesselLaunchIdentity.RecordingsShareLaunch`, the rule `MissionVesselNaming` already used for a tree's own legs), never across trees. As implemented this is not limited to parent / continuation hops: inside one launch's tree a pid is unique per live vessel, and the legs that carry ANOTHER mission's earlier launch (dock partners, `MissionVesselNaming.TryResolveForeignOwner`) are unioned only among themselves, reaching their owner through edge 1, so a partner never merges into the tree's own ship through a pid.
6. In-tree re-pidded continuation (moved from `MissionVesselNaming.MergeRepiddedContinuations`): the fallback for undocks recorded before DockAbsorbRows existed. In-tree only, because craft-baked part pids collide across launches. As implemented it is restricted to exactly that: the candidate leg must be a child of an Undock branch point (a staging split, a decouple or a dock never takes it; a dock's merged leg belongs to the dominant by guid and the absorbed vessel stays itself), and the vessel it continues must have ENDED by the leg's start, counting every recording of the tree that carries one of that vessel's guids, debris included (a booster that keeps the launch guid as debris, 8.17, is still the launch vessel running), and treating an open live leg (no child branch point, no terminal state, no later chain segment: its points are still in the recorder buffer, so its EndUT is unknown) as running.

EVA legs are kerbal identities (key = kerbal name) and excluded from vessel naming, as today (`MissionVesselNaming.cs:277`). Debris legs are vessel identities like any other.

Live vessel -> identity: the live `Vessel.id` (or `VesselLaunchIdentity.ReadLaunchGuid(ProtoVessel)` in TS / KSC) looked up in the guid index (every guid any member carried plus link guids); ghost map pids are skipped (`GhostMapPresence.IsGhostMapVessel`). A write requires this positive guid match; a bare pid never resolves.

Links for vessels that already exist when the feature ships, or that the resume path re-keys:
- Spawn link backfill on load: a live vessel that matches a recording's `SpawnedVesselPersistentId` through the GENUINE-spawn branch of `VesselLaunchIdentity.LiveVesselIsRecordedSpawn` (never the adoption-stamp branch, where the spawn pid equals the recorded pid) gets a Spawn link from its guid to the recording's guid. This covers every Parsek spawn made before the upgrade.
- Resume link: `TryTakeCommittedTreeForSpawnedVesselRestore` (`ParsekFlight.cs:15595-15610`) records a Resume link (live guid -> resumed recording's guid) BEFORE it clears `SpawnedVesselPersistentId`. This restore is retried at 1 Hz (`ParsekFlight.cs:4296-4332`), so the link may appear after the post-load re-apply; the live write rule of 7.4 covers that (a covered live leg with a differing live name is written through whenever the pass runs).

As implemented (Phase 2b, `VesselNameStore.Links.cs`): every resolution the store runs (`VesselNameStore.ResolveUniverse`) passes the link rows to `VesselIdentity.Resolve`. Guids joined by links form one component even through a guid no recording carries (a spawned copy before its first leg, a copy re-spawned from a copy), every node carrying a guid of one component joins one identity, and every link guid of that component joins the identity's guid set, so a live re-keyed vessel resolves by its own guid and a decision is indexed under it. A link whose component reaches no recording is ignored. The live write rule's current leg follows the links (`Resolution.LinkTargets`) when no leg carries the live guid yet: for a spawned copy that is the spawning leg's guid, for an undocked ship its pre-dock legs. `AddLink` refuses unknown, empty (`Guid.Empty`) and self links, keeps the first row for a guid (a different second target is a Warn), and schedules the apply pass for the next host tick (`SelfHealTick`, reason `self-heal:link-added`) instead of running it inside a spawn or a stock event; the self-heal is inert while the store holds no decision, which is when a link changes nothing. The Resume link is added in `TryTakeCommittedTreeForSpawnedVesselRestore` before the block that clears the spawn flags, only on a match through the genuine-spawn branch (`IsGenuineSpawnPidMatch`, the backfill's rule): that restore's spawn-pid match is pid-only, and an adoption-stamp pid is craft-baked, so it never writes a permanent link. Edge 4's union-find iterates a snapshot of its guid keys (path compression writes the map), and a resolver fault in a Parsek rename refuses it (`resolve-failed`) instead of throwing into the Recordings tab. The spawn backfill runs at all three OnLoad tails (cold, plain rewind, in-session) just before the tail's apply pass, over every recording the store sees and the loaded game's protos, through `VesselLaunchIdentity.IsGenuineSpawnPidMatch` (the genuine-spawn branch alone); log `Spawn link backfill (reason): vessels= genuineSpawnMatches= adoptionStampSkipped= added= alreadyLinked= links=`.

`VesselIdentity` and the apply pass read raw committed recordings, superseded ones included (a decision names the physical vessel, whatever the timeline): both files get `[ERS-exempt]` entries in `scripts/ers-els-audit-allowlist.txt` (physical-identity correlation).

### 6.3 Changes to existing types

- `Recording.VesselName`: meaning becomes "display name": the identity's decision when one exists, else the captured stock name. Key, layout and codec unchanged.
- `MissionVesselNaming.NameVersion` becomes `DisplayNames.Version` (global), with a static `DisplayNamesChanged` event. Bumped by: decision / link change, `ApplyRecordingRename` (now a decision), `MissionStore.RenameMission` / `MissionGroupLink.RenameMissionGroup`, hydration repair.

### 6.4 Serialization

New `VESSEL_NAMES` node inside the ParsekScenario `.sfs` node:

```
VESSEL_NAMES
{
    nextSeq = 18
    DECISION
    {
        seq = 17
        guid = 6f1c...            // one line per guid
        guid = 2b70...
        name = Relay-2
        prior = P2                // one line per prior name
        covered = 3f0a...         // one line per covered leg id
        uncovered = 9b11...       // one line per uncovered leg id
        CAPTURED                  // one per covered leg: its own name before any decision applied
        {
            leg = 3f0a...
            name = Mission1
        }
        source = StockDialog
        ut = 123456.7             // R, InvariantCulture, diagnostics only
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

Trigger: a Harmony Prefix + Postfix pair on `Vessel.onVesselRenameAccept(string, VesselType)` (`Vessel.cs:11240`) and on `Part.onVesselNamingAccept(string, VesselType, int)` (`Part.cs:24425`), both private (signatures from the decompile, confirmed by `ilspycmd` before patching). The Prefix saves the vessel's current name in `__state`; the Postfix compares it with the name after the body ran. Detection never relies on `onVesselRename`: stock fires it INSIDE the accept bodies (before any postfix), from part-naming automation, and as a `from == to` UI ping (5.1), so it cannot tell a player decision from automation. Parsek subscribes to no `onVesselRename` handler at all.

Order in the Postfix: ghost map vessels are filtered first; resolve the vessel's identity (6.2); if none (a vessel Parsek never recorded), do nothing. If the name changed (raw compare; type-only changes are ignored): record a decision (source StockDialog / StockPartNaming, `priorNames` = the saved name plus the identity's current leg names), update the vessel's priority naming part with 7.3 step 3 (loaded or unloaded, stock's tie rule) so the post-load `vesselNamedBy == null` trap cannot revert it (5.1), run the apply pass (7.4), bump the version.

The part-naming Postfix records a decision only when the accept actually changed `vessel.vesselName` (the part became the priority naming part); a part naming that only pre-sets a future split name is not a decision for the current vessel: the future child captures it as its first name when it separates.

As implemented (Phase 2a): `Patches/VesselRenameCapturePatch.cs` holds both pairs (`VesselRenameAcceptCapturePatch`, `PartVesselNamingAcceptCapturePatch`, targets resolved by `AccessTools.Method` with the decompiled parameter types; `Prepare` Warns once and skips the class when a target is missing). The Postfix runs `ParsekGameModeGate.CheckInert`, then `VesselNameLiveSync.OnStockRenameAccepted`: the pure core `RecordStockRename` ignores a ghost map vessel, an unchanged name and a vessel with no guid match, picks the vessel's current leg (7.4 step 3) as the decision's row and records with the Parsek editor's unchanged-row refusal OFF (renaming a vessel back to the name its legs already show is still a player choice), then the naming part is set and the live write rule runs. Log: `Stock rename captured: source=... pid=... guid=... 'old' -> 'new' seq=... namingPart=...`.

Automatic renames are not detected as events: they show up as a NEW leg whose captured name is neither the decision nor a prior name, and coverage (6.1) leaves that leg uncovered. A mid-leg automatic rename with no new leg (a naming part reverting the name on `onVesselWasModified`) cannot happen for a vessel Parsek named, because every Parsek write keeps the priority naming part equal to the decision; if it happens anyway, the next live write (7.4) restores the decision.

### 7.2 Parsek row rename (write-through)

`MissionVesselNaming.ApplyRecordingRename` (Recordings tab commit, seam `op=edit field=recordingname`) becomes a decision for the row's identity (source ParsekRename), then: apply pass (7.4), then the live write rule of 7.4 for every live vessel of the identity. Refusals: blank, unchanged, EVA row (ruling 6), identity with no known guid (6.1). Mission and group names are untouched. As implemented (Phase 2a): before the decision is recorded, the names the identity's live vessels carry now (raw and capture-localized) are added to its prior names, as the stock capture adds the replaced name, so a live vessel whose name no leg shows (a stock rename before this build, an automatic stock name, a naming-part revert) reads as stale and is written through (ruling 3).

### 7.3 Write-through to a live stock vessel

One helper, `VesselRenameWriter.ApplyStockName(Vessel v, string name)`, emulating `onVesselRenameAccept` without changing the type:

1. `Vessel.IsValidVesselName(name)` else refuse (log).
2. `from = v.vesselName; v.vesselName = name`; if the vessel is unloaded also `v.protoVessel.vesselName = name`.
3. Update the priority naming part with stock's rule (`VesselNaming.cs:33-80`: the FIRST part in order with the strictly highest `namingPriority` above 0): loaded through `VesselNaming.FindPriorityNamePart(v)`; unloaded (where `v.parts` is empty) over `v.protoVessel.protoPartSnapshots[i].vesselNaming` with the same rule. Set its `vesselName` only; type and priority untouched.
4. Fire `GameEvents.onVesselRename` (contracts' `SpecificVesselParameter`, PAW, info header). Parsek has no handler on it (7.1), so no re-entry guard is needed.

A ProtoVessel-only entry (no `Vessel` object) gets `ProtoVessel.vesselName` plus the same naming-part rule over its part snapshots, with no event.

As implemented (Phase 2a, `VesselRenameWriter.cs`): `ApplyStockName` (Vessel) and `ApplyProtoName` (ProtoVessel). The tie rule is the pure `FindPriorityNamingIndex` over the parts' (or snapshots') `VesselNaming` entries, a line-for-line copy of `VesselNaming.FindPriorityNamePart`, used for loaded parts too so one tested rule serves both. A proto part snapshot built from a live part shares the part's `VesselNaming` object (`ProtoPartSnapshot` constructor), and a part loaded from a snapshot takes the snapshot's object, so writing the snapshot's entry is what the part gets on load. Refusals (blank, ghost, no vessel) Warn. Log: `Write-through applied: vessel pid=... guid=... 'old' -> 'new' loaded=... namingPart=part|proto-part|none reason=...`.

Never for ghost map vessels (those use 7.8).

### 7.4 Apply pass (store -> derived copies and live vessels)

`VesselNameStore.ApplyAll()`:
1. Classify every not-yet-classified leg of every identity with a decision (6.1 coverage), from its current `VesselName`.
2. For each covered leg whose `VesselName` differs from the decision, set it. Uncovered legs keep their captured names.
3. Live write rule: for each live vessel (FLIGHT: all of `FlightGlobals.Vessels`, loaded or not; TS / KSC: `flightState.protoVessels`) whose identity has a decision, find the vessel's CURRENT leg: the leg recording it (active or background recorder), else the newest leg in the effective timeline carrying one of its guids (for a Parsek-spawned vessel, the spawning leg). If that leg is covered and the live name differs, write the decision through (7.3), but only when the live name is STALE: the decision name, one of the decision's prior names, or the leg's own captured name, compared raw and then localized as a leg captures it (`Recording.ResolveLocalizedName`). Those are the cases a load or stock leaves behind (a quicksave name after a load, a stock-restored pre-dock name). Any other live name is a name nobody chose: a player rename always records a decision first through the capture Postfix (7.1) and a Parsek write is the decision itself, so it is a stock automatic rename after the leg opened (the old vessel renamed one Update after a staging split, 5.1 / 8.17). Then nothing is written; when the current leg is still open (the recorder is recording that vessel on it) it is marked uncovered and its `VesselName` takes the live name in capture form, as if it had opened after the rename; a closed leg keeps its coverage. Coverage stays decided by names, with no timing assumption. A leg CLOSE never renames a leg and never changes its coverage: the parent leg closes at a split about 0.5 s after the split (`CreateSplitBranch`), by when `ModuleDecouple.WaitAndSwitchFocus` has already renamed the old vessel (one Update after the split), so a close-time name is not the name the leg was flown under (the launch leg flown under the player's name would take "B Debris"). The same 0.5 s delay means the post-split CHILD leg is created after the automatic rename and captures it, so it is uncovered by the ordinary apply pass; the stale-name rule above covers what remains whenever a pass runs. If the leg is uncovered, the live vessel keeps its later automatic stock name (ruling 4). Log (Info): `Live write rule: stock automatic name kept on pid=... leg=... 'old' -> 'new' ...`.
4. Bump `DisplayNames.Version` once if anything changed; one summary log line with counts.

Runs: after any decision; at the ParsekScenario OnLoad tail (steps 1-2 and the proto part of 3, after hydration repair and the journal finisher); once after the post-load strip (7.6); after every commit / merge / Re-Fly union / optimizer pass; whenever a link is added; and self-heals in the per-frame hosts when `RecordingStore.StateVersion` moved or (FLIGHT, checked at 1 Hz) the live tree's recording-id set changed. O(recordings + vessels), only on a change.

As implemented (Phase 2a, `VesselNameLiveSync.cs`): `VesselNameStore.ApplyAll(reason, scope)` runs steps 1, 2, 4 and then step 3 over the scope's vessels. The three OnLoad tails (cold, plain rewind, in-session) pass `ProtosOnly` (the loaded game's `flightState.protoVessels`); every other caller (commit, optimizer, self-heal, a Parsek rename, a stock capture) passes `Auto`: in FLIGHT every `FlightGlobals.Vessels` entry once `FlightGlobals.ready` and no Re-Fly strip is pending, at the Tracking Station / Space Center the game's protos through their `Vessel` object when one exists. The current leg is the live tree's leg recording the vessel (the active recorder's leg for its vessel, else the tree's `BackgroundMap` entry), else the newest leg (start, then end, then id) carrying the vessel's launch guid, superseded legs only as a last resort. A current leg the apply pass has not classified is left alone. Log: `Live write pass (reason) scope=...: scanned= written= alreadyNamed= uncovered= unclassified= noCurrentLeg= noIdentity= noDecision= skippedGhost= failed=`.

Captured names are refreshed at the leg's close ONLY from the recorded vessel itself, resolved by pid + guid; when that vessel is gone or not positively matched (a dock-merge stop, where the absorbed vessel no longer exists; a split stop, where the active vessel may already be the new pod) the leg keeps its existing name. The stop-time `CaptureAtStop.VesselName` is NOT copied (it is the active vessel's name, `FlightRecorder.cs:8620`). The background parent continuation reads its live parent vessel's name instead of the parent recording's copy (`BackgroundRecorder.cs:1747`). As implemented in Phase 0: a tree leg is never renamed at its close (`ParsekFlight.AppendCapturedDataToRecording` does not copy the capture's name, pinned by a test), so it keeps the name captured at its start; the one close that assigns a name, the standalone split fallback (`FallbackCommitSplitRecorder`), takes it from `ParsekFlight.ResolveRecordedVesselNameAtClose` (live recorded vessel on a positive pid + guid match, else the leg's start-of-leg snapshot name); the continuation reads the live name through `BackgroundRecorder.ResolveParentContinuationName`.

### 7.5 Spawns

- The spawn node is stamped only when a decision covers the SPAWNING leg: `VESSEL.name` = that recording's post-apply `VesselName`, and the priority PART's `VESSELNAMING` `name` in the node is set to the same value with stock's tie rule (7.3 step 3), so the spawned vessel does not revert to an old part naming on its first `onVesselWasModified`. Both the regenerate and the `preserveIdentity` path. Otherwise the node is left exactly as today (raw snapshot name, which may be a `#autoLOC` tag). Snapshot sidecars are NOT rewritten (a name-only rewrite would need `MarkFilesDirty`, whose `.prec` epoch advance is the F9 stale-sidecar trap).
- `RegenerateVesselIdentity` records a Spawn link (new guid -> source recording's guid). This replaces relying on `SpawnedVesselPersistentId`, which about ten sites reset to 0.
- `RegenerateVesselIdentity` returns the old -> new part uid map from `VesselSnapshotOps.RegeneratePartIdentities` (`VesselSnapshotOps.cs:672-673` gives every PART a new `uid`). The spawn node's `DOCKEDVESSEL` / `DOCKEDVESSEL_Other` `rootUId` values are remapped with it (today they are not, so an undock of a Parsek-spawned docked stack misses stock's pre-dock name restore, `Part.cs:7258`), and `dockAbsorbs` rows keyed by an old uid are re-keyed.
- The "Vessel 'X' has appeared!" message reads the same name the spawned vessel carries.

As implemented (Phase 2b, `VesselNameSpawn.cs`, `VesselSpawner.cs`): `RespawnVessel` and `SpawnAtPosition` take the spawning recording as `nameSource` (passed by `RespawnValidatedRecording`, the `SpawnOrRecoverIfTooClose` position path, the KSC spawn and the chain-tip spawn; a restore of a vessel's own snapshot after a failed despawn passes none and is left as it was). After the regenerate or preserve branch, `ApplySpawnStamp` resolves over the live recordings and the links, runs the apply pass (so a spawning leg the pass has not seen is classified first), and stamps only a COVERED leg: `VESSEL.name` and the priority PART's `VESSELNAMING` `name`, the tie rule being `VesselRenameWriter.FindPriorityNamingIndex` over the PART nodes. `RegenerateVesselIdentity(spawnNode, source)` reads the snapshot's old guid, links the fresh guid to the source recording's guid (the snapshot's own guid when no recording is known), and returns the uid map that `RegeneratePartIdentities` now fills. Code-truth corrections: (1) the map is also applied to the docking port's `dockUId` (`ModuleDockingNode.dockedPartUId`, which `FindOtherNode` looks up with `FlightGlobals.FindPartByID`): left unmapped it is guaranteed to dangle, or to name a part of the ORIGINAL vessel when that one still exists; (2) `ModuleDockingNode` saves only `DOCKEDVESSEL` (`vesselName`, `vesselType`, `rootUId`); `DOCKEDVESSEL_Other` is the claw's (`ModuleGrappleNode`); both are remapped; (3) dock-absorb rows under an old uid are COPIED to the new uid, not moved: the recording's snapshot keeps the old uids, so the next respawn of the same snapshot (after a rewind) maps them again and must still find the row. A spawn that fails after the regenerate leaves a link from a guid that never exists, which nothing reads. Logs: `Spawn node stamped: rec= 'old' -> 'new' namingPart=part[i]|none context=`, `Spawn node left unstamped: rec= reason=uncovered|unclassified|no-decision|no-identity`, `Dock absorb rows re-keyed for regenerated part uids: copied=`.

### 7.6 Loads, rewinds, quickloads, reverts

The store is static memory like `RecordingStore`: cold load reads the save; every in-session load (plain rewind, Re-Fly start, Discard Re-fly, stock revert, F9, scene change) keeps memory. New `LoadReconcilePolicy` category `VesselNames`, NOT consumed early (`DecideEarly` throws when the three refined in-session kinds disagree, `LoadReconcilePolicy.cs:371-391`): Cold = Save; every in-session kind = Memory. Phase 2a flips StockRevert to a new action `MemoryUndoRevertedFlight`, applied after `RevertDetector.Consume`, and DiscardReFly to the same action (below); Phase 1 ships them as Memory.

Revert (ruling 5). A flight epoch is the span the stock Revert button would cancel:
- Stock flight: at `onFlightReady`, bump `FlightEpoch` when `FlightDriver.PostInitState` is a different object than at the previous `onFlightReady` (stock assigns it just before `onFlightReady`, `FlightDriver.cs:892-905`; Parsek reads it today only at revert time, `RevertDetector.cs:42`, `:50`). Code truth (IL of `FlightDriver.PostInit`, 2026-10-11): the assignment is unconditional, so it runs on EVERY flight start (a launch, a revert, F9 in flight, a scene change into flight), not only on the new-launch path. The epoch therefore moves on every flight entry, and the cancelled span is "since the last flight entry", which is exactly the state both revert buttons go back to.
- Re-Fly: the stock Revert button is intercepted (`RevertInterceptor` routes it to `DiscardReFlyHandler` or `RetryHandler`), so a Re-Fly attempt start also bumps `FlightEpoch`. Discard Re-fly and Retry cancel the attempt the way revert cancels a flight, so both undo the attempt's epoch (ruling 8). The attempt's epoch spans every flight entry inside the attempt: stock rebuilds `PostInitState` on each one (an F9, a Tracking Station round trip), so while the Re-Fly marker of the session that started the epoch is live, `onFlightReady` neither bumps the epoch nor drops its entries; the epoch ends with the attempt (merge or marker cleared: the next flight entry bumps; Discard Re-fly and Retry: the undo ends it). A stock flight keeps "since the last flight entry", which is right because stock allows revert after a resume only from PRELAUNCH (`FlightDriver.cs:387`).
Every decision recorded while an epoch is current pushes an undo entry (full copies of the decision it replaced, possibly none, and of the one it added) onto that epoch's list. On a cancelling load, walk the cancelled epoch's list newest first: for a StockDialog / StockPartNaming entry, restore the replaced decision (or remove the added one) unless a later ParsekRename on the same identity is current, which stays. Example: TS rename "Relay-2", then a stock rename "Relay-X" in a flight that is reverted: the decision returns to "Relay-2" with its coverage sets, which is also what the reverted live vessel carries.

As implemented (Phase 2a, `VesselNameStore.RevertUndo.cs`): an entry holds the added decision's seq and a full copy of the decision it replaced; a bump drops older epochs' entries (only the current epoch can be cancelled). The undo restores the replaced copy under every guid no other current decision holds, gives the legs the removed decision had renamed their own captured name back (`CapturedNames`), then runs the apply pass. "A later decision replaced the stock one" is detected as "its seq is no longer in the store", which covers a later ParsekRename, a later stock rename that was itself undone first (it restores this one), and a fold. Wiring: `ParsekScenario.OnLoad` calls `VesselNameStore.UndoCurrentFlightEpoch` right after `ClassifyRefined` when the policy cell is `MemoryUndoRevertedFlight` (StockRevert, DiscardReFly), before the tail re-apply. A Re-Fly Retry's load classifies `ReFlyStart`, which a fresh Re-Fly invoke shares (and that cancels nothing), so the Retry undo runs in `RevertInterceptor.RetryHandler` after its last refusal and before `RewindInvoker.StartInvoke`, and the ReFlyStart cell stays Memory. The Re-Fly attempt's epoch starts at the end of `RewindInvoker.RunStripActivateMarker` (`VesselNameLiveSync.OnReFlyStripComplete`). Log: `Flight epoch -> N reason=...`, `Revert undo: dropped ... restored ...`, `Revert undo (reason): epoch= entries= undone= keptParsekRename= keptReplacedLater= legsRestored=`.

After a load the live stock vessels carry the quicksave's names. Re-apply:
1. OnLoad tail: the apply pass over recordings and over `HighLogic.CurrentGame.flightState.protoVessels` (scenario OnLoad runs before `flightState.Load()`, so TS, KSC and unloaded vessels are covered).
2. Once after the post-load strip has finished: the live write rule over ALL of `FlightGlobals.Vessels` (loaded or not), from the end of `RewindInvoker.RunStripActivateMarker` when a rewind or Re-Fly is consuming the load (its strip is deferred to `onFlightReady`, `RewindInvoker.cs:1179-1200`), else from the first host tick after `OnFlightReady`. A FLIGHT->FLIGHT revert restores vessels OnLoad's proto pass does not see (`ParsekScenario.cs:4110-4113`), so this pass is not optional.
As implemented (Phase 2a): the plain-rewind branch of OnLoad (which returns early) runs the same tail. The post-strip pass is `VesselNameLiveSync.OnReFlyStripComplete` at the end of `RunStripActivateMarker`'s success path; otherwise `ParsekFlight.OnFlightReady` arms `VesselNameLiveSync` (first, before any early return) and `ParsekFlight.Update` runs the pass on the first tick where `RewindInvokeContext.Pending` is false (a strip that fails clears the flag, so the pass still runs). A strip pass in the last 5 s keeps `onFlightReady` from arming a second one; a second pass would be idempotent anyway.
3. The OnLoad-tail proto pass may run before a strip: safe, because after Phase 0 the strip / kill paths match by identity first. Their remaining name rules are the guid-less legacy fallbacks (never a decision, 6.1), the #587 debris-only prior-career rule (another launch's debris, never a decision's identity) and the `RewindOwnerStrip` owner-name rule (7.9; since Phase 2b a re-keyed owner copy is matched by its link guid, or, unanchored and unlinked, by the Phase 1 alias pair).

### 7.7 Splits, docks, undocks

- Split child: a new identity named by stock (AutoRename or its naming part). No decision is inherited: P2 does not take the parent's decision.
- Continuing vessel keeps its guid and identity; if it has a decision, the new leg captures the live name, which already equals the decision.
- Old vessel renamed one Update after a staging split (5.1): an automatic name; its next leg captures it and is uncovered (6.1), never a decision.
- Undock mechanics (decompiled `ModuleDockingNode.Undock` / `Part.Undock`): stock always undocks the CHILD side of the coupling joint (`if (parent != otherNode.part) otherNode.Undock()`), and the absorbed vessel was attached as the child at `Part.Couple` (`ModuleDockingNode.cs:4441`). `Part.Undock` builds the leaving vessel with `vessel.id = Guid.NewGuid()`, re-roots it on the pre-dock root part when `DockedVesselInfo.rootPartUId` is in its subtree, and names it from `DockedVesselInfo.name` (the name at dock time). The half left behind keeps the merged vessel's guid, which is the dominant's. A ship built docked in the editor (`undockPreAttached`) leaves through `Decouple`, a plain split. So the ordinary dock / undock round trip ALWAYS re-keys the returning ship: without the UndockReturn link its post-undock legs are a different identity from its pre-dock legs, and a rename after any undock would leave every pre-dock leg on the old name (ruling 1). The link is required, not an edge case.
- Dock merge: the merged leg belongs to the dominant identity (stock keeps its name). The absorbed identity's legs keep their names; its decision (if any) stays dormant. A rename while docked is a decision for the dominant identity only (same as stock). At the dock event Parsek records a DockAbsorbRow (absorbed vessel's root part flightID -> its guid). A naming part taking over the merged vessel's name is automatic: the merged leg captures that name and is uncovered (6.1).
- As implemented (Phase 2b): the row is recorded in `ParsekFlight.OnPartCouple` before any early return (`VesselNameLiveSync.OnDockCouple`), from `data.from.vessel`: `ModuleDockingNode.DockToVessel` calls `base.part.Couple(node.part)` and `Part.Couple` fires `onPartCouple(this, tgtPart)` FIRST, before it reparents and destroys `this.vessel`, so `data.from.vessel` is the absorbed vessel still whole, and its `rootPart.flightID` is exactly the `rootPartUId` stock stores in `DockedVesselInfo` a few lines earlier. EVA kerbals and ghost map vessels are skipped. Part flightIDs survive a dock (`Part.Couple` changes no `flightID`) and a save / load (`ProtoPartSnapshot` saves `uid = flightID` and `ConfigurePart` assigns it back).
- Undock: stock restores the departing half's pre-dock name. At `onVesselsUndocking` Parsek looks the departing vessel's root part flightID up in `dockAbsorbs`; on a hit it adds an UndockReturn link (departing guid -> absorbed guid). The leaving ship's first leg captures the stock-restored pre-dock name, which is the decision or a prior name, so it is covered, and the live write rule (7.4) writes the decision through when it differs (a Parsek rename made while it was docked). Docks recorded before the upgrade have no row: fall back to the in-tree re-pidded-continuation rule `MissionVesselNaming.MergeRepiddedContinuations` already applies (a leg that starts after one of the tree's own vessels ended and shares part pids with it, best match per hop), moved into `VesselIdentity` as an in-tree-only edge; otherwise the departing half is a new identity (for example a ship built docked in the editor). Chained docks work unchanged: a tanker docked to the ferry stays inside the leaving vessel and keeps its own row for its own later undock. Parsek does not read `DockedVesselInfo` (both nodes carry one, `ModuleDockingNode.cs:4424-4430`).
  As implemented (Phase 2b): `ParsekFlight.OnVesselsUndocking` calls `VesselNameLiveSync.OnUndockSplit(newVessel)` before any early return, so a link is added whether or not Parsek is recording. Verified in the decompile (`Part.Undock`, KSP 1.12.5): `obj = this.vessel[newVesselInfo.rootPartUId]`; when that part is an indirect child of the undocking part, `part.SetHierarchyRoot(part)` makes it the new vessel's root, else the undocking part is; the new `Vessel` gets `id = Guid.NewGuid()`; `onVesselsUndocking(oldVessel, vessel)` fires last. So the leaving vessel's root is the pre-dock root in the ordinary case, and a miss (the pre-dock root not in the leaving subtree) falls back to a new identity. A row hit is consumed (removed); a later dock records a new one. Logs: `Dock absorb row recorded: rootUid= guid=`, `Undock: dock absorb row hit rootUid= absorbedGuid= leavingGuid= linkAdded=`, `Undock: no dock absorb row for rootUid=`.

### 7.8 Ghosts

- Map / TS ghost protos: in the per-tick host (`UpdateFlightMapGhostLifecycle`, `UpdateTrackingStationGhostLifecycle`), when `DisplayNames.Version` moved, walk `vesselsByRecordingIndex`, `overlapInstanceVessels` and `vesselsByChainPid` and set `vesselName` / `protoVessel.vesselName` to `GhostVesselNamePrefix + display name` in place. No event, no rebuild (a rebuild trips line-blink / icon-jump and drops targets). Stock list and labels read the name live (5.1). Resolve the recording index by id at apply time; never cache it across frames (committed-list index contract).
- CommNet: the per-tick refresh loop (`GhostCommNetManager.cs:621-640`) copies a changed name into `Node.displayName`; derive the name from `rec.VesselName` first, the snapshot second.
- Flight ghost labels, map markers and the watch label already read `rec.VesselName` per frame.

### 7.9 Name-as-identity sites converted (phase 0, before names move)

Phase 0 is implemented (branch `vessel-naming-sync`); the Change column states what shipped. Pinned by `VesselNameIdentityPhase0Tests` (a renamed vessel still matched by identity, a same-named different launch not matched, guid-less legacy still matched by name).

| Site | Today | Change | Risk if left |
|---|---|---|---|
| `RewindInvoker.ResolveInPlaceContinuationDebrisToKill` | kills left-alone quicksave vessels whose name matches a recording | identity first: a candidate that is the same launch as a Destroyed-terminal or suppressed-subtree recording (`VesselLaunchIdentity.CandidateMatchesRecording`, source + spawn, as the rewind strip) is killed whatever either is named, and a candidate of the active Re-Fly recording's own launch (source or spawn) never is. The identity arm is NARROWED (owner call, review 2026-10-11): parent-chain recordings keep exactly their previous name-only matching, because the chain's earliest leg shares its pid + guid with debris that split off it under another name (root R0 and its continuation D), which an identity match there would `Die()` where the name rule never did. The name rule is KEPT as a debris-only prior-career relic rule: the survey admits `VesselType.Debris` only (`IsDebrisKillSurveyCandidate`), so a same-named ship can no longer be `Die()`d, and the rule is #587's deliberate clearing of an EARLIER launch's same-named debris (no identity reaches it), pinned by the in-game cell `NameCollidingDebrisIsStillRemovedByTheBug587Kill` and harness lane S4.2. Dropping it is an owner call | high: `Die()` on a same-named ship (closed by the debris-only survey), or a doubled vessel (closed by the identity match) |
| `RecordingStore.RewindOwnerStrip`, legacy `FromNames` overload | name-only fallback when unanchored (no quicksave vessel carries the owner guid) | Phase 0: unchanged. Open check SETTLED: the PID strip (`ResolveRewindStripSpawnedPids`) only sees committed `SpawnedVesselPersistentId` values, and the resume path (`TryTakeCommittedTreeForSpawnedVesselRestore`) clears it while keeping the old guid, so a re-keyed owner copy in the quicksave is stripped ONLY by the unanchored name fallback. Consequence for Phase 1: a decision that renames the owner's legs makes that fallback compare the new name against the quicksave's old one, so Phase 1 must keep this path matching the owner's prior names (or not rename such owners) until Phase 2b. Phase 1 (done): the unanchored fallback also accepts `OwnerAliasNames`, exactly the owner recording's own pre-decision captured name (the decision's `CapturedNames` entry, else its current name) plus the decision name, never the wider `priorNames`. Phase 2b (done): `RewindOwnerStrip.OwnerIdentityGuids` is the identity's full guid set (`VesselNameStore.IdentityGuidsFor`: recording guids plus link guids), and a quicksave vessel carrying any of those guids is stripped by guid under any name; anchoring (`RewindOwnerStripIsAnchored`) and the different-launch keep read the same set. The UNANCHORED fallback is the union of that guid set and the Phase 1 alias pair, kept for a copy resumed BEFORE the upgrade, which has no Resume link (the resume cleared the spawn pid the backfill reads) and still carries the old name. The owner's current name keeps its pre-identity rule (stripped unless anchored and its guid is known and outside the set); a name stands alone only for a guid-less owner | medium: strips another launch renamed to the owner's name |
| `VesselSpawner.ShouldRecoverBlockerVessel` | name equality before the spawn-pid check | done: `VesselLaunchIdentity.LiveVesselIsRecordedSpawn` with the blocker's live guid; no name (an adoption-stamped relaunch of the same craft is never recovered) | medium: #112 recovery silently skipped, walkback instead |
| `RecordingStore.SupersedeTerminalSpawn` UT-contiguity repair branch | same-name + UT contiguity fallback | done (`IsSameVesselForContinuationRepair`): two known launch guids decide alone; the name only when a guid is unknown. A spawned continuation's re-keyed guid is linked only by Phase 2b's Spawn link, so for guid-bearing data this already-polluted-shape repair waits for 2b (the commit-time spawned-pid branch is unchanged) | medium: missed supersede, double spawn |
| `ParsekFlight.RestoreActiveTreeFromPending` quickload restore (active and EVA-parent walk) | name fallback after pid | done: pid (guid-gated) only; the name only for a recording with neither a pid nor a guid (`QuickloadResumeMatchGuard.MayMatchByNameFallback`) | low-medium |
| `ParsekFlight.RecoverTimelineSpawnedVessel` | selects the recordings to recover by leaf name (the vessel gate already used `LiveVesselIsRecordedSpawn`) | done: recordings selected by `VesselLaunchIdentity.RecordingsShareVesselIdentity` (`IsTimelineSpawnOfLeafVessel`); name only for guid-less legacy | medium |
| `GhostPlaybackEngine.LogUnitTransitionIfChanged` watch handoff | prefers members with the watched name | done: `GhostPlaybackLogic.IsSameWatchedVessel` over the new `IPlaybackTrajectory.RecordedVesselGuid` (chain members share the guid); name only when a guid is unknown | low (camera) |
| `PostLoadStripper.FindTreeNameCollisions` | name collision warning | kept as a diagnostic | none (log) |
| `ParsekScenario.MatchesVessel`: `UpdateRecordingsForTerminalEvent`, `HasPendingLedgerRecordingForVessel`, `RecoveryDispatchScope.ResolveOwner` | name REQUIRED, then pid / guid | done (`ClassifyVesselMatch`): a known pid that differs never matches; two known guids decide alone (equal = match under any name); the name only when a guid is unknown | high: a renamed vessel's recovery misses the Recovered stamp and its recovery-cost actions |
| `LedgerOrchestrator.PickRecoveryRecordingId` | name fallback after guid | no change needed: already identity first (launch guid, then genuine spawn pid); the name walk runs only when neither reaches a recording and is guid-filtered, so with a known live guid it admits only guid-less recordings | medium |
| `LedgerRolloutAdoption.cs:229`, `:688` | name + site when pid is 0 | keep (history, legacy only) | low |

### 7.10 Mission names and TreeName

Mission names stay Parsek-owned (`MissionGroupLink`). `TreeName` stays the raw launch name for its non-display roles (root group generation, default mission seed, seam lookup, persistence, about 60 log sites and 21 harness logContract regexes that pin `tree='...'`). Display readers switch to the mission display name (`MissionStore.FindOriginalMission(treeId)?.Name ?? TreeName`): `MergeDialog.cs:209-210`, `:387-388`, `:672`, `MergeDialog.DialogText.cs:171`, `LogisticsWindowUI.cs:1319`, `:2505`, `Logistics/RouteRunPrompt.cs:177`, `RecordingStore.cs:5802`, `MissionVesselNaming.cs:283`. Fix the two false comments (`RecordingStore.cs:5772-5774`, `RecordingsTableUI.cs:4911-4912`).

### 7.11 Display refresh and numbering

Every cache that bakes a vessel or mission name keys on `DisplayNames.Version` or subscribes to `DisplayNamesChanged`: Missions naming cache, Mission Log `ComputeChangeSignature`, Timeline `InvalidateCache`, Kerbals `cachedVM`, `CommittedFutureIndexCache`, Recordings name sort, `DockEventGraphCache` (and the Missions digest / chapter caches and dock-partner tooltip memo keyed on it), TS atmospheric focus target.

Numbering: `MissionVesselNaming` groups legs by vessel first and then numbers per name (it used to bucket by name first, which split one vessel with two names). As implemented (Phase 1): each leg keeps its own display name (section 4: a leg never learns its name from another leg), and a vessel's ordinal among the vessels carrying a name is its FIRST appearance overall, not the first leg carrying that name, so one vessel with two captured names is one vessel in both buckets. Logistics "Flights used" and near-miss labels use `MissionVesselNaming` numbering instead of `NumberRepeatedNames`. Map, TS, watch, screen messages, Kerbals and Timeline show raw display names. A `[n]` suffix is never written to stock.

---

## 8. Edge Cases

1. **Rename to a name another vessel already has.** Allowed (stock allows it); identity keeps them apart; Missions numbering disambiguates within a mission.
2. **Localization tag names.** Captured leg names are stored already localized (`Recording.ResolveLocalizedName` at capture, `ParsekFlight.cs:13495`, `:5988`); decision names are literal as typed; coverage compares these two forms, which are the same form a live vessel name has once captured. Snapshot / spawn-node names stay raw (`#autoLOC` tags) unless a decision covers the spawning leg (7.5).
3. **`from == to` events, type-only changes, asteroid / comet / deployed-science pings.** Ignored (Parsek has no `onVesselRename` handler; 7.1 reacts only to the two accept methods).
4. **Rename of a vessel Parsek never recorded.** No identity, nothing stored. If it is recorded later, its legs capture the live name.
5. **Rename during a Re-Fly session.** The decision lives in the store; the union swap at commit restores older `VesselName`s and the apply pass re-applies the decision.
6. **Rename then F9 to an earlier quicksave.** Decision kept (ruling 1); live vessels renamed again by 7.6. Staging after the F9 with the control part leaving: the old vessel's next leg captures stock's AutoRename name, which is neither the decision nor a prior name, so it is uncovered and keeps it (ruling 4), whatever the UT.
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
- Coverage (ruling 4): decision then the control part leaves at staging -> the old vessel's next leg (AutoRename name) is uncovered, earlier legs keep the decision, the live write rule leaves that vessel alone; the same after F9 to before the decision's UT; a naming-part takeover at a dock leaves the merged leg uncovered; a leg captured with a prior name (stale, before write-through) is covered and its live vessel is written; a new decision covers every existing leg again.
- Accept ordering: stock fires `onVesselRename` inside the accept body; the Prefix / Postfix pair records exactly one decision and leaves the replaced decision's coverage untouched.
- Revert (ruling 5): TS rename "Relay-2", in-flight stock rename "Relay-X", revert -> "Relay-2" with its coverage; a ParsekRename made in the reverted flight survives; Discard Re-fly and Retry undo the attempt's stock decisions; `FlightEpoch` bumps only when `PostInitState` changes; the category is not consumed early and the Phase 1 cells are Memory.
- Conflict: two components joined after a rewind keep the higher `seq`, not the later UT.
- Spawn: a part-named vessel renamed then respawned keeps the decision after `onVesselWasModified` (naming part stamped); a spawn of an uncovered leg keeps its own name; a docked stack spawned by Parsek undocks with stock's pre-dock name and the UndockReturn link (remapped `rootUId`).
- TS rename of an unloaded vessel updates its proto naming part (tie rule: first part with the strictly highest priority).
- Blocker recovery: an adoption-stamped relaunch of the same craft under another name is never recovered.
- Recovery after a rename: a vessel renamed after its legs were captured still gets Recovered and its recovery-cost actions (each `MatchesVessel` caller).
- Key stability: a decision survives adding a leg with a new guid, an F9 trim of the founding leg, and a new link joining two components (highest-seq decision wins, Warn logged).
- Pre-upgrade spawn: a vessel spawned before the feature resolves through the backfilled Spawn link; an adoption-stamp vessel does not; a resumed spawned vessel resolves through the Resume link and its post-resume legs join the identity.
- Capture refresh: a split stop and a dock-merge stop keep the leg's name when the recorded vessel is not the active one / is gone.
- Log assertions for the 14 events.

### 15.2 In-game tests (new category `VesselNames`, FLIGHT)
- Stock-equivalent rename of the active recorded vessel: every leg, ghost proto name, CommNet name updated.
- Write-through: Parsek rename renames the live vessel, fires `onVesselRename` once, and is not re-captured.
- Naming part updated so an `onVesselWasModified` does not revert the name (dialog rename right after a load; TS rename then fly then a part change).
- Parsek rename while docked, then undock: the leaving ship ends with the decision name.

### 15.3 Harness lanes (fly only on request)
GUI-25 commits a recording rename on `interbody-route-recorded`: from Phase 1 the rename relabels every leg of that vessel, and from Phase 2a it also renames a live vessel there; re-read its later captures in both PRs.

The seam verb (2026-10-11, branch `vessel-names-links`) is `RenameVessel name=<name> [pid=<pid>]` (the active vessel when `pid=` is absent; FLIGHT, single-phase, default budget): it invokes the private `Vessel.onVesselRenameAccept(string, VesselType)` by reflection with the vessel's current type, so stock renames the vessel and Parsek's capture pair (7.1) runs as for a player. OK payload `pid= old= new= type= active= changed= legs= decision= decisionName=`; `old` is the live stock name before the accept, so a rename doubles as a read of the name a load or a write-through left on the vessel. Contract: `TestCommandRenameVessel`, `design-autotest-command-seam.md` (`#### RenameVessel`). saveParse gained a `recordingNames` census (committed legs per `vesselName`) beside `vesselNames` (live vessels per name). Three lanes, each a reading run with saveParse report-only (flights 2026-10-11: VN-2 result `2026-10-11_1653` and VN-3 result `2026-10-11_1733` PASS attempt 1, live-proven; VN-1 red twice on spec defects only, a recordings floor and an F9 too close to its F5 to classify as a quickload, both fixed, not re-flown); VN-1's `RunTests` pins `total=` and sits in `INTERIM_PIN_IDS` until a green flight:
- `VN-1-stock-rename-staging-f9` (GS-1 recorded from the pad): the `VesselNames` in-game category on the recorded pad vessel; a stock rename on the pad; staging, where stock names the booster from the player's name and that automatic name stays on its legs (ruling 4); a second stock rename after landing renaming the pod's identity (GS-1's pod keeps one recording across the split, so ruling 7 across a separate launch recording is proven by unit tests only); an in-flight F9 to a quicksave from before it, after which a post-load pass writes the name back (7.6); a Parsek recording rename after an in-flight commit written through to the live pod (7.2, 7.3).
- `VN-2-refly-rename-discard` (RF-1's flight): a Parsek rename made before a re-fly is written onto the vessel the rewind point's quicksave reloads (7.6 step 2); inside the attempt a stock rename of the re-flown vessel and a Parsek rename of its sibling; Discard Re-Fly undoes the attempt's stock rename and keeps the Parsek one (rulings 5 and 8).
- `VN-3-spawn-rename-rewind` (Real Spawn Control on a committed landed probe): a Parsek rename made before the spawn stamps the spawn node and the Spawn link joins the fresh guid (7.5); a stock rename of the spawned vessel and a Parsek write-through to it; a Rewind-to-Launch strips it and the replay respawns it with a second Spawn link, stamped with its last name (8.12, the respawn branch). The kept branch is not reachable on this host: the preset recording (trimmed on load to EndUT 82.16) starts after the save UT, so the playhead sweep latches it into replay scope and the rewind always reads it as replaying.

Not driven: a plain stock Revert to Launch outside a Re-Fly (load kind StockRevert): no seam verb reaches `FlightDriver.RevertToLaunch` outside a live Re-Fly session, and the only driver is the isolated in-game `RevertFlow` cell; the Discard Re-fly load in VN-2 takes the same `MemoryUndoRevertedFlight` action. Also not driven: the post-staging race where the control part leaves on the new vessel (8.17; the GS-1 craft keeps its command pod), the kept branch of 8.12 (a rewind that keeps a spawned vessel as committed history and writes its last name onto the reloaded vessel; it needs a spawning recording that starts before the save UT), a dock / undock round trip, a resumed spawned vessel and the backfill over a pre-upgrade save.

---

## 16. Implementation Phasing

| Phase | Content | Player-visible |
|---|---|---|
| 0 | Convert name-as-identity sites (7.9), the recovery / terminal cluster first; fix the false comments; capture refresh from the recorded vessel only (7.4 last paragraph). IMPLEMENTED (branch `vessel-naming-sync`; 7.9 records what shipped per site) | no (safety) |
| 1 | `VesselIdentity` shared resolver (and `MissionVesselNaming` on it), `VesselNameStore` (decisions, seq, coverage, codec), policy cell (all in-session kinds Memory), apply pass steps 1-2 and 4, `DisplayNames.Version` + cache plumbing (7.11), ghost / CommNet in-place rename (7.8), Parsek row rename as identity-wide decision with the EVA and legacy refusals. IMPLEMENTED (branch `vessel-names-store`, on top of phase 0; status note below) | yes: rename is consistent inside Parsek |
| 2a | Harmony capture of stock renames (7.1), write-through helper with naming parts (7.3), live write rule (7.4 step 3) and post-load re-apply (7.6), revert epochs + undo + the policy flip (ruling 5; ships with the re-apply, or a reverted stock rename is written back). IMPLEMENTED (branch `vessel-names-stock`, on top of phase 1; status note below) | yes: stock is the authority |
| 2b | Spawn stamp + Spawn link + uid remap + Resume link + spawn backfill (6.2, 7.5), DockAbsorbRows + UndockReturn links (7.7, required: the ordinary dock / undock round trip re-keys the ship), `RewindOwnerStrip` on the full guid set. IMPLEMENTED (branch `vessel-names-links`, on top of phase 2a; status note below) | yes: spawned, resumed and undocked vessels keep their names |
| 3 | TreeName display readers (7.10), numbering unification | yes |

Phase 1 renames every leg of an identity without renaming the live vessel; it is safe only because Phase 0 already removed every name-required match (the recovery cluster in particular).

**Phase 1 status (2026-10-11, branch `vessel-names-store`).** Implemented: `VesselIdentity` (edges 1, 2, 3, 5, 6; edge 4 is a `guidLinks` seam on `Resolve` with no producer), `MissionVesselNaming` on it with vessel-first numbering, `VesselNameStore` (decisions, seq, guid index, coverage, `VESSEL_NAMES` codec with decisions + `nextSeq`, loaded on the cold path just before the committed trees so a tree-load fault cannot drop it), the `VesselNames` policy category (Cold = Save, every in-session kind Memory), the apply pass steps 1, 2 and 4 (run after a decision, at both OnLoad tails, after `CommitTree` and the optimizer pass, and as a self-heal in the FLIGHT / KSC / TS `Update` on a `StateVersion` move or, in FLIGHT at 1 Hz, a live-tree leg-set change), `DisplayNames.Version` + `DisplayNamesChanged` and every 7.11 cache, the in-place ghost proto / CommNet / TS atmospheric focus target renames, and the Parsek row rename as an identity-wide decision (refused, logged, for blank / unchanged-row / EVA / guid-less). Deviations are recorded inline in 6.1 (conflict fold, carried prior names, replace keeps foreign guids), 6.2 (edges 5 and 6) and 7.11 (numbering); 7.9 records the `RewindOwnerStrip` alias names. Phase 1 is merged on top of Phase 0 (`vessel-naming-sync`). Between 2a and 2b, a rename reaches live vessels that carry a recorded launch guid but not yet spawned, resumed or undocked ships, which keep today's behavior. Each phase lands as its own PR with its CHANGELOG / todo updates; GC-7 closes with phase 2b.

**Phase 2a status (2026-10-11, branch `vessel-names-stock`).** Implemented: the two Harmony capture pairs (7.1), `VesselRenameWriter` (7.3), the live write rule in every apply pass and the Parsek rename writing through (7.2, 7.4 step 3, `VesselNameLiveSync`), the OnLoad-tail proto pass on all three tails and the post-load pass after the Re-Fly strip or on the first flight host tick (7.6 steps 1-3), flight epochs and the revert undo with the policy action `MemoryUndoRevertedFlight` on StockRevert and DiscardReFly (Retry undoes in `RetryHandler`; its ReFlyStart cell stays Memory). Code-truth corrections are inline in 5.1 (part naming on the part already naming the vessel), 7.1, 7.3, 7.4 and 7.6 (`PostInitState` is rebuilt on every flight start). Tests: `VesselNamesStockTests` (unit) and the in-game category `VesselNames` (three FLIGHT cells, never flown). The staging race (the old vessel's post-staging automatic rename landing after its new leg captured the decision name) is closed by the stale-name condition of 7.4 step 3: the unchosen live name is never written over, the post-split child leg is created after the automatic rename and captures it (uncovered), and a leg that still opened under the decision name is uncovered by the live pass while open. Leg closes never rename a leg (7.4 step 3: the parent closes after the automatic rename). A Parsek rename adds the identity's live vessel names to its prior names (7.2), and a Re-Fly attempt's epoch survives F9 and scene round trips inside the attempt (7.6). Not live-proven: the capture on a real dialog accept, the naming-part re-derive after a load, the post-load pass after a revert and after a Re-Fly strip, and the staging race itself on a real decoupler.

---

**Phase 2b status (2026-10-11, branch `vessel-names-links`).** Implemented: link rows and dock-absorb rows in the store with the decisions' lifecycle and the `LINK` / `DOCK_ABSORB` codec (`VesselNameStore.Links.cs`), identity edge 4 over link components with link guids in the identity's guid set (6.2 note), the spawn stamp, the Spawn link and the uid remap (7.5 note, `VesselNameSpawn.cs`), the Resume link before the spawn pid is cleared, the OnLoad-tail spawn-link backfill, the dock-absorb row at `onPartCouple` and the UndockReturn link at `onVesselsUndocking` (7.7 notes), and `RewindOwnerStrip` on the identity's full guid set (7.9). GC-7 stays open until the live proof below is done. Deviations, each recorded inline: an added link schedules the apply pass for the next host tick rather than running it inside the spawn or the stock event; `dockUId` is remapped with the dock records; re-keyed dock-absorb rows are copied, not moved; a dock-absorb row is consumed by its undock. Tests: `VesselNamesLinksTests`, plus the updated `RewindOwnerStripTests` and `VesselNameIdentityPhase0Tests`. Not live-proven: a real Parsek spawn stamped and linked (naming part surviving `onVesselWasModified`), a spawned docked stack undocking with the remapped `rootUId` / `dockUId`, a real dock / undock round trip writing the decision through, the Resume link on a real resume, the backfill over a real pre-upgrade save, and a rewind stripping a renamed owner's re-keyed copy.

## 17. Open Questions

None open. The three questions of the 2026-10-10 draft (automatic names, revert, EVA rows) are rulings 4-6 (section 1.1).

---

## 18. Code Layout

| New / changed | Role |
|---|---|
| `Source/Parsek/VesselIdentity.cs` (new, pure) | union-find resolver, live-vessel lookup |
| `Source/Parsek/VesselNameStore.cs` (new) | decisions, links, codec, apply pass, version |
| `Source/Parsek/VesselRenameWriter.cs` (new) | stock-equivalent write-through |
| `Source/Parsek/VesselNameLiveSync.cs` (new, Phase 2a) | live write rule, stock capture core, post-load pass |
| `Source/Parsek/VesselNameStore.RevertUndo.cs` (new, Phase 2a) | flight epochs, undo entries, the revert undo |
| `Source/Parsek/VesselNameStore.Links.cs` (new, Phase 2b) | link rows, dock-absorb rows, their codec, the spawn-link backfill, the identity guid set |
| `Source/Parsek/VesselNameSpawn.cs` (new, Phase 2b) | spawn node stamp, dock-record uid remap |
| `Source/Parsek/Patches/VesselRenameCapturePatch.cs` (new) | Prefix / Postfix pairs on the two accept methods |
| `MissionVesselNaming.cs` | rename becomes a decision; vessel-first grouping |
| `ParsekScenario.cs` | store load / save, OnLoad tail re-apply |
| `ParsekFlight.cs` | OnFlightReady re-apply, capture fixes |
| `VesselSpawner.cs` | spawn name stamp, Spawn link, uid map |
| `GhostMapPresence.cs`, `GhostCommNetManager.cs` | in-place ghost renames |
| `LoadReconcilePolicy.cs` | `VesselNames` category |
| caches listed in 7.11 | version keys |
| `scripts/ers-els-audit-allowlist.txt` | `[ERS-exempt]` entries for `VesselIdentity.cs` and `VesselNameStore.cs` |

---

## References / Related Docs

- Research reports (2026-10-10 session): stock decompile of KSP 1.12.5 naming, Parsek identity model, load lifecycle, refresh plumbing.
- `docs/dev/research/origin-proof-partner-identity-memo.md` (dock dominance, undock re-key).
- `.claude/CLAUDE.md` gotcha "`persistentId` is craft-baked, NOT launch-unique".
