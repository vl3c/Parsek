"""Fake mission subprocess for the M-B1 run.py handoff smoke test.

An agent cannot pilot a real KSP (MEMORY: in-game-sweep-needs-operator) and the
real missions link the GPL kRPC client behind the module venv, so run.py's
autopilot HANDOFF -- spawn the mission with the venv python, bounded-wait it,
read + map its result JSON -- is exercised by this stub. It stands in for
``harness/missions/<name>.py``: it reads the CLI contract run.py passes
(``--params/--rpc-host/--rpc-port/--stream-port/--result/--budget``), writes a
scripted design-shaped mission-result JSON to ``--result``, prints a couple of
``[Mission]`` lines to stdout (which run.py folds into the harness log), and
exits with the design's zero/nonzero code. It links NO kRPC and drives no game.

Modes (test-injected by the smoke test's FakeRuntime.spawn_mission):
  ok          write MISSION-OK, exit 0 (the happy handoff)
  assertfail  write MISSION-ASSERT-FAIL, exit 1 (a telemetry assertion unmet ->
              run.py maps INVALID(mission), retry-once)
  noresult    write NOTHING and exit 1 (edge 12: a mission that never wrote a
              readable result -> run.py fails closed to INVALID(tooling-mission))
  midcommit   write a ROUTE-1 mid-mission `cmd=CommitTree` into the seam channel
              under the reserved id, THEN MISSION-ASSERT-FAIL (exit 1). That is
              HARNESS-MIDMISSION-COMMIT-BYPASS's exact shape: a world-mutating
              write landing BEFORE a verdict exists, which the unmet-mission tail
              gate (driver.steps only) never sees.

ASCII only; stdlib only.
"""

from __future__ import annotations

import argparse
import json
import sys


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--params", default="{}")
    parser.add_argument("--rpc-host", default="127.0.0.1")
    parser.add_argument("--rpc-port", type=int, default=50000)
    parser.add_argument("--stream-port", type=int, default=50001)
    parser.add_argument("--result", required=True)
    parser.add_argument("--budget", type=float, default=600.0)
    parser.add_argument("--mode", default="ok",
                        choices=["ok", "assertfail", "noresult", "midcommit"])
    # Route-1 seam bridge, as run.py hands it to a real mission. Only the
    # `midcommit` mode uses them; every other mode ignores them exactly as
    # B1/B2/B4/B5/B7/forge do.
    parser.add_argument("--seam-commands", default="")
    parser.add_argument("--seam-commit-id", default="")
    args = parser.parse_args(argv)

    print("[Mission][Info][Connect] connected in 0.1s attempts=1 (fake, mode=%s)" % args.mode)
    print("[Mission][Info][Verdict] fake mission rpc=%s:%d budget=%ss"
          % (args.rpc_host, args.rpc_port, args.budget))

    if args.mode == "noresult":
        # Crash before the finally that writes the result (edge 12): no file exists.
        print("[Mission][Error][Verdict] fake mission wrote no result (edge 12)")
        return 1

    if args.mode == "midcommit":
        # Byte-identical to mission_runner._perform_seam_commit's own write.
        #
        # FAIL-CLOSED like the real thing (mission_runner._perform_seam_commit): without
        # the guard, a spawn that did not forward --seam-commands hits open("") and dies
        # with a FileNotFoundError traceback, which the harness would classify as a
        # tooling-mission failure -- a confusing, self-inflicted verdict standing in for
        # the plain "this fixture was not wired up".
        if not args.seam_commands:
            print("[Mission][Error][Seam] midcommit requires --seam-commands; none given")
            return 1
        with open(args.seam_commands, "a", encoding="utf-8") as fh:
            fh.write("id=%s cmd=CommitTree\n" % args.seam_commit_id)
        print("[Mission][Info][Seam] commit command written id=%s cmd=CommitTree"
              % args.seam_commit_id)

    if args.mode in ("assertfail", "midcommit"):
        verdict = "MISSION-ASSERT-FAIL"
        reason = "fake apoapsis outside window"
        exit_code = 1
    else:
        verdict = "MISSION-OK"
        reason = "fake landed within apoapsis window"
        exit_code = 0

    result = {
        "schema": 1,
        "mission": "fake",
        "verdict": verdict,
        "reason": reason,
        "phasesReached": ["PRELAUNCH", "ASCENT", "COAST", "DESCENT", "LANDED"],
        "connect": {"attempts": 1, "connectedSeconds": 0.1, "rpcPort": args.rpc_port},
        "assertions": [
            {"name": "apoapsisWindow", "met": verdict == "MISSION-OK",
             "value": 14210.4, "window": [6000, 30000]},
        ],
        "wallSeconds": 1,
        "krpcClientVersion": "0.5.4",
        "krpcServerVersion": "0.5.4",
        "error": None,
    }
    with open(args.result, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(result, sort_keys=True, indent=2) + "\n")
    print("[Mission][Info][Verdict] mission verdict=%s reason=%s" % (verdict, reason))
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
