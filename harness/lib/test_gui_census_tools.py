"""Pure tests for the two GUI-census operator tools:
`harness/tools/gui_contact_sheet.py` and `harness/tools/stage_local_fixture.py`.

Both are OPERATOR tools rather than run-flow code - neither is imported by `run.py` -
so the contract they owe is narrower than the V3 contact sheet's, but it is the same in
kind: read-only over run artifacts / a real save, safe on every partial or missing
input, and self-contained static output. These cells cover the pure selection,
grouping, HTML and classification functions against a FAKE directory tree (the
fake-KSP smoke convention: no real game, no real run, no real save).

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import io
import os
import shutil
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_TOOLS = os.path.join(os.path.dirname(_HERE), "tools")
for _p in (_HERE, _TOOLS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import gui_contact_sheet as gcs  # noqa: E402
import hlib  # noqa: E402
import stage_local_fixture as slf  # noqa: E402


def _touch(path, body=b"x"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as fh:
        fh.write(body)


class ContactSheetSelectionTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="parsek-gui-sheet-")
        self.addCleanup(shutil.rmtree, self.dir, True)

    def test_only_images_are_listed_and_they_are_name_sorted(self):
        # Name-sorted, not mtime-sorted: two runs of the same census must produce the
        # same page, and the labels are what carry the reading order.
        for name in ("ksc-settings-basic.png", "ksc-main-advanced.png",
                     "KSP.log", "parsek-render-manifest.txt", "notes.md"):
            _touch(os.path.join(self.dir, name))
        self.assertEqual(["ksc-main-advanced.png", "ksc-settings-basic.png"],
                         gcs.list_images(self.dir))

    def test_extension_match_is_case_insensitive(self):
        # KSP writes .png; a hand-dropped .PNG must still show rather than vanish.
        _touch(os.path.join(self.dir, "shot.PNG"))
        _touch(os.path.join(self.dir, "shot2.JPEG"))
        self.assertEqual(["shot.PNG", "shot2.JPEG"], gcs.list_images(self.dir))

    def test_the_extension_set_matches_the_harness_harvest(self):
        # A file the harvest copies but this sheet ignores is an invisible artifact; the
        # reverse is a broken <img>. The harvest set is wider than the image set by
        # exactly the GUI-tree dump suffix (a JSON sidecar the sheet must NOT feed to an
        # <img>); everything else in it is an image this sheet lists.
        harvested = set(hlib.ARTIFACT_SHOTS_SUFFIXES)
        self.assertIn(".gui.json", harvested)
        self.assertEqual(harvested - {".gui.json"}, set(gcs.IMAGE_EXTENSIONS))

    def test_an_absent_directory_is_empty_not_an_error(self):
        self.assertEqual([], gcs.list_images(os.path.join(self.dir, "nope")))

    def test_directories_named_like_images_are_not_listed(self):
        os.makedirs(os.path.join(self.dir, "trap.png"))
        self.assertEqual([], gcs.list_images(self.dir))


class ContactSheetGroupingTests(unittest.TestCase):
    def test_label_is_the_stem(self):
        self.assertEqual("ksc-main-advanced",
                         gcs.label_of("ksc-main-advanced.png"))
        self.assertEqual("no-extension", gcs.label_of("no-extension"))

    def test_group_is_the_first_dash_segment_when_it_is_a_known_scene(self):
        self.assertEqual("ksc", gcs.group_of("ksc-main-advanced.png"))
        self.assertEqual("flight", gcs.group_of("flight-gloops.png"))
        self.assertEqual("map", gcs.group_of("map-overlay.png"))

    def test_an_unmodelled_prefix_lands_in_other_rather_than_being_dropped(self):
        # This is a VIEWER: a label whose shape it does not model must still be visible.
        # Dropping it would silently shorten the census a reviewer is counting on.
        self.assertEqual(gcs.OTHER_GROUP, gcs.group_of("weird_name.png"))
        self.assertEqual(gcs.OTHER_GROUP, gcs.group_of("vab-parts.png"))

    def test_sections_follow_the_declared_order_and_empty_ones_are_dropped(self):
        names = ["flight-a.png", "ksc-b.png", "zzz.png", "ksc-a.png"]
        sections = gcs.group_images(names)
        self.assertEqual(["ksc", "flight", gcs.OTHER_GROUP],
                         [g for g, _ in sections])
        # Incoming order preserved within a section.
        self.assertEqual(["ksc-b.png", "ksc-a.png"], sections[0][1])
        # No heading over nothing.
        self.assertNotIn("map", [g for g, _ in sections])


class ContactSheetRenderTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="parsek-gui-render-")
        self.addCleanup(shutil.rmtree, self.dir, True)

    def test_every_image_gets_a_cell_with_its_label_as_the_caption(self):
        html = gcs.render_html("2026-01-01_0000_shots",
                               ["ksc-main-advanced.png", "ksc-settings-basic.png"])
        self.assertIn("ksc-main-advanced", html)
        self.assertIn("ksc-settings-basic", html)
        self.assertEqual(2, html.count("<figure>"))
        # Bare filenames as src: the page is written INTO the image directory.
        self.assertIn('src="ksc-main-advanced.png"', html)

    def test_dynamic_text_is_escaped_and_hrefs_are_quoted(self):
        # A capture label cannot contain a space or '<' (the verb rejects both), but the
        # page must not depend on that: it renders whatever is in the folder, including
        # a file a human dropped there.
        html = gcs.render_html("d", ["a b&<script>.png"])
        self.assertNotIn("<script>", html)
        self.assertIn("&amp;", html)
        self.assertIn("a%20b", html)

    def test_an_empty_directory_renders_a_named_cause_not_a_blank_page(self):
        html = gcs.render_html("2026-01-01_0000_shots", [])
        self.assertIn("no images in this directory", html)
        # The two ways it happens are named, so a reader can tell them apart.
        self.assertIn("captured none", html)
        self.assertIn("not a _shots directory", html)

    def test_generate_writes_index_html_into_the_directory(self):
        _touch(os.path.join(self.dir, "ksc-main-advanced.png"))
        path, count = gcs.generate(self.dir)
        self.assertEqual(os.path.join(self.dir, "index.html"), path)
        self.assertEqual(1, count)
        with io.open(path, encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("ksc-main-advanced", body)
        # No leftover tmp file from the atomic write.
        self.assertEqual(["index.html", "ksc-main-advanced.png"],
                         sorted(os.listdir(self.dir)))

    def test_generate_is_idempotent_and_never_lists_its_own_output(self):
        # index.html is not an image, so a second run must not grow the page.
        _touch(os.path.join(self.dir, "a.png"))
        gcs.generate(self.dir)
        _path, count = gcs.generate(self.dir)
        self.assertEqual(1, count)

    def test_it_does_not_write_the_v3_sheets_files(self):
        # The two tools share the folder and must not collide: this one owns
        # `index.html` INSIDE a *_shots dir, the V3 sheet owns `<runId>_contact.html`
        # and `index.html` at the results ROOT.
        _touch(os.path.join(self.dir, "a.png"))
        gcs.generate(self.dir)
        for name in os.listdir(self.dir):
            self.assertFalse(name.endswith("_contact.html"))


class StageLocalFixtureTests(unittest.TestCase):
    def test_the_leaf_rule_is_the_harness_run_save_name_rule(self):
        # The leaf is ALSO the run save name inside the instance, so a name this tool
        # accepted and validate_spec rejected would stage cleanly and then be unusable.
        for good in ("c1-gui", "c1 gui", "c1_gui", "C1"):
            with self.subTest(leaf=good):
                self.assertIsNone(slf.validate_leaf(good))
        for bad in ("", ".", "..", "c1/gui", "c1\\gui", "c1.gui", "c1*"):
            with self.subTest(leaf=bad):
                self.assertIsNotNone(slf.validate_leaf(bad))

    def test_the_copy_rule_keeps_persistent_and_every_parsek_sidecar(self):
        for rel in ("persistent.sfs", "persistent.loadmeta",
                    "Parsek/Recordings/abc.prec",
                    "Parsek/Recordings/abc_vessel.craft",
                    "Parsek/RewindPoints/rp1.sfs",
                    "Parsek/GameState/state.cfg",
                    "Ships/VAB/Kerbal X.craft",
                    "AddOns/DistantObject/Settings.cfg"):
            with self.subTest(rel=rel):
                keep, why = slf.classify_entry(rel, keep_quicksaves=False)
                self.assertTrue(keep, "%s dropped as %s" % (rel, why))

    def test_no_quicksaves_drops_only_the_saves_own_quicksaves(self):
        # The discriminator is "an .sfs that is neither persistent.sfs at the root nor
        # under Parsek/". A RewindPoints .sfs is load-bearing and must survive.
        for rel in ("quicksave.sfs", "quicksave #1.sfs", "persistent.sfs.bak.sfs"):
            with self.subTest(rel=rel):
                keep, why = slf.classify_entry(rel, keep_quicksaves=False)
                self.assertFalse(keep)
                self.assertIn("quicksave", why)
        keep, _ = slf.classify_entry("Parsek/RewindPoints/rp1.sfs",
                                     keep_quicksaves=False)
        self.assertTrue(keep)
        # And with the flag off (the default) nothing .sfs is dropped at all.
        for rel in ("quicksave.sfs", "persistent.sfs"):
            with self.subTest(rel=rel):
                self.assertTrue(slf.classify_entry(rel, keep_quicksaves=True)[0])

    def test_a_nested_persistent_sfs_is_not_mistaken_for_the_real_one(self):
        # The root-only check matters: KSP's own pre-Parsek backup siblings and any
        # nested copy are quicksave-class, not THE save.
        keep, why = slf.classify_entry("Backup/persistent.sfs", keep_quicksaves=False)
        self.assertFalse(keep)
        self.assertIn("quicksave", why)

    def test_os_junk_is_always_skipped(self):
        for rel in ("Thumbs.db", "Ships/.DS_Store"):
            with self.subTest(rel=rel):
                keep, why = slf.classify_entry(rel, keep_quicksaves=True)
                self.assertFalse(keep)
                self.assertEqual("os junk", why)

    def test_the_analyzer_output_dir_is_dropped_unconditionally(self):
        # NOT tidiness. `run.py` invokes the analyzer with `-FreshSaveGate`, whose
        # Forbid mode treats the PRESENCE of a findings baseline in the produced save as
        # a failure (BASELINE-FORBIDDEN -> INVALID(fixture-authoring)). A staged
        # `analysis/baseline.cfg` would therefore make the lane INVALID before it read a
        # single window, naming fixture authoring rather than the copy that carried it
        # in - and the census lanes' `[expectations.analyzer] gating = false` does not
        # cover it, because report-only demotes FINDINGS only and an analyzer INVALID
        # stays a verdict (hlib.analyzer_report_only_covers). Unconditional: it is
        # dropped even with quicksaves KEPT.
        for keep_quicksaves in (True, False):
            for rel in ("analysis/baseline.cfg", "analysis/c1.analysis.txt",
                        "analysis/c1.analysis.json", "Analysis/baseline.cfg"):
                with self.subTest(rel=rel, keep_quicksaves=keep_quicksaves):
                    keep, why = slf.classify_entry(rel, keep_quicksaves)
                    self.assertFalse(keep)
                    self.assertEqual("analyzer output dir", why)
        # ...and only at the TOP level: a Parsek sidecar tree that happens to carry an
        # `analysis` segment deeper down is untouched.
        self.assertTrue(slf.classify_entry("Parsek/analysis/keep.cfg",
                                           keep_quicksaves=False)[0])

    def test_a_loadmeta_follows_its_own_sfs_in_both_directions(self):
        # A `.loadmeta` is KSP's sidecar for the `.sfs` of the same stem. Dropping
        # `quicksave.sfs` while keeping `quicksave.loadmeta` would leave the save folder
        # advertising a quicksave whose bytes are gone; keeping `persistent.loadmeta` is
        # the same rule read the other way.
        keep, why = slf.classify_entry("quicksave.loadmeta", keep_quicksaves=False)
        self.assertFalse(keep)
        self.assertIn("quicksave", why)
        self.assertTrue(slf.classify_entry("persistent.loadmeta",
                                           keep_quicksaves=False)[0])
        # With the flag off (the default) nothing is dropped either way.
        self.assertTrue(slf.classify_entry("quicksave.loadmeta",
                                           keep_quicksaves=True)[0])

    def test_plan_copy_walks_a_fake_save(self):
        src = tempfile.mkdtemp(prefix="parsek-fake-save-")
        self.addCleanup(shutil.rmtree, src, True)
        _touch(os.path.join(src, "persistent.sfs"))
        _touch(os.path.join(src, "persistent.loadmeta"))
        _touch(os.path.join(src, "quicksave.sfs"))
        _touch(os.path.join(src, "quicksave.loadmeta"))
        _touch(os.path.join(src, "Parsek", "Recordings", "a.prec"))
        _touch(os.path.join(src, "analysis", "baseline.cfg"))
        _touch(os.path.join(src, "Thumbs.db"))
        copy, skipped = slf.plan_copy(src, keep_quicksaves=False)
        self.assertEqual(["Parsek/Recordings/a.prec", "persistent.loadmeta",
                          "persistent.sfs"], copy)
        self.assertEqual(["Thumbs.db", "analysis/baseline.cfg",
                          "quicksave.loadmeta", "quicksave.sfs"], skipped)

    def test_the_local_saves_dir_is_the_prefix_hlib_classifies(self):
        # The tool and hlib must agree on the directory, or a staged fixture would sit
        # somewhere no spec can name.
        rel = os.path.relpath(slf.LOCAL_SAVES_DIR, hlib_harness_root()).replace(
            os.sep, "/")
        self.assertEqual(hlib.LOCAL_FIXTURE_PREFIX.rstrip("/"), rel)
        self.assertTrue(hlib.is_local_fixture_template(
            hlib.LOCAL_FIXTURE_PREFIX + "c1-gui"))

    def test_the_staged_dir_is_gitignored_with_only_two_committed_files(self):
        # The whole premise: a clone gets the spec and not the bytes. If the ignore file
        # ever stopped covering `*`, a staged 42 MB career would show up in git status
        # and eventually in a commit.
        ignore = os.path.join(slf.LOCAL_SAVES_DIR, ".gitignore")
        self.assertTrue(os.path.isfile(ignore))
        with io.open(ignore, encoding="utf-8") as fh:
            lines = [l.strip() for l in fh if l.strip() and not l.startswith("#")]
        self.assertEqual(["*", "!.gitignore", "!README.md"], lines)


def hlib_harness_root():
    return os.path.dirname(_HERE)


if __name__ == "__main__":
    unittest.main()
