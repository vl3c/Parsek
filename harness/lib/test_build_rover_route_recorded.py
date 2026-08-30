"""Fixture gates for `rover-route-recorded`, the SUPPLY-ROUTE lane host.

WHAT THIS FILE GUARDS, AND WHY IT CANNOT BE LEFT TO `test_saveparse.py`.
`RECORDED_FIXTURES` pins the shape every recorded fixture shares - trees,
recordings, terminal states, sidecar floor, schema generation. It pins NOTHING
about a `ROUTE_CONNECTION_WINDOWS` node, because `harness/lib/saveparse.py` has
no route-window facet (nor a `routes` one; a todo improvement is filed). The one
surface this fixture exists for would therefore be unguarded everywhere. These
cells wire `harness/tools/build_rover_route_recorded.py --check` into the suite
so a hand-edit of the committed bytes - or a future re-harvest that quietly
produces a different window - reds here rather than on RVR-1's next flight.

IT CANNOT RE-RUN THE BUILD, for the same reason `DepotRouteRecordedFixture-
DriftTests` and `DunaOneRecordedFixtureDriftTests` cannot: the input is a
COLLECTED operator save outside the repo that will never be committed. The claims
are made against the RESULT instead, and three of them are the ones no other cell
in the suite makes:

  * THE WINDOW IS TARGET-BRANCH. Every route window in the committed corpus
    before this one is initiator-branch, which is exactly why
    `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` and
    `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` sit in H39's and
    H40's MEASURED_SKIPPED rosters as a HARVEST requirement. A re-harvest that
    lost the branch would put both cells back to Skip while every structural
    count still read correct.
  * THE CROSS-TREE LINK SURVIVES. The second cell needs the window's target pid
    to be carried by a recording in the OTHER tree; dropping that tree would look
    like tidying and would silently un-pay half the debt.
  * NOTHING CARRIES A `mergeState` KEY. That absence IS
    `RouteCandidateFinder.IsTreeFullySealed`, and RVR-2's
    `SealSlot ... alreadySealed=True` pin is a lie without it.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "rover-route-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
SCENARIOS_DIR = os.path.join(_HARNESS, "scenarios")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_rover_route_recorded.py")
    spec = importlib.util.spec_from_file_location(
        "build_rover_route_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RoverRouteRecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_rover_route_recorded.py --check` INTO THE SUITE."""

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

    def test_the_route_window_pin_holds(self):
        """Run on its own as well as inside verify_save, so a window regression
        names the WINDOW rather than arriving inside a fifty-line save diff."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_route_windows(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_seal_state_pin_holds(self):
        """Likewise for the seal absence, which RVR-2's no-op guard rests on."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_seal_state(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_window_is_target_branch_and_that_is_the_whole_point(self):
        """THE DEBT H39 AND H40 NAME, asserted as the predicate the in-game cell
        evaluates rather than as two constants that happen to differ.

        `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` searches for
        `window.TransferTargetVesselPid != recording.VesselPersistentId` with
        both nonzero. Both other recorded route hosts fail it because their two
        docking craft are Kerbal X descendants sharing one BAKED persistentId."""
        b = self.builder
        self.assertNotEqual(b.ROUTE_WINDOW_TARGET_PID, b.DOCK_MEMBER_VESSEL_PID,
                            "the window would be initiator-branch")
        self.assertNotEqual("0", b.ROUTE_WINDOW_TARGET_PID)
        self.assertNotEqual("0", b.DOCK_MEMBER_VESSEL_PID)
        # And from the bytes, not from the constants.
        scn = b.parsek_scenario(self.lines)
        target = None
        carrier = None
        for tree in b.child_nodes(self.lines, scn, "RECORDING_TREE"):
            for rec in b.child_nodes(self.lines, tree, "RECORDING"):
                for holder in b.child_nodes(self.lines, rec,
                                            "ROUTE_CONNECTION_WINDOWS"):
                    for w in b.child_nodes(self.lines, holder, "WINDOW"):
                        target = b.get_value(self.lines, w, "transferTargetPid")
                        carrier = b.get_value(self.lines, rec,
                                              "vesselPersistentId")
        self.assertIsNotNone(target, "the fixture carries no route window")
        self.assertNotEqual(target, carrier)

    def test_the_cross_tree_partner_recording_is_kept(self):
        """THE SECOND DEBT. `RouteProof_CrossTreeCommittedPartner_HasEndpointProof`
        adds one conjunct to the cell above: the target pid must ALSO be carried
        by a recording in `RecordingStore.CommittedRecordings`. Here that is the
        OTHER tree's single recording, so keeping tree A whole is load-bearing
        and not tidiness."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        carriers = {}
        for tree in b.child_nodes(self.lines, scn, "RECORDING_TREE"):
            tree_id = b.get_value(self.lines, tree, "id")
            for rec in b.child_nodes(self.lines, tree, "RECORDING"):
                carriers.setdefault(
                    b.get_value(self.lines, rec, "vesselPersistentId"), []
                ).append((tree_id, b.get_value(self.lines, rec, "recordingId")))
        hits = carriers.get(b.ROUTE_WINDOW_TARGET_PID, [])
        self.assertEqual(
            [(b.ENDPOINT_TREE_ID, b.ENDPOINT_TREE_RECORDING_IDS[0])], hits,
            "the window's target pid must be carried by exactly the endpoint "
            "tree's own recording")

    def test_the_endpoint_headroom_makes_rvr2s_two_cycle_chain_derivable(self):
        """RVR-2's causal chain is ARITHMETIC over these bytes, not a guess.

        The window measures a 97.6 LiquidFuel transfer (transport 200 -> 102.4,
        endpoint 200 -> 297.6), and the live endpoint vessel holds 297.6 / 400.
        So cycle 1 fits in the 102.4 of headroom and cycle 2 is short by 92.8,
        which is the `BLOCKED kind=DestinationFull` the spec pins. If a
        re-harvest moved either number the spec's expectation moves with it, so
        the two are checked against each other HERE rather than in prose."""
        b = self.builder
        dock_amount = float(b.ROUTE_WINDOW_RESOURCE_ROWS["DOCK_ENDPOINT_RESOURCES"][1])
        undock_amount = float(
            b.ROUTE_WINDOW_RESOURCE_ROWS["UNDOCK_ENDPOINT_RESOURCES"][1])
        delivered = undock_amount - dock_amount
        self.assertAlmostEqual(97.6, delivered, places=6)

        stored, capacity = b.ENDPOINT_VESSEL_LIQUIDFUEL
        headroom = capacity - stored
        self.assertGreater(headroom, delivered,
                           "cycle 1 would not fit: the fixture cannot deliver at all")
        self.assertLess(headroom - delivered, delivered,
                        "cycle 2 would ALSO fit: the DestinationFull block the "
                        "spec pins would never fire")

    def test_the_fixture_carries_no_route_and_that_is_what_rvr2_needs(self):
        """The mirror image of `depot-route-recorded`, stated so the two are not
        swapped by accident. This fixture is the route CANDIDATE host - the
        operator created the route AFTER the save was written - which is what
        gives `RouteCommand action=create` something to do. A `ROUTES` node here
        would make the create answer `candidate-already-promoted`."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        self.assertEqual([], b.child_nodes(self.lines, scn, "ROUTES"))
        prompted = b.child_nodes(self.lines, scn, "PROMPTED_ROUTE_CANDIDATES")
        self.assertEqual(1, len(prompted))
        self.assertEqual(b.PROMPTED_CANDIDATE_TREE_ID,
                         b.get_value(self.lines, prompted[0], "treeId"))


class RoverRouteSpecFixtureSyncTests(unittest.TestCase):
    """The spec-to-fixture pairing, which nothing else checks and which costs a
    live flight to get wrong (the `CL-1-pod-impact` lesson).

    Deliberately a TEXT check over the committed spec files rather than a TOML
    parse: the ids the specs must carry are the fixture's own constants, and a
    substring scan cannot be fooled by a spec that parses but names a different
    tree."""

    SPECS = ("RVR-1-rover-route-proof.toml",
             "RVR-2-rover-route-create.toml",
             "RVR-3-route-lifecycle.toml")

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.text = {}
        for name in cls.SPECS:
            path = os.path.join(SCENARIOS_DIR, name)
            with open(path, "r", encoding="utf-8") as fh:
                cls.text[name] = fh.read()

    def test_all_three_specs_stage_this_fixture(self):
        for name in self.SPECS:
            self.assertIn('"fixtures/saves/rover-route-recorded"',
                          self.text[name], name)

    def test_rvr2_names_the_transport_tree_id_verbatim(self):
        """The SealSlot and create steps address the tree by id. A spec naming
        the ENDPOINT tree would seal and promote the wrong one - and the endpoint
        tree carries no route window, so the create would answer
        `candidate-ineligible` rather than failing loudly."""
        text = self.text["RVR-2-rover-route-create.toml"]
        self.assertIn(self.builder.TRANSPORT_TREE_ID, text)
        self.assertNotIn('tree = "%s"' % self.builder.ENDPOINT_TREE_ID, text)

    def test_rvr3_does_not_arm_render_composition_capture(self):
        """THE CATEGORY AUTHOR'S CONSTRAINT, as a gate.

        `RouteLifecycleRuntimeTests.RequireRenderCompositionUnarmed` self-skips
        three of the six cells whenever M-A7 capture is armed, and the arming
        surface is the DECLARATION of `[expectations.renderComposition]`
        (`run.py` sets `PARSEK_RENDER_MANIFEST=1` for any spec that declares one).
        A spec that pins `skipped=0` while declaring the block is internally
        inconsistent, and the inconsistency would only surface on a flight."""
        text = self.text["RVR-3-route-lifecycle.toml"]
        self.assertNotIn("[expectations.renderComposition]", text)
        self.assertNotIn("ExportRenderManifest", text)


if __name__ == "__main__":
    unittest.main()
