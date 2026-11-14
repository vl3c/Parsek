"""Install slots for parallel harness runs (pure).

A SLOT is one KSP install copy the harness can fly a lane in. Copy 1 of a
profile is the profile's own ``instanceDir`` (``automation/<profile>``); copy
k >= 2 lives beside it as ``automation/<profile>-<k>``. The slot NAME is that
directory's basename, so ``stock-minimal`` is copy 1 and ``stock-minimal-2``
is copy 2. Copies 2..k are provisioned exactly like copy 1 (same stack, same
Parsek DLL) and differ only in their kRPC ports and their slot lock.

Every value here is derived, never read from disk, except ``discover_slots``
(which only lists directories handed to it by the caller). The shells
(``run.py``, ``provision.py``, ``tools/parallel_runner.py``) do the I/O.

Contract (plan: project file harness/parallel-runs-plan-2026-11-14.md):

- kRPC ports: ``PROFILE_PORT_BASE[profile] + 2 * (copy - 1)`` for the RPC port,
  plus one for the stream port. Copy 1 of stock-minimal keeps 50000/50001, the
  value every install carried before slots existed. Each profile owns a block of
  ``PORT_BLOCK`` ports, so no two slots of any profiles share a port.
- Slot lock: ``automation/.ksp-slot-<slot>.lock`` (the machinelock protocol).
  Every run.py that flies in an instance holds that instance's slot lock.
- Launch stamp: ``automation/.ksp-launch-stamp`` spaces KSP starts machine-wide.
- Unity prefs snapshot: ``automation/.unity-prefs-snapshot.json`` (crash-safe
  restore of the Screenmanager registry values every KSP on the PC shares).
"""

import os
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

AUTOMATION_DIR = "automation"

# Each profile owns PORT_BLOCK consecutive ports starting at its base.
PORT_BLOCK = 20
PROFILE_PORT_BASE: Dict[str, int] = {
    "stock-minimal": 50000,
    "stock-tree": 50020,
    "modded-compat": 50040,
}

# Copies a profile may have (ports for copy k must fit the profile's block).
MAX_COPIES = PORT_BLOCK // 2

SLOT_LOCK_PREFIX = ".ksp-slot-"
SLOT_LOCK_SUFFIX = ".lock"
LAUNCH_STAMP_NAME = ".ksp-launch-stamp"
UNITY_PREFS_SNAPSHOT_NAME = ".unity-prefs-snapshot.json"

# Minimum wall seconds between two KSP starts on the machine (fewer
# simultaneous focus grabs on the owner's desktop, less boot contention).
DEFAULT_LAUNCH_SPACING_SECONDS = 15.0


class SlotError(ValueError):
    """A slot name or copy number the contract does not allow."""


def _check_profile(profile: str) -> None:
    if profile not in PROFILE_PORT_BASE:
        raise SlotError("unknown profile %r (known: %s)"
                        % (profile, ", ".join(sorted(PROFILE_PORT_BASE))))


def _check_copy(copy: int) -> None:
    if not isinstance(copy, int) or isinstance(copy, bool) or copy < 1 or copy > MAX_COPIES:
        raise SlotError("copy must be an int in 1..%d, got %r" % (MAX_COPIES, copy))


def slot_name(profile: str, copy: int = 1) -> str:
    """``stock-minimal`` for copy 1, ``stock-minimal-2`` for copy 2, ..."""
    _check_profile(profile)
    _check_copy(copy)
    return profile if copy == 1 else "%s-%d" % (profile, copy)


def parse_slot_name(name: str) -> Tuple[str, int]:
    """Inverse of ``slot_name``: (profile, copy). Raises SlotError otherwise.

    Profiles are matched longest-first so a profile whose name ends in
    ``-<digits>`` could never be mistaken for a copy suffix."""
    if not isinstance(name, str) or not name:
        raise SlotError("empty slot name")
    for profile in sorted(PROFILE_PORT_BASE, key=len, reverse=True):
        if name == profile:
            return profile, 1
        prefix = profile + "-"
        if name.startswith(prefix):
            tail = name[len(prefix):]
            if tail.isdigit() and not tail.startswith("0"):
                copy = int(tail)
                if copy >= 2:
                    _check_copy(copy)
                    return profile, copy
    raise SlotError("not a slot name: %r" % name)


def instance_relpath(profile: str, copy: int = 1) -> Tuple[str, str]:
    """Path parts of the slot's install dir, relative to the umbrella root."""
    return (AUTOMATION_DIR, slot_name(profile, copy))


def instance_dir(umbrella_root: str, slot: str) -> str:
    """Absolute install dir of a slot under ``umbrella_root``."""
    profile, copy = parse_slot_name(slot)
    return os.path.abspath(os.path.join(umbrella_root, *instance_relpath(profile, copy)))


def krpc_ports(profile: str, copy: int = 1) -> Tuple[int, int]:
    """(rpc_port, stream_port) the slot's kRPC server is stamped with."""
    _check_profile(profile)
    _check_copy(copy)
    rpc = PROFILE_PORT_BASE[profile] + 2 * (copy - 1)
    return rpc, rpc + 1


def krpc_ports_for_slot(slot: str) -> Tuple[int, int]:
    profile, copy = parse_slot_name(slot)
    return krpc_ports(profile, copy)


def slot_lock_relpath(slot: str) -> Tuple[str, str]:
    parse_slot_name(slot)
    return (AUTOMATION_DIR, SLOT_LOCK_PREFIX + slot + SLOT_LOCK_SUFFIX)


def slot_lock_path(umbrella_root: str, slot: str) -> str:
    return os.path.join(umbrella_root, *slot_lock_relpath(slot))


def slot_from_lock_name(filename: str) -> Optional[str]:
    """The slot a lock file name belongs to, or None when it is not a slot lock."""
    if not (filename.startswith(SLOT_LOCK_PREFIX) and filename.endswith(SLOT_LOCK_SUFFIX)):
        return None
    slot = filename[len(SLOT_LOCK_PREFIX):-len(SLOT_LOCK_SUFFIX)]
    try:
        parse_slot_name(slot)
    except SlotError:
        return None
    return slot


def launch_stamp_path(umbrella_root: str) -> str:
    return os.path.join(umbrella_root, AUTOMATION_DIR, LAUNCH_STAMP_NAME)


def unity_prefs_snapshot_path(umbrella_root: str) -> str:
    return os.path.join(umbrella_root, AUTOMATION_DIR, UNITY_PREFS_SNAPSHOT_NAME)


def launch_wait_seconds(last_launch_epoch: Optional[float], now_epoch: float,
                        spacing_seconds: float = DEFAULT_LAUNCH_SPACING_SECONDS) -> float:
    """Seconds to wait before the next KSP start, given the last start's time.

    None (no stamp) or a stamp in the future by more than the spacing (a clock
    jump) waits 0, so a bad stamp can never stall a run."""
    if last_launch_epoch is None:
        return 0.0
    elapsed = now_epoch - last_launch_epoch
    if elapsed < -spacing_seconds:
        return 0.0
    return max(0.0, spacing_seconds - elapsed)


def same_instance_dir(a: str, b: str) -> bool:
    """True when two paths name the SAME install dir. Equality of the
    normalized real paths, never a prefix test: ``automation/stock-minimal`` is
    a string prefix of ``automation/stock-minimal-2``."""
    if not a or not b:
        return False
    return (os.path.normcase(os.path.realpath(os.path.abspath(a)))
            == os.path.normcase(os.path.realpath(os.path.abspath(b))))


def exe_belongs_to_instance(exe_path: Optional[str], instance: str) -> bool:
    """True when a KSP process's executable lives directly in ``instance``.
    An unknown exe path (None / empty) returns True: callers treat a KSP whose
    path they cannot read as busy, never as somebody else's."""
    if not exe_path:
        return True
    return same_instance_dir(os.path.dirname(exe_path), instance)


def discover_slots(dir_names: Iterable[str],
                   profiles: Optional[Sequence[str]] = None) -> List[str]:
    """Slot names among ``dir_names`` (the basenames under ``automation/``),
    ordered by profile then copy. The caller passes only directories that hold
    a ``KSP_x64.exe``; this function only parses names."""
    wanted = set(profiles) if profiles is not None else None
    found = []
    for name in dir_names:
        try:
            profile, copy = parse_slot_name(name)
        except SlotError:
            continue
        if wanted is not None and profile not in wanted:
            continue
        found.append((profile, copy, name))
    order = {p: i for i, p in enumerate(sorted(PROFILE_PORT_BASE))}
    found.sort(key=lambda t: (order[t[0]], t[1]))
    return [name for _, _, name in found]
