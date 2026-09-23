"""Fixture gates for `pad-runway-pair` (the EX-1 host).

The fixture is DERIVED by `harness/tools/build_pad_runway_pair.py` from two committed
fixtures (`logi-cargo-pad` as the base, `rover-route-recorded`'s `rover fuel 0` as the
cloned second vessel), so a re-forge of either donor must re-run the builder. These
cells turn that "must" into a red, and pin the three properties the EX-1 lane depends
on: the subject sits at VESSEL index 1 with `activeVessel = 1` and the second vessel
directly after it (so a Rewind-to-Launch strip of the subject leaves index 1 naming
the second vessel), and the clone shares no identity with its donor.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import saveparse  # noqa: E402

SAVES = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_SFS = os.path.join(SAVES, "pad-runway-pair", "persistent.sfs")
DONOR_SFS = os.path.join(SAVES, "rover-route-recorded", "persistent.sfs")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_pad_runway_pair.py")
    spec = importlib.util.spec_from_file_location("build_pad_runway_pair", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _vessels(path):
    with open(path, "r", encoding="utf-8") as fh:
        res = saveparse.parse_sfs(fh.read())
    assert res.ok, res.error
    fs = res.root.first("GAME").first("FLIGHTSTATE")
    return fs, fs.nodes_named("VESSEL")


class PadRunwayPairFixtureTests(unittest.TestCase):

    def test_committed_bytes_are_the_builder_output(self):
        builder = _load_builder()
        sfs, meta = builder.build_texts()
        self.assertEqual([], builder._committed_matches(sfs, meta),
                         "pad-runway-pair drifted from its builder; re-run "
                         "harness/tools/build_pad_runway_pair.py")

    def test_second_vessel_at_index_zero_and_the_subject_focused(self):
        # A save written at the Space Center carries activeVessel = 0, so the vessel at
        # index 0 is what the post-rewind LoadGame focuses: it must be the runway rover,
        # never the host asteroid (EX-1's first reading focused the asteroid).
        fs, vessels = _vessels(FIXTURE_SFS)
        self.assertEqual("2", fs.value("activeVessel"))
        self.assertEqual(["Probe", "SpaceObject", "Probe"],
                         [v.value("type") for v in vessels])
        self.assertEqual(["rover fuel 0", "Logi Cargo Rig"],
                         [vessels[0].value("name"), vessels[2].value("name")])
        self.assertEqual(["Runway", "LaunchPad"],
                         [vessels[0].value("landedAt"), vessels[2].value("landedAt")])
        # Both vessels were rolled out at the save's own UT: nothing from the future.
        ut = fs.value("UT")
        self.assertEqual([ut, ut], [vessels[0].value("lct"), vessels[2].value("lct")])

    def test_clone_shares_no_identity_with_its_donor(self):
        _, fixture = _vessels(FIXTURE_SFS)
        _, donor = _vessels(DONOR_SFS)
        clone = fixture[0]
        original = [v for v in donor if v.value("name") == "rover fuel 0"][0]
        self.assertNotEqual(original.value("pid"), clone.value("pid"))
        self.assertNotEqual(original.value("persistentId"), clone.value("persistentId"))

        def part_pids(vessel):
            return {p.value("persistentId") for p in vessel.nodes_named("PART")}
        self.assertEqual(len(original.nodes_named("PART")), len(clone.nodes_named("PART")))
        self.assertFalse(part_pids(original) & part_pids(clone))
        all_pids = [v.value("persistentId") for v in fixture]
        self.assertEqual(len(all_pids), len(set(all_pids)))


if __name__ == "__main__":
    unittest.main()
