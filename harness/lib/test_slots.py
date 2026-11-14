import os
import tempfile
import unittest

import slots


class SlotNameTests(unittest.TestCase):
    def test_copy_one_is_the_profile_dir(self):
        self.assertEqual(slots.slot_name("stock-minimal"), "stock-minimal")
        self.assertEqual(slots.slot_name("stock-minimal", 2), "stock-minimal-2")

    def test_parse_round_trips_every_profile_and_copy(self):
        for profile in slots.PROFILE_PORT_BASE:
            for copy in range(1, slots.MAX_COPIES + 1):
                self.assertEqual(slots.parse_slot_name(slots.slot_name(profile, copy)),
                                 (profile, copy))

    def test_parse_refuses_foreign_and_malformed_names(self):
        for bad in ["", "stock-minimal-1", "stock-minimal-0", "stock-minimal-02",
                    "stock-minimal-x", "results-archive", ".artifact-cache",
                    "stock-minimal-%d" % (slots.MAX_COPIES + 1)]:
            with self.assertRaises(slots.SlotError, msg=bad):
                slots.parse_slot_name(bad)

    def test_bad_copy_and_profile_refused(self):
        for copy in [0, -1, slots.MAX_COPIES + 1, True, 1.0]:
            with self.assertRaises(slots.SlotError):
                slots.slot_name("stock-minimal", copy)
        with self.assertRaises(slots.SlotError):
            slots.slot_name("nope", 1)


class PortTests(unittest.TestCase):
    def test_stock_minimal_copy_one_keeps_the_legacy_ports(self):
        self.assertEqual(slots.krpc_ports("stock-minimal", 1), (50000, 50001))

    def test_every_slot_of_every_profile_has_unique_ports(self):
        seen = {}
        for profile in slots.PROFILE_PORT_BASE:
            for copy in range(1, slots.MAX_COPIES + 1):
                for port in slots.krpc_ports(profile, copy):
                    self.assertNotIn(port, seen, "%s#%d" % (profile, copy))
                    seen[port] = (profile, copy)

    def test_port_blocks_do_not_overlap(self):
        bases = sorted(slots.PROFILE_PORT_BASE.values())
        for a, b in zip(bases, bases[1:]):
            self.assertGreaterEqual(b - a, slots.PORT_BLOCK)


class PathTests(unittest.TestCase):
    def test_instance_dir_and_lock(self):
        root = os.path.abspath("umb")
        self.assertEqual(slots.instance_dir(root, "stock-tree-3"),
                         os.path.join(root, "automation", "stock-tree-3"))
        lock = slots.slot_lock_path(root, "stock-minimal-2")
        self.assertEqual(os.path.basename(lock), ".ksp-slot-stock-minimal-2.lock")
        self.assertEqual(slots.slot_from_lock_name(os.path.basename(lock)), "stock-minimal-2")
        self.assertIsNone(slots.slot_from_lock_name(".ksp-machine.lock"))
        self.assertIsNone(slots.slot_from_lock_name(".ksp-slot-bogus.lock"))

    def test_prefix_is_not_the_same_instance(self):
        with tempfile.TemporaryDirectory() as tmp:
            a = os.path.join(tmp, "stock-minimal")
            b = os.path.join(tmp, "stock-minimal-2")
            os.makedirs(a)
            os.makedirs(b)
            self.assertTrue(slots.same_instance_dir(a, a + os.sep))
            self.assertFalse(slots.same_instance_dir(a, b))
            self.assertFalse(slots.exe_belongs_to_instance(os.path.join(b, "KSP_x64.exe"), a))
            self.assertTrue(slots.exe_belongs_to_instance(os.path.join(a, "KSP_x64.exe"), a))
            self.assertTrue(slots.exe_belongs_to_instance(None, a))

    def test_discover_orders_and_filters(self):
        names = ["stock-tree", "stock-minimal-3", "results-archive", "stock-minimal",
                 "stock-minimal-2", ".artifact-cache", "modded-compat"]
        self.assertEqual(slots.discover_slots(names),
                         ["modded-compat", "stock-minimal", "stock-minimal-2",
                          "stock-minimal-3", "stock-tree"])
        self.assertEqual(slots.discover_slots(names, ["stock-minimal"]),
                         ["stock-minimal", "stock-minimal-2", "stock-minimal-3"])


class LaunchSpacingTests(unittest.TestCase):
    def test_wait(self):
        self.assertEqual(slots.launch_wait_seconds(None, 100.0), 0.0)
        self.assertEqual(slots.launch_wait_seconds(100.0, 105.0, 15.0), 10.0)
        self.assertEqual(slots.launch_wait_seconds(100.0, 130.0, 15.0), 0.0)
        self.assertEqual(slots.launch_wait_seconds(200.0, 100.0, 15.0), 0.0)


if __name__ == "__main__":
    unittest.main()
