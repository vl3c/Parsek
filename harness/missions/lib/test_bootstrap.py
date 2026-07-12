"""Unit tests for the PURE decision functions of bootstrap_venv.py (design M-B1
"Dependency manifest"). No venv, no pip, no network -- only the side-effect-free
requirements parse, resolved-version extraction, freeze hashing, stamp
construction, promotion edit, and the stamp self-check. The dry-run I/O path is
exercised with a stubbed runner so no venv is ever created.

Import path: the same missions/lib discovery root trick as test_shells -- prepend
missions/ so ``import bootstrap_venv`` resolves.
"""

import json
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import bootstrap_venv as bv   # noqa: E402


REQ_TEXT = (
    "# comment\n"
    "krpc==0.5.4\n"
    "# protobuf==<RESOLVED>   # PROVISIONAL: filled in later; do NOT hand-pin.\n"
)

FREEZE = "krpc==0.5.4\nprotobuf==4.21.12\nsix==1.16.0\n"


class ParseRequirementsTests(unittest.TestCase):
    def test_committed_pins_only(self):
        """Only real pins are returned; the PROVISIONAL protobuf comment line is
        NOT (it must stay out of hlib.venv_admission's enforced set)."""
        reqs = bv.parse_requirements(REQ_TEXT)
        self.assertEqual(reqs, {"krpc": "0.5.4"})
        self.assertNotIn("protobuf", reqs)

    def test_canonical_names(self):
        self.assertEqual(bv.parse_requirements("Proto_Buf==1.2\n"), {"proto-buf": "1.2"})


class ResolvedVersionTests(unittest.TestCase):
    def test_extract_present_and_absent(self):
        self.assertEqual(bv.resolved_version_from_freeze(FREEZE, "protobuf"), "4.21.12")
        self.assertEqual(bv.resolved_version_from_freeze(FREEZE, "krpc"), "0.5.4")
        self.assertIsNone(bv.resolved_version_from_freeze(FREEZE, "numpy"))


class FreezeHashTests(unittest.TestCase):
    def test_order_independent_and_stable(self):
        a = bv.freeze_hash("krpc==0.5.4\nprotobuf==4.21.12\n")
        b = bv.freeze_hash("protobuf==4.21.12\nkrpc==0.5.4\n")   # reordered
        self.assertEqual(a, b)
        self.assertEqual(len(a), 64)  # sha256 hex

    def test_differs_on_version_change(self):
        a = bv.freeze_hash("protobuf==4.21.12\n")
        b = bv.freeze_hash("protobuf==4.21.13\n")
        self.assertNotEqual(a, b)


class BuildStampTests(unittest.TestCase):
    def test_stamp_shape_matches_venv_admission(self):
        """The stamp carries pins as dist->version under stamp['pins'] -- exactly
        what hlib.venv_admission reads."""
        stamp = bv.build_stamp("0.5.4", "4.21.12", FREEZE, created_utc="2026-07-12T00:00:00Z")
        self.assertEqual(stamp["pins"], {"krpc": "0.5.4", "protobuf": "4.21.12"})
        self.assertEqual(stamp["schema"], bv.VENV_STAMP_SCHEMA)
        self.assertEqual(stamp["source"], bv.SOURCE_PYPI)
        self.assertEqual(len(stamp["freezeHash"]), 64)

    def test_serialize_deterministic(self):
        s1 = bv.build_stamp("0.5.4", "4.21.12", FREEZE, created_utc="2026-07-12T00:00:00Z")
        s2 = bv.build_stamp("0.5.4", "4.21.12", FREEZE, created_utc="2026-07-12T00:00:00Z")
        self.assertEqual(bv.serialize_stamp(s1), bv.serialize_stamp(s2))
        # round-trips
        self.assertEqual(json.loads(bv.serialize_stamp(s1)), s1)


class StampSatisfiesRequirementsTests(unittest.TestCase):
    def test_admits_matching_and_tolerates_extra_pin(self):
        stamp = bv.build_stamp("0.5.4", "4.21.12", FREEZE, created_utc="Z")
        # committed requirements has ONLY krpc (protobuf not yet promoted): still OK.
        self.assertTrue(bv.stamp_satisfies_requirements(stamp, {"krpc": "0.5.4"}))
        # after promotion, both enforced: still OK.
        self.assertTrue(bv.stamp_satisfies_requirements(stamp, {"krpc": "0.5.4", "protobuf": "4.21.12"}))

    def test_refuses_missing_and_drifted(self):
        stamp = bv.build_stamp("0.5.4", "4.21.12", FREEZE, created_utc="Z")
        self.assertFalse(bv.stamp_satisfies_requirements(None, {"krpc": "0.5.4"}))
        self.assertFalse(bv.stamp_satisfies_requirements(stamp, {"krpc": "0.5.3"}))  # drift


class PromoteRequirementsTests(unittest.TestCase):
    def test_replaces_provisional_comment_with_real_pin(self):
        out = bv.promote_requirements_text(REQ_TEXT, "4.21.12")
        reqs = bv.parse_requirements(out)
        self.assertEqual(reqs, {"krpc": "0.5.4", "protobuf": "4.21.12"})

    def test_idempotent(self):
        once = bv.promote_requirements_text(REQ_TEXT, "4.21.12")
        twice = bv.promote_requirements_text(once, "4.21.12")
        self.assertEqual(once, twice)

    def test_updates_existing_pin_version(self):
        existing = "krpc==0.5.4\nprotobuf==4.21.0\n"
        out = bv.promote_requirements_text(existing, "4.21.12")
        self.assertEqual(bv.parse_requirements(out)["protobuf"], "4.21.12")

    def test_appends_when_neither_present(self):
        out = bv.promote_requirements_text("krpc==0.5.4\n", "4.21.12")
        self.assertEqual(bv.parse_requirements(out)["protobuf"], "4.21.12")


class SmokeSourceTests(unittest.TestCase):
    def test_smoke_source_exercises_generated_code(self):
        src = bv.generated_code_smoke_source()
        self.assertIn("import krpc", src)
        self.assertIn("KRPC_pb2", src)          # compiled protobuf bindings
        self.assertIn("SerializeToString", src)  # generated-code round-trip
        self.assertIn("smoke-ok", src)


class VenvPythonPathTests(unittest.TestCase):
    def test_platform_layout(self):
        p = bv.venv_python_path("/x/.venv")
        self.assertTrue(p.endswith("python.exe") if os.name == "nt" else p.endswith("python"))


class DryRunTests(unittest.TestCase):
    def test_dry_run_creates_no_venv_and_calls_no_runner(self):
        """--dry-run runs the pure decisions + plan only; it never invokes the
        subprocess runner (no venv, no pip, no network)."""
        calls = []
        def boom(*a, **k):
            calls.append(a)
            raise AssertionError("dry-run must not call the runner")
        # Point at the committed requirements.txt (parsed, not written).
        req_path = os.path.join(_MISSIONS, "requirements.txt")
        code = bv.run_bootstrap(requirements_path=req_path, dry_run=True, runner=boom)
        self.assertEqual(code, 0)
        self.assertEqual(calls, [])

    def test_plan_lists_steps(self):
        plan = bv.plan_bootstrap("/x/.venv", "/x/requirements.txt")
        self.assertTrue(any("create venv" in s for s in plan))
        self.assertTrue(any("smoke" in s for s in plan))
        self.assertTrue(any(".venv-stamp.json" in s for s in plan))


if __name__ == "__main__":
    unittest.main()
