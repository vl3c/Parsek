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


if __name__ == "__main__":
    unittest.main()
