"""Machine-lock FILE I/O -- the thin shell half of the EC-10 exclusivity contract.

The DECISION half is pure and lives in ``provlib`` (``acquire_lock``,
``lock_lease_expired``, ``should_release``). This module owns only the file
operations, and exists so ``run.py`` and ``provision.py`` share ONE acquire
protocol instead of two.

WHY ONE MODULE. The first cut of the machine lock implemented acquire twice: a
careful bounded-retry version in run.py and a shorter one inline in
provision.py whose ``FileExistsError`` branch unconditionally ``os.replace``d
the lockfile. That branch was reachable with no stale lock at all -- provision
reads (no file, decision ``acquired-free``), a harness run creates its lock in
the gap, provision's exclusive create fails, and provision then overwrote the
LIVE run's lock and proceeded to DEPLOY over its instance. Two reviewers found
it independently. A protocol duplicated in two shells drifts; this module is the
structural fix, not just the patch.

THE PROTOCOL. Winning is decided by exactly one atomic operation: the
``O_CREAT|O_EXCL`` create. Everything else only ever REMOVES a lock already
judged stale, and never removes one it did not verify:

  1. Try the exclusive create. Success is the only way to hold the lock.
  2. On FileExistsError, read the file and run the pure predicate.
     - live holder within its lease -> REFUSE.
     - dead pid, or expired lease   -> stale, continue.
  3. Reclaim a stale lock by MOVING it aside (``os.replace`` to a pid-unique
     quarantine name) and then CHECKING that what we moved is byte-identical to
     what we judged. A plain ``os.remove`` here is the second race the reviewers
     found: two processes that both read the same stale lock both call remove,
     and the loser deletes the WINNER's freshly created lock, after which both
     believe they hold it. If the bytes differ, the file we moved is somebody's
     fresh lock -- restore it and refuse.
  4. Loop back to the exclusive create, which decides the winner atomically.

Bounded to ATTEMPTS tries, then refuses. Two racers that both loop on reclaim
livelock instead of serializing; refusing is always safe (a refusal costs a run,
a false acquire costs a corrupted flight and possibly a false PASS).

Stdlib only, no third-party deps. Windows is the target platform, so there is no
fcntl.

PORTABILITY NOTE, stated honestly because an earlier draft claimed the opposite:
the create and the payload write are two operations, so between a winner's
O_EXCL create and the close of its write the lockfile exists and is EMPTY. On
Windows the winner's open handle makes a racer's rename fail with a sharing
violation, which closes that window as a side effect of platform semantics
rather than by design. The protocol does not RELY on that -- an empty file is
never treated as a lock and never reclaimed (see the ``raw == b""`` branch) --
so the window is closed on POSIX too, where the rename would otherwise succeed.
"""

from __future__ import annotations

import json
import os
from typing import Callable, Dict, Optional, Tuple

import provlib

# Bounded retry. Each pass costs at most one liveness probe; three is enough to
# absorb a lost reclaim without becoming a spin.
ATTEMPTS = 3

# Outcomes returned alongside the path (None on refusal).
WON_FREE = "acquired-free"
WON_RECLAIMED = "reclaimed-stale"
REFUSED_LIVE = "refused-live"
REFUSED_RACE = "refused-lost-race"
REFUSED_IO = "refused-io"


def lock_path_for(umbrella_root: str) -> str:
    """The one machine-wide lockfile path.

    NOTE (known constraint, deliberately not redesigned): this is derived from
    the umbrella root, so mutual exclusion holds across checkouts that SHARE an
    umbrella -- the documented sibling-worktree layout. Two invocations given
    different ``--umbrella-root`` values, or run from a checkout nested
    somewhere with a different parent, resolve DIFFERENT lockfiles while still
    contending for the same machine-global kRPC ports and GPU. Keeping the key
    umbrella-relative is what lets the test suites and `--umbrella-root` runs
    isolate themselves; a truly machine-global path (e.g. under PROGRAMDATA)
    would serialize the unit suites against real runs. Documented in
    harness/README.md rather than silently assumed.
    """
    return os.path.join(umbrella_root, *provlib.MACHINE_LOCK_RELPATH)


def read_lock_bytes(path: str) -> Optional[bytes]:
    """Raw bytes, or None when absent/unreadable. Raw rather than parsed because
    the reclaim verification is a byte compare: two locks can parse to equal-
    looking dicts and still be different writes."""
    try:
        with open(path, "rb") as fh:
            return fh.read()
    except OSError:
        return None


def parse_lock(raw: Optional[bytes]) -> Optional[Dict]:
    """Parse lock bytes into the dict the pure predicate consumes. A torn or
    non-dict payload reads as None ('no usable lock'); provlib's malformed
    branches then decide, so an unparseable lockfile can never wedge the machine."""
    if raw is None:
        return None
    try:
        payload = json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return None
    return payload if isinstance(payload, dict) else None


def read_lock(path: str) -> Optional[Dict]:
    return parse_lock(read_lock_bytes(path))


def describe_holder(existing: Optional[Dict]) -> str:
    if not existing:
        return "unknown holder"
    return "pid=%s worktree=%s selection=%s since=%s" % (
        existing.get("pid"), existing.get("worktree") or "?",
        existing.get("selection") or "?", existing.get("startedIso") or "?")


def _restore_quarantined(
    quarantine: str,
    path: str,
    log: Callable[[str, str], None],
) -> None:
    """Put a wrongly-quarantined lock back, WITHOUT overwriting a new winner.

    The restore is an exclusive create, not an ``os.replace``: while the lock
    path was briefly empty a third process may have legitimately won it via
    O_EXCL, and stomping that would break this module's one invariant -- that
    holding is decided solely by the exclusive create. If the path is taken, the
    new winner keeps it and we simply drop our copy.
    """
    payload = read_lock_bytes(quarantine)
    if payload is None:
        return
    try:
        fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        log("Warn", "run-lock: not restoring the quarantined lock -- another "
                    "holder won %s in the meantime" % path)
    except OSError as exc:
        log("Error", "run-lock: could not restore %s after an aborted "
                     "reclaim: %s" % (path, exc))
        return
    else:
        with os.fdopen(fd, "wb") as fh:
            fh.write(payload)
    try:
        os.remove(quarantine)
    except OSError:
        pass


def acquire(
    path: str,
    payload_fn: Callable[[], Dict],
    now_fn: Callable[[], float],
    pid_alive_fn: Callable[[int], bool],
    lease_seconds: Optional[float] = provlib.DEFAULT_LEASE_SECONDS,
    log_fn: Optional[Callable[[str, str], None]] = None,
) -> Tuple[Optional[str], str, Optional[Dict]]:
    """Acquire the machine lock.

    Returns ``(path_or_None, reason, blocking_holder_or_None)``. ``payload_fn``
    is called per attempt so the stamped timestamp is the moment we actually
    win, not the moment we started trying.
    """
    def log(level: str, message: str) -> None:
        if log_fn:
            log_fn(level, message)

    pid = os.getpid()
    for attempt in range(1, ATTEMPTS + 1):
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            pass
        except OSError as exc:
            log("Error", "run-lock unusable at %s: %s" % (path, exc))
            return None, REFUSED_IO, None
        else:
            with os.fdopen(fd, "w", encoding="utf-8") as fh:
                json.dump(payload_fn(), fh)
            return path, (WON_FREE if attempt == 1 else WON_RECLAIMED), None

        # Someone holds it. Decide against the CURRENT contents.
        raw = read_lock_bytes(path)
        if raw == b"":
            # THE CREATE WINDOW. Between a winner's O_EXCL create and the close
            # of its write the file exists and is EMPTY. Parsing that yields
            # "no lock", and reclaiming on it would delete the winner's file
            # while it is still writing. On Windows the winner's open handle
            # makes our rename fail anyway, but that is a platform accident, not
            # the guarantee -- on POSIX the rename succeeds and BOTH would hold.
            # An empty file is never a real lock, so back off and re-decide.
            continue
        existing = parse_lock(raw)
        decision = provlib.acquire_lock(existing, pid, now_fn(), pid_alive_fn,
                                        lease_seconds=lease_seconds)
        if not decision.acquired:
            return None, REFUSED_LIVE, existing

        if attempt == ATTEMPTS:
            log("Warn", "run-lock: gave up after %d attempts; current holder %s"
                % (ATTEMPTS, describe_holder(read_lock(path))))
            return None, REFUSED_RACE, read_lock(path)

        # Stale. Move it aside atomically, then VERIFY we moved the thing we
        # judged -- never a fresh holder's lock.
        quarantine = "%s.stale-%d" % (path, pid)
        try:
            os.replace(path, quarantine)
        except OSError:
            # Already gone (another process reclaimed first), or undeletable.
            # Re-loop: the exclusive create re-decides.
            continue

        moved = read_lock_bytes(quarantine)
        if moved is None or raw is None or moved != raw:
            # Not provably the lock we judged. Either a fresh holder created it
            # between our read and our move, or a read failed -- and a read
            # failure is NOT benign here: if the file had simply been released,
            # the rename above would have raised FileNotFoundError instead of
            # succeeding. Two correlated unreadable reads (one AV or backup hold
            # spans both) would otherwise compare None == None, "verify", and
            # delete a LIVE holder's lock. Restore and refuse.
            _restore_quarantined(quarantine, path, log)
            log("Warn", "run-lock: aborted reclaim, %s"
                % ("a read failed while verifying" if (moved is None or raw is None)
                   else "a fresh holder appeared (%s)" % describe_holder(parse_lock(moved))))
            return None, REFUSED_LIVE, parse_lock(moved)

        log("Warn", "run-lock reclaiming stale lock (%s, reason=%s)"
            % (describe_holder(existing), decision.reason))
        try:
            os.remove(quarantine)
        except OSError:
            pass   # harmless leftover; it is not at the lock path
    return None, REFUSED_RACE, None


def heartbeat(
    path: str,
    payload_fn: Callable[[], Dict],
    log_fn: Optional[Callable[[str, str], None]] = None,
) -> bool:
    """Refresh our lock's timestamp. Returns True when we still hold it.

    A False return means we were reclaimed -- the caller MUST stop, because
    continuing would run unlocked alongside whoever took over. ``startedIso`` is
    carried over from the existing lock so a refusal keeps reporting when the
    hold BEGAN, not when it was last refreshed.
    """
    existing = read_lock(path)
    if existing is None:
        # A transient read failure (an AV or backup hold) is indistinguishable
        # from "reclaimed", and treating it as loss would abort a whole nightly.
        # Retry once before believing it.
        existing = read_lock(path)
    if not provlib.should_release(existing, os.getpid()):
        if log_fn:
            log_fn("Warn", "run-lock heartbeat: lock is no longer ours (%s)"
                   % describe_holder(existing))
        return False
    payload = payload_fn()
    if existing and existing.get("startedIso"):
        payload["startedIso"] = existing["startedIso"]
    tmp = "%s.tmp-%d" % (path, os.getpid())
    try:
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(payload, fh)
    except OSError as exc:
        if log_fn:
            log_fn("Warn", "run-lock heartbeat failed: %s" % exc)
        return True   # we still held it a moment ago; the next beat re-checks

    # FINAL ownership check, as late as possible. This read and the replace below
    # are still a TOCTOU pair -- a file cannot be compare-and-swapped -- but the
    # window is now two adjacent syscalls rather than spanning the payload build.
    # Checking AFTER the replace instead would be worthless: by then we have
    # already overwritten whoever reclaimed us, so the read only ever shows our
    # own write. Reaching here at all requires the lease to have expired under a
    # live holder, which LeaseCoversWorstCaseScenarioTests makes unreachable for
    # every committed spec.
    current = read_lock(path)
    if not provlib.should_release(current, os.getpid()):
        try:
            os.remove(tmp)
        except OSError:
            pass
        if log_fn:
            log_fn("Warn", "run-lock heartbeat: reclaimed mid-beat by %s"
                   % describe_holder(current))
        return False
    try:
        os.replace(tmp, path)
    except OSError as exc:
        if log_fn:
            log_fn("Warn", "run-lock heartbeat failed: %s" % exc)
    return True


def release(
    path: Optional[str],
    log_fn: Optional[Callable[[str, str], None]] = None,
) -> bool:
    """Delete the lock ONLY if it is still ours. Returns True if we removed it.

    An unconditional delete lets a run that was already reclaimed as stale
    remove the lock a different LIVE run now holds, turning one refusal into two
    concurrent flights.
    """
    if not path:
        return False
    existing = read_lock(path)
    if not provlib.should_release(existing, os.getpid()):
        if existing is not None and log_fn:
            log_fn("Warn", "run-lock not released: held by %s, not us (pid=%d)"
                   % (describe_holder(existing), os.getpid()))
        return False
    try:
        os.remove(path)
    except OSError:
        return False
    if log_fn:
        log_fn("Info", "run-lock released path=%s" % path)
    return True
