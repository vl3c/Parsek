"""Unit tests for the b29_jool_kerbin lane -- the INBOUND INTERPLANETARY subject.

WHAT THIS FILE OWNS, and it is deliberately narrow. B29 is a THIN ALIAS shell over
`mlib.b5_decide` (byte-identical in shape to b25 / b26 / b28) plus a schema that is
`b28_laythe_jool.schema.toml` MINUS the six parent-relay keys PLUS exactly one new
one. So the cells here prove the four things that can actually rot -- the alias, the
schema's loadability, the committed param shape validating through the REAL
validator, and a missing required key being rejected -- plus the lane-specific
claims the mission docstring and the spec header make, which nothing else in the
tree would catch:

  * IT IS A TWO-STAGE RELAY NOW (flight 2, 2026-08-27). As authored it pinned "NOT
    a relay" - Jool is a planet, the moon-origin refusal is out of scope - and the
    refusal half held: MechJeb PLANNED from the planet park. But the PLAN from a
    590.3 Mm periapsis delivered an ecc-12.535 Kerbin arrival pricing the capture
    at 3,625 m/s, so the lane re-scoped onto B26's relay (the spec's FLIGHT 2
    LEDGER is the record). The cells now pin the relay ARMED, two-stage
    (`relayParkAtParent` unset), with all six keys declared in B28's shapes.
  * `ejectionEccFloor` RETIRED to 0 with the relay (B26's reason: a correct
    patched-conic escape from this park is BOUND, ecc 0.9424); the stage-2
    apoapsis floor took over as burn-done evidence and its weak-but-structural
    inbound shape is machine-checked here.
  * The `_b5_correction_via_bodies` PRECONDITION holds, and unlike B28 it is LIVE
    here rather than a forward guard, because this lane runs its corrections.
  * THE MUN-EXCLUSION IS ARITHMETIC, NOT A TOLERANCE. `parkMaxApoapsisMeters` sits
    below the near edge of the Mun's SOI band and the aimed apoapsis sits below
    that, so an orbit passing the park gate provably cannot reach the band. A cell
    recomputes the band from the same constants rather than trusting the comment.
  * THE PARK ECCENTRICITY CEILING COVERS THE ORBIT THE LANE AIMS AT. The obvious
    spec -- set the apoapsis, leave the park triple at every prior lane's 0.25 --
    deadlocks AFTER the capture burn is flown, and mlib now raises at load instead.

WHAT THIS FILE DOES NOT OWN. The ELLIPTICAL CAPTURE MACHINE (the node arithmetic,
the refusal ladder, the dispatch, the flag-off inertness) belongs to `mlib` and to
`test_capture_elliptical.py`; the cells below touch it only where the SPEC SHAPE is
the subject. And nothing here asserts a ROUTING outcome for the produced recording:
V20M / V20T / V20K (reserved for this subject, deliberately NOT authored ahead of
its first flight) read that off the product and will gate on neither reading. See
`b29_jool_kerbin.py`'s header and `docs/dev/autotest-roadmap.md` -> the gap register
-> **G2 - Return legs**.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import copy
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import hlib                     # noqa: E402
import mission_runner           # noqa: E402
import mlib                     # noqa: E402
import b29_jool_kerbin as b29   # noqa: E402

SHELL_PATH = os.path.join(_MISSIONS, "b29_jool_kerbin.py")
SCHEMA_PATH = os.path.join(_MISSIONS, "b29_jool_kerbin.schema.toml")
B28_SCHEMA_PATH = os.path.join(_MISSIONS, "b28_laythe_jool.schema.toml")
SPEC_PATH = os.path.join(_HARNESS, "scenarios", "B29-jool-kerbin-return.toml")

# The six PARENT-RELAY keys. AS AUTHORED this lane's schema deliberately did not
# declare them; flight 2 (2026-08-27) re-scoped the lane onto the relay and the
# schema now declares all six (B28's declarations verbatim), with the two-stage
# subset armed in the spec - `relayParkAtParent` stays declared-and-UNSET (the park
# is at KERBIN, the target, not at the parent).
RELAY_KEYS = ("parentRelayTransfer", "relayParkAtParent", "escapeSoiSpeedMps",
              "escapeMaxDeltaVMps", "escapeNodeMinLeadSeconds",
              "escapeTimeoutSeconds")
RELAY_KEYS_ARMED = ("parentRelayTransfer", "escapeSoiSpeedMps",
                    "escapeMaxDeltaVMps", "escapeNodeMinLeadSeconds",
                    "escapeTimeoutSeconds")

# THE COMMITTED PARAM SHAPE. Values that are TUNING (budgets, warp factors) are
# sized in the spec and deliberately NOT re-stated here -- a second copy of a moving
# number is a second thing to leave stale. What IS here is the SHAPE: every required
# key the schema declares, plus the values that are STRUCTURAL on this lane and
# would make it a different mission if they moved.
B29_PARAMS = {
    # --- structural. Each of these is argued in b29_jool_kerbin.py's header.
    "startInOrbit": True,
    "interplanetaryTransfer": True,
    "homeBodyName": "Jool",
    "targetBodyName": "Kerbin",
    "returnBodyName": "Sun",
    "viaBodyNames": ["Sun"],
    "captureEnabled": True,
    # --- the parent-relay block (the flight-2 re-scope; argued in the spec).
    "parentRelayTransfer": True,
    "escapeSoiSpeedMps": 450,
    "escapeMaxDeltaVMps": 500,
    "escapeNodeMinLeadSeconds": 300,
    "escapeTimeoutSeconds": 900000,
    # POSITIVE with the relay (the stage-2 burn-done floor, Sun frame); 0 as
    # authored. And the ecc floor RETIRED to 0 with the relay (a correct
    # patched-conic escape from this park is BOUND, ecc 0.9424).
    "transferMinApoapsisMeters": 50000000000,
    "ejectionEccFloor": 0,
    # --- the elliptical capture, and the three park values it is checked against.
    "captureEllipticalApoapsisMeters": 6000000,
    "courseCorrectPeriapsisMeters": 150000,
    "parkMaxApoapsisMeters": 8900000,
    "parkMaxEccentricity": 0.85,
    "parkMinPeriapsisMeters": 90000,
    # --- required budgets / windows (tuning; the spec owns the real values).
    "planTimeoutSeconds": 300000,
    "transferBurnTimeoutSeconds": 22000000,
    "coastTimeoutSeconds": 32000000,
    "flybyTimeoutSeconds": 400000,
    "targetPeriapsisFloorMeters": 80000,
    "capturePlanTimeoutSeconds": 300000,
    "captureBurnTimeoutSeconds": 3000000,
    "parkDwellSeconds": 180,
    "parkTimeoutSeconds": 600,
    # --- required by the ORBIT-START entry gate (the fixture's Jool park is
    # 584,321,095.005 x 584,330,474.175 m at ecc 7.9440625486254423e-06).
    "startInOrbitMinPeriapsisMeters": 250000000,
    "startInOrbitMaxEccentricity": 0.05,
}

# THE FIXTURE'S OWN PARK, read off `fixtures/saves/jool-park-nerv/persistent.sfs`.
# Restated here because the ORBIT-START cells below need something to bracket, and
# these three numbers are the fixture's identity rather than tuning.
FIXTURE_SMA = 590325784.58972526
FIXTURE_ECC = 7.9440625486254423e-06
JOOL_RADIUS = 6000000.0

# THE MUN BAND, recomputed rather than quoted. Both constants have in-repo sources
# (mlib.STOCK_BODY_GRAVITY's Mun row for the SOI, and its own Kerbin comment for the
# 12,000,000 m orbital radius the 7.01x ratio is taken against).
MUN_ORBIT_RADIUS = 12000000.0
KERBIN_RADIUS = 600000.0


def _read_toml(path):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


class AliasTests(unittest.TestCase):
    """The shell delegates and declares no machine of its own."""

    def test_build_state_is_b5_initial_state_over_b5_params_from_dict(self):
        state = b29.build_state(B29_PARAMS)
        expected = mlib.b5_initial_state(mlib.b5_params_from_dict(B29_PARAMS))
        self.assertEqual(state, expected)

    def test_decide_is_b5_decide_across_the_frame_shapes_this_lane_sees(self):
        params = mlib.b5_params_from_dict(B29_PARAMS)
        for frame in (
                snap(ut=0.0, body="Jool", altitude=584321095.0,
                     periapsis=584321095.0, apoapsis=584330474.0,
                     eccentricity=FIXTURE_ECC, situation="ORBITING"),
                snap(ut=1.0e6, body="Sun", altitude=1.0e10,
                     periapsis=1.0e10, apoapsis=6.8e10, eccentricity=0.6),
                snap(ut=2.0e7, body="Kerbin", altitude=8.0e7,
                     periapsis=150000.0, apoapsis=-4.0e5, eccentricity=2.56,
                     time_to_periapsis=30000.0),
        ):
            with self.subTest(body=frame.body):
                state = mlib.b5_initial_state(params)
                self.assertEqual(b29.decide(state, frame),
                                 mlib.b5_decide(state, frame))

    def test_evaluate_is_evaluate_b5_assertions_with_the_state_carried_through(self):
        params = mlib.b5_params_from_dict(B29_PARAMS)
        state = mlib.b5_initial_state(params)
        mine = b29.evaluate([], B29_PARAMS, state=state)
        theirs = mlib.evaluate_b5_assertions(
            [], params,
            phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
            min_target_altitude=getattr(state, "min_target_altitude", None),
            state=state)
        self.assertEqual([r.name for r in mine], [r.name for r in theirs])

    def test_the_mission_spec_knobs_are_the_capture_family_s(self):
        self.assertTrue(b29.SPEC.allow_rails_warp)
        self.assertEqual(b29.SPEC.max_physics_warp, 4.0)
        # The SF-4 contract: every assertion is machine-carried evidence, so a
        # settle tail only adds transient-failure surface past the committed tree.
        self.assertEqual(b29.SPEC.settle_frames, 0)
        self.assertEqual(b29.SPEC.name, "b29_jool_kerbin")

    def test_the_module_declares_no_machine_of_its_own(self):
        """A phase constant or a decision table appearing HERE would mean the lane
        stopped being an alias, which is the property the whole file rests on."""
        with open(SHELL_PATH, encoding="ascii") as handle:
            source = handle.read()
        for banned in ("def _decide", "PHASE_", "if state.phase ==",
                       "class B5", "Action("):
            self.assertNotIn(banned, source)


class SchemaTests(unittest.TestCase):
    """The declared schema is what `run.py` injects into `hlib.validate_spec`."""

    def setUp(self):
        self.schema = _read_toml(SCHEMA_PATH)
        self.b28 = _read_toml(B28_SCHEMA_PATH)

    def test_the_shell_and_the_schema_sit_side_by_side_under_the_mission_name(self):
        self.assertTrue(os.path.isfile(SHELL_PATH))
        self.assertTrue(os.path.isfile(SCHEMA_PATH))
        self.assertEqual(
            os.path.basename(SCHEMA_PATH),
            os.path.basename(SHELL_PATH)[:-3] + ".schema.toml")

    def test_the_schema_is_b28s_plus_exactly_one(self):
        """As authored this was 'B28's MINUS the six relay keys plus one'; the
        flight-2 re-scope restored the six (see the schema's bottom block for the
        re-argument), so the relation is now clean superset-by-one."""
        mine = set(self.schema["params"])
        theirs = set(self.b28["params"])
        self.assertEqual(theirs - mine, set(),
                         "B29 now declares everything B28 does")
        self.assertEqual(mine - theirs, {"captureEllipticalApoapsisMeters"},
                         "B29 must add exactly one key to the shared set")

    def test_all_six_relay_keys_are_declared_with_b28s_shapes(self):
        """The re-scope's declarations are B28's VERBATIM - same type, same
        range - so the family's bounds stay comparable."""
        for key in RELAY_KEYS:
            with self.subTest(key=key):
                self.assertIn(key, self.schema["params"])
                self.assertEqual(self.schema["params"][key],
                                 self.b28["params"][key])

    def test_every_declared_type_is_one_hlib_actually_checks(self):
        """A misspelled type is not a load error -- it is a declaration that
        silently validates EVERYTHING, which is worse than no declaration because
        it looks checked. (origin/main a79060a41 closed the spellings behind
        hlib.MISSION_PARAM_TYPES after B26 shipped a `boolean`.)"""
        for name, decl in sorted(self.schema["params"].items()):
            with self.subTest(param=name):
                self.assertIn(decl["type"], hlib.MISSION_PARAM_TYPES)

    def test_the_new_key_is_declared_optional_and_numeric(self):
        decl = self.schema["params"]["captureEllipticalApoapsisMeters"]
        self.assertFalse(decl["required"],
                         "the key must be OPTIONAL: it is off on every other lane "
                         "and required=true would break all of them")
        self.assertEqual(decl["type"], "number")
        self.assertEqual(decl["min"], 0.0)

    def test_the_committed_param_shape_validates_clean(self):
        self.assertEqual(hlib._validate_mission_params(B29_PARAMS, self.schema), [])

    def test_every_required_key_is_rejected_when_missing(self):
        required = [n for n, d in self.schema["params"].items() if d["required"]]
        self.assertTrue(required)
        for name in sorted(required):
            if name not in B29_PARAMS:
                continue
            with self.subTest(param=name):
                short = copy.deepcopy(B29_PARAMS)
                del short[name]
                self.assertTrue(hlib._validate_mission_params(short, self.schema),
                                "%s went missing and nothing complained" % (name,))

    def test_a_wrong_typed_value_is_rejected_for_the_new_key(self):
        for bad in ("6000000", True, None, [6000000]):
            with self.subTest(value=bad):
                params = dict(B29_PARAMS,
                              captureEllipticalApoapsisMeters=bad)
                self.assertTrue(
                    hlib._validate_mission_params(params, self.schema),
                    "a %r apoapsis validated clean" % (type(bad).__name__,))


class LaneShapeTests(unittest.TestCase):
    """The claims the mission docstring makes about THIS lane's params."""

    def test_it_is_a_two_stage_relay(self):
        """The flight-2 re-scope: parent-relay ARMED, park-at-parent OFF (the
        park is at KERBIN, the target - this is the TWO-STAGE lane, B26's shape,
        from the first planet park in the family)."""
        params = mlib.b5_params_from_dict(B29_PARAMS)
        self.assertTrue(params.parent_relay_transfer)
        self.assertFalse(params.relay_park_at_parent)
        for key in RELAY_KEYS_ARMED:
            self.assertIn(key, B29_PARAMS)
        self.assertNotIn("relayParkAtParent", B29_PARAMS)

    def test_the_three_body_names_are_distinct(self):
        """Jool / Kerbin / Sun. B28 needed five paragraphs on this because its
        target WAS its parent; here the point is simply that no two collide, so
        none of `b5_decide`'s body-equality branches can alias."""
        names = [B29_PARAMS["homeBodyName"], B29_PARAMS["targetBodyName"],
                 B29_PARAMS["returnBodyName"]]
        self.assertEqual(len(set(names)), 3, names)

    def test_the_return_body_is_a_member_of_the_via_bodies(self):
        """`_b5_correction_via_bodies` narrows the no-encounter correction domain
        to `(return_body,)` on an interplanetary lane, and its own docstring says
        the 'can only ever REMOVE a firing opportunity' argument holds ONLY while
        return_body is a member of via_bodies -- a precondition NO CODE ENFORCES.
        LIVE on this lane, unlike B28's: corrections are ON."""
        params = mlib.b5_params_from_dict(B29_PARAMS)
        self.assertIn(params.return_body, params.via_bodies)
        self.assertEqual(mlib._b5_correction_via_bodies(params),
                         (params.return_body,))
        self.assertGreater(params.course_correct_periapsis, 0.0,
                           "corrections are ON here, which is what makes the "
                           "precondition live rather than a forward guard")

    def test_the_via_bodies_name_no_jool_moon_and_no_kerbin_moon(self):
        """Both halves are derived in the spec header: the PROGRADE ejection at a
        park 3.28x Pol's orbit descends through no moon shell, and the Mun/Minmus
        shells the Kerbin arrival does cross are inside the TARGET's frame, which
        the target hop owns before the coast-domain test is reached."""
        params = mlib.b5_params_from_dict(B29_PARAMS)
        for moon in ("Laythe", "Vall", "Tylo", "Bop", "Pol", "Mun", "Minmus"):
            with self.subTest(moon=moon):
                self.assertNotIn(moon, params.via_bodies)
                self.assertNotIn(moon, mlib._b5_coast_bodies(params))

    def test_the_ejection_floor_retired_to_zero_with_the_relay(self):
        """As authored: 1.001, the B15 inward calibration for the plain planner's
        hyperbolic ejection. With the relay, stage 1 is a BOUND patched-conic
        escape (ecc 0.9424 at escapeSoiSpeedMps 450), so a hyperbolic floor is a
        number no correct stage-1 frame can satisfy - B26 retired it for exactly
        this reason and this lane follows."""
        params = mlib.b5_params_from_dict(B29_PARAMS)
        self.assertEqual(params.ejection_ecc_floor, 0.0)

    def test_the_stage2_apoapsis_floor_is_not_a_standalone_early_exit(self):
        """The relay mirror of the retired ecc-floor cell: on an INBOUND leg the
        pre-burn Sun orbit (a ~= Jool's 68.77 Gm) already clears the 50 Gm floor,
        so the floor alone must never exit the phase - the TRANSFER-BURN exit is
        `(consumed or stuck) and floor_met`, and the node must be CONSUMED
        first. This cell is the spec comment's 'weak-but-structural' claim,
        machine-checked."""
        params = mlib.b5_params_from_dict(B29_PARAMS)
        mid = snap(ut=100.0, body="Sun", altitude=68000000000.0,
                   periapsis=66000000000.0, apoapsis=69000000000.0,
                   eccentricity=0.02)
        self.assertTrue(
            mlib._b5_transfer_burn_done(params, mid,
                                        relay_stage=mlib.RELAY_STAGE_TRANSFER),
            "the stage-2 floor is met before the burn even starts")
        state = mlib.b5_initial_state(params)
        state = mlib.replace(state, phase=mlib.B5_TRANSFER_BURN,
                             planned_node_count=1,
                             relay_stage=mlib.RELAY_STAGE_TRANSFER)
        # node_count still 1 -> NOT consumed -> the machine must stay in the phase.
        pending = mlib.replace(mid, node_count=1)
        new_state, _actions = mlib.b5_decide(state, pending)
        self.assertEqual(new_state.phase, mlib.B5_TRANSFER_BURN)

    def test_the_park_window_excludes_the_mun_band_by_arithmetic(self):
        """Recomputed from the constants rather than trusting the spec comment."""
        mun_soi = mlib.STOCK_BODY_GRAVITY["Mun"][2]
        band_near_edge_alt = MUN_ORBIT_RADIUS - mun_soi - KERBIN_RADIUS
        self.assertLess(B29_PARAMS["parkMaxApoapsisMeters"], band_near_edge_alt,
                        "the park gate must sit BELOW the near edge of the Mun's "
                        "SOI band, so an orbit that passes it cannot reach the "
                        "band on any revolution")
        self.assertLess(B29_PARAMS["captureEllipticalApoapsisMeters"],
                        B29_PARAMS["parkMaxApoapsisMeters"],
                        "the aimed apoapsis must sit under the gate that has to "
                        "accept it")

    def test_the_park_eccentricity_ceiling_covers_the_orbit_the_lane_aims_at(self):
        """The trap this pins: every prior lane sets 0.25 and the mlib default is
        0.5, while the aimed ellipse is 0.7959 -- so 'set the apoapsis, leave the
        park triple alone' deadlocks AFTER the capture burn has been flown."""
        r_a = KERBIN_RADIUS + B29_PARAMS["captureEllipticalApoapsisMeters"]
        r_p = KERBIN_RADIUS + B29_PARAMS["courseCorrectPeriapsisMeters"]
        ecc = (r_a - r_p) / (r_a + r_p)
        self.assertAlmostEqual(ecc, 0.7959, places=4)
        self.assertLessEqual(ecc, B29_PARAMS["parkMaxEccentricity"])
        self.assertGreater(ecc, 0.5,
                           "if this ever drops under mlib's default ceiling the "
                           "load-time guard stops being load-bearing and this "
                           "cell should be re-argued rather than deleted")

    def test_the_arrival_periapsis_clears_kerbins_atmosphere(self):
        """70,000 m is Kerbin's atmosphere. Both the aimed periapsis and the park
        floor must clear it, and the flyby floor is the in-SOI guard."""
        for key in ("courseCorrectPeriapsisMeters", "parkMinPeriapsisMeters",
                    "targetPeriapsisFloorMeters"):
            with self.subTest(param=key):
                self.assertGreater(B29_PARAMS[key], 70000.0)

    def test_the_orbit_start_gate_admits_the_fixtures_own_park(self):
        """The gate must pass the bytes COLD. Its eccentricity ceiling in
        particular is 6,294x the park's own 7.94e-06."""
        r_pe = FIXTURE_SMA * (1.0 - FIXTURE_ECC)
        self.assertGreater(r_pe - JOOL_RADIUS,
                           B29_PARAMS["startInOrbitMinPeriapsisMeters"])
        self.assertLess(FIXTURE_ECC, B29_PARAMS["startInOrbitMaxEccentricity"])

    def test_the_committed_shape_parses_through_mlibs_load_time_gates(self):
        params = mlib.b5_params_from_dict(B29_PARAMS)
        self.assertEqual(params.home_body, "Jool")
        self.assertEqual(params.target_body, "Kerbin")
        self.assertTrue(params.interplanetary_transfer)
        self.assertTrue(params.capture_enabled)
        self.assertEqual(params.capture_elliptical_apoapsis, 6000000.0)

    def test_the_target_body_has_the_gravity_row_the_capture_needs(self):
        """`capture_node_plan` reads mu and R for the TARGET body -- the first
        consumer of STOCK_BODY_GRAVITY that is not the relay's escape."""
        mu, radius, _soi = mlib._body_gravity(B29_PARAMS["targetBodyName"])
        self.assertAlmostEqual(mu, 3.5316e12, delta=1e6)
        self.assertEqual(radius, KERBIN_RADIUS)


class SpecSyncTests(unittest.TestCase):
    """The committed scenario TOML, read as the source of truth it is."""

    def setUp(self):
        if not os.path.isfile(SPEC_PATH):
            self.skipTest("scenario spec not committed yet: %s" % (SPEC_PATH,))
        self.spec = _read_toml(SPEC_PATH)
        self.params = self.spec["driver"]["missionParams"]

    def test_the_spec_points_at_this_mission_and_the_stripped_fixture(self):
        self.assertEqual(self.spec["driver"]["mission"], "b29_jool_kerbin")
        self.assertEqual(self.spec["fixture"]["saveTemplate"],
                         "fixtures/saves/jool-park-nerv")

    def test_the_structural_values_match_the_committed_spec(self):
        """Only the STRUCTURAL keys, never the tuning ones -- a second copy of a
        moving number is a second thing to leave stale."""
        for key in ("startInOrbit", "interplanetaryTransfer", "homeBodyName",
                    "targetBodyName", "returnBodyName", "viaBodyNames",
                    "captureEnabled", "transferMinApoapsisMeters",
                    "ejectionEccFloor", "captureEllipticalApoapsisMeters",
                    "courseCorrectPeriapsisMeters", "parkMaxApoapsisMeters",
                    "parkMaxEccentricity") + RELAY_KEYS_ARMED:
            with self.subTest(param=key):
                self.assertEqual(self.params[key], B29_PARAMS[key])

    def test_the_committed_spec_validates_against_the_committed_schema(self):
        self.assertEqual(
            hlib._validate_mission_params(self.params, _read_toml(SCHEMA_PATH)), [])

    def test_the_committed_spec_parses_through_mlibs_load_time_gates(self):
        mlib.b5_params_from_dict(self.params)

    def test_the_spec_declares_the_render_composition_block_bare(self):
        """DECLARED (so the C# recorder arms at Awake) and BARE (so nothing gates
        on a window this never-flown lane cannot yet have measured)."""
        block = self.spec["expectations"]["renderComposition"]
        self.assertNotIn("gating", block)
        self.assertEqual(block, {})

    def test_the_spec_exports_the_manifest_immediately_before_teardown(self):
        cmds = [s.get("cmd") for s in self.spec["driver"]["steps"]]
        self.assertEqual(cmds[-2:], ["ExportRenderManifest", "FlushAndQuit"])

    def test_the_forbidden_tokens_cover_both_derived_absences(self):
        """The Jool moons (the departure crosses no shell) AND the Kerbin moons
        (the arrival crosses both shells and must not be captured by either)."""
        forbidden = " ".join(
            self.spec["expectations"]["logContracts"]["forbidden"])
        for moon in ("Laythe", "Vall", "Tylo", "Bop", "Pol", "Mun", "Minmus"):
            with self.subTest(moon=moon):
                self.assertIn(moon, forbidden)

    def test_the_required_tokens_name_both_seams_and_the_kerbin_terminal(self):
        required = self.spec["expectations"]["logContracts"]["required"]
        joined = " ".join(required)
        self.assertIn("Jool to Sun", joined)
        self.assertIn("Sun to Kerbin", joined)
        self.assertIn("terminalOrbitBody=Kerbin", joined)


if __name__ == "__main__":
    unittest.main()
