"""Fixture gates for `rover-relay-c-recorded`, the WRONG-PROOF RELAY host.

WHAT THIS FILE GUARDS, AND WHY IT CANNOT BE LEFT TO `test_saveparse.py`.
`RECORDED_FIXTURES` pins the shape every recorded fixture shares - trees,
recordings, terminal states, branch points, sidecar floor, schema generation and
the zero-route reading. It pins NOTHING about a `ROUTE_CONNECTION_WINDOWS` node
and NOTHING about a `ROUTE_ORIGIN_PROOF` node, because `harness/lib/saveparse.py`
has a facet for neither. On THIS fixture that second gap is the whole subject:
what makes it worth committing next to `rover-relay-recorded` is TWO PERSISTED
PROOFS THAT NAME THE WRONG ORIGIN, and no structural count can see them. These
cells wire `harness/tools/build_rover_relay_c_recorded.py --check` into the suite
so a hand-edit of the committed bytes - or a re-harvest that quietly produces a
different relay - reds here rather than on RVR-7's next flight.

IT CANNOT RE-RUN THE BUILD, for the reason every sibling drift class cannot: the
input is a COLLECTED operator save outside the repo that will never be committed.
The claims are made against the RESULT instead, and four of them are made nowhere
else in the suite:

  * THE TWO PROOFS ARE WRONG, IN TWO DIFFERENT WAYS. Hop 1 (the PICKUP at rover B)
    bound the TRANSPORT itself as origin (`originName='C' originPid=612987736`,
    with B's root part in the TRANSPORT slot - the halves exactly inverted). Hop 2
    (the DELIVERY at rover A) bound the DESTINATION as origin
    (`originName='A' originPid=4280917262`) for a window that has no pickup at
    all. Both were written by the 2026-09-02 undock binder, both say
    `pickup=Carried pickupValidated=0`, and both are quoted verbatim from the
    source flight's KSP.log in the builder header. THESE ARE THE ONLY COMMITTED
    BYTES on which an analysis that derives the origin from the PICKUP WINDOW can
    be shown to override a bound proof, because every other route fixture in the
    corpus carries zero proof nodes.
  * THE WINDOW FLOW DIRECTIONS ARE WHAT THE PROOFS CONTRADICT. Window 0's
    transport GAINS (a pickup, so its endpoint B is the SOURCE) and window 1's
    LOSES (a delivery, so its endpoint A is the DESTINATION), in the resource
    dimension AND - by KIND, per PR #1620 - in the inventory one. That derivation
    is the whole content of "source B, destination A", so it is computed from the
    bytes rather than restated.
  * THE HOP-1 IDENTITY SWAP. KSP resolved the hop-1 merged vessel to rover B's
    identity, so window 1 of the sibling's "both windows TARGET-branch" property
    does NOT hold here: window 0 is INITIATOR-branch (target pid == carrier pid)
    and only window 1 is TARGET-branch. That is a structural difference worth a
    cell of its own, because a reader who copies the sibling's claim onto this
    fixture will predict the wrong `RouteProof_*` cell outcomes.
  * THE FIXTURE IS STAGED AT START-OF-CYCLE, AND THE STAGING IS DERIVED FROM THE
    WINDOWS. As harvested the save was written AFTER the relay, so B held 45.6
    LiquidFuel against the window's own 154.4 pickup manifest and A was at 400/400
    with 6 of 6 inventory slots used - both all-or-nothing gates false, every
    driven cycle blocking and emitting nothing. Builder step 3 restores each
    physical endpoint to the state ITS OWN window recorded at ITS dock (200 / 400
    and three of six slots on both), lifting the STOREDPART bytes verbatim out of
    the snapshot. The cells below re-read the windows rather than the builder's
    constants, check the lift by inner `PART persistentId`, confirm the transport
    is untouched, and pin the one-cycle arithmetic RVR-7's driver shape and its two
    hold-token forbids both rest on.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import re
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# The endpoint-matrix cells read the same fixture bytes the STAGE STEP will patch,
# through the same pure module, so a declaration this suite accepts is one the
# staged run can apply.
import savepatch  # noqa: E402

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "rover-relay-c-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
SCENARIOS_DIR = os.path.join(_HARNESS, "scenarios")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_rover_relay_c_recorded.py")
    spec = importlib.util.spec_from_file_location(
        "build_rover_relay_c_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RoverRelayCRecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_rover_relay_c_recorded.py --check` INTO THE SUITE."""

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

    def test_the_wrong_origin_proof_pins_hold(self):
        """THE FIXTURE'S PRODUCT, on its own.

        Run separately so a re-harvest flown on a FIXED binder - which would make
        the proofs correct and retire the override's only subject - names the
        proofs rather than arriving as a generic save diff."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_wrong_origin_proofs(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_seal_state_pin_holds(self):
        """The seal absence, which any create verdict's ATTRIBUTION rests on:
        `ClassifyCreateRefusal` reports the first failing gate, so an unsealed tree
        hides the analysis behind `tree-not-sealed`."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_seal_state(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_no_route_state_pin_holds(self):
        """Three absences - `ROUTES`, `PROMPTED_ROUTE_CANDIDATES`,
        `DISMISSED_ROUTE_CANDIDATES` - each of which changes what a create verdict
        over these bytes means. NOTE the fourth absence the SIBLING asserts
        (`ROUTE_ORIGIN_PROOF`) is deliberately NOT one of them here: this fixture
        carries two, and they are the point."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_no_route_state(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_flow_directions_pin_holds(self):
        """PICKUP then DELIVERY, derived from the bytes in both dimensions. This
        is the claim the two persisted proofs contradict, so it gets its own cell:
        a re-harvest that flattened either direction would leave a fixture that
        still parses as a two-window relay while no longer supporting one route."""
        scn = self.builder.parsek_scenario(self.lines)
        self.assertIsNotNone(scn, "no ParsekScenario node")
        problems = self.builder.verify_flow_directions(self.lines, scn)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_bound_origins_are_not_the_windows_own_source(self):
        """THE OVERRIDE'S SUBJECT, RE-DERIVED FROM THE BYTES rather than from the
        builder's constants.

        For each window the SOURCE is decided by the transport's own resource
        delta: a transport that GAINS took the cargo FROM its endpoint, so that
        endpoint is the source. Hop 1's proof must therefore NOT name window 0's
        endpoint pid, and hop 2 has no source at all yet still carries a proof.
        Both are the shape an analysis that trusts the persisted proof gets wrong,
        and a save where either stopped holding is a save this fixture no longer
        has a reason to exist for."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        windows, _pids = b._window_records(self.lines, scn)
        self.assertEqual(2, len(windows), "the fixture must carry two windows")

        proofs = {}
        for tree in b.child_nodes(self.lines, scn, "RECORDING_TREE"):
            for rec in b.child_nodes(self.lines, tree, "RECORDING"):
                rec_id = b.get_value(self.lines, rec, "recordingId")
                for proof in b.child_nodes(self.lines, rec, "ROUTE_ORIGIN_PROOF"):
                    proofs[rec_id] = b.get_value(
                        self.lines, proof, "startDockedOriginVesselPid")
        self.assertEqual(2, len(proofs),
                         "the fixture must carry exactly two origin proofs")

        def transport_delta(node):
            def total(name):
                out = 0.0
                for holder in b.child_nodes(self.lines, node, name):
                    for row in b.child_nodes(self.lines, holder, "RESOURCE"):
                        if b.get_value(self.lines, row, "name") == "LiquidFuel":
                            out += float(b.get_value(self.lines, row, "amount"))
                return out
            return (total("UNDOCK_TRANSPORT_RESOURCES")
                    - total("DOCK_TRANSPORT_RESOURCES"))

        # Window 0: a PICKUP, so its endpoint IS the source, and the proof on its
        # carrying recording names something else.
        _t0, rec0, _c0, node0 = windows[0]
        self.assertGreater(transport_delta(node0), 0.0,
                           "window 0 is no longer a pickup")
        source0 = b.get_value(self.lines, node0, "transferTargetPid")
        self.assertIn(rec0, proofs, "the pickup hop lost its origin proof")
        self.assertNotEqual(
            source0, proofs[rec0],
            "the pickup hop's proof now names the window's own source, so there "
            "is nothing left for a pickup-window override to correct")

        # Window 1: a DELIVERY, so it has no source at all - and still carries a
        # proof, which is the second, different wrongness.
        _t1, rec1, _c1, node1 = windows[1]
        self.assertLess(transport_delta(node1), 0.0,
                        "window 1 is no longer a delivery")
        self.assertIn(rec1, proofs,
                      "the delivery hop lost the origin proof it should never "
                      "have had")

    def test_window_zero_is_initiator_branch_and_window_one_is_target(self):
        """THE HOP-1 IDENTITY SWAP, derived FROM THE BYTES.

        `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` searches for
        `window.TransferTargetVesselPid != recording.VesselPersistentId` with both
        nonzero. `rover-relay-recorded` satisfies that on BOTH its windows; this
        fixture satisfies it on ONE, because KSP resolved the hop-1 merged vessel
        to rover B's identity and the dock member therefore carries the same pid
        the window targets. A reader who copies the sibling's claim across will
        predict the wrong cell outcomes, so the asymmetry is pinned."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        windows, _pids = b._window_records(self.lines, scn)
        self.assertEqual(2, len(windows))
        branches = []
        for _tree_id, _rec_id, carrier, node in windows:
            target = b.get_value(self.lines, node, "transferTargetPid")
            self.assertNotEqual("0", target)
            self.assertNotEqual("0", carrier)
            branches.append("INITIATOR" if target == carrier else "TARGET")
        self.assertEqual(["INITIATOR", "TARGET"], branches)

    def test_each_windows_cross_tree_partner_recording_is_kept(self):
        """THE FOREST IS THREE TREES FOR A REASON.

        `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` requires the
        target pid to ALSO be carried by a recording in
        `RecordingStore.CommittedRecordings`. Each window's partner lives in its
        own single-recording origin tree, so BOTH origin trees are load-bearing
        and neither is spare payload.

        Stated as MEMBERSHIP rather than as the sibling's "exactly one holder",
        because the hop-1 identity swap means window 0's target pid is also
        carried by two relay-tree members."""
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
        for (tree_id, _rec_id, _carrier, node), want in zip(windows,
                                                            b.ROUTE_WINDOWS):
            target = b.get_value(self.lines, node, "transferTargetPid")
            holders = carriers.get(target, [])
            self.assertIn(want["partner"], holders,
                          "window targeting pid %s lost its partner" % target)
            self.assertNotEqual(want["partner"][0], tree_id,
                                "the partner must live in a DIFFERENT tree")
        self.assertEqual(10, len(rec_pids), "the forest lost a recording")

    def test_the_two_hops_resource_deltas_balance_and_point_opposite_ways(self):
        """The arithmetic that makes ONE route out of TWO windows.

        Hop 1 moves +154.4 onto the transport and -154.4 off B; hop 2 moves -200
        off the transport and +200 into A. Balanced on both, opposite in sign, and
        the delivered amount is LARGER than the picked-up one because the
        transport arrived at B already carrying 200 of its own."""
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
        picked = (float(first["UNDOCK_TRANSPORT_RESOURCES"][1])
                  - float(first["DOCK_TRANSPORT_RESOURCES"][1]))
        delivered = (float(second["DOCK_TRANSPORT_RESOURCES"][1])
                     - float(second["UNDOCK_TRANSPORT_RESOURCES"][1]))
        self.assertAlmostEqual(154.4, picked, places=6)
        self.assertAlmostEqual(200.0, delivered, places=6)
        self.assertGreater(delivered, picked,
                           "the transport no longer contributes fuel of its own")

    def test_the_endpoints_are_staged_at_start_of_cycle(self):
        """THE REPAIR, ASSERTED AGAINST THE WINDOWS IT WAS DERIVED FROM.

        As harvested, the save was written AFTER the relay, so the pickup source
        was drained (B 45.6 against a 154.4 manifest) and the destination
        saturated in both dimensions (A 400/400, 6 of 6 slots) - a driven cycle
        blocked and emitted nothing. Step 3 of the builder restores both physical
        endpoints to the state THEIR OWN window recorded at ITS dock, in
        FLIGHTSTATE only. This cell re-reads the windows and re-derives every
        restored value from them, so the repair cannot drift from the recording
        it replays."""
        problems = self.builder.verify_start_of_cycle_endpoints(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_restored_stored_parts_are_the_windows_own_bytes(self):
        """THE LIFT IS VERBATIM, checked by inner `PART persistentId`.

        The repair writes the STOREDPART nodes out of the window's own
        `DOCK_ENDPOINT_INVENTORY` snapshot rather than synthesising them, so every
        restored stored part on B and on A must carry the inner `persistentId` its
        window recorded. That is also what makes the placement auditable: it is the
        pid, not the slot index, that says which of rover A's two slot-1 stations
        was the original (see `CRAFT_AUTHORED_INVENTORY_LAYOUT`).

        It doubles as a collision check. All six restored pids must be distinct -
        two endpoints restored from two different windows must not end up naming
        the same stored part."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        windows, _pids = b._window_records(self.lines, scn)
        records = {r["pid"]: r for r in b.vessel_records(self.lines)}
        seen = set()
        for pid, window_index in b.REPAIR_TARGETS:
            window = windows[window_index][3]
            want = set()
            for _part, _slot, block in b._window_dock_endpoint_stored_parts(
                    self.lines, window):
                for line in block:
                    text = line.strip()
                    if text.startswith("persistentId = "):
                        want.add(text)
                        break
            got = set()
            span = records[pid]["span"]
            for module in b._inventory_modules(self.lines, span):
                for holder in b.child_nodes(self.lines, module, "STOREDPARTS"):
                    for stored in b.child_nodes(self.lines, holder, "STOREDPART"):
                        for i in range(stored[0], stored[1]):
                            text = self.lines[i].strip()
                            if text.startswith("persistentId = "):
                                got.add(text)
                                break
            self.assertEqual(want, got,
                             "vessel pid %s does not hold its window's own "
                             "stored-part bytes" % pid)
            self.assertEqual(set(), seen & got,
                             "two restored endpoints share a stored-part "
                             "persistentId")
            seen |= got
        self.assertEqual(6, len(seen), "expected six distinct restored stored parts")

    def test_the_transport_is_untouched_by_the_repair(self):
        """C IS NOT A REPAIR TARGET, and that is a decision rather than an
        oversight: the pickup writer removes from the SOURCE and the delivery
        writer stores into the DESTINATION, so a dispatch never reads the
        transport's own hold. It still carries the relay's output - 154.4 / 400
        LiquidFuel and three stored parts including the RE-HASHED station
        (`5bcde9ad...`, 166 lines where the craft-authored one is 165) - and a
        repair that reached it would be editing the only surviving witness to that
        move."""
        b = self.builder
        records = {r["pid"]: r for r in b.vessel_records(self.lines)}
        self.assertNotIn(b.TRANSPORT_LIVE_PID,
                         {pid for pid, _w in b.REPAIR_TARGETS})
        transport = records[b.TRANSPORT_LIVE_PID]
        amount, capacity = b._sum_resource(self.lines, transport["span"],
                                           "LiquidFuel")
        self.assertAlmostEqual(154.39999999999196, amount, places=6)
        self.assertAlmostEqual(400.0, capacity, places=6)
        self.assertEqual(3, len(b._live_stored_items(self.lines,
                                                     transport["span"])))

    def test_one_cycle_fits_and_a_second_would_not(self):
        """THE ARITHMETIC THAT DECIDES RVR-7'S DRIVER SHAPE, over the repaired
        bytes rather than over prose.

        Cycle 0: B holds 200 against a 154.4 pickup manifest (fits), and A has
        exactly 200 of LiquidFuel headroom against a 200 delivery manifest -
        `RouteDeliveryPlanner` takes `Math.Min(requested, freeCapacity)` and flags
        partial only when that is SHORT, so an exact fit is a full fit.
        Cycle 1: B would be down to 45.6 and A back at 400/400, so BOTH
        all-or-nothing gates fail. That is why RVR-7 drives ONE send-once where
        RVR-2 drives two, and why it can forbid both hold tokens."""
        b = self.builder
        records = {r["pid"]: r for r in b.vessel_records(self.lines)}
        pickup = (float(b.ROUTE_WINDOW_RESOURCE_ROWS[0]["UNDOCK_TRANSPORT_RESOURCES"][1])
                  - float(b.ROUTE_WINDOW_RESOURCE_ROWS[0]["DOCK_TRANSPORT_RESOURCES"][1]))
        delivery = (float(b.ROUTE_WINDOW_RESOURCE_ROWS[1]["UNDOCK_ENDPOINT_RESOURCES"][1])
                    - float(b.ROUTE_WINDOW_RESOURCE_ROWS[1]["DOCK_ENDPOINT_RESOURCES"][1]))
        src, _cap = b._sum_resource(self.lines,
                                    records[b.ENDPOINT_B_LIVE_PID]["span"],
                                    "LiquidFuel")
        dst, dst_cap = b._sum_resource(self.lines,
                                       records[b.ENDPOINT_A_LIVE_PID]["span"],
                                       "LiquidFuel")
        self.assertGreaterEqual(src, pickup, "cycle 0's pickup does not fit")
        self.assertGreaterEqual(dst_cap - dst, delivery,
                                "cycle 0's delivery does not fit")
        self.assertLess(src - pickup, pickup,
                        "a SECOND pickup would fit, so the one-cycle driver "
                        "shape and the OriginLacksCargo forbid are both wrong")
        self.assertLess(dst_cap - (dst + delivery), delivery,
                        "a SECOND delivery would fit, so the one-cycle driver "
                        "shape and the DestinationFull forbid are both wrong")

    def test_the_relay_geometry_is_a_surface_drive(self):
        """The scale, computed from the bytes: three rovers hundreds of metres
        apart, far outside the ~200 m docking range and well inside physics range
        of each other. It is what makes this a DRIVE relay rather than a warp, and
        it decides which live-vessel guards find a subject.

        IT DOES **NOT** DECIDE THE WRITER PATH, and the authored version of this
        docstring said it did ("what makes a driven route over these bytes take
        `path=loaded`"). RVR-7's first census measured `path=unloaded` on every
        writer over exactly these bytes: a seam `TimeJump` warps with the endpoints
        PACKED, so the load state at the DISPATCH TICK decides, not the
        separation."""
        problems = self.builder.verify_geometry(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_active_vessel_is_the_transport_rover_and_not_an_asteroid(self):
        """THE ONE EDIT THE BUILDER MAKES TO THE SAVE, gated on its own.

        The source was written from the SPACE CENTER, so KSP left
        `activeVessel = 0` pointing at `Ast. RQL-681` in solar orbit. That save
        BOOTS - `IsLoadedGameFocusable` is happy with it - straight into deep space
        with all three rovers unloaded, while every structural facet still reads
        correct."""
        b = self.builder
        records = b.vessel_records(self.lines)
        fs = b.flightstate_node(self.lines)
        index = int(b.get_value(self.lines, fs, "activeVessel"))
        active = records[index]
        self.assertEqual(b.ACTIVE_VESSEL_NAME, active["name"])
        self.assertEqual(b.ACTIVE_VESSEL_PID, active["pid"])
        # `Probe`, not the sibling's `Rover`: this operator's C came off the
        # hop-1 undock as a re-typed vessel. Pinned so a reader does not carry
        # the sibling's type across.
        self.assertEqual("Probe", active["type"])
        self.assertEqual("LANDED", active["sit"])
        self.assertNotEqual(b.SOURCE_ACTIVE_VESSEL_NAME, active["name"])

    def test_the_active_vessel_carries_the_logistics_fixture_requirement(self):
        """`UnloadedFuelVesselFixture` returns `reason = "no-liquidfuel-resource"`
        and every unloaded-depot `Logistics` cell skips unless the ACTIVE vessel
        carries a LiquidFuel RESOURCE node with positive capacity. Pinned here as
        a fixture property so a future `Logistics` host lane over these bytes does
        not have to assume it."""
        b = self.builder
        records = {r["pid"]: r for r in b.vessel_records(self.lines)}
        active = records[b.ACTIVE_VESSEL_PID]
        amount, capacity = b._sum_resource(self.lines, active["span"], "LiquidFuel")
        self.assertGreater(capacity, 0.0, "no LiquidFuel capacity on the host")
        self.assertGreater(amount, 0.0, "the host's LiquidFuel tank is empty")


class RoverRelayCSpecFixtureSyncTests(unittest.TestCase):
    """The spec-to-fixture pairing, which nothing else checks and which costs a
    live flight to get wrong (the `CL-1-pod-impact` lesson).

    Deliberately a TEXT check over the committed spec files rather than a TOML
    parse: the ids the specs must carry are the fixture's own constants, and a
    substring scan cannot be fooled by a spec that parses but names a different
    tree."""

    # Every committed spec that stages this fixture: RVR-7, the delivering
    # baseline, plus the RVR-8..RVR-15 ENDPOINT MATRIX that varies the live
    # FLIGHTSTATE around it through `[[fixture.liveState]]`. The list is spelled
    # out rather than globbed so a new consumer is a deliberate edit here.
    SPECS = ("RVR-7-rover-relay-c-dispatch.toml",
             "RVR-8-rover-relay-c-second-cycle-hold.toml",
             "RVR-9-rover-relay-c-surface-cadence.toml",
             "RVR-10-rover-relay-c-origin-empty.toml",
             "RVR-11-rover-relay-c-origin-partial.toml",
             "RVR-12-rover-relay-c-origin-cargo-missing.toml",
             "RVR-13-rover-relay-c-destination-full.toml",
             "RVR-14-rover-relay-c-destination-partial.toml",
             "RVR-15-rover-relay-c-destination-empty.toml")

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
            self.assertIn('"fixtures/saves/rover-relay-c-recorded"',
                          self.text[name], name)

    def test_rvr7_names_the_relay_tree_id_verbatim_and_neither_origin_tree(self):
        """The SealSlot and create steps address the tree by id. A spec naming an
        ORIGIN tree would seal and analyse a single-recording launch that carries
        no window at all, so the create would answer `candidate-ineligible` for a
        completely different reason and the lane would look green while measuring
        nothing."""
        text = self.text["RVR-7-rover-relay-c-dispatch.toml"]
        self.assertIn(self.builder.RELAY_TREE_ID, text)
        self.assertNotIn('tree = "%s"' % self.builder.ENDPOINT_A_TREE_ID, text)
        self.assertNotIn('tree = "%s"' % self.builder.ENDPOINT_B_TREE_ID, text)

    def test_rvr7_pins_the_seal_total_to_the_relay_tree_size(self):
        """`sealslot complete ... total=N` counts the RECORDING nodes of the sealed
        tree, so N is the relay tree's own size and nothing else. Before this cell
        the token was a free literal: a `total=8 -> total=7` mutation passed the
        whole `harness/lib` suite (measured 2026-09-03 in the PR #1622 review),
        because `verify_seal_state` pins the SAVE and no cell pinned the SPEC.
        Scoped to the `required` list, not the whole file, for the same reason as
        the path cell below: the header quotes the token in prose."""
        want = "sealslot complete mode=tree tree=%s total=%d sealed=0" % (
            self.builder.RELAY_TREE_ID, len(self.builder.RELAY_TREE_RECORDING_IDS))
        self.assertIn(want, self._required_block())

    def test_rvr7_expects_the_create_to_be_admitted(self):
        """The lane's ENTIRE product is the ADMISSION of the relay, so an
        `expect = "REJECTED"` on the create step would be a spec that passes when
        the product regresses back into refusing a legitimate two-hop relay.

        `ParsekTestCommandAddon.RouteCommandCreate` calls
        `SetExecResult("REJECTED", null, msg)` on any non-`None` refusal, so the
        OK verdict and the `refusal=None` token are two independent instruments on
        the same fact (the GS-3 / H58 shape)."""
        text = self.text["RVR-7-rover-relay-c-dispatch.toml"]
        self.assertNotIn('expect = "REJECTED"', text)
        self.assertIn("refusal=None", text)

    def test_rvr7_forbids_the_refusal_line(self):
        """The vacuity guard. A run whose create was refused still satisfies the
        boot and seal tokens, so the forbid is what says no refusal may reach the
        seam at the end of this lane."""
        text = self.text["RVR-7-rover-relay-c-dispatch.toml"]
        self.assertIn("routecommand rejected", text)

    def test_rvr7_forbids_both_eligibility_hold_tokens(self):
        """THE FORBIDS THE START-OF-CYCLE REPAIR EXISTS TO EARN.

        The repair restores B's cargo and A's headroom so eligibility steps 6 and
        8 - `RouteOriginCargoCheck.HasRequired` and
        `RouteDestinationCapacityCheck.HasCapacityForAllStops`, both
        all-or-nothing - pass. Forbidding both hold kinds is what makes a
        REGRESSION TO THE SPENT STATE red explicitly, and name which end
        regressed, instead of presenting as a missing delivery token."""
        text = self.text["RVR-7-rover-relay-c-dispatch.toml"]
        self.assertIn("BLOCKED kind=OriginLacksCargo", text)
        self.assertIn("BLOCKED kind=DestinationFull", text)

    def test_rvr7_drives_exactly_one_send_once(self):
        """ONE CYCLE, AND THE COUNT IS PART OF THE ARGUMENT.

        After cycle 0 the endpoints are spent again exactly as the operator left
        them (B down to 45.6 against a 154.4 manifest, A back at 400/400), so a
        SECOND send-once would block - and blocking is what the two forbids above
        catch. A spec with two send-once steps would therefore contradict its own
        forbid list. RVR-2 drives two because ITS fixture has headroom for a
        second partial cycle; this one does not, and
        `test_one_cycle_fits_and_a_second_would_not` pins that arithmetic."""
        text = self.text["RVR-7-rover-relay-c-dispatch.toml"]
        self.assertEqual(1, text.count('action = "send-once"'))
        self.assertEqual(1, text.count('cmd = "TimeJump"'))

    def test_rvr7_pins_the_unloaded_path_on_both_halves(self):
        """`path=unloaded` on all THREE writer lines, MEASURED on the first census
        (run 2026-09-02_2346 UTC): the seam `TimeJump` to ut=1100 warps with the
        endpoints packed, so the load state at the dispatch tick decides the
        writer path, not physics range. The authored prediction was `loaded`
        (all three rovers sit within 313 m / 731 m / 1041 m of each other, pinned
        by `verify_geometry`) and the first flight red on exactly that, with the
        route otherwise created, dispatched and delivered in full. This cell pins
        the measured spelling so the prediction cannot creep back.

        SCOPED TO THE `required` LIST, not to the whole file: the header still
        carries the `loaded` reasoning as a superseded prediction, and a
        file-wide scan reads that prose as a pin - the standing "spec comments
        quote keys verbatim" trap."""
        required_block = self._required_block()
        self.assertIn("Origin debit: route=.* origin=B", required_block)
        self.assertIn("Delivery write: route=.* dest=A", required_block)
        self.assertEqual(3, required_block.count("path=unloaded"))
        self.assertEqual(0, required_block.count("path=loaded\""))

    def _required_block(self):
        text = self.text["RVR-7-rover-relay-c-dispatch.toml"]
        return text.split("required  = [", 1)[1].split("]", 1)[0]

    def test_rvr7_does_not_pin_the_catch_block_inventory_line(self):
        """`RemoveInventory(part=..., kind=...)` LIVES IN A CATCH BLOCK.

        `LiveInventoryPickupWriter.RemoveOne` emits it as a Warn ONLY when the
        removal threw; the success line is `Inventory remove (loaded|unloaded): ...
        removed=1` - and RVR-7 measured the UNLOADED spelling. Requiring the catch-block string would red every correct run
        and pass only on an exception, so this cell keeps it out of the spec for
        good rather than leaving the next author to re-derive it."""
        text = self.text["RVR-7-rover-relay-c-dispatch.toml"]
        required_block = text.split("required  = [", 1)[1].split("]", 1)[0]
        self.assertNotIn("RemoveInventory(", required_block)
        self.assertIn("Inventory remove \\\\(unloaded\\\\)", required_block)

    def test_the_spec_does_not_arm_render_composition_capture(self):
        """`run.py` sets `PARSEK_RENDER_MANIFEST=1` for any spec that DECLARES an
        `[expectations.renderComposition]` block, and this lane measures no render
        surface. Declaring one would arm a recorder the lane would then have to
        reason about for nothing."""
        for name in self.SPECS:
            self.assertNotIn("[expectations.renderComposition]", self.text[name])
            self.assertNotIn("ExportRenderManifest", self.text[name])


class RoverRelayCEndpointMatrixTests(unittest.TestCase):
    """THE RVR-8..RVR-15 ENDPOINT MATRIX, checked against the FIXTURE'S OWN BYTES.

    Eight lanes over one committed save, each staging a different LIVE endpoint
    state through `[[fixture.liveState]]` and pinning tokens DERIVED from that
    state. The derivations are arithmetic on numbers this fixture carries, so
    they are re-derivable here - and that is the point: a matrix whose numbers
    were hand-copied would rot the first time the fixture was re-harvested, and
    it would rot SILENTLY, because a wrong shortfall pin reds as a product
    finding on a live flight rather than as a fixture finding in CI.

    THE STRONGEST CELL IS `test_every_pinned_origin_shortfall_is_the_fixtures_own
    _subtraction`, which recomputes 154.39 / 54.39 / 108.79 from the window
    snapshots and the specs' own declared tank values and asserts the pinned
    digits are a prefix of the result. Nothing else in the tree connects those
    literals to the bytes."""

    MATRIX = ("RVR-8-rover-relay-c-second-cycle-hold.toml",
              "RVR-9-rover-relay-c-surface-cadence.toml",
              "RVR-10-rover-relay-c-origin-empty.toml",
              "RVR-11-rover-relay-c-origin-partial.toml",
              "RVR-12-rover-relay-c-origin-cargo-missing.toml",
              "RVR-13-rover-relay-c-destination-full.toml",
              "RVR-14-rover-relay-c-destination-partial.toml",
              "RVR-15-rover-relay-c-destination-empty.toml")

    # The lanes whose product is a BLOCKED cycle. RVR-8 blocks too, but only on
    # its SECOND cycle - its first one delivers - so it is deliberately absent
    # from the "nothing was written" sweep below.
    BLOCKING = ("RVR-10-rover-relay-c-origin-empty.toml",
                "RVR-11-rover-relay-c-origin-partial.toml",
                "RVR-12-rover-relay-c-origin-cargo-missing.toml",
                "RVR-13-rover-relay-c-destination-full.toml",
                "RVR-14-rover-relay-c-destination-partial.toml")

    PID_A = 4280917262
    PID_B = 90564594

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.spec = {}
        cls.text = {}
        for name in cls.MATRIX:
            path = os.path.join(SCENARIOS_DIR, name)
            with open(path, "rb") as fh:
                cls.spec[name] = tomllib.load(fh)
            with open(path, "r", encoding="utf-8") as fh:
                cls.text[name] = fh.read()
        with open(os.path.join(FIXTURE_DIR, "persistent.sfs"),
                  "r", encoding="utf-8", newline="") as fh:
            cls.lines = fh.read().replace("\r\n", "\n").split("\n")

    # -- helpers ---------------------------------------------------------

    def _required(self, name):
        return "\n".join(
            self.spec[name]["expectations"]["logContracts"]["required"])

    def _forbidden(self, name):
        return "\n".join(
            self.spec[name]["expectations"]["logContracts"]["forbidden"])

    def _window_resource(self, window_index, holder, resource="LiquidFuel"):
        window = savepatch.route_windows(self.lines)[window_index]
        for h in savepatch.child_nodes(self.lines, window, holder):
            for row in savepatch.child_nodes(self.lines, h, "RESOURCE"):
                if savepatch.get_value(self.lines, row, "name") == resource:
                    return float(savepatch.get_value(self.lines, row, "amount"))
        raise AssertionError("window %d has no %s %s row"
                             % (window_index, holder, resource))

    def _pickup_manifest(self):
        """The pickup manifest is window 0's TRANSPORT delta: what rover C gained
        across the hop-1 dock. Read from the bytes, never a literal."""
        return (self._window_resource(0, "UNDOCK_TRANSPORT_RESOURCES")
                - self._window_resource(0, "DOCK_TRANSPORT_RESOURCES"))

    def _delivery_manifest(self):
        """Window 1's ENDPOINT delta: what rover A gained across the hop-2 dock."""
        return (self._window_resource(1, "UNDOCK_ENDPOINT_RESOURCES")
                - self._window_resource(1, "DOCK_ENDPOINT_RESOURCES"))

    def _live_state(self, name):
        return {e["pid"]: e for e in savepatch.declared_live_state(
            self.spec[name].get("fixture") or {})}

    @staticmethod
    def _pinned_shortfalls(block):
        """Every `shortfall=<digits>\\.<digits>` literal in a required block, as
        the plain decimal string it pins (e.g. `154.39`)."""
        # The block is the PARSED token list, so a spec's `shortfall=154\\.39`
        # arrives here as `shortfall=154\.39`: one literal backslash, then a dot.
        return [m.group(1) + "." + m.group(2) for m in
                re.finditer(r"shortfall=([0-9]+)\\\.([0-9]+)", block)]

    # -- the fixture-derived cells ---------------------------------------

    def test_the_manifests_this_matrix_is_built_on_are_the_recorded_ones(self):
        # The two numbers every lane's arithmetic starts from, asserted against
        # the committed windows so a re-harvest that moved either reds HERE with
        # the cause named, rather than on a flight as a mystery gate finding.
        self.assertAlmostEqual(154.3999999999952, self._pickup_manifest(), places=9)
        self.assertAlmostEqual(200.0, self._delivery_manifest(), places=9)

    def test_every_pinned_origin_shortfall_is_the_fixtures_own_subtraction(self):
        """`RouteOriginCargoCheck.HasRequired` reports `need - stored`, so each
        lane's pinned shortfall is the recorded pickup manifest minus the tank
        that lane stages. Recomputed here from the bytes plus the spec's own
        declaration; the pinned digits must be a PREFIX of the result (the specs
        pin leading digits and regex the float tail)."""
        need = self._pickup_manifest()
        for name in ("RVR-10-rover-relay-c-origin-empty.toml",
                     "RVR-11-rover-relay-c-origin-partial.toml"):
            entry = self._live_state(name)[self.PID_B]
            stored = float(entry["resources"]["LiquidFuel"])
            want = repr(need - stored)
            pinned = self._pinned_shortfalls(self._required(name))
            self.assertTrue(pinned, "%s pins no shortfall at all" % name)
            for got in pinned:
                self.assertTrue(want.startswith(got),
                                "%s pins shortfall=%s but need %r - stored %r = %s"
                                % (name, got, need, stored, want))
            # All three producers (gate, hold, BLOCKED) must carry the SAME value:
            # a fix that plumbs it into one and not the others is the regression
            # the matrix is authored around.
            self.assertEqual(3, len(pinned),
                             "%s must pin the shortfall on all three producer "
                             "lines (gate / RecordHold / BLOCKED), found %d"
                             % (name, len(pinned)))

    def test_rvr8_pins_the_shortfall_left_by_its_own_first_cycle(self):
        """RVR-8 declares NO liveState: its second cycle meets whatever the first
        one left. Both defensible readings of that leftover - the window's own
        recorded post-pickup endpoint, and the live subtraction `200 - manifest` -
        agree to the pinned digits, so the pin holds under either."""
        need = self._pickup_manifest()
        recorded_left = self._window_resource(0, "UNDOCK_ENDPOINT_RESOURCES")
        live_left = self._window_resource(0, "DOCK_ENDPOINT_RESOURCES") - need
        name = "RVR-8-rover-relay-c-second-cycle-hold.toml"
        pinned = self._pinned_shortfalls(self._required(name))
        self.assertEqual(3, len(pinned), pinned)
        for got in pinned:
            self.assertTrue(repr(need - recorded_left).startswith(got), got)
            self.assertTrue(repr(need - live_left).startswith(got), got)
        self.assertEqual([], savepatch.declared_live_state(
            self.spec[name].get("fixture") or {}),
            "RVR-8 reaches its edge by PLAYING the fixture forward; a liveState "
            "block here would make that claim false")

    def test_the_spent_origin_tank_pins_accept_both_readings(self):
        """THE TOKEN THE FIRST CENSUS RED ON, and the cell that keeps the fix honest.

        The spent-origin tank has TWO defensible float tails and the flights
        measured both. The operator's hand-flown save recorded rover B's
        post-pickup endpoint as `45.59999999999814` - the result of KSP's own
        resource flow across many physics frames. A harness lane never replays
        that flow: it DEBITS the restored 200 by the recorded manifest in ONE
        write, so the live tank reads `200 - 154.3999999999952 =
        45.6000000000048`. Same number to four significant figures, different last
        digits, and `tankAfter=45\\.6 path=unloaded` matched neither (it is a
        PREFIX pin followed by a literal space, so the trailing digits break it).
        RVR-8's first census (`2026-09-03_1807`) and RVR-15's
        (`2026-09-03_1814`) both red on exactly that, with every other token -
        including all three `shortfall=108.79` pins - green.

        The re-pin is `45\\.[56][0-9]*`, which spans BOTH readings and nothing
        else near them, and this cell recomputes both from the fixture's own
        window rather than trusting the literal. The sibling cell above already
        asserted the SHORTFALL pins hold under either reading; this is the same
        claim for the TANK pins, which is the half that was missing when the two
        censuses flew."""
        need = self._pickup_manifest()
        recorded_left = self._window_resource(0, "UNDOCK_ENDPOINT_RESOURCES")
        live_left = self._window_resource(0, "DOCK_ENDPOINT_RESOURCES") - need
        # Neither reading may be the OTHER's exact repr, or the pin would be
        # trivially satisfiable and this cell would prove nothing.
        self.assertNotEqual(repr(recorded_left), repr(live_left))
        for name in ("RVR-8-rover-relay-c-second-cycle-hold.toml",
                     "RVR-15-rover-relay-c-destination-empty.toml"):
            tokens = [t for t in
                      self.spec[name]["expectations"]["logContracts"]["required"]
                      if "tankAfter=45" in t or "raw=45" in t]
            self.assertTrue(tokens, "%s pins no spent-origin tank at all" % name)
            for token in tokens:
                rx = re.compile(token)
                for reading in (recorded_left, live_left):
                    line = ("[Parsek][INFO][Logi] Origin debit: route=r1 origin=B "
                            "pid=90564594 resource=LiquidFuel requested=%r "
                            "debited=%r tankBefore=200 tankAfter=%r path=unloaded"
                            % (need, need, reading))
                    cause = ("[Parsek][VERBOSE][Route] PickupSourcesHaveCargo: "
                             "route 698efc9d short-cause=physical pid=90564594 "
                             "resource=LiquidFuel raw=%r netted=%r "
                             "reservingRouteId=<none> reservedByRoute=0"
                             % (reading, reading))
                    self.assertTrue(rx.search(line) or rx.search(cause),
                                    "%s: %r matches neither reading %r"
                                    % (name, token, reading))

    def test_every_declared_live_state_is_satisfiable_by_these_bytes(self):
        """The applier's own assertions, run at CI time instead of at stage time.
        A pid the save does not carry, a resource the vessel does not have, or an
        amount above `maxAmount` would abort a real run pre-boot as
        INVALID(staging); this cell turns that into a red here."""
        for name in self.MATRIX:
            entries = savepatch.declared_live_state(
                self.spec[name].get("fixture") or {})
            for entry in entries:
                patched, notes = savepatch.apply_live_state(
                    "\n".join(self.lines), [entry],
                    save_name="rover-relay-c-recorded")
                self.assertTrue(notes, "%s: %r patched nothing" % (name, entry))
                self.assertNotEqual("\n".join(self.lines), patched,
                                    "%s: %r is a no-op declaration" % (name, entry))

    def test_the_destination_lanes_stage_the_headroom_they_claim(self):
        """RVR-13 / RVR-14 / RVR-15 sample the destination axis at 0 / 100 / 400
        units of headroom against the recorded 200 delivery manifest, and RVR-7
        is the exact fit at 200. The gate refuses whenever headroom < manifest, so
        the first two must FORBID a delivery and the third must REQUIRE one."""
        manifest = self._delivery_manifest()
        capacity = 400.0
        want = {"RVR-13-rover-relay-c-destination-full.toml": 0.0,
                "RVR-14-rover-relay-c-destination-partial.toml": 100.0,
                "RVR-15-rover-relay-c-destination-empty.toml": 400.0}
        for name, headroom in want.items():
            staged = float(self._live_state(name)[self.PID_A]
                           ["resources"]["LiquidFuel"])
            self.assertAlmostEqual(headroom, capacity - staged, places=9, msg=name)
            if headroom < manifest:
                self.assertIn("Delivery write:", self._forbidden(name), name)
                self.assertIn("BLOCKED kind=DestinationFull", self._required(name), name)
            else:
                self.assertIn("Delivery write:", self._required(name), name)
                self.assertIn("BLOCKED kind=DestinationFull", self._forbidden(name), name)

    def test_rvr15_pins_the_empty_tank_write_the_fixture_predicts(self):
        """The lane's headline: the SAME manifest into an EMPTY tank, so
        `tankBefore=0 tankAfter=<manifest>` where RVR-7 measured
        `tankBefore=200 tankAfter=400`. Both columns move, both manifest columns
        do not - which is what proves the writer reads the live tank."""
        manifest = self._delivery_manifest()
        self.assertEqual(manifest, int(manifest))
        want = "requested=%d written=%d tankBefore=0 tankAfter=%d capacity=400" % (
            int(manifest), int(manifest), int(manifest))
        self.assertIn(want,
                      self._required("RVR-15-rover-relay-c-destination-empty.toml"))

    # -- the shape cells -------------------------------------------------

    def test_every_matrix_lane_addresses_the_relay_tree_and_no_other(self):
        for name in self.MATRIX:
            self.assertIn(self.builder.RELAY_TREE_ID, self.text[name], name)
            self.assertNotIn('tree = "%s"' % self.builder.ENDPOINT_A_TREE_ID,
                             self.text[name], name)
            self.assertNotIn('tree = "%s"' % self.builder.ENDPOINT_B_TREE_ID,
                             self.text[name], name)

    def test_every_matrix_lane_pins_the_seal_total_to_the_relay_tree_size(self):
        # The RVR-7 cell's reasoning, applied across the matrix: `total=N` counts
        # the sealed tree's RECORDING nodes, and a free literal there survived a
        # whole-suite mutation test once already.
        want = "sealslot complete mode=tree tree=%s total=%d sealed=0" % (
            self.builder.RELAY_TREE_ID, len(self.builder.RELAY_TREE_RECORDING_IDS))
        for name in self.MATRIX:
            self.assertIn(want, self._required(name), name)

    def test_every_matrix_lane_keeps_the_vacuity_forbids(self):
        for name in self.MATRIX:
            self.assertIn("routecommand rejected", self._forbidden(name), name)
            self.assertIn("\\[Parsek\\]\\[ERROR\\]", self._forbidden(name), name)

    def test_every_matrix_lane_expects_the_create_to_be_admitted(self):
        # The whole matrix rests on the SAME create succeeding: the live-state
        # edits touch FLIGHTSTATE only, so an analysis that answered differently
        # would mean the patcher reached the Parsek payload.
        for name in self.MATRIX:
            self.assertNotIn('expect = "REJECTED"', self.text[name], name)
            self.assertIn("refusal=None", self._required(name), name)

    def test_the_blocking_lanes_forbid_every_writer(self):
        """A blocked cycle emits NOTHING - no dispatch, no debit, no delivery.
        Forbidding all three is what turns a gate that degraded to best-effort
        into a red instead of a quieter green."""
        for name in self.BLOCKING:
            forbidden = self._forbidden(name)
            self.assertIn("dispatch fired", forbidden, name)
            self.assertIn("Origin debit:", forbidden, name)
            self.assertIn("Delivery write:", forbidden, name)

    def test_the_blocking_lanes_declare_the_inverse_cycle_counters(self):
        for name in self.BLOCKING:
            routes = self.spec[name]["expectations"]["routes"]
            self.assertEqual({"min": 0, "max": 0}, routes["completedCycles"], name)
            self.assertEqual({"min": 1, "max": 1}, routes["skippedCycles"], name)
            self.assertNotIn("gating", routes,
                             "%s must stay REPORT-ONLY: arming is a per-scenario "
                             "operator decision after a reading run" % name)

    def test_only_rvr8_drives_a_second_cycle(self):
        for name in self.MATRIX:
            sends = self.text[name].count('action = "send-once"')
            jumps = self.text[name].count('cmd = "TimeJump"')
            if name == "RVR-8-rover-relay-c-second-cycle-hold.toml":
                self.assertEqual(2, sends, name)
                self.assertEqual(2, jumps, name)
                self.assertEqual(1, self.text[name].count('action = "activate"'), name)
            else:
                self.assertEqual(1, sends, name)
                self.assertEqual(1, jumps, name)
                self.assertEqual(0, self.text[name].count('action = "activate"'), name)

    def test_rvr9_is_rvr7_with_only_the_clock_moved(self):
        """The lane's whole argument is that it differs from RVR-7 in ONE
        argument, so a divergence cannot be attributed to anything else."""
        with open(os.path.join(SCENARIOS_DIR, "RVR-7-rover-relay-c-dispatch.toml"),
                  "rb") as fh:
            rvr7 = tomllib.load(fh)
        rvr9 = self.spec["RVR-9-rover-relay-c-surface-cadence.toml"]
        self.assertEqual([], savepatch.declared_live_state(rvr9["fixture"]))
        a = [dict(s) for s in rvr7["driver"]["steps"]]
        b = [dict(s) for s in rvr9["driver"]["steps"]]
        self.assertEqual(len(a), len(b))
        differences = [(x, y) for x, y in zip(a, b) if x != y]
        self.assertEqual(1, len(differences),
                         "RVR-9 must differ from RVR-7 in exactly one step: %r"
                         % (differences,))
        self.assertEqual("TimeJump", differences[0][0]["cmd"])
        self.assertEqual("1100", differences[0][0]["args"]["ut"])
        self.assertEqual("600", differences[0][1]["args"]["ut"])

    def test_rvr9_forbids_the_orbital_phase_lock_on_this_tree(self):
        """Three forbids, all three MEASURED PRESENT on the pre-fix control run:
        the producer scoped to this tree, the METHOD name (durable across a
        rename of the APPLIED line) and the consequence."""
        forbidden = self._forbidden("RVR-9-rover-relay-c-surface-cadence.toml")
        self.assertIn("PhaseLock APPLIED.*%s" % self.builder.RELAY_TREE_ID, forbidden)
        self.assertIn("method=single-vessel-orbital", forbidden)
        self.assertIn("clock pre-anchor/degenerate", forbidden)

    def test_no_matrix_lane_pins_a_run_local_route_id_as_a_literal(self):
        """The H59 authoring defect: a route id is generated per run, so a literal
        one reds on every flight but the one it was harvested from. Every lane
        must regex it."""
        for name in self.MATRIX:
            for line in self._required(name).splitlines():
                self.assertNotIn("route 698efc9d", line, name)
                self.assertNotIn("route 5563af1d", line, name)

    def test_no_matrix_lane_pins_a_line_past_its_em_dash(self):
        """`ProcessLoopRoute`'s BLOCKED line and the phase-lock skip line both
        carry a literal em dash, which a token cannot match (the file is ASCII by
        house rule and the log renders the character itself). Every pin must stop
        before it."""
        for name in self.MATRIX:
            body = self._required(name) + self._forbidden(name)
            self.assertNotIn("emitted nothing", body, name)
            self.assertNotIn("degenerate -", body, name)


if __name__ == "__main__":
    unittest.main()
