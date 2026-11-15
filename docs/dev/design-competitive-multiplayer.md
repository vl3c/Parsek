# Parsek Multiplayer: the Space Race - Design Document

*Design specification for competitive async multiplayer: up to four players race each other
to the milestones of the Kerbal system, each in their own game, sharing through a synced folder.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings
to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost
vessels alongside new ones. This document specifies how several players, each in their own KSP
instance with their own save and their own game clock, run a space race: everyone sees the
others' space centers, flights and flags as ghosts and scenery, and one common board shows who
reached each milestone at the earliest game date.*

**Status:** DESIGN v3 (2026-11-15, pre-implementation). Written from the owner's rulings of
2026-11-11, 2026-11-14 and 2026-11-15 (section 0), a stock-code feasibility pass on KSP 1.12.5
(section 12) and a design consultation. v2 folded in three independent reviews of v1, a
measured inventory of the 1.1 code
(`docs/dev/research/multiplayer-base-refactoring-2026-11-14.md`) that sized what base Parsek
needs changed first (15.11), and two final reviews; section 13 lists the decisions still open.
v3 folds in the corrections found while writing the implementation plan
(`docs/dev/plans/multiplayer/design-corrections.md`, which stays as the record of what changed
and why; its line numbers refer to v2) and the owner's answers of 2026-11-14 and 2026-11-15.
Nothing here is implemented, and nothing is implemented before the owner says so (roadmap
"Current focus").
**Supersedes:** `docs/dev/done/design-coop-async-multiplayer.md`. Cooperative multiplayer is
deprecated (owner ruling 2026-11-14). Section 5 lists what this design keeps from it.
**Out of scope, permanently:** real-time synchronization; any shared vessel, shared crew or
pooled economy; any way for one player's data to change another player's game state.
**Related docs:** `docs/parsek-architecture.md`,
`docs/parsek-game-actions-and-resources-recorder-design.md` (milestones, the once-ever walk),
`docs/parsek-timeline-design.md`, `docs/parsek-ghost-trajectory-rendering-design.md`,
`docs/dev/design-load-restores-the-save.md`, `docs/dev/design-save-format-policy.md`,
`docs/roadmap.md` (Phase 15), `docs/dev/plans/multiplayer/README.md` (the implementation
plan).

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
  (decision card, 2026-11-14, decision D1). REPLACED on 2026-11-15: dates are compared to
  the exact second (below).
- Holding flights back and sharing them just before the end date is allowed (decision card,
  2026-11-14; 7.10).
- A flight flown with the cheat menu or a debug tool does not score: it is shown on the
  board, marked, worth nothing (decision card, 2026-11-14; 7.12).
- The shared launch sites need Making History, so whether a race uses them is a setting the
  creator chooses; when it is on, joining needs the expansion (decision card, 2026-11-14;
  7.3).
- A rival's space center buildings are solid: they can be crashed into and their runway can
  be landed on, like terrain (decision card, 2026-11-14; 7.3).
- Rival milestones dated after your own game clock are always shown (decision card,
  2026-11-14).
- A milestone completed with the debug menu (Max Progression, the debug console) does not
  score either, by the ruling on debug tools above (decision D3): the race drops such
  completions from the board and marks the player as having used a debug tool. Which of them
  a mod can observe is spike S9's finding (question Q-26; 7.12).
- The refactored base build ships as version 1.0.2. Where this document says "1.1" (the 1.1
  code, the 1.1.x build) it names that code structure, not a version (2026-11-14).
- Multiplayer ships in version 1.1.0 (2026-11-14).
- The two single-player launch pad defects (a parked flight whose stored longitude has wound
  past the date line, and alternate launch sites read before they were built) are fixed in
  base Parsek in a work item of their own, outside this feature, so the prerefactors change
  no behavior (answer to question Q-04, 2026-11-14; 15.11).
- The ghost positioner change is rechecked by a representative set of 18 lanes, one per way
  the positioner places a ghost: "there's no point in running every single test for that,
  we can do it later at release; pick the most representative tests to run to recheck the
  ghost system after changes". The run of every lane happens once, at release (answer to
  question Q-05, 2026-11-14; 15.11).
- The Watch camera on a rival flight is not in the first build. Rivals are seen on the map,
  in the Tracking Station and as ghosts nearby (answer to question Q-07, 2026-11-14; 15.6).
- The speed test of the cloud sync programs (spike S8) runs last, after the race works end to
  end on a plain shared folder: "leave this at the end, its an external dependency". The copy
  worker and the tuning of the poll wait on it; until then files are copied on the main
  thread with the default poll numbers (question Q-03, 2026-11-14; 15.9).

**2026-11-15**

- All multiplayer work lands on one long-lived `multiplayer` branch. Each piece of work is a
  sub-branch that merges into it. Main stays clean until one final merge of `multiplayer`
  into main.
- The Multiplayer menu entry is not hidden behind a developer switch: it is always visible on
  the `multiplayer` branch, which no player sees before the final merge (answer to question
  Q-08; 7.1, 14).
- The race's calendar (6-hour or 24-hour days) is the creator's own calendar setting at
  creation, shown in the setup dialog, with no field of its own; after the exact-second
  ruling below it only sets how dates are displayed (Q-09; 6.6, 7.2).
- A race save whose space center cannot be built (a planet mod changed Kerbin mid-game) is not
  loaded: one message, back to the main menu, the save untouched (Q-10; 7.3).
- Stock's "allow other launch sites" switched on mid-game in a race created without the shared
  launch sites: the race setting wins, launches from those sites stay refused and the stock
  option is greyed (Q-11; 7.3).
- One player writing the race folder from two computers at once: the game puts its files back
  twice, then stops putting them back and says so on screen (Q-12; 7.15).
- A first is announced on screen only when a rival is involved (beaten, lost to, or tied with
  in the very same second); a first no rival had reached shows only on the board (Q-14; 7.8).
- After the end date only final results are announced: one "the race is over" message, and a
  message when a late file changes the final board (Q-16; 7.8, 7.10).
- If the stock plaque window cannot show a rival flag's text (spike S7), one small Parsek popup
  may show it: a fourth new-window exception, below (Q-21; 7.7).
- A rival's ghost is labeled with its owner's name and color, such as "Ghost: Ana - Kerbal X",
  in the ghost label and in the map and Tracking Station lists (Q-22; 7.4).
- What the creator chooses, once, for everyone: the game mode; the difficulty, with the full
  stock difficulty panel (the four stock presets if the panel cannot be opened there); an
  optional end date (a real-world date and time; without one the race never ends); "no
  retakes", off by default; the shared launch sites, off by default (on means joining needs
  Making History); and the game's name. A race always has four seats. None of these changes
  after creation, and from then on the creator is a player like the others (decision cards;
  7.2).
- A player may change their own difficulty settings mid-game; nothing is blocked, and a mark
  next to their name tells everyone which settings changed (7.12).
- A cheat or debug tool marks every milestone the flight reaches after the tool was used, also
  after it is switched off (7.12).
- Rival ghosts can be targeted like the player's own, are never CommNet relays or control
  points, and do not show in the Space Center view in the first build (6.4, 7.7).
- Only flyable vessels a rival left behind (landers, stations, rovers, bases) stay as scenery;
  rival debris is not shown (7.7).
- Anyone with the shared folder may join; there is no join password. A player who stops
  playing keeps their seat and points, and nobody can remove a player (7.2; 8, cases 6, 7a).
- The Space Race window opens in the Flight and Space Center scenes, not in the Tracking
  Station (7.13).
- The race suit is locked: kerbals always wear the player's race look, and the suit choice of
  the stock suit picker is blocked in a race save (the owner chose this against the
  recommendation to leave the picker free; 7.4). Only the suit choice is blocked: the same
  stock window still lets the player take off a kerbal's helmet and neck ring and change the
  suit light color (owner, 2026-11-15).
- Milestone dates are compared to the exact second of game time, replacing the whole days
  of 2026-11-14 (decision D1): "we should just compare exact second when the milestone is
  achieved instead of using this tie mechanism; it's simpler and more realistic". The
  earliest second holds the first with full points. There are no same-day ties; only the
  very same second is shared, its points split exactly among those players. Players level on
  total points on the final board are joint winners, with no tiebreak step. The day length
  now only sets how dates are displayed. The owner accepted the costs knowingly (6.6).
- With "no retakes" on, any real, clean rival flight to a milestone that your game had
  already seen blocks you from taking that first earlier, even if that flight itself won
  nothing (answer to question Q-15; 7.11).
- A shared file changed after it was seen gets a neutral mark, "files in <player>'s folder
  were changed", visible to all; no points move and the last good state stays (7.15).
- The shared folder location is typed or pasted into a text field in the setup dialog and in
  Settings, checked on Validate; there is no browse button and no folder picker (7.2).
- Nothing is sent to rivals while a Re-Fly is in progress; sharing waits until it ends (7.5).

**UI exception.** The standing rule "no new player-facing UI surfaces" gets four owner
exceptions from the rulings above, all of them absent from single-player saves: the main menu
Multiplayer entry, the race setup dialog, the Space Race window, and, only if spike S7 shows
the stock plaque window cannot show a rival's text, one small popup for reading a rival's
flag (7.7). `.claude/CLAUDE.md` records them when the first one is built. That is the whole
list. Everything else the feature shows uses a surface that exists: a Multiplayer section in
the existing Settings window, hover text inside the Space Race window, one-shot screen
messages for events, and the stock confirmation dialog for the setup dialog's own
confirmations. Two race blocks sit on stock controls (the "allow other launch sites"
option, 7.3, and the suit choice in the stock suit picker, 7.4); both use the standing
stock-control annotation exception: the control's own disabled state and reason text on that
same control, with a click-block that reads the same predicate (for the launch sites option,
7.3 says what happens if stock's window does not let the toggle be greyed). There is no
folder picker (the location is typed
or pasted), no marker drawn at a rival's site, and no panel on a stock screen.

---

## 1. Introduction

A race is one whole multiplayer game: up to four players who each play a full Sandbox, Science or
Career game from day 1, compared across every milestone of the Kerbal system for as long as they
play (owner, 2026-11-15). It is not a contest to reach one chosen goal first, and there are no
race types or presets: every race has the same milestones and the same points (7.9). Each player
plays their own save, at their own pace, on their own game clock. Nobody needs to be online at the
same time. A folder synced by any file-sharing service (Dropbox, Google Drive, a network share)
carries each player's public state to the others. Correspondence chess, not real-time multiplayer.

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
| Player launches | From their own KSC (only they can), or, when the race uses them, from any stock alternate launch site (anyone can; they need the Making History expansion, 7.3) |
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
4. **Same files, same board.** Every score is a function of the files the players wrote
   and of nothing else: not of when a file reached one machine, not of a player's calendar
   setting. No machine is special and there is no server. One exception follows from having
   no server (7.15): a machine refuses a rival's file that was changed after it saw it and
   keeps scoring what it saw, so while a changed file sits in the folder, a machine that
   never saw the real one can show a different board. The writer's own game puts the real
   file back at its next sync, and the boards agree again.
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
    Nothing is refused for them (the one difficulty option a race holds is stock's "allow
    other launch sites", 7.3), and nothing is taken from the player's own game. The one
    thing that costs points is a flight flown with such a tool (7.12, owner ruling).
11. **Reuse before invention.** Ghost playback, map presence, the milestone rows and their
    once-ever walk, the shared window chrome and styles and the safe-write file helpers
    already exist. This feature adds the exchange, the sites, the looks, the board and its
    window.

---

## 3. Terminology

| Term | Definition |
|------|------------|
| Race | One whole multiplayer game, played by up to four players as full Sandbox, Science or Career games and scored across every milestone; not a contest for one goal. On disk: a manifest, a set of players, the ladder, an optional end date and house rules, rooted in one shared folder. |
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
| Game date | The game's own clock. Stored and compared to the exact second of game time; shown as a day and a time of day in the race's calendar (6.6). |
| Rival store | The in-memory, read-only set of rivals' recordings, flags and milestones built from the latest complete standing of each rival and the small tree files it names (never a trajectory or a craft file). Not part of the save. |
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
                                    of the last export, the time of the last sync
  race.cfg                          a copy of the race file, taken at create or join
  own/player.cfg                    a copy of this player's identity file
  own/standings/                    a copy of the standings this player exported
  own/payload/                      the payload of standings not yet whole in the
                                    shared folder, and of the last standing before
                                    the end date (6.3)
  rivals/<playerId>/                the local copy the game plays from:
    player.cfg                      a copy of that rival's identity file
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
        +--> ghost playback (flight, map, tracking station)
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
| Disclosure facts: mod list, difficulty settings and their digest, cheat and debug tool use per flight, whether test tooling ran in the save | Facility levels, tech, anything else of the career |
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
| Freshness | Always the folder's latest | The latest as of the last sync; a sync runs at the ticks of 7.5 (a save load, entering the Space Center or the Tracking Station, a slow poll there, Sync now) |
| Moving or renaming the shared folder | The game breaks until the path is fixed | The game plays on; only sharing pauses until the path is fixed in Settings |

**Decision: copy.** The shared folder is transport only. Only the copy step touches it
(15.9), apart from the setup dialog and the location change in Settings, which check and
write it when the player presses a button (7.2, 15.9); everything the game shows and
everything it scores comes from the local copy. The
same holds in the
other direction: an export is written to the local `own/` folder first and then copied out,
so a full or missing shared folder never loses an export, it only delays it.

The copy is a cache, not data: deleting `Race/rivals/` costs nothing but a re-copy at the next
sync, and it is never included in a save, a quicksave or a backup of the save file itself.
What it keeps is bounded: what each rival's standing in effect names, the few older
standings the rules need (6.3); a file no kept standing names is deleted at an import tick
outside a flight.

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
| Player identity: stable id plus display name | Kept (6.1, 7.2) |
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
  DIFFICULTY { }            - the difficulty every save is created from: an explicit,
                              ordered list of stock difficulty settings only, named
                              field by field (7.12); never the whole GameParameters
                              node, and no mod's custom settings node
  difficultyDigest: string  - hash of those values in that order, for disclosure
  ladderId: string          - which ladder table applies ("standard-1")
  dateUnit: string          - how game dates are compared; always SECOND in this
                              format (6.6)
  calendar: string          - KERBIN or EARTH: how long a displayed day is, the same
                              for every player (6.6); display only, it never affects
                              scoring; taken from the creator's own calendar setting
                              at creation (owner, 2026-11-15)
  alternateSites: bool      - the stock alternate launch sites are in use (7.3);
                              false unless the creator turns it on
  endRealTime: string       - optional; empty = no limit
  noRetakes: bool           - house rule (7.11); false unless the creator turns it on
  siteTableId: string       - which site table applies ("kerbin-equator-1", 7.3)
  parsekVersion: string     - informational
  MOD_LIST { MOD id version ... } - the creator's mods, informational

PlayerManifest (players/<id>/player.cfg, single writer = that player)
  playerId: string          - "p_" + Guid("N"), generated when the player joins
  displayName: string       - unique within the race (7.2)
  kind: string              - HUMAN (default), RECORDED or AI (15.10); the first
                              build writes and accepts only HUMAN
  look: int                 - 0..3 (7.4); changed by the clash rule at join (7.3)
                              or by the player in Settings (7.2)
  siteSlot: int             - 0..3 (7.3); never changes
  joinedRealTime: string

Standing (players/<id>/standings/<seq>.pstd, immutable, written LAST)
  seq: int                  - per-player, rising
  playerId: string          - must match the subtree
  exportedRealTime: string  - the writer's clock (6.5)
  prevSeq: int              - the seq of the standing prevHash names (empty for the
                              first); with it a pruned older standing ("history
                              incomplete") is told apart from another file under
                              that seq ("chain broken", 7.15)
  prevHash: string          - content hash of the standing with the next lower seq
                              in this player's folder (empty for the first); makes
                              the standings a chain (7.15)
  note: string              - optional: why this standing was written when the
                              public state did not change (a heal, 7.15)
  frontierUT: double        - latest committed game date
  raceFormat, recordingFormat, recordingSchemaGeneration: int
  RECORDINGS { REC id treeId startUT endUT prec precProof ghost ghostProof TOOL ... }
                            - every effective committed recording, its game-date
                              range and its two payload files: prec and ghost are
                              their 16-character hash names, precProof and
                              ghostProof their SHA-256 proofs (6.3). One TOOL child
                              (kind, detail, ut) per cheat or debug tool used while
                              it was flown, ut = the game date it was first used
                              (7.12); the codec derives ToolsFromUT, the least of
                              those dates, and the tool ids
  TREES { TREE id hash proof ... }
                            - their trees: each tree file's 16-character hash name
                              and SHA-256 proof. A tree file is the production
                              RecordingTree node holding only the tree's effective
                              recordings, written by the production writer and cut
                              to one key list: what the tree reader needs; per
                              recording id, vesselName, startUT, endUT,
                              parentAnchorRecordingId, chainId, chainIndex and
                              sidecarEpoch (the trajectory reader checks it); and
                              the terminal and endpoint keys (terminal state,
                              terminal orbit, end position, endpoint body,
                              isDebris) that leftovers are built from (7.6). No
                              other key of the save leaves it
  MILESTONES { M id ut recordingId since ... }
                            - the player's milestone table (7.8): every once-ever
                              stock milestone, by its stock qualified id
                              (Mun/Landing, ReachedSpace); the board maps the ids
                              to ladder rows (7.9), so a ladder retune needs no
                              new export. ut is the exact game time in seconds,
                              which the board compares (6.6).
                              One entry per date on record, so
                              usually one per milestone and two after a rewind
                              and an earlier re-earn; since = the seq of this
                              player's standing in which this exact date first
                              appeared, carried forward unchanged while the date
                              stands (7.10, 7.11)
  LEFTOVERS { L recordingId fromUT untilUT vtype ... }
                            - what the player's flights left in the world and for
                              how long (7.7); untilUT empty = still there; vtype =
                              the leftover's vessel type, so its map icon is right
                              from scene load (empty when unknown)
  FLAGS { F recordingId site placedBy plaque flagUrl body lat lon alt rot ut pickupUT ... }
                            - every flag planted in an effective recording, read
                              from its flag events: where, by whom, the plaque
                              text, from when (ut) and until when (pickupUT, empty
                              while it stands; 7.7)
  NAMES { }                                  - mission and vessel names
  DISCLOSURE { modDigest, MOD ..., difficultyDigest, DIFFICULTY { }, testTooling,
               DEBUG_COMPLETION ... }
                            - DIFFICULTY: the save's current values of the same
                              ordered list of stock settings, so the window can name
                              which settings differ from the manifest's (7.12);
                              testTooling: Parsek's test tooling ran in this save
                              (7.12); one DEBUG_COMPLETION row (milestone id, ut)
                              per milestone completed with a debug action a mod
                              can observe (spike S9; 7.12). Both are kept on the
                              race scenario node (RaceLink below)
  SEEN { RIVAL playerId seq hash ... }       - highest standing of each rival applied
                                               BEFORE this one was written, with its
                                               content hash: a receipt (7.11, 7.15)
  PART_MANIFEST { }                          - distinct part names used
  signature: string         - reserved, empty in the first build (7.15, step A)

RaceLink (the race mark; in the save, in a scenario node of its own, 7.1)
  raceLinkFormat: int       - version of the mark (starts at 1); registered in
                              SaveFormat.cs as the race-link row (15.7)
  raceId, playerId, siteSlot
  siteTableId, ladderId, dateUnit, calendar, alternateSites
  gameMode, difficultyDigest
  (identity and choices that never change after the save is created, so every
   kind of load can take it from the loaded save as is. The display name and the
   look are NOT here: they are cosmetic, may change, and live in player.cfg and
   in local.cfg)
  The same scenario node carries one child with the lifetime of the save, which
  every kind of load also takes from the loaded save: RACE_FACTS (test tooling ran,
  the debug completions; 7.12). No suit state is kept: the suit is locked to the
  look, and every load dresses the roster in it (7.4).

RaceLocal (saves/<save>/Parsek/Race/local.cfg; this machine only, not in the save)
  sharedFolderPath: string  - chosen in the setup dialog, editable in Settings (7.2)
  displayName, look         - as last written to player.cfg
  lastStandingDigest: string - hash of the last exported public state
  lastSyncRealTime: string  - when a sync last reached the shared folder (6.5);
                              Continue shows it (7.2)
  firstImportDone: bool     - the first import after joining has completed; until it
                              has, this game writes no standing (7.5)
  SHOW { RIVAL playerId bool ... } - the show-ghosts toggles
  Beside it: Race/race.cfg and Race/own/player.cfg, copies of the race file and of
  this player's identity file. Settings rebuilds the shared files from them (7.2),
  and the name and look are read back from the second when local.cfg is lost.

RivalCache (local files under saves/<save>/Parsek/Race/rivals/<playerId>/, not named
            by the save, never exported; rebuilt from the folder if deleted)
  the applied standing, the kept older standings (6.3) and the payload files the
  applied standing names
  witness.cfg: seq and content hash of every standing of that rival this machine
               has applied, with the local time it first saw each (7.15)

OwnMirror (local files under saves/<save>/Parsek/Race/own/, never synced)
  a copy of the standings this player exported, to detect and undo changes
  somebody else made to this player's subtree in the shared folder (7.15),
  and the payload of the standings that are not yet whole in the shared
  folder and of the last standing before the end date (6.3)

Board (derived, runtime only; section 7.8)
```

### 6.2 Changes to existing types

There is no owner tag on `Recording`, `RecordingTree` or `GameAction`: a rival's recording
never becomes one of those in the local stores (6.4). The tree and ledger types gain no new
fields; the ledger gains one additive action type (below).

The player's own data gains two small facts that base Parsek does not keep today. Both are
additive (an optional child node, a new enum member), so neither bumps a format (the save
format policy's additive rule), and both are written only in a race save, which is marked
from its creation (principle 9), so a single-player save stays byte-identical:

- which of the listed tools were used during the flight, each with the game date it was
  first used (7.12; addition A3 of 15.11), kept on the recording. Single player detects
  them and writes log lines only, so there is nothing to derive this from at export time;
- that a committed flight's vessel was later deleted or destroyed outside any recording
  (7.7; addition A2). Today nothing records that end. It is kept in the ledger as a new
  action type, `VesselEnded` (the recording, the vessel name, deleted or destroyed), so it
  follows the effective ledger, tombstones and deduplication like a recovery row and moves
  no pool. After a rewind to before it, it behaves as single player's recovery row does
  there (to verify in the code). A destroy event also fires when a scene unloads and when
  the game quits, so the destroyed end is taken from stock `Vessel.Die` (to verify by
  decompile), and a vessel a recorder is recording is left to the recorder.

The race mark is NOT a child of the PARSEK node. It is the node of a second, tiny scenario
module (`ParsekRaceScenario`), for two reasons the reviews found:

- A build without race code does not carry unknown children of the PARSEK node (the
  save-format policy holds named stores, not arbitrary children), so a mark stored there
  would be lost the first time an older build saved the game. A scenario node whose type the
  running build does not know is kept by stock as it is and written back (VERIFY in spike S6:
  stock keeps a `SCENARIO` node of a missing module type across a save, and so do the save
  files Parsek itself rewrites: the rewind copies, the Discard Re-fly write of
  `persistent.sfs` and the test-batch crash reconcile).
- The mark is needed before any scenario module has loaded: the sites must be applied before
  the Space Center scene builds (7.1). The game's list of scenario nodes is available as soon
  as the save is read, so the race gate reads the mark from there. For the same reason a new
  race save gets its mark before it first starts: the Space Center re-reads
  `persistent.sfs`, so the mark is added and the save written before the game starts (7.2).

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
  A standing written while the folder was out of reach can find, when it is copied out, a
  file of the same seq that another machine wrote for this player meanwhile. It was never
  published, so no receipt names it: the exporter renumbers it and every later unpublished
  standing to follow the folder's highest seq.
- **Every standing is copied out, in order.** A standing is written to the local own mirror
  first and copied to the shared folder when it is reachable. Standings written while it was
  not are copied later, oldest first, so the chain in the folder has no hole of the writer's
  making. The payload goes the same way: when the exporter writes a standing it copies the
  payload files that standing newly names into the own mirror (the bytes the hashes were
  taken from), and each stays there until it is in the shared folder. The save may rewrite
  a recording in the meantime (an optimizer merge, an in-place Re-Fly); the mirror's copy
  is what keeps the older standing whole.
- **Payload identity.** A recording's payload files are named by recording id AND content
  hash (`<recordingId>.<hash><suffix>`), the naming the save's own sidecar versions already
  use (`SidecarVersions`). A recording id alone does not identify content: an in-place Re-Fly
  continuation and an optimizer merge keep the id and change the bytes. With the hash in the
  name a payload file is immutable by construction, a rewritten recording is simply a new
  file, and nothing is frozen on the owner's side. For the trajectory file (`.prec`) the
  exporter takes the hash from the sidecar version the save already recorded, so it is not
  re-hashed at every sync. The `.prec` carries the sidecar epoch, so a commit flush and the
  next save write different bytes for the same trajectory: a recording whose epoch is still
  waiting to advance is not exported until it has. A ghost payload file is never the save's
  own bytes (next item), so its hash is computed once, when the exporter writes it. That
  16-character hash is a name, not a proof (it is the first 64 bits of SHA-1). What the
  chain, the receipts and the payload check of 7.15 compare is a SHA-256, computed once
  when a file is first copied (15.9) and carried in the standing beside the file's name.
- **A ghost snapshot of its own.** Every ghost payload is a copy made for export. A ghost
  snapshot as the save keeps it is a full vessel node whose parts name their crew, and the
  roster is never shared (4.3); a recording whose ghost looks exactly like its vessel keeps
  no separate ghost snapshot file at all and uses its vessel snapshot, which is never
  shared either. So the exporter writes into the payload a ghost snapshot derived from the
  save's ghost or vessel snapshot, parts only: the `crew` values are stripped, and the
  parts, their modules and their resources are kept (to verify on a real snapshot that
  nothing else private remains).
- **Export only what is on disk.** The exporter runs after the save's sidecars are flushed,
  copies the files, and hashes the COPY. A standing never names bytes that were still
  changing.
- **What is kept.** A standing is a full snapshot, so the folder would grow without bound if
  every one were kept. A player's exporter keeps in the shared folder: the last 20 standings;
  every standing that is the `since` of a milestone date listed by the latest standing or
  by the last standing stamped before the end date (those answer "when did this date first
  appear", 7.10 and 7.11); and the last standing stamped before the end date itself (7.10),
  whose payload the own mirror also keeps for good so it can be put back. It deletes the
  rest, and payload none of the kept standings names, 3 days of real time after it stopped
  being kept. Importers keep the same set in the rival cache, pruned at import ticks outside
  a flight, so the board, the final board and the no-retakes rule are all computed from the
  local copy.
- **Versioning.** `raceFormat` versions the exchange files. A standing with a newer
  `raceFormat` is not applied: the rival's board rows are still shown if the milestone block
  parses, their ghosts are not, and the Space Race window says "Ana runs a newer Parsek". The
  board rows of such a rival are marked "not checked here", because this build cannot verify
  them (7.8). Recordings in a newer recording format than the local build reads stop only
  that rival's ghosts, and the window says "Ana runs a newer Parsek" for them too: the check
  of a board row reads only the standing, so those rows are checked and scored as usual.
- **Id safety.** Every id that becomes part of a path passes the recording id validator
  first (today `RecordingPaths.ValidateRecordingId`; its pure check moves to the floor so
  the rules module can call it, prerefactor P7). The moved check uses a fixed character set,
  today's Windows set, so nothing changes on Windows: today's check asks the operating system
  which characters are invalid, so the same id can pass on Linux and fail on Windows, and a
  shared folder is read by both. A standing with an unsafe id is refused whole.
- **Path length.** Windows paths are limited to roughly 250 characters, and the cache sits
  under `saves/<save>/Parsek/Race/rivals/<playerId>/`. The cache uses short directory names
  (the first 8 characters of a player id, checked for collision), the setup dialog caps the
  race save's name, and the validator of the shared folder location refuses a location whose
  longest expected path would not fit. The longest expected paths, in the shared folder
  under `players/<id>/` (4.1) and in the local cache, are computed from the longest id, hash
  and suffix of each layout, which the exchange code fixes.
- **Local writes** into the shared folder are safe-writes (temporary file, then rename) with
  retry and backoff, because sync clients hold brief locks. `FileIOUtils` has the safe-write
  today, but it has no retry and logs through `ParsekLog`, so the copy step (15.9) keeps a
  small copy routine of its own with the retry. A safe-write leaves temporary names behind
  (`.tmp`, `.bak.<guid>`) that a sync client uploads like any file; the file-name grammar of
  the shared folder ignores them.

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
- The consumers of the rival store are exactly: the rival ghost lane (flight, map view and
  the Tracking Station; not the Space Center scene in the first build, 15.6), the rival flag
  presenter, the board, and the Space Race window.

The reviews found four side channels that the rule above does not close by itself. Each is
closed by a design choice, not by a guard added later:

| Side channel | What would leak | Closed by |
|--------------|-----------------|-----------|
| Replayed flags are real, saved vessels | The playback engine itself plants a flag for any trajectory that carries flag events (`GhostPlaybackLogic.ApplyFlagEvents` reaching `GhostVisualBuilder.SpawnFlagVessel`, which adds the flag to the saved flight state). A rival flag would be saved, would count as "a flag already stands here" for the player's own flag replay, and would be a vessel stock can switch to, recover, target and scan for contracts | A rival flight hands the engine NO flag events. Rival flags are drawn by their own presenter as plaque replicas that are not vessels (7.7) |
| Map presence uses real proto vessels | A ghost with an orbit line is a vessel in the stock vessel list, which stock walks for progress, contracts and CommNet | Rival map objects are registered in the same guard set own ghosts use (`IsGhostMapVessel`), which every vessel walk already checks; each entry records its owner, so an own cleanup or rewind never unregisters a rival (15.6); and an in-game test pins that a rival ghost crossing a sphere of influence or landing changes nothing in the local progress or ledger (7.7, 11) |
| Ghost CommNet | An own ghost counts as a CommNet relay, as the vessel it replays. A rival relay would give the local player probe control and science transmission | Rival ghosts are never CommNet nodes: the rival lane is not wired to `GhostCommNetManager` at all |
| Scenery that follows a rival | Buildings whose shape follows a rival's facility levels would change collision geometry under local vessels when a rival's standing arrives | Scenery is the same in every game and never changes: always fully built (7.3). The standing carries no facility levels |

Mechanical gates, so the invariant stays true as the code changes:

- `[forbidden]` edges in `scripts/arch/modules.toml` (listed in 15.4): the ledger, roster,
  recorder, rewind, mission and route modules cannot name anything of the race. In the other
  direction: the rules module names nothing above the floor; the live half (`RaceWorld`)
  cannot name the ledger, the roster, rewind, missions or routes, and may name the
  `Recording` module only for its file codecs and data types, inside its loader; only the
  export module reads the player's own model.
- For the part of that the `[forbidden]` edges cannot express: a cell of the architecture
  suite over the text scan's type-to-module map pins that inside `RaceWorld`, types of the
  `Recording` module appear only in the loader files (a grep cannot know which module a type
  belongs to), and a grep-audit gate pins that no race file names a recorder or a recording
  store (15.4).
- A grep-audit gate in the `GrepAuditTests` family: the rival store's type names appear only
  in an allowlisted set of files.
- A headless test: load a race save with a populated rival cache, run the ledger walk and the
  save writer, and assert the outputs are byte-identical to the same save with an empty
  cache.
- A harness lane, because the headless test cannot see the stock flight state, which is where
  the side channels above would land: fly the same scripted session twice, once with an
  empty rival cache and once with a full one (ghosts, leftovers, flags, an orbiting station),
  and diff the two saved games whole. Two flown sessions never land on exactly the same
  time values (an explicit-UT warp lands near its UT, and flight physics does not repeat),
  so the diff is canonical: only a short pinned list of time values may differ, each with a
  written reason, and everything else must be identical. Byte identity is the headless
  test's (open: question Q-13, section 13).

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

A milestone's date is stored exactly, as the game's universal time in seconds, and two dates
are compared to the exact second of game time (owner, 2026-11-15, replacing the whole game
days of ruling D1): the date is cut to its whole second, and the earlier second holds the
first with full points. There are no same-day ties. Only the very same second is shared,
which is expected almost never (7.8). This is a rule of the race, not a setting. The manifest
still carries it, as `dateUnit = SECOND`, so that every machine reads the rule from the file
and a later format could add another unit; this build writes `SECOND` and refuses to join a
race whose manifest says anything else.

What exact seconds cost, accepted knowingly by the owner:

- *Seat luck.* The sites are spread around Kerbin, so the Mun faces them up to about three
  hours apart. On the first direct Mun and Minmus flights that difference can decide the
  first, and it is nobody's skill.
- *The opening firsts go to the quickest launch.* The easy early milestones (reaching space,
  orbit) go to whoever flies them first in game time.
- *A seen date can be beaten by one second.* A player who sees a rival's date can take the
  first by flying one second earlier. Groups that dislike this switch on the "no retakes"
  house rule (7.11).

`calendar` fixes how long a displayed day is (a Kerbin day of 6 hours or an Earth day of 24),
because the stock setting is per player and the board must read the same for everyone. It is
for display only and never affects scoring. The race takes it from the creator's own
calendar setting when the race is created, and the setup dialog shows it; it has no field of
its own (owner, 2026-11-15). The window shows dates in that calendar. Days are counted from
day 1 at game time 0, with no year, in both calendars ("day 30"), with the time of day to
the second where two dates would otherwise read alike (7.13). The window formats them
itself: Parsek's own time formatter and stock `KSPUtil.PrintDateCompact` follow the player's
own calendar setting.


---

## 7. Behavior

### 7.1 The Multiplayer mode and the race mark

The stock main menu shows, after Start Game, a list of entries (Start New, Resume Saved,
Training, Scenarios). Parsek adds one more, **Multiplayer**. It opens the race setup dialog
(7.2). The entry is always visible on the `multiplayer` branch, from the phase that builds it
on; there is no developer switch, because no player sees that branch before its one final
merge into main (owner, 2026-11-15; section 14).

A race save is a normal KSP save of mode Sandbox, Science or Career, created by Parsek from the
race manifest. KSP's game-mode list is fixed, so "Multiplayer" is not a fourth KSP mode: it is
the race mark, a scenario node of its own in the save (6.2). One predicate, the race gate,
answers "is this a race save" from that mark as soon as the save has been read, before any
scene is built, and every multiplayer code path runs behind it. When Parsek itself is inert in
a save (its game-mode gate), the race gate answers no: the save plays as no race, and its
mark is kept as it is and written back unchanged.

- A race save is created only by the setup dialog. There is no way to turn an existing save
  into a race save: every player starts at day 1, which is what makes game dates comparable.
- A race save appears in the stock Resume Saved list like any save (its name carries the race
  name). Loading it there is the same as continuing it from the Multiplayer dialog.
- The sites, the terrain patches and the scenery (7.3) exist only while a race save is loaded.
  One handler reconciles them at every scene load request against the mark of the game being
  loaded (spike S4a checks that the game is known there; if it is not, the handler moves as a
  whole, 7.3): applied when a race save starts, before its first scene is built, and removed
  before the first scene of a single-player save by whatever route it is reached (the main
  menu, a load from inside a game, a harness load), so a single-player save always finds the
  stock Kerbin.
- Two race saves can live on one install. Each has its own mark and its own local race
  directory; going from one straight to the other, by any route, re-applies the sites for the
  new home site.
- Single-player saves never read a shared folder and never show a multiplayer surface.

### 7.2 The race setup dialog

One dialog in the stock dialog style, with three actions.

**Create a race.** The creator sets (owner, 2026-11-15):

- the race's name, which shows in the Join dialog, in the Space Race window and in each
  player's save name;
- game mode: Sandbox, Science or Career, once, for every player;
- difficulty, through the stock difficulty options panel (the same one Start New uses); the
  resulting settings go into the manifest and every player's save is created from them. If
  spike S6 shows the panel cannot be opened from this dialog, the creator picks one of the
  four stock presets (Easy, Normal, Moderate, Hard; owner, 2026-11-15, Q-17 in 13.A); the
  manifest stores the full settings either way;
- the shared folder's location, in the same dialog as the name and the look: a path field
  (typed or pasted; there is no folder picker) and a Validate button (exists, writable,
  empty of any race, short enough for the longest paths in the folder and in the local
  copy, 6.3); no browse button and no folder picker (owner, 2026-11-15);
- whether the shared launch sites are in use (7.3): off unless the creator turns it on;
- an optional end date: a real-world date and time; without one the race never ends (7.10);
- house rules: "no retakes" (7.11), off unless the creator turns it on;
- their own display name and look;
- the race's calendar: the creator's own stock calendar setting (Kerbin or Earth days),
  shown in the dialog and written into the manifest, with no field of its own; it only sets
  how dates are displayed (6.6).

There is no player count to set: a race always has four seats. Everything above except the
creator's own name and look is fixed for good once the race is created: the mode, the
difficulty the saves start from, the end date, "no retakes" and the shared launch sites
never change, and nothing can change them later. After creating, the creator is a player like
the others. The one setting that can change later is each player's own difficulty, in their
own game, which is disclosed (7.12).

Then Parsek writes `race.cfg`, the creator's `player.cfg`, creates the save, adds the race
mark to it and writes it to disk before the game starts (the Space Center scene reads the
save file back from disk, so a mark added after the start would be missing), and starts it
at the creator's home site (slot 0, the stock KSC).

**Join a race.** The player picks the shared folder's location the same way. The dialog
then shows the race name,
mode, end date and house rules, and who has joined with which look. The player enters a
display name (a name already used in the race is refused, so messages and the board never
show two players alike), picks one of the free looks (taken ones are greyed), and sees the
site they will get. Joining creates the player's id; it is stored in the new save. Join
writes their `player.cfg`, creates their save from the manifest with the race mark written
in before the start (as for Create), and starts it at their home site. A joiner who finds
differences between their mods and the creator's list sees them named, as information.

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
  nobody has reached yet; the Join dialog says so before they commit. (To make that true of
  the files, a game that has just joined writes its first standing only after its first
  import has completed, 7.5.)
- A first sync that may be large. It is spread over several polls with progress in the status
  text, and the game is playable meanwhile.

A join after the end date is allowed: the final board is unchanged (a player with no
standing from before the end date is not on it) and the newcomer plays "after the end"
(7.10).

**Continue.** Lists the local race saves with race name, own look and last sync, and loads the
chosen one.

Refusals, each with its exact reason: the folder is missing or not writable; the folder has no
race (Join) or already has one (Create); the race is full; the race's `raceFormat` is newer
than the build; the display name is taken; the race uses the shared launch sites and this
install lacks the expansion they need (7.3); Kerbin is modified (edge case 24).

**Who may join.** Anyone who can write to the shared folder can take a free seat. Sharing the
folder is the invitation, and the folder's own sharing settings are the guest list. Parsek
adds no approval step and no join password on top (owner, 2026-11-15).

**Who may leave.** Nobody can remove a player, the creator included. A player who stops
playing keeps their seat and their points: their space center, flights, flags and board
dates stay in the race (owner, 2026-11-15; 8, cases 6 and 7a).

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

**The one direct touch of the shared folder.** Validate, Create, Join and the location change
in Settings act on the shared folder from a button press. They are the one place where the
game reads and writes the shared folder directly rather than through the local race
directory (15.9): each goes through the race transport port on the main thread, the same port
the sync uses.

### 7.3 The four sites

**The site table.** Four fixed sites on Kerbin, identical in every race save, shipped with the
mod and named by `siteTableId`:

- Site 0: the stock KSC (footprint centre latitude -0.074, longitude -74.64; ground at 66 m).
- Site 1: the opposite side of Kerbin (ideal longitude 105.36 E).
- Site 2 and site 3: a quarter turn either way (ideal longitudes 15.36 E and 164.64 W).

Site 0's centre above is the midpoint of the pad and the runway, the point the survey tool
uses. A move places the KSC by its own origin point, which spike S4a measures, so the other
sites' offsets in the table are taken from that measured origin.

The ideal longitudes are where the sites would be if spacing were exact. The terrain survey
of 2026-11-14 (`docs/dev/research/kerbin-equator-survey-2026-11-14/`, task S1) measured what
is there:

| Site | Ideal point | Nearest usable ground | Its natural height |
|------|-------------|-----------------------|--------------------|
| 1 | 105.36 E: dry, rough upland | 106.7 E, 1.3 degrees away | about 135 m |
| 2 | 15.36 E: open sea | 18.8 E, 3.4 degrees away (a plateau) | about 520 m |
| 3 | 164.64 W: dry, rough highlands | 162.3 W, 2.3 degrees away | about 675 m |

So every site can stand within 3.4 degrees of its ideal point, which keeps the sites opposite
for every practical purpose (3.4 degrees is 36 km on the ground and about three and a half
minutes of Kerbin's rotation). What the survey also shows is that none of that ground is at the
KSC's height: ground as low as the KSC's lies 12 degrees from site 2's point and 32 degrees from
site 3's. The stock KSC itself stands on a flattened patch in rough terrain (415 m of height
range within 3 km), so a site does not need naturally flat land, only dry ground under its
patch.

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
the east); and the same terrain patch data in every game. The survey alone cannot give the
exact spots: its fine grid does not cover them all, its fine step is about 262 m, and none of
its rows lies on the KSC's latitude (-0.074). Spike S1b scans that row at about 20 m around
each spot and checks that the whole footprint is dry.

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
  safe at any time; their kerbals are re-dressed once, at the next load.

A fifth player folder (five simultaneous joins) is ignored by every game, by the same order:
the four earliest are the race.

**The home site is a working space center.** In each player's game the working KSC stands at
their home site: the Space Center scene, the VAB and SPH, the launch pad and runway, recovery
distance and every stock facility behave as they do at the stock location. This is done by
moving the stock space center object onto a flattening terrain patch at the home site, the
technique the KSCSwitcher mod has used for years (section 12); the stock patch at site 0
itself never moves (below). Only one working space center per game is
needed, because each player runs their own game.

The Space Center view follows (owner ruling 2026-11-14): entering the Space Center scene
shows the player's own site, with the camera starting over their own buildings, and those are
the buildings that are clicked, upgraded and repaired. Nothing in the game still points at
the stock location: the camera, the "return to Space Center" button, recovery distance, the
tracking station's home marker and the map's KSC marker all mean the home site. A player
never sees the Space Center scene of a rival's site; a rival's buildings are only visible
from outside, in flight or from the map.

**The other three sites are scenery.** At each other site stands an exact visual copy of the
KSC buildings: solid (owner ruling 2026-11-14), not clickable, not destructible, not
upgradable, and always fully built, in every mode. The first draft let a Career site show its
player's facility levels; that is dropped, because buildings whose shape follows a rival
would change what a local vessel can collide with when a rival's file arrives (6.4). The
buildings are the same in every game and never change. An unassigned site has them too.
A copy is not cloned from the live KSC, which shows the local player's levels; it is built
from each facility's top-level model (the last upgrade level's prefab), and its facility,
destruction and click behaviour is stripped before it wakes, because those parts register
with the game by the stock building ids and two buildings with one id would share stock
state.

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
  a copy. Nor does a name tell the sites apart: at every home site the stock names are the
  same (`LaunchPad`, `Runway`), so a rival recording's launch-site name does not say which
  site it left. The site is taken from its player's seat (`siteSlot` in the player file).

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
  (owner ruling 2026-11-14). It is off unless the creator turns it on, so a friend without
  the expansion can join by default (owner, 2026-11-15). When it is on, joining needs the
  expansion, and the race's
  difficulty settings enable the sites from the start, so nobody has to travel farther than a
  rival to unlock one. When it is off, every player launches only from their own site. (The
  sites stock hides until a player has visited them are not part of this: they would favor
  whoever lives nearest.) The race setting maps onto stock's "allow other launch sites"
  difficulty option, which a player can switch mid-race. The race setting wins (owner,
  2026-11-15): in a race created without the sites, launches from them stay refused and the
  stock option shows greyed, whatever the difficulty settings say. This is a stock-control
  annotation under the standing exception: the option's own disabled state with the reason
  (the race was created without these sites) on that same option, and the launch refusal
  reads the same predicate as the greyed option. Two parts are certain: the setting itself
  is held off (set back at every load and whenever the stock difficulty window applies, since
  stock reads it directly, for example for the ground stations at those sites), and launches
  from those sites are refused. The greyed option depends on reaching stock's toggle after
  its window is built (the toggle has no named method of its own); if it cannot be reached,
  the toggle stays live, snaps back to off when the window applies, and one screen message
  says why, an event that changed a value the player had just set. The editor's site picker
  does not list those sites at all, as in a stock game with the option off.

**Launch areas.** The site table also lists the launch areas: for each of the four sites the
pad (a circle around the pad's spawn point) and the runway (the whole strip, not only its
threshold), and for each stock alternate site its pad or airstrip, each with a margin.
Proposed sizes: 100 m around a pad; the strip's own length and width plus 50 m around a
runway. The exact numbers are measured with the sites (tasks S1 and S4). Because they are in
the site table, every game agrees on them. They are the exclusion areas of section 7.7.

**Equal science at every site.** The KSC area and its buildings are science locations of
their own (the KSC biome and the building mini-biomes), worth real science in the first hours
of a Science or Career race. Every player must find the same ones at their home site. Stock
names the place a landed vessel stands at from tags on the colliders and triggers of the KSC
(the pad, the runway, the grounds and each building), and the experiment biome reads that
name before it reads Kerbin's biome map. So the KSC area, like the building mini-biomes, is
most likely named by those tags and moves with the working space center; the terrain
survey's read of the biome map finds no KSC biome at the KSC's longitudes. Spike S2 settles
it at a moved site. If the KSC names follow the move, nothing more is needed; if part of the
KSC area does come from the biome map, a biome override in a radius around each player's
home site gives it there. Until S2 has run, treat this as the main fairness risk of Science
and Career races.

**What is not equal, said plainly.** The sites share a latitude, so every player launches
into the same inclination with the same rotation speed, and interplanetary windows are the
same for everyone. Three things differ and are accepted:

- *Height* (the owner's ruling above).
- *The neighbourhood.* The stock KSC has a shore, water and an island airfield close by; the
  other sites have whatever lies around them. Early science flights to nearby biomes are
  therefore not identical. What is near each site is listed (open question 13.C.2), and the
  window shows it. The biomes in that list come from the game's own biome function (spike
  S2), not from the terrain survey's biome data, which the survey itself calls unreliable.
- *The time of day and the Mun.* When a race starts it is morning at one site and night at
  another, and the Mun comes overhead up to three hours apart. Dates are compared to the
  second, so this can decide the first direct Mun and Minmus flights; the owner accepted
  that seat luck knowingly (6.6).

**Moving the space center cleanly.** The review listed what holds the stock position and
must follow the move, so the spike (S4) and the implementation reset each one and nothing is
found later: the stock space center object caches its latitude, longitude and surface
normal once at start, and every stock reader of that cache (recovery value, survey
contracts, the warp to the next morning, the distance records, random surface points,
music) reads it through that one object, so one refresh serves them all; the KSC city,
moved along the equator, turns about its own up axis by the longitude change unless its
final angle is corrected; both kinds of stock spawn point cache their position, each launch
site's and each space center facility's (stock refreshes the second only on a facility
level change); the CommNet home station caches its position when the network starts;
Parsek holds the stock pad and runway coordinates as four constants and caches the circles
of the stock launch sites for the life of the process once a read is complete, with no
reset (prerefactor P1). That cache may also hold the KSC's own pad and runway, because its
read admits those names; spike S4a settles it, and if it does, the move filters or rebuilds
them. (The two single-player defects of the launch-area rule, a longitude wound past the
date line and a partial read of the stock sites kept until the game restarts, are already
fixed in base Parsek.) Last, the flattening patches need the terrain rebuilt after they are
added or removed; which stock call re-collects an added patch and rebuilds the ground in
each scene is for spikes S4b and S5 to settle. Site 0's stock patch stays where it is in
every game, and every race save, whatever its home site, adds the patches of sites 1 to 3.

The whole set is applied or undone at the scene load request, before the next scene is
built. The one scene request handler, `ParsekRaceBoot.OnSceneRequest`, calls
`RaceSites.Reconcile` on every request, the main menu included, and compares what is applied
with what the game being loaded wants: stock for the main menu or a save with no race mark,
else the race's site table and home site. A race save's first scene applies; a scene change
inside one race save (flight, quickload, revert, rewind, Re-Fly) changes nothing; any way
into a single-player save (the main menu, a load from inside a game, a harness load)
restores stock first; a race save with another home site restores, then applies (edge case
31). Whether the game being loaded is already known at the request on every way in is
checked by spike S4a. If it is not, the one handler moves as a whole, the race gate and the
reconcile together, to the loaded game's post-load event or to a prefix on `Game.Start`, so
one place still decides on every way in. Apply and restore are one pair of operations with
an in-game test on both. A failed step rolls the whole set back to stock and logs the
step. A race save whose sites cannot be set up (a step fails, or Kerbin was changed by a mod
after the race started) is not loaded: the game stays at the main menu with one message
naming the reason, and the save is not touched, because loading it on stock Kerbin could
destroy the vessels at its home site (owner, 2026-11-15). The refusal is decided before the
save's first scene loads, in a prefix on `Game.Start()`, which every way into a saved game
calls and which is what loads that scene; a scene request cannot do it, since stock fires
the request event and then loads the scene anyway. Stock itself rewrites `persistent.sfs`
from the game it has just read before `Game.Start()` (the load dialog and the quickload), so
"untouched" means no scene ran and no Parsek code wrote; spike S4a checks the file's bytes.
If the prefix cannot be used, the load finishes at the stock space center with the save
copied aside first and copied back after the return to the main menu.

### 7.4 Looks

A look is a suit style plus one fixed UI color. There are four, one per player, taken from the
stock suits (owner preference: stock first):

| Look | Suit | Needs |
|------|------|-------|
| 0 | Base suit | nothing |
| 1 | Slim suit | nothing |
| 2 | Vintage suit | Making History |
| 3 | Future suit | Breaking Ground |

The slim suit's EVA parts exist by name in the base game; which install folder ships them is
checked by spike S3 on a copy of the game without the expansions. The race rules name the
four suits with a list of their own, because the stock suit type cannot be named by the
headless race core.

Each look's UI color is fixed by the mod to match the suit's dominant color (task S3 picks the
four colors in game). The color is used for that player's ghost labels and map icons, orbit
lines, and their column and cells in the Space Race window. The color belongs to the look
and is not chosen separately, so four players always have four different colors.

**Where a DLC is missing.** Parsek provides two stock-like replacement sets, one resembling
the vintage suit and one resembling the future suit, built on a base-game suit. A machine
that lacks the DLC uses the replacement wherever that look would show: on the player's own
kerbals if they picked it, and on a rival's ghost kerbals. A look is therefore always
available and always distinct, with or without DLC.

How the sets are made and offered waits on spike S3; the working reading (open: questions
Q-19 and Q-20, section 13):

- Art: the DLC textures cannot be shipped, so Parsek recolors the base suit's own textures
  in memory at load and ships nothing. Stock-like art drawn for Parsek and shipped as files
  is the fallback if the recolor looks wrong.
- Offer: Parsek applies the sets itself, only to race kerbals and rival ghosts. They are not
  stock suit entries (`SUITCOMBOS` configuration), because such an entry under `GameData`
  shows in the stock suit picker of every save, single player included. This holds if S3
  shows the applied texture in EVA, IVA and the portrait; if it does not, the sets have to
  become stock suit entries, which puts a race item into every save and needs the owner's
  permission. With the suit locked, a race player never picks a suit, so what stays open
  under Q-19 is only whether single-player saves may list the two sets in their picker.

**Applying the look.**

- Own kerbals: the race suit is locked (owner, 2026-11-15; the owner chose this against the
  recommendation to leave the picker free). Every kerbal of a race save wears the player's
  look, always. A kerbal is dressed when it joins the roster (the starting crew of a new
  save, later hires and rescues), and at every load Parsek dresses every own kerbal that is
  not in the look, so no suit state is kept in the save. The starting crew is created before
  any scenario exists, so no hook sees it join; the first load of a new race save dresses
  it. A look changed in Settings or by the clash rule (7.3) re-dresses the whole roster at
  the next load. Stock resets a suit whose DLC is not installed to the default suit when it
  loads the save, before Parsek runs, so on a machine without the DLC the same load dresses
  the player's own kerbals in the replacement (spike S3 checks what stock does).
- The suit choice of the stock suit picker is blocked in a race save. Only the suit choice:
  the same stock window still lets the player take off a kerbal's helmet and neck ring and
  change the suit light color (owner, 2026-11-15). Stock has one way into that window, the
  suit button of a crew list row, which the Astronaut Complex and the VAB and SPH crew
  dialog both show, so one block covers every place (found by decompile; the task that
  blocks the picker, plan, looks phase, re-checks it). The block is a stock-control
  annotation under the standing exception: the suit buttons' own disabled state, with the
  reason "Race suits are locked to your race look" on those same controls, and a click-block
  on the suit choice that reads the same predicate, so a click that reaches it anyway changes
  nothing.
  In a single-player save the picker is untouched.
- Rival ghost kerbals (EVA ghosts) are drawn with the rival's look, the suit every race
  kerbal wears; the ghost build does not read the suit from the recording. Today's EVA
  ghost is built from the kerbal part named in the
  recording, and the suit styles may be separate parts: on a machine that lacks the
  expansion a recorded suit's part would be missing. An EVA kerbal is a vessel of a single
  part, so its whole ghost would fall back to the plain placeholder sphere; a kerbal in an
  external seat of a larger vessel would just be missing from that vessel's ghost. So the
  ghost build needs a substitution, not only a texture, and it covers both cases: any kerbal
  part resolves to one the machine has, dressed in the look's suit or its replacement. The
  texture is new as well: a ghost kerbal shares the stock model's materials today, so no
  ghost shows a suit color yet (addition A4 of 15.11; VERIFY in task S3).
- Rival vessels keep their own appearance. Their label, map icon and orbit line carry the
  color. A rival map vessel the player focuses shows the stock focus color instead, like any
  focused vessel. The label and the map name also carry the rival's display name, because
  colors are hard to tell apart for some players and the Tracking Station list has no color:
  `Ana - Kerbal X` beside a ghost in flight, `Ghost: Ana - Kerbal X` on the map and in the
  Tracking Station (owner, 2026-11-15).

Rosters are private. Each player has their own Jebediah; a rival's kerbals exist in the local
game only as names on ghost labels, so equal names never collide.

### 7.5 Export

A player's public state is exported as a new standing whenever it changed. At each sync tick
(below) the exporter computes a digest of the public state: the effective committed recording
set with content hashes, the milestone table, the leftovers, the flags, names, disclosure
facts. If it differs from `lastStandingDigest` in the local race file, it writes any new
payload, then the standing (to the local `own/` folder first, from where it is copied to the
shared folder), and stores the new digest. The receipts (`SEEN`) are not part of the digest:
seeing a rival does not by itself cause an export.

What "effective committed" means is exactly what single player already means by it (ERS): a
flight that was merged and is not superseded, retired or discarded. So:

- An unmerged flight, a flight awaiting its merge dialog and an open Re-Fly attempt are never
  shared.
- Nothing is exported while a Re-Fly session is open; sharing waits until it ends (owner,
  2026-11-15). The effective set hides the flights
  the open attempt would replace, so a standing written then would withdraw flights the
  rivals already have. The standing stays as it was until the attempt is merged or
  discarded. The exporter asks the effective state whether a Re-Fly session is open (a read
  it gains for this), not the flight scene.
- A Re-Fly merge replaces the superseded flight in the next standing.
- A flight that is still open to Re-Fly (merged, earned nothing after its separation, ended
  in a crash or was left in flight off focus; single player lists it under Unfinished
  Flights) is an effective committed flight like any other: it is shared, and rivals see it
  as a ghost. Single player closes a flight to Re-Fly once the ledger counts anything it
  earned after its separation, a milestone included, and what it earned before the
  separation stays when it is re-flown. So the date the ledger credits for a milestone is
  never on a flight a Re-Fly replaces; a flight still open to Re-Fly can hold only a later
  repeat of a milestone an earlier flight already holds. A Re-Fly merge can add a date to
  the table, and can take away only such a repeat (it matters for the board only when the
  earlier date does not score, 7.8). A date it adds is the game date of the re-flown flight,
  which lies in the past of the player's clock; it is treated like any date reached after a
  rewind: it can take a first, and under the no-retakes rule a rival flight the game had
  already seen blocks it (7.11). No rule is special to Re-Fly.
- A load that drops flights (a quickload, a loaded older save) drops them from the next
  standing (principle 7; the co-op rule "shared stays shared" is gone because nothing depends
  on a shared flight). The player is told once, after the load: "2 shared flights left the
  race", because under the no-retakes rule a dropped first may not be retakable (7.11).
- A rewind changes nothing by itself: committed flights in the player's future are still
  theirs and stay shared.

**Sync ticks.** Save load, entering the Space Center or the Tracking Station, after a tree
commit or a Re-Fly merge, a low-frequency poll in the Space Center and the Tracking Station,
and the Sync now button. During a flight the only ticks are the one after a commit and the
Sync now button, and both only export; nothing is polled and no rival standing is applied
while a vessel is flying. While copies run on the main thread (15.9; whether a copy worker
replaces that waits on spike S8, open: question Q-18, section 13), an export during a
flight writes only the local own mirror, and the copy to the shared folder waits until the
flight ends.

The tick after a commit is found by watching the effective recording and ledger sets the game
already caches: when they change, a commit or a merge happened. The exporter adds no second
listener to the merge dialog's commit event, which is a static that only the flight scene
writes.

**Order inside a tick: export first, then import.** The standing written in a tick lists as
seen only what had been applied before the tick. A flight committed before the player's game
had applied a rival's claim is therefore never stamped as "flown after seeing it" (7.11).
The one exception is a game that has just joined: it writes no standing until its first
import has completed, so a late joiner's first standing lists everything already shared.

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
- **Flights load lazily.** The rival store is built from the standing (ids, game-date
  ranges, leftovers, milestones, flags) and the rival's tree files. A tree file is a small
  config file and holds what a leftover needs and the standing does not: the end position,
  the terminal orbit, the endpoint body, the terminal state and whether the vessel is
  debris. No trajectory or snapshot file (`.prec`, `.craft`) is opened when the store is
  built. A flight's trajectory file is opened when its ghost is first needed, so a scene load
  does not parse every flight of every rival (15.13); until then the ghost engine skips that
  flight with a neutral reason of its own, never as a finished flight.
- **Missing parts.** A rival recording that uses parts the local install lacks plays as a
  ghost with those parts skipped, and the Space Race window notes "Carol uses parts you do not
  have". The loader removes those parts before the ghost is built and logs them once per
  flight, not once per part. Kerbal EVA parts are kept, so a kerbal in a suit part of an
  expansion this machine lacks still reaches the look substitution of 7.4 and shows.
- **Missing bodies.** A rival flight to a body the local install does not have (a planet
  pack on one side) is not played; its board row shows, and still counts, marked "cannot be
  watched here".
- **Removal.** A recording the rival's latest standing no longer names leaves the rival store;
  its ghost, its leftover and its flags go with it. Cached files that no kept standing
  names are deleted at the next import tick outside a flight; the 3 days of 6.3 are the
  shared folder's.

### 7.7 What a rival's world looks like

Rival ghosts play at the game dates they were recorded, against the local player's clock.

- **A flight in the local past** is over: only what it left behind shows.
- **A flight in the local future** plays when the local clock reaches it, like any committed
  future flight of the player's own.
- **Rival ghosts are only seen.** They have no collision, cannot be docked to, boarded,
  grabbed, transferred to, recovered or terminated, and no stock action reaches them (the
  existing ghost guards). They are never CommNet relays or control points: only the player's
  own ghosts relay, and a rival's network never changes the player's game (owner,
  2026-11-15; 6.4). They never loop: a rival flight plays once, at its date. They can be
  targeted for navigation, like the player's own ghosts, for example to fly alongside one or
  measure a distance (owner, 2026-11-15).
- **Leftovers.** After a flight ends, what it left stays visible from that game date on: a
  vessel left in orbit stays on its orbit with an orbit line and a map icon in the rival's
  color; a vessel left on a surface stays where it stood. A recovered or destroyed vessel
  leaves nothing.

  How long a leftover stays is something only its owner's game knows: the vessel may later be
  flown again, recovered or deleted in the Tracking Station, and none of those is a shared
  flight. So the owner's exporter says it: the standing's `LEFTOVERS` block lists each
  flight that left something, with the vessel's type (for its map icon), from which game
  date, and until which (the start of the next flight of that same vessel, its recovery, or
  the moment the owner's game found it gone; empty while it is still there). The viewer
  shows a leftover exactly inside that window.

  What a leftover looks like is the flight's own ghost held at its last moment (the staged,
  deployed look the flight ended with), not a second snapshot. It is an ordinary entry of
  the rival ghost lane with no mode of its own: the loader extends the flight's end to the
  leftover's end (an empty end becomes a finite horizon) and, for a vessel left in orbit,
  appends an orbit tail; the engine already holds the last pose past the last point and
  plays an orbit tail. So it obeys the same distance rules and the same ghost budget as any
  ghost (15.6, 15.13), with the lowest priority. Debris is not kept (owner, 2026-11-15):
  only flyable vessels a rival left behind (landers, stations, rovers, bases) leave a
  leftover, and the exporter lists no debris in `LEFTOVERS`, so a rival's spent stages do
  not clutter the sky. Debris still plays with its parent while the flight itself replays.
- **Flags** are the exception the owner asked for: a rival's flag can be walked up to and read.
  A rival's planted flag, listed in the standing's `FLAGS` block, appears from the game date it
  was planted as a **plaque replica**: a flag model with the rival's flag texture when its
  path (`flagUrl` in `FLAGS`) names a texture this install also has, as every stock flag
  does, and the default flag texture otherwise (a flag's texture is a path inside its
  writer's install, and a custom flag file is not shared), plus a click target, which
  opens the stock plaque dialog with the rival's text. It is not a vessel. This is a change from
  the first draft, which reused Parsek's own flag replay: that replay creates a real flag
  vessel, which the game saves, which stock lets a player switch to, recover and target, and
  whose stock states re-enable taking it down. A replica has none of those problems by
  construction: nothing to strip at save, nothing in the vessel list, nothing a contract or a
  stock action can find, and no collider to trip over. Spike S7 settles how the model is built
  (the stock flag part has no mesh file to clone) and opens the stock plaque dialog, whose entry
  `FlagPlaqueDialog.Spawn(string, string, Callback)` is public, with text the mod supplies. If
  it cannot, a rival's plaque is read in one small stock-styled Parsek popup opened by the
  same click: the fourth new-window exception the owner allows, and only in that case
  (owner, 2026-11-15; section 0).
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
  (`SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement`, 50 m circles: the KSC
  pad and the runway's west threshold are constants there, the other launch sites come from
  `LaunchSiteExclusionZones`, whose read admits the names `LaunchPad` and `Runway`, so its
  cache possibly holds the stock KSC's own pad and runway as well; its `runtime=N` log line
  settles which). The race rule is wider (whole runways, all four sites, flags) and applies
  to what is SHOWN. Both use one pure shape test over plain coordinates (a
  circle or a strip, longitude wrapped), each with its own areas: single player keeps its
  50 m circles, the race passes the site table (prerefactor P1). In a race save the
  single-player circles must follow the moved home site (task S4).

  While a rival's flight is being replayed its ghost is shown wherever it is, launch areas
  included: a launch has to start on a pad, and a ghost cannot block anything. The first
  draft also hid a ghost that stood still in a launch area for 60 seconds during a replay.
  That is dropped from the first build: a ghost blocks nothing, a flight ends when it is
  committed, and the rule had loopholes of its own (a slowly creeping rover). It stays as an
  option if play shows a need (13.B).
- **Where rival ghosts show.** In flight, in map view and in the Tracking Station. The Space
  Center scene shows a rival's buildings and no rival ghost in the first build (owner,
  2026-11-15): that scene
  has its own playback code, separate from the flight scene's engine (15.6), and the sites
  are a quarter of the planet apart.
- **Watching.** The Watch camera does not follow a rival ghost in the first build (owner
  answer, 13.A). The camera code identifies what it follows by its position in the player's
  own list, and the groundwork that changes that (prerefactor P3b) is deferred. A rival
  flight is seen on the map, in the Tracking Station and as a ghost nearby, while the local
  clock is inside it. A flight in the local past is seen the way the player's own past is:
  by rewinding to before it. Following a rival ghost with the Watch camera (a camera,
  nothing else) comes with P3b, and a viewer that replays any rival flight at any time,
  outside the local timeline, is a later addition (7.15, hardening).
- **Hiding.** The Space Race window has one show toggle per rival, for performance and for
  players who want a quiet sky. It hides that rival's ghosts, leftovers and flags alike.
  Until the window exists (phase R4), every rival is shown.
- **The Tracking Station list.** Rival objects with a map presence appear there the way own
  ghosts do, under the same filter, and follow the same toggle.

### 7.8 Milestones and the board

**A player's milestone table.** For each ladder milestone, every game date at which one of
the player's effective committed flights is on record as reaching it, earliest first, each
with that flight's recording id. Entries are keyed by the stock milestone id (`Mun/Landing`,
`ReachedSpace`), not by the ladder row, and the board maps each to its ladder row (7.9), so
a retuned ladder needs no new export. It is derived from data single player already keeps: the
ledger's milestone rows, which exist in all three game modes and which the once-ever walk
already credits to the earliest date. Nearly always that is one date. A second one exists
only after a rewind: stock records a milestone once, so a flight flown AFTER the date on
record reaches nothing new and adds no row, while a flight flown after a rewind to before
it, reaching the milestone at an earlier date, adds an earlier row; the walk then credits
the earlier one, and the later flight's row stays for as long as that flight is committed.
The date that counts for a player is the earliest of their dates that scores (the board,
below). The later row stays in the effective ledger after the earlier re-earn (the walk only
stops counting it as the one that pays), and the no-retakes rule of 7.11 reads it.
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
- A Re-Fly never replaces the flight whose date the ledger credits (7.5). One that replaces
  a flight holding only a later repeat removes that repeat, and the earlier date stays: a
  Re-Fly tombstones only eligible rows of the superseded subtree, and never a row that
  names no flight.
- A milestone row tagged to a flight that is not among the effective recordings is dropped
  from the table, with a log line; it is never credited to a different flight.

A deeper check of the trajectory against the claim (on the Mun's surface and at rest, for a
landing) is a later hardening (open question 13.C.4).

**The board.** For every ladder milestone m and every player p, the date d(p, m) or nothing.
Only a date that scores can hold a first. A date that is unverified (above), or that comes from
a flight flown with a listed tool (7.12), is shown on the board with its mark and is skipped
when the holder is decided: it takes no points and it keeps none from the next player, and the
player's own next date, if they have one, is read instead. A date the player's game completed
from the debug menu, outside any flight, is not on the board at all (7.12). The first is the
earliest scoring date, compared to the exact second of game time (6.6), and its holder takes
the milestone's full points (owner, 2026-11-15). Being one second earlier is enough; a
player on the same game day but later holds nothing. Only players who reached it in the very
same second are co-holders, which is expected almost never; they share its points exactly.
That share is the one place a fraction can appear, so the smallest exact form does: the
board counts points in twelfths internally (at most four players can share a second, and 12
divides by 2, 3 and 4), so a share is a whole number of twelfths, "level on points" is exact,
and nothing is rounded. The window shows totals to two decimals. A player's score is the sum
over the milestones they hold.

The board is recomputed from the applied standings at every sync and whenever the local
player's own table changes. Nothing about it is stored.

**Rows this install cannot fully read.** Two marks say what this install cannot do with a
row, and they mean different things for the score:

- A flight to a body this install does not have (7.6) is marked "cannot be watched here".
  Its date is still checked, because the check above reads only the standing, so the row
  scores like any other.
- A standing in a race format newer than this build reads (its `raceFormat`, 6.3) is
  marked "not checked here". Its rows count as the standing states them, and stay marked.
  This is the one case in which two machines can show different boards from the same
  files, and the mark is what tells the players so; updating the older build removes it.
  Recordings newer than this build, in a standing it can read, mark no row: the check reads
  only the standing, so those rows are checked as usual and only their ghosts wait (edge
  case 14).

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
cannot be confused with another player's. When both dates fall on one game day, the message
gives the time of day to the second ("day 31, 2:14:05 beats your day 31, 3:01:44").

The first board of a session is compared against the board built from the rival cache at
load; nothing about the board is persisted, so a load itself posts no message. A first the
local player takes is announced only when a rival is involved (beaten, lost to, or tied
with in the very same second); one that no rival had reached posts nothing, and the window
shows it (owner, 2026-11-15). After the end date only final results are posted: the one
"the race is over" message, and a message when a late standing changes the final board.
Changes to the board of the present after the end show in the window and are not announced
(owner, 2026-11-15).

### 7.9 The ladder

The ladder is a fixed table shipped with the mod (`ladderId`), so every machine scores alike.
It is built from the stock world-first progress milestones, keyed by the same stock ids
Parsek already records: a body's step is `Body/Step` (`Mun/Landing`, `Mun/Orbit`), the
returns are that body's `ReturnFromFlyBy`, `ReturnFromOrbit` and `ReturnFromSurface`, and
reaching space is the bare `ReachedSpace`, with no body. Those ids are stable and do not
depend on the game's language. A ladder row may unite two stock ids (a landing or a
splashdown); a standing's milestone entries keep the stock id and the board maps it to its
row (7.8).

Proposed shape, to be tuned by play (open question 13.C.1):

- **Steps**, with base points: flyby 1, orbit 2, landing 3, a kerbal on the surface (a
  surface EVA or a planted flag, whichever came first: one step) 1 more, return from orbit
  2, return from the surface 4, plus the Kerbin firsts (reach space, orbit) at 1 each.
- **Bodies**, with a multiplier: Kerbin, Mun and Minmus 1; Duna, Ike, Eve orbit and Gilly 2;
  Moho, Dres, Jool (flyby, orbit, return from orbit) and the Jool moons 3; Eeloo 4; the Eve
  surface return 6. The Sun is not on the ladder.
- **Left out:** the stock altitude, speed, distance and depth records (continuous values that
  reward grinding); first launch (everyone has it in the first minute);
  and, by choice, a landing, splashdown or surface return on Jool and every step at the Sun,
  although stock defines landing, splashdown and the returns for every body.

Points the review raised, for the play test to settle rather than this document:

- **Flybys are cheap in bulk.** One tour of the Jool system collects five flybys. The
  multiplier makes that 15 points for one flight; the play test decides whether flybys of
  moons count less.
- **Water.** On a body with seas (Laythe, Eve) stock has a separate splashdown step. The
  ladder counts "landing or splashdown, whichever came first" as the landing.
- **Crewed or not.** Stock world firsts do not ask whether a kerbal was aboard, and neither
  does this ladder. A group that wants "a kerbal on the Mun" has the kerbal-on-the-surface
  step, which needs one.

The ladder is the same in all three modes. In Sandbox every part is available on day 1, so a
Sandbox race is about flying and planning; in Science and Career it is also about building the
science to unlock the parts early. Science is the expected default (it has the tech race
without the money). In Sandbox the first days decide a lot, because nothing gates the
flights; the setup dialog says so next to the mode.

### 7.10 The end date

A race may have a real-time end date, a real-world date and time the creator sets or leaves
empty (owner, 2026-11-15). When it has passed, the board is final: for each player
it counts their last complete standing stamped before the end date (a standing whose
payload is not all in the folder never counts, 6.3). The Space Race window names the winner
and shows the final board. Players level on total points are joint winners; there is no
tiebreak step (owner, 2026-11-15). Everyone
can keep playing and sharing; later standings still drive ghosts and are shown as "after the end".
A player with no standing stamped before the end (one who joined after it, 7.2) is not on
the final board.

- **The final board is a function of the files.** It is computed from the standings in the
  folder (each player's exporter keeps their last pre-end standing for good, and its payload,
  6.3), not from when a file happened to arrive on one machine. Every machine that has the
  files, and a build that reads all of them (7.8), shows the same final board.
- **Holding back is legal** (owner ruling 2026-11-14). A player may fly in private and
  share at the last hour; the race has no referee to say when a flight "should" have been
  shared. What a player risks by holding back is the sync itself: a standing that is not in
  the folder cannot count.
- **The clock is the writer's own.** A standing stamped before the end that a machine first sees
  more than 1 hour after the end date (a tunable constant) is marked "arrived late" on every
  machine that saw it late (7.12). It still counts, so a final board can still change after the
  end date, when such a file arrives. Among friends a wrong clock is visible and talked about; a
  group that wants more gets it from the hardening steps of 7.15.
- **No end date:** the race simply continues, and the board always shows the present.

### 7.11 House rule: no retakes

The owner's optional rule: "first to get there wins, no one-upping". It is the creator's
choice at creation and off by default; normally a first can be retaken by reaching it at an
earlier game date, and one second earlier is enough (owner, 2026-11-15; 6.6). Under the
rule, a flight your game had seen blocks you however much earlier you fly. It is the answer
for groups that dislike a seen date being beaten by one second. With no server and no
trusted clock, the rule is
stated so that every machine computes it from the files alone, without any real-time stamp:

**You cannot take a first you already knew about.** Each standing records, in its SEEN block,
the highest standing of each rival its writer's game had applied before writing it, and each
milestone entry records the standing in which that date first appeared (`since`). A player's
date for a milestone is **blocked by a rival** when a scoring entry of that rival for that
milestone (any of them, if the rival lists two, 7.8) had already been seen by the player's game
when the player's date first appeared. Under this rule the holder is the earliest date that is
not blocked. Two players who each reached the Mun before seeing the other's claim compare by
game date as usual, to the second; once a player's game has seen a rival's Mun landing,
flying an earlier one no longer takes it.

Details the reviews asked for:

- **Improving your own date never hurts you.** If a player held a date before seeing the
  rival's and later flies an even earlier one, the earlier one is "seen" and does not count,
  but the first one keeps counting for as long as its flight is still shared. The exporter
  lists both.
- **A block lasts only while the rival's claim stands.** If the rival's flight leaves the
  race (they loaded an older save), their claim is gone and the milestone is open again for
  everyone who had seen it.
- **Only a date that scores can block.** A rival's date that is unverified or tool-marked
  holds nothing (7.8), so having seen it blocks nobody. "Scores" here means the checks of
  7.8 alone: any real, clean rival flight your game had seen blocks you, even one that itself
  won nothing because it was "seen first" under this rule (owner, 2026-11-15). So the rule
  never depends on a chain of other players' receipts and cannot loop on edited files. A
  date that becomes a scoring date later blocks from the standing its entry names as
  `since`. The standings kept for good cannot say in which
  standing it first scored; the two differ only when a flight added later covers an older
  unverified date, which the exporter does not produce.
- **A missing `since` standing blocks nothing.** If the standing a player's entry names as
  `since` is neither applied nor kept, the receipts it carried cannot be read: the date is
  not blocked, and its cell is marked "history incomplete".
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
- **Difficulty:** a player may change their own difficulty settings mid-game, and nothing is
  blocked (owner, 2026-11-15), apart from the one option a race holds (stock's "allow other
  launch sites", 7.3). The standing carries a digest of the save's difficulty settings and
  the settings themselves (`DIFFICULTY` in `DISCLOSURE`, 6.1); the digest differs from the
  manifest's when the player changed them in game, and the mark next to the player's name
  names which settings changed. "The difficulty settings" are an explicit, ordered list of
  STOCK difficulty settings only, named field by field, every one a gameplay setting of the
  stock difficulty panel: the eleven fields of stock's difficulty block
  (`GameParameters.DifficultyParams`); the nine career values of
  `GameParameters.CareerParams` (starting funds, science and reputation, the five gain and
  loss multipliers, and the reputation lost for a declined contract); thirteen settings of
  stock's Advanced section (`GameParameters.AdvancedParams`: negative funds, the G-force
  and pressure limits, kerbal experience and immediate level-up, part upgrades, building
  impact damage and the rest; its two Mission-only settings are left out, since a race is
  never a Mission game); and the seven CommNet settings (`CommNet.CommNetParams`: require
  signal for control, plasma blackout, range and DSN modifiers, the two occlusion
  multipliers, extra ground stations). That is forty settings. Never the whole
  `GameParameters` node: a `CustomParameterNode` is included only when it is one of those
  two stock types, named by exact type, never Parsek's own settings and never another
  mod's, so changing one of those never marks a player and never leaves the save.
- **Cheat and debug tools, per flight.** Each recording entry of a standing says whether a
  tool from a fixed list was on or was used while that flight was recorded. The list is
  explicit, because "a cheat" is not one switch:
  - the stock cheat options (`CheatOptions`): infinite propellant and electricity, no crash
    damage, unbreakable joints, ignore heat, ignore the EVA construction mass limit, ignore
    kerbal inventory limits, ignore the agency mindset on contracts, set position by middle
    click, and the like;
  - the debug widgets, among them hack gravity (the `HackGravity` widget's state, which
    Parsek already reads; it is not a cheat option);
  - the stock debug actions that move a vessel (set orbit, set position, rendezvous); in
    Science and Career, the ones that add science, funds or reputation or unlock technology
    or progress (max tech, max facilities, max experience, repair, the console's funds,
    science and reputation sets); and the debug menu's Physics, Difficulty, Kerbal and
    contract screens, comets and the object thrower. Spike S9 settles which of these a mod
    can observe; one that cannot is listed in this document as out of reach (7.15);
  - Parsek's own test tooling (the in-game test runner, the automation command seam, the
    autorun hook): a race save in which any of it ran says so for good. The fact is kept on
    the race scenario node and exported as `testTooling` in the standing's `DISCLOSURE`.
    The seam marks the save when it is armed and the runner and the autorun hook when they
    run, so no single command has to. Injected recordings are written by a script before
    the game starts, so Parsek cannot see them: nothing injects into a race save, and the
    injector either stamps the mark or refuses a race save. Every race the harness flies is
    therefore tool-marked, and its own flights never score (open: question Q-24,
    section 13).
  Not on the list, because they change nothing a rival could not also do: part clipping,
  non-strict part attachment, and the biome overlay.
  A flight flown with a listed tool **does not score** (owner ruling 2026-11-14): its
  milestones are shown on the board, marked, and count for nothing. A flight is cut into
  several recordings, so the mark follows the flight, not the cut. Each recording keeps,
  per listed tool, the game date the tool was first on or used; its standing entry carries
  one `TOOL` child per tool with that date, and the earliest of them is the recording's
  tools-from date. A milestone date is marked when any recording of its tree has a
  tools-from date at or before it. So every milestone the flight reaches after a tool was
  first used is marked and does not score, also after the tool is switched off, because a
  tool's effect lasts (owner, 2026-11-15). A tool first used after the date, in the same
  recording or a later one, does not reach back.
  To score such a milestone the player has to reach it again without the tool at an EARLIER
  game date, after a rewind to before the marked flight, or in a save loaded from before
  it. A clean flight flown after the marked one adds no date, because stock records a
  milestone only once (7.8). Using a tool on the way to a first is therefore expensive, and
  the mark on the flight says so as soon as the flight is merged.

  None of this is stored today. Single player detects a gravity hack, a cheat teleport and
  a cheat currency change and writes log lines only (a currency change made through a stock
  `Set` call raises no change event and is not seen). The per-flight fact is new capture in
  base Parsek, kept on the recording so it follows optimizer splits and merges (addition A3
  of 15.11).
- **Milestones completed from the debug menu.** Max Progression, the debug console and the
  legacy cheat window complete a stock milestone outside any flight (they all reach
  `ProgressNode.CheatComplete()`), so no flight carries a mark and the completion could be
  credited to a flight that covers its date. Such a completion **does not score** either
  (owner decision D3): the standing lists each one the player's game observed, as a
  `DEBUG_COMPLETION` row of its `DISCLOSURE` block, and the board drops that milestone date
  entirely, so it holds nothing and blocks nothing, and marks the player as having
  completed milestones from the debug menu. Which of these a mod can observe is spike S9's;
  one it cannot is listed as out of reach (7.15).

A standing stamped before the end date that the local machine first saw more than 1 hour
after it is marked "arrived late" locally (7.10).

### 7.13 The Space Race window

A new Parsek window, present only in race saves, opened from a button in the main Parsek
window. It follows KSP's UI scale like every Parsek window and shares their window chrome
and styles; the board itself is new drawing (the existing log-table window has four fixed
text columns and cannot draw a column per player). Like every Parsek window it exists in the
Flight and Space Center scenes, the two scenes that host the Parsek UI, and not in the
Tracking Station (owner, 2026-11-15).

- **Header:** race name; time left or "final"; one block per player in their color: name,
  points, frontier ("at day 61"), last shared time, their site and its height, disclosure
  marks, the show toggle.
- **Table:** ladder milestones as rows, grouped by body; one column per player. A cell shows
  the game date that player reached the milestone, or nothing. The holder's cell is
  highlighted in their color; an unverified, tool-marked, "seen first", "history
  incomplete", "cannot be watched here" or "not checked here" cell is marked, and its hover
  says why. A points column shows what the row is worth.
- **Dates** read "day 31": days counted from day 1 with no year, in both calendars, with
  the time of day to the second in the hover. Dates are compared to the second (6.6), so
  when two dates in one row fall on the same day, both cells show the time of day too
  ("day 31, 2:14:05"). The window formats them itself, because the existing date formatters
  follow the player's own calendar setting.
- **A cell's hover** says at which game date that flight flies, which is where to rewind to
  see it. Sending the Watch camera to the flight's ghost from the cell comes with the camera
  groundwork for rival flights, not in the first build (7.7).
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
   its payload files and the number and hash of the same player's previous standing.
   Changing an old file breaks the chain behind it, and the number tells a standing that is
   simply gone (history incomplete) from a chain that does not connect.
3. **Every game remembers what it saw.** For each rival, the local rival cache keeps the
   number and hash of every standing it has applied (the witness log, outside the shared
   folder). Every standing a player exports also lists the latest standing of each rival it
   had seen, with its hash: a receipt the other players hold for you. A rival standing that
   changed after it was seen, a chain that does not connect, or a number that went backwards
   is not applied: that rival's last good state stays, and the Space Race window says
   "files in Ana's folder were changed", with the reason, visible to every player; no
   points move (owner, 2026-11-15). The wording is deliberate. Without
   signatures nobody can tell WHO changed a file in Ana's folder, and the likeliest victim of
   an edit is Ana herself, so the mark describes the folder and accuses nobody. An older
   standing that is simply gone is "history incomplete", not a change: owners prune (6.3),
   and the newer standing is applied as usual. A receipt that contradicts a file is shown as
   a disagreement naming both players. A game that never saw the real file (a late joiner,
   a rebuilt cache) has no earlier state to keep, so it applies the file it finds and shows
   that disagreement; until the writer's game heals the folder (item 4) its board can
   differ from the others' (principle 4). The race file is treated the same way: each game
   keeps the copy it took when it joined, and a `race.cfg` that differs from it is not
   applied and is marked.
4. **Your own files heal themselves.** At every sync the exporter compares its subfolder in
   the shared folder with its local copy of what it wrote.
   - A file it wrote that was changed or deleted there is put back: a standing from the own
     mirror, a payload file from the own mirror or, while the save still holds those exact
     bytes, from the save. Payload of an older standing that neither holds any more cannot
     be put back; the others keep the newer standing they already applied.
   - A standing it did not write (a higher number than its own latest) is not deleted, since
     it may be this same player's export from another machine or from before a restored
     backup. Instead the game writes a fresh standing after it at once, with a note saying
     which numbers it replaces. A standing is a full snapshot, so whatever the unknown files
     claimed is replaced, and the others see the note and clear the mark.
   - The same files changed back again and again means two games write this subfolder (the
     same player playing the race on two computers at once, 8 case 3). The game puts its
     files back twice, then stops putting them back and says on screen that the race
     folder is being written from two computers (owner, 2026-11-15).
   Either way the player is told once on screen.
5. **The replay is the proof** (7.8). A date on the board counts only when the flight behind
   it is shared and covers that date, and anyone can watch it happen at that date.
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
  is not running to heal it, and a swapped key is itself a detected change. (Spike S12,
  optional, checks that the signing classes of the game's runtime work in KSP and how fast;
  the format reserves a signature field from the start.)
- **B. Flight check.** Instead of trusting the claimed milestone table, each game checks a
  rival's claim against the recording itself and marks the cell when it does not hold:
  - *The claim check:* at the claimed date the trajectory really does what the milestone
    says (at rest on the Mun's surface for a landing, a closed orbit around Duna for an
    orbit).
  - *The teleport check:* the recording is continuous. The offline recording analyzer has
    no continuity rule today, so this is a new rule (15.4); it would run over a rival's
    recording and over the player's own alike, and catch a craft moved by an editor or a
    debug tool.
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
score: the last good state keeps scoring, because the mark cannot say who is at fault. This
is the one place where what a machine remembers decides what it scores (principle 4).

---

## 8. Edge cases

1. **A rival's standing is half synced.** Named payload missing or failing its hash: the
   previous applied standing stays in effect; retry at the next sync. Never partially applied.
2. **Sync-service conflict copies** (`... (1).pstd`, "conflicted copy"): ignored, named in a
   log warning. With one writer per file they can only come from misuse (case 3).
3. **Same player id on two machines.** Both write `players/<id>/`. Standings with equal seq
   and different content: the importer takes neither, warns naming the two-machine cause, and
   keeps the last good one. Each of the two games also puts back the files it wrote (7.15),
   which would undo the other's files forever; so a game puts the same files back twice,
   then stops and says on screen that the race folder is being written from two computers
   (owner, 2026-11-15). The supported way to move machines is to copy the save and play it
   in one place at a time (7.2).
4. **Two joiners claim one slot or one look.** Section 7.3: a shared site is kept by both; a
   clashing look is changed for the later joiner.
5. **The shared folder is offline or gone.** The save plays on with the cached rivals. If the
   folder is deleted for good, every player's save is still a complete game; relinking a new
   folder under the same race id restarts sharing from each player's next standing.
6. **A player quits the race.** They keep their seat and their points (owner, 2026-11-15):
   their last standing stays, so their space center, ghosts, flags and board dates remain,
   and their slot and look stay taken. Nobody can remove a player. The window shows their
   last shared time.
7. **A player joins late.** They start at day 1 like everyone and are behind only in real
   time. Game dates are comparable because every save starts at day 1. Nobody else's save
   changes (7.2).
7a. **A fifth friend wants in.** Refused: four seats, four sites, four looks. A seat is not
   freed when a player stops playing, because their flights and dates stay in the race
   (owner, 2026-11-15).
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
10. **A Re-Fly of a flight the rivals already have.** A flight that can still be re-flown is
    shared like any other. Only a flight that earned nothing after its separation can be
    re-flown, so a Re-Fly never removes the date the ledger credits; at most it removes a later
    repeat of a milestone held earlier. The merged attempt replaces the old flight for the
    rivals at the next sync (their ghost of it changes). If the attempt reaches a milestone, its
    date is the game date of the re-flown flight and follows the usual rules, the no-retakes
    rule included (7.5, 7.11). An open attempt is never shared.
11. **Two players reach a milestone on the same game day.** The earlier second holds the
    first with full points; the later player holds nothing for it, even two minutes later
    across midnight or on the same morning (6.6, 7.8).
11a. **Two players reach a milestone in the very same second.** Both hold it and share its
    points exactly (7.8). Expected almost never. Players level on total points on the final
    board are joint winners; there is no tiebreak (7.10).
12. **A milestone reached with no recording behind it** (a row the ledger holds without a
    flight): the exporter names the flight whose range covers the date; if there is none it
    is listed as unverified and scores nothing (7.8). A row tagged to a flight that is no
    longer effective is dropped, never credited to another flight.
13. **A rival uses parts I do not have.** The ghost draws without them and the window says so.
14. **A rival runs a newer Parsek.** If their standing's race format is one this build
    reads, their board dates are checked and score as usual, and only the ghosts of
    recordings this build cannot read wait for an update. If the race format itself is
    newer, their rows count as stated, marked "not checked here" (7.8). The window says
    which.
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
21. **I change difficulty settings mid-race.** Allowed; the others see which settings I
    changed (owner, 2026-11-15; 7.12). The one exception is stock's "allow other launch
    sites" in a race created without the alternate sites: the race setting wins, launches
    from those sites stay refused and the option is greyed with the reason (owner,
    2026-11-15; 7.3).
21a. **I want to give a kerbal a different suit.** Not in a race save: the suit buttons of
    the stock suit picker are greyed there with the reason "Race suits are locked to your
    race look", and every kerbal wears my race look; I can still take off the helmet and neck
    ring and change the suit light color (owner, 2026-11-15; 7.4). Changing my look in
    Settings re-dresses the whole roster at the next load.
22. **I open the race save with multiplayer code missing** (an older Parsek build, or Parsek
    removed). The race mark is a scenario node of its own, which the game keeps and writes
    back even when no installed code knows it (6.2; spike S6 checks it, through Parsek's own
    save rewrites too), so the save is still
    a race save when the right build opens it again. But the sites are not applied by a
    build that does not know them: a player at site 0 notices nothing, while a player at
    another site finds their space center back at the stock location and their vessels at
    the home site standing on unflattened ground, where stock may destroy them. An older
    build cannot warn about this, so the release notes of the first race build do: do not
    open a race save with an older Parsek.
23. **A single-player save is loaded after a race save in one session.** The sites, patches
    and scenery are removed at the scene load request, before the single-player save's scene
    builds, whether the load comes through the main menu or from inside a game (7.1). A
    harness lane pins both routes.
24. **Kerbin is changed by a planet mod.** The site table assumes stock Kerbin. A race refuses
    to start on a modified Kerbin and says why (a refusal, like a newer format or a missing
    expansion the race needs, because terrain parity is not a matter of taste). A race save
    already under way whose space center cannot be set up (Kerbin changed after the race
    started) is not loaded: the game stays at the main menu with one message and the save
    is left untouched, since loading it on stock Kerbin could destroy its vessels (owner,
    2026-11-15). The refusal is decided before the first scene loads (7.3).
25. **Three players; site 3 is empty.** Its buildings stand, fully built like every site, and
    nobody launches from them.
26. **My site starts at night and my rival's in daylight.** Yes: the sites are spread around
    Kerbin. Time warp to morning costs a fraction of a game day (7.3).
27. **A rival recovered a lander weeks ago and I still see it.** Not after the rival's next
    export: their standing says until when each leftover was there (7.7). Before that export
    arrives, the old state shows.
28. **A rival flew to a body my install does not have.** The flight is not played here; its
    board row shows, marked "cannot be watched here", and still scores (7.6, 7.8).
29. **I want to change my name or my look mid-race.** Both are cosmetic and live outside the
    save: the Multiplayer section of Settings changes them if the new name or look is free,
    and the others see it at the next scene after their next sync. My kerbals wear the new
    look from my next load (7.4).
30. **I rewind to retake a first in Science or Career and cannot afford the parts I used
    later.** The single-player rule holds: science my committed future spends is reserved.
    A retake is flown with what I really had at that date. That is the price of a retake.
31. **Two race saves on one install.** Each has its own mark and local race directory. The
    sites are reconciled at every scene load request against the loaded game's mark, so
    going from one race save straight to another with a different home site moves them
    (7.1).

---

## 9. Error recovery

| Failure | Recovery | Data loss |
|---------|----------|-----------|
| Export interrupted (crash, folder offline) | Payload without a standing is inert. After a crash the next tick writes a fresh standing; a standing already in the own mirror is copied out, with its payload, when the folder returns (6.3) | None |
| Shared-folder write hits a sync-client lock | Safe-write retries with backoff, then waits for the next tick | None |
| Corrupt or unsafe standing (bad hash, bad id, bad stamp, truncated) | Refused whole with the file and reason logged; the previous applied standing stays | None locally |
| Cloud placeholder payload (not downloaded) | The copy step brings it into the local race directory, and the game reads only that copy. With the copy worker the download happens outside the game's frame loop. In the main-thread form, which is built first, no import runs during a flight; outside flight at most one placeholder is opened per poll (opening is what makes the sync program download it), with its open time logged, so a standing with several placeholder files arrives over several polls, and that first open can stall the Space Center or the Tracking Station (15.9; the worker is open: question Q-18, section 13). The status text says what is still arriving | None; slow |
| Rival cache deleted or damaged | Rebuilt from the folder at the next sync | None; the witness log restarts, so earlier changes to a rival's history can no longer be noticed by this machine |
| A rival standing changed after it was seen, or its chain does not connect | Not applied; the last good state stays and keeps scoring; the window says files in that player's folder were changed, with the reason; that player's own game repairs it at its next sync | None |
| The local player's subfolder was changed by someone else | Files this game wrote are put back; a standing it did not write is superseded by a fresh one with a note (7.15); one on-screen message. The same files changed again after two put-backs: the game stops putting them back and says the folder is being written from two computers (8 case 3; owner, 2026-11-15) | None |
| A load rolls the save back | The rival store is unaffected; the next export is a fresh snapshot of the rolled-back state; one message says how many shared flights left the race | The dropped flights leave the race, as they left the save |
| The local race directory beside the save is gone | Settings asks for the shared folder again; the own mirror and the rival cache are rebuilt from it at the next sync | None; the witness log restarts |
| The save was restored from an older backup | Plays on; numbering continues from the folder; the next export is a snapshot of the restored state | Flights made after the backup leave the race |
| Race manifest missing from the folder | The save keeps playing on its `RaceLink` (sites, ladder, mode); sharing pauses with a status note | None |

Default-safe throughout: when in doubt, do not apply the rival's new state, keep the old one,
and say exactly why.

---

## 10. Logging

Per the standing convention (`[Parsek][LEVEL][Subsystem] message`), with batch counters for
per-item loops. One plain tag per subsystem, never a nested form: `Race` (mark, setup,
gate), `RaceSites` (move, patches, scenery), `RaceLaunchGuard` (refused launches),
`RaceExport`, `RaceSync` (every sync, import and poll line), `RivalStore`, `RivalLane` (the
rival ghost lane), `RaceBoard`, `RaceLooks`. A race file that logs under a tag not on this
list fails a source gate. The race sync messages are posted from one place, the sync
driver, which logs each as `[RaceSync] Message posted:`.

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
- Board derivation: holders ordered to the exact second, including two dates on one game
  day and two dates either side of midnight; one share test of the very same second (exact
  twelfths, nothing rounded); an unverified or tool-marked date earlier than a scoring one
  is shown, holds nothing, and under no retakes blocks nobody; the calendar changes the
  displayed dates and no holder; the end date cut; level total points name joint winners; a
  manifest whose `dateUnit` is not `SECOND` is refused.
- Difficulty disclosure: a changed setting is named by the mark; an unchanged save carries
  no mark.
- Verification: a date inside the named recording, inside a sibling recording of the same
  tree, on a boundary within the tolerance, outside every range.
- The no-retakes rule over standing histories: blind dates, a date improved after seeing, a
  rival's claim that later disappears, a seen rival date that itself won nothing still
  blocking, a late joiner, export-before-import in one tick.
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
- Same files, same board: the board computed from one untampered folder on two machines
  running the same build, with different arrival orders, calendar settings and installed
  bodies, is identical.
- A changed file: the machine that saw the real standing keeps it, scores it and marks the
  folder; a machine that never saw it applies the changed one and shows the receipt
  disagreement; after the writer's heal both boards agree.
- Two dates for one milestone: the earlier one tool-marked or blocked, the later one counts;
  a tool first used at or before the date in an earlier recording of the same tree marks
  the date, and a tool first used after it, in a later recording or later in the same one,
  does not.
- Debug completions: a milestone date the standing lists as completed from the debug menu
  is not on the board, the next player holds, and the player carries the mark.
- The keep rule after the end date: a date the last pre-end standing lists and the latest
  standing dropped still has its `since` standing; a pre-end standing whose payload the
  save has since rewritten is completed from the own mirror.
- Cost: building the rival store opens no trajectory or vessel payload file (`.prec`,
  `.craft`); it reads the standings and the rival tree files only.
- A late join: slot and look taken from what is free, no change to the existing players'
  state, the no-retakes rule for a player who has seen everything.
- The isolation test of 6.4 (byte-identical ledger walk and save output with and without a
  rival cache).
- The grep-audit and module-layer gates of 6.4.

**In-game tests** (new categories, each in one scene and hosted by one harness lane, which
hosts several categories: RACE-9 the Space Center ones, RACE-10 the flight ones, RACE-11 the
Tracking Station ones, SITES-3 the editor one, SITES-2 the one for a single-player save
loaded after a race save): the working space center at each of the four sites (pad and
runway spawn, Space Center scene, recovery distance); terrain height at fixed sample points
of all four patches equals the site table; scenery present and inert; sites removed on
leaving the race save; looks applied to own roster and to an EVA ghost; the suit buttons of
the stock suit picker greyed with their reason and the suit choice refused in a race save,
with the helmet, neck ring and suit light still free, and the picker untouched in a
single-player save; stock's "allow other launch sites" option greyed with its reason in a
race created without the shared sites; a rival flag is
readable and it is not a vessel; a rival leftover has no colliders; a rival ghost crossing a
sphere of influence and landing leaves the local progress and ledger unchanged; a rival relay
is not a CommNet node; a vessel standing on a scenery pad is not "at the launch pad" and
gives no building science; an EVA ghost recorded in a suit this install lacks is still drawn.

**Harness lanes** (`RACE-0` to `RACE-13`, and `SITES-1` to `SITES-3` for the sites). One PC
can play both sides in turn, against a shared folder the harness stages inside the game
instance (the harness touches no folder outside it, so a folder elsewhere on disk is left to
the play test). The lanes drive the race through the command seam (`RaceSetup
action=create|join`, `RaceCommand action=status|set-folder|sync|offline|online`,
`ListHandles kind=sites|rivals|board`) and one harness step that stages a shared folder.
Every race lane loads at the Space Center, and a spec runs one in-game batch, so per-site
values are read through `ListHandles kind=sites`. Every race the harness plays is marked by
the test tooling (7.12), so its own flights never score: scoring, holder changes, no retakes
and the end date are proven headless and with generator-built rival folders, which carry no
mark (open: question Q-24, section 13).

- Player A creates a race and flies to orbit; player B joins, syncs, and the lane asserts A's
  ghost launch from site 0, the board row with its tool mark, and B's untouched ledger.
- A generator-built rival folder in which B reaches the milestone earlier arrives; A syncs;
  the lane asserts the holder change and the message.
- A quickloads past a shared flight; B syncs; the ghost and the row are gone.
- A race save, then a single-player save in one session, through the main menu and through
  a load from inside the game: stock Kerbin restored. Two race saves with different home
  sites in one session.
- The whole-save isolation lane of 6.4: the same scripted session with an empty and with a
  full rival cache produces saved games that match under a canonical comparison in which
  only listed time values may differ, each with a written reason; two flown sessions never
  land on exactly the same times, so byte identity is proven headless only (open: question
  Q-13, section 13).
- Optional, on the operator's PC: two game copies writing one shared folder at the same
  moment (open: question Q-25, section 13).

**Fixtures:** a synthetic shared folder builder (several players, standings and payload) next
to the existing generators, so import, the board and the window can be tested without a second
install. Committed shared-folder fixtures live in `harness/fixtures/race/`, not under
`harness/fixtures/saves/`, which a recursive fixture glob reads. The offline recording
analyzer never reads the rival cache.

---

## 12. Feasibility in KSP 1.12.5

Checked against the decompiled stock assembly on 2026-11-14. Signatures and field names are
reliable; bodies are obfuscation-noisy, so each item ends in what must still be proven in the
running game.

| Idea | Finding | Still to prove (spike) |
|------|---------|------------------------|
| Launch from another place | Stock has a launch-site mechanism: `LaunchSite` (public constructor) and `PSystemSetup.AddLaunchSite` (public); the stock alternate sites use it | Not needed for the home site if the working KSC is moved |
| Move the working KSC | `SpaceCenter` reads its position from its transform at `Start` (private latitude, longitude, surface normal); the flatten patch is a `PQSMod_MapDecalTangent` with public position, radius and absolute offset; the KSC is a `PQSCity`. KSCSwitcher and Kopernicus move all three on 1.12. A race moves the space center object and leaves the stock patch at site 0, adding the patches of sites 1 to 3 (7.3). What also caches the old place: the city's longitude rotation, the facility spawn points (`PSystemSetup.SpaceCenterFacility.SpawnPoint`, refreshed only on a level change), every reader of the space center's cached position (survey contracts, morning warp, records, random surface points, music) and `CommNetHome` | S4: move it in a spike and run a full launch, recovery and Space Center visit; check Parsek's own readers of the space center position (`LaunchSiteExclusionZones`, launch-site names in recordings, KSC-scene ghost playback; the optimizer's launch-site rule reads only the name, not a position) |
| The Space Center camera at the home site | `SpaceCenterCamera2` finds its start position by a named transform under Kerbin's surface (`pqsName`, `initialPositionTransformName`), which is part of the KSC object that moves; `altitudeInitial` is public. KSCSwitcher resets this camera after a move | S4: enter the Space Center scene at each of the four sites; camera height, building clicks, the day and night lighting |
| Exact KSC copies as scenery | Kerbal Konstructs clones the stock KSC buildings at run time as static objects | S5: clone the hierarchy at the fully built level, from each facility's last `UpgradeLevel.facilityPrefab` rather than the live KSC (which shows the local levels and registers duplicate ids), strip facility, click and destruction behaviour before `Awake`, keep the colliders but remove the tags stock reads from them (launch pad, runway, building science); measure load, memory and frame cost of three copies |
| Same terrain in every game | The patch is data on a PQS mod; the same patch data gives the same heights | S5: sample heights in two game processes on one PC (the gate); a second machine is a cross-check |
| Dry land on the equator | MEASURED 2026-11-14 (`docs/dev/research/kerbin-equator-survey-2026-11-14/`): half the KSC latitude is dry, and there is dry ground within 3.4 degrees of each ideal point, at 135 m, about 520 m and about 675 m. The eastern quarter point itself is open sea. No fine survey grid covers the three spots (the fine step is about 262 m) and no survey row is at the KSC latitude (-0.074) | Site height is decided (natural height). Remaining: S1b scans the KSC latitude at about 20 m around the three spots for the exact coordinates and checks that the whole footprint is dry |
| KSC science at every site | Building mini-biomes are tag-based (`MiniBiome`), so they should follow the buildings. Stock names the landed-at place from collider and trigger tags (`Part.SetLandedAt`), and the experiment biome reads that name first (`ScienceUtil.GetExperimentBiome`), so the KSC area is probably named by tags too; the survey's biome read finds no KSC biome at the KSC's longitudes | S2: prove both at a moved site; a biome override is designed only if S2 shows the KSC area is read from the biome map; prove the scenery copies give none |
| The shared launch sites | All stock alternate launch sites belong to Making History; a game without it has none (`LaunchSiteExclusionZones` already checks for the expansion) | None; it is a race setting (7.3) |
| The race mark survives an older build | Stock holds each scenario node as data and writes it back; a node whose module type is not installed should be kept the same way | S6: save a game holding a scenario node of an unknown type and read it back, through stock and through Parsek's own save rewrites (rewind copies, the Discard Re-fly write of `persistent.sfs`, the crash reconcile) |
| Four stock looks | `ProtoCrewMember.KerbalSuit` has Default, Vintage, Future, Slim; Vintage and Future belong to the DLCs; per kerbal the suit, the combo id and the light color are saved | S3: confirm each look in game and pick the four UI colors. The suit picker's one opener and its suit, helmet and light controls are known by decompile; the task that blocks the picker (plan, looks phase) re-checks them and proves the block in game (7.4) |
| Stock-like replacements | Mods add suit sets with `SUITCOMBOS` config nodes (suit type, gender, textures, sprite); stock supports this since 1.10 | S3: two replacement texture sets; how a missing-DLC machine resolves a DLC suit. A `SUITCOMBOS` node under `GameData` shows in the stock suit picker of every save, single player included; whether the sets are listed there (only single-player saves would see them, since the suit choice is blocked in a race save) or applied only to race kerbals and rival ghosts, and where their art comes from, is open: questions Q-19 and Q-20, section 13 |
| Look on ghost kerbals | Parsek builds EVA ghosts from the stock EVA part model | S3: apply a suit texture to the ghost |
| Multiplayer in the main menu | `MainMenu` holds its entries as public `TextProButton3D` fields with a public tap callback; the entry lists used for keyboard navigation are private | S6: clone an entry, place it, wire keyboard navigation |
| Create a save from code | `GamePersistence.CreateNewGame(name, mode, parameters, flag, scene, facility)` is public and is what the stock New Game dialog calls | S6: create and start a race save from the dialog; open the stock difficulty panel from it (if it cannot, the creator gets the four stock presets: owner, 2026-11-15, Q-17 in 13.A) |
| Milestones in every mode | `ProgressTracking` is added to every new game, Sandbox included; Parsek's recorder writes a milestone event in every mode | None |
| Readable rival flags | `FlagSite` exposes Read Plaque, Take Down and Pick Up as separate part events, but a real flag is a saved vessel whose placed state re-enables taking it down, and the plaque is readable only from within 2 m on EVA. A replica (flag model, click target, the stock plaque dialog) avoids all of that. The flag part has no mesh file to clone; the dialog's entry `FlagPlaqueDialog.Spawn(string, string, Callback)` is public and static | S7: how to build the replica's model; open the stock plaque dialog with supplied text (if it cannot, one small Parsek popup, owner 2026-11-15, 7.7); decide the click range |
| Launch areas stay clear | Parsek already retires a flight that ends parked on the KSC pad, the runway threshold or a stock alternate site (50 m circles: the KSC pad and runway threshold are constants in `SpawnCollisionDetector`, the other launch sites are read from `PSystemSetup` by `LaunchSiteExclusionZones`, whose read admits the names `LaunchPad` and `Runway`, so its cache possibly holds the stock KSC's own pad and runway too); a rival end state is only a ghost, so not showing it is a filter at the rival store | S4: measure the pad and full runway footprints at the moved sites and the alternate sites |
| Cheat disclosure | `CheatOptions` holds the stock cheat switches as public static fields; hack gravity is not one of them but the state of the `HackGravity` debug widget, which Parsek already reads. The debug menu's ACTIONS (set orbit, set position, add science, unlock technology, max tech, max facilities, the console's currency sets) are not switches, no "fill resources" action is among the 1.12.5 cheat types (S9 confirms it in game), and Max Progression and the console complete milestones through `ProgressNode.CheatComplete()` | S9: which of those actions and completions a mod can observe |
| Joining later | Nothing game-side: a join writes one new subfolder; all four sites exist in every game from the start | None |
| Noticing edited files | Hashes, a chain and a local witness log need only file reads and a hash function, which the recording sidecars already use | None for the simple version; for signed standings, spike S12 (optional) proves the runtime's signing classes work inside KSP |

---

## 13. Open questions

### 13.A Decided by the owner after the reviews

D1 to D5 were asked as decision cards on 2026-11-14 and all answered the same day; section 0
records them as rulings; D1 was replaced on 2026-11-15 (row Dates). The rows after them
are the owner's answers to questions the implementation plan raised
(`docs/dev/plans/multiplayer/design-corrections.md`, part 2, where the numbers come from),
the owner's rulings on versions and on the branch, and the decision cards of 2026-11-15
(the setup choices and the smaller choices that were designer choices before). Of these
rows only Q-03 still waits for the owner: its timing is decided, and it is asked when spike
S8 is about to run (listed again at the end of 13.C).

| Id | Question | Proposal in this document |
|----|----------|---------------------------|
| D1 | How finely are game dates compared, and what does a tie give? | DECIDED 2026-11-14: whole game days; players tied on a day split the points. REPLACED 2026-11-15 by the row Dates below |
| D2 | May a player hold flights back and share them at the last moment before the end date? | DECIDED 2026-11-14: yes; the last standing stamped before the end counts (7.10) |
| D3 | Does a flight flown with cheat or debug tools score? | DECIDED 2026-11-14: no; shown, marked, worth nothing (7.12). A milestone completed from the debug menu outside any flight does not score either: it is dropped from the board and the player is marked (question Q-26, answered by this decision; 7.12) |
| D4 | The shared launch sites exist only with the Making History expansion. What does a race do? | DECIDED 2026-11-14: a race setting chosen by the creator; when it is on, joining needs the expansion (7.3) |
| D5 | Are a rival's buildings solid? | DECIDED 2026-11-14: solid, fully built, and the same in every game (7.3) |
| Q-03 | Which sync programs does the file-speed test (spike S8) cover? | DEFERRED BY THE OWNER 2026-11-14 ("leave this at the end, its an external dependency"): S8 runs last, after the race works end to end on a plain shared folder; the copy worker and the poll tuning wait for it. The question itself is asked when S8 is about to run (13.C, 14) |
| Q-04 | Do the two single-player launch pad fixes (a stored longitude wound past the date line; alternate sites read before they were built) land with the launch-area rule? | DECIDED 2026-11-14: fix both on main as their own work, outside this feature; they are on main, and every prerefactor stays behavior-neutral (14) |
| Q-05 | How long does the owner's PC fly to prove the ghost positioner change? | DECIDED 2026-11-14: a representative set of 18 lanes, one per way the positioner places a ghost, plus the daily tier; the full run of every lane happens once, at release (14) |
| Q-07 | Is the groundwork for the Watch camera on a rival flight built now or later? | DECIDED 2026-11-14: later; the Watch camera on rival flights is not in the first build (7.7, 14) |
| Q-08 | Does the Multiplayer menu entry stay hidden during development? | DECIDED 2026-11-15: no; it is always visible on the `multiplayer` branch, which no player sees before the final merge; there is no developer switch and no later change to turn it on (7.1, 14) |
| Q-09 | Who sets how long a race day is? | DECIDED 2026-11-15: the creator's own calendar setting at creation, shown in the setup dialog, with no field of its own. Since the row Dates it sets only how dates are displayed (6.6, 7.2) |
| Q-10 | What happens to a race save whose space center cannot be built? | DECIDED 2026-11-15: it is not loaded; one message, back to the main menu, the save untouched (7.3, 8 case 24) |
| Q-11 | A player turns on stock's "allow other launch sites" in a race created without them | DECIDED 2026-11-15: the race setting wins; launches from those sites stay refused and the option is greyed with the reason (7.3, 8 case 21) |
| Q-12 | One player writes the race folder from two computers at once | DECIDED 2026-11-15: the game puts its files back twice, then stops and says so on screen (7.15, 8 case 3) |
| Q-14 | Which firsts are announced on screen? | DECIDED 2026-11-15: only those where a rival is involved (beaten, lost to, or tied with in the very same second); a first no rival had reached shows only on the board (7.8) |
| Q-15 | Under "no retakes", does a rival date that is itself "seen first" still block? | DECIDED 2026-11-15: yes; any real, clean rival flight your game had seen blocks you, even one that won nothing itself (7.11) |
| Q-16 | Which messages appear after the end date? | DECIDED 2026-11-15: only final results: one "the race is over" message, and one when a late file changes the final board; later changes to the board of the present show in the window only (7.8, 7.10) |
| Q-17 | What does the creator get if the stock difficulty panel cannot be opened from the setup dialog? | DECIDED 2026-11-15: the full stock difficulty panel, with the four stock presets as the fallback if spike S6 shows the panel cannot be opened there; the manifest stores the full settings either way. Custom settings after a failed S6 are not an owner question now; they come back only if S6 fails (7.2, 12) |
| Q-21 | If the stock plaque window cannot show a rival flag's text, may Parsek show its own popup? | DECIDED 2026-11-15: yes, one small popup, the fourth new-window exception, only if spike S7 shows the stock window cannot (0, 7.7) |
| Q-22 | How is a rival's ghost labeled? | DECIDED 2026-11-15: owner name and color, such as "Ghost: Ana - Kerbal X", in the ghost label and the map and Tracking Station lists (7.4) |
| Setup | What does the creator choose? | DECIDED 2026-11-15: the mode, once, for everyone; the difficulty with the full stock panel (the four stock presets if the panel cannot be opened there); an optional end date; "no retakes", off by default; the shared launch sites, off by default; the game's name; always four seats, no setting. None of it changes after creation, and the creator is then a player like the others (7.2, 7.3, 7.10, 7.11) |
| Difficulty | May a player change their own difficulty mid-game? | DECIDED 2026-11-15: yes, nothing is blocked (apart from the launch sites option, Q-11); a mark next to the player's name names the settings that changed (7.12) |
| Tools | How far does a cheat or debug tool mark its flight? | DECIDED 2026-11-15: every milestone the flight reaches after the tool was first used, also after it is switched off (7.12) |
| Rival ghosts | Targeting, CommNet, the Space Center view | DECIDED 2026-11-15: a rival ghost can be targeted like an own ghost; it is never a CommNet relay or control point; it does not show in the Space Center view in the first build, where rival buildings do (6.4, 7.7) |
| Leftovers | Which rival vessels stay as scenery? | DECIDED 2026-11-15: flyable vessels only (landers, stations, rovers, bases); rival debris is not shown or listed as a leftover (7.7) |
| Seats | Who may join, and what happens to a player who stops? | DECIDED 2026-11-15: anyone with the folder; no join password. A player who stops keeps seat and points, and nobody can remove a player (7.2, 8 cases 6, 7a) |
| Window | Where does the Space Race window open? | DECIDED 2026-11-15: in the Flight and Space Center scenes, like every Parsek window; not in the Tracking Station (7.13) |
| Suit | May players change a kerbal's suit in a race? | DECIDED 2026-11-15, against the recommendation: no; the race suit is locked, kerbals always wear the player's look, and the suit buttons of the stock suit picker are greyed with their reason and the suit choice is blocked in a race save; the helmet, neck ring and suit light stay free (owner, 2026-11-15) (7.4) |
| Dates | How finely are game dates compared, and what does a tie give? | DECIDED 2026-11-15, replacing D1: to the exact second of game time ("we should just compare exact second when the milestone is achieved instead of using this tie mechanism; it's simpler and more realistic"). The earliest second holds the first with full points; only the very same second is shared, its points split exactly; players level on total points on the final board are joint winners, with no tiebreak. A tiebreak by the second for same-day ties, ruled earlier the same day, is moot. The owner accepted the costs: seat luck on the first Mun and Minmus flights, the opening firsts to the quickest launch, a seen date beaten by one second (6.6, 7.8, 7.10) |
| Changed files | What does a hand-edited shared file do to its player? | DECIDED 2026-11-15: a neutral mark, "files in <player>'s folder were changed", visible to all; no points move; the last good state stays (7.15) |
| Folder entry | How is the shared folder location entered? | DECIDED 2026-11-15: typed or pasted into a text field in the setup dialog and in Settings, checked on Validate; no browse button, no folder picker (7.2) |
| Re-Fly | Is anything sent to rivals while a Re-Fly is in progress? | DECIDED 2026-11-15: no; sharing waits until it ends (7.5) |
| Versions | Which versions ship what? | DECIDED 2026-11-14: the refactored base build ships as version 1.0.2 ("1.1" in this document names that code structure, not a version); the multiplayer mode ships as version 1.1.0 (14) |
| Branch | Where does the work land? | DECIDED 2026-11-15: on one long-lived `multiplayer` branch with sub-branches; main stays clean until one final merge (14) |

### 13.B Chosen by the designer, for the owner to overrule

Smaller choices the reviews forced. Each has one sensible answer under the rulings already
given, so they are written in as decided; any of them can be reopened. The later rows were
taken while the implementation plan was joined; a row that names a question is one the plan
also put to the owner, and the design follows it until the owner answers (13.C).

| Choice | Where |
|--------|-------|
| Rival flags are plaque replicas (flag model plus a click target that opens the stock plaque dialog), not real flag vessels | 7.7 |
| An any-time replay viewer of rival flights is later; in the first build a rival flight is seen while the local clock is inside it, without the Watch camera (owner answer Q-07, 13.A) | 7.7, 7.13 |
| Under "no retakes": a block lasts only while the rival's claim stands, and improving your own date never costs you the date you had | 7.11 |
| Moving to a new PC is copying the save folder; "start over in my seat" exists only for a lost save and says what it costs | 7.2 |
| Two players who claimed one site at the same moment both keep it; only a clashing look is changed | 7.3 |
| The 60-second standing-still rule is not in the first build | 7.7 |
| Rival sites get no map marker (how the folder location is entered is the owner's, 13.A) | 0, 7.2 |
| Every file access is on the main thread first; one background worker that only copies files comes only with the owner's yes, as an exception to the no-threads rule, after spike S8, which runs last (question Q-18) | 15.9 |
| A rival's docking approach, or a kerbal beside its lander, is played from the stored body-fixed positions, not relative to the other vessel | 15.5 |
| A date that scores nothing (unverified, or flown with a listed tool) cannot hold a first and, under "no retakes", cannot block one; the next scoring date holds it (that a "seen first" date still blocks is the owner's, Q-15, 13.A) | 7.8, 7.11 |
| A standing in a race format newer than the local build reads counts as it states, marked "not checked here"; newer recordings in a standing the build can read only keep their ghosts from playing | 6.3, 7.8 |
| A changed file is refused only by the games that saw the real one; until its writer's game restores it, a game that never saw it can show a different board | 2, 7.15 |
| The tool mark (how far it reaches is the owner's, 13.A) is carried as a first-use date per tool on each recording and follows the flight across its cuts into recordings; a tool first used after a date does not reach back to it; scoring such a milestone takes an earlier clean flight after a rewind | 7.12 |
| A game that has just joined exports nothing until its first import is complete | 7.5 |
| The own mirror keeps payload until it is in the shared folder, and the last pre-end standing's payload for good | 6.3 |
| The playback catalog port stays in the `Recording` module; the rival lane is given an empty catalog (`RecordingPlaybackCatalog.None`), and there is no rival catalog type | 15.6, 15.11 |
| The line and label style (`PlaybackTrajectoryStyle`) and its engine arguments are base Parsek, built with the looks work and landed right after the engine prerefactors, before the rival lane's style source; rivals show in a neutral color until the player colors exist | 15.11, 14 |
| The trajectory decode change (P6) is built in parallel with the other prerefactors and merged when green, outside the window in which the ghost positioner change is proven by flights (question Q-06) | 14, 15.11 |
| Races flown by the harness stay marked as tool-flown and never score; scoring, holder changes, no retakes and the end date are proven without the game and with generated rival folders (question Q-24) | 7.12, 11 |
| Setup lives in Controllers (`ParsekRaceSetup`) behind an interface the UI declares; the UI does not create a save through the `RaceWorld` facade | 7.2, 15.4 |
| The race world is reconciled at every scene load request against the loaded game's mark, not only on the way to the main menu | 7.1, 15.7 |
| The whole-save isolation lane compares the two saves under a canonical comparison in which only listed time values may differ, each with a written reason; byte identity is proven headless (question Q-13) | 6.4, 11 |
| The replacement suit sets are applied only to race kerbals and rival ghosts, not listed in the stock suit picker, if spike S3 shows the applied texture in EVA, IVA and the portrait (question Q-19; with the race picker blocked, what is open is only a listing in single-player saves) | 7.4 |
| The first board of a session is compared against the one built at load, so a load posts no message (which changes are announced is the owner's, Q-14 and Q-16, 13.A) | 7.8 |

### 13.C For the play test and the phase plans

1. **The ladder's numbers.** The steps, the body multipliers, moon flybys, and whether crewed
   and uncrewed firsts differ (7.9). They want a play test before they are frozen under a
   `ladderId`. The first table assumes Jool at 3 (flyby, orbit, return from orbit), the Sun
   left out, and one "kerbal on the surface" step worth 1.
2. **The exact coordinates of the three new sites.** Their height is decided (natural
   height, 7.3). What remains is spike S1b: a scan of the KSC latitude at about 20 m around
   106.7 E, 18.8 E and 162.3 W to pick the exact spot where the whole footprint is dry at
   the KSC's orientation, and a look at what water lies near each. Biomes near each spot
   come from the stock biome function in the game (spike S2), not from the survey's data.
3. **Targeting rival ghosts.** Decided: allowed, like the player's own ghosts (owner,
   2026-11-15; 13.A).
4. **How deep verification goes.** The flight exists and covers the date (the first build),
   or also a check of the trajectory against the claim (7.15, step B).
5. **One-goal races.** Not planned (owner, 2026-11-15): a race is a whole game scored across
   every milestone (section 1), never a contest for a single one.
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
    its own local race directory. Going from one race save straight to another with a
    different home site is covered by reconciling the sites at every scene load request
    against the loaded game's mark (7.1, 15.7); a harness lane drives it.

**Owner questions still open.** Numbered as in
`docs/dev/plans/multiplayer/design-corrections.md`, part 2, which gives each its options.
Until the owner answers, the design and the plans follow the recommendation given here.
The questions the owner answered on 2026-11-15 are in 13.A.

- *Needed before work starts.*
  - Q-01: do the spikes run on their own copy of the game (a test copy and one without the
    expansions)? Recommended: yes, so the owner's own game stays as it is.
  - Q-02: is the spike branch kept on GitHub as a record, never merged? Recommended: yes;
    the spike reports cite its commits.
- *Assumed as recommended; the plans already follow them.*
  - Q-06: the trajectory decode change (P6) is built in parallel with the other
    prerefactors and merged when green (14).
  - Q-23: a rival ghost inside an atmosphere gets no map marker in flight in R3, as in the
    Tracking Station; revisited with the looks (15.6).
  - Q-24: races the harness flies stay tool-marked and never score; scoring is proven with
    generated rival folders (7.12, 11).
  - Q-25: two game copies racing on one PC is an optional operator lane (11).
- *Asked when their phase comes.*
  - Q-13 (R3): how the two saves of the "rivals change nothing" lane must match.
    Recommended: a canonical comparison where only listed time values may differ, each with
    a written reason (6.4, 11).
- *Asked after a spike.*
  - Q-18 (S8): may the race copy files on one background worker? Recommended: yes if S8
    shows freezes over one second on a common sync program, otherwise no; the main-thread
    form is built first either way (15.9).
  - Q-19 (S3): do the two replacement suit sets appear in the stock suit picker? Since the
    race suit is locked (13.A), the suit choice is blocked in a race save, so what is still
    open is only whether every single-player save may list the two sets. Recommended: no, they
    are applied only to race kerbals and rival ghosts, if S3 shows the applied texture in
    EVA, IVA and the portrait; otherwise yes (7.4, 12).
  - Q-20 (S3): where does the replacement suit art come from? Recommended: Parsek recolors
    the player's own base suit in memory at load; drawn art only if that looks wrong (7.4).
- *Asked at the end.*
  - Q-03 (before S8 runs): which sync programs does the file-speed test cover? Recommended:
    OneDrive plus Dropbox or Google Drive, plus an offline network share (13.A, 14).

---

## 14. Implementation phasing

No phase starts before the owner says so. Each phase is independently testable. The mode
ships as version 1.1.0 (owner decision of 2026-11-14); work starts after version 1.0.2, the
refactored build, is released ("1.1" in this document names that code structure, not a
version).

| Phase | Scope | Notes |
|-------|-------|-------|
| R0 | Spikes on the owner's PC: S1b (the exact spots of the three new sites, by a finer scan), S2 to S7 of section 12, S9 and S10 of 15.11, S11 (what one ghost costs per frame at race scale), S12 (hashing and signing inside the game's runtime; optional, for R7) and S13 (the results folded into this document). S8, the sync program speed test, runs last, after the race works end to end on a plain shared folder (owner answer to Q-03, 13.A). S1, the terrain survey, and the code inventory that sizes the prerefactors (`docs/dev/research/multiplayer-base-refactoring-2026-11-14.md`) are done | Answers, not code: site positions, scenery copies, suits, the difficulty panel, the plaque window, what a mod can see of debug tools. Retires the feasibility risks before any feature code |
| R0b | The prerefactors of 15.11, each its own change with no behavior change. The ghost positioner change (P2b) is one uninterrupted run of changes, proven at its end by a representative set of 18 lanes plus the daily tier, not by every lane (owner answer to Q-05, 13.A). The trajectory decode change (P6) is built in parallel and merged when green, outside that window (open: question Q-06, section 13). The groundwork for the Watch camera on rival flights (P3b) is not in the first build (owner answer to Q-07). The two single-player launch pad fixes landed on main outside this feature (owner answer to Q-04), so every prerefactor stays behavior-neutral | Base Parsek ready for a second ghost lane; no player-visible change. Single player keeps passing every suite and lane; the feature then lands on prepared ground. P2b is the largest. The additions of 15.11 land with the phase that first uses them; A1 and the capture halves of A2 and A3 land in R3 with export, because a fact that was not recorded cannot be rebuilt later. The line and label style seam (A5) is base Parsek and lands right after P2c and P2a, before the ghost positioner change (P2b) begins, ahead of its race users |
| R1 | The race mark and gate, the main menu entry (visible from this phase on; there is no developer switch, owner 2026-11-15), the setup dialog (create, join, continue), `race.cfg` and `player.cfg`, slot and look assignment. `RaceCore` is registered (rule, layer, headless line, the forbidden edges whose two ends exist) in the commit that creates its first file, not ahead of it; `RaceWorld` and `RaceExport` register the same way with their own first files (the rival flight type, which can start on day one, and export in R3) | A race save can be created and joined; it plays like single player |
| R2 | The four sites: moving the working space center, the four terrain patches, the scenery copies, launch rules (with the greyed "allow other launch sites" option), removal on leaving the save, refusing to load a race save whose sites cannot be set up, science parity | Each player starts at their own copy of the KSC; single-player saves are untouched. The world of the race, still without rivals |
| R3 | Export (standings, payload, digest) and import (the copy step, rival cache, rival store), with the race calendar, the ladder and the milestone tables the standing's milestones need; rival ghosts in flight, map view and the Tracking Station, in a neutral color until R5; leftovers, readable flags, launch areas kept clear, the isolation gates including the whole-save lane, and the simple tamper evidence of 7.15 (chain, witness log, receipts, self-heal). The Watch camera on rival flights is not part of it | "See your rivals": a player's flights reach the shared folder and show up for rivals as ghosts, leftovers and flags. Every file access is on the main thread |
| R4 | The board (dates compared to the exact second, 6.6), the Space Race window, holder-change messages | "Have a race" |
| R5 | Looks: own roster, the locked race suit and the blocked suit choice of the stock suit picker, ghost kerbals, the player colors on labels, icons and orbit lines (replacing R3's neutral color); the replacement suit sets | |
| R6 | End date and final board (level total points name joint winners), disclosure marks (naming the changed difficulty settings), the no-retakes house rule, missing-parts and newer-version notes. The copy worker (if the owner allows it, open: question Q-18, section 13) and the poll tuning wait for spike S8 and are the last pieces | |
| R7 | Hardening of 7.15, as far as the owner wants it: signed standings, the flight check (claim, teleport, sky, launch), the any-time replay viewer, the sealed-race house rule | Optional and not planned for the first build; the formats reserve room from R3 |
| R8 | Recorded opponents and the solo race (15.10): the `Opponents` folder, the third setup choice, a harness campaign that produces opponents | Optional and not planned for the first build. After R4, independent of R7; needs no change to R1 to R6 beyond the `kind` field and seat reservation they already carry |

The full run of every harness lane flies once, at release time, on the final state of the
work (owner answer to Q-05).

**One branch.** All of this work, the prerefactors and the tooling included, lands on one
long-lived branch, `multiplayer`, cut from main when the owner gives the go. Every
change is a sub-branch with a pull request into `multiplayer`, and main is merged into
`multiplayer` regularly (after every release from main and at least weekly), so main stays
clean until one final pull request merges `multiplayer` into it.

The tasks of each phase, their dependencies, the waves in which they can run side by side
and what needs the owner's PC are in `docs/dev/plans/multiplayer/` (its `README.md` first).

---

## 15. Architecture

Owner ruling 2026-11-14: multiplayer is implemented "on the structure of the 1.1.x build", and
"lets really think about the ideal structure and architecture from the start, so we don't have
a lot to refactor later". This section is that structure. (That build ships as version 1.0.2,
by the owner's decision of 2026-11-14; "1.1" in this document names its code structure, not a
version number. Multiplayer itself ships as version 1.1.0.) It is written against the 1.1 module
map (`scripts/arch/modules.toml`, `docs/dev/arch/README.md`): one folder per module, five
declared layers (floor, model, features, presentation, entry), headless modules, forbidden
edges, static lifetimes, all ratcheted in CI. The names the implementation plans fix are
used as they are; other file and type names are proposals; the shape is binding.

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

Three new modules, plus a few files in existing ones. The folder is the module: every source
file sits in its module's folder, `Source/Parsek/<Module>/<Name>.cs`, and a file in the root
of `Source/Parsek` reds the architecture suite.

| Module | Folder | Layer | Headless | What it holds |
|--------|--------|-------|----------|---------------|
| `RaceCore` | `Source/Parsek/RaceCore/` | model | yes | The vocabulary and every rule: manifests, the standing codec and chain, the witness log, slot and look assignment, the site table and launch-area predicate (over plain latitude and longitude), the ladder, milestone-table and board derivation (from neutral rows, below), the house rules, the public-state digest, the sync pipeline as a pure function over ports (15.3), the Space Race window's row model (dates as numbers; the window's text is formatted in `UI`), and the text of the race's screen messages (plain words and integers; `RaceWorld` posts them and may not name `UI`). No game types, no stores, nothing above the floor |
| `RaceExport` | `Source/Parsek/RaceExport/` | features | no | The one reader of the player's own model. `RaceExportSource` turns ERS, ELS, the vessel identities and names into one immutable `PublicState`: recordings with hashes and date ranges, the milestone reach rows (as neutral rows: id, date, recording id, tree id), the leftovers and their lifetimes, the disclosure facts. Beside it, `OwnPayloadReader`, the own half of the payload port (15.3), serves the payload files a standing names. Nothing else lives here |
| `RaceWorld` | `Source/Parsek/RaceWorld/` | features | no (runtime-coupled) | Everything live that is not a read of the own model: the race session for the loaded save, its facade and the sync driver, the adapters behind the folder, transport and clock ports and the rival-cache half of the payload port, the rival store, the rival side of the ghost lane (loader, policy, leftovers, flags, map presence), the four sites (space center move, terrain patches, scenery), looks |
| (existing) `UI` | `Source/Parsek/UI/` | presentation | no | `SpaceRaceWindowUI`, `RaceSetupDialog`, the Settings section; drawn from `RaceCore` row models |
| (existing) `Controllers` | `Source/Parsek/Controllers/` | entry | no | `ParsekRaceMenu.cs` (the main menu entry point), `ParsekRaceScenario.cs` (the race mark's scenario module), `ParsekRaceBoot.cs` (the one handler of scene load requests, 15.7), `ParsekRaceSetup.cs` (create, join and continue a race save, behind an interface `UI` declares, 15.4), and the composition of the rival lane in each scene controller (15.6). The folder places them; no `modules.toml` edit is needed |
| (existing) `Patches` | `Source/Parsek/Patches/` | entry | no | Guards that must sit on stock entry points (for example a launch from a rival's site, the stock suit picker and the "allow other launch sites" option, 7.3, 7.4) |
| (tooling) `TestCommands`, `InGameTests`, `UI/Gallery` | | | | Seam verbs for the harness lanes (`RaceSetup action=create\|join`, `RaceCommand action=status\|set-folder\|sync\|offline\|online`, `ListHandles kind=sites\|rivals\|board`, and one harness step that stages the shared folder), the in-game test categories, the window's gallery mock states |

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
| `IRaceTransport`: ask for files to be copied between the shared folder and the local race directory, ask for the listing of a shared directory (copy-out needs the folder's highest seq, import the list of standings) and for the proof of a shared file (to heal it), and report what has arrived | `RaceWorld`, the copy step of 15.9: a main-thread transport first, the copy worker if allowed. Both keep their own small copy routine, because `FileIOUtils` logs through `ParsekLog` and has no retry | an in-memory copy that completes at once, or on demand to test half-synced states |
| `IRaceClock`: real time in UTC | `RaceWorld`, system clock | a settable clock |
| `IPublicStateSource`: the player's public state as one immutable `PublicState` | `RaceExport`, `RaceExportSource` | a hand-built `PublicState` |
| `IRacePayload`: the content hash and the local path of a recording's files by id | split in two: the own half in `RaceExport`, reading the trajectory file through the save's sidecar versions (reading them is reading the own model) and the ghost and tree bytes from the exporter's own copies; the rival half in `RaceWorld`, over the rival cache | byte arrays |

The sync is then one pure call, `RaceSync.Run(ports, inputs)`, over one bundle of the ports
above (`SyncPorts`: the local folder, the transport, the clock, the public-state source and
the own half of the payload port) and the tick's plain inputs (`SyncInputs`). It returns a
`SyncResult`: what was exported, the import verdict per rival (`RivalStandingVerdict`), the
board, the messages to show, the marks. The shell, the sync driver `RaceSyncDriver` in
`RaceWorld`, runs it at the ticks of 7.5 and applies the result. Nothing inside the call
touches the game, and nothing inside it waits on the shared folder.

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
  show or hide a rival). Creating, joining and continuing a race save is entry-layer work
  (P10's create-and-start core, `GamePersistence`), so a `RaceWorld` call into it would be a
  backward edge: `UI` declares the setup interface (`IRaceSetupHost`, beside the dialog), a
  Controllers host implements it, and `ParsekRaceSetup` in Controllers does the work.
- `Controllers` and `Patches` reference the `RaceWorld` facade and `RaceExport`'s source,
  and compose them.

New `[forbidden]` edges in `scripts/arch/modules.toml`. They are written there as here,
with a list on the right side (`From -> A | B`): prerefactor P12 teaches both parsers the
list form, which expands to one edge per element, and the single-edge parser refuses a `|`,
so a list can never pass as one module named `A | B`. The map holds 2 edges today; this
block is 71.

```
Only the entry layer and the UI may name the race:
  Recording   -> RaceWorld | RaceExport | RaceCore
  GameActions -> RaceWorld | RaceExport | RaceCore
  Crew        -> RaceWorld | RaceExport | RaceCore
  Rewind, Missions, Logistics, SwitchIntent, Reaim, Timeline,
  Ghost, MapRender, Display, Rendering -> RaceWorld | RaceExport | RaceCore

The rules module names nothing above the floor (every module of every higher layer):
  RaceCore -> Recording | GameActions | Crew | Ghost | Rewind | Missions |
              Logistics | MapRender | SwitchIntent | Reaim | Rendering |
              Display | UI | Timeline | Controllers | Patches |
              RaceWorld | RaceExport

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
  `RaceCore` may not name. The pure id check moves to the floor (prerefactor P7); the two
  race formats (`raceLinkFormat` of the race mark, `raceFormat` of the exchange files) are
  registered in `SaveFormat.cs`, and `RaceCore` carries its own constants and classifier,
  which a test pins equal to the registered rows.
- The milestone table is a rule, so it stays in `RaceCore`, but it cannot read ledger rows.
  `RaceExport` hands it neutral rows; the rule orders them per ladder id, earliest first.
- The launch-area predicate takes plain coordinates, not a recording type.
- The later flight checks (7.15, step B) reuse the offline analyzer's rules, which are typed
  on `Recording` and live in a tooling module. (Four of them read only what the playback
  interface offers. None checks continuity, so a "teleport" check is a new rule, not a
  reused one.) Where they run (on the loader's private
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
- **Recording types only in the loader.** `RaceWorld` may reference the `Recording` module,
  and that module also holds the recorders and the recording stores. So: inside `RaceWorld`
  a type of that module is named only in the loader files, and no file of the three race
  modules names `FlightRecorder`, `BackgroundRecorder`, `RecordingStore`, the pending-tree
  slot or the tree commit and discard services. (`RaceExport` reads the own recordings
  through `EffectiveState` only.) The first half is an architecture-suite cell, not a grep:
  a grep cannot tell which module a type belongs to, so the cell reads the text scan's
  module map and a list of the loader files.

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
- **No segment events.** The part-event code casts the trajectory back to `Recording` in two
  places to read its segment events, and the only ones playback acts on are flag pick-ups,
  which remove real flag vessels and write records into the save. The first draft wanted
  those reads moved onto the interface (P8). That is dropped: the cast is what keeps a
  trajectory that is not a `Recording` away from those side effects, so it stays, and a test
  pins that a `RivalFlight` reaches neither. (The five other casts on the playback path, four
  in `GhostMapPresence` and one in the anchor resolver, are in code the rival lane does not
  run.)
- **No relative sections.** A stretch flown close to another vessel (a docking approach, a
  kerbal beside its lander) is stored relative to that vessel. Placing it means finding the
  other vessel's recording through about 2,200 lines that are typed on `Recording` and read
  chain and tree fields the playback interface does not have. Both recorders also keep a
  second, body-fixed copy beside such a stretch, the one single-player debris already plays
  from. The loader keeps only that copy and hands the engine plain body-fixed sections, so a
  rival flight never asks for an anchor and that code is not generalized. A stretch with
  fewer than two stored positions is not drawn. The cost is a possible small wobble between
  two rival ghosts that are docking. Spike S10 looks at it, and counts how many relative
  stretches in real saves carry a complete copy.

Rival ids live in their own space: every key the display code holds for a rival ghost is the
pair (player id, recording id), never a bare recording id and never a committed-list index.
One exception is accepted: a few static caches keyed by a bare recording id stay shared
across lanes (the tail-lift plan caches and their preview recording,
`SectionAnnotationStore`, `RenderSessionState`). That is safe because a rival trajectory
never reaches them (it is flattened and gets no tail lift), and a source gate on the rival
lane's files pins that it calls none of them.

### 15.6 The rival ghost lane

**Rival ghosts play on their own instance of the playback engine.** That much of the first
draft stands. What the technical review corrected is the claim that this is nearly free, and
the code inventory that followed (`docs/dev/research/multiplayer-base-refactoring-2026-11-14.md`)
measured by how much. The whole playback path reads the player's own stores about 120
times, but the part a second engine actually runs holds about 28 of those reads, in 8
lookup functions. The larger problem is a different one: the code that places ghosts keeps
its working state on the flight scene object, keyed by position in the player's own list,
so two lanes on one scene object would overwrite each other.

What was measured in the code:

| Piece | Today | What the rival lane needs |
|-------|-------|---------------------------|
| `GhostPlaybackEngine` | An instance; takes a list of `IPlaybackTrajectory`; in-game tests already build extra instances, but on the own positioner, so those engines share one lane and are no model for the rival lane. One store-reading function (the tree of a debris recording). It writes the global diagnostics state, and its spawn throttle is a field of the instance | The catalog for that one lookup, its own diagnostics sink, and a spawn budget shared with the own engine (P2a, P2c). The frame context carries no frame number, so the scene controller that hosts both engines resets the shared budget once per frame, before the first engine runs |
| `IPlaybackTrajectory` | 37 members and one setter: a position in the player's own list, written by the optimizer | The setter leaves the interface, so a rival flight can be immutable; the getter stays, because two engine readers need it, and a rival flight returns -1 (P2c) |
| `IGhostPositioner` and `ParsekFlightGhostPositioner` | The implementation is built on a `ParsekFlight` host. Four store lookups, and about 60 references to 14 host members: the orbit cache (keyed by list position), the ghost position entries the late update re-applies, log-once sets, the watch state, the loop units | The catalog (P2a) and a per-lane state object in place of the host fields (P2b). The interface stays in the entry layer, where the scene controllers compose the lane |
| Relative-section anchors (`RelativeAnchorResolver`, about 2,200 lines) | Typed on `Recording` and `RecordingTree`; reads chain and tree fields the interface does not have | Not used by the rival lane: a rival flight's relative stretches are flattened at load (15.5) |
| `GhostMapPresence` | A static class of about 16,600 lines with 36 static collections, 11 of them keyed by committed index. The part that creates a map vessel is about 400 lines, already typed on the interface | Not made per-lane. Rivals get a small map presence of their own on the extracted primitive (P3) |
| `WatchModeController` | Identity is a committed index: 16 committed-list read lines in the controller, and about 40 outside reads of the watched index in 15 files; needs a ghost that is live at the current game date | Nothing in the first build: the Watch camera does not follow a rival flight. When it does, a lane-qualified target (P3b), and rivals are watchable only while live (7.7) |
| `ParsekKSC*.cs` | The Space Center scene does not use the engine: it has its own slots and a single-ghost path typed on `Recording` | Nothing in the first build: that scene shows rival scenery only (7.7) |
| `Source/Parsek/Controllers/ParsekTrackingStation.cs` | No engine either: `GhostMapPresence` plus markers keyed by committed index | Rival map vessels through `RivalMapPresence`; no marker for a rival ghost inside an atmosphere (open: question Q-23, section 13) |
| `ParsekFlightPlaybackHost`, `ParsekPlaybackPolicy` | Drive and police the own engine; read the own stores | Not reused: the rival engine gets its own small host and policy |

The design that follows:

- **A catalog port.** One small interface, `IPlaybackCatalog`
  (`Source/Parsek/Recording/IPlaybackCatalog.cs`): find a recording by id, find the tree that
  holds an id, the pending tree. Its answers are `Recording` and `RecordingTree`, so it lives
  in the `Recording` module, below every consumer, not on the floor (a floor port naming
  model types would be a backward reference). The tree lookups of today are variants, not
  copies (pending first, committed first, a tree-id match, a flat-list fallback, the list
  only); the port names each variant and each caller keeps its own. The own lane's catalog
  (`RecordingPlaybackCatalog.Own`) is a thin wrapper over the stores, with no behavior
  change. The rival lane gets an empty catalog of the `Recording` module,
  `RecordingPlaybackCatalog.None`, which answers "none" to every lookup: the engine asks it
  only for a debris parent, which a rival flight never has. The rival store is not a
  catalog, and there is no rival catalog type, so no rival file names a `Recording` type
  outside the loader. After this the engine and the positioner do not name a store at all,
  which is also a cleaner engine for single player (P2a).
- **Lane state.** The orbit cache, the ghost position entries, the log-once sets, the watch
  query and the loop-unit lookup move from fields of the flight scene object into one
  per-lane object that the positioner and the late-update helpers are handed. The own lane's
  object holds exactly what the fields hold today. The loop units themselves stay on the own
  playback host; a rival lane has none. This is the largest prerefactor (P2b), and the one
  the first drafts did not see.
- **Composition at the entry layer.** The scene controllers already build the own engine
  and its positioner. In a race save they build a second engine and a second positioner with
  the empty catalog and the rival lane's state, and the rival data and policy they get from
  the `RaceRuntime` facade. Both engines read one per-frame spawn budget
  (`GhostSpawnFrameBudget`), which the scene controller builds, hands to both and resets
  once per frame before the first engine runs. `RaceWorld` supplies data and policy; it does
  not construct the engine, so it never has to name an entry-layer type.
- **A second instance has its own slot index space**, so the committed-list index contract
  (`Source/Parsek/Recording/RecordingStore.CommittedListNotifications.cs`) is untouched and
  no rival ghost can sit at a committed index. The rival list is replaced only at scene load
  (7.6), so the rival engine needs no mid-list insert or remove notifications at all.
- **`RivalPlaybackPolicy`** receives the rival engine's lifecycle events. It has no spawn
  path, no resource deltas, no chain logic, no crew and no CommNet: it only decides
  visibility (the show toggle, launch areas); once the Watch camera follows rival flights, it
  also feeds the watch camera. The spawn machinery is simply not wired to this engine. That
  is the structural form of "rivals are only seen".
- **Leftovers are entries of the rival engine held at their last moment**: the flight's ghost
  kept at its last pose inside its leftover window. This needs no engine mode: the engine
  already holds the last pose when a trajectory's end lies past its last point, and plays an
  orbit tail after the last point, so the loader widens the flight's end to the end of its
  window and appends that tail. A leftover with no end date ends at a finite named horizon,
  never infinity. Not a separate static presenter, as the first draft had it: an object
  standing on a body still has to follow the floating origin, an object in orbit moves, and
  a separate presenter would sit outside the ghost budget and the distance rules.
- **A small rival map presence** (`RivalMapPresence`) for orbit lines, map icons, targeting
  and the Tracking Station: it uses the same primitive own ghosts use (a ghost map vessel,
  registered in the guard set every vessel walk already checks) through a small extracted
  service, with its own stores keyed by (player id, recording id). Lane-qualifying the
  existing `GhostMapPresence` would touch most of its 16,600 lines for a lane that never
  spawns, never chains and never loops (prerefactor P3 is the extraction of that primitive).
  Three patch sites assume a guarded map vessel belongs to the player's own timeline (the
  "will materialize" message, the map watch menu, the Tracking Station selection); they get
  a neutral "whose ghost is this" query in the same change. What follows from sharing the
  primitive:
  - The guard set records which lane registered each map vessel, and teardown is per owner:
    an own scene cleanup or rewind removes the own map vessels only (today one call clears
    the whole set, which would unregister live rival vessels).
  - The strip at save stays one call, which removes every registered ghost map vessel,
    whatever its lane.
  - The primitive builds orbiting map vessels only; landed leftovers in map view and the
    Tracking Station need a landed variant of it, added with the rival map presence.
  - The own map presence keeps its creation log line and its `GhostCreated` render trace;
    the rival map presence writes its own creation line and trace.
  - `GhostMapPresence` keeps its own spawn throttle, outside the engines' spawn budget; the
    rival map presence has a throttle of its own.
- **Flags** go through `RivalFlags` (plaque replicas, 7.7).
- **Neutral seams for what the ghost code must not know.** The ghost and map code may not
  name the race, yet rival ghosts need a color on their label, icon and orbit line, an owner
  name, a visibility filter and a share of the ghost budget. Each is a neutral value on the
  floor that the lane is constructed with: a style per trajectory (`PlaybackTrajectoryStyle`,
  color and label prefix; null means no style), a visibility flag, and one ghost budget
  object both engines draw from, own ghosts first (`PlaybackGhostBudget`, addition A5; it
  counts live ghosts and is not the per-frame spawn budget above). The visibility flag
  exists (the per-trajectory skip flag the own host already passes). The other two are new
  concepts, not extractions: map icons are drawn in a fixed grey today, Parsek never sets a
  ghost's orbit line color, and there is no ghost-count cap at all. The map marker renderer
  sits in Controllers, so a marker's color is handed to it by the scene controller.
- Each scene controller reaches all of this through one facade, `RaceRuntime`, with two
  scene calls: scene ready (build the rival store from the local rival cache) and scene exit
  (drop it). The per-frame work is the rival lane's own tick, which the scene controller
  calls after the own engine's; the sync is not a facade call but the sync driver's tick
  (`RaceSyncDriver.Tick`, 15.3), run at the ticks of 7.5. In a single-player save the facade
  is absent and each call is one null check.

The sizes and the order of the prerefactors are in 15.11.

### 15.7 State and lifetimes

| State | Where | Lifetime | Rule |
|-------|-------|----------|------|
| `RaceLink` | Its own scenario node in the save (6.2) | the save | Holds only identity and choices that never change (race id, player id, site, table ids, date unit), and its own format number `raceLinkFormat` (the `race-link` row of `SaveFormat.cs`). So every load kind (cold, quickload, rewind, Re-Fly, revert) can take it from the loaded save with no special case; `LoadReconcilePolicy` gets one row saying so. The gate reads it from the game's scenario node list, which exists before any scene is built |
| `RaceSession` | memory | one scene of the loaded save | Built by the race scenario module's load in each scene, from `RaceLink` and `local.cfg`, and dropped in its `OnDestroy`; its one static, `RaceSession.Current`, is tagged `scene` in `scripts/arch/lifetimes.toml` |
| The child of the race scenario node: `RACE_FACTS` (whether test tooling ran, `testTooling`, and the debug-menu completions, `DEBUG_COMPLETION` rows) | the race scenario node, through the `IRaceScenarioChild` hook | the save | It rolls back with the save on every load; it has its own `LoadReconcilePolicy` row (every load kind takes the loaded save's copy), and a child the build does not know is kept verbatim. No suit state is kept: the suit is locked to the look and every load dresses the roster (7.4) |
| Rival store | memory | the loaded save, rebuilt each scene | Replaced whole, never edited in place |
| `RaceLocal`: folder location, display name and look, show toggles, last export digest, time of the last sync, whether the first import after joining is done; local copies of `race.cfg` and of the own `player.cfg` | `Race/local.cfg`, `Race/race.cfg` and `Race/own/player.cfg` beside the save | the machine | Not in the save, so no load rolls a setting back; missing file means "ask for the folder again" and the name and look are read back from `Race/own/player.cfg`; the Settings folder change rebuilds the shared `race.cfg` and `player.cfg` from the local copies |
| Rival cache, witness log, own mirror | `Race/` beside the save | until deleted | Never named by the save, never synced. Checked in the inventory: no save-folder sweep walks unknown children of `Parsek/`, Parsek's own file collector walks only its recordings, rewind points and saves folders, and a race save has a footprint from birth so no pre-Parsek backup is taken. Three sweeps do take the whole folder and get the change of addition A7: `collect-logs.py`, the test-batch crash reconcile, and the in-game test runner's batch snapshot and restore |
| Applied sites (moved space center, terrain patches, scenery) | Kerbin's own objects | the process, across saves | The one piece of state that outlives a save: `RaceSites` owns apply and restore as a pair. The one handler of scene load requests, `ParsekRaceBoot.OnSceneRequest`, calls `RaceSites.Reconcile` on every request, the main menu included, against the race mark of the game being loaded (spike S4a checks that the game is known there; if it is not, the handler moves as a whole, with the race gate, to the loaded game's post-load event or a prefix on `Game.Start`, 7.3): so a load from inside a game (stock's load menu, the harness's `LoadGame`), a race-to-race load and a return to the main menu all apply or restore, and a race save's sites are applied before its first scene; an in-game test pins stock Kerbin after leaving a race save by each route (7.3 lists what must be reset) |
| Exchange files | shared folder | until pruned by their writer (6.3) | `raceFormat` is registered in `SaveFormat.cs` together with `raceLinkFormat` (rows `race-exchange` and `race-link`), both at 1. The golden gate's frozen folders are saves, so the first golden folder is a race save holding the mark and the local files (`race.cfg`, `player.cfg`, `local.cfg`), written when the two rows are registered, since `race.cfg` exists from the first phase; a later golden folder adds a standing, its payload and an own mirror at the same format numbers (a new folder needs no bump). Unknown blocks in a newer standing are ignored, the additive rule of the save-format policy |

### 15.8 Inert outside a race

Every entry point of the feature sits behind one predicate, `RaceGate.IsRaceSave`, read from
the race mark. In a single-player save no race object is constructed, no site is applied,
no poll runs and no file is touched. The predicate composes with Parsek's own inert switch
(`ParsekGameModeGate`, in `Patches`, which `RaceCore` and `RaceWorld` may not name): the
Controllers host passes the inert flag in, and an inert Parsek means no race, with the race
mark kept verbatim. The replacement suit sets of 7.4 follow the same rule as working
reading: Parsek applies them only to race kerbals and rival ghosts, and they are not listed
in the stock suit picker, which would show them in every single-player save (the race
suit choice is blocked, 7.4); whether that listing is allowed is still to be asked
(open: question Q-19, section 13; it depends on spike S3 showing the applied suit in EVA,
IVA and the portrait).
The proof is mechanical: the existing unit suite, the
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

- **The game never touches the shared folder from a Unity callback**, with the one
  exception below. Every read the game does (at load, at a sync tick, when a ghost needs its
  file) is from the local race directory beside the save.
- **One copy worker moves bytes between the shared folder and the local race directory**,
  in both directions, and computes the hash of what it copied. It parses nothing, formats
  nothing, logs through a queue the main thread drains, and never touches a game object. It
  keeps its own small copy routine (`FileIOUtils` logs through `ParsekLog` and has no
  retry). It is one allowlisted site in the background-thread gate, and the main thread
  talks to it only through `IRaceTransport` (ask for a copy, a listing or a proof, read what
  has arrived).
- **The setup dialog is the one place that touches the shared folder from a button.**
  Validate, Create, Join and the Settings folder change read and write it directly, through
  the same `IRaceTransport` port on its main-thread adapter. Until the transport exists (R3),
  the dialog uses a smaller shared-folder port with the same main-thread adapter, which the
  transport then absorbs.

This is an exception to a standing repository rule, so it needs the owner's yes (open:
question Q-18, section 13). Spike S8 (15.11) measures the stall on the common sync programs
first, so the decision is taken on numbers, and S8 runs last, after the race works end to
end on a plain shared folder. Until then the race is built in the fallback form, the first
draft's budgeted main-thread steps:

- one operation per frame with a byte budget, and retries across frames;
- a time budget per poll: once it is spent, no further operation starts in that poll, and
  what is left waits for the next poll;
- no import and no copying during a flight (an export in flight writes the own mirror only
  and copies out after the flight), so every read of the shared folder happens outside
  flight;
- only files whose names are exactly the ones the race expects are read; anything else in a
  player's folder, a sync program's conflict copies included, is ignored;
- a file the sync program marks as a cloud placeholder is never skipped for good, because
  only opening it makes the program download it: at most one placeholder is opened per
  poll, at an import outside flight, with the time the open took logged, and the rest wait
  for the next poll.

The known risk of this form is the freeze of that one placeholder open, which can last
seconds, at the Space Center or in the Tracking Station. The copy worker and the tuning of
the poll numbers wait on S8; until then the design's default poll numbers hold.
`IRaceTransport` is the seam that makes either form a local change.

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

### 15.11 Prerefactors, additions and extra spikes

Measured in `docs/dev/research/multiplayer-base-refactoring-2026-11-14.md`,
which holds the evidence (file and line) for every row. Sizes: S is under about 15 call
sites; M is 15 to 60 sites, or a change to saved bytes or to a per-frame path. Nothing on
the list is larger.

**Prerefactors.** Changes to single-player code. Each lands on its own, before the feature,
with no behavior change, proven by the existing suites and lanes.

| Id | Change | Size | Why |
|----|--------|------|-----|
| P1 | One source for "where is the working space center": a `HomeSpaceCenter` value in `Source/Parsek/Core/HomeSpaceCenter.cs` (pad, runway threshold, and a version for caches to key on) that returns the stock numbers, read by the KSC circles in place of the four constants in `SpawnCollisionDetector` (the constants stay as aliases of the stock numbers); one pure launch-area shape test (`LaunchAreaGeometry.Contains` in `Source/Parsek/Core/LaunchArea.cs`: circle or strip, longitude wrapped, taking over the wrapped distance base Parsek already computes) that single player's 50 m rule calls; the launch-site circles `LaunchSiteExclusionZones` caches for the process (the stock launch sites, possibly the KSC's own pad and runway, which the cache's `runtime=N` log line settles) are dropped when the home version changes and at the main menu. The runway end, the strip half-width, the method a race sets the value through, and the version key of `LaunchSiteOnlyGhost`'s cache (today keyed on the store's version only) come with the sites phase and the measured numbers of S4, because a member with no production caller may not land (15.14) | M | The home site moves in a race save; launch areas (7.3, 7.7) |
| P2a | The catalog port: `IPlaybackCatalog` in the `Recording` module (`Source/Parsek/Recording/IPlaybackCatalog.cs`, with the own-lane `RecordingPlaybackCatalog` beside it): find a recording by id, find the tree holding an id, the pending tree, each lookup a named variant. The variants of the tree lookup and the by-id lookups in the engine, the positioner, the anchor resolvers, the flight anchor context builder and the map state vectors call it, each keeping its search order. The empty catalog `RecordingPlaybackCatalog.None` lands with the rival lane, its first caller | M | The rival lane (15.6) |
| P2b | Lane state: the orbit cache, the ghost position entries, the log-once sets, the watch query and the loop-unit lookup move from `ParsekFlight` fields into one per-lane object (`Source/Parsek/Controllers/ParsekFlightPlaybackLane.cs`) handed to the positioner (about 60 references to 14 members) and to the late-update helpers of `ParsekFlightPlaybackHost` | M | Two lanes in one flight scene (15.6). The largest one |
| P2c | `LoopSyncParentIdx` loses its setter on `IPlaybackTrajectory` (the getter stays; `Recording` keeps its setter for the optimizer); the engine gets its own diagnostics sink (`Source/Parsek/Ghost/GhostPlaybackDiagnosticsSink.cs`) and a per-frame spawn budget, `GhostSpawnFrameBudget` (`Source/Parsek/Ghost/GhostSpawnFrameBudget.cs`), that two engines can share; an engine resets only a budget it owns | S | An immutable `RivalFlight` (15.5); two engines in one scene |
| P3 | Extract the ghost-map-vessel primitive (build, register in the guard set, load, remove; about 400 lines) from `GhostMapPresence` into a small service, `GhostMapVessels` (`Source/Parsek/Ghost/GhostMapVessels.cs`), that records which lane owns each registered vessel, so teardown is per owner; a neutral "whose ghost is this" query for the three patch sites that assume an own-timeline ghost. The strip at save stays one call for every lane, and the creation log line and render trace stay in `GhostMapPresence` | M | Rival map presence (15.6) |
| P3b | A lane-qualified watch target (lane, index, id) in `WatchModeController` (16 committed-list read lines in the controller; about 40 outside reads in 15 files stay as they are) | M | Watching a rival flight (7.7). Deferred: the Watch camera on a rival flight is not in the first build, and P3b comes with it |
| P4 | (dropped) Options on the flag replay spawn | | Rival flags are plaque replicas, not flag vessels (7.7) |
| P5 | (now addition A4) | | |
| P6 | Let the trajectory codec decode into a plain value (`Source/Parsek/Recording/RecordingTrajectoryPayload.cs`), with `Recording` as one consumer, and give the trajectory load from a given path a production entry (today the trajectory load exists only under test-hook names; the snapshot codec already has a production entry, and the whole-recording loader needs none, because a `RivalFlight` never loads a `Recording`). The writer is not touched | M | `RivalFlight` (15.5) |
| P7 | Move the pure recording-id check to the floor (`Source/Parsek/Core/SafeFileId.cs`) with a fixed character set, today's Windows set, so nothing changes on Windows (`RecordingPaths.ValidateRecordingId` keeps calling it); a save-root accessor on `RecordingPaths`, whose one user is the file collector | S | The standing codec validates ids without naming `Recording` (15.4). The race does not use the accessor: `RaceWorld` names `Recording` types only in its loader, so it computes the local race directory itself, from `KSPUtil.ApplicationRootPath` and `HighLogic.SaveFolder` |
| P8 | (dropped) Segment events through `IPlaybackTrajectory` | | The cast it would remove is a safety (15.5) |
| P9 | (now addition A5) | | |
| P10 | Lift the create-and-start-a-save core out of `TestCommands` into `Source/Parsek/LogIO/NewSaveStarter.cs` (the floor), in separate steps (delete, create, start), so a caller can work on the created game before it starts; the test verb and the race setup (`ParsekRaceSetup`, Controllers) both call it. The seam's load-and-start route, the only production-quality one, later moves beside it for Continue | S | The setup dialog's Create (7.2), which adds the race mark and writes the save between create and start, because the Space Center re-reads `persistent.sfs`; the harness verb for it |
| P11 | Harness specs stop pinning the window count (100 `windows=11` literals in 25 specs): spec files are read raw by at least five loaders, so a spec writes `windows=\d+` and the harness library's test cells hold the derivation, the count equal to the window table; the two near-identical `ParsekUI` constructors share one body | S | Adding one window |
| P12 | `[forbidden]` accepts a list on the right side (`From -> A \| B`) in both parsers, expanded to one edge per element; the single-edge parser refuses a `\|`, so an unexpanded list cannot pass as one module name | S | 15.4 |

Order: P12 and P11 (tooling only); then P7, P1 and P10 (independent of each other); then
P2c and P2a; then P2b, as one uninterrupted window of small changes (a tooling change to
`scripts/refactor/il_compare.py` so it can fold a renamed field, then the code changes,
each proven by it; CI does not run those tooling tests, so they run locally) with one
flight proof at the end: a representative set of 18 lanes, one per way the positioner
places a ghost, flown with the daily tier; the full run of every lane happens once, at
release. P3 shares no file with P2b: it starts at once, takes its second step after P2a's
map change, and runs beside P2c, P2a and P2b. P3b waits with the Watch camera on rival
flights. P6 shares no file with the others and writes no byte differently, so it is built
in parallel and merged when green, but never inside the P2b window, so that the flight
proof flies one known set of changes (open: question Q-06, section 13). P2a, P7, P10 and
P11 are worth doing for single player alone. The two single-player launch pad defects that
P1 would have touched (a stored longitude wound past the date line, launch sites read
before they were built) are fixed in base Parsek outside this feature, so P1 changes no
behavior.

**Additions.** Facts and seams base Parsek does not have today. Each is additive (no save
format bump), inert outside a race, and lands with the phase that first uses it.

| Id | Addition | Size | For |
|----|----------|------|-----|
| A1 | One pure query beside `CommittedMilestoneReach`: the reach rows per milestone id over the effective ledger, earliest first (the first is the one the once-ever walk credits), each with its date and flight id | S | The milestone table (7.8) |
| A2 | One pure "stands from date A until date B" query composing the existing spawnable-leaf, vessel-identity, ghost-chain and recovery checks, plus a recorded fact when a committed flight's vessel is deleted or destroyed outside a recording (today nothing records that), stored as one additive ledger action type (`VesselEnded`), so it follows the effective ledger, tombstones and deduplication like a recovery row | M | Leftovers (7.7) |
| A3 | The first-use date (UT) of each tool on the recording (gravity hack, cheat teleport, cheat currency), so a tool first used after a date does not reach back to it | S to M | Disclosure (7.12) |
| A4 | Build options on the ghost kerbal build: a part-name substitute before the part lookup, a material override after the materials are cloned | S to M | Looks on ghost kerbals (7.4) |
| A5 | Neutral ghost seams on the floor: a per-trajectory style, `PlaybackTrajectoryStyle` (`Source/Parsek/Core/PlaybackTrajectoryStyle.cs`; color and label prefix, a sealed class, null means no style), read at about 6 sites, built in base Parsek right after P2c with its sites and the engine arguments; and one ghost budget object, `PlaybackGhostBudget` (`Source/Parsek/Core/PlaybackGhostBudget.cs`, a count of live ghosts, not P2c's per-frame spawn budget), with the rival lane | S each | Color and the shared soft cap (15.6) |
| A6 | Window chrome shared by Parsek's table windows (id, rectangle, input lock, resize, UI scale, hover strip, Close) | S | The Space Race window (7.13) |
| A7 | `collect-logs.py` lists `Parsek/Race/` and does not copy it; the test-batch crash reconcile and the in-game test runner's batch snapshot and restore leave it alone (Parsek's own file collector never walks it); one shared "find the scenario node of a game before its scenarios load" helper (`Source/Parsek/LogIO/ScenarioNodeLookup.cs`) in place of nine production copies (six over a scenario list, three over a GAME node; a tooling copy stays) | S | The race mark's gate (7.1); other players' files stay out of log bundles |

Spikes beyond S1b and S2 to S7 of section 12 (S1, the terrain survey of 7.3, is done):

- **S8**: measure what a first open of a not-yet-downloaded file, and an open on an offline
  network share, cost on the common sync clients (Dropbox, Google Drive, OneDrive), to settle
  15.9. It runs last, after the race works end to end on a plain shared folder; which sync
  programs it covers is asked when it is about to run (open: question Q-03, section 13).
  The copy worker and the tuning of the poll numbers wait on it.
- **S9**: which stock debug-menu actions (set orbit, set position, add science, unlock
  technology) can be observed by a mod, to settle the list of 7.12; and which milestone
  completions made from the debug menu (Max Progression, the console) a mod can see, since
  such a completion does not score.
- **S10**: play a recorded docking, and a kerbal walking beside a lander, from the
  body-fixed copy of their relative stretches alone, and look at them; and count, over the
  saves on the owner's PC, how many relative stretches carry a complete copy. Settles the
  flattening of 15.5.
- **S11**: the frame cost of ghosts at race scale, per ghost in flight and map view, and the
  main-thread time of the first lazy open of a trajectory file, to set the cap of the ghost
  budget and the numbers of 15.13.
- **S12** (optional): whether SHA-256 and a signing algorithm work inside KSP's Mono, and how
  fast, for signed standings later (section 12, "Noticing edited files").
- **S13**: the close-out, which folds the spike results into this document (S8's result is
  written with S8's own report, since S8 runs last).

### 15.12 Code layout

Every file sits in its module's folder, `Source/Parsek/<Module>/<Name>.cs`. A file in an
existing module needs no registration; a new module registers with its first file (15.14).

| Concept | Code |
|---------|------|
| Race mark and gate | `Source/Parsek/RaceCore/RaceLink.cs`, `Source/Parsek/RaceCore/RaceGate.cs`, `Source/Parsek/RaceCore/RaceScenarioChild.cs` (`IRaceScenarioChild`, the hook for the scenario node's children); `Source/Parsek/Controllers/ParsekRaceScenario.cs`, `Source/Parsek/Controllers/ParsekRaceBoot.cs` (the one scene request handler) |
| Manifests, standing and its codec, chain, witness log | `Source/Parsek/RaceCore/RaceManifest.cs`, `Source/Parsek/RaceCore/Standing.cs`, `Source/Parsek/RaceCore/StandingCodec.cs`, `Source/Parsek/RaceCore/WitnessLog.cs`, `Source/Parsek/RaceCore/OwnMirror.cs` (the chain is checked in the sync's witness stage and over the own mirror) |
| Slots and looks assignment | `Source/Parsek/RaceCore/RaceSlots.cs` |
| Site table and launch areas | `Source/Parsek/RaceCore/SiteTable.cs`; each site row carries its launch areas (`SiteRow.LaunchAreas`), tested through `LaunchAreaGeometry.Contains` in `Source/Parsek/Core/LaunchArea.cs` (P1) |
| Public state, digest, sync pipeline, ports | `Source/Parsek/RaceCore/PublicState.cs`, `Source/Parsek/RaceCore/PublicStateDigest.cs`, `Source/Parsek/RaceCore/RaceSync.cs` (one partial file per stage), `Source/Parsek/RaceCore/SyncPorts.cs`, `Source/Parsek/RaceCore/RacePorts.cs` (`IRaceFolder`), `Source/Parsek/RaceCore/IRaceTransport.cs`, `Source/Parsek/RaceCore/IRacePayload.cs`, `Source/Parsek/RaceCore/IPublicStateSource.cs` |
| Milestone table, ladder, board, house rules, checks | `Source/Parsek/RaceCore/MilestoneTable.cs`, `Source/Parsek/RaceCore/Ladder.cs`, `Source/Parsek/RaceCore/RaceBoard.cs`, `Source/Parsek/RaceCore/HouseRules.cs`, `Source/Parsek/RaceCore/ClaimChecks.cs` |
| Window row model, message text | `Source/Parsek/RaceCore/RaceBoardPresentation.cs`, `Source/Parsek/RaceCore/RaceMessages.cs` |
| The one reader of the own model | `Source/Parsek/RaceExport/RaceExportSource.cs`; the own half of the payload port, `Source/Parsek/RaceExport/OwnPayloadReader.cs` |
| Session, facade, sync driver | `Source/Parsek/RaceWorld/RaceSession.cs`, `Source/Parsek/RaceWorld/RaceRuntime.cs`, `Source/Parsek/RaceWorld/RaceSyncDriver.cs` |
| Adapters | `Source/Parsek/RaceWorld/RaceFolder.cs`, `Source/Parsek/RaceWorld/RaceMainThreadTransport.cs` (the copy step on the main thread, built first), `Source/Parsek/RaceWorld/RaceCopyWorker.cs` (the copy worker, if the owner allows it, 15.9), and the rival-cache half of the payload port, in `RaceWorld` (its file name is a proposal: `Source/Parsek/RaceWorld/RacePayloadFiles.cs`) |
| Rival store, flights, lane | `Source/Parsek/RaceWorld/RivalStore.cs`, `Source/Parsek/RaceWorld/RivalFlight.cs`, `Source/Parsek/RaceWorld/RivalFlightLoader.cs` (the loader), `Source/Parsek/RaceWorld/RivalPlaybackPolicy.cs`, `Source/Parsek/RaceWorld/RivalMapPresence.cs`, `Source/Parsek/RaceWorld/RivalFlags.cs`; the rival lane's empty catalog is `RecordingPlaybackCatalog.None` in `Source/Parsek/Recording/RecordingPlaybackCatalog.cs`; the composition in the scene controllers is `Source/Parsek/Controllers/ParsekFlightRivalLane.cs` and `Source/Parsek/Controllers/ParsekTrackingStationRivalLane.cs` |
| Sites | `Source/Parsek/RaceWorld/RaceSites.cs` (space center move, patches), `Source/Parsek/RaceWorld/RaceScenery.cs` |
| Looks | `Source/Parsek/RaceWorld/RaceLooks.cs` (dressing the roster); the block of the suit choice in the stock suit picker in `Patches`, reading one predicate for its greyed state and its click-block (7.4). The replacement suits are recolored in memory from the player's own base suit, so no suit files ship (open: question Q-20, section 13), applied only to race kerbals and rival ghosts and not listed in the stock suit picker of single-player saves (open: question Q-19, section 13). Only if shipped art is needed instead: suit sets under `GameData/Parsek/Suits/`, which the release zip, the dev deploy and the harness deploy each list by name, so all three lists gain them |
| Setup | `Source/Parsek/Controllers/ParsekRaceSetup.cs` (create, join, continue), behind `IRaceSetupHost`, declared in `Source/Parsek/UI/RaceSetupDialog.cs`; it calls the create-and-start core `Source/Parsek/LogIO/NewSaveStarter.cs` (P10) |
| Menu entry, setup dialog, window | `Source/Parsek/Controllers/ParsekRaceMenu.cs`, `Source/Parsek/UI/RaceSetupDialog.cs`, `Source/Parsek/UI/SpaceRaceWindowUI.cs` (new drawing on the shared window chrome of A6, registered in `ParsekWindowSet`) |
| Flight checks (later) | `ClaimChecks` entries; where the analyzer's rules run is decided with step B (15.4) |

### 15.13 Cost: what the feature may spend

Ghost visuals must be "correct visually, minimal, and efficient" (the standing design
principle), and a race adds up to three more players' worth of ghosts. The budget, measured
in spikes S5 (scenery) and S11 (ghosts per frame, the first lazy open) and pinned where it
can be:

| Cost | Rule |
|------|------|
| Scene load | The rival store is built from the standings and the rival tree files, which are small cfg files (end position, terminal orbit, endpoint body, terminal state, the debris flag are there, not in the standing); a rival's flags come from the standing's `FLAGS` block. A flight's trajectory file is opened when its ghost is first needed (7.6). Building the store for three rivals with hundreds of flights each must not be noticeable; a unit test pins that no `.prec` or `.craft` file is opened by the build |
| Per frame | Rival ghosts and leftovers draw from the same ghost budget as own ghosts, own first, leftovers last (15.6). A hidden rival costs nothing |
| A sync tick on the main thread | A trajectory file's hash is taken from the sidecar version the save already recorded, so it is not re-hashed; a recording whose sidecar epoch is still pending waits for the next save. A ghost payload is always a stripped copy of the ghost snapshot (no crew), so it is hashed once, at export. The board is a table of at most four players by about a hundred milestones. The no-retakes rule and the chain check read the kept standings (6.3), not the whole history |
| During a flight | No polling, no import. One export after a commit: the public state is built in memory and the file copy is the copy step's work (15.9); while the copy step runs on the main thread, an export in flight writes the own mirror only and the copy-out waits until after the flight |
| Scenery | Three building sets, always fully built, measured in spike S5 for load time, memory and frame cost at each site; a copy far from the camera is not built |
| Disk | The rival cache holds what each rival's applied standing names plus the kept standings; the shared folder holds, per player, the kept standings and the payload they name (6.3) |

### 15.14 Existing gates the feature has to satisfy

The 1.1 build guards its structure with tests that a new feature trips unless it registers
itself. Named here so the phase plans carry them, not discover them:

- `scripts/arch/modules.toml`: the folder is the module, so a new file in an existing module
  (`Source/Parsek/Controllers/ParsekRaceMenu.cs`, `ParsekRaceScenario.cs`, `ParsekRaceBoot.cs`,
  `ParsekRaceSetup.cs`) is placed by its folder with no edit. Each race module registers in
  the commit that creates its first file, never ahead of it, because a rule whose folder is
  not on disk, a module with no file and a `[forbidden]` edge that names an undeclared module
  each red the architecture suite. A registration is: the `[[module]]` rule (`name`,
  `folder`); the module's layer in `[layers.modules]`; its line in `scripts/arch/atlas.toml`;
  `RaceCore` in `[headless.modules]` with its reason line, `RaceWorld` in
  `[size] runtimeCoupled`; the `[forbidden]` edges of 15.4 whose two ends exist by then,
  written in P12's list form; and the re-pins the registration moves (headless, layer and
  knot pins). So the 71 edges arrive in three parts: 29 with `RaceCore`, 21 with
  `RaceWorld`, 20 with `RaceExport`, and the edge `RaceCore -> RaceWorld` with whichever of
  the first two lands second.
- A new file in `Core` (`HomeSpaceCenter`, `LaunchArea`, `SafeFileId`, the A5 types) also
  moves the architecture suite's count of `Core` files (`test_phase_two_catch_all_count`)
  and its kernel list (`test_kernel_files_resolve_to_core`), in the same commit.
- The "statics written by another production type" ratchet: the home space center value of
  P1 is set by `RaceWorld`, so it is set through a method of its own type, never as a static
  field another type writes; the same holds for every static of the feature.
- The uncalled-method ratchet: a production method that only tests call counts as uncalled,
  and that count may not rise. So `RaceCore` does not land ahead of its shell as a pure
  library: each piece arrives with a thin production caller in the same change.
- `scripts/arch/lifetimes.toml` and `SaveScopedSessionResetTests`: every mutable static of
  the feature, with the applied-sites state and the home space center value tagged for the
  process, the session and the rival lane's per-scene state tagged for the scene, and the
  rest for the loaded save. The cold-load roots are `ParsekScenario`'s and `ParsekSettings`'
  load methods, so a second scenario module's statics read as never cleared:
  `ParsekRaceScenario` keeps instance state only, or its `OnLoad` joins the roots.
- `LoadReconcilePolicy`: the dialog's Continue is a load through a declared initiator
  (`KnownLoadInitiators`, a table keyed by file that gates `GamePersistence.LoadGame(` calls
  only); the only production-quality load-and-start route today is the command seam's, so it
  moves beside the create-and-start core of P10 and both the seam and Continue call it. The
  race mark gets its row in the load-kind table (it is taken from the loaded save by every
  kind), and so does each child of the race scenario node (15.7). Creating and starting a
  new save is not a load and that gate does not see it; it goes through the one
  create-and-start core of P10.
- `Source/Parsek/Recording/SaveFormat.cs` and `SaveFormatGoldenGateTests`: two rows,
  `race-link` (`raceLinkFormat`) and `race-exchange` (`raceFormat`), registered together. The
  gate's frozen folders are saves, so the golden folder that covers them is a race save
  holding the mark and the local files, and a later folder adds a standing and its payload at
  the same numbers (15.7).
- The background-thread gate: one allowlisted site, if the owner allows the copy worker
  (15.9; open: question Q-18, section 13).
- The ERS / ELS routing gate: `RaceExportSource` reads through `EffectiveState`, so it needs
  no exemption.
- The GUI gates: the new window in `design-gui-inventory.md`, the window and tab
  vocabularies `GuiCensusSeamVerbTests` pins, the harness window count (P11), its gallery
  mock states, and the UI-scale source gate; about 17 places in all. It gets no `UiSurface`
  key: that table only says Basic or Advanced, and "race saves only" stays the `RaceGate`
  predicate. Two things are firsts for the mod: something drawn at the main menu, and a
  dialog with typed fields (7.2). Built from stock uGUI (a cloned main menu button, a stock
  `PopupDialog`) they follow the stock UI scale and need no UI-scale host of their own; only
  an IMGUI fallback would need one. That reading holds if spike S6 shows the stock scale
  applies to both. The Settings
  window opens 280 wide, too narrow for a folder path, so its Multiplayer section shows the
  path shortened, with the whole path in the hover and a button that opens the setup
  dialog's field to change it.
- The stock-control annotation rules: the two race blocks on stock controls (the suit choice
  of the suit picker, 7.4, and the "allow other launch sites" option, 7.3) are clickable
  kinds, so each ships its
  greyed state, its reason text and its click-block together, reading one predicate, as the
  existing stock-screen blocks do (`StockUiDecorationQuery`), and the GUI mirror's pairing
  check covers them.
- The save-folder sweeps, `collect-logs.py` (it copies every `Parsek/<dir>` beside a save;
  `Race/` holds other players' files and should be listed, not copied whole), the
  test-batch crash reconcile (it deletes and restores a save's whole `Parsek/` folder), the
  in-game test runner's batch snapshot and restore (all three are addition A7; Parsek's own
  file collector never walks `Race/`) and the pre-Parsek backup (a race save is created by
  Parsek, so it has a footprint from birth and no backup is taken).
- The harness gates the first race commits meet: the OnSave / OnLoad characterization
  goldens, the harness library's per-verb tables and their source-sync cells (the new seam
  verbs of 15.2), the regenerated lane catalog for each new in-game category, the growth
  rule of the coverage registry, the pinned batch tallies of the specs that host a race
  category, the list of never-flown specs (`INTERIM_PIN_IDS`) and the multi-category lane
  groups.

---

## References

- `docs/dev/done/design-coop-async-multiplayer.md` - the deprecated co-op design; its sections
  4.1, 6.3, 7.1 and 13 are the ancestry of the exchange here.
- `docs/roadmap.md` Phase 15.
- `docs/parsek-game-actions-and-resources-recorder-design.md` - milestone rows and the
  once-ever walk the milestone table reads.
- `docs/dev/design-load-restores-the-save.md` - the load contracts a race save keeps.
- `docs/dev/research/multiplayer-base-refactoring-2026-11-14.md` - the measured inventory of
  the 1.1 code behind the prerefactors and additions of 15.11.
- `docs/dev/design-save-format-policy.md` - what a build keeps of data it does not know, and
  why the race mark is a scenario node of its own (6.2).
