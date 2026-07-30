"""Fake-KSP stub process for the M-A5 run.py shell smoke test.

An agent cannot pilot a real KSP (MEMORY: in-game-sweep-needs-operator), so the
run.py I/O shell is exercised by this stub: a real child process that reads the
M-A2 command file, writes scripted response + journal lines, and drops a
BATCH_COMPLETE-bearing KSP.log + a results file -- exactly the artifacts a real
game leaves -- so the harness's tail / dedupe / budget-kill / verify plumbing is
driven end to end with NO Unity. It is launched by the smoke test's FakeRuntime
in place of KSP_x64.exe.

Modes:
  pass       respond OK to every command; on RunTests emit BATCH_COMPLETE
             (failed=0) + a clean results file; exit 0 on FlushAndQuit.
  hang       respond to the boot steps, then WEDGE on RunTests (never respond),
             so the harness run-budget watchdog must kill the process tree.
  bootcrash  exit(1) immediately with no response line (boot-phase self-exit).
  autopilot  respond OK to every command (M-B1 handoff: LoadGame -> SetSetting ->
             [mission phase, no channel traffic] -> CommitTree -> FlushAndQuit);
             simulate Parsek AUTO-RECORD by emitting "Recording started" on
             LoadGame + "Recording stopped" on CommitTree and dropping ONE .prec
             recording into the staged save, so the flown scenario's
             recordings.count.min>=1 + REC log rules are satisfied exactly as a
             real auto-recorded flight would satisfy them.
  autopilot-loadfail
             like autopilot, but the boot LoadGame returns verdict=ERROR (a boot
             that never settled to FLIGHT). No recording is started/dropped. run.py
             must SKIP the mission spawn (design handoff step 1: only hand off after
             a LoadGame OK) so a dead boot never burns the mission budget.
  autopilot-kerballost
             like autopilot (the mission flies and returns MISSION-OK), but the
             post-mission EvaChuteDeploy answers verdict=ERROR msg=eva-chute-kerbal-lost
             and logs the matching [Parsek][ERROR] line. This is EVA-4-atmo-chute
             flight 3 (2026-07-25) verbatim: a MISSION-OK run whose subject died in the
             seam tail. The harness must red it on the OUTCOME channel, not only on a
             spec-authored expectation regex.
  autopilot-evarefused
             like autopilot, but the post-mission EvaExit answers
             verdict=REJECTED msg=kerbal-not-aboard - a SPEC fault (a typo'd kerbal
             name), not a flight outcome. The harness must classify it exactly as the
             same refusal pre-mission would be: a retryable driver-stage INVALID, never
             PARSEK-FAIL against the mod.
  multipass  like pass, but a multi-category RunTests (category "A,B" / "all") emits
             a per-category BATCH_COMPLETE line for each token PLUS the final
             category=multi:<count> aggregate (all failed=0), the M-A3 multi-category
             autorun shape.
  multinoagg like multipass, but OMITS the category=multi:<count> aggregate line (the
             per-category lines are present, the summary is not) -- the defined-fault
             shape the harness must red batch-incomplete instead of reading green off a
             per-category line.
  multimismatch
             like multipass, but the category=multi:<count> aggregate declares ONE MORE
             category than per-category lines present (a category batch cut off before
             its BATCH_COMPLETE) -- the SF2 count-mismatch defined fault the harness must
             red batch-incomplete instead of reading green off the mis-counted aggregate.
  scene-route-wrong
             R12 negative control. Like `pass`, but the boot LoadGame IGNORES its
             scene= argument: it still answers verdict=OK (with scene=SPACECENTER in the
             payload, so the divergence is on the wire) and the batch then runs at
             SPACECENTER with every test scene-skipped -- `total=5 passed=0 failed=0
             skipped=5 ... scene=SPACECENTER`. This is the B10 shape verbatim: a batch
             that executed ZERO tests while every cheap contract (`failed=0`, a nonzero
             BATCH_COMPLETE, exit 0, a clean analyzer) reads green. The ONLY thing that
             can red it is the anti-vacuity WHOLE-tally pin naming the scene, so a spec
             carrying one must go PARSEK-FAIL here and PASS under `pass` -- same spec,
             only the fake lies. See test_run_smoke.SceneRoutedBootSmokeTests.

The honest modes HONOUR `scene=`: absent -> FLIGHT (the pre-R12 focusable route),
`scene=spacecenter` -> SPACECENTER, `scene=trackstation` -> TRACKSTATION. The landing
scene is reported on the LoadGame response AND on the BATCH_COMPLETE line, which is
where a spec's tally pin reads it from.

ASCII only; stdlib only.
"""

from __future__ import annotations

import argparse
import os
import sys
import time

COMMANDS = "parsek-test-commands.txt"
RESPONSES = "parsek-test-responses.txt"
JOURNAL = "parsek-test-commands.journal"
RESULTS = "parsek-test-results.txt"
KSP_LOG = "KSP.log"

# R12. The seam's `scene=` argument is fail-closed and lowercase-only; these are the
# GameScenes tokens each accepted spelling lands on, and absent = the pre-R12 focusable
# route. The fake reports the landing scene on both surfaces a real run carries it on
# (the LoadGame response payload and the BATCH_COMPLETE line) so a scene-route spec has
# something real to pin.
SCENE_ARG_LANDING = {"spacecenter": "SPACECENTER", "trackstation": "TRACKSTATION"}
DEFAULT_LANDING_SCENE = "FLIGHT"


def _read_commands(path):
    if not os.path.isfile(path):
        return []
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return [l.rstrip("\n") for l in fh if l.strip()]
    except OSError:
        return []


def _parse(line):
    fields = {}
    for tok in line.split():
        if "=" in tok:
            k, _, v = tok.partition("=")
            fields.setdefault(k, v)
    return fields


def _append(path, text):
    with open(path, "a", encoding="utf-8") as fh:
        fh.write(text)
        fh.flush()


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, help="instance KSP root (channel files live here)")
    parser.add_argument("--mode", default="pass",
                        choices=["pass", "hang", "bootcrash", "autopilot", "autopilot-loadfail",
                                 "autopilot-kerballost", "autopilot-evarefused",
                                 "multipass", "multinoagg", "multimismatch",
                                 "scene-route-wrong"])
    parser.add_argument("--max-seconds", type=float, default=120.0)
    args = parser.parse_args(argv)

    root = args.root
    log_path = os.path.join(root, KSP_LOG)
    _append(log_path, "[LOG] [Parsek][INFO][Init] SessionStart runUtc=9000\n")
    _append(log_path, "[LOG] [Parsek][INFO][TestCommands] armed session=fake pid=%d\n" % os.getpid())

    if args.mode == "bootcrash":
        _append(log_path, "[LOG] Fatal boot error (fake bootcrash)\n")
        return 1

    responses_path = os.path.join(root, RESPONSES)
    journal_path = os.path.join(root, JOURNAL)
    processed = set()
    seq = 0
    deadline = time.time() + args.max_seconds
    # The scene the (fake) game is sitting in. Set by LoadGame, read by RunTests when it
    # stamps its BATCH_COMPLETE line -- the same coupling a real run has, and the whole
    # reason a wrong-scene boot is observable at all.
    landing_scene = DEFAULT_LANDING_SCENE

    while time.time() < deadline:
        for line in _read_commands(os.path.join(root, COMMANDS)):
            fields = _parse(line)
            cid = fields.get("id")
            cmd = fields.get("cmd")
            if not cid or cid in processed:
                continue
            if cmd == "RunTests" and args.mode == "hang":
                # Wedge: never respond, so the harness budget watchdog kills us.
                _append(log_path, "[LOG] [Parsek][INFO][TestRunner] RunTests wedged (fake hang)\n")
                processed.add(cid)  # do not re-log; just spin below
                continue
            processed.add(cid)
            seq += 1
            _append(journal_path, "id=%s cmd=%s phase=EXECUTED\n" % (cid, cmd))
            # A boot that never settles to FLIGHT: LoadGame reports ERROR and no
            # recording is started (design handoff step 1 test seam).
            load_failed = (args.mode == "autopilot-loadfail" and cmd == "LoadGame")
            # EVA-4 flight 3: the kerbal's canopy is cut mid-descent and the kerbal
            # dies, so the verb's own bounded wait gives up on its named terminal.
            kerbal_lost = (args.mode == "autopilot-kerballost" and cmd == "EvaChuteDeploy")
            eva_refused = (args.mode == "autopilot-evarefused" and cmd == "EvaExit")
            # R12 scene routing. An honest boot lands where scene= asked; the
            # scene-route-wrong mode answers OK and lands at SPACECENTER regardless,
            # which is the B10 fail-open shape a spec's whole-tally pin must catch.
            if cmd == "LoadGame":
                requested = fields.get("scene")
                if args.mode == "scene-route-wrong":
                    landing_scene = "SPACECENTER"
                else:
                    landing_scene = SCENE_ARG_LANDING.get(requested, DEFAULT_LANDING_SCENE)
            if args.mode in ("autopilot", "autopilot-loadfail", "autopilot-kerballost",
                             "autopilot-evarefused") and not load_failed:
                # Simulate Parsek auto-record around the flown mission: start on
                # the launch (LoadGame) transition, drop a recording + stop it at
                # commit time, so the flown scenario has a recording by commit.
                if cmd == "LoadGame":
                    _append(log_path, "[LOG] [Parsek][INFO][Recorder] Recording started\n")
                    _drop_recording(root)
                elif cmd == "CommitTree":
                    _append(log_path, "[LOG] [Parsek][INFO][Recorder] Recording stopped\n")
            if cmd == "RunTests":
                category = fields.get("category", "RecordingInvariants")
                if args.mode in ("multipass", "multinoagg", "multimismatch"):
                    _emit_multi_batch(log_path, category,
                                      emit_aggregate=(args.mode != "multinoagg"),
                                      count_delta=(1 if args.mode == "multimismatch" else 0))
                elif args.mode == "scene-route-wrong":
                    # Booted to the wrong scene, so BOTH runner filters skip every
                    # member: total is unchanged (it counts declarations) while passed
                    # collapses to 0. failed=0 is still true, which is exactly why
                    # `failed=0\b` alone cannot gate this.
                    _append(log_path,
                            "[LOG] [Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=5 "
                            "passed=0 failed=0 skipped=5 category=%s scene=%s\n"
                            % (category, landing_scene))
                else:
                    _append(log_path,
                            "[LOG] [Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=5 "
                            "passed=5 failed=0 skipped=0 category=%s scene=%s\n"
                            % (category, landing_scene))
                _write_results(os.path.join(root, RESULTS), category)
            if eva_refused:
                _append(responses_path,
                        "id=%s cmd=%s verdict=REJECTED seq=%d msg=kerbal-not-aboard\n"
                        % (cid, cmd, seq))
                continue
            if kerbal_lost:
                _append(log_path,
                        "[LOG] [Parsek][ERROR][TestCommands] evachutedeploy "
                        "eva-chute-kerbal-lost kerbal=Jebediah Kerman canopy=true "
                        "chuteState=Stowed situation=FLYING alive=false elapsed=20.5s\n")
                _append(responses_path,
                        "id=%s cmd=%s verdict=ERROR seq=%d msg=eva-chute-kerbal-lost\n"
                        % (cid, cmd, seq))
                continue
            verdict = "ERROR" if load_failed else "OK"
            # Both scene-routing verbs report their LANDING scene in the payload, as the
            # real seam does. Nothing in run.py reads it today (no payload field is ever
            # captured -- roadmap Cause C's "no runtime-to-spec data path"), so it is on
            # the wire for a human reading a collected channel file, and for whatever
            # closes that gap later. The gate is the BATCH_COMPLETE tally, not this key.
            payload = ""
            if verdict == "OK" and cmd == "LoadGame":
                payload = " scene=%s" % landing_scene
            elif verdict == "OK" and cmd == "ExitToSpaceCenter":
                landing_scene = "SPACECENTER"
                payload = " scene=SPACECENTER"
            _append(responses_path, "id=%s cmd=%s verdict=%s seq=%d%s\n"
                    % (cid, cmd, verdict, seq, payload))
            if cmd == "FlushAndQuit":
                _append(log_path, "[LOG] [Parsek][INFO][TestCommands] flushandquit: quitting (fake)\n")
                return 0
        time.sleep(0.05)

    return 0


def _drop_recording(root):
    """Simulate a Parsek auto-recorded flight by dropping one .prec into the staged
    save's Parsek/Recordings dir. The staging step created exactly one save dir
    under saves/, so the single subdir IS the run save (the fake KSP is not told
    the leaf name by run.py)."""
    saves = os.path.join(root, "saves")
    if not os.path.isdir(saves):
        return
    subdirs = [d for d in sorted(os.listdir(saves))
               if os.path.isdir(os.path.join(saves, d))]
    if not subdirs:
        return
    rec_dir = os.path.join(saves, subdirs[0], "Parsek", "Recordings")
    os.makedirs(rec_dir, exist_ok=True)
    prec = os.path.join(rec_dir, "mission-flight.prec")
    if not os.path.isfile(prec):
        with open(prec, "w", encoding="utf-8") as fh:
            fh.write("# fake auto-recorded mission flight\n")


def _emit_multi_batch(log_path, category, emit_aggregate, count_delta=0):
    """Emit the M-A3 multi-category autorun BATCH_COMPLETE shape: one per-category
    line per token, then (unless suppressed) the final category=multi:<count>
    aggregate carrying the union tally. All failed=0 (a clean multi-category run).
    ``count_delta`` skews the aggregate's declared <count> away from the per-category
    line count (SF2 count-mismatch fault seam); 0 = a consistent count."""
    if category == "all":
        cats = ["CatOne", "CatTwo"]
    else:
        cats = [c.strip() for c in category.split(",") if c.strip()]
    total = 0
    for c in cats:
        _append(log_path,
                "[LOG] [Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=3 "
                "passed=3 failed=0 skipped=0 category=%s scene=FLIGHT\n" % c)
        total += 3
    if emit_aggregate:
        _append(log_path,
                "[LOG] [Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=%d "
                "passed=%d failed=0 skipped=0 category=multi:%d scene=FLIGHT\n"
                % (total, total, len(cats) + count_delta))


def _write_results(path, category):
    text = (
        "PARSEK TEST RESULTS\n"
        "===================\n"
        "ALL RESULTS (grouped by scene)\n"
        "  FLIGHT\n"
        "    PASSED  %s.SomeTest\n"
        "\n"
        "FAILURES (grouped by scene)\n"
        "  (none)\n"
    ) % category
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text)


if __name__ == "__main__":
    sys.exit(main())
