"""Tests for `savepatch.py`, the pure FLIGHTSTATE patcher behind
`[[fixture.liveState]]`.

THE TESTS RUN ON TWO SUBJECTS ON PURPOSE, and the pair is the point:

  * A SYNTHETIC save (`_SYNTHETIC`, built here) exercises the mechanism's own
    branches - set / clamp / clear / insert-the-CSV / preserve-CRLF - in bytes
    small enough that "nothing else changed" is checkable by eye as well as by
    assertion.
  * THE COMMITTED FIXTURE `rover-relay-c-recorded` exercises it against the
    bytes the matrix lanes actually stage. That half carries the strongest cell
    in this file: applying `restore-dock-endpoint:<N>` to a fixture the BUILDER
    already restored from the same window must be a BYTE-IDENTICAL no-op, and
    clearing an endpoint and then restoring it must return the file to its
    committed bytes exactly. That is the "one implementation" claim proved
    mechanically rather than asserted in a comment.

Runnable with the stdlib runner only::

    cd harness && python -m unittest discover -s lib -q
"""

import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")
for _p in (HARNESS_ROOT, HERE, TOOLS_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import savepatch  # noqa: E402
import build_career_pad_craft as nodebase  # noqa: E402
import build_rover_relay_c_recorded as relayc  # noqa: E402

FIXTURE_NAME = "rover-relay-c-recorded"
FIXTURE_SFS = os.path.join(HARNESS_ROOT, "fixtures", "saves", FIXTURE_NAME,
                           "persistent.sfs")

# The three rovers, by the pids every matrix lane addresses them with.
PID_A = 4280917262   # the DELIVERY destination
PID_B = 90564594     # the PICKUP source
PID_C = 612987736    # the transport (never patched by any lane)


def _read_fixture() -> str:
    with open(FIXTURE_SFS, "rb") as fh:
        return fh.read().decode("utf-8")


def _lines(text: str):
    return text.replace("\r\n", "\n").split("\n")


def _diff_lines(before: str, after: str):
    """[(index, beforeLine, afterLine)] for every line that changed, plus a
    marker row when the line COUNT changed (an insert/delete)."""
    a, b = _lines(before), _lines(after)
    out = []
    if len(a) != len(b):
        out.append(("count", len(a), len(b)))
    for i, (x, y) in enumerate(zip(a, b)):
        if x != y:
            out.append((i, x, y))
    return out


# A minimal, KSP-shaped save: GAME > FLIGHTSTATE > VESSEL > PART > {RESOURCE,
# MODULE}. Depths match a real save exactly (module keys at five tabs), which is
# what makes the CSV-insert branch below a real test of the depth-exact anchor
# rather than of a toy.
_SYNTHETIC = "\n".join([
    "GAME",
    "{",
    "\tFLIGHTSTATE",
    "\t{",
    "\t\tactiveVessel = 0",
    "\t\tVESSEL",
    "\t\t{",
    "\t\t\tname = Alpha",
    "\t\t\tpersistentId = 111",
    "\t\t\tPART",
    "\t\t\t{",
    "\t\t\t\tname = tank",
    "\t\t\t\tRESOURCE",
    "\t\t\t\t{",
    "\t\t\t\t\tname = LiquidFuel",
    "\t\t\t\t\tamount = 50",
    "\t\t\t\t\tmaxAmount = 100",
    "\t\t\t\t}",
    "\t\t\t\tMODULE",
    "\t\t\t\t{",
    "\t\t\t\t\tname = ModuleInventoryPart",
    "\t\t\t\t\tstagingEnabled = True",
    "\t\t\t\t\tinventory = evaChute",
    "\t\t\t\t\tSTOREDPARTS",
    "\t\t\t\t\t{",
    "\t\t\t\t\t\tSTOREDPART",
    "\t\t\t\t\t\t{",
    "\t\t\t\t\t\t\tpartName = evaChute",
    "\t\t\t\t\t\t\tslotIndex = 0",
    "\t\t\t\t\t\t}",
    "\t\t\t\t\t}",
    "\t\t\t\t}",
    "\t\t\t}",
    "\t\t}",
    "\t\tVESSEL",
    "\t\t{",
    "\t\t\tname = Beta",
    "\t\t\tpersistentId = 222",
    "\t\t\tPART",
    "\t\t\t{",
    "\t\t\t\tname = tank",
    "\t\t\t\tRESOURCE",
    "\t\t\t\t{",
    "\t\t\t\t\tname = LiquidFuel",
    "\t\t\t\t\tamount = 10",
    "\t\t\t\t\tmaxAmount = 100",
    "\t\t\t\t}",
    "\t\t\t}",
    "\t\t}",
    "\t}",
    "}",
    "",
])


class SharedImplementationTests(unittest.TestCase):
    """THE DRIFT GUARD. The whole justification for a lib-side module doing a
    builder's job is that there is exactly ONE implementation; identity (`is`,
    not equality) is the only form of that claim a refactor cannot quietly
    break."""

    def test_the_node_helpers_are_the_builders_own(self):
        self.assertIs(nodebase.find_node, savepatch.find_node)
        self.assertIs(nodebase.child_nodes, savepatch.child_nodes)
        self.assertIs(nodebase.get_value, savepatch.get_value)
        self.assertIs(nodebase.set_value, savepatch.set_value)

    def test_the_relay_builder_calls_this_module_rather_than_a_copy(self):
        self.assertIs(savepatch.inventory_modules, relayc._inventory_modules)
        self.assertIs(savepatch.dock_endpoint_stored_parts,
                      relayc._window_dock_endpoint_stored_parts)
        self.assertIs(savepatch.rewrite_container, relayc._rewrite_container)
        self.assertIs(savepatch.SNAPSHOT_INDENT_STRIP, relayc.SNAPSHOT_INDENT_STRIP)
        self.assertIs(savepatch.MODULE_KEY_INDENT, relayc.MODULE_KEY_INDENT)

    def test_the_tools_path_edit_cannot_shadow_anything(self):
        """`hlib` imports `savepatch`, so `savepatch`'s `sys.path` edit runs in
        every harness process. It APPENDS rather than inserting at 0, and this
        cell closes the other half: `tools/` must share no module name with
        `lib/`, with `harness/` itself, or with the stdlib, so no ordering
        question can arise in the first place."""
        tools = {f[:-3] for f in os.listdir(TOOLS_DIR) if f.endswith(".py")}
        lib = {f[:-3] for f in os.listdir(HERE) if f.endswith(".py")}
        root = {f[:-3] for f in os.listdir(HARNESS_ROOT) if f.endswith(".py")}
        self.assertEqual(set(), tools & lib)
        self.assertEqual(set(), tools & root)
        self.assertEqual(set(), tools & set(sys.stdlib_module_names))

    def test_the_layout_table_is_one_object(self):
        # The derivation comment lives in the builder; the VALUES live here. If a
        # future edit re-typed the tuple into the builder, this reds.
        self.assertIs(savepatch.INVENTORY_LAYOUTS[FIXTURE_NAME],
                      relayc.CRAFT_AUTHORED_INVENTORY_LAYOUT)
        self.assertEqual(((0, "0", "evaChute"),
                          (1, "0", "evaScienceKit"),
                          (1, "1", "DeployedCentralStation")),
                         savepatch.INVENTORY_LAYOUTS[FIXTURE_NAME])

    def test_window_indexing_agrees_with_the_builders_own_walk(self):
        # `windowIndex` in a spec must mean what `REPAIR_TARGETS` means. The
        # builder's walk carries extra tuple fields, so identity is impossible
        # here; span equality over the committed bytes is the next best thing.
        lines = _lines(_read_fixture())
        scn = relayc.parsek_scenario(lines)
        builder_windows = [w for _t, _r, _p, w in relayc._window_records(lines, scn)[0]]
        self.assertEqual(builder_windows, savepatch.route_windows(lines))
        self.assertEqual(2, len(builder_windows),
                         "the fixture's two route windows are what every "
                         "restore-dock-endpoint index addresses")


class SyntheticResourceTests(unittest.TestCase):
    def test_setting_one_resource_changes_exactly_one_line(self):
        after, notes = savepatch.apply_live_state(
            _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": 0}}])
        diff = _diff_lines(_SYNTHETIC, after)
        self.assertEqual(1, len(diff), diff)
        idx, was, now = diff[0]
        self.assertEqual("\t\t\t\t\tamount = 50", was)
        self.assertEqual("\t\t\t\t\tamount = 0", now)
        self.assertEqual(["pid=111 name=Alpha resources=[LiquidFuel 50->0] "
                          "inventory=keep"], notes)

    def test_the_other_vessel_is_untouched(self):
        after, _ = savepatch.apply_live_state(
            _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": 0}}])
        self.assertIn("\t\t\t\t\tamount = 10", after,
                      "Beta's tank must not move when Alpha is patched")

    def test_two_entries_apply_independently(self):
        after, notes = savepatch.apply_live_state(
            _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": 100}},
                         {"pid": 222, "resources": {"LiquidFuel": 0}}])
        self.assertEqual(2, len(_diff_lines(_SYNTHETIC, after)))
        self.assertEqual(2, len(notes))

    def test_above_max_is_a_hard_error_not_a_clamp(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": 101}}])
        self.assertIn("exceeds maxAmount", str(ctx.exception))
        self.assertIn("101", str(ctx.exception))

    def test_a_negative_amount_is_refused_by_the_applier_too(self):
        # The validator already rejects `< 0` at spec time; the applier is also
        # reachable WITHOUT the validator in front of it (the builder, a direct
        # caller), and a negative `amount =` is a save KSP never writes.
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": -1}}])
        self.assertIn("negative", str(ctx.exception))
        self.assertIn("-1", str(ctx.exception))

    def test_a_multi_tank_vessel_is_refused_not_split(self):
        # Two LiquidFuel RESOURCE nodes on one vessel: "LiquidFuel = 20" has two
        # defensible readings (per tank / across the vessel), so the applier
        # refuses and names the count rather than picking one.
        # Anchored on Alpha's own `amount = 50` (Beta's tank reads 10), so the
        # splice lands on the vessel the entry names.
        first_tank_end = ("\t\t\t\t\tamount = 50\n"
                          "\t\t\t\t\tmaxAmount = 100\n\t\t\t\t}\n")
        second_tank = "\n".join([
            "\t\t\t\tRESOURCE",
            "\t\t\t\t{",
            "\t\t\t\t\tname = LiquidFuel",
            "\t\t\t\t\tamount = 5",
            "\t\t\t\t\tmaxAmount = 100",
            "\t\t\t\t}",
            "",
        ])
        self.assertEqual(1, _SYNTHETIC.count(first_tank_end))
        two_tanks = _SYNTHETIC.replace(first_tank_end, first_tank_end + second_tank, 1)
        self.assertNotEqual(_SYNTHETIC, two_tanks, "the second tank was spliced")
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                two_tanks, [{"pid": 111, "resources": {"LiquidFuel": 20}}])
        self.assertIn("2 LiquidFuel RESOURCE node(s)", str(ctx.exception))
        self.assertIn("Alpha", str(ctx.exception))

    def test_exactly_at_max_is_accepted(self):
        after, _ = savepatch.apply_live_state(
            _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": 100}}])
        self.assertIn("\t\t\t\t\tamount = 100", after)

    def test_an_absent_resource_names_the_vessel(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                _SYNTHETIC, [{"pid": 111, "resources": {"Oxidizer": 1}}])
        self.assertIn("Alpha", str(ctx.exception))
        self.assertIn("Oxidizer", str(ctx.exception))

    def test_an_unknown_pid_is_a_hard_error(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                _SYNTHETIC, [{"pid": 999, "resources": {"LiquidFuel": 1}}])
        self.assertIn("999", str(ctx.exception))

    def test_no_entries_returns_the_text_unchanged(self):
        after, notes = savepatch.apply_live_state(_SYNTHETIC, [])
        self.assertEqual(_SYNTHETIC, after)
        self.assertEqual([], notes)

    def test_amount_formatting_matches_the_saves_own_style(self):
        self.assertEqual("0", savepatch.format_amount(0))
        self.assertEqual("400", savepatch.format_amount(400))
        self.assertEqual("400", savepatch.format_amount(400.0))
        self.assertEqual("45.6", savepatch.format_amount(45.6))


class SyntheticInventoryTests(unittest.TestCase):
    def test_clear_empties_the_container_and_drops_the_csv_key(self):
        after, notes = savepatch.apply_live_state(
            _SYNTHETIC, [{"pid": 111, "inventory": "clear"}])
        self.assertNotIn("STOREDPART\n", after.replace("STOREDPARTS", "X"))
        self.assertNotIn("inventory = ", after,
                         "KSP omits the key entirely for an empty container; a "
                         "blank `inventory = ` is a shape it never writes")
        self.assertIn("\t\t\t\t\tSTOREDPARTS\n\t\t\t\t\t{\n\t\t\t\t\t}", after)
        self.assertEqual(["pid=111 name=Alpha resources=[-] "
                          "inventory=clear (1 container(s))"], notes)

    def test_clear_then_a_manual_restore_round_trips_the_csv_insert(self):
        # The insert branch (`inventory` key ABSENT, anchored on stagingEnabled)
        # is the one that spliced a CSV into the middle of a stored part while it
        # was being written. Clearing then re-placing the same block must return
        # the file to its own bytes.
        lines = _lines(_SYNTHETIC)
        vessel = [s for _n, pid, s in savepatch.flightstate_vessels(lines)
                  if pid == "111"][0]
        module = savepatch.inventory_modules(lines, vessel)[0]
        block = lines[module[0]:module[1]]
        stored_start = next(i for i, l in enumerate(block)
                            if l.strip() == "STOREDPART")
        stored_end = next(i for i in range(stored_start, len(block))
                          if block[i].strip() == "}") + 1
        entry = (0, "evaChute", block[stored_start:stored_end])

        cleared = savepatch.rewrite_container(lines, module, [])
        vessel2 = [s for _n, pid, s in savepatch.flightstate_vessels(cleared)
                   if pid == "111"][0]
        module2 = savepatch.inventory_modules(cleared, vessel2)[0]
        restored = savepatch.rewrite_container(cleared, module2, [entry])
        self.assertEqual(lines, restored)

    def test_a_vessel_with_no_container_is_a_hard_error(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                _SYNTHETIC, [{"pid": 222, "inventory": "clear"}])
        self.assertIn("Beta", str(ctx.exception))

    def test_keep_is_the_default_and_changes_nothing(self):
        after, notes = savepatch.apply_live_state(
            _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": 50}}])
        self.assertEqual(_SYNTHETIC, after, "50 -> 50 plus inventory=keep is a no-op")
        self.assertIn("inventory=keep", notes[0])

    def test_restore_without_a_layout_row_names_the_save(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                _SYNTHETIC, [{"pid": 111, "inventory": "restore-dock-endpoint:0"}],
                save_name="not-a-known-fixture")
        self.assertIn("not-a-known-fixture", str(ctx.exception))


class LineEndingTests(unittest.TestCase):
    """A whole-file re-ending would turn a one-line patch into a multi-megabyte
    diff, and `rover-relay-c-recorded` is LF where every builder-authored fixture
    is CRLF, so both directions matter."""

    def test_lf_stays_lf(self):
        after, _ = savepatch.apply_live_state(
            _SYNTHETIC, [{"pid": 111, "resources": {"LiquidFuel": 1}}])
        self.assertNotIn("\r\n", after)

    def test_crlf_stays_crlf(self):
        crlf = _SYNTHETIC.replace("\n", "\r\n")
        after, _ = savepatch.apply_live_state(
            crlf, [{"pid": 111, "resources": {"LiquidFuel": 1}}])
        self.assertNotIn("\n\n", after.replace("\r\n", "\n\n").replace("\n\n", "\r\n"))
        self.assertEqual(after.count("\r\n"), crlf.count("\r\n"))
        self.assertNotIn("\r\r", after)


class CommittedFixtureTests(unittest.TestCase):
    """The bytes the matrix lanes actually stage."""

    @classmethod
    def setUpClass(cls):
        cls.text = _read_fixture()

    def test_the_fixture_is_lf(self):
        self.assertNotIn("\r\n", self.text,
                         "the harvest wrote this save LF-only; the patcher must "
                         "preserve that")

    def test_draining_the_pickup_source_changes_exactly_one_line(self):
        after, notes = savepatch.apply_live_state(
            self.text, [{"pid": PID_B, "resources": {"LiquidFuel": 0}}],
            save_name=FIXTURE_NAME)
        diff = _diff_lines(self.text, after)
        self.assertEqual(1, len(diff), diff)
        _idx, was, now = diff[0]
        self.assertEqual("amount = 200", was.strip())
        self.assertEqual("amount = 0", now.strip())
        self.assertIn("name=B", notes[0])
        self.assertIn("LiquidFuel 200->0", notes[0])

    def test_the_partial_origin_amount_lands_verbatim(self):
        after, _ = savepatch.apply_live_state(
            self.text, [{"pid": PID_B, "resources": {"LiquidFuel": 100}}],
            save_name=FIXTURE_NAME)
        diff = _diff_lines(self.text, after)
        self.assertEqual(1, len(diff), diff)
        self.assertEqual("amount = 100", diff[0][2].strip())

    def test_filling_the_destination_changes_exactly_one_line(self):
        after, _ = savepatch.apply_live_state(
            self.text, [{"pid": PID_A, "resources": {"LiquidFuel": 400}}],
            save_name=FIXTURE_NAME)
        diff = _diff_lines(self.text, after)
        self.assertEqual(1, len(diff), diff)
        self.assertEqual("amount = 400", diff[0][2].strip())

    def test_over_capacity_on_the_destination_is_refused(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                self.text, [{"pid": PID_A, "resources": {"LiquidFuel": 401}}],
                save_name=FIXTURE_NAME)
        self.assertIn("maxAmount 400", str(ctx.exception))

    def test_the_parsek_payload_is_never_touched(self):
        # The route windows are the patcher's INPUT, so editing one would make a
        # restore unfalsifiable. Assert the ParsekScenario span is byte-identical
        # after the most invasive combination any lane declares.
        after, _ = savepatch.apply_live_state(
            self.text,
            [{"pid": PID_B, "resources": {"LiquidFuel": 0}, "inventory": "clear"},
             {"pid": PID_A, "resources": {"LiquidFuel": 0}, "inventory": "clear"}],
            save_name=FIXTURE_NAME)
        before_lines, after_lines = _lines(self.text), _lines(after)
        b_scn = savepatch.parsek_scenario_node(before_lines)
        a_scn = savepatch.parsek_scenario_node(after_lines)
        self.assertEqual(before_lines[b_scn[0]:b_scn[1]],
                         after_lines[a_scn[0]:a_scn[1]],
                         "no recording, window, branch point, origin proof or "
                         "ledger row may move")

    def test_restoring_an_already_restored_endpoint_is_a_byte_identical_noop(self):
        # THE ONE-IMPLEMENTATION PROOF. `build_rover_relay_c_recorded.py` step 3
        # restored B from window 0 and A from window 1 at BUILD time; running the
        # stage-side mode over the same windows must reproduce those bytes
        # exactly, because it is the same code.
        for pid, window in ((PID_B, 0), (PID_A, 1)):
            after, _ = savepatch.apply_live_state(
                self.text,
                [{"pid": pid, "inventory": "restore-dock-endpoint:%d" % window}],
                save_name=FIXTURE_NAME)
            self.assertEqual(self.text, after,
                             "restore-dock-endpoint:%d on pid %d moved bytes the "
                             "builder had already placed" % (window, pid))

    def test_clear_then_restore_returns_the_committed_bytes(self):
        for pid, window in ((PID_B, 0), (PID_A, 1)):
            cleared, _ = savepatch.apply_live_state(
                self.text, [{"pid": pid, "inventory": "clear"}],
                save_name=FIXTURE_NAME)
            self.assertNotEqual(self.text, cleared)
            restored, _ = savepatch.apply_live_state(
                cleared,
                [{"pid": pid, "inventory": "restore-dock-endpoint:%d" % window}],
                save_name=FIXTURE_NAME)
            self.assertEqual(self.text, restored)

    def test_clearing_the_destination_frees_every_slot(self):
        after, notes = savepatch.apply_live_state(
            self.text, [{"pid": PID_A, "inventory": "clear"}],
            save_name=FIXTURE_NAME)
        lines = _lines(after)
        vessel = [s for _n, pid, s in savepatch.flightstate_vessels(lines)
                  if pid == str(PID_A)][0]
        modules = savepatch.inventory_modules(lines, vessel)
        self.assertEqual(2, len(modules), "rover A carries two containers")
        for module in modules:
            holder = savepatch.child_nodes(lines, module, "STOREDPARTS")[0]
            self.assertEqual([], savepatch.child_nodes(lines, holder, "STOREDPART"))
            self.assertIsNone(savepatch.get_value(lines, module, "inventory"))
        self.assertIn("clear (2 container(s))", notes[0])

    def test_a_window_index_past_the_end_is_refused(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_live_state(
                self.text, [{"pid": PID_B, "inventory": "restore-dock-endpoint:9"}],
                save_name=FIXTURE_NAME)
        self.assertIn("window 9", str(ctx.exception))

    def test_the_transport_is_addressable_but_no_lane_patches_it(self):
        # C is left exactly as saved by every lane in the matrix (the pickup
        # writer removes from the SOURCE and the delivery writer stores into the
        # DESTINATION, so nothing a dispatch reads touches the transport's hold).
        # The mechanism does not FORBID it; this cell records that it works, so a
        # future lane that needs it does not have to rediscover the pid.
        after, notes = savepatch.apply_live_state(
            self.text, [{"pid": PID_C, "resources": {"LiquidFuel": 0}}],
            save_name=FIXTURE_NAME)
        self.assertIn("name=C", notes[0])
        self.assertEqual(1, len(_diff_lines(self.text, after)))


class ValidateLiveStateTests(unittest.TestCase):
    """The spec surface, checked pre-launch by `hlib.validate_spec`."""

    def test_absent_is_valid(self):
        self.assertEqual([], savepatch.validate_live_state(
            {"saveTemplate": "fixtures/saves/x", "injectedRecordings": "none"}))

    def test_the_matrix_shapes_are_accepted(self):
        for entries in ([{"pid": PID_B, "resources": {"LiquidFuel": 0}}],
                        [{"pid": PID_B, "resources": {"LiquidFuel": 100}}],
                        [{"pid": PID_B, "inventory": "clear"}],
                        [{"pid": PID_B, "resources": {"LiquidFuel": 200},
                          "inventory": "clear"}],
                        [{"pid": PID_A, "resources": {"LiquidFuel": 400}}],
                        [{"pid": PID_A, "inventory": "restore-dock-endpoint:1"}],
                        [{"pid": PID_A, "inventory": "keep",
                          "resources": {"LiquidFuel": 300}}],
                        [{"pid": PID_A, "resources": {"LiquidFuel": 0}},
                         {"pid": PID_B, "resources": {"LiquidFuel": 0}}]):
            self.assertEqual([], savepatch.validate_live_state(
                {"liveState": entries}), entries)

    def test_rejected_shapes(self):
        cases = {
            "not-a-list": {"liveState": {"pid": 1}},
            "empty": {"liveState": []},
            "not-a-table": {"liveState": ["x"]},
            "unknown-key": {"liveState": [{"pid": 1, "fuel": 2}]},
            "no-pid": {"liveState": [{"resources": {"LiquidFuel": 0}}]},
            "zero-pid": {"liveState": [{"pid": 0, "resources": {"LiquidFuel": 0}}]},
            "bool-pid": {"liveState": [{"pid": True, "resources": {"LiquidFuel": 0}}]},
            "string-pid": {"liveState": [{"pid": "90564594",
                                          "resources": {"LiquidFuel": 0}}]},
            "dup-pid": {"liveState": [{"pid": 5, "resources": {"LiquidFuel": 0}},
                                      {"pid": 5, "inventory": "clear"}]},
            "resources-not-table": {"liveState": [{"pid": 5, "resources": 1}]},
            "resources-empty": {"liveState": [{"pid": 5, "resources": {}}]},
            "amount-not-number": {"liveState": [{"pid": 5,
                                                 "resources": {"LiquidFuel": "0"}}]},
            "amount-bool": {"liveState": [{"pid": 5,
                                           "resources": {"LiquidFuel": True}}]},
            "amount-negative": {"liveState": [{"pid": 5,
                                               "resources": {"LiquidFuel": -1}}]},
            "inventory-not-string": {"liveState": [{"pid": 5, "inventory": 1}]},
            "inventory-unknown": {"liveState": [{"pid": 5, "inventory": "wipe"}]},
            "inventory-undock-mode": {"liveState": [
                {"pid": 5, "inventory": "restore-undock-endpoint:1"}]},
            "inventory-no-index": {"liveState": [
                {"pid": 5, "inventory": "restore-dock-endpoint:"}]},
            "inventory-bad-index": {"liveState": [
                {"pid": 5, "inventory": "restore-dock-endpoint:+1"}]},
            "patches-nothing": {"liveState": [{"pid": 5}]},
        }
        for name, fixture in cases.items():
            self.assertNotEqual([], savepatch.validate_live_state(fixture),
                                "%s must be rejected" % name)

    def test_a_float_amount_is_accepted(self):
        # `45.6` is a legitimate declaration (it is the endpoint value the source
        # flight itself left behind), so the validator must not require an int.
        self.assertEqual([], savepatch.validate_live_state(
            {"liveState": [{"pid": 5, "resources": {"LiquidFuel": 45.6}}]}))

    def test_parse_inventory_mode(self):
        self.assertEqual(("keep", None), savepatch.parse_inventory_mode("keep"))
        self.assertEqual(("clear", None), savepatch.parse_inventory_mode("clear"))
        self.assertEqual(("restore-dock-endpoint", 1),
                         savepatch.parse_inventory_mode("restore-dock-endpoint:1"))
        with self.assertRaises(ValueError):
            savepatch.parse_inventory_mode("restore-dock-endpoint:x")


class ValidateSpecWiringTests(unittest.TestCase):
    """The block is validated by `hlib.validate_spec`, where every other fixture
    key is - so a malformed declaration is INVALID-SPEC with KSP never launched
    rather than a staging abort after the instance was prepared."""

    def _spec(self, live_state):
        fixture = {"saveTemplate": "fixtures/saves/%s" % FIXTURE_NAME,
                   "injectedRecordings": "none", "craft": []}
        if live_state is not None:
            fixture["liveState"] = live_state
        return {
            "schema": hlib.SCHEMA_VERSION,
            "id": "LIVESTATE-probe",
            "tier": "nightly",
            "instanceProfile": "stock-minimal",
            "fixture": fixture,
            "driver": {"kind": "seam", "steps": [
                {"cmd": "LoadGame", "args": {"save": "${runSave}"}, "expect": "OK"},
                {"cmd": "FlushAndQuit", "expect": "OK"},
            ]},
            "expectations": {"allowedAnomalies": []},
            "runtime": {"budgetSeconds": 600},
            "retry": {"policy": "once"},
        }

    def test_a_good_block_adds_no_error(self):
        base = hlib.validate_spec(self._spec(None), {}, bug_ids=[])
        good = hlib.validate_spec(
            self._spec([{"pid": PID_B, "resources": {"LiquidFuel": 0}}]), {},
            bug_ids=[])
        self.assertEqual(base.errors, good.errors)

    def test_a_bad_block_reaches_validate_spec(self):
        bad = hlib.validate_spec(
            self._spec([{"pid": -1, "resources": {"LiquidFuel": 0}}]), {}, bug_ids=[])
        self.assertTrue(any("liveState" in e for e in bad.errors), bad.errors)


class CommittedSpecUsageTests(unittest.TestCase):
    """Every committed spec that declares liveState must declare it against the
    ONE fixture whose bytes the layout table and the pid vocabulary describe.
    A lane pointing this mechanism at a different save would either fail closed
    at staging (best case) or patch a vessel nobody derived tokens from."""

    def test_only_the_relay_c_fixture_carries_live_state(self):
        import tomllib
        scenarios = os.path.join(HARNESS_ROOT, "scenarios")
        for name in sorted(n for n in os.listdir(scenarios) if n.endswith(".toml")):
            with open(os.path.join(scenarios, name), "rb") as fh:
                spec = tomllib.load(fh)
            fixture = spec.get("fixture") or {}
            if not savepatch.declared_live_state(fixture):
                continue
            self.assertEqual(
                "fixtures/saves/%s" % FIXTURE_NAME, fixture.get("saveTemplate"),
                "%s declares [[fixture.liveState]] against a save whose inventory "
                "layout and endpoint pids nothing has derived" % name)
            # And every declared pid must be one of the three rovers, so a typo
            # reds here rather than at staging time on a real instance.
            for entry in savepatch.declared_live_state(fixture):
                self.assertIn(entry.get("pid"), (PID_A, PID_B, PID_C),
                              "%s patches an unknown pid %r" % (name, entry.get("pid")))


if __name__ == "__main__":
    unittest.main()
