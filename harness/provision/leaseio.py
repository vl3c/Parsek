"""Copy-lease FILE I/O, shared by run.py, provision.py, the parallel runner and
tools/ksp_lease.py. The decisions are pure and live in ``harness/lib/leases.py``;
see its docstring for the contract.

Two jobs:

- Read and write the lease registry (``automation/.ksp-lease-<slot>.json``) and
  the worktree half (``harness/.ksp-lease.json``), atomically (tmp + replace).
- ``acquire_machine``: take the machine lock the way every machine owner does
  (``machinelock.acquire``) and then refuse, releasing it again, while any live
  lease exists. A claim is written only while its writer holds the machine lock,
  and a machine owner checks the registry only while it holds the machine lock,
  so a lease and a machine owner can never both hold the PC.
"""

from __future__ import annotations

import json
import os
import sys
from typing import Callable, Dict, Optional, Tuple

_LIB_DIR = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "lib"))
if _LIB_DIR not in sys.path:
    sys.path.append(_LIB_DIR)

import leases  # noqa: E402
import machinelock  # noqa: E402
import slots  # noqa: E402

REFUSED_LEASED = "refused-leased"


def _read_json(path: str) -> Optional[Dict]:
    try:
        with open(path, "rb") as fh:
            raw = fh.read()
    except OSError:
        return None
    try:
        obj = json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return None
    return obj if isinstance(obj, dict) else None


def _write_json_atomic(path: str, obj: Dict) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = "%s.tmp-%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(obj, fh, indent=1, sort_keys=True)
        fh.write("\n")
    os.replace(tmp, path)


def read_leases(umbrella_root: str) -> Dict[str, Optional[Dict]]:
    """slot -> parsed payload (None when unreadable) for every registry file."""
    auto = os.path.join(umbrella_root, slots.AUTOMATION_DIR)
    try:
        names = sorted(os.listdir(auto))
    except OSError:
        return {}
    out: Dict[str, Optional[Dict]] = {}
    for name in names:
        slot = leases.slot_from_lease_name(name)
        if slot is None:
            continue
        out[slot] = _read_json(os.path.join(auto, name))
    return out


def read_lease(umbrella_root: str, slot: str) -> Optional[Dict]:
    return _read_json(leases.lease_path(umbrella_root, slot))


def write_lease(umbrella_root: str, lease: Dict) -> str:
    path = leases.lease_path(umbrella_root, str(lease["slot"]))
    _write_json_atomic(path, lease)
    return path


def remove_lease(umbrella_root: str, slot: str) -> bool:
    try:
        os.remove(leases.lease_path(umbrella_root, slot))
        return True
    except OSError:
        return False


def read_worktree_ref(worktree_root: str) -> Optional[Dict]:
    return leases.parse_ksp_lease_ref(_read_json(leases.worktree_lease_path(worktree_root)))


def write_worktree_ref(worktree_root: str, lease: Dict) -> str:
    path = leases.worktree_lease_path(worktree_root)
    _write_json_atomic(path, leases.worktree_ref(lease))
    return path


def remove_worktree_ref(worktree_root: str) -> bool:
    try:
        os.remove(leases.worktree_lease_path(worktree_root))
        return True
    except OSError:
        return False


def first_live_lease(umbrella_root: str, now: float) -> Optional[Dict]:
    live = leases.live_leases(read_leases(umbrella_root), now)
    if not live:
        return None
    return live[sorted(live)[0]]


def resolve(worktree_root: str, umbrella_root: str, now: float
            ) -> Tuple["leases.UseDecision", Optional[Dict]]:
    """This worktree's lease decision and the registry payload it was judged against."""
    ref = read_worktree_ref(worktree_root)
    lease = read_lease(umbrella_root, str(ref["slot"])) if ref else None
    return leases.decide_use(ref, lease, now), lease


def renew_if_ours(umbrella_root: str, token: str, slot: str, now: float,
                  ttl_seconds: Optional[int] = None,
                  pin: Optional[Tuple[Optional[str], str, str]] = None) -> Optional[Dict]:
    """Re-read the registry entry and, when it still carries ``token`` and is live,
    renew it (and optionally re-pin: ``pin = (sha, source, iso)``). Returns the
    written payload, or None when the lease is no longer ours. The re-read and the
    replace are a TOCTOU pair, as in ``machinelock.heartbeat``; reaching the window
    needs the lease to expire under its live owner, which every renew pushes away."""
    current = read_lease(umbrella_root, slot)
    if not current or current.get("token") != token or not leases.is_live(current, now):
        return None
    updated = leases.renewed(current, now, ttl_seconds)
    if pin is not None:
        updated = leases.with_pin(updated, pin[0], pin[1], pin[2])
    write_lease(umbrella_root, updated)
    return updated


def acquire_machine(
    lock_path: str,
    umbrella_root: str,
    payload_fn: Callable[[], Dict],
    now_fn: Callable[[], float],
    pid_alive_fn: Callable[[int], bool],
    lease_seconds=None,
    log_fn: Optional[Callable[[str, str], None]] = None,
) -> Tuple[Optional[str], str, Optional[Dict]]:
    """``machinelock.acquire`` for a MACHINE owner, refusing on a live copy lease.

    Returns what ``machinelock.acquire`` returns; on a live lease the lock just
    taken is released again and the reason is ``REFUSED_LEASED`` with a
    holder-shaped dict naming the lease (``leases.holder_for_lease``)."""
    kwargs = {} if lease_seconds is None else {"lease_seconds": lease_seconds}
    path, reason, holder = machinelock.acquire(
        lock_path, payload_fn=payload_fn, now_fn=now_fn, pid_alive_fn=pid_alive_fn,
        log_fn=log_fn, **kwargs)
    if path is None:
        return path, reason, holder
    live = first_live_lease(umbrella_root, now_fn())
    if live is None:
        return path, reason, holder
    machinelock.release(path, log_fn=log_fn)
    if log_fn:
        log_fn("Warn", "machine lock given back: a copy lease is live (%s); the machine is "
                       "shared by leases until they are released or expire"
               % leases.describe_lease(live, now_fn()))
    return None, REFUSED_LEASED, leases.holder_for_lease(live)
