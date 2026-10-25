#!/usr/bin/env python3
"""List which parts each STOCK tech node unlocks (the scripted campaign program).

The campaign ladder (docs/dev/autotest-roadmap.md, "The scripted campaign program")
drafts every stage's craft from the parts unlocked at that point of the campaign,
on the `stock-tree` instance (stock-minimal without CommunityTechTree and
ProbesBeforeCrew). This tool reads a KSP install's own config files - the stock
`TechTree.cfg` and every part cfg under the stock part trees - and writes the
per-node unlock listing the craft are checked against:

    python harness/tools/list_tech_unlocks.py \
        --gamedata "<KSP>/GameData" --out docs/dev/research/stock-tech-tree-part-unlocks.md

Only `GameData/Squad` and `GameData/SquadExpansion` are read, never a mod folder or
`ModuleManager.ConfigCache`: the listing is the UNPATCHED stock tree, which is what
the stock-tree instance runs. A part with no `TechRequired`, or one naming a node the
tree does not have (`Unresearchable`), is listed separately so nothing is dropped
silently. Part names are written in their runtime dot form (KSP turns `_` into `.`).

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import sys
from typing import Dict, List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, "..", "lib"))

import saveparse  # noqa: E402

STOCK_PART_ROOTS: Tuple[str, ...] = ("Squad", "SquadExpansion")
TECH_TREE_REL = os.path.join("Squad", "Resources", "TechTree.cfg")


def strip_comments(text: str) -> str:
    """ConfigNode drops `//` to end of line, on value lines too."""
    out = []
    for line in text.splitlines():
        idx = line.find("//")
        out.append(line[:idx] if idx >= 0 else line)
    return "\n".join(out)


def tolerable_parse_error(error: str) -> bool:
    """A stray bare word on its own line (stock ships one: `fxOriginalOffset` in the
    Mainsail cfg) is ignored by ConfigNode.Load; the parser reports it as a dangling
    node name and keeps going. Brace faults are not tolerable: the tree is wrong."""
    faults = [f.strip() for f in error.split(";") if f.strip()]
    return all(f.startswith("dangling node name") for f in faults)


def runtime_part_name(cfg_name: str) -> str:
    return cfg_name.replace("_", ".")


def parse_tech_tree(text: str) -> List[Dict]:
    """RDNode rows in file order: id, title, cost, parents."""
    parsed = saveparse.parse_sfs(strip_comments(text))
    if not parsed.ok:
        raise ValueError("TechTree.cfg parse error: %s" % parsed.error)
    nodes = []
    for tree in parsed.root.nodes_named("TechTree"):
        for rd in tree.nodes_named("RDNode"):
            parents = [p.value("parentID") for p in rd.nodes_named("Parent")]
            nodes.append({
                "id": rd.value("id") or "",
                "title": rd.value("title") or "",
                "cost": int(float(rd.value("cost") or "0")),
                "parents": [p for p in parents if p],
            })
    return nodes


def parse_parts(text: str, source: str) -> List[Dict]:
    """Every top-level PART node of one cfg file."""
    parsed = saveparse.parse_sfs(strip_comments(text))
    if not parsed.ok and not tolerable_parse_error(parsed.error):
        raise ValueError("%s: parse error: %s" % (source, parsed.error))
    parts = []
    for part in parsed.root.nodes_named("PART"):
        name = part.value("name")
        if not name:
            continue
        parts.append({
            "name": runtime_part_name(name),
            "title": (part.value("title") or "").strip(),
            "tech": (part.value("TechRequired") or "").strip(),
            "mass": part.value("mass") or "",
            "cost": part.value("cost") or "",
            "category": part.value("category") or "",
            "source": source,
        })
    return parts


def collect_parts(gamedata: str) -> List[Dict]:
    parts: List[Dict] = []
    for root_name in STOCK_PART_ROOTS:
        root = os.path.join(gamedata, root_name)
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames.sort()
            if "Parts" not in os.path.relpath(dirpath, root).split(os.sep):
                continue
            for fn in sorted(filenames):
                if not fn.lower().endswith(".cfg"):
                    continue
                path = os.path.join(dirpath, fn)
                with open(path, "r", encoding="utf-8", errors="replace") as fh:
                    rel = os.path.relpath(path, gamedata).replace(os.sep, "/")
                    parts.extend(parse_parts(fh.read(), rel))
    return parts


def load_localization(gamedata: str) -> Dict[str, str]:
    """The en-us `#autoLOC_*` strings of the stock dictionaries, so titles read as
    the player sees them. Read line-wise: a dictionary value may hold `//` or `=`."""
    table: Dict[str, str] = {}
    for root_name in STOCK_PART_ROOTS:
        root = os.path.join(gamedata, root_name)
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames.sort()
            if os.path.basename(dirpath) != "Localization":
                continue
            for fn in sorted(filenames):
                if not fn.lower().endswith(".cfg"):
                    continue
                with open(os.path.join(dirpath, fn), "r", encoding="utf-8-sig",
                          errors="replace") as fh:
                    in_en = False
                    depth = 0
                    for raw in fh:
                        line = raw.strip()
                        if line == "en-us":
                            in_en = True
                            depth = 0
                            continue
                        if in_en:
                            if line == "{":
                                depth += 1
                                continue
                            if line == "}":
                                depth -= 1
                                if depth <= 0:
                                    in_en = False
                                continue
                            key, sep, value = line.partition("=")
                            if sep and key.strip().startswith("#"):
                                table.setdefault(key.strip(), value.strip())
    return table


def localize(text: str, table: Dict[str, str]) -> str:
    """Resolve a `#autoLOC_*` title, decode the dictionary's `\\u0020` escape and keep
    the listing ASCII (house style: `Mystery Goo(TM)` loses its trademark sign)."""
    value = table.get(text, text) if text.startswith("#") else text
    value = value.replace("\\u0020", " ").strip()
    return value.encode("ascii", "ignore").decode("ascii")


def build_listing(nodes: List[Dict], parts: List[Dict]) -> Tuple[Dict[str, List[Dict]], List[Dict]]:
    """Node id -> its parts (sorted by name), plus the parts no node unlocks."""
    by_node: Dict[str, List[Dict]] = {n["id"]: [] for n in nodes}
    orphans: List[Dict] = []
    for p in parts:
        if p["tech"] in by_node:
            by_node[p["tech"]].append(p)
        else:
            orphans.append(p)
    for v in by_node.values():
        v.sort(key=lambda p: p["name"])
    orphans.sort(key=lambda p: (p["tech"], p["name"]))
    return by_node, orphans


def cumulative_cost(nodes: List[Dict]) -> Dict[str, int]:
    """Science to research a node and every ancestor it needs (any one parent of an
    any-parent node is enough in stock, so the cheapest path is taken)."""
    by_id = {n["id"]: n for n in nodes}
    memo: Dict[str, int] = {}

    def walk(nid: str, seen: Tuple[str, ...]) -> int:
        if nid in memo:
            return memo[nid]
        node = by_id.get(nid)
        if node is None or nid in seen:
            return 0
        if not node["parents"]:
            total = node["cost"]
        else:
            total = node["cost"] + min(walk(p, seen + (nid,)) for p in node["parents"])
        memo[nid] = total
        return total

    return {n["id"]: walk(n["id"], ()) for n in nodes}


def render_markdown(nodes: List[Dict], by_node: Dict[str, List[Dict]], orphans: List[Dict],
                    gamedata_label: str) -> str:
    cum = cumulative_cost(nodes)
    lines = [
        "# Stock tech tree: parts per node (KSP 1.12.5)",
        "",
        "Generated by `harness/tools/list_tech_unlocks.py` from the dev install's unpatched",
        "`%s` (`Squad` + `SquadExpansion` only). Do not edit by hand; re-run the tool."
        % gamedata_label,
        "It is the craft budget of the scripted campaign program",
        "(`docs/dev/autotest-roadmap.md`, \"The scripted campaign program\") on the",
        "`stock-tree` instance. `cost` is the node's own science; `path` is the cheapest",
        "science total of the node plus the ancestors it needs. Part names are the",
        "runtime dot form.",
        "",
    ]
    order = sorted(nodes, key=lambda n: (cum[n["id"]], n["cost"], n["id"]))
    for n in order:
        lines.append("## %s (`%s`)" % (n["title"] or n["id"], n["id"]))
        lines.append("")
        parents = ", ".join("`%s`" % p for p in n["parents"]) or "none"
        lines.append("cost %d, path %d, parents %s" % (n["cost"], cum[n["id"]], parents))
        lines.append("")
        rows = by_node.get(n["id"], [])
        if not rows:
            lines.append("No parts.")
            lines.append("")
            continue
        lines.append("| part | title | mass t | cost |")
        lines.append("|---|---|---|---|")
        for p in rows:
            lines.append("| `%s` | %s | %s | %s |" % (p["name"], p["title"], p["mass"], p["cost"]))
        lines.append("")
    lines.append("## Parts no node unlocks")
    lines.append("")
    if not orphans:
        lines.append("None.")
    else:
        lines.append("| part | TechRequired | source |")
        lines.append("|---|---|---|")
        for p in orphans:
            lines.append("| `%s` | %s | `%s` |" % (p["name"], p["tech"] or "(none)", p["source"]))
    lines.append("")
    return "\n".join(lines)


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--gamedata", required=True, help="a KSP install's GameData directory")
    ap.add_argument("--out", default=None, help="markdown file to write (default: stdout)")
    args = ap.parse_args(argv)
    tree_path = os.path.join(args.gamedata, TECH_TREE_REL)
    with open(tree_path, "r", encoding="utf-8", errors="replace") as fh:
        nodes = parse_tech_tree(fh.read())
    parts = collect_parts(args.gamedata)
    table = load_localization(args.gamedata)
    for row in nodes + parts:
        row["title"] = localize(row["title"], table)
    by_node, orphans = build_listing(nodes, parts)
    text = render_markdown(nodes, by_node, orphans, "GameData")
    if args.out:
        with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
    else:
        sys.stdout.write(text)
    sys.stderr.write("nodes=%d parts=%d unlisted=%d\n" % (len(nodes), len(parts), len(orphans)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
