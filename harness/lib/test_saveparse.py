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

import os
import re
import tomllib
import unittest

import saveparse

LIB_DIR = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(LIB_DIR)
FIXTURE_SAVES_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves")
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")


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
    are pinned EXACTLY. Eleven fixtures carry the spliced inert ParsekScenario
    node (a flyable template must, or the FLIGHT route records nothing); the
    three fresh-* templates carry none. ALL committed fixtures carry zero
    trees / staging rows - the rich payloads are injected at stage time and
    deliberately NOT committed. A fixture edit that changes any of this reds
    here instead of on the next nightly."""

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
        "eva2-lko-crewed": True,
        "eva3-pad-3crew": True,
        "fresh-career": False,
        "fresh-sandbox": False,
        "fresh-science": False,
        "gloops-airshow": True,
        "gs1-two-stage-pad": True,
        "gs2-orbital-stack": True,
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
    }

    def test_fixture_set_is_exactly_the_committed_set(self):
        found = sorted(d for d in os.listdir(FIXTURE_SAVES_DIR)
                       if os.path.isdir(os.path.join(FIXTURE_SAVES_DIR, d)))
        expected = sorted(set(self.EXPECTED_SCENARIO_PRESENCE)
                          | set(self.RECORDED_FIXTURES))
        self.assertEqual(expected, found,
                         "committed fixture set changed - re-pin this sweep")

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


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
