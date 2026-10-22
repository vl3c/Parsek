#!/usr/bin/env python3
"""Build the committed `career-earned-pad-oldgen` fixture BY CONSTRUCTION (the GC-3 host).

WHY THIS FIXTURE EXISTS. Todo GC3-SCHEMA-REJECTED-TREES-LOSE-EARNED-VALUE (PR #2158,
owner ruling 2026-10-21 "Keep them inert, untouched"): a committed recording tree from
another recording schema generation is rejected by the load gate (by design, no
migration), but the rejection used to spread. The next save dropped the tree for good,
the ledger reconcile pruned every earning row tagged to its recordings, and stock
milestone flags were cleared. The fix keeps such a tree INERT (`InertRecordingTrees`):
its node is written back verbatim on every save and its recording ids count as known.
No committed fixture carried a tree of another generation, so the fix had only unit
tests.

`career-earned-pad` is the right base: a CAREER whose one committed tree ("Jumping
Flea", two recordings) EARNED, with 11 of its 16 ledger rows tagged to those recordings
(four milestones, three science subjects, a recovery funds row, kerbal rows), and a
PRELAUNCH pad craft so `LoadGame` boots into FLIGHT. No committed career fixture
carries both earned tagged rows and a rewind point; this one carries no rewind point.

THE EDITS, and why they are the minimal ones.
  1. `persistent.sfs`: the TREE-level `recordingSchemaGeneration = 4` (the value
     directly under `RECORDING_TREE`) becomes `3`. `RecordingTree.Load` checks that
     key first and returns an empty tree on a mismatch, so
     `InertRecordingTrees.ClassifySchemaRejection` answers `tree-generation-older` and
     the WHOLE tree is set aside without reading any recording. The two per-recording
     stamps and the sidecars stay at 4 on purpose: the load never reads them for a
     tree the tree-level gate rejected, and the offline analyzer (which reads each
     RECORDING's own stamp and ignores the tree-level one) keeps seeing the base's
     healthy recordings, so a lane on this fixture is not red by an analyzer INV5
     finding about a stamp the product never consults.
  2. `Parsek/GameState/ledger.pgld`: the header `recordingSchemaGeneration = 4`
     becomes `3`, as a save written by a generation-3 build carries it. This is the
     second owner ruling's cell (GC3-LEDGER-GENERATION-GATE-DROPS-ROWS): the ledger
     loads when its own `version` matches, whatever recording generation it names.
     The rows are untouched.
  3. The save `Title`, so the Load menu names the fixture.
Every other file is career-earned-pad's byte for byte (CRLF kept).

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_career_earned_pad_oldgen.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "career-earned-pad")
OUT_DIR = os.path.join(SAVES, "career-earned-pad-oldgen")

BASE_TITLE = "career-earned-pad (CAREER)"
TITLE = "career-earned-pad-oldgen (CAREER)"
TREE_ID = "5eb172ab29034ccbbac497ce6406c149"
BASE_GENERATION = 4
OTHER_GENERATION = 3
CRLF = "\r\n"
SFS = "persistent.sfs"
LEDGER = "Parsek/GameState/ledger.pgld"
EDITED = (SFS, LEDGER)

TREE_GEN_LINE = "\t\t\trecordingSchemaGeneration = %d"
LEDGER_GEN_LINE = "recordingSchemaGeneration = %d"


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def tree_blocks(lines: List[str]) -> List[tuple]:
    """(start, end_exclusive, id) of every depth-2 RECORDING_TREE block in the save."""
    out = []
    i = 0
    while i < len(lines):
        if lines[i] == "\t\tRECORDING_TREE":
            assert lines[i + 1] == "\t\t{", "RECORDING_TREE not followed by an open brace"
            j = i + 2
            while lines[j] != "\t\t}":
                j += 1
            tree_id = ""
            for k in range(i + 2, j):
                if lines[k].startswith("\t\t\tid = "):
                    tree_id = lines[k][len("\t\t\tid = "):]
                    break
            out.append((i, j + 1, tree_id))
            i = j
        i += 1
    return out


def build_sfs() -> str:
    base = _read(os.path.join(BASE_DIR, SFS))
    assert CRLF in base, "career-earned-pad is committed CRLF"
    lines = base.split(CRLF)
    trees = tree_blocks(lines)
    assert [t[2] for t in trees] == [TREE_ID], [t[2] for t in trees]
    start, end, _ = trees[0]
    assert not any(l.startswith("\t\t\tisActive = ") or l.startswith("\t\t\tisPending = ")
                   for l in lines[start:end]), "the base tree must be a committed tree"
    gen_rows = [k for k in range(start, end) if lines[k] == TREE_GEN_LINE % BASE_GENERATION]
    assert len(gen_rows) == 1, "tree-level generation line not found exactly once"
    out = list(lines)
    out[gen_rows[0]] = TREE_GEN_LINE % OTHER_GENERATION
    title = out.index("\tTitle = " + BASE_TITLE)
    out[title] = "\tTitle = " + TITLE
    return CRLF.join(out)


def build_ledger() -> str:
    base = _read(os.path.join(BASE_DIR, LEDGER.replace("/", os.sep)))
    assert CRLF in base, "career-earned-pad's ledger is committed CRLF"
    lines = base.split(CRLF)
    assert lines[0] == "version = 0", "ledger version line moved"
    assert lines[1] == LEDGER_GEN_LINE % BASE_GENERATION, "ledger generation line moved"
    lines[1] = LEDGER_GEN_LINE % OTHER_GENERATION
    return CRLF.join(lines)


def expected_files() -> Dict[str, bytes]:
    """{'/'-separated relative path: bytes} of every file the fixture holds."""
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = _read_bytes(path)
    files[SFS] = build_sfs().encode("utf-8")
    files[LEDGER] = build_ledger().encode("utf-8")
    return files


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    if os.path.isdir(OUT_DIR):
        for root, _dirs, names in os.walk(OUT_DIR):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, OUT_DIR).replace(os.sep, "/")] = _read_bytes(path)
    problems = []
    for rel in sorted(set(want) | set(have)):
        if rel not in have:
            problems.append("missing " + rel)
        elif rel not in want:
            problems.append("unexpected " + rel)
        elif have[rel] != want[rel]:
            problems.append("drift in " + rel)
    return problems


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="re-derive and compare against the committed fixture")
    args = ap.parse_args(argv)
    if args.check:
        problems = committed_problems()
        for p in problems:
            print("DRIFT: " + p)
        print("OK" if not problems else "FAIL")
        return 0 if not problems else 1
    if os.path.isdir(OUT_DIR):
        shutil.rmtree(OUT_DIR)
    for rel, data in expected_files().items():
        path = os.path.join(OUT_DIR, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote " + OUT_DIR)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
