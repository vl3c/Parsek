#!/usr/bin/env python3
"""Spike S10a: fold relative_census.csv files into the decision table.

Reads every <root>/<label>/relative_census.csv (written by the manual xUnit test
Parsek.Tests.Spikes.RelativeCopyCensusTests.Manual_RelativeCopyCensus_WritesCsv) and
prints, as Markdown, the relative SECONDS and section counts whose body-fixed copy is
complete, partial, a single sample or absent, by recorder source class, per save and in
total, and the percentage the plan's decision rule tests (complete seconds over all
relative seconds, pass at 95 percent).

The output carries only the save labels (the copy folder names), counts and seconds.

Usage:
  python summarize_census.py <root> [<root> ...] [--out FILE]
  python summarize_census.py --selftest
Stdlib only.
"""

import argparse
import csv
import io
import os
import sys
import tempfile
import unittest

PASS_PERCENT = 95.0
CLASSES = ("complete", "partial", "single", "none")


class Bucket(object):
    __slots__ = ("count", "seconds", "covered", "match_frames")

    def __init__(self):
        self.count = 0
        self.seconds = 0.0
        self.covered = 0.0      # seconds the copy covers (overlap with the section range)
        self.match_frames = 0   # partial sections whose copy shadows every frame

    def add(self, row):
        self.count += 1
        self.seconds += row["duration_s"]
        self.covered += row["bf_covered_s"]
        if row["bf_matches_frames"]:
            self.match_frames += 1


class Tally(object):
    def __init__(self):
        self.by_class = dict((c, Bucket()) for c in CLASSES)

    def add(self, row):
        c = row["copy_class"]
        if c not in self.by_class:
            raise ValueError("unknown copy_class %r" % c)
        self.by_class[c].add(row)

    def total_count(self):
        return sum(b.count for b in self.by_class.values())

    def total_seconds(self):
        return sum(b.seconds for b in self.by_class.values())

    def covered_seconds(self):
        return sum(b.covered for b in self.by_class.values())

    def complete_percent(self):
        t = self.total_seconds()
        if t <= 0.0:
            return None
        return 100.0 * self.by_class["complete"].seconds / t

    def covered_percent(self):
        t = self.total_seconds()
        if t <= 0.0:
            return None
        return 100.0 * self.covered_seconds() / t


def parse_bool(text):
    return text.strip().lower() == "true"


def read_rows(path):
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        for raw in reader:
            yield {
                "save_label": raw["save_label"],
                "source_class": raw["source_class"],
                "copy_class": raw["copy_class"],
                "duration_s": float(raw["duration_s"]),
                "bf_covered_s": float(raw["bf_covered_s"]),
                "bf_matches_frames": parse_bool(raw.get("bf_matches_frames", "false")),
            }


def find_csvs(roots):
    found = []
    for root in roots:
        if os.path.isfile(root):
            found.append(root)
            continue
        for name in sorted(os.listdir(root)):
            p = os.path.join(root, name, "relative_census.csv")
            if os.path.isfile(p):
                found.append(p)
    return found


def fold(rows):
    """Returns (by_source, by_save, total, by_save_source)."""
    by_source, by_save, by_save_source = {}, {}, {}
    total = Tally()
    for r in rows:
        total.add(r)
        by_source.setdefault(r["source_class"], Tally()).add(r)
        by_save.setdefault(r["save_label"], Tally()).add(r)
        by_save_source.setdefault((r["save_label"], r["source_class"]), Tally()).add(r)
    return by_source, by_save, total, by_save_source


def fmt_s(x):
    return "%.2f" % x


def fmt_pct(x):
    return "n/a" if x is None else "%.1f%%" % x


def cell(b):
    return "%d / %s" % (b.count, fmt_s(b.seconds))


HEADER = ("| {key} | sections | relative s | complete (n / s) | partial (n / s) | "
          "partial, copy = frames (n) | single (n / s) | none (n / s) | covered s | "
          "complete % of s | covered % of s |")
RULE = "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"


def table_line(key, t):
    return "| %s | %d | %s | %s | %s | %d | %s | %s | %s | %s | %s |" % (
        key, t.total_count(), fmt_s(t.total_seconds()),
        cell(t.by_class["complete"]), cell(t.by_class["partial"]),
        t.by_class["partial"].match_frames,
        cell(t.by_class["single"]), cell(t.by_class["none"]),
        fmt_s(t.covered_seconds()),
        fmt_pct(t.complete_percent()), fmt_pct(t.covered_percent()))


def verdict(total):
    p = total.complete_percent()
    if p is None:
        return "No relative seconds: the rule has nothing to test."
    if p >= PASS_PERCENT:
        return ("%s of relative seconds have a complete copy (>= %.0f%%): R3W-04 keeps "
                "'not drawn' for the rest." % (fmt_pct(p), PASS_PERCENT))
    return ("%s of relative seconds have a complete copy (< %.0f%%): name the producers "
            "that skip the copy (see the by-source table)." % (fmt_pct(p), PASS_PERCENT))


def render(rows, saves_read):
    by_source, by_save, total, _ = fold(rows)
    out = []
    out.append("Saves read: %d; with relative sections: %d." % (saves_read, len(by_save)))
    out.append("")
    out.append("### By recorder source (all saves)")
    out.append("")
    out.append(HEADER.format(key="source"))
    out.append(RULE)
    for k in sorted(by_source):
        out.append(table_line(k, by_source[k]))
    out.append(table_line("**total**", total))
    out.append("")
    out.append("### By save")
    out.append("")
    out.append(HEADER.format(key="save"))
    out.append(RULE)
    for k in sorted(by_save):
        out.append(table_line(k, by_save[k]))
    out.append(table_line("**total**", total))
    out.append("")
    out.append("Decision rule: " + verdict(total))
    return "\n".join(out) + "\n"


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("roots", nargs="*", help="census output roots (or CSV files)")
    ap.add_argument("--out", help="also write the Markdown to this file")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)
    if args.selftest:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest)
        res = unittest.TextTestRunner(verbosity=1).run(suite)
        return 0 if res.wasSuccessful() else 1
    if not args.roots:
        ap.error("name at least one census root")
    csvs = find_csvs(args.roots)
    if not csvs:
        sys.stderr.write("no relative_census.csv found under %s\n" % ", ".join(args.roots))
        return 2
    rows = []
    for p in csvs:
        rows.extend(read_rows(p))
    text = render(rows, len(csvs))
    sys.stdout.write(text)
    if args.out:
        with io.open(args.out, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    return 0


# ---------------------------------------------------------------- selftest

_HDR = ("save_label,tree_index,tree_active,recording_id,section_index,source_class,"
        "section_source,environment,copy_class,start_ut,end_ut,duration_s,frames,"
        "frames_first_ut,frames_last_ut,bf_frames,bf_first_ut,bf_last_ut,bf_covered_s,"
        "bf_gap_start_s,bf_gap_end_s,bf_coverage_frac,frames_gap_start_s,frames_gap_end_s,"
        "bf_matches_frames,is_eva,is_debris,parent_anchored,bp_links,has_anchor_recording,"
        "anchor_vessel_set,is_boundary_seam")


def _row(label, src, cls, dur, covered, match):
    return ("%s,0,false,id,0,%s,Background,Atmospheric,%s,0,%r,%r,2,0,0,2,0,0,%r,0,0,1,0,0,%s,"
            "false,false,false,,true,false,false" % (
                label, src, cls, dur, dur, covered, "true" if match else "false"))


class SelfTest(unittest.TestCase):
    def _write(self, root, label, lines):
        d = os.path.join(root, label)
        os.makedirs(d)
        with io.open(os.path.join(d, "relative_census.csv"), "w", encoding="utf-8") as f:
            f.write(_HDR + "\n" + "\n".join(lines) + "\n")

    def test_fold_counts_seconds_by_class_source_and_save(self):
        tmp = tempfile.mkdtemp()
        self._write(tmp, "a", [
            _row("a", "parent-anchored", "complete", 90.0, 90.0, True),
            _row("a", "docking", "partial", 10.0, 9.0, True),
        ])
        self._write(tmp, "b", [
            _row("b", "active", "none", 5.0, 0.0, False),
            _row("b", "eva", "single", 1.0, 0.0, False),
        ])
        os.makedirs(os.path.join(tmp, "empty"))  # no CSV: skipped
        csvs = find_csvs([tmp])
        self.assertEqual(2, len(csvs))
        rows = [r for p in csvs for r in read_rows(p)]
        by_source, by_save, total, _ = fold(rows)
        self.assertEqual(4, total.total_count())
        self.assertAlmostEqual(106.0, total.total_seconds())
        self.assertAlmostEqual(99.0, total.covered_seconds())
        self.assertAlmostEqual(100.0 * 90.0 / 106.0, total.complete_percent())
        self.assertEqual(1, by_source["docking"].by_class["partial"].match_frames)
        self.assertEqual(1, by_save["b"].by_class["none"].count)
        self.assertAlmostEqual(5.0, by_save["b"].by_class["none"].seconds)

    def test_verdict_threshold(self):
        t = Tally()
        t.add({"copy_class": "complete", "duration_s": 95.0, "bf_covered_s": 95.0,
               "bf_matches_frames": True})
        t.add({"copy_class": "none", "duration_s": 5.0, "bf_covered_s": 0.0,
               "bf_matches_frames": False})
        self.assertIn("keeps", verdict(t))
        t.add({"copy_class": "none", "duration_s": 1.0, "bf_covered_s": 0.0,
               "bf_matches_frames": False})
        self.assertIn("skip the copy", verdict(t))
        self.assertIn("nothing to test", verdict(Tally()))

    def test_unknown_class_is_an_error(self):
        t = Tally()
        with self.assertRaises(ValueError):
            t.add({"copy_class": "weird", "duration_s": 1.0, "bf_covered_s": 0.0,
                   "bf_matches_frames": False})

    def test_render_has_both_tables_and_the_rule(self):
        rows = [{"save_label": "s1", "source_class": "docking", "copy_class": "partial",
                 "duration_s": 2.0, "bf_covered_s": 1.5, "bf_matches_frames": True}]
        text = render(rows, 1)
        self.assertIn("### By recorder source", text)
        self.assertIn("### By save", text)
        self.assertIn("| docking | 1 | 2.00 |", text)
        self.assertIn("Decision rule:", text)

    def test_header_matches_the_csharp_writer(self):
        here = os.path.dirname(os.path.abspath(__file__))
        src = os.path.join(here, "..", "..", "..", "Source", "Parsek.Tests", "Spikes",
                           "RelativeCopyCensus.cs")
        if not os.path.isfile(src):
            self.skipTest("C# source not beside this script")
        text = io.open(src, encoding="utf-8").read()
        start = text.index("internal static readonly string[] Header =")
        block = text[start:text.index("};", start)]
        names = [p.split('"')[1] for p in block.split(",") if '"' in p]
        self.assertEqual(_HDR.split(","), names)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
