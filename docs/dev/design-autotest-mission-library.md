# Design: Mission Library (Module M-B1)

Status: DRAFT (2026-07-12). Module M-B1 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`, section 2 stack, section 5 scenario
program, section 10 determinism guardrails, section 11b row M-B1). This is the
Step 3 design doc the plan's section 11b mandates before any code; the vision +
gameplay scenarios are the plan plus the scenario catalog
(`docs/dev/automated-testing-scenario-catalog.md`, blocks B1-B2, tiers 0-1,
autopilot sources 5.4, failure modes 5.5). The mission-spec extension is "new
data that persists" (it feeds spec validation and the verdict), so this is the
full workflow, no shortcuts.

Consumed contracts (already merged, read as authorities, never re-specified
here): the harness core `docs/dev/design-autotest-harness-core.md` (M-A5, which
RESERVES the `autopilot` driver kind this doc defines), the command seam
`docs/dev/design-autotest-command-seam.md` (M-A2, which OWNS boot / settings /
commit / quit lifecycle), the autorun hooks
`docs/dev/design-autotest-autorun-hooks.md` (M-A3), the offline analyzer +
baseline `docs/dev/design-autotest-offline-analyzer.md` /
`design-autotest-findings-baseline.md` (M-A1), and the stack setup /
provisioner `docs/dev/design-autotest-stack-setup.md` (M-A6, which already
provisions kRPC 0.5.4 + KRPC.MechJeb 0.7.1 + MechJeb2 2.15.1.0 into BOTH
instance profiles and stamps the kRPC server `autoStartServers=True` /
`autoAcceptConnections=True` / `confirmRemoveClient=False`). This doc pins
against their PUBLIC contract surfaces (the seam channel grammar and verb table,
the driver-validity verdict inputs, the provisioned stack + stamped kRPC
settings), never their internals.

Plain ASCII, no em dashes, no emoji.

---

## Problem

M-A5 delivered an unattended pipeline that drives a SEAM-only scenario: boot the
instance, pin settings, run an in-game batch, quit, verify. Its explicit v1
limitation is that nothing FLIES. A seam scenario cannot produce a real ascent,
a real suborbital arc and chute descent, a real orbit insertion, or the physics
seams, SOI numerics, and recorded trajectories that only live flight produces
(harness core "Deferred Items", plan section 5). The plan's whole ladder
(catalog sections 2-3) rests on atomic FLOWN blocks: B1 (pad hop) and B2 (LKO
ascent) are the two smallest, and everything above them (multi-vessel, loop,
re-fly, routes) depends on being able to make KSP fly a scripted mission while
Parsek records.

M-B1 is the MISSION LIBRARY: the `autopilot` driver kind that makes a scenario
FLY. It adds a mission script reference plus parameters to the scenario spec, a
harness-side python mission that drives flight through the already-provisioned
kRPC + MechJeb + KRPC.MechJeb stack BETWEEN the seam's lifecycle steps, and the
plumbing that turns mission telemetry assertions (reached apoapsis window,
landed within a situation, orbit params within tolerance) into DRIVER-VALIDITY
verdict inputs. The seam REMAINS the lifecycle channel (boot via LoadGame, pin
settings via SetSetting, commit via CommitTree, quit via FlushAndQuit); the
mission script only owns the flying in between. Parsek records the flown mission
exactly as it records a human-flown one, and the EXISTING verifier chain
(analyzer over the produced recordings, log validation, expectations) decides
whether Parsek recorded it correctly.

v1 ships EXACTLY TWO missions: B1 pad-hop (raw kRPC throttle + stage + coast +
chute, assert an apoapsis window and a landed/splashed state) and B2 LKO-ascent
(KRPC.MechJeb AscentAutopilot to an 80 km circular orbit, assert orbit params
within tolerance). Both fly the fixture save's PRE-PLACED vessel (no VAB flow in
v1) and record via Parsek auto-record.

## Terminology

- **Mission**: a harness-side python program under `harness/missions/<name>.py`
  that flies ONE scripted atomic block via kRPC and emits a mission result. It
  is invoked by run.py as a SUBPROCESS (like the ps1 verifiers), never imported
  into run.py / hlib.
- **Mission phase step**: a driver step of kind `mission` (not a seam command)
  that hands control from the seam to the mission subprocess and back. It sits
  between seam steps in the driver's ordered `steps` list.
- **Handoff**: the ordered transition `seam step -> mission phase -> seam step`.
  The seam brings KSP to a settled FLIGHT (LoadGame OK) and pins settings; the
  mission connects, flies, asserts, disconnects; the seam commits and quits.
- **Mission verdict**: the mission subprocess's own terminal classification of
  the FLIGHT (not of Parsek): `MISSION-OK`, `MISSION-ASSERT-FAIL`,
  `MISSION-CONNECT-TIMEOUT`, `MISSION-FLAKE`, `MISSION-ERROR`. It is a
  DRIVER-VALIDITY input, mapped by hlib into the harness verdict taxonomy.
- **Mission-validity gate**: the doctrine (plan section 9 item 1) that a mission
  that failed to FLY as intended is a DRIVER problem (INVALID), not a Parsek
  defect. The mission's telemetry assertions answer "did the driver set up the
  flight the scenario needs?"; Parsek correctness is judged separately by the
  verifier chain over the produced save.
- **Telemetry snapshot**: a frozen, kRPC-free struct of the flight quantities a
  phase decision reads (UT, altitude, vertical speed, apoapsis, periapsis,
  eccentricity, inclination, vessel situation, current-stage resources). The
  pure phase state machine consumes snapshots so it is unit-testable without a
  game.
- **Mission venv**: the vendored, module-scoped Python virtual environment at
  `harness/missions/.venv`, provisioned from `harness/missions/requirements.txt`,
  carrying the pinned `krpc` client (and its `protobuf` dependency). It exists so
  the third-party kRPC client is isolated to the mission subprocess and run.py /
  hlib stay stdlib-only.
- **mlib**: the pure, separately-importable, unittest-covered mission decision
  library (`harness/missions/lib/mlib.py`), the M-B1 analogue of `hlib.py` /
  `provlib.py`: the phase state machines, the telemetry-assertion evaluators, the
  mission-result serialization, and the connect-retry decision all live here with
  no kRPC import, so they test with fake telemetry and no game.

## Mental Model

```
   scenario spec (driver.kind = "autopilot")
            |
            v
   hlib.validate_spec  (pure; rejects a bad mission ref / params / handoff)
            |  valid
            v
   +-----------------------------------------------------------------------+
   |  run.py loop (ADMIT [+venv-admit] / LOCK / STAGE / LAUNCH)             |
   |                                                                        |
   |  DRIVE the ordered driver steps:                                       |
   |                                                                        |
   |    seam LoadGame  --------> KSP boots to a settled FLIGHT (OK)         |
   |    seam SetSetting autoRecordOnLaunch=true, warp/tracing pins (OK)     |
   |                                                                        |
   |    MISSION PHASE  --------> run.py spawns the mission SUBPROCESS with  |
   |      (kind=mission)          the mission venv python:                  |
   |                             .venv/Scripts/python missions/<name>.py    |
   |                               --params <json> --rpc-host 127.0.0.1     |
   |                               --rpc-port 50000 --stream-port 50001     |
   |                               --result <path> --budget <s>             |
   |                                    |                                   |
   |                                    v   (mission process, in venv)      |
   |                          +----------------------------------------+    |
   |                          | connect kRPC (bounded retry)           |    |
   |                          | mlib phase state machine over          |    |
   |                          |   telemetry snapshots (pure decisions): |   |
   |                          |   PRELAUNCH->ASCENT->COAST->DESCENT->   |   |
   |                          |   LANDED   (B1)                          |   |
   |                          |   PRELAUNCH->MJ-ASCENT->CIRCULARIZE->    |   |
   |                          |   ORBIT    (B2)                          |   |
   |                          | evaluate telemetry assertions (pure)   |    |
   |                          | disconnect; write mission-result JSON  |    |
   |                          +----------------------------------------+    |
   |                                    |  mission verdict                  |
   |    run.py reads the result <-------+                                   |
   |      MISSION-OK        -> mission step MET, proceed                    |
   |      MISSION-ASSERT-FAIL / CONNECT-TIMEOUT / FLAKE / ERROR             |
   |                       -> driver stage failed at the mission step       |
   |                          (retry-once-then-INVALID per subkind)         |
   |                                                                        |
   |    seam CommitTree  ------> commit the recorded tree in FLIGHT (OK)    |
   |    seam FlushAndQuit ------> commit-safe quit (OK)                     |
   |                                                                        |
   |  VERIFY (unchanged): analyzer over the produced recordings, log        |
   |  validation, expectations (recordings.count.min>=1) -> PASS/PARSEK-FAIL|
   +-----------------------------------------------------------------------+
```

Three invariants shape the design:

- **The seam owns lifecycle; the mission owns flight.** run.py never links kRPC;
  the mission subprocess is the ONLY thing that does, exactly as the ps1
  verifiers are the only things that touch pwsh. Boot, settings, commit, and quit
  stay seam commands (M-A2). This keeps run.py / hlib stdlib-only and keeps the
  kRPC GPL/protobuf dependency confined to one isolated subprocess and one venv.

- **A mission that did not fly is a DRIVER problem, not a Parsek defect.** The
  mission's telemetry assertions feed DRIVER VALIDITY (retry-once-then-INVALID),
  never PARSEK-FAIL. Whether Parsek RECORDED the flight correctly is a separate
  question answered by the unchanged verifier chain over the produced save. This
  is the plan's mission-invalid-vs-Parsek-failure doctrine, made mechanical.

- **A mission never hangs and never lies.** Every kRPC wait is bounded (connect
  retry, ascent, coast, descent, circularize, each with a budget); expiry yields
  a mission verdict, never an indefinite block. The harness run budget and its
  process-tree kill (M-A5) remain the outer ceiling, so a wedged mission that
  never writes a result is still KILLED by the existing watchdog.

## Data Model

Two persisted formats: the scenario-spec `[driver]` extension (parsed + validated
by hlib) and the mission-result JSON (written by the mission subprocess, read by
run.py). Plus one committed dependency manifest (`requirements.txt` + a venv
stamp). All are "new data that persists" per plan 11b, so all get round-trip /
validation tests.

### Scenario spec `[driver]` extension: `kind = "autopilot"`

The autopilot driver is a SUPERSET of the seam driver: it keeps the ordered seam
`steps` (the seam still owns lifecycle) and ADDS a mission reference, mission
parameters, and exactly one `mission`-kind step in `steps` marking the handoff.

```toml
schema = 1
id = "B1-pad-hop"
tier = "daily"
description = "Pad hop: pre-placed craft, throttle+stage, coast, chute, land; assert apoapsis window + landed."
instanceProfile = "stock-minimal"
tags = ["B1", "S0.1", "flown"]

[fixture]
# The pre-placed vessel rides IN the fixture save (no VAB flow in v1). LoadGame
# drops straight into FLIGHT with this vessel on the pad. runSaveName is the
# template leaf, per M-A5.
saveTemplate       = "fixtures/saves/b1-pad-craft"
injectedRecordings = "none"
craft              = []            # v1 uses the pre-placed vessel, not a staged .craft

[driver]
kind    = "autopilot"             # M-B1. hlib.validate_spec now ACCEPTS this (was reject-only).
mission = "b1_pad_hop"            # -> harness/missions/b1_pad_hop.py  (filename-safe, must resolve)

# Ordered driver steps. The seam owns every lifecycle step; the one mission step
# (kind="mission") is the handoff. ${runSave} is substituted by the harness. NOTE:
# `steps` stays in the [driver] table, so it is declared ABOVE the
# [driver.missionParams] sub-table header -- a key placed after the sub-table
# header would be scoped to driver.missionParams.steps (TOML table scoping) and the
# spec would validate as having zero steps.
steps = [
  { cmd = "LoadGame",   args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 240 },
  { cmd = "SetSetting", args = { name = "autoRecordOnLaunch", value = "true" }, expect = "OK" },
  { phase = "mission",  expect = "MISSION-OK", budget = 600 },   # <- flight happens here
  { cmd = "CommitTree",                                          expect = "OK" },
  { cmd = "FlushAndQuit",                                        expect = "OK" },
]

# Mission parameters. Free-form per mission; hlib validates the block against the
# mission's declared param schema (harness/missions/<name>.schema.toml). Values
# are TOLERANCES / WINDOWS, never golden trajectories (plan section 10).
[driver.missionParams]
throttle              = 1.0
apoapsisWindowMeters  = { min = 6000, max = 30000 }   # a WINDOW, not a golden apoapsis
chuteDeployAltMeters  = 2500
landedSituations      = ["LANDED", "SPLASHED"]        # either accepted
ascentTimeoutSeconds  = 90
coastTimeoutSeconds   = 180
descentTimeoutSeconds = 240

# A flown scenario PRODUCES a recording, so recordings are expected. count.min>=1
# keeps the REC-001/REC-003 log rules MANDATORY (M-A5 verifier 4): a dropped
# recording reds. The analyzer (Forbid) over the produced recording is the Parsek
# correctness gate.
[expectations.recordings]
count = { min = 1, max = 1 }
[expectations.logContracts]
required  = ["Recording started", "Recording stopped"]
forbidden = ["\\[Parsek\\]\\[ERROR\\]"]
allowedAnomalies = []

[runtime]
budgetSeconds = 900               # outer wall-clock ceiling (M-A5 watchdog)
[retry]
policy = "once"                   # mission FLAKE / connect-timeout retry-once (below)
[expectedFail]
bugId = ""
subkind = ""
```

The B2 spec differs only in `mission = "b2_lko_ascent"`, its `missionParams`
(target orbit + tolerances), and a longer mission budget:

```toml
[driver]
kind    = "autopilot"
mission = "b2_lko_ascent"
# `steps` stays in [driver], declared ABOVE the [driver.missionParams] header.
steps = [
  { cmd = "LoadGame",   args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 240 },
  { cmd = "SetSetting", args = { name = "autoRecordOnLaunch", value = "true" }, expect = "OK" },
  { phase = "mission",  expect = "MISSION-OK", budget = 780 },
  { cmd = "CommitTree",                                          expect = "OK" },
  { cmd = "FlushAndQuit",                                        expect = "OK" },
]
[driver.missionParams]
targetApoapsisMeters   = 80000
targetPeriapsisMeters  = 80000
apoErrorMeters         = 5000     # |Ap - target| <= tol   (tuned from actuals, plan section 10)
periErrorMeters        = 5000     # |Pe - target| <= tol
eccentricityMax        = 0.02     # near-circular
inclinationErrorDeg    = 2.0      # |inc - launch-site inc| <= tol
ascentTimeoutSeconds   = 420      # LKO 6-10 min (plan section 10)
circularizeTimeoutSeconds = 300
[runtime]
budgetSeconds = 1200
```

Budget arithmetic (the invariant the two example specs satisfy, and the rule a spec
author must keep): the MISSION step budget must out-wait the flight, and the RUN
budget must out-wait the whole attempt:

- `missionBudget >= connectBudget + sum(phaseBudgets) + margin` -- the mission
  subprocess wall-clock ceiling covers the bounded connect plus every phase budget
  plus slack. B1: 600 >= 30 (connect) + (90+180+240) + margin. B2: 780 >= 30 +
  (420+300) + margin.
- `runtime.budgetSeconds >= sum(step budgets) + margin` -- the outer KSP run budget
  covers every budgeted step (LoadGame + the mission step; CommitTree / FlushAndQuit
  are fast and unbudgeted) plus slack. B1: 900 >= 240 (LoadGame) + 600 (mission) +
  margin. B2: 1200 >= 240 + 780 + margin.

A spec that violates the second rule surfaces as a KILLED attempt (M-A5 watchdog),
not a hang; a spec-validation cross-check of the arithmetic is a deferred cheap guard
(below).

Spec-validation rules for `kind = "autopilot"` (pure, `hlib.validate_spec`; the
seam-kind rules are unchanged and still apply to the seam steps):

- `driver.kind` is now `seam` XOR `autopilot` (was `seam`-only).
- `driver.mission` present, filename-safe, and resolves to an existing
  `harness/missions/<mission>.py`. An unknown mission -> reject (the boot would
  waste time launching KSP for a mission that cannot run).
- `driver.missionParams` validates against the mission's declared param schema
  `harness/missions/<mission>.schema.toml` (required keys present, types /
  ranges, windows well-formed with `min <= max`). A missing required param or a
  window with `min > max` -> reject.
- EXACTLY ONE `mission`-kind step in `steps`; its `expect` is `MISSION-OK`; its
  optional `budget` bounds the mission subprocess wall-clock and is `> 0`.
- The mission step is PRECEDED by a `LoadGame` step (the FLIGHT handoff owner):
  the mission cannot connect before KSP is in FLIGHT, so a mission step at index 0
  or before the first LoadGame -> reject.
- The FIRST step is still `LoadGame` with `save == ${runSave}` / runSaveName
  (unchanged M-A5 rule); the QUIT owner is still exactly one `FlushAndQuit` XOR
  `autorun.exit`; a `mission` step is NOT a BATCH owner and NOT a seam verb, so
  it is exempt from the seam-verb / reserved-verb checks (which apply only to
  `cmd`-kind steps).
- A `mission` step and a `RunTests` step MAY coexist (fly, then run an in-game
  `RecordingInvariants` canary batch over the live store); at most one of each.
- `instanceProfile` must be a profile that provisions the flight stack. Both
  `stock-minimal` and `modded-compat` do (M-A6 `stackComponents` lists krpc /
  mechjeb2 / krpc_mechjeb in both), so no new profile rule is needed; a future
  profile lacking them would fail admission, not spec validation.

### Mission result: `harness/results/<runId>_mission.json`

Written by the mission subprocess to the `--result` path, read by run.py. `runId`
is ATTEMPT-SUFFIXED, so each retry writes its OWN `<runId>_mission.json`; run.py also
DELETES any file at the target path before spawning the subprocess. Together the
per-attempt path plus delete-before-spawn close the stale-result read: run.py can
never mistake a prior attempt's result (or a leftover file) for this attempt's.
Stable key order, deterministic, floats via `repr`. The `verdict` is the mission's
DRIVER-VALIDITY signal; `telemetry` is evidence, not a Parsek judgement.

```json
{
  "schema": 1,
  "mission": "b1_pad_hop",
  "verdict": "MISSION-OK",
  "reason": "landed within apoapsis window",
  "phasesReached": ["PRELAUNCH", "ASCENT", "COAST", "DESCENT", "LANDED"],
  "connect": { "attempts": 2, "connectedSeconds": 3.1, "rpcPort": 50000 },
  "assertions": [
    { "name": "apoapsisWindow", "met": true,  "value": 14210.4, "window": [6000, 30000] },
    { "name": "landedSituation", "met": true,  "value": "LANDED", "accepted": ["LANDED", "SPLASHED"] }
  ],
  "wallSeconds": 71,
  "krpcClientVersion": "0.5.4",
  "krpcServerVersion": "0.5.4",
  "error": null
}
```

`verdict` is one of `MISSION-OK`, `MISSION-ASSERT-FAIL` (connected + flew but a
telemetry assertion unmet), `MISSION-CONNECT-TIMEOUT` (could not connect within
the bounded retry), `MISSION-FLAKE` (autopilot / node execution stalled, or the
connection dropped mid-flight after connecting), `MISSION-ERROR` (an unexpected
exception in the mission script). The mission ALSO exits with a nonzero code on
any non-OK verdict, so run.py has a fallback signal if the result file is
missing (edge case below).

The mission step ALSO appears as a ROW in the run's `driver.steps` array in
`harness/results/<runId>.json` (the same array the seam steps populate), so a reader
sees the mission inline with the lifecycle steps. Its row shape mirrors a seam-step
row plus the mission facts:

```json
{ "id": 2, "phase": "mission", "verdict": "MISSION-OK", "missionVerdict": "MISSION-OK", "met": true, "subkind": null }
```

On a failure the row carries the mapped subkind, e.g.
`{ "id": 2, "phase": "mission", "verdict": "INVALID", "missionVerdict": "MISSION-ASSERT-FAIL", "met": false, "subkind": "mission" }`.
The standalone `<runId>_mission.json` (above) holds the full telemetry / assertion
evidence; the `driver.steps` row holds the one-line outcome.

### Dependency manifest: `harness/missions/requirements.txt` + venv stamp

```
# harness/missions/requirements.txt  --  the ONLY third-party python in harness/.
# krpc pinned to match the provisioned kRPC 0.5.4 SERVER (pins.toml [krpc].tag).
krpc==0.5.4
# PROVISIONAL (unverifiable offline in this environment): the krpc pin above AND the
# protobuf pin below are BOTH unconfirmed until the first verified bootstrap. The
# bootstrap installs krpc==0.5.4 through pip's OWN resolver with NO hand-pinned
# protobuf (0.5.x-era kRPC may target a protobuf 4.x-era range, so a hand-pinned
# 3.20.3 would be an unverified guess), verifies `import krpc` + a generated-code
# smoke, freezes the RESOLVED protobuf version into the .venv-stamp, and only THEN is
# that resolved version promoted to the committed pin here.
# protobuf==<RESOLVED>   # PROVISIONAL: filled in from the .venv-stamp after the first verified bootstrap; do NOT hand-pin before that.
```

The venv bootstrap (`harness/missions/bootstrap_venv.py`, a thin shell) creates
`harness/missions/.venv`, installs `krpc==0.5.4` through pip's OWN resolver (NO
hand-pinned protobuf), then VERIFIES the install actually works: `import krpc`
succeeds AND a generated-code smoke (construct a krpc protobuf message type / call a
client stub that exercises the compiled `.proto` bindings) runs clean, proving the
resolved protobuf is ABI-compatible with the client's generated code. Only on a clean
smoke does it write `harness/missions/.venv/.venv-stamp.json` recording the pinned
krpc version, the RESOLVED protobuf version (frozen from `pip freeze`), and a
`pip freeze` hash. The FIRST verified bootstrap PROMOTES the resolved protobuf
version into `requirements.txt` as the committed pin (turning the PROVISIONAL
protobuf line into a real pin). run.py checks this stamp at pre-launch ADMIT (venv
admission, below) and refuses with INVALID(tooling-venv) if the venv is missing or
its stamp drifts from `requirements.txt`, mirroring the M-A6 instance admission.

VERSION-PIN VERIFICATION (cannot be confirmed offline in this environment; the krpc
AND protobuf pins are BOTH PROVISIONAL until this runs). The `krpc` PyPI client is
released in lockstep with each kRPC server tag, so `krpc==0.5.4` is EXPECTED to exist
and to match the provisioned server, but neither its presence on PyPI nor the
protobuf range it declares can be confirmed here. Verify at bootstrap time (network
required), and record the resolved facts in the stamp:

```
python -m pip index versions krpc            # lists available krpc releases; expect 0.5.4 present
# or, resolve-only without installing (the resolver pulls krpc + its protobuf):
python -m pip download krpc==0.5.4 -d harness/missions/.cache/pipcheck
# or check the release page:  https://pypi.org/project/krpc/0.5.4/
```

If `krpc==0.5.4` is ever ABSENT from PyPI (a client release lag), the fallback
installs the SEPARATE `krpc-python` client release asset -- NOT the GameData-only
`krpc-0.5.4.zip` (703 KB, a verified DLL-only server layout with no python client
inside). The krpc-python asset has its OWN download URL + sha256, recorded in
`pins.toml` alongside the server pins (a distinct `[krpc_python_client]` entry); the
bootstrap installs it via `pip install <path-to-downloaded-krpc-python>` and the
resolver then pulls a compatible protobuf, frozen into the stamp exactly as in the
PyPI path. The server and this matched client are version-matched by construction.
The bootstrap logs the resolved source (PyPI vs krpc-python asset) so the venv
provenance is explicit.

## Behavior

### The dependency decision (this doc owns it)

DECISION: a vendored, module-scoped venv at `harness/missions/.venv`, provisioned
from `requirements.txt` by a bootstrap step, with `krpc==0.5.4` (+ its `protobuf`
dependency) pinned. The mission runs as a SUBPROCESS invoked with the venv's
python. run.py / hlib import NOTHING from the venv.

REJECTED ALTERNATIVE: `pip install krpc` into the system / dev python. Rejected
because it (a) breaks the harness's stdlib-only invariant at the process level (a
dev running the per-PR `python -m unittest` cadence would need krpc + protobuf on
their base interpreter or the imports would fail), (b) risks a protobuf version
clash with anything else on the dev machine, and (c) makes the third-party
dependency implicit and unreproducible instead of a committed, pinned,
admission-checked artifact.

JUSTIFICATION for the venv:
- **Module boundary preserved.** The kRPC client and its protobuf transitive
  dependency live in ONE directory (`harness/missions/.venv`) used by ONE
  subprocess. `run.py`, `hlib.py`, `provlib.py`, and all their unittest suites
  stay stdlib-only and keep running under the base interpreter with no
  third-party install. This mirrors how the ps1 verifiers keep pwsh out of the
  python decision core.
- **Reproducible + admission-checkable.** `requirements.txt` is committed and
  pinned; the `.venv-stamp.json` lets a run refuse a missing / drifted venv the
  same way instance admission refuses a drifted manifest, so a stale client can
  never silently certify a flight.
- **Isolated GPL/licensing surface.** The kRPC client (GPLv3) is never imported
  into Parsek or into the harness decision core; it is executed as a separate
  process. This matches the plan section 2 stance that RPC USE does not infect
  Parsek and the seam avoids kRPC linkage entirely. The mission SHELLS
  (`harness/missions/b1_pad_hop.py` / `b2_lko_ascent.py`) DO import the kRPC client,
  so those files are themselves taken as GPLv3 (a derivative of the GPL client) and
  carry that notice; the obligation is confined to the kRPC-importing mission shells
  under `harness/missions/*.py` and never reaches Parsek, run.py, hlib, provlib, or
  the kRPC-free `mlib`, keeping the licensing boundary aligned with the module
  boundary.
- **`.venv` and `.cache` are gitignored** (like `harness/provision/.cache` /
  `.stage`), so the vendored binaries are not committed; only `requirements.txt`
  and the bootstrap are.

### Connection lifecycle

1. **Server auto-start (already provisioned).** M-A6 stamped
   `GameData/kRPC/PluginData/settings.cfg` with `autoStartServers=True`,
   `autoAcceptConnections=True`, `confirmRemoveClient=False`, so the RPC server
   starts and accepts on its default `127.0.0.1:50000` (RPC) / `50001` (stream)
   with no in-game click. M-B1 adds NO kRPC config; it consumes the stamped
   settings. The default host/ports are the mission's defaults and are
   overridable by run.py CLI args for a future multi-instance layout.
2. **Connect AFTER the seam confirms FLIGHT.** run.py only spawns the mission
   subprocess when the handoff order guarantees FLIGHT: the mission step follows a
   `LoadGame` step that already returned `OK` (LoadGame's own two-phase
   completion requires a settled FLIGHT with a loaded game, M-A2). Even so, the
   RPC server may take a moment to bind the port after the scene settles, so the
   mission connects with a BOUNDED RETRY (pure `mlib.decide_connect_retry`: N
   attempts, fixed backoff, total connect budget, e.g. up to 10 attempts over 30 s).
   Connect budget exhausted -> `MISSION-CONNECT-TIMEOUT`.
3. **Version check on connect.** The mission reads the server version
   (`krpc.connect().krpc.get_status().version`) and compares it to the client
   version; a mismatch is recorded in the result and, if the major/minor differ
   (an ABI-incompatible pairing), the mission aborts `MISSION-ERROR`
   (subkind tooling-mission) rather than flying against a mismatched RPC surface.
4. **Every wait bounded.** Connect, ascent, coast, descent, circularize each
   carry a budget from `missionParams`; a phase that overruns its budget yields
   `MISSION-FLAKE` (autopilot stalled) with the stuck phase named. The mission
   never blocks unbounded on a stream read.
5. **Disconnect before quit.** The mission CLOSES the kRPC connection
   (`conn.close()` in a `finally`) before it writes the result and exits, so the
   connection is gone before the harness sends the seam `FlushAndQuit`. A
   lingering client during the game's quit is avoided; even if the game tears the
   socket first, the mission's `finally` swallows the close error and still
   writes the result.

Failure taxonomy mapping (mission verdict -> harness verdict, via
`hlib.classify_mission_step`):

| Mission verdict | Cause | Harness classification | Retry |
|---|---|---|---|
| `MISSION-OK` | flew as intended, all telemetry assertions met | mission step MET; proceed | - |
| `MISSION-CONNECT-TIMEOUT` | server never reachable within connect budget | INVALID subkind `tooling-krpc` | retry-once |
| `MISSION-ASSERT-FAIL` | connected + flew but a telemetry assertion unmet | INVALID subkind `mission` | retry-once |
| `MISSION-FLAKE` | autopilot / node-exec stalled, or connection dropped mid-flight | INVALID subkind `autopilot-flake` | retry-once |
| `MISSION-ERROR` | unexpected exception, or ABI version mismatch | INVALID subkind `tooling-mission` | retry-once |

All five non-OK verdicts are DRIVER-VALIDITY INVALIDs, never PARSEK-FAIL, per the
mission-validity gate: a flight that did not fly as the scenario needs is not a
Parsek finding. They are retry-once-then-INVALID (plan section 10 "retry once;
two invalids => quarantine"), reusing M-A5's existing driver-stage retry path.
Two consecutive INVALIDs terminate INVALID and add a flake-ledger entry.
`MISSION-ASSERT-FAIL` is classified INVALID(mission) deliberately: an apoapsis
that landed outside its window means the AUTOPILOT under- or over-performed, not
that Parsek mis-recorded. Parsek correctness is decided ONLY by the verifier
chain over whatever the flight actually produced.

CRUCIAL SEPARATION: a mission that returns `MISSION-OK` still runs the full
verifier chain. If Parsek recorded that good flight WRONG (analyzer reds the
produced recording, a REC log rule fails, or `recordings.count` is off), the run
is PARSEK-FAIL. The mission verdict gates "did we get a valid flight to test
against"; the verifier chain gates "did Parsek record it right". The two are
orthogonal and both must pass for a PASS.

CLASSIFICATION CARVE-OUT (mission-OK + missing recording evidence). When the mission
step returned MISSION-OK, the run gets the FULL verifier chain EVEN IF a later seam
step errored. In particular, if the flight flew fine (MISSION-OK) but the recording
EVIDENCE is MISSING -- `CommitTree` returns `ERROR no-active-tree`, or the
`recordings.count.min` expectation is unmet -- that missing evidence is
VERDICT-DRIVING, NOT triage-only. A good flight that Parsek failed to record is
exactly the defect M-B1 exists to catch, so it classifies PARSEK-FAIL(expectation),
NOT a driver-INVALID that a retry would paper over. The CommitTree ERROR is a
triage-only driver-INVALID ONLY when the mission step itself did NOT return
MISSION-OK (no valid flight was produced to record in the first place). So:
MISSION-OK gates the flight, a MISSION-OK run always faces the full verifier chain,
and a missing recording on a MISSION-OK run is a Parsek defect.

### The handoff (seam step -> mission phase -> seam step)

run.py's `drive_seam` loop (M-A5) already walks `steps` in order and, for a
`cmd`-kind step, writes a channel line and waits for the seam verdict. M-B1 adds
ONE branch: a `phase = "mission"` step is not written to the channel; instead
run.py:

1. Confirms the immediately-preceding lifecycle state is FLIGHT (the prior
   LoadGame step's `OK` is the evidence; no channel traffic during the mission).
2. Re-checks the mission venv as a cheap BACKSTOP (stamp present + matches
   `requirements.txt`). The load-bearing venv gate already ran at pre-launch ADMIT
   (terminal INVALID(tooling-venv), no KSP booted); this in-flight re-check only
   guards a venv mutated AFTER admission, and if it trips the mission step fails
   INVALID(tooling-venv) with no subprocess spawned.
3. DELETES any stale mission-result file at the target path, then spawns the mission
   subprocess with the venv python, passing `--params` (the `missionParams` block as
   JSON), `--rpc-host/--rpc-port/--stream-port`, a `--result <path>` under
   `harness/results/` (the per-attempt path `<runId>_mission.json`; `runId` is
   attempt-suffixed, so a retry writes a DISTINCT file), and `--budget` (the mission
   step's budget). Delete-before-spawn plus the per-attempt path close any
   stale-result read -- run.py never mistakes a prior attempt's result for this one.
4. WAITS on the subprocess with the SAME non-blocking poll loop `drive_seam` uses for
   a seam step -- NEVER a blocking `wait()`. Each POLL_INTERVAL it checks, in order:
   (a) did the subprocess EXIT (read the result); (b) has the mission STEP budget
   elapsed; (c) has the outer RUN budget elapsed. The two budget expiries have two
   distinct kills:
   - **Run-budget expiry** (the whole-attempt ceiling, M-A5): kill the MISSION
     subprocess FIRST, then the KSP process tree; the attempt's verdict is KILLED.
   - **Mission-step-budget expiry** (the mission overran its own budget while the run
     still has time): kill ONLY the mission subprocess -> the mission step is
     INVALID(autopilot-flake); run.py then continues into the seam teardown (step 5).
   On a normal exit, run.py reads the mission-result JSON (falling back to the exit
   code if the file is absent) and maps the verdict: `MISSION-OK` matches the step's
   `expect`; any non-OK is a failed mission step with the mapped INVALID subkind.
5. run.py drives the REMAINING seam steps REGARDLESS of the mission step outcome. On
   a met (MISSION-OK) step the seam resumes normally: `CommitTree` commits the
   recorded tree in FLIGHT (the seam verb's `activeTree != null` guard is satisfied
   by the auto-recorded flight), then `FlushAndQuit` quits commit-safe. On a FAILED
   or killed mission step run.py STILL drives them: `CommitTree` may return `ERROR`
   (e.g. no-active-tree) -- that verdict is RECORDED, not acted on mid-drive -- and
   `FlushAndQuit` still brings KSP down cleanly so no orphan process is left.
   CLASSIFICATION happens AFTERWARD, from the accumulated facts (the mission verdict
   PLUS every seam step's recorded verdict PLUS the verifier chain), never by
   aborting the drive at the first failure -- see the classification carve-out above
   for the MISSION-OK-but-no-recording case.

The channel is QUIET during the mission phase (no seam commands in flight, so the
seam pump idles), and the mission phase never runs concurrently with an in-game
test batch (the pump's batch gate is orthogonal; a `RunTests` step, if present,
is a SEPARATE later step, never overlapping the mission). This keeps the mission
flight and the seam strictly sequential.

### Mission B1: pad-hop (raw kRPC)

Fixture: `b1-pad-craft` save with a small pre-placed solid-booster-plus-chute
vessel on the launchpad in a PRELAUNCH situation (no VAB flow). `autoRecordOnLaunch=true`.
The mission (`harness/missions/b1_pad_hop.py`) drives the flight with RAW kRPC
(no MechJeb): a pure phase state machine (`mlib.b1_decide`) over telemetry
snapshots.

Phases (each transition a pure decision over a snapshot):
- **PRELAUNCH -> ASCENT**: set `control.throttle = throttle`, then
  `control.activate_next_stage()` to release clamps / ignite the SRB. The stage
  activation + throttle is the first real modification of the flight.
- **ASCENT -> COAST**: when the active-stage solid fuel is exhausted (thrust
  drops to ~0 / stage resources depleted), cut throttle. Bounded by
  `ascentTimeoutSeconds`.
- **COAST -> DESCENT**: coast to apoapsis at 1x (a 6-30 km hop never leaves
  Kerbin's 70 km atmosphere, and stock KSP FORBIDS rails warp inside the atmosphere,
  so B1 never rails-warps -- the whole hop is powered / atmospheric at 1x), then on
  the way down deploy the chute when `altitude <= chuteDeployAltMeters`. Bounded by
  `coastTimeoutSeconds`.
- **DESCENT -> LANDED**: when `vessel.situation` in `landedSituations`
  (LANDED or SPLASHED). Bounded by `descentTimeoutSeconds`.

Telemetry assertions (driver validity):
- `apoapsisWindow`: peak apoapsis within `apoapsisWindowMeters` (a WINDOW, not a
  golden apoapsis).
- `landedSituation`: final situation in `landedSituations`.
All met -> `MISSION-OK`. Any unmet -> `MISSION-ASSERT-FAIL`. A phase timeout ->
`MISSION-FLAKE`.

Recording interplay (the fixture-policy note): the vessel rides IN the fixture
save PRE-PLACED, so it is NOT a fresh VAB launch. Two auto-record paths cover it,
and the design relies on the FIRST that applies:
- If the pre-placed vessel is in a PRELAUNCH / on-pad situation, releasing the
  clamps (stage activation) is the launch transition, so `autoRecordOnLaunch`
  fires as it would for a real launch (this is the D1 `auto-record-launch`
  dimension the scenario intends to exercise).
- If KSP treats the loaded vessel as already flying (situation != PRELAUNCH),
  `autoRecordOnLaunch` may NOT fire on load; the first throttle/stage input then
  trips auto-record-on-first-modification-after-switch (also a shipped default).
Either way a recording MUST exist by commit time; `expectations.recordings.count.min = 1`
plus the mandatory REC-001/REC-003 log rules turn a failure to auto-record into a
DRIVER/expectation red, not a silent green. If a future fixture proves to sit in
a non-PRELAUNCH situation and neither path fires reliably, the driver adds an
explicit `StartRecording` seam step before the mission phase (deferred; the v1
fixtures are authored PRELAUNCH-on-pad so `autoRecordOnLaunch` is the honest
trigger).

### Mission B2: LKO-ascent (KRPC.MechJeb)

Fixture: `b2-ascent-craft` save with a pre-placed adequate-TWR ascent vehicle
(e.g. a Kerbal-X-class stack) on the pad, PRELAUNCH. `autoRecordOnLaunch=true`.
The mission (`harness/missions/b2_lko_ascent.py`) uses KRPC.MechJeb's
`AscentAutopilot`: set the target apoapsis to `targetApoapsisMeters`, enable
`autostage`, engage the autopilot, and wait (bounded by `ascentTimeoutSeconds`)
until MechJeb reports the ascent complete and the apoapsis is at target; then let
MechJeb execute the circularization node (bounded by `circularizeTimeoutSeconds`).
The phase state machine (`mlib.b2_decide`) is PRELAUNCH -> MJ-ASCENT ->
CIRCULARIZE -> ORBIT, each transition a pure decision over a snapshot
(MechJeb-autopilot-enabled flag + orbit apoapsis / periapsis).

Telemetry assertions (driver validity), all WITHIN TOLERANCE (never golden):
- `apoapsisError`: `|apoapsis - targetApoapsisMeters| <= apoErrorMeters`.
- `periapsisError`: `|periapsis - targetPeriapsisMeters| <= periErrorMeters`.
- `eccentricity`: `eccentricity <= eccentricityMax` (near-circular).
- `inclinationError`: `|inclination - launchSiteLatitude| <= inclinationErrorDeg`
  (a due-east launch from KSC targets ~0 deg; the tolerance absorbs MechJeb's
  steering noise).
All met -> `MISSION-OK`; any unmet -> `MISSION-ASSERT-FAIL`; a MechJeb stall
(node never executes, autopilot never reports complete) -> `MISSION-FLAKE` on the
phase timeout.

### Determinism guardrails (plan section 10) and MechJeb failure modes (catalog 5.5)

- **Rails warp only for exoatmospheric coasts; 1x for powered / atmospheric
  flight.** B1 flies at 1x THROUGHOUT: a 6-30 km hop never exits Kerbin's 70 km
  atmosphere, and stock KSP forbids rails warp inside the atmosphere, so B1 never
  rails-warps at all (powered ascent, coast, chute-deploy window, and the whole
  atmospheric descent are all 1x). B2 is the only v1 mission that rails-warps, and
  only on its EXOATMOSPHERIC coast: the mission asserts 1x (physics warp OFF) during
  powered ascent and permits RAILS warp only for the above-atmosphere coast to the
  circularization node; physics warp is never engaged by the mission (edge case
  below). The provisioned instance already pins `PHYSICS_FRAME_DT_LIMIT=0.03`,
  autostrut policy, and framerate cap (M-A6 stock-minimal settings), so the physics
  substrate is fixed.
- **Tolerances, never golden values.** Every assertion above is a window or a
  tolerance band tuned from harness-recorded ACTUALS (the harness records
  per-scenario actuals from day one, plan section 10), not a hard-coded expected
  trajectory. Physics nondeterminism (plan section 12) bounds the tightness, so
  the bands start generous and tighten as actuals accumulate.
- **MechJeb warp stutter during coast (catalog 5.5).** MechJeb's coast produces
  noisy velocity across warp transitions; the mission SAMPLES orbit params only
  after warp has settled to 1x (a debounce in `mlib`: require K consecutive
  in-tolerance snapshots at 1x before asserting), so a transient warp-edge
  reading never false-fails an assertion.
- **MechJeb landing is NOT used in v1.** The flakiest MechJeb mode (arms only
  below an altitude threshold, can brake late / overshoot, worse with atmosphere)
  is avoided entirely: B1 lands under a chute + gravity (raw kRPC, deterministic),
  B2 stops at orbit. This keeps both v1 missions in the TRIVIAL/SCRIPTED
  automation band (catalog section 3).
- **Adequate craft, small budgets tuned to archetype.** B1 uses a robust
  high-TWR SRB hop (MechJeb's low-TWR weakness is irrelevant, no MechJeb); B2
  uses an adequate-TWR stack (MechJeb ascent is weak on low-TWR / wobbly stacks).
  Budgets follow the plan section 10 planning numbers (pad-hop ~3-5 min, LKO 6-10 min);
  the outer run budget gives margin over the mission budget so a MechJeb stall
  surfaces as a `MISSION-FLAKE` (retryable) rather than a harness KILL.
- **Flake policy reuse.** A mission that flakes is retry-once; a persistent flake
  (>20% over a week) quarantines the scenario for redesign (smaller craft, more
  RCS, different autopilot), reusing M-A5's flake ledger unchanged (KILLED and
  the new mission INVALID subkinds both count toward the rate).

### hlib additions (pure, harness-side python only)

M-B1 extends the pure decision library, NOT Parsek:
- `validate_spec` accepts `kind == "autopilot"` and validates the mission ref /
  params / handoff (above).
- `classify_mission_step(mission_verdict) -> (met: bool, subkind: str)` maps the
  five mission verdicts to a met-flag + INVALID subkind, feeding the existing
  driver-validity stage.
- New INVALID subkinds. FOUR are RETRYABLE, registered in
  `RETRYABLE_INVALID_SUBKINDS`: `mission`, `tooling-krpc`, `tooling-mission`,
  `autopilot-flake` (all driver/tooling stages, so the existing retry-once policy
  applies with no change to `classify_verdict`'s verdict enum). `tooling-venv` is a
  TERMINAL, NON-retryable INVALID (a missing / drifted venv is a provisioning fault
  a retry cannot fix, caught at pre-launch ADMIT before any KSP boot), so it is NOT
  added to `RETRYABLE_INVALID_SUBKINDS`.
- `venv_admission(stamp, requirements) -> (ok, subkind)` mirrors instance
  admission for the mission venv; it runs at the pre-launch ADMIT phase (alongside
  instance admission) and its `tooling-venv` INVALID is TERMINAL / non-retryable.

The mission-side pure library `mlib` holds `b1_decide` / `b2_decide` (phase state
machines), `evaluate_b1_assertions` / `evaluate_b2_assertions`,
`decide_connect_retry`, and `serialize_mission_result`.

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. **kRPC server not up despite the stamp** (server disabled, a settings
   regression, or the port never bound). The connect retry exhausts its budget
   with no connection. -> `MISSION-CONNECT-TIMEOUT` -> INVALID(tooling-krpc),
   retry-once. The mission records the last connect exception; if the server was
   never armed this points at a provisioning drift (the stamped settings), which
   the retry cannot fix but the flake ledger surfaces. v1.
2. **Port conflict** (another process, or a prior orphaned KSP, holds 50000). The
   connect either fails (connect-timeout) or connects to the WRONG server; the
   version check catches a foreign server (unexpected version / no vessel) and the
   mission aborts. -> INVALID(tooling-krpc). The M-A5 zombie-KSP preflight and
   run lock already refuse a second run against a live instance, so a stray KSP on
   the same port is caught before launch; a non-KSP squatter surfaces as a connect
   failure. v1.
3. **kRPC client / server version mismatch** (the venv installed a client whose
   major/minor differs from the provisioned 0.5.4 server). The on-connect version
   check detects it. -> `MISSION-ERROR` (subkind tooling-mission), NOT a flight
   attempt against a mismatched RPC surface. The venv stamp check should have
   caught a drifted client before launch; this is the in-flight backstop. v1.
4. **Venv missing or drifted** (never bootstrapped, or `requirements.txt`
   changed without re-running bootstrap). venv-admission fails at pre-launch ADMIT,
   alongside instance admission, BEFORE KSP is launched -- so no KSP boot is spent on
   a mission that cannot import krpc. -> terminal INVALID(tooling-venv), NON-retryable
   (a retry cannot fix a provisioning fault). Points at re-running `bootstrap_venv.py`.
   The in-flight mission-step re-check (handoff step 2) is only a cheap backstop for a
   post-ADMIT mutation. v1.
5. **Connection drops mid-burn** (KSP hiccup, GC stall, socket reset after a
   successful connect). The mission's next bounded stream read raises; the mission
   catches it, records the phase it dropped in. -> `MISSION-FLAKE`
   (subkind autopilot-flake), retry-once (a transient drop usually clears on a
   fresh boot). v1.
6. **MechJeb node execution stalls** (B2: the circularization node never
   executes, or the ascent autopilot never reports complete). The CIRCULARIZE /
   MJ-ASCENT phase overruns its budget. -> `MISSION-FLAKE`, retry-once; a
   persistent stall quarantines the scenario (catalog 5.5 names MechJeb the known
   worst offender). v1.
7. **Physics-warp accidentally engaged** (a stray high-warp request during
   powered flight would distort the recorded trajectory and the physics). The
   mission NEVER requests physics warp and ASSERTS the expected warp state around
   powered phases (B1 asserts 1x THROUGHOUT, since it never leaves the atmosphere
   where rails warp is forbidden; B2 asserts `TimeWarp.WarpMode == RAILS` or 1x); an
   unexpected physics-warp state is treated as a determinism violation ->
   `MISSION-FLAKE` (the flight is not the controlled one the scenario needs),
   retry-once. The instance's pinned physics settings make this rare. v1.
8. **Vessel destroyed mid-mission** (a wobble RUD, a chute-less crash, an
   overpressure). The active vessel / control reference goes invalid; the next
   telemetry read raises or reports no active vessel. -> the mission distinguishes
   an EXPECTED terminal (B1 SPLASHED/LANDED already reached -> assert normally)
   from an UNEXPECTED destruction (mid-ascent RUD -> `MISSION-FLAKE`, the flight
   did not complete). A destruction that was the intended outcome is a DIFFERENT
   scenario (S2.4 destruction tree, deferred), not B1/B2. v1.
9. **Mission script raises an unexpected exception** (a kRPC API surprise, a
   None dereference, a bad param). The mission's top-level `try/except` catches
   it, writes `MISSION-ERROR` with the traceback string into the result, closes
   the connection in `finally`, and exits nonzero. run.py maps it ->
   INVALID(tooling-mission), retry-once. An exception NEVER leaks as a hang (the
   process exits) and never as a Parsek finding. v1.
10. **Seam FlushAndQuit while kRPC still connected** (the mission failed to close
    the socket before returning). The mission ALWAYS closes in a `finally` before
    writing the result, so by the time run.py sends FlushAndQuit the client is
    gone. If a close raced the game's own teardown, the game's FlushAndQuit still
    forces the commit-safe save and quits (M-A2); a half-open socket on the
    already-exiting mission process is harmless. -> clean quit. v1.
11. **Telemetry NaN / Inf** (a kRPC field returns NaN during a transient, e.g.
    apoapsis at the exact vertical-velocity zero-crossing, or an undefined orbit
    element on the pad). The assertion evaluators treat a NaN/Inf telemetry value
    as UNMET-for-now (never as a passing comparison), and the phase state machine
    debounces (require K consecutive finite in-tolerance snapshots), so a single
    NaN frame neither passes nor fails an assertion. A phase that produces only
    NaN past its budget -> `MISSION-FLAKE`. This mirrors the RewindReadbackGuard
    NaN semantics. v1.
12. **Mission subprocess never writes a result** (killed by a budget expiry, or
    crashed before the `finally`). run.py finds no result file (delete-before-spawn
    makes the absence real, not a stale prior file). -> it falls back to the
    subprocess EXIT CODE: nonzero with no result -> INVALID(tooling-mission). A
    MISSION-STEP-budget expiry (run.py's non-blocking poll loop, step (b)) kills only
    the mission subprocess -> INVALID(autopilot-flake), then drives the seam
    teardown. A RUN-budget expiry (poll step (c)) kills the mission subprocess FIRST
    then the KSP process tree -> KILLED (M-A5), regardless of the mission. v1.
13. **auto-record did not fire** (the pre-placed vessel loaded in a non-PRELAUNCH
    situation and neither auto-record path tripped, so no recording exists at
    commit). CommitTree returns `ERROR no-active-tree`. Classification follows the
    carve-out above: because the mission step returned MISSION-OK (the flight flew
    fine), the missing recording is VERDICT-DRIVING, not triage -> the run is
    PARSEK-FAIL(expectation) (`expectations.recordings.count.min=1` unmet), NOT a
    driver-INVALID that a retry would mask. A good flight that Parsek failed to record
    is precisely the defect M-B1 exists to catch. (Only if the mission step itself did
    NOT return MISSION-OK would the CommitTree ERROR be a triage-only driver-INVALID.)
    The interplay is documented (Mission B1); v1 fixtures are authored PRELAUNCH so
    `autoRecordOnLaunch` is the honest trigger, and the explicit-StartRecording
    fallback is the deferred hardening. v1 (surfaced, not silently green).
14. **Mission flew fine but Parsek recorded it WRONG** (MISSION-OK, but the
    analyzer reds the produced recording, or a REC log rule fails, or the
    recording count is off). -> the verifier chain reds -> PARSEK-FAIL. This is
    the WHOLE POINT: a valid flight that Parsek mis-records is exactly the defect
    M-B1 exists to catch. The mission-validity gate and the Parsek verifier chain
    are orthogonal. v1.
15. **Assertion just outside tolerance** (B2 apoapsis 76 km vs an 80 km +/-5 km
    band -> met; 74 km -> ASSERT-FAIL). -> `MISSION-ASSERT-FAIL` ->
    INVALID(mission), retry-once (a MechJeb under-shoot is autopilot variance, not
    a Parsek defect); a persistent out-of-band result quarantines for a tolerance
    re-tune or a craft change. Tolerances are tuned from actuals, never golden. v1.
16. **modded-compat vs stock-minimal** (an autopilot scenario declares
    `instanceProfile` that does not provision the flight stack). Both provisioned
    profiles DO provision krpc/mechjeb/krpc_mechjeb, so a mismatch surfaces as an
    admission diff on `profile` (M-A5 INVALID admission), not a mission failure;
    the mission never launches against a stack-less instance. v1.
17. **RunTests canary coexists with the mission** (a scenario that flies THEN runs
    a `RecordingInvariants` batch over the live store). The mission phase and the
    RunTests step are strictly sequential (the pump's batch gate never overlaps the
    quiet-channel mission phase); the RunTests step follows CommitTree so the batch
    walks a committed tree. -> both run in order. v1 (allowed by validation).
18. **kRPC stream leak across a retry** (attempt 1 flaked with a live stream; the
    retry boots a fresh KSP process). FRESH STAGE PER ATTEMPT is the load-bearing
    retry-safety fact: each retry re-stages the fixture save from scratch, boots a
    FRESH KSP process, and spawns a FRESH mission subprocess with a fresh connection
    against a fresh auto-started server -- so the prior attempt's process, server,
    streams, AND any recording it produced are all gone, and the attempt-suffixed
    `runId` gives the retry its own result path. No cross-attempt state can leak (no
    stale stream, no stale recording, no stale result); this is what makes retry-once
    safe. v1.
19. **Mission budget vs seam LoadGame budget interaction** (the mission step's
    budget must out-wait the flight, but the outer run budget bounds the whole
    attempt). Per the budget arithmetic above -- `missionBudget >= connectBudget +
    sum(phaseBudgets) + margin` and `runtime.budgetSeconds >= sum(step budgets) +
    margin` -- the mission step budget is validated `> 0` and the run budget is sized
    to exceed LoadGame + mission + commit + quit with margin; if a spec under-budgets
    the run relative to the mission, the attempt is KILLED (M-A5 watchdog) rather than
    hanging, and the too-tight budget is a spec-authoring fix. v1 (surfaced via
    KILLED; a spec-validation cross-check of `runtime.budgetSeconds >= sum(step
    budgets) + margin` is deferred).

## What Doesn't Change

- **ZERO Parsek C# changes in v1. Expected NONE.** M-B1 is entirely harness-side
  python: the mission scripts, `mlib`, the `requirements.txt` + venv bootstrap,
  and the hlib validation/classification additions. No Parsek source file, no
  in-game code, no Harmony patch, no recording/save/sidecar/ledger format is
  touched. Parsek records a kRPC-flown mission through its EXISTING auto-record
  and recording paths, identically to a human-flown one; the flight being
  script-driven is invisible to Parsek.
- **The seam protocol is untouched.** LoadGame / SetSetting / CommitTree /
  FlushAndQuit are consumed exactly as M-A2 specced them; no new seam verb, no
  channel-grammar change, no journal change. The `mission` step is a HARNESS-SIDE
  step type that writes NOTHING to the channel; the seam never sees it.
- **The autorun hooks, analyzer, baseline filter, and provisioner are consumed
  as-is.** M-A6 already provisions the flight stack and stamps the kRPC settings;
  M-B1 adds no provisioning step to the instance (only the separate mission venv).
- **The M-A5 verifier chain and verdict taxonomy are unchanged.** The mission
  verdict feeds the EXISTING driver-validity stage (retry-once-then-INVALID); the
  Parsek-correctness verifiers are the same analyzer / log-validate / expectations
  chain. M-B1 adds only new INVALID SUBKINDS: four RETRYABLE (mission / tooling-krpc /
  tooling-mission / autopilot-flake) join the existing driver/tooling retry set, and
  `tooling-venv` is a TERMINAL non-retryable INVALID checked at pre-launch ADMIT; it
  adds NO new top-level verdict.
- **The dev install is never touched.** Missions fly only on provisioned
  automation instances; the mission venv lives under `harness/missions/`, not on
  the dev interpreter.
- **run.py / hlib stay stdlib-only.** The third-party kRPC + protobuf live ONLY in
  `harness/missions/.venv`, used only by the mission subprocess; the harness
  decision core imports nothing from the venv.

## Backward Compatibility

Greenfield module; nothing to migrate. The `[driver]` autopilot extension is
ADDITIVE: a `kind = "seam"` spec is unchanged and validates/runs exactly as
before (the seam-driver rules are untouched); only a `kind = "autopilot"` spec
exercises the new path. The mission-result and venv-stamp formats carry
`schema = 1`; a schema bump makes the harness refuse an old artifact with a clear
message rather than mis-parse, consistent with the project's no-migration stance.
New missions are added as new `harness/missions/<name>.py` + `<name>.schema.toml`
files plus a spec; no existing spec, mission, or result changes. The
`requirements.txt` pin is bumped only in lockstep with a kRPC server pin change in
`pins.toml` (a version bump re-runs the bootstrap and re-stamps the venv). A
result JSON from an older mission version is readable as long as the top-level
`schema` matches.

## Diagnostic Logging

The mission subprocess logs to stdout (captured by run.py into the per-invocation
harness log) AND records structured evidence in the mission-result JSON, one
decision per line, format `[Mission][LEVEL][Phase] message` (mirroring
`[Harness]` / `[Provision]` / ParsekLog), so a scheduled unattended flight is
reconstructable from the log alone. run.py logs the handoff under its existing
`[Harness]` tags. Every branch logs; the per-frame telemetry poll uses the
batch-counting convention (one rate-limited summary line, not one per frame).

- **VENV-ADMIT** (`[Harness][*][Mission]`): `Info` "mission venv-admit
  stamp=<path> result=<OK|DRIFT|MISSING>"; on non-OK `Warn` "mission venv drift:
  <requirements> vs <stamp>; INVALID tooling-venv".
- **SPAWN** (`[Harness]`): `Info` "mission spawn name=<mission> venv-python=<path>
  rpc=<host:port> budget=<s>s result=<path>".
- **CONNECT** (`[Mission][*][Connect]`): `VerboseRateLimited` "connect attempt=<n>/<N>
  elapsed=<s>/<budget>"; on success `Info` "connected in <s>s attempts=<n>
  serverVersion=<v> clientVersion=<v>"; on version mismatch `Error` "version
  mismatch client=<v> server=<v> -> MISSION-ERROR"; on budget expiry `Warn`
  "connect timeout after <n> attempts / <s>s -> MISSION-CONNECT-TIMEOUT".
- **PHASE** (`[Mission][*][<Phase>]`): `Info` per transition "phase <old> -> <new>
  ut=<ut> alt=<m> ap=<m> vsurf=<m/s>"; `VerboseRateLimited` (1 Hz) "telemetry
  ap=<m> pe=<m> ecc=<e> inc=<deg> situation=<s> warp=<mode>x<rate>" so a stuck
  phase shows exactly what it waited on; on a phase-budget overrun `Warn` "phase
  <phase> timed out after <s>s -> MISSION-FLAKE".
- **WARP** (`[Mission][*][<Phase>]`): `Info` "warp set mode=<RAILS|PHYS> rate=<r>
  reason=<coast|circularize>"; `Warn` "unexpected physics-warp in powered phase
  -> MISSION-FLAKE" (edge 7).
- **ASSERT** (`[Mission][*][Assert]`): `Info` one line per assertion
  "assert <name> value=<v> window/tol=<...> met=<bool>" (InvariantCulture floats);
  a NaN/Inf value logs `Warn` "assert <name> non-finite value; unmet-for-now"
  (edge 11).
- **VERDICT** (`[Mission][*][Verdict]`): `Info` "mission verdict=<V> reason=<why>
  phasesReached=[<...>] wall=<s>s"; the mission ALSO writes this into the result
  JSON.
- **DISCONNECT** (`[Mission][*][Connect]`): `Verbose` "disconnect (finally)
  closed=<bool>"; on a close error `Warn` "disconnect error swallowed: <ex>".
- **HANDOFF-RETURN** (`[Harness][*][Drive]`): `Info` "mission step=<i>
  verdict=<mission-verdict> -> <met|INVALID subkind=<s>>"; on non-OK `Warn`
  "mission step failed: <mission-verdict> -> INVALID <subkind> (retry per policy)".

Goal: reading only the harness log + the mission-result JSON, a developer can
reconstruct which mission flew, whether it connected, which phase it stalled in or
which assertion it missed, the mission verdict and its mapping to the harness
verdict, and whether the subsequent verifier chain judged Parsek's recording, all
without a game and without the source.

## Test Plan

Every test states the regression it catches. The pure decision logic
(`mlib` phase state machines + assertion evaluators + connect-retry, and the
`hlib` autopilot-spec / mission-classification additions) is unittest-covered
with NO kRPC and NO game, in the per-PR cadence. The kRPC-touching mission shell
is exercised by a FAKE-TELEMETRY harness test plus a PENDING-OPERATOR live
runbook, because an agent cannot pilot KSP (MEMORY: in-game-sweep-needs-operator).

The pure `mlib` library lives at `harness/missions/lib/mlib.py`, adding a THIRD
unittest discovery root to the harness's existing two (`harness/lib` and
`harness/provision`): the per-PR cadence becomes `python -m unittest discover -s lib
-q AND discover -s provision -q AND discover -s missions/lib -q`. So that discovering
the missions package on the BASE interpreter (no krpc installed) never fails, the
mission SHELLS (`b1_pad_hop.py` / `b2_lko_ascent.py`) MUST LAZY-import krpc -- the
`import krpc` lives inside the connect function, never at module top -- and `mlib`
imports NOTHING from krpc at all. Discovery and the fake-telemetry integration tests
then import the pure modules and the shell modules on the base interpreter without
touching the venv.

### Pure unit tests (unittest, no kRPC, no KSP)

- **Autopilot spec validation accept + each reject.** A well-formed autopilot
  spec validates clean; fixtures that (a) name an unknown mission, (b) omit a
  required missionParam, (c) give a window with `min > max`, (d) place the
  `mission` step before the first LoadGame, (e) declare two `mission` steps,
  (f) set the mission step `expect` to something other than MISSION-OK, each
  reject with the right reason. Fails if a malformed autopilot spec launches KSP
  and wastes a boot, or a valid one is wrongly rejected (blocking every flown
  scenario).
- **B1 phase state machine over fake telemetry.** Drive `b1_decide` through a
  scripted snapshot sequence: PRELAUNCH (throttle+stage) -> ASCENT (fuel burning)
  -> COAST (fuel exhausted, apoapsis climbing then falling) -> DESCENT (alt <=
  deploy threshold -> chute) -> LANDED (situation LANDED). Assert the transitions
  and the emitted actions (throttle set, stage activated, chute deployed) at the
  right snapshots. Fails if the machine deploys the chute during ascent, cuts
  throttle early, or never detects landing (a real mission would then hang to its
  budget and flake).
- **B2 phase state machine over fake telemetry.** Drive `b2_decide`: PRELAUNCH ->
  MJ-ASCENT (autopilot engaged, apoapsis climbing) -> CIRCULARIZE (node executes,
  periapsis rising) -> ORBIT (both at target). Fails if it asserts orbit before
  circularization completes, or never leaves MJ-ASCENT (false flake).
- **Assertion evaluators: met / unmet / boundary / NaN.** `evaluate_b1_assertions`
  and `evaluate_b2_assertions` over: an in-window value (met), just-outside
  (unmet), exactly-on-boundary (met, inclusive), and a NaN/Inf value
  (unmet-for-now, never a passing compare). Fails if a golden-value comparison
  sneaks in (a near-miss reds instead of tolerating), a boundary flips wrong, or a
  NaN telemetry value passes an assertion (the most dangerous silent pass).
- **Debounce over noisy telemetry.** A sequence with a single warp-edge
  out-of-tolerance / NaN frame surrounded by in-tolerance frames -> the assertion
  still MET (K-consecutive debounce); a genuinely persistent out-of-tolerance run
  -> UNMET. Fails if a MechJeb warp-stutter frame (catalog 5.5) false-fails a good
  flight, or a debounce masks a real miss.
- **Connect-retry decision.** `decide_connect_retry` yields RETRY within budget /
  attempt count, and TIMEOUT past either. Fails if a slow server bind is not
  retried (spurious connect-timeout) or an unreachable server retries forever
  (unbounded wait).
- **Mission-verdict -> harness classification.** `hlib.classify_mission_step` maps
  MISSION-OK -> met; CONNECT-TIMEOUT -> INVALID(tooling-krpc); ASSERT-FAIL ->
  INVALID(mission); FLAKE -> INVALID(autopilot-flake); ERROR ->
  INVALID(tooling-mission); each retryable-once. And a MISSION-OK flight whose
  verifier chain reds still classifies PARSEK-FAIL (the orthogonality). Fails if
  an assertion miss poisons the Parsek-defect bucket (INVALID(mission) misread as
  PARSEK-FAIL), or a mis-recorded good flight is swallowed as a mission problem.
- **Venv admission (pre-launch, terminal).** `venv_admission` admits a stamp
  matching `requirements.txt`, refuses a missing stamp or a drifted pin with a
  TERMINAL non-retryable `tooling-venv` INVALID at the pre-launch ADMIT phase (no KSP
  boot). Fails if a stale/absent kRPC client silently certifies a flight, or if a
  venv fault is wrongly made retryable.
- **Mission-result round-trip + determinism.** `serialize_mission_result` then
  parse yields an equal object; identical inputs produce byte-identical JSON
  (stable key order). Fails if the result diffs churn or a field the harness reads
  is dropped.

### Fake-telemetry integration (mission shell, no game)

- **Fake-kRPC happy path (B1 + B2).** A fake telemetry/control seam injected into
  the mission (the kRPC calls sit behind an injectable interface, `mlib`-pure
  decisions drive it) replays a scripted flight; the mission writes MISSION-OK
  with the expected assertions. Fails if the shell mis-wires the phase machine to
  the (fake) kRPC surface.
- **Fake-kRPC connect failure -> MISSION-CONNECT-TIMEOUT.** The fake refuses every
  connect; the mission exhausts the retry and writes CONNECT-TIMEOUT + nonzero
  exit. Fails if the mission hangs (no connect budget) or exits OK.
- **Fake-kRPC phase stall -> MISSION-FLAKE.** The fake never advances past ASCENT;
  the mission times out the phase and writes FLAKE naming the stuck phase. Fails
  if a stalled autopilot wedges the mission instead of flaking.
- **Fake-kRPC exception -> MISSION-ERROR.** The fake raises mid-flight; the
  mission catches it, closes in `finally`, writes ERROR + traceback + nonzero
  exit. Fails if an exception leaks as a hang or as no result file.
- **run.py handoff over a fake mission subprocess.** A stub mission binary reading
  the params and writing a scripted mission-result drives run.py's autopilot
  handoff end-to-end (LoadGame OK -> mission MET -> CommitTree OK -> FlushAndQuit
  OK -> verifier chain -> PASS), and a stub writing MISSION-ASSERT-FAIL ->
  INVALID(mission)+retry. Fails if the handoff mis-maps a mission verdict or runs
  the mission before FLIGHT.

### PENDING-OPERATOR live items (agent cannot pilot KSP)

- **Live B1 pad-hop, once, on a provisioned stock-minimal instance.** Bootstrap
  the mission venv (confirm `krpc==0.5.4` resolved via pip, the `import krpc` +
  generated-code smoke pass, the RESOLVED protobuf version frozen into the stamp, and
  the protobuf pin promoted into `requirements.txt`), run the
  B1 scenario: the harness admits the instance, stages the pad-craft fixture,
  boots via seam LoadGame, the mission connects to the auto-started kRPC server,
  flies the hop (throttle+stage+coast+chute), lands, asserts the apoapsis window +
  landed state -> MISSION-OK; CommitTree + FlushAndQuit; the analyzer (Forbid)
  greens the produced recording, the REC log rules pass, `recordings.count == 1`
  -> PASS. Grep evidence: the `[Mission]` connect/phase/verdict lines, the
  `Recording started`/`Recording stopped` lines, the `RED=0` analyzer header, the
  written result JSON + `_mission.json`.
- **Live B2 LKO-ascent, once.** As B1 but MechJeb AscentAutopilot to 80 km
  circular; confirm the orbit-param assertions pass within tolerance and Parsek
  recorded the ascent (an orbital-checkpoint recording the analyzer greens).
- **Live flake capture.** Deliberately under-craft (low-TWR) or under-budget one
  run to confirm a MISSION-FLAKE surfaces as INVALID(autopilot-flake) + retry-once
  + a flake-ledger entry, and that a MISSION-ASSERT-FAIL (a deliberately wrong
  apoapsis window) surfaces as INVALID(mission), NOT PARSEK-FAIL. This proves the
  mission-validity gate keeps autopilot variance out of the Parsek-defect bucket.

## Deferred Items and Open Questions

Recorded so they are not lost; none blocks the two v1 missions.

- **launch_vessel (VAB flow) is DEFERRED.** v1 flies the fixture save's
  PRE-PLACED vessel only. Building/launching a craft from the VAB (or via
  TestingTools) into a fresh launch is deferred; it needs a launch seam or a
  scripted VAB flow and is not required for B1/B2.
- **Explicit StartRecording fallback.** If a future fixture cannot reliably
  auto-record the pre-placed vessel (non-PRELAUNCH situation), the driver would
  add an explicit `StartRecording` seam step before the mission phase. v1 fixtures
  are authored PRELAUNCH-on-pad so `autoRecordOnLaunch` is the honest trigger.
- **Missions B3-B8 (catalog).** EVA branch, undock split, staging, rendezvous +
  dock, Mun transfer, and the loop mission are the plan's next flown blocks; they
  reuse this module's mission-runner + `mlib` phase-machine pattern and the same
  handoff, and land after B1/B2 are green (docking/EVA are the HARD autopilots per
  catalog 5.4, budgeted by the flake policy).
- **Scene-exit commit variant.** v1 commits via the `CommitTree` seam verb; the
  plan 3.3 alternative (kRPC scene change to Space Center, then FlushAndQuit) is a
  future variant, not needed for the two atomic blocks.
- **Run-budget >= sum-of-step-budgets spec cross-check.** A too-tight run budget
  relative to the mission surfaces today as a KILLED (M-A5 watchdog); a
  spec-validation arithmetic cross-check is a cheap future guard.
- **Multi-instance ports.** v1 uses the default `127.0.0.1:50000/50001`; a
  multi-instance parallel layout (distinct ports per instance) is deferred with
  M-C1 multi-session orchestration. The mission already takes host/port CLI args
  so the layout is a run.py wiring change, not a mission change.
- **kRPC client source pin.** If `krpc==0.5.4` is absent from PyPI at bootstrap,
  the fallback installs the SEPARATE `krpc-python` client release asset (its own URL
  + sha256 recorded in `pins.toml` as a `[krpc_python_client]` entry), NOT the
  GameData-only `krpc-0.5.4.zip` (703 KB, DLL-only server layout, no python client
  inside); the resolved source (PyPI vs krpc-python asset) is recorded in the venv
  stamp. Confirming which source PyPI actually offers, and the krpc-python asset URL +
  hash, is a network step deferred to bootstrap time.
