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
  parser/evaluator behind the `saveParse` verifier row - `[expectations.rewind]`,
  `[expectations.recordings.structure]`, the gate-12
  `[expectations.recordings.points]` block that asserts recordings actually
  RECORDED something rather than merely existing as `.prec` files, and
  `[expectations.routes]`, the SUPPLY-ROUTE block over the `ROUTES` /
  `DORMANT_ROUTES` nodes: route count, statuses by `RouteStatus` NAME, stops,
  SOURCE rows, origin / destination bodies, route-id and endpoint-pid identity,
  plus a `codecRejects` counter for a route the game would DROP on the next
  load. There is deliberately NO escrow facet - `RouteStore`'s `cargoEscrow` is
  pure RAM and no save carries it),
  + `rendercompose.py`, the M-A7 pure render-composition parser / clock-math
  re-derivation / RC-* rule set behind the `renderCompose` verifier row
  (`[expectations.renderComposition]`, evaluated over the produced
  `parsek-render-manifest.txt`; REPORT-ONLY, no committed spec arms it),
  + `ghostlife.py`, the pure flight-scene ghost-lifecycle parser/evaluator
  behind the `ghostLifecycle` verifier row (`[expectations.ghostLifecycle]`:
  GhostRenderTrace MeshSpawned/MeshDestroyed per-recId spawn/destroy balance,
  spawn-census window, zero-spawn vacuity floor; REPORT-ONLY unless armed via
  `GHOSTLIFE_ARMED_SPECS` - empty as shipped),
  `provision/` (`provlib.py` pure, `provision.py`
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

## The machine lock (only one run at a time, machine-wide)

`run.py` and `provision/provision.py` are mutually exclusive, arbitrated by ONE
lockfile:

```
<umbrella-root>/automation/.ksp-machine.lock
```

**Why the MACHINE and not the instance directory.** The resources an automation
run monopolises are machine-global, not per-instance: the hardcoded kRPC ports
(50000/50001), the single GPU, and the timing-sensitive physics clock a second
concurrent KSP perturbs. A per-instance lock let two different profiles both
acquire, both launch, and both bind 50000 - where the loser's bind fails soft and
its mission then drives the WINNER's game, a live path to a false PASS.

**What takes it.** A whole `run.py` invocation (acquired before the spec loop,
released in a `finally`, timestamp heartbeated at each scenario boundary) and a
whole `provision.py` run including `--repair`. A nightly therefore holds the
machine for its full duration, which is the point.

**What does NOT take it,** deliberately: `--dry-run` (launches nothing), the unit
suites, venv bootstrap, `collect-logs.py`, and `dotnet build`'s deploy to the DEV
instance. Locking routine dev builds behind an 8h harness hold would be worse
than the disease; the dev install stays under the hash-verify discipline in
`.claude/CLAUDE.md`.

**On contention** both shells fail FAST (no queueing): `run.py` writes one
`INVALID(instance-locked)` result per selected scenario and exits 1;
`provision.py` aborts `EC-10`. Neither ever touches the holder's lockfile. The
refusal names the holder's pid, worktree, selection and start time.

**Stale locks** are reclaimed automatically two ways: the holder pid is dead, or
the lease (`provlib.DEFAULT_LEASE_SECONDS`, 8h) has expired - the second is the
backstop for Windows pid reuse, which pid-liveness alone cannot escape. A live
run heartbeats its timestamp, so expiry never fires on a genuinely running
selection. If you are certain nothing is running and want the machine back now,
delete the lockfile.

The coarse "any `KSP_x64.exe` alive" zombie preflight is UNCHANGED and still runs
per attempt. It is the backstop for KSPs no lockfile knows about (a manual
launch, an orphan from a killed run). It cannot bind a pid to a directory, so a
dev-instance KSP being open also refuses a harness run - a deliberate
false-positive, since one GPU means it would perturb the flight anyway. **Do not
narrow it without re-reading this section** (the deferred residual R8
"`_ksp_running_against` coarseness" in `docs/dev/design-autotest-stack-setup.md`
- NOT the unrelated R8 in `autotest-roadmap.md`): it is a second, independent
guard.

### Known constraint: the key is the umbrella root

The lockfile path is derived from the umbrella root, so exclusion holds across
checkouts that SHARE an umbrella - the documented sibling-worktree layout. Two
invocations given different `--umbrella-root` values, or run from a checkout
whose parent differs (e.g. a nested `.claude/worktrees/<name>` isolation
worktree), resolve DIFFERENT lockfiles while still contending for the same
machine-global kRPC ports and GPU. This is deliberate: a truly machine-global
path would serialize the unit suites and `--umbrella-root` test runs against
real runs. `--instance-dir` bypasses umbrella resolution entirely, so pointing it
at the shared automation instance from an odd umbrella flies that instance under
a different lock. Prefer the default umbrella; treat those flags as
single-operator tools.

## Running a tier on request (agent-driven)

Tests are run BY TYPE, on request: the operator asks an agent for a tier and the
agent flies it and reports back. The `run-tier` project skill
(`.claude/skills/run-tier/SKILL.md`) encodes that flow - coordination preflight,
build/instance agreement, invocation, and what to say about the result. The same
command works typed by hand:

```
cd harness && python tools/tier_runner.py --tier daily     # or nightly / operator
```

`tools/tier_runner.py` is a thin wrapper around `run.py` that owns only what
`run.py` cannot: keeping the box awake for the duration, refusing to queue
behind a busy machine, classifying the whole invocation, and leaving a one-line
trace to read afterwards. It never decides a verdict, never re-runs a scenario,
and never provisions.

| Tier | Specs | What it is |
|---|---|---|
| `daily` | 22 | the everyday set; ~20 min observed |
| `nightly` | 46 | the full set; hours |
| `operator` | 11 | explicit-invocation-only specs (see below) |

Spec counts are as of 2026-08-05 and DRIFT as specs land; recount with
`python run.py --dry-run --tier <t>` (launches nothing, takes no lock).

`--tier` is an EXACT match, so the three are disjoint: `--tier nightly` does not
re-run the daily specs. Asking for two types means two requests, deliberately -
one run holds the machine, and the second would skip rather than queue.

**The `operator` tier is requestable** even though `hlib.CADENCE_TIERS` maps no
cadence to it: the carve-out's rule is that an operator-tiered spec runs "ONLY
on an explicit `--tier operator` / `--id` invocation", and this runner's
invocation is exactly that - not a `--cadence` sweep that would pick the tier up
implicitly. The cost the carve-out protects against is real, though (a
(b)-tiered spec is EXPENSIVE AND UNFLOWN, e.g. B16-eve-orbit at ~2.6 h per
attempt), so treat that tier as a deliberate, occasional request.

**Policies the runner enforces:**

- **Wake hold.** The runner pins the system awake for the whole invocation
  (`ES_CONTINUOUS|ES_SYSTEM_REQUIRED`). Without it, a multi-hour tier nobody is
  sitting with is suspended by the idle timer about two minutes in, mid-flight.
- **Skip, do not queue.** If the machine lock is held or any `KSP_x64.exe` is
  alive, the runner exits 3 having invoked nothing, and the requester is told
  who holds it. Letting `run.py` refuse instead would stamp one junk
  `INVALID(instance-locked)` result JSON PER SELECTED SPEC (46 for the nightly
  tier) into `results/` and onto the index, burying real history under a run
  that never flew.
- **Never auto-provision.** An unfit instance reports `NEEDS-PROVISION`
  (exit 2) and stops. Provisioning DEPLOYs whatever
  `Source/Parsek/bin/Debug/Parsek.dll` the invoking worktree holds, which is as
  likely to be a half-finished build as a release candidate; which worktree's
  DLL the automation instance carries is a human call, always.
- **Never re-run a finding.** `run.py` owns retry policy; a PARSEK-FAIL that
  reaches the history line already survived it.

Runner exit codes: `0` GREEN, `1` RED (a finding, a KILLED, an XPASS, or the
"flew nothing" shape), `2` NEEDS-PROVISION, `3` SKIPPED, `4` runner internal
error.

### Reviewing a run

When a requested tier finishes - hours later for a nightly - four surfaces, each
answering a different question:

1. **`results/index.html`** - one open. Every run newest-first, verdict-colored,
   linking each run's contact sheet.
2. **`results/tier-runs/history.txt`** - one line per invocation
   (`<utc> tier=… outcome=… exit=… <tally> log=…`). A MISSING line means the
   runner never got that far: it was never started, or it died before its
   history append. The named `results/tier-runs/<tier>_<stamp>.log` holds that
   invocation's full tee.
3. **`results/summary.txt`** tail - the raw verdict-per-attempt ledger.
4. **`coverage/flake.json`** - which scenarios are drifting toward quarantine.

What a red means:

- **PARSEK-FAIL** - a finding. File it (forensics go in
  `docs/dev/todo-and-known-bugs.md`). NEVER re-run it away: run.py already owns
  retry policy, so a PARSEK-FAIL that reached the history line survived it.
- **INVALID** with subkind `drift` / `manifest-missing` / `provision-incomplete`
  - the instance is not fit; the runner labels the whole invocation
  `NEEDS-PROVISION`. Provision from the intended worktree
  (`cd harness && python provision/provision.py --profile stock-minimal`).
- **KILLED** - a budget kill. Always called out explicitly in the tally so it
  cannot hide behind twenty passes beside it.
- **XPASS** - amber. An expected-fail guard now passes: confirm the bug is
  closed, then remove the `expectedFail` key so it stops being expected.

### What accumulates, and what may be pruned

Permanent by design, and NOT to be pruned by any automation: `results/*.json`,
`results/summary.txt`, the per-run `*_contact.html` + `index.html`,
`results/<ts>_harness.log`, and the mission stdout logs. They are the whole
historical record, and they are small (text).

Automatically bounded: `results/<runId>_shots/` (run.py keeps the newest 40 dirs
/ 2 GiB - the only heavy artifacts), and `results/tier-runs/*.log` (the runner
rotates its OWN logs at 60 days; it never touches anything else, and never
`history.txt`).

Safe to prune by hand if the disk ever demands it: old `_shots/` dirs and old
`<ts>_harness.log` files. **Never delete `results/*.claim`** - the run-id stakes
are exclusive-create and load-bearing (see the ownership boundary above);
deleting one lets a future run overwrite an earlier run's records.

## Fixture saves and the shared craft library

`fixtures/saves/<name>/` holds committed KSP save templates; `run.py::stage_fixture`
copies one verbatim into the provisioned instance's `saves/` for each run. Their
contents and the pinned career constants are documented in
`fixtures/saves/README.md`.

A craft flown by two or more of those fixtures is committed ONCE under
`fixtures/ships/` and overlaid into the staged save's `Ships/VAB/` at stage time,
per `fixtures/shared-ships.toml`. That directory is what kRPC's
`SpaceCenter.launch_vessel("VAB", <name>, ...)` resolves
`<save>/Ships/VAB/<name>.craft` against, so the craft has to BE there at run time -
it just no longer has to be there in git. Committing a physical copy per save cost
180,799 duplicated lines across 27 files (twelve byte-identical `Kerbal X.craft`),
and made every copy an independent drift risk: `build_dd1_craft.py` had grown a
three-path gate purely to chase its own copies.

Two rules when adding or harvesting a fixture:

- **Needs a shared craft?** Add the save's row to `shared-ships.toml`. Do NOT copy
  the `.craft` into the fixture - `SharedShipsManifestTests` (in `lib/test_hlib.py`)
  hashes the whole fixture tree and reds on any file byte-identical to a library
  craft, including one that was renamed.
- **Craft used by exactly one fixture?** It stays physically in that fixture and is
  NOT listed (`gloops-airshow/Ships/VAB/Auto-Saved Ship.craft` is the only one
  today). The same test reds a library craft that drops to one consumer.

### Per-spec live endpoint state (`[[fixture.liveState]]`)

One fixture, many endpoint states. A SUPPLY ROUTE replays a recorded run against
the CURRENT LIVE ENDPOINTS, so most of what a dispatch lane measures is decided by
two numbers that live in `FLIGHTSTATE` and nowhere else: what the source holds at
the crossing, and how much headroom the destination has. Authoring the origin-empty,
origin-partial, cargo-missing, destination-full, destination-partial and
destination-empty edges as separate FIXTURES would mean one full save tree per
variant (a `persistent.sfs` plus ~39 sidecars) whose Parsek payload is
byte-identical to its siblings and whose only difference is a single `amount =`
line - N re-harvests to maintain and N shapes in `RECORDED_FIXTURES` that can drift
apart without anything noticing.

A spec declares the state it wants instead:

```toml
[fixture]
saveTemplate       = "fixtures/saves/rover-relay-c-recorded"
injectedRecordings = "none"

[[fixture.liveState]]
pid       = 90564594                 # rover B, the pickup source
resources = { LiquidFuel = 0 }       # drain the tank
inventory = "clear"                  # and empty both containers
```

* `pid` - a `FLIGHTSTATE` `persistentId`. Exactly one vessel must carry it.
* `resources` - resource name -> amount. Exactly one matching `RESOURCE` node must
  exist on that vessel (a multi-tank vessel is REFUSED rather than guessed at: "100"
  would mean two different things and the tokens derived from it would be
  underivable). An amount above `maxAmount` is a SPEC ERROR and aborts; it is never
  clamped, because a silent cap makes every derived token wrong while the run stays
  green.
* `inventory` - `keep` (the default; omit the key), `clear` (empty every
  `ModuleInventoryPart` and drop its `inventory` CSV key, the shape KSP itself
  writes), or `restore-dock-endpoint:<windowIndex>` (restore from that route
  window's own `DOCK_ENDPOINT_INVENTORY` snapshot - the fixture's own recorded
  bytes).
* `remove = true` - DELETE the vessel's whole `VESSEL` node, the only way to author
  "the endpoint this route names is no longer in the save" without a second harvest.
  Exclusive with `resources` / `inventory` (both would patch a node this entry then
  deletes), and REFUSED for any vessel at or before the save's `activeVessel` INDEX:
  that key is positional, so removing an earlier vessel silently re-points the focus
  at a different craft and every token the lane derives becomes a statement about a
  different scene. It does NOT by itself produce an `EndpointLost` hold -
  `RouteEndpointResolver` walks root-part -> pid -> SURFACE PROXIMITY, so on a
  surface endpoint a removal only opens the third step, and whether that step misses
  is a property of what else is parked near the recorded coordinates (on
  `rover-route-recorded` it does not miss: RVR-18).

A sibling key covers the one career quantity a route lane's arithmetic runs on:

```toml
[fixture.career]
funds = 7409                          # one dispatch cost short, so the gate refuses
```

`[fixture.career]` is a single table, not an array, and today accepts `funds` only.
It rewrites the `funds` key of the save's one `SCENARIO { name = Funding }` node and
ABORTS on a save carrying none - which is exactly what a career declaration on a
SANDBOX template looks like from inside the applier. It is deliberately NOT a
`liveState` entry: that block is FLIGHTSTATE-only by contract, and that boundary is
its whole safety argument. The declared number IS the number the dispatch gate reads:
`LedgerOrchestrator.EnsureInitialFundsSeed` seeds the ledger FROM
`Funding.Instance.Funds`, and `PatchFunds`' guarded uplift refuses to raise the live
pool to a ledger running balance above it (RVR-4 measured that clamp on this very
fixture family), so committed milestone rows do not move it.

**THE TWO 2026-09-03 ADDITIONS ARE LIVE-PROVEN TOO**: `remove = true` on RVR-18
(`2026-09-03_2011`, PASS attempt 1 - `liveState patched pid=2123618197 name=rover fuel 0
removed=1`, and the route then resolved through the proximity step to a different
vessel), and `[fixture.career] funds` on RVR-17 (`2026-09-03_2010`, PASS attempt 1 -
`career patched funds 11000->7409`, with the game's own guarded-uplift clamp then
reporting `live=7409`, i.e. the declared seed reached the pool the dispatch gate reads).

**LIVE-PROVEN 2026-09-03** across the six RVR-8..RVR-15 lanes that stage one: every
harness log carries its patch line (e.g. `liveState patched pid=90564594 name=B
resources=[LiquidFuel 200->0] inventory=keep`) and every run then measured the route
gate reading exactly that state. A patch that had silently done nothing would have left
those lanes measuring the UNPATCHED fixture - where the cycle DELIVERS - so their own
required tokens are the mechanism's falsification rather than a separate assertion
about it. `inventory = "clear"` is proven too (RVR-12, RVR-15): it is the only mode
that rewrites container bodies rather than one `amount =` line, and KSP loaded,
resolved and gated both saves without complaint.

WHERE IT RUNS AND WHAT IT TOUCHES. `run.py::stage_fixture` step 3b, on the STAGED
COPY, after the template copy and after any injection (last writer wins). FLIGHTSTATE
only: no recording, window, branch point, origin proof or ledger row is ever
rewritten - the route windows are the patcher's INPUT, so editing one would make a
restore unfalsifiable. Line endings are preserved (`rover-relay-c-recorded` is LF
where every builder-authored fixture is CRLF).

FAIL CLOSED, PRE-BOOT. A pid the save does not carry, a resource the vessel does not
have, an amount over capacity, a window index out of range: every one aborts as
`INVALID(staging)` with the cause named and KSP never launched. The shape is checked
one layer earlier still, by `hlib.validate_spec`, so a malformed declaration is
INVALID-SPEC before the instance is even prepared. There is deliberately no
fail-open path: a patch that quietly did nothing would leave the lane measuring the
UNPATCHED fixture and reporting green.

ONE IMPLEMENTATION, SHARED WITH THE BUILDER. `harness/lib/savepatch.py` owns the
snapshot-lift, slot-placement and `inventory` CSV logic, and
`tools/build_rover_relay_c_recorded.py` (whose step 3 does the same edit at BUILD
time, to stage that fixture at start-of-cycle) calls it rather than carrying a copy.
`lib/test_savepatch.py` asserts the identity with `is` and proves the sharing
behaviourally: applying `restore-dock-endpoint:<N>` to an endpoint the builder
already restored from the same window is a BYTE-IDENTICAL no-op, and clearing then
restoring returns the committed bytes exactly.

WHAT IT CANNOT DO, and why that is a property of the bytes: there is no `fill` mode,
no `restore-undock-endpoint:<N>` and no `relocate`. An `UNDOCK_ENDPOINT_INVENTORY`
snapshot is not a census of the resulting inventory (on `rover-relay-c-recorded`
window 1 it carries four items, two of them the same part name at the same
`slotIndex`, against a live rover holding six, with no container index recorded), and
a fill mode would mean authoring `STOREDPART` nodes no snapshot ever wrote. A
`relocate` mode would change NOTHING for a route whose endpoint still carries a live
pid: `RouteEndpointResolver`'s pid step is position-blind and wins before proximity is
consulted, so moving a vessel is only observable once its pid is gone - which is what
`remove` already does.

The destination-slots-full edge is not expressible BY STAGING on
`rover-relay-c-recorded` (rover A starts with three free slots and one cycle consumes
at most two), and that limit is a property of THAT fixture rather than of this
mechanism: on `rover-route-recorded` the destination starts with three free slots and
one cycle consumes TWO, so RVR-16 reaches the same edge by PLAYING the fixture with a
single `resources` declaration. The relay-c version of the edge still needs a second
harvest; it stays filed in `docs/dev/autotest-roadmap.md` item 15.

### Recording sidecars: what is committed and what is derived

A fixture's `Parsek/Recordings/` carries the AUTHORITATIVE sidecars - `<id>.prec`
(trajectory) and `<id>_vessel.craft` / `<id>_ghost.craft` (snapshots), all binary -
plus ONE derived text mirror, `<id>.prec.txt`.

Parsek writes a readable text mirror beside each of the three at runtime
(default-on diagnostics). Only the trajectory one is committed, because scenario
headers cite values read straight out of it (`V6M-mun-player-loop`,
`V6T-mun-ts-arrival`, `V7M-minmus-player-loop`, `M1-mission-loop-unit`). The two
SNAPSHOT mirrors are not committed: nothing cites one, they cost 334,023 lines
over 99 files, and they are strictly derived - an offline decode reconstructs all
99 byte-for-byte from the retained binaries, which are a strict superset of the
text. **To read one, load the fixture in KSP**; the text is regenerated from the
binary that is still in git. The two halves come back by different routes, which
is worth knowing before relying on either: the VESSEL mirror has a real write-path
fallback (`ReconcileReadableSidecarMirrors`'s `AuthoritativeSidecar` branch,
`RecordingSidecarStore.cs:1313-1321`), while the GHOST mirror has none - it comes
back only because `LoadSnapshotSidecarsFromPaths` hydrates the snapshot from the
binary at load (`:443-449`), so the in-memory branch fires on the next save. Gated by
`CommittedFixtureMirrorTests`, which also asserts the binaries and the cited
`.prec.txt` are still there - dropping a mirror is only safe while the thing it is
rebuilt from survives.

### What a fixture must NOT carry

Harvesting a produced save brings along everything KSP and Parsek wrote beside
the state a scenario needs. Three populations are exhaust, all pruned by
`harvest_bdock_station.py` and gated in `lib/test_hlib.py` /
`lib/test_saveparse.py`:

| Not committed | Why | Gate |
| --- | --- | --- |
| `*_vessel.craft.txt`, `*_ghost.craft.txt` | derived; the mod rebuilds them from the committed binaries | `CommittedFixtureMirrorTests` |
| `Parsek/Saves/parsek_rw_*.sfs` + their `rewindSave =` hints | Rewind-to-LAUNCH quicksaves are RUN exhaust, not fixture payload: the `InvokeRewindToLaunch` seam verb (added 2026-08-27 with GS-4) reaches the one the run's OWN recording captured, never a committed one - a fixture-committed `parsek_rw_*` would be a stale world snapshot nothing can validate, and the one analyzer rule that looks (`Inv9RewindPoint`) only checks existence and parseability, never content | `CommittedFixtureRewindSaveTests` |
| `quicksave.sfs` / `quicksave.loadmeta` | near-copy of the fixture's own `persistent.sfs`; every in-game quickload uses a NAMED slot | `test_no_fixture_commits_a_quicksave` |

Two look like exhaust and are **payload** - do not confuse them:

- `Parsek/RewindPoints/rp_*.sfs` is deep-parsed by
  `RewindInvoker.PartLoaderPrecondition.Check`, and `test_saveparse` pins the
  count. Rewind-to-SEPARATION (the `InvokeRewind` seam verb) is a different
  system from the Rewind-to-LAUNCH quicksaves above.
- `<id>.prec.txt` is a live test input, not just a review surface:
  `OptimizerTransferCohesionTests` globs every fixture's `*.prec.txt` recursively
  and `ReaimTransferSynthesizerTests` names one directly.

The file/hint pairing matters: deleting a `parsek_rw_*.sfs` while leaving its
`rewindSave =` key dangles the reference, which `Inv9RewindPoint` WARNs on and
escalates to FAIL for a `CommittedProvisional` recording - analyzer RED under the
Forbid fresh-save gate. Both halves go together, which is what the gate pins.

`tools/harvest_bdock_station.py` drops harvested craft the library already holds
and prints the manifest row to add, and prunes the snapshot mirrors and
`Parsek/Saves` so a harvest cannot re-commit them. The overlay fails CLOSED, pre-boot, as `INVALID(staging)` on every way it can be
unsatisfiable - a declared craft missing from the library, a fixture that both
declares and commits one, and a manifest that is missing or unparseable. Never
silently, because the symptom otherwise surfaces minutes later as a
`launch_vessel` failure classified against a perfectly good spec. The
missing-manifest case is deliberately NOT treated as "degrade to the pre-library
behaviour": before the dedup a verbatim copytree carried the craft, and now it
carries nothing, so degrading would stage twelve fixtures craftless and report
success. A save with no manifest row is legitimate (most fixtures need no shared
craft) and logs a `no rows for save=` line, so the harness log can always tell
"overlay ran" from "no row".

The consumer-side gate is `test_every_spec_that_launches_a_craft_can_resolve_it`:
every spec with a `driver.missionParams.craftName` must resolve that craft from
its own fixture either physically or through a manifest row. That is the cell
that catches a dropped row before it costs a flight - the weaker "the saveTemplate
names a real directory" form stays green when a row goes missing. Pure resolution:
`hlib.plan_shared_ship_overlay` / `hlib.validate_shared_ships_manifest`.

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
