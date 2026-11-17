"""A fake ``run.py --slot`` child for test_campaign's shell smoke.

What a campaign stage's real child does, with no KSP: it applies run.py's own
``--stage-from`` refusal (``run._stage_from_refusal``) and run.py's own
``stage_fixture`` into ``automation/<slot>/saves/<leaf>``, then "flies" by
copying the staged save to the run's produced-save snapshot
``results/<runId>_save`` with a line naming the lane and a recording sidecar,
and writes ``results/<runId>.json`` (its ``snapshot`` block) and the
``--report-json`` file. What it saw goes to ``$FAKE_CHILD_STATE/stage-<lane>-<pid>.json``.

Environment: ``FAKE_CHILD_STATE``, ``FAKE_CHILD_UMBRELLA``, ``FAKE_CHILD_HARNESS``
(the temp harness root: its scenarios, fixtures and results), ``FAKE_CHILD_SLEEP``,
``FAKE_CHILD_VERDICTS`` (lane -> verdict; ``NOSNAP`` reports PASS with no
snapshot), ``FAKE_CHILD_CLOBBER=1`` (before staging, overwrite every other run's
snapshot and every instance save, as a sibling run re-staging a shared template
would).
"""

import argparse
import json
import os
import shutil
import sys
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(HERE)
for _p in (HERE, HARNESS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import run  # noqa: E402
import slotrun  # noqa: E402
import tomllib  # noqa: E402

CLOBBERED = "CLOBBERED BY A SIBLING RUN\n"


def _spec(harness: str, lane: str) -> dict:
    with open(os.path.join(harness, "scenarios", lane + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _clobber(results: str, instance_root: str) -> None:
    for name in os.listdir(results):
        sfs = os.path.join(results, name, "persistent.sfs")
        if name.endswith("_save") and os.path.isfile(sfs):
            with open(sfs, "w", encoding="utf-8") as fh:
                fh.write(CLOBBERED)
    for slot in os.listdir(instance_root):
        saves = os.path.join(instance_root, slot, "saves")
        if os.path.isdir(saves):
            for leaf in os.listdir(saves):
                sfs = os.path.join(saves, leaf, "persistent.sfs")
                if os.path.isfile(sfs):
                    with open(sfs, "w", encoding="utf-8") as fh:
                        fh.write(CLOBBERED)


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--slot", required=True)
    p.add_argument("--ids-file", required=True)
    p.add_argument("--tests-prebuilt", action="store_true")
    p.add_argument("--no-coverage", action="store_true")
    p.add_argument("--keep-stop-request", action="store_true")
    p.add_argument("--report-json", required=True)
    p.add_argument("--window", required=True)
    p.add_argument("--desktop-id")
    p.add_argument("--stage-from")
    args = p.parse_args()
    with open(args.ids_file, encoding="utf-8") as fh:
        lane = [l.strip() for l in fh if l.strip() and not l.startswith("#")][0]
    state = os.environ["FAKE_CHILD_STATE"]
    umbrella = os.environ["FAKE_CHILD_UMBRELLA"]
    harness = os.environ["FAKE_CHILD_HARNESS"]
    results = os.path.join(harness, "results")
    automation = os.path.join(umbrella, "automation")
    marker = os.path.join(state, "running-%s-%d" % (lane, os.getpid()))
    open(marker, "w").close()
    concurrent = len([f for f in os.listdir(state) if f.startswith("running-")])

    try:
        with open(os.path.join(automation, ".ksp-machine.lock"), encoding="utf-8") as fh:
            holder = json.load(fh)
    except (OSError, ValueError):
        holder = {}
    record = {"lane": lane, "slot": args.slot, "argv": sys.argv[1:],
              "tokenMatches": holder.get("token") == os.environ.get(slotrun.RUNNER_TOKEN_ENV)
              and holder.get("kind") == "parallel-runner",
              "lockSelection": holder.get("selection"), "refusal": None,
              "staged": None, "stagedVab": [], "stageOk": None}

    if os.environ.get("FAKE_CHILD_CLOBBER") == "1":
        _clobber(results, automation)

    run.RESULTS_DIR = results
    run.HARNESS_ROOT = harness
    run._CTX = run.SelectionContext(slot=args.slot)
    verdict = json.loads(os.environ.get("FAKE_CHILD_VERDICTS") or "{}").get(lane, "PASS")
    if args.stage_from:
        refusal, real = run._stage_from_refusal(args.stage_from, [lane])
        record["refusal"] = refusal
        run._CTX.stage_from = None if refusal else real
        if refusal:
            verdict = "INVALID"
    run_id = "run-%s-%s" % (lane, uuid.uuid4().hex[:8])
    instance = os.path.join(automation, args.slot)
    leaf = ""
    if verdict != "INVALID":
        logger = run.HarnessLogger(os.path.join(state, "harness-%s-%d.log" % (lane, os.getpid())))
        try:
            ok, leaf, subkind = run.stage_fixture(_spec(harness, lane), instance, run.Runtime(),
                                                  logger)
        finally:
            logger.close()
        record["stageOk"] = ok
        if not ok:
            verdict = "INVALID"
        else:
            staged = os.path.join(instance, "saves", leaf)
            with open(os.path.join(staged, "persistent.sfs"), encoding="utf-8") as fh:
                record["staged"] = fh.read()
            vab = os.path.join(staged, "Ships", "VAB")
            record["stagedVab"] = sorted(os.listdir(vab)) if os.path.isdir(vab) else []

    time.sleep(float(os.environ.get("FAKE_CHILD_SLEEP", "0.2")))
    concurrent = max(concurrent, len([f for f in os.listdir(state) if f.startswith("running-")]))
    record["concurrent"] = concurrent
    os.remove(marker)

    snapshot = {"ran": False, "path": None, "reason": "no produced save"}
    if verdict not in ("INVALID",) and leaf:
        staged = os.path.join(instance, "saves", leaf)
        rec_id = uuid.uuid4().hex
        with open(os.path.join(staged, "persistent.sfs"), "a", encoding="utf-8") as fh:
            fh.write("STAGE = %s\nrecordingId = %s\n" % (lane, rec_id))
        rdir = os.path.join(staged, "Parsek", "Recordings")
        os.makedirs(rdir, exist_ok=True)
        with open(os.path.join(rdir, rec_id + ".prec"), "wb") as fh:
            fh.write(b"PSK0")
        if verdict != "NOSNAP":
            snap = os.path.join(results, run_id + "_save")
            shutil.copytree(staged, snap)
            snapshot = {"ran": True, "path": os.path.basename(snap), "reason": "copied"}
        else:
            snapshot = {"ran": False, "path": None, "reason": "snapshot-failed"}
            verdict = "PASS"
    with open(os.path.join(results, run_id + ".json"), "w", encoding="utf-8") as fh:
        json.dump({"runId": run_id, "scenarioId": lane, "verdict": verdict,
                   "snapshot": snapshot}, fh)
    with open(os.path.join(state, "stage-%s-%d.json" % (lane, os.getpid())), "w") as fh:
        json.dump(record, fh)
    entry = {"id": lane, "verdict": verdict, "attempts": 1, "runIds": [run_id],
             "wallSeconds": 1}
    with open(args.report_json, "w", encoding="utf-8") as fh:
        json.dump({"schema": "parsek-run-report/1", "slot": args.slot, "lanes": [entry]}, fh)
    return 0 if verdict in ("PASS", "EXPECTED-FAIL") else 1


if __name__ == "__main__":
    sys.exit(main())
