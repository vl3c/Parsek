"""Gates for `harness/tools/build_psub_splashed.py` (the submarine kit's sea host).

The real source (`fixtures/saves/psub-pad`, FORGE-psub-pad's harvest) is not committed
until the forge flies, so the derivation runs here on a SYNTHETIC pad save: the forge
base's own PRELAUNCH vessel (mk1-capsule on the LaunchPad) renamed PSUB-1, retitled
`psub-pad`, with a Parsek tree node added to its ParsekScenario. The committed-fixture
contract (PENDING while neither directory exists, FAIL when only one does) is pinned too.

Stdlib only; no KSP. ASCII only.
"""
import contextlib
import importlib.util
import io
import os
import shutil
import tempfile
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_psub_splashed.py")
BASE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "bdock-forge-base")


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = _load(BUILDER, "build_psub_splashed")


def _synthetic_pad_text():
    with open(os.path.join(BASE, "persistent.sfs"), encoding="utf-8", newline="") as fh:
        text = fh.read()
    assert text.count("\t\t\tname = mk1-capsule\n") == 1
    text = text.replace("\t\t\tname = mk1-capsule\n", "\t\t\tname = PSUB-1\n")
    text = text.replace("\tTitle = smoke-fixture (SANDBOX)\n", "\tTitle = psub-pad (SANDBOX)\n")
    text = text.replace("\t\tgameStateEventCount = 1\n\t}",
                        "\t\tgameStateEventCount = 1\n\t\tparsekSaveFormat = 1\n"
                        "\t\tRECORDING_TREE\n\t\t{\n\t\t\tid = abc\n\t\t\tRECORDING\n"
                        "\t\t\t{\n\t\t\t\tx = 1\n\t\t\t}\n\t\t}\n\t}", 1)
    return text


def _vessel_lines(text, name):
    lines = text.split("\n")
    lo, hi = builder.vessel_span(lines, name)
    return lines[lo:hi]


class DerivationTests(unittest.TestCase):
    def setUp(self):
        self.src = _synthetic_pad_text()
        self.out = builder.build_sfs_text(self.src)

    def test_the_vessel_is_splashed_at_the_site(self):
        v = _vessel_lines(self.out, "PSUB-1")
        for line in ("\t\t\tsit = SPLASHED", "\t\t\tlanded = False", "\t\t\tlandedAt = ",
                     "\t\t\tdisplaylandedAt = ", "\t\t\tsplashed = True",
                     "\t\t\tlat = -0.0972", "\t\t\tlon = -74.15",
                     "\t\t\talt = %r" % builder.ROOT_ABOVE_HULL,
                     "\t\t\thgt = %r" % builder.ROOT_ABOVE_HULL):
            self.assertIn(line, v)
        self.assertIn("\t\t\tlaunchedFrom = LaunchPad", v)  # kept: where it launched

    def test_the_orbit_is_the_canonical_surface_tuple_with_its_ref_kept(self):
        v = _vessel_lines(self.out, "PSUB-1")
        i = v.index("\t\t\tORBIT")
        orbit = v[i + 2:v.index("\t\t\t}", i)]
        self.assertEqual(["\t\t\t\tSMA = 0", "\t\t\t\tECC = 1", "\t\t\t\tINC = 0",
                          "\t\t\t\tLPE = 0", "\t\t\t\tLAN = 0", "\t\t\t\tMNA = 0",
                          "\t\t\t\tEPH = 0", "\t\t\t\tREF = 1", "\t\t\t\tIDENT = Squad/Kerbin"],
                         orbit)

    def test_only_the_named_lines_change(self):
        a, b = self.src.split("\n"), self.out.split("\n")
        # The scenario strip removes lines; compare the vessel nodes line by line.
        va, vb = _vessel_lines(self.src, "PSUB-1"), _vessel_lines(self.out, "PSUB-1")
        self.assertEqual(len(va), len(vb))
        changed = sorted({x.strip().split(" =")[0] for x, y in zip(va, vb) if x != y})
        self.assertEqual(sorted(["sit", "landed", "landedAt", "displaylandedAt", "splashed",
                                 "lat", "lon", "alt", "hgt", "SMA", "ECC", "INC", "LPE",
                                 "LAN", "MNA", "EPH"]), changed)
        self.assertLess(len(b), len(a))
        # Every other VESSEL (the asteroid) is byte-identical.
        self.assertEqual(_vessel_lines(self.src, "Ast. VPA-167"),
                         _vessel_lines(self.out, "Ast. VPA-167"))

    def test_the_parsek_scenario_is_stripped_and_the_title_moved(self):
        self.assertNotIn("RECORDING_TREE", self.out)
        self.assertNotIn("parsekSaveFormat", self.out)
        self.assertIn("\t\tname = ParsekScenario\n\t\tscene = 7, 5, 8, 6\n"
                      "\t\tgameStateEventCount = 1\n\t}", self.out)
        self.assertIn("\tTitle = psub-splashed (SANDBOX)\n", self.out)
        self.assertNotIn("psub-pad", self.out)

    def test_the_hull_centre_starts_at_sea_level(self):
        self.assertAlmostEqual(0.9985339, builder.ROOT_ABOVE_HULL, places=5)

    def test_a_source_not_on_the_pad_is_refused(self):
        bad = self.src.replace("\t\t\tsit = PRELAUNCH\n", "\t\t\tsit = LANDED\n", 1)
        with self.assertRaises(SystemExit):
            builder.build_sfs_text(bad)

    def test_a_missing_or_doubled_vessel_is_refused(self):
        with self.assertRaises(SystemExit):
            builder.build_sfs_text(self.src.replace("name = PSUB-1", "name = Other"))
        lines = self.src.split("\n")
        lo, hi = builder.vessel_span(lines, "PSUB-1")
        doubled = lines[:hi + 1] + lines[lo - 1:hi + 1] + lines[hi + 1:]
        with self.assertRaises(SystemExit):
            builder.build_sfs_text("\n".join(doubled))

    def test_the_output_is_lf_and_ascii(self):
        self.assertNotIn("\r", self.out)
        self.assertTrue(all(ord(c) < 128 for c in self.out))


class FixtureContractTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="psub-splashed-test-")
        self.src = os.path.join(self.tmp, "psub-pad")
        self.out = os.path.join(self.tmp, "psub-splashed")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _stage_source(self):
        os.makedirs(os.path.join(self.src, "AddOns"))
        shutil.copy(os.path.join(BASE, "persistent.loadmeta"), self.src)
        for name in os.listdir(os.path.join(BASE, "AddOns")):
            p = os.path.join(BASE, "AddOns", name)
            if os.path.isfile(p):
                shutil.copy(p, os.path.join(self.src, "AddOns"))
        os.makedirs(os.path.join(self.src, "Ships", "VAB"))
        with open(os.path.join(self.src, "Ships", "VAB", "PSUB-1.craft"), "w") as fh:
            fh.write("ship = PSUB-1\n")
        with open(os.path.join(self.src, "persistent.sfs"), "w", encoding="utf-8",
                  newline="") as fh:
            fh.write(_synthetic_pad_text())

    def _main(self, *args):
        with contextlib.redirect_stdout(io.StringIO()):
            return builder.main(list(args) + ["--source", self.src, "--out", self.out])

    def test_pending_while_neither_exists(self):
        self.assertIsNone(builder.committed_problems(self.src, self.out))
        self.assertEqual(0, self._main("--check"))

    def test_write_then_check_is_ok_and_ships_are_not_copied(self):
        self._stage_source()
        self.assertEqual(1, self._main("--check"))      # source only: FAIL
        self.assertEqual(0, self._main("--write"))
        self.assertEqual([], builder.committed_problems(self.src, self.out))
        self.assertEqual(0, self._main("--check"))
        self.assertFalse(os.path.exists(os.path.join(self.out, "Ships")))
        self.assertTrue(os.path.isfile(os.path.join(self.out, "persistent.loadmeta")))
        with open(os.path.join(self.out, "persistent.sfs"), "a") as fh:
            fh.write("x")
        self.assertEqual(["drift in persistent.sfs"],
                         builder.committed_problems(self.src, self.out))

    def test_output_without_source_fails(self):
        os.makedirs(self.out)
        self.assertEqual(1, self._main("--check"))

    def test_write_without_source_refuses(self):
        with self.assertRaises(SystemExit):
            self._main("--write")

    def test_the_committed_state_passes_check(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, builder.main(["--check"]))


if __name__ == "__main__":
    unittest.main()
