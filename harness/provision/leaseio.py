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
import provlib  # noqa: E402
import slots  # noqa: E402

REFUSED_LEASED = "refused-leased"
REFUSED_SLOT_HELD = "refused-slot-held"


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


def _unreadable_stand_in(path: str, slot: str) -> Optional[Dict]:
    """A registry file that exists but cannot be read or parsed (an AV or backup
    hold, a sharing violation mid-replace, a torn write) FAILS CLOSED, as the
    machine lock does: it reads as a live lease until its mtime is older than the
    longest time to live, so it can never let a machine owner in by accident."""
    try:
        mtime = os.path.getmtime(path)
    except OSError:
        return None   # gone: released between the listing and the read
    return {"schema": leases.LEASE_SCHEMA, "slot": slot, "token": "unreadable-lease-file",
            "owner": "? (unreadable lease file %s)" % path, "worktree": "?",
            "claimedIso": "?", "renewedAt": float(mtime),
            "ttlSeconds": leases.MAX_TTL_SECONDS, "dllSha256": None, "unreadable": True}


def _read_lease_file(path: str, slot: str) -> Optional[Dict]:
    payload = _read_json(path)
    if payload is None and os.path.exists(path):
        payload = _read_json(path)   # one retry, as machinelock.heartbeat does
        if payload is None:
            return _unreadable_stand_in(path, slot)
    return payload


def read_leases(umbrella_root: str) -> Dict[str, Optional[Dict]]:
    """slot -> payload for every registry file (an unreadable one as a fail-closed
    stand-in, ``_unreadable_stand_in``)."""
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
        payload = _read_lease_file(os.path.join(auto, name), slot)
        if payload is not None:
            out[slot] = payload
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


def effective_leases(umbrella_root: str, now: float,
                     pid_alive_fn: Optional[Callable[[int], bool]] = None) -> Dict[str, Optional[Dict]]:
    """The registry as every decision must read it. With ``pid_alive_fn``, a lease
    whose time to live ran out while a live process still holds its copy's slot
    lock (a leased attempt longer than the lease, between two renews) is treated
    as live: the copy is in use, so it neither frees the machine nor its place
    under the cap."""
    registry = read_leases(umbrella_root)
    if pid_alive_fn is None or not registry:
        return registry
    expired = [slot for slot, lease in registry.items()
               if lease is not None and not leases.is_live(lease, now)]
    if not expired:
        return registry
    states = [st for st in machinelock.read_slot_locks(umbrella_root) if st.slot in expired]
    for st in provlib.live_slot_locks(states, os.getpid(), now, pid_alive_fn):
        lease = dict(registry[st.slot] or {})
        lease["slot"] = st.slot
        lease["token"] = lease.get("token") or "expired-but-flying"
        lease["renewedAt"] = float(now)
        lease["ttlSeconds"] = max(1, int(lease.get("ttlSeconds") or 1))
        lease["owner"] = "%s (lease expired, its copy still flying)" % (lease.get("owner") or "?")
        lease["expiredButHeld"] = True
        registry[st.slot] = lease
    return registry


def first_live_lease(umbrella_root: str, now: float,
                     pid_alive_fn: Optional[Callable[[int], bool]] = None) -> Optional[Dict]:
    """The first live lease (``effective_leases``), or None."""
    live = leases.live_leases(effective_leases(umbrella_root, now, pid_alive_fn), now)
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
                  pin: Optional[Tuple[Optional[str], str, str]] = None,
                  holding_slot_lock: bool = False) -> Optional[Dict]:
    """Re-read the registry entry and, when it still carries ``token`` and is live,
    renew it (and optionally re-pin: ``pin = (sha, source, iso)``). Returns the
    written payload, or None when the lease is no longer ours. The re-read and the
    replace are a TOCTOU pair, as in ``machinelock.heartbeat``; reaching the window
    needs the lease to expire under its live owner, which every renew pushes away."""
    current = read_lease(umbrella_root, slot)
    if current is None and os.path.exists(leases.lease_path(umbrella_root, slot)):
        current = read_lease(umbrella_root, slot)   # one retry on a transient hold
    if not current or current.get("token") != token or current.get("unreadable"):
        return None
    if not leases.is_live(current, now) and not holding_slot_lock:
        return None
    # An expired lease whose caller still holds the copy's slot lock is renewed:
    # while that lock is held no claim could take the copy (effective_leases
    # counts it live), so the token still names the only owner.
    updated = leases.renewed(current, now, ttl_seconds)
    if pin is not None:
        updated = leases.with_pin(updated, pin[0], pin[1], pin[2])
    try:
        write_lease(umbrella_root, updated)
    except OSError:
        # The lease is still ours a moment ago; a renew that could not write
        # leaves it as it was (the next boundary retries), but a pin that could
        # not be written is reported as not written.
        return None if pin is not None else dict(current)
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
    live = first_live_lease(umbrella_root, now_fn(), pid_alive_fn)
    if live is None:
        # No lease. A live slot lock with the machine lock free belongs to nobody
        # legitimate (a machine owner takes its slot locks AFTER this), so refuse
        # on it too: whatever holds it is flying a KSP this owner cannot see.
        states = machinelock.read_slot_locks(umbrella_root)
        held = provlib.live_slot_locks(states, os.getpid(), now_fn(), pid_alive_fn)
        if not held:
            return path, reason, holder
        machinelock.release(path, log_fn=log_fn)
        if log_fn:
            log_fn("Warn", "machine lock given back: a live slot lock is held with the machine "
                           "free (%s)" % "; ".join(provlib.describe_slot_lock(st) for st in held))
        lock = held[0].lock or {}
        return None, REFUSED_SLOT_HELD, {
            "pid": lock.get("pid"), "worktree": lock.get("worktree") or "?",
            "selection": "slot %s: %s" % (held[0].slot, lock.get("selection") or "?"),
            "startedIso": lock.get("startedIso") or "?"}
    machinelock.release(path, log_fn=log_fn)
    if log_fn:
        log_fn("Warn", "machine lock given back: a copy lease is live (%s); the machine is "
                       "shared by leases until they are released or expire"
               % leases.describe_lease(live, now_fn()))
    return None, REFUSED_LEASED, leases.holder_for_lease(live)
