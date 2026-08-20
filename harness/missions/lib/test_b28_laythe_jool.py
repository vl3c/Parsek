"""Unit tests for the b28_laythe_jool lane -- the RETURN-DIRECTION subject.

WHAT THIS FILE OWNS, and it is deliberately narrow. B28 is the smallest lane
addition the suite has taken in a while: a THIN ALIAS shell over `mlib.b5_decide`
(byte-identical in shape to `b25_laythe_orbit.py` / `b26_laythe_vall.py`) plus a
schema that is `b26_laythe_vall.schema.toml` PLUS EXACTLY ONE KEY, with exactly two
inherited declarations re-argued. So the cells here prove the four things that can
actually rot -- the alias, the schema's loadability, the committed param shape
validating through the REAL validator, and a missing required key being rejected --
plus the two lane-specific claims the mission docstring makes and nothing else in
the tree would catch:

  * `returnBodyName` is "Sun" and NOT "Jool". Setting it to the target body is not
    a tuning preference, it is an unflyable lane: `b5_decide`'s TARGET-FLYBY branch
    tests the leave-the-target-SOI guard BEFORE the capture-arming branch, so with
    the two strings equal the machine ASSERT-FAILs on the frame it arrives.
  * The `_b5_correction_via_bodies` PRECONDITION holds on this lane. That function
    narrows the no-encounter correction domain to `(return_body,)` and its own
    docstring is explicit that the "can only ever REMOVE a firing opportunity"
    argument rests on `return_body` being a member of `via_bodies` -- a
    precondition NO CODE ENFORCES. `test_shells.py`'s
    `test_the_return_body_is_a_member_of_the_via_bodies_on_every_lane` pins it for
    B7/B15/B16; this file pins it for B28, because a lane whose target is its
    origin's PARENT is exactly the shape where an author reaches for the parent's
    name and gets it wrong.

WHAT THIS FILE DOES NOT OWN. The `relayParkAtParent` MACHINE (its three load-time
implications, the structural stage-2 suppression, the `escapedHomeSoi` disjunct)
belongs to `mlib` and to `test_mlib.py`; the cells below touch it only where the
SPEC SHAPE is the subject -- i.e. where a wrong value in this lane's params would
be the defect. And nothing here asserts a ROUTING outcome for the produced
recording: V19M/V19T (reserved for this subject, not yet authored) read that off
the product and will gate on neither reading. See
`b28_laythe_jool.py`'s header and `docs/dev/autotest-roadmap.md` -> the gap
register -> **G2 - Return legs**.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import copy
import math
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
import b28_laythe_jool as b28   # noqa: E402

SHELL_PATH = os.path.join(_MISSIONS, "b28_laythe_jool.py")
SCHEMA_PATH = os.path.join(_MISSIONS, "b28_laythe_jool.schema.toml")
B26_SCHEMA_PATH = os.path.join(_MISSIONS, "b26_laythe_vall.schema.toml")
# The scenario TOML is authored separately (it is not this module's deliverable),
# so every cell below is written against the params dict rather than against the
# file. `SpecSyncTests` is the one cell that reads it, and it says out loud what it
# does when the file is not there yet.
SPEC_PATH = os.path.join(_HARNESS, "scenarios", "B28-laythe-jool-return.toml")

# THE COMMITTED PARAM SHAPE. Values that are TUNING (budgets, warp factors, park
# windows) are sized in the spec and deliberately NOT re-stated here -- a second
# copy of a moving number is a second thing to leave stale. What IS here is the
# SHAPE: every required key the schema declares, plus the seven values that are
# STRUCTURAL on this lane and would make it a different mission if they moved.
# The tuning entries carry plausible in-range numbers only so the dict validates.
B28_PARAMS = {
    # --- structural. Each of these is argued in b28_laythe_jool.py's header.
    "startInOrbit": True,
    "interplanetaryTransfer": True,
    "parentRelayTransfer": True,
    "relayParkAtParent": True,
    "homeBodyName": "Laythe",
    "targetBodyName": "Jool",
    "returnBodyName": "Sun",
    "viaBodyNames": ["Sun"],
    "captureEnabled": True,
    "transferMinApoapsisMeters": 0,
    # --- the relay's stage-1 sizing (the escape IS the whole transfer here).
    "escapeSoiSpeedMps": 450,
    "escapeMaxDeltaVMps": 700,
    "escapeNodeMinLeadSeconds": 300,
    "escapeTimeoutSeconds": 2400,
    # CORRECTIONS OFF, matching the committed spec, and it is not tuning: 0
    # disables BOTH correction phases outright. It matters to the via-body cells
    # below -- with corrections off the `_b5_correction_via_bodies` domain never
    # fires on this lane, so satisfying its precondition is a FORWARD guard for
    # whoever enables corrections later rather than a live one today. Satisfying
    # it costs nothing; violating it would leave a trap with no symptom.
    "courseCorrectPeriapsisMeters": 0,
    # --- required budgets / windows (tuning; the spec owns the real values).
    "planTimeoutSeconds": 300,
    "transferBurnTimeoutSeconds": 600000,
    "coastTimeoutSeconds": 4000000,
    "flybyTimeoutSeconds": 2500000,
    "targetPeriapsisFloorMeters": 1000000,
    "capturePlanTimeoutSeconds": 300000,
    "captureBurnTimeoutSeconds": 3000000,
    "parkMinPeriapsisMeters": 1000000,
    "parkMaxApoapsisMeters": 1500000000,
    "parkMaxEccentricity": 0.25,
    "parkDwellSeconds": 180,
    "parkTimeoutSeconds": 600,
    # --- required by the ORBIT-START entry gate (the fixture's Laythe park is
    # 87,931.3 x 56,240.3 m at ecc 0.0277, so the floor/ceiling bracket it).
    "startInOrbitMinPeriapsisMeters": 52000,
    "startInOrbitMaxEccentricity": 0.1,
}


def _read_toml(path):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def _nan_safe(rows):
    """Assertion rows with every NaN replaced by a sentinel string.

    REQUIRED, not a convenience: `evaluate_b5_assertions` reads NaN for every row
    whose telemetry never arrived (an unflown state reads NaN for the flyby
    periapsis floor, the park apsides, the tumble gate), and NaN != NaN by IEEE
    rule -- so a raw list comparison of two IDENTICAL row lists can never pass on
    an unflown state. Comparing the normalised forms keeps the cell an equality
    between the two code paths instead of quietly degrading into a
    length-and-names check."""
    def norm(value):
        if isinstance(value, float) and math.isnan(value):
            return "<nan>"
        if isinstance(value, dict):
            return {k: norm(v) for k, v in value.items()}
        if isinstance(value, (list, tuple)):
            return type(value)(norm(v) for v in value)
        return value
    return [(row.name, row.met, norm(row.value), norm(row.detail))
            for row in rows]


class ShellIsAThinAliasTests(unittest.TestCase):
    """THE ALIAS, PROVED BEHAVIOURALLY RATHER THAN BY READING THE FILE.

    A shell that grew its own decision -- a special case for the return
    direction, a tweak to the escape, anything -- would still LOOK like b25/b26
    at a glance. These cells drive the shell's three seams and the pure machine
    side by side and require identical answers, so a divergence reds here instead
    of on a Jool flight."""

    @classmethod
    def setUpClass(cls):
        cls.parsed = mlib.b5_params_from_dict(B28_PARAMS)

    def test_build_state_is_b5_initial_state_over_b5_params_from_dict(self):
        self.assertEqual(b28.build_state(B28_PARAMS),
                         mlib.b5_initial_state(mlib.b5_params_from_dict(B28_PARAMS)))

    def test_decide_is_b5_decide_across_the_frame_shapes_this_lane_sees(self):
        """FOUR FRAME SHAPES, not one, because a shell that special-cased a
        single phase would pass a one-frame comparison. The set walks the lane's
        own story: the fixture's Laythe park, an unreadable frame, the parent
        frame the escape delivers into, and the arrival reading Jool."""
        frames = (
            snap(ut=28_817_025.3, body="Laythe", situation="ORBITING",
                 apoapsis=87_931.3, periapsis=56_240.3, eccentricity=0.0277,
                 altitude=72_085.8),
            snap(ut=28_817_030.0, body="", situation=""),
            snap(ut=28_820_000.0, body="Jool", situation="ORBITING",
                 apoapsis=90_000_000.0, periapsis=1_200_000.0,
                 eccentricity=0.9, altitude=50_000_000.0),
            snap(ut=28_900_000.0, body="Jool", situation="ORBITING",
                 apoapsis=1_400_000_000.0, periapsis=1_000_000.0,
                 eccentricity=0.99, altitude=1_000_000.0),
        )
        for frame in frames:
            with self.subTest(body=frame.body, ut=frame.ut):
                state = b28.build_state(B28_PARAMS)
                self.assertEqual(b28.decide(state, frame),
                                 mlib.b5_decide(state, frame))

    def test_evaluate_is_evaluate_b5_assertions_with_the_state_carried_through(self):
        """The frames ride the seam UNUSED (every assertion is machine-carried
        evidence), so the cell that matters is that the STATE reaches the
        evaluator: a shell that dropped it would silently fail every carried row
        closed and look like a mission that never flew."""
        state = b28.build_state(B28_PARAMS)
        self.assertEqual(
            _nan_safe(b28.evaluate([], B28_PARAMS, state)),
            _nan_safe(mlib.evaluate_b5_assertions(
                [], mlib.b5_params_from_dict(B28_PARAMS),
                phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
                min_target_altitude=getattr(state, "min_target_altitude", None),
                state=state)))
        # ...and the row set is the one this lane's docstring enumerates, so a
        # dropped `state=` (which would still compare equal above, both sides
        # being wrong the same way) cannot pass unnoticed: escapedHomeSoi and
        # startedInHomeOrbit exist ONLY because the flags are armed.
        names = {row.name for row in b28.evaluate([], B28_PARAMS, state)}
        self.assertLessEqual({"escapedHomeSoi", "startedInHomeOrbit",
                              "capturedInTargetOrbit", "parkedStable",
                              "treeCommitted"}, names)

    def test_the_mission_spec_knobs_are_b25_and_b26s(self):
        """The three MissionSpec knobs are a CONTRACT, not defaults: rails warp
        permitted (every leg of this lane warps), the shared physics ceiling that
        bounds the correction-burn attitude flip, and NO settle tail (the SF-4
        contract -- the terminal frame is the frame AFTER the tree was committed,
        which is exactly the frame not to keep polling past)."""
        self.assertIsInstance(b28.SPEC, mission_runner.MissionSpec)
        self.assertEqual(b28.SPEC.name, b28.MISSION_NAME)
        self.assertTrue(b28.SPEC.allow_rails_warp)
        self.assertEqual(b28.SPEC.max_physics_warp, 4.0)
        self.assertEqual(b28.SPEC.settle_frames, 0)
        self.assertIs(b28.SPEC.build_state, b28.build_state)
        self.assertIs(b28.SPEC.decide, b28.decide)
        self.assertIs(b28.SPEC.evaluate, b28.evaluate)

    def test_the_module_declares_no_machine_of_its_own(self):
        """MUTATION: add a phase constant, a decision helper or a params
        dataclass to the shell and this reds. `mlib` owns the machine; a shell
        that starts carrying one has stopped being an alias, and the honest
        response is to move it into mlib where every lane's tests can see it."""
        own = {name for name in vars(b28)
               if not name.startswith("_")
               and name not in ("os", "sys", "List", "Optional", "annotations",
                                "mission_runner", "mlib")}
        self.assertEqual(
            own, {"MISSION_NAME", "SPEC", "build_state", "decide", "evaluate",
                  "make_control", "main"},
            "b28_laythe_jool.py grew a module-level name beyond the alias "
            "surface -- if it is machine, it belongs in mlib")


class SchemaTests(unittest.TestCase):
    """The declared schema is what `run.py` injects into `hlib.validate_spec`, so
    it is checked THROUGH the real validator, never restated."""

    @classmethod
    def setUpClass(cls):
        cls.schema = _read_toml(SCHEMA_PATH)
        cls.declared = cls.schema["params"]
        cls.b26_declared = _read_toml(B26_SCHEMA_PATH)["params"]

    def test_the_shell_and_the_schema_sit_side_by_side_under_the_mission_name(self):
        """`run.py::resolve_mission_schemas` registers a mission purely by the
        existence of `<name>.py` + `<name>.schema.toml`, and hlib rejects a spec
        naming a mission with no declared schema. A mission whose two files
        disagree on the name is a mission that validates NOTHING."""
        self.assertTrue(os.path.isfile(SHELL_PATH))
        self.assertTrue(os.path.isfile(SCHEMA_PATH))
        self.assertEqual(os.path.basename(SHELL_PATH),
                         "%s.py" % b28.MISSION_NAME)
        self.assertEqual(os.path.basename(SCHEMA_PATH),
                         "%s.schema.toml" % b28.MISSION_NAME)
        self.assertTrue(hlib._MISSION_RE.match(b28.MISSION_NAME),
                        "the mission name must be filename-safe or run.py "
                        "refuses to probe disk for it")

    def test_the_schema_is_b26s_plus_exactly_relay_park_at_parent(self):
        """THE FILE'S CENTRAL CLAIM, mechanised. B28 is B26's parent-relay mode
        with stage 2 deleted, and the schema header says so. If this lane ever
        needs a key B26 does not declare, that claim has stopped being true and
        must be re-argued rather than quietly patched (the B16 rule).

        MUTATION: add or drop any other key and this reds."""
        self.assertEqual(set(self.declared) - set(self.b26_declared),
                         {"relayParkAtParent"})
        self.assertEqual(set(self.b26_declared) - set(self.declared), set())

    def test_exactly_two_inherited_declarations_are_re_argued(self):
        """The other half of the claim: every INHERITED declaration is B26's
        verbatim except two, each argued at its own key and named in the schema
        header so a diff of the two files has no unexplained line.

        MUTATION: re-value any third bound and this reds -- which is the point.
        The house rule is "raise a bound HERE with its own comment, never push a
        value past it and never quietly relax one" (B21's header), and a silent
        third relaxation is exactly what that rule forbids."""
        diverged = {k for k in set(self.declared) & set(self.b26_declared)
                    if self.declared[k] != self.b26_declared[k]}
        self.assertEqual(diverged, {"padAlignEjection", "soiLeadSeconds"})
        # (1) The type-spelling correction. "boolean" is not one of
        # `hlib._check_param_type`'s six, so B26's declaration validates ANYTHING.
        self.assertEqual(self.declared["padAlignEjection"]["type"], "bool")
        self.assertEqual(self.b26_declared["padAlignEjection"]["type"], "boolean")
        # (2) The SOI-lead bound, adopted from B21/B22 rather than invented: B26's
        # 3,600 is a short-moon-hop number and this lane arrives at JOOL.
        self.assertEqual(self.declared["soiLeadSeconds"]["max"], 1_000_000.0)
        self.assertEqual(self.b26_declared["soiLeadSeconds"]["max"], 3600.0)
        self.assertEqual(
            self.declared["soiLeadSeconds"]["max"],
            _read_toml(os.path.join(_MISSIONS, "b22_jool_orbit.schema.toml"))
            ["params"]["soiLeadSeconds"]["max"],
            "the raised bound must be B22's verbatim -- same target body, same "
            "capture tail, already flown at 100,000")
        # min and type do NOT move with it.
        self.assertEqual(self.declared["soiLeadSeconds"]["min"],
                         self.b26_declared["soiLeadSeconds"]["min"])
        self.assertEqual(self.declared["soiLeadSeconds"]["type"],
                         self.b26_declared["soiLeadSeconds"]["type"])

    def test_every_declared_type_is_one_hlib_actually_checks(self):
        """THE TRAP THE CELL ABOVE IS A SPECIAL CASE OF. A misspelled `type` is
        not a load error -- `_check_param_type` returns [] for anything outside
        its six spellings -- so it is a declaration that silently validates
        everything, which is strictly worse than no declaration at all because it
        LOOKS checked. Pinned for the whole file, not just the one key."""
        accepted = {"float", "int", "number", "window", "list", "string", "bool"}
        for name, decl in sorted(self.declared.items()):
            with self.subTest(param=name):
                self.assertIn(
                    (decl or {}).get("type"), accepted,
                    "%s declares a type hlib._check_param_type does not know, "
                    "so its values are checked by nothing" % (name,))

    def test_relay_park_at_parent_is_declared_optional_and_bool(self):
        """OPTIONAL, like every other flag-gated key: an unarmed spec must not be
        forced to state a mode it does not use. `bool` because
        `hlib._check_param_type` rejects a non-bool under that spelling, and a
        string "false" would read TRUTHY in `b5_params_from_dict` -- the exact
        misplacement class `driver.skipTailOnUnmetMission` already documents."""
        self.assertEqual(self.declared["relayParkAtParent"],
                         {"required": False, "type": "bool"})

    def test_the_committed_param_shape_validates_clean(self):
        """THE LOAD-BEARING CELL: the shape this lane ships, through the REAL
        pure validator with the REAL parsed schema."""
        self.assertEqual(hlib._validate_mission_params(B28_PARAMS, self.schema), [])

    def test_every_required_key_is_rejected_when_missing(self):
        """TABLE-DRIVEN rather than one sampled key, so a future `required` flip
        is covered the moment it lands. Each subTest drops exactly one required
        key from the committed shape and requires the validator to name it.

        MUTATION: flip any `required = true` to false and its subTest reds."""
        required = sorted(k for k, v in self.declared.items()
                          if (v or {}).get("required"))
        self.assertTrue(required, "the schema declares no required params at "
                                  "all, which cannot be right")
        for key in required:
            with self.subTest(missing=key):
                self.assertIn(key, B28_PARAMS,
                              "the committed shape omits a REQUIRED key")
                short = copy.deepcopy(B28_PARAMS)
                del short[key]
                errs = hlib._validate_mission_params(short, self.schema)
                self.assertTrue(
                    any(key in e and "required param missing" in e
                        for e in errs),
                    "dropping %s produced %r" % (key, errs))

    def test_a_wrong_typed_value_is_rejected_for_the_new_flag(self):
        """The negative control for the flag's own declaration: without a working
        `type` the cell above would pass on a schema that checks nothing. A
        string is the realistic mistake (TOML `relayParkAtParent = "true"`), and
        it is the dangerous one -- it parses TRUTHY."""
        for bad in ("true", 1, [True]):
            with self.subTest(value=bad):
                errs = hlib._validate_mission_params(
                    dict(B28_PARAMS, relayParkAtParent=bad), self.schema)
                self.assertTrue(any("relayParkAtParent" in e for e in errs),
                                "%r was accepted; errs=%r" % (bad, errs))


class BodyNamingTests(unittest.TestCase):
    """THE TWO LANE-SPECIFIC CLAIMS the mission docstring makes about
    `returnBodyName`, which no schema check can reach because both are about the
    RELATIONSHIP between three string values."""

    @classmethod
    def setUpClass(cls):
        cls.parsed = mlib.b5_params_from_dict(B28_PARAMS)

    def test_the_return_body_is_not_the_target_body(self):
        """WHY "Sun" AND NOT "Jool", mechanised. `b5_decide`'s TARGET-FLYBY
        branch computes `return_body = _b5_return_body(params)` and its FIRST
        test is `capture_enabled and snapshot.body == return_body` ->
        ASSERT-FAIL, "left the target SOI without capturing". The capture-arming
        test on `target_body` is the NEXT branch down. Equal strings therefore
        kill the mission on the frame it ARRIVES -- deterministically, every
        attempt, with a message describing something that did not happen.

        MUTATION: set returnBodyName = "Jool" in the committed shape and this
        reds instead of the lane failing on a Jool flight."""
        self.assertEqual(self.parsed.target_body, "Jool")
        self.assertEqual(mlib._b5_return_body(self.parsed), "Sun")
        self.assertNotEqual(mlib._b5_return_body(self.parsed),
                            self.parsed.target_body,
                            "returnBodyName equals targetBodyName: TARGET-FLYBY "
                            "tests the leave-the-target-SOI guard BEFORE the "
                            "capture-arming branch, so this lane would "
                            "ASSERT-FAIL on arrival")
        # The empty default is the other way to get it wrong: it resolves to the
        # HOME body, which the relay's own load-time check already rejects, and
        # which is not the exit event either.
        self.assertTrue(self.parsed.return_body,
                        "an empty returnBodyName resolves to homeBodyName")

    def test_the_return_body_is_a_member_of_the_via_bodies(self):
        """THE PRECONDITION `_b5_correction_via_bodies`' SAFETY ARGUMENT RESTS
        ON, pinned here for B28 exactly as `test_shells.py`'s
        `test_the_return_body_is_a_member_of_the_via_bodies_on_every_lane` pins
        it for B7/B15/B16. That function narrows the no-encounter correction
        domain to `(return_body,)` on an interplanetary lane and its docstring is
        explicit that "a strict subset, so it can only ever REMOVE a firing
        opportunity" holds ONLY while `return_body` is a member of `via_bodies`.
        NO CODE ENFORCES IT.

        This lane is the shape most likely to break it: the target is the
        origin's PARENT, so an author reaching for a body name reaches for
        "Jool", and `viaBodyNames = ["Jool"]` with `returnBodyName = "Sun"` would
        make the narrowing ADD a firing opportunity in an SOI the coast never
        declared legal."""
        self.assertTrue(self.parsed.interplanetary_transfer)
        self.assertIn(self.parsed.return_body, self.parsed.via_bodies)
        self.assertTrue(
            set(mlib._b5_correction_via_bodies(self.parsed))
            <= set(self.parsed.via_bodies),
            "the correction-domain narrowing is no longer a subset of the "
            "declared coast bodies -- re-argue _b5_correction_via_bodies before "
            "shipping this lane")

    def test_the_coast_domain_deliberately_omits_the_parent(self):
        """STATED SO IT IS NOT MISTAKEN FOR AN OVERSIGHT and not 'fixed' by a
        future reader. `_b5_coast_bodies` here is ("", "Laythe", "Sun") and does
        NOT contain "Jool". That is harmless because COAST-TO-TARGET tests
        `snapshot.body == target_body` FIRST and hops into TARGET-FLYBY, so a
        Jool reading never reaches the coast-domain test. Adding "Jool" would
        legalise a Jool reading in a phase that should never see one."""
        domain = mlib._b5_coast_bodies(self.parsed)
        self.assertEqual(domain, ("", "Laythe", "Sun"))
        self.assertNotIn(self.parsed.target_body, domain)


class ParamShapeTests(unittest.TestCase):
    """The committed shape against `mlib`'s load-time contract. These cells are
    about THIS SPEC'S VALUES, not about the `relayParkAtParent` machine (which is
    mlib's and test_mlib.py's): each one names a value that, if it moved in this
    lane's params, would be the defect."""

    def test_the_committed_shape_parses_and_arms_the_mode(self):
        parsed = mlib.b5_params_from_dict(B28_PARAMS)
        self.assertTrue(parsed.start_in_orbit)
        self.assertTrue(parsed.interplanetary_transfer)
        self.assertTrue(parsed.parent_relay_transfer)
        self.assertTrue(parsed.relay_park_at_parent)
        self.assertTrue(parsed.capture_enabled)
        self.assertEqual(parsed.home_body, "Laythe")
        self.assertEqual(parsed.target_body, "Jool")

    def test_the_stage_two_floor_is_asserted_zero_and_written_out(self):
        """ASSERTED ZERO, and the key must be PRESENT. Stage 2 is structurally
        unreachable here (the COAST hand-off carries `not relay_park_at_parent`),
        so a positive floor is burn-done evidence no frame can ever read -- it
        would sit in the params looking like a live threshold to whoever debugs
        the flight next. ABSENT IS NOT 0: an omitted key parses to
        `b5_params_from_dict`'s own positive default, which is why the gate reads
        presence and why this cell checks both halves."""
        self.assertEqual(B28_PARAMS["transferMinApoapsisMeters"], 0)
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(
                dict(B28_PARAMS, transferMinApoapsisMeters=36_000_000))
        short = copy.deepcopy(B28_PARAMS)
        del short["transferMinApoapsisMeters"]
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(short)

    def test_the_flag_cannot_ride_alone(self):
        """Its two REQUIRES, checked from THIS lane's shape so a future edit that
        drops either companion key reds here. Alone the flag is INERT (there is
        no stage-2 hand-off to suppress) and the spec would fly the ordinary
        transfer machine while reading as if it had asked to park at a parent;
        without the capture tail the arrival takes TARGET-FLYBY's
        not-capture_enabled exit to RETURN and commits nothing while every flyby
        row still reads met."""
        for dropped in ("parentRelayTransfer", "captureEnabled"):
            with self.subTest(without=dropped):
                with self.assertRaises(ValueError):
                    mlib.b5_params_from_dict(dict(B28_PARAMS, **{dropped: False}))

    def test_the_home_body_has_the_gravity_row_the_escape_needs(self):
        """The relay computes its stage-1 node from `STOCK_BODY_GRAVITY`, and the
        load-time check runs `_body_gravity(homeBodyName)` so a missing row lands
        at spec-parse time (free) rather than mid-flight on the one frame that
        matters. B28 departs LAYTHE, the row B26 added -- the return direction
        needs no new constants, which is a real part of why this lane is cheap."""
        mu, radius, soi = mlib._body_gravity("Laythe")
        self.assertGreater(mu, 0.0)
        self.assertEqual(radius, 500_000.0)
        self.assertAlmostEqual(soi, 3_723_645.81113302, places=4)
        # The stage-1 burn-done threshold the mission docstring quotes: the
        # SOI-reach floor is an apoapsis ALTITUDE, not a radius.
        self.assertAlmostEqual(soi - radius, 3_223_645.81113302, places=4)


class SpecSyncTests(unittest.TestCase):
    """B28_PARAMS ABOVE IS ONLY WORTH ANYTHING IF IT MATCHES THE SPEC IT CLAIMS TO
    MIRROR -- the lesson `MissionParamsMatchTheSpecsTests` was written for, where
    a fixture dict had drifted seven keys from its spec and every claim computed
    over it was arithmetic on the wrong data.

    The scenario TOML is authored separately from this module. It IS committed
    today and these cells are ARMED; the skip guard stays because it is the honest
    behaviour if the spec is ever renamed or moved -- a cell that would otherwise
    pass vacuously on an empty dict, or blow up with a FileNotFoundError that reads
    like a product defect. The skip names the path it looked for, so a `-v` run says
    which file went missing rather than leaving a silent 's'."""

    def _spec_params(self):
        if not os.path.isfile(SPEC_PATH):
            raise unittest.SkipTest(
                "scenario spec not committed yet: %s -- this cell arms itself "
                "the moment it lands" % (SPEC_PATH,))
        return _read_toml(SPEC_PATH)["driver"]["missionParams"]

    def test_the_structural_values_match_the_committed_spec(self):
        """STRUCTURAL VALUES ONLY, by design. The budgets and warp factors are
        the spec's to tune and re-stating them here would be a second copy of a
        moving number; these seven are the ones that make B28 the lane it is, and
        a change to any of them is a change of mission rather than of tuning."""
        spec = self._spec_params()
        for key in ("startInOrbit", "interplanetaryTransfer",
                    "parentRelayTransfer", "relayParkAtParent",
                    "homeBodyName", "targetBodyName", "returnBodyName",
                    "viaBodyNames", "captureEnabled",
                    "transferMinApoapsisMeters"):
            with self.subTest(key=key):
                self.assertIn(key, spec)
                self.assertEqual(spec[key], B28_PARAMS[key])

    def test_the_committed_spec_validates_against_the_committed_schema(self):
        """The end-to-end statement: the shipped spec's own missionParams block
        through the real pure validator with the real parsed schema."""
        spec = self._spec_params()
        self.assertEqual(
            hlib._validate_mission_params(spec, _read_toml(SCHEMA_PATH)), [])

    def test_the_committed_spec_parses_through_mlibs_load_time_gates(self):
        """The layer the schema cannot reach: `b5_params_from_dict`'s
        cross-key implications (the relay's four, the park-at-parent's three).
        A spec can be schema-clean and still be refused here."""
        parsed = mlib.b5_params_from_dict(self._spec_params())
        self.assertTrue(parsed.relay_park_at_parent)
        self.assertEqual(parsed.transfer_min_apoapsis, 0.0)


if __name__ == "__main__":
    unittest.main()
