"""Fixture + spec gates for the pre-Parsek backup lane (PPB-1 / PPB-2).

WHAT THIS FILE GUARDS. The lane rests on two claims that are invisible in the
bytes and expensive to discover on a flight:

  1. `preparsek-untouched-career` is DERIVED from `career-earned-pad` - itself a
     derived fixture - by reducing its ParsekScenario node to the inert `name` +
     `scene` form and dropping the rest of Parsek's state. If the base is
     re-harvested and the derivation is not re-run, the lane flies a subject
     nobody meant, and BOTH failure modes are silent on a flight:
       - the node comes back POPULATED -> `HasParsekGameplayFootprint` reads the
         save as already-touched, the backup never fires;
       - the node goes MISSING -> the seam's FLIGHT route never instantiates the
         ParsekScenario module at all (it calls `StartAndFocusVessel` with no
         `UpdateScenarioModules`), so `OnLoad` never runs and the backup STILL
         never fires - CL-1 flight 1's zero-recording run.
     Either way PPB-1 reds on tokens that read exactly like a product defect.
  2. The two specs pin LITERAL log lines that live in C# format strings. A rename
     in `PreParsekBackup.cs` or `ParsekScenario.cs` would leave the pins pointing
     at text nothing emits - again red on a flight rather than here.

IT READS OUTSIDE `harness/`, deliberately, joining the small set that does
(`CommittedBatchTallySourceSyncTests`, `test_doc_spec_sync.py`,
`test_the_c_sharp_writer_still_emits_pointcount`, `test_career_earned_pad.py`).
Read-only; needs the full repo checkout, not a built DLL.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import re
import unittest

try:
    import tomllib
except ImportError:  # pragma: no cover - Python < 3.11
    import tomli as tomllib  # type: ignore

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_REPO = os.path.dirname(_HARNESS)

SAVES = os.path.join(_HARNESS, "fixtures", "saves")
SCENARIOS = os.path.join(_HARNESS, "scenarios")
PARSEK_SRC = os.path.join(_REPO, "Source", "Parsek")

UNTOUCHED = "preparsek-untouched-career"
BRANDNEW = "preparsek-brandnew-career"
SPEC_UNTOUCHED = "PPB-1-untouched-career-backup.toml"
SPEC_BRANDNEW = "PPB-2-brandnew-career-skip.toml"


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_preparsek_fixtures.py")
    spec = importlib.util.spec_from_file_location("build_preparsek_fixtures", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _spec(name):
    with open(os.path.join(SCENARIOS, name), "rb") as fh:
        return tomllib.load(fh)


def _read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


class PreParsekFixtureDriftTests(unittest.TestCase):
    """WIRES `build_preparsek_fixtures.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_both_committed_fixtures_are_byte_identical_to_a_fresh_rebuild(self):
        problems = []
        for target, base, exp_scn, exp_set in self.builder.TARGETS:
            problems += self.builder.build_one(target, base, exp_scn, exp_set,
                                               check_only=True)
        self.assertEqual([], problems)

    def test_the_targets_table_is_the_two_this_lane_stages(self):
        # Anti-vacuity: the cell above iterates TARGETS, so an emptied or renamed
        # table would pass it while checking nothing.
        self.assertEqual([UNTOUCHED, BRANDNEW],
                         [t[0] for t in self.builder.TARGETS])

    def test_the_untouched_fixture_keeps_an_INERT_parsek_scenario_node(self):
        # THE TWO-SIDED STATEMENT, made directly rather than through the builder,
        # because getting either side wrong is a silent flight-long failure:
        #  - node ABSENT boots the FLIGHT route with no ParsekScenario MODULE
        #    (LoadGameImpl's focusable branch does no UpdateScenarioModules), so
        #    OnLoad never runs and the backup can never fire - CL-1 flight 1's
        #    zero-recording run, and what the first cut of this fixture would have
        #    reproduced;
        #  - node POPULATED makes HasParsekGameplayFootprint true and the backup is
        #    skipped with reason=already-parsek-footprint.
        # Only `name` + `scene` satisfies both, which is also exactly what KSP's own
        # AddToAllGames injection writes for a freshly created proto.
        d = os.path.join(SAVES, UNTOUCHED)
        self.assertFalse(os.path.isdir(os.path.join(d, "Parsek")),
                         "%s carries a Parsek/ subdir, which suppresses the backup" % UNTOUCHED)
        lines = [l.rstrip("\r\n") for l in
                 _read(os.path.join(d, "persistent.sfs")).splitlines()]
        self.assertIn("\t\tname = ParsekScenario", lines,
                      "%s has no ParsekScenario node; the FLIGHT route would never "
                      "instantiate the module and the backup could not fire" % UNTOUCHED)
        i = lines.index("\t\tname = ParsekScenario")
        # The whole node is five lines: header, brace, name, scene, close.
        self.assertEqual(["\tSCENARIO", "\t{"], lines[i - 2:i])
        self.assertTrue(lines[i + 1].startswith("\t\tscene = "), lines[i + 1])
        self.assertEqual("\t}", lines[i + 2],
                         "the ParsekScenario node carries more than name+scene, so "
                         "HasParsekGameplayFootprint reads it as a footprint")
        self.assertNotIn("ParsekSettings", "\n".join(lines))

    def test_the_untouched_fixture_is_not_brand_new_empty(self):
        # The OTHER half of the gate, and the half that makes this fixture
        # different from every pre-existing footprint-free one. Mirrors
        # PreParsekBackup.IsBrandNewEmptySave's four inputs at the text level; the
        # authoritative check against the real C# predicate is the xUnit cell
        # PreParsekBackupFixtureShapeTests, which parses the same file through
        # CareerSaveParser.
        lines = [l.strip() for l in
                 _read(os.path.join(SAVES, UNTOUCHED, "persistent.sfs")).splitlines()]
        self.assertGreater(lines.count("VESSEL"), 0,
                           "the untouched fixture must keep a VESSEL node - a "
                           "vessel-less save is brand-new-empty AND routes LoadGame "
                           "to SPACECENTER, moving PPB-1's pinned scene")
        self.assertGreater(lines.count("CONTRACT"), 0,
                           "the untouched fixture must keep its CONTRACT nodes")

    def test_the_brandnew_fixture_is_empty_and_footprint_free(self):
        d = os.path.join(SAVES, BRANDNEW)
        self.assertFalse(os.path.isdir(os.path.join(d, "Parsek")))
        text = _read(os.path.join(d, "persistent.sfs"))
        self.assertNotIn("name = ParsekScenario", text)
        self.assertNotIn("ParsekSettings", text)
        # NODE names, not substrings: the save carries a `CONTRACTS` SCENARIO with
        # no CONTRACT children, and a `Progress` node with no milestones. Those
        # empty containers are what brand-new-empty LOOKS like, so the assertion
        # has to count child nodes rather than mentions.
        lines = [l.strip() for l in text.splitlines()]
        self.assertEqual(0, lines.count("VESSEL"))
        self.assertEqual(0, lines.count("CONTRACT"))
        self.assertEqual(0, lines.count("Science"))


class PreParsekSpecWiringTests(unittest.TestCase):
    """The two specs must stage the two fixtures, and stage them ALONE."""

    def test_each_spec_stages_its_own_fixture(self):
        self.assertEqual("fixtures/saves/%s" % UNTOUCHED,
                         _spec(SPEC_UNTOUCHED)["fixture"]["saveTemplate"])
        self.assertEqual("fixtures/saves/%s" % BRANDNEW,
                         _spec(SPEC_BRANDNEW)["fixture"]["saveTemplate"])

    def test_no_other_spec_shares_either_leaf(self):
        # THE PRODUCED-SAVE CLOBBER RACE, gated rather than trusted to a comment:
        # two specs sharing a saveTemplate leaf share one staged directory in the
        # instance, so a sibling run can overwrite a finished run's produced save.
        # These two fixtures exist partly BECAUSE of that rule (the brand-new one
        # is a copy of `fresh-career`, which four specs already share), so a later
        # spec quietly adopting one would undo the reason it was built.
        for leaf, owner in ((UNTOUCHED, SPEC_UNTOUCHED), (BRANDNEW, SPEC_BRANDNEW)):
            users = []
            for name in sorted(os.listdir(SCENARIOS)):
                if not name.endswith(".toml"):
                    continue
                tmpl = (_spec(name).get("fixture", {}) or {}).get("saveTemplate", "")
                if os.path.basename(tmpl.rstrip("/")) == leaf:
                    users.append(name)
            self.assertEqual([owner], users,
                             "%s is staged by more than one spec" % leaf)

    def test_the_untouched_fixture_declares_the_craft_its_pin_measures(self):
        # PPB-1 pins `craftDirsMirrored=1`, which is only reachable if the staged save
        # HAS a craft dir at cold-load time. The fixture commits no craft (the shared
        # library rule), so the whole pin rests on one manifest row. Dropping that row
        # would leave the spec pinning a value the run can no longer produce - a red on
        # a flight instead of here.
        with open(os.path.join(_HARNESS, "fixtures", "shared-ships.toml"), "rb") as fh:
            manifest = tomllib.load(fh)
        ships = (manifest.get("ships", {}) or {}).get(UNTOUCHED)
        self.assertTrue(ships, "%s declares no shared craft, so PPB-1's "
                               "craftDirsMirrored=1 pin cannot be satisfied" % UNTOUCHED)
        self.assertFalse(os.path.isdir(os.path.join(SAVES, UNTOUCHED, "Ships")),
                         "%s commits a craft physically; the overlay rule (and "
                         "SharedShipsManifestTests) forbids declaring AND committing"
                         % UNTOUCHED)

    def test_neither_spec_arms_a_gating_expectation_block(self):
        # Both ship REPORT-ONLY: every pin is derived, none measured. `saveParse`
        # arming is separately pinned by ARMED_ALLOWLIST in test_hlib; this cell
        # states the lane's own intent at the source.
        for name in (SPEC_UNTOUCHED, SPEC_BRANDNEW):
            exp = _spec(name).get("expectations", {}) or {}
            for block, body in exp.items():
                if isinstance(body, dict):
                    self.assertNotEqual(True, body.get("gating"),
                                        "%s arms [expectations.%s]" % (name, block))


class PreParsekLogTokenSourceSyncTests(unittest.TestCase):
    """Every literal the two specs pin must still exist in the C# that emits it.

    Same shape as `test_the_c_sharp_writer_still_emits_pointcount`: the pins are a
    hardcoded copy of text that lives in another language, so the copy needs a
    gate. Each entry is (source file, literal fragment, why it is pinned). The
    fragments are chosen to be RUNS BETWEEN INTERPOLATION HOLES - an interpolated
    C# string is not stored the way it renders, and this cell reads the SOURCE, so
    a fragment spanning a hole would be checking a string no compiler ever sees.
    """

    EXPECTED = (
        ("PreParsekBackup.cs", "Captured pre-Parsek backup: save='",
         "PPB-1's capture pin"),
        ("PreParsekBackup.cs", "pristineVerdict=",
         "PPB-1's pristine-outcome token"),
        ("PreParsekBackup.cs", "outcome=captured-pristine-unverified",
         "the Warn-severity half of the pristine check - a reason nothing pins but "
         "whose EXISTENCE is what keeps a read failure off the Error channel"),
        ("PreParsekBackup.cs", "First-contact backup: save='",
         "PPB-1's decision-side pin"),
        ("PreParsekBackup.cs", "Skip: reason=",
         "every skip pin on both specs"),
        ("PreParsekBackup.cs", "outcome=captured-not-pristine",
         "the forbidden non-pristine token on both specs"),
        ("ParsekScenario.cs", "OnSave: saving ",
         "PPB-1's ordering pair - both the required existence proof and the "
         "forbidden before-the-capture form"),
        (os.path.join("InGameTests", "PreParsekBackupInGameTests.cs"),
         "[InGameTest] backup shape OK: dir='",
         "PPB-1's craft-mirror pin"),
        (os.path.join("InGameTests", "PreParsekBackupInGameTests.cs"),
         "craftDirsMirrored=",
         "PPB-1's craft-mirror pin, value half"),
    )

    def test_every_pinned_literal_still_exists_in_the_source(self):
        for leaf, fragment, why in self.EXPECTED:
            with self.subTest(fragment=fragment):
                text = _read(os.path.join(PARSEK_SRC, leaf))
                self.assertIn(fragment, text,
                              "%s no longer contains %r, which %s depends on"
                              % (leaf, fragment, why))

    def test_the_skip_reason_literals_the_specs_name_are_all_real(self):
        # The `reason=` values are produced by PreParsekBackup.ShouldBackup's own
        # branches plus the two pre-gate skips. Pull them out of the specs and
        # require each to appear in the source, so a renamed reason reds here
        # rather than turning a forbidden pin into a permanent no-op.
        text = _read(os.path.join(PARSEK_SRC, "PreParsekBackup.cs"))
        seen = 0
        for name in (SPEC_UNTOUCHED, SPEC_BRANDNEW):
            lc = ((_spec(name).get("expectations", {}) or {})
                  .get("logContracts", {}) or {})
            for pat in list(lc.get("required", [])) + list(lc.get("forbidden", [])):
                for reason in re.findall(r"Skip: reason=([a-z0-9-]+)", pat):
                    seen += 1
                    # TWO spellings are legitimate and both must be accepted: the
                    # pre-gate skips interpolate the token straight into the message
                    # (`reason=no-persistent-sfs`), while ShouldBackup's branches
                    # assign it to the out-parameter (`reason = "brand-new-empty"`)
                    # and the message interpolates the variable. Requiring only the
                    # first would red on four of the six real reasons.
                    self.assertTrue(
                        ("reason=%s" % reason) in text
                        or ('reason = "%s"' % reason) in text,
                        "%s pins Skip: reason=%s but PreParsekBackup.cs emits no "
                        "such literal in either the interpolated or the "
                        "ShouldBackup-assignment form" % (name, reason))
        self.assertGreater(seen, 3, "no reason literals extracted - cell is inert")


if __name__ == "__main__":
    unittest.main()
