"""Pure tests for the stock tech-tree listing tool (`harness/tools/list_tech_unlocks.py`).

The listing is the craft budget of the scripted campaign program
(docs/dev/research/stock-tech-tree-part-unlocks.md). These cells pin the parsing
rules it depends on: stock cfg quirks are tolerated without dropping a part, the
cheapest research path is the one reported, and titles come out as plain ASCII.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import os
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_TOOLS = os.path.join(os.path.dirname(_HERE), "tools")
if _TOOLS not in sys.path:
    sys.path.insert(0, _TOOLS)

import list_tech_unlocks as ltu  # noqa: E402


class ParsePartsTests(unittest.TestCase):

    def test_inline_comments_and_a_stray_bare_word_keep_the_part(self):
        # The stock Mainsail cfg carries a bare `fxOriginalOffset` line inside a
        # MODULE; ConfigNode.Load ignores it and so must the listing.
        text = "\n".join([
            "PART",
            "{",
            "\tname = liquidEngine_v2 // the runtime name has a dot",
            "\tTechRequired = generalRocketry",
            "\ttitle = #autoLOC_1",
            "\tmass = 1.5",
            "\tMODULE",
            "\t{",
            "\t\tname = ModuleEngines",
            "\t\tfxOriginalOffset",
            "\t\tEngineType = LiquidFuel",
            "\t}",
            "}",
        ])
        parts = ltu.parse_parts(text, "x.cfg")
        self.assertEqual(1, len(parts))
        self.assertEqual("liquidEngine.v2", parts[0]["name"])
        self.assertEqual("generalRocketry", parts[0]["tech"])
        self.assertEqual("1.5", parts[0]["mass"])

    def test_a_brace_fault_is_not_tolerated(self):
        with self.assertRaises(ValueError):
            ltu.parse_parts("PART\n{\nname = a\n", "broken.cfg")


class TreeTests(unittest.TestCase):

    TREE = "\n".join([
        "TechTree", "{",
        "RDNode", "{", "id = start", "title = Start", "cost = 0", "}",
        "RDNode", "{", "id = a", "title = A", "cost = 5",
        "Parent", "{", "parentID = start", "}", "}",
        "RDNode", "{", "id = b", "title = B", "cost = 45",
        "Parent", "{", "parentID = start", "}", "}",
        "RDNode", "{", "id = c", "title = C", "cost = 10",
        "Parent", "{", "parentID = a", "}",
        "Parent", "{", "parentID = b", "}", "}",
        "}",
    ])

    def test_cumulative_cost_takes_the_cheapest_parent(self):
        nodes = ltu.parse_tech_tree(self.TREE)
        cum = ltu.cumulative_cost(nodes)
        self.assertEqual({"start": 0, "a": 5, "b": 45, "c": 15}, cum)

    def test_a_part_naming_no_node_is_listed_not_dropped(self):
        nodes = ltu.parse_tech_tree(self.TREE)
        parts = [{"name": "p1", "tech": "a"}, {"name": "rock", "tech": "Unresearcheable"},
                 {"name": "eva", "tech": ""}]
        by_node, orphans = ltu.build_listing(nodes, parts)
        self.assertEqual(["p1"], [p["name"] for p in by_node["a"]])
        self.assertEqual(["eva", "rock"], sorted(p["name"] for p in orphans))


class LocalizationTests(unittest.TestCase):

    def test_titles_resolve_and_stay_ascii(self):
        with tempfile.TemporaryDirectory() as gd:
            loc = os.path.join(gd, "Squad", "Localization")
            os.makedirs(loc)
            with open(os.path.join(loc, "dictionary.cfg"), "w", encoding="utf-8") as fh:
                fh.write("﻿Localization\n{\n\ten-us\n\t{\n"
                         "\t\t#autoLOC_1 = Mystery Goo™ Unit\\u0020\n"
                         "\t\t#autoLOC_2 = a // b = c\n\t}\n\tde-de\n\t{\n"
                         "\t\t#autoLOC_1 = Geheim\n\t}\n}\n")
            table = ltu.load_localization(gd)
        self.assertEqual("Mystery Goo Unit", ltu.localize("#autoLOC_1", table))
        self.assertEqual("a // b = c", ltu.localize("#autoLOC_2", table))
        self.assertEqual("plain", ltu.localize("plain", table))


if __name__ == "__main__":
    unittest.main()
