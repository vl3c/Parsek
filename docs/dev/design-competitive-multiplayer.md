# Parsek Multiplayer: the Space Race - Design Document

*Design specification for competitive async multiplayer: up to four players race each other
to the milestones of the Kerbal system, each in their own game, sharing through a synced folder.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings
to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost
vessels alongside new ones. This document specifies how several players, each in their own KSP
instance with their own save and their own game clock, run a space race: everyone sees the
others' space centers, flights and flags as ghosts and scenery, and one common board shows who
reached each milestone at the earliest game date.*

**Status:** DESIGN v2 (2026-11-14, pre-implementation). Written from the owner's rulings of
2026-11-11 and 2026-11-14 (section 0), a stock-code feasibility pass on KSP 1.12.5 (section 12)
and a design consultation. v2 folds in three independent reviews of v1 (one clean read, one
adversarial on the rules of play, one adversarial on the technical plan): the data model
(section 6), the rival world (7.5 to 7.13) and the architecture (15.2 to 15.6, 15.9, 15.11 to
15.14) were rewritten where the reviews found them wrong, and section 13 lists the decisions
still open. Nothing here is implemented, and nothing is implemented before the owner says so
(roadmap "Current focus").
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
- "the four ksc copies means that the main game KSC camera should be moved for each player to
  their location so they see their own buildings" (7.3).
- "for later (but important to mention now), we might want to support adding AI or recorded
  flights that could play as another player, so we should think if this changes anything at
  the start"; "i was thinking along the lines of how we have the auto flight tests, those
  could be the basis for a kind of AI player, with scripted or prerecorded missions ... just
  something that could allow a single player to get a feel for what multiplayer is like ...
  in game a player would see the timeline of a player and use that to challenge themselves to
  compete" (15.10).
- "we will implement on the structure of the 1.1.x build", after the design is settled;
  "important: lets really think about the ideal structure and architecture from the start, so
  we don't have a lot to refactor later" (section 15).
- "we should support allowing other players to join that multiplayer game later".
- "we should also consider some ideas about how we could prevent players just modifying the
  shared game files to cheat (but we can start with a simple version, hoping players know each
  other and are well meaning)".
- Site height: each new site keeps its natural ground height, near its exact point (decision
  card, 2026-11-14, after the terrain survey).
- Scoring: a points ladder (decision card, 2026-11-14). Each first is worth points by
  difficulty; the most points held when time runs out wins.
- Game dates are compared in whole days, and players tied on a day split the points
  (decision card, 2026-11-14; 6.6, 7.8).
- Holding flights back and sharing them just before the end date is allowed (decision card,
  2026-11-14; 7.10).
- Rival milestones dated after your own game clock are always shown (decision card,
  2026-11-14).

**UI exception.** The standing rule "no new player-facing UI surfaces" gets three owner
exceptions from the rulings above, all of them absent from single-player saves: the main menu
Multiplayer entry, the race setup dialog, and the Space Race window. `.claude/CLAUDE.md` records
them when the first one is built. That is the whole list. Everything else the feature shows
uses a surface that exists: a Multiplayer section in the existing Settings window, hover text
inside the Space Race window, one-shot screen messages for events, and the stock confirmation
dialog for the setup dialog's own confirmations. There is no folder picker (the location is
typed or pasted), no marker drawn at a rival's site, and no panel on a stock screen.

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
| A rival's flight has ended | What it left behind stays for as long as it is there in the rival's game: the station in orbit and the lander on the Mun as ghosts that cannot be touched, the flag as an object whose plaque can be read |
| A rival left a vehicle parked on a runway, a pad or an alternate launch site | Nobody else sees it. Launch areas always look empty of rivals' leftovers |
| Player flies through a rival's ghost | Nothing happens. Ghosts have no collision and cannot be docked to, boarded, grabbed or recovered |
| Player opens the Space Race window | A table of milestones by player, each cell the game date that player reached it, the holder marked in their color; points, each player's progress and the time left on top |
| A rival takes a first the player held | One on-screen message at the next sync: "Bogdan took Mun Landing: day 30 beats your day 42" |
| Player rewinds and lands earlier | At the next sync the board on every machine gives the first back |
| Player quickloads to before a shared flight | The flight leaves the race at the next sync, as it left the player's own game |
| The end date passes | The board freezes on what was shared in time and names the winner. Everyone can keep playing |
| Career mode | The player's own world-first rewards, contracts and funds work exactly as in single player, whatever the board says |
| Somebody edited a shared file by hand | Every game that had seen the real file notices: the edited state is not applied, the window says that files in that player's folder were changed (it does not say by whom), and a player whose own files were touched has them restored |
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
   history. An importer never has to replay a log in order: ghosts and the present board
   need only each player's latest standing. History is read for exactly three things, and
   the few standings they need are kept on purpose (6.3): when a date first appeared (the
   no-retakes rule), what was shared before the end date (the final board), and whether a
   file was changed after it was seen (7.15).
4. **Same files, same board.** Every score is a function of the files in the shared folder
   and of nothing else: not of when a file reached one machine, not of a player's calendar
   setting, not of what one machine remembers. What a machine remembers (its witness log)
   can add a mark next to a name; it never changes a score. No machine is special and there
   is no server.
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
   never becomes a race save, and a race save never stops being one: the mark is stored
   where a build without race code keeps it untouched (6.2).
10. **Disclose, do not block.** A race is played among friends. Different mods, changed
    difficulty settings and cheat or debug tool use are shown next to the player's name.
    Nothing is refused for them, and nothing is taken from the player's own game. The one
    thing that costs points is a flight flown with such a tool (7.12, decision D3).
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
| Race save | A save created through the Multiplayer menu. A normal Sandbox, Science or Career save with a race mark in a scenario node of its own (6.2). |
| Site | One of four fixed space center locations on Kerbin's equator. Site 0 is the stock KSC. |
| Home site | The site a player is assigned. Their working space center stands there. |
| Alternate launch site | A stock non-KSC launch site (the Dessert pad and airfield, Woomerang, the Island airfield). They exist only with the Making History expansion (7.3). Common to all players. |
| Launch area | The pad and the whole runway of each of the four sites, and the pad or airstrip of each alternate launch site, with a margin. Part of the site table, so identical in every game. A rival's leftovers inside one are never shown (section 7.7). |
| Look | A player's suit style and the one UI color that goes with it. Four looks, one per player. |
| Shared folder | The folder synced by the players' file-sharing service. Parsek reads all of it and writes only the local player's subtree (the creator also writes the manifest, once). |
| Standing | One immutable export file: a player's complete public state at one moment (section 6.1). |
| Payload | The recording files a standing refers to, stored under the player's subtree, each named by recording id and content hash so a file never changes. |
| Leftover | What a finished rival flight left in the world: a vessel in orbit or on a surface, shown as a ghost for as long as its owner's game says it is there (7.7). |
| Game date | The game's own clock. Stored exactly; compared in the race's date unit, whole days or seconds (6.6). |
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
      payload/                      every file is named by id AND content hash,
        <recordingId>.<hash>.prec   written once, before the standing that first
        <recordingId>.<hash>_ghost.craft   names it; never changed
        trees/
          <treeId>.<hash>.cfg       tree topology
    p_bogdan/
      ...
```

And on each player's own machine, beside the save, never synced and never named by the save:

```
saves/<save>/Parsek/Race/
  local.cfg                         this machine's settings: the shared folder
                                    location, the show-ghosts toggles, the digest
                                    of the last export
  own/standings/                    a copy of the standings this player exported
  rivals/<playerId>/                the local copy the game plays from:
    standings/                      the standing in effect for that rival and the
                                    few older ones the rules need (6.3)
    payload/...                     the recording files the one in effect names
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
| Leftovers: which flights left a vessel in the world, from when until when | Rewind points, quicksaves, unmerged flights, an open Re-Fly session |
| Look, display name, site slot | Supply routes and their cargo |
| Disclosure facts: mod list, difficulty settings digest, cheat and debug tool use per flight | Facility levels, tech, anything else of the career |
| Frontier (latest committed game date) | Anything from a save that is not a race save |

### 4.4 Read from the shared folder, or copy?

The owner asked for this to be analyzed. The choice is where the game takes a rival's files
from while it plays.

| | Read directly from the shared folder | Copy into the save's folder, play from the copy |
|---|---|---|
| Folder offline, drive unplugged, sync client closed | Rivals vanish, or the game stalls on a missing path | Nothing changes; the last synced rivals keep playing |
| Cloud "online-only" files | The first read of a file downloads it, which can freeze the game for seconds, possibly in the middle of a flight | The download happens in the copy step (15.9), never while a ghost needs the file |
| A file changes or disappears while a ghost is playing it | Possible: the sync client rewrites files whenever it likes | Impossible: the copy changes only when Parsek applies a new standing, at a scene change |
| Half-synced state | Must be handled on every read | Handled once, at copy time: a standing is copied only when everything it names has arrived and matches its hash |
| Noticing edited files (7.15) | Nothing local to compare against | The copy and the witness log are the comparison |
| Disk space | None extra | One copy of each rival flight in use (flights are small: trajectories and one craft snapshot each) |
| Freshness | Always the folder's latest | The latest as of the last sync; a sync runs at every scene change, on a slow poll outside flight, and on Sync now |
| Moving or renaming the shared folder | The game breaks until the path is fixed | The game plays on; only sharing pauses until the path is fixed in Settings |

**Decision: copy.** The shared folder is transport only. Only the copy step touches it
(15.9); everything the game shows and everything it scores comes from the local copy. The
same holds in the
other direction: an export is written to the local `own/` folder first and then copied out,
so a full or missing shared folder never loses an export, it only delays it.

The copy is a cache, not data: deleting `Race/rivals/` costs nothing but a re-copy at the next
sync, and it is never included in a save, a quicksave or a backup of the save file itself.
What it keeps is bounded: what each rival's standing in effect names, the few older
standings the rules need (6.3), and a grace period for files a newer standing dropped.

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
window, the end date and the house rules, readable rival flags, leftovers.

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
  dateUnit: string          - how game dates are compared: DAY or SECOND (6.6)
  calendar: string          - KERBIN or EARTH: what "a day" is, for every player (6.6)
  alternateSites: bool      - the stock alternate launch sites are in use (7.3)
  endRealTime: string       - optional; empty = no limit
  noRetakes: bool           - house rule (7.11)
  siteTableId: string       - which site table applies ("kerbin-equator-1", 7.3)
  parsekVersion: string     - informational
  MOD_LIST { MOD id version ... } - the creator's mods, informational

PlayerManifest (players/<id>/player.cfg, single writer = that player)
  playerId: string          - "p_" + Guid("N"), generated when the player joins
  displayName: string       - unique within the race (7.2)
  kind: string              - HUMAN (default), RECORDED or AI (15.10); the first
                              build writes and accepts only HUMAN
  look: int                 - 0..3 (7.4); may be changed once by the clash rule (7.3)
  siteSlot: int             - 0..3 (7.3); never changes
  joinedRealTime: string

Standing (players/<id>/standings/<seq>.pstd, immutable, written LAST)
  seq: int                  - per-player, rising
  playerId: string          - must match the subtree
  exportedRealTime: string  - the writer's clock (6.5)
  prevHash: string          - content hash of the standing with the next lower seq
                              in this player's folder (empty for the first); makes
                              the standings a chain (7.15)
  note: string              - optional: why this standing was written when the
                              public state did not change (a heal, 7.15)
  frontierUT: double        - latest committed game date
  raceFormat, recordingFormat, recordingSchemaGeneration: int
  RECORDINGS { REC id hash treeId startUT endUT tools ... }
                            - every effective committed recording, its content
                              hash, its game-date range, and whether cheat or
                              debug tools were on while it was flown (7.12)
  TREES { TREE id hash ... }                 - their trees
  MILESTONES { M id ut recordingId since ... }
                            - the player's ladder table (7.8); since = the seq of
                              this player's standing in which this exact date
                              first appeared, carried forward unchanged while
                              the date stands (7.10, 7.11)
  LEFTOVERS { L recordingId fromUT untilUT ... }
                            - what the player's flights left in the world and for
                              how long (7.7); untilUT empty = still there
  NAMES { }                                  - mission and vessel names
  DISCLOSURE { modDigest, MOD ..., difficultyDigest }
  SEEN { RIVAL playerId seq hash ... }       - highest standing of each rival applied
                                               BEFORE this one was written, with its
                                               content hash: a receipt (7.11, 7.15)
  PART_MANIFEST { }                          - distinct part names used
  signature: string         - reserved, empty in the first build (7.15, step A)
  Flags are inside the recordings (flag events), not listed separately.

RaceLink (the race mark; in the save, in a scenario node of its own, 7.1)
  raceId, playerId, siteSlot
  siteTableId, ladderId, dateUnit, calendar, alternateSites
  gameMode, difficultyDigest
  (identity and choices that never change after the save is created, so every
   kind of load can take it from the loaded save as is. The display name and the
   look are NOT here: they are cosmetic, may change, and live in player.cfg and
   in local.cfg)

RaceLocal (saves/<save>/Parsek/Race/local.cfg; this machine only, not in the save)
  sharedFolderPath: string  - chosen in the setup dialog, editable in Settings (7.2)
  displayName, look         - as last written to player.cfg
  lastStandingDigest: string - hash of the last exported public state
  SHOW { RIVAL playerId bool ... } - the show-ghosts toggles

RivalCache (local files under saves/<save>/Parsek/Race/rivals/<playerId>/, not named
            by the save, never exported; rebuilt from the folder if deleted)
  the applied standing, the kept older standings (6.3) and the payload files the
  applied standing names
  witness.cfg: seq and content hash of every standing of that rival this machine
               has applied, with the local time it first saw each (7.15)

OwnMirror (local files under saves/<save>/Parsek/Race/own/, never synced)
  a copy of the standings this player exported, to detect and undo changes
  somebody else made to this player's subtree in the shared folder (7.15)

Board (derived, runtime only; section 7.8)
```

### 6.2 Changes to existing types

None of the recording, tree or ledger types gain a field. In particular there is no owner tag
on `Recording`, `RecordingTree` or `GameAction`: a rival's recording never becomes one of those
in the local stores (6.4). The "tools were on" fact of 7.12 is derived at export time from data
the recorder already keeps or from a small additive key on the recording (decided at planning;
additive either way, so no format bump).

The race mark is NOT a child of the PARSEK node. It is the node of a second, tiny scenario
module (`ParsekRaceScenario`), for two reasons the reviews found:

- A build without race code does not carry unknown children of the PARSEK node (the
  save-format policy holds named stores, not arbitrary children), so a mark stored there
  would be lost the first time an older build saved the game. A scenario node whose type the
  running build does not know is kept by stock as it is and written back (VERIFY in spike S6:
  stock keeps a `SCENARIO` node of a missing module type across a save).
- The mark is needed before any scenario module has loaded: the sites must be applied before
  the Space Center scene builds (7.1). The game's list of scenario nodes is available as soon
  as the save is read, so the race gate reads the mark from there.

### 6.3 Exchange format

- **Completeness protocol.** Payload files are written first, the standing last. An importer
  ignores payload nobody names, and treats a standing whose named files are missing or fail
  their hash as "not complete yet": it keeps showing the previous applied standing for that
  rival and retries at the next sync.
- **Latest complete standing wins.** For each rival the importer applies the highest-seq
  standing that is complete. Older standings are never applied after a newer one. A gap in seq
  numbers means nothing.
- **Seq allocation.** The highest seq found in the player's own `standings/` directory in the
  shared folder OR in the local own mirror, plus one. A save rolled back by a load never
  re-uses a number, and neither does a game whose shared folder was offline or restored.
- **Every standing is copied out, in order.** A standing is written to the local own mirror
  first and copied to the shared folder when it is reachable. Standings written while it was
  not are copied later, oldest first, so the chain in the folder has no hole of the writer's
  making.
- **Payload identity.** A recording's payload files are named by recording id AND content
  hash (`<recordingId>.<hash><suffix>`), the naming the save's own sidecar versions already
  use (`SidecarVersions`). A recording id alone does not identify content: an in-place Re-Fly
  continuation and an optimizer merge keep the id and change the bytes. With the hash in the
  name a payload file is immutable by construction, a rewritten recording is simply a new
  file, and nothing is frozen on the owner's side. The exporter takes each hash from the
  sidecar version the save already recorded, so no file is re-hashed at every sync.
- **Export only what is on disk.** The exporter runs after the save's sidecars are flushed,
  copies the files, and hashes the COPY. A standing never names bytes that were still
  changing.
- **What is kept.** A standing is a full snapshot, so the folder would grow without bound if
  every one were kept. A player's exporter keeps in the shared folder: the last 20 standings;
  every standing that is the `since` of a milestone date the latest standing still lists
  (those answer "when did this date first appear", 7.10 and 7.11); and the last standing
  stamped before the end date (7.10). It deletes the rest, and payload none of the kept
  standings names, after a grace period. Importers keep the same set in the rival cache, so
  the board, the final board and the no-retakes rule are all computed from the local copy.
- **Versioning.** `raceFormat` versions the exchange files. A standing with a newer
  `raceFormat`, or recordings in a newer recording format than the local build reads, is not
  applied: the rival's board rows are still shown if the milestone block parses, their ghosts
  are not, and the Space Race window says "Ana runs a newer Parsek". The board rows of such a
  rival are marked "not checked here", because this build cannot verify them (7.8).
- **Id safety.** Every id that becomes part of a path passes the recording id validator
  first (today `RecordingPaths.ValidateRecordingId`; its pure check moves to the floor so
  the rules module can call it, prerefactor P7). A standing with an unsafe id is refused
  whole.
- **Path length.** Windows paths are limited to roughly 250 characters, and the cache sits
  under `saves/<save>/Parsek/Race/rivals/<playerId>/`. The cache uses short directory names
  (the first 8 characters of a player id, checked for collision), the setup dialog caps the
  race save's name, and the validator of the shared folder location refuses a location whose
  longest expected path would not fit.
- **Local writes** into the shared folder are safe-writes (temporary file, then rename) with
  retry and backoff, because sync clients hold brief locks. `FileIOUtils` has the safe-write
  today; the retry is new work.

### 6.4 The isolation invariant

One rule: **no code outside the race modules and the rival ghost lane may read the rival
store, no rival data is ever written into the save, and no rival object is ever something the
stock game or Parsek's own model counts.**

- Rival recordings live in the rival store, a separate in-memory structure. They are not in
  `RecordingStore.CommittedRecordings` or `CommittedTrees`, not in ERS or ELS, and have no
  ledger rows locally.
- They therefore cannot reach, by construction: the ledger walk and the pools, the click
  reservation, crew reservation and stand-ins, ghost chains and vessel holds, the claimed
  vessel disposal gate, the spawn gate and every spawn path, rewind points and Re-Fly, the
  optimizer, Missions, Logistics (route sources and endpoints), the own Timeline window and
  the Recordings table.
- The consumers of the rival store are exactly: the rival ghost lane (flight, map, tracking
  station, KSC scene), the rival flag presenter, the board, and the Space Race window.

The reviews found four side channels that the rule above does not close by itself. Each is
closed by a design choice, not by a guard added later:

| Side channel | What would leak | Closed by |
|--------------|-----------------|-----------|
| Replayed flags are real, saved vessels | The playback engine itself plants a flag for any trajectory that carries flag events (`GhostPlaybackLogic.ApplyFlagEvents` reaching `GhostVisualBuilder.SpawnFlagVessel`, which adds the flag to the saved flight state). A rival flag would be saved, would count as "a flag already stands here" for the player's own flag replay, and would be a vessel stock can switch to, recover, target and scan for contracts | A rival flight hands the engine NO flag events. Rival flags are drawn by their own presenter as plaque replicas that are not vessels (7.7) |
| Map presence uses real proto vessels | A ghost with an orbit line is a vessel in the stock vessel list, which stock walks for progress, contracts and CommNet | Rival map objects are registered in the same guard set own ghosts use (`IsGhostMapVessel`), which every vessel walk already checks, and an in-game test pins that a rival ghost crossing a sphere of influence or landing changes nothing in the local progress or ledger (7.7, 11) |
| Ghost CommNet | An own ghost counts as a CommNet relay, as the vessel it replays. A rival relay would give the local player probe control and science transmission | Rival ghosts are never CommNet nodes: the rival lane is not wired to `GhostCommNetManager` at all |
| Scenery that follows a rival | Buildings whose shape follows a rival's facility levels would change collision geometry under local vessels when a rival's standing arrives | Scenery is the same in every game and never changes: always fully built (7.3). The standing carries no facility levels |

Mechanical gates, so the invariant stays true as the code changes:

- `[forbidden]` edges in `scripts/arch/modules.toml` (listed in 15.4): the ledger, roster,
  recorder, rewind, mission and route modules cannot name anything of the race, and the race
  modules cannot name them, except the one export module.
- A grep-audit gate in the `GrepAuditTests` family: the rival store's type names appear only
  in an allowlisted set of files.
- A headless test: load a race save with a populated rival cache, run the ledger walk and the
  save writer, and assert the outputs are byte-identical to the same save with an empty
  cache.
- A harness lane, because the headless test cannot see the stock flight state, which is where
  the side channels above would land: fly the same scripted session twice, once with an
  empty rival cache and once with a full one (ghosts, leftovers, flags, an orbiting station),
  and diff the two saved games whole. They must be identical.

How the invariant is built into the module structure, the rival data type and the rival
ghost lane is section 15 (15.4 to 15.6).

### 6.5 Timestamps

Real-time stamps use the fixed-width invariant format `yyyy-MM-ddTHH:mm:ssZ` (UTC), compared
after parsing. A standing whose stamp does not parse is refused. A player's standings carry
rising stamps: a standing is stamped `max(now, previous own stamp + 1 s)`.

Real time is the writer's own clock. It decides only two things: which standing is a player's
last before the end date (7.10), and what the "arrived late" mark says (7.12). The no-retakes
rule does not use real time at all; it uses the receipts (7.11).

### 6.6 Game dates

A milestone's date is stored exactly, as the game's universal time in seconds. How two dates
are COMPARED is a race setting, `dateUnit`, fixed in the manifest so every machine compares
alike:

- `DAY` (the default, owner ruling 2026-11-14): dates are compared as whole game days. Two
  players who reach a milestone on the same game day are tied.
- `SECOND`: dates are compared exactly. Ties practically never happen.

Why whole days: (a) the sites are spread around Kerbin, so the Mun and the
launch windows to it come up to three hours apart for different players, a difference that
is nobody's skill and that a whole-day comparison mostly absorbs; (b) with exact seconds the
opening firsts become a clicking race and a rival's date can be beaten by one second, which
makes "wait, look, then beat it by a hair" the best strategy; with whole days a first is
taken only by being a full day earlier, and matching the day is a tie (7.8).

`calendar` fixes what a day is (a Kerbin day of 6 hours or an Earth day of 24), because the
stock setting is per player and the comparison must not depend on it. The window shows dates
in that calendar, with the time of day on hover.


---

## 7. Behavior

### 7.1 The Multiplayer mode and the race mark

The stock main menu shows, after Start Game, a list of entries (Start New, Resume Saved,
Training, Scenarios). Parsek adds one more, **Multiplayer**. It opens the race setup dialog
(7.2).

A race save is a normal KSP save of mode Sandbox, Science or Career, created by Parsek from the
race manifest. KSP's game-mode list is fixed, so "Multiplayer" is not a fourth KSP mode: it is
the race mark, a scenario node of its own in the save (6.2). One predicate, the race gate,
answers "is this a race save" from that mark as soon as the save has been read, before any
scene is built, and every multiplayer code path runs behind it.

- A race save is created only by the setup dialog. There is no way to turn an existing save
  into a race save: every player starts at day 1, which is what makes game dates comparable.
- A race save appears in the stock Resume Saved list like any save (its name carries the race
  name). Loading it there is the same as continuing it from the Multiplayer dialog.
- The sites, the terrain patches and the scenery (7.3) exist only while a race save is loaded.
  They are applied when a race save starts, before its first scene is built, and removed on
  every return to the main menu, so a single-player save always finds the stock Kerbin.
- Two race saves can live on one install. Each has its own mark and its own local race
  directory; going from one straight to the other re-applies the sites for the new home site.
- Single-player saves never read a shared folder and never show a multiplayer surface.

### 7.2 The race setup dialog

One dialog in the stock dialog style, with three actions.

**Create a race.** The creator sets:

- race name;
- game mode: Sandbox, Science or Career;
- difficulty, through the stock difficulty options panel (the same one Start New uses); the
  resulting settings go into the manifest and every player's save is created from them;
- the shared folder's location, in the same dialog as the name and the look: a path field
  (typed or pasted; there is no folder picker) and a Validate button (exists, writable,
  empty of any race, short enough for the paths the local copy needs, 6.3);
- how game dates are compared (6.6), and whether the shared launch sites are in use (7.3);
- optional end date (real time);
- house rules: "no retakes" (7.11);
- their own display name and look.

Then Parsek writes `race.cfg`, the creator's `player.cfg`, creates the save, and starts it at
the creator's home site (slot 0, the stock KSC).

**Join a race.** The player picks the shared folder's location the same way. The dialog
then shows the race name,
mode, end date and house rules, and who has joined with which look. The player enters a
display name (a name already used in the race is refused, so messages and the board never
show two players alike), picks one of the free looks (taken ones are greyed), and sees the
site they will get. Joining creates the player's id; it is stored in the new save. Join
writes their `player.cfg`, creates their save from the manifest, and starts it at
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
than the build; the display name is taken; the race uses the shared launch sites and this
install lacks the expansion they need (7.3); Kerbin is modified (edge case 24).

**Who may join.** Anyone who can write to the shared folder can take a free seat. Sharing the
folder is the invitation, and the folder's own sharing settings are the guest list. Parsek
adds no approval step on top (a group that wants one gets it from the signed standings of
7.15).

**Moving machines, and a lost save.** A player is their save, so:

- *A new PC, or a reinstall:* copy the save's folder, with the `Parsek` folder inside it, to
  the new install and open it with Continue. It is the same player with the same flights. If
  the folder's location differs on the new machine, Settings asks for it. If the local race
  directory was not copied, it is rebuilt from the shared folder at the first sync.
- *An older backup of the save was restored:* the game plays on. The next export is a
  snapshot of the restored state, so flights made after the backup leave the race, as after
  any load of an older save. Numbering continues from what is in the folder.
- *The save is lost for good:* Join offers "start over as" for an existing player. It makes
  a fresh save with that player's id, site and look. Its first export lists no flights, so
  everything that player had flown leaves the race; the dialog says so before it is
  confirmed. Like joining, it is as open as the folder is.
- *The same save copied and played on two machines at once:* two games write as one player.
  Every game notices (edge case 3) and says so; nothing is merged.

In a race save the existing Settings window gains a Multiplayer section: the shared folder
location, the player's display name and look (both cosmetic, changeable to any free one),
Sync now, and status text (last sync, rivals' last shared time, anything not yet complete).

**Changing the location in Settings** (owner ruling 2026-11-14). The location can be edited at
any time; the new one is validated before it is accepted:

- It holds this race (same race id): accepted. This is the normal case, a folder that was
  moved, renamed or mounted under another drive letter.
- It is empty: accepted after a confirmation, as "move the race here". Parsek writes the
  race file and the player's own subfolder there from its local copies. The other players
  point their games at the new folder the same way, and each brings their own files. The
  confirmation says that a player who moves alone has only stopped sharing: the others keep
  racing in the old folder and see that player's last shared time grow old.
- It holds a different race, or is not writable: refused with the reason.

Changing the location never touches the save or the local copy of the rivals. While the
location is wrong or unreachable the game plays on with the rivals it has, and the status
text says sharing is paused and why.

### 7.3 The four sites

**The site table.** Four fixed sites on Kerbin, identical in every race save, shipped with the
mod and named by `siteTableId`:

- Site 0: the stock KSC (footprint centre latitude -0.074, longitude -74.64; ground at 66 m).
- Site 1: the opposite side of Kerbin (ideal longitude 105.36 E).
- Site 2 and site 3: a quarter turn either way (ideal longitudes 15.36 E and 164.64 W).

The ideal longitudes are where the sites would be if spacing were exact. The terrain survey
of 2026-11-14 (`docs/dev/research/kerbin-equator-survey-2026-11-14/`, task S1) measured what
is there:

| Site | Ideal point | Nearest usable ground | Its natural height |
|------|-------------|-----------------------|--------------------|
| 1 | 105.36 E: dry, rough upland | 106.7 E, 1.3 degrees away | about 135 m |
| 2 | 15.36 E: open sea | 18.8 E, 3.4 degrees away (a plateau) | about 520 m |
| 3 | 164.64 W: dry, rough highlands | 162.3 W, 2.3 degrees away | about 675 m |

So every site can stand within 3.4 degrees of its ideal point, which keeps the sites
opposite for every practical purpose (3.4 degrees is 36 km on the ground and 49 seconds of
Kerbin's rotation). What the survey also shows is that none of that ground is at the KSC's
height: ground as low as the KSC's lies 12 degrees from site 2's point and 32 degrees from
site 3's. The stock KSC itself stands on a flattened patch in rough terrain (415 m of
height range within 3 km), so a site does not need naturally flat land, only dry ground
under its patch.

**Each site keeps its natural height** (owner ruling 2026-11-14). The sites stand at the
nearest usable ground to their ideal points and are flattened at the local ground level,
not cut down to the KSC's 66 m and not moved away to low ground. Being as opposite as
possible, and looking like part of the landscape, is worth more than identical heights. The
cost is known and small: a higher site starts in slightly thinner air and a little closer
to space, an edge of roughly one percent or less of the fuel to orbit, in a race that is
scored on game dates. The Space Race window shows each site's height so nobody is surprised.

Requirements a final site must meet: dry land under the whole
footprint; the same latitude as the KSC, so every player launches into the same inclination
with the same rotation speed; room for the full footprint at the KSC's orientation (pad to
the east); and the same terrain patch data in every game.

**Slot assignment.** A human player takes the first free slot in the order 0, 1, 2, 3: two
players are opposite each
other, the third and fourth take the quarter turns. A late joiner never moves anyone, which
matters because flights already recorded launched from a fixed place. Three players therefore
sit at 0, 180 and 90 degrees rather than 120 apart; stability is worth more than symmetry.
The rule is "first free slot in that order", not "slot equals join order", because a seat can
already be taken out of order: a recorded opponent (15.10) is tied to the site its flights
were launched from and brings its slot with it.

Two players who join at the same moment, before the folder synced, can claim the same slot or
the same look. A site is never changed after the save exists: the home site is part of the
race mark, vessels stand on it, and a load must find it where the save left it. So:

- *The same site:* both keep it. Two players sharing a site only means their ghosts overlap
  on the pad. The later joiner (by `joinedRealTime`, ties by player id) is told once, and if
  they have not flown yet the remedy costs nothing: delete the save and join again.
- *The same look:* the later joiner's look is changed to the first free one, with one
  on-screen message. A look is cosmetic and lives outside the save file (6.1), so this is
  safe at any time; their kerbals are re-dressed at the next load.

A fifth player folder (five simultaneous joins) is ignored by every game, by the same order:
the four earliest are the race.

**The home site is a working space center.** In each player's game the working KSC stands at
their home site: the Space Center scene, the VAB and SPH, the launch pad and runway, recovery
distance and every stock facility behave as they do at the stock location. This is done by
moving the stock space center object and its flattening terrain patch, the technique the
KSCSwitcher mod has used for years (section 12). Only one working space center per game is
needed, because each player runs their own game.

The Space Center view follows (owner ruling 2026-11-14): entering the Space Center scene
shows the player's own site, with the camera starting over their own buildings, and those are
the buildings that are clicked, upgraded and repaired. Nothing in the game still points at
the stock location: the camera, the "return to Space Center" button, recovery distance, the
tracking station's home marker and the map's KSC marker all mean the home site. A player
never sees the Space Center scene of a rival's site; a rival's buildings are only visible
from outside, in flight or from the map.

**The other three sites are scenery.** At each other site stands an exact visual copy of the
KSC buildings: solid (proposed, decision D5), not clickable, not destructible, not
upgradable, and always fully built, in every mode. The first draft let a Career site show its
player's facility levels; that is dropped, because buildings whose shape follows a rival
would change what a local vessel can collide with when a rival's file arrives (6.4). The
buildings are the same in every game and never change. An unassigned site has them too.

Solid scenery is real ground, so a player can land on a rival's runway and take off from it;
that is allowed, it is terrain like any other. Three things must be made true for it to stay
only terrain (task S5):

- A stock building's colliders carry tags that the game reads: a vessel standing on one is
  "landed at the launch pad" or "at the runway", stock checks by name whether a launch site
  is clear, and the KSC grounds give building-by-building science. The copies must carry
  none of those tags. Otherwise a vessel parked on a rival's pad would read as standing on
  the local player's own pad, and four sites' worth of building science would exist.
- No science comes from a rival's site beyond what the terrain there gives anyone.
- Parsek's own readers of launch-site names (the launch-area retirement rule) must not match
  a copy.

**The terrain is the same in every game.** All four flattened patches exist in every race
save, whoever is playing. A rival's ghost rolling down its runway must sit on the same ground
in every game.

**Who launches where** (owner ruling 2026-11-14):

- Your own KSC: only you. The stock launch flow from the VAB and SPH puts your vessel on your
  home site's pad or runway. There is no way to launch from a rival's site.
- The stock alternate launch sites (the Dessert launch site and airfield, Woomerang, the
  Island airfield): common. Any player can launch from them, exactly as stock offers them.
  The review found the catch: all of them come with the Making History expansion, and a game
  without it has none. So whether a race uses them is a race setting the creator chooses
  (proposed, decision D4). When it is on, joining needs the expansion, and the race's
  difficulty settings enable the sites from the start, so nobody has to travel farther than a
  rival to unlock one. When it is off, every player launches only from their own site. (The
  sites stock hides until a player has visited them are not part of this: they would favor
  whoever lives nearest.)

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

**What is not equal, said plainly.** The sites share a latitude, so every player launches
into the same inclination with the same rotation speed, and interplanetary windows are the
same for everyone. Three things differ and are accepted:

- *Height* (the owner's ruling above).
- *The neighbourhood.* The stock KSC has a shore, water and an island airfield close by; the
  other sites have whatever lies around them. Early science flights to nearby biomes are
  therefore not identical. The site survey lists what is near each site (open question
  13.C.2), and the window shows it.
- *The time of day and the Mun.* When a race starts it is morning at one site and night at
  another, and the Mun comes overhead up to three hours apart. Compared to the second, that
  would decide the first Mun flights in a Sandbox race; it is the main reason dates are
  compared in whole days (6.6).

**Moving the space center cleanly.** The review listed what holds the stock position and
must follow the move, so the spike (S4) and the implementation reset each one and nothing is
found later: the stock space center object caches its latitude, longitude and surface
normal once at start; each stock launch site caches its spawn points; Parsek caches its
launch-area circles and holds the stock pad and runway coordinates as constants (prerefactor
P1); the flattening patches need the terrain rebuilt after they are added or removed. Site
0's stock patch stays where it is in every game; a player at another site gets a new patch
there. The whole set is applied before a race save's first scene and undone on every return
to the main menu, as one pair of operations with an in-game test on both.

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
lines, and their column and cells in the Space Race window. The color belongs to the look
and is not chosen separately, so four players always have four different colors.

**Where a DLC is missing.** Stock lets a mod add suit color sets for a base-game suit
(`SUITCOMBOS` configuration). Parsek ships two stock-like replacement sets, one resembling the
vintage suit and one resembling the future suit, built on a base-game suit. A machine that
lacks the DLC uses the replacement wherever that look would show: on the player's own kerbals
if they picked it, and on a rival's ghost kerbals. A look is therefore always available and
always distinct, with or without DLC.

**Applying the look.**

- Own kerbals: a kerbal gets the player's look when it joins the roster (the starting crew
  when the save is created, later hires and rescues). After that the stock suit picker is the
  player's own business; Parsek does not force the suit back at every load. What marks a
  player to the others is the color on labels, icons and orbit lines, which they cannot
  change.
- Rival ghost kerbals (EVA ghosts) are drawn with the rival's look, whatever suit the rival's
  kerbal actually wore. Today's EVA ghost is built from the kerbal part named in the
  recording, and the suit styles may be separate parts: on a machine that lacks the
  expansion a recorded suit's part would be missing, and the missing-parts rule would skip
  the whole kerbal. So the ghost build needs a substitution, not only a texture: any kerbal
  part resolves to one the machine has, dressed in the look's suit or its replacement
  (prerefactor P5; VERIFY in task S3).
- Rival vessels keep their own appearance. Their label, map icon and orbit line carry the
  color.

Rosters are private. Each player has their own Jebediah; a rival's kerbals exist in the local
game only as names on ghost labels, so equal names never collide.

### 7.5 Export

A player's public state is exported as a new standing whenever it changed. At each sync tick
(below) the exporter computes a digest of the public state: the effective committed recording
set with content hashes, the milestone table, the leftovers, names, disclosure facts. If it
differs from `lastStandingDigest` in the local race file, it writes any new payload, then the
standing (to the local `own/` folder first, from where it is copied to the shared folder),
and stores the new digest. The receipts (`SEEN`) are not part of the digest: seeing a rival
does not by itself cause an export.

What "effective committed" means is exactly what single player already means by it (ERS): a
flight that was merged and is not superseded, retired or discarded. So:

- An unmerged flight, a flight awaiting its merge dialog and an open Re-Fly attempt are never
  shared.
- A Re-Fly merge replaces the superseded flight in the next standing.
- A load that drops flights (a quickload, a loaded older save) drops them from the next
  standing (principle 7; the co-op rule "shared stays shared" is gone because nothing depends
  on a shared flight). The player is told once, after the load: "2 shared flights left the
  race", because under the no-retakes rule a dropped first may not be retakable (7.11).
- A rewind changes nothing by itself: committed flights in the player's future are still
  theirs and stay shared.

**Sync ticks.** Save load, entering the Space Center or the Tracking Station, after a tree
commit or a Re-Fly merge, a low-frequency poll in the Space Center and the Tracking Station,
and the Sync now button. During a flight the only tick is the one after a commit, and it only
exports; nothing is polled while a vessel is flying.

**Order inside a tick: export first, then import.** The standing written in a tick lists as
seen only what had been applied before the tick. A flight committed before the player's game
had applied a rival's claim is therefore never stamped as "flown after seeing it" (7.11).

An export failure (folder offline) is silent apart from the status text and one log warning.
Nothing is lost: the standing is in the local own mirror and is copied out when the folder
returns (6.3).

### 7.6 Import and the rival store

The shared folder is never read while the game needs an answer from it. A copy step (15.9)
brings each rival's standings and the payload the newest one names into the local rival
cache. At each sync tick the game looks only at the cache: for each rival it finds the
highest-seq standing that is complete (6.3), checks it against the witness log and the chain
(7.15), and records it as that rival's applied standing. Then it rebuilds that rival's part
of the rival store and recomputes the board.

- **The board updates at once**, in any scene.
- **Ghosts update at scene changes.** While the player is in flight the new rival ghosts wait
  for the next scene load, so nothing appears or vanishes around a flying vessel.
- **The rival cache is outside the save.** A quickload, a rewind or a revert does not roll
  rivals back: their world does not depend on the local timeline. At every load the rival
  store is rebuilt from the cache, and only from the cache.
- **Flights load lazily.** The rival store is built from the standing alone (ids, game-date
  ranges, leftovers, milestones). A flight's trajectory file is opened when its ghost is first
  needed, so a scene load does not parse every flight of every rival (15.13).
- **Missing parts.** A rival recording that uses parts the local install lacks plays as a
  ghost with those parts skipped, and the Space Race window notes "Carol uses parts you do not
  have".
- **Missing bodies.** A rival flight to a body the local install does not have (a planet
  pack on one side) is not played; its board row shows, marked "not checked here".
- **Removal.** A recording the rival's latest standing no longer names leaves the rival store;
  its ghost, its leftover and its flags go with it. Cached payload files nobody names are
  deleted after a grace period.

### 7.7 What a rival's world looks like

Rival ghosts play at the game dates they were recorded, against the local player's clock.

- **A flight in the local past** is over: only what it left behind shows.
- **A flight in the local future** plays when the local clock reaches it, like any committed
  future flight of the player's own.
- **Rival ghosts are only seen.** They have no collision, cannot be docked to, boarded,
  grabbed, transferred to, recovered or terminated, and no stock action reaches them (the
  existing ghost guards). They are never CommNet relays or control points (6.4). They never
  loop: a rival flight plays once, at its date. They can be targeted for navigation, as
  ghosts can today (open question 13.C.3).
- **Leftovers.** After a flight ends, what it left stays visible from that game date on: a
  vessel left in orbit stays on its orbit with an orbit line and a map icon in the rival's
  color; a vessel left on a surface stays where it stood. A recovered or destroyed vessel
  leaves nothing.

  How long a leftover stays is something only its owner's game knows: the vessel may later be
  flown again, recovered or deleted in the Tracking Station, and none of those is a shared
  flight. So the owner's exporter says it: the standing's `LEFTOVERS` block lists each
  flight that left something, from which game date, and until which (the start of the next
  flight of that same vessel, its recovery, or the moment the owner's game found it gone;
  empty while it is still there). The viewer shows a leftover exactly inside that window.

  What a leftover looks like is the flight's own ghost held at its last moment (the staged,
  deployed look the flight ended with), not a second snapshot. It is an entry of the rival
  ghost lane in a cheap hold mode, so it obeys the same distance rules and the same ghost
  budget as any ghost (15.6, 15.13), with the lowest priority. Debris is not kept: only
  vessels the rival could still fly leave a leftover, so a rival's spent stages do not
  clutter the sky.
- **Flags** are the exception the owner asked for: a rival's flag can be walked up to and
  read. A rival's planted flag appears from the game date it was planted as a **plaque
  replica**: the stock flag model with the rival's flag texture, plus a click target, which
  opens the stock plaque dialog with the rival's text. It is not a vessel. This is a change
  from the first draft, which reused Parsek's own flag replay: that replay creates a real
  flag vessel, which the game saves, which stock lets a player switch to, recover and target,
  and whose stock states re-enable taking it down. A replica has none of those problems by
  construction: nothing to strip at save, nothing in the vessel list, nothing a contract or a
  stock action can find, and no collider to trip over (VERIFY in spike S7: the stock plaque
  dialog can be opened for text the mod supplies).
- **Launch areas stay clear** (owner ruling 2026-11-14). Whatever a rival left inside a launch
  area (section 7.3) is not shown to other players: no leftover, no map icon, no orbit or
  surface marker, no flag. The rule is: a rival leftover whose position is inside a launch
  area is not shown, and a rival flag planted inside one is not created. It applies to all
  four sites' pads and runways and to the alternate sites, in every game, and it is decided
  by the viewer's game from the recording's end position and the site table, so it does not
  rely on what the rival's game says. The owner still sees and uses their own vehicle in
  their own game (apart from the existing single-player rule below); the flight itself, its
  replay up to the moment it parks, and any milestone it reached are unaffected. A parked
  vehicle that later moves out of the area in a new flight is shown again from that flight
  on.

  This extends a rule single player already has: a flight that ends parked on the KSC pad or
  runway threshold, or on a stock alternate site, is retired and never becomes a real vessel
  (`SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement`, 50 m circles from
  `LaunchSiteExclusionZones`). The race rule is wider (whole runways, all four sites, flags)
  and applies to what is SHOWN, so it needs its own pure predicate over the site table and
  plain coordinates rather than a wider single-player rule. In a race save the single-player
  circles must follow the moved home site (task S4).

  While a rival's flight is being replayed its ghost is shown wherever it is, launch areas
  included: a launch has to start on a pad, and a ghost cannot block anything. The first
  draft also hid a ghost that stood still in a launch area for 60 seconds during a replay.
  That is dropped from the first build: a ghost blocks nothing, a flight ends when it is
  committed, and the rule had loopholes of its own (a slowly creeping rover). It stays as an
  option if play shows a need (13.B).
- **Watching.** The existing watch camera works on a rival ghost while that ghost is in the
  scene, which is while the local clock is inside the flight. It is a camera, nothing else.
  A flight in the local past is watched the way the player's own past is: by rewinding to
  before it. A viewer that replays any rival flight at any time, outside the local timeline,
  is a later addition (7.15, hardening).
- **Hiding.** The Space Race window has one show toggle per rival, for performance and for
  players who want a quiet sky. It hides that rival's ghosts, leftovers and flags alike.
- **The Tracking Station list.** Rival objects with a map presence appear there the way own
  ghosts do, under the same filter, and follow the same toggle.

### 7.8 Milestones and the board

**A player's milestone table.** For each ladder milestone, the earliest game date at which one
of the player's effective committed flights reached it, and that flight's recording id. It is
derived from data single player already keeps: the ledger's milestone rows, which exist in all
three game modes and which the once-ever walk already credits to the earliest date. After a
rewind and an earlier flight, the walk credits the earlier one, and the table follows.
(Checked in the code by the review: milestone events are captured in every mode; the walk
credits the first in game-date order; after a rewind a milestone is open again until the
clock passes the committed row, so an earlier re-earn is recorded; rows follow their flight
through optimizer splits.)

**The replay is the proof.** A milestone entry counts only if the standing lists a flight
that covers its date: the recording it names, or another recording of the same tree, whose
game-date range (carried in the standing) contains the date within one second. The same-tree
allowance matters because a flight is cut into several recordings (at a staging, at an
atmosphere boundary, by a Re-Fly) and a milestone can fall on a boundary. An entry that fails
is shown as "unverified" and scores nothing. Notes from the review:

- Stock completes the "return from" milestones when the vessel touches down on Kerbin
  (checked in the stock code: on the change to landed or splashed), not at recovery, so they
  fall inside the flight.
- A few Kerbin firsts are rows the ledger holds without naming a flight. For those the
  exporter names the flight whose range covers the date; if there is none the entry stays
  unverified (edge case 12).
- VERIFY at planning: a Re-Fly that removes the flight which first reached a milestone, while
  a later flight also reached it, must leave the later flight's date in the table.

A deeper check of the trajectory against the claim (on the Mun's surface and at rest, for a
landing) is a later hardening (open question 13.C.4).

**The board.** For every ladder milestone m and every player p, the date d(p, m) or nothing.
The first is the earliest date in the race's date unit (6.6); its holders are every player
with that date. With whole-day comparison, two players who reached the milestone on the same
game day are co-holders and **split the points** (owner ruling 2026-11-14): a milestone
worth 6 gives each of two co-holders 3. Matching a rival's day therefore takes half of the
points from them, and only being a full day earlier takes them all. A player's score is the
sum over the milestones they hold.

The board is recomputed from the applied standings at every sync and whenever the local
player's own table changes. Nothing about it is stored.

**All dates are shown** (owner ruling 2026-11-14): a rival's milestone appears on the board
with its game date as soon as it is shared, even when that date is ahead of the local clock.
You always know what you have to beat. Together with joining later this means a late joiner
sees every date before flying anything; that is the ruling's intent, and the house rule of
7.11 is the answer for groups that do not want it.

**Messages.** When a sync changes the holder of a milestone the local player held or now
holds, one on-screen message says so ("Bogdan took Mun Landing: day 30 beats your day 42",
"Mun Landing is yours again"). A sync that brings many changes sends one summary message; the
window has the detail. These are events that changed something, which is what a screen message
is for. Messages name a player in words; names are unique within a race (7.2), so a name
cannot be confused with another player's.

### 7.9 The ladder

The ladder is a fixed table shipped with the mod (`ladderId`), so every machine scores alike.
It is built from the stock world-first progress milestones, keyed by the same ids Parsek
already records (`Body/Step`, for example `Mun/Landing`). Those ids are stable and do not
depend on the game's language.

Proposed shape, to be tuned by play (open question 13.C.1):

- **Steps**, with base points: flyby 1, orbit 2, landing 3, surface EVA or flag 1 more, return
  from orbit 2, return from the surface 4, plus the Kerbin firsts (reach space, orbit) at 1
  each.
- **Bodies**, with a multiplier: Kerbin, Mun and Minmus 1; Duna, Ike, Eve orbit and Gilly 2;
  Moho, Dres and the Jool moons 3; Eeloo 4; the Eve surface return 6.
- **Left out:** the stock altitude, speed, distance and depth records (continuous values that
  reward grinding and tie constantly); first launch (everyone has it in the first minute);
  anything stock does not define (a landing on Jool or the Sun).

Points the review raised, for the play test to settle rather than this document:

- **Flybys are cheap in bulk.** One tour of the Jool system collects five flybys. The
  multiplier makes that 15 points for one flight; the play test decides whether flybys of
  moons count less.
- **Water.** On a body with seas (Laythe, Eve) stock has a separate splashdown step. The
  ladder counts "landing or splashdown, whichever came first" as the landing.
- **Crewed or not.** Stock world firsts do not ask whether a kerbal was aboard, and neither
  does this ladder. A group that wants "a kerbal on the Mun" has the EVA and flag steps,
  which need one.

The ladder is the same in all three modes. In Sandbox every part is available on day 1, so a
Sandbox race is about flying and planning; in Science and Career it is also about building the
science to unlock the parts early. Science is the expected default (it has the tech race
without the money). In Sandbox the first days decide a lot, because nothing gates the
flights; the setup dialog says so next to the mode.

### 7.10 The end date

A race may have a real-time end date. When it has passed, the board is final: for each player
it counts their last standing stamped before the end date. The Space Race window names the
winner and shows the final board. Everyone can keep playing and sharing; later standings
still drive ghosts and are shown as "after the end".

- **The final board is a function of the files.** It is computed from the standings in the
  folder (each player's exporter keeps their last pre-end standing for good, and its payload,
  6.3), not from when a file happened to arrive on one machine. Every machine that has the
  files shows the same final board.
- **Holding back is legal** (owner ruling 2026-11-14). A player may fly in private and
  share at the last hour; the race has no referee to say when a flight "should" have been
  shared. What a player risks by holding back is the sync itself: a standing that is not in
  the folder cannot count.
- **The clock is the writer's own.** A standing stamped before the end that reaches the
  others long after it is marked "arrived late" on every machine that saw it late (7.12). It
  still counts. Among friends a wrong clock is visible and talked about; a group that wants
  more gets it from the hardening steps of 7.15.
- **No end date:** the race simply continues, and the board always shows the present.

### 7.11 House rule: no retakes

The owner's optional rule: "first to get there wins, no one-upping". With no server and no
trusted clock, the rule is stated so that every machine computes it from the files alone,
without any real-time stamp:

**You cannot take a first you already knew about.** Each standing records, in its SEEN block,
the highest standing of each rival its writer's game had applied before writing it, and each
milestone entry records the standing in which that date first appeared (`since`). A player's
date for a milestone is **blocked by a rival** when the rival's own entry for that milestone
had already been seen by the player's game when the player's date first appeared. Under this
rule the holder is the earliest date that is not blocked. Two players who each reached the
Mun before seeing the other's claim compare by game date as usual; once a player's game has
seen a rival's Mun landing, flying an earlier one no longer takes it.

Details the reviews asked for:

- **Improving your own date never hurts you.** If a player held a date before seeing the
  rival's and later flies an even earlier one, the earlier one is "seen" and does not count,
  but the first one keeps counting for as long as its flight is still shared. The exporter
  lists both.
- **A block lasts only while the rival's claim stands.** If the rival's flight leaves the
  race (they loaded an older save), their claim is gone and the milestone is open again for
  everyone who had seen it.
- **A dropped first can be lost for good.** A player who held a first, quickloads past its
  flight and flies it again has, by then, seen the rivals' claims. That is the rule working;
  the message after such a load (7.5) says a first left the race.
- **It is an honor rule with evidence, not a lock.** A player who never lets their game sync
  never "sees" anything. What the files give is evidence: every standing says what its writer
  had seen, the others hold receipts, and a group can read from the window who had seen what
  when a date appeared. The window shows a blocked date as "seen first" with the standing
  that proves it.
- A late joiner has seen everything already shared, so they can only take firsts nobody has
  reached yet; the Join dialog says so before they commit. A recorded opponent (15.10) sees
  nothing and blocks nobody.

The standings that answer "when did this date first appear" are the ones the exporter keeps
for good (6.3), so the rule is computed from the local copy even when the folder is offline.

### 7.12 Disclosure

Each standing carries facts that are shown next to the player's name in the Space Race window
when they differ from the race's:

- **Mods:** a digest of the mod list, with the list.
- **Difficulty:** a digest of the save's difficulty settings; it differs from the manifest's
  when the player changed them in game.
- **Cheat and debug tools, per flight.** Each recording entry of a standing says whether a
  tool from a fixed list was on or was used while that flight was recorded. The list is
  explicit, because "a cheat" is not one switch:
  - the stock cheat options that change physics or resources (infinite propellant and
    electricity, no crash damage, unbreakable joints, hack gravity, ignore heat, and the
    like);
  - the stock debug actions that move a vessel or give resources (set orbit, set position,
    rendezvous, fill resources) and, in Science and Career, the ones that add science or
    funds or unlock technology (VERIFY in a spike which of these can be observed; one that
    cannot is listed in this document as out of reach, 7.15);
  - Parsek's own test tooling (the in-game test runner, the automation command seam, injected
    recordings): a race save in which any of it ran says so for good.
  Not on the list, because they change nothing a rival could not also do: part clipping,
  non-strict part attachment, and the biome overlay.
  A flight flown with a listed tool **does not score** (proposed, decision pending): its
  milestones are shown on the board, marked, and count for nothing. The player can fly it
  again without the tool.

A standing stamped before the end date that the local machine first saw long after it is
marked "arrived late" locally (7.10).

### 7.13 The Space Race window

A new Parsek window, present only in race saves, opened from a button in the main Parsek
window. It follows KSP's UI scale like every Parsek window and reuses the log-table window
template.

- **Header:** race name; time left or "final"; one block per player in their color: name,
  points, frontier ("at day 61"), last shared time, their site and its height, disclosure
  marks, the show toggle.
- **Table:** ladder milestones as rows, grouped by body; one column per player. A cell shows
  the game date that player reached the milestone, or nothing. The holder's cell is
  highlighted in their color; an unverified, tool-marked, "seen first" or "not checked here"
  cell is marked, and its hover says why. A points column shows what the row is worth.
- **Click a cell:** if that flight's ghost is in the scene, the watch camera goes to it.
  Otherwise the hover says at which game date it flies, which is where to rewind to see it.
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
   is not applied: that rival's last good state stays, and the Space Race window says
   "files in Ana's folder were changed", with the reason. The wording is deliberate. Without
   signatures nobody can tell WHO changed a file in Ana's folder, and the likeliest victim of
   an edit is Ana herself, so the mark describes the folder and accuses nobody. An older
   standing that is simply gone is "history incomplete", not a change: owners prune (6.3).
   A receipt that contradicts a file is shown as a disagreement naming both players.
4. **Your own files heal themselves.** At every sync the exporter compares its subfolder in
   the shared folder with its local copy of what it wrote.
   - A file it wrote that was changed or deleted there is put back.
   - A standing it did not write (a higher number than its own latest) is not deleted, since
     it may be this same player's export from another machine or from before a restored
     backup. Instead the game writes a fresh standing after it at once, with a note saying
     which numbers it replaces. A standing is a full snapshot, so whatever the unknown files
     claimed is replaced, and the others see the note and clear the mark.
   Either way the player is told once on screen.
5. **The replay is the proof** (7.8). A date on the board counts only when the flight behind
   it is shared and covers that date, and anyone can click the cell and watch it happen.
6. **Disclosure** (7.12): mods and changed difficulty settings are shown next to the
   player's name, and a flight flown with a cheat or debug tool is marked.

What the simple version catches: a typo-level or text-editor change to any shared file,
anybody writing into somebody else's subfolder while that player still plays, a claimed date
with no flight behind it, a quietly replaced history. What it does not catch, said plainly:

- a player who changes their OWN save or recordings consistently before the mod exports
  them, or who runs a changed mod;
- somebody writing as a player whose game is no longer running to heal it (taking over a
  seat that went quiet), or copying another player's flight files into their own folder and
  claiming the flight. Both are closed by signed standings (step A), and the second is also
  visible without them: the same flight appears under two names.

**Hardening, for later, in order of value.**

- **A. Signed standings.** Each player gets a key pair when they join; the public half is in
  their `player.cfg` and every other game pins it the first time it sees it. Each standing is
  signed. Then nobody can write a standing in another player's name even if the victim's game
  is not running to heal it, and a swapped key is itself a detected change. (VERIFY that
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
  - *A replay viewer:* play any rival flight at any time, outside the local timeline, from
    the board. The first build can only show a rival flight while the local clock is inside
    it (7.7); the viewer makes "anyone can watch it" true without rewinding.
- **B2. An end the clock cannot move.** The final board trusts each writer's own stamp
  (7.10). A stricter rule for groups that want it: a standing counts for the final board
  only if another player's standing, itself stamped before the end, lists it as seen. That
  needs the others' games to run near the end, so it is a house rule, not the default.
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
never delete anything of a rival's, never touch the local player's game. A mark changes no
score: the last good state keeps scoring, because the mark cannot say who is at fault.

---

## 8. Edge cases

1. **A rival's standing is half synced.** Named payload missing or failing its hash: the
   previous applied standing stays in effect; retry at the next sync. Never partially applied.
2. **Sync-service conflict copies** (`... (1).pstd`, "conflicted copy"): ignored, named in a
   log warning. With one writer per file they can only come from misuse (case 3).
3. **Same player id on two machines.** Both write `players/<id>/`. Standings with equal seq
   and different content: the importer takes neither, warns naming the two-machine cause, and
   keeps the last good one. The supported way to move machines is to copy the save and play
   it in one place at a time (7.2).
4. **Two joiners claim one slot or one look.** Section 7.3: a shared site is kept by both; a
   clashing look is changed for the later joiner.
5. **The shared folder is offline or gone.** The save plays on with the cached rivals. If the
   folder is deleted for good, every player's save is still a complete game; relinking a new
   folder under the same race id restarts sharing from each player's next standing.
6. **A player quits the race.** Their last standing stays: their ghosts, flags and board dates
   remain, their slot and look stay taken. The window shows their last shared time.
7. **A player joins late.** They start at day 1 like everyone and are behind only in real
   time. Game dates are comparable because every save starts at day 1. Nobody else's save
   changes (7.2).
7a. **A fifth friend wants in.** Refused: four seats, four sites, four looks. A seat is not
   freed when a player stops playing, because their flights and dates stay in the race.
7b. **Somebody edits a rival's standing in the shared folder to give them a worse date.**
   Every game that had already seen the real file refuses the changed one; the rival's own
   game puts the real file back at its next sync (7.15).
7c. **A player edits their own latest standing by hand to claim an earlier date.** The date
   has no flight behind it that covers it, so it shows as unverified and scores nothing; if
   an already-seen file was changed, the window says that files in that player's folder were
   changed (7.15).
8. **"I rewound and my rival's ghosts are still there."** Yes: rivals do not depend on your
   timeline. Their flights replay as your clock passes them again.
9. **"I quickloaded and lost a flight I had shared."** It leaves the race at the next sync
   (7.5), and one message after the load says so. Fly it again; if it is earlier, so much the
   better. Under the no-retakes house rule a first lost this way may be gone for good,
   because by now the rivals' claims have been seen (7.11).
10. **A Re-Fly of a flight that held a first.** The merged attempt replaces the flight. If the
    milestone now falls on a different date, the table and the board follow. An open attempt
    is never shared.
11. **Equal dates.** Co-holders, who split the points (7.8). With whole-day comparison ties
    are common for the opening firsts and happen later too; with exact comparison they
    practically never happen (6.6).
12. **A milestone reached with no recording behind it** (a row the ledger holds without a
    flight): the exporter names the flight whose range covers the date; if there is none it
    is listed as unverified and scores nothing (7.8).
13. **A rival uses parts I do not have.** The ghost draws without them and the window says so.
14. **A rival runs a newer Parsek.** Their board dates show if readable; their ghosts wait
    until the local build can read their recordings. The window says which.
15. **Two rivals launch from the same alternate site at the same game date.** Their ghosts
    overlap. Cosmetic.
16. **My vessel flies through a rival's space center.** The buildings are solid scenery, the
    same in every game. Crashing into them is a crash, as into any terrain.
16a. **I land on a rival's runway and take off again.** Allowed; it is ground. My vessel is
    not "at the runway" for any stock or Parsek rule (7.3), and if I leave it there the
    rival and everyone else simply do not see it (7.7).
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
    is shown throughout: a ghost in a replay blocks nothing. Only what is LEFT in a launch
    area is hidden (7.7).
19. **The end date passes while I am in flight.** The board freezes at the next sync. A flight
    committed after the end date is "after the end".
20. **My PC clock is wrong.** A standing stamped in the future still orders after the player's
    previous ones. Rivals see "arrived late" or a last-shared time in the future, and can say
    so.
21. **I change difficulty settings mid-race.** Allowed; disclosed (7.12).
22. **I open the race save with multiplayer code missing** (an older Parsek build, or Parsek
    removed). The race mark is a scenario node of its own, which the game keeps and writes
    back even when no installed code knows it (6.2; VERIFY in spike S6), so the save is still
    a race save when the right build opens it again. But the sites are not applied by a
    build that does not know them: a player at site 0 notices nothing, while a player at
    another site finds their space center back at the stock location and their vessels at
    the home site standing on unflattened ground, where stock may destroy them. An older
    build cannot warn about this, so the release notes of the first race build do: do not
    open a race save with an older Parsek.
23. **A single-player save is loaded after a race save in one session.** The sites, patches
    and scenery are removed before the single-player save's scene builds (7.1). A harness lane
    pins this.
24. **Kerbin is changed by a planet mod.** The site table assumes stock Kerbin. A race refuses
    to start on a modified Kerbin and says why (the one refusal besides format, because
    terrain parity is not a matter of taste).
25. **Three players; site 3 is empty.** Its buildings stand, fully built like every site, and
    nobody launches from them.
26. **My site starts at night and my rival's in daylight.** Yes: the sites are spread around
    Kerbin. Time warp to morning costs a fraction of a game day (7.3).
27. **A rival recovered a lander weeks ago and I still see it.** Not after the rival's next
    export: their standing says until when each leftover was there (7.7). Before that export
    arrives, the old state shows.
28. **A rival flew to a body my install does not have.** The flight is not played here; its
    board row shows, marked "not checked here" (7.6).
29. **I want to change my name or my look mid-race.** Both are cosmetic and live outside the
    save: the Multiplayer section of Settings changes them if the new name or look is free,
    and the others see it at their next sync.
30. **I rewind to retake a first in Science or Career and cannot afford the parts I used
    later.** The single-player rule holds: science my committed future spends is reserved.
    A retake is flown with what I really had at that date. That is the price of a retake.
31. **Two race saves on one install.** Each has its own mark and local race directory; the
    sites follow whichever is loaded (7.1).

---

## 9. Error recovery

| Failure | Recovery | Data loss |
|---------|----------|-----------|
| Export interrupted (crash, folder offline) | Payload without a standing is inert; the next tick writes a fresh standing | None |
| Shared-folder write hits a sync-client lock | Safe-write retries with backoff, then waits for the next tick | None |
| Corrupt or unsafe standing (bad hash, bad id, bad stamp, truncated) | Refused whole with the file and reason logged; the previous applied standing stays | None locally |
| Cloud placeholder payload (not downloaded) | The copy step downloads it outside the game's frame loop (15.9); the game never opens a placeholder itself. The status text says what is still arriving | None; slow |
| Rival cache deleted or damaged | Rebuilt from the folder at the next sync | None; the witness log restarts, so earlier changes to a rival's history can no longer be noticed by this machine |
| A rival standing changed after it was seen, or its chain does not connect | Not applied; the last good state stays and keeps scoring; the window says files in that player's folder were changed, with the reason; that player's own game repairs it at its next sync | None |
| The local player's subfolder was changed by someone else | Files this game wrote are put back; a standing it did not write is superseded by a fresh one with a note (7.15); one on-screen message | None |
| A load rolls the save back | The rival store is unaffected; the next export is a fresh snapshot of the rolled-back state; one message says how many shared flights left the race | The dropped flights leave the race, as they left the save |
| The local race directory beside the save is gone | Settings asks for the shared folder again; the own mirror and the rival cache are rebuilt from it at the next sync | None; the witness log restarts |
| The save was restored from an older backup | Plays on; numbering continues from the folder; the next export is a snapshot of the restored state | Flights made after the backup leave the race |
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
- Board derivation: holders, co-holders and the split of points, unverified and tool-marked
  entries, both date units and both calendars, the end date cut.
- Verification: a date inside the named recording, inside a sibling recording of the same
  tree, on a boundary within the tolerance, outside every range.
- The no-retakes rule over standing histories: blind dates, a date improved after seeing, a
  rival's claim that later disappears, a late joiner, export-before-import in one tick.
- What is kept: the pruning rule never drops a standing the final board or the no-retakes
  rule still needs, nor payload such a standing names.
- Leftover windows: a vessel flown again, recovered, deleted; a debris end state.
- The launch-area predicate: positions inside, outside and on the margin of each pad and
  runway of the site table; flags.
- Public-state digest: unchanged state exports nothing.
- Tamper evidence: a changed or renumbered rival standing is refused with its reason and the
  last good state kept; a pruned one is "history incomplete" and not a change; a broken
  chain; a receipt that contradicts a file; the own subfolder is restored after a foreign
  edit; an unknown newer standing in the own subfolder is superseded with a note; a restored
  backup continues the numbering.
- Same files, same board: the board computed from one folder on two machines with different
  witness logs, arrival orders and calendar settings is identical.
- Cost: building the rival store opens no payload file.
- A late join: slot and look taken from what is free, no change to the existing players'
  state, the no-retakes rule for a player who has seen everything.
- The isolation test of 6.4 (byte-identical ledger walk and save output with and without a
  rival cache).
- The grep-audit and module-layer gates of 6.4.

**In-game tests** (new category): the working space center at each of the four sites (pad and
runway spawn, Space Center scene, recovery distance); terrain height at fixed sample points of
all four patches equals the site table; scenery present and inert; sites removed on leaving
the race save; looks applied to own roster and to an EVA ghost; a rival flag is readable and
it is not a vessel; a rival leftover has no colliders; a rival ghost crossing a sphere of
influence and landing leaves the local progress and ledger unchanged; a rival relay is not a
CommNet node; a vessel standing on a scenery pad is not "at the launch pad" and gives no
building science; an EVA ghost recorded in a suit this install lacks is still drawn.

**Harness lanes.** One PC can play both sides in turn, against a temporary shared folder:

- Player A creates a race and flies to orbit; player B joins, syncs, and the lane asserts A's
  ghost launch from site 0, the board row, and B's untouched ledger.
- B reaches the milestone earlier; A syncs; the lane asserts the holder change and the
  message.
- A quickloads past a shared flight; B syncs; the ghost and the row are gone.
- A race save, then a single-player save in one session: stock Kerbin restored. Two race
  saves with different home sites in one session.
- The whole-save isolation lane of 6.4: the same scripted session with an empty and with a
  full rival cache produces identical saved games.

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
| The Space Center camera at the home site | `SpaceCenterCamera2` finds its start position by a named transform under Kerbin's surface (`pqsName`, `initialPositionTransformName`), which is part of the KSC object that moves; `altitudeInitial` is public. KSCSwitcher resets this camera after a move | S4: enter the Space Center scene at each of the four sites; camera height, building clicks, the day and night lighting |
| Exact KSC copies as scenery | Kerbal Konstructs clones the stock KSC buildings at run time as static objects | S5: clone the hierarchy at the fully built level, strip facility, click and destruction behaviour, keep the colliders but remove the tags stock reads from them (launch pad, runway, building science); measure load, memory and frame cost of three copies |
| Same terrain in every game | The patch is data on a PQS mod; the same patch data gives the same heights | S5: sample heights on two machines |
| Dry land on the equator | MEASURED 2026-11-14 (`docs/dev/research/kerbin-equator-survey-2026-11-14/`): half the KSC latitude is dry, and there is dry ground within 3.4 degrees of each ideal point, at 135 m, about 520 m and about 675 m. The eastern quarter point itself is open sea | Site height is decided (natural height). Remaining: a finer scan of the three spots for the exact coordinates and the footprint's fit |
| KSC science at every site | Building mini-biomes are tag-based (`MiniBiome`), so they should follow the buildings; the KSC area biome is in the biome map | S2: prove both at a moved site; design the biome override; prove the scenery copies give none |
| The shared launch sites | All stock alternate launch sites belong to Making History; a game without it has none (`LaunchSiteExclusionZones` already checks for the expansion) | None; it is a race setting (7.3) |
| The race mark survives an older build | Stock holds each scenario node as data and writes it back; a node whose module type is not installed should be kept the same way | S6: save a game holding a scenario node of an unknown type and read it back |
| Four stock looks | `ProtoCrewMember.KerbalSuit` has Default, Vintage, Future, Slim; Vintage and Future belong to the DLCs; per kerbal the suit, the combo id and the light color are saved | S3: confirm each look in game and pick the four UI colors |
| Stock-like replacements | Mods add suit sets with `SUITCOMBOS` config nodes (suit type, gender, textures, sprite); stock supports this since 1.10 | S3: two replacement texture sets; how a missing-DLC machine resolves a DLC suit |
| Look on ghost kerbals | Parsek builds EVA ghosts from the stock EVA part model | S3: apply a suit texture to the ghost |
| Multiplayer in the main menu | `MainMenu` holds its entries as public `TextProButton3D` fields with a public tap callback; the entry lists used for keyboard navigation are private | S6: clone an entry, place it, wire keyboard navigation |
| Create a save from code | `GamePersistence.CreateNewGame(name, mode, parameters, flag, scene, facility)` is public and is what the stock New Game dialog calls | S6: create and start a race save from the dialog |
| Milestones in every mode | `ProgressTracking` is added to every new game, Sandbox included; Parsek's recorder writes a milestone event in every mode | None |
| Readable rival flags | `FlagSite` exposes Read Plaque, Take Down and Pick Up as separate part events, but a real flag is a saved vessel whose placed state re-enables taking it down, and the plaque is readable only from within 2 m on EVA. A replica (flag model, click target, the stock plaque dialog) avoids all of that | S7: build the replica; open the stock plaque dialog with supplied text; decide the click range |
| Launch areas stay clear | Parsek already retires a flight that ends parked on the KSC pad, the runway threshold or a stock alternate site (`LaunchSiteExclusionZones`, 50 m circles read from `PSystemSetup` launch-site spawn points); a rival end state is only a ghost, so not showing it is a filter at the rival store | S4: measure the pad and full runway footprints at the moved sites and the alternate sites |
| Cheat disclosure | `CheatOptions` holds the stock cheat switches as public static fields. The debug menu's ACTIONS (set orbit, set position, add science, unlock technology) are not switches | S9: which of those actions a mod can observe |
| Joining later | Nothing game-side: a join writes one new subfolder; all four sites exist in every game from the start | None |
| Noticing edited files | Hashes, a chain and a local witness log need only file reads and a hash function, which the recording sidecars already use | None for the simple version; for signed standings, prove the runtime's signing classes work inside KSP |

---

## 13. Open questions

### 13.A Waiting for the owner

Each is written into the document as a proposal and asked as a decision card on 2026-11-14.

| Id | Question | Proposal in this document |
|----|----------|---------------------------|
| D1 | How finely are game dates compared, and what does a tie give? | DECIDED 2026-11-14: whole game days; players tied on a day split the points (6.6, 7.8) |
| D2 | May a player hold flights back and share them at the last moment before the end date? | DECIDED 2026-11-14: yes; the last standing stamped before the end counts (7.10) |
| D3 | Does a flight flown with cheat or debug tools score? | No: shown, marked, worth nothing (7.12) |
| D4 | The shared launch sites exist only with the Making History expansion. What does a race do? | A race setting chosen by the creator; when it is on, joining needs the expansion (7.3) |
| D5 | Are a rival's buildings solid? | Solid, fully built, and the same in every game (7.3) |

### 13.B Chosen by the designer, for the owner to overrule

Smaller choices the reviews forced. Each has one sensible answer under the rulings already
given, so they are written in as decided; any of them can be reopened.

| Choice | Where |
|--------|-------|
| Rival flags are plaque replicas (flag model plus a click target that opens the stock plaque dialog), not real flag vessels | 7.7 |
| Rival ghosts are never CommNet relays | 6.4, 7.7 |
| Only vessels a rival could still fly are kept as leftovers; debris is not | 7.7 |
| A rival flight is watched while its ghost is in the scene; an any-time replay viewer is later | 7.7, 7.13 |
| Under "no retakes": a block lasts only while the rival's claim stands, and improving your own date never costs you the date you had | 7.11 |
| Moving to a new PC is copying the save folder; "start over in my seat" exists only for a lost save and says what it costs | 7.2 |
| Anyone who has the shared folder may take a free seat; sharing the folder is the invitation | 7.2 |
| Two players who claimed one site at the same moment both keep it; only a clashing look is changed | 7.3 |
| A kerbal gets the player's look when hired; the stock suit picker stays the player's own | 7.4 |
| A detected file change marks the player with neutral wording and changes no score | 7.15 |
| A departed player's seat is kept | 8, case 7a |
| The 60-second standing-still rule is not in the first build | 7.7 |
| The shared folder location is typed or pasted, with no folder picker; rival sites get no map marker | 0, 7.2 |
| One background worker that only copies files (needs the owner's yes, as an exception to the no-threads rule, when the feature is planned) | 15.9 |

### 13.C For the play test and the phase plans

1. **The ladder's numbers.** The steps, the body multipliers, moon flybys, and whether crewed
   and uncrewed firsts differ (7.9). They want a play test before they are frozen under a
   `ladderId`.
2. **The exact coordinates of the three new sites.** Their height is decided (natural
   height, 7.3). What remains is a finer scan around 106.7 E, 18.8 E and 162.3 W to pick the
   exact spot where the whole footprint fits at the KSC's orientation, and a look at what
   biomes and water lie near each.
3. **Targeting rival ghosts.** Allowed as for any ghost (proposed), or blocked so that nothing
   about a rival is selectable?
4. **How deep verification goes.** The flight exists and covers the date (the first build),
   or also a check of the trajectory against the claim (7.15, step B).
5. **One-goal races.** A preset where a single milestone decides the race; a small addition on
   top of the ladder once the ladder exists.
6. **Looks beyond four players.** Not planned: four sites, four looks.
7. **Launch area sizes.** 100 m around a pad, 50 m around a runway strip: measured with the
   sites.
8. **How far the first build goes against cheating.** The simple version of 7.15 (proposed),
   or also signed standings before a first public release.
9. **Recorded and AI opponents** (later). In a solo race the opponents hold firsts and can
   win (that is the challenge). In a race between people, does an added recorded opponent
   compete for firsts and the win, or only set the pace? A recorded opponent takes one of
   the four seats (it needs a site and a look).
10. **Two races on one install.** Nothing prevents it: each race save has its own mark and
    its own local race directory. The session rule (15.7) must cover going from one race
    save straight to another with a different home site.

---

## 14. Implementation phasing

No phase starts before the owner says so. Each phase is independently testable.

| Phase | Scope | Notes |
|-------|-------|-------|
| R0 | The inventory of 15.6 (every store read on the playback path, with sizes for P2, P3 and P9), then spikes S1 to S7 of section 12 and S8, S9 of 15.11, on the owner's PC | Decides the site table, sizes the prerefactors and retires the feasibility risks before any feature code |
| R0b | Prerefactors P1 to P9 of 15.11, each its own change with no behavior change | Single player keeps passing every suite and lane; the feature then lands on prepared ground. P2 is the large one |
| R1 | The three new modules with their layer, headless and forbidden-edge rules in place from the first commit; the race mark and gate, the main menu entry, the setup dialog (create, join, continue), `race.cfg` and `player.cfg`, slot and look assignment | A race save that plays as single player |
| R2 | The four sites: moving the working space center, the four terrain patches, the scenery copies, launch rules, removal on leaving the save, science parity | The world of the race, still without rivals |
| R3 | Export (standings, payload, digest) and import (the copy step, rival cache, rival store), rival ghosts in flight, map, tracking station and Space Center, leftovers, readable flags, launch areas kept clear, the isolation gates including the whole-save lane, and the simple tamper evidence of 7.15 (chain, witness log, receipts, self-heal) | "See your rivals" |
| R4 | Milestone tables, the board, the Space Race window, holder-change messages | "Have a race" |
| R5 | Looks: own roster, ghost kerbals, colors on labels, icons and orbit lines; the replacement suit sets | |
| R6 | End date and final board, disclosure, the no-retakes house rule, missing-parts and newer-version notes, poll tuning | |
| R7 | Hardening of 7.15, as far as the owner wants it: signed standings, the flight check (claim, teleport, sky, launch), the any-time replay viewer, the sealed-race house rule | Optional; the formats reserve room from R3 |
| R8 | Recorded opponents and the solo race (15.10): the `Opponents` folder, the third setup choice, a harness campaign that produces opponents | After R4, independent of R7; needs no change to R1 to R6 beyond the `kind` field and seat reservation they already carry |

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
 leftovers         --(1)-->  (immutable) --(2)--> files  ======================
                                                              ||  sync service
 rival ghosts, flags,       RivalState       Standing        \/
 leftovers, board  <--(4)--  (immutable) <--(3)-- files   rival cache (local)

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
| `RaceCore` | `Source/Parsek/RaceCore/` | model | yes | The vocabulary and every rule: manifests, the standing codec and chain, the witness log, slot and look assignment, the site table and launch-area predicate (over plain latitude and longitude), the ladder, milestone-table and board derivation (from neutral rows, below), the house rules, the public-state digest, the sync pipeline as a pure function over ports (15.3), the Space Race window's row model (dates as numbers; the text is formatted in `UI`). No game types, no stores, nothing above the floor |
| `RaceExport` | `Source/Parsek/RaceExport/` | features | no | The one reader of the player's own model. `RaceExportSource` turns ERS, ELS, the vessel identities and names into one immutable `PublicState`: recordings with hashes and date ranges, the milestone rows the walk credited (as neutral rows: id, date, recording id, tree id), the leftovers and their lifetimes, the disclosure facts. Nothing else lives here |
| `RaceWorld` | `Source/Parsek/RaceWorld/` | features | no (runtime-coupled) | Everything live that is not a read of the own model: the race session for the loaded save, the adapters behind the folder, clock and payload ports, the rival store, the rival side of the ghost lane (catalog, policy, leftovers, flags, map presence), the four sites (space center move, terrain patches, scenery), looks |
| (existing) `UI` | `Source/Parsek/UI/` | presentation | no | `SpaceRaceWindowUI`, `RaceSetupDialog`, the Settings section; drawn from `RaceCore` row models |
| (existing) `Controllers` | root files | entry | no | `ParsekRaceMenu.cs` (the main menu entry point; it needs its own placement rule in `modules.toml`, a root file with no rule reds the map suite), `ParsekRaceScenario.cs` (the race mark's scenario module), and the composition of the rival lane in each scene controller (15.6) |
| (existing) `Patches` | `Patches/` | entry | no | Guards that must sit on stock entry points (for example a launch from a rival's site) |
| (tooling) `TestCommands`, `InGameTests`, `UI/Gallery` | | | | Seam verbs for the harness lanes, the in-game test category, the window's gallery mock states |

Why three modules and not one: the 1.1 map assigns a module to exactly one layer, and
multiplayer has a half that is rules over data and a half that is Unity objects. Keeping the
rules in their own headless module means the whole race (export, import, tamper checks, the
board) runs in the unit-test host with a fake folder, and CI's headless ratchet stops game
calls from leaking into it. The third module exists for one reason: the map ratchets edges
BETWEEN modules, not between files of one module, so "exactly one place reads the player's
own model" can only be a build property if that place is a module of its own.

### 15.3 Ports: the pure core and its thin shell

`RaceCore` owns the logic and declares what it needs from the outside as small interfaces.
`RaceWorld` and `RaceExport` implement them. This is the house pattern (pure decision
library, thin I/O shell), applied to the whole feature.

| Port (declared in `RaceCore`) | Real adapter | Test double |
|------|------|------|
| `IRaceFolder`: list, read, safe-write and delete files under the LOCAL race directory (the own mirror and the rival cache) | `RaceWorld`, on `FileIOUtils` | an in-memory folder; also the synthetic shared-folder fixture builder |
| `IRaceTransport`: ask for files to be copied between the shared folder and the local race directory, and report what has arrived | `RaceWorld`, the copy step of 15.9 | an in-memory copy that completes at once, or on demand to test half-synced states |
| `IRaceClock`: real time in UTC | `RaceWorld`, system clock | a settable clock |
| `IPublicStateSource`: the player's public state as one immutable `PublicState` | `RaceExport`, `RaceExportSource` | a hand-built `PublicState` |
| `IRacePayload`: the content hash and the local path of a recording's files by id | `RaceWorld`, over the save's sidecar versions and the rival cache | byte arrays |

The sync is then one pure call, `RaceSync.Run(folder, transport, clock, source, link)`, that
returns a `SyncResult`: what was exported, the new `RivalState` per rival, the board, the
messages to show, the marks. The shell applies the result. Nothing inside the call touches
the game, and nothing inside it waits on the shared folder.

### 15.4 Dependency rules, enforced

What the 1.1 map can and cannot enforce, said exactly, because the first draft overstated it:
a reference from a lower layer up to a higher one is counted and ratcheted; a reference
between two modules of the SAME layer is only reported. `RaceWorld` sits in the features
layer beside Ghost, Rewind, Missions, Logistics and the rest, so without more it could name
any of them freely. Every property this design relies on is therefore written as a
`[forbidden]` edge, which reds on any layer.

Allowed references:

- `RaceCore` references only the floor (`LogIO`, `Config`, `Core`, `Trajectory`).
- `RaceExport` references `RaceCore`, the floor, `Recording`, `Rewind` (`EffectiveState`),
  `GameActions` (milestone rows) and `Missions` (the vessel identity resolver and names).
- `RaceWorld` references `RaceCore`, the floor, `Recording` (the sidecar codecs and the
  recording data types, inside the loader only) and `Ghost` (the visual builders and the
  primitives of the ghost lane).
- `UI` references `RaceCore` (row models) and the `RaceWorld` facade (commands: sync now,
  show or hide a rival, create, join).
- `Controllers` and `Patches` reference the `RaceWorld` facade and `RaceExport`'s source,
  and compose them.

New `[forbidden]` edges in `scripts/arch/modules.toml`:

```
Nothing below the entry and presentation layers may name the race:
  Recording   -> RaceWorld | RaceExport | RaceCore
  GameActions -> RaceWorld | RaceExport | RaceCore
  Crew        -> RaceWorld | RaceExport | RaceCore
  Rewind, Missions, Logistics, SwitchIntent, Reaim, Timeline,
  Ghost, MapRender, Display, Rendering -> RaceWorld | RaceExport

The rules module names nothing above the floor:
  RaceCore -> Recording | GameActions | Crew | Ghost | Rewind | Missions |
              Logistics | RaceWorld | RaceExport | UI | Controllers

The live half cannot reach the player's own model:
  RaceWorld -> GameActions | Crew | Rewind | Missions | Logistics |
               SwitchIntent | Reaim | Timeline | RaceExport

The reader cannot show anything:
  RaceExport -> Ghost | MapRender | Display | Rendering | RaceWorld
```

Read plainly: the ledger, the roster, the recorder, rewind, missions and routes cannot name
anything of the race; the ghost and map code does not know rivals exist (it is handed
trajectories and a catalog, 15.6); the half that draws rivals cannot name the ledger, the
roster, rewind, missions or routes at all; and the one module that reads the player's own
model cannot draw.

Consequences the reviews pointed out, each with its answer:

- The standing codec validates ids and the exchange format has a version. Both helpers live
  in `Recording` today (`RecordingPaths.ValidateRecordingId`, `SaveFormat.cs`), which
  `RaceCore` may not name. The pure id check moves to the floor (prerefactor P7); `raceFormat`
  is registered in `SaveFormat.cs` by the shell, and `RaceCore` carries its own constant that
  a test pins equal.
- The milestone table is a rule, so it stays in `RaceCore`, but it cannot read ledger rows.
  `RaceExport` hands it neutral rows; the rule picks the earliest per ladder id.
- The launch-area predicate takes plain coordinates, not a recording type.
- The later flight checks (7.15, step B) reuse the offline analyzer's rules, which are typed
  on `Recording` and live in a tooling module. Where they run (on the loader's private
  `Recording` inside `RaceWorld`, or after the rules are generalized to trajectories) is
  decided when that step is planned; nothing in the first build depends on it.

Finer gates the module map cannot express, as grep-audit gates in the `GrepAuditTests`
family:

- **No store in the rules.** `RaceCore` names no `*Store` type. (The map alone does not give
  this: `MilestoneStore` and `GroupHierarchyStore` are floor types.)
- **No writer of the own model.** No file of the three race modules names a store's mutating
  entry point or a persistence writer of the save (the same shape as the GUI gallery's
  write-set gate). Two things the race does write, stated so the gate's allowlist is honest:
  the suit of the player's OWN kerbals (`RaceLooks`, stock roster fields, cosmetic) and the
  position of the stock space center objects (`RaceSites`).
- **The rival store's type names** appear only in an allowlisted set of files (6.4).

Plus the headless test and the harness lane of 6.4, and `RaceCore` joins
`[headless.modules]`.

### 15.5 Rival data is its own type

A rival's recording is never an instance of `Recording` outside the loader. The rival store
holds `RivalFlight` values: sealed, immutable after load, implementing `IPlaybackTrajectory`
(the interface the playback engine reads trajectories through), plus the owner's player id
and the leftover window. A method that takes a `Recording` cannot be handed a rival flight
by mistake, so the compiler enforces what the forbidden edges enforce at module level. The
existing sidecar codecs fill it (prerefactor P6; the fallback is a private inner `Recording`
that never leaves the loader).

What a `RivalFlight` deliberately answers differently from an own recording:

- **No flag events.** The engine plants a real flag for any trajectory that has them; a
  rival flight has none to give. Its flags are kept beside it for the `RivalFlags` presenter
  (6.4, 7.7).
- **No loop.** Loop playback anchors to a live vessel of the local game by its id. A rival
  flight reports loop playback off and no anchor, always.
- **Segment events through the interface.** Two places in the part-event code cast the
  trajectory back to `Recording` to read its segment events; a rival flight would silently
  get none. Those reads move onto the interface (prerefactor P8).

Rival ids live in their own space: every key the display code holds for a rival ghost is the
pair (player id, recording id), never a bare recording id and never a committed-list index.

### 15.6 The rival ghost lane

**Rival ghosts play on their own instance of the playback engine.** That much of the first
draft stands. What the technical review corrected is the claim that this is nearly free:
the engine is an instance, but it and its collaborators reach into the player's own stores
in about 120 places, and a second instance would read the same stores.

What was checked in the code:

| Piece | Today | What the rival lane needs |
|-------|-------|---------------------------|
| `GhostPlaybackEngine` | An instance; takes a list of `IPlaybackTrajectory`; its only static is a test hook; in-game tests build extra instances. But it looks up trees and recordings by bare id in `RecordingStore.CommittedTrees` and `PendingTreeSlot`, and calls a static of `ParsekFlight` | A catalog handed in at construction (below) |
| `IGhostPositioner` and `ParsekFlightGhostPositioner` | The interface is placed in `Controllers`, the entry layer; the implementation is built on a `ParsekFlight` host and reads the own stores | The interface moves down to the floor; the implementation takes the catalog |
| Relative-section anchors (`RecordedRelativeAnchorPoseResolver`) | Resolve the anchor recording from the own stores only | Through the catalog, or a rival's rendezvous, docking approach or EVA beside its lander will not be placed |
| `GhostMapPresence` | A static class of about 16,600 lines with about 20 static stores keyed by committed index or vessel id; its tracking-station file is typed on `Recording` | Not made per-lane. Rivals get a small map presence of their own (below) |
| `WatchModeController` | Keyed by committed index; needs a ghost that is live at the current game date | A lane-qualified key; rivals are watchable only while live (7.7) |
| `ParsekKSC.Playback.cs` | The Space Center scene's own playback with its own slots | A second slot set for the rival list |
| `ParsekFlightPlaybackHost`, `ParsekPlaybackPolicy` | Drive and police the own engine; read the own stores | Not reused for rivals: the rival engine gets its own small host and policy |

The design that follows:

- **A catalog port.** One small interface on the floor, `IPlaybackCatalog`: find a tree or a
  recording by id, the pending tree, the chain and anchor lookups the engine, the positioner
  and the anchor resolver need. The own lane's catalog is a thin wrapper over the stores,
  with no behavior change. The rival lane's catalog is the rival store. After this the
  engine and the positioner do not name a store at all, which is also a cleaner engine for
  single player. This is the largest prerefactor (P2).
- **Composition at the entry layer.** The scene controllers already build the own engine
  and its positioner. In a race save they build a second engine and a second positioner with
  the rival catalog, which they get from the `RaceRuntime` facade. `RaceWorld` supplies data
  and policy; it does not construct the engine, so it never has to name an entry-layer type.
- **A second instance has its own slot index space**, so the committed-list index contract
  (`RecordingStore.CommittedListNotifications.cs`) is untouched and no rival ghost can sit at
  a committed index. The rival list is replaced only at scene load (7.6), so the rival engine
  needs no mid-list insert or remove notifications at all.
- **`RivalPlaybackPolicy`** receives the rival engine's lifecycle events. It has no spawn
  path, no resource deltas, no chain logic, no crew and no CommNet: it only decides
  visibility (the show toggle, launch areas) and feeds the watch camera. The spawn machinery
  is simply not wired to this engine. That is the structural form of "rivals are only seen".
- **Leftovers are entries of the rival engine in a hold mode**: the flight's ghost kept at
  its last moment inside its leftover window. Not a separate static presenter, as the first
  draft had it: an object standing on a body still has to follow the floating origin, an
  object in orbit moves, and a separate presenter would sit outside the ghost budget and the
  distance rules.
- **A small rival map presence** (`RivalMapPresence`) for orbit lines, map icons, targeting
  and the Tracking Station: it uses the same primitive own ghosts use (a ghost map vessel,
  registered in the guard set every vessel walk already checks) through a small extracted
  service, with its own stores keyed by (player id, recording id). Lane-qualifying the
  existing `GhostMapPresence` would touch most of its 16,600 lines for a lane that never
  spawns, never chains and never loops (prerefactor P3 is the extraction of that primitive).
- **Flags** go through `RivalFlags` (plaque replicas, 7.7).
- **Neutral seams for what the ghost code must not know.** The ghost and map code may not
  name the race, yet rival ghosts need a color on their label, icon and orbit line, an owner
  name, a visibility filter and a share of the ghost budget. Each is a neutral value on the
  floor that the lane is constructed with: a style (color and label prefix) per trajectory,
  a visibility predicate, and one budget object both engines draw from, own ghosts first
  (prerefactor P9).
- Each scene controller reaches all of this through one facade, `RaceRuntime`, with four
  calls: scene ready, per-frame tick, scene exit, and sync. In a single-player save the
  facade is absent and the calls are one null check.

The sizes of P2, P3 and P9 are not known from reading. Phase R0 therefore starts with an
inventory (every store read on the playback path, by file, with the catalog member that
replaces it), and the prerefactors are sized from it before any is started.

### 15.7 State and lifetimes

| State | Where | Lifetime | Rule |
|-------|-------|----------|------|
| `RaceLink` | Its own scenario node in the save (6.2) | the save | Holds only identity and choices that never change (race id, player id, site, table ids, date unit). So every load kind (cold, quickload, rewind, Re-Fly, revert) can take it from the loaded save with no special case; `LoadReconcilePolicy` gets one row saying so. The gate reads it from the game's scenario node list, which exists before any scene is built |
| `RaceSession` | memory | the loaded save | Built at load from `RaceLink`; dropped by the cold-load reset; tagged in `scripts/arch/lifetimes.toml` |
| Rival store | memory | the loaded save, rebuilt each scene | Replaced whole, never edited in place |
| `RaceLocal`: folder location, display name and look, show toggles, last export digest | `Race/local.cfg` beside the save | the machine | Not in the save, so no load rolls a setting back; missing file means "ask for the folder again" and the name and look are read back from `player.cfg` |
| Rival cache, witness log, own mirror | `Race/` beside the save | until deleted | Never named by the save, never synced. VERIFY at planning that the save-folder sweeps, the file collector and the pre-Parsek backup footprint check treat `Parsek/Race/` correctly |
| Applied sites (moved space center, terrain patches, scenery) | Kerbin's own objects | the process, across saves | The one piece of state that outlives a save: `RaceSites` owns apply and restore as a pair. Apply runs when a race save starts, before its first scene; restore runs on every return to the main menu and before a different race save applies its own; an in-game test pins stock Kerbin after leaving a race save (7.3 lists what must be reset) |
| Exchange files | shared folder | until pruned by their writer (6.3) | `raceFormat` is registered in `SaveFormat.cs` with a golden fixture folder, like every persisted format; unknown blocks in a newer standing are ignored, the additive rule of the save-format policy |

### 15.8 Inert outside a race

Every entry point of the feature sits behind one predicate, `RaceGate.IsRaceSave`, read from
the race mark. In a single-player save no race object is constructed, no site is applied,
no poll runs and no file is touched. The proof is mechanical: the existing unit suite, the
harness lanes and the architecture ratchets must be unchanged by the multiplayer code while
no race save is loaded, and one test pins that a single-player save's output is byte-identical
with the feature compiled in.

### 15.9 File access and the main thread

Parsek starts no threads, and a grep gate with an empty allowlist keeps it so (in-game text
formatting relies on the main thread's culture). Shared folders are slow and can stall: a
cloud "placeholder" file downloads when first opened, and an offline network share can hang
an open for many seconds.

The first draft kept the no-thread rule and bounded each folder operation with a byte and
time budget. The technical review is right that this cannot work: a budget can stop the game
from starting the NEXT read, but it cannot interrupt one open call that the operating system
is blocking. So the design changes:

- **The game never touches the shared folder from a Unity callback.** Every read the game
  does (at load, at a sync tick, when a ghost needs its file) is from the local race
  directory beside the save.
- **One copy worker moves bytes between the shared folder and the local race directory**,
  in both directions, and computes the hash of what it copied. It parses nothing, formats
  nothing, logs through a queue the main thread drains, and never touches a game object. It
  is one allowlisted site in the background-thread gate, and the main thread talks to it
  only through `IRaceTransport` (ask for a copy, read what has arrived).

This is an exception to a standing repository rule, so it needs the owner's yes when the
feature is planned; spike S8 (15.11) measures the stall on the common sync clients first, so
the decision is taken on numbers. If the answer is no, the fallback is the first draft's
budgeted main-thread steps with the known risk of a multi-second freeze on a placeholder
file, and `IRaceTransport` is the seam that makes either one a local change.

### 15.10 Built to extend

Choices made now so later ideas need no restructuring:

- **N players in the data, four in the world.** Standings, the rival store and the board take
  any number of players. Four is a property of the site table and the looks only.
- **Tables are data with ids** (`siteTableId`, `ladderId`): a new ladder or site table is a
  new id, and old races keep theirs.
- **A rival does not have to be a person** (owner note 2026-11-14: AI or recorded flights
  playing as another player, later). Nothing in the import, the rival store, the ghost lane
  or the board asks who wrote a player folder, so this needs no restructuring, but four
  things are fixed now so it stays true:
  - *The player entry says what it is* (`kind`: human, recorded, AI). The window can label
    it, and rules can treat it differently: a recorded opponent never "sees" anything, so
    the no-retakes rule and the receipts skip it, and it does not count as a player for
    "everyone has joined".
  - *A recorded opponent is a finished player folder*: one manifest, one final standing and
    its payload, copied into `players/` by the race creator (or shipped as a pack). Its
    flights were launched from one site, so it carries its site slot and takes that seat;
    slot assignment already allows a seat taken out of order (7.3). A pack records the mode,
    the difficulty, the mod list and the site table it was flown with, and a race refuses a
    pack whose site table differs. Its dates are all known from day 1, which fits the ruling
    that rival dates are always shown: it is a pace car.
  - *An AI is a writer of a player folder.* Whatever produces the flights (a script flying a
    second game, a planner that picks from a library of recorded flights as the race goes
    on), it joins by writing standings into its own subfolder, exactly as a player's game
    does. So the shared folder format IS the interface for it: it is documented in this
    document, versioned by `raceFormat`, and changed only additively. The pure `RaceCore`
    codec can be used outside the game for that reason (it has no game types).
  - *Fair play still applies.* A recorded or AI player is marked as such on the board, and
    whether it can hold firsts and win, or only sets the pace, is a race setting.
  - *The solo race* (owner note 2026-11-14) is the first use: one person against one to
    three recorded opponents, to get a feel for a race without friends. It needs no shared
    folder at all: the opponents ship with the mod (or as downloads) as finished player
    folders under `GameData/Parsek/Opponents/<name>/`, and a solo race takes them through the
    same transport port a shared folder is copied through (15.3). The setup dialog gains a third
    choice next to Create and Join, "Race a recorded opponent", with the opponent's name,
    look, site and difficulty.
  - *Where recorded opponents come from:* the automated flight harness. Its mission library
    already flies real missions hands-free (pad hops, orbits, Mun and Minmus landings and
    returns, flybys and orbits of the other planets) and each one leaves ordinary Parsek
    recordings. An opponent is produced by a harness campaign that plays a race save from
    one site and flies a chosen list of missions at chosen game dates; the player folder that
    save exports is the opponent. Difficulty is the schedule: an easy opponent lands on the
    Mun on day 120, a hard one on day 25. Because each is really flown, every flight is
    watchable and passes the same checks a human's would. Generating them is later work.
  - *Any playthrough can become an opponent* (owner note 2026-11-14: "if I or another
    player play a career save, that all gets recorded anyway, maybe we could use that as the
    AI player later, of various difficulty depending on how good the initial player was").
    A player's public state is already exported as one complete snapshot, so "save this
    playthrough as an opponent" is the same exporter writing a finished player folder to a
    chosen place. It works from a race save and from an ordinary single-player save. How
    hard the opponent is follows from how well it was played, and a set of them is a
    difficulty ladder. Two limits follow from what a recording is: an opponent keeps the
    site it launched from, and an ordinary single-player save launches from the stock KSC,
    so a race can hold one opponent made from a single-player save and the person racing it
    takes another site; and an opponent carries the mode, difficulty and mod list it was
    played with, which the race it is added to must match or disclose.
  What is deliberately NOT designed now: how a live AI would decide what to fly, re-timing
  a recorded program to keep pace with the humans, or moving a recorded program to another
  site (an opponent recorded from site 1 always sits at site 1).
- **Verification is a pipeline of checks** over (claim, rival flight) returning marks. The
  first build registers one check (the flight exists and covers the date); the flight checks
  of 7.15 are more entries in the same list. A signature field is reserved in the standing.
- **House rules are pure functions** from the set of standings to holders, so a new rule is
  one function and one manifest flag.

### 15.11 Prerefactors and extra spikes

Changes to single-player code that multiplayer needs. Each lands on its own, before the
feature, with no behavior change, proven by the existing suites and lanes. They are not all
small: P2 and P3 touch the playback path widely, and their size is set by the R0 inventory
(15.6), not by this table.

| Id | Change | Why |
|----|--------|-----|
| P1 | One source for "where is the working space center": today the pad and runway coordinates are constants in `SpawnCollisionDetector`, with related readers in `LaunchSiteExclusionZones` (which also caches its circles), the launch-site departure rule and the route origin proof. A `HomeSpaceCenter` value in `Core` that returns the stock numbers unless a race sets it, and a reset for the cached circles | The home site moves in a race save |
| P2 | The catalog port: `IPlaybackCatalog` on the floor, handed to the engine, the positioner and the relative-anchor resolver, replacing their direct reads of `RecordingStore` and `PendingTreeSlot`; `IGhostPositioner` moves from `Controllers` to the floor; the committed-index member `LoopSyncParentIdx` leaves `IPlaybackTrajectory` | The rival lane (15.6). The largest prerefactor |
| P3 | Extract the ghost-map-vessel primitive (create, register in the guard set, update orbit, remove) from `GhostMapPresence` into a small service; lane-qualified key in the watch camera; a second slot set in the Space Center scene's playback | Rival map presence and watching (15.6) |
| P4 | (dropped) Options on the flag replay spawn | Rival flags are plaque replicas, not flag vessels (7.7) |
| P5 | A kerbal-look hook on the EVA ghost build: which suit model and texture to use, including a substitute when the recorded suit's part is not installed | Looks on ghost kerbals (7.4) |
| P6 | Let the sidecar codecs decode into a plain trajectory value, with `Recording` as one consumer | `RivalFlight` (15.5) |
| P7 | Move the pure recording-id check to the floor (`RecordingPaths.ValidateRecordingId` keeps calling it) | The standing codec validates ids without naming `Recording` (15.4) |
| P8 | Segment events through `IPlaybackTrajectory` instead of a cast back to `Recording` in the part-event code | A rival flight's part events (15.5) |
| P9 | Neutral ghost seams on the floor: a per-trajectory style (color, label prefix), a visibility predicate, and one ghost budget object shared by engine instances | Color, hiding and the shared soft cap without the ghost code naming the race (15.6) |

Spikes beyond S1 to S7 of section 12:

- **S8**: measure what a first open of a not-yet-downloaded file, and an open on an offline
  network share, cost on the common sync clients (Dropbox, Google Drive, OneDrive), to settle
  15.9.
- **S9**: which stock debug-menu actions (set orbit, set position, add science, unlock
  technology) can be observed by a mod, to settle the list of 7.12.

### 15.12 Code layout

| Concept | Code |
|---------|------|
| Race mark and gate | `RaceCore/RaceLink.cs`, `RaceCore/RaceGate.cs`; `ParsekRaceScenario.cs` |
| Manifests, standing codec, chain, witness log | `RaceCore/RaceManifest.cs`, `StandingCodec.cs`, `StandingChain.cs` |
| Slots and looks assignment | `RaceCore/RaceSlots.cs` |
| Site table and launch areas | `RaceCore/SiteTable.cs`, `RaceLaunchAreas.cs` |
| Public state, digest, sync pipeline, ports | `RaceCore/PublicState.cs`, `RaceSync.cs`, `RacePorts.cs` |
| Milestone table, ladder, board, house rules, checks | `RaceCore/MilestoneTable.cs`, `Ladder.cs`, `RaceBoard.cs`, `HouseRules.cs`, `ClaimChecks.cs` |
| Window row model | `RaceCore/RaceBoardPresentation.cs` |
| The one reader of the own model | `RaceExport/RaceExportSource.cs` |
| Session, facade | `RaceWorld/RaceSession.cs`, `RaceRuntime.cs` |
| Adapters | `RaceWorld/RaceFolder.cs`, `RaceTransport.cs` (the copy step), `RacePayloadFiles.cs` |
| Rival store, flights, lane | `RaceWorld/RivalStore.cs`, `RivalFlight.cs`, `RivalCatalog.cs`, `RivalPlaybackPolicy.cs`, `RivalMapPresence.cs`, `RivalFlags.cs` |
| Sites | `RaceWorld/RaceSites.cs` (space center move, patches), `RaceScenery.cs` |
| Looks | `RaceWorld/RaceLooks.cs`; suit sets under `GameData/Parsek/Suits/` |
| Menu entry, setup dialog, window | `ParsekRaceMenu.cs`, `UI/RaceSetupDialog.cs`, `UI/SpaceRaceWindowUI.cs` (on the `StructureListWindowUI` template, registered in `ParsekWindowSet`) |
| Flight checks (later) | `ClaimChecks` entries; where the analyzer's rules run is decided with step B (15.4) |

### 15.13 Cost: what the feature may spend

Ghost visuals must be "correct visually, minimal, and efficient" (the standing design
principle), and a race adds up to three more players' worth of ghosts. The budget, to be
measured in the spikes and pinned where it can be:

| Cost | Rule |
|------|------|
| Scene load | The rival store is built from the standings alone. A flight's trajectory file is opened when its ghost is first needed (7.6). Building the store for three rivals with hundreds of flights each must not be noticeable; a unit test pins that no payload file is opened by the build |
| Per frame | Rival ghosts and leftovers draw from the same ghost budget as own ghosts, own first, leftovers last (15.6). A hidden rival costs nothing |
| A sync tick on the main thread | The digest uses the content hashes the save already holds for its sidecars; nothing is re-hashed. The board is a table of at most four players by about a hundred milestones. The no-retakes rule and the chain check read the kept standings (6.3), not the whole history |
| During a flight | No polling, no import. One export after a commit: the public state is built in memory and the file copy is the copy step's work (15.9) |
| Scenery | Three building sets, always fully built, measured in spike S5 for load time, memory and frame cost at each site; a copy far from the camera is not built |
| Disk | The rival cache holds what each rival's applied standing names plus the kept standings; the shared folder holds, per player, the kept standings and the payload they name (6.3) |

### 15.14 Existing gates the feature has to satisfy

The 1.1 build guards its structure with tests that a new feature trips unless it registers
itself. Named here so the phase plans carry them, not discover them:

- `scripts/arch/modules.toml`: a placement rule for every new root file (`ParsekRaceMenu.cs`,
  `ParsekRaceScenario.cs`) and a layer for each new module; the forbidden edges of 15.4;
  `RaceCore` in `[headless.modules]` with its pins.
- `scripts/arch/lifetimes.toml` and `SaveScopedSessionResetTests`: every mutable static of
  the feature, with the applied-sites state tagged for the process and everything else for
  the loaded save.
- `LoadReconcilePolicy`: the dialog's Continue and the first start of a created save are
  loads through a declared initiator (`KnownLoadInitiators`), and the race mark gets its row
  in the load-kind table (it is taken from the loaded save by every kind).
- `SaveFormat.cs` and `SaveFormatGoldenGateTests`: `raceFormat` registered with a golden
  folder, although it is not a save format.
- The background-thread gate: one allowlisted site, if the owner allows the copy worker
  (15.9).
- The ERS / ELS routing gate: `RaceExportSource` reads through `EffectiveState`, so it needs
  no exemption.
- The GUI gates: the new window in `design-gui-inventory.md`, its `UiSurface` key, the
  window and tab vocabularies `GuiCensusSeamVerbTests` pins, its gallery mock states, and
  the UI-scale source gate.
- The save-folder sweeps, the file collector (`collect-logs.py` copies every `Parsek/<dir>`
  beside a save; `Race/` holds other players' files and should be listed, not copied whole)
  and the pre-Parsek backup (a race save is created by Parsek, so it has a footprint from
  birth and no backup is taken).

---

## References

- `docs/dev/done/design-coop-async-multiplayer.md` - the deprecated co-op design; its sections
  4.1, 6.3, 7.1 and 13 are the ancestry of the exchange here.
- `docs/roadmap.md` Phase 15.
- `docs/parsek-game-actions-and-resources-recorder-design.md` - milestone rows and the
  once-ever walk the milestone table reads.
- `docs/dev/design-load-restores-the-save.md` - the load contracts a race save keeps.
- `docs/dev/design-save-format-policy.md` - what a build keeps of data it does not know, and
  why the race mark is a scenario node of its own (6.2).
