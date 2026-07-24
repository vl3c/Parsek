#!/usr/bin/env python3
"""Harvest the FORGE-produced pad save into the committed B-DOCK Station fixture.

The FIXTURE-FORGE run (`FORGE-bdock-station.toml` driving `forge_station.py`)
leaves a KSP save with the docking Kerbal X pre-placed on the LaunchPad
(PRELAUNCH), persisted as `persistent.sfs`. This tool copies that produced save,
PRUNES Parsek state (so the fixture carries zero prior recordings / backups),
NORMALIZES the title, and writes it to
`harness/fixtures/saves/bdock-station-pad/` -- the committed B2-shape fixture that
`BDOCK-1-station-interceptor.toml` consumes (a pre-placed Station + the
`Ships/VAB/Kerbal X.craft` the Interceptor's launch_vessel re-launches).

It is the headless replacement for the operator fixture flight (2026-07-22
operator-principle override): the automation forges its own state.

GENERIC: the same tool harvests the EVA-3 pad fixture (the same forge with three
named crew) by passing `--target-name eva3-pad-3crew` (the name EVA-3-multi-kerbal's
saveTemplate references).

Usage:
    # After a forge run, the produced save is at
    #   <ksp-instance>/saves/bdock-forge-base/
    python harness/tools/harvest_bdock_station.py --save-dir <path-to-produced-save>
    # or point at the instance root + the run-save name:
    python harness/tools/harvest_bdock_station.py --instance <ksp-instance> \
        --run-save bdock-forge-base

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)                       # harness/
_FIXTURES_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

# Files copied verbatim from the produced save (the bootable pad state + the craft
# the Interceptor re-launches). Everything else (quicksaves, Parsek state,
# pre-Parsek backups) is deliberately excluded.
_KEEP_FILES = ("persistent.sfs", "persistent.loadmeta")
# Subdirectories copied verbatim (Ships/VAB carries the Kerbal X.craft; AddOns is
# the stock scaffolding the sibling fixtures also commit).
_KEEP_DIRS = ("Ships", "AddOns")
# Directories that must NEVER end up in the fixture (Parsek recordings / rewind
# points / journals, and pre-Parsek safety backups the mod drops).
_PRUNE_DIR_NAMES = ("Parsek",)
_PRUNE_DIR_PREFIXES = (".parsek-backup", ".parsek-backup-staging")


def log(msg: str) -> None:
    sys.stdout.write("[Harvest] %s\n" % msg)


def resolve_save_dir(args) -> str:
    if args.save_dir:
        return os.path.abspath(args.save_dir)
    if args.instance:
        return os.path.abspath(os.path.join(args.instance, "saves", args.run_save))
    raise SystemExit("harvest: pass --save-dir <path> OR --instance <dir> [--run-save NAME]")


def normalize_title(sfs_text: str, title: str) -> str:
    """Rewrite the GAME node's `Title = ...` line to `<title> (SANDBOX)` (the
    committed-fixture title convention, mirroring b2-lko-craft / bdock-station-craft).
    Pure text substitution on the first Title line; leaves the file otherwise
    byte-identical. Returns the new text."""
    want = "%s (SANDBOX)" % title
    # Match a leading-whitespace `Title = <anything>` line (the GAME node's title).
    pattern = re.compile(r"^(\s*Title\s*=\s*).*$", re.MULTILINE)
    if pattern.search(sfs_text):
        return pattern.sub(lambda m: m.group(1) + want, sfs_text, count=1)
    log("warning: no Title line found in persistent.sfs; leaving title unchanged")
    return sfs_text


def count_vessels(sfs_text: str) -> int:
    # VESSEL nodes are tab/space-indented inside FLIGHTSTATE.
    return len(re.findall(r"(?m)^\s*VESSEL\s*$", sfs_text))


def read_active_vessel(sfs_text: str):
    m = re.search(r"(?m)^\s*activeVessel\s*=\s*(-?\d+)\s*$", sfs_text)
    return int(m.group(1)) if m else None


def harvest(save_dir: str, target_name: str, title: str, force: bool) -> int:
    if not os.path.isdir(save_dir):
        raise SystemExit("harvest: produced save dir not found: %s" % save_dir)
    src_sfs = os.path.join(save_dir, "persistent.sfs")
    if not os.path.isfile(src_sfs):
        raise SystemExit("harvest: %s has no persistent.sfs (did the forge run + SaveGame?)"
                         % save_dir)

    with open(src_sfs, "r", encoding="utf-8", errors="replace") as fh:
        sfs_text = fh.read()

    # Sanity: the produced save must boot into flight on the pre-placed Station.
    active = read_active_vessel(sfs_text)
    vessels = count_vessels(sfs_text)
    log("produced save: activeVessel=%s vessels=%d" % (active, vessels))
    if active is None or active < 0 or vessels < 1:
        msg = ("produced save is not focusable (activeVessel=%s vessels=%d): the "
               "forge run did not leave a Station on the pad" % (active, vessels))
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")

    target = os.path.join(_FIXTURES_SAVES, target_name)
    if os.path.isdir(target):
        log("removing existing fixture %s" % target)
        shutil.rmtree(target)
    os.makedirs(target)

    # 1) persistent.sfs with the normalized title.
    normalized = normalize_title(sfs_text, title)
    with open(os.path.join(target, "persistent.sfs"), "w", encoding="utf-8",
              newline="\n") as fh:
        fh.write(normalized)
    log("wrote persistent.sfs (title -> %s (SANDBOX))" % title)

    # 2) other kept files (loadmeta) verbatim.
    for name in _KEEP_FILES:
        if name == "persistent.sfs":
            continue
        src = os.path.join(save_dir, name)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(target, name))
            log("copied %s" % name)

    # 3) kept directories (Ships/VAB craft + AddOns), pruning Parsek/backup dirs.
    for dname in _KEEP_DIRS:
        src = os.path.join(save_dir, dname)
        if os.path.isdir(src):
            shutil.copytree(src, os.path.join(target, dname),
                            ignore=_ignore_pruned)
            log("copied %s/" % dname)

    # 4) belt-and-braces prune of any Parsek/backup dir that slipped in (e.g. at
    # the save root, not under a kept dir).
    pruned = _prune_state(target)
    if pruned:
        log("pruned Parsek/backup dirs: %s" % ", ".join(pruned))

    craft = os.path.join(target, "Ships", "VAB")
    craft_files = sorted(os.listdir(craft)) if os.path.isdir(craft) else []
    log("fixture Ships/VAB: %s" % (craft_files or "<none>"))
    if not craft_files:
        log("warning: the fixture has no Ships/VAB craft; the Interceptor's "
            "launch_vessel will fail to resolve <save>/Ships/VAB/<name>.craft")

    log("harvested -> %s" % target)
    return 0


def _ignore_pruned(directory, names):
    ignored = []
    for n in names:
        if n in _PRUNE_DIR_NAMES or any(n.startswith(p) for p in _PRUNE_DIR_PREFIXES):
            ignored.append(n)
    return set(ignored)


def _prune_state(root: str):
    pruned = []
    for dirpath, dirnames, _files in os.walk(root):
        for d in list(dirnames):
            if d in _PRUNE_DIR_NAMES or any(d.startswith(p) for p in _PRUNE_DIR_PREFIXES):
                full = os.path.join(dirpath, d)
                shutil.rmtree(full, ignore_errors=True)
                dirnames.remove(d)
                pruned.append(os.path.relpath(full, root))
    return pruned


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="harvest_bdock_station",
        description="Harvest a FORGE-produced pad save into the committed B-DOCK "
                    "Station fixture (prune Parsek state, normalize, write to "
                    "harness/fixtures/saves/<target-name>).")
    p.add_argument("--save-dir", default=None,
                   help="path to the FORGE-produced save directory "
                        "(<ksp-instance>/saves/bdock-forge-base)")
    p.add_argument("--instance", default=None,
                   help="KSP instance root (alternative to --save-dir; joined with "
                        "saves/<run-save>)")
    p.add_argument("--run-save", default="bdock-forge-base",
                   help="the forge run-save name under <instance>/saves (default: "
                        "bdock-forge-base)")
    p.add_argument("--target-name", default="bdock-station-pad",
                   help="the committed fixture directory name under "
                        "harness/fixtures/saves (default: bdock-station-pad; use "
                        "eva3-pad-3crew for the EVA-3 3-crew pad fixture)")
    p.add_argument("--title", default=None,
                   help="the fixture title (default: the target-name)")
    p.add_argument("--force", action="store_true",
                   help="write the fixture even if the produced save is not "
                        "focusable (diagnostics only)")
    return p


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    save_dir = resolve_save_dir(args)
    title = args.title or args.target_name
    return harvest(save_dir, args.target_name, title, args.force)


if __name__ == "__main__":
    sys.exit(main())
