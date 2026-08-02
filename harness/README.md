# harness/ - Parsek automated-testing module

For WHAT IS DONE / PROVEN / GATED across the whole initiative (modules,
test cases, roadmap), see `docs/dev/autotest-status.md` - the single status
authority. This README owns module MECHANICS only.

This directory is a self-contained module. It is designed so it could later be
split into its own repository and consumed by Parsek as a git submodule at this
same path. Keep it that way.

## Ownership boundary

Everything the harness fetches or generates lives UNDER `harness/`:

- Code: `run.py`, `lib/` (`hlib.py` pure decision library + `oracle.py`, the
  M-B2 pure ledger oracle, + `saveparse.py`, the M-C2 pure save-structure
  parser/evaluator behind the `saveParse` verifier row), `provision/` (`provlib.py` pure, `provision.py`
  shell), `missions/` (M-B1: mission shells + `lib/mlib.py` pure mission
  decisions + `bootstrap_venv.py`), and their `test_*.py`. run.py drives
  seam-only scenarios AND autopilot scenarios (the mission handoff spawns the
  mission subprocess with the venv python; venv admission runs at pre-launch
  ADMIT), plus the M-B2 ledger-oracle verifier (pre-launch seed baseline,
  per-run action manifest, expected-vs-save diff -> PARSEK-FAIL(ledger) on
  hard drift). On an autopilot scenario whose mission step comes back UNMET,
  run.py drives the CLEANUP tail steps only (`hlib.SEAM_VERB_TAIL_ROLE`;
  design-autotest-harness-core.md "The unmet-mission tail"), so a scenario
  whose tail contains irreversible in-world verbs cannot fire them over a
  flight that never reached its envelope.
- Declarative inputs: `scenarios/*.toml` (incl. `[expectations.ledger]`),
  `coverage/registry.toml`, `provision/pins.toml`, `provision/profiles/*.toml`,
  `missions/<name>.schema.toml` + `missions/requirements.txt`.
- Generated per-run (gitignored): `results/<runId>.json` + `<runId>.manifest.json`
  + `<runId>_mission.json`.
- The runId is `<UTC minute>_<scenarioId>[_run<N>][_a<N>]`. Every per-run
  artifact is keyed by it ALONE, so it is resolved against what `results/`
  already holds rather than assumed free (`hlib.resolve_run_id` over
  `hlib.claimed_run_ids`), then STAKED (`results/<runId>.claim`,
  exclusive-create, never reaped) so two CONCURRENT invocations cannot both take
  a free id: a second run of one scenario inside the same minute
  takes `_run2` and run.py Warns naming both ids -- it never overwrites an
  earlier run's result JSON, `_shots` dir or contact sheet. `_a<N>` is a
  different axis: the `[retry] policy` re-flying ONE run, which deliberately
  shares the stem and is distinguished by the attempt suffix.
- Caches + scratch (gitignored): `provision/.cache/` (release zips, the
  module-owned git source clones `krpc-src` / `krpc_mechjeb-src`, kRPC compile
  refs, the built `TestingTools.dll`) and `provision/.stage/`.
- Generated outputs (gitignored): `results/`, `coverage/coverage.*`, `flake.json`.

Provisioned KSP instances live at the umbrella root under `automation/`
(gitignored), NOT here. The dev KSP install (`../Kerbal Space Program/`) is a
documented READ-ONLY source (never written).

## The only reach-out into Parsek

The harness consumes a fixed Parsek-repo contract surface (which would remain in
the Parsek repo under a submodule split): `scripts/analyze-recordings.ps1`,
`scripts/validate-ksp-log.ps1`, `scripts/inject-recordings.ps1`,
`scripts/collect-logs.py`, the dotnet-test-hosted `Parsek.Analyzer`, the deployed
`Source/Parsek/bin/Debug/Parsek.dll` under test, and the launch-time seam/hooks
env contracts (`PARSEK_TEST_COMMANDS`, `PARSEK_AUTORUN_TESTS`, etc.).

Do NOT add new reads or writes outside `harness/`, `automation/`, the target
instance, the read-only dev KSP install, or that contract surface. The pinned
kRPC / KRPC.MechJeb git sources are cloned into `provision/.cache/<comp>-src`
(NOT the umbrella `mods/` clones); `--krpc-src <path>` optionally overrides the
kRPC source with an existing clone.

The instance's kRPC `PluginData/settings.cfg` is OWNED by the provisioner: every
provision/repair pass overwrites it with the complete golden template
(`provlib.KRPC_GOLDEN_SETTINGS_LINES`), discarding any hand or in-game edits by
design - a PARTIAL file zero-defaults every omitted key and silently disables
all RPC execution (maxTimePerUpdate=0). Tune kRPC by editing the template, not
the instance file.

Full enumeration + submodule split recipe:
`docs/dev/design-autotest-stack-setup.md` ("Module boundary and submodule
readiness"), cross-referenced from `docs/dev/design-autotest-harness-core.md`.

## Live observability (what is the run doing RIGHT NOW)

The observability surface for a running (or just-finished) mission flight,
newest-first (design authority: `docs/dev/design-live-observability.md`):

- `results/<runId>_mission.stdout.log` - the LIVE mission log. Written
  unbuffered by the mission subprocess, so its tail is current to the last
  poll frame (~0.5 s). Line format `[Mission][LEVEL][Phase] message`;
  telemetry lines are rate-limited to ~1 Hz, phase transitions / actions /
  [Plan]/[Point]/[Throttle]/[Warp] events are loud one-shot lines.
- `results/<ts>_harness.log` - the per-invocation harness log (which step the
  orchestrator is in; the mission stdout is folded in AFTER the mission
  exits, so mid-flight you read the mission log directly).
- `results/<runId>_status.json` - the live status file (Phase 2, shipped):
  every production mission rewrites it atomically every ~2 s with the
  decoded snapshot, the machine decision state (phase, rounds,
  planAttempts, bodyBlank, burn latches, warp commands), and the last 10
  sparse events. The status CLI prefers it when fresh and falls back to
  log parsing (older runs / a stalled mission process).
- In-log observability (Phase 2): a ~5 s rate-limited `machine phase=...`
  decision-state line, a trailing `ut=` token on the telemetry line, loud
  `gate <field> old->new | <snapshot values>` lines on every machine
  latch/gate flip, and a 20-frame `window dump` (compact one-line-per-frame
  ring buffer) on phase transitions / flakes / vessel-lost / gate flips.

`status.py` renders all of that as one panel so an operator report ("looks
stuck at 1x", "warp oscillating") maps to machine state in ONE step:

```
python status.py                    # newest run, one shot
python status.py --watch 5         # re-render every 5 s
python warp_audit.py results/<runId>_mission.stdout.log   # no-1x-coast PR-gate audit
python warp_audit.py <log> --fail-on-violation            # exit 1 on any 1x coast segment
python status.py --run 2026-07-22_1210    # a specific run (prefix ok)
python status.py --raw 40          # last 40 raw mission-stdout lines
python status.py --head 650        # REPLAY: panel as of the first 650 lines
```

The panel shows: scenario + attempt + run age, log liveness (last-write age),
current phase with time-in-phase (game est. via the time-to-SOI drift, wall
est. via the 1 Hz telemetry cadence, budget from the scenario TOML), the last
telemetry line decoded one labeled field per line, the last sparse events,
the full phase history with durations, and a heuristic WHAT IS IT DOING line
(e.g. it names a PLAN-CORRECTION over-cap plan-removal loop -- which looks
like a silent 1x hang in game -- and predicts the fall-through time).
Stdlib only; parsers are pure functions tested in `lib/test_status.py`.

## Contact sheets (V3): what a run leaves for the human eye

Every attempt - PASS included - runs a light UNCONDITIONAL artifact step in
`run.py`: the run's `KSP.log` (bounded - whole file up to 64 MiB, else first
8 MiB + last 56 MiB with an explicit truncation marker; decision in
`hlib.plan_artifact_log_copy`) plus any `Screenshots/` files stamped inside
the run's wall-clock window (`hlib.select_run_screenshots`; the V4 capture
verbs will feed this) land in `results/<runId>_shots/`. The heavy non-PASS
collect-logs snapshot is unchanged, and the verdict is written durably BEFORE
the artifact copy starts (a kill mid-copy can never cost a result). A
retention pass then bounds cross-run growth (`hlib.select_shots_dirs_to_prune`:
newest 40 `*_shots` dirs / 2 GiB total kept, the current run's dir always
protected) -- only the heavy shots dirs are pruned; result JSONs, summary, and
the contact pages (which embed the extracted key lines as text) are permanent.

`tools/contact_sheet.py` then renders `results/<runId>_contact.html` - any
captured images next to the run's key log lines (`BATCH_COMPLETE`, every
`faithful-parity summary`, every `phase=Anomaly` raise with its +/-3 nearest
`probe frame summary` lines) plus the verifier verdict rows - and
`results/index.html` (all runs newest-first, verdict-colored). Self-contained
static HTML, no external assets; generated failure-isolated at the end of
every attempt (a sheet failure is a Warn, never a verdict change), and safe on
every partial input (no KSP.log, no images, malformed result JSON). By hand:

```
python tools/contact_sheet.py                # all sheets + index
python tools/contact_sheet.py --run-id <id>  # one run's sheet + index
python tools/contact_sheet.py --index-only   # just the index
```

## Running the tests

```
cd harness
python -m unittest discover -s lib -q
python -m unittest discover -s provision -q
python -m unittest discover -s missions/lib -q
```

Stdlib only (no pytest, no third-party deps) on the BASE interpreter: the
mission shells lazy-import krpc inside their connect function, so all three
discovery roots run with no venv. The `lib` root is the one that reads OUTSIDE
`harness/`: `CommittedBatchTallySourceSyncTests` walks `../Source/Parsek` to
cross-check each spec's pinned `BATCH_COMPLETE` tally against the C#
`[InGameTest]` attributes, and `test_doc_spec_sync.py` reads `../docs/dev`. Both
are read-only and need the full repo checkout, not a built DLL. The ONLY third-party python lives in the
mission venv (`missions/requirements.txt`, bootstrapped by
`missions/bootstrap_venv.py`, gitignored `.venv/`), used exclusively by the
mission subprocess at flight time. A `--dry-run` provision needs no
network, downloads, or writes outside `harness/`:

```
python provision/provision.py --profile stock-minimal --dry-run
```
