#!/usr/bin/env python3
"""Build the `recording-quarantine-ksc` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. `ParsekFileCollector` (design-load-restores-the-save.md 4.5, "The
quarantine") judges every recording file under `saves/<save>/Parsek/Recordings/` on a
cold load, and two fixes changed what it does with a file nothing names:

  * RECORDING-QUARANTINE-NEVER-EMPTIED (commit 9fdd78b): an unnamed head moves into
    `_quarantine/<yyyy-MM-dd>/`; a quarantined file a save names again goes back (on a
    cold load BEFORE hydration, so the save loads whole); a flat (undated) quarantined
    file is filed into today's folder; a file still unnamed 14 whole UTC days after its
    folder's date is deleted.
  * GC15-UNNAMED-VERSION-DELETED-WITHOUT-GRACE (commit 9ec9104): an unnamed retained
    version `_versions/<id>.<hash><suffix>` moves into `_versions/_quarantine/<date>/`
    instead of being deleted at once, waits the same 14 days, and goes back when a save
    names its (id, hash) again - also on a cold load before hydration.

No committed fixture carries any of those states, and no seam verb can plant a file, so
QR-1 needs a save whose files ARE those states before boot. This tool derives one from
`career-earned-ksc` (vessel-less career, boots to the Space Center, committed tree
"Jumping Flea" with two recordings) and changes nothing else:

  R1 = 1d611e7533a64508ae6f3b305a51615e, the GC-15 subject (a restored older save).
    * Its RECORDING node gains `vesselHash` / `ghostHash` (the writer's keys, in the
      writer's order right after `sidecarEpoch`) naming the BASE snapshot bytes.
    * Its head `_vessel.craft` / `_ghost.craft` become a NEWER copy (the base bytes plus
      a short ASCII trailer), so neither head carries the hash the save names: the
      state a save brought back from outside the folder meets.
    * The named ghost bytes wait as a live version `_versions/<R1>.<ghostHash>_ghost.craft`
      (the cold load promotes it straight away).
    * The named vessel bytes wait in `_versions/_quarantine/2020-01-01/` - quarantined by
      an earlier collection and LONG EXPIRED - so the cold load must restore the version
      the loaded save names before the purge can see it.
    The promotion moves both newer heads aside into `_versions` under their own hashes,
    nothing names them, and the same load's collection must QUARANTINE them (before the
    fix it deleted an unnamed version at once).
  R2 = 5436a7e8840b4c5885afcbaedc9dc037, the restore subject. All five of its files sit
    in `_quarantine/2020-01-01/` (expired), none at the head position. The save names
    it, so the cold load must move them back before hydration (before the fix the
    folder was never read and the recording hydrated `trajectory-missing`).
  Three unnamed orphan recordings (full five-file sets, R2's bytes under a synthetic id,
  the shape of a flight recorded after a save the player never saved again):
    * O1 `9f1e5000000000000000000000000001` at the HEAD position: the first pass must
      quarantine it into today's folder, and the same pass must judge it waiting.
    * O2 `9f1e5000000000000000000000000002` in `_quarantine/2020-01-01/`: expired, purged.
    * O3 `9f1e5000000000000000000000000003` FLAT in `_quarantine/` (the pre-fix
      undated layout): filed into today's folder.
  One unnamed expired version:
    * O4 `9f1e5000000000000000000000000004` as `_versions/_quarantine/2020-01-01/
      <O4>.<hash>_ghost.craft` (R2's ghost bytes plus its own trailer): purged.

WHY GHOST AND VESSEL RATHER THAN `.prec` FOR R1. The collector and the resolution treat
the three hashed kinds identically, and an unresolved snapshot whose head exists fails
the recording exactly like a trajectory (`stale-sidecar-epoch`). A `.prec` version would
need a `.prec.txt` mirror and a `_vessel.craft` beside it inside `_versions/`
(`CommittedFixtureMirrorTests` pairs every committed `.prec` with both), which is not a
shape Parsek writes.

THE DATE `2020-01-01` is far enough in the past that every wall clock the lane can fly
under reads it expired (`IsQuarantineExpired`: now >= folder date + 14 days); a file the
lane itself quarantines lands in TODAY's folder and must stay. No committed date can be
"fresh", which is why the fresh case is the file the first pass moves.

The base save's `.prec.txt` mirrors travel with each set, so every committed `.prec` keeps
its mirror and its `_vessel.craft` sibling. No `_vessel.craft.txt` / `_ghost.craft.txt`
mirror is written (the snapshot-mirror gate forbids them).

Run `python harness/tools/build_recording_quarantine_ksc.py` to (re)write the fixture and
`--check` to re-derive and compare byte for byte against the committed one;
`harness/lib/test_recording_quarantine_ksc.py` runs the check in-process.

Stdlib only; ASCII only; no em dashes.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys

_HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASE_DIR = os.path.join(_HARNESS_ROOT, "fixtures", "saves", "career-earned-ksc")
TARGET_DIR = os.path.join(_HARNESS_ROOT, "fixtures", "saves", "recording-quarantine-ksc")

REC = "Parsek/Recordings"
QUARANTINE = REC + "/_quarantine"
VERSIONS = REC + "/_versions"
VERSION_QUARANTINE = VERSIONS + "/_quarantine"
EXPIRED_DATE = "2020-01-01"

R1 = "1d611e7533a64508ae6f3b305a51615e"
R2 = "5436a7e8840b4c5885afcbaedc9dc037"
O1_FRESH = "9f1e5000000000000000000000000001"
O2_EXPIRED = "9f1e5000000000000000000000000002"
O3_FLAT = "9f1e5000000000000000000000000003"
O4_VERSION = "9f1e5000000000000000000000000004"

# A recording's five committed files (RecordingStore.RecordingFileSuffixes minus the two
# snapshot mirrors, which no fixture commits).
SET_SUFFIXES = (".prec", ".prec.txt", ".pann", "_vessel.craft", "_ghost.craft")

TRAILER_VESSEL = b"QR-1 newer vessel snapshot written by a later save\n"
TRAILER_GHOST = b"QR-1 newer ghost snapshot written by a later save\n"
TRAILER_O4 = b"QR-1 orphan ghost version nothing names\n"


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def content_hash(data: bytes) -> str:
    """`SidecarContentHash.Compute`: the first 64 bits of SHA-1, 16 lower-case hex."""
    return hashlib.sha1(data).hexdigest()[:16]


def _base(rel: str) -> bytes:
    return _read(os.path.join(BASE_DIR, rel.replace("/", os.sep)))


def derived_values() -> dict:
    """Every hash and byte count the QR-1 spec pins, derived from the base bytes."""
    vessel = _base("%s/%s_vessel.craft" % (REC, R1))
    ghost = _base("%s/%s_ghost.craft" % (REC, R1))
    o4 = _base("%s/%s_ghost.craft" % (REC, R2)) + TRAILER_O4
    o2_bytes = sum(len(_base("%s/%s%s" % (REC, R2, s))) for s in SET_SUFFIXES)
    return {
        "r1VesselNamedHash": content_hash(vessel),
        "r1GhostNamedHash": content_hash(ghost),
        "r1VesselNewerHash": content_hash(vessel + TRAILER_VESSEL),
        "r1GhostNewerHash": content_hash(ghost + TRAILER_GHOST),
        "o4Hash": content_hash(o4),
        # TryDelete adds each purged file's length: O2's five files and O4's version.
        "bytesFreed": o2_bytes + len(o4),
    }


def derive_persistent_sfs(source: bytes, vessel_hash: str, ghost_hash: str) -> bytes:
    """The base save with `vesselHash` / `ghostHash` added to R1's RECORDING node,
    right after its `sidecarEpoch` line (the order RecordingTreeRecordCodec writes)."""
    nl = b"\r\n" if b"\r\n" in source else b"\n"
    lines = source.split(nl)
    out = []
    in_r1 = False
    inserted = 0
    for line in lines:
        out.append(line)
        text = line.decode("utf-8", "replace")
        m = re.match(r"^(\s*)recordingId = (\S+)\s*$", text)
        if m:
            in_r1 = m.group(2) == R1
        if in_r1 and re.match(r"^\s*sidecarEpoch = \d+\s*$", text):
            indent = re.match(r"^(\s*)", text).group(1).encode("ascii")
            out.append(indent + b"vesselHash = " + vessel_hash.encode("ascii"))
            out.append(indent + b"ghostHash = " + ghost_hash.encode("ascii"))
            inserted += 1
            in_r1 = False
    assert inserted == 1, "R1's sidecarEpoch line not found exactly once"
    return nl.join(out)


def expected_files() -> dict:
    """{relative path: bytes} of every file the fixture must contain."""
    v = derived_values()
    files = {}
    for dirpath, _dirnames, filenames in os.walk(BASE_DIR):
        for fn in filenames:
            rel = os.path.relpath(os.path.join(dirpath, fn), BASE_DIR).replace(os.sep, "/")
            if rel.startswith(REC + "/" + R2):
                continue  # R2 lives in the expired quarantine folder instead
            files[rel] = _read(os.path.join(dirpath, fn))
    files["persistent.sfs"] = derive_persistent_sfs(
        _base("persistent.sfs"), v["r1VesselNamedHash"], v["r1GhostNamedHash"])

    vessel = _base("%s/%s_vessel.craft" % (REC, R1))
    ghost = _base("%s/%s_ghost.craft" % (REC, R1))
    files["%s/%s_vessel.craft" % (REC, R1)] = vessel + TRAILER_VESSEL
    files["%s/%s_ghost.craft" % (REC, R1)] = ghost + TRAILER_GHOST
    files["%s/%s.%s_ghost.craft" % (VERSIONS, R1, v["r1GhostNamedHash"])] = ghost
    files["%s/%s/%s.%s_vessel.craft" % (VERSION_QUARANTINE, EXPIRED_DATE, R1,
                                        v["r1VesselNamedHash"])] = vessel

    for suffix in SET_SUFFIXES:
        data = _base("%s/%s%s" % (REC, R2, suffix))
        files["%s/%s/%s%s" % (QUARANTINE, EXPIRED_DATE, R2, suffix)] = data
        files["%s/%s%s" % (REC, O1_FRESH, suffix)] = data
        files["%s/%s/%s%s" % (QUARANTINE, EXPIRED_DATE, O2_EXPIRED, suffix)] = data
        files["%s/%s%s" % (QUARANTINE, O3_FLAT, suffix)] = data

    o4 = _base("%s/%s_ghost.craft" % (REC, R2)) + TRAILER_O4
    files["%s/%s/%s.%s_ghost.craft" % (VERSION_QUARANTINE, EXPIRED_DATE, O4_VERSION,
                                       v["o4Hash"])] = o4
    return files


def check() -> list:
    """Differences between a fresh derivation and the committed fixture (empty = OK)."""
    problems = []
    expected = expected_files()
    for rel, data in sorted(expected.items()):
        path = os.path.join(TARGET_DIR, rel.replace("/", os.sep))
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


def verify_shape(files: dict) -> list:
    """Post-conditions QR-1 depends on, stated on the derived bytes."""
    problems = []
    v = derived_values()
    sfs = files.get("persistent.sfs", b"").decode("utf-8", "replace").replace("\r\n", "\n")
    if re.search(r"^\t+VESSEL$", sfs, re.M) is not None:
        problems.append("carries a VESSEL node; the Space Center route needs none")
    if "name = ParsekScenario" not in sfs:
        problems.append("no ParsekScenario node")
    if sfs.count("vesselHash = ") != 1 or sfs.count("ghostHash = ") != 1 or "precHash = " in sfs:
        problems.append("the save must name exactly R1's vessel and ghost hashes, and no prec hash")
    if ("vesselHash = %s" % v["r1VesselNamedHash"]) not in sfs:
        problems.append("R1's vesselHash does not name the base vessel bytes")
    if ("ghostHash = %s" % v["r1GhostNamedHash"]) not in sfs:
        problems.append("R1's ghostHash does not name the base ghost bytes")
    for rid in (O1_FRESH, O2_EXPIRED, O3_FLAT, O4_VERSION):
        if rid in sfs:
            problems.append("the save names orphan %s; the collector would keep it" % rid)
    for kind, named in (("vessel", "r1VesselNamedHash"), ("ghost", "r1GhostNamedHash")):
        head = files.get("%s/%s_%s.craft" % (REC, R1, kind))
        if head is None or content_hash(head) == v[named]:
            problems.append("R1's %s head must hold bytes the save does not name" % kind)
    for rel in files:
        if rel.startswith(REC + "/" + R2):
            problems.append("R2 has a head file (%s); it must exist only in quarantine" % rel)
        if rel.endswith(("_vessel.craft.txt", "_ghost.craft.txt")):
            problems.append("snapshot mirror committed: %s" % rel)
        if rel.endswith(".prec"):
            stem = rel[:-len(".prec")]
            if stem + ".prec.txt" not in files or stem + "_vessel.craft" not in files:
                problems.append("%s lacks its .prec.txt / _vessel.craft sibling" % rel)
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture; exit 1 on drift")
    args = parser.parse_args(argv)
    if not os.path.isdir(BASE_DIR):
        print("FAIL: base fixture missing: %s" % BASE_DIR)
        return 1
    files = expected_files()
    shape = verify_shape(files)
    for p in shape:
        print("FAIL: " + p)
    if shape:
        return 1
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("recording-quarantine-ksc: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    shutil.rmtree(TARGET_DIR, ignore_errors=True)
    for rel, data in files.items():
        path = os.path.join(TARGET_DIR, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote %s (%d files)" % (TARGET_DIR, len(files)))
    for key, value in sorted(derived_values().items()):
        print("  %s = %s" % (key, value))
    return 0


if __name__ == "__main__":
    sys.exit(main())
