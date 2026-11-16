#!/usr/bin/env python3
"""Read-only checks for the R0 spike instances (spike branch only, task S0).

Every subcommand only READS. Nothing here creates, copies, links or deletes a
file, so it is safe to run against the dev install. Exit code 0 = every check
passed, 1 = a check failed, 2 = bad arguments or unreadable input.

    python spikes/r0/s0_check.py dll <Parsek.dll> [--expect-absent] [--utf8 ClassName ...]
    python spikes/r0/s0_check.py tree <dir>
    python spikes/r0/s0_check.py links <dir> [--expect REL ...] [--expect-none]
    python spikes/r0/s0_check.py log <KSP.log> --session unarmed|armed|dlc|nodlc
    python spikes/r0/s0_check.py --selftest

Links (junctions, symlinks) are found from lstat alone and never descended into,
the same rule as harness/provision/provision.py `_find_reparse_points`, so a
junction to the dev install's GameData/Squad or GameData/SquadExpansion is
reported, not walked.
"""

import argparse
import hashlib
import os
import stat
import sys
import tempfile

BUILD_MARKER = "PARSEK-SPIKE-R0"
UTF8_IDENTIFIERS = ("SpikeHost", "SpikeCsv")
INFO_STRINGS = ("com.parsek.spike",)

SPIKE_PREFIX = "[Spike"
SESSION_START = "[Parsek][INFO][Init] SessionStart runUtc="
TO_SPACE_CENTER = "Scene Change : From MAINMENU to SPACECENTER"
ARMED_S0 = "[Parsek][INFO][Spike] armed: S0 build=PARSEK-SPIKE-R0"
S0_NO_PATCHES = "[Parsek][INFO][Spike] spike S0: no patches to apply"
SPIKE_WARN = "[Parsek][WARN][Spike"
NO_EXPANSIONS = "No Expansions detected"
EXPANSION_FOUND_HEAD = "Expansion "
EXPANSION_FOUND_TAIL = " detected in path "


def is_link(path):
    """True when ``path`` ITSELF is a symlink or a Windows reparse point."""
    try:
        st = os.lstat(path)
    except OSError:
        return False
    if stat.S_ISLNK(st.st_mode) or getattr(st, "st_reparse_tag", 0):
        return True
    isjunction = getattr(os.path, "isjunction", None)
    return bool(isjunction and isjunction(path))


def link_target(path):
    try:
        return os.readlink(path)
    except (OSError, ValueError, NotImplementedError):
        return "<unreadable>"


def find_links(root):
    """Every link under ``root`` (relative, forward slashes), never descending into one."""
    found = []
    stack = [root]
    while stack:
        cur = stack.pop()
        try:
            entries = list(os.scandir(cur))
        except OSError:
            continue
        for entry in entries:
            if is_link(entry.path):
                found.append(entry.path)
            elif entry.is_dir(follow_symlinks=False):
                stack.append(entry.path)
    return sorted(os.path.relpath(p, root).replace("\\", "/") for p in found)


def tree_stats(root):
    """(files, bytes, links) of the real files under ``root``, links not followed."""
    files = total = links = 0
    stack = [root]
    while stack:
        cur = stack.pop()
        for entry in os.scandir(cur):
            if is_link(entry.path):
                links += 1
            elif entry.is_dir(follow_symlinks=False):
                stack.append(entry.path)
            elif entry.is_file(follow_symlinks=False):
                files += 1
                total += entry.stat(follow_symlinks=False).st_size
    return files, total, links


def dll_report(data, extra_utf8=()):
    counts = {}
    for s in (BUILD_MARKER,) + UTF8_IDENTIFIERS + tuple(extra_utf8) + INFO_STRINGS:
        counts[s] = (data.count(s.encode("utf-8")), data.count(s.encode("utf-16-le")))
    return counts


def check_dll(data, expect_absent, extra_utf8=()):
    """List of failure strings (empty = pass). ``extra_utf8``: a spike's own class
    names, which must be present in the metadata (UTF-8)."""
    counts = dll_report(data, extra_utf8)
    failures = []
    if expect_absent:
        u8, u16 = counts[BUILD_MARKER]
        if u8 or u16:
            failures.append("%s present (utf8=%d utf16=%d) in a DLL that must not be the spike build"
                            % (BUILD_MARKER, u8, u16))
        return failures
    if counts[BUILD_MARKER][1] == 0:
        failures.append("%s missing in UTF-16" % BUILD_MARKER)
    for s in UTF8_IDENTIFIERS + tuple(extra_utf8):
        if counts[s][0] == 0:
            failures.append("%s missing in UTF-8" % s)
    return failures


def check_log(lines, session):
    """(failures, evidence lines) for one proof session."""
    spike = [l for l in lines if SPIKE_PREFIX in l]
    starts = [l for l in lines if SESSION_START in l]
    ksc = [l for l in lines if TO_SPACE_CENTER in l]
    found = [l for l in lines if EXPANSION_FOUND_HEAD in l and EXPANSION_FOUND_TAIL in l]
    none = [l for l in lines if NO_EXPANSIONS in l]
    failures, evidence = [], []
    if session in ("unarmed", "armed", "nodlc", "dlc"):
        if len(starts) != 1:
            failures.append("expected exactly one Parsek SessionStart line (one KSP session per log), got %d"
                            % len(starts))
        evidence += starts[:1]
    if session == "unarmed":
        if spike:
            failures.append("%d line(s) contain %r with PARSEK_SPIKE unset" % (len(spike), SPIKE_PREFIX))
            evidence += spike[:5]
        if not ksc:
            failures.append("no %r line: the career save never reached the Space Center" % TO_SPACE_CENTER)
        evidence += ksc[:1]
    elif session == "armed":
        armed = [l for l in lines if ARMED_S0 in l]
        if len(armed) != 1:
            failures.append("expected exactly one armed line %r, got %d" % (ARMED_S0, len(armed)))
        if not any(S0_NO_PATCHES in l for l in lines):
            failures.append("no %r line" % S0_NO_PATCHES)
        warns = [l for l in lines if SPIKE_WARN in l]
        if warns:
            failures.append("%d spike WARN line(s) (unknown id or failed patch)" % len(warns))
        evidence += armed + warns[:5]
    elif session == "dlc":
        if not found:
            failures.append("no %r...%r line: this instance does not see the expansions"
                            % (EXPANSION_FOUND_HEAD, EXPANSION_FOUND_TAIL))
        if none:
            failures.append("%r present in an instance that should have the DLCs" % NO_EXPANSIONS)
        evidence += found
    elif session == "nodlc":
        if not none:
            failures.append("no %r line" % NO_EXPANSIONS)
        if found:
            failures.append("%d expansion(s) still detected" % len(found))
        evidence += none[:1] + found
    else:
        failures.append("unknown session %r" % session)
    return failures, evidence


def _read_lines(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    return raw.decode("utf-8", errors="replace").splitlines()


def cmd_dll(args):
    with open(args.path, "rb") as fh:
        data = fh.read()
    print("path=%s" % os.path.abspath(args.path))
    print("size=%d sha256=%s" % (len(data), hashlib.sha256(data).hexdigest()))
    for s, (u8, u16) in dll_report(data, args.utf8).items():
        print("%s utf8=%d utf16=%d" % (s, u8, u16))
    failures = check_dll(data, args.expect_absent, args.utf8)
    return _verdict(failures)


def cmd_tree(args):
    if not os.path.isdir(args.path) or is_link(args.path):
        print("FAIL: %s is not a real directory" % args.path)
        return 1
    files, total, links = tree_stats(args.path)
    print("path=%s files=%d bytes=%d links=%d" % (os.path.abspath(args.path), files, total, links))
    return 0


def cmd_links(args):
    if not os.path.isdir(args.path):
        print("FAIL: %s is not a directory" % args.path)
        return 1
    found = find_links(args.path)
    for rel in found:
        print("link %s -> %s" % (rel, link_target(os.path.join(args.path, rel))))
    print("links=%d" % len(found))
    failures = []
    if args.expect_none and found:
        failures.append("expected no links, found %d" % len(found))
    if args.expect is not None and sorted(args.expect) != found:
        failures.append("expected exactly %s, found %s" % (sorted(args.expect), found))
    return _verdict(failures)


def cmd_log(args):
    lines = _read_lines(args.path)
    failures, evidence = check_log(lines, args.session)
    print("log=%s lines=%d session=%s" % (os.path.abspath(args.path), len(lines), args.session))
    for l in evidence:
        print("  " + l.strip())
    return _verdict(failures)


def _verdict(failures):
    for f in failures:
        print("FAIL: " + f)
    print("RESULT: %s" % ("PASS" if not failures else "FAIL"))
    return 0 if not failures else 1


def selftest():
    problems = []
    marker = BUILD_MARKER.encode("utf-16-le") + b"SpikeHost\0SpikeCsv"
    if check_dll(marker, False):
        problems.append("dll check rejects a marked DLL")
    if not check_dll(b"ParsekFlight", False):
        problems.append("dll check accepts an unmarked DLL")
    if not check_dll(marker, False, ["SpikeS4EventOrder"]) or check_dll(marker + b"SpikeS4EventOrder", False, ["SpikeS4EventOrder"]):
        problems.append("--utf8 class check misread")
    if check_dll(b"ParsekFlight", True) or not check_dll(marker, True):
        problems.append("--expect-absent inverted")
    ok_unarmed = [SESSION_START + "1", "x " + TO_SPACE_CENTER]
    if check_log(ok_unarmed, "unarmed")[0]:
        problems.append("clean unarmed log rejected")
    if not check_log(ok_unarmed + ["[Parsek][INFO][Spike.S4a] x"], "unarmed")[0]:
        problems.append("unarmed log with a spike line accepted")
    ok_armed = [SESSION_START + "1", "[LOG 1]: " + ARMED_S0, "[LOG 1]: " + S0_NO_PATCHES]
    if check_log(ok_armed, "armed")[0]:
        problems.append("clean armed log rejected")
    if not check_log(ok_armed + ["[Parsek][WARN][Spike] armed id has no registration: S9"], "armed")[0]:
        problems.append("armed log with a spike WARN accepted")
    if check_log([SESSION_START + "1", NO_EXPANSIONS], "nodlc")[0]:
        problems.append("clean nodlc log rejected")
    dlc = [SESSION_START + "1", "Expansion MakingHistory detected in path x"]
    if not check_log(dlc, "nodlc")[0] or check_log(dlc, "dlc")[0]:
        problems.append("dlc / nodlc logs misread")
    with tempfile.TemporaryDirectory() as tmp:
        target = os.path.join(tmp, "target")
        os.makedirs(os.path.join(target, "deep"))
        with open(os.path.join(target, "deep", "f.bin"), "wb") as fh:
            fh.write(b"12345")
        inst = os.path.join(tmp, "inst", "GameData")
        os.makedirs(inst)
        with open(os.path.join(inst, "a.txt"), "wb") as fh:
            fh.write(b"ab")
        try:
            os.symlink(target, os.path.join(inst, "Squad"), target_is_directory=True)
        except (OSError, NotImplementedError):
            print("selftest: cannot create a symlink here; link cells skipped")
        else:
            root = os.path.join(tmp, "inst")
            if find_links(root) != ["GameData/Squad"]:
                problems.append("find_links: %r" % find_links(root))
            if tree_stats(root) != (1, 2, 1):
                problems.append("tree_stats followed a link: %r" % (tree_stats(root),))
    for p in problems:
        print("SELFTEST FAIL: " + p)
    print("SELFTEST %s" % ("PASS" if not problems else "FAIL"))
    return 0 if not problems else 1


def main(argv=None):
    if argv is None:
        argv = sys.argv[1:]
    if argv == ["--selftest"]:
        return selftest()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("dll")
    p.add_argument("path")
    p.add_argument("--expect-absent", action="store_true",
                   help="pass only when the build marker is ABSENT (the dev install's DLL)")
    p.add_argument("--utf8", action="append", default=[], metavar="NAME",
                   help="a spike class name that must be in the DLL metadata (repeatable)")
    p = sub.add_parser("tree")
    p.add_argument("path")
    p = sub.add_parser("links")
    p.add_argument("path")
    p.add_argument("--expect", nargs="*", default=None, metavar="REL",
                   help="the exact set of link paths, relative to PATH, forward slashes")
    p.add_argument("--expect-none", action="store_true")
    p = sub.add_parser("log")
    p.add_argument("path")
    p.add_argument("--session", required=True, choices=["unarmed", "armed", "dlc", "nodlc"])
    args = ap.parse_args(argv)
    try:
        return {"dll": cmd_dll, "tree": cmd_tree, "links": cmd_links, "log": cmd_log}[args.cmd](args)
    except OSError as exc:
        print("ERROR: %s" % exc)
        return 2


if __name__ == "__main__":
    sys.exit(main())
