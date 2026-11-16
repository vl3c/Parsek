"""Pure race evaluator, part 1: the rival-id leak scan and the save-equivalence diff.

Multiplayer plan task T-05 (``docs/dev/plans/multiplayer/test-and-harness.md``). The
``raceParse`` verifier row (T-38) and the structure facets (T-21) build on this module; it
has no caller in ``run.py`` until T-38 lands.

Two questions about a produced race save, both answered from what is on disk:

* Did rival data leak into the player's own save? Design 6.4: no rival data is ever written
  into the save. Rival files live only under ``Parsek/Race/`` (the rival cache and the own
  mirror, design 4.1 / 6.1), so ``leak_scan`` searches every OTHER file of the produced save
  for every rival id ``collect_rival_ids`` read off the shared folder. Four places a rival id
  can land are each a leak class with its own ``where`` token:

    - ``path``: a file or directory name outside ``Parsek/Race/`` carries the id (a rival
      payload file copied into ``Parsek/Recordings/``);
    - ``text``: a text file carries it (a key value or a node in ``persistent.sfs``, a
      ``.prec.txt`` mirror, a ``GameState`` file); the finding names the line and the
      ConfigNode path of that line (``GAME/SCENARIO/RIVAL/playerId``);
    - ``binary``: the raw bytes of a binary sidecar carry it (the ``PSK0`` trajectory
      codec writes its strings length-prefixed and uncompressed);
    - ``snapshot``: the inflated text of a ``PSN0`` snapshot sidecar carries it (that codec
      deflates its body, so a raw byte scan cannot see inside it).

* Did the rivals change anything? ``save_equivalence`` diffs two produced saves canonically
  (``persistent.sfs`` and every ``Parsek/`` file outside ``Parsek/Race/``): ConfigNode text
  (and inflated ``PSN0`` snapshots) as trees, aligned by node name and position (scenario
  nodes by their ``name`` value), every other file byte for byte. Two flown sessions never
  land on exactly the same time values, so a short PINNED mask (``EQUIVALENCE_MASK``, a
  module constant, never a spec key) lists the values compared within a tolerance (UT and
  UT-derived values) or not at all (values that differ by nature), each with its written
  reason. That is option (a) of owner question Q-13, which the plan follows until the owner
  answers it.

Results carry a status: ``PASS``, ``FAIL`` (findings), ``VACUOUS`` (no rival id to look for,
so the scan proved nothing) and ``ERROR`` (the save cannot be read whole: no
``persistent.sfs``, an undecodable snapshot, a ConfigNode parse fault). A save that cannot
be read never reads as clean.

Layout facts consumed here (design 4.1 and 6.1):

  - shared folder: ``players/<playerId>/`` per player; ``payload/<recordingId>.<hash><suffix>``
    and ``payload/trees/<treeId>.<hash>.cfg``, where ``<hash>`` is
    ``SidecarContentHash`` (16 lower-case hex characters);
  - safe-write leftovers (``.tmp``, ``.bak.<guid>``) are not part of the folder's grammar
    and are ignored;
  - produced save: rival and own-mirror files only under ``Parsek/Race/``.

The I/O shell is two small readers (``list_folder``, ``read_save_files``); every decision is
a pure function over the in-memory listing or file map. ASCII only; stdlib only.
"""

from __future__ import annotations

import fnmatch
import os
import re
from dataclasses import dataclass
from typing import Dict, Iterable, List, Mapping, Optional, Sequence, Tuple, Union

import resourceq
import saveparse

# ---------------------------------------------------------------------------
# Layout constants.
# ---------------------------------------------------------------------------

SAVE_FILE = "persistent.sfs"
PARSEK_DIR = "Parsek/"
RACE_DIR = "Parsek/Race/"

PLAYERS_DIR = "players"
PAYLOAD_DIR = "payload"
TREES_DIR = "trees"

# SidecarContentHash.HexLength (Source/Parsek/Recording/SidecarVersions.cs).
CONTENT_HASH_HEX_LENGTH = 16

# <recordingId>.<hash><suffix>: the greedy id takes the LAST well-formed hash, so an id
# that itself contains dots still parses; the suffix starts with "." or "_" (".prec",
# "_ghost.craft", "_vessel.craft").
PAYLOAD_NAME_RE = re.compile(
    r"^(?P<id>.+)\.(?P<hash>[0-9a-f]{%d})(?P<suffix>[._][^/]*)$" % CONTENT_HASH_HEX_LENGTH)
TREE_NAME_RE = re.compile(
    r"^(?P<id>.+)\.(?P<hash>[0-9a-f]{%d})\.cfg$" % CONTENT_HASH_HEX_LENGTH)

ID_KIND_PLAYER = "player"
ID_KIND_RECORDING = "recording"
ID_KIND_TREE = "tree"
ID_KIND_UNKNOWN = "id"

WHERE_PATH = "path"
WHERE_TEXT = "text"
WHERE_BINARY = "binary"
WHERE_SNAPSHOT = "snapshot"
LEAK_WHERE = (WHERE_PATH, WHERE_TEXT, WHERE_BINARY, WHERE_SNAPSHOT)

STATUS_PASS = "PASS"
STATUS_FAIL = "FAIL"
STATUS_VACUOUS = "VACUOUS"
STATUS_ERROR = "ERROR"

SNAPSHOT_MAGIC = resourceq.SNAPSHOT_MAGIC
# Binary sidecar magics (trajectory, pannotations and its canonical twin); a file that
# starts with one, or holds a NUL byte, is scanned and compared as bytes.
BINARY_MAGICS = (b"PSK0", b"PNA0", b"PNC0", SNAPSHOT_MAGIC)


def _norm_path(path: str) -> str:
    return path.replace("\\", "/").lstrip("/")


def is_race_path(rel_path: str) -> bool:
    """True for a save-relative path under ``Parsek/Race/`` (exact directory, case kept)."""
    return _norm_path(rel_path).startswith(RACE_DIR)


# ---------------------------------------------------------------------------
# Rival ids from the shared folder.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class RivalIds:
    """Every id the shared folder names for players other than the local one."""

    player_ids: Tuple[str, ...] = ()
    recording_ids: Tuple[str, ...] = ()
    tree_ids: Tuple[str, ...] = ()
    # Paths under a rival's subtree that fit no part of the folder grammar (reported, not
    # guessed at), and safe-write leftovers the grammar ignores.
    unrecognised: Tuple[str, ...] = ()
    ignored: Tuple[str, ...] = ()

    def tagged(self) -> Tuple[Tuple[str, str], ...]:
        """``(kind, id)`` pairs, each id once (first kind wins), empty ids dropped."""
        seen = set()
        out: List[Tuple[str, str]] = []
        for kind, ids in ((ID_KIND_PLAYER, self.player_ids),
                          (ID_KIND_RECORDING, self.recording_ids),
                          (ID_KIND_TREE, self.tree_ids)):
            for rid in ids:
                if rid and rid not in seen:
                    seen.add(rid)
                    out.append((kind, rid))
        return tuple(out)

    @property
    def count(self) -> int:
        return len(self.tagged())


def _is_safe_write_leftover(name: str) -> bool:
    return name.endswith(".tmp") or ".bak." in name


def collect_rival_ids(folder_listing: Iterable[str],
                      own_player_dir: Optional[str]) -> RivalIds:
    """Rival player, recording and tree ids from a shared-folder listing.

    ``folder_listing`` is the folder's file paths relative to its root (``/`` or ``\\``);
    ``own_player_dir`` is the local player's subfolder name under ``players/`` (the own
    player id; a leading ``players/`` is accepted), whose subtree is skipped whole.
    """
    own = _norm_path(own_player_dir or "").rstrip("/")
    if own.startswith(PLAYERS_DIR + "/"):
        own = own[len(PLAYERS_DIR) + 1:]

    players: List[str] = []
    recordings: List[str] = []
    trees: List[str] = []
    unrecognised: List[str] = []
    ignored: List[str] = []

    def add(into: List[str], value: str) -> None:
        if value not in into:
            into.append(value)

    for raw in folder_listing:
        path = _norm_path(raw)
        parts = [p for p in path.split("/") if p]
        if len(parts) < 2 or parts[0] != PLAYERS_DIR:
            continue
        player = parts[1]
        if own and player == own:
            continue
        add(players, player)
        rest = parts[2:]
        if not rest:
            continue
        name = rest[-1]
        if _is_safe_write_leftover(name):
            ignored.append(path)
            continue
        if rest[0] != PAYLOAD_DIR:
            continue  # player.cfg, standings/: no id in the name
        if len(rest) == 2:
            m = PAYLOAD_NAME_RE.match(name)
            if m:
                add(recordings, m.group("id"))
                continue
        elif len(rest) == 3 and rest[1] == TREES_DIR:
            m = TREE_NAME_RE.match(name)
            if m:
                add(trees, m.group("id"))
                continue
        unrecognised.append(path)

    return RivalIds(player_ids=tuple(players), recording_ids=tuple(recordings),
                    tree_ids=tuple(trees), unrecognised=tuple(unrecognised),
                    ignored=tuple(ignored))


def _tag_ids(rival_ids: Union[RivalIds, Iterable[str]]) -> Tuple[Tuple[str, str], ...]:
    if isinstance(rival_ids, RivalIds):
        return rival_ids.tagged()
    seen = set()
    out = []
    for rid in rival_ids:
        if rid and rid not in seen:
            seen.add(rid)
            out.append((ID_KIND_UNKNOWN, rid))
    return tuple(out)


# ---------------------------------------------------------------------------
# ConfigNode line context (which node / key a line belongs to).
# ---------------------------------------------------------------------------


def configNode_line_contexts(text: str) -> List[str]:
    """For each line of ConfigNode text, the slash-joined node path plus the key (a value
    line) or the node name (a name line). Brace and value rules follow
    ``saveparse.parse_sfs``; a malformed file still gets a best-effort context."""
    stack: List[str] = []
    pending: Optional[str] = None
    out: List[str] = []
    # Split on "\n" only, so line numbers agree with the byte scan's newline count.
    for raw in text.split("\n"):
        line = raw.strip()
        if not line or line.startswith("//"):
            out.append("/".join(stack))
            continue
        if "=" in line:
            pending = None
            key = line.partition("=")[0].strip()
            out.append("/".join(stack + [key]))
            continue
        context = None
        buf = ""
        for ch in line + "\n":
            if ch in "{}\n":
                token = buf.strip()
                buf = ""
                if token:
                    pending = token
                    if context is None:
                        context = "/".join(stack + [token])
                if ch == "{":
                    stack.append(pending or "")
                    pending = None
                elif ch == "}" and stack:
                    if context is None:
                        context = "/".join(stack)  # a close line names the node it closes
                    stack.pop()
            else:
                buf += ch
        out.append(context if context is not None else "/".join(stack))
    return out


# ---------------------------------------------------------------------------
# The leak scan.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LeakFinding:
    path: str            # save-relative, "/" separated
    rival_id: str
    id_kind: str
    where: str           # one of LEAK_WHERE
    line: Optional[int] = None    # text / snapshot: 1-based line of the (inflated) text
    offset: Optional[int] = None  # binary: byte offset
    context: str = ""             # text / snapshot: ConfigNode path of the line

    def describe(self) -> str:
        loc = self.path
        if self.line is not None:
            loc += ":%d" % self.line
        elif self.offset is not None:
            loc += "@%d" % self.offset
        extra = (" " + self.context) if self.context else ""
        return "%s rival %s %s in %s%s" % (self.where, self.id_kind, self.rival_id, loc, extra)


@dataclass(frozen=True)
class LeakScanResult:
    status: str
    findings: Tuple[LeakFinding, ...] = ()
    files_scanned: int = 0
    files_skipped_race: int = 0
    ids_scanned: int = 0
    errors: Tuple[str, ...] = ()

    @property
    def leaked(self) -> bool:
        return bool(self.findings)


def _is_binary(data: bytes) -> bool:
    return data[:4] in BINARY_MAGICS or b"\x00" in data


def _id_pattern(tagged: Sequence[Tuple[str, str]]) -> "re.Pattern[bytes]":
    # Longest first, so an id that contains another is reported as itself.
    ids = sorted({rid for _, rid in tagged}, key=lambda s: (-len(s), s))
    return re.compile(b"|".join(re.escape(rid.encode("utf-8")) for rid in ids))


def _text_hits(path: str, text: str, pattern, kinds: Mapping[str, str],
               where: str) -> List[LeakFinding]:
    data = text.encode("utf-8")
    contexts: Optional[List[str]] = None
    found: List[LeakFinding] = []
    seen = set()
    for m in pattern.finditer(data):
        rid = m.group(0).decode("utf-8")
        line = data.count(b"\n", 0, m.start()) + 1
        if (rid, line) in seen:
            continue
        seen.add((rid, line))
        if contexts is None:
            contexts = configNode_line_contexts(text)
        context = contexts[line - 1] if line - 1 < len(contexts) else ""
        found.append(LeakFinding(path=path, rival_id=rid, id_kind=kinds[rid], where=where,
                                 line=line, context=context))
    return found


def leak_scan_files(files: Mapping[str, bytes],
                    rival_ids: Union[RivalIds, Iterable[str]]) -> LeakScanResult:
    """The pure scan over a produced save held in memory: ``files`` maps each
    save-relative path to its bytes. Every path outside ``Parsek/Race/`` is searched
    (its name, its bytes, and the inflated text of a ``PSN0`` snapshot)."""
    tagged = _tag_ids(rival_ids)
    kinds = {rid: kind for kind, rid in tagged}
    norm = {_norm_path(p): data for p, data in files.items()}
    errors: List[str] = []
    if SAVE_FILE not in norm:
        errors.append("no %s in the produced save" % SAVE_FILE)

    scanned = 0
    skipped = 0
    findings: List[LeakFinding] = []
    pattern = _id_pattern(tagged) if tagged else None

    for path in sorted(norm):
        if is_race_path(path):
            skipped += 1
            continue
        scanned += 1
        if pattern is None:
            continue
        data = norm[path]
        for m in pattern.finditer(path.encode("utf-8")):
            rid = m.group(0).decode("utf-8")
            findings.append(LeakFinding(path=path, rival_id=rid, id_kind=kinds[rid],
                                        where=WHERE_PATH))
        if _is_binary(data):
            seen = set()
            for m in pattern.finditer(data):
                rid = m.group(0).decode("utf-8")
                if rid in seen:
                    continue  # one finding per id per binary file
                seen.add(rid)
                findings.append(LeakFinding(path=path, rival_id=rid, id_kind=kinds[rid],
                                            where=WHERE_BINARY, offset=m.start()))
            if data[:4] == SNAPSHOT_MAGIC:
                try:
                    inflated = resourceq.decode_snapshot_sidecar(data)
                except Exception as exc:  # noqa: BLE001 - any decode fault is unscannable
                    errors.append("%s: snapshot not decodable (%s)" % (path, exc))
                    continue
                findings.extend(_text_hits(path, inflated, pattern, kinds, WHERE_SNAPSHOT))
        else:
            text = data.decode("utf-8", errors="replace").lstrip("\ufeff")
            findings.extend(_text_hits(path, text, pattern, kinds, WHERE_TEXT))

    if errors:
        status = STATUS_ERROR
    elif not tagged:
        status = STATUS_VACUOUS
    elif findings:
        status = STATUS_FAIL
    else:
        status = STATUS_PASS
    return LeakScanResult(status=status, findings=tuple(findings), files_scanned=scanned,
                          files_skipped_race=skipped, ids_scanned=len(tagged),
                          errors=tuple(errors))


# ---------------------------------------------------------------------------
# I/O shell: the two readers.
# ---------------------------------------------------------------------------


def list_folder(root: str) -> List[str]:
    """Every file under ``root`` as a ``/``-separated path relative to it, sorted."""
    out: List[str] = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            rel = os.path.relpath(os.path.join(dirpath, name), root)
            out.append(rel.replace(os.sep, "/"))
    return sorted(out)


def read_save_files(save_dir: str, skip_race: bool = True) -> Dict[str, bytes]:
    """The produced save as ``{relative path: bytes}``. ``Parsek/Race/`` is listed with
    empty bytes when ``skip_race`` (the scan and the diff never read it, and a rival cache
    can be large), so its file count still shows."""
    files: Dict[str, bytes] = {}
    for rel in list_folder(save_dir):
        if skip_race and is_race_path(rel):
            files[rel] = b""
            continue
        with open(os.path.join(save_dir, *rel.split("/")), "rb") as fh:
            files[rel] = fh.read()
    return files


def leak_scan(save_dir: str, rival_ids: Union[RivalIds, Iterable[str]]) -> LeakScanResult:
    """Leak scan of a produced save directory (see ``leak_scan_files``)."""
    return leak_scan_files(read_save_files(save_dir), rival_ids)


# ---------------------------------------------------------------------------
# Save equivalence.
# ---------------------------------------------------------------------------

MASK_TOLERANCE = "tolerance"
MASK_IGNORE = "ignore"
MASK_KINDS = (MASK_TOLERANCE, MASK_IGNORE)


@dataclass(frozen=True)
class MaskEntry:
    """One masked value: ``file_glob`` matches the save-relative file, ``key_glob`` the
    value's mask path (node names joined by ``/``, a SCENARIO node as
    ``SCENARIO:<its name value>``, no indices, the key last), both ``fnmatchcase``
    patterns. ``tolerance`` compares both values as numbers within ``tolerance``;
    ``ignore`` compares only that the value is present on both sides."""

    file_glob: str
    key_glob: str
    kind: str
    tolerance: float
    reason: str


# The game clock of two flown sessions differs by the real-time latency of the steps that
# run at 1x (scene loads, seam round trips) after a WarpToUT that itself lands within
# 0.25 s of its target. One window for every UT stamp; RACE-4's reading runs (T-27) tighten
# or widen it, each change with its written reason.
UT_TOLERANCE_SECONDS = 60.0

EQUIVALENCE_MASK: Tuple[MaskEntry, ...] = (
    MaskEntry(SAVE_FILE, "GAME/FLIGHTSTATE/UT", MASK_TOLERANCE, UT_TOLERANCE_SECONDS,
              "the game clock at SaveGame: an explicit-UT warp lands near, not on, its UT "
              "and the 1x steps after it take real time"),
    MaskEntry(SAVE_FILE, "GAME/FLIGHTSTATE/VESSEL/lastUT", MASK_TOLERANCE,
              UT_TOLERANCE_SECONDS,
              "stock stamps each vessel with the UT it was last updated, which follows the "
              "game clock"),
    MaskEntry(SAVE_FILE, "GAME/FLIGHTSTATE/VESSEL/lct", MASK_TOLERANCE, UT_TOLERANCE_SECONDS,
              "the launch UT of a vessel launched during the session follows the game clock "
              "at the launch"),
    MaskEntry(SAVE_FILE, "GAME/FLIGHTSTATE/VESSEL/met", MASK_TOLERANCE, UT_TOLERANCE_SECONDS,
              "mission elapsed time is game clock minus launch UT"),
    MaskEntry(SAVE_FILE, "GAME/FLIGHTSTATE/VESSEL/ORBIT/EPH", MASK_TOLERANCE,
              UT_TOLERANCE_SECONDS,
              "the orbit epoch is the UT the elements were last derived, which a vessel "
              "loaded during the session moves to the game clock"),
    MaskEntry(SAVE_FILE, "GAME/SCENARIO:ProgressTracking/Progress/*/completed",
              MASK_TOLERANCE, UT_TOLERANCE_SECONDS,
              "a milestone reached during the session is stamped with the UT it was reached"),
    MaskEntry(SAVE_FILE, "GAME/SCENARIO:ParsekScenario/saveStamp", MASK_IGNORE, 0.0,
              "a fresh GUID on every save (ParsekScenario.WriteSaveStamp), the in-session "
              "load canary; it differs by nature"),
)


def validate_mask(mask: Sequence[MaskEntry]) -> List[str]:
    """Shape problems of a mask (empty when well-formed)."""
    problems: List[str] = []
    seen = set()
    for i, e in enumerate(mask):
        where = "mask[%d] %s %s" % (i, e.file_glob, e.key_glob)
        if e.kind not in MASK_KINDS:
            problems.append("%s: kind %r not in %s" % (where, e.kind, MASK_KINDS))
        if e.kind == MASK_TOLERANCE and not (e.tolerance > 0):
            problems.append("%s: a tolerance entry needs tolerance > 0" % where)
        if e.kind == MASK_IGNORE and e.tolerance != 0:
            problems.append("%s: an ignore entry carries no tolerance" % where)
        if not e.reason.strip():
            problems.append("%s: no written reason" % where)
        if fnmatch.fnmatchcase(RACE_DIR + "x", e.file_glob):
            problems.append("%s: masks a file under %s, which is never compared"
                            % (where, RACE_DIR))
        if (e.file_glob, e.key_glob) in seen:
            problems.append("%s: duplicate entry" % where)
        seen.add((e.file_glob, e.key_glob))
    return problems


def _find_mask(mask: Sequence[MaskEntry], path: str, mask_path: str) -> Optional[MaskEntry]:
    for e in mask:
        if fnmatch.fnmatchcase(path, e.file_glob) and fnmatch.fnmatchcase(mask_path, e.key_glob):
            return e
    return None


EQ_FILE_MISSING = "file-missing"
EQ_BYTES_DIFFER = "bytes-differ"
EQ_PARSE_ERROR = "parse-error"
EQ_NODE_MISSING = "node-missing"
EQ_VALUE_COUNT = "value-count"
EQ_VALUE_DIFFERS = "value-differs"
EQ_OUTSIDE_TOLERANCE = "outside-tolerance"


@dataclass(frozen=True)
class EquivalenceFinding:
    path: str        # save-relative file
    kind: str        # one of the EQ_* tokens
    where: str = ""  # display path inside the file (with sibling indices)
    a: Optional[str] = None
    b: Optional[str] = None

    def describe(self) -> str:
        tail = ""
        if self.a is not None or self.b is not None:
            tail = " a=%r b=%r" % (self.a, self.b)
        return "%s %s %s%s" % (self.kind, self.path, self.where, tail)


@dataclass(frozen=True)
class MaskedDifference:
    """A value that differed but the mask allowed; listed so a reading run sees what the
    mask absorbed."""

    path: str
    where: str
    a: str
    b: str
    reason: str


@dataclass(frozen=True)
class EquivalenceResult:
    status: str
    findings: Tuple[EquivalenceFinding, ...] = ()
    masked: Tuple[MaskedDifference, ...] = ()
    files_compared: int = 0
    errors: Tuple[str, ...] = ()

    @property
    def equivalent(self) -> bool:
        return self.status == STATUS_PASS


def _compared_paths(files: Mapping[str, bytes]) -> List[str]:
    return [p for p in files
            if p == SAVE_FILE or (p.startswith(PARSEK_DIR) and not is_race_path(p))]


def _node_label(node: saveparse.SfsNode) -> str:
    if node.name == "SCENARIO":
        return "SCENARIO:%s" % (node.value("name") or "")
    return node.name


def _group_children(node: saveparse.SfsNode) -> Dict[str, List[saveparse.SfsNode]]:
    groups: Dict[str, List[saveparse.SfsNode]] = {}
    for child in node.nodes:
        groups.setdefault(_node_label(child), []).append(child)
    return groups


def _group_values(node: saveparse.SfsNode) -> Dict[str, List[str]]:
    groups: Dict[str, List[str]] = {}
    for k, v in node.values:
        groups.setdefault(k, []).append(v)
    return groups


def _parse_number(raw: str) -> Optional[float]:
    try:
        value = float(raw)
    except (TypeError, ValueError):
        return None
    return value if value == value else None  # NaN never compares within a tolerance


def _join(prefix: str, segment: str) -> str:
    return segment if not prefix else prefix + "/" + segment


def _diff_nodes(path: str, a: saveparse.SfsNode, b: saveparse.SfsNode, where: str,
                mask_where: str, mask: Sequence[MaskEntry],
                findings: List[EquivalenceFinding], masked: List[MaskedDifference]) -> None:
    va, vb = _group_values(a), _group_values(b)
    for key in sorted(set(va) | set(vb)):
        la, lb = va.get(key, []), vb.get(key, [])
        key_where = _join(where, key)
        key_mask = _join(mask_where, key)
        if len(la) != len(lb):
            findings.append(EquivalenceFinding(path, EQ_VALUE_COUNT, key_where,
                                               str(len(la)), str(len(lb))))
            continue
        for i, (x, y) in enumerate(zip(la, lb)):
            if x == y:
                continue
            shown = key_where if len(la) == 1 else "%s[%d]" % (key_where, i)
            entry = _find_mask(mask, path, key_mask)
            if entry is not None and entry.kind == MASK_IGNORE:
                masked.append(MaskedDifference(path, shown, x, y, entry.reason))
                continue
            if entry is not None and entry.kind == MASK_TOLERANCE:
                nx, ny = _parse_number(x), _parse_number(y)
                if nx is not None and ny is not None and abs(nx - ny) <= entry.tolerance:
                    masked.append(MaskedDifference(path, shown, x, y, entry.reason))
                    continue
                findings.append(EquivalenceFinding(path, EQ_OUTSIDE_TOLERANCE, shown, x, y))
                continue
            findings.append(EquivalenceFinding(path, EQ_VALUE_DIFFERS, shown, x, y))

    ga, gb = _group_children(a), _group_children(b)
    for label in sorted(set(ga) | set(gb)):
        ca, cb = ga.get(label, []), gb.get(label, [])
        for i in range(max(len(ca), len(cb))):
            shown = _join(where, label if max(len(ca), len(cb)) == 1 else "%s[%d]" % (label, i))
            if i >= len(ca) or i >= len(cb):
                findings.append(EquivalenceFinding(path, EQ_NODE_MISSING, shown,
                                                   "present" if i < len(ca) else None,
                                                   "present" if i < len(cb) else None))
                continue
            _diff_nodes(path, ca[i], cb[i], shown, _join(mask_where, label), mask,
                        findings, masked)


def _as_configNode_text(data: bytes) -> Tuple[Optional[str], str]:
    """``(text, error)``: the ConfigNode text of a file, or None for a file compared as
    bytes. A ``PSN0`` snapshot is inflated; an undecodable one is an error."""
    if data[:4] == SNAPSHOT_MAGIC:
        try:
            return resourceq.decode_snapshot_sidecar(data), ""
        except Exception as exc:  # noqa: BLE001
            return None, "snapshot not decodable (%s)" % exc
    if _is_binary(data):
        return None, ""
    try:
        return data.decode("utf-8").lstrip("\ufeff"), ""
    except UnicodeDecodeError:
        return None, ""


def save_equivalence_files(files_a: Mapping[str, bytes], files_b: Mapping[str, bytes],
                           mask: Sequence[MaskEntry] = EQUIVALENCE_MASK) -> EquivalenceResult:
    """The pure canonical diff of two produced saves held in memory."""
    na = {_norm_path(p): d for p, d in files_a.items()}
    nb = {_norm_path(p): d for p, d in files_b.items()}
    errors: List[str] = ["mask: " + p for p in validate_mask(mask)]
    for side, files in (("a", na), ("b", nb)):
        if SAVE_FILE not in files:
            errors.append("save %s has no %s" % (side, SAVE_FILE))

    findings: List[EquivalenceFinding] = []
    masked: List[MaskedDifference] = []
    pa, pb = set(_compared_paths(na)), set(_compared_paths(nb))
    compared = 0
    for path in sorted(pa | pb):
        if path not in pa or path not in pb:
            findings.append(EquivalenceFinding(path, EQ_FILE_MISSING, "",
                                               "present" if path in pa else None,
                                               "present" if path in pb else None))
            continue
        compared += 1
        da, db = na[path], nb[path]
        ta, ea = _as_configNode_text(da)
        tb, eb = _as_configNode_text(db)
        if ea or eb:
            errors.extend("%s (%s): %s" % (path, side, e)
                          for side, e in (("a", ea), ("b", eb)) if e)
            continue
        if ta is not None and tb is not None:
            ra, rb = saveparse.parse_sfs(ta), saveparse.parse_sfs(tb)
            if ra.ok and rb.ok:
                _diff_nodes(path, ra.root, rb.root, "", "", mask, findings, masked)
                continue
            if da == db:
                continue  # identical text that is not ConfigNode: nothing to canonicalise
            findings.append(EquivalenceFinding(path, EQ_PARSE_ERROR, "",
                                               ra.error or None, rb.error or None))
            continue
        if da != db:
            findings.append(EquivalenceFinding(path, EQ_BYTES_DIFFER, "",
                                               "%d bytes" % len(da), "%d bytes" % len(db)))

    if errors:
        status = STATUS_ERROR
    elif findings:
        status = STATUS_FAIL
    else:
        status = STATUS_PASS
    return EquivalenceResult(status=status, findings=tuple(findings), masked=tuple(masked),
                             files_compared=compared, errors=tuple(errors))


def save_equivalence(save_a: str, save_b: str,
                     mask: Sequence[MaskEntry] = EQUIVALENCE_MASK) -> EquivalenceResult:
    """Canonical diff of two produced save directories (see ``save_equivalence_files``)."""
    return save_equivalence_files(read_save_files(save_a), read_save_files(save_b), mask)
