#!/usr/bin/env python3
"""Phase 0 batch builder: cluster + tier + file-batches.csv + per-batch manifests.

Batch budget (audit rubric): one agent pass reads at most ~100 KB of test source and
produces at most ~80 method rows. Files over either bound are split into consecutive
method ranges. Packed tiers additionally cap total methods per batch at 60 so a batch
never demands more verdicts than a pass can sustain. Files with zero test methods
(helpers/generators) are excluded from review batches.

Cluster priority is risk-first: rewind/recording/ledger/recorder before UI and
organization work.

Outputs:
  file-batches.csv            one row per batch (routing summary)
  work/batches.json           machine-readable batch list
  work/manifests/<id>.json    expected method rows per batch (linter + agent input)
  work/non-test-files.txt     zero-test .cs files in the test project
"""

import argparse
import csv
import json
import math
import os
import sys
from collections import defaultdict

PRIORITY = [
    "rewind-refly", "recording-tree", "ledger-career", "recorder-events",
    "logistics-route", "spawn-vessel", "ghost-playback", "map-render",
    "trajectory-orbit", "mission-groups", "analyzer", "logging", "harness-seam",
    "wiring-gates", "ui-settings", "io-serialization", "legacy-bugfix", "catchall",
]

MAX_KB = 100.0
MAX_METHODS_PER_FILE_PASS = 80
MAX_METHODS_PER_BATCH = 60


def load_rows(path, key):
    rows = defaultdict(list)
    with open(path, newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            rows[r[key]].append(r)
    return rows


def split_file(file_row, methods):
    """Split a large file into consecutive method ranges within both budgets."""
    size = float(file_row["size_kb"])
    methods = sorted(methods, key=lambda m: int(m["line"]))
    n_chunks = max(2, int(math.ceil(size / MAX_KB)),
                  int(math.ceil(len(methods) / MAX_METHODS_PER_FILE_PASS)))
    per = max(1, int(math.ceil(len(methods) / n_chunks)))
    ranges = []
    for i in range(0, len(methods), per):
        part = methods[i:i + per]
        start = int(part[0]["line"])
        end = int(part[-1]["line"]) + 40 if i + per < len(methods) else 10 ** 9
        ranges.append({
            "start": start,
            "end": end,
            "methods": [{"method": m["method"], "line": int(m["line"])} for m in part],
        })
    return ranges


def make_batch(batch_id, cluster, tier, files):
    methods = 0
    size = 0.0
    for f in files:
        methods += f["methods"]
        size += f["size_kb"]
    return {
        "batch_id": batch_id,
        "cluster": cluster,
        "tier": tier,
        "files": files,
        "methods": methods,
        "size_kb": round(size, 1),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--summary", required=True)
    ap.add_argument("--inventory", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    with open(args.summary, newline="", encoding="utf-8") as fh:
        files = [r for r in csv.DictReader(fh)]
    inv = load_rows(args.inventory, "file")

    by_cluster = defaultdict(list)
    for f in files:
        by_cluster[f["cluster"]].append(f)

    batches = []
    non_test = []
    for cluster in PRIORITY + sorted(set(by_cluster) - set(PRIORITY)):
        entries = sorted(by_cluster.get(cluster, []), key=lambda r: -float(r["size_kb"]))
        current = []
        current_kb = 0.0
        current_methods = 0
        current_tier = None
        counter = 0

        def flush():
            nonlocal current, current_kb, current_methods, current_tier, counter
            if not current:
                return
            counter += 1
            batches.append(make_batch("%s-%03d" % (cluster, counter), cluster,
                                      current_tier, current))
            current = []
            current_kb = 0.0
            current_methods = 0
            current_tier = None

        for r in entries:
            if int(r["tests"]) == 0:
                non_test.append(r["file"])
                continue
            size = float(r["size_kb"])
            tier = r["tier"]
            methods = int(r["tests"])
            rows = inv.get(r["file"], [])
            if size > MAX_KB or len(rows) > MAX_METHODS_PER_FILE_PASS:
                flush()
                ranges = split_file(r, rows)
                per_range_kb = round(size / len(ranges), 1)
                for rg in ranges:
                    counter += 1
                    batch_id = "%s-%03d" % (cluster, counter)
                    batches.append(make_batch(
                        batch_id, cluster, "A1",
                        [{"file": r["file"], "size_kb": per_range_kb,
                          "methods": len(rg["methods"]), "ranges": [rg]}]))
                continue
            cap = {"A": 2, "B": 6, "C": 12, "D": 25}[tier]
            if current and (current_kb + size > MAX_KB
                            or current_methods + methods > MAX_METHODS_PER_BATCH
                            or len(current) >= cap):
                flush()
            if not current:
                current_tier = tier
            current.append({"file": r["file"], "size_kb": size, "methods": methods})
            current_kb += size
            current_methods += methods
        flush()

    os.makedirs(os.path.join(args.out, "work", "manifests"), exist_ok=True)
    for b in batches:
        man = {
            "batch_id": b["batch_id"],
            "cluster": b["cluster"],
            "tier": b["tier"],
            "expected_methods": b["methods"],
            "files": [],
        }
        for f in b["files"]:
            if "ranges" in f:
                man["files"].append({"file": f["file"], "ranges": f["ranges"]})
            else:
                man["files"].append({
                    "file": f["file"],
                    "ranges": [{"start": 1, "end": 10 ** 9,
                                "methods": [{"method": m["method"], "line": int(m["line"])}
                                            for m in inv.get(f["file"], [])]}],
                })
        with open(os.path.join(args.out, "work", "manifests", b["batch_id"] + ".json"),
                  "w", encoding="utf-8") as fh:
            json.dump(man, fh, indent=1)

    with open(os.path.join(args.out, "work", "batches.json"), "w", encoding="utf-8") as fh:
        json.dump(batches, fh, indent=1)
    with open(os.path.join(args.out, "file-batches.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["batch_id", "cluster", "tier", "files", "methods", "size_kb"])
        for b in batches:
            w.writerow([b["batch_id"], b["cluster"], b["tier"],
                        ";".join(f["file"] for f in b["files"]), b["methods"], b["size_kb"]])
    with open(os.path.join(args.out, "work", "non-test-files.txt"), "w", encoding="utf-8") as fh:
        for f in non_test:
            fh.write(f + "\n")

    per_cluster = defaultdict(int)
    for b in batches:
        per_cluster[b["cluster"]] += 1
    print("batches=%d non-test files=%d" % (len(batches), len(non_test)))
    for c in PRIORITY:
        if per_cluster.get(c):
            print("  %-18s batches=%d" % (c, per_cluster[c]))
    for c in sorted(set(per_cluster) - set(PRIORITY)):
        print("  %-18s batches=%d" % (c, per_cluster[c]))


if __name__ == "__main__":
    sys.exit(main())
