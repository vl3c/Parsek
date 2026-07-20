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
import tempfile


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


class KrpcSettingsStampTests(unittest.TestCase):
    """F3 (golden-template semantics, post first-live-B1-run rewrite): the stamp
    returns the COMPLETE golden settings.cfg regardless of input. Partial
    edit-in-place was retired because kRPC's ConfigurationFile fields are
    uninitialized (any omitted key loads as zero/false; maxTimePerUpdate=0
    starves RPC execution while handshakes still succeed) and because kRPC
    rewrites the file at exit anyway. These cells pin the golden shape."""

    def _keys(self, text):
        out = {}
        for line in text.splitlines():
            k = provlib.settings_key_of(line)
            if k is not None:
                out[k] = line.split("=", 1)[1].strip()
        return out

    def test_input_ignored_full_replace(self):
        golden = provlib.stamp_krpc_settings(None)
        partial = "KRPCConfiguration\n{\n\tautoStartServers = False\n}\n"
        for shipped in (None, "", partial, "totally mangled { not a config"):
            self.assertEqual(golden, provlib.stamp_krpc_settings(shipped))

    def test_idempotent(self):
        once = provlib.stamp_krpc_settings(None)
        self.assertEqual(once, provlib.stamp_krpc_settings(once))

    def test_hands_free_keys(self):
        keys = self._keys(provlib.stamp_krpc_settings(None))
        self.assertEqual(keys["autoStartServers"], "True")
        self.assertEqual(keys["autoAcceptConnections"], "True")
        self.assertEqual(keys["confirmRemoveClient"], "False")

    def test_every_executor_key_at_healthy_default(self):
        # The load-bearing cells: these four zeroed out in the pre-golden partial
        # file and killed ALL RPC execution (maxTimePerUpdate=0 budget).
        keys = self._keys(provlib.stamp_krpc_settings(None))
        self.assertEqual(keys["maxTimePerUpdate"], "5000")
        self.assertEqual(keys["adaptiveRateControl"], "True")
        self.assertEqual(keys["blockingRecv"], "True")
        self.assertEqual(keys["recvTimeout"], "1000")
        self.assertEqual(keys["pauseServerWithGame"], "False")
        self.assertEqual(keys["logLevel"], "Info")

    def test_server_node_item_wrapped_fixed_id(self):
        out = provlib.stamp_krpc_settings(None)
        lines = [l.strip() for l in out.splitlines()]
        # ConfigurationStorage convention: servers { Item { ... settings { Item {...} } } }
        si = lines.index("servers")
        self.assertEqual(lines[si + 1], "{")
        self.assertEqual(lines[si + 2], "Item")
        self.assertIn("id = %s" % provlib.KRPC_DEFAULT_SERVER_ID, lines)
        self.assertIn("protocol = ProtocolBuffersOverTCP", lines)
        for key, value in (("address", "127.0.0.1"),
                           ("rpc_port", "50000"),
                           ("stream_port", "50001")):
            ki = lines.index("key = %s" % key)
            self.assertEqual(lines[ki + 1], "value = %s" % value)
        self.assertEqual(out.count("name = Default Server"), 1)

    def test_lf_terminated_single_node(self):
        out = provlib.stamp_krpc_settings(None)
        self.assertTrue(out.startswith("KRPCConfiguration\n{"))
        self.assertTrue(out.endswith("}\n"))
        self.assertNotIn("\r", out)
        self.assertEqual(out.count("KRPCConfiguration"), 1)


class KrpcSettingsStampShellTests(unittest.TestCase):
    """F3 shell wiring: _stamp_krpc_settings edits the on-disk kRPC settings.cfg,
    records krpcSettingsSha256 over the LF-written bytes, and VERIFY re-hashes it
    (drift on a later manual edit), mirroring settingsFinalSha256."""

    def _ctx(self, um):
        import provision
        return provision.ProvisionContext(
            profile_name="t", pins={}, profile={"instanceDir": "automation/test"},
            umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)

    def test_stamp_writes_lf_and_records_matching_sha(self):
        import hashlib
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            path = provision._krpc_settings_path(ctx)
            os.makedirs(os.path.dirname(path))
            # Shipped default (CRLF) with the three hands-blocking values.
            with open(path, "wb") as fh:
                fh.write(b"KRPCConfiguration\r\n{\r\n\tautoStartServers = False\r\n"
                         b"\tautoAcceptConnections = False\r\n\tconfirmRemoveClient = True\r\n}\r\n")
            provision._stamp_krpc_settings(ctx)
            with open(path, "rb") as fh:
                raw = fh.read()
            self.assertNotIn(b"\r\n", raw, "stamped kRPC settings.cfg must be LF-only")
            text = raw.decode("utf-8")
            self.assertIn("autoStartServers = True", text)
            self.assertIn("autoAcceptConnections = True", text)
            self.assertIn("confirmRemoveClient = False", text)
            self.assertEqual(hashlib.sha256(raw).hexdigest(), ctx.krpc_settings_sha)

    def test_stamp_synths_when_zip_shipped_no_settings(self):
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            provision._stamp_krpc_settings(ctx)  # no file present -> synth
            path = provision._krpc_settings_path(ctx)
            self.assertTrue(os.path.isfile(path))
            with open(path, "r", encoding="utf-8") as fh:
                text = fh.read()
            self.assertIn("autoStartServers = True", text)
            self.assertTrue(ctx.krpc_settings_sha)

    def test_verify_drifts_on_later_manual_krpc_settings_edit(self):
        import tempfile
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            provision._stamp_krpc_settings(ctx)
            manifest = {"components": {}, "junctionTargets": {}, "devSourcedMods": {},
                        "krpcSettingsSha256": ctx.krpc_settings_sha}
            self.assertTrue(provision.phase_verify(ctx, manifest))
            self.assertEqual(getattr(ctx, "verify_drift", []), [])
            # A manual kRPC settings change AFTER provisioning must drift.
            with open(provision._krpc_settings_path(ctx), "a", encoding="utf-8") as fh:
                fh.write("\tmanualEdit = True\n")
            self.assertFalse(provision.phase_verify(ctx, manifest))
            self.assertTrue(any(d.field == "krpcSettingsSha256" for d in ctx.verify_drift))


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


class GitSourceResolutionTests(unittest.TestCase):
    """Module boundary: BUILD-TT + PIN read the git-pinned source from a
    module-owned clone under .cache/<comp>-src (never the umbrella mods/ clone),
    so harness/ is submodule-ready. provlib.resolve_git_source is the pure
    picker; cases = cached-and-has-commit / cached-stale / absent / override."""

    CACHE = "/harness/.cache/krpc-src"
    COMMIT = "11f1f1366fa4301049f6eac6640604127a9d763b"

    def test_cached_and_has_commit_reuses_no_fetch(self):
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, cache_has_git=True, cache_has_commit=True)
        self.assertEqual(d.action, "reuse-cache")
        self.assertEqual(d.reason, "cached-and-has-commit")
        self.assertEqual(d.source_dir, self.CACHE)
        self.assertFalse(d.fetch)

    def test_cached_stale_refetches(self):
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, cache_has_git=True, cache_has_commit=False)
        self.assertEqual(d.action, "refetch-cache")
        self.assertEqual(d.reason, "cached-stale")
        self.assertEqual(d.source_dir, self.CACHE)
        self.assertTrue(d.fetch)

    def test_absent_clones(self):
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, cache_has_git=False, cache_has_commit=False)
        self.assertEqual(d.action, "clone")
        self.assertEqual(d.reason, "absent")
        self.assertEqual(d.source_dir, self.CACHE)
        self.assertTrue(d.fetch)

    # ----- cached-wrong-origin (fork re-pin; KRPC.MechJeb genhis -> darchambault,
    # 2026-07-20). A stale-origin cache refetches EVEN when the commit is present:
    # PIN peel-verifies the TAG, which only the pinned remote carries. -----

    def test_wrong_origin_refetches_even_with_commit_present(self):
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, cache_has_git=True, cache_has_commit=True,
            cache_origin_matches=False)
        self.assertEqual(d.action, "refetch-cache")
        self.assertEqual(d.reason, "cached-wrong-origin")
        self.assertTrue(d.fetch)

    def test_wrong_origin_refetches_without_commit(self):
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, cache_has_git=True, cache_has_commit=False,
            cache_origin_matches=False)
        self.assertEqual(d.action, "refetch-cache")
        self.assertEqual(d.reason, "cached-wrong-origin")

    def test_wrong_origin_irrelevant_without_cache(self):
        # No cache clone at all: absent wins regardless of the origin flag.
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, cache_has_git=False, cache_has_commit=False,
            cache_origin_matches=False)
        self.assertEqual(d.action, "clone")
        self.assertEqual(d.reason, "absent")

    def test_override_wins_over_wrong_origin(self):
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, override_path="/dev/mods/krpc",
            override_present=True, cache_has_git=True, cache_has_commit=True,
            cache_origin_matches=False)
        self.assertEqual(d.action, "use-override")

    def test_override_present_wins_over_cache(self):
        # An explicit --krpc-src clone beats the cache even when the cache is good.
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, override_path="/dev/mods/krpc",
            override_present=True, cache_has_git=True, cache_has_commit=True)
        self.assertEqual(d.action, "use-override")
        self.assertEqual(d.reason, "override-present")
        self.assertEqual(d.source_dir, "/dev/mods/krpc")
        self.assertFalse(d.fetch)

    def test_override_missing_is_flagged(self):
        # Override given but not a git clone: the shell aborts on override-missing.
        d = provlib.resolve_git_source(
            self.CACHE, self.COMMIT, override_path="/nope",
            override_present=False)
        self.assertEqual(d.action, "use-override")
        self.assertEqual(d.reason, "override-missing")
        self.assertEqual(d.source_dir, "/nope")

    def test_cache_dirname_table(self):
        self.assertEqual(provlib.GIT_SOURCE_CACHE_DIRNAME["krpc"], "krpc-src")
        self.assertEqual(provlib.GIT_SOURCE_CACHE_DIRNAME["krpc_mechjeb"], "krpc_mechjeb-src")


class EnsureGitSourceShellTests(unittest.TestCase):
    """Shell wiring for _ensure_git_source (no network / no writes): a dry-run
    with no cache clone emits the source-resolution plan line + leaves the source
    unmaterialized; an override that is not a git clone aborts EC-4."""

    def _ctx(self, dry_run, override=None):
        import provision
        pins = {"krpc": {"commit": "abc123", "sourceRepo": "https://example/krpc"}}
        return provision.ProvisionContext(
            profile_name="t", pins=pins, profile={}, umbrella_root="/um",
            dry_run=dry_run, repair=False, parsek_dll_override=None,
            krpc_src_override=override)

    def test_dry_run_absent_is_unmaterialized_and_logs_plan(self):
        import provision, tempfile
        with tempfile.TemporaryDirectory() as tmp:
            saved = provision.CACHE_DIR
            provision.CACHE_DIR = os.path.join(tmp, ".cache")  # never created here
            try:
                ctx = self._ctx(dry_run=True)
                src = provision._ensure_git_source(ctx, "krpc")
            finally:
                provision.CACHE_DIR = saved
        self.assertIsNone(src)
        self.assertFalse(ctx.aborted)
        self.assertTrue(any("source-resolution action=clone reason=absent" in l
                            for l in ctx.log_lines))

    def test_memoized_second_call_does_not_relog(self):
        import provision, tempfile
        with tempfile.TemporaryDirectory() as tmp:
            saved = provision.CACHE_DIR
            provision.CACHE_DIR = os.path.join(tmp, ".cache")
            try:
                ctx = self._ctx(dry_run=True)
                provision._ensure_git_source(ctx, "krpc")
                n_after_first = sum("source-resolution" in l for l in ctx.log_lines)
                provision._ensure_git_source(ctx, "krpc")
                n_after_second = sum("source-resolution" in l for l in ctx.log_lines)
            finally:
                provision.CACHE_DIR = saved
        self.assertEqual(n_after_first, 1)
        self.assertEqual(n_after_second, 1)

    def test_override_not_a_clone_aborts(self):
        import provision, tempfile
        with tempfile.TemporaryDirectory() as tmp:
            saved = provision.CACHE_DIR
            provision.CACHE_DIR = os.path.join(tmp, ".cache")
            try:
                # override path exists but has no .git -> override-missing -> abort
                ctx = self._ctx(dry_run=False, override=tmp)
                src = provision._ensure_git_source(ctx, "krpc", override=tmp)
            finally:
                provision.CACHE_DIR = saved
        self.assertIsNone(src)
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-4", ctx.abort_reason)


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

    def test_v054_darchambault_081_matches(self):
        # Web-verified 2026-07-20 during the fork re-pin (MechJeb 2.15.x compat).
        d = provlib.evaluate_krpc_mechjeb_pair("v0.5.4", "darchambault", "v0.8.1", "v0.5.4")
        self.assertTrue(d.ok)
        self.assertEqual(d.reason, "match")
        self.assertFalse(d.requires_web_verify)

    def test_v054_cross_fork_tag_mismatch(self):
        # A fork/tag combination NOT in the proven table stays a mismatch, even
        # when the fork and the tag each appear in other rows.
        d = provlib.evaluate_krpc_mechjeb_pair("v0.5.4", "darchambault", "v0.7.1", "v0.5.4")
        self.assertFalse(d.ok)
        self.assertEqual(d.reason, "mismatch")
        d2 = provlib.evaluate_krpc_mechjeb_pair("v0.5.4", "genhis", "v0.8.1", "v0.5.4")
        self.assertFalse(d2.ok)

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


class ParsekAuxPayloadTests(unittest.TestCase):
    """DEPLOY aux payload (version + toolbar textures) source-set decision.

    The deployed GameData/Parsek must carry Parsek.version + Textures/
    parsek_{24,32,38,64}.png alongside the DLL; a missing toolbar icon
    (parsek_32/64) is what floods ToolbarControl.OnGUI with per-frame NREs.
    Priority per dest: worktree GameData/Parsek -> repo img/ -> dev install."""

    WT_GD = "/wt/GameData/Parsek"
    WT_IMG = "/wt/img"
    DEV_GD = "/dev/GameData/Parsek"

    def _payload(self, present):
        present_set = set(present)
        return provlib.resolve_parsek_aux_payload(
            self.WT_GD, self.WT_IMG, self.DEV_GD, lambda p: p in present_set)

    def test_all_from_worktree_gamedata_when_present(self):
        present = [
            self.WT_GD + "/Parsek.version",
            self.WT_GD + "/Textures/parsek_24.png",
            self.WT_GD + "/Textures/parsek_32.png",
            self.WT_GD + "/Textures/parsek_38.png",
            self.WT_GD + "/Textures/parsek_64.png",
        ]
        pl = self._payload(present)
        self.assertEqual(pl.missing_required, ())
        self.assertEqual(pl.missing_optional, ())
        dests = {f.dest_rel for f in pl.files}
        self.assertEqual(dests, {
            "Parsek.version", "Textures/parsek_24.png", "Textures/parsek_32.png",
            "Textures/parsek_38.png", "Textures/parsek_64.png"})
        self.assertTrue(all(f.origin == "worktree-gamedata" for f in pl.files))

    def test_real_instance_layout_img_and_dev_fallback(self):
        # The actual repo shape: version only in worktree GameData/Parsek, the two
        # toolbar textures only in img/, 24/38 only in the dev install.
        present = [
            self.WT_GD + "/Parsek.version",
            self.WT_IMG + "/parsek logo - 32.png",
            self.WT_IMG + "/parsek logo - 64.png",
            self.DEV_GD + "/Textures/parsek_24.png",
            self.DEV_GD + "/Textures/parsek_38.png",
        ]
        pl = self._payload(present)
        self.assertEqual(pl.missing_required, ())
        self.assertEqual(pl.missing_optional, ())
        by_dest = {f.dest_rel: f for f in pl.files}
        self.assertEqual(by_dest["Parsek.version"].origin, "worktree-gamedata")
        self.assertEqual(by_dest["Textures/parsek_32.png"].origin, "worktree-img")
        self.assertEqual(by_dest["Textures/parsek_32.png"].source, self.WT_IMG + "/parsek logo - 32.png")
        self.assertEqual(by_dest["Textures/parsek_64.png"].origin, "worktree-img")
        self.assertEqual(by_dest["Textures/parsek_24.png"].origin, "dev-install")
        self.assertEqual(by_dest["Textures/parsek_38.png"].origin, "dev-install")

    def test_worktree_gamedata_wins_over_img_and_dev(self):
        present = [
            self.WT_GD + "/Textures/parsek_32.png",
            self.WT_IMG + "/parsek logo - 32.png",
            self.DEV_GD + "/Textures/parsek_32.png",
            self.WT_GD + "/Parsek.version",
            self.WT_IMG + "/parsek logo - 64.png",
        ]
        pl = self._payload(present)
        by_dest = {f.dest_rel: f for f in pl.files}
        self.assertEqual(by_dest["Textures/parsek_32.png"].origin, "worktree-gamedata")

    def test_missing_required_vs_optional_split(self):
        # Only 24/38 available (optional); no version, no 32/64 (required).
        present = [
            self.DEV_GD + "/Textures/parsek_24.png",
            self.DEV_GD + "/Textures/parsek_38.png",
        ]
        pl = self._payload(present)
        self.assertIn("Parsek.version", pl.missing_required)
        self.assertIn("Textures/parsek_32.png", pl.missing_required)
        self.assertIn("Textures/parsek_64.png", pl.missing_required)
        self.assertEqual(pl.missing_optional, ())
        self.assertEqual({f.dest_rel for f in pl.files},
                         {"Textures/parsek_24.png", "Textures/parsek_38.png"})

    def test_optional_missing_when_24_38_absent_everywhere(self):
        present = [
            self.WT_GD + "/Parsek.version",
            self.WT_IMG + "/parsek logo - 32.png",
            self.WT_IMG + "/parsek logo - 64.png",
        ]
        pl = self._payload(present)
        self.assertEqual(pl.missing_required, ())
        self.assertEqual(set(pl.missing_optional),
                         {"Textures/parsek_24.png", "Textures/parsek_38.png"})

    def test_aux_diff_field_routes_to_parsek_repair(self):
        # A drifted aux file must map to the parsek component so --repair
        # re-deploys it (component_of_diff_field takes parts[1]).
        self.assertEqual(
            provlib.component_of_diff_field("components.parsek.auxFiles.Textures/parsek_32.png"),
            "parsek")


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

    def test_plan_stamps_krpc_settings_hands_free(self):
        # F3: the plan must include the kRPC settings hands-free stamp WRITE.
        plan = provlib.build_action_plan(self.PINS, self.PROFILE)
        writes = [a.detail for a in plan if a.step == "INSTALL" and a.verb == "WRITE"]
        self.assertTrue(any("PluginData/settings.cfg" in d and "autoStartServers=True" in d
                            for d in writes))
        # A profile without kRPC in the stack must NOT emit the stamp line.
        plan2 = provlib.build_action_plan({}, {"stackComponents": ["parsek"]})
        self.assertFalse(any("PluginData/settings.cfg" in a.detail for a in plan2))

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

    def test_runtime_writable_dir_convention(self):
        # EC-3 hash stability across game launches: PluginData is the KSP
        # runtime-writable convention (KSPCommunityFixes/PluginData/TextureCache
        # re-drifted the instance on every launch before this prune).
        self.assertTrue(provlib.is_runtime_writable_dir("PluginData"))
        self.assertTrue(provlib.is_runtime_writable_dir("plugindata"))
        self.assertFalse(provlib.is_runtime_writable_dir("Plugins"))
        self.assertFalse(provlib.is_runtime_writable_dir("PluginDataX"))


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


class IdempotentSkipTests(unittest.TestCase):
    """SF9: decide_idempotent_skip is the pure per-component skip classifier. A
    heavy phase may hash-short-circuit ONLY when a prior COMPLETE provision left
    an instance whose on-disk hash already equals what would be installed; every
    other case (no prior, nothing recorded, absent on disk, drift) re-does the
    work so a drifted/absent component still re-provisions exactly as today.
    Regression guard: a first run or half-provision must never trust on-disk."""

    H = "a" * 64
    H2 = "b" * 64

    def test_hash_match_skips(self):
        d = provlib.decide_idempotent_skip(True, self.H, self.H)
        self.assertTrue(d.skip)
        self.assertEqual(d.reason, provlib.SKIP_HASH_MATCH)

    def test_no_prior_never_skips(self):
        # A first provision / a half-provision (prior_complete False) always works.
        d = provlib.decide_idempotent_skip(False, self.H, self.H)
        self.assertFalse(d.skip)
        self.assertEqual(d.reason, provlib.SKIP_NO_PRIOR)

    def test_drift_redoes(self):
        d = provlib.decide_idempotent_skip(True, self.H, self.H2)
        self.assertFalse(d.skip)
        self.assertEqual(d.reason, provlib.SKIP_HASH_DRIFT)

    def test_absent_on_disk_redoes(self):
        d = provlib.decide_idempotent_skip(True, self.H, None)
        self.assertFalse(d.skip)
        self.assertEqual(d.reason, provlib.SKIP_ABSENT_ON_DISK)

    def test_nothing_recorded_redoes(self):
        for expected in (None, ""):
            d = provlib.decide_idempotent_skip(True, expected, self.H)
            self.assertFalse(d.skip)
            self.assertEqual(d.reason, provlib.SKIP_NOT_RECORDED)

    def test_installed_map_digest_stable_and_discriminating(self):
        a = provlib.installed_map_digest({"KRPC.dll": "h1", "KRPC.Core.dll": "h2"})
        b = provlib.installed_map_digest({"KRPC.Core.dll": "h2", "KRPC.dll": "h1"})
        self.assertEqual(a, b, "digest is order-independent")
        c = provlib.installed_map_digest({"KRPC.dll": "h1", "KRPC.Core.dll": "hX"})
        self.assertNotEqual(a, c, "a changed member changes the digest")
        # A missing member (empty-filtered) yields a DIFFERENT digest than the full
        # set, so an absent DLL forces the skip decision off.
        partial = provlib.installed_map_digest({"KRPC.dll": "h1"})
        self.assertNotEqual(a, partial)


class InventoryDiffTests(unittest.TestCase):
    """SF10: diff_inventory is the pure installed-file inventory diff. A recorded
    file gone/changed drifts; an on-disk file the inventory never recorded is an
    ADDED drift (the gap this closes -- an injected DLL beside the hashed ones).
    PluginData paths are tolerated on ALL axes (runtime-writable, rewritten per
    launch). Regression: a clean folder yields no diff; an injected non-PluginData
    file drifts; an injected PluginData file does NOT."""

    def test_clean_folder_no_diff(self):
        rec = {"GameData/kRPC/KRPC.dll": "h1", "GameData/kRPC/KRPC.Core.dll": "h2"}
        self.assertEqual(provlib.diff_inventory(rec, dict(rec)), [])

    def test_added_file_outside_pluginfata_drifts(self):
        rec = {"GameData/kRPC/KRPC.dll": "h1"}
        cur = {"GameData/kRPC/KRPC.dll": "h1", "GameData/kRPC/evil.dll": "hX"}
        diffs = provlib.diff_inventory(rec, cur)
        self.assertEqual([(d.rel, d.kind) for d in diffs],
                         [("GameData/kRPC/evil.dll", "added")])

    def test_missing_authored_file_drifts(self):
        rec = {"GameData/kRPC/KRPC.dll": "h1", "GameData/kRPC/KRPC.Core.dll": "h2"}
        cur = {"GameData/kRPC/KRPC.dll": "h1"}
        diffs = provlib.diff_inventory(rec, cur)
        self.assertEqual([(d.rel, d.kind) for d in diffs],
                         [("GameData/kRPC/KRPC.Core.dll", "missing")])

    def test_changed_authored_file_drifts(self):
        rec = {"GameData/kRPC/KRPC.dll": "h1"}
        cur = {"GameData/kRPC/KRPC.dll": "hX"}
        diffs = provlib.diff_inventory(rec, cur)
        self.assertEqual([(d.rel, d.kind) for d in diffs],
                         [("GameData/kRPC/KRPC.dll", "changed")])

    def test_pluginfata_addition_tolerated(self):
        # LG4 blind spot: a file injected/written under PluginData is invisible to
        # the drift hash by design (runtime-writable) and must NOT red.
        rec = {"GameData/kRPC/KRPC.dll": "h1"}
        cur = {"GameData/kRPC/KRPC.dll": "h1",
               "GameData/kRPC/PluginData/TextureCache/x.bin": "hX",
               "GameData/kRPC/PluginData/settings.cfg": "hS"}
        self.assertEqual(provlib.diff_inventory(rec, cur), [])

    def test_pluginfata_change_and_removal_tolerated(self):
        rec = {"GameData/kRPC/PluginData/settings.cfg": "h1"}
        cur = {"GameData/kRPC/PluginData/settings.cfg": "hX"}  # rewritten at launch
        self.assertEqual(provlib.diff_inventory(rec, cur), [])
        self.assertEqual(provlib.diff_inventory(rec, {}), [])  # removed, tolerated

    def test_case_insensitive_path_compare(self):
        # Item 10: KSP GameData lives on a case-insensitive Windows FS, so a recorded
        # path and an on-disk scan that differ ONLY in case are the SAME file -- no
        # missing/added drift (mirrors the PluginData name.lower() handling).
        rec = {"GameData/kRPC/KRPC.dll": "h1"}
        cur = {"GameData/krpc/KRPC.dll": "h1"}  # folder cased differently by the scan
        self.assertEqual(provlib.diff_inventory(rec, cur), [])
        # A genuine content change is still caught despite the case difference.
        cur_changed = {"GameData/krpc/KRPC.dll": "hX"}
        diffs = provlib.diff_inventory(rec, cur_changed)
        self.assertEqual([d.kind for d in diffs], ["changed"])

    def test_path_has_runtime_writable_segment(self):
        self.assertTrue(provlib.path_has_runtime_writable_segment("GameData/kRPC/PluginData/x"))
        self.assertTrue(provlib.path_has_runtime_writable_segment("PluginData/x"))
        self.assertTrue(provlib.path_has_runtime_writable_segment("a\\plugindata\\b"))
        self.assertFalse(provlib.path_has_runtime_writable_segment("GameData/kRPC/KRPC.dll"))
        self.assertFalse(provlib.path_has_runtime_writable_segment("GameData/kRPC/PluginDataX/x"))

    def test_group_inventory_by_folder_unions_shared_krpc_folder(self):
        inventories = {
            "krpc": [{"rel": "GameData/kRPC/KRPC.dll", "sha256": "h1"}],
            "testingtools": [{"rel": "GameData/kRPC/TestingTools.dll", "sha256": "h2"}],
            "krpc_mechjeb": [{"rel": "GameData/kRPC/KRPC.MechJeb.dll", "sha256": "h3"}],
            "mechjeb2": [{"rel": "GameData/MechJeb2/MechJeb2.dll", "sha256": "h4"}],
        }
        groups = provlib.group_inventory_by_folder(inventories)
        owners, merged = groups["GameData/kRPC"]
        self.assertEqual(owners, ("krpc", "krpc_mechjeb", "testingtools"))
        self.assertEqual(set(merged), {
            "GameData/kRPC/KRPC.dll", "GameData/kRPC/TestingTools.dll",
            "GameData/kRPC/KRPC.MechJeb.dll"})
        mj_owners, mj_merged = groups["GameData/MechJeb2"]
        self.assertEqual(mj_owners, ("mechjeb2",))
        self.assertEqual(set(mj_merged), {"GameData/MechJeb2/MechJeb2.dll"})

    def test_group_inventory_empty_is_empty(self):
        # Old manifest without inventories tolerated at the grouping layer.
        self.assertEqual(provlib.group_inventory_by_folder({}), {})
        self.assertEqual(provlib.group_inventory_by_folder(None), {})


class DllInstallPathLayoutTests(unittest.TestCase):
    """Regression: the first live smoke false-drifted all four MechJeb2 DLLs
    because the verify resolver assumed flat component layouts; MechJeb2
    ships its DLLs under Plugins/. Fails if the resolver loses the
    flat-then-Plugins-then-recursive probe order."""

    def test_flat_then_plugins_then_recursive(self):
        import provision as prov
        with tempfile.TemporaryDirectory() as td:
            import types
            ctx = types.SimpleNamespace(instance_dir=td)
            gd = os.path.join(td, "GameData")
            os.makedirs(os.path.join(gd, "kRPC"))
            os.makedirs(os.path.join(gd, "MechJeb2", "Plugins"))
            os.makedirs(os.path.join(gd, "MechJeb2", "Parts", "Deep"))
            open(os.path.join(gd, "kRPC", "KRPC.dll"), "wb").write(b"a")
            open(os.path.join(gd, "MechJeb2", "Plugins", "MechJeb2.dll"), "wb").write(b"b")
            open(os.path.join(gd, "MechJeb2", "Parts", "Deep", "Odd.dll"), "wb").write(b"c")
            self.assertTrue(prov._dll_install_path(ctx, "krpc", "KRPC.dll").endswith(
                os.path.join("kRPC", "KRPC.dll")))
            self.assertTrue(prov._dll_install_path(ctx, "mechjeb2", "MechJeb2.dll").endswith(
                os.path.join("Plugins", "MechJeb2.dll")))
            self.assertTrue(prov._dll_install_path(ctx, "mechjeb2", "Odd.dll").endswith(
                os.path.join("Deep", "Odd.dll")))
            # missing file resolves to the flat path for None-hash drift reporting
            self.assertTrue(prov._dll_install_path(ctx, "mechjeb2", "Absent.dll").endswith(
                os.path.join("MechJeb2", "Absent.dll")))


def _live_dev_ctx(um, profile=None, pins=None, dry_run=False):
    """A live-mode ctx over a throwaway umbrella with a dev install present, so an
    abort's provision-log write stays out of the repo (mirrors the existing
    live-mode shell tests)."""
    import provision
    os.makedirs(os.path.join(um, "Kerbal Space Program", "GameData"), exist_ok=True)
    prof = profile or {"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program"}
    return provision.ProvisionContext(
        profile_name="t", pins=pins or {}, profile=prof, umbrella_root=um,
        dry_run=dry_run, repair=False, parsek_dll_override=None)


class ClonePreClearAbortShellTests(unittest.TestCase):
    """LG-round gap: the CLONE-path pre-clear abort branch of
    _copy_and_verify_dev_mod (a pre-existing instance mod DIRECTORY that the
    scoped delete cannot clear -> abort EC-3, never a merge-copy over stale
    files) was exercised only by the live run. Direct shell test."""

    def test_scoped_delete_failure_aborts_ec3(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = _live_dev_ctx(um)
            src = os.path.join(ctx.dev_install, "GameData", "MyMod")
            os.makedirs(src)
            with open(os.path.join(src, "a.dll"), "wb") as fh:
                fh.write(b"aaaa")
            dst = os.path.join(ctx.instance_dir, "GameData", "MyMod")
            os.makedirs(dst)  # a stale instance DIRECTORY forces the pre-clear branch
            saved = provision._scoped_delete_instance_subtree
            provision._scoped_delete_instance_subtree = lambda c, t: False  # cannot clear
            try:
                res = provision._copy_and_verify_dev_mod(ctx, "MyMod", src, dst)
            finally:
                provision._scoped_delete_instance_subtree = saved
            self.assertIsNone(res)
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-3", ctx.abort_reason)
            self.assertTrue(any("stale instance copy could not be cleared" in l for l in ctx.log_lines))

    def test_single_file_mod_skips_preclear(self):
        # A single-file mod dst is exactly replaced by the copy; the pre-clear
        # (rmtree) branch must NOT run for it (rmtree cannot delete a file anyway).
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = _live_dev_ctx(um)
            src = os.path.join(ctx.dev_install, "GameData", "Solo.dll")
            os.makedirs(os.path.dirname(src), exist_ok=True)
            with open(src, "wb") as fh:
                fh.write(b"solo")
            dst = os.path.join(ctx.instance_dir, "GameData", "Solo.dll")
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, "wb") as fh:
                fh.write(b"old")  # pre-existing FILE, not a dir
            called = []
            saved = provision._scoped_delete_instance_subtree
            provision._scoped_delete_instance_subtree = lambda c, t: called.append(t) or True
            try:
                h = provision._copy_and_verify_dev_mod(ctx, "Solo.dll", src, dst)
            finally:
                provision._scoped_delete_instance_subtree = saved
            self.assertTrue(h)
            self.assertFalse(ctx.aborted)
            self.assertEqual(called, [], "single-file mod must not invoke the dir pre-clear")


class VerifyAuxFilesRehashShellTests(unittest.TestCase):
    """LG-round gap: the VERIFY auxFiles re-hash loop (a dropped/edited toolbar
    texture or version file drifts) was exercised only by the live run. Direct
    shell test: a clean payload verifies; a modified or missing aux drifts.

    (An old manifest with no componentInventories is amber-tolerated here, so the
    aux check is isolated.)"""

    def _ctx(self, um):
        return _live_dev_ctx(um)

    def _write_aux(self, ctx, name, data):
        p = os.path.join(ctx.parsek_gamedata, *name.split("/"))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as fh:
            fh.write(data)
        return p

    def _manifest(self, aux):
        return {"components": {"parsek": {"auxFiles": aux}},
                "junctionTargets": {}, "devSourcedMods": {}}

    def test_clean_aux_payload_verifies(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            pv = self._write_aux(ctx, "Parsek.version", b"1.2.3")
            px = self._write_aux(ctx, "Textures/parsek_32.png", b"PNG32")
            aux = {"Parsek.version": provision.sha256_file(pv),
                   "Textures/parsek_32.png": provision.sha256_file(px)}
            self.assertTrue(provision.phase_verify(ctx, self._manifest(aux)))
            self.assertEqual(getattr(ctx, "verify_drift", []), [])

    def test_edited_aux_drifts(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            pv = self._write_aux(ctx, "Parsek.version", b"1.2.3")
            px = self._write_aux(ctx, "Textures/parsek_32.png", b"PNG32")
            aux = {"Parsek.version": provision.sha256_file(pv),
                   "Textures/parsek_32.png": provision.sha256_file(px)}
            with open(px, "wb") as fh:
                fh.write(b"TAMPERED")
            self.assertFalse(provision.phase_verify(ctx, self._manifest(aux)))
            self.assertTrue(any(d.field == "components.parsek.auxFiles.Textures/parsek_32.png"
                                for d in ctx.verify_drift))

    def test_missing_aux_drifts_with_none_actual(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            pv = self._write_aux(ctx, "Parsek.version", b"1.2.3")
            aux = {"Parsek.version": provision.sha256_file(pv),
                   "Textures/parsek_64.png": "deadbeef"}  # never written
            self.assertFalse(provision.phase_verify(ctx, self._manifest(aux)))
            self.assertTrue(any(d.field == "components.parsek.auxFiles.Textures/parsek_64.png"
                                and d.actual is None for d in ctx.verify_drift))


class Sf10InventoryVerifyShellTests(unittest.TestCase):
    """SF10 shell wiring: phase_verify diffs each install folder against the
    recorded componentInventories. An injected file in GameData/kRPC drifts; a
    PluginData injection does NOT; and an OLD manifest with no inventories is
    amber-tolerated (verifies as before)."""

    def _ctx(self, um):
        return _live_dev_ctx(um)

    def _install_file(self, ctx, rel, data):
        p = os.path.join(ctx.instance_dir, *rel.split("/"))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as fh:
            fh.write(data)
        return p

    def _manifest(self, inventories):
        return {"components": {}, "junctionTargets": {}, "devSourcedMods": {},
                "componentInventories": inventories}

    def test_clean_inventory_verifies(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            p = self._install_file(ctx, "GameData/kRPC/KRPC.dll", b"krpc")
            inv = {"krpc": [{"rel": "GameData/kRPC/KRPC.dll", "sha256": provision.sha256_file(p)}]}
            self.assertTrue(provision.phase_verify(ctx, self._manifest(inv)))
            self.assertEqual(getattr(ctx, "verify_drift", []), [])

    def test_injected_file_in_krpc_folder_drifts(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            p = self._install_file(ctx, "GameData/kRPC/KRPC.dll", b"krpc")
            inv = {"krpc": [{"rel": "GameData/kRPC/KRPC.dll", "sha256": provision.sha256_file(p)}]}
            self._install_file(ctx, "GameData/kRPC/evil.dll", b"pwned")  # the SF10 gap
            self.assertFalse(provision.phase_verify(ctx, self._manifest(inv)))
            self.assertTrue(any(d.field == "components.krpc.inventory.GameData/kRPC/evil.dll"
                                and d.kind == "added" for d in ctx.verify_drift))

    def test_injected_plugindata_file_tolerated(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            p = self._install_file(ctx, "GameData/kRPC/KRPC.dll", b"krpc")
            inv = {"krpc": [{"rel": "GameData/kRPC/KRPC.dll", "sha256": provision.sha256_file(p)}]}
            # A runtime write under PluginData is invisible by design (LG4 blind spot).
            self._install_file(ctx, "GameData/kRPC/PluginData/TextureCache/x.bin", b"cache")
            self.assertTrue(provision.phase_verify(ctx, self._manifest(inv)))
            self.assertEqual(getattr(ctx, "verify_drift", []), [])

    def test_old_manifest_without_inventories_amber_tolerated(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._install_file(ctx, "GameData/kRPC/KRPC.dll", b"krpc")
            manifest = {"components": {}, "junctionTargets": {}, "devSourcedMods": {}}  # no inventories key
            self.assertTrue(provision.phase_verify(ctx, manifest))
            self.assertTrue(any("inventory absent" in l and "[Amber]" in l for l in ctx.log_lines))

    def test_folder_fallback_arms_inventory_from_disk(self):
        # Item 2: when the prior manifest predates componentInventories (_prior_inventory
        # returns []), a hash-match SKIP must ARM the inventory by SCANNING the on-disk
        # folder, not stamp it empty. An empty inventory would make the NEXT VERIFY red
        # EVERY on-disk file as an added-file drift storm.
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._install_file(ctx, "GameData/kRPC/KRPC.dll", b"krpc")
            self._install_file(ctx, "GameData/kRPC/KRPC.Core.dll", b"core")
            ctx.prior_manifest = {"componentInventories": {}}  # pre-M-A6.2 manifest
            self.assertEqual(provision._prior_inventory(ctx, "krpc"), [])
            # The testingtools-style fallback scans the folder and records both files.
            armed = (provision._prior_inventory(ctx, "krpc")
                     or provision._inventory_of_folder(ctx, "krpc"))
            self.assertEqual(sorted(e["rel"] for e in armed),
                             ["GameData/kRPC/KRPC.Core.dll", "GameData/kRPC/KRPC.dll"])
            # Feeding the armed inventory to VERIFY passes green (no added-file drift storm).
            self.assertTrue(provision.phase_verify(ctx, self._manifest({"krpc": armed})))
            self.assertEqual(getattr(ctx, "verify_drift", []), [])

    def test_missing_authored_file_drifts(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            p = self._install_file(ctx, "GameData/kRPC/KRPC.dll", b"krpc")
            inv = {"krpc": [
                {"rel": "GameData/kRPC/KRPC.dll", "sha256": provision.sha256_file(p)},
                {"rel": "GameData/kRPC/KRPC.Core.dll", "sha256": "deadbeef"}]}  # never written
            self.assertFalse(provision.phase_verify(ctx, self._manifest(inv)))
            self.assertTrue(any(d.field == "components.krpc.inventory.GameData/kRPC/KRPC.Core.dll"
                                and d.kind == "missing" for d in ctx.verify_drift))


class Sf9InstallSkipShellTests(unittest.TestCase):
    """SF9 shell wiring for INSTALL: a prior COMPLETE provision whose installed
    DLLs already match the manifest (and whose pin is unchanged) hash-short-
    circuits the extraction; a moved pin or a drifted/absent DLL re-extracts."""

    def _ctx(self, um):
        import provision
        os.makedirs(os.path.join(um, "Kerbal Space Program"), exist_ok=True)
        return provision.ProvisionContext(
            profile_name="t",
            pins={"krpc": {"releaseZipUrl": "http://x/krpc.zip", "releaseZipSha256": "SHA_KRPC",
                           "releaseCompileDlls": ["KRPC.Core.dll"]}},
            profile={"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program",
                     "stackComponents": ["krpc"]},
            umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)

    def test_krpc_extraction_skipped_when_hash_and_pin_match(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            # An already-installed kRPC DLL whose hash the prior manifest records.
            p = os.path.join(ctx.instance_dir, "GameData", "kRPC", "KRPC.Core.dll")
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as fh:
                fh.write(b"krpc-core")
            sha = provision.sha256_file(p)
            ctx.prior_complete = True
            ctx.prior_manifest = {
                "components": {"krpc": {"sha256": "SHA_KRPC", "installedDlls": {"KRPC.Core.dll": sha}}},
                "componentInventories": {"krpc": [{"rel": "GameData/kRPC/KRPC.Core.dll", "sha256": sha}]}}
            provision._install_stack(ctx, ["krpc"])
            self.assertFalse(ctx.aborted, "must not touch the (absent) cached zip on a skip")
            self.assertTrue(any("SF9 skip kRPC extraction" in l for l in ctx.log_lines))
            self.assertEqual(ctx.component_extra["krpc"]["installedDlls"], {"KRPC.Core.dll": sha})
            self.assertEqual(ctx.component_inventories["krpc"],
                             [{"rel": "GameData/kRPC/KRPC.Core.dll", "sha256": sha}])

    def test_moved_pin_forces_reextract_not_skip(self):
        import provision
        import tempfile
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            p = os.path.join(ctx.instance_dir, "GameData", "kRPC", "KRPC.Core.dll")
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as fh:
                fh.write(b"krpc-core")
            sha = provision.sha256_file(p)
            ctx.prior_complete = True
            # Prior manifest recorded a DIFFERENT release sha256 (the pin moved).
            ctx.prior_manifest = {
                "components": {"krpc": {"sha256": "OLD_DIFFERENT", "installedDlls": {"KRPC.Core.dll": sha}}}}
            provision._install_stack(ctx, ["krpc"])
            # No cached zip present -> the re-extract attempt aborts EC-4 (proving it
            # did NOT skip). SF9 skip would have returned cleanly.
            self.assertTrue(ctx.aborted)
            self.assertIn("EC-4", ctx.abort_reason)
            self.assertFalse(any("SF9 skip kRPC extraction" in l for l in ctx.log_lines))


class IdempotentSecondRunE2ETests(unittest.TestCase):
    """SF9 end-to-end: provision the skippable phases (CLONE / DEPLOY / INSTALL /
    VERIFY + MANIFEST) twice into a temp instance fixture with fakes, no network
    and no dotnet/git. The SECOND run must hash-short-circuit every heavy phase
    (mutable surface, dev-mod copy, Parsek DLL + aux, every stack component) yet
    still VERIFY clean -- and SF10 records + re-verifies the installed-file
    inventory across both runs. Windows-only (directory junctions)."""

    def _dll_bytes(self):
        return (b"\x00\x00" + "ParsekFlight".encode("utf-16-le")
                + b"\x00\x00" + "GhostPlaybackEngine".encode("utf-16-le"))

    def _zip(self, path, entries):
        import zipfile
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with zipfile.ZipFile(path, "w") as zf:
            for name, data in entries.items():
                zf.writestr(name, data)

    def _setup(self, um):
        import provision
        dev = os.path.join(um, "Kerbal Space Program")
        os.makedirs(os.path.join(dev, "GameData", "Squad"), exist_ok=True)
        os.makedirs(os.path.join(dev, "GameData", "SquadExpansion"), exist_ok=True)
        with open(os.path.join(dev, "buildID64.txt"), "wb") as fh:
            fh.write(b"build 12345")
        with open(os.path.join(dev, "KSP_x64.exe"), "wb") as fh:
            fh.write(b"MZfakeexe")
        with open(os.path.join(dev, "settings.cfg"), "wb") as fh:
            fh.write(b"FRAMERATE_LIMIT = 120\nUI_SCALE = 1.0\n")
        # A dev-sourced mod (content-hashed + SF9-skippable).
        modsrc = os.path.join(dev, "GameData", "MyMod")
        os.makedirs(modsrc, exist_ok=True)
        with open(os.path.join(modsrc, "MyMod.dll"), "wb") as fh:
            fh.write(b"mymod-bytes")
        # Fake worktree carrying the Parsek.dll source + aux payload.
        wt = os.path.join(um, "worktree")
        dllpath = os.path.join(wt, "Source", "Parsek", "bin", "Debug", "Parsek.dll")
        os.makedirs(os.path.dirname(dllpath), exist_ok=True)
        with open(dllpath, "wb") as fh:
            fh.write(self._dll_bytes())
        gd_parsek = os.path.join(wt, "GameData", "Parsek")
        os.makedirs(os.path.join(gd_parsek, "Textures"), exist_ok=True)
        with open(os.path.join(gd_parsek, "Parsek.version"), "wb") as fh:
            fh.write(b'{"VERSION":{"MAJOR":1}}')
        for size in (32, 64):
            with open(os.path.join(gd_parsek, "Textures", "parsek_%d.png" % size), "wb") as fh:
                fh.write(b"PNG%d" % size)
        # Cached release zips + a "built" TestingTools.dll.
        cache = os.path.join(um, ".cache")
        self._zip(os.path.join(cache, "krpc.zip"), {
            "GameData/kRPC/KRPC.dll": b"krpc-main",
            "GameData/kRPC/KRPC.Core.dll": b"krpc-core"})
        self._zip(os.path.join(cache, "kmj.zip"), {
            "KRPC.MechJeb.dll": b"kmj-dll", "KRPC.MechJeb.json": b"{}"})
        tt = os.path.join(cache, "TestingTools.dll")
        with open(tt, "wb") as fh:
            fh.write(b"testingtools-shim")
        pins = {
            "krpc": {"releaseZipUrl": "http://x/krpc.zip", "releaseZipSha256": "SHA_KRPC",
                     "releaseCompileDlls": ["KRPC.Core.dll", "KRPC.dll"]},
            "krpc_mechjeb": {"downloadUrl": "http://x/kmj.zip", "tag": "v0.7.1",
                             "commit": "KMJCOMMIT"},
            "mechjeb2": {}, "kspVersion": "1.12.5"}
        profile = {
            "instanceDir": "automation/test", "baseInstall": "Kerbal Space Program",
            "stackComponents": ["krpc", "testingtools", "krpc_mechjeb", "parsek"],
            "devSourcedMods": ["MyMod"], "settings": {"FRAMERATE_LIMIT": "60"}}
        return dev, wt, cache, tt, pins, profile

    def _run_phases(self, provision, ctx, tt_path):
        junctions, dev_status = provision.phase_clone(ctx)
        deltas = provision.phase_settings(ctx)
        parsek_info = provision.phase_deploy(ctx)
        ctx.testingtools_dll = tt_path
        ctx.testingtools_sha = provision.sha256_file(tt_path)
        provision.phase_install(ctx)
        provision.phase_mm_cache(ctx)
        manifest = provision.phase_manifest(ctx, {}, junctions, deltas, parsek_info, dev_status)
        verified = provision.phase_verify(ctx, manifest)
        return verified

    def test_second_run_short_circuits_and_verifies(self):
        import json
        import provision
        import tempfile
        if os.name != "nt":
            self.skipTest("directory junctions are Windows-only")
        with tempfile.TemporaryDirectory() as um:
            dev, wt, cache, tt, pins, profile = self._setup(um)
            saved = (provision.WORKTREE_ROOT, provision.CACHE_DIR, provision.STAGE_DIR)
            provision.WORKTREE_ROOT = wt
            provision.CACHE_DIR = cache
            provision.STAGE_DIR = os.path.join(um, ".stage")
            try:
                # --- Run 1: full provision (no prior manifest -> nothing skips). ---
                ctx1 = provision.ProvisionContext(
                    profile_name="stock", pins=pins, profile=profile, umbrella_root=um,
                    dry_run=False, repair=False, parsek_dll_override=None)
                provision._load_prior_provision_state(ctx1)
                self.assertFalse(ctx1.prior_complete)
                if any("could not create junction" in l for l in ctx1.log_lines):
                    self.skipTest("junctions unavailable")
                v1 = self._run_phases(provision, ctx1, tt)
                if ctx1.aborted and "EC-8" in ctx1.abort_reason:
                    self.skipTest("junction creation failed in this environment")
                self.assertTrue(v1, "first provision must VERIFY clean: %s"
                                % getattr(ctx1, "verify_drift", None))
                self.assertFalse(any("SF9 skip" in l for l in ctx1.log_lines),
                                 "a first run has no prior manifest to skip against")
                # The manifest carries the SF10 inventories, out of the admission keys.
                manifest_path = os.path.join(ctx1.parsek_gamedata, "provision-manifest.json")
                with open(manifest_path, "r", encoding="utf-8") as fh:
                    m = json.load(fh)
                self.assertIn("componentInventories", m)
                self.assertNotIn("componentInventories", provlib.ADMISSION_KEYS)
                self.assertTrue(m["componentInventories"].get("krpc"))

                # --- Run 2: re-provision the same clean instance -> hash-short-circuit. ---
                ctx2 = provision.ProvisionContext(
                    profile_name="stock", pins=pins, profile=profile, umbrella_root=um,
                    dry_run=False, repair=False, parsek_dll_override=None)
                provision._load_prior_provision_state(ctx2)
                self.assertTrue(ctx2.prior_complete, "run 1 cleared the incomplete marker")
                v2 = self._run_phases(provision, ctx2, tt)
                self.assertTrue(v2, "idempotent re-run must VERIFY clean: %s"
                                % getattr(ctx2, "verify_drift", None))
                joined = "\n".join(ctx2.log_lines)
                self.assertIn("SF9 skip mutable-surface", joined)
                self.assertIn("SF9 skipped", joined)  # dev-mod + aux batch skips
                self.assertIn("SF9 skip stage+install-copy", joined)  # Parsek DLL
                self.assertIn("SF9 skip kRPC extraction", joined)
                self.assertIn("SF9 skip TestingTools.dll install", joined)
                self.assertIn("SF9 skip KRPC.MechJeb extraction", joined)

                # --- SF10 --repair convergence: an injected file in GameData/kRPC
                # drifts, and _repair_stack_folders scoped-deletes + re-extracts the
                # folder so the injection is REMOVED (a merge-copy could not). ---
                evil = os.path.join(ctx2.instance_dir, "GameData", "kRPC", "evil.dll")
                with open(evil, "wb") as fh:
                    fh.write(b"pwned")
                ctx3 = provision.ProvisionContext(
                    profile_name="stock", pins=pins, profile=profile, umbrella_root=um,
                    dry_run=False, repair=False, parsek_dll_override=None)
                provision._load_prior_provision_state(ctx3)
                ctx3.testingtools_dll = tt
                ctx3.testingtools_sha = provision.sha256_file(tt)
                with open(manifest_path, "r", encoding="utf-8") as fh:
                    m2 = json.load(fh)
                self.assertFalse(provision.phase_verify(ctx3, m2),
                                 "an injected file in GameData/kRPC must drift (SF10)")
                self.assertTrue(any(d.field.startswith("components.krpc.inventory") and d.kind == "added"
                                    for d in ctx3.verify_drift))
                provision._repair_stack_folders(ctx3, ["krpc"])
                self.assertFalse(os.path.exists(evil),
                                 "scoped-delete + re-extract must remove the injected file")
                self.assertTrue(os.path.isfile(
                    os.path.join(ctx3.instance_dir, "GameData", "kRPC", "KRPC.dll")))
                self.assertTrue(os.path.isfile(
                    os.path.join(ctx3.instance_dir, "GameData", "kRPC", "TestingTools.dll")),
                    "folder-sibling components re-install after the scoped delete")
            finally:
                provision.WORKTREE_ROOT, provision.CACHE_DIR, provision.STAGE_DIR = saved


class Sf9BuildTtSkipShellTests(unittest.TestCase):
    """SF9/S1: the BUILD-TT skip must gate on EVERY fresh input the shim links
    against, not just the cached-dll hash + kRPC source commit. The shim HintPaths
    into the kRPC release binaries (release-zip pin) AND the dev install's Managed
    reference DLLs (KSP version / buildID64), so a moved releaseZipSha256 pin OR a
    KSP version bump leaves the cached TestingTools.dll linked against stale refs
    and MUST rebuild. _build_testingtools is stubbed so no dotnet/git runs."""

    def _make(self, um, provision, *, prior_commit="KRPCCOMMIT", prior_zip="SHA_ZIP",
              prior_b64_matches=True):
        dev = os.path.join(um, "Kerbal Space Program")
        os.makedirs(dev, exist_ok=True)
        with open(os.path.join(dev, "buildID64.txt"), "wb") as fh:
            fh.write(b"build 777")
        # Module-owned git-source override: a dir carrying a .git subdir so
        # _ensure_git_source returns use-override WITHOUT any git clone/fetch.
        override = os.path.join(um, "krpc-src")
        os.makedirs(os.path.join(override, ".git"), exist_ok=True)
        cache = os.path.join(um, ".cache")
        os.makedirs(cache, exist_ok=True)
        tt_path = os.path.join(cache, "TestingTools.dll")
        with open(tt_path, "wb") as fh:
            fh.write(b"testingtools-shim-bytes")
        tt_sha = provision.sha256_file(tt_path)
        dev_b64 = provision.sha256_file(os.path.join(dev, "buildID64.txt"))
        pins = {"krpc": {"commit": "KRPCCOMMIT", "releaseZipSha256": "SHA_ZIP"},
                "testingtools": {}}
        ctx = provision.ProvisionContext(
            profile_name="t", pins=pins,
            profile={"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program"},
            umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None,
            krpc_src_override=override)
        ctx.prior_complete = True
        ctx.prior_manifest = {
            "buildId64Sha256": dev_b64 if prior_b64_matches else "OLD_B64",
            "components": {
                "krpc": {"commit": prior_commit, "sha256": prior_zip},
                "testingtools": {"dllSha256": tt_sha}}}
        return ctx, cache

    def _run(self, provision, ctx, cache):
        built = {"called": False}
        saved_cache = provision.CACHE_DIR
        saved_build = provision._build_testingtools
        provision.CACHE_DIR = cache

        def _stub(*a, **k):
            built["called"] = True
        provision._build_testingtools = _stub
        try:
            provision.phase_build_tt(ctx, {"krpc": "KRPCCOMMIT"})
        finally:
            provision.CACHE_DIR = saved_cache
            provision._build_testingtools = saved_build
        return built["called"]

    def test_all_inputs_stable_skips_build(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx, cache = self._make(um, provision)
            called = self._run(provision, ctx, cache)
            self.assertFalse(called, "stable inputs must SKIP the dotnet build")
            self.assertTrue(any("SF9 skip dotnet build" in l for l in ctx.log_lines))
            self.assertFalse(ctx.aborted)

    def test_moved_krpc_release_pin_forces_rebuild(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx, cache = self._make(um, provision, prior_zip="OLD_ZIP_SHA")
            called = self._run(provision, ctx, cache)
            self.assertTrue(called, "a moved kRPC release-zip pin must rebuild")
            self.assertFalse(any("SF9 skip dotnet build" in l for l in ctx.log_lines))

    def test_bumped_dev_buildid64_forces_rebuild(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx, cache = self._make(um, provision, prior_b64_matches=False)
            called = self._run(provision, ctx, cache)
            self.assertTrue(called, "a bumped dev buildID64 must rebuild")
            self.assertFalse(any("SF9 skip dotnet build" in l for l in ctx.log_lines))

    def test_moved_krpc_source_commit_forces_rebuild(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx, cache = self._make(um, provision, prior_commit="OLDCOMMIT")
            called = self._run(provision, ctx, cache)
            self.assertTrue(called, "a retagged kRPC source commit must rebuild")
            self.assertFalse(any("SF9 skip dotnet build" in l for l in ctx.log_lines))


class Sf9MutableSurfaceStatTests(unittest.TestCase):
    """SF9/S2: the CLONE mutable-surface skip cannot trust buildID64.txt alone --
    a partially-deleted instance or a swapped stock Managed DLL leaves buildID64
    intact yet the copied surface corrupt, and VERIFY never re-hashes the stock
    Managed tree. The skip gate re-stats KSP_x64_Data/Managed (fileCount + bytes)
    and refuses to skip on any drift."""

    def test_stat_matches_pure(self):
        self.assertTrue(provlib.mutable_surface_stat_matches({"fileCount": 3, "bytes": 100}, 3, 100))

    def test_missing_recorded_stat_no_match(self):
        self.assertFalse(provlib.mutable_surface_stat_matches(None, 0, 0))
        self.assertFalse(provlib.mutable_surface_stat_matches({}, 0, 0))

    def test_count_or_size_drift_no_match(self):
        rec = {"fileCount": 3, "bytes": 100}
        self.assertFalse(provlib.mutable_surface_stat_matches(rec, 2, 100))  # deleted file
        self.assertFalse(provlib.mutable_surface_stat_matches(rec, 3, 99))   # resized file

    def _ctx(self, um):
        import provision
        os.makedirs(os.path.join(um, "Kerbal Space Program"), exist_ok=True)
        return provision.ProvisionContext(
            profile_name="t", pins={},
            profile={"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program"},
            umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)

    def _setup_instance(self, provision, ctx):
        b64 = os.path.join(ctx.instance_dir, "buildID64.txt")
        os.makedirs(os.path.dirname(b64), exist_ok=True)
        with open(b64, "wb") as fh:
            fh.write(b"build 999")
        # Fresh-source rule (item 10): the mutable-surface skip also gates on the DEV
        # install's current buildID64 (the surface is copied FROM there). Seed it equal
        # to the instance/recorded so the clean case still skips.
        dev_b64 = os.path.join(ctx.dev_install, "buildID64.txt")
        os.makedirs(os.path.dirname(dev_b64), exist_ok=True)
        with open(dev_b64, "wb") as fh:
            fh.write(b"build 999")
        managed = os.path.join(ctx.instance_dir, "KSP_x64_Data", "Managed")
        os.makedirs(managed, exist_ok=True)
        for n, data in (("Assembly-CSharp.dll", b"asmcs"), ("UnityEngine.dll", b"unity-bytes")):
            with open(os.path.join(managed, n), "wb") as fh:
                fh.write(data)
        ctx.prior_complete = True
        ctx.prior_manifest = {
            "buildId64Sha256": provision.sha256_file(b64),
            "mutableSurfaceManagedStat": provision._managed_stat_dict(ctx),
            "junctionTargets": {}}
        return managed

    def test_clean_instance_skips(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._setup_instance(provision, ctx)
            self.assertTrue(provision._clone_surface_skips(ctx, {}))

    def test_partial_deletion_forces_recopy(self):
        # The required S2 case: delete a Managed DLL, rerun -> must NOT skip.
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            managed = self._setup_instance(provision, ctx)
            os.remove(os.path.join(managed, "UnityEngine.dll"))
            self.assertFalse(provision._clone_surface_skips(ctx, {}),
                             "a deleted Managed DLL must refuse the mutable-surface skip")
            self.assertTrue(any("Managed stat drift" in l for l in ctx.log_lines))

    def test_old_manifest_without_stat_no_skip(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._setup_instance(provision, ctx)
            del ctx.prior_manifest["mutableSurfaceManagedStat"]  # pre-S2 manifest
            self.assertFalse(provision._clone_surface_skips(ctx, {}),
                             "a manifest without the Managed stat must re-copy to arm the check")

    def test_dev_buildid64_bump_forces_recopy(self):
        # Item 10 fresh-source rule: a dev-side KSP version bump changes the dev
        # buildID64 while the instance still matches the OLD recorded hash. An
        # instance-vs-recorded-only gate would wrongly skip and never re-propagate the
        # new KSP; the fresh-source gate must refuse the skip.
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._setup_instance(provision, ctx)
            # Bump ONLY the dev install's buildID64 (instance + recorded stay "build 999").
            with open(os.path.join(ctx.dev_install, "buildID64.txt"), "wb") as fh:
                fh.write(b"build 1000")
            self.assertFalse(provision._clone_surface_skips(ctx, {}),
                             "a bumped dev buildID64 must refuse the mutable-surface skip")
            self.assertTrue(any("dev buildID64 changed" in l for l in ctx.log_lines))


class Sf9DevModSourceGateTests(unittest.TestCase):
    """SF9/S3: the dev-sourced-mod skip must gate on the FRESH dev-source tree-hash
    (dev-source == recorded == instance), not instance-vs-manifest alone. A
    dev-side mod update must re-copy; an unchanged one must skip. Drives the real
    phase_clone (junctions are Windows-only)."""

    def _setup(self, um, provision):
        dev = os.path.join(um, "Kerbal Space Program")
        os.makedirs(os.path.join(dev, "GameData", "Squad"), exist_ok=True)
        os.makedirs(os.path.join(dev, "GameData", "SquadExpansion"), exist_ok=True)
        with open(os.path.join(dev, "buildID64.txt"), "wb") as fh:
            fh.write(b"build 12345")
        with open(os.path.join(dev, "KSP_x64.exe"), "wb") as fh:
            fh.write(b"MZfakeexe")
        modsrc = os.path.join(dev, "GameData", "MyMod")
        os.makedirs(modsrc, exist_ok=True)
        with open(os.path.join(modsrc, "MyMod.dll"), "wb") as fh:
            fh.write(b"mymod-v1")
        pins = {"kspVersion": "1.12.5"}
        profile = {"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program",
                   "stackComponents": ["parsek"], "devSourcedMods": ["MyMod"]}
        return dev, modsrc, pins, profile

    def _ctx(self, provision, um, pins, profile):
        return provision.ProvisionContext(
            profile_name="stock", pins=pins, profile=profile, umbrella_root=um,
            dry_run=False, repair=False, parsek_dll_override=None)

    def _prior_from(self, provision, ctx, dev_status):
        b64 = os.path.join(ctx.instance_dir, "buildID64.txt")
        return {"buildId64Sha256": provision.sha256_file(b64),
                "mutableSurfaceManagedStat": provision._managed_stat_dict(ctx),
                "junctionTargets": {}, "devSourcedMods": dict(dev_status),
                "components": {}}

    def test_touched_source_recopies_untouched_skips(self):
        import provision
        if os.name != "nt":
            self.skipTest("directory junctions are Windows-only")
        with tempfile.TemporaryDirectory() as um:
            dev, modsrc, pins, profile = self._setup(um, provision)
            saved = provision.CACHE_DIR
            provision.CACHE_DIR = os.path.join(um, ".cache")
            try:
                # Run 1: seed (no prior manifest -> full copy).
                ctx1 = self._ctx(provision, um, pins, profile)
                _, dev_status1 = provision.phase_clone(ctx1)
                if ctx1.aborted and "EC-8" in ctx1.abort_reason:
                    self.skipTest("junction creation unavailable in this environment")
                self.assertFalse(ctx1.aborted, ctx1.abort_reason)
                v1 = dev_status1["MyMod"]

                # Touch the DEV source -> the fresh-source gate must RE-COPY even
                # though the instance still matches the old recorded hash.
                with open(os.path.join(modsrc, "MyMod.dll"), "wb") as fh:
                    fh.write(b"mymod-v2-CHANGED")
                ctx2 = self._ctx(provision, um, pins, profile)
                ctx2.prior_complete = True
                ctx2.prior_manifest = self._prior_from(provision, ctx2, dev_status1)
                _, dev_status2 = provision.phase_clone(ctx2)
                self.assertFalse(ctx2.aborted, ctx2.abort_reason)
                self.assertNotEqual(dev_status2["MyMod"], v1,
                                    "a touched dev source must re-copy the new bytes")
                self.assertFalse(any("SF9 skipped" in l and "dev-sourced" in l
                                     for l in ctx2.log_lines),
                                 "a touched dev source must NOT log a dev-mod skip")

                # Re-run with the source now unchanged -> skip.
                ctx3 = self._ctx(provision, um, pins, profile)
                ctx3.prior_complete = True
                ctx3.prior_manifest = self._prior_from(provision, ctx3, dev_status2)
                _, dev_status3 = provision.phase_clone(ctx3)
                self.assertFalse(ctx3.aborted, ctx3.abort_reason)
                self.assertEqual(dev_status3["MyMod"], dev_status2["MyMod"])
                self.assertTrue(any("SF9 skipped" in l and "dev-sourced" in l
                                    for l in ctx3.log_lines),
                                "an unchanged dev source must skip the re-copy")
            finally:
                provision.CACHE_DIR = saved


class Sf9PriorProvisionFenceTests(unittest.TestCase):
    """N2: the abort-then-rerun fence. _load_prior_provision_state must only trust
    on-disk state (prior_complete=True) when a manifest is present AND there is no
    .provision-incomplete marker; a present marker or an unreadable manifest leaves
    prior_complete False so the heavy phases re-provision from scratch."""

    def _ctx(self, um):
        import provision
        os.makedirs(os.path.join(um, "Kerbal Space Program"), exist_ok=True)
        return provision.ProvisionContext(
            profile_name="t", pins={},
            profile={"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program"},
            umbrella_root=um, dry_run=False, repair=False, parsek_dll_override=None)

    def _write_manifest(self, provision, ctx, text):
        os.makedirs(ctx.parsek_gamedata, exist_ok=True)
        with open(os.path.join(ctx.parsek_gamedata, "provision-manifest.json"),
                  "w", encoding="utf-8") as fh:
            fh.write(text)

    def test_clean_manifest_no_marker_is_complete(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._write_manifest(provision, ctx, '{"schema": 1}')
            provision._load_prior_provision_state(ctx)
            self.assertTrue(ctx.prior_complete)
            self.assertEqual(ctx.prior_manifest, {"schema": 1})

    def test_marker_present_forces_incomplete(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._write_manifest(provision, ctx, '{"schema": 1}')
            with open(provision._incomplete_marker_path(ctx), "w", encoding="utf-8") as fh:
                fh.write("provisioning in progress\n")
            provision._load_prior_provision_state(ctx)
            self.assertFalse(ctx.prior_complete,
                             "a present .provision-incomplete marker must force a full re-provision")
            self.assertEqual(ctx.prior_manifest, {"schema": 1},
                             "the manifest is still parsed even when incomplete")

    def test_unreadable_manifest_forces_incomplete(self):
        import provision
        with tempfile.TemporaryDirectory() as um:
            ctx = self._ctx(um)
            self._write_manifest(provision, ctx, "{not valid json")
            provision._load_prior_provision_state(ctx)
            self.assertFalse(ctx.prior_complete)
            self.assertIsNone(ctx.prior_manifest)
            self.assertTrue(any("prior manifest unreadable" in l for l in ctx.log_lines),
                            "the unreadable-manifest except branch must warn")


class PinStableKrpcMechjebCommitTests(unittest.TestCase):
    """N1: _install_pin_stable for krpc_mechjeb gates on BOTH the tag and the
    pinned commit (the tag is mutable). A tag-only match is no longer stable."""

    def _ctx(self, prior, pin):
        import provision
        ctx = provision.ProvisionContext(
            profile_name="t", pins={"krpc_mechjeb": pin},
            profile={}, umbrella_root=".", dry_run=True, repair=False, parsek_dll_override=None)
        ctx.prior_manifest = {"components": {"krpc_mechjeb": prior}}
        return ctx

    def test_tag_and_commit_match_stable(self):
        import provision
        ctx = self._ctx({"tag": "v0.7.1", "commit": "C1"}, {"tag": "v0.7.1", "commit": "C1"})
        self.assertTrue(provision._install_pin_stable(ctx, "krpc_mechjeb"))

    def test_moved_commit_not_stable(self):
        import provision
        ctx = self._ctx({"tag": "v0.7.1", "commit": "OLD"}, {"tag": "v0.7.1", "commit": "NEW"})
        self.assertFalse(provision._install_pin_stable(ctx, "krpc_mechjeb"))

    def test_tag_match_but_no_commit_not_stable(self):
        import provision
        ctx = self._ctx({"tag": "v0.7.1"}, {"tag": "v0.7.1"})
        self.assertFalse(provision._install_pin_stable(ctx, "krpc_mechjeb"))


if __name__ == "__main__":
    unittest.main()
