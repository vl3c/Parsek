"""Unit cells for the B-DOCK-2 second-dock harvest (mission `bdock_second_dock`,
scenario `BDOCK-2-second-dock-harvest`, fixture `bdock-second-dock-recorded`).

Three groups:

  1. THE PURE WRAPPER (`mlib.sdock_decide`). It must hand the whole Interceptor
     half to `bdock_decide` unchanged, replace exactly two seams (the START, where
     the booted fixture's active Station is captured instead of flown, and the END,
     where B-DOCK's TRANSFER entry becomes the background tail), and DEGRADE - never
     fail - when the background switch is refused.
  2. THE SHELL, driven end to end over a scripted flight with no krpc and no KSP.
  3. SPEC / SCHEMA SYNC: the harvest spec's mission params satisfy the declared
     schema, and the background subject pid names a vessel the booted fixture holds.

NO krpc, NO KSP, NO network.
"""

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

import mlib                        # noqa: E402
import bdock_second_dock           # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "BDOCK-2-second-dock-harvest.toml")
SCHEMA_PATH = os.path.join(_MISSIONS, "bdock_second_dock.schema.toml")
SOURCE_FIXTURE_SFS = os.path.join(_HARNESS, "fixtures", "saves", "bdock-recorded",
                                  "persistent.sfs")

SUBJECT = 1223410921

PARAMS = mlib.SDockParams(
    bdock=mlib.BDockParams(interceptor_apoapsis=90000.0,
                           interceptor_periapsis=90000.0,
                           launch_settle_debounce=2,
                           rendezvous_noprogress_frames=5),
    bg_subject_pid=SUBJECT,
)

# The Interceptor half, frame by frame, from B-DOCK's own happy-path script
# (test_mlib._bdock_walk_to), starting at INT-LAUNCH.
INTERCEPTOR_FRAMES = [
    snap(ut=170.0, situation="PRE_LAUNCH"),
    snap(ut=175.0, situation="PRE_LAUNCH"),                          # -> INT-ASCENT
    snap(ut=400.0, apoapsis=88000.0, mj_ascent_complete=True),       # -> INT-CIRCULARIZE
    snap(ut=450.0, periapsis=89000.0, vessel_count=2),               # -> INT-SEPARATE
    snap(ut=451.0, vessel_count=3, available_thrust=0.0),
    snap(ut=452.0, vessel_count=3, available_thrust=0.0),
    snap(ut=453.0, vessel_count=3, available_thrust=0.0),
    snap(ut=454.0, vessel_count=3, available_thrust=180000.0),
    snap(ut=455.0, vessel_count=3, available_thrust=180000.0),
    snap(ut=456.0, vessel_count=3, available_thrust=180000.0),       # -> INT-PHASING-ORBIT
    snap(ut=460.0),                                                  # -> SET-TARGET
    snap(ut=470.0, target_set=True),                                 # -> RENDEZVOUS
    snap(ut=480.0, mj_rendezvous_enabled=True, target_distance=5000.0),
    snap(ut=490.0, mj_rendezvous_enabled=False, target_distance=80.0),  # -> MATCH
    snap(ut=500.0, target_rel_speed=0.5),                            # -> DOCK
    snap(ut=505.0, target_distance=90.0),
    snap(ut=510.0, mj_docking_enabled=True, docking_state="Docking", target_distance=50.0),
    snap(ut=520.0, mj_docking_enabled=False, docking_state="Docked"),   # docked
]


def _switch_reply(ut, switched="true", active=SUBJECT, result="OK"):
    return snap(ut=ut, seam_command_result=result,
                seam_command_tag=mlib.SDOCK_SWITCH_TAG,
                seam_command_payload=(("switched", switched),
                                      ("activeVesselPid", str(active))))


def _walk_to_dock_settle(params=PARAMS):
    state = mlib.sdock_initial_state(params)
    per = []
    state, actions = mlib.sdock_decide(state, snap(ut=160.0))
    per.append((state.phase, actions))
    for f in INTERCEPTOR_FRAMES:
        state, actions = mlib.sdock_decide(state, f)
        per.append((state.phase, actions))
        if state.done or state.phase == mlib.SDOCK_DOCK_SETTLE:
            break
    return state, per


def _run_tail(state, frames):
    per = []
    for f in frames:
        state, actions = mlib.sdock_decide(state, f)
        per.append((state.phase, actions))
        if state.done:
            break
    return state, per


HAPPY_TAIL = [
    snap(ut=525.0),                 # DOCK-SETTLE dwell
    snap(ut=531.0),                 # -> BG-SWITCH (click)
    _switch_reply(532.0),           # -> BG-EVENT
    snap(ut=534.0),                 # settle
    snap(ut=538.0),                 # emit capture + engines on
    snap(ut=544.0),                 # -> BG-RETURN (switch home)
    snap(ut=555.0),                 # -> BG-BACKGROUND (engines off on the subject)
    snap(ut=566.0),                 # -> SDOCK-TERMINAL
]


class SDockStartSeamTests(unittest.TestCase):

    def test_prelaunch_captures_the_booted_station_and_launches_into_int_launch(self):
        state = mlib.sdock_initial_state(PARAMS)
        new, actions = mlib.sdock_decide(state, snap(ut=160.0))
        self.assertEqual(new.phase, mlib.BDOCK_INT_LAUNCH)
        self.assertEqual(new.inner.phase, mlib.BDOCK_INT_LAUNCH)
        self.assertEqual(actions, [Action(mlib.ACTION_CAPTURE_STATION),
                                   Action(mlib.ACTION_LAUNCH_VESSEL, text="Kerbal X")])

    def test_no_station_phase_is_ever_entered(self):
        state, _ = _walk_to_dock_settle()
        for station_phase in (mlib.BDOCK_STATION_ASCENT, mlib.BDOCK_STATION_CIRCULARIZE,
                              mlib.BDOCK_STATION_SEPARATE, mlib.BDOCK_STATION_ORBIT,
                              mlib.BDOCK_STATION_COMMIT):
            self.assertNotIn(station_phase, state.phases_reached)


class SDockDelegationTests(unittest.TestCase):

    def test_the_interceptor_half_is_bdock_decide_frame_for_frame(self):
        # Drive the wrapper and a bare B-DOCK machine (started at INT-LAUNCH) over
        # the same frames: every delegated frame's phase and actions must agree.
        wrapper = mlib.sdock_initial_state(PARAMS)
        wrapper, _ = mlib.sdock_decide(wrapper, snap(ut=160.0))
        bare = mlib._bdock_enter(mlib.bdock_initial_state(PARAMS.bdock),
                                 mlib.BDOCK_INT_LAUNCH, 160.0)
        for f in INTERCEPTOR_FRAMES[:-1]:
            wrapper, wa = mlib.sdock_decide(wrapper, f)
            bare, ba = mlib.bdock_decide(bare, f)
            self.assertEqual(wrapper.phase, bare.phase)
            self.assertEqual(wa, ba)

    def test_a_delegated_flake_is_mirrored_to_the_top_level(self):
        state = mlib.sdock_initial_state(PARAMS)
        state, _ = mlib.sdock_decide(state, snap(ut=160.0))
        # INT-LAUNCH budget is 300 s: never settling flakes the inner machine.
        state, _ = mlib.sdock_decide(state, snap(ut=1000.0, situation="FLYING"))
        self.assertTrue(state.done)
        self.assertEqual(state.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(state.flake_phase, mlib.BDOCK_INT_LAUNCH)


class SDockEndSeamTests(unittest.TestCase):

    def test_dock_completion_enters_the_tail_and_drops_the_transfer(self):
        state, per = _walk_to_dock_settle()
        self.assertEqual(state.phase, mlib.SDOCK_DOCK_SETTLE)
        self.assertTrue(state.inner.docked_confirmed)
        self.assertEqual(per[-1][1], [Action(mlib.ACTION_MJ_DISABLE_DOCKING),
                                      Action(mlib.ACTION_CAPTURE_HOME_VESSEL)])
        for _, actions in per:
            for a in actions:
                self.assertNotEqual(a.kind, mlib.ACTION_START_RESOURCE_TRANSFER)
                self.assertNotEqual(a.kind, mlib.ACTION_UNDOCK)


class SDockTailTests(unittest.TestCase):

    def test_happy_tail_clicks_acts_returns_and_acts_in_background(self):
        state, _ = _walk_to_dock_settle()
        state, per = _run_tail(state, HAPPY_TAIL)
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict)
        self.assertEqual(state.phase, mlib.SDOCK_TERMINAL)
        self.assertEqual(state.bg_switch_result, "OK")
        self.assertTrue(state.bg_event_emitted)
        self.assertTrue(state.bg_background_emitted)
        emitted = [a for _, acts in per for a in acts]
        click = emitted[0]
        self.assertEqual(click.kind, mlib.ACTION_PARSEK_SEAM_COMMAND)
        self.assertEqual(click.seam_verb, "SimulateStockSwitchClick")
        self.assertEqual(click.seam_args, (("site", "map"), ("pid", str(SUBJECT))))
        self.assertEqual(click.seam_tag, mlib.SDOCK_SWITCH_TAG)
        kinds = [a.kind for a in emitted]
        # Capture happens BEFORE the engines are touched, the return BEFORE the
        # background action.
        self.assertLess(kinds.index(mlib.ACTION_CAPTURE_BG_SUBJECT),
                        kinds.index(mlib.ACTION_SET_ENGINES_ACTIVE))
        self.assertLess(kinds.index(mlib.ACTION_SWITCH_TO_HOME_VESSEL),
                        kinds.index(mlib.ACTION_BG_SUBJECT_SET_ENGINES_ACTIVE))
        self.assertIn(Action(mlib.ACTION_BG_SUBJECT_SET_ENGINES_ACTIVE, value=0.0), emitted)
        self.assertIn(Action(mlib.ACTION_SET_ENGINES_ACTIVE, value=1.0), emitted)

    def test_dock_settle_waits_its_dwell_before_clicking(self):
        state, _ = _walk_to_dock_settle()
        state, actions = mlib.sdock_decide(state, snap(ut=525.0))
        self.assertEqual(state.phase, mlib.SDOCK_DOCK_SETTLE)
        self.assertEqual(actions, [])

    def test_a_stale_reply_from_another_tag_never_advances_the_switch(self):
        state, _ = _walk_to_dock_settle()
        state, _ = _run_tail(state, HAPPY_TAIL[:2])
        self.assertEqual(state.phase, mlib.SDOCK_BG_SWITCH)
        stale = snap(ut=532.0, seam_command_result="OK", seam_command_tag="other",
                     seam_command_payload=(("switched", "true"),
                                           ("activeVesselPid", str(SUBJECT))))
        state, _ = mlib.sdock_decide(state, stale)
        self.assertEqual(state.phase, mlib.SDOCK_BG_SWITCH)

    def test_a_refused_click_degrades_to_terminal_not_a_failure(self):
        for result in ("ERROR", "TIMEOUT"):
            state, _ = _walk_to_dock_settle()
            state, _ = _run_tail(state, HAPPY_TAIL[:2])
            state, actions = mlib.sdock_decide(state, _switch_reply(532.0, result=result))
            self.assertTrue(state.done)
            self.assertIsNone(state.verdict)
            self.assertEqual(state.phase, mlib.SDOCK_TERMINAL)
            self.assertEqual(state.bg_switch_result, result)
            self.assertEqual(actions, [])

    def test_a_switch_that_landed_elsewhere_is_named_and_degrades(self):
        state, _ = _walk_to_dock_settle()
        state, _ = _run_tail(state, HAPPY_TAIL[:2])
        state, _ = mlib.sdock_decide(state, _switch_reply(532.0, active=42))
        self.assertTrue(state.done)
        self.assertEqual(state.bg_switch_result, "WRONG-VESSEL")
        self.assertFalse(state.bg_event_emitted)

    def test_no_reply_within_budget_degrades(self):
        state, _ = _walk_to_dock_settle()
        state, _ = _run_tail(state, HAPPY_TAIL[:2])
        state, _ = mlib.sdock_decide(state, snap(ut=531.0 + 121.0))
        self.assertTrue(state.done)
        self.assertEqual(state.bg_switch_result, "BUDGET")

    def test_subject_zero_disables_the_tail(self):
        params = mlib.SDockParams(bdock=PARAMS.bdock, bg_subject_pid=0)
        state, _ = _walk_to_dock_settle(params)
        state, actions = mlib.sdock_decide(state, snap(ut=531.0))
        self.assertTrue(state.done)
        self.assertEqual(actions, [])
        self.assertEqual(state.bg_switch_result, "")


class SDockAssertionTests(unittest.TestCase):

    def test_all_three_met_on_the_happy_path(self):
        state, _ = _walk_to_dock_settle()
        state, _ = _run_tail(state, HAPPY_TAIL)
        rows = mlib.evaluate_sdock_assertions([], PARAMS, state.phases_reached, state)
        self.assertEqual([r.name for r in rows],
                         ["reachedInterceptorOrbit", "interceptorSeparated", "docked"])
        self.assertTrue(all(r.met for r in rows))

    def test_docked_unmet_without_the_inner_evidence(self):
        state = mlib.sdock_initial_state(PARAMS)
        rows = mlib.evaluate_sdock_assertions([], PARAMS, (mlib.BDOCK_DOCK,), state)
        self.assertFalse([r for r in rows if r.name == "docked"][0].met)


class SDockParamTests(unittest.TestCase):

    def test_params_from_dict_reads_the_tail_keys(self):
        p = mlib.sdock_params_from_dict({
            "bgSubjectPid": SUBJECT, "dockSettleSeconds": 12, "bgSettleSeconds": 6,
            "bgEventHoldSeconds": 7, "bgReturnSettleSeconds": 8,
            "bgBackgroundHoldSeconds": 9, "bgPhaseTimeoutSeconds": 99,
            "interceptorApoapsisMeters": 91000})
        self.assertEqual(p.bg_subject_pid, SUBJECT)
        self.assertEqual((p.dock_settle_seconds, p.bg_settle_seconds,
                          p.bg_event_hold_seconds, p.bg_return_settle_seconds,
                          p.bg_background_hold_seconds, p.bg_phase_timeout),
                         (12.0, 6.0, 7.0, 8.0, 9.0, 99.0))
        self.assertEqual(p.bdock.interceptor_apoapsis, 91000.0)


class SDockShellTests(unittest.TestCase):

    def test_shell_flies_the_scripted_mission_to_mission_ok(self):
        frames = [snap(ut=160.0)] + INTERCEPTOR_FRAMES + HAPPY_TAIL
        control = FakeMissionControl(frames)
        params = {"interceptorApoapsisMeters": 90000, "interceptorPeriapsisMeters": 90000,
                  "launchSettleDebounceFrames": 2, "bgSubjectPid": SUBJECT}
        code, result = run(bdock_second_dock.SPEC, params, control, budget=90000.0)
        self.assertEqual(result["mission"], "bdock_second_dock")
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)


class SDockSpecSyncTests(unittest.TestCase):

    def _spec(self):
        with open(SPEC_PATH, "rb") as fh:
            return tomllib.load(fh)

    def test_spec_names_this_mission_and_satisfies_the_schema(self):
        spec = self._spec()
        self.assertEqual(spec["driver"]["mission"], "bdock_second_dock")
        with open(SCHEMA_PATH, "rb") as fh:
            schema = tomllib.load(fh)["params"]
        params = spec["driver"]["missionParams"]
        for key, rule in schema.items():
            if rule.get("required"):
                self.assertIn(key, params, key)
        for key in params:
            self.assertIn(key, schema, "undeclared mission param %s" % key)

    def test_the_subject_pid_is_a_live_vessel_in_the_booted_fixture(self):
        spec = self._spec()
        pid = spec["driver"]["missionParams"]["bgSubjectPid"]
        self.assertEqual(pid, SUBJECT)
        with open(SOURCE_FIXTURE_SFS, "r", encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn("\t\t\tpersistentId = %d\n" % pid, text)


if __name__ == "__main__":
    unittest.main()
