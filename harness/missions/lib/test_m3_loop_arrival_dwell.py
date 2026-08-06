"""Unit cells for the m3_loop_arrival_dwell machine (the Tier-2 loop-arrival
map dwell): the MissionConfig arm round trip (tag-gated, fail-closed), the
V1-style OBSERVED camera gate, the span-clock window arithmetic, the
window-stamp evidence, and every named give-up.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP)::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import unittest

import mlib
from mlib import TelemetrySnapshot


def snap(**kw):
    return TelemetrySnapshot(**kw)


def params(**over):
    base = {
        "treeId": "tree-1",
        "departOffsetSeconds": 89448.917,
        "arriveOffsetSeconds": 4474604.451,
        "parkOffsetSeconds": 4506361.14,
        "dwellLeadSeconds": 120.0,
        "dwellHoldSeconds": 60.0,
    }
    base.update(over)
    return mlib.m3_params_from_dict(base)


class M3ParamsTests(unittest.TestCase):
    def test_tree_id_is_required(self):
        with self.assertRaises(ValueError):
            mlib.m3_params_from_dict({"departOffsetSeconds": 1})

    def test_offsets_parse(self):
        p = params()
        self.assertEqual("tree-1", p.tree_id)
        self.assertAlmostEqual(89448.917, p.depart_offset, places=3)


class M3ArmLoopTests(unittest.TestCase):
    def test_first_frame_issues_one_missionconfig_seam_command(self):
        st, acts = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        self.assertTrue(st.arm_issued)
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_PARSEK_SEAM_COMMAND, acts[0].kind)
        self.assertEqual("MissionConfig", acts[0].seam_verb)
        self.assertEqual(mlib.M3_TAG_ARM, acts[0].seam_tag)
        args = dict(acts[0].seam_args)
        self.assertEqual("tree-1", args["tree"])
        self.assertEqual("true", args["loop"])
        self.assertNotIn("intervalSeconds", args)

    def test_interval_param_rides_the_seam_args(self):
        st, acts = mlib.m3_decide(
            mlib.m3_initial_state(params(loopIntervalSeconds=19645697.0)),
            snap(ut=1.0))
        self.assertEqual("19645697.000", dict(acts[0].seam_args)["intervalSeconds"])

    def test_ok_result_reads_the_anchor_and_commands_the_camera(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, acts = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "9160500.25"),)))
        self.assertEqual(mlib.M3_CAMERA, st.phase)
        self.assertEqual(9160500.25, st.anchor_ut)
        kinds = [a.kind for a in acts]
        self.assertEqual([mlib.ACTION_CAMERA_SET_MAP,
                          mlib.ACTION_CAMERA_FOCUS_BODY,
                          mlib.ACTION_CAMERA_SET_POSE], kinds)

    def test_a_stale_tag_never_advances(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, acts = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag="othertag",
            seam_command_payload=(("anchorUt", "5.0"),)))
        self.assertEqual(mlib.M3_ARM_LOOP, st.phase)
        self.assertFalse(st.done)

    def test_error_result_flakes_with_the_decoded_reason(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, _ = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="ERROR", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("msg", "unknown-tree"),)))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("unknown-tree", st.flake_reason)

    def test_unreadable_anchor_is_a_named_flake(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, _ = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "bogus"),)))
        self.assertTrue(st.done)
        self.assertIn("anchorUt", st.flake_reason)


class M3DwellFlowTests(unittest.TestCase):
    def _armed(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, _ = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "10000.0"),)))
        return st

    def test_camera_gate_is_observed_not_commanded(self):
        st = self._armed()
        held, acts = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Flight"))
        self.assertEqual(mlib.M3_CAMERA, held.phase)
        self.assertEqual([], acts)
        moved, acts = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        self.assertEqual(mlib.M3_WARP_DEPART, moved.phase)
        self.assertEqual(mlib.ACTION_WARP_TO_UT, acts[-1].kind)
        # anchor 10000 + depart offset - lead
        self.assertAlmostEqual(10000.0 + 89448.917 - 120.0, acts[-1].value, 3)

    def test_full_dwell_sequence_stamps_ordered_windows(self):
        st = self._armed()
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        depart = 10000.0 + 89448.917
        arrive = 10000.0 + 4474604.451
        park = 10000.0 + 4506361.14
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))   # warp arrived
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))   # stamp + hold
        st, acts = mlib.m3_decide(st, snap(ut=depart - 50.0))  # hold done
        self.assertEqual(mlib.M3_WARP_ARRIVE, st.phase)
        self.assertEqual(mlib.ACTION_WARP_TO_UT, acts[-1].kind)
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 119.0))
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 119.0))
        st, acts = mlib.m3_decide(st, snap(ut=arrive - 50.0))
        self.assertEqual(mlib.M3_HOLD_PARK, st.phase)
        st, _ = mlib.m3_decide(st, snap(ut=park + 1.0))
        st, _ = mlib.m3_decide(st, snap(ut=park + 62.0))
        self.assertTrue(st.done)
        rows = mlib.evaluate_m3_assertions((), st.params, st.phases_reached, st)
        self.assertTrue(all(r.met for r in rows),
                        [(r.name, r.met) for r in rows])

    def test_hold_cancels_residual_warp_on_entry(self):
        st = self._armed()
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        depart = 10000.0 + 89448.917
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))
        st, acts = mlib.m3_decide(st, snap(ut=depart - 119.0, warp_mode="RAILS"))
        self.assertIn(mlib.ACTION_CANCEL_WARP, [a.kind for a in acts])

    def test_missing_window_stamp_fails_the_dwell_row_closed(self):
        p = params()
        st = mlib.m3_initial_state(p)
        rows = mlib.evaluate_m3_assertions((), p, ("ARM-LOOP",), st)
        by = {r.name: r for r in rows}
        self.assertFalse(by["dwelledAllWindows"].met)
        self.assertFalse(by["loopArmed"].met)


if __name__ == "__main__":
    unittest.main()
