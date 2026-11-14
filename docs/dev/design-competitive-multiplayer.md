# Parsek Multiplayer: the Space Race - Design Document

*Design specification for competitive async multiplayer: up to four players race each other
to the milestones of the Kerbal system, each in their own game, sharing through a synced folder.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings
to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost
vessels alongside new ones. This document specifies how several players, each in their own KSP
instance with their own save and their own game clock, run a space race: everyone sees the
others' space centers, flights and flags as ghosts and scenery, and one common board shows who
reached each milestone at the earliest game date.*

**Status:** DESIGN v1 (2026-11-14, pre-implementation). Written from the owner's rulings of
2026-11-11 and 2026-11-14 (section 0), a stock-code feasibility pass on KSP 1.12.5 (section 12)
and a design consultation. Nothing here is implemented, and nothing is implemented before the
owner says so (roadmap "Current focus").
**Supersedes:** `docs/dev/done/design-coop-async-multiplayer.md`. Cooperative multiplayer is
deprecated (owner ruling 2026-11-14). Section 5 lists what this design keeps from it.
**Out of scope, permanently:** real-time synchronization; any shared vessel, shared crew or
pooled economy; any way for one player's data to change another player's game state.
**Related docs:** `docs/parsek-architecture.md`,
`docs/parsek-game-actions-and-resources-recorder-design.md` (milestones, the once-ever walk),
`docs/parsek-timeline-design.md`, `docs/parsek-ghost-trajectory-rendering-design.md`,
`docs/dev/design-load-restores-the-save.md`, `docs/dev/design-save-format-policy.md`,
`docs/roadmap.md` (Phase 15).

---

## 0. Owner rulings

These are binding. Quotes are the owner's words.

**2026-11-11**

- Competitive comes first: "we should probably build competitive multiplayer first on this
  general design stroke".
- Vessels and ledgers stay fully separate per player. Common are the other players' ghosts and
  one milestone timeline.
- A first is never final. The timeline is dynamic: any player can go back and reach a milestone
  at an earlier game date to take it. Who is first is recomputed from the timeline.
- A race is bounded by an optional real-time limit. "First to get there wins, no retakes" is a
  house rule a group can choose.

**2026-11-14**

- "i've decided its too complicated to support coop multiplayer, so that idea is deprecated".
- Up to four players. "the space centers should be exact copies of KSC but at other locations,
  and players will be automatically assigned locations so they are opposite of each other as
  possible". All on the equator, on flat land.
- "the alternate launch locations are common - meaning that any player can launch from desert
  site etc. but only a player can launch from its own ksc".
- "we need an interface so players can select name, color at the start". One distinct look per
  player, from the stock suits: "we prefer stock, but if DLCs are missing we should have
  stock-ish replacements if possible".
- "it will be a new game mode so its easier for us to isolate - will be selectable from the
  menu - just like Singleplayer, Multiplayer will support Sandbox, Science and Career modes".
- "each players sees the other players buildings, vessels, flags etc. but only as ghosts or
  non-interactable objects, so there is no interaction that could break causality for each of
  them".
- "flags of other players should be readable, so they should be clickable, not pure ghosts".
- "we will need a separate multiplayer timeline window where milestones are shown for each,
  color coded etc. so we can actually have a space race".
- "the multiplayer infrastructure will be async, based on a shared folder - preferably additive
  so its easy to integrate in each players world".
- "we need to prevent other players ghosts parking on the runway or on the launch locations -
  if a player leaves his vehicle in that exclusion area, it would just not be visible to the
  other player; to prevent weird game breaking strategies".
- "when selecting the name and color the player should also select the location where the
  shared folder is; we should analyze if we would read directly from there or copy that stuff
  inside the KSP folder"; "that location - it should be editable from settings" (4.4, 7.2).
- "we will implement on the structure of the 1.1.x build", after the design is settled;
  "important: lets really think about the ideal structure and architecture from the start, so
  we don't have a lot to refactor later" (section 15).
- "we should support allowing other players to join that multiplayer game later".
- "we should also consider some ideas about how we could prevent players just modifying the
  shared game files to cheat (but we can start with a simple version, hoping players know each
  other and are well meaning)".
- Scoring: a points ladder (decision card, 2026-11-14). Each first is worth points by
  difficulty; the most points held when time runs out wins.
- Rival milestones dated after your own game clock are always shown (decision card,
  2026-11-14).

**UI exception.** The standing rule "no new player-facing UI surfaces" gets three owner
exceptions from the rulings above, all of them absent from single-player saves: the main menu
Multiplayer entry, the race setup dialog, and the Space Race window. `.claude/CLAUDE.md` records
them when the first one is built.

---

## 1. Introduction

A race is a group of up to four players. Each plays their own save, at their own pace, on their
own game clock. Nobody needs to be online at the same time. A folder synced by any file-sharing
service (Dropbox, Google Drive, a network share) carries each player's public state to the
others. Correspondence chess, not real-time multiplayer.

What a player shares is what a spectator could see: their flights (as recordings), their flags,
their space center, and the game date at which they reached each milestone. What a player keeps
is everything their game computes with: ledger, science, funds, reputation, contracts, tech,
facilities, roster, vessels. A rival's data only ever ADDS things to look at. It is never an
input to anything.

The race itself is a board. For each milestone on the ladder, the holder is the player who
reached it at the earliest game date. Because Parsek lets a player rewind and fly an earlier
mission, a first can be taken back: land on the Mun on day 30 and you take the first from the
rival who landed on day 42. When the race's real-time limit runs out, the player holding the
most points wins.

### 1.1 What the player sees

| Situation | What happens |
|-----------|--------------|
| Main menu | A Multiplayer entry next to the stock ones opens the race setup dialog |
| Player creates a race | They pick a race name, game mode, difficulty, shared folder, optional end date and house rules, then their own name and look. A fresh save starts at day 1 at their space center |
| Friend joins | They point at the shared folder, see the race and who is in it, pick a name and a free look, and get a fresh save at their own space center, on the far side of Kerbin |
| A friend joins weeks later | The same Join. A race always has four seats; any free one can be taken at any time. The newcomer starts at day 1, sees everything the others already did as ghosts and board dates, and can still take any first by reaching it at an earlier game date |
| Player launches | From their own KSC (only they can), or from any stock alternate launch site (anyone can) |
| Player commits a flight | Their public state is written to their own subfolder of the shared folder |
| A rival's state arrives | Their flights appear as ghosts at the game dates they were flown, in their color; their milestones appear on the board |
| Player looks at a rival's site | An exact copy of the KSC stands there as scenery. Rival rockets lift off from it as ghosts when the player's clock reaches each launch |
| A rival's flight has ended | What it left behind stays: the station in orbit and the lander on the Mun as ghosts that cannot be touched, the flag as an object whose plaque can be read |
| A rival left a vehicle parked on a runway, a pad or an alternate launch site | Nobody else sees it. Launch areas always look empty of rivals' leftovers |
| Player flies through a rival's ghost | Nothing happens. Ghosts have no collision and cannot be docked to, boarded, grabbed or recovered |
| Player opens the Space Race window | A table of milestones by player, each cell the game date that player reached it, the holder marked in their color; points, each player's progress and the time left on top |
| A rival takes a first the player held | One on-screen message at the next sync: "Bogdan took Mun Landing: day 30 beats your day 42" |
| Player rewinds and lands earlier | At the next sync the board on every machine gives the first back |
| Player quickloads to before a shared flight | The flight leaves the race at the next sync, as it left the player's own game |
| The end date passes | The board freezes on what was shared in time and names the winner. Everyone can keep playing |
| Career mode | The player's own world-first rewards, contracts and funds work exactly as in single player, whatever the board says |
| Somebody edited a shared file by hand | Every game notices: the edited state is not applied, the player's name gets a "files changed" mark, and a player whose own files were touched has them restored |
| The shared folder is offline | The game plays as single player with the last known rivals; sharing resumes when the folder returns |

### 1.2 Worked example

Race "Mun or Bust", Science mode, three weeks of real time. Ana created it, Bogdan and Carol
joined. Sites: Ana at the stock KSC, Bogdan on the opposite side of Kerbin, Carol a quarter turn
away. Looks: Ana the base suit, Bogdan the slim suit, Carol the vintage suit.

```
Real day 1    Ana flies to orbit on game day 3 and commits. Her standing 1
              lists that flight and "Kerbin/Orbit: day 3".
Real day 2    Bogdan, who had not synced yet, reaches orbit on game day 2.
              His standing 1 lists "Kerbin/Orbit: day 2".
Sync          Every machine reads both standings. Board: Kerbin orbit is
              Bogdan's (day 2 beats day 3). Ana sees one message, and from
              now on a slim-suit ghost rocket lifts off from the far side of
              Kerbin on day 2 of HER clock whenever she rewinds past it.
Real day 5    Carol lands on the Mun on game day 40. She holds Mun Landing.
Real day 9    Ana is at game day 55. The board shows Carol's day 40. Ana
              rewinds to day 20, where her tech is thinner, builds a lighter
              lander, and lands on game day 31. She commits; her standing 6
              lists "Mun/Landing: day 31". The first is hers. Carol's lander
              ghost still touches down on day 40 in everyone's game; it is
              simply no longer the first.
Real day 12   Carol quickloads to a save from before her Mun flight to try a
              different approach. Her next standing no longer lists the day
              40 flight or its milestones. On every machine her lander ghost
              and her flag are gone and the board forgets day 40. Nobody
              else's game changes in any other way: nothing of theirs ever
              depended on Carol's flight.
Real day 21   The end date passes. The board counts each player's last
              standing shared before it: Ana 14 points, Bogdan 9, Carol 6.
```

Ana's ledger never saw a row of Bogdan's or Carol's. In her own game she was paid her own
science for her own orbit on day 3 and it was never taken back when Bogdan's day 2 arrived: the
board is a comparison, not an economy.

---

## 2. Design principles

1. **Additive only.** A rival's data adds ghosts, scenery, readable flags and board rows. It is
   never an input to anything the local game computes or saves: not the ledger walk, not a
   pool, not the roster, not a vessel, not a contract, not tech, not a facility, not a
   reservation, hold or gate. This is the isolation invariant, and section 6.4 turns it into a
   mechanical gate. Remove the shared folder and the save is a normal, consistent single-player
   save.
2. **No interaction, so no paradox.** Nothing a player does can touch a rival's object, and
   nothing a rival does can change a player's game. There is therefore nothing to arbitrate,
   nothing to hold, and nothing to take back. Every conflict rule of the old co-op design
   exists to resolve an interaction this design does not have.
3. **A player's public state is one snapshot.** Each export is the player's complete public
   state at that moment. The latest one is the truth about that player; older ones are
   history. An importer never has to replay a log in order, and a missing older file never
   matters.
4. **Same files, same board.** Given the same set of shared files, every machine derives the
   same board. No machine is special and there is no server.
5. **Game date is the race clock; real time only bounds the race.** Firsts are compared by game
   date. Time warp gives no advantage, because it spends game days. The optional end date is
   the only place real time enters.
6. **A first is derived, never stored.** The board is recomputed from the standings at every
   sync. Taking a first, losing one and getting one back are all the same recomputation.
7. **Single-player rules hold unchanged inside a race.** Rewind, Re-Fly, quickload, the ledger
   safety rules and the committed-value rule work on a player's own data exactly as they do in
   single player. Nothing a rival does can lower a value a player earned, because nothing a
   rival does reaches the ledger.
8. **One writer per file, forever.** Every file in the shared folder is written by exactly one
   player. The sync service never sees a conflict.
9. **An isolated mode.** Multiplayer saves are created only through the Multiplayer menu and
   carry a mark. Every line of multiplayer code runs behind that mark. A single-player save
   never becomes a race save and a race save never stops being one.
10. **Disclose, do not block.** A race is played among friends. Different mods, changed
    difficulty settings and cheat menu use are shown next to the player's name. Nothing is
    refused for them.
11. **Reuse before invention.** Ghost playback, map presence, flag replay, the milestone rows
    and their once-ever walk, the log-table window template and the safe-write file helpers
    already exist. This feature adds the exchange, the sites, the looks, the board and its
    window.

---

## 3. Terminology

| Term | Definition |
|------|------------|
| Race | One competition: a manifest, a set of players, a ladder, optional end date and house rules, rooted in one shared folder. |
| Creator | The player who created the race. They write the race manifest once. They have no other special role. |
| Player id | Stable GUID identifying a player, generated at first setup. The display name is separate. |
| Rival | Any other player in the race, as seen from the local player. |
| Race save | A save created through the Multiplayer menu. A normal Sandbox, Science or Career save with a race mark in the PARSEK node. |
| Site | One of four fixed space center locations on Kerbin's equator. Site 0 is the stock KSC. |
| Home site | The site a player is assigned. Their working space center stands there. |
| Alternate launch site | A stock non-KSC launch site (the Dessert pad and airfield, Woomerang, the Island airfield). Common to all players. |
| Launch area | The pad and the whole runway of each of the four sites, and the pad or airstrip of each alternate launch site, with a margin. Part of the site table, so identical in every game. A rival's leftovers inside one are never shown (section 7.7). |
| Look | A player's suit style and the one UI color that goes with it. Four looks, one per player. |
| Shared folder | The folder synced by the players' file-sharing service. Parsek reads all of it and writes only the local player's subtree (the creator also writes the manifest, once). |
| Standing | One immutable export file: a player's complete public state at one moment (section 6.1). |
| Payload | The recording files a standing refers to, stored once per recording under the player's subtree. |
| Rival store | The in-memory, read-only set of rivals' recordings, flags and milestones built from the latest complete standing of each rival. Not part of the save. |
| Ladder | The fixed list of milestones that count in a race, each with a point value (section 7.9). |
| First / holder | For a ladder milestone, the earliest game date any player reached it, and the player or players who did. |
| Board | The derived table of every player's date for every ladder milestone, the holders and the points. |
| Frontier | The latest game date a player has committed anything at. Shows whether a rival is pushing forward or polishing early dates. |
| End date | The optional real-time moment after which the board is final. |
| House rule | An optional race setting chosen by the creator, for example "no retakes". |

---

## 4. Mental model

### 4.1 The shared folder

```
<SharedFolder>/                     (one sync service per race)
  race.cfg                          single writer: the creator, written once
  players/
    p_ana/                          single writer: Ana, forever
      player.cfg                    identity, look, site slot, join stamp
      standings/
        000006.pstd                 one immutable file per export
        000005.pstd
        ...
      payload/
        <recordingId>/              written once, before the standing that
          meta.cfg                  first names it; never changed
          <recordingId>.prec
          <recordingId>_ghost.craft
        trees/
          <treeId>_<hash>.cfg       tree topology, named by content hash
    p_bogdan/
      ...
```

And on each player's own machine, beside the save, never synced and never named by the save:

```
saves/<save>/Parsek/Race/
  local.cfg                         this machine's settings: the shared folder
                                    location, the show-ghosts toggles, the digest
                                    of the last export
  own/standings/                    a copy of every standing this player exported
  rivals/<playerId>/                the local copy the game plays from:
    applied.pstd                    the standing in effect for that rival
    payload/...                     the recording files it names
    witness.cfg                     what this machine has seen of that rival (7.15)
```

### 4.2 Data flow

```
 OWN SAVE (authoritative, private)             SHARED FOLDER
 ---------------------------------             -------------
 committed recordings, effective  --export-->  players/<me>/payload/...
 ledger milestones, flags, look                players/<me>/standings/N.pstd
        ^
        |  nothing ever flows back
        |  into the save
        |
 RIVAL STORE (derived, read-only)  <--import-- players/<rival>/standings/max
 rival recordings, flags,                      + the payload it names
 milestones, looks, sites                      (copied to a local cache)
        |
        +--> ghost playback (flight, map, tracking station, KSC)
        +--> rival flags (readable objects, never saved)
        +--> the board --> Space Race window, one-shot messages
```

### 4.3 What crosses and what never does

| Shared (public state) | Never shared |
|-----------------------|--------------|
| Committed, effective recordings: trajectory, part events, ghost snapshot, flag events | Funds, science, reputation and every ledger row except the milestone dates below |
| Tree topology (so debris and stages replay with their parent) | Contracts, strategies, tech, part purchases |
| Vessel and mission names | The roster, crew assignments, kerbal stats |
| Milestone table: ladder milestone id, game date reached, the recording that reached it | Vessel snapshots used for spawning (rivals never spawn) |
| Facility levels (Career, for the look of the rival's space center) | Rewind points, quicksaves, unmerged flights, an open Re-Fly session |
| Look, display name, site slot | Supply routes and their cargo |
| Disclosure facts: mod list, difficulty settings digest, cheat use | Anything from a save that is not a race save |
| Frontier (latest committed game date) | |

### 4.4 Read from the shared folder, or copy?

The owner asked for this to be analyzed. The choice is where the game takes a rival's files
from while it plays.

| | Read directly from the shared folder | Copy into the save's folder, play from the copy |
|---|---|---|
| Folder offline, drive unplugged, sync client closed | Rivals vanish, or the game stalls on a missing path | Nothing changes; the last synced rivals keep playing |
| Cloud "online-only" files | The first read of a file downloads it, which can freeze the game for seconds, possibly in the middle of a flight | The download happens during a sync, in small budgeted steps, never while a ghost needs the file |
| A file changes or disappears while a ghost is playing it | Possible: the sync client rewrites files whenever it likes | Impossible: the copy changes only when Parsek applies a new standing, at a scene change |
| Half-synced state | Must be handled on every read | Handled once, at copy time: a standing is copied only when everything it names has arrived and matches its hash |
| Noticing edited files (7.15) | Nothing local to compare against | The copy and the witness log are the comparison |
| Disk space | None extra | One copy of each rival flight in use (flights are small: trajectories and one craft snapshot each) |
| Freshness | Always the folder's latest | The latest as of the last sync; a sync runs at every scene change, on a slow poll, and on Sync now |
| Moving or renaming the shared folder | The game breaks until the path is fixed | The game plays on; only sharing pauses until the path is fixed in Settings |

**Decision: copy.** The shared folder is transport only. Parsek touches it at sync moments
and nowhere else; everything the game shows comes from the local copy. The same holds in the
other direction: an export is written to the local `own/` folder first and then copied out,
so a full or missing shared folder never loses an export, it only delays it.

The copy is a cache, not data: deleting `Race/rivals/` costs nothing but a re-copy at the next
sync, and it is never included in a save, a quicksave or a backup of the save file itself.
What it keeps is bounded: only what each rival's standing in effect names, plus a grace
period for files a newer standing dropped.

**Where the location is stored.** In `local.cfg` beside the save, not inside the save file.
The location belongs to the machine (the same race folder is `D:\Dropbox\Race` on one PC
and `/home/ana/Dropbox/Race` on another), and a setting stored in the save file would be
rolled back by a quickload or a rewind to whatever it was when that save was written.

---

## 5. What is kept from the co-op design

The deprecated co-op design (`docs/dev/done/design-coop-async-multiplayer.md`) had two halves.
The exchange half carries over in simplified form. The interaction half is gone, because there
is no interaction.

| Co-op design element | Here |
|----------------------|------|
| Shared folder, one write-only subfolder per player, no server | Kept (4.1) |
| Player identity: stable id plus display name | Kept (7.1) |
| Payload written first, envelope last, hashes gate integrity | Kept (6.3) |
| Safe-write with retry, cloud-placeholder and sync-conflict-copy handling | Kept (9) |
| Missing-parts ghosts render with the absent parts skipped | Kept (7.6) |
| Ordered packet log, per-player seq with gap detection, import journal in the save | Replaced by latest-snapshot-wins standings; no ordering between files matters (principle 3) |
| Retirement records for rewound or re-flown flights | Dropped: the next standing simply no longer lists the flight |
| "Exported" freeze on the optimizer and on ledger ordering fields | Dropped: payloads are named by id and content hash, so a rewritten recording is just a new payload |
| Foreign recordings as citizens of the committed list, with an ownership fence | Replaced by a separate rival store that the save never contains (6.4) |
| Checkpoint 0: cloning the founder's save | Dropped: every player starts a fresh save from the race manifest |
| Contract and applicant regeneration on join | Dropped (fresh saves) |
| Foreign vessel spawning and full control, tip claims, the arbitration fold, the salvage ladder | Dropped |
| Pooled economy: merged ledger walk, once-ever cross-player spends, merge debt, shared reservation, capture key, baseline pinning | Dropped |
| Held earnings, finality, heartbeats, away marks | Dropped |
| Crew ownership, crew claims, stand-in convergence, roster cap rules | Dropped: rosters are private |
| "Shared stays shared" after a load | Dropped: nothing depends on a shared flight, so a load can unshare it |
| Mod allowlist | Replaced by disclosure (principle 10) |
| Attribution in the timeline and Recordings windows | Replaced by the Space Race window; single-player windows are unchanged |

New in this design: the race manifest and setup dialog, the main menu entry and the race mark,
the four sites with terrain and scenery, the looks, the ladder and the board, the Space Race
window, the end date and the house rules, readable rival flags, persistent end-state ghosts.

---

## 6. Data model

All exchange files are KSP ConfigNode text. Names below are proposals; the implementation plan
fixes them.

### 6.1 New types

```
RaceManifest (race.cfg, written once by the creator)
  raceId: string            - "race_" + Guid("N")
  name: string
  raceFormat: int           - version of the exchange format (starts at 1)
  createdRealTime: string   - fixed-format UTC timestamp (6.5)
  gameMode: string          - SANDBOX, SCIENCE_SANDBOX or CAREER
  DIFFICULTY { }            - the stock GameParameters node every save is created from
  difficultyDigest: string  - hash of that node, for disclosure
  ladderId: string          - which ladder table applies ("standard-1")
  endRealTime: string       - optional; empty = no limit
  noRetakes: bool           - house rule (7.11)
  siteTableId: string       - which site table applies ("kerbin-equator-1", 7.3)
  parsekVersion: string     - informational
  MOD_LIST { MOD id version ... } - the creator's mods, informational

PlayerManifest (players/<id>/player.cfg, single writer = that player)
  playerId: string          - "p_" + Guid("N")
  displayName: string
  look: int                 - 0..3 (7.4)
  siteSlot: int             - 0..3 (7.3)
  joinedRealTime: string

Standing (players/<id>/standings/<seq>.pstd, immutable, written LAST)
  seq: int                  - per-player, rising
  playerId: string          - must match the subtree
  exportedRealTime: string  - the writer's clock (6.5)
  prevHash: string          - content hash of this player's previous standing
                              (empty for the first); makes the standings a chain (7.15)
  frontierUT: double        - latest committed game date
  raceFormat, recordingFormat, recordingSchemaGeneration: int
  RECORDINGS { REC id hash treeId ... }      - every effective committed recording
  TREES { TREE id hash ... }                 - their trees
  MILESTONES { M id ut recordingId ... }     - the player's ladder table (7.8)
  FLAGS are inside the recordings (flag events), not listed separately
  FACILITIES { F id level ... }              - Career only
  NAMES { }                                  - mission and vessel names
  DISCLOSURE { modDigest, MOD ..., difficultyDigest, cheatsUsed }
  SEEN { RIVAL playerId seq hash ... }       - highest rival standing imported
                                               when this one was written, with its
                                               content hash: a receipt (7.11, 7.15)
  PART_MANIFEST { }                          - distinct part names used

RaceLink (in the save: PARSEK node, child RACE; the race mark)
  raceId, playerId, displayName, look, siteSlot
  siteTableId, ladderId     - so sites and ladder never depend on the folder
  gameMode, difficultyDigest
  (identity and choices only: nothing here ever changes after the save is created,
   so every kind of load can take it from the loaded save as is)

RaceLocal (saves/<save>/Parsek/Race/local.cfg; this machine only, not in the save)
  sharedFolderPath: string  - chosen in the setup dialog, editable in Settings (7.2)
  lastStandingDigest: string - hash of the last exported public state
  SHOW { RIVAL playerId bool ... } - the show-ghosts toggles

RivalCache (local files under saves/<save>/Parsek/Race/rivals/<playerId>/, not named
            by the save, never exported; rebuilt from the folder if deleted)
  the applied standing and the payload files it names
  witness.cfg: seq and content hash of every standing of that rival this machine
               has applied, with the local time it first saw each (7.15)

OwnMirror (local files under saves/<save>/Parsek/Race/own/, never synced)
  a copy of every standing this player exported, to detect and undo changes
  somebody else made to this player's subtree in the shared folder (7.15)

Board (derived, runtime only; section 7.8)
```

### 6.2 Changes to existing types

None of the recording, tree or ledger types gain a field. In particular there is no owner tag
on `Recording`, `RecordingTree` or `GameAction`: a rival's recording never becomes one of those
in the local stores (6.4).

`ParsekScenario` saves and loads the `RACE` node. It is an additive optional child of the
PARSEK node, so by the save-format policy it needs no format bump. A save without it is a
single-player save.

### 6.3 Exchange format

- **Completeness protocol.** Payload files are written first, the standing last. An importer
  ignores payload nobody names, and treats a standing whose named files are missing or fail
  their hash as "not complete yet": it keeps showing the previous applied standing for that
  rival and retries at the next sync.
- **Latest complete standing wins.** For each rival the importer applies the highest-seq
  standing that is complete. Older standings are never applied after a newer one. A gap in seq
  numbers means nothing.
- **Seq allocation** is folder-derived at every export: highest seq in the own `standings/`
  directory plus one. A save rolled back by a load therefore never re-uses a number.
- **Payload identity.** A recording's payload directory is named by recording id and its
  standing entry carries a content hash. If the local optimizer or a Re-Fly rewrites a
  recording, the next standing names the new id or hash and the exporter writes a new payload.
  Nothing is frozen on the owner's side.
- **Owner cleanup.** A player's own payload that none of their last few standings names may be
  deleted by that player's exporter after a grace period. Importers tolerate it: they only
  ever need what the latest complete standing names.
- **Versioning.** `raceFormat` versions the exchange files. A standing with a newer
  `raceFormat`, or recordings in a newer recording format than the local build reads, is not
  applied: the rival's board rows are still shown if the milestone block parses, their ghosts
  are not, and the Space Race window says "Ana runs a newer Parsek".
- **Id safety.** Every id that becomes part of a path passes
  `RecordingPaths.ValidateRecordingId` first. A standing with an unsafe id is refused whole.
- **Local writes** into the shared folder use `FileIOUtils` safe-write with retry and backoff
  (sync clients hold brief locks).

### 6.4 The isolation invariant

One rule: **no code outside the multiplayer module and the ghost display hosts may read the
rival store, and no rival data is ever written into the save.**

- Rival recordings live in the rival store, a separate in-memory structure. They are not in
  `RecordingStore.CommittedRecordings` or `CommittedTrees`, not in ERS or ELS, and have no
  ledger rows locally.
- They therefore cannot reach, by construction: the ledger walk and the pools, the click
  reservation, crew reservation and stand-ins, ghost chains and vessel holds, the claimed
  vessel disposal gate, the spawn gate and every spawn path, rewind points and Re-Fly, the
  optimizer, Missions, Logistics (route sources and endpoints), the own Timeline window and
  the Recordings table.
- The consumers of the rival store are exactly: the ghost playback hosts (flight, map,
  tracking station, KSC scene), the rival flag presenter, the rival space center scenery
  (facility levels), the board, and the Space Race window.
- Rival objects that are real KSP objects for a time (flag vessels, map presence proto
  vessels) are created at load from the rival store and are stripped before every save, the
  way ghost map vessels are today (VERIFY: the existing strip path and that a flag vessel can
  ride it).

Mechanical gates, so the invariant stays true as the code changes:

- Module rules in `scripts/arch/modules.toml`: the ledger, roster, recorder, rewind, mission,
  route and ghost modules are forbidden to reference the race modules (`[forbidden]` edges,
  listed in 15.4).
- A grep-audit gate in the `GrepAuditTests` family: the rival store's type names appear only
  in an allowlisted set of files.
- A headless test: load a race save with a populated rival cache, run the ledger walk and the
  save writer, and assert the outputs are byte-identical to the same save with an empty
  cache.

How the invariant is built into the module structure, the rival data type and the rival
ghost lane is section 15 (15.4 to 15.6).

### 6.5 Timestamps

Real-time stamps use the fixed-width invariant format `yyyy-MM-ddTHH:mm:ssZ` (UTC), compared
after parsing. A standing whose stamp does not parse is refused. A player's standings carry
rising stamps: a standing is stamped `max(now, previous own stamp + 1 s)`.

Real time is the writer's own clock. It decides only two things: which standing is a player's
last before the end date (7.10), and the "no retakes" bookkeeping (7.11). Section 7.12 says what
is disclosed when clocks look wrong.

---

## 7. Behavior

### 7.1 The Multiplayer mode and the race mark

The stock main menu shows, after Start Game, a list of entries (Start New, Resume Saved,
Training, Scenarios). Parsek adds one more, **Multiplayer**. It opens the race setup dialog
(7.2).

A race save is a normal KSP save of mode Sandbox, Science or Career, created by Parsek from the
race manifest. KSP's game-mode list is fixed, so "Multiplayer" is not a fourth KSP mode: it is
the `RACE` node in the save's PARSEK node. One predicate, the race gate, answers "is this a
race save" from that node, and every multiplayer code path runs behind it.

- A race save is created only by the setup dialog. There is no way to turn an existing save
  into a race save: every player starts at day 1, which is what makes game dates comparable.
- A race save appears in the stock Resume Saved list like any save (its name carries the race
  name). Loading it there is the same as continuing it from the Multiplayer dialog.
- The sites, the terrain patches and the scenery (7.3) exist only while a race save is loaded.
  Returning to the main menu or loading a single-player save restores the stock Kerbin.
- Single-player saves never read a shared folder and never show a multiplayer surface.

### 7.2 The race setup dialog

One dialog in the stock dialog style, with three actions.

**Create a race.** The creator sets:

- race name;
- game mode: Sandbox, Science or Career;
- difficulty, through the stock difficulty options panel (the same one Start New uses); the
  resulting settings go into the manifest and every player's save is created from them;
- the shared folder's location, in the same dialog as the name and the look: a path field
  (typed or pasted) with a simple folder picker beside it and a Validate button (exists,
  writable, empty of any race);
- optional end date (real time);
- house rules: "no retakes" (7.11);
- their own display name and look.

Then Parsek writes `race.cfg`, the creator's `player.cfg`, creates the save, and starts it at
the creator's home site (slot 0, the stock KSC).

**Join a race.** The player picks the shared folder's location the same way. The dialog
then shows the race name,
mode, end date and house rules, and who has joined with which look. The player enters a
display name, picks one of the free looks (taken ones are greyed), and sees the site they will
get. Join writes their `player.cfg`, creates their save from the manifest, and starts it at
their home site. A joiner who finds differences between their mods and the creator's list sees
them named, as information.

**Joining later** (owner ruling 2026-11-14). A race always has four seats and there is no
player count to set. Join works at any time while a seat is free: an hour after the race was
created or two months in. Sharing the folder is the invitation. Nothing changes in anyone
else's save when somebody joins: their site's buildings were already standing in every game,
so the newcomer only adds a column to the board, ghosts to the sky and one on-screen message
at the next sync ("Dan joined the race"). What a late joiner gets:

- A fresh save at day 1, like everyone. Game dates stay comparable because every save starts
  at day 1; the newcomer is behind only in real time.
- Everything the others shared so far: their flights replay as ghosts as the newcomer's clock
  passes each one, what they left behind appears at its game date, and the board already
  shows every date to beat.
- The same chance at every first: a first belongs to the earliest game date, so the newcomer
  takes one by reaching it earlier in game time than its holder did. Under the "no retakes"
  house rule a late joiner has seen everything already shared, so they can only take firsts
  nobody has reached yet; the Join dialog says so before they commit.
- A first sync that may be large. It is spread over several polls with progress in the status
  text, and the game is playable meanwhile.

A join after the end date is allowed: the final board is unchanged and the newcomer plays
"after the end" (7.10).

**Continue.** Lists the local race saves with race name, own look and last sync, and loads the
chosen one.

Refusals, each with its exact reason: the folder is missing or not writable; the folder has no
race (Join) or already has one (Create); the race is full; the race's `raceFormat` is newer
than the build; a `players/<id>/` subtree already exists for this player id (then the dialog
offers Rejoin: a fresh save bound to the existing subtree, seq continuing from the folder).

In a race save the existing Settings window gains a Multiplayer section: the shared folder
location, Sync now, and status text (last sync, rivals' last shared time, anything not yet
complete).

**Changing the location in Settings** (owner ruling 2026-11-14). The location can be edited at
any time; the new one is validated before it is accepted:

- It holds this race (same race id): accepted. This is the normal case, a folder that was
  moved, renamed or mounted under another drive letter.
- It is empty: accepted after a confirmation, as "move the race here". Parsek writes the
  race file and the player's own subfolder there from its local copies. The other players
  point their games at the new folder the same way, and each brings their own files.
- It holds a different race, or is not writable: refused with the reason.

Changing the location never touches the save or the local copy of the rivals. While the
location is wrong or unreachable the game plays on with the rivals it has, and the status
text says sharing is paused and why.

### 7.3 The four sites

**The site table.** Four fixed sites on Kerbin, identical in every race save, shipped with the
mod and named by `siteTableId`:

- Site 0: the stock KSC (latitude -0.097, longitude -74.56).
- Site 1: the opposite side of Kerbin (target longitude near 105 E).
- Site 2 and site 3: a quarter turn either way (targets near 15 E and 165 W).

The targets are where the sites would be if spacing were exact. The real coordinates come from
a terrain survey (section 12, task S1) and must satisfy: dry land under the whole footprint;
the same latitude as the KSC, so every player launches into the same inclination with the same
rotation speed; ground elevation close enough to the KSC's that the flattened patch can sit at
exactly the KSC's altitude, so nobody launches from thinner air; room for the full footprint
at the KSC's orientation (pad to the east). A target that fails moves along the equator to the
nearest spot that passes.

**Slot assignment.** Slots are taken in the order 0, 1, 2, 3: two players are opposite each
other, the third and fourth take the quarter turns. A late joiner never moves anyone, which
matters because flights already recorded launched from a fixed place. Three players therefore
sit at 0, 180 and 90 degrees rather than 120 apart; stability is worth more than symmetry.

Two players who join at the same moment, before the folder synced, can claim the same slot or
the same look. The rule is deterministic: the earlier `joinedRealTime` keeps it (ties by
player id); the other is moved to the next free slot or look with one on-screen message. This
is applied only while the losing player has not committed a flight. After that both keep what
they have (two players sharing a site only means overlapping ghosts) and the Space Race window
says so.

**The home site is a working space center.** In each player's game the working KSC stands at
their home site: the Space Center scene, the VAB and SPH, the launch pad and runway, recovery
distance and every stock facility behave as they do at the stock location. This is done by
moving the stock space center object and its flattening terrain patch, the technique the
KSCSwitcher mod has used for years (section 12). Only one working space center per game is
needed, because each player runs their own game.

**The other three sites are scenery.** At each other site stands an exact visual copy of the
KSC buildings: solid, not clickable, not destructible, not upgradable. In Sandbox and Science
they show the fully built look. In Career each shows its player's facility levels from their
latest standing (cosmetic; a site with no player yet shows level 1). An unassigned site still
has its buildings, so the world looks the same in every game.

**The terrain is the same in every game.** All four flattened patches exist in every race
save, whoever is playing. A rival's ghost rolling down its runway must sit on the same ground
in every game.

**Who launches where** (owner ruling 2026-11-14):

- Your own KSC: only you. The stock launch flow from the VAB and SPH puts your vessel on your
  home site's pad or runway. There is no way to launch from a rival's site.
- The stock alternate launch sites (the Dessert launch site and airfield, Woomerang, the
  Island airfield): common. Any player can launch from them, exactly as stock offers them. By
  default the race's difficulty settings enable them from the start, so nobody has to travel
  farther than a rival to unlock one (open question 13.6).

**Launch areas.** The site table also lists the launch areas: for each of the four sites the
pad (a circle around the pad's spawn point) and the runway (the whole strip, not only its
threshold), and for each stock alternate site its pad or airstrip, each with a margin.
Proposed sizes: 100 m around a pad; the strip's own length and width plus 50 m around a
runway. The exact numbers are measured with the sites (tasks S1 and S4). Because they are in
the site table, every game agrees on them. They are the exclusion areas of section 7.7.

**Equal science at every site.** The KSC area and its buildings are science locations of
their own (the KSC biome and the building mini-biomes), worth real science in the first hours
of a Science or Career race. Every player must find the same ones at their home site. The
building mini-biomes come from the buildings themselves, so they move with the working space
center (VERIFY). The surrounding KSC biome comes from Kerbin's biome map and stays at the
stock location, so the design requires a biome override in a radius around each player's home
site (VERIFY the read paths; task S2). Until that is proven, treat it as the main fairness
risk of Science and Career races.

Longitude itself is fair: every site has the same latitude and altitude, interplanetary
windows are the same for everyone, and the Mun's position relative to a site differs only by
a fraction of Kerbin's six-hour day.

### 7.4 Looks

A look is a suit style plus one fixed UI color. There are four, one per player, taken from the
stock suits (owner preference: stock first):

| Look | Suit | Needs |
|------|------|-------|
| 0 | Base suit | nothing |
| 1 | Slim suit | nothing |
| 2 | Vintage suit | Making History |
| 3 | Future suit | Breaking Ground |

Each look's UI color is fixed by the mod to match the suit's dominant color (task S3 picks the
four colors in game). The color is used for that player's ghost labels and map icons, orbit
lines, the site marker, and their column and cells in the Space Race window.

**Where a DLC is missing.** Stock lets a mod add suit color sets for a base-game suit
(`SUITCOMBOS` configuration). Parsek ships two stock-like replacement sets, one resembling the
vintage suit and one resembling the future suit, built on a base-game suit. A machine that
lacks the DLC uses the replacement wherever that look would show: on the player's own kerbals
if they picked it, and on a rival's ghost kerbals. A look is therefore always available and
always distinct, with or without DLC.

**Applying the look.**

- Own kerbals: every kerbal of the player's roster wears the player's look. Parsek sets it
  when the save is created, when a kerbal is hired or rescued, and at load. The player's own
  roster is otherwise untouched.
- Rival ghost kerbals (EVA ghosts) are drawn with the rival's look (VERIFY: today's EVA ghost
  is built from the stock EVA part model with its default texture; task S3).
- Rival vessels keep their own appearance. Their label, map icon and orbit line carry the
  color.

Rosters are private. Each player has their own Jebediah; a rival's kerbals exist in the local
game only as names on ghost labels, so equal names never collide.

### 7.5 Export

A player's public state is exported as a new standing whenever it changed. At each sync tick
(below) the exporter computes a digest of the public state: the effective committed recording
set with content hashes, the milestone table, names, facility levels, disclosure facts. If it
differs from `lastStandingDigest` in the local race file, it writes any new payload, then the
standing (to the local `own/` folder first, then to the shared folder), and stores the new
digest.

What "effective committed" means is exactly what single player already means by it (ERS): a
flight that was merged and is not superseded, retired or discarded. So:

- An unmerged flight, a flight awaiting its merge dialog and an open Re-Fly attempt are never
  shared.
- A Re-Fly merge replaces the superseded flight in the next standing.
- A quickload that drops flights drops them from the next standing (principle 7; the co-op
  rule "shared stays shared" is gone because nothing depends on a shared flight).
- A rewind changes nothing by itself: committed flights in the player's future are still
  theirs and stay shared.

Sync ticks: save load, entering the Space Center or the Tracking Station, after a tree commit
or a Re-Fly merge, a low-frequency poll in the Space Center, Tracking Station and map view,
and the Sync now button. An export failure (folder offline) is silent apart from the status
text and one log warning; the next tick retries, and because a standing is a full snapshot
nothing queues.

### 7.6 Import and the rival store

At each sync tick, for each rival subtree: find the highest-seq standing, check it is complete
(6.3), copy any payload not yet in the local rival cache, and record it as that rival's
applied standing. Then rebuild that rival's part of the rival store and recompute the board.

- **The board updates at once**, in any scene.
- **Ghosts update at scene changes.** While the player is in flight the new rival ghosts wait
  for the next scene load, so nothing appears or vanishes around a flying vessel.
- **The rival cache is outside the save.** A quickload, a rewind or a revert does not roll
  rivals back: their world does not depend on the local timeline. At every load the rival
  store is rebuilt from the cache and the folder.
- **Missing parts.** A rival recording that uses parts the local install lacks plays as a
  ghost with those parts skipped, and the Space Race window notes "Carol uses parts you do not
  have".
- **Removal.** A recording the rival's latest standing no longer names leaves the rival store;
  its ghost, its end-state object and its flags go with it. Cached payload files nobody names
  are deleted after a grace period.

### 7.7 What a rival's world looks like

Rival ghosts play at the game dates they were recorded, against the local player's clock.

- **A flight in the local past** is over: only what it left behind shows.
- **A flight in the local future** plays when the local clock reaches it, like any committed
  future flight of the player's own.
- **After a flight ends** its end state persists as a ghost from that game date on: a vessel
  left in orbit stays on its orbit with an orbit line and map icon in the rival's color; a
  vessel left on a surface stays where it stood. A recovered or destroyed vessel leaves
  nothing. Rival vessels never become real vessels: they have no collision, cannot be docked
  to, boarded, grabbed, transferred to, recovered or terminated, and no stock action reaches
  them (the existing ghost guards). They can be targeted for navigation, as ghosts can today
  (open question 13.4).
- **Flags** are the exception the owner asked for. A rival's planted flag appears from the
  game date it was planted as a real flag object, so that a kerbal on EVA can walk up and read
  its plaque with the stock Read Plaque action. Everything else on it is blocked: it cannot be
  taken down, picked up, renamed, recovered or deleted, and it is never written into the save.
  Parsek already replays its own flags as real flag objects; this is the same mechanism with
  the removal actions switched off (VERIFY: the stock flag's take-down and pick-up events can
  be disabled per flag). A rival flag must not become an obstacle either: it is clickable but
  a vessel or a kerbal passes through it (VERIFY: a click target without a physical collider;
  task S7).
- **Launch areas stay clear** (owner ruling 2026-11-14). Whatever a rival left inside a launch
  area (section 7.3) is not shown to other players: no end-state ghost, no map icon, no orbit
  or surface marker, no flag. The rule is: a rival vessel whose flight ended at rest on the
  surface with its end position inside a launch area leaves nothing visible, and a rival flag
  planted inside one is not created. It applies to all four sites' pads and runways and to
  the alternate sites, in every game, and it is decided by the viewer's game from the
  recording's end position and the site table, so it does not rely on what the rival's game
  says. The owner still sees and uses their own vehicle in their own game (apart from the
  existing single-player rule below); the flight itself, its replay up to the moment it
  parks, and any milestone it reached are unaffected. A parked vehicle that later moves out
  of the area in a new flight is shown again from that flight on.

  This extends a rule single player already has: a flight that ends parked on the KSC pad or
  runway threshold, or on a stock alternate site, is retired and never becomes a real vessel
  (`SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement`, 50 m circles from
  `LaunchSiteExclusionZones`). The race rule is wider (whole runways, all four sites, flags)
  and applies to what is SHOWN, so it needs its own pure predicate over the site table rather
  than a wider single-player rule. In a race save the single-player circles must follow the
  moved home site (task S4).

  While a rival's flight is being replayed its ghost is shown wherever it is, launch areas
  included: a launch has to start on a pad, and a ghost cannot block anything. The one
  exception closes the loophole of a recording that never ends: a rival ghost that has stood
  still inside a launch area for more than 60 seconds of game time is hidden until it moves
  again, unless that area belongs to its own launch (a rocket waiting on its own pad before
  liftoff is shown). Open question 13.10.
- **Watching.** The existing watch camera works on a rival ghost. It is a camera, nothing
  else.
- **Hiding.** The Space Race window has one show-ghosts toggle per rival, for performance and
  for players who want a quiet sky.

### 7.8 Milestones and the board

**A player's milestone table.** For each ladder milestone, the earliest game date at which one
of the player's effective committed flights reached it, and that flight's recording id. It is
derived from data single player already keeps: the ledger's milestone rows, which exist in all
three game modes and which the once-ever walk already credits to the earliest date. After a
rewind and an earlier flight, the walk credits the earlier one, and the table follows.

**The replay is the proof.** A milestone entry counts only if the standing also lists the
recording it names and the date lies inside that recording. An entry that fails is shown as
"unverified" and scores nothing. Friends can also simply watch the ghost: clicking a cell in
the Space Race window starts the watch camera on that flight at that date. A deeper check of
the trajectory against the claim (on the Mun's surface and at rest, for a landing) is a later
hardening (open question 13.7).

**The board.** For every ladder milestone m and every player p, the date d(p, m) or nothing.
The first is the smallest date; its holder is every player with that date (equal dates are
co-holders and each scores the points). A player's score is the sum of the points of the
milestones they hold.

The board is recomputed from the applied standings at every sync and whenever the local
player's own table changes. Nothing about it is stored.

**All dates are shown** (owner ruling 2026-11-14): a rival's milestone appears on the board
with its game date as soon as it is shared, even when that date is ahead of the local clock.
You always know what you have to beat.

**Messages.** When a sync changes the holder of a milestone the local player held or now
holds, one on-screen message says so ("Bogdan took Mun Landing: day 30 beats your day 42",
"Mun Landing is yours again"). A sync that brings many changes sends one summary message; the
window has the detail. These are events that changed something, which is what a screen message
is for.

### 7.9 The ladder

The ladder is a fixed table shipped with the mod (`ladderId`), so every machine scores alike.
It is built from the stock world-first progress milestones, keyed by the same ids Parsek
already records (`Body/Step`, for example `Mun/Landing`).

Proposed shape, to be tuned by play (open question 13.1):

- **Steps**, with base points: flyby 1, orbit 2, landing 3, surface EVA or flag 1 more, return
  from orbit 2, return from the surface 4, plus the Kerbin firsts (first launch, reach space,
  orbit) at 1 each.
- **Bodies**, with a multiplier: Kerbin, Mun and Minmus 1; Duna, Ike, Eve orbit and Gilly 2;
  Moho, Dres and the Jool moons 3; Eeloo 4; the Eve surface return 6.
- **Left out:** the stock altitude, speed, distance and depth records. They are continuous
  values, they reward grinding, and they tie constantly.

The ladder is the same in all three modes. In Sandbox every part is available on day 1, so a
Sandbox race is about flying and planning; in Science and Career it is also about building the
science to unlock the parts early. Science is the expected default (it has the tech race
without the money).

### 7.10 The end date

A race may have a real-time end date. When the local real clock passes it, the board becomes
final: for each player it counts the last standing whose `exportedRealTime` is not after the
end date. The Space Race window names the winner and shows the final board. Everyone can keep
playing and sharing; later standings still drive ghosts and are shown as "after the end".

Without an end date the race simply continues, and the board always shows the present.

### 7.11 House rule: no retakes

The owner's optional rule: "first to get there wins, no one-upping". With no server and no
trusted clock, the rule is stated so that every machine computes it from the files alone:

**You cannot take a first you already knew about.** Each standing records, in its SEEN block,
the highest standing of each rival its writer had imported. A player's date for a milestone
counts for holding only if it first appeared in one of their standings written before they had
imported any rival standing that contains that milestone. Under this rule the holder is the
earliest such date. Two players who each reached the Mun before seeing the other's claim
compare by game date as usual; once a player has seen a rival's Mun landing, flying an earlier
one no longer takes it.

Standings are immutable and all of them stay in the folder, so each machine can read when a
date first appeared. A date that later disappears (its flight was rewound away) stops
counting, as always.

### 7.12 Disclosure

Each standing carries three facts, shown next to the player's name in the Space Race window
when they differ from the race's:

- **Mods:** a digest of the mod list, with the list.
- **Difficulty:** a digest of the save's difficulty settings; it differs from the manifest's
  when the player changed them in game.
- **Cheats:** true once any stock cheat option (infinite propellant, no crash damage and the
  like) was on while a flight was recorded. The flight's milestones are marked on the board
  (open question 13.5: mark only, or score nothing).

A standing stamped before the end date that the local machine first saw long after it is
marked "arrived late" locally. It still counts: the mark makes a wrong clock visible, which is
proportionate among friends.

### 7.13 The Space Race window

A new Parsek window, present only in race saves, opened from a button in the main Parsek
window. It follows KSP's UI scale like every Parsek window and reuses the log-table window
template.

- **Header:** race name; time left or "final"; one block per player in their color: name,
  points, frontier ("at day 61"), last shared time, disclosure marks, the show-ghosts toggle.
- **Table:** ladder milestones as rows, grouped by body; one column per player. A cell shows
  the game date that player reached the milestone, or nothing. The holder's cell is
  highlighted in their color; an unverified or cheat-marked cell is marked. A points column
  shows what the row is worth.
- **Click a cell:** watch that flight at that date.
- **View switch:** by ladder order, or chronological by game date (the "timeline" reading:
  what happened when, across all players).

It shows nothing of anyone's ledger.

### 7.14 Career, Science and Sandbox

Nothing is pooled, so the three modes differ only in what each player's own game has.

- **Sandbox:** no science, no funds. The board is the whole game.
- **Science:** each player earns and spends their own science. A milestone a rival reached
  first still pays the local player nothing less: stock pays science only for experiments, and
  those are private.
- **Career:** each player has their own funds, reputation, contracts and strategies. Stock's
  world-first rewards are paid by the player's own career when the player first reaches the
  milestone in their own game, whatever the board says. The board never writes to a ledger, so
  the ruling "never reduce what a player earned" holds by construction.

All players of a race play the same mode with the same starting difficulty settings, because
their saves are created from the same manifest.

### 7.15 Fair play: what stops someone editing the shared files

The shared folder is plain files that every player's sync client can write. Parsek's code is
open, and everything runs on the players' own machines with no server to referee. So nothing
here can make cheating impossible: a determined player can build a changed copy of the mod.
The aim is narrower and reachable: an edit made by hand to the shared files is NOTICED by
every other game, an honest player's files cannot be quietly changed by someone else, and a
claim on the board can always be checked by watching it. The first build ships the simple
version; the hardening steps are listed so the formats leave room for them.

**The simple version (first build).** Suited to friends who mean well.

1. **Everyone writes only their own subfolder.** The importer reads a player's data only from
   `players/<their id>/` and ignores everything else in the folder.
2. **Standings are numbered, never rewritten, and chained.** Each standing names the hash of
   its payload files and the hash of the same player's previous standing. Changing an old
   file breaks the chain behind it.
3. **Every game remembers what it saw.** For each rival, the local rival cache keeps the
   number and hash of every standing it has applied (the witness log, outside the shared
   folder). Every standing a player exports also lists the latest standing of each rival it
   had seen, with its hash: a receipt the other players hold for you. A rival standing that
   changed after it was seen, a chain that does not connect, or a number that went backwards
   is not applied: that rival's last good state stays, and the Space Race window marks them
   "files changed" with the reason. The honest causes (a restored backup, a reinstall) are
   fixed with Rejoin, which continues the chain from the folder.
4. **Your own files heal themselves.** At every sync the exporter compares its subfolder in
   the shared folder with its local copy of what it wrote. If someone else changed or deleted
   a file there, Parsek puts it back and says so once on screen.
5. **The replay is the proof** (7.8). A date on the board counts only when the flight behind
   it is shared and covers that date, and anyone can click the cell and watch it happen.
6. **Disclosure** (7.12): mods, changed difficulty settings and stock cheat use are shown
   next to the player's name.

What the simple version catches: a typo-level or text-editor change to any shared file,
anybody writing into somebody else's subfolder, a claimed date with no flight behind it, a
quietly replaced history. What it does not catch: a player who changes their OWN save or
recordings consistently before the mod exports them, or who runs a changed mod.

**Hardening, for later, in order of value.**

- **A. Signed standings.** Each player gets a key pair when they join; the public half is in
  their `player.cfg` and every other game pins it the first time it sees it. Each standing is
  signed. Then nobody can write a standing in another player's name even if the victim's game
  is not running to heal it, and a swapped key is itself a "files changed" event. (VERIFY that
  the signing classes of the game's runtime work in KSP; the format reserves a signature
  field from the start.)
- **B. Flight check.** Instead of trusting the claimed milestone table, each game checks a
  rival's claim against the recording itself and marks the cell when it does not hold:
  - *The claim check:* at the claimed date the trajectory really does what the milestone
    says (at rest on the Mun's surface for a landing, a closed orbit around Duna for an
    orbit).
  - *The teleport check:* the recording is continuous. The existing offline recording
    analyzer already holds such rules; they run over a rival's recording as they do over the
    player's own, and catch a craft moved by an editor or a debug tool.
  - *The sky check:* the planets and moons are a clock nobody can edit. A flight to the Mun
    or beyond crosses from one body's surroundings to another's at a place and time that only
    fit where those bodies were at that game date. A flight whose dates were shifted earlier
    by editing the file no longer lines up, and the check says so. This is what makes a
    faked earlier date hard beyond Kerbin orbit.
  - *The launch check:* the flight starts at that player's own site or at an alternate site.
- **C. Parts against the clock** (Science and Career). Each standing could list the game
  date at which each tech node was unlocked; a flight using a part before its node's date is
  marked. It only checks the player's story against itself, and it publishes tech progress,
  so it is the weakest step and optional.
- **D. A sealed race** as a house rule: only stock plus an agreed mod list, no cheat menu,
  fixed difficulty. With the checks above this turns disclosure marks into "does not score".

**What stays out of reach, said plainly:** a changed build of the mod, a player's own save
edited so that everything agrees with itself, and help from tools outside the game. Against
those the answer is the same as at a table of friends: the board shows every flight, and
anyone can watch it.

**On detection** the rule follows principle 10: mark and explain, keep the last good state,
never delete anything of a rival's, never touch the local player's game. Whether a marked
player's firsts still score is open question 13.11.

---

## 8. Edge cases

1. **A rival's standing is half synced.** Named payload missing or failing its hash: the
   previous applied standing stays in effect; retry at the next sync. Never partially applied.
2. **Sync-service conflict copies** (`... (1).pstd`, "conflicted copy"): ignored, named in a
   log warning. With one writer per file they can only come from misuse (case 3).
3. **Same player id on two machines.** Both write `players/<id>/`. Standings with equal seq
   and different content: the importer takes neither, warns naming the two-machine cause, and
   keeps the last good one. The dialog's Rejoin is the supported way to move machines.
4. **Two joiners claim one slot or one look.** Section 7.3: earlier join stamp keeps it, the
   other moves if they have not flown yet.
5. **The shared folder is offline or gone.** The save plays on with the cached rivals. If the
   folder is deleted for good, every player's save is still a complete game; relinking a new
   folder under the same race id restarts sharing from each player's next standing.
6. **A player quits the race.** Their last standing stays: their ghosts, flags and board dates
   remain, their slot and look stay taken. The window shows their last shared time.
7. **A player joins late.** They start at day 1 like everyone and are behind only in real
   time. Game dates are comparable because every save starts at day 1. Nobody else's save
   changes (7.2).
7a. **A fifth friend wants in.** Refused: four seats, four sites, four looks. A seat is not
   freed when a player stops playing, because their flights and dates stay in the race
   (open question 13.12).
7b. **Somebody edits a rival's standing in the shared folder to give them a worse date.**
   Every game that had already seen the real file refuses the changed one; the rival's own
   game puts the real file back at its next sync (7.15).
7c. **A player edits their own latest standing by hand to claim an earlier date.** The date
   has no flight behind it that covers it, so it shows as unverified and scores nothing; if
   an already-seen file was changed, the player is marked "files changed" (7.15).
8. **"I rewound and my rival's ghosts are still there."** Yes: rivals do not depend on your
   timeline. Their flights replay as your clock passes them again.
9. **"I quickloaded and lost a flight I had shared."** It leaves the race at the next sync
   (7.5). Fly it again; if it is earlier, so much the better.
10. **A Re-Fly of a flight that held a first.** The merged attempt replaces the flight. If the
    milestone now falls on a different date, the table and the board follow. An open attempt
    is never shared.
11. **Equal dates.** Co-holders; both score. Common for the opening firsts, rare later.
12. **A milestone reached with no recording behind it** (a row the ledger holds without a
    flight): listed as unverified, scores nothing.
13. **A rival uses parts I do not have.** The ghost draws without them and the window says so.
14. **A rival runs a newer Parsek.** Their board dates show if readable; their ghosts wait
    until the local build can read their recordings. The window says which.
15. **Two rivals launch from the same alternate site at the same game date.** Their ghosts
    overlap. Cosmetic.
16. **My vessel flies through a rival's space center.** The buildings are solid scenery, the
    same in every game. Crashing into them is a crash, as into any terrain.
17. **I land next to a rival's lander.** It is a ghost; nothing can touch it. Its flag can be
    read.
18. **A rival planted a flag where I am about to land.** The flag cannot be knocked over or
    removed, and it is not an obstacle: the lander passes through it (7.7).
18a. **A rival parks a huge craft on the runway of an alternate site, or drives a rover onto
    my runway and leaves it.** Nobody else sees it (7.7). In their own game it is retired or
    stays as their vessel, by the single-player rules.
18b. **A rival plants a row of flags across a launch pad.** Not created in anyone else's game
    (7.7).
18c. **A rival's base stands 200 m from a runway.** Outside the launch area, so it shows. The
    margins are the line; they are part of the site table and the same for everyone.
18d. **A rival's ghost taxis out, waits ten minutes on a shared airstrip, then takes off.** It
    is shown while it moves and hidden while it waits beyond 60 seconds, unless that airstrip
    is where the flight launched from (7.7).
19. **The end date passes while I am in flight.** The board freezes at the next sync. A flight
    committed after the end date is "after the end".
20. **My PC clock is wrong.** A standing stamped in the future still orders after the player's
    previous ones. Rivals see "arrived late" or a last-shared time in the future, and can say
    so.
21. **I change difficulty settings mid-race.** Allowed; disclosed (7.12).
22. **I open the race save with multiplayer code missing** (an older Parsek build): the `RACE`
    node is unknown data and is kept verbatim per the save-format policy; the sites are not
    applied, so vessels landed at a moved home site would be found on unflattened terrain.
    The build that adds multiplayer warns before an older one is likely to be used (VERIFY the
    downgrade story at planning).
23. **A single-player save is loaded after a race save in one session.** The sites, patches
    and scenery are removed before the single-player save's scene builds (7.1). A harness lane
    pins this.
24. **Kerbin is changed by a planet mod.** The site table assumes stock Kerbin. A race refuses
    to start on a modified Kerbin and says why (the one refusal besides format, because
    terrain parity is not a matter of taste).
25. **Three players; site 3 is empty.** Its buildings stand at level 1 and nobody launches
    from them.

---

## 9. Error recovery

| Failure | Recovery | Data loss |
|---------|----------|-----------|
| Export interrupted (crash, folder offline) | Payload without a standing is inert; the next tick writes a fresh standing | None |
| Shared-folder write hits a sync-client lock | Safe-write retries with backoff, then waits for the next tick | None |
| Corrupt or unsafe standing (bad hash, bad id, bad stamp, truncated) | Refused whole with the file and reason logged; the previous applied standing stays | None locally |
| Cloud placeholder payload (not downloaded) | Chunked, time-budgeted hydration across polls; a warning suggests keeping the folder available offline | None; slow |
| Rival cache deleted or damaged | Rebuilt from the folder at the next sync | None; the witness log restarts, so earlier changes to a rival's history can no longer be noticed by this machine |
| A rival standing changed after it was seen, or its chain does not connect | Not applied; the last good state stays; the player is marked "files changed" with the reason; Rejoin on their side repairs an honest cause | None |
| The local player's subfolder was changed by someone else | Restored from the local copy at the next sync, with one on-screen message | None |
| A load rolls the save back | The rival store is unaffected; the next export is a fresh snapshot of the rolled-back state | The dropped flights leave the race, as they left the save |
| Race manifest missing from the folder | The save keeps playing on its `RaceLink` (sites, ladder, mode); sharing pauses with a status note | None |

Default-safe throughout: when in doubt, do not apply the rival's new state, keep the old one,
and say exactly why.

---

## 10. Logging

Per the standing convention (`[Parsek][LEVEL][Subsystem] message`), with batch counters for
per-item loops. Proposed tags: `Race` (mark, setup, gate), `RaceSites` (move, patches,
scenery), `RaceExport`, `RaceImport`, `RivalStore`, `RaceBoard`, `RaceLooks`.

Logged at Info: race created or joined; each export (seq, counts, digest); each applied rival
standing (player, seq, recordings added and removed, milestones); every holder change; site
application and removal with coordinates; every refusal with its reason. Guard skips and
per-rival poll results at Verbose, rate-limited where they repeat.

---

## 11. Test plan

**Unit tests (pure, headless).**

- Standing codec round trip; refusal of bad ids, stamps and hashes.
- Latest-complete-standing selection, including incomplete newest and seq gaps.
- Slot and look assignment, and the simultaneous-join rule.
- Milestone table derivation from a ledger with rewinds, Re-Fly supersedes and discards.
- Board derivation: holders, co-holders, points, unverified entries, the end date cut.
- The no-retakes rule over standing histories.
- The launch-area predicate: end positions inside, outside and on the margin of each pad and
  runway of the site table; flags; the standing-still rule and its own-launch exception.
- Public-state digest: unchanged state exports nothing.
- Tamper evidence: a changed, removed or renumbered rival standing is refused with its reason
  and the last good state kept; a broken chain; a receipt that contradicts a file; the own
  subfolder is restored after a foreign edit.
- A late join: slot and look taken from what is free, no change to the existing players'
  state, the no-retakes rule for a player who has seen everything.
- The isolation test of 6.4 (byte-identical ledger walk and save output with and without a
  rival cache).
- The grep-audit and module-layer gates of 6.4.

**In-game tests** (new category): the working space center at each of the four sites (pad and
runway spawn, Space Center scene, recovery distance); terrain height at fixed sample points of
all four patches equals the site table; scenery present and inert; sites removed on leaving
the race save; looks applied to own roster and to an EVA ghost; a rival flag is readable and
its removal actions are absent; a rival end-state ghost has no colliders.

**Harness lanes.** One PC can play both sides in turn, against a temporary shared folder:

- Player A creates a race and flies to orbit; player B joins, syncs, and the lane asserts A's
  ghost launch from site 0, the board row, and B's untouched ledger.
- B reaches the milestone earlier; A syncs; the lane asserts the holder change and the
  message.
- A quickloads past a shared flight; B syncs; the ghost and the row are gone.
- A race save, then a single-player save in one session: stock Kerbin restored.

**Fixtures:** a synthetic shared folder builder (several players, standings and payload) next
to the existing generators, so import, the board and the window can be tested without a second
install.

---

## 12. Feasibility in KSP 1.12.5

Checked against the decompiled stock assembly on 2026-11-14. Signatures and field names are
reliable; bodies are obfuscation-noisy, so each item ends in what must still be proven in the
running game.

| Idea | Finding | Still to prove (spike) |
|------|---------|------------------------|
| Launch from another place | Stock has a launch-site mechanism: `LaunchSite` (public constructor) and `PSystemSetup.AddLaunchSite` (public); the stock alternate sites use it | Not needed for the home site if the working KSC is moved |
| Move the working KSC | `SpaceCenter` reads its position from its transform at `Start` (private latitude, longitude, surface normal); the flatten patch is a `PQSMod_MapDecalTangent` with public position, radius and absolute offset; the KSC is a `PQSCity`. KSCSwitcher and Kopernicus move all three on 1.12 | S4: move it in a spike and run a full launch, recovery and Space Center visit; check Parsek's own readers of the space center position (`LaunchSiteExclusionZones`, launch-site names in recordings, the launch-site departure rule in the optimizer, KSC-scene ghost playback) |
| Exact KSC copies as scenery | Kerbal Konstructs clones the stock KSC buildings at run time as static objects | S5: clone the hierarchy, strip facility, click and destruction behaviour, keep colliders; show a chosen facility level; measure load and frame cost of three copies |
| Same terrain in every game | The patch is data on a PQS mod; the same patch data gives the same heights | S5: sample heights on two machines |
| Dry, flat land on the equator | NOT verified. The stock height data could not be read from files, and the wiki maps were unreachable | S1: an in-game scan of the equator (height, ocean, slope) around the three targets; the result fixes the site table |
| KSC science at every site | Building mini-biomes are tag-based (`MiniBiome`), so they should follow the buildings; the KSC area biome is in the biome map | S2: prove both at a moved site; design the biome override |
| Four stock looks | `ProtoCrewMember.KerbalSuit` has Default, Vintage, Future, Slim; Vintage and Future belong to the DLCs; per kerbal the suit, the combo id and the light color are saved | S3: confirm each look in game and pick the four UI colors |
| Stock-like replacements | Mods add suit sets with `SUITCOMBOS` config nodes (suit type, gender, textures, sprite); stock supports this since 1.10 | S3: two replacement texture sets; how a missing-DLC machine resolves a DLC suit |
| Look on ghost kerbals | Parsek builds EVA ghosts from the stock EVA part model | S3: apply a suit texture to the ghost |
| Multiplayer in the main menu | `MainMenu` holds its entries as public `TextProButton3D` fields with a public tap callback; the entry lists used for keyboard navigation are private | S6: clone an entry, place it, wire keyboard navigation |
| Create a save from code | `GamePersistence.CreateNewGame(name, mode, parameters, flag, scene, facility)` is public and is what the stock New Game dialog calls | S6: create and start a race save from the dialog |
| Milestones in every mode | `ProgressTracking` is added to every new game, Sandbox included; Parsek's recorder writes a milestone event in every mode | None |
| Readable rival flags | `FlagSite` exposes Read Plaque, Take Down and Pick Up as separate part events; Parsek already spawns replayed flags as real flag objects | S7: disable the removal events per flag; strip rival flags at save; keep the flag clickable without a physical collider |
| Launch areas stay clear | Parsek already retires a flight that ends parked on the KSC pad, the runway threshold or a stock alternate site (`LaunchSiteExclusionZones`, 50 m circles read from `PSystemSetup` launch-site spawn points); a rival end state is only a ghost, so not showing it is a filter at the rival store | S4: measure the pad and full runway footprints at the moved sites and the alternate sites |
| Cheat disclosure | `CheatOptions` holds the stock cheat switches as public static fields | None |
| Joining later | Nothing game-side: a join writes one new subfolder; all four sites exist in every game from the start | None |
| Noticing edited files | Hashes, a chain and a local witness log need only file reads and a hash function, which the recording sidecars already use | None for the simple version; for signed standings, prove the runtime's signing classes work inside KSP |

---

## 13. Open questions

1. **The ladder's numbers.** The steps, the body multipliers and which stock firsts are in
   (7.9) are a first proposal. They want a play test before they are frozen under a
   `ladderId`.
2. **The exact sites.** Fixed by the survey (S1). If no spot near a target passes, how far may
   a site move before "opposite" stops being true enough?
3. **Rival facility levels in Career.** Latest shared levels (proposed), levels as of the
   local game date (needs an upgrade history in the standing), or always fully built?
4. **Targeting rival ghosts.** Allowed as for any ghost (proposed), or blocked so that nothing
   about a rival is selectable?
5. **Cheat-marked milestones.** Marked on the board and still scored (disclose, do not block),
   or scored as nothing?
6. **Alternate launch sites.** Enabled for everyone from day 1 (proposed), or left to the
   stock unlock-by-visit, which favors whoever lives nearest?
7. **How deep verification goes.** The recording exists and covers the date (proposed for the
   first build), or also a check of the trajectory against the claim.
8. **One-goal races.** A preset where a single milestone decides the race; a small addition on
   top of the ladder once the ladder exists.
9. **Looks beyond four players.** Not planned: four sites, four looks.
10. **Launch areas.** The sizes (100 m around a pad, 50 m around a runway strip) and the
    60-second rule for a ghost standing still in a launch area during a replay (7.7) are
    proposals. Is hiding only what was LEFT there enough, with replays always shown?
11. **Detected file changes.** A player marked "files changed" (7.15): only marked, with their
    last good state still scoring (proposed), or scoring nothing until it is resolved?
12. **A seat of a player who left.** Kept forever (proposed, their flights stay), or may the
    creator free it for a newcomer, who would then get a site with old ghosts on it?
13. **How far the first build goes against cheating.** The simple version of 7.15 (proposed),
    or also signed standings and the flight check before a first public release?
---

## 14. Implementation phasing

No phase starts before the owner says so. Each phase is independently testable.

| Phase | Scope | Notes |
|-------|-------|-------|
| R0 | Spikes S1 to S7 of section 12 and S8 of 15.11, on the owner's PC | Decides the site table and retires the feasibility risks before any feature code |
| R0b | Prerefactors P1 to P6 of 15.11, each its own change with no behavior change | Single player keeps passing every suite and lane; the feature then lands on prepared ground |
| R1 | The two new modules with their layer, headless and forbidden-edge rules in place from the first commit; the race mark and gate, the main menu entry, the setup dialog (create, join, continue), `race.cfg` and `player.cfg`, slot and look assignment | A race save that plays as single player |
| R2 | The four sites: moving the working space center, the four terrain patches, the scenery copies, launch rules, removal on leaving the save, science parity | The world of the race, still without rivals |
| R3 | Export (standings, payload, digest) and import (rival cache, rival store), rival ghosts in flight, map, tracking station and Space Center, end-state ghosts, readable flags, launch areas kept clear, the isolation gates, and the simple tamper evidence of 7.15 (chain, witness log, receipts, self-heal) | "See your rivals" |
| R4 | Milestone tables, the board, the Space Race window, holder-change messages | "Have a race" |
| R5 | Looks: own roster, ghost kerbals, colors on labels, icons and orbit lines; the replacement suit sets | |
| R6 | End date and final board, disclosure, the no-retakes house rule, missing-parts and newer-version notes, poll tuning | |
| R7 | Hardening of 7.15, as far as the owner wants it: signed standings, the flight check (claim, teleport, sky, launch), the sealed-race house rule | Optional; the formats reserve room from R3 |

Task-level plans are written per phase at dispatch time, per
`docs/dev/development-workflow.md`.

---

## 15. Architecture

Owner ruling 2026-11-14: multiplayer is implemented "on the structure of the 1.1.x build", and
"lets really think about the ideal structure and architecture from the start, so we don't have
a lot to refactor later". This section is that structure. It is written against the 1.1 module
map (`scripts/arch/modules.toml`, `docs/dev/arch/README.md`): five declared layers (floor,
model, features, presentation, entry), headless modules, forbidden edges, static lifetimes,
all ratcheted in CI. File and type names are proposals; the shape is binding.

### 15.1 The shape in one picture

```
 own save (model)                                         shared folder
 ERS / ELS, names,          PublicState      Standing      players/<id>/...
 facility levels   --(1)-->  (immutable) --(2)--> files  ======================
                                                              ||  sync service
 rival ghosts, flags,       RivalState       Standing        \/
 scenery, board    <--(4)--  (immutable) <--(3)-- files   rival cache (local)

 (1) the ONLY read of the player's own model        (3) verify, chain, witness
 (2) pure: snapshot -> standing + digest            (4) the ONLY place rival data
                                                         becomes something visible
```

Data moves one way through four stages, and each stage hands the next an immutable value. No
stage keeps a reference into the previous one. That is what makes each stage testable alone
and what keeps a rival's data from ever flowing back toward the save: there is no arrow from
the lower row to the upper one, and the build fails if someone adds one (15.4).

### 15.2 Modules

Three new modules, placed by folder rule, plus a few files in existing ones.

| Module | Folder | Layer | Headless | What it holds |
|--------|--------|-------|----------|---------------|
| `RaceCore` | `Source/Parsek/RaceCore/` | model | yes | The vocabulary and every rule: manifests, the standing codec and chain, the witness log, slot and look assignment, the site table and launch-area predicate, the ladder, milestone-table and board derivation, the public-state digest, the sync pipeline as a pure function over ports (15.3), the Space Race window's row model. No game types, no stores |
| `RaceWorld` | `Source/Parsek/RaceWorld/` | features | no (runtime-coupled) | Everything live: the race session for the loaded save, the adapters behind the ports (real folder, real clock, the own-model reader), the rival store, the rival ghost lane, leftovers and flags, the four sites (space center move, terrain patches, scenery), looks |
| (existing) `UI` | `Source/Parsek/UI/` | presentation | no | `SpaceRaceWindowUI`, `RaceSetupDialog`, the Settings section; drawn from `RaceCore` row models |
| (existing) `Controllers` | root files | entry | no | `ParsekRaceMenu.cs` (the main menu entry point), `ParsekScenario.Race.cs` (save and load of the `RACE` node), one call each from the scene controllers into the race facade |
| (existing) `Patches` | `Patches/` | entry | no | Guards that must sit on stock entry points (for example the rival flag's removal events, a launch from a rival's site) |
| (tooling) `TestCommands`, `InGameTests`, `UI/Gallery` | | | | Seam verbs for the harness lanes, the in-game test category, the window's gallery mock states |

Why two modules and not one: the 1.1 map assigns a module to exactly one layer, and
multiplayer has a half that is rules over data and a half that is Unity objects. Keeping the
rules in their own headless module means the whole race (export, import, tamper checks, the
board) runs in the unit-test host with a fake folder, and CI's headless ratchet stops game
calls from leaking into it.

### 15.3 Ports: the pure core and its thin shell

`RaceCore` owns the logic and declares what it needs from the outside as small interfaces.
`RaceWorld` implements them. This is the house pattern (pure decision library, thin I/O
shell), applied to the whole feature.

| Port (declared in `RaceCore`) | Real adapter (in `RaceWorld`) | Test double |
|------|------|------|
| `IRaceFolder`: list, read, safe-write and delete files under the shared folder and the local cache | `FileIOUtils`-based, with retry and budgets | an in-memory folder; also the synthetic shared-folder fixture builder |
| `IRaceClock`: real time in UTC | system clock | a settable clock |
| `IPublicStateSource`: the player's public state as one immutable `PublicState` | `RaceExportSource`, the one file that reads ERS and ELS through `EffectiveState` | a hand-built `PublicState` |
| `IRacePayload`: hash, copy and open a recording's files by id | the sidecar files of the save and of the rival cache | byte arrays |

The sync is then one pure call, `RaceSync.Run(folder, clock, source, link)`, that returns a
`SyncResult`: what was exported, the new `RivalState` per rival, the board, the messages to
show, the marks. The shell applies the result. Nothing inside the call touches the game.

### 15.4 Dependency rules, enforced

Allowed references (everything else is a build failure or a ratchet red):

- `RaceCore` references only the floor (`LogIO`, `Config`, `Core`, `Trajectory`).
- `RaceWorld` references `RaceCore`, the floor, `Recording` (the sidecar codecs and the
  recording data types), `Ghost` (the playback engine, the visual builders, map presence), and
  through its one export file `Rewind` (`EffectiveState`) and `GameActions` (milestone rows).
- `UI` references `RaceCore` (row models) and the `RaceWorld` facade (commands: sync now,
  show or hide a rival, create, join).
- `Controllers` and `Patches` reference the `RaceWorld` facade.

New `[forbidden]` edges in `scripts/arch/modules.toml`, so that the isolation invariant of
section 6.4 is a property of the build:

```
Recording  -> RaceWorld      GameActions -> RaceWorld     Crew       -> RaceWorld
Rewind     -> RaceWorld      Missions    -> RaceWorld     Logistics  -> RaceWorld
SwitchIntent -> RaceWorld    Reaim       -> RaceWorld     Timeline   -> RaceWorld
Ghost      -> RaceWorld      MapRender   -> RaceWorld     Display    -> RaceWorld
Rendering  -> RaceWorld      GameActions -> RaceCore      Crew       -> RaceCore
RaceCore   -> Recording      RaceCore    -> GameActions   RaceCore   -> Ghost
```

Read plainly: the ledger, the roster, the recorder, rewind, missions and routes cannot name
anything of the race; the ghost and map code does not know rivals exist (it is handed
trajectories, as today); and the rules module cannot name a store.

Two finer gates the module map cannot express, as grep-audit gates in the `GrepAuditTests`
family:

- **One reader of the own model.** Inside `RaceWorld`, only `RaceExportSource.cs` may name
  `EffectiveState`, `RecordingStore`, `Ledger` or `MilestoneStore`.
- **No writer of the own model.** No file of `RaceWorld` or `RaceCore` may name a store's
  mutating entry points or a persistence writer of the save (the same shape as the GUI
  gallery's write-set gate).

Plus the headless test of 6.4 (byte-identical ledger walk and save output with and without a
rival cache), and `RaceCore` joins `[headless.modules]`.

### 15.5 Rival data is its own type

A rival's recording is never an instance of `Recording` outside the loader. The rival store
holds `RivalFlight` values: sealed, immutable after load, implementing `IPlaybackTrajectory`
(the interface the playback engine already reads everything through), plus the end-state
snapshot, the flag events and the owner's player id. A method that takes a `Recording` cannot
be handed a rival flight by mistake, so the compiler enforces what the forbidden edges
enforce at module level. The existing sidecar codecs fill it (VERIFY at planning that they
can decode into something other than a `Recording`; the fallback is a private inner
`Recording` that never leaves the loader).

Rival ids live in their own space: every key the display code holds for a rival ghost is the
pair (player id, recording id), never a bare recording id and never a committed-list index.

### 15.6 The rival ghost lane

Decided, replacing the two candidates of the first draft: **rival ghosts play on their own
instance of the playback engine.**

- `GhostPlaybackEngine` is an instance with no mutable static state of its own (one test
  hook aside; static stores of its collaborators that are keyed by index or by a bare
  recording id are prerefactor P3), it takes its
  trajectories as a list of `IPlaybackTrajectory` and its world placement through
  `IGhostPositioner`, and the in-game tests already build extra instances. A second instance
  has its own slot index space, so the committed-list index contract
  (`RecordingStore.CommittedListNotifications.cs`) is untouched and no rival ghost can sit at
  a committed index.
- The rival list is replaced only at scene load (7.6), so the rival engine needs no
  mid-list insert or remove notifications at all.
- The engine's lifecycle events go to a `RivalPlaybackPolicy` that has no spawn path, no
  resource deltas, no chain logic and no crew: it only decides visibility (the show toggle,
  launch areas) and the watch camera. The spawn machinery is simply not wired to this engine.
  That is the structural form of "rivals are only seen".
- Leftovers (a rival vessel at rest after its flight, a station on its orbit) are not engine
  entries. They are static objects placed once per scene by a `RivalLeftovers` presenter from
  the end-state snapshot, so they cost nothing per frame. Flags go through `RivalFlags`.
- One budget: the ghost soft caps count both engines, own ghosts first.
- Each scene controller reaches all of this through one facade, `RaceRuntime`, with four
  calls: scene ready, per-frame tick, scene exit, and sync. In a single-player save the
  facade is absent and the calls are one null check.

VERIFY at planning, by inventory: the flight scene drives the engine through
`ParsekFlightPlaybackHost`; the Space Center scene has its own playback code with its own
slots (`ParsekKSC.Playback.cs`) and the tracking station works through `GhostMapPresence`.
Each of the three needs the lane threaded through once, as a parameter and not as a copy
(prerefactor P2).

### 15.7 State and lifetimes

| State | Where | Lifetime | Rule |
|-------|-------|----------|------|
| `RaceLink` | `RACE` node in the save | the save | Holds only identity and choices that never change (race id, player id, slot, look, table ids). So every load kind (cold, quickload, rewind, Re-Fly, revert) can take it from the loaded save with no special case; `LoadReconcilePolicy` gets one row saying so |
| `RaceSession` | memory | the loaded save | Built at load from `RaceLink`; dropped by the cold-load reset; tagged in `scripts/arch/lifetimes.toml` |
| Rival store | memory | the loaded save, rebuilt each scene | Replaced whole, never edited in place |
| `RaceLocal`: folder location, show toggles, last export digest | `Race/local.cfg` beside the save | the machine | Not in the save, so no load rolls a setting back; missing file means "ask for the folder again" |
| Rival cache, witness log, own mirror | `Race/` beside the save | until deleted | Never named by the save, never synced. VERIFY at planning that the save-folder sweeps, the file collector and the pre-Parsek backup footprint check treat `Parsek/Race/` correctly |
| Applied sites (moved space center, terrain patches, scenery) | Kerbin's own objects | the process, across saves | The one piece of state that outlives a save: `RaceSites` owns apply and restore as a pair, restore runs before any non-race save builds a scene, and an in-game test pins stock Kerbin after leaving a race save |
| Exchange files | shared folder | forever | `raceFormat` is registered in `SaveFormat.cs` with a golden fixture folder, like every persisted format; unknown blocks in a newer standing are ignored, the additive rule of the save-format policy |

### 15.8 Inert outside a race

Every entry point of the feature sits behind one predicate, `RaceGate.IsRaceSave`, read from
the `RACE` node. In a single-player save no race object is constructed, no site is applied,
no poll runs and no file is touched. The proof is mechanical: the existing unit suite, the
harness lanes and the architecture ratchets must be unchanged by the multiplayer code while
no race save is loaded, and one test pins that a single-player save's output is byte-identical
with the feature compiled in.

### 15.9 File access and the main thread

Parsek starts no threads, and a grep gate with an empty allowlist keeps it so (in-game text
formatting relies on the main thread's culture). Shared folders are slow and can stall: a
cloud "placeholder" file downloads when first read. The design keeps the no-thread rule and
bounds the cost instead: every folder operation goes through `IRaceFolder` with a byte and
time budget per poll, a sync is resumable across polls, and nothing in the frame loop waits on
the folder. If spike S8 (15.11) shows that one placeholder read can still freeze the game for
seconds, the fallback is a single worker that only copies bytes between the shared folder and
the local cache (no parsing, no formatting, no game objects), as one allowlisted site. That
choice is deferred to the measurement, and `IRaceFolder` is the seam that makes either one a
local change.

### 15.10 Built to extend

Choices made now so later ideas need no restructuring:

- **N players in the data, four in the world.** Standings, the rival store and the board take
  any number of players. Four is a property of the site table and the looks only.
- **Tables are data with ids** (`siteTableId`, `ladderId`): a new ladder or site table is a
  new id, and old races keep theirs.
- **An opponent pack is a rival.** A pre-recorded space program to race against alone is a
  folder of standings and payload under a player id; the importer needs nothing new.
- **Verification is a pipeline of checks** over (claim, rival flight) returning marks. The
  first build registers one check (the flight exists and covers the date); the flight checks
  of 7.15 are more entries in the same list. A signature field is reserved in the standing.
- **House rules are pure functions** from the set of standings to holders, so a new rule is
  one function and one manifest flag.

### 15.11 Prerefactors and extra spikes

Small changes to single-player code that multiplayer needs. Each lands on its own, before
the feature, with no behavior change, proven by the existing suites and lanes.

| Id | Change | Why |
|----|--------|-----|
| P1 | One source for "where is the working space center": today the pad and runway coordinates are constants in `SpawnCollisionDetector`, with related readers in `LaunchSiteExclusionZones`, the launch-site departure rule and the route origin proof. A `HomeSpaceCenter` value in `Core` that returns the stock numbers unless a race sets it | The home site moves in a race save |
| P2 | Thread a lane through the three scene playback paths (flight host, Space Center playback, tracking station map presence) | The rival lane (15.6) |
| P3 | Lane-qualified keys wherever ghost-side state is keyed by a bare recording id or a committed index (map presence stores, the watch camera) | Rival ids live in their own space (15.5) |
| P4 | Options on the flag replay spawn (`GhostVisualBuilder.SpawnFlagVessel`): removable or not, a physical obstacle or not, stripped at save or not | Readable rival flags (7.7) |
| P5 | A suit texture hook on the EVA ghost build | Looks on ghost kerbals (7.4) |
| P6 | Let the sidecar codecs decode into a plain trajectory value, with `Recording` as one consumer | `RivalFlight` (15.5) |

Extra spike: **S8**, measure what a first read of a not-yet-downloaded file costs on the
common sync clients (Dropbox, Google Drive, OneDrive), to settle 15.9.

### 15.12 Code layout

| Concept | Code |
|---------|------|
| Race mark and gate | `RaceCore/RaceLink.cs`, `RaceCore/RaceGate.cs`; `ParsekScenario.Race.cs` |
| Manifests, standing codec, chain, witness log | `RaceCore/RaceManifest.cs`, `StandingCodec.cs`, `StandingChain.cs` |
| Slots and looks assignment | `RaceCore/RaceSlots.cs` |
| Site table and launch areas | `RaceCore/SiteTable.cs`, `RaceLaunchAreas.cs` |
| Public state, digest, sync pipeline, ports | `RaceCore/PublicState.cs`, `RaceSync.cs`, `RacePorts.cs` |
| Milestone table, ladder, board, house rules, checks | `RaceCore/MilestoneTable.cs`, `Ladder.cs`, `RaceBoard.cs`, `HouseRules.cs`, `ClaimChecks.cs` |
| Window row model | `RaceCore/RaceBoardPresentation.cs` |
| Session, facade | `RaceWorld/RaceSession.cs`, `RaceRuntime.cs` |
| Adapters | `RaceWorld/RaceFolder.cs`, `RaceExportSource.cs`, `RacePayloadFiles.cs` |
| Rival store, flights, lane | `RaceWorld/RivalStore.cs`, `RivalFlight.cs`, `RivalPlaybackPolicy.cs`, `RivalLeftovers.cs`, `RivalFlags.cs` |
| Sites | `RaceWorld/RaceSites.cs` (space center move, patches), `RaceScenery.cs` |
| Looks | `RaceWorld/RaceLooks.cs`; suit sets under `GameData/Parsek/Suits/` |
| Menu entry, setup dialog, window | `ParsekRaceMenu.cs`, `UI/RaceSetupDialog.cs`, `UI/SpaceRaceWindowUI.cs` (on the `StructureListWindowUI` template, registered in `ParsekWindowSet`) |
| Flight checks (later) | `RaceCore/ClaimChecks.cs` entries over the `Parsek.Analyzer` rules |

---

## References

- `docs/dev/done/design-coop-async-multiplayer.md` - the deprecated co-op design; its sections
  4.1, 6.3, 7.1 and 13 are the ancestry of the exchange here.
- `docs/roadmap.md` Phase 15.
- `docs/parsek-game-actions-and-resources-recorder-design.md` - milestone rows and the
  once-ever walk the milestone table reads.
- `docs/dev/design-load-restores-the-save.md` - the load contracts a race save keeps.
- `docs/dev/design-save-format-policy.md` - why the `RACE` node needs no format bump.
