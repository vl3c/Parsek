"""Claim, renew, release and list copy leases (one KSP install copy per agent thread).

A lease lets one worktree own one provisioned KSP copy (``automation/<profile>``
or ``automation/<profile>-<k>``) across many ``provision.py`` / ``run.py``
calls, so two threads can fly at the same time on their own copies with their
own Parsek.dll builds. Contract: ``harness/lib/leases.py``; how-to:
``harness/README.md`` -> "Copy leases".

    cd harness
    python tools/ksp_lease.py claim --owner "far-watch"          # lowest free copy
    python tools/ksp_lease.py claim --owner "mod-parts" --copy 2  # a given copy
    python provision/provision.py --profile stock-minimal --lease # this copy only
    python run.py --lease --id LANE-A                                 # flies the leased copy
    python tools/ksp_lease.py status
    python tools/ksp_lease.py renew --ttl-hours 4
    python tools/ksp_lease.py release

EXIT CODES: 0 done, 2 refused by the contract (bad argument, cap reached, the
copy is leased, no lease to act on), 3 the machine is owned by somebody else
right now (a serial run, a plain provision or the parallel runner), 4 error.

Stdlib only.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.abspath(os.path.join(HERE, ".."))
WORKTREE_ROOT = os.path.abspath(os.path.join(HARNESS_ROOT, ".."))
UMBRELLA_ROOT = os.path.abspath(os.path.join(WORKTREE_ROOT, ".."))
LIB_DIR = os.path.join(HARNESS_ROOT, "lib")
PROVISION_DIR = os.path.join(HARNESS_ROOT, "provision")
for _p in (LIB_DIR, PROVISION_DIR, HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hashlib  # noqa: E402

import leaseio  # noqa: E402
import leases  # noqa: E402
import machinelock  # noqa: E402
import provlib  # noqa: E402
import slots  # noqa: E402

EXIT_OK = 0
EXIT_REFUSED = 2
EXIT_MACHINE_BUSY = 3
EXIT_ERROR = 4

PARSEK_DLL_REL = os.path.join("GameData", "Parsek", "Plugins", "Parsek.dll")
KSP_EXE = "KSP_x64.exe"

# A release waits this long for a machine owner's momentary hold (a refused
# acquire releases at once) before giving up.
RELEASE_LOCK_WAIT_SECONDS = 20.0


def utcnow_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def pid_alive(pid) -> bool:
    """Fails closed (reports ALIVE) when the probe cannot run, like run.py."""
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return False
    if pid <= 0:
        return False
    if sys.platform == "win32":
        try:
            out = subprocess.run(["tasklist", "/FI", "PID eq %d" % pid, "/FO", "CSV", "/NH"],
                                 capture_output=True, text=True)
        except OSError:
            return True
        for row in (out.stdout or "").splitlines():
            cells = [c.strip('"') for c in row.split('","')]
            if len(cells) >= 2 and cells[1].strip() == str(pid):
                return True
        return False
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return True
    return True


def sha256_file(path: str) -> Optional[str]:
    try:
        h = hashlib.sha256()
        with open(path, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


class Context:
    """Everything the commands touch, injectable for tests."""

    def __init__(self, umbrella_root: str = UMBRELLA_ROOT, worktree_root: str = WORKTREE_ROOT,
                 now_fn: Callable[[], float] = time.time,
                 pid_alive_fn: Callable[[int], bool] = pid_alive,
                 prefs_begin_fn: Optional[Callable[[str, Callable], str]] = None,
                 prefs_end_fn: Optional[Callable[[str, Callable], str]] = None,
                 sleep_fn: Callable[[float], None] = time.sleep,
                 out=None):
        self.umbrella_root = os.path.abspath(umbrella_root)
        self.worktree_root = os.path.abspath(worktree_root)
        self.now_fn = now_fn
        self.pid_alive_fn = pid_alive_fn
        self.prefs_begin_fn = prefs_begin_fn
        self.prefs_end_fn = prefs_end_fn
        self.sleep_fn = sleep_fn
        self.out = out or sys.stdout

    def say(self, text: str) -> None:
        self.out.write(text + "\n")
        self.out.flush()

    def log_fn(self, level: str, msg: str) -> None:
        if level in ("Warn", "Error"):
            self.say("[%s] %s" % (level.upper(), msg))

    def automation_dir(self) -> str:
        return os.path.join(self.umbrella_root, slots.AUTOMATION_DIR)

    def provisioned_dirs(self) -> List[str]:
        try:
            names = os.listdir(self.automation_dir())
        except OSError:
            return []
        return [n for n in names
                if os.path.isfile(os.path.join(self.automation_dir(), n, KSP_EXE))]

    def _prefs(self, begin: bool) -> None:
        fn = self.prefs_begin_fn if begin else self.prefs_end_fn
        if fn is None:
            try:
                import unity_prefs  # tools/unity_prefs.py
            except ImportError:
                return
            fn = unity_prefs.begin if begin else unity_prefs.end
        try:
            outcome = fn(self.umbrella_root, self.log_fn)
            self.say("unity prefs %s: %s" % ("snapshot" if begin else "restore", outcome))
        except Exception as exc:  # noqa: BLE001 - prefs hygiene never costs a lease
            self.say("[WARN] unity prefs %s failed (%s: %s)"
                     % ("snapshot" if begin else "restore", type(exc).__name__, exc))


def _lock_payload(ctx: Context, what: str) -> Dict:
    return {"pid": os.getpid(), "timestamp": ctx.now_fn(), "worktree": ctx.worktree_root,
            "selection": what, "startedIso": utcnow_iso()}


def _take_machine_lock(ctx: Context, what: str, wait_seconds: float = 0.0) -> Optional[str]:
    """The machine lock for the few milliseconds a registry change takes. A plain
    ``machinelock.acquire`` (not the lease-refusing one): leases may be live."""
    lock_path = machinelock.lock_path_for(ctx.umbrella_root)
    deadline = ctx.now_fn() + wait_seconds
    while True:
        path, reason, holder = machinelock.acquire(
            lock_path, payload_fn=lambda: _lock_payload(ctx, what), now_fn=ctx.now_fn,
            pid_alive_fn=ctx.pid_alive_fn, log_fn=ctx.log_fn)
        if path is not None:
            return path
        if ctx.now_fn() >= deadline:
            ctx.say("REFUSED: the machine is owned by %s (%s). A lease can be claimed only "
                    "while no serial run, plain provision or parallel runner holds the PC."
                    % (machinelock.describe_holder(holder), reason))
            return None
        ctx.sleep_fn(1.0)


def _live_slot_lock(ctx: Context, slot: str):
    states = [st for st in machinelock.read_slot_locks(ctx.umbrella_root) if st.slot == slot]
    live = provlib.live_slot_locks(states, os.getpid(), ctx.now_fn(), ctx.pid_alive_fn)
    return live[0] if live else None


def _print_next_steps(ctx: Context, lease: Dict) -> None:
    profile, copy = slots.parse_slot_name(lease["slot"])
    ctx.say("next: python provision/provision.py --profile %s --lease   (deploys this "
            "worktree's Parsek.dll to %s only, and pins it)" % (profile, lease["slot"]))
    ctx.say("      python run.py --lease --id <LANE>   (or --ids-file; flies in %s)"
            % lease["slot"])
    ctx.say("      python tools/ksp_lease.py release   (when this thread's flights are done)")


def cmd_claim(ctx: Context, owner: str, profile: str, copy: Optional[int], ttl: int,
              cap: int) -> int:
    lock = _take_machine_lock(ctx, "lease-claim owner=%s" % owner)
    if lock is None:
        return EXIT_MACHINE_BUSY
    try:
        now = ctx.now_fn()
        registry = leaseio.effective_leases(ctx.umbrella_root, now, ctx.pid_alive_fn)
        provisioned = leases.provisioned_slot_names(ctx.provisioned_dirs(), profile)
        decision = leases.decide_claim(registry, now, profile, ctx.worktree_root, cap=cap,
                                       copy=copy, provisioned_slots=provisioned)
        if not decision.ok:
            ctx.say("REFUSED (%s): %s" % (decision.reason, decision.detail))
            return EXIT_REFUSED
        slot = decision.slot
        if decision.reason == leases.CLAIM_ALREADY_HELD:
            current = leaseio.read_lease(ctx.umbrella_root, slot) or {}
            updated = leases.renewed(current, now, ttl)
            leaseio.write_lease(ctx.umbrella_root, updated)
            leaseio.write_worktree_ref(ctx.worktree_root, updated)
            ctx.say("lease renewed (already held) %s" % leases.describe_lease(updated, now))
            _print_next_steps(ctx, updated)
            return EXIT_OK
        held = _live_slot_lock(ctx, slot)
        if held is not None:
            ctx.say("REFUSED (slot-locked): %s; a run is still flying in that copy"
                    % provlib.describe_slot_lock(held))
            return EXIT_REFUSED
        instance = slots.instance_dir(ctx.umbrella_root, slot)
        dll = sha256_file(os.path.join(instance, PARSEK_DLL_REL))
        lease = leases.build_lease(slot, leases.new_token(), owner, ctx.worktree_root, now,
                                   utcnow_iso(), ttl_seconds=ttl, dll_sha256=dll)
        first = not leases.others_live(registry, now, slot)
        if first:
            # The first live lease owns the Unity screen prefs every KSP on the PC
            # shares (the machine owner's duty when no lease exists); the last
            # release restores them.
            ctx._prefs(begin=True)
        leaseio.write_lease(ctx.umbrella_root, lease)
        leaseio.write_worktree_ref(ctx.worktree_root, lease)
        ctx.say("lease claimed (%s) %s instance=%s" % (decision.detail, leases.describe_lease(lease, now),
                                                     instance))
        if not dll:
            ctx.say("note: %s has no deployed Parsek.dll yet; provision it under the lease "
                    "before flying" % slot)
        _print_next_steps(ctx, lease)
        return EXIT_OK
    finally:
        machinelock.release(lock, log_fn=ctx.log_fn)


def _resolve_own(ctx: Context):
    decision, lease = leaseio.resolve(ctx.worktree_root, ctx.umbrella_root, ctx.now_fn())
    return decision, lease


def cmd_renew(ctx: Context, ttl: Optional[int]) -> int:
    decision, lease = _resolve_own(ctx)
    if not decision.ok:
        ctx.say("REFUSED (%s): %s" % (decision.reason, decision.detail))
        return EXIT_REFUSED
    updated = leaseio.renew_if_ours(ctx.umbrella_root, lease["token"], decision.slot,
                                    ctx.now_fn(), ttl)
    if updated is None:
        ctx.say("REFUSED (lease-lost): the lease of %s changed while renewing" % decision.slot)
        return EXIT_REFUSED
    ctx.say("lease renewed %s" % leases.describe_lease(updated, ctx.now_fn()))
    return EXIT_OK


def cmd_release(ctx: Context) -> int:
    ref = leaseio.read_worktree_ref(ctx.worktree_root)
    if ref is None:
        ctx.say("nothing to release: this worktree holds no lease")
        return EXIT_OK
    slot = str(ref["slot"])
    lease = leaseio.read_lease(ctx.umbrella_root, slot)
    if not lease or lease.get("token") != ref.get("token"):
        leaseio.remove_worktree_ref(ctx.worktree_root)
        ctx.say("the lease of %s is no longer this worktree's (expired and re-claimed, or "
                "removed); dropped the worktree's reference" % slot)
        return EXIT_OK
    held = _live_slot_lock(ctx, slot)
    if held is not None:
        ctx.say("REFUSED (slot-locked): %s; stop the run in this copy first"
                % provlib.describe_slot_lock(held))
        return EXIT_REFUSED
    lock = _take_machine_lock(ctx, "lease-release slot=%s" % slot,
                              wait_seconds=RELEASE_LOCK_WAIT_SECONDS)
    if lock is None:
        return EXIT_MACHINE_BUSY
    try:
        now = ctx.now_fn()
        registry = leaseio.read_leases(ctx.umbrella_root)
        current = registry.get(slot)
        if not current or current.get("token") != ref.get("token"):
            # Re-checked under the machine lock: our lease expired and another
            # worktree claimed this copy while we waited. Never delete theirs.
            leaseio.remove_worktree_ref(ctx.worktree_root)
            ctx.say("the lease of %s was re-claimed by another worktree while releasing; "
                    "dropped only this worktree's reference" % slot)
            return EXIT_OK
        leaseio.remove_lease(ctx.umbrella_root, slot)
        leaseio.remove_worktree_ref(ctx.worktree_root)
        ctx.say("lease released slot=%s owner=%s" % (slot, lease.get("owner")))
        if not leases.others_live(registry, now, slot):
            ctx._prefs(begin=False)
        return EXIT_OK
    finally:
        machinelock.release(lock, log_fn=ctx.log_fn)


def cmd_status(ctx: Context, as_json: bool) -> int:
    now = ctx.now_fn()
    registry = leaseio.read_leases(ctx.umbrella_root)
    machine = machinelock.read_lock(machinelock.lock_path_for(ctx.umbrella_root))
    ref = leaseio.read_worktree_ref(ctx.worktree_root)
    rows = []
    for slot, lease in sorted(registry.items()):
        rows.append({"slot": slot, "live": bool(lease) and leases.is_live(lease, now)
                     and lease.get("slot") == slot,
                     "mine": bool(ref and lease and ref.get("token") == lease.get("token")),
                     "lease": lease})
    if as_json:
        ctx.say(json.dumps({"leases": rows, "machineLock": machine,
                            "worktreeLease": ref}, indent=1, sort_keys=True))
        return EXIT_OK
    ctx.say("machine lock: %s" % (machinelock.describe_holder(machine) if machine else "free"))
    if not rows:
        ctx.say("leases: none")
    for row in rows:
        lease = row["lease"]
        state = "LIVE" if row["live"] else "stale"
        desc = leases.describe_lease(lease, now) if lease else "slot=%s (unreadable)" % row["slot"]
        ctx.say("lease %s%s %s" % (state, " (this worktree)" if row["mine"] else "", desc))
    if ref is not None and not any(r["mine"] for r in rows):
        ctx.say("this worktree's reference names %s, but the registry no longer has it"
                % ref.get("slot"))
    return EXIT_OK


def _ttl_from_hours(value: Optional[float]) -> Optional[int]:
    if value is None:
        return None
    return leases.clamp_ttl(float(value) * 3600)


def main(argv=None, ctx: Optional[Context] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--umbrella-root", help="override the umbrella root (tests)")
    sub = parser.add_subparsers(dest="cmd", required=True)
    claim = sub.add_parser("claim", help="claim a copy for this worktree (renews one already held)")
    claim.add_argument("--owner", required=True, help="who holds it: the thread or task name")
    claim.add_argument("--profile", default="stock-minimal")
    claim.add_argument("--copy", type=int, help="a given copy number (default: lowest free)")
    claim.add_argument("--ttl-hours", type=float, default=leases.DEFAULT_TTL_SECONDS / 3600.0,
                       help="time to live after the last renew (default 3; every leased run "
                            "renews at each attempt)")
    claim.add_argument("--cap", type=int, default=leases.DEFAULT_CAP,
                       help="most live leases on the machine (default 2 while the owner uses "
                            "the PC; 3 when away)")
    renew = sub.add_parser("renew", help="push this worktree's lease expiry out")
    renew.add_argument("--ttl-hours", type=float)
    sub.add_parser("release", help="give this worktree's copy back")
    status = sub.add_parser("status", help="list leases and the machine lock")
    status.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    if ctx is None:
        ctx = Context(umbrella_root=args.umbrella_root or UMBRELLA_ROOT)
    try:
        if args.cmd == "claim":
            try:
                ttl = _ttl_from_hours(args.ttl_hours)
                cap = leases.clamp_cap(args.cap)
                if args.copy is not None:
                    slots.slot_name(args.profile, args.copy)
                else:
                    slots.slot_name(args.profile, 1)
            except (leases.LeaseError, slots.SlotError) as exc:
                ctx.say("REFUSED (bad-argument): %s" % exc)
                return EXIT_REFUSED
            return cmd_claim(ctx, args.owner, args.profile, args.copy, ttl, cap)
        if args.cmd == "renew":
            try:
                ttl = _ttl_from_hours(args.ttl_hours)
            except leases.LeaseError as exc:
                ctx.say("REFUSED (bad-argument): %s" % exc)
                return EXIT_REFUSED
            return cmd_renew(ctx, ttl)
        if args.cmd == "release":
            return cmd_release(ctx)
        return cmd_status(ctx, args.json)
    except Exception as exc:  # noqa: BLE001 - one line, exit 4
        ctx.say("ERROR: %s: %s" % (type(exc).__name__, exc))
        return EXIT_ERROR


if __name__ == "__main__":
    sys.exit(main())
