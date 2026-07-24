#!/usr/bin/env python3
"""Harvest a FORGE-produced save into a committed fixture.

A FIXTURE-FORGE run leaves a KSP save holding the state some scenario needs as
its START state, persisted as `persistent.sfs`. This tool copies that produced
save, PRUNES Parsek state (so the fixture carries zero prior recordings /
backups), NORMALIZES the title, and writes it under
`harness/fixtures/saves/<target-name>/`.

It is the headless replacement for the operator fixture flight (2026-07-22
operator-principle override): the automation forges its own state.

GENERIC over the three forges shipped so far -- the produced save's SITUATION is
not assumed anywhere in this tool:

  FORGE-bdock-station (forge_station)  --target-name bdock-station-pad
      the docking Kerbal X pre-placed on the LaunchPad (PRELAUNCH).
  FORGE-eva3-pad      (forge_station)  --target-name eva3-pad-3crew
      the same pad state with three named crew aboard.
  FORGE-eva2-lko      (forge_lko)      --target-name eva2-lko-crewed
      a CREWED orbital stage parked in a ~100 km circular Kerbin orbit
      (ORBITING, not PRELAUNCH).

The only pad-shaped thing left is the OPTIONAL sanity gate: pass
`--expect-situation ORBITING` (or any comma-separated set) to require that the
save's ACTIVE vessel is in one of those situations before the fixture is written.
Omitted, the gate is off and the behaviour is exactly what the two pad forges
have always had (activeVessel present + at least one VESSEL node). The active
vessel's name + situation are ALWAYS logged, so an operator sees what was stamped
even without the gate.

Usage:
    # After a forge run, the produced save is at
    #   <ksp-instance>/saves/bdock-forge-base/
    python harness/tools/harvest_bdock_station.py --save-dir <path-to-produced-save>
    # or point at the instance root + the run-save name:
    python harness/tools/harvest_bdock_station.py --instance <ksp-instance> \
        --run-save bdock-forge-base
    # the orbital forge, with the situation gate armed:
    python harness/tools/harvest_bdock_station.py --instance <ksp-instance> \
        --run-save bdock-forge-base --target-name eva2-lko-crewed \
        --expect-situation ORBITING

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


def flightstate_span(sfs_text: str) -> str:
    """The FLIGHTSTATE node's text, or the whole input when there is no FLIGHTSTATE.

    `activeVessel` is an INDEX into the VESSEL nodes of FLIGHTSTATE, so every scan
    that index resolves against must see exactly those nodes and in that order. A
    VESSEL node living anywhere else in the save (a SCENARIO node that embeds one -
    stock DiscoverableObjects and several mods do this, and Parsek's own scenario
    node sits immediately BEFORE FLIGHTSTATE) would otherwise be counted and would
    SHIFT every index by one, so `records[active]` would name the wrong vessel and
    the situation gate would pass or fail on the wrong craft.

    Anchoring is start + brace-walk: from the FLIGHTSTATE header to its matching
    close brace, falling back to end-of-text if the braces do not balance (a
    truncated save) and to the whole input if there is no FLIGHTSTATE header at all
    (so a bare VESSEL fragment still parses, which the unit tests rely on). Pure
    text: no ConfigNode parser, no KSP."""
    m = re.search(r"(?m)^\s*FLIGHTSTATE\s*$", sfs_text)
    if m is None:
        return sfs_text
    open_idx = sfs_text.find("{", m.end())
    if open_idx < 0:
        return sfs_text[m.end():]
    depth = 0
    for i in range(open_idx, len(sfs_text)):
        ch = sfs_text[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return sfs_text[open_idx:i + 1]
    return sfs_text[open_idx:]


def count_vessels(sfs_text: str) -> int:
    # VESSEL nodes are tab/space-indented inside FLIGHTSTATE -- and ONLY those count
    # (see flightstate_span: a VESSEL node elsewhere would shift the activeVessel index).
    return len(re.findall(r"(?m)^\s*VESSEL\s*$", flightstate_span(sfs_text)))


def read_active_vessel(sfs_text: str):
    m = re.search(r"(?m)^\s*activeVessel\s*=\s*(-?\d+)\s*$", sfs_text)
    return int(m.group(1)) if m else None


def read_vessel_records(sfs_text: str):
    """(name, situation) for every VESSEL node, in FLIGHTSTATE order (the order
    `activeVessel` indexes into).

    Scans the FLIGHTSTATE span only (`flightstate_span`), so a VESSEL node embedded
    in some other node cannot shift the index. Each VESSEL node opens with its own
    `name = ` / `sit = ` lines BEFORE any child PART nodes, so the FIRST match of
    each inside the node's span is the vessel's own. Missing keys read "" (never
    guessed). Pure text parsing: no ConfigNode parser, no KSP."""
    sfs_text = flightstate_span(sfs_text)
    starts = [m.start() for m in re.finditer(r"(?m)^\s*VESSEL\s*$", sfs_text)]
    records = []
    for i, start in enumerate(starts):
        end = starts[i + 1] if i + 1 < len(starts) else len(sfs_text)
        span = sfs_text[start:end]
        name = re.search(r"(?m)^\s*name\s*=\s*(.*)$", span)
        sit = re.search(r"(?m)^\s*sit\s*=\s*(.*)$", span)
        records.append((name.group(1).strip() if name else "",
                        sit.group(1).strip() if sit else ""))
    return records


def parse_expected_situations(value):
    """Normalize a `--expect-situation` argument (comma-separated, any case) to a
    tuple of UPPER tokens. None / "" -> () = the gate is OFF."""
    if not value:
        return ()
    return tuple(tok.strip().upper() for tok in str(value).split(",") if tok.strip())


def check_active_situation(records, active_index, expected):
    """The OPTIONAL situation gate. Returns (ok, message).

    ok is True when the gate is off (`expected` empty). Otherwise the active
    index must resolve to a VESSEL record whose situation is in `expected`. Fails
    CLOSED: an out-of-range index or an unreadable situation is NOT a pass, so an
    orbital forge that lost its stage can never stamp a fixture silently."""
    if not expected:
        return True, "situation gate off"
    if active_index is None or active_index < 0 or active_index >= len(records):
        return False, ("activeVessel index %s does not resolve to one of the %d "
                       "VESSEL nodes" % (active_index, len(records)))
    name, sit = records[active_index]
    if sit.upper() not in expected:
        return False, ("active vessel %r is %s, expected one of %s"
                       % (name, sit or "<unreadable>", ",".join(expected)))
    return True, "active vessel %r is %s" % (name, sit)


def harvest(save_dir: str, target_name: str, title: str, force: bool,
            expected_situations=()) -> int:
    if not os.path.isdir(save_dir):
        raise SystemExit("harvest: produced save dir not found: %s" % save_dir)
    src_sfs = os.path.join(save_dir, "persistent.sfs")
    if not os.path.isfile(src_sfs):
        raise SystemExit("harvest: %s has no persistent.sfs (did the forge run + SaveGame?)"
                         % save_dir)

    with open(src_sfs, "r", encoding="utf-8", errors="replace") as fh:
        sfs_text = fh.read()

    # Sanity 1 (all forges): the produced save must boot into flight on SOME
    # vessel -- LoadGame's IsLoadedGameFocusable gate rejects a save with no
    # active vessel. No situation is assumed here (pad AND orbital forges pass).
    active = read_active_vessel(sfs_text)
    vessels = count_vessels(sfs_text)
    records = read_vessel_records(sfs_text)
    active_name, active_sit = ("", "")
    if active is not None and 0 <= active < len(records):
        active_name, active_sit = records[active]
    log("produced save: activeVessel=%s vessels=%d active=%r situation=%s"
        % (active, vessels, active_name, active_sit or "<unreadable>"))
    if active is None or active < 0 or vessels < 1:
        msg = ("produced save is not focusable (activeVessel=%s vessels=%d): the "
               "forge run did not leave a usable vessel" % (active, vessels))
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")

    # Sanity 2 (OPTIONAL, off unless --expect-situation is passed): the active
    # vessel must be in one of the expected situations. This is what makes an
    # ORBITAL harvest honest -- a forge that flaked mid-ascent, or a save whose
    # focus landed on the spent core, would otherwise stamp a broken fixture.
    ok, detail = check_active_situation(records, active, expected_situations)
    if not ok:
        msg = "situation gate failed: %s" % detail
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")
    elif expected_situations:
        log("situation gate passed: %s" % detail)

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
        log("warning: the fixture has no Ships/VAB craft; any consumer that calls "
            "launch_vessel on it will fail to resolve <save>/Ships/VAB/<name>.craft "
            "(harmless for a fixture whose scenario never launches anything)")

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
        description="Harvest a FORGE-produced save (pad OR orbital) into a "
                    "committed fixture: prune Parsek state, normalize the title, "
                    "write to harness/fixtures/saves/<target-name>.")
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
                        "eva3-pad-3crew for the EVA-3 3-crew pad fixture, "
                        "eva2-lko-crewed for the EVA-2 crewed-LKO fixture)")
    p.add_argument("--expect-situation", default=None,
                   help="comma-separated situations the produced save's ACTIVE "
                        "vessel must be in (e.g. ORBITING for an orbital forge, "
                        "PRELAUNCH for a pad forge). Omitted = gate off")
    p.add_argument("--title", default=None,
                   help="the fixture title (default: the target-name)")
    p.add_argument("--force", action="store_true",
                   help="write the fixture even if the produced save is not "
                        "focusable or fails the situation gate (diagnostics only)")
    return p


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    save_dir = resolve_save_dir(args)
    title = args.title or args.target_name
    return harvest(save_dir, args.target_name, title, args.force,
                   parse_expected_situations(args.expect_situation))


if __name__ == "__main__":
    sys.exit(main())
