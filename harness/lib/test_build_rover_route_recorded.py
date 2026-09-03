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
  * THE ENDPOINT INVENTORY IS REPAIRED (added 2026-09-01, after RVR-2 flight 1).
    The harvested endpoint carried two `STOREDPART` nodes a ROUTE DELIVERY had
    placed - the operator hand-drove a Send Once over these trees before the
    save was written - and with them the destination had no free slot, so RVR-2
    flight 1 answered `BLOCKED kind=DestinationFull
    reason=stored-part:evaScienceKit` at cycle 0 instead of delivering. The
    builder strips exactly the two the delivery's own `Inventory store:` lines
    name (`part7/mod1/slot1` evaChute, `part7/mod1/slot2` evaScienceKit) and
    leaves the second container verbatim. Without a pin here the repair is one
    re-harvest away from silently reverting, and the symptom would present as a
    route defect on a flight rather than as a fixture defect in this suite.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import math
import os
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

    def test_the_endpoint_inventory_repair_holds(self):
        """THE REPAIR PIN, run on its own so a revert names the INVENTORY rather
        than arriving inside a fifty-line save diff.

        RVR-2 flight 1 is the worked example this guards against: every driven
        step succeeded, the chain reached a confirmed dock crossing, and cycle 0
        still answered `BLOCKED kind=DestinationFull
        reason=stored-part:evaScienceKit` - because the harvested endpoint was
        already holding the output of a delivery the operator had hand-driven
        over the same trees before the save was written."""
        problems = self.builder.verify_endpoint_inventory(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_neither_route_delivered_stored_part_survives(self):
        """The absence stated over the WHOLE endpoint vessel and from the BYTES,
        not from the builder's own constants.

        The two nodes are identified POSITIVELY rather than by shape: the
        collected flight log names them as `part7/mod1/slot1` (evaChute) and
        `part7/mod1/slot2` (evaScienceKit), and
        `IDeliveryCapacityProbe.ProbeFirstEmptyInventorySlot` fixes what those
        indices mean ("vessel part order, then module order within the part,
        then ascending slot index"). Slot 0 is proven PRE-EXISTING by the same
        rule: the writer takes the first EMPTY slot, and no part before 7 carries
        an inventory module at all, so slot 1 can only have been chosen over slot
        0 if slot 0 was already occupied."""
        b = self.builder
        vessel = b.endpoint_vessel_node(self.lines)
        self.assertIsNotNone(vessel, "the endpoint vessel is missing")
        rows = {}
        for part_index, module_index, module, _pid in b._inventory_modules(
                self.lines, vessel):
            rows[(part_index, module_index)] = tuple(
                (slot, name, qty)
                for slot, name, qty, _span in b._stored_part_rows(self.lines, module))
        self.assertEqual(b.ENDPOINT_INVENTORY_AFTER, rows)
        target = b.ROUTE_DELIVERED_MODULE[:2]
        for slot, name in b.ROUTE_DELIVERED_SLOTS:
            self.assertNotIn(
                (slot, name), [(s, n) for s, n, _q in rows[target]],
                "part%d/mod%d/slot%s still holds the route-delivered %s"
                % (target[0], target[1], slot, name))

    def test_the_repaired_slot_headroom_makes_cycle_1_deliver_and_cycle_2_block(self):
        """The INVENTORY half of RVR-2's two-cycle chain, checked the way the
        LiquidFuel half is: the numbers and the conclusion against each other, so
        neither can move alone.

        Slot capacity is 3 per `ConformalStorageUnit`, MEASURED off the collected
        log's `ProbeLoadedFirstEmpty: ... modulesScanned=2 slotsOccupied=5
        slotsConsumed=1` (5 + 1 = 6 across the two modules) rather than read from
        the save, since `InventorySlots` is a part-config property."""
        b = self.builder
        vessel = b.endpoint_vessel_node(self.lines)
        modules = b._inventory_modules(self.lines, vessel)
        occupied = sum(len(b._stored_part_rows(self.lines, m))
                       for _p, _m, m, _pid in modules)
        capacity = len(modules) * b.ENDPOINT_INVENTORY_SLOTS_PER_MODULE
        free = capacity - occupied
        items = b.ROUTE_MANIFEST_INVENTORY_ITEMS
        self.assertGreaterEqual(
            free, items,
            "cycle 1 would BLOCK on slots: the endpoint is still polluted")
        self.assertLess(
            free - items, items,
            "cycle 2 would ALSO fit on slots, so the DestinationFull block would "
            "rest on the LiquidFuel half alone")

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

    Two instruments, deliberately. The forward cells are a TEXT check over the
    committed spec files rather than a TOML parse: the ids the specs must carry
    are the fixture's own constants, and a substring scan cannot be fooled by a
    spec that parses but names a different tree. The reverse cell (which specs
    on disk stage this fixture) PARSES `fixture.saveTemplate` instead: spec
    headers quote key lines verbatim, so a text scan would count a header that
    merely discusses the staging line as a consumer, and the wrong fix (adding
    that spec to SPECS) would then be green in both directions."""

    # Every committed spec that stages this fixture. `H56` is the odd one out and is
    # listed anyway: it reads NONE of the recorded corpus (its six `RouteDockCapture`
    # cells self-provision everything they touch) and boots these bytes purely for the
    # LIVE properties of the active vessel - a LANDED 17-part rover rather than a
    # PRELAUNCH pad rig, which is what drives the origin-proof probe's non-PRELAUNCH
    # branch. A spec that stages the fixture for a live reason still breaks if the
    # fixture is renamed, which is exactly what this class exists to catch. `H57`
    # boots it for the same live reason; `H58` and `H59` read the committed corpus
    # itself. All three are held to the same pairing.
    # RVR-16 and RVR-18 are the 2026-09-03 ENDPOINT MATRIX over this fixture: same
    # payload, same driver, a different LIVE FLIGHTSTATE declared per lane through
    # `[[fixture.liveState]]`. Their numbers are re-derived from these bytes by
    # `RoverRouteEndpointMatrixTests` below.
    SPECS = ("RVR-1-rover-route-proof.toml",
             "RVR-2-rover-route-create.toml",
             "RVR-3-route-lifecycle.toml",
             "RVR-16-rover-route-destination-slots-full.toml",
             "RVR-18-rover-route-endpoint-removed.toml",
             "H56-route-dock-capture-landed.toml",
             "H57-route-start-docked-origin-landed.toml",
             "H58-route-rewind-to-launch.toml",
             "H59-surface-route-map-lines.toml")

    FIXTURE_PATH = "fixtures/saves/rover-route-recorded"
    FIXTURE_LITERAL = '"%s"' % FIXTURE_PATH

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.text = {}
        for name in cls.SPECS:
            path = os.path.join(SCENARIOS_DIR, name)
            with open(path, "r", encoding="utf-8") as fh:
                cls.text[name] = fh.read()
        # The reverse direction: every committed spec whose PARSED staging key
        # names this fixture, whether SPECS lists it or not.
        cls.on_disk = []
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            staged = (spec.get("fixture", {}) or {}).get("saveTemplate")
            if staged == cls.FIXTURE_PATH:
                cls.on_disk.append(name)

    def test_every_consumer_spec_stages_this_fixture(self):
        for name in self.SPECS:
            self.assertIn(self.FIXTURE_LITERAL, self.text[name], name)

    def test_specs_is_exactly_the_consumers_on_disk(self):
        # SET EQUALITY against what is on disk, not the one-way check above: this
        # fires both when a listed member is removed/renamed AND when a new spec
        # stages the fixture without being added here.
        self.assertEqual(sorted(self.on_disk), sorted(self.SPECS),
                         "the committed specs staging rover-route-recorded differ "
                         "from SPECS in this test. A spec here but not on disk was "
                         "removed or renamed; a spec on disk but not here is new and "
                         "must be added to SPECS in the same commit")

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


class RoverRouteEndpointMatrixTests(unittest.TestCase):
    """THE RVR-16 / RVR-18 ENDPOINT MATRIX, checked against the FIXTURE'S OWN BYTES.

    Two lanes over one committed save, each staging a different LIVE FLIGHTSTATE
    through `[[fixture.liveState]]` and pinning tokens DERIVED from that state.
    The derivations are arithmetic and geometry over numbers this fixture
    carries, so they are re-derivable here - and that is the point: a pin that
    was hand-copied rots the first time the fixture is re-harvested, and it rots
    SILENTLY, because a wrong token reds as a product finding on a live flight
    rather than as a fixture finding in CI.

    THE TWO STRONGEST CELLS ARE
    `test_rvr16s_staged_tank_makes_the_slot_half_the_only_blocker` (the whole
    reason RVR-16 can measure the inventory branch at all) and
    `test_rvr18s_proximity_pick_is_the_nearest_surface_vessel` (which recomputes
    the fallback's own search over the committed coordinates). Nothing else in
    the tree connects those literals to the bytes."""

    RVR16 = "RVR-16-rover-route-destination-slots-full.toml"
    RVR18 = "RVR-18-rover-route-endpoint-removed.toml"

    ENDPOINT_PID = "2123618197"     # `rover fuel 0`, the window's transferTargetPid
    SUBSTITUTE_PID = "2875537755"   # `A`, the rover that physically docked

    # `RouteOrchestrator.SurfaceProximityRadiusMeters`. Spelled here rather than
    # read from C#: this suite is stdlib-only Python, and a change to the constant
    # that this file did not follow shows up as a geometry cell whose margins stop
    # matching the lane's claim.
    PROXIMITY_RADIUS_M = 500.0
    # Kerbin's radius, for degrees -> metres. Stock, and the only body in play.
    KERBIN_RADIUS_M = 600000.0
    # `RouteEndpointResolver.IsSurfaceSituation`.
    SURFACE_SITUATIONS = ("LANDED", "SPLASHED", "PRELAUNCH")

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.spec = {}
        cls.text = {}
        for name in (cls.RVR16, cls.RVR18):
            path = os.path.join(SCENARIOS_DIR, name)
            with open(path, "rb") as fh:
                cls.spec[name] = tomllib.load(fh)
            with open(path, "r", encoding="utf-8") as fh:
                cls.text[name] = fh.read()
        with open(FIXTURE_SFS, "r", encoding="utf-8", newline="") as fh:
            cls.lines = fh.read().replace("\r\n", "\n").split("\n")

    # -- helpers ---------------------------------------------------------

    def _required(self, name):
        return "\n".join(
            self.spec[name]["expectations"]["logContracts"]["required"])

    def _forbidden(self, name):
        return "\n".join(
            self.spec[name]["expectations"]["logContracts"]["forbidden"])

    def _live_state(self, name):
        return {e["pid"]: e for e in savepatch.declared_live_state(
            self.spec[name].get("fixture") or {})}

    def _manifest(self):
        """The delivery manifest's RESOURCE half: the window's own subtraction."""
        b = self.builder
        return (float(b.ROUTE_WINDOW_RESOURCE_ROWS["UNDOCK_ENDPOINT_RESOURCES"][1])
                - float(b.ROUTE_WINDOW_RESOURCE_ROWS["DOCK_ENDPOINT_RESOURCES"][1]))

    def _vessel(self, pid):
        matches = [(n, s) for n, vpid, s in savepatch.flightstate_vessels(self.lines)
                   if vpid == pid]
        self.assertEqual(1, len(matches), "pid %s is not unique in FLIGHTSTATE" % pid)
        return matches[0]

    def _free_slots(self, pid):
        _name, span = self._vessel(pid)
        modules = savepatch.inventory_modules(self.lines, span)
        occupied = 0
        for module in modules:
            for holder in savepatch.child_nodes(self.lines, module, "STOREDPARTS"):
                occupied += len(savepatch.child_nodes(self.lines, holder, "STOREDPART"))
        return (len(modules) * self.builder.ENDPOINT_INVENTORY_SLOTS_PER_MODULE
                - occupied)

    def _liquid_fuel(self, pid):
        _name, span = self._vessel(pid)
        rows = [r for part in savepatch.child_nodes(self.lines, span, "PART")
                for r in savepatch.child_nodes(self.lines, part, "RESOURCE")
                if savepatch.get_value(self.lines, r, "name") == "LiquidFuel"]
        self.assertEqual(1, len(rows),
                         "pid %s must carry exactly one LiquidFuel node for a "
                         "liveState `resources` declaration to be unambiguous" % pid)
        return (float(savepatch.get_value(self.lines, rows[0], "amount")),
                float(savepatch.get_value(self.lines, rows[0], "maxAmount")))

    def _endpoint_at_dock_node(self):
        windows = savepatch.route_windows(self.lines)
        self.assertEqual(1, len(windows), "the fixture must carry ONE route window")
        holders = savepatch.child_nodes(self.lines, windows[0], "ENDPOINT_AT_DOCK")
        self.assertEqual(1, len(holders))
        return holders[0]

    def _endpoint_at_dock(self):
        node = self._endpoint_at_dock_node()
        return {k: savepatch.get_value(self.lines, node, k)
                for k in ("vesselPersistentId", "bodyName", "latitude", "longitude",
                          "altitude", "isSurface")}

    def _surface_distance_m(self, lat_a, lon_a, alt_a, lat_b, lon_b, alt_b):
        """The separation the resolver's own comparison sees, to within the
        margins these cells rely on.

        `TrySurfaceFallbackPure` compares WORLD positions, so this is a stand-in
        rather than the identical arithmetic - which is why no cell below pins a
        distance to the metre and every one of them asserts an ORDERING or a
        margin instead (16x on the pick, 485 m against the radius)."""
        per_deg = self.KERBIN_RADIUS_M * math.pi / 180.0
        dlat = (lat_a - lat_b) * per_deg
        dlon = ((lon_a - lon_b) * per_deg
                * math.cos(math.radians((lat_a + lat_b) / 2.0)))
        dalt = alt_a - alt_b
        return math.sqrt(dlat * dlat + dlon * dlon + dalt * dalt)

    # -- RVR-16: the inventory half of the capacity gate ------------------

    def test_rvr16_declares_exactly_one_staged_tank(self):
        entries = self._live_state(self.RVR16)
        self.assertEqual([int(self.ENDPOINT_PID)], list(entries))
        entry = entries[int(self.ENDPOINT_PID)]
        self.assertEqual({"LiquidFuel"}, set(entry["resources"]))
        # `inventory` is deliberately absent (the `keep` default): the lane's slot
        # arithmetic is the COMMITTED fixture's own occupancy.
        self.assertNotIn("inventory", entry)

    def test_rvr16s_staged_tank_makes_the_slot_half_the_only_blocker(self):
        """The lane's whole premise, re-derived: with the declared tank BOTH
        cycles' resource half fits, so `FirstShortToken` cannot name a resource
        and must fall through to the inventory lines."""
        staged = float(self._live_state(self.RVR16)[int(self.ENDPOINT_PID)]
                       ["resources"]["LiquidFuel"])
        manifest = self._manifest()
        _stored, capacity = self._liquid_fuel(self.ENDPOINT_PID)
        self.assertLessEqual(staged, capacity, "the applier would refuse this")
        self.assertGreaterEqual(capacity - staged, manifest,
                                "cycle 0's resource half would not fit")
        self.assertGreaterEqual(
            capacity - (staged + manifest), manifest,
            "cycle 1 would block on LiquidFuel, which is RVR-2's reading and the "
            "thing this lane exists to take out of the way")

    def test_rvr16s_slot_arithmetic_fits_once_and_not_twice(self):
        free = self._free_slots(self.ENDPOINT_PID)
        items = self.builder.ROUTE_MANIFEST_INVENTORY_ITEMS
        self.assertGreaterEqual(free, items, "cycle 0 would not deliver at all")
        self.assertLess(free - items, items,
                        "cycle 1 would ALSO fit on slots, so the DestinationFull "
                        "block this lane pins would never fire")

    def _inventory_census(self, holder_name):
        """{(partName, identityHash): quantity} for one window inventory census."""
        window = savepatch.route_windows(self.lines)[0]
        out = {}
        for holder in savepatch.child_nodes(self.lines, window, holder_name):
            for item in savepatch.child_nodes(self.lines, holder, "ITEM"):
                key = (savepatch.get_value(self.lines, item, "partName"),
                       savepatch.get_value(self.lines, item, "identityHash"))
                out[key] = int(savepatch.get_value(self.lines, item, "quantity"))
        return out

    def _delivery_manifest_items(self):
        """The inventory manifest, DERIVED: the window's endpoint gains across the
        dock, keyed by (partName, identityHash)."""
        dock = self._inventory_census("DOCK_ENDPOINT_INVENTORY")
        undock = self._inventory_census("UNDOCK_ENDPOINT_INVENTORY")
        return {k: undock.get(k, 0) - dock.get(k, 0)
                for k in set(dock) | set(undock)
                if undock.get(k, 0) - dock.get(k, 0) > 0}

    def test_the_delivery_manifest_is_the_windows_own_delta(self):
        """THREE ITEMS, ONE UNIT EACH, and the station is one of them.

        This is the cell RVR-16's first census bought. The lane was authored
        against a TWO-item manifest, on the strength of RVR-2's create ACK
        (`stop-inventory=2`) and of the operator's own hand-flown cycle emitting
        two `Inventory store:` lines - and both readings were wrong about the
        manifest (the ACK field does not count what the census counts, and that
        cycle was SLOT-LIMITED against an already-delivered-to endpoint). The
        window said three all along. The station arrives under a NEW identity
        hash because stock rebuilds the snapshot on store, which is why it reads
        as a gain rather than as a no-op."""
        items = self._delivery_manifest_items()
        self.assertEqual(3, len(items),
                         "the window's endpoint delta is %r" % (items,))
        self.assertEqual({1}, set(items.values()), "every item must be one unit")
        self.assertEqual({"DeployedCentralStation", "evaChute", "evaScienceKit"},
                         {name for name, _hash in items})
        self.assertEqual(self.builder.ROUTE_MANIFEST_INVENTORY_ITEMS, len(items),
                         "the builder's item count and the window disagree")

    def test_rvr16_pins_a_store_token_for_every_manifest_item(self):
        """Cycle 0 must be shown to consume ALL THREE free slots, or cycle 1's
        zero-free-slot refusal is not a state the lane created."""
        required = self._required(self.RVR16)
        for name, _hash in self._delivery_manifest_items():
            self.assertIn("part=%s" % name, required,
                          "no `Inventory store:` token for manifest item %s" % name)

    def test_rvr16s_shortfall_token_is_the_first_item_it_stores(self):
        """With ZERO free slots EVERY inventory line is short, so
        `FirstShortToken` names the manifest's FIRST item - which is also the
        first slot `PrepareDelivery` hands out, i.e. the first `Inventory store:`
        line of cycle 0. This cell keeps those two halves of the spec in step.

        THE ORDER IS MEASURED, NOT DERIVED, and that is stated here so nobody
        re-derives it: `PrepareDelivery`'s own comment says the manifest arrives
        "already sorted by IdentityHash", and hash-ascending over the delta above
        is DeployedCentralStation (`5bcde9ad`) -> evaChute (`67867f65`) ->
        evaScienceKit (`796e8060`), which would make the STATION the shortfall.
        Two independent flights put the station LAST (RVR-16's census
        `2026-09-03_2007` and RVR-18's green run `2026-09-03_2011`, the latter
        into a different vessel), so the window's hashes do not predict the
        manifest's order and the spec pins what flew."""
        required = self.spec[self.RVR16]["expectations"]["logContracts"]["required"]
        stores = [t for t in required if "Inventory store:" in t]
        self.assertEqual(3, len(stores), stores)
        first = stores[0].split("part=")[-1].strip()
        for token in ("short=stored-part:%s" % first,
                      "detail=stored-part:%s" % first,
                      "reason=stored-part:%s" % first):
            self.assertIn(token, "\n".join(required),
                          "the shortfall tokens must name %r, the part the FIRST "
                          "pinned store line delivers" % first)
        # And the named part must be a real manifest member, not a typo that
        # happens to be consistent with itself.
        self.assertIn(first, {name for name, _h in self._delivery_manifest_items()})

    def test_rvr16_forbids_the_reading_an_unpatched_run_would_print(self):
        """The staging's falsification. On the UNPATCHED fixture cycle 1 blocks on
        LiquidFuel - RVR-2 measured exactly that - so a patch that silently did
        nothing produces the forbidden line."""
        self.assertIn("BLOCKED kind=DestinationFull reason=LiquidFuel",
                      self._forbidden(self.RVR16))

    def test_rvr16_pins_the_staged_tank_literal_in_its_delivery_token(self):
        """`tankBefore=<staged>` is the only token that proves the declared number
        reached the game, so the literal in the spec must be the literal the
        applier writes for the declared value."""
        staged = (self._live_state(self.RVR16)[int(self.ENDPOINT_PID)]
                  ["resources"]["LiquidFuel"])
        self.assertIn("tankBefore=%s" % savepatch.format_amount(staged),
                      self._required(self.RVR16))

    # -- RVR-18: the surface-proximity fallback ---------------------------

    def test_rvr18_removes_the_endpoint_and_clears_the_substitute(self):
        entries = self._live_state(self.RVR18)
        self.assertEqual(sorted([int(self.ENDPOINT_PID), int(self.SUBSTITUTE_PID)]),
                         sorted(entries))
        self.assertTrue(entries[int(self.ENDPOINT_PID)].get("remove"))
        self.assertEqual("clear", entries[int(self.SUBSTITUTE_PID)].get("inventory"))

    def test_rvr18s_removal_is_after_the_active_vessel(self):
        """The applier REFUSES a removal at or before `activeVessel`, so a lane
        declaring one would abort at staging on a prepared instance under the
        machine lock. Caught here instead."""
        vessels = savepatch.flightstate_vessels(self.lines)
        index = [i for i, (_n, vpid, _s) in enumerate(vessels)
                 if vpid == self.ENDPOINT_PID][0]
        self.assertGreater(index, savepatch._active_vessel_index(self.lines))

    def test_the_delivery_endpoint_carries_no_root_part_id(self):
        """Why the resolver's FIRST step cannot run here, from the bytes: the stop
        endpoint is `window.EndpointAtDock` verbatim, and only an ORIGIN endpoint
        built from a `RouteOriginProof` ever gets a `RootPartUId`. So the walk is
        pid -> proximity, which is what makes the removal reach the fallback."""
        keys = self._endpoint_at_dock()
        self.assertEqual(self.ENDPOINT_PID, keys["vesselPersistentId"])
        self.assertEqual("Kerbin", keys["bodyName"])
        self.assertEqual("True", keys["isSurface"])
        self.assertIsNone(savepatch.get_value(
            self.lines, self._endpoint_at_dock_node(), "rootPartUId"))

    def test_rvr18s_proximity_pick_is_the_nearest_surface_vessel(self):
        """The fallback's own search, recomputed. Every LANDED / SPLASHED /
        PRELAUNCH vessel is a candidate; the nearest one inside 500 m wins; the
        removed endpoint is not in the list because the lane deleted it."""
        keys = self._endpoint_at_dock()
        lat, lon, alt = (float(keys["latitude"]), float(keys["longitude"]),
                         float(keys["altitude"]))
        ranked = []
        for name, vpid, span in savepatch.flightstate_vessels(self.lines):
            if vpid == self.ENDPOINT_PID:
                continue  # removed by the lane
            if savepatch.get_value(self.lines, span, "sit") not in self.SURFACE_SITUATIONS:
                continue
            ranked.append((self._surface_distance_m(
                lat, lon, alt,
                float(savepatch.get_value(self.lines, span, "lat")),
                float(savepatch.get_value(self.lines, span, "lon")),
                float(savepatch.get_value(self.lines, span, "alt"))), vpid, name))
        ranked.sort()
        self.assertTrue(ranked, "no surface candidate at all")
        best, best_pid, _best_name = ranked[0]
        self.assertEqual(self.SUBSTITUTE_PID, best_pid,
                         "the nearest surface vessel to the recorded endpoint is "
                         "%r, not the pid RVR-18 pins" % (ranked[0],))
        self.assertLess(best, self.PROXIMITY_RADIUS_M,
                        "the fallback would MISS and the lane would measure "
                        "EndpointLost instead")
        self.assertIn("pid=%s" % self.SUBSTITUTE_PID, self._required(self.RVR18))
        # And the margin is what makes the pick robust rather than lucky.
        self.assertGreater(ranked[1][0], best * 4.0,
                           "the runner-up is close enough that terrain settling "
                           "could flip the pick; re-derive before flying")

    def test_the_removed_endpoint_was_itself_outside_the_radius(self):
        """A detail worth pinning because it inverts the intuition: the vessel the
        route NAMES sits further from the recorded dock coordinates than the radius
        allows, so proximity could never have re-found it. It resolves only because
        the pid step wins first."""
        keys = self._endpoint_at_dock()
        _name, span = self._vessel(self.ENDPOINT_PID)
        distance = self._surface_distance_m(
            float(keys["latitude"]), float(keys["longitude"]), float(keys["altitude"]),
            float(savepatch.get_value(self.lines, span, "lat")),
            float(savepatch.get_value(self.lines, span, "lon")),
            float(savepatch.get_value(self.lines, span, "alt")))
        self.assertGreater(distance, self.PROXIMITY_RADIUS_M)

    def test_rvr18s_substitute_fits_one_cycle_and_not_two(self):
        """The substitute's own arithmetic, which decides both of RVR-18's
        post-delivery tokens: its tank takes one manifest and not two (so cycle 1
        blocks on LiquidFuel), and its CLEARED containers hold two cycles' worth of
        items (so the inventory half is not what refuses)."""
        stored, capacity = self._liquid_fuel(self.SUBSTITUTE_PID)
        manifest = self._manifest()
        self.assertGreaterEqual(capacity - stored, manifest)
        self.assertLess(capacity - (stored + manifest), manifest)
        _name, span = self._vessel(self.SUBSTITUTE_PID)
        slots = (len(savepatch.inventory_modules(self.lines, span))
                 * self.builder.ENDPOINT_INVENTORY_SLOTS_PER_MODULE)
        self.assertGreaterEqual(
            slots, 2 * self.builder.ROUTE_MANIFEST_INVENTORY_ITEMS,
            "a cleared substitute must have room for both driven cycles, or the "
            "cycle-1 LiquidFuel token would be a slot refusal instead")
        self.assertIn("reason=LiquidFuel", self._required(self.RVR18))

    def test_rvr18_forbids_the_reading_an_unpatched_run_would_print(self):
        self.assertIn("dest=rover fuel 0", self._forbidden(self.RVR18))

    # -- both lanes ------------------------------------------------------

    def test_both_lanes_drive_rvr2s_steps(self):
        """The matrix design: the driver is the CONTROL and the liveState block is
        the variable. A lane that quietly changed a step would be measuring
        something RVR-2's green run does not underwrite."""
        with open(os.path.join(SCENARIOS_DIR, "RVR-2-rover-route-create.toml"),
                  "rb") as fh:
            base = tomllib.load(fh)["driver"]["steps"]
        for name in (self.RVR16, self.RVR18):
            steps = self.spec[name]["driver"]["steps"]
            self.assertEqual(len(base), len(steps), name)
            for want, got in zip(base, steps):
                self.assertEqual(want["cmd"], got["cmd"], name)
                if want["cmd"] == "RouteCommand":
                    self.assertEqual(want["args"]["action"], got["args"]["action"],
                                     name)
                if want["cmd"] == "TimeJump":
                    self.assertEqual(want["args"]["ut"], got["args"]["ut"], name)

    def test_neither_lane_arms_a_gating_save_parse_block(self):
        """Both declare `[expectations.routes]` and
        `[expectations.recordings.structure]` as READINGS. Arming either is an
        operator decision taken after a report-only run whose facets match, with
        its own `ARMED_ALLOWLIST` entry - and `test_hlib` reds if one is armed
        without it. This cell keeps the intent visible in the lane's own file.

        PARSED, not text-scanned: both headers use the word "gating" in prose to
        say they are NOT armed, and a substring scan would read that as the
        arming it is denying."""
        for name in (self.RVR16, self.RVR18):
            expectations = self.spec[name]["expectations"]
            blocks = {
                "routes": expectations.get("routes") or {},
                "recordings.structure":
                    (expectations.get("recordings") or {}).get("structure") or {},
            }
            for label, block in blocks.items():
                self.assertTrue(block, "%s declares no %s block at all"
                                % (name, label))
                self.assertNotIn("gating", block,
                                 "%s arms its %s block" % (name, label))

    def test_neither_lane_arms_render_composition_capture(self):
        for name in (self.RVR16, self.RVR18):
            self.assertNotIn("[expectations.renderComposition]", self.text[name])
            self.assertNotIn("ExportRenderManifest", self.text[name])


if __name__ == "__main__":
    unittest.main()
