"""Cells for raceparse (multiplayer plan T-05): the rival-id leak scan and the
save-equivalence diff. The leak scan is proven non-vacuous per leak class: a rival id
planted as a node, a key value, a file name, raw binary bytes and inside a deflated
snapshot each red, while the same id under ``Parsek/Race/`` does not.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import os
import shutil
import struct
import tempfile
import unittest
import uuid
import zlib
from unittest import mock

import raceparse as rp
import saveparse

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(HERE)
FIXTURE = os.path.join(HARNESS, "fixtures", "saves", "cs-career-mun4")

OWN = "0f0f0f0f0f0f4f0f8f0f0f0f0f0f0f0f"
RIVAL = "a1b2c3d4e5f64a1b8c2d3e4f5a6b7c8d"
RIVAL_REC = "5e5e5e5e5e5e4e5e9e5e5e5e5e5e5e5e"
RIVAL_TREE = "7d7d7d7d7d7d4d7dad7d7d7d7d7d7d7d"
OWN_REC = "1c1c1c1c1c1c4c1c8c1c1c1c1c1c1c1c"
HASH = "0123456789abcdef"

SHARED_LISTING = (
    "race.cfg",
    "players/%s/player.cfg" % OWN,
    "players/%s/standings/000003.pstd" % OWN,
    "players/%s/payload/%s.%s.prec" % (OWN, OWN_REC, HASH),
    "players/%s/player.cfg" % RIVAL,
    "players/%s/standings/000006.pstd" % RIVAL,
    "players/%s/payload/%s.%s.prec" % (RIVAL, RIVAL_REC, HASH),
    "players/%s/payload/%s.%s_ghost.craft" % (RIVAL, RIVAL_REC, "fedcba9876543210"),
    "players/%s/payload/trees/%s.%s.cfg" % (RIVAL, RIVAL_TREE, HASH),
)

BASE_SFS = """GAME
{
\tversion = 1.12.5
\tFLIGHTSTATE
\t{
\t\tversion = 1.12.5
\t\tUT = 1000.5
\t\tVESSEL
\t\t{
\t\t\tname = Kerbal X
\t\t\tlct = 900
\t\t\tlastUT = 1000.5
\t\t\tmet = 100.5
\t\t\tORBIT
\t\t\t{
\t\t\t\tSMA = 600000
\t\t\t\tEPH = 1000.5
\t\t\t}
\t\t}
\t}
\tSCENARIO
\t{
\t\tname = Funding
\t\tfunds = 25000
\t}
\tSCENARIO
\t{
\t\tname = ParsekScenario
\t\tsaveStamp = 11111111111111111111111111111111
\t\tRECORDING_TREE
\t\t{
\t\t\tRECORDING
\t\t\t{
\t\t\t\trecordingId = %(own_rec)s
\t\t\t\tvesselName = Kerbal X
\t\t\t}
\t\t}
\t}
}
""" % {"own_rec": OWN_REC}


def _psn0(text: str) -> bytes:
    """A PSN0 snapshot sidecar in the SnapshotSidecarCodec layout resourceq decodes."""
    raw = text.encode("utf-8")
    comp = zlib.compressobj(9, zlib.DEFLATED, -15)
    body = comp.compress(raw) + comp.flush()
    header = (rp.SNAPSHOT_MAGIC + struct.pack("<ii", 0, 4) + bytes([1])
              + struct.pack("<iiI", len(raw), len(body), 0))
    return header + body


def _psk0(rec_id: str) -> bytes:
    """A PSK0-shaped trajectory sidecar: magic, ints, the length-prefixed recording id."""
    encoded = rec_id.encode("utf-8")
    return b"PSK0" + struct.pack("<iii", 1, 4, 1) + bytes([len(encoded)]) + encoded + b"\x00" * 8


def _base_save() -> dict:
    return {
        "persistent.sfs": BASE_SFS.encode("utf-8"),
        "Parsek/Recordings/%s.prec" % OWN_REC: _psk0(OWN_REC),
        "Parsek/Recordings/%s_ghost.craft" % OWN_REC: _psn0(
            "PART\n{\n\tname = mk1pod.v2\n}\n"),
        "Parsek/GameState/events.pgse": b"version = 1\r\nGAME_STATE_EVENT\r\n{\r\n\tut = 5\r\n}\r\n",
        "Parsek/Race/local.cfg": ("SHOW\n{\n\tRIVAL = %s True\n}\n" % RIVAL).encode("utf-8"),
        "Parsek/Race/rivals/a1b2c3d4/payload/%s.%s.prec" % (RIVAL_REC, HASH): _psk0(RIVAL_REC),
    }


def _ids() -> rp.RivalIds:
    return rp.collect_rival_ids(SHARED_LISTING, OWN)


class CollectRivalIdsTests(unittest.TestCase):

    def test_player_recording_and_tree_ids_of_rivals_only(self):
        ids = _ids()
        self.assertEqual(ids.player_ids, (RIVAL,))
        self.assertEqual(ids.recording_ids, (RIVAL_REC,))
        self.assertEqual(ids.tree_ids, (RIVAL_TREE,))
        self.assertEqual(ids.unrecognised, ())
        self.assertEqual(ids.count, 3)

    def test_the_own_subtree_is_skipped_in_either_spelling(self):
        for own in (OWN, "players/" + OWN, "players/%s/" % OWN):
            ids = rp.collect_rival_ids(SHARED_LISTING, own)
            self.assertNotIn(OWN, ids.player_ids)
            self.assertNotIn(OWN_REC, ids.recording_ids)

    def test_without_an_own_dir_every_player_is_a_rival(self):
        ids = rp.collect_rival_ids(SHARED_LISTING, None)
        self.assertEqual(ids.player_ids, (OWN, RIVAL))
        self.assertIn(OWN_REC, ids.recording_ids)

    def test_backslash_paths_and_each_payload_suffix(self):
        listing = [
            "players\\p2\\payload\\rec.one.%s.prec" % HASH,
            "players\\p2\\payload\\rec-two.%s_ghost.craft" % HASH,
            "players\\p2\\payload\\rec3.%s_vessel.craft" % HASH,
        ]
        ids = rp.collect_rival_ids(listing, "p1")
        self.assertEqual(ids.recording_ids, ("rec.one", "rec-two", "rec3"))

    def test_safe_write_leftovers_are_ignored_and_misfits_reported(self):
        listing = [
            "players/p2/payload/%s.%s.prec.tmp" % (RIVAL_REC, HASH),
            "players/p2/payload/%s.%s.prec.bak.%s" % (RIVAL_REC, HASH, uuid.uuid4().hex),
            "players/p2/payload/not-a-payload-name.prec",
            "players/p2/payload/trees/tree.ABCDEF0123456789.cfg",  # upper-case hash
            "players/p2/payload/deep/x.%s.prec" % HASH,
        ]
        ids = rp.collect_rival_ids(listing, None)
        self.assertEqual(ids.recording_ids, ())
        self.assertEqual(ids.tree_ids, ())
        self.assertEqual(len(ids.ignored), 2)
        self.assertEqual(len(ids.unrecognised), 3)
        self.assertEqual(ids.player_ids, ("p2",))

    def test_files_outside_players_name_no_one(self):
        ids = rp.collect_rival_ids(["race.cfg", "readme.txt", "players"], None)
        self.assertEqual(ids.count, 0)

    def test_hash_length_is_the_content_hash_length(self):
        self.assertEqual(rp.CONTENT_HASH_HEX_LENGTH, 16)
        short = "players/p2/payload/r.%s.prec" % HASH[:15]
        self.assertEqual(rp.collect_rival_ids([short], None).recording_ids, ())

    def test_list_folder_reads_a_real_tree(self):
        with tempfile.TemporaryDirectory() as tmp:
            for rel in SHARED_LISTING:
                path = os.path.join(tmp, *rel.split("/"))
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "wb") as fh:
                    fh.write(b"x")
            self.assertEqual(rp.list_folder(tmp), sorted(SHARED_LISTING))
            self.assertEqual(rp.collect_rival_ids(rp.list_folder(tmp), OWN), _ids())


class LeakScanTests(unittest.TestCase):
    """Each leak class reds on its own; the clean base save passes."""

    def scan(self, files):
        return rp.leak_scan_files(files, _ids())

    def assert_red(self, files, where, rival_id, path=None):
        res = self.scan(files)
        self.assertEqual(res.status, rp.STATUS_FAIL, res)
        hits = [f for f in res.findings if f.where == where and f.rival_id == rival_id
                and (path is None or f.path == path)]
        self.assertTrue(hits, "no %s finding for %s in %s" % (where, rival_id, res.findings))
        return hits

    def test_the_clean_save_passes_although_parsek_race_holds_rival_ids(self):
        res = self.scan(_base_save())
        self.assertEqual(res.status, rp.STATUS_PASS, res.findings)
        self.assertEqual(res.files_skipped_race, 2)
        self.assertEqual(res.files_scanned, 4)
        self.assertEqual(res.ids_scanned, 3)

    def test_a_race_node_in_persistent_sfs_reds_with_its_node_path(self):
        files = _base_save()
        files["persistent.sfs"] = BASE_SFS.replace(
            "\t\tRECORDING_TREE\n",
            "\t\tRIVAL\n\t\t{\n\t\t\tplayerId = %s\n\t\t}\n\t\tRECORDING_TREE\n" % RIVAL,
        ).encode("utf-8")
        hit = self.assert_red(files, rp.WHERE_TEXT, RIVAL, "persistent.sfs")[0]
        self.assertEqual(hit.id_kind, rp.ID_KIND_PLAYER)
        self.assertEqual(hit.context, "GAME/SCENARIO/RIVAL/playerId")
        line = BASE_SFS.count("\n", 0, BASE_SFS.index("\t\tRECORDING_TREE\n")) + 3
        self.assertEqual(hit.line, line)

    def test_a_node_named_by_a_rival_id_reds(self):
        files = _base_save()
        files["persistent.sfs"] = BASE_SFS.replace(
            "\tSCENARIO\n\t{\n\t\tname = Funding",
            "\t%s\n\t{\n\t}\n\tSCENARIO\n\t{\n\t\tname = Funding" % RIVAL_TREE,
        ).encode("utf-8")
        hit = self.assert_red(files, rp.WHERE_TEXT, RIVAL_TREE, "persistent.sfs")[0]
        self.assertEqual(hit.id_kind, rp.ID_KIND_TREE)
        self.assertEqual(hit.context, "GAME/" + RIVAL_TREE)

    def test_a_race_key_value_in_persistent_sfs_reds(self):
        files = _base_save()
        files["persistent.sfs"] = BASE_SFS.replace(
            "vesselName = Kerbal X", "vesselName = Kerbal X\n\t\t\t\tparentRecordingId = %s"
            % RIVAL_REC).encode("utf-8")
        hit = self.assert_red(files, rp.WHERE_TEXT, RIVAL_REC, "persistent.sfs")[0]
        self.assertEqual(hit.id_kind, rp.ID_KIND_RECORDING)
        self.assertEqual(hit.context,
                         "GAME/SCENARIO/RECORDING_TREE/RECORDING/parentRecordingId")

    def test_a_race_file_outside_parsek_race_reds_by_its_name(self):
        files = _base_save()
        name = "Parsek/Recordings/%s.%s.prec" % (RIVAL_REC, HASH)
        files[name] = b"no id in these bytes"
        self.assert_red(files, rp.WHERE_PATH, RIVAL_REC, name)

    def test_a_rival_player_directory_outside_parsek_race_reds(self):
        files = _base_save()
        name = "Parsek/rivals/%s/player.cfg" % RIVAL
        files[name] = b"displayName = Ana\n"
        self.assert_red(files, rp.WHERE_PATH, RIVAL, name)

    def test_a_planted_id_in_a_binary_sidecar_reds(self):
        files = _base_save()
        name = "Parsek/Recordings/%s.prec" % OWN_REC
        files[name] = _psk0(OWN_REC) + _psk0(RIVAL_REC)
        hit = self.assert_red(files, rp.WHERE_BINARY, RIVAL_REC, name)[0]
        self.assertIsNotNone(hit.offset)
        self.assertIsNone(hit.line)

    def test_a_planted_id_inside_a_deflated_snapshot_reds_where_raw_bytes_cannot_see_it(self):
        files = _base_save()
        name = "Parsek/Recordings/%s_ghost.craft" % OWN_REC
        blob = _psn0("PART\n{\n\tname = mk1pod.v2\n\tvesselName = %s\n}\n" % RIVAL)
        self.assertNotIn(RIVAL.encode("utf-8"), blob)  # the class is not caught by bytes
        files[name] = blob
        hit = self.assert_red(files, rp.WHERE_SNAPSHOT, RIVAL, name)[0]
        self.assertEqual((hit.line, hit.context), (4, "PART/vesselName"))

    def test_a_planted_id_in_a_text_sidecar_reds(self):
        files = _base_save()
        name = "Parsek/GameState/events.pgse"
        files[name] = files[name] + ("tag = %s\r\n" % RIVAL_REC).encode("utf-8")
        hit = self.assert_red(files, rp.WHERE_TEXT, RIVAL_REC, name)[0]
        self.assertEqual((hit.line, hit.context), (6, "tag"))

    def test_only_the_exact_parsek_race_directory_is_excluded(self):
        for name in ("Parsek/RaceNotes/x.cfg", "Parsek/Recordings/Race/x.cfg",
                     "parsek/race/x.cfg", "Race/x.cfg"):
            files = _base_save()
            files[name] = ("id = %s\n" % RIVAL).encode("utf-8")
            self.assert_red(files, rp.WHERE_TEXT, RIVAL, name)

    def test_own_ids_are_not_findings(self):
        res = rp.leak_scan_files(_base_save(), rp.collect_rival_ids(SHARED_LISTING, OWN))
        self.assertFalse([f for f in res.findings if f.rival_id in (OWN, OWN_REC)])

    def test_no_rival_id_is_vacuous_not_a_pass(self):
        res = rp.leak_scan_files(_base_save(), rp.RivalIds())
        self.assertEqual(res.status, rp.STATUS_VACUOUS)
        self.assertEqual(rp.leak_scan_files(_base_save(), [""]).status, rp.STATUS_VACUOUS)

    def test_a_save_without_persistent_sfs_is_an_error_not_clean(self):
        files = _base_save()
        del files["persistent.sfs"]
        self.assertEqual(self.scan(files).status, rp.STATUS_ERROR)

    def test_an_undecodable_snapshot_is_an_error_not_clean(self):
        files = _base_save()
        files["Parsek/Recordings/%s_ghost.craft" % OWN_REC] = rp.SNAPSHOT_MAGIC + b"\x00" * 30
        res = self.scan(files)
        self.assertEqual(res.status, rp.STATUS_ERROR)
        self.assertIn("snapshot not decodable", res.errors[0])

    def test_plain_id_iterables_are_accepted(self):
        files = _base_save()
        files["persistent.sfs"] += ("x = %s\n" % RIVAL).encode("utf-8")
        res = rp.leak_scan_files(files, [RIVAL, RIVAL])
        self.assertEqual(res.ids_scanned, 1)
        self.assertEqual(res.findings[0].id_kind, rp.ID_KIND_UNKNOWN)

    def test_the_committed_fixture_is_clean_and_a_plant_in_its_copy_reds(self):
        # A real produced save: fresh rival ids read nothing; one of its own sidecar
        # ids, taken as a rival's, reds in every place that id lives.
        fresh = rp.RivalIds(player_ids=(uuid.uuid4().hex,), recording_ids=(uuid.uuid4().hex,),
                            tree_ids=(uuid.uuid4().hex,))
        res = rp.leak_scan(FIXTURE, fresh)
        self.assertEqual(res.status, rp.STATUS_PASS, res.findings[:3])
        self.assertGreater(res.files_scanned, 100)

        with tempfile.TemporaryDirectory() as tmp:
            save = os.path.join(tmp, "save")
            shutil.copytree(FIXTURE, save)
            rec = sorted(n for n in os.listdir(os.path.join(save, "Parsek", "Recordings"))
                         if n.endswith(".prec"))[0][:-len(".prec")]
            planted = rp.RivalIds(recording_ids=(rec,))
            res = rp.leak_scan(save, planted)
            self.assertEqual(res.status, rp.STATUS_FAIL)
            wheres = {f.where for f in res.findings}
            self.assertTrue({rp.WHERE_PATH, rp.WHERE_TEXT, rp.WHERE_BINARY} <= wheres, wheres)

            # The same plant moved under Parsek/Race/ is no longer a leak.
            race = os.path.join(save, "Parsek", "Race", "rivals", "x")
            os.makedirs(race)
            with open(os.path.join(race, "%s.%s.prec" % (RIVAL_REC, HASH)), "wb") as fh:
                fh.write(_psk0(RIVAL_REC))
            res = rp.leak_scan(save, rp.RivalIds(recording_ids=(RIVAL_REC,)))
            self.assertEqual(res.status, rp.STATUS_PASS, res.findings)
            self.assertEqual(res.files_skipped_race, 1)


def _write_tree(root: str, files: dict) -> None:
    for rel, data in files.items():
        path = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)


def _symlink(test, target: str, link: str, directory: bool) -> None:
    try:
        os.symlink(target, link, target_is_directory=directory)
    except (OSError, NotImplementedError) as exc:  # Windows without the privilege
        test.skipTest("cannot create a symlink here: %s" % exc)


class ReaderCompletenessTests(unittest.TestCase):
    """A save the readers could not list or read whole never reads as clean."""

    LEAK = "Parsek/Recordings/x.cfg"

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name
        self.save = os.path.join(self.tmp, "save")
        files = _base_save()
        files[self.LEAK] = ("parent = %s\n" % RIVAL_REC).encode("utf-8")
        _write_tree(self.save, files)

    def tearDown(self):
        self._tmp.cleanup()

    def unlistable(self, leaf):
        real = os.scandir

        def scandir(path="."):
            if os.path.basename(os.fspath(path)) == leaf:
                raise PermissionError(13, "Permission denied", os.fspath(path))
            return real(path)
        return mock.patch("os.scandir", scandir)

    def test_the_planted_file_reds_when_everything_is_readable(self):
        self.assertEqual(rp.leak_scan(self.save, _ids()).status, rp.STATUS_FAIL)

    def test_an_unlistable_folder_is_an_error_not_a_pass(self):
        with self.unlistable("Recordings"):
            res = rp.leak_scan(self.save, _ids())
        self.assertEqual(res.status, rp.STATUS_ERROR, res)
        self.assertTrue(any("cannot list" in e and "Recordings" in e for e in res.errors))

    def test_an_unlistable_folder_in_both_saves_is_an_equivalence_error(self):
        other = os.path.join(self.tmp, "other")
        shutil.copytree(self.save, other)
        self.assertEqual(rp.save_equivalence(self.save, other).status, rp.STATUS_PASS)
        with self.unlistable("Recordings"):
            res = rp.save_equivalence(self.save, other)
        self.assertEqual(res.status, rp.STATUS_ERROR, res)
        self.assertEqual(sum("cannot list" in e for e in res.errors), 2)

    def test_list_folder_raises_rather_than_return_a_partial_listing(self):
        with self.unlistable("Recordings"):
            with self.assertRaises(rp.FolderReadError) as ctx:
                rp.list_folder(self.save)
        self.assertTrue(ctx.exception.problems)

    def test_a_missing_save_folder_is_an_error(self):
        res = rp.leak_scan(os.path.join(self.tmp, "nowhere"), _ids())
        self.assertEqual(res.status, rp.STATUS_ERROR)
        with self.assertRaises(rp.FolderReadError):
            rp.list_folder(os.path.join(self.tmp, "nowhere"))

    def test_a_file_that_cannot_be_read_is_an_error(self):
        real_open = open

        def failing_open(path, *args, **kwargs):
            if os.fspath(path).endswith("x.cfg"):
                raise PermissionError(13, "Permission denied", os.fspath(path))
            return real_open(path, *args, **kwargs)
        with mock.patch.object(rp, "open", failing_open, create=True):
            res = rp.leak_scan(self.save, _ids())
        self.assertEqual(res.status, rp.STATUS_ERROR, res)
        self.assertTrue(any("cannot read Parsek/Recordings/x.cfg" in e for e in res.errors))

    def test_a_dangling_file_link_is_an_error(self):
        _symlink(self, os.path.join(self.tmp, "gone.cfg"),
                 os.path.join(self.save, "Parsek", "dangling.cfg"), directory=False)
        res = rp.leak_scan(self.save, _ids())
        self.assertEqual(res.status, rp.STATUS_ERROR, res)

    def test_a_symlinked_folder_is_followed_and_its_leak_reds(self):
        outside = os.path.join(self.tmp, "elsewhere")
        recs = os.path.join(self.save, "Parsek", "Recordings")
        shutil.move(recs, outside)
        _symlink(self, outside, recs, directory=True)
        res = rp.leak_scan(self.save, _ids())
        self.assertEqual(res.status, rp.STATUS_FAIL, res)
        self.assertIn(self.LEAK, {f.path for f in res.findings})

    def test_one_folder_reached_twice_through_a_link_is_an_error(self):
        # Recordings linked into Parsek/Race/: read once under the excluded name only,
        # it would hide the leak, so it is an error, never a skip.
        recs = os.path.join(self.save, "Parsek", "Recordings")
        cache = os.path.join(self.save, "Parsek", "Race", "rivals", "zz")
        shutil.move(recs, cache)
        _symlink(self, cache, recs, directory=True)
        res = rp.leak_scan(self.save, _ids())
        self.assertEqual(res.status, rp.STATUS_ERROR, res)
        self.assertTrue(any("already walked" in e for e in res.errors))

    def test_a_link_loop_is_an_error(self):
        _symlink(self, os.path.join(self.save, "Parsek"),
                 os.path.join(self.save, "Parsek", "loop"), directory=True)
        res = rp.leak_scan(self.save, _ids())
        self.assertEqual(res.status, rp.STATUS_ERROR, res)


class LineContextTests(unittest.TestCase):

    def test_brace_forms_and_values(self):
        text = "A\n{\n\tk = 1\n\tB {\n\t\tj = 2\n\t}\n\tC { }\n\tm = 3\n}\n"
        self.assertEqual(rp.configNode_line_contexts(text), [
            "A", "A", "A/k", "A/B", "A/B/j", "A/B", "A/C", "A/m", "A", ""])


def _mask_paths(text: str) -> set:
    """Every (mask path) of a value in ConfigNode text, as save_equivalence spells it."""
    out = set()

    def walk(node, prefix):
        for k, _ in node.values:
            out.add(rp._join(prefix, k))
        for child in node.nodes:
            walk(child, rp._join(prefix, rp._node_label(child)))

    walk(saveparse.parse_sfs(text).root, "")
    return out


class EquivalenceMaskPinTests(unittest.TestCase):
    """The mask is a module constant (Q-13 option (a)): pinned here, never a spec key."""

    PINNED = (
        ("persistent.sfs", "GAME/FLIGHTSTATE/UT", "tolerance", 60.0),
        ("persistent.sfs", "GAME/FLIGHTSTATE/VESSEL/lastUT", "tolerance", 60.0),
        ("persistent.sfs", "GAME/FLIGHTSTATE/VESSEL/lct", "tolerance", 60.0),
        ("persistent.sfs", "GAME/FLIGHTSTATE/VESSEL/met", "tolerance", 60.0),
        ("persistent.sfs", "GAME/FLIGHTSTATE/VESSEL/ORBIT/EPH", "tolerance", 60.0),
        ("persistent.sfs", "GAME/SCENARIO:ProgressTracking/Progress/*/completed",
         "tolerance", 60.0),
        ("persistent.sfs", "GAME/SCENARIO:ParsekScenario/saveStamp", "ignore", 0.0),
    )

    def test_the_mask_is_pinned(self):
        self.assertEqual(tuple((e.file_glob, e.key_glob, e.kind, e.tolerance)
                               for e in rp.EQUIVALENCE_MASK), self.PINNED)

    def test_every_entry_is_well_formed_and_carries_a_written_reason(self):
        self.assertEqual(rp.validate_mask(rp.EQUIVALENCE_MASK), [])
        for e in rp.EQUIVALENCE_MASK:
            self.assertGreater(len(e.reason), 20, e)

    def test_every_entry_matches_a_value_of_a_real_career_save(self):
        with open(os.path.join(FIXTURE, "persistent.sfs"), encoding="utf-8") as fh:
            paths = _mask_paths(fh.read())
        for e in rp.EQUIVALENCE_MASK:
            self.assertTrue(any(rp.fnmatch.fnmatchcase(p, e.key_glob) for p in paths),
                            "%s matches nothing in %s" % (e.key_glob, FIXTURE))

    def test_validate_mask_names_each_shape_fault(self):
        bad = (
            rp.MaskEntry("persistent.sfs", "A/x", "fuzzy", 1.0, "reason long enough here"),
            rp.MaskEntry("persistent.sfs", "A/y", rp.MASK_TOLERANCE, 0.0, "reason"),
            rp.MaskEntry("persistent.sfs", "A/z", rp.MASK_IGNORE, 5.0, "reason"),
            rp.MaskEntry("persistent.sfs", "A/w", rp.MASK_IGNORE, 0.0, "  "),
            rp.MaskEntry("Parsek/*", "A/v", rp.MASK_IGNORE, 0.0, "reason"),
            rp.MaskEntry("persistent.sfs", "A/z", rp.MASK_IGNORE, 0.0, "reason"),
        )
        problems = rp.validate_mask(bad)
        for needle in ("not in", "tolerance > 0", "carries no tolerance", "no written reason",
                       "never compared", "duplicate"):
            self.assertTrue(any(needle in p for p in problems), (needle, problems))

    def test_a_malformed_mask_is_an_error_not_a_pass(self):
        files = _base_save()
        bad = (rp.MaskEntry("persistent.sfs", "A", "fuzzy", 0.0, "r"),)
        self.assertEqual(rp.save_equivalence_files(files, dict(files), bad).status,
                         rp.STATUS_ERROR)


class SaveEquivalenceTests(unittest.TestCase):

    def diff(self, a, b, mask=rp.EQUIVALENCE_MASK):
        return rp.save_equivalence_files(a, b, mask)

    def with_sfs(self, old, new):
        files = _base_save()
        assert old in BASE_SFS, old
        files["persistent.sfs"] = BASE_SFS.replace(old, new).encode("utf-8")
        return files

    def test_two_copies_of_a_committed_fixture_are_equivalent(self):
        with tempfile.TemporaryDirectory() as tmp:
            a = os.path.join(tmp, "a")
            b = os.path.join(tmp, "b")
            shutil.copytree(FIXTURE, a)
            shutil.copytree(FIXTURE, b)
            res = rp.save_equivalence(a, b)
            self.assertEqual(res.status, rp.STATUS_PASS, res.findings[:3])
            self.assertGreater(res.files_compared, 100)
            self.assertEqual(res.masked, ())

    def test_a_one_byte_change_outside_the_mask_reds_in_the_fixture(self):
        with tempfile.TemporaryDirectory() as tmp:
            a = os.path.join(tmp, "a")
            b = os.path.join(tmp, "b")
            shutil.copytree(FIXTURE, a)
            shutil.copytree(FIXTURE, b)
            recs = os.path.join(b, "Parsek", "Recordings")
            prec = sorted(n for n in os.listdir(recs) if n.endswith(".prec"))[0]
            path = os.path.join(recs, prec)
            with open(path, "rb") as fh:
                data = bytearray(fh.read())
            data[-1] ^= 0x01
            with open(path, "wb") as fh:
                fh.write(bytes(data))
            res = rp.save_equivalence(a, b)
            self.assertEqual(res.status, rp.STATUS_FAIL)
            self.assertEqual([(f.kind, f.path) for f in res.findings],
                             [(rp.EQ_BYTES_DIFFER, "Parsek/Recordings/" + prec)])

    def test_a_one_byte_value_change_in_persistent_sfs_reds(self):
        res = self.diff(_base_save(), self.with_sfs("funds = 25000", "funds = 25001"))
        self.assertEqual(res.status, rp.STATUS_FAIL)
        self.assertEqual([(f.kind, f.where, f.a, f.b) for f in res.findings],
                         [(rp.EQ_VALUE_DIFFERS, "GAME/SCENARIO:Funding/funds", "25000", "25001")])

    def test_a_ut_change_inside_tolerance_passes_and_is_listed(self):
        res = self.diff(_base_save(), self.with_sfs("\tUT = 1000.5", "\tUT = 1059.5"))
        self.assertEqual(res.status, rp.STATUS_PASS, res.findings)
        self.assertEqual([(m.where, m.a, m.b) for m in res.masked],
                         [("GAME/FLIGHTSTATE/UT", "1000.5", "1059.5")])

    def test_a_ut_change_outside_tolerance_reds(self):
        res = self.diff(_base_save(), self.with_sfs("\tUT = 1000.5", "\tUT = 1061"))
        self.assertEqual(res.status, rp.STATUS_FAIL)
        self.assertEqual([(f.kind, f.where) for f in res.findings],
                         [(rp.EQ_OUTSIDE_TOLERANCE, "GAME/FLIGHTSTATE/UT")])

    def test_a_masked_key_still_reds_when_it_is_not_a_number(self):
        res = self.diff(_base_save(), self.with_sfs("\tUT = 1000.5", "\tUT = soon"))
        self.assertEqual(res.findings[0].kind, rp.EQ_OUTSIDE_TOLERANCE)
        res = self.diff(self.with_sfs("\tUT = 1000.5", "\tUT = NaN"),
                        self.with_sfs("\tUT = 1000.5", "\tUT = NaN "))
        self.assertEqual(res.status, rp.STATUS_PASS)  # equal text compares equal first

    def test_the_tolerance_applies_only_at_its_own_path(self):
        # 'lastUT' under a node other than FLIGHTSTATE/VESSEL is compared exactly.
        a = self.with_sfs("funds = 25000", "funds = 25000\n\t\tlastUT = 10")
        b = self.with_sfs("funds = 25000", "funds = 25000\n\t\tlastUT = 11")
        res = self.diff(a, b)
        self.assertEqual([(f.kind, f.where) for f in res.findings],
                         [(rp.EQ_VALUE_DIFFERS, "GAME/SCENARIO:Funding/lastUT")])

    def test_each_ut_derived_entry_absorbs_a_small_drift(self):
        b = self.with_sfs("lct = 900", "lct = 905")
        b["persistent.sfs"] = (b["persistent.sfs"].decode("utf-8")
                               .replace("lastUT = 1000.5", "lastUT = 1003")
                               .replace("met = 100.5", "met = 98")
                               .replace("EPH = 1000.5", "EPH = 1003")).encode("utf-8")
        res = self.diff(_base_save(), b)
        self.assertEqual(res.status, rp.STATUS_PASS, res.findings)
        self.assertEqual(len(res.masked), 4)

    def test_the_save_stamp_differs_by_nature_and_is_ignored(self):
        res = self.diff(_base_save(), self.with_sfs(
            "saveStamp = 11111111111111111111111111111111",
            "saveStamp = 22222222222222222222222222222222"))
        self.assertEqual(res.status, rp.STATUS_PASS, res.findings)
        self.assertEqual(len(res.masked), 1)
        # Present on one side only is still a difference.
        res = self.diff(_base_save(), self.with_sfs(
            "\t\tsaveStamp = 11111111111111111111111111111111\n", ""))
        self.assertEqual(res.findings[0].kind, rp.EQ_VALUE_COUNT)

    def test_a_scenario_node_is_aligned_by_name_not_position(self):
        swapped = BASE_SFS
        funding = "\tSCENARIO\n\t{\n\t\tname = Funding\n\t\tfunds = 25000\n\t}\n"
        swapped = swapped.replace(funding, "")
        swapped = swapped.replace("\t\t}\n\t}\n}\n", "\t\t}\n\t}\n" + funding + "}\n")
        files = _base_save()
        files["persistent.sfs"] = swapped.encode("utf-8")
        self.assertEqual(self.diff(_base_save(), files).status, rp.STATUS_PASS)

    def test_an_added_node_reds(self):
        res = self.diff(_base_save(), self.with_sfs(
            "\t\tRECORDING_TREE\n", "\t\tRIVAL_STORE\n\t\t{\n\t\t}\n\t\tRECORDING_TREE\n"))
        self.assertEqual([(f.kind, f.where, f.a, f.b) for f in res.findings],
                         [(rp.EQ_NODE_MISSING, "GAME/SCENARIO:ParsekScenario/RIVAL_STORE",
                           None, "present")])

    def test_a_second_vessel_reds_with_its_index(self):
        vessel_end = "\t\t\t}\n\t\t}\n\t}\n\tSCENARIO\n\t{\n\t\tname = Funding"
        res = self.diff(_base_save(), self.with_sfs(
            vessel_end, "\t\t\t}\n\t\t}\n\t\tVESSEL\n\t\t{\n\t\t}\n\t}\n"
            "\tSCENARIO\n\t{\n\t\tname = Funding"))
        self.assertEqual([(f.kind, f.where) for f in res.findings],
                         [(rp.EQ_NODE_MISSING, "GAME/FLIGHTSTATE/VESSEL[1]")])

    def test_a_file_on_one_side_only_reds(self):
        b = _base_save()
        b["Parsek/Recordings/extra.prec"] = _psk0("extra")
        res = self.diff(_base_save(), b)
        self.assertEqual([(f.kind, f.path, f.a, f.b) for f in res.findings],
                         [(rp.EQ_FILE_MISSING, "Parsek/Recordings/extra.prec", None, "present")])

    def test_parsek_race_and_files_outside_parsek_are_not_compared(self):
        b = _base_save()
        b["Parsek/Race/local.cfg"] = b"lastSyncRealTime = later\n"
        b["Parsek/Race/rivals/other/x.prec"] = b"more"
        b["persistent.loadmeta"] = b"anything"
        b["Ships/VAB/x.craft"] = b"anything"
        self.assertEqual(self.diff(_base_save(), b).status, rp.STATUS_PASS)

    def test_a_snapshot_is_compared_by_its_inflated_tree(self):
        name = "Parsek/Recordings/%s_ghost.craft" % OWN_REC
        b = _base_save()
        b[name] = _psn0("PART\n{\n\tname = mk1pod.v3\n}\n")
        res = self.diff(_base_save(), b)
        self.assertEqual([(f.kind, f.where) for f in res.findings],
                         [(rp.EQ_VALUE_DIFFERS, "PART/name")])

    def test_a_text_sidecar_is_compared_as_a_tree(self):
        name = "Parsek/GameState/events.pgse"
        b = _base_save()
        b[name] = b[name].replace(b"ut = 5", b"ut = 6")
        res = self.diff(_base_save(), b)
        self.assertEqual([(f.kind, f.path, f.where) for f in res.findings],
                         [(rp.EQ_VALUE_DIFFERS, name, "GAME_STATE_EVENT/ut")])

    def test_a_truncated_save_is_a_parse_error_finding(self):
        b = _base_save()
        b["persistent.sfs"] = BASE_SFS[:-3].encode("utf-8")
        res = self.diff(_base_save(), b)
        self.assertEqual(res.status, rp.STATUS_FAIL)
        self.assertEqual(res.findings[0].kind, rp.EQ_PARSE_ERROR)

    def test_a_missing_persistent_sfs_is_an_error(self):
        b = _base_save()
        del b["persistent.sfs"]
        self.assertEqual(self.diff(_base_save(), b).status, rp.STATUS_ERROR)

    def test_an_undecodable_snapshot_is_an_error(self):
        b = _base_save()
        b["Parsek/Recordings/%s_ghost.craft" % OWN_REC] = rp.SNAPSHOT_MAGIC + b"\x01" * 30
        self.assertEqual(self.diff(_base_save(), b).status, rp.STATUS_ERROR)

    def test_an_explicit_mask_can_ignore_a_key_the_pinned_one_compares(self):
        b = self.with_sfs("funds = 25000", "funds = 99")
        mask = rp.EQUIVALENCE_MASK + (rp.MaskEntry(
            "persistent.sfs", "GAME/SCENARIO:Funding/funds", rp.MASK_IGNORE, 0.0,
            "test-only: proves the mask parameter is honoured"),)
        self.assertEqual(self.diff(_base_save(), b).status, rp.STATUS_FAIL)
        self.assertEqual(self.diff(_base_save(), b, mask).status, rp.STATUS_PASS)



def _marked(files: dict, player: str) -> dict:
    """``files`` with the race mark (R1-03) naming ``player`` in persistent.sfs."""
    out = dict(files)
    text = out["persistent.sfs"].decode("utf-8")
    mark = ("\tSCENARIO\n\t{\n\t\tname = %s\n\t\traceId = race_1\n\t\t%s = %s\n\t}\n}\n"
            % (rp.RACE_MARK_SCENARIO, rp.RACE_MARK_PLAYER_KEY, player))
    out["persistent.sfs"] = (text[:text.rstrip().rfind("}")] + mark).encode("utf-8")
    return out


class RaceParseRowTests(unittest.TestCase):
    """Multiplayer T-38: the raceParse row's spec block, the own-player rule and the
    evaluation over every save a race lane produced."""

    ARMED = {"race": {"gating": True}}

    def evaluate(self, *saves, listing=SHARED_LISTING, expectations=None):
        inputs = [rp.RaceSaveInput(label, files) for label, files in saves]
        return rp.evaluate_race_parse(self.ARMED if expectations is None else expectations,
                                      listing, (), inputs)

    def test_the_block_validates_and_arms_only_on_the_literal_true(self):
        self.assertEqual([], rp.validate_race_expectations({}))
        self.assertEqual([], rp.validate_race_expectations({"gating": True}))
        self.assertEqual([], rp.validate_race_expectations({"gating": False}))
        for bad, needle in (([], "must be a table"), ({"gate": True}, "unknown key 'gate'"),
                            ({"gating": "true"}, "must be true or false"),
                            ({"gating": 1}, "must be true or false")):
            with self.subTest(bad=bad):
                errors = rp.validate_race_expectations(bad)
                self.assertTrue(any(needle in e for e in errors), errors)
        self.assertTrue(rp.race_gating_armed({"race": {"gating": True}}))
        for unarmed in ({}, None, {"race": {}}, {"race": {"gating": 1}},
                        {"race": {"gating": False}}, {"race": True}):
            with self.subTest(unarmed=unarmed):
                self.assertFalse(rp.race_gating_armed(unarmed))

    def test_the_own_player_comes_from_the_race_mark(self):
        text = _marked(_base_save(), OWN)["persistent.sfs"].decode("utf-8")
        self.assertEqual((OWN, ""), rp.own_player_id(text))
        self.assertEqual((None, ""), rp.own_player_id(BASE_SFS))  # in no race
        two = text.replace("raceId = race_1", "raceId = race_1\n\t\tplayerId = %s" % RIVAL)
        empty = text.replace("playerId = %s" % OWN, "playerId =")
        twice = text.replace("\tSCENARIO\n\t{\n\t\tname = %s" % rp.RACE_MARK_SCENARIO,
                             "\tSCENARIO\n\t{\n\t\tname = %s\n\t}\n\tSCENARIO\n\t{\n"
                             "\t\tname = %s" % (rp.RACE_MARK_SCENARIO, rp.RACE_MARK_SCENARIO))
        for bad, needle in ((None, "no persistent.sfs"), (text[:len(text) // 2], "does not parse"),
                            (two, "2 non-empty playerId"), (empty, "0 non-empty playerId"),
                            (twice, "2 SCENARIO nodes")):
            with self.subTest(needle=needle):
                own, problem = rp.own_player_id(bad)
                self.assertIsNone(own)
                self.assertIn(needle, problem)

    def test_a_clean_save_passes_and_its_own_ids_are_not_rivals(self):
        res = self.evaluate(("a", _marked(_base_save(), OWN)))
        self.assertEqual(rp.STATUS_PASS, res.status, res.mismatches)
        self.assertFalse(res.leaked or res.unevaluable)
        (row,) = res.saves
        self.assertEqual((OWN, 3), (row.own_player, row.ids_scanned))
        self.assertEqual(2, row.files_skipped_race)  # the rival cache and local.cfg

    def test_a_rival_id_in_a_sidecar_is_a_leak(self):
        files = _marked(_base_save(), OWN)
        files["Parsek/Recordings/%s.prec" % RIVAL_REC] = _psk0(RIVAL_REC)
        res = self.evaluate(("a", files))
        self.assertEqual(rp.STATUS_FAIL, res.status)
        self.assertTrue(res.leaked)
        self.assertFalse(res.unevaluable)
        self.assertTrue(any("leak in save a" in m and RIVAL_REC in m for m in res.mismatches),
                        res.mismatches)

    def test_each_save_sees_the_other_players_as_rivals(self):
        # Two saves of one lane: B's own recording id is a rival id in A's save.
        b_files = _marked(_base_save(), RIVAL)
        a_files = _marked(_base_save(), OWN)
        a_files["Parsek/GameState/leak.txt"] = ("ref = %s\n" % RIVAL_REC).encode("utf-8")
        res = self.evaluate(("a", a_files), ("b", b_files))
        self.assertEqual(rp.STATUS_FAIL, res.status)
        a_row, b_row = res.saves
        self.assertEqual([RIVAL_REC], [f.rival_id for f in a_row.findings])
        # B's save holds OWN_REC (the base save's recording), which is A's id.
        self.assertEqual({OWN_REC}, {f.rival_id for f in b_row.findings})

    def test_a_save_in_no_race_treats_every_player_as_a_rival(self):
        res = self.evaluate(("boot", _base_save()))
        (row,) = res.saves
        self.assertIsNone(row.own_player)
        self.assertEqual(rp.STATUS_FAIL, res.status)  # OWN_REC is in it, and it is foreign here
        self.assertIn(OWN_REC, {f.rival_id for f in row.findings})

    def test_the_harness_written_analysis_folder_is_skipped(self):
        files = _marked(_base_save(), OWN)
        files["analysis/run.analysis.json"] = ('{"id": "%s"}' % RIVAL_REC).encode("utf-8")
        res = self.evaluate(("a", files))
        self.assertEqual(rp.STATUS_PASS, res.status, res.mismatches)
        self.assertEqual(1, res.saves[0].files_skipped_harness)

    def test_a_scan_that_cannot_prove_anything_is_unevaluable(self):
        own_only = tuple(p for p in SHARED_LISTING if RIVAL not in p)
        cases = {
            "folder": rp.evaluate_race_parse(self.ARMED, None, ("cannot list x",),
                                             [rp.RaceSaveInput("a", _marked(_base_save(), OWN))]),
            "no-save": self.evaluate(("a", None)),
            "no-sfs": self.evaluate(("a", {"Parsek/x.txt": b"x"})),
            "two-ids": self.evaluate(("a", _marked(_marked(_base_save(), OWN), RIVAL))),
            "vacuous": self.evaluate(("a", _marked(_base_save(), OWN)), listing=own_only),
            "nothing": rp.evaluate_race_parse(self.ARMED, SHARED_LISTING, (), []),
        }
        for name, res in cases.items():
            with self.subTest(case=name):
                self.assertIn(res.status, (rp.STATUS_ERROR, rp.STATUS_VACUOUS))
                self.assertTrue(res.unevaluable)
                self.assertFalse(res.leaked)
                self.assertTrue(res.mismatches)
        self.assertEqual(rp.STATUS_VACUOUS, cases["vacuous"].status)

    def test_a_leak_wins_over_an_unreadable_part(self):
        files = _marked(_base_save(), OWN)
        files["Parsek/Recordings/%s.prec" % RIVAL_REC] = _psk0(RIVAL_REC)
        res = self.evaluate(("a", files), ("b", None))
        self.assertEqual(rp.STATUS_FAIL, res.status)
        self.assertTrue(res.leaked)
        self.assertFalse(res.unevaluable)

    def test_the_row_dict_caps_findings_but_keeps_the_count(self):
        files = _marked(_base_save(), OWN)
        for i in range(rp.ROW_FINDINGS_CAP + 5):
            files["Parsek/GameState/leak%02d.txt" % i] = RIVAL_REC.encode("utf-8")
        row = self.evaluate(("a", files)).saves[0].to_dict()
        self.assertEqual(rp.ROW_FINDINGS_CAP + 5, row["findingCount"])
        self.assertEqual(rp.ROW_FINDINGS_CAP, len(row["findings"]))


if __name__ == "__main__":
    unittest.main()
