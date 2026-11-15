"""Unit tests for split_ghostpositioner.py.

Run: python3 -m unittest discover -s scripts/refactor -p "test_*.py" -q
"""

import collections
import contextlib
import io
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import split_ghostpositioner as sgp  # noqa: E402


SRC = """\
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // The positioner.
    internal sealed class ParsekFlightGhostPositioner : IGhostPositioner
    {
        private readonly ParsekFlight host;

        internal ParsekFlightGhostPositioner(ParsekFlight host)
        {
            this.host = host;
        }

        private readonly Dictionary<int, int> seen = new Dictionary<int, int>();

        private const double Cap = 250.0;

        void IGhostPositioner.Place(int index)
        {
            Place(index, 1.0);
        }

        // Moves with the Orbit group.
        internal void Place(int index, double ut)
        {
            string s = $"ut={ut.ToString("F1", CultureInfo.InvariantCulture)} {{literal}}";
        }

        internal Dictionary<int, int> SeenForTesting
            => seen;

        internal static readonly Func<int, bool> IsPositive =
            x => x > 0;

        internal Vector3 Offset(int a)
        {
            // Math here is a comment only.
            return Vector3.zero;
        }

        internal static int Clamp(int a)
        {
            return Math.Max(0, a);
        }
    }
}
"""


class SplitGhostPositionerTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.src = os.path.join(self.dir, "ParsekFlightGhostPositioner.cs")
        with open(self.src, "w", encoding="utf-8", newline="") as fh:
            fh.write(SRC)
        self.saved = (sgp.AREAS, sgp.MAIN_MEMBERS)
        sgp.AREAS = collections.OrderedDict([
            ("Orbit", ("Orbit members.", ["Place@2", "SeenForTesting"])),
            ("Math", ("Math members.", ["Offset", "Clamp"])),
        ])
        sgp.MAIN_MEMBERS = ["Place@1"]

    def tearDown(self):
        sgp.AREAS, sgp.MAIN_MEMBERS = self.saved
        shutil.rmtree(self.dir)

    def run_main(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = sgp.main(["--src", self.src] + list(args))
        return rc, out.getvalue()

    def read(self, name):
        with open(os.path.join(self.dir, name), encoding="utf-8") as fh:
            return fh.read()

    def test_split_moves_mapped_members_and_keeps_state(self):
        rc, _ = self.run_main()
        self.assertEqual(0, rc)
        main = self.read("ParsekFlightGhostPositioner.cs")
        orbit = self.read("ParsekFlightGhostPositioner.Orbit.cs")
        maths = self.read("ParsekFlightGhostPositioner.Math.cs")
        self.assertIn("    internal sealed partial class ParsekFlightGhostPositioner : IGhostPositioner\n", main)
        self.assertIn("    internal sealed partial class ParsekFlightGhostPositioner\n    {\n", orbit)
        for kept in ("private readonly ParsekFlight host;", "private const double Cap",
                     "IsPositive =", "void IGhostPositioner.Place(int index)", "seen = new"):
            self.assertIn(kept, main)
        self.assertIn("internal void Place(int index, double ut)", orbit)
        self.assertIn("// Moves with the Orbit group.", orbit)
        self.assertIn("SeenForTesting\n            => seen;", orbit)
        self.assertNotIn("internal void Place(int index, double ut)", main)
        self.assertIn("internal static int Clamp", maths)

    def test_each_file_keeps_only_the_usings_it_names(self):
        self.run_main()
        main = self.read("ParsekFlightGhostPositioner.cs")
        orbit = self.read("ParsekFlightGhostPositioner.Orbit.cs")
        maths = self.read("ParsekFlightGhostPositioner.Math.cs")
        # Main: Dictionary and Func; no Vector3, no CultureInfo.
        self.assertTrue(main.startswith("using System;\nusing System.Collections.Generic;\n\nnamespace Parsek\n"))
        # Orbit: CultureInfo only inside an interpolation hole, Dictionary in the property.
        self.assertTrue(orbit.startswith(
            "using System.Collections.Generic;\nusing System.Globalization;\n\nnamespace Parsek\n"))
        # Math: Math in code (the comment does not count) and Vector3.
        self.assertTrue(maths.startswith("using System;\nusing UnityEngine;\n\nnamespace Parsek\n"))

    def test_code_text_keeps_holes_and_drops_literals_and_comments(self):
        text = 'a /* Math */ $"x{CultureInfo.X} {{Vector3}}" + @"Func" // Guid\n'
        words = set(sgp.code_text(text).split())
        self.assertIn("CultureInfo.X", words)
        self.assertNotIn("Math", words)
        self.assertNotIn("Func", sgp.code_text(text))
        self.assertNotIn("Vector3", sgp.code_text(text))
        self.assertNotIn("Guid", sgp.code_text(text))

    def test_overloads_split_by_occurrence(self):
        parsed = sgp.parse_file(SRC)
        targets, unmapped = sgp.assign(parsed["members"])
        self.assertEqual([], unmapped)
        places = [t for m, t in zip(parsed["members"], targets) if m.name == "Place"]
        self.assertEqual([None, "Orbit"], places)

    def test_a_partly_numbered_overload_is_refused(self):
        sgp.MAIN_MEMBERS = []
        with self.assertRaises(sgp.SplitError):
            self.run_main("--check")

    def test_bare_and_numbered_mapping_of_one_name_is_refused(self):
        sgp.MAIN_MEMBERS = ["Place"]
        with self.assertRaises(sgp.SplitError):
            self.run_main("--check")

    def test_mapping_a_field_is_refused(self):
        sgp.AREAS["Orbit"] = ("x", ["Place@2", "SeenForTesting", "IsPositive"])
        with self.assertRaises(sgp.SplitError):
            self.run_main("--check")

    def test_stale_mapping_is_refused(self):
        sgp.AREAS["Orbit"] = ("x", ["Place@2", "SeenForTesting", "Gone"])
        with self.assertRaises(sgp.SplitError):
            self.run_main("--check")
        sgp.AREAS["Orbit"] = ("x", ["Place@2", "Place@3", "SeenForTesting"])
        with self.assertRaises(sgp.SplitError):
            self.run_main("--check")

    def test_an_unmapped_member_is_refused(self):
        sgp.AREAS["Math"] = ("x", ["Offset"])
        with self.assertRaises(sgp.SplitError):
            self.run_main("--check")

    def test_check_writes_nothing_and_a_second_run_is_refused(self):
        self.run_main("--check")
        self.assertFalse(os.path.exists(os.path.join(self.dir, "ParsekFlightGhostPositioner.Orbit.cs")))
        self.assertEqual(SRC, self.read("ParsekFlightGhostPositioner.cs"))
        self.run_main()
        with self.assertRaises(sgp.SplitError):
            self.run_main()

    def test_member_count_is_preserved(self):
        self.run_main()
        total = 0
        for name, decl in (("ParsekFlightGhostPositioner.cs", sgp.CLASS_DECL),
                           ("ParsekFlightGhostPositioner.Orbit.cs", sgp.PART_DECL),
                           ("ParsekFlightGhostPositioner.Math.cs", sgp.PART_DECL)):
            total += len(sgp.parse_file(self.read(name), decl)["members"])
        self.assertEqual(len(sgp.parse_file(SRC)["members"]), total)

    def test_the_mapping_names_every_group_of_the_plan(self):
        groups = list(self.saved[0].keys())
        self.assertEqual(["Points", "Orbit", "OrbitTailContinuity", "Checkpoint", "WorldPosition",
                          "RecordedRelative", "RelativeAnchor", "BodyFixedPrimary", "Trace", "Loop",
                          "TailLift"], groups)


if __name__ == "__main__":
    unittest.main()
