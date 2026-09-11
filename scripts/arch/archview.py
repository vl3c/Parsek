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
import html
import json
import re
import subprocess
import sys
import tomllib
from collections import defaultdict
from pathlib import Path

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


def _count_quotes(source, i):
    n = len(source)
    j = i
    while j < n and source[j] == '"':
        j += 1
    return j - i


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


def _starts_string_at(source, i):
    """True when a string literal (with any $ / @ prefixes) starts at i."""
    c = source[i]
    if c == '"':
        return True
    nxt = source[i + 1 : i + 2]
    if c == "@" and nxt == '"':
        return True
    if c == "$" and nxt == '"':
        return True
    nminus1 = source[i + 2 : i + 3]
    if c in ("$", "@") and nxt in ("$", "@") and nxt != c and nminus1 == '"':
        return True
    return False


def _scan_interpolation_hole(source, i, out):
    """Skip one {..} hole starting at the brace; return the index after it.

    The hole body is code, so it is recursively stripped and kept in `out`
    (type names used only inside an interpolated string are real references).
    Nested braces, strings, char literals and comments inside the hole are
    scanned as units so their braces cannot close the hole early.
    """
    n = len(source)
    start = i + 1
    depth = 1
    j = start
    while j < n:
        c = source[j]
        if c == "{":
            depth += 1
            j += 1
            continue
        if c == "}":
            depth -= 1
            if depth == 0:
                out.append(strip_comments_and_strings(source[start:j]))
                return j + 1
            j += 1
            continue
        if c == "'":
            j = _skip_char_literal(source, j + 1)
            continue
        if _starts_string_at(source, j):
            j = _skip_string_literal(source, j, out)
            continue
        if c == "/" and source[j + 1 : j + 2] == "/":
            k = source.find("\n", j)
            j = n if k == -1 else k
            continue
        if c == "/" and source[j + 1 : j + 2] == "*":
            k = source.find("*/", j + 2)
            j = n if k == -1 else k + 2
            continue
        j += 1
    out.append(strip_comments_and_strings(source[start:n]))
    return n


def _skip_raw_string(source, i, quote_count, interpolated, out):
    n = len(source)
    j = i + quote_count
    while j < n:
        c = source[j]
        if c == '"':
            run = _count_quotes(source, j)
            if run >= quote_count:
                return j + run
            j += run
            continue
        if interpolated and c == "{":
            if source[j + 1 : j + 2] == "{":
                j += 2
                continue
            j = _scan_interpolation_hole(source, j, out)
            continue
        j += 1
    return n


def _skip_regular_body(source, i, verbatim, interpolated, out):
    n = len(source)
    while i < n:
        c = source[i]
        if not verbatim and c == "\\":
            i += 2
            continue
        if c == '"':
            if verbatim and source[i + 1 : i + 2] == '"':
                i += 2
                continue
            return i + 1
        if interpolated and c == "{":
            if source[i + 1 : i + 2] == "{":
                i += 2
                continue
            i = _scan_interpolation_hole(source, i, out)
            continue
        if interpolated and c == "}" and source[i + 1 : i + 2] == "}":
            i += 2
            continue
        i += 1
    return n


def _skip_string_literal(source, i, out):
    """Skip one string literal beginning at i (with any $ / @ prefixes).

    Returns the index after the literal. String text is discarded; code inside
    interpolation holes is recursively stripped and appended to `out`.
    """
    n = len(source)
    verbatim = False
    interpolated = False
    while i < n and source[i] in ("$", "@"):
        if source[i] == "$":
            interpolated = True
        else:
            verbatim = True
        i += 1
    if i >= n or source[i] != '"':
        return i
    quote_count = _count_quotes(source, i)
    if quote_count >= 3:
        return _skip_raw_string(source, i, quote_count, interpolated, out)
    return _skip_regular_body(source, i + 1, verbatim, interpolated, out)


def strip_comments_and_strings(source):
    """Remove // and /* */ comments and string / char literals.

    A single left-to-right scan, so a `//` or `/*` inside a string is not a
    comment and a `"` inside a comment is not a string. Interpolation holes
    keep their code (recursively stripped) so a reference inside `$"{...}"`
    still counts; string text, including text inside holes of nested strings,
    is discarded.
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
        if _starts_string_at(source, i):
            i = _skip_string_literal(source, i, out)
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


def _write_text(path, text):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def _dot_escape(text):
    return text.replace("\\", "\\\\").replace('"', '\\"')


def render_dot(model, min_edge):
    """Return the Graphviz source for the production module graph."""
    modules = {m["name"]: m for m in model["modules"] if not m["tooling"]}
    edges = [
        e
        for e in model["edges"]
        if e["from"] in modules and e["to"] in modules and e["weight"] >= min_edge
    ]
    max_weight = max((e["weight"] for e in edges), default=1)

    lines = [
        "digraph modules {",
        "  rankdir=TB;",
        '  graph [fontname="Helvetica", ranksep="0.75", nodesep="0.45"];',
        "  node [shape=box, style=\"rounded,filled\", fontname=\"Helvetica\", fontsize=10,"
        ' fillcolor="#eef3fa", color="#4c78a8"];',
        '  edge [fontname="Helvetica", fontsize=8, color="#8a949e", arrowsize=0.7];',
    ]
    for name in sorted(modules):
        entry = modules[name]
        tooltip = "%s: %d files, fan-out %d, fan-in %d, instability %.2f" % (
            name,
            entry["files"],
            entry["fanOut"],
            entry["fanIn"],
            entry["instability"],
        )
        lines.append(
            '  "%s" [label="%s\\n%d files", tooltip="%s"];'
            % (_dot_escape(name), _dot_escape(name), entry["files"], _dot_escape(tooltip))
        )
    for edge in edges:
        penwidth = 1.0 + 4.0 * edge["weight"] / max_weight
        color = "#cc0000" if edge["cyclic"] else "#8a949e"
        types = ", ".join(edge["types"][:12])
        if len(edge["types"]) > 12:
            types += " (+%d more)" % (len(edge["types"]) - 12)
        tooltip = "%s -> %s: %d references; types: %s" % (
            edge["from"],
            edge["to"],
            edge["weight"],
            types,
        )
        lines.append(
            '  "%s" -> "%s" [penwidth=%.2f, color="%s", tooltip="%s"];'
            % (
                _dot_escape(edge["from"]),
                _dot_escape(edge["to"]),
                penwidth,
                color,
                _dot_escape(tooltip),
            )
        )
    lines.append("}")
    return "\n".join(lines) + "\n"


def render_svg(dot_path, svg_path):
    """Run graphviz over the dot file. Warns and returns False when absent."""
    try:
        completed = subprocess.run(
            ["dot", "-Tsvg", str(dot_path), "-o", str(svg_path)],
            capture_output=True,
            text=True,
            check=False,
        )
    except FileNotFoundError:
        print("WARN graphviz 'dot' not found; modules.svg not written", file=sys.stderr)
        return False
    if completed.returncode != 0:
        print(
            "WARN dot failed (exit %d): %s" % (completed.returncode, completed.stderr.strip()),
            file=sys.stderr,
        )
        return False
    return True


def render_matrix_html(model):
    """Return the dependency structure matrix as a standalone HTML page."""
    modules = sorted(
        (m for m in model["modules"] if not m["tooling"]),
        key=lambda m: (m["instability"], m["name"]),
    )
    by_name = {m["name"]: m for m in modules}
    names = [m["name"] for m in modules]
    prod = set(names)
    edge_map = {(e["from"], e["to"]): e for e in model["edges"]}
    prod_edges = [e for e in model["edges"] if e["from"] in prod and e["to"] in prod]
    max_weight = max((e["weight"] for e in prod_edges), default=1)
    tooling = sorted(m["name"] for m in model["modules"] if m["tooling"])

    def shade(weight):
        alpha = 0.06 + 0.74 * (weight / max_weight) ** 0.6
        return "background-color:rgba(46,94,156,%.3f)" % alpha

    rows = []
    header = ['<th class="corner">from \\ to</th>']
    for name in names:
        header.append('<th class="col">%s</th>' % html.escape(name))
    header.append('<th class="sum">I</th>')
    rows.append("<tr>%s</tr>" % "".join(header))
    for name in names:
        cells = ['<th class="row">%s</th>' % html.escape(name)]
        for other in names:
            if other == name:
                cells.append('<td class="diag">%d</td>' % by_name[name]["files"])
                continue
            edge = edge_map.get((name, other))
            if edge is None:
                cells.append("<td></td>")
                continue
            css_class = "cell cyc" if edge["cyclic"] else "cell"
            types = ", ".join(edge["types"][:16])
            cells.append(
                '<td class="%s" style="%s" title="%d references: %s">%d</td>'
                % (css_class, shade(edge["weight"]), edge["weight"], html.escape(types), edge["weight"])
            )
        cells.append('<td class="sum">%.2f</td>' % by_name[name]["instability"])
        rows.append("<tr>%s</tr>" % "".join(cells))
    footer = ['<th class="row">fan-in</th>']
    for name in names:
        footer.append('<td class="sum">%d</td>' % by_name[name]["fanIn"])
    footer.append("<td></td>")
    rows.append("<tr>%s</tr>" % "".join(footer))

    lines = [
        "<!DOCTYPE html>",
        '<html lang="en">',
        "<head>",
        '<meta charset="utf-8">',
        "<title>Parsek dependency structure matrix</title>",
        "<style>",
        'body { font-family: "Segoe UI", Arial, sans-serif; margin: 20px; color: #222; }',
        "h1 { font-size: 18px; margin-bottom: 4px; }",
        "p.note { font-size: 12px; color: #555; max-width: 980px; }",
        "table { border-collapse: collapse; font-size: 11px; }",
        "th, td { border: 1px solid #d7dde4; padding: 3px 5px; text-align: center; }",
        "th.col { writing-mode: vertical-rl; transform: rotate(180deg); height: 120px;"
        " vertical-align: bottom; font-weight: 600; }",
        "th.row { text-align: left; font-weight: 600; white-space: nowrap; padding-right: 8px; }",
        "th.corner { text-align: left; color: #777; font-weight: 400; }",
        "td.diag { background: #f0f2f5; color: #666; }",
        "td.cyc { outline: 2px solid #cc0000; outline-offset: -2px; }",
        "td.sum { background: #fafbfc; color: #444; }",
        ".legend { margin: 10px 0; font-size: 12px; }",
        ".swatch { display: inline-block; width: 12px; height: 12px; margin: 0 5px 0 14px;"
        " vertical-align: -2px; border: 1px solid #bbb; }",
        "</style>",
        "</head>",
        "<body>",
        "<h1>Parsek dependency structure matrix</h1>",
        '<p class="note">Modules are ordered by instability ascending, so sinks (low'
        " instability) come first. Cell value is the number of cross-module references from"
        " the row module to the column module; shade scales with that count. A red outline"
        " marks a cell whose two modules sit in the same dependency cycle (same strongly"
        " connected component); the way back may run through other modules. Diagonal is the"
        " file count.</p>",
        '<p class="note">Generated by <code>scripts/arch/archview.py</code>; do not edit by'
        " hand. Production modules only; hidden tooling modules: %s.</p>" % html.escape(", ".join(tooling)),
        '<p class="legend"><span class="swatch" style="background:#e8eef7"></span>low'
        '<span class="swatch" style="background:#2e5e9c"></span>high reference count'
        '<span class="swatch" style="outline:2px solid #cc0000; outline-offset:-2px"></span>on a cycle</p>',
        "<table>",
    ]
    lines.extend("  " + row for row in rows)
    lines.extend(["</table>", "</body>", "</html>", ""])
    return "\n".join(lines)


EXPLORE_TEMPLATE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Parsek module explorer</title>
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color: #222; }
#app { display: flex; height: 100%; }
#main { flex: 1 1 auto; display: flex; flex-direction: column; min-width: 0; }
#controls { padding: 8px 12px; border-bottom: 1px solid #d0d6dd; display: flex; gap: 18px;
  align-items: center; flex-wrap: wrap; font-size: 13px; }
#cy { flex: 1 1 auto; min-height: 0; background: #fcfdfe; }
#panel { width: 340px; flex: 0 0 340px; border-left: 1px solid #d0d6dd; overflow: auto;
  padding: 10px 14px; font-size: 13px; box-sizing: border-box; }
#panel h2 { font-size: 15px; margin: 4px 0 2px; }
#panel h3 { font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; color: #667;
  margin: 14px 0 4px; }
#panel p.metrics { color: #555; font-size: 12px; margin: 0 0 6px; }
#panel p.hint, #panel p.none { color: #777; }
#panel ul { list-style: none; margin: 0; padding: 0; }
#panel li { padding: 5px 0; border-bottom: 1px solid #eef1f4; }
#panel .w { color: #2e5e9c; font-weight: 600; }
#panel .types { color: #667; font-size: 11px; word-wrap: break-word; }
.legend { color: #555; }
.swatch { display: inline-block; width: 12px; height: 12px; vertical-align: -2px;
  margin-right: 4px; border: 1px solid #bbb; }
.swatch.edge { height: 4px; background: #9aa4b1; }
.swatch.red { background: #cc0000; }
.swatch.gray { background: #9aa4b1; }
</style>
</head>
<body>
<div id="app">
  <div id="main">
    <div id="controls">
      <label><input type="checkbox" id="tooling"> show tooling modules</label>
      <label>min edge weight
        <input type="range" id="minw" min="1" max="@@MAX_WEIGHT@@" value="@@MIN_EDGE@@">
        <span id="minwv">@@MIN_EDGE@@</span>
      </label>
      <span class="legend"><span class="swatch edge"></span>edge width = reference count</span>
      <span class="legend"><span class="swatch red"></span>edge on a cycle</span>
      <span class="legend"><span class="swatch gray"></span>tooling module</span>
    </div>
    <div id="cy"></div>
  </div>
  <div id="panel">
    <p class="hint">Click a module to see its direct neighbours.</p>
  </div>
</div>
<script src="https://cdnjs.cloudflare.com/ajax/libs/cytoscape/3.30.2/cytoscape.min.js"></script>
<script>
"use strict";
const DATA = @@DATA@@;
const MIN_EDGE = @@MIN_EDGE@@;
const MAX_WEIGHT = @@MAX_WEIGHT@@;
const MODULES = DATA.modules;
const EDGES = DATA.edges;
const MAP_MAX = Math.max(MAX_WEIGHT, 2);

let showTooling = false;
let minWeight = MIN_EDGE;
let cy = null;

function esc(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function isTooling(name) {
  const found = MODULES.find(function (m) { return m.name === name; });
  return found ? found.tooling : false;
}

function panelHint() {
  document.getElementById("panel").innerHTML =
    '<p class="hint">Click a module to see its direct neighbours.</p>';
}

function edgeSection(title, edges, other) {
  if (!edges.length) {
    return "<h3>" + title + "</h3><p class=\\"none\\">none</p>";
  }
  const sorted = edges.slice().sort(function (a, b) { return b.weight - a.weight; });
  let body = "<ul>";
  sorted.forEach(function (e) {
    body += "<li><b>" + esc(e[other]) + "</b> <span class=\\"w\\">" + e.weight + "</span>" +
      '<div class="types">' + esc(e.types.join(", ")) + "</div></li>";
  });
  return "<h3>" + title + "</h3>" + body + "</ul>";
}

function renderPanel(name) {
  const module = MODULES.find(function (m) { return m.name === name; });
  const outgoing = EDGES.filter(function (e) {
    return e.from === name && (showTooling || !isTooling(e.to));
  });
  const incoming = EDGES.filter(function (e) {
    return e.to === name && (showTooling || !isTooling(e.from));
  });
  const panel = document.getElementById("panel");
  panel.innerHTML =
    "<h2>" + esc(module.name) + "</h2>" +
    '<p class="metrics">' + module.files + " files - fan-out " + module.fanOut +
    ", fan-in " + module.fanIn + ", instability " + module.instability.toFixed(2) + "</p>" +
    edgeSection("outgoing", outgoing, "to") +
    edgeSection("incoming", incoming, "from");
  panel.scrollTop = 0;
}

function clearFocus() {
  cy.elements().removeClass("faded focused");
}

function focusModule(name) {
  clearFocus();
  const keep = new Set([name]);
  cy.edges().forEach(function (e) {
    if (e.data("source") === name || e.data("target") === name) {
      keep.add(e.data("source"));
      keep.add(e.data("target"));
    }
  });
  cy.elements().addClass("faded");
  cy.nodes().forEach(function (n) {
    if (keep.has(n.id())) n.removeClass("faded");
  });
  cy.getElementById(name).removeClass("faded").addClass("focused");
  cy.edges().forEach(function (e) {
    if (e.data("source") === name || e.data("target") === name) e.removeClass("faded");
  });
  renderPanel(name);
}

function rebuild() {
  const modules = MODULES.filter(function (m) { return showTooling || !m.tooling; });
  const names = new Set(modules.map(function (m) { return m.name; }));
  const elements = modules.map(function (m) {
    return { data: { id: m.name, label: m.name + "\\n" + m.files + " files", tooling: m.tooling } };
  });
  EDGES.forEach(function (e) {
    if (!names.has(e.from) || !names.has(e.to)) return;
    if (e.weight < minWeight) return;
    elements.push({ data: {
      id: e.from + "->" + e.to,
      source: e.from,
      target: e.to,
      weight: e.weight,
      cyclic: e.cyclic
    } });
  });
  cy.elements().remove();
  cy.add(elements);
  cy.layout({ name: "circle", padding: 40, avoidOverlap: true, spacingFactor: 1.15 }).run();
  clearFocus();
  panelHint();
}

if (typeof cytoscape === "undefined") {
  document.getElementById("cy").innerHTML =
    '<p style="padding:20px;color:#666">Cytoscape.js could not be loaded from the CDN' +
    " (offline?). edges.json and matrix.html still carry the full data.</p>";
} else {
  cy = cytoscape({
    container: document.getElementById("cy"),
    elements: [],
    wheelSensitivity: 0.25,
    style: [
      { selector: "node", style: {
        label: "data(label)",
        "font-size": 9,
        color: "#ffffff",
        "text-valign": "center",
        "text-halign": "center",
        "text-wrap": "wrap",
        "text-max-width": 96,
        width: 104,
        height: 38,
        "background-color": "#4c78a8"
      } },
      { selector: "node[?tooling]", style: { "background-color": "#9aa4b1" } },
      { selector: "edge", style: {
        width: "mapData(weight, 1, " + MAP_MAX + ", 1, 10)",
        "line-color": "#9aa4b1",
        "target-arrow-color": "#9aa4b1",
        "target-arrow-shape": "triangle",
        "arrow-scale": 0.8,
        "curve-style": "bezier"
      } },
      { selector: "edge[?cyclic]", style: {
        "line-color": "#cc0000",
        "target-arrow-color": "#cc0000"
      } },
      { selector: ".faded", style: { opacity: 0.1 } },
      { selector: "node.focused", style: { "border-width": 4, "border-color": "#e8810c" } }
    ]
  });

  cy.on("tap", "node", function (evt) { focusModule(evt.target.id()); });
  cy.on("tap", function (evt) {
    if (evt.target === cy) { clearFocus(); panelHint(); }
  });

  document.getElementById("tooling").addEventListener("change", function () {
    showTooling = this.checked;
    rebuild();
  });
  document.getElementById("minw").addEventListener("input", function () {
    minWeight = parseInt(this.value, 10);
    document.getElementById("minwv").textContent = String(minWeight);
    rebuild();
  });

  rebuild();
}
</script>
</body>
</html>
"""


def render_explore_html(model, min_edge):
    """Return the interactive neighbourhood explorer as a standalone page."""
    max_weight = max((e["weight"] for e in model["edges"]), default=1)
    slider_max = max(max_weight, 1)
    slider_value = min(max(min_edge, 1), slider_max)
    payload = {"modules": model["modules"], "edges": model["edges"]}
    data = json.dumps(payload, ensure_ascii=True, separators=(",", ":")).replace("</", "<\\/")
    text = EXPLORE_TEMPLATE
    text = text.replace("@@DATA@@", data)
    text = text.replace("@@MIN_EDGE@@", str(slider_value))
    text = text.replace("@@MAX_WEIGHT@@", str(slider_max))
    return text


# ---------------------------------------------------------------------------
# checker (report-only)
# ---------------------------------------------------------------------------


def parse_edge_spec(spec):
    """Return (from, to) for a "From -> To" policy string."""
    parts = [part.strip() for part in spec.split("->")]
    if len(parts) != 2 or not parts[0] or not parts[1]:
        raise ValueError("bad edge spec: %r" % spec)
    return parts[0], parts[1]


def format_metrics_table(model):
    """Render the production-module metrics table as plain text."""
    modules = sorted(
        (m for m in model["modules"] if not m["tooling"]),
        key=lambda m: (-(m["fanIn"] + m["fanOut"]), m["name"]),
    )
    header = ("module", "files", "fan-out", "fan-in", "I")
    rows = [
        (
            m["name"],
            str(m["files"]),
            str(m["fanOut"]),
            str(m["fanIn"]),
            "%.2f" % m["instability"],
        )
        for m in modules
    ]
    widths = [
        max(len(header[i]), max((len(row[i]) for row in rows), default=0)) for i in range(5)
    ]
    lines = ["  ".join(header[i].ljust(widths[i]) for i in range(5)).rstrip()]
    lines.append("  ".join("-" * width for width in widths))
    for row in rows:
        lines.append("  ".join(row[i].ljust(widths[i]) for i in range(5)).rstrip())
    return "\n".join(lines)


def two_way_couplings(model):
    """Return (a, b, weightAB, weightBA) for every mutually coupled pair."""
    production = {m["name"] for m in model["modules"] if not m["tooling"]}
    weight = {(e["from"], e["to"]): e["weight"] for e in model["edges"]}
    pairs = []
    for (a, b) in weight:
        if a >= b:
            continue
        if a in production and b in production and (b, a) in weight:
            pairs.append((a, b, weight[(a, b)], weight[(b, a)]))
    pairs.sort(key=lambda pair: (-max(pair[2], pair[3]), pair[0], pair[1]))
    return pairs


def run_check(model, forbidden, allowed):
    """Print the report-only architecture check.

    Never raises on a malformed policy spec: a bad "From -> To" string is
    reported and skipped, because the final line and the exit code are part of
    the report contract.
    """
    print()
    hidden = sorted(m["name"] for m in model["modules"] if m["tooling"])
    print("Per-module metrics (production modules, sorted by fan-in + fan-out):")
    print(format_metrics_table(model))
    if hidden:
        print("Tooling modules hidden from this table and the views: %s." % ", ".join(hidden))

    print()
    pairs = two_way_couplings(model)
    print("Two-way couplings (production modules only):")
    if not pairs:
        print("  none.")
    for a, b, weight_ab, weight_ba in pairs:
        print("  %s <-> %s: %s->%s=%d, %s->%s=%d" % (a, b, a, b, weight_ab, b, a, weight_ba))

    lookup = {(e["from"], e["to"]): e for e in model["edges"]}
    print()
    print("Forbidden edges (declared boundaries that must not exist):")
    violations = 0
    for spec in forbidden:
        try:
            frm, to = parse_edge_spec(spec)
        except ValueError as exc:
            print("  WARN bad [forbidden] edge spec: %s" % exc)
            continue
        edge = lookup.get((frm, to))
        if edge is None:
            continue
        violations += 1
        print("  VIOLATION %s -> %s: weight=%d" % (frm, to, edge["weight"]))
        print("    referenced types: %s" % ", ".join(edge["types"]))
        print("    referencing files:")
        for rel_path in edge["files"]:
            print("      %s" % rel_path)
    if violations == 0:
        print("  none found.")

    if allowed:
        allowed_edges = set()
        for spec in allowed:
            try:
                allowed_edges.add(parse_edge_spec(spec))
            except ValueError as exc:
                print("  WARN bad [allowed] edge spec: %s" % exc)
        production = {m["name"] for m in model["modules"] if not m["tooling"]}
        print()
        print("Production edges not in [allowed] (%d allowlist entries):" % len(allowed_edges))
        unexpected = [
            e
            for e in model["edges"]
            if e["from"] in production
            and e["to"] in production
            and (e["from"], e["to"]) not in allowed_edges
        ]
        if not unexpected:
            print("  none.")
        for edge in unexpected:
            print("  %s -> %s: weight=%d" % (edge["from"], edge["to"], edge["weight"]))
    print()
    print("ARCH-CHECK report-only")


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
    parser.add_argument(
        "--check",
        action="store_true",
        help="print the report-only architecture check after generating the views",
    )
    args = parser.parse_args(argv)

    # Report-only contract: a missing or malformed input is a warning, never a
    # nonzero exit, and --check always ends with the ARCH-CHECK line.
    forbidden = []
    allowed = []
    model = None
    try:
        rules, tooling, forbidden, allowed = load_rules(args.modules)
        model = build_model(args.source, rules, tooling)
    except Exception as exc:
        print("WARN archview: %s" % exc, file=sys.stderr)

    if model is not None:
        try:
            for rel_path in model["unclassified"]:
                print("WARN unclassified file: %s (add a rule to modules.toml)" % rel_path)

            out_dir = Path(args.out)
            out_dir.mkdir(parents=True, exist_ok=True)

            json_path = out_dir / "edges.json"
            write_json(model, json_path)
            print("Wrote %s" % json_path)

            dot_path = out_dir / "modules.dot"
            _write_text(dot_path, render_dot(model, args.min_edge))
            print("Wrote %s" % dot_path)

            svg_path = out_dir / "modules.svg"
            if render_svg(dot_path, svg_path):
                print("Wrote %s" % svg_path)

            matrix_path = out_dir / "matrix.html"
            _write_text(matrix_path, render_matrix_html(model))
            print("Wrote %s" % matrix_path)

            explore_path = out_dir / "explore.html"
            _write_text(explore_path, render_explore_html(model, args.min_edge))
            print("Wrote %s" % explore_path)
        except Exception as exc:
            print("WARN archview: %s" % exc, file=sys.stderr)

    if args.check:
        if model is not None:
            try:
                run_check(model, forbidden, allowed)
            except Exception as exc:
                print("WARN arch-check: %s" % exc, file=sys.stderr)
                print()
                print("ARCH-CHECK report-only")
        else:
            print()
            print("ARCH-CHECK report-only")
    return 0


if __name__ == "__main__":
    sys.exit(main())
