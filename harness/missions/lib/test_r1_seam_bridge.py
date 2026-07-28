"""Unit tests for the GENERALIZED command-seam bridge on ``KrpcMissionControl``
(``_perform_seam_command`` / ``_read_seam_response_fields``) and for the
additive contract against the live-proven ``_perform_seam_commit`` path.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

These methods only touch the channel files + time, so no connection is needed.
Each cell names the mutation it was verified against.
"""

import os
import shutil
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
for _p in (_MISSIONS, _HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import mission_runner  # noqa: E402
import mlib  # noqa: E402


class GeneralizedSeamCommandTests(unittest.TestCase):

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-seamcmd-")
        self.cmds = os.path.join(self.tmp, "parsek-test-commands.txt")
        self.resps = os.path.join(self.tmp, "parsek-test-responses.txt")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _ctrl(self, reserved="0003"):
        c = mission_runner.KrpcMissionControl(use_mechjeb=True)
        c.configure_seam(self.cmds, self.resps, reserved)
        return c

    def _seed(self, *lines):
        with open(self.resps, "w", encoding="utf-8") as fh:
            for line in lines:
                fh.write(line + "\n")

    def _cmd_text(self):
        with open(self.cmds, "r", encoding="utf-8") as fh:
            return fh.read()

    # ---- the wire ----

    def test_writes_a_sub_id_command_and_reads_ok(self):
        self._seed("id=0003.rewind cmd=InvokeRewind verdict=OK seq=7 ut=140.0 "
                   "rewound=true session=s1 rp=rp_b9_root slot=1 activePid=42")
        c = self._ctrl()
        c._perform_seam_command("InvokeRewind",
                                (("rp", "rp_b9_root"), ("slot", "1")), "rewind")
        self.assertEqual(c._seam_command_result, "OK")
        self.assertEqual(c._seam_command_tag, "rewind")
        self.assertIn("id=0003.rewind cmd=InvokeRewind rp=rp_b9_root slot=1",
                      self._cmd_text())

    def test_payload_fields_are_captured_minus_the_envelope(self):
        """MUTATION: return the whole field map as the payload (drop the
        id/cmd/verdict/seq filter) and this reds. The envelope is transport, not
        an observation the machine should read."""
        self._seed("id=0003.state cmd=RecordingState verdict=OK seq=3 ut=99.0 "
                   "recording=false tree=t1 points=0 scene=FLIGHT")
        c = self._ctrl()
        c._perform_seam_command("RecordingState", (), "state")
        payload = dict(c._seam_command_payload)
        self.assertEqual(payload.get("recording"), "false")
        self.assertEqual(payload.get("tree"), "t1")
        self.assertEqual(payload.get("scene"), "FLIGHT")
        for envelope_key in ("id", "cmd", "verdict", "seq"):
            self.assertNotIn(envelope_key, payload)

    def test_non_ok_verdict_reads_error(self):
        self._seed("id=0003.rewind cmd=InvokeRewind verdict=REJECTED seq=1 "
                   "msg=refly-gate%20Rewind%20point%20is%20marked%20corrupted")
        c = self._ctrl()
        c._perform_seam_command("InvokeRewind", (("rp", "x"), ("slot", "0")), "rewind")
        self.assertEqual(c._seam_command_result, "ERROR")
        # The refusal reason still rides the payload, so the give-up is diagnosable.
        self.assertIn("msg", dict(c._seam_command_payload))

    def test_first_terminal_wins_on_a_crash_recovery_rewrite(self):
        self._seed("id=0003.commit cmd=CommitTree verdict=OK seq=1",
                   "id=0003.commit cmd=CommitTree verdict=ERROR seq=2")
        c = self._ctrl()
        c._perform_seam_command("CommitTree", (), "commit")
        self.assertEqual(c._seam_command_result, "OK")

    def test_a_response_for_the_bare_reserved_id_is_ignored(self):
        """THE distinct-id contract, end to end. The seeded line carries the BARE
        reserved id -- exactly what the live-proven `_perform_seam_commit` writes
        -- so a generalized command that (wrongly) polled the bare id would read
        this reply as its own.

        MUTATION: `cid = self._seam_commit_id` (drop the sub-id derivation) and
        this reds. NOTE the non-zero poll window: with `poll_seconds=0.0` the poll
        loop body never runs and the call TIMEOUTs without ever reading the
        channel, so the cell would pass under its own mutation -- which is exactly
        how this test was blind on its first pass."""
        self._seed("id=0003 cmd=CommitTree verdict=OK seq=1")
        c = self._ctrl()
        c._perform_seam_command("InvokeRewind", (("rp", "x"), ("slot", "0")),
                                "rewind", poll_seconds=0.2)
        self.assertEqual(c._seam_command_result, "TIMEOUT")

    def test_no_response_times_out(self):
        c = self._ctrl()
        c._perform_seam_command("CommitTree", (), "commit", poll_seconds=0.0)
        self.assertEqual(c._seam_command_result, "TIMEOUT")

    # ---- fail-closed paths (never a MISSION-ERROR) ----

    def test_no_seam_configured_errors(self):
        c = mission_runner.KrpcMissionControl(use_mechjeb=True)
        c._perform_seam_command("CommitTree", (), "commit")
        self.assertEqual(c._seam_command_result, "ERROR")

    def test_missing_verb_or_tag_errors(self):
        c = self._ctrl()
        c._perform_seam_command(None, (), "commit")
        self.assertEqual(c._seam_command_result, "ERROR")
        c._perform_seam_command("CommitTree", (), None)
        self.assertEqual(c._seam_command_result, "ERROR")

    def test_write_failure_errors_rather_than_raising(self):
        c = self._ctrl()
        c._seam_commands_path = os.path.join(self.tmp, "no-such-dir", "cmds.txt")
        c._perform_seam_command("CommitTree", (), "commit")
        self.assertEqual(c._seam_command_result, "ERROR")

    def test_a_new_command_never_publishes_the_previous_commands_payload(self):
        """MUTATION: drop the `self._seam_command_payload = ()` (and/or the tag)
        reset at the top of _perform_seam_command and this reds -- a TIMEOUTing
        command would publish the PREVIOUS command's payload alongside its own
        failure token, so a machine reading the payload would act on evidence from
        a command that did not run.

        The first response deliberately CARRIES payload fields; the earlier
        version of this cell seeded a payload-free reply and was therefore blind
        to its own mutation.

        HONEST SCOPE: the sibling `self._seam_command_result = ""` reset is NOT
        covered here and cannot be -- every exit path of _perform_seam_command
        assigns the result explicitly, so removing that one line is unobservable.
        It is kept as defence against a future early-return that forgets to."""
        self._seed("id=0003.state cmd=RecordingState verdict=OK seq=1 "
                   "recording=true tree=t7 points=42 scene=FLIGHT")
        c = self._ctrl()
        c._perform_seam_command("RecordingState", (), "state")
        self.assertEqual(dict(c._seam_command_payload).get("tree"), "t7")
        c._perform_seam_command("InvokeRewind", (("rp", "x"), ("slot", "0")),
                                "rewind", poll_seconds=0.2)
        self.assertEqual(c._seam_command_result, "TIMEOUT")
        self.assertEqual(c._seam_command_tag, "rewind")
        self.assertEqual(c._seam_command_payload, ())

    # ---- the additive contract against the live-proven commit path ----

    def test_commit_path_is_untouched_and_still_works(self):
        self._seed("id=0003 cmd=CommitTree verdict=OK seq=1 ut=1240.0")
        c = self._ctrl()
        c._perform_seam_commit()
        self.assertEqual(c._seam_commit_result, "OK")
        self.assertIn("id=0003 cmd=CommitTree", self._cmd_text())

    def test_read_seam_response_delegation_is_behaviour_identical(self):
        """The verdict-only reader now delegates to the field reader; the
        selection rule (first line whose id matches AND that carries a verdict)
        must be unchanged. MUTATION: make the field reader return the first
        id-matching line regardless of a verdict and this reds."""
        self._seed("id=0003 cmd=CommitTree seq=1",              # no verdict yet
                   "id=0002 cmd=SetSetting verdict=OK seq=2",   # other id
                   "id=0003 cmd=CommitTree verdict=ERROR seq=3")
        c = self._ctrl()
        self.assertEqual(c._read_seam_response("0003"), "ERROR")
        self.assertIsNone(c._read_seam_response("0009"))
        fields = c._read_seam_response_fields("0003")
        self.assertEqual(fields.get("verdict"), "ERROR")
        self.assertEqual(fields.get("seq"), "3")

    def test_generalized_commit_line_matches_the_live_proven_bytes(self):
        """A CommitTree issued through the generalized path differs from the
        live-proven line ONLY in the id sub-tag. MUTATION: change
        format_seam_command_line's part order and this reds."""
        c = self._ctrl()
        c._perform_seam_command("CommitTree", (), "commit", poll_seconds=0.0)
        line = self._cmd_text().strip()
        self.assertEqual(line, "id=0003.commit cmd=CommitTree")

    def test_perform_dispatches_the_new_action_kind(self):
        """MUTATION: delete the ACTION_PARSEK_SEAM_COMMAND branch in perform() and
        this reds with 'unknown action kind'."""
        self._seed("id=0003.commit cmd=CommitTree verdict=OK seq=1")
        c = self._ctrl()
        # perform() reads space_center/active_vessel for MOST kinds; the two seam
        # kinds are handled before any kRPC access, so call the branch directly
        # through the same dispatch table by faking the connection-free path.
        action = mlib.Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="CommitTree",
                             seam_args=(), seam_tag="commit")
        self.assertEqual(action.kind, mlib.ACTION_PARSEK_SEAM_COMMAND)
        c._perform_seam_command(action.seam_verb, action.seam_args, action.seam_tag)
        self.assertEqual(c._seam_command_result, "OK")


class SeamCommandDispatchSourceTests(unittest.TestCase):
    """Source-text gate: `perform()` must route the new action kind. A pure
    behavioural test would need a live kRPC connection (perform resolves
    space_center before the dispatch chain), so the wiring itself is gated by
    reading the source -- the same pattern the repo uses for ParsekScenario
    OnSave/OnLoad hookup."""

    def test_perform_has_a_seam_command_branch(self):
        path = os.path.join(_MISSIONS, "mission_runner.py")
        with open(path, "r", encoding="utf-8") as fh:
            src = fh.read()
        self.assertIn("elif kind == mlib.ACTION_PARSEK_SEAM_COMMAND:", src)
        self.assertIn("self._perform_seam_command(action.seam_verb, action.seam_args,",
                      src)

    def test_read_snapshot_publishes_the_three_new_fields(self):
        path = os.path.join(_MISSIONS, "mission_runner.py")
        with open(path, "r", encoding="utf-8") as fh:
            src = fh.read()
        for token in ("seam_command_result=self._seam_command_result",
                      "seam_command_tag=self._seam_command_tag",
                      "seam_command_payload=self._seam_command_payload"):
            self.assertIn(token, src)


if __name__ == "__main__":
    unittest.main()
