# Parsek Cooperative Async Multiplayer - Design Document

*Design specification for shared-folder cooperative async multiplayer: multiple players contribute missions to one shared career timeline.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies how multiple players, each in their own KSP instance, share one cooperative career: one KSC, one pooled economy, one timeline, exchanged asynchronously through a file-sharing folder (Dropbox, Google Drive, or any service of the players' choice).*

**Status:** DESIGN v5 (pre-implementation; interview complete 2026-09-01; adversarial review + code verification folded in 2026-09-01; player-perspective edge-case pass, spawn-ownership reconciliation, and implementation task breakdown added 2026-09-01; 2026-09-02: full control of foreign vessels (Fly/Switch-To/Recover as claims via a foreign-continuation route, replacing the earlier Fly block), trajectory-aware tip-advance predicate, attribution shown only with 2+ registered players; 2026-09-02 gameplay stress test (14 multi-player sessions): own continuations and recoveries of exported terminals are claims too, covering-link acceptance + fit clause (c), pending-claim masking, orphaned coupling dependents salvaged not deleted, UT-gated live blocks for foreign tech/hires, seq allocation from the folder, deterministic campaign stand-ins, rescue carve-out, route endpoint re-check, shared reservation attribution - see `docs/dev/plans/coop-async-multiplayer-tasks.md`)
**Version:** v1 (roadmap Phase 14)
**Out of scope:** competitive play and per-player economies (Phase 15, `docs/roadmap.md`), real-time synchronization (permanently out per roadmap), Gloops extraction (decoupled from this feature; see section 10), contract pooling (v2; see 7.11 and section 10), excise-and-stitch conflict salvage (v1.1 upgrade; see 7.9 Case 2).
**Related docs:** `docs/parsek-architecture.md`, `docs/parsek-game-actions-and-resources-recorder-design.md`, `docs/parsek-timeline-design.md`, `docs/parsek-rewind-to-separation-design.md`, `docs/dev/design-mission-crosstree-dock.md`, `docs/dev/dock-undock-recording-structure.md`, `docs/roadmap.md` (Phase 14).

---

## 1. Introduction

Cooperative async multiplayer lets a group of players run one shared space program. All players are on the same team: they share the KSC, the career save, and the timeline. Each player's missions and flights are added to the shared timeline under their name. Players never need to be online simultaneously; the timeline converges over time as each player's contributions propagate through a shared folder. Correspondence chess, not real-time multiplayer.

The central architectural decision: the shared folder does NOT hold a mirrored copy of the save. It holds one write-only subfolder per player containing append-only contribution packets, plus founder-written checkpoints (the campaign snapshot is checkpoint 0) for bootstrapping. Each player keeps their own complete local save; convergence is achieved by importing everyone's packets and re-deriving shared state through the existing ledger recalculation engine. "One shared save" is delivered as an experience, not as a literal shared `persistent.sfs`.

This document covers:

- Campaign lifecycle: creation, join, membership, checkpoints, the shared folder contract.
- Player identity and attribution.
- The contribution packet format, export triggers, and the import pipeline.
- Foreign recordings as full read-only timeline citizens, and the ownership fence.
- Foreign vessel spawning, cross-player interaction, the arbitration fold, and the conflict salvage ladder.
- The merged-economy contract: deterministic ordering, dedup, debt, credit resolution, baseline pinning.
- Crew pooling via the partitioned roster, including the crew claim rule.
- Retirement propagation (rewind/supersede/discard/truncation of shared data).

It deliberately does not cover: per-player economies, contract instance pooling, opponent packs, or any change to the single-player recording pipeline beyond the additive fields and stamps listed in sections 5 and 6.

### 1.1 What the player sees

| Situation | What happens |
|-----------|--------------|
| Player sets their name and shared folder in Settings | A stable player identity is created; the save is linked to the campaign |
| Founder clicks "Create campaign" | Campaign manifest + checkpoint 0 (full save snapshot) written to the shared folder |
| Friend clicks "Join campaign" | Local save cloned from the latest checkpoint, packets past it imported, player registered; the clone's stock contract queue and applicant pool are regenerated |
| Player commits a mission | Recording + ledger actions exported as a packet into their own shared subfolder |
| Player researches tech / upgrades a facility at KSC | The actions ride the next action-flush packet (no commit needed) |
| Peer's packet arrives (sync service delivers it) | On next merge (load, scene change, or background poll): peer's mission appears in the timeline, Recordings Manager, and as ghosts, attributed to them |
| Peer's station recording completes and current UT passes its end | The station spawns as a real vessel in your game; you can fly to it and dock |
| Player clicks Fly / Switch-To on a peer's spawned vessel | Works exactly like flying your own spawned vessel: you take control; the flight records as YOUR continuation of their vessel (a new local tree linked to their recording) and becomes a claim at commit; the fold decides whether it is canonical |
| Player recovers or terminates a peer's spawned vessel from the Tracking Station | Same behavior as for your own spawned vessels; a recovery additionally shares a terminal claim so the vessel's canonical story ends Recovered for everyone |
| Player's kerbal boards a peer's spawned station and takes control | Allowed: the crewed vessel continues in YOUR tree as a claim; the arbitration fold decides whether it becomes canonical |
| Player takes control of a peer's vessel, looks around, and leaves without changing anything | The no-op segment is auto-discarded (existing switch-segment classifier); no claim, nothing shared |
| Player opens Settings after a merge | Member list shows each player's name and last-known game clock ("Ana: Y1 D120"), the shared balance including any debt, and pending export/import counts |
| Solo save, or a campaign with only one registered player | No player names appear anywhere (timeline, Recordings Manager, Kerbals window); attribution renders only once two or more distinct player ids are registered in the campaign |
| Two players interacted with the same vessel before syncing | The arbitration fold picks the canonical interaction (share order); the other player is auto-resolved by the salvage ladder and notified on screen |
| Two players spent the same funds before syncing | Balance goes into debt (displays 0); a warning names the colliding spends; future earnings fill the hole |
| Two players achieve the same milestone first | Earlier UT gets the credit; the total payout never doubles |
| Two players both flew Jebediah before syncing | The earlier-shared packet claims him; the other mission gets a derived stand-in and a flagged row |
| A peer uses a mod you do not have | Their ghost renders with missing parts skipped, marked, and never spawns real; if they modify YOUR station with such a part, it becomes a held, marked ghost instead of a real vessel until you have the part |
| Player rewinds their own shared mission | Peers receive the supersede as a retirement record; dependent peer recordings are orphan-flagged, kept, and marked |

### 1.2 Worked example

Campaign "Duna Together", players Ana (`p_ana`) and Bogdan (`p_bogdan`). Ana founded the campaign from her existing career at UT 8,000,000.

```
UT 8,100,000  Ana launches "Harbor" station core, circularizes at 120 km,
              commits. Terminal Orbiting. Packet ana/000014 exported:
              tree + recordings + ledger rows (launch cost, science,
              milestone "First Station") + game-state event bundle.
UT (Bogdan)   Bogdan's Parsek polls the shared folder at KSC, imports
              ana/000014. Harbor appears in his timeline ("Harbor - Ana"),
              plays as a ghost, and later spawns real when his UT passes
              8,102,400 (Harbor's EndUT).
UT 8,150,000  Bogdan flies a tanker, docks to the spawned Harbor at
              8,151,000, transfers 800 LF, undocks 8,151,600, deorbits,
              recovers. Commits at 2026-09-01T19:42:11Z. Packet
              bogdan/000007 exported, carrying a tip claim on Harbor's
              terminal recording.
Meanwhile     Ana, not yet synced, flies a lab module and docks it to
              Harbor at UT 8,140,000, leaving it attached (structural).
              Commits at 2026-09-01T19:30:05Z. Packet ana/000015 exported
              with its own tip claim on the same Harbor recording.
Fold          Every machine runs the same arbitration fold over all known
              claims in (claimRealTime, playerId) order: Ana's 19:30:05
              processes first. Her interaction is structural, so it
              advances Harbor's canonical tip: core -> +lab.
              Bogdan's 19:42:11 claim processes second. His window
              [8,151,000..8,151,600] intersects no canonical continuation
              window, but the fit predicate's state clause fails: Harbor's
              canonical part tree at 8,151,000 includes the lab, and
              Bogdan's recorded dock target does not. Fit fails ->
              Case 2 -> v1 truncates.
Resolution    On Bogdan's machine the salvage executes: his docked-window
              and post-undock recordings are retired, the pre-dock segment
              survives under a new recording id with a recomputed Orbiting
              terminal and a dock-time snapshot, and the tail's ledger rows
              are tombstoned. His next packet carries the truncation
              retirement. Every other machine derives the same verdict
              from the fold and masks Bogdan's post-dock chain from
              canonical state until that packet confirms it durably.
              Bogdan is notified on screen; his tanker sits parked in
              orbit near Harbor. He later takes control of it (his own
              vessel), redocks to the lab-equipped Harbor, and re-flies
              the delivery.
Economy       Both paid launch costs concurrently; the merged walk dips
              to -12,000 funds at UT 8,150,200. Both machines show 0
              funds; the next earning of 40,000 patches to 28,000.
              One-shot warning names both rollout rows.
```

If Bogdan's fuel run had happened before Ana's lab flight on the timeline AND against the part tree he actually recorded (say docked window [8,120,000..8,120,600], pre-lab Harbor), the fit predicate would pass and it would splice (Case 1): both missions fully canonical, Harbor's timeline shows "Docked: Tanker (Bogdan)", and the only fiction is 800 LF that Ana's later recording never knew was gone.

---

## 2. Design Philosophy

1. **One shared program as an experience, not a shared file.** Players share outcomes (timeline, economy, world), never a concurrently-written file. Every file in the shared folder has exactly one writer, forever. The sync service never sees a conflict, so its conflict handling never matters.
2. **Append-only exchange.** Packets, retirement records, checkpoints, and manifests are only ever added. The protocol never deletes or rewrites shared files (the sole exception: each player's own `player.cfg`, single-writer). Pruning is allowed only below a checkpoint's high-water marks (7.13); any other gap is tolerated loudly, never silently.
3. **Owner-authoritative data vs local runtime state vs derived state.** Three kinds of state, three rules. (a) A recording's trajectory, events, snapshots, topology, and ledger rows belong to its owner: only the owner mutates them, and changes are exchanged as packets/retirements. (b) Spawn stamps, ghost state, and render state are per-save runtime facts: every player's game spawns its own copy of a peer's station, and those stamps are never exported. (c) Arbitration outputs (verdicts, canonical flags, visit annotations, masks) are DERIVED state: recomputed from the exchanged log on every merge, never write-once, and exempt from the ownership fence because deriving is not editing.
4. **Deterministic convergence: same log, same state.** Given the same set of shared files (packets + retirements + checkpoints), every machine independently derives identical shared state: same walk order, same fold verdicts, same credit assignment. Machines that have seen different subsets may transiently differ; they converge exactly when their logs do. No machine is special; there is no server.
5. **Two clocks, used deliberately.** Vessel-state lineage and crew claims are resolved by SHARE order (packet export timestamp, ties by player id). The economy walk is resolved by UT order (as the ledger already does). These are different questions and get different clocks; this is the one place the design departs from pure UT ordering, and it does so to keep vessel history causal.
6. **Paradox avoidance over fairness.** Conflict resolution never rewrites a winner's data and never moves anything backward in time. All physical salvage is loser-side restructuring of the loser's own data; everyone else applies the same verdict as derived masking. A little resource slack (fuel appearing from nothing) is acceptable; deleting recorded state is not.
7. **Reuse before invention.** The recalculation engine already replays from UT=0 and supports retroactive insertion; the once-ever dedup pattern already exists three times (milestones, contracts, science caps); the ghost-block guard family already fences stock entry points; `RecordingOptimizer.SplitAtUT` already cuts recordings; supersede relations and tombstones already retire timeline segments; route revalidation already handles dangling references. This feature composes those mechanisms; it invents only the exchange layer and the arbitration fold.
8. **Basic but reliable.** v1 prefers the smaller mechanism with the louder failure. Degradation is always visible (marked ghosts, on-screen notices, log lines), never silent.

---

## 3. Terminology

| Term | Definition |
|------|------------|
| Campaign | One shared career: a manifest, checkpoints, and a set of member players, rooted in one shared folder. |
| Founder | The player who created the campaign. Their save at creation time is checkpoint 0; their seed rows are the campaign's only economy seeds. |
| Player id | Stable GUID identifying a player across sessions, generated once at first identity setup. Display name is separate and freely editable. |
| Shared folder | The folder synced by the players' file-sharing service (one service per campaign; see 7.1). Parsek reads all of it, writes only the local player's subtree (and, for the founder, checkpoints). |
| Packet | One append-only contribution: an envelope file plus a payload directory, exported by one player, carrying committed trees/recordings, ledger actions, game-state events, crew attributes, tip claims, and retirement records. |
| Checkpoint | A founder-written full-state snapshot plus a per-player seq high-water map. Checkpoint 0 is the campaign snapshot. Joiners bootstrap from the latest checkpoint; packets at or below every mark of a checkpoint are prunable. |
| Foreign (data/recording/vessel) | Owned by another player. Loaded locally as a full citizen but fenced read-only (owner-authoritative surfaces). |
| Canonical chain / tip | Per physical vessel: the owner's committed topology AS OF THE TERMINAL RECORDING'S FIRST EXPORT (checkpoint or packet), extended only by the tip-advancing continuations the arbitration fold accepts - the owner's own later continuations and recoveries included (they are claims like anyone else's, 7.7). The tip is the last link. Derived purely from exchanged data; local spawn stamps play no role. |
| Consuming interaction | An interaction that takes over a vessel's state: taking control (Fly/Switch-To), dock, claw (non-EVA), board, EVA construction on it, recovery. Flying nearby or targeting is not consuming (verified: no store mutation occurs). |
| Tip-advancing vs transient | A consuming interaction ADVANCES the tip when it leaves the target with a different part tree, crew, or trajectory (orbit / landed position beyond tolerance) than it found; it is TRANSIENT (a visit) when it leaves all three as it found them. Resources and inventory never decide this. |
| Foreign continuation | A new local tree, owned by the controlling player, whose root recording continues a foreign recording's spawned vessel (cross-tree link `ContinuesForeignRecordingId`). The route Fly/Switch-To on a foreign spawn takes instead of the tree-replacing restore path. |
| Tip claim | A packet's declaration that one of its recordings consumed a specific recording's terminal (target recording id + window + claim timestamp). |
| Arbitration fold | The pure, deterministic pass every machine runs at every merge over ALL known claims and retirements, in one global (claimRealTime, playerId) order, producing all derived multiplayer state from scratch. |
| Salvage ladder | The deterministic three-case resolution applied to a claim the fold rejects: splice, truncate (stitch later), truncate. |
| Visit annotation | The derived canonical record of a spliced (Case 1) non-structural visit: attribution on the target's timeline without a continuation. Recomputed by the fold; any persisted copy is a cache. |
| Retirement record | An append-only export telling peers that the owner superseded/rewound/discarded/truncated previously shared data. A truncation retirement is also a permanent claim withdrawal in the fold. |
| Derived mask | The fold's suppression of a rejected claim's continuation chain from canonical state (ERS-level), applied identically on every machine, ahead of the loser's durable retirement packet. |
| Merge | The import pipeline run: scan shared folder, apply new packets, run the fold, recalculate, patch (or defer the patch). |

Design-concept-to-implementation-class mapping: section 18.

---

## 4. Mental Model

### 4.1 The shared folder

```
<SharedFolder>/                        (one sync service per campaign)
  campaign.cfg                         single writer: founder, written once
  checkpoints/
    000000/                            = the campaign snapshot (checkpoint 0)
      highwater.cfg                    {playerId -> seq} (all zero for 000000)
      persistent.sfs
      Parsek/...                       (full sidecar tree, strip list applied)
    000001/                            later founder-written checkpoints
  players/
    p_ana/                             single writer: Ana, forever
      player.cfg                       identity + lastSeq, rewritten by Ana only
      packets/
        000014_a3f9.ppkt               envelope (written LAST = packet complete)
        000014_a3f9/                   payload dir (written first)
          <recId>.prec
          <recId>_vessel.craft
          <recId>_ghost.craft
    p_bogdan/
      player.cfg
      packets/ ...
```

### 4.2 The arbitration fold under concurrency

```
 Known log (all machines, eventually identical):
   claims:      (19:30:05, p_ana,    target=HarborCore, structural)
                (19:42:11, p_bogdan, target=HarborCore, non-structural)
   retirements: (later) p_bogdan truncation: withdraws his claim,
                retires his docked-window + post-undock ids,
                supersedes his pre-dock id -> new head id

 Fold (recomputed from scratch at every merge, same result everywhere):
   1. order claims by (claimRealTime, playerId)
   2. Ana's claim: target is on the canonical chain, structural
      -> advances tip: HarborCore -> +lab
   3. Bogdan's claim: withdrawn? not yet -> evaluate: fit predicate fails
      -> verdict Case 2 -> derived mask over his post-dock chain
      (on Bogdan's machine only: also EXECUTE the truncation, once)
   4. after Bogdan's retirement arrives: his claim is withdrawn,
      the mask is replaced by durable retirement state; the fold's
      canonical output is unchanged

 A claim whose target id is unknown locally -> PENDING (excluded from the
 fold, retried next merge). A claim whose target is not on the canonical
 chain -> a loss by definition, evaluated against the canonical state
 covering its window. Withdrawn claims never contend again (no
 resurrection, even if the winner is later retired).
```

### 4.3 Data flow on merge

```
 shared folder ----scan----> new packets (per player, by seq)
      |                          |
      |                   validate (schema gen, hashes, id safety, timestamps)
      |                          |
      |                apply: recordings -> RecordingStore (owner-tagged,
      |                                     appended at END, latched replayed)
      |                       actions    -> Ledger (owner-tagged)
      |                       events     -> MilestoneStore registration
      |                       crew       -> roster materialization
      |                       retirements-> supersede rows / withdrawals
      |                          |
      |                arbitration fold -> verdicts, masks, annotations,
      |                                    crew claims (all derived)
      |                          |
      |                RecalculateAndPatch (current-UT cutoff walk,
      |                authoritative reduction; patch deferred while a
      |                recorder is live, lands at the next trigger)
      |                          |
      +--- local exports <--- commit path + action flush (own data only)
```

---

## 5. Existing Systems: What Changes vs What's New

| Component | Current behavior | Required change | Complexity |
|-----------|------------------|-----------------|------------|
| `RecordingStore` | Static store; `CommitTree` is the only production registration seam (7-step orchestrator, `RecordingStore.cs:1076-1084`) | Owner tags; ownership fence; import registration mirroring the CommitTree shape (append-at-END only, `BumpStateVersion`, `RebuildBackgroundMap`) | Med |
| `GameActions/RecalculationEngine.cs` (`SortActions`) | Sort (UT, earning-first, Sequence); ties fall to list order | Add (OwnerPlayerId, ActionId) tiebreak: deterministic cross-machine | Low |
| `GameActions/Ledger.cs` (`Reconcile`) | On TRACKSTATION/EDITOR loads with an initialized clock, future-UT spending/contract/untagged rows are permanently pruned (`Ledger.cs:763-940`); earnings are not | Campaign-linked reconcile always preserves future timeline rows (peers warp ahead); recordingId-validity pruning unchanged | Med |
| `LedgerOrchestrator` | Uncut `RecalculateAndPatch()` walks ALL actions (future earnings patch into current pools); cutoff variant exists (`RecalculateAndPatchForCurrentTimelineIfFutureActions`) | Campaign-linked recalcs ALWAYS run the current-UT cutoff walk (peer future rows otherwise inflate pools and drag the contract-deadline horizon); merge recalcs pass authoritative reduction | Med |
| `FundsModule` / `ScienceModule` / `FacilitiesModule` / `StrategiesModule` | Duplicate same-target spends double-charge, idempotently apply | Once-ever by target key: first effective, duplicates uncharged + ineffective (fixes a latent single-player double-charge too) | Med |
| `KspStatePatcher` + `GameStateStore` baselines | `CaptureBaselineIfNeeded` snapshots LIVE state at every commit; `SelectTechBaselineForPatch` picks latest baseline <= cutoff | Campaign-linked patching pins to the checkpoint baseline (identical on every machine); post-link locally captured baselines are not used for merged facets (they bake foreign state divergently) | Med |
| `MilestoneStore` | Independent state built at commit (`CreateMilestone`); feeds live blocks (`TechResearchPatch`, `KerbalHirePatch`) and overlays; NEVER rebuilt from the ledger | Import registers foreign milestone/event bundles (packets carry them) so live blocks see foreign tech unlocks and hires | Med |
| `KerbalsModule` / `CrewReservationManager` | Reservation derived from UT=0; unknown crew names warn-and-skip at reservation; only `VesselSpawner.EnsureCrewExistInRoster` creates missing crew (spawn-time, name-only, random stats) | Import-time crew materialization from packet crew attributes; crew claim rule (share order) + derived stand-in substitution for losers | Med |
| `PlaybackScopeTracker` | A recording whose activation start is past and was never latched is `historical-never-replayed` and suppressed from playback AND spawn | Importer latches imported recordings as replayed so peer history plays and spawns | Low |
| Spawn gate (`GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` + hosts) | Spawns terminal Orbiting/Landed/Splashed leaves once | Unchanged for foreign recordings (spawn stamps are local runtime state); `MissingParts` recordings get a new early reject | Low |
| Fly/Switch-To consume (`ParsekFlight.TryConsumeStockActionIntent` -> `TryTakeCommittedTreeForSpawnedVesselRestore`) | Three branches: committed-tree clone (deep-clone + replace in place at commit), BG-member continuation, standalone | Fourth branch for FOREIGN spawned vessels: a new local tree whose root continues the foreign recording (`ContinuesForeignRecordingId`), built with `SwitchSegmentBuilder`; the restore path is never taken for foreign trees (it would replace the owner's tree). No-op segments auto-discard via `SwitchSegmentNoOpClassifier` | Med |
| TS Recover of a spawned committed vessel (`GhostTrackingStationPatch` `OnRecoverConfirm` passthrough for real vessels; recovery ledger rows) | Own spawned vessels: whatever happens today (verify at plan time how the recording's terminal is marked) | Foreign spawns: identical behavior plus a Recover terminal claim in the next flush packet. No guards: foreign vessels are controlled as if own | Low |
| `TerminalSpawnSupersededByRecordingId` (`RecordingStore.SupersedeTerminalSpawn.cs`) | Written at dock merge + commit-time pass; read by the spawn gate; NEVER cleared anywhere | Fold-driven spawn-ownership reconciliation: clear stale/dangling stamps, re-point the canonical tip at a live tracked instance, hold a degraded ghost when the tip is unspawnable (7.8 step 6) | Med |
| Post-commit leaf spawner (`ParsekFlight.SpawnTreeLeaves` <- `RecordingTree.GetSpawnableLeaves` / `IsSpawnableLeaf`) | Spawns committed leaves WITHOUT the spawn gate: three checks (no children, terminal not Destroyed/Recovered/Docked/Boarded, snapshot non-null); ignores the supersede stamp, `VesselSpawned`, debris, rewind suppression | Route through the spawn gate (pre-refactor C3, a behavior change with its own PR) so masks and re-points bind all six spawn paths | Med |
| `RecordingStore.RunOptimizationPass` (five sub-passes) | Merge, split, boring-tail trim, loop-sync parent indices, discovery-time checkpoint normalization; the last three rewrite bytes outside any boundary predicate | One `IsOptimizationFrozen` predicate at four sites (merge guard, split loop head + its discovery normalization, tail-trim loop head) | Low |
| `Logistics/RouteStore` endpoint validation | Endpoint resolves any Parsek-tracked pid | Refuse foreign-owned endpoints at route creation | Low |
| Dock merge (`ParsekFlight.HandleTreeDockMerge` / `CreateMergeBranch`) | Branch in controller's tree; `MarkTerminalSpawnSupersededByDockMerge` stamps the foreign recording's local spawn state; pre-dock parent closed `TerminalState.Docked`, NO fresh snapshot stamped on it | Allowed on foreign spawns; stamp stays local-only; commit derives a tip claim; NEW: stamp the transient dock-time self-snapshot onto the closing parent (makes truncation clean; additive, harmless solo) | Med |
| `RecordingOptimizer` (load-time merge/split) | Rewrites committed recordings; `SplitAtUT` nulls the head's terminal | Frozen for exported recordings (export = optimization fence, like the existing supersede-row `CanAutoMerge` guard) and for all foreign recordings | Low |
| `LedgerRolloutAdoption` / `LedgerRecoveryFundsPairing` | Reassign `Sequence` on EXISTING rows (`LedgerRolloutAdoption` line 93, 355/391/431) | Exported actions' ordering fields (UT, Type, Sequence) freeze; adoption/pairing must run before export or express changes as new rows | Med |
| `RecordingStore.CleanOrphanFiles` | Quarantines unknown-id sidecars on load | Unchanged (imported foreign ids are in the known set); import self-heals a crash via the import journal | Low |
| Ghost chain derivation | `EvaluateAndApplyGhostChains` has exactly two call sites (`OnFlightReady`, merge-dialog commit) | Import adds a third trigger after registration | Low |
| `MergeDialog` / notifications | Merge/discard dialogs | One-shot ScreenMessage for fold verdicts + debt warnings; flags in Recordings Manager rows (existing surfaces only, per standing no-new-UI-surfaces directive) | Low |
| `UI/SettingsWindowUI` | Settings sections | New Multiplayer section: player name, shared-folder path + validate, campaign create/join/leave, sync-now, checkpoint (founder) | Med |
| `TimelineBuilder` / timeline window | Entries with semantic colors | Owner name on attributed entries; per-player filter alongside existing source filters | Low |
| `ParsekScenario` OnSave/OnLoad | Save/load + sweeps; the `SaveStagingList`/`LoadStagingList` pattern for additive lists | Persist owner fields, campaign link, annotation cache, orphan flags, import journal pointer, following the staging-list pattern | Med |
| `EffectiveState` | Supersede walk does not halt at tree boundaries; a TODO at `EffectiveState.cs:109` proposes ADDING a halt | The TODO's proposed halt is REJECTED: cross-tree supersede rows become legitimate producible data under retirements, and the current non-halting walk is the contract. Closing the TODO means deleting it, not implementing it | Low |
| `Logistics/RouteStore` | Routes revalidate sources vs ERS | Unchanged; routes are owner-local (foreign route rows import as inert history) | Low |

New subsystems: campaign store (manifest, checkpoints), packet exporter, packet importer + journal, arbitration fold (claims, verdicts, masks, crew claims), conflict classifier + salvage executor, ownership fence. Section 18 lists proposed files.

---

## 6. Data Model

### 6.1 New types

```
PlayerIdentity (class, per-save in ParsekScenario; machine default cached in PluginData)
  playerId: string      - "p_" + Guid("N"), generated once, never edited
  displayName: string   - free text, editable in Settings, trimmed, non-empty

CampaignManifest (campaign.cfg, ConfigNode, written once by founder)
  campaignId: string        - "camp_" + Guid("N")
  name: string              - display name (free text)
  saveSlug: string          - filename-safe slug derived from name (7.2)
  founderPlayerId: string
  createdRealTime: string   - fixed-format UTC timestamp (6.5)
  createdUT: double         - founder's UT at creation
  formatVersion: int        - pinned RecordingStore.CurrentRecordingFormatVersion
  schemaGeneration: int     - pinned RecordingStore.CurrentRecordingSchemaGeneration
  parsekVersion: string     - informational
  gameMode: string          - CAREER / SCIENCE / SANDBOX (economy inert outside CAREER)

CheckpointManifest (checkpoints/<n>/highwater.cfg, single writer = founder)
  checkpointSeq: int
  createdRealTime: string
  HIGHWATER { PLAYER id seq ... }  - highest packet seq per player included

PlayerManifest (players/<id>/player.cfg, ConfigNode, single writer = owner)
  playerId, displayName
  joinedRealTime: string
  lastSeq: int              - highest packet sequence this player has exported

PacketEnvelope (<seq>_<shortId>.ppkt, ConfigNode, written LAST)
  packetId: string          - "pkt_" + Guid("N")
  seq: int                  - per-player monotonic, gap-detectable
  ownerPlayerId: string     - must match the players/<id>/ subtree (validated)
  exportedRealTime: string  - fixed-format UTC timestamp (6.5); the arbitration clock
  exporterUT: double        - the exporter's current game UT at export (display only:
                              the Settings member list shows each player's clock)
  schemaGeneration, formatVersion: int
  TREES { }                 - complete per-tree RECORDING_TREE + RECORDING states (6.6)
  ACTIONS { }               - GameAction rows (owner's, non-seed only)
  EVENTS { }                - per-tree game-state event bundles (milestone registration; 7.5)
  CREW { MEMBER name trait gender veteran ... } - minimal attributes for roster materialization
  TIP_CLAIMS { CLAIM ... }  - see TipClaim
  RETIREMENTS { ... }       - see RetirementRecord
  MISSION_NAMES { }         - tree id -> mission/root-group name at export
  PART_MANIFEST { }         - distinct part names used by this packet's snapshots
  PAYLOAD { FILE name size sha256 ... } - integrity list for the payload dir

TipClaim (node inside envelope; the claimant is the envelope's ownerPlayerId)
  kind: enum { Dock=0, Board=1, Claw=2, Control=3, Recover=4 }
  targetRecordingId: string      - the consumed recording's id
  targetOwnerPlayerId: string
  continuationRecordingId: string - the claimant's continuation recording (the
                                    docked-window recording for Dock/Board/Claw;
                                    the foreign-continuation root for Control;
                                    null for Recover, which carries no recording)
  interactionStartUT: double      - dock/board/take-control/recover UT
  interactionEndUT: double        - undock/unboard/release UT (= start for one-way)
  advancesTip: bool               - claimant-computed HINT: the target's part tree,
                                    crew, or trajectory differs at exit vs entry
                                    (tolerances in 7.9); every importer recomputes
                                    it from the payload and refuses the packet on
                                    mismatch (Error naming the claim). Recover is
                                    always tip-advancing (it ends the chain)
  claimRealTime: string           - equals envelope exportedRealTime

RetirementRecord (node inside envelope; append-only)
  kind: enum { Supersede=0, RewindRetire=1, Discard=2, Truncation=3 }
  recordingIds: list<string>      - owner's retired recording ids
  supersedeRelations: list        - Old->New rows (Supersede and Truncation)
  withdrawnClaimContinuationId: string - Truncation only: the claim this withdraws
  reasonUT: double

ConflictVerdict (derived, runtime only, recomputed by every fold, never persisted
                 as authority)
  claim: TipClaim
  outcome: enum { Won=0, Spliced=1, Rejected=2, Pending=3, Withdrawn=4 }
  caseApplied: enum { None=0, Case1Splice=1, Case2Truncate=2, Case3Truncate=3 }

VisitAnnotation (derived by the fold; a cached copy may persist in the .sfs
                 under the campaign node but is overwritten by every fold)
  targetRecordingId: string
  visitorPlayerId, visitorRecordingId: string
  startUT, endUT: double
  resourceDeltaSummary: string    - display-only ("-800 LF"), never applied

ImportJournal (saves/<save>/Parsek/Multiplayer/import-state.cfg, local only)
  appliedPackets: list<(playerId, seq, packetId)>
  pendingRefs: list<(packetId, missingRecordingId)>  - claims/retirements awaiting referents
  lastScanRealTime: string
```

Class-vs-struct: all exchange types are classes (ConfigNode round-trip, nullable
fields). Enums carry explicit int values (serialized).

### 6.2 Changes to existing types

**`Recording`** (`Source/Parsek/Recording.cs`):
- `OwnerPlayerId: string` - null on pre-campaign and solo recordings; stamped at
  campaign link/creation where null, at commit once linked; foreign on import.
  Null reads as "local player" for fence purposes. Stamping never overwrites a
  non-null value (a save that was in a previous campaign keeps prior owners).
- `Exported: bool` - set when the recording has left the machine (in a packet OR
  inside a checkpoint, including checkpoint 0); freezes optimization (6.4) and
  forbids destructive local edits not expressible as a retirement.
- `OrphanedByRetiredRecordingId: string` - set when a retirement removed a
  recording this one depended on (7.10); null otherwise. Local, persisted.
- `ContinuesForeignRecordingId: string` - set on the ROOT recording of a
  foreign-continuation tree (7.7): the foreign recording whose spawned vessel
  this tree took control of. Owner-authoritative on the continuing side (it
  is the claimant's own data), exchanged in the TREES fragment, read by claim
  derivation and by the fold's chain walk. Null otherwise.
- `MissingParts: bool` (runtime-only, derived at load/import) - snapshot
  references part names absent from the local `PartLoader`; blocks spawn and
  interaction, marks the ghost.

**`RecordingTree`**: `OwnerPlayerId: string` (same semantics).

**`GameAction`** (`Source/Parsek/GameActions/GameAction.cs`):
- `OwnerPlayerId: string` - stamped like Recording's. Once an action is exported,
  its ordering fields (UT, Type, Sequence) are FROZEN: `LedgerRolloutAdoption` /
  `LedgerRecoveryFundsPairing` re-sequencing must run before export or express
  the change as new rows (section 5 row; test 15.1).

**`RecalculationEngine.SortActions`**: sort key becomes
`(UT, earning-first, Sequence, OwnerPlayerId ?? "", ActionId)` - the two appended
keys exist purely so independently minted same-UT actions order identically on
every machine.

**`ParsekScenario`**: persists the campaign link (campaignId, playerId, shared
folder path, displayName), the annotation cache, orphan flags, and hosts the
merge hook points and the background poll coroutine. New nodes follow the
existing `SaveStagingList`/`LoadStagingList` remove-then-re-add pattern
(`ParsekScenario.cs:2534/2693`), including registration in the corresponding
`RemoveNodes` block.

### 6.3 Serialization format

- All exchange files are KSP ConfigNode text (same tooling, diffable, additive).
  Payload sidecars are byte-copies of the owner's local sidecars (`.prec` may be
  BinaryV0; the envelope's PAYLOAD hashes gate integrity).
- **Seq allocation is folder-derived at EVERY export:** `seq = max(local
  lastSeq, own player.cfg lastSeq, highest seq in own packets/) + 1`, never
  the local save alone. A crash between the envelope write and the next local
  save otherwise re-uses a seq (edge case 50).
- **Packet completeness protocol:** the payload directory is written first, the
  envelope last. Sync services deliver files in arbitrary order; an importer
  ignores any payload directory without its envelope, and an envelope whose
  PAYLOAD list does not verify (missing file, size/hash mismatch) is "incomplete,
  retry next merge" (Warn), escalating per 13's cloud-placeholder rules.
- Local writes into the shared folder use `FileIOUtils` safe-write (tmp+rename)
  with retry-and-backoff: sync clients briefly hold Windows share locks while
  hashing, so a failed replace is retried (3 attempts, growing delay) before the
  export queue takes over. The `.tmp` suffix is excluded from importer scans.
- Nothing in the shared folder is deleted or rewritten by protocol, except the
  owner's own `player.cfg` (single-writer rewrite, safe-write) and founder
  checkpoint creation. Pruning below checkpoint high-water marks is a manual,
  documented operation (7.13).
- Foreign recordings' sidecars land in the normal `saves/<save>/Parsek/Recordings/`
  directory under their original ids (GUID collision across machines is
  negligible; `RecordingPaths.ValidateRecordingId` gates every id arriving from
  a packet before any path is built - packets fail loudly on an invalid id).
- `SidecarEpoch` pairs (metadata + sidecar) always travel together inside one
  packet, so the exact-equality check holds for imported pairs.
- Runtime-only, never exported: spawn stamps (`VesselSpawned`,
  `SpawnedVesselPersistentId`, spawn safety/death state), ghost state, local
  `TerminalSpawnSupersededByRecordingId` stamps on foreign recordings,
  `MissingParts`, orphan flags, derived fold outputs, the import journal.

### 6.4 The ownership fence

One rule, enforced at every committed-data mutation site: **a mutation of
owner-authoritative data requires `OwnerPlayerId` in {null, local player}.**
Owner-authoritative surfaces: Points, PartEvents, SegmentEvents, FlagEvents,
TrackSections, OrbitSegments, snapshots, tree topology (recordings, branch
points, chain fields), MergeState, loop config, mission/group names, ledger
rows. Blocked operations on foreign data: rewind/re-fly, supersede authorship,
seal/stash, discard, rename, optimizer merge/split, Fly/Switch-To restore,
loop-config edits, group reassignment.

Explicitly exempt:

- **Local runtime state** (allowed on foreign recordings): spawn stamps and
  spawn safety state, `TerminalSpawnSupersededByRecordingId` + spawn-flag
  clears at dock merge and at the commit-time continued-source pass,
  `MissingParts`, orphan flags, ghost/render state, crew auto-unreserve
  bookkeeping at EndUT passage. Per-save facts, never exported, free to
  diverge.
- **Derived fold outputs** (masks, verdicts, annotations, crew claims):
  deriving is not editing. The fold changes what is CANONICAL, never the
  owner's stored bytes.
- **Owner-packet application**: replacing a stored tree's topology with a
  higher-seq TREES fragment from the SAME owner is the owner's own newer data
  applied in seq order, not a local edit (6.6).

`FilesDirty` on a foreign recording is permitted only for the local sidecar
rewrite that persists exempt state; the exported content of a foreign recording
is never regenerated locally.

Additionally, `Exported == true` freezes:

- the optimizer for OWN recordings (precedent: the supersede-row guard inside
  `RecordingOptimizer.CanAutoMerge`) - once shared, a recording's id and
  content are a contract with peers. This includes the founder's snapshot-
  shared history (`Exported` is set by checkpoint inclusion, not only by
  packets).
- the ordering fields of OWN exported ledger actions (6.2).

Own destructive verbs (rewind, supersede, discard, salvage truncation) remain
allowed on exported data because they are expressible as retirement records
(7.10).

### 6.5 Timestamps and ordering keys

Every exchange timestamp uses the fixed-width invariant format
`yyyy-MM-ddTHH:mm:ssZ` (UTC, whole seconds). Comparison is parse-then-compare
(DateTimeOffset ticks, InvariantCulture), never string comparison; mixed
precisions from future versions must still parse. A packet whose timestamps do
not parse is refused (Error). The arbitration key is
`(exportedRealTime ticks, ownerPlayerId ordinal)`; the ledger tiebreak key is
`(OwnerPlayerId ?? "", ActionId)`, both ordinal.

### 6.6 Cross-packet tree updates

TREES fragments are complete per-tree states. The importer replaces stored
topology for a tree id when a higher-seq packet from the SAME owner carries it
(fence-exempt per 6.4); a tree fragment whose owner differs from the packet's
subtree owner is refused. Recording CONTENT is immutable once exported: a
packet never NEEDS to re-carry an already-exported recording's payload, only
new recordings plus the updated tree topology; an importer that does see a
known recording id again accepts it idempotently (payload skipped, topology
applied, Verbose line) - a crash after the envelope write can legitimately
re-export (edge case 50). Content changes are only ever expressed as
retirements plus NEW recording ids (this is how salvage truncation ships:
7.9), with one derived exception: a recording's terminal becoming Recovered
is DERIVED from the winning Recover claim (7.7), never a stored mutation of
exported content.

---

## 7. Behavior

### 7.1 Player identity and settings

The Settings window gains a Multiplayer section (existing surface; complies with
the standing no-new-UI-surfaces directive - no new windows or popups):

- **Player name**: text field. First edit generates the permanent `playerId`
  (displayed read-only next to it, so identity recovery is possible; see edge
  case 27).
- **Shared folder**: text field for an absolute path + a Validate button that
  probes existence, writability, campaign presence, remaining path-length
  headroom (worst-case protocol filename appended to the root; refuse with the
  measured length near the Windows MAX_PATH limit), and cloud-placeholder
  status, reporting inline text. No OS folder-browser dialog. Default
  suggestion: `<KSP>/Multiparsek/<campaign>/` - a sane, inside-game-folder
  default per modding convention; in practice most groups will type a path
  inside their sync client's directory, which is their explicit informed
  choice.
- **Create campaign / Join campaign / Leave campaign / Sync now** buttons, and
  for the founder **Create checkpoint**, with inline status text: member list
  with each member's last-known game clock (from the latest packet's
  `exporterUT`), the shared balance including debt depth ("shared funds: 0
  displayed, 12,000 in debt"), last merge time, pending export/import counts,
  pending-arbitration count, the missing-parts summary, and a "recent
  verdicts (last merge)" text block listing each fold verdict, credit shift,
  and debt event of the last merge - a 300-packet catch-up merge produces one
  batched ScreenMessage, and this block is where the detail survives (edge
  case 44).
- **Validate also compares part manifests:** every packet already carries a
  PART_MANIFEST and `campaign.cfg` records the founder's `GameData` folder
  list (informational); Validate and Join report "Dana's packets use 3 parts
  you lack: ..." up front, and the hint text carries the shared-infrastructure
  rule ("stations others must dock to: stock parts only", edge case 37).
- **Relinking**: editing the shared-folder path on an already-linked save is
  accepted only if the target folder's `campaign.cfg` carries the SAME
  campaignId as the save's link (a moved Dropbox folder); a different
  campaign is refused with "this folder holds campaign X, this save belongs
  to campaign Y".

One sync service per campaign is a stated requirement (docs + validate hint):
bridging two services with a copying machine breaks the write-order protocol
and produces renamed duplicates. The importer recognizes sync-conflict-suffixed
envelope names (e.g. `... (1).ppkt`, `(conflicted copy)`) and Warn-names them
specifically (edge case 4).

Identity is stored per-save; a machine-wide default (name + playerId) is cached
under `GameData/Parsek/PluginData/identity.cfg` so new campaigns prefill.

### 7.2 Campaign creation

Founder, on a career save (science/sandbox allowed; economy layer inert there).
Preconditions, refused with the exact reason: shared folder invalid or already
carrying a campaign; save already campaign-linked; a merge journal is
non-empty; a re-fly session marker is armed; a test batch marker is present;
NotCommitted provisionals are pending. Then, in this order:

1. Derive `saveSlug` from the campaign name (filename-safe slug, uniquified
   against existing saves with the `#N` pattern).
2. Stamp `OwnerPlayerId = founderId` on every committed recording/tree/action
   WHERE IT IS NULL (never overwriting a prior campaign's owners), and set
   `Exported = true` on all of them (the checkpoint carries them off-machine;
   the flag records "shared", not "sent as a packet").
3. Force a full local save (so the stamps are on disk).
4. Copy `persistent.sfs` + the `saves/<save>/Parsek/` sidecar tree into
   `checkpoints/000000/` via `FileIOUtils.CopyDirectory`, applying the strip
   list: `Parsek/RewindPoints/`, `Parsek/Multiplayer/` (import journal), and
   the marker singletons are removed from the copied `.sfs` (re-fly marker,
   merge journal, stock-action intent, switch-segment session, test batch
   marker). Verify the copy; failure aborts creation loudly (no partial
   campaign).
5. Write `checkpoints/000000/highwater.cfg` (all players at seq 0).
6. Write `campaign.cfg` (pins formatVersion + schemaGeneration + gameMode +
   saveSlug). The manifest is written LAST: a folder without it is an
   unjoinable partial and the founder simply retries.
7. Write `players/<founderId>/player.cfg`.

The founder's three `*Initial` seed rows live in checkpoint 0's ledger and are
the campaign's ONLY seeds, forever.

### 7.3 Joining

1. Player points Settings at the shared folder; Join validates `campaign.cfg`
   (schema generation must equal the local build's, else refuse with the exact
   mismatch - at join time the manifest pin IS the gate; per-packet gates take
   over afterwards).
2. If `players/<localPlayerId>/` already exists, this is a RE-join: skip to
   step 5 with a fresh save clone, and resume the existing subtree (edge case
   27) - never mint a second subtree for the same id.
3. Parsek clones the latest checkpoint into a NEW local save
   (`saves/<saveSlug>/`), never touching an existing save.
4. **Post-clone regeneration:** the clone's stock `ContractSystem` scenario
   node and astronaut applicant pool are stripped/regenerated so each member
   rolls fresh procedural contracts and applicants (mechanism: remove the
   `SCENARIO { name = ContractSystem }` node from the cloned `persistent.sfs`
   so KSP recreates it on first load, and remove `ROSTER` entries whose
   `type = Applicant`; crew/assigned kerbals are untouched). Without this every member
   completes the founder's identical cloned contract queue and pours N times
   the same payouts into the shared pool (contracts are local-only but their
   rewards are shared), and applicant name collisions become near-certain.
5. `PreParsekBackup` recognizes the clone (Parsek footprint present) and stays
   quiet.
6. Writes `players/<playerId>/player.cfg`. The exporter seeds `lastSeq` from
   max(shared player.cfg, a scan of the own packets/ dir) - never from the
   fresh local save - so a deleted-and-rejoined save can never restart seq
   numbering (edge case 27).
7. Runs a full merge: imports every packet above the checkpoint's high-water
   marks, in per-player seq order.
8. The joiner's KSP career state converges on first load: the ledger walk +
   patcher overwrite funds/science/rep/tech/facilities/roster from the merged
   ledger, which is the mechanism that makes N locally-cloned saves one shared
   career.

### 7.4 Export (own contributions)

Three triggers:

1. **Commit**: a recording-tree commit exports a full packet: the committed
   tree's complete TREES fragment, new recordings' sidecars, the commit's new
   ledger actions (own, non-seed), the tree's game-state event bundle (EVENTS),
   crew attributes for any own crew not previously exported (CREW),
   mission/root-group name, derived tip claims (from the tree's Dock/Board
   branch points whose `TargetVesselPersistentId` resolves to a shared
   recording's vessel, plus the commit-time continued-source marks), and the
   part manifest.
2. **Retirement**: own rewind/supersede/discard/truncation exports a
   retirement packet (7.10).
3. **Action flush**: on each merge tick (and Sync now), own ledger actions not
   yet carried by any packet export as an ACTIONS-only packet (no TREES, no
   payload). Recording-scoped actions always ride their commit's packet;
   KSC/system actions (tech, facilities, hires, strategies) ride the next
   flush - a player who only does KSC work still contributes. Flush packets
   also carry MISSION_NAMES for any exported tree renamed since its last
   export, so mission renames propagate (importers apply them to the
   read-only foreign mission name).

Steps: write payload dir, then envelope (seq per 6.3: folder-derived max + 1),
then rewrite own `player.cfg` (safe-write with retry). Mark exported
recordings/actions `Exported = true`. Export failure (folder offline, disk
full) is non-blocking: the commit stands locally, the packet is queued and
retried on every merge tick, with a settings-visible "N packets pending
export; their claims will be timestamped when the folder returns" note and a
Warn. The arbitration timestamp is stamped when the envelope is actually
written (share order means what it says); the `[PacketExport]` Info line for
a queued packet names the original commit time so a lost photo-finish is
explainable from the log (edge case 54).

### 7.5 Import (the merge)

Runs at: save load (after the existing OnLoad sweeps), scene changes into
KSC/Tracking Station, a low-frequency background poll (default 90s) while in
KSC/TS/Map scenes, and the manual Sync now button.

Pipeline per new packet (ordered per player by seq; cross-player apply order
is arbitrary and harmless BECAUSE all cross-player semantics live in the fold
and the UT-sorted walk, and claims/retirements with unknown referents pend):

1. **Validate**: schema generation/format (hard refuse, name the reason - the
   existing `IsRecordingSchemaCompatible` policy), payload hashes (chunked,
   time-budgeted; see 13), id safety (`ValidateRecordingId` on every id),
   timestamps parse (6.5), owner consistency (packet under `players/X/` must
   carry ownerPlayerId X; tree fragments must belong to X), structural-hint
   recomputation on claims (6.1), no seed rows (Warn + strip).
2. **Apply recordings**: copy sidecars into the local Recordings dir, register
   trees/recordings mirroring the `CommitTree` shape: append at the END of
   `CommittedRecordings` (never insert: index-keyed engine/UI state has
   delete-side reindexing only), `BumpStateVersion`, `RebuildBackgroundMap`,
   auto-group with the owner's `AutoGeneratedRootGroupName` honored
   (display-name uniquified locally on collision, owner's canonical name
   preserved), mission seeded read-only, `MilestoneStore` registration from
   the packet's EVENTS bundle carrying each event's UT (so
   `TechResearchPatch`/`KerbalHirePatch` live blocks and stock-UI overlays
   see foreign unlocks/hires - but a live block honors a FOREIGN
   registration only when its UT <= the local current UT; a peer's unlock
   in your future must not stop you researching the node now, and your
   earlier-UT research then becomes the effective charged row under
   once-ever dedup with the peer's later row refunded and a credit-shift
   notice; edge case 49), and latch each
   imported recording in `PlaybackScopeTracker` as already-replayed (without
   this, peer history entirely in the local past is suppressed as
   historical-never-replayed and neither plays nor spawns).
3. **Apply actions** into the ledger (owner-tagged).
4. **Materialize crew** from the CREW block: create missing roster kerbals
   with the packet's name/trait/gender/veteran attributes (extending the
   `VesselSpawner.EnsureCrewExistInRoster` mechanism, which today creates
   name-only randoms at spawn time); reservation then derives normally.
5. **Apply retirements**: append supersede rows, register withdrawals,
   orphan-flag local dependents (7.10). Unknown referents pend (below).
6. **Run the arbitration fold** (7.8) over ALL known claims + retirements:
   recompute verdicts, masks, annotations, crew claims from scratch. Execute
   any newly-decided salvage that belongs to the LOCAL player (7.9).
7. **Recalculate**: one campaign recalc per merge batch: always the
   current-UT cutoff walk (never the uncut walk: peer future earnings must
   not patch into today's pools, and the contract PrePass deadline horizon
   must not be dragged forward by peer future rows), with authoritative
   reduction so the DrawdownGuard does not floor away a legitimate merged
   balance drop. Mid-flight, the existing patch-deferral applies: the walk
   runs, the KSP write lands at the next trigger with the deferral cleared
   (there is no flush queue; poll ticks and commits are the triggers).
8. **Journal**: record (playerId, seq, packetId) applied, plus the pending
   set: any claim or retirement referencing a recording id not yet known is
   journaled pending, EXCLUDED from the fold, retried each merge, and subject
   to the same staleness escalation as incomplete packets.
9. **Notify**: one batched ScreenMessage per merge summarizing new missions,
   fold verdicts, and economy warnings; full detail at Info level in the log.

World-state changes with a loaded/nearby affected vessel (e.g. a tip advance
retiring a stale local spawn while the player is parked next to it) are
deferred to the next scene change; everything else applies immediately.

Two campaign-linked behavior overrides accompany the merge machinery,
regardless of merge timing:

- **Ledger reconcile preserves the future.** `Ledger.Reconcile`'s UT-based
  pruning (which today permanently deletes future-UT spending/contract/
  untagged rows on TRACKSTATION and EDITOR loads) always runs with
  future-timeline preservation under a campaign link. Peers warp ahead;
  their future rows are campaign data, not stale junk. RecordingId-validity
  pruning is unchanged (imported recordings are registered, so their rows
  are valid).
- **Baseline pinning.** Campaign-linked patching selects the checkpoint
  baseline (identical on every machine) instead of the latest local
  baseline: post-link locally captured baselines bake merged foreign state
  (e.g. a peer's tech unlock) into unshared local files, which diverges the
  patch layer when that foreign state is later retired. The full walk from
  the pinned baseline re-derives everything; local baselines remain for
  non-campaign saves.

### 7.6 Foreign recordings as citizens

Imported recordings join `CommittedRecordings`/`CommittedTrees` and flow through
every existing read path: timeline entries (attributed "Vessel Name - Ana"),
Recordings Manager rows (owner column/badge, mutation controls disabled),
Missions tab (foreign trees visible, selections read-only), ghost playback,
map/TS presence, ERS/ELS. After registration the importer triggers ghost-chain
re-derivation (the third call site of `EvaluateAndApplyGhostChains`).

**Crew.** Foreign kerbals materialize at import (7.5 step 4): visible in the
roster and the Kerbals window, owner-tagged, dismissal-blocked, reservation
derived normally from UT=0. The assignment gate: a player may crew missions
only with kerbals they own or that are unclaimed. The gate runs at crew
ASSIGNMENT time (the VAB/SPH crew panel and the launch-pad crew dialog,
through the existing `CrewDialogFilter` patch), never at commit and never at
an in-flight board event. **Rescue carve-out:** a kerbal taken aboard from a
foreign spawned vessel in flight (EVA and Board from a vessel the local player
did not launch) is exempt; the rescuing recording's crew row is attributed to
the rescuer, ownership stays with the original owner, and the kerbal's
reservation bounds at the rescuer's recovery UT (edge case 48). A kerbal's
owner is a DERIVED fact: the owner of the earliest-shared packet (by the 6.5
arbitration key) containing a committed recording with that kerbal aboard;
checkpoint 0 counts as the earliest share, so a founder who flew the starting
four before creating the campaign owns them, and joiners hire (edge case 51).
Because the gate is evaluated locally pre-merge, two players can legitimately
launch the same kerbal before syncing; the fold then resolves ownership by
share order, and the LOSING recordings receive a derived crew substitution: a
deterministically named stand-in replaces the kerbal in the derived
reservation/display state, and the row is flagged "crew conflict: Jebediah is
on Ana's roster". The loser's stored recording bytes are not touched (the
substitution is derived state); their future missions must use
owned/unclaimed crew.

**Stand-ins converge.** Under a campaign link every stand-in name (the
ordinary reservation stand-ins the existing machinery mints while a kerbal is
away, and the conflict substitutions above) derives deterministically from
(campaignId, slot-owner name, chain depth) instead of KSP's random naming, and
the CREW block carries `standInFor` + `chainDepth` so an importer places the
kerbal in the SAME chain slot rather than materializing an unrelated kerbal.
Without this, one machine retires "Hanley" as a reclaimed stand-in while
another treats him as a normal active kerbal (edge case 52).

**Roster cap.** KSP's Astronaut Complex cap counts every roster kerbal; with
four members' crews materialized, level 1 is full after one hire. The hire
gate (`KerbalHirePatch`, which already wraps `HireRecruit`) computes the cap
over OWN plus UNCLAIMED crew only, and materialized foreign crew who are
Aboard a foreign vessel materialize as `Assigned`. Settings and the user
guide still recommend the pooled Astronaut Complex upgrade early (edge case
51).

### 7.7 Foreign vessel spawning and interaction

Spawning is unchanged: the same gate, the same terminal rules
(Orbiting/Landed/Splashed leaves), the same warp-to-spawn flow. The spawn
stamps land locally (exempt state). Two additions:

- `MissingParts` recordings never pass the spawn gate (new early reject with a
  logged reason) and their ghosts render with missing parts skipped plus a
  marker in the Recordings Manager row.
- **Any player controls any vessel in the campaign as if it were their own.**
  It is co-op: there are no ownership guards on stock verbs. Fly/Switch-To,
  Recover, and Terminate on a peer's spawned vessel behave exactly as on your
  own spawned vessels. What differs is only WHERE the resulting data lands
  and how it is shared:
  - **Fly/Switch-To (taking control).** The stock click arms the existing
    `StockActionIntentMarker`; `TryConsumeStockActionIntent` gains a fourth
    branch: when the target is a FOREIGN spawned vessel, the continuation is
    NOT the restore path (`TryTakeCommittedTreeForSpawnedVesselRestore`
    deep-clones and replaces the owner's committed tree, which the fence
    forbids and the exchange cannot carry). Instead `SwitchSegmentBuilder`
    creates a new local tree, owned by the local player, whose root recording
    continues the foreign vessel (`ContinuesForeignRecordingId` = the foreign
    recording; `VesselPersistentId` = the live spawn pid). A fresh
    `SwitchSegmentSession` is armed as today. The foreign recording receives
    only local spawn-state stamps (exempt). A segment that changed nothing is
    auto-discarded by the existing `SwitchSegmentNoOpClassifier` hooks (scene
    exit, in-flight re-switch): no claim, nothing exported. Otherwise the
    commit exports the tree plus a `Control` tip claim whose window is
    [take-control UT, release UT].
  - **Recover from the Tracking Station / KSC (own or foreign).** The player
    experience is today's: recovery funds and crew return as the
    recoverer's ledger rows and the local instance despawns. The DATA
    differs once the vessel's terminal recording is `Exported`: today own
    recovery mutates the recording's stored terminal to Recovered
    (`ParsekScenario.OnVesselRecovered` -> `UpdateRecordingsForTerminalEvent`)
    and appends a `FundsEarning(Recovery)` tagged to it; under a campaign
    link the terminal is NOT mutated (exported content is frozen, 6.6).
    Instead the recovery emits a `Recover` tip claim in the next flush
    packet - for the OWNER too - and "terminal = Recovered at UT x" is a
    DERIVED fact from the winning claim on every machine. Recover is always
    tip-advancing. Recovery earnings for a spawned committed vessel are
    once-ever by target recording id: the winning claimant's row is
    effective, a losing recoverer's row is masked by the fold (edge case
    48). Recovery of a NOT-yet-exported own vessel keeps today's stored
    mutation (nothing to converge yet).
  - **Own continuation of an exported terminal.** When the owner
    Fly/Switch-To's their OWN spawned vessel whose terminal recording is
    `Exported`, the restore path runs as today (deep-clone, continue,
    replace the own tree at commit - the fence allows it, it is own data),
    but the commit ALSO derives a `Control` claim (target = that terminal
    recording, window = [take-control UT, release UT]) exactly as a foreign
    continuation would. Own claims contend by the same key. Without this,
    the owner's topology would make their continuation canonical by
    definition and silently overrule an earlier-shared peer claim (edge case
    47). A losing own continuation is truncated with the 7.9 procedure
    applied inside the owner's tree: the pre-continuation leaf is the
    surviving head (it already carries its terminal and snapshot; no new id
    is needed), the continuation segments retire, and the retirement packet
    withdraws the claim.
  - **Terminate from the Tracking Station.** Same as for own spawned
    committed vessels today: the local instance is deleted, the spawn-death
    check re-spawns it up to `MaxSpawnDeathCycles`, then marks it abandoned
    LOCALLY. No claim (termination is not a recorded event today for own
    vessels either); the canonical chain is untouched and peers are
    unaffected. Consistent, if slightly haunted; documented in the user
    guide.
  - **Coupling (dock, non-EVA claw, board, EVA construction).** Exactly as
    against own spawned vessels today: the branch and merged recording land
    in the controller's tree (verified: the dock branch point has one parent,
    the merged child carries the survivor pid and a combined snapshot
    including the target's parts); the foreign recording receives only the
    local spawn-state stamp; at commit the interaction is a `Dock` / `Board`
    / `Claw` claim. The EVA-kerbal grapple carve-out is preserved (no partner
    stamp, no claim).

Flying nearby without coupling or taking control touches nothing and never
produces a claim (verified: no store mutation on proximity).

One additive recorder change ships with this feature: at dock-merge time,
`CreateMergeBranch` stamps the transient dock-time self-snapshot
(`pendingDockSelfSnapshot`) onto the closing active pre-dock parent, and the
partner snapshot onto a background parent when the partner pid matches (both
today keep their segment-start snapshot and get no fresh one). The stamps are
conditional: the captures happen only while the two vessels are still
distinct, so a null capture leaves the segment-start snapshot in place (the
fallback in 7.9 step 1 is load-bearing, not a corner). Ghost appearance is
unaffected (meshes read the separate `GhostVisualSnapshot`). This is what
makes salvage truncation clean (7.9); it is harmless in solo play.

At commit, consuming interactions with shared vessels produce tip claims in
the exported packet, derived from the branch points and the terminal-spawn
supersede marks.

### 7.8 The arbitration fold

All cross-player vessel and crew semantics are computed by one pure fold,
recomputed from scratch at every merge, over the complete known log of claims
and retirements. Inputs: exchanged data only (committed topology from packets,
claims, retirements). Local spawn stamps play no role. Output: for each
physical vessel a canonical chain and tip; for each claim a verdict; visit
annotations; derived masks; crew ownership. Same log in, same state out, on
every machine (transient divergence exists exactly while logs differ, and
heals when they converge).

The fold:

1. Order all claims by `(claimRealTime, ownerPlayerId)` (6.5) into ONE global
   sequence (not per-target buckets: chains of claims interact). Claims are
   keyed by `continuationRecordingId` (Recover claims by target id +
   claimant): when several claims share a key (a re-export after a crash,
   edge case 50), only the earliest by arbitration key enters the sequence;
   the rest are dropped with a Verbose line, never treated as new
   contenders.
2. Skip claims that are WITHDRAWN: a claim is withdrawn when ANY retirement
   (Truncation naming it explicitly, or a Supersede / RewindRetire / Discard
   whose retired ids include the claim's continuation recording - e.g. the
   claimant rewound to before the dock) retires its continuation. A
   withdrawal is permanent: an executed loss never contends again, even if
   the claim that beat it is later retired (no resurrection; a fresh joiner
   replaying the full log reaches the same state as every incumbent).
3. For each remaining claim in order:
   - If its target recording id is unknown locally: PENDING (excluded,
     retried next merge). A pending claim's continuation chain is derived-
     masked exactly like a rejected one until the claim resolves (it neither
     plays nor spawns; its row reads "pending arbitration"), so no fourth
     player can dock to undecided infrastructure and cascade into a
     deterministic loss (edge case 29).
   - Determine the canonical link COVERING the claim's window start s (the
     chain link whose interval contains s; the chain as built so far in this
     fold). If the claim's target is that link and the fit predicate (7.9)
     passes: the claim is ACCEPTED. Tip-advancing claims (the target left
     with a different part tree, crew, or trajectory; every Control claim
     that moved the vessel; every Recover claim) advance the tip: their
     continuation becomes the next link (Recover ends the chain). Transient
     claims (the target left exactly as found, resources aside) canonicalize
     as a VisitAnnotation and do NOT advance the tip (a fuel run leaves the
     tip unchanged, so later fuel runs against the same tip id are ordinary
     accepted visits, not conflicts).
   - Otherwise (target is not the covering link, or fit fails): the claim
     is REJECTED and enters the salvage ladder, classified against the
     canonical state covering its window. This includes claims targeting a
     continuation that itself lost (the A->B->C cascade: C's target B is off
     the canonical chain, so C rejects deterministically). A losing Recover
     claim additionally masks its claimant's recovery earning row (7.7):
     the fold's ledger mask is a set of recording ids PLUS a set of action
     ids.
4. Derived masks: every rejected, non-withdrawn claim's continuation chain
   (docked-window recording + descendants) and every accepted non-structural
   claim's target-side post-undock recording (the demoted station-side leaf)
   is masked from canonical state on ALL machines. Masking is derived
   suppression at the effective-state layer: masked recordings neither play
   as ghosts nor pass any spawn path (all six, including the post-commit
   leaf spawner, which today bypasses the spawn gate and must be routed
   through it first) nor feed the ledger. The ledger half is a recording-id
   exclusion inside `ComputeELS` keyed on the mask version: today ELS
   retires rows ONLY through durable per-action tombstones, and that
   contract widens to "tombstones or fold masks" rather than minting fake
   tombstones for a derived fact. The mask is replaced by durable retirement
   state when the loser's confirming packet arrives.
5. Crew ownership (7.6) is folded in the same pass, by the same key.
6. **Spawn-ownership reconciliation** (local, per vessel). Today a dock
   merge stamps the target's `TerminalSpawnSupersededByRecordingId` at merge
   time and NOTHING ever clears it; ownership of the physical spawn is
   otherwise emergent from "leaf with a spawnable terminal". Both facts break
   under the fold (a demoted leaf would win spawn by leaf-ness; a stale
   stamp would stop the canonical tip from ever spawning). So after every
   fold, for each vessel: the canonical tip's recording is the sole local
   spawn owner; a local supersede stamp that points at a masked, demoted,
   retired, or unknown recording is cleared; and when a demoted or masked
   continuation was tracking a LIVE local vessel instance (the physical
   station you undocked from, now with less fuel), the canonical tip's
   local `SpawnedVesselPersistentId` is re-pointed at that instance's pid so
   the same physical vessel keeps standing and no duplicate spawns. When the
   canonical tip cannot spawn locally (missing parts), the vessel is
   represented as a HELD DEGRADED GHOST at the tip's terminal state: the
   existing terminal-hold machinery (the same hold the terminal-orbit
   safety check uses instead of spawning) keeps the ghost at its end pose
   indefinitely, the existing missing-parts rendering skips the absent
   parts, and the row/tooltip says "needs parts X". A held ghost is inert by
   construction (no physics; the ghost-block guard family already covers
   every stock verb), so it cannot lure the player into interactions whose
   claims would inevitably fail the fit predicate against the true tip. Any
   older REAL instance retires when the tip advances (edge case 12). Shared
   infrastructure never vanishes; it becomes a visible, honest placeholder
   (edge case 37).

Clock skew between machines can misorder a photo-finish by seconds; the key is
still total and identical everywhere, which is the property that matters
(convergence over fairness; players coordinate socially). The rule can be
iterated later without schema changes.

### 7.9 The salvage ladder

Classification inputs are exchanged data: the continuation recording's start/
end crew and resource manifests, its vessel snapshot(s), the post-undock
recordings' snapshots, the trajectory points at the window boundaries, and the
claim window UTs. Concretely: part tree at entry = the continuation
recording's vessel snapshot (the docked-window recording for coupling, the
foreign-continuation root for Control); part tree at exit = the post-undock
recordings' snapshots (coupling) or the continuation's terminal snapshot
(Control); net crew = start/end crew manifests (both vessels for coupling);
trajectory at entry/exit = the target's recorded state at interactionStartUT
and interactionEndUT (orbit elements, or landed body-fixed position); resource
deltas = start/end resource manifests (informational only); window = the
claim's interactionStart/EndUT.

**Trajectory tolerance** (constant, centralized): an orbit counts as unchanged
when semi-major axis, eccentricity, inclination, and LAN each stay within the
same tolerances the existing loop-alignment / re-aim machinery already uses
for "same orbit" decisions; a landed position counts as unchanged within the
scene float-grid tolerance (`InGameFixtureMath.SceneFloatGridToleranceMeters`)
plus a docking-nudge allowance. Docking imparts real but tiny impulses; a
reboost or a plane change does not fit inside these tolerances, and that is
the point.

**The fit predicate** (referenced by the fold): a claim window [s,e] FITS iff
(a) [s,e] intersects no canonical continuation or accepted-visit window of the
target, AND (b) the target's canonical part tree, crew, and trajectory at UT
s (walking the canonical chain) equal, within tolerance, the target state the
claimant's recording captured at entry, AND (c) for a TIP-ADVANCING claim, no
already-accepted tip-advancing continuation (or Recover) of the target starts
after s. Transient claims are exempt from (c). Resources and inventory are
exempt from (b) (accepted slack). Clause (c) is what keeps share order
primary: a later-shared claim that happened EARLIER in UT than an accepted
structural link cannot splice itself in front of it and rewrite the state
that link was recorded against (edge case 47); it rejects and salvages like
any other loss. A window after a tip-advancing canonical continuation never
fits unless the claimant started from that continuation's exit state; a
window after only transient canonical visits fits.

- **Case 1 - splice (transient visit).** The interaction left the target as
  found (part trees, net crew, and trajectory identical at exit vs entry,
  within tolerance, on BOTH vessels) AND the fit predicate passes. The claim
  is ACCEPTED by the fold (this is the accepted-visit path of 7.8, listed
  here for completeness): the visitor's mission is fully canonical, the
  target gains a derived VisitAnnotation ("Docked: Tanker (Bogdan)"), the
  visitor's target-side post-undock recording is derived-demoted (never
  canonical on any machine; its bytes are untouched). Resource deltas are
  NEVER applied to the canonical target chain: fuel may appear from nothing,
  never vanish. A docked visit that reboosted the station is NOT transient:
  it is a tip-advancing claim and, if accepted, advances the tip to the
  visitor's target-side post-undock recording.
- **Case 2 - transient, fit fails.** v1 verdict: truncate (as Case 3). The
  designed upgrade (v1.1, out of scope for the first ship):
  excise-and-stitch - retire only the docked-window recording and re-link the
  pre-dock segment to the structurally-identical post-undock segment with a
  synthetic continuation link, preserving the rest of the mission. The
  dock/undock segmentation already provides the clean cut points; the
  synthetic link is the one new mechanism, hence deferred.
- **Case 3 - tip-advancing, rejected** (structure, crew, or trajectory
  changed, and the target was not the canonical tip in the recorded state).
  Truncate. For a Control claim (foreign-continuation tree) truncation
  degenerates: there is no pre-control segment of the claimant's own to keep
  (the vessel flown was the peer's), so the whole continuation tree is
  retired, its ledger rows tombstoned, a Truncation retirement exported, and
  the claimant's local instance of the target reverts to the canonical tip
  per edge case 12. Anything the claimant's OWN vessels did in that session
  (their tug's flight to the rendezvous, for example) lives in their own
  trees and is untouched.

**Truncation procedure** (executes ONLY on the claimant's machine, on the
claimant's own data; every other machine holds the derived mask until the
confirming packet arrives). Expressed entirely in existing schema vocabulary
so peers can apply it durably:

1. The pre-dock recording survives under a NEW recording id (content
   immutability of exported ids, 6.6), with: `ChildBranchPointId` cleared
   (the `RemoveEmptyBranchPoints` mechanism), terminal state recomputed from
   its stored tail (the `InferTerminalStateFromTrajectory` +
   `PopulateTerminalOrbitFromLastSegment` / `PopulateTerminalPositionFromLastPoint`
   pure cores; new wiring, since today only the finalize path calls them and
   the pre-dock parent is closed `TerminalState.Docked`, which is NOT
   spawnable), and a vessel snapshot: the dock-time self-snapshot stamped at
   merge time (7.7), with the segment-start snapshot as fallback for
   recordings from before that stamp existed (structurally identical by the
   branch invariant - part changes always create branch points - with stale
   resources as accepted slack).
2. The docked-window recording, the post-undock recordings, their branch
   points, and the dock branch point are retired.
3. The tail's ledger rows are tombstoned (existing eligibility rules).
4. A Truncation retirement is exported: supersede rows (original pre-dock id
   -> new head id), retired ids, and the withdrawn claim's continuation id.
5. In practice the recomputed terminal is Orbiting or Landed, so the loser's
   vessel parks as a spawnable terminal next to the target for a later redo
   via the normal own-vessel Fly/Switch-To continuation. A non-spawnable cut
   state (edge case 11) keeps the history but parks nothing.
6. The loser's LOCAL live instance of the target (which physically carries
   the losing interaction's effects: the attached module, the missing fuel)
   is retired per edge case 12 (despawn, deferred while loaded), and the
   canonical tip spawns when reached. The loser's parked vessel spawns from
   the new head.

Board conflicts follow the same shape; the pre-board parent closes
`TerminalState.Boarded` (also not spawnable) and gets the same terminal
recompute; a truncated board may leave an EVA-kerbal recording as the
surviving head (edge case 26).

All verdicts produce: one ScreenMessage naming the vessel, the winner, and the
case applied; an Info log line with full ids; a persistent flag on affected
Recordings Manager rows.

### 7.10 Retirement propagation (own rewind / supersede / discard / truncation)

Own destructive verbs remain fully available on shared data. Each exports a
retirement record: supersede relations (Old -> New ids) for re-fly and
truncation, retirement ids for rewind-retired subtrees, plain ids for discards
of previously exported data. Importers append the supersede rows (the
append-only supersede walk and ERS filtering then hide the retired data
everywhere - see the section 5 row on the `EffectiveState` cross-tree TODO,
whose proposed halt is rejected), tombstone the associated ledger rows per the
existing eligibility rules, feed withdrawals into the fold, and handle any
LOCAL recordings that depended on a retired tip in two ways:

- **Transient dependents** (accepted visits, dock links with no structural
  effect): kept, visible, `OrphanedByRetiredRecordingId` set, marked
  "depends on a superseded mission (Ana re-flew Harbor)", their ledger
  effects tombstoned - the route-revalidation pattern
  (`MissingSourceRecording`) generalized.
- **Coupling dependents** (a Dock/Board/Claw continuation that left a module
  attached, or a Control continuation): the dependent's own vessel would
  otherwise exist NOWHERE (it lives only inside a merged recording that can
  never spawn), which is deletion of recorded state by another route
  (principle 6). So the dependent's owner's machine runs the 7.9 truncation
  procedure at the coupling boundary: the pre-coupling segment survives
  under a new head id with a recomputed terminal and the dock-time snapshot,
  parked at its pre-coupling state; the docked-window and post-undock
  recordings retire; the truncation retirement ships. The orphan flag stays
  on the retired ids for history. The player keeps the lab they paid for,
  in orbit where the old station was, and can redock it to the new tip
  (edge case 46).

Retired foreign recordings' files are NOT deleted locally (history stays
browsable; disk cost accepted for v1; checkpoints bound the shared-folder
cost).

### 7.11 The economy contract

Foundations (all deterministic, all walk-level):

1. **Total order**: sort key (UT, earning-first, Sequence, OwnerPlayerId,
   ActionId), with exported actions' ordering fields frozen (6.2). Identical
   derived state on every machine, always.
2. **Founder-only seeds**: packets never carry `*Initial` rows; checkpoint 0
   does. The importer strips stray seeds with a Warn.
3. **Once-ever same-target spends**: walk-level keyed sets (the
   milestones/contracts pattern) for ScienceSpending by nodeId,
   FacilityUpgrade by (facilityId, toLevel), StrategyActivate setup costs by
   strategyId, KerbalHire by kerbalName. First-in-order is effective and
   charged; duplicates are ineffective and UNCHARGED (also fixes the latent
   single-player double-charge on duplicate rows). Idempotent application
   (tech unlock HashSet, absolute facility level) is unchanged.
4. **Cutoff walks only**: campaign-linked recalcs always use the current-UT
   cutoff variant (peers' future rows exist almost always; the uncut walk
   would patch future earnings into current pools and drag the contract
   PrePass deadline horizon to the latest peer row).
5. **Authoritative merges**: merge-triggered recalcs set the authoritative-
   reduction flag so `ApplyDrawdownGuard` does not floor a legitimate merged
   balance drop.
6. **Pinned baseline**: campaign-linked patching derives from the checkpoint
   baseline + the full walk (7.5), never from post-link local baselines.

Policies:

- **Debt, not cancellation.** Genuine concurrent overspends stand; the running
  balance goes negative; the KSP patch clamps the displayed pool at 0 (existing
  behavior); future earnings fill the hole before money shows again. A one-shot
  warning at merge names the colliding spends and the depth of the debt. No
  purchase is ever retroactively cancelled (paradox avoidance).
- **Shared reservation, attributed.** This is the existing single-player
  reservation design, not a new rule, but across free-running clocks it
  bites: `availableFunds` (and science) at your UT is the MINIMUM projected
  balance through EVERY member's committed future rows, so a member far
  ahead in UT who has spent down the pool pins the budget of members behind.
  The stock currency overlay's "Reserved" line names the largest future
  spender and UT ("reserved: 120,000 by Ana at Y2 D250"), the Settings
  member list repeats it, and the join notice (edge case 31) explains it.
  Not a defect: the money IS committed; the fix is attribution.
- **Recovery earnings are once-ever per recovered vessel** (7.7): the
  winning Recover claimant's row is effective, any other recoverer's row is
  fold-masked.
- **Earlier UT wins credit.** Milestone/first races across players resolve by
  the existing once-ever UT-order walk - identical to how retroactive commits
  shift credit today. Deep-warping ahead cannot lock in a first; credit (never
  the total) can shift at a later merge, with a notification when it does.
- **Science and reputation** pool with no special handling (per-subject caps
  and once-ever milestones already sum/dedup correctly).
- **Contracts are local-only in v1**: contract accept/complete/fail rows are
  excluded from packets; funds/rep from a player's contracts still enter the
  shared pool as that player's plain earning rows. Each member's contract
  queue is independently rolled (7.3 step 4). Canonical cross-save contract
  identity is the v2 problem (section 10).
- **Routes are owner-local**: routes execute only in the owner's game; their
  ledger rows share as the owner's actions; foreign route rows import as inert
  history (`RouteModule` is already observe-only). Route ENDPOINTS must be
  own-owned vessels in v1: a foreign spawn is not a valid endpoint (route
  cargo math would mutate a peer's vessel per cycle with no recording, no
  claim, and no arbitration), refused at route creation with the owner
  named - and RE-CHECKED after every fold: a route whose origin or stop
  endpoint resolves to a vessel whose canonical tip is now foreign-owned (a
  peer took control of your depot and moved it) pauses with a new
  `RouteStatus.EndpointForeign` (binds the tree, ghost-driving off),
  revalidated each merge, resuming only when the endpoint's tip returns to
  the owner through the owner's own accepted continuation (edge case 53).

### 7.12 Timeline and attribution

**Attribution is shown only when there is someone to attribute to.** One
predicate, `ShowOwnerAttribution`, gates every owner-name surface: true iff
the save is campaign-linked AND the campaign has at least two DISTINCT
registered player ids (counted from the `players/<id>/` manifests seen at the
last merge, plus the local player). A solo save (no campaign) and a
one-member campaign show no names anywhere: the timeline, the Recordings
Manager, the Missions tab, and the Kerbals window look exactly as they do in
single-player Parsek. The first time a second member's manifest arrives, the
names appear; they never disappear afterwards (a departed member's history
still needs its author).

When shown: every attributed entry (recordings and recording-scoped actions)
resolves the owner's display name through the campaign member list;
KSC/system actions with `RecordingId == null` use the action's own
`OwnerPlayerId`. Display: name appended to the entry text ("Launch: Tanker
from Launch Pad on Kerbin - Bogdan"); existing semantic colors unchanged (a
per-player color scheme would fight the earning/spending/action color
language - deferred). A per-player filter joins the existing source filter
row (also gated). Fold-derived visit annotations render as attributed
timeline entries on the target. The timeline's NOW
divider carries the shared balance text when the walk is in debt ("shared
funds in debt: 12,000"), since KSP's own display clamps at 0 and would
otherwise hide the fact.

### 7.13 Checkpoints

The founder can create checkpoint N at any time (Settings button): same copy +
strip procedure as 7.2 steps 3-5, plus `highwater.cfg` recording the highest
seq per player INCLUDED in the checkpointed state (the founder's own merge
state at that moment; the checkpoint therefore requires the founder's pending
set to be empty and all known packets applied, else refused with the reason).
Joiners always bootstrap from the latest complete checkpoint and import only
packets above its marks. Packets at or below EVERY member's checkpointed mark
are safely prunable by hand; the importer treats a gap BELOW the latest
checkpoint's marks as normal, and a gap ABOVE them as edge case 6 (halt that
player's imports at the gap, loudly). Checkpoints bound both the joiner
bootstrap cost and the folder's growth.

---

## 8. Edge Cases

1. **Clock skew photo-finish.** Two claims on one tip within seconds, machines'
   clocks skewed. The fold's key is total and identical everywhere, so the
   verdict may be "unfair" but is convergent. Accepted; iterate later if it
   bites (17.2).
2. **Half-synced packet.** Envelope present, payload file missing or hash
   mismatch (sync mid-flight). Importer skips with Warn "incomplete, will
   retry". Escalation distinguishes cloud placeholders (edge 24) from real
   corruption; never partially applies.
3. **Payload without envelope.** Ignored silently (normal write-order state).
4. **Sync service conflict copies.** Files with sync-client suffixes
   (`(1)`, `(conflicted copy)`) can only appear if the single-writer rule was
   violated or a bridge machine copied between services. Importer ignores
   non-protocol filenames and Warn-names recognized conflict-copy patterns
   specifically, once per session, pointing at the one-service-per-campaign
   requirement.
5. **Same player id on two machines.** Both write `players/<id>/`: duplicate
   (playerId, seq) with differing packetIds. The importer refuses the
   DUPLICATES (both), Errors naming the cause, and keeps applying already-
   journaled packets (an already-applied seq is never retroactively refused,
   so one player's mishap cannot poison history peers already hold).
   Prevention: per-machine identity cache; recovery: edge 27.
6. **Gap in a player's seq above the latest checkpoint.** Packet 12 present,
   11 missing. Importer applies nothing past the gap for that player, Warns
   with the missing seq; resolves when the file syncs or the founder
   checkpoints past it. Gaps below the latest checkpoint's marks are normal
   pruning (7.13).
7. **Schema generation drift.** A peer updates Parsek first; their packets
   carry generation 5 vs local 4. Hard refuse with the exact reason (existing
   gate policy, no migration); the merge continues for compatible players.
   The manifest pin is the gate at JOIN time; per-packet gates are
   authoritative afterwards (the manifest then serves as display).
8. **Missing parts.** Foreign snapshot references parts absent locally: ghost
   renders degraded (skipped parts), row marked, spawn-gate reject with
   reason, interaction impossible (never spawns). Settings shows the
   aggregated missing-part list per peer.
9. **Two non-structural visits to one tip.** Neither advances the tip (7.8):
   both are accepted visits when their windows do not intersect and both fit;
   if the windows intersect, the later-shared claim fails clause (a) and
   truncates (v1) - with the v1.1 stitch it survives minus the visit.
10. **Loser already recovered the mission.** Truncation retires the recovery:
    post-cut ledger rows (recovery funds, science, XP) tombstoned; live-world
    roster/kerbal state reconciles through the existing reservation walk (the
    kerbals are canonically aboard the parked truncated vessel; recalc derives
    reservation accordingly). "Kerbal visible in Astronaut Complex but
    canonically aboard" is exactly the state the reservation system already
    models for committed recordings.
11. **Truncation cut lands in a non-spawnable situation.** Interaction during
    atmospheric flight (rare: claw grab mid-air). The truncated head keeps its
    history; the recomputed terminal is not spawnable; no parked vessel, redo
    starts from scratch. Notified explicitly.
12. **Stale local spawn after a tip advance arrives.** The old tip's spawned
    vessel exists locally; the winner's continuation retires it. If unloaded:
    despawned immediately (no recovery ledger rows). If loaded/nearby:
    deferred to scene change (7.5). If the retiring instance is COUPLED to
    the player's active vessel (the tanker is still docked to it), retirement
    defers until they separate, never at scene change (a despawn would take
    the active vessel's docked partner out from under it). If the local
    player meanwhile committed an interaction with the stale spawn, that is
    itself a tip claim and resolves through the fold - no special case.
13. **Import arrives mid-flight.** Walk runs, patch defers (existing
    machinery, lands at the next trigger); world-affecting applications defer
    per 7.5. The recorder never observes a mid-flight world edit.
14. **Debt exceeds all future earnings.** Balance stays negative indefinitely;
    display stays 0; every spend gate that consults the walk
    (`CanAffordScienceSpending`) refuses. Social problem by design; the
    warning names the debt each merge while it persists.
15. **Milestone credit flips after a deep-warp player merges.** Player at year
    3 held "First Mun Landing"; a peer lands at year 1 and shares. Credit
    shifts (existing retroactive behavior); notification names old and new
    holder; totals unchanged.
16. **Founder's pre-campaign history.** Rides checkpoint 0 with founder
    ownership and `Exported = true` (optimizer-frozen; its ids are a contract
    with joiners). Founder rewinding pre-campaign missions exports
    retirements like any other shared data.
17. **Two campaigns, one machine.** Each campaign is its own local save +
    linked folder; no cross-talk (campaign link is per-save). Same playerId
    may appear in both. A save that LEFT a campaign keeps its foreign-owned
    data read-only; creating a new campaign from it never re-stamps non-null
    owners (7.2 step 2).
18. **Shared folder offline / unmounted.** Exports queue (7.4), imports skip
    with a single Warn per session; play continues fully local. On return,
    the next merge catches up.
19. **Player leaves / is removed.** v1: leaving is social (stop syncing).
    Their contributed history remains in every save (append-only). No
    revocation mechanism.
20. **Ghost-vs-ghost visual overlap during an accepted visit window.** The
    target's own ghost and the visitor's merged-vessel ghost both render
    during [start..end]. v1 accepts the overlap (windows are short); the
    VisitAnnotation gives a later renderer pass the data to suppress the
    target's ghost during the window. Deferred polish (17.3).
21. **Crew and applicant collisions.** Clones regenerate applicant pools
    (7.3 step 4), so cross-save hire-name collisions are rare; if one occurs,
    once-ever KerbalHire dedup by name makes the second hire uncharged and
    the roster kerbal's owner is the earlier-shared hirer. The starting four
    are resolved by the crew claim rule (7.6).
22. **Packet id or recording id fails `ValidateRecordingId`.** Entire packet
    refused with Error naming the id (defense against hand-edited or corrupt
    packets feeding path construction).
23. **Founder machine dies mid-creation or mid-checkpoint.** `campaign.cfg` /
    `highwater.cfg` are written only after the copied state verifies, so a
    partial is an unjoinable/ignored directory; the founder retries.
24. **Cloud placeholder files (Drive/OneDrive Files-On-Demand).** Directory
    listings show dehydrated stubs; reading them hydrates over the network.
    Hash verification is chunked with a per-tick time budget and carry-over,
    so a large packet hydrates across polls without a main-thread freeze;
    detected placeholder attributes downgrade the staleness escalation to a
    Warn suggesting "mark the campaign folder 'always keep on this device'"
    instead of a corruption Error.
25. **Optimizer wants to merge an exported chain segment pair.** Blocked by
    the Exported freeze (6.4); logged at Verbose with the recording ids, so
    the cost (slightly less compact storage for shared recordings) is
    observable.
26. **Board-conflict truncation leaves an EVA head.** The surviving pre-board
    recording is an EVA kerbal; its recomputed terminal (Orbiting/Landed)
    parks the kerbal for pickup or continuation. If the recompute yields a
    non-spawnable state, edge 11 applies.
27. **Save deleted, player rejoins.** The join flow detects the existing
    `players/<id>/` subtree (playerId recovered from the machine cache or
    typed from another member's Settings display) and resumes: fresh clone
    from the latest checkpoint, own packets import like anyone else's, and
    `lastSeq` seeds from max(player.cfg, own packet scan) so seq never
    restarts. A rejoin under a NEW playerId is a new member by design: the
    old subtree's contributions stay attributed to the ghost identity
    forever; the docs say so.
28. **Winner retired after the loser truncated.** Ana's lab attach beat
    Bogdan; Bogdan truncated (withdrawal exported); Ana later rewinds the lab
    mission. The fold does NOT resurrect Bogdan's claim (withdrawals are
    permanent); Harbor's canonical chain reverts to the core terminal, and
    dependents of Ana's lab (if any) orphan-flag per 7.10. A fresh joiner
    replaying the whole log reaches the same state.
29. **Claim arrives before its target recording.** Carol's claim targets
    Bogdan's continuation while Bogdan's packet is still syncing. The claim
    pends (excluded from the fold, journaled), retried each merge; Carol's
    machine shows her interaction as "pending arbitration" rather than won,
    and her continuation chain is derived-masked on every machine until it
    resolves (7.8 step 3): it does not play and does not spawn, so Dana
    cannot dock to an undecided continuation and inherit its fate.
30. **Baseline capture after import.** A post-merge local baseline snapshots
    live state that includes foreign effects; under the campaign link it is
    never used for merged-facet patching (7.5 pinning), so a later foreign
    retirement re-derives cleanly from the pinned baseline + walk.

Player-perspective cases (a session walked as a player, not a developer):

31. **"The sky is empty after I joined."** A joiner's clock starts at the
    checkpoint's UT; every mission peers flew after that is in the joiner's
    FUTURE, so ghosts are dormant and nothing has spawned yet. By design
    (free-running UTs). Also: available funds may read 0 (the shared
    reservation projects through every member's future spends, 7.11), the
    starting four are the founder's, and the hire button may be grey (roster
    cap, 7.6). One join-time ScreenMessage + Settings text explains all of
    it: "Your clock is at the checkpoint (Y1 D12); members are up to 400
    days ahead; funds, tech, and crew reflect the campaign at YOUR clock.
    Use the timeline's Warp to time to catch up." Settings shows each
    member's clock so the joiner knows how far; the timeline shows the future
    entries dimmed beyond the NOW divider.
32. **"I clicked Terminate on Ana's old probe in the Tracking Station."**
    Same as terminating your own spawned committed vessel today: the local
    instance goes, the spawn-death check re-spawns it a few times, then it
    is abandoned locally (7.7). Ana's canonical probe is unaffected; nothing
    is shared. Recover, by contrast, IS shared: it ends the probe's canonical
    chain as Recovered for everyone via a Recover claim.
33. **"I switched to Bogdan's station and flew it to a higher orbit."**
    Allowed and recorded as YOUR continuation of his station (a
    foreign-continuation tree, 7.7): a tip-advancing Control claim at
    commit. If Bogdan shared a tip-advancing change first, your continuation
    is rejected: the tree is retired whole (Case 3, Control form), your
    local station reverts to the canonical tip, and your own tug's flight
    (a separate tree) is untouched. Boarding it with a kerbal and flying it
    is the same story via a Board claim (a truncated board parks your kerbal
    on EVA, edge 26). Settings and the Recordings Manager show "pending
    arbitration" until the fold has both packets.
33a. **"I took control of Ana's rover, drove it 200 m, and switched back."**
    A tip-advancing Control claim (landed position moved beyond tolerance).
    If you only turned the lights on and left: `SwitchSegmentNoOpClassifier`
    treats it as a no-op segment and auto-discards it; no claim.
33b. **"While docked for refueling I reboosted the station."** Not a transient
    visit: the trajectory clause fails, so the claim is tip-advancing; if
    accepted, the station's canonical tip moves to your target-side
    post-undock recording (the station on its new orbit).
34. **Quickload (F9) after a merge tick.** Within a session the in-memory
    store is the source of truth (existing `ParsekScenario` rule), so the
    imported recordings and actions survive the quickload; the quickloaded
    WORLD may lack foreign vessels spawned since the quicksave, and the
    existing spawn-death check re-spawns them. Nothing is lost.
35. **Revert to launch / VAB after a mid-flight merge.** Same as 34: imported
    data persists in memory, the reverted world re-derives spawns. A dock to
    a foreign spawn inside the reverted flight never commits, so no claim
    exists; the dangling local supersede stamp it left is cleared by
    reconciliation (7.8 step 6).
36. **"I copied my campaign save as a backup and played both."** Two saves,
    one playerId, one subtree: edge 5's duplicate-seq refusal on every peer.
    The identity cache records (campaignId -> saveSlug); linking or loading a
    SECOND save bound to the same campaign and player on one machine Warns
    on screen ("another save on this machine is linked to this campaign as
    you"). Backups are fine to keep, not to play.
37. **"Ana attached a modded module to my station and now I can't see my
    station."** The canonical tip needs parts you lack; 7.8 step 6 replaces
    the real instance with a held degraded ghost at the tip's terminal
    state, marked with the missing part names in the Recordings Manager row
    and the Settings summary. You can see it and target it but not dock to
    it (ghosts are inert), which is deliberate: a real stale instance would
    invite interactions whose claims can never fit the true tip. This locks
    out the OWNER too: if Dana docks a modded hab to Ana's Harbor, Ana cannot
    dock to her own station until she installs the pack or Dana undocks the
    hab and shares - which is why Validate/Join report part-set differences
    up front and the hint text says shared stations should use stock parts
    (7.1). Install the mod (or ask the modder to detach and share) and the
    next merge spawns it real again.
38. **"My Dropbox moved to another drive."** Relink via the Settings path
    field; accepted only if the campaignId matches (7.1).
39. **"Someone deleted the shared folder."** Every member's local save still
    holds everything imported so far. v1 recovery: the founder (or any
    member with the most complete merge) creates a NEW campaign from their
    save (new campaignId, their merged state becomes checkpoint 0 with the
    original owner tags preserved), and the others JOIN it fresh. Their
    local-only, never-exported missions cannot be carried into the new
    campaign in v1 (no "join with existing save" flow; deferred item D12).
    Documented loudly in the user guide.
40. **"I renamed my mission after sharing it."** The rename rides the next
    flush packet's MISSION_NAMES (7.4); peers see it on their next merge.
41. **"I set up a supply route to Bogdan's fuel depot."** Refused at route
    creation with the owner named (7.11); routes need own-owned endpoints.
42. **"We were both docked to the same station at the same time in our own
    games and both synced at once."** No special case: each dock is a claim;
    the fold orders them; one splices or advances, the other truncates (v1)
    or stitches (v1.1). Both players see the verdict on screen and in the
    Recordings Manager.
43. **"Can I loop Ana's mission in my game?"** Not in v1, and neither does
    Ana's own loop setting reach you: mission-level loop settings live in
    each save's `MissionStore` and no packet block carries them, while the
    recording-level loop fields do ride the TREES fragment but are IGNORED
    for foreign recordings. Foreign missions play their real run once and
    never loop locally (also the soft-cap reason: four members' loops would
    multiply ghosts). Watch mode on foreign ghosts works (read-only). Local
    view preferences on foreign missions are deferred item D10.
44. **"I was away for three months."** Incumbents import every packet above
    their journal (checkpoints only shortcut JOINERS), so the catch-up merge
    is long: chunked hashing keeps the game responsive, notifications batch
    per merge, and the fold runs once at the end of the batch. Settings shows
    progress ("importing 212 of 340 packets").
45. **"Bogdan's engineer died on his mission, then he rewound it."** The
    death's reputation penalty pooled at first merge; the rewind's
    retirement tombstones it and the kerbal returns to the roster on every
    machine (existing rewind semantics carried cross-machine by the
    retirement record).

Stress-test cases (fourteen imagined multi-player sessions, 2026-09-02):

46. **"Ana re-flew Harbor's launch; my lab was docked to the old Harbor."**
    Bogdan's accepted lab continuation depended on the retired core. It is a
    coupling dependent, so 7.10 salvages rather than orphan-flags: on
    Bogdan's machine the lab's pre-dock segment survives under a new head id
    (recomputed Orbiting terminal, dock-time snapshot), parked where the old
    station was; the docked window and post-undock recordings retire and
    ship as a truncation. His lab still exists, he still paid for it once,
    and he can redock it to Ana's new Harbor. Carol's fuel visit to the old
    station is a transient dependent: flagged and harmless.
47. **"I reboosted my own station; Bogdan had already moved it and shared
    first."** Ana's own continuation of her exported Harbor derives a
    `Control` claim (7.7) and loses to Bogdan's earlier-shared one; her
    continuation segments retire inside her own tree (the pre-continuation
    leaf is the head), the retirement withdraws her claim, and her local
    Harbor reverts to Bogdan's tip. Symmetric: Carol's truss docked EARLIER
    in UT than Bogdan's lab but shared LATER loses under fit clause (c),
    because the lab was recorded against a truss-less core; she parks and
    redocks. Both are told on screen which claim won and why (share order).
48. **"Ana recovered my capsule from her Tracking Station; I recovered it
    too, unsynced."** Two `Recover` claims on one target; the earlier-shared
    one wins; the other's recovery earning is fold-masked so the pool gains
    the capsule once; the stored terminal is never mutated on either machine
    (Recovered is derived). The rescue variant: Ana Switch-To's Bogdan's
    fuel-less lander, EVAs Bob, boards him into her ship, recovers. The crew
    gate does not fire at board (7.6 rescue carve-out); the lander's Control
    claim is crew-changing and tip-advancing; Bob's reservation bounds at
    Ana's recovery UT; Bob is back on everyone's roster, still Bogdan's.
49. **"Ana unlocked Advanced Rocketry at Y2 D10; I'm at Y1 D40 and R&D says
    it's already committed."** The live research block honors a foreign
    registration only once your clock reaches its UT (7.5 step 2). You
    research it now at Y1 D40; your earlier-UT row is the effective charged
    one, Ana's is refunded at the next merge with a credit-shift notice; the
    node is unlocked from Y1 D40 on for everyone.
50. **KSP crashes two seconds after the envelope is written.** The packet is
    in the folder, `player.cfg` says seq 14, the local save still says 13
    and `Exported=false`. Next launch: seq derives from the folder (6.3), so
    the re-export is 000015; it re-carries known recording ids (accepted
    idempotently, 6.6) and the SAME claim under the same
    `continuationRecordingId` (only the earliest copy contends, 7.8 step 1).
    Nothing is refused, nothing self-masks.
51. **Four players, Astronaut Complex level 1.** Everyone's crews
    materialize into everyone's roster; the stock cap would be full after
    one hire. The hire gate counts own plus unclaimed crew only (7.6), and
    the starting four belong to the founder (checkpoint 0 is the earliest
    share), so joiners hire from day one. Upgrade the Astronaut Complex early
    (pooled cost); Settings says so.
52. **"My stand-in Hanley is retired on my machine and a normal kerbal on
    Carol's."** Prevented by deterministic campaign stand-in naming plus the
    CREW block's `standInFor`/`chainDepth` (7.6): every machine mints the
    same name into the same chain slot, so reclaim/retire converge.
53. **"Carol took control of my fuel depot and reboosted it; my route kept
    running."** After the fold, the depot's canonical tip is Carol's; the
    route re-check (7.11) pauses the route as `EndpointForeign` and names
    her; it resumes when Bogdan's own accepted continuation makes the tip
    his again.
54. **"My Dropbox was unmounted Monday; I committed first and still lost."**
    The export queued; its envelope was written Wednesday; the arbitration
    timestamp is Wednesday (share order means share order). Acceptable v1
    limitation: the queued-export Warn and Settings text say so in advance,
    and the export log line carries the original commit time so the outcome
    is explainable.
55. **"After three months away the merge told me 14 verdicts in one
    message."** The batched ScreenMessage is a summary; the Settings "recent
    verdicts (last merge)" block (7.1) and the per-row flags carry the
    detail.

---

## 9. What Doesn't Change

- The single-player experience with no campaign linked: every new field is
  null/absent and every new code path is behind the campaign link. (The one
  deliberate solo-visible improvement: the once-ever spend dedup also fixes
  the latent solo double-charge on duplicate same-target rows, and the
  dock-time parent snapshot stamp records slightly richer data.)
- The recording pipeline: sampling, finalization, optimization semantics for
  un-exported local data, schema format/generation, sidecar layout.
- Spawn rules, warp-to-spawn, ghost playback, watch mode, LOD.
- The ledger's append-only nature, tombstone eligibility rules, patch targets,
  and the recalc trigger model (merge adds trigger sites and pins the cutoff
  variant under a campaign link; semantics inside the walk are additive).
- Rewind-to-Separation mechanics on own data, the merge journal, RP lifecycle.
- ERS/ELS routing discipline and the grep-audit gate (new import/fence/fold
  code routes through `EffectiveState` like everything else; fold masks are
  implemented AT the ERS layer, not around it).
- Supply-route execution, `RouteTreeGuard`, route ledger rows.
- `CleanOrphanFiles`, `LoadTimeSweep`, `PreParsekBackup` behavior (imported
  data is registered before they can see it as foreign).

## 10. Out of Scope (v1)

- Per-player economies, milestone racing, opponent packs (Phase 15).
- Contract pooling / canonical contract identity (v2; contracts are
  local-only per 7.11).
- Using the tree-replacing RESTORE path on a foreign tree (foreign control
  goes through the foreign-continuation route, 7.7; the restore path stays
  own-tree only, forever).
- Excise-and-stitch salvage (Case 2 upgrade; v1.1; see 7.9).
- Applying visit resource deltas to canonical chains (decided against, not
  deferred: fuel from nothing beats deleted state).
- Per-player timeline colors, avatars, chat, presence indicators.
- Revoking or garbage-collecting a departed player's contributions.
- Automatic checkpoint scheduling (v1 checkpoints are founder-manual).
- Any real-time or lockstep synchronization (permanently out, per roadmap).
- Gloops extraction and the `.gloop` format (decoupled: packets are
  Parsek-native full-fidelity bundles because citizens need ledger rows,
  interaction, and spawning that `.gloop` deliberately strips; the roadmap's
  "extraction is the Phase 14 prerequisite" note is superseded by this doc).

---

## 11. Backward Compatibility

- **Schema generation bumps to 5 (decided 2026-09-02).** The exchange layer
  is born on a fresh generation: `RecordingStore.CurrentRecordingSchemaGeneration`
  becomes 5 with the first data-model task (M2.1), so every recording and
  sidecar that carries the new fields (`OwnerPlayerId`, `Exported`,
  `OrphanedByRetiredRecordingId`, `ContinuesForeignRecordingId`) is
  generation-5 data. Per the standing rule there is NO migration path:
  generation-4 recordings are rejected on load (`generation-older`), exactly
  like every earlier bump. The mod is not public yet, so the cost is
  re-stamping the committed fixture saves (`Fixtures/C1Career`,
  `Fixtures/C2CareerPostFix`, the harness fixture saves; the derived
  `career-earned-pad` harness fixture is rebuilt by its builder script) in
  the same task. Cross-PLAYER compatibility is enforced at the exchange
  boundary on top of that: the packet gate hard-refuses any
  generation/format mismatch (existing `IsRecordingSchemaCompatible`
  policy), and the campaign manifest pins the values as the join gate.
- New-build saves opened on an old build: old builds ignore unknown ConfigNode
  values by construction; a campaign-linked save degrades to a normal solo
  save on an old build (no fence, no merge) - players on one campaign are
  expected to run the same Parsek version, and the packet gate enforces the
  data side of that expectation.
- Checkpoints are version-pinned at creation; a joiner on a newer generation
  is refused at join with the exact reason (no checkpoint migration - the
  founder re-checkpoints after everyone updates).

---

## 12. Performance Budget

| Operation | Budget | Justification |
|-----------|--------|---------------|
| Shared-folder poll (steady state, no new packets) | One directory listing + journal diff, < 5 ms, KSC/TS/Map scenes only, every 90 s | Listing envelope names and comparing to the journal; no file contents read |
| Merge with new packets | Proportional to packet payload; runs where a load already runs (load/scene/poll tick), never per-frame; hash verification chunked per 13 | Sidecar copies + one cutoff recalc per batch |
| Campaign recalc (cutoff walk) | ~2x an uncut walk (projection pass + real pass), event-driven, never per-frame | Existing machinery; peers-ahead makes the cutoff variant the campaign default |
| Arbitration fold | O(claims log claims) per merge batch; claims are rare (one per interaction) | Pure, no I/O |
| Ownership fence checks | One string null/equality per mutation call site | Negligible; no per-frame surface |
| Foreign ghosts | Same pipeline as local ghosts; soft caps and LOD govern flight cost | Marginal cost is in COUNT, budgeted below |

Steady-state scale (4 players, measured bases from the current codebase:
`.sfs` metadata ~1.5 KB/recording amortized at p50, richest fixture 18.2% of a
1 MB save; TS ghost protos spawn at 2/tick; the per-frame O(N) surfaces are
`ComputePlaybackFlags`'s array walk and the loop-unit signature hash):

| Scale | Expectation | Commitment before M2 ships |
|-------|-------------|----------------------------|
| ~200 recordings total (early campaign) | Indistinguishable from a large solo save | None |
| ~800 recordings | ~1.2 MB ParsekScenario node re-serialized per save; ledger up to ~10k rows full-replayed per event-driven recalc; TS proto population takes tens of seconds | Measure via the scale-parameterized fixture (15.5); acceptable as-is |
| ~2,000-3,000 recordings (4 x 200 missions with debris/chains) | 3-4.5 MB metadata per save write; 20-40k ledger rows per walk; hundreds of TS protos | Per-peer TS proto visibility cap + measured walk time gate; if either budget fails the fixture, the mitigation lands before M2, not after |

Fresh-join cost is bounded by checkpoints (7.13), not by campaign age. No new
per-frame work is introduced anywhere.

---

## 13. Error Recovery

| Failure | Recovery | Data loss |
|---------|----------|-----------|
| Export interrupted (crash/offline) | Payload dir without envelope is inert; retry queue re-exports whole packet next merge tick | None (commit is durable locally) |
| Shared-folder write hits a sync-client file lock | Safe-write retries with backoff (3 attempts), then queue | None |
| Import crash between sidecar copy and registration | Journal lacks the entry -> clean re-apply; orphan cleanup quarantines the half-applied sidecars on next load | None (packet is the source of truth) |
| Corrupt packet (bad hash, bad id, bad timestamp, structural-hint mismatch, truncated node) | Refuse whole packet, Error with file + reason; never partially applied | None locally; owner re-exports on request |
| Cloud-placeholder payload (dehydrated stub) | Chunked, time-budgeted hydration across polls; Warn suggests pinning the folder offline-available; no corruption Error for placeholders | None; slow |
| Shared folder disappears mid-session | Exports queue, imports skip; single Warn per session | None |
| Checkpoint copy fails at creation | Checkpoint not published (highwater/manifest written last); founder retries | None |
| Journal corrupt/deleted | Re-imports are idempotent (ids match, actions dedup by ActionId presence); journal rebuilds; pending set re-derives | None; one slow merge |
| Duplicate (playerId, seq) | Both duplicates refused, Error naming the two-machine cause; already-journaled packets stay applied | None applied |
| Claim/retirement referencing an unknown recording | Pends in the journal, excluded from the fold, retried; staleness escalation names the missing id | None; transient divergence until the referent syncs |

Default-safe principle throughout: when in doubt, refuse the packet and say
exactly why; the shared folder still holds it for a retry.

---

## 14. Diagnostic Logging

Format: `[Parsek][LEVEL][Subsystem] message`, per the standing convention.
Batch counters for per-item loops (one summary line per merge, per-item lines
only under ~20 items). Every line carries the relevant ids (campaignId short
form, playerId, packetId/seq, recordingId, UT).

### 14.1 Subsystem tags

| Tag | Owns |
|-----|------|
| `[Campaign]` | Create/join/leave, manifest and checkpoint lifecycle, membership |
| `[PacketExport]` | Export triggers (commit/retirement/flush), queue, retries, seq allocation |
| `[PacketImport]` | Scan, validation verdicts, apply batches, journal, pending set |
| `[Fold]` | Claim ordering, verdicts, withdrawals, pendings, masks, crew claims |
| `[Salvage]` | Truncation execution detail (new head id, cut UTs, tombstoned rows, terminal recompute) |
| `[MergeEconomy]` | Debt warnings, credit shifts, once-ever dedup hits, seed strips, reconcile-preservation and cutoff decisions |
| `[OwnershipFence]` | Blocked mutation attempts on foreign data (Warn, names the operation and owner) |
| `[ForeignRoster]` | Kerbal materialization, crew claims/substitutions, assignment-gate refusals |

### 14.2 Logged events

- **Info**: campaign created/joined/checkpointed (ids, folder, high-water);
  packet exported (seq, kind, tree, action count, claim count); merge summary
  (packets applied, recordings added, actions added, verdicts, pendings,
  per-player counts); fold verdict (case, vessel, winner, loser, cut UT);
  crew claim resolution (kerbal, owner, substituted recording); credit shift
  (milestone, old->new holder); debt onset/clearance (depth, colliding action
  ids); retirement applied (kind, id count, withdrawals, orphan flags set).
- **Warn**: incomplete packet (file, reason, retry count, placeholder
  detection); seq gap above checkpoint (player, missing seq); stray seed
  stripped; fence block (operation, recording, owner); missing parts (peer,
  part list, first occurrence per part); conflict-copy filename recognized;
  identity-on-two-machines detection; folder offline (once per session);
  pending-referent staleness.
- **Verbose**: per-packet validation detail; optimizer freeze skips; fold
  input census (claims known/pending/withdrawn); journal writes. Poll ticks
  with zero deltas are NOT logged (silence = nothing new).
- **Error**: hard packet refusals (schema, hash, id safety, timestamp,
  structural-hint mismatch, duplicate seq) with the exact file and reason;
  join refusals; creation-precondition refusals.

Goal, per the house rule: a developer reading KSP.log alone can reconstruct
every merge, every fold verdict, and why.

---

## 15. Test Plan

### 15.1 Unit tests (pure logic, headless)

- **SortActionsDeterministicAcrossConcatenationOrder** - two action sets with
  colliding (UT, Sequence), merged in both orders, walk output identical.
  Fails if the (OwnerPlayerId, ActionId) tiebreak regresses.
- **ExportedActionOrderingFieldsFrozen** - a rollout-adoption-style Sequence
  reassignment against an Exported action is refused/deferred; the sort key
  of an exported action never changes.
- **FoldGlobalOrderAndCascade** - claim sets in shuffled arrival orders
  produce identical verdicts; the A->B->C cascade (B loses, C targeted B)
  rejects C deterministically; a pending-target claim stays Pending.
- **FoldWithdrawalPermanence** - after a Truncation withdrawal, retiring the
  winning claim does NOT resurrect the withdrawn one; a full-log replay
  (fresh-joiner shape) matches the incremental result.
- **FitPredicate** - clause (a) window-intersection and clause (b) canonical-
  state-match fixtures: fuel-only visit against the recorded part tree
  (fits), visit after a structural continuation (state mismatch, fails),
  visit between two non-structural visits (fits), overlapping windows
  (fails).
- **NonStructuralWinnerDoesNotAdvanceTip** - accepted visit leaves the tip
  id unchanged; a later claim on that tip id is an ordinary next evaluation.
- **StructuralHintRecompute** - a packet whose claim's structural bool
  disagrees with its manifests is refused.
- **OnceEverSpendDedup** - duplicate nodeId/facility(id,toLevel)/strategy/hire
  rows: first charged+effective, second uncharged+ineffective; single-player
  duplicate double-charge regression covered.
- **SeedExclusion** - exporter never emits IsSeedType rows; importer strips a
  crafted seed-bearing packet with the Warn.
- **CampaignReconcilePreservesFuture** - a campaign-linked reconcile in the
  TRACKSTATION shape keeps future-UT spending/contract/untagged rows that
  the solo path prunes.
- **PacketEnvelopeRoundTrip** - envelope+payload write/read byte-stable;
  hash-mismatch, missing-file, and bad-timestamp refusal paths.
- **TimestampOrderingParseThenCompare** - mixed-precision timestamps order by
  ticks, not string; unparseable refuses.
- **OwnershipFenceMatrix** - every fenced operation vs (null, local, foreign)
  owner; exempt runtime-state writes and owner-packet tree replacement pass.
- **ExportedFreezeIncludesSnapshotHistory** - checkpoint-carried recordings
  (founder history) are optimizer-frozen exactly like packet-exported ones.
- **ImportJournalIdempotence** - re-applying an applied packet is a no-op;
  journal loss triggers clean re-apply; pending set re-derives.
- **SalvageTruncationCut** - truncation at a dock boundary yields a NEW head
  id with cleared child link, recomputed spawnable terminal (from the
  InferTerminalStateFromTrajectory core), the dock-time snapshot (or
  segment-start fallback), correct supersede rows, and the matching
  tombstone set.
- **CrewClaimByShareOrder** - two packets flying the same kerbal: earlier
  key owns; the loser's derived stand-in name is identical across machines.
- **DebtClampAndFill** - merged overdraft walk: running balance negative,
  patch target 0, next earning fills debt first.
- **CampaignNameSlug** - hostile campaign names (reserved device names,
  trailing dots, path-length overflow, case-only collisions) slug and
  uniquify safely.
- **SpawnOwnershipReconciliation** - after a fold: a stamp pointing at a
  masked/demoted/retired/unknown recording is cleared; a demoted
  continuation tracking a live pid re-points the canonical tip at that pid;
  an unspawnable tip yields a held degraded ghost, never a real spawn of an
  older link. Fails if a stale stamp survives, two links claim spawn for one
  vessel, or an older link spawns real.
- **WithdrawalByAnyRetirementKind** - a RewindRetire whose ids include a
  claim's continuation withdraws the claim exactly like a Truncation.
- **RouteEndpointOwnershipGate** - route creation against a foreign-owned
  endpoint refuses with the owner named; own endpoints pass.
- **TransientVisitRequiresUnchangedTrajectory** - a docked visit whose exit
  orbit differs from entry beyond tolerance classifies tip-advancing, not
  transient; a docking-nudge-sized delta stays transient.
- **ControlClaimDerivation** - a foreign-continuation tree exports one
  `Control` claim with the right window; a no-op segment exports none;
  a Recover exports a `Recover` claim with `advancesTip = true`.
- **ShowOwnerAttributionPredicate** - false for no campaign and for one
  registered id; true at two distinct ids; stays true when a member's
  manifest later disappears.
- **OwnContinuationDerivesClaim** - the restore path on an Exported own
  terminal exports a Control claim; on a never-exported terminal it does
  not.
- **FitClauseCKeepsShareOrder** - a later-shared, earlier-UT structural
  claim against a link with an accepted later structural continuation
  rejects; the same claim marked transient passes.
- **CoveringLinkAcceptance** - a transient visit whose window start falls
  inside the core link is accepted even though the current tip is a later
  continuation (the worked example's Case 1).
- **PendingClaimMasksContinuation** - a claim with an unknown target masks
  its continuation from ERS and the spawn gate; resolution lifts the mask.
- **DuplicateClaimKeyKeepsEarliest** - two claims sharing
  `continuationRecordingId` contend once, by the earliest key.
- **RecoverOnceEverByTargetRecording** - two Recover claims on one target:
  one effective recovery earning, the other's action id in the fold mask;
  no stored terminal mutation on an Exported recording.
- **ForeignFutureTechDoesNotBlockLocalResearch** - a foreign tech
  registration with UT > current UT does not block `TechResearchPatch`;
  with UT <= current UT it does.
- **OrphanedCouplingDependentIsSalvagedNotFlagged** - a retirement that
  removes the target of a structural dependent produces the truncation
  (new head, retirements), not a bare orphan flag; a transient dependent
  gets the flag.
- **CampaignStandInNamesConverge** - two machine contexts mint the same
  stand-in name for the same slot and depth.
- **HireCapCountsOwnAndUnclaimedOnly** - materialized foreign crew do not
  consume the local Astronaut Complex cap.
- **RouteEndpointRecheckAfterFold** - a fold that moves an endpoint's tip
  to a foreign owner pauses the route `EndpointForeign`; the owner's
  accepted continuation resumes it.

### 15.2 Integration tests (synthetic fixtures)

- **TwoPlayerLedgerMergeConverges** - two independently generated ledgers +
  recordings, merged on two simulated machines in opposite orders; full
  derived career state byte-identical. The regression: nondeterministic
  convergence.
- **JoinBootstrapEqualsIncumbentState** - checkpoint clone + packet import
  reproduces an incumbent's effective state exactly, including after a
  truncation-withdrawal history (fresh-join replay convergence).
- **TreeFragmentReconciliation** - packet 15 carrying an updated TREES
  fragment for a tree from packet 14 replaces topology without re-carrying
  recording payloads; a cross-owner fragment refuses.
- **ActionFlushPacket** - KSC-only economy work exports via the flush
  trigger and merges correctly with no TREES.
- **RefusedPacketLeavesNoTrace** - corrupt packet applied against a populated
  store; store, ledger, journal unchanged.
- **RetirementOrphanFlagging** - peer supersede retiring a tip with a local
  dependent visit; dependent kept, flagged via
  `OrphanedByRetiredRecordingId`, ledger effects tombstoned; ERS hides the
  retired chain (cross-tree supersede walk, halt-TODO rejected).
- **MilestoneStoreRegistrationFromEvents** - imported EVENTS bundles make
  `GetCommittedTechIds`/`GetCommittedKerbalHireNames` include foreign
  unlocks/hires (live-block visibility).
- **BaselinePinning** - a post-import local baseline is ignored for
  campaign-facet patching; retiring the foreign unlock re-locks the node on
  every machine.
- **ImportedPastRecordingPlaysAndSpawns** - an imported recording entirely in
  the local past is latched (not historical-never-replayed) and its terminal
  spawns.
- **MissingPartsImportDegrades** - packet with an unknown part name: imports,
  marked, spawn-gate rejects with the logged reason.

### 15.3 Log-assertion tests

- **MergeSummaryLineAlwaysEmitted** - every merge with >= 1 packet logs the
  `[PacketImport]` summary with counts (catches silent-merge regressions).
- **FenceBlockAlwaysWarns** - a blocked foreign mutation without its
  `[OwnershipFence]` Warn fails.
- **VerdictLineCarriesIds** - fold verdicts must log case + both player ids +
  cut UT; withdrawals and pendings must log their referents.

### 15.4 In-game tests (`InGameTests/`, new category `Multiplayer`)

- **CampaignCreateJoinRoundTrip** - create in a temp shared dir, join into a
  fresh save, assert converged funds/science/rep vs founder, and assert the
  clone's contract queue and applicant pool differ from the founder's.
- **ForeignSpawnControlContinues** - import a fixture packet with a
  terminal-orbit recording, warp past EndUT, Switch-To the spawned vessel,
  burn, switch back: assert a foreign-continuation tree exists with
  `ContinuesForeignRecordingId` set, the foreign committed tree is
  byte-unchanged, and the commit exports a `Control` claim; then repeat
  without burning and assert the no-op auto-discard leaves no tree and no
  claim.
- **AttributionHiddenUntilSecondMember** - a one-member campaign renders no
  owner names in the timeline / Recordings Manager / Kerbals window; a second
  member manifest makes them appear.
- **MergeWhileFlying** - packet drop during an active recording: walk runs,
  patch deferred, world untouched until scene change.

### 15.5 Synthetic recordings / fixtures

- **two-player-campaign** fixture (Generators/): founder save + two player
  packet sets including one tip-claim collision per ladder case, one
  claim-cascade, one withdrawal-after-winner-retirement, one crew claim
  race, one debt collision, one milestone race - the shared fixture behind
  15.2 and future harness scenarios. **Scale-parameterized** (recording
  count as a knob) so the section 12 budgets are measured, not assumed.
- Harness note: a future harness scenario family can drive two sequential
  "players" against one temp shared folder in one KSP instance (create,
  export, re-join as the second identity) - deferred to autotest planning,
  not part of this doc's commitments.

---

## 16. Implementation Phasing

| Phase | Scope | Notes |
|-------|-------|-------|
| M1 | Identity + campaign store (manifest, checkpoint 0, join/rejoin, ContractSystem/applicant regeneration) + Settings section | Ships inert without a campaign |
| M2 | Export/import of recordings as read-only citizens + ownership fence + journal + scope-tracker latching + MilestoneStore/crew registration + attribution | No interaction, no economy merge: purely additive world sharing; scale budgets measured here |
| M3 | Ledger exchange: deterministic sort + ordering freeze, seeds policy, once-ever dedup, debt, cutoff-walk mandate, reconcile preservation, baseline pinning, authoritative merges, credit notifications, action flush | The pooled economy |
| M4 | Foreign spawns + full control (foreign-continuation route, Recover claims) + coupling + dock-time parent snapshot stamp + tip claims + the arbitration fold + salvage ladder (Cases 1 and 3; Case 2 = truncate) + retirement propagation + derived masks | The co-op world |
| M5 | Crew claim rule + derived substitution + assignment gate | |
| M6 | Polish: founder checkpoints N>0, missing-parts UX, cloud-placeholder handling, poll tuning, notifications batching, per-player timeline filter | |
| v1.1 | Excise-and-stitch (Case 2 upgrade) | Designed here, built later |

Ground-up per the maintainer's direction: each phase is independently playable
and testable; M2 alone already delivers "see your friends' missions".

The task-level breakdown (per-phase tasks with files, tests, dependencies,
and done conditions), the codebase inventory seeded from the verified
mechanics, and the deferred-items list live in `docs/dev/plans/`:
`coop-async-multiplayer-tasks.md`, `coop-async-multiplayer-inventory.md`,
`coop-async-multiplayer-deferred.md`. Per-task implementation plans
(`coop-async-multiplayer-task-N-<component>.md`) are written by clean-context
Plan agents at dispatch time, per `docs/dev/development-workflow.md` step 4a.

---

## 17. Open Questions

### 17.1 (resolved) Schema generation bump
Decided 2026-09-02: bump to 5 (section 11).

### 17.2 Arbitration clock hardening
(timestamp, playerId) is v1. If skew-unfairness bites in practice, candidates:
NTP-style offset sampling against peers' packet timestamps, or a per-tip
first-writer sentinel file (single-writer per claimant, still no shared
writes). Iterate on evidence; the fold's key can change without schema
changes.

### 17.3 Visit-window ghost overlap suppression
Edge case 20: whether/when to spend a renderer pass consuming VisitAnnotations
to hide the target ghost during accepted visit windows.

### 17.4 Poll interval and scene set
90 s in KSC/TS/Map is a starting point; tune against real sync-service
latencies (Dropbox delivers in seconds; Drive can lag minutes) and the
chunked-hydration budget.

### 17.5 Stand-in attribute fidelity
The CREW block carries name/trait/gender/veteran. Whether to also carry
courage/stupidity/XP for exact cross-machine roster parity, or accept
cosmetic divergence on derived stand-ins, is an M5 call.

---

## 18. Code Layout / Implementation Map (proposed)

| Concept (this doc) | Code |
|--------------------|------|
| Player identity, campaign link | `Source/Parsek/Multiplayer/PlayerIdentity.cs`, `CampaignLink` state in `ParsekScenario` |
| Campaign manifest/checkpoint lifecycle | `Source/Parsek/Multiplayer/CampaignStore.cs` |
| Packet envelope/payload codec + timestamp/id validation | `Source/Parsek/Multiplayer/PacketCodec.cs` (pure core) |
| Export queue + triggers (commit / retirement / action flush) | `Source/Parsek/Multiplayer/PacketExporter.cs` |
| Import pipeline + journal + pending set | `Source/Parsek/Multiplayer/PacketImporter.cs`, `ImportJournal.cs` |
| Arbitration fold (claims, verdicts, withdrawals, masks, crew claims) | `Source/Parsek/Multiplayer/ArbitrationFold.cs` (pure core, unit-tested) |
| Fit predicate + conflict classification | `Source/Parsek/Multiplayer/ConflictClassifier.cs` (pure) |
| Salvage execution | `Source/Parsek/Multiplayer/SalvageExecutor.cs` (reuses `RecordingOptimizer.SplitAtUT`-family cuts, `InferTerminalStateFromTrajectory` cores, tombstone family) |
| Ownership fence | `Source/Parsek/Multiplayer/OwnershipFence.cs` (pure predicate) + call-site guards |
| Foreign roster + crew claims | extensions in `KerbalsModule.cs` / `CrewReservationManager.cs`; materialization extending the `VesselSpawner.EnsureCrewExistInRoster` mechanism |
| Economy foundations | `RecalculationEngine.SortActions`, keyed sets in the four spend modules, `LedgerOrchestrator` merge trigger + cutoff mandate, `Ledger.Reconcile` preservation flag, `KspStatePatcher` baseline pinning |
| Foreign-continuation route (taking control of a foreign spawn) | fourth branch in `ParsekFlight.TryConsumeStockActionIntent` + `SwitchSegmentBuilder`; no-op discard via `SwitchSegmentNoOpClassifier` |
| Recover claim | recovery hook (FlightResults / TS recover path) -> `PacketExporter` flush |
| Attribution gate | `Multiplayer/OwnerAttribution.cs` (`ShowOwnerAttribution`, pure) consumed by `TimelineBuilder`, `RecordingsTableUI`, `MissionsWindowUI`, `KerbalsWindowUI` |
| Dock-time parent snapshot stamp | `ParsekFlight.CreateMergeBranch` |
| Settings section | `UI/SettingsWindowUI.cs` |
| Attribution | `TimelineBuilder` + timeline window filter row |

Pure decision cores (fold, classifier, fence, codec validation) follow the
house pure-core + thin-shell pattern for headless testability.

---

## References / Related Docs

- `docs/roadmap.md` Phase 14 (superseded in part: Gloops prerequisite removed,
  additive-ghost-only import model replaced by full citizenship; the
  correspondence-chess framing and player-identity goals carry forward).
- `docs/parsek-game-actions-and-resources-recorder-design.md` - the ledger this
  design merges.
- `docs/dev/design-mission-crosstree-dock.md` - the derived-link precedent for
  cross-tree interaction without foreign-side writes.
- `docs/parsek-rewind-to-separation-design.md` - supersede/tombstone machinery
  reused by retirements and salvage.
- `docs/dev/dock-undock-recording-structure.md` - the dock episode structure
  the salvage ladder cuts along.
- `docs/dev/plans/coop-async-multiplayer-tasks.md` - phase/task breakdown.
- `docs/dev/plans/coop-async-multiplayer-inventory.md` - verified-mechanics
  codebase inventory (baseline recorded at kickoff).
- `docs/dev/plans/coop-async-multiplayer-deferred.md` - deferred items D1+.
- Interview record: decisions taken 2026-09-01 with the maintainer (sync model,
  concurrency, time model, crew, citizenship, contracts, interaction model,
  salvage ladder, economy contract, cadence, credit rule, Gloops decoupling,
  mod-set tolerance, campaign flow, pooling defaults). Revised same day after
  two adversarial design reviews and three code-mechanics verifications
  (arbitration-fold reframe; truncation procedure grounded in the verified
  dock-episode structure; future-UT reconcile/cutoff/baseline/milestone-store
  corrections; import-seam registration procedure; checkpoints; contract-queue
  regeneration; crew claim rule).
