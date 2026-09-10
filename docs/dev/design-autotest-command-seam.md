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
id=0005b cmd=RunTests category=SceneExitMerge isolated=true
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
  **NOTE FOR SPEC AUTHORS - this is a WIRE rule, not a TOML rule.** Everywhere this doc
  writes "(percent-encoded)" beside an argument it is describing the token that reaches
  the seam file, and `run.py` performs that encoding ITSELF when it writes the command
  line. A scenario spec therefore carries the RAW value:
  `args = { vessel = "Kerbal X Probe" }`, never `"Kerbal%20X%20Probe"`. Pre-encoding in
  the TOML double-encodes - the seam receives `Kerbal%2520X%2520Probe` and refuses
  `target-not-found` for a vessel the author named correctly.
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

The **persistence route** column is load-bearing. 5 of the 13 whitelisted settings are
NOT authoritatively persisted through `GameParameters.CustomParameterNode` (13/5 since
the 2026-08-27 settings simplification retired `showCommittedFutureOverlays`,
`blockCommittedActions`, `autoBackupExistingSaves` and `transitedBodyRotationModeIndex`
-- SetSetting on any of those four now rejects `setting-not-whitelisted`): for
`writeReadableSidecarMirrors`, `showRouteLines`, `ghostRenderTracing`,
`mapRenderTracing`, and `ledgerTracing`, the `ParsekSettingsPersistence` sidecar
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
| `showRouteLines` | bool | true/false | `showRouteLines` | GameParameters + `ParsekSettingsPersistence.Record*` |
| `samplingDensity` | int | 0..2 | `samplingDensity` | GameParameters |
| `ghostAudioVolume` | float | 0.0..1.0 (InvariantCulture) | `ghostAudioVolume` | GameParameters |

(Removed 2026-08-27 with their settings: `autoBackupExistingSaves`,
`showCommittedFutureOverlays`, `blockCommittedActions` -- behaviors permanently on --
and `transitedBodyRotationModeIndex` -- landing-body alignment pinned Loose via
`ParsekSettings.LandingBodyAlignmentMode`.)

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

> Update (M-C1.1): a further verb, `SaveGame` (an in-process persist of the current game
> for the M-B3 R6 persist-before-reload script), was later added as a NEW implemented verb
> - it was never in the reserved list above, so it is additive rather than a promotion. See
> the M-C1.1 follow-ups section of `design-autotest-seam-verbs-c1.md`.

> Update (M-C2): three EVA verbs - `EvaExit`, `EvaBoard`, `PlantFlag` - were added as NEW
> implemented verbs (additive, never reserved). See `design-autotest-eva-missions.md`.

> Update (EVA-4): a fourth EVA-family verb, `EvaChuteDeploy` (arm a kerbal's personal
> parachute through the same public `ModuleEvaChute.Deploy()` both stock player paths call,
> bounded-wait for the canopy to be VERIFIED open, and optionally hold through the descent
> until the kerbal is LANDED/SPLASHED and ALIVE), was added the same additive way, bringing
> the implemented table to 19. It is the FIRST EVA verb in the `DEFERRED_SEAM_VERBS` family:
> its `awaitDown` stage holds the FIFO head for the kerbal's whole chuted descent, so the
> 540 s per-step cap governs it. Its dispatch precondition is FLIGHT plus the same `not-eva`
> defer `PlantFlag` / `EvaBoard` use. Forensics + decompile evidence in
> `todo-and-known-bugs.md` ("EVA-4 - atmospheric mid-flight EVA + kerbal personal chute").

> Update (R12): scene routing. Roadmap item R12 closes Cause C ("scene entry is
> two-valued") in two independent pieces, delivered below as **A1** (an additive `scene=`
> ARGUMENT on the existing `LoadGame` verb) and **A2** (a new additive verb
> `ExitToSpaceCenter`), plus **B** (`SimulateStockSwitchClick`), which is a PROMOTION out of
> the reserved list above rather than an addition to it. The three together bring the
> implemented table to 21 and the reserved list to 10.

> Update (the loop lanes): three further PROMOTIONS out of the reserved list above, each
> the same strict shape as R12/B (wire token byte-identical, only the response changes).
> `MissionConfig` (arrival-validation lane, 2026-08-06) arms/disarms a committed mission's
> loop playback through the production `MissionStore.SetLoopEnabled`, taking the table to
> 22 implemented / 9 reserved. `StartLoopPlayback` and `EnterWatchMode` (player-workflow
> lane, 2026-08-08) close the flight-scene half that arming only prepared: warp to the
> looped mission's next faithful departure through the Missions window's own path
> (`TryBuildLoopUnitForSelection` -> `ComputeNextRelaunchUT` ->
> `ParsekFlight.FastForwardToEventUT`; DEFERRED, and completed on the LEAD-adjusted LANDED
> UT, because that production call lands 15 s BEFORE lift-off), then enter ghost watch mode
> with the entry VERIFIED by read-back (`IsWatchingGhost && WatchedRecordingIndex ==
> target`) - `EnterWatchMode` is a silent-failing TOGGLE, so an unverified call would report
> a false OK, and re-issuing it on the already-watched index would EXIT watch mode. That
> brings the table to **24 implemented / 7 reserved**. `StopPlayback` deliberately stays
> reserved: teardown is `FlushAndQuit`'s job, so a stop verb would be a second, weaker
> owner of it. ONE VERDICT OUTLIER A SPEC MUST BE WRITTEN AGAINST: the shared
> `unknown-tree` refusal carries `REJECTED` on `StartLoopPlayback` / `EnterWatchMode` (a
> no-side-effect lookup miss) but `ERROR` on `MissionConfig` (pre-existing), so a spec
> asserting a bad-tree refusal must match `expect` PER VERB - the harness subkind is
> `driver-arg` either way, because `hlib._SEAM_REFUSAL_SUBKINDS` maps the msg token and
> never reads the verdict.

> Update (M-A7): one further verb, `ExportRenderManifest`, added the ADDITIVE way (never
> in the reserved list above, like `SaveGame` and the EVA family), bringing the table to
> **25 implemented / 7 reserved**. It flushes the armed render-composition recorder's
> accumulation to `parsek-render-manifest.txt` in the KSP root and answers OK with the
> written path plus the record counts, so a lane can take the manifest at a MEASURED
> instant instead of provoking a scene bounce to reach the auto-flush - which would change
> the very composition being measured. SINGLE-PHASE (the flush plus one safe-write are
> synchronous, so there is nothing to wait for), no args, precondition `AnyScene` because
> the recorder is a DontDestroyOnLoad addon that accumulates in every scene. Its one
> refusal is `REJECTED msg=manifest-not-armed`, deliberately not a defer: the arm gate is
> the `PARSEK_RENDER_MANIFEST` env var read once at Awake, so waiting could never turn it
> into a success. See `design-autotest-render-composition.md`.

> Update (the map-view pair): two further verbs, `EnterMapView` and `ExitMapView`, added
> the ADDITIVE way (the reserved list above never carried a camera / scene-presentation
> verb), bringing the table to **27 implemented / 7 reserved**. They exist because of a
> MEASURED instrument gap and not a wish. Everything Parsek draws on the map surface is
> gated on the map being OPEN, and no seam verb ever opened it:
> `GhostTrajectoryPolylineRenderer.Driver.LateUpdate`'s second statement is
> `if (!MapView.MapIsEnabled) return;`, so on EVERY render-composition lane flown to date
> the ownership-PUBLISH half of the pipeline never ran once - zero `Polyline frame:`
> summaries across a whole flight - while the INTENT half, driven from `ParsekFlight`'s
> per-frame update, ran map-open or not. A lane that never opens the map therefore
> measures intent and reports it as a draw. Full forensics:
> `RC-OWN-DRAW-HALF-IS-MAP-GATED` in `todo-and-known-bugs.md`.
>
> **Contract.** No args in v1 (camera focus / zoom were considered and left out: the
> draw-half evidence a lane reads is the manifest plus the tracer lines, and neither is a
> function of where the map camera points). Precondition `RequiresFlight` - the map view
> is a FLIGHT overlay, the same reason `SimulateStockSwitchClick` carries that row for the
> click that lives on it, and TRACKSTATION's planetarium is a different scene with its own
> always-on map that `MapView.EnterMapView` does not drive. IDEMPOTENT: an already-open
> map answers `OK alreadyOpen=true` without calling stock at all, and the exit mirror
> answers `OK alreadyClosed=true`; the OK payload is `mapOpen` plus that flag, and the flag
> is present in BOTH values so a lane can tell "my step opened it" from "it was already
> open" rather than from an absent key.
>
> **SINGLE-PHASE, and this is a decompile fact rather than a convenience.** KSP 1.12.5's
> `MapView.EnterMapView()` delegates to the instance `enterMapView()`, whose body assigns
> `MapIsEnabled = true` and fires `GameEvents.OnMapEntered` SYNCHRONOUSLY before returning;
> the deferred `Invoke("endEnterMapTransition", transitionDuration)` that follows only
> disables the UI cameras and does not gate `MapIsEnabled`. `exitMapView()` is the mirror -
> its FIRST statement is `MapIsEnabled = false`. So the very property the polyline Driver
> gates on is truthfully readable on the line after the call, there is nothing to wait for,
> and a deferred shape would invent a wait that does not exist (the `MissionConfig` /
> `SimulateStockSwitchClick` shape, no `TryComplete*` counterpart, no `DeferralBudget` row).
> Every stock refusal path is synchronous too, so a false read-back is a FINAL answer.
>
> **Refusals, and the verdict split a spec's `expect` must be written against.**
> `REJECTED msg=map-view-unavailable` is the ONE pre-call gate (`MapView.fetch` is null, so
> stock was never called). Everything after the call is `ERROR`: `map-not-entered` /
> `map-not-exited` when the call ran and the read-back still disagrees, and
> `map-view-threw` when it threw. That is exactly where `SimulateStockSwitchClick` draws
> the line - REJECTED for every gate it evaluates before calling stock,
> ERROR for `switch-refused-by-stock` / `switch-threw` after - and `EnterWatchMode`'s
> post-call `watch-not-entered` is ERROR too. A REJECTED on a post-call decline would claim
> the seam declined to act, when it acted and the GAME declined. Stock declines silently on
> three paths - `ConstantMode`, `MissionSystem.AllowCameraSwitch(CameraMode.Map)` false,
> and `HighLogic.CurrentGame.Parameters.Flight.CanUseMap` false - so the read-back is the
> only honest verdict source, and the decline CLASS is deliberately not re-derived: naming
> a cause we did not observe could disagree with the one that actually fired (the
> `EnterWatchMode` discipline). The two directions never share a decline token, so a spec
> pinning `map-not-entered` can never match an exit that declined, and the throw token is
> kept apart from both because a throw is a different investigation. No
> `_SEAM_REFUSAL_SUBKINDS` rows were added for these four tokens (the `ExportRenderManifest`
> precedent): they ride the coarse `driver-verdict-mismatch` until a lane's forensics ask
> for finer routing.
>
> **Why the exit mirror ships with it.** Not teardown: `FlushAndQuit` saves and quits
> regardless of camera mode, and the render-composition manifest's scene-exit flush does
> not care whether the map was open. It ships so a map-open lane can CLOSE the map again
> before flight-scene steps that behave differently under the overlay -
> `SimulateStockSwitchClick` already has to call `MapView.ExitMapView()` itself after
> switching, and `EnterWatchMode` drives the flight camera. Harness roles: both are
> `world-mutating` on the tail axis (stock's enter takes an `InputLockManager` control lock
> on the live vessel - `EnterWatchMode`'s argument verbatim) and `recording` on the
> post-mission gating axis (their OK is a read-back of the game's camera mode, never a
> claim about a kerbal's physical state). The two axes disagree by design.

> Update (the logistics verbs): two further PROMOTIONS out of the reserved list above -
> `SealSlot` and `RouteCommand` - the FIFTH and SIXTH strict ones (wire token
> byte-identical, only the response changes). They take the table to **30 implemented /
> 5 reserved**. (The count crossing 27 -> 28 on the way is `InvokeRewindToLaunch`, which
> landed additively without an update note of its own; the reserved list was unchanged
> by it.)
>
> **What they close, and it is a MEASURED wall rather than a wish.** No driven lane could
> create or operate a supply route, so every committed route fixture in the suite is a
> HARVEST of a hand-flown session (`fixtures/saves/depot-route-recorded`, the suite's only
> committed Active route, exists for exactly this reason - see the B27 amendment on
> roadmap G1). The cause is a chain of two: route candidacy requires a FULLY SEALED tree
> (`RouteCandidateFinder.IsTreeFullySealed` - every recording at `MergeState.Immutable`),
> and an automated profile ending on a flight-class terminal leaves `CommittedProvisional`
> members behind BY DESIGN, so no automated mission produces a sealed tree; and sealing is
> a player action with no seam path. H35's own spec header documents its fixture as
> deliberately not a candidate for this, and `ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH`
> in `todo-and-known-bugs.md` names promoting `SealSlot` as fix road (1). Both verbs are
> that road.
>
> **Why the OTHER two slot names stay reserved.** `FlySlot`'s mechanism is already
> driveable under a different name - `InvokeRewind` IS the re-fly - and a second spelling
> would make the wire token ambiguous about which half of the timeline machinery a spec
> exercised, the identical argument that kept `InvokeRewindToLaunch` a separate verb
> rather than an `InvokeRewind` mode. `StashSlot` has no consumer: nothing in the suite
> needs to OPEN a slot, only to close one.
>
> Full contracts below (`#### SealSlot` and `#### RouteCommand`).

> Update (DeleteRecording, 2026-09-02): one further ADDITIVE verb, `DeleteRecording
> index=<n>` - the SaveGame / EVA / ExportRenderManifest shape, never in the reserved
> envelope (which carried no recording-deletion verb). It takes the table to **31
> implemented / 5 reserved**; the reserved list is unchanged.
>
> **What it closes: AUTOMATION-GAP-KSC-TABLE-DELETE.** The Space Center delete path
> (`RecordingStore.DeleteRecordingFull`, whose Removing / Removed notifications the
> `ParsekKSC` host shifts its index-keyed ghost state from) had no driven caller: the
> subscriber that stopped a KSC delete from leaving every ghost above the row playing its
> neighbour's trajectory was pinned headlessly and never exercised in the scene it was
> written for. The Recordings table's delete is an IMGUI button, and the seam had no
> recording-mutation verb below `DiscardTree`.
>
> **Deliberately wider than the button on one axis.** The table's "X" is offered only on
> `IsGhostOnly` rows, and the only producer of those is the Gloops recorder - no fixture
> carries one and no driven step can make one. The removal the lane exists to drive is a
> MID-LIST one under living ghosts, which an appended ghost-only row can never be, so the
> verb takes any committed index. It is NOT wider in what it calls: every route is a
> production entry point (see the contract below).
>
> Full contract below (`#### DeleteRecording`).

> Update (the GUI census, 2026-09-10): TWO further ADDITIVE verbs, `CaptureScreenshot
> label=<name> [superSize=<1-4>]` and
> `UiAction op=<open|close|tab|complexity|rect|describe> ...` - the
> SaveGame / ExportRenderManifest / ListHandles / WarpToUT shape, never in the reserved
> list above, so neither is a promotion and the implemented table moves alone: **35
> implemented / 5 reserved**.
>
> They exist because a GUI review had no evidence surface AT ALL, and both halves of that
> gap are measured rather than argued. First: the always-collect leg has copied
> run-window files out of `<instance>/Screenshots/` into `results/<runId>_shots/` since V3
> (`hlib.select_run_screenshots`, 64-file / 256 MB caps) and `tools/contact_sheet.py` has
> rendered whatever it found there - but nothing ever WROTE a file, because the only
> screenshot path in the game is the player's F1 key. That tool's own empty-state text
> ("no screenshots captured (the V4 capture verbs feed this)") has been the literal truth
> on every run to date. Second: every Parsek window's open flag is only ever written by a
> player click - the toolbar callback for the main window, then one button per sub-window -
> so an unattended run draws NOTHING and a capture verb on its own would have photographed
> an empty scene. One verb without the other buys nothing.
>
> `CaptureScreenshot` is the only TWO-PHASE verb in the seam whose completion is a FILE
> poll, and the two-phase shape is not optional: `ScreenCapture.CaptureScreenshot` returns
> immediately and Unity encodes the PNG at the end of a later frame, so a single-phase OK
> would claim a file that may not exist and the NEXT step (another capture, a window close,
> a scene exit) would race the write. `UiAction` is two-phase in exactly TWO of its six
> ops - `open` and `rect`, whose read-back only means something after a frame has been
> DRAWN - and single-phase in the other four, including the one that LOOKS deferred; see
> its section for both. Full contracts below (`#### CaptureScreenshot`, `#### UiAction`).
>
> NO NEW PLAYER-FACING SURFACE, which is a house rule rather than a preference: `UiAction`
> writes the same fields the existing buttons write and reads them back. It adds no window,
> no button and nothing reachable in a normal game; the only production entry points it
> calls are `ParsekUI.SetUiComplexityMode` and `ApplyPendingUiComplexityModeIfAny`.

> Update (the GUI census, half two, 2026-09-11): ONE further ADDITIVE verb,
> `DumpGuiTree label=<name>`, the same shape again - never in the reserved list, so the
> implemented table moves alone: **36 implemented / 5 reserved**.
>
> It closes the half a picture cannot carry. An IMGUI window has no retained widget tree,
> no `GameObject` hierarchy and no accessibility surface: the only artefact of a window is
> the pixels it drew that frame, so a screenshot cannot say which rows a filter left
> visible, which control was disabled, what nests inside what, or which tooltip a control
> published. `GuiTreeRecorder` (`docs/dev/design-gui-tree-dump.md`) records exactly that
> for ONE Repaint pass and writes it as `Screenshots/<label>.gui.json`, which
> `hlib.ARTIFACT_SHOTS_SUFFIXES` already harvests alongside the PNGs and
> `harness/tools/gui_tree_view.py` renders as boxes over the matching screenshot. The
> recorder shipped with NO seam verb on purpose - its API is `internal` and a verb was a
> separate change on a sibling branch - so until now nothing unattended could arm it.
>
> TWO-PHASE, and not optionally: arming asks for the NEXT Repaint pass, which the recorder
> records and then assembles + serialises + writes from the FOLLOWING `LateUpdate`. A
> single-phase OK would claim a file that does not exist yet, and the next step would not
> merely race the write - it would change the very UI the pending capture is about to
> record. Full contract below (`#### DumpGuiTree`).

> Update (WarpToUT, 2026-09-09): one further ADDITIVE verb, `WarpToUT ut=<absolute UT>
> [maxRate=<float>]` - the SaveGame / ExportRenderManifest / ListHandles shape, never in
> the reserved envelope (which carried no warp verb). It takes the table to **33
> implemented / 5 reserved**; the reserved list is unchanged.
>
> **What it closes: RF12-NO-SEAM-PATH-CONCLUDES-A-REFLY-IN-FLIGHT.** RF-6 measured that
> nine of the twelve session-gated in-game `Rewind` cells skip on a precondition a live
> re-fly session alone cannot supply, and four of them name the same one: the re-fly must
> be CONCLUDED - landed or crashed, so a terminal state is stamped - not merely flown.
> RF-12 was authored to supply it and its reading run refuted BOTH candidate routes.
> `CommitTree` answers `no-active-tree`, because a re-fly's provisional is an in-place
> fork attached to a PENDING tree while `CommitTreeImpl` commits `ParsekFlight.activeTree`.
> And `TimeJump` - the seam's only clock verb - is an EPOCH SHIFT, not a warp:
> `ParsekFlight.TimeJumpTo` -> `TimeJumpManager.ExecuteJump` stops warp and moves the
> clock instantly with frozen relative positions, so a jump "past the impact" leaves the
> same vessel in the same place with a later clock. There was no wait verb either, so a
> crash that has to happen in real time could not be sequenced against a batch.
>
> **Why it is not a second spelling of TimeJump.** The two drive different mechanisms and
> a spec's wire token has to say which one it used - the identical argument that kept
> `InvokeRewindToLaunch` separate from `InvokeRewind` and that keeps `FlySlot` reserved.
> `TimeJump` moves the clock and freezes the world; `WarpToUT` drives the stock rails rate
> ladder (`TimeWarp.SetRate`) so the world SIMULATES forward and the vessel really flies
> its trajectory, re-enters, and can impact. Both stay implemented; neither is deprecated,
> because a lane that wants a chain tip spawned across a long span without moving the
> vessel still wants the epoch shift, and paying real seconds for it would be a
> regression.
>
> **Deliberately GENERAL, first consumer notwithstanding.** Nothing about the contract is
> re-fly-shaped: any lane that wants real elapsed game time (a decaying orbit, a descent,
> a coast between burns) drives it the same way. The first consumers are RF-12W and
> RF-12L.
>
> Full contract below (`#### WarpToUT`).

> Update (`InvokeRewindToLaunch tree=latest`, 2026-09-02): ONE optional ARGUMENT VALUE,
> no new verb and no table movement (still 31 implemented / 5 reserved). Additive under
> the "readers ignore unknown keys" clause; every existing spec is byte-unaffected.
> Contract below (`#### D12/A2`).

#### D12/A2 - `InvokeRewindToLaunch tree=latest`

**Contract.** The `tree=` argument gains exactly one non-id spelling. `tree=latest`
(matched ORDINAL-IGNORE-CASE, so `latest` or `LATEST`) selects the MOST RECENTLY COMMITTED
tree - last in `RecordingStore.CommittedTrees`, which is append-ordered on commit, so
last-in-list is the definition rather than an approximation. With no committed tree it
answers `REJECTED no-committed-tree` (the world-state verdict the bare call gives for the
same world), never `unknown-tree`, which would blame the argument for an empty save.
Everything else is unchanged: an explicit id still resolves by ordinal-exact match, an
unknown id is still `unknown-tree`, and the BARE no-arg call still refuses
`ambiguous-tree` over several committed trees.

**Two ordering guarantees, both tested.** (1) The id path is untouched: the keyword is
tested only AFTER the exact-id scan fails, so a committed tree literally named `latest`
would still be selected BY ID (`ResolveTarget_ExplicitIdStillWins_EvenForATreeNamedLatest`).
Real ids are 32-hex `Guid` "N" strings, so the collision is unreachable in practice and
the ordering makes it harmless anyway. (2) The keyword does NOT relax the ambiguity rule
(`ResolveTarget_TheKeywordDoesNotRelaxTheAmbiguityRule`): "the operator did not say" and
"the operator said: the newest one" are different intents, and only the second is an
explicit choice. No other near-miss word is accepted - `newest`, `last`, `active`, `first`
all stay `unknown-tree`.

**Why it exists, and it is a MEASURED wall rather than a convenience.** Rewind-to-Launch
needs a subject carrying a launch quicksave: `RecordingStore.CanRewind` resolves the owner
through `GetRewindRecording`, which wants a non-empty `Recording.RewindSaveFileName` on the
recording or its tree ROOT. Every committed recorded fixture ships that field EMPTY by
harvest policy (`harvest_bdock_station.py` prunes `Parsek/Saves` and clears the hint;
`build_rover_route_recorded.py` gates the absence in both directions, with INV9's
dangling-hint WARN as the rationale), so NO committed fixture can ever be the subject. The
one remaining road is for a lane to produce its own subject in-run - `StartRecording` ->
`CommitTree`, which does write the quicksave (`FlightRecorder.CaptureRewindSave` runs at
every non-promotion recording start) - and that road was closed too: a fresh tree's id is a
runtime `Guid`, the harness had exactly one spec-side substitution (`${runSave}`; no
step could consume a prior step's payload until R10's `${step.field}` capture, see
`#### ListHandles`), and on any host that already carries committed
trees the auto-select then refuses `ambiguous-tree`. GS-4 escapes only because its host
fixture has ZERO committed trees, so its mission's own launch leaves exactly one. This
keyword is that gap and nothing else; it is what makes `H58-route-rewind-to-launch` a lane
in which a rewind actually fires. See ROUTE-REWIND-TO-LAUNCH-UNREACHABLE-ON-COMMITTED-FIXTURES.

**Observability.** The applier logs the choice on its own line before the gate:
`invokerewindtolaunch target resolved tree=<id> resolvedBy=<ExplicitId|LatestKeyword|AutoSingle> arg=<raw> committedTrees=<n>`.
The `invoking` line names the tree and root recording but not the RULE, and on a `latest`
run this line plus `committedTrees=` is the only proof the lane rewound the tree it had
just committed rather than one the fixture shipped. Pure decision in
`TestCommandRewindToLaunch.ResolveTarget` (keyword const `LatestTreeKeyword`, resolution
enum `RewindToLaunchTargetResolution`), xUnit-covered in
`TestCommandRewindToLaunchTests.cs`.

#### R12/A1 - `LoadGame scene=<spacecenter|trackstation>`

**Contract.** `LoadGame` gains one optional argument. ABSENT is the pre-R12 contract
verbatim: the SAVE's shape derives the route (focusable active vessel -> FLIGHT, otherwise
the no-vessel SPACECENTER resume). PRESENT, the route is caller-chosen:
`scene=trackstation` boots to TRACKSTATION regardless of vessels; `scene=spacecenter`
forces the KSC resume even on a save that would otherwise fly. Game VALIDITY still outranks
the request - a null / incompatible / flight-state-less game is `ERROR msg=load-failed` on
every route, because a requested scene cannot rescue a save that did not parse.

**Why an argument and not a verb.** Cause C is that scene ENTRY is two-valued; the fix is
making the ENTRY three-valued. No consumer needs a live transition INTO the tracking
station - a TRACKSTATION batch spec boots straight there - and boot-route parity with the
SPACECENTER route is exactly what known-gate 6 demands. The argument is additive under the
"readers ignore unknown keys" clause, so every existing spec is byte-unaffected.

**Parse is fail-closed and case-sensitive**, matching `RunTests`' `isolated=`: only the
lowercase literals `spacecenter` / `trackstation` are accepted, anything else (including
an EMPTY value, `TRACKSTATION`, `ts`, `ksc`, `flight`) is `REJECTED msg=scene-arg-invalid
scene=<raw>`, validated BEFORE the save is read so a bad ARG never presents as a bad SAVE.
This strictness is the B10-shaped fail-open guard: a silently-ignored `scene=` would boot
the DEFAULT route while the spec believes it asked for another one, and a wrong-scene boot
reads GREEN through a batch whose tests all scene-skip. A TRACKSTATION spec must
additionally pin `scene=TRACKSTATION` in its whole `BATCH_COMPLETE` tally.

**`flight` is deliberately NOT an accepted value.** A forced FLIGHT boot is not
expressible (the focusable route needs an in-range `activeVesselIdx` only the save can
supply, so `scene=flight` on a vessel-less save could only mean "fail", which
`load-failed` already says), and it would inherit known-gate 6 - the FLIGHT route
deliberately does not run `UpdateScenarioModules` - a documented product asymmetry that
must not be widened as a side effect of adding a scene argument.

**Boot sequence, VERIFIED against the KSP 1.12.5 decompile rather than assumed** (known-gate
6's own lesson is that each route owns its boot contract and must be re-derived): the
TRACKSTATION route is byte-for-byte the SPACECENTER route's bootstrap with a different
`startScene` - `HighLogic.CurrentGame = game` -> `game.startScene = TRACKSTATION` ->
`GamePersistence.UpdateScenarioModules(game)` ->
`GamePersistence.SaveGame(game, "persistent", save, OVERWRITE)` -> `game.Start()`. Evidence:
`Game.Start()` sends any `startScene` that is neither EDITOR nor FLIGHT to
`HighLogic.LoadScene(startScene)` (the same branch SPACECENTER takes; only FLIGHT gets
`FlightDriver.StartAndFocusVessel`), and `SpaceTracking.Start()` calls
`GamePersistence.LoadGame("persistent", HighLogic.SaveFolder, ...)` then `st.Load()` ->
`HighLogic.CurrentGame = this; ScenarioRunner.SetProtoModules(scenarios)` on THAT disk
game - identical in shape to `SpaceCenterMain.Start()`. So the tracking station also throws
our in-memory game away and re-reads `persistent.sfs`, which makes the pre-`Start()` disk
write just as LOAD-BEARING here: without it `SetProtoModules` instantiates a scenario set
with no `ParsekScenario`, `OnLoad` never runs, and a TRACKSTATION batch runs against a
Parsek that never woke up. **ZERO deltas from the SPACECENTER route were found.**

**Completion.** The pre-R12 two-valued `bool expectSpaceCenter` became an expected
`TestCommandScene` (`TestCommandLoadGame.ExpectedSceneFor(route)`), so each route completes
ONLY on its own settled scene; a MAINMENU settle and the budget expiry keep their meanings
on all three. The success payload's existing `scene` key now reports the landing scene.
Budget unchanged (`LoadGameSeconds = 300`).

#### R12/A2 - `ExitToSpaceCenter` (no args)

**Contract.** FLIGHT-only (dispatch precondition `RequiresFlight`). Drives the live
FLIGHT -> SPACECENTER transition through the stock Space Center exit-button path
(`SceneExitInterceptor.SafeWritePersistent(SPACECENTER)` then
`HighLogic.LoadScene(SPACECENTER)`), so the normal finalize / stash / auto-commit pipeline
runs. Two-phase, LoadGame-style: the terminal is deferred until SPACECENTER settles with a
game loaded -> `OK scene=SPACECENTER`; a MAINMENU settle -> `ERROR
msg=exit-failed-returned-to-menu`; budget expiry -> `ERROR msg=exit-timeout`. Budget
`ExitToSpaceCenterSeconds = 120`, sized like `AnswerMergeDialog` (the only other verb that
DRIVES a scene exit and holds the head across its settle) rather than like `LoadGame`,
which additionally parses a cold save off disk.

**Why a narrow verb and not a generic `LoadScene`.** CL-1 needs the live FLIGHT exit WITH
its side effects: all four pending-tree auto-commit routes are gated on
`HighLogic.LoadedScene != GameScenes.FLIGHT`, and no seam verb produced that transition -
a destroyed active recorded vessel stashes its tree as PENDING and nulls `activeTree`, so
`CommitTree` fails its `HasActiveTree` guard and `FlushAndQuit` saves and quits from inside
FLIGHT (hence that run's final `saving 0 committed tree(s)`). A generic scene loader would
over-promise EDITOR / MAINMENU (MAINMENU always shows the merge dialog under an active
tree, by design) and under-specify this gauntlet.

**WEDGE GUARD (the load-bearing part).** Parsek's own `HighLogic.LoadScene` prefix BLOCKS a
flight exit and spawns a `ControlTypes.All`-locking modal whenever a merge decision is
outstanding, and nothing re-invokes `LoadScene` except that dialog's own `postChoice` - so
a driven exit into that state does not fail, it WEDGES until the step budget expires.
`AnswerMergeDialog` cannot rescue it: it finds its popup through `FindReFlyMergePopup`,
which is `markerLive`-gated to the re-fly dialog alone, so a plain whole-tree merge popup
is structurally unmatchable (that exact combination already cost one
deterministically-INVALID S4.1 run). The verb therefore evaluates the SAME live predicates
the prefix will, against the same destination, BEFORE initiating anything, and refuses with
a typed `REJECTED msg=dialog-required variant=<token>`. Three refusal shapes:

- `variant=RegularMerge` - autoMerge OFF with a live (or pending) tree.
- `variant=ReFlyAttempt` - a live re-fly session marker, which forces the modal even under
  autoMerge.
- `variant=SwitchSegmentSession` - an armed `SwitchSegmentSession` with NO live active
  tree. This one has no `DialogVariant` of its own: the prefix's Bug-C branch routes the
  dialog to the SESSION'S tree without consulting the decision matrix at all, so autoMerge
  does not suppress it. A guard built only on `ShouldShowDialogBeforeSceneChangeLive` would
  read `None` here and wedge.

Plus two dispatch-level rejects mirroring `InvokeRewind`: `load-in-flight` and
`merge-journal-in-flight` (a driven transition must not race the
`MergeJournalOrchestrator` finisher). NOTE the deliberate ABSENCE of a `recording-active`
reject, the one place this verb diverges from `LoadGame`: exiting WITH a live recorder is
the whole point, since the exit is what finalizes the tree and reaches the auto-commit.

The guard is deliberately CONSERVATIVE - a SUPERSET of the wedging states. Three prefix
fast paths (the no-op switch-segment auto-discard, the no-op no-session committed-resume
auto-discard, and the idle-on-pad auto-discard) can convert a would-be-modal into a silent
pass-through, so the guard can refuse an exit the prefix would in fact have allowed. It
stays conservative because all three fast paths both DETECT AND MUTATE: probing them from
a refusal check would tear down a live tree as a side effect of asking a question. A false REJECTED is a typed, side-effect-free answer the orchestrator
classifies immediately; a wedge is an input lock held to the budget.

**Persist-before-LoadScene is mandatory**, for the reason `AnswerMergeDialog` documents
in-code: a raw `HighLogic.LoadScene` models a scene exit that no stock UI route performs
(stock `saveAndExit` saves before the prefix fires), and driving one already produced a
FIXTURE-shaped divergence that presented as a product defect.

**v1 supported shape** (document this for spec authors): `SetSetting autoMerge=true`
earlier in the spec, record something, `ExitToSpaceCenter`, observe the auto-commit. With
autoMerge OFF the verb correctly REJECTS rather than wedging, which is a refusal, not a
regression. The downstream commit outcome is proven through the existing grep-stable
commit / auto-commit log lines, not through this verb's payload.

**Diagnostic logging** (extending the "Per verb specifics" list below):

- `LoadGame` gains `requestedScene=<Unspecified|SpaceCenter|TrackingStation>` on its
  `Info` "loadgame start" line and `expected=<scene>` on all three completion lines; the
  KSC route's line is now `Info` "loadgame spacecenter route (<requested|no-vessel>): ..."
  (it names WHICH selector fired) and the new route logs `Info` "loadgame trackstation
  route: booting to TRACKSTATION save=<folder> protoVessels=N activeVesselIdx=N" plus the
  same persist-result Info/Warn pair the KSC route emits. A bad arg is `Warn` "loadgame
  rejected reason=scene-arg-invalid scene=<raw>".
- `ExitToSpaceCenter`: `Info` "exittospacecenter start scene=<current> autoMerge=<bool>
  hasActiveTree=<bool> hasPendingTree=<bool> switchSegmentSession=<bool>
  activeTreeVariant=<v> pendingTreeVariant=<v>" (the whole guard input set, so a refusal is
  reconstructable from the log alone), then either `Warn` "exittospacecenter refused
  reason=dialog-required variant=<token> gate=<decision> ..." or `Info` "exittospacecenter
  driving scene-exit dest=SPACECENTER persisted=<bool> ...", closing with `Info`
  "exittospacecenter complete scene=SPACECENTER game-loaded=true elapsed=<n>s", `Error`
  "exittospacecenter failed-returned-to-menu ...", or `Error` "exittospacecenter timeout
  ...".

#### R12/B - `SimulateStockSwitchClick` (the promoted reserved verb)

**What it closes.** kRPC's `active_vessel` setter calls `FlightGlobals.SetActiveVessel`
DIRECTLY and bypasses the patched stock handler entirely, so a switch-segment scenario
driven through the mission driver certifies the WRONG code path: no
`StockActionIntentMarker` is armed, the consume site reads `NoIntent`, no segment starts,
and the scenario goes green while `MapFocusObjectOnSelectPatch` rots. This verb reproduces
the CLICK's own contract - the marker, then the same `SetActiveVessel` the patched handler
performs. It is the first PROMOTION out of the reserved list since M-C1 (implemented
20 -> 21, reserved 11 -> 10); the wire token is byte-identical before and after, only the
response changes.

**Contract.** FLIGHT-only (dispatch precondition `RequiresFlight`; the map view is a FLIGHT
overlay). SINGLE-phase: after the unloaded-target refusal below the whole path is
synchronous - `SetActiveVessel` fires `onVesselSwitching` -> `MakeActive` ->
`onVesselChange` (hence Parsek's consume) INSIDE the call - so there is nothing left to
wait for and a two-phase terminal would hold the FIFO head polling for an event that
already happened. At-most-once is unaffected: the pump journals CLAIMED before invoking ANY
executor, so a crash mid-switch replays as INTERRUPTED and never re-switches. It rides the
default 60 s budget, which therefore only ever bounds the `not-in-flight` DEFER. Two
dispatch-level rejects mirror `ExitToSpaceCenter`: `load-in-flight` and
`merge-journal-in-flight`. There is deliberately NO `recording-active` reject - a live
recorder is the PRECONDITION for the switch-segment cases the verb exists to exercise.

**Arguments.**

| arg | values | meaning |
|---|---|---|
| `site` | `map` \| `ts` \| `ksc` | absent = `map`. v1 drives `map` only. |
| `vessel` | exact vessel name (percent-encoded ON THE WIRE; a spec carries the RAW name - `run.py` encodes) | target by name. |
| `pid` | `persistentId` (uint) | target by pid. WINS when both are given. |

`vessel=` exists because live pids are not spec-addressable - a TOML author cannot know the
pid a launch will get - the same stable-addressing problem `InvokeRewind` solved. `pid=`
wins when both are supplied because it is the unambiguous selector (KSP dedups
`persistentId` among CURRENTLY-LIVE vessels; names are freely duplicated), so a spec that
supplies both gets the precise one rather than a refusal to debug. The `site=` parse is
fail-closed and case-sensitive like `LoadGame`'s `scene=`; the `pid=` parse is
`NumberStyles.Integer` + `InvariantCulture`, the seam's one numeric-arg style (shared with
`EvaBoard`'s `targetPid`), which tolerates surrounding whitespace. The asymmetry is
deliberate: a mis-spelled site silently drives the wrong click, stray whitespace around a
number cannot mis-target anything. Target resolution sweeps `FlightGlobals.Vessels` (NOT
`VesselsLoaded` - the unloaded case must be DETECTED and refused with its own reason, not
read as "no such vessel") and excludes Parsek ghosts, which are real entries in that list.

**Typed-error taxonomy.** Every refusal below happens BEFORE any side effect and therefore
rides `REJECTED`, per the seam's existing verdict split (a no-side-effect refusal is
`REJECTED`, a terminal after the verb ACTED is `ERROR`). The two `ERROR`s are the only
post-arm outcomes.

| verdict | `msg` | when |
|---|---|---|
| REJECTED | `site-arg-invalid site=<raw>` | `site=` present and not one of the three spellings (incl. empty, `MAP`, `trackstation`) |
| REJECTED | `site-not-implemented site=<ts\|ksc>` | a known site v1 does not drive |
| REJECTED | `target-arg-missing` | neither `vessel=` nor `pid=` |
| REJECTED | `pid-arg-invalid pid=<raw>` | `pid=` present and not a uint |
| REJECTED | `vessel-arg-invalid vessel=` | `vessel=` present but empty |
| REJECTED | `target-not-found <pid=N\|vessel=X>` | no live vessel matched |
| REJECTED | `target-name-ambiguous vessel=<name> matches=<n>` | more than one live vessel carries that name |
| REJECTED | `target-is-ghost <pid=N\|vessel=X>` | the only matches were Parsek ghost map vessels |
| REJECTED | `scenario-not-ready` | `ParsekScenario.Instance` is null (nothing can hold the marker) |
| REJECTED | `cannot-switch-vessels-far` | the Prefix's gate 5 is off, so stock would not arm at all |
| REJECTED | `target-already-active` | the target IS the active vessel (a guaranteed no-op) |
| REJECTED | `dialog-required case=<A-session\|B-unloaded\|C-loaded-separate-committed>` | a pre-switch merge dialog would spawn |
| REJECTED | `dialog-pending` | a merge dialog is already open (the Prefix's re-entry branch) |
| REJECTED | `target-unloaded` | the target is out of the physics bubble (v1 scope) |
| ERROR | `switch-threw` | `SetActiveVessel` threw |
| ERROR | `switch-refused-by-stock` | the call returned but the active vessel is not the target |

Three of those deserve their reasons stated:

- **The dialog cases are refusals, never driven dialogs.** The real Prefix has three dialog
  branches - Case A (armed `SwitchSegmentSession`, different target), Case B (no session, an
  in-flight recording, unloaded target), Case C (no session, an in-flight recording, a
  LOADED separate previously-committed target) - plus a re-entry branch when a popup is
  already open. A seam verb cannot answer a `ControlTypes.All`-locking modal
  (`AnswerMergeDialog` is `markerLive`-gated to the re-fly popup alone), so each is a typed
  refusal naming the case. The classification comes from the patch's OWN
  `DecidePreSwitchDialogAction`, called with the same seven inputs the Prefix computes,
  including the Case C committed-tree lookup - a restatement of the predicate would drift
  out of step with the product's case analysis, and Case C would have been missed entirely.
  The case tokens are byte-identical to the Prefix's own `openCase` log string.
- **`target-already-active` and `switch-refused-by-stock` exist to prevent a green no-op.**
  Stock `SetActiveVessel` returns false without switching for an already-active vessel, six
  `ClearToSave` reasons (not in atmosphere, under acceleration, moving over surface, about
  to crash, on a ladder, throttled up), and a non-`Owned` discovery level. The first is
  knowable before arming and refuses; the rest are caught AFTER the call by comparing the
  OBSERVED active vessel against the target.
- **`target-unloaded` is v1 scope, not a bug.** An unloaded target sends stock through
  `onVesselSwitchingToUnloaded` + `FlightDriver.StartAndFocusVessel` - a FLIGHT scene reload
  that the 2 s `MapSwitchTo` TTL cannot survive, and which by DESIGN starts no segment (the
  patch's Postfix clears `refused-no-switch` and the new scene has no in-scene marker). It
  would also make an otherwise-synchronous verb straddle a scene load. Refusing keeps v1
  in-bubble and honest. `dialog-required case=B-unloaded` outranks it when a recording is
  live, so the design's named case is what a spec sees.

**Marker fidelity (the load-bearing guarantee).** The marker is built FIELD-FOR-FIELD as
`MapFocusObjectOnSelectPatch` builds it at both of its arm sites: `Action = MapSwitchTo`,
`SourceScene = Flight`, `TargetVesselPersistentId` = the target's pid, `CapturedRealtime` =
`Time.realtimeSinceStartup`, `CapturedUT` = `Planetarium.GetUniversalTime()` behind the same
null guard, `ProcessSessionId` = `ParsekProcess.ProcessSessionId`, `IntentId` = a fresh
GUID. **The TTL is not an input and must never become one**: it is derived from `Action`
(2 s for `MapSwitchTo`) and re-evaluated by the consume site's `EvaluateStaleness` on every
path. Lengthening it, or arranging to skip that evaluation, would certify a marker lifetime
no player click can produce - which is the very failure the verb exists to close. A unit
cell pins the marker's field SET so an eighth field added later cannot be silently left at
its default by the factory.

The verb also performs the patch's **Postfix-equivalent cleanup itself** - it has no Harmony
Postfix - clearing the marker with the identical `refused-no-switch` reason under the
identical "still armed under MY IntentId" guard. Without it a refused switch would leave an
armed marker for the NEXT switch in the same 2 s window to mis-consume.

**Consume-outcome contract.** The payload reports what was ARMED (`intentId`, `targetPid`,
`targetName`, `site`, `route`) and what was OBSERVED after the call (`activeVesselPid`,
`switched`) - never what the consume site DECIDED. The consume runs synchronously inside
`SetActiveVessel` and is already fully instrumented (`TryConsumeStockActionIntent` logs its
route, and on a refusal `FormatRefusalDiagnostic` plus the clear reason), so re-deriving the
outcome into the payload would duplicate a grep-stable signal and invite the two to
disagree. The verb does not wait for it. The observed pair is not optional decoration: a
payload that reported only "we armed and called" would be a commanded-not-observed claim,
and `switched=false` is what turns a stock refusal into a red.

**NOTE FOR SPEC AUTHORS - the surface-target trap.** The consume REFUSES surface targets by
design (`Refused_OnSurfaceTarget` / clear reason `on-surface-defer-to-trigger`, deferring to
the normal auto-record trigger): the switch still happens and the verb still reports
`OK switched=true`, but NO segment arms. Every existing harness fixture launches from the
pad, so a switch-segment scenario that targets a landed/pre-launch/splashed vessel will read
green and certify nothing. A live proof of segment-arming must target a genuinely FLYING /
ORBITING vessel and assert on the consume LOG line, not on this verb's verdict.

**What v1 leaves for later**: `site=ts` / `site=ksc` (both cross scenes into a fresh FLIGHT
load and must go through their own patched handlers - the TS one runs
`RemoveAllGhostVesselsBeforeStockFly`, a live-list/saved-file index desync fix a hand-rolled
`FlightDriver.StartAndFocusVessel` would reintroduce), unloaded targets, and any
dialog-driving route.

**Diagnostic logging**: `Info` "switchclick start site=<token> selector=<pid=N|vessel=X>
scene=<current> activeVesselPid=<n>", then `Info` "switchclick gate targetPid=<n>
targetName=<name> scenarioReady=<bool> canSwitchVesselsFar=<bool> targetIsActiveVessel=<bool>
targetIsUnloaded=<bool> hasActiveRecording=<bool> hasActiveSession=<bool> priorFocusedPid=<n>
anotherDialogOpen=<bool> targetIsSeparateCommittedVessel=<bool> dialogDecision=<d>" (the
whole gate input set on one line, so a refusal is reconstructable from the log alone). Then
one of: `Warn` "switchclick rejected reason=<msg> ..." (arg / site / resolution), `Warn`
"switchclick refused gate=<decision> reason=<msg> targetPid=<n> ...", or the action pair
`Info` "switchclick armed intent: intentId=<guid> action=MapSwitchTo targetPid=<n>
sourceScene=Flight capturedUT=<R> ttl=2s route=plain-arm-and-switch" followed by `Info`
"switchclick complete site=map targetPid=<n> targetName=<name> intentId=<guid>
route=plain-arm-and-switch activeVesselPid=<n> switched=true". Failure tails: `Info`
"switchclick cleared own marker intentId=<guid> reason=refused-no-switch ...", `Warn`
"switchclick failed reason=switch-refused-by-stock targetPid=<n> activeVesselPid=<n>
setActiveVesselReturned=<bool> ...", `Error` "switchclick SetActiveVessel threw ...", and the
Case C `Verbose` pair.

#### SealSlot (the fifth promoted reserved verb)

**Contract.** Precondition `RequiresGameLoaded` - NOT `RequiresFlight`, and the choice is
load-bearing rather than permissive. The verb reads and mutates SAVE-scoped stores only
(`RecordingStore.CommittedTrees`, `ParsekScenario.RewindPoints`) and touches no vessel,
camera or scene; the Unfinished Flights window it reproduces is open in FLIGHT and at the
KSC alike, and the lane that needs it most runs AFTER an `ExitToSpaceCenter`, where a
`RequiresFlight` row would defer to its budget and `TIMEOUT`. SINGLE-PHASE: `TrySeal` and
its persist are synchronous, so the read-back (recount the tree) is a final answer - no
`TryComplete*` counterpart, no `DeferralBudget` row, the 60 s default bounding only the
game-not-loaded defer. Two dispatch-level rejects mirror `ExitToSpaceCenter`:
`merge-journal-in-flight` and `load-in-flight`. There is deliberately NO `recording-active`
reject - the verb acts on COMMITTED state and the fly -> commit -> seal -> create lane runs
with a recorder live.

**It drives the production path, and that is the whole point.** Every seal goes through
`UnfinishedFlightSealHandler.TrySeal`, which is what the UI button calls: resolve the
recording's rewind point + child slot, flip the slot's EFFECTIVE chain+supersede tip to
`Immutable`, mark the tip's sidecars dirty, call
`ParsekScenario.BumpSupersedeStateVersionLive()` (the ERS-cache invalidation AND the
`RouteStore.RevalidateSources` call, in one), persist the game, and only then reap a
now-orphaned rewind point. A seam that flipped the enum itself would skip all five and
leave the very candidacy sweep this verb exists to unblock reading a stale cache.

**Arguments - two spellings, one verb.**

| arg | values | meaning |
|---|---|---|
| `tree` | committed `RecordingTree.Id` | seal EVERY open member of the tree |
| `rp` | `RewindPointId` | the D9 slot spelling, with `slot=` |
| `slot` | slot index (int >= 0) | `ChildSlot.SlotIndex` under `rp=` |

The reserved NAME is a slot verb (the D9 unfinished-flights lifecycle, beside `StashSlot`
/ `FlySlot`), so `rp=` + `slot=` is kept and reuses `InvokeRewind`'s `unknown-rp` /
`unknown-slot` vocabulary verbatim - including that verb's documented choice that an
ABSENT arg is just an unresolvable target rather than a separate missing-arg reason. The
CONSUMER that forced the promotion is tree-scoped, though: a fixture typically carries
more than one open provisional and candidacy needs them all closed, so `tree=` seals the
whole tree in one command. **`rp=` wins when both are supplied**, mirroring
`SimulateStockSwitchClick`'s `pid=`-beats-`vessel=` rule and for the same reason: a caller
who supplies both gets the precise selector rather than a refusal to debug.

**IDEMPOTENT, and a spec must be written against that.** An already-sealed tree answers
`OK sealed=0 alreadySealed=true`, never a reject - the RVR-2 roadmap sketch uses
`SealSlot` as a no-op guard over fixtures whose trees are already sealed, and a refusal
there would red a healthy lane.

**Payload.** Tree mode: `tree`, `mode=tree`, `total`, `sealed`, `failed`, `remaining`,
`alreadySealed`, `fullySealed`. Slot mode: `mode=slot`, `rp`, `slot`, `tip`, `sealed`,
`alreadySealed`, `fullySealed`. `sealed` counts what THIS command flipped and
`alreadySealed` says whether anything needed flipping, so a lane can tell "my step sealed
it" from "it was already sealed" rather than from an absent key (the map-view pair's rule).

**Typed-error taxonomy.**

| verdict | `msg` | when |
|---|---|---|
| REJECTED | `target-arg-missing` | neither `tree=` nor `rp=` |
| REJECTED | `unknown-slot` | `rp=` present and `slot=` absent / unparseable / negative / unmatched |
| REJECTED | `unknown-rp` | no rewind point carries that id |
| REJECTED | `unknown-tree` | no committed tree carries that id |
| ERROR | `seal-incomplete <firstReason>` | the tree pass ran and left members open |
| ERROR | `seal-refused <handlerReason>` | slot mode: the production handler declined (`tip-unresolvable`, `slot-index-invalid`, ...) |
| ERROR | `no-scenario` | slot mode with no `ParsekScenario.Instance` |

The three ERRORs are the post-act-or-product-declined half of the seam's verdict split, not
the "no side effect yet" half REJECTED owns. `seal-incomplete` is literally post-act
(earlier members may already be sealed and the game persisted). `seal-refused` and
`no-scenario` fire before any flip, and they are ERROR for the OTHER half of the same rule:
the verb reached the production handler (or found the scenario singleton absent) and
something that is not the seam declined - `InvokeRewind`'s own `no-scenario` row and the
`switch-refused-by-stock` precedent. A REJECTED on either would claim the seam declined to
act.

**One tree-mode outcome deserves its own reading, because it looks like a handler failure
and is not.** `TrySeal` never seals the recording it is handed - it flips that recording's
slot's EFFECTIVE chain+supersede tip. A tree can therefore carry an open member that is not
its own slot's tip (`RecordingStore.CommitTree` demotes both a qualifying HEAD and a chain
tip), and for such a member every `TrySeal` SUCCEEDS while the member stays
`CommittedProvisional` forever. The pass reports `ERROR seal-incomplete member-not-slot-tip`
with `failed=0 remaining=N` - the distinct token exists precisely so an operator does not go
hunting a refusal that never happened. It is a FIXTURE-SHAPE answer: that tree cannot be
made a route candidate by sealing alone, and the UI's Seal button has the identical residue.
A lane author reading it should change the fixture, not the verb.

**No new `_SEAM_REFUSAL_SUBKINDS` rows are needed**, and that is a property of the design
rather than an oversight: every REJECTED token above is already mapped for another verb,
and the table is keyed by msg token alone, never by (verb, msg). The two ERRORs stay
UNMAPPED on purpose (the `switch-refused-by-stock` precedent).

**Diagnostic logging:** `Info` "sealslot start mode=<m> rp= slot= tree=", then per-member
`Warn` "sealslot member-refused tree= rec= reason=" on a decline, closing with `Info`
"sealslot complete mode=<m> ... alreadySealed=<bool>" or `Error` "sealslot incomplete
tree= total= sealed= failed= remaining= firstReason=" / "sealslot refused rp= slot= tip=
reason=". The production handler's own `Sealed slot=... mergeState=X->Y ... reaped=N` line
still fires per seal, so a lane pins THAT for the state change and this verb's line for
the command outcome.

#### RouteCommand (the sixth promoted reserved verb)

**Contract.** Precondition `RequiresGameLoaded` and the same two dispatch rejects, for the
reasons `SealSlot` states above (a merge journal mid-finalize is rewriting the supersede
list a candidacy walk reads; a `LoadGame` would swap the stores out between the gate read
and the mutation). SINGLE-PHASE: build + store + orchestrator arm are all synchronous
state mutation, so the read-back (the route's own status) is a final answer. Sub-commands
dispatch on an `action=` arg, exactly like `KscAction`.

**Every action drives the production path.** `create` goes through
`RouteCreationService.CreatePausedFromCandidate` - a new `internal static` funnel holding
the build + store + manual-loop-clear sequence that used to live inline in
`LogisticsWindowUI.CreateRouteFromCandidate`, a PRIVATE instance method on a UI window.
The window now calls the same funnel, so the two cannot drift: identical
`RouteBuilder.BuildRoute` call shape (`initialStatus: Paused`,
`allowIntervalBelowTransit: true`), identical `RouteStore.AddRoute`, identical
`RouteTreeGuard.ForceClearManualLoopForRoute`. What stayed in the window is presentation
only - the interval resolve, the one-shot "manual loop turned off" toast (the funnel
returns the cleared count so that decision is unchanged), and its own grep-stable
`Logistics: Create Route from candidate ...` Info line. The three operations go through
`RouteOrchestrator.TrySendOneCycleNow` / `TryPause` / `TryActivate`, which are what the
window's row buttons and the in-game `RouteRewindTimeline` cells call.

**Arguments.**

| arg | values | meaning |
|---|---|---|
| `action` | `create` \| `send-once` \| `pause` \| `activate` | required; fail-closed and case-sensitive |
| `tree` | committed `RecordingTree.Id` | `create` only; the candidate tree |
| `name` | route name (percent-encoded ON THE WIRE; a spec carries the RAW name) | `create` only; absent lets `RouteBuilder` generate one |
| `interval` | seconds, InvariantCulture, finite and > 0 | `create` only; absent = the route's snapped minimum |
| `route` | route id, unique id PREFIX, or exact name | the three operations |

`interval=` absent resolves through `RouteCreationDialog.ComputeRootToUndockSpan`, the SAME
helper both production create paths funnel their default through, so a driven create and a
player create produce the identical interval for one analysis. Whatever is passed is then
snapped by the builder to `N * TransitDuration` with `N >= 1`.

**The `route=` selector is three-tiered** - exact `Id`, then unique `Id` PREFIX, then
unique exact `Name` - and a tier matching more than once is `route-ambiguous` rather than
an arbitrary pick. The prefix tier exists because a route id is a bare
`Guid.NewGuid().ToString("N")` minted at create time and therefore not spec-pinnable; what
a lane HAS is the 8-char short id every log line prints (`RouteIds.Short`) or the create
step's own OK payload. The exact-id tier runs first and alone, so an id that IS a route
wins even if it also prefixes another - the only way a precise handle can never be made
ambiguous by an unrelated route appearing later. Duplicate NAMES are ordinary rather than
exotic (`RouteBuilder` generates default names), which is why the name tier refuses an
ambiguity instead of picking.

**Typed-error taxonomy.**

| verdict | `msg` | when |
|---|---|---|
| REJECTED | `missing-arg` | `action=` absent |
| REJECTED | `unknown-action` | `action=` present and not one of the four |
| REJECTED | `tree-arg-missing` | `create` without `tree=` |
| REJECTED | `interval-arg-invalid` | `interval=` present and not a finite positive double |
| REJECTED | `unknown-tree` | no committed tree carries that id |
| REJECTED | `candidate-dismissed` | the candidate was dismissed in the Logistics window |
| REJECTED | `tree-not-sealed` | not every recording is Immutable - run `SealSlot` first |
| REJECTED | `candidate-ineligible <RouteAnalysisStatus>` | route analysis rejected the tree |
| REJECTED | `candidate-already-promoted` | a stored route already claims the source recording |
| REJECTED | `route-build-rejected <builderReason>` | `RouteBuilder` declined (nothing was stored); e.g. `source-no-longer-eligible`, `endpoint-missing`. NOT `interval-below-transit`: the funnel passes `allowIntervalBelowTransit: true`, so a short interval is CLAMPED rather than refused |
| REJECTED | `route-arg-missing` | an operation without `route=` |
| REJECTED | `unknown-route` | no stored route matched the selector |
| REJECTED | `route-ambiguous` | more than one stored route matched |
| ERROR | `route-action-refused <action>` | the orchestrator declined (wrong status for the action) |

**The create refusals are walked in `RouteCandidateFinder.DeriveCandidates`' OWN gate
order** (dismissed -> sealed -> eligible -> already-promoted) so the token names the FIRST
gate that closed and a lane's fix is the right one: `tree-not-sealed` wants an earlier
`SealSlot` step, `candidate-ineligible` means the FLIGHT did not produce a promotable
supply run and is a fixture question. The analysis status rides as a compound tail
(`candidate-ineligible%20MissingRouteProof` on the wire, the `refly-gate` shape) so a lane
learns WHY the analysis rejected rather than merely that it did; the harness classifies off
the head token, so the tail costs nothing there.

**`route-action-refused` is ERROR while every create refusal is REJECTED**, and the line is
drawn exactly where `SimulateStockSwitchClick` draws it: the create refusals all happen
BEFORE any side effect, whereas the operation refusal happens because the verb reached the
production call and the PRODUCT declined (`TryActivate` is Paused-only;
`TrySendOneCycleNow` refuses `InTransit` / `EndpointLost` / `SourceChanged` /
`MissingSourceRecording`; `TryPause` refuses an already-Paused route). A REJECTED there
would claim the seam declined to act. NOTE for spec authors: `pause` on an already-paused
route is therefore an ERROR, not a green no-op - deliberately, because an OK would make the
assertion vacuous.

**Payload.** `create`: `action`, `created=true`, `route`, `name`, `tree`, `status`,
`intervalSeconds`, `transitSeconds`, `cadence`, `stops`, `manualLoopsCleared`. Operations:
`action`, `route`, `name`, `match` (which selector tier hit), `applied`, `statusBefore`,
`status`. The before/after status pair is not decoration - it is what turns a silent
no-op into something a lane can assert on.

**What v1 leaves for later:** `delete` / `dismiss` / `link` / cadence edits. Each is a
separate production surface with its own confirmation dialog and none is on the
create-and-run path this verb exists to unblock; all are additive under the seam's
"readers ignore unknown keys" clause.

**Diagnostic logging:** `Info` "routecommand start action= tree= route= name= interval=",
then for `create` the whole gate input set on one line - `Info` "routecommand create gate
tree= treeFound= dismissed= sealed= eligible= status= alreadyPromoted= refusal=", so a
refusal is reconstructable from the log alone - closing with `Info` "routecommand complete
action=... " or `Warn` "routecommand rejected reason=..." / "routecommand refused action=
route= statusBefore= status=". The shared funnel emits its own `[Route]`
"CreatePausedFromCandidate created tree= route= name= status= interval= transit= cadence=
manualLoopsCleared=" line, which is what a spec pins for the creation itself.

#### DeleteRecording (additive; the Recordings-table per-row delete)

**Contract.** Precondition `RequiresGameLoaded` and the logistics pair's two dispatch
rejects (`load-in-flight`, `merge-journal-in-flight`, load-first), for the same reasons: a
merge journal mid-finalize is rewriting the supersede rows and committed list a delete
removes from, and a `LoadGame` would swap the store out between the bound check and the
removal so the index would name a different recording. NO `recording-active` guard: the
verb acts on COMMITTED rows and the table offers the delete with a recorder live.
SINGLE-PHASE: the removal is a synchronous list mutation whose notifications fan out
inside the call, so the read-back is a final answer and the verb rides the 60 s default
budget.

**Every route is a production entry point.** The verb reproduces
`RecordingsTableUI.DeleteGhostOnlyRecording`'s scene branch and adds the table's other
delete:

| scene | row | production call |
|---|---|---|
| FLIGHT (flight host present) | `IsGhostOnly` | `ParsekFlight.DeleteGhostOnlyRecording(index)` - the "X" button |
| FLIGHT (flight host present) | any other | `ParsekFlight.DeleteRecording(index)` - behind `CanDeleteRecording`, checked HERE first |
| anything else (SPACECENTER, TRACKSTATION) | any | `RecordingStore.DeleteRecordingFull(index)` - the KSC branch |

All three end in `RecordingStore.RemoveRecordingAt`, which degrades chain siblings, deletes
the sidecar files and removes the row through `RemoveCommittedAtWithNotifications` - the
one primitive every mid-list mutation shares - so the flight / KSC / TS hosts shift their
index-keyed state exactly as they do for a player's click.

**Arguments.** `index=<n>` REQUIRED: a non-negative InvariantCulture integer naming a
committed-LIST index (raw, not ERS - the hosts key on the same list; the applier file is
`[ERS-exempt]` for that reason). No auto-select and no id selector: a delete with no
target is a spec error, and `RecordingState` exposes no committed ids, so a lane pins the
index off its fixture's bytes the way V22K pins its tree id.

**Verified by read-back, never by the call returning.** All three production calls are
`void` and swallow a refusal with a Warn (an out-of-range index, a not-ghost-only row on
the ghost-only path, a blocked flight delete). The verdict therefore rests on the row being
GONE from the committed list afterwards - by REFERENCE, since the old index now names the
row's former neighbour and an id can be null or duplicated - else `ERROR
delete-not-applied`.

**Typed-error taxonomy.**

| verdict | `msg` | when |
|---|---|---|
| REJECTED | `index-arg-invalid` | `index=` absent, negative, fractional, locale-comma or unparseable |
| REJECTED | `index-out-of-range` | `index >= committed.Count` (no side effect) |
| REJECTED | `flight-delete-blocked` | FLIGHT, non-ghost-only row, `CanDeleteRecording` false (a live recorder or a tracked chain continuation); typed here because the production call refuses silently |
| ERROR | `delete-not-applied` | the routed call returned with the row still present |

The harness maps the two index refusals to `driver-arg` through the existing
`_SEAM_REFUSAL_SUBKINDS` rows (keyed by msg token, shared with EnterWatchMode).

**Payload (OK).** `index`, `recId`, `vessel`, `ghostOnly`, `route`
(`store` | `flight-ghost-only` | `flight-full`), `committedBefore`, `committedAfter` - the
count pair is there because `RecordingState` reports no committed count, so this is the
only seam-side read of the removal.

**Diagnostic logging.** `Info` "deleterecording start index= recId= vessel= ghostOnly=
route= committed= scene=", then "deleterecording complete index= recId= route=
committedBefore= committedAfter="; `Warn` "deleterecording rejected reason=..." and `Error`
"deleterecording error reason=delete-not-applied ...". The production path's own lines
still fire and are what a spec pins for the removal itself: `[RecordingStore]
DeleteRecordingFull: deleting '<name>' at index N` (store route), `Removed recording
'<name>' (id=...) at index N`, and the hosts' shift lines - `[KSCGhost] KSC ghost state
reindexed after committed removal at #N: primary= overlapSets=` (Verbose, and only when a
KSC ghost was alive) or `[Flight] Committed recording #N removed - reindexed engine, held,
map, watch and chain state`.

**Harness roles.** `SEAM_VERB_TAIL_ROLE`: world-mutating (it destroys durable recorded
data - `DiscardTree`'s reasoning). `SEAM_VERB_POST_MISSION_ROLE`: `recording` (its OK is a
read-back of Parsek's own store, never a kerbal claim).

**First consumer.** `S0.11-ksc-table-delete`: V22K's boot into SPACECENTER with the loop
member rendered, a short dwell, `DeleteRecording index=1`, a longer dwell - pinning the
KSC host's reindex line with the deleted index literal.

#### ListHandles (additive; the R10 handle-list verb)

**What it closes.** Until R10 nothing a driven run could read named a LIVE object. Every
verb that addresses one - `InvokeRewind rp=`, `SealSlot rp=`, `SimulateStockSwitchClick
pid=`, `DeleteRecording index=` - took an id the spec author had to know in advance, and a
live id is a fresh `Guid` (RewindPoints, recordings, trees) or a launch-assigned
`persistentId` (vessels). S1.5's own header names the wall exactly: "a seam channel
exposing the live RewindPoint id (InvokeRewind matches RewindPointId EXACTLY and live ids
are fresh GUIDs ...; RecordingState's payload is recording/tree/points/scene only)". The
harness half of the answer is the `${step.field}` substitution in
`design-autotest-harness-core.md` ("Runtime handles"); this verb is the seam half: an
OBSERVATION verb whose payload is a flat, index-suffixed enumeration of one handle
family, so a later step can name a member by `${<step>.rp0}` and the harness carries the
live id onto the wire.

**Why a new verb rather than a wider `RecordingState`.** `RecordingState` is a
FLIGHT-flavoured four-field snapshot that eleven committed lanes and the R1 mission
machine read by exact key; widening it would put an unbounded list on every one of those
lines and every one of those readers. A list verb with a REQUIRED `kind=` keeps each
family on its own bounded line and leaves the four-field payload byte-identical. It is
the `ExportRenderManifest` shape: ADDITIVE (31 -> 32 implemented, reserved unchanged at
5), never in the reserved envelope, read-only with respect to the game world.

**Contract.** `RequiresGameLoaded` (`ParsekScenario.Instance` and the committed store
exist in every loaded scene; the `active` family answers with an empty tree outside a
live FLIGHT rather than deferring). SINGLE-PHASE (a synchronous walk of in-memory state),
so it rides the 60 s default budget, which only ever bounds the game-not-loaded defer.
No side effects, so every refusal is `REJECTED` and there is no `ERROR` terminal.

| arg | values | meaning |
|---|---|---|
| `kind` | `rewindpoints` \| `committed` \| `active` | REQUIRED. Which handle family to enumerate. Fail-closed, case-sensitive (the `LoadGame scene=` rule): absent is `REJECTED kind-arg-missing`, anything else is `REJECTED kind-arg-invalid kind=<raw>`. Mirrored in `hlib.VERB_SCOPED_CLOSED_ARGS` so a typo is INVALID(spec-invalid) before a boot. |

**Payload grammar.** Every value is `InvariantCulture` and percent-encoded on the wire
like every other payload; keys are `<family><i>` and `<family><i><attr>` with `i`
zero-based in a DEFINED order, so a spec can name the first / the newest member without
knowing its id. Each family carries `count=<total>` and `truncated=<bool>`: the payload
is CAPPED (16 rewind points x 8 slots, 32 committed recordings, 16 background members)
because a seam line is a single wire token list, and `truncated=true` is the reader's
signal that `count` exceeds what was enumerated - never a silent cut.

- `kind=rewindpoints count=<n> truncated=<b> rp<i>=<RewindPointId> rp<i>ut=<UT>
  rp<i>provisional=<b> rp<i>corrupted=<b> rp<i>slots=<m> rp<i>slot<j>=<OriginChildRecordingId>
  rp<i>slot<j>open=<b>` in `ParsekScenario.RewindPoints` list order (append order, so
  `rp<count-1>` is the newest). `open` is true iff the slot's EFFECTIVE tip
  (`ChildSlot.EffectiveRecordingId` over `RecordingSupersedes`, the SealSlot lookup)
  resolves to a committed recording that is not `MergeState.Immutable`; an unresolvable
  tip reads `open=false`, since nothing could be invoked or sealed through it. The
  family's single `truncated` flag covers BOTH caps (a slot cut cannot show in `count`,
  so it raises the flag too), and `rp<i>slots` always reports the untruncated total.
- `kind=committed count=<n> truncated=<b> rec<i>=<RecordingId> rec<i>tree=<TreeId>
  rec<i>pid=<VesselPersistentId> rec<i>spawnedPid=<SpawnedVesselPersistentId or 0>
  rec<i>name=<vessel name> rec<i>spawned=<b> rec<i>state=<MergeState>` walking
  `RecordingStore.CommittedTrees` in list order and each tree's recordings
  ordinal-sorted by id (a dictionary walk has no order contract). Two pids per row on
  purpose: `pid` is the craft-baked `persistentId`, reused by every launch of the same
  craft, while `spawnedPid` is the KSP-unique pid of the really-spawned vessel (0 until a
  spawn). This is the family a D18 `committed-interaction-claiming` lane reads, and
  `rec<i>spawnedPid` (never `rec<i>pid`) is the `SimulateStockSwitchClick pid=` a
  committed-spawned-clone switch needs.
- `kind=active tree=<TreeId or empty> activeRec=<RecordingId or empty> activePid=<pid or 0>
  bg=<n> truncated=<b> bg<i>pid=<pid> bg<i>rec=<RecordingId>` from the live
  `ParsekFlight` tree's `BackgroundMap`, members sorted by pid ascending. This is the
  family the D5 `chain-continuation-switch` lane reads: `bg<i>pid` is the switch target
  the consume site's bg-member-continuation route requires
  (`activeTree.BackgroundMap.ContainsKey(newPid)`, ParsekFlight `TryConsumeStockActionIntent`).

**Observability.** One Info line per call, `listhandles kind=<k> count=<n>
truncated=<b>` (a refusal logs `listhandles rejected reason=<r> kind=<raw>` at Warn, the
way every other verb's refusal does). The enumerated ids themselves are NOT in
KSP.log (the pump's `exec id=<id> verdict=OK` line carries no payload): they live in
the response channel `parsek-test-responses.txt`, which the harness collects with the
run, and in the harness's own `captured` / `substituted` log lines and result-record
rows. The consumer verb's log is what puts the ACTED-ON id into KSP.log (`invokerewind
start rp=<id> slot=<n>` and `Re-Fly (Rewind-to-Separation) StartInvoke: sess=<s>
rp=<id>` for the first consumer), so the identity proof is the response line's `rp0=`
against those two, measured on `2026-09-08_0844_RH-1-live-rp-handle-rewind`.

**Pure decision.** `TestCommandListHandles` (`ParseKind`, the three `Build*Payload`
builders over plain DTO rows, the caps as named constants), xUnit-covered in
`TestCommandListHandlesTests.cs`. The partial `ParsekTestCommandAddon.ListHandles.cs`
only walks the live objects into DTOs. ERS note: the walk reads `CommittedTrees`, the
un-audited tree surface SealSlot already walks, never the audited flat lists.

**Role tables.** `SEAM_VERB_TAIL_ROLE`: `inert` (RecordingState's reason: a read that
changes nothing, safe on an unmet tail). `SEAM_VERB_POST_MISSION_ROLE`: `recording` (its
OK is a read-back of Parsek's own store).

**First consumer.** `RH-1-live-rp-handle-rewind`: `bdock-recorded` (three harvested
RewindPoints with real `Guid` ids and their quicksaves on disk, three
`CommittedProvisional` slot tips), `ListHandles kind=rewindpoints` labelled `handles`,
then `InvokeRewind rp=${handles.rp0} slot=<the open slot>` - the first driven rewind
whose target id was never written into a spec.

#### WarpToUT (additive; the REAL rails warp)

**What it closes.** See the Update note above:
RF12-NO-SEAM-PATH-CONCLUDES-A-REFLY-IN-FLIGHT. The short form is that the seam could move
the CLOCK but not the WORLD, and every terminal-gated in-game `Rewind` cell needs a
provisional that reached an ending.

**Contract.** `RequiresFlight` - a hard precondition, not a convenience: rails warp is a
flight-scene mechanism and the whole content of the verb is that the ACTIVE VESSEL travels
while the clock advances. It is a DEFER on not-in-flight (the wrong-scene case is
overwhelmingly a scene still settling in from the previous step), like every other
FLIGHT-only verb. TWO-PHASE, and its completion is a genuine POLL rather than a settle:
`TimeJump` lands its clock synchronously and only watches the spawn queue drain, while
this verb watches a clock that advances over many frames. Budget 540 s
(`DeferralBudget.WarpToUTSeconds`), which is the harness's own
`MAX_DEFERRED_STEP_BUDGET_SECONDS` and is MEASURED rather than guessed: RF-12W's first
reading run covered 270 game-seconds in 73 real seconds while the re-flown craft was above
~30 km, and then stock's altitude ceiling dropped to rate index 0 with the craft still
descending, so the remainder ran at 1x. An initial 300 s (InvokeRewind's size) could not
cover that and would have ERRORed `warp-timeout` on a warp working exactly as designed. It
is a `DEFERRED_SEAM_VERB` on the harness side, so the same 540 s per-step cap governs any
budget a spec declares.

| arg | values | meaning |
|---|---|---|
| `ut` | InvariantCulture float | REQUIRED, ABSOLUTE, and strictly in the future. Absent / unparseable is `REJECTED missing-warp-target` (a locale comma such as `600,0` fails: InvariantCulture only); non-finite or beyond 1e12 is `REJECTED target-out-of-range`; at or before now is `REJECTED backward-warp`. |
| `maxRate` | InvariantCulture float `>= 1` | OPTIONAL cap on the rails rate the ladder may select. Absent means uncapped. Present-but-unparseable or below 1 is `REJECTED max-rate-invalid` - FAIL-CLOSED rather than ignored, so a mis-typed cap can never read as an uncapped warp. |

**Why `ut` is absolute-only, unlike `TimeJump`'s `ut` / `deltaSeconds` pair.** A warp's own
duration depends on the clamps stock applies, so a delta-relative target would land
somewhere the spec author cannot name in a log contract. An absolute target is a number a
`required` row can pin.

**There are TWO ladders, and the applier picks the live one.** Stock keeps `warpRates`
(rails) and `physicsWarpRates` (physics warp, `TimeWarp.Modes.LOW`) as separate arrays
sharing one index space with very different values: rails index 3 is 50x, physics index 3
is 4x. RF-12W's reading run 2 logged `rate=4 rateIndex=3` inside the atmosphere, which is
what surfaced the pair. `SafeWarpRates` re-reads `TimeWarp.WarpMode` every frame (stock
switches ladders on its own as a vessel climbs out of the atmosphere) and hands the live
array to the pure selector, which never looks either up itself; `SafeMaxRateIndexForActiveVessel`
answers with `maxPhysicsRate_index` in LOW mode, because the altitude limits are a RAILS
concept that does not apply to the physics ladder.

**The ladder is advisory; the read-back is truth.** Stock owns the real clamps - a body's
rails altitude limit, a vessel under acceleration, an SOI-transition guard - and applies
them inside `TimeWarp.SetRate`. The pure `TestCommandWarpToUT.SelectRateIndex` picks the
highest rung whose rate is within the optional cap AND can still run for
`MinRealSecondsAtRate` (2.5 s, stock's own `WarpTo` default `minTimeWarping`) before the
target arrives, bounded by the ceiling stock publishes
(`TimeWarp.GetMaxRateForAltitude`, lifted for a landed vessel and for no active vessel).
The applier then reads `TimeWarp.CurrentRateIndex` back and logs `requested=` beside
`applied=` and `clamped=`. **A clamp is therefore NOT a refusal.** It is a slower warp, and
in the limit (a vessel under drag inside the atmosphere, pinned to 1x) the verb degrades
into a real-time wait that the budget bounds. That is deliberate: refusing there would make
the verb useless on the exact lane it was built for, and `maxRate=1` in the terminal
payload is how a reader tells a waited span from a warped one. Only a warp that cannot be
driven AT ALL is `REJECTED`: no `TimeWarp` controller (`warp-unavailable`) or a stock input
lock on `ControlTypes.TIMEWARP` (`warp-locked`).

**The warp is always lowered, and the OK is the proof.** The mirror-direction obligation
(CLAUDE.md: a fix derived from an asymmetry must be checked in the mirror direction) for a
verb that RAISES warp is that it must LOWER it deterministically. Three mechanisms, in
increasing order of bluntness: the ladder selector walks itself down on approach and
returns rung 0 inside the last 2.5 s purely as a function of the shrinking span; the
applier force-sets rate 0 the instant the target is reached; and the timeout and exception
paths force rate 0 BEFORE their terminal, so a run that classifies INVALID off this verb
cannot hand a warped game to whatever runs next. Crucially the SUCCESS predicate itself
requires `currentRateIndex <= 0`
(`TestCommandWarpToUT.DecideWarpCompletion`), so an OK terminal is evidence the rate came
down rather than an assumption about it - mutating that clause out reds exactly one xUnit
cell, `DecideWarpCompletion_StillWaitingWhileTheGameIsStillWarped`.

**Terminal payload.** `ut=<reached UT> target=<captured target> delta=<target - startUT>
maxRate=<highest rate actually observed>`, all InvariantCulture round-trip ("R"). The
timeout terminal is `ERROR msg=warp-timeout`.

**Observability.** All at `[Parsek][INFO][TestCommands]`, InvariantCulture:
`warptout start ut=<t> delta=<d>s maxRate=<c> rate=<r> ceilingIndex=<i>` once;
`warptout rate requested=<i> applied=<j> rate=<r> ceilingIndex=<c> clamped=<bool>
remaining=<s>s ut=<t>` on each ladder CHANGE (bounded by the ladder's own length, so it is
plain Info rather than rate-limited); `warptout dewarp reason=<r> fromIndex=<i> rate=<r>`
whenever the rate is forced down; and `warptout complete reachedUT=<u> ut=<t> rate=<r>
maxRate=<m> elapsed=<e>s` on success. Refusals log `warptout refused reason=<r> ...` at
Warn and the timeout logs at Error.

**Pure decision.** `TestCommandWarpToUT` (`ResolveTargetUt`, `ResolveMaxRate`,
`IsForwardWarp`, `EvaluateFeasibility`, `HasReached`, `SelectRateIndex`,
`DecideWarpCompletion`, `BuildCompletePayload`, and the refusal-reason constants),
xUnit-covered in `TestCommandWarpToUTTests.cs`. The partial
`ParsekTestCommandAddon.WarpToUT.cs` only samples live KSP state and calls
`TimeWarp.SetRate`.

**Role tables.** `SEAM_VERB_TAIL_ROLE`: `world-mutating`, and in the strongest sense in
that table - it does not merely move a clock, it simulates the world forward, so a vessel
can re-enter, break up or impact inside the verb. `SEAM_VERB_POST_MISSION_ROLE`:
`recording`, deliberately, even though the verb can end a vessel: its OK means "the clock
reached the target and warp came back down", a statement about the SEAM rather than about
a kerbal's physical in-world state, which is the whole content of the `outcome` set. What
the warp did to the vessel is asserted from the terminal-state log lines a spec pins - the
`ExitToSpaceCenter` carve-out verbatim.

**First consumers.** `RF-12W-rewind-batch-after-warp-crash` (warp a re-flown atmospheric
half past its impact so `ApplyTerminalDestruction` stamps `Destroyed` in flight, then run
the 39-cell `Rewind` batch inside the still-live session) and
`RF-12L-rewind-batch-after-landing` (the same shape against a conclusion the mission
library flies).

#### RF-3/A1 - `LoadGame allowLiveRecorder=refly`

**Contract.** `LoadGame` gains one optional argument. ABSENT is the pre-RF-3 contract
verbatim, including the `recording-active` dispatch reject. PRESENT and equal to the
literal `refly`, the load is admitted past that reject - and ONLY when the runtime state
also carries a live re-fly session marker (`DispatchState.ActiveReFlyMarker`, the bit
`AnswerMergeDialog` already reads). With the arg and NO marker, a live recorder still
refuses `recording-active`, unchanged and with the same token: the argument buys nothing
on its own. `load-in-flight` is untouched on every path.

**Why the guard needed an opening at all.** A quickload taken while a re-fly session is
live is a state ORDINARY PLAY reaches - CLAUDE.md's `ReFlyProvisionalBinding` entry lists
it explicitly ("reachable from ordinary play (rewind, F5/F9, conclude without flying)"),
the in-game cell `F5MidReFlyResume` exists for it, and the todo entry
`REFLY-BATCH-BASELINE-DISCARDS-LIVE-SESSION` has held the question of what it does open
since 2026-08-11. The seam could not reach it: `S4.4-refly-quicksave-mid-session`'s
reading run 1 measured `reject id=0005 cmd=LoadGame reason=recording-active` and its
header recorded the boundary in the spec's own words - the seam "cannot reproduce
F9-mid-refly without either stopping the recorder first (which changes the experiment) or
a guard-bypass the seam deliberately does not offer".

**Why not just `StopRecording` first,** which needs no C# at all: because a load WITH a
live recorder is the event the guard exists to describe. Stopping the recorder produces a
different event that happens to reach the same disk, and the whole subject is what the
product does to a session across a reload it did not expect. The alternative was
considered and rejected on exactly that ground.

**Why an argument and not a verb** (R12/A1's reasoning, verbatim in shape): the fault
being fixed is that the dispatch guard is two-valued when the state it guards is
three-valued - no recorder, a recorder, and a recorder running while a re-fly session
marker is live. Said precisely, because the code says no more than this: the guard reads
marker PRESENCE (`DispatchState.ActiveReFlyMarker`, the same bit `AnswerMergeDialog`
reads), NOT that the live recorder belongs to that session. A stale or synthetic marker
would therefore admit a load over an unrelated recorder. That is accepted rather than
overlooked: the whole surface is seam-only (`PARSEK_TEST_COMMANDS=1`), and `LoadTimeSweep`
culls zombie markers at OnLoad. A binding check would need the marker's tree id, which is
`ReFlySessionMarker.ResolveInPlaceContinuationTarget`'s own guard and not this verb's. The argument is additive under the "readers ignore unknown
keys" clause, so every existing spec is byte-unaffected, and the guard's strength for
every caller that does not pass it is literally unchanged code.

**Parse is fail-closed and case-sensitive**, matching `scene=` and `RunTests`' `isolated=`
(`TestCommandLoadGame.TryParseAllowLiveRecorder`): only the lowercase literal `refly` is
accepted; anything else - including an EMPTY value, `ReFly`, `true`, `1` - is `REJECTED
msg=allow-live-recorder-arg-invalid`, evaluated BEFORE the recorder term so a typo can
never be masked by a state in which the opt-in would not have been needed. There is
deliberately no `true` / `any` spelling: the value names WHICH live-recorder state it
excuses, so a future second case is a second value carrying its own state term rather than
a widening of this one.

**Where it lives.** Entirely inside the pure `TestCommandDispatcher.DecideDispatch`
`case "LoadGame"`. No executor change, no payload change, no new wire token beyond the
argument itself and its one refusal reason. Mirrored harness-side by
`hlib.LOADGAME_ALLOW_LIVE_RECORDER_KEY` in `VERB_SCOPED_CLOSED_ARGS` (so a case-variant
key, a bad value or the arg on the wrong verb is INVALID(spec-invalid) before a boot) and
by an `allow-live-recorder-arg-invalid` row in the reject-reason class map. Adding it
required lowering that validator's case-variant comparison on BOTH sides - it is the
table's first non-lowercase key, and `key.lower() == arg_key` could never have fired for
it.

**Diagnostics.** The refusal side Warns with its reason like every other dispatch reject.
The ADMIT side is logged on `loadgame start`, which carries `allowLiveRecorder=<raw|(none)>`
and `recorderLive=<true|false>` unconditionally - without them a load admitted past the
guard is indistinguishable in `KSP.log` from a load taken with no recorder at all, which is
exactly the guard-condition-skip the logging rule exists for. It is also what lets a spec
pin the F9 half on a token instead of on a step verdict.

**Coverage.** `TestCommandDispatchTests` walks the conjunction one leg at a time (arg with
marker executes; arg without marker still refuses `recording-active`; marker without arg
still refuses; three bad values and one bad value with no recorder at all refuse
`allow-live-recorder-arg-invalid`; the arg does not relax `InvokeRewind`'s identical token
or `LoadGame`'s own `load-in-flight`), and `TestCommandLoadGameTests` pins the parse.

**First consumer.** `S4.4-refly-quicksave-mid-session`, re-shaped into the F5+F9
experiment its header describes: the bare reload keeps `expect = "REJECTED"` as a live
negative control for the guard, and the very next step reloads the same quicksave with the
opt-in. The pair is the mutation test carried inside the flight.

#### CaptureScreenshot (additive; one PNG into the harvested directory)

`CaptureScreenshot label=<filename-safe name> [superSize=<1-4>]`. Precondition
`AnyScene`, the `ExportRenderManifest` row: a screenshot is meaningful in every settled
scene (a census walks KSC and FLIGHT), and the dispatcher's safe-point gate already
refuses to dispatch during LOADING, a scene transition or the settle window - which is
exactly when a capture would photograph a black frame. Rides the 60 s DEFAULT budget and
is deliberately absent from `DEFERRED_SEAM_VERBS`, the `EnterWatchMode` shape: it IS
two-phase, but its completion lands in a frame or two, and a capture that has not landed
within a minute is broken rather than slow.

**The target directory is not a choice.** `run.py`'s artifact leg harvests
`<instance>/Screenshots/` and nowhere else, so the path is derived from
`KSPUtil.ApplicationRootPath` + `Screenshots` and the directory is created when absent (a
fresh provisioned instance may not have one until the first F1). A capture written
anywhere else would exist on the operator's disk and never reach a result folder. The
payload reports the RELATIVE path (`Screenshots/<label>.png`, forward slashes) because the
absolute one names a machine.

**Two agreeing size samples before OK.** A first sighting can be a partially written file -
Unity encodes the PNG into the same path the poll reads - so "exists and non-empty" is not
proof of a complete image, and a truncated PNG in a contact sheet reads as a Parsek render
defect rather than as a harvest artifact. The poll therefore requires the same non-zero
size on two consecutive frames. `Settled` is decided BEFORE the budget, so a capture that
landed on the very frame the budget expired is a success rather than a red over a file
sitting in the artifact folder.

**A colliding label is overwritten, not refused.** Re-running a census over the same
instance re-uses the labels, so the applier DELETES a colliding target before calling the
engine - otherwise the poll would see a settled file immediately and report OK for the OLD
image - and reports `overwrote=true`. A REJECTED there would make every second run
useless.

**`superSize` defaults to 1 on purpose.** Unity's supersize path re-renders through the
cameras at the multiplied resolution, and screen-space IMGUI is not guaranteed to survive
that - which would silently drop the very windows a census exists to photograph. The arg
is offered for a scene shot that wants the detail; the census lanes stay at 1 until a run
proves otherwise.

**The engine call is a plain compile-time call.** `UnityEngine.ScreenCapture` lives in
`UnityEngine.ScreenCaptureModule.dll`, and `Parsek.csproj` now references it as an
eleventh Unity module. The worry that made the first draft reflective was real but
UNMEASURED: the cloud / CI build resolves its reference DLLs from the PRIVATE
`vl3c/ksp-refs` repo rather than from a KSP install, and a reference that repo does not
carry is a hard compile error on the required `tests` check. That repo DOES carry it
(`KSP_x64_Data/Managed/UnityEngine.ScreenCaptureModule.dll`, checked with `gh api` over
the repo contents, and `cloud-test.sh` copies the whole `Managed` directory next to the
test host so runtime resolution is covered too), so the reflection bought nothing and
cost something: a missing API could only ever surface as a run-time `REJECTED` after a
whole KSP boot, and a real build dependency was hidden behind a string. There is
therefore NO `screenshot-api-unavailable` terminal - a missing API is a build failure.

**Terminals.** `REJECTED`: `label-arg-missing` (required, never defaulted - an invented
name would collide across steps and silently overwrite an earlier capture),
`label-arg-invalid` (the label becomes a filename in the harvested directory, so the rule
is `^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9-])?$`, max 96 - the harness's own filename-safe
id shape at the head, which excludes every path separator, `..`, whitespace and every
non-ASCII byte, plus one TIGHTENING at the tail: no trailing `.` or `_`, because the paired
`DumpGuiTree`'s writer runs `GuiTreeRecorder.SanitizeLabel`, which trims exactly those two,
so `ksc-settings_` would name three different files across the pair. A trailing `-` is not
trimmed and stays legal), `supersize-arg-invalid` (rejected rather than clamped: a typo that
silently captured at 1x would read as a resolution problem),
`screenshot-dir-unavailable`. `ERROR`:
`screenshot-not-written` (the budget expired with no settled file) and `screenshot-threw`
(kept apart from the silent paths for the reason `SimulateStockSwitchClick` keeps
`switch-threw` apart from `switch-refused-by-stock` - a throw is a different
investigation). `OK` payload: `label`, `path`, `bytes` (the SETTLED size, so a reader tells
a real capture from a 0-byte stub without opening the folder), `superSize`, `overwrote`
(always present, both values - the `EnterMapView alreadyOpen` rule).

**Pure decision.** `TestCommandCaptureScreenshot` (`IsValidLabel`, `TryParseLabel`,
`TryParseSuperSize`, `DecidePoll`, `RelativePathFor`, `BuildPayload`), xUnit-covered in
`TestCommandCaptureScreenshotTests.cs`. The partial
`ParsekTestCommandAddon.CaptureScreenshot.cs` owns the path resolution, the pre-delete, the
reflective invoke and the `FileInfo` polls, and nothing else.

**Tail / post-mission roles.** `inert` and `recording`. `inert` is not a borderline call:
it writes ONE file under the KSP root and touches no vessel, no save, no career and no
Parsek persisted state - the `ExportRenderManifest` row exactly. Mirrored in the C#
`NonMutatingVerbs` set (so `FlushAndQuit`'s save suppression is not cleared by a capture),
and a harness cell reads that set out of the source to keep the two from drifting.

#### UiAction (additive; drive the Parsek windows for a capture)

`UiAction op=<open|close|tab|complexity|rect|describe> [window=] [tab=] [mode=]
[x= y= w= h=]`. Precondition `RequiresGameLoaded`, NOT `RequiresFlight`, and the choice is
the `ListHandles` one: the Parsek UI is hosted in SPACECENTER as well as FLIGHT, so a KSC
census under a flight row would defer to its budget and TIMEOUT. A scene that hosts no
Parsek UI at all - the Tracking Station draws markers only (`ParsekTrackingStation.OnGUI`)
and no editor host exists - is the verb's own `REJECTED ui-host-unavailable`, not a defer:
the dispatcher already waited for a loaded game, and waiting longer cannot make a host
appear.

**It drives internal state rather than synthesising clicks.** Each window's open flag is a
plain field behind `IsOpen` that the main window's own buttons write; the tab selectors are
plain ints. Faking a click would need an IMGUI event injected into a specific window's
layout at a specific rect - not reproducible, and not better proof of anything, because
the button handler's whole body IS the field write. The seam writes the same fields and
READS THEM BACK.

**TWO OPS ARE TWO-PHASE: `open` and `rect`** (`TestCommandUiAction.OpIsTwoPhase`), and
neither is optionally so. Both write a value whose read-back only means something after an
OnGUI pass has run, so before a frame is DRAWN both read back exactly what was just
written and prove nothing. For `open` that is not academic: `SpawnControlUI.DrawIfOpen`
force-closes itself on its FIRST draw whenever `ResolveAutoCloseReason` fires - and
"ZERO nearby spawn candidates" is the normal state of any craft with nothing recorded
passing close by - so a single-phase OK let a census photograph empty scenery under the
label `flight-spawncontrol-advanced`, which a reviewer reads as a render defect. For
`rect` it was worse in a quieter way: the tolerance predicate below was comparing a field
with the value assigned to it one line earlier, so it could not fail on ANY input. Both
ops hold the FIFO head for ONE drawn frame (`DecideSettlePoll`, `SettleFrames = 1`;
Unity runs `Update` - where the pump lives - before `OnGUI`, so one advanced frame
guarantees a full IMGUI pass) and then read the live state back.

**`close`, `tab`, `complexity` and `describe` stay SINGLE-PHASE.** `close` is out of the
two-phase set after walking the MIRROR DIRECTION rather than by symmetry: a drawing window
can LOWER its own flag UNPROMPTED, while nothing raises one WITHOUT A PLAYER CLICK. Draw
paths DO raise open flags - the RouteRunPrompt banner's "Open Logistics" button inside
`ParsekUI.DrawWindow` (`ParsekUI.cs:809`), `RecordingsTableUI.ShowMissionForRecording` /
`ScrollToRecording` (`:467` / `:521`, reached from the Missions digest GoTo and the two
Timeline GoTo buttons), and `StructureListWindowUI.OpenForMission` / `OpenForRoute`
(`:89` / `:100`, reached from the Missions "Log" and Logistics "Log (Route)" /
"Log (Mission)" buttons) - but every one of those sites is a `GUILayout.Button` handler,
and the seam synthesises no clicks. So no drawn frame in an unattended run raises a flag
the seam just lowered, and there is no self-opening window a settled close read-back could
catch. `tab` is out because its one
live clamp (`RecordingsTableUI`'s Basic tab clamp) runs from the complexity LATCH, which
the applier drives synchronously in `Update`, not from a draw. `complexity` is the one
that LOOKS deferred: `ParsekUI.SetUiComplexityMode` persists the value and only QUEUES the
draw-visible one, which a later `Update` latches through
`ApplyPendingUiComplexityModeIfAny`. The seam pump runs in `Update`, which is exactly
where that latch is contractually allowed to be called (never from OnGUI), so the applier
calls it directly and reads back `AppliedUiComplexityMode`. The production latch stays the
only path that applies a mode, and the op needs no `TryComplete*` counterpart.

**`op=complexity`'s no-op test reads BOTH the latch and the persisted setting**
(`IsComplexityAlreadySatisfied`), because `SetUiComplexityMode` no-ops on the SETTING: a
save that persisted Basic under the fail-open Advanced latch (a `ParsekUI` constructed
before `ParsekSettings.Current` existed) looked "not already satisfied" on a Basic
request, called a setter that queued nothing, and then ERRORed `complexity-not-applied` on
the mode the save actually carried. When the setting is already right and only the latch
has drifted, the applier calls the new `ParsekUI.TryRequeuePersistedUiComplexityMode()` -
which can only ever queue the value the settings object already holds, so it adds no way
to apply a mode the save does not carry and no way around `ShouldRefuseModeChange`.

**The window vocabulary is a table, in the main window's own button order** (so a describe
payload reads down the same list a reviewer sees on screen): `main`, `missions`,
`timeline`, `kerbals`, `career`, `logistics`, `structure`, `settings`, `spawncontrol`,
`gloops`, `testrunner`. `main` is in it because every sub-window draw in both hosts sits
inside the host's `showUI` gate, so a sub-window with `IsOpen = true` and the main window
hidden is invisible - `op=open window=main` is the first step of any census, and the only
op that touches the scene host rather than `ParsekUI`. `spawncontrol` and `gloops` are
FLIGHT-ONLY (`ParsekKSC.OnGUI` draws neither), and asking for either at the Space Center
is a `REJECTED window-not-in-scene` NAMING THE SCENE: an "opened" Gloops recorder there
would produce a capture of the scene without it, which reads as a render defect rather
than as a spec that asked for the wrong scene.

THREE WINDOW HOSTS ARE DELIBERATELY EXCLUDED, and the list comes from a grep of every
`ClickThruBlocker.GUILayoutWindow` / `GUILayout.Window` site under `Source/Parsek` rather
than from memory, so "eleven windows" is a claim about the whole program: `GroupPickerUI`
("Set Parent Group" / "Manage Groups" - a real window with its own rect and input lock,
and IN the Advanced -> Basic close set), `LogisticsWindowUI`'s round-trip LINK PICKER, and
`TestRunnerShortcut` (the global Ctrl+Shift+T window, which shares the Test Runner title
but is a separate MonoBehaviour with no accessor and no complexity gate - the `testrunner`
token is the Settings-launched `TestRunnerUI`). The first two are excluded for the same
reason as each other: they are popups over a SELECTION (a recordings row, a logistics
row), so raising their flag with nothing armed would photograph an empty picker - the
`GUI-CENSUS-STRUCTURE-WINDOW-HAS-NO-DRIVEABLE-TARGET` shape, and worse. Adding any of the
three needs a way to drive its CONTEXT first, not just a table row.

**Tabs, and the two windows that only look tabbed.** Four windows carry a selector:
`missions` (`missions`, `recordings`), `timeline` (`overview`, `details`, `rewindff`,
`refly` - its filter modes, four mutually exclusive views of one window, which is the same
thing as tabs for a census), `kerbals` (`roster`, `outcomes`), `career` (`contracts`,
`strategies`, `facilities`, `milestones`). Settings has six SECTIONS that all draw in one
pass, three of them Basic-hidden, so the Advanced/Basic capture PAIR is its section
coverage; Logistics' Active / Paused / Dormant / Candidate bubbles are expand-collapse
rather than a selector. `op=tab` on either is `REJECTED window-has-no-tabs`, deliberately
distinct from `tab-unknown`: "this window has no tabs" and "this window has tabs but not
that one" send an author to different fixes.

**`op=rect` and its ASYMMETRIC read-back.** The op exists so a census can place and
enlarge a window and show more rows than the default size fits. All four of `x/y/w/h` are
required - a partial rect mixes a commanded position with a stale size, so the capture
would not be reproducible - and each is a finite invariant-culture float in range (a
zero-width window photographs as a MISSING window, which is the exact false reading a
census must not produce). The read-back happens after the settle frame, on the rect the
window's own draw resolved - which is what makes the predicate a measurement rather than
an echo - and it checks position and WIDTH to a 1 px tolerance but
HEIGHT AS A FLOOR, because every one of these is a `GUILayout` window and resolves to
`Max(passedHeight, contentMin)`: a window whose content is taller legitimately grows, and
strict equality would ERROR on a rect that was applied exactly as asked. The MAIN window
is exempt from the size half entirely (BOTH hosts pass a fixed `GUILayout.Width(250)` and
BOTH zero its height every frame - `ParsekFlight.OnGUI` and `ParsekKSC.OnGUI` each open
with `windowRect.height = 0f`), so only its position can be commanded. The
asymmetry is walked in the mirror direction too: a height BELOW the commanded floor is
still a failure.

**`op=describe` is the reviewer's inventory**, and it is what a supervising agent reads
next to the images: `op=describe scene=<token> complexity=<mode> count=<n>` then, per
window in table order, `w<i>`, `w<i>avail`, `w<i>open`, `w<i>rect`, `w<i>tabs`, `w<i>tab`.
Its LOG line carries a second, coarser form that the payload does not need and a SPEC
does: `open=<n> openWindows=<comma-joined names, or ->`. The per-window keys are on the
WIRE, which never reaches `KSP.log`, so an `[expectations.logContracts]` regex could not
assert which windows were open at all. Paired with the count, the list is an EXACT claim -
`open=2 openWindows=main,missions` cannot match a scene with a third window open, because
the count would differ - which is what makes a `describe`-after-`open` step a cheap in-run
check that the four window-token -> live-object mappings are wired to the windows they
name. The labelled screenshot is still the final instrument; this catches a mis-wire
before anyone opens the folder.
EVERY window gets a row including the ones this scene does not draw, and every row carries
all six keys: an absent row would be indistinguishable from an older seam build, and a
reader comparing a KSC describe with a FLIGHT one needs the flight-only rows
present-and-unavailable rather than missing. Absent values report a `-` sentinel rather
than an empty value, because a trailing `key=` on the wire is easy to misread as a
truncated line. An unmeasured rect (a window whose rect field is still all-zero before its
first draw seeds the default from the main window's position) reports `rect=-` rather than
`0,0,0,0`, which would send a reader hunting an off-screen window. The list is NOT capped,
unlike `ListHandles`: this one is bounded BY CONSTRUCTION at the table's length, a
compile-time literal.

**Terminals.** `REJECTED`: `op-arg-missing` / `op-arg-invalid` (the message carries the
valid op list), `window-arg-missing` / `window-unknown` (the message carries the whole
valid window list, which is the only place a spec author learns the spelling without
reading the source), `window-not-in-scene`, `tab-arg-missing` / `tab-unknown` (message
carries THAT window's tabs) / `window-has-no-tabs`, `mode-arg-missing` /
`mode-arg-invalid`, `rect-arg-missing` / `rect-arg-invalid`, `ui-host-unavailable`, and
`complexity-refused-gloops-recording` - the ONE production refusal
(`ParsekUI.ShouldRefuseModeChange`: switching to Basic while a Gloops recording runs would
hide the window without stopping the recorder), checked PRE-CALL through a new
`ParsekUI.WouldRefuseModeChange` so the response NAMES the cause instead of inferring it
from a failed read-back (the `EnterWatchMode` discipline). `ERROR`, all post-call:
`window-not-toggled` (the flag read back wrong IMMEDIATELY, i.e. the window's own setter
declined the value outright), `window-self-closed` (the flag WAS raised and a window that
drew itself put it back down - kept apart from the previous one because they send an
operator to different places: a setter refusing is a Parsek-side defect in the window
class, while a window self-closing is a statement about the SCENE the lane is flying,
whose remedy is a different host), `tab-not-applied` (reachable live - Basic clamps the
Missions window's tab back to index 0, so `tab=recordings` cannot hold there, and a census
that believed it photographed the Recordings tab in Basic photographed Missions),
`complexity-not-applied`, `rect-not-applied`, `ui-action-not-settled` (the budget expired
before a single frame was drawn: the renderer stopped or the pump never reached another
safe point - NOT a refusal, which is why it is not spelled like one),
`window-host-hidden` (see below), `ui-action-threw`.

**THE SETTLE'S HOST-VISIBILITY GATE (`window-host-hidden`).** A frame count says a frame
HAPPENED, never that this window was in it. Both hosts gate the WHOLE Parsek surface
behind their own `showUI` - `ParsekKSC.OnGUI` returns at `if (!showUI) return;` (`:229`,
with the pause gate right after at `:234-235`) and `ParsekFlight.OnGUI` draws the window
only inside `if (showUI)` (`:2096` / `:2108`) - and every sub-window draw sits inside that
gate. So with `main` closed, a settled `open` or `rect` on a SUB-window read back a value
no draw pass had touched, which is the "comparing a field with itself" defect the
two-phase settle exists to remove. `rect` is the worse half: an unresolved rect still
holds exactly the commanded value, so `RectAppliedWithinTolerance` PASSES and the census
then photographs a scene with no Parsek window in it. The settle therefore refuses a
non-`main` `open` / `rect` while the host's `showUI` is false, as a terminal `ERROR
window-host-hidden` (its own token rather than folded into `ui-action-not-settled`,
because the remedy is different: open `main` first, not "the renderer stopped"). `main`
itself is EXEMPT and must be - its open flag IS that `showUI`, so gating it would refuse
the op that turns the surface on. Decision: `TestCommandUiAction.SettleRefusedForHiddenHost`
(pure, and it fails CLOSED on an unrecognised token). Both settle log lines now carry
`hostShowUi=<true|false>`, so a reader can tell a settled read-back's premise from the
log alone. NOT gated: the PAUSE overlay, which suppresses the `main` draw too and so is a
different shape from this window-scoped refusal - and nothing an unattended run drives
opens the Esc menu. Both census specs open `main` as their first `UiAction` step, which
is now a stated PRECONDITION of any non-`main` `open` / `rect` rather than a convention.

**Pure decision.** `TestCommandUiAction` (the window / op / mode / tab tables,
`TryParseOp`, `TryResolveWindow`, `IsAvailableInScene`, `TryResolveTab`, `TryParseMode`,
`TryParseRect`, `RectAppliedWithinTolerance`, `OpIsTwoPhase`, `DecideSettlePoll`,
`SettleRefusedForHiddenHost`, `IsComplexityAlreadySatisfied`, `FormatOpenWindowList`,
and every payload builder),
xUnit-covered in `TestCommandUiActionTests.cs`.

The partial `ParsekTestCommandAddon.UiAction.cs` owns ONE resolver -
`ResolveWindowHandle(ui, window)`, returning a small `UiWindowHandle` adapter (open
get/set, rect get/set, tab get/set, the last pair null for a window with no selector) -
plus the settle completion and nothing else. The first draft had FOUR parallel switches
over the same eleven names, which is the shape to avoid here for a specific reason: a
mapping error (the kerbals arm reaching the career window's field) lived in one of eight
arms while the other seven stayed right, and the whole xUnit suite passed either way,
because the pure half never touches a live window and nothing headless could witness it.
One row per window makes that class of error a single site - and the spec-side
`describe`-after-`open` step above is the in-run check that the rows point where they say.
The mapping cannot live on the pure side at all: that half must stay free of
`UnityEngine` / `ParsekUI` references, which is what lets xUnit exercise it without KSP.

**Accessors added** (all `internal`, no new player-facing surface): a
`WindowRectForTesting` get/set on each of the ten window classes (`SettingsWindowUI`
already had the getter), `SelectedTabForTesting` + `TabCountForTesting` on
`KerbalsWindowUI`, `TierFilterModeIndexForTesting` on `TimelineWindowUI` (an INT over the
private filter-mode enum, so the enum stays private), `GetLogisticsUI()` /
`GetStructureListUI()` on `ParsekUI` (the two sub-windows with no accessor, because
neither is in the Advanced -> Basic close set), `ParsekUI.WouldRefuseModeChange`,
`ParsekUI.PersistedUiComplexityMode` + `ParsekUI.TryRequeuePersistedUiComplexityMode()`
(the setting-vs-latch pair the `complexity` no-op test needs; the second is deliberately
not a general mode setter - it can only queue the value the settings object already
holds, and its decision is the pure
`ParsekUI.TryDecidePersistedUiComplexityRequeue(applied, persisted, out queue)`,
xUnit-covered in `UiComplexityModeCloseHandlerTests.cs` along with the live wrapper's
drift / agree / no-settings arms. The REQUESTED mode is not an input: the only caller
that has one calls `SetUiComplexityMode` first, so the setting already equals the request
by the time it gets here),
`ParsekFlight.MainWindowRectForTesting`, and `ShowUIForTesting` +
`MainWindowRectForTesting` on `ParsekKSC` (whose `showUI` had no accessor at all - only
the two toolbar callbacks wrote it). The KSC host is found with
`FindObjectOfType<ParsekKSC>()` rather than a new static `Instance`: a UiAction step runs
a few times per run, and adding a static plus its lifecycle would be a bigger change - and
a new stale-reference risk across scene loads - than this caller justifies.

**Harness-side validation.** `op` / `window` / `mode` are `VERB_SCOPED_CLOSED_ARGS` rows
(taking that table from five to eight), and two dedicated validators own what a flat table
cannot express: `validate_ui_action_step` holds the per-op REQUIREDNESS (`op` itself, the
window for the four window-ops, `tab` for `op=tab`, `mode` for `op=complexity`, all four
coordinates for `op=rect`), flags an arg an op does not read, and validates `tab` against
THAT WINDOW's own vocabulary - the typo class this exists for is
`tab = "recordings"` on the `career` window, a real tab name on a DIFFERENT window, which
a flat closed set over the union of every tab token would pass and which would then cost a
whole boot. `validate_capture_screenshot_step` mirrors the label and superSize parses;
`label` is deliberately NOT a closed-arg row because `MissionMark` already owns that arg
name and that table asserts one owner verb per key.

The mirror itself is checked rather than trusted:
`GuiCensusSeamVerbTests.test_the_window_and_op_vocabularies_mirror_the_c_sharp_tables`
PARSES the C# `WindowTable` - anchored to the uncommented array assignment, with `//`
comments stripped from the region, because that table's header comment quotes window
names, tab names and window titles verbatim - and pins `UIACTION_WINDOW_VALUES` against it
as an ORDERED list and each window's tab tuple against its `NewSpec` row IN ORDER. Order
is load-bearing twice: the window order is the order a describe payload and a census's
capture labels read in, and each tab's INDEX is the value the live selector field takes,
so a reordered vocabulary would photograph the wrong tab under the right label. A sibling
cell drives the parse over a SYNTHETIC source carrying decoy `NewSpec(...)` text inside
comments, so the comment-stripping half cannot go vacuous.

**Tail / post-mission roles.** `world-mutating` and `recording`, and the tail role is NOT
about opening windows: `op=complexity` PERSISTS `uiComplexityMode` through
`ParsekSettingsPersistence` and then runs the Advanced -> Basic gated-window close set and
the Missions tab clamp, which is `SetSetting`'s own row. The other ops are weaker (a
window open flag is transient), but the table is per-VERB and its fail-safe direction is
world-mutating, so the honest label costs nothing - the unmet tail skips `inert` too.

**First consumers.** `GUI-1-census-ksc` (nine KSC windows, 22 captures across Advanced and
Basic) and `GUI-2-census-flight` (the flight-only windows plus the flight form of the main
window). Both on an OPERATOR-LOCAL fixture, both never flown; see
`harness/fixtures/local-saves/README.md` for why a census host cannot be committed.

#### DumpGuiTree (additive; one IMGUI control tree beside the PNG)

`DumpGuiTree label=<filename-safe name>`. Precondition `AnyScene`, `CaptureScreenshot`'s
row and for a slightly different reason: the recorder intercepts the PROCESS's IMGUI
funnels rather than Parsek's, so a dump is meaningful wherever anything draws, and the
safe-point gate already excludes LOADING, a transition and the settle window - which is
when nothing worth recording is on screen. Deliberately NOT `RequiresGameLoaded` like its
census partner `UiAction`: that verb drives PARSEK's windows and needs a save behind them.
Rides the 60 s DEFAULT budget and is absent from `DEFERRED_SEAM_VERBS`, the
`CaptureScreenshot` shape.

**What it is for.** `CaptureScreenshot` gave a GUI review pixels; this gives it structure,
and the two are driven as a PAIR under one label so a reviewer can read the tree beside the
image. The recorder's own design doc (`design-gui-tree-dump.md`) owns the mechanism - 17
opt-in Harmony interceptions of UnityEngine IMGUI funnels, applied at arm and removed when
the capture flushes, so a disarmed recorder costs the process nothing.

**The `label` rule is `CaptureScreenshot`'s, by delegation rather than by copy.**
`TestCommandDumpGuiTree.IsValidLabel` CALLS the capture verb's predicate and re-exports its
two reject tokens (`label-arg-missing`, `label-arg-invalid`). A census pairs one dump with
one PNG under one label, so a label one verb accepted and the other refused would leave a
PNG with no tree beside it. The rule is also strictly TIGHTER than the recorder's own
`SanitizeLabel` AT BOTH ENDS - it requires an alphanumeric first character and refuses a
trailing `.` or `_`, which is exactly what `SanitizeLabel`'s closing `.Trim('.', '_')`
removes - so an accepted label survives sanitisation unchanged and the path the payload
reports is the path the recorder writes. Pinned in the mirror direction by two unit cells
rather than argued in a comment: an accepted label is unchanged by the sanitiser, and a
label the sanitiser WOULD change is refused by both verbs.

**No pre-delete, and no two-sample size rule** - the two places this verb deliberately
differs from its sibling. `CaptureScreenshot` needs both because UNITY writes its PNG
asynchronously into the path the poll reads: a stale file would settle immediately, and a
first sighting can be half a file. Here the completion signal is the recorder's own
`LastCaptureWritten` + `LastWrittenPath` pair, which the arm clears before anything else
(so a stale file cannot satisfy it), and the writer is Parsek's own `FlushCapture`, whose
`File.WriteAllText` has RETURNED before the flag is set (so the file cannot be partial).

**The poll's first rule is that a FAULT beats a written dump.** `GuiTreeRecorder.Fault`
clears `ArmedFlag` but leaves an OPEN capture for the pump to flush, so a faulted arm can
leave a real file on disk describing a frame the recorder stopped following. The file is
left in place - it is evidence about the fault - but the verdict refuses to call it a
product, because a partial control tree in a census folder reads as a Parsek UI defect
rather than as a recorder fault. `Settled` is then decided BEFORE the budget, the
`CaptureScreenshot.DecidePoll` rule.

**Terminals.** `REJECTED`: `label-arg-missing`, `label-arg-invalid` (the same two tokens
the capture verb emits, deliberately). `ERROR`: `gui-tree-arm-refused reason=<why>` (the
recorder refused to arm and named why - today the only reason is `inside-gui-pass`. ERROR
rather than REJECTED because the seam pump runs in `Update`, which Unity runs BEFORE the
frame's `OnGUI`, so the refusal is unreachable by construction: if it fires, the dispatch
moved), `gui-tree-timeout` (NO dump - either the recorder gave itself up on its own
900-frame `armed-no-repaint` bound because nothing drew IMGUI through a patched funnel, or
the verb's budget expired first; ONE token because the actionable fact is identical and the
log line carries `gaveUp=` plus the recorder's own reason), and `gui-tree-faulted` (a patch
body threw during the captured frame, or applying the patches threw).

**`OK` payload: `label`, `path`, `bytes`, `windows`, `nodes`, `patched`, `hits`.** The last
four are the capture's own reading, on the WIRE rather than only in the file because a spec
can only regex what reaches the response line and the log, and they answer three different
failures: `windows` / `nodes` say the capture has CONTENT (a dump of an empty frame is a
valid file and a useless product), `patched=<ok>/<of>` says the interceptions were
INSTALLED (the recorder's arm-time `Harmony.GetPatchInfo` reading, owner-scoped to its own
Harmony id - a flush-time reading would report false for everything, because the capture
removes its own patches), and `hits` says they FIRED (the sum of the per-funnel body-run
counters; a funnel Mono inlined reads `patched: true, hits: 0`). `patched` is one token
rather than two because the reading only means anything as a pair - which is what lets a
census lane pin `patched=17/17` as a literal while leaving `hits` to a reader, since a hit
count is a property of whatever the frame happened to contain.

**Pure decision.** `TestCommandDumpGuiTree` (`IsValidLabel`, `TryParseLabel`, `DecidePoll`,
`RelativePathFor`, `FormatPatched`, `BuildPayload`), xUnit-covered in
`TestCommandDumpGuiTreeTests.cs`. The partial `ParsekTestCommandAddon.DumpGuiTree.cs` owns
the arm, the recorder polls and the `FileInfo` stat, and nothing else. Three per-arm
readings were added to the recorder for it - `LastArmRefusalReason`, `LastDisarmReason`,
`FaultsSinceArm` - because the recorder previously reported all three only to the log,
which is enough for a person and not for a verb whose terminal a spec reads.

**Tail / post-mission roles.** `inert` and `recording`, `CaptureScreenshot`'s pair exactly:
it writes ONE file under the KSP root and touches no vessel, no save, no career and no
Parsek persisted state. The Harmony interceptions are the one thing that could argue
otherwise and they do not - applied at arm, removed at flush, observation-only, and none of
them on a Parsek method at all. Mirrored in the C# `NonMutatingVerbs` set, which a harness
cell reads out of the source.

**First consumers.** `GUI-1-census-ksc` (22 dumps, one after every capture) and
`GUI-2-census-flight` (5). Both pin `patched=17/17` on every dump's OK line, which is what
makes the census's first flight a MEASUREMENT of the interception layer rather than only a
picture gallery.

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
| `RunTests` | any scene the runner supports; else Defer | `InGameTestRunner.RunAll()` (no `category`) or `RunCategory(category)`; with `isolated=true` (R5) the `*IncludingFlightRestore` variant instead, which also admits `RestoreBatchFlightBaselineAfterExecution` tests and restores a flight baseline after each. An `isolated` value other than the exact lowercase `true`/`false` is REJECTED `isolated-arg-invalid` (fail-closed: a silent fallback would run the ordinary filter and print an all-skipped tally that reads like a Parsek defect). Response deferred until `IsRunning` goes true->false and `ExportResultsFile` ran | `passed`, `failed`, `skipped`, `results=parsek-test-results.txt` |
| `LoadGame` | any scene incl. MAINMENU (the BOOT CHANNEL); Reject if a recorder is live (`msg=recording-active`, unless `allowLiveRecorder=refly` is passed AND a re-fly session marker is live - RF-3/A1) or a load is already in flight (`msg=load-in-flight`) | long-running two-phase (like `RunTests`): journal `CLAIMED` -> initiate load (`HighLogic.SaveFolder = dir`; `GamePersistence.LoadGame(...)`; `FlightDriver.StartAndFocusVessel(...)` - the same Assembly-CSharp-only sequence as v0.5.4 `TestingTools.LoadSave`, no kRPC types); response deferred until the new scene settles (pure `TestCommandLoadGame.DecideLoadCompletion`): a settled FLIGHT scene with `HighLogic.CurrentGame != null` -> journal `EXECUTED` + terminal `OK`; a settle-back to MAINMENU -> `ERROR msg=load-failed-returned-to-menu` (a failed flight boot, e.g. an NRE in `FlightDriver.Start` on an incompatible save); the LoadGame budget expiring -> `ERROR msg=load-timeout`. A null / incompatible game detected up front (before two-phase) is still `ERROR msg=load-failed` | `scene`, `save`, `allowLiveRecorder` |
| `MissionMark` | any scene | emit a stable `[Parsek][Info][TestCommands] MISSIONMARK label=<label> ut=<ut>` log line (H3-style correlation) | `label` echoed |
| `CaptureScreenshot` | any scene (the `ExportRenderManifest` row; the safe-point gate already excludes LOADING / a transition / the settle window, which is when a capture would photograph a black frame) | pre-delete a colliding target, then the reflectively-resolved `UnityEngine.ScreenCapture.CaptureScreenshot(<KSP root>/Screenshots/<label>.png, superSize)`; TWO-PHASE, holding the head until the file reports the same non-zero size on two consecutive polls | `label`, `path` (relative), `bytes` (settled), `superSize`, `overwrote` |
| `UiAction` | game loaded, any scene that HOSTS the Parsek UI (FLIGHT / SPACECENTER); a scene with no host is `REJECTED ui-host-unavailable`, never a defer | per `op`: write a window's `IsOpen`, write a tab selector, `ParsekUI.SetUiComplexityMode` + the production `Update` latch, write a window rect, or walk the window table read-only. Every op read-back-verified - and `open` / `rect` TWO-PHASE, holding the head for one DRAWN frame so the read-back describes what the window's own draw resolved rather than the value just written | per op: `op window open already` / `op window tab index already` / `op mode already` / `op window rect` / the describe inventory (`scene complexity count` + six keys per window) |
| `DumpGuiTree` | any scene (the `CaptureScreenshot` row) | `GuiTreeRecorder.ArmForNextRepaint(label)` from the Update-phase pump (the recorder REFUSES to arm from inside an IMGUI pass), then TWO-PHASE, holding the head until the recorder reports THIS arm's dump written to `<KSP root>/Screenshots/<label>.gui.json` | `label`, `path` (relative), `bytes`, `windows`, `nodes`, `patched` (`<ok>/<of>`, the arm-time reading), `hits` |
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
  verb never silently discards an in-flight recording. THE ONE EXCEPTION is RF-3/A1's
  `allowLiveRecorder=refly`, which admits the load while a re-fly session marker is live
  so the seam can drive an F9 mid re-fly; without the marker the reject is unchanged.
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
| `CaptureScreenshot` | (default) 60 s | TWO-PHASE but deliberately NOT in `DEFERRED_SEAM_VERBS`, the `EnterWatchMode` shape: the completion is a file poll that lands in a frame or two, so a capture still unwritten after a minute is broken rather than slow, and a longer budget would only delay the diagnosis |
| `DumpGuiTree` | (default) 60 s | TWO-PHASE and NOT in `DEFERRED_SEAM_VERBS`, the `CaptureScreenshot` shape: the wait is one Repaint pass plus the `LateUpdate` that flushes it, and the RECORDER gives the arm up on its own after 900 frames (`GuiTreeRecorder.ArmTimeoutFrames`, ~15 s at 60 fps) - so this budget is a backstop behind a shorter bound, not the primary one |
| `UiAction` | (default) 60 s | bounds the game-not-loaded dispatch defer AND the one-frame settle wait of its two two-phase ops. A settle that has not landed in a minute means the game stopped drawing, not that it is slow, so the default is the right size and the terminal is named `ui-action-not-settled` rather than spelled like a refusal |

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
- `RunTests`: `Info` "runtests start category=<cat|all> isolated=<true|false>" (the
  `isolated=` token added by R5) and "runtests complete passed=N
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
- **Tracked-setting persistence route.** Each of the 5
  `ParsekSettingsPersistence`-authoritative names (`writeReadableSidecarMirrors`,
  `showRouteLines`, `ghostRenderTracing`, `mapRenderTracing`, `ledgerTracing`; 8 before
  the 2026-08-27 settings simplification)
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
