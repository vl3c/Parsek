#!/usr/bin/env python3
"""Write the generated flight profile catalog, `docs/dev/flight-profiles.md`.

Thin I/O shell over ``harness/lib/lanecatalog.py`` (every derivation lives there):
reads every lane spec under ``harness/scenarios/*.toml``, the dimension registry,
the lane rows of ``docs/dev/autotest-status.md`` and the ``[InGameTest]``
categories declared under ``Source/Parsek``, and writes one markdown page with
gap-hunting summary tables and one row per lane. It flies nothing and reads no
run results.

Usage::

    python harness/tools/lane_catalog.py            # rewrite docs/dev/flight-profiles.md
    python harness/tools/lane_catalog.py --check    # exit 1 when the committed doc is stale
    python harness/tools/lane_catalog.py --json -   # the per-lane data as JSON on stdout

``harness/lib/test_lanecatalog.py`` regenerates the doc in memory and reds while
the committed copy differs, so rerun this after changing a spec, the registry, an
in-game category or a lane's status row.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import tomllib

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
sys.path.insert(0, os.path.join(HARNESS_ROOT, "lib"))

import hlib  # noqa: E402
import lanecatalog  # noqa: E402


def _read_text(path: str) -> str:
    # Explicit utf-8: the Windows default codepage would mis-decode the status doc.
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def load_specs(repo_root: str):
    specs = []
    for path in sorted(glob.glob(os.path.join(repo_root, "harness", "scenarios", "*.toml"))):
        with open(path, "rb") as fh:
            specs.append(tomllib.load(fh))
    return specs


def declared_categories(repo_root: str):
    """Every in-game test category declared under Source/Parsek (bin/obj pruned so
    a stale build copy cannot add one)."""
    cats = set()
    source = os.path.join(repo_root, "Source", "Parsek")
    for dirpath, dirnames, filenames in os.walk(source):
        dirnames[:] = sorted(d for d in dirnames if d not in ("bin", "obj"))
        for name in sorted(filenames):
            if not name.endswith(".cs"):
                continue
            text = _read_text(os.path.join(dirpath, name))
            if "InGameTest" not in text:
                continue
            cats.update(d.category for d in hlib.parse_ingame_test_declarations(text, name))
    return cats


def build(repo_root: str):
    """``(lanes, document_text)`` for the tree at ``repo_root``."""
    specs = load_specs(repo_root)
    status_text = _read_text(os.path.join(repo_root, "docs", "dev", "autotest-status.md"))
    with open(os.path.join(repo_root, "harness", "coverage", "registry.toml"), "rb") as fh:
        registry = tomllib.load(fh)
    lanes = lanecatalog.build_catalog(specs, status_text)
    text = lanecatalog.render(lanes, registry, declared_categories(repo_root),
                              hlib.IMPLEMENTED_SEAM_VERBS)
    return lanes, text


def build_document(repo_root: str) -> str:
    return build(repo_root)[1]


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    parser.add_argument("--repo-root", default=REPO_ROOT)
    parser.add_argument("--check", action="store_true",
                        help="compare with the committed doc instead of writing it")
    parser.add_argument("--json", metavar="PATH",
                        help="also write the per-lane data as JSON ('-' for stdout)")
    args = parser.parse_args(argv)

    lanes, text = build(args.repo_root)
    out_path = os.path.join(args.repo_root, *lanecatalog.DOC_RELPATH.split("/"))
    if args.json:
        payload = json.dumps([lanecatalog.lane_to_json(l) for l in lanes], indent=1,
                             sort_keys=True)
        if args.json == "-":
            print(payload)
        else:
            with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(payload + "\n")
    if args.check:
        try:
            current = _read_text(out_path).replace("\r\n", "\n")
        except OSError:
            current = None
        if current != text:
            print("%s is stale; run: %s" % (lanecatalog.DOC_RELPATH,
                                             lanecatalog.REGENERATE_COMMAND), file=sys.stderr)
            return 1
        print("%s is current (%d lanes)" % (lanecatalog.DOC_RELPATH, len(lanes)))
        return 0
    if args.json == "-":
        return 0
    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print("wrote %s (%d lanes)" % (lanecatalog.DOC_RELPATH, len(lanes)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
