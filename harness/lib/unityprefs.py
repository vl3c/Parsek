"""Unity player prefs hygiene for harness runs (pure).

Every KSP on the PC, the owner's Steam play install included, shares ONE
registry key for Unity's screen prefs (HKCU\\Software\\Squad\\Kerbal Space
Program), and every KSP quit writes its own window mode back into it. A harness
run therefore used to leave the play install at the harness window size. The
holder of the machine lock (a serial run.py, or the parallel runner)
snapshots the Unity screen values at its start and restores them after its
last KSP exited. The snapshot file doubles as the crash marker: one left by a
run that died is restored before a new snapshot is taken.

This module decides; the winreg / file shell is harness/tools/unity_prefs.py.

Registry value types are stored by their winreg number: REG_SZ 1,
REG_EXPAND_SZ 2, REG_BINARY 3, REG_DWORD 4, REG_MULTI_SZ 7, REG_QWORD 11.
Unity writes ints as REG_DWORD, floats as REG_QWORD and strings as REG_BINARY.

ASCII only; stdlib only.
"""

from __future__ import annotations

import json
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

PREFS_KEY = r"Software\Squad\Kerbal Space Program"
VALUE_PREFIXES: Tuple[str, ...] = ("Screenmanager", "UnitySelectMonitor")
SNAPSHOT_SCHEMA = "parsek-unity-prefs/1"

REG_SZ = 1
REG_EXPAND_SZ = 2
REG_BINARY = 3
REG_DWORD = 4
REG_MULTI_SZ = 7
REG_QWORD = 11
_INT_TYPES = (REG_DWORD, REG_QWORD)
_STR_TYPES = (REG_SZ, REG_EXPAND_SZ)

# One registry value: (name, data, type) in winreg's own shapes (bytes for
# REG_BINARY, int for DWORD / QWORD, str for SZ, list of str for MULTI_SZ).
RegValue = Tuple[str, object, int]


class SnapshotError(ValueError):
    """A snapshot file that cannot be read back into registry values."""


def is_tracked_name(name: str) -> bool:
    return isinstance(name, str) and name.startswith(VALUE_PREFIXES)


def select_tracked(values: Iterable[RegValue]) -> List[RegValue]:
    """The values of the key run.py owns (Screenmanager* / UnitySelectMonitor*),
    sorted by name."""
    return sorted((v for v in values if is_tracked_name(v[0])), key=lambda v: v[0])


def _encode_data(data: object, reg_type: int) -> object:
    if reg_type == REG_BINARY:
        if not isinstance(data, (bytes, bytearray)):
            raise SnapshotError("REG_BINARY data is not bytes: %r" % (data,))
        return bytes(data).hex()
    if reg_type in _INT_TYPES:
        if isinstance(data, bool) or not isinstance(data, int):
            raise SnapshotError("integer registry data is not an int: %r" % (data,))
        return data
    if reg_type in _STR_TYPES:
        if not isinstance(data, str):
            raise SnapshotError("string registry data is not a str: %r" % (data,))
        return data
    if reg_type == REG_MULTI_SZ:
        if not isinstance(data, (list, tuple)) or not all(isinstance(s, str) for s in data):
            raise SnapshotError("REG_MULTI_SZ data is not a list of str: %r" % (data,))
        return list(data)
    raise SnapshotError("unsupported registry value type %r" % (reg_type,))


def _decode_data(data: object, reg_type: int) -> object:
    if reg_type == REG_BINARY:
        if not isinstance(data, str):
            raise SnapshotError("REG_BINARY data is not a hex string")
        try:
            return bytes.fromhex(data)
        except ValueError as exc:
            raise SnapshotError("REG_BINARY data is not hex: %s" % exc)
    return _encode_data(data, reg_type)


def encode_snapshot(values: Sequence[RegValue], taken_utc: str, pid: int) -> str:
    """The snapshot file text (JSON). Only tracked values are kept."""
    rows = [{"name": n, "type": int(t), "data": _encode_data(d, int(t))}
            for n, d, t in select_tracked(values)]
    return json.dumps({"schema": SNAPSHOT_SCHEMA, "key": PREFS_KEY, "takenUtc": taken_utc,
                       "pid": int(pid), "values": rows}, indent=1, sort_keys=True) + "\n"


def decode_snapshot(text: str) -> List[RegValue]:
    """Registry values from a snapshot file. Raises SnapshotError on any doubt,
    so a damaged file is never half-applied."""
    try:
        doc = json.loads(text)
    except ValueError as exc:
        raise SnapshotError("snapshot is not JSON: %s" % exc)
    if not isinstance(doc, dict) or doc.get("schema") != SNAPSHOT_SCHEMA:
        raise SnapshotError("snapshot schema is not %s" % SNAPSHOT_SCHEMA)
    rows = doc.get("values")
    if not isinstance(rows, list):
        raise SnapshotError("snapshot has no values list")
    out: List[RegValue] = []
    for row in rows:
        if not isinstance(row, dict):
            raise SnapshotError("snapshot value is not an object")
        name, reg_type = row.get("name"), row.get("type")
        if not is_tracked_name(name):
            raise SnapshotError("snapshot names an untracked value %r" % (name,))
        if isinstance(reg_type, bool) or not isinstance(reg_type, int):
            raise SnapshotError("snapshot value %s has no integer type" % name)
        out.append((name, _decode_data(row.get("data"), reg_type), reg_type))
    return select_tracked(out)


def plan_restore(snapshot: Sequence[RegValue], current: Sequence[RegValue]
                 ) -> Tuple[List[RegValue], List[str]]:
    """(values to write, names to delete) that put the tracked values back to
    the snapshot: every snapshot value that is missing or differs (data or
    type) is written; every tracked value the snapshot did not hold is deleted
    (a KSP added it during the run). Untracked values are never touched."""
    want: Dict[str, RegValue] = {v[0]: v for v in select_tracked(snapshot)}
    have: Dict[str, RegValue] = {v[0]: v for v in select_tracked(current)}
    writes = [want[n] for n in sorted(want)
              if n not in have or have[n][1] != want[n][1] or have[n][2] != want[n][2]]
    deletes = sorted(n for n in have if n not in want)
    return writes, deletes


def describe(values: Sequence[RegValue], limit: int = 6) -> str:
    """A short log rendering: names with int data inline, others by type."""
    parts = []
    for name, data, reg_type in select_tracked(values)[:limit]:
        if reg_type in _INT_TYPES:
            parts.append("%s=%s" % (name, data))
        else:
            parts.append("%s=<type %d>" % (name, reg_type))
    more = len(select_tracked(values)) - limit
    if more > 0:
        parts.append("+%d more" % more)
    return ", ".join(parts) if parts else "none"


START_SNAPSHOT = "snapshot"
START_RECOVER = "recover-then-snapshot"
START_DISCARD = "discard-then-snapshot"


def start_action(marker_exists: bool, decoded: Optional[List[RegValue]]) -> str:
    """What a start does with the snapshot path it finds:
    'snapshot' (no marker), 'recover-then-snapshot' (a readable marker from a
    run that died) or 'discard-then-snapshot' (a marker that does not decode;
    it cannot be applied, so it is set aside and a fresh snapshot taken)."""
    if not marker_exists:
        return START_SNAPSHOT
    if decoded is None:
        return START_DISCARD
    return START_RECOVER
