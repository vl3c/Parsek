"""logContracts.forwardedSeeds: the optimizer's forwarded permanent seeds judged entry
by entry (owner ruling 2026-10-15, todo TIER-2026-10-12-LANE-VARIANCE).

The fixture lines below reproduce RF-9's two measured splits: the 19 seeds of the
promoted focus split on the green 2026-10-13_0305 flight, the same 19 plus the knocked-off
R8winglet's Destroyed + Decoupled pair on the 2026-10-12_2119 flight, and the probe
split's one seed. The pids are the measured ones.
"""
import os
import sys
import tomllib
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
for _p in (HERE, HARNESS_ROOT):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import mutlib  # noqa: E402
import run as harness_run  # noqa: E402

RF9_SPEC = os.path.join(HARNESS_ROOT, "scenarios", "RF-9-atmosphere-exit-split-stays-open.toml")


def load_rf9():
    with open(RF9_SPEC, "rb") as fh:
        return tomllib.load(fh)


def validate(spec):
    """The real admission path (registry, bug ids, mission schemas), as run.py runs it."""
    mission_schemas, _ = harness_run.resolve_mission_schemas(spec)
    return hlib.validate_spec(spec, harness_run.load_registry(), harness_run._load_bug_ids(), mission_schemas)

FOCUS_19 = [
    ("Decoupled", "Decoupler.2", 2923926328),
    ("Decoupled", "launchClamp1", 1696482355),
    ("Decoupled", "launchClamp1", 2448645546),
    ("Decoupled", "launchClamp1", 4218587858),
    ("Decoupled", "radialDecoupler1-2", 104415233),
    ("Decoupled", "radialDecoupler1-2", 1837294693),
    ("Decoupled", "radialDecoupler1-2", 2520489354),
    ("Decoupled", "radialDecoupler1-2", 3089190462),
    ("Decoupled", "radialDecoupler1-2", 3467718280),
    ("Decoupled", "radialDecoupler1-2", 3560278526),
    ("ShroudJettisoned", "HeatShield2", 2104924005),
    ("ShroudJettisoned", "liquidEngine2", 48626980),
    ("ShroudJettisoned", "liquidEngine2", 2502517217),
    ("ShroudJettisoned", "liquidEngine2", 2909057254),
    ("ShroudJettisoned", "liquidEngine2", 3105750609),
    ("ShroudJettisoned", "liquidEngine2", 3684832787),
    ("ShroudJettisoned", "liquidEngine2", 3703308415),
    ("ShroudJettisoned", "liquidEngine2-2.v2", 1592830016),
    ("ShroudJettisoned", "liquidEngineMainsail.v2", 235925713),
]
PROBE_1 = [("ShroudJettisoned", "liquidEngineMainsail.v2", 235925713)]
WINGLET_A = [("Decoupled", "R8winglet", 2908259249), ("Destroyed", "R8winglet", 2908259249)]
WINGLET_B = [("Decoupled", "R8winglet", 111), ("Destroyed", "R8winglet", 111)]

FOCUS_EXPECTED = ["%s:%s" % (t, p) for t, p, _ in FOCUS_19]
WINGLET_GROUP = {"part": "R8winglet", "types": ["Decoupled", "Destroyed"]}


def seed_line(entries, ut="173.8"):
    """A forwarded-seed line exactly as the C# writes it (canonical order)."""
    body = ", ".join("%s:%s#%d" % e for e in sorted(entries))
    return ("[LOG 06:08:41.335] [Parsek][INFO][Optimizer] Forwarded %d permanent state "
            "event(s) as seeds at UT=%s: [%s]" % (len(entries), ut, body))


def focus_item(groups=True):
    item = {"expected": list(FOCUS_EXPECTED)}
    if groups:
        item["acceptableExtraGroups"] = [dict(WINGLET_GROUP)]
    return item


def judge(items, *lines):
    return hlib.evaluate_forwarded_seeds(items, "\n".join(lines) + "\n")


class ParseTests(unittest.TestCase):

    def test_parses_entries_count_and_ut(self):
        lines = hlib.parse_forwarded_seed_lines("noise\n" + seed_line(PROBE_1, "173.9") + "\n")
        self.assertEqual(len(lines), 1)
        ln = lines[0]
        self.assertEqual((ln.line_no, ln.count, ln.ut, ln.problem), (2, 1, "173.9", ""))
        self.assertEqual(ln.entries, (("ShroudJettisoned", "liquidEngineMainsail.v2", 235925713),))

    def test_count_only_line_is_a_problem_candidate(self):
        text = ("[Parsek][INFO][Optimizer] Forwarded 19 permanent state event(s) as seeds "
                "at UT=173.8\n")
        ln = hlib.parse_forwarded_seed_lines(text)[0]
        self.assertIn("count-only", ln.problem)
        self.assertEqual(ln.entries, ())

    def test_count_disagreeing_with_list_is_a_problem(self):
        text = seed_line(PROBE_1).replace("Forwarded 1 ", "Forwarded 2 ")
        self.assertIn("count 2 != 1", hlib.parse_forwarded_seed_lines(text)[0].problem)

    def test_null_part_name_entry_parses(self):
        text = ("Forwarded 1 permanent state event(s) as seeds at UT=5.0: "
                "[ParachuteDestroyed:?#5]\n")
        self.assertEqual(hlib.parse_forwarded_seed_lines(text)[0].entries,
                         (("ParachuteDestroyed", "?", 5),))


class EvaluateTests(unittest.TestCase):

    def test_accepts_exact_expected(self):
        self.assertEqual(judge([focus_item()], seed_line(FOCUS_19)), [])

    def test_accepts_exact_expected_with_no_groups_declared(self):
        self.assertEqual(judge([focus_item(groups=False)], seed_line(FOCUS_19)), [])

    def test_accepts_expected_plus_one_winglet_group(self):
        # The 2026-10-12_2119 shape: 21 = 19 + the knocked-off winglet's pair.
        self.assertEqual(judge([focus_item()], seed_line(FOCUS_19 + WINGLET_A)), [])

    def test_accepts_two_winglet_groups_on_different_pids(self):
        self.assertEqual(judge([focus_item()], seed_line(FOCUS_19 + WINGLET_A + WINGLET_B)), [])

    def test_rejects_winglet_pair_when_no_group_declared(self):
        m = judge([focus_item(groups=False)], seed_line(FOCUS_19 + WINGLET_A))
        self.assertEqual(len(m), 1)
        self.assertIn("Destroyed:R8winglet#2908259249", m[0])

    def test_rejects_missing_entry(self):
        short = [e for e in FOCUS_19 if e[1] != "Decoupler.2"]
        m = judge([focus_item()], seed_line(short))
        self.assertEqual(len(m), 1)
        self.assertTrue(m[0].startswith(hlib.EXPECTATION_FORWARDED_SEEDS_MISS_PREFIX))
        self.assertIn("missing ['Decoupled:Decoupler.2']", m[0])
        self.assertIn("unacceptable extra []", m[0])

    def test_rejects_unknown_extra(self):
        m = judge([focus_item()], seed_line(FOCUS_19 + [("FairingJettisoned", "fairingSize1", 9)]))
        self.assertEqual(len(m), 1)
        self.assertIn("unacceptable extra ['FairingJettisoned:fairingSize1#9']", m[0])

    def test_rejects_surplus_copy_of_an_expected_entry(self):
        m = judge([focus_item()], seed_line(FOCUS_19 + [("Decoupled", "launchClamp1", 7)]))
        self.assertEqual(len(m), 1)
        self.assertIn("Decoupled:launchClamp1#", m[0])

    def test_rejects_lone_destroyed_without_its_decoupled(self):
        m = judge([focus_item()], seed_line(FOCUS_19 + [("Destroyed", "R8winglet", 2908259249)]))
        self.assertEqual(len(m), 1)
        self.assertIn("unacceptable extra ['Destroyed:R8winglet#2908259249']", m[0])

    def test_rejects_group_split_across_two_pids(self):
        split = [("Decoupled", "R8winglet", 1), ("Destroyed", "R8winglet", 2)]
        m = judge([focus_item()], seed_line(FOCUS_19 + split))
        self.assertEqual(len(m), 1)
        self.assertIn("Decoupled:R8winglet#1", m[0])
        self.assertIn("Destroyed:R8winglet#2", m[0])

    def test_rejects_group_types_on_the_wrong_part(self):
        wrong = [("Decoupled", "R8wingletX", 5), ("Destroyed", "R8wingletX", 5)]
        self.assertEqual(len(judge([focus_item()], seed_line(FOCUS_19 + wrong))), 1)

    def test_two_items_each_satisfied_by_a_different_line(self):
        items = [focus_item(), {"expected": ["ShroudJettisoned:liquidEngineMainsail.v2"]}]
        self.assertEqual(judge(items, seed_line(FOCUS_19), seed_line(PROBE_1, "173.9")), [])

    def test_probe_item_is_not_satisfied_by_the_focus_line(self):
        # The focus line CONTAINS the probe's entry, but its 18 other seeds are extras.
        m = judge([{"expected": ["ShroudJettisoned:liquidEngineMainsail.v2"]}], seed_line(FOCUS_19))
        self.assertEqual(len(m), 1)

    def test_names_the_closest_line(self):
        short = [e for e in FOCUS_19 if e[1] != "HeatShield2"]
        m = judge([focus_item()], seed_line(PROBE_1, "173.9"), seed_line(short))
        self.assertEqual(len(m), 1)
        self.assertIn("none of 2 seed line(s); closest is log line 2", m[0])
        self.assertIn("missing ['ShroudJettisoned:HeatShield2']", m[0])

    def test_no_seed_line_at_all_fails(self):
        m = judge([focus_item()], "nothing here")
        self.assertEqual(len(m), 1)
        self.assertIn("no `Forwarded N", m[0])

    def test_count_only_line_cannot_satisfy(self):
        m = judge([focus_item()], "Forwarded 19 permanent state event(s) as seeds at UT=173.8")
        self.assertEqual(len(m), 1)
        self.assertIn("count-only", m[0])

    def test_wired_into_evaluate_expectations(self):
        exp = {"logContracts": {hlib.FORWARDED_SEEDS_KEY: [focus_item()]}}
        self.assertEqual(hlib.evaluate_expectations(exp, None, seed_line(FOCUS_19)).status, "PASS")
        bad = hlib.evaluate_expectations(exp, None, seed_line(FOCUS_19[1:]))
        self.assertEqual(bad.status, "FAIL")
        self.assertTrue(bad.mismatches[0].startswith(hlib.EXPECTATION_FORWARDED_SEEDS_MISS_PREFIX))


class ValidateTests(unittest.TestCase):

    def v(self, value):
        return hlib.validate_forwarded_seeds_spec(value)

    def test_valid_items_pass(self):
        self.assertEqual(self.v([focus_item(), {"expected": ["Decoupled:a"]}]), [])

    def test_malformed_specs_are_rejected(self):
        bad = [
            [],
            "Decoupled:a",
            ["Decoupled:a"],
            [{}],
            [{"expected": []}],
            [{"expected": ["Decoupled"]}],
            [{"expected": ["Decoupld:a"]}],
            [{"expected": ["Decoupled:a#5"]}],
            [{"expected": ["Decoupled:a b"]}],
            [{"expected": [5]}],
            [{"expected": ["Decoupled:a"], "extra": 1}],
            [{"expected": ["Decoupled:a"], "acceptableExtraGroups": {"part": "w"}}],
            [{"expected": ["Decoupled:a"], "acceptableExtraGroups": [{"part": "w"}]}],
            [{"expected": ["Decoupled:a"], "acceptableExtraGroups": [{"part": "w", "types": []}]}],
            [{"expected": ["Decoupled:a"],
              "acceptableExtraGroups": [{"part": "w", "types": ["Exploded"]}]}],
            [{"expected": ["Decoupled:a"],
              "acceptableExtraGroups": [{"part": "w", "types": ["Destroyed", "Destroyed"]}]}],
            [{"expected": ["Decoupled:a"],
              "acceptableExtraGroups": [{"part": "w", "types": ["Destroyed"], "pid": 1}]}],
            # A group part also named by expected would make the extra ambiguous.
            [{"expected": ["Decoupled:a"],
              "acceptableExtraGroups": [{"part": "a", "types": ["Destroyed"]}]}],
            [{"expected": ["Decoupled:a"],
              "acceptableExtraGroups": [{"part": "w", "types": ["Destroyed", "Decoupled"]},
                                        {"part": "w", "types": ["Decoupled", "Destroyed"]}]}],
        ]
        for value in bad:
            with self.subTest(value=value):
                self.assertNotEqual(self.v(value), [])

    def test_validate_spec_routes_the_facet(self):
        spec = load_rf9()
        spec["expectations"]["logContracts"][hlib.FORWARDED_SEEDS_KEY] = [{"expected": []}]
        errors = list(validate(spec).errors)
        self.assertTrue(any("forwardedSeeds" in e for e in errors), errors)


class Rf9SpecTests(unittest.TestCase):
    """RF-9 carries the facet, judged against both measured flight shapes."""

    def setUp(self):
        self.spec = load_rf9()
        self.lc = self.spec["expectations"]["logContracts"]

    def test_spec_validates(self):
        self.assertEqual(list(validate(self.spec).errors), [])

    def test_no_count_token_remains(self):
        self.assertFalse(any("permanent state event" in p for p in self.lc["required"]))

    def test_green_flight_shape_passes(self):
        self.assertEqual(hlib.evaluate_forwarded_seeds(
            self.lc[hlib.FORWARDED_SEEDS_KEY],
            seed_line(FOCUS_19) + "\n" + seed_line(PROBE_1, "173.9") + "\n"), [])

    def test_collision_flight_shape_passes(self):
        self.assertEqual(hlib.evaluate_forwarded_seeds(
            self.lc[hlib.FORWARDED_SEEDS_KEY],
            seed_line(FOCUS_19 + WINGLET_A, "173.6") + "\n"
            + seed_line(PROBE_1, "173.7") + "\n"), [])

    def test_lone_winglet_half_fails(self):
        m = hlib.evaluate_forwarded_seeds(
            self.lc[hlib.FORWARDED_SEEDS_KEY],
            seed_line(FOCUS_19 + WINGLET_A[1:]) + "\n" + seed_line(PROBE_1, "173.9") + "\n")
        self.assertEqual(len(m), 1)

    def test_count_only_log_fails(self):
        text = ("Forwarded 19 permanent state event(s) as seeds at UT=173.8\n"
                "Forwarded 1 permanent state event(s) as seeds at UT=173.9\n")
        self.assertEqual(len(hlib.evaluate_forwarded_seeds(self.lc[hlib.FORWARDED_SEEDS_KEY],
                                                           text)), 2)


class MutationLaneTests(unittest.TestCase):
    """The mutation lane's forwardedSeeds edits are KILLED on a green log."""

    def test_every_seed_edit_is_killed(self):
        exp = {"logContracts": {hlib.FORWARDED_SEEDS_KEY: [focus_item()]}}
        text = "a\n" + seed_line(FOCUS_19 + WINGLET_A) + "\nb\n"
        muts = mutlib.mutate_forwarded_seeds(exp, text)
        self.assertEqual({m.kind for m in muts},
                         {"seed-delete-line", "seed-drop-entry", "seed-inject-extra",
                          "seed-split-group"})
        for m in muts:
            with self.subTest(kind=m.kind):
                self.assertEqual(m.outcome, mutlib.KILLED, m)

    def test_lane_evaluator_sees_a_deleted_seed_line(self):
        exp = {"logContracts": {"required": ["BEGIN"],
                                hlib.FORWARDED_SEEDS_KEY: [focus_item()]}}
        text = "BEGIN\n" + seed_line(FOCUS_19) + "\n"
        model = mutlib.build_log_model(text)
        ev = mutlib.LaneEvaluator(exp, None, model, {})
        self.assertEqual(ev.outcome(()), mutlib.SURVIVED)
        self.assertEqual(ev.outcome((1,)), mutlib.KILLED)
        self.assertEqual(ev.confirm((1,)), mutlib.KILLED)


if __name__ == "__main__":
    unittest.main()
