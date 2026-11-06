"""Drift and shape gates for the `career-new-goal-twin` fixture (CTW-1's host).

The fixture is DERIVED from `cs-career-new` by `harness/tools/build_career_new_goal_twin.py`:
the stock New Game plus a second Offered copy of its "reach space" exploration contract under
another guid and seed. A hand edit to either save, or a change to the derivation, reds here
instead of in a flight whose twin was never in the pool.
"""
import importlib.util
import os
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_new_goal_twin.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-new-goal-twin", "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-new", "persistent.sfs")
SPEC = os.path.join(HARNESS_ROOT, "scenarios", "CTW-1-contract-goal-twin-withdrawn.toml")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_new_goal_twin", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="ascii", newline="") as fh:
        return fh.read()


class CareerNewGoalTwinDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_only_adds_the_twin_node(self):
        b = _load_builder()
        src, out = _read(SOURCE).split("\n"), _read(FIXTURE).split("\n")
        start, end = b.find_contract_block(src, b.ORIGINAL_GUID)
        size = end - start + 1
        self.assertEqual(len(src) + size, len(out))
        self.assertEqual(src[:end + 1], out[:end + 1])
        self.assertEqual(src[end + 1:], out[end + 1 + size:])

    def test_the_twin_differs_from_the_original_only_by_guid_and_seed(self):
        b = _load_builder()
        out = _read(FIXTURE).split("\n")
        o_start, o_end = b.find_contract_block(out, b.ORIGINAL_GUID)
        t_start, t_end = b.find_contract_block(out, b.TWIN_GUID)
        original, twin = out[o_start:o_end + 1], out[t_start:t_end + 1]
        self.assertEqual(len(original), len(twin))
        diff = [(a.strip(), c.strip()) for a, c in zip(original, twin) if a != c]
        self.assertEqual(
            [("guid = " + b.ORIGINAL_GUID, "guid = " + b.TWIN_GUID),
             ("seed = " + b.ORIGINAL_SEED, "seed = " + b.TWIN_SEED)],
            diff)

    def test_both_copies_are_offered_reach_space_explorations_of_kerbin(self):
        b = _load_builder()
        out = _read(FIXTURE).split("\n")
        for guid in (b.ORIGINAL_GUID, b.TWIN_GUID):
            start, end = b.find_contract_block(out, guid)
            block = [line.strip() for line in out[start:end + 1]]
            self.assertIn("type = ExplorationContract", block)
            self.assertIn("state = Offered", block)
            self.assertIn("targetType = REACHSPACE", block)
            self.assertIn("targetBody = 1", block)
            self.assertEqual(1, sum(1 for line in block if line == "name = ProgressTrackingParameter"))

    def test_the_lane_names_the_fixture_guids_and_goal(self):
        b = _load_builder()
        spec = _read(SPEC)
        self.assertIn('saveTemplate       = "fixtures/saves/career-new-goal-twin"', spec)
        for literal in (b.ORIGINAL_GUID, b.TWIN_GUID, b.GOAL_KEY.replace("@", "@")):
            self.assertIn(literal, spec)


if __name__ == "__main__":
    unittest.main()
