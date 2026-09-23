#!/usr/bin/env python3
"""Phase 0 results parser: TRX baseline + coverlet cobertura + completeness check.

Outputs (relative to --out):
  coverage-by-class.csv        per production class line/branch/method coverage
  work/durations.csv           per test method case count / outcome / total duration
  work/trx-summary.txt         run counts
  work/inventory-completeness.txt  scanner vs TRX method-name diff
"""

import argparse
import csv
import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def parse_trx(path, out_dir):
    tree = ET.parse(path)
    root = tree.getroot()
    by_method = defaultdict(lambda: {"cases": 0, "outcome": "Passed", "duration": 0.0})
    for res in root.iter(TRX_NS + "UnitTestResult"):
        name = res.get("testName") or ""
        method_full = name.split("(", 1)[0]
        parts = method_full.rsplit(".", 2)
        if len(parts) < 2:
            continue
        cls, method = parts[-2], parts[-1]
        key = (cls, method)
        rec = by_method[key]
        rec["cases"] += 1
        if res.get("outcome") not in ("Passed", "NotExecuted"):
            rec["outcome"] = res.get("outcome") or rec["outcome"]
        dur = res.get("duration") or "00:00:00"
        try:
            h, m, s = dur.split(":")
            rec["duration"] += int(h) * 3600 + int(m) * 60 + float(s)
        except ValueError:
            pass
    with open(os.path.join(out_dir, "durations.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["class", "method", "cases", "outcome", "duration_seconds"])
        for (cls, method), rec in sorted(by_method.items()):
            w.writerow([cls, method, rec["cases"], rec["outcome"], "%.4f" % rec["duration"]])
    return by_method


def parse_cobertura(path, out_dir):
    tree = ET.parse(path)
    root = tree.getroot()
    rows = []
    for cls in root.iter("class"):
        rows.append({
            "class": cls.get("name", ""),
            "filename": cls.get("filename", ""),
            "line_rate": cls.get("line-rate", ""),
            "branch_rate": cls.get("branch-rate", ""),
            "method_rate": cls.get("method-rate", ""),
            "lines_covered": cls.get("lines-covered", ""),
            "lines_valid": cls.get("lines-valid", ""),
            "branches_covered": cls.get("branches-covered", ""),
            "branches_valid": cls.get("branches-valid", ""),
        })
    rows.sort(key=lambda r: (float(r["line_rate"] or 0), r["class"]))
    with open(os.path.join(out_dir, "coverage-by-class.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()) if rows else [])
        w.writeheader()
        for r in rows:
            w.writerow(r)
    return rows


def completeness(inv_path, trx_methods, out_dir):
    inv = set()
    with open(inv_path, newline="", encoding="utf-8") as fh:
        for row in csv.DictReader(fh):
            inv.add((row["class"], row["method"]))
    trx = set(trx_methods.keys())
    trx_only = sorted(trx - inv)
    inv_only = sorted(inv - trx)
    with open(os.path.join(out_dir, "work", "inventory-completeness.txt"), "w",
              encoding="utf-8") as fh:
        fh.write("inventory methods: %d\ntrx methods: %d\n" % (len(inv), len(trx)))
        fh.write("trx-only (scanner missed): %d\n" % len(trx_only))
        for cls, m in trx_only[:400]:
            fh.write("  + %s.%s\n" % (cls, m))
        fh.write("inventory-only (scanner extra / not executed): %d\n" % len(inv_only))
        for cls, m in inv_only[:400]:
            fh.write("  - %s.%s\n" % (cls, m))
    return len(trx_only), len(inv_only)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--trx", required=True)
    ap.add_argument("--cobertura", required=True)
    ap.add_argument("--inventory", required=True)
    ap.add_argument("--out", required=True, help="audit dir holding work/")
    args = ap.parse_args()

    os.makedirs(os.path.join(args.out, "work"), exist_ok=True)
    methods = parse_trx(args.trx, os.path.join(args.out, "work"))
    classes = parse_cobertura(args.cobertura, args.out)
    total_cases = sum(r["cases"] for r in methods.values())
    failed = [k for k, r in methods.items() if r["outcome"] not in ("Passed", "NotExecuted")]
    skipped = [k for k, r in methods.items() if r["outcome"] == "NotExecuted"]
    with open(os.path.join(args.out, "work", "trx-summary.txt"), "w", encoding="utf-8") as fh:
        fh.write("methods: %d\ncases: %d\nfailed: %d\nnot-executed: %d\nclasses covered: %d\n"
                 % (len(methods), total_cases, len(failed), len(skipped), len(classes)))
        for cls, m in failed[:50]:
            fh.write("FAIL %s.%s\n" % (cls, m))
        for cls, m in skipped[:50]:
            fh.write("SKIP %s.%s\n" % (cls, m))
    trx_only, inv_only = completeness(args.inventory, methods, args.out)
    print("methods=%d cases=%d failed=%d not-executed=%d classes=%d" %
          (len(methods), total_cases, len(failed), len(skipped), len(classes)))
    print("completeness: trx-only=%d inventory-only=%d (see work/inventory-completeness.txt)" %
          (trx_only, inv_only))


if __name__ == "__main__":
    sys.exit(main())
