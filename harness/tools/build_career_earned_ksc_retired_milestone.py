#!/usr/bin/env python3
"""Build the `career-earned-ksc-retired-milestone` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. TL-1 is the live read of TIMELINE-PHANTOM-MILESTONE-AFTER-REFLY-OF-LATER-
SEGMENT (fixed 2026-10-24): a milestone whose ledger row a Re-Fly merge tombstoned, while
its `MilestoneStore` copy stays tagged to a recording the ERS still shows, used to come
back on the Timeline as a legacy "Milestone" text row. The precondition is a SAVE state:
a `LEDGER_TOMBSTONES` entry retiring a `MilestoneAchievement` row, plus a committed
milestone-store event with the same (type, UT, key) tagged to a visible recording.

NO FLIGHT CAN PRODUCE IT ON THE CURRENT BUILD. Since the 2026-10-19 ledger-safety rulings
(design `docs/parsek-rewind-to-separation-design.md` section 2.12, Rule 1) a milestone is
a SEALING row (`SupersedeCommit.IsLossRow` lists only crew losses, loss reputation and
building damage), `RewindInvoker.CanInvoke` precondition 7 refuses any slot whose retire
set holds one, and `SupersedeCommit.CommitTombstones` - the only tombstone writer - writes
exactly that retire set. So a milestone tombstone exists only in a save a pre-ruling
merge wrote; the section 2.12 migration repairs no tombstone already written. Those saves
are the population the fix still serves, and this fixture is one of them, made on
purpose.

WHAT IT IS. `career-earned-ksc` (vessel-less career, boots to the Space Center, one
committed tree "Jumping Flea" with two recordings, nothing superseded) plus ONE
`LEDGER_TOMBSTONES` node in the ParsekScenario node, retiring the `Kerbin/Science`
milestone row (UT 346.08, `act_9483fb3b...`, tagged to `1d611e75...`). Its copy in
`Parsek/GameState/milestones.pgsm` is tagged to the same, visible, recording - the same
display state the todo describes (the copy kept the first segment's tag while the row was
retired). The other three milestone rows stay effective, so their copies stay hidden by
the ordinary ELS dedup and the lane can tell the two paths apart. Every other byte is the
base's.

Run `python harness/tools/build_career_earned_ksc_retired_milestone.py` to (re)write the
fixture and `--check` to re-derive and compare byte for byte;
`harness/lib/test_career_earned_ksc_retired_milestone.py` runs the check in process.

Stdlib only; ASCII only; no em dashes.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import sys

_HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASE_DIR = os.path.join(_HARNESS_ROOT, "fixtures", "saves", "career-earned-ksc")
TARGET_DIR = os.path.join(_HARNESS_ROOT, "fixtures", "saves", "career-earned-ksc-retired-milestone")

RETIRED_ACTION_ID = "act_9483fb3bdbe34043a3405851d4a69450"
RETIRED_MILESTONE_ID = "Kerbin/Science"
RETIRED_UT = "346.0799999999752"
TAGGED_RECORDING_ID = "1d611e7533a64508ae6f3b305a51615e"
# The tombstone names the flight's second recording as the retiring one; nothing reads the
# field beyond diagnostics (the load-time orphan check matches on the action id only).
RETIRING_RECORDING_ID = "5436a7e8840b4c5885afcbaedc9dc037"
TOMBSTONE_ID = "tomb_7a1f0e5c2d3b4a69b8c7d6e5f4a3b2c1"
TOMBSTONE_UT = "348.07999999997338"
TOMBSTONE_CREATED = "2026-10-12T12:00:00.0000000Z"


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def tombstone_lines() -> list:
    """The LEDGER_TOMBSTONES node at the ParsekScenario child depth (two tabs), in the
    shape `LedgerTombstone.SaveInto` writes."""
    return [
        "\t\tLEDGER_TOMBSTONES",
        "\t\t{",
        "\t\t\tENTRY",
        "\t\t\t{",
        "\t\t\t\ttombstoneId = " + TOMBSTONE_ID,
        "\t\t\t\tactionId = " + RETIRED_ACTION_ID,
        "\t\t\t\tretiringRecordingId = " + RETIRING_RECORDING_ID,
        "\t\t\t\tut = " + TOMBSTONE_UT,
        "\t\t\t\tcreatedRealTime = " + TOMBSTONE_CREATED,
        "\t\t\t}",
        "\t\t}",
    ]


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    """The base save with the LEDGER_TOMBSTONES node appended as the ParsekScenario
    node's last child. Line endings and every other byte are preserved."""
    nl = b"\r\n" if b"\r\n" in source_bytes else b"\n"
    lines = source_bytes.split(nl)
    starts = [i for i, l in enumerate(lines) if l.strip() == b"name = ParsekScenario"]
    assert len(starts) == 1, "expected one ParsekScenario node, found %d" % len(starts)
    assert b"LEDGER_TOMBSTONES" not in source_bytes, "the base already carries tombstones"
    # Walk from the node's opening brace (the line above `name =`) to its closing one.
    depth = 1
    i = starts[0]
    while depth > 0:
        i += 1
        s = lines[i].strip()
        if s == b"{":
            depth += 1
        elif s == b"}":
            depth -= 1
    close = i
    insert = [l.encode("ascii") for l in tombstone_lines()]
    return nl.join(lines[:close] + insert + lines[close:])


def expected_files() -> dict:
    """{relative path: bytes} of every file the fixture must contain."""
    files = {}
    for dirpath, _dirnames, filenames in os.walk(BASE_DIR):
        for fn in filenames:
            rel = os.path.relpath(os.path.join(dirpath, fn), BASE_DIR).replace(os.sep, "/")
            files[rel] = _read(os.path.join(dirpath, fn))
    files["persistent.sfs"] = derive_persistent_sfs(files["persistent.sfs"])
    return files


def check() -> list:
    """Differences between a fresh derivation and the committed fixture (empty = OK)."""
    problems = []
    expected = expected_files()
    for rel, data in expected.items():
        path = os.path.join(TARGET_DIR, rel)
        if not os.path.isfile(path):
            problems.append("missing %s" % rel)
        elif _read(path) != data:
            problems.append("%s differs from a fresh derivation" % rel)
    if os.path.isdir(TARGET_DIR):
        for dirpath, _, filenames in os.walk(TARGET_DIR):
            for fn in filenames:
                rel = os.path.relpath(os.path.join(dirpath, fn), TARGET_DIR).replace(os.sep, "/")
                if rel not in expected:
                    problems.append("unexpected file in fixture: %s" % rel)
    return problems


def _ledger_rows(ledger_text: str) -> list:
    rows = []
    for block in re.findall(r"GAME_ACTION\n\{(.*?)\n\}", ledger_text, re.S):
        rows.append(dict(re.findall(r"^\t(\w+) = (.*?)$", block, re.M)))
    return rows


def verify_shape(sfs_text: str, ledger_text: str, milestones_text: str) -> list:
    """Post-conditions the fixture exists for, stated on the derived bytes (LF text)."""
    problems = []
    if re.search(r"^\t*Mode = CAREER$", sfs_text, re.M) is None:
        problems.append("not a CAREER save")
    if re.search(r"^\t+VESSEL$", sfs_text, re.M) is not None:
        problems.append("carries a VESSEL node; the Space Center route needs none")
    if "RECORDING_SUPERSEDES" in sfs_text:
        problems.append("carries a supersede row; the tagged recording must stay visible")
    entries = re.findall(r"^\t\t\t\tactionId = (\S+)$", sfs_text, re.M)
    if entries != [RETIRED_ACTION_ID]:
        problems.append("expected exactly one tombstone retiring %s, found %r"
                        % (RETIRED_ACTION_ID, entries))
    rows = [r for r in _ledger_rows(ledger_text) if r.get("actionId") == RETIRED_ACTION_ID]
    if len(rows) != 1:
        problems.append("the retired action is not one ledger row")
    else:
        row = rows[0]
        if row.get("type") != "4" or row.get("milestoneId") != RETIRED_MILESTONE_ID:
            problems.append("the retired row is not the %s milestone" % RETIRED_MILESTONE_ID)
        if row.get("ut") != RETIRED_UT or row.get("recordingId") != TAGGED_RECORDING_ID:
            problems.append("the retired row moved (ut / recordingId)")
    copy = re.search(
        r"GAME_STATE_EVENT\n\t\{\n\t\tut = %s\n\t\ttype = 18\n\t\tkey = %s\n[^}]*?"
        r"\t\trecordingId = %s\n\t\}" % (re.escape(RETIRED_UT), re.escape(RETIRED_MILESTONE_ID),
                                         TAGGED_RECORDING_ID),
        milestones_text)
    if copy is None:
        problems.append("no milestone-store copy of %s tagged to %s"
                        % (RETIRED_MILESTONE_ID, TAGGED_RECORDING_ID))
    if ("recordingId = " + TAGGED_RECORDING_ID) not in sfs_text:
        problems.append("the tagged recording is not in the committed tree")
    return problems


def _lf(data: bytes) -> str:
    return data.decode("utf-8", "replace").replace(chr(13) + chr(10), chr(10))


def verify_files(files: dict) -> list:
    return verify_shape(_lf(files["persistent.sfs"]),
                        _lf(files["Parsek/GameState/ledger.pgld"]),
                        _lf(files["Parsek/GameState/milestones.pgsm"]))


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture; exit 1 on drift")
    args = parser.parse_args(argv)
    if not os.path.isdir(BASE_DIR):
        print("FAIL: base fixture missing: %s" % BASE_DIR)
        return 1
    files = expected_files()
    shape = verify_files(files)
    for p in shape:
        print("FAIL: " + p)
    if shape:
        return 1
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("career-earned-ksc-retired-milestone: %s"
              % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    shutil.rmtree(TARGET_DIR, ignore_errors=True)
    for rel, data in files.items():
        path = os.path.join(TARGET_DIR, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote %s (%d files)" % (TARGET_DIR, len(files)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
