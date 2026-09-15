#!/usr/bin/env python3
"""Phase 0 fragment merger: folds linted JSONL fragments into D1 and the registers.

Outputs:
  test-inventory.csv        D1 with verdict/register_id filled from method rows
  findings.csv              D2 candidate findings (all finding rows, fragment order)
  issue-register.csv        the same rows sorted High > Medium > Low, july_ref from --july-refs
  coverage-proposals.csv    D3 candidates
"""

import argparse
import csv
import json
import os
import sys
from collections import defaultdict


def norm_file(path):
    p = (path or "").replace("\\", "/")
    for prefix in ("Source/Parsek.Tests/", "./", "Source/Parsek.Tests"):
        if p.startswith(prefix):
            p = p[len(prefix):]
    return p.lstrip("/")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--inventory", required=True)
    ap.add_argument("--fragments", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--july-refs", default=None,
                    help="JSONL of {id, july_ref}; fills july_ref on findings and dupe_of_july_id on coverage rows")
    args = ap.parse_args()

    method_rows = {}
    findings = []
    coverage = []
    for fn in sorted(os.listdir(args.fragments)):
        if not fn.endswith(".jsonl"):
            continue
        with open(os.path.join(args.fragments, fn), encoding="utf-8") as fh:
            for raw in fh:
                line = raw.strip()
                if not line:
                    continue
                rec = json.loads(line)
                if rec.get("kind") == "method":
                    method_rows[(norm_file(rec.get("file")), rec.get("method"))] = rec
                elif rec.get("kind") == "finding":
                    findings.append(rec)
                elif rec.get("kind") == "coverage":
                    coverage.append(rec)

    finding_by_method = {}
    for f in findings:
        key = (norm_file(f.get("file")), f.get("method"))
        finding_by_method.setdefault(key, f["id"])

    rows = []
    filled = 0
    with open(args.inventory, newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            key = (norm_file(r["file"]), r["method"])
            m = method_rows.get(key)
            if m:
                r["verdict"] = m.get("verdict") or ""
                r["register_id"] = finding_by_method.get(key, "")
                filled += 1
            rows.append(r)

    fields = list(rows[0].keys()) if rows else []
    with open(os.path.join(args.out, "test-inventory.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=fields)
        w.writeheader()
        for r in rows:
            w.writerow(r)

    refs = {}
    if args.july_refs and os.path.exists(args.july_refs):
        with open(args.july_refs, encoding="utf-8") as fh:
            for raw in fh:
                if raw.strip():
                    rec = json.loads(raw)
                    refs[rec["id"]] = rec["july_ref"]
    sev_rank = {"Critical": 0, "High": 1, "Medium": 2, "Low": 3}
    for f in findings:
        f["july_ref"] = refs.get(f["id"], f.get("july_ref") or "none")
        f.setdefault("merged", "")
    for c in coverage:
        if c.get("cand_id") in refs:
            c["dupe_of_july_id"] = refs[c["cand_id"]]
    if findings:
        ffields = sorted({k for f in findings for k in f.keys()})
        ordered = sorted(findings, key=lambda f: (sev_rank.get(f.get("severity"), 9),
                                                   f.get("category", ""), f.get("id", "")))
        with open(os.path.join(args.out, "issue-register.csv"), "w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=ffields, extrasaction="ignore")
            w.writeheader()
            for f in ordered:
                w.writerow(f)
        with open(os.path.join(args.out, "findings.csv"), "w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=ffields, extrasaction="ignore")
            w.writeheader()
            for f in findings:
                w.writerow(f)

    if coverage:
        cfields = sorted({k for c in coverage for k in c.keys()})
        with open(os.path.join(args.out, "coverage-proposals.csv"), "w", newline="",
                  encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=cfields, extrasaction="ignore")
            w.writeheader()
            for c in coverage:
                w.writerow(c)

    print("methods filled %d/%d; findings=%d; coverage candidates=%d" %
          (filled, len(rows), len(findings), len(coverage)))


if __name__ == "__main__":
    sys.exit(main())
