"""Guards the recurring stale-doc trap: prose that quotes a scenario spec's numbers
and then rots when the spec moves.

WHY THIS EXISTS. On the autotest-b1chute branch a budget moved three separate times,
and each time at least one document was left quoting the superseded value - including
the forensics entry that is itself about a stale claim, and a commit whose subject line
promised "add a check for it" while adding no check. Two independent reviewers flagged
the same gap. This is that check.

WHAT IT DOES. Reads the numbers out of the committed `.toml` specs (the single source
of truth) and asserts that every documented claim about them agrees. It is deliberately
NOT a generic doc linter: each entry below names one spec value and the exact prose
patterns that quote it, so a failure says which document contradicts which spec.

WHEN A CLAIM MOVES. If a doc sentence is legitimately rewritten, update its pattern
here in the same commit. If a spec value moves, this test tells you every place the
prose has to follow. Deliberately-historical prose (an entry that says "the first draft
said 600, which was wrong") is fine: the patterns match the CURRENT-VALUE sentence only,
so narrate old values freely as long as the live claim is right.

Stdlib only; no KSP, no network. ASCII only.
"""

import os
import re
import tomllib
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
DOCS = os.path.join(REPO_ROOT, "docs", "dev")


def _spec(name):
    with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
        return tomllib.load(fh)


def _mission_step_budget(spec):
    for step in spec["driver"]["steps"]:
        if step.get("phase") == "mission":
            return step["budget"]
    raise AssertionError("spec has no mission step")


def _read(path):
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


class B1DocSpecSyncTests(unittest.TestCase):
    """Every number the docs assert about B1-pad-hop must match B1-pad-hop.toml.

    Each case is (doc file, regex with ONE capturing group, expected value). The regex
    must be specific enough that it cannot drift onto an unrelated number.
    """

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B1-pad-hop.toml")
        mp = cls.spec["driver"]["missionParams"]
        cls.truth = {
            "descent": mp["descentTimeoutSeconds"],
            "fullDeployAlt": mp["chuteFullDeployAltMeters"],
            "armRate": mp["chuteArmMaxRateMps"],
            "mission": _mission_step_budget(cls.spec),
            "wall": cls.spec["runtime"]["budgetSeconds"],
        }

    def _check(self, doc, pattern, key, occurrences=None):
        text = _read(os.path.join(DOCS, doc))
        found = re.findall(pattern, text)
        self.assertTrue(found, "%s: pattern never matched, so it guards nothing "
                               "(did the sentence get rewritten?): %s" % (doc, pattern))
        if occurrences is not None:
            self.assertEqual(len(found), occurrences,
                             "%s: expected %d sites for %s, found %d - a new claim was "
                             "added or one was dropped; update this test with it"
                             % (doc, occurrences, key, len(found)))
        want = int(self.truth[key])
        for got in found:
            self.assertEqual(int(got), want,
                             "%s quotes %s as %s but B1-pad-hop.toml says %d"
                             % (doc, key, got, want))

    # -- the status doc's B1 row -------------------------------------------------
    def test_status_row_budgets(self):
        self._check("autotest-status.md", r"descent 240 -> (\d+) s", "descent")
        self._check("autotest-status.md", r"mission 600 -> (\d+) s", "mission")
        self._check("autotest-status.md", r"wall 900 -> (\d+) s", "wall")

    def test_status_row_full_deploy_altitude(self):
        self._check("autotest-status.md",
                    r"chuteFullDeployAltMeters was raised 1000 -> (\d+)", "fullDeployAlt")

    # -- the forensics entry -----------------------------------------------------
    def test_forensics_budgets(self):
        self._check("todo-and-known-bugs.md", r"descent 240 -> (\d+) s", "descent")
        self._check("todo-and-known-bugs.md", r"mission 600 -> (\d+) s", "mission")
        self._check("todo-and-known-bugs.md", r"wall 900 -> (\d+) s", "wall")

    def test_forensics_full_deploy_altitude(self):
        self._check("todo-and-known-bugs.md",
                    r"`chuteFullDeployAltMeters` \((\d+),", "fullDeployAlt")

    # -- the design doc: BOTH the example block and the invariant prose ----------
    # The invariant prose sits ~40 lines below the example block, which is exactly how
    # it survived the one-off sweep that only looked at the block.
    def test_design_doc_example_block(self):
        doc = "design-autotest-mission-library.md"
        self._check(doc, r"chuteFullDeployAltMeters = (\d+)", "fullDeployAlt")
        self._check(doc, r"descentTimeoutSeconds = (\d+)", "descent")
        self._check(doc, r"chuteArmMaxRateMps\s+= (\d+)", "armRate")

    def test_design_doc_budget_invariant_prose(self):
        doc = "design-autotest-mission-library.md"
        self._check(doc, r"B1: (\d+) >= 30 \(connect\)", "mission")
        self._check(doc, r"B1: (\d+) >= 240 \(LoadGame\)", "wall")
        self._check(doc, r"B1: \d+ >= 240 \(LoadGame\) \+ (\d+) \(mission\)", "mission")


class B2DocSpecSyncTests(unittest.TestCase):
    """B2's numbers share the same design-doc sentences as B1's, and a blanket
    search-and-replace for a B1 value has already matched B2's block once on this
    branch. Pin B2 too so the next one is caught."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B2-lko-ascent.toml")

    def test_design_doc_matches_b2_spec(self):
        text = _read(os.path.join(DOCS, "design-autotest-mission-library.md"))
        mission = _mission_step_budget(self.spec)
        wall = self.spec["runtime"]["budgetSeconds"]
        m = re.search(r"B2: (\d+) >= 30 \+\s*\n?\s*\(420\+300\)", text)
        self.assertIsNotNone(m, "B2 mission-budget invariant sentence not found")
        self.assertEqual(int(m.group(1)), int(mission))
        m = re.search(r"B2: (\d+) >= 240 \+ (\d+) \+ margin", text)
        self.assertIsNotNone(m, "B2 runtime-budget invariant sentence not found")
        self.assertEqual(int(m.group(1)), int(wall))
        self.assertEqual(int(m.group(2)), int(mission))


if __name__ == "__main__":
    unittest.main()
