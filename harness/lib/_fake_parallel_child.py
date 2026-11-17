"""A fake ``run.py --slot`` child for test_parallel's shell smoke.

Reads the flags the parallel runner passes, checks the runner token against the
machine lock, records what it saw under ``$FAKE_CHILD_STATE`` and writes the
``--report-json`` file run.py writes. ``$FAKE_CHILD_VERDICTS`` maps a lane to its
verdict (default PASS); ``-`` writes no report and exits 3; ``STOP`` prints
run.py's stop-honoured line and reports the lane NOT-RUN.
``$FAKE_CHILD_SLEEPS`` (``{lane: seconds}``) overrides ``$FAKE_CHILD_SLEEP`` (the
time a child lingers before it reports) per lane. ``$FAKE_CHILD_STOP_AFTER`` names a lane that, once it finished, drops the
runner's stop-request file (``$FAKE_CHILD_STOP_REQUEST``), as an operator's
``--stop`` mid-run would, then writes ``stop-dropped`` under ``$FAKE_CHILD_STATE``.
``$FAKE_CHILD_AWAIT`` (``{lane: file name under $FAKE_CHILD_STATE}``) holds a
lane's exit until that file exists (at most ``AWAIT_LIMIT_SECONDS``), so a test
orders two children's ends by an event, not by sleeps.

A paired half (``--pair-dir`` / ``--pair-shared``, multiplayer T-54) reads its
script from ``$FAKE_CHILD_PAIRS`` (``{lane: {"role", "barriers", "timeout"}}``):
it lists the shared folder as it found it, role A writes ``A.token`` there, and
each barrier runs through run.py's own ``wait_at_barrier``; after each one it
records whether A's token is in the shared folder. A lost barrier reports
INVALID (run.py's partner-lost) and stops the half there.
"""

import argparse
import json
import os
import sys
import time

AWAIT_LIMIT_SECONDS = 30.0


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
    p.add_argument("--pair-dir")
    p.add_argument("--pair-shared")
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
    verdict = json.loads(os.environ.get("FAKE_CHILD_VERDICTS") or "{}").get(lane, "PASS")
    pair = None
    if args.pair_dir:
        pair = _fly_pair_half(lane, args, verdict)
        if pair.get("lost"):
            verdict = "INVALID"
    sleeps = json.loads(os.environ.get("FAKE_CHILD_SLEEPS") or "{}")
    time.sleep(float(sleeps.get(lane, os.environ.get("FAKE_CHILD_SLEEP", "0.2"))))
    awaited = json.loads(os.environ.get("FAKE_CHILD_AWAIT") or "{}").get(lane)
    if awaited:
        deadline = time.monotonic() + AWAIT_LIMIT_SECONDS
        while not os.path.exists(os.path.join(state, awaited)) and time.monotonic() < deadline:
            time.sleep(0.02)
    concurrent = max(concurrent, len([f for f in os.listdir(state) if f.startswith("running-")]))
    os.remove(marker)
    record = {"lane": lane, "slot": args.slot, "argv": sys.argv[1:], "start": start,
              "concurrent": concurrent,
              "tokenMatches": holder.get("token") == os.environ.get("PARSEK_HARNESS_RUNNER_TOKEN")
              and holder.get("kind") == "parallel-runner",
              "runnerPid": int(os.environ.get("PARSEK_HARNESS_RUNNER_PID", "0")),
              "pair": pair}
    with open(os.path.join(state, "argv-%s-%d.json" % (lane, os.getpid())), "w") as fh:
        json.dump(record, fh)
    if os.environ.get("FAKE_CHILD_STOP_AFTER") == lane:
        with open(os.environ["FAKE_CHILD_STOP_REQUEST"], "w", encoding="utf-8") as fh:
            fh.write("stop\n")
        open(os.path.join(state, "stop-dropped"), "w").close()
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


def _fly_pair_half(lane, args, verdict):
    script = json.loads(os.environ.get("FAKE_CHILD_PAIRS") or "{}").get(lane) or {}
    role = script.get("role", "A")
    out = {"role": role, "pairDir": args.pair_dir, "shared": args.pair_shared,
           "sharedAtStart": sorted(os.listdir(args.pair_shared)), "barriers": [],
           "sawToken": []}
    if verdict == "-":
        return out  # crashes before its first barrier
    harness = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    for path in (harness, os.path.join(harness, "lib"), os.path.join(harness, "provision")):
        if path not in sys.path:
            sys.path.insert(0, path)
    import run  # noqa: E402  (the real barrier loop)
    if role == "A":
        with open(os.path.join(args.pair_shared, "A.token"), "w", encoding="utf-8") as fh:
            fh.write(lane + "\n")
    for name in script.get("barriers", []):
        met, reason, waited = run.wait_at_barrier(args.pair_dir, name, role,
                                                  float(script.get("timeout", 10)),
                                                  poll_seconds=0.05)
        out["barriers"].append({"name": name, "met": met, "reason": reason,
                                "waited": round(waited, 3)})
        out["sawToken"].append(os.path.exists(os.path.join(args.pair_shared, "A.token")))
        if not met:
            out["lost"] = True
            break
    return out


if __name__ == "__main__":
    sys.exit(main())
