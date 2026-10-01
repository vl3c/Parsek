"""Fixture + spec gates for `career-idless-same-name-pad` and
`L7-career-idless-same-name-xp-refusal`.

WHAT THIS FILE GUARDS. `career-idless-same-name-pad` is the host of the stage-2
live proof of `KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY` (the XP-leg
`ambiguous-recovery-recording` refusal). Since stage 3 (identity first) that
refusal lives only on the picker's NAME FALLBACK, which a recovery reaches only
when no recording carries the vessel's launch guid or genuine spawn pid; there,
every survivor is id-less, so the shape is two id-less same-name recordings plus a
vessel no recording knows by identity. The builder makes it from
`career-same-name-pad` with three edits (guids stripped, snapshot pids stripped,
live persistentId re-stamped); see its docstring.

IT READS OUTSIDE `harness/` through the base builder (the fixture is DERIVED from
`Source/Parsek.Tests/Fixtures/C2CareerPostFix/`), so a re-harvest of that xUnit
fixture reds here until the builder is re-run.

THE SNAPSHOT SIDECARS ARE COMPARED DECODED. The builder re-encodes them with the
local zlib, whose compressed bytes are not promised stable across zlib versions;
the decoded payload (and the header fields) are. The save text is compared as raw
bytes like every sibling drift cell.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest

try:
    import tomllib
except ImportError:  # pragma: no cover - Python < 3.11
    import tomli as tomllib  # type: ignore

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_TOOLS = os.path.join(_HARNESS, "tools")

FIXTURE_NAME = "career-idless-same-name-pad"
FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", FIXTURE_NAME)
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
FIXTURE_META = os.path.join(FIXTURE_DIR, "persistent.loadmeta")
FIXTURE_RECORDINGS = os.path.join(FIXTURE_DIR, "Parsek", "Recordings")
SPEC_PATH = os.path.join(_HARNESS, "scenarios", "L7-career-idless-same-name-xp-refusal.toml")


def _load_builder():
    if _TOOLS not in sys.path:
        sys.path.insert(0, _TOOLS)
    path = os.path.join(_TOOLS, "build_career_idless_same_name_pad.py")
    spec = importlib.util.spec_from_file_location("build_career_idless_same_name_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CareerIdlessSameNamePadFixtureDriftTests(unittest.TestCase):
    """WIRES `build_career_idless_same_name_pad.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()

    def _host(self, name):
        return os.path.join(_HARNESS, "fixtures", "saves", self.b.HOST_NAME, name)

    def _lines(self):
        return self.b.read_lines(FIXTURE_SFS)

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        problems = self.b.verify(self._lines(), self.b.CREW_NAME)
        problems += self.b.verify_sidecars(FIXTURE_DIR)
        self.assertEqual([], problems, "; ".join(problems))

    def test_the_committed_save_is_byte_identical_to_a_fresh_rebuild(self):
        b = self.b
        rebuilt = b.build(
            b.read_lines(self._host("persistent.sfs")),
            b.read_lines(os.path.join(b.RECORDINGS_DIR, "persistent.sfs")),
            "%s (CAREER)" % FIXTURE_NAME)
        with open(FIXTURE_SFS, "rb") as fh:
            committed = fh.read()
        self.assertEqual(committed, "\r\n".join(rebuilt).encode("utf-8"),
                         "%s has drifted from build_career_idless_same_name_pad.py; "
                         "re-run the builder and commit" % FIXTURE_NAME)

    def test_the_committed_loadmeta_is_byte_identical_to_a_fresh_rebuild(self):
        b = self.b
        rebuilt = b.build_loadmeta(b.read_lines(self._host("persistent.loadmeta")),
                                   self._lines())
        with open(FIXTURE_META, "rb") as fh:
            committed = fh.read()
        self.assertEqual(committed, "\r\n".join(rebuilt).encode("utf-8"))

    def test_the_snapshots_decode_to_the_source_minus_its_pid_line(self):
        # The drift guard for edit 2, on decoded payloads (see the module docstring).
        b = self.b
        source_dir = os.path.join(b.RECORDINGS_DIR, "Parsek", "Recordings")
        snaps = sorted(n for n in os.listdir(FIXTURE_RECORDINGS)
                       if n.endswith(b.SNAPSHOT_SUFFIXES))
        self.assertEqual(4, len(snaps))
        for n in snaps:
            with open(os.path.join(source_dir, n), "rb") as fh:
                src_header, src_payload = b.decode_snapshot(fh.read())
            with open(os.path.join(FIXTURE_RECORDINGS, n), "rb") as fh:
                header, payload = b.decode_snapshot(fh.read())
            self.assertEqual(src_header, header, n)
            self.assertEqual(b.strip_snapshot_pid(src_payload), payload, n)
            # The negative control: the source DID carry the guid the backfill reads.
            self.assertIsNotNone(b.snapshot_top_level_pid(src_payload), n)
            self.assertIsNone(b.snapshot_top_level_pid(payload), n)

    def test_every_other_sidecar_is_a_verbatim_copy(self):
        b = self.b
        source_dir = os.path.join(b.RECORDINGS_DIR, "Parsek", "Recordings")
        others = sorted(n for n in os.listdir(FIXTURE_RECORDINGS)
                        if not n.endswith(b.SNAPSHOT_SUFFIXES))
        self.assertTrue(others)
        self.assertEqual(sorted(n for n in os.listdir(source_dir)
                                if not n.endswith(b.SNAPSHOT_SUFFIXES)), others)
        for n in others:
            with open(os.path.join(source_dir, n), "rb") as fh:
                src = fh.read()
            with open(os.path.join(FIXTURE_RECORDINGS, n), "rb") as fh:
                self.assertEqual(src, fh.read(), n)

    def test_the_recordings_carry_no_launch_guid(self):
        b = self.b
        lines = self._lines()
        self.assertEqual([], b.same_name._values_named(lines, "recordedVesselGuid"))
        self.assertEqual([b.RECORDED_VESSEL_NAME] * 2,
                         b.same_name._values_named(lines, "vesselName"))

    def test_the_live_vessel_shares_no_identity_with_any_recording(self):
        b = self.b
        lines = self._lines()
        fs = b.find_node(lines, "FLIGHTSTATE")
        ship = b.child_nodes(lines, fs, "VESSEL")[0]
        self.assertEqual(b.NEW_LAUNCH_GUID, b.get_value(lines, ship, "pid"))
        self.assertEqual(b.NEW_VESSEL_PERSISTENT_ID, b.get_value(lines, ship, "persistentId"))
        self.assertEqual([b.RECORDED_VESSEL_PERSISTENT_ID] * 2,
                         b.same_name._values_named(lines, "vesselPersistentId"))
        self.assertEqual([], b.same_name._values_named(lines, "spawnedPid"))

    def test_both_recordings_end_before_the_clock_and_the_pick_ends_last(self):
        # The weak tier (most-recent-ended) and the expected pick, from the save text.
        b = self.b
        lines = self._lines()
        fs = b.find_node(lines, "FLIGHTSTATE")
        clock = float(b.get_value(lines, fs, "UT"))
        ids = b.same_name._values_named(lines, "recordingId")
        ends = [float(v) for v in b.same_name._values_named(lines, "explicitEndUT")]
        self.assertEqual(2, len(ids))
        self.assertEqual(2, len(ends))
        self.assertTrue(all(e < clock for e in ends))
        self.assertEqual(b.EXPECT_PICK_RECORDING_ID, ids[ends.index(max(ends))])

    def test_the_science_is_unbanked(self):
        b = self.b
        lines = self._lines()
        rnd = b.same_name._scenario_node(lines, "ResearchAndDevelopment")
        self.assertIsNotNone(rnd)
        self.assertEqual([], b.child_nodes(lines, rnd, "Science"))


class L7SpecFixtureSyncTests(unittest.TestCase):
    """The spec and the fixture must name each other, and the spec must keep the
    shape that reaches stage 2."""

    @classmethod
    def setUpClass(cls):
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)
        cls.b = _load_builder()

    def _required(self):
        return self.spec["expectations"]["logContracts"]["required"]

    def _forbidden(self):
        return self.spec["expectations"]["logContracts"]["forbidden"]

    def test_the_spec_stages_this_fixture(self):
        self.assertEqual("fixtures/saves/%s" % FIXTURE_NAME,
                         self.spec["fixture"]["saveTemplate"])
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"])

    def test_the_flight_records_nothing(self):
        # A recording of the flight carries the live guid and moves every leg onto
        # path=launch-guid, where stage 2 cannot fire. All three triggers off, no
        # StartRecording, and the count pinned at the fixture's two.
        steps = self.spec["driver"]["steps"]
        pins = {s["args"]["name"]: s["args"]["value"] for s in steps
                if s.get("cmd") == "SetSetting"}
        for key in ("autoRecordOnLaunch", "autoRecordOnEva",
                    "autoRecordOnFirstModificationAfterSwitch"):
            self.assertEqual("false", pins.get(key), key)
        self.assertFalse(any(s.get("cmd") == "StartRecording" for s in steps))
        self.assertEqual({"min": 2, "max": 2}, self.spec["expectations"]["recordings"]["count"])

    def test_the_spec_pins_the_refusal_and_forbids_the_row(self):
        req = self._required()
        self.assertTrue(any("Recovery kerbal XP refused:" in t
                            and "reason=ambiguous-recovery-recording" in t
                            and "corroboration=unknown-launch-guid" in t for t in req), req)
        self.assertTrue(any("PickRecoveryRecordingId path:" in t
                            and "path=name-fallback" in t for t in req), req)
        self.assertIn("Recovery kerbal XP recorded:", self._forbidden())

    def test_funds_and_science_still_credit_the_expected_pick(self):
        pick = self.b.EXPECT_PICK_RECORDING_ID
        req = self._required()
        self.assertTrue(any(t.startswith("VesselRecovery funds patched:") and pick in t
                            for t in req), req)
        self.assertTrue(any(t.startswith("KSC science recorded:") and pick in t
                            for t in req), req)


if __name__ == "__main__":
    unittest.main()
