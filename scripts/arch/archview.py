#!/usr/bin/env python3
"""Parsek module and type dependency map.

Reads scripts/arch/modules.toml, walks Source/Parsek/**/*.cs, and writes a
module-level dependency graph, a type-level ladder (types.json + ladder.html)
and the views (Graphviz layered digraph, dependency structure matrix,
interactive neighbourhood explorer) into docs/dev/arch/.

APPROXIMATION NOTICE: this is a source-text scan, not a compiler. References
are found by matching identifiers against a table of declared type names, so
it can miss a reference spelled in a way the text does not literally contain
(aliases, nameof, generic inference, reflection) and can over-count a name
reused as an unqualified local or a string (string literals and comments are
stripped first, which removes the common false positives). The scan is
deliberately good enough to answer "which modules and types reference which"
at folder granularity; it is not sound enough to gate a build. See
docs/dev/arch/README.md.

Every decision function here (assign_module, strip_comments_and_strings,
declared_types, references, metrics, sccs, type_declarations, type_references,
type_role, type_levels, type_fanin, knots, knot_hubs, greedy_sink_cuts,
sublevels, own_body_segments, scan_declaration_members, merge_type_size,
size_recommendations, size_tier, size_report, parse_growth) is pure: it takes
data and returns data, and performs no I/O, so the unit tests can drive it
directly.
"""

from __future__ import annotations

import argparse
import bisect
import datetime
import html
import json
import re
import subprocess
import sys
import textwrap
import tomllib
from collections import Counter, defaultdict
from pathlib import Path

DEFAULT_SOURCE = "Source/Parsek"
DEFAULT_OUT = "docs/dev/arch"
DEFAULT_MIN_EDGE = 8
DEFAULT_MODULES = Path(__file__).resolve().parent / "modules.toml"
DEFAULT_ATLAS = Path(__file__).resolve().parent / "atlas.toml"
REPO_ROOT = Path(__file__).resolve().parents[2]

SKIP_DIR_NAMES = {"bin", "obj", "Properties"}
MIN_TYPE_NAME_LEN = 4
HISTORY_SWEEP_LIMIT = 40

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


def strip_comments_and_strings(source, newline_drops=None):
    """Remove // and /* */ comments and string / char literals.

    A single left-to-right scan, so a `//` or `/*` inside a string is not a
    comment and a `"` inside a comment is not a string. Interpolation holes
    keep their code (recursively stripped) so a reference inside `$"{...}"`
    still counts; string text, including text inside holes of nested strings,
    is discarded.

    A `//` comment and a preprocessor line keep their trailing newline, so the
    line structure only breaks where a block comment or a multi-line string
    body is dropped. Pass a list as `newline_drops` to record those: it
    receives one `(offset in the returned text, newlines dropped there)` tuple
    per such span, which is what `line_index` turns back into original file
    line numbers. The returned text is identical either way, so no caller that
    ignores the argument is affected.
    """
    out = []
    i = 0
    n = len(source)
    at_line_start = True
    track = newline_drops is not None

    def record_drop(start, end, appended_before):
        dropped = source.count("\n", start, end)
        if appended_before is not None:
            dropped -= "".join(out[appended_before:]).count("\n")
        if dropped > 0:
            newline_drops.append((len(out), dropped))

    while i < n:
        c = source[i]
        nxt = source[i + 1] if i + 1 < n else ""
        if at_line_start and c == "#":
            # Preprocessor directive: `#region Spawn Decision` and `#if X` carry
            # free text, not code, and a region label matching a type name is a
            # phantom reference.
            j = source.find("\n", i)
            i = n if j == -1 else j
            continue
        if c == "\n":
            at_line_start = True
        elif c not in (" ", "\t", "\r"):
            at_line_start = False
        if c == "/" and nxt == "/":
            j = source.find("\n", i)
            i = n if j == -1 else j
            continue
        if c == "/" and nxt == "*":
            j = source.find("*/", i + 2)
            end = n if j == -1 else j + 2
            if track:
                record_drop(i, end, None)
            i = end
            continue
        if _starts_string_at(source, i):
            before = len(out)
            end = _skip_string_literal(source, i, out)
            if track:
                record_drop(i, end, before)
            i = end
            continue
        if c == "'":
            i = _skip_char_literal(source, i + 1)
            continue
        out.append(c)
        i += 1
    return "".join(out)


def line_index(stripped, newline_drops):
    """Return a callable mapping a stripped-text offset to an original line number.

    `stripped` is what `strip_comments_and_strings` returned and `newline_drops`
    the list it filled: the newlines that went out with block comments and
    multi-line string bodies. Line numbers are 1-based and count the original
    file, so a reported start line opens the right line in an editor.
    """
    newlines = []
    start = stripped.find("\n")
    while start != -1:
        newlines.append(start)
        start = stripped.find("\n", start + 1)
    drop_offsets = []
    drop_prefix = []
    running = 0
    for offset, dropped in newline_drops:
        running += dropped
        drop_offsets.append(offset)
        drop_prefix.append(running)

    def line_at(offset):
        kept = bisect.bisect_left(newlines, offset)
        index = bisect.bisect_right(drop_offsets, offset)
        dropped = drop_prefix[index - 1] if index else 0
        return kept + dropped + 1

    return line_at


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
        if _is_plain_call(source, match.start(), match.end()):
            continue
        seen.add(ident)
        found.append((ident, target))
    return found


def _is_plain_call(source, start, end):
    """True when the identifier is invoked as a method: `Name(` not preceded by `new`.

    A type name is never directly followed by `(` except in a constructor call,
    which carries `new` before it. The lookback walks back over a dotted name
    first, so `new Namespace.Type(` and `new Outer.Nested(` count as
    constructors even though only the last segment is in the type table. A
    local or static method that happens to share a type's name
    (`return Decision(true, ...)`) is not a reference.
    """
    j = end
    n = len(source)
    while j < n and source[j] in (" ", "\t"):
        j += 1
    if j >= n or source[j] != "(":
        return False
    k = start
    while k > 0 and (source[k - 1].isalnum() or source[k - 1] in "_."):
        k -= 1
    q = k
    while q > 0 and source[q - 1] in " \t\r\n":
        q -= 1
    if q >= 3 and source[q - 3 : q] == "new":
        if q == 3 or not (source[q - 4].isalnum() or source[q - 4] == "_"):
            return False
    return True


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
# type-level decisions
# ---------------------------------------------------------------------------

TYPE_MODIFIERS = (
    "public",
    "internal",
    "protected",
    "private",
    "static",
    "abstract",
    "sealed",
    "partial",
    "readonly",
    "unsafe",
    "new",
)
CSHARP_NON_BASE = {
    "byte",
    "sbyte",
    "short",
    "ushort",
    "int",
    "uint",
    "long",
    "ulong",
    "float",
    "double",
    "decimal",
    "char",
    "bool",
    "string",
    "object",
}
TYPE_HEADER_RE = re.compile(
    r"(?:(?:%s)\s+)*(?P<kind>class|struct|interface|enum)\s+(?P<name>[A-Z][A-Za-z0-9_]*)"
    % "|".join(TYPE_MODIFIERS)
)
METHOD_DECL_RE = re.compile(r"\w+\s+\w+\s*\([^)]*\)\s*(?:\{|=>)")
ENTRY_BASES = {"MonoBehaviour", "ScenarioModule", "PartModule", "VesselModule"}
ROLE_ORDER = ("entry", "interface", "abstract", "enum", "static", "implements", "data", "service")


def _matches_word(source, i, word):
    n = len(source)
    if not source.startswith(word, i):
        return False
    before_ok = i == 0 or not (source[i - 1].isalnum() or source[i - 1] == "_")
    after = i + len(word)
    after_ok = after >= n or not (source[after].isalnum() or source[after] == "_")
    return before_ok and after_ok


def _skip_generic_args(source, i):
    """Skip a <...> run starting at i; return the index after the closing >."""
    depth = 0
    n = len(source)
    while i < n:
        if source[i] == "<":
            depth += 1
        elif source[i] == ">":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return n


def _strip_generics(text):
    out = []
    depth = 0
    for char in text:
        if char == "<":
            depth += 1
        elif char == ">":
            depth = max(0, depth - 1)
        elif depth == 0:
            out.append(char)
    return "".join(out)


def _base_names(text):
    """Split a base list into bare type names ("List<Foo>" -> "List")."""
    parts = []
    depth = 0
    current = []
    for char in text:
        if char in "<([":
            depth += 1
        elif char in ">)]":
            depth = max(0, depth - 1)
        if char == "," and depth == 0:
            parts.append("".join(current))
            current = []
        else:
            current.append(char)
    parts.append("".join(current))
    names = []
    for part in parts:
        stripped = _strip_generics(part).split("::")[-1].split(".")[-1].strip()
        if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", stripped) and stripped not in CSHARP_NON_BASE:
            names.append(stripped)
    return names


def type_declarations(source):
    """Return one dict per type declaration in already-stripped source.

    Keys: name, kind, modifiers (set), bases (base names with generic arguments
    and namespace qualifiers stripped), span (open-brace offset, offset after
    the matching close brace), start (offset of the first modifier), body (the
    text inside the braces), before (the 300 characters before the header, so
    attribute-based roles can see it), enclosing (name of the enclosing type or
    None) and parent (index of the enclosing declaration or None, used to
    attribute nested-type references). Names shorter than four characters are
    skipped, same as declared_types.
    """
    declarations = []
    stack = []
    n = len(source)
    for match in TYPE_HEADER_RE.finditer(source):
        name = match.group("name")
        kind = match.group("kind")
        if len(name) < MIN_TYPE_NAME_LEN:
            continue
        start = match.start()
        # `record class Foo` / `record struct Foo` match the class/struct
        # keyword but are records, which are out of scope; a positional record
        # with no body would otherwise swallow the next declaration's braces.
        if re.search(r"\brecord\s+$", source[max(0, start - 16) : start]):
            continue
        i = match.end()
        while i < n and source[i] in " \t\r\n":
            i += 1
        if i < n and source[i] == "<":
            i = _skip_generic_args(source, i)
            while i < n and source[i] in " \t\r\n":
                i += 1
        bases = []
        j = i
        if kind != "enum" and j < n and source[j] == ":":
            j += 1
            depth = 0
            while j < n:
                char = source[j]
                if depth == 0 and (char == "{" or char == ";" or _matches_word(source, j, "where")):
                    break
                if char == "<":
                    depth += 1
                elif char == ">":
                    depth = max(0, depth - 1)
                j += 1
            bases = _base_names(source[i + 1 : j])
        open_brace = source.find("{", j)
        if open_brace == -1:
            continue
        depth = 0
        k = open_brace
        while k < n:
            if source[k] == "{":
                depth += 1
            elif source[k] == "}":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        end = k + 1 if k < n else n
        modifiers = set(re.findall(r"[a-z]+", match.group(0)[: match.start("kind") - start]))
        modifiers &= set(TYPE_MODIFIERS)
        declaration = {
            "name": name,
            "kind": kind,
            "modifiers": modifiers,
            "bases": bases,
            "span": (open_brace, end),
            "start": start,
            "body": source[open_brace + 1 : max(open_brace + 1, end - 1)],
            "before": source[max(0, start - 300) : start],
        }
        while stack and declarations[stack[-1]]["span"][1] <= start:
            stack.pop()
        declaration["parent"] = stack[-1] if stack else None
        declaration["enclosing"] = declarations[stack[-1]]["name"] if stack else None
        stack.append(len(declarations))
        declarations.append(declaration)
    return declarations


def type_references(source, declarations, type_table):
    """Return {declared type name: set of other in-table names its body references}.

    A reference inside a nested type's span belongs to the nested type only:
    the outer type's scan skips the nested declaration's whole range, header
    included. Duplicate names in one call are merged.
    """
    result = defaultdict(set)
    by_parent = declarations_by_parent(declarations)
    for index, declaration in enumerate(declarations):
        found = result[declaration["name"]]
        for seg_start, seg_end in own_body_segments(declarations, index, by_parent):
            text = source[seg_start:seg_end]
            for match in IDENT_RE.finditer(text):
                ident = match.group(0)
                if ident == declaration["name"] or ident not in type_table:
                    continue
                if _is_plain_call(text, match.start(), match.end()):
                    continue
                found.add(ident)
    return result


def type_role(declaration):
    """Return exactly one role string for a declaration, in checked order."""
    if any(base in ENTRY_BASES for base in declaration["bases"]):
        return "entry"
    if "[KSPAddon" in declaration["before"] or "[HarmonyPatch" in declaration["before"]:
        return "entry"
    if declaration["kind"] == "interface":
        return "interface"
    if "abstract" in declaration["modifiers"]:
        return "abstract"
    if declaration["kind"] == "enum":
        return "enum"
    if "static" in declaration["modifiers"]:
        return "static"
    if any(re.match(r"^I[A-Z]", base) for base in declaration["bases"]):
        return "implements"
    if declaration["kind"] == "struct" or not METHOD_DECL_RE.search(declaration["body"]):
        return "data"
    return "service"


def type_levels(type_graph):
    """Return {type name: level}; a level is the longest path to a sink.

    `type_graph` maps a type name to an iterable of the type names it
    references. Cycles are condensed with `sccs`, so every member of a cycle
    shares one level, and a level-0 type references no in-repo type.
    """
    nodes = sorted(type_graph)
    edges = []
    for name in nodes:
        for target in type_graph[name]:
            if target in type_graph and target != name:
                edges.append((name, target))
    components = sccs(nodes, edges)
    component_of = {}
    for index, component in enumerate(components):
        for member in component:
            component_of[member] = index
    successors = {index: set() for index in range(len(components))}
    for frm, to in edges:
        a, b = component_of[frm], component_of[to]
        if a != b:
            successors[a].add(b)

    order = []
    seen = set()
    for root in sorted(successors):
        if root in seen:
            continue
        seen.add(root)
        stack = [(root, iter(sorted(successors[root])))]
        while stack:
            node, iterator = stack[-1]
            advanced = False
            for nxt in iterator:
                if nxt not in seen:
                    seen.add(nxt)
                    stack.append((nxt, iter(sorted(successors[nxt]))))
                    advanced = True
                    break
            if not advanced:
                order.append(node)
                stack.pop()

    level = {}
    for node in order:
        child_levels = [level[child] for child in successors[node]]
        level[node] = 0 if not child_levels else 1 + max(child_levels)
    return {name: level[component_of[name]] for name in nodes}


def file_type_references(source, type_table):
    """Set of type-table names referenced anywhere in an already-stripped file."""
    found = set()
    for match in IDENT_RE.finditer(source):
        ident = match.group(0)
        if ident in type_table and not _is_plain_call(source, match.start(), match.end()):
            found.add(ident)
    return found


def type_fanin(name, file_references, declaring_files):
    """Number of distinct files outside the declaring file that reference the type.

    Deliberately file-based: a big file counts once, so the number reads as
    "how many files must be understood before this type" rather than as a raw
    reference count.
    """
    return sum(
        1
        for rel_path, names in file_references.items()
        if name in names and rel_path not in declaring_files
    )


def knots(type_graph, type_modules):
    """Return every strongly connected component of size 2 or more, largest first.

    Each entry is {members, size, modules}: `members` are the sorted type
    names in the component and `modules` counts members per module. A knot is
    where longest-path levelling collapses, because every member shares one
    level.
    """
    nodes = sorted(type_graph)
    edges = [
        (name, target)
        for name in nodes
        for target in type_graph[name]
        if target in type_graph and target != name
    ]
    components = [sorted(component) for component in sccs(nodes, edges) if len(component) >= 2]
    components.sort(key=lambda members: (-len(members), members[0]))
    result = []
    for members in components:
        counts = defaultdict(int)
        for name in members:
            counts[type_modules.get(name, "?")] += 1
        result.append(
            {
                "members": members,
                "size": len(members),
                "modules": {module: counts[module] for module in sorted(counts)},
            }
        )
    return result


def knot_hubs(component, type_graph):
    """Return one knot's members as hubs: in-knot fan-in, then in-knot references.

    Fan-in counts only OTHER members of the component, so a popular type that
    the rest of the codebase uses but the knot itself does not is not a hub.
    Rows are sorted by fan-in descending, name ascending.
    """
    member_set = set(component)
    fan_in = {name: 0 for name in component}
    references = {name: [] for name in component}
    for name in component:
        for target in type_graph.get(name, ()):
            if target in member_set and target != name:
                references[name].append(target)
                fan_in[target] += 1
    hubs = [
        {"name": name, "fanIn": fan_in[name], "references": sorted(references[name])}
        for name in component
    ]
    hubs.sort(key=lambda hub: (-hub["fanIn"], hub["name"]))
    return hubs


def greedy_sink_cuts(component, type_graph, max_steps=12, candidates=12):
    """Cut one knot apart by removing the outgoing edges of chosen sink types.

    At each step, among the `candidates` members of the current largest
    component with the highest in-knot fan-in, the sink whose outgoing in-knot
    edges leave the smallest largest component wins (ties by name). Those
    edges are removed for good and the step is recorded. The sequence stops
    after the cut that leaves the largest remaining component below 30 (so a
    knot that starts small still gets its first cut), or after `max_steps`
    steps.
    """
    members = sorted(component)
    member_set = set(members)
    edges = {
        (name, target)
        for name in members
        for target in type_graph.get(name, ())
        if target in member_set and target != name
    }
    cuts = []
    while len(cuts) < max_steps:
        components = [part for part in sccs(members, edges) if len(part) >= 2]
        components.sort(key=lambda part: (-len(part), sorted(part)[0]))
        if not components:
            break
        largest = components[0]
        size_before = len(largest)
        largest_set = set(largest)
        fan_in = {name: 0 for name in largest}
        for frm, to in edges:
            if frm in largest_set and to in largest_set:
                fan_in[to] += 1
        ranked = sorted(largest, key=lambda name: (-fan_in[name], name))[:candidates]
        if not ranked:
            break

        best_name = None
        best_key = None
        best_outgoing = None
        for name in ranked:
            outgoing = {(frm, to) for (frm, to) in edges if frm == name and to in largest_set}
            trial = edges - outgoing
            after = max((len(part) for part in sccs(members, trial)), default=0)
            key = (after, name)
            if best_key is None or key < best_key:
                best_name, best_key, best_outgoing = name, key, outgoing
        size_after = best_key[0]
        dropped = sorted(target for (_frm, target) in best_outgoing)
        edges -= best_outgoing
        cuts.append(
            {
                "step": len(cuts) + 1,
                "sink": best_name,
                "sizeBefore": size_before,
                "sizeAfter": size_after,
                "droppedReferences": dropped,
            }
        )
        # The size stop applies once a cut has happened: a 30-member remainder
        # is small enough that further nibbling reorders nothing useful, while
        # a knot that starts small still gets its one cut so the sublevels
        # inside it have an order at all.
        if size_after < 30:
            break
    return cuts


def sublevels(component, type_graph, cuts):
    """Return {member: sublevel} after the cut sequence's edges are removed.

    The cut is a display device: it only removes the outgoing edges of the
    sinks named by `cuts`, then runs the ordinary longest-path levelling over
    what is left of the component. Members still inside a residual cycle share
    a sublevel, exactly as they shared a level before.
    """
    member_set = set(component)
    removed = {
        (cut["sink"], target) for cut in cuts for target in cut["droppedReferences"]
    }
    subgraph = {
        name: [
            target
            for target in type_graph.get(name, ())
            if target in member_set and target != name and (name, target) not in removed
        ]
        for name in component
    }
    return type_levels(subgraph)


# ---------------------------------------------------------------------------
# size view: measurement
# ---------------------------------------------------------------------------

# Thresholds live here, as named constants, and nowhere else. The runtime
# coupling list is the one size input that names modules, so it lives in
# modules.toml ([size] runtimeCoupled) where every other module name is.
LARGE_FILE_LINES = 1000
GIANT_TYPE_LINES = 5000
LONG_METHOD_LINES = 90
SIZE_TOP_N = 25
SIZE_TOP_FILES = 15
PURE_POOL_METHODS = 8
PURE_POOL_LINES = 400
MUTABLE_STATIC_FLOOR = 10
NESTED_TYPE_FLOOR = 5
SIBLING_TYPE_FLOOR = 3
HOTSPOT_PRIORITY_RANK = 10
TOP_METHODS_PER_TYPE = 5
SIZE_CHECK_WIDTH = 96
DEFAULT_RUNTIME_COUPLED = (
    "Controllers",
    "Display",
    "Ghost",
    "MapRender",
    "Patches",
    "Rendering",
    "UI",
)

MEMBER_MODIFIERS = (
    "public",
    "private",
    "protected",
    "internal",
    "static",
    "virtual",
    "override",
    "sealed",
    "abstract",
    "extern",
    "unsafe",
    "async",
    "new",
    "partial",
    "readonly",
    "const",
    "volatile",
    "required",
)
FIELD_MODIFIERS = (
    "public",
    "private",
    "protected",
    "internal",
    "static",
    "readonly",
    "const",
    "volatile",
    "new",
    "unsafe",
    "required",
)
# One nesting level of generic arguments is enough for the shapes that occur
# (`Dictionary<string, List<int>>`); anything deeper fails to match and the
# member is skipped rather than guessed at.
_GENERIC_ARGS = r"<[^<>;{}()]*(?:<[^<>;{}()]*>[^<>;{}()]*)*>"
_TYPE_TOKEN = r"[A-Za-z_][A-Za-z0-9_.]*(?:\s*%s)?(?:\s*\?)?(?:\s*\[\s*[,\s]*\])*" % _GENERIC_ARGS
# A field's type may carry a tuple inside its generic arguments
# (`Dictionary<uint, (double startUT, double endUT)>`), which the method-header
# token deliberately does not allow: there a `(` is the parameter list.
_GENERIC_ARGS_WITH_TUPLES = r"<[^<>;{}]*(?:<[^<>;{}]*>[^<>;{}]*)*>"
_FIELD_TYPE_TOKEN = (
    r"[A-Za-z_][A-Za-z0-9_.]*(?:\s*%s)?(?:\s*\?)?(?:\s*\[\s*[,\s]*\])*" % _GENERIC_ARGS_WITH_TUPLES
)
METHOD_HEADER_RE = re.compile(
    r"(?<![A-Za-z0-9_.])(?P<mods>(?:\b(?:%s)\b[ \t\r\n]+)*)"
    r"(?P<ret>%s)[ \t\r\n]+(?P<name>[A-Za-z_][A-Za-z0-9_]*)[ \t\r\n]*(?:%s[ \t\r\n]*)?\("
    % ("|".join(MEMBER_MODIFIERS), _TYPE_TOKEN, _GENERIC_ARGS)
)
FIELD_DECL_RE = re.compile(
    r"(?<![A-Za-z0-9_.])(?P<mods>(?:\b(?:%s)\b[ \t\r\n]+)+)"
    r"(?P<type>%s)[ \t\r\n]+(?P<name>[A-Za-z_][A-Za-z0-9_]*)[ \t\r\n]*(?P<term>[;=,])"
    % ("|".join(FIELD_MODIFIERS), _FIELD_TYPE_TOKEN)
)
# A statement reads like a declaration to the header pattern (`return Foo(x)`),
# so a match whose return token or name is one of these is dropped outright
# rather than counted as a member the scan could not delimit.
STATEMENT_KEYWORDS = frozenset(
    """if else for foreach while do switch case return yield throw new using lock fixed
    catch try finally await in is as when goto checked unchecked stackalloc sizeof typeof
    nameof default base this var where select from let orderby out ref params delegate
    operator""".split()
)
# A `static readonly Dictionary` cannot be reassigned but its CONTENTS change,
# so it is shared mutable state and belongs in the static state map beside the
# reassignable statics. Matched on the declared type text: any array, or a name
# carrying one of these markers. `ReadOnly`, `Immutable` and `Frozen` in the
# name veto the match, because `IReadOnlyList<T>` is not a mutable handle. A
# readonly field holding a custom class with mutable fields is still missed:
# text cannot see that.
MUTABLE_COLLECTION_TYPE_MARKERS = (
    "Dictionary",
    "List",
    "HashSet",
    "SortedSet",
    "Queue",
    "Stack",
    "Bag",
    "Collection",
    "Lookup",
    "StringBuilder",
    "Array",
)
IMMUTABLE_TYPE_MARKERS = ("ReadOnly", "Immutable", "Frozen")
# The identifiers that make a method "live": if a static method's body mentions
# none of them, and none of its type's shared statics, it is a candidate for
# an `internal static` helper with unit tests. Deliberately short - this is a
# candidate pool to read, not a purity proof.
LIVE_KSP_IDENTIFIERS = (
    "Vessel",
    "ProtoVessel",
    "FlightGlobals",
    "MapView",
    "PlanetariumCamera",
    "OrbitDriver",
    "GameEvents",
    "Time",
    "Planetarium",
    "HighLogic",
    "GameObject",
    "Transform",
    "Debug",
)
LIVE_KSP_RE = re.compile(r"\b(?:%s)\b" % "|".join(LIVE_KSP_IDENTIFIERS))
WORD_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
# What may sit between a parameter list and a body: whitespace and generic
# constraint syntax (`where T : class, new()`). Anything else means the scan
# lost the member.
_CONSTRAINT_CHARS = frozenset(
    " \t\r\n_:,.<>[]()?abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
)


def declarations_by_parent(declarations):
    """Return {parent index or None: [(index, declaration)]} for a file's declarations."""
    by_parent = defaultdict(list)
    for index, declaration in enumerate(declarations):
        by_parent[declaration["parent"]].append((index, declaration))
    return by_parent


def own_body_segments(declarations, index, by_parent=None):
    """Return the (start, end) runs of a declaration's body that are its own.

    A nested type's whole range, header included, belongs to the nested type,
    so it is cut out of the enclosing declaration's runs. Empty runs are
    dropped.
    """
    declaration = declarations[index]
    if by_parent is None:
        by_parent = declarations_by_parent(declarations)
    segments = []
    pos = declaration["span"][0] + 1
    for _child_index, child in sorted(by_parent.get(index, []), key=lambda pair: pair[1]["start"]):
        segments.append((pos, child["start"]))
        pos = child["span"][1]
    segments.append((pos, declaration["span"][1] - 1))
    return [(start, end) for start, end in segments if end > start]


def is_mutable_collection_type(type_text):
    """True when a declared type names a collection whose contents can change (pure).

    Any array counts, as does any name carrying a marker from
    MUTABLE_COLLECTION_TYPE_MARKERS, unless the name also carries a
    ReadOnly / Immutable / Frozen marker.
    """
    compact = "".join(type_text.split())
    if any(marker in compact for marker in IMMUTABLE_TYPE_MARKERS):
        return False
    if "[" in compact:
        return True
    return any(marker in compact for marker in MUTABLE_COLLECTION_TYPE_MARKERS)


def _match_parens(text, index, limit):
    """Return the offset of the `)` closing the `(` at index, or None."""
    depth = 0
    i = index
    while i < limit:
        char = text[i]
        if char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return None


def _match_braces(text, index, limit):
    """Return the offset of the `}` closing the `{` at index, or None."""
    depth = 0
    i = index
    while i < limit:
        char = text[i]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return None


def _expression_body_end(text, index, limit):
    """Return the offset of the `;` ending an expression body, or None.

    Depth-aware, so a `;` inside a statement lambda (`x => { a(); }`) or inside
    a `for` header written in a lambda does not end the member.
    """
    depth = 0
    i = index
    while i < limit:
        char = text[i]
        if char in "([{":
            depth += 1
        elif char in ")]}":
            if depth == 0:
                return None
            depth -= 1
        elif char == ";" and depth == 0:
            return i
        i += 1
    return None


def _member_body(text, index, limit):
    """Return (kind, marker, end) for what follows a member's parameter list.

    Kinds: "block" (a braced body; marker is the `{`, end the `}`),
    "expression" (an `=>` body; marker is the `=`, end the `;`), "none" (a
    declaration with no body: interface, abstract, extern, partial) and "skip"
    (the scan cannot delimit it; end is None).
    """
    i = index
    while i < limit:
        char = text[i]
        if char == "{":
            end = _match_braces(text, i, limit)
            return ("block", i, end) if end is not None else ("skip", i, None)
        if char == ";":
            return ("none", i, i)
        if char == "=" and i + 1 < limit and text[i + 1] == ">":
            end = _expression_body_end(text, i + 2, limit)
            return ("expression", i, end) if end is not None else ("skip", i, None)
        if char not in _CONSTRAINT_CHARS:
            return ("skip", i, None)
        i += 1
    return ("skip", limit, None)


def scan_declaration_members(text, declarations, index, line_at=None, by_parent=None):
    """Return one declaration part's member facts (pure).

    `text` is stripped source, `declarations` what `type_declarations` returned
    for it, and `line_at` maps a stripped offset to an original file line
    (default: count the newlines in `text`, which is exact when no block
    comment or multi-line string was dropped).

    Keys: methods (name, mods, ret, static, coroutine, startLine, lines, body
    span), fields (name, mods, static, mutableStatic), skipped (members the
    scan could not delimit), nested (types declared inside this one),
    startLine, endLine and lines for the declaration itself.
    """
    if line_at is None:
        line_at = line_index(text, [])
    if by_parent is None:
        by_parent = declarations_by_parent(declarations)
    declaration = declarations[index]
    methods = []
    fields = []
    skipped = 0
    for seg_start, seg_end in own_body_segments(declarations, index, by_parent):
        gaps = []
        gap_start = seg_start
        pos = seg_start
        while True:
            match = METHOD_HEADER_RE.search(text, pos, seg_end)
            if match is None:
                break
            ret = match.group("ret").strip()
            name = match.group("name")
            if ret.split(".")[-1] in STATEMENT_KEYWORDS or name in STATEMENT_KEYWORDS:
                pos = match.end()
                continue
            close = _match_parens(text, match.end() - 1, seg_end)
            if close is None:
                skipped += 1
                pos = match.end()
                continue
            kind, marker, end = _member_body(text, close + 1, seg_end)
            if kind == "skip":
                skipped += 1
                pos = close + 1
                continue
            modifiers = set(re.findall(r"[a-z]+", match.group("mods"))) & set(MEMBER_MODIFIERS)
            start_line = line_at(match.start())
            methods.append(
                {
                    "name": name,
                    "ret": ret,
                    "mods": sorted(modifiers),
                    "static": "static" in modifiers,
                    "coroutine": ret == "IEnumerator" or ret.startswith("IEnumerator<"),
                    "startLine": start_line,
                    "lines": 0 if kind == "none" else line_at(end) - start_line + 1,
                    "bodyStart": None if kind == "none" else marker,
                    "bodyEnd": None if kind == "none" else end,
                }
            )
            gaps.append((gap_start, match.start()))
            pos = (marker + 1) if kind == "none" else (end + 1)
            gap_start = pos
        gaps.append((gap_start, seg_end))
        for gap_start, gap_end in gaps:
            if gap_end <= gap_start:
                continue
            for field in FIELD_DECL_RE.finditer(text, gap_start, gap_end):
                modifiers = set(re.findall(r"[a-z]+", field.group("mods"))) & set(FIELD_MODIFIERS)
                is_static = "static" in modifiers
                is_const = "const" in modifiers
                is_readonly = "readonly" in modifiers
                fields.append(
                    {
                        "name": field.group("name"),
                        "type": field.group("type").strip(),
                        "mods": sorted(modifiers),
                        "static": is_static,
                        # Reassignable static state.
                        "mutableStatic": is_static and not is_const and not is_readonly,
                        # Fixed handle, mutable contents: the other half of the
                        # static state map.
                        "readonlyCollectionStatic": is_static
                        and is_readonly
                        and not is_const
                        and is_mutable_collection_type(field.group("type")),
                    }
                )
    span_start, span_end = declaration["span"]
    nested = sum(
        1
        for other in declarations
        if other is not declaration and span_start < other["start"] < span_end
    )
    start_line = line_at(declaration["start"])
    return {
        "methods": methods,
        "fields": fields,
        "skipped": skipped,
        "nested": nested,
        "startLine": start_line,
        "endLine": line_at(span_end - 1),
        "lines": line_at(span_end - 1) - start_line + 1,
    }


def method_is_pure_candidate(body, shared_statics):
    """True when a static method's body names no live KSP type and no shared static.

    `body` is the stripped body text and `shared_statics` the set of the type's
    static state field names: both the reassignable ones and the readonly
    collections, because a method reading a shared dictionary is not a pure
    helper either. A text test, so it can be fooled by a helper that reaches
    live state one call away; the pool is a reading list, not a proof.
    """
    if LIVE_KSP_RE.search(body):
        return False
    if not shared_statics:
        return True
    return not (set(WORD_RE.findall(body)) & set(shared_statics))


def merge_type_size(parts, body_text=None):
    """Merge one type's declaration parts into its size facts (pure).

    `parts` is a list of {"file", "facts"} where facts is what
    `scan_declaration_members` returned, and `body_text(file, start, end)`
    returns the stripped body text for the purity estimate (omit it to skip the
    estimate). Partials merge here: lines, methods and fields are summed across
    every part, so a type spread over ten files reads as one type.
    """
    files = []
    methods = []
    fields = []
    skipped = 0
    nested = 0
    for part in parts:
        facts = part["facts"]
        files.append({"file": part["file"], "lines": facts["lines"]})
        for method in facts["methods"]:
            entry = dict(method)
            entry["file"] = part["file"]
            methods.append(entry)
        fields.extend(facts["fields"])
        skipped += facts["skipped"]
        nested += facts["nested"]
    files.sort(key=lambda row: (-row["lines"], row["file"]))
    mutable_statics = sorted({field["name"] for field in fields if field["mutableStatic"]})
    readonly_collections = sorted(
        {field["name"] for field in fields if field["readonlyCollectionStatic"]}
    )
    shared_statics = sorted(set(mutable_statics) | set(readonly_collections))
    long_methods = [entry for entry in methods if entry["lines"] >= LONG_METHOD_LINES]
    ranked = sorted(methods, key=lambda entry: (-entry["lines"], entry["name"], entry["file"]))
    pure_methods = 0
    pure_lines = 0
    if body_text is not None:
        for entry in methods:
            if not entry["static"] or entry["bodyStart"] is None or entry["coroutine"]:
                continue
            body = body_text(entry["file"], entry["bodyStart"], entry["bodyEnd"])
            if method_is_pure_candidate(body, shared_statics):
                pure_methods += 1
                pure_lines += entry["lines"]
    return {
        "lines": sum(row["lines"] for row in files),
        "files": files,
        "methods": len(methods),
        "longMethods": len(long_methods),
        "topMethods": [
            {
                "name": entry["name"],
                "file": entry["file"],
                "startLine": entry["startLine"],
                "lines": entry["lines"],
            }
            for entry in ranked[:TOP_METHODS_PER_TYPE]
        ],
        "coroutines": sum(1 for entry in methods if entry["coroutine"]),
        "fields": len(fields),
        "mutableStatics": len(mutable_statics),
        "mutableStaticNames": mutable_statics,
        "readonlyCollectionStatics": len(readonly_collections),
        "readonlyCollectionStaticNames": readonly_collections,
        "nestedTypes": nested,
        "pureStaticMethods": pure_methods,
        "pureStaticLines": pure_lines,
        "skippedMembers": skipped,
    }


# ---------------------------------------------------------------------------
# catch-all placement
# ---------------------------------------------------------------------------

PLACEMENT_FAMILIES = (
    ("GuiTree", "GuiTree"),
    ("GameState", "GameActions"),
    ("ParsekConfig", "Config"),
    ("ParsekSettings", "Config"),
    ("SettingWhitelist", "Config"),
    ("ParsekUI", "UI"),
    ("MapRender", "MapRender"),
    ("RouteProof", "Logistics"),
    ("RouteConnection", "Logistics"),
    ("RouteEndpoint", "Logistics"),
    ("RouteHarvest", "Logistics"),
    ("RouteRun", "Logistics"),
    ("RouteOrigin", "Logistics"),
    ("Route", "Logistics"),
    ("PartEvent", "Recording"),
    ("FlagEvent", "Recording"),
    ("SegmentEvent", "Recording"),
    ("SegmentBoundary", "Recording"),
    ("SurfacePosition", "Recording"),
    ("AnchorDetector", "Recording"),
    ("RelativeAnchor", "Recording"),
    ("TrackSection", "Recording"),
    ("ControllerInfo", "Recording"),
    ("Mission", "Missions"),
)


def default_placement_rules():
    """The five placement rules, as data, in checked order.

    R1 runs first and its moves are rebuilt into the map before R2-R5 are
    applied, so the evidence their thresholds see is the post-R1 state (root
    utilities gain external references from the files R1 just moved).
    """
    return [
        {"id": "R1", "families": dict(PLACEMENT_FAMILIES)},
        {"id": "R2", "minExternal": 5, "minShare": 0.5, "dominance": 2.0},
        {"id": "R3", "minExternal": 5, "pairShare": 0.8, "soloShare": 0.6},
        {"id": "R4"},
    ]


def historical_catch_all_rules(rules):
    """Return rules whose last entry matches every root file.

    The kernel guard made Core an explicit list, so a reconstruction of a
    pre-phase map has no catch-all left; the placement report still rebuilds
    the historical last-rule bucket this way.
    """
    if not rules:
        return list(rules)
    return list(rules[:-1]) + [{"name": rules[-1]["name"], "prefix": ".*"}]


def place(evidence, rules):
    """Return (destination, rule_id, tag) for one file's placement evidence.

    Rules are checked in order and the first that fires wins. R1 matches the
    file name against a family prefix; R2 moves a file whose references have a
    clear owner (a majority share, or a top module at least `dominance` times
    the second); R3 keeps a two-owner file in the catch-all as a
    SPLIT-CANDIDATE; R4 tags a file nothing references outside as an ORPHAN;
    nothing matching is UNDECIDED.
    """
    name = evidence["file"]
    external = evidence["externalRefs"]
    by_module = evidence["byModule"]
    for rule in rules:
        rule_id = rule["id"]
        if rule_id == "R1":
            for prefix, module in rule["families"].items():
                if name.startswith(prefix):
                    return module, "R1", ""
        elif rule_id == "R2":
            if external >= rule["minExternal"] and by_module:
                top_count = by_module[0][1]
                second_count = by_module[1][1] if len(by_module) > 1 else 0
                if top_count / external >= rule["minShare"] or (
                    second_count and top_count >= rule["dominance"] * second_count
                ):
                    return by_module[0][0], "R2", ""
        elif rule_id == "R3":
            if external >= rule["minExternal"] and len(by_module) >= 2:
                top_share = by_module[0][1] / external
                pair_share = (by_module[0][1] + by_module[1][1]) / external
                if top_share < rule["soloShare"] and pair_share >= rule["pairShare"]:
                    return None, "R3", "SPLIT-CANDIDATE: %s, %s" % (
                        by_module[0][0],
                        by_module[1][0],
                    )
        elif rule_id == "R4":
            if external == 0:
                return None, "R4", "ORPHAN"
    return None, "R5", "UNDECIDED"


def placement_evidence(model):
    """Return per-file placement evidence for the module the last rule names.

    Historically that module was the catch-all; after the kernel guard it is
    the explicit Core kernel list, and the report rebuilds the old catch-all
    for its before-state. One entry per file in that module, sorted by external
    references descending (ties by file): declared type count (a name declared
    in more than one module still counts here even though the type graph drops
    it), how many of them are in knot 1, the number of (referencing type,
    referenced type) pairs arriving from types in other modules, that count
    split by referencing module, the top module's share of it, and the highest
    file-based fan-in among the file's types.
    """
    catch_all = model.get("catchAll")
    entries = model.get("types", [])
    declared = model.get("declaredTypeCounts", {})
    module_of = {entry["name"]: entry["module"] for entry in entries}
    per_file = {}
    for rel_path, module in model.get("fileModules", {}).items():
        if module == catch_all:
            per_file[rel_path] = {
                "file": rel_path,
                "types": declared.get(rel_path, 0),
                "knotTypes": 0,
                "byModule": {},
                "hub": "",
                "hubFanIn": 0,
            }
    for entry in entries:
        bucket = per_file.get(entry["file"])
        if bucket is None:
            continue
        if entry.get("knot") == 1:
            bucket["knotTypes"] += 1
        if entry.get("fanIn", 0) > bucket["hubFanIn"]:
            bucket["hubFanIn"] = entry["fanIn"]
            bucket["hub"] = entry["name"]
        for other in entry.get("referencedBy", []):
            other_module = module_of.get(other)
            if other_module is not None and other_module != catch_all:
                bucket["byModule"][other_module] = bucket["byModule"].get(other_module, 0) + 1

    evidence = []
    for bucket in per_file.values():
        by_module = sorted(bucket["byModule"].items(), key=lambda item: (-item[1], item[0]))
        external = sum(count for _module, count in by_module)
        evidence.append(
            {
                "file": bucket["file"],
                "types": bucket["types"],
                "knotTypes": bucket["knotTypes"],
                "externalRefs": external,
                "byModule": by_module,
                "share": (by_module[0][1] / external) if external else 0.0,
                "hub": bucket["hub"],
                "hubFanIn": bucket["hubFanIn"],
            }
        )
    evidence.sort(key=lambda entry: (-entry["externalRefs"], entry["file"]))
    return evidence


def print_placement_evidence(model):
    """Print one evidence line per file in the module the last rule names."""
    catch_all = model.get("catchAll")
    evidence = placement_evidence(model)
    print("PLACEMENT EVIDENCE (files in the module the last rule names, %s):" % catch_all)
    print(
        "  %-34s %5s %5s %7s %6s  %s"
        % ("file", "types", "knot", "extRefs", "share", "top=Module(count), second=Module(count)  hub=Name(fanIn)")
    )
    if not evidence:
        print("  none.")
    for entry in evidence:
        by_module = entry["byModule"]
        top = "%s(%d)" % by_module[0] if by_module else "-"
        second = "%s(%d)" % by_module[1] if len(by_module) > 1 else "-"
        hub = "%s(%d)" % (entry["hub"], entry["hubFanIn"]) if entry["hub"] else "-"
        print(
            "  %-34s %5d %5d %7d %6.2f  top=%s, second=%s  hub=%s"
            % (
                entry["file"],
                entry["types"],
                entry["knotTypes"],
                entry["externalRefs"],
                entry["share"],
                top,
                second,
                hub,
            )
        )


def _placement_evidence_line(entry):
    by_module = entry["byModule"]
    top = "%s(%d)" % by_module[0] if by_module else "-"
    second = "%s(%d)" % by_module[1] if len(by_module) > 1 else "-"
    hub = "%s(%d)" % (entry["hub"], entry["hubFanIn"]) if entry["hub"] else "-"
    return (
        "types=%d, knot=%d, extRefs=%d, share=%.2f, top=%s, second=%s, hub=%s"
        % (
            entry["types"],
            entry["knotTypes"],
            entry["externalRefs"],
            entry["share"],
            top,
            second,
            hub,
        )
    )


def render_placement_report(before_model, after_r1_model, after_model, rules):
    """Return the core-placement report as Markdown (evidence only).

    `before_model` is the map before the phase, `after_r1_model` the map with
    only the R1 family rules applied, and `after_model` the final map. R1 rows
    carry the original evidence; R2-R5 rows carry the evidence recomputed
    after the R1 moves, because that is the state their thresholds saw.
    """
    catch_all = before_model.get("catchAll") or "?"
    original = placement_evidence(before_model)
    post_r1 = {entry["file"]: entry for entry in placement_evidence(after_r1_model)}
    families = {}
    for rule in rules:
        if rule["id"] == "R1":
            families.update(rule["families"])

    placements = []
    for entry in original:
        destination = None
        for prefix, module in families.items():
            if entry["file"].startswith(prefix):
                destination = module
                break
        if destination is not None:
            placements.append((entry, destination, "R1", ""))
        else:
            post = post_r1.get(entry["file"], entry)
            placed, rule_id, tag = place(post, rules)
            placements.append((post, placed, rule_id, tag))

    moved = defaultdict(int)
    split = []
    orphan = []
    undecided = []
    rows = []
    for entry, destination, rule_id, tag in placements:
        if destination:
            moved[destination] += 1
        elif rule_id == "R3":
            split.append(entry)
        elif rule_id == "R4":
            orphan.append(entry)
        else:
            undecided.append(entry)
        by_module = entry["byModule"]
        top = "%s(%d)" % by_module[0] if by_module else "-"
        second = "%s(%d)" % by_module[1] if len(by_module) > 1 else "-"
        rows.append(
            "| %s | %d | %d | %d | %.2f | %s | %s | %s | %s |"
            % (
                entry["file"],
                entry["types"],
                entry["knotTypes"],
                entry["externalRefs"],
                entry["share"],
                top,
                second,
                rule_id,
                destination if destination else tag,
            )
        )

    def module_entry(model, name):
        for module in model["modules"]:
            if module["name"] == name:
                return module
        return None

    def upward_count(model):
        return sum(1 for edge in model["edges"] if edge["upward"] and not_tooling(model, edge))

    destinations = sorted(moved)
    table_modules = [catch_all] + destinations
    metric_rows = []
    for name in table_modules:
        before = module_entry(before_model, name) or {}
        after = module_entry(after_model, name) or {}
        metric_rows.append(
            "| %s | %d | %d | %d | %d | %d | %d | %.2f | %.2f |"
            % (
                name,
                before.get("files", 0),
                after.get("files", 0),
                before.get("fanOut", 0),
                after.get("fanOut", 0),
                before.get("fanIn", 0),
                after.get("fanIn", 0),
                before.get("instability", 0.0),
                after.get("instability", 0.0),
            )
        )

    mismatches = [
        entry["file"]
        for entry, destination, _rule_id, _tag in placements
        if destination
        and after_model.get("fileModules", {}).get(entry["file"]) != destination
    ]

    lines = [
        "# Core placement evidence",
        "",
        "Generated by `scripts/arch/archview.py --place`; do not edit by hand.",
        "The table is the state before this phase's placement rules: every file then",
        "assigned to the catch-all module, the rule the placement policy fires for it,",
        "and where it went. R1 rows carry the original evidence (the name family does",
        "not consult it); every other row carries the evidence recomputed after the R1",
        "moves, which is the state R2-R5 were applied to. Evidence only, no",
        "recommendations.",
        "",
        "## Files and evidence",
        "",
        "| file | types | knot 1 | extRefs | share | top | second | rule | destination or tag |",
        "| --- | --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    lines.extend(rows)
    lines.extend(["", "## Files moved", ""])
    if moved:
        for destination in destinations:
            lines.append("- %s: %d" % (destination, moved[destination]))
    else:
        lines.append("- none")
    lines.extend(["", "## SPLIT-CANDIDATE", ""])
    if split:
        for entry in split:
            lines.append("- %s (%s)" % (entry["file"], _placement_evidence_line(entry)))
    else:
        lines.append("- none")
    lines.extend(["", "## ORPHAN", ""])
    if orphan:
        for entry in orphan:
            lines.append("- %s (%s)" % (entry["file"], _placement_evidence_line(entry)))
    else:
        lines.append("- none")
    lines.extend(["", "## UNDECIDED", ""])
    if undecided:
        for entry in undecided:
            lines.append("- %s (%s)" % (entry["file"], _placement_evidence_line(entry)))
    else:
        lines.append("- none")
    lines.extend(
        [
            "",
            "## Module metrics before and after",
            "",
            "| module | files before | files after | fan-out before | fan-out after |"
            " fan-in before | fan-in after | I before | I after |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- |",
        ]
    )
    lines.extend(metric_rows)
    lines.extend(
        [
            "",
            "Upward edges (production, no tooling): before %d, after %d."
            % (upward_count(before_model), upward_count(after_model)),
            "",
            "Catch-all (%s): %d files before, %d files after."
            % (
                catch_all,
                (module_entry(before_model, catch_all) or {}).get("files", 0),
                (module_entry(after_model, catch_all) or {}).get("files", 0),
            ),
            "",
            "Placement rules whose destination does not match the current map: %s."
            % (", ".join(sorted(mismatches)) if mismatches else "none"),
            "",
        ]
    )
    return "\n".join(lines)


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


def build_model(source_root, rules, tooling, measure_sizes=True):
    """Scan the source tree and return the JSON-ready model dict.

    `measure_sizes=False` skips the per-member size scan (and leaves
    `model["sizes"]` empty) for callers that only need the graph, such as the
    two historical models `--place` rebuilds.
    """
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
    newline_drops = {}
    raw_lines = {}
    for rel_path, _module in assignments:
        text = (source_root / rel_path).read_text(encoding="utf-8-sig", errors="replace")
        drops = []
        stripped[rel_path] = strip_comments_and_strings(text, drops)
        newline_drops[rel_path] = drops
        raw_lines[rel_path] = len(text.splitlines())

    declaring_modules = defaultdict(set)
    for rel_path, module in assignments:
        for name in declared_types(stripped[rel_path]):
            declaring_modules[name].add(module)
    # A name declared in two modules (nested `Entry`, `Outcome`, ...) cannot be
    # attributed by text alone, so it is dropped from the table rather than
    # credited to whichever module the walk met first.
    type_modules = {
        name: next(iter(modules))
        for name, modules in declaring_modules.items()
        if len(modules) == 1
    }
    ambiguous = sorted(name for name, modules in declaring_modules.items() if len(modules) > 1)

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

    instability = {m["name"]: m["instability"] for m in modules}
    edges = []
    for (frm, to) in sorted(weights, key=lambda key: (-weights[key], key[0], key[1])):
        edges.append(
            {
                "from": frm,
                "to": to,
                "weight": weights[(frm, to)],
                "cyclic": (frm, to) in cyclic,
                "upward": is_upward(instability[frm], instability[to]),
                "files": sorted(edge_files[(frm, to)]),
                "types": sorted(edge_types[(frm, to)]),
            }
        )

    # Type ladder: production types only, keyed by name. A name declared by
    # more than one module is excluded (text cannot say which one a use means),
    # and tooling modules do not contribute types at all.
    graph_names = {
        name
        for name, declaring in declaring_modules.items()
        if len(declaring) == 1 and next(iter(declaring)) not in tooling
    }
    first_declaration = {}
    all_declarations = defaultdict(list)
    references_union = defaultdict(set)
    declaring_files = defaultdict(set)
    declared_counts = {}
    file_references = {}
    size_files = []
    size_parts = defaultdict(list)
    # A partial type has no single file. Its PRIMARY file is the one holding
    # the most of its body lines (ties by name), because that is the file the
    # churn and hotspot join reads: attributing GhostMapPresence to whichever
    # part the walk met first hid it from every history table.
    part_lines = defaultdict(list)
    for rel_path, module in assignments:
        if module in tooling:
            continue
        declarations = type_declarations(stripped[rel_path])
        declared_counts[rel_path] = len(declarations)
        file_references[rel_path] = file_type_references(stripped[rel_path], graph_names)
        line_at = line_index(stripped[rel_path], newline_drops[rel_path])
        by_parent = declarations_by_parent(declarations)
        size_files.append(
            {
                "file": rel_path,
                "module": module,
                "lines": raw_lines[rel_path],
                "declaredTypes": len(declarations),
                "topLevelTypes": sum(1 for d in declarations if d["parent"] is None),
                "types": sorted({d["name"] for d in declarations}),
            }
        )
        for index, declaration in enumerate(declarations):
            name = declaration["name"]
            if name not in graph_names:
                continue
            declaring_files[name].add(rel_path)
            all_declarations[name].append(declaration)
            # The part's own line span is always measured (it decides the
            # primary file below); the member scan is what `measure_sizes`
            # gates, because it is the expensive half.
            part_lines[name].append(
                (
                    line_at(declaration["span"][1] - 1) - line_at(declaration["start"]) + 1,
                    rel_path,
                )
            )
            if measure_sizes:
                size_parts[name].append(
                    {
                        "file": rel_path,
                        "facts": scan_declaration_members(
                            stripped[rel_path], declarations, index, line_at, by_parent
                        ),
                    }
                )
            if name not in first_declaration:
                first_declaration[name] = (module, rel_path, declaration)
        for name, refs in type_references(stripped[rel_path], declarations, graph_names).items():
            if name in graph_names:
                references_union[name] |= {ref for ref in refs if ref in graph_names and ref != name}
    graph_names &= set(first_declaration)

    type_graph = {name: sorted(references_union[name]) for name in graph_names}
    type_level = type_levels(type_graph)
    reverse_references = defaultdict(set)
    for name, refs in type_graph.items():
        for target in refs:
            reverse_references[target].add(name)

    # Knots: the SCCs of size 2+ where the level collapses. Each knot gets a
    # 1-based index in largest-first order, a greedy cut sequence, and a
    # sublevel per member (the ordinary levelling after the cut edges are
    # removed) so the ladder can order the inside of the knot.
    type_modules_map = {name: first_declaration[name][0] for name in graph_names}
    knot_list = knots(type_graph, type_modules_map)
    knot_of = {}
    sublevel_of = {}
    for index, knot in enumerate(knot_list, 1):
        knot["index"] = index
        cuts = greedy_sink_cuts(knot["members"], type_graph)
        knot["cuts"] = cuts
        for name, level in sublevels(knot["members"], type_graph, cuts).items():
            knot_of[name] = index
            sublevel_of[name] = level

    types = []
    for name in sorted(graph_names, key=lambda n: (first_declaration[n][0], n)):
        module, rel_path, declaration = first_declaration[name]
        ordered_files = [
            file_name
            for _lines, file_name in sorted(
                part_lines.get(name, []), key=lambda pair: (-pair[0], pair[1])
            )
        ]
        primary_file = ordered_files[0] if ordered_files else rel_path
        # A partial type declares its base and modifiers on one part only, so
        # the merged name sees the union of every part rather than whichever
        # file the walk met first.
        modifiers = sorted({mod for part in all_declarations[name] for mod in part["modifiers"]})
        bases = []
        for part in all_declarations[name]:
            for base in part["bases"]:
                if base not in bases:
                    bases.append(base)
        role_declaration = {
            "kind": declaration["kind"],
            "modifiers": set(modifiers),
            "bases": bases,
            "body": declaration["body"],
            "before": " ".join(part["before"] for part in all_declarations[name]),
        }
        types.append(
            {
                "name": name,
                "module": module,
                "file": primary_file,
                "files": ordered_files or [rel_path],
                "kind": declaration["kind"],
                "modifiers": modifiers,
                "bases": bases,
                "enclosing": declaration["enclosing"],
                "role": type_role(role_declaration),
                "level": type_level[name],
                "knot": knot_of.get(name),
                "sublevel": sublevel_of.get(name),
                "fanIn": type_fanin(name, file_references, declaring_files[name]),
                "fanOut": len(type_graph[name]),
                "referencesTo": type_graph[name],
                "referencedBy": sorted(reverse_references.get(name, set())),
            }
        )
    # Size facts are measured here because this is where the stripped text, the
    # declarations and the partial-merge rule already live; the history join,
    # the rules and the tiers are a separate pure pass (size_report).
    def body_text(rel_path, start, end):
        return stripped[rel_path][start:end]

    top_level_in_file = {row["file"]: row["topLevelTypes"] for row in size_files}
    size_types = []
    for name in sorted(graph_names, key=lambda n: (first_declaration[n][0], n)):
        merged = merge_type_size(size_parts[name], body_text)
        merged["name"] = name
        merged["module"] = first_declaration[name][0]
        merged["file"] = merged["files"][0]["file"] if merged["files"] else first_declaration[name][1]
        merged["topLevelTypesInFile"] = top_level_in_file.get(merged["file"], 1)
        size_types.append(merged)
    size_types.sort(key=lambda row: (-row["lines"], row["name"]))
    size_files.sort(key=lambda row: (-row["lines"], row["file"]))

    histogram = defaultdict(int)
    for entry in types:
        histogram[entry["level"]] += 1
    levels_payload = {
        "max": max(histogram) if histogram else 0,
        "histogram": {level: histogram[level] for level in sorted(histogram)},
    }

    return {
        "modules": modules,
        "edges": edges,
        "unclassified": unclassified,
        "ambiguousTypes": ambiguous,
        "types": types,
        "typeLevels": levels_payload,
        "knots": knot_list,
        "fileModules": {rel_path: module for rel_path, module in assignments},
        "declaredTypeCounts": declared_counts,
        "sizes": {"files": size_files, "types": size_types},
        "catchAll": rules[-1]["name"] if rules else None,
    }


def is_upward(instability_from, instability_to):
    """True when an edge runs against the stability gradient.

    Martin's stable-dependencies rule: a module should depend only on modules
    at least as stable as itself (lower or equal instability). An edge from a
    more stable module to a less stable one is the violation worth drawing in
    red; with the whole production graph inside one strongly connected
    component, "on a cycle" marks nearly every edge and separates nothing.
    """
    return instability_from < instability_to


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
        color = "#cc0000" if edge["upward"] else "#8a949e"
        types = ", ".join(edge["types"][:12])
        if len(edge["types"]) > 12:
            types += " (+%d more)" % (len(edge["types"]) - 12)
        tooltip = "%s -> %s: %d references; types: %s" % (
            edge["from"],
            edge["to"],
            edge["weight"],
            types,
        )
        # Upward edges do not constrain the layout, so dot ranks modules along
        # the stability gradient (unstable at the top, stable sinks at the
        # bottom) and every red arrow is visibly the one pointing back up.
        constraint = ", constraint=false" if edge["upward"] else ""
        lines.append(
            '  "%s" -> "%s" [penwidth=%.2f, color="%s", tooltip="%s"%s];'
            % (
                _dot_escape(edge["from"]),
                _dot_escape(edge["to"]),
                penwidth,
                color,
                _dot_escape(tooltip),
                constraint,
            )
        )
    lines.append("}")
    return "\n".join(lines) + "\n"


def render_svg(dot_path, svg_path):
    """Run graphviz over the dot file. Warns and returns False when absent.

    A failure removes the target so a previous run's map is not left behind
    pretending to be current.
    """
    svg_path = Path(svg_path)
    try:
        completed = subprocess.run(
            ["dot", "-Tsvg", str(dot_path), "-o", str(svg_path)],
            capture_output=True,
            text=True,
            check=False,
        )
    except FileNotFoundError:
        print("WARN graphviz 'dot' not found; modules.svg not written", file=sys.stderr)
        svg_path.unlink(missing_ok=True)
        return False
    if completed.returncode != 0:
        print(
            "WARN dot failed (exit %d): %s" % (completed.returncode, completed.stderr.strip()),
            file=sys.stderr,
        )
        svg_path.unlink(missing_ok=True)
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
            css_class = "cell cyc" if edge["upward"] else "cell"
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
        " the row module to the column module; shade scales with that count. With rows and"
        " columns both sorted by instability, everything above the diagonal points from a"
        " more stable module to a less stable one; a red outline marks exactly those"
        " upward edges (the stable-dependencies rule says they should not exist)."
        " Diagonal is the file count.</p>",
        '<p class="note">Generated by <code>scripts/arch/archview.py</code>; do not edit by'
        " hand. Production modules only; hidden tooling modules: %s.</p>" % html.escape(", ".join(tooling)),
        '<p class="legend"><span class="swatch" style="background:#e8eef7"></span>low'
        '<span class="swatch" style="background:#2e5e9c"></span>high reference count'
        '<span class="swatch" style="outline:2px solid #cc0000; outline-offset:-2px"></span>upward (stable depends on less stable)</p>',
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
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color: #222;
  background: #fcfdfe; }
#app { display: flex; height: 100%; }
#main { flex: 1 1 auto; display: flex; flex-direction: column; min-width: 0; }
#controls { padding: 8px 12px; border-bottom: 1px solid #d0d6dd; display: flex; gap: 18px;
  align-items: center; flex-wrap: wrap; font-size: 13px; background: #f6f8fa; }
#graph { flex: 1 1 auto; min-height: 0; }
#graph svg { width: 100%; height: 100%; display: block; }
#panel { width: 340px; flex: 0 0 340px; border-left: 1px solid #d0d6dd; overflow: auto;
  padding: 10px 14px; font-size: 13px; box-sizing: border-box; background: #ffffff; }
#panel h2 { font-size: 15px; margin: 4px 0 2px; }
#panel h3 { font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; color: #667;
  margin: 14px 0 4px; }
#panel p.metrics { color: #555; font-size: 12px; margin: 0 0 6px; }
#panel p.hint, #panel p.none { color: #777; }
#panel ul { list-style: none; margin: 0; padding: 0; }
#panel li { padding: 5px 0; border-bottom: 1px solid #eef1f4; }
#panel .w { color: #2e5e9c; font-weight: 600; }
#panel .up { color: #cc0000; font-size: 11px; margin-left: 4px; }
#panel .types { color: #667; font-size: 11px; word-wrap: break-word; }
.legend { color: #555; }
.swatch { display: inline-block; width: 12px; height: 12px; vertical-align: -2px;
  margin-right: 4px; border: 1px solid #bbb; }
.swatch.edge { height: 4px; background: #9aa4b1; }
.swatch.red { background: #cc0000; }
.swatch.gray { background: #9aa4b1; }
g.node { cursor: pointer; }
g.node rect { fill: #4c78a8; stroke: #2f5580; stroke-width: 1; }
g.node.tooling rect { fill: #9aa4b1; stroke: #6d7681; }
g.node text { fill: #ffffff; font-size: 11px; text-anchor: middle; pointer-events: none; }
g.node.focused rect { stroke: #e8810c; stroke-width: 4; }
path.edge { fill: none; stroke: #9aa4b1; }
path.edge.upward { stroke: #cc0000; }
.faded { opacity: 0.08; }
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
      <span class="legend"><span class="swatch red"></span>upward edge (stable depends on less stable)</span>
      <span class="legend"><span class="swatch gray"></span>tooling module</span>
      <span class="legend">rows: unstable (top) to stable (bottom)</span>
    </div>
    <div id="graph"></div>
  </div>
  <div id="panel">
    <p class="hint">Click a module to see its direct neighbours.</p>
  </div>
</div>
<script>
"use strict";
// Self-contained: no library, no network. Nineteen boxes and under a hundred
// arrows do not need a graph engine, and a docs artifact must open from disk.
const DATA = @@DATA@@;
const MIN_EDGE = @@MIN_EDGE@@;
const MAX_WEIGHT = @@MAX_WEIGHT@@;
const MODULES = DATA.modules;
const EDGES = DATA.edges;
const SVG_NS = "http://www.w3.org/2000/svg";
const NODE_W = 124;
const NODE_H = 40;
const PER_ROW = 5;

let showTooling = false;
let minWeight = MIN_EDGE;
let focused = null;

function esc(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function moduleByName(name) {
  return MODULES.find(function (m) { return m.name === name; });
}

function visibleModules() {
  return MODULES.filter(function (m) { return showTooling || !m.tooling; });
}

function visibleEdges(names) {
  return EDGES.filter(function (e) {
    return names.has(e.from) && names.has(e.to) && e.weight >= minWeight;
  });
}

// Layout: modules sorted by instability, cut into rows of at most PER_ROW so
// no row overflows (unstable at the top, sinks at the bottom, the same
// reading as the dot view). Ties in instability never straddle a row edge.
function layout(modules, width, height) {
  const sorted = modules.slice().sort(function (a, b) {
    return b.instability - a.instability || (a.name < b.name ? -1 : 1);
  });
  const rows = [];
  let row = [];
  sorted.forEach(function (m, i) {
    const sameAsPrev = i > 0 && sorted[i - 1].instability === m.instability;
    if (row.length >= PER_ROW && !sameAsPrev) { rows.push(row); row = []; }
    row.push(m);
  });
  if (row.length) rows.push(row);
  const pos = {};
  const top = 50;
  const bottom = height - 50;
  rows.forEach(function (r, ri) {
    const y = rows.length === 1 ? (top + bottom) / 2 : top + (bottom - top) * ri / (rows.length - 1);
    r.sort(function (a, b) { return a.name < b.name ? -1 : 1; });
    r.forEach(function (m, ci) {
      pos[m.name] = { x: width * (ci + 1) / (r.length + 1), y: y };
    });
  });
  return pos;
}

function el(tag, attrs) {
  const node = document.createElementNS(SVG_NS, tag);
  Object.keys(attrs).forEach(function (k) { node.setAttribute(k, attrs[k]); });
  return node;
}

// Quadratic curve bowed to the right of travel, so A->B and B->A separate.
function edgePath(a, b) {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const len = Math.sqrt(dx * dx + dy * dy) || 1;
  const nx = -dy / len;
  const ny = dx / len;
  const bow = Math.min(40, len * 0.12);
  const mx = (a.x + b.x) / 2 + nx * bow;
  const my = (a.y + b.y) / 2 + ny * bow;
  // Trim both ends to the box border along the chord.
  const ux = dx / len;
  const uy = dy / len;
  const trimA = Math.min(NODE_W / 2 / Math.max(Math.abs(ux), 0.001), NODE_H / 2 / Math.max(Math.abs(uy), 0.001));
  const sx = a.x + ux * trimA;
  const sy = a.y + uy * trimA;
  const ex = b.x - ux * (trimA + 6);
  const ey = b.y - uy * (trimA + 6);
  return "M" + sx + "," + sy + " Q" + mx + "," + my + " " + ex + "," + ey;
}

function render() {
  const host = document.getElementById("graph");
  host.innerHTML = "";
  const width = Math.max(host.clientWidth, 600);
  const height = Math.max(host.clientHeight, 400);
  const modules = visibleModules();
  const names = new Set(modules.map(function (m) { return m.name; }));
  const edges = visibleEdges(names);
  const pos = layout(modules, width, height);

  const svg = el("svg", { viewBox: "0 0 " + width + " " + height });
  const defs = el("defs", {});
  ["#9aa4b1", "#cc0000"].forEach(function (color, i) {
    const marker = el("marker", { id: "arrow" + i, viewBox: "0 0 10 10", refX: "9", refY: "5",
      markerWidth: "9", markerHeight: "9", markerUnits: "userSpaceOnUse", orient: "auto-start-reverse" });
    marker.appendChild(el("path", { d: "M0,0 L10,5 L0,10 z", fill: color }));
    defs.appendChild(marker);
  });
  svg.appendChild(defs);

  const edgeLayer = el("g", { id: "edges" });
  edges.forEach(function (e) {
    const path = el("path", {
      d: edgePath(pos[e.from], pos[e.to]),
      "stroke-width": (1 + 7 * (e.weight - 1) / Math.max(MAX_WEIGHT - 1, 1)).toFixed(2),
      "marker-end": e.upward ? "url(#arrow1)" : "url(#arrow0)",
      "class": "edge" + (e.upward ? " upward" : ""),
      "data-from": e.from,
      "data-to": e.to
    });
    const title = el("title", {});
    title.textContent = e.from + " -> " + e.to + ": " + e.weight + " references";
    path.appendChild(title);
    edgeLayer.appendChild(path);
  });
  svg.appendChild(edgeLayer);

  const nodeLayer = el("g", { id: "nodes" });
  modules.forEach(function (m) {
    const p = pos[m.name];
    const g = el("g", { "class": "node" + (m.tooling ? " tooling" : ""), "data-name": m.name,
      transform: "translate(" + (p.x - NODE_W / 2) + "," + (p.y - NODE_H / 2) + ")" });
    g.appendChild(el("rect", { width: NODE_W, height: NODE_H, rx: 6, ry: 6 }));
    const t1 = el("text", { x: NODE_W / 2, y: 16 });
    t1.textContent = m.name;
    const t2 = el("text", { x: NODE_W / 2, y: 31, "font-size": "9" });
    t2.textContent = m.files + " files, I=" + m.instability.toFixed(2);
    g.appendChild(t1);
    g.appendChild(t2);
    g.addEventListener("click", function (evt) { evt.stopPropagation(); focusModule(m.name); });
    nodeLayer.appendChild(g);
  });
  svg.appendChild(nodeLayer);
  svg.addEventListener("click", function () { clearFocus(); panelHint(); });
  host.appendChild(svg);
  if (focused && names.has(focused)) applyFocus(focused); else { focused = null; panelHint(); }
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
      (e.upward ? '<span class="up">upward</span>' : "") +
      '<div class="types">' + esc(e.types.join(", ")) + "</div></li>";
  });
  return "<h3>" + title + "</h3>" + body + "</ul>";
}

function renderPanel(name) {
  const module = moduleByName(name);
  const outgoing = EDGES.filter(function (e) {
    return e.from === name && (showTooling || !moduleByName(e.to).tooling);
  });
  const incoming = EDGES.filter(function (e) {
    return e.to === name && (showTooling || !moduleByName(e.from).tooling);
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
  focused = null;
  document.querySelectorAll("#graph .faded, #graph .focused").forEach(function (n) {
    n.classList.remove("faded");
    n.classList.remove("focused");
  });
}

function applyFocus(name) {
  const keep = new Set([name]);
  document.querySelectorAll("#graph path.edge").forEach(function (p) {
    const from = p.getAttribute("data-from");
    const to = p.getAttribute("data-to");
    const touches = from === name || to === name;
    if (touches) { keep.add(from); keep.add(to); }
    p.classList.toggle("faded", !touches);
  });
  document.querySelectorAll("#graph g.node").forEach(function (g) {
    const n = g.getAttribute("data-name");
    g.classList.toggle("faded", !keep.has(n));
    g.classList.toggle("focused", n === name);
  });
  renderPanel(name);
}

function focusModule(name) {
  clearFocus();
  focused = name;
  applyFocus(name);
}

document.getElementById("tooling").addEventListener("change", function () {
  showTooling = this.checked;
  render();
});
document.getElementById("minw").addEventListener("input", function () {
  minWeight = parseInt(this.value, 10);
  document.getElementById("minwv").textContent = String(minWeight);
  render();
});
window.addEventListener("resize", render);
render();
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


def write_types_json(model, out_path):
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "types": model.get("types", []),
        "levels": model.get("typeLevels", {"max": 0, "histogram": {}}),
    }
    out_path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


LADDER_TEMPLATE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Parsek type ladder</title>
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color: #222;
  background: #fcfdfe; }
#app { display: flex; height: 100%; }
#main { flex: 1 1 auto; display: flex; flex-direction: column; min-width: 0; }
#controls { padding: 8px 12px; border-bottom: 1px solid #d0d6dd; display: flex; gap: 18px;
  align-items: center; flex-wrap: wrap; font-size: 13px; background: #f6f8fa; }
#board { flex: 1 1 auto; overflow: auto; }
#panel { width: 340px; flex: 0 0 340px; border-left: 1px solid #d0d6dd; overflow: auto;
  padding: 10px 14px; font-size: 13px; box-sizing: border-box; background: #ffffff; }
#panel h2 { font-size: 15px; margin: 4px 0 2px; }
#panel h3 { font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; color: #667;
  margin: 14px 0 4px; }
#panel p.metrics { color: #555; font-size: 12px; margin: 0 0 6px; }
#panel p.hint, #panel p.none { color: #777; }
#panel ul { list-style: none; margin: 0; padding: 0; }
#panel li { padding: 4px 0; border-bottom: 1px solid #eef1f4; }
#panel .mod { color: #667; font-size: 11px; }
#panel .fi { color: #667; font-size: 11px; }
#panel a.ref { color: #2e5e9c; cursor: pointer; text-decoration: none; }
#panel a.ref:hover { text-decoration: underline; }
table.ladder { border-collapse: collapse; font-size: 11px; }
table.ladder th, table.ladder td { border: 1px solid #e3e8ee; padding: 3px 5px;
  vertical-align: top; }
table.ladder thead th { position: sticky; top: 0; background: #f6f8fa; z-index: 2;
  font-weight: 600; white-space: nowrap; text-align: left; }
table.ladder th.lvl { position: sticky; left: 0; background: #f6f8fa; z-index: 1;
  font-weight: 600; text-align: right; white-space: nowrap; }
table.ladder thead th.corner { position: sticky; left: 0; top: 0; z-index: 3; }
td.cell { min-width: 130px; max-width: 230px; }
.chip { display: inline-flex; align-items: center; border: 1px solid #d7dde4; border-radius: 9px;
  padding: 1px 6px 1px 2px; margin: 1px; background: #ffffff; cursor: pointer;
  white-space: nowrap; }
.chip:hover { border-color: #4c78a8; }
.chip .glyph { display: inline-block; width: 14px; line-height: 14px; text-align: center;
  border-radius: 50%; background: #4c78a8; color: #ffffff; font-size: 9px; margin-right: 4px; }
.chip .fi { color: #667; font-size: 9px; margin-left: 4px; }
.chip.role-E .glyph { background: #b45309; }
.chip.role-I .glyph { background: #7c3aed; }
.chip.role-A .glyph { background: #0e7490; }
.chip.role-N .glyph { background: #6d7681; }
.chip.role-S .glyph { background: #15803d; }
.chip.role-M .glyph { background: #2e5e9c; }
.chip.role-D .glyph { background: #9aa4b1; }
.chip.role-C .glyph { background: #cc0000; }
.chip-hidden { display: none; }
td.cell.expanded .chip-hidden { display: inline-flex; }
td.cell.expanded .more { display: none; }
.more { color: #2e5e9c; cursor: pointer; font-size: 10px; margin-left: 3px; }
.ladder.filtering .chip { display: none; }
.ladder.filtering .chip.match { display: inline-flex; }
.ladder.filtering .more { display: none; }
.chip.dim { opacity: 0.15; }
.chip.hl-down { border-color: #2e5e9c; box-shadow: 0 0 0 2px rgba(46,94,156,0.35); }
.chip.hl-up { border-color: #b45309; box-shadow: 0 0 0 2px rgba(180,83,9,0.35); }
.chip.hl-self { border-color: #e8810c; box-shadow: 0 0 0 3px rgba(232,129,12,0.45); }
tr.knot-row > :first-child { border-left: 3px solid #b45309; }
tr.knot-row td { background: #fffaf3; }
th.lvl.knot { vertical-align: top; }
th.lvl.knot .knotlabel { display: block; color: #b45309; font-weight: 400; font-size: 9px; }
.xmark { display: inline-block; margin-left: 3px; padding: 0 2px; border: 1px solid #b45309;
  border-radius: 3px; color: #b45309; font-size: 8px; font-weight: 700; cursor: help; }
.legend .chip { cursor: default; }
</style>
</head>
<body>
<div id="app">
  <div id="main">
    <div id="controls">
      <label>filter <input type="text" id="filter" placeholder="type name"></label>
      <span class="legend">roles:
        <span class="chip role-E"><span class="glyph">E</span>entry</span>
        <span class="chip role-I"><span class="glyph">I</span>interface</span>
        <span class="chip role-A"><span class="glyph">A</span>abstract</span>
        <span class="chip role-N"><span class="glyph">N</span>enum</span>
        <span class="chip role-S"><span class="glyph">S</span>static</span>
        <span class="chip role-M"><span class="glyph">M</span>implements</span>
        <span class="chip role-D"><span class="glyph">D</span>data</span>
        <span class="chip role-C"><span class="glyph">C</span>service</span>
      </span>
      <span class="legend"><span class="xmark">X</span> cut sink (hover for the references it drops)</span>
      <span class="legend">rows: level (top = most abstract); a knot is split into sub-rows,
        top = higher sublevel</span>
    </div>
    <div id="board"><table class="ladder" id="ladder"></table></div>
  </div>
  <div id="panel">
    <p class="hint">Click a chip to see what it references and what references it.</p>
  </div>
</div>
<script>
"use strict";
// Self-contained: no library, no network. Columns are production modules by
// descending instability; rows are levels, highest at the top, so a bottom-up
// read is a dependency-order read.
const DATA = @@DATA@@;
const MODULES = DATA.modules;
const TYPES = DATA.types;
const MAX_LEVEL = DATA.maxLevel;
const KNOTS = DATA.knots || [];
const ROLE_LETTER = { entry: "E", interface: "I", abstract: "A", enum: "N", "static": "S",
  implements: "M", data: "D", service: "C" };
const COLLAPSE_AT = 12;

const BY_NAME = {};
TYPES.forEach(function (t) { BY_NAME[t.name] = t; });

// Sink types from each knot's greedy cut sequence, so a chip can carry the X
// marker and say what it drops.
const SINK_DROPS = {};
KNOTS.forEach(function (knot) {
  Object.keys(knot.sinks || {}).forEach(function (name) { SINK_DROPS[name] = knot.sinks[name]; });
});

let selected = null;
let filter = "";

function esc(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function chipHtml(type, hidden) {
  const letter = ROLE_LETTER[type.role];
  let marker = "";
  if (SINK_DROPS[type.name]) {
    marker = '<span class="xmark" title="cut sink; drops: ' +
      esc(SINK_DROPS[type.name].join(", ")) + '">X</span>';
  }
  return '<span class="chip role-' + letter + (hidden ? " chip-hidden" : "") +
    '" data-name="' + esc(type.name) + '"><span class="glyph">' + letter + "</span>" +
    esc(type.name) + '<span class="fi">' + type.fanIn + "</span>" + marker + "</span>";
}

function cellHtml(cells, module, level, knot, sub) {
  const knotPart = knot === null || knot === undefined ? "" : knot;
  const subPart = sub === null || sub === undefined ? "" : sub;
  const key = module + "\\u0000" + level + "\\u0000" + knotPart + "\\u0000" + subPart;
  const list = (cells[key] || []).slice().sort(function (a, b) {
    return b.fanIn - a.fanIn || (a.name < b.name ? -1 : 1);
  });
  let cell = '<td class="cell">';
  list.forEach(function (t, i) { cell += chipHtml(t, i >= COLLAPSE_AT); });
  if (list.length > COLLAPSE_AT) {
    cell += '<span class="more">+' + (list.length - COLLAPSE_AT) + " more</span>";
  }
  return cell + "</td>";
}

function rowHtml(cells, level, knot, sub, rowspan) {
  let html = '<tr class="' + (knot ? "knot-row" : "") + '">';
  if (knot) {
    if (rowspan) {
      html += '<th class="lvl knot" rowspan="' + rowspan + '">L' + level +
        '<span class="knotlabel">knot ' + knot.index + " (" + knot.size + ")</span></th>";
    }
  } else {
    html += '<th class="lvl">L' + level + "</th>";
  }
  MODULES.forEach(function (m) {
    html += cellHtml(cells, m.name, level, knot ? knot.index : null, sub);
  });
  return html + "</tr>";
}

function buildTable() {
  const cells = {};
  const knotSubs = {};
  TYPES.forEach(function (t) {
    const knotPart = t.knot === null || t.knot === undefined ? "" : t.knot;
    const subPart = t.sublevel === null || t.sublevel === undefined ? "" : t.sublevel;
    const key = t.module + "\\u0000" + t.level + "\\u0000" + knotPart + "\\u0000" + subPart;
    (cells[key] = cells[key] || []).push(t);
    if (knotPart !== "") {
      const levelKey = knotPart + "\\u0000" + t.level;
      (knotSubs[levelKey] = knotSubs[levelKey] || {})[subPart] = true;
    }
  });
  let html = '<thead><tr><th class="lvl corner">level</th>';
  MODULES.forEach(function (m) {
    html += "<th>" + esc(m.name) + '<div class="mod">' + m.files + " files, I=" +
      m.instability.toFixed(2) + "</div></th>";
  });
  html += "</tr></thead><tbody>";
  for (let level = MAX_LEVEL; level >= 0; level--) {
    html += rowHtml(cells, level, null, null, null);
    KNOTS.forEach(function (knot) {
      const subs = Object.keys(knotSubs[knot.index + "\\u0000" + level] || {});
      if (!subs.length) return;
      subs.sort(function (a, b) { return Number(b) - Number(a); });
      subs.forEach(function (sub, i) {
        html += rowHtml(cells, level, knot, Number(sub), i === 0 ? subs.length : null);
      });
    });
  }
  document.getElementById("ladder").innerHTML = html + "</tbody>";
}

function applyFilter() {
  const table = document.getElementById("ladder");
  table.classList.toggle("filtering", filter !== "");
  document.querySelectorAll("#ladder .chip").forEach(function (chip) {
    const name = chip.getAttribute("data-name").toLowerCase();
    chip.classList.toggle("match", filter !== "" && name.indexOf(filter) !== -1);
  });
}

function clearSelection() {
  selected = null;
  document.querySelectorAll("#ladder .chip").forEach(function (chip) {
    chip.classList.remove("dim", "hl-down", "hl-up", "hl-self");
  });
  document.getElementById("panel").innerHTML =
    '<p class="hint">Click a chip to see what it references and what references it.</p>';
}

function refList(names) {
  const byModule = {};
  names.forEach(function (name) {
    const type = BY_NAME[name];
    if (!type) return;
    (byModule[type.module] = byModule[type.module] || []).push(type);
  });
  const modules = Object.keys(byModule).sort();
  if (!modules.length) return '<p class="none">none</p>';
  let html = "";
  modules.forEach(function (module) {
    byModule[module].sort(function (a, b) {
      return b.fanIn - a.fanIn || (a.name < b.name ? -1 : 1);
    });
    html += '<div><span class="mod">' + esc(module) + "</span><ul>";
    byModule[module].forEach(function (type) {
      html += '<li><a class="ref" data-name="' + esc(type.name) + '">' + esc(type.name) +
        '</a> <span class="fi">fan-in ' + type.fanIn + ", L" + type.level + "</span></li>";
    });
    html += "</ul></div>";
  });
  return html;
}

function renderPanel(type) {
  document.getElementById("panel").innerHTML =
    "<h2>" + esc(type.name) + "</h2>" +
    '<p class="metrics">' + esc(type.role) + " " + esc(type.kind) + ", " + esc(type.module) +
    " - level " + type.level + ", fan-in " + type.fanIn + ", fan-out " + type.fanOut +
    (type.enclosing ? ", nested in " + esc(type.enclosing) : "") + "</p>" +
    "<h3>references (" + type.referencesTo.length + ")</h3>" + refList(type.referencesTo) +
    "<h3>referenced by (" + type.referencedBy.length + ")</h3>" + refList(type.referencedBy);
  document.getElementById("panel").scrollTop = 0;
}

function selectType(name) {
  const type = BY_NAME[name];
  if (!type) return;
  selected = name;
  const down = {};
  const up = {};
  type.referencesTo.forEach(function (n) { down[n] = true; });
  type.referencedBy.forEach(function (n) { up[n] = true; });
  document.querySelectorAll("#ladder .chip").forEach(function (chip) {
    const n = chip.getAttribute("data-name");
    const isSelf = n === name;
    const isDown = !!down[n];
    const isUp = !!up[n];
    chip.classList.toggle("hl-self", isSelf);
    chip.classList.toggle("hl-down", !isSelf && isDown);
    chip.classList.toggle("hl-up", !isSelf && isUp);
    chip.classList.toggle("dim", !isSelf && !isDown && !isUp);
  });
  renderPanel(type);
}

document.getElementById("ladder").addEventListener("click", function (evt) {
  const more = evt.target.closest ? evt.target.closest(".more") : null;
  if (more) { more.parentElement.classList.add("expanded"); return; }
  const chip = evt.target.closest ? evt.target.closest(".chip") : null;
  if (chip) { selectType(chip.getAttribute("data-name")); return; }
  clearSelection();
});
document.getElementById("filter").addEventListener("input", function () {
  filter = this.value.trim().toLowerCase();
  applyFilter();
});
document.getElementById("panel").addEventListener("click", function (evt) {
  const link = evt.target.closest ? evt.target.closest("a.ref") : null;
  if (link) selectType(link.getAttribute("data-name"));
});

buildTable();
applyFilter();
clearSelection();
</script>
</body>
</html>
"""


def render_ladder_html(model):
    """Return the type ladder as a standalone HTML page."""
    modules = sorted(
        (m for m in model["modules"] if not m["tooling"]),
        key=lambda m: (-m["instability"], m["name"]),
    )
    payload = {
        "modules": [
            {"name": m["name"], "files": m["files"], "instability": m["instability"]}
            for m in modules
        ],
        "types": model.get("types", []),
        "maxLevel": model.get("typeLevels", {}).get("max", 0),
        "knots": [
            {
                "index": knot["index"],
                "size": knot["size"],
                "sinks": {cut["sink"]: cut["droppedReferences"] for cut in knot["cuts"]},
            }
            for knot in model.get("knots", [])
        ],
    }
    data = json.dumps(payload, ensure_ascii=True, separators=(",", ":")).replace("</", "<\\/")
    return LADDER_TEMPLATE.replace("@@DATA@@", data)


# ---------------------------------------------------------------------------
# atlas renderer
# ---------------------------------------------------------------------------


def load_prose(path):
    """Return the atlas prose dict from a TOML file."""
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def band_for(instability, bands):
    """Return (band_id, label) for an instability value; first match wins."""
    for band_id, band in bands.items():
        if instability >= band["min"]:
            return band_id, band["label"]
    return None, ""


def prose_findings(model, prose):
    """Return the five atlas staleness lists for prose against the live model.

    Keys: missingSummaries (production modules with no summary),
    missingGlossary (glossary names that are not declared types),
    staleGlossary (glossary names outside the live top 18 hubs, informational),
    staleReadings (upward readings whose edge no longer exists) and
    missingReadingOrder (reading-order types not in the model).
    """
    production = [module for module in model["modules"] if not module["tooling"]]
    summaries = prose.get("modules", {})

    def has_summary(name):
        entry = summaries.get(name)
        return isinstance(entry, dict) and bool(entry.get("summary"))

    missing_summaries = sorted(
        module["name"] for module in production if not has_summary(module["name"])
    )
    type_names = {entry["name"] for entry in model.get("types", [])}
    glossary = prose.get("glossary", {})
    missing_glossary = sorted(name for name in glossary if name not in type_names)
    hubs = {
        entry["name"]
        for entry in sorted(model.get("types", []), key=lambda t: (-t["fanIn"], t["name"]))[:18]
    }
    stale_glossary = sorted(name for name in glossary if name in type_names and name not in hubs)
    edge_keys = {(edge["from"], edge["to"]) for edge in model["edges"]}
    stale_readings = []
    for reading in prose.get("upward_readings", []):
        if not isinstance(reading, dict):
            stale_readings.append(str(reading))
            continue
        spec = reading.get("edge", "")
        try:
            frm, to = parse_edge_spec(spec)
        except (ValueError, TypeError):
            stale_readings.append(str(spec))
            continue
        if (frm, to) not in edge_keys:
            stale_readings.append(spec)
    order_types = set()
    for step in prose.get("reading_order", []):
        if not isinstance(step, dict):
            continue
        names = step.get("types", [])
        if not isinstance(names, list):
            continue
        order_types.update(name for name in names if isinstance(name, str))
    missing_order = sorted(name for name in order_types if name not in type_names)
    return {
        "missingSummaries": missing_summaries,
        "missingGlossary": missing_glossary,
        "staleGlossary": stale_glossary,
        "staleReadings": stale_readings,
        "missingReadingOrder": missing_order,
    }


def _thousands(value):
    return format(int(value), ",")


def _type_chips(names):
    return " ".join('<span class="id">%s</span>' % html.escape(name) for name in names)


def _svg_inline(svg_text):
    """Return modules.svg ready to inline: prolog, doctype and size gone."""
    if not svg_text:
        return (
            '<p class="layer">modules.svg is missing; run the generator with Graphviz '
            "on PATH to draw the map.</p>"
        )
    start = svg_text.find("<svg")
    text = svg_text[start:] if start != -1 else svg_text
    text = re.sub(
        r"<svg\b[^>]*>",
        lambda match: re.sub(r'\s+(width|height)="[^"]*"', "", match.group(0)),
        text,
        count=1,
    )
    return text.lstrip()


def _atlas_eyebrow():
    today = datetime.date.today().isoformat()
    branch = "unknown"
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode == 0 and result.stdout.strip():
            branch = result.stdout.strip()
    except Exception:
        pass
    return "Source snapshot, %s, branch %s" % (today, branch)


def _atlas_tiles(model):
    files = sum(module["files"] for module in model["modules"])
    types = len(model.get("types", []))
    histogram = model.get("typeLevels", {}).get("histogram", {})
    level_zero = histogram.get(0, histogram.get("0", 0))
    knots = model.get("knots", [])
    knot_size = knots[0]["size"] if knots else 0
    modules = sum(1 for module in model["modules"] if not module["tooling"])
    return "\n".join(
        [
            '  <div class="tile"><div class="n">%s</div>'
            '<div class="l">C&#35; files in one assembly</div></div>' % _thousands(files),
            '  <div class="tile"><div class="n">%s</div>'
            '<div class="l">production types across %d modules</div></div>'
            % (_thousands(types), modules),
            '  <div class="tile"><div class="n">%s</div>'
            '<div class="l">of those types depend on nothing in the repo:'
            " plain data and enums</div></div>" % _thousands(level_zero),
            '  <div class="tile"><div class="n knot">%s</div>'
            '<div class="l">types locked in one dependency cycle</div></div>'
            % _thousands(knot_size),
        ]
    )


def _atlas_directory(model, prose):
    production = [module for module in model["modules"] if not module["tooling"]]
    by_module = defaultdict(list)
    for entry in model.get("types", []):
        by_module[entry["module"]].append(entry)
    summaries = prose.get("modules", {})
    bands = prose.get("bands", {})
    grouped = defaultdict(list)
    band_order = []
    for module in sorted(production, key=lambda m: (-m["instability"], m["name"])):
        band_id, label = band_for(module["instability"], bands)
        if band_id not in grouped:
            band_order.append((band_id, label))
        grouped[band_id].append(module)
    lines = []
    for band_id, label in band_order:
        lines.append('<tr class="band"><td colspan="6">%s</td></tr>' % html.escape(label))
        for module in grouped[band_id]:
            entries = by_module.get(module["name"], [])
            hubs = sorted(entries, key=lambda t: (-t["fanIn"], t["name"]))[:3]
            summary_entry = summaries.get(module["name"])
            summary = (
                summary_entry.get("summary")
                if isinstance(summary_entry, dict)
                else None
            ) or "(no summary yet)"
            lines.append(
                '<tr><td class="name">%s</td><td>%s</td><td class="num">%d</td>'
                '<td class="num">%d</td><td class="num">%.2f</td><td>%s</td></tr>'
                % (
                    html.escape(module["name"]),
                    summary,
                    module["files"],
                    len(entries),
                    module["instability"],
                    _type_chips([hub["name"] for hub in hubs]),
                )
            )
    return "\n".join(lines)


def _atlas_vocabulary(model, prose):
    glossary = prose.get("glossary", {})
    lines = []
    for entry in sorted(model.get("types", []), key=lambda t: (-t["fanIn"], t["name"]))[:18]:
        meaning_entry = glossary.get(entry["name"])
        meaning = (
            meaning_entry.get("meaning") if isinstance(meaning_entry, dict) else None
        ) or "(no meaning yet)"
        lines.append(
            "  <dt>%s <span class=\"layer\">%d files</span></dt><dd>%s</dd>"
            % (html.escape(entry["name"]), entry["fanIn"], meaning)
        )
    return "\n".join(lines)


def _atlas_knot_intro(model, prose):
    page = prose.get("page", {})
    knots = model.get("knots", [])
    if not knots:
        return "<p>No knot today.</p>"
    knot = knots[0]
    production_types = len(model.get("types", [])) or 1
    percent = round(100 * knot["size"] / production_types)
    module_of = {entry["name"]: entry["module"] for entry in model.get("types", [])}
    counts = Counter(module_of[name] for name in knot["members"] if name in module_of)
    top = sorted(counts.items(), key=lambda item: (-item[1], item[0]))[:8]
    listed = ", ".join("%s (%d)" % (html.escape(name), count) for name, count in top)
    more = len(counts) - len(top)
    tail = " and %d more modules" % more if more > 0 else ""
    return (
        '<p><span class="knot">%d types, %d percent of the production code, sit in one '
        "strongly connected component.</span> %s It spans %s%s. %s</p>"
        % (
            knot["size"],
            percent,
            page.get("knot_note", ""),
            listed,
            tail,
            page.get("knot_consequence", ""),
        )
    )


def _atlas_knot_cuts(model):
    knots = model.get("knots", [])
    if not knots:
        return ""
    lines = []
    for cut in knots[0]["cuts"][:7]:
        drops = cut["droppedReferences"]
        names = _type_chips(drops[:4])
        if len(drops) > 4:
            names += " +%d" % (len(drops) - 4)
        lines.append(
            '<tr><td class="name">%s</td><td class="num">%d</td><td class="num">%d</td>'
            "<td>%s</td></tr>"
            % (html.escape(cut["sink"]), cut["sizeBefore"], cut["sizeAfter"], names)
        )
    return "\n".join(lines)


def _atlas_upward_rows(model, prose, forbidden):
    tooling = {module["name"] for module in model["modules"] if module["tooling"]}
    edge_by = {(edge["from"], edge["to"]): edge for edge in model["edges"]}
    readings = {}
    for reading in prose.get("upward_readings", []):
        if not isinstance(reading, dict):
            continue
        try:
            key = parse_edge_spec(reading.get("edge", ""))
        except (ValueError, TypeError):
            continue
        readings[key] = reading.get("reading", "")
    rows = [
        edge
        for edge in model["edges"]
        if edge["upward"]
        and edge["from"] not in tooling
        and edge["to"] not in tooling
        and edge["weight"] >= 14
    ]
    seen = {(edge["from"], edge["to"]) for edge in rows}
    for spec in forbidden or []:
        try:
            frm, to = parse_edge_spec(spec)
        except ValueError:
            continue
        edge = edge_by.get((frm, to))
        if edge is not None and (frm, to) not in seen:
            rows.append(edge)
            seen.add((frm, to))
    rows.sort(key=lambda edge: (-edge["weight"], edge["from"], edge["to"]))
    lines = []
    seen_pairs = set()
    for edge in rows:
        pair = tuple(sorted((edge["from"], edge["to"])))
        if pair in seen_pairs:
            # The reverse edge is already in the list: the pair row carries
            # both weights, including the forbidden edge's.
            continue
        seen_pairs.add(pair)
        reverse = edge_by.get((edge["to"], edge["from"]))
        if reverse is not None:
            name = '<span class="up">%s &harr; %s</span>' % (
                html.escape(edge["from"]),
                html.escape(edge["to"]),
            )
            weight = "%d / %d" % (edge["weight"], reverse["weight"])
        else:
            name = '<span class="up">%s &rarr; %s</span>' % (
                html.escape(edge["from"]),
                html.escape(edge["to"]),
            )
            weight = str(edge["weight"])
        reading = readings.get((edge["from"], edge["to"]), "")
        if not reading:
            reading = readings.get((edge["to"], edge["from"]), "")
        lines.append(
            '<tr><td class="name">%s</td><td class="num">%s</td><td>%s</td><td>%s</td></tr>'
            % (name, weight, _type_chips(edge["types"][:3]), reading)
        )
    return "\n".join(lines)


def _atlas_reading_order(prose):
    lines = []
    for step in prose.get("reading_order", []):
        if not isinstance(step, dict):
            continue
        names = step.get("types", [])
        chips = _type_chips([name for name in names if isinstance(name, str)]) if isinstance(names, list) else ""
        text = step.get("text", "")
        body = (chips + ". " if chips else "") + (text if isinstance(text, str) else "")
        lines.append("  <li><strong>%s</strong> %s</li>" % (step.get("title", ""), body))
    return "\n".join(lines)


def _atlas_history(history):
    """Return the atlas co-change tables, or a one-line notice when empty."""
    if not history or not history.get("commits"):
        return '<p class="layer">No co-change history in the window.</p>'
    lines = [
        "<h3>Module pairs that change together</h3>",
        '<div class="wide"><table>',
        '<tr><th>Modules</th><th class="num">Both</th><th class="num">Either</th>'
        '<th class="num">Jaccard</th></tr>',
    ]
    for row in history.get("modulePairs", [])[:10]:
        lines.append(
            '<tr><td class="name">%s &harr; %s</td><td class="num">%d</td>'
            '<td class="num">%d</td><td class="num">%.2f</td></tr>'
            % (html.escape(row["a"]), html.escape(row["b"]), row["both"], row["either"],
               row["jaccard"])
        )
    lines.extend(
        [
            "</table></div>",
            "<h3>Files that change together across modules</h3>",
            '<div class="wide"><table>',
            '<tr><th>Files</th><th>Modules</th><th class="num">Commits</th></tr>',
        ]
    )
    for row in history.get("filePairs", [])[:10]:
        lines.append(
            '<tr><td><span class="id">%s</span> &harr; <span class="id">%s</span></td>'
            '<td>%s, %s</td><td class="num">%d</td></tr>'
            % (html.escape(row["a"]), html.escape(row["b"]), html.escape(row["moduleA"]),
               html.escape(row["moduleB"]), row["count"])
        )
    lines.extend(
        [
            "</table></div>",
            "<h3>Hotspots (file commits times fan-in)</h3>",
            '<div class="wide"><table>',
            '<tr><th>Type</th><th>Module</th><th class="num">File commits</th>'
            '<th class="num">Fan-in</th><th class="num">Hotspot</th></tr>',
        ]
    )
    for row in history.get("hotspots", [])[:10]:
        lines.append(
            '<tr><td class="name">%s</td><td>%s</td><td class="num">%d</td>'
            '<td class="num">%d</td><td class="num">%d</td></tr>'
            % (html.escape(row["name"]), html.escape(row["module"]), row["fileCommits"],
               row["fanIn"], row["hotspot"])
        )
    lines.append("</table></div>")
    return "\n".join(lines)


def _atlas_sizes(sizes):
    """Return the atlas size tables, or a one-line notice when there is no size data."""
    if not sizes or not sizes.get("types"):
        return '<p class="layer">No size data.</p>'
    lines = [
        "<h3>Largest files</h3>",
        '<div class="wide"><table>',
        '<tr><th>File</th><th>Module</th><th class="num">Lines</th>'
        '<th class="num">Types</th><th class="num">Growth</th></tr>',
    ]
    for row in sizes["files"][:10]:
        net = row.get("netLinesAdded")
        lines.append(
            '<tr><td><span class="id">%s</span></td><td>%s</td><td class="num">%s</td>'
            '<td class="num">%d</td><td class="num">%s</td></tr>'
            % (
                html.escape(row["file"]),
                html.escape(row["module"]),
                _thousands(row["lines"]),
                row["declaredTypes"],
                ("%+d" % net) if net is not None else "&ndash;",
            )
        )
    lines.extend(
        [
            "</table></div>",
            "<h3>Largest types, partials merged</h3>",
            '<div class="wide"><table>',
            '<tr><th>Type</th><th>Module</th><th class="num">Lines</th>'
            '<th class="num">Files</th><th class="num">Methods</th>'
            '<th class="num">&ge;%d lines</th>'
            '<th class="num">Static state<br>set + collection</th>'
            "<th>Tier</th></tr>" % LONG_METHOD_LINES,
        ]
    )
    for row in sizes["types"][:10]:
        lines.append(
            '<tr><td class="name">%s</td><td>%s</td><td class="num">%s</td>'
            '<td class="num">%d</td><td class="num">%d</td><td class="num">%d</td>'
            '<td class="num">%d + %d</td><td>%s</td></tr>'
            % (
                html.escape(row["name"]),
                html.escape(row["module"]),
                _thousands(row["lines"]),
                len(row["files"]),
                row["methods"],
                row["longMethods"],
                row["mutableStatics"],
                row.get("readonlyCollectionStatics", 0),
                html.escape(row["tier"]),
            )
        )
    lines.extend(
        [
            "</table></div>",
            "<h3>What a split would start with</h3>",
            '<div class="wide"><table>',
            '<tr><th>Type</th><th>Tier</th><th>Candidates</th></tr>',
        ]
    )
    ranked = [row for row in sizes["types"][:10] if row["recommendations"]]
    if not ranked:
        lines.append('<tr><td colspan="3">No rule fired.</td></tr>')
    for row in ranked:
        items = "".join(
            "<li><code>%s</code> %s</li>"
            % (html.escape(item["rule"]), html.escape(item["text"]))
            for item in row["recommendations"]
        )
        lines.append(
            '<tr><td class="name">%s</td><td>%s</td><td><ul>%s</ul></td></tr>'
            % (html.escape(row["name"]), html.escape(row["tier"]), items)
        )
    lines.append("</table></div>")
    return "\n".join(lines)


def render_atlas_html(model, prose, svg_text=None, forbidden=None, history=None, sizes=None):
    """Return the Parsek Atlas page: prose from `prose`, everything else generated."""
    page = prose.get("page", {})
    replacements = {
        "@@EYEBROW@@": _atlas_eyebrow(),
        "@@LEDE@@": page.get("lede", ""),
        "@@TILES@@": _atlas_tiles(model),
        "@@MAP_NOTE@@": page.get("map_note", ""),
        "@@MAP@@": _svg_inline(svg_text),
        "@@MAP_READING@@": page.get("map_reading", ""),
        "@@DIRECTORY_NOTE@@": page.get("directory_note", ""),
        "@@DIRECTORY@@": _atlas_directory(model, prose),
        "@@VOCABULARY_NOTE@@": page.get("vocabulary_note", ""),
        "@@VOCABULARY@@": _atlas_vocabulary(model, prose),
        "@@KNOT_INTRO@@": _atlas_knot_intro(model, prose),
        "@@KNOT_CUTS_NOTE@@": page.get("knot_cuts_note", ""),
        "@@KNOT_CUTS@@": _atlas_knot_cuts(model),
        "@@KNOT_CALLOUT@@": page.get("knot_callout", ""),
        "@@LAYERING_NOTE@@": page.get("layering_note", ""),
        "@@UPWARD@@": _atlas_upward_rows(model, prose, forbidden),
        "@@HISTORY_READING@@": page.get("history_reading", ""),
        "@@HISTORY_BODY@@": _atlas_history(history),
        "@@SIZE_NOTE@@": page.get("size_note", ""),
        "@@SIZE_BODY@@": _atlas_sizes(sizes),
        "@@READING_NOTE@@": page.get("reading_note", ""),
        "@@READING@@": _atlas_reading_order(prose),
        "@@VIEWS_NOTE@@": page.get("views_note", ""),
        "@@FOOT@@": page.get("approximation_note", ""),
    }
    text = ATLAS_TEMPLATE
    for marker, value in replacements.items():
        text = text.replace(marker, value)
    return text


ATLAS_TEMPLATE = """<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Parsek Atlas</title>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Barlow+Condensed:wght@500;600;700&family=Source+Sans+3:ital,wght@0,400;0,600;1,400&family=JetBrains+Mono:wght@400;500&display=swap">
<style>
:root {
  --bg: #f4f6f8; --panel: #ffffff; --ink: #1b2430; --ink-2: #4d5a68; --ink-3: #7b8794;
  --rule: #d6dde5; --accent: #2f6fb3; --accent-soft: #e3edf8; --red: #c0392b; --red-soft: #f9e4e1;
  --amber: #b8730a; --amber-soft: #fbefd9; --mono-bg: #eef2f6;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #0f151c; --panel: #161e27; --ink: #e6ebf1; --ink-2: #aab5c1; --ink-3: #7d8994;
    --rule: #2a3542; --accent: #6ea4e0; --accent-soft: #1d2c3f; --red: #e06b5e; --red-soft: #3a1f1c;
    --amber: #e0a23a; --amber-soft: #3a2c14; --mono-bg: #1d2733;
  }
}
:root[data-theme="dark"] {
  --bg: #0f151c; --panel: #161e27; --ink: #e6ebf1; --ink-2: #aab5c1; --ink-3: #7d8994;
  --rule: #2a3542; --accent: #6ea4e0; --accent-soft: #1d2c3f; --red: #e06b5e; --red-soft: #3a1f1c;
  --amber: #e0a23a; --amber-soft: #3a2c14; --mono-bg: #1d2733;
}
* { box-sizing: border-box; }
body { background: var(--bg); color: var(--ink); font-family: "Source Sans 3", "Segoe UI", system-ui, sans-serif;
  font-size: 17px; line-height: 1.55; margin: 0; }
main { max-width: 1120px; margin: 0 auto; padding: 40px 28px 80px; }
h1, h2, h3 { font-family: "Barlow Condensed", "Arial Narrow", sans-serif; text-wrap: balance; margin: 0; line-height: 1.05; }
h1 { font-size: 64px; font-weight: 700; letter-spacing: -0.01em; }
h2 { font-size: 34px; font-weight: 600; margin-top: 64px; padding-top: 18px; border-top: 2px solid var(--ink); }
h3 { font-size: 22px; font-weight: 600; margin-top: 28px; color: var(--ink); }
p, li { max-width: 70ch; }
p { margin: 14px 0; }
.eyebrow { font-family: "Barlow Condensed", sans-serif; font-weight: 600; font-size: 14px; letter-spacing: 0.12em;
  text-transform: uppercase; color: var(--accent); }
.lede { font-size: 20px; color: var(--ink-2); max-width: 64ch; }
code, .id { font-family: "JetBrains Mono", Consolas, monospace; font-size: 0.86em; background: var(--mono-bg);
  padding: 1px 5px; border-radius: 3px; }
.tiles { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; margin: 28px 0 8px; }
.tile { background: var(--panel); border: 1px solid var(--rule); padding: 14px 16px 12px; }
.tile .n { font-family: "Barlow Condensed", sans-serif; font-size: 44px; font-weight: 700; line-height: 1;
  font-variant-numeric: tabular-nums; }
.tile .l { color: var(--ink-2); font-size: 14px; margin-top: 6px; }
.panel { background: #ffffff; border: 1px solid var(--rule); padding: 14px; overflow-x: auto; margin: 18px 0; }
.panel svg { display: block; min-width: 1100px; height: auto; }
.wide { overflow-x: auto; margin: 18px 0; }
table { border-collapse: collapse; width: 100%; font-size: 15px; }
th, td { text-align: left; vertical-align: top; padding: 8px 10px; border-bottom: 1px solid var(--rule); }
th { font-family: "Barlow Condensed", sans-serif; font-size: 14px; letter-spacing: 0.08em; text-transform: uppercase;
  color: var(--ink-2); font-weight: 600; white-space: nowrap; }
td.num { font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; }
th.num { text-align: right; }
td.name { font-weight: 600; white-space: nowrap; }
td .id { white-space: nowrap; }
.layer { font-family: "Barlow Condensed", sans-serif; font-size: 13px; letter-spacing: 0.08em; text-transform: uppercase;
  color: var(--ink-3); }
tr.band td { background: var(--accent-soft); font-family: "Barlow Condensed", sans-serif; font-size: 15px;
  letter-spacing: 0.06em; text-transform: uppercase; color: var(--accent); font-weight: 600; padding: 6px 10px; }
.up { color: var(--red); font-weight: 600; }
.knot { color: var(--amber); font-weight: 600; }
.callout { border-left: 4px solid var(--accent); background: var(--panel); padding: 12px 18px; margin: 20px 0; max-width: 78ch; }
.callout.red { border-color: var(--red); }
ol.steps { counter-reset: s; list-style: none; padding: 0; }
ol.steps li { counter-increment: s; position: relative; padding-left: 52px; margin: 14px 0; }
ol.steps li::before { content: counter(s); position: absolute; left: 0; top: 0; width: 36px; height: 36px;
  border: 2px solid var(--accent); color: var(--accent); font-family: "Barlow Condensed", sans-serif;
  font-weight: 700; font-size: 20px; display: flex; align-items: center; justify-content: center; }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 8px 18px; max-width: 80ch; }
dt { font-family: "JetBrains Mono", monospace; font-size: 14px; white-space: nowrap; padding-top: 2px; }
dd { margin: 0; color: var(--ink-2); }
.foot { color: var(--ink-3); font-size: 14px; margin-top: 56px; border-top: 1px solid var(--rule); padding-top: 14px; }
@media (max-width: 760px) { .tiles { grid-template-columns: repeat(2, 1fr); } h1 { font-size: 46px; } dl { grid-template-columns: 1fr; } }
</style>
</head><body><main>
<div class="eyebrow">@@EYEBROW@@</div>
<h1>Parsek Atlas</h1>
<p class="lede">@@LEDE@@</p>

<div class="tiles">
@@TILES@@
</div>

<h2>The map</h2>
<p>@@MAP_NOTE@@</p>
<div class="panel">@@MAP@@</div>
<p>@@MAP_READING@@</p>

<h2>Module directory</h2>
<p>@@DIRECTORY_NOTE@@</p>
<div class="wide"><table>
<tr><th>Module</th><th>What it is</th><th class="num">Files</th><th class="num">Types</th><th class="num">I</th><th>Hubs</th></tr>
@@DIRECTORY@@
</table></div>

<h2>The vocabulary</h2>
<p>@@VOCABULARY_NOTE@@</p>
<dl>
@@VOCABULARY@@
</dl>

<h2>The knot</h2>
@@KNOT_INTRO@@
<p>@@KNOT_CUTS_NOTE@@</p>
<div class="wide"><table>
<tr><th>Make this a sink</th><th class="num">Knot before</th><th class="num">after</th><th>Its references inside the knot</th></tr>
@@KNOT_CUTS@@
</table></div>
<div class="callout">@@KNOT_CALLOUT@@</div>

<h2>Where the layering breaks</h2>
<p>@@LAYERING_NOTE@@</p>
<div class="wide"><table>
<tr><th>Upward edge</th><th class="num">Refs</th><th>Through</th><th>Reading</th></tr>
@@UPWARD@@
</table></div>

<h2>What changes together</h2>
<p>@@HISTORY_READING@@</p>
@@HISTORY_BODY@@

<h2>Largest files and types</h2>
<p>@@SIZE_NOTE@@</p>
@@SIZE_BODY@@

<h2>Reading order</h2>
<p>@@READING_NOTE@@</p>
<ol class="steps">
@@READING@@
</ol>

<h2>The generated views</h2>
<p>@@VIEWS_NOTE@@</p>
<dl>
  <dt>explore.html</dt><dd>The module graph as an interactive page: click a module to see its neighbours and the exact types behind each edge.</dd>
  <dt>matrix.html</dt><dd>The dependency structure matrix. Both axes sorted by stability, so everything above the diagonal is an upward edge.</dd>
  <dt>ladder.html</dt><dd>Every type placed by module and abstraction level, with the knot split into sub-rows and its cut sinks marked.</dd>
  <dt>modules.svg</dt><dd>The static map shown above.</dd>
  <dt>core-placement.md</dt><dd>The evidence table behind where each former root file went.</dd>
  <dt>sizes.json</dt><dd>The size view: the largest files and types, their long methods, mutable statics and pure static pools, with the rules that fired.</dd>
  <dt>archview.py --check</dt><dd>The text report: metrics, upward edges, couplings, hubs, roles, level profile, knots, forbidden edges, sizes.</dd>
</dl>

<p class="foot">@@FOOT@@</p>
</main>
</body></html>
"""


# ---------------------------------------------------------------------------
# change history
# ---------------------------------------------------------------------------


def default_since():
    """Return the ISO date 18 months before today."""
    today = datetime.date.today()
    month = today.month - 18
    year = today.year
    while month <= 0:
        month += 12
        year -= 1
    return today.replace(year=year, month=month, day=1).isoformat()


def history_git_args(since):
    """Return the single git log invocation the history is built from."""
    return [
        "git",
        "log",
        "--no-merges",
        "--since=%s" % since,
        "--date=short",
        "--pretty=format:COMMIT%x09%H%x09%ad",
        "--name-only",
        "--",
        "Source/Parsek",
    ]


def git_log_lines(repo_root, since=None):
    """Return raw `git log --name-only` text for Source/Parsek, or "" on failure."""
    if since is None:
        since = default_since()
    try:
        completed = subprocess.run(
            history_git_args(since),
            cwd=str(repo_root),
            capture_output=True,
            text=True,
            check=False,
        )
    except Exception as exc:
        print("WARN archview: git log unavailable: %s" % exc, file=sys.stderr)
        return ""
    if completed.returncode != 0:
        print(
            "WARN archview: git log failed (exit %d): %s"
            % (completed.returncode, completed.stderr.strip()),
            file=sys.stderr,
        )
        return ""
    return completed.stdout


def growth_git_args(since):
    """Return the single git log invocation the per-file growth is built from."""
    return [
        "git",
        "log",
        "--no-merges",
        "--since=%s" % since,
        "--numstat",
        "--pretty=format:COMMIT%x09%H",
        "--",
        "Source/Parsek",
    ]


def git_numstat_lines(repo_root, since=None):
    """Return raw `git log --numstat` text for Source/Parsek, or "" on failure."""
    if since is None:
        since = default_since()
    try:
        completed = subprocess.run(
            growth_git_args(since),
            cwd=str(repo_root),
            capture_output=True,
            text=True,
            check=False,
        )
    except Exception as exc:
        print("WARN archview: git log --numstat unavailable: %s" % exc, file=sys.stderr)
        return ""
    if completed.returncode != 0:
        print(
            "WARN archview: git log --numstat failed (exit %d): %s"
            % (completed.returncode, completed.stderr.strip()),
            file=sys.stderr,
        )
        return ""
    return completed.stdout


def parse_growth(text):
    """Parse `git log --numstat` text into net added lines per file (pure).

    Returns {"files": {rel path: added - deleted}, "commits": kept, "skipped":
    sweeps}. Binary rows (`-` counts) are ignored and a commit touching more
    than HISTORY_SWEEP_LIMIT source files is skipped, the same sweep rule the
    co-change history uses, so a bulk rename does not read as growth. An empty
    string (git missing or the window empty) returns empty tables.
    """
    commits = []
    current = None
    for raw_line in text.split("\n"):
        line = raw_line.rstrip("\r")
        if not line.strip():
            continue
        if line.startswith("COMMIT\t"):
            current = []
            commits.append(current)
            continue
        if current is None:
            continue
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        added, deleted, path = parts[0], parts[1], parts[2]
        if added == "-" or deleted == "-":
            continue
        if "=>" in path:
            # A rename row (`a => b`, `dir/{a => b}/f.cs`) names no single file;
            # its counts are the move, not growth.
            continue
        rel_path = _history_path(path)
        if rel_path is None:
            continue
        try:
            current.append((rel_path, int(added) - int(deleted)))
        except ValueError:
            continue
    totals = defaultdict(int)
    kept = 0
    skipped = 0
    for rows in commits:
        if not rows:
            continue
        if len({rel_path for rel_path, _net in rows} ) > HISTORY_SWEEP_LIMIT:
            skipped += 1
            continue
        kept += 1
        for rel_path, net in rows:
            totals[rel_path] += net
    return {"files": dict(totals), "commits": kept, "skipped": skipped}


def _history_path(line):
    """Return the build_model-style rel path for a git log file line, or None."""
    if line.startswith('"') and line.endswith('"'):
        line = line[1:-1].replace('\\"', '"').replace("\\\\", "\\")
    line = line.replace("\\", "/")
    if not line.endswith(".cs"):
        return None
    prefix = "Source/Parsek/"
    if not line.startswith(prefix):
        return None
    rel_path = line[len(prefix) :]
    if any(part in SKIP_DIR_NAMES for part in rel_path.split("/")[:-1]):
        return None
    return rel_path


def parse_history(text):
    """Parse `git log --name-only` text into commits (pure).

    Returns {"commits": [{"sha", "date", "files"}], "skipped": N,
    "largest": M}. Only .cs files under Source/Parsek/ count, bin/obj/
    Properties directories are skipped the way build_model skips them, and a
    commit touching more than HISTORY_SWEEP_LIMIT source files is a sweep: it
    is skipped, and `largest` is the biggest skipped sweep.
    """
    commits = []
    current = None
    for raw_line in text.split("\n"):
        line = raw_line.rstrip("\r").strip()
        if not line:
            continue
        if line.startswith("COMMIT\t"):
            parts = line.split("\t")
            current = None
            if len(parts) >= 3:
                current = {"sha": parts[1], "date": parts[2], "files": []}
                commits.append(current)
            continue
        if current is None:
            continue
        rel_path = _history_path(line)
        if rel_path is not None:
            current["files"].append(rel_path)
    kept = []
    skipped = 0
    largest = 0
    for commit in commits:
        files = sorted(set(commit["files"]))
        if not files:
            continue
        if len(files) > HISTORY_SWEEP_LIMIT:
            skipped += 1
            largest = max(largest, len(files))
            continue
        kept.append({"sha": commit["sha"], "date": commit["date"], "files": files})
    return {"commits": kept, "skipped": skipped, "largest": largest}


def history_metrics(commits, model, rules):
    """Return the co-change tables for the scanned commits (pure).

    `commits` is parse_history's list, `model` supplies the file-to-module map,
    the per-type file lists and the type fan-ins, and `rules` is the fallback
    assigner for files the model does not know. Tooling modules are excluded
    everywhere. Tables: files (commits, lastTouched, churnRank), hotspots (top
    30 by fileCommits * fanIn, where fileCommits is the union of the commits
    touching any file the type is declared in), modules (commits touching it)
    and modulePairs (every unordered production pair with both/either/jaccard),
    and filePairs (top 40 cross-module pairs with count >= 5).
    """
    tooling = {module["name"] for module in model["modules"] if module["tooling"]}
    module_of_file = {}
    for rel_path, module in model.get("fileModules", {}).items():
        if module not in tooling:
            module_of_file[rel_path] = module
    for commit in commits:
        for rel_path in commit["files"]:
            if rel_path in module_of_file:
                continue
            module = assign_module(rel_path, rules)
            if module is not None and module not in tooling:
                module_of_file[rel_path] = module

    file_commits = defaultdict(int)
    last_touched = {}
    for commit in commits:
        for rel_path in set(commit["files"]):
            if rel_path not in module_of_file:
                continue
            file_commits[rel_path] += 1
            date = commit["date"]
            if rel_path not in last_touched or date > last_touched[rel_path]:
                last_touched[rel_path] = date

    module_commits = defaultdict(set)
    file_commit_index = defaultdict(set)
    file_pair_counts = Counter()
    for index, commit in enumerate(commits):
        files = sorted({rel_path for rel_path in commit["files"] if rel_path in module_of_file})
        for rel_path in files:
            file_commit_index[rel_path].add(index)
        for module in {module_of_file[rel_path] for rel_path in files}:
            module_commits[module].add(index)
        for i in range(len(files)):
            for j in range(i + 1, len(files)):
                a, b = files[i], files[j]
                if module_of_file[a] != module_of_file[b]:
                    file_pair_counts[(a, b)] += 1

    files = []
    for rel_path in sorted(module_of_file):
        files.append(
            {
                "file": rel_path,
                "module": module_of_file[rel_path],
                "commits": file_commits.get(rel_path, 0),
                "lastTouched": last_touched.get(rel_path),
            }
        )
    files.sort(key=lambda row: (-row["commits"], row["file"]))
    for rank, row in enumerate(files, 1):
        row["churnRank"] = rank

    hotspots = []
    for entry in model.get("types", []):
        # A partial type counts the UNION of the commits that touched any of
        # its files: summing per-file counts would count a commit twice when it
        # edited two parts of the same type.
        type_files = [
            rel_path
            for rel_path in entry.get("files", [entry["file"]])
            if rel_path in module_of_file
        ]
        if not type_files:
            continue
        touching = set()
        for rel_path in type_files:
            touching |= file_commit_index.get(rel_path, set())
        commits_for_type = len(touching)
        hotspots.append(
            {
                "name": entry["name"],
                "module": entry["module"],
                "file": entry["file"],
                "files": type_files,
                "fileCommits": commits_for_type,
                "fanIn": entry["fanIn"],
                "hotspot": commits_for_type * entry["fanIn"],
            }
        )
    hotspots.sort(key=lambda row: (-row["hotspot"], row["name"]))
    hotspots = hotspots[:30]

    module_names = sorted(module_commits)
    modules = [{"name": name, "commits": len(module_commits[name])} for name in module_names]
    module_pairs = []
    for i in range(len(module_names)):
        for j in range(i + 1, len(module_names)):
            a, b = module_names[i], module_names[j]
            both = len(module_commits[a] & module_commits[b])
            either = len(module_commits[a] | module_commits[b])
            module_pairs.append(
                {
                    "a": a,
                    "b": b,
                    "both": both,
                    "either": either,
                    "jaccard": round(both / either, 4) if either else 0.0,
                }
            )
    module_pairs.sort(key=lambda row: (-row["jaccard"], -row["both"], row["a"], row["b"]))

    file_pairs = []
    for (a, b), count in file_pair_counts.items():
        if count < 5:
            continue
        file_pairs.append(
            {
                "a": a,
                "b": b,
                "moduleA": module_of_file[a],
                "moduleB": module_of_file[b],
                "count": count,
            }
        )
    file_pairs.sort(key=lambda row: (-row["count"], row["a"], row["b"]))
    file_pairs = file_pairs[:40]

    return {
        "files": files,
        "hotspots": hotspots,
        "modules": modules,
        "modulePairs": module_pairs,
        "filePairs": file_pairs,
    }


def write_history_json(payload, out_path):
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


# ---------------------------------------------------------------------------
# size view: rules, tiers and the report
# ---------------------------------------------------------------------------


def load_size_settings(path):
    """Return the [size] table from a modules.toml file, with defaults filled in."""
    try:
        with open(path, "rb") as handle:
            data = tomllib.load(handle)
    except Exception:
        data = {}
    table = data.get("size", {}) if isinstance(data, dict) else {}
    coupled = table.get("runtimeCoupled")
    if not isinstance(coupled, list) or not all(isinstance(name, str) for name in coupled):
        coupled = list(DEFAULT_RUNTIME_COUPLED)
    return {"runtimeCoupled": sorted(set(coupled))}


def _method_citation(entry):
    return "%s %d lines at %s:%d" % (
        entry["name"],
        entry["lines"],
        entry["file"],
        entry["startLine"],
    )


def size_recommendations(entry, runtime_coupled=()):
    """Return the recommendation rows for one type's size facts (pure).

    Rules are additive and ordered cheapest and safest first (S1) to riskiest
    (S5), with S6 and S7 as notes that change how a slice is run rather than
    what it is. Every row cites the numbers that fired it. Nothing here says a
    split is safe: the rows are candidates and evidence, the same contract the
    knot cut table carries.
    """
    rows = []
    if entry["longMethods"] >= 1:
        longest = [
            _method_citation(method)
            for method in entry["topMethods"]
            if method["lines"] >= LONG_METHOD_LINES
        ][:3]
        text = (
            "%d method(s) at or above %d lines (longest: %s): a same-file extract-method"
            " pass is the cheapest slice."
            % (entry["longMethods"], LONG_METHOD_LINES, "; ".join(longest) or "not delimited")
        )
        if entry["coroutines"]:
            text += " %d IEnumerator method(s) stay whole (guidelines item 6)." % entry["coroutines"]
        rows.append({"rule": "S1", "text": text})
    if (
        entry["pureStaticMethods"] >= PURE_POOL_METHODS
        or entry["pureStaticLines"] >= PURE_POOL_LINES
    ):
        rows.append(
            {
                "rule": "S2",
                "text": "%d static method(s), %d lines, name no live KSP type and no static"
                " state of this type: candidate internal static helper with unit tests. No"
                " pre-existing access modifier changes (guidelines items 7 and 13)."
                % (entry["pureStaticMethods"], entry["pureStaticLines"]),
            }
        )
    # S3 keys on the LARGEST SINGLE FILE, not the total: a type already spread
    # over six 1,000-line partial files has done exactly what this rule asks
    # for, and telling it to split again says nothing.
    largest_part = entry["files"][0] if entry["files"] else None
    if largest_part and largest_part["lines"] >= GIANT_TYPE_LINES:
        rows.append(
            {
                "rule": "S3",
                "text": "%s holds %s of %s lines across %d file(s): split that file by"
                " responsibility into further partial-class files, which moves no call site."
                % (
                    largest_part["file"],
                    _thousands(largest_part["lines"]),
                    _thousands(entry["lines"]),
                    len(entry["files"]),
                ),
            }
        )
    static_state = entry["mutableStatics"] + entry.get("readonlyCollectionStatics", 0)
    if static_state >= MUTABLE_STATIC_FLOOR:
        rows.append(
            {
                "rule": "S4",
                "text": "%d field(s) of static state (%d reassignable, %d readonly collections"
                " whose contents change): build the static mutable state map before moving"
                " anything, and keep this type as a compatibility facade in the first slice."
                % (static_state, entry["mutableStatics"], entry.get("readonlyCollectionStatics", 0)),
            }
        )
    top_level = entry.get("topLevelTypesInFile", 1)
    siblings = max(0, top_level - 1)
    nested = entry["nestedTypes"]
    if nested >= NESTED_TYPE_FLOOR or top_level >= SIBLING_TYPE_FLOOR:
        parts = []
        if nested:
            parts.append(
                "%d nested type(s) move to a partial file of %s itself (it is %spartial today)"
                % (nested, entry["name"], "" if entry.get("partial") else "not ")
            )
        if siblings:
            parts.append("%d sibling top-level type(s) in %s move to their own files"
                         % (siblings, entry["file"]))
        rows.append({"rule": "S5", "text": "; ".join(parts) + "."})
    if entry["module"] in set(runtime_coupled):
        rows.append(
            {
                "rule": "S6",
                "text": "module %s is runtime-coupled: needs in-game validation, and log text"
                " and rate-limit keys must stay byte-identical." % entry["module"],
            }
        )
    rank = entry.get("hotspotRank")
    if rank and rank <= HOTSPOT_PRIORITY_RANK:
        rows.append(
            {
                "rule": "S7",
                "text": "hotspot rank %d (file commits %d times fan-in %d): raises priority."
                % (rank, entry.get("fileCommits", 0), entry.get("fanIn", 0)),
            }
        )
    return rows


def size_tier(entry):
    """Return (tier, score) for one type's size facts (pure).

    Four points, one axis each, and a tier from their sum: size (2 at or above
    GIANT_TYPE_LINES, 1 at or above LARGE_FILE_LINES), long methods (1 at 3 or
    more, 2 at 8 or more), static state (1 at MUTABLE_STATIC_FLOOR or more,
    counting reassignable statics and readonly collections together) and
    hotspot rank (1 inside the top HOTSPOT_PRIORITY_RANK). Tier 1 at 4 or more,
    Tier 2 at 2 or 3, watch below that.

    Size here is the type's TOTAL lines, not its largest file: a 30,000-line
    type is a big type however many files hold it. S3, which asks for a split
    of one file, reads the largest file instead.
    """
    score = 0
    if entry["lines"] >= GIANT_TYPE_LINES:
        score += 2
    elif entry["lines"] >= LARGE_FILE_LINES:
        score += 1
    if entry["longMethods"] >= 8:
        score += 2
    elif entry["longMethods"] >= 3:
        score += 1
    if entry["mutableStatics"] + entry.get("readonlyCollectionStatics", 0) >= MUTABLE_STATIC_FLOOR:
        score += 1
    rank = entry.get("hotspotRank")
    if rank and rank <= HOTSPOT_PRIORITY_RANK:
        score += 1
    if score >= 4:
        return "Tier 1", score
    if score >= 2:
        return "Tier 2", score
    return "watch", score


def size_report(model, history=None, growth=None, settings=None):
    """Return the sizes.json payload: measurement joined with history, ruled and tiered.

    Pure: `model["sizes"]` is what build_model measured, `history` the
    history.json payload (churn ranks and hotspots), `growth` what parse_growth
    returned, and `settings` the [size] table. Missing history or growth simply
    leaves those columns null.
    """
    sizes = model.get("sizes", {})
    settings = settings or {"runtimeCoupled": list(DEFAULT_RUNTIME_COUPLED)}
    runtime_coupled = settings.get("runtimeCoupled", [])
    growth = growth or {"files": {}, "commits": 0, "skipped": 0}
    history = history or {}
    churn = {row["file"]: row for row in history.get("files", [])}
    hotspot_rank = {}
    hotspot_row = {}
    for rank, row in enumerate(history.get("hotspots", []), 1):
        hotspot_rank.setdefault(row["name"], rank)
        hotspot_row.setdefault(row["name"], row)
    partners = defaultdict(list)
    for row in history.get("filePairs", []):
        partners[row["a"]].append((row["b"], row["count"]))
        partners[row["b"]].append((row["a"], row["count"]))
    type_meta = {entry["name"]: entry for entry in model.get("types", [])}

    files = []
    for row in sizes.get("files", []):
        churn_row = churn.get(row["file"], {})
        files.append(
            {
                "file": row["file"],
                "module": row["module"],
                "lines": row["lines"],
                "declaredTypes": row["declaredTypes"],
                "topLevelTypes": row["topLevelTypes"],
                "types": row["types"],
                "netLinesAdded": growth["files"].get(row["file"]),
                "commits": churn_row.get("commits"),
                "churnRank": churn_row.get("churnRank"),
            }
        )

    types = []
    for row in sizes.get("types", []):
        entry = dict(row)
        entry.pop("mutableStaticNames", None)
        entry.pop("readonlyCollectionStaticNames", None)
        meta = type_meta.get(entry["name"], {})
        entry["partial"] = "partial" in meta.get("modifiers", [])
        entry["role"] = meta.get("role")
        entry["level"] = meta.get("level")
        entry["knot"] = meta.get("knot")
        entry["fanIn"] = meta.get("fanIn", 0)
        churn_row = churn.get(entry["file"], {})
        entry["commits"] = churn_row.get("commits")
        entry["churnRank"] = churn_row.get("churnRank")
        if growth["files"]:
            entry["netLinesAdded"] = sum(
                growth["files"].get(part["file"], 0) for part in entry["files"]
            )
        else:
            entry["netLinesAdded"] = None
        entry["hotspotRank"] = hotspot_rank.get(entry["name"])
        hot = hotspot_row.get(entry["name"], {})
        entry["hotspot"] = hot.get("hotspot")
        entry["fileCommits"] = hot.get("fileCommits", churn_row.get("commits", 0) or 0)
        entry["coChangePartners"] = [
            {"file": name, "count": count}
            for name, count in sorted(
                partners.get(entry["file"], []), key=lambda pair: (-pair[1], pair[0])
            )[:2]
        ]
        tier, score = size_tier(entry)
        entry["tier"] = tier
        entry["score"] = score
        entry["recommendations"] = size_recommendations(entry, runtime_coupled)
        types.append(entry)

    return {
        "thresholds": {
            "largeFileLines": LARGE_FILE_LINES,
            "giantTypeLines": GIANT_TYPE_LINES,
            "longMethodLines": LONG_METHOD_LINES,
            "purePoolMethods": PURE_POOL_METHODS,
            "purePoolLines": PURE_POOL_LINES,
            "mutableStaticFloor": MUTABLE_STATIC_FLOOR,
            "nestedTypeFloor": NESTED_TYPE_FLOOR,
            "siblingTypeFloor": SIBLING_TYPE_FLOOR,
            "hotspotPriorityRank": HOTSPOT_PRIORITY_RANK,
            "topN": SIZE_TOP_N,
        },
        "runtimeCoupledModules": list(runtime_coupled),
        "growth": {
            "since": history.get("since"),
            "commits": growth.get("commits", 0),
            "skippedSweeps": growth.get("skipped", 0),
            "available": bool(growth.get("files")),
        },
        "files": files,
        "types": types,
    }


def write_sizes_json(payload, out_path):
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


# ---------------------------------------------------------------------------
# checker (report-only)
# ---------------------------------------------------------------------------


def parse_edge_spec(spec):
    """Return (from, to) for a "From -> To" policy string.

    A non-string spec raises ValueError like any other malformed spec, so
    callers that guard with `except ValueError` cover both.
    """
    if not isinstance(spec, str):
        raise ValueError("bad edge spec: %r" % (spec,))
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


def not_tooling(model, edge):
    tooling = {m["name"] for m in model["modules"] if m["tooling"]}
    return edge["from"] not in tooling and edge["to"] not in tooling


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


def hub_types(model, limit=25):
    """Types with the highest file-based fan-in, name-sorted on ties."""
    return sorted(model.get("types", []), key=lambda t: (-t["fanIn"], t["name"]))[:limit]


def type_role_groups(model):
    """[(role, count, [three example names])] by count, then role order."""
    groups = defaultdict(list)
    for entry in model.get("types", []):
        groups[entry["role"]].append(entry)
    rows = []
    for role in sorted(groups, key=lambda r: (-len(groups[r]), ROLE_ORDER.index(r))):
        examples = sorted(groups[role], key=lambda t: (-t["fanIn"], t["name"]))[:3]
        rows.append((role, len(groups[role]), [t["name"] for t in examples]))
    return rows


def level_profile(model):
    """([(module, total, {level: count}, max level)], overall max level)."""
    by_module = defaultdict(list)
    for entry in model.get("types", []):
        by_module[entry["module"]].append(entry)
    production = sorted(m["name"] for m in model["modules"] if not m["tooling"])
    profile = []
    overall = 0
    for module in production:
        counts = defaultdict(int)
        for entry in by_module.get(module, []):
            counts[entry["level"]] += 1
        max_level = max(counts) if counts else 0
        overall = max(overall, max_level)
        profile.append((module, len(by_module.get(module, [])), dict(counts), max_level))
    return profile, overall


def print_size_section(sizes):
    """Print the SIZE section: largest files, largest types, then the rules that fired."""
    print()
    print(
        "SIZE (text-scan approximation; large file >= %d lines, giant >= %d, long method >= %d):"
        % (LARGE_FILE_LINES, GIANT_TYPE_LINES, LONG_METHOD_LINES)
    )
    if not sizes or not sizes.get("files"):
        print("  no size data.")
        return
    growth = sizes.get("growth", {})
    if growth.get("available"):
        print(
            "  growth column: net lines added since %s over %d commits (%d sweeps skipped)."
            % (growth.get("since"), growth.get("commits", 0), growth.get("skippedSweeps", 0))
        )
    else:
        print("  growth column: no git history (git unavailable or the window is empty).")
    print("  Top %d files by lines:" % SIZE_TOP_FILES)
    for row in sizes["files"][:SIZE_TOP_FILES]:
        net = row.get("netLinesAdded")
        print(
            "    %7s  %-52s %-13s types=%-3d growth=%s"
            % (
                _thousands(row["lines"]),
                row["file"],
                row["module"],
                row["declaredTypes"],
                ("%+d" % net) if net is not None else "n/a",
            )
        )
    print(
        "  Top %d types by lines (partials merged; mth=methods, coro=IEnumerator,"
        " statics=reassignable+readonly collections):" % SIZE_TOP_N
    )
    for row in sizes["types"][:SIZE_TOP_N]:
        print(
            "    %7s %-32s %-12s files=%-2d mth=%-4d long=%-3d coro=%-2d statics=%-6s"
            " pure=%-4d %s"
            % (
                _thousands(row["lines"]),
                row["name"][:32],
                row["module"],
                len(row["files"]),
                row["methods"],
                row["longMethods"],
                row["coroutines"],
                "%d+%d" % (row["mutableStatics"], row.get("readonlyCollectionStatics", 0)),
                row["pureStaticMethods"],
                row["tier"],
            )
        )
    skipped = sum(row["skippedMembers"] for row in sizes["types"])
    print("  Members the scan could not delimit, and so did not count: %d." % skipped)
    print("  Recommendations (candidates and evidence, never a verdict):")
    ranked = [row for row in sizes["types"][:SIZE_TOP_N] if row["recommendations"]]
    if not ranked:
        print("    none.")
    for row in ranked:
        print(
            "    [%s score=%d] %s (%s, %s lines in %d file(s))"
            % (
                row["tier"],
                row["score"],
                row["name"],
                row["module"],
                _thousands(row["lines"]),
                len(row["files"]),
            )
        )
        for recommendation in row["recommendations"]:
            # Wrapped for a terminal; the atlas renders the same text unwrapped.
            print(
                textwrap.fill(
                    recommendation["text"],
                    width=SIZE_CHECK_WIDTH,
                    initial_indent="      %s " % recommendation["rule"],
                    subsequent_indent="         ",
                    break_long_words=False,
                    break_on_hyphens=False,
                )
            )


def run_check(model, forbidden, allowed, prose=None, history=None, sizes=None):
    """Print the report-only architecture check.

    Never raises on a malformed policy spec: a bad "From -> To" string is
    reported and skipped, because the final line and the exit code are part of
    the report contract. `prose` is the atlas prose dict, `history` the
    history.json payload and `sizes` the sizes.json payload (None for callers
    that do not have them); the ATLAS and HISTORY sections report what no
    longer matches the model, and SIZE is the last section before the marker.
    """
    print()
    hidden = sorted(m["name"] for m in model["modules"] if m["tooling"])
    print("Per-module metrics (production modules, sorted by fan-in + fan-out):")
    print(format_metrics_table(model))
    if hidden:
        print("Tooling modules hidden from this table and the views: %s." % ", ".join(hidden))

    ambiguous = model.get("ambiguousTypes", [])
    if ambiguous:
        print(
            "Type names declared in more than one module, excluded from the scan (%d): %s"
            % (len(ambiguous), ", ".join(ambiguous))
        )

    print()
    upward = [e for e in model["edges"] if e["upward"] and not_tooling(model, e)]
    print("Upward edges (a more stable module depending on a less stable one):")
    if not upward:
        print("  none.")
    for edge in sorted(upward, key=lambda e: (-e["weight"], e["from"], e["to"])):
        print("  %s -> %s: weight=%d" % (edge["from"], edge["to"], edge["weight"]))

    print()
    pairs = two_way_couplings(model)
    print("Two-way couplings (production modules only):")
    if not pairs:
        print("  none.")
    for a, b, weight_ab, weight_ba in pairs:
        print("  %s <-> %s: %s->%s=%d, %s->%s=%d" % (a, b, a, b, weight_ab, b, a, weight_ba))

    print()
    print("HUB TYPES (top 25 by file-based fan-in):")
    hubs = hub_types(model)
    if not hubs:
        print("  none.")
    for entry in hubs:
        print(
            "  %-28s fan-in=%-4d role=%-10s module=%-13s level=%d"
            % (entry["name"], entry["fanIn"], entry["role"], entry["module"], entry["level"])
        )

    print()
    print("TYPE ROLES:")
    roles = type_role_groups(model)
    if not roles:
        print("  none.")
    for role, count, examples in roles:
        print("  %-10s %4d  e.g. %s" % (role, count, ", ".join(examples)))

    print()
    print("LEVEL PROFILE (production modules):")
    profile, overall = level_profile(model)
    if not profile:
        print("  none.")
    for module, total, counts, max_level in profile:
        levels_text = " ".join("L%d=%d" % (level, counts[level]) for level in sorted(counts))
        print("  %-13s %4d types  %-42s max level=%d" % (module, total, levels_text, max_level))
    if profile:
        print("  Overall max level: %d" % overall)

    knot_list = model.get("knots", [])
    print()
    print("KNOTS (strongly connected components of size 2 or more):")
    if not knot_list:
        print("  none.")
    for knot in knot_list[:10]:
        breakdown = ", ".join(
            "%s=%d" % (module, count)
            for module, count in sorted(knot["modules"].items(), key=lambda item: (-item[1], item[0]))
        )
        print("  size=%-4d %s" % (knot["size"], breakdown))
    if len(knot_list) > 10:
        print("  +%d more knots (total %d)." % (len(knot_list) - 10, len(knot_list)))
    if knot_list:
        type_graph = {entry["name"]: entry["referencesTo"] for entry in model.get("types", [])}
        largest = knot_list[0]
        print("Largest knot hubs (top 15 by in-knot fan-in):")
        hubs = knot_hubs(largest["members"], type_graph)[:15]
        if not hubs:
            print("  none.")
        for hub in hubs:
            refs = ", ".join(hub["references"][:8])
            if len(hub["references"]) > 8:
                refs += " +%d" % (len(hub["references"]) - 8)
            print("  %-28s in-knot fan-in=%-4d -> %s" % (hub["name"], hub["fanIn"], refs or "none"))
        print("Greedy sink cuts (largest knot):")
        if not largest["cuts"]:
            print("  none.")
        for cut in largest["cuts"]:
            print(
                "  %d -> cut %s -> %d (drops: %s)"
                % (
                    cut["sizeBefore"],
                    cut["sink"],
                    cut["sizeAfter"],
                    ", ".join(cut["droppedReferences"]),
                )
            )

    findings = prose_findings(model, prose or {})
    print()
    print("ATLAS (prose against the live model):")

    def atlas_list(title, items):
        if items:
            print("  %s (%d): %s" % (title, len(items), ", ".join(items)))
        else:
            print("  %s: none." % title)

    atlas_list("production modules without a summary", findings["missingSummaries"])
    atlas_list("glossary entries naming a missing type", findings["missingGlossary"])
    atlas_list("glossary entries outside the live top 18", findings["staleGlossary"])
    atlas_list("upward readings whose edge no longer exists", findings["staleReadings"])
    atlas_list("reading-order types not in the model", findings["missingReadingOrder"])

    print()
    print("HISTORY (co-change over Source/Parsek):")
    if not history or not history.get("commits"):
        print("  no co-change history (git log unavailable or the window is empty).")
    else:
        print(
            "  window: since %s, %d commits (%d sweeps skipped, largest %d files)"
            % (
                history["since"],
                history["commits"],
                history["skippedSweeps"],
                history["largestSweep"],
            )
        )
        print("  Top hotspots (type, module, file commits, fan-in, hotspot):")
        if not history.get("hotspots"):
            print("    none.")
        for row in history["hotspots"][:15]:
            print(
                "    %-26s %-12s file commits=%-4d fan-in=%-4d hotspot=%d"
                % (row["name"], row["module"], row["fileCommits"], row["fanIn"], row["hotspot"])
            )
        print("  Top module pairs by jaccard:")
        if not history.get("modulePairs"):
            print("    none.")
        for row in history["modulePairs"][:10]:
            print(
                "    %s <-> %s: both=%d, either=%d, jaccard=%.2f"
                % (row["a"], row["b"], row["both"], row["either"], row["jaccard"])
            )
        print("  Top cross-module file pairs:")
        if not history.get("filePairs"):
            print("    none.")
        for row in history["filePairs"][:15]:
            print(
                "    %s <-> %s: count=%d (%s, %s)"
                % (row["a"], row["b"], row["count"], row["moduleA"], row["moduleB"])
            )
        pair_lookup = {
            (row["a"], row["b"]): row["both"] for row in history.get("modulePairs", [])
        }
        print("  Forbidden edges (commits touching both sides in the window):")
        if not forbidden:
            print("    none.")
        for spec in forbidden:
            try:
                frm, to = parse_edge_spec(spec)
            except ValueError:
                continue
            print("    %s -> %s: %d" % (frm, to, pair_lookup.get(tuple(sorted((frm, to))), 0)))

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

    print_size_section(sizes)
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
    parser.add_argument(
        "--place",
        action="store_true",
        help="print placement evidence for the last rule's module and write core-placement.md under --out",
    )
    args = parser.parse_args(argv)

    # Report-only contract: a missing or malformed input is a warning, never a
    # nonzero exit, and --check always ends with the ARCH-CHECK line.
    forbidden = []
    allowed = []
    prose = {}
    history_payload = None
    sizes_payload = None
    model = None
    size_settings = {"runtimeCoupled": list(DEFAULT_RUNTIME_COUPLED)}
    try:
        rules, tooling, forbidden, allowed = load_rules(args.modules)
        size_settings = load_size_settings(args.modules)
        model = build_model(args.source, rules, tooling)
    except Exception as exc:
        print("WARN archview: %s" % exc, file=sys.stderr)
    try:
        prose = load_prose(DEFAULT_ATLAS)
    except Exception as exc:
        print("WARN archview: atlas prose not loaded: %s" % exc, file=sys.stderr)

    if model is not None:
        try:
            for rel_path in model["unclassified"]:
                if "/" in rel_path:
                    print("WARN unclassified file: %s (add a rule to modules.toml)" % rel_path)
                else:
                    print(
                        "WARN unplaced root file: %s (add a rule to modules.toml;"
                        " Core is the kernel, not a catch-all)" % rel_path
                    )

            out_dir = Path(args.out)
            out_dir.mkdir(parents=True, exist_ok=True)

            since = default_since()
            parsed = parse_history(git_log_lines(REPO_ROOT, since))
            history_payload = {
                "since": since,
                "commits": len(parsed["commits"]),
                "skippedSweeps": parsed["skipped"],
                "largestSweep": parsed["largest"],
            }
            history_payload.update(history_metrics(parsed["commits"], model, rules))
            growth = parse_growth(git_numstat_lines(REPO_ROOT, since))
            sizes_payload = size_report(model, history_payload, growth, size_settings)

            json_path = out_dir / "edges.json"
            write_json(model, json_path)
            print("Wrote %s" % json_path)

            dot_path = out_dir / "modules.dot"
            _write_text(dot_path, render_dot(model, args.min_edge))
            print("Wrote %s" % dot_path)

            svg_path = out_dir / "modules.svg"
            svg_rendered = render_svg(dot_path, svg_path)
            if svg_rendered:
                print("Wrote %s" % svg_path)

            matrix_path = out_dir / "matrix.html"
            _write_text(matrix_path, render_matrix_html(model))
            print("Wrote %s" % matrix_path)

            explore_path = out_dir / "explore.html"
            _write_text(explore_path, render_explore_html(model, args.min_edge))
            print("Wrote %s" % explore_path)

            types_path = out_dir / "types.json"
            write_types_json(model, types_path)
            print("Wrote %s" % types_path)

            ladder_path = out_dir / "ladder.html"
            _write_text(ladder_path, render_ladder_html(model))
            print("Wrote %s" % ladder_path)

            history_path = out_dir / "history.json"
            write_history_json(history_payload, history_path)
            print("Wrote %s" % history_path)

            sizes_path = out_dir / "sizes.json"
            write_sizes_json(sizes_payload, sizes_path)
            print("Wrote %s" % sizes_path)

            atlas_path = out_dir / "atlas.html"
            svg_text = None
            if svg_rendered and svg_path.exists():
                svg_text = svg_path.read_text(encoding="utf-8")
            _write_text(
                atlas_path,
                render_atlas_html(
                    model, prose, svg_text, forbidden, history_payload, sizes_payload
                ),
            )
            print("Wrote %s" % atlas_path)

            if args.place:
                print()
                print_placement_evidence(model)
                before_rules = historical_catch_all_rules(
                    [rule for rule in rules if not rule.get("placement")]
                )
                after_r1_rules = historical_catch_all_rules(
                    [
                        rule
                        for rule in rules
                        if not rule.get("placement") or rule.get("placement") == "R1"
                    ]
                )
                # Placement evidence needs the graph only, so these two skip
                # the member scan.
                before_model = build_model(args.source, before_rules, tooling, measure_sizes=False)
                after_r1_model = build_model(
                    args.source, after_r1_rules, tooling, measure_sizes=False
                )
                report_path = out_dir / "core-placement.md"
                _write_text(
                    report_path,
                    render_placement_report(
                        before_model, after_r1_model, model, default_placement_rules()
                    ),
                )
                print("Wrote %s" % report_path)
        except Exception as exc:
            print("WARN archview: %s" % exc, file=sys.stderr)

    if args.check:
        if model is not None:
            try:
                run_check(model, forbidden, allowed, prose, history_payload, sizes_payload)
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
