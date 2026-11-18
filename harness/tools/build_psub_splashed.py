#!/usr/bin/env python3
"""Build the `psub-splashed` fixture BY CONSTRUCTION (FORGE-psub-pad's harvest, moved into the sea).

WHY THIS FIXTURE EXISTS. The SUB lanes (todo SUBMARINE-TEST-KIT) need the stock submarine
PSUB-1 (build_psub_craft.py) FLOATING in Kerbin's sea, to record it diving. A splashdown
cannot deliver it: a 5.75 t vessel under parachutes hits the water far too fast, and a
powered boat or hop is a whole new flight profile. But KSP places a LANDED / SPLASHED
vessel from its saved lat / lon / alt when the save loads (it is how Parsek's own end
spawns place one: they write the canonical surface ORBIT, which carries no position at
all, `VesselSnapshotOps.WriteCanonicalSurfaceOrbitValues`). So the one thing that needs
KSP is the craft's load (assumption C1 of build_psub_craft.py), which FORGE-psub-pad
does on the pad, and this builder moves the vessel node into the sea.

THE SOURCE is the harvested pad save, `fixtures/saves/psub-pad` by default (written by
`harvest_bdock_station.py --target-name psub-pad --expect-situation PRELAUNCH` after the
forge flight), or any save directory passed as `--source`.

WHAT IS COPIED AND WHAT IS DERIVED.
  COPIED VERBATIM from the source: `persistent.loadmeta` and `AddOns/`.
  NOT COPIED: `Parsek/` (whatever the forge recorded) and `Ships/` (no SUB lane launches
  PSUB-1 again: a second launch would carry the same craft-baked persistentId; the
  observer craft SUB-2 rolls out is a library craft, `fixtures/shared-ships.toml`).
  EDITED IN `persistent.sfs`:
    - the `SCENARIO{name=ParsekScenario}` node keeps its `name`, `scene` and
      `gameStateEventCount` lines and loses every other value and every child node
      (build_mallard_splashed.py's strip, imported from it), so the SUB lanes record on a
      save with no Parsek history;
    - the `Title`, `psub-pad (...)` -> `psub-splashed (...)` (the mode suffix kept);
    - the ONE VESSEL named PSUB-1, which must read `sit = PRELAUNCH`:
        sit = SPLASHED, landed = False, splashed = True, landedAt and
        displaylandedAt emptied;
        lat / lon = SITE_LAT / SITE_LON (below);
        alt = hgt = the root part's height over the hull's centre in the craft
        (build_psub_craft.layout(): the Octo2 root sits ROOT_ABOVE_HULL m above the
        FL-T200 hull), so the hull's centre starts at sea level and the sub settles to
        its own waterline from there;
        ORBIT: the canonical surface tuple (SMA 0, ECC 1, INC / LPE / LAN / MNA / EPH 0;
        REF and IDENT kept), the shape Parsek's own SPLASHED spawns write.
      Everything else of the node is kept: `rot` (a surface-relative rotation; moving the
      vessel 0.41 deg of longitude tilts its up axis by the same 0.41 deg off the local
      vertical, which the first SAS frame takes out), the parts, the stage, the crew
      (none).
  Every other VESSEL, the roster, the clock and the stock scenarios are untouched.

THE SITE. lat -0.0972 (the pad's own latitude), lon -74.15: about 4.3 km east of the pad,
over the sea AIR-3 loiters over (its loiter leg runs lon -73.85 .. -74.30 at lat -0.05).
Far enough that a splashed vessel there stays unloaded while the player sits on the pad
(a splashed vessel loads within about 2.5 km), close enough for the Space Center's ghost
range (BOAT-1's Mallard ended 9 km east). The depth of the sea floor there is NOT
measured: the dive logs Flight.Elevation at its handoff (`floor=` in the `handoff:`
reading), and ends its sink at the floor if the floor comes before the target depth.

MODES.
  --write   (re)write the output from the source; refuses when the source is missing.
  --check   re-derive and compare against the committed output. While NEITHER the source
            nor the output is committed (the forge has not flown) it prints PENDING and
            exits 0; exactly one of the two present is a FAIL (a half-done harvest).

`harness/lib/test_psub_splashed.py` runs the derivation on a synthetic pad save (the forge
base's own PRELAUNCH vessel renamed) and pins the PENDING contract.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import re
import shutil
import sys
from typing import Dict, List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
SOURCE_DIR = os.path.join(SAVES, "psub-pad")
OUT_DIR = os.path.join(SAVES, "psub-splashed")

LF = "\n"
SOURCE_TITLE_LEAF = "psub-pad"
TITLE_LEAF = "psub-splashed"
COPIED_TOP = ("persistent.loadmeta", "AddOns")
VESSEL_NAME = "PSUB-1"
SOURCE_SITUATION = "PRELAUNCH"
SITE_LAT = -0.0972
SITE_LON = -74.15
CANONICAL_SURFACE_ORBIT = (("SMA", "0"), ("ECC", "1"), ("INC", "0"), ("LPE", "0"),
                           ("LAN", "0"), ("MNA", "0"), ("EPH", "0"))


def _load(name: str):
    spec = importlib.util.spec_from_file_location(name, os.path.join(_HERE, name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_mallard = _load("build_mallard_splashed")
_craft = _load("build_psub_craft")


def root_above_hull() -> float:
    """The Octo2 root's height over the hull's centre in PSUB-1 (metres)."""
    parts = {p.key: p for p in _craft.layout()}
    return round(parts["probe"].pos[1] - parts["hull"].pos[1], 6)


ROOT_ABOVE_HULL = root_above_hull()


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def vessel_span(lines: List[str], name: str) -> Tuple[int, int]:
    """(open_brace_index, close_brace_index) of the ONE depth-2 VESSEL named `name`."""
    found: List[Tuple[int, int]] = []
    for i, line in enumerate(lines):
        if line == "\t\tVESSEL" and i + 1 < len(lines) and lines[i + 1] == "\t\t{":
            j = lines.index("\t\t}", i + 2)
            if ("\t\t\tname = " + name) in lines[i + 2:j]:
                found.append((i + 1, j))
    if len(found) != 1:
        raise SystemExit("source save holds %d VESSEL nodes named %s, not 1"
                         % (len(found), name))
    return found[0]


def _set_value(lines: List[str], lo: int, hi: int, indent: str, key: str,
               value: str) -> None:
    """Replace the one `<indent><key> = ...` line in lines[lo:hi] (the node's own
    depth only: a child node's line carries a deeper indent)."""
    prefix = indent + key + " ="
    hits = [i for i in range(lo, hi) if lines[i] == prefix or
            lines[i].startswith(prefix + " ")]
    if len(hits) != 1:
        raise SystemExit("expected one %r line, found %d" % (key, len(hits)))
    lines[hits[0]] = prefix + " " + value


def relocate_vessel(lines: List[str]) -> List[str]:
    """Move the PSUB-1 VESSEL node into the sea (the edits the docstring lists)."""
    out = list(lines)
    open_i, close_i = vessel_span(out, VESSEL_NAME)
    ind = "\t\t\t"
    sit = [l for l in out[open_i:close_i] if l.startswith(ind + "sit = ")]
    if sit != [ind + "sit = " + SOURCE_SITUATION]:
        raise SystemExit("%s reads %r, not sit = %s (re-harvest with --expect-situation "
                         "%s)" % (VESSEL_NAME, sit, SOURCE_SITUATION, SOURCE_SITUATION))
    alt = repr(ROOT_ABOVE_HULL)
    for key, value in (("sit", "SPLASHED"), ("landed", "False"), ("landedAt", ""),
                       ("displaylandedAt", ""), ("splashed", "True"),
                       ("lat", repr(SITE_LAT)), ("lon", repr(SITE_LON)),
                       ("alt", alt), ("hgt", alt)):
        _set_value(out, open_i, close_i, ind, key, value)
    # The vessel's own ORBIT child (depth 3).
    orbit = [i for i in range(open_i, close_i) if out[i] == ind + "ORBIT"]
    if len(orbit) != 1 or out[orbit[0] + 1] != ind + "{":
        raise SystemExit("%s has %d ORBIT nodes, not 1" % (VESSEL_NAME, len(orbit)))
    o_lo = orbit[0] + 2
    o_hi = out.index(ind + "}", o_lo)
    for key, value in CANONICAL_SURFACE_ORBIT:
        _set_value(out, o_lo, o_hi, ind + "\t", key, value)
    return out


def retitle(lines: List[str]) -> List[str]:
    out = list(lines)
    pat = re.compile(r"^\tTitle = %s (\(.*\))$" % re.escape(SOURCE_TITLE_LEAF))
    hits = [i for i, l in enumerate(out) if pat.match(l)]
    if len(hits) != 1:
        raise SystemExit("source save has no `Title = %s (<MODE>)` line"
                         % SOURCE_TITLE_LEAF)
    out[hits[0]] = "\tTitle = %s %s" % (TITLE_LEAF, pat.match(out[hits[0]]).group(1))
    return out


def build_sfs_text(source: str) -> str:
    """The output persistent.sfs from the source persistent.sfs text."""
    if "\r" in source:
        raise SystemExit("source persistent.sfs carries CR bytes (the harvest writes LF)")
    lines = source.split(LF)
    open_i, close_i = _mallard.scenario_span(lines)
    kept, _dropped = _mallard.strip_scenario(lines[open_i + 1:close_i])
    lines[open_i + 1:close_i] = kept
    lines = retitle(relocate_vessel(lines))
    out = LF.join(lines)
    assert "RECORDING_TREE" not in out and "parsekSaveFormat" not in out
    return out


def expected_files(source_dir: str) -> Dict[str, bytes]:
    files: Dict[str, bytes] = {}
    for top in COPIED_TOP:
        src = os.path.join(source_dir, top)
        if os.path.isfile(src):
            files[top] = _read_bytes(src)
            continue
        for root, _dirs, names in os.walk(src):
            for name in names:
                path = os.path.join(root, name)
                files[os.path.relpath(path, source_dir).replace(os.sep, "/")] = \
                    _read_bytes(path)
    files["persistent.sfs"] = build_sfs_text(
        _read(os.path.join(source_dir, "persistent.sfs"))).encode("utf-8")
    return files


def committed_problems(source_dir: str, out_dir: str) -> Optional[List[str]]:
    """None while NEITHER directory exists (PENDING); else the list of problems."""
    have_src, have_out = os.path.isdir(source_dir), os.path.isdir(out_dir)
    if not have_src and not have_out:
        return None
    if not have_src:
        return ["source %s is missing but the output is present" % source_dir]
    if not have_out:
        return ["output %s is missing: run build_psub_splashed.py --write" % out_dir]
    want = expected_files(source_dir)
    have: Dict[str, bytes] = {}
    for root, _dirs, names in os.walk(out_dir):
        for name in names:
            path = os.path.join(root, name)
            have[os.path.relpath(path, out_dir).replace(os.sep, "/")] = _read_bytes(path)
    problems = []
    for rel in sorted(set(want) | set(have)):
        if rel not in have:
            problems.append("missing " + rel)
        elif rel not in want:
            problems.append("unexpected " + rel)
        elif have[rel] != want[rel]:
            problems.append("drift in " + rel)
    return problems


def write(source_dir: str, out_dir: str) -> None:
    if not os.path.isfile(os.path.join(source_dir, "persistent.sfs")):
        raise SystemExit("no persistent.sfs in %s: fly FORGE-psub-pad and harvest it "
                         "first (harvest_bdock_station.py --target-name psub-pad "
                         "--expect-situation PRELAUNCH)" % source_dir)
    files = expected_files(source_dir)
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    for rel, data in files.items():
        path = os.path.join(out_dir, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true",
                      help="re-derive and compare against the committed fixture")
    mode.add_argument("--write", action="store_true", help="(re)write the fixture")
    ap.add_argument("--source", default=SOURCE_DIR,
                    help="the harvested pad save (default fixtures/saves/psub-pad)")
    ap.add_argument("--out", default=OUT_DIR,
                    help="the output save (default fixtures/saves/psub-splashed)")
    args = ap.parse_args(argv)
    if args.write:
        write(args.source, args.out)
        print("wrote %s (PSUB-1 at lat %s lon %s alt %s)"
              % (args.out, SITE_LAT, SITE_LON, ROOT_ABOVE_HULL))
        return 0
    problems = committed_problems(args.source, args.out)
    if problems is None:
        print("PENDING: neither %s nor %s exists yet (fly FORGE-psub-pad, harvest, "
              "then --write)" % (os.path.relpath(args.source, HARNESS),
                                 os.path.relpath(args.out, HARNESS)))
        return 0
    for p in problems:
        print("DRIFT: " + p)
    print("OK" if not problems else "FAIL")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
