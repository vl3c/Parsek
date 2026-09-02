"""Fixture gates for `interbody-route-recorded`, the INTER-BODY supply-route host.

WHAT THIS FILE GUARDS, AND WHY IT CANNOT BE LEFT TO `test_saveparse.py`.
`RECORDED_FIXTURES` pins the shape every recorded fixture shares - trees,
recordings, terminal states, branch points, the sidecar floor, the route facet.
It does NOT pin the four things that make THIS save the G10 subject rather than
one more route fixture:

  * the inter-body ENDPOINT PAIR itself (ORIGIN bodyName Kerbin against the STOP
    ENDPOINT bodyName Duna). `RouteTrajectoryLineRenderer.ClassifyRouteScope`
    reads exactly that pair; if a re-harvest landed a same-body route the
    `routes.destinationBodies` census would still read `{Duna: 1, Mun: 1}` from
    the OTHER route and nothing would notice.
  * the SEAL STATE (zero `mergeState` lines = every recording Immutable), which
    is what the route create gate required and what a re-harvest from a
    mid-merge save would silently lose.
  * the dock/undock PAIR with `transferKind = DockingPort` - a claw stamps
    `Grapple`, a different `RouteConnectionKind` reading.
  * the stored `dispatchWindowPeriod = 0` line, which the 2026-09-02 scope fix
    deliberately LEFT ON THE WIRE (no schema move). A fixture that stopped
    carrying it would mean the codec changed under us.

IT CANNOT RE-RUN THE BUILD: the input is the operator's own hand-played campaign
save outside the repo, which will never be committed. The claims are made against
the RESULT.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves",
                           "interbody-route-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
SCENARIOS_DIR = os.path.join(_HARNESS, "scenarios")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_interbody_route_recorded.py")
    spec = importlib.util.spec_from_file_location(
        "build_interbody_route_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class InterbodyRouteRecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_interbody_route_recorded.py --check` INTO THE SUITE."""

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

    # Each of the four subject-defining windows is re-run ALONE below, so a
    # regression names the window rather than arriving inside a fifty-line save
    # diff (the rover/depot drift-test convention).

    def test_the_route_endpoints_are_on_two_different_bodies(self):
        problems = self.builder.verify_routes(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_tree_is_sealed(self):
        problems = self.builder.verify_seal_state(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_dock_undock_pair_is_a_docking_port_window(self):
        problems = self.builder.verify_route_windows(self.lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_stored_period_line_is_still_on_the_wire(self):
        """The scope fix demoted DispatchWindowPeriod WITHOUT a schema move.

        The renderer no longer reads it, but the codec still writes and reads it
        unconditionally, so an inter-body route in a committed save must still
        carry `dispatchWindowPeriod = 0`. This cell is the fixture-side half of
        `RouteCodecTests.Serialize_InterBodyRoute_StillWritesTheZeroPeriodLine_NoSchemaMove`:
        the C# cell proves the writer still emits it, this one proves a real
        save on disk still has it.
        """
        hits = [ln.strip() for ln in self.lines
                if ln.strip().startswith("dispatchWindowPeriod")]
        self.assertEqual(["dispatchWindowPeriod = 0",
                          "dispatchWindowPeriod = 0"], hits,
                         "both committed routes must still carry the stored "
                         "period line at 0")

    def test_the_builder_pins_agree_with_the_module_constants(self):
        """The two ids the file tree cell walks are the route's own members."""
        self.assertEqual(4, len(self.builder.INTERBODY_ROUTE_MEMBER_IDS))
        self.assertIn(self.builder.INTERBODY_ROUTE_ID,
                      self.builder.ROUTE_FACET_PINS["ids"])
        self.assertIn(self.builder.MUN_ROUTE_ID,
                      self.builder.ROUTE_FACET_PINS["ids"])
        self.assertEqual({"Kerbin": 2},
                         self.builder.ROUTE_FACET_PINS["originBodies"])
        self.assertEqual({"Duna": 1, "Mun": 1},
                         self.builder.ROUTE_FACET_PINS["destinationBodies"])


class InterbodyRouteSpecFixtureSyncTests(unittest.TestCase):
    """Every committed spec that stages this fixture, kept HONEST rather than restated.

    The rover file's hand-maintained roster went stale (four listed, seven staging),
    so the consumer set here is DERIVED from the scenarios directory: any spec whose
    PARSED `fixture.saveTemplate` names this fixture is a consumer, and the tuple
    below is only the floor.

    EVERY CLAIM IS MADE AGAINST THE PARSED TOML, NEVER AGAINST THE FILE TEXT. These
    specs carry long prose headers that quote the very tokens the cells below rule
    out - V26T's header explains at length why V18T's `skippedByStatus=[1-9]` forbid
    does NOT transfer to this host - and a text scan reads a comment as a
    declaration, which is how a guard passes GREEN on the wrong evidence.
    """

    EXPECTED_AT_LEAST = ("B32-interbody-route-scope.toml",
                         "V26M-interbody-route-map-lines.toml",
                         "V26T-interbody-route-ts-arrival.toml")
    SAVE_TEMPLATE = "fixtures/saves/interbody-route-recorded"

    @classmethod
    def setUpClass(cls):
        cls.specs = {}
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                cls.specs[name] = tomllib.load(fh)
        cls.consumers = {
            name: spec for name, spec in cls.specs.items()
            if ((spec.get("fixture") or {}).get("saveTemplate")
                == cls.SAVE_TEMPLATE)}

    def _log_contracts(self, spec):
        exp = spec.get("expectations") or {}
        lc = exp.get("logContracts") or {}
        return list(lc.get("required") or []), list(lc.get("forbidden") or [])

    def test_every_expected_consumer_spec_stages_this_fixture(self):
        for name in self.EXPECTED_AT_LEAST:
            self.assertIn(name, self.specs,
                          "%s is missing from harness/scenarios" % name)
            self.assertIn(name, self.consumers,
                          "%s does not stage this fixture" % name)

    def test_no_consumer_spec_expects_a_single_committed_route(self):
        """A lane over this host must expect TWO committed routes, not one.

        The save carries the Active Kerbin -> Duna subject AND a Paused
        Kerbin -> Mun route. A spec copied from V18T's single-route
        `[expectations.routes] count = { min = 1, max = 1 }` - or from its
        `routes=1 transitioned=0` token - would red on its first flight for a
        reason having nothing to do with what it measures.
        """
        for name, spec in sorted(self.consumers.items()):
            routes = (spec.get("expectations") or {}).get("routes") or {}
            count = routes.get("count")
            if isinstance(count, dict):
                self.assertNotEqual({"min": 1, "max": 1}, count, name)
            required, _ = self._log_contracts(spec)
            for pattern in required:
                self.assertNotIn("routes=1 transitioned=0", pattern, name)

    def test_no_consumer_spec_forbids_the_paused_route_skip(self):
        """V18T's `skippedByStatus=[1-9]` forbid does NOT transfer to this host.

        V18T can forbid it because its subject is the only committed route, so a
        non-zero skip means THE subject was skipped. Here one of the two routes
        is deliberately Paused, so a skip count of 1 is the EXPECTED reading and
        the forbid would red a correct run.
        """
        for name, spec in sorted(self.consumers.items()):
            _, forbidden = self._log_contracts(spec)
            for pattern in forbidden:
                self.assertNotIn("skippedByStatus", pattern, name)

    def test_every_consumer_spec_carries_the_pre_fix_negative_control(self):
        """The reading the subject produced BEFORE the scope fix stays forbidden.

        `malformed=[1-9]` in the draw summary and `scope=MalformedMixedBodies` at
        the classification site are what this save emitted on every drawn frame
        under the retired period-as-scope-flag contract. Carrying them as forbids
        means a regression reds on the lane itself rather than needing a separate
        control run.
        """
        for name, spec in sorted(self.consumers.items()):
            _, forbidden = self._log_contracts(spec)
            self.assertIn("Route line draw: .* malformed=[1-9]", forbidden, name)
            self.assertIn("scope=MalformedMixedBodies", forbidden, name)

    def test_every_consumer_spec_requires_the_interbody_scope_reading(self):
        """The token the whole package exists to make emittable.

        A consumer that does not require `scope=InterBody basis=Endpoints` is a
        lane over this subject that never states what makes it the subject.
        """
        for name, spec in sorted(self.consumers.items()):
            required, _ = self._log_contracts(spec)
            self.assertTrue(
                any("scope=InterBody basis=Endpoints" in p for p in required),
                "%s stages the inter-body fixture but requires no InterBody "
                "scope reading" % name)

    def test_no_consumer_spec_arms_a_render_composition_or_routes_gate(self):
        """NEVER FLOWN, so nothing is armed - and the sibling allowlists agree.

        `test_no_committed_spec_arms_gating` (save-parse) and the
        render-composition validator both refuse an arming that no reading run
        earned; this cell states the same thing locally, so a future edit that
        arms one of these three lanes reds beside the fixture it arms over.
        """
        for name, spec in sorted(self.consumers.items()):
            exp = spec.get("expectations") or {}
            for block in ("routes", "renderComposition",
                          "rewind", "recordings"):
                node = exp.get(block)
                if isinstance(node, dict):
                    self.assertNotIn("gating", node,
                                     "%s arms [expectations.%s]" % (name, block))


if __name__ == "__main__":
    unittest.main()
