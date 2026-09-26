"""Unit tests for the pinned GameData-mod path (GT-8 closure, D17 persistent-rotation).

A profile ``[[optionalMods]]`` entry carrying ``pin = "<pins table>"`` is installed from
the shared artifact cache (content-addressed by the committed sha256, re-hashed on every
use) instead of being copied from the dev GameData. Pure cells over provlib, plus shell
cells over provision's INSTALL helper in a throwaway umbrella. Stdlib only, no network.
"""

import io
import os
import tempfile
import tomllib
import unittest
import zipfile

import provlib

HERE = os.path.dirname(os.path.abspath(__file__))


def _load(rel):
    with open(os.path.join(HERE, rel), "rb") as fh:
        return tomllib.load(fh)


def _zip(entries):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, data in entries:
            if name.endswith("/"):
                zf.writestr(zipfile.ZipInfo(name), b"")
            else:
                zf.writestr(name, data)
    return buf.getvalue()


class OptionalModSourceDecisionTests(unittest.TestCase):

    def test_a_pin_wins_over_a_dev_copy_and_over_required(self):
        self.assertEqual(provlib.OPTIONAL_MOD_SOURCE_PINNED,
                         provlib.decide_optional_mod_source({"pin": "x", "required": True}, False))
        # A stale dev copy never shadows the pinned bytes.
        self.assertEqual(provlib.OPTIONAL_MOD_SOURCE_PINNED,
                         provlib.decide_optional_mod_source({"pin": "x"}, True))

    def test_unpinned_entries_keep_the_ec12_contract(self):
        self.assertEqual(provlib.OPTIONAL_MOD_SOURCE_DEV_COPY,
                         provlib.decide_optional_mod_source({"required": True}, True))
        self.assertEqual(provlib.OPTIONAL_MOD_SOURCE_ABORT,
                         provlib.decide_optional_mod_source({"required": True}, False))
        self.assertEqual(provlib.OPTIONAL_MOD_SOURCE_ABSENT,
                         provlib.decide_optional_mod_source({"required": False}, False))
        self.assertEqual(provlib.OPTIONAL_MOD_SOURCE_ABSENT,
                         provlib.decide_optional_mod_source({}, False))


class PinnedModPinTests(unittest.TestCase):

    GOOD = {"kind": "gamedata-mod", "gamedataFolders": ["PersistentRotation"],
            "downloadUrl": "https://host.invalid/x.zip", "sha256": "a" * 64}

    def test_a_good_pin_validates(self):
        self.assertIsNone(provlib.validate_pinned_mod_pin("x", self.GOOD))

    def test_missing_table_wrong_kind_and_bad_folders_are_refused(self):
        self.assertIn("no [x] table", provlib.validate_pinned_mod_pin("x", None))
        self.assertIn("kind", provlib.validate_pinned_mod_pin("x", dict(self.GOOD, kind="release")))
        for folders in ([], None, "PersistentRotation", ["a/b"], ["a\\b"], [".."], [""]):
            self.assertIsNotNone(
                provlib.validate_pinned_mod_pin("x", dict(self.GOOD, gamedataFolders=folders)),
                folders)

    def test_profile_pinned_mods_keeps_profile_order_and_skips_unpinned(self):
        prof = {"optionalMods": [
            {"name": "B", "pin": "b"}, {"name": "Dev", "required": False}, {"name": "A", "pin": "a"}]}
        self.assertEqual([("B", "b"), ("A", "a")], provlib.profile_pinned_mods(prof))
        self.assertEqual([], provlib.profile_pinned_mods({}))


class PinnedModArtifactTests(unittest.TestCase):

    def setUp(self):
        self.pins = _load("pins.toml")

    def test_modded_compat_fetches_both_pinned_zips_and_stock_minimal_none(self):
        mc = provlib.pinned_mod_artifacts(self.pins, _load("profiles/modded-compat.toml"))
        self.assertEqual(["persistentrotation", "spacetuxlibrary"], [a.comp for a in mc])
        for a in mc:
            self.assertIsNotNone(provlib.artifact_cache_key(a.sha256), a.comp)
            self.assertTrue(a.url.startswith("https://github.com/linuxgurugamer/"), a.url)
        self.assertEqual([], provlib.pinned_mod_artifacts(
            self.pins, _load("profiles/stock-minimal.toml")))

    def test_the_committed_pins_carry_the_operator_approved_digests(self):
        # Pinned on 2026-09-26 from the one approved download of each zip; a pin move
        # must be a deliberate edit of this cell too.
        self.assertEqual("f455e527cc3618cfd36c9fe1d80d0697edf8af45a9adf914f2c8e510e169121b",
                         self.pins["persistentrotation"]["sha256"])
        self.assertEqual("e916b6907e8310e08baa49532d04f92068864cb38b93afd664bf712b190197f1",
                         self.pins["spacetuxlibrary"]["sha256"])
        self.assertEqual(["PersistentRotation"], self.pins["persistentrotation"]["gamedataFolders"])
        self.assertEqual(["SpaceTuxLibrary"], self.pins["spacetuxlibrary"]["gamedataFolders"])

    def test_every_profile_pin_resolves_to_a_valid_table(self):
        for name in ("modded-compat.toml", "stock-minimal.toml"):
            for opt_name, key in provlib.profile_pinned_mods(_load("profiles/" + name)):
                self.assertIsNone(provlib.validate_pinned_mod_pin(key, self.pins.get(key)),
                                  "%s: %s -> %s" % (name, opt_name, key))

    def test_modded_compat_persistent_rotation_is_required_and_pinned(self):
        opts = {o["name"]: o for o in _load("profiles/modded-compat.toml")["optionalMods"]}
        self.assertEqual(True, opts["PersistentRotation"]["required"])
        self.assertEqual("persistentrotation", opts["PersistentRotation"]["pin"])
        self.assertEqual(True, opts["SpaceTuxLibrary"]["required"])
        self.assertEqual("spacetuxlibrary", opts["SpaceTuxLibrary"]["pin"])

    def test_all_pinned_artifacts_adds_gamedata_mods_after_the_stack(self):
        comps = [a.comp for a in provlib.all_pinned_artifacts(self.pins)]
        self.assertEqual(["krpc", "krpc_mechjeb", "mechjeb2"], comps[:3])
        self.assertIn("persistentrotation", comps)
        self.assertIn("spacetuxlibrary", comps)

    def test_the_cache_seeder_takes_a_pinned_mod_zip(self):
        sha = self.pins["persistentrotation"]["sha256"]
        plan = provlib.plan_cache_seed(self.pins, {"x/whatever.zip": sha, "x/other": "b" * 64}, [])
        self.assertEqual([("x/whatever.zip", sha, "persistentrotation", "copy")],
                         [(e.source, e.sha256, e.comp, e.action) for e in plan])

    def test_the_action_plan_names_the_pinned_install_only_where_declared(self):
        mc = provlib.build_action_plan(self.pins, _load("profiles/modded-compat.toml"))
        pinned = [a.detail for a in mc if "pinned optional mod" in a.detail]
        self.assertEqual(2, len(pinned))
        self.assertTrue(any("GameData/PersistentRotation" in d for d in pinned))
        sm = provlib.build_action_plan(self.pins, _load("profiles/stock-minimal.toml"))
        self.assertFalse(any("pinned optional mod" in a.detail for a in sm))


class PinnedModZipPlanTests(unittest.TestCase):

    def test_declared_folders_land_as_is_and_everything_else_is_skipped(self):
        names = [
            "GameData/PersistentRotation/",
            "GameData/PersistentRotation/Plugins/PersistentRotationUpgraded.dll",
            "GameData/PersistentRotation/README.md",
            "GameData/Other/Plugins/x.dll",
            "README.md",
            "GameData/PersistentRotation",
        ]
        self.assertEqual(
            [("GameData/PersistentRotation/Plugins/PersistentRotationUpgraded.dll",
              "GameData/PersistentRotation/Plugins/PersistentRotationUpgraded.dll"),
             ("GameData/PersistentRotation/README.md", "GameData/PersistentRotation/README.md")],
            provlib.plan_pinned_mod_install(names, ["PersistentRotation"]))

    def test_a_bare_rooted_zip_is_wrapped_under_gamedata(self):
        self.assertEqual(
            [("SpaceTuxLibrary/Plugins/KSP_Log.dll", "GameData/SpaceTuxLibrary/Plugins/KSP_Log.dll")],
            provlib.plan_pinned_mod_install(["SpaceTuxLibrary/Plugins/KSP_Log.dll"], ["SpaceTuxLibrary"]))

    def test_folder_match_is_exact(self):
        self.assertEqual([], provlib.plan_pinned_mod_install(
            ["GameData/PersistentRotationX/a.dll", "GameData/persistentrotation/a.dll"],
            ["PersistentRotation"]))

    def test_a_zip_slip_entry_is_left_for_the_escape_guard(self):
        plan = provlib.plan_pinned_mod_install(["GameData/PR/../../evil.dll"], ["PR"])
        self.assertEqual(1, len(plan))
        self.assertTrue(provlib.gamedata_dest_escapes(plan[0][1]))


class PinnedModInstallShellTests(unittest.TestCase):
    """provision._install_pinned_mods and the DOWNLOAD / CLONE hooks, in a throwaway
    umbrella. Only the network (``provision._download``) and CACHE_DIR are redirected."""

    def setUp(self):
        import provision
        self.provision = provision
        self.tmp = tempfile.TemporaryDirectory()
        self.um = self.tmp.name
        os.makedirs(os.path.join(self.um, "Kerbal Space Program", "GameData"))
        self.saved_cache_dir = provision.CACHE_DIR
        self.saved_download = provision._download
        provision.CACHE_DIR = os.path.join(self.um, "wt-cache")
        self.downloads = []

        def no_download(ctx, url):
            self.downloads.append(url)
            return None

        provision._download = no_download
        self.zip = _zip([
            ("GameData/PR/", b""),
            ("GameData/PR/Plugins/PR.dll", b"dll-bytes"),
            ("GameData/PR/PR.version", b"{}"),
            ("README.txt", b"root file"),
        ])
        self.sha = provision.sha256_bytes(self.zip)
        self.pins = {"pr": {"kind": "gamedata-mod", "version": "1.0", "gamedataFolders": ["PR"],
                            "downloadUrl": "https://host.invalid/pr-1.0.zip", "sha256": self.sha}}
        self.profile = {"instanceDir": "automation/test", "baseInstall": "Kerbal Space Program",
                        "optionalMods": [{"name": "PR", "required": True, "pin": "pr"}]}

    def tearDown(self):
        self.provision.CACHE_DIR = self.saved_cache_dir
        self.provision._download = self.saved_download
        self.tmp.cleanup()

    def _ctx(self, dry_run=False):
        return self.provision.ProvisionContext(
            profile_name="t", pins=self.pins, profile=self.profile, umbrella_root=self.um,
            dry_run=dry_run, repair=False, parsek_dll_override=None)

    def _seed(self, data):
        d = os.path.join(self.um, "automation", ".artifact-cache")
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, self.sha), "wb") as fh:
            fh.write(data)

    def _inst(self, *parts):
        return os.path.join(self.um, "automation", "test", "GameData", *parts)

    def test_extracts_the_declared_folder_from_the_cache_and_records_its_tree_hash(self):
        self._seed(self.zip)
        stale = self._inst("PR", "Plugins", "Stale.dll")
        os.makedirs(os.path.dirname(stale))
        with open(stale, "wb") as fh:
            fh.write(b"old")
        ctx = self._ctx()
        self.provision._install_pinned_mods(ctx)
        self.assertFalse(ctx.aborted, ctx.abort_reason)
        with open(self._inst("PR", "Plugins", "PR.dll"), "rb") as fh:
            self.assertEqual(b"dll-bytes", fh.read())
        self.assertFalse(os.path.exists(stale), "the stale folder must be cleared first")
        self.assertFalse(os.path.exists(self._inst("README.txt")))
        rec = ctx.pinned_mods["PR"]
        self.assertEqual(("pr", "1.0", self.sha), (rec["pin"], rec["version"], rec["sha256"]))
        self.assertEqual(self.provision._content_tree_hash(self._inst("PR"), ctx), rec["treeHash"])
        self.assertTrue(any("re-hash" in l and "OK" in l for l in ctx.log_lines))

    def test_a_corrupt_cache_entry_aborts_ec3_and_extracts_nothing(self):
        self._seed(b"not the pinned bytes")
        ctx = self._ctx()
        self.provision._install_pinned_mods(ctx)
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-3", ctx.abort_reason)
        self.assertFalse(os.path.exists(self._inst("PR")))

    def test_an_absent_cache_entry_aborts_ec4(self):
        ctx = self._ctx()
        self.provision._install_pinned_mods(ctx)
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-4", ctx.abort_reason)

    def test_dry_run_writes_nothing(self):
        ctx = self._ctx(dry_run=True)
        self.provision._install_pinned_mods(ctx)
        self.assertFalse(ctx.aborted)
        self.assertFalse(os.path.exists(self._inst("PR")))
        self.assertTrue(any("would extract pinned mod PR" in l for l in ctx.log_lines))

    def test_download_takes_the_pinned_zip_from_the_cache_without_a_download(self):
        self._seed(self.zip)
        self.pins.update({
            "krpc": {"releaseZipUrl": "OPEN", "releaseZipSha256": "OPEN"},
            "krpc_mechjeb": {"downloadUrl": "OPEN", "releaseZipSha256": "OPEN"},
            "mechjeb2": {"downloadUrl": "OPEN", "sha256": "OPEN"},
        })
        ctx = self._ctx(dry_run=True)
        self.provision.phase_download(ctx)
        self.assertTrue(any("pr release zip" in l and "cache-hit" in l for l in ctx.log_lines),
                        ctx.log_lines)
        self.assertEqual([], self.downloads)

    def test_download_refuses_an_unusable_pin_before_fetching(self):
        self.pins["pr"]["kind"] = "release"
        ctx = self._ctx()
        self.provision.phase_download(ctx)
        self.assertTrue(ctx.aborted)
        self.assertIn("EC-13", ctx.abort_reason)
        self.assertEqual([], self.downloads)

    def test_clone_never_looks_a_pinned_required_mod_up_in_the_dev_gamedata(self):
        ctx = self._ctx(dry_run=True)
        self.provision.phase_clone(ctx)
        self.assertFalse(ctx.aborted, ctx.abort_reason)
        self.assertTrue(any("optional mod PR pinned" in l for l in ctx.log_lines))


if __name__ == "__main__":
    unittest.main()
