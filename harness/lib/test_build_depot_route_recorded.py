"""Fixture gates for `depot-route-recorded`, the suite's FIRST ROUTE subject.

WHAT THIS FILE GUARDS, AND WHY IT CANNOT BE LEFT TO `test_saveparse.py`.
`RECORDED_FIXTURES` pins the shape every recorded fixture shares - trees,
recordings, terminal states, sidecar floor, schema generation. It pins NOTHING
about a `ROUTE`, because `harness/lib/saveparse.py` has no `routes` facet yet (a
todo improvement is filed). The one surface this fixture exists for would
therefore be unguarded everywhere. These cells wire
`harness/tools/build_depot_route_recorded.py --check` into the suite so a
hand-edit of the committed bytes - or a future re-harvest that quietly produces a
different route - reds here rather than on the route-render lane's next flight.

IT CANNOT RE-RUN THE BUILD, for the same reason
`DunaOneRecordedFixtureDriftTests` cannot: the input is an operator save
(`Kerbal Space Program/saves/orbital supply route DELIVERY test`) outside the
repo that will never be committed. The claims are made against the RESULT
instead, and they are the ones that matter for a route:

  * the ROUTE's own fields, its four SOURCE rows, and the STOP endpoint's
    resolution to a live `Depot` VESSEL node - the nine fields
    `RouteStore.RevalidateSources` compares against a live rebuild, plus the
    endpoint the D3 keep-list exists to protect;
  * the dock member's RECORDING node, hashed, because `routeProofHash
    5432980487a27600` is computed over its ROUTE_CONNECTION_WINDOWS;
  * the active-vessel re-point, re-resolved by name and pid rather than trusted
    as an index;
  * the INV2 repair as a fixed point.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "depot-route-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_depot_route_recorded.py")
    spec = importlib.util.spec_from_file_location(
        "build_depot_route_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DepotRouteRecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_depot_route_recorded.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = cls.builder.read_lines(FIXTURE_SFS)

    def test_the_committed_save_satisfies_every_post_condition(self):
        problems = self.builder.verify_save(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_committed_file_tree_satisfies_every_post_condition(self):
        problems = self.builder.verify_tree(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_route_pin_holds(self):
        """Run on its own as well as inside verify_save, so a route regression
        names the ROUTE rather than arriving inside a fifty-line save diff."""
        problems = self.builder.verify_route(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_inv2_repair_is_a_fixed_point(self):
        problems = self.builder.verify_prec(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_repaired_recording_carries_the_documented_section_count(self):
        """50 sections in, 48 out - the two drops are enumerated in the tool.

        Pinned as a NUMBER as well as through the fixed-point cell above, because
        the two fail differently: a re-harvest that produced a recording with a
        different section count would still be a fixed point (nothing to dedupe)
        and would slip past every other cell here."""
        prec = os.path.join(FIXTURE_DIR, "Parsek", "Recordings",
                            self.builder.INV2_REPAIR_RECORDING_ID + ".prec")
        with open(prec, "rb") as fh:
            _count_offset, sections = self.builder.read_prec_sections(fh.read())
        self.assertEqual(self.builder.INV2_EXPECTED_SECTIONS_AFTER, len(sections))
        self.assertEqual(
            self.builder.INV2_EXPECTED_SECTIONS_BEFORE
            - len(self.builder.INV2_DROPPED_SECTION_INDICES),
            self.builder.INV2_EXPECTED_SECTIONS_AFTER,
            "the before/after/drop-list constants disagree with each other")

    def test_the_repair_never_touches_a_route_source(self):
        """THE PROPERTY THAT MAKES A REPAIR SAFE IN A ROUTE FIXTURE.

        `RouteStore.RevalidateSources` compares nine SOURCE fields against a live
        rebuild and flips the route to `SourceChanged` on any drift, which never
        auto-recovers. The repaired recording must therefore not be one the
        route's SOURCE_REFS cover - and the four that ARE covered must still be
        exactly the four the ROUTE names."""
        self.assertNotIn(self.builder.INV2_REPAIR_RECORDING_ID,
                         self.builder.ROUTE_RECORDING_IDS)
        source_ids = tuple(row[0] for row in self.builder.ROUTE_SOURCE_ROWS)
        self.assertEqual(self.builder.ROUTE_RECORDING_IDS, source_ids,
                         "the ROUTE's RECORDING_IDS and its SOURCE rows name "
                         "different recordings")

    def test_the_two_trees_are_both_kept_and_both_related_to_the_route(self):
        """DECISION 4, made mechanical. The sibling tree was kept because it is
        NOT independent: its chain recordings carry the same
        `vesselPersistentId` / `recordedVesselGuid` as the `Depot` the ROUTE's
        STOP endpoint names, i.e. it is that vessel's own launch lineage. If a
        re-harvest ever broke that link the keep-both decision would need
        re-taking, so the link is asserted rather than remembered."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        trees = {b.get_value(self.lines, t, "id"): t
                 for t in b.child_nodes(self.lines, scn, "RECORDING_TREE")}
        self.assertEqual(sorted(trees),
                         sorted([b.DEPOT_LINEAGE_TREE_ID, b.BACKING_TREE_ID]))

        def guids_for(tree_id, pid):
            recs = b.child_nodes(self.lines, trees[tree_id], "RECORDING")
            return {b.get_value(self.lines, r, "recordedVesselGuid")
                    for r in recs
                    if b.get_value(self.lines, r, "vesselPersistentId") == pid}

        depot_pid = b.ROUTE_STOP_ENDPOINT_PID
        lineage = guids_for(b.DEPOT_LINEAGE_TREE_ID, depot_pid)
        backing = guids_for(b.BACKING_TREE_ID, depot_pid)
        self.assertTrue(lineage, "the sibling tree carries no recording of the "
                                 "Depot's persistentId %s" % depot_pid)
        self.assertTrue(backing, "the backing tree carries no recording of the "
                                 "Depot's persistentId %s" % depot_pid)
        self.assertEqual(
            lineage, backing,
            "the two trees' Depot recordings no longer share a launch guid: the "
            "sibling tree may now be independent, and decision 4 (keep both) "
            "must be re-taken")

    def test_the_dock_member_node_is_byte_exact(self):
        """D4 as its own cell. `verify_route` folds this into the route pin;
        stated separately because it is the ONE thing whose failure means a
        route-killing `SourceChanged` flip rather than a cosmetic drift."""
        digest = self.builder.dock_member_node_digest(self.lines)
        self.assertEqual(self.builder.DOCK_MEMBER_NODE_SHA256, digest)


class DepotRouteActiveVesselTests(unittest.TestCase):
    """The re-point (decision 5), checked from both ends.

    The harvest's own focusability gate PASSES on the source save's asteroid,
    which is exactly why this needs a cell of its own: nothing upstream would
    have caught a fixture that boots focused on `Ast. YRJ-552`."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = cls.builder.read_lines(FIXTURE_SFS)

    def test_the_active_vessel_is_the_depot(self):
        b = self.builder
        records = b.vessel_records(self.lines)
        fs = b.flightstate_node(self.lines)
        index = int(b.get_value(self.lines, fs, "activeVessel"))
        self.assertEqual(b.ACTIVE_VESSEL_INDEX, index)
        self.assertEqual(b.ACTIVE_VESSEL_NAME, records[index]["name"])
        self.assertEqual(b.ACTIVE_VESSEL_PID, records[index]["pid"])
        self.assertEqual(b.ACTIVE_VESSEL_SITUATION, records[index]["sit"])

    def test_the_source_saves_asteroid_is_no_longer_focused(self):
        b = self.builder
        records = b.vessel_records(self.lines)
        fs = b.flightstate_node(self.lines)
        index = int(b.get_value(self.lines, fs, "activeVessel"))
        self.assertNotEqual(b.SOURCE_ACTIVE_VESSEL_NAME, records[index]["name"])

    def test_both_route_vessels_survive(self):
        """D3. The STOP endpoint's vessel and the delivery vessel must be alive
        in FLIGHTSTATE; a route whose endpoint resolves nothing is the failure
        the keep-list exists to prevent, and it is invisible in the ROUTE node."""
        b = self.builder
        by_pid = {r["pid"]: r for r in b.vessel_records(self.lines)}
        for name, pid, vtype, sit in b.REQUIRED_VESSELS:
            self.assertIn(pid, by_pid, name)
            self.assertEqual((name, vtype, sit),
                             (by_pid[pid]["name"], by_pid[pid]["type"],
                              by_pid[pid]["sit"]))


class DepotRouteRepeatedValueReaderTests(unittest.TestCase):
    """The one helper this recipe adds, exercised on synthetic shapes.

    `get_value` returns only the FIRST match, and `RECORDING_IDS` /
    `EXCLUDED_INTERVALS` are repeated-key nodes - reading them with `get_value`
    would silently pin one entry out of four and every other cell here would
    still pass."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_it_returns_every_direct_value_in_file_order(self):
        lines = ["RECORDING_IDS", "{", "\tid = a", "\tid = b", "\tid = c", "}"]
        self.assertEqual(["a", "b", "c"],
                         self.builder._repeated_values(lines, (0, 6), "id"))

    def test_it_ignores_the_same_key_inside_a_nested_node(self):
        lines = ["N", "{", "\tid = a", "\tCHILD", "\t{", "\t\tid = nope", "\t}",
                 "\tid = b", "}"]
        self.assertEqual(["a", "b"],
                         self.builder._repeated_values(lines, (0, 9), "id"))

    def test_it_returns_empty_for_an_absent_key(self):
        lines = ["N", "{", "\tother = a", "}"]
        self.assertEqual([], self.builder._repeated_values(lines, (0, 4), "id"))


class DepotRouteStemTests(unittest.TestCase):
    """`_stem` must strip `.txt` FIRST.

    Without it `X_ghost.craft.txt` falls through to `name.split('.')[0]` and
    reads as a family called `X_ghost` - which is exactly how this save's 22
    recordings once read as 64 families and produced a phantom orphan sweep in
    the harvest plan. The harvest prunes those mirrors, so the bug is invisible
    against a finished fixture; these cells state the rule directly."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_authoritative_suffixes(self):
        stem = self.builder._stem
        self.assertEqual("abc", stem("abc.prec"))
        self.assertEqual("abc", stem("abc.pann"))
        self.assertEqual("abc", stem("abc_vessel.craft"))
        self.assertEqual("abc", stem("abc_ghost.craft"))

    def test_the_mirrors_resolve_to_the_same_family(self):
        stem = self.builder._stem
        self.assertEqual("abc", stem("abc.prec.txt"))
        self.assertEqual("abc", stem("abc_vessel.craft.txt"))
        self.assertEqual("abc", stem("abc_ghost.craft.txt"))


if __name__ == "__main__":
    unittest.main()
