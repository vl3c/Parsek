# Design: ParsekTestCommands (M-A2 command seam)

Module M-A2 of the automated-testing initiative (`docs/dev/automated-testing-plan.md`
sections 3.1, 3.3, 11b). This is the "new data that persists" full-workflow design
doc the plan mandates for the command-file format. Implements the v1 command subset
only; the wire protocol is designed to carry the phase 3+ commands without a format
break.

---

## Problem

The automated-testing harness needs to drive a running KSP + Parsek instance from an
external Python orchestrator: boot the instance into a chosen save from the main menu,
change settings, start and commit recordings on purpose, run the in-game test batch, mark
mission checkpoints, and quit in a commit-safe way.
There is no control surface today. The in-game test runner is interactive-only
(`Ctrl+Shift+T`, `TestRunnerShortcut.Update`), and the plan's audit confirmed "no env
hooks exist anywhere in Source/Parsek". kRPC could drive some of this, but linking a
kRPC service against Parsek.dll entangles GPL and does not cover Parsek-specific
actions (commit, discard, rewind-invoke, merge-dialog answers) that kRPC never exposes.

We need a control channel that: needs no kRPC linkage (works headless-of-kRPC, no GPL
entanglement), is inert and unshippable in normal play, survives a mid-run KSP crash
without re-running a non-idempotent command, is human-readable and grep-able for
debugging, and whose parse/validate/dispatch logic is pure and xUnit-testable without
Unity.

## Terminology

- **Orchestrator**: the external Python process (M-A5 harness) that writes commands and
  reads responses. It is the ONLY writer of the command file and the ONLY reader of the
  response file.
- **Addon**: the in-game DDOL `MonoBehaviour` (`ParsekTestCommandAddon`) that polls the
  command file, executes commands on the Unity main thread, and appends responses. Runs
  only in the automation instance, only when env-gated on.
- **Command**: one line in the command file. Carries a unique **command id**, a **verb**
  (the action), and zero or more `key=value` **args**.
- **Verdict**: the terminal outcome the addon writes for a command id: `OK`, `ERROR`,
  `REJECTED`, `TIMEOUT`, or `INTERRUPTED`.
- **Journal**: an append-only write-ahead log (`.journal` file) recording per-command-id
  lifecycle phases (`CLAIMED` / `EXECUTED` / `DONE`). It is the durable at-most-once
  mechanism; it is NOT the response file.
- **Safe point**: a main-thread moment at which a command may execute: not the `LOADING`
  scene, not during a scene transition, scene has settled, and no in-game test batch is
  running. Each verb additionally declares a scene/state precondition.
- **Pump**: the per-frame step (in the addon's `Update`) that reads new command lines,
  makes a dispatch decision for the head command, and executes or defers it.
- **Reserved verb**: a phase 3+ command name the v1 parser recognizes by name but does
  not implement. It is rejected with a distinct reason so the orchestrator can probe
  capability, rather than being confused with a typo.

## Mental Model

```
  external orchestrator (Python)                 automation KSP instance
  -----------------------------                  -----------------------
  append command lines  ---------------------->  parsek-test-commands.txt
      (id, verb, args)                                     |
                                                           v  Update() pump (main thread)
                                              +--------------------------------+
                                              | 1. read new whole lines        |
                                              | 2. ParseLine  (pure)           |
                                              | 3. DecideDispatch (pure):      |
                                              |      Execute | Defer | Reject  |
                                              |      Interrupted (from journal)|
                                              | 4. journal CLAIMED  (WAL)      |
                                              | 5. run side effect (Unity)     |
                                              | 6. journal EXECUTED            |
                                              | 7. append response line        |
                                              | 8. journal DONE                |
                                              +--------------------------------+
                                                           |
  read/tail response lines <-------------------  parsek-test-responses.txt
      (id, verdict, payload)                     parsek-test-commands.journal (WAL)
                                                 parsek-test-commands.lock (session)
```

Strict FIFO: the pump only looks at the oldest not-yet-terminal command. If that command
must defer (its scene/state is not ready), later commands wait behind it until it runs or
times out. The orchestrator controls ordering by controlling append order.

At-most-once via a three-phase journal: execution happens ONLY on the transition from
"no journal entry for this id" to `CLAIMED` -> `EXECUTED`. If a crash leaves an id at
`CLAIMED` (side effect maybe-partially ran), the addon does NOT re-execute; it reports
`INTERRUPTED`. If a crash leaves an id at `EXECUTED` (side effect definitely ran, response
maybe not written), the addon does NOT re-execute; it just (re)writes the response and
marks `DONE`. A non-idempotent command therefore runs zero-or-one times, never twice.

## Data Model

All files live in the **KSP root** (`KSPUtil.ApplicationRootPath`), NOT next to the save.

### File location decision and justification

The four channel files sit at the KSP root with fixed names:

| File | Writer | Reader | Semantics |
|---|---|---|---|
| `parsek-test-commands.txt` | orchestrator | addon | append-only command log |
| `parsek-test-responses.txt` | addon | orchestrator | append-only verdict log |
| `parsek-test-commands.journal` | addon | addon (on restart) | append-only WAL |
| `parsek-test-commands.lock` | addon | addon | single-session guard |

KSP root, not save-scoped (`saves/<save>/...`), because:

1. **Discovery timing.** `RecordingPaths.TryGetSaveContext` resolves the save folder from
   `HighLogic.SaveFolder`, which is empty at the main menu and during early boot. The
   first commands an orchestrator sends (pin settings, or the `LoadGame` boot command)
   arrive before any save is loaded, so a save-scoped path cannot be resolved yet.
2. **FlushAndQuit from menu scenes.** FlushAndQuit must work with no game loaded (quit
   from the main menu). A save-scoped channel would be unreachable there.
3. **One channel per instance.** The orchestrator drives a single dedicated automation
   instance and selects the save via the `LoadGame` boot command (the boot channel;
   kRPC/TestingTools RPCs are not available at the main menu). A single fixed root-level
   channel is simpler than tracking which save is active.
4. **Multiple saves** are handled by the orchestrator sequencing load/quit, not by
   per-save files; the channel is per-instance, not per-save.

`KSPUtil.ApplicationRootPath` is always available (it is used unconditionally in
`RecordingPaths.TryGetSaveContext` before any save context), so the channel exists from
process start.

### Command line grammar

One command per line, newline (`\n`) terminated. Tokens are whitespace-separated
`key=value` pairs. Two keys are reserved and required first: `id` then `cmd`. Remaining
tokens are verb-specific args in any order. Example:

```
id=0001 cmd=SetSetting name=autoRecordOnLaunch value=false
id=0002 cmd=StartRecording
id=0003 cmd=RecordingState
id=0004 cmd=CommitTree
id=0005 cmd=RunTests category=RecordingInvariants
id=0006 cmd=LoadGame save=DefaultCareer name=persistent
id=0007 cmd=MissionMark label=mun%20landing%20start
id=0008 cmd=FlushAndQuit
```

Grammar rules:

- `id`: orchestrator-generated token, unique across the whole run (monotonic integer or
  GUID). It is the ack correlation key and the journal dedup key. Ids must be unique
  across process restarts within one logical run; the orchestrator either uses
  monotonic-across-run ids or truncates the three channel files between runs (see
  Backward Compatibility). The id MUST be encoding-stable: because it is embedded VERBATIM
  (not re-encoded) into journal and response lines, a decoded id that would itself need
  percent-encoding is rejected at parse time (`REJECTED msg=malformed-id`). This closes two
  holes: a decoded space (`id=a%20b` -> "a b") would journal-tokenize to a shorter id "a" (a
  re-execution-after-restart mismatch), and a decoded newline (`id=a%0Ab`) would forge a
  journal line. In practice monotonic integers and GUIDs are always encoding-stable.
- `cmd`: the verb. Case-sensitive, exact match against the known-verb table. Like the id it
  must be encoding-stable (it is written raw into the `CLAIMED` journal line); an
  encoding-unsafe verb is `REJECTED msg=malformed-verb`.
- Optional `v=<n>`: protocol version, default `1`. Unknown keys are ignored (forward
  compatibility), so phase-3 verbs may add new keys freely.
- **Value encoding**: a value that contains whitespace, `=`, `%`, or a control character
  MUST be percent-encoded (`%20` for space, `%25` for `%`, `%3D` for `=`). The parser
  percent-decodes values. This keeps a value with spaces (a `MissionMark` label, a path)
  on one token. Numeric values use `InvariantCulture` (no locale commas).
- A line that does not end in `\n` is a partial write; the pump leaves it for the next
  poll and does not parse it.
- Blank lines and lines beginning with `#` are ignored (comments).

`ParsedCommand` (pure result of `TestCommandParser.ParseLine`):

```
struct ParsedCommand
    string     RawLine
    int        LineNumber          // 1-based position in the file
    string     Id                  // null if the id token was absent
    string     Verb                // null if the cmd token was absent
    Dictionary<string,string> Args // decoded values
    bool       ParseOk
    string     ParseError          // reason string when ParseOk == false
```

### Response line grammar

One line per terminal outcome, appended by the addon, newline terminated:

```
id=0002 cmd=StartRecording verdict=OK seq=2 ut=1234.5 recordingId=abc123
id=0004 cmd=CommitTree verdict=ERROR seq=4 ut=1240.0 msg=no-active-tree
id=0003 cmd=RecordingState verdict=OK seq=3 ut=1235.1 recording=true tree=abc123 points=812
id=0009 cmd=SetSetting verdict=REJECTED seq=9 msg=setting-not-whitelisted%20name%3Dfoo
id=0005 cmd=RunTests verdict=OK seq=5 ut=1300.0 passed=42 failed=0 skipped=3 results=parsek-test-results.txt
```

Fields: `id`, `cmd`, `verdict`, `seq` (monotonic per-process response counter, so the
orchestrator can order and detect gaps), `ut` (`Planetarium.GetUniversalTime()` when a
game is loaded, omitted otherwise), then verb-specific payload keys, then an optional
`msg` (percent-encoded free text; carries the reason for `ERROR`/`REJECTED`/`TIMEOUT`).
Values are percent-encoded and `InvariantCulture`-formatted, matching the command grammar
so one reader handles both.

**seq caveat (per-process, resets on restart).** `seq` is a per-PROCESS counter: after a
crash the restarted process starts `seq` from its base again, so a `seq` value the
orchestrator sees may be a GAP (a response it missed) OR a RESET (a fresh process
recounting). The orchestrator must not treat non-monotonic `seq` across a restart boundary
as a lost response; it correlates outcomes by command `id` (stable across restarts) and
uses `seq` only for ordering WITHIN one process run. A journal `session` change (or a
`LoadGame`/restart it initiated) marks the boundary where `seq` may legitimately reset.

**Orchestrator response contract (crash-recovery duplicates).** The addon normally writes
exactly one terminal response line per command id, but a crash AFTER the response append
and BEFORE the journal `DONE` leaves the id at `EXECUTED`; on restart the addon rewrites
the response line from `EXECUTED` (it cannot know the first append survived). The
orchestrator therefore treats the FIRST terminal response line for a given id as
authoritative and IGNORES any later duplicate line for that same id -- later duplicates are
crash-recovery rewrites, not new outcomes. (A rewritten line is byte-equivalent in verdict,
payload, AND msg -- the recovery re-emits the ORIGINAL terminal outcome stored on the
enriched `EXECUTED` journal line, see the Journal grammar -- only `seq` and `ut` differ, since
`seq` is a fresh per-process counter and `ut` is re-sampled at recovery time.)

### Journal line grammar (WAL)

Append-only, newline terminated. One line per phase transition:

```
id=0002 phase=CLAIMED seq=2 verb=StartRecording session=7f3a... t=17390512.884
id=0002 phase=EXECUTED seq=2 t=17390512.951 verdict=OK payload=recordingId%3Dabc123 msg=
id=0002 phase=DONE seq=2 verdict=OK t=17390512.960
```

`session` is `ParsekProcess.ProcessSessionId` (the AppDomain-lifetime GUID) so a journal
can be attributed to a specific process run. `t` is wall-clock seconds (InvariantCulture).

The `EXECUTED` line is ENRICHED: it stores the terminal `verdict`, the whole verb-specific
`payload`, and any `msg` so a crash-recovery replay at `EXECUTED` can re-emit the ORIGINAL
response line (true RewriteResponse), not a synthetic ack. The `payload` value is the
space-joined `key=encodedValue` list percent-encoded ONCE MORE so the whole list rides as a
single wire token (`payload=recordingId%3Dabc123`); an empty payload / msg is written as an
empty token (`payload=` / `msg=`). Because ids and verbs are encoding-stable (rejected at
parse time otherwise, see the command grammar), the id/verb embed verbatim and only the
payload/msg/verdict fields are encoded. On addon startup the journal is replayed into an
in-memory map `id -> highest phase seen`:

- id at `DONE`  -> already fully handled; skip (no re-execute, no re-respond).
- id at `EXECUTED` (no `DONE`) -> side effect ran; skip execution, re-emit the ORIGINAL
  terminal response from the stored `verdict`/`payload`/`msg` (byte-equivalent to the
  pre-crash response modulo `seq`/`ut`), append `DONE`. This recovers a crash or an IO
  failure between side effect and response. A pre-enrichment / torn `EXECUTED` line with no
  stored verdict falls back to an `INTERRUPTED msg=recovered-executed` ack.
- id at `CLAIMED` (no `EXECUTED`) -> crashed mid-execution; do NOT re-execute; write an
  `INTERRUPTED msg=interrupted-claimed` terminal response and append `DONE`.

Journal replay tolerates a TORN TRAILING LINE: a crash mid-append can leave a final journal
line without a terminating `\n`; replay ignores any trailing line that is not
newline-terminated (it was never durably committed), exactly as the orchestrator ignores a
torn trailing response line. Only whole, newline-terminated journal lines drive the phase
map.

**Durability threat model.** The journal's durability guarantee TARGET is
append-visible-on-process-kill: a newline-terminated line that was flushed before the KSP
process was killed (crash, `Application.Quit`, orchestrator SIGKILL) is visible on the next
process's replay. That is the scope the at-most-once mechanism defends. Full power loss /
OS crash (unflushed OS page cache lost) is explicitly OUT OF SCOPE: the automation instance
runs on a dev PC under a scheduled task, not a datacenter with fsync guarantees, and a
power-loss mid-append is treated like any torn trailing line (ignored) with no stronger
promise. We do not fsync every journal append.

### Lock file grammar

Single line written once on addon startup after the env gate passes:

```
session=7f3a... pid=48213 t=17390510.101 root=C:\...\KSP
```

Used only to detect two automation instances pointed at the same KSP root
(`DecideLockOwnership` pure predicate). A lock whose `session` matches our own
`ProcessSessionId` is ours (reclaim). For a foreign lock, ownership is decided by PID
LIVENESS, not by `t`: the addon probes whether `pid` is still a live process
(`Process.GetProcessById` succeeds and, where cheaply checkable, the process path matches a
KSP instance). A live foreign pid means another instance owns the channel; we stand down
(log Warn, do not consume). A dead foreign pid means the previous owner crashed; the lock is
reclaimed with a Warn. `t` is NOT the staleness primary -- it is a tie-break / log detail
only, because `t` is written once at startup and never refreshed, so a long-lived healthy
instance would look "stale" by `t` while a just-crashed instance would look "fresh". PID
liveness is the correct liveness signal; `t` is retained purely for human-readable logging
and as a last-resort tie-break when a PID cannot be probed.

### SetSetting whitelist

`SetSetting` may mutate ONLY the fields in an explicit allowlist, each mapped to a typed
parse-and-range check. There is no reflective "set any field": the dispatcher switches on
the whitelisted name and calls a typed setter, so the command is data, never code.

The **persistence route** column is load-bearing. 8 of the 16 whitelisted settings are
NOT authoritatively persisted through `GameParameters.CustomParameterNode`: for
`writeReadableSidecarMirrors`, `showCommittedFutureOverlays`, `blockCommittedActions`,
`showRouteLines`, `autoBackupExistingSaves`, `ghostRenderTracing`, `mapRenderTracing`,
and `ledgerTracing`, the `ParsekSettingsPersistence` sidecar
(`GameData/Parsek/settings.cfg`) is authoritative -- `ParsekScenario.OnLoad` calls
`ParsekSettingsPersistence.ApplyTo(ParsekSettings.Current)`, which OVERWRITES the
`GameParameters` values on EVERY save load. A dispatcher that only mutated the live field
would see its change silently reverted at the next save load (including a load driven by
the new `LoadGame` verb). So the dispatcher routes those tracked names through the
`ParsekSettingsPersistence.Record*` path (mirroring `UI/SettingsWindowUI.cs`, which does
the same), not through the `GameParameters` field alone.

| name | type | validation | field on `ParsekSettings` | persistence route |
|---|---|---|---|---|
| `autoRecordOnLaunch` | bool | true/false | `autoRecordOnLaunch` | GameParameters |
| `autoRecordOnEva` | bool | true/false | `autoRecordOnEva` | GameParameters |
| `autoRecordOnFirstModificationAfterSwitch` | bool | true/false | same field | GameParameters |
| `autoMerge` | bool | true/false | `autoMerge` | GameParameters |
| `verboseLogging` | bool | true/false | `verboseLogging` | GameParameters |
| `ghostRenderTracing` | bool | true/false | `ghostRenderTracing` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `mapRenderTracing` | bool | true/false | `mapRenderTracing` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `ledgerTracing` | bool | true/false | `ledgerTracing` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `writeReadableSidecarMirrors` | bool | true/false | `writeReadableSidecarMirrors` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `autoBackupExistingSaves` | bool | true/false | `autoBackupExistingSaves` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `showCommittedFutureOverlays` | bool | true/false | `showCommittedFutureOverlays` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `blockCommittedActions` | bool | true/false | `blockCommittedActions` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `showRouteLines` | bool | true/false | `showRouteLines` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `samplingDensity` | int | 0..2 | `samplingDensity` | GameParameters |
| `ghostAudioVolume` | float | 0.0..1.0 (InvariantCulture) | `ghostAudioVolume` | GameParameters |
| `transitedBodyRotationModeIndex` | int | 0..2 | `transitedBodyRotationModeIndex` | GameParameters |

An unknown name -> `REJECTED msg=setting-not-whitelisted`. A value that fails the typed
parse or range -> `REJECTED msg=setting-value-invalid`. The setter mutates the live
`ParsekSettings.Current` and, for a GameParameters-only setting, persists through the
existing `GameParameters.CustomParameterNode` save path on the next game save (which
`FlushAndQuit` forces), so no new serialization is added. For the 8
`ParsekSettingsPersistence`-authoritative settings, the setter ALSO calls the matching
`ParsekSettingsPersistence.Record*` so the value survives the next `ApplyTo` at save load;
without this, the tracing flags an automation run pins would silently revert at the next
save load, including a `LoadGame`.

### Known-verb table (v1 vs reserved)

- **Implemented (v1)**: `SetSetting`, `StartRecording`, `StopRecording`, `CommitTree`,
  `DiscardTree`, `RecordingState`, `RunTests`, `LoadGame`, `MissionMark`, `FlushAndQuit`.
- **Reserved (recognized, `REJECTED msg=not-implemented-v1`)**: `StartLoopPlayback`,
  `StopPlayback`, `EnterWatchMode`, `InvokeRewind`, `AnswerMergeDialog`, `KscAction`,
  `SealSlot`, `StashSlot`, `FlySlot`, `RouteCommand`, `MissionConfig`, `TimeJump`,
  `SimulateStockSwitchClick`, `CrashAfterJournalPhase`, `RunInvariantReport`.
- **Anything else**: `REJECTED msg=unknown-command`.

Reserving the phase-3 names now means the envelope (id/cmd/args, percent-encoding,
journal, verdicts) is designed once and the later commands slot in without a format break.

> Update (M-C1): the four verbs `InvokeRewind`, `AnswerMergeDialog`, `TimeJump`, and
> `KscAction` have since been promoted from Reserved to implemented; see
> `design-autotest-seam-verbs-c1.md`. The v1 contract above is kept as the historical record.

## Behavior

### Addon lifecycle

`ParsekTestCommandAddon` mirrors `TestRunnerShortcut`: `[KSPAddon(KSPAddon.Startup.Instantly, true)]`
with `DontDestroyOnLoad(gameObject)` in `Awake`, a singleton guard (destroy the duplicate),
and per-frame work in `Update`. It subscribes `GameEvents.onGameSceneLoadRequested` to
set a `sceneTransitioning` flag and a small settle counter (cleared a couple of frames
into the new scene), exactly the scene-scoped-state pattern `TestRunnerShortcut` uses for
its input lock.

`Awake` env gate: if `Environment.GetEnvironmentVariable("PARSEK_TEST_COMMANDS") != "1"`
(exact match, fail-closed), the addon logs one Verbose line and disables itself (no
polling, no file access). Only the literal `1` arms it.

On first armed frame: acquire/inspect the lock (`DecideLockOwnership`), replay the journal
into the in-memory phase map, reconcile any `CLAIMED`/`EXECUTED` leftovers (crash
recovery, above), and seed the in-memory processed-id set + command-file byte offset.

### The pump (per `Update`, main thread)

1. If not armed, return. If `HighLogic.LoadedScene == LOADING`, `sceneTransitioning`, or
   the settle counter > 0, return (no safe point).
2. If an in-game test batch is running, return - do not execute other commands mid-batch.
   The gate is an OR of BOTH runners that can own a batch: the runner the addon owns for
   `RunTests`, AND the interactive Ctrl+Shift+T runner (via
   `TestRunnerShortcut.ActiveRunnerForGating`). Both share the campaign-isolation baseline
   machinery, so a command overlapping either batch could corrupt the save under test; the
   pure `DecideDispatch` re-checks this via `DispatchState.BatchRunning`.
3. Read any whole new lines appended since the last byte offset. Parse each into the
   pending FIFO queue (skipping ids already terminal per the journal / processed set). The
   addon opens the command file for reading with `FileShare.ReadWrite` so the external
   orchestrator (which holds its own share-all append handle) can keep appending while the
   addon reads; neither side takes an exclusive lock on the command file.
4. Look at the head command only. Build a `DispatchState` snapshot from Unity
   (scene, `HighLogic.CurrentGame != null`, `ParsekSettings.Current != null`, recorder
   recording, `activeTree != null`, transitioning, batch running, journal phase for this
   id) and call the pure `DecideDispatch(parsed, state)`:
   - `Reject(reason)`: write terminal `REJECTED`, journal `CLAIMED`+`EXECUTED`+`DONE`
     (no side effect), advance.
   - `Interrupted`: write terminal `INTERRUPTED`, journal `DONE`, advance (crash recovery).
   - `Defer(reason)`: leave at head; if it has exceeded its per-command deferral budget,
     convert to `TIMEOUT` terminal and advance; else return and retry next frame.
   - `Execute`: journal `CLAIMED`, run the verb handler (below), journal `EXECUTED`, write
     the terminal response, journal `DONE`, advance. `RunTests` and `FlushAndQuit` are
     multi-frame/terminal-process and handled specially (below).

Per-poll logging follows the batch-counting convention (one summary line: lines read, N
parsed, N deferred), with bounded per-command Info lines (command counts are small).

### Verb handlers and their preconditions

| Verb | Scene/state precondition | Action | Success payload |
|---|---|---|---|
| `SetSetting` | game loaded (`ParsekSettings.Current != null`), any scene; else Defer | typed whitelist setter mutates `ParsekSettings.Current` | `name`, `value` echoed |
| `StartRecording` | FLIGHT with a loaded, unpacked active vessel, not restoring/re-fly/merge-journal; else Defer | `ParsekFlight.StartRecording(...)`, then RE-SAMPLE `HasLiveRecorderForTagging()`; a refusal (vessel not ready / packed / guard blocked) is `ERROR msg=start-refused`, never a false OK (F4) | `recordingId`, `already=true` if a recorder was live |
| `StopRecording` | FLIGHT; else Defer | `ParsekFlight.StopRecording()` (idempotent: OK with `idle=true` if no recorder) | `stopped` bool |
| `CommitTree` | FLIGHT with `activeTree != null`; if no tree -> `ERROR msg=no-active-tree` (mirrors `CommitTreeFlight`'s guard) | `ParsekFlight.CommitTreeFlight()` | `committed=true` |
| `DiscardTree` | FLIGHT; if no active tree -> OK `nothing=true` | stop recorder if live, then `ParsekFlight.AutoDiscardActiveTreeWithMessage(reason, screenMessage, ledgerRecalcReason)` (the wrong-context-caller entry point) with test-command-specific strings | `discarded` bool |
| `RecordingState` | any scene (read-only) | snapshot recorder/tree state (reuses `ParsekLog.FormatRecState` inputs) | `recording`, `tree` (the `RecordingTree.Id` of the active tree, empty when none - adjudication B), `points`, `scene` |
| `RunTests` | any scene the runner supports; else Defer | `InGameTestRunner.RunAll()` (no `category`) or `RunCategory(category)`; response deferred until `IsRunning` goes true->false and `ExportResultsFile` ran | `passed`, `failed`, `skipped`, `results=parsek-test-results.txt` |
| `LoadGame` | any scene incl. MAINMENU (the BOOT CHANNEL); Reject if a recorder is live (`msg=recording-active`) or a load is already in flight (`msg=load-in-flight`) | long-running two-phase (like `RunTests`): journal `CLAIMED` -> initiate load (`HighLogic.SaveFolder = dir`; `GamePersistence.LoadGame(...)`; `FlightDriver.StartAndFocusVessel(...)` - the same Assembly-CSharp-only sequence as v0.5.4 `TestingTools.LoadSave`, no kRPC types); response deferred until the new scene settles (pure `TestCommandLoadGame.DecideLoadCompletion`): a settled FLIGHT scene with `HighLogic.CurrentGame != null` -> journal `EXECUTED` + terminal `OK`; a settle-back to MAINMENU -> `ERROR msg=load-failed-returned-to-menu` (a failed flight boot, e.g. an NRE in `FlightDriver.Start` on an incompatible save); the LoadGame budget expiring -> `ERROR msg=load-timeout`. A null / incompatible game detected up front (before two-phase) is still `ERROR msg=load-failed` | `scene`, `save` |
| `MissionMark` | any scene | emit a stable `[Parsek][Info][TestCommands] MISSIONMARK label=<label> ut=<ut>` log line (H3-style correlation) | `label` echoed |
| `FlushAndQuit` | any scene (incl. menus) | if a game is loaded, force a scenario/game save so committed data is durable, THEN `Application.Quit()` deferred one frame; response + journal `DONE` written and flushed BEFORE quitting. Deliberately replaces kRPC master's `Quit()` RPC (a bare `Application.Quit()`, not commit-safe). | `saved` bool |

Notes:
- `StartRecording` when already recording relies on `FlightRecorder`/`ParsekFlight`
  guards (`CanStartRecorderWithActiveTreeHead`); the handler reports `OK already=true`
  rather than forcing a second recorder.
- `DiscardTree` entry point: there is no whole-tree UI "Discard" button to reuse, so the
  handler calls `ParsekFlight.AutoDiscardActiveTreeWithMessage(reason, screenMessage,
  ledgerRecalcReason)`, which exists precisely for wrong-context callers and lets the
  command supply test-command-specific strings. It deliberately does NOT call
  `AutoDiscardIdleActiveTree`, which hardcodes an "idle on pad" screen toast and the wrong
  ledger-recalc reason for a scripted discard.
- `RunTests` and `LoadGame` are the two long-running verbs: each is `CLAIMED` + started,
  then the pump gates all other commands until the operation settles. For `RunTests`, when
  the batch finishes and results export, the handler reads `Passed/Failed/Skipped`, writes
  the terminal response, and marks `EXECUTED`+`DONE`. A crash mid-batch leaves it `CLAIMED`
  -> reported `INTERRUPTED` on restart (the batch's own `TestBatchMarker` crash-reconcile
  handles the save side).
- `LoadGame` is the BOOT CHANNEL: it is the seam's way to boot the automation instance into
  a specific save without any kRPC linkage (kRPC RPCs are not available at the main menu).
  Its `save=<folder> name=<file>` values are percent-encoded like every other arg. It
  executes at `MAINMENU` because the pump's safe-point gate permits any non-`LOADING`,
  settled scene, and `LoadGame` declares no FLIGHT/game precondition. It is two-phase like
  `RunTests`: `CLAIMED` when the load is kicked off, then the pump defers all other commands
  until the new scene settles with `HighLogic.CurrentGame != null`, at which point the
  handler writes the terminal response (`scene=`, `save=`) and marks `EXECUTED`+`DONE`. A
  crash mid-load leaves it `CLAIMED` -> reported `INTERRUPTED` on restart (the journal file
  survives the scene load). Mid-flight, `LoadGame` is Rejected while a recorder is live
  (`recording-active`); the orchestrator must `CommitTree` / `DiscardTree` first, so the
  verb never silently discards an in-flight recording.
- `FlushAndQuit` does NOT auto-commit an in-flight recording. Committing is done only by
  explicit `CommitTree` or a real scene-exit; a bare quit from flight has never persisted
  a live uncommitted recorder, and the command preserves that. To keep an in-flight
  recording, the orchestrator sends `CommitTree` before `FlushAndQuit`.

### Deferral budgets (per-command TIMEOUT)

Each command carries a deferral budget: the maximum wall-clock time it may sit at the head
of the queue in `Defer` before the pump converts it to a `TIMEOUT` terminal and advances
(so a never-satisfiable command never wedges the run). The DEFAULT budget is 60 s
wall-clock. Some verbs need a different bound and override the default:

| Verb | Deferral budget | Rationale |
|---|---|---|
| (default) | 60 s | covers ordinary scene-settle / game-loaded waits |
| `StartRecording` | scene-wait budget | may wait for FLIGHT with an unpacked active vessel; sized to the scene-arrival wait rather than a fixed 60 s |
| `RunTests` | batch budget from the scenario spec | a full in-game batch can run minutes; the budget comes from the scenario's declared runtime budget, not a fixed default |
| `LoadGame` | load budget (e.g. 300 s) | a cold `GamePersistence.LoadGame` + scene settle can take minutes on a large save; longer than the default, shorter than an infinite hang |

Budgets are measured from when the command first reaches the head and begins deferring. On
expiry the pump writes `TIMEOUT` with `msg` carrying the last defer reason and advances.

### Reserved / phase-3 forward map (design only, not implemented)

The reserved verbs are recognized so the envelope is stable. Two carry
implementation notes for when they land, because they touch documented traps:

- `AnswerMergeDialog(choice)`: the merge dialog's buttons run their action IN the
  `DialogGUIButton` callback (`MergeDialog.ShowTreeDialog` wires `MergeCommit` /
  `MergeDiscard` directly inside the button lambdas). Per the project's deferred-field
  PopupDialog callback trap, `AnswerMergeDialog` must locate the live popup by
  `MergeDialog.DialogName` ("ParsekMerge") and invoke the chosen button's action
  directly, NOT set a `pendingChoice` field that some `DrawWindow` reads later. NOTE:
  `MergeDialog.DialogName` is a PRIVATE `const` today; the phase-3 implementation of
  `AnswerMergeDialog` must internal-ize `DialogName` (make it `internal const`) so the
  addon can look the live popup up by name. This is a phase-3 change, not a v1 one.
- `InvokeRewind(rpId)`: must surface the `RewindInvoker.CanInvoke(rp, out reason)` gate
  (scene invokable, no pending invocation, not corrupted, quicksave present on disk, no
  active re-fly marker, deep-parse precondition) in the response `msg` when it declines,
  so the orchestrator sees WHY a rewind was refused rather than a bare failure.

## Edge Cases

Exhaustive. Each: scenario -> expected behavior -> v1 or deferred.

1. **Command file appears mid-scene-transition.** Pump is gated off during
   `sceneTransitioning` and the settle counter; commands are read but the head defers
   until the scene settles. -> Deferred-until-safe. v1.
2. **Malformed line (unparseable kv, garbage).** `ParseLine` returns `ParseOk=false`;
   terminal `REJECTED msg=malformed`, journaled through to `DONE`, pump advances. If an
   `id` token was present it is used; otherwise the response uses `id=line#<n>` so the
   orchestrator can correlate by position. v1.
3. **Line missing `id`.** Treated as malformed; response `id=line#<n>
   verdict=REJECTED msg=missing-id`. v1.
4. **Line missing `cmd`.** `REJECTED msg=missing-cmd` under the real id. v1.
5. **Unknown command name.** `REJECTED msg=unknown-command`. v1.
6. **Reserved (phase-3) command in a v1 addon.** `REJECTED msg=not-implemented-v1`
   (distinct from unknown-command so the orchestrator can detect capability). v1.
7. **Duplicate id in the command file.** The journal/processed-set already has the id;
   the second occurrence is skipped with a Warn and NO second response line (one response
   per id). v1.
8. **KSP crashes after the side effect but before the response is written.** Journal is at
   `EXECUTED`. On restart the addon does NOT re-execute; it writes the response and marks
   `DONE`. At-most-once preserved (ran exactly once). v1.
9. **KSP crashes after `CLAIMED` but before/partway through the side effect.** Journal at
   `CLAIMED`. Addon does NOT re-execute; writes `INTERRUPTED`. The command ran zero or a
   partial number of times; the orchestrator treats `INTERRUPTED` as "unknown outcome" and
   reconciles (e.g. sends `RecordingState`). At-most-once preserved (never twice). v1.
10. **Response file append fails (locked by the orchestrator's reader).** The guarded
    append retries with bounded backoff; the journal is only marked `DONE` after the line
    lands, and the side effect is guarded by `EXECUTED`, so a persistent failure leaves the
    id at `EXECUTED` and the response is (re)written next frame or next restart WITHOUT
    re-executing. An ack is never silently dropped. v1.
11. **Two KSP instances sharing the same KSP root/files.** The lock file records
    `ProcessSessionId` and `pid`. A second instance decides ownership by probing the
    recorded `pid`'s liveness: a live foreign pid -> stand down (Warn, does not consume); a
    dead foreign pid (previous owner crashed) -> reclaim with a Warn. `t` is not used as the
    staleness bound. This configuration is out of scope by design (the plan uses one
    dedicated automation instance) but is detected, not silently double-executed, and a
    crashed owner does not wedge a fresh run. v1.
12. **FlushAndQuit during active recording.** FlushAndQuit forces a save of committed
    state then quits; the in-flight uncommitted recorder is discarded by design (a bare
    quit never persisted one). Orchestrator sends `CommitTree` first to keep it. v1
    (behavior documented, not a bug).
13. **StartRecording when already recording.** Underlying guards prevent a second
    recorder; handler reports `OK already=true`. v1.
14. **CommitTree with no active tree.** `ERROR msg=no-active-tree`, mirroring
    `CommitTreeFlight`'s existing "No active tree to commit" guard. v1.
15. **DiscardTree with no active tree.** `OK nothing=true` (idempotent no-op). v1.
16. **SetSetting mid-recording.** Allowed. Live-read settings (e.g. `samplingDensity`
    thresholds, tracing flags) take effect immediately for subsequent samples; launch-only
    settings (`autoRecordOnLaunch`) are inert until the next launch. Does not corrupt the
    in-flight recording. v1.
17. **SetSetting before any game is loaded (main menu).** `ParsekSettings.Current` is
    null; the command Defers until a game loads, or `TIMEOUT`s if the orchestrator
    sequenced it wrong. v1.
18. **SetSetting non-whitelisted name / out-of-range value.** `REJECTED
    msg=setting-not-whitelisted` or `setting-value-invalid`; the field is never touched.
    v1 (security-critical).
19. **RunTests while a batch is already running.** Strict FIFO plus the `IsRunning` gate
    defers the next command until the batch finishes. v1.
20. **StartRecording issued in a scene that never becomes FLIGHT.** Defers until its
    per-command budget expires -> `TIMEOUT`, pump advances so the run is not wedged. v1.
21. **Env var set to something other than `1`** (`0`, `true`, empty). Fail-closed: addon
    stays inert. v1 (security).
22. **Partial trailing line** (orchestrator mid-write when the pump polls). The pump only
    processes newline-terminated lines; the fragment is left for the next poll. v1.
23. **Value with spaces / `=` (e.g. a MissionMark label or a path).** Percent-encoded by
    the orchestrator, decoded by the parser onto one token; a bad encoding is `REJECTED
    msg=malformed`. v1.
24. **Command file grows large over a long run.** Steady state uses the in-memory byte
    offset + processed-set (O(new lines) per poll); only startup does a full rescan +
    journal replay. The orchestrator rotates the files between runs. v1.
25. **Orchestrator forgot to truncate files between runs.** Leftover `DONE` journal makes
    all old ids no-ops; the addon logs "no fresh commands". If the orchestrator reuses old
    ids it must truncate; monotonic-across-run ids avoid the issue entirely (documented
    contract). v1.
26. **Response line torn by a crash mid-append.** A response line is written in a single
    append call ending in `\n`; the orchestrator ignores any trailing line without a
    newline. The journal `DONE` combined with the terminal response is the source of truth
    (a torn response with no `DONE` is rewritten on restart from `EXECUTED`). v1.
27. **LoadGame naming a nonexistent / incompatible save.** Two failure surfaces. (a) The
    up-front `GamePersistence.LoadGame` returns a null / version-incompatible game or an
    out-of-range active-vessel index: `IsLoadedGameFocusable` fails, the handler never
    initiates the flight boot and writes `ERROR msg=load-failed` + `DONE` (single-phase).
    (b) The save PARSED and was focusable but the flight boot fails at runtime -- e.g.
    `FlightDriver.Start()` throws a `NullReferenceException` because a mod-part active
    vessel is absent from the instance (the first-live-run failure, F2). The two-phase
    completion now detects this via `TestCommandLoadGame.DecideLoadCompletion`: the scene
    settles back at MAINMENU with no flight -> terminal `ERROR msg=load-failed-returned-to-menu`;
    a load that never settles anywhere -> terminal `ERROR msg=load-timeout` once the LoadGame
    budget (300 s) expires, rather than the completion polling PENDING to the harness run
    budget. Either terminal ERROR lets the harness classify a driver-INVALID (fixture). The
    instance stays at the menu; the orchestrator reconciles. v1.
28. **KSP crashes mid-LoadGame (during the scene load).** The journal is at `CLAIMED`
    (the load was initiated, the settle never completed). On restart the addon does NOT
    re-initiate the load; it writes `INTERRUPTED` and marks `DONE`. The journal file
    survives the scene load, so at-most-once is preserved and the orchestrator treats the
    outcome as unknown and re-issues `LoadGame`. v1.
29. **LoadGame at MAINMENU (the boot path).** This is the intended boot channel: the first
    command an orchestrator sends after process start selects the save. The pump's
    safe-point gate permits execution at `MAINMENU` (not `LOADING`, settled), and `LoadGame`
    declares no game precondition, so it executes there rather than deferring. v1.
30. **LoadGame mid-flight with a live recorder or active tree.** Rejected with
    `msg=recording-active` (a live recorder) so the load never silently discards an
    in-flight recording; the orchestrator must send `CommitTree` or `DiscardTree` first.
    A second `LoadGame` while one is already in flight is Rejected `msg=load-in-flight`.
    v1 (behavior documented, not a bug).

## Deferred Items and Open Questions

Tracked follow-ups deliberately NOT implemented in the M-A2 fix round (reviewer nits + a
design deferral). None blocks the seam; each is recorded so it is not lost.

- **Vessel-ready Defer for `StartRecording` (F4 follow-up).** The v1 handler contains a
  refusal by RE-SAMPLING the recorder after `ParsekFlight.StartRecording` and returning
  `ERROR msg=start-refused` (better than a false OK). The cleaner long-term fix is a new
  `DispatchState` readiness bit (active vessel loaded + unpacked + not restoring/re-fly/
  merge-journal) so the command DEFERS until FLIGHT is genuinely ready and only executes
  when `StartRecording` will succeed, converting a transient refusal into a normal wait
  rather than a terminal error. Deferred because it widens the dispatch state and wants its
  own decision-matrix coverage; not done in this pass.
- **N1: deferral-budget timing uses wall-clock.** `WallClockSeconds()` is
  `DateTime.UtcNow`-based; an NTP / clock adjustment mid-run could distort a per-command
  deferral budget. A monotonic source (`Stopwatch` / `Time.realtimeSinceStartup`) would be
  more robust. Low impact on a dev PC; tracked.
- **N2: `line#<n>` fallback id is per-process.** The `FallbackId(lineNumber)` correlation id
  for an id-less malformed line resets its line counter each process, so the same
  `line#<n>` can denote a different line across a restart. Only affects malformed, id-less
  lines (which are REJECTED); tracked.
- **N4: command-file byte offset is not persisted.** `commandByteOffset` is in-memory and
  resets to 0 on restart, forcing one full command-file rescan on the first post-restart
  poll (deduped by the processed-set, so correct, but O(file)). A persisted offset would
  avoid the rescan on very long runs. Tracked.
- **N5: startup multi-id recovery shares one retry slot.** The `headPendingResponse` slot
  holds a single deferred ack; if several startup recovery acks fail their append in the
  same session, only the last retries within that session (the rest are backstopped by
  cross-restart re-recovery, since no `DONE` is written until the append lands). Acceptable
  given the restart durability, but tracked.
- **R1: LoadGame completion predicate [RESOLVED, F2].** Completion now runs through the
  pure `TestCommandLoadGame.DecideLoadCompletion(elapsed, scene, currentGameNonNull, budget)`
  -> `{StillWaiting, CompleteOk, LoadTimeout, LoadFailedMenu}`. Success requires a settled
  FLIGHT scene with a loaded game (no longer `CurrentGame != null` at any scene); a
  post-initiation failure that dumps back to MAINMENU surfaces as `ERROR
  msg=load-failed-returned-to-menu` (fast, before the budget), and a never-settling load as
  `ERROR msg=load-timeout` against the LoadGame budget. Relies on the same invariant that
  made the old predicate safe -- the scene-transition flag is raised synchronously at
  initiation and the pump only polls completion at settled scenes -- so a MAINMENU
  observation reliably means the load bounced (no grace period needed).
- **R2: lock Unknown-liveness tie-break effectively always reclaims.** When the pid probe
  returns Unknown (e.g. access denied on a live foreign process), `DecideLockOwnership`
  compares the existing lock's t against now, which an existing lock always loses, so the
  channel can be stolen from a live-but-unprobeable instance. Acceptable on a single-user
  dev box; an age threshold would harden it.
- **R3: scenarioBudgetSeconds is never wired.** `DeferralBudget.BudgetSeconds` accepts a
  scenario-spec budget parameter that no caller supplies yet; the design's
  "budget from the scenario spec" is deferred to the M-A5 harness integration.
- **R4: no strict-FIFO unit test.** The queue/timeout mechanics live in the MonoBehaviour;
  the dispatch-decision half is pure-tested but head-only ordering itself is not. A future
  pure pump-step extraction would make it unit-testable.

## What Doesn't Change

- No recording format, schema generation, sidecar, tree, ledger, or save-file field
  changes. `RecordingStore.CurrentRecordingFormatVersion` /
  `CurrentRecordingSchemaGeneration` are untouched; no migration path is added.
- No gameplay behavior in normal play. The addon is env-gated and inert unless
  `PARSEK_TEST_COMMANDS=1`; it is never shipped enabled and adds no Settings-UI toggle.
- `TestRunnerShortcut` and the `Ctrl+Shift+T` runner are unchanged; `RunTests` reuses the
  existing `InGameTestRunner.RunAll` / `RunCategory` / `ExportResultsFile` surface.
- `ParsekSettings` serialization is unchanged; `SetSetting` mutates existing fields
  through their existing persistence routes - `GameParameters.CustomParameterNode` for
  GameParameters-only settings, and additionally `ParsekSettingsPersistence.Record*` for
  the 8 sidecar-authoritative settings (mirroring `UI/SettingsWindowUI.cs`). No new
  serialization format is added.
- No kRPC reference is added; no assembly is linked that would create a GPL entanglement.
- No new `GameEvents` subscriptions that affect gameplay - the addon only tracks scene
  transitions for its own safe-point gating, matching `TestRunnerShortcut`.
- Recording lifecycle policy (plan 3.3) is unchanged; this module supplies the control
  verbs the policy assumes (pin auto-record off via `SetSetting`, `StartRecording` /
  `CommitTree` deliberately).

## Backward Compatibility

- The channel files are ephemeral test artifacts, not versioned save data, so there is no
  save migration concern. They exist only in the automation instance.
- **Protocol forward/backward compatibility.** The command line carries an optional `v=`
  field (default 1) and readers ignore unknown keys, so phase-3 verbs may add keys without
  breaking a v1 parser; a v1 addon `REJECT`s (does not crash on) reserved and unknown
  verbs. A future response consumer ignores unknown payload keys the same way. New
  verbs and new whitelist entries are additive.
- **Cross-run reuse.** The orchestrator either (a) uses ids that are unique across process
  restarts within a run and truncates all four files when starting a fresh logical run, or
  (b) uses globally unique ids (GUIDs). The addon trusts the journal for at-most-once; it
  never rewrites or deletes the orchestrator's command file.
- No existing recordings, saves, or settings files are read or rewritten by this module
  beyond the live `ParsekSettings.Current` mutation, which round-trips through the
  unchanged parameters-save path.

## Diagnostic Logging

Subsystem tag: `TestCommands`. Format is the standard `[Parsek][LEVEL][TestCommands]
message` (`ParsekLog.Write`). Per-poll iteration uses the batch-counting convention;
per-command lines are bounded (few commands) so per-command Info is allowed. Numeric
values use InvariantCulture.

Env gate and lifecycle:
- Awake gate decision: `Info` "armed" (with `PARSEK_TEST_COMMANDS=1`) or `Verbose`
  "inert: PARSEK_TEST_COMMANDS=<value-or-unset>".
- Lock: `Info` "lock acquired session=<id> pid=<n>", or `Warn` "standing down: foreign
  live lock session=<other> pid=<n> (alive) t=<t>", or `Warn` "reclaimed crashed lock
  session=<other> pid=<n> (dead) t=<t>". `t` is logged for context; pid liveness is the
  decision.
- Startup journal replay: `Info` summary "journal replay: N done, N executed-not-done
  (rewriting response), N claimed-not-executed (INTERRUPTED)".

Per poll:
- `Verbose` (rate-limited, shared key) one summary line: "poll: read=N lines, parsed=N,
  deferred-head=<verb/none>".

Per command (Info unless noted):
- Receipt: "recv id=<id> cmd=<verb> args=<count>".
- Dispatch decision: one line per decision path, never silent -
  "dispatch id=<id> -> EXECUTE", "dispatch id=<id> -> DEFER reason=<r>" (rate-limited per
  id while it keeps deferring), "dispatch id=<id> -> REJECT reason=<r>",
  "dispatch id=<id> -> INTERRUPTED (journal=<phase>)".
- Journal writes: `Verbose` "journal id=<id> phase=CLAIMED|EXECUTED|DONE".
- Execution: "exec id=<id> cmd=<verb> start" then "exec id=<id> verdict=<v> <payload>".
- Response append: `Verbose` "response appended id=<id> verdict=<v>"; on IO failure
  `Warn` "response append failed id=<id> attempt=<n>: <ex>" and, if exhausted, `Error`
  "response append giving up this frame id=<id>; will retry (journal=EXECUTED)".
- Timeout: `Warn` "timeout id=<id> cmd=<verb> deferred=<seconds>s reason=<lastDeferReason>".
- Duplicate id: `Warn` "duplicate id=<id> ignored".
- Malformed / unknown / reserved: `Warn` "reject id=<id> cmd=<verb> reason=<malformed|
  unknown-command|not-implemented-v1>".

Per verb specifics:
- `SetSetting`: `Info` "setting name=<name> old=<old> new=<new>", or `Warn`
  "setting rejected name=<name> reason=<not-whitelisted|value-invalid> raw=<value>".
- `StartRecording`/`StopRecording`/`CommitTree`/`DiscardTree`: `Info` with the resulting
  tree/recording id and whether it was a no-op (already/idle/nothing).
- `RunTests`: `Info` "runtests start category=<cat|all>" and "runtests complete passed=N
  failed=N skipped=N results=<path>".
- `LoadGame`: `Info` "loadgame start save=<folder> name=<file> scene=<current>" and
  "loadgame complete scene=<new> save=<folder> game-loaded=<bool>", or `Warn`
  "loadgame rejected reason=<recording-active|load-in-flight>", or `Error`
  "loadgame failed save=<folder>: game null/incompatible". The start/complete pair brackets
  the boot channel so a KSP.log reader can see the instance booted into the intended save.
- `MissionMark`: `Info` "MISSIONMARK label=<label> ut=<ut>" (stable, grep-able for
  orchestration correlation).
- `FlushAndQuit`: `Info` "flushandquit: saved=<bool> game-loaded=<bool>; quitting" -
  logged and flushed BEFORE `Application.Quit`.

Goal: a developer reading KSP.log can reconstruct, for every command id, that it was
received, which dispatch branch it took and why, whether it executed, and the terminal
verdict - without the source.

## Test Plan

Pure core (`TestCommandParser`, `DecideDispatch`, whitelist setters, response/journal
formatters, `DecideLockOwnership`, percent codec) is `internal static` and xUnit-tested
without Unity. Only the thin addon (`ParsekTestCommandAddon`) touches Unity/KSP and is
exercised in-game.

Unit tests (each: input -> expected -> what makes it fail):

- **Parse valid line round-trip.** `id=0001 cmd=SetSetting name=x value=false` ->
  id/verb/args populated. Fails if the parser mis-splits `key=value` tokens or drops args.
- **Parse malformed lines.** Missing `id`, missing `cmd`, a bare token with no `=`, a
  value with an illegal raw space. Each -> `ParseOk=false` with the right reason. Fails if
  a malformed line is accepted and later executed as a real command.
- **Percent codec round-trip.** `mun%20landing`/`a%3Db`/`50%25` decode to the literals and
  re-encode back. Fails if a `MissionMark` label with spaces is split across tokens or a
  `%` value is corrupted.
- **Whitelist accept + type/range.** Each whitelisted name parses to the typed value;
  `samplingDensity=1` sets 1, `samplingDensity=5` -> reject, `ghostAudioVolume=0,7`
  (comma locale) -> reject (InvariantCulture only), `autoMerge=yes` -> reject. Fails if an
  out-of-range or locale-broken value is written, or a non-bool passes a bool setting.
- **Whitelist rejects arbitrary field.** `name=someOtherField` -> `REJECTED
  not-whitelisted`; assert no reflective set occurs. Fails if the dispatcher can set a
  non-whitelisted field (the security-critical test - proves commands are data, not code).
- **Tracked-setting persistence route.** Each of the 8
  `ParsekSettingsPersistence`-authoritative names (`writeReadableSidecarMirrors`,
  `showCommittedFutureOverlays`, `blockCommittedActions`, `showRouteLines`,
  `autoBackupExistingSaves`, `ghostRenderTracing`, `mapRenderTracing`, `ledgerTracing`)
  routes through the matching `ParsekSettingsPersistence.Record*` call (asserted via the
  persistence seam / a spy), and the 8 GameParameters-only names do NOT. Fails if a tracked
  setting is written only to the live field, which `ParsekScenario.OnLoad`'s `ApplyTo`
  would silently revert at the next save load (the exact bug this column fixes).
- **Dispatch decision matrix.** For each verb x state
  (scene, game-loaded, recording, has-tree, transitioning, batch-running, journal-phase)
  assert `Execute` / `Defer(reason)` / `Reject(reason)` / `Interrupted`. Key rows:
  `StartRecording` outside FLIGHT -> Defer; `CommitTree` with no tree -> the handler's
  `no-active-tree` error path; `SetSetting` with no game -> Defer; `RunTests` while a
  batch runs -> Defer; `LoadGame` at MAINMENU with no recorder -> Execute (boot channel);
  `LoadGame` with a live recorder -> Reject(`recording-active`); `LoadGame` while another
  load is in flight -> Reject(`load-in-flight`). Fails if a command executes in an unsafe
  scene/state, or if `LoadGame` silently discards an in-flight recording.
- **Three-phase journal at-most-once.** Given a journal with an id at `CLAIMED` ->
  decision `Interrupted` (no execute). At `EXECUTED` -> skip execute, rewrite response,
  `DONE`. At `DONE` -> skip entirely. Fresh id -> `Execute`. Fails if a mid-execution
  crash re-runs a non-idempotent command (the core correctness guarantee).
- **LoadGame journal at-most-once (long-running boot).** A `LoadGame` id at `CLAIMED`
  (crashed mid-scene-load) -> `Interrupted`, never re-initiate the load; at `EXECUTED`
  (loaded, response not written) -> rewrite response, `DONE`; at `DONE` -> skip. Fails if
  a crash mid-boot re-triggers `GamePersistence.LoadGame` or double-acks the boot.
- **Duplicate id.** Two lines with the same id -> second returns "already handled", no
  second response. Fails if a command is executed or acked twice.
- **Strict FIFO / no reordering.** A deferring head command blocks a later ready command
  until it times out. Fails if the pump jumps ahead and violates ordering.
- **Timeout conversion.** A command deferred past its budget -> `TIMEOUT` terminal, pump
  advances. Fails if a never-satisfiable command wedges the run forever.
- **Reserved vs unknown verb.** A reserved phase-3 name -> `not-implemented-v1`; a
  gibberish name -> `unknown-command`. Fails if v1 silently executes or mis-buckets a
  future command.
- **Response formatter stability.** Assert the exact grep-able shape (`id=`, `cmd=`,
  `verdict=`, `seq=` present; payload keys percent-encoded; InvariantCulture floats).
  Fails if a field is dropped or a locale comma leaks into `ut`.
- **Lock ownership decision.** Own-session lock -> reclaim; foreign lock with a LIVE pid ->
  stand down; foreign lock with a DEAD pid -> reclaim-with-warn; the injected pid-liveness
  probe is the primary, `t` only a tie-break/log detail. Fails if two instances both
  consume, if a live foreign lock is stolen (pid-liveness ignored in favor of a fresh `t`),
  or if a crashed instance's lock wedges a fresh run (dead pid not reclaimed because `t`
  looked recent).
- **Env gate predicate.** `"1"` -> armed; `null`/`"0"`/`"true"`/`""` -> inert. Fails if
  the addon consumes commands without the exact gate (ships enabled by accident).

Log-assertion tests (via `ParsekLog.TestSinkForTesting`, per `RewindLoggingTests`):

- Every dispatch branch (Execute/Defer/Reject/Interrupted) emits a `[TestCommands]` line
  with the id and reason. Fails if a decision branch is silent (a debugging blind spot),
  or if the receipt/verdict lines lose their grep-able shape under refactor.
- `MissionMark` emits the stable `MISSIONMARK label= ut=` line. Fails if the orchestration
  correlation marker format drifts.

In-game tests (`InGameTests`, live KSP only - the addon's Unity side):

- With `PARSEK_TEST_COMMANDS` unset, `ParsekTestCommandAddon` performs no file access and
  no polling (assert inert). Fails if the shipped default is not fully inert.
- A `StartRecording` -> `RecordingState` -> `CommitTree` -> `RecordingState` sequence
  through the file channel produces the expected verdicts and a committed tree in FLIGHT.
  Fails if the addon's main-thread execution or safe-point gating is wrong. (This is the
  end-to-end path M-A5 depends on; per the plan and MEMORY note on in-game sweeps, it is
  delivered as an automated in-game test plus a PENDING-OPERATOR runbook, since an agent
  cannot pilot KSP.)
- A cold-boot `LoadGame` -> `RecordingState` sequence: the addon armed at process start,
  the first command a `LoadGame save=<folder> name=<file>` issued at `MAINMENU`, drives the
  instance into FLIGHT and a following `RecordingState` returns `OK` with the loaded
  scene/save. Fails if the boot channel does not execute at the menu or the safe-point gate
  wrongly defers it (the automation instance would never leave the menu). Delivered as an
  automated in-game test plus a PENDING-OPERATOR runbook.
