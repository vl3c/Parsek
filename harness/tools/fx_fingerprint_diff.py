#!/usr/bin/env python3
"""FX-fingerprint A/B set-diff over two collected KSP.logs (R14, report-only).

Thin I/O shell over the pure hlib functions
(``parse_fx_fingerprint_lines`` / ``diff_fx_fingerprints`` /
``format_fx_fingerprint_diff``): reads two KSP.log files (typically
``results/<runId>_shots/KSP.log`` from a capture run on each instance
profile), extracts every ``[Parsek][VERBOSE][FxFingerprint]`` line, and prints
the deterministic set-diff report to stdout.

REPORT-ONLY BY DESIGN: this tool gates nothing and writes nothing. A ghost FX
divergence between instance profiles is expected wherever a mod (ReStock, SWE)
authors different EFFECTS for a part; the report exists so those divergences
are enumerated and reviewed instead of unseen. Capture recipe: fly the same
fixture save on both instances with verboseLogging on (the ABFX capture-run
shape documented in docs/dev/todo-and-known-bugs.md T48), then:

    python tools/fx_fingerprint_diff.py \
        results/<stockRun>_shots/KSP.log results/<moddedRun>_shots/KSP.log \
        --a-label stock-minimal --b-label modded-compat

Exit code 0 always (unless an input file is unreadable): differences are the
report's content, not a failure.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))

import hlib  # noqa: E402


def main(argv=None):
    ap = argparse.ArgumentParser(description="Set-diff [FxFingerprint] lines from two KSP.logs")
    ap.add_argument("log_a", help="first KSP.log (side A)")
    ap.add_argument("log_b", help="second KSP.log (side B)")
    ap.add_argument("--a-label", default="A", help="label for side A (e.g. stock-minimal)")
    ap.add_argument("--b-label", default="B", help="label for side B (e.g. modded-compat)")
    args = ap.parse_args(argv)

    sides = []
    for path, label in ((args.log_a, args.a_label), (args.log_b, args.b_label)):
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                parsed = hlib.parse_fx_fingerprint_lines(fh)
        except OSError as exc:
            print("ERROR: cannot read %s (%s): %s" % (label, path, exc))
            return 2
        print("side %s: %s (%d fingerprint key(s), %d malformed line(s))"
              % (label, path, len(parsed.entries), parsed.malformed))
        sides.append(parsed)

    diff = hlib.diff_fx_fingerprints(sides[0].entries, sides[1].entries)
    for line in hlib.format_fx_fingerprint_diff(diff, args.a_label, args.b_label):
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
