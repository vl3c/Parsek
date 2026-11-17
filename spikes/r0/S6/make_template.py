#!/usr/bin/env python3
"""Build the S6 template save (spike branch only, plan r0-spikes.md task S6 (a)).

The template is the committed career fixture harness/fixtures/saves/cs-career-mun4 plus
its three shared craft (harness/fixtures/shared-ships.toml) plus the race mark of
spikes/r0/S6/template/mark.cfg, inserted into persistent.sfs after the last top-level
SCENARIO of GAME, in KSP's own tab-indented format. The 4.9 MB fixture is not committed a
second time; this script composes it, and the owner stages the result as the lane's
operator-local fixture:

    python spikes/r0/S6/make_template.py --out <umbrella>/logs/spikes/S6/<date>/template/spike-s6-mark
    python harness/tools/stage_local_fixture.py --from <that dir> --as spike-s6-mark

The same output folder, copied to <instance>/saves/spike-s6-mark, is the hand-staged save
of the plan's wording. --out must lie outside the repository. The script then re-reads
persistent.sfs and refuses unless it holds exactly one mark with the pinned hash.

    python spikes/r0/S6/make_template.py --selftest

Stdlib only; ASCII only.
"""

import argparse
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True  # no __pycache__ beside the tools in the repo tree
import s6cfg  # noqa: E402

REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
FIXTURE = os.path.join(REPO, "harness", "fixtures", "saves", "cs-career-mun4")
SHIPS = os.path.join(REPO, "harness", "fixtures", "ships")
CRAFT = ("CS Flea Probe", "Flea Antenna", "Jumping Flea")  # shared-ships.toml row of cs-career-mun4


def insert_mark(text, mark):
    """The save text with `mark` inserted after the last top-level SCENARIO block of GAME.
    Keeps the file's own line ending. Raises ValueError when the shape is not KSP's."""
    nl = "\r\n" if "\r\n" in text else "\n"
    lines = text.split(nl)
    last = -1
    for i, line in enumerate(lines):
        if line == "\tSCENARIO":
            last = i
    if last < 0:
        raise ValueError("no top-level SCENARIO in GAME")
    end = -1
    for j in range(last + 1, len(lines)):
        if lines[j] == "\t}":
            end = j
            break
    if end < 0:
        raise ValueError("last SCENARIO is not closed")
    mark_lines = mark.replace("\r\n", "\n").rstrip("\n").split("\n")
    return nl.join(lines[:end + 1] + mark_lines + lines[end + 1:])


def build(out, fixture=FIXTURE, ships=SHIPS, craft=CRAFT):
    if os.path.exists(out):
        raise ValueError("--out exists already: %s (remove it or pick another)" % out)
    shutil.copytree(fixture, out)
    vab = os.path.join(out, "Ships", "VAB")
    os.makedirs(vab, exist_ok=True)
    for name in craft:
        shutil.copy2(os.path.join(ships, name + ".craft"), os.path.join(vab, name + ".craft"))
    with open(s6cfg.MARK_FILE, "r", encoding="utf-8") as fh:
        mark = fh.read()
    sfs = os.path.join(out, "persistent.sfs")
    with open(sfs, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    if s6cfg.mark_facts(text)[1] != 0:
        raise ValueError("the fixture already holds a mark")
    with open(sfs, "w", encoding="utf-8", newline="") as fh:
        fh.write(insert_mark(text, mark))
    with open(sfs, "r", encoding="utf-8", newline="") as fh:
        facts = s6cfg.mark_facts(fh.read())
    if facts[1] != 1 or facts[3] != s6cfg.EXPECTED_MARK_HASH:
        raise ValueError("verification failed: marks=%d hash=%s" % (facts[1], facts[3]))
    return facts


def _inside(path, root):
    path = os.path.normcase(os.path.realpath(path))
    root = os.path.normcase(os.path.realpath(root))
    return path == root or path.startswith(root + os.sep)


def _selftest():
    failures = []
    sample = "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = A\n\t}\n\tSCENARIO\n\t{\n\t\tname = B\n\t\tX\n\t\t{\n\t\t}\n\t}\n\tROSTER\n\t{\n\t}\n}\n"
    with open(s6cfg.MARK_FILE, "r", encoding="utf-8") as fh:
        mark = fh.read()
    out = insert_mark(sample, mark)
    root = s6cfg.parse(out)
    names = s6cfg.scenario_names(root)
    if names != ["A", "B", s6cfg.MARK_NAME]:
        failures.append("scenario order %r" % names)
    if out.index("ParsekRaceSpikeMark") > out.index("ROSTER") or out.index("ParsekRaceSpikeMark") < out.index("name = B"):
        failures.append("mark not placed after the last SCENARIO")
    crlf = insert_mark(sample.replace("\n", "\r\n"), mark)
    if "\n" in crlf.replace("\r\n", ""):
        failures.append("CRLF file got LF lines")
    try:
        insert_mark("GAME\n{\n}\n", mark)
        failures.append("no-SCENARIO save accepted")
    except ValueError:
        pass
    if os.path.isdir(FIXTURE):
        with tempfile.TemporaryDirectory() as tmp:
            facts = build(os.path.join(tmp, "t"))
            if facts[1] != 1 or facts[3] != s6cfg.EXPECTED_MARK_HASH:
                failures.append("fixture build facts %r" % (facts,))
            for name in CRAFT:
                if not os.path.isfile(os.path.join(tmp, "t", "Ships", "VAB", name + ".craft")):
                    failures.append("craft missing: " + name)
    if failures:
        for f in failures:
            print("SELFTEST FAIL: " + f)
        return 1
    print("make_template selftest: PASS")
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--out")
    p.add_argument("--fixture", default=FIXTURE)
    p.add_argument("--selftest", action="store_true")
    a = p.parse_args(argv)
    if a.selftest:
        return _selftest()
    if not a.out:
        p.print_usage()
        return 2
    if _inside(a.out, REPO):
        print("refused: --out is inside the repository (%s); write under <umbrella>/logs/" % REPO)
        return 2
    try:
        facts = build(os.path.abspath(a.out), a.fixture)
    except (OSError, ValueError) as exc:
        print("make_template failed: %s" % exc)
        return 1
    print("template written: %s marks=%d hash=%s scenarios=%d" % (a.out, facts[1], facts[3], facts[5]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
