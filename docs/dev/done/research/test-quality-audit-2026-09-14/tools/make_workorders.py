#!/usr/bin/env python3
"""Build dispatch work orders: pair adjacent same-cluster batches within a size budget.

Input: file-batches.csv in generation order (already risk-first by cluster).
Output: work/workorders.txt - one line per agent pass, comma-separated batch ids.
"""

import argparse
import csv
import os
import sys

MAX_KB = 150.0
MAX_METHODS = 100


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--batches", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    with open(args.batches, newline="", encoding="utf-8") as fh:
        rows = list(csv.DictReader(fh))

    orders = []
    i = 0
    while i < len(rows):
        a = rows[i]
        if i + 1 < len(rows):
            b = rows[i + 1]
            if (a["cluster"] == b["cluster"]
                    and float(a["size_kb"]) + float(b["size_kb"]) <= MAX_KB
                    and int(a["methods"]) + int(b["methods"]) <= MAX_METHODS):
                orders.append([a["batch_id"], b["batch_id"]])
                i += 2
                continue
        orders.append([a["batch_id"]])
        i += 1

    with open(os.path.join(args.out, "work", "workorders.txt"), "w", encoding="utf-8") as fh:
        for o in orders:
            fh.write(",".join(o) + "\n")
    print("workorders=%d (from %d batches)" % (len(orders), len(rows)))


if __name__ == "__main__":
    sys.exit(main())
