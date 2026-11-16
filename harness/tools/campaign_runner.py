"""Fly a campaign ladder: lanes in order, each staged from the previous lane's save.

    cd harness && python tools/campaign_runner.py run --ladder cs-career --name cs-1 --from CS-3 --to CS-4b
    cd harness && python tools/campaign_runner.py run --ladder cs-career --name cs-all --dry-run
    cd harness && python tools/campaign_runner.py status --name cs-1
    cd harness && python tools/campaign_runner.py stop --name cs-1
    cd harness && python tools/campaign_runner.py resume --name cs-1
    cd harness && python tools/campaign_runner.py promote --name cs-1 --stage CS-4

Every decision lives in ``lib/campaign.py`` (pure, unit-tested); this file does
the I/O. It is the parallel runner with a different queue: the machine lock,
the slot and DLL checks, the one Parsek.Tests build, the Unity prefs, the child
spawn, the dispatcher (``parallel.decide_next``) and the end-of-run
bookkeeping are ``tools/parallel_runner.py``'s own; what it adds is which
stages are ready (``campaign.ready_stages``) and the handover between them.

THE HANDOVER. When a stage ends green, its produced save is taken from the
run's own snapshot (``results/<runId>_save``, copied out of the instance under
the lock; never the instance save, which a sibling run re-stages within
seconds) and harvested at once with ``harvest_bdock_station.harvest`` into
``results/campaign/<run>/<chain>/<NN>-<id>/out/<leaf>/``. The next chained stage
gets a fresh copy in its own ``in/`` folder (plus the ``carryCraft`` of its
committed fixture) and a ``handover.json`` with this runner's token, and its
child runs ``run.py --slot <s> --stage-from <stage>/in``. A stage that is not
green, a green stage with no snapshot, or a harvest that refuses breaks its
chain there: later stages never fly, and ``campaign.txt`` names the stage.

Contract: harness/README.md, "Campaign chains".

EXIT CODES: 0 every selected stage green; 1 a chain broke, or stages were left
unflown; 2 a refused ladder, range or name, no slot for a profile, slots on
different DLLs; 3 the machine lock is held, or a stop left stages unflown; 4 the
runner itself broke.

Stdlib only.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import shutil
import sys
import time
import traceback
from typing import Any, Dict, List, Optional, Sequence, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import parallel_runner as pr  # noqa: E402  (puts lib/ and provision/ on sys.path)
import campaign  # noqa: E402
import hlib  # noqa: E402
import parallel  # noqa: E402

SELECTION_LABEL = "campaign-runner"


# ---------------------------------------------------------------------------
# Paths and reads
# ---------------------------------------------------------------------------


def campaign_dir(ctx: pr.Context, name: str) -> str:
    return os.path.join(ctx.results_dir, campaign.CAMPAIGN_RESULTS_DIRNAME, name)


def ladder_path(ctx: pr.Context, ladder: str) -> str:
    return os.path.join(ctx.harness_root, campaign.LADDERS_DIRNAME, ladder + ".toml")


def stage_dir(run_dir: str, stage: campaign.Stage) -> str:
    return os.path.join(run_dir, stage.chain, stage.dirname)


def load_ladder_table(ctx: pr.Context, ladder: str) -> Dict:
    import tomllib
    with open(ladder_path(ctx, ladder), "rb") as fh:
        return tomllib.load(fh)


def shared_ships(ctx: pr.Context) -> Dict[str, Sequence[str]]:
    import tomllib
    path = os.path.join(ctx.harness_root, "fixtures", "shared-ships.toml")
    try:
        with open(path, "rb") as fh:
            ships = tomllib.load(fh).get("ships") or {}
    except (OSError, ValueError):
        return {}
    return {str(k): tuple(v) for k, v in ships.items() if isinstance(v, list)}


def committed_craft(ctx: pr.Context, leaves: Sequence[str]) -> Dict[str, set]:
    out: Dict[str, set] = {}
    for leaf in leaves:
        vab = os.path.join(ctx.harness_root, "fixtures", "saves", leaf, "Ships", "VAB")
        try:
            out[leaf] = {n[:-len(".craft")] for n in os.listdir(vab) if n.endswith(".craft")}
        except OSError:
            out[leaf] = set()
    return out


def check_ladder(ctx: pr.Context, ladder: campaign.Ladder,
                 specs_by_id: Dict[str, Dict]) -> List[str]:
    """The file-backed half of a ladder check: every craft a stage launches
    reaches its staged save."""
    crafts = {s.spec_id: campaign.spec_craft_names(specs_by_id.get(s.spec_id, {}))
              for s in ladder.stages()}
    return campaign.craft_reach_problems(
        ladder, crafts, shared_ships(ctx),
        committed_craft(ctx, sorted({s.template_leaf for s in ladder.stages()})))


def load_records(run_dir: str, ladder: campaign.Ladder) -> Dict[str, campaign.StageRecord]:
    out: Dict[str, campaign.StageRecord] = {}
    for st in ladder.stages():
        rec = campaign.StageRecord.from_json(
            pr.read_json(os.path.join(stage_dir(run_dir, st), campaign.STAGE_RECORD_FILE)))
        if rec is not None and rec.spec_id == st.spec_id:
            out[st.spec_id] = rec
    return out


def write_record(run_dir: str, stage: campaign.Stage, rec: campaign.StageRecord) -> None:
    pr.write_json_atomic(os.path.join(stage_dir(run_dir, stage), campaign.STAGE_RECORD_FILE),
                         rec.to_json())


def harvest_module(ctx: pr.Context):
    return pr._load_by_path("harvest_bdock_station",
                            os.path.join(ctx.harness_root, "tools", "harvest_bdock_station.py"))


class Loaded:
    """A run's plan with its ladders: ``full`` (the whole ladder as it was when
    the run was made) and ``ladder`` (the range this run flies)."""

    def __init__(self, plan: Dict, full: campaign.Ladder, ladder: campaign.Ladder,
                 specs_by_id: Dict[str, Dict]):
        self.plan = plan
        self.full = full
        self.ladder = ladder
        self.specs_by_id = specs_by_id


def build_loaded(ctx: pr.Context, plan: Dict) -> Loaded:
    specs_by_id = {str(s.get("id")): s for s in pr.load_specs(ctx)}
    full = campaign.parse_ladder(plan.get("ladderTable"), specs_by_id)
    ladder = campaign.select_range(full, plan.get("from"), plan.get("to"))
    return Loaded(plan, full, ladder, specs_by_id)


def read_plan(run_dir: str) -> Optional[Dict]:
    plan = pr.read_json(os.path.join(run_dir, campaign.PLAN_FILE))
    if not isinstance(plan, dict) or plan.get("schema") != campaign.PLAN_SCHEMA:
        return None
    return plan


def write_verdict(run_dir: str, loaded: Loaded, records: Dict[str, campaign.StageRecord],
                  stopped: bool) -> Tuple[str, int, str]:
    outcome, code, detail = campaign.campaign_outcome(loaded.ladder, records, stopped)
    lines = ["# campaign %s ladder=%s outcome=%s exit=%d"
             % (loaded.plan.get("name"), loaded.full.name, outcome, code)]
    for st in loaded.ladder.stages():
        rec = records.get(st.spec_id)
        lines.append("stage %s %s %s source=%s" % (
            st.chain, st.spec_id, campaign.describe_record(rec) if rec else "NOT-FLOWN",
            (rec.source if rec and rec.source else "-")))
    lines += [o.line() for o in campaign.chain_outcomes(loaded.ladder, records)]
    pr.write_text_atomic(os.path.join(run_dir, campaign.VERDICT_FILE), "\n".join(lines) + "\n")
    return outcome, code, detail


# ---------------------------------------------------------------------------
# The handover
# ---------------------------------------------------------------------------


class HandoverError(Exception):
    pass


def prepare_stage(ctx: pr.Context, run_dir: str, loaded: Loaded, stage: campaign.Stage,
                  records: Dict[str, campaign.StageRecord], token: str,
                  log: pr.RunnerLog) -> Tuple[List[str], str]:
    """Clear the stage's folder of an earlier attempt and, for a chained stage,
    build its ``in/`` save and handover marker. Returns (extra run.py argv,
    source label)."""
    sdir = stage_dir(run_dir, stage)
    for name in (campaign.IN_DIRNAME, campaign.OUT_DIRNAME):
        shutil.rmtree(os.path.join(sdir, name), ignore_errors=True)
    for name in (campaign.STAGE_RECORD_FILE, campaign.HANDOVER_FILE):
        try:
            os.remove(os.path.join(sdir, name))
        except OSError:
            pass
    os.makedirs(sdir, exist_ok=True)
    source, prev_id = campaign.stage_source(loaded.ladder, stage, records)
    if source != campaign.HANDOVER_CHAIN:
        log.info("Handover", "stage=%s stages its committed fixture %s (%s)"
                 % (stage.spec_id, stage.template_leaf, stage.reason))
        return [], campaign.HANDOVER_FIXTURE
    prev = loaded.ladder.stage(prev_id)
    rec = records.get(prev_id)
    if prev is None or rec is None or not rec.green or not rec.out_leaf:
        raise HandoverError("predecessor %s has no green harvested save" % prev_id)
    prev_out = os.path.join(stage_dir(run_dir, prev), campaign.OUT_DIRNAME, rec.out_leaf)
    if not os.path.isfile(os.path.join(prev_out, "persistent.sfs")):
        raise HandoverError("predecessor %s's harvested save is missing: %s"
                            % (prev_id, prev_out))
    in_dir = os.path.join(sdir, campaign.IN_DIRNAME)
    shutil.copytree(prev_out, in_dir)
    vab = os.path.join(in_dir, "Ships", "VAB")
    for craft in stage.carry_craft:
        src = os.path.join(ctx.harness_root, "fixtures", "saves", stage.template_leaf,
                           "Ships", "VAB", craft + ".craft")
        if not os.path.isfile(src):
            raise HandoverError("carryCraft %r is not in fixtures/saves/%s/Ships/VAB"
                                % (craft, stage.template_leaf))
        os.makedirs(vab, exist_ok=True)
        shutil.copy2(src, os.path.join(vab, craft + ".craft"))
    pr.write_json_atomic(os.path.join(sdir, campaign.HANDOVER_FILE),
                         campaign.handover_marker(loaded.plan.get("name"), stage, prev_id,
                                                  rec.run_ids, token))
    log.info("Handover", "stage=%s stages %s's save %s (runIds=%s)%s"
             % (stage.spec_id, prev_id, prev_out, ",".join(rec.run_ids) or "-",
                (" + carryCraft %s" % ", ".join(stage.carry_craft))
                if stage.carry_craft else ""))
    return ["--stage-from", in_dir], prev_id


def harvest_into(ctx: pr.Context, src: str, target_root: str, leaf: str,
                 flags: campaign.HarvestFlags, log: pr.RunnerLog) -> None:
    """``harvest_bdock_station.harvest`` with its output in the runner log.
    Raises HandoverError when a gate refuses."""
    mod = harvest_module(ctx)
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf):
            mod.harvest(src, leaf, leaf, False,
                        mod.parse_expected_situations(flags.expect_situation or None),
                        keep_parsek=flags.keep_parsek, allow_no_vessel=flags.allow_no_vessel,
                        expected_mode=flags.expect_mode, target_root=target_root)
    except SystemExit as exc:
        raise HandoverError(str(exc.code if exc.code is not None else exc))
    finally:
        for line in buf.getvalue().splitlines():
            log.raw(line)


def settle_stage(ctx: pr.Context, run_dir: str, loaded: Loaded, stage: campaign.Stage,
                 outcome: parallel.LaneOutcome, source: str,
                 log: pr.RunnerLog) -> campaign.StageRecord:
    """The stage's record: its verdict, and for a green stage the harvest of its
    own snapshot into ``out/``."""
    base = campaign.StageRecord(stage.spec_id, outcome.verdict, outcome.attempts,
                                tuple(outcome.run_ids), "", "", source, "")
    if outcome.verdict not in campaign.GREEN_VERDICTS:
        log.warn("Handover", "stage=%s %s: the chain stops here" % (stage.spec_id,
                                                                   outcome.verdict))
        return base
    try:
        if not outcome.run_ids:
            raise HandoverError("the child reported no run id")
        run_id = outcome.run_ids[-1]
        result = pr.read_json(os.path.join(ctx.results_dir, "%s.json" % run_id))
        snap = (result or {}).get("snapshot") if isinstance(result, dict) else None
        if not isinstance(snap, dict) or not snap.get("ran") or not snap.get("path"):
            raise HandoverError("run %s has no produced-save snapshot (reason=%s); the "
                                "instance save is never used, a sibling run can overwrite it"
                                % (run_id, (snap or {}).get("reason") if isinstance(snap, dict)
                                   else "no result"))
        snap_name = os.path.basename(str(snap.get("path")))
        src = os.path.join(ctx.results_dir, snap_name)
        if not snap_name.endswith(hlib.SAVE_SNAPSHOT_DIR_SUFFIX) or not os.path.isdir(src):
            raise HandoverError("snapshot %s is not a save folder under results/" % snap_name)
        leaf = campaign.harvest_leaf(loaded.full, stage)
        out_root = os.path.join(stage_dir(run_dir, stage), campaign.OUT_DIRNAME)
        shutil.rmtree(out_root, ignore_errors=True)
        os.makedirs(out_root)
        harvest_into(ctx, src, out_root, leaf, stage.harvest, log)
    except HandoverError as exc:
        log.error("Handover", "stage=%s %s but its handover failed: %s; the chain stops here"
                  % (stage.spec_id, outcome.verdict, exc))
        return campaign.StageRecord(stage.spec_id, outcome.verdict, outcome.attempts,
                                    tuple(outcome.run_ids), campaign.HANDOVER_FAILED,
                                    str(exc), source, "")
    log.info("Handover", "stage=%s %s: harvested %s -> %s"
             % (stage.spec_id, outcome.verdict, snap_name, os.path.join(out_root, leaf)))
    return campaign.StageRecord(stage.spec_id, outcome.verdict, outcome.attempts,
                                tuple(outcome.run_ids), campaign.HANDOVER_OK, "", source, leaf)


# ---------------------------------------------------------------------------
# The dispatch loop
# ---------------------------------------------------------------------------


def stage_lane(ctx: pr.Context, loaded: Loaded, stage: campaign.Stage,
               durations: Dict, medians: Dict) -> parallel.Lane:
    spec = loaded.specs_by_id.get(stage.spec_id, {})
    p50, src = parallel.lane_p50(stage.spec_id, durations, medians)
    return parallel.Lane(stage.spec_id, stage.profile, str(spec.get("tier")), p50, src,
                         parallel.exclusive_reason(spec))


def campaign_loop(ctx: pr.Context, folder: pr.RunFolder, loaded: Loaded,
                  by_profile: Dict[str, List[str]], resolution: parallel.WindowResolution,
                  keeper: pr.LockKeeper, job: pr.WindowsJob, log: pr.RunnerLog
                  ) -> pr.LoopResult:
    running: Dict[int, Tuple[pr.Child, str]] = {}
    try:
        return _campaign_dispatch(ctx, folder, loaded, by_profile, resolution, keeper, job,
                                  log, running)
    except BaseException:
        pr._kill_running({s: c for s, (c, _src) in running.items()}, log)
        for child, _src in running.values():
            try:
                child.proc.wait(timeout=pr.CHILD_KILL_WAIT_SECONDS)
            except Exception:  # noqa: BLE001
                pass
            folder.event("abandon", seq=child.seq, lane=child.lane.lane_id, slot=child.slot)
        raise


def _campaign_dispatch(ctx: pr.Context, folder: pr.RunFolder, loaded: Loaded,
                       by_profile: Dict[str, List[str]], resolution: parallel.WindowResolution,
                       keeper: pr.LockKeeper, job: pr.WindowsJob, log: pr.RunnerLog,
                       running: Dict[int, Tuple[pr.Child, str]]) -> pr.LoopResult:
    run_dir = folder.path
    ladder = loaded.ladder
    plan_ids = [s.spec_id for s in ladder.stages()]
    records = load_records(run_dir, ladder)
    failed: set = set()
    lane_lines = [l for l in folder.summary_lines() if parallel.parse_summary_line(l)]
    durations, medians = pr.load_durations(ctx), pr.load_index_medians(ctx)
    fallback_slots = int(loaded.plan.get("slotsAtStart") or parallel.DEFAULT_SLOTS)
    seq = max([int(e.get("seq") or 0) for e in folder.events()] + [0])
    result = pr.LoopResult()
    last_start: Optional[float] = None
    last_wait: Optional[Tuple[str, str]] = None
    mem_cache: Tuple[float, Optional[parallel.MemoryReading]] = (0.0, None)
    green = [s for s in plan_ids if records.get(s) and records[s].green]
    log.info("Plan", "%d stage(s) in range, %d already green%s"
             % (len(plan_ids), len(green), (": " + ", ".join(green)) if green else ""))
    while True:
        for s, (child, source) in list(running.items()):
            if child.proc.poll() is None:
                continue
            del running[s]
            outcome, child_stopped = pr._finish_child(child, folder, lane_lines, log)
            if child_stopped:
                result.stopped = True
            if outcome is None:
                continue
            result.ran += 1
            result.run_ids.extend(outcome.run_ids)
            stage = ladder.stage(child.lane.lane_id)
            rec = settle_stage(ctx, run_dir, loaded, stage, outcome, source, log)
            write_record(run_dir, stage, rec)
            records[stage.spec_id] = rec
            if not rec.green:
                failed.add(stage.spec_id)
            folder.event("settle", lane=stage.spec_id, verdict=rec.verdict,
                         handover=rec.handover, reason=rec.reason, outLeaf=rec.out_leaf)
            folder.write_summary(lane_lines, plan_ids, [])
            write_verdict(run_dir, loaded, records, False)

        slots_value, stop = folder.read_control(fallback_slots)
        if pr._consume_stop_request(ctx):
            log.warn("Stop", "%s (results/%s): running stages finish, nothing new starts"
                     % (hlib.STOP_HONOURED_MARKER, hlib.STOP_REQUEST_FILENAME))
            folder.write_control(slots_value, True)
            folder.event("stop", source="stop-request")
            stop = True
        if stop and not result.stopped:
            log.warn("Stop", "stop requested; %d running stage(s) finish" % len(running))
            result.stopped = True
        if keeper.lost.is_set() and not result.lock_lost:
            result.lock_lost = True
        halt = result.stopped or result.lock_lost

        ready = [] if halt else campaign.ready_stages(
            ladder, records, [c.lane.lane_id for c, _s in running.values()], failed)
        pending = [stage_lane(ctx, loaded, st, durations, medians) for st in ready]
        memory = None
        if running and pending and not halt:
            now = time.time()
            if now - mem_cache[0] >= pr.MEMORY_CACHE_SECONDS or mem_cache[1] is None:
                mem_cache = (now, ctx.memory_fn())
            memory = mem_cache[1]
        running_view = [parallel.Running(c.lane.lane_id, c.slot, c.lane.exclusive, c.started)
                        for c, _s in running.values()]
        decision = parallel.decide_next(pending, running_view, by_profile, slots_value, halt,
                                        memory, time.time(), last_start,
                                        ctx.dispatch_gap_seconds, ctx.boot_window_seconds)
        if decision.action == parallel.ACTION_DONE:
            break
        if decision.action == parallel.ACTION_START:
            lane = next(l for l in pending if l.lane_id == decision.lane_id)
            stage = ladder.stage(lane.lane_id)
            try:
                extra, source = prepare_stage(ctx, run_dir, loaded, stage, records,
                                              keeper.token, log)
            except (HandoverError, OSError) as exc:
                log.error("Handover", "stage=%s cannot be staged: %s; the chain stops here"
                          % (stage.spec_id, exc))
                rec = campaign.StageRecord(stage.spec_id, parallel.VERDICT_NOT_RUN, 0, (),
                                           campaign.HANDOVER_FAILED, "staging: %s" % exc,
                                           "", "")
                write_record(run_dir, stage, rec)
                records[stage.spec_id] = rec
                failed.add(stage.spec_id)
                write_verdict(run_dir, loaded, records, False)
                continue
            seq += 1
            window = decision.window or resolution.mode
            desktop = resolution.desktop_id if window == parallel.WINDOW_DESKTOP else None
            child = pr._spawn_child(ctx, folder, job, seq, lane, decision.slot, window, desktop,
                                    keeper.token, log, extra_argv=extra)
            running[seq] = (child, source)
            last_start = child.started
            last_wait = None
            mem_cache = (0.0, None)
            folder.event("dispatch", seq=seq, lane=lane.lane_id, slot=decision.slot,
                         window=window, pid=child.proc.pid, reason=decision.reason,
                         source=source)
            continue
        key = (decision.wait_kind or "", decision.log_key or
               (decision.reason if decision.wait_kind == parallel.WAIT_STOP else ""))
        if key != last_wait:
            last_wait = key
            log.info("Wait", "%s: %s" % (decision.wait_kind, decision.reason))
            folder.event("wait", waitKind=decision.wait_kind, reason=decision.reason)
        time.sleep(ctx.poll_seconds)
    folder.write_summary(lane_lines, plan_ids, [])
    return result


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------


def _refuse(log: pr.RunnerLog, code: int, step: str, message: str) -> int:
    log.error(step, message)
    return code


def format_dry_run(loaded: Loaded, p50: Dict[str, Tuple[float, str]], slots_value: int
                   ) -> List[str]:
    out = ["campaign ladder=%s stages=%d slots=%d" % (loaded.full.name,
                                                       len(loaded.ladder.stages()), slots_value)]
    for st in loaded.ladder.stages():
        source, prev = campaign.stage_source(loaded.ladder, st, {})
        out.append("stage %s %s (%s) p50=%.0fs (%s) stages %s%s" % (
            st.chain, st.spec_id, st.ref, p50[st.spec_id][0], p50[st.spec_id][1],
            ("%s's save" % prev) if source == campaign.HANDOVER_CHAIN
            else "fixtures/saves/%s (%s)" % (st.template_leaf, st.reason),
            (" + carryCraft %s" % ", ".join(st.carry_craft)) if st.carry_craft
            and source == campaign.HANDOVER_CHAIN else ""))
    sched = campaign.simulate_schedule(loaded.ladder, lambda s: p50[s][0], slots_value)
    for item in sched:
        out.append("schedule %s slot=%d start=%.0fs end=%.0fs"
                   % (item.spec_id, item.slot + 1, item.start, item.end))
    if sched:
        out.append("schedule end %.0fs = %.2f h (p50s, no dispatch gap)"
                   % (max(i.end for i in sched), max(i.end for i in sched) / 3600.0))
    return out


def cmd_run(ctx: pr.Context, args, log: pr.RunnerLog) -> int:
    if not parallel.valid_run_name(args.name):
        return _refuse(log, parallel.EXIT_USAGE, "Select", "bad run name %r" % args.name)
    run_dir = campaign_dir(ctx, args.name)
    if read_plan(run_dir) is not None and not args.dry_run:
        return _refuse(log, parallel.EXIT_USAGE, "Select",
                       "campaign %s already has a plan; use `resume --name %s` or another name"
                       % (args.name, args.name))
    n_slots = parallel.clamp_slots(args.slots)
    if n_slots is None:
        return _refuse(log, parallel.EXIT_USAGE, "Select", "--slots must be %d..%d"
                       % (parallel.MIN_SLOTS, parallel.MAX_SLOTS))
    try:
        table = load_ladder_table(ctx, args.ladder)
    except (OSError, ValueError) as exc:
        return _refuse(log, parallel.EXIT_USAGE, "Ladder", "cannot read ladder %s: %s"
                       % (ladder_path(ctx, args.ladder), exc))
    plan = {"schema": campaign.PLAN_SCHEMA, "name": args.name, "ladder": args.ladder,
            "ladderTable": table, "from": args.from_ref, "to": args.to_ref,
            "createdUtc": pr.utcnow_iso(), "gitHead": pr.git_head(ctx),
            "windowMode": args.window, "slotsAtStart": n_slots}
    try:
        loaded = build_loaded(ctx, plan)
    except campaign.LadderError as exc:
        return _refuse(log, parallel.EXIT_USAGE, "Ladder", "refused: %s" % exc)
    problems = check_ladder(ctx, loaded.ladder, loaded.specs_by_id)
    if problems:
        return _refuse(log, parallel.EXIT_USAGE, "Ladder", "refused: %s" % "; ".join(problems))
    durations, medians = pr.load_durations(ctx), pr.load_index_medians(ctx)
    p50 = {s.spec_id: parallel.lane_p50(s.spec_id, durations, medians)
           for s in loaded.ladder.stages()}
    for line in format_dry_run(loaded, p50, n_slots):
        log.info("Plan", line)
    if args.dry_run:
        log.info("Done", "dry-run: nothing written, nothing flown")
        return parallel.EXIT_GREEN
    os.makedirs(run_dir, exist_ok=True)
    log.attach(os.path.join(run_dir, parallel.RUNNER_LOG_FILE))
    pr.write_json_atomic(os.path.join(run_dir, campaign.PLAN_FILE), plan)
    folder = pr.RunFolder(run_dir)
    folder.write_control(n_slots, False)
    return execute(ctx, folder, loaded, log)


def cmd_resume(ctx: pr.Context, args, log: pr.RunnerLog) -> int:
    run_dir = campaign_dir(ctx, args.name)
    plan = read_plan(run_dir) if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Resume", "no campaign %r under %s"
                       % (args.name, run_dir))
    log.attach(os.path.join(run_dir, parallel.RUNNER_LOG_FILE))
    try:
        loaded = build_loaded(ctx, plan)
    except campaign.LadderError as exc:
        return _refuse(log, parallel.EXIT_USAGE, "Ladder", "refused on resume: %s" % exc)
    folder = pr.RunFolder(run_dir)
    slots_value, stop = folder.read_control(int(plan.get("slotsAtStart") or 2))
    if stop:
        log.info("Resume", "clearing the stop flag left by the previous invocation")
    folder.write_control(slots_value, False)
    if plan.get("gitHead") != pr.git_head(ctx):
        log.warn("Resume", "this worktree is at %s, the campaign was made at %s"
                 % (pr.git_head(ctx), plan.get("gitHead")))
    return execute(ctx, folder, loaded, log)


def execute(ctx: pr.Context, folder: pr.RunFolder, loaded: Loaded, log: pr.RunnerLog) -> int:
    records = load_records(folder.path, loaded.ladder)
    todo = [s for s in loaded.ladder.stages()
            if not (records.get(s.spec_id) and records[s.spec_id].green)]
    if not todo:
        outcome, code, detail = write_verdict(folder.path, loaded, records, False)
        log.info("Done", "every stage is green; outcome=%s exit=%d %s" % (outcome, code, detail))
        return code
    by_profile = parallel.slots_by_profile(pr.discover_slot_names(ctx))
    missing = sorted({s.profile for s in todo} - set(by_profile))
    if missing:
        return _refuse(log, parallel.EXIT_NEEDS_PROVISION, "Slots",
                       "no provisioned slot for profile(s) %s; provision them "
                       "(`python provision/provision.py --profile <p>`)" % ", ".join(missing))
    used = [s for p in sorted({st.profile for st in todo}) for s in by_profile.get(p, [])]
    log.info("Slots", "slots in use: %s" % ", ".join(used))

    def loop(keeper, job, resolution):
        return campaign_loop(ctx, folder, loaded, by_profile, resolution, keeper, job, log)

    refused, result = pr.fly_under_machine_lock(
        ctx, folder, str(loaded.plan.get("name")), str(loaded.plan.get("windowMode")), used,
        len(todo), log, loop, SELECTION_LABEL)
    if refused is not None:
        return refused
    records = load_records(folder.path, loaded.ladder)
    outcome, code, detail = write_verdict(folder.path, loaded, records, result.stopped)
    if result.lock_lost and code == parallel.EXIT_GREEN:
        outcome, code = campaign.OUTCOME_RED, campaign.EXIT_RED
    folder.event("end", outcome=outcome, exit=code, detail=detail)
    (log.info if code == campaign.EXIT_GREEN else log.warn)(
        "Done", "campaign %s outcome=%s exit=%d %s; verdict %s"
        % (loaded.plan.get("name"), outcome, code, detail,
           os.path.join(folder.path, campaign.VERDICT_FILE)))
    return code


def cmd_stop(ctx: pr.Context, args, log: pr.RunnerLog) -> int:
    run_dir = campaign_dir(ctx, args.name)
    plan = read_plan(run_dir) if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Control", "no campaign %r" % args.name)
    folder = pr.RunFolder(run_dir)
    n, _stop = folder.read_control(int(plan.get("slotsAtStart") or 2))
    folder.write_control(n, True)
    log.info("Control", "campaign %s stop requested: running stages finish, nothing new "
                        "starts; `resume --name %s` continues" % (args.name, args.name))
    return 0


def cmd_status(ctx: pr.Context, args, log: pr.RunnerLog) -> int:
    run_dir = campaign_dir(ctx, args.name)
    plan = read_plan(run_dir) if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Status", "no campaign %r" % args.name)
    try:
        loaded = build_loaded(ctx, plan)
    except campaign.LadderError as exc:
        return _refuse(log, parallel.EXIT_USAGE, "Status", "ladder refused: %s" % exc)
    folder = pr.RunFolder(run_dir)
    events = folder.events()
    log.raw("campaign %s ladder=%s: %s" % (args.name, loaded.full.name,
                                           pr.runner_state(ctx, args.name, events)))
    records = load_records(run_dir, loaded.ladder)
    for st in loaded.ladder.stages():
        rec = records.get(st.spec_id)
        log.raw("stage %s %s %s" % (st.chain, st.spec_id,
                                    campaign.describe_record(rec) if rec else "not flown"))
    for o in campaign.chain_outcomes(loaded.ladder, records):
        log.raw(o.line())
    return 0


def cmd_promote(ctx: pr.Context, args, log: pr.RunnerLog) -> int:
    """Replace the successor's committed fixture with a green stage's harvested
    save (the stage-to-fixture promotion the campaign program did by hand). Craft
    the committed fixture held that the stage's save lacks (a builder's craft for
    the successor, a craft another lane flies) are kept."""
    run_dir = campaign_dir(ctx, args.name)
    plan = read_plan(run_dir) if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Promote", "no campaign %r" % args.name)
    try:
        loaded = build_loaded(ctx, plan)
    except campaign.LadderError as exc:
        return _refuse(log, parallel.EXIT_USAGE, "Promote", "ladder refused: %s" % exc)
    stage = campaign.resolve_ref(loaded.full, args.stage)
    if stage is None:
        return _refuse(log, parallel.EXIT_USAGE, "Promote", "no stage %r" % args.stage)
    rec = load_records(run_dir, loaded.full).get(stage.spec_id)
    target, why = campaign.promotion_target(loaded.full, stage, rec)
    if target is None:
        return _refuse(log, parallel.EXIT_USAGE, "Promote", "refused: %s" % why)
    src = os.path.join(stage_dir(run_dir, stage), campaign.OUT_DIRNAME, rec.out_leaf)
    fixture_vab = os.path.join(ctx.harness_root, "fixtures", "saves", target, "Ships", "VAB")
    kept: Dict[str, bytes] = {}
    if os.path.isdir(fixture_vab):
        for name in os.listdir(fixture_vab):
            if name.endswith(".craft"):
                with open(os.path.join(fixture_vab, name), "rb") as fh:
                    kept[name] = fh.read()
    try:
        harvest_into(ctx, src, os.path.join(ctx.harness_root, "fixtures", "saves"), target,
                     stage.harvest, log)
    except HandoverError as exc:
        return _refuse(log, parallel.EXIT_RED, "Promote", "harvest refused: %s" % exc)
    restored = []
    for name, data in sorted(kept.items()):
        dst = os.path.join(fixture_vab, name)
        if not os.path.exists(dst):
            os.makedirs(fixture_vab, exist_ok=True)
            with open(dst, "wb") as fh:
                fh.write(data)
            restored.append(name)
    log.info("Promote", "stage %s (runIds=%s) -> fixtures/saves/%s; kept %d committed "
                        "craft the stage's save lacks%s. Commit the fixture and re-run the "
                        "harness suites (fixture gates, the specs' pinned values)"
             % (stage.spec_id, ",".join(rec.run_ids) or "-", target, len(restored),
                (": " + ", ".join(restored)) if restored else ""))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="campaign_runner",
        description="Fly a campaign ladder (harness/campaigns/<ladder>.toml): each chained "
                    "stage stages the previous stage's harvested save.")
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("run", help="plan and fly a ladder (or a range of one chain)")
    p.add_argument("--ladder", required=True, help="harness/campaigns/<ladder>.toml")
    p.add_argument("--name", required=True, help="run name: results/campaign/<name>/")
    p.add_argument("--from", dest="from_ref", help="first stage (spec id or label)")
    p.add_argument("--to", dest="to_ref", help="last stage (spec id or label)")
    p.add_argument("--slots", default=str(parallel.DEFAULT_SLOTS),
                   help="stages at once (two chains pipeline on two)")
    p.add_argument("--window", choices=parallel.WINDOW_MODES, default=parallel.WINDOW_DESKTOP)
    p.add_argument("--dry-run", action="store_true",
                   help="print each stage's source and the p50 schedule; write nothing")
    p = sub.add_parser("resume", help="fly again from the stage that broke or was not flown")
    p.add_argument("--name", required=True)
    p = sub.add_parser("stop", help="running stages finish, nothing new starts")
    p.add_argument("--name", required=True)
    p = sub.add_parser("status", help="each stage's record and each chain's verdict")
    p.add_argument("--name", required=True)
    p = sub.add_parser("promote", help="make a green stage's save its successor's committed "
                                       "fixture")
    p.add_argument("--name", required=True)
    p.add_argument("--stage", required=True, help="spec id or label")
    return parser


COMMANDS = {"run": cmd_run, "resume": cmd_resume, "stop": cmd_stop, "status": cmd_status,
            "promote": cmd_promote}


def main(argv: Optional[Sequence[str]] = None, ctx: Optional[pr.Context] = None) -> int:
    args = build_parser().parse_args(argv)
    ctx = ctx or pr.Context()
    log = pr.RunnerLog(ctx.console, "Campaign")
    try:
        return COMMANDS[args.command](ctx, args, log)
    except KeyboardInterrupt:
        log.error("Abort", "interrupted; children die with the runner's job object")
        return parallel.EXIT_ERROR
    except Exception:  # noqa: BLE001
        log.error("Error", "campaign runner internal error: %s"
                  % traceback.format_exc(limit=5).replace("\n", " | "))
        return parallel.EXIT_ERROR
    finally:
        log.close()


if __name__ == "__main__":
    sys.exit(main())
