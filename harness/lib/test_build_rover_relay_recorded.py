"""Fixture gates for `rover-relay-recorded`, the UNTYPED-DEPOT RELAY host.

WHAT THIS FILE GUARDS, AND WHY IT CANNOT BE LEFT TO `test_saveparse.py`.
`RECORDED_FIXTURES` pins the shape every recorded fixture shares - trees,
recordings, terminal states, branch points, sidecar floor, schema generation,
and (since the 2026-09-02 `routes` facet) the zero-route reading. It pins NOTHING
about a `ROUTE_CONNECTION_WINDOWS` node, because `harness/lib/saveparse.py` has no
route-window facet. On THIS fixture that gap is the whole subject: what makes it
different from every eligible-looking sibling is a single IDENTITY HASH inside one
window's inventory nodes, and no structural count can see it. These cells wire
`harness/tools/build_rover_relay_recorded.py --check` into the suite so a
hand-edit of the committed bytes - or a future re-harvest that quietly produces a
different relay - reds here rather than on RVR-5's or RVR-6's next flight.

IT CANNOT RE-RUN THE BUILD, for the reason `RoverRouteRecordedFixtureDriftTests`
and `DunaOneRecordedFixtureDriftTests` cannot: the input is a COLLECTED operator
save outside the repo that will never be committed. The claims are made against
the RESULT instead, and four of them are made nowhere else in the suite:

  * THE RELAY PRODUCES NO ROUTE, AND IT IS NOT AN ACCIDENT. Zero `ROUTES`, zero
    `PROMPTED_ROUTE_CANDIDATES`, zero `DISMISSED_ROUTE_CANDIDATES`, zero
    `ROUTE_ORIGIN_PROOF` - four absences, each of which changes what RVR-5's
    refusal MEANS if it stops holding. A dismissal row alone would turn
    `candidate-ineligible` into `candidate-dismissed` while every count still
    read correct.
  * THE UNWITNESSED INVENTORY GAIN IS A HASH FACT, AND IT IS A **PRODUCT
    DEFECT'S ONLY COMMITTED SUBJECT**. `RouteAnalysisEngine.
    HasUnwitnessedInventoryGain` pairs a transport gain to an endpoint loss BY
    IDENTITY HASH; here the station that ARRIVES on the transport
    (`5bcde9ad...`) is not the one the endpoint GIVES UP (`5072997a...`) - and
    THEY ARE THE SAME PHYSICAL PART. Stock's
    `ModuleInventoryPart.StoreCargoPartAtSlot(Part, int)` re-serialises the moved
    part through a live `ProtoPartSnapshot`, so `ModuleGroundExpControl.OnSave`
    adds a runtime-computed `canComm` value the craft-authored `STOREDPART` never
    had, and `ComputeInventoryPayloadIdentityHash` - which hashes module-level
    values by design - reports a different identity for a part nobody swapped.
    Filed as LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE
    (OPEN, needs a design call). The other two moved items (`evaChute`,
    `evaScienceKit`) close cleanly, because their modules write nothing new.
    WHAT THIS GUARDS: the fixture being quietly cleaned up. A re-harvest with no
    inventory moved, or one flown after the hash is fixed, produces an ELIGIBLE
    tree - which would leave RVR-5's refusal pins asserting something the bytes
    no longer hold and would retire the defect's only committed subject. When the
    fix lands, these cells and RVR-5's pins are re-measured TOGETHER.
  * BOTH WINDOWS ARE TARGET-BRANCH AND BOTH CROSS-TREE LINKS SURVIVE. This is
    `rover-route-recorded`'s single property, held twice. Dropping either origin
    tree would look like tidying a two-recording forest and would silently break
    the partner link for the window that needed it.
  * NOTHING CARRIES A `mergeState` KEY. That absence IS
    `RouteCandidateFinder.IsTreeFullySealed`, and it is what makes RVR-5's
    refusal ATTRIBUTABLE: `ClassifyCreateRefusal` returns the FIRST failure of
    found -> dismissed -> sealed -> eligible, so an unsealed tree would refuse
    `tree-not-sealed` and the lane would prove nothing about candidacy at all.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "rover-relay-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
SCENARIOS_DIR = os.path.join(_HARNESS, "scenarios")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_rover_relay_recorded.py")
    spec = importlib.util.spec_from_file_location(
        "build_rover_relay_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RoverRelayRecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_rover_relay_recorded.py --check` INTO THE SUITE."""

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

    def test_the_two_route_window_pins_hold(self):
        """Run on its own as well as inside verify_save, so a window regression
        names the WINDOWS rather than arriving inside a fifty-line save diff."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_route_windows(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_seal_state_pin_holds(self):
        """Likewise for the seal absence, which RVR-5's refusal ATTRIBUTION rests
        on: `ClassifyCreateRefusal` reports the first failing gate, so an
        unsealed tree hides the analysis behind `tree-not-sealed`."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_seal_state(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_no_route_state_pin_holds(self):
        """The four absences, on their own, because each one changes what RVR-5's
        `candidate-ineligible` refusal means."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_no_route_state(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_unwitnessed_inventory_gain_pin_holds(self):
        """THE SECOND FAIL-CLOSED REASON, on its own.

        `RouteAnalysisStatus.MixedPickupDelivery` is the status the measured
        derive pass reports over this exact tree (`mixedPickup=1`, source-flight
        KSP.log line 28049), and its cause is one module value stock adds to a
        part in transit (LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-
        CARGO-MOVE). Run separately so a re-harvest that made the tree ELIGIBLE -
        or a hash fix that lands without re-measuring RVR-5 - names the inventory
        rather than arriving as a generic save diff."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_unwitnessed_inventory_gain(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_both_windows_are_target_branch_and_that_is_the_point(self):
        """Derived FROM THE BYTES, not from the builder's constants.

        `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` searches for
        `window.TransferTargetVesselPid != recording.VesselPersistentId` with both
        nonzero. `rover-route-recorded` is the only other committed host that
        satisfies it, and it satisfies it once; this fixture satisfies it twice,
        on two DIFFERENT partner rovers."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        windows, _pids = b._window_records(self.lines, scn)
        self.assertEqual(2, len(windows), "the fixture must carry two windows")
        for _tree_id, _rec_id, carrier, node in windows:
            target = b.get_value(self.lines, node, "transferTargetPid")
            self.assertNotEqual("0", target)
            self.assertNotEqual("0", carrier)
            self.assertNotEqual(target, carrier,
                                "the window is initiator-branch")

    def test_each_windows_cross_tree_partner_recording_is_kept(self):
        """THE FOREST IS THREE TREES FOR A REASON.

        `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` adds one conjunct
        to the cell above: the target pid must ALSO be carried by a recording in
        `RecordingStore.CommittedRecordings`. Here each window's partner lives in
        its own single-recording origin tree, so BOTH origin trees are
        load-bearing and neither is spare payload."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        windows, rec_pids = b._window_records(self.lines, scn)
        carriers = {}
        for tree in b.child_nodes(self.lines, scn, "RECORDING_TREE"):
            tree_id = b.get_value(self.lines, tree, "id")
            for rec in b.child_nodes(self.lines, tree, "RECORDING"):
                carriers.setdefault(
                    b.get_value(self.lines, rec, "vesselPersistentId"), []
                ).append((tree_id, b.get_value(self.lines, rec, "recordingId")))
        for (_tree_id, _rec_id, _carrier, node), want in zip(windows,
                                                             b.ROUTE_WINDOWS):
            target = b.get_value(self.lines, node, "transferTargetPid")
            self.assertEqual([want["partner"]], carriers.get(target, []),
                             "window targeting pid %s lost its partner" % target)
        self.assertEqual(9, len(rec_pids), "the forest lost a recording")

    def test_the_two_hops_resource_deltas_balance(self):
        """The half of the relay that WORKS, asserted so the refusal is not
        misread.

        A reader who sees `candidate-ineligible` will reach for the resource
        bookkeeping first. It is fine: hop 1 moves +200 / -200 LiquidFuel and hop
        2 moves -126.8 / +126.8, both exactly balanced between transport and
        endpoint. What fails is the INVENTORY half, and only on hop 1."""
        b = self.builder
        for i, rows in enumerate(b.ROUTE_WINDOW_RESOURCE_ROWS):
            transport = (float(rows["UNDOCK_TRANSPORT_RESOURCES"][1])
                         - float(rows["DOCK_TRANSPORT_RESOURCES"][1]))
            endpoint = (float(rows["UNDOCK_ENDPOINT_RESOURCES"][1])
                        - float(rows["DOCK_ENDPOINT_RESOURCES"][1]))
            self.assertAlmostEqual(0.0, transport + endpoint, places=6,
                                   msg="hop %d does not balance" % i)
            self.assertNotAlmostEqual(0.0, transport, places=6,
                                      msg="hop %d moved nothing" % i)
        first = b.ROUTE_WINDOW_RESOURCE_ROWS[0]
        second = b.ROUTE_WINDOW_RESOURCE_ROWS[1]
        loaded = (float(first["UNDOCK_TRANSPORT_RESOURCES"][1])
                  - float(first["DOCK_TRANSPORT_RESOURCES"][1]))
        unloaded = (float(second["DOCK_TRANSPORT_RESOURCES"][1])
                    - float(second["UNDOCK_TRANSPORT_RESOURCES"][1]))
        self.assertAlmostEqual(200.0, loaded, places=6)
        self.assertAlmostEqual(126.8, unloaded, places=6)
        self.assertGreater(loaded, unloaded,
                           "the relay would not be a PARTIAL delivery any more")

    def test_the_gained_station_hash_is_not_the_one_the_endpoint_gave_up(self):
        """The rejection restated as the comparison the engine makes, and read
        from the BYTES rather than from the two hash constants.

        The two hashes are ONE part before and after a live move, so what this
        cell asserts is that the defect is still IN these bytes: a gained station
        identity that is not among the endpoint's lost ones. It reds on the two
        changes that would retire the subject without anyone noticing - a
        re-harvest that moved no inventory, and a hash fix landed without
        re-measuring RVR-5's `mixedPickup=1` / `candidate-ineligible` pins."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        windows, _pids = b._window_records(self.lines, scn)
        node = windows[b.UNWITNESSED_GAIN_WINDOW_INDEX][3]

        def hashes(name):
            out = {}
            for holder in b.child_nodes(self.lines, node, name):
                for item in b.child_nodes(self.lines, holder, "ITEM"):
                    if b.get_value(self.lines, item, "partName") != \
                            b.UNWITNESSED_GAIN_PART_NAME:
                        continue
                    key = b.get_value(self.lines, item, "identityHash")
                    out[key] = out.get(key, 0) + int(
                        b.get_value(self.lines, item, "quantity") or "0")
            return out

        gained = {h for h, q in hashes("UNDOCK_TRANSPORT_INVENTORY").items()
                  if q > hashes("DOCK_TRANSPORT_INVENTORY").get(h, 0)}
        lost = {h for h, q in hashes("DOCK_ENDPOINT_INVENTORY").items()
                if q > hashes("UNDOCK_ENDPOINT_INVENTORY").get(h, 0)}
        self.assertTrue(gained, "the transport gained no station at all")
        self.assertTrue(lost, "the endpoint gave up no station at all")
        self.assertEqual(set(), gained & lost,
                         "the gained station IS one the endpoint gave up, so the "
                         "gain is witnessed and the tree would be ELIGIBLE")

    def test_the_relay_geometry_is_a_surface_drive(self):
        """The scale, computed from the bytes: three rovers hundreds of metres
        apart, far outside the ~200 m docking range and well inside physics range
        of each other. It is what makes this a DRIVE relay rather than a warp, and
        what a re-harvest that moved a rover would change."""
        problems = self.builder.verify_geometry(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_active_vessel_is_the_transport_rover_and_not_an_asteroid(self):
        """THE ONE EDIT THE BUILDER MAKES TO THE SAVE, gated on its own.

        The source was written from the SPACE CENTER, so KSP left
        `activeVessel = 0` pointing at a stock asteroid in solar orbit. That save
        BOOTS - `IsLoadedGameFocusable` is happy with it - straight into deep
        space with all three rovers unloaded, which would make RVR-6's whole
        live-vessel census vacuous while every structural facet still read
        correct."""
        b = self.builder
        records = b.vessel_records(self.lines)
        fs = b.flightstate_node(self.lines)
        index = int(b.get_value(self.lines, fs, "activeVessel"))
        active = records[index]
        self.assertEqual(b.ACTIVE_VESSEL_NAME, active["name"])
        self.assertEqual(b.ACTIVE_VESSEL_PID, active["pid"])
        self.assertEqual("Rover", active["type"])
        self.assertEqual("LANDED", active["sit"])
        self.assertNotEqual(b.SOURCE_ACTIVE_VESSEL_NAME, active["name"])

    def test_the_active_vessel_carries_the_logistics_fixture_requirement(self):
        """`UnloadedFuelVesselFixture` returns `reason = "no-liquidfuel-resource"`
        and every unloaded-depot `Logistics` cell skips unless the ACTIVE vessel
        carries a LiquidFuel RESOURCE node with positive capacity. RVR-6's census
        is exactly those cells, so the requirement is a fixture pin rather than a
        spec assumption - and it is the LiquidFuel one, never staging (H39
        generalised `IsolatedBatchWiringGroupTests` to a per-category capability
        table for this: the PRELAUNCH + `ModuleEngines` check is the
        `SceneExitMerge` / `Rewind` requirement, and `Logistics` never stages)."""
        b = self.builder
        records = {r["pid"]: r for r in b.vessel_records(self.lines)}
        active = records[b.ACTIVE_VESSEL_PID]
        amount, capacity = b._sum_resource(self.lines, active["span"], "LiquidFuel")
        self.assertGreater(capacity, 0.0, "no LiquidFuel capacity on the host")
        self.assertGreater(amount, 0.0, "the host's LiquidFuel tank is empty")


class RoverRelaySpecFixtureSyncTests(unittest.TestCase):
    """The spec-to-fixture pairing, which nothing else checks and which costs a
    live flight to get wrong (the `CL-1-pod-impact` lesson).

    Deliberately a TEXT check over the committed spec files rather than a TOML
    parse: the ids the specs must carry are the fixture's own constants, and a
    substring scan cannot be fooled by a spec that parses but names a different
    tree."""

    # Every committed spec that stages this fixture.
    SPECS = ("RVR-5-rover-relay-eligibility.toml",
             "RVR-6-rover-relay-logistics-host.toml")

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.text = {}
        for name in cls.SPECS:
            path = os.path.join(SCENARIOS_DIR, name)
            with open(path, "r", encoding="utf-8") as fh:
                cls.text[name] = fh.read()

    def test_every_consumer_spec_stages_this_fixture(self):
        for name in self.SPECS:
            self.assertIn('"fixtures/saves/rover-relay-recorded"',
                          self.text[name], name)

    def test_rvr5_names_the_relay_tree_id_verbatim_and_neither_origin_tree(self):
        """The SealSlot and create steps address the tree by id. A spec naming an
        ORIGIN tree would seal and analyse a single-recording launch that carries
        no window at all, so the create would still answer `candidate-ineligible`
        - for a completely different reason, and the lane would look green while
        measuring nothing."""
        text = self.text["RVR-5-rover-relay-eligibility.toml"]
        self.assertIn(self.builder.RELAY_TREE_ID, text)
        self.assertNotIn('tree = "%s"' % self.builder.ENDPOINT_A_TREE_ID, text)
        self.assertNotIn('tree = "%s"' % self.builder.ENDPOINT_B_TREE_ID, text)

    def test_rvr5_pins_the_seal_total_to_the_relay_tree_size(self):
        """`sealslot complete ... total=N` counts the RECORDING nodes of the sealed
        tree, so N is the relay tree's own size and nothing else. The mirror of
        the RVR-7 cell in `test_build_rover_relay_c_recorded.py`: until 2026-09-03
        the `total=7` here was a free literal no cell pinned. Scoped to RVR-5's
        `required` list, because the header quotes the token in prose and RVR-6
        seals nothing."""
        text = self.text["RVR-5-rover-relay-eligibility.toml"]
        required_block = text.split("required  = [", 1)[1].split("]", 1)[0]
        want = "sealslot complete mode=tree tree=%s total=%d sealed=0" % (
            self.builder.RELAY_TREE_ID, len(self.builder.RELAY_TREE_RECORDING_IDS))
        self.assertIn(want, required_block)

    def test_rvr5_expects_the_create_to_be_admitted(self):
        """INVERTED WITH THE LANE ON 2026-09-03. Until then this cell asserted
        `expect = "REJECTED"` and the `candidate-ineligible` reason, because RVR-5
        pinned the REFUSAL of this relay. Both refusal reasons are now closed
        (PR #1620 matches stored cargo by kind; PR #1618 plus the pickup-window
        origin derivation replace the player-typed-depot requirement), so the
        lane's ENTIRE product is the ADMISSION and a surviving `expect =
        "REJECTED"` would be a spec that passes when the product regresses back
        into refusing a legitimate two-hop relay.

        `ParsekTestCommandAddon.RouteCommandCreate` calls
        `SetExecResult("REJECTED", null, msg)` on any non-`None` refusal, so the
        OK verdict and the `refusal=None` token are two independent instruments on
        the same fact, and the `routecommand rejected` forbid is the third."""
        text = self.text["RVR-5-rover-relay-eligibility.toml"]
        self.assertNotIn('expect = "REJECTED"', text)
        self.assertIn("refusal=None", text)
        self.assertIn("routecommand rejected", text)

    def test_neither_spec_arms_render_composition_capture(self):
        """`run.py` sets `PARSEK_RENDER_MANIFEST=1` for any spec that DECLARES an
        `[expectations.renderComposition]` block, and neither lane measures a
        render surface. Declaring one would arm a recorder both lanes would then
        have to reason about for nothing."""
        for name in self.SPECS:
            self.assertNotIn("[expectations.renderComposition]", self.text[name])
            self.assertNotIn("ExportRenderManifest", self.text[name])


if __name__ == "__main__":
    unittest.main()
