"""Pure race evaluator, part 1: the rival-id leak scan and the save-equivalence diff.

Multiplayer plan task T-05 (``docs/dev/plans/multiplayer/test-and-harness.md``). The
``raceParse`` verifier row (T-38, ``evaluate_race_parse`` at the end of this file, called by
``run.py``) and the structure facets (T-21) build on this module.

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
``persistent.sfs``, a folder that cannot be listed, a file that cannot be read, one folder
reached twice through a link, an undecodable snapshot, a bad mask). A save that cannot be
read never reads as clean; ``walk_folder`` states the link and junction rule.

Layout facts consumed here (design 4.1 and 6.1):

  - shared folder: ``players/<playerId>/`` per player; ``payload/<recordingId>.<hash><suffix>``
    and ``payload/trees/<treeId>.<hash>.cfg``, where ``<hash>`` is
    ``SidecarContentHash`` (16 lower-case hex characters);
  - safe-write leftovers (``.tmp``, ``.bak.<guid>``) are not part of the folder's grammar
    and are ignored;
  - produced save: rival and own-mirror files only under ``Parsek/Race/``.

The I/O shell is two small readers (``list_folder``, ``read_save_tree``); every decision is
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
                    rival_ids: Union[RivalIds, Iterable[str]],
                    read_errors: Sequence[str] = ()) -> LeakScanResult:
    """The pure scan over a produced save held in memory: ``files`` maps each
    save-relative path to its bytes. Every path outside ``Parsek/Race/`` is searched
    (its name, its bytes, and the inflated text of a ``PSN0`` snapshot).
    ``read_errors`` (from ``read_save_tree``) make the result ERROR."""
    tagged = _tag_ids(rival_ids)
    kinds = {rid: kind for kind, rid in tagged}
    norm = {_norm_path(p): data for p, data in files.items()}
    errors: List[str] = list(read_errors)
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


class FolderReadError(Exception):
    """A folder could not be listed whole; ``problems`` names every part that failed."""

    def __init__(self, problems: Sequence[str]):
        super().__init__("; ".join(problems))
        self.problems = tuple(problems)


def walk_folder(root: str) -> Tuple[List[str], List[str]]:
    """``(files, problems)``: every file under ``root`` as a sorted ``/``-separated
    relative path, and every reason the listing is not whole.

    The rule, so no part of a save is skipped in silence: a directory that cannot be
    listed is a problem (``os.walk``'s default drops it); a symlinked directory, and on
    Windows a junction, is FOLLOWED (``followlinks=True``; ``os.walk`` follows junctions
    on Python 3.8 to 3.11 and, since 3.12, only with ``followlinks``), and a directory
    whose real path (``os.path.realpath``, which resolves both) was already walked is a
    problem, never a skip: a link loop, or one folder reached by two names (a recordings
    folder linked into ``Parsek/Race/`` would otherwise be read under the excluded name
    only). A missing or non-directory ``root`` is a problem too."""
    problems: List[str] = []
    out: List[str] = []
    if not os.path.isdir(root):
        return out, ["%s is not a readable directory" % root]

    def on_error(exc: OSError) -> None:
        where = getattr(exc, "filename", None) or root
        problems.append("cannot list %s (%s)" % (where, exc.__class__.__name__))

    seen = set()
    for dirpath, dirnames, filenames in os.walk(root, onerror=on_error, followlinks=True):
        rel_dir = os.path.relpath(dirpath, root).replace(os.sep, "/")
        real = os.path.normcase(os.path.realpath(dirpath))
        if real in seen:
            problems.append("directory %s was already walked under another name or is a "
                            "link loop" % rel_dir)
            dirnames[:] = []
            continue
        seen.add(real)
        dirnames.sort()
        for name in filenames:
            rel = os.path.relpath(os.path.join(dirpath, name), root)
            out.append(rel.replace(os.sep, "/"))
    return sorted(out), problems


def list_folder(root: str) -> List[str]:
    """Every file under ``root`` (see ``walk_folder`` for the link rule). Raises
    ``FolderReadError`` when the listing is not whole, so a caller never collects ids
    off part of a folder."""
    files, problems = walk_folder(root)
    if problems:
        raise FolderReadError(problems)
    return files


def read_save_tree(save_dir: str,
                   skip_race: bool = True) -> Tuple[Dict[str, bytes], Tuple[str, ...]]:
    """``(files, problems)``: the produced save as ``{relative path: bytes}`` and every
    directory or file that could not be listed or read. ``Parsek/Race/`` files are listed
    with empty bytes when ``skip_race`` (the scan and the diff never read them, and a
    rival cache can be large), so their count still shows."""
    rels, problems = walk_folder(save_dir)
    files: Dict[str, bytes] = {}
    for rel in rels:
        if skip_race and is_race_path(rel):
            files[rel] = b""
            continue
        try:
            with open(os.path.join(save_dir, *rel.split("/")), "rb") as fh:
                files[rel] = fh.read()
        except OSError as exc:
            problems.append("cannot read %s (%s)" % (rel, exc.__class__.__name__))
    return files, tuple(problems)


def leak_scan(save_dir: str, rival_ids: Union[RivalIds, Iterable[str]]) -> LeakScanResult:
    """Leak scan of a produced save directory (see ``leak_scan_files``); a save that
    could not be read whole reads ERROR."""
    files, problems = read_save_tree(save_dir)
    return leak_scan_files(files, rival_ids, read_errors=problems)


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
                           mask: Sequence[MaskEntry] = EQUIVALENCE_MASK,
                           read_errors: Sequence[str] = ()) -> EquivalenceResult:
    """The pure canonical diff of two produced saves held in memory. ``read_errors``
    (from ``read_save_tree`` of either save) make the result ERROR."""
    na = {_norm_path(p): d for p, d in files_a.items()}
    nb = {_norm_path(p): d for p, d in files_b.items()}
    errors: List[str] = list(read_errors) + ["mask: " + p for p in validate_mask(mask)]
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
    files_a, problems_a = read_save_tree(save_a)
    files_b, problems_b = read_save_tree(save_b)
    problems = (["save a: " + p for p in problems_a] + ["save b: " + p for p in problems_b])
    return save_equivalence_files(files_a, files_b, mask, read_errors=problems)


# ---------------------------------------------------------------------------
# The raceParse verifier row (T-38): the spec block, the save's own player, and the
# leak scan of every save a race lane produced.
# ---------------------------------------------------------------------------

# `[expectations.race]`. `gating = true` arms the leak scan (PARSEK-FAIL(race-isolation)
# on a leak, INVALID(race-parse) when the scan cannot prove anything); without it the
# row reports and never moves the verdict. T-21 adds the structure facet keys here.
RACE_BLOCK = "race"
GATING_KEY = "gating"
RACE_BLOCK_KEYS: Tuple[str, ...] = (GATING_KEY,)

# The race mark (multiplayer R1-03): one SCENARIO node of this name in the save's
# persistent.sfs, carrying the save's own player id. Mirrors RaceFormat.ScenarioModuleName
# and RaceLinkCodec.KeyPlayerId (test_raceparse.RaceMarkSourceSyncTests).
RACE_MARK_SCENARIO = "ParsekRaceScenario"
RACE_MARK_PLAYER_KEY = "playerId"

# Save-relative directories the HARNESS writes into a produced save (the offline
# analyzer's analysis/ folder). Never the game's output, so the scan skips them; a
# rival id there could only be an echo of one the scan already finds in the save.
HARNESS_WRITTEN_DIRS: Tuple[str, ...] = ("analysis/",)

# Findings listed per save in the row (the count is always whole).
ROW_FINDINGS_CAP = 20


def race_block_declared(expectations: Optional[Mapping]) -> bool:
    return isinstance(expectations, Mapping) and RACE_BLOCK in expectations


def race_gating_armed(expectations: Optional[Mapping]) -> bool:
    """True when ``[expectations.race]`` sets ``gating = true`` (the literal bool)."""
    if not isinstance(expectations, Mapping):
        return False
    block = expectations.get(RACE_BLOCK)
    return isinstance(block, Mapping) and block.get(GATING_KEY) is True


def validate_race_expectations(block) -> List[str]:
    """Errors for an ``[expectations.race]`` table (spec validation, pre-launch)."""
    where = "expectations.%s" % RACE_BLOCK
    if not isinstance(block, Mapping):
        return ["%s must be a table, got %s" % (where, type(block).__name__)]
    errors: List[str] = []
    for key in sorted(str(k) for k in block if k not in RACE_BLOCK_KEYS):
        errors.append("%s: unknown key %r (known: %s)"
                      % (where, key, ", ".join(RACE_BLOCK_KEYS)))
    if GATING_KEY in block and not isinstance(block[GATING_KEY], bool):
        errors.append("%s.%s must be true or false (true arms the leak scan), got %r"
                      % (where, GATING_KEY, block[GATING_KEY]))
    return errors


def own_player_id(save_text: Optional[str]) -> Tuple[Optional[str], str]:
    """``(own player id, problem)`` from a produced save's persistent.sfs text.

    No race mark: ``(None, "")``, a save that is in no race, so every player of the
    shared folder is a rival to it. One mark with one non-empty ``playerId``: that
    id. Anything else (no file, a file that does not parse, two marks, a mark with no
    or two ids) is a problem: the scan cannot tell own ids from rival ids."""
    if save_text is None:
        return None, "no %s" % SAVE_FILE
    parsed = saveparse.parse_sfs(save_text)
    if not parsed.ok:
        return None, "%s does not parse (%s)" % (SAVE_FILE, parsed.error)
    marks = [scenario for game in parsed.root.nodes_named("GAME")
             for scenario in game.nodes_named("SCENARIO")
             if scenario.value("name") == RACE_MARK_SCENARIO]
    if not marks:
        return None, ""
    if len(marks) > 1:
        return None, "%d SCENARIO nodes named %s" % (len(marks), RACE_MARK_SCENARIO)
    ids = [v for v in marks[0].values_named(RACE_MARK_PLAYER_KEY) if v]
    if len(ids) != 1:
        return None, ("the %s node carries %d non-empty %s values"
                      % (RACE_MARK_SCENARIO, len(ids), RACE_MARK_PLAYER_KEY))
    return ids[0], ""


def is_harness_written_path(rel_path: str) -> bool:
    path = _norm_path(rel_path)
    return any(path.startswith(prefix) for prefix in HARNESS_WRITTEN_DIRS)


@dataclass(frozen=True)
class RaceSaveInput:
    """One produced save as the shell read it: ``files`` from ``read_save_tree``
    (None when the save could not be read at all) and every read problem."""

    label: str
    files: Optional[Mapping[str, bytes]]
    problems: Tuple[str, ...] = ()


@dataclass(frozen=True)
class RaceSaveReport:
    label: str
    status: str
    own_player: Optional[str] = None
    ids_scanned: int = 0
    files_scanned: int = 0
    files_skipped_race: int = 0
    files_skipped_harness: int = 0
    findings: Tuple[LeakFinding, ...] = ()
    errors: Tuple[str, ...] = ()

    def to_dict(self) -> Dict:
        return {"save": self.label, "status": self.status, "ownPlayer": self.own_player,
                "idsScanned": self.ids_scanned, "filesScanned": self.files_scanned,
                "filesSkippedRace": self.files_skipped_race,
                "filesSkippedHarness": self.files_skipped_harness,
                "findingCount": len(self.findings),
                "findings": [f.describe() for f in self.findings[:ROW_FINDINGS_CAP]],
                "errors": list(self.errors)}


@dataclass(frozen=True)
class RaceParseResult:
    """The row's evaluation. ``status``: FAIL when any save carries a rival id
    (whatever else failed: a leak found is a leak), else ERROR when the folder or a
    save could not be read whole or a save's own player is unknown, else VACUOUS when
    a save had no rival id to look for, else PASS."""

    status: str
    gating: bool
    folder_files: int = 0
    folder_errors: Tuple[str, ...] = ()
    unrecognised: Tuple[str, ...] = ()
    saves: Tuple[RaceSaveReport, ...] = ()

    @property
    def leaked(self) -> bool:
        return any(s.findings for s in self.saves)

    @property
    def unevaluable(self) -> bool:
        """No leak found and the scan proved nothing whole (ERROR or VACUOUS)."""
        return not self.leaked and self.status in (STATUS_ERROR, STATUS_VACUOUS)

    @property
    def mismatches(self) -> List[str]:
        out: List[str] = ["race folder: %s" % e for e in self.folder_errors]
        for s in self.saves:
            out.extend("leak in save %s: %s" % (s.label, f.describe()) for f in s.findings)
            out.extend("save %s unreadable: %s" % (s.label, e) for e in s.errors)
            if s.status == STATUS_VACUOUS:
                out.append("save %s: no rival id to scan for" % s.label)
        return out


def _scan_one_save(save: RaceSaveInput, folder_listing: Sequence[str]) -> RaceSaveReport:
    if save.files is None:
        return RaceSaveReport(save.label, STATUS_ERROR,
                              errors=tuple(save.problems) or ("save not read",))
    files = {_norm_path(p): data for p, data in save.files.items()}
    sfs = files.get(SAVE_FILE)
    own, problem = own_player_id(None if sfs is None
                                 else sfs.decode("utf-8", errors="replace"))
    if problem:
        return RaceSaveReport(save.label, STATUS_ERROR,
                              errors=tuple(save.problems) + ("own player: " + problem,))
    scanned = {p: d for p, d in files.items() if not is_harness_written_path(p)}
    rivals = collect_rival_ids(folder_listing, own)
    res = leak_scan_files(scanned, rivals, read_errors=save.problems)
    return RaceSaveReport(save.label, res.status, own_player=own,
                          ids_scanned=res.ids_scanned, files_scanned=res.files_scanned,
                          files_skipped_race=res.files_skipped_race,
                          files_skipped_harness=len(files) - len(scanned),
                          findings=res.findings, errors=res.errors)


def evaluate_race_parse(expectations: Optional[Mapping],
                        folder_listing: Optional[Sequence[str]],
                        folder_errors: Sequence[str],
                        saves: Sequence[RaceSaveInput]) -> RaceParseResult:
    """The raceParse row over a race lane's snapshots: ``folder_listing`` is the
    shared-folder snapshot's file list (None when it could not be listed whole, with
    ``folder_errors`` saying why); each save is scanned for the ids of every player
    other than its own (``own_player_id``), so two saves of one lane each see the
    other's player as a rival."""
    gating = race_gating_armed(expectations)
    errors = tuple(folder_errors)
    if folder_listing is None:
        return RaceParseResult(STATUS_ERROR, gating,
                               folder_errors=errors or ("race folder snapshot not listed",))
    reports = tuple(_scan_one_save(s, folder_listing) for s in saves)
    unrecognised = tuple(collect_rival_ids(folder_listing, None).unrecognised)
    if not reports:
        errors = errors + ("no produced save to scan",)
    if any(r.findings for r in reports):
        status = STATUS_FAIL
    elif errors or any(r.status == STATUS_ERROR for r in reports):
        status = STATUS_ERROR
    elif any(r.status == STATUS_VACUOUS for r in reports):
        status = STATUS_VACUOUS
    else:
        status = STATUS_PASS
    return RaceParseResult(status, gating, folder_files=len(folder_listing),
                           folder_errors=errors, unrecognised=unrecognised, saves=reports)


# The classify_verdict flags the row raises (hlib.classify_verdict reads them), shared by
# run.py and the race-leak mutation check so both judge a result the same way.
FLAG_LEAK = "race_isolation_leak"
FLAG_UNEVALUABLE = "race_parse_unevaluable"


def verdict_flags(result: RaceParseResult) -> Dict[str, bool]:
    """Both flags are False unless the scan is armed: unarmed, the row only reports."""
    return {FLAG_LEAK: result.gating and result.leaked,
            FLAG_UNEVALUABLE: result.gating and result.unevaluable}
