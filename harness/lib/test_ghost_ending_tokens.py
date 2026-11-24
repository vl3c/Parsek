"""A required ghost-ending token must not be met by the quit alone.

`GhostPlaybackEngine.DestroyAllGhosts` writes `phase=MeshDestroyed ... reason=engine teardown`
for every ghost still alive, so a required `MeshDestroyed` token whose reason is open says
nothing about the ghost's own ending (todo GHOST-ENDING-TOKENS-MET-BY-THE-QUIT).
The reason literal is held to the engine by `test_ghostlife`.
"""
import glob
import os
import re
import tomllib
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCENARIOS = os.path.join(HERE, "..", "scenarios")

# (spec id, vessel) whose open or teardown token is backed by an exact count in the same spec.
BACKED = {
    # Six `Stock FXMonger.Explode` lines per replay.
    ("GS-8-kerbalx-zone-round-trip", "Kerbal X Debris"),
    # Exact destroy census and explosion count per cycle.
    ("GS-9-kerbalx-repeat-rewind", "Kerbal X Debris"),
    # The second rewind's own teardown, pinned as the census' first cycle-2 destroy.
    ("GS-9-kerbalx-repeat-rewind", "Kerbal X Probe"),
}

# Required destroy tokens whose vessel VESSEL_RE cannot read (an escaped or captured name, a
# vessel not before the reason), per spec: read by hand, each closes its reason or is bound to
# an earlier line. A new one must be read and counted here, never skipped in silence.
UNREAD = {
    "GS-6-part-event-applier-sweep": 1,
    "GS-8-kerbalx-zone-round-trip": 1,
    "GS-9-kerbalx-repeat-rewind": 2,
    "MC-4-making-history-desert": 1,
    "S1.9-part-showcase-render": 1,
    "V22M-kerbin-splashdown-player-loop": 1,
    "V23M-mun-landing-player-loop": 1,
}

VESSEL_RE = re.compile(r"phase=MeshDestroyed [^\"]*?vessel=([A-Za-z0-9 \-]+?)(?: reason=|$)")


def teardown_line(vessel):
    return ("[LOG 00:00:01.000] [Parsek][INFO][GhostRenderTrace] phase=MeshDestroyed rec=0123abcd "
            "recId=0123abcd ghostIndex=0 frame=1 currentUT=1.000 playbackUT=1.000 "
            "vessel=%s reason=engine teardown" % vessel)


def met_by_the_quit(token):
    """The vessel of a token a quit-only log meets, else None."""
    m = VESSEL_RE.search(token)
    if m is None:
        return None
    vessel = m.group(1)
    log = "flushandquit: Application.Quit\n" + teardown_line(vessel) + "\n"
    return vessel if re.search(token, log) is not None else None


class GhostEndingTokenTests(unittest.TestCase):

    def test_no_required_destroy_token_is_met_by_the_quit_alone(self):
        tokens = 0
        offenders = set()
        unread = {}
        for path in sorted(glob.glob(os.path.join(SCENARIOS, "*.toml"))):
            with open(path, "rb") as f:
                spec = tomllib.load(f)
            contracts = spec.get("expectations", {}).get("logContracts", {}) or {}
            for token in contracts.get("required", []) or []:
                if "phase=MeshDestroyed " not in token:
                    continue
                tokens += 1
                if VESSEL_RE.search(token) is None:
                    unread[spec["id"]] = unread.get(spec["id"], 0) + 1
                vessel = met_by_the_quit(token)
                if vessel is not None:
                    offenders.add((spec["id"], vessel))
        self.assertGreater(tokens, 20)
        self.assertEqual(BACKED, offenders)
        self.assertEqual(UNREAD, unread)

    def test_the_old_token_shapes_are_caught(self):
        self.assertEqual("Kerbal X Probe", met_by_the_quit(
            r"phase=MeshDestroyed [^\r\n]*vessel=Kerbal X Probe reason="))
        self.assertEqual("Kerbal X Debris", met_by_the_quit(
            r"phase=MeshDestroyed [^\r\n]*vessel=Kerbal X Debris"))
        self.assertIsNone(met_by_the_quit(
            r"phase=MeshDestroyed [^\r\n]*vessel=Kerbal X Debris reason=(?!engine teardown)"))


if __name__ == "__main__":
    unittest.main()
