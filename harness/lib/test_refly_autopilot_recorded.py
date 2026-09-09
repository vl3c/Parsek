"""Fixture gates for `refly-autopilot-recorded`, the FIXED-BEHAVIOUR TWIN of
`refly-a-recorded`.

WHAT THIS FILE GUARDS. `refly-a-recorded` freezes the sealing defect: a chain TIP
carrying the terminal and NO `mergeState` key, which the codec reads back as
Immutable and which silently closes the slot. This fixture is the same flight shape
flown by a committed spec against the FIXED DLL, and its two chain TIPs carry
`mergeState = CommittedProvisional`. The pair is the whole regression floor for PR
#1658, and it is readable with no flight at all.

So the gate below is the INVERSE of its sibling's: where
`test_build_refly_a_recorded` asserts a defect has not healed, this asserts a fix
has not regressed. If a re-harvest ever produces a TIP without the key, that is not
fixture drift - it is #1658 back, and the response is to fix the code rather than
re-pin these bytes. The builder's own failure string says so where an operator will
meet it.

IT CANNOT RE-RUN THE HARVEST from the source, the same limit
`ReflyARecordedFixtureDriftTests` and `DunaOneRecordedFixtureDriftTests` state: the
input is a results-directory produced save that is not committed. What it CAN do is
run every post-condition `build_refly_autopilot_recorded.py --check` runs, in
process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import io
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "refly-autopilot-recorded")
SIBLING_DIR = os.path.join(_HARNESS, "fixtures", "saves", "refly-a-recorded")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_refly_autopilot_recorded.py")
    spec = importlib.util.spec_from_file_location(
        "build_refly_autopilot_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ReflyAutopilotRecordedFixtureDriftTests(unittest.TestCase):

    def setUp(self):
        self.builder = _load_builder()

    def test_every_post_condition_the_builder_checks_still_holds(self):
        """The whole `--check` surface, in process. MUTATION: drop the
        `mergeState` key from either chain TIP and this reds with the sentence an
        operator needs (fix the code, do not re-pin the fixture)."""
        problems = self.builder.verify()
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_two_tips_carry_the_open_bit_and_that_is_the_point(self):
        """Stated a second time, independently of the builder's loop, because this
        one assertion IS the regression floor for PR #1658 and a gate that only
        exists inside a helper is a gate somebody refactors away."""
        lines = self.builder._read_lines(self.builder.FIXTURE_SFS)
        by_id = {r["recordingId"]: r
                 for r in self.builder._recording_blocks(lines)}
        for _chain, head_id, tip_id in self.builder.EXPECT_SPLIT_PAIRS:
            self.assertEqual("CommittedProvisional",
                             by_id[head_id].get("mergeState"),
                             "HEAD %s lost its mergeState" % head_id)
            self.assertEqual("CommittedProvisional",
                             by_id[tip_id].get("mergeState"),
                             "TIP %s lost its mergeState: PR #1658 has regressed"
                             % tip_id)
            self.assertEqual("1", by_id[tip_id].get("chainIndex"))

    def test_the_sibling_fixture_still_carries_the_DEFECT_it_is_kept_for(self):
        """THE PAIR IS THE EVIDENCE, so this cell reads the OTHER fixture too. The
        two saves are only meaningful against each other: same craft, same staging
        plan, same optimizer split, opposite outcome. A re-harvest that healed
        `refly-a-recorded` would leave this fixture asserting a fix against
        nothing."""
        sfs = os.path.join(SIBLING_DIR, "persistent.sfs")
        self.assertTrue(os.path.isfile(sfs), "refly-a-recorded is missing")
        with io.open(sfs, "r", encoding="utf-8", errors="replace") as handle:
            lines = handle.read().split("\n")
        by_id = {r["recordingId"]: r
                 for r in self.builder._recording_blocks(lines)}
        # THE ID IS NAMED, and there is deliberately no "first chainIndex 1" fallback.
        # The first cut of this cell carried a wrong id and a fallback that found the
        # right block anyway, which is a cell that cannot fail for the reason it was
        # written: a re-harvest adding a second chain would have had it assert over
        # whichever block parsed first. A missing id is a red.
        tip = by_id.get("8da7c2c2a6d84505b4bc121fdb9f528f")
        self.assertIsNotNone(
            tip, "refly-a-recorded's chain TIP 8da7c2c2 is gone: this cell names the "
                 "seed's TIP by id, so a re-harvest that changed it must be read "
                 "rather than absorbed by a fallback")
        self.assertIsNone(
            tip.get("mergeState"),
            "refly-a-recorded's chain TIP grew a mergeState key: it is the DEFECT "
            "fixture and healing it is a red, not an improvement (its own drift "
            "test carries the full argument)")

    def test_the_live_rewind_point_is_here_and_the_launch_save_is_not(self):
        """The two halves of what this fixture may carry.

        The RewindPoint SURVIVED, which is the single fact that separates it from
        `refly-a-recorded` (whose point the defect reaped before collection), and it
        is what lets a future lane re-fly this host.

        The rewind-to-LAUNCH quicksave is ABSENT even though the produced save had
        one, and that is POLICY rather than a gap: `harvest_bdock_station.py` prunes
        it, `CommittedFixtureRewindSaveTests` forbids it in every fixture, and a lane
        that needs one PRODUCES it in-run - H58's header spells out the mechanism
        (`FlightRecorder.CaptureRewindSave` writes the quicksave at every
        non-promotion recording start, so StartRecording / StopRecording / CommitTree
        mints a rewindable tree that `tree=latest` resolves to). An earlier cut of the
        builder restored the payload here on the belief that RF-4 would need it; it
        does not, and the restore is gone."""
        rp = os.path.join(FIXTURE_DIR, "Parsek", "RewindPoints",
                          self.builder.EXPECT_RP_ID + ".sfs")
        self.assertTrue(os.path.isfile(rp), rp)
        saves = os.path.join(FIXTURE_DIR, "Parsek", "Saves")
        self.assertFalse(os.path.isdir(saves) and os.listdir(saves),
                         "the rewind-to-launch payload is back with no lane to read "
                         "it; amend CommittedFixtureRewindSaveTests in the same "
                         "change as the lane, or drop the payload")


if __name__ == "__main__":
    unittest.main()
