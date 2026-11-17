"""SPIKE BRANCH ONLY (spike/mp-r0, never merged; plan r0-spikes.md S6, S11).

The reference-DLL admission skip for the R0 spike instances, and the S6 and S11 tools'
own selftests run from the harness suite so a change to them reds here.
"""

import os
import subprocess
import sys
import unittest

import hlib

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
S6 = os.path.join(REPO, "spikes", "r0", "S6")
S11 = os.path.join(REPO, "spikes", "r0", "S11")


class SpikeInstanceDirTests(unittest.TestCase):
    def test_spike_dirs_are_recognised(self):
        for d in ("C:/u/automation/spike-r0", "C:\\u\\automation\\spike-r0-content",
                  "/u/automation/spike-r0-nodlc/", "automation/Spike-R0-2"):
            self.assertTrue(hlib.is_spike_instance_dir(d), d)

    def test_other_dirs_are_not(self):
        for d in (None, "", "C:/u/automation/stock-minimal", "/u/automation/stock-tree-2",
                  "/u/spike-r0/automation/modded-compat", "/u/automation/myspike-r0"):
            self.assertFalse(hlib.is_spike_instance_dir(d), d)

    def test_the_reference_comparison_itself_is_unchanged(self):
        self.assertIsNone(hlib.decide_reference_dll_drift("stock-tree", "aa", "aa"))
        self.assertIsNotNone(hlib.decide_reference_dll_drift("stock-tree", "aa", "bb"))


class SpikeS6ToolSelftests(unittest.TestCase):
    def _run(self, script):
        p = subprocess.run([sys.executable, os.path.join(S6, script), "--selftest"],
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, universal_newlines=True)
        self.assertEqual(0, p.returncode, p.stdout)

    def test_check_mark(self):
        self._run("check_mark.py")

    def test_make_template(self):
        self._run("make_template.py")

    def test_content_diff(self):
        self._run("content_diff.py")


class SpikeS11ToolSelftests(unittest.TestCase):
    def test_summarize_cost(self):
        p = subprocess.run([sys.executable, os.path.join(S11, "summarize_cost.py"), "--selftest"],
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, universal_newlines=True)
        self.assertEqual(0, p.returncode, p.stdout)
        self.assertIn("summarize_cost selftest: PASS", p.stdout)


if __name__ == "__main__":
    unittest.main()
