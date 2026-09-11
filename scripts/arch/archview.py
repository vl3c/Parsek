#!/usr/bin/env python3
"""Parsek module dependency map.

Reads scripts/arch/modules.toml, walks Source/Parsek/**/*.cs, and writes a
module-level dependency graph plus three views (Graphviz layered digraph,
dependency structure matrix, interactive neighbourhood explorer) into
docs/dev/arch/.

APPROXIMATION NOTICE: this is a source-text scan, not a compiler. References
are found by matching identifiers against a table of declared type names, so
it can miss a reference spelled in a way the text does not literally contain
(aliases, nameof, generic inference, reflection) and can over-count a name
reused as an unqualified local or a string (string literals and comments are
stripped first, which removes the common false positives). The scan is
deliberately good enough to answer "which modules reference which" at the
folder granularity; it is not sound enough to gate a build. See
docs/dev/arch/README.md.

Every decision function here (assign_module, strip_comments_and_strings,
declared_types, references, metrics, sccs) is pure: it takes data and returns
data, and performs no I/O, so the unit tests can drive it directly.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tomllib
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SOURCE = "Source/Parsek"
DEFAULT_OUT = "docs/dev/arch"
DEFAULT_MIN_EDGE = 8
DEFAULT_MODULES = Path(__file__).resolve().parent / "modules.toml"

SKIP_DIR_NAMES = {"bin", "obj", "Properties"}
MIN_TYPE_NAME_LEN = 4

TYPE_DECL_RE = re.compile(r"\b(?:class|struct|interface|enum)\s+([A-Z][A-Za-z0-9_]+)")
IDENT_RE = re.compile(r"\b[A-Z][A-Za-z0-9_]{3,}\b")
PATH_SPLIT_RE = re.compile(r"[\\/]")


# ---------------------------------------------------------------------------
# pure decision functions
# ---------------------------------------------------------------------------


def assign_module(rel_path, rules):
    """Return the module name for a source path, or None when unclassified.

    `rel_path` is relative to Source/Parsek/ and may use / or \\ separators.
    Rules are evaluated in order and the first match wins. `folder` rules match
    the first path segment and only apply to files inside a subfolder; `prefix`
    rules are regexes for the file name of a root-level file.
    """
    parts = [p for p in PATH_SPLIT_RE.split(rel_path) if p]
    if not parts:
        return None
    if len(parts) > 1:
        first = parts[0]
        for rule in rules:
            if rule.get("folder") == first:
                return rule["name"]
        return None
    name = parts[-1]
    for rule in rules:
        prefix = rule.get("prefix")
        if prefix and re.match(prefix, name):
            return rule["name"]
    return None


def _skip_regular_string(source, i):
    n = len(source)
    while i < n:
        c = source[i]
        if c == "\\":
            i += 2
            continue
        if c == '"':
            return i + 1
        i += 1
    return n


def _skip_verbatim_string(source, i):
    n = len(source)
    while i < n:
        if source[i] == '"':
            if i + 1 < n and source[i + 1] == '"':
                i += 2
                continue
            return i + 1
        i += 1
    return n


def _skip_char_literal(source, i):
    n = len(source)
    while i < n:
        c = source[i]
        if c == "\\":
            i += 2
            continue
        if c == "'":
            return i + 1
        i += 1
    return n


def strip_comments_and_strings(source):
    """Remove // and /* */ comments and string / char literals.

    A single left-to-right scan, so a `//` or `/*` inside a string is not a
    comment and a `"` inside a comment is not a string. Interpolated strings
    are stripped whole (their {..} holes are not scanned); that is part of the
    documented approximation.
    """
    out = []
    i = 0
    n = len(source)
    while i < n:
        c = source[i]
        nxt = source[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = source.find("\n", i)
            i = n if j == -1 else j
            continue
        if c == "/" and nxt == "*":
            j = source.find("*/", i + 2)
            i = n if j == -1 else j + 2
            continue
        if c == "@" and nxt == '"':
            i = _skip_verbatim_string(source, i + 2)
            continue
        if c == "$" and nxt == "@" and source[i + 2 : i + 3] == '"':
            i = _skip_verbatim_string(source, i + 3)
            continue
        if c == "@" and nxt == "$" and source[i + 2 : i + 3] == '"':
            i = _skip_verbatim_string(source, i + 3)
            continue
        if c == "$" and nxt == '"':
            i = _skip_regular_string(source, i + 2)
            continue
        if c == '"':
            if source.startswith('"""', i):
                j = source.find('"""', i + 3)
                i = n if j == -1 else j + 3
                continue
            i = _skip_regular_string(source, i + 1)
            continue
        if c == "'":
            i = _skip_char_literal(source, i + 1)
            continue
        out.append(c)
        i += 1
    return "".join(out)


def declared_types(source):
    """Return type names declared in already-stripped C# source.

    Names shorter than four characters are ignored because they collide with
    too much ordinary short-name traffic.
    """
    names = []
    for match in TYPE_DECL_RE.finditer(source):
        name = match.group(1)
        if len(name) >= MIN_TYPE_NAME_LEN:
            names.append(name)
    return names


def references(source, type_modules, from_module):
    """Return distinct (type_name, module) pairs referenced from this file.

    `type_modules` maps a declared type name to its owning module. References
    to types owned by `from_module` are excluded, so only cross-module edges
    are produced. First-seen order is preserved for deterministic output.
    """
    found = []
    seen = set()
    for match in IDENT_RE.finditer(source):
        ident = match.group(0)
        target = type_modules.get(ident)
        if target is None or target == from_module or ident in seen:
            continue
        seen.add(ident)
        found.append((ident, target))
    return found


def metrics(module_files, edge_weights):
    """Compute files / fanIn / fanOut / instability for every module.

    `module_files` maps name to file count; `edge_weights` is an iterable of
    (from, to, weight). Instability is fanOut / (fanOut + fanIn), defined as
    0.0 for a module with no edges at all.
    """
    result = {
        name: {"files": count, "fanIn": 0, "fanOut": 0, "instability": 0.0}
        for name, count in module_files.items()
    }
    for frm, to, weight in edge_weights:
        if frm in result:
            result[frm]["fanOut"] += weight
        if to in result:
            result[to]["fanIn"] += weight
    for entry in result.values():
        total = entry["fanIn"] + entry["fanOut"]
        entry["instability"] = (entry["fanOut"] / total) if total else 0.0
    return result


def sccs(nodes, edges):
    """Tarjan strongly connected components, iterative to avoid recursion caps.

    `nodes` is an iterable of node names; `edges` an iterable of (from, to)
    pairs. Edges naming nodes outside the set are ignored. Component order and
    the order inside each component follow the input order, so callers that
    pass sorted input get deterministic output.
    """
    adjacency = {node: [] for node in nodes}
    for frm, to in edges:
        if frm in adjacency and to in adjacency:
            adjacency[frm].append(to)

    index_of = {}
    low = {}
    stack = []
    on_stack = set()
    counter = 0
    components = []

    for root in adjacency:
        if root in index_of:
            continue
        work = [(root, 0)]
        while work:
            node, nxt = work[-1]
            if nxt == 0:
                index_of[node] = low[node] = counter
                counter += 1
                stack.append(node)
                on_stack.add(node)
            descended = False
            neighbors = adjacency[node]
            while nxt < len(neighbors):
                neighbor = neighbors[nxt]
                nxt += 1
                if neighbor not in index_of:
                    work[-1] = (node, nxt)
                    work.append((neighbor, 0))
                    descended = True
                    break
                if neighbor in on_stack:
                    low[node] = min(low[node], index_of[neighbor])
            if descended:
                continue
            work[-1] = (node, nxt)
            if low[node] == index_of[node]:
                component = []
                while True:
                    popped = stack.pop()
                    on_stack.discard(popped)
                    component.append(popped)
                    if popped == node:
                        break
                components.append(component)
            work.pop()
            if work:
                parent = work[-1][0]
                low[parent] = min(low[parent], low[node])
    return components


# ---------------------------------------------------------------------------
# extraction shell
# ---------------------------------------------------------------------------


def load_rules(path):
    """Return (rules, tooling, forbidden, allowed) from a modules.toml file."""
    with open(path, "rb") as handle:
        data = tomllib.load(handle)
    rules = data.get("module", [])
    tooling = set(data.get("tooling", {}).get("modules", []))
    forbidden = list(data.get("forbidden", {}).get("edges", []))
    allowed = list(data.get("allowed", {}).get("edges", []))
    return rules, tooling, forbidden, allowed


def iter_source_files(source_root):
    """Return sorted source-relative paths of every .cs file to scan."""
    source_root = Path(source_root)
    files = []
    for path in source_root.rglob("*.cs"):
        rel = path.relative_to(source_root)
        if any(part in SKIP_DIR_NAMES for part in rel.parts[:-1]):
            continue
        files.append(rel)
    files.sort(key=lambda p: p.as_posix())
    return files


def build_model(source_root, rules, tooling):
    """Scan the source tree and return the JSON-ready model dict."""
    source_root = Path(source_root)
    assignments = []
    unclassified = []
    for rel in iter_source_files(source_root):
        module = assign_module(rel.as_posix(), rules)
        if module is None:
            unclassified.append(rel.as_posix())
        else:
            assignments.append((rel.as_posix(), module))

    stripped = {}
    for rel_path, _module in assignments:
        text = (source_root / rel_path).read_text(encoding="utf-8-sig", errors="replace")
        stripped[rel_path] = strip_comments_and_strings(text)

    type_modules = {}
    for rel_path, module in assignments:
        for name in declared_types(stripped[rel_path]):
            if name not in type_modules:
                type_modules[name] = module

    module_files = defaultdict(int)
    for _rel_path, module in assignments:
        module_files[module] += 1

    edge_files = defaultdict(set)
    edge_types = defaultdict(set)
    weights = defaultdict(int)
    for rel_path, module in assignments:
        for type_name, target in references(stripped[rel_path], type_modules, module):
            key = (module, target)
            weights[key] += 1
            edge_files[key].add(rel_path)
            edge_types[key].add(type_name)

    module_metrics = metrics(module_files, [(f, t, w) for (f, t), w in weights.items()])
    modules = []
    for name in sorted(module_metrics):
        entry = module_metrics[name]
        modules.append(
            {
                "name": name,
                "files": entry["files"],
                "fanIn": entry["fanIn"],
                "fanOut": entry["fanOut"],
                "instability": round(entry["instability"], 4),
                "tooling": name in tooling,
            }
        )

    production = sorted(m["name"] for m in modules if not m["tooling"])
    production_edges = [
        (frm, to) for (frm, to) in weights if frm in production and to in production
    ]
    components = sccs(production, production_edges)
    component_of = {}
    for component in components:
        for node in component:
            component_of[node] = component
    cyclic = {
        (frm, to)
        for (frm, to) in production_edges
        if component_of[frm] is component_of[to] and len(component_of[frm]) > 1
    }

    edges = []
    for (frm, to) in sorted(weights, key=lambda key: (-weights[key], key[0], key[1])):
        edges.append(
            {
                "from": frm,
                "to": to,
                "weight": weights[(frm, to)],
                "cyclic": (frm, to) in cyclic,
                "files": sorted(edge_files[(frm, to)]),
                "types": sorted(edge_types[(frm, to)]),
            }
        )

    return {
        "modules": modules,
        "edges": edges,
        "unclassified": unclassified,
    }


def write_json(model, out_path):
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    payload = {"modules": model["modules"], "edges": model["edges"]}
    out_path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Parsek module dependency map and views.",
    )
    parser.add_argument("--source", default=DEFAULT_SOURCE, help="module source root")
    parser.add_argument("--out", default=DEFAULT_OUT, help="output directory for views")
    parser.add_argument(
        "--min-edge",
        type=int,
        default=DEFAULT_MIN_EDGE,
        help="minimum edge weight drawn in the dot view",
    )
    parser.add_argument(
        "--modules",
        default=str(DEFAULT_MODULES),
        help="path to modules.toml",
    )
    args = parser.parse_args(argv)

    rules, tooling, _forbidden, _allowed = load_rules(args.modules)
    model = build_model(args.source, rules, tooling)
    for rel_path in model["unclassified"]:
        print("WARN unclassified file: %s (add a rule to modules.toml)" % rel_path)

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    json_path = out_dir / "edges.json"
    write_json(model, json_path)
    print("Wrote %s" % json_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
