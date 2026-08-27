"""Unit tests for the GENERALIZED command-seam bridge on ``KrpcMissionControl``
(``_perform_seam_command`` / ``_read_seam_response_fields``) and for the
additive contract against the live-proven ``_perform_seam_commit`` path.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

These methods only touch the channel files + time, so no connection is needed.
Each cell names the mutation it was verified against.
"""

import ast
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
    """Source-derived gate: `perform()` must route the new action kind, and must
    route it ABOVE the active-vessel resolve. A pure behavioural test would need a
    live kRPC connection (perform resolves space_center for most kinds), so the
    wiring itself is gated by reading the source -- the same pattern the repo uses
    for ParsekScenario OnSave/OnLoad hookup.

    DERIVED WITH AST, NOT REGEX (this repo's rule, learned three times): the
    earlier cut asserted the literal string ``elif kind ==
    mlib.ACTION_PARSEK_SEAM_COMMAND:`` and therefore pinned the branch's KEYWORD
    as well as its existence -- so hoisting the dispatch above the
    ``sc.active_vessel`` resolve (an `if` at the top of the method, not an `elif`
    in the chain) red the gate while the wiring it guards was strictly more
    correct. A string match also reads a COMMENT mentioning the constant as if it
    were code. The parse below asks the two questions that actually matter: is the
    constant compared anywhere in ``perform``, and is the comparison BEFORE the
    line that resolves the active vessel."""

    def _perform_fn(self):
        path = os.path.join(_MISSIONS, "mission_runner.py")
        with open(path, "r", encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        for node in ast.walk(tree):
            if isinstance(node, ast.ClassDef) and node.name == "KrpcMissionControl":
                for sub in node.body:
                    if isinstance(sub, ast.FunctionDef) and sub.name == "perform":
                        return sub
        self.fail("KrpcMissionControl.perform not found")

    @staticmethod
    def _action_compare_lines(fn, const_name):
        """Line numbers of every ``... == mlib.<const_name>`` comparison inside
        ``fn``. Attribute-matched off the AST, so a comment naming the constant
        contributes nothing."""
        lines = []
        for node in ast.walk(fn):
            if not isinstance(node, ast.Compare):
                continue
            for operand in [node.left] + list(node.comparators):
                if (isinstance(operand, ast.Attribute)
                        and operand.attr == const_name
                        and isinstance(operand.value, ast.Name)
                        and operand.value.id == "mlib"):
                    lines.append(node.lineno)
        return sorted(lines)

    @staticmethod
    def _active_vessel_line(fn):
        """Line number of the FIRST ``sc.active_vessel`` read inside ``fn`` -- the
        boundary every vessel-free action must be dispatched above.

        MIN over every occurrence, not the first thing ``ast.walk`` happens to
        yield: walk is breadth-first over an unordered body, so on a function with
        two such reads it can return the LATER one, and a boundary that drifts
        downward silently widens the window this cell exists to close."""
        lines = [node.lineno for node in ast.walk(fn)
                 if (isinstance(node, ast.Attribute)
                     and node.attr == "active_vessel"
                     and isinstance(node.value, ast.Name)
                     and node.value.id == "sc")]
        return min(lines) if lines else None

    @staticmethod
    def _attr_chain(node):
        """``action.seam_verb`` -> "action.seam_verb"; anything else -> None. Used
        to read what an argument IS rather than that one was passed."""
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name):
            return "%s.%s" % (node.value.id, node.attr)
        return None

    def _vessel_free_fn(self):
        path = os.path.join(_MISSIONS, "mission_runner.py")
        with open(path, "r", encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        for node in ast.walk(tree):
            if isinstance(node, ast.ClassDef) and node.name == "KrpcMissionControl":
                for sub_node in node.body:
                    if (isinstance(sub_node, ast.FunctionDef)
                            and sub_node.name == "_perform_vessel_free"):
                        return sub_node
        self.fail("KrpcMissionControl._perform_vessel_free not found")

    def test_perform_has_a_seam_command_branch(self):
        fn = self._vessel_free_fn()
        self.assertTrue(
            self._action_compare_lines(fn, "ACTION_PARSEK_SEAM_COMMAND"),
            "_perform_vessel_free no longer compares against "
            "mlib.ACTION_PARSEK_SEAM_COMMAND; a generalized seam command would "
            "reach its unhandled-kind raise")
        calls = [n for n in ast.walk(fn)
                 if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                 and n.func.attr == "_perform_seam_command"]
        self.assertEqual(1, len(calls),
                         "expected exactly one _perform_seam_command call site")

    def test_the_seam_command_call_threads_the_verb_args_and_tag(self):
        """THE ARGUMENT THREADING, re-pinned after the AST conversion dropped it.
        The old string gate matched the literal call text and therefore checked
        WHAT was passed; asserting only that a call exists would still pass on
        `self._perform_seam_command(None, None, None)`, which silently resolves
        every command to a fail-closed ERROR token the machine gives up on. Read
        the Call node's arguments instead."""
        fn = self._vessel_free_fn()
        call = [n for n in ast.walk(fn)
                if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                and n.func.attr == "_perform_seam_command"][0]
        chains = [self._attr_chain(a) for a in call.args]
        chains += [self._attr_chain(kw.value) for kw in call.keywords]
        for want in ("action.seam_verb", "action.seam_args", "action.seam_tag"):
            self.assertIn(want, chains,
                          "_perform_seam_command must be handed %s; got %s. A "
                          "command whose verb/args/tag are not threaded resolves "
                          "to a fail-closed ERROR the machine gives up on."
                          % (want, chains))

    def test_the_vessel_free_set_is_the_single_authority_for_the_hoist(self):
        """THE HOIST INVARIANT, made structural. `sc.active_vessel` RAISES in
        SPACECENTER (and mid-scene-reload), and `perform()` is NOT wrapped by the
        fly loop, so a raise there ends the mission as a MISSION-ERROR.
        `mlib.VESSEL_FREE_ACTION_KINDS` is what decides which actions dodge that
        resolve - not a hand-maintained run of positional special cases, which is
        what this replaced and which had no way to state its own rule.

        Three things are pinned: perform() consults the SET (by name), it does so
        ABOVE the resolve, and every member of the set has a branch in
        `_perform_vessel_free`. MUTATION: add a kind to the set without a branch,
        or move the set check below the resolve, and this reds."""
        fn = self._perform_fn()
        boundary = self._active_vessel_line(fn)
        self.assertIsNotNone(boundary, "perform() no longer resolves sc.active_vessel")
        set_reads = [n.lineno for n in ast.walk(fn)
                     if isinstance(n, ast.Attribute)
                     and n.attr == "VESSEL_FREE_ACTION_KINDS"
                     and isinstance(n.value, ast.Name) and n.value.id == "mlib"]
        self.assertTrue(set_reads,
                        "perform() no longer consults mlib.VESSEL_FREE_ACTION_KINDS; "
                        "the hoist has gone back to positional special cases")
        self.assertTrue(
            all(ln < boundary for ln in set_reads),
            "mlib.VESSEL_FREE_ACTION_KINDS is consulted at line(s) %s, at or below "
            "the sc.active_vessel resolve at line %d; it must be read ABOVE it or "
            "a SPACECENTER-phase mission raises out of perform()"
            % (set_reads, boundary))
        # Every member is actually handled, so the set cannot promise a dodge the
        # dispatcher does not deliver.
        free_fn = self._vessel_free_fn()
        handled = set()
        for const in ("ACTION_SET_ROSTER_WATCH", "ACTION_SET_SIBLING_WATCH",
                      "ACTION_PARSEK_COMMIT_TREE", "ACTION_PARSEK_SEAM_COMMAND",
                      "ACTION_LAUNCH_VESSEL"):
            if self._action_compare_lines(free_fn, const):
                handled.add(getattr(mlib, const))
        self.assertEqual(set(mlib.VESSEL_FREE_ACTION_KINDS), handled,
                         "mlib.VESSEL_FREE_ACTION_KINDS and the branches in "
                         "_perform_vessel_free disagree")

    def test_every_vessel_free_kind_performs_with_no_active_vessel(self):
        """THE BEHAVIOURAL HALF of the cell above, and the one that would have
        caught the original defect without reading any source: drive each member
        against a connection whose `active_vessel` RAISES, exactly as kRPC does in
        SPACECENTER, and require that none of them raises out of perform()."""

        class _Boom(object):
            @property
            def active_vessel(self):
                raise RuntimeError("no active vessel (SPACECENTER)")

            def launch_vessel(self, *a, **kw):
                self.launched = (a, kw)

        class _Conn(object):
            def __init__(self):
                self.space_center = _Boom()

        tmp = tempfile.mkdtemp(prefix="parsek-vfree-")
        try:
            cmds = os.path.join(tmp, "c.txt")
            resps = os.path.join(tmp, "r.txt")
            with open(resps, "w", encoding="utf-8") as fh:
                fh.write("id=0003.t cmd=RecordingState verdict=OK seq=1\n")
                fh.write("id=0003 cmd=CommitTree verdict=OK seq=2\n")
            control = mission_runner.KrpcMissionControl(use_mechjeb=False)
            control.configure_seam(cmds, resps, "0003")
            control._conn = _Conn()
            actions = {
                mlib.ACTION_SET_ROSTER_WATCH: mlib.Action(
                    mlib.ACTION_SET_ROSTER_WATCH, text="Jeb"),
                mlib.ACTION_SET_SIBLING_WATCH: mlib.Action(
                    mlib.ACTION_SET_SIBLING_WATCH, text="Booster"),
                mlib.ACTION_PARSEK_COMMIT_TREE: mlib.Action(
                    mlib.ACTION_PARSEK_COMMIT_TREE),
                mlib.ACTION_PARSEK_SEAM_COMMAND: mlib.Action(
                    mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="RecordingState",
                    seam_args=(), seam_tag="t"),
                mlib.ACTION_LAUNCH_VESSEL: mlib.Action(
                    mlib.ACTION_LAUNCH_VESSEL, text="Jumping Flea",
                    launch_site="LaunchPad"),
            }
            self.assertEqual(set(mlib.VESSEL_FREE_ACTION_KINDS), set(actions),
                             "a kind joined VESSEL_FREE_ACTION_KINDS without a "
                             "behavioural cell proving it survives a raising "
                             "active_vessel")
            for kind, action in sorted(actions.items()):
                control.perform(action)          # must not raise
            # And the two that reach the game actually did their work.
            self.assertEqual("OK", control._seam_command_result)
            self.assertEqual(("VAB", "Jumping Flea", "LaunchPad"),
                             control._conn.space_center.launched[0])
            # A kind NOT in the set still hits the resolve, so the set is a real
            # boundary rather than a blanket try/except.
            with self.assertRaises(RuntimeError):
                control.perform(mlib.Action(mlib.ACTION_CUT_THROTTLE, 0.0))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_the_vessel_lost_snapshot_carries_the_seam_terminal(self):
        """CRITICAL, and it was a LATENT DEADLOCK rather than a lost diagnostic. A
        machine reads a seam terminal exclusively off a snapshot, and every
        scene-straddling verb (InvokeRewind / InvokeRewindToLaunch / LoadGame)
        answers with the world torn down - so the terminal lands on frames whose
        active vessel is unreadable. Dropping the seam fields from the vessel_lost
        constructor meant those verbs' OKs could never be seen, and the phase
        waiting for one burned its whole frame bound on every HEALTHY run.

        Driven, not read: a connection whose active_vessel raises forces
        read_snapshot down its vessel_lost path, and the terminal stored by the
        preceding perform() must still be published."""

        class _Boom(object):
            ut = 4242.0

            @property
            def active_vessel(self):
                raise RuntimeError("no active vessel (SPACECENTER)")

        class _Conn(object):
            def __init__(self):
                self.space_center = _Boom()

        tmp = tempfile.mkdtemp(prefix="parsek-vlost-")
        try:
            cmds = os.path.join(tmp, "c.txt")
            resps = os.path.join(tmp, "r.txt")
            with open(resps, "w", encoding="utf-8") as fh:
                fh.write("id=0003.rewind cmd=InvokeRewindToLaunch verdict=OK "
                         "seq=1 rewound=true tree=t_kx\n")
            control = mission_runner.KrpcMissionControl(use_mechjeb=False)
            control.configure_seam(cmds, resps, "0003")
            control._conn = _Conn()
            control.perform(mlib.Action(mlib.ACTION_PARSEK_SEAM_COMMAND,
                                        seam_verb="InvokeRewindToLaunch",
                                        seam_args=(("tree", "t_kx"),),
                                        seam_tag="rewind"))
            # Drive read_snapshot past the transient-raise window onto the
            # vessel_lost path (the streak re-raises below the limit by design).
            snapshot = None
            for _ in range(mission_runner.READ_FAIL_STREAK_LIMIT + 1):
                try:
                    snapshot = control.read_snapshot()
                except Exception:
                    continue
            self.assertIsNotNone(snapshot, "never reached the vessel_lost path")
            self.assertTrue(snapshot.vessel_lost)
            self.assertEqual("rewind", snapshot.seam_command_tag)
            self.assertEqual("OK", snapshot.seam_command_result)
            self.assertEqual("t_kx", dict(snapshot.seam_command_payload)["tree"])
            # The pure reader a machine actually uses must now resolve it.
            self.assertEqual("OK", mlib._seam_result(snapshot, "rewind"))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

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
