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
import unittest

import saveparse

LIB_DIR = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(LIB_DIR)
FIXTURE_SAVES_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves")


def _read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


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
    are pinned EXACTLY. Nine fixtures carry the spliced inert ParsekScenario
    node (a flyable template must, or the FLIGHT route records nothing); the
    three fresh-* templates carry none. ALL committed fixtures carry zero
    trees / staging rows - the rich payloads are injected at stage time and
    deliberately NOT committed. A fixture edit that changes any of this reds
    here instead of on the next nightly."""

    # fixture dir -> ParsekScenario node present in persistent.sfs
    EXPECTED_SCENARIO_PRESENCE = {
        "b1-pad-craft": True,
        "b2-lko-craft": True,
        "bdock-forge-base": True,
        "bdock-station-craft": True,
        "bdock-station-pad": True,
        "career-pad-craft": True,
        "eva2-lko-crewed": True,
        "eva3-pad-3crew": True,
        "fresh-career": False,
        "fresh-sandbox": False,
        "fresh-science": False,
        "gloops-airshow": True,
    }

    def test_fixture_set_is_exactly_the_committed_set(self):
        found = sorted(d for d in os.listdir(FIXTURE_SAVES_DIR)
                       if os.path.isdir(os.path.join(FIXTURE_SAVES_DIR, d)))
        self.assertEqual(sorted(self.EXPECTED_SCENARIO_PRESENCE), found,
                         "committed fixture set changed - re-pin this sweep")

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

    def test_quicksave_sidecars_parse_too(self):
        # Three fixtures also commit a quicksave.sfs; same pinned emptiness.
        for name in ("b1-pad-craft", "b2-lko-craft", "gloops-airshow"):
            path = os.path.join(FIXTURE_SAVES_DIR, name, "quicksave.sfs")
            snap = saveparse.parse_parsek_scenario(_read(path))
            with self.subTest(fixture=name):
                self.assertTrue(snap.parsed, "%s: %s" % (name, snap.error))
                self.assertTrue(snap.scenario_found, name)
                self.assertEqual(0, len(snap.trees), name)

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

    `RecordingTreeRecordCodec.SaveRecording` writes `pointCount` on EVERY
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
                          "recordings": 4, "unparsed": 0}, f)
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
                          "recordings": 3, "unparsed": 1}, f)
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

    def test_zero_recordings_reads_zero_not_absent(self):
        # A save that recorded NOTHING is the stronger form of the gate-12
        # defect, so it must red the same window rather than vanish into an
        # absent facet. All three statistics degrade to 0 over an empty set.
        snap = saveparse.parse_parsek_scenario(
            "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t}\n}\n")
        self.assertEqual({"total": 0, "largest": 0, "smallest": 0,
                          "recordings": 0, "unparsed": 0},
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
        # counts .prec FILES, and both trees have exactly two recordings. This
        # asserts the OLD signal is genuinely blind, so the new one is not
        # redundant surface.
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

    def test_a_debris_zero_is_a_legitimate_reading_not_a_defect(self):
        # `pointCount` is the FLAT rec.Points list, and
        # DebrisRelativeRecorderPolicy.TrimFlatPointsPastRenderableTail EMPTIES
        # it on a parent-anchored debris recording with no renderable tail (the
        # real coverage lives in TrackSection.frames / bodyFixedFrames, which
        # this facet cannot see). So a HEALTHY debris-producing tree can read 0
        # on one recording. `largest` and `total` ride over it; a `smallest`
        # window would be measuring that trim rather than a defect. Pinned so
        # the asymmetry is a decision on record, not a surprise in the field.
        healthy_with_debris = (300, 0)
        self.assertEqual(saveparse.STATUS_PASS,
                         self._evaluate(healthy_with_debris,
                                        {"gating": True, "largest": {"min": 2}}).status)
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
