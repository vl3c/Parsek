"""Unit tests for saveparse.py, the pure M-C2/R9 save-parse verifier core.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Two test corpora, deliberately:

1. The COMMITTED fixture saves under harness/fixtures/saves/ - real
   Parsek-shaped .sfs text staged into every live run. Their structural counts
   are PINNED EXACTLY (which fixtures carry the spliced inert ParsekScenario
   node, and that every one of them carries zero trees / staging rows), so a
   fixture edit that changes the parse surface reds HERE, not on the next
   nightly. The rich tree/RP payloads (rewind-b9) are injected at stage time by
   `dotnet test --filter InjectRewindB9` and are NOT committed as saves, which
   is why corpus 2 exists.

2. PRODUCTION-SHAPED synthetic text, authored against the C# writers
   (ParsekScenario.SaveRewindStagingState, RecordingTree.Save,
   RecordingTreeRecordCodec.SaveRecordingInto, RewindPoint.SaveInto,
   RecordingSupersedeRelation.SaveInto, LedgerTombstone.SaveInto,
   ChildSlot.SaveInto) and the RewindB9Fixture / ScenarioWriter injected shape
   (ids b9-stack-root / b9-upper-b / b9-booster-a / rp_b9_root). Every load-
   bearing writer quirk is exercised: numeric BRANCH_POINT.type (so
   VesselSwitchContinuation is `type = 8`), name-string mergeState with the
   Immutable default OMITTED, absent staging parents meaning zero rows, POINT
   (not ENTRY) as the RewindPoints child, duplicate parentId/childId keys as
   data, and the isActive/isPending tree markers.

Plus the adversarial mutations a hand-edited or torn file produces: truncated
text, unbalanced braces, duplicated ids, duplicated ParsekScenario nodes,
missing nodes. The binding property throughout: a file that cannot be parsed
must NEVER read as "zero rows" (parsed=False, and with a block declared the
evaluator raises a named mismatch).
"""

import importlib.util
import os
import re
import tomllib
import unittest

import saveparse

LIB_DIR = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(LIB_DIR)
FIXTURE_SAVES_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves")
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")

# `observed_routes_facets` over a save carrying NO route surface at all. Spelled
# out rather than derived so a facet KEY that silently disappeared (or arrived)
# reds here: a fixture map full of `{}` would agree with any shape.
NO_ROUTES_FACET = {
    "count": 0, "dormant": 0, "stops": 0, "sourceRefs": 0,
    "completedCycles": 0, "skippedCycles": 0, "codecRejects": 0, "unparsed": 0,
    "unknownStatuses": 0, "unknownConnectionKinds": 0,
    "statuses": {}, "connectionKinds": {},
    "originBodies": {}, "destinationBodies": {}, "holdKinds": {},
    "ids": [], "destinationVesselPids": [],
    "dismissedCandidates": 0, "promptedCandidates": 0,
}


def _read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def load_spec(name):
    """One committed scenario spec, so a cell can assert against the REAL
    declared window rather than a literal of its own (which would stay green
    when the committed spec drifts)."""
    with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
        return tomllib.load(fh)


# ---------------------------------------------------------------------------
# Production-shaped synthetic corpus (corpus 2). Shapes pinned against the C#
# writers; ids from RewindB9Fixture / ScenarioWriter so the text models exactly
# what an injected rewind-b9 save carries after an S4.1-style merge.
# ---------------------------------------------------------------------------

B9_MERGED_SFS = """\
GAME
{
	version = 1.12.5
	Title = gloops-airshow
	SCENARIO
	{
		name = DiscoverableObjects
		scene = 7, 8, 5
	}
	SCENARIO
	{
		name = ParsekScenario
		scene = 7, 5, 8, 6
		missionHideArchived = False
		gameStateEventCount = 1
		RECORDING_TREE
		{
			id = tree-b9-stack-root
			treeName = B9 Stack
			rootRecordingId = b9-stack-root
			treeFormatVersion = 0
			recordingSchemaGeneration = 4
			RECORDING
			{
				recordingId = b9-stack-root
				vesselName = B9 Stack
				treeId = tree-b9-stack-root
				vesselPersistentId = 200001
				recordingFormatVersion = 1
				recordingSchemaGeneration = 4
				loopPlayback = False
				loopIntervalSeconds = 0
				lastResIdx = -1
				pointCount = 5
				recordingGroup = Rewind-B9
			}
			RECORDING
			{
				recordingId = b9-upper-b
				vesselName = B9 Upper B
				treeId = tree-b9-stack-root
				vesselPersistentId = 200002
				recordedVesselGuid = 7bfa0000-0000-0000-0000-00000000b9ab
				terminalState = 0
				recordingFormatVersion = 1
				recordingSchemaGeneration = 4
				loopPlayback = False
				loopIntervalSeconds = 0
				lastResIdx = -1
				pointCount = 3
				recordingGroup = Rewind-B9
			}
			RECORDING
			{
				recordingId = b9-booster-a
				vesselName = B9 Booster A
				treeId = tree-b9-stack-root
				vesselPersistentId = 200003
				terminalState = 4
				terrainHeightAtEnd = 75
				recordingFormatVersion = 1
				recordingSchemaGeneration = 4
				loopPlayback = False
				loopIntervalSeconds = 0
				lastResIdx = -1
				pointCount = 4
				recordingGroup = Rewind-B9
			}
			RECORDING
			{
				recordingId = b9-booster-refly
				vesselName = B9 Booster A
				treeId = tree-b9-stack-root
				vesselPersistentId = 200004
				terminalState = 4
				recordingFormatVersion = 1
				recordingSchemaGeneration = 4
				loopPlayback = False
				loopIntervalSeconds = 0
				lastResIdx = -1
				pointCount = 9
				mergeState = CommittedProvisional
			}
			BRANCH_POINT
			{
				id = bp_b9_root
				ut = 81.159999999999638
				type = 0
				parentId = b9-stack-root
				childId = b9-upper-b
				childId = b9-booster-a
				splitCause = DECOUPLE
				rewindPointId = rp_b9_root
			}
			BRANCH_POINT
			{
				id = bp_b9_switch
				ut = 141.15999999999964
				type = 8
				parentId = b9-upper-b
				childId = b9-booster-refly
			}
		}
		REWIND_POINTS
		{
			POINT
			{
				rewindPointId = rp_b9_root
				branchPointId = bp_b9_root
				ut = 81.159999999999638
				quicksaveFilename = rp_b9_root.sfs
				sessionProvisional = True
				focusSlotIndex = 0
				CHILD_SLOT
				{
					slotIndex = 0
					originChildRecordingId = b9-upper-b
					controllable = True
				}
				CHILD_SLOT
				{
					slotIndex = 1
					originChildRecordingId = b9-booster-a
					controllable = True
				}
				PID_SLOT_MAP
				{
					pid = 3049371
					slot = 0
				}
			}
		}
		RECORDING_SUPERSEDES
		{
			ENTRY
			{
				relationId = rsr_0123456789abcdef0123456789abcdef
				oldRecordingId = b9-booster-a
				newRecordingId = b9-booster-refly
				ut = 81.159999999999638
				createdRealTime = 2026-07-30T09:40:00Z
			}
		}
		LEDGER_TOMBSTONES
		{
			ENTRY
			{
				tombstoneId = tomb_0123456789abcdef0123456789abcdef
				actionId = act_0123456789abcdef0123456789abcdef
				retiringRecordingId = b9-booster-a
				ut = 81.159999999999638
			}
		}
		RECORDING_REWIND_RETIREMENTS
		{
			ENTRY
			{
				retirementId = rrt_0123456789abcdef0123456789abcdef
				recordingId = b9-booster-a
				restoredRecordingId =
				sourceSupersedeRelationId = rsr_0123456789abcdef0123456789abcdef
				rewindUT = 81.159999999999638
				createdUT = 141.15999999999964
				reason = rewound-out-supersede-old-side
			}
		}
	}
	FLIGHTSTATE
	{
		version = 1.12.5
		UT = 21.159999999999638
	}
}
"""

# An in-flight save: one committed tree, one ACTIVE tree, one PENDING tree.
THREE_TREE_SFS = """\
GAME
{
	SCENARIO
	{
		name = ParsekScenario
		gameStateEventCount = 0
		RECORDING_TREE
		{
			id = tree-committed
			treeName = Done
			rootRecordingId = rec-committed
			treeFormatVersion = 0
			recordingSchemaGeneration = 4
			RECORDING
			{
				recordingId = rec-committed
				vesselName = Done
				vesselPersistentId = 1
				terminalState = 1
				recordingFormatVersion = 1
				recordingSchemaGeneration = 4
				loopPlayback = False
				loopIntervalSeconds = 0
				lastResIdx = -1
				pointCount = 2
			}
		}
		RECORDING_TREE
		{
			id = tree-live
			treeName = Live
			rootRecordingId = rec-live
			treeFormatVersion = 0
			recordingSchemaGeneration = 4
			isActive = True
			RECORDING
			{
				recordingId = rec-live
				vesselName = Live
				vesselPersistentId = 2
				recordingFormatVersion = 1
				recordingSchemaGeneration = 4
				loopPlayback = False
				loopIntervalSeconds = 0
				lastResIdx = -1
				pointCount = 1
				mergeState = NotCommitted
			}
		}
		RECORDING_TREE
		{
			id = tree-stash
			treeName = Stash
			rootRecordingId = rec-stash
			treeFormatVersion = 0
			recordingSchemaGeneration = 4
			isPending = True
			RECORDING
			{
				recordingId = rec-stash
				vesselName = Stash
				vesselPersistentId = 3
				isDebris = True
				parentAnchorRecordingId = rec-live
				recordingFormatVersion = 1
				recordingSchemaGeneration = 4
				loopPlayback = False
				loopIntervalSeconds = 0
				lastResIdx = -1
				pointCount = 1
				mergeState = NotCommitted
			}
		}
	}
}
"""


class SfsTextParserTests(unittest.TestCase):
    """The generic ConfigNode-text layer: KSP's exact output shape parses, and
    every malformation is a DEFINED fault, never an exception or a silent
    empty tree."""

    def test_values_children_and_duplicate_keys(self):
        res = saveparse.parse_sfs(
            "NODE\n{\n\ta = 1\n\ta = 2\n\tempty = \n\tCHILD\n\t{\n\t\tb = x\n\t}\n}\n")
        self.assertTrue(res.ok)
        node = res.root.first("NODE")
        self.assertEqual("1", node.value("a"))          # first-wins read
        self.assertEqual(["1", "2"], node.values_named("a"))  # duplicates are data
        self.assertEqual("", node.value("empty"))
        self.assertEqual("x", node.first("CHILD").value("b"))

    def test_single_line_open_and_comments_tolerated(self):
        res = saveparse.parse_sfs("// header comment\nNODE {\n\tk = v\n}\n")
        self.assertTrue(res.ok)
        self.assertEqual("v", res.root.first("NODE").value("k"))

    def test_inline_empty_node_form(self):
        # The smoke-test staged fixture writes exactly "GAME { }" - ConfigNode's
        # PreFormatConfig splits braces onto their own tokens, so must we.
        res = saveparse.parse_sfs("GAME { }\n")
        self.assertTrue(res.ok, res.error)
        self.assertEqual(["GAME"], [n.name for n in res.root.nodes])
        self.assertEqual([], res.root.first("GAME").nodes)

    def test_empty_text_parses_to_empty_root(self):
        res = saveparse.parse_sfs("")
        self.assertTrue(res.ok)
        self.assertEqual([], res.root.nodes)

    def test_value_containing_equals_keeps_tail(self):
        res = saveparse.parse_sfs("NODE\n{\n\tk = a = b\n}\n")
        self.assertEqual("a = b", res.root.first("NODE").value("k"))

    def test_truncated_file_is_a_parse_fault(self):
        # Cut B9_MERGED_SFS mid-node: unclosed braces must red the parse, never
        # read as fewer rows.
        truncated = B9_MERGED_SFS[: len(B9_MERGED_SFS) // 2]
        res = saveparse.parse_sfs(truncated)
        self.assertFalse(res.ok)
        self.assertIn("unclosed", res.error)

    def test_unbalanced_close_is_a_parse_fault(self):
        res = saveparse.parse_sfs("NODE\n{\n}\n}\n")
        self.assertFalse(res.ok)
        self.assertIn("unbalanced close", res.error)

    def test_brace_without_name_is_a_parse_fault(self):
        res = saveparse.parse_sfs("{\n\tk = v\n}\n")
        self.assertFalse(res.ok)
        self.assertIn("brace without node name", res.error)

    def test_dangling_name_is_a_parse_fault(self):
        res = saveparse.parse_sfs("NODE\n{\n\tk = v\n}\nORPHAN\n")
        self.assertFalse(res.ok)
        self.assertIn("dangling node name", res.error)


class B9SnapshotTests(unittest.TestCase):
    """The production-shaped merged-B9 text: every surface extracted, exact
    ids/counts pinned."""

    @classmethod
    def setUpClass(cls):
        cls.snap = saveparse.parse_parsek_scenario(B9_MERGED_SFS)

    def test_parsed_and_found(self):
        self.assertTrue(self.snap.parsed)
        self.assertEqual("", self.snap.error)
        self.assertTrue(self.snap.scenario_found)

    def test_tree_topology(self):
        self.assertEqual(1, len(self.snap.trees))
        tree = self.snap.trees[0]
        self.assertEqual("tree-b9-stack-root", tree.tree_id)
        self.assertEqual("b9-stack-root", tree.root_recording_id)
        self.assertTrue(tree.is_committed)
        self.assertEqual(4, tree.schema_generation)
        self.assertEqual(
            ["b9-stack-root", "b9-upper-b", "b9-booster-a", "b9-booster-refly"],
            [r.recording_id for r in tree.recordings])

    def test_terminal_states(self):
        by_id = {r.recording_id: r for r in self.snap.recordings}
        self.assertIsNone(by_id["b9-stack-root"].terminal_state)   # key absent
        self.assertEqual(0, by_id["b9-upper-b"].terminal_state)    # Orbiting
        self.assertEqual(4, by_id["b9-booster-a"].terminal_state)  # Destroyed
        self.assertEqual("Orbiting", saveparse.terminal_state_name(0))
        self.assertEqual("Destroyed", saveparse.terminal_state_name(4))

    def test_merge_state_default_is_immutable_when_omitted(self):
        # The writer OMITS mergeState = Immutable (the default); absence must
        # decode as Immutable, and the written name-string form must survive.
        by_id = {r.recording_id: r for r in self.snap.recordings}
        self.assertEqual("Immutable", by_id["b9-booster-a"].merge_state)
        self.assertEqual("CommittedProvisional", by_id["b9-booster-refly"].merge_state)

    def test_branch_points_numeric_types_and_duplicate_child_ids(self):
        bps = self.snap.branch_points
        self.assertEqual(2, len(bps))
        split = bps[0]
        self.assertEqual("bp_b9_root", split.bp_id)
        self.assertEqual(0, split.bp_type)  # Undock - numeric on disk
        self.assertEqual(("b9-upper-b", "b9-booster-a"), split.child_ids)
        self.assertEqual("rp_b9_root", split.rewind_point_id)
        switch = bps[1]
        self.assertEqual(8, switch.bp_type)
        self.assertEqual("VesselSwitchContinuation",
                         saveparse.branch_type_name(switch.bp_type))

    def test_supersede_rows(self):
        self.assertEqual(1, len(self.snap.supersedes))
        row = self.snap.supersedes[0]
        self.assertEqual("rsr_0123456789abcdef0123456789abcdef", row.relation_id)
        self.assertEqual("b9-booster-a", row.old_recording_id)
        self.assertEqual("b9-booster-refly", row.new_recording_id)
        self.assertAlmostEqual(81.16, row.ut, places=2)

    def test_tombstone_rows(self):
        self.assertEqual(1, len(self.snap.tombstones))
        row = self.snap.tombstones[0]
        self.assertEqual("tomb_0123456789abcdef0123456789abcdef", row.tombstone_id)
        self.assertEqual("act_0123456789abcdef0123456789abcdef", row.action_id)
        self.assertEqual("b9-booster-a", row.retiring_recording_id)

    def test_rewind_retirement_rows(self):
        self.assertEqual(1, len(self.snap.rewind_retirements))
        self.assertEqual("rewound-out-supersede-old-side",
                         self.snap.rewind_retirements[0].reason)

    def test_rewind_points_use_point_children(self):
        self.assertEqual(1, len(self.snap.rewind_points))
        rp = self.snap.rewind_points[0]
        self.assertEqual("rp_b9_root", rp.rewind_point_id)
        self.assertEqual("bp_b9_root", rp.branch_point_id)
        self.assertTrue(rp.session_provisional)
        self.assertFalse(rp.corrupted)
        self.assertEqual(2, rp.slot_count)  # CHILD_SLOTs only, not PID_SLOT_MAP

    def test_observed_facets_shape(self):
        obs = saveparse.observed_structure_facets(self.snap)
        self.assertEqual({"supersedeRows": 1, "tombstones": 1, "rewindPoints": 1,
                          "rewindRetirements": 1},
                         obs["rewind"])
        structure = obs["recordings"]["structure"]
        self.assertEqual(1, structure["trees"])
        self.assertEqual(1, structure["committedTrees"])
        self.assertEqual(4, structure["recordings"])
        self.assertEqual({"Orbiting": 1, "Destroyed": 2}, structure["terminalStates"])
        self.assertEqual({"Undock": 1, "VesselSwitchContinuation": 1},
                         structure["branchPoints"])
        self.assertEqual([], structure["duplicateRecordingIds"])

    def test_no_duplicate_recording_ids(self):
        self.assertEqual((), saveparse.duplicate_recording_ids(self.snap))


class TreeMarkerTests(unittest.TestCase):
    """isActive / isPending markers and the committed-tree default."""

    @classmethod
    def setUpClass(cls):
        cls.snap = saveparse.parse_parsek_scenario(THREE_TREE_SFS)

    def test_marker_classification(self):
        kinds = {t.tree_id: (t.is_active, t.is_pending, t.is_committed)
                 for t in self.snap.trees}
        self.assertEqual((False, False, True), kinds["tree-committed"])
        self.assertEqual((True, False, False), kinds["tree-live"])
        self.assertEqual((False, True, False), kinds["tree-stash"])

    def test_committed_tree_count_facet(self):
        structure = saveparse.observed_structure_facets(self.snap)["recordings"]["structure"]
        self.assertEqual(3, structure["trees"])
        self.assertEqual(1, structure["committedTrees"])

    def test_debris_and_anchor_fields(self):
        by_id = {r.recording_id: r for r in self.snap.recordings}
        stash = by_id["rec-stash"]
        self.assertTrue(stash.is_debris)
        self.assertEqual("rec-live", stash.parent_anchor_recording_id)
        self.assertIsNone(by_id["rec-live"].parent_anchor_recording_id)


class AdversarialMutationTests(unittest.TestCase):
    """Hand-mutated fixture text: missing nodes, duplicated ids, torn files.
    The binding property: a mutation is either an EXACT count change or a
    named parse fault - never a crash, never a silent zero."""

    def test_missing_staging_parents_mean_zero_rows(self):
        # Delete the whole RECORDING_SUPERSEDES / LEDGER_TOMBSTONES /
        # REWIND_POINTS / RECORDING_REWIND_RETIREMENTS blocks: the writer only
        # emits non-empty parents, so absence IS the zero-rows truth.
        text = B9_MERGED_SFS
        for parent in ("RECORDING_SUPERSEDES", "LEDGER_TOMBSTONES",
                       "REWIND_POINTS", "RECORDING_REWIND_RETIREMENTS"):
            self.assertIn(parent, text)
            start = text.index("\t\t%s\n" % parent)
            # Node body: name line + open brace ... matching close at same depth.
            open_idx = text.index("{", start)
            depth, i = 0, open_idx
            while i < len(text):
                if text[i] == "{":
                    depth += 1
                elif text[i] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                i += 1
            text = text[:start] + text[i + 2:]  # drop through '}\n'
        snap = saveparse.parse_parsek_scenario(text)
        self.assertTrue(snap.parsed, snap.error)
        self.assertEqual(0, len(snap.supersedes))
        self.assertEqual(0, len(snap.tombstones))
        self.assertEqual(0, len(snap.rewind_points))
        self.assertEqual(0, len(snap.rewind_retirements))
        # The tree surface is untouched by the staging deletion.
        self.assertEqual(4, len(snap.recordings))

    def test_duplicated_recording_id_is_surfaced(self):
        text = B9_MERGED_SFS.replace("recordingId = b9-upper-b",
                                     "recordingId = b9-booster-a")
        snap = saveparse.parse_parsek_scenario(text)
        self.assertTrue(snap.parsed)
        self.assertEqual(("b9-booster-a",), saveparse.duplicate_recording_ids(snap))
        # ... and it reaches the run JSON via the observed facets, not just the
        # helper (adversarial-review finding 5).
        obs = saveparse.observed_structure_facets(snap)
        self.assertEqual(["b9-booster-a"],
                         obs["recordings"]["structure"]["duplicateRecordingIds"])

    def test_truncated_file_never_reads_as_zero_rows(self):
        truncated = B9_MERGED_SFS[: B9_MERGED_SFS.index("RECORDING_SUPERSEDES")]
        snap = saveparse.parse_parsek_scenario(truncated)
        self.assertFalse(snap.parsed)
        self.assertNotEqual("", snap.error)
        # And the evaluator names it rather than comparing counts:
        result = saveparse.evaluate_save_structure(
            {"rewind": {"supersedeRows": {"min": 1}}}, snap)
        self.assertEqual(1, len(result.mismatches))
        self.assertIn("save unreadable", result.mismatches[0])

    def test_duplicate_parsek_scenario_nodes_are_a_defined_fault(self):
        dup = B9_MERGED_SFS.replace(
            "\tFLIGHTSTATE",
            "\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t}\n\tFLIGHTSTATE", 1)
        snap = saveparse.parse_parsek_scenario(dup)
        self.assertFalse(snap.parsed)
        self.assertIn("2 ParsekScenario", snap.error)

    def test_no_parsek_scenario_node_is_clean_zero(self):
        snap = saveparse.parse_parsek_scenario("GAME\n{\n\tversion = 1.12.5\n}\n")
        self.assertTrue(snap.parsed)
        self.assertFalse(snap.scenario_found)
        self.assertEqual(0, len(snap.trees))

    def test_empty_or_whitespace_text_is_a_parse_fault(self):
        # Adversarial-review finding 1: a zero-byte / whitespace-only file is
        # what an interrupted save write leaves behind, and it is trivially
        # brace-balanced - it must NOT read as a clean all-zero parse (an armed
        # max = 0 window would PASS on a torn save).
        for text in ("", "   \n\t\n", "// only a comment\n", None):
            with self.subTest(text=repr(text)):
                snap = saveparse.parse_parsek_scenario(text)
                self.assertFalse(snap.parsed)
                self.assertIn("no top-level GAME node", snap.error)

    def test_non_save_text_is_a_parse_fault(self):
        snap = saveparse.parse_parsek_scenario("NODE\n{\n\tk = v\n}\n")
        self.assertFalse(snap.parsed)
        self.assertIn("no top-level GAME node", snap.error)

    def test_utf8_bom_is_stripped_not_faulted(self):
        # Round-2 NEW-1: KSP writes UTF-8 without BOM, but fixture .sfs files
        # are tool- and hand-authored (PowerShell 5.1 -Encoding UTF8 emits a
        # BOM by default). A BOM'd save must parse identically, not trip the
        # GAME-node fault with a misleading "empty or non-save text" reason.
        snap = saveparse.parse_parsek_scenario("\ufeff" + B9_MERGED_SFS)
        self.assertTrue(snap.parsed, snap.error)
        self.assertTrue(snap.scenario_found)
        self.assertEqual(4, len(snap.recordings))

    def test_unparseable_type_and_bools_degrade_defined(self):
        text = (B9_MERGED_SFS
                .replace("type = 0", "type = banana")
                .replace("sessionProvisional = True", "sessionProvisional = maybe"))
        snap = saveparse.parse_parsek_scenario(text)
        self.assertTrue(snap.parsed)
        bp = snap.branch_points[0]
        self.assertIsNone(bp.bp_type)
        # Unparseable numeric type buckets as "unparsed", never a crash.
        obs = saveparse.observed_structure_facets(snap)
        self.assertEqual(1, obs["recordings"]["structure"]["branchPoints"].get("unparsed"))
        # bool.TryParse failure keeps the default (True for sessionProvisional).
        self.assertTrue(snap.rewind_points[0].session_provisional)

    def test_unknown_future_terminal_state_surfaces_as_number(self):
        text = B9_MERGED_SFS.replace("terminalState = 4", "terminalState = 42", 1)
        snap = saveparse.parse_parsek_scenario(text)
        obs = saveparse.observed_structure_facets(snap)
        self.assertEqual(1, obs["recordings"]["structure"]["terminalStates"].get("42"))


class CommittedFixtureSweepTests(unittest.TestCase):
    """Corpus 1: every committed fixture save parses, and its structural counts
    are pinned EXACTLY. Twenty-one fixtures carry a ParsekScenario node (a flyable
    template MUST, or the FLIGHT route records nothing - see
    `test_every_node_less_fixture_is_vessel_less`); five carry none - the three
    fresh-* templates, `strategy-career` (which is `fresh-career` plus a reputation
    seed), and `preparsek-brandnew-career` (`fresh-career` retitled) - and every one
    of the five is VESSEL-LESS, which is what makes the absence safe. ALL committed
    fixtures carry zero trees / staging rows - the rich payloads are injected at
    stage time and deliberately NOT committed. A fixture edit that changes any of
    this reds here instead of on the next nightly."""

    # fixture dir -> ParsekScenario node present in persistent.sfs
    EXPECTED_SCENARIO_PRESENCE = {
        "b1-pad-craft": True,
        "b17-duna-pad": True,
        "b18-dres-pad": True,
        "b2-lko-craft": True,
        "bdock-forge-base": True,
        "bdock-station-craft": True,
        "bdock-station-pad": True,
        "career-pad-craft": True,
        # `career-contract-pad` BELONGS HERE DESPITE CARRYING A `Parsek/` TREE.
        # It ships one `Parsek/GameState/ledger.pgld` (two fixture-carried
        # `type = 5` ContractAccept rows), but this map is about the ParsekScenario
        # node INSIDE `persistent.sfs`, and there that node is as inert as its
        # donor's: zero trees, zero supersedes, zero tombstones, zero rewind
        # points. A ledger SIDECAR is not a `LEDGER` node, which is the same
        # distinction `career-earned-pad` sits on the other side of only because
        # IT also carries recordings.
        "career-contract-pad": True,
        "career-science-pad": True,
        # THE ONE ENTRY DERIVED FROM A RECORDED FIXTURE RATHER THAN FORGED.
        # `duna-park-probe` is `duna-direct-recorded` with Parsek's own state
        # removed: the `Parsek/` sidecar directory pruned by a harvest WITHOUT
        # --keep-parsek, plus a manual excision of the residual ParsekScenario
        # CHILDREN (RECORDING_TREE / GROUP_HIERARCHY / MILESTONE_STATE). It
        # belongs HERE and not in RECORDED_FIXTURES precisely because the strip
        # puts it back under the zero-trees contract this map asserts.
        #
        # THE NODE IS STILL PRESENT (True) AND THAT IS DELIBERATE: only the
        # children were excised. A flyable template must carry the node or the
        # FLIGHT route records nothing.
        #
        # WHY THE STRIP IS LOAD-BEARING, so a future re-harvest does not undo it:
        # `B23-ike-orbit` starts its recording through the seam on a vessel that
        # `duna-direct-recorded` holds a COMMITTED TREE for, and a seam
        # StartRecording cannot open a standalone tree on a committed tree's own
        # launch - the committed-restore path re-resumes that recording and
        # StartRecording no-ops onto it (measured, B23 flight 1, run
        # 2026-08-18_2242; see todo-and-known-bugs.md ->
        # SEAM-STARTRECORDING-JOINS-COMMITTED-TREE). The zero-trees assertions in
        # `test_every_persistent_sfs_parses_with_pinned_counts` are therefore not
        # bookkeeping here: they are the fixture's whole reason to exist.
        "duna-park-probe": True,
        # THE SECOND PARSEK-STRIPPED DERIVED FIXTURE, and the same strip for the
        # same reason. `eve-park-kerbalx` is `eve-orbit-recorded` (B16's
        # --keep-parsek harvest, which carries 8 committed recordings) with
        # Parsek's own state removed: the `Parsek/` sidecar directory pruned by a
        # harvest WITHOUT --keep-parsek, plus a manual brace-balanced excision of
        # the residual ParsekScenario CHILDREN. It belongs HERE and not in
        # RECORDED_FIXTURES precisely because the strip puts it back under the
        # zero-trees contract this map asserts.
        #
        # THE NODE IS STILL PRESENT (True) AND THAT IS DELIBERATE: only the
        # children were excised. A flyable template must carry the node or the
        # FLIGHT route records nothing.
        #
        # FIVE CHILD NODE TYPES WERE REMOVED, not `duna-park-probe`'s three:
        # RECORDING_TREE (1), GROUP_HIERARCHY (1) and MILESTONE_STATE (4) - the
        # B23 recipe - PLUS KERBAL_SLOTS (1) and CREW_REPLACEMENTS (1), which
        # exist here only because the source recording is CREWED. Those two are
        # Parsek's crew-reservation bookkeeping (Jebediah reserved, `Suster
        # Kerman` allocated as the stand-in) and they POINT AT THE RECORDING THAT
        # WAS JUST REMOVED, so leaving them would be residue by any reading. What
        # survives is Suster as an ordinary Available pilot and Jebediah Assigned
        # aboard the pod exactly as before - the WORLD is untouched.
        #
        # WHY THE STRIP IS LOAD-BEARING, so a future re-harvest does not undo it:
        # `B24-gilly-orbit` starts its recording through the seam on a vessel that
        # `eve-orbit-recorded` holds a COMMITTED TREE for, and a seam
        # StartRecording cannot open a standalone tree on a committed tree's own
        # launch (the same defect quoted just above). The zero-trees assertions in
        # `test_every_persistent_sfs_parses_with_pinned_counts` are therefore not
        # bookkeeping here either: they are the fixture's whole reason to exist.
        "eve-park-kerbalx": True,
        # THE THIRD PARSEK-STRIPPED DERIVED FIXTURE, and the strip is now a
        # PATTERN rather than two special cases. `jool-park-nerv` is
        # `jool-orbit-recorded` (B22's --keep-parsek harvest, which carries FIVE
        # committed recordings) with Parsek's own state removed: the `Parsek/`
        # sidecar directory pruned by a harvest WITHOUT --keep-parsek, plus a
        # manual brace-balanced excision of the residual ParsekScenario CHILDREN.
        # It belongs HERE and not in RECORDED_FIXTURES precisely because the strip
        # puts it back under the zero-trees contract this map asserts.
        #
        # THE NODE IS STILL PRESENT (True) AND THAT IS DELIBERATE: only the
        # children were excised. A flyable template must carry the node or the
        # FLIGHT route records nothing.
        #
        # EIGHT CHILD NODES OF FIVE TYPES WERE REMOVED, the same five
        # `eve-park-kerbalx` needed (this subject is CREWED too): RECORDING_TREE
        # (1), GROUP_HIERARCHY (1), MILESTONE_STATE (4), KERBAL_SLOTS (1) and
        # CREW_REPLACEMENTS (1). The last two are Parsek's crew-reservation
        # bookkeeping - here `Valentina Kerman` reserved with `Debmal Kerman`
        # allocated as the stand-in - and they POINT AT THE RECORDINGS THAT WERE
        # JUST REMOVED, so leaving them would be residue by any reading. What
        # survives is Debmal as an ordinary Available pilot and Valentina Assigned
        # aboard the pod exactly as before; the WORLD is untouched, including the
        # inherited oddity that Jebediah reads `Missing` in this save (a B18-B22
        # chain leftover carried through unchanged, NOT a strip artefact).
        #
        # WHY THE STRIP IS LOAD-BEARING, so a future re-harvest does not undo it:
        # `B25-laythe-orbit` starts its recording through the seam on a vessel
        # that `jool-orbit-recorded` holds a COMMITTED TREE for, and a seam
        # StartRecording cannot open a standalone tree on a committed tree's own
        # launch (the same defect quoted twice above). The zero-trees assertions
        # in `test_every_persistent_sfs_parses_with_pinned_counts` are therefore
        # not bookkeeping here either: they are the fixture's whole reason to
        # exist. (5 is the number to watch for in the produced save: it is what
        # `jool-orbit-recorded`, the save this was stripped from, carries.)
        "jool-park-nerv": True,
        # THE FOURTH PARSEK-STRIPPED DERIVED FIXTURE, and the one that shows the
        # strip recipe is "excise every child" rather than "excise these five
        # names". `laythe-park-nerv` is `laythe-orbit-recorded` (B25's
        # --keep-parsek harvest, one committed recording) with Parsek's own state
        # removed: the `Parsek/` sidecar directory pruned by a harvest WITHOUT
        # --keep-parsek, plus a manual brace-balanced excision of the residual
        # ParsekScenario CHILDREN.
        #
        # THE NODE IS STILL PRESENT (True) AND THAT IS DELIBERATE: only the
        # children were excised. A flyable template must carry the node or the
        # FLIGHT route records nothing.
        #
        # FIVE CHILD NODES OF **FOUR** TYPES WERE REMOVED, and the difference from
        # its three predecessors is the point: `RECORDING_TREE` (1),
        # `KERBAL_SLOTS` (1), `CREW_REPLACEMENTS` (1) and `MILESTONE_STATE` (2) -
        # **NO `GROUP_HIERARCHY`**, because B25's tree is a single standalone
        # recording with no debris subgroup to nest, and only TWO milestone rows
        # rather than `jool-park-nerv`'s four. A strip written as a fixed list of
        # five node names would have been fine here by luck; one written as "walk
        # the node and drop every child" is correct by construction, and that is
        # what was done.
        #
        # WHY THE STRIP IS LOAD-BEARING: `B26-laythe-vall-transfer` starts its
        # recording through the seam on a vessel that `laythe-orbit-recorded`
        # holds a COMMITTED TREE for, and a seam StartRecording cannot open a
        # standalone tree on a committed tree's own launch (measured, B23 flight
        # 1). It ALSO decouples B26 from V16M/V16T, whose eight jump UTs are
        # calibrated off `laythe-orbit-recorded`'s exact bytes.
        "laythe-park-nerv": True,
        # THE FIFTH PARSEK-STRIPPED DERIVED FIXTURE, and it is `laythe-park-nerv`'s
        # mirror image: where that one proved the strip recipe must not be a fixed
        # list because a node it expected was ABSENT, this one proves it from the
        # other side, because a node the Laythe recipe never saw is PRESENT.
        # `mun-park-kerbalx` is `mun-orbit-recorded` (B11's produced save, and
        # V6M/V6T's subject) with Parsek's own state removed: the `Parsek/` sidecar
        # directory pruned by a harvest WITHOUT --keep-parsek, plus a manual
        # brace-balanced excision of the residual ParsekScenario CHILDREN.
        #
        # THE NODE IS STILL PRESENT (True) AND THAT IS DELIBERATE: only the children
        # were excised. A flyable template must carry the node or the FLIGHT route
        # records nothing.
        #
        # SEVEN CHILD NODES OF **FIVE** TYPES WERE REMOVED, the largest strip of the
        # five: `RECORDING_TREE` (1), `GROUP_HIERARCHY` (1), `MILESTONE_STATE` (3),
        # `KERBAL_SLOTS` (1) and `CREW_REPLACEMENTS` (1). The source carries EIGHT
        # recordings - B11's Kerbal X sheds six radial boosters plus an upper-stage
        # decoupling, `branchPoints {"JointBreak": 5}` - so unlike B25's single
        # standalone recording there IS a `Kerbal X / Debris` subgroup to nest and
        # the `GROUP_HIERARCHY` node exists. A strip written as `laythe-park-nerv`'s
        # measured four names would have silently left it behind.
        #
        # WHY THE STRIP IS LOAD-BEARING: `B30-mun-minmus-transfer` starts its
        # recording through the seam on a vessel that `mun-orbit-recorded` holds a
        # COMMITTED TREE for, and a seam StartRecording cannot open a standalone tree
        # on a committed tree's own launch (measured, B23 flight 1). It ALSO
        # decouples B30 from V6M/V6T, whose jump UTs are calibrated off
        # `mun-orbit-recorded`'s exact bytes.
        "mun-park-kerbalx": True,
        "eva2-lko-crewed": True,
        "eva3-pad-3crew": True,
        "fresh-career": False,
        "fresh-sandbox": False,
        "fresh-science": False,
        # `fresh-career` WITH A REPUTATION SEED and nothing else - two lines of
        # the save differ. It inherits the base's deliberate ABSENCE of a
        # ParsekScenario node (False) along with everything else, which is right
        # for the same reason it is right on the base: `L3-strategy-currency-
        # conversion` enters through the seam's SPACECENTER route, where
        # `LoadGameImpl` writes persistent.sfs after `UpdateScenarioModules` and
        # the node gets created for it. Derived by
        # `harness/tools/build_strategy_career.py`, drift-guarded by
        # `StrategyCareerFixtureDriftTests`.
        "strategy-career": False,
        # THE TWO PRE-PARSEK-BACKUP FIXTURES. `PreParsekBackup.HasParsekGameplayFootprint`
        # reads a POPULATED `SCENARIO{name=ParsekScenario}` node as "this save has
        # already met Parsek" and declines to back it up, so a lane that wants to
        # observe the backup FIRE must stage a save the probe reads as untouched -
        # WITHOUT losing the node itself, which is a different requirement entirely.
        #
        # `preparsek-untouched-career` is `career-earned-pad` with that node REDUCED
        # TO ITS INERT FORM (`name` + `scene` only), the `PARAMETERS > ParsekSettings`
        # node deleted, the `Parsek/` sidecar tree not copied, and the Title restamped.
        # It keeps the career (a PRELAUNCH pad craft, nine contracts, four science
        # subjects, a crewed CAREER_LOG) so it is NOT brand-new-empty - the other half
        # of the gate.
        #
        # TRUE, AND IT IS THE SAME RULE AS THE `*-park-*` FAMILY ABOVE, reached from
        # the opposite direction. Those keep the node and excise its CHILDREN because
        # a flyable template must carry it; this one is flyable too - it is the ONLY
        # fixture in the corpus that is both FOCUSABLE and wanted footprint-free - so
        # it keeps the node for the identical reason: the seam's FLIGHT route calls
        # StartAndFocusVessel with no UpdateScenarioModules, and a save with no NODE
        # boots with no MODULE (CL-1 flight 1). The difference is only how far the
        # emptying goes. The `*-park-*` saves keep 4 values, which IS a footprint by
        # HasParsekGameplayFootprint's `values.Count > 2`; this one keeps 2, the inert
        # form KSP itself writes via AddToAllGames, which is not.
        #
        # `preparsek-brandnew-career` is `fresh-career` with the Title restamped and
        # nothing else - the brand-new-empty CONTROL, on its own leaf rather than
        # sharing `fresh-career` with B10 / R7c / L1 / M2 (the produced-save clobber
        # race: specs that share a saveTemplate leaf share one staged directory). It
        # carries NO node and needs none, for the reason the new
        # `test_every_node_less_fixture_is_vessel_less` cell now GATES: it is
        # vessel-less, so it routes to SPACECENTER, where LoadGameImpl DOES call
        # UpdateScenarioModules + SaveGame and KSP writes the inert node to disk
        # itself before the scene boots.
        #
        # Both are built and drift-guarded by `tools/build_preparsek_fixtures.py` +
        # `lib/test_preparsek_fixtures.py`, which re-runs the derivation over the
        # committed bases and asserts byte-identity.
        "preparsek-untouched-career": True,
        "preparsek-brandnew-career": False,
        "gloops-airshow": True,
        "gs1-two-stage-pad": True,
        "gs2-orbital-stack": True,
        # The FORGE-logi-pad harvest: the purpose-built `Logi Cargo Rig` PRELAUNCH on
        # the pad, the fixture H38-logistics-isolated flies. True like every other
        # forged pad fixture - the forge boots a save that already carries a
        # ParsekScenario node, and the harvest prunes Parsek RECORDING state (trees,
        # supersedes, tombstones, rewind points), not the node itself.
        #
        # ONE SHAPE DIFFERENCE from its sibling pad fixtures, recorded here so it is
        # not rediscovered as a defect: its ParsekScenario node carries a
        # MILESTONE_STATE CHILD node (`id` + `lastReplayedIdx = 0`) where
        # gs1-two-stage-pad / bdock-station-pad / b17-duna-pad carry values only. That
        # node is `MilestoneStore.SaveMutableState`'s per-milestone replay cursor - it
        # is present in career-earned-pad and in every recorded fixture, and the
        # siblings lack it only because their own forge session produced no milestone.
        # It is not recording state, no gate reads it, and the five zero-counts
        # asserted below all hold over it, so it is deliberately NOT stripped.
        "logi-cargo-pad": True,
    }

    # RECORDED-STATE fixtures (harvest --keep-parsek): produced saves whose
    # COMMITTED RECORDING is the fixture payload, so the zero-trees contract
    # above is exactly what they deliberately break. Pinned per fixture from
    # the harvest-time parse; a drift reds here instead of on the consumer
    # lane's next flight. duna-direct-recorded is the B17 green flight
    # (run 2026-08-06_1527, PASS at the armed spec): the clean direct
    # Kerbin->Sun->Duna recording the "Looped re-aim interplanetary transfer"
    # todo entry's option-3 validation consumes -- 1 committed tree, main
    # recording Orbiting (Duna, gated by the flight's terminalOrbitBody token),
    # booster debris Destroyed, no RP/supersede/tombstone rows.
    RECORDED_FIXTURES = {
        "duna-direct-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 2,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1, "Destroyed": 1},
            "branchPoints": {"JointBreak": 1},
            "minAuthoritativeSidecars": 8,
            # The recorded payload's IDENTITY, not just its shape: a
            # re-harvest that swapped in a different flight with the same
            # topology would otherwise pass every cell in the suite.
            "recordingIds": ["311d98e32547491e8dd37aec2526d25d",
                             "3397c2e5d9f2433caca2e419d7e356b7"],
            # Pinned against the C# loader constant below: a generation bump
            # makes RecordingStore reject these recordings at load
            # (generation-older) while saveparse still parses the sfs fine,
            # so without this pin the fixture would degrade SILENTLY into
            # one that loads zero recordings and tests nothing.
            "schemaGeneration": 4,
        },
        # --- THE SAME-PARENT MOON-TRANSFER LOOP SUBJECT ------------------
        # PROVENANCE: ike-orbit-recorded <- B23-ike-orbit, run 2026-08-18_2308,
        # PASS attempt 1 (mission wall 370.5 s, zero Unity exceptions),
        # --keep-parsek. THE SUITE'S FIRST RECORDING WHOSE LAUNCH BODY IS NOT
        # KERBIN: the DD1 starts already parked in Duna orbit, Hohmann-transfers
        # to IKE and commits in Ike orbit, so the loop lanes can read it as a
        # SAME-PARENT transfer - the class no committed fixture carried before.
        #
        # THE ONE-RECORDING TOPOLOGY IS THE POINT, not an accident of a simple
        # craft. B23 flight 1 produced a green run whose hop was APPENDED to
        # B17's Kerbin-rooted committed recording (seam StartRecording answered
        # `already=true`); flight 2 ran against the Parsek-stripped
        # `duna-park-probe` and answered `already=false`, minting the fresh
        # standalone tree pinned here. So `trees`/`committedTrees`/`recordings`
        # = 1/1/1 IS the fixture's contract, not merely its shape: a 2 or 3 here
        # means that defect is back. See todo-and-known-bugs.md ->
        # SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.
        #
        # `branchPoints` is EMPTY and must stay so: one craft, no separation
        # event anywhere in the profile (the booster was shed on the Kerbin
        # ascent two missions upstream, in B17).
        #
        # THE SEAM THE LOOP LANES CONSUME, read off the committed
        # `.prec.txt` ORBIT_SEGMENT chain and quoted here because V14M/V14T
        # anchor their brackets on it: ten segments, Duna 0-5 then Ike 6-9, with
        # the body change at the adjacent `endUT == startUT` pair
        # 9,177,480.8980102781. Against the recording's own
        # `explicitStartUT = 9,160,398.1036915872` that is a seam offset of
        # 17,082.794 s - the number MissionPeriodicity's `Orbital(Ike)
        # same-parent ... off=` should reproduce (the V6M convention: the offset
        # is measured from the recording's EXPLICIT start, NOT from the first
        # orbit segment's startUT, which here is 9,160,400.624).
        "ike-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 1,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1},
            "branchPoints": {},
            "minAuthoritativeSidecars": 4,
            "recordingIds": ["05ceee33806d4079a1d9d125a1359115"],
            "schemaGeneration": 4,
        },
        # --- THE ECCENTRIC-MOON LOOP SUBJECT -----------------------------
        # PROVENANCE: gilly-orbit-recorded <- B24-gilly-orbit, run
        # 2026-08-19_1655, PASS attempt 1 (mission wall 1,075 s, every assertion
        # met with NO parameter moved), --keep-parsek. The crewed Kerbal X upper
        # stage starts already parked in Eve orbit at ~4,986 km, Hohmann-transfers
        # to GILLY and commits in a 27,024 x 26,321 m Gilly park (ecc 0.009).
        #
        # WHY IT EXISTS ALONGSIDE `ike-orbit-recorded`, since both are
        # orbit-rooted same-parent moon transfers: Ike is near-circular (e 0.03),
        # near-equatorial (i 0.2 deg) and its SOI is 32.80% of its own orbital
        # radius, so the phase-lock TOLERANCE there is ~3,420 s wide and any
        # residual disappears into it. Gilly is e 0.55, i 12 deg and SOI/SMA
        # 0.40%, giving tol = SOI/v_orb = 247.6 s at the circular speed (133-460 s
        # across the eccentricity swing) - the tightest duty cycle in the stock
        # system, tighter than Pol's. This is the payload V15M/V15T read, and the
        # first on which a phase-lock residual would be measurable at all.
        #
        # THE ONE-RECORDING TOPOLOGY IS THE CONTRACT, not an accident. B24 ran
        # against the Parsek-stripped `eve-park-kerbalx` for exactly this reason
        # and the seam answered `startrecording
        # recordingId=77f724bb1d4844c3b132a1ccc00a7df3 already=false` (KSP.log
        # 11910) - `already=FALSE`, minting the fresh standalone Eve-rooted tree
        # 355840bc81bf45f8868b7d2508ca6de4. If a future re-harvest reads 2 or 8
        # recordings here that defect is back (8 is the specific number to watch:
        # it is what `eve-orbit-recorded`, the save `eve-park-kerbalx` was
        # stripped from, carries). See todo-and-known-bugs.md ->
        # SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.
        #
        # `branchPoints` is EMPTY and must stay so: one craft, no separation event
        # anywhere in the profile (the boosters and the flameout-staged core were
        # shed on B16's Kerbin ascent, two missions upstream).
        #
        # THE SEAM THE LOOP LANES CONSUME, read off the committed `.prec.txt`
        # ORBIT_SEGMENT chain and quoted here because V15M/V15T anchor their
        # brackets on it: SEVEN segments, Eve 0-3 then Gilly 4-6, with the body
        # change at the adjacent `endUT == startUT` pair 15,879,012.441954412.
        # Against the recording's own `explicitStartUT = 15,764,033.04501527`
        # that is a seam offset of 114,979.397 s - the number
        # MissionPeriodicity's `Orbital(Gilly) same-parent ... off=` should
        # reproduce (the V6M convention: measured from the recording's EXPLICIT
        # start, NOT from segment 0's startUT, which here is 15,764,035.545 and
        # would put every bracket 2.500 s off).
        #
        # AND THE DESTINATION TAIL IS ONLY 381.489 s LONG (explicitEndUT
        # 15,879,393.931458754 minus the seam) against a 115,360.886 s span, i.e.
        # 0.33% of the recording. That is by far the shortest destination phase of
        # any loop subject and it is what bounds where V15M/V15T may place a park
        # epoch; it is a property of Gilly's 126,123 m SOI, not of the flight.
        "gilly-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 1,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1},
            "branchPoints": {},
            "minAuthoritativeSidecars": 4,
            "recordingIds": ["77f724bb1d4844c3b132a1ccc00a7df3"],
            "schemaGeneration": 4,
        },
        # --- THE FIRST JOOL-MOON LOOP SUBJECT ----------------------------
        # PROVENANCE: laythe-orbit-recorded <- B25-laythe-orbit, run
        # 2026-08-19_2039, PASS attempt 1 (mission wall 742 s, the full phase
        # chain through ORBIT-COMMITTED), --keep-parsek. THE SUITE'S FIRST
        # RECORDING OF AN INWARD TRANSFER: the crewed Duna Rocket starts already
        # parked in JOOL orbit at ~584,321 km - 3.28x Pol's orbit, i.e. OUTSIDE
        # the whole moon system - and transfers DOWN to Laythe on a retrograde
        # ejection, so the intercept is the transfer's PERIAPSIS and the
        # home-frame apoapsis never moves. Every prior b5 moon transfer went the
        # other way.
        #
        # IT TOOK TWO FLIGHTS, AND FLIGHT 1 IS WHY THE PINS BELOW CAN BE TRUSTED.
        # Flight 1 (`_1948` / `_2001`, both INVALID(driver-flake)) flew the
        # identical profile clean through capture and delivered an
        # 86,843 x 55,089 m Laythe park at ecc 0.028 that the lane's own declared
        # `parkMinPeriapsisMeters = 60000` refused - a healthy park 4,911 m below
        # a floor written before anyone had measured what a 163.5 s capture burn
        # at 5.40 m/s^2 does to a periapsis (it drops it ~15.4 km, systematically:
        # 15,382 m then 15,415 m). The floor was resized to 52,000 off that
        # measurement and flight 2 green'd on the first attempt.
        #
        # THE ONE-RECORDING TOPOLOGY IS THE CONTRACT, not an accident. B25 runs
        # against the Parsek-stripped `jool-park-nerv` for exactly this reason and
        # its seam answered `startrecording
        # recordingId=370d38246d6e42848f140884081428af already=false` -
        # `already=FALSE`, minting the fresh standalone JOOL-rooted tree
        # 0ffee6458331466481f5c7aa0212b515. If a future re-harvest reads 2 or FIVE
        # recordings here that defect is back (5 is the specific number to watch:
        # it is what `jool-orbit-recorded`, the save `jool-park-nerv` was stripped
        # from, carries). See todo-and-known-bugs.md ->
        # SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.
        #
        # `branchPoints` is EMPTY and must stay so: one craft, no separation event
        # anywhere in the profile (everything sheddable came off on B18-B22's
        # Kerbin ascent and interplanetary legs, several missions upstream).
        #
        # THE SEAM THE LOOP LANES CONSUME, read off the committed `.prec.txt`
        # ORBIT_SEGMENT chain and quoted here because V16M/V16T anchor their
        # brackets on it: TEN segments, Jool 0-5 then Laythe 6-9, with the body
        # change at the adjacent `endUT == startUT` pair 28,814,456.826437414.
        # Against the recording's own `explicitStartUT = 27,787,320.719510831`
        # that is a seam offset of 1,027,136.107 s - the number
        # MissionPeriodicity's `Orbital(Laythe) same-parent ... off=` should
        # reproduce (the V6M convention: measured from the recording's EXPLICIT
        # start, NOT from segment 0's startUT, which here is 27,787,323.260 and
        # would put every bracket 2.540 s off - the same ~2.5 s trap Gilly's and
        # Ike's fixtures both carry).
        #
        # TWO PROPERTIES THAT MAKE THIS SUBJECT DIFFERENT FROM EVERY PRIOR ONE.
        # (1) THE SPAN IS 1,029,702.298 s = 19.435 LAYTHE PERIODS, so
        # `QuantizeCadenceToMultipleOfP` should take k = 20 and the loop cadence
        # is TWENTY moon periods rather than the one every previous loop subject
        # had - the suite's first k > 1 cadence, and the whole reason the V16 pair
        # exists. (2) ALL FOUR Laythe-framed segments are the APPROACH HYPERBOLA
        # (`sma = -2,107,372.848 ecc = 1.2713`); the captured park itself is the
        # 356.780 s tail after the last closed segment (28,816,666.237 ->
        # explicitEndUT 28,817,023.017), which is where V16M's park epoch has to
        # sit and is NOT where the destination phase's 70.7% point falls.
        "laythe-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 1,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1},
            "branchPoints": {},
            "minAuthoritativeSidecars": 4,
            "recordingIds": ["370d38246d6e42848f140884081428af"],
            "schemaGeneration": 4,
        },
        # --- THE CROSS-PARENT (MOON-TO-MOON) LOOP SUBJECT ----------------
        # PROVENANCE: vall-transfer-recorded <- B26-laythe-vall-transfer, run
        # 2026-08-20_1752, PASS attempt 1 (mission wall 1,408 s), --keep-parsek.
        # **THE FIRST COMPLETED MOON-TO-MOON TRANSFER IN THE SUITE**, and it took
        # THREE FLIGHTS across two mission shapes to produce - which is why the
        # provenance is a story rather than a run id:
        #   FLIGHT 1 (2026-08-19) refused in PLAN-TRANSFER: MechJeb 2.15.1's
        #     OperationInterplanetaryTransfer cannot plan from a MOON-PARKED
        #     origin (`NextTimeOfRadius: given radius of 3723645.81113302 is
        #     never achieved`). Not a Parsek defect and not a spec defect; filed
        #     as MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN.
        #   FLIGHT 2 (2026-08-20) flew the PARENT-RELAY mode built in response.
        #     Its core worked - the whole two-stage phase flow and
        #     `escapedHomeSoi met=True` - but it measured two defects: an escape
        #     sized for a hyperbolic excess AT INFINITY where KSP hands over AT
        #     THE SOI BOUNDARY (3.12x over), and a coast-warp thrash on a
        #     `time_to_soi` that flapped between two candidate encounters.
        #   FLIGHT 3 (2026-08-20) flew both fixes green, end to end through
        #     ORBIT-COMMITTED. THIS FIXTURE IS ITS PRODUCT.
        #
        # WHY IT EXISTS ALONGSIDE the three same-parent moon subjects: Vall is
        # NOT a child of Laythe, so a Laythe-rooted recording targeting Vall is
        # the CROSS-PARENT class `IsSameParentTarget` sends to `ApplyReaim`
        # rather than to the phase-lock solver. Ike/Gilly/Laythe all measured
        # `method=single-orbital`; this is the payload that lets V17M/V17T
        # measure whether the OTHER road exists. **THE ROUTING IS UNMEASURED** -
        # this fixture is the subject, not the answer.
        #
        # THE ONE-RECORDING TOPOLOGY IS THE CONTRACT, not an accident: B26 ran
        # against the Parsek-stripped `laythe-park-nerv` for exactly that reason.
        # `branchPoints` is EMPTY and must stay so - one craft, no separation
        # event anywhere in the profile.
        #
        # THE BYTES V17M/V17T ANCHOR ON, read off this fixture and quoted here
        # because both lanes' jump tables are derived from them:
        #   explicitStartUT  28,817,026.617051531   (UT0 - the V6M convention;
        #                    segment 0's startUT is 28,817,029.317051470, a
        #                    2.700 s gap, and using it would put every bracket
        #                    2.7 s off)
        #   explicitEndUT    28,896,846.042240269
        #   span             **79,819.425188738 s**
        #   THIRTEEN ORBIT_SEGMENTs and **TWO** body-change seams, which no prior
        #   loop subject has had:
        #     ESCAPE  Laythe->Jool at 28,823,386.230090793  (offset 6,359.613)
        #     ARRIVAL Jool->Vall   at 28,892,466.219888370  (offset 75,439.603)
        #   destination phase  4,379.822 s, of which the last ORBIT_SEGMENT ends
        #     at 28,896,012.542259 - so the recording closes with an
        #     **833.500 s SEGMENT-LESS PARKED TAIL** (the B25/V16M shape: the
        #     Vall segments 10-12 are all the APPROACH HYPERBOLA, sma
        #     -2,733,908.68 ecc 1.1715, and the captured park carries no closed
        #     segment of its own). V17M's park epoch is based on that TAIL, not
        #     on the whole destination phase.
        #   save clock (FLIGHTSTATE UT)  28,896,848.582240213
        #   pointCount 746, endpointBodyName Vall, endpointPhase 3
        "vall-transfer-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 1,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1},
            "branchPoints": {},
            "minAuthoritativeSidecars": 4,
            "recordingIds": ["625d63e022c449d6a44b5269c8b54a21"],
            "schemaGeneration": 4,
        },
        # --- THE SECOND CROSS-PARENT (MOON-TO-MOON) SUBJECT --------------
        # PROVENANCE: mun-minmus-recorded <- B30-mun-minmus-transfer, run
        # 2026-08-24_1536_B30-mun-minmus-transfer (2026-08-24), PASS attempt 1
        # (mission wall 2,320 s / run wall 2,377 s - the full twenty-phase chain
        # through ORBIT-COMMITTED with all eight telemetry assertions met),
        # harvested --keep-parsek. DLL PROVENANCE: flown on the post-PR-#1523
        # DLL with **auto-merge recordings ON**, deployed hash prefix
        # 27f5f31907efce27.
        #
        # WHY IT EXISTS ALONGSIDE vall-transfer-recorded, its sibling directly
        # above: roadmap gap G4 wanted the SECOND moon-to-moon transfer and a
        # REPLICATION of B26's shape at a SECOND parent. Mun and Minmus are
        # sibling moons of Kerbin, so `IsSameParentTarget` classifies this
        # CROSS-PARENT exactly as Laythe/Vall. It was flown via B26's flag-gated
        # PARENT-RELAY mode (`parentRelayTransfer`), inherited verbatim; the only
        # code the lane needed was one cited `mlib.STOCK_BODY_GRAVITY` row for
        # Mun. What DIFFERS from the Jool subject is worth stating, because it is
        # what makes this a replication rather than a duplicate: a DIFFERENT
        # parent (Kerbin, whose SOI is only 7.01x Mun's orbital radius against
        # Jool's 90.35x Laythe's) and an INCLINED target (Minmus ~6 deg, where
        # Laythe/Vall are coplanar). **THE ROUTING IS UNMEASURED** - this fixture
        # is the subject, not the answer; V21M/V21T read it and gate on nothing.
        #
        # THE ONE-RECORDING TOPOLOGY IS THE CONTRACT, not an accident: B30 ran
        # against the Parsek-stripped `mun-park-kerbalx` for exactly that reason.
        # `branchPoints` is EMPTY and must stay so - one craft, no separation
        # event anywhere in the profile. Both body-change seams were SUPPRESSED
        # in tree mode by the recorder (`SOI change boundary suppressed in tree
        # mode: Mun to Kerbin` and `... Kerbin to Minmus`), so the recording
        # stayed cohesive across both.
        #
        # THE BYTES V21M/V21T ANCHOR ON, read off this fixture:
        #   tree id          029afab30803454894b02be12567af81
        #   recordingId      a219b5a47df14988987b1f02a976fc0b
        #   explicitStartUT  21,747.140307940532
        #   explicitEndUT    305,651.766077061360
        #   TRACK_SECTION envelope (THE SPAN, per the span-end trap - NOT the
        #     point list): 21,747.100307940530 -> 305,654.546077063950,
        #     spanDur **283,907.445769123 s**, 36 sections
        #   THIRTEEN ORBIT_SEGMENTs, body roster Mun x3 / Kerbin x7 / Minmus x3,
        #   and TWO body-change seams:
        #     ESCAPE  Mun->Kerbin    at 38,842.400559838  (offset from envelope
        #       start 17,095.300251898)
        #     ARRIVAL Kerbin->Minmus at 288,977.964167143 (offset 267,230.863859202)
        #     both are exact adjacent endUT == startUT pairs
        #   last ORBIT_SEGMENT ends at 304,859.806076324, so the recording closes
        #     with a **794.740000740 s SEGMENT-LESS PARKED TAIL** (the
        #     B25/V16M/V17M shape; the three Minmus segments are the approach
        #     hyperbola and the captured park carries no closed segment of its
        #     own). V21M's park epoch is based on that TAIL.
        #   save clock (FLIGHTSTATE UT)  305,655.666077064990
        #   pointCount 1444, endpointBodyName Minmus, endpointPhase 3,
        #     terminalState 0 (Orbiting)
        #   authoritative sidecars: 4 (.pann, .prec, _ghost.craft, _vessel.craft)
        #     plus the committed .prec.txt mirror
        "mun-minmus-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 1,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1},
            "branchPoints": {},
            "minAuthoritativeSidecars": 4,
            "recordingIds": ["a219b5a47df14988987b1f02a976fc0b"],
            "schemaGeneration": 4,
        },
        # --- THE FIRST RETURN-DIRECTION SUBJECT --------------------------
        # PROVENANCE: jool-return-recorded <- B28-laythe-jool-return, run
        # 2026-08-20_2330, PASS attempt 1 (mission wall 474.6 s, the full chain
        # through ORBIT-COMMITTED with all eight assertions met), --keep-parsek.
        # The crewed Duna Rocket departs the Parsek-stripped `laythe-park-nerv`
        # park, fires ONE prograde escape node out of Laythe's SOI and
        # circularizes into a 19,728,666 x 19,728,629 m JOOL park at ecc 7.3e-7 -
        # its own PARENT.
        #
        # WHY IT EXISTS ALONGSIDE the five orbit-rooted subjects above, all of
        # which are OUTBOUND: this is the only recording in the corpus whose
        # target is an ANCESTOR of its launch body. That shape is what makes the
        # V19 pair's routing reading possible at all - `IsSameParentTarget` asks
        # only whether the target is a direct CHILD, so the relation is
        # DIRECTIONAL and no committed subject had ever exercised the inversion.
        #
        # STRUCTURALLY it is the B25/V16M shape: EIGHT ORBIT_SEGMENTs (Laythe
        # 0-3, Jool 4-7) across ONE body-change seam at 28,823,386.197673805,
        # and a SEGMENT-LESS parked tail from 28,828,820.894 to the recording's
        # end at 28,829,017.014 (the capture burn plus the 180 s park dwell). The
        # segment-less tail is load-bearing for V19T, whose TS init walk can skip
        # a loop-armed base recording `noOrbit=1` at an epoch inside it - the
        # measured V17T outcome on the same shape - so a re-harvest that changed
        # the tail would move that lane's posture, not just its numbers.
        "jool-return-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 1,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1},
            "branchPoints": {},
            "minAuthoritativeSidecars": 4,
            "recordingIds": ["28f5cc0158c8461eb995ea1e505aa67e"],
            "schemaGeneration": 4,
        },
        # PROVENANCE: kerbin-return-recorded <- B29-jool-kerbin-return FLIGHT 3, run
        # 2026-08-27, PASS attempt 1 (MISSION-OK, wall 2,103 s, all eight assertions
        # met), --keep-parsek. The crewed Duna Rocket departs the Parsek-stripped
        # `jool-park-nerv` park on a TWO-STAGE PARENT RELAY - a 272.36 m/s escape node
        # out of Jool's SOI, then a 1,455.20 m/s Sun-frame Hohmann node, two corrections
        # totalling 22.97, and a -1,350.10 m/s capture into a 99,928 x 6,041,092 m KERBIN
        # ellipse at ecc 0.809 inc 16.44.
        #
        # **IT IS THE FIRST RECORDING IN THE CORPUS THAT ARRIVES AT KERBIN FROM ANOTHER
        # PLANET**, which is why it exists alongside `jool-return-recorded` (the
        # moon-to-parent inversion) rather than duplicating it: that one closed the
        # moon-to-parent half of G2, this one closes the planet-to-Kerbin half, and the
        # V20 pair reads the RENDER of the arrival off these bytes.
        #
        # STRUCTURALLY it is the longest-span subject in the corpus by a wide margin:
        # EIGHTEEN ORBIT_SEGMENTs (Jool 0-4, Sun 5-13, Kerbin 14-17) across **TWO**
        # body-change seams at 36,198,519.425954551 (Jool->Sun) and 60,366,070.331327148
        # (Sun->Kerbin), 42 TRACK_SECTIONs (24 `ref=0`, 18 `ref=2`, zero `ref=1`), a
        # TRACK_SECTION envelope of [27,787,321.139510822, 60,393,896.914155044] =
        # 32,606,575.774644222 s, and a **391.700 s SEGMENT-LESS parked tail** from the
        # last ORBIT_SEGMENT's end at 60,393,505.214090839 - the capture burn fires
        # INSIDE that tail, so the Kerbin conics on disk are the HYPERBOLIC approach
        # (sma -392,274.880 m, ecc 2.7877) and never the delivered park.
        # THE SEGMENT-LESS TAIL AND THE TWO SEAMS ARE BOTH LOAD-BEARING for the V20 pair
        # (jump placement, the TS init-walk reading, and a `recordings` count window that
        # must admit TWO splittable candidates rather than one), so a re-harvest that
        # changed either would move those lanes' posture and not just their numbers.
        "kerbin-return-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 1,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1},
            "branchPoints": {},
            "minAuthoritativeSidecars": 4,
            "recordingIds": ["6d171d14ef474fbf95c69ada863bda12"],
            "schemaGeneration": 4,
        },
        # --- THE MOON LOOP-VALIDATION PAIR -------------------------------
        # PROVENANCE: mun-orbit-recorded  <- B11-mun-orbit, run
        # 2026-08-08_1458, PASS attempt 1, wall 1321 s (harvested
        # --keep-parsek from the produced b2-lko-craft save).
        # minmus-orbit-recorded <- B12-minmus-orbit, run 2026-08-08_1441,
        # PASS attempt 1, wall 630 s. Both harvests passed the
        # --expect-situation ORBITING gate on the parked craft.
        #
        # Both are the SAME shape and differ only on the target body: the
        # stock Kerbal X flown by the capture-enabled b5 machine, committed
        # MID-MISSION while parked in the target body's SOI. That is why the
        # topology is 8 recordings rather than the duna lane's 2 - this
        # launcher sheds six radial boosters plus one flameout-staged
        # remnant, where the B17 DD1 probe sheds one booster.
        #
        # WHY THE TWO TERMINAL MAPS DIFFER, and why that is not a defect. The
        # Minmus profile's flameout fires during ASCENT (gate
        # `flameoutStages 0->1` at ut 468.173 on run _1441, matching recording
        # c3b7b530's start 468.17328536978187) and leaves its remnant in
        # KERBIN ORBIT, so Orbiting 2 = Minmus-parked craft + that core. The
        # Mun profile's fires during CORRECTION-BURN (ut 4900.476 on run
        # _1458, matching recording 10da4419's start 4900.475848693869) and
        # drops its remnant on a Kerbin-impacting trajectory, where it reads
        # Destroyed. THE MUN MAP IS THEREFORE {Orbiting 1, Landed 1,
        # Destroyed 6}, NOT "Destroyed 7" - an earlier draft of this comment
        # wrote 7 and contradicted the dict two lines below it. Root-caused
        # 2026-08-08 - see the B11-TERMINAL-TOKEN-NEVER-TRUE entry in
        # todo-and-known-bugs.md and the B11 spec header. Pin what each
        # fixture MEASURED; do not harmonize them.
        "mun-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 8,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            # Orbiting 1 = the Mun-parked committed craft (SMA 339,568.968 m,
            # ecc 0.000126, ref Mun = ~139.6 km altitude).
            # Landed 1 = ONE OF THE FIRST ASCENT-SHED RADIAL BOOSTERS
            # (998cc41e, recording start 48.48, down beside the pad at
            # lat -0.104 lon -74.545 alt 67.99) - NOT the flameout-staged
            # remnant, which an earlier draft of this comment named here and
            # which is the opposite of what the save says.
            # Destroyed 6 = the other five radial boosters PLUS the
            # flameout-staged remnant (10da4419, recording start
            # 4900.475848693869 = the `flameoutStages 0->1` gate fire). That
            # remnant is Destroyed on all three measured B11 flights; the row
            # that actually flips class between flights is one of the first
            # booster pair (998cc41e / a834e40a, same start UT 48.48).
            "terminalStates": {"Orbiting": 1, "Landed": 1, "Destroyed": 6},
            "branchPoints": {"JointBreak": 5},
            "minAuthoritativeSidecars": 32,
            "recordingIds": ["0fd603e389b94d6488d92f4e3c6b7957",
                             "10da441999fb4ff7a09bf6be0f068d48",
                             "595e99bfbba74b9990daa5f16bf786c6",
                             "5e44719ae936481489e22d72707d9225",
                             "998cc41e5ce64a3681fff8df9efe802e",
                             "a834e40aebfd4b8090b32bd8221e5e92",
                             "c9e18b4c9be848698bcc5fe445b95574",
                             "e8fc9f46072b42a2b8d3c39de23c64d2"],
            "schemaGeneration": 4,
        },
        "minmus-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 8,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            # Orbiting 2 = the Minmus-parked committed craft (SMA 98,463.595 m,
            # ecc 0.000292, ref Minmus = ~38.5 km altitude) PLUS the
            # flameout-staged remnant this profile leaves in Kerbin orbit
            # (c3b7b530, recording start 468.17328536978187 = run _1441's
            # `flameoutStages 0->1` gate fire at ut 468.173).
            # Destroyed 6 = the six radial boosters.
            "terminalStates": {"Orbiting": 2, "Destroyed": 6},
            "branchPoints": {"JointBreak": 5},
            "minAuthoritativeSidecars": 32,
            "recordingIds": ["6daa39387478442dad20c1f7aeec3ec3",
                             "7304b9a00fc245349640367b051fbeb7",
                             "882cb2239abb49558599c3b1291f851d",
                             "96ecd888bb2f43929b7647ca14e4697e",
                             "9bb8ea185d804b0c81242c1a1a9930a0",
                             "a46c58f4f3e84894ada41e84c8666f1e",
                             "c3b7b530f58a4068b559e5367dcf16a2",
                             "ce180cdd0d794a83ba2bc4430ca29056"],
            "schemaGeneration": 4,
        },
        # --- THE EVE (INWARD-TRANSFER) LOOP-VALIDATION FIXTURE ------------
        # PROVENANCE: eve-orbit-recorded <- B16-eve-orbit, run
        # 2026-08-11_0718, PASS attempt 1, wall 1843 s / mission 1769 s
        # (result JSON archived in that worktree's harness/results/).
        # Harvested --keep-parsek through the --expect-situation ORBITING
        # gate from the produced b2-lko-craft save. Same stock Kerbal X /
        # capture-enabled b5 machine as the moon pair, committed MID-MISSION
        # while parked at Eve (pe 4,985,446 / ap 4,986,170 m alt, ecc ~0;
        # capturedInTargetOrbit read 0.000, gate window pe>=500 km /
        # ap<=13,000 km / ecc<=0.25, park below Gilly's 14,175 km shell).
        #
        # Orbiting 2 = the Eve-parked committed craft (75a6ab25,
        # terminalOrbitBody=Eve on the run's own CommitTreeFlight terminal
        # line) PLUS the flameout-staged remnant left in KERBIN orbit
        # (081b06e8, terminalOrbitBody=Kerbin; recording start
        # 11,830,586.218 = the TRANSFER-BURN Mainsail flameout at the
        # ejection). The Minmus-shaped map, not the Mun one - the remnant's
        # class is profile-dependent; pin what each fixture MEASURED.
        # Destroyed 6 = the six radial boosters.
        #
        # SPAN (measured off this fixture's .prec.txt mirrors, min/max over
        # all ut/startUT/endUT keys): spanStart 34.680, spanEnd
        # 15,764,030.825, span 15,763,996.145 s = 1.0733 Kerbin-Eve
        # synodic periods (14,687,035.419 s). This fixture's loop unit
        # therefore sits in the span>synodic regime (cadence != synodic,
        # PadAlignLaunch declines) - the consumer lane must measure the
        # schedule off the MissionConfig echo, never assume the moon/duna
        # lanes' cadence == synodic shape.
        "eve-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 8,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 2, "Destroyed": 6},
            "branchPoints": {"JointBreak": 5},
            "minAuthoritativeSidecars": 32,
            "recordingIds": ["081b06e81737471fb5d85f3e0e92d49b",
                             "0b4193a0540e4fa7af9890fe4ba5c10d",
                             "2f3bf43348534202b226afbb6ae00ce9",
                             "75a6ab25a0f445219a82b7b841e44ba8",
                             "7d373f22d0a04ff2a58d43bbcca47757",
                             "8eda6186afcd46db81551ada10dcef9a",
                             "c5403bf4a1144815bfa0d8691e38a587",
                             "fbc705e91fcd4b5a8176cf5493807a0b"],
            "schemaGeneration": 4,
        },
        # --- THE DRES PROGRAM'S ORBIT FIXTURE ----------------------------
        # PROVENANCE: dres-orbit-recorded <- B19-dres-orbit, run
        # 2026-08-12_0047, PASS attempt 1, wall 2981 s. Harvested with
        # `harvest_bdock_station.py --keep-parsek --expect-situation
        # ORBITING` from the produced b18-dres-pad save (the gate passed on
        # 'Duna Rocket' ORBITING at Dres). NO fresh driven run was needed and
        # NOTHING was hand-edited: the green run's produced save survived the
        # step-3 re-provision untouched (provisioning rebuilds GameData, not
        # saves), so the committed bytes ARE that flight's own output. Every
        # number below was re-measured off THESE COMMITTED BYTES with
        # saveparse.parse_parsek_scenario + observed_structure_facets and is
        # identical to that run's saveParse verifier facets.
        #
        # WHAT THE FIVE RECORDINGS ARE, by measured span UT -- worth writing
        # down because the count alone cannot distinguish them:
        #   bc4a3a6d  main orbiter, ut 26.2 -> 20,393,407.1 (the 20.393M
        #             game-second loop-unit span), 1,546 points, Orbiting/Dres
        #   a6177cfb  ascent booster, ut 97.1 -> 111.9, Destroyed
        #   902b516c  ascent booster, ut 97.1 -> 111.7, Destroyed
        #   e3c055b7  core Mainsail stack dropped by MechJeb AUTOSTAGE during
        #             ascent, ut 1,697.3 -> 1,703.3, Orbiting/Kerbin
        #   a547f78a  Skipper stack dropped by the JETTISON phase, ut 2,699.5
        #             -> 2,704.2 (exactly the JETTISON window), Orbiting/Kerbin
        # So 3 of the terminals are Orbiting and only 2 are Destroyed, and two
        # of the Orbiting three are parked DEBRIS at Kerbin rather than the
        # Dres orbiter -- a consumer asserting "Orbiting == the payload" would
        # be wrong on this fixture.
        #
        # THE POST-COMMIT FRESH TREE LEFT NOTHING. The flight's log shows
        # Parsek opening a new recording after the commit
        # (`Recording started: ... parts=40, promotion`), which is the hazard
        # B16's spec flags as FIRST-FLIGHT-TO-CONFIRM. Measured here: the
        # harvested save carries exactly ONE RECORDING_TREE, `isActive=False`,
        # no second tree, no provisional, no orphan sixth recording, and zero
        # activeTree/ReFlySession/SwitchSegment residue. Nothing was trimmed to
        # make that true.
        "dres-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 5,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 3, "Destroyed": 2},
            "branchPoints": {"JointBreak": 3},
            "minAuthoritativeSidecars": 20,
            "recordingIds": ["902b516ccc69491f9097d9c3dddd9e5d",
                             "a547f78a99f54d5d873b2f6c71ecc5e6",
                             "a6177cfb4e4c4c2ea43c0a02f20e28d1",
                             "bc4a3a6d361549d2a7cdd9d4eb5574c1",
                             "e3c055b7af8f4d9e8f9e8bf3b8aa0f1a"],
            "schemaGeneration": 4,
        },
        # --- THE EELOO PROGRAM'S ORBIT FIXTURE ---------------------------
        # PROVENANCE: eeloo-orbit-recorded <- B21-eeloo-orbit, run
        # 2026-08-12_2239, PASS attempt 1, wall 3092 s (mission 3020 s).
        # Harvested with `harvest_bdock_station.py --save-dir <snapshot>
        # --target-name eeloo-orbit-recorded --expect-situation ORBITING
        # --keep-parsek`; the gate passed on 'Duna Rocket' ORBITING at Eeloo,
        # vessels=4. Every number below was re-measured off THESE COMMITTED
        # BYTES with saveparse.parse_parsek_scenario +
        # observed_structure_facets and is identical to that run's own
        # saveParse verifier facets.
        #
        # WHY THE HARVEST WENT THROUGH A SNAPSHOT, and why the run id matters
        # more here than on any earlier fixture. B21 flew green TWICE. The
        # FIRST green run (2026-08-12_2003, also PASS, wall 3083 s) had its
        # produced save DESTROYED 53 s after it ended, because sibling
        # scenario B20-moho-orbit declares the same `saveTemplate =
        # "fixtures/saves/b18-dres-pad"` and the produced-save directory is
        # `_leaf_of(saveTemplate)` - so both runs stage into the same
        # `<instance>/saves/b18-dres-pad`, and the machine lock serialises
        # RUNS, not the produced save's lifetime (filed as
        # HARNESS-PRODUCED-SAVE-CLOBBERED-BY-SIBLING-RUN in
        # todo-and-known-bugs.md). THE COMMITTED BYTES ARE _2239's, harvested
        # from a snapshot taken in the same command as the run. Do not
        # re-measure any pin here against _2003.
        #
        # WHAT THE FIVE RECORDINGS ARE, by measured span UT -- the count
        # alone cannot distinguish the Eeloo payload from the Kerbin-parked
        # debris that also read Orbiting:
        #   20d890f9  main orbiter, ut 26.2 -> 47,036,743.9 (the 47.037M
        #             game-second loop-unit span), 1,501 points,
        #             Orbiting/Eeloo, terminal elements sma 11,552,911.723
        #             ecc 0.000713 inc 87.743 at epoch 47,036,746.304 -
        #             which is exactly _2239's ORBIT-COMMIT entry UT, the
        #             tightest available tie between these bytes and that run
        #   c849bbee  ascent booster, ut 97.1 -> 149.0, Destroyed
        #   d8bfbb2c  ascent booster, ut 97.1 -> 148.7, Destroyed
        #   34ba13d3  core stack dropped by MechJeb AUTOSTAGE during ascent,
        #             ut 1,697.3 -> 1,716.4 (MJ-ASCENT ran 26.2 -> 1,715.3),
        #             Orbiting/Kerbin
        #   cf7ff6b8  Skipper stack dropped by the JETTISON phase, ut 2,699.0
        #             -> 2,703.9 (the JETTISON PHASE itself ran 2,698.475 ->
        #             2,703.315, ORBIT -> JETTISON -> PLAN-TRANSFER; 2,703.9
        #             is the RECORDING's end, 0.6 s past the phase exit, not
        #             the phase exit itself), Orbiting/Kerbin
        # So 3 terminals are Orbiting and 2 Destroyed, and two of the
        # Orbiting three are parked DEBRIS at Kerbin, not the Eeloo orbiter.
        #
        # STRUCTURALLY IDENTICAL TO dres-orbit-recorded ON EVERY FACET
        # PINNED HERE - 5 recordings, Orbiting 3 / Destroyed 2, JointBreak 3,
        # 20 authoritative sidecars - because it is the same craft flown on
        # the same B19 profile to a different target. The two fixtures differ
        # only in points and in span. Read that as confirmation the profile
        # is body-independent, NOT as evidence one fixture can stand in for
        # the other: the consumer lanes key off the Eeloo tree id
        # d4ce5c45adae4f3e89c4ac6bbec6b167 and on the terminal body being
        # Eeloo, and a stale Dres GUID answers OK-with-nothing-to-do. TWO
        # DIFFERENT SPELLINGS, do not grep the wrong one: IN THIS FIXTURE the
        # key is `tOrbBody = Eeloo` (persistent.sfs:729, with the two parked
        # Kerbin debris reading `tOrbBody = Kerbin` at :1021 and :1073);
        # `terminalOrbitBody=` is the KSP.log spelling, emitted by
        # ParsekFlight.cs:13446 and GhostMapPresence.Observability.cs:295 and
        # found in a collected log, never in the save.
        #
        # LINE ENDINGS: THIS FIXTURE IS CRLF WHERE THE OLDER RECORDED
        # FIXTURES ARE LF, AND THAT IS DELIBERATE. Census of the committed
        # bytes: 14 of these 33 files contain CRLF and 19 do not. The pure-
        # CRLF ones are the ConfigNode writes - `persistent.loadmeta` (16
        # CRLF / 0 LF), all five `Parsek/GameState/*` files (`ledger.pgld`
        # 210/0, `milestones.pgsm` 157/0, the two `.pgsb` baselines, and
        # `events.pgse`), and all five `.prec.txt` (29,395/0 for the main
        # orbiter). `persistent.sfs` is LF-only (15,562 LF); the sidecars are
        # predominantly LF, and EXACTLY THREE FILES ARE MIXED - all three
        # named so the census is complete rather than illustrative:
        # 20d890f9's `.prec` (3 CRLF / 331 LF), 20d890f9's `_ghost.craft`
        # (1 CRLF / 91 LF) and d8bfbb2c's `.prec` (1 CRLF / 49 LF). The
        # other 11 CRLF-bearing files are pure CRLF. By contrast
        # dres-orbit-recorded commits 1
        # CRLF-bearing file of 33 and bdock-recorded 2 of 106 - in both cases
        # a single CRLF inside one `.prec`.
        # MECHANISM. `harvest_bdock_station.py` writes persistent.sfs
        # EXPLICITLY with an LF-only `newline` argument
        # (harvest_bdock_station.py:314-316) and copies everything else
        # verbatim - `shutil.copy2` for the kept root files at :325 and
        # `shutil.copytree` for the kept directories at :335 - so every
        # non-sfs file lands byte-for-byte as KSP and
        # Parsek wrote it on Windows, which for ConfigNode output is CRLF.
        # `.gitattributes:30` (`harness/fixtures/**    -text`) then keeps
        # those bytes stable on every platform: no index normalisation on
        # add, no smudge on checkout, and no gate anywhere compares these
        # bytes against LF-generated output - the consumers parse structure,
        # not bytes.
        # WHY THE OLDER FIXTURES ARE LF - VERIFIED, not assumed. Both were
        # committed BEFORE the `-text` rule existed: it landed in cc257c44d
        # (2026-08-12 13:03 UTC), which `git merge-base --is-ancestor`
        # reports is NOT an ancestor of either dres-orbit-recorded's add
        # commit fc0a6e8b0 (2026-08-12 01:48 UTC) or bdock-recorded's
        # 60de84ac2 (2026-08-11 17:24 UTC). This checkout has
        # `core.autocrlf = true`, so those adds went through CRLF -> LF index
        # normalisation. The fingerprint proves it rather than merely
        # suggesting it: dres's committed `ledger.pgld` blob is 0 CRLF / 210
        # LF while this fixture's, from the same Parsek writer on the same
        # OS, is 210 CRLF / 0 LF - same 210 lines, opposite endings, a
        # difference no game code produces. And the one thing that DID
        # survive normalisation in the old fixtures is explained by the same
        # rule: `.prec` carries NUL bytes in its first 8 KB, git therefore
        # classified it binary and skipped the filter, which is why dres's
        # `.prec` kept its lone CRLF exactly as this fixture's `.prec` files
        # keep theirs.
        # WHY IT IS NOT BEING NORMALISED. The provenance block above asserts
        # that the committed bytes ARE `_2239`'s output. Rewriting the line
        # endings for tidiness would make that assertion false in exactly the
        # way this branch has just had to correct elsewhere (the Duna Rocket
        # craft's retracted "byte-identical to its download" claim). Truthful
        # bytes beat a tidy convention: these are left as the game wrote them
        # so the claim stays literally true. Do NOT LF-normalise this
        # fixture, and do NOT "fix" the inconsistency with the older two.
        #
        # POINTS ARE NOT STABLE TO THE UNIT - deliberately NOT pinned here,
        # and any consumer window over them must not be tight. Measured
        # across the two green B21 runs: total 1,700 / largest 1,517
        # (_2003) against total 1,684 / largest 1,501 (_2239), a 16-point
        # swing in the main orbiter on two runs of the same spec. smallest
        # (21) and every structural facet were identical across both.
        #
        # RISK 2 DID NOT FIRE, AND THE MARGIN IS THE FINDING: THE
        # 5-RECORDING PIN HERE IS A KNIFE EDGE, NOT A COMFORTABLE
        # MEASUREMENT. The count rests on the four radial LF drop tanks
        # (istg 1, 4 x 360 = 1,440 units) never emptying, because emptying
        # them would let MechJeb autostage pop a SIXTH recording. Measured
        # from `_2239`'s telemetry lf track, all three readings: 2,240.000 at
        # TRANSFER-BURN entry, 1,305.522 at CAPTURE-BURN entry (934.478
        # spent, well inside the radial group) - but 800.452 at ORBIT-COMMIT,
        # i.e. 1,439.548 of the 1,440 spent by the end of the capture burn.
        # THE RADIALS RETAINED 0.452 UNITS, 0.03% OF THE GROUP. _2003 landed
        # at 801.135 (1.135 retained - still under a tenth of a percent). So
        # the 5-recording topology is measured and correct for both green
        # runs, and the flight came within 0.03% of being a 6-recording one.
        # THE CONCRETE CONSEQUENCE for anyone re-harvesting: a marginally
        # different arrival geometry, a slightly larger correction round, or
        # ANY change to the craft or the park altitude spends those 0.452
        # units, autostage sheds istg 1, and the count becomes 6 - which
        # would red the spec's {5, 5} pin AND this fixture's `recordings`/
        # `recordingIds` pins ON AN ENTIRELY CORRECT FLIGHT. That is the
        # drop-tank shed, not a regression.
        # WHAT TO DO WHEN IT HAPPENS: re-pin to the measured value, add the
        # new recording's id and span UT to the list above, and say which
        # recording appeared. DO NOT relax `recordings` to a range - B19's
        # contract is pin the measurement, and a range would re-hide the
        # transition this margin exists to warn about.
        # IT ALSO MOVES THE FLAMEOUT ANALYSIS in the spec's
        # maxCorrectionDvMps block: its two-trip reading assumes istg 1 is
        # still attached, so on a flight that DOES shed, istg 1 is gone and a
        # flameout pop lands on istg 0 - the POD decoupler - on the FIRST
        # trip rather than the second.
        "eeloo-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 5,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 3, "Destroyed": 2},
            "branchPoints": {"JointBreak": 3},
            "minAuthoritativeSidecars": 20,
            "recordingIds": ["20d890f9f34b466fb8be07d83103a9a7",
                             "34ba13d3aaea4297a3e5bee543de4b9d",
                             "c849bbeea3df44a1bcb85d8f9f2fc06b",
                             "cf7ff6b80f8649df962cfbf1a37907b1",
                             "d8bfbb2c7c654b0fa83f1a6d43394833"],
            "schemaGeneration": 4,
        },
        # --- THE JOOL ORBIT FIXTURE --------------------------------------
        # PROVENANCE: jool-orbit-recorded <- B22-jool-orbit, run
        # 2026-08-17_1959, PASS attempt 1, wall 2441 s, every verifier PASS
        # or SKIPPED (result JSON in harness/results/). Harvested with
        # `harvest_bdock_station.py --keep-parsek --expect-situation
        # ORBITING` from a SNAPSHOT COPY of the produced b18-dres-pad save,
        # taken the instant run.py returned -- that leaf is shared by
        # B18-B21 and the machine lock does not protect a finished run's
        # save (HARNESS-PRODUCED-SAVE-CLOBBERED-BY-SIBLING-RUN).
        #
        # Every number below is READ BACK through this cell's own parser
        # (saveparse.observed_structure_facets), never counted by hand.
        # The topology is SHAPE-IDENTICAL to eeloo-orbit-recorded above --
        # same tree count, same 5 recordings, same {Orbiting 3, Destroyed 2}
        # terminals, same {JointBreak 3} branch points, same 20 authoritative
        # sidecars -- which is what a clean RETARGET of the same profile
        # should produce, and is the cheapest evidence that it was one.
        #
        # THE V-LANE PRECONDITION, VERIFIED ON THE HARVESTED BYTES: the
        # recordings reference Kerbin, Sun and Jool ONLY -- zero Laythe /
        # Vall / Tylo / Bop / Pol occurrences across all five `.prec.txt`
        # sidecars (the five hits in persistent.sfs are KSP's own celestial
        # `BodyName =` roster, not recorded legs). So `moons=0` off the
        # dest-constraints line, and this fixture cannot silently route a
        # consumer onto the never-live-flown M-MIS-6 multi-moon path.
        #
        # The arrival this fixture carries: requested 600,000,000 m ALTITUDE,
        # delivered 584,327,170.912 (k = 0.9739) = 590,327,171 m RADIUS,
        # 2.789x Pol's 211,666,345 m clearance edge, 24.04% of Jool's SOI.
        # Terminal park ap 584,330,474.177 / pe 584,321,095.004, ecc 8e-6.
        "jool-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 5,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 3, "Destroyed": 2},
            "branchPoints": {"JointBreak": 3},
            "minAuthoritativeSidecars": 20,
            "recordingIds": ["1d6fa27e66bd47fc85baffe90522bef3",
                             "98c11e0d19804aecb1b7e66a94f8ede7",
                             "ad34ea8948ba4c1fb839bbc691f21deb",
                             "b13418e758764bb8adf061cff8236f1b",
                             "ddcc6d007ff54a9c8246c7958011883a"],
            "schemaGeneration": 4,
        },
        # --- THE ROUTE-PROOF (DOCKING) FIXTURE ---------------------------
        # PROVENANCE: bdock-recorded <- BDOCK-1-station-interceptor, run
        # 2026-08-11_1606, PASS attempt 1, wall 2146 s / mission 2093 s
        # (result JSON in harness/results/). Harvested with
        # `harvest_bdock_station.py --keep-parsek --expect-situation
        # ORBITING` from the produced bdock-station-pad save. Every number
        # below was re-measured off THESE COMMITTED BYTES with
        # saveparse.parse_parsek_scenario + observed_structure_facets, and
        # is identical to that run's own saveParse verifier facets.
        #
        # THE REGISTRY'S FIRST TWO-TREE FIXTURE, and its first with any
        # REWIND_POINTS rows. Read the pin accordingly: the consumer cell
        # parses the WHOLE scenario, so `terminalStates` / `branchPoints`
        # are ONE MERGED histogram across both trees, not per-tree maps.
        # The per-tree attribution, for anyone reading a mismatch:
        #   788554a9... (root a32f62f5 "Kerbal X") - 8 recordings: root
        #     Orbiting + 6 debris Destroyed + "Kerbal X Probe" b07cfd6c
        #     Orbiting/CommittedProvisional. 5 JointBreak branch points,
        #     one carrying rp_72ebafb5.
        #   8c677bba... (root 5157d655 "Kerbal X") - 11 recordings: root
        #     DOCKED + 6 debris Destroyed + "Kerbal X Probe" 500c0ba9
        #     Orbiting/CommittedProvisional + the docked-state recording
        #     f049901e + its two post-undock children 37d0dc07 (Orbiting,
        #     Immutable) and 4af6cfd7 (Orbiting, CommittedProvisional).
        #     5 JointBreak + 1 Dock (type 2, child f049901e) + 1 Undock
        #     (type 0, children 37d0dc07 / 4af6cfd7, carrying rp_1df613c7).
        # 19 recordings but terminalStates sums to 18: f049901e, the
        # docked-state recording, writes NO terminalState key at all, and
        # observed_structure_facets counts only resolvable ones. That is
        # measured, not a miscount - do not "fix" it to 19.
        #
        # THE PAYLOAD IS THE PROOF SURFACE, NOT CANDIDACY. f049901e owns
        # the save's single ROUTE_CONNECTION_WINDOWS node (one WINDOW:
        # dockUT 8949.268 -> undockUT 8950.588, transferKind=DockingPort,
        # transferTargetPid 3620499050, 28 transport + endpoint part pid
        # rows), and four recordings carry ROUTE_RUN_MANIFEST nodes
        # (a32f62f5, 5157d655, f049901e, 37d0dc07). What this fixture
        # deliberately is NOT is a route CANDIDATE: three of its 19
        # recordings are MergeState.CommittedProvisional (b07cfd6c,
        # 500c0ba9, 4af6cfd7 - TWO of them in the route-owning tree), and
        # RouteCandidateFinder.IsTreeFullySealed requires EVERY recording
        # Immutable, so neither tree can be picked up as a candidate
        # without the player's Seal action. See the
        # ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH entry in
        # todo-and-known-bugs.md. A future harvest that arrived with three
        # Immutables here would be a DIFFERENT fixture and must re-pin the
        # finding too, not just these numbers.
        #
        # minAuthoritativeSidecars 75 is the MEASURED authoritative file
        # count, not a family multiple: 19 .prec + 19 .pann + 19
        # _vessel.craft + 18 _ghost.craft. f049901e is the one recording
        # with no _ghost.craft (a 2-point docked-state row). All 19 ids
        # resolve to a non-empty .prec - checked, no orphans either way.
        # The floor was 131 while the tree also carried 56 derived .txt
        # mirrors; those are no longer committed (the two snapshot mirrors
        # are regenerated from the binaries on demand), and the floor now
        # counts ONLY authoritative sidecars so a mirror cannot pad it.
        # --- THE MOHO PROGRAM'S ORBIT FIXTURE ----------------------------
        # PROVENANCE: moho-orbit-recorded <- B20-moho-orbit, run
        # 2026-08-12_2331, PASS attempt 1. Harvested with
        # `harvest_bdock_station.py --keep-parsek --expect-situation ORBITING`
        # (situation gate PASSED on 'Duna Rocket' ORBITING at Moho, no --force)
        # from a SNAPSHOT of the produced save rather than from the instance
        # directly -- see the note below. Every number here was re-measured off
        # THESE COMMITTED BYTES with saveparse.parse_parsek_scenario +
        # observed_structure_facets, never copied from the run.
        #
        # WHY A SNAPSHOT, and it cost a flight to learn: the saveTemplate LEAF
        # IS the runSaveName, and B20 shares `fixtures/saves/b18-dres-pad` with
        # the sibling B21-eeloo-orbit lane, so BOTH stage into
        # `automation/stock-minimal/saves/b18-dres-pad`. The produced save is
        # destroyed by whichever run stages next. A first harvest of THIS lane
        # was overwritten exactly that way, and the situation gate caught it
        # ('is PRELAUNCH, expected ORBITING') -- the gate was then overridden
        # with --force, which turned a correct refusal into silent data loss.
        # DO NOT PASS --force TO A FIXTURE HARVEST. Copy the produced save out
        # first and harvest the copy.
        #
        # WHAT THE FIVE RECORDINGS ARE, by measured span UT -- the SAME five
        # roles as dres-orbit-recorded, because the craft and the ascent profile
        # are identical and only the destination changed:
        #   2d99b581  main orbiter, ut 26.3 -> 2,884,092.9 (the 2.884M
        #             game-second loop-unit span), 1,834 points, Orbiting/Moho
        #   92bfc5b4  ascent booster, ut 97.1 -> 149.0, 68 points, Destroyed
        #   8bee0671  ascent booster, ut 97.1 -> 148.8, 67 points, Destroyed
        #   4fa3a07d  core Mainsail stack dropped by MechJeb AUTOSTAGE during
        #             ascent, ut 1,697.2 -> 1,716.6, 27 points, Orbiting/Kerbin
        #   8218e120  Skipper stack dropped by the JETTISON phase, ut 2,699.1
        #             -> 2,703.9 (exactly the JETTISON window), 21 points,
        #             Orbiting/Kerbin
        # So 3 terminals are Orbiting and only 2 Destroyed, and two of the
        # Orbiting three are parked DEBRIS at Kerbin rather than the Moho
        # orbiter -- the same trap dres-orbit-recorded carries. The four debris
        # point counts (68/67/27/21) reproduced EXACTLY across two flights of
        # this profile, which is what makes the topology a pin rather than a
        # snapshot of one run.
        #
        # THE SPAN IS THE INTERESTING DIFFERENCE, and V11 will care: 2.884M game
        # seconds against the Dres fixture's 20.393M. This flight's
        # Kerbin->Moho ejection window fell ~398,000 s out (B19's Dres wait was
        # ~8.4M), so almost the whole span is transfer rather than LKO loiter.
        # The unit spans ~0.99 of a Kerbin->Moho synodic (2,884,066.6 game s
        # against a physical 2,918,346.4) where the Dres unit was ~1.79 of one.
        # V11 MEASURED what the loop machinery derives from that: the unit
        # schedules on ONE window spacing where Dres took two.
        "moho-orbit-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 5,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 3, "Destroyed": 2},
            "branchPoints": {"JointBreak": 3},
            "minAuthoritativeSidecars": 20,
            "recordingIds": ["2d99b581c9e942acb8519233a9fbd64b",
                             "4fa3a07d06734889bdfceebcde3b1325",
                             "8218e1205711401fa765998c0baea66f",
                             "8bee06711eb34a84be0355523858784b",
                             "92bfc5b4b61a4fd78eac1619409f1389"],
            "schemaGeneration": 4,
        },
        "bdock-recorded": {
            "trees": 2, "committedTrees": 2, "recordings": 19,
            "supersedes": 0, "tombstones": 0, "rewind_points": 3,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 5, "Destroyed": 12, "Docked": 1},
            "branchPoints": {"JointBreak": 10, "Dock": 1, "Undock": 1},
            "minAuthoritativeSidecars": 75,
            "recordingIds": ["0821dac8ecae4eac8522d7e88cd76705",
                             "08d3217670d341de8c94cc0d9defea69",
                             "1cfb0ec7f90e4bdda580b348f142232c",
                             "30e3d912eb3b406ab5f745b267634064",
                             "37d0dc074351408ba0374230793abb1c",
                             "4af6cfd725d646ccbac9ef2f7749667e",
                             "4f7042d450ca44e9936a355864dee3d6",
                             "500c0ba9c18b4e2f96d64dd4d3b40b63",
                             "5157d6555bd3499592c46d8508dbedf4",
                             "8bec4c80a8854508b2f1a406a4ab4669",
                             "9bd1a291bdd64ecab0c207190c8b0a27",
                             "a32f62f52dc84d6a94daf93460ec6548",
                             "ab5fbd335b22413c8b792a3cd394904d",
                             "ae60f691c24a49658391c95d7d46ce9a",
                             "b07cfd6cc27d47e7a6fb497d9836e665",
                             "e48bd55861804c55aa2748d931a43d78",
                             "f049901e1f4641ffae490b2f52b1d55e",
                             "f17e1186e9ed449b93650eb5f011a932",
                             "fd29c89536564f31bccec5c8e3f0fbc9"],
            "schemaGeneration": 4,
        },
        # --- THE CAREER-LEDGER STRICT SUBJECT ----------------------------
        # PROVENANCE, and it is a two-step one rather than a harvest: harness run
        # `2026-08-19_2130_L3-career-science-recover` (PASS attempt 1, MISSION-OK
        # across all nine phases, zero `[Parsek][ERROR]` lines) produced the save;
        # it was committed as the xUnit fixture
        # `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`, where
        # `C2CareerPostFixReplayTests` proves its ledger replays to within float
        # noise of KSP's own pools; and `harness/tools/build_career_earned_pad.py`
        # then derives THIS fixture from it BY CONSTRUCTION by splicing in one
        # PRELAUNCH vessel. The derivation (including byte-identity with a fresh
        # rebuild) is gated by `CareerEarnedPadFixtureDriftTests` in
        # `harness/lib/test_career_earned_pad.py`.
        #
        # WHY THE VESSEL HAD TO BE SPLICED, since it is the one thing here that is
        # not a harvest artifact: the flight RECOVERED its craft, so the produced
        # save carries zero VESSEL nodes, and `L4-ledger-groundtruth-strict` drives
        # a `Scene = GameScenes.FLIGHT` in-game category. A vessel-less save routes
        # LoadGame to NoVesselSpaceCenter and the batch would scene-skip its only
        # declaration.
        #
        # THE 1/1/2 TOPOLOGY IS THE CONTRACT: the flown main recording plus the
        # post-recovery continuation, both `Immutable`, in ONE committed tree - the
        # same pair `L3-career-science-recover` pins as `recordings.count = 2` and
        # the same pair the ledger's recovery credit resolves through. A different
        # number means the fixture was re-derived from something else.
        # --- THE SAME-NAME RELAUNCH SUBJECT (THE RECOVERY CORRELATOR) ----
        # PROVENANCE: derived BY CONSTRUCTION by
        # `harness/tools/build_career_same_name_pad.py`, which splices
        # `C2CareerPostFix`'s RECORDING_TREE (the two chained `Jumping Flea`
        # recordings, `recordedVesselGuid = f77e4207...`) into the PRE-FLIGHT
        # career `career-science-pad` - the save that produced them. The two
        # halves are two moments of ONE timeline, which is why the recordings'
        # `preLaunchFunds = 500000` / `preLaunchScience = 100` are the host's own
        # live pools. Byte-identity with a fresh rebuild is gated by
        # `CareerSameNamePadFixtureDriftTests` in
        # `harness/lib/test_career_same_name_pad.py`.
        #
        # WHY IT EXISTS AND WHY IT IS NOT `career-earned-pad`, measured rather
        # than argued: `L6-career-same-name-recover` reading run 1
        # (`2026-09-02_1137`) flew the recover mission over `career-earned-pad`
        # and never reached recovery - that save is L3's PRODUCED one, so the
        # launchpad-biome science is already banked at cap and TRANSMIT credited
        # 0.0, failing the mission's structural transmit -> recover gate. The
        # banked-science conflict is intrinsic to reusing a produced save. THIS
        # fixture carries the same two same-name recordings with NO banked
        # `Science` subject and the seed pools, so a second launch of the same
        # craft can earn, transmit and be recovered.
        #
        # THE 1/1/2 TOPOLOGY IS THE SAME CONTRACT `career-earned-pad` states -
        # the same spliced pair, so a different number means the derivation read
        # a different tree. What differs is the LIVE launch identity: the host
        # vessel's `pid` is re-stamped away from those recordings'
        # `recordedVesselGuid` (so the guid filter has two conclusive mismatches
        # to drop) while its craft-baked `persistentId` is deliberately LEFT
        # colliding, which is the trap the correlator has to survive.
        "career-same-name-pad": {
            "trees": 1, "committedTrees": 1, "recordings": 2,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 1},
            "branchPoints": {},
            # 8 = two recordings x (.prec + .pann + _vessel.craft +
            # _ghost.craft); the `.prec.txt` mirrors are committed but excluded
            # from this floor by the `.txt` filter.
            "minAuthoritativeSidecars": 8,
            "recordingIds": ["1d611e7533a64508ae6f3b305a51615e",
                             "5436a7e8840b4c5885afcbaedc9dc037"],
            "schemaGeneration": 4,
        },
        "career-earned-pad": {
            "trees": 1, "committedTrees": 1, "recordings": 2,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 1},
            "branchPoints": {},
            # 8 = two recordings x (.prec + .pann + _vessel.craft + _ghost.craft).
            # The `.prec.txt` mirrors are committed (the harness requires them
            # per-trajectory) but excluded from this floor by the `.txt` filter
            # above, which is the point of that filter.
            "minAuthoritativeSidecars": 8,
            # RE-PINNED 2026-08-20 (branch `kerbal-xp-row`): KSP mints fresh
            # recording ids per run, so the second harvest of `C2CareerPostFix`
            # (run `2026-08-20_1925_L3-career-science-recover_run2`, flown to add
            # the recovery's `KerbalExperience` ledger row) moved both of these.
            # The 1/1/2 topology above did NOT move, which is the check that the
            # re-harvest produced the same SHAPE of subject and not a different
            # one.
            "recordingIds": ["1d611e7533a64508ae6f3b305a51615e",
                             "5436a7e8840b4c5885afcbaedc9dc037"],
            "schemaGeneration": 4,
        },
        # --- THE FIRST SURFACE-ENDPOINT SUBJECT (ATMOSPHERIC) ------------
        # PROVENANCE: kerbin-splashdown-recorded <- B4-reentry-splashdown, run
        # 2026-08-24_1431, PASS attempt 1 (wall 1,065 s, mission wall 989.2 s,
        # MISSION-OK across PRELAUNCH -> MJ-ASCENT -> CIRCULARIZE -> ORBIT ->
        # DEORBIT -> REENTRY -> SPLASHDOWN, both telemetry assertions met),
        # --keep-parsek. DLL provenance: the post-#1523 auto-merge-ON build,
        # deployed hash prefix 27f5f31907efce27.
        #
        # **THE SUITE'S FIRST COMMITTED RECORDING THAT DOES NOT END AT AN
        # ORBIT.** Fourteen orbit and transfer fixtures preceded it and not one
        # landed or splashed save existed, which is the whole reason every V
        # loop lane before G3a reads its arrival through a conic. V22M / V22T /
        # V22K consume this one.
        #
        # THE TERMINAL IS `Landed`, NOT `Splashed`, AND THAT IS A MEASUREMENT.
        # B4's own `landedSituations = ["LANDED", "SPLASHED"]` accepts either and
        # the phase is NAMED splashdown, so the terminal this harvest carries was
        # genuinely unknown until the bytes existed. The mission asserted
        # `landedSituation value=LANDED met=True` at alt 0.687 m, the tree's
        # `terminalState = 1` decodes to `TerminalState.Landed`, the save's own
        # active Ship VESSEL reads `sit = LANDED splashed = False`, and
        # `TERMINAL_POSITION` sits at lat -0.120 / lon 86.698 / alt 152.476
        # against `terrainHeightAtEnd = 151.923` - dry land on Kerbin's equatorial
        # continent east of KSC, a shoreline touchdown rather than a water one.
        # A legal B4 pass, and V22's `terminalStates` window is declared from it.
        #
        # THE TOPOLOGY IS SEVEN, WHICH IS NOT THE NINE THE COMMIT-BLIND COUNT
        # READS. `run.py count_recordings` observed 9 and B4's own count window
        # is {8,9}; the committed tree carries SEVEN recordings (1 subject + 6
        # booster debris) over THREE JointBreak branch points. The two are not in
        # conflict - the count is commit-blind by design - and the difference was
        # exactly two UNCOMMITTED single-POINT sidecar stubs the harvest brought
        # across (91c0ea09... at UT 650.254 and fc56a5b8... at UT 649.414, both
        # `sectionAuthoritative = False`, neither named anywhere in
        # persistent.sfs, and one of them with no `_vessel.craft` at all).
        #
        # THOSE SEVEN FILES WERE PRUNED BY HAND before this fixture was committed,
        # and the reason is a real invariant rather than tidiness:
        # `CommittedFixtureMirrorTests.test_the_authoritative_snapshot_binaries_
        # are_still_committed` requires every committed `.prec` to keep its
        # authoritative `_vessel.craft`, and an orphan stub cannot. They are
        # harvest exhaust of a class no prior recorded fixture produced, because
        # every prior one carried 1-2 recordings, so
        # `harvest_bdock_station.py` has no "sidecar with no RECORDING row" prune
        # to catch them (it prunes readable mirrors, backup dirs and
        # `Parsek/Saves`). Worth adding there before the next multi-recording
        # harvest; nothing about it is a Parsek finding.
        #
        # `branchPoints` = 3 JointBreak and `terminalStates` = 6 Destroyed + 1
        # Landed ARE the contract: the Kerbal X sheds three booster pairs at UT
        # 48.48 / 64.12 / 83.16 and every one of those six children is destroyed
        # on impact while the pod alone survives to the surface.
        #
        # THE BYTES V22M/V22T/V22K ANCHOR ON:
        #   tree             c05c834cd2754892b4588e7ce9220c3f
        #   main recording   28b6e543d67c4d1c9e4763b451c01df5
        #   explicitStartUT  34.5399999999998      (UT0 - the V6M convention)
        #   explicitEndUT    1,274.6835614006252
        #   span             **1,240.143561401 s** (and here first-POINT to
        #                    last-POINT equals the explicit stamps exactly, which
        #                    is NOT true of every fixture - vall-transfer's differ)
        #   FOUR ORBIT_SEGMENTs, ALL `Kerbin`, and **ZERO body-change seams** -
        #     the launch-body-only shape V22M pre-registers as starving both
        #     routing roads. The last segment ends at 853.723561, so the
        #     recording closes with a **420.960 s SEGMENT-LESS DESCENT TAIL**,
        #     which is the part V22 exists to read.
        #   save clock (FLIGHTSTATE UT)  1,276.6235614006234
        #   pointCount 1285, endpointBodyName Kerbin, endpointPhase 1
        "kerbin-splashdown-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 7,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 1, "Destroyed": 6},
            "branchPoints": {"JointBreak": 3},
            # 28 = seven recordings x (.prec + .pann + _vessel.craft +
            # _ghost.craft), which after the orphan prune above is also the exact
            # committed count. Stated against the COMMITTED payload on purpose.
            "minAuthoritativeSidecars": 28,
            "recordingIds": ["095aea3ad69949deb610d178576d15d2",
                             "28b6e543d67c4d1c9e4763b451c01df5",
                             "3bebe6f76d8d4422a4780a472aee25f3",
                             "538e778897a24beba12baf394415d029",
                             "b3cd21d7ca2a45a986f27c368a26dca7",
                             "bc1ee56e2d584fcabe45e25d32ca2ee3",
                             "d2226a5d9a4947b5889ec273aead95a7"],
            "schemaGeneration": 4,
        },
        # --- THE FIRST SURFACE-ENDPOINT SUBJECT (AIRLESS) ----------------
        # PROVENANCE: mun-landing-recorded <- B13-mun-landing, run
        # 2026-08-24_1449, PASS attempt 1 (wall 2,811 s, mission wall 2,764.1 s -
        # the suite's most expensive scenario - MISSION-OK across the full
        # twenty-one-phase chain through SURFACE-COMMITTED, `landedOnTargetBody
        # value=Mun` and `landedStable value=SURFACE-COMMIT` both met),
        # --keep-parsek. DLL provenance: the post-#1523 auto-merge-ON build,
        # deployed hash prefix 27f5f31907efce27.
        #
        # WHY IT EXISTS ALONGSIDE `kerbin-splashdown-recorded`: this is **V6 WITH
        # THE ENDPOINT MOVED FROM ORBIT TO SURFACE** - same launch body, target,
        # parent, road, craft family and fixture ancestry as `mun-orbit-recorded`,
        # with EXACTLY ONE shape dimension moved. That makes it the control that
        # separates the two dimensions V22 moves at once (endpoint type AND a
        # multi-recording debris tree). It is also AIRLESS, so its ghost suppresses
        # under `polyline-owns-phase` / `belowAtmosphere=False` where the Kerbin
        # sibling takes the `below-atmosphere` branch - the two fixtures pin
        # different reason tokens on purpose.
        #
        # THE TOPOLOGY IS EIGHT AND THE COMMIT-BLIND COUNT AGREES (both 8, against
        # B13's own {8,8} window) - no orphan stubs here, unlike the Kerbin
        # sibling. FIVE JointBreak branch points, and note that the FIRST of them
        # (UT 34.52, `debrisCount = 3`) carries NO `childId` rows at all: three
        # launch-clamp children coalesced away without minting recordings. Seven
        # Destroyed + one Landed.
        #
        # THE BYTES V23M/V23T ANCHOR ON:
        #   tree             0da22482a8a648c6835b7bcd6b0f200d
        #   main recording   61f3775361fe4130a66a69b1425b7209
        #   explicitStartUT  34.5199999999998      (UT0 - the V6M convention;
        #                    segment 0's startUT is 230.013733, a **195.494 s**
        #                    gap, by far the widest in the corpus and the one that
        #                    would move every bracket if the wrong one were used)
        #   explicitEndUT    23,256.9452936296
        #   span             **23,222.425293630 s**
        #   ELEVEN ORBIT_SEGMENTs: `Kerbin` 0-7 then `Mun` 8-10, with ONE
        #     body-change seam at the adjacent `endUT == startUT` pair
        #     **16,425.168889** - a seam offset of **16,390.648889 s** off UT0,
        #     the number `Orbital(Mun) same-parent ... off=` should reproduce.
        #   The last segment ends at 20,966.465294, so the recording closes with a
        #     **2,290.480 s SEGMENT-LESS DESCENT-AND-LANDED TAIL** - the part V23
        #     exists to read, and five times longer than the Kerbin sibling's.
        #   save clock (FLIGHTSTATE UT)  23,258.405293629632
        #   pointCount 1442, endpointBodyName Mun, endpointPhase 1
        "mun-landing-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 8,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 1, "Destroyed": 7},
            "branchPoints": {"JointBreak": 5},
            # 32 = eight recordings x (.prec + .pann + _vessel.craft +
            # _ghost.craft), which is also the exact committed count here.
            "minAuthoritativeSidecars": 32,
            "recordingIds": ["2927d112f61743be94e431e91dd79830",
                             "5c4faaf2d48345138df2620561d9c38f",
                             "61f3775361fe4130a66a69b1425b7209",
                             "940b1f5a759347bb8b44e9cab7da089c",
                             "9697229f1b644c948dce380c64b133ca",
                             "cee839f75a824bc2b65a8e2350ab1993",
                             "d3a8c31ace604370b474d04a4584bf7f",
                             "d9b5675fc64f439aa5fff170f72d6ad4"],
            "schemaGeneration": 4,
        },
        # --- THE FIRST FREE-PLAY SUBJECT (A NEW PROVENANCE CLASS) --------
        # PROVENANCE: duna-one-recorded <- THE OPERATOR'S OWN HAND-PLAYED
        # SESSION, snapshot `logs/2026-08-25_1537_s15-duna-one-manifest-run2`
        # (save `s15`), VISUALLY VALIDATED BY THE OPERATOR 2026-08-25. Harvested
        # `--save-dir <log copy>/saves/s15 --target-name duna-one-recorded
        # --expect-situation PRELAUNCH --keep-parsek` (the gate passed on
        # 'Jumping Flea' PRELAUNCH, vessels=13), then stripped and repaired by
        # `harness/tools/build_duna_one_recorded.py`.
        #
        # WHY THAT PROVENANCE IS DIFFERENT FROM EVERY ENTRY ABOVE, and why it
        # matters more than the usual run id: all fourteen siblings are the
        # product of a DRIVEN harness run - one scenario, one craft, one
        # committed tree, harvested verbatim out of the produced save. This one is
        # a real mission a human flew for its own sake, so it carries shapes a
        # synthesised profile does not reach: a four-segment vessel CHAIN across
        # an interplanetary transfer, a controlled-decoupled probe, an EVA branch
        # point, six pieces of ascent debris, a loop-ARMED MISSION row
        # (`loopPlayback = True`), and a save clock five billion seconds in. That
        # is what the M-A7 RC-WARP lane needs to read.
        #
        # IT IS NOT THE HARVEST'S RAW OUTPUT, and the difference is a recipe, not
        # a hand-edit. A free-play save carries a career, not a subject: this one
        # held FOUR unrelated RECORDING_TREEs, 47 sidecar families, five stand-in
        # kerbal slots, one orphan RECORDING_SUPERSEDES row, 12 MB of orphan-sweep
        # `_quarantine`, and twelve analyzer INV2 FAILs. Everything kept or
        # dropped is spelled out in `build_duna_one_recorded.py`, which also has a
        # `--check` mode wired to `DunaOneRecordedFixtureDriftTests` in
        # `test_build_duna_one_recorded.py`, so a hand-edit of these bytes reds in
        # the harness suite rather than in a live flight. The committed save reads
        # GREEN under `analyze-recordings.ps1 -FailOnRed -FreshSaveGate`
        # (`FAIL=0 WARN=15 RED=0`; the 15 are INV8 phantom-attribution WARNs from
        # the restored `ledger.pgld`, which still records the whole free-play
        # career - see the todo entry).
        #
        # THE INV2 REPAIR, named here because it is the one place the committed
        # bytes differ from what Parsek wrote. The main transfer recording
        # `61e9177193444e329247d0e8288cf91e` carried SIX redundant TrackSections,
        # each a duplicate of coverage a neighbour already owned, and
        # `Inv2NoDoubleCover` FAILs on every one. The builder dropped exactly
        # these six (index, span) and NOTHING ELSE - no trajectory point moved, no
        # other recording was touched, and the top-level 22-entry ORBIT_SEGMENT
        # list is byte-untouched:
        #   34  [64044032.725027621, 65004886.739419721]  Kerbin->Sun seam
        #   43  [70898646.0584081,   70912683.547375381]  Sun->Duna seam
        #   47  [70956143.35894987,  70956471.231831044]  Duna->Ike seam
        #   51  [70958360.7066507,   70958731.38776888]   Ike->Duna seam
        #   60  [70960696.459866241, 70960923.929514691]  contained in 61
        #   62  [70960923.929514691, 70962487.1269182]    contained in 61
        # Sections 34/43/47/51 are the frame-less `ref=0 src=0` shells of an
        # EXACT-span pair whose other half is the `ref=2 src=2` OrbitalCheckpoint
        # carrying that span's ORBIT_SEGMENT; 60 and 62 partition section 61
        # exactly, and 62's nested segment is element-for-element identical to
        # 61's. Coverage is therefore invariant, which the builder asserts rather
        # than assumes. The four seam positions are NOT a coincidence - see
        # todo-and-known-bugs.md -> RECORDER-SUSPECTED-DOUBLE-EMIT-AT-SOI-SEAM.
        #
        # THE BYTES THE RC-WARP LANE ANCHORS ON, all re-measured off THESE
        # COMMITTED BYTES:
        #   tree id          1ccdb19215034ac19f3a8e31697b05ed
        #     root group "Duna One", MISSION 0aad5325bcfb4ea1a147d8691ec26443
        #     name "Duna One", loopPlayback True, loopAnchorUT 5180683162.3895044
        #   main transfer    61e9177193444e329247d0e8288cf91e (chainIndex 1 of the
        #     four-segment chain aff63064eefd4ee0a099f5c57728bb55)
        #   explicitStartUT  52,569,490.911798075   (UT0 - the V6M convention;
        #                    ORBIT_SEGMENT 0's startUT is 52,569,494.685523860,
        #                    a 3.774 s gap that would put every bracket off)
        #   explicitEndUT    70,963,652.639611751   span 18,394,161.727813676 s
        #   TWENTY-TWO top-level ORBIT_SEGMENTs, body roster Kerbin x9 (0-8),
        #     Sun x3 (9-11), Duna x2 (12-13), Ike x2 (14-15), Duna x6 (16-21),
        #     and FOUR body-change seams - more than any other committed subject -
        #     each an exact adjacent `endUT == startUT` pair. Offsets from
        #     explicitStartUT, quoted as the Python repr of the subtraction:
        #       Kerbin->Sun at 64,044,032.725027621  off 11474541.813229546
        #       Sun->Duna   at 70,898,646.0584081    off 18329155.14661002
        #       Duna->Ike   at 70,956,143.35894987   off 18386652.447151795
        #       Ike->Duna   at 70,958,360.7066507    off 18388869.79485263
        #     The Duna->Ike->Duna pair is a 2,217.348 s Ike SOI GRAZE on the way
        #     in, not a moon capture; the destination is Duna.
        #   save clock (FLIGHTSTATE UT)  5,336,112,610.6518345, activeVessel 8
        #     ('Jumping Flea', PRELAUNCH), 13 VESSEL nodes, Mode SANDBOX
        #   pointCount total 1921 over the 13 recordings (largest 681 = the
        #     transfer, smallest 18 = the landing tail)
        #
        # `terminalStates` SUMS TO 9, NOT 13, AND THAT IS CORRECT: the four
        # members of the chain (5d68d429 / 61e91771 / 0b91670c / 7609b87b) carry
        # NO `terminalState` key at all, so `observed_structure_facets` counts
        # none for them. Landed 2 = the landed Kerbal X `cead1f22` plus
        # Valentina's EVA `f9caa140`; Orbiting 1 = the decoupled `Kerbal X Probe`
        # `6561c8eb`; Destroyed 6 = the six ascent debris. `branchPoints` carries
        # the suite's FIRST `EVA` entry alongside 4 `JointBreak`.
        #
        # `minAuthoritativeSidecars` IS 50, NOT 52 (13 x 4), and that is also
        # correct: `61e91771` and `0b91670c` are chain CONTINUATIONS and reuse the
        # chain head's `_vessel.craft` rather than carrying one - which is why
        # `CommittedFixtureMirrorTests` grew its chain-continuation exemption for
        # this fixture. The committed count is exactly 50.
        "duna-one-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 13,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Orbiting": 1, "Landed": 2, "Destroyed": 6},
            "branchPoints": {"EVA": 1, "JointBreak": 4},
            "minAuthoritativeSidecars": 50,
            "recordingIds": ["0b91670cf8334780a2b78687e80d6923",
                             "4ed6e4f2767d455685b64b488704a023",
                             "5d68d429060b429987bc8be7bb930bd2",
                             "61e9177193444e329247d0e8288cf91e",
                             "6561c8eb97dd48d6825e9d6c7c04d22a",
                             "6dae41d1b2584f0cbc49afdd587cbdfe",
                             "70e9c28bd20947d0afeaa1deb9215e34",
                             "7609b87bc4fa44788ecd180e177b8475",
                             "7c92064d5d0640a1a18126bc2aab2cc7",
                             "b06a8b5f81274995879f42b67d24eae8",
                             "cead1f22d40443f48d1bd955ad072257",
                             "efe1ab5e2fbd48dd868c724f3ab56344",
                             "f9caa140787248f3b67d48dfcf494c7b"],
            "schemaGeneration": 4,
        },
        # --- THE HELIOCENTRIC-PARKING DEPARTURE ---------------------------
        # PROVENANCE: duna-park-recorded <- THE SAME OPERATOR SAVE as
        # `duna-one-recorded` above (snapshot
        # `logs/2026-08-25_1537_s15-duna-one-manifest-run2`, save `s15`,
        # visually validated 2026-08-25), harvested `--save-dir <log copy>/saves/s15
        # --target-name duna-park-recorded --expect-situation PRELAUNCH
        # --keep-parsek` (the gate passed on 'Jumping Flea' PRELAUNCH,
        # vessels=13), then stripped and repaired by
        # `harness/tools/build_duna_park_recorded.py`. That save carried FOUR
        # unrelated RECORDING_TREEs; this fixture keeps a DIFFERENT one from its
        # sibling, so the two are disjoint payloads out of one harvest.
        #
        # WHY IT IS A SEPARATE SUBJECT AND NOT A DUPLICATE OF `duna-one-recorded`.
        # Both are crewed Duna missions with a multi-segment chain, ascent
        # debris, a decoupled probe and an EVA - the counts below barely separate
        # them. What separates them is HOW THEY GET TO DUNA, which lives in the
        # transfer recording's ORBIT_SEGMENT list and which no saveparse facet
        # reads:
        #
        #   `duna-one-recorded` (tree 1ccdb192) is a DIRECT transfer. It parks in
        #   KERBIN orbit (six consecutive Kerbin segments at sma 731,229.576,
        #   ecc 0.00131), ejects, and its three Sun segments are ONE conic split
        #   by warp (sma 17,604,964,389.77 throughout). The departure burn
        #   happens inside Kerbin's SOI.
        #
        #   THIS ONE (tree ced78481, "Kerbal X #2") is a HELIOCENTRIC PARKING
        #   DEPARTURE - the operator's own description was "orbits the star until
        #   alignment is good". Its transfer `aa48920e...` (856 points, the
        #   largest recording in the source save) ejects from Kerbin almost
        #   immediately (segment 1 = Kerbin sma 55,427,165.82, ecc 0.97577, an
        #   escape ellipse) and then COASTS ON ONE SUN ORBIT across THREE
        #   consecutive segments whose elements agree to ten significant figures:
        #     seg 2  sma 14,072,049,898.090191  ecc 0.0326934153364881
        #            [2,547,568,544.056056  -> 2,560,670,336.8959155]
        #     seg 3  sma 14,072,049,898.089064  ecc 0.032693415336629207
        #            [2,560,670,342.59591   -> 2,560,985,257.3773251]
        #     seg 4  sma 14,072,049,898.090006  ecc 0.03269341533672783
        #            [2,560,985,293.2372909 -> 2,561,070,763.99165]
        #   That is 13,502,219.94 s held (about 156 Kerbin days) at 3.5% outside
        #   Kerbin's own heliocentric sma (13,599,840,256 m) - a PHASING orbit,
        #   not a transfer. THE DEPARTURE BURN IS AN ELEMENT STEP at
        #   2,561,070,900.0315204: seg 5 jumps to sma 17,908,765,008.460636 /
        #   ecc 0.19216439941 (+27% sma, +488% ecc).
        #
        # The third candidate was ruled out MECHANICALLY: tree 7f01f8b9 (also
        # named "Kerbal X") carries NO Sun segment at all - its two chains run
        # Kerbin -> Mun -> Kerbin and never leave the Kerbin system.
        #
        # THE BYTES A LANE ANCHORS ON, all re-measured off THESE COMMITTED BYTES:
        #   tree id          ced7848157674b8ea19311377c0f6fbc
        #     root group "Kerbal X #2", MISSION 6fa271def0a549eb8375ddbf445b1344
        #     name "Kerbal X #2", loopPlayback False,
        #     loopAnchorUT 5080722184.761054
        #   park transfer    aa48920e43fb4bf483940e0d8191a1ce (chainIndex 1 of the
        #     four-segment chain 1d63a7a6cd86466389b748bf8f092f42)
        #   explicitStartUT  2,547,277,637.2482719  explicitEndUT
        #     2,570,542,381.0310211  span 23,264,743.782749 s
        #   FOURTEEN top-level ORBIT_SEGMENTs, body roster Kerbin x2 (0-1),
        #     Sun x4 (2-5), Duna x8 (6-13), and TWO body-change seams:
        #       Kerbin->Sun at 2,547,568,544.056056
        #       Sun->Duna   at 2,570,454,935.6223264
        #     Duna arrival is hyperbolic (ecc 3.6025) and CAPTURES into an
        #     ellipse at 2,570,492,255.33601 (sma 495,883.11, ecc 0.042042).
        #   save clock (FLIGHTSTATE UT)  5,336,112,610.6518345, activeVessel 8
        #     ('Jumping Flea', PRELAUNCH), 13 VESSEL nodes, Mode SANDBOX - all
        #     four shared with `duna-one-recorded`, which is stripped from the
        #     same save and therefore carries the same world.
        #   pointCount total 2706 over the 14 recordings (largest 856 = the park
        #     transfer, smallest 26 = the decoupled probe)
        #
        # THE INV2 REPAIR, named here because it is the one place the committed
        # bytes differ from what Parsek wrote. The analyzer Forbid gate was run
        # FIRST on the freshly stripped bytes and read `FAIL=4 WARN=16 RED=1`;
        # the builder then dropped exactly these four redundant TrackSections
        # from `aa48920e...` (52 -> 48) and NOTHING ELSE - no trajectory point
        # moved, no other recording was touched, and the 14-entry top-level
        # ORBIT_SEGMENT list is byte-untouched:
        #    1  [2547277750.9906116, 2547280707.4508519]  frame-less shell,
        #       a strict prefix of section 2
        #    3  [2547280707.4508519, 2547281403.3664637]  a re-clip of section
        #       2's conic (same elements, same epoch), contained in it
        #   12  [2547568544.056056,  2560670336.8959155]  KERBIN->SUN SOI SEAM:
        #       a `ref=0` shell duplicating section 13, the checkpoint that
        #       carries the PARK segment itself
        #   30  [2570454935.6223264, 2570490859.060472]   SUN->DUNA SOI SEAM:
        #       the same shape beside section 31's arrival checkpoint
        # Two of the four sit exactly on SOI seams, which is not a coincidence -
        # see todo-and-known-bugs.md -> RECORDER-SUSPECTED-DOUBLE-EMIT-AT-SOI-SEAM.
        # After the repair the save reads GREEN under
        # `analyze-recordings.ps1 -FailOnRed -FreshSaveGate`
        # (`FAIL=0 WARN=16 RED=0`; the 16 are INV8 phantom-attribution WARNs from
        # the restored `ledger.pgld`, which still records the whole free-play
        # career - the same class the sibling carries 15 of).
        #
        # `terminalStates` SUMS TO 10, NOT 14, AND THAT IS CORRECT: the four
        # members of the chain (8538d9e1 / aa48920e / acf1435a / c5d0148a) carry
        # NO `terminalState` key at all. Landed 3 = the landed Kerbal X
        # `5466feba`, Merger's EVA `2f724cbd`, and debris `8884af4a`;
        # Destroyed 4 + SubOrbital 1 = the ascent debris; Orbiting 2 = the
        # decoupled probe `ed76396d` and the Duna-shed debris `9f68333f`.
        #
        # `minAuthoritativeSidecars` IS 54, NOT 56 (14 x 4): `aa48920e`
        # (chainIndex 1) and `acf1435a` (chainIndex 2) are chain CONTINUATIONS
        # and reuse the chain head's `_vessel.craft`. NOTE that `c5d0148a`
        # (chainIndex 3) is NOT among them - it carries its own - so the
        # exemption is a MEASURED list, not "every continuation".
        "duna-park-recorded": {
            "trees": 1, "committedTrees": 1, "recordings": 14,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 3, "Destroyed": 4, "SubOrbital": 1,
                               "Orbiting": 2},
            "branchPoints": {"EVA": 1, "JointBreak": 6},
            "minAuthoritativeSidecars": 54,
            "recordingIds": ["2f724cbd0030407f884e125e89f0add0",
                             "5466febaeffa4042ba211c0e1fe88b91",
                             "5f7e2b8768a642deb919163c8d2ed8ee",
                             "8538d9e156a34af194083c6068739888",
                             "8884af4a14f84256b73704a97a2e5476",
                             "8eabd16b458f4317a24e2e1adc6cec9c",
                             "9f68333fa762479e81bfe7d1827d4afd",
                             "aa48920e43fb4bf483940e0d8191a1ce",
                             "acf1435a6df24b2aa2abbc6da88d6b36",
                             "c5d0148a530348c9a5effad16021b836",
                             "d154c46d7d884bb582559e18fb55e730",
                             "df7ebece093b4c828f9a41c25e18927c",
                             "ed76396d1c8e42a8b4e9c7a39f868063",
                             "f475897c8f4a4523835b3620197b5af2"],
            "schemaGeneration": 4,
        },
        # --- THE FIRST ROUTE SUBJECT (B27) --------------------------------
        # PROVENANCE: depot-route-recorded <- THE OPERATOR'S OWN FREE-PLAY
        # SANDBOX SAVE `Kerbal Space Program/saves/orbital supply route DELIVERY
        # test` (340,420 B), harvested from a scratch COPY (the live save is
        # read-only) with `--target-name depot-route-recorded
        # --expect-situation ORBITING --keep-parsek`, then finished by
        # `harness/tools/build_depot_route_recorded.py`.
        #
        # WHY IT IS A HARVEST AND NOT A FLIGHT, which is a ratified deviation
        # from B27's register entry rather than a shortcut. That entry called for
        # a route forged "over the BDOCK station fixture", and that path is
        # CLOSED: route candidacy is gated on `IsTreeFullySealed`, and `SealSlot`
        # / `RouteCommand` are RESERVED command-seam verbs (H35
        # ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH), so no driven run can
        # create a ROUTE at all today. Free-play harvest - the
        # `duna-one-recorded` provenance class - is the only verb-free path.
        # B27 therefore ships as a FORGE-CLASS STAMP (tool + drift test, no
        # flight); the flight variant stays deferred behind those two verbs.
        # The deviation is recorded in `docs/dev/autotest-roadmap.md`'s G1 note.
        #
        # THE FIXTURE IS NAMED `depot-route-recorded` AND NEVER AFTER THE SOURCE
        # SAVE, deliberately: `run.py::stage_fixture` rmtree's the same-named
        # save inside the automation instance, so a fixture called
        # `orbital supply route DELIVERY test` would delete the operator's
        # hand-played save the first time any scenario staged it.
        #
        # THE ROUTE - the one thing this fixture exists for. ITS SHAPE IS PINNED
        # IN THIS MAP as of 2026-09-02 (the `"routes"` key below), read through
        # `saveparse.observed_routes_facets`; the SAME dict is
        # `build_depot_route_recorded.ROUTE_FACET_PINS`, so the sweep and the
        # builder check are two consumers of ONE measurement rather than two
        # hand-kept copies. The float CLOCKS, the four SOURCE rows' nine
        # `RevalidateSources` fields and the STOP endpoint's resolution to a live
        # `Depot` VESSEL node stay builder-side in `verify_route` (wired into the
        # suite by `DepotRouteRecordedFixtureDriftTests`): a facet should not
        # carry a fixture's float identity. The full route, for reference:
        #   id                    5420f805fcbb453b8d5928b71393f14b
        #   name                  "Route: Kerbin -> Kerbin" (the real string
        #                         carries U+2192, not an ASCII arrow)
        #   status                Active, completedCycles 1, skippedCycles 0,
        #                         pauseAfterCurrentCycle True, isKscOrigin True
        #   backingMissionTreeId  c9ef80ee91b34de2b3717a4fb8bd1226
        #   dockMemberRecordingId 70667ab4a1d34ef0bc05ce9911bfcd30
        #   recordedDockUT        17,478.248634212287 (= nextDispatchUT)
        #   dispatchWindowEpochUT 1,420.246738452149
        #   dispatchWindowPeriod  0 (SameBody - both ends are Kerbin, so there is
        #                         no synodic window and the cadence is the
        #                         transit duration alone)
        #   dispatchInterval      16,058.001895760137 (= transitDuration)
        #   RECORDING_IDS         44129e52 / 8b036c83 / 0c8ec58d / 70667ab4
        #                         (treeOrders 0 / 7 / 8 / 9 of the backing tree)
        #   STOP                  DockingPort, endpoint vesselPersistentId
        #                         3620499050 = `Depot`, Kerbin, alt 215,032.70 m,
        #                         DELIVERY_MANIFEST LiquidFuel 80.28 / Oxidizer
        #                         98.12
        #
        # BOTH TREES ARE KEPT WHOLE, and neither half of that is bookkeeping.
        # `c9ef80ee` is the ROUTE's backing tree: `RouteStore.RevalidateSources`
        # compares NINE `SOURCE` fields (recordingId / treeId / treeOrder /
        # startUT / endUT / sidecarEpoch / format / generation / routeProofHash)
        # against a live rebuild, and ANY drift flips the route to
        # `SourceChanged`, which never auto-recovers and would kill the
        # GhostDriving state the lane reads. `af5628b4` ("Kerbal X") was checked
        # for independence and is NOT independent: its chain recordings
        # `56298d83` and `ed43b6fb` carry `vesselPersistentId 3620499050` and
        # `recordedVesselGuid 05d3ea0f...`, the SAME LAUNCH as the `Depot` the
        # STOP endpoint names - it is that vessel's own launch lineage. The
        # drift test asserts that guid link rather than trusting this comment.
        #
        # THE ACTIVE VESSEL WAS RE-POINTED, and this is the one edit to the save
        # body. The source save's `activeVessel = 0` is an ASTEROID
        # (`Ast. YRJ-552`), which the harvest's focusability check happily
        # accepts; the builder re-points it to index 9 = `Depot` (pid 3620499050,
        # ORBITING), re-resolving the index by name + pid rather than trusting
        # the number. `--expect-situation ORBITING` is armed against the
        # situation of the vessel that ends up focused, and is true of both.
        #
        # THE INV2 REPAIR. The analyzer Forbid gate was run FIRST, before any
        # repair, and read `FAIL=2 WARN=0 RED=1`: two INV2-NO-DOUBLE-COVER FAILs
        # on the Transporter's chain segment `a85a7ae0...` (50 TrackSections).
        # The builder dropped exactly two and NOTHING ELSE (50 -> 48):
        #   26  [6163.7967133907259, 6194.1851923946306]  a frame-less 65-byte
        #       shell with NO ORBIT_SEGMENT, a strict prefix of section 27
        #   28  [6194.1851923946306, 6590.41224317588]    a re-clip of section
        #       27's conic - same inc/ecc/sma/lan/argPe/mna/body/ofr* and the
        #       same `epoch = 6163.7967133907259`
        # 26 and 28 partition 27's span exactly, so coverage is invariant.
        # CRITICALLY `a85a7ae0` is NOT one of the four ROUTE source recordings,
        # so no `routeProofHash` and no SOURCE row covers it; the drift test
        # asserts that rather than leaving it to this comment. After the repair
        # the save reads GREEN under `analyze-recordings.ps1 -FailOnRed
        # -FreshSaveGate` (`FAIL=0 WARN=0 INFO=0 STALE=0 RED=0` - the only
        # RECORDED fixture in this map with a zero-WARN reading).
        #
        # OTHER MEASURED BYTES:
        #   save clock (FLIGHTSTATE UT)  36,138.111257421704, 19 VESSEL nodes,
        #     Mode SANDBOX. `Transporter` (pid 788309716, Ship, ORBITING) must
        #     survive alongside the Depot: it flew the delivery leg and
        #     recording `efb9be71` is its trajectory.
        #   `terminalStates` SUMS TO 19, NOT 22: the four chain members
        #     (56298d83 / ed43b6fb / 44129e52 / a85a7ae0) carry no
        #     `terminalState`, and `70667ab4` - the dock member - carries none
        #     either, because the route's cycle is still running.
        #   `branchPoints` carries the suite's first `Dock` and `Undock` entries
        #     alongside 8 JointBreak and 1 Launch.
        #   `minAuthoritativeSidecars` is 86 = 22 x .prec + 22 x .pann +
        #     21 x _vessel.craft + 21 x _ghost.craft; `0c8ec58d` carries no
        #     `_vessel.craft` and `70667ab4` no `_ghost.craft`.
        #   pointCount total 3115 over the 22 recordings (largest 638 = the
        #     Transporter's chain segment, smallest 40 = the dock member).
        #
        # WHAT THE FIXTURE DOES NOT CARRY, all removed by the builder: the
        # source's `Backup/` (four rolling `persistent (...).sfs`, ~1.1 MB), the
        # EMPTY `Parsek/RewindPoints/`, and `Ships/` (the operator's edited
        # `Kerbal X.craft` plus KSP's `Auto-Saved Ship.craft` VAB autosave - this
        # is a RECORDED render subject that launches nothing, exactly like
        # `duna-one-recorded`, which carries no `Ships/` either).
        "depot-route-recorded": {
            "trees": 2, "committedTrees": 2, "recordings": 22,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Destroyed": 14, "Orbiting": 3, "Docked": 2},
            "branchPoints": {"JointBreak": 8, "Launch": 1, "Dock": 1,
                             "Undock": 1},
            "minAuthoritativeSidecars": 86,
            "recordingIds": ["0254e32d38344f81a55aa548e46fc3f8",
                             "0c8ec58d618246e38eafedc116a262c8",
                             "0d81230e06274d40a051ad865fea45f7",
                             "1e447d2a207c4a6faca53929c799b112",
                             "271fcf13cf324aab99dedf022a0b8701",
                             "3a881d7e090d420bb83d86324a1e358d",
                             "44129e52aec64f08b25cdd3ca22ea34d",
                             "56298d8360d14db68d488f6d2aee7f72",
                             "6996cdece5f946558130f27e9fd94f9a",
                             "70667ab4a1d34ef0bc05ce9911bfcd30",
                             "8b036c83624b44e6b531f03990d31b5e",
                             "8b76aa505ea1490282a77127eca60e42",
                             "9290180cd1034abcb977e12c5e16ec4b",
                             "997efb42ed894fbd8e7f92ed628d69f4",
                             "a85a7ae00da043c28e13fe221630ce85",
                             "bc856135add54204b51fe6479fcc3947",
                             "d44fdc4cc1284b839c0cbd52718727d9",
                             "e269b79b5540422898bec716523b16e0",
                             "ed43b6fbd97b438895d9cfbaf3bf1c9b",
                             "ee42e0e523a84732a5a96b25ccedb58d",
                             "ef23bb0df71645f4833607864ea2c627",
                             "efb9be7191284013983a9f3662604bc4"],
            "schemaGeneration": 4,
            # THE CORPUS'S ONLY COMMITTED ROUTE, as the shared facet reads it.
            # Byte-identical to `build_depot_route_recorded.ROUTE_FACET_PINS`
            # (asserted below), and identical to what five produced saves of two
            # scenarios measured off these bytes: V18T runs 2026-08-26_2042 /
            # _2317 / _2318 and H40 runs 2026-08-28_2253 / _2358 all read
            # count 1 / statuses {Active: 1} / codecRejects 0 through this same
            # parser. Neither lane mutates the route, so that agreement is the
            # determinism statement a `[expectations.routes]` window rests on.
            "routes": {
                "count": 1, "dormant": 0, "stops": 1, "sourceRefs": 4,
                "completedCycles": 1, "skippedCycles": 0,
                "codecRejects": 0, "unparsed": 0, "unknownStatuses": 0,
                "unknownConnectionKinds": 0,
                "statuses": {"Active": 1},
                "connectionKinds": {"DockingPort": 1},
                "originBodies": {"Kerbin": 1},
                "destinationBodies": {"Kerbin": 1},
                "holdKinds": {},
                "ids": ["5420f805fcbb453b8d5928b71393f14b"],
                "destinationVesselPids": ["3620499050"],
                "dismissedCandidates": 0, "promptedCandidates": 0,
            },
        },
        # --- THE SUPPLY-ROUTE LANE HOST (RVR-1 / RVR-2 / RVR-3 / H56) -----
        # H56 stages it for a LIVE reason rather than for this payload: its six
        # self-provisioning `RouteDockCapture` cells read none of the corpus and
        # want the active vessel's LANDED situation, which is what drives the
        # origin-proof probe's non-PRELAUNCH branch. The pinned facets below are
        # unaffected by it - the cells' per-test baseline restore is what keeps
        # them so, and the lane's `recordings.count = 5` is the instrument.
        # PROVENANCE: rover-route-recorded <- THE OPERATOR'S OWN HAND-FLOWN
        # SANDBOX SAVE `logistics-rover-A`, collected 2026-08-30 into
        # `.claude/worktrees/logs/2026-08-30_1106_rover-route/saves/
        # logistics-rover-a` (persistent.sfs 364 KB, 33 sidecar files),
        # harvested from a scratch COPY with `--target-name
        # rover-route-recorded --expect-situation PRELAUNCH --keep-parsek`,
        # then finished by `harness/tools/build_rover_route_recorded.py`.
        # The `duna-one-recorded` / `depot-route-recorded` provenance class.
        #
        # THE COLLECTED-LOG RESTORE. `collect-logs.py` moves the save's
        # `Parsek/` to a SIBLING `parsek/` and leaves only `Parsek/Recordings`
        # behind, so the six `Parsek/GameState` files were restored into the
        # scratch copy BEFORE the harvest. The builder asserts all six by name;
        # a missed restore reds there rather than shipping a thinner fixture.
        #
        # WHY IT EXISTS, AND IT IS A DEBT H39 AND H40 BOTH NAME. Every
        # `ROUTE_CONNECTION_WINDOWS` node in the committed corpus before this
        # one is INITIATOR-branch (`transferTargetPid` equals the carrying
        # recording's `vesselPersistentId`), because `bdock-recorded` and
        # `depot-route-recorded` both dock Kerbal X descendants that share ONE
        # BAKED `persistentId`. That is why
        # `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` and
        # `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` appear in both
        # lanes' MEASURED_SKIPPED rosters as "a HARVEST requirement: a recorded
        # dock between craft with DIFFERENT baked pids". This is that harvest -
        # two rovers, pids 313889796 and 2123618197 - so the window is
        # TARGET-branch and both cells find a subject.
        #
        # THE ROUTE WINDOW - the one thing this fixture exists for, and the one
        # thing THIS MAP DOES NOT PIN, because `saveparse.py` has no
        # route-window facet (nor a `routes` one). It is pinned BUILDER-side in
        # `build_rover_route_recorded.py::verify_route_windows`, wired into the
        # suite by `RoverRouteRecordedFixtureDriftTests`:
        #   carrier              f2fb77ea... (tree 73e50f1e "B", treeOrder 1),
        #                        vesselPersistentId 313889796
        #   windowId             dock-513.539999999823-target-2123618197
        #   dockUT / undockUT    513.539999999823 / 594.27999999974952
        #   transferTargetPid    2123618197 -> carried by 3582d724... in the
        #                        OTHER tree, which is the cross-tree conjunct
        #   transferKind         DockingPort
        #   endpointSituation    1 (LANDED) - THE SUITE'S FIRST SURFACE ROUTE
        #                        ENDPOINT; both other route fixtures are orbital
        #   ENDPOINT_AT_DOCK     Kerbin, lat 0.0055209707591019428,
        #                        lon -74.726196706906393, alt 65.978650289936922,
        #                        isSurface True
        #   measured transfer    LiquidFuel 97.6 (transport 200 -> 102.4,
        #                        endpoint 200 -> 297.6) plus
        #                        DeployedCentralStation x1 / evaChute x1 /
        #                        evaScienceKit x1
        #
        # THERE IS NO `ROUTES` NODE, AND THAT IS THE POINT - the mirror image of
        # `depot-route-recorded`, so the two are not interchangeable. The
        # operator created the route AFTER this save was written, which is
        # exactly what gives RVR-2's `RouteCommand action=create` something to
        # do. What the save DOES carry is `PROMPTED_ROUTE_CANDIDATES { treeId =
        # 73e50f1e... }`, Parsek's own record that it found the transport tree
        # route-eligible. There is no `ROUTE_ORIGIN_PROOF` node either: both
        # trees launch from the Runway and the producer skips proof for a KSC
        # site by design.
        #
        # NOTHING IS SEALED BECAUSE EVERYTHING ALREADY IS. No RECORDING node
        # carries a `mergeState` key, which is how the codec spells the default
        # `MergeState.Immutable`, so `RouteCandidateFinder.IsTreeFullySealed` is
        # already true for BOTH trees and RVR-2's `SealSlot tree=...` is expected
        # to answer `alreadySealed=True remaining=0`. The builder pins that
        # absence; without the pin the no-op guard would be untestable.
        #
        # THE ACTIVE VESSEL WAS RE-POINTED, the first of the two edits to the
        # save body (the second is the endpoint inventory repair below). The
        # source's `activeVessel = 10` is `rover fuel 0`, PRELAUNCH on the
        # Runway; the builder re-points to index 7 = `B` (pid 313889796, Rover,
        # LANDED), the transport rover and route origin, re-resolving the index
        # by name + pid. A PRELAUNCH-focused boot is the fresh-rollout shape
        # `RecordingStore.SceneEntryFreshRolloutVesselPid` has a fast path for,
        # which is not a posture a committed-tree lane should open in. Neither
        # RVR-1 cell reads `FlightGlobals.ActiveVessel` (both walk
        # `CommittedTrees`), so the choice is free for them.
        #
        # THE ENDPOINT INVENTORY WAS REPAIRED (2026-09-01, after RVR-2 flight 1),
        # the second edit to the save body and the only one NO NUMBER IN THIS MAP
        # MOVES FOR: it lives inside a FLIGHTSTATE VESSEL node, so trees,
        # recordings, terminal states, branch points, sidecars, schema generation
        # and pointCount are all unchanged, and the pin lives builder-side in
        # `verify_endpoint_inventory` instead. What moved: two `STOREDPART` nodes
        # a ROUTE DELIVERY had placed into `rover fuel 0` were stripped. The
        # operator hand-created route `fd6ee2ff` over these same trees and drove
        # one Send Once at UT 750.06 BEFORE the save was written, and its own log
        # lines name the two slots verbatim (`part7/mod1/slot1` evaChute,
        # `part7/mod1/slot2` evaScienceKit) - so the harvested endpoint was the
        # physical state PLUS one already-run delivery, with no free slot left.
        # RVR-2 flight 1 measured the consequence exactly: the whole driven chain
        # executed and cycle 0 answered `BLOCKED kind=DestinationFull
        # reason=stored-part:evaScienceKit` instead of delivering. The second
        # container (`part8/mod1`) is untouched and is pinned as such. The
        # LiquidFuel is deliberately NOT reverted (297.6 is post-delivery too);
        # keeping it is what makes RVR-2's cycle-1-fits / cycle-2-blocks chain
        # reachable in TWO driven cycles, and the builder header records the
        # asymmetry as a decision rather than an oversight.
        #
        # NO `.prec` IS REPAIRED, and that is a MEASUREMENT. The analyzer Forbid
        # gate was run on the harvested bytes BEFORE anything else and read
        # `FAIL=0 WARN=0 INFO=0 STALE=0 BASELINED=0 RED=0` - clean on the first
        # pass, unlike `depot-route-recorded`, which needed a two-section INV2
        # containment dedupe.
        #
        # OTHER MEASURED BYTES:
        #   save clock (FLIGHTSTATE UT)  979.47999999939918, Mode SANDBOX,
        #     11 VESSEL nodes of which 3 are real (`B` 313889796 Rover LANDED,
        #     `A` 2875537755 Rover LANDED, `rover fuel 0` 2123618197 Probe
        #     PRELAUNCH) and 8 are stock asteroids kept verbatim.
        #   THE PID COLLISION IS REAL AND IS LOAD-BEARING: `rover fuel 0` carries
        #     the SAME baked persistentId 2123618197 as the recorded destination
        #     rover in tree 6a2d7247, with conclusively different `pid` guids
        #     (836ca8fa... live vs 0c322ddb... recorded) - the craft-baked-pid
        #     trap, here as a fixture property. `RouteEndpointResolver` resolves
        #     by `FlightGlobals.FindVessel(pid)` with no guid gate and no loaded
        #     gate, so a driven route's STOP resolves to `rover fuel 0`, ~568 m
        #     from the focus (inside stock's landed LOAD distance, outside its
        #     350 m PACK distance; the earlier `5.4 km` reading was wrong, see
        #     `build_rover_route_recorded.py`) and therefore PACKED -
        #     `path=unloaded`, which IS
        #     a delivering path (`LiveDeliveryWriters.WriteResourceUnloaded`
        #     writes `ProtoPartResourceSnapshot.amount`).
        #   `terminalStates` SUMS TO 4, NOT 5: the dock member f2fb77ea carries
        #     no `terminalState` (it is a mid-tree merged child).
        #   `branchPoints` is the suite's second `Dock`/`Undock` pair and carries
        #     NO `JointBreak` and no `Launch` - two rovers, one dock, one undock,
        #     nothing shed.
        #   `minAuthoritativeSidecars` is 19 = 5 x .prec + 5 x .pann +
        #     4 x _vessel.craft + 5 x _ghost.craft; `cf8d06fc` carries no
        #     `_vessel.craft`.
        #   pointCount total 426 over the 5 recordings (largest 116, smallest 32).
        #
        # WHAT THE FIXTURE DOES NOT CARRY: `Parsek/Saves` (three `parsek_rw_*`
        # plus a `parsek_career_start`, pruned by the harvest with the one
        # `rewindSave` hint that referenced them cleared), `Ships/` (the
        # collected save carried none, and this is a RECORDED subject that
        # launches nothing), and the two `.craft.txt` snapshot mirrors.
        # THE INTER-BODY SUPPLY-ROUTE HOST, and the only save in the corpus
        # whose route runs between two bodies. Roadmap gap G10 (B32 / V26M /
        # V26T) needs it because every route mechanism that is route-SPECIFIC in
        # the render engages only on that shape: `ClassifyRouteScope = InterBody`
        # had never been read live, and `FilterLegsToEndpointBodies` - the
        # ratified transfer-leg DROP - had never dropped a leg on a driven run.
        # On the SameBody host (`depot-route-recorded`, V18T) both clauses are
        # satisfied BY SCOPE and confirm nothing.
        #
        # PROVENANCE. The operator's own hand-played SANDBOX campaign
        # `Kerbal Space Program/saves/orbital supply route`, harvested READ-ONLY
        # from a scratch COPY on 2026-09-02 via
        # `harvest_bdock_station.py --keep-parsek --expect-situation ORBITING`,
        # then finished by `harness/tools/build_interbody_route_recorded.py`
        # (drops the `Ships/` the harvester keeps, restores the 618-byte AddOns
        # donor from `depot-route-recorded`). The harvest pruned `Parsek/Saves`,
        # cleared four dangling `rewindSave` hints and dropped 86 ORPHAN sidecars
        # (306 -> 220 files). The operator's save was never written to. Two
        # siblings were inspected and rejected in the same pass: `orbital supply
        # route CLEAN` carries ZERO ROUTE nodes, and `orbital supply route
        # DELIVERY test` carries one `Route: Kerbin -> Kerbin` at
        # completedCycles = 1 (a SameBody duplicate of V18T's subject).
        #
        # WHY IT IS THE SUBJECT, read off the bytes against the roadmap's own
        # 8-step specification: a Duna-orbit depot placed first (`Depot Station
        # Duna I`, two Duna-start recordings), a KSC-pad transport (`Duna Supply
        # 1`, isKscOrigin = True), a positive DELIVERY_MANIFEST (LiquidFuel
        # 257.83 / Oxidizer 315.13), a dock/undock PAIR at
        # dockUT = 72353218.8197432 / undockUT = 72353267.2397331 with
        # transferKind = DockingPort, a SEALED tree (zero `mergeState` lines in
        # the whole save, and the codec writes that key only when the state is
        # not Immutable), status Active at completedCycles = 0, and nothing
        # deleted afterwards (45 recordings, 23 Destroyed terminals = the ascent
        # debris still present). The route's scope inputs are ORIGIN bodyName
        # Kerbin against STOP ENDPOINT bodyName Duna.
        #
        # TWO ROUTES, DELIBERATELY. The save also carries `Route: KSC -> Mun`,
        # Paused, origin Kerbin / stop Mun - a SECOND inter-body route under the
        # endpoint scope rule. That is why `count` is 2 and `statuses` reads
        # {Active: 1, Paused: 1}: cutting it would have meant hand-editing the
        # operator's ParsekScenario, a worse trade than a richer census. A lane
        # over this host must expect two committed routes.
        #
        # BOTH routes carry `dispatchWindowPeriod = 0`, which is the whole point:
        # under the retired period-as-scope-flag contract both classified
        # MalformedMixedBodies and drew no line at all
        # (todo ROUTE-INTERBODY-SCOPE-NEVER-REACHABLE, fixed 2026-09-02). The
        # line stays on the wire by design - the fix moved the authority, not the
        # schema - and `test_build_interbody_route_recorded` pins that it is
        # still there.
        #
        # REPAIRED AT BUILD TIME, the `duna-one-recorded` / `depot-route-recorded`
        # precedent, and the reading is the argument. The 2026-09-02 B32 / V26M /
        # V26T runs all classified `PARSEK-FAIL(analyzer)` on
        # `INV2-NO-DOUBLE-COVER`; the fixture read `FAIL=3 WARN=1 RED=1` under the
        # Forbid gate. `build_interbody_route_recorded.py` now runs the SHARED
        # containment dedupe (imported from `build_duna_one_recorded`, third
        # consumer, no copy) over FOUR recordings and drops TWELVE sections -
        # seven 65-byte frame-less shells and three 170-byte re-clips of a conic
        # the kept envelope already carries, plus an exact-span duplicate pair.
        # The predicate is CONTAINMENT, so the coverage union cannot move, and
        # `repair_prec` refuses to write if it does or if a PARTIAL overlap would
        # be left. Reading after: `FAIL=0 WARN=1 INFO=0 STALE=0 BASELINED=0
        # RED=0`. NONE of the counts pinned below moved - the drops are sidecar
        # TrackSections, not recordings, files or `.sfs` structure - which is why
        # this block is a provenance note rather than a re-derivation.
        #
        # A BASELINE WAS NOT AN OPTION and the reason is structural: `run.py`
        # hard-codes `-FreshSaveGate` on every produced-save analyzer run, which
        # sets `PARSEK_ANALYZER_BASELINE_MODE=forbid`, and in Forbid the PRESENCE
        # of `analysis/baseline.cfg` is itself a `BASELINE-FORBIDDEN` FAIL. The
        # producer defect behind the residue is filed as
        # INTERBODY-SAVE-CARRIES-INV2-DOUBLE-COVER; it is not repaired away.
        #
        # `recordingIds` is the four [root..undock] MEMBERS of the inter-body
        # route rather than all 45: those are the ones the route resolves and a
        # route-line build walks, so a sidecar loss THERE is the loss that breaks
        # the lane. Their bodies are the scope inputs seen from the member side:
        # Kerbin, <transfer - no startBodyName>, Duna, Duna.
        "interbody-route-recorded": {
            "trees": 4, "committedTrees": 4, "recordings": 45,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Destroyed": 23, "Docked": 4, "Orbiting": 11,
                               "SubOrbital": 2},
            "branchPoints": {"Dock": 2, "JointBreak": 20, "Launch": 2,
                             "Undock": 2, "VesselSwitchContinuation": 1},
            "minAuthoritativeSidecars": 175,
            "recordingIds": ["d23e453bc982482b850ce717ba83bffd",
                             "5ca48c99fa55435e8cf8547a6ef27a39",
                             "3700f40e66c84ff79ce5197b362cf937",
                             "caa6190c37f74e928bfcdc8652ef3910"],
            "schemaGeneration": 4,
            "routes": {
                "count": 2, "dormant": 0, "stops": 2, "sourceRefs": 8,
                "completedCycles": 0, "skippedCycles": 0,
                "codecRejects": 0, "unparsed": 0, "unknownStatuses": 0,
                "unknownConnectionKinds": 0,
                "statuses": {"Active": 1, "Paused": 1},
                "connectionKinds": {"DockingPort": 2},
                "originBodies": {"Kerbin": 2},
                "destinationBodies": {"Duna": 1, "Mun": 1},
                "holdKinds": {},
                "ids": ["8f644e71b1164df3bb735330127d2ee7",
                        "71a983a16dc04d78bc2a2b90f1d184b0"],
                "destinationVesselPids": ["4277041026", "1413036399"],
                "dismissedCandidates": 2, "promptedCandidates": 0,
            },
        },
        "rover-route-recorded": {
            "trees": 2, "committedTrees": 2, "recordings": 5,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 3, "Docked": 1},
            "branchPoints": {"Dock": 1, "Undock": 1},
            "minAuthoritativeSidecars": 19,
            "recordingIds": ["0996f1ba7c7b4d3a8d95cf8be77fbe6d",
                             "3582d724892245c8939f6a354baff278",
                             "4370a799d00644f68d9b4a2ca9f72d0c",
                             "cf8d06fc7bf74e1a82bc70fc79290847",
                             "f2fb77ea5af34870bc08f5a0e9f0d78f"],
            "schemaGeneration": 4,
            # NO ROUTE, and that ABSENCE is the fixture's contract: it is the
            # route CANDIDATE host, so `RouteCommand action=create` has
            # something to do. `promptedCandidates` 1 is Parsek's own record
            # that it found the dock-merged tree route-ELIGIBLE - the closest
            # the bytes come to RVR-2's create precondition, and the surface
            # `build_rover_route_recorded.py::verify_save` now asserts through
            # this same parser.
            "routes": {
                "count": 0, "dormant": 0, "stops": 0, "sourceRefs": 0,
                "completedCycles": 0, "skippedCycles": 0,
                "codecRejects": 0, "unparsed": 0, "unknownStatuses": 0,
                "unknownConnectionKinds": 0,
                "statuses": {}, "connectionKinds": {},
                "originBodies": {}, "destinationBodies": {}, "holdKinds": {},
                "ids": [], "destinationVesselPids": [],
                "dismissedCandidates": 0, "promptedCandidates": 1,
            },
        },
        # --- THE SAME PAYLOAD, STAMPED INTO CAREER -----------------------
        # PROVENANCE: NOT A HARVEST. `rover-route-career` is built BY
        # CONSTRUCTION from two COMMITTED inputs by
        # `harness/tools/build_rover_route_career.py`: the save above supplies
        # the Parsek payload and the world, and `fresh-career` supplies the
        # seven career SCENARIO nodes (Funding / ResearchAndDevelopment /
        # Reputation / ScenarioUpgradeableFacilities / StrategySystem /
        # ScenarioContractEvents / ContractSystem), lifted VERBATIM. GAME `Mode`
        # flips SANDBOX -> CAREER, `Title` takes this fixture's own leaf, and
        # SANDBOX-only `ScenarioNewGameIntro` is dropped. That is the entire
        # diff. It is the `build_strategy_career.py` precedent applied in the
        # other direction, and because BOTH inputs are committed the drift cell
        # can re-run the build and assert byte-identity - which
        # `RoverRouteRecordedFixtureDriftTests` cannot do, its input being a
        # collected operator save outside the repo.
        #
        # IT BELONGS IN THIS MAP RATHER THAN IN `EXPECTED_SCENARIO_PRESENCE`
        # for the ordinary reason: it carries the recorded payload, so the
        # zero-trees contract that map asserts is exactly what it breaks.
        #
        # WHY THE ROW BELOW IS IDENTICAL TO ITS SIBLING'S, LINE FOR LINE, AND
        # WHY THAT IS THE POINT RATHER THAN A COPY-PASTE. The career stamp
        # touches GAME-level VALUES and GAME-level SCENARIO SIBLINGS only; the
        # `ParsekScenario` node and the whole `FLIGHTSTATE` are asserted
        # BYTE-IDENTICAL to the sandbox save's by
        # `build_rover_route_career.verify_payload_unchanged` /
        # `verify_flightstate_unchanged`, and every sidecar is compared as bytes
        # by `test_the_sidecar_tree_is_a_verbatim_copy`. So every facet this map
        # reads is READ OUT OF THE SAME BYTES. Two rows that must agree, checked
        # independently, is what makes a stamp that quietly touched a RECORDING
        # node red HERE as well as there.
        #
        # WHAT IT IS FOR: `RVR-4-rover-route-career-cost`, the roadmap's Tier C
        # item 9 (Costed dispatch). `env.IsCareer && route.IsKscOrigin` is the
        # gate on all three career-only surfaces -
        # `RouteDispatchEvaluator.CheckEligibility` step 7 (`KscFundsAvailable`),
        # `EmitDispatchDebit`'s cost computation, and `ApplyDeliveryFromPlan`
        # step 7's live `Funding.Instance.AddFunds(-cost)` - and none of them is
        # reachable in sandbox, so no committed lane has ever touched them. The
        # lane is RVR-2's driver over these bytes with `env.IsCareer` the only
        # variable moved, which makes RVR-2's green sandbox run its control.
        #
        # THE ONE NUMBER A READER WILL WANT, and it is NOT in this map because
        # nothing in `saveparse.py` can read it: the seeded `Funding funds` is
        # 11000, solved so the seed ALONE affords exactly one dispatch and not
        # two (band [7488.08, 14663.84), derived from the committed snapshot
        # bytes plus the automation instance's own part-cost database). The
        # derived dispatch cost is 7410. `RoverRouteCareerSeedBandTests` in
        # `harness/lib/test_build_rover_route_career.py` re-derives both bounds;
        # the builder's header carries the whole derivation, including why the
        # funds-short hold is NOT reachable on this subject (the committed
        # `ledger.pgld` pays 18,200 in milestone awards on top of any seed, and
        # only a delivering cycle charges).
        "rover-route-career": {
            "trees": 2, "committedTrees": 2, "recordings": 5,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 3, "Docked": 1},
            "branchPoints": {"Dock": 1, "Undock": 1},
            "minAuthoritativeSidecars": 19,
            "recordingIds": ["0996f1ba7c7b4d3a8d95cf8be77fbe6d",
                             "3582d724892245c8939f6a354baff278",
                             "4370a799d00644f68d9b4a2ca9f72d0c",
                             "cf8d06fc7bf74e1a82bc70fc79290847",
                             "f2fb77ea5af34870bc08f5a0e9f0d78f"],
            "schemaGeneration": 4,
            # NO ROUTE, and that ABSENCE is the fixture's contract: it is the
            # route CANDIDATE host, so `RouteCommand action=create` has
            # something to do. `promptedCandidates` 1 is Parsek's own record
            # that it found the dock-merged tree route-ELIGIBLE - the closest
            # the bytes come to RVR-2's create precondition, and the surface
            # `build_rover_route_recorded.py::verify_save` now asserts through
            # this same parser.
            "routes": {
                "count": 0, "dormant": 0, "stops": 0, "sourceRefs": 0,
                "completedCycles": 0, "skippedCycles": 0,
                "codecRejects": 0, "unparsed": 0, "unknownStatuses": 0,
                "unknownConnectionKinds": 0,
                "statuses": {}, "connectionKinds": {},
                "originBodies": {}, "destinationBodies": {}, "holdKinds": {},
                "ids": [], "destinationVesselPids": [],
                "dismissedCandidates": 0, "promptedCandidates": 1,
            },
        },
        # --- THE UNTYPED-DEPOT RELAY HOST (RVR-5 / RVR-6) ----------------
        # PROVENANCE: the operator's own hand-flown SANDBOX save
        # `logistics-rover-B`, flown 2026-09-02 and collected into the umbrella
        # `logs/2026-09-02_2041/`. Harvested from a scratch COPY with
        # `--keep-parsek --expect-situation ORBITING` and finished by
        # `harness/tools/build_rover_relay_recorded.py`. The
        # `duna-one-recorded` / `depot-route-recorded` / `rover-route-recorded`
        # provenance class.
        #
        # WHAT IT IS: three identical 16-part rovers A, B and C on the KSC
        # shore, all LANDED, each with ONE ModuleCommand and ONE dockingPort2
        # and no grapple. C drove to B, docked at UT 218.22, loaded +200
        # LiquidFuel (B 200 -> 0), undocked at UT 276.00, drove ~780 m to A,
        # docked at UT 340.12, unloaded 126.8 LiquidFuel (A 200 -> 326.8),
        # undocked at UT 402.50 and drove away. Saved at UT 443.64 with C's
        # recording stopped.
        #
        # WHY IT EXISTS, AND WHY IT IS NOT INTERCHANGEABLE WITH
        # `rover-route-recorded`: it is the first committed save that carries a
        # COMPLETE, BALANCED two-hop relay and STILL produces no route. That is
        # what `routes` reads below - EVERY count zero, including
        # `promptedCandidates`, where the sibling carries 1. Two INDEPENDENT
        # fail-closed reasons, both measured on the source flight's KSP.log:
        #   (1) NO ORIGIN PROOF - `RouteOriginProof skipped: no depot half ...
        #       seams=2 candidates=0 ... (neither docked half is typed Base or
        #       Station ...)`, once per dock (log lines 20911 / 24463). All
        #       three rovers are `vesselType = Rover`. The standing todo entry
        #       ROUTE-ORIGIN-PROOF-REQUIRES-A-PLAYER-TYPED-DEPOT; these are the
        #       first committed bytes that hold its output. NOTE this is a
        #       DIFFERENT zero from the sibling's, whose producer skips because
        #       both trees start at a KSC site.
        #   (2) `RouteAnalysisStatus.MixedPickupDelivery` - an UNWITNESSED
        #       INVENTORY GAIN, measured over the relay tree in exactly this
        #       shape (log line 28049: `mixedPickup=1`), AND AN OPEN PRODUCT
        #       DEFECT rather than a designed refusal. While docked at hop 1 the
        #       player moved the SAME `DeployedCentralStation` from B to C and it
        #       RE-HASHED in transit: stock's `StoreCargoPartAtSlot(Part, int)`
        #       rebuilds a live `ProtoPartSnapshot`, so
        #       `ModuleGroundExpControl.OnSave` adds a runtime-computed `canComm`
        #       value the craft-authored `STOREDPART` never had, and
        #       `ComputeInventoryPayloadIdentityHash` hashes module values by
        #       design. So the arriving item's identityHash (5bcde9ad...) is not
        #       the one the endpoint gave up (5072997a...) and
        #       `HasUnwitnessedInventoryGain` fails the window closed. Filed as
        #       LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE
        #       (OPEN). The `evaChute` and `evaScienceKit` moved in the same
        #       window closed cleanly - their modules write nothing computed.
        #       THESE BYTES ARE THAT DEFECT'S ONLY COMMITTED SUBJECT, so a
        #       re-harvest that moved no inventory would retire it;
        #       `RoverRelayRecordedFixtureDriftTests` pins the gain/loss walk so
        #       that reds in `harness/lib`. CORRECTED 2026-09-03: this used to add
        #       "and RVR-5 is its regression instrument", which is no longer true.
        #       The defect is FIXED (PR #1620, cargo matched BY KIND) and RVR-5 is
        #       now the ADMISSION lane over these bytes - LIVE-PROVEN
        #       `2026-09-02_2244`, PASS attempt 1, one route with `stops=2`. The
        #       two hashes below are kept as the only committed record of the
        #       re-hash, and they are now inert.
        #
        # OTHER MEASURED BYTES:
        #   save clock (FLIGHTSTATE UT) 443.63999999988647, Mode SANDBOX,
        #     6 VESSEL nodes of which 3 are real (`rover C` 1461186781 Rover
        #     LANDED - the active vessel after the builder's re-point - plus
        #     `rover B` 35783242 and `rover A` 1625259141, all LANDED Rovers of
        #     16 parts) and 3 are stock asteroids kept verbatim.
        #   THE ACTIVE VESSEL IS A BUILDER EDIT. The source was saved from the
        #     SPACE CENTER, so KSP left `activeVessel = 0` pointing at
        #     `Ast. UYX-230`; that save BOOTS (IsLoadedGameFocusable accepts it)
        #     straight into solar orbit with every rover unloaded. Step 1 of the
        #     builder re-points to index 1.
        #   THE PID SPLIT ACROSS THE UNDOCKS is the craft-baked-pid trap in the
        #     OTHER direction from the sibling's: the two route windows name
        #     target pids 2123618197 and 831319732 (the ORIGIN recordings'), but
        #     the LIVE rovers B and A carry 35783242 and 1625259141 because
        #     `Part.Undock` re-pids the separated half.
        #     CORRECTED 2026-09-03: this used to conclude that
        #     `RouteEndpointResolver.TryResolveEndpoint` "would find no live
        #     endpoint at all", and named that a third reason the fixture cannot
        #     host a delivery. STRUCK. The resolver walks RootPart -> Pid ->
        #     SurfaceProximity, and the proximity step is bounded by
        #     `SurfaceProximityRadiusMeters = 500` against the window's own
        #     recorded `ENDPOINT_AT_DOCK` - which these landed rovers are within
        #     metres of. Only the PID step misses.
        #   TWO route windows, BOTH TARGET-branch, each with its target pid
        #     carried by a recording in a DIFFERENT committed tree. The sibling
        #     holds that property once; this fixture holds it twice, which is
        #     why RVR-6 pins the two `RouteProof_*` cell tokens.
        #   `terminalStates` SUMS TO 7, NOT 9: the two dock members e175776c and
        #     e6cb44a7 carry no `terminalState` (they are mid-tree merged
        #     children).
        #   `branchPoints` is the suite's first Dock 2 / Undock 2 ALTERNATION -
        #     two hops rather than one supply run - and carries no `JointBreak`
        #     and no `Launch`.
        #   `minAuthoritativeSidecars` is 35 = 9 x .prec + 9 x .pann +
        #     8 x _vessel.craft + 9 x _ghost.craft; `31e843024f` carries no
        #     `_vessel.craft`.
        #   pointCount total 811 over the 9 recordings (largest 153, smallest
        #     26).
        #   The three rovers sit 336 m (A-C), 783 m (A-B) and 983 m (B-C) apart:
        #     far outside the ~200 m dock range, well inside physics range.
        #
        # WHAT THE FIXTURE DOES NOT CARRY: `Parsek/Saves` (FIVE `parsek_rw_*`
        # plus a `parsek_career_start`, pruned by the harvest with BOTH
        # `rewindSave` hints cleared - both payloads existed in the source, so
        # the prune is what would have made the hints dangle), `Ships/` (the
        # collected save carried none, and this is a
        # RECORDED subject that launches nothing), and the two `.craft.txt`
        # snapshot mirrors.
        "rover-relay-recorded": {
            "trees": 3, "committedTrees": 3, "recordings": 9,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 5, "Docked": 2},
            "branchPoints": {"Dock": 2, "Undock": 2},
            "minAuthoritativeSidecars": 35,
            "recordingIds": ["073a1ed6fdbc411da694dfcc59bdbc9f",
                             "0f391265a0b2453ea94fccd5daa1febb",
                             "31e843024f3347dfafc030f8d64796be",
                             "49eaec92876041efa53deb1f5e5c96f4",
                             "5f76d136e3dc4316bff71f4cfb0688a4",
                             "9511fa11878e413d9e4ea1861afae034",
                             "e175776c7c614e0a893a15f5bf84ff2c",
                             "e6cb44a7243d4377a5c6051c91636c0b",
                             "ff014f588ed640aaa8e48fbabc8a1c38"],
            "schemaGeneration": 4,
            # EVERY COUNT ZERO, INCLUDING `promptedCandidates`, AND THAT IS
            # WHAT GIVES RVR-5's CREATE SOMETHING TO DO. These are the STAGED
            # bytes: the sibling carries `promptedCandidates` 1 (Parsek's own
            # record that it found the tree route-ELIGIBLE), where the DLL that
            # wrote these never offered the tree at all - the measured
            # `DeriveCandidates: ... candidates=0` from the other side. A prompted
            # row appearing after a re-harvest would contradict the fixture's own
            # evidence; a `ROUTES` node would make the create answer
            # `candidate-already-promoted`; a dismissed row would make it answer
            # `candidate-dismissed` while every other facet still read correct.
            # Both are asserted by
            # `build_rover_relay_recorded.py::verify_no_route_state` as well.
            # NOT TO BE CONFUSED WITH THE PRODUCED SAVE: RVR-5 flew green
            # (`2026-09-02_2244`) and its produced save reads `routes count=1
            # statuses={Paused:1} stops=2`, which is that lane's
            # `[expectations.routes]` block, not this one.
            "routes": {
                "count": 0, "dormant": 0, "stops": 0, "sourceRefs": 0,
                "completedCycles": 0, "skippedCycles": 0,
                "codecRejects": 0, "unparsed": 0, "unknownStatuses": 0,
                "unknownConnectionKinds": 0,
                "statuses": {}, "connectionKinds": {},
                "originBodies": {}, "destinationBodies": {}, "holdKinds": {},
                "ids": [], "destinationVesselPids": [],
                "dismissedCandidates": 0, "promptedCandidates": 0,
            },
        },
        # --- THE WRONG-PROOF RELAY HOST (RVR-7) --------------------------
        # PROVENANCE: the operator's SECOND hand-flown relay, the SANDBOX save
        # `logistics-rover-c`, flown 2026-09-02 and collected into the umbrella
        # `logs/2026-09-03_0026_rover-c/`. Harvested from a scratch COPY with
        # `--keep-parsek --expect-situation ORBITING` (never `--force`) and
        # finished by `harness/tools/build_rover_relay_c_recorded.py`. Same
        # provenance class as `rover-relay-recorded`.
        #
        # WHAT IT IS: three identical 16-part rovers named A, B and C on the KSC
        # shore, all LANDED, each with one `probeStackSmall`, two
        # `ConformalStorageUnit` containers of three slots and one `dockingPort2`.
        # C drove to B, docked at UT 155.82, loaded +154.4 LiquidFuel
        # (B 200 -> 45.6) plus three stored items, undocked at UT 212.54, drove to
        # A, docked at UT 274.18, unloaded 200 LiquidFuel (A 200 -> 400) plus four
        # items, undocked at UT 335.32 and drove off. Saved at UT 410.40 from the
        # SPACE CENTER.
        #
        # WHY IT EXISTS ALONGSIDE `rover-relay-recorded`, WHICH IT OTHERWISE
        # RESEMBLES: it is the only committed save in the corpus that carries
        # PERSISTED `ROUTE_ORIGIN_PROOF` NODES, and BOTH NAME THE WRONG ORIGIN.
        # Written by the 2026-09-02 undock binder (PR #1618) before the analysis
        # learned to derive the origin from the PICKUP WINDOW, and quoted verbatim
        # in the builder header from the source flight's own KSP.log:
        #   hop 1 (the PICKUP at B) bound `originName='C' originPid=612987736` -
        #     the TRANSPORT ITSELF - and put B's root part in the transport slot,
        #     the two halves exactly inverted;
        #   hop 2 (a pure DELIVERY, with no source at all) bound
        #     `originName='A' originPid=4280917262` - the DESTINATION.
        # Both record `pickup=Carried pickupValidated=0`. Every other route fixture
        # carries ZERO proof nodes, so these bytes are the ONLY subject on which an
        # analysis can be shown to IGNORE a bound proof. `saveparse.py` has no
        # origin-proof facet, so the two nodes are pinned builder-side by
        # `verify_wrong_origin_proofs` and wired in by
        # `RoverRelayCRecordedFixtureDriftTests`.
        #
        # OTHER MEASURED BYTES:
        #   save clock (FLIGHTSTATE UT) 410.3999999999167, Mode SANDBOX, 9 VESSEL
        #     nodes of which 3 are real (`C` 612987736 Probe LANDED - the active
        #     vessel after the builder's re-point - plus `B` 90564594 Rover and `A`
        #     4280917262 Probe, all LANDED, all 16 parts) and 6 are stock asteroids
        #     kept verbatim.
        #   THE ACTIVE VESSEL IS A BUILDER EDIT. The source was saved from the
        #     SPACE CENTER, so KSP left `activeVessel = 0` pointing at
        #     `Ast. RQL-681`; that save BOOTS (IsLoadedGameFocusable accepts it)
        #     straight into solar orbit with every rover unloaded. Step 1 of the
        #     builder re-points to index 5.
        #   THE HOP-1 IDENTITY SWAP. KSP resolved the hop-1 merged vessel to B's
        #     identity, so the dock member `39ac117a` carries B's pid AND B's
        #     `recordedVesselGuid`. Consequences: window 0 is INITIATOR-branch
        #     where BOTH of the sibling's windows are TARGET-branch (only window 1
        #     is TARGET-branch here), the origin binder inverted the halves at hop
        #     1, and the relay tree's ROOT `8604fbc7` carries no `_vessel.craft` -
        #     the same dock-merge-parent shape as the sibling's `31e84302`, but
        #     invisible to the guid correlator in `CommittedFixtureMirrorTests`,
        #     which grew a third exemption for it.
        #   THE PID SPLIT ACROSS THE UNDOCKS. Window 1 names target pid 2123618197
        #     (rover A as its own launch recorded it) while the LIVE A carries
        #     4280917262. Unlike the sibling that does NOT strand the endpoint:
        #     `RouteEndpointResolver` falls back to a great-circle proximity search
        #     bounded by `SurfaceProximityRadiusMeters = 500` and the live A sits
        #     ~9 m from the window's recorded `ENDPOINT_AT_DOCK`.
        #   THE FIXTURE IS STAGED AT START-OF-CYCLE, and this is a BUILDER EDIT to
        #     FLIGHTSTATE, not the harvested state. As harvested the endpoints were
        #     SPENT (B LiquidFuel 45.6 / 400 against its own window's 154.4 pickup
        #     manifest, A 400 / 400 with 6 of 6 inventory slots occupied), so both
        #     all-or-nothing eligibility gates were false and every driven cycle
        #     blocked emitting nothing. Builder step 3 restores each PHYSICAL
        #     endpoint to the state ITS OWN window recorded at ITS dock - B from
        #     window 0's `DOCK_ENDPOINT_*`, A from window 1's - so both now read
        #     LiquidFuel 200 / 400 with three of six slots occupied, with the
        #     STOREDPART bytes LIFTED VERBATIM from the window snapshots (inner
        #     `persistentId` included, which is how the placement is audited: rover
        #     A holds a station at slot 1 in BOTH containers and only the pid says
        #     which was the original). The transport C is UNTOUCHED at 154.4 / 400.
        #     Precedent: `build_rover_route_recorded.py` step 3, which strips the
        #     `STOREDPART` nodes a hand-driven Send Once had already delivered into
        #     ITS endpoint. NONE OF THE FACETS BELOW MOVES - the repair edits
        #     FLIGHTSTATE and every facet here reads ParsekScenario.
        #   `terminalStates` SUMS TO 7, NOT 10: the two dock members and the first
        #     segment of rover A's post-undock chain carry no `terminalState`.
        #   `branchPoints` is the same Dock 2 / Undock 2 ALTERNATION as the
        #     sibling's, and carries no `JointBreak` and no `Launch`.
        #   `minAuthoritativeSidecars` is 39 = 10 x .prec + 10 x .pann +
        #     9 x _vessel.craft + 10 x _ghost.craft.
        #   pointCount total 769 over the 10 recordings (largest 153, smallest 1).
        #   The three rovers sit 313 m (A-C), 731 m (A-B) and 1041 m (B-C) apart:
        #     far outside the ~200 m dock range, well inside physics range.
        #
        # WHAT THE FIXTURE DOES NOT CARRY: `Parsek/Saves` (FIVE `parsek_rw_*` plus
        # a `parsek_career_start`, pruned by the harvest with BOTH `rewindSave`
        # hints cleared), `Ships/` (the collected save carried none, and this is a
        # RECORDED subject that launches nothing), and the `.craft.txt` snapshot
        # mirrors.
        "rover-relay-c-recorded": {
            "trees": 3, "committedTrees": 3, "recordings": 10,
            "supersedes": 0, "tombstones": 0, "rewind_points": 0,
            "rewind_retirements": 0,
            "terminalStates": {"Landed": 5, "Docked": 2},
            "branchPoints": {"Dock": 2, "Undock": 2},
            "minAuthoritativeSidecars": 39,
            "recordingIds": ["2ce8804f5f5b4bfdb4e9483cf827c593",
                             "39ac117a8a8b4d61b1296983e7d538a8",
                             "4a31577192894f9ab7390db3f00bfc35",
                             "4a61a530e8784a2c9322f00d18ab422f",
                             "5c8476924adb4a1d8bf0215034b69e78",
                             "8604fbc77d54482eae83424b7e401954",
                             "9fed706a8b85498e9f20a06aa80c3464",
                             "a597f168e5d24e4f94f0803f80246832",
                             "b9df0ee00fd84831a0d9619b4e34fc97",
                             "ec4bf428ea0048adbeaede46aa2f6b49"],
            "schemaGeneration": 4,
            # EVERY ROUTE COUNT ZERO, exactly as the sibling. The DLL that wrote
            # these bytes is the one that bound the two wrong proofs and had not
            # yet learned to derive the origin from the pickup window, so it never
            # offered the relay tree as a candidate. A `promptedCandidates` row
            # appearing after a re-harvest would mean the save came from a
            # different build and the fixture is no longer the override's subject;
            # a `dismissedCandidates` row would make a create answer
            # `candidate-dismissed` while every other facet still read correct.
            # Both are asserted by
            # `build_rover_relay_c_recorded.py::verify_no_route_state` as well.
            # NOTE what this facet CANNOT see and what therefore lives
            # builder-side: the two `ROUTE_ORIGIN_PROOF` nodes themselves.
            "routes": {
                "count": 0, "dormant": 0, "stops": 0, "sourceRefs": 0,
                "completedCycles": 0, "skippedCycles": 0,
                "codecRejects": 0, "unparsed": 0, "unknownStatuses": 0,
                "unknownConnectionKinds": 0,
                "statuses": {}, "connectionKinds": {},
                "originBodies": {}, "destinationBodies": {}, "holdKinds": {},
                "ids": [], "destinationVesselPids": [],
                "dismissedCandidates": 0, "promptedCandidates": 0,
            },
        },
    }

    def test_fixture_set_is_exactly_the_committed_set(self):
        found = sorted(d for d in os.listdir(FIXTURE_SAVES_DIR)
                       if os.path.isdir(os.path.join(FIXTURE_SAVES_DIR, d)))
        expected = sorted(set(self.EXPECTED_SCENARIO_PRESENCE)
                          | set(self.RECORDED_FIXTURES))
        self.assertEqual(expected, found,
                         "committed fixture set changed - re-pin this sweep")

    def test_every_node_less_fixture_is_vessel_less(self):
        """KNOWN-GATE 6, AS A CELL RATHER THAN AS PROSE.

        A fixture with no `SCENARIO{name=ParsekScenario}` node is only safe if it is
        VESSEL-LESS. `TestCommandLoadGame.DecideLoadRoute` sends a save with a
        focusable vessel down `LoadGameImpl`'s FLIGHT branch, which calls
        `FlightDriver.StartAndFocusVessel` with NO `GamePersistence.
        UpdateScenarioModules` and no `SaveGame` - so the ParsekScenario MODULE is
        never instantiated, `ParsekScenario.OnLoad` never runs, and nothing Parsek
        does is reachable for the whole flight. The vessel-less saves are safe
        because they take the NoVesselSpaceCenter branch instead, where
        `UpdateScenarioModules` + `SaveGame(persistent, OVERWRITE)` run before
        `game.Start()` and KSP writes the node to disk itself.

        THIS COST A FLIGHT ONCE (CL-1 flight 1, 2026-07-28: the whole profile flew
        correctly and produced ZERO recordings), and the lesson has lived only as
        prose in three places since - `build_career_pad_craft.py`'s splice comment,
        the presence map above, and autotest-status known-gate 6. Prose does not
        red. This cell does, locally, before a fixture author spends a flight
        rediscovering it - which is exactly what the pre-Parsek backup lane's first
        cut of `preparsek-untouched-career` would have done, having deleted the node
        from the corpus's only focusable footprint-free save.
        """
        checked = 0
        for name, has_node in sorted(self.EXPECTED_SCENARIO_PRESENCE.items()):
            if has_node:
                continue
            checked += 1
            text = _read(os.path.join(FIXTURE_SAVES_DIR, name, "persistent.sfs"))
            lines = [l.strip() for l in text.splitlines()]
            with self.subTest(fixture=name):
                self.assertEqual(
                    0, lines.count("VESSEL"),
                    "%s carries no ParsekScenario node AND has VESSEL node(s), so it "
                    "routes to FLIGHT, where the module is never instantiated: "
                    "OnLoad never runs and Parsek is inert for the whole run. Either "
                    "splice an inert node (name + scene) or keep the save "
                    "vessel-less." % name)
                active = [l for l in lines if l.startswith("activeVessel = ")]
                for l in active:
                    self.assertEqual(
                        "activeVessel = -1", l,
                        "%s is node-less and names a focusable activeVessel" % name)
        self.assertGreater(checked, 0,
                           "no node-less fixture found - this gate is inert")

    def test_recorded_fixtures_carry_their_pinned_payload(self):
        for name, want in sorted(self.RECORDED_FIXTURES.items()):
            path = os.path.join(FIXTURE_SAVES_DIR, name, "persistent.sfs")
            snap = saveparse.parse_parsek_scenario(_read(path))
            obs = saveparse.observed_structure_facets(snap)
            structure = obs["recordings"]["structure"]
            with self.subTest(fixture=name):
                self.assertTrue(snap.parsed, "%s: %s" % (name, snap.error))
                self.assertTrue(snap.scenario_found, name)
                self.assertEqual(want["trees"], structure["trees"], name)
                self.assertEqual(want["committedTrees"],
                                 structure["committedTrees"], name)
                self.assertEqual(want["recordings"],
                                 structure["recordings"], name)
                self.assertEqual(want["terminalStates"],
                                 structure["terminalStates"], name)
                self.assertEqual(want["branchPoints"],
                                 structure["branchPoints"], name)
                self.assertEqual(want["supersedes"], len(snap.supersedes), name)
                self.assertEqual(want["tombstones"], len(snap.tombstones), name)
                self.assertEqual(want["rewind_points"],
                                 len(snap.rewind_points), name)
                self.assertEqual(want["rewind_retirements"],
                                 len(snap.rewind_retirements), name)
                # THE SUPPLY-ROUTE FACET. An entry WITHOUT a `routes` key
                # asserts the all-zero shape rather than skipping: a route
                # leaking into a non-route subject is exactly the drift a
                # per-fixture opt-in would hide, and every recorded fixture
                # bar three genuinely carries none.
                self.assertEqual(want.get("routes", NO_ROUTES_FACET),
                                 obs["routes"], name)
                # The sidecars ARE the payload: metadata pointing at nothing
                # is the failure --keep-parsek exists to prevent.
                #
                # Count only AUTHORITATIVE sidecars. Parsek writes a readable
                # `.txt` mirror beside each one, and this floor used to count
                # those too - which let derived data pad a payload floor, so a
                # fixture could lose real sidecars and still clear it on mirror
                # count alone. Mirrors are excluded here even though the
                # trajectory one (`.prec.txt`) is still committed.
                recordings_dir = os.path.join(FIXTURE_SAVES_DIR, name,
                                              "Parsek", "Recordings")
                self.assertTrue(os.path.isdir(recordings_dir), name)
                authoritative = [f for f in os.listdir(recordings_dir)
                                 if not f.endswith(".txt")]
                self.assertGreaterEqual(len(authoritative),
                                        want["minAuthoritativeSidecars"], name)
                for rid in want["recordingIds"]:
                    prec = os.path.join(recordings_dir, rid + ".prec")
                    self.assertTrue(os.path.isfile(prec),
                                    "%s: %s.prec missing" % (name, rid))
                    self.assertGreater(os.path.getsize(prec), 0,
                                       "%s: %s.prec is empty" % (name, rid))
                text = _read(path)
                gens = set(re.findall(
                    r"recordingSchemaGeneration = (\d+)", text))
                self.assertEqual({str(want["schemaGeneration"])}, gens,
                                 "%s: schema generations drifted" % name)
                store_cs = os.path.join(
                    os.path.dirname(os.path.abspath(__file__)), "..", "..",
                    "Source", "Parsek", "RecordingStore.cs")
                with open(store_cs, encoding="utf-8") as fh:
                    src = fh.read()
                m = re.search(
                    r"CurrentRecordingSchemaGeneration\s*=\s*(\d+)", src)
                self.assertIsNotNone(m, "schema-generation constant moved")
                self.assertEqual(
                    want["schemaGeneration"], int(m.group(1)),
                    "%s: RecordingStore.CurrentRecordingSchemaGeneration "
                    "no longer accepts this fixture's recordings - the "
                    "fixture must be re-harvested at the new generation "
                    "(or the consumer lane silently tests nothing)" % name)

    def test_every_persistent_sfs_parses_with_pinned_counts(self):
        for name, has_scenario in sorted(self.EXPECTED_SCENARIO_PRESENCE.items()):
            path = os.path.join(FIXTURE_SAVES_DIR, name, "persistent.sfs")
            snap = saveparse.parse_parsek_scenario(_read(path))
            with self.subTest(fixture=name):
                self.assertTrue(snap.parsed, "%s: %s" % (name, snap.error))
                self.assertEqual(has_scenario, snap.scenario_found, name)
                self.assertEqual(0, len(snap.trees), name)
                self.assertEqual(0, len(snap.supersedes), name)
                self.assertEqual(0, len(snap.tombstones), name)
                self.assertEqual(0, len(snap.rewind_points), name)
                self.assertEqual(0, len(snap.rewind_retirements), name)
                # A PAD / CRAFT fixture must carry NO route surface either. A
                # spliced-in inert ParsekScenario node has nothing to hold one,
                # so a non-zero here means the fixture was derived from a save
                # with route state and the derivation did not strip it.
                self.assertEqual(NO_ROUTES_FACET,
                                 saveparse.observed_routes_facets(snap), name)

    def test_no_fixture_commits_a_quicksave(self):
        # Was `test_quicksave_sidecars_parse_too`, which asserted that the
        # quicksave.sfs committed by b1-pad-craft / b2-lko-craft / gloops-airshow
        # parsed and held zero trees. That test was self-referential: it existed
        # because the files existed, and asserted nothing any scenario needs.
        #
        # The files were pre-convention harvest exhaust - each a near-copy of its
        # own fixture's persistent.sfs (differing by 73 / 467 / 41 lines), 23,931
        # lines over 6 files. Nothing reads them: no spec names one, no seam verb
        # quickloads, and every in-game TriggerQuickload call site quicksaves to a
        # NAMED test slot first, never the default "quicksave" slot.
        # `harvest_bdock_station.py` already prunes "the stale quicksave.*" when
        # normalizing a produced save (design-autotest-bdock-missions.md:186), so
        # every fixture forged after that convention carries none; these three were
        # the holdouts. This cell now pins the convention instead of the exhaust.
        offenders = []
        for name in sorted(os.listdir(FIXTURE_SAVES_DIR)):
            fixture = os.path.join(FIXTURE_SAVES_DIR, name)
            if not os.path.isdir(fixture):
                continue
            offenders.extend("%s/%s" % (name, f) for f in sorted(os.listdir(fixture))
                             if f.startswith("quicksave."))
        self.assertEqual([], offenders,
                         "committed quicksave.* is harvest exhaust nothing reads; "
                         "harvest_bdock_station.py prunes it")

    def test_observed_facets_for_a_fixture_are_all_zero(self):
        snap = saveparse.parse_parsek_scenario(
            _read(os.path.join(FIXTURE_SAVES_DIR, "gloops-airshow", "persistent.sfs")))
        obs = saveparse.observed_structure_facets(snap)
        self.assertEqual({"supersedeRows": 0, "tombstones": 0, "rewindPoints": 0,
                          "rewindRetirements": 0},
                         obs["rewind"])
        self.assertEqual(
            {"trees": 0, "committedTrees": 0, "recordings": 0,
             "terminalStates": {}, "branchPoints": {},
             "duplicateRecordingIds": []},
            obs["recordings"]["structure"])


class SpecSurfaceValidationTests(unittest.TestCase):
    """The [expectations.rewind] / [expectations.recordings.structure] spec
    surfaces: malformed windows are pre-launch rejections, never blocks that
    silently evaluate as no-ops."""

    def test_none_and_good_shapes_validate(self):
        self.assertEqual([], saveparse.validate_rewind_expectations(None))
        self.assertEqual([], saveparse.validate_rewind_expectations(
            {"supersedeRows": {"max": 0}, "tombstones": {"max": 0}}))
        self.assertEqual([], saveparse.validate_rewind_expectations(
            {"gating": True, "supersedeRows": 1, "rewindPoints": {"min": 1, "max": 2}}))
        self.assertEqual([], saveparse.validate_structure_expectations(
            {"trees": {"min": 1, "max": 1},
             "terminalStates": {"Destroyed": {"max": 0}},
             "branchPoints": {"VesselSwitchContinuation": 1}}))

    def test_s41_committed_block_shape_validates(self):
        # The exact block S4.1-rewind-merge declares today - it MUST keep
        # validating (verdict neutrality: the spec is untouched by M-C2).
        self.assertEqual([], saveparse.validate_rewind_expectations(
            {"supersedeRows": {"max": 0}, "tombstones": {"max": 0}}))

    def test_unknown_keys_rejected(self):
        errs = saveparse.validate_rewind_expectations({"supersedes": {"max": 0}})
        self.assertEqual(1, len(errs))
        self.assertIn("unknown key", errs[0])
        errs = saveparse.validate_structure_expectations({"branchpoints": {}})
        self.assertTrue(errs)

    def test_bad_windows_rejected(self):
        for bad in (-1, "x", True, {}, {"min": -1}, {"min": 2, "max": 1},
                    {"min": 1.5}, {"lo": 1}):
            with self.subTest(window=bad):
                self.assertTrue(saveparse.validate_rewind_expectations(
                    {"supersedeRows": bad}), bad)

    def test_bad_enum_names_rejected(self):
        errs = saveparse.validate_structure_expectations(
            {"terminalStates": {"Exploded": 1}})
        self.assertTrue(any("unknown name" in e for e in errs))
        errs = saveparse.validate_structure_expectations(
            {"branchPoints": {"Switch": {"max": 0}}})
        self.assertTrue(any("unknown name" in e for e in errs))

    def test_non_bool_gating_rejected(self):
        errs = saveparse.validate_rewind_expectations({"gating": "true"})
        self.assertTrue(any("gating" in e for e in errs))
        errs = saveparse.validate_structure_expectations({"gating": 1})
        self.assertTrue(any("gating" in e for e in errs))

    def test_armed_empty_block_rejected(self):
        # Adversarial-review finding 4: gating = true with zero assertion keys
        # is a gate the author believes is on and that can never red - the
        # exact failure _validate_window refuses one level down.
        errs = saveparse.validate_rewind_expectations({"gating": True})
        self.assertTrue(any("gates nothing" in e for e in errs))
        errs = saveparse.validate_structure_expectations({"gating": True})
        self.assertTrue(any("gates nothing" in e for e in errs))
        # gating = false with no assertions is NOT an error (warned instead).
        self.assertEqual([], saveparse.validate_rewind_expectations({"gating": False}))

    def test_declared_empty_block_warns(self):
        warns = saveparse.save_structure_expectation_warnings(
            {"rewind": {}, "recordings": {"structure": {"gating": False}}})
        self.assertEqual(2, len(warns))
        self.assertTrue(all("reports nothing and gates nothing" in w for w in warns))
        self.assertEqual([], saveparse.save_structure_expectation_warnings(
            {"rewind": {"supersedeRows": {"max": 0}}}))
        self.assertEqual([], saveparse.save_structure_expectation_warnings(None))

    def test_non_table_blocks_rejected(self):
        self.assertTrue(saveparse.validate_rewind_expectations("nope"))
        self.assertTrue(saveparse.validate_structure_expectations([1]))


class EvaluateSaveStructureTests(unittest.TestCase):
    """The verifier decision: REPORT-only by default (verdict neutrality),
    PASS/FAIL only under the opt-in gating key."""

    @classmethod
    def setUpClass(cls):
        cls.snap = saveparse.parse_parsek_scenario(B9_MERGED_SFS)

    def test_default_is_report_even_on_mismatch(self):
        # S4.1's committed shape (max = 0) against a save that HAS a supersede
        # row: mismatches recorded, status stays REPORT, verdict untouched.
        exp = {"rewind": {"supersedeRows": {"max": 0}, "tombstones": {"max": 0}}}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_REPORT, r.status)
        self.assertFalse(r.gating)
        self.assertEqual(2, len(r.mismatches))
        self.assertIn("rewind.supersedeRows 1 > max 0", r.mismatches)
        self.assertIn("rewind.tombstones 1 > max 0", r.mismatches)
        self.assertEqual(("rewind",), r.blocks)

    def test_gating_pass_and_fail(self):
        good = {"rewind": {"gating": True, "supersedeRows": 1, "tombstones": {"min": 1},
                           "rewindPoints": {"min": 1, "max": 1}}}
        r = saveparse.evaluate_save_structure(good, self.snap)
        self.assertEqual(saveparse.STATUS_PASS, r.status)
        self.assertTrue(r.gating)
        self.assertEqual((), r.mismatches)

        bad = {"rewind": {"gating": True, "supersedeRows": 2}}
        r = saveparse.evaluate_save_structure(bad, self.snap)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertEqual(("rewind.supersedeRows 1 != 2",), r.mismatches)

    def test_structure_block_windows_and_buckets(self):
        exp = {"recordings": {
            "count": {"min": 1},  # verifier-7 key, ignored here
            "structure": {
                "trees": 1, "committedTrees": {"min": 1}, "recordings": {"min": 4, "max": 4},
                "terminalStates": {"Destroyed": 2, "Recovered": {"max": 0}},
                "branchPoints": {"Undock": 1, "VesselSwitchContinuation": {"min": 1}},
            }}}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual((), r.mismatches)
        self.assertEqual(("recordings.structure",), r.blocks)

        exp["recordings"]["structure"]["branchPoints"]["Breakup"] = {"min": 1}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(
            ("recordings.structure.branchPoints.Breakup 0 < min 1",), r.mismatches)

    def test_gating_in_either_block_arms(self):
        exp = {"recordings": {"structure": {"gating": True, "trees": 1}}}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertTrue(r.gating)
        self.assertEqual(saveparse.STATUS_PASS, r.status)
        self.assertTrue(saveparse.gating_armed(exp))
        self.assertFalse(saveparse.gating_armed(
            {"rewind": {"supersedeRows": {"max": 0}}}))
        self.assertFalse(saveparse.gating_armed(None))

    def test_missing_save_with_block_is_a_named_mismatch(self):
        exp = {"rewind": {"gating": True, "supersedeRows": {"max": 0}}}
        r = saveparse.evaluate_save_structure(exp, None)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertIn("save unreadable: missing persistent.sfs", r.mismatches[0])
        self.assertEqual({}, r.observed)

    def test_missing_save_without_block_is_empty_report(self):
        r = saveparse.evaluate_save_structure({}, None)
        self.assertEqual(saveparse.STATUS_REPORT, r.status)
        self.assertEqual((), r.mismatches)
        self.assertEqual((), r.blocks)
        self.assertEqual({}, r.observed)

    def test_observed_facets_ride_every_result(self):
        r = saveparse.evaluate_save_structure({}, self.snap)
        self.assertEqual(1, r.observed["rewind"]["supersedeRows"])
        self.assertEqual(saveparse.STATUS_REPORT, r.status)

    def test_scenarioless_save_with_a_block_is_a_named_mismatch(self):
        # Adversarial-review finding 2: ParsekScenario is AddToAllGames, so a
        # PRODUCED save without the node means Parsek never loaded (wrong DLL,
        # MM failure, load exception) - structurally indistinguishable from
        # "Parsek wrote zero rows" without this check. A declared block must
        # therefore raise a named mismatch, never a green all-zero read.
        snap = saveparse.parse_parsek_scenario("GAME\n{\n\tversion = 1\n}\n")
        r = saveparse.evaluate_save_structure(
            {"rewind": {"gating": True, "supersedeRows": {"max": 0}}}, snap)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertIn("no ParsekScenario node", r.mismatches[0])
        self.assertIs(False, r.scenario_found)
        # No block declared: same save degrades to an empty REPORT row (the
        # fresh-* templates legitimately lack the node).
        r = saveparse.evaluate_save_structure({}, snap)
        self.assertEqual(saveparse.STATUS_REPORT, r.status)
        self.assertEqual((), r.mismatches)

    def test_gating_is_per_block(self):
        # Adversarial-review finding 3: the key lives INSIDE each block, so it
        # must arm only that block. Arming the proven rewind block while the
        # exploratory structure block still mismatches -> PASS, with the
        # structure mismatch recorded report-only.
        exp = {
            "rewind": {"gating": True, "supersedeRows": 1, "tombstones": 1},
            "recordings": {"structure": {"trees": 99}},
        }
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_PASS, r.status)
        self.assertTrue(r.gating)
        self.assertEqual(("rewind",), r.armed_blocks)
        self.assertEqual(("recordings.structure.trees 1 != 99",), r.mismatches)
        self.assertEqual((), r.armed_mismatches)
        # Mirror image: structure armed, rewind mismatching report-only.
        exp = {
            "rewind": {"supersedeRows": 99},
            "recordings": {"structure": {"gating": True, "trees": 1}},
        }
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_PASS, r.status)
        self.assertEqual(("recordings.structure",), r.armed_blocks)
        self.assertEqual(("rewind.supersedeRows 1 != 99",), r.mismatches)
        # An armed block's own mismatch still FAILs.
        exp["recordings"]["structure"]["trees"] = 2
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertEqual(("recordings.structure.trees 1 != 2",), r.armed_mismatches)

    def test_structural_faults_gate_any_armed_block(self):
        # An unreadable save undermines EVERY declared assertion, so it must
        # gate whichever block is armed (attribution to all declared blocks).
        exp = {"rewind": {"supersedeRows": {"max": 0}},
               "recordings": {"structure": {"gating": True, "trees": 1}}}
        r = saveparse.evaluate_save_structure(exp, None)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertTrue(all("save unreadable" in m for m in r.armed_mismatches))
        # Round-2 NEW-2: attributed to both blocks internally, but the flat
        # report lists carry the string ONCE, not once per declared block.
        self.assertEqual(1, len(r.mismatches))
        self.assertEqual(1, len(r.armed_mismatches))


class PointsFacetTests(unittest.TestCase):
    """Gate 12: the recorded-POINTS distribution parsed off `pointCount`.

    `RecordingTreeRecordCodec` (`SaveRecordingInto` ->
    `SaveRecordingResourceAndState` -> `SaveMutablePlaybackState`, the write at
    `RecordingTreeRecordCodec.cs:344`) writes `pointCount` on EVERY
    RECORDING node as `rec.Points.Count`, so the save itself carries the number
    the `FinalizeTreeRecordings: ... points=N` Verbose line reports. Reading it
    here (not from the log) is what makes the assertion independent of a
    scenario's `verboseLogging` pin.
    """

    @classmethod
    def setUpClass(cls):
        cls.snap = saveparse.parse_parsek_scenario(B9_MERGED_SFS)

    def test_point_count_parses_off_every_recording(self):
        counts = {r.recording_id: r.point_count for r in self.snap.recordings}
        self.assertEqual({"b9-stack-root": 5, "b9-upper-b": 3,
                          "b9-booster-a": 4, "b9-booster-refly": 9}, counts)

    def test_observed_order_statistics(self):
        f = saveparse.observed_points_facets(self.snap)
        self.assertEqual({"total": 21, "largest": 9, "smallest": 3,
                          "trivialRecordings": 0, "recordings": 4,
                          "unparsed": 0}, f)
        # ...and it rides the structure facets under recordings.points, so a
        # run JSON records it whether or not the spec declares a block.
        self.assertEqual(
            f, saveparse.observed_structure_facets(self.snap)["recordings"]["points"])

    def test_absent_point_count_is_unparsed_never_zero(self):
        # A RECORDING with no pointCount key must NOT contribute a real 0 to
        # the order statistics - that is the "never read a torn file as zero
        # rows" rule applied one level down. `smallest` stays 3 (the smallest
        # MEASURED count), and the gap is surfaced as `unparsed`.
        text = B9_MERGED_SFS.replace("\t\t\t\tpointCount = 3\n", "")
        snap = saveparse.parse_parsek_scenario(text)
        f = saveparse.observed_points_facets(snap)
        self.assertEqual({"total": 18, "largest": 9, "smallest": 4,
                          "trivialRecordings": 0, "recordings": 3,
                          "unparsed": 1}, f)
        self.assertIsNone(
            next(r for r in snap.recordings if r.recording_id == "b9-upper-b").point_count)

    def test_unparsed_is_a_defined_mismatch_for_a_declared_block(self):
        # The windows would otherwise be asserting over a distribution the save
        # did not fully supply. Report-only by default, gating when armed.
        text = B9_MERGED_SFS.replace("\t\t\t\tpointCount = 3\n", "")
        snap = saveparse.parse_parsek_scenario(text)
        exp = {"recordings": {"points": {"largest": {"min": 2}}}}
        r = saveparse.evaluate_save_structure(exp, snap)
        self.assertEqual(saveparse.STATUS_REPORT, r.status)
        self.assertEqual(1, len(r.mismatches))
        self.assertIn("no readable pointCount", r.mismatches[0])
        exp["recordings"]["points"]["gating"] = True
        self.assertEqual(saveparse.STATUS_FAIL,
                         saveparse.evaluate_save_structure(exp, snap).status)

    def test_a_negative_point_count_is_unparsed_not_a_real_count(self):
        # Same rule as an absent key: a value that is not a real count must not
        # deflate total/smallest (the fail-open direction for a `max` window).
        text = B9_MERGED_SFS.replace("pointCount = 3", "pointCount = -3")
        f = saveparse.observed_points_facets(saveparse.parse_parsek_scenario(text))
        self.assertEqual(1, f["unparsed"])
        self.assertEqual(18, f["total"])      # -3 excluded, not summed
        self.assertEqual(4, f["smallest"])    # -3 excluded, not the minimum

    def test_the_double_written_active_tree_is_deduped(self):
        # Production writes one tree TWICE: OnSave serializes the committed
        # trees, then SaveActiveTreeIfAny writes the still-active tree again as
        # `isActive = True`. Measured across the 149 real produced saves under
        # logs/: 5 carry duplicate recording ids, and 2026-07-28_1818_S4.1
        # reads total 24 where the truth is 12 - while 2026-07-28_1939, the SAME
        # spec, reads 12. Summed facets would be a run-to-run coin-flip 2x, and
        # a doubled TRIVIAL recording would false-red the load-bearing budget.
        one = saveparse.parse_parsek_scenario(B9_MERGED_SFS)
        base = saveparse.observed_points_facets(one)
        # Re-serialize the same tree a second time, marked active, exactly as
        # SaveActiveTreeIfAny does.
        tree_start = B9_MERGED_SFS.index("\t\tRECORDING_TREE")
        tree_end = B9_MERGED_SFS.index("\t\tREWIND_POINTS")
        dup = (B9_MERGED_SFS[:tree_end]
               + B9_MERGED_SFS[tree_start:tree_end].replace(
                   "treeFormatVersion = 0", "treeFormatVersion = 0\n\t\t\tisActive = True", 1)
               + B9_MERGED_SFS[tree_end:])
        snap = saveparse.parse_parsek_scenario(dup)
        self.assertEqual(2, len(snap.trees), "fixture did not actually duplicate the tree")
        self.assertEqual(8, len(snap.recordings), "raw rows should be doubled")
        self.assertEqual(base, saveparse.observed_points_facets(snap),
                         "the double-written active tree changed the measured "
                         "distribution - facets must dedupe by recording id")

    def test_zero_recordings_reads_zero_not_absent(self):
        # A save that recorded NOTHING is the stronger form of the gate-12
        # defect, so it must red the same window rather than vanish into an
        # absent facet. All three statistics degrade to 0 over an empty set.
        snap = saveparse.parse_parsek_scenario(
            "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t}\n}\n")
        self.assertEqual({"total": 0, "largest": 0, "smallest": 0,
                          "trivialRecordings": 0, "recordings": 0,
                          "unparsed": 0},
                         saveparse.observed_points_facets(snap))
        r = saveparse.evaluate_save_structure(
            {"recordings": {"points": {"gating": True, "largest": {"min": 2}}}}, snap)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertEqual(("recordings.points.largest 0 < min 2",), r.armed_mismatches)


class Gate12CalibrationTests(unittest.TestCase):
    """The statement gate 12 needs, encoded against the MEASURED runs.

    Calibration measured 2026-08-01 on `stock-minimal` during the PR #1408
    verification:

    - HEALTHY `2026-08-01_1626_EVA-2-orbital-board`: the EVA kerbal recorded 5
      points over its ~10.068 s `settleSeconds` dwell (maxDist 1888 m); the pod
      recorded 1, because it lives only 0.167 s (EvaBoard -> StopRecording)
      against a 0.200 s minimum sample interval.
    - BROKEN `2026-07-30_1532_S0.7-exit-auto-commit`: BOTH recordings finalized
      at `points=1 maxDist=0m`.

    So the discriminator MUST be `largest`, and no window may require every
    recording to exceed one point - that would red the healthy run too. This
    cell exists so a future "tighten the window" edit that reintroduces the
    per-recording form fails here instead of on a nightly.
    """

    HEALTHY = (5, 1)   # EVA kerbal, pod
    BROKEN = (1, 1)    # both empty

    @staticmethod
    def _sfs(point_counts):
        recs = "".join(
            "\t\t\tRECORDING\n\t\t\t{\n\t\t\t\trecordingId = r%d\n"
            "\t\t\t\tvesselName = V%d\n\t\t\t\tpointCount = %d\n\t\t\t}\n"
            % (i, i, n) for i, n in enumerate(point_counts))
        return ("GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n"
                "\t\tRECORDING_TREE\n\t\t{\n\t\t\tid = t\n%s\t\t}\n\t}\n}\n" % recs)

    def _evaluate(self, point_counts, block):
        snap = saveparse.parse_parsek_scenario(self._sfs(point_counts))
        return saveparse.evaluate_save_structure(
            {"recordings": {"points": block}}, snap)

    def test_largest_window_separates_healthy_from_broken(self):
        block = {"gating": True, "largest": {"min": 2}}
        self.assertEqual(saveparse.STATUS_PASS,
                         self._evaluate(self.HEALTHY, block).status)
        broken = self._evaluate(self.BROKEN, block)
        self.assertEqual(saveparse.STATUS_FAIL, broken.status)
        self.assertEqual(("recordings.points.largest 1 < min 2",),
                         broken.armed_mismatches)

    def test_recordings_count_alone_cannot_separate_them(self):
        # The whole reason gate 12 existed: `recordings.count = {min=2,max=2}`
        # counts .prec FILES, and both the healthy and the broken tree hold
        # exactly two recordings, so no count window can tell them apart.
        #
        # SCOPE, stated because the cell is weaker than it looks: this is a
        # PROXY. The real `recordings.count` is a .prec FILE listing
        # (run.py::count_recordings), which this cell does not call - it asserts
        # the equivalent structural fact on the parsed save (same node count
        # either way). That is enough to show the new facet is not redundant
        # surface; it is not a test of verifier 7.
        for counts in (self.HEALTHY, self.BROKEN):
            snap = saveparse.parse_parsek_scenario(self._sfs(counts))
            self.assertEqual(2, len(snap.recordings))

    def test_a_per_recording_floor_would_red_the_healthy_run(self):
        # `smallest` is a LEGAL key, but pinning it >= 2 on an EVA-2-shaped
        # tree reds the GOOD run (the pod's single point is expected). Kept as
        # an executable warning against "just require every recording to have
        # points" - the obvious tightening that does not hold here.
        block = {"gating": True, "smallest": {"min": 2}}
        self.assertEqual(saveparse.STATUS_FAIL,
                         self._evaluate(self.HEALTHY, block).status)

    def test_the_committed_eva2_window_actually_separates_the_two(self):
        """THE GUARD ON THE WINDOW ITSELF, not on the code.

        Every other cell here builds its OWN literal block, so all of them stay
        green if the COMMITTED window is loosened. Rewriting EVA-2's block to
        `largest = { min = 1 }` left the entire suite green - and `min = 1` is
        exactly the broken baseline's reading, i.e. the one edit that silently
        reproduces the defect gate 12 exists to close. "This window is flaky,
        loosen it" is the most likely future edit, and a PASS is silent.

        So this cell reads the REAL committed spec and asserts the PROPERTY
        rather than the literal: evaluated against the calibrated distributions,
        EVA-2's own block must PASS the healthy shape and FAIL the broken one.
        A window that stops discriminating reds here no matter which key or
        bound was weakened, and legitimate re-pinning (a tighter bound, an added
        key, a new facet) stays green as long as it still separates them.
        """
        block = dict(load_spec("EVA-2-orbital-board.toml")
                     ["expectations"]["recordings"]["points"])
        # Evaluate as if ARMED so the verdict is PASS/FAIL, not REPORT. This
        # cell asserts the window's DISCRIMINATING POWER; whether it is armed is
        # a separate operator decision pinned by
        # test_eva2_declares_the_points_block_unarmed in test_hlib.py.
        block["gating"] = True
        healthy = self._evaluate(self.HEALTHY, block)
        self.assertEqual(saveparse.STATUS_PASS, healthy.status,
                         "EVA-2's committed points window reds the HEALTHY calibrated "
                         "run (kerbal 5 points, pod 1): %s" % (healthy.armed_mismatches,))
        broken = self._evaluate(self.BROKEN, block)
        self.assertEqual(saveparse.STATUS_FAIL, broken.status,
                         "EVA-2's committed points window PASSES the BROKEN calibrated "
                         "run (both recordings 1 point) - it no longer discriminates, "
                         "which is the defect gate 12 exists to close")
        # ...and it must also catch a PARTIAL recurrence: one healthy recording
        # with empty siblings is the shape an aggregate-only window waves through.
        partial = self._evaluate((5, 1, 1, 1), block)
        self.assertEqual(saveparse.STATUS_FAIL, partial.status,
                         "EVA-2's committed window passes a tree where one recording is "
                         "healthy and three recorded nothing - an aggregate-only window")

    def test_a_legitimate_zero_is_absorbed_by_the_budget_not_by_luck(self):
        # `pointCount` is the FLAT rec.Points list, and there are THREE known
        # paths to a low/zero count on a tree nobody would call broken: the
        # parent-anchored debris trim, time warp (OnPhysicsFrame early-returns
        # on isOnRails, so a warped coast samples no flat points), and an
        # unbound Re-Fly provisional observed in the field on S4.1. This is what
        # makes `trivialRecordings` a BUDGET rather than a floor of zero, and
        # `smallest` unsafe to pin.
        healthy_with_debris = (300, 0)
        # The budget absorbs the known-legit zero...
        self.assertEqual(saveparse.STATUS_PASS,
                         self._evaluate(healthy_with_debris,
                                        {"gating": True, "largest": {"min": 2},
                                         "trivialRecordings": {"max": 1}}).status)
        # ...but it is a BUDGET, not a blanket pass: a SECOND empty recording
        # still reds. This is the property that separates "absorbed a known
        # legitimate zero" from "stopped asserting".
        self.assertEqual(saveparse.STATUS_FAIL,
                         self._evaluate((300, 0, 0),
                                        {"gating": True, "largest": {"min": 2},
                                         "trivialRecordings": {"max": 1}}).status)
        # A per-recording FLOOR, by contrast, reds the healthy shape outright -
        # which is why `smallest` is documented as the least safe key to pin.
        self.assertEqual(saveparse.STATUS_FAIL,
                         self._evaluate(healthy_with_debris,
                                        {"gating": True, "smallest": {"min": 1}}).status)

    def test_total_is_a_coarser_second_signal(self):
        # total 6 vs 2 also separates them, and is the facet to pin when a
        # scenario's per-recording split is not stable enough to bound.
        self.assertEqual(saveparse.STATUS_PASS,
                         self._evaluate(self.HEALTHY,
                                        {"gating": True, "total": {"min": 4}}).status)
        self.assertEqual(saveparse.STATUS_FAIL,
                         self._evaluate(self.BROKEN,
                                        {"gating": True, "total": {"min": 4}}).status)


class PointsBlockSpecSurfaceTests(unittest.TestCase):
    """`[expectations.recordings.points]` validation + per-block independence."""

    def test_good_shapes_validate(self):
        self.assertEqual([], saveparse.validate_points_expectations(None))
        self.assertEqual([], saveparse.validate_points_expectations(
            {"largest": {"min": 2}}))
        self.assertEqual([], saveparse.validate_points_expectations(
            {"gating": True, "total": 21, "largest": {"min": 1, "max": 9},
             "smallest": {"min": 1}}))

    def test_unknown_keys_and_bad_windows_rejected(self):
        errs = saveparse.validate_points_expectations({"biggest": {"min": 2}})
        self.assertTrue(any("unknown key" in e for e in errs))
        for bad in (-1, "x", True, {}, {"min": -1}, {"min": 2, "max": 1},
                    {"min": 1.5}, {"lo": 1}):
            with self.subTest(window=bad):
                self.assertTrue(
                    saveparse.validate_points_expectations({"largest": bad}), bad)
        self.assertTrue(saveparse.validate_points_expectations("nope"))

    def test_non_bool_gating_and_armed_empty_rejected(self):
        self.assertTrue(any("gating" in e for e in
                            saveparse.validate_points_expectations({"gating": "true"})))
        self.assertTrue(any("gates nothing" in e for e in
                            saveparse.validate_points_expectations({"gating": True})))
        self.assertEqual([], saveparse.validate_points_expectations({"gating": False}))

    def test_armed_window_that_cannot_red_is_rejected(self):
        # Third notch of the same rule _validate_window and _validate_armed_empty
        # enforce: an ARMED window whose only bound is `min = 0` can never fail
        # (counts are never negative), so it is a gate the author believes is on.
        # This is the shape a "loosen the flaky gate" edit converges on, and it
        # fails SILENTLY - a gate that cannot red looks exactly like one passing.
        for block in ({"gating": True, "largest": {"min": 0}},
                      {"gating": True, "trivialRecordings": {"min": 0}}):
            with self.subTest(block=block):
                errs = saveparse.validate_points_expectations(block)
                self.assertTrue(any("can never red" in e for e in errs), block)
        # A max beside it CAN red, so it is accepted...
        self.assertEqual([], saveparse.validate_points_expectations(
            {"gating": True, "largest": {"min": 0, "max": 9}}))
        # ...and UNARMED `min = 0` is merely uninformative, not a lie.
        self.assertEqual([], saveparse.validate_points_expectations(
            {"largest": {"min": 0}}))
        # Applies to the two older blocks too (zero committed specs use one).
        self.assertTrue(any("can never red" in e for e in
                            saveparse.validate_rewind_expectations(
                                {"gating": True, "supersedeRows": {"min": 0}})))
        self.assertTrue(any("can never red" in e for e in
                            saveparse.validate_structure_expectations(
                                {"gating": True, "trees": {"min": 0}})))

    def test_declared_empty_block_warns(self):
        warns = saveparse.save_structure_expectation_warnings(
            {"recordings": {"points": {"gating": False}}})
        self.assertEqual(1, len(warns))
        self.assertIn("expectations.recordings.points", warns[0])
        self.assertEqual([], saveparse.save_structure_expectation_warnings(
            {"recordings": {"points": {"largest": {"min": 2}}}}))

    def test_block_is_declared_and_armed_independently(self):
        # The reason points is a SIBLING of structure rather than a key inside
        # it: a spec that armed `structure` must not auto-arm a points window
        # added later (gating is per-block, adversarial-review finding 3).
        exp = {"recordings": {"structure": {"gating": True, "trees": 1},
                              "points": {"largest": {"min": 2}}}}
        self.assertEqual(("recordings.structure", "recordings.points"),
                         saveparse.declared_structure_blocks(exp))
        self.assertEqual(("recordings.structure",),
                         saveparse.armed_structure_blocks(exp))

        snap = saveparse.parse_parsek_scenario(B9_MERGED_SFS)
        exp["recordings"]["points"]["largest"] = {"min": 99}
        r = saveparse.evaluate_save_structure(exp, snap)
        self.assertEqual(saveparse.STATUS_PASS, r.status)  # structure passes
        self.assertEqual(("recordings.points.largest 9 < min 99",), r.mismatches)
        self.assertEqual((), r.armed_mismatches)  # points is report-only
        # Arming points alone gates points alone.
        exp["recordings"]["structure"].pop("gating")
        exp["recordings"]["points"]["gating"] = True
        r = saveparse.evaluate_save_structure(exp, snap)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertEqual(("recordings.points",), r.armed_blocks)

    def test_all_three_blocks_coexist(self):
        exp = {"rewind": {"supersedeRows": {"max": 9}},
               "recordings": {"structure": {"trees": 1},
                              "points": {"largest": {"min": 1}}}}
        r = saveparse.evaluate_save_structure(
            exp, saveparse.parse_parsek_scenario(B9_MERGED_SFS))
        self.assertEqual(("rewind", "recordings.structure", "recordings.points"),
                         r.blocks)
        self.assertEqual((), r.mismatches)
        self.assertFalse(saveparse.gating_armed(exp))


# ---------------------------------------------------------------------------
# The SUPPLY-ROUTE facet ([expectations.routes]).
# ---------------------------------------------------------------------------

def _routes_sfs(routes_body=""):
    """A minimal well-formed save whose ParsekScenario carries `routes_body`.

    Written as TEXT rather than assembled from SfsNode objects on purpose: the
    thing under test is a reading of BYTES the C# writer produces, so a cell that
    hand-built the node tree would skip the half that has ever been wrong.
    """
    return ("GAME\n{\n\tversion = 1.12.5\n\tSCENARIO\n\t{\n"
            "\t\tname = ParsekScenario\n\t\tscene = 7, 5, 8, 6\n"
            + routes_body + "\t}\n}\n")


# One ROUTE node in the shape `RouteCodec.SerializeInto` writes, trimmed to the
# keys this facet reads. Structurally copied from the `depot-route-recorded`
# bytes (ids renamed so a cell cannot accidentally assert about the fixture).
_ROUTE_NODE = (
    "\t\t\tROUTE\n"
    "\t\t\t{\n"
    "\t\t\t\tid = route-1\n"
    "\t\t\t\tname = Route: Kerbin -> Mun\n"
    "\t\t\t\tisKscOrigin = True\n"
    "\t\t\t\tstatus = Active\n"
    "\t\t\t\tcompletedCycles = 3\n"
    "\t\t\t\tskippedCycles = 1\n"
    "\t\t\t\tbackingMissionTreeId = tree-a\n"
    "\t\t\t\tdockMemberRecordingId = rec-dock\n"
    "\t\t\t\tEXCLUDED_INTERVALS\n"
    "\t\t\t\t{\n"
    "\t\t\t\t\texcludedInterval = rec-a/seg3\n"
    "\t\t\t\t}\n"
    "\t\t\t\tRECORDING_IDS\n"
    "\t\t\t\t{\n"
    "\t\t\t\t\tid = rec-a\n"
    "\t\t\t\t\tid = rec-dock\n"
    "\t\t\t\t}\n"
    "\t\t\t\tSOURCE_REFS\n"
    "\t\t\t\t{\n"
    "\t\t\t\t\tSOURCE\n"
    "\t\t\t\t\t{\n"
    "\t\t\t\t\t\trecordingId = rec-a\n"
    "\t\t\t\t\t\ttreeId = tree-a\n"
    "\t\t\t\t\t}\n"
    "\t\t\t\t\tSOURCE\n"
    "\t\t\t\t\t{\n"
    "\t\t\t\t\t\trecordingId = rec-dock\n"
    "\t\t\t\t\t\ttreeId = tree-a\n"
    "\t\t\t\t\t}\n"
    "\t\t\t\t}\n"
    "\t\t\t\tORIGIN\n"
    "\t\t\t\t{\n"
    "\t\t\t\t\tbodyName = Kerbin\n"
    "\t\t\t\t\tlatitude = 0\n"
    "\t\t\t\t\tlongitude = 0\n"
    "\t\t\t\t\taltitude = 0\n"
    "\t\t\t\t\tisSurface = True\n"
    "\t\t\t\t}\n"
    "\t\t\t\tSTOP\n"
    "\t\t\t\t{\n"
    "\t\t\t\t\tconnectionKind = DockingPort\n"
    "\t\t\t\t\tsegmentIndexBefore = 0\n"
    "\t\t\t\t\tENDPOINT\n"
    "\t\t\t\t\t{\n"
    "\t\t\t\t\t\tvesselPersistentId = 4277041026\n"
    "\t\t\t\t\t\tbodyName = Mun\n"
    "\t\t\t\t\t\tisSurface = False\n"
    "\t\t\t\t\t}\n"
    "\t\t\t\t}\n"
    "\t\t\t}\n")

_STOP_HEAD = "\t\t\t\tSTOP\n"


def _committed(node):
    return _routes_sfs("\t\tROUTES\n\t\t{\n" + node + "\t\t}\n")


ONE_ROUTE_SFS = _committed(_ROUTE_NODE)


class RouteParseTests(unittest.TestCase):
    """The ROUTES-node parse, pinned against RouteStore.SaveRoutesTo +
    RouteCodec.SerializeInto."""

    def test_a_committed_route_reads_every_modelled_field(self):
        snap = saveparse.parse_parsek_scenario(ONE_ROUTE_SFS)
        self.assertTrue(snap.parsed, snap.error)
        self.assertEqual(1, len(snap.routes))
        r = snap.routes[0]
        self.assertEqual("route-1", r.route_id)
        self.assertEqual("Route: Kerbin -> Mun", r.name)
        self.assertEqual("Active", r.status)
        self.assertTrue(r.is_ksc_origin)
        self.assertEqual(3, r.completed_cycles)
        self.assertEqual(1, r.skipped_cycles)
        self.assertEqual("tree-a", r.backing_mission_tree_id)
        self.assertEqual("rec-dock", r.dock_member_recording_id)
        self.assertEqual(("rec-a", "rec-dock"), r.recording_ids)
        self.assertEqual(("rec-a", "rec-dock"), r.source_recording_ids)
        self.assertEqual(("rec-a/seg3",), r.excluded_intervals)
        self.assertFalse(r.dormant)
        self.assertEqual("", r.codec_reject)
        self.assertEqual("Kerbin", r.origin.body_name)
        self.assertTrue(r.origin.is_surface)
        self.assertEqual(1, len(r.stops))
        self.assertEqual("DockingPort", r.stops[0].connection_kind)
        self.assertEqual("4277041026", r.stops[0].endpoint.vessel_persistent_id)
        self.assertEqual("Mun", r.stops[0].endpoint.body_name)

    def test_no_routes_node_is_the_ordinary_no_route_save(self):
        """RouteStore.SaveRoutesTo writes NOTHING on an empty store, so an absent
        node is the common path and must not read as a fault."""
        snap = saveparse.parse_parsek_scenario(_routes_sfs())
        self.assertTrue(snap.parsed, snap.error)
        self.assertEqual((), snap.routes)
        self.assertEqual(NO_ROUTES_FACET, saveparse.observed_routes_facets(snap))

    def test_dormant_routes_are_counted_apart_from_committed_ones(self):
        """DORMANT_ROUTES is a sparse SIBLING carrying the same ROUTE children.
        A dormant route cannot dispatch, bind a tree or render (RouteStore hands
        only CommittedRoutes to every consumer), so folding it into `count` would
        make every window mean the weaker claim."""
        text = _routes_sfs("\t\tDORMANT_ROUTES\n\t\t{\n" + _ROUTE_NODE + "\t\t}\n")
        snap = saveparse.parse_parsek_scenario(text)
        self.assertEqual((), snap.routes)
        self.assertEqual(1, len(snap.dormant_routes))
        self.assertTrue(snap.dormant_routes[0].dormant)
        facet = saveparse.observed_routes_facets(snap)
        self.assertEqual(0, facet["count"])
        self.assertEqual(1, facet["dormant"])
        # Every other aggregate is committed-only, so the dormant route
        # contributes nothing to stops / statuses / cycles.
        self.assertEqual(0, facet["stops"])
        self.assertEqual({}, facet["statuses"])
        self.assertEqual(0, facet["completedCycles"])

    def test_an_absent_or_unknown_status_reads_active(self):
        """RouteCodec.ParseStatusOrWarn maps BOTH to Active. Reading them any
        other way would invent a state the game never has - `statuses` must say
        what the GAME will hold, not what the bytes spell."""
        for spelling in ("", "\t\t\t\tstatus = FutureState\n"):
            node = _ROUTE_NODE.replace("\t\t\t\tstatus = Active\n", spelling)
            snap = saveparse.parse_parsek_scenario(_committed(node))
            with self.subTest(spelling=spelling or "<absent>"):
                self.assertEqual("Active", snap.routes[0].status)
                self.assertEqual(
                    {"Active": 1},
                    saveparse.observed_routes_facets(snap)["statuses"])

    def test_an_unknown_status_spelling_stays_visible_in_triage(self):
        """That mapping is game-faithful but LOSSY, so the raw spelling is kept
        on the row and counted. `RouteStatus` is APPEND-ONLY, so a non-zero
        `unknownStatuses` means the save was written by a NEWER Parsek than this
        parser - a version signal, which is why it is NOT a mismatch: redding
        would fail closed on an additive change that broke nothing."""
        node = _ROUTE_NODE.replace("status = Active", "status = FutureState")
        snap = saveparse.parse_parsek_scenario(_committed(node))
        self.assertEqual("FutureState", snap.routes[0].status_raw)
        self.assertEqual(
            1, saveparse.observed_routes_facets(snap)["unknownStatuses"])
        r = saveparse.evaluate_save_structure({"routes": {"count": 1}}, snap)
        self.assertEqual((), r.mismatches)
        # An ABSENT key is not "unknown" - it is the writer's own default path.
        absent = saveparse.parse_parsek_scenario(_committed(
            _ROUTE_NODE.replace("\t\t\t\tstatus = Active\n", "")))
        self.assertIsNone(absent.routes[0].status_raw)
        self.assertEqual(
            0, saveparse.observed_routes_facets(absent)["unknownStatuses"])

    def test_a_sparse_endpoint_pid_and_body_bucket_nowhere(self):
        """Both endpoint keys are sparse on the writer side (pid omitted at 0 -
        the KSC-origin shape, bodyName omitted when empty), so neither absence is
        a fault and neither may become an empty-string bucket."""
        node = (_ROUTE_NODE
                .replace("\t\t\t\t\t\tvesselPersistentId = 4277041026\n", "")
                .replace("\t\t\t\t\t\tbodyName = Mun\n", ""))
        snap = saveparse.parse_parsek_scenario(_committed(node))
        facet = saveparse.observed_routes_facets(snap)
        self.assertEqual([], facet["destinationVesselPids"])
        self.assertEqual({}, facet["destinationBodies"])
        # ...while the STOP itself still counts, so `stops` stays honest.
        self.assertEqual(1, facet["stops"])
        self.assertIsNone(snap.routes[0].stops[0].endpoint.vessel_persistent_id)

    def test_two_routes_aggregate_across_statuses_and_bodies(self):
        second = (_ROUTE_NODE
                  .replace("id = route-1", "id = route-2")
                  .replace("status = Active", "status = SourceChanged")
                  .replace("bodyName = Mun", "bodyName = Duna")
                  .replace("vesselPersistentId = 4277041026",
                           "vesselPersistentId = 1413036399"))
        snap = saveparse.parse_parsek_scenario(_committed(_ROUTE_NODE + second))
        facet = saveparse.observed_routes_facets(snap)
        self.assertEqual(2, facet["count"])
        self.assertEqual({"Active": 1, "SourceChanged": 1}, facet["statuses"])
        self.assertEqual({"Kerbin": 2}, facet["originBodies"])
        self.assertEqual({"Mun": 1, "Duna": 1}, facet["destinationBodies"])
        self.assertEqual({"DockingPort": 2}, facet["connectionKinds"])
        self.assertEqual(6, facet["completedCycles"])
        self.assertEqual(2, facet["skippedCycles"])
        self.assertEqual(4, facet["sourceRefs"])
        self.assertEqual(["route-1", "route-2"], facet["ids"])
        self.assertEqual(["4277041026", "1413036399"],
                         facet["destinationVesselPids"])

    def test_the_two_sparse_candidate_intent_siblings_are_read(self):
        text = _routes_sfs(
            "\t\tDISMISSED_ROUTE_CANDIDATES\n\t\t{\n"
            "\t\t\ttreeId = tree-x\n\t\t\ttreeId = tree-y\n\t\t}\n"
            "\t\tPROMPTED_ROUTE_CANDIDATES\n\t\t{\n\t\t\ttreeId = tree-z\n\t\t}\n")
        snap = saveparse.parse_parsek_scenario(text)
        self.assertEqual(("tree-x", "tree-y"), snap.dismissed_candidate_tree_ids)
        self.assertEqual(("tree-z",), snap.prompted_candidate_tree_ids)
        facet = saveparse.observed_routes_facets(snap)
        self.assertEqual(2, facet["dismissedCandidates"])
        self.assertEqual(1, facet["promptedCandidates"])

    def test_an_unreadable_snapshot_yields_no_facets_at_all(self):
        """ABSENT means "not measured", never zero - the observed_points_facets
        contract, so a torn save cannot satisfy a `max` window."""
        self.assertEqual({}, saveparse.observed_routes_facets(None))
        self.assertEqual(
            {}, saveparse.observed_routes_facets(
                saveparse.parse_parsek_scenario("GAME\n{\n")))

    def test_a_connection_kind_is_normalised_the_way_the_loader_reads_it(self):
        """`RouteNodeCodec.ParseConnectionKind` tries a NUMERIC spelling FIRST and
        maps anything unrecognised to the `Unknown` member, so the facet must
        bucket what the loader HOLDS, not what the bytes spell. Bucketing the raw
        value would put a `"1"` key in `connectionKinds` on a save the game reads
        as `DockingPort`, and no whitelisted window could ever name it."""
        cases = [
            # (raw spelling, bucket, counts as an unknown collapse)
            ("DockingPort", "DockingPort", False),   # the ordinary written form
            ("1", "DockingPort", False),             # the numeric spelling C# accepts
            ("0", "None", False),
            ("4", "Unknown", False),                 # Unknown's own ordinal - defined
            ("Unknown", "Unknown", False),           # ...and its own name
            ("Klaw", "Unknown", True),               # collapsed: not a member
            ("99", "Unknown", True),                 # collapsed: out of range
        ]
        for raw, bucket, collapsed in cases:
            node = _ROUTE_NODE.replace("connectionKind = DockingPort",
                                       "connectionKind = " + raw)
            snap = saveparse.parse_parsek_scenario(_committed(node))
            facet = saveparse.observed_routes_facets(snap)
            with self.subTest(raw=raw):
                self.assertEqual(raw, snap.routes[0].stops[0].connection_kind_raw)
                self.assertEqual(bucket, snap.routes[0].stops[0].connection_kind)
                self.assertEqual({bucket: 1}, facet["connectionKinds"])
                self.assertEqual(1 if collapsed else 0,
                                 facet["unknownConnectionKinds"])
                # Every bucket is a name the whitelist accepts, which is the
                # property that makes the normalisation worth doing.
                self.assertEqual([], saveparse.validate_routes_expectations(
                    {"connectionKinds": {bucket: 1}}))

    def test_an_absent_connection_kind_is_the_none_default_not_a_collapse(self):
        """The writer never omits the key, so an absent one is a hand-mutated
        save - it reads as the `None` member (C#'s first branch) and is NOT an
        unknown spelling."""
        node = _ROUTE_NODE.replace(
            "\t\t\t\t\tconnectionKind = DockingPort\n", "")
        snap = saveparse.parse_parsek_scenario(_committed(node))
        self.assertIsNone(snap.routes[0].stops[0].connection_kind_raw)
        self.assertEqual("None", snap.routes[0].stops[0].connection_kind)
        facet = saveparse.observed_routes_facets(snap)
        self.assertEqual({"None": 1}, facet["connectionKinds"])
        self.assertEqual(0, facet["unknownConnectionKinds"])

    def test_the_sparse_hold_kind_is_measured_not_discarded(self):
        """MEASURED-ONLY, no spec window - but RECORDED, because the row parses
        the key and a parsed-then-discarded surface is a doc claim nothing backs
        (the `rewindRetirements` precedent). Real shape: RVR-4's produced save
        carries `lastHoldKind = FundsShort` on its one Paused route."""
        node = _ROUTE_NODE.replace(
            "\t\t\t\tstatus = Active\n",
            "\t\t\t\tstatus = Paused\n\t\t\t\tlastHoldKind = FundsShort\n")
        snap = saveparse.parse_parsek_scenario(_committed(node))
        self.assertEqual("FundsShort", snap.routes[0].last_hold_kind)
        self.assertEqual({"FundsShort": 1},
                         saveparse.observed_routes_facets(snap)["holdKinds"])
        # A never-held route writes no key at all, so it buckets nowhere.
        self.assertEqual(
            {}, saveparse.observed_routes_facets(
                saveparse.parse_parsek_scenario(ONE_ROUTE_SFS))["holdKinds"])

    def test_the_facet_rides_the_structure_facets_dict(self):
        """`observed_structure_facets` is what run.py records, so a facet nobody
        can read off a run JSON would be a doc claim nothing backs."""
        obs = saveparse.observed_structure_facets(
            saveparse.parse_parsek_scenario(ONE_ROUTE_SFS))
        self.assertEqual(1, obs["routes"]["count"])


class RouteCodecRejectTests(unittest.TestCase):
    """`codec_reject` mirrors the two rejects in RouteCodec.DeserializeFrom. A
    route the LOADER drops is a route the save has already lost, and it is
    indistinguishable from "no route was ever created" once the game reads the
    file back."""

    def test_zero_stop_children_is_a_reject(self):
        node = _ROUTE_NODE[:_ROUTE_NODE.index(_STOP_HEAD)] + "\t\t\t}\n"
        snap = saveparse.parse_parsek_scenario(_committed(node))
        self.assertEqual("zero STOP children", snap.routes[0].codec_reject)
        self.assertEqual(1, saveparse.observed_routes_facets(snap)["codecRejects"])

    def test_a_source_missing_treeid_is_a_reject(self):
        node = _ROUTE_NODE.replace("\t\t\t\t\t\ttreeId = tree-a\n", "", 1)
        snap = saveparse.parse_parsek_scenario(_committed(node))
        self.assertEqual("SOURCE child #0 is missing recordingId or treeId",
                         snap.routes[0].codec_reject)

    def test_the_source_reject_wins_over_the_stop_reject(self):
        """C# walks SOURCE_REFS BEFORE the STOP check, so a node failing both
        reports the SOURCE reason - the same one the game's own Warn prints."""
        node = _ROUTE_NODE.replace("\t\t\t\t\t\ttreeId = tree-a\n", "", 1)
        node = node[:node.index(_STOP_HEAD)] + "\t\t\t}\n"
        snap = saveparse.parse_parsek_scenario(_committed(node))
        self.assertEqual("SOURCE child #0 is missing recordingId or treeId",
                         snap.routes[0].codec_reject)

    def test_an_unreadable_cycle_counter_never_reads_as_a_real_zero(self):
        node = _ROUTE_NODE.replace("completedCycles = 3", "completedCycles = ?")
        snap = saveparse.parse_parsek_scenario(_committed(node))
        self.assertIsNone(snap.routes[0].completed_cycles)
        facet = saveparse.observed_routes_facets(snap)
        self.assertEqual(1, facet["unparsed"])
        # The route contributes NOTHING to either sum rather than a false 0.
        self.assertEqual(0, facet["completedCycles"])
        self.assertEqual(0, facet["skippedCycles"])


class RoutesBlockValidationTests(unittest.TestCase):
    """The `[expectations.routes]` spec surface, refused PRE-LAUNCH."""

    def test_no_block_is_valid(self):
        self.assertEqual([], saveparse.validate_routes_expectations(None))

    def test_a_full_block_is_accepted(self):
        self.assertEqual([], saveparse.validate_routes_expectations({
            "gating": True,
            "count": {"min": 1, "max": 1},
            "dormant": {"max": 0},
            "stops": 1,
            "sourceRefs": {"min": 4, "max": 4},
            "completedCycles": {"min": 1},
            "skippedCycles": {"max": 0},
            "statuses": {"Active": {"min": 1, "max": 1}},
            "connectionKinds": {"DockingPort": 1},
            "originBodies": {"Kerbin": 1},
            "destinationBodies": {"Kerbin": 1},
            "ids": ["5420f805fcbb453b8d5928b71393f14b"],
            "destinationVesselPids": ["3620499050"],
        }))

    def test_an_unknown_key_is_refused(self):
        errs = saveparse.validate_routes_expectations({"escrow": 1})
        self.assertEqual(1, len(errs))
        self.assertIn("unknown key(s) ['escrow']", errs[0])

    def test_a_non_bool_gating_is_refused(self):
        errs = saveparse.validate_routes_expectations(
            {"gating": "true", "count": 1})
        self.assertTrue(any("must be a bool" in e for e in errs), errs)

    def test_an_armed_block_with_no_assertion_key_is_refused(self):
        errs = saveparse.validate_routes_expectations({"gating": True})
        self.assertTrue(any("gates nothing" in e for e in errs), errs)

    def test_an_armed_min_zero_window_is_refused(self):
        errs = saveparse.validate_routes_expectations(
            {"gating": True, "count": {"min": 0}})
        self.assertTrue(any("can never red" in e for e in errs), errs)
        # ...and is merely uninformative when the block is a READING.
        self.assertEqual([], saveparse.validate_routes_expectations(
            {"count": {"min": 0}}))

    def test_an_unknown_status_or_connection_kind_name_is_refused(self):
        errs = saveparse.validate_routes_expectations({"statuses": {"Retired": 1}})
        self.assertTrue(any("unknown name(s) ['Retired']" in e for e in errs), errs)
        errs = saveparse.validate_routes_expectations(
            {"connectionKinds": {"Klaw": 1}})
        self.assertTrue(any("unknown name(s) ['Klaw']" in e for e in errs), errs)

    def test_a_body_group_takes_free_form_names(self):
        """A body name is not an enum - there is no committed list of every body
        a modded install can present - so only the WINDOW is checked."""
        self.assertEqual([], saveparse.validate_routes_expectations(
            {"originBodies": {"SomeModdedPlanet": {"min": 1}}}))
        errs = saveparse.validate_routes_expectations(
            {"originBodies": {"Kerbin": -1}})
        self.assertTrue(any("must be >= 0" in e for e in errs), errs)

    def test_a_set_key_must_be_a_list_of_non_empty_strings(self):
        errs = saveparse.validate_routes_expectations({"ids": "route-1"})
        self.assertTrue(any("must be a list of strings" in e for e in errs), errs)
        errs = saveparse.validate_routes_expectations({"ids": ["", 3]})
        self.assertEqual(2, len(errs), errs)
        # An EMPTY list is a real claim ("the save carries none"), not an empty
        # window, so it is accepted.
        self.assertEqual([], saveparse.validate_routes_expectations({"ids": []}))


class RoutesBlockEvaluationTests(unittest.TestCase):
    """The verifier decision over `[expectations.routes]`."""

    def setUp(self):
        self.snap = saveparse.parse_parsek_scenario(ONE_ROUTE_SFS)

    def test_the_block_is_report_only_until_armed(self):
        exp = {"routes": {"count": {"min": 9}}}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_REPORT, r.status)
        self.assertFalse(r.gating)
        self.assertEqual(("routes",), r.blocks)
        self.assertEqual((), r.armed_blocks)
        self.assertEqual(("routes.count 1 < min 9",), r.mismatches)
        self.assertEqual((), r.armed_mismatches)

    def test_arming_makes_it_pass_or_fail(self):
        exp = {"routes": {"gating": True, "count": {"min": 1, "max": 1},
                          "statuses": {"Active": 1}}}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_PASS, r.status)
        self.assertEqual(("routes",), r.armed_blocks)
        exp["routes"]["statuses"] = {"SourceChanged": {"min": 1}}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertEqual(("routes.statuses.SourceChanged 0 < min 1",),
                         r.armed_mismatches)

    def test_gating_stays_per_block(self):
        """Arming routes must not promote a declared-but-unarmed rewind block -
        the adversarial-review finding 3 property, re-asserted for the fourth
        block rather than assumed to carry over."""
        exp = {"routes": {"gating": True, "count": 1},
               "rewind": {"supersedeRows": {"min": 1}}}
        r = saveparse.evaluate_save_structure(exp, self.snap)
        self.assertEqual(saveparse.STATUS_PASS, r.status)
        self.assertEqual(("rewind", "routes"), r.blocks)
        self.assertEqual(("routes",), r.armed_blocks)
        self.assertEqual(("rewind.supersedeRows 0 < min 1",), r.mismatches)
        self.assertEqual((), r.armed_mismatches)

    def test_a_codec_reject_is_a_defined_mismatch_with_no_window_declared(self):
        node = _ROUTE_NODE[:_ROUTE_NODE.index(_STOP_HEAD)] + "\t\t\t}\n"
        snap = saveparse.parse_parsek_scenario(_committed(node))
        r = saveparse.evaluate_save_structure({"routes": {"count": 1}}, snap)
        self.assertTrue(any("would be DROPPED by RouteCodec" in m
                            for m in r.mismatches), r.mismatches)

    def test_an_unreadable_cycle_counter_is_a_defined_mismatch(self):
        node = _ROUTE_NODE.replace("skippedCycles = 1", "skippedCycles = x")
        snap = saveparse.parse_parsek_scenario(_committed(node))
        r = saveparse.evaluate_save_structure({"routes": {"count": 1}}, snap)
        self.assertTrue(any("no readable completedCycles/skippedCycles" in m
                            for m in r.mismatches), r.mismatches)

    def test_a_set_assertion_compares_membership_not_order(self):
        second = _ROUTE_NODE.replace("id = route-1", "id = route-2")
        snap = saveparse.parse_parsek_scenario(_committed(_ROUTE_NODE + second))
        exp = {"routes": {"gating": True, "ids": ["route-2", "route-1"]}}
        self.assertEqual(saveparse.STATUS_PASS,
                         saveparse.evaluate_save_structure(exp, snap).status)
        exp["routes"]["ids"] = ["route-1", "route-3"]
        r = saveparse.evaluate_save_structure(exp, snap)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertEqual(
            ("routes.ids ['route-1', 'route-2'] != ['route-1', 'route-3']",),
            r.armed_mismatches)

    def test_an_unreadable_save_gates_the_routes_block(self):
        r = saveparse.evaluate_save_structure(
            {"routes": {"gating": True, "count": 1}}, None)
        self.assertEqual(saveparse.STATUS_FAIL, r.status)
        self.assertEqual(("save unreadable: missing persistent.sfs",),
                         r.armed_mismatches)

    def test_a_declared_assertion_less_block_warns(self):
        warns = saveparse.save_structure_expectation_warnings({"routes": {}})
        self.assertEqual(1, len(warns))
        self.assertIn("expectations.routes", warns[0])

    def test_the_singular_reserved_near_miss_warns(self):
        """`route` (singular) is still in hlib.RESERVED_EXPECTATION_BLOCKS, so it
        is recorded SKIPPED and gates nothing. A one-character slip must not look
        like an armed gate."""
        self.assertEqual([], saveparse.reserved_route_block_warnings(
            {"routes": {"count": 1}}))
        warns = saveparse.reserved_route_block_warnings({"route": {"count": 1}})
        self.assertEqual(1, len(warns))
        self.assertIn("[expectations.routes], plural", warns[0])


class RouteFacetFixtureAgreementTests(unittest.TestCase):
    """ONE MEASUREMENT, TWO CONSUMERS. The builder's `ROUTE_FACET_PINS` and this
    file's `RECORDED_FIXTURES[...]["routes"]` are two spellings of one dict;
    without this cell they are two things to drift, which is the exact failure
    moving the pins off builder-side was meant to end."""

    def test_the_builder_and_the_sweep_pin_the_same_route_facet(self):
        path = os.path.join(TOOLS_DIR, "build_depot_route_recorded.py")
        spec = importlib.util.spec_from_file_location(
            "build_depot_route_recorded_facet_check", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertEqual(
            CommittedFixtureSweepTests
            .RECORDED_FIXTURES["depot-route-recorded"]["routes"],
            module.ROUTE_FACET_PINS,
            "the fixture sweep and build_depot_route_recorded.py disagree about "
            "the committed route - re-derive both from "
            "saveparse.observed_routes_facets over the fixture bytes")

    def test_the_pinned_facet_is_what_the_parser_reads_off_the_bytes(self):
        path = os.path.join(FIXTURE_SAVES_DIR, "depot-route-recorded",
                            "persistent.sfs")
        snap = saveparse.parse_parsek_scenario(_read(path))
        self.assertEqual(
            CommittedFixtureSweepTests
            .RECORDED_FIXTURES["depot-route-recorded"]["routes"],
            saveparse.observed_routes_facets(snap))

    def test_the_interbody_builder_and_the_sweep_pin_the_same_route_facet(self):
        path = os.path.join(TOOLS_DIR, "build_interbody_route_recorded.py")
        spec = importlib.util.spec_from_file_location(
            "build_interbody_route_recorded_facet_check", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertEqual(
            CommittedFixtureSweepTests
            .RECORDED_FIXTURES["interbody-route-recorded"]["routes"],
            module.ROUTE_FACET_PINS,
            "the fixture sweep and build_interbody_route_recorded.py disagree "
            "about the committed routes - re-derive both from "
            "saveparse.observed_routes_facets over the fixture bytes")

    def test_the_interbody_pinned_facet_is_what_the_parser_reads(self):
        path = os.path.join(FIXTURE_SAVES_DIR, "interbody-route-recorded",
                            "persistent.sfs")
        snap = saveparse.parse_parsek_scenario(_read(path))
        self.assertEqual(
            CommittedFixtureSweepTests
            .RECORDED_FIXTURES["interbody-route-recorded"]["routes"],
            saveparse.observed_routes_facets(snap))

    def test_the_interbody_fixture_is_the_corpus_only_cross_body_route(self):
        """THE claim the fixture exists to make, stated as a sweep.

        `ClassifyRouteScope` answers InterBody when the origin body differs from
        a stop body, so a fixture is an inter-body subject exactly when its
        `originBodies` and `destinationBodies` censuses do not name the same
        single body. Before this harvest NO committed fixture was one, which is
        why the G10 lanes could not be authored even after the product fix.
        """
        cross = []
        for name, want in sorted(
                CommittedFixtureSweepTests.RECORDED_FIXTURES.items()):
            routes = want.get("routes") or {}
            origins = set(routes.get("originBodies") or {})
            dests = set(routes.get("destinationBodies") or {})
            if not origins or not dests:
                continue
            if origins != dests or len(origins) > 1 or len(dests) > 1:
                cross.append(name)
        self.assertEqual(["interbody-route-recorded"], cross)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
