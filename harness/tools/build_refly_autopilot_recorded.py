#!/usr/bin/env python3
"""Finish and gate `refly-autopilot-recorded`, the FIXED-BEHAVIOUR TWIN of
`refly-a-recorded`.

WHAT THIS SAVE IS. The produced save of RF-9's second reading run
(`2026-09-08_2258_RF-9-atmosphere-exit-split-stays-open`, PASS attempt 1, on the
post-#1658 / post-#1659 DLL, deployed hash cd8ddb6b691e3fb8), harvested by the
generic

    python harness/tools/harvest_bdock_station.py \\
        --save-dir harness/results/<runId>_save \\
        --target-name refly-autopilot-recorded --keep-parsek

WHY IT IS WORTH COMMITTING, in one sentence: it is the SAME FLIGHT SHAPE as the
operator's manual seed - a crewed Kerbal X whose probe-cored core comes off inside
the atmosphere, whose top stack then coasts out through 70 km and is left there at
the scene exit, whose promoted recording the optimizer therefore SPLITS at an
Atmospheric -> ExoBallistic boundary - and it carries the OPPOSITE OUTCOME in its
committed bytes.

    refly-a-recorded  (pre-#1658)   TIP 8da7c2c2  chainIndex=1  NO mergeState key
    refly-autopilot   (post-#1658)  TIP a76c3839  chainIndex=1  mergeState =
                                                                CommittedProvisional

That pair is the whole regression floor for PR #1658, readable with no flight at
all: the terminal moved to the tip and, now, so did the open bit. The probe half
carries the identical pair on its own chain (HEAD 490f0f52 / TIP 99ae6e58), so the
fixture proves the carry on BOTH slots of one RewindPoint rather than on one.

AND IT IS REPRODUCIBLE, which the seed is not. `refly-a-recorded` came out of a
save an operator flew by hand and can never fly identically again; this one is the
output of a committed spec plus a committed mission profile, so a future re-harvest
is a command rather than an evening.

WHAT THIS TOOL DOES, AND WHAT IT DELIBERATELY DOES NOT. It is a POST-CONDITION GATE
and nothing else. An earlier cut of it also restored `Parsek/Saves/` and the
`rewindSave` hint the harvester prunes, on the belief that a rewind-to-LAUNCH lane
would need a fixture-committed launch quicksave. THAT BELIEF WAS WRONG, and H58's own
header says so in as many words: the absence is POLICY rather than decay
(`harvest_bdock_station.py` prunes the directory and clears the hint, and
`build_rover_route_recorded.py` gates the absence in BOTH directions), and H58 does not
rewind a fixture tree at all - it PRODUCES its own subject in-run, because
`FlightRecorder.CaptureRewindSave` writes the quicksave at every non-promotion recording
start, so `StartRecording` / `StopRecording` / `CommitTree` mints a rewindable tree that
`tree=latest` then resolves to. So the restore was solving a problem no lane has, in a
way the corpus forbids, and it is gone.

Everything else here is POST-CONDITIONS, and they carry the weight. `--check` runs
them without touching the tree and is what `harness/lib/test_refly_autopilot_recorded.py`
executes in process.

    python harness/tools/build_refly_autopilot_recorded.py \\
        --source harness/results/<runId>_save        # restore + verify
    python harness/tools/build_refly_autopilot_recorded.py --check   # verify only

IT CANNOT RE-RUN THE HARVEST from the source: the produced save is a results
artifact and is not committed. What it CAN do is re-run every post-condition, which
is what a drift gate over a committed fixture actually needs.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import io
import os
from typing import List, Optional, Sequence

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "refly-autopilot-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")

EXPECT_TITLE = "refly-autopilot-recorded (SANDBOX)"

# The RewindPoint the flight's own core discard authored, and the file its
# quicksaveFilename names. Both are asserted: a row with no file is a point that
# cannot be re-flown, and a file with no row is dead weight.
EXPECT_RP_ID = "rp_404ea488f5ce40b6bd0bef9405f8c1d9"

# THE TWO SPLIT PAIRS, and the reason this fixture exists. Each is
# (chainId, HEAD id, TIP id): the optimizer cut both the pod's and the probe's
# recording at the atmosphere exit, and on a post-#1658 DLL both TIPs must carry
# `mergeState = CommittedProvisional`. On a pre-fix DLL the TIP has no mergeState
# key at all, which the codec reads back as Immutable - that is the sealing defect,
# and `refly-a-recorded` is its frozen copy.
EXPECT_SPLIT_PAIRS = (
    ("a130b3869834479db1efa7718a177c54",
     "4a7739f6cc074185a82cbd10ccaeb01d", "a76c38394cbd4f7b9053d656fc80d134"),
    ("e8b07a0cc55a4e88a1d427afc5386ed6",
     "490f0f52563240c7b56e1fd5b44b56dd", "99ae6e587bd74328a8ab70f7bb434ad6"),
)

EXPECT_RECORDING_COUNT = 11

# The chain TIP whose two `isPredicted` ORBIT_SEGMENTs are the render subject: the
# extrapolated re-entry the finalizer appended when the scene exited above the
# atmosphere. It is the same shape `refly-a-recorded`'s 8da7c2c2 carries, which is
# what makes the two fixtures readable against each other.
EXPECT_PREDICTED_TAIL_REC = "a76c38394cbd4f7b9053d656fc80d134"
EXPECT_PREDICTED_TAIL_SEGMENTS = 2


def _read_lines(path: str) -> List[str]:
    with io.open(path, "r", encoding="utf-8", errors="replace") as handle:
        return handle.read().split("\n")


def _values(lines: List[str], key: str) -> List[str]:
    prefix = key + " = "
    return [l.strip()[len(prefix):] for l in lines if l.strip().startswith(prefix)]


def _recording_blocks(lines: List[str]) -> List[dict]:
    """Every RECORDING's flat key set, keyed off `recordingId`.

    A deliberately shallow parse: the keys this gate reads (`chainId`,
    `chainIndex`, `mergeState`, `vesselName`) are all siblings of `recordingId`
    inside one node, and a full ConfigNode parser here would be a second
    implementation of something the C# already owns."""
    out: List[dict] = []
    cur: Optional[dict] = None
    for line in lines:
        text = line.strip()
        if text.startswith("recordingId = "):
            cur = {"recordingId": text.split(" = ", 1)[1]}
            out.append(cur)
            continue
        if cur is None:
            continue
        for key in ("chainId", "chainIndex", "mergeState", "vesselName",
                    "terminalState", "pointCount"):
            if text.startswith(key + " = "):
                cur.setdefault(key, text.split(" = ", 1)[1])
    return out


def verify() -> List[str]:
    """Failure strings (empty = every post-condition holds)."""
    problems: List[str] = []
    if not os.path.isfile(FIXTURE_SFS):
        return ["fixture persistent.sfs missing: %s" % FIXTURE_SFS]
    lines = _read_lines(FIXTURE_SFS)

    titles = _values(lines, "Title")
    if titles[:1] != [EXPECT_TITLE]:
        problems.append("Title is %r, expected %r" % (titles[:1], EXPECT_TITLE))

    recs = _recording_blocks(lines)
    if len(recs) != EXPECT_RECORDING_COUNT:
        problems.append("expected %d RECORDING rows, found %d"
                        % (EXPECT_RECORDING_COUNT, len(recs)))
    by_id = {r["recordingId"]: r for r in recs}

    # THE GATE THIS FIXTURE EXISTS FOR. Both halves of both splits must carry the
    # open bit. A TIP that loses its `mergeState` key is not a fixture that drifted;
    # it is PR #1658 regressed, and the right response is to fix the code, never to
    # re-pin this file.
    for chain_id, head_id, tip_id in EXPECT_SPLIT_PAIRS:
        head, tip = by_id.get(head_id), by_id.get(tip_id)
        if head is None or tip is None:
            problems.append("split pair %s is incomplete: head=%s tip=%s"
                            % (chain_id, head is not None, tip is not None))
            continue
        for role, rec, index in (("HEAD", head, "0"), ("TIP", tip, "1")):
            if rec.get("chainId") != chain_id:
                problems.append("%s %s chainId is %r, expected %r"
                                % (role, rec["recordingId"], rec.get("chainId"),
                                   chain_id))
            if rec.get("chainIndex") != index:
                problems.append("%s %s chainIndex is %r, expected %r"
                                % (role, rec["recordingId"], rec.get("chainIndex"),
                                   index))
            if rec.get("mergeState") != "CommittedProvisional":
                problems.append(
                    "%s %s mergeState is %r, expected CommittedProvisional. THIS IS "
                    "THE #1658 REGRESSION FLOOR: a chain TIP with no mergeState key "
                    "reads back Immutable and the slot silently closes. Fix the code, "
                    "do not re-pin the fixture"
                    % (role, rec["recordingId"], rec.get("mergeState")))

    # The RewindPoint the flight authored, on both surfaces.
    rp_rows = [v for v in _values(lines, "rewindPointId") if v == EXPECT_RP_ID]
    if not rp_rows:
        problems.append("no REWIND_POINTS row for %s: this fixture's whole "
                        "difference from refly-a-recorded is that its point SURVIVED"
                        % EXPECT_RP_ID)
    rp_file = os.path.join(FIXTURE_DIR, "Parsek", "RewindPoints",
                           EXPECT_RP_ID + ".sfs")
    if not os.path.isfile(rp_file):
        problems.append("the RewindPoint quicksave is missing: %s" % rp_file)

    # THE REWIND-TO-LAUNCH PAYLOAD, in whichever direction the caller asked for.
    # Committed, it must be ABSENT - the standing rule `CommittedFixtureRewindSaveTests`
    # enforces over every fixture. Right after a restore it must be PRESENT and named.
    # Both are asserted here so the two statements cannot drift apart: the day a lane
    # needs the payload, this gate and that one are amended together.
    saves_dir = os.path.join(FIXTURE_DIR, "Parsek", "Saves")
    saves = sorted(n for n in os.listdir(saves_dir)) if os.path.isdir(saves_dir) else []
    if saves:
        problems.append(
            "Parsek/Saves carries %r. NO recorded fixture carries a rewind-to-launch "
            "quicksave, by policy rather than by accident: the harvester prunes it, "
            "CommittedFixtureRewindSaveTests forbids it, and a lane that needs one "
            "PRODUCES it in-run the way H58 does (StartRecording captures it)" % saves)
    hints = [v for v in _values(lines, "rewindSave") if v]
    if hints:
        problems.append("dangling rewindSave hint(s) %r with no committed payload"
                        % hints)

    # The render subject.
    tail = os.path.join(FIXTURE_DIR, "Parsek", "Recordings",
                        EXPECT_PREDICTED_TAIL_REC + ".prec.txt")
    if not os.path.isfile(tail):
        problems.append("the predicted-tail sidecar is missing: %s" % tail)
    else:
        with io.open(tail, "r", encoding="utf-8", errors="replace") as handle:
            text = handle.read()
        got = text.count("isPredicted = True")
        if got != EXPECT_PREDICTED_TAIL_SEGMENTS:
            problems.append(
                "%s carries %d isPredicted segments, expected %d - the extrapolated "
                "re-entry is the render lanes' subject"
                % (EXPECT_PREDICTED_TAIL_REC, got, EXPECT_PREDICTED_TAIL_SEGMENTS))
    return problems


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="run the post-conditions only; change nothing")
    args = parser.parse_args(list(argv) if argv is not None else None)
    # `--check` is accepted and ignored-as-a-no-op-difference: this tool has only one
    # mode now, and keeping the flag means the invocation in the docstring, in the
    # fixture README and in the drift test all stay valid.
    _ = args.check

    problems = verify()
    for problem in problems:
        print("[Build][FAIL] " + problem)
    if problems:
        return 1
    print("[Build] refly-autopilot-recorded: every post-condition holds")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
