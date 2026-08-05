"""Shell tests for the FX-fingerprint A/B CLI (`harness/tools/fx_fingerprint_diff.py`)
plus one hlib capture edge the main suite leaves unpinned.

The pure half (parse / diff / format) is covered in test_hlib.py
(FxFingerprintCaptureTests / FxFingerprintDiffTests / FxFingerprintReportTests);
these cells drive the thin I/O shell end-to-end over temp files, matching the
house convention that every tools/ shell has its own cells (test_contact_sheet
precedent): summary lines printed, report emitted, exit 0 on a clean diff and
exit 2 on an unreadable input.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

_HERE = os.path.dirname(os.path.abspath(__file__))
_TOOLS = os.path.join(os.path.dirname(_HERE), "tools")
for _p in (_HERE, _TOOLS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import fx_fingerprint_diff as tool  # noqa: E402
import hlib  # noqa: E402


_PREFIX = "[LOG 00:04:12.345] "


def _line(part, fp):
    return (_PREFIX + hlib.FX_FINGERPRINT_ANCHOR
            + " part='%s' kind=engine midx=0 systems=1 nullSystems=0"
              " curves=em0/sp0 fp=[%s]" % (part, fp))


class FxFingerprintCliShellTests(unittest.TestCase):
    """The CLI must read two real files, print the deterministic report, and
    fail loudly (exit 2) only on an unreadable input -- never on a diff."""

    def _write(self, tmp, name, lines):
        path = os.path.join(tmp, name)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write("\n".join(lines) + "\n")
        return path

    def test_end_to_end_diff_over_temp_logs_exits_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            a = self._write(tmp, "a.log", [
                "[LOG 00:00:01.000] boot noise",
                _line("solidBooster.sm.v2", "stockFlame<thrustTransform pos=(0.00,0.00,0.38)"),
            ])
            b = self._write(tmp, "b.log", [
                _line("solidBooster.sm.v2", "ReStock/FX/srb-core<fxTransformCore pos=(0.00,0.00,0.00)"),
            ])
            out = io.StringIO()
            with redirect_stdout(out):
                rc = tool.main([a, b, "--a-label", "stock", "--b-label", "modded"])
            self.assertEqual(0, rc)
            text = out.getvalue()
            self.assertIn("side stock:", text)
            self.assertIn("side modded:", text)
            self.assertIn("1 fingerprint key(s), 0 malformed line(s)", text)
            self.assertIn(
                "fx-fingerprint-diff a=stock b=modded match=0 changed=1"
                " only-in-a=0 only-in-b=0 unstable-a=0 unstable-b=0", text)
            self.assertIn("changed part='solidBooster.sm.v2' kind=engine midx=0", text)

    def test_identical_logs_report_match_and_exit_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            lines = [_line("liquidEngine2", "flame<thrustTransform pos=(0.00,0.00,0.00)")]
            a = self._write(tmp, "a.log", lines)
            b = self._write(tmp, "b.log", lines)
            out = io.StringIO()
            with redirect_stdout(out):
                rc = tool.main([a, b])
            self.assertEqual(0, rc)
            self.assertIn("match=1 changed=0 only-in-a=0 only-in-b=0", out.getvalue())

    def test_unreadable_input_exits_two_and_names_the_side(self):
        with tempfile.TemporaryDirectory() as tmp:
            b = self._write(tmp, "b.log", [_line("p", "x<y pos=(0.00,0.00,0.00)")])
            missing = os.path.join(tmp, "nope.log")
            out = io.StringIO()
            with redirect_stdout(out):
                rc = tool.main([missing, b, "--a-label", "stock"])
            self.assertEqual(2, rc)
            self.assertIn("ERROR: cannot read stock", out.getvalue())


class FxFingerprintBracketEdgeTests(unittest.TestCase):
    """Pins the one capture edge the main suite leaves untested: an fp ENTRY
    containing a literal ']' on a line with NO suppressed suffix. The cut is at
    the line's LAST ']' -- which on such a line is the payload's own closing
    bracket -- so the interior bracket must survive inside the captured value."""

    def test_literal_bracket_in_entry_without_suffix_survives(self):
        fp = "weird]name<thrustTransform pos=(0.00,0.00,0.00)"
        got = hlib.parse_fx_fingerprint_lines([_line("oddPart", fp)])
        self.assertEqual(0, got.malformed)
        ((key, values),) = got.entries.items()
        self.assertEqual(("oddPart", "engine", 0), key)
        self.assertEqual(
            ("systems=1 nullSystems=0 curves=em0/sp0 fp=[%s]" % fp,), values)


if __name__ == "__main__":
    unittest.main()
