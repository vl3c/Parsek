"""Claim-to-token pin for the CL-4 stand-in lane (`CL-4-refly-crew-standin`).

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

CL-4 is CL-3's shape step for step (same fixture, same injected preset, same
mission machine, same conclusion) with ONE new claim, D12 `stand-ins`, and ONE new
REQUIRED token, the `Stand-in generated` line the tombstone's release of the
death-derived permanent reservation triggers. CL-1, CL-2 and CL-3 each carry a pin
file binding their claims to the tokens that gate them; this is CL-4's. Every cell
names the failure that would otherwise pass silently: a claim that outlives its
token, a token whose direction has been flipped to the inverted `forbidden` form the
registry's D12 re-pin record warns about, or a lane that has drifted off CL-3's shape
while still claiming to inherit its reasoning.
"""

import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
_LIB = os.path.join(_HARNESS, "lib")
if _LIB not in sys.path:
    sys.path.insert(0, _LIB)
import hlib  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "CL-4-refly-crew-standin.toml")
CL3_PATH = os.path.join(_HARNESS, "scenarios", "CL-3-refly-crew-tombstone.toml")
REGISTRY_PATH = os.path.join(_HARNESS, "coverage", "registry.toml")

STAND_IN_TOKEN_PREFIX = "Stand-in generated: "
STAND_IN_OWNER = "for slot 'Jebediah Kerman' depth 0"
REP_PENALTY_TOKEN = "Tombstoned [1-9][0-9]* career actions \\(.*Reputation=[1-9]"


def _load(path):
    with open(path, "rb") as fh:
        return tomllib.load(fh)


class Cl4ClaimPinTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.spec = _load(SPEC_PATH)
        cls.cl3 = _load(CL3_PATH)
        cls.registry = _load(REGISTRY_PATH)
        cls.claimed = cls.spec.get("dimensionsCovered", {})
        contracts = cls.spec["expectations"]["logContracts"]
        cls.required = list(contracts.get("required", []))
        cls.forbidden = list(contracts.get("forbidden", []))

    def _required(self, fragment):
        return any(fragment in tok for tok in self.required)

    def test_the_spec_validates_through_the_real_validator(self):
        verdict = hlib.validate_spec(self.spec, self.registry)
        self.assertTrue(verdict.ok, list(verdict.errors))

    def test_the_d12_claims_are_exactly_stand_ins_and_the_rep_penalty(self):
        # `tombstones` and `dead-crew-strip` are CL-3's; re-claiming them here would
        # double-count the same tokens on the same shape. `tombstone-rep-penalty`
        # joined on 2026-09-10 when the product change shipped.
        self.assertEqual(["stand-ins", "tombstone-rep-penalty"],
                         self.claimed.get("D12", []))
        self.assertEqual([], self.claimed.get("D9", []))

    def test_the_rep_penalty_claim_is_gated_by_its_own_required_token(self):
        # CLAIM-IS-NOT-GATE, second cell: the claim is legal only while the spec
        # REQUIRES a `Tombstoned` line whose Reputation= term is non-zero. It is a
        # SEPARATE token from CL-3's `Kerbal=[1-9]` one so a red names the new cell.
        self.assertIn(REP_PENALTY_TOKEN, self.required)
        self.assertTrue(self._required("Kerbal=[1-9]"),
                        "CL-3's own tombstone token must stay alongside it")
        self.assertFalse(any("Reputation=[1-9]" in tok for tok in self.forbidden))

    def test_the_registry_records_the_rep_penalty_cell_as_reachable(self):
        # Until 2026-09-10 the registry pinned this cell UNREACHABLE BY ANY FLIGHT. If
        # that wording ever comes back, this claim has lost its producer.
        with open(REGISTRY_PATH, encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("`tombstone-rep-penalty` IS REACHABLE", body)
        self.assertNotIn("`tombstone-rep-penalty` IS UNREACHABLE BY ANY FLIGHT", body)
        self.assertIn("tombstone-rep-penalty", self.registry["D12"]["values"])

    def test_the_stand_in_claim_is_gated_by_a_required_token(self):
        # CLAIM-IS-NOT-GATE: the claim is legal only while the spec REQUIRES the line
        # the registry pins the cell to, on the fixture's baked pod crew.
        self.assertTrue(self._required(STAND_IN_TOKEN_PREFIX),
                        "D12 stand-ins is claimed with no `Stand-in generated` token")
        self.assertTrue(self._required(STAND_IN_OWNER),
                        "the stand-in token must pin the slot OWNER and depth 0")

    def test_the_stand_in_token_is_never_forbidden(self):
        # THE INVERSION the registry's re-pin record documents: a Dead row makes the
        # reservation permanent and permanents get no slot, so a FORBIDDEN stand-in
        # gate passes exactly when the tombstone did nothing.
        self.assertFalse(any(STAND_IN_TOKEN_PREFIX in tok for tok in self.forbidden),
                         "a forbidden `Stand-in generated` row is the inverted gate")

    def test_the_stand_in_token_is_downstream_of_the_strip_chain(self):
        # The stand-in is a consequence; requiring the chain ahead of it makes a red
        # name its cause. All three predecessors are CL-3's own REQUIRED tokens.
        for fragment in ("supersede relations for subtree rooted at",
                         "Tombstoned [1-9][0-9]* career actions",
                         "(permanent=0 "):
            self.assertTrue(self._required(fragment), fragment)

    def test_the_shape_is_cl3s_step_for_step(self):
        # Same fixture, preset, mission and mission params, plus exactly one added
        # SetSetting (verboseLogging) and nothing else.
        self.assertEqual(self.cl3["fixture"], self.spec["fixture"])
        self.assertEqual(self.cl3["driver"]["mission"], self.spec["driver"]["mission"])
        self.assertEqual(self.cl3["driver"]["missionParams"],
                         self.spec["driver"]["missionParams"])
        cl3_steps = [s.get("cmd", "mission") for s in self.cl3["driver"]["steps"]]
        cl4_steps = [s.get("cmd", "mission") for s in self.spec["driver"]["steps"]]
        cl4_minus_verbose = list(cl4_steps)
        cl4_minus_verbose.remove("SetSetting")
        self.assertEqual(cl3_steps, cl4_minus_verbose)
        verbose = [s for s in self.spec["driver"]["steps"]
                   if s.get("cmd") == "SetSetting"
                   and s.get("args", {}).get("name") == "verboseLogging"]
        self.assertEqual(1, len(verbose))

    def test_the_rewind_block_is_armed_on_measured_facets(self):
        # Armed after the reading run (2026-09-09_1813) measured CL-3's numbers, the
        # tombstones floor re-pinned off 2026-09-09_2158 on the rep-penalty DLL;
        # `rewindPoints` stays unpinned because the merge journal reaps the RP.
        rewind = self.spec["expectations"]["rewind"]
        self.assertTrue(rewind.get("gating"))
        self.assertEqual({"min": 1}, rewind["supersedeRows"])
        # 2 since 2026-09-10: the death row and its KerbalDeath reputation penalty.
        self.assertEqual({"min": 2}, rewind["tombstones"])
        self.assertNotIn("rewindPoints", rewind)

    def test_the_registry_pins_stand_ins_to_a_generated_stand_in(self):
        # The pin this lane claims against must stay in the registry's D12 block.
        with open(REGISTRY_PATH, encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("`stand-ins` IS PINNED", body)
        self.assertIn("TryCreateGeneratedStandIn", body)
        self.assertIn("stand-ins", self.registry["D12"]["values"])


if __name__ == "__main__":
    unittest.main()
