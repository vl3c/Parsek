#!/usr/bin/env python3
"""Build the `career-idless-same-name-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. `KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY` stage 2 refuses
the irreversible recovery XP row (`reason=ambiguous-recovery-recording`) when
the recovery pick is ambiguous. Since stage 3 (identity first, PR #1947) that
refusal lives ONLY on the picker's name fallback, which runs when:

  - NO admissible recording carries the recovering vessel's launch guid
    (`LedgerOrchestrator.IsPositiveLaunchGuidMatch` finds nothing), and
  - NO recording is the source of a genuine Parsek spawn with the vessel's pid
    (`IsGenuineSpawnPidMatch` finds nothing).

On that fallback the stage-1 filter can only drop a candidate whose guid
CONCLUSIVELY differs, and the recovery seam always supplies a live guid
(`ProtoVessel.vesselID`), so every survivor is an id-LESS recording. Stage 2
then fires iff two or more such same-name survivors remain and a weak tier
(`most-recent-ended` / `global-latest`) chose among them. Every current write
site stamps `recordedVesselGuid` (the stage-2 writer audit), so two id-less
same-name survivors is LEGACY DATA: a generation-4 recording from before the
key existed, with no snapshot to backfill it from. This fixture is that legacy
data, built so the one live flight `L7-career-idless-same-name-xp-refusal` can
recover a vessel against it.

THE BASE. Everything `build_career_same_name_pad.py` does is done first and
unchanged (the C2CareerPostFix RECORDING_TREE spliced into the pre-flight
`career-science-pad`, the rewind hint stripped, the clock moved to 409.56, the
live vessel's `pid` re-stamped to `NEW_LAUNCH_GUID`, no earned ledger). See that
builder's docstring for why each of those is load-bearing; the un-banked
launchpad science in particular is what lets the recover mission reach RECOVER.

THE THREE EDITS ON TOP:

  1. Every `recordedVesselGuid` line is STRIPPED from the spliced tree. Both
     recordings are now id-less, which is what the name fallback needs: the
     stage-1 filter keeps them (an unknown guid is never conclusive) and
     stage 2's corroboration reads `unknown-launch-guid`.
  2. The top-level `pid` of every `_vessel.craft` / `_ghost.craft` snapshot
     sidecar is STRIPPED (decode PSN0 -> edit -> re-encode). Without this the
     load-time backfill in `RecordingSidecarStore` re-reads the guid from the
     snapshot (`VesselLaunchIdentity.TryReadVesselGuid`) and edit 1 is undone at
     the first OnLoad: the filter would then drop both recordings as
     conclusively different and the run would measure `no-recovery-recording`
     instead of the ambiguity refusal.
  3. The live vessel's craft-baked `persistentId` is RE-STAMPED away from the
     recordings' `vesselPersistentId` (the opposite of the base fixture, which
     keeps the collision on purpose). With the recordings id-less, every
     guid-gated pid site (`VesselLaunchIdentity.LiveVesselIsRecordedLaunch`)
     falls back to pid-only, so a colliding pid would let the committed-tree
     restore path (`ParsekFlight.TryFindCommittedTreeForSpawnedVessel`) treat
     the pad craft as the recorded vessel and resume a recording on it - whose
     recorder-start backstop (`Recording.AdoptRecordedVesselGuidIfEmpty`) would
     stamp the live guid and send the recovery down the launch-guid path. The
     lane needs a recovered vessel NO recording knows by identity; its NAME is
     the only link, which is exactly the legacy shape.

The flight itself must not record either: the spec pins all three auto-record
settings off, so no recording of the run carries the live guid.

WHAT A RECOVERY OVER THIS FIXTURE THEN PRODUCES (arithmetic from the source,
a PREDICTION until flown): every leg picks through `path=name-fallback
reason=no-recording-carries-launch-guid nameMatches=2`; the guid filter drops 0
(`reason=no-conclusive-mismatch`); both recordings ended before the clock, so
the tier is `most-recent-ended` and the pick is the later-ending
`5436a7e8...` (EndUT 347.02 vs 342.06). Funds and science credit that pick;
the XP leg refuses with `survivors=2 ... corroboration=unknown-launch-guid`.

SNAPSHOT RE-ENCODING. The PSN0 container is `SnapshotSidecarCodec`'s: magic,
int32 format version, int32 schema generation, byte codec (1 = raw deflate),
int32 uncompressed length, int32 compressed length, uint32 CRC-32 of the
payload, then the raw-deflate payload (a `SNAPSHOT_SIDECAR` wrapper around the
snapshot node). The header fields are carried over and the lengths / CRC
recomputed. Compressed BYTES depend on the local zlib, so the drift cells
compare decoded payloads, not the compressed bytes.

USAGE
    python harness/tools/build_career_idless_same_name_pad.py            # write
    python harness/tools/build_career_idless_same_name_pad.py --check    # verify

Stdlib only; no KSP, no network. ASCII only. CRLF out, like every committed
fixture.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import struct
import sys
import zlib
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# The base build is reused verbatim, so the two fixtures cannot drift apart on
# anything but the three edits above.
import build_career_same_name_pad as same_name  # noqa: E402

read_lines = same_name.read_lines
write_lines = same_name.write_lines
find_node = same_name.find_node
child_nodes = same_name.child_nodes
get_value = same_name.get_value
set_value = same_name.set_value
contains_line = same_name.contains_line

RECORDINGS_DIR = same_name.RECORDINGS_DIR
HOST_NAME = same_name.HOST_NAME
TARGET_NAME = "career-idless-same-name-pad"
CREW_NAME = same_name.CREW_NAME
NEW_LAUNCH_GUID = same_name.NEW_LAUNCH_GUID
RECORDED_VESSEL_PERSISTENT_ID = same_name.RECORDED_VESSEL_PERSISTENT_ID
RECORDED_VESSEL_NAME = same_name.RECORDED_VESSEL_NAME
EXPECT_RECORDING_COUNT = same_name.EXPECT_RECORDING_COUNT
PRUNED_PARSEK_SUBDIRS = same_name.PRUNED_PARSEK_SUBDIRS

# The live craft's re-stamped persistentId (edit 3). A FIXED literal so the
# fixture is reproducible; asserted unequal to every recorded pid and to every
# part persistentId in the save.
NEW_VESSEL_PERSISTENT_ID = "3141592653"

# The pick the tier walk must make: the later-ending of the two spliced
# recordings. Held here so the drift cells and the spec can name it.
EXPECT_PICK_RECORDING_ID = "5436a7e8840b4c5885afcbaedc9dc037"

SNAPSHOT_SUFFIXES = ("_vessel.craft", "_ghost.craft")

_GUID_LINE_RE = re.compile(r"^\s*recordedVesselGuid = ")

# PSN0 container (SnapshotSidecarCodec): magic + <i i B i i I> + payload.
_PSN0_MAGIC = b"PSN0"
_PSN0_HEADER = struct.Struct("<iiBiiI")
_PSN0_HEADER_LEN = len(_PSN0_MAGIC) + _PSN0_HEADER.size
_PSN0_CODEC_DEFLATE = 1

# The snapshot node's own top-level pid: two tabs deep (SNAPSHOT_SIDECAR > VESSEL).
_SNAPSHOT_PID_LINE = re.compile(r"^\t\tpid = \S+$")


# ---------------------------------------------------------------------------
# THE SAVE TEXT
# ---------------------------------------------------------------------------

def build(host_lines: List[str], produced_lines: List[str], title: str) -> List[str]:
    """The base build, then edits 1 and 3. Pure: takes and returns line lists."""
    lines = same_name.build(host_lines, produced_lines, title)

    # 1. Strip every recorded launch guid from the spliced tree.
    lines = [line for line in lines if not _GUID_LINE_RE.match(line)]

    # 3. Re-stamp the live craft's persistentId away from the recordings'.
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        raise AssertionError("the built save has no FLIGHTSTATE node")
    vessels = child_nodes(lines, fs, "VESSEL")
    if len(vessels) != 1:
        raise AssertionError("expected exactly 1 VESSEL, found %d" % len(vessels))
    if not set_value(lines, vessels[0], "persistentId", NEW_VESSEL_PERSISTENT_ID):
        raise AssertionError("the host VESSEL has no persistentId line")
    return lines


def build_loadmeta(host_meta: List[str], lines: List[str]) -> List[str]:
    return same_name.build_loadmeta(host_meta, lines)


# ---------------------------------------------------------------------------
# THE SNAPSHOT SIDECARS (edit 2)
# ---------------------------------------------------------------------------

def decode_snapshot(data: bytes) -> Tuple[Tuple[int, int, int], bytes]:
    """((formatVersion, schemaGeneration, codec), payload) of a PSN0 sidecar.

    Raises AssertionError on anything SnapshotSidecarCodec.TryLoad would refuse:
    bad magic, a length that disagrees with the file, a CRC mismatch."""
    if data[:4] != _PSN0_MAGIC:
        raise AssertionError("not a PSN0 snapshot sidecar (magic %r)" % data[:4])
    fv, gen, codec, ulen, clen, crc = _PSN0_HEADER.unpack(
        data[4:_PSN0_HEADER_LEN])
    body = data[_PSN0_HEADER_LEN:]
    if len(body) != clen:
        raise AssertionError("compressed length %d != header %d" % (len(body), clen))
    if codec != _PSN0_CODEC_DEFLATE:
        raise AssertionError("unsupported snapshot codec %d" % codec)
    payload = zlib.decompress(body, -15)
    if len(payload) != ulen:
        raise AssertionError("uncompressed length %d != header %d" % (len(payload), ulen))
    if (zlib.crc32(payload) & 0xFFFFFFFF) != crc:
        raise AssertionError("payload CRC-32 mismatch")
    return (fv, gen, codec), payload


def encode_snapshot(header: Tuple[int, int, int], payload: bytes) -> bytes:
    """A PSN0 sidecar carrying ``payload`` under the given header fields."""
    fv, gen, codec = header
    comp = zlib.compressobj(9, zlib.DEFLATED, -15)
    body = comp.compress(payload) + comp.flush()
    return (_PSN0_MAGIC
            + _PSN0_HEADER.pack(fv, gen, codec, len(payload), len(body),
                                zlib.crc32(payload) & 0xFFFFFFFF)
            + body)


def strip_snapshot_pid(payload: bytes) -> bytes:
    """The payload with the snapshot node's top-level `pid` line removed.

    Exactly ONE such line must exist: zero means the source no longer carries a
    guid to strip (re-check the edit), two means the wrapper shape changed."""
    text = payload.decode("utf-8")
    lines = text.split("\n")
    hits = [i for i, line in enumerate(lines) if _SNAPSHOT_PID_LINE.match(line)]
    if len(hits) != 1:
        raise AssertionError("expected exactly 1 top-level snapshot pid line, found %d"
                             % len(hits))
    del lines[hits[0]]
    return "\n".join(lines).encode("utf-8")


def snapshot_top_level_pid(payload: bytes) -> Optional[str]:
    """The snapshot node's top-level pid, or None. The value
    `VesselLaunchIdentity.TryReadVesselGuid` would backfill from."""
    for line in payload.decode("utf-8").split("\n"):
        if _SNAPSHOT_PID_LINE.match(line):
            return line.split("=", 1)[1].strip()
    return None


def rebuild_snapshot(source_bytes: bytes) -> bytes:
    header, payload = decode_snapshot(source_bytes)
    return encode_snapshot(header, strip_snapshot_pid(payload))


# ---------------------------------------------------------------------------
# THE POST-CONDITIONS
# ---------------------------------------------------------------------------

def verify(lines: List[str], crew_name: str) -> List[str]:
    """Return a list of failure strings (empty = every post-condition holds)."""
    problems: List[str] = []

    mode = None
    for line in lines:
        if line.startswith("\tMode = "):
            mode = line.split("=", 1)[1].strip()
            break
    if mode != same_name.EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r" % (mode, same_name.EXPECT_MODE))

    # ---- un-banked flight science (the recover mission's TRANSMIT gate) ----
    rnd = same_name._scenario_node(lines, "ResearchAndDevelopment")
    if rnd is None:
        problems.append("no ResearchAndDevelopment SCENARIO node")
    elif child_nodes(lines, rnd, "Science"):
        problems.append("the career has BANKED Science subjects - the recover "
                        "mission would fail TRANSMIT before reaching recovery")
    if same_name._scenario_value(lines, "Funding", "funds") != same_name.EXPECT_FUNDS:
        problems.append("funds pool is not the seed %r" % same_name.EXPECT_FUNDS)
    if (same_name._scenario_value(lines, "ResearchAndDevelopment", "sci")
            != same_name.EXPECT_SCIENCE):
        problems.append("science pool is not the seed %r" % same_name.EXPECT_SCIENCE)

    # ---- the live vessel ---------------------------------------------------
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        problems.append("no FLIGHTSTATE node")
        return problems
    vessels = child_nodes(lines, fs, "VESSEL")
    if len(vessels) != 1:
        problems.append("expected exactly 1 VESSEL, found %d" % len(vessels))
        return problems
    ship = vessels[0]
    if get_value(lines, ship, "sit") != same_name.EXPECT_SITUATION:
        problems.append("vessel sit is %r, expected %r"
                        % (get_value(lines, ship, "sit"), same_name.EXPECT_SITUATION))
    if get_value(lines, fs, "activeVessel") != "0":
        problems.append("activeVessel is not '0'")
    if not contains_line(lines, ship, "crew = %s" % crew_name):
        problems.append("no `crew = %s` line inside the vessel: the XP leg needs a "
                        "crewed recovery" % crew_name)

    # ---- THE SUBJECT: a vessel no recording knows by identity --------------
    recorded_guids = same_name._values_named(lines, "recordedVesselGuid")
    recorded_pids = same_name._values_named(lines, "vesselPersistentId")
    recorded_names = same_name._values_named(lines, "vesselName")
    recorded_ids = same_name._values_named(lines, "recordingId")
    spawned = same_name._values_named(lines, "spawnedPid")
    if recorded_guids:
        problems.append("the spliced recordings still carry recordedVesselGuid %r: "
                        "the lane needs id-LESS survivors" % recorded_guids)
    if len(recorded_ids) != EXPECT_RECORDING_COUNT:
        problems.append("expected %d recordings, found %d"
                        % (EXPECT_RECORDING_COUNT, len(recorded_ids)))
    if EXPECT_PICK_RECORDING_ID not in recorded_ids:
        problems.append("the expected pick %s is not a recording here"
                        % EXPECT_PICK_RECORDING_ID)
    if set(recorded_names) != {RECORDED_VESSEL_NAME}:
        problems.append("the recordings name %r, expected all %r - the name is the "
                        "only link the fallback has" % (sorted(set(recorded_names)),
                                                        RECORDED_VESSEL_NAME))
    if spawned:
        problems.append("a recording carries spawnedPid %r: the spawn-pid path could "
                        "decide instead of the name fallback" % spawned)
    if get_value(lines, ship, "pid") != NEW_LAUNCH_GUID:
        problems.append("vessel pid is %r, expected %r"
                        % (get_value(lines, ship, "pid"), NEW_LAUNCH_GUID))
    vessel_pid = get_value(lines, ship, "persistentId")
    if vessel_pid != NEW_VESSEL_PERSISTENT_ID:
        problems.append("vessel persistentId is %r, expected the re-stamped %r"
                        % (vessel_pid, NEW_VESSEL_PERSISTENT_ID))
    if vessel_pid in recorded_pids:
        problems.append("the live persistentId %r equals a recording's "
                        "vesselPersistentId: with id-less recordings the pid-only "
                        "fallback would match it" % vessel_pid)
    part_pids = same_name._values_named(lines[ship[0] + 2:ship[1]], "persistentId")
    if part_pids.count(NEW_VESSEL_PERSISTENT_ID) != 1:
        problems.append("the re-stamped persistentId %s collides with a part pid"
                        % NEW_VESSEL_PERSISTENT_ID)

    # ---- the clock: both recordings ended before it (weak tier) -----------
    ut = get_value(lines, fs, "UT")
    ends = [float(v) for v in same_name._values_named(lines, "explicitEndUT")]
    if ut is None or not ends or float(ut) < max(ends):
        problems.append("the recordings do not all end before the career clock %r "
                        "(ends %r)" % (ut, ends))
    if get_value(lines, ship, "lct") != ut:
        problems.append("vessel lct is not the career clock")

    stray = [line.strip() for line in lines if same_name._REWIND_HINT_RE.match(line)]
    if stray:
        problems.append("rewindSave hint(s) survived: %s" % (stray,))
    return problems


def verify_sidecars(target_dir: str) -> List[str]:
    """File-tree post-conditions: every snapshot decodes and carries no pid."""
    problems: List[str] = []
    recordings = os.path.join(target_dir, "Parsek", "Recordings")
    names = os.listdir(recordings) if os.path.isdir(recordings) else []
    snaps = sorted(n for n in names if n.endswith(SNAPSHOT_SUFFIXES))
    if len(snaps) != 2 * EXPECT_RECORDING_COUNT:
        problems.append("expected %d snapshot sidecars, found %d: %s"
                        % (2 * EXPECT_RECORDING_COUNT, len(snaps), snaps))
    for n in snaps:
        with open(os.path.join(recordings, n), "rb") as fh:
            try:
                _, payload = decode_snapshot(fh.read())
            except AssertionError as ex:
                problems.append("%s does not decode: %s" % (n, ex))
                continue
        pid = snapshot_top_level_pid(payload)
        if pid is not None:
            problems.append("%s still carries snapshot pid %s: the load-time backfill "
                            "would restore the stripped guid" % (n, pid))
    mirrors = sorted(n for n in names if n.endswith(".craft.txt"))
    if mirrors:
        problems.append("forbidden snapshot mirrors: %s" % (mirrors,))
    return problems


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of writing it")
    parser.add_argument("--crew", default=CREW_NAME)
    args = parser.parse_args(argv)

    target_dir = os.path.join(_SAVES, TARGET_NAME)
    target_sfs = os.path.join(target_dir, "persistent.sfs")

    if args.check:
        if not os.path.isfile(target_sfs):
            print("FAIL: %s does not exist" % target_sfs)
            return 1
        problems = verify(read_lines(target_sfs), args.crew) + verify_sidecars(target_dir)
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % TARGET_NAME)
        return 0

    host_dir = os.path.join(_SAVES, HOST_NAME)
    for d in (RECORDINGS_DIR, host_dir):
        if not os.path.isdir(d):
            print("FAIL: missing input fixture %s" % d)
            return 1

    host_lines = read_lines(os.path.join(host_dir, "persistent.sfs"))
    produced_lines = read_lines(os.path.join(RECORDINGS_DIR, "persistent.sfs"))
    built = build(host_lines, produced_lines, "%s (CAREER)" % TARGET_NAME)
    problems = verify(built, args.crew)
    if problems:
        for p in problems:
            print("FAIL: %s" % p)
        return 1

    os.makedirs(target_dir, exist_ok=True)
    write_lines(target_sfs, built)
    write_lines(os.path.join(target_dir, "persistent.loadmeta"),
                build_loadmeta(read_lines(os.path.join(host_dir, "persistent.loadmeta")),
                               built))

    parsek_src = os.path.join(RECORDINGS_DIR, "Parsek")
    parsek_dst = os.path.join(target_dir, "Parsek")
    shutil.rmtree(parsek_dst, ignore_errors=True)
    shutil.copytree(parsek_src, parsek_dst,
                    ignore=shutil.ignore_patterns(*PRUNED_PARSEK_SUBDIRS))

    # Edit 2: re-encode every snapshot with its top-level pid stripped.
    recordings = os.path.join(parsek_dst, "Recordings")
    for n in sorted(os.listdir(recordings)):
        if not n.endswith(SNAPSHOT_SUFFIXES):
            continue
        path = os.path.join(recordings, n)
        with open(path, "rb") as fh:
            rebuilt = rebuild_snapshot(fh.read())
        with open(path, "wb") as fh:
            fh.write(rebuilt)

    addons_src = os.path.join(host_dir, "AddOns")
    addons_dst = os.path.join(target_dir, "AddOns")
    if os.path.isdir(addons_src):
        shutil.rmtree(addons_dst, ignore_errors=True)
        shutil.copytree(addons_src, addons_dst)

    problems = verify_sidecars(target_dir)
    for p in problems:
        print("FAIL: %s" % p)
    if problems:
        return 1
    print("OK: wrote %s (host=%s recordings=C2CareerPostFix id-less, crew=%s)"
          % (target_dir, HOST_NAME, args.crew))
    return 0


if __name__ == "__main__":
    sys.exit(main())
