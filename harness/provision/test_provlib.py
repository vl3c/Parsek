"""Unit tests for provlib.py, the pure decision logic of the M-A6 provisioner.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/provision

Each test names the regression it guards (design Test Plan). No pytest, no KSP,
no network, no filesystem writes.
"""

import os
import tomllib
import unittest

import provlib


PROFILES_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "profiles")


class RealProfileFileTests(unittest.TestCase):
    """Guards BLOCKER 1: a bare `stackComponents` key placed AFTER a
    `[[optionalMods]]` array-of-tables header binds to that table entry (not the
    top level), silently leaving the profile with NO stack components -- a live
    run would then install nothing while still writing a manifest. Loads the two
    REAL on-disk profile files (not inline dicts, which cannot catch a TOML
    placement bug) and asserts the top-level stack list + a non-empty
    devSourcedMods for both."""

    EXPECTED_STACK = ["krpc", "testingtools", "mechjeb2", "krpc_mechjeb", "parsek"]

    def _load(self, name):
        with open(os.path.join(PROFILES_DIR, name), "rb") as fh:
            return tomllib.load(fh)

    def test_stock_minimal_stack_and_devmods(self):
        p = self._load("stock-minimal.toml")
        self.assertEqual(p.get("stackComponents"), self.EXPECTED_STACK)
        self.assertTrue(p.get("devSourcedMods"), "stock-minimal devSourcedMods must be non-empty")

    def test_modded_compat_stack_and_devmods(self):
        p = self._load("modded-compat.toml")
        self.assertEqual(p.get("stackComponents"), self.EXPECTED_STACK)
        self.assertTrue(p.get("devSourcedMods"), "modded-compat devSourcedMods must be non-empty")

    def test_modded_compat_stack_not_swallowed_by_optionalmods(self):
        # The precise BLOCKER 1 regression: the stack list must be top-level, and
        # the optionalMods entry must NOT carry a stackComponents key.
        p = self._load("modded-compat.toml")
        for entry in p.get("optionalMods", []):
            self.assertNotIn("stackComponents", entry)


class PhaseInstallEmptyStackTests(unittest.TestCase):
    """Guards reviewer 14 (the mask that hid BLOCKER 1): phase_install must WARN
    loudly when a profile has no stackComponents instead of silently doing
    nothing."""

    def test_empty_stack_warns(self):
        import provision
        ctx = provision.ProvisionContext(
            profile_name="broken", pins={}, profile={"stackComponents": []},
            umbrella_root=".", dry_run=True, repair=False, parsek_dll_override=None)
        provision.phase_install(ctx)
        self.assertTrue(any("NO stackComponents" in l for l in ctx.log_lines),
                        "empty stackComponents must produce a WARN")
        self.assertTrue(any(l.startswith("[Provision][Warn][Install]") for l in ctx.log_lines))

    def test_nonempty_stack_no_warn(self):
        import provision
        ctx = provision.ProvisionContext(
            profile_name="ok", pins={},
            profile={"stackComponents": ["krpc", "parsek"]},
            umbrella_root=".", dry_run=True, repair=False, parsek_dll_override=None)
        provision.phase_install(ctx)
        self.assertFalse(any("NO stackComponents" in l for l in ctx.log_lines))


class LivePhasesImplementedTests(unittest.TestCase):
    """M-A6.1: the heavy live phases are implemented, so the old EC-LIVE guard is
    gone. A live run now genuinely provisions; it aborts at DOWNLOAD when a
    consumed sha256 is still OPEN (the mechjeb2 pin, EC-13, no-network guard), and
    a live --repair is no longer blocked up front.

    Live-mode ctxs use a throwaway umbrella tempdir: a non-dry-run abort() logs
    through log(), which writes provision-log.txt under the instance dir, and that
    must never land in the repo tree."""

    def test_ec_live_guard_removed(self):
        import provision
        self.assertFalse(hasattr(provision, "_guard_live_unimplemented"),
                         "the EC-LIVE unimplemented-phase guard must be gone once live phases land")

    def test_live_download_open_pin_aborts_ec13_no_network(self):
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as umbrella:
            ctx = provision.ProvisionContext(
                profile_name="x",
                pins={"krpc": {"releaseZipUrl": "OPEN", "releaseZipSha256": "OPEN"},
                      "krpc_mechjeb": {}, "mechjeb2": {"downloadUrl": "OPEN", "sha256": "OPEN"}},
                profile={}, umbrella_root=umbrella, dry_run=False, repair=False,
                parsek_dll_override=None)
            provision.phase_download(ctx)  # must not touch the network
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-13", ctx.abort_reason)


class DevSourcedModVerifyTests(unittest.TestCase):
    """BLOCKER 1: dev-sourced mods are verified INSTANCE-side. phase_clone hashes
    the instance copy (partial-copy detection), phase_verify re-hashes the
    instance GameData/<mod> against the manifest (swapped-DLL / injected-file
    drift), and --repair scoped-deletes + re-copies so an INJECTED extra file --
    which a plain overwrite-only re-copy can never remove -- converges.

    Live-mode ctxs use a throwaway umbrella tempdir under automation/ (so the
    EC-16 fence + provision-log writes stay out of the repo)."""

    def _ctx(self, umbrella):
        import provision
        os.makedirs(os.path.join(umbrella, "Kerbal Space Program", "GameData"), exist_ok=True)
        profile = {"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program",
                   "devSourcedMods": ["MyMod"]}
        return provision.ProvisionContext(
            profile_name="t", pins={}, profile=profile, umbrella_root=umbrella,
            dry_run=False, repair=False, parsek_dll_override=None)

    def _make_src_mod(self, ctx, files):
        src = os.path.join(ctx.dev_install, "GameData", "MyMod")
        for rel, data in files.items():
            p = os.path.join(src, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as fh:
                fh.write(data)
        return src

    def _manifest(self, dev_status):
        return {"components": {}, "junctionTargets": {}, "devSourcedMods": dev_status}

    def _full_copy(self, ctx):
        import provision
        src = self._make_src_mod(ctx, {"a.dll": b"aaaa", "sub/b.cfg": b"bbbb"})
        dst = os.path.join(ctx.instance_dir, "GameData", "MyMod")
        h = provision._copy_and_verify_dev_mod(ctx, "MyMod", src, dst)
        return src, dst, h

    def test_partial_copy_aborts_ec3(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            src = self._make_src_mod(ctx, {"a.dll": b"aaaa", "sub/b.cfg": b"bbbb"})
            dst = os.path.join(ctx.instance_dir, "GameData", "MyMod")
            orig = provision._copy_dir

            def partial(c, s, d, skip_top=None):
                os.makedirs(d, exist_ok=True)
                with open(os.path.join(d, "a.dll"), "wb") as fh:
                    fh.write(b"aaaa")  # only ONE of the two source files
                return 1, 4

            provision._copy_dir = partial
            try:
                res = provision._copy_and_verify_dev_mod(ctx, "MyMod", src, dst)
            finally:
                provision._copy_dir = orig
            self.assertIsNone(res)
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-3", ctx.abort_reason)
            self.assertTrue(any("partial copy" in l for l in ctx.log_lines))

    def test_full_copy_verifies_clean(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            _src, _dst, h = self._full_copy(ctx)
            self.assertTrue(h)
            self.assertFalse(ctx.aborted)
            ok = provision.phase_verify(ctx, self._manifest({"MyMod": h}))
            self.assertTrue(ok)
            self.assertEqual(getattr(ctx, "verify_drift", []), [])

    def test_swapped_dll_detected_and_repaired(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            _src, dst, h = self._full_copy(ctx)
            # Swap the installed DLL for different bytes (post-clone clobber).
            with open(os.path.join(dst, "a.dll"), "wb") as fh:
                fh.write(b"EVIL")
            ok = provision.phase_verify(ctx, self._manifest({"MyMod": h}))
            self.assertFalse(ok)
            self.assertTrue(any(d.field == "devSourcedMods.MyMod" for d in ctx.verify_drift))
            # Repair -> re-copy from source -> converges.
            provision._repair_dev_mods(ctx, ["MyMod"])
            ok2 = provision.phase_verify(ctx, self._manifest({"MyMod": h}))
            self.assertTrue(ok2)

    def test_injected_extra_file_removed_by_repair(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            _src, dst, h = self._full_copy(ctx)
            injected = os.path.join(dst, "sub", "evil.dll")
            with open(injected, "wb") as fh:
                fh.write(b"injected")
            ok = provision.phase_verify(ctx, self._manifest({"MyMod": h}))
            self.assertFalse(ok, "an injected extra file must drift")
            provision._repair_dev_mods(ctx, ["MyMod"])
            self.assertFalse(os.path.exists(injected),
                             "scoped-delete + re-copy must remove the injected file")
            ok2 = provision.phase_verify(ctx, self._manifest({"MyMod": h}))
            self.assertTrue(ok2)

    def test_missing_instance_mod_drifts(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            _src, dst, h = self._full_copy(ctx)
            import shutil
            shutil.rmtree(dst)
            ok = provision.phase_verify(ctx, self._manifest({"MyMod": h}))
            self.assertFalse(ok)
            self.assertTrue(any(d.field == "devSourcedMods.MyMod" and d.actual is None
                                for d in ctx.verify_drift))

    def test_scoped_delete_refuses_outside_instance_gamedata(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            # A target OUTSIDE the instance GameData must never be deleted.
            outside = os.path.join(ctx.dev_install, "GameData", "MyMod")
            self._make_src_mod(ctx, {"a.dll": b"aaaa"})
            self.assertTrue(os.path.isdir(outside))
            ok = provision._scoped_delete_instance_subtree(ctx, outside)
            self.assertFalse(ok)
            self.assertTrue(os.path.isdir(outside), "dev-install path must survive")


class DeployAbortTests(unittest.TestCase):
    """SF8: with no --parsek-dll override and no worktree bin/Debug build, a LIVE
    DEPLOY aborts EC-9 demanding --parsek-dll (never deploys an unrelated
    worktree's DLL); a dry-run only warns and exits cleanly."""

    def _ctx(self, um, dry_run):
        import provision
        return provision.ProvisionContext(
            profile_name="t", pins={}, profile={"instanceDir": "automation/test"},
            umbrella_root=um, dry_run=dry_run, repair=False, parsek_dll_override=None)

    def test_live_deploy_aborts_when_no_source(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            saved = provision.WORKTREE_ROOT
            provision.WORKTREE_ROOT = os.path.join(um, "empty-worktree")
            try:
                ctx = self._ctx(um, dry_run=False)
                provision.phase_deploy(ctx)
            finally:
                provision.WORKTREE_ROOT = saved
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-9", ctx.abort_reason)
            self.assertTrue(any("--parsek-dll" in l for l in ctx.log_lines))

    def test_dry_run_no_source_warns_not_abort(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            saved = provision.WORKTREE_ROOT
            provision.WORKTREE_ROOT = os.path.join(um, "empty-worktree")
            try:
                ctx = self._ctx(um, dry_run=True)
                provision.phase_deploy(ctx)
            finally:
                provision.WORKTREE_ROOT = saved
            self.assertFalse(ctx.aborted)
            self.assertTrue(any("would ABORT" in l for l in ctx.log_lines))


class BufferedLogTests(unittest.TestCase):
    """SF2: a live run whose instanceDir aliases the dev install aborts EC-16 in
    PREFLIGHT WITHOUT creating provision-log.txt at the alias target -- log lines
    stay buffered until the gate opens the file."""

    def test_mis_aliased_profile_writes_no_log_before_gate(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            dev = os.path.join(um, "Kerbal Space Program")
            os.makedirs(dev, exist_ok=True)
            ctx = provision.ProvisionContext(
                profile_name="t", pins={},
                profile={"instanceDir": "Kerbal Space Program",
                         "baseInstall": "Kerbal Space Program"},
                umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)
            provision.phase_preflight(ctx)
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-16", ctx.abort_reason)
            self.assertFalse(ctx.log_file_enabled)
            self.assertFalse(os.path.exists(
                os.path.join(dev, "GameData", "Parsek", "provision-log.txt")),
                "no provision-log.txt may be written at the alias target before the gate")


class LivePhaseExceptionTests(unittest.TestCase):
    """SF3: an OSError / subprocess failure / missing dotnet mid-live-run is
    caught by run(), converted to a clean EC abort + exit 2 with the lock
    released; the .provision-incomplete marker is left in place."""

    def _live_ctx(self, um):
        import provision
        os.makedirs(os.path.join(um, "Kerbal Space Program"), exist_ok=True)
        return provision.ProvisionContext(
            profile_name="t", pins={},
            profile={"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program"},
            umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)

    def test_dotnet_missing_filenotfound_aborts_ec4(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._live_ctx(um)
            saved = {n: getattr(provision, n) for n in
                     ("phase_preflight", "phase_pin", "phase_download", "phase_build_tt")}
            provision.phase_preflight = lambda c: None
            provision.phase_pin = lambda c: {}
            provision.phase_download = lambda c: None

            def boom(c, resolved):
                raise FileNotFoundError("dotnet")

            provision.phase_build_tt = boom
            try:
                code = provision.run(ctx)
            finally:
                for n, f in saved.items():
                    setattr(provision, n, f)
            self.assertEqual(code, 2)
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-4", ctx.abort_reason)

    def test_copy_oserror_aborts_ec6_marker_stays_lock_released(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._live_ctx(um)
            provision._write_incomplete_marker(ctx)
            marker = provision._incomplete_marker_path(ctx)
            lock_path = os.path.join(ctx.parsek_gamedata, ".provision.lock")
            with open(lock_path, "w", encoding="utf-8") as fh:
                fh.write("{}")
            ctx.lock_path = lock_path
            ctx.lock_acquired = True
            saved = {n: getattr(provision, n) for n in
                     ("phase_preflight", "phase_pin", "phase_download",
                      "phase_build_tt", "phase_pair", "phase_clone")}
            provision.phase_preflight = lambda c: None
            provision.phase_pin = lambda c: {}
            provision.phase_download = lambda c: None
            provision.phase_build_tt = lambda c, r: None
            provision.phase_pair = lambda c: None

            def boom(c):
                raise OSError("disk gone")

            provision.phase_clone = boom
            try:
                code = provision.run(ctx)
            finally:
                for n, f in saved.items():
                    setattr(provision, n, f)
            self.assertEqual(code, 2)
            self.assertIn("EC-6", ctx.abort_reason)
            self.assertTrue(os.path.isfile(marker), "marker must stay after a failed live run")
            self.assertFalse(os.path.isfile(lock_path), "owned lock must be released")


class KrpcZipLayoutTests(unittest.TestCase):
    """SF4: a kRPC release zip missing a compile DLL or shipping TestingTools.dll
    ABORTS DOWNLOAD (EC-3/GT-5) instead of logging and proceeding to cache it."""

    def _ctx(self):
        import provision
        return provision.ProvisionContext(
            profile_name="t",
            pins={"krpc": {"releaseCompileDlls": ["KRPC.Core.dll", "KRPC.SpaceCenter.dll"],
                           "mustNotContain": ["TestingTools.dll"]}},
            profile={}, umbrella_root=".", dry_run=True, repair=False, parsek_dll_override=None)

    def _zip_bytes(self, names):
        import io
        import zipfile
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            for n in names:
                zf.writestr(n, b"x")
        return buf.getvalue()

    def test_good_layout_passes(self):
        import provision
        ctx = self._ctx()
        data = self._zip_bytes(["GameData/kRPC/KRPC.Core.dll", "GameData/kRPC/KRPC.SpaceCenter.dll"])
        self.assertTrue(provision._assert_krpc_zip_layout(ctx, data))
        self.assertFalse(ctx.aborted)

    def test_missing_compile_dll_aborts(self):
        import provision
        ctx = self._ctx()
        data = self._zip_bytes(["GameData/kRPC/KRPC.Core.dll"])
        self.assertFalse(provision._assert_krpc_zip_layout(ctx, data))
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-3", ctx.abort_reason)

    def test_forbidden_testingtools_aborts(self):
        import provision
        ctx = self._ctx()
        data = self._zip_bytes(["GameData/kRPC/KRPC.Core.dll", "GameData/kRPC/KRPC.SpaceCenter.dll",
                                "GameData/kRPC/TestingTools.dll"])
        self.assertFalse(provision._assert_krpc_zip_layout(ctx, data))
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-3", ctx.abort_reason)


class ZipSlipGuardTests(unittest.TestCase):
    """SF5: gamedata_dest_escapes rejects any extraction dest that, once
    posixpath-normalized, escapes the instance GameData/ root."""

    def test_normal_dests_safe(self):
        self.assertFalse(provlib.gamedata_dest_escapes("GameData/kRPC/KRPC.dll"))
        self.assertFalse(provlib.gamedata_dest_escapes("GameData/MechJeb2/Plugins/MechJeb2.dll"))

    def test_krpc_traversal_escapes(self):
        self.assertTrue(provlib.gamedata_dest_escapes("GameData/kRPC/../../evil"))

    def test_mechjeb_traversal_escapes(self):
        self.assertTrue(provlib.gamedata_dest_escapes("GameData/../../evil"))

    def test_absolute_escapes(self):
        self.assertTrue(provlib.gamedata_dest_escapes("/etc/passwd"))

    def test_outside_gamedata_escapes(self):
        self.assertTrue(provlib.gamedata_dest_escapes("Plugins/x.dll"))


class ZipSlipExtractTests(unittest.TestCase):
    """SF5 orchestrator: _extract_zip_plan aborts (never writes) on a traversal
    entry that escapes the instance GameData."""

    def test_extract_aborts_on_traversal_entry(self):
        import zipfile
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as um:
            os.makedirs(os.path.join(um, "automation", "test"), exist_ok=True)
            zpath = os.path.join(um, "evil.zip")
            with zipfile.ZipFile(zpath, "w") as zf:
                zf.writestr("../../evil.dll", b"pwned")
            ctx = provision.ProvisionContext(
                profile_name="t", pins={}, profile={"instanceDir": "automation/test"},
                umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)
            provision._extract_zip_plan(ctx, "mechjeb2", zpath)
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-3", ctx.abort_reason)
            self.assertFalse(os.path.exists(os.path.join(um, "automation", "evil.dll")))
            self.assertFalse(os.path.exists(os.path.join(um, "evil.dll")))


def _make_junction_or_skip(test, link, target):
    import subprocess
    if os.name != "nt":
        test.skipTest("junctions are Windows-only")
    os.makedirs(target, exist_ok=True)
    res = subprocess.run(["cmd", "/c", "mklink", "/J", link, target],
                         capture_output=True, text=True)
    if res.returncode != 0:
        test.skipTest("could not create junction: %s" % (res.stderr or res.stdout).strip())


class ResolvedAliasRecheckTests(unittest.TestCase):
    """SF6(a): the EC-16 alias guard is re-run on REALPATH-resolved instance
    paths before the marker write, catching a junction that resolves a
    string-clean instanceDir/GameData into the read-only dev install."""

    def _ctx(self, um):
        import provision
        return provision.ProvisionContext(
            profile_name="t", pins={},
            profile={"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program"},
            umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)

    def test_junctioned_gamedata_into_dev_install_aborts(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            dev = os.path.join(um, "Kerbal Space Program")
            os.makedirs(os.path.join(dev, "GameData"), exist_ok=True)
            inst = os.path.join(um, "automation", "test")
            os.makedirs(inst, exist_ok=True)
            _make_junction_or_skip(self, os.path.join(inst, "GameData"), dev)
            ctx = self._ctx(um)
            ok = provision._recheck_alias_resolved(ctx)
            self.assertFalse(ok)
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-16", ctx.abort_reason)

    def test_clean_instance_passes(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            os.makedirs(os.path.join(um, "Kerbal Space Program"), exist_ok=True)
            os.makedirs(os.path.join(um, "automation", "test", "GameData"), exist_ok=True)
            ctx = self._ctx(um)
            self.assertTrue(provision._recheck_alias_resolved(ctx))
            self.assertFalse(ctx.aborted)


class ReparsePointSkipTests(unittest.TestCase):
    """SF6(b): copy / hash walks skip reparse-point (junction) subdirs so a
    junction inside a copied tree cannot pull a stock payload in or loop."""

    def test_content_hash_ignores_junctioned_subdir(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            mod = os.path.join(um, "Mod")
            os.makedirs(mod, exist_ok=True)
            with open(os.path.join(mod, "real.dll"), "wb") as fh:
                fh.write(b"real")
            ext = os.path.join(um, "external")
            os.makedirs(ext, exist_ok=True)
            with open(os.path.join(ext, "huge.bin"), "wb") as fh:
                fh.write(b"x" * 1000)
            _make_junction_or_skip(self, os.path.join(mod, "linked"), ext)
            h_with = provision._content_tree_hash(mod)
            os.rmdir(os.path.join(mod, "linked"))  # remove link, not target
            h_without = provision._content_tree_hash(mod)
            self.assertEqual(h_with, h_without,
                             "a junctioned subdir must not contribute to the content hash")

    def test_copy_dir_skips_junctioned_subdir(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            src = os.path.join(um, "src")
            os.makedirs(src, exist_ok=True)
            with open(os.path.join(src, "real.dll"), "wb") as fh:
                fh.write(b"real")
            ext = os.path.join(um, "external")
            os.makedirs(ext, exist_ok=True)
            with open(os.path.join(ext, "huge.bin"), "wb") as fh:
                fh.write(b"x" * 1000)
            _make_junction_or_skip(self, os.path.join(src, "linked"), ext)
            ctx = provision.ProvisionContext(
                profile_name="t", pins={}, profile={}, umbrella_root=um,
                dry_run=False, repair=False, parsek_dll_override=None)
            dst = os.path.join(um, "dst")
            files, _b = provision._copy_dir(ctx, src, dst)
            self.assertEqual(files, 1)
            self.assertTrue(os.path.isfile(os.path.join(dst, "real.dll")))
            self.assertFalse(os.path.exists(os.path.join(dst, "linked")),
                             "a junctioned subdir must not be copied")


class ResolvePinTests(unittest.TestCase):
    """Design: resolve_pin -- guards GT-1 retag/move. A moved tag (tag resolves
    to a commit != recorded) must be rejected."""

    RECORDED = "11f1f1366fa4301049f6eac6640604127a9d763b"

    def test_match(self):
        r = provlib.resolve_pin(self.RECORDED + "\n", self.RECORDED)
        self.assertTrue(r.ok)
        self.assertEqual(r.reason, "match")

    def test_match_case_insensitive_and_trimmed(self):
        r = provlib.resolve_pin("  " + self.RECORDED.upper() + "  ", self.RECORDED)
        self.assertTrue(r.ok)

    def test_mismatch_moved_tag(self):
        # The annotated-tag OBJECT sha (4e9dfbed) is NOT the commit; a caller
        # that forgot to peel with ^{commit} must be reported as a mismatch,
        # never silently accepted.
        r = provlib.resolve_pin("4e9dfbedd21409fb75c19f55f839d68520e752c1", self.RECORDED)
        self.assertFalse(r.ok)
        self.assertEqual(r.reason, "mismatch")

    def test_missing_tag(self):
        r = provlib.resolve_pin("", self.RECORDED)
        self.assertFalse(r.ok)
        self.assertEqual(r.reason, "missing")


class SettingsDeltaTests(unittest.TestCase):
    """Design: apply_settings -- guards EC-15 and non-determinism. Existing keys
    replaced, order/comments preserved, absent keys appended, unrelated keys
    untouched."""

    def test_replace_existing_preserves_order_and_unrelated(self):
        base = [
            "// header comment",
            "FULLSCREEN = True",
            "",
            "FRAMERATE_LIMIT = 120",
            "UI_SCALE = 1.2",
        ]
        out = provlib.apply_settings(base, {"FRAMERATE_LIMIT": "60", "FULLSCREEN": "False"})
        self.assertEqual(out[0], "// header comment")
        self.assertEqual(out[1], "FULLSCREEN = False")
        self.assertEqual(out[2], "")
        self.assertEqual(out[3], "FRAMERATE_LIMIT = 60")
        self.assertEqual(out[4], "UI_SCALE = 1.2")  # unrelated key untouched

    def test_append_missing_key(self):
        base = ["FULLSCREEN = True"]
        out = provlib.apply_settings(base, {"SCREEN_RESOLUTION_WIDTH": "1280"})
        self.assertIn("SCREEN_RESOLUTION_WIDTH = 1280", out)
        self.assertEqual(len(out), 2)

    def test_noop_when_equal_value_still_rewrites_same_text(self):
        base = ["QUALITY_PRESET = 0"]
        out = provlib.apply_settings(base, {"QUALITY_PRESET": "0"})
        self.assertEqual(out, ["QUALITY_PRESET = 0"])

    def test_braces_and_comments_are_not_keys(self):
        self.assertIsNone(provlib.settings_key_of("{"))
        self.assertIsNone(provlib.settings_key_of("}"))
        self.assertIsNone(provlib.settings_key_of("// FRAMERATE_LIMIT = 1"))
        self.assertIsNone(provlib.settings_key_of(""))
        self.assertEqual(provlib.settings_key_of("  FRAMERATE_LIMIT = 60"), "FRAMERATE_LIMIT")

    def test_keys_present_classifies_replaced_vs_appended(self):
        base = ["FULLSCREEN = True", "UI_SCALE = 1.2"]
        present = provlib.keys_present(base, ["FULLSCREEN", "NEW_KEY"])
        self.assertEqual(present, {"FULLSCREEN"})


class ManifestDiffTests(unittest.TestCase):
    """Design: compare_manifest -- guards EC-3/EC-5 silent admission of a
    drifted instance. A changed hash, changed tag/commit, missing component, or
    changed settings delta MUST be reported."""

    def _manifest(self):
        return {
            "profile": "stock-minimal",
            "kspVersion": "1.12.5",
            "components": {
                "krpc": {"tag": "v0.5.4", "commit": "11f1f13"},
                "parsek": {"dllSha256": "aaaa", "signatureStrings": {"ParsekFlight": 3}},
            },
            "settingsDeltasApplied": {"FRAMERATE_LIMIT": "60"},
            "devSourcedMods": {"000_Harmony": "treehash1"},
            # non-admission fields must be ignored:
            "generatedUtc": "2026-07-11T00:00:00Z",
        }

    def test_identical_is_empty(self):
        m = self._manifest()
        self.assertEqual(provlib.compare_manifest(m, dict(m)), [])

    def test_ignores_nonadmission_fields(self):
        a = self._manifest()
        b = self._manifest()
        b["generatedUtc"] = "different-timestamp"
        self.assertEqual(provlib.compare_manifest(a, b), [])

    def test_hash_drift_reported(self):
        a = self._manifest()
        b = self._manifest()
        b["components"]["parsek"]["dllSha256"] = "bbbb"
        diffs = provlib.compare_manifest(a, b)
        self.assertTrue(any(d.field == "components.parsek.dllSha256" and d.kind == "changed" for d in diffs))

    def test_pin_bump_reported(self):
        a = self._manifest()
        b = self._manifest()
        b["components"]["krpc"]["tag"] = "v0.5.5"
        diffs = provlib.compare_manifest(a, b)
        self.assertTrue(any(d.field == "components.krpc.tag" and d.kind == "changed" for d in diffs))

    def test_missing_component_reported(self):
        a = self._manifest()
        b = self._manifest()
        del b["components"]["krpc"]
        diffs = provlib.compare_manifest(a, b)
        self.assertTrue(any(d.field.startswith("components.krpc") and d.kind == "missing" for d in diffs))

    def test_added_component_reported(self):
        a = self._manifest()
        b = self._manifest()
        b["components"]["extra"] = {"tag": "x"}
        diffs = provlib.compare_manifest(a, b)
        self.assertTrue(any(d.field.startswith("components.extra") and d.kind == "added" for d in diffs))

    def test_settings_delta_change_reported(self):
        a = self._manifest()
        b = self._manifest()
        b["settingsDeltasApplied"]["FRAMERATE_LIMIT"] = "120"
        diffs = provlib.compare_manifest(a, b)
        self.assertTrue(any(d.field == "settingsDeltasApplied.FRAMERATE_LIMIT" and d.kind == "changed" for d in diffs))

    def test_signature_count_change_reported(self):
        a = self._manifest()
        b = self._manifest()
        b["components"]["parsek"]["signatureStrings"]["ParsekFlight"] = 1
        diffs = provlib.compare_manifest(a, b)
        self.assertTrue(any("signatureStrings.ParsekFlight" in d.field for d in diffs))


class TestingToolsSourceTests(unittest.TestCase):
    """Design: BUILD-TT source set EXCLUDES AutoLoadGame (S-4). The 2-file shim
    keeps only OrbitTools.cs + TestingTools.cs and asserts the auto-loader is
    dropped so it cannot race the seam's LoadGame boot."""

    ALL_FIVE = ["AutoLoadGame.cs", "AutoSwitchVessel.cs", "OrbitTools.cs",
                "TestingTools.cs", "TestingTools.csproj"]

    def test_selects_only_two_and_excludes_autoloader(self):
        sel = provlib.select_testingtools_sources(self.ALL_FIVE)
        self.assertTrue(sel.ok)
        self.assertEqual(sorted(sel.included), ["OrbitTools.cs", "TestingTools.cs"])
        self.assertNotIn("AutoLoadGame.cs", sel.included)
        self.assertNotIn("AutoSwitchVessel.cs", sel.included)
        self.assertTrue(sel.autoloader_excluded)

    def test_drops_the_two_racing_sources(self):
        sel = provlib.select_testingtools_sources(self.ALL_FIVE)
        self.assertEqual(sorted(sel.dropped), ["AutoLoadGame.cs", "AutoSwitchVessel.cs"])

    def test_missing_shim_source_fails(self):
        sel = provlib.select_testingtools_sources(["OrbitTools.cs"])  # no TestingTools.cs
        self.assertFalse(sel.ok)
        self.assertIn("missing-shim-source", sel.reason)

    def test_capability_table_matches_design(self):
        self.assertEqual(
            provlib.TESTINGTOOLS_CAPABILITIES,
            ("LoadSave", "RemoveOtherVessels", "SetCircularOrbit", "SetOrbit",
             "ClearRotation", "ApplyRotation"),
        )
        self.assertEqual(provlib.MISSING_VS_MASTER, ("autoLoadFlags", "Quit", "SetLanded"))


class JunctionTests(unittest.TestCase):
    """Design: verify_junctions -- guards EC-8. A dangling junction target must
    not be reported OK. Plus junction-vs-copy classification (design CLONE)."""

    def test_dangling_junction_reported(self):
        # The SquadExpansion LINK does not resolve (realpath returns None).
        manifest = {"junctionTargets": {
            "GameData/Squad": "/dev/Squad",
            "GameData/SquadExpansion": "/dev/SquadExpansion",
        }}
        resolved = {"GameData/Squad": "/dev/Squad"}
        dangling = provlib.verify_junctions(manifest, lambda link: resolved.get(link))
        self.assertEqual(dangling, ["GameData/SquadExpansion"])

    def test_all_resolve_is_empty(self):
        manifest = {"junctionTargets": {"GameData/Squad": "/dev/Squad"}}
        self.assertEqual(provlib.verify_junctions(manifest, lambda link: "/dev/Squad"), [])

    def test_repointed_junction_reported(self):
        # The LINK exists but its realpath points somewhere other than the
        # recorded target: must fail even though a target may still exist.
        manifest = {"junctionTargets": {"GameData/Squad": "/dev/Squad"}}
        dangling = provlib.verify_junctions(manifest, lambda link: "/other/Squad")
        self.assertEqual(dangling, ["GameData/Squad"])

    def test_classify_stock_vs_devsourced_vs_stack(self):
        dev = ["000_Harmony", "CommunityTechTree"]
        self.assertEqual(provlib.classify_gamedata_entry("Squad", dev), "junction-stock")
        self.assertEqual(provlib.classify_gamedata_entry("SquadExpansion", dev), "junction-stock")
        self.assertEqual(provlib.classify_gamedata_entry("000_Harmony", dev), "copy-devsourced")
        self.assertEqual(provlib.classify_gamedata_entry("kRPC", dev), "stack-install")
        self.assertEqual(provlib.classify_gamedata_entry("Parsek", dev), "stack-install")
        self.assertEqual(provlib.classify_gamedata_entry("MysteryMod", dev), "unknown")


class Utf16Tests(unittest.TestCase):
    """Design: count_utf16 -- guards the DLL-identity check and EC-9/EC-11. A
    present signature must count > 0; an absent one must count 0."""

    def test_present_signature_counts(self):
        buf = "xx".encode("utf-16-le") + "ParsekFlight".encode("utf-16-le") \
            + "yy".encode("utf-16-le") + "ParsekFlight".encode("utf-16-le")
        self.assertEqual(provlib.count_utf16(buf, "ParsekFlight"), 2)

    def test_absent_signature_zero(self):
        buf = "nothing here".encode("utf-16-le")
        self.assertEqual(provlib.count_utf16(buf, "MissingLabel"), 0)

    def test_ascii_bytes_do_not_match_utf16(self):
        # An ASCII-encoded copy must NOT satisfy the UTF-16-LE grep.
        buf = "ParsekFlight".encode("ascii")
        self.assertEqual(provlib.count_utf16(buf, "ParsekFlight"), 0)

    def test_check_signatures_flags_wrong_count(self):
        buf = "ParsekFlight".encode("utf-16-le")
        res = provlib.check_signatures(buf, {"ParsekFlight": 1, "Absent": 0, "Wrong": 2})
        self.assertTrue(res["ParsekFlight"][2])
        self.assertTrue(res["Absent"][2])
        self.assertFalse(res["Wrong"][2])


class DiskAndPathGuardTests(unittest.TestCase):
    """Design: estimate_instance_bytes / is_path_too_long -- guards EC-6/EC-7."""

    def test_estimate_sums_copied_surface_only(self):
        profile = {"devSourcedMods": ["A", "B"], "stackComponents": ["krpc", "parsek"]}
        sizes = {"A": 100, "B": 200, "krpc": 50, "parsek": 10}
        est = provlib.estimate_instance_bytes(profile, sizes, base_copy_bytes=1000)
        self.assertEqual(est, 1000 + 100 + 200 + 50 + 10)

    def test_over_budget_flagged(self):
        self.assertTrue(provlib.is_over_budget(900, 1000, safety_margin_bytes=200))
        self.assertFalse(provlib.is_over_budget(700, 1000, safety_margin_bytes=200))

    def test_path_too_long_flagged(self):
        self.assertTrue(provlib.is_path_too_long("C:/" + "a" * 300))
        self.assertFalse(provlib.is_path_too_long("C:/short/path"))

    def test_extended_length_prefix_exempt(self):
        self.assertFalse(provlib.is_path_too_long("\\\\?\\C:/" + "a" * 300))


class InstanceDirAliasTests(unittest.TestCase):
    """Design EC-16 / reviewer 4: an instance dir that equals, nests inside, or
    contains the read-only dev install (or is not under automation/) must be
    rejected before any destructive live primitive runs."""

    DEV = "C:/Code/Parsek/Kerbal Space Program"
    GOOD = "C:/Code/Parsek/automation/stock-minimal"

    def test_good_instance_dir(self):
        d = provlib.check_instance_dir_alias(self.GOOD, self.DEV, "automation/stock-minimal")
        self.assertTrue(d.ok)
        self.assertEqual(d.reason, "ok")

    def test_equal_rejected(self):
        d = provlib.check_instance_dir_alias(self.DEV, self.DEV, "automation/x")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "equals-dev-install")

    def test_equal_rejected_case_and_sep_insensitive(self):
        d = provlib.check_instance_dir_alias(
            "c:\\code\\parsek\\kerbal space program", self.DEV, "automation/x")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "equals-dev-install")

    def test_nested_in_dev_install_rejected(self):
        d = provlib.check_instance_dir_alias(
            self.DEV + "/GameData/instance", self.DEV, "automation/x")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "nested-in-dev-install")

    def test_dev_install_nested_in_instance_rejected(self):
        parent = "C:/Code/Parsek"
        d = provlib.check_instance_dir_alias(parent, self.DEV, "automation/x")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "dev-install-nested-in-instance")

    def test_not_under_automation_rejected(self):
        d = provlib.check_instance_dir_alias(
            "C:/Code/Parsek/instances/x", self.DEV, "instances/x")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "not-under-automation")

    def test_prefix_sibling_is_not_nested(self):
        # "automation" must not be treated as nested under "auto".
        self.assertFalse(provlib.is_path_within("C:/x/automation", "C:/x/auto"))
        self.assertTrue(provlib.is_path_within("C:/x/auto/child", "C:/x/auto"))


class SettingsHashRoundTripTests(unittest.TestCase):
    """Guards BLOCKER 2: settingsFinalSha256 must equal the hash of the bytes
    actually written. Writing with newline='\\n' and recording the re-read hash
    means a live VERIFY re-hash can never spuriously exit 3 DRIFT (the old code
    hashed '\\n' text but wrote CRLF on Windows)."""

    def test_written_settings_hash_matches_recorded_and_uses_lf(self):
        import hashlib
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as umbrella:
            dev = os.path.join(umbrella, "Kerbal Space Program")
            os.makedirs(dev)
            # Dev file deliberately CRLF: proves the platform-translation trap.
            with open(os.path.join(dev, "settings.cfg"), "wb") as fh:
                fh.write(b"FRAMERATE_LIMIT = 120\r\nUI_SCALE = 1.2\r\n")
            profile = {"baseInstall": "Kerbal Space Program",
                       "instanceDir": "automation/x",
                       "settings": {"FRAMERATE_LIMIT": "60", "NEW_KEY": "1"}}
            ctx = provision.ProvisionContext(
                profile_name="x", pins={}, profile=profile,
                umbrella_root=umbrella, dry_run=False, repair=False,
                parsek_dll_override=None)
            provision.phase_settings(ctx)
            written = os.path.join(ctx.instance_dir, "settings.cfg")
            with open(written, "rb") as fh:
                raw = fh.read()
            self.assertNotIn(b"\r\n", raw, "instance settings.cfg must be LF-only")
            on_disk = hashlib.sha256(raw).hexdigest()
            self.assertEqual(on_disk, ctx.settings_final_sha,
                             "recorded settingsFinalSha256 must equal the on-disk byte hash")


class LockTests(unittest.TestCase):
    """Design: acquire_lock -- guards EC-10. A live lock must not be stolen; a
    stale (dead-pid) lock must be reclaimed."""

    def test_no_lock_acquires(self):
        d = provlib.acquire_lock(None, pid=42, now=1.0, is_alive_fn=lambda p: True)
        self.assertTrue(d.acquired)
        self.assertEqual(d.reason, "acquired-free")

    def test_live_other_pid_refused(self):
        lock = {"pid": 99, "timestamp": 1.0}
        d = provlib.acquire_lock(lock, pid=42, now=2.0, is_alive_fn=lambda p: True)
        self.assertFalse(d.acquired)
        self.assertEqual(d.reason, "refused-live")
        self.assertEqual(d.holder_pid, 99)

    def test_dead_pid_reclaimed(self):
        lock = {"pid": 99, "timestamp": 1.0}
        d = provlib.acquire_lock(lock, pid=42, now=2.0, is_alive_fn=lambda p: False)
        self.assertTrue(d.acquired)
        self.assertEqual(d.reason, "reclaimed-stale")

    def test_same_pid_reentrant(self):
        lock = {"pid": 42, "timestamp": 1.0}
        d = provlib.acquire_lock(lock, pid=42, now=2.0, is_alive_fn=lambda p: True)
        self.assertTrue(d.acquired)

    def test_malformed_holder_reclaimed_not_crash(self):
        # A lockfile carrying an unparseable pid (None / non-integer) is stale,
        # reclaimed, never a crash. (An entirely empty {} is a no-lock, handled
        # by test_no_lock_acquires.)
        for bad in ({"pid": "notapid"}, {"pid": None}):
            d = provlib.acquire_lock(bad, pid=42, now=1.0, is_alive_fn=lambda p: True)
            self.assertTrue(d.acquired, bad)
            self.assertEqual(d.reason, "reclaimed-stale")


class PairDecisionTests(unittest.TestCase):
    """Reviewer 7: extracted PAIR assertion (GT-6 / EC-14)."""

    def test_v054_genhis_071_matches(self):
        d = provlib.evaluate_krpc_mechjeb_pair("v0.5.4", "genhis", "v0.7.1", "v0.5.4")
        self.assertTrue(d.ok)
        self.assertEqual(d.reason, "match")
        self.assertFalse(d.requires_web_verify)

    def test_v054_wrong_fork_mismatch(self):
        d = provlib.evaluate_krpc_mechjeb_pair("v0.5.4", "darchambault", "v0.7.1", "v0.5.4")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "mismatch")

    def test_v054_wrong_paired_tag_mismatch(self):
        d = provlib.evaluate_krpc_mechjeb_pair("v0.5.4", "genhis", "v0.7.1", "v0.5.3")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "mismatch")

    def test_nonv054_requires_web_verify(self):
        d = provlib.evaluate_krpc_mechjeb_pair("master-abc123", "genhis", "v0.7.1", "v0.5.4")
        self.assertFalse(d.ok)
        self.assertTrue(d.requires_web_verify)
        self.assertEqual(d.reason, "verify-required-nonv054")


class DeploySourceTests(unittest.TestCase):
    """Reviewer 7: extracted DEPLOY source selection. SF8: no hardcoded
    sibling-worktree default -- absent an override AND a worktree build, the
    selection is not-ok so DEPLOY aborts demanding --parsek-dll."""

    def test_override_always_wins(self):
        d = provlib.select_parsek_dll_source("/x/over.dll", "/wt/Parsek.dll", True)
        self.assertEqual(d.source, "/x/over.dll")
        self.assertEqual(d.reason, "override")
        self.assertTrue(d.ok)

    def test_worktree_build_when_present(self):
        d = provlib.select_parsek_dll_source(None, "/wt/Parsek.dll", True)
        self.assertEqual(d.source, "/wt/Parsek.dll")
        self.assertEqual(d.reason, "worktree-build")
        self.assertTrue(d.ok)

    def test_no_source_when_worktree_absent_and_no_override(self):
        d = provlib.select_parsek_dll_source(None, "/wt/Parsek.dll", False)
        self.assertIsNone(d.source)
        self.assertEqual(d.reason, "missing-no-source")
        self.assertFalse(d.ok)

    def test_override_wins_even_without_worktree_build(self):
        d = provlib.select_parsek_dll_source("/x/over.dll", "/wt/Parsek.dll", False)
        self.assertEqual(d.source, "/x/over.dll")
        self.assertTrue(d.ok)


class LockfileReleaseTests(unittest.TestCase):
    """Reviewer 10: a run removes ONLY a lockfile it created, on completion."""

    def _ctx(self, umbrella):
        import provision
        return provision.ProvisionContext(
            profile_name="x", pins={}, profile={}, umbrella_root=umbrella,
            dry_run=False, repair=False, parsek_dll_override=None)

    def test_owned_lock_removed_on_finish(self):
        import json
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as umbrella:
            lock = os.path.join(umbrella, ".provision.lock")
            with open(lock, "w") as fh:
                json.dump({"pid": 1}, fh)
            ctx = self._ctx(umbrella)
            ctx.lock_path = lock
            ctx.lock_acquired = True
            provision._finish(ctx, 0)
            self.assertFalse(os.path.exists(lock))

    def test_unowned_lock_not_removed(self):
        import json
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as umbrella:
            lock = os.path.join(umbrella, ".provision.lock")
            with open(lock, "w") as fh:
                json.dump({"pid": 999}, fh)
            ctx = self._ctx(umbrella)  # lock_acquired never set: we do not own it
            provision._finish(ctx, 0)
            self.assertTrue(os.path.exists(lock))


class OpenPinTests(unittest.TestCase):
    """Design: is_open_pin -- guards EC-13. An OPEN sha256 placeholder must be
    detected so DOWNLOAD aborts instead of installing unverified bytes."""

    def test_open_variants_detected(self):
        for v in ["OPEN", "OPEN-fill-at-first-download", "OPEN-something", "", None, "  "]:
            self.assertTrue(provlib.is_open_pin(v), v)

    def test_real_hash_not_open(self):
        self.assertFalse(provlib.is_open_pin("a" * 64))


class LogFormatTests(unittest.TestCase):
    """Design: Diagnostic Logging -- the provision-log line shape."""

    def test_format(self):
        line = provlib.format_log_line("Info", "PIN", "krpc match")
        self.assertEqual(line, "[Provision][Info][PIN] krpc match")


class ActionPlanTests(unittest.TestCase):
    """Design: --dry-run action plan coherence."""

    PINS = {
        "krpc": {"tag": "v0.5.4", "commit": "11f1f13", "releaseZipUrl": "u", "releaseZipSha256": "OPEN"},
        "mechjeb2": {"buildNumber": "2.15.x.x", "downloadUrl": "OPEN", "sha256": "OPEN"},
        "krpc_mechjeb": {"fork": "genhis", "tag": "v0.7.1", "commit": "398bc33", "pairedKrpcTag": "v0.5.4"},
    }
    PROFILE = {
        "instanceDir": "automation/stock-minimal",
        "devSourcedMods": ["000_Harmony", "CommunityTechTree"],
        "stackComponents": ["krpc", "testingtools", "mechjeb2", "krpc_mechjeb", "parsek"],
        "settings": {"FRAMERATE_LIMIT": "60", "QUALITY_PRESET": "0"},
    }

    def test_plan_covers_all_phases(self):
        plan = provlib.build_action_plan(self.PINS, self.PROFILE)
        steps = {a.step for a in plan}
        for expected in ("PIN", "FETCH", "BUILD-TT", "PAIR", "CLONE", "SETTINGS",
                         "DEPLOY", "INSTALL", "MM-CACHE", "MANIFEST", "VERIFY"):
            self.assertIn(expected, steps)

    def test_plan_junctions_stock_and_copies_devsourced(self):
        plan = provlib.build_action_plan(self.PINS, self.PROFILE)
        junctions = [a.detail for a in plan if a.verb == "JUNCTION"]
        self.assertTrue(any("Squad" in d for d in junctions))
        copies = [a.detail for a in plan if a.verb == "COPY"]
        self.assertTrue(any("000_Harmony" in d for d in copies))

    def test_plan_deletes_mm_cache(self):
        plan = provlib.build_action_plan(self.PINS, self.PROFILE)
        deletes = [a.detail for a in plan if a.verb == "DELETE"]
        self.assertTrue(any("ConfigCache" in d for d in deletes))

    def test_parsek_not_double_installed(self):
        plan = provlib.build_action_plan(self.PINS, self.PROFILE)
        installs = [a.detail for a in plan if a.step == "INSTALL"]
        self.assertFalse(any("GameData/parsek" in d for d in installs))

    def test_install_labels_use_real_gamedata_folders(self):
        # N11: INSTALL plan labels name the real on-disk folder, not the pin id.
        plan = provlib.build_action_plan({}, {"stackComponents": ["krpc", "mechjeb2", "parsek"]})
        joined = " ".join(a.detail for a in plan if a.step == "INSTALL")
        self.assertIn("GameData/kRPC", joined)
        self.assertIn("GameData/MechJeb2", joined)
        self.assertEqual(provlib.stack_component_install_folder("testingtools"), "GameData/kRPC")
        self.assertEqual(provlib.stack_component_install_folder("krpc_mechjeb"), "GameData/kRPC")


class CloneToplevelTests(unittest.TestCase):
    """Design CLONE: the mutable surface is copied, GameData is built
    selectively, the KSP data dir is copied-except-junction, and mutable /
    harness-owned trees (saves/Logs/settings.cfg) are skipped so no dev state
    leaks into an automation instance."""

    def test_gamedata_is_built(self):
        self.assertEqual(provlib.clone_toplevel_disposition("GameData"), "build-gamedata")

    def test_ksp_data_dir_copy_except_junction(self):
        self.assertEqual(provlib.clone_toplevel_disposition("KSP_x64_Data"),
                         "copy-tree-except-junction")

    def test_settings_and_saves_skipped(self):
        self.assertEqual(provlib.clone_toplevel_disposition("settings.cfg"), "skip")
        self.assertEqual(provlib.clone_toplevel_disposition("saves"), "skip")
        self.assertEqual(provlib.clone_toplevel_disposition("Logs"), "skip")

    def test_exe_and_files_copied(self):
        self.assertEqual(provlib.clone_toplevel_disposition("KSP_x64.exe"), "copy")
        self.assertEqual(provlib.clone_toplevel_disposition("buildID64.txt"), "copy")
        self.assertEqual(provlib.clone_toplevel_disposition("Internals"), "copy")

    def test_ksp_log_and_crash_dumps_skipped(self):
        # N20: dev-run logs + timestamped crash dumps never leak into an instance.
        self.assertEqual(provlib.clone_toplevel_disposition("KSP.log"), "skip")
        self.assertEqual(provlib.clone_toplevel_disposition("Player.log"), "skip")
        self.assertEqual(provlib.clone_toplevel_disposition("crash_2024-01-01_120000"), "skip")
        self.assertEqual(provlib.clone_toplevel_disposition("Crash-report"), "skip")
        # A real payload with a "crash"-ish infix is still copied (prefix-only).
        self.assertEqual(provlib.clone_toplevel_disposition("KSP_x64.exe"), "copy")

    def test_streamingassets_is_the_only_junctioned_ksp_data_entry(self):
        self.assertTrue(provlib.ksp_data_entry_is_junction("StreamingAssets"))
        self.assertFalse(provlib.ksp_data_entry_is_junction("Managed"))
        self.assertFalse(provlib.ksp_data_entry_is_junction("Resources"))


class ExtendedLengthPathTests(unittest.TestCase):
    """Design EC-7 / R13: the live CLONE copy/junction must use the \\\\?\\
    extended-length prefix on Windows so deep KSP asset trees under a long
    umbrella root do not overflow MAX_PATH."""

    def test_local_path_prefixed(self):
        self.assertEqual(provlib.to_extended_length_path("C:/a/b"), "\\\\?\\C:\\a\\b")

    def test_backslash_input_prefixed(self):
        self.assertEqual(provlib.to_extended_length_path("C:\\a\\b"), "\\\\?\\C:\\a\\b")

    def test_unc_path_prefixed(self):
        self.assertEqual(provlib.to_extended_length_path("\\\\srv\\share\\x"),
                         "\\\\?\\UNC\\srv\\share\\x")

    def test_already_prefixed_unchanged(self):
        p = "\\\\?\\C:\\a"
        self.assertEqual(provlib.to_extended_length_path(p), p)


class TreeDigestTests(unittest.TestCase):
    """Design EC-3: a dev-sourced mod's content tree-hash must be deterministic
    regardless of walk order or OS separator so a re-run does not false-drift."""

    def test_order_and_separator_independent(self):
        a = provlib.canonical_tree_digest_input([("b/c.dll", "h2"), ("a.txt", "h1")])
        b = provlib.canonical_tree_digest_input([("a.txt", "h1"), ("b\\c.dll", "h2")])
        self.assertEqual(a, b)

    def test_content_change_changes_digest(self):
        a = provlib.canonical_tree_digest_input([("a.txt", "h1")])
        b = provlib.canonical_tree_digest_input([("a.txt", "h2")])
        self.assertNotEqual(a, b)

    def test_added_file_changes_digest(self):
        a = provlib.canonical_tree_digest_input([("a.txt", "h1")])
        b = provlib.canonical_tree_digest_input([("a.txt", "h1"), ("b.txt", "h1")])
        self.assertNotEqual(a, b)


class ShimCsprojTests(unittest.TestCase):
    """Design BUILD-TT / GT-4 / GT-9 / S-4: the shim csproj compiles ONLY the two
    shim sources (default globbing off so a stray AutoLoadGame.cs cannot slip in),
    targets net472, and HintPaths the KSP + kRPC references."""

    def _render(self):
        return provlib.render_testingtools_shim_csproj(
            "C:/dev/KSP_x64_Data/Managed", "C:/cache/GameData/kRPC",
            ["OrbitTools.cs", "TestingTools.cs"])

    def test_net472_and_defaultcompile_off(self):
        xml = self._render()
        self.assertIn("<TargetFramework>net472</TargetFramework>", xml)
        self.assertIn("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", xml)

    def test_only_two_sources_plus_assemblyinfo(self):
        xml = self._render()
        self.assertIn('<Compile Include="OrbitTools.cs" />', xml)
        self.assertIn('<Compile Include="TestingTools.cs" />', xml)
        self.assertIn('<Compile Include="AssemblyInfo.cs" />', xml)
        self.assertNotIn("AutoLoadGame.cs", xml)
        self.assertNotIn("AutoSwitchVessel.cs", xml)

    def test_references_ksp_and_krpc(self):
        xml = self._render()
        self.assertIn("Assembly-CSharp.dll", xml)
        self.assertIn("UnityEngine.CoreModule.dll", xml)
        self.assertIn("KRPC.Core.dll", xml)
        self.assertIn("KRPC.SpaceCenter.dll", xml)
        self.assertIn("Google.Protobuf.dll", xml)

    def test_assemblyinfo_names_the_assembly(self):
        info = provlib.render_testingtools_assemblyinfo()
        self.assertIn("TestingTools", info)


class BuildTtAssemblyTests(unittest.TestCase):
    """Design S-4: the AutoLoadGame type must be ABSENT from the built shim (it
    would race the seam's LoadGame boot) and a TestingTools type must be present
    (a real build). Proxy is a UTF-8 metadata grep over the assembly bytes."""

    def test_shim_assembly_passes(self):
        # Simulate #Strings heap bytes: TestingTools present, AutoLoadGame absent.
        buf = b"...OrbitTools.TestingTools.SetOrbit..."
        r = provlib.evaluate_build_tt_assembly(buf)
        self.assertTrue(r.ok)
        self.assertEqual(r.autoloadgame_count, 0)
        self.assertTrue(r.has_testingtools_type)

    def test_autoloadgame_present_fails(self):
        buf = b"...TestingTools.AutoLoadGame..."
        r = provlib.evaluate_build_tt_assembly(buf)
        self.assertFalse(r.ok)
        self.assertEqual(r.reason, "autoloadgame-present")

    def test_empty_build_fails(self):
        r = provlib.evaluate_build_tt_assembly(b"nothing relevant here")
        self.assertFalse(r.ok)
        self.assertEqual(r.reason, "testingtools-type-absent")

    def test_count_utf8(self):
        self.assertEqual(provlib.count_utf8(b"abcabc", "abc"), 2)
        self.assertEqual(provlib.count_utf8(b"abc", ""), 0)


class ZipInstallPlanTests(unittest.TestCase):
    """Design INSTALL / GT-5: the kRPC GameData/kRPC subtree lands as-is; only
    the prebuilt KRPC.MechJeb.dll (+ .json) is taken from the KRPC.MechJeb release
    root; directory entries and out-of-footprint files are skipped."""

    def test_krpc_subtree_as_is(self):
        names = ["GameData/kRPC/", "GameData/kRPC/KRPC.dll",
                 "GameData/kRPC/KRPC.Core.dll", "other/thing.txt"]
        plan = provlib.plan_zip_install("krpc", names)
        dests = dict(plan)
        self.assertEqual(dests["GameData/kRPC/KRPC.dll"], "GameData/kRPC/KRPC.dll")
        self.assertNotIn("other/thing.txt", dests)
        self.assertNotIn("GameData/kRPC/", dests)  # directory skipped

    def test_krpc_mechjeb_only_dll_and_json(self):
        names = ["KRPC.MechJeb.dll", "KRPC.MechJeb.json", "README.md",
                 "C#/MechJeb.cs", "LICENSE"]
        plan = dict(provlib.plan_zip_install("krpc_mechjeb", names))
        self.assertEqual(plan["KRPC.MechJeb.dll"], "GameData/kRPC/KRPC.MechJeb.dll")
        self.assertEqual(plan["KRPC.MechJeb.json"], "GameData/kRPC/KRPC.MechJeb.json")
        self.assertNotIn("README.md", plan)
        self.assertNotIn("C#/MechJeb.cs", plan)

    def test_mechjeb2_gamedata_as_is_else_wrapped(self):
        plan = dict(provlib.plan_zip_install(
            "mechjeb2", ["GameData/MechJeb2/MechJeb2.dll", "bareroot.dll"]))
        self.assertEqual(plan["GameData/MechJeb2/MechJeb2.dll"],
                         "GameData/MechJeb2/MechJeb2.dll")
        self.assertEqual(plan["bareroot.dll"], "GameData/bareroot.dll")

    def test_krpc_installed_dll_names(self):
        pin = {"releaseRuntimeDlls": ["KRPC.dll", "KRPC.Core.dll"]}
        self.assertEqual(provlib.krpc_installed_dll_names(pin), ["KRPC.dll", "KRPC.Core.dll"])
        self.assertEqual(provlib.krpc_installed_dll_names(
            {"releaseCompileDlls": ["KRPC.Core.dll"]}), ["KRPC.Core.dll"])


class RepairPlanTests(unittest.TestCase):
    """Design --repair / EC-3: a VERIFY drift diff converges to the MINIMAL
    targeted work set -- only the drifted component / dev mod / settings, and an
    unmappable field is surfaced (never silently converged to nothing)."""

    def _diff(self, field, kind="changed"):
        return provlib.ManifestDiff(field, "e", "a", kind)

    def test_component_of_field(self):
        self.assertEqual(provlib.component_of_diff_field(
            "components.krpc.installedDlls.KRPC.dll"), "krpc")
        self.assertEqual(provlib.component_of_diff_field(
            "components.parsek.dllSha256"), "parsek")
        self.assertEqual(provlib.component_of_diff_field(
            "settingsDeltasApplied.FRAMERATE_LIMIT"), provlib.SETTINGS_REPAIR_TOKEN)
        self.assertEqual(provlib.component_of_diff_field(
            "devSourcedMods.000_Harmony"), "devmod:000_Harmony")
        self.assertIsNone(provlib.component_of_diff_field("kspVersion"))

    def test_plan_targets_only_drifted(self):
        plan = provlib.plan_repair([
            self._diff("components.krpc.installedDlls.KRPC.dll"),
            self._diff("components.parsek.dllSha256"),
            self._diff("devSourcedMods.000_Harmony"),
            self._diff("settingsDeltasApplied.UI_SCALE"),
        ])
        self.assertEqual(plan.components, ("krpc", "parsek"))
        self.assertEqual(plan.dev_mods, ("000_Harmony",))
        self.assertTrue(plan.settings)
        self.assertEqual(plan.unrepairable, ())

    def test_unrepairable_field_surfaced(self):
        plan = provlib.plan_repair([self._diff("kspVersion")])
        self.assertEqual(plan.unrepairable, ("kspVersion",))
        self.assertEqual(plan.components, ())


if __name__ == "__main__":
    unittest.main()
