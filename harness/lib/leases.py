"""Copy leases: one agent thread owns one KSP install copy across many calls (pure).

A LEASE gives one worktree (one agent thread) a single install copy (a slot,
``lib/slots.py``) for as long as its work takes: several ``provision.py`` and
``run.py`` calls, with gaps between them. Two threads holding leases on two
copies fly at the same time, each on its own Parsek.dll, and neither can
provision over the other's copy.

How the pieces fit (I/O lives in ``provision/leaseio.py`` and
``tools/ksp_lease.py``; this module only decides):

- A lease file ``automation/.ksp-lease-<slot>.json`` (the REGISTRY entry) holds
  the slot, a random token, the owner label, the worktree, when it was claimed
  and last renewed, its time to live, and the Parsek.dll sha256 the owner
  pinned. The worktree keeps its half in ``harness/.ksp-lease.json`` (gitignored):
  the slot and the token. ``run.py --lease`` and ``provision.py --lease`` read
  that file, so a thread never has to carry the token across shell calls.
- A lease is LIVE until ``renewedAt + ttlSeconds``. Holder pids are NOT used:
  a lease spans processes on purpose (it covers the gaps between calls), so
  only the time to live, an explicit release or a renew decides. Every leased
  run.py renews it at each attempt boundary.
- A claim is made while holding the machine lock, and every machine-lock
  holder (a serial run.py, a plain provision.py, the parallel runner) refuses
  while any live lease exists, checked AFTER it took the machine lock. So a
  lease and a machine owner can never both hold the PC.
- At most ``cap`` live leases at once (default 2: the owner's "two KSPs while I
  use the PC" rule; 3 when the owner is away).
- A lane that moves the cursor or takes the foreground (``parallel.exclusive_reason``)
  cannot fly under a lease; it needs the machine (no live lease).
- The DLL pin: a claim pins the copy's deployed Parsek.dll (when it has one),
  and a provision under the lease re-pins what it deployed. A leased run admits
  an instance only when its deployed DLL equals the pin, so a clobbered copy is
  refused before boot instead of flying somebody else's build.
"""

import os
import secrets
from dataclasses import dataclass
from typing import Dict, Iterable, List, Mapping, Optional, Sequence, Tuple

import slots

LEASE_SCHEMA = "parsek-ksp-lease/1"
LEASE_PREFIX = ".ksp-lease-"
LEASE_SUFFIX = ".json"

# The worktree half: harness/.ksp-lease.json, relative to the worktree root.
WORKTREE_LEASE_RELPATH = ("harness", ".ksp-lease.json")

DEFAULT_TTL_SECONDS = 3 * 3600
MAX_TTL_SECONDS = 12 * 3600
DEFAULT_CAP = 2
MAX_CAP = 3

# The admission diff field a leased run reports when the deployed DLL is not the pin.
LEASE_DLL_DIFF_FIELD = "parsek.dllSha256 (lease pin)"

DLL_SOURCE_CLAIM = "claim"
DLL_SOURCE_PROVISION = "provision"

# decide_claim reasons
CLAIM_NEW = "claimed"
CLAIM_ALREADY_HELD = "already-held"
CLAIM_CAP_REACHED = "cap-reached"
CLAIM_SLOT_LEASED = "slot-leased"
CLAIM_NO_FREE_COPY = "no-free-copy"
CLAIM_HOLDS_OTHER_SLOT = "holds-another-slot"

# decide_use reasons
USE_OK = "ok"
USE_NO_WORKTREE_LEASE = "no-worktree-lease"
USE_LEASE_MISSING = "lease-missing"
USE_TOKEN_MISMATCH = "token-mismatch"
USE_EXPIRED = "lease-expired"


class LeaseError(ValueError):
    """A lease argument the contract does not allow."""


# ---------------------------------------------------------------------------
# Paths and names
# ---------------------------------------------------------------------------


def lease_file_name(slot: str) -> str:
    slots.parse_slot_name(slot)
    return LEASE_PREFIX + slot + LEASE_SUFFIX


def lease_path(umbrella_root: str, slot: str) -> str:
    return os.path.join(umbrella_root, slots.AUTOMATION_DIR, lease_file_name(slot))


def slot_from_lease_name(filename: str) -> Optional[str]:
    """The slot a registry file name belongs to, or None (temp files, other names)."""
    if not (filename.startswith(LEASE_PREFIX) and filename.endswith(LEASE_SUFFIX)):
        return None
    slot = filename[len(LEASE_PREFIX):-len(LEASE_SUFFIX)]
    try:
        slots.parse_slot_name(slot)
    except slots.SlotError:
        return None
    return slot


def worktree_lease_path(worktree_root: str) -> str:
    return os.path.join(worktree_root, *WORKTREE_LEASE_RELPATH)


def _path_key(path: Optional[str]) -> str:
    if not path:
        return ""
    return os.path.normcase(os.path.normpath(os.path.abspath(path)))


def same_worktree(a: Optional[str], b: Optional[str]) -> bool:
    return bool(a) and bool(b) and _path_key(a) == _path_key(b)


def new_token() -> str:
    return secrets.token_hex(16)


def clamp_ttl(seconds) -> int:
    try:
        value = int(float(seconds))
    except (TypeError, ValueError):
        raise LeaseError("ttl must be a number of seconds, got %r" % (seconds,))
    if value < 60 or value > MAX_TTL_SECONDS:
        raise LeaseError("ttl must be 60..%d seconds, got %d" % (MAX_TTL_SECONDS, value))
    return value


def clamp_cap(value) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 1 or value > MAX_CAP:
        raise LeaseError("cap must be an int in 1..%d, got %r" % (MAX_CAP, value))
    return value


# ---------------------------------------------------------------------------
# Lease payloads
# ---------------------------------------------------------------------------


def _num(value) -> Optional[float]:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    return None


def expires_at(lease: Optional[Mapping]) -> Optional[float]:
    """Epoch seconds the lease ends at, or None for a malformed payload."""
    if not isinstance(lease, Mapping):
        return None
    renewed = _num(lease.get("renewedAt"))
    ttl = _num(lease.get("ttlSeconds"))
    if renewed is None or ttl is None or ttl <= 0:
        return None
    return renewed + ttl


def is_live(lease: Optional[Mapping], now: float) -> bool:
    """A well-formed lease before its expiry. A malformed or expired one is stale
    and blocks nobody (the next claim of that slot replaces it)."""
    end = expires_at(lease)
    if end is None:
        return False
    if not lease.get("token") or not lease.get("slot"):
        return False
    return now < end


def live_leases(leases: Mapping[str, Optional[Mapping]], now: float) -> Dict[str, Mapping]:
    """slot -> lease for every live lease, keyed by the FILE's slot (a payload
    whose slot disagrees with its file name is treated as stale)."""
    out: Dict[str, Mapping] = {}
    for slot, lease in leases.items():
        if lease is not None and lease.get("slot") == slot and is_live(lease, now):
            out[slot] = lease
    return out


def build_lease(slot: str, token: str, owner: str, worktree: str, now: float, now_iso: str,
                ttl_seconds: int = DEFAULT_TTL_SECONDS, dll_sha256: Optional[str] = None,
                ) -> Dict:
    slots.parse_slot_name(slot)
    if not owner or not owner.strip():
        raise LeaseError("a lease needs an owner label (the thread or task name)")
    lease = {
        "schema": LEASE_SCHEMA,
        "slot": slot,
        "token": token,
        "owner": owner.strip(),
        "worktree": worktree,
        "claimedAt": float(now),
        "claimedIso": now_iso,
        "renewedAt": float(now),
        "ttlSeconds": int(ttl_seconds),
        "dllSha256": dll_sha256.lower() if dll_sha256 else None,
        "dllSource": DLL_SOURCE_CLAIM if dll_sha256 else None,
        "pinnedIso": now_iso if dll_sha256 else None,
    }
    return lease


def renewed(lease: Mapping, now: float, ttl_seconds: Optional[int] = None) -> Dict:
    """A copy renewed at ``now`` (optionally with a new time to live)."""
    out = dict(lease)
    out["renewedAt"] = float(now)
    if ttl_seconds is not None:
        out["ttlSeconds"] = int(ttl_seconds)
    return out


def with_pin(lease: Mapping, dll_sha256: Optional[str], source: str, now_iso: str) -> Dict:
    out = dict(lease)
    out["dllSha256"] = dll_sha256.lower() if dll_sha256 else None
    out["dllSource"] = source if dll_sha256 else None
    out["pinnedIso"] = now_iso if dll_sha256 else None
    return out


def worktree_ref(lease: Mapping) -> Dict:
    """The worktree half of a lease: what ``harness/.ksp-lease.json`` holds."""
    return {"schema": LEASE_SCHEMA, "slot": lease.get("slot"), "token": lease.get("token"),
            "owner": lease.get("owner")}


def holder_for_lease(lease: Mapping) -> Dict:
    """A machine-lock-shaped holder dict, so a refusal that a lease caused reads
    like every other holder line (machinelock.describe_holder)."""
    return {"pid": "lease", "worktree": lease.get("worktree") or "?",
            "selection": "lease:%s owner=%s" % (lease.get("slot"), lease.get("owner") or "?"),
            "startedIso": lease.get("claimedIso") or "?"}


def describe_lease(lease: Mapping, now: float) -> str:
    end = expires_at(lease)
    left = "?" if end is None else ("%dm" % max(0, int((end - now) // 60)))
    pin = lease.get("dllSha256")
    return "slot=%s owner=%s worktree=%s since=%s expiresIn=%s dll=%s%s" % (
        lease.get("slot"), lease.get("owner") or "?", lease.get("worktree") or "?",
        lease.get("claimedIso") or "?", left, (pin[:12] if pin else "unpinned"),
        (" (%s)" % lease.get("dllSource")) if pin and lease.get("dllSource") else "")


# ---------------------------------------------------------------------------
# Decisions
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ClaimDecision:
    ok: bool
    reason: str
    slot: Optional[str] = None
    detail: str = ""


def decide_claim(leases: Mapping[str, Optional[Mapping]], now: float, profile: str,
                 worktree: str, cap: int = DEFAULT_CAP, copy: Optional[int] = None,
                 provisioned_slots: Iterable[str] = ()) -> ClaimDecision:
    """Which slot this worktree gets, or why not.

    - A worktree holds at most one lease: claiming again returns the slot it
      already holds (the shell renews it), unless ``copy`` names another slot.
    - ``copy`` asks for one copy; without it the lowest-numbered free copy of
      ``profile`` is chosen, provisioned copies first (so a thread that wants to
      provision its own may still pass ``--copy``).
    - At most ``cap`` live leases machine-wide."""
    live = live_leases(leases, now)
    wanted = slots.slot_name(profile, copy) if copy is not None else None
    for slot, lease in sorted(live.items()):
        if same_worktree(lease.get("worktree"), worktree):
            if wanted is not None and wanted != slot:
                return ClaimDecision(False, CLAIM_HOLDS_OTHER_SLOT, slot,
                                     "this worktree already leases %s; release it first" % slot)
            return ClaimDecision(True, CLAIM_ALREADY_HELD, slot,
                                 "this worktree already leases %s" % slot)
    if wanted is not None and wanted in live:
        return ClaimDecision(False, CLAIM_SLOT_LEASED, wanted,
                             describe_lease(live[wanted], now))
    if len(live) >= cap:
        return ClaimDecision(False, CLAIM_CAP_REACHED, None,
                             "%d live lease(s), cap %d: %s" % (
                                 len(live), cap,
                                 "; ".join(describe_lease(l, now) for _s, l in sorted(live.items()))))
    if wanted is not None:
        return ClaimDecision(True, CLAIM_NEW, wanted, "requested copy %d" % copy)
    provisioned = set(provisioned_slots)
    candidates = [slots.slot_name(profile, k) for k in range(1, slots.MAX_COPIES + 1)]
    free = [s for s in candidates if s not in live]
    ready = [s for s in free if s in provisioned]
    if ready:
        return ClaimDecision(True, CLAIM_NEW, ready[0], "lowest free provisioned copy")
    if free:
        return ClaimDecision(True, CLAIM_NEW, free[0],
                             "no free provisioned copy; %s must be provisioned under the lease"
                             % free[0])
    return ClaimDecision(False, CLAIM_NO_FREE_COPY, None, "every copy of %s is leased" % profile)


@dataclass(frozen=True)
class UseDecision:
    ok: bool
    reason: str
    slot: Optional[str] = None
    detail: str = ""


def decide_use(ref: Optional[Mapping], lease: Optional[Mapping], now: float) -> UseDecision:
    """May this worktree (its ``harness/.ksp-lease.json`` = ``ref``) act under the
    lease registered for ``ref``'s slot (``lease``)?"""
    if not isinstance(ref, Mapping) or not ref.get("slot") or not ref.get("token"):
        return UseDecision(False, USE_NO_WORKTREE_LEASE, None,
                           "this worktree holds no lease (python tools/ksp_lease.py claim "
                           "--owner <thread>)")
    slot = str(ref.get("slot"))
    if not isinstance(lease, Mapping):
        return UseDecision(False, USE_LEASE_MISSING, slot,
                           "no lease file for %s (released, or never claimed); claim again"
                           % slot)
    if lease.get("token") != ref.get("token") or lease.get("slot") != slot:
        return UseDecision(False, USE_TOKEN_MISMATCH, slot,
                           "the lease of %s now belongs to %s" % (slot, describe_lease(lease, now)))
    if not is_live(lease, now):
        return UseDecision(False, USE_EXPIRED, slot,
                           "the lease of %s expired; claim again" % slot)
    return UseDecision(True, USE_OK, slot, describe_lease(lease, now))


def decide_dll_pin(lease: Mapping, deployed_sha256: Optional[str]) -> Optional[Tuple[str, str]]:
    """(expected, actual) when the deployed DLL is not the lease's pin, else None.
    An unpinned lease refuses too: a leased run flies only a DLL its owner chose."""
    pin = lease.get("dllSha256")
    actual = (deployed_sha256 or "").lower() or "missing"
    if not pin:
        return ("unpinned (provision the copy under the lease)", actual)
    if actual != str(pin).lower():
        return (str(pin).lower(), actual)
    return None


def others_live(leases: Mapping[str, Optional[Mapping]], now: float, slot: str) -> List[str]:
    """Live leased slots other than ``slot`` (who still needs the Unity prefs snapshot)."""
    return sorted(s for s in live_leases(leases, now) if s != slot)


def parse_ksp_lease_ref(obj) -> Optional[Dict]:
    """The worktree file's payload, or None when it is not a lease reference."""
    if not isinstance(obj, Mapping):
        return None
    slot = obj.get("slot")
    token = obj.get("token")
    if not isinstance(slot, str) or not isinstance(token, str) or not token:
        return None
    try:
        slots.parse_slot_name(slot)
    except slots.SlotError:
        return None
    return dict(obj)


def provisioned_slot_names(dir_names: Sequence[str], profile: str) -> List[str]:
    return slots.discover_slots(dir_names, [profile])
