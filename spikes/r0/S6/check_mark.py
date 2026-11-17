#!/usr/bin/env python3
"""Race mark checker for spike S6 (spike branch only, plan r0-spikes.md task S6 (b)).

Diffs the race mark node in every written save against the template's mark. Read-only.
Exit code 0 = every check passed, 1 = a check failed, 2 = bad arguments or unreadable input.

    python spikes/r0/S6/check_mark.py tree <dir> [<dir> ...]
        Every *.sfs under each dir (a save folder, a results harvest, or the spike's
        parsek-spike/S6/snapshots/ copies, whose names end in .sfs too). A file with a GAME
        node must hold exactly one mark whose canonical form equals the template's. Files
        without a GAME node are listed and not judged.
    python spikes/r0/S6/check_mark.py log <KSP.log>
        Reads the [Spike.S6] lines: every savewatch line of a GAME save must carry marks=1
        and the expected hash; prints the earliest-hook lines, the hook order per cycle,
        the menu, dialog, difficulty, create, resume and content lines, and the FAIL ones.
    python spikes/r0/S6/check_mark.py diff <a.sfs> <b.sfs>
        Prints both canonical marks and their line diff.
    python spikes/r0/S6/check_mark.py --selftest

--expect <mark.cfg> names another template mark (default spikes/r0/S6/template/mark.cfg).
Stdlib only; ASCII only.
"""

import argparse
import difflib
import os
import re
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.dont_write_bytecode = True  # no __pycache__ beside the tools in the repo tree
import s6cfg  # noqa: E402

SAVEWATCH_RE = re.compile(
    r"\[Spike\.S6\] savewatch seq=(\d+) trigger=(\S+) file=(.*?) bytes=(\d+) game=([01]) "
    r"marks=(\d+) hash=(\S+) probes=(\d+) scenarios=(\d+)")
HOOK_RE = re.compile(r"\[Spike\.S6\] hook seq=(\d+) cycle=(\d+) name=(\S+) scene=(\S+) .*? mark=(-?\d+)")
S6_LINE_RE = re.compile(r"\[Spike\.S6\] (.*)$")
INTEREST = ("earliest ", "cycle begin", "menu ", "dialog ", "difficulty accepted=", "difficulty check",
            "difficulty shown", "difficulty open", "create ", "resume", "bodies ", "content ", "rnd-tree ",
            "sc-arrival", "probe OnLoad", "proto-load", "auto:", "patch summary", "options ")


def judge(text, expect_hash):
    """('OK'|'NOT-GAME'|'MISSING'|'DUPLICATE'|'CHANGED', facts)."""
    facts = s6cfg.mark_facts(text)
    is_game, marks, _canon, h, _probes, _sc = facts
    if not is_game:
        return "NOT-GAME", facts
    if marks == 0:
        return "MISSING", facts
    if marks > 1:
        return "DUPLICATE", facts
    if h != expect_hash:
        return "CHANGED", facts
    return "OK", facts


def walk_sfs(dirs):
    for d in dirs:
        if os.path.isfile(d):
            yield d
            continue
        for root, _dirs, files in os.walk(d):
            for f in sorted(files):
                if f.lower().endswith(".sfs"):
                    yield os.path.join(root, f)


def cmd_tree(dirs, expect_canon, expect_hash, out=sys.stdout):
    bad = 0
    total = 0
    for path in walk_sfs(dirs):
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                text = fh.read()
        except OSError as exc:
            out.write("UNREADABLE %s (%s)\n" % (path, exc))
            bad += 1
            continue
        total += 1
        status, facts = judge(text, expect_hash)
        out.write("%-9s marks=%d hash=%s probes=%d scenarios=%d %s\n"
                  % (status, facts[1], facts[3], facts[4], facts[5], path))
        if status in ("MISSING", "DUPLICATE", "CHANGED"):
            bad += 1
            if status == "CHANGED":
                for line in difflib.unified_diff(expect_canon.split("\n"), facts[2].split("\n"),
                                                 "template", path, lineterm=""):
                    out.write("    " + line + "\n")
    out.write("tree: files=%d failed=%d expectHash=%s result=%s\n"
              % (total, bad, expect_hash, "PASS" if bad == 0 and total > 0 else "FAIL"))
    return 0 if bad == 0 and total > 0 else 1


def cmd_log(path, expect_hash, out=sys.stdout):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            lines = fh.read().splitlines()
    except OSError as exc:
        out.write("unreadable log %s: %s\n" % (path, exc))
        return 2
    saves = 0
    bad = 0
    files = {}
    cycles = {}
    others = []
    fails = []
    for line in lines:
        m = SAVEWATCH_RE.search(line)
        if m:
            saves += 1
            seq, trig, f, _b, game, marks, h = m.groups()[:7]
            files.setdefault(f, []).append((int(seq), trig, game, int(marks), h))
            if game == "1" and (marks != "1" or h != expect_hash):
                bad += 1
                fails.append("savewatch seq=%s file=%s marks=%s hash=%s" % (seq, f, marks, h))
            continue
        m = HOOK_RE.search(line)
        if m:
            _seq, cyc, name, scene, mark = m.groups()
            cycles.setdefault(int(cyc), []).append("%s@%s:%s" % (name, scene, mark))
            continue
        m = S6_LINE_RE.search(line)
        if m:
            body = m.group(1)
            if any(body.startswith(p) for p in INTEREST):
                others.append(body)
            if "result=FAIL" in body or "[WARN][Spike.S6]" in line:
                fails.append(body)
    out.write("== save writes (file: seq/trigger/game/marks/hash)\n")
    for f in sorted(files):
        rows = files[f]
        out.write("%s\n" % f)
        for seq, trig, game, marks, h in rows:
            out.write("    %4d %-9s game=%s marks=%d hash=%s%s\n"
                      % (seq, trig, game, marks, h,
                         "" if game == "0" or (marks == 1 and h == expect_hash) else "   <-- FAIL"))
    out.write("== hook order per cycle (hook@scene:marksInCurrentGame)\n")
    for c in sorted(cycles):
        out.write("cycle %d: %s\n" % (c, " ".join(cycles[c])))
    out.write("== other S6 lines\n")
    for body in others:
        out.write("  " + body + "\n")
    out.write("== failures\n")
    for f in fails:
        out.write("  " + f + "\n")
    ok = saves > 0 and bad == 0
    out.write("log: savewatchLines=%d badGameSaves=%d otherFailures=%d result=%s\n"
              % (saves, bad, len(fails) - bad, "PASS" if ok else "FAIL"))
    return 0 if ok else 1


def cmd_diff(a, b, out=sys.stdout):
    canons = []
    for p in (a, b):
        with open(p, "r", encoding="utf-8", errors="replace") as fh:
            facts = s6cfg.mark_facts(fh.read())
        out.write("%s: game=%s marks=%d hash=%s\n" % (p, facts[0], facts[1], facts[3]))
        canons.append(facts[2] or "")
    for line in difflib.unified_diff(canons[0].split("\n"), canons[1].split("\n"), a, b, lineterm=""):
        out.write(line + "\n")
    return 0 if canons[0] == canons[1] and canons[0] else 1


# --------------------------------------------------------------------------- selftest

SFS_HEAD = "GAME\n{\n\tversion = 1.12.5\n\tMode = CAREER\n\tSCENARIO\n\t{\n\t\tname = ResearchAndDevelopment\n\t\tscene = 7, 8, 5\n\t}\n"
SFS_TAIL = "\tFLIGHTSTATE\n\t{\n\t\tUT = 1\n\t}\n}\n"


def _selftest():
    import io
    failures = []

    def check(cond, what):
        if not cond:
            failures.append(what)

    canon, h = s6cfg.expected_mark()
    check(h == s6cfg.EXPECTED_MARK_HASH, "template hash %s != pinned %s" % (h, s6cfg.EXPECTED_MARK_HASH))
    check(canon == "SCENARIO {\nname = ParsekRaceSpikeMark\nscene = 5,7,8\nrace {\nid = spike-1\nslot = 2\n}\n}",
          "canonical form moved: %r" % canon)
    with open(s6cfg.MARK_FILE, "r", encoding="utf-8") as fh:
        mark = fh.read()
    # Whitespace normalisation: KSP may write "5,7,8" or tabs; same hash.
    spaced = mark.replace("5, 7, 8", "5,7,8").replace("\tslot = 2", "\tslot\t=   2")
    check(s6cfg.mark_facts(SFS_HEAD + spaced + SFS_TAIL)[3] == h, "normalised mark hashed differently")
    check(s6cfg.parse("A { b = 1 }\n").children("A")[0].get("b") == "1", "one-line node not parsed")
    check(s6cfg.normalize_value("  5 ,  7,8 ") == "5,7,8", "normalize_value")

    with tempfile.TemporaryDirectory() as tmp:
        good = os.path.join(tmp, "good")
        os.makedirs(os.path.join(good, "Parsek", "RewindPoints"))
        for rel in ("persistent.sfs", "quicksave.sfs", os.path.join("Parsek", "RewindPoints", "rp_x.sfs")):
            with open(os.path.join(good, rel), "w") as fh:
                fh.write(SFS_HEAD + mark + SFS_TAIL)
        with open(os.path.join(good, "Parsek", "notgame.sfs"), "w") as fh:
            fh.write("PARSEK\n{\n}\n")
        buf = io.StringIO()
        check(cmd_tree([good], canon, h, buf) == 0, "good tree failed:\n" + buf.getvalue())
        check("NOT-GAME" in buf.getvalue(), "non-GAME file not listed")

        for name, body in (("missing", SFS_HEAD + SFS_TAIL),
                           ("dup", SFS_HEAD + mark + mark + SFS_TAIL),
                           ("changed", SFS_HEAD + mark.replace("slot = 2", "slot = 3") + SFS_TAIL)):
            d = os.path.join(tmp, name)
            os.makedirs(d)
            with open(os.path.join(d, "persistent.sfs"), "w") as fh:
                fh.write(body)
            buf = io.StringIO()
            check(cmd_tree([d], canon, h, buf) == 1, "%s tree passed" % name)
        buf = io.StringIO()
        check(cmd_tree([os.path.join(tmp, "empty-none")], canon, h, buf) == 1, "empty tree passed")

        log = os.path.join(tmp, "KSP.log")
        good_line = ("[LOG 00:00:01.000] [Parsek][INFO][Spike.S6] savewatch seq=1 trigger=initial "
                     "file=s/persistent.sfs bytes=10 game=1 marks=1 hash=%s probes=0 scenarios=24 snapshot=-\n" % h)
        hook = ("[LOG 00:00:02.000] [Parsek][INFO][Spike.S6] hook seq=1 cycle=1 name=onGameStateCreated "
                "scene=MAINMENU arg= game=1 cycleGame=1 folder=s mark=1 probeProto=0 probeLoaded=-1 nodeMarks=-1\n")
        with open(log, "w") as fh:
            fh.write(good_line + hook + "[LOG] [Parsek][INFO][Spike.S6] earliest cycle=1 source=create hook=x\n")
        buf = io.StringIO()
        check(cmd_log(log, h, buf) == 0, "good log failed:\n" + buf.getvalue())
        check("cycle 1: onGameStateCreated@MAINMENU:1" in buf.getvalue(), "hook order not printed")
        with open(log, "a") as fh:
            fh.write(good_line.replace("seq=1", "seq=2").replace("marks=1 hash=%s" % h, "marks=0 hash=-"))
        buf = io.StringIO()
        check(cmd_log(log, h, buf) == 1, "log with a markless GAME save passed")
        a = os.path.join(good, "persistent.sfs")
        b = os.path.join(tmp, "changed", "persistent.sfs")
        buf = io.StringIO()
        check(cmd_diff(a, a, buf) == 0 and cmd_diff(a, b, io.StringIO()) == 1, "diff exit codes")

    if failures:
        for f in failures:
            print("SELFTEST FAIL: " + f)
        return 1
    print("check_mark selftest: PASS (hash %s)" % h)
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--expect", default=s6cfg.MARK_FILE)
    p.add_argument("mode", nargs="?", choices=("tree", "log", "diff"))
    p.add_argument("paths", nargs="*")
    a = p.parse_args(argv)
    if a.selftest:
        return _selftest()
    if not a.mode or not a.paths:
        p.print_usage()
        return 2
    try:
        canon, h = s6cfg.expected_mark(a.expect)
    except (OSError, ValueError) as exc:
        print("cannot read the expected mark: %s" % exc)
        return 2
    if a.mode == "tree":
        return cmd_tree(a.paths, canon, h)
    if a.mode == "log":
        return cmd_log(a.paths[0], h)
    if len(a.paths) != 2:
        print("diff takes two files")
        return 2
    return cmd_diff(a.paths[0], a.paths[1])


if __name__ == "__main__":
    sys.exit(main())
