"""The injection-preset four-surface sync cell run.py's own comment promised
("The sync is manual; a cell asserting the two sets are equal is the fix, and
is not yet written" -- now it is). A preset must exist on ALL FOUR surfaces or
the harness fails open in a specific, measured way (HARNESS-INJECT-FAILS-OPEN,
run.py's _inject_postcondition_missing docstring): present in hlib but missing
in run.py validates clean, stages NO injection, runs NO postcondition, and
boots an un-injected save.

Surfaces:
  1. hlib.INJECTED_RECORDINGS      (spec validation; "none" is the no-op)
  2. run.py RP_SIDECAR_BY_PRESET   (staging + fail-closed postcondition)
  3. scripts/inject-recordings.ps1 $injectFilterByPreset (the xUnit filter map)
  4. Source/Parsek.Tests/SyntheticRecordingTests.cs Inject* facts (the
     injectors themselves; asserted by source text, the
     CommittedBatchTallySourceSyncTests precedent for reading outside
     harness/)

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import os
import re
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_REPO = os.path.dirname(_HARNESS)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
if _HARNESS not in sys.path:
    sys.path.insert(0, _HARNESS)

import hlib  # noqa: E402


def _read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


class InjectionPresetSyncTests(unittest.TestCase):
    def _run_py_presets(self):
        # Parse run.py's table from source text rather than importing run.py
        # (importing the shell pulls its CLI/env side surface into a unit
        # test; the table is a literal, so text is the honest source).
        text = _read(os.path.join(_HARNESS, "run.py"))
        m = re.search(r"RP_SIDECAR_BY_PRESET = \{(.*?)\}", text, re.S)
        self.assertIsNotNone(m, "run.py RP_SIDECAR_BY_PRESET not found")
        return set(re.findall(r"\"([a-z0-9-]+)\"\s*:", m.group(1)))

    def _ps1_presets(self):
        text = _read(os.path.join(_REPO, "scripts", "inject-recordings.ps1"))
        m = re.search(r"\$injectFilterByPreset = @\{(.*?)\}", text, re.S)
        self.assertIsNotNone(m, "ps1 $injectFilterByPreset not found")
        return dict(re.findall(r"\"([a-z0-9-]+)\"\s*=\s*\"(\w+)\"", m.group(1)))

    def test_all_four_surfaces_carry_the_same_preset_set(self):
        hlib_presets = set(hlib.INJECTED_RECORDINGS) - {"none"}
        run_presets = self._run_py_presets()
        ps1 = self._ps1_presets()
        self.assertEqual(hlib_presets, run_presets,
                         "hlib.INJECTED_RECORDINGS vs run.py "
                         "RP_SIDECAR_BY_PRESET drifted")
        self.assertEqual(hlib_presets, set(ps1),
                         "hlib.INJECTED_RECORDINGS vs inject-recordings.ps1 "
                         "$injectFilterByPreset drifted")

    def test_every_ps1_filter_names_a_real_xunit_inject_fact(self):
        ps1 = self._ps1_presets()
        source = _read(os.path.join(
            _REPO, "Source", "Parsek.Tests", "SyntheticRecordingTests.cs"))
        for preset, fact in sorted(ps1.items()):
            self.assertRegex(
                source, r"public void %s\(\)" % re.escape(fact),
                "preset %r maps to xUnit fact %r which does not exist in "
                "SyntheticRecordingTests.cs" % (preset, fact))
