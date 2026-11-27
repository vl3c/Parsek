"""Cells for the copy-lease I/O (provision/leaseio.py) and its CLI (tools/ksp_lease.py),
in temp umbrellas: claims under the machine lock, the cap, the Unity prefs duty of the
first claim and the last release, a machine owner refused while a lease is live, and
the run index / tier runner / parallel runner seeing the same rule."""

import io
import json
import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
for _p in (HERE, os.path.join(HARNESS_ROOT, "provision"), os.path.join(HARNESS_ROOT, "tools")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import ksp_lease  # noqa: E402
import leaseio  # noqa: E402
import leases  # noqa: E402
import machinelock  # noqa: E402
import slots  # noqa: E402


class _Clock:
    def __init__(self, t=1_000_000.0):
        self.t = t

    def __call__(self):
        return self.t


def _provision_copy(um, slot, dll=b"dll-bytes"):
    inst = slots.instance_dir(um, slot)
    os.makedirs(os.path.join(inst, "GameData", "Parsek", "Plugins"), exist_ok=True)
    with open(os.path.join(inst, "KSP_x64.exe"), "wb") as fh:
        fh.write(b"exe")
    if dll is not None:
        with open(os.path.join(inst, ksp_lease.PARSEK_DLL_REL), "wb") as fh:
            fh.write(dll)
    return inst


class _Box(unittest.TestCase):

    def setUp(self):
        self.um = tempfile.mkdtemp(prefix="parsek-lease-")
        os.makedirs(os.path.join(self.um, "automation"))
        self.clock = _Clock()
        self.prefs = []
        self.worktrees = {}
        for name in ("a", "b", "c"):
            wt = os.path.join(self.um, "Parsek-%s" % name)
            os.makedirs(os.path.join(wt, "harness"))
            self.worktrees[name] = wt

    def tearDown(self):
        shutil.rmtree(self.um, ignore_errors=True)

    def ctx(self, wt="a", alive=lambda pid: True):
        out = io.StringIO()
        ctx = ksp_lease.Context(
            umbrella_root=self.um, worktree_root=self.worktrees[wt], now_fn=self.clock,
            pid_alive_fn=alive,
            prefs_begin_fn=lambda um, log: self.prefs.append(("begin", wt)) or "snapshot",
            prefs_end_fn=lambda um, log: self.prefs.append(("end", wt)) or "restored",
            sleep_fn=lambda s: setattr(self.clock, "t", self.clock.t + s), out=out)
        return ctx

    def run_cli(self, wt, *argv, alive=lambda pid: True):
        ctx = self.ctx(wt, alive)
        code = ksp_lease.main(list(argv), ctx=ctx)
        return code, ctx.out.getvalue()

    def ref(self, wt):
        return leaseio.read_worktree_ref(self.worktrees[wt])


class ClaimReleaseTests(_Box):

    def test_two_worktrees_get_two_copies_and_a_third_hits_the_cap(self):
        _provision_copy(self.um, "stock-minimal")
        _provision_copy(self.um, "stock-minimal-2", dll=b"other-build")
        code, out = self.run_cli("a", "claim", "--owner", "far-watch")
        self.assertEqual(0, code, out)
        self.assertEqual("stock-minimal", self.ref("a")["slot"])
        code, out = self.run_cli("b", "claim", "--owner", "mod-parts")
        self.assertEqual(0, code, out)
        self.assertEqual("stock-minimal-2", self.ref("b")["slot"])
        code, out = self.run_cli("c", "claim", "--owner", "third")
        self.assertEqual(ksp_lease.EXIT_REFUSED, code)
        self.assertIn("cap-reached", out)
        self.assertIsNone(self.ref("c"))
        # Only the first claim took the Unity prefs snapshot.
        self.assertEqual([("begin", "a")], self.prefs)
        # The machine lock is never left behind by a claim.
        self.assertFalse(os.path.exists(machinelock.lock_path_for(self.um)))
        # Each lease pins the DLL its copy carried at claim time.
        a = leaseio.read_lease(self.um, "stock-minimal")
        b = leaseio.read_lease(self.um, "stock-minimal-2")
        self.assertEqual(ksp_lease.sha256_file(os.path.join(
            slots.instance_dir(self.um, "stock-minimal-2"), ksp_lease.PARSEK_DLL_REL)),
            b["dllSha256"])
        self.assertNotEqual(a["dllSha256"], b["dllSha256"])
        self.assertEqual(("far-watch", self.worktrees["a"]), (a["owner"], a["worktree"]))

    def test_releases_restore_the_prefs_only_after_the_last_lease(self):
        _provision_copy(self.um, "stock-minimal")
        _provision_copy(self.um, "stock-minimal-2")
        self.run_cli("a", "claim", "--owner", "a")
        self.run_cli("b", "claim", "--owner", "b")
        code, out = self.run_cli("a", "release")
        self.assertEqual(0, code, out)
        self.assertIsNone(self.ref("a"))
        self.assertIsNone(leaseio.read_lease(self.um, "stock-minimal"))
        self.assertEqual([("begin", "a")], self.prefs)
        code, out = self.run_cli("b", "release")
        self.assertEqual(0, code, out)
        self.assertEqual([("begin", "a"), ("end", "b")], self.prefs)
        code, out = self.run_cli("b", "release")
        self.assertEqual(0, code)
        self.assertIn("holds no lease", out)

    def test_claiming_again_renews_the_same_slot(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "a")
        token = self.ref("a")["token"]
        self.clock.t += 600
        code, out = self.run_cli("a", "claim", "--owner", "a", "--ttl-hours", "5")
        self.assertEqual(0, code, out)
        self.assertIn("already held", out)
        lease = leaseio.read_lease(self.um, "stock-minimal")
        self.assertEqual((token, self.clock.t, 5 * 3600),
                         (lease["token"], lease["renewedAt"], lease["ttlSeconds"]))

    def test_a_claim_while_a_machine_owner_holds_the_pc_exits_3(self):
        with open(machinelock.lock_path_for(self.um), "w") as fh:
            json.dump({"pid": 4242, "timestamp": self.clock.t, "selection": "nightly"}, fh)
        code, out = self.run_cli("a", "claim", "--owner", "a")
        self.assertEqual(ksp_lease.EXIT_MACHINE_BUSY, code)
        self.assertIn("nightly", out)
        self.assertIsNone(self.ref("a"))

    def test_a_copy_with_a_live_slot_lock_is_not_claimed(self):
        _provision_copy(self.um, "stock-minimal")
        with open(slots.slot_lock_path(self.um, "stock-minimal"), "w") as fh:
            json.dump({"pid": 4242, "timestamp": self.clock.t, "slot": "stock-minimal"}, fh)
        code, out = self.run_cli("a", "claim", "--owner", "a", "--copy", "1")
        self.assertEqual(ksp_lease.EXIT_REFUSED, code)
        self.assertIn("slot-locked", out)

    def test_release_is_refused_while_a_run_flies_in_the_copy(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "a")
        with open(slots.slot_lock_path(self.um, "stock-minimal"), "w") as fh:
            json.dump({"pid": 4242, "timestamp": self.clock.t, "slot": "stock-minimal"}, fh)
        code, out = self.run_cli("a", "release")
        self.assertEqual(ksp_lease.EXIT_REFUSED, code)
        self.assertIsNotNone(leaseio.read_lease(self.um, "stock-minimal"))

    def test_an_unprovisioned_copy_is_claimed_unpinned(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "a")
        code, out = self.run_cli("b", "claim", "--owner", "b")
        self.assertEqual(0, code, out)
        self.assertIn("no deployed Parsek.dll", out)
        self.assertIsNone(leaseio.read_lease(self.um, "stock-minimal-2")["dllSha256"])

    def test_an_expired_lease_is_taken_over_and_its_old_holder_cannot_renew(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "a", "--ttl-hours", "1")
        self.clock.t += 3601
        code, out = self.run_cli("b", "claim", "--owner", "b", "--copy", "1")
        self.assertEqual(0, code, out)
        code, out = self.run_cli("a", "renew")
        self.assertEqual(ksp_lease.EXIT_REFUSED, code)
        self.assertIn("token-mismatch", out)
        code, out = self.run_cli("a", "release")
        self.assertEqual(0, code)
        self.assertEqual("b", leaseio.read_lease(self.um, "stock-minimal")["owner"])

    def test_status_lists_leases(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "far-watch")
        code, out = self.run_cli("a", "status")
        self.assertEqual(0, code)
        self.assertIn("lease LIVE (this worktree) slot=stock-minimal owner=far-watch", out)
        code, out = self.run_cli("b", "status", "--json")
        doc = json.loads(out)
        self.assertEqual([True], [r["live"] for r in doc["leases"]])

    def test_bad_arguments_exit_2(self):
        for argv in (["claim", "--owner", "a", "--cap", "4"],
                     ["claim", "--owner", "a", "--ttl-hours", "0"],
                     ["claim", "--owner", "a", "--copy", "11"],
                     ["claim", "--owner", "a", "--profile", "nope"]):
            with self.subTest(argv=argv):
                code, out = self.run_cli("a", *argv)
                self.assertEqual(ksp_lease.EXIT_REFUSED, code, out)


class AcquireMachineTests(_Box):

    def acquire(self):
        return leaseio.acquire_machine(
            machinelock.lock_path_for(self.um), self.um,
            payload_fn=lambda: {"pid": os.getpid(), "timestamp": self.clock.t},
            now_fn=self.clock, pid_alive_fn=lambda pid: True)

    def test_a_live_lease_refuses_a_machine_owner_and_gives_the_lock_back(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "far-watch")
        path, reason, holder = self.acquire()
        self.assertIsNone(path)
        self.assertEqual(leaseio.REFUSED_LEASED, reason)
        self.assertIn("far-watch", machinelock.describe_holder(holder))
        self.assertFalse(os.path.exists(machinelock.lock_path_for(self.um)))

    def test_an_expired_lease_does_not_block(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "a", "--ttl-hours", "1")
        self.clock.t += 3601
        path, reason, _h = self.acquire()
        self.assertIsNotNone(path)
        machinelock.release(path)

    def test_renew_if_ours_checks_the_token(self):
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "a")
        token = self.ref("a")["token"]
        self.assertIsNone(leaseio.renew_if_ours(self.um, "nope", "stock-minimal", self.clock.t))
        updated = leaseio.renew_if_ours(self.um, token, "stock-minimal", self.clock.t + 5,
                                        pin=("EF" * 32, leases.DLL_SOURCE_PROVISION, "iso"))
        self.assertEqual(("ef" * 32, "provision", self.clock.t + 5),
                         (updated["dllSha256"], updated["dllSource"], updated["renewedAt"]))

    def test_the_tier_runner_skips_while_a_lease_is_live(self):
        import tier_runner
        _provision_copy(self.um, "stock-minimal")
        self.run_cli("a", "claim", "--owner", "far-watch")
        lease = leaseio.read_lease(self.um, "stock-minimal")
        lease["renewedAt"] = 10 ** 12  # live by the wall clock tier_runner reads
        leaseio.write_lease(self.um, lease)

        class Log:
            lines = []

            def info(self, *a):
                self.lines.append(a)

            def warn(self, *a):
                self.lines.append(a)
        orig = tier_runner.UMBRELLA_ROOT
        tier_runner.UMBRELLA_ROOT = self.um
        try:
            outcome = tier_runner.preflight_machine(Log())
        finally:
            tier_runner.UMBRELLA_ROOT = orig
        self.assertIsNotNone(outcome)
        self.assertEqual(tier_runner.OUTCOME_SKIPPED, outcome.outcome)
        self.assertIn("copy lease live", outcome.detail)


if __name__ == "__main__":
    unittest.main()
