# Design: Live Observability of Running Test Flights

Status: Phase 1 SHIPPED (supervisor-side, new files only); Phase 2 PLANNED
(mission-side instrumentation, gated on the review-fixes branch landing --
do NOT touch `mission_runner.py` / `mlib.py` until the base is ready).

Operator-requested. Problem statement: when the operator watches the game and
reports a symptom ("warp oscillating", "stuck at 1x", "flight results dialog
up"), the supervising session must reconstruct the mission machine's state by
grepping the newest `results/*_mission.stdout.log`, sampling rate-limited
telemetry lines, and INFERRING decision state (which correction round, is the
burn latch set, why has a gate not opened, was a plan disqualified). Fourteen-
plus live B5/B6 findings (`docs/dev/todo-and-known-bugs.md`) were diagnosed
this way. Two defect classes were nearly invisible:

- a SINGLE-FRAME attitude-error transient that opened the throttle gate
  BETWEEN two 1 Hz telemetry samples (no line ever showed the passing value);
- repeated course-correction plan disqualifications (over-cap removal loop)
  that look, in game, like a silent 1x hang -- the loud evidence exists but
  only as scattered `[Plan]` warns whose relationship to the phase budget must
  be computed by hand.

Goal: the supervisor observes the mission's full state in ONE step.

## Architecture overview

Two halves, deliberately decoupled:

1. **Supervisor-side (Phase 1, shipped)**: `harness/status.py`, a stdlib-only
   CLI over the EXISTING artifacts. Works for every past and future run with
   zero mission-side changes; degrades gracefully (missing TOML, missing
   status file, py < 3.11).
2. **Mission-side (Phase 2, planned below)**: four additive instrumentation
   seams in `mission_runner.py` + pure helpers in `mlib.py` that make the
   decision state EXPLICIT in the log and in a live status file, so the
   supervisor-side panel stops estimating and starts reading.

Phase 1 already consumes the Phase 2 status file when present
(`read_status_file` in status.py prefers a fresh `results/<runId>_status.json`
and falls back to log parsing), so Phase 2 needs no status.py changes to
light up.

## Phase 1 (shipped): harness/status.py

- **Run discovery**: newest `results/*_mission.stdout.log` by mtime (the
  mission subprocess writes it unbuffered, `-u`, so mtime is a liveness
  signal); `--run <id>` prefix-selects; the newest `*_harness.log` supplies
  the orchestrator step context (pre-mission LoadGame staging, post-mission
  verifier chain).
- **Pure parsers** (unit-tested in `harness/lib/test_status.py`, discovery
  root `lib`): `parse_log_line` (the `[Mission][LEVEL][Phase]` /
  `[Harness][LEVEL][Step]` shapes), `parse_telemetry_message` (the 17-field
  `telemetry ...` line incl. the `warp=RAILSx1000.000` mode/rate split),
  `parse_phase_transition`, `parse_action_message`, the `_VERDICT_RE` verdict
  line, `split_run_id` (`<ts>_<scenario>[_aN]` -> scenario + attempt).
- **Derived views**: phase history with GAME durations (transition-line `ut`
  deltas) and WALL estimates (telemetry-line counts at the ~1 Hz rate limit);
  time-in-phase for the OPEN phase estimated from the time-to-SOI-change
  drift (`tts` decreases 1:1 with UT while finite -- the telemetry line
  carries no `ut`, a deliberate Phase 2 fix); phase budgets read from the
  scenario TOML `[driver.missionParams]` via `tomllib`.
- **Heuristic line** (`derive_heuristic`): maps phase + telemetry tail +
  sparse events to a plain-English "what is it doing / why might it look
  stuck" sentence. Encoded patterns: the PLAN-* over-cap removal loop (count,
  last dv vs cap, fall-through ETA against the plan budget), negligible-dv
  removal, planner server-side failures, BURN-phase executor idle vs autowarp
  vs burning (node-dv static-tail measure, stagnation/no-start watchdog
  bounds), COAST native-warp-active vs rails-held vs at-1x-no-warp-commanded,
  flyby/ascent progress, finished-run verdict.
- **Modes**: one-shot panel, `--watch N`, `--raw K`, `--head N` (replay: the
  panel as of the first N lines -- post-hoc "what would it have said" at any
  mid-run moment).

Validated against the real 2026-07-22 logs: the `--head 650` replay of run
`2026-07-22_1210_B5-mun-flyby` mid PLAN-CORRECTION names the over-cap loop
("9 plan(s) removed OVER-CAP (last dv=172.5 m/s > cap 150.0) ... falls
through to COAST-TO-TARGET in ~1m02s") -- the exact case that read as a
silent 1x hang live.

Known Phase 1 estimation limits (all erased by Phase 2): no `ut` on telemetry
lines (game time-in-phase is a tts-drift estimate, unavailable when tts is
NaN); machine internals (correction_rounds_done, corr_burn_started,
aligned_streak, min_node_dv, burn_static_since age, warp_cmd/warp_to_cmd,
planned_node_count) are inferred, not read; single-frame transients remain
invisible between samples.

## Phase 2 (planned): mission-side instrumentation

MECHANICAL patch plan against the post-review-fixes tip. All changes are
additive; no existing log line changes shape (the Phase 1 parsers keep
working untouched). Everything decision-shaped lands as pure helpers in
`mlib.py` with unit tests; `mission_runner.py` only performs the I/O the
helpers dictate, mirroring the existing pure/shell split.

### 2a. MACHINE-STATE line (rate-limited, ~5 s cadence)

A second rate-limited line logging the DECISION state verbatim, so operator
reports map to machine state without inference.

- `mlib.py`: add `MACHINE_STATE_FIELDS: Tuple[str, ...] = ("phase",
  "correction_rounds_done", "corr_burn_started", "aligned_streak",
  "min_node_dv", "burn_static_since", "warp_cmd", "phys_warp_cmd",
  "warp_to_cmd", "last_warp_issue_ut", "planned_node_count", "last_plan_ut",
  "phase_entry_ut", "frozen_count")` and a pure
  `format_machine_state(state, ut=float("nan")) -> str` that renders
  `machine phase=... entryUt=... rounds=... corrBurnStarted=...
  alignedStreak=... minNodeDv=... burnStaticAge=... warpCmd=...
  physWarpCmd=... warpToCmd=... plannedNodes=... lastPlanUt=... frozen=...`
  via `getattr(state, field, None)` so it works for B1/B2/B4 states too
  (absent fields render `-`). `burnStaticAge` is derived: `ut -
  burn_static_since` when both finite (the AGE is the diagnostic quantity;
  the raw stamp is meaningless without the current UT). Floats through the
  existing `nan`-style formatting; ASCII `key=value` tokens so
  `status.py:parse_kv_tokens` decodes it as-is.
- `mission_runner.py::_fly_loop_body`: after the existing telemetry
  `log.verbose_rate_limited("telemetry", ...)` call, add
  `log.verbose_rate_limited("machine", state.phase,
  mlib.format_machine_state(state, snapshot.ut), interval=5.0)`.
  (`MissionLogger.verbose_rate_limited` already takes `interval`.)
- ALSO append `ut=<snapshot.ut>` to the existing telemetry line message (one
  new trailing token; the Phase 1 parser tokenizes k=v generically, so this
  is non-breaking and upgrades the game-time-in-phase estimate to exact).
- Tests: `missions/lib/test_mlib.py` (or a new `test_observability.py`):
  field coverage for a B5State, absent-field fallback for a B1State,
  burnStaticAge derivation, NaN rendering.

### 2b. GATE-EVIDENCE lines (event-driven, no rate limit)

Whenever a gate OPENS or a guard FIRES, log the exact values that decided it.
Events are sparse (tens per flight), so plain Info lines.

- `mlib.py`: pure `diff_machine_state(prev, new) -> List[str]`: compares
  `MACHINE_STATE_FIELDS` (minus the per-frame-noisy `last_plan_ut` /
  `phase_entry_ut` / `frozen_count`) and returns
  `["corr_burn_started False->True", "aligned_streak 2->3", ...]`. Pure,
  fully unit-testable, zero knowledge of logging.
- `mission_runner.py::_fly_loop_body`: after `state, actions = decide(...)`,
  emit `for change in mlib.diff_machine_state(prev_state, state):
  log.info(state.phase, "gate %s | apErr=%s nodeDv=%s warp=%sx%s ut=%s" %
  (change, ...snapshot fields...))` -- the snapshot context is what makes a
  single-frame transient (e.g. the attitude-error dip that flipped
  `corr_burn_started`) visible BY DEFINITION: the flip is logged on the frame
  it happens with the values that caused it, independent of any rate limit.
  Requires keeping `prev_state = state` before the decide call (one local).
- The existing loud sites already cover the runner-side guards ([Plan] cap
  removal, [Warp] watchdog, [Throttle] readback, [Point] abort outcomes); 2b
  adds the MACHINE-side latches those sites cannot see.
- Tests: diff helper unit tests (field added/removed/changed, None handling,
  foreign state objects).

### 2c. EVENT-WINDOW ring buffer (post-hoc transient visibility)

- `mlib.py`: pure `format_snapshot_compact(snapshot) -> str` (one line per
  frame: `ut alt ap pe nodes nodeDv thr apErr warp situation`, ~90 chars).
- `mission_runner.py::_fly_loop_body`: a `collections.deque(maxlen=20)` of
  compact frames, appended every frame (0.5 s cadence = a ~10 s window).
  Dump ONCE, at Verbose, prefixed `window[i/N]`, on: (a) phase transition,
  (b) verdict/flake set (incl. the warp-violation second strike), (c) the
  vessel-lost snapshot, (d) any `diff_machine_state` change from 2b. Guard
  with a per-trigger-frame flag so one frame emitting several triggers dumps
  once. This is the flight-data-recorder answer to "what happened BETWEEN
  the rate-limited samples right before X".
- Tests: deque bounding + dump-once semantics via the existing fake-control
  fly-loop tests (inject a scripted control, capture the sink).

### 2d. LIVE STATUS FILE (results/<runId>_status.json, ~2 s cadence)

- `mission_runner.py`: new small class `StatusFileWriter(path, clock,
  interval=2.0)` with `maybe_write(payload: dict) -> None`: skips when
  `clock() - last_write < interval`; serializes with `json.dumps(...,
  sort_keys=True)`; writes `path + ".tmp"` then `os.replace` (atomic on
  Windows, same pattern as `_write_result_file`); EVERY exception swallowed
  (best-effort: the status file must never block or kill the fly loop).
- Path derivation in `run_mission`: the shell already receives `result_path`
  = `results/<runId>_mission.json`; status path =
  `result_path.replace("_mission.json", "_status.json")` (fallback: sibling
  `+ ".status.json"` when the suffix is absent). Passed into `fly_loop` as an
  optional `status_writer=None` param (tests pass None; behavior unchanged).
- Payload (built in `_fly_loop_body` once per frame, written at most every
  2 s): `{"schema": 1, "mission": spec.name, "phase": state.phase,
  "phasesReached": list, "machine": {field: value ... from
  MACHINE_STATE_FIELDS}, "snapshot": {decoded telemetry fields},
  "events": [last 10 event strings], "wallWritten": time.time()}`. The
  last-10 events ride a second small deque fed by a tee on the logger sink
  (wrap `MissionLogger.sink` at construction: every non-telemetry Info/Warn
  line is appended; telemetry excluded by prefix check).
- `status.py` (Phase 1) already prefers this file when fresh
  (`STATUS_FILE_FRESH_SECONDS = 15`), rendering the `machine` block verbatim;
  no supervisor-side change required.
- Tests: `StatusFileWriter` cadence + atomicity (tmp never left behind on a
  simulated dump failure) with injected clock; payload shape via the fake
  fly-loop run asserting the file parses and carries the machine block.

### 2e. Cost discipline

Respects the existing logging philosophy (rate-limited per-frame data, loud
sparse events):

- Machine-state line: 1 line / 5 s vs telemetry's 1 line / 1 s = +20% line
  count, ~+25% bytes (the line is shorter than telemetry). A 900 s flight:
  ~180 extra lines.
- Telemetry `ut=` token: +9 bytes/line, negligible.
- Gate lines: bounded by actual state changes; B5's noisiest field
  (`aligned_streak`) changes at most a few dozen times per correction round;
  expected < 150 lines/flight. If live volume exceeds that, drop
  `aligned_streak` from the diffed set (it is visible in 2a anyway).
- Ring dumps: 20 lines per trigger, ~15 transitions + < 10 gate triggers per
  flight = < 500 lines/flight, Verbose.
- Status file: one ~2 KB atomic write / 2 s, off the log entirely; try/except
  guarantees the fly loop never blocks on it (worst case: the write is
  skipped, the file goes stale, status.py falls back to log parsing).
- Total: well under 2x the current mission log size; the harness log is
  unaffected until the post-exit fold-in.

### Phase 2 execution checklist (when the base is ready)

1. Branch from the then-current `autotest-b5-mun` tip (AFTER review-fixes
   merges); confirm `git log --oneline -3` shows the review-fixes commits.
2. mlib.py: add `MACHINE_STATE_FIELDS`, `format_machine_state`,
   `diff_machine_state`, `format_snapshot_compact` + unit tests. Run
   `python -m unittest discover -s missions/lib -q`.
3. mission_runner.py: `_fly_loop_body` (machine line, prev-state diff ->
   gate lines, ring buffer + dump sites, status payload build),
   `fly_loop`/`run_mission` (StatusFileWriter + optional param + sink tee),
   telemetry `ut=` token. Run all three discovery roots.
4. status.py: no change required; verify the panel against a fake status
   file, then live on the next operator-run flight.
5. Docs: this file (flip Phase 2 to SHIPPED), harness/README.md
   observability section (status-file bullet becomes unconditional),
   CHANGELOG if the wave is user-facing.

## Non-goals

- No second connection / no reads from KSP by the supervisor (the mission
  process is the single kRPC client owner; observability is artifact-based).
- No changes to hlib/run.py verdict semantics; observability is read-only.
- No streaming/push channel; the 2 s status file + unbuffered log tail are
  within one poll interval of real time, which matches the operator loop.
