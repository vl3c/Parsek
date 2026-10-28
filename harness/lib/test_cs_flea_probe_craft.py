"""Drift and shape gates for `CS Flea Probe.craft` (RW-5's craft, in the `cs-career-mun4`
fixture).

The craft is DERIVED from the shared `Jumping Flea.craft` by
`harness/tools/build_cs_flea_probe_craft.py`: the stock Flea (fresh persistentIds) plus a
probe stand on the booster's side, a TT-38K radial decoupler holding an FL-T100 with a
Stayputnik on top. A hand edit to the craft, a change to the derivation, a part the host
has not researched, or a split that no longer leaves two command parts reds here instead
of in a flight whose decouple authors no rewind point.
"""
import hashlib
import importlib.util
import math
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(HARNESS_ROOT, "tools")
BUILDER = os.path.join(TOOLS, "build_cs_flea_probe_craft.py")
ORBITER_BUILDER = os.path.join(TOOLS, "build_cs_orbiter_craft.py")
BASE = os.path.join(HARNESS_ROOT, "fixtures", "ships", "Jumping Flea.craft")
HOST_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun4")
VAB = os.path.join(HOST_DIR, "Ships", "VAB")
CRAFT = os.path.join(VAB, "CS Flea Probe.craft")
HOST_SFS = os.path.join(HOST_DIR, "persistent.sfs")

ADDED = ["radialDecoupler", "fuelTankSmallFlat", "probeCoreSphere.v2"]
# Parts whose stock cfg carries ModuleCommand (the controllable test,
# ParsekFlight.IsTrackableVessel): the Mk1 pod and the Stayputnik.
COMMAND_PARTS = {"mk1pod.v2", "probeCoreSphere.v2"}


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    # The shared base craft is CRLF (KSP-written); the builder normalizes it the same way.
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _blocks(text):
    """{part token: block text} in file order (dicts keep insertion order)."""
    out = {}
    for blk in text.split("\nPART\n{\n")[1:]:
        token = re.match(r"\tpart = (\S+)\n", blk).group(1)
        out[token] = blk.split("\n}", 1)[0]
    return out


def _name(token):
    return token.rsplit("_", 1)[0]


def _scalar(block, key):
    return re.search(r"(?m)^\t%s = (.*)$" % re.escape(key), block).group(1)


def _vec(block, key):
    return tuple(float(c) for c in _scalar(block, key).split(","))


class CsFleaProbeCraftTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = _load(BUILDER, "build_cs_flea_probe_craft")
        cls.text = _read(CRAFT)
        cls.blocks = _blocks(cls.text)
        cls.base_blocks = _blocks(_read(BASE))

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.check())

    def test_every_tail_matches_its_stock_donor_when_the_install_is_present(self):
        if not os.path.isdir(self.b.DEFAULT_DONOR_SHIPS):
            self.skipTest("no stock-tree install at %s" % self.b.DEFAULT_DONOR_SHIPS)
        self.assertEqual([], self.b.verify_donors(self.b.DEFAULT_DONOR_SHIPS))

    def test_the_other_cs_craft_on_the_host_still_derive_unchanged(self):
        # The host's own CS craft are copies its harvests carried forward; each is still
        # exactly what its builder variant produces (build_cs_orbiter_craft.py).
        o = _load(ORBITER_BUILDER, "build_cs_orbiter_craft")
        for fname, built in (("CS Orbiter.craft", o.build()),
                             ("CS Mun Flyby.craft", o.mun_build()),
                             ("CS Mun Science.craft", o.munsci_build())):
            with self.subTest(craft=fname):
                with open(os.path.join(VAB, fname), encoding="utf-8", newline="") as fh:
                    self.assertEqual(built, fh.read())

    def test_it_is_the_flea_plus_exactly_the_probe_stand(self):
        base = list(self.base_blocks)
        tokens = list(self.blocks)
        self.assertEqual(base, tokens[:len(base)])
        self.assertEqual(ADDED, [_name(t) for t in tokens[len(base):]])

    def test_every_flea_block_is_the_base_but_its_persistent_id_and_the_one_new_link(self):
        for token, base_block in self.base_blocks.items():
            with self.subTest(part=token):
                strip = lambda b: re.sub(r"(?m)^\tpersistentId = .*\n", "", b)
                mine = strip(self.blocks[token])
                if token == self.b.HOST:
                    mine = mine.replace("\tlink = radialDecoupler_%d\n" % self.b.DECOUPLER_UID,
                                        "", 1)
                self.assertEqual(strip(base_block), mine)

    def test_no_persistent_id_is_shared_with_the_jumping_flea(self):
        # A craft-baked persistentId is reused on every launch; RW-5 records both craft.
        base = _read(BASE)
        theirs = set(re.findall(r"(?m)^\t?persistentId = (\d+)$", base))
        mine = re.findall(r"(?m)^\t?persistentId = (\d+)$", self.text)
        self.assertEqual(len(mine), len(set(mine)))
        self.assertEqual(set(), theirs & set(mine))

    def test_every_added_part_is_linked_from_its_parent(self):
        # A child its parent does not link is dropped at craft load, silently.
        for token in list(self.blocks)[len(self.base_blocks):]:
            with self.subTest(part=token):
                self.assertIn("\tlink = %s\n" % token, self.text)

    def test_the_decouple_leaves_two_command_parts_one_on_each_side(self):
        # The part tree from the link lines; cutting the TT-38K off its host is the split.
        children = {t: re.findall(r"(?m)^\tlink = (\S+)$", b) for t, b in self.blocks.items()}
        dec = "radialDecoupler_%d" % self.b.DECOUPLER_UID

        def subtree(root):
            out, todo = [], [root]
            while todo:
                t = todo.pop()
                out.append(t)
                todo.extend(children[t])
            return out
        stand = set(subtree(dec))
        root = list(self.blocks)[0]
        flea = set(subtree(root)) - stand
        self.assertEqual(set(self.blocks), stand | flea)
        self.assertEqual({"probeCoreSphere.v2"}, {_name(t) for t in stand} & COMMAND_PARTS)
        self.assertEqual({"mk1pod.v2"}, {_name(t) for t in flea} & COMMAND_PARTS)
        self.assertEqual("mk1pod.v2", _name(root))

    def test_the_decoupler_is_radial_and_its_kick_is_zero(self):
        block = self.blocks["radialDecoupler_%d" % self.b.DECOUPLER_UID]
        self.assertRegex(block, r"(?m)^\tsrfN = srfAttach,%s$" % re.escape(self.b.HOST))
        self.assertIn("\t\tname = ModuleAnchoredDecoupler\n", block)
        self.assertIn("\t\tejectionForcePercent = 0\n", block)
        tail = block[block.index("\tEVENTS\n"):]
        self.assertEqual(self.b.TAILS["radialDecoupler"].replace(
            self.b.EJECTION_FROM, self.b.EJECTION_TO), tail)

    def test_the_stand_stands_on_the_pad_beside_the_booster(self):
        host = self.blocks[self.b.HOST]
        tank = self.blocks["fuelTankSmallFlat_%d" % self.b.TANK_UID]
        booster_bottom = _vec(host, "pos")[1] + self.b.RT5_BOTTOM_NODE
        tank_bottom = _vec(tank, "pos")[1] - self.b.T100_HALF
        self.assertAlmostEqual(booster_bottom, tank_bottom, places=6)
        # The TT-38K sits on the fins' band, where the base measures the body's radius.
        dec = self.blocks["radialDecoupler_%d" % self.b.DECOUPLER_UID]
        fin_band = {_vec(b, "attPos0")[1] for t, b in self.base_blocks.items()
                    if t.startswith("basicFin_")}
        self.assertEqual(1, len(fin_band))
        self.assertAlmostEqual(fin_band.pop(), _vec(dec, "attPos0")[1], places=9)
        # Clear of the fins at azimuths 90 / 210 / 330 (60 degrees from each).
        x, _, z = _vec(tank, "pos")
        self.assertAlmostEqual(270.0, math.degrees(math.atan2(z, x)) % 360.0, places=6)

    def test_the_stage_order_drops_the_stand_first(self):
        istg = {_name(t): int(_scalar(b, "istg")) for t, b in self.blocks.items()
                if int(_scalar(b, "sidx")) >= 0}
        self.assertEqual({"radialDecoupler": 2, "solidBooster.sm.v2": 1,
                          "parachuteSingle": 0}, istg)

    def test_every_part_is_researched_on_the_host(self):
        sfs = _read(HOST_SFS)
        rnd = sfs[sfs.index("\t\tname = ResearchAndDevelopment\n"):]
        rnd = rnd[:rnd.index("\n\t\tScience\n")]
        researched = set()
        for tech in rnd.split("\t\tTech\n")[1:]:
            if re.search(r"(?m)^\t\t\tstate = Available$", tech):
                researched |= set(re.findall(r"(?m)^\t\t\tpart = (\S+)$", tech))
        self.assertEqual(set(), {_name(t) for t in self.blocks} - researched)

    def test_the_ship_is_named_for_the_editor_step(self):
        self.assertRegex(self.text, r"(?m)^ship = CS Flea Probe$")

    def test_the_jumping_flea_base_is_the_committed_shared_library_craft(self):
        # The derivation reads the library copy; this pins that the base did not move
        # under it (sha256 of the committed bytes, 2026-10-28).
        with open(BASE, "rb") as fh:
            digest = hashlib.sha256(fh.read()).hexdigest()
        self.assertEqual(JUMPING_FLEA_SHA256, digest)


JUMPING_FLEA_SHA256 = "6e820d753a1da739478ba60aaf245c3e20b178d1dab876cb4aa51b8d351f1166"


if __name__ == "__main__":
    unittest.main()
