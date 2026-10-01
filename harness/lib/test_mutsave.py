"""Unit tests for mutsave (the save-perturbation mutation checker, phase 2 PR 1).

Synthetic saves only (the test_saveparse samples plus a FLIGHTSTATE grafted on),
so nothing here depends on an archive being present on the machine. The
vacuity cells break one saveparse path by monkeypatching it and assert the
checker names the window VACUOUS; each restores the patched attribute.
"""

import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mutlib  # noqa: E402
import mutsave  # noqa: E402
import saveparse  # noqa: E402
from test_saveparse import B9_MERGED_SFS, ONE_ROUTE_SFS, THREE_TREE_SFS  # noqa: E402

_VESSELS = (
    "\t\tVESSEL\n\t\t{\n\t\t\tname = Spawned One\n\t\t\ttype = Ship\n"
    "\t\t\tpersistentId = 4242\n"
    "\t\t\tPART\n\t\t\t{\n\t\t\t\tname = mk1pod.v2\n\t\t\t}\n\t\t}\n"
    "\t\tVESSEL\n\t\t{\n\t\t\tname = Ast. ABC-123\n\t\t\ttype = SpaceObject\n"
    "\t\t\tpersistentId = 77\n\t\t}\n")
_FS_UT = "\t\tUT = 21.159999999999638\n"
_FS_TAIL = _FS_UT + "\t}\n}\n"


def _with_flightstate(text):
    """B9 plus two FLIGHTSTATE vessels (one spawned, one asteroid) and a
    spawnedPid on its first recording, so the vessel facets have something to
    count."""
    assert text.endswith(_FS_TAIL)
    text = text.replace("recordingId = ", "spawnedPid = 4242\n\t\t\t\trecordingId = ", 1)
    return text[:-len(_FS_TAIL)] + _FS_UT + _VESSELS + "\t}\n}\n"


SAVE = _with_flightstate(B9_MERGED_SFS)


def _facets(text):
    return saveparse.observed_structure_facets(saveparse.parse_parsek_scenario(text))


def _pinned_expectations(text):
    """Every count window pinned EXACTLY at the measured value, all four blocks
    armed: the strictest spec a save can carry."""
    f = _facets(text)
    st = f["recordings"]["structure"]
    pts = f["recordings"]["points"]
    rt = f["routes"]
    exp = {
        "rewind": dict({"gating": True}, **{k: f["rewind"][k] for k in
                                             ("supersedeRows", "tombstones", "rewindPoints")}),
        "recordings": {
            "structure": dict({"gating": True},
                              **{k: st[k] for k in saveparse.STRUCTURE_SCALAR_KEYS},
                              terminalStates=dict(st["terminalStates"]),
                              branchPoints=dict(st["branchPoints"]),
                              vesselNames=dict(st["vesselNames"])),
            "points": dict({"gating": True},
                           **{k: pts[k] for k in saveparse.POINTS_ASSERTION_KEYS}),
        },
    }
    if rt["count"]:
        exp["routes"] = dict({"gating": True}, **{k: rt[k] for k in saveparse.ROUTES_COUNT_KEYS},
                             statuses=dict(rt["statuses"]),
                             connectionKinds=dict(rt["connectionKinds"]),
                             originBodies=dict(rt["originBodies"]),
                             destinationBodies=dict(rt["destinationBodies"]),
                             ids=list(rt["ids"]),
                             destinationVesselPids=list(rt["destinationVesselPids"]))
    return exp


def _run(exp, text=SAVE):
    lane = mutlib.check_save_lane({"id": "T", "expectations": exp}, "test", text)
    return lane, {g.label: g for g in lane.save_gates}


class Patched:
    """Swap one module attribute for the duration of a with-block."""

    def __init__(self, module, name, value):
        self.module, self.name, self.value = module, name, value

    def __enter__(self):
        self.saved = getattr(self.module, self.name)
        setattr(self.module, self.name, self.value)

    def __exit__(self, *exc):
        setattr(self.module, self.name, self.saved)


class WriterTests(unittest.TestCase):

    def test_write_sfs_round_trips_every_sample(self):
        for text in (SAVE, B9_MERGED_SFS, THREE_TREE_SFS, ONE_ROUTE_SFS):
            root = saveparse.parse_sfs(text).root
            again = mutsave.write_sfs(root)
            self.assertTrue(saveparse.parse_sfs(again).ok)
            self.assertEqual(_facets(text), _facets(again))

    def test_reduction_drops_parts_and_keeps_the_facets(self):
        root = saveparse.parse_sfs(SAVE).root
        reduced = mutsave.reduce_save(root)
        text = mutsave.write_sfs(reduced)
        self.assertNotIn("mk1pod", text)
        self.assertEqual(_facets(SAVE), _facets(text))
        base = mutsave.prepare_baseline(_pinned_expectations(SAVE), SAVE)
        self.assertTrue(base.reduced)
        self.assertEqual("", base.note)

    def test_edits_never_touch_the_baseline_tree(self):
        exp = _pinned_expectations(SAVE)
        base = mutsave.prepare_baseline(exp, SAVE)
        before = mutsave.write_sfs(base.root)
        mutsave.mutate_save_structure(exp, base.root)
        self.assertEqual(before, mutsave.write_sfs(base.root))


class CrossingTargetTests(unittest.TestCase):

    def test_targets_sit_just_past_each_bound(self):
        self.assertEqual([("down", 3, "min 4"), ("up", 5, "max 4")],
                         mutsave.crossing_targets(4, 4))
        self.assertEqual([("down", 1, "min 2")], mutsave.crossing_targets({"min": 2}, 9))
        self.assertEqual([("up", 1, "max 0")], mutsave.crossing_targets({"max": 0}, 0))

    def test_untestable_bounds_carry_no_target(self):
        down = mutsave.crossing_targets({"min": 0}, 3)
        self.assertEqual([("down", None, "min 0 cannot be undercut")], down)
        far = mutsave.crossing_targets({"max": 10000}, 2)
        self.assertIsNone(far[0][1])
        self.assertIn("more than", far[0][2])


class PerturbationKillTests(unittest.TestCase):
    """Positive cells: on a healthy parser every armed window of every family is
    PROVEN, through BOTH directions of an exact pin."""

    def _assert_all_proven(self, text):
        exp = _pinned_expectations(text)
        lane, gates = _run(exp, text)
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline, lane.baseline_reasons)
        self.assertTrue(gates)
        for label, g in gates.items():
            with self.subTest(label=label):
                self.assertEqual(mutsave.PROVEN, g.verdict, g.reason)
        for m in lane.mutations:
            with self.subTest(target=m.target, kind=m.kind):
                self.assertEqual(mutlib.KILLED, m.outcome, m.note)
        return lane, gates

    def test_rewind_structure_and_points_windows_are_proven(self):
        lane, gates = self._assert_all_proven(SAVE)
        for label in ("rewind.supersedeRows", "rewind.tombstones", "rewind.rewindPoints",
                      "recordings.structure.trees", "recordings.structure.committedTrees",
                      "recordings.structure.recordings", "recordings.structure.ghostChainNodes",
                      "recordings.structure.spawnedVessels",
                      "recordings.structure.terminalStates.Destroyed",
                      "recordings.structure.branchPoints.Undock",
                      "recordings.structure.vesselNames.Spawned One",
                      "recordings.points.total", "recordings.points.largest",
                      "recordings.points.smallest", "recordings.points.trivialRecordings"):
            self.assertIn(label, gates)
        kinds = {m.kind for m in lane.mutations}
        self.assertEqual({mutsave.KIND_DOWN, mutsave.KIND_UP, mutsave.KIND_FAULT}, kinds)

    def test_route_windows_sets_and_faults_are_proven(self):
        _lane, gates = self._assert_all_proven(ONE_ROUTE_SFS)
        for label in ("routes.count", "routes.dormant", "routes.stops", "routes.sourceRefs",
                      "routes.completedCycles", "routes.skippedCycles",
                      "routes.statuses.Active", "routes.connectionKinds.DockingPort",
                      "routes.originBodies.Kerbin", "routes.destinationBodies.Mun",
                      "routes.ids", "routes.destinationVesselPids",
                      "routes:strip-stops", "routes:drop-completedCycles", "routes:tear",
                      "routes:drop-scenario"):
            self.assertIn(label, gates)

    def test_an_absent_bucket_upper_bound_is_proven_by_synthesis(self):
        exp = {"recordings": {"structure": {
            "gating": True, "terminalStates": {"Landed": {"max": 0}},
            "branchPoints": {"Dock": {"max": 0}}, "vesselNames": {"Nobody": {"max": 0}}}},
            "rewind": {"gating": True, "tombstones": {"max": 1}}}
        _lane, gates = _run(exp)
        for label in ("recordings.structure.terminalStates.Landed",
                      "recordings.structure.branchPoints.Dock",
                      "recordings.structure.vesselNames.Nobody", "rewind.tombstones"):
            self.assertEqual(mutsave.PROVEN, gates[label].verdict, label)

    def test_a_wide_window_is_still_proven_at_its_bound(self):
        exp = {"recordings": {"structure": {"gating": True, "recordings": {"min": 1, "max": 9}}}}
        lane, gates = _run(exp)
        self.assertEqual(mutsave.PROVEN, gates["recordings.structure.recordings"].verdict)
        details = [m.detail for m in lane.mutations if m.kind != mutsave.KIND_FAULT]
        self.assertTrue(any(d.startswith("4 -> 0 (min 1)") for d in details), details)
        self.assertTrue(any(d.startswith("4 -> 10 (max 9)") for d in details), details)

    def test_the_spaceobject_vessel_is_not_a_name_bucket(self):
        f = _facets(SAVE)["recordings"]["structure"]
        self.assertEqual({"Spawned One": 1}, f["vesselNames"])
        self.assertEqual(1, f["spawnedVessels"])


class VacuityTests(unittest.TestCase):
    """Negative cells: break one parser / evaluator path and the checker must
    name the window VACUOUS (and list the survivor for triage)."""

    def test_a_dead_ghost_chain_count_is_vacuous_on_its_max_zero_tripwire(self):
        exp = {"recordings": {"structure": {"gating": True, "ghostChainNodes": 0}}}
        with Patched(saveparse, "_count_ghost_chain_nodes", lambda _sc: 0):
            lane, gates = _run(exp)
        g = gates["recordings.structure.ghostChainNodes"]
        self.assertEqual(mutsave.VACUOUS, g.verdict)
        self.assertEqual("recordings.structure", g.block)
        self.assertIn("dead path", g.reason)
        survivors = [m for m in lane.mutations if m.outcome == mutlib.SURVIVED]
        self.assertEqual([mutlib.TRIAGE], [m.triage for m in survivors])
        report = mutlib.render_report([lane], [])
        self.assertIn("## Vacuous save-parse gates", report)
        self.assertIn("T (test): block `recordings.structure` "
                      "`recordings.structure.ghostChainNodes =0`", report)

    def test_a_terminal_state_the_parser_stopped_reading_is_vacuous(self):
        real = saveparse._parse_recording

        def no_terminal(node):
            row = real(node)
            return row.__class__(**dict(row.__dict__, terminal_state=None))

        exp = {"recordings": {"structure": {"gating": True,
                                            "terminalStates": {"Landed": {"max": 0}}}}}
        with Patched(saveparse, "_parse_recording", no_terminal):
            _lane, gates = _run(exp)
        g = gates["recordings.structure.terminalStates.Landed"]
        self.assertEqual(mutsave.VACUOUS, g.verdict)
        self.assertIn("dead path", g.reason)

    def test_a_window_the_evaluator_no_longer_checks_is_vacuous(self):
        real = saveparse._check_window

        def skip_trees(label, spec_val, measured, mismatches):
            if label != "recordings.structure.trees":
                real(label, spec_val, measured, mismatches)

        exp = {"recordings": {"structure": {"gating": True, "trees": 1, "recordings": 4}}}
        with Patched(saveparse, "_check_window", skip_trees):
            _lane, gates = _run(exp)
        self.assertEqual(mutsave.VACUOUS, gates["recordings.structure.trees"].verdict)
        self.assertIn("evaluator stayed green", gates["recordings.structure.trees"].reason)
        self.assertEqual(mutsave.PROVEN, gates["recordings.structure.recordings"].verdict)

    def test_a_points_unparsed_rule_that_never_fires_fails_its_fault(self):
        real = saveparse.observed_points_facets

        def never_unparsed(snapshot):
            out = real(snapshot)
            if out:
                out["unparsed"] = 0
            return out

        exp = {"recordings": {"points": {"gating": True, "largest": {"min": 2}}}}
        with Patched(saveparse, "observed_points_facets", never_unparsed):
            _lane, gates = _run(exp)
        self.assertEqual(mutsave.VACUOUS, gates["recordings.points:drop-pointCount"].verdict)
        self.assertEqual(mutsave.PROVEN, gates["recordings.points:tear"].verdict)

    def test_a_set_key_the_evaluator_dropped_is_vacuous(self):
        exp = {"routes": {"gating": True, "count": 1, "ids": ["route-1"]}}
        with Patched(saveparse, "ROUTES_SET_KEYS", ()):
            _lane, gates = _run(exp, ONE_ROUTE_SFS)
        self.assertEqual(mutsave.VACUOUS, gates["routes.ids"].verdict)
        self.assertEqual(mutsave.PROVEN, gates["routes.count"].verdict)


class UncheckedAndScopeTests(unittest.TestCase):

    def test_a_far_upper_bound_and_a_bare_min_zero_are_unchecked(self):
        exp = {"recordings": {"structure": {"gating": True, "recordings": {"max": 100000},
                                            "trees": {"min": 0}}}}
        _lane, gates = _run(exp)
        self.assertEqual(mutsave.UNCHECKED, gates["recordings.structure.recordings"].verdict)
        self.assertEqual(mutsave.UNCHECKED, gates["recordings.structure.trees"].verdict)

    def test_an_unarmed_block_gets_no_gates(self):
        exp = {"rewind": {"supersedeRows": 1},
               "recordings": {"structure": {"gating": True, "trees": 1}}}
        _lane, gates = _run(exp)
        self.assertFalse(any(label.startswith("rewind") for label in gates))
        self.assertIn("recordings.structure.trees", gates)

    def test_a_save_the_armed_blocks_fail_is_not_green(self):
        exp = {"recordings": {"structure": {"gating": True, "trees": 3}}}
        lane, gates = _run(exp)
        self.assertEqual(mutlib.BASELINE_NOT_GREEN, lane.baseline)
        self.assertEqual({}, gates)
        self.assertTrue(any("trees 1 != 3" in r for r in lane.baseline_reasons))
        lane, _ = _run(exp, None)
        self.assertEqual(["no archived save"], lane.baseline_reasons)

    def test_check_lane_runs_save_edits_only_when_it_has_the_save_text(self):
        exp = {"recordings": {"structure": {"gating": True, "trees": 1}}}
        spec = {"id": "T", "expectations": exp}
        snap = saveparse.parse_parsek_scenario(SAVE)
        with_text = mutlib.check_lane(spec, mutlib.ArchiveInputs("a", "", None, snap, SAVE))
        self.assertEqual(mutlib.BASELINE_GREEN, with_text.baseline)
        self.assertTrue(with_text.save_gates)
        without = mutlib.check_lane(spec, mutlib.ArchiveInputs("a", "", None, snap))
        self.assertEqual([], without.save_gates)
        self.assertIn("saveGates(proven=", mutlib.summary_line(with_text))


if __name__ == "__main__":
    unittest.main()
