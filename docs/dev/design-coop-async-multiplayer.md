# Parsek Cooperative Async Multiplayer - Design Document

*Design specification for shared-folder cooperative async multiplayer: multiple players contribute missions to one shared career timeline.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies how multiple players, each in their own KSP instance, share one cooperative career: one KSC, one pooled economy, one timeline, exchanged asynchronously through a file-sharing folder (Dropbox, Google Drive, or any service of the players' choice).*

**Status:** DESIGN v5 (pre-implementation; interview complete 2026-09-01; adversarial review + code verification folded in 2026-09-01; player-perspective edge-case pass, spawn-ownership reconciliation, and implementation task breakdown added 2026-09-01; 2026-09-02: full control of foreign vessels (Fly/Switch-To/Recover as claims via a foreign-continuation route, replacing the earlier Fly block), trajectory-aware tip-advance predicate, attribution shown only with 2+ registered players; 2026-09-02 gameplay stress test (14 multi-player sessions): own continuations and recoveries of exported terminals are claims too, covering-link acceptance + fit clause (c), pending-claim masking, orphaned coupling dependents salvaged not deleted, UT-gated live blocks for foreign tech/hires, seq allocation from the folder, deterministic campaign stand-ins, rescue carve-out, route endpoint re-check, shared reservation attribution - see `docs/dev/plans/coop-async-multiplayer-tasks.md`). DESIGN v6 (2026-10-24): freshness pass against the single-player rules built since v5 - see "Revision 2026-10-24" below. DESIGN v7 (2026-10-24, same day): owner rulings on open questions 17.6 to 17.8 - a losing flight's rows are trimmed, contested earnings are held until final, co-op runs only with an allowlisted mod set, and co-op is built mode by mode: Sandbox (v1), then Science (v2), then Career (v3), with no implementation before base Parsek is release-ready - see "Owner rulings 2026-10-24" below.)
**Version:** roadmap Phase 14, built mode by mode (owner ruling 2026-10-24, see "Game modes" below): v1 = Sandbox, v2 = Science, v3 = Career. Not implemented before base Parsek is release-ready.
**Out of scope:** the science pool in v1 (v2) and funds, reputation, contracts, facility upgrades, hires and strategies before v3; competitive play and per-player economies (Phase 15, `docs/roadmap.md`), real-time synchronization (permanently out per roadmap), Gloops extraction (decoupled from this feature; see section 10), contract pooling (v3 or later; see 7.11 and section 10), excise-and-stitch conflict salvage (v1.1 upgrade; see 7.9 Case 2), any exchange of supply routes (routes carry no money and their outcome is never committed; 7.11).
**Related docs:** `docs/parsek-architecture.md`, `docs/parsek-game-actions-and-resources-recorder-design.md` (section 3.13, the committed-value rule), `docs/parsek-timeline-design.md`, `docs/parsek-rewind-to-separation-design.md` (section 2.12, Re-Fly ledger safety), `docs/dev/design-load-restores-the-save.md`, `docs/parsek-logistics-supply-routes-design.md` (section 10.6), `docs/dev/design-mission-crosstree-dock.md`, `docs/dev/dock-undock-recording-structure.md`, `docs/roadmap.md` (Phase 14).

### Revision 2026-10-24 (design v6)

v5 was written on 2026-09-02. Since then the single-player rules this design
composes with were ruled and built (0.10.6, PRs #2151 through #2193). None of
them changes the exchange model (packets, the fold, the salvage ladder); they
change what a merge, a verdict and a load may do to a player's career. This
pass applies them (two of its defaults, "verdicts move vessels, never value"
and "both recoveries are paid", were reversed the same day by the owner
rulings in the next section):

- **Ledger safety** (owner rulings 2026-10-19, rewind design 2.12; committed-value
  rule, game-actions design 3.13, ruled 2026-10-21, built in PRs #2172 to #2180):
  nothing may take back value a player earned. v5 retired a losing claim's rows
  through fold masks and truncation tombstones; v6 does not. A verdict moves
  vessels, never value (new section 7.10.1). The fold's ledger mask is gone, so
  the tombstone check stays the only ELS filter. A Re-Fly retirement can only
  raise an importer's balances, because its retire set holds losses alone.
- **The economy speaks the 3.13 rule**: concurrent purchases make a merge debt,
  any other loss stops at the reserved floor, and with no committed cost ahead a
  loss applies as stock applies it. "Captured before", which decides the floor,
  becomes "shared before" across players (7.11 foundation 7). A merge steps the
  stock pool reference, so peers' value is never filed as a leak. Clicks read
  the click reservation and the committed-future index, not the milestone store.
  Part and part-upgrade purchases join the once-ever keys. Recoveries leave
  them: two players who recover one vessel before syncing are both paid.
- **Load restores the save** (`design-load-restores-the-save.md`, PRs #2155,
  #2164, #2169, #2170): the import journal and the export queue move into the
  save, so a load rolls them back with everything else. Anything already shared
  comes back at the next merge, because a packet is permanent (new section
  7.5.1, open question 17.6). Edge cases 34 and 35 are rewritten.
- **Quarantine grace** (PR #2183, GC-15 in PR #2192): unnamed recording files wait
  14 days before deletion. Imported sidecars are named by the save like any
  others, and a re-import after a load finds them again.
- **Logistics has no money** (PRs #2161 to #2168, logistics design 10.6): routes
  write no ledger rows and their outcome is never committed, so nothing about a
  route is exported. The endpoint rules stay.
- **Holds and gates now built in single player** (#2181 rewind vessel hold, GC-2
  / GC-9 claimed-vessel disposal, GC-12 tourists, GC-13 alarm Jump, Re-Fly switch
  block): foreign committed recordings feed them exactly like own ones (7.6, 7.7).
- **Re-Fly seals a slot a peer has claimed** (new clause in 7.10.1), and a merge
  waits while a Re-Fly session or a merge journal is open (7.5).

Choices this pass made on the owner's behalf were recorded as open questions
17.6 to 17.8; the owner ruled on all three the same day (next section).

### Owner rulings 2026-10-24 (design v7)

The owner was interviewed on 17.6 to 17.8 and ruled. Background analysis
(how Git, Mercurial phases, bank holds, sagas and blockchains handle value
built on history that may be rewritten):
`/mnt/project-files/analysis/coop-trim-ledger-conflict-2026-10-24.md`
(project files, outside the repo).

- **17.6, "Shared stays shared."** A load never un-shares an exported packet;
  the next merge imports it back (7.5.1, unchanged from v6).
- **17.7, a trimmed flight's rows are trimmed too.** v6 kept a losing
  flight's ledger rows so that no verdict took value back. The owner
  rejected that: "if we resolve the race condition by taking parallel
  timeline A instead of B, and we trim B at the split point.. that means we
  should trim all ledger-related recorded actions also; otherwise we open the
  door for paradoxes". His single-player observation ("purchases can only be
  done after exiting flight mode and going to the space center.. which means
  the recording is either merged or discarded or trimmed already") is Git's
  rule: rewrite only history nobody has built on. In a campaign a race can
  only be decided after the losing flight's merge (an earlier-shared packet
  may still be on its way), so the campaign form of the rule is "settle
  before the spend": **earnings a lost race could trim are held, visible but
  not spendable, until the race is final, then released at their original
  UT or trimmed** (owner pick: "After everyone syncs past it"). Nothing
  spendable is ever taken back, and nothing is built on value that can
  vanish. New section 7.10.2; 7.9, 7.10, 7.10.1 and edge cases 10, 28, 33,
  46, 48 change with it.
- **17.8, co-op runs only with an allowlisted mod set.** Money that moves
  outside the ledger is a mod-compatibility problem, and in a shared career a
  mod that pays one player breaks everyone's. A campaign links only when
  every member runs the same set of mods and every mod in it is on Parsek's
  verified co-op allowlist (owner pick: allowlist, not blocklist). Single
  player keeps today's flexibility. A `StockPoolAdjustment` row that still
  appears in a linked save is then a Parsek gap, not a mod: it is shared as a
  ledger row like any other, logged, and announced once on screen (7.11
  foundation 8).
- **Away members (17.9).** Any member may mark a silent member away, so one
  absent player cannot hold everyone's contested earnings forever (7.10.2
  rule 6).
- **Game modes: Sandbox first, then Science, then Career.** The owner, after
  the rulings above: "all these ledger problems make me think that career
  mode might not be good for multiplayer because of its global resources and
  maybe science mode is the best place to actually build and test
  multiplayer first", then: "we will build multiplayer co-op for sandbox
  first, then add complexity for science mode later; just write it down, but
  we won't start implementing until after i get parsek base working". So v1
  is Sandbox only: no shared pool, the test bed for the exchange, the fold,
  the salvage ladder and crew on their own. v2 adds Science saves: one shared
  pool, science, spent on tech, with no funds, reputation, contracts,
  facility upgrades (stock has them all built; VERIFY at v2 planning), hire
  costs or strategies, so merge debt in funds, contract penalties and funds
  payouts do not arise. v3 adds Career. A save of a mode not yet supported
  cannot found or join a campaign (7.2). The ledger and career rules in this
  document stay as the v2 / v3 design, so earlier versions build nothing
  that blocks them (7.11 "Scope by version"). Nothing here is implemented
  before base Parsek is release-ready (roadmap "Current focus").

#### Game modes

| Mode | Version | Shared pool | What the ledger rules reduce to |
|------|---------|-------------|---------------------------------|
| Sandbox | v1 | none | Exchange, fold, salvage, crew; no ledger rows to hold, trim or merge |
| Science | v2 | science | Once-ever tech; concurrent research of different nodes can leave a science merge debt; a contested flight's science (and science recovered aboard) is held until final (7.10.2); a trimmed tail's science goes with it |
| Career | v3 | funds, science, reputation | Everything in 7.10.1, 7.10.2 and 7.11 as written |

---

## 1. Introduction

Cooperative async multiplayer lets a group of players run one shared space program. All players are on the same team: they share the KSC, the save, and the timeline. (v1 runs on Sandbox saves, v2 adds Science, v3 Career; the career examples below describe the v3 design, see "Game modes".) Each player's missions and flights are added to the shared timeline under their name. Players never need to be online simultaneously; the timeline converges over time as each player's contributions propagate through a shared folder. Correspondence chess, not real-time multiplayer.

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
| Two players spent the same funds before syncing | Both purchases stand (each was affordable where it was clicked); the merged balance goes into a merge debt, shown on the existing `Debt:` tooltip line, and a warning names the colliding spends; future earnings fill the hole |
| A peer's flight loses a kerbal before a purchase you had already shared | The loss stops at the reserved floor and the rest becomes a debt, exactly as in single player; "captured before" means "shared before" (7.11) |
| Two players achieve the same milestone first | Earlier UT gets the credit; the total payout never doubles |
| Two players both flew Jebediah before syncing | The earlier-shared packet claims him; the other mission gets a derived stand-in and a flagged row |
| A peer uses a mod you do not have | Their ghost renders with missing parts skipped, marked, and never spawns real; if they modify YOUR station with such a part, it becomes a held, marked ghost instead of a real vessel until you have the part |
| Player rewinds their own shared mission | Peers receive the supersede as a retirement record; dependent peer recordings are orphan-flagged, kept, and marked; nobody's earnings are taken back (7.10.1) |
| Player quickloads (F9) to before something they already shared | The load restores the save exactly, as in single player; at the next merge everything already in the shared folder (own packets included) imports again, because a shared packet is permanent (7.5.1) |
| Player's claim loses the arbitration | Their vessel is moved back (parked, or reverted to the winner's state); what they earned and lost on the losing flight stays on the ledger (7.10.1) |

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
              terminal and a dock-time snapshot. The tail's ledger rows
              are trimmed; what it earned was held while the race was
              open and never reached the pool (7.10.2). His
              next packet carries the truncation retirement. Every other machine derives the same verdict
              from the fold and masks Bogdan's post-dock chain from
              canonical state until that packet confirms it durably.
              Bogdan is notified on screen; his tanker sits parked in
              orbit near Harbor. He later takes control of it (his own
              vessel), redocks to the lab-equipped Harbor, and re-flies
              the delivery.
Economy       Both paid launch costs concurrently. Each launch was
              affordable on its own machine and a purchase is paid in
              full (game-actions 3.13), so the merged walk dips to
              -12,000 funds at UT 8,150,200: a merge debt. Both machines
              show 0 funds with a "Debt: 12,000" tooltip line; the next
              earning of 40,000 patches to 28,000. A one-shot warning
              names both rollout rows.
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
9. **Single-player rules hold inside a campaign; shared is permanent.** A load restores the save, a click never digs into a reservation, and nothing takes back value a player earned (`design-load-restores-the-save.md`, game-actions design 3.13, rewind design 2.12). The campaign adds no exception to them. It adds one fact they did not have to face: once a packet is in the shared folder it is in every peer's timeline, so a load can roll back only what was not yet shared, and value a fold verdict could still take back is never spendable: a contested flight's earnings wait until its race is final (7.5.1, 7.10.1, 7.10.2).

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
| Merge debt | The part of the merged funds or science walk below zero that concurrent purchases leave: each was affordable where it was clicked, together they overdraw. Paid from later gains (7.11). Distinct from the single-player floor debt of game-actions 3.13 part 4, which a loss leaves; the existing `Debt:` tooltip line shows both. |
| Capture key | Under a campaign link, a ledger row's position for the reserved-floor question "captured before?": `(share key, cseq)`, so captured before means shared before (7.11 foundation 7). |
| Claimed vessel (single-player term) | A real vessel a committed future flight still uses (the Ghost Chain Rule, `ClaimedVesselDisposalGate`, the rewind vessel hold). Unrelated to a tip claim; foreign committed flights claim vessels exactly like own ones (7.7). |

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
      |                       actions    -> Ledger (owner-tagged, capture key)
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
| `MilestoneStore` + `CommittedFutureIndex` | The stock-screen click blocks and marks (`TechResearchPatch`, `KerbalHirePatch`, the facility, part and part-upgrade blocks) read ONE UT-keyed committed-future index over the effective ledger (`CommittedFutureIndex`); the milestone store no longer feeds them | Foreign rows reach the index through the ledger (7.5 step 3); its per-target blocks skip a foreign row after the local current UT (edge case 49) while the pool reservation keeps it; EVENTS registration remains only where an event consumer remains (VERIFY at M2) | Low |
| `GameActions/ClickReservation.cs` (game-actions 3.13 part 3) | Purchases, launches and Mission Control clicks refused when they dig into what committed costs and open contract penalties need | Unchanged rule; the projection includes every member's committed future rows (the shared reservation, 7.11), and the existing Reserved tooltip line names the largest future spender | Low |
| `GameActions/StockPoolReference.cs` (3.13, phase 2) | Money stock applied with no ledger row becomes a `StockPoolAdjustment` row at the next update | A merge, the join's first recalc and a hold change (7.10.2) step the reference by the target change, so value a merge brings or a hold keeps is never filed as a leak; in a linked save an adjustment row is a Parsek gap: exported like any own Space Center row, logged Warn, announced once on screen (7.11 foundation 8, ruled 17.8) | Med |
| Ledger walk + `ClickReservationGate` (3.13) | Every effective row sets pools, reservation, floor and debt | Held rows (7.10.2) are outside every walk that sets pools, the click reservation, the floor and the debt, until their claim is final; a final accepted claim releases them at their original UT, a final rejected one trims them; the funds and science tooltips' existing Reserved area gains one Pending line | Med |
| Mod set (new, campaign create / join / import) | None | `campaign.cfg` pins the campaign's mod set; create, join and every import refuse a mod set that differs or holds a mod off Parsek's verified co-op allowlist (7.2, 7.3, 7.5 step 1, ruled 17.8) | Low |
| `GameActions/CaptureSequence.cs` + `ReservedFloorPlan.cs` (3.13 part 4) | A per-save capture sequence `cseq` decides which committed costs a loss's floor protects | Campaign capture key `(share key, cseq)`: shared before = captured before (7.11 foundation 7); `cseq` values are never renumbered | Med |
| Revert prune (`ParsekScenario.PruneRevertedFlightLedgerRows`, ruling O2) | A stock revert removes only the untagged rows the reverted flight added | Also keeps every foreign-owned row: a peer's Space Center row imported mid-flight is not the reverted flight's (7.5.1) | Low |
| Save-file load (`SaveFileLoadIntent`, `LoadReconcilePolicy`, the save's `GAME_STATE` node) | F9 and every Load restore Parsek exactly as the save holds it; scene changes, reverts, rewinds and Re-Fly loads keep memory | The import journal and export queue are saved in the PARSEK node, so a load rolls them back; the next merge re-imports whatever was shared, own packets included (7.5.1) | Med |
| `ParsekFileCollector` (quarantine grace, PR #2183, GC-15) | Unnamed heads and retained versions wait 14 days in dated quarantine, then go; a save naming them again restores them | Unchanged; imported sidecars are named by the save like any others, with their content hashes (load design 4.4), and a re-import after a load finds them again | Low |
| Ghost chains, `RewindVesselHold`, `ClaimedVesselDisposalGate` (GC-2, GC-9) | A vessel a committed future flight uses is held (ghosted in flight, hidden outside flight after a rewind), and its Recover, Delete and launch-site clear are refused | Unchanged; foreign committed recordings claim like own ones; only canonical (unmasked) recordings feed the chains (7.8 step 4) | Low |
| Re-Fly gates (`SupersedeCommit.RetireSetHoldsSealingRow`, the session switch block) | A slot whose retire set holds a gain is sealed; during a session only the re-flown vessel and its own EVA crew can be flown | The value gate gains a peer-claim clause (7.10.1); the switch block covers foreign vessels unchanged; a merge waits while a session is live (7.5.1) | Low |
| `VesselNameStore` (GC-7) | Name decisions keyed by launch guid; the apply pass rewrites each covered leg's `VesselName` | Decisions on own launches export in flush packets (`VESSEL_NAMES`); a rename of a foreign launch stays a local display decision and never rewrites the foreign recording's stored bytes (VERIFY at M2) | Low |
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
| `Logistics/RouteStore` | Routes revalidate sources vs ERS; no money, no ledger rows (route events live in `RouteEventLog`), outcome never committed | Unchanged; routes are owner-local and never exported (7.11) | Low |

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
  gameMode: string          - SANDBOX in v1; SCIENCE from v2; CAREER from v3.
                              Science pools science only; Sandbox pools nothing
  MOD_SET { MOD id version ... } - the campaign's mod set, every entry on Parsek's
                              verified co-op allowlist (7.2; ruled 17.8)

CheckpointManifest (checkpoints/<n>/highwater.cfg, single writer = founder)
  checkpointSeq: int
  createdRealTime: string
  HIGHWATER { PLAYER id seq ... }  - highest packet seq per player included

PlayerManifest (players/<id>/player.cfg, ConfigNode, single writer = owner)
  playerId, displayName
  joinedRealTime: string
  lastSeq: int              - highest packet sequence this player has exported
  heartbeatRealTime: string - rewritten at every merge tick AFTER the envelopes it
                              covers: "nothing of mine is keyed before this";
                              read by the finality test (7.10.2)

PacketEnvelope (<seq>_<shortId>.ppkt, ConfigNode, written LAST)
  packetId: string          - "pkt_" + Guid("N")
  seq: int                  - per-player monotonic, gap-detectable
  ownerPlayerId: string     - must match the players/<id>/ subtree (validated)
  exportedRealTime: string  - fixed-format UTC timestamp (6.5); the arbitration clock
  exporterUT: double        - the exporter's current game UT at export (display only:
                              the Settings member list shows each player's clock)
  schemaGeneration, formatVersion: int
  modSetHash: string        - hash of the exporter's mod set; must equal the
                              campaign's (7.5 step 1)
  TREES { }                 - complete per-tree RECORDING_TREE + RECORDING states (6.6)
  ACTIONS { }               - GameAction rows (owner's, non-seed only)
  EVENTS { }                - per-tree game-state event bundles (milestone registration; 7.5)
  CREW { MEMBER name type trait gender veteran ... } - minimal attributes for roster
                              materialization; type = Crew or Tourist (a tourist
                              materializes as a tourist, GC-12)
  TIP_CLAIMS { CLAIM ... }  - see TipClaim
  RETIREMENTS { ... }       - see RetirementRecord
  MISSION_NAMES { }         - tree id -> mission/root-group name at export
  VESSEL_NAMES { }          - launch guid -> name decision, own launches only (GC-7)
  PART_MANIFEST { }         - distinct part names used by this packet's snapshots
  PAYLOAD { FILE name size sha256 ... } - integrity list for the payload dir

TipClaim (node inside envelope; the claimant is the envelope's ownerPlayerId)
  kind: enum { Dock=0, Board=1, Claw=2, Control=3, Recover=4, Retire=5 }
                                   (Retire: a Re-Fly or rewind retirement of an
                                   EXPORTED recording, 7.10.2 rule 5)
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
  payoutActionIds: list<string>   - Recover only: the recovery rows this claim
                                    carries (funds, science aboard, crew), held
                                    until the claim is final (7.10.2)
  claimRealTime: string           - equals envelope exportedRealTime

RetirementRecord (node inside envelope; append-only)
  kind: enum { Supersede=0, RewindRetire=1, Discard=2, Truncation=3 }
  recordingIds: list<string>      - owner's retired recording ids
  supersedeRelations: list        - Old->New rows (Supersede and Truncation)
  withdrawnClaimContinuationId: string - Truncation only: the claim this withdraws
  tombstonedActionIds: list<string> - the ledger rows the owner's operation retired
                                    (a Re-Fly's retire set: loss rows only); every
                                    importer retires exactly these, never recomputes
                                    them; for a Truncation: the trimmed tail's rows
                                    minus the inherited ending's (7.9 step 3)
  reasonUT: double

AwayMark (node inside envelope; append-only; any member may write it, 7.10.2)
  awayPlayerId: string            - the silent member
  seqHigh: int                    - the away member's last seq the writer had
                                    imported; packets above it pend until Back
Back (node inside envelope; written by the returning member)
  (no fields: the envelope's key is the moment the member counts again)

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

ImportJournal (saved in the PARSEK scenario node under MULTIPLAYER; local only,
               never exported; rolls back with a load, 7.5.1)
  appliedPackets: list<(playerId, seq, packetId)>
  pendingRefs: list<(packetId, missingRecordingId)>  - claims/retirements awaiting referents
  lastScanRealTime: string

ExportQueue (saved next to the journal; local only)
  queued: list<packet>  - envelope content and the local sidecar ids it carries,
                          built at commit / retirement / flush time, not yet
                          written to the folder (7.4)
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

- Each row keeps the `CaptureSequence` (`cseq`) its owner stamped; an importer
  never renumbers it. Under a campaign link the reserved-floor plan reads the
  capture key `(share key, cseq)` instead of `cseq` alone (7.11 foundation 7).

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
- Imported sidecars land as heads, and the importer records their content
  hashes (load design 4.4) on the recordings it registers, so the next save
  names exactly those bytes. A foreign recording's content never changes
  locally (6.4); a local exempt-state rewrite retains its predecessor like
  any sidecar write.
- Local, never exported: spawn stamps (`VesselSpawned`,
  `SpawnedVesselPersistentId`, spawn safety/death state), ghost state, local
  `TerminalSpawnSupersededByRecordingId` stamps on foreign recordings,
  `MissingParts`, orphan flags, derived fold outputs, the import journal and
  the export queue (both saved in the save, 6.1), name decisions on foreign
  launches.

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
- **Local display decisions**: a vessel-name decision on a foreign launch
  (a stock rename of a peer's spawned vessel) is per-save display state; the
  name apply pass may show it but never rewrites the foreign recording's
  stored bytes or exports it. Anything more (looping, restyling) is deferred
  item D10.

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
`(OwnerPlayerId ?? "", ActionId)`, both ordinal. Keys are monotone per
writer: an envelope is stamped `max(now, previous own key + 1 s)`, and an
importer refuses (Error) a packet keyed below its predecessor from the same
writer, so a clock set back can never slip a packet in front of a race that
is already final (7.10.2).

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

Founder, on a save of a supported mode (v1 Sandbox, v2 Science, v3 Career;
any other is refused with "co-op for this game mode comes in a later
version", owner ruling 2026-10-24). In Sandbox there is no pool; in Science
the shared pool is science alone.
Preconditions, refused with the exact reason: shared folder invalid or already
carrying a campaign; save already campaign-linked; a merge journal is
non-empty; a re-fly session marker is armed; a test batch marker is present;
NotCommitted provisionals are pending; a pending tree awaits its merge
dialog; a committed-tree restore attempt or a switch-segment session is live;
the installed mod set holds a mod that is not on Parsek's verified co-op
allowlist (the refusal names each such mod; ruled 17.8, 7.11 foundation 8).
Then, in this order (the founder's mod set is pinned in `campaign.cfg`
`MOD_SET`):

1. Derive `saveSlug` from the campaign name (filename-safe slug, uniquified
   against existing saves with the `#N` pattern).
2. Stamp `OwnerPlayerId = founderId` on every committed recording/tree/action
   WHERE IT IS NULL (never overwriting a prior campaign's owners), and set
   `Exported = true` on all of them (the checkpoint carries them off-machine;
   the flag records "shared", not "sent as a packet").
3. Force a full local save (so the stamps are on disk).
4. Copy `persistent.sfs` + the `saves/<save>/Parsek/` sidecar tree into
   `checkpoints/000000/` via `FileIOUtils.CopyDirectory`, applying the strip
   list: `Parsek/RewindPoints/`, `Parsek/Saves/` (rewind saves),
   `Parsek/Recordings/_quarantine/` and `Parsek/Recordings/_versions/_quarantine/`
   (files no save names), and from the copied `.sfs` the MULTIPLAYER node
   (journal and queue) and the marker singletons (re-fly marker, merge
   journal, stock-action intent, switch-segment session, test batch marker).
   `Parsek/Recordings/_versions/` is copied with the heads, because the save
   names content hashes (load design 4.4). The copied `.sfs` carries the
   `GAME_STATE` node (ledger, events, milestones, baselines), so checkpoint 0's
   career travels inside the save file. Verify the copy; failure aborts
   creation loudly (no partial campaign).
5. Write `checkpoints/000000/highwater.cfg` (all players at seq 0).
6. Write `campaign.cfg` (pins formatVersion + schemaGeneration + gameMode +
   saveSlug). The manifest is written LAST: a folder without it is an
   unjoinable partial and the founder simply retries.
7. Write `players/<founderId>/player.cfg`.

The founder's three `*Initial` seed rows live in checkpoint 0's ledger and are
the campaign's ONLY seeds, forever.

### 7.3 Joining

1. Player points Settings at the shared folder; Join validates `campaign.cfg`
   (schema generation must equal the local build's, and the local mod set must
   equal the campaign's `MOD_SET`, else refuse with the exact mismatch or the
   missing / extra mods named - at join time the manifest pin IS the gate;
   per-packet gates take over afterwards). A member who later changes their
   mods is refused at every import and export until the set matches again
   (Warn naming the mods; play continues locally, exports queue).
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
   career. The clone's first load is a cold save-file load of the checkpoint
   (it reads checkpoint 0's `GAME_STATE`), and the merge that follows is a
   merge recalc: it steps the stock pool reference by the merged change
   (7.11 foundation 5), so the joiner's pools take the campaign's value and
   no `StockPoolAdjustment` row is minted for it.

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
   read-only foreign mission name), and VESSEL_NAMES for name decisions on
   own launches made since the last flush.

Steps: write payload dir, then envelope (seq per 6.3: folder-derived max + 1),
then rewrite own `player.cfg` (safe-write with retry). Mark exported
recordings/actions `Exported = true`. Export failure (folder offline, disk
full) is non-blocking: the commit stands locally, the packet is queued (in
the save, 6.1) and retried on every merge tick, with a settings-visible "N packets pending
export; their claims will be timestamped when the folder returns" note and a
Warn. The arbitration timestamp is stamped when the envelope is actually
written (share order means what it says); the `[PacketExport]` Info line for
a queued packet names the original commit time so a lost photo-finish is
explainable from the log (edge case 54).

### 7.5 Import (the merge)

Runs at: save load (after the existing OnLoad sweeps), scene changes into
KSC/Tracking Station, a low-frequency background poll (default 90s) while in
KSC/TS/Map scenes, and the manual Sync now button. It never runs while a
Re-Fly session is live or a merge journal is open (7.5.1).

Pipeline per new packet (ordered per player by seq; cross-player apply order
is arbitrary and harmless BECAUSE all cross-player semantics live in the fold
and the UT-sorted walk, and claims/retirements with unknown referents pend):

1. **Validate**: schema generation/format (hard refuse, name the reason - the
   existing `IsRecordingSchemaCompatible` policy), payload hashes (chunked,
   time-budgeted; see 13), id safety (`ValidateRecordingId` on every id),
   timestamps parse and stay monotone per writer (6.5), mod set
   (`modSetHash` equals the campaign's; else refuse naming the differing mods,
   like schema drift, ruled 17.8), owner consistency (packet under `players/X/` must
   carry ownerPlayerId X; tree fragments must belong to X), structural-hint
   recomputation on claims (6.1), no seed rows (Warn + strip).
2. **Apply recordings**: copy sidecars into the local Recordings dir, register
   trees/recordings mirroring the `CommitTree` shape: append at the END of
   `CommittedRecordings` (never insert: index-keyed engine/UI state has
   delete-side reindexing only), `BumpStateVersion`, `RebuildBackgroundMap`,
   auto-group with the owner's `AutoGeneratedRootGroupName` honored
   (display-name uniquified locally on collision, owner's canonical name
   preserved), mission seeded read-only, `MilestoneStore` registration from
   the packet's EVENTS bundle carrying each event's UT, for the consumers
   that still read events (VERIFY at M2 which remain; if none, the EVENTS
   block is dropped). The stock-screen click blocks and marks no longer read
   the milestone store: they read the committed-future index over the
   effective ledger (`CommittedFutureIndex`), so foreign Space Center rows
   reach them through the ledger (step 3). One campaign rule there: the
   index's per-target blocks (tech, hire, facility, part and part-upgrade
   purchase) skip a FOREIGN row whose UT is after the local current UT. A
   peer's unlock in your future must not stop you researching the node now;
   your earlier-UT research then becomes the effective charged row under
   once-ever dedup, the peer's later row is left uncharged, and a
   credit-shift notice says so (edge case 49). Foreign future rows still
   count in the pool reservation. Then latch each
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
   reduction so the drawdown guard does not floor away a legitimate merged
   balance drop, and the stock pool reference stepped by the merged change
   so imported value is never filed as a `StockPoolAdjustment` leak
   (7.11 foundation 5). Mid-flight, the existing patch-deferral applies: the walk
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

Three campaign-linked behavior overrides accompany the merge machinery,
regardless of merge timing (the third, the revert prune, is in 7.5.1):

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

### 7.5.1 Loads, reverts and rewinds

Single player has one rule per load source (`design-load-restores-the-save.md`):
a save-file load (F9, a pause-menu or main-menu Load) restores Parsek exactly
as the save holds it; scene changes, stock reverts, plain rewinds and Re-Fly
loads keep memory (ruling D2). A campaign keeps every one of those contracts
and adds one fact: **a shared packet is permanent, so a load can roll back
only what was not yet shared.**

- **The merge state lives in the save.** The import journal and the export
  queue are saved in the PARSEK node with everything else (6.1), not in an
  external file. A save-file load therefore restores the journal of its
  moment: packets applied after the save are unapplied again, and the next
  merge (the load itself triggers one) imports them again. Their sidecars are
  still on disk (a later save names them) or wait in the 14-day quarantine,
  which a save naming them again restores (load design 4.5); failing both, the
  importer copies them from the packet again.
- **Own packets come back too.** An own packet written after the save (a
  committed flight, a Space Center purchase in a flush packet, a retirement)
  is in the folder for good and already in peers' timelines. The next merge
  imports it like a peer's packet, as own data (`OwnerPlayerId` = local,
  `Exported = true`), the way a re-join imports a player's own packets (edge
  case 27). Seq numbers stay folder-derived (6.3), so the rolled-back save
  never re-uses one. In a campaign the quickload rulings (a quickload drops
  flights committed and purchases made after the save) therefore hold for
  what was not yet shared; the batched merge message after such a load names
  what came back. A packet still in the export queue at the load rolls back
  with the save and is never shared. To undo a shared flight the player uses
  the timeline's own verbs (Re-Fly), never a load (open question 17.6).
- **Reverts and rewinds keep memory**, as in single player, so imported data
  survives them (edge cases 34, 35). The stock revert's ledger prune (ruling
  O2: only rows the reverted flight wrote) also keeps every foreign-owned row:
  a peer's untagged Space Center row imported mid-flight is not the reverted
  flight's.
- **No merge during a Re-Fly session or an open merge journal.** Re-Fly Rule 3
  (rewind design 2.12) needs the session to keep the ledger it was invoked
  against, and the merge journal must not see the store move under it. The
  poll and the scene triggers skip with one Verbose line while either is live;
  the first trigger after the session merges or discards catches up. The
  session's own retirement exports through the normal retirement trigger
  after its merge.

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
through the crew dialog's reservation refusal, `StockUiCrewDialogDecoration`), never at commit and never at
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

**Tourists.** The CREW block carries the kerbal type, so a peer's tourist
materializes as a tourist (GC-12: tourists stay tourists across spawns and
holds) and never enters anyone's assignable pool. The contract that brought
the tourist is the peer's local contract (7.11).

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
    tip-advancing. Its payout rows (`payoutActionIds`) are held until the
    claim is final (7.10.2): the winner's are released at the recovery UT,
    a loser's are trimmed, so two players who recover one vessel before
    syncing are paid once, and nobody spends a payout that could vanish
    (edge case 48, ruled 17.7). Recovery of a NOT-yet-exported own vessel
    keeps today's stored mutation and pays at once (nothing to contest).
  - **Claimed vessels stay claimed.** Recover, Delete and the stock
    launch-site clear are refused for a vessel a committed future flight
    still holds back (`ClaimedVesselDisposalGate`, GC-2 / GC-9), whoever
    flew that flight: a peer's shared dock to your station at a later UT
    holds your station exactly as your own committed flight would, and a
    rewind's vessel hold (#2181) treats foreign committed recordings like
    own ones. During a Re-Fly session every switch to another vessel is
    refused (rewind design 2.12 Rule 3), foreign spawns included.
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
     claim's payout, held until final, is trimmed (7.7, 7.10.2).
4. Derived masks: every rejected, non-withdrawn claim's continuation chain
   (docked-window recording + descendants) and every accepted non-structural
   claim's target-side post-undock recording (the demoted station-side leaf)
   is masked from canonical state on ALL machines. Masking is derived
   suppression at the effective-state layer: masked recordings neither play
   as ghosts nor pass any spawn path (all six, including the post-commit
   leaf spawner, which today bypasses the spawn gate and must be routed
   through it first) nor feed a ghost chain or a route endpoint. On the
   ledger side the fold derives the held set (7.10.2): a masked or pending
   chain's gains sit outside every walk until the claim is final; a final
   rejection drops them from ELS on every machine, and the loser's
   confirming truncation names them durably (7.9 step 3). The mask is
   replaced by durable retirement state when that packet arrives.
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
  retired (its ledger rows trimmed, step 3), a Truncation retirement exported, and
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
3. The tail's ledger rows are trimmed (owner ruling 17.7): they leave the
   effective ledger on every machine, named in the retirement's
   `tombstonedActionIds`. Its gains never reached a pool, because they were
   held while the claim was not final (7.10.2); its losses leave too, which
   only raises value. One group stays: the rows of the vessel's ENDING that
   the head inherits in step 5 (the recovery or the loss, with its crew rows)
   follow that ending, so the crew who came home stay home and nobody
   recovers the vessel twice. A contract outcome inside the window stays
   (3.13 part 1, the contract is local); its reward leaves with the tail.
4. A Truncation retirement is exported: supersede rows (original pre-dock id
   -> new head id), retired ids, and the withdrawn claim's continuation id.
5. When the retired tail ended the vessel (Recovered or Destroyed), the head
   takes that ending instead of the recomputed terminal: the recovery or loss
   rows kept in step 3 then describe a vessel that is really gone, and
   nothing parks to be recovered twice (VERIFY at M4 how the ending's UT is
   carried on a head whose trajectory stops at the dock). Otherwise the
   recomputed terminal is in practice Orbiting or Landed, so the loser's
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
of previously exported data (a kind v1 never produces: no committed recording
can be deleted since 0.10.5, and an unmerged attempt is never exported).
Importers append the supersede rows (the append-only supersede walk and ERS
filtering then hide the retired data everywhere - see the section 5 row on
the `EffectiveState` cross-tree TODO, whose proposed halt is rejected),
tombstone exactly the record's `tombstonedActionIds` (6.1), feed withdrawals
into the fold, and handle any
LOCAL recordings that depended on a retired tip in two ways:

- **Transient dependents** (accepted visits, dock links with no structural
  effect): kept, visible, `OrphanedByRetiredRecordingId` set, marked
  "depends on a superseded mission (Ana re-flew Harbor)", their ledger
  rows kept (a transient visit changed nothing the retirement removed) - the route-revalidation pattern
  (`MissingSourceRecording`) generalized.
- **Coupling dependents** (a Dock/Board/Claw continuation that left a module
  attached, or a Control continuation): the dependent's own vessel would
  otherwise exist NOWHERE (it lives only inside a merged recording that can
  never spawn), which is deletion of recorded state by another route
  (principle 6). So the dependent's owner's machine runs the 7.9 truncation
  procedure at the coupling boundary: the pre-coupling segment survives
  under a new head id with a recomputed terminal and the dock-time snapshot,
  parked at its pre-coupling state; the docked-window and post-undock
  recordings retire; the truncation retirement ships, trimming their rows
  as in 7.9 step 3. This happens only while the dependent's claim is not
  final: a retirement of exported data is itself a `Retire` claim (7.10.2
  rule 5), so a final claim is never orphaned; the later-keyed retirement
  loses instead. The orphan flag stays on the retired ids for history. The player keeps the lab they paid for,
  in orbit where the old station was, and can redock it to the new tip
  (edge case 46).

Retired foreign recordings' files are NOT deleted locally (history stays
browsable; disk cost accepted for v1; checkpoints bound the shared-folder
cost).

### 7.10.1 Ledger safety in a campaign (owner rulings 2026-10-19 and 2026-10-21)

Single player now guarantees that nothing takes back value a player earned
("we absolutely never change past behavior that could affect the ledger in a
negative value way"): a Re-Fly retires losses only (rewind design 2.12), and
every other operation follows the committed-value rule (game-actions design
3.13: committed outcomes do not move earlier, known obligations are reserved,
a click that digs into a reservation is greyed, any other loss stops at the
reserved floor with the rest a debt; a recording that fails to load keeps its
rows). v5 left the campaign side of this open (todo
`LEDGER-RETIREMENT-PATHS-OUTSIDE-REFLY`). A campaign adds three ways a row
could be taken back - a peer's retirement, a fold verdict, a merge arriving
after a spend - and v6 closes each:

1. **Retirements an importer applies raise value only.** A Re-Fly supersede
   retires only loss rows (Rule 1), so applying it on any machine can only
   raise that machine's balances at every UT, whatever peer spends that
   machine holds; no import-side check is needed (v5 asked for one). The
   record names the rows (`tombstonedActionIds`), so every machine retires the
   same set. The re-fly's own new rows enter the importer's walk like any
   imported rows (point 3). A rewind retirement carries what the owner's own
   rewind retired under the single-player rules and applies as is. When the
   retirement touches EXPORTED data it is also a `Retire` claim, and until
   that claim is final the value it frees counts as a held gain (7.10.2
   rule 5).
2. **Verdicts take back only value that was never spendable** (owner ruling
   17.7, v7; v6 said "verdicts move vessels, never value"). A rejected claim's
   tail is trimmed with its rows (7.9 step 3), but every gain a not-final
   claim could lose was held (7.10.2): outside the pools, the reservation,
   the floor and the debt. So a trim removes rows no balance ever counted,
   and no balance at any UT goes down. A pending claim's gains stay held; a
   demoted leaf and a transient dependent keep their rows, because nothing
   of theirs is trimmed. ELS gains one derived filter beside the tombstone
   check: held rows, recomputed by every fold (principle 3c). Where a
   trimmed tail ended the vessel, the ending and its rows go to the
   surviving head (7.9 step 5).
3. **Merges never re-price what you hold.** A peer's rows are new history to
   the importer: their purchases are paid in full, so concurrent purchases
   make a merge debt (7.11), and their losses obey the reserved floor with
   capture order = share order (7.11 foundation 7). A merge never removes a
   row the importer held; the once-ever keys only move a credit earlier
   (milestones, firsts) or leave a duplicate purchase uncharged, and neither
   lowers a balance at any UT. A merge can lower a balance only by adding a
   peer's real cost or loss: new history the importer did not have, never a
   row taken back.
4. **A peer's claim seals a Re-Fly slot.** A slot whose effective tip
   recording is the target of a known accepted or pending claim by another
   player cannot be re-flown: the value gate (rewind design 2.12 Rule 1)
   gains one clause, reason `peerClaim:<claimantPlayerId>`, read at the same
   sites. Re-flying it would truncate the claimant's coupling on their
   machine (7.10) for an interaction the fold already accepted. A claim that
   arrives after the merge is handled by 7.10 as before, under point 2.

v6 accepted a little value collected twice (both players paid for one
capsule; a losing Control continuation keeping its science). v7 removes it:
the loser's payout and science were held and are trimmed (edge case 48).
The cost moves to time: a contested flight's earnings wait for the race to
be final (7.10.2).

### 7.10.2 Held earnings: settle before the spend (owner ruling 17.7, 2026-10-24)

In single player a flight is merged, discarded or trimmed before the player
can reach a Space Center button, so nothing is ever bought with value that
later disappears. In a campaign the losing flight's merge can come first:
an earlier-keyed packet may still be on its way (a slow sync, a player who
wrote it offline, edge case 54), and with single-writer files and no server
"nobody claimed this before me" is a statement about other machines' future
uploads (17.2). It becomes true only when every other member has published
past the claim. So the campaign settles the race before the SPEND instead
of before the merge, the way a bank holds a deposit until it clears and
Bitcoin will not let new coins be spent for 100 blocks.

1. **What is held.** A gain row (funds, science or reputation up, a milestone
   or contract reward) that the trim of a not-final claim would remove: the
   rows tagged to the continuation chain of a pending or not-final claim
   (Dock, Board, Claw, Control, an own continuation of an exported terminal),
   minus the inherited-ending rows (7.9 step 5); the `payoutActionIds` of a
   not-final Recover claim; and, for a not-final `Retire` claim, the losses
   its tombstones would free. Losses are never held: they apply at once
   (recognize a loss now, a gain when it clears). Flights that touch no
   exported vessel hold nothing and play exactly as in single player. Space
   Center rows are never flight-tagged and never held.
2. **Where held rows are.** Outside every walk that sets the pools, the click
   reservation, the reserved floor and the debt. The pools are patched
   without them, so stock itself refuses a purchase that would need them; no
   new gate. Held is DERIVED from the fold on every merge (principle 3c),
   never stored. The stock pool reference steps by a hold change like a
   merge step (7.11 foundation 5), so stock's own recovery payout is not
   filed as a leak.
3. **Final.** A claim keyed t is final when, for every member not marked
   away, the importer holds that member's packets with no seq gap up to
   their `player.cfg` lastSeq, and their `heartbeatRealTime` is after t. The
   fold's verdict on a claim depends only on earlier-keyed claims and
   retirements, so once all of those are known the verdict cannot change.
   Keys are monotone per writer (6.5), so a clock set back cannot reopen it.
4. **Release or trim.** A final, accepted claim's held rows enter the walk
   at their ORIGINAL UT (the only point every machine agrees on). Adding a
   gain can only fill a debt sooner, move a once-ever credit earlier, or meet
   a loss the 0 clamp had forgiven; no balance at any UT goes down. On
   release the rows keep their exported `cseq` and share key, so the floor
   reads them as if they had always been there. A final, rejected claim's
   held rows are trimmed (7.9 step 3); they never entered a balance.
5. **Retirements of shared data are claims.** A Re-Fly or rewind that
   retires an EXPORTED recording writes a `Retire` claim, keyed at its
   retirement packet and ordered with the others. A KNOWN earlier claim on
   the recording seals the slot (7.10.1 point 4). An earlier-keyed claim that
   surfaces after the merge beats it: the retirement is withdrawn and its
   session salvaged like a losing continuation; its new gains and the value
   its tombstones freed were held, so nothing comes back down. Without this
   rule a later retirement could orphan a final claim (edge case 46) and
   "final" would not mean final.
6. **Away members.** A silent member keeps every contested earning waiting.
   Any member may write an `AwayMark` naming them (owner ruling 17.9); an
   away member drops out of the finality test. Their packets above the mark's
   `seqHigh` pend until their own `Back` record and then rank at that
   record's key, so their offline claims lose to everything that became
   final meanwhile: deterministic, and they lose ties, never value.
7. **Known before the merge.** Before the merge dialog of a flight that will
   export a claim, Parsek runs a time-budgeted sync and the fold with the
   prospective claim included; when it would lose, the dialog's existing
   body says so ("Ana's packet, shared 19:30, docked to Harbor first; if you
   merge, this flight is cut at the dock and keeps nothing after it"). The
   KSC-entry merge already runs before any click (7.5). This settles every
   conflict already on disk the single-player way: before the Space Center.
8. **What the player sees.** The funds and science tooltips' existing
   Reserved area gains one line, "Pending: 30,000 until Ana syncs" (extra
   wording in an existing tooltip, budgeted by `TooltipEchoBudgetTests`).
   When a race settles, the batched merge message says either "Harbor: your
   tanker dock stands; 30,000 funds and 40 science released" or "Ana docked
   to Harbor first; your tanker flight is cut at the dock and its pending
   30,000 funds and 40 science are dropped". No new surface.
9. **Defect path.** A trim that would remove a row already released (only
   possible with a writer's clock past the monotone check or a build that
   predates holds) keeps the row, logs a Warn naming the claim, and never
   charges a debt for it.

VERIFY at M3: the walk is monotone in added gain rows at every UT (strategy
converters, the 0 clamp, once-ever credit shifts, science caps; a proof
test over `RecalculationEngine`); `KspStatePatcher` patches pools without
held rows while stock has already paid a recovery, with the reference
stepped by the held amount; a held milestone shows as reached in stock
ProgressTracking while only its reward waits; heartbeat write rate against
sync-service throttling.

### 7.11 The economy contract

**Scope by version** (owner ruling 2026-10-24). v1 (Sandbox) writes no pool
rows; only foundations 1 and 2 apply, and nothing is held or trimmed. v2
(Science) applies every rule below to the science pool alone: once-ever tech
(foundation 3), cutoff walks, authoritative merges and the stock pool
reference for science, a science merge debt when two members research
different nodes before syncing, held science (7.10.2). The funds and
reputation rules, contracts, facility upgrades, hires, strategies, part
purchases and the reserved floor for contract penalties are the v3 (Career)
design.

Foundations (all deterministic, all walk-level):

1. **Total order**: sort key (UT, earning-first, Sequence, OwnerPlayerId,
   ActionId), with exported actions' ordering fields frozen (6.2). Identical
   derived state on every machine, always.
2. **Founder-only seeds**: packets never carry `*Initial` rows; checkpoint 0
   does. The importer strips stray seeds with a Warn.
3. **Once-ever same-target spends**: walk-level keyed sets (the
   milestones/contracts pattern) for ScienceSpending by nodeId,
   FacilityUpgrade by (facilityId, toLevel), StrategyActivate setup costs by
   strategyId, KerbalHire by kerbalName, part purchases by part name, part
   upgrades by upgrade name (the `FundsSpending` source `PartUpgrade` row,
   game-actions 3.13). First-in-order is effective and
   charged; duplicates are ineffective and UNCHARGED (also fixes the latent
   single-player double-charge on duplicate rows). Idempotent application
   (tech unlock HashSet, absolute facility level) is unchanged.
4. **Cutoff walks only**: campaign-linked recalcs always use the current-UT
   cutoff variant (peers' future rows exist almost always; the uncut walk
   would patch future earnings into current pools and drag the contract
   PrePass deadline horizon to the latest peer row).
5. **Authoritative merges**: merge-triggered recalcs set the authoritative-
   reduction flag so `ApplyDrawdownGuard` does not floor a legitimate merged
   balance drop, and step the stock pool reference (`StockPoolReference`,
   3.13 phase 2) by the merge's target change, so value a merge adds or
   removes is Parsek's own change, never read as money stock applied without
   a row. Without the step, the first update after a merge would file the
   whole merged difference as a `StockPoolAdjustment` row and export it back
   to the peers it came from.
6. **Pinned baseline**: campaign-linked patching derives from the checkpoint
   baseline + the full walk (7.5), never from post-link local baselines.
7. **Capture order is share order.** The reserved floor (3.13 part 4) asks
   whether a cost row was captured before a loss, by the per-save capture
   sequence `cseq`. A per-save counter means nothing across machines, so under
   a campaign link a row's capture key is `(share key, cseq)`: rows carried by
   checkpoint 0 first, then each packet's rows at that packet's arbitration
   key (6.5) in their exported `cseq` order, then own rows not yet exported in
   local `cseq` order. Same log, same keys, same floor on every machine; an
   own unexported row's position settles when its packet is written
   (transient divergence, like a pending claim). This is principle 5 applied
   to the economy's one share-order question; the walk itself stays in UT
   order.
8. **No money outside the ledger** (owner ruling 17.8). A campaign links only
   with an allowlisted mod set (7.2, 7.3), so nothing but stock and Parsek
   moves the pools. A `StockPoolAdjustment` row minted in a linked save is
   therefore a Parsek gap: it is kept as a ledger row (the event), exported
   in the next flush like any own Space Center row so every machine walks
   the same log, logged Warn with the amount and the pool, and announced by
   one on-screen message naming the amount. Rows minted before the link ride
   checkpoint 0. VERIFY at M3 that a merge never mints one (foundation 5).

Policies:

- **Concurrent purchases make a merge debt, never a cancellation.** Each
  purchase was affordable where it was clicked, against that machine's
  reservation; merged, two of them can overdraw. A purchase is paid in full
  (3.13: a cost is never turned into a floor debt) and never cancelled after
  the fact (paradox avoidance), so the merged walk runs below zero: the merge
  debt. Stock shows 0, the funds and science tooltips' existing `Debt:` line
  shows the depth, a one-shot warning at the merge names the colliding spends,
  and later gains fill the hole before money shows again. While it lasts the
  click reservation finds nothing available, so purchases and launches are
  refused for their cost (3.13 part 3); flights already flying are never
  touched. VERIFY at M3: how `KspStatePatcher.PatchFunds` writes a negative
  target under the stock pool reference (expected: stock 0, the shortfall
  carried in the walk), and that the `Debt:` line reads the merge debt as
  well as the floor debt.
- **Any other loss follows the reserved floor.** A peer's crew death, vessel
  loss or contract-fail penalty is a loss row like a local one. With a cost
  captured (shared) before it still ahead, it stops at the reserved floor and
  the rest is a debt charged from the first room above the floor; with none it
  applies as stock applies it (funds and science stop at 0 and the rest is
  forgiven, reputation may go negative). So a peer who is behind in UT cannot
  lose away the money a purchase you already shared needs.
- **Shared reservation, attributed.** This is the existing single-player
  reservation, not a new rule, but across free-running clocks it bites: what
  the click reservation (3.13 part 3, `ClickReservationGate`) lets you spend
  at your UT is the MINIMUM projected balance through EVERY member's
  committed future rows, so a member far ahead in UT who has spent down the
  pool pins the budget of members behind. The funds tooltip's existing
  Reserved line names the largest future spender and UT ("reserved: 120,000
  by Ana at Y2 D250"; extra wording in an existing tooltip, budgeted by
  `TooltipEchoBudgetTests`), the Settings member list repeats it, and the
  join notice (edge case 31) explains it. Not a defect: the money IS
  committed; the fix is attribution.
- **One recovery is paid** (7.7): concurrent recoveries of one vessel hold
  their payouts until the race is final; the winner's is released, the
  loser's trimmed (7.10.2, ruled 17.7).
- **Earlier UT wins credit.** Milestone/first races across players resolve by
  the existing once-ever UT-order walk - identical to how retroactive commits
  shift credit today. Deep-warping ahead cannot lock in a first; credit (never
  the total) can shift at a later merge, with a notification when it does.
- **Science and reputation** pool with no special handling (per-subject caps
  and once-ever milestones already sum/dedup correctly).
- **Contracts are local-only in v1**: contract accept/complete/fail rows are
  excluded from packets; funds/rep from a player's contracts still enter the
  shared pool as that player's plain earning and loss rows. Each member's
  contract queue is independently rolled (7.3 step 4). The committed-value
  rule's contract parts apply per machine: a committed outcome of your own
  contract cannot move earlier (3.13 part 1), and your open contracts'
  penalties are reserved while a committed cost lies ahead (part 2). Peers'
  contracts are invisible to you; their penalties reach you only as the loss
  rows they file, under the floor rule above. Canonical cross-save contract
  identity is a Career-stage problem (v3 or later, section 10).
- **Routes are owner-local and never exported.** Supply routes carry no money
  and write no ledger rows; a route's cycles exist only as the owner's clock
  passes them, and every load back in time drops the later ones (logistics
  design 10.6). Nothing about a route is committed, so nothing is shared:
  route definitions, the route event log and route cargo stay in the owner's
  save, and peers see the owner's vessels as their recordings left them
  (cargo a route moved since is the resource slack of principle 6). Space
  Center origin routes are free in every mode, as in single player. Route ENDPOINTS must be
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
timeline entries on the target. A debt's depth shows on the funds and science
tooltips' existing `Debt:` line (3.13 part 4), which reads the merge debt too
(7.11), since KSP's own display stops at 0 and would otherwise hide it.

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
10. **Loser already recovered the mission.** The surviving head takes the
    tail's Recovered ending instead of parking a vessel (7.9 step 5), and the
    recovery's rows follow that ending, so the vessel and its crew exist once,
    at the KSC. Everything else the docked window earned was held while the
    claim was open and is trimmed (7.9 step 3, 7.10.2): canonically the
    tanker never docked, and nothing it earned there was ever spendable.
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
    display stays 0 with the `Debt:` tooltip line; the click reservation
    (3.13 part 3) finds nothing available, so every purchase and every launch
    is refused for its cost, and a zero-cost action is still allowed. Social
    problem by design; the warning names the debt each merge while it
    persists.
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
    is rejected: the tree is retired whole with its rows (Case 3, Control
    form; its gains were held, 7.10.2), your local station reverts to the
    canonical tip, and your own tug's flight (a separate tree) is untouched. Boarding it with a kerbal and flying it
    is the same story via a Board claim (a truncated board parks your kerbal
    on EVA, edge 26). Settings and the Recordings Manager show "pending
    arbitration", and the pool tooltips show its earnings as pending, until
    the race is final.
33a. **"I took control of Ana's rover, drove it 200 m, and switched back."**
    A tip-advancing Control claim (landed position moved beyond tolerance).
    If you only turned the lights on and left: `SwitchSegmentNoOpClassifier`
    treats it as a no-op segment and auto-discards it; no claim.
33b. **"While docked for refueling I reboosted the station."** Not a transient
    visit: the trajectory clause fails, so the claim is tip-advancing; if
    accepted, the station's canonical tip moves to your target-side
    post-undock recording (the station on its new orbit).
34. **Quickload (F9) after a merge tick.** The load restores the save
    exactly (`design-load-restores-the-save.md`), import journal included, so
    packets applied after the quicksave are unapplied for a moment; the
    merge the load triggers imports them again (7.5.1). Their sidecars come
    back from disk or from the 14-day quarantine, or are copied from the
    packets again. Foreign vessels spawn again as their recordings say.
    Nothing shared is lost. (v5 relied on in-session loads keeping memory;
    that rule is gone.)
35. **Revert to launch / VAB after a mid-flight merge.** A revert keeps
    memory, so imported data persists and the reverted world re-derives
    spawns; the revert's ledger prune keeps every foreign-owned row (7.5.1). A
    dock to a foreign spawn inside the reverted flight never commits, so no
    claim exists; the dangling local supersede stamp it left is cleared by
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
    death's reputation penalty pooled at first merge. A death is a loss row,
    so the slot stays re-flyable (rewind design 2.12 Rule 1); the re-fly's
    retirement names the penalty in `tombstonedActionIds`, every machine
    retires it (a raise), and the kerbal's fate follows the re-fly on every
    machine (Rule 2).

Stress-test cases (fourteen imagined multi-player sessions, 2026-09-02):

46. **"Ana re-flew Harbor's launch; my lab was docked to the old Harbor."**
    Bogdan's accepted lab continuation depended on the retired core. It is a
    coupling dependent, so 7.10 salvages rather than orphan-flags: on
    Bogdan's machine the lab's pre-dock segment survives under a new head id
    (recomputed Orbiting terminal, dock-time snapshot), parked where the old
    station was; the docked window and post-undock recordings retire and
    ship as a truncation. His lab still exists, he still paid for it once,
    and he can redock it to Ana's new Harbor. Carol's fuel visit to the old
    station is a transient dependent: flagged and harmless. This happens only
    while Bogdan's claim is not final: Ana's re-fly of shared data is a
    `Retire` claim (7.10.2 rule 5). If Bogdan's lab claim was already final
    (or keyed before her retirement), the re-fly is refused for that slot
    when known (7.10.1 point 4) or withdrawn when it surfaces later; a final
    claim is never orphaned.
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
    one wins and is the canonical Recovered. Both payouts were held until the
    race was final (7.10.2): the winner's is released at her recovery UT, the
    loser's is trimmed, so the pool gains the capsule once (v6 paid both);
    the stored terminal is never mutated on either machine (Recovered is
    derived). The rescue variant: Ana Switch-To's Bogdan's
    fuel-less lander, EVAs Bob, boards him into her ship, recovers. The crew
    gate does not fire at board (7.6 rescue carve-out); the lander's Control
    claim is crew-changing and tip-advancing; Bob's reservation bounds at
    Ana's recovery UT; Bob is back on everyone's roster, still Bogdan's.
49. **"Ana unlocked Advanced Rocketry at Y2 D10; I'm at Y1 D40 and R&D says
    it's already committed."** The committed-future index's research block
    skips a foreign row after your clock (7.5 step 2). You research it now
    at Y1 D40; your earlier-UT row is the effective charged one, Ana's is
    left uncharged at the next merge with a credit-shift notice; the node is
    unlocked from Y1 D40 on for everyone.
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

Cases from the single-player rules built since v5 (2026-10-24):

56. **"I pressed F5, committed a flight, the folder synced, then I pressed
    F9."** The load restores the save and the flight is gone locally for a
    moment. Its packet is in the folder and in Bogdan's game, so the merge
    the load triggers imports it back as mine, and the merge message says so
    (7.5.1). Had the export still been queued at the F9, the queue rolled
    back with the save and the flight is gone for good, as in single player.
57. **"Bogdan's packet arrived while I was in a Re-Fly session."** The merge
    waits; my session keeps the ledger it started with. The first merge after
    I merge or discard imports it (7.5.1).
58. **"Ana failed a contract at Y1 D20; I had already shared a Y1 D60 base
    purchase."** My purchase was shared first, so it counts as captured
    before her loss: her penalty stops at the floor my purchase needs and the
    rest is a debt from later gains (7.11). Had her packet been shared first,
    her loss applies as stock applies it (down to 0, the rest forgiven) and my
    purchase, paid in full, can leave a merge debt.
59. **"Bogdan shared a dock to my station at UT 9,000; I am at UT 8,000 and
    want to recover the station."** His committed future dock holds my
    station by the Ghost Chain Rule: Recover is greyed with the reason, as for
    my own committed future flights (7.7).
60. **"I want to Re-Fly my booster, but Carol docked to it and shared."**
    Carol's accepted claim on the booster seals the slot (7.10.1 point 4):
    re-flying it would truncate her flight on her machine. With no claim on
    it, the slot is open as in single player.

Cases from the owner rulings of 2026-10-24 (v7):

61. **"I docked my tanker to Harbor, merged, and the 30,000 it earned says
    pending."** Harbor is Ana's exported station, so the dock is a claim, and
    Ana has not synced since before it. Your pools and the purchase buttons
    count without the 30,000 until her next sync passes the dock (7.10.2);
    then the merge message says "released" and the money is there, booked at
    the dock's UT. Had Ana shared an earlier dock, the message says "dropped"
    instead, and nothing you bought depended on it.
62. **"Carol has not played for two weeks and my earnings are stuck."** Mark
    her away from the Settings member list (any member may, owner ruling
    17.9). Your pending earnings release at the next merge. When Carol comes
    back, her offline flights count as shared at her return, so a race she
    would have won by an earlier key she now loses; she loses the tie, never
    value she could already spend.
63. **"I installed a mod that pays funds for science, and co-op stopped
    syncing."** The mod is not on the verified co-op allowlist, so imports
    and exports refuse until your mod set matches the campaign's again (7.3
    step 1). Single player saves are unaffected.
64. **"A toast said Parsek booked 1,200 funds it could not explain."** A stock
    path Parsek does not track yet moved money in a linked save. The amount
    is kept as a ledger row, shared with everyone, and logged so the gap can
    be fixed (7.11 foundation 8).

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
  ELS keeps the tombstone check as its only filter (fold masks are physical,
  7.10.1). The committed-value rule (game-actions 3.13) and the Re-Fly value
  gate (rewind design 2.12) apply as built, plus the capture key (7.11) and
  one value-gate clause (7.10.1).
- The load contracts of `design-load-restores-the-save.md`: a save-file load
  restores the save; scene changes, reverts, rewinds and Re-Fly loads keep
  memory. A campaign adds only the re-import of what was already shared
  (7.5.1).
- Rewind-to-Separation mechanics on own data, the merge journal, RP lifecycle.
- ERS/ELS routing discipline and the grep-audit gate (new import/fence/fold
  code routes through `EffectiveState` like everything else; fold masks are
  implemented AT the ERS layer, not around it).
- Supply-route execution and `RouteTreeGuard` (routes write no ledger rows
  since 0.10.6 and are never exported).
- `ParsekFileCollector` (with its 14-day quarantine), `LoadTimeSweep`,
  `PreParsekBackup` behavior (imported data is registered, and named by the
  save, before they can see it as unknown).

## 10. Out of Scope (v1)

- Science and Career saves (owner ruling 2026-10-24): v1 founds and joins
  Sandbox saves only; Science is v2, Career v3. The ledger and career
  sections of this document are their design.
- Per-player economies, milestone racing, opponent packs (Phase 15).
- Contract pooling / canonical contract identity (v3 or later; contracts are
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
- Exchanging supply routes, route state or route cargo (routes are
  owner-local by design, 7.11).
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
- The campaign's saved state (link, import journal, export queue, annotation
  cache) is scenario-node data, like the load design's `GAME_STATE` node: it
  does not move the recording schema generation.
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
| Journal missing or corrupt (a hand-edited save) | Re-imports are idempotent (ids match, actions dedup by ActionId presence); journal rebuilds; pending set re-derives | None; one slow merge |
| A load rolls the save back past applied or written packets | The save's journal and queue roll back with it; the next merge re-imports what was shared, own packets included (7.5.1); a queued packet that rolled back is never shared | None of what was shared |
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
| `[Salvage]` | Truncation execution detail (new head id, cut UTs, kept rows, terminal recompute) |
| `[MergeEconomy]` | Merge-debt and floor-debt warnings, credit shifts, once-ever dedup hits, seed strips, reconcile-preservation and cutoff decisions, capture-key mapping, stock-pool-reference steps |
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
- **Verbose**: merge skipped while a Re-Fly session or merge journal is
  live (once per trigger kind); per-packet validation detail; optimizer freeze skips; fold
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
- **ImportJournalRollsBackWithLoad** - journal and export queue round-trip
  through the save; a cold load of an older save unapplies later packets and
  the next merge re-imports them, own packets as own data with
  `Exported = true`; a queued packet that rolled back is never written.
- **SalvageTruncationCut** - truncation at a dock boundary yields a NEW head
  id with cleared child link, recomputed spawnable terminal (from the
  InferTerminalStateFromTrajectory core), the dock-time snapshot (or
  segment-start fallback), correct supersede rows, and `tombstonedActionIds`
  naming the tail's rows minus the inherited ending's; a tail that ended
  Recovered or Destroyed hands that ending, with its rows, to the head.
- **CrewClaimByShareOrder** - two packets flying the same kerbal: earlier
  key owns; the loser's derived stand-in name is identical across machines.
- **MergeDebtAndFloor** - two concurrent purchases overdraw the merged walk
  (merge debt, patch target 0, next earning fills it first); a peer loss
  shared after a cost it lies behind stops at the reserved floor; with no
  cost captured before it, it applies as stock (stops at 0, forgiven).
- **CaptureKeyIsShareOrder** - rows from checkpoint 0, two packets and own
  unexported rows order by (share key, cseq) identically on two machines
  that applied the packets in opposite orders; `cseq` values are never
  renumbered.
- **MergeStepsStockPoolReference** - a merge that changes the funds target
  mints no `StockPoolAdjustment` row on the next update; money stock applied
  outside the merge still does.
- **HeldUntilFinal** - a contested flight's gain rows are outside the pools,
  the reservation, the floor and the debt while its claim is pending or not
  final; losses apply at once; a final accepted claim releases them at their
  original UT and no balance at any UT goes down; a final rejected claim
  trims them and no balance at any UT goes down.
- **FinalityTest** - final only when every non-away member's packets are
  gap-free to lastSeq and their heartbeat is after the claim key; a packet
  keyed below its writer's previous key is refused; an `AwayMark` removes a
  member from the test and their later packets rank at their `Back` record.
- **RetireClaimNeverOrphansAFinalClaim** - a Re-Fly retirement of exported
  data keyed after a final claim on the same recording is withdrawn; its
  held gains and freed losses never entered a balance.
- **ModSetGate** - create, join and import refuse a mod set that differs from
  `MOD_SET` or holds a mod off the allowlist, naming the mods.
- **ReFlyRetirementAppliesNamedRows** - an imported Re-Fly retirement
  tombstones exactly its `tombstonedActionIds`, all loss rows; balances at
  every UT are >= before on a machine holding extra peer spends.
- **PeerClaimSealsReFlySlot** - a slot whose tip is the target of another
  player's accepted or pending claim fails the value gate with
  `peerClaim:<id>`; an own or absent claim leaves it open.
- **RevertPruneKeepsForeignRows** - a revert after a mid-flight import keeps
  the imported untagged foreign rows and prunes the flight's own.
- **MergeWaitsForReFlySessionAndJournal** - every merge trigger skips while
  a Re-Fly marker or merge journal is live and catches up after.
- **ForeignTouristMaterializesAsTourist** - a CREW member of type Tourist
  materializes as a tourist and never enters the assignable pool.
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
- **RecoverPaysEveryRecoverer** - two Recover claims on one target: one
  canonical Recovered (share order), both recovery earnings effective, no
  ledger mask; no stored terminal mutation on an Exported recording.
- **ForeignFutureTechDoesNotBlockLocalResearch** - a foreign tech row with
  UT > current UT does not block `TechResearchPatch` (the committed-future
  index skips it) but stays in the pool reservation; with UT <= current UT
  it blocks; an own future row blocks as in single player.
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
  `OrphanedByRetiredRecordingId`, ledger rows kept; ERS hides the
  retired chain (cross-tree supersede walk, halt-TODO rejected).
- **CommittedFutureIndexSeesForeignRows** - imported foreign Space Center
  rows reach the committed-future index through the ledger: a past foreign
  tech unlock or hire blocks its stock click, a future one does not
  (edge case 49), and both count in the pool reservation.
- **QuickloadPastOwnExport** - F5, commit and export, F9: the flight is
  absent after the load and present, owned and `Exported`, after the merge
  the load triggers; its sidecars come back from quarantine or the packet.
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
| M1 | Identity + campaign store (manifest, checkpoint 0, join/rejoin, mod-set allowlist gate, ContractSystem/applicant regeneration) + Settings section | Ships inert without a campaign |
| M2 | Export/import of recordings as read-only citizens + ownership fence + journal and export queue in the save (re-import after a load, 7.5.1) + merge deferral during Re-Fly sessions + scope-tracker latching + crew registration + attribution | No interaction, no economy merge: purely additive world sharing; scale budgets measured here |
| M3 (v2) | Ledger exchange for the science pool (the funds and reputation half is v3): deterministic sort + ordering freeze, seeds policy, once-ever dedup, merge debt + reserved floor with the capture key, stock-pool-reference stepping, committed-future-index foreign-future rule, cutoff-walk mandate, reconcile preservation, revert prune keeps foreign rows, baseline pinning, authoritative merges, credit notifications, action flush | The pooled economy |
| M4 | Foreign spawns + full control (foreign-continuation route, Recover claims) + coupling + dock-time parent snapshot stamp + tip claims + the arbitration fold + salvage ladder (Cases 1 and 3; Case 2 = truncate; a truncation trims the tail's rows except the inherited ending's) + held earnings (finality test, heartbeats, monotone keys, away marks, release / trim, merge-dialog preview) + retirement propagation (named tombstone sets; `Retire` claims) + the Re-Fly peer-claim seal | The co-op world |
| M5 | Crew claim rule + derived substitution + assignment gate | |
| M6 | Polish: founder checkpoints N>0, missing-parts UX, cloud-placeholder handling, poll tuning, notifications batching, per-player timeline filter | |
| v1.1 | Excise-and-stitch (Case 2 upgrade) | Designed here, built later |
| v2 | Science saves: M3 for the science pool + held earnings (science) | Built after v1 is played |
| v3 | Career saves: funds and reputation pools, contracts, facility upgrades, hires, strategies, part purchases, the reserved floor, recovery funds payouts | Designed here (7.10.1, 7.10.2, 7.11); built after v2 is played |

Ground-up per the maintainer's direction: each phase is independently playable
and testable; M2 alone already delivers "see your friends' missions". v1
(Sandbox) is M1, M2, M4 without held earnings, M5 and M6; M3 and the held
earnings of M4 arrive with Science saves (v2). No phase starts before base
Parsek is release-ready (owner ruling 2026-10-24).

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

Generation 5 stays RESERVED for this design's shape change. The 2026-09-06
operator ruling on P14 (`TerminalState.Disassembled`) settled the adjacent
question and did NOT consume the number: a purely additive enum member renames
no key, adds no field and changes no layout, so it shipped at generation 4 and
no fixture was re-stamped. Whoever bumps for a SHAPE change still owns the
re-harvest of the 33 stamped fixtures; the measured blast radius is recorded in
`docs/dev/todo-and-known-bugs.md` under DISASSEMBLED-VESSEL-TERMINAL-STATE.

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
The CREW block carries name/type/trait/gender/veteran. Whether to also carry
courage/stupidity/XP for exact cross-machine roster parity, or accept
cosmetic divergence on derived stand-ins, is an M5 call.

### 17.6 (resolved) A load past something already shared
Owner ruling 2026-10-24: "Shared stays shared." The text (7.5.1) stands: a
load restores the save, then the next merge imports again everything in the
folder, own packets included, so a quickload cannot unshare. Alternatives
considered and rejected: (a) the load writes retirement
records for own packets newer than the save - rejected by default, because a
retirement that removes a shared flight would take its earnings back on every
peer and orphan their dependents, which the ledger-safety ruling forbids; (b)
export only at an explicit save, so a quickload never crosses a shared packet
- rejected by default, because it delays every share and still fails for a
quickload past an explicit save.

### 17.7 (resolved) What a losing flight keeps
v6 defaulted to "verdicts keep value" (no row ever trimmed; both recoveries
paid). Owner ruling 2026-10-24 reversed it: a trimmed flight's rows are
trimmed too, or the door opens to paradoxes. To keep the never-reduce ruling
at the same time, contested earnings are held until the race is final
(owner pick among: hold until everyone syncs; spend at once and trim into
debt; spend at once and charge back today; never take back). Text: 7.9 step
3, 7.10.1 point 2, 7.10.2.

### 17.8 (resolved) Money outside the ledger
Owner ruling 2026-10-24: a mod-compatibility question. A campaign links only
with a matching mod set whose every mod is on Parsek's verified co-op
allowlist (allowlist, not blocklist); single player keeps today's
flexibility. A `StockPoolAdjustment` row in a linked save is a Parsek gap:
shared as a ledger row, logged, announced once on screen (7.11 foundation 8).
VERIFY at M3 that a merge never mints one (foundation 5).

### 17.9 (resolved) Releasing a silent member's hold
Owner ruling 2026-10-24: any member may mark a silent member away (an
append-only `AwayMark`, 6.1); the away member's packets then rank at their
`Back` record (7.10.2 rule 6). Rejected: founder only; nobody.

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
| Economy foundations | `RecalculationEngine.SortActions`, keyed sets in the spend modules, `LedgerOrchestrator` merge trigger + cutoff mandate, `Ledger.Reconcile` preservation flag, `KspStatePatcher` baseline pinning, `StockPoolReference` merge step, the capture key in `CaptureSequence` / `ReservedFloorPlan`, the foreign-future skip in `CommittedFutureIndex`, the foreign-row keep in `ParsekScenario.PruneRevertedFlightLedgerRows` |
| Merge state in the save | `MULTIPLAYER` node in `ParsekScenario` OnSave / OnLoad (journal, export queue) + a `LoadReconcilePolicy` category for it |
| Re-Fly peer-claim seal | one clause in `SupersedeCommit.RetireSetHoldsSealingRow`, fed by the fold's claim set |
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
  design merges; section 3.13 is the committed-value rule (reserved floor,
  debt, click reservation, revert and failed-load rules).
- `docs/dev/design-load-restores-the-save.md` - the load contracts 7.5.1
  builds on (save-file loads, content hashes, the 14-day quarantine).
- `docs/parsek-logistics-supply-routes-design.md` section 10.6 - why routes
  carry no money and are never committed.
- `docs/dev/done/plans/committed-value-rule-implementation.md`,
  `docs/dev/done/plans/refly-ledger-safety-implementation.md` - the built
  mechanisms behind 7.10.1 and 7.11.
- `docs/dev/design-mission-crosstree-dock.md` - the derived-link precedent for
  cross-tree interaction without foreign-side writes.
- `docs/parsek-rewind-to-separation-design.md` - supersede/tombstone machinery
  reused by retirements and salvage; section 2.12 is Re-Fly ledger safety
  (loss-only retire sets, session isolation, the switch block).
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
- Freshness pass 2026-10-24 (v6): the 2026-10-19 Re-Fly ledger-safety rulings,
  the 2026-10-21 committed-value and logistics rulings, load restores the
  save, the quarantine grace and the GC traces, as built through PR #2193.
