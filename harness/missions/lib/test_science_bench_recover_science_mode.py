"""Unit cells for science_bench_recover's SCIENCE-MODE knobs: the independent
career-pool read, `recoverCreditPool` + `recoverMinScienceGain` (the RECOVERED
terminal measured on the science pool, for a stock Science game that has no
funds) and `transmitMaxExperiments` (transmit N experiments, recover the rest).

Five groups:

  1. THE DEFAULTS ARE THE OLD MACHINE. A cross-version replay against the fixed
     pre-change sha, over a success flight and three failure flights, comparing
     every phase, action and reason string; plus local cells that the defaults
     add no evidence token and no row key.
  2. THE SCIENCE-POOL RECOVERED TERMINAL. Credit across the recovery, a pre-loss
     only pair refused, a late transmit credit that must not pose as the
     recovery's, the dark-channel FLAKE vs the no-credit ASSERT-FAIL, and the
     funds pool never consulted.
  3. THE TRANSMIT CAP, on both sides of the seam: the machine carries it on every
     emission, the runner sends at most N over the whole run, and data left
     aboard trips no gate.
  4. THE INDEPENDENT POOL READ: a raising funds pool leaves science finite, one
     Warn per pool, and the vessel-lost path carries science in a Science game.
  5. SCHEMA / PARAMS: declarations, ADMIT refusals, and the builder.

NO krpc, NO KSP, NO network. Helpers come from the sibling suite through its
MODULE object, never by importing its TestCase classes (discovery would run
them twice).
"""

import dataclasses
import math
import os
import subprocess
import sys
import tomllib
import types
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
_REPO_ROOT = os.path.dirname(_HARNESS)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib                                  # noqa: E402
import mission_runner                        # noqa: E402
import science_bench_recover                 # noqa: E402
import test_science_bench_recover as base_t  # noqa: E402
from test_shells import FakeMissionControl, run  # noqa: E402

NAN = float("nan")

# The CS-1 shape: a Science game, transmit ONE of the two Goo units, recover the
# other. The funds floor is still required by the schema and is not consulted.
SCIENCE_KEYS = {"recoverCreditPool": "science", "recoverMinScienceGain": 1.0,
                "transmitMaxExperiments": 1}


def sparams(**over):
    out = base_t.params(**SCIENCE_KEYS)
    out.update(over)
    return out


def ssci(**kw):
    """A live frame of a SCIENCE game: the funds pool reads NaN on every frame."""
    kw.setdefault("career_funds", NAN)
    return base_t.sci(**kw)


def slost(ut, **kw):
    kw.setdefault("career_funds", NAN)
    return base_t.lost(ut, **kw)


def _science_game(frame):
    return dataclasses.replace(frame, career_funds=NAN)


def sflown(**over):
    """Flown and landed in a Science game, sitting in COLLECT."""
    state = mlib.sbr_initial_state(mlib.sbr_params_from_dict(sparams(**over)))
    for frame in base_t.FLIGHT_FRAMES + [base_t.LANDED_FRAME]:
        state, actions = mlib.sbr_decide(state, _science_game(frame))
    return state, actions


def s_in_transmit(**over):
    state, _ = sflown(**over)
    return base_t.drive(state,
                        ssci(ut=8.0, situation="LANDED", science_data_count=2,
                             career_science=10.0),
                        ssci(ut=9.0, situation="LANDED", science_data_count=2,
                             career_science=10.0))


def s_in_recover(**over):
    """At RECOVER entry: ONE experiment transmitted (data count now 1, not 0) and
    the science pool credited from 10 to 25."""
    return base_t.drive(s_in_transmit(**over),
                        ssci(ut=10.0, situation="LANDED", science_data_count=1,
                             career_science=25.0),
                        ssci(ut=11.0, situation="LANDED", science_data_count=1,
                             career_science=25.0))


def s_asked(state, ut=12.0, science=25.0):
    return base_t.drive(state,
                        ssci(ut=ut, situation="LANDED", science_data_count=1,
                             career_science=science,
                             vessel_recoverable=mlib.RECOVERABLE_YES),
                        ssci(ut=ut + 1.0, situation="LANDED", science_data_count=1,
                             career_science=science,
                             vessel_recoverable=mlib.RECOVERABLE_YES))


GRACE = mlib.SBR_RECOVER_CREDIT_GRACE_FRAMES


# ---------------------------------------------------------------------------
# 1. The defaults are the old machine.
# ---------------------------------------------------------------------------

_POOL_BASELINE_MODULE_NAME = "_mlib_baseline_pre_science_pool"


def _replay_script(tail):
    """The sibling suite's scripted L3 flight up to the frame before the
    recovery, plus ``tail``."""
    return list(base_t.DWELL_REPLAY_SCRIPT[:-1]) + list(tail)


_rf = base_t._rf

REPLAY_SCRIPTS = {
    "recovered": base_t.DWELL_REPLAY_SCRIPT,
    "no-credit": _replay_script(
        [_rf(ut=14.0 + i, vessel_lost=True, career_funds=1000.0, career_science=25.0,
             recover_request_result=mlib.RECOVER_REQUEST_ISSUED)
         for i in range(GRACE)]),
    "dark-pool": _replay_script(
        [_rf(ut=14.0 + i, vessel_lost=True, career_funds=NAN, career_science=25.0,
             recover_request_result=mlib.RECOVER_REQUEST_ISSUED)
         for i in range(GRACE)]),
    # The transmit never credits: a science pool stuck at 10 through the budget.
    "transmit-dry": (list(base_t.DWELL_REPLAY_SCRIPT[:10])
                     + [_rf(ut=10.0 + i * 30.0, situation="LANDED",
                            science_data_count=2, career_science=10.0)
                        for i in range(8)]),
}


class SbrSciencePoolCompatibilityTests(unittest.TestCase):
    """THE CROSS-VERSION REPLAY for this change: with none of the new keys
    declared (or with `recoverCreditPool = "funds"` spelled out), this module's
    SBR machine must be the pre-change one, frame for frame, reason string for
    reason string, over a success flight AND three failure flights (the failure
    reasons carry the evidence string, which is where a new token would leak).

    The baseline is a FIXED sha, for the reason the dwell replay's own note
    records: `b8de091` is the origin/main tip this change branched from, the last
    `mlib.py` with no `SBR_CREDIT_POOLS`. Same self-skip discipline (a shallow
    clone that lacks it), and the same rule: never advance the sha to make a
    failure go away."""

    BASELINE_SHA = "b8de091"

    @classmethod
    def setUpClass(cls):
        cls.baseline = None
        cls.skip_reason = None
        try:
            proc = subprocess.run(
                ["git", "show", "%s:harness/missions/lib/mlib.py" % (cls.BASELINE_SHA,)],
                cwd=_REPO_ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        except OSError as exc:                                   # pragma: no cover
            cls.skip_reason = "git is not runnable here (%r)" % (exc,)
            return
        if proc.returncode != 0 or not proc.stdout:              # pragma: no cover
            cls.skip_reason = ("no baseline mlib: %s does not resolve in this tree "
                               "(a shallow clone); NOT an expected state in CI, which "
                               "pins fetch-depth: 0" % (cls.BASELINE_SHA,))
            return
        module = types.ModuleType(_POOL_BASELINE_MODULE_NAME)
        module.__file__ = "<%s:harness/missions/lib/mlib.py>" % (cls.BASELINE_SHA,)
        # Registered before the exec: @dataclass resolves its module from
        # sys.modules (see the sibling replay's note).
        sys.modules[_POOL_BASELINE_MODULE_NAME] = module
        try:
            exec(compile(proc.stdout.decode("utf-8"), module.__file__, "exec"),
                 module.__dict__)
        except Exception as exc:                                 # pragma: no cover
            sys.modules.pop(_POOL_BASELINE_MODULE_NAME, None)
            cls.skip_reason = "baseline mlib did not import: %r" % (exc,)
            return
        cls.baseline = module

    @classmethod
    def tearDownClass(cls):
        sys.modules.pop(_POOL_BASELINE_MODULE_NAME, None)

    def setUp(self):
        if self.baseline is None:
            self.skipTest(self.skip_reason)

    def _replay(self, module, param_dict, script):
        snap_of = base_t.SbrDwellCompatibilityTests._snapshot
        shape_of = base_t.SbrDwellCompatibilityTests._action_shape
        state = module.sbr_initial_state(module.sbr_params_from_dict(param_dict))
        trace = []
        for fields in script:
            state, actions = module.sbr_decide(state, snap_of(module, fields))
            trace.append((state.phase, state.done, state.verdict,
                          state.loss_reason, state.flake_reason,
                          tuple(shape_of(mlib, self.baseline, a) for a in actions)))
        return state, trace

    @staticmethod
    def _spec_params(spec_name):
        with open(os.path.join(_HARNESS, "scenarios", spec_name), "rb") as fh:
            return tomllib.load(fh)["driver"]["missionParams"]

    def _param_sets(self):
        l3 = self._spec_params("L3-career-science-recover.toml")
        l6 = self._spec_params("L6-career-same-name-recover.toml")
        for key in ("recoverCreditPool", "recoverMinScienceGain",
                    "transmitMaxExperiments"):
            self.assertNotIn(key, l3, "L3 must stay on the defaults")
        explicit = dict(l3)
        explicit["recoverCreditPool"] = "funds"
        return {"L3": l3, "L6": l6, "L3+explicit-funds": explicit}

    def test_every_default_param_set_replays_identically_over_every_script(self):
        compared = 0
        for set_name, param_dict in self._param_sets().items():
            for script_name, script in REPLAY_SCRIPTS.items():
                head_state, head = self._replay(mlib, param_dict, script)
                base_state, base = self._replay(self.baseline, param_dict, script)
                self.assertEqual(len(base), len(head))
                for i, (b, h) in enumerate(zip(base, head)):
                    self.assertEqual(b, h, "%s / %s frame %d diverges from %s"
                                     % (set_name, script_name, i, self.BASELINE_SHA))
                base_rows = [r.to_dict() for r in self.baseline.evaluate_sbr_assertions(
                    [], self.baseline.sbr_params_from_dict(param_dict), base_state)]
                head_rows = [r.to_dict() for r in mlib.evaluate_sbr_assertions(
                    [], mlib.sbr_params_from_dict(param_dict), head_state)]
                self.assertEqual(base_rows, head_rows, "%s / %s" % (set_name, script_name))
                compared += 1
        self.assertEqual(12, compared)

    def test_the_failure_scripts_really_reach_their_named_terminals(self):
        # The replay is only as strong as the terminals it reaches: a script that
        # stalled mid-flight would agree trivially.
        l3 = self._param_sets()["L3"]
        expected = {"recovered": None,
                    "no-credit": "vessel-lost-without-recovery-credit",
                    "dark-pool": "career-pool-channel-dark",
                    "transmit-dry": "transmit-credited-no-science"}
        for name, token in expected.items():
            state, _ = self._replay(mlib, l3, REPLAY_SCRIPTS[name])
            self.assertTrue(state.done, name)
            if token is None:
                self.assertEqual(mlib.SBR_RECOVERED, state.phase)
            else:
                self.assertIn(token, state.loss_reason or state.flake_reason or "", name)

    def test_the_baseline_really_is_the_pre_change_one(self):
        # The mutation guard: a baseline that already had the pools would make
        # the replay compare the change against itself.
        self.assertFalse(hasattr(self.baseline, "SBR_CREDIT_POOLS"),
                         "the baseline at %s already has recoverCreditPool; do NOT "
                         "advance BASELINE_SHA" % (self.BASELINE_SHA,))
        self.assertTrue(hasattr(self.baseline, "sbr_recover_dwell_satisfied"),
                        "the baseline must carry the dwell, or the L6 set compares "
                        "nothing")


class SbrDefaultSurfaceTests(unittest.TestCase):
    """Local cells for the default surface, which run even without the baseline."""

    def test_the_defaults_build_the_funds_transmit_everything_machine(self):
        p = mlib.sbr_params_from_dict(base_t.PARAMS)
        self.assertEqual(mlib.SBR_CREDIT_POOL_FUNDS, p.recover_credit_pool)
        self.assertIsNone(p.transmit_max_experiments)
        self.assertEqual(0.1, p.recover_min_science)
        self.assertEqual(mlib.sbr_params_from_dict({}).recover_credit_pool, "funds")

    def test_the_default_transmit_action_carries_no_value(self):
        state = base_t.in_transmit()
        self.assertEqual(mlib.SBR_TRANSMIT, state.phase)
        state, actions = base_t.flown()
        state, actions = base_t.drive_actions(
            state,
            base_t.sci(ut=8.0, situation="LANDED", science_data_count=2),
            base_t.sci(ut=9.0, situation="LANDED", science_data_count=2))
        self.assertEqual([mlib.Action(mlib.ACTION_TRANSMIT_SCIENCE)], actions)
        self.assertIsNone(actions[0].value)

    def test_the_default_evidence_carries_no_new_token(self):
        state = base_t.drive(base_t._asked(base_t.in_recover()),
                             *[base_t.lost(14.0 + i, career_funds=1000.0)
                               for i in range(GRACE)])
        self.assertIn("vessel-lost-without-recovery-credit", state.loss_reason)
        self.assertIn("the career funds pool never rose by the required 100.000",
                      state.loss_reason)
        for token in ("recoverCreditPool=", "recoverScienceBaseline=",
                      "sciencePostLoss=", "transmitMaxExperiments="):
            self.assertNotIn(token, state.loss_reason)

    def test_the_default_recovered_row_has_no_pool_key(self):
        state = base_t.drive(base_t._asked(base_t.in_recover()),
                             base_t.lost(14.0, career_funds=1500.0))
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(base_t.PARAMS), state)}
        detail = rows["vesselRecoveredObserved"].detail
        self.assertNotIn("creditPool", detail)
        self.assertEqual(100.0, detail["required"])
        self.assertEqual(500.0, rows["vesselRecoveredObserved"].value)

    def test_a_science_game_on_the_default_pool_flakes_naming_funds(self):
        # WHY the knob exists: the default terminal cannot certify without funds.
        state = base_t.drive(s_asked(s_in_recover(recoverCreditPool="funds")),
                             *[slost(14.0 + i, career_science=40.0) for i in range(GRACE)])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("career-pool-channel-dark", state.flake_reason)
        self.assertIn("the career funds pool never produced", state.flake_reason)


# ---------------------------------------------------------------------------
# 2. The science-pool RECOVERED terminal.
# ---------------------------------------------------------------------------


class SbrSciencePoolRecoverTests(unittest.TestCase):

    def test_the_whole_tail_runs_in_a_science_game_with_funds_dark_throughout(self):
        state, _ = sflown()
        self.assertEqual(mlib.SBR_COLLECT, state.phase)
        state = s_in_recover()
        self.assertEqual(mlib.SBR_RECOVER, state.phase)
        self.assertIsNone(state.funds_baseline)
        self.assertEqual(25.0, state.recover_science_baseline)

    def test_a_science_credit_across_the_recovery_is_the_success_terminal(self):
        state = base_t.drive(s_asked(s_in_recover()), slost(14.0, career_science=40.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict)
        self.assertEqual(15.0, mlib._sbr_recovery_credit(state))

    def test_the_assertion_rows_all_meet_and_the_row_names_its_pool(self):
        state = base_t.drive(s_asked(s_in_recover()), slost(14.0, career_science=40.0))
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(sparams()), state)}
        self.assertTrue(all(o.met for o in rows.values()),
                        {k: o.met for k, o in rows.items()})
        rec = rows["vesselRecoveredObserved"]
        self.assertEqual(15.0, rec.value)
        self.assertEqual("science", rec.detail["creditPool"])
        self.assertEqual(1.0, rec.detail["required"])
        self.assertEqual(25.0, rec.detail["baseline"])
        self.assertEqual(40.0, rec.detail["final"])
        # The partial transmit left data aboard; the collect row still reads the MAX.
        self.assertEqual(2, rows["scienceCollectedObserved"].value)

    def test_a_pre_loss_only_pair_never_certifies(self):
        # Asked, issued, the science pool readable before the loss and DARK on
        # every vessel-gone frame: nothing was observed ACROSS the recovery.
        state = s_asked(s_in_recover())
        state = base_t.drive(state, ssci(ut=13.5, situation="LANDED",
                                         career_science=25.0,
                                         recover_request_result=mlib.RECOVER_REQUEST_ISSUED))
        self.assertTrue(state.recover_issued)
        self.assertIsNone(mlib._sbr_recovery_credit(state))
        state = base_t.drive(state, *[slost(14.0 + i, career_science=NAN)
                                      for i in range(GRACE)])
        self.assertNotEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("career-pool-channel-dark", state.flake_reason)
        self.assertIn("the career science pool never produced", state.flake_reason)
        self.assertIn("recoverCreditPool=science", state.flake_reason)

    def test_a_readable_unmoved_science_pool_is_the_break_up_terminal(self):
        state = base_t.drive(s_asked(s_in_recover()),
                             *[slost(14.0 + i, career_science=25.0) for i in range(GRACE)])
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-without-recovery-credit", state.loss_reason)
        self.assertIn("the career science pool never rose by the required 1.000",
                      state.loss_reason)

    def test_one_frame_short_of_the_grace_condemns_nothing(self):
        state = base_t.drive(s_asked(s_in_recover()),
                             *[slost(14.0 + i, career_science=25.0)
                               for i in range(GRACE - 1)])
        self.assertFalse(state.done)
        # And a credit landing on the last grace frame still certifies.
        state = base_t.drive(state, slost(30.0, career_science=40.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)

    def test_a_late_transmit_credit_cannot_pose_as_the_recovery_credit(self):
        # The science pool rises on LIVE RECOVER frames before the issue (a
        # transmit that completed after the TRANSMIT gate let go). The baseline
        # follows it, so the recovery that then credits nothing is condemned. A
        # baseline frozen on RECOVER entry would have certified this break-up.
        state = s_asked(s_in_recover(), science=40.0)
        self.assertEqual(40.0, state.recover_science_baseline)
        state = base_t.drive(state, *[slost(14.0 + i, career_science=40.0)
                                      for i in range(GRACE)])
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-without-recovery-credit", state.loss_reason)

    def test_a_reading_after_the_issue_is_never_absorbed_into_the_baseline(self):
        # The mirror of the cell above: a LIVE frame that already carries the
        # ISSUED stamp was read after Recover() was called, so it is post-event.
        state = s_asked(s_in_recover())
        state = base_t.drive(state, ssci(ut=13.5, situation="LANDED", career_science=40.0,
                                         recover_request_result=mlib.RECOVER_REQUEST_ISSUED))
        self.assertEqual(25.0, state.recover_science_baseline)
        state = base_t.drive(state, slost(14.0, career_science=40.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)

    def test_a_declined_ask_keeps_the_baseline_live(self):
        state = s_asked(s_in_recover())
        state = base_t.drive(state, ssci(ut=13.5, situation="LANDED", career_science=30.0,
                                         recover_request_result=mlib.RECOVER_REQUEST_DECLINED))
        self.assertTrue(state.recover_declined)
        self.assertEqual(30.0, state.recover_science_baseline)

    def test_a_zero_science_credit_never_certifies_even_at_a_hand_built_zero_floor(self):
        p = dataclasses.replace(mlib.sbr_params_from_dict(sparams()),
                                recover_min_science=0.0)
        self.assertFalse(mlib._sbr_recover_credit_met(p, 0.0))
        self.assertFalse(mlib._sbr_recover_credit_met(p, -1.0))
        self.assertFalse(mlib._sbr_recover_credit_met(p, None))
        self.assertTrue(mlib._sbr_recover_credit_met(p, 0.5))
        # Under the funds pool a 0.0 floor still certifies a 0.0 credit (unchanged).
        f = mlib.sbr_params_from_dict(base_t.params(recoverMinFundsGain=0.0))
        self.assertTrue(mlib._sbr_recover_credit_met(f, 0.0))

    def test_the_floor_is_the_science_one(self):
        state = base_t.drive(s_asked(s_in_recover(recoverMinScienceGain=20.0)),
                             *[slost(14.0 + i, career_science=40.0) for i in range(GRACE)])
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("required 20.000", state.loss_reason)

    def test_the_funds_pool_is_never_consulted(self):
        # A CAREER save under the science pool: funds rising across the recovery
        # with science flat is still no recovery credit for THIS pool. (The asks
        # are driven with the credited 25.0 pool; the sibling `_asked` frames read
        # the bare 10.0 default, which would fake a 15.0 credit here.)
        def career_asked(state):
            return base_t.drive(state, *[
                base_t.sci(ut=12.0 + i, situation="LANDED", career_science=25.0,
                           vessel_recoverable=mlib.RECOVERABLE_YES) for i in range(2)])
        state = base_t.drive(career_asked(base_t.in_recover(**SCIENCE_KEYS)),
                             *[base_t.lost(14.0 + i, career_funds=99999.0,
                                           career_science=25.0) for i in range(GRACE)])
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("career science pool", state.loss_reason)
        # And a science credit certifies whatever funds read.
        state = base_t.drive(career_asked(base_t.in_recover(**SCIENCE_KEYS)),
                             base_t.lost(14.0, career_funds=NAN, career_science=30.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)

    def test_a_loss_before_the_issue_is_still_the_pre_recovery_loss(self):
        state = base_t.drive(s_in_recover(),
                             base_t.snap(ut=12.0, vessel_lost=True, career_science=40.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-before-recovery", state.loss_reason)

    def test_the_whole_science_game_flight_is_mission_ok_through_the_shell(self):
        frames = [_science_game(f) for f in base_t.FLIGHT_FRAMES + [base_t.LANDED_FRAME]]
        frames += [ssci(ut=8.0 + i, situation="LANDED", science_data_count=2,
                        career_science=10.0) for i in range(2)]
        frames += [ssci(ut=10.0 + i, situation="LANDED", science_data_count=1,
                        career_science=25.0) for i in range(2)]
        frames += [ssci(ut=12.0 + i, situation="LANDED", science_data_count=1,
                        career_science=25.0, vessel_recoverable=mlib.RECOVERABLE_YES)
                   for i in range(2)]
        frames += [slost(14.0, career_science=40.0)]
        control = FakeMissionControl(frames)
        code, result = run(science_bench_recover.SPEC, sparams(), control)
        self.assertEqual(0, code, result.get("reason"))
        self.assertEqual(mlib.MISSION_OK, result["verdict"])
        transmits = [a for a in control.actions if a.kind == mlib.ACTION_TRANSMIT_SCIENCE]
        self.assertEqual([1.0], [a.value for a in transmits])


# ---------------------------------------------------------------------------
# 3. The transmit cap.
# ---------------------------------------------------------------------------


class SbrTransmitCapMachineTests(unittest.TestCase):

    def test_the_cap_rides_the_transmit_entry_emission(self):
        state, _ = sflown(transmitMaxExperiments=1)
        state, actions = base_t.drive_actions(
            state,
            ssci(ut=8.0, situation="LANDED", science_data_count=2),
            ssci(ut=9.0, situation="LANDED", science_data_count=2))
        self.assertEqual([mlib.Action(mlib.ACTION_TRANSMIT_SCIENCE, value=1.0)], actions)

    def test_every_re_emit_carries_the_same_cap_and_the_re_emits_stay_bounded(self):
        state = s_in_transmit(transmitMaxExperiments=1, transmitTimeoutSeconds=10000)
        state, actions = base_t.drive_actions(
            state, *[ssci(ut=10.0 + i, situation="LANDED", science_data_count=2,
                          career_science=10.0) for i in range(200)])
        transmits = [a for a in actions if a.kind == mlib.ACTION_TRANSMIT_SCIENCE]
        self.assertEqual(mlib.SBR_ACTION_REEMIT_LIMIT, len(transmits))
        self.assertEqual({1.0}, {a.value for a in transmits})

    def test_a_partial_transmit_trips_no_gate_that_assumes_an_empty_craft(self):
        # Data still aboard (count 1) on every TRANSMIT and RECOVER frame.
        state = s_asked(s_in_recover())
        self.assertEqual(mlib.SBR_RECOVER, state.phase)
        self.assertFalse(state.done)
        self.assertEqual(2, state.data_count_max)
        self.assertEqual(1, state.recover_asks)

    def test_the_cap_reports_itself_in_the_evidence(self):
        state = base_t.drive(s_asked(s_in_recover(transmitMaxExperiments=3)),
                             *[slost(14.0 + i, career_science=25.0) for i in range(GRACE)])
        self.assertIn("transmitMaxExperiments=3", state.loss_reason)
        self.assertIn("recoverScienceBaseline=25.000", state.loss_reason)
        self.assertIn("sciencePostLoss=25.000", state.loss_reason)

    def test_a_cap_alone_keeps_the_funds_pool(self):
        p = mlib.sbr_params_from_dict(base_t.params(transmitMaxExperiments=1))
        self.assertEqual(mlib.SBR_CREDIT_POOL_FUNDS, p.recover_credit_pool)
        state = base_t.drive(base_t._asked(base_t.in_recover(transmitMaxExperiments=1)),
                             base_t.lost(14.0, career_funds=1500.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)


class TransmitCapRunnerTests(unittest.TestCase):

    def setUp(self):
        self.lines = []
        self._orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = self.lines.append

    def tearDown(self):
        mission_runner._stdout_sink = self._orig

    def test_a_cap_of_one_sends_the_first_and_keeps_the_rest_aboard(self):
        a, b = base_t._FakeExperiment(has_data=True), base_t._FakeExperiment(has_data=True)
        base_t.control()._perform_transmit_science(base_t._FakeVessel([a, b]), 1.0)
        self.assertEqual((1, 0), (a.transmitted, b.transmitted))
        self.assertTrue(b.has_data_value)
        self.assertTrue(any("transmit_science sent=1 skipped=0 failed=0 cap=1 "
                            "runTotal=1 heldBack=1" in l for l in self.lines), self.lines)

    def test_a_re_emitted_transmit_never_sends_past_the_cap_over_the_run(self):
        exps = [base_t._FakeExperiment(has_data=True) for _ in range(3)]
        c = base_t.control()
        for _ in range(1 + mlib.SBR_ACTION_REEMIT_LIMIT):
            c._perform_transmit_science(base_t._FakeVessel(exps), 2.0)
        self.assertEqual([1, 1, 0], [e.transmitted for e in exps])
        self.assertEqual(2, c._transmit_sent_total)

    def test_a_raising_send_does_not_count_and_the_next_module_takes_its_place(self):
        bad = base_t._FakeExperiment(has_data=True, raise_on=("transmit",))
        good = base_t._FakeExperiment(has_data=True)
        c = base_t.control()
        c._perform_transmit_science(base_t._FakeVessel([bad, good]), 1.0)
        self.assertEqual(1, good.transmitted)
        self.assertEqual(1, c._transmit_sent_total)

    def test_the_uncapped_verb_and_its_log_line_are_unchanged(self):
        a, b = base_t._FakeExperiment(has_data=True), base_t._FakeExperiment(has_data=True)
        base_t.control()._perform_transmit_science(base_t._FakeVessel([a, b]))
        self.assertEqual((1, 1), (a.transmitted, b.transmitted))
        self.assertIn("[Mission][Info][Science] transmit_science sent=2 skipped=0 failed=0",
                      self.lines)

    def test_perform_routes_the_cap_from_the_action_value(self):
        a, b = base_t._FakeExperiment(has_data=True), base_t._FakeExperiment(has_data=True)
        vessel = base_t._FakeVessel([a, b])
        vessel.control = None
        c = mission_runner.KrpcMissionControl(client_name="test", read_science=True)
        c._conn = base_t.PerformDispatchTests._Conn(vessel)
        c.perform(mlib.Action(mlib.ACTION_TRANSMIT_SCIENCE, value=1.0))
        c.perform(mlib.Action(mlib.ACTION_TRANSMIT_SCIENCE, value=1.0))
        self.assertEqual((1, 0), (a.transmitted, b.transmitted))


# ---------------------------------------------------------------------------
# 4. The independent pool read.
# ---------------------------------------------------------------------------


class _PoolsSC:
    def __init__(self, funds=1234.0, science=56.0, funds_raises=False,
                 science_raises=False):
        self.funds_value, self.science_value = funds, science
        self.funds_raises, self.science_raises = funds_raises, science_raises

    @property
    def funds(self):
        if self.funds_raises:
            raise RuntimeError("Funds not available in this game mode")
        return self.funds_value

    @property
    def science(self):
        if self.science_raises:
            raise RuntimeError("Science not available in this game mode")
        return self.science_value


class IndependentPoolReadTests(unittest.TestCase):

    def setUp(self):
        self.lines = []
        self._orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = self.lines.append

    def tearDown(self):
        mission_runner._stdout_sink = self._orig

    def _warns(self, pool):
        return [l for l in self.lines if "career %s pool UNREADABLE" % (pool,) in l]

    def test_a_raising_funds_pool_leaves_science_finite(self):
        funds, science = base_t.control()._read_career_pools(_PoolsSC(funds_raises=True))
        self.assertTrue(math.isnan(funds))
        self.assertEqual(56.0, science)

    def test_a_raising_science_pool_leaves_funds_finite(self):
        funds, science = base_t.control()._read_career_pools(_PoolsSC(science_raises=True))
        self.assertEqual(1234.0, funds)
        self.assertTrue(math.isnan(science))

    def test_one_warn_per_pool_and_a_dark_funds_pool_never_mutes_science(self):
        c = base_t.control()
        sc = _PoolsSC(funds_raises=True)
        for _ in range(5):
            c._read_career_pools(sc)
        self.assertEqual(1, len(self._warns("funds")))
        self.assertEqual(0, len(self._warns("science")))
        sc.science_raises = True
        for _ in range(5):
            c._read_career_pools(sc)
        self.assertEqual(1, len(self._warns("funds")))
        self.assertEqual(1, len(self._warns("science")))

    def test_the_vessel_lost_snapshot_carries_science_in_a_science_game(self):
        class _Conn:
            def __init__(self):
                class _SC:
                    ut = 512.0

                    @property
                    def active_vessel(self):
                        raise RuntimeError("active vessel recovered")

                    @property
                    def funds(self):
                        raise RuntimeError("Funds not available in this game mode")

                    @property
                    def science(self):
                        return 40.0

                    def get_kerbal(self, name):
                        return None

                self.space_center = _SC()

        c = mission_runner.KrpcMissionControl(client_name="test", read_science=True)
        c._conn = _Conn()
        for _ in range(mission_runner.READ_FAIL_STREAK_LIMIT - 1):
            with self.assertRaises(RuntimeError):
                c.read_snapshot()
        snapshot = c.read_snapshot()
        self.assertTrue(snapshot.vessel_lost)
        self.assertTrue(math.isnan(snapshot.career_funds))
        self.assertEqual(40.0, snapshot.career_science)


# ---------------------------------------------------------------------------
# 5. Schema / params.
# ---------------------------------------------------------------------------


class ScienceModeSchemaTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        with open(base_t.SCHEMA_PATH, "rb") as fh:
            cls.schema = tomllib.load(fh)
        cls.params = cls.schema["params"]

    def test_the_three_keys_are_declared_optional_with_their_shapes(self):
        pool = self.params["recoverCreditPool"]
        self.assertFalse(pool["required"])
        self.assertEqual("string", pool["type"])
        self.assertEqual(list(mlib.SBR_CREDIT_POOLS), pool["values"])
        floor = self.params["recoverMinScienceGain"]
        self.assertFalse(floor["required"])
        self.assertEqual("number", floor["type"])
        self.assertGreater(floor["min"], 0.0, "the science credit floor must be "
                           "strictly positive: it is the only proof of a recovery")
        cap = self.params["transmitMaxExperiments"]
        self.assertFalse(cap["required"])
        self.assertEqual("int", cap["type"])
        self.assertEqual(1, cap["min"])

    def test_the_science_mode_params_validate_whole(self):
        import hlib
        self.assertEqual([], hlib._validate_mission_params(sparams(), self.schema))
        self.assertEqual([], hlib._validate_mission_params(
            base_t.params(recoverCreditPool="funds"), self.schema))

    def test_bad_values_die_at_admit(self):
        import hlib
        for bad in ({"recoverCreditPool": "Science"},
                    {"recoverCreditPool": "reputation"},
                    {"recoverCreditPool": 1},
                    {"recoverMinScienceGain": 0.0},
                    {"transmitMaxExperiments": 0},
                    {"transmitMaxExperiments": 1.5}):
            self.assertNotEqual([], hlib._validate_mission_params(sparams(**bad), self.schema),
                                bad)

    def test_the_builder_reads_every_new_key(self):
        p = mlib.sbr_params_from_dict(sparams(recoverMinScienceGain=3.5,
                                              transmitMaxExperiments=2))
        self.assertEqual(mlib.SBR_CREDIT_POOL_SCIENCE, p.recover_credit_pool)
        self.assertEqual(3.5, p.recover_min_science)
        self.assertEqual(2, p.transmit_max_experiments)

    def test_an_unknown_pool_raises_rather_than_falling_back_to_funds(self):
        with self.assertRaises(ValueError):
            mlib.sbr_params_from_dict(base_t.params(recoverCreditPool="credits"))


if __name__ == "__main__":
    unittest.main()
