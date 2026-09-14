#!/usr/bin/env python3
"""Phase 0 fragment linter: validates agent JSONL output against batch manifests.

A batch passes only when: every method in the manifest appears exactly once, enums are
valid, every non-ok verdict carries a category and a falsifiability string, findings carry
the required fields, and ids are unique. No finding enters D2 except through a linted
fragment.

Usage:
  python lint_fragments.py --manifests <dir> --fragments <dir> --batch <id> [--batch ...]
  python lint_fragments.py --manifests <dir> --fragments <dir> --all
"""

import argparse
import json
import os
import re
import sys

VERDICTS = {"ok", "weak", "vacuous", "duplicate", "brittle", "misleading", "organization",
            "source-gate-ok", "source-gate-misplaced", "fixture-limited", "uncertain"}
NEEDS_FINDING = {"weak", "vacuous", "duplicate", "brittle", "misleading", "organization",
                 "source-gate-misplaced", "uncertain"}
ACCEPTED_NO_FINDING = {"ok", "source-gate-ok", "fixture-limited"}
CATEGORIES = {"T1", "T2", "T3", "T4", "T5", "T6", "T7"}
SEVERITIES = {"Critical", "High", "Medium", "Low"}
CONFIDENCES = {"high", "medium", "low"}
EFFORTS = {"S", "M", "L"}
CASE_KINDS = {"negative", "mirror-direction", "roundtrip", "boundary", "serializer-key",
              "state-transition", "integration"}
RISKS = {"data-loss", "career", "recording", "playback", "ui"}
FALSIFIABILITY_RE = re.compile(r"->")


def norm_file(path):
    p = (path or "").replace("\\", "/")
    for prefix in ("Source/Parsek.Tests/", "./", "Source/Parsek.Tests"):
        if p.startswith(prefix):
            p = p[len(prefix):]
    return p.lstrip("/")


def lint_batch(batch_id, manifest_dir, fragment_dir, suffix=""):
    errors = []
    warnings = []
    man_path = os.path.join(manifest_dir, batch_id + ".json")
    frag_path = os.path.join(fragment_dir, batch_id + suffix + ".jsonl")
    if not os.path.exists(man_path):
        return ["manifest missing: %s" % man_path], []
    if not os.path.exists(frag_path):
        return ["fragment missing: %s" % frag_path], []
    with open(man_path, encoding="utf-8") as fh:
        man = json.load(fh)
    expected = set()
    for f in man["files"]:
        for rg in f["ranges"]:
            for m in rg["methods"]:
                expected.add((norm_file(f["file"]), m["method"]))

    seen = {}
    findings = []
    found_ids = set()
    with open(frag_path, encoding="utf-8") as fh:
        for lineno, raw in enumerate(fh, 1):
            line = raw.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError as e:
                errors.append("line %d: bad JSON (%s)" % (lineno, e))
                continue
            kind = rec.get("kind")
            if kind == "method":
                key = (norm_file(rec.get("file")), rec.get("method"))
                if key in seen:
                    errors.append("line %d: duplicate method row %s.%s" % (lineno, key[0], key[1]))
                seen[key] = rec
                v = rec.get("verdict")
                if v not in VERDICTS:
                    errors.append("line %d: invalid verdict %r" % (lineno, v))
                    continue
                if v in NEEDS_FINDING:
                    if rec.get("category") not in CATEGORIES:
                        errors.append("line %d: non-ok verdict without valid category" % lineno)
                    f = rec.get("falsifiability") or ""
                    if not FALSIFIABILITY_RE.search(f):
                        errors.append("line %d: non-ok verdict without falsifiability -> red" % lineno)
                    if v == "duplicate" and not rec.get("twin"):
                        errors.append("line %d: duplicate without twin" % lineno)
                    if len(rec.get("note") or "") > 300:
                        warnings.append("line %d: note longer than 300 chars" % lineno)
            elif kind == "finding":
                fid = rec.get("id")
                if not fid:
                    errors.append("line %d: finding without id" % lineno)
                elif fid in found_ids:
                    errors.append("line %d: duplicate finding id %s" % (lineno, fid))
                else:
                    found_ids.add(fid)
                    findings.append(rec)
                for field, allowed in (("category", CATEGORIES), ("severity", SEVERITIES),
                                       ("confidence", CONFIDENCES), ("effort", EFFORTS)):
                    if rec.get(field) not in allowed:
                        errors.append("line %d: finding invalid %s=%r" % (lineno, field, rec.get(field)))
                if not FALSIFIABILITY_RE.search(rec.get("falsifiability") or ""):
                    errors.append("line %d: finding without falsifiability -> red" % lineno)
                for req in ("what_it_asserts", "why_weak", "proposed_action"):
                    if not rec.get(req):
                        errors.append("line %d: finding missing %s" % (lineno, req))
            elif kind == "coverage":
                if rec.get("case_kind") not in CASE_KINDS:
                    errors.append("line %d: coverage invalid case_kind" % lineno)
                if rec.get("risk") not in RISKS:
                    errors.append("line %d: coverage invalid risk" % lineno)
                for req in ("sut_file", "sut_line", "proposed_name", "sketch"):
                    if rec.get(req) in (None, ""):
                        errors.append("line %d: coverage missing %s" % (lineno, req))
            else:
                errors.append("line %d: unknown kind %r" % (lineno, kind))

    missing = expected - set(seen)
    extra = set(seen) - expected
    for f, m in sorted(missing):
        errors.append("missing method row: %s . %s" % (f, m))
    for f, m in sorted(extra):
        errors.append("unexpected method row: %s . %s" % (f, m))
    return errors, warnings


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifests", required=True)
    ap.add_argument("--fragments", required=True)
    ap.add_argument("--batch", action="append", default=[])
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--suffix", default="", help="appended to batch id before .jsonl (pilot runs)")
    ap.add_argument("--report", default=None)
    args = ap.parse_args()

    batches = args.batch
    if args.all:
        batches = sorted(os.path.splitext(f)[0]
                         for f in os.listdir(args.manifests) if f.endswith(".json"))
    if not batches:
        print("no batches selected")
        return 2

    lines = []
    failed = 0
    for b in batches:
        errors, warnings = lint_batch(b, args.manifests, args.fragments, args.suffix)
        status = "PASS" if not errors else "FAIL"
        if errors:
            failed += 1
        lines.append("%s %s errors=%d warnings=%d" % (status, b, len(errors), len(warnings)))
        for e in errors[:20]:
            lines.append("    ERR %s" % e)
        for w in warnings[:10]:
            lines.append("    WARN %s" % w)
    text = "\n".join(lines)
    print(text)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as fh:
            fh.write(text + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
