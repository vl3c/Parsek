"""Parallel-run slot copies in the provisioner (plan section 5).

Pure cells pin the provlib decisions (the slot kRPC stamp, its VERIFY rule, the
slot-lock liveness rule, the copy-removal guards, the tasklist parse); shell
cells drive provision.py over a temp umbrella with injected liveness and a fake
process lister. Nothing here runs KSP, a network fetch, or touches a real PC.

    python -m unittest discover -s harness/provision
"""

import hashlib
import json
import os
import tempfile
import tomllib
import unittest
from unittest import mock

import machinelock
import provision
import provlib
import slots

PROFILES_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "profiles")

# sha256 of the golden kRPC settings.cfg before slots existed. Every provisioned
# instance and every pinned hash carries these bytes for 50000/50001.
LEGACY_DEFAULT_STAMP_SHA = "85f2bb64cc9630a09007bcb1844a3442ec0c0627485c675727da765386f01fd6"


def _sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _server_fields(text):
    lines = [l.strip() for l in text.splitlines()]
    out = {}
    for l in lines:
        if l.startswith("id = ") or l.startswith("name = "):
            k, v = l.split(" = ", 1)
            out[k] = v
    for key in ("rpc_port", "stream_port", "address"):
        i = lines.index("key = %s" % key)
        out[key] = lines[i + 1].split(" = ", 1)[1]
    return out


class SlotKrpcStampTests(unittest.TestCase):

    def test_default_output_is_byte_identical_to_the_pre_slot_stamp(self):
        self.assertEqual(_sha(provlib.stamp_krpc_settings(None)), LEGACY_DEFAULT_STAMP_SHA)
        self.assertEqual(_sha(provlib.stamp_krpc_settings("x", 50000, 50001)), LEGACY_DEFAULT_STAMP_SHA)
        self.assertEqual(provlib.krpc_golden_settings_lines(), provlib.KRPC_GOLDEN_SETTINGS_LINES)

    def test_stock_minimal_copy_1_keeps_the_legacy_bytes(self):
        ports = slots.krpc_ports("stock-minimal", 1)
        self.assertEqual(_sha(provlib.stamp_krpc_settings(None, *ports)), LEGACY_DEFAULT_STAMP_SHA)

    def test_each_slot_carries_its_ports_and_a_distinct_server_identity(self):
        ids, names = set(), set()
        for profile in slots.PROFILE_PORT_BASE:
            for copy in range(1, slots.MAX_COPIES + 1):
                rpc, stream = slots.krpc_ports(profile, copy)
                text = provlib.stamp_krpc_settings(None, rpc, stream)
                f = _server_fields(text)
                self.assertEqual((f["rpc_port"], f["stream_port"]), (str(rpc), str(stream)))
                self.assertEqual(f["address"], "127.0.0.1")
                ids.add(f["id"])
                names.add(f["name"])
        count = len(slots.PROFILE_PORT_BASE) * slots.MAX_COPIES
        self.assertEqual(len(ids), count)
        self.assertEqual(len(names), count)

    def test_only_the_four_server_lines_differ_from_the_default(self):
        default = provlib.stamp_krpc_settings(None).splitlines()
        other = provlib.stamp_krpc_settings(None, 50042, 50043).splitlines()
        self.assertEqual(len(default), len(other))
        changed = [(a.strip(), b.strip()) for a, b in zip(default, other) if a != b]
        self.assertEqual(len(changed), 4, changed)
        self.assertIn(("value = 50000", "value = 50042"), changed)
        self.assertIn(("value = 50001", "value = 50043"), changed)
        self.assertIn(("name = Default Server", "name = Harness Server 50042-50043"), changed)

    def test_default_port_id_is_the_fixed_default_id(self):
        self.assertEqual(provlib.krpc_server_id_for_port(50000), provlib.KRPC_DEFAULT_SERVER_ID)
        self.assertNotEqual(provlib.krpc_server_id_for_port(50002), provlib.KRPC_DEFAULT_SERVER_ID)

    def test_still_full_template_and_idempotent(self):
        once = provlib.stamp_krpc_settings(None, 50020, 50021)
        self.assertEqual(once, provlib.stamp_krpc_settings(once, 50020, 50021))
        self.assertIn("maxTimePerUpdate = 5000", once)
        self.assertIn("autoStartServers = True", once)
        self.assertTrue(once.endswith("}\n"))
        self.assertNotIn("\r", once)

    def test_bad_ports_raise(self):
        for rpc, stream in ((0, 1), (50000, 70000), (50000, 50000), (True, 50001), ("50000", 50001)):
            with self.assertRaises(ValueError):
                provlib.stamp_krpc_settings(None, rpc, stream)


class KrpcSettingsVerifyReasonTests(unittest.TestCase):

    def test_table(self):
        r = provlib.krpc_settings_verify_reason
        self.assertEqual(r("a", "a", "a"), "ok")
        self.assertEqual(r("a", None, "a"), "not-recorded")
        self.assertEqual(r(None, "a", "a"), "absent-on-disk")
        self.assertEqual(r("b", "a", "a"), "manifest-mismatch")
        # Copy 1 of stock-tree carrying the legacy 50000 stamp in file AND manifest.
        self.assertEqual(r("legacy", "legacy", "slot"), "not-slot-stamp")
        self.assertEqual(r("a", "a", None), "ok")
        for reason in ("ok", "not-recorded", "absent-on-disk"):
            self.assertIn(reason, provlib.KRPC_SETTINGS_VERIFY_PASS)
        for reason in ("manifest-mismatch", "not-slot-stamp"):
            self.assertNotIn(reason, provlib.KRPC_SETTINGS_VERIFY_PASS)

    def test_drift_field_repairs_by_restamp(self):
        self.assertEqual(provlib.component_of_diff_field("krpcSettingsSha256"),
                         provlib.KRPC_SETTINGS_REPAIR_TOKEN)
        plan = provlib.plan_repair([provlib.ManifestDiff("krpcSettingsSha256", "a", "b", "changed")])
        self.assertTrue(plan.krpc_settings)
        self.assertEqual(plan.unrepairable, ())
        self.assertFalse(provlib.plan_repair([]).krpc_settings)


class LiveSlotLocksTests(unittest.TestCase):
    NOW = 1_000_000.0

    def _lock(self, pid, age=10.0):
        return {"pid": pid, "timestamp": self.NOW - age, "selection": "lane-x"}

    def test_liveness_follows_the_machine_lock_rule(self):
        alive = {11, 12, 99}
        states = [
            provlib.SlotLockState("stock-minimal", self._lock(11)),                 # live
            provlib.SlotLockState("stock-minimal-2", self._lock(13)),               # dead pid
            provlib.SlotLockState("stock-minimal-3", self._lock(12, age=9 * 3600)),  # expired
            provlib.SlotLockState("stock-tree", None),                              # unparseable
            provlib.SlotLockState("stock-tree-2", None, empty=True),                # mid-create
            provlib.SlotLockState("modded-compat", self._lock(99)),                 # our own pid
        ]
        live = provlib.live_slot_locks(states, 99, self.NOW, lambda p: p in alive)
        self.assertEqual([s.slot for s in live], ["stock-minimal", "stock-tree-2"])

    def test_no_lease_means_pid_only(self):
        states = [provlib.SlotLockState("stock-minimal", self._lock(11, age=99 * 3600))]
        self.assertEqual(len(provlib.live_slot_locks(states, 1, self.NOW, lambda p: True,
                                                     lease_seconds=None)), 1)

    def test_describe(self):
        text = provlib.describe_slot_lock(provlib.SlotLockState("stock-minimal-2", self._lock(11)))
        self.assertIn("slot=stock-minimal-2", text)
        self.assertIn("pid=11", text)
        self.assertIn("lane-x", text)
        self.assertIn("empty lockfile", provlib.describe_slot_lock(
            provlib.SlotLockState("stock-tree", None, empty=True)))


class ReadSlotLocksTests(unittest.TestCase):

    def test_reads_only_slot_lock_names(self):
        with tempfile.TemporaryDirectory() as um:
            auto = os.path.join(um, "automation")
            os.makedirs(auto)
            files = {
                ".ksp-slot-stock-minimal.lock": json.dumps({"pid": 5, "timestamp": 1.0}),
                ".ksp-slot-stock-tree-2.lock": "",
                ".ksp-slot-stock-minimal.lock.stale-7": json.dumps({"pid": 7}),
                ".ksp-slot-stock-minimal-2.lock.tmp-8": json.dumps({"pid": 8}),
                ".ksp-slot-bogus.lock": json.dumps({"pid": 9}),
                ".ksp-machine.lock": json.dumps({"pid": 10}),
                ".ksp-slot-modded-compat.lock": "{not json",
            }
            for name, body in files.items():
                with open(os.path.join(auto, name), "w") as fh:
                    fh.write(body)
            states = {s.slot: s for s in machinelock.read_slot_locks(um)}
            self.assertEqual(sorted(states), ["modded-compat", "stock-minimal", "stock-tree-2"])
            self.assertEqual(states["stock-minimal"].lock["pid"], 5)
            self.assertTrue(states["stock-tree-2"].empty)
            self.assertIsNone(states["modded-compat"].lock)
            self.assertFalse(states["modded-compat"].empty)

    def test_missing_automation_dir(self):
        with tempfile.TemporaryDirectory() as um:
            self.assertEqual(machinelock.read_slot_locks(um), [])


class CopyRemovalDecisionTests(unittest.TestCase):

    def test_order_and_reasons(self):
        d = provlib.decide_copy_removal
        self.assertEqual(d(1, True, False, False, False).reason, "copy-1-never-removed")
        self.assertEqual(d(1, False, True, True, True).reason, "copy-1-never-removed")
        self.assertEqual(d(2, False, True, True, True).reason, "instance-aliases-dev-install")
        self.assertEqual(d(2, True, True, True, True).reason, "instance-is-a-link")
        self.assertEqual(d(2, True, False, True, True).reason, "slot-locked")
        self.assertEqual(d(2, True, False, False, True).reason, "ksp-running")
        ok = d(3, True, False, False, False)
        self.assertTrue(ok.ok)
        self.assertEqual(ok.reason, "ok")


class FreeSpaceMarginTests(unittest.TestCase):

    def test_copy_1_keeps_the_original_margin_extra_copies_leave_room(self):
        self.assertEqual(provlib.free_space_margin_bytes(1), 512 * 1024 * 1024)
        self.assertEqual(provlib.free_space_margin_bytes(2), provlib.EXTRA_COPY_FREE_SPACE_MARGIN_BYTES)
        self.assertGreater(provlib.free_space_margin_bytes(3), provlib.free_space_margin_bytes(1))


class TasklistParseTests(unittest.TestCase):

    def test_parse(self):
        text = ('"KSP_x64.exe","1234","Console","1","4,000,000 K"\r\n'
                '"ksp_x64.EXE","77","Console","1","1 K"\n'
                '"explorer.exe","5","Console","1","1 K"\n'
                '"KSP_x64.exe","notapid","Console","1","1 K"\n'
                'INFO: No tasks are running which match the specified criteria.\n')
        self.assertEqual(provlib.parse_tasklist_csv_pids(text, "KSP_x64.exe"), [1234, 77])
        self.assertEqual(provlib.parse_tasklist_csv_pids(None, "KSP_x64.exe"), [])
        self.assertEqual(provlib.parse_tasklist_csv_pids(
            "INFO: No tasks are running which match the specified criteria.", "KSP_x64.exe"), [])


class ProfilesAreSlotsTests(unittest.TestCase):
    """ctx.slot (the instance dir basename) must equal the profile name for copy
    1, so provision and run.py derive the same kRPC ports for the same dir."""

    def test_every_profile_instance_dir_is_its_copy_1_slot_dir(self):
        names = sorted(f[:-5] for f in os.listdir(PROFILES_DIR) if f.endswith(".toml"))
        self.assertEqual(names, sorted(slots.PROFILE_PORT_BASE))
        for name in names:
            with open(os.path.join(PROFILES_DIR, name + ".toml"), "rb") as fh:
                prof = tomllib.load(fh)
            self.assertEqual(prof["name"], name)
            self.assertEqual(prof["instanceDir"], "/".join(slots.instance_relpath(name, 1)))


def _ctx(um, profile="stock-minimal", copy=1, dry_run=False, instance_dir=None):
    prof = {"instanceDir": instance_dir or "automation/%s" % profile}
    return provision.ProvisionContext(
        profile_name=profile, pins={}, profile=prof, umbrella_root=um,
        dry_run=dry_run, repair=False, parsek_dll_override=None, copy=copy)


class ContextSlotTests(unittest.TestCase):

    def test_copy_1_is_the_profile_dir(self):
        ctx = _ctx("/um", "stock-tree")
        self.assertEqual(ctx.instance_rel, "automation/stock-tree")
        self.assertEqual(ctx.slot, "stock-tree")
        self.assertEqual(ctx.krpc_ports, (50020, 50021))

    def test_copy_k_dir_and_ports(self):
        ctx = _ctx("/um", "stock-minimal", copy=3)
        self.assertEqual(ctx.instance_rel, "automation/stock-minimal-3")
        self.assertTrue(slots.same_instance_dir(ctx.instance_dir,
                                                slots.instance_dir("/um", "stock-minimal-3")))
        self.assertEqual(ctx.slot, "stock-minimal-3")
        self.assertEqual(ctx.krpc_ports, (50004, 50005))

    def test_non_slot_instance_keeps_legacy_ports(self):
        ctx = _ctx("/um", "t", instance_dir="automation/test")
        self.assertEqual(ctx.krpc_ports, (50000, 50001))

    def test_cli_validation(self):
        v = provision.validate_copy_args
        self.assertIsNone(v("stock-minimal", 1, False, False))
        self.assertIsNone(v("stock-minimal", 3, False, True))
        self.assertIsNone(v("stock-minimal", 2, True, False))
        self.assertIn("copy 1", v("stock-minimal", 1, True, False))
        self.assertIn("exclusive", v("stock-minimal", 2, True, True))
        self.assertIn("--copy 0", v("stock-minimal", 0, False, False))
        self.assertIn("--copy 11", v("stock-minimal", 11, False, False))
        self.assertIn("unknown profile", v("nope", 2, False, False))

    def test_dry_run_action_plan_names_the_copy_dir(self):
        import contextlib
        import io
        ctx = _ctx("/um", "stock-minimal", copy=2, dry_run=True)
        ctx.profile = dict(ctx.profile, devSourcedMods=["000_Harmony"])
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            provision.print_action_plan(ctx)
        self.assertIn("automation/stock-minimal-2/GameData/000_Harmony", buf.getvalue())
        self.assertNotIn("automation/stock-minimal/", buf.getvalue())


class SlotStampShellTests(unittest.TestCase):

    def _quiet(self):
        return mock.patch("builtins.print")

    def test_copy_stamp_writes_slot_ports_and_verify_accepts_it(self):
        with tempfile.TemporaryDirectory() as um, self._quiet():
            ctx = _ctx(um, "stock-minimal", copy=2)
            provision._stamp_krpc_settings(ctx)
            with open(provision._krpc_settings_path(ctx), "r", encoding="utf-8") as fh:
                text = fh.read()
            f = _server_fields(text)
            self.assertEqual((f["rpc_port"], f["stream_port"]), ("50002", "50003"))
            self.assertEqual(ctx.krpc_settings_sha, provision._slot_krpc_stamp_sha(ctx))
            manifest = {"components": {}, "junctionTargets": {}, "devSourcedMods": {},
                        "krpcSettingsSha256": ctx.krpc_settings_sha}
            self.assertTrue(provision.phase_verify(ctx, manifest))

    def test_stock_minimal_copy_1_stamp_is_the_legacy_hash(self):
        with tempfile.TemporaryDirectory() as um, self._quiet():
            ctx = _ctx(um, "stock-minimal")
            provision._stamp_krpc_settings(ctx)
            self.assertEqual(ctx.krpc_settings_sha, LEGACY_DEFAULT_STAMP_SHA)

    def test_legacy_50000_stamp_on_stock_tree_copy_1_drifts_then_restamp_converges(self):
        with tempfile.TemporaryDirectory() as um, self._quiet():
            ctx = _ctx(um, "stock-tree")
            path = provision._krpc_settings_path(ctx)
            os.makedirs(os.path.dirname(path))
            with open(path, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(provlib.stamp_krpc_settings(None))
            manifest = {"components": {}, "junctionTargets": {}, "devSourcedMods": {},
                        "krpcSettingsSha256": LEGACY_DEFAULT_STAMP_SHA}
            self.assertFalse(provision.phase_verify(ctx, manifest))
            self.assertEqual([d.field for d in ctx.verify_drift], ["krpcSettingsSha256"])
            self.assertTrue(provlib.plan_repair(ctx.verify_drift).krpc_settings)
            # The re-stamp every provision / repair pass does moves it to 50020.
            provision._stamp_krpc_settings(ctx)
            manifest["krpcSettingsSha256"] = ctx.krpc_settings_sha
            self.assertTrue(provision.phase_verify(ctx, manifest))
            with open(path, "r", encoding="utf-8") as fh:
                self.assertIn("value = 50020", fh.read())

    def test_manifest_records_slot_keys(self):
        with tempfile.TemporaryDirectory() as um, self._quiet():
            ctx = _ctx(um, "stock-tree", copy=2, dry_run=True)
            m = provision.phase_manifest(ctx, {}, {}, {}, {}, {})
            self.assertEqual(m["slot"], "stock-tree-2")
            self.assertEqual(m["copy"], 2)
            self.assertEqual((m["krpcRpcPort"], m["krpcStreamPort"]), (50022, 50023))
            self.assertEqual(m["instanceDir"], "automation/stock-tree-2")
            self.assertEqual(m["profile"], "stock-tree")
            for key in ("slot", "copy", "krpcRpcPort", "krpcStreamPort", "instanceDir"):
                self.assertNotIn(key, provlib.ADMISSION_KEYS)


def _umbrella_with_dev(um):
    dev = os.path.join(um, "Kerbal Space Program")
    os.makedirs(os.path.join(dev, "GameData", "Squad", "Parts"))
    with open(os.path.join(dev, "GameData", "Squad", "Parts", "keep.cfg"), "w") as fh:
        fh.write("stock")
    os.makedirs(os.path.join(um, "automation"))
    return dev


def _write_slot_lock(um, slot, pid):
    path = slots.slot_lock_path(um, slot)
    with open(path, "w") as fh:
        json.dump({"pid": pid, "timestamp": provision._now(), "selection": "lane"}, fh)
    return path


class PreflightSlotLockTests(unittest.TestCase):

    def _run(self, um, dry_run, alive):
        ctx = _ctx(um, "stock-minimal", dry_run=dry_run)
        with mock.patch("builtins.print"), \
                mock.patch.object(provision, "_pid_alive", lambda pid: alive), \
                mock.patch.object(provision, "_ksp_running_against", lambda d: False):
            provision.phase_preflight(ctx)
            code = provision._finish(ctx, 0)
        return ctx, code

    def test_live_slot_lock_refuses_live_and_dry_run(self):
        for dry_run in (True, False):
            with tempfile.TemporaryDirectory() as um:
                _umbrella_with_dev(um)
                _write_slot_lock(um, "stock-minimal-2", os.getpid() + 1)
                ctx, code = self._run(um, dry_run, alive=True)
                self.assertTrue(ctx.aborted)
                self.assertEqual(code, 2)
                self.assertIn("EC-10", ctx.abort_reason)
                self.assertIn("slot=stock-minimal-2", ctx.abort_reason)
                # The machine lock this run took is released again.
                self.assertFalse(os.path.exists(machinelock.lock_path_for(um)))

    def test_stale_slot_lock_does_not_block(self):
        with tempfile.TemporaryDirectory() as um:
            _umbrella_with_dev(um)
            _write_slot_lock(um, "stock-minimal-2", os.getpid() + 1)
            ctx, code = self._run(um, True, alive=False)
            self.assertFalse(ctx.aborted, ctx.abort_reason)
            self.assertEqual(code, 0)


@unittest.skipUnless(hasattr(os, "symlink"), "needs os.symlink")
class RemoveCopyTests(unittest.TestCase):
    """--remove: junctions (symlink stand-ins on Linux) are unlinked, never
    followed, so the DEV install's stock folders survive."""

    def _copy_dir(self, um, dev, slot="stock-minimal-2"):
        inst = slots.instance_dir(um, slot)
        os.makedirs(os.path.join(inst, "GameData", "Parsek", "Plugins"))
        with open(os.path.join(inst, "GameData", "Parsek", "Plugins", "Parsek.dll"), "wb") as fh:
            fh.write(b"dll")
        with open(os.path.join(inst, "KSP_x64.exe"), "wb") as fh:
            fh.write(b"exe")
        try:
            os.symlink(os.path.join(dev, "GameData", "Squad"), os.path.join(inst, "GameData", "Squad"),
                       target_is_directory=True)
            os.symlink(os.path.join(dev, "GameData", "Squad", "Parts", "keep.cfg"),
                       os.path.join(inst, "GameData", "Parsek", "linked.cfg"))
        except OSError as exc:  # Windows without the symlink privilege
            self.skipTest("cannot create symlinks: %s" % exc)
        return inst

    def _remove(self, um, copy=2, dry_run=False, lister=lambda: [], alive=True):
        ctx = _ctx(um, "stock-minimal", copy=copy, dry_run=dry_run)
        ctx.remove = True
        with mock.patch("builtins.print"), \
                mock.patch.object(provision, "_pid_alive", lambda pid: alive):
            code = provision.remove_copy(ctx, lister=lister)
        return ctx, code

    def _dev_intact(self, dev):
        return os.path.isfile(os.path.join(dev, "GameData", "Squad", "Parts", "keep.cfg"))

    def test_removes_the_copy_and_leaves_the_junction_target(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev)
            ctx, code = self._remove(um)
            self.assertEqual(code, 0, ctx.abort_reason)
            self.assertFalse(os.path.lexists(inst))
            self.assertTrue(self._dev_intact(dev))
            self.assertFalse(os.path.exists(machinelock.lock_path_for(um)))

    def test_link_walk_does_not_descend_into_links(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev)
            found = provision._find_reparse_points(inst)
            self.assertEqual(sorted(os.path.relpath(p, inst) for p in found),
                             sorted([os.path.join("GameData", "Squad"),
                                     os.path.join("GameData", "Parsek", "linked.cfg")]))

    def test_dry_run_deletes_nothing(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev)
            ctx, code = self._remove(um, dry_run=True)
            self.assertEqual(code, 0)
            self.assertTrue(os.path.isfile(os.path.join(inst, "KSP_x64.exe")))
            self.assertTrue(os.path.islink(os.path.join(inst, "GameData", "Squad")))

    def test_copy_1_is_refused(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev, slot="stock-minimal")
            ctx, code = self._remove(um, copy=1)
            self.assertEqual(code, 2)
            self.assertIn("copy-1-never-removed", ctx.abort_reason)
            self.assertTrue(os.path.isdir(inst))

    def test_live_slot_lock_refuses(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev)
            _write_slot_lock(um, "stock-minimal-2", os.getpid() + 1)
            ctx, code = self._remove(um)
            self.assertEqual(code, 2)
            self.assertIn("EC-10", ctx.abort_reason)
            self.assertTrue(os.path.isdir(inst))

    def test_another_slots_lock_does_not_block_removal(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev)
            _write_slot_lock(um, "stock-minimal-3", os.getpid() + 1)
            ctx, code = self._remove(um)
            self.assertEqual(code, 0, ctx.abort_reason)
            self.assertFalse(os.path.lexists(inst))

    def test_live_machine_lock_refuses(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev)
            with open(machinelock.lock_path_for(um), "w") as fh:
                json.dump({"pid": os.getpid() + 1, "timestamp": provision._now()}, fh)
            ctx, code = self._remove(um)
            self.assertEqual(code, 2)
            self.assertIn("EC-10", ctx.abort_reason)
            self.assertTrue(os.path.isdir(inst))
            self.assertTrue(os.path.exists(machinelock.lock_path_for(um)))

    def test_ksp_running_from_the_copy_refuses_path_scoped(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            inst = self._copy_dir(um, dev)
            own = os.path.join(inst, "KSP_x64.exe")
            ctx, code = self._remove(um, lister=lambda: [(4321, own)])
            self.assertEqual(code, 2)
            self.assertIn("EC-1", ctx.abort_reason)
            self.assertIn("4321", ctx.abort_reason)
            self.assertTrue(os.path.isdir(inst))
            # An unreadable exe path counts as running here.
            ctx, code = self._remove(um, lister=lambda: [(9, None)])
            self.assertEqual(code, 2)
            self.assertTrue(os.path.isdir(inst))
            # copy 1's KSP ("stock-minimal" is a string prefix of "stock-minimal-2")
            # and the owner's own play install do not block.
            other = [(1, os.path.join(slots.instance_dir(um, "stock-minimal"), "KSP_x64.exe")),
                     (2, os.path.join(dev, "KSP_x64.exe"))]
            ctx, code = self._remove(um, lister=lambda: other)
            self.assertEqual(code, 0, ctx.abort_reason)
            self.assertFalse(os.path.lexists(inst))

    def test_instance_dir_that_is_a_link_is_refused(self):
        with tempfile.TemporaryDirectory() as um:
            dev = _umbrella_with_dev(um)
            elsewhere = os.path.join(um, "elsewhere")
            os.makedirs(elsewhere)
            with open(os.path.join(elsewhere, "precious.txt"), "w") as fh:
                fh.write("x")
            os.symlink(elsewhere, slots.instance_dir(um, "stock-minimal-2"), target_is_directory=True)
            ctx, code = self._remove(um)
            self.assertEqual(code, 2)
            self.assertIn("instance-is-a-link", ctx.abort_reason)
            self.assertTrue(os.path.isfile(os.path.join(elsewhere, "precious.txt")))
            self.assertTrue(self._dev_intact(dev))

    def test_absent_copy_is_a_no_op(self):
        with tempfile.TemporaryDirectory() as um:
            _umbrella_with_dev(um)
            ctx, code = self._remove(um)
            self.assertEqual(code, 0, ctx.abort_reason)


class ProcessListerTests(unittest.TestCase):

    def test_off_windows_lists_nothing(self):
        if os.name == "nt":
            self.skipTest("Windows lists real processes")
        self.assertEqual(provision._list_ksp_processes(), [])
        self.assertIsNone(provision._process_image_path(os.getpid()))

    def test_running_in_dir_filters_by_exact_dir(self):
        inst = os.path.join(tempfile.gettempdir(), "automation", "stock-minimal-2")
        procs = [(1, os.path.join(inst, "KSP_x64.exe")),
                 (2, os.path.join(os.path.dirname(inst), "stock-minimal", "KSP_x64.exe")),
                 (3, None)]
        self.assertEqual([p for p, _ in provision._ksp_running_in_dir(inst, lambda: procs)], [1, 3])


if __name__ == "__main__":
    unittest.main()
