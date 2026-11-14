"""A fake ``run.py --slot`` child for test_parallel's shell smoke.

Reads the flags the parallel runner passes, checks the runner token against the
machine lock, records what it saw under ``$FAKE_CHILD_STATE`` and writes the
``--report-json`` file run.py writes. ``$FAKE_CHILD_VERDICTS`` maps a lane to its
verdict (default PASS); ``-`` writes no report and exits 3; ``STOP`` prints
run.py's stop-honoured line and reports the lane NOT-RUN.
``$FAKE_CHILD_STOP_AFTER`` names a lane that, once it finished, drops the
runner's stop-request file (``$FAKE_CHILD_STOP_REQUEST``), as an operator's
``--stop`` mid-run would.
"""

import argparse
import json
import os
import sys
import time


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
    args = p.parse_args()
    with open(args.ids_file, encoding="utf-8") as fh:
        lane = [l.strip() for l in fh if l.strip() and not l.startswith("#")][0]
    state = os.environ["FAKE_CHILD_STATE"]
    lock = os.path.join(os.environ["FAKE_CHILD_UMBRELLA"], "automation", ".ksp-machine.lock")
    try:
        with open(lock, encoding="utf-8") as fh:
            holder = json.load(fh)
    except (OSError, ValueError):
        holder = {}
    marker = os.path.join(state, "running-%s-%d" % (lane, os.getpid()))
    open(marker, "w").close()
    start = time.time()
    concurrent = len([f for f in os.listdir(state) if f.startswith("running-")])
    time.sleep(float(os.environ.get("FAKE_CHILD_SLEEP", "0.2")))
    concurrent = max(concurrent, len([f for f in os.listdir(state) if f.startswith("running-")]))
    os.remove(marker)
    record = {"lane": lane, "slot": args.slot, "argv": sys.argv[1:], "start": start,
              "concurrent": concurrent,
              "tokenMatches": holder.get("token") == os.environ.get("PARSEK_HARNESS_RUNNER_TOKEN")
              and holder.get("kind") == "parallel-runner",
              "runnerPid": int(os.environ.get("PARSEK_HARNESS_RUNNER_PID", "0"))}
    with open(os.path.join(state, "argv-%s-%d.json" % (lane, os.getpid())), "w") as fh:
        json.dump(record, fh)
    if os.environ.get("FAKE_CHILD_STOP_AFTER") == lane:
        with open(os.environ["FAKE_CHILD_STOP_REQUEST"], "w", encoding="utf-8") as fh:
            fh.write("stop\n")
    verdict = json.loads(os.environ.get("FAKE_CHILD_VERDICTS") or "{}").get(lane, "PASS")
    if verdict == "-":
        return 3
    entry = {"id": lane, "verdict": verdict, "attempts": 1, "runIds": ["run-" + lane],
             "wallSeconds": 1}
    if verdict == "STOP":
        print("[Harness][Warn][Select] stop request honoured ran=0 dropped=1")
        entry = {"id": lane, "verdict": "NOT-RUN", "attempts": 0, "runIds": []}
    with open(args.report_json, "w", encoding="utf-8") as fh:
        json.dump({"schema": "parsek-run-report/1", "slot": args.slot, "lanes": [entry]}, fh)
    return 0 if verdict in ("PASS", "EXPECTED-FAIL") else 1


if __name__ == "__main__":
    sys.exit(main())
