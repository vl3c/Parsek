"""Direct tests for the shared machine-lock protocol (harness/provision/machinelock.py).

These drive ``machinelock`` itself rather than reaching it through ``run.py``, so
the branches only the loser of a race ever executes -- the aborted reclaim, the
restore, the read-failure guard, the create window -- are covered on their own
terms. Both shells share this module, so a cell here covers provision.py's half
too; before this file existed provision's side rested entirely on source-text
assertions.

Every race is driven deterministically by mutating the lockfile inside the
injected ``pid_alive`` probe. That injection point is faithful, not arbitrary:
in production the probe shells out to ``tasklist``, and that ~100ms is exactly
the window a racing process gets between our read and our reclaim.
"""

import json
import os
import tempfile
import unittest

import machinelock
import provlib

ALIVE = lambda pid: True
DEAD = lambda pid: False


class _Case(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="mlock-")
        self.path = os.path.join(self.dir, "automation", ".ksp-machine.lock")
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        self.logs = []

    def log(self, level, msg):
        self.logs.append("%s %s" % (level, msg))

    def seed(self, payload):
        with open(self.path, "w", encoding="utf-8") as fh:
            json.dump(payload, fh)
        return payload

    def on_disk(self):
        with open(self.path, "r", encoding="utf-8") as fh:
            return json.load(fh)

    def acquire(self, alive=ALIVE, now=1000.0, lease=3600.0, payload=None):
        return machinelock.acquire(
            self.path,
            payload_fn=lambda: payload or {"pid": os.getpid(), "timestamp": now},
            now_fn=lambda: now, pid_alive_fn=alive,
            lease_seconds=lease, log_fn=self.log)


class AcquireBasicsTests(_Case):

    def test_free_machine_is_won_on_the_first_attempt(self):
        got, reason, holder = self.acquire()
        self.assertEqual(got, self.path)
        self.assertEqual(reason, machinelock.WON_FREE)
        self.assertIsNone(holder)
        self.assertEqual(self.on_disk()["pid"], os.getpid())

    def test_live_holder_is_refused_and_untouched(self):
        held = self.seed({"pid": os.getpid() + 1, "timestamp": 1000.0})
        got, reason, holder = self.acquire(alive=ALIVE, now=1000.0)
        self.assertIsNone(got)
        self.assertEqual(reason, machinelock.REFUSED_LIVE)
        self.assertEqual(holder["pid"], held["pid"])
        self.assertEqual(self.on_disk(), held)

    def test_dead_holder_is_reclaimed(self):
        self.seed({"pid": os.getpid() + 1, "timestamp": 1000.0})
        got, reason, _ = self.acquire(alive=DEAD)
        self.assertEqual(got, self.path)
        self.assertEqual(reason, machinelock.WON_RECLAIMED)
        self.assertEqual(self.on_disk()["pid"], os.getpid())

    def test_expired_lease_is_reclaimed_though_the_pid_looks_alive(self):
        self.seed({"pid": os.getpid() + 1, "timestamp": 0.0})
        got, _, _ = self.acquire(alive=ALIVE, now=10_000.0, lease=100.0)
        self.assertEqual(got, self.path)

    def test_unwritable_location_refuses_rather_than_raising(self):
        blocked = os.path.join(self.dir, "nope.lock")
        os.makedirs(blocked, exist_ok=True)      # a DIRECTORY where the file goes
        got, reason, _ = machinelock.acquire(
            blocked, payload_fn=lambda: {"pid": os.getpid(), "timestamp": 1.0},
            now_fn=lambda: 1.0, pid_alive_fn=DEAD, lease_seconds=None, log_fn=self.log)
        self.assertIsNone(got)
        self.assertIn(reason, (machinelock.REFUSED_IO, machinelock.REFUSED_RACE))


class AbortedReclaimTests(_Case):
    """The loser's path. Each of these would, if wrong, end with TWO holders."""

    def _swap_during_probe(self, replacement):
        """Return a pid_alive that judges the current lock dead, then swaps a
        different lock onto the path -- the racing-winner window."""
        fired = []

        def probe(pid):
            if not fired:
                fired.append(True)
                with open(self.path, "w", encoding="utf-8") as fh:
                    json.dump(replacement, fh)
                return False          # the lock we READ looks stale
            return True               # whatever is there NOW is live
        return probe

    def test_a_fresh_holder_appearing_mid_reclaim_is_restored_not_stolen(self):
        self.seed({"pid": os.getpid() + 1, "timestamp": 0.0, "selection": "stale"})
        winner = {"pid": os.getpid() + 2, "timestamp": 5000.0, "selection": "WINNER"}
        got, reason, holder = self.acquire(
            alive=self._swap_during_probe(winner), now=5000.0)
        self.assertIsNone(got, "must not steal a lock it did not judge")
        self.assertEqual(reason, machinelock.REFUSED_LIVE)
        self.assertEqual(self.on_disk(), winner)
        self.assertEqual(holder["selection"], "WINNER")

    def test_an_unreadable_verify_aborts_the_reclaim(self):
        """Both reads failing must NOT compare None == None and 'verify'. A
        single AV or backup hold spans both reads, and the consequence would be
        deleting a LIVE holder's lock."""
        live = self.seed({"pid": os.getpid() + 1, "timestamp": 1000.0})
        real_read = machinelock.read_lock_bytes
        calls = []

        def failing_read(path):
            calls.append(path)
            # Fail the pre-decision read and the post-quarantine verify.
            return None if len(calls) <= 2 else real_read(path)

        machinelock.read_lock_bytes = failing_read
        try:
            got, reason, _ = self.acquire(alive=DEAD)
        finally:
            machinelock.read_lock_bytes = real_read
        self.assertIsNone(got)
        self.assertEqual(self.on_disk(), live, "a live lock must survive")
        self.assertTrue(any("aborted reclaim" in l for l in self.logs), self.logs)

    def test_the_empty_create_window_is_never_reclaimed(self):
        """Between a winner's O_EXCL create and its write the file is EMPTY.
        Parsing that yields 'no lock'; reclaiming on it would delete the
        winner's file mid-write. On POSIX the rename would succeed, so the guard
        must be in the protocol, not in Windows sharing semantics."""
        with open(self.path, "wb") as fh:
            fh.write(b"")
        got, reason, _ = self.acquire(alive=ALIVE)
        self.assertIsNone(got)
        self.assertEqual(reason, machinelock.REFUSED_RACE)
        self.assertTrue(os.path.isfile(self.path), "must not delete the winner's file")


class RestoreTests(_Case):

    def test_restore_does_not_overwrite_a_third_party_that_won_the_path(self):
        """While the path was briefly free a third process may legitimately win
        it via O_EXCL. The invariant is that only the exclusive create decides a
        holder, so the restore must stand down rather than stomp it."""
        quarantine = self.path + ".stale-999"
        with open(quarantine, "w", encoding="utf-8") as fh:
            json.dump({"pid": 111, "selection": "quarantined"}, fh)
        third = self.seed({"pid": 222, "selection": "THIRD-PARTY"})
        machinelock._restore_quarantined(quarantine, self.path, self.log)
        self.assertEqual(self.on_disk(), third)
        self.assertTrue(any("not restoring" in l for l in self.logs), self.logs)

    def test_restore_puts_the_lock_back_when_the_path_is_free(self):
        quarantine = self.path + ".stale-999"
        original = {"pid": 111, "selection": "original"}
        with open(quarantine, "w", encoding="utf-8") as fh:
            json.dump(original, fh)
        machinelock._restore_quarantined(quarantine, self.path, self.log)
        self.assertEqual(self.on_disk(), original)
        self.assertFalse(os.path.isfile(quarantine))


class HeartbeatTests(_Case):

    def test_heartbeat_refreshes_timestamp_and_keeps_started_iso(self):
        self.seed({"pid": os.getpid(), "timestamp": 1.0, "startedIso": "BEGAN"})
        ok = machinelock.heartbeat(
            self.path,
            payload_fn=lambda: {"pid": os.getpid(), "timestamp": 999.0,
                                "startedIso": "NOW"},
            log_fn=self.log)
        self.assertTrue(ok)
        self.assertEqual(self.on_disk()["timestamp"], 999.0, "lease must advance")
        self.assertEqual(self.on_disk()["startedIso"], "BEGAN",
                         "'since' must report when the hold began")

    def test_heartbeat_reports_loss_and_leaves_the_reclaimers_lock(self):
        theirs = self.seed({"pid": os.getpid() + 1, "timestamp": 1.0})
        ok = machinelock.heartbeat(
            self.path, payload_fn=lambda: {"pid": os.getpid(), "timestamp": 2.0},
            log_fn=self.log)
        self.assertFalse(ok)
        self.assertEqual(self.on_disk(), theirs)

    def test_heartbeat_detects_a_reclaim_that_lands_mid_beat(self):
        """A reclaim landing between the first ownership check and the write must
        NOT be papered over. Overwriting the reclaimer's fresh lock and returning
        True would keep us flying beside it -- and if the reclaimer is a
        provision it never heartbeats, so it would never learn and would DEPLOY
        over a live instance. The late re-check catches it and the reclaimer's
        lock survives."""
        self.seed({"pid": os.getpid(), "timestamp": 1.0})
        theirs = {"pid": os.getpid() + 1, "timestamp": 50.0, "selection": "reclaimer"}

        def payload():
            self.seed(theirs)          # reclaim lands after the first check
            return {"pid": os.getpid(), "timestamp": 2.0}

        self.assertFalse(machinelock.heartbeat(self.path, payload_fn=payload,
                                               log_fn=self.log))
        self.assertEqual(self.on_disk(), theirs,
                         "the reclaimer's lock must survive our beat")
        self.assertTrue(any("reclaimed mid-beat" in l for l in self.logs), self.logs)
        leftovers = [f for f in os.listdir(os.path.dirname(self.path)) if ".tmp-" in f]
        self.assertEqual([], leftovers, "the abandoned temp file must be cleaned up")

    def test_a_transient_read_failure_does_not_read_as_loss(self):
        """A blip must not abort a whole nightly; it is retried once."""
        self.seed({"pid": os.getpid(), "timestamp": 1.0})
        real = machinelock.read_lock_bytes
        calls = []

        def flaky(path):
            calls.append(path)
            return None if len(calls) == 1 else real(path)

        machinelock.read_lock_bytes = flaky
        try:
            ok = machinelock.heartbeat(
                self.path, payload_fn=lambda: {"pid": os.getpid(), "timestamp": 2.0},
                log_fn=self.log)
        finally:
            machinelock.read_lock_bytes = real
        self.assertTrue(ok)


class ReleaseTests(_Case):

    def test_release_removes_only_our_own_lock(self):
        self.seed({"pid": os.getpid(), "timestamp": 1.0})
        self.assertTrue(machinelock.release(self.path, log_fn=self.log))
        self.assertFalse(os.path.isfile(self.path))

    def test_release_declines_a_foreign_lock(self):
        theirs = self.seed({"pid": os.getpid() + 1, "timestamp": 1.0})
        self.assertFalse(machinelock.release(self.path, log_fn=self.log))
        self.assertEqual(self.on_disk(), theirs)

    def test_release_of_absent_or_none_is_a_noop(self):
        self.assertFalse(machinelock.release(None, log_fn=self.log))
        self.assertFalse(machinelock.release(self.path, log_fn=self.log))


class PathTests(_Case):

    def test_path_is_umbrella_scoped_and_shared(self):
        umbrella = os.path.join("C:", os.sep, "umb")
        self.assertEqual(machinelock.lock_path_for(umbrella),
                         os.path.join(umbrella, *provlib.MACHINE_LOCK_RELPATH))
        self.assertNotIn("stock-minimal", machinelock.lock_path_for(umbrella))


if __name__ == "__main__":
    unittest.main()
