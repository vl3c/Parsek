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


class LiveUnimplementedTests(unittest.TestCase):
    """Guards BLOCKER 3: a live run must abort at the first unimplemented heavy
    phase (EC-LIVE) rather than half-provision; --dry-run is unaffected;
    live --repair aborts up front."""

    def _ctx(self, dry_run, repair=False):
        import provision
        return provision.ProvisionContext(
            profile_name="x", pins={}, profile={}, umbrella_root=".",
            dry_run=dry_run, repair=repair, parsek_dll_override=None)

    def test_dry_run_phase_not_guarded(self):
        import provision
        ctx = self._ctx(dry_run=True)
        self.assertFalse(provision._guard_live_unimplemented(ctx, "Build-TT"))
        self.assertFalse(ctx.aborted)

    def test_live_phase_aborts_ec_live(self):
        import provision
        ctx = self._ctx(dry_run=False)
        self.assertTrue(provision._guard_live_unimplemented(ctx, "Build-TT"))
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-LIVE", ctx.abort_reason)

    def test_live_repair_aborts_in_run(self):
        import provision
        ctx = self._ctx(dry_run=False, repair=True)
        code = provision.run(ctx)
        self.assertEqual(code, 2)
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-LIVE", ctx.abort_reason)


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
        manifest = {"junctionTargets": {
            "GameData/Squad": "/dev/Squad",
            "GameData/SquadExpansion": "/dev/SquadExpansion",
        }}
        existing = {"/dev/Squad"}
        dangling = provlib.verify_junctions(manifest, lambda p: p in existing)
        self.assertEqual(dangling, ["GameData/SquadExpansion"])

    def test_all_resolve_is_empty(self):
        manifest = {"junctionTargets": {"GameData/Squad": "/dev/Squad"}}
        self.assertEqual(provlib.verify_junctions(manifest, lambda p: True), [])

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


if __name__ == "__main__":
    unittest.main()
