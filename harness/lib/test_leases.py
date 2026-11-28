"""Pure cells for the copy-lease contract (lib/leases.py)."""

import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import leases  # noqa: E402

NOW = 1_000_000.0
WT_A = os.path.abspath(os.path.join(os.sep, "um", "Parsek-a"))
WT_B = os.path.abspath(os.path.join(os.sep, "um", "Parsek-b"))
WT_C = os.path.abspath(os.path.join(os.sep, "um", "Parsek-c"))


def _lease(slot, worktree, token="t", renewed=NOW, ttl=3600, dll="ab" * 32, owner="x"):
    lease = leases.build_lease(slot, token, owner, worktree, renewed, "iso", ttl, dll)
    return lease


class NameTests(unittest.TestCase):

    def test_lease_file_names_round_trip_and_reject_other_names(self):
        self.assertEqual(".ksp-lease-stock-minimal-2.json", leases.lease_file_name("stock-minimal-2"))
        self.assertEqual("stock-minimal-2", leases.slot_from_lease_name(".ksp-lease-stock-minimal-2.json"))
        for name in (".ksp-lease-stock-minimal-2.json.tmp-5", ".ksp-slot-stock-minimal.lock",
                     ".ksp-lease-nope.json", "x.json"):
            with self.subTest(name=name):
                self.assertIsNone(leases.slot_from_lease_name(name))

    def test_bad_slot_names_raise(self):
        with self.assertRaises(Exception):
            leases.lease_file_name("stock-minimal-0")


class LivenessTests(unittest.TestCase):

    def test_live_until_renewed_plus_ttl(self):
        lease = _lease("stock-minimal", WT_A, ttl=60)
        self.assertTrue(leases.is_live(lease, NOW + 59))
        self.assertFalse(leases.is_live(lease, NOW + 60))

    def test_malformed_payloads_are_stale(self):
        for bad in (None, {}, {"slot": "stock-minimal", "token": "t", "renewedAt": "x",
                               "ttlSeconds": 60},
                    {"slot": "stock-minimal", "token": "", "renewedAt": NOW, "ttlSeconds": 60},
                    {"slot": "stock-minimal", "token": "t", "renewedAt": NOW, "ttlSeconds": 0},
                    {"slot": "stock-minimal", "token": "t", "renewedAt": True, "ttlSeconds": 60}):
            with self.subTest(bad=bad):
                self.assertFalse(leases.is_live(bad, NOW))

    def test_a_payload_whose_slot_disagrees_with_its_file_is_not_live(self):
        registry = {"stock-minimal-2": _lease("stock-minimal", WT_A)}
        self.assertEqual({}, leases.live_leases(registry, NOW))

    def test_renew_and_pin_return_copies(self):
        lease = _lease("stock-minimal", WT_A, renewed=NOW - 100)
        later = leases.renewed(lease, NOW, 7200)
        self.assertEqual((NOW, 7200), (later["renewedAt"], later["ttlSeconds"]))
        self.assertEqual(NOW - 100, lease["renewedAt"])
        pinned = leases.with_pin(later, "CD" * 32, leases.DLL_SOURCE_PROVISION, "iso2")
        self.assertEqual(("cd" * 32, "provision", "iso2"),
                         (pinned["dllSha256"], pinned["dllSource"], pinned["pinnedIso"]))

    def test_an_owner_label_is_required(self):
        with self.assertRaises(leases.LeaseError):
            leases.build_lease("stock-minimal", "t", "  ", WT_A, NOW, "iso")

    def test_ttl_and_cap_bounds(self):
        self.assertEqual(3600, leases.clamp_ttl(3600.4))
        for bad in (59, leases.MAX_TTL_SECONDS + 1, "x"):
            with self.subTest(ttl=bad), self.assertRaises(leases.LeaseError):
                leases.clamp_ttl(bad)
        self.assertEqual(3, leases.clamp_cap(3))
        for bad in (0, 4, True, "2"):
            with self.subTest(cap=bad), self.assertRaises(leases.LeaseError):
                leases.clamp_cap(bad)


class ClaimTests(unittest.TestCase):
    PROVISIONED = ("stock-minimal", "stock-minimal-2", "stock-minimal-3")

    def claim(self, registry, worktree=WT_A, copy=None, cap=2, provisioned=PROVISIONED):
        return leases.decide_claim(registry, NOW, "stock-minimal", worktree, cap=cap, copy=copy,
                                   provisioned_slots=provisioned)

    def test_first_claim_gets_copy_1(self):
        d = self.claim({})
        self.assertEqual((True, leases.CLAIM_NEW, "stock-minimal"), (d.ok, d.reason, d.slot))

    def test_second_worktree_gets_the_next_free_copy(self):
        d = self.claim({"stock-minimal": _lease("stock-minimal", WT_A)}, worktree=WT_B)
        self.assertEqual((True, "stock-minimal-2"), (d.ok, d.slot))

    def test_provisioned_copies_are_preferred_over_lower_unprovisioned_ones(self):
        d = self.claim({"stock-minimal": _lease("stock-minimal", WT_A)}, worktree=WT_B,
                       provisioned=("stock-minimal", "stock-minimal-3"))
        self.assertEqual("stock-minimal-3", d.slot)

    def test_with_no_free_provisioned_copy_the_lowest_free_is_named(self):
        d = self.claim({"stock-minimal": _lease("stock-minimal", WT_A)}, worktree=WT_B,
                       provisioned=("stock-minimal",))
        self.assertEqual((True, "stock-minimal-2"), (d.ok, d.slot))
        self.assertIn("must be provisioned", d.detail)

    def test_the_cap_refuses_a_third_lease(self):
        reg = {"stock-minimal": _lease("stock-minimal", WT_A),
               "stock-minimal-2": _lease("stock-minimal-2", WT_B)}
        d = self.claim(reg, worktree=WT_C)
        self.assertEqual((False, leases.CLAIM_CAP_REACHED), (d.ok, d.reason))
        self.assertEqual((True, "stock-minimal-3"), (self.claim(reg, WT_C, cap=3).ok,
                                                      self.claim(reg, WT_C, cap=3).slot))

    def test_an_expired_lease_frees_its_copy_and_its_cap_place(self):
        reg = {"stock-minimal": _lease("stock-minimal", WT_A, renewed=NOW - 7200),
               "stock-minimal-2": _lease("stock-minimal-2", WT_B)}
        d = self.claim(reg, worktree=WT_C)
        self.assertEqual((True, "stock-minimal"), (d.ok, d.slot))

    def test_claiming_again_from_the_same_worktree_returns_its_slot(self):
        reg = {"stock-minimal-2": _lease("stock-minimal-2", WT_A)}
        d = self.claim(reg)
        self.assertEqual((True, leases.CLAIM_ALREADY_HELD, "stock-minimal-2"),
                         (d.ok, d.reason, d.slot))
        # Path spelling does not matter.
        d = self.claim(reg, worktree=WT_A + os.sep)
        self.assertEqual(leases.CLAIM_ALREADY_HELD, d.reason)

    def test_a_worktree_never_holds_two_leases(self):
        reg = {"stock-minimal-2": _lease("stock-minimal-2", WT_A)}
        d = self.claim(reg, copy=1)
        self.assertEqual((False, leases.CLAIM_HOLDS_OTHER_SLOT), (d.ok, d.reason))

    def test_a_requested_copy_leased_by_another_is_refused(self):
        reg = {"stock-minimal-2": _lease("stock-minimal-2", WT_A)}
        d = self.claim(reg, worktree=WT_B, copy=2)
        self.assertEqual((False, leases.CLAIM_SLOT_LEASED, "stock-minimal-2"),
                         (d.ok, d.reason, d.slot))
        d = self.claim(reg, worktree=WT_B, copy=3)
        self.assertEqual((True, "stock-minimal-3"), (d.ok, d.slot))


class UseTests(unittest.TestCase):

    def test_reasons(self):
        lease = _lease("stock-minimal-2", WT_A, token="tok")
        ref = leases.worktree_ref(lease)
        cases = [
            (None, lease, NOW, leases.USE_NO_WORKTREE_LEASE),
            ({"slot": "stock-minimal-2"}, lease, NOW, leases.USE_NO_WORKTREE_LEASE),
            (ref, None, NOW, leases.USE_LEASE_MISSING),
            (dict(ref, token="other"), lease, NOW, leases.USE_TOKEN_MISMATCH),
            (ref, lease, NOW + 3600, leases.USE_EXPIRED),
            (ref, lease, NOW + 1, leases.USE_OK),
        ]
        for r, l, now, want in cases:
            with self.subTest(want=want):
                d = leases.decide_use(r, l, now)
                self.assertEqual(want, d.reason)
                self.assertEqual(want == leases.USE_OK, d.ok)

    def test_worktree_ref_parse(self):
        self.assertIsNone(leases.parse_ksp_lease_ref({"slot": "nope", "token": "t"}))
        self.assertIsNone(leases.parse_ksp_lease_ref([]))
        self.assertEqual("stock-minimal",
                         leases.parse_ksp_lease_ref({"slot": "stock-minimal", "token": "t"})["slot"])


class DllPinTests(unittest.TestCase):

    def test_match_drift_and_unpinned(self):
        lease = _lease("stock-minimal", WT_A, dll="AB" * 32)
        self.assertIsNone(leases.decide_dll_pin(lease, "ab" * 32))
        self.assertEqual(("ab" * 32, "cd" * 32), leases.decide_dll_pin(lease, "CD" * 32))
        self.assertEqual(("ab" * 32, "missing"), leases.decide_dll_pin(lease, None))
        unpinned = _lease("stock-minimal", WT_A, dll=None)
        self.assertIsNone(unpinned["dllSource"])
        self.assertIn("unpinned", leases.decide_dll_pin(unpinned, "ab" * 32)[0])


class DescribeTests(unittest.TestCase):

    def test_describe_and_holder_shape(self):
        lease = _lease("stock-minimal-2", WT_A, owner="far-watch", ttl=3600)
        text = leases.describe_lease(lease, NOW + 600)
        self.assertIn("slot=stock-minimal-2 owner=far-watch", text)
        self.assertIn("expiresIn=50m", text)
        self.assertIn("dll=abababababab (claim)", text)
        holder = leases.holder_for_lease(lease)
        self.assertEqual("lease:stock-minimal-2 owner=far-watch", holder["selection"])

    def test_others_live(self):
        reg = {"stock-minimal": _lease("stock-minimal", WT_A),
               "stock-minimal-2": _lease("stock-minimal-2", WT_B),
               "stock-minimal-3": _lease("stock-minimal-3", WT_C, renewed=NOW - 9999)}
        self.assertEqual(["stock-minimal-2"], leases.others_live(reg, NOW, "stock-minimal"))
        self.assertEqual([], leases.others_live({"stock-minimal": reg["stock-minimal"]}, NOW,
                                                "stock-minimal"))


if __name__ == "__main__":
    unittest.main()
